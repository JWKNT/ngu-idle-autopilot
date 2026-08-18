#!/bin/zsh
# FILE PURPOSE
#
# This is the fail-closed deployment entrypoint for the injected NGU Idle autopilot. It proves
# that exactly one host NGUIdle process and exactly one Wine/Windows NGUIdle process exist, records
# both process identities, discovers the just-built DLL MVID dynamically, injects once, and waits
# for a new synchronized telemetry session before publishing pointer ownership. The resulting JSON
# claim binds the pointer to the host PID/start/command, Windows PID/start, producer session, MVID,
# DLL hash, and game-assembly hash. Only after that proof does it start the read-only monitor and
# dashboard. It never starts or restarts the game and never treats injector text alone as success.
#
# Inputs are the built DLL, the installed game assembly, injector transport, one live game process,
# runtime config, and deployment/decision telemetry. Outputs are runtime/deployment-claim.json, a
# compatibility pointer file, archived stale/pending claims, companion PIDs, and console status.
# A fixture-only mode exists for tests under an explicit temporary root; it cannot invoke Wine or
# launch companions. Strategy and game lifecycle policy do not belong in this script.
set -euo pipefail

bot_dir=${0:A:h}
crossover_app="/Users/jw/Applications/CrossOver 26.3.app"
wine_bin="${NGU_WINE_BIN:-$crossover_app/Contents/SharedSupport/CrossOver/bin/wine}"
runtime_dir="${NGU_RUNTIME_DIR:-$bot_dir/runtime}"
dll_path="${NGU_DLL_PATH:-$bot_dir/NGUIdleAutopilot.dll}"
game_assembly_path="${NGU_GAME_ASSEMBLY_PATH:-/Users/jw/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/NGU IDLE/NGUIdle_Data/Managed/Assembly-CSharp.dll}"
claim_file="$runtime_dir/deployment-claim.json"
pointer_file="$runtime_dir/assembly-pointer.txt"
deployment_file="$runtime_dir/deployment.json"
decision_file="$runtime_dir/decision.json"
archive_dir="$runtime_dir/deployment-claims/archive"
handshake_timeout="${NGU_HANDSHAKE_TIMEOUT_SECONDS:-45}"
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
  dll_path="$fixture_root/NGUIdleAutopilot.dll"
  game_assembly_path="$fixture_root/Assembly-CSharp.dll"
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
    with open(sys.argv[1], encoding="utf-8") as handle:
        value = json.load(handle)
    for key in sys.argv[2].split("."):
        value = value[key]
    if isinstance(value, bool):
        print("true" if value else "false")
    elif value is not None:
        print(value)
except Exception:
    pass
PY
}

sha256_file() {
  shasum -a 256 "$1" | awk '{print $1}'
}

process_command_sha256() {
  print -rn -- "$1" | shasum -a 256 | awk '{print $1}'
}

