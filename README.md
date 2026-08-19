<!--
FILE PURPOSE

This is the public repository entrypoint: it explains what the NGU Idle autopilot does, separates
original source from injector/runtime dependencies, and gives safe build/run orientation. Detailed
operator behavior lives in README-AUTOPILOT.md; architecture and audited strategy assumptions live
in their dedicated documents.
-->

# NGU Idle Autopilot

NGU Idle Autopilot is an adaptive, in-process controller for the Windows Steam build of **NGU Idle**. It reads live Unity game state, continuously evaluates progression choices, and executes actions through the game's own controllers. It does **not** automate mouse movement, screen pixels, or a Computer Use session.

The bot currently coordinates resource allocation, Basic and Advanced Training, Augments, Time Machine, bosses, Adventure combat, inventory merging and conservative trashing, equipment loadouts, purchases, Titans, quests, Yggdrasil, Money Pit, NGUs, hacks, wishes, cards, cooking, and rebirth timing across Normal, Evil, and Sadistic progression.

This repository is source-first. It deliberately excludes game saves, live telemetry, generated profiles, decompiled game code, game/Unity assemblies, compiled bot builds, and third-party injector executables.

## Source map

| Path | Responsibility | Authorship / origin |
| --- | --- | --- |
| `source/Autopilot/` | Strategy planner, live decision telemetry, progression goals, rebirth optimization, purchases, cards, and cooking | Autopilot-specific code |
| `source/Managers/` | Verified controller actions for combat, Adventure, inventory, loadouts, quests, Titans, Yggdrasil, diggers, and the Money Pit | Extensively modified manager layer built on NGUInjector |
| `source/AllocationProfiles/` | Energy, Magic, Resource 3, and timed rebirth allocation execution | Derived from and extended beyond NGUInjector |
| `source/Main.cs` | Unity lifecycle, synchronization gate, control-loop scheduling, and action logging | Modified NGUInjector host |
| `source/AutopilotLoader.cs` | Minimal public entry point called after injection | Autopilot integration code |
| `monitor/` | Separate macOS Swift status window and loopback dashboard bridge; both are read-only | Autopilot-specific code |
| `docs/` | Static jehlp.net dashboard client and strategy documentation | Autopilot-specific code |
| `build.command` | Compiles against the locally installed game's current managed assemblies | Local build tooling |
| `run.command`, `stop.command`, `status.command` | Injection lifecycle and operator status scripts | Local runtime tooling |

See [STRATEGY.md](STRATEGY.md) for a plain-language explanation of every major solver policy,
[ARCHITECTURE.md](ARCHITECTURE.md) for the execution boundary,
[docs/GUIDE-AUDIT.md](docs/GUIDE-AUDIT.md) for the full guide-to-policy audit, and
[THIRD_PARTY.md](THIRD_PARTY.md) for provenance.

## Bot versus injector

These are intentionally separate concepts:

- **The bot** is `NGUIdleAutopilot.dll`, built from `source/`. It contains the state model, strategy, optimizers, safety checks, and all game actions.
- **NGUInjector** is the inherited project substrate. Its allocation/profile framework, settings UI, and manager conventions were used as a base and have been substantially extended.
- **SharpMonoInjector** is only the transport used by `run.command` and `stop.command` to load or unload the bot DLL in the running Mono process. It does not decide how to play NGU Idle and is not linked into the bot's strategy.
- **The monitor** is a separate read-only Swift app. It consumes `runtime/decision.json` and `runtime/logs/actions.log`; closing it does not stop automation.
- **The dashboard** is the same static client on GitHub Pages and at `http://127.0.0.1:47635/`. A loopback Python bridge reads those telemetry files and exposes no control methods. The public page contains no save or history; it discovers a supervised Cloudflare Quick Tunnel that exposes the current read-only snapshot, while the loopback page remains the local fallback.

The repository does not redistribute NGU Idle's `Assembly-CSharp.dll` or Unity assemblies. `build.command` copies those locally as compile-time references from an installed game.

## Runtime flow

1. SharpMonoInjector calls `NGUAutopilot.Loader.Init`.
2. `AutopilotLoader.cs` creates the Unity host component.
3. `Main.cs` finds the live `Character` and waits for a verified, fully loaded gameplay state.
4. `AutopilotManager` snapshots the game, creates a stage plan, writes transparent telemetry, and installs the generated allocation profile.
5. Fast routines allocate resources and execute only through native game controllers.
6. The monitor renders confirmed actions, holds, ETAs, candidate rebirths, progression goals, and
   a separate sparse Key Events history for victories, significant level boundaries, discoveries,
   MAXX completions, EXP/AP purchases, and major rewards.
7. The loopback bridge serves the dashboard and a read-only snapshot API on `127.0.0.1`; the
   execution envelope exposes exact loaded-assembly binding coverage, and no game
   state is uploaded to GitHub Pages or another remote service.

## Dashboard

The public dashboard is published at [jehlp.net/ngu-idle-dashboard](https://jehlp.net/ngu-idle-dashboard/) from its independent [dashboard repository](https://github.com/JWKNT/ngu-idle-dashboard). This repository retains a local mirror because the loopback bridge must serve the identical client beside the running game. The dashboard follows the shared jehlp.net typography, color tokens, theme control, hairline structure, and content-first layout while presenting NGU-specific telemetry.

`run.command` starts the bridge with the bot. The first viewport reports the calculated rebirth ETA, next modeled boss and ETA, current Adventure route, and the exact named EXP purchase and shortfall. Deeper sections explain allocations, resource holds, combat, equipment, inventory safety, rebirth candidates, Basic Training, and confirmed key events. The API is intentionally read-only; the bridge remains loopback-bound and `monitor/run_dashboard_public_tunnel.command` supplies the restart-safe public HTTPS transport.

## Build

The checked-in `build.command` targets the author's macOS + CrossOver layout. It requires:

- NGU Idle installed in the CrossOver `Steam` bottle
- CrossOver's Wine/Mono compiler tools
- the current game's managed assemblies
- `swiftc` to build the optional macOS monitor

Run:

```sh
./build.command
```

The command obtains references from the installed game and produces `NGUIdleAutopilot.dll` plus the monitor application locally. Those artifacts remain ignored.

## Injection dependency

Place the SharpMonoInjector console files described in [`injector/README.md`](injector/README.md) under `injector/`, then start NGU Idle and run:

```sh
./run.command
```

Use `./stop.command` to request a clean unload. Because Mono can retain an ejected assembly, restart the game before injecting a newly compiled DLL.

The scripts contain explicit paths for the author's CrossOver installation and bottle. Adjust them for another environment before use.

## Configuration and safety

`autopilot.example.json` documents the operator-facing policy switches. On first launch the bot creates live files under ignored `runtime/` storage.

- `dry-run`: calculate and report without game mutations.
- `assist`: routine reversible automation.
- `full`: permits configured irreversible progression actions such as purchases and rebirths.

The automation hard-pauses until the game reports that autosave loading is complete and the main menu has been hidden. Irreversible actions also have independent configuration gates.

This is a progression optimizer, not a proof of the globally optimal infinite-horizon NGU Idle policy. Decisions are based on explicit finite-horizon models, game formulas, current inventory/state, and continuously refreshed event candidates. The monitor reports assumptions, holds, selected events, runner-up choices, and ETAs so decisions remain inspectable.

## License and game ownership

The modified NGUInjector-derived source remains under Apache-2.0; see [LICENSE](LICENSE) and [NOTICE](NOTICE). SharpMonoInjector is a separate MIT-licensed dependency and is not committed here. NGU Idle and its game data are owned by their respective rights holders and are not distributed by this repository.
