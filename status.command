#!/bin/zsh
# FILE PURPOSE
#
# This read-only lifecycle inspector reports whether the game, bound injection claim, immutable
# deployment identity, current decision frame, built artifacts, and companion PIDs describe one
# coherent deployment. It understands both the macOS host PID and Wine/.NET producer PID and uses
# process starts to detect PID reuse. It also requires the loaded game's complete build-pinned
# native-binding catalog: one missing token/signature makes the deployment a mismatch. JSON is the
# default output so operators and later integration tasks can consume the same evidence without
# scraping prose; --require-active makes any state other than a fully matching synchronized
# deployment return nonzero.
#
# Inputs are process tables plus runtime claim/deployment/decision files and current DLL/game bytes.
# It writes nothing, never injects/ejects, and never repairs claims. A fixture-only mode is restricted
# to an explicit deployment-lifecycle.* temporary root for safe lifecycle regression tests.
set -euo pipefail

bot_dir=${0:A:h}
wine_bin="${NGU_WINE_BIN:-/Users/jw/Applications/CrossOver 26.3.app/Contents/SharedSupport/CrossOver/bin/wine}"
runtime_dir="${NGU_RUNTIME_DIR:-$bot_dir/runtime}"
dll_path="${NGU_DLL_PATH:-$bot_dir/NGUIdleAutopilot.dll}"
game_assembly_path="${NGU_GAME_ASSEMBLY_PATH:-/Users/jw/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/NGU IDLE/NGUIdle_Data/Managed/Assembly-CSharp.dll}"
fixture_mode=false
fixture_root=""
require_active=false
[[ "${1:-}" == "--require-active" ]] && require_active=true

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
fi

claim_file="$runtime_dir/deployment-claim.json"
pointer_file="$runtime_dir/assembly-pointer.txt"
deployment_file="$runtime_dir/deployment.json"
decision_file="$runtime_dir/decision.json"

discover_game_pids() {
  if [[ "$fixture_mode" == true ]]; then
    python3 - "$fixture_root/processes.json" <<'PY'
import json, sys
for game in json.load(open(sys.argv[1], encoding="utf-8")).get("games", []): print(int(game["osPid"]))
PY
  else
    pgrep -f 'NGUIdle\.exe([[:space:]]|$)' 2>/dev/null || true
  fi
}

game_record_field() {
  local pid=$1 field=$2
  if [[ "$fixture_mode" == true ]]; then
    python3 - "$fixture_root/processes.json" "$pid" "$field" <<'PY'
import json, sys
for game in json.load(open(sys.argv[1], encoding="utf-8")).get("games", []):
    if int(game["osPid"]) == int(sys.argv[2]): print(game.get(sys.argv[3], "")); break
PY
  elif [[ "$field" == "osCommand" ]]; then
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
  else
    local process_listing
    process_listing=$(env CX_BOTTLE=Steam "$wine_bin" winedbg --command 'info proc' 2>&1)
    print -r -- "$process_listing" | awk "/'NGUIdle.exe'/ { gsub(/^=/, \"\", \$1); print \$1 }" | \
      python3 -c 'import sys; [print(int(line.strip(), 16)) for line in sys.stdin if line.strip()]'
  fi
}

