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
Main.cs synchronization + epoch provider
  one nonzero RootTransaction per tick
  refuses mutations outside verified gameplay/epoch
              |
              +---------------------------+
              |                           |
              v                           v
Autopilot planner                  Typed action managers
snapshot -> terminal DAG           current live bridge:
-> bounded scheduler (shadow)      Card/Cooking/Ygg/Quest only
-> staged authority               every other route held
              |
              v
runtime deployment + decision epoch
+ append-only, session-marked action log
              |
              v
macOS Action Monitor + loopback dashboard
exact-session read-only presentation; no game-control authority
```

## Source layers

### Autopilot strategy

`source/Autopilot/` contains the policy layer. It selects the current progression objective, derives resource allocations, calculates boss and rebirth ETAs, exposes short-term goals, and chooses actions allowed by configuration.

Notable components:

- `AutopilotPlanner.cs`: stage-specific Normal/Evil/Sadistic planning.
- `AutopilotManager.cs`: live snapshot, action coordination, and decision telemetry.
- `MutationCoordinator.cs`: one exclusive nonzero root, typed child intents, postconditions,
  compensation/quarantine, and epoch fencing. A normal native return is not proof of commit.
- `ProgressionGoalEngine.cs` / `ProgressionDependencyGraph.cs`: immutable typed terminal DAG.
- `GlobalEventScheduler.cs` / `PlannerTrace.cs`: bounded global search and archived trace surface;
  authority is hard `ShadowOnly` in this deployment.
- `RebirthOptimizer.cs`: exact piecewise time-multiplier candidates, projected boss/training events, AP breakpoints, and sticky near-tie selection.
- `ProgressionGoalEngine.cs`: source-backed progression gates shown in the monitor.
- `MajorUnlockPlanner.cs`: one-time Adventure/Titan mechanic pushes with recovery and contextual gear constraints.
- `AutopilotConfig.cs`: mode and irreversible-action boundaries.

### Allocation execution

`source/AllocationProfiles/` parses timed resource priorities and uses the game's native allocation methods. The autopilot generates `runtime/profiles/autopilot.generated.json`; it is runtime output, not source.

### Native controller adapters

`source/Managers/` contains the game-facing implementation. These classes verify state before and after operations such as gear swaps, inventory merges, Adventure movement, attacks, quests, and Titan handling. Strategy belongs in `Autopilot/`; direct game mutation belongs here or in the inherited allocation breakpoint layer.

### Unity host

`source/AutopilotLoader.cs` is the public injected entry point. `source/Main.cs` owns the Unity lifecycle, binds the shared game-epoch provider, opens at most one root per automation tick, executes typed child intents, and closes/aborts that root before publishing telemetry. The synchronization gate checks the game's `MainMenuController`; log text, a requested configuration flag, or a shadow schedule never authorizes mutations.

### Monitor

`monitor/ActionMonitor.swift` has no game handle and no input mechanism. It requires `deployment.json` and `decision.json` to agree on producer PID, session, active build, bot artifact hash, and game-assembly hash. It then admits Live Actions and Key Events only from the exact matching durable session block; an absent/mismatched marker yields an empty tail. Its Strategy & Goals page exposes the decision/root epoch join, root ID/state/counts, capacity, staged authority, scheduler shadow hashes/statistics/evidence, and rebirth/challenge/difficulty/END horizons.

`monitor/dashboard_server.py` applies the same join for `/api/state`. It returns raw producer state plus a normalized read-only `observability` object and session-tail status. Legacy `-1`, empty hash, and unknown/zero-evidence scheduler sentinels become JSON `null`; the UI renders them as `Unavailable`. It never turns a held route into a countdown or combines `Held`, `Pending`, and `Quarantined`.

### Telemetry and session contract

- `runtime/deployment.json` identifies the injected process/session/build and disk/game artifacts.
- `runtime/decision.json` identifies the same deployment plus the current `gameEpochFingerprint`,
  staged authority, `mutationRoot`, and `globalScheduler` shadow record.
- `mutationRoot.epochFingerprint` must equal the decision epoch for a committed presentation.
- `runtime/logs/actions.log` is append-only. Every injection starts an exact
  `=== SESSION <UTC> id <session> build <MVID> pid <PID> ===` block.
- Monitor/dashboard consumers fail closed: no matching deployment/session marker means no action
  tail, even when older lines are syntactically valid.
- Optional forecast values remain unavailable unless the producer supplied a finite non-negative
  number. Provenance, sample count, and confidence travel together; `Unknown/0/0` is not evidence.

## What is not source

- `runtime/`: local config, saves/backups, logs, decisions, generated profiles, and assembly pointers.
- `build/`: copied game references and compiler output.
- `work/` and `out/`: decompilation/audit scratch material.
- `NGUIdleAutopilot*.dll`, `*.exe`, and the monitor `.app`: generated or third-party binaries.
- `Assembly-CSharp.dll` and Unity assemblies: game-owned compile-time references.

All of the above are ignored so a clone represents the authored/modified code rather than one player's live game state.
