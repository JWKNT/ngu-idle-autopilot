#!/bin/zsh
# FILE PURPOSE
#
# This teardown entrypoint ejects only an assembly whose deployment claim still matches the exact
# NGU Idle host PID/start/command, Wine PID/start, producer session, MVID, DLL hash, game hash, and
# pointer accepted by run.command. It refuses ambiguous process sets and PID reuse. A claim whose
# process has exited or been replaced is archived because its address-space pointer is necessarily
# stale; telemetry mismatches on a still-live process are preserved and refused for diagnosis.
# After successful ejection it stops only the monitor/dashboard PIDs recorded in that same claim.
#
# Inputs are runtime/deployment-claim.json, deployment/decision telemetry, the compatibility pointer
# file, and the live process tables. Outputs are an injector result, removal of a successfully spent
# claim, exact companion shutdown, or archived stale evidence. This script does not kill/restart the
# game, guess pointers, mutate saves, or decide strategy. Its fixture-only mode is restricted to an
# explicit deployment-lifecycle.* temporary root and never invokes Wine or live companions.
set -euo pipefail

bot_dir=${0:A:h}
wine_bin="${NGU_WINE_BIN:-/Users/jw/Applications/CrossOver 26.3.app/Contents/SharedSupport/CrossOver/bin/wine}"
runtime_dir="${NGU_RUNTIME_DIR:-$bot_dir/runtime}"
claim_file="$runtime_dir/deployment-claim.json"
pointer_file="$runtime_dir/assembly-pointer.txt"
deployment_file="$runtime_dir/deployment.json"
decision_file="$runtime_dir/decision.json"
archive_dir="$runtime_dir/deployment-claims/archive"
fixture_mode=false
fixture_root=""

if [[ "${NGU_LIFECYCLE_TEST_MODE:-}" == "fixture-v1" ]]; then
  fixture_mode=true
  fixture_root="${NGU_LIFECYCLE_FIXTURE_ROOT:-}"
  if [[ -z "$fixture_root" || ! -d "$fixture_root" || "$fixture_root" != *"deployment-lifecycle."* ]]; then
    print -u2 "Fixture mode requires a deployment-lifecycle.* temporary root."
    exit 2
  fi
  runtime_dir="$fixture_root/runtime"
  claim_file="$runtime_dir/deployment-claim.json"
  pointer_file="$runtime_dir/assembly-pointer.txt"
  deployment_file="$runtime_dir/deployment.json"
  decision_file="$runtime_dir/decision.json"
  archive_dir="$runtime_dir/deployment-claims/archive"
fi

json_field() {
  python3 - "$1" "$2" <<'PY'
import json, sys
try:
    value = json.load(open(sys.argv[1], encoding="utf-8"))
    for key in sys.argv[2].split("."):
        value = value[key]
    if isinstance(value, bool): print("true" if value else "false")
    elif value is not None: print(value)
except Exception: pass
PY
}

process_command_sha256() {
  print -rn -- "$1" | shasum -a 256 | awk '{print $1}'
}

discover_game_pids() {
  if [[ "$fixture_mode" == true ]]; then
    python3 - "$fixture_root/processes.json" <<'PY'
import json, sys
for game in json.load(open(sys.argv[1], encoding="utf-8")).get("games", []): print(int(game["osPid"]))
PY
    return
  fi
  pgrep -f 'NGUIdle\.exe([[:space:]]|$)' 2>/dev/null || true
}

