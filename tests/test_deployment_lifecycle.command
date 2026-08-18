#!/bin/zsh
# FILE PURPOSE
#
# This fixture-only regression suite exercises run.command, stop.command, and status.command as
# complete processes without addressing Wine, NGU Idle, the live runtime directory, or any save.
# It creates explicit deployment-lifecycle.* temporary roots containing synthetic macOS/Wine PID
# tables, fake artifacts, injector output, and delayed lifecycle telemetry. The cases prove zero/
# duplicate refusal, an exact one-process synchronized claim, stop validation, failed-handshake
# cleanup, legacy/stale archival after restart, PID-reuse refusal, and telemetry-mismatch refusal.
#
# The only outputs are assertion diagnostics and a nonzero exit on failure. Cleanup validates every
# mktemp root before recursive deletion. New lifecycle fields or claim invariants belong in these
# fixtures; native injection, process signals, monitor launch, and production runtime files do not.
set -euo pipefail

repo_dir=${0:A:h:h}
run_script="$repo_dir/run.command"
stop_script="$repo_dir/stop.command"
status_script="$repo_dir/status.command"
fixture_mvid="11111111-2222-3333-4444-555555555555"
assertions=0
roots=()
LAST_OUTPUT=""
LAST_STATUS=0
NEW_FIXTURE=""

cleanup() {
  local root
  for root in "${roots[@]}"; do
    if [[ "$root" == *"/deployment-lifecycle."* && -d "$root" ]]; then
      /bin/rm -rf -- "$root"
    fi
  done
}
trap cleanup EXIT INT TERM

assert_true() {
  local condition=$1 message=$2
  assertions=$((assertions + 1))
  if [[ "$condition" != true ]]; then
    print -u2 "FAIL: $message"
    print -u2 -- "$LAST_OUTPUT"
    exit 1
  fi
}

assert_contains() {
  local haystack=$1 needle=$2 message=$3
  assertions=$((assertions + 1))
  if [[ "$haystack" != *"$needle"* ]]; then
    print -u2 "FAIL: $message (missing '$needle')"
    print -u2 -- "$haystack"
    exit 1
  fi
}

new_fixture() {
  NEW_FIXTURE=$(mktemp -d "${TMPDIR:-/tmp}/deployment-lifecycle.XXXXXX")
  roots+=("$NEW_FIXTURE")
  mkdir -p "$NEW_FIXTURE/runtime/logs" "$NEW_FIXTURE/runtime/deployment-claims/archive"
  python3 - "$NEW_FIXTURE" <<'PY'
from pathlib import Path
import sys
root = Path(sys.argv[1])
(root / "NGUIdleAutopilot.dll").write_bytes(b"synthetic-autopilot-artifact-v1")
(root / "Assembly-CSharp.dll").write_bytes(b"synthetic-game-assembly-v1")
(root / "inject-result.txt").write_text("Injection result: 0x00000000ABCDEF01\n", encoding="utf-8")
(root / "eject-result.txt").write_text("Ejection successful\n", encoding="utf-8")
(root / "processes.json").write_text('{"games": []}\n', encoding="utf-8")
PY
}

set_processes() {
  local root=$1 specification=${2:-}
  python3 - "$root/processes.json" "$specification" <<'PY'
import json, sys
games = []
for raw in filter(None, sys.argv[2].split("|")):
    os_pid, windows_pid, start = raw.split(",", 2)
    games.append({
        "osPid": int(os_pid),
        "windowsPid": int(windows_pid),
        "osStartUtc": start,
        "osCommand": r"C:\Program Files (x86)\Steam\steamapps\common\NGU IDLE\NGUIdle.exe BROWSER_USE_AVAILABLE_BACKENDS=chrome,iab",
    })
with open(sys.argv[1], "w", encoding="utf-8") as handle:
    json.dump({"games": games}, handle)
    handle.write("\n")
PY
}

