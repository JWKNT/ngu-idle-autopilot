#!/bin/zsh
set -euo pipefail

bot_dir=${0:A:h}
source_dir="$bot_dir/source"
game_managed="/Users/jw/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/NGU IDLE/NGUIdle_Data/Managed"
crossover_app="/Users/jw/Applications/CrossOver 26.3.app"
wine_bin="$crossover_app/Contents/SharedSupport/CrossOver/bin/wine"
mono_dir="$crossover_app/Contents/SharedSupport/CrossOver/share/wine/mono/wine-mono-10.4.1/lib/mono/4.5"
refs_dir="$bot_dir/build/references"

mkdir -p "$refs_dir" "$bot_dir/build"
cp "$game_managed/Assembly-CSharp.dll" "$refs_dir/"
cp "$game_managed"/UnityEngine*.dll "$refs_dir/"

cd "$source_dir"
env CX_BOTTLE=Steam "$wine_bin" "$mono_dir/resgen.exe" SettingsForm.resx ../build/SettingsForm.resources

sources=(**/*.cs(N))
unity_refs=(../build/references/UnityEngine*.dll(N))
ref_args=()
for ref in "${unity_refs[@]}"; do ref_args+=("-r:$ref"); done

env CX_BOTTLE=Steam "$wine_bin" "$mono_dir/csc.exe" \
  -nologo -langversion:latest -target:library -out:../NGUIdleAutopilot.dll \
  -resource:../build/SettingsForm.resources,NGUInjector.SettingsForm.resources \
  -r:../build/references/Assembly-CSharp.dll "${ref_args[@]}" \
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll \
  -r:System.Xml.dll -r:System.Data.dll -r:System.Xml.Linq.dll "${sources[@]}"

print "Built $bot_dir/NGUIdleAutopilot.dll"

if [[ -x /usr/bin/swiftc ]]; then
  /usr/bin/swiftc -O -framework AppKit "$bot_dir/monitor/ActionMonitor.swift" -o "$bot_dir/ngu-action-monitor"
  app_contents="$bot_dir/NGU Action Monitor.app/Contents"
  mkdir -p "$app_contents/MacOS"
  cp "$bot_dir/ngu-action-monitor" "$app_contents/MacOS/NGUActionMonitor"
  cp "$bot_dir/monitor/Info.plist" "$app_contents/Info.plist"
  /usr/bin/codesign --force --sign - --deep "$bot_dir/NGU Action Monitor.app"
  print "Built $bot_dir/ngu-action-monitor"
fi
