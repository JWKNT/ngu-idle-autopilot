#!/bin/zsh
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