write_telemetry() {
  local root=$1 session=$2 mvid=$3 decision_session=${4:-$2} decision_game_hash=${5:-}
  python3 - "$root" "$session" "$mvid" "$decision_session" "$decision_game_hash" <<'PY'
from datetime import datetime, timezone
from pathlib import Path
import hashlib, json, sys
root = Path(sys.argv[1])
session, mvid, decision_session, bad_game_hash = sys.argv[2:]
games = json.loads((root / "processes.json").read_text(encoding="utf-8"))["games"]
game = games[0]
dll_hash = hashlib.sha256((root / "NGUIdleAutopilot.dll").read_bytes()).hexdigest()
game_hash = hashlib.sha256((root / "Assembly-CSharp.dll").read_bytes()).hexdigest()
producer_start = game["osStartUtc"].replace("Z", ".5000000Z")
now = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
deployment = {
    "schemaVersion": 2,
    "observedAt": now,
    "producerPid": game["windowsPid"],
    "producerProcessStartUtc": producer_start,
    "producerSessionId": session,
    "telemetryHandshake": f'{game["windowsPid"]}:{session}:{mvid}',
    "activeBuildId": mvid,
    "diskArtifactSha256": dll_hash,
    "gameAssemblySha256": game_hash,
}
decision = {
    "schemaVersion": 2,
    "time": now,
    "producerPid": game["windowsPid"],
    "producerSessionId": decision_session,
    "buildId": mvid,
    "diskArtifactSha256": dll_hash,
    "gameAssemblySha256": bad_game_hash or game_hash,
    "decisionSequence": 7,
    "synced": True,
    "syncState": "active-gameplay",
    "decisionPhase": "post-automation-transaction",
    "automationTransactionComplete": True,
    "automationTransactionError": "",
}
(root / "runtime" / "deployment.json").write_text(json.dumps(deployment, indent=2) + "\n", encoding="utf-8")
(root / "runtime" / "decision.json").write_text(json.dumps(decision, indent=2) + "\n", encoding="utf-8")
PY
}

schedule_telemetry() {
  local root=$1 session=$2 decision_session=${3:-$2} decision_game_hash=${4:-}
  (
    sleep 0.35
    write_telemetry "$root" "$session" "$fixture_mvid" "$decision_session" "$decision_game_hash"
  ) &
}

invoke_run() {
  local root=$1 timeout=${2:-2}
  set +e
  LAST_OUTPUT=$(env NGU_LIFECYCLE_TEST_MODE=fixture-v1 \
    NGU_LIFECYCLE_FIXTURE_ROOT="$root" NGU_HANDSHAKE_TIMEOUT_SECONDS="$timeout" \
    NGU_EXPECTED_MVID_OVERRIDE="$fixture_mvid" "$run_script" 2>&1)
  LAST_STATUS=$?
  set -e
}

invoke_stop() {
  local root=$1
  set +e
  LAST_OUTPUT=$(env NGU_LIFECYCLE_TEST_MODE=fixture-v1 \
    NGU_LIFECYCLE_FIXTURE_ROOT="$root" "$stop_script" 2>&1)
  LAST_STATUS=$?
  set -e
}

invoke_status() {
  local root=$1 requirement=${2:-}
  set +e
  LAST_OUTPUT=$(env NGU_LIFECYCLE_TEST_MODE=fixture-v1 \
    NGU_LIFECYCLE_FIXTURE_ROOT="$root" "$status_script" $requirement 2>&1)
  LAST_STATUS=$?
  set -e
}

run_successful_claim() {
  local root=$1 session=$2
  schedule_telemetry "$root" "$session"
  invoke_run "$root"
  assert_true "$([[ $LAST_STATUS -eq 0 ]] && print true || print false)" "matching fixture injection succeeds"
  assert_contains "$LAST_OUTPUT" "claimed after synchronized fixture handshake" "success is declared only after handshake"
}

