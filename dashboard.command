#!/bin/zsh

# FILE PURPOSE
#
# This is the operator's one-command dashboard check and reconnect entrypoint. It proves that the
# loopback bridge is reading a fresh snapshot from the active game, updates the installed public
# tunnel supervisor from this checkout, preserves a healthy public route, and restarts only a stale
# public route. It never injects the bot, changes game state, or exposes a control endpoint.

set -euo pipefail

readonly repo_dir=${0:A:h}
readonly local_base="http://127.0.0.1:47635"
readonly public_page="https://jehlp.net/ngu-idle-dashboard/"
readonly gist_id="574be4aaf834537b70c62e4505f5ea31"
readonly gist_file="ngu-dashboard-endpoint.json"
readonly launch_label="net.jehlp.ngu-dashboard-cloudflared"
readonly launch_domain="gui/$(id -u)"
readonly launch_plist="$HOME/Library/LaunchAgents/$launch_label.plist"
readonly installed_dir="$HOME/Library/Application Support/NGUDashboardTunnel"
readonly installed_supervisor="$installed_dir/run_dashboard_public_tunnel.command"
readonly source_supervisor="$repo_dir/monitor/run_dashboard_public_tunnel.command"
readonly runtime_dir="$repo_dir/runtime"
readonly dashboard_server="$repo_dir/monitor/dashboard_server.py"
readonly dashboard_pid_file="$runtime_dir/dashboard-server.pid"
readonly dashboard_log="$runtime_dir/logs/dashboard-server.log"
readonly deployment_claim="$runtime_dir/deployment-claim.json"
readonly python_bin="$(command -v python3)"

restart_local_bridge() {
  if ! "$repo_dir/status.command" --require-active >/dev/null 2>&1; then
    print -u2 "The bot does not have a healthy active deployment. Run ./status.command for details."
    print -u2 "If the bot is inactive, start it with ./run.command."
    return 1
  fi

  local claim_signature
  claim_signature="$("$python_bin" - "$deployment_claim" <<'PY'
import json, sys
claim = json.load(open(sys.argv[1], encoding="utf-8"))
keys = ("claimState", "gameOsPid", "gameOsProcessStartUtc", "producerPid",
        "producerSessionId", "activeBuildId", "diskArtifactSha256")
print(json.dumps({key: claim.get(key) for key in keys}, sort_keys=True, separators=(",", ":")))
PY
)"

  local old_pid old_command listener_pid listener_command dashboard_pid claim_update
  if [[ -f "$dashboard_pid_file" ]]; then
    old_pid="$(<"$dashboard_pid_file")"
    old_command="$(ps -p "$old_pid" -o command= 2>/dev/null || true)"
    if [[ "$old_pid" == <-> && "$old_command" == *"dashboard_server.py"* ]]; then
      kill "$old_pid" 2>/dev/null || true
    fi
    /bin/rm -f "$dashboard_pid_file"
  fi

  for listener_pid in $(/usr/sbin/lsof -tiTCP:47635 -sTCP:LISTEN 2>/dev/null || true); do
    listener_command="$(ps -p "$listener_pid" -o command= 2>/dev/null || true)"
    if [[ "$listener_command" == *"dashboard_server.py"* ]]; then
      kill "$listener_pid" 2>/dev/null || true
    else
      print -u2 "Port 47635 belongs to unrelated PID $listener_pid; refusing to replace it."
      return 1
    fi
  done
  for _ in {1..20}; do
    [[ -z "$(/usr/sbin/lsof -tiTCP:47635 -sTCP:LISTEN 2>/dev/null || true)" ]] && break
    /bin/sleep 0.1
  done

  "$python_bin" "$dashboard_server" --root "$repo_dir/docs" --runtime "$runtime_dir" --port 47635 \
    --daemon --pid-file "$dashboard_pid_file" --log "$dashboard_log"
  dashboard_pid=""
  for _ in {1..40}; do
    [[ -f "$dashboard_pid_file" ]] && dashboard_pid="$(<"$dashboard_pid_file")" || dashboard_pid=""
    listener_pid="$(/usr/sbin/lsof -tiTCP:47635 -sTCP:LISTEN 2>/dev/null | /usr/bin/head -1 || true)"
    if [[ "$dashboard_pid" == <-> && "$listener_pid" == "$dashboard_pid" ]] \
        && /usr/bin/curl --fail --silent --max-time 2 "$local_base/api/health" >/dev/null; then
      break
    fi
    dashboard_pid=""
    /bin/sleep 0.1
  done
  if [[ -z "$dashboard_pid" ]]; then
    print -u2 "The local dashboard listener did not become healthy. See $dashboard_log."
    return 1
  fi

  claim_update="$(/usr/bin/mktemp "$runtime_dir/.deployment-claim.XXXXXX")"
  if ! "$python_bin" - "$deployment_claim" "$claim_update" "$dashboard_pid" "$claim_signature" <<'PY'
