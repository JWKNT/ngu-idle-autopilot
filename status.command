#!/bin/zsh
# FILE PURPOSE
#
# This read-only diagnostic prints current game/monitor processes and recent telemetry/log state so
# an operator can distinguish healthy synchronization from stale files. It must never inject,
# eject, kill, or mutate the save; recovery actions belong to run.command/stop.command and explicit
# deployment procedures.
set -euo pipefail

bot_dir=${0:A:h}
if [[ -f "$bot_dir/runtime/decision.json" ]]; then
  print "Latest decision:"
  sed -n '1,120p' "$bot_dir/runtime/decision.json"
else
  print "No decision has been recorded yet."
fi
if [[ -f "$bot_dir/runtime/logs/inject.log" ]]; then
  print "\nRecent log:"
  tail -30 "$bot_dir/runtime/logs/inject.log"
fi