test_zero_and_duplicate_refusal() {
  local zero duplicate
  new_fixture
  zero=$NEW_FIXTURE
  set_processes "$zero" ""
  invoke_run "$zero" 1
  assert_true "$([[ $LAST_STATUS -ne 0 ]] && print true || print false)" "zero game processes are refused"
  assert_contains "$LAST_OUTPUT" "no NGUIdle.exe host process" "zero-process reason is explicit"
  assert_true "$([[ ! -f $zero/runtime/deployment-claim.json ]] && print true || print false)" "zero-process refusal writes no claim"

  new_fixture
  duplicate=$NEW_FIXTURE
  set_processes "$duplicate" "7001,1201,2026-08-18T05:00:00Z|7002,1202,2026-08-18T05:00:01Z"
  invoke_run "$duplicate" 1
  assert_true "$([[ $LAST_STATUS -ne 0 ]] && print true || print false)" "two game processes are refused"
  assert_contains "$LAST_OUTPUT" "expected exactly one" "duplicate-process reason is explicit"
  assert_true "$([[ ! -f $duplicate/runtime/deployment-claim.json ]] && print true || print false)" "duplicate refusal writes no claim"
}

test_matching_claim_status_and_stop() {
  local root
  new_fixture
  root=$NEW_FIXTURE
  set_processes "$root" "7001,1201,2026-08-18T05:00:00Z"
  run_successful_claim "$root" "session-one"
  assertions=$((assertions + 1))
  python3 - "$root/runtime/deployment-claim.json" "$fixture_mvid" <<'PY' || { print -u2 "FAIL: claim binds all required identity fields"; exit 1; }
import json, sys
claim = json.load(open(sys.argv[1], encoding="utf-8"))
required = ("assemblyPointer", "gameOsPid", "gameOsProcessStartUtc", "gameOsCommandSha256",
            "producerPid", "producerProcessStartUtc", "producerSessionId", "activeBuildId",
            "diskArtifactSha256", "gameAssemblySha256", "telemetryHandshake")
assert all(claim.get(key) not in (None, "") for key in required)
assert claim["activeBuildId"] == sys.argv[2]
assert claim["gameOsPid"] == 7001 and claim["producerPid"] == 1201
PY
  invoke_status "$root" --require-active
  assert_true "$([[ $LAST_STATUS -eq 0 ]] && print true || print false)" "status recognizes the exact active deployment"
  assert_contains "$LAST_OUTPUT" '"state": "active"' "status publishes active state"
  invoke_stop "$root"
  assert_true "$([[ $LAST_STATUS -eq 0 ]] && print true || print false)" "exact matching claim ejects"
  assert_contains "$LAST_OUTPUT" "validated and unloaded" "fixture ejection reports validated teardown"
  assert_true "$([[ ! -e $root/runtime/deployment-claim.json && ! -e $root/runtime/assembly-pointer.txt ]] && print true || print false)" "successful stop spends claim and pointer"
}

test_failed_handshake_is_never_claimed() {
  local root
  new_fixture
  root=$NEW_FIXTURE
  set_processes "$root" "7101,1211,2026-08-18T05:10:00Z"
  schedule_telemetry "$root" "bad-hash-session" "bad-hash-session" "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
  invoke_run "$root" 1
  assert_true "$([[ $LAST_STATUS -ne 0 ]] && print true || print false)" "hash-mismatched telemetry fails the handshake"
  assert_contains "$LAST_OUTPUT" "was not claimed" "failed handshake never declares injection success"
  assert_true "$([[ ! -f $root/runtime/deployment-claim.json && ! -f $root/runtime/assembly-pointer.txt ]] && print true || print false)" "failed handshake publishes no active ownership"
  pending_count=$(find "$root/runtime/deployment-claims/archive" -name '.pending-injection.*' -type f | wc -l | tr -d ' ')
  assert_true "$([[ $pending_count -ge 1 ]] && print true || print false)" "failed pending claim is archived with cleanup evidence"
}