discover_game_pids() {
  if [[ "$fixture_mode" == true ]]; then
    python3 - "$fixture_root/processes.json" <<'PY'
import json, sys
for game in json.load(open(sys.argv[1], encoding="utf-8")).get("games", []):
    print(int(game["osPid"]))
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
        print(game.get(sys.argv[3], ""))
        break
PY
    return
  fi
  if [[ "$field" == "osCommand" ]]; then
    ps -p "$pid" -o command= 2>/dev/null | sed -e 's/[[:space:]]*$//'
  elif [[ "$field" == "osStartUtc" ]]; then
    local raw_start
    raw_start=$(ps -p "$pid" -o lstart= 2>/dev/null | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')
    [[ -n "$raw_start" ]] || return 1
    python3 - "$raw_start" <<'PY'
from datetime import datetime, timezone
import sys
value = datetime.strptime(sys.argv[1], "%a %b %d %H:%M:%S %Y").astimezone(timezone.utc)
print(value.isoformat().replace("+00:00", "Z"))
PY
  fi
}

discover_windows_pids() {
  if [[ "$fixture_mode" == true ]]; then
    python3 - "$fixture_root/processes.json" <<'PY'
import json, sys
for game in json.load(open(sys.argv[1], encoding="utf-8")).get("games", []):
    print(int(game["windowsPid"]))
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
  local stamp destination
  stamp=$(date -u +%Y%m%dT%H%M%SZ)
  destination="$archive_dir/${target_file:t}.$stamp.$RANDOM"
  mv "$target_file" "$destination"
  print -r -- "$reason" > "$destination.reason.txt"
  print "Archived stale ${target_file:t}: $reason"
}

process_identity_still_exact() {
  local current_pids current_windows current_start current_command current_command_hash
  current_pids=(${(f)"$(discover_game_pids)"})
  (( ${#current_pids} == 1 )) || return 1
  [[ "${current_pids[1]}" == "$game_os_pid" ]] || return 1
  current_start=$(game_record_field "$game_os_pid" osStartUtc)
  current_command=$(game_record_field "$game_os_pid" osCommand)
  current_command_hash=$(process_command_sha256 "$current_command")
  [[ "$current_start" == "$game_os_start" && "$current_command_hash" == "$game_os_command_hash" ]] || return 1
  current_windows=(${(f)"$(discover_windows_pids)"})
  (( ${#current_windows} == 1 )) || return 1
  [[ "${current_windows[1]}" == "$game_windows_pid" ]]
}

if [[ ! "$handshake_timeout" == <-> || "$handshake_timeout" -lt 1 || "$handshake_timeout" -gt 300 ]]; then
  print -u2 "NGU_HANDSHAKE_TIMEOUT_SECONDS must be an integer from 1 through 300."
  exit 2
fi
if [[ "$fixture_mode" != true && ! -x "$wine_bin" ]]; then
  print -u2 "CrossOver was not found at: $crossover_app"
  exit 1
fi
if [[ ! -f "$dll_path" || ! -f "$game_assembly_path" ]]; then
  print -u2 "The built autopilot DLL and installed game assembly must both exist before injection."
  exit 1
fi

mkdir -p "$runtime_dir/logs" "$runtime_dir/profiles" "$archive_dir"
if [[ ! -f "$runtime_dir/autopilot.json" && "$fixture_mode" != true ]]; then
  cp "$bot_dir/autopilot.example.json" "$runtime_dir/autopilot.json"
fi

game_pids=(${(f)"$(discover_game_pids)"})
if (( ${#game_pids} == 0 )); then
  print -u2 "Refusing injection: no NGUIdle.exe host process exists."
  exit 1
fi
if (( ${#game_pids} != 1 )); then
  print -u2 "Refusing injection: expected exactly one NGUIdle.exe host process, found ${#game_pids}."
  exit 1
fi
game_os_pid=${game_pids[1]}
game_os_start=$(game_record_field "$game_os_pid" osStartUtc)
game_os_command=$(game_record_field "$game_os_pid" osCommand)
if [[ -z "$game_os_start" || "$game_os_command" != *"NGUIdle.exe"* ]]; then
  print -u2 "Refusing injection: the unique host PID did not retain the expected executable identity."
  exit 1
fi
game_os_command_hash=$(process_command_sha256 "$game_os_command")

windows_pids=(${(f)"$(discover_windows_pids)"})
if (( ${#windows_pids} != 1 )); then
  print -u2 "Refusing injection: expected exactly one Wine NGUIdle process, found ${#windows_pids}."
  exit 1
fi
game_windows_pid=${windows_pids[1]}

# A bound claim from this exact process is active ownership. A claim from a dead/restarted process
# can be archived because its address-space pointer is unusable. A reused host PID is archived but
# deliberately requires another invocation, preventing a restart from being mistaken for continuity.
if [[ -f "$claim_file" ]]; then
  claimed_os_pid=$(json_field "$claim_file" gameOsPid)
  claimed_os_start=$(json_field "$claim_file" gameOsProcessStartUtc)
  claimed_windows_pid=$(json_field "$claim_file" producerPid)
  if [[ -z "$claimed_os_pid" || -z "$claimed_os_start" || -z "$claimed_windows_pid" ]]; then
    archive_runtime_file "$claim_file" "malformed deployment claim"
    archive_runtime_file "$pointer_file" "pointer accompanied a malformed deployment claim"
    print -u2 "Refusing injection after archiving a malformed claim; inspect the archive and retry."
    exit 1
  fi
  if [[ "$claimed_os_pid" == "$game_os_pid" && "$claimed_os_start" == "$game_os_start" \
      && "$claimed_windows_pid" == "$game_windows_pid" ]]; then
    print -u2 "A deployment claim already owns this exact game process. Run ./stop.command first."
    exit 1
  fi
  archive_runtime_file "$claim_file" "claim belongs to a different game PID/start identity"
  archive_runtime_file "$pointer_file" "pointer belongs to a different game PID/start identity"
  if [[ "$claimed_os_pid" == "$game_os_pid" && "$claimed_os_start" != "$game_os_start" \
      || "$claimed_windows_pid" == "$game_windows_pid" ]]; then
    print -u2 "Refusing injection on this invocation: a claimed PID was reused by a different process start."
    exit 1
  fi
fi

# A legacy bare pointer has no ownership proof. Retain it if telemetry says this same process may
# still host it; archive it only when the producer PID/start no longer agrees with the unique game.
if [[ -f "$pointer_file" ]]; then
  legacy_pid=$(json_field "$deployment_file" producerPid)
  legacy_start=$(json_field "$deployment_file" producerProcessStartUtc)
  legacy_same_start=false
  if python3 - "$legacy_start" "$game_os_start" <<'PY'
from datetime import datetime
import sys
def value(text): return datetime.fromisoformat(text.replace("Z", "+00:00"))
try: raise SystemExit(0 if abs((value(sys.argv[1])-value(sys.argv[2])).total_seconds()) < 2 else 1)
except Exception: raise SystemExit(1)
PY
  then
    legacy_same_start=true
  fi
  if [[ "$legacy_pid" == "$game_windows_pid" && "$legacy_same_start" == true ]]; then
    print -u2 "An unbound legacy pointer may still belong to this game process. Restart NGU Idle before injecting."
    exit 1
  fi
  archive_runtime_file "$pointer_file" "legacy pointer does not match the unique current game process"
  if [[ "$legacy_pid" == "$game_windows_pid" && "$legacy_same_start" != true ]]; then
    print -u2 "Refusing injection on this invocation: the legacy Wine PID was reused after restart."
    exit 1
  fi
fi

expected_dll_hash=$(sha256_file "$dll_path")
expected_game_hash=$(sha256_file "$game_assembly_path")
if [[ "$fixture_mode" == true ]]; then
  expected_mvid="${NGU_EXPECTED_MVID_OVERRIDE:-}"
else
  expected_mvid=$(python3 - "$dll_path" <<'PY'
import sys
try:
    import dnfile
    pe = dnfile.dnPE(sys.argv[1])
    print(str(pe.net.mdtables.Module.rows[0].Mvid).lower())
except Exception as exc:
    print("Unable to discover DLL MVID dynamically: " + str(exc), file=sys.stderr)
    raise SystemExit(1)
PY
)
fi
if [[ -z "$expected_mvid" ]]; then
  print -u2 "Unable to discover the expected DLL MVID."
  exit 1
fi

previous_session=$(json_field "$deployment_file" producerSessionId)
injection_marker=$(mktemp "$runtime_dir/.injection-marker.XXXXXX")
pending_file=$(mktemp "$runtime_dir/.pending-injection.XXXXXX")
rm -f "$decision_file"

if [[ "$fixture_mode" == true ]]; then
  result=$(<"$fixture_root/inject-result.txt")
else
  cd "$bot_dir"
  injector_dll_arg="$dll_path"
  [[ "$dll_path" == "$bot_dir/NGUIdleAutopilot.dll" ]] && injector_dll_arg="NGUIdleAutopilot.dll"
  result=$(env CX_BOTTLE=Steam "$wine_bin" injector/smi.exe inject -p NGUIdle \
    -a "$injector_dll_arg" -n NGUAutopilot -c Loader -m Init)
fi
print -r -- "$result"
pointer=${result##*: }
pointer=${pointer//$'\r'/}
pointer_digits=${pointer#0x}
if [[ "$result" != *": 0x"* || "$pointer" != 0x* || -z "$pointer_digits" \
    || "$pointer_digits" == *[^[:xdigit:]]* ]]; then
  rm -f "$injection_marker" "$pending_file"
  print -u2 "Injection transport did not return a valid assembly pointer."
  exit 1
fi

python3 - "$pending_file" "$pointer" "$game_os_pid" "$game_os_start" "$game_os_command_hash" \
  "$game_windows_pid" "$expected_mvid" "$expected_dll_hash" "$expected_game_hash" <<'PY'
import datetime, json, sys
keys = ["assemblyPointer", "gameOsPid", "gameOsProcessStartUtc", "gameOsCommandSha256",
        "producerPid", "activeBuildId", "diskArtifactSha256", "gameAssemblySha256"]
values = sys.argv[2:]
data = dict(zip(keys, values))
data["schemaVersion"] = 1
data["claimState"] = "pending-handshake"
data["gameOsPid"] = int(data["gameOsPid"])
data["producerPid"] = int(data["producerPid"])
data["injectedAtUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00", "Z")
with open(sys.argv[1], "w", encoding="utf-8") as handle:
    json.dump(data, handle, indent=2, sort_keys=True)
    handle.write("\n")
PY

handshake=""
for (( attempt=1; attempt<=handshake_timeout*5; attempt++ )); do
  if ! process_identity_still_exact; then
    break
  fi
  if [[ -f "$deployment_file" && -f "$decision_file" \
      && "$deployment_file" -nt "$injection_marker" && "$decision_file" -nt "$injection_marker" ]]; then
    handshake=$(python3 - "$deployment_file" "$decision_file" "$game_os_start" "$game_windows_pid" \
      "$previous_session" "$expected_mvid" "$expected_dll_hash" "$expected_game_hash" <<'PY'
from datetime import datetime
import json, sys
try:
    deployment = json.load(open(sys.argv[1], encoding="utf-8"))
    decision = json.load(open(sys.argv[2], encoding="utf-8"))
    host_start, pid, previous, mvid, dll_hash, game_hash = sys.argv[3:]
    pid = int(pid)
    session = str(deployment.get("producerSessionId", ""))
    producer_start = str(deployment.get("producerProcessStartUtc", ""))
    def instant(text): return datetime.fromisoformat(text.replace("Z", "+00:00"))
    mutation_root = decision.get("mutationRoot")
    decision_epoch = decision.get("gameEpochFingerprint")
    clean_closed_root = (
        isinstance(mutation_root, dict)
        and type(mutation_root.get("id")) is int
        and mutation_root["id"] > 0
        and mutation_root.get("state") == "closed"
        and isinstance(decision_epoch, str)
        and bool(decision_epoch)
        and mutation_root.get("epochFingerprint") == decision_epoch
        and all(type(mutation_root.get(key)) is int and mutation_root[key] == 0 for key in (
            "pendingSteps", "rejectedSteps", "quarantinedSteps"))
    )
    valid = (
        int(deployment.get("schemaVersion", 0)) >= 2
        and int(deployment.get("producerPid", -1)) == pid
        and abs((instant(producer_start)-instant(host_start)).total_seconds()) < 2
        and session and session != previous
        and deployment.get("telemetryHandshake") == f"{pid}:{session}:{mvid}"
        and str(deployment.get("activeBuildId", "")).lower() == mvid.lower()
        and deployment.get("diskArtifactSha256") == dll_hash
        and deployment.get("gameAssemblySha256") == game_hash
        and int(decision.get("producerPid", -1)) == pid
        and decision.get("producerSessionId") == session
        and str(decision.get("buildId", "")).lower() == mvid.lower()
        and decision.get("diskArtifactSha256") == dll_hash
        and decision.get("gameAssemblySha256") == game_hash
        and decision.get("synced") is True
        and decision.get("syncState") == "active-gameplay"
        and decision.get("decisionPhase") == "post-automation-transaction"
        and decision.get("automationTransactionComplete") is True
        and decision.get("automationTransactionError") == ""
        and clean_closed_root
        and int(decision.get("decisionSequence", 0)) > 0
    )
    if valid:
        print("|".join((session, producer_start, str(decision["decisionSequence"]),
                        str(deployment.get("observedAt", "")))))
except Exception:
    pass
PY
)
    [[ -n "$handshake" ]] && break
  fi
  sleep 0.2
done

if [[ -z "$handshake" ]]; then
  cleanup_result="not attempted because the target process identity changed"
  if process_identity_still_exact; then
    if [[ "$fixture_mode" == true ]]; then
      cleanup_result=$(<"$fixture_root/eject-result.txt")
    else
      cleanup_result=$(env CX_BOTTLE=Steam "$wine_bin" injector/smi.exe eject -p NGUIdle \
        -a "$pointer" -n NGUAutopilot -c Loader -m Unload 2>&1 || true)
    fi
  fi
  archive_runtime_file "$pending_file" "synchronized telemetry handshake failed; cleanup: $cleanup_result"
  rm -f "$injection_marker"
  print -u2 "Injection was not claimed: no synchronized PID/start/session/MVID/hash-matching decision arrived."
  exit 1
fi

session=${handshake%%|*}
remainder=${handshake#*|}
producer_start=${remainder%%|*}
remainder=${remainder#*|}
decision_sequence=${remainder%%|*}
deployment_observed=${remainder#*|}

# One final process check closes the race between telemetry validation and ownership publication.
if ! process_identity_still_exact; then
  archive_runtime_file "$pending_file" "target process identity changed immediately before claim publication"
  rm -f "$injection_marker"
  print -u2 "Refusing to publish a pointer claim after target PID/start changed."
  exit 1
fi

claim_temp=$(mktemp "$runtime_dir/.deployment-claim.XXXXXX")
python3 - "$claim_temp" "$pointer" "$game_os_pid" "$game_os_start" "$game_os_command_hash" \
  "$game_windows_pid" "$producer_start" "$session" "$expected_mvid" "$expected_dll_hash" \
  "$expected_game_hash" "$decision_sequence" "$deployment_observed" <<'PY'
import datetime, json, sys
data = {
    "schemaVersion": 1,
    "claimState": "active",
    "claimedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00", "Z"),
    "assemblyPointer": sys.argv[2],
    "gameOsPid": int(sys.argv[3]),
    "gameOsProcessStartUtc": sys.argv[4],
    "gameOsCommandSha256": sys.argv[5],
    "producerPid": int(sys.argv[6]),
    "producerProcessStartUtc": sys.argv[7],
    "producerSessionId": sys.argv[8],
    "activeBuildId": sys.argv[9],
    "diskArtifactSha256": sys.argv[10],
    "gameAssemblySha256": sys.argv[11],
    "acceptedDecisionSequence": int(sys.argv[12]),
    "deploymentObservedAt": sys.argv[13],
    "monitorPid": -1,
    "dashboardPid": -1,
}
data["telemetryHandshake"] = f'{data["producerPid"]}:{data["producerSessionId"]}:{data["activeBuildId"]}'
with open(sys.argv[1], "w", encoding="utf-8") as handle:
    json.dump(data, handle, indent=2, sort_keys=True)
    handle.write("\n")
PY
mv "$claim_temp" "$claim_file"
pointer_temp=$(mktemp "$runtime_dir/.assembly-pointer.XXXXXX")
print -r -- "$pointer" > "$pointer_temp"
mv "$pointer_temp" "$pointer_file"
rm -f "$pending_file" "$injection_marker"

if [[ "$fixture_mode" == true ]]; then
  print "Autopilot claimed after synchronized fixture handshake: PID $game_os_pid / Wine PID $game_windows_pid / session $session / MVID $expected_mvid."
  exit 0
fi

# Companions are launched only after the ownership claim exists. New launches use absolute paths;
# their exact PIDs are then folded into the claim so stop.command never guesses by a broad name.
monitor_app="$bot_dir/NGU Action Monitor.app"
monitor_exec="$monitor_app/Contents/MacOS/NGUActionMonitor"
if [[ ! -x "$monitor_exec" ]]; then
  print -u2 "Autopilot is claimed, but the Action Monitor executable is missing."
  exit 1
fi
for old_monitor_pid in $(pgrep -f "$monitor_exec" 2>/dev/null || true); do
  old_monitor_command=$(ps -p "$old_monitor_pid" -o command= 2>/dev/null || true)
  if [[ "$old_monitor_command" == "$monitor_exec "* ]]; then
    kill "$old_monitor_pid" 2>/dev/null || true
  fi
done
open -n "$monitor_app" --args "$runtime_dir/logs/actions.log" "$decision_file"
monitor_pid=""
for _ in {1..40}; do
  monitor_candidates=(${(f)"$(pgrep -f "$monitor_exec.*$decision_file" 2>/dev/null || true)"})
  if (( ${#monitor_candidates} == 1 )); then
    monitor_pid=${monitor_candidates[1]}
    break
  fi
  sleep 0.1
done
if [[ -z "$monitor_pid" ]]; then
  print -u2 "Autopilot is claimed, but exactly one session-bound Action Monitor did not start."
  exit 1
fi

dashboard_script="$bot_dir/monitor/dashboard_server.py"
dashboard_pid_file="$runtime_dir/dashboard-server.pid"
dashboard_log="$runtime_dir/logs/dashboard-server.log"
if [[ -f "$dashboard_pid_file" ]]; then
  old_dashboard_pid=$(<"$dashboard_pid_file")
  old_dashboard_command=$(ps -p "$old_dashboard_pid" -o command= 2>/dev/null || true)
  if [[ "$old_dashboard_pid" == <-> && "$old_dashboard_command" == *"dashboard_server.py"* ]]; then
    kill "$old_dashboard_pid" 2>/dev/null || true
  fi
  rm -f "$dashboard_pid_file"
fi
for dashboard_listener_pid in $(lsof -tiTCP:47635 -sTCP:LISTEN 2>/dev/null || true); do
  dashboard_listener_command=$(ps -p "$dashboard_listener_pid" -o command= 2>/dev/null || true)
  if [[ "$dashboard_listener_command" == *"dashboard_server.py"* ]]; then
    kill "$dashboard_listener_pid" 2>/dev/null || true
  else
    print -u2 "Autopilot is claimed, but port 47635 belongs to unrelated PID $dashboard_listener_pid."
    exit 1
  fi
done
for _ in {1..20}; do
  [[ -z "$(lsof -tiTCP:47635 -sTCP:LISTEN 2>/dev/null || true)" ]] && break
  sleep 0.1
done
python3 "$dashboard_script" --root "$bot_dir/docs" --runtime "$runtime_dir" --port 47635 \
  --daemon --pid-file "$dashboard_pid_file" --log "$dashboard_log"
dashboard_pid=""
for _ in {1..40}; do
  [[ -f "$dashboard_pid_file" ]] && dashboard_pid=$(<"$dashboard_pid_file") || dashboard_pid=""
  dashboard_listener_pid=$(lsof -tiTCP:47635 -sTCP:LISTEN 2>/dev/null | head -1 || true)
  if [[ "$dashboard_pid" == <-> && "$dashboard_listener_pid" == "$dashboard_pid" ]] \
      && curl -fsS "http://127.0.0.1:47635/api/health" >/dev/null 2>&1; then
    break
  fi
  dashboard_pid=""
  sleep 0.1
done
if [[ -z "$dashboard_pid" ]]; then
  print -u2 "Autopilot is claimed, but the exact dashboard listener did not become healthy."
  exit 1
fi

claim_update=$(mktemp "$runtime_dir/.deployment-claim.XXXXXX")
python3 - "$claim_file" "$claim_update" "$monitor_pid" "$dashboard_pid" <<'PY'
import json, sys
data = json.load(open(sys.argv[1], encoding="utf-8"))
data["monitorPid"] = int(sys.argv[3])
data["dashboardPid"] = int(sys.argv[4])
with open(sys.argv[2], "w", encoding="utf-8") as handle:
    json.dump(data, handle, indent=2, sort_keys=True)
    handle.write("\n")
PY
mv "$claim_update" "$claim_file"

mode=$(sed -n 's/.*"Mode"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$runtime_dir/autopilot.json" | head -1)
[[ -z "$mode" ]] && mode="unknown"
print "Autopilot active in $mode mode after synchronized identity proof."
print "Host PID $game_os_pid / Wine PID $game_windows_pid / session $session / MVID $expected_mvid."
print "Action Monitor PID $monitor_pid and dashboard PID $dashboard_pid are bound to this deployment."
print "Local dashboard ready at http://127.0.0.1:47635/."