game_record_field() {
  local pid=$1 field=$2
  if [[ "$fixture_mode" == true ]]; then
    python3 - "$fixture_root/processes.json" "$pid" "$field" <<'PY'
import json, sys
for game in json.load(open(sys.argv[1], encoding="utf-8")).get("games", []):
    if int(game["osPid"]) == int(sys.argv[2]):
        print(game.get(sys.argv[3], "")); break
PY
    return
  fi
  if [[ "$field" == "osCommand" ]]; then
    ps -p "$pid" -o command= 2>/dev/null | sed -e 's/[[:space:]]*$//'
  else
    local raw_start
    raw_start=$(ps -p "$pid" -o lstart= 2>/dev/null | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')
    [[ -n "$raw_start" ]] || return 1
    python3 - "$raw_start" <<'PY'
from datetime import datetime, timezone
import sys
print(datetime.strptime(sys.argv[1], "%a %b %d %H:%M:%S %Y").astimezone(timezone.utc).isoformat().replace("+00:00", "Z"))
PY
  fi
}

discover_windows_pids() {
  if [[ "$fixture_mode" == true ]]; then
    python3 - "$fixture_root/processes.json" <<'PY'
import json, sys
for game in json.load(open(sys.argv[1], encoding="utf-8")).get("games", []): print(int(game["windowsPid"]))
PY
    return
  fi
  local process_listing
  process_listing=$(env CX_BOTTLE=Steam "$wine_bin" winedbg --command 'info proc' 2>&1)
  print -r -- "$process_listing" | awk "/'NGUIdle.exe'/ { gsub(/^=/, \"\", \$1); print \$1 }" | \
    python3 -c 'import sys; [print(int(line.strip(), 16)) for line in sys.stdin if line.strip()]'
}

archive_runtime_file() {
  local target_file=$1 reason=$2
  [[ -f "$target_file" ]] || return 0
  mkdir -p "$archive_dir"
  local destination="$archive_dir/${target_file:t}.$(date -u +%Y%m%dT%H%M%SZ).$RANDOM"
  mv "$target_file" "$destination"
  print -r -- "$reason" > "$destination.reason.txt"
  print "Archived stale ${target_file:t}: $reason"
}

archive_claim_bundle() {
  local reason=$1
  archive_runtime_file "$claim_file" "$reason"
  archive_runtime_file "$pointer_file" "$reason"
}

if [[ "$fixture_mode" != true && ! -x "$wine_bin" ]]; then
  print -u2 "CrossOver injector transport is unavailable."
  exit 1
fi
if [[ ! -f "$claim_file" ]]; then
  if [[ -f "$pointer_file" ]]; then
    game_pids=(${(f)"$(discover_game_pids)"})
    if (( ${#game_pids} == 0 )); then
      archive_runtime_file "$pointer_file" "unbound legacy pointer remained after its game process exited"
    else
      print -u2 "Refusing ejection: a bare legacy pointer has no PID/start/session/hash ownership proof."
      exit 1
    fi
  fi
  print -u2 "No active deployment claim was recorded."
  exit 1
fi

claim_values=$(python3 - "$claim_file" <<'PY'
import json, sys
try:
    data = json.load(open(sys.argv[1], encoding="utf-8"))
    keys = ("schemaVersion", "claimState", "assemblyPointer", "gameOsPid",
            "gameOsProcessStartUtc", "gameOsCommandSha256", "producerPid",
            "producerProcessStartUtc", "producerSessionId", "activeBuildId",
            "diskArtifactSha256", "gameAssemblySha256", "telemetryHandshake",
            "monitorPid", "dashboardPid")
    values = [str(data[key]) for key in keys]
    if int(values[0]) != 1 or values[1] != "active": raise ValueError("not active")
    print("|".join(values))
except Exception: pass
PY
)
if [[ -z "$claim_values" ]]; then
  archive_claim_bundle "malformed or non-active deployment claim"
  print -u2 "Refusing ejection after archiving a malformed claim."
  exit 1
fi

IFS='|' read -r claim_schema claim_state pointer claimed_os_pid claimed_os_start \
  claimed_command_hash claimed_windows_pid claimed_producer_start claimed_session claimed_mvid \
  claimed_dll_hash claimed_game_hash claimed_handshake monitor_pid dashboard_pid <<< "$claim_values"
pointer_digits=${pointer#0x}
if [[ "$pointer" != 0x* || -z "$pointer_digits" || "$pointer_digits" == *[^[:xdigit:]]* ]]; then
  archive_claim_bundle "claim contained an invalid assembly pointer"
  print -u2 "Refusing ejection: invalid claimed pointer."
  exit 1
fi
if [[ -f "$pointer_file" && "$(tr -d '\r\n' < "$pointer_file")" != "$pointer" ]]; then
  print -u2 "Refusing ejection: compatibility pointer and bound claim disagree."
  exit 1
fi

game_pids=(${(f)"$(discover_game_pids)"})
if (( ${#game_pids} == 0 )); then
  archive_claim_bundle "claimed game process exited; its address-space pointer is stale"
  print -u2 "No game process remains; archived the stale deployment claim without ejection."
  exit 1
fi
if (( ${#game_pids} != 1 )); then
  print -u2 "Refusing ejection: expected exactly one NGUIdle.exe host process, found ${#game_pids}."
  exit 1
fi
current_os_pid=${game_pids[1]}
current_os_start=$(game_record_field "$current_os_pid" osStartUtc)
current_os_command=$(game_record_field "$current_os_pid" osCommand)
current_command_hash=$(process_command_sha256 "$current_os_command")
if [[ "$current_os_pid" == "$claimed_os_pid" && "$current_os_start" != "$claimed_os_start" ]]; then
  archive_claim_bundle "host PID was reused with a different process start time"
  print -u2 "Refusing ejection: PID reuse invalidated the pointer claim."
  exit 1
fi
if [[ "$current_os_pid" != "$claimed_os_pid" || "$current_os_start" != "$claimed_os_start" ]]; then
  archive_claim_bundle "unique game process is a different PID/start from the claimed address space"
  print -u2 "Refusing ejection: archived a claim belonging to a prior game process."
  exit 1
fi
if [[ "$current_command_hash" != "$claimed_command_hash" || "$current_os_command" != *"NGUIdle.exe"* ]]; then
  print -u2 "Refusing ejection: the claimed host PID's executable command identity changed."
  exit 1
fi

windows_pids=(${(f)"$(discover_windows_pids)"})
if (( ${#windows_pids} != 1 )); then
  print -u2 "Refusing ejection: expected exactly one Wine NGUIdle process, found ${#windows_pids}."
  exit 1
fi
if [[ "${windows_pids[1]}" != "$claimed_windows_pid" ]]; then
  print -u2 "Refusing ejection: Wine PID does not match the bound producer PID."
  exit 1
fi

# Deployment identity is immutable evidence from module startup. Decision telemetry may currently
# be synchronized or paused at a menu, but it must still name exactly the claimed producer/session.
telemetry_valid=$(python3 - "$deployment_file" "$decision_file" "$claimed_os_start" \
  "$claimed_windows_pid" "$claimed_producer_start" "$claimed_session" "$claimed_mvid" \
  "$claimed_dll_hash" "$claimed_game_hash" "$claimed_handshake" <<'PY'
from datetime import datetime
import json, sys
try:
    deployment = json.load(open(sys.argv[1], encoding="utf-8"))
    decision = json.load(open(sys.argv[2], encoding="utf-8"))
    host_start, pid, producer_start, session, mvid, dll_hash, game_hash, handshake = sys.argv[3:]
    pid = int(pid)
    def instant(text): return datetime.fromisoformat(text.replace("Z", "+00:00"))
    ok = (
        int(deployment.get("schemaVersion", 0)) >= 2
        and int(deployment.get("producerPid", -1)) == pid
        and deployment.get("producerProcessStartUtc") == producer_start
        and abs((instant(producer_start)-instant(host_start)).total_seconds()) < 2
        and deployment.get("producerSessionId") == session
        and deployment.get("telemetryHandshake") == handshake
        and str(deployment.get("activeBuildId", "")).lower() == mvid.lower()
        and deployment.get("diskArtifactSha256") == dll_hash
        and deployment.get("gameAssemblySha256") == game_hash
        and int(decision.get("producerPid", -1)) == pid
        and decision.get("producerSessionId") == session
        and str(decision.get("buildId", "")).lower() == mvid.lower()
        and decision.get("diskArtifactSha256") == dll_hash
        and decision.get("gameAssemblySha256") == game_hash
    )
    if ok: print("true")
except Exception: pass
PY
)
if [[ "$telemetry_valid" != true ]]; then
  print -u2 "Refusing ejection: live deployment/decision telemetry does not exactly match the claim."
  exit 1
fi

if [[ "$fixture_mode" == true ]]; then
  result=$(<"$fixture_root/eject-result.txt")
else
  cd "$bot_dir"
  result=$(env CX_BOTTLE=Steam "$wine_bin" injector/smi.exe eject -p NGUIdle \
    -a "$pointer" -n NGUAutopilot -c Loader -m Unload)
fi
print -r -- "$result"
if [[ "$result" != *"Ejection successful"* ]]; then
  print -u2 "Ejection did not complete; the validated claim remains active."
  exit 1
fi

rm -f "$claim_file" "$pointer_file"
if [[ "$fixture_mode" == true ]]; then
  print "Autopilot fixture claim validated and unloaded."
  exit 0
fi

monitor_exec="$bot_dir/NGU Action Monitor.app/Contents/MacOS/NGUActionMonitor"
if [[ "$monitor_pid" == <-> && "$monitor_pid" -gt 0 ]]; then
  monitor_command=$(ps -p "$monitor_pid" -o command= 2>/dev/null || true)
  if [[ "$monitor_command" == "$monitor_exec "* && "$monitor_command" == *"$decision_file"* ]]; then
    kill "$monitor_pid" 2>/dev/null || true
    print "Claim-bound Action Monitor stopped."
  fi
fi
dashboard_script="$bot_dir/monitor/dashboard_server.py"
if [[ "$dashboard_pid" == <-> && "$dashboard_pid" -gt 0 ]]; then
  dashboard_command=$(ps -p "$dashboard_pid" -o command= 2>/dev/null || true)
  if [[ "$dashboard_command" == *"$dashboard_script"* && "$dashboard_command" == *"$runtime_dir"* ]]; then
    kill "$dashboard_pid" 2>/dev/null || true
    print "Claim-bound local dashboard stopped."
  fi
fi
rm -f "$runtime_dir/action-monitor.pid" "$runtime_dir/dashboard-server.pid"
print "Autopilot unloaded after exact deployment-claim validation."
