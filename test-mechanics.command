#!/bin/zsh
# FILE PURPOSE
#
# Purpose: Compile and run the dependency-free mechanics golden suite without building or loading
# the NGU Idle autopilot assembly.
#
# Mechanism: The script selects only source/Autopilot/Mechanics*.cs, Reset*.cs, Titan*.cs, and the
# standalone tests/MechanicsRegressionTests.cs entry point. It invokes the same CrossOver C# compiler
# family used by build.command, emits under build/tests, and runs that isolated executable.
#
# Inputs and outputs: There are no game/runtime inputs. Compiler and test diagnostics go to stdout;
# build/tests/MechanicsRegressionTests.exe is the only generated artifact.
#
# Invariants and safety: This script must never call build.command, load Assembly-CSharp, inject a DLL,
# open a save, or read/write runtime/configuration state. A nonzero compiler or test exit propagates.
#
# Extension points and non-goals: Add new pure file prefixes to the sources array only when they have
# no Unity/game dependency. Live differential validation remains a separate explicitly authorized job.
set -euo pipefail

bot_dir=${0:A:h}
crossover_app="/Users/jw/Applications/CrossOver 26.3.app"
wine_bin="$crossover_app/Contents/SharedSupport/CrossOver/bin/wine"
mono_dir="$crossover_app/Contents/SharedSupport/CrossOver/share/wine/mono/wine-mono-10.4.1/lib/mono/4.5"
test_build_dir="$bot_dir/build/tests"

mkdir -p "$test_build_dir"
cd "$bot_dir"

sources=(
  source/Autopilot/Mechanics*.cs(N)
  source/Autopilot/Reset*.cs(N)
  source/Autopilot/Titan*.cs(N)
  tests/MechanicsRegressionTests.cs
)

env CX_BOTTLE=Steam "$wine_bin" "$mono_dir/csc.exe" \
  -nologo -langversion:latest -target:exe -out:build/tests/MechanicsRegressionTests.exe \
  -r:System.dll -r:System.Core.dll "${sources[@]}"

env CX_BOTTLE=Steam "$wine_bin" build/tests/MechanicsRegressionTests.exe

env CX_BOTTLE=Steam "$wine_bin" "$mono_dir/csc.exe" \
  -nologo -langversion:latest -target:exe -out:build/tests/ExecutionSafetyRegressionTests.exe \
  source/Autopilot/ExecutionSafety.cs tests/ExecutionSafetyRegressionTests.cs

env CX_BOTTLE=Steam "$wine_bin" build/tests/ExecutionSafetyRegressionTests.exe
