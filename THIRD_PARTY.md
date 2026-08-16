# Third-party provenance

## NGUInjector

This project is derived from [rvazarkar/NGUInjector](https://github.com/rvazarkar/NGUInjector), version 3.4.2-era source, licensed under Apache License 2.0.

The inherited substrate includes the Unity host, settings/profile framework, allocation breakpoint structure, and portions of the original managers. This repository adds and substantially modifies automation synchronization, adaptive planning, telemetry, combat, inventory/loadout handling, purchases, progression systems, and safety verification. Modified source remains under the repository's Apache-2.0 license.

## SharpMonoInjector

[warbler/SharpMonoInjector](https://github.com/warbler/SharpMonoInjector) is an MIT-licensed external process-injection tool. Local runtime copies of its console executable/library are intentionally ignored and are not part of the bot source.

SharpMonoInjector's role ends after it asks Mono to invoke `NGUAutopilot.Loader.Init` (or `Unload`). It contains no NGU Idle strategy, optimization, or action logic.

## SimpleJson

`source/SimpleJson.cs` retains its embedded upstream copyright and license notice.

## NGU Idle and Unity

NGU Idle, `Assembly-CSharp.dll`, and Unity runtime assemblies are not included. They are used only as local compile-time/runtime dependencies obtained from the user's installed copy of the game.

