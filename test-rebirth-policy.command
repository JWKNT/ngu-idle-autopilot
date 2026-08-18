#!/bin/zsh
# FILE PURPOSE
#
# Compile and run the read-only rebirth-policy golden tests against the already-built autopilot DLL.
# This never builds/injects the bot, launches NGU Idle, or reads/writes save, runtime, or config state.
# The executable is emitted into a unique temporary build/tests directory and removed on exit. A
# stale production DLL naturally fails when expected policy APIs are absent, so callers build first.
set -euo pipefail

bot_dir=${0:A:h}
crossover_app="/Users/jw/Applications/CrossOver 26.3.app"
wine_bin="$crossover_app/Contents/SharedSupport/CrossOver/bin/wine"
mono_dir="$crossover_app/Contents/SharedSupport/CrossOver/share/wine/mono/wine-mono-10.4.1/lib/mono/4.5"

mkdir -p "$bot_dir/build/tests"
temporary_absolute=$(mktemp -d "$bot_dir/build/tests/rebirth-policy.XXXXXX")
temporary_relative=${temporary_absolute#$bot_dir/}
cleanup() {
  if [[ "$temporary_absolute" == "$bot_dir"/build/tests/rebirth-policy.* ]]; then
    find "$temporary_absolute" -type f -delete
    find "$temporary_absolute" -depth -type d -empty -delete
  fi
}
trap cleanup EXIT INT TERM
cd "$bot_dir"

env CX_BOTTLE=Steam "$wine_bin" "$mono_dir/csc.exe" \
  -nologo -langversion:latest -target:exe \
  -out:"$temporary_relative/RebirthPolicyGoldenTests.exe" \
  -r:System.dll tests/RebirthPolicyGoldenTests.cs

env CX_BOTTLE=Steam "$wine_bin" "$temporary_relative/RebirthPolicyGoldenTests.exe"
