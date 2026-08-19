#!/bin/zsh
# FILE PURPOSE
#
# Purpose: Aggregate every controller-free, source-contract, compiled-DLL reflection, lifecycle,
# and fault-injection regression suite landed by audit tasks 1-29.
#
# Mechanism: Each test is compiled into a unique temporary build/tests directory with either its
# minimal pure production sources, the complete source tree for live-type compile coverage, or the
# already-built DLL for internal reflection. Stubbed mutation suites compile only their owning
# production files. The fixture-only deployment lifecycle suite runs last.
#
# Inputs and outputs: Inputs are maintained source/tests, the explicitly built bot DLL, and the
# copied read-only reference assemblies under build/references/work. Diagnostics and exact assertion
# counts go to stdout. Temporary executables are deleted on exit.
#
# Invariants and safety: This runner never invokes build.command, injects/restarts/ejects NGU Idle,
# opens a save, writes runtime/configuration, or calls a live native controller. Native-binding tests
# inspect metadata only. A nonzero compiler or test result stops the aggregate immediately.
#
# Extension points and non-goals: Every new focused suite must be added here with its narrowest
# useful compile boundary. Copied-save/live differentials remain separately authorized work.
set -euo pipefail

bot_dir=${0:A:h}
crossover_app="/Users/jw/Applications/CrossOver 26.3.app"
wine_bin="$crossover_app/Contents/SharedSupport/CrossOver/bin/wine"
mono_dir="$crossover_app/Contents/SharedSupport/CrossOver/share/wine/mono/wine-mono-10.4.1/lib/mono/4.5"