import json, sys
claim_path, output_path, dashboard_pid, expected = sys.argv[1:]
claim = json.load(open(claim_path, encoding="utf-8"))
keys = ("claimState", "gameOsPid", "gameOsProcessStartUtc", "producerPid",
        "producerSessionId", "activeBuildId", "diskArtifactSha256")
actual = json.dumps({key: claim.get(key) for key in keys}, sort_keys=True, separators=(",", ":"))
if actual != expected:
    raise SystemExit("deployment identity changed while restarting the dashboard")
claim["dashboardPid"] = int(dashboard_pid)
with open(output_path, "w", encoding="utf-8") as handle:
    json.dump(claim, handle, indent=2, sort_keys=True)
    handle.write("\n")
PY
  then
    kill "$dashboard_pid" 2>/dev/null || true
    /bin/rm -f "$claim_update"
    print -u2 "Deployment identity changed; the replacement dashboard was not claimed."
    return 1
  fi
  /bin/mv "$claim_update" "$deployment_claim"
}

public_endpoint() {
  /opt/homebrew/bin/gh gist view "$gist_id" -f "$gist_file" 2>/dev/null \
    | "$python_bin" -c 'import json, sys; print(json.load(sys.stdin).get("apiBase", ""))' 2>/dev/null
}

public_endpoint_healthy() {
  local tunnel_url="$1"
  local tunnel_host="${tunnel_url#https://}"
  local public_ip
  [[ "$tunnel_url" == https://*.trycloudflare.com ]] || return 1
  public_ip="$(/usr/bin/dig +time=2 +tries=1 +short @1.1.1.1 "$tunnel_host" A 2>/dev/null \
    | /usr/bin/awk '/^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$/ { print; exit }')"
  [[ -n "$public_ip" ]] || return 1
  /usr/bin/curl --fail --silent --max-time 10 \
    --resolve "$tunnel_host:443:$public_ip" "$tunnel_url/api/health" >/dev/null 2>&1
}

if ! /usr/bin/curl --fail --silent --max-time 5 "$local_base/api/health" >/dev/null; then
  print "Reconnecting the local dashboard to the active deployment..."
  restart_local_bridge || exit 1
fi

state_summary="$(/usr/bin/curl --fail --silent --max-time 5 "$local_base/api/state" \
  | "$python_bin" -c '
import json, sys
payload = json.load(sys.stdin)
age_value = payload.get("stateAgeSeconds")
if not isinstance(age_value, (int, float)):
    raise SystemExit(1)
age = float(age_value)
state = payload.get("state") or {}
if not payload.get("ok") or age > 10 or not state.get("enabled"):
    raise SystemExit(1)
print("build %s · snapshot %.1fs old · %s" % (
    str(state.get("buildId", "unknown"))[:12], age, state.get("stage", "active game")))
')" || {
  print -u2 "The bridge is running, but it is not receiving a fresh active-game snapshot."
  print -u2 "Run ./status.command for the deployment diagnosis; use ./run.command only if it says the bot is inactive."
  exit 1
}

print "Local dashboard connected: $state_summary"

for required in /opt/homebrew/bin/cloudflared /opt/homebrew/bin/gh /usr/bin/dig; do
  if [[ ! -x "$required" ]]; then
    print -u2 "Public dashboard prerequisite is missing: $required"
    exit 1
  fi
done
if [[ ! -f "$launch_plist" ]]; then
  print -u2 "The public dashboard LaunchAgent is not installed at $launch_plist."
  print -u2 "The local dashboard is still available at $local_base/."
  exit 1
fi

/bin/mkdir -p "$installed_dir"
supervisor_changed=false
if [[ ! -f "$installed_supervisor" ]] || ! /usr/bin/cmp -s "$source_supervisor" "$installed_supervisor"; then
  /usr/bin/install -m 755 "$source_supervisor" "$installed_supervisor"
  supervisor_changed=true
fi

tunnel_url="$(public_endpoint || true)"
if [[ "$supervisor_changed" == false ]] && public_endpoint_healthy "$tunnel_url"; then
  print "Public dashboard connected: $public_page"
  print "Local fallback: $local_base/"
  exit 0
fi

print "Refreshing the public dashboard connection..."
/bin/launchctl kickstart -k "$launch_domain/$launch_label"

for _ in {1..180}; do
  tunnel_url="$(public_endpoint || true)"
  if public_endpoint_healthy "$tunnel_url"; then
    print "Public dashboard connected: $public_page"
    print "Local fallback: $local_base/"
    exit 0
  fi
  /bin/sleep 1
done

print -u2 "The local dashboard is connected, but the public tunnel did not become healthy within three minutes."
print -u2 "See $HOME/Library/Logs/NGUDashboardCloudflared.current.log for the transport error."
exit 1
