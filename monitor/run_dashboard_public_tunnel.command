#!/bin/zsh

# FILE PURPOSE
#
# This supervisor keeps an account-less Cloudflare Quick Tunnel attached to the dashboard's
# loopback-only HTTP bridge. Each tunnel restart receives a new public hostname, so the supervisor
# publishes only that hostname (never telemetry or credentials) to the dashboard's public GitHub
# Gist discovery record. The browser then discovers the current read-only transport at page load.

set -u

readonly CLOUDFLARED_BIN="/opt/homebrew/bin/cloudflared"
readonly GH_BIN="/opt/homebrew/bin/gh"
readonly BRIDGE_URL="http://127.0.0.1:47635"
readonly GIST_ID="574be4aaf834537b70c62e4505f5ea31"
readonly GIST_FILE="ngu-dashboard-endpoint.json"
readonly LOG_DIR="$HOME/Library/Logs"
readonly TUNNEL_LOG="$LOG_DIR/NGUDashboardCloudflared.log"
readonly CURRENT_LOG="$LOG_DIR/NGUDashboardCloudflared.current.log"

mkdir -p "$LOG_DIR"

cloudflared_pid=""

stop_tunnel() {
  if [[ -n "$cloudflared_pid" ]] && kill -0 "$cloudflared_pid" 2>/dev/null; then
    kill "$cloudflared_pid" 2>/dev/null || true
    wait "$cloudflared_pid" 2>/dev/null || true
  fi
}

shutdown() {
  stop_tunnel
  exit 0
}

publish_endpoint() {
  local tunnel_url="$1"
  local content payload
  content="$(/usr/bin/jq -n --arg apiBase "$tunnel_url" --arg updatedAt "$(/bin/date -u +%Y-%m-%dT%H:%M:%SZ)" \
    '{apiBase:$apiBase,transport:"cloudflare-quick-tunnel",updatedAt:$updatedAt}')"
  payload="$(/usr/bin/mktemp -t ngu-dashboard-gist.XXXXXX)"
  /usr/bin/jq -n --arg filename "$GIST_FILE" --arg content "$content" \
    '{files:{($filename):{content:$content}}}' > "$payload"
  "$GH_BIN" api --method PATCH "/gists/$GIST_ID" --input "$payload" >> "$TUNNEL_LOG" 2>&1
  /bin/rm -f "$payload"
}

trap shutdown INT TERM
trap stop_tunnel EXIT

while true; do
  /bin/date -u '+%Y-%m-%dT%H:%M:%SZ starting dashboard public tunnel' >> "$TUNNEL_LOG"
  : > "$CURRENT_LOG"
  "$CLOUDFLARED_BIN" tunnel --no-autoupdate --url "$BRIDGE_URL" >> "$CURRENT_LOG" 2>&1 &
  cloudflared_pid="$!"

  tunnel_url=""
  for _ in {1..120}; do
    tunnel_url="$(/usr/bin/grep -Eo 'https://[a-z0-9-]+\.trycloudflare\.com' "$CURRENT_LOG" | /usr/bin/tail -1)"
    [[ -n "$tunnel_url" ]] && break
    kill -0 "$cloudflared_pid" 2>/dev/null || break
    /bin/sleep 1
  done

  if [[ -n "$tunnel_url" ]]; then
    publish_endpoint "$tunnel_url" || true
  else
    /bin/date -u '+%Y-%m-%dT%H:%M:%SZ tunnel hostname was not discovered' >> "$TUNNEL_LOG"
  fi

  wait "$cloudflared_pid" 2>/dev/null || true
  cloudflared_pid=""
  /bin/cat "$CURRENT_LOG" >> "$TUNNEL_LOG"
  /bin/sleep 5
done