mkdir -p "$bot_dir/build/tests"
temporary_absolute=$(mktemp -d "$bot_dir/build/tests/task29.XXXXXX")
temporary_relative=${temporary_absolute#$bot_dir/}
cp "$bot_dir/NGUIdleAutopilot.dll" "$temporary_absolute/"
cp "$bot_dir/build/references/Assembly-CSharp.dll" "$temporary_absolute/"
cp "$bot_dir"/build/references/UnityEngine*.dll "$temporary_absolute/"
cleanup() {
  if [[ "$temporary_absolute" == "$bot_dir"/build/tests/task29.* ]]; then
    find "$temporary_absolute" -type f -delete
    find "$temporary_absolute" -depth -type d -empty -delete
  fi
}
trap cleanup EXIT INT TERM
cd "$bot_dir"

unity_refs=(build/references/UnityEngine*.dll(N))
game_ref_args=(-r:build/references/Assembly-CSharp.dll)
for ref in "${unity_refs[@]}"; do game_ref_args+=("-r:$ref"); done

compile_run() {
  local name=$1
  shift
  print "== $name =="
  env CX_BOTTLE=Steam "$wine_bin" "$mono_dir/csc.exe" \
    -nologo -langversion:latest -target:exe \
    -out:"$temporary_relative/$name.exe" -r:System.dll -r:System.Core.dll \
    "$@" "tests/$name.cs"
  env CX_BOTTLE=Steam "$wine_bin" "$temporary_relative/$name.exe"
}

compile_run_define() {
  local name=$1 symbol=$2
  shift 2
  print "== $name =="
  env CX_BOTTLE=Steam "$wine_bin" "$mono_dir/csc.exe" \
    -nologo -langversion:latest -target:exe -define:"$symbol" \
    -out:"$temporary_relative/$name.exe" -r:System.dll -r:System.Core.dll \
    "$@" "tests/$name.cs"
  env CX_BOTTLE=Steam "$wine_bin" "$temporary_relative/$name.exe"
}

compile_full_run() {
  local name=$1 entry=$2
  local full_sources=(source/**/*.cs(N))
  print "== $name (full-source compile) =="
  env CX_BOTTLE=Steam "$wine_bin" "$mono_dir/csc.exe" \
    -nologo -langversion:latest -target:exe -main:"$entry" \
    -out:"$temporary_relative/$name.exe" -r:System.dll -r:System.Core.dll \
    -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Xml.dll -r:System.Data.dll \
    -r:System.Xml.Linq.dll "${game_ref_args[@]}" "${full_sources[@]}" "tests/$name.cs"
  env CX_BOTTLE=Steam "$wine_bin" "$temporary_relative/$name.exe"
}

compile_reflection_run() {
  local name=$1
  shift
  print "== $name (compiled-DLL reflection) =="
  env CX_BOTTLE=Steam "$wine_bin" "$mono_dir/csc.exe" \
    -nologo -langversion:latest -target:exe \
    -out:"$temporary_relative/$name.exe" -r:System.dll -r:System.Core.dll \
    "tests/$name.cs"
  env CX_BOTTLE=Steam "$wine_bin" "$temporary_relative/$name.exe" "$@"
}

compile_run MechanicsRegressionTests \
  source/Autopilot/Mechanics*.cs(N) source/Autopilot/ResetStateRegistry.cs \
  source/Autopilot/Titan*.cs(N)
compile_run ExecutionSafetyRegressionTests source/Autopilot/ExecutionSafety.cs
compile_run MutationCoordinatorTests source/Autopilot/ExecutionSafety.cs \
  source/Autopilot/MutationCoordinator.cs
compile_run AllocationReloadTests source/SimpleJson.cs \
  source/AllocationProfiles/AllocationPlanCompiler.cs
compile_run LifecycleEpochTests source/Autopilot/GameEpoch.cs
compile_run NativeBindingContractTests source/Autopilot/NativeBindingRegistry.cs
compile_run LootCapacityTests source/Autopilot/PhysicalTopology.cs source/Autopilot/LootCapacity.cs
compile_full_run InventoryTopologyTests InventoryTopologyTests
compile_full_run PersistentSystemTests PersistentSystemTests
compile_run FightBossOracleTests source/Autopilot/MechanicsOracle.cs
compile_run AdventureCombatStateTests
compile_run TitanOracleTests source/Autopilot/TitanMechanics.cs
compile_run LoadoutSolverTests source/Managers/ParetoLoadoutSolver.cs
compile_run ExactAllocationTests source/Autopilot/ExactResourceAllocator.cs
compile_run ExpPurchasePolicyTests source/Autopilot/ExpPurchasePolicy.cs
compile_run PermanentMarginalTests source/Autopilot/PermanentMarginalOracle.cs
compile_run StochasticKernelTests source/Autopilot/MechanicsStochastic.cs
compile_run CollectionModelTests source/Autopilot/MechanicsStochastic.cs \
  source/Autopilot/PhysicalTopology.cs source/Autopilot/LootCapacity.cs \
  source/Managers/LootSourceCatalog.cs source/Managers/ParetoLoadoutSolver.cs
compile_run ProgressionGraphTests source/Autopilot/MechanicsEndgame.cs \
  source/Autopilot/OptimizationSnapshot.cs source/Autopilot/ProgressionDependencyGraph.cs
compile_run GlobalSchedulerTests source/Autopilot/MechanicsEndgame.cs \
  source/Autopilot/OptimizationSnapshot.cs source/Autopilot/ProgressionDependencyGraph.cs \
  source/Autopilot/GlobalEventScheduler.cs source/Autopilot/PlannerTrace.cs
compile_run_define GoldEventLedgerTests GOLD_LEDGER_TESTS \
  source/Autopilot/ExactResourceAllocator.cs source/Autopilot/ResourceHorizonModel.cs
compile_run GoldBootstrapTests source/Autopilot/GoldBootstrapPlanner.cs

compile_run PermanentPurchaseTests source/Autopilot/ExecutionSafety.cs \
  source/Autopilot/MutationCoordinator.cs source/Autopilot/NativeBindingRegistry.cs \
  source/Autopilot/MechanicsEndgame.cs source/Autopilot/PhysicalTopology.cs \
  source/Autopilot/LootCapacity.cs source/Autopilot/PurchaseDescriptorCatalog.cs \
  source/Autopilot/PermanentPurchaseManager.cs
compile_full_run ApQpPermanentSpendTests ApQpPermanentSpendTests
compile_run ItopodPerkTests source/Autopilot/ExecutionSafety.cs \
  source/Autopilot/MutationCoordinator.cs source/Autopilot/MechanicsOracle.cs \
  source/Autopilot/MechanicsStochastic.cs source/Autopilot/MechanicsEndgame.cs \
  source/Autopilot/PhysicalTopology.cs source/Autopilot/LootCapacity.cs \
  source/Autopilot/ItopodPerkPlanner.cs source/Managers/Move69Manager.cs
compile_run TitanExecutionTests source/Autopilot/ExecutionSafety.cs \
  source/Autopilot/MutationCoordinator.cs source/Autopilot/NativeBindingRegistry.cs \
  source/Autopilot/MechanicsOracle.cs \
  source/Autopilot/MechanicsEndgame.cs source/Autopilot/PhysicalTopology.cs \
  source/Autopilot/LootCapacity.cs source/Autopilot/TitanMechanics.cs \
  source/Managers/ParetoLoadoutSolver.cs source/Managers/TitanExecutionManager.cs
compile_run_define EndgameTransactionTests ENDGAME_TRANSACTION_TEST_STUBS \
  source/Autopilot/ExecutionSafety.cs source/Autopilot/MutationCoordinator.cs \
  source/Autopilot/MechanicsEndgame.cs source/Autopilot/PhysicalTopology.cs \
  source/Autopilot/LootCapacity.cs source/Autopilot/EndgameTransactionManager.cs
compile_full_run PermanentBloodSpellTests PermanentBloodSpellTests

compile_full_run CardCookingTests CardCookingTests
compile_full_run QuestYggTests QuestYggTests
compile_full_run EndgameDependencyTests EndgameDependencyTests
compile_full_run OrdinaryRebirthTransactionTests OrdinaryRebirthTransactionTests

compile_reflection_run ChallengeControllerTests "$temporary_relative/NGUIdleAutopilot.dll"
compile_reflection_run RebirthTransitionTests "$temporary_relative/NGUIdleAutopilot.dll"
compile_reflection_run ResetExecutionTests "$temporary_relative/NGUIdleAutopilot.dll" "$bot_dir"
./test-rebirth-policy.command
./tests/test_deployment_lifecycle.command

print "PASS: aggregate regression runner completed 37 focused suites"
