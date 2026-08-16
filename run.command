#!/bin/zsh
# FILE PURPOSE
#
# This deployment entrypoint injects the already-built DLL into one running NGUIdle process,
# records the returned assembly pointer for safe ejection, and launches the separate read-only
# monitor. It does not start the game or build source. A successful injector pointer is required;
# gameplay mutation remains paused until the DLL independently verifies active synchronization.
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
