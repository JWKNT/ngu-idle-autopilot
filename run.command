#!/bin/zsh
# FILE PURPOSE
#
# This deployment entrypoint injects the already-built DLL into one running NGUIdle process,
# records the returned assembly pointer for safe ejection, and launches both read-only views: the
# native action monitor and the loopback dashboard bridge. It does not start the game or build
# source. A successful injector pointer is required; gameplay mutation remains paused until the
# DLL independently verifies active synchronization. The dashboard only reads generated telemetry
# and binds to 127.0.0.1; it is not a second automation or command channel.
set -euo pipefail

bot_dir=${0:A:h}
crossover_app="/Users/jw/Applications/CrossOver 26.3.app"
wine_bin="$crossover_app/Contents/SharedSupport/CrossOver/bin/wine"

if [[ ! -x "$wine_bin" ]]; then
  print -u2 "CrossOver was not found at: $crossover_app"
  exit 1
fi
if ! pgrep -f 'NGUIdle.exe' >/dev/null; then
  print -u2 "Start NGU Idle in the CrossOver Steam bottle first."
  exit 1
fi

mkdir -p "$bot_dir/runtime/logs" "$bot_dir/runtime/profiles"
if [[ ! -f "$bot_dir/runtime/autopilot.json" ]]; then
  cp "$bot_dir/autopilot.example.json" "$bot_dir/runtime/autopilot.json"
fi

cd "$bot_dir"
# decision.json is generated telemetry, not save data. Remove the previous producer's
# retained frame before injection so a newly opened monitor cannot briefly present it
# as current while the DLL is still crossing the gameplay synchronization barrier.
rm -f "$bot_dir/runtime/decision.json"
result=$(env CX_BOTTLE=Steam "$wine_bin" injector/smi.exe inject -p NGUIdle -a NGUIdleAutopilot.dll -n NGUAutopilot -c Loader -m Init)
print -r -- "$result"
pointer=${result##*: }
pointer=${pointer//$'\r'/}
if [[ "$pointer" == 0x* ]]; then
  print -r -- "$pointer" > "$bot_dir/runtime/assembly-pointer.txt"
fi

if [[ "$result" != *": 0x"* ]]; then
  print -u2 "Injection did not complete. See the error above."
  exit 1
fi
mode=$(sed -n 's/.*"Mode"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$bot_dir/runtime/autopilot.json" | head -1)
[[ -z "$mode" ]] && mode="unknown"
print "Autopilot injected in $mode mode. Execution remains paused until active gameplay is verified."

monitor_app="$bot_dir/NGU Action Monitor.app"
if [[ -d "$monitor_app" ]]; then
  pkill -f "$monitor_app/Contents/MacOS/NGUActionMonitor" 2>/dev/null || true
  open -n "$monitor_app" --args "$bot_dir/runtime/logs/actions.log" "$bot_dir/runtime/decision.json"
  print "Live action window opened."
fi

# The public dashboard is a static shell. Its game-state authority is this exact local bridge,
# detached into its own process session and tracked by a PID whose full script path is validated,
# so it survives the invoking terminal while lifecycle cleanup cannot target an unrelated Python
# process. A bridge failure does not change the already-confirmed injector state.
dashboard_script="$bot_dir/monitor/dashboard_server.py"
dashboard_pid_file="$bot_dir/runtime/dashboard-server.pid"
dashboard_log="$bot_dir/runtime/logs/dashboard-server.log"
if [[ -f "$dashboard_pid_file" ]]; then
  old_dashboard_pid=$(<"$dashboard_pid_file")
  old_dashboard_command=$(ps -p "$old_dashboard_pid" -o command= 2>/dev/null || true)
  if [[ "$old_dashboard_pid" == <-> && "$old_dashboard_command" == *"$dashboard_script"* ]]; then
    kill "$old_dashboard_pid" 2>/dev/null || true
  fi
  rm -f "$dashboard_pid_file"
fi

python3 "$dashboard_script" --root "$bot_dir/docs" --runtime "$bot_dir/runtime" --port 47635 \
  --daemon --pid-file "$dashboard_pid_file" --log "$dashboard_log"

dashboard_ready=false
for _ in {1..20}; do
  if curl -fsS "http://127.0.0.1:47635/api/health" >/dev/null 2>&1; then
    dashboard_ready=true
    break
  fi
  sleep 0.1
done
if [[ "$dashboard_ready" == true ]]; then
  print "Local dashboard ready at http://127.0.0.1:47635/."
else
  print -u2 "Dashboard bridge did not become ready; gameplay automation remains active."
  if [[ -f "$dashboard_pid_file" ]]; then
    dashboard_pid=$(<"$dashboard_pid_file")
    dashboard_command=$(ps -p "$dashboard_pid" -o command= 2>/dev/null || true)
    if [[ "$dashboard_command" == *"$dashboard_script"* ]]; then
      kill "$dashboard_pid" 2>/dev/null || true
    fi
    rm -f "$dashboard_pid_file"
  fi
fi
