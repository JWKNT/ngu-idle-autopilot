#!/bin/zsh
# FILE PURPOSE
#
# Compile and run the read-only rebirth-policy golden tests against the already-built autopilot DLL.
# This never builds/injects the bot, launches NGU Idle, or reads/writes save, runtime, or config state.
# The only generated file is build/tests/RebirthPolicyGoldenTests.exe; a stale production DLL will
# naturally fail when expected policy APIs are absent, so callers must build explicitly beforehand.
set -euo pipefail

bot_dir=${0:A:h}
crossover_app="/Users/jw/Applications/CrossOver 26.3.app"
wine_bin="$crossover_app/Contents/SharedSupport/CrossOver/bin/wine"
mono_dir="$crossover_app/Contents/SharedSupport/CrossOver/share/wine/mono/wine-mono-10.4.1/lib/mono/4.5"

mkdir -p "$bot_dir/build/tests"
cd "$bot_dir"

env CX_BOTTLE=Steam "$wine_bin" "$mono_dir/csc.exe" \
  -nologo -langversion:latest -target:exe \
  -out:build/tests/RebirthPolicyGoldenTests.exe \
  -r:System.dll tests/RebirthPolicyGoldenTests.cs

env CX_BOTTLE=Steam "$wine_bin" build/tests/RebirthPolicyGoldenTests.exe
