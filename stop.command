#!/bin/zsh
set -euo pipefail

bot_dir=${0:A:h}
wine_bin="/Users/jw/Applications/CrossOver 26.3.app/Contents/SharedSupport/CrossOver/bin/wine"
pointer_file="$bot_dir/runtime/assembly-pointer.txt"

if [[ ! -f "$pointer_file" ]]; then
  print -u2 "No assembly pointer was recorded. Restarting NGU Idle also unloads the bot."
  exit 1
fi

pointer=$(<"$pointer_file")
pointer=${pointer//$'\r'/}
cd "$bot_dir"
result=$(env CX_BOTTLE=Steam "$wine_bin" injector/smi.exe eject -p NGUIdle -a "$pointer" -n NGUAutopilot -c Loader -m Unload)
print -r -- "$result"
if [[ "$result" != *"Ejection successful"* ]]; then
  print -u2 "Ejection did not complete. Restarting NGU Idle will unload the bot."
  exit 1
fi
rm -f "$pointer_file"
print "Autopilot unloaded."

monitor_app="$bot_dir/NGU Action Monitor.app"
pkill -f "$monitor_app/Contents/MacOS/NGUActionMonitor" 2>/dev/null || true
rm -f "$bot_dir/runtime/action-monitor.pid"