game_pids=(${(f)"$(discover_game_pids)"})
windows_pids=()
if (( ${#game_pids} > 0 )); then
  windows_pids=(${(f)"$(discover_windows_pids)"})
fi
current_os_pid=-1
current_os_start=""
current_os_command=""
current_command_hash=""
if (( ${#game_pids} == 1 )); then
  current_os_pid=${game_pids[1]}
  current_os_start=$(game_record_field "$current_os_pid" osStartUtc)
  current_os_command=$(game_record_field "$current_os_pid" osCommand)
  current_command_hash=$(print -rn -- "$current_os_command" | shasum -a 256 | awk '{print $1}')
fi
current_windows_pid=-1
(( ${#windows_pids} == 1 )) && current_windows_pid=${windows_pids[1]}
current_dll_hash=""
current_game_hash=""
[[ -f "$dll_path" ]] && current_dll_hash=$(shasum -a 256 "$dll_path" | awk '{print $1}')
[[ -f "$game_assembly_path" ]] && current_game_hash=$(shasum -a 256 "$game_assembly_path" | awk '{print $1}')
pointer_text=""
[[ -f "$pointer_file" ]] && pointer_text=$(tr -d '\r\n' < "$pointer_file")

status_json=$(python3 - "$claim_file" "$deployment_file" "$decision_file" \
  "${#game_pids}" "$current_os_pid" "$current_os_start" "$current_command_hash" \
  "${#windows_pids}" "$current_windows_pid" "$pointer_text" "$current_dll_hash" \
  "$current_game_hash" <<'PY'
from datetime import datetime
import json, os, sys
claim_path, deployment_path, decision_path = sys.argv[1:4]
host_count, host_pid, host_start, command_hash = sys.argv[4:8]
wine_count, wine_pid, pointer, disk_hash, game_disk_hash = sys.argv[8:13]
host_count, host_pid, wine_count, wine_pid = map(int, (host_count, host_pid, wine_count, wine_pid))
def read(path):
    try: return json.load(open(path, encoding="utf-8"))
    except Exception: return None
def instant(text): return datetime.fromisoformat(str(text).replace("Z", "+00:00"))
claim_file_present = os.path.exists(claim_path)
claim, deployment, decision = read(claim_path), read(deployment_path), read(decision_path)
state, reason = "unclaimed", "no bound deployment claim"
matches = {"process": False, "deployment": False, "decision": False, "pointer": False}
if host_count == 0:
    state, reason = ("stale", "claimed process exited") if claim else ("stopped", "no game process")
elif host_count != 1 or wine_count != 1:
    state, reason = "ambiguous", f"host process count={host_count}, Wine process count={wine_count}"
elif claim_file_present and claim is None:
    state, reason = "malformed", "deployment claim exists but is not valid JSON"
elif claim is None:
    state, reason = ("legacy-unbound", "bare pointer has no ownership claim") if pointer else ("unclaimed", "game is running without a bound claim")
else:
    try:
        same_pid = int(claim["gameOsPid"]) == host_pid
        same_start = claim["gameOsProcessStartUtc"] == host_start
        if same_pid and not same_start:
            state, reason = "stale-pid-reuse", "host PID matches but process start differs"
        else:
            matches["process"] = (same_pid and same_start
                and claim["gameOsCommandSha256"] == command_hash
                and int(claim["producerPid"]) == wine_pid)
            # The JSON claim is authoritative. assembly-pointer.txt is a compatibility mirror:
            # when present it must agree, but a crash between the two atomic renames must not make
            # an otherwise exact claim impossible to eject safely.
            matches["pointer"] = not pointer or pointer == claim["assemblyPointer"]
            if deployment:
                matches["deployment"] = (
                    int(deployment.get("schemaVersion", 0)) >= 2
                    and int(deployment.get("producerPid", -1)) == wine_pid
                    and deployment.get("producerProcessStartUtc") == claim["producerProcessStartUtc"]
                    and abs((instant(claim["producerProcessStartUtc"])-instant(host_start)).total_seconds()) < 2
                    and deployment.get("producerSessionId") == claim["producerSessionId"]
                    and deployment.get("telemetryHandshake") == claim["telemetryHandshake"]
                    and str(deployment.get("activeBuildId", "")).lower() == str(claim["activeBuildId"]).lower()
                    and deployment.get("diskArtifactSha256") == claim["diskArtifactSha256"]
                    and deployment.get("gameAssemblySha256") == claim["gameAssemblySha256"]
                    and deployment.get("nativeBindingKnownBuild") is True
                    and deployment.get("nativeBindingsComplete") is True
                    and type(deployment.get("nativeBindingDescriptorCount")) is int
                    and deployment.get("nativeBindingDescriptorCount") > 0
                    and deployment.get("nativeBindingBoundCount") == deployment.get("nativeBindingDescriptorCount")
                    and deployment.get("nativeBindingFailureCount") == 0)
            if decision:
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
                matches["decision"] = (
                    int(decision.get("producerPid", -1)) == wine_pid
                    and decision.get("producerSessionId") == claim["producerSessionId"]
                    and str(decision.get("buildId", "")).lower() == str(claim["activeBuildId"]).lower()
                    and decision.get("diskArtifactSha256") == claim["diskArtifactSha256"]
                    and decision.get("gameAssemblySha256") == claim["gameAssemblySha256"]
                    and decision.get("synced") is True
                    and decision.get("automationTransactionComplete") is True
                    and decision.get("automationTransactionError") == ""
                    and decision.get("nativeBindingKnownBuild") is True
                    and decision.get("nativeBindingsComplete") is True
                    and type(decision.get("nativeBindingDescriptorCount")) is int
                    and decision.get("nativeBindingDescriptorCount") > 0
                    and decision.get("nativeBindingBoundCount") == decision.get("nativeBindingDescriptorCount")
                    and decision.get("nativeBindingFailureCount") == 0
                    and clean_closed_root)
            if all(matches.values()): state, reason = "active", "all bound identities and synchronized telemetry match"
            elif not matches["process"]: state, reason = "stale", "claim belongs to a different process identity"
            else: state, reason = "mismatch", "claim, pointer, deployment, or decision evidence disagrees"
    except Exception as exc:
        state, reason = "malformed", "claim could not be validated: " + str(exc)
result = {
    "schemaVersion": 1,
    "state": state,
    "reason": reason,
    "gameHostProcessCount": host_count,
    "gameHostPid": host_pid if host_pid > 0 else None,
    "gameHostProcessStartUtc": host_start or None,
    "gameWineProcessCount": wine_count,
    "gameWinePid": wine_pid if wine_pid > 0 else None,
    "claimPresent": claim_file_present,
    "pointerPresent": bool(pointer),
    "matches": matches,
    "decisionSynced": bool(decision and decision.get("synced") is True),
    "automationTransactionComplete": bool(decision and decision.get("automationTransactionComplete") is True),
    "nativeBindingsComplete": decision.get("nativeBindingsComplete") if decision else None,
    "nativeBindingDescriptorCount": decision.get("nativeBindingDescriptorCount") if decision else None,
    "nativeBindingBoundCount": decision.get("nativeBindingBoundCount") if decision else None,
    "nativeBindingFailureCount": decision.get("nativeBindingFailureCount") if decision else None,
    "nativeBindingFailureSummary": decision.get("nativeBindingFailureSummary") if decision else None,
    "currentDiskArtifactSha256": disk_hash or None,
    "currentGameAssemblySha256": game_disk_hash or None,
    "artifactChangedSinceClaim": bool(claim and disk_hash and disk_hash != claim.get("diskArtifactSha256")),
    "gameAssemblyChangedSinceClaim": bool(claim and game_disk_hash and game_disk_hash != claim.get("gameAssemblySha256")),
}
if claim:
    result["claim"] = {key: claim.get(key) for key in (
        "gameOsPid", "gameOsProcessStartUtc", "producerPid", "producerProcessStartUtc",
        "producerSessionId", "activeBuildId", "diskArtifactSha256", "gameAssemblySha256",
        "acceptedDecisionSequence", "monitorPid", "dashboardPid")}
print(json.dumps(result, indent=2, sort_keys=True))
PY
)
print -r -- "$status_json"
state=$(print -r -- "$status_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')
if [[ "$require_active" == true && "$state" != "active" ]]; then
  exit 1
fi
