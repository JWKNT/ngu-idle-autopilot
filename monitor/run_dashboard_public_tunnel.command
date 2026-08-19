#!/bin/zsh

# FILE PURPOSE
#
# This supervisor keeps an account-less Cloudflare Quick Tunnel attached to the dashboard's
# loopback-only HTTP bridge. It proves each generated hostname through public DNS and the bridge's
# read-only health endpoint before publishing that hostname (never telemetry or credentials) to the
# dashboard's GitHub Gist discovery record. It periodically rechecks the public route and replaces a
# tunnel whose process survives after its hostname or route has expired.

set -u

readonly CLOUDFLARED_BIN="/opt/homebrew/bin/cloudflared"
readonly GH_BIN="/opt/homebrew/bin/gh"
readonly BRIDGE_URL="http://127.0.0.1:47635"
readonly GIST_ID="574be4aaf834537b70c62e4505f5ea31"
readonly GIST_FILE="ngu-dashboard-endpoint.json"
readonly LOG_DIR="$HOME/Library/Logs"
readonly TUNNEL_LOG="$LOG_DIR/NGUDashboardCloudflared.log"
readonly CURRENT_LOG="$LOG_DIR/NGUDashboardCloudflared.current.log"
readonly HEALTH_PATH="/api/health"
readonly HEALTH_INTERVAL_SECONDS=30
readonly HEALTH_FAILURE_LIMIT=3

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
  "$GH_BIN" api --method PATCH "/gists/$GIST_ID" --input "$payload" \
    >/dev/null 2>> "$TUNNEL_LOG"
  local result=$?
  /bin/rm -f "$payload"
  return "$result"
}

public_endpoint_healthy() {
  local tunnel_url="$1"
  local tunnel_host="${tunnel_url#https://}"
  local public_ip

  # Asking a public resolver first avoids poisoning the Mac/router resolver with the short
  # NXDOMAIN window that can occur while a new Quick Tunnel hostname is being provisioned.
  public_ip="$(/usr/bin/dig +time=2 +tries=1 +short @1.1.1.1 "$tunnel_host" A 2>/dev/null \
    | /usr/bin/awk '/^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$/ { print; exit }')"
  [[ -n "$public_ip" ]] || return 1

  /usr/bin/curl --fail --silent --max-time 10 \
    --resolve "$tunnel_host:443:$public_ip" \
    "$tunnel_url$HEALTH_PATH" >/dev/null 2>&1
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
    tunnel_healthy=false
    for _ in {1..120}; do
      if public_endpoint_healthy "$tunnel_url"; then
        tunnel_healthy=true
        break
      fi
      kill -0 "$cloudflared_pid" 2>/dev/null || break
      /bin/sleep 1
    done

    if [[ "$tunnel_healthy" == true ]]; then
      endpoint_published=false
      if publish_endpoint "$tunnel_url"; then
        endpoint_published=true
        /bin/date -u "+%Y-%m-%dT%H:%M:%SZ published healthy dashboard tunnel $tunnel_url" \
          >> "$TUNNEL_LOG"
      else
        /bin/date -u '+%Y-%m-%dT%H:%M:%SZ failed to publish dashboard tunnel' >> "$TUNNEL_LOG"
      fi

      health_failures=0
      while kill -0 "$cloudflared_pid" 2>/dev/null; do
        /bin/sleep "$HEALTH_INTERVAL_SECONDS"
        kill -0 "$cloudflared_pid" 2>/dev/null || break
        if public_endpoint_healthy "$tunnel_url"; then
          health_failures=0
          if [[ "$endpoint_published" == false ]] && publish_endpoint "$tunnel_url"; then
            endpoint_published=true
            /bin/date -u "+%Y-%m-%dT%H:%M:%SZ published healthy dashboard tunnel $tunnel_url after retry" \
              >> "$TUNNEL_LOG"
          fi
        else
          (( health_failures += 1 ))
          /bin/date -u "+%Y-%m-%dT%H:%M:%SZ public dashboard health failure $health_failures/$HEALTH_FAILURE_LIMIT" \
            >> "$TUNNEL_LOG"
          if (( health_failures >= HEALTH_FAILURE_LIMIT )); then
            /bin/date -u '+%Y-%m-%dT%H:%M:%SZ replacing stale dashboard tunnel' >> "$TUNNEL_LOG"
            stop_tunnel
            break
          fi
        fi
      done
    else
      /bin/date -u '+%Y-%m-%dT%H:%M:%SZ tunnel hostname never became publicly healthy' \
        >> "$TUNNEL_LOG"
      stop_tunnel
    fi
  else
    /bin/date -u '+%Y-%m-%dT%H:%M:%SZ tunnel hostname was not discovered' >> "$TUNNEL_LOG"
  fi

  wait "$cloudflared_pid" 2>/dev/null || true
  cloudflared_pid=""
  /bin/cat "$CURRENT_LOG" >> "$TUNNEL_LOG"
  /bin/sleep 5
done
