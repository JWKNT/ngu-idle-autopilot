#!/bin/zsh
# FILE PURPOSE
#
# This script cleanly ejects the exact assembly pointer recorded by run.command and stops only the
# companion monitor and the exact loopback dashboard bridge PID. It intentionally does not kill
# NGU Idle; a full game restart is a separate deployment step used when Mono may retain old
# assemblies. Never guess an assembly pointer or target unrelated Wine/CrossOver/Python processes.
set -euo pipefail

bot_dir=${0:A:h}
wine_bin="/Users/jw/Applications/CrossOver 26.3.app/Contents/SharedSupport/CrossOver/bin/wine"
pointer_file="$bot_dir/runtime/assembly-pointer.txt"
dashboard_script="$bot_dir/monitor/dashboard_server.py"
dashboard_pid_file="$bot_dir/runtime/dashboard-server.pid"

# Read-only companions can remain even when no assembly pointer exists, so clean them first. The
# dashboard process is stopped only when both its recorded PID and full script path agree.
monitor_app="$bot_dir/NGU Action Monitor.app"
pkill -f "$monitor_app/Contents/MacOS/NGUActionMonitor" 2>/dev/null || true
rm -f "$bot_dir/runtime/action-monitor.pid"
if [[ -f "$dashboard_pid_file" ]]; then
  dashboard_pid=$(<"$dashboard_pid_file")
  dashboard_command=$(ps -p "$dashboard_pid" -o command= 2>/dev/null || true)
  if [[ "$dashboard_pid" == <-> && "$dashboard_command" == *"$dashboard_script"* ]]; then
    kill "$dashboard_pid" 2>/dev/null || true
    print "Local dashboard stopped."
  fi
  rm -f "$dashboard_pid_file"
fi

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
