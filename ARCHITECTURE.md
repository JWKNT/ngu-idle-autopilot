<!--
FILE PURPOSE

This document maps injector lifecycle, synchronized control loops, native game-controller
boundaries, telemetry, and the separate monitor process. It is the quickest architectural handoff
for future contributors; keep ownership arrows and safety boundaries aligned with executable code.
-->

# Architecture

## Trust and execution boundaries

```text
SharpMonoInjector console
  transport only: inject/eject DLL
              |
              v
NGUAutopilot.Loader.Init
  creates one Unity GameObject host
              |
              v
Main.cs synchronization + scheduler
  refuses mutations outside verified gameplay
              |
              +---------------------------+
              |                           |
              v                           v
Autopilot planner                  Native action managers
snapshot -> plan -> ETAs           combat / inventory / zones /
-> generated allocation profile   purchases / rebirth / etc.
              |
              v
runtime telemetry (JSON + append-only action log)
              |
              v
macOS Action Monitor
read-only presentation; no game-control authority
```

## Source layers

### Autopilot strategy

`source/Autopilot/` contains the policy layer. It selects the current progression objective, derives resource allocations, calculates boss and rebirth ETAs, exposes short-term goals, and chooses actions allowed by configuration.

Notable components:

- `AutopilotPlanner.cs`: stage-specific Normal/Evil/Sadistic planning.
- `AutopilotManager.cs`: live snapshot, action coordination, and decision telemetry.
- `RebirthOptimizer.cs`: exact piecewise time-multiplier candidates, projected boss/training events, AP breakpoints, and sticky near-tie selection.
- `ProgressionGoalEngine.cs`: source-backed progression gates shown in the monitor.
- `AutopilotConfig.cs`: mode and irreversible-action boundaries.

### Allocation execution

`source/AllocationProfiles/` parses timed resource priorities and uses the game's native allocation methods. The autopilot generates `runtime/profiles/autopilot.generated.json`; it is runtime output, not source.

### Native controller adapters

`source/Managers/` contains the game-facing implementation. These classes verify state before and after operations such as gear swaps, inventory merges, Adventure movement, attacks, quests, and Titan handling. Strategy belongs in `Autopilot/`; direct game mutation belongs here or in the inherited allocation breakpoint layer.

### Unity host

`source/AutopilotLoader.cs` is the public injected entry point. `source/Main.cs` owns the Unity lifecycle and schedules work at the appropriate cadence. The synchronization gate checks the game's `MainMenuController`; log text alone never authorizes mutations.

### Monitor

`monitor/ActionMonitor.swift` has no reference to the game process and no input mechanism. It verifies telemetry schema/build/process/sequence fields and color-codes confirmed actions, warnings, resource holds, combat, progression, and ETAs.

## What is not source

- `runtime/`: local config, saves/backups, logs, decisions, generated profiles, and assembly pointers.
- `build/`: copied game references and compiler output.
- `work/` and `out/`: decompilation/audit scratch material.
- `NGUIdleAutopilot*.dll`, `*.exe`, and the monitor `.app`: generated or third-party binaries.
- `Assembly-CSharp.dll` and Unity assemblies: game-owned compile-time references.

All of the above are ignored so a clone represents the authored/modified code rather than one player's live game state.