test_legacy_pointer_and_restart_archival() {
  local root
  new_fixture
  root=$NEW_FIXTURE
  set_processes "$root" "7202,1222,2026-08-18T05:20:02Z"
  python3 - "$root" <<'PY'
from pathlib import Path
import json, sys
root = Path(sys.argv[1])
(root / "runtime" / "assembly-pointer.txt").write_text("0x00000000DEAD0001\n", encoding="utf-8")
(root / "runtime" / "deployment.json").write_text(json.dumps({
    "schemaVersion": 2,
    "producerPid": 1221,
    "producerProcessStartUtc": "2026-08-18T05:20:00.5000000Z",
    "producerSessionId": "prior-process",
}) + "\n", encoding="utf-8")
PY
  run_successful_claim "$root" "replacement-session"
  legacy_count=$(find "$root/runtime/deployment-claims/archive" -name 'assembly-pointer.txt.*' -type f | wc -l | tr -d ' ')
  assert_true "$([[ $legacy_count -ge 1 ]] && print true || print false)" "legacy pointer from prior process is archived before reinjection"

  set_processes "$root" "7203,1223,2026-08-18T05:20:03Z"
  invoke_stop "$root"
  assert_true "$([[ $LAST_STATUS -ne 0 ]] && print true || print false)" "stop refuses a claim after game restart"
  assert_contains "$LAST_OUTPUT" "prior game process" "restart invalidation is explicit"
  assert_true "$([[ ! -f $root/runtime/deployment-claim.json && ! -f $root/runtime/assembly-pointer.txt ]] && print true || print false)" "restart-stale claim and pointer are archived"
}

test_pid_reuse_refusal() {
  local root run_root
  new_fixture
  root=$NEW_FIXTURE
  set_processes "$root" "7301,1231,2026-08-18T05:30:00Z"
  run_successful_claim "$root" "pid-reuse-session"
  set_processes "$root" "7301,1232,2026-08-18T05:31:00Z"
  invoke_stop "$root"
  assert_true "$([[ $LAST_STATUS -ne 0 ]] && print true || print false)" "PID reuse is refused"
  assert_contains "$LAST_OUTPUT" "PID reuse" "PID reuse gets a distinct diagnostic"
  reused_count=$(find "$root/runtime/deployment-claims/archive" -name 'deployment-claim.json.*' -type f | wc -l | tr -d ' ')
  assert_true "$([[ $reused_count -ge 1 ]] && print true || print false)" "PID-reused claim is archived, never ejected"

  new_fixture
  run_root=$NEW_FIXTURE
  set_processes "$run_root" "7351,1235,2026-08-18T05:35:00Z"
  run_successful_claim "$run_root" "run-pid-reuse-session"
  set_processes "$run_root" "7351,1236,2026-08-18T05:36:00Z"
  invoke_run "$run_root" 1
  assert_true "$([[ $LAST_STATUS -ne 0 ]] && print true || print false)" "run also refuses a reused PID on the archival invocation"
  assert_contains "$LAST_OUTPUT" "claimed PID was reused" "run reports claimed PID reuse distinctly"
  assert_true "$([[ ! -f $run_root/runtime/deployment-claim.json ]] && print true || print false)" "run archives a PID-reused claim without publishing replacement ownership"
}

test_stop_telemetry_mismatch_refusal() {
  local root
  new_fixture
  root=$NEW_FIXTURE
  set_processes "$root" "7401,1241,2026-08-18T05:40:00Z"
  run_successful_claim "$root" "stop-session"
  write_telemetry "$root" "stop-session" "$fixture_mvid" "wrong-decision-session"
  invoke_stop "$root"
  assert_true "$([[ $LAST_STATUS -ne 0 ]] && print true || print false)" "stop refuses mismatched decision telemetry"
  assert_contains "$LAST_OUTPUT" "does not exactly match" "stop telemetry mismatch is explicit"
  assert_true "$([[ -f $root/runtime/deployment-claim.json && -f $root/runtime/assembly-pointer.txt ]] && print true || print false)" "mismatch retains active evidence instead of guessing"
  write_telemetry "$root" "stop-session" "$fixture_mvid"
  invoke_stop "$root"
  assert_true "$([[ $LAST_STATUS -eq 0 ]] && print true || print false)" "restored exact telemetry permits stop"
}

test_zero_and_duplicate_refusal
test_matching_claim_status_and_stop
test_failed_handshake_is_never_claimed
test_legacy_pointer_and_restart_archival
test_pid_reuse_refusal
test_stop_telemetry_mismatch_refusal

print "Deployment lifecycle fixture tests passed: $assertions assertions"
