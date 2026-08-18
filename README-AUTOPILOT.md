<!--
FILE PURPOSE

This is the operator runbook for automation modes, strategy stages, safety rules, configuration,
and runtime artifacts. It describes supported behavior rather than implementing it. Whenever a
new irreversible policy or major subsystem is deployed, update this document with the same safety
boundary expressed in source.
-->

# NGU Idle Autopilot

This is a direct, in-process NGU Idle controller for the Windows Steam build running in the local CrossOver `Steam` bottle. It does not use mouse/keyboard automation or Computer Use. `SharpMonoInjector` loads the C# controller into Unity, where it reads the live `Character` state and calls the same game controllers used by the UI.

The planner adapts across Normal, Evil, and Sadistic. It models timed Energy/Magic/Resource 3 allocation profiles, bosses, reachable adventure zones, Basic/Advanced Training, augments, Time Machine, Blood Magic, NGUs, hacks, wishes, inventory, gear, diggers, Titans, Yggdrasil, quests, money pit/spin, cards, cooking, permanent purchases, perks, quirks, rebirths, challenges, difficulty transitions, and the END route. Modeling a route does not grant it mutation authority.

It is an adaptive strategy engine, not a proof of a mathematically global optimum. NGU Idle has long-horizon choices whose value depends on future play time and player goals; the bot uses progression-oriented policies derived from the game guide and current unlock/state information.

## Start safely

1. Launch NGU Idle through Steam in CrossOver.
2. Double-click `run.command`. It claims one exact game process and waits for matching deployment and decision telemetry before starting the monitor/dashboard.
3. The first run creates `runtime/autopilot.json` with `Enabled: true` and `Mode: "dry-run"`. Dry-run only reads state, calculates a plan, and writes `runtime/decision.json`; it does not operate the game.
4. Inspect the decision with `status.command` and the overlay in the upper-left of the game.
5. Change `Mode` to `"assist"` when the plan looks sensible. The file is hot-reloaded.

Use `stop.command` to unload. Restarting the game also unloads injected code. Do not launch the monitor or dashboard against retained runtime files by hand: their action views intentionally remain empty until `deployment.json`, `decision.json`, and the current `actions.log` session marker agree.

## Safety gates

- `dry-run`: observe and calculate only.
- `assist`: requests the verified-reversible authority stage. The deployment ceiling and typed root still decide which managers receive an executable child intent.
- `full`: requests enabled finite-resource managers, but it cannot override the compiled deployment ceiling or a missing native binding/proof.

The reconciled deployment currently opens one nonzero, epoch-fenced root per automation tick. Only the typed Card/Cooking/Yggdrasil/Quest integrations receive that root, and their own state/config gates still apply. Legacy allocation, Adventure, inventory, Titan, Blood, permanent-purchase, Money Pit, challenge, difficulty, rebirth, MOVE69, and END mutation paths are held. The global scheduler is telemetry-only `ShadowOnly` and has no execution path.

These configuration keys exist, but the current build normalizes every unproven irreversible route back to false even if a retained file says true:

- `AllowExpSpending`
- `AllowApSpending`
- `AllowRebirths`
- `AllowChallenges` (also requires rebirths)
- `AllowCardYeeting`
- `AllowPerkSpending`
- `AllowQuirkSpending`
- `AllowEndSequence` (requires all sixteen END pieces in their canonical slots; placement is transactional and defaults off)

The additional staged-authority fields for permanent purchases, Money Pit, difficulty, Titans 1–12, Titans 13–14, and MOVE69 are likewise hard false in this deployment. Treat `runtime/decision.json` → `authorityStage` and `stagedAuthority` as the effective authority, not the requested configuration.

Set `ExpReserve`, `PPReserve`, and `QPReserve` to protect currency. Keep Steam Cloud/save backups enabled, and test assist mode on a copied save before enabling rebirths or challenges.

## Strategy outline

- Normal pre-NGU: event-scored rebirths across exact Number breakpoints, projected boss kills, persistent Basic Training events, AP ticks, and Titan windows; Basic Training caps, boss EXP, best reachable gear, and only Augment/Time Machine work that pays before the selected reset.
- Resource decisions use separate current-run and persistent ledgers. Basic Training can reserve Energy for cap compression with a short multi-run payback; EXP/AP can wait for a higher-return permanent purchase instead of draining into a cheaper runner-up. Magic refill ROI is compared against permanent P/C/B gain per EXP, while Time Machine allocation requires a named Gold shortfall that can be spent before rebirth.
- Boosts go to active/explicit gear first, then the always-on Infinity Cube up to its native full-value softcaps, then speculative locked gear. The Cube is not an equippable item.
- Inventory cleanup runs proactively. The native trash recovery slot is intentionally rolling; only MAXXED same-ID dominated copies or no-special fixed armor with an all-future same-slot dominance proof are overwritten there.
- Adventure collection keeps a permanent MAXX-debt queue. It snipes the strongest fightable set first, then deliberately backtracks for older incomplete sets, known zone Bonus Accessories, and already-discovered equipment entries before falling through to optional ITOPOD farming. Inventory-space AP purchases move ahead of convenience upgrades when the live free-slot reserve is smaller than projected merge/drop pressure.
- Normal post-NGU: longer runs emphasizing Adventure/Drop NGUs, Yggdrasil, beards, PP and Titan gear.
- Early Evil: boss climb via TM then augments; later switch to Normal NGUs/Advanced Training and finish with Evil NGUs.
- Mature Evil: 24-hour beard cycles with Adventure NGUs, hacks, wishes and quests.
- Sadistic: event-scored MacGuffin cycles, Sadistic NGUs, milestone-efficient hacks/wishes, and Adventure/PP/QP cards. The terminal dependency graph separately schedules T12 versions, Wish 203, END Hack/Card/Blood, floor-1450 ITOPOD, T13/T14, exact item placement, and the opt-in final trigger.

The current decision and objective are always written to `runtime/decision.json`. Each active frame includes the producer session/build/game hashes, decision epoch, root transaction ID/state/counts, staged authority, and the global scheduler's shadow hashes/statistics/blocker. Missing ETAs and unknown evidence are unavailable—not zero, immediate, or high confidence. `Held`, `Pending`, and `Quarantined` are different transaction outcomes.

The read-only dashboard joins that frame to `runtime/deployment.json`. Its event and error tails include only lines after the exact matching `=== SESSION … id … ===` marker and stop at the next marker, so retained actions from an older injection cannot appear as current evidence. Rebirth, challenge, difficulty, and END horizons show unavailable whenever the producer did not emit a finite estimate. The generated allocation profile is `runtime/profiles/autopilot.generated.json`. Do not edit the generated profile; edit `runtime/autopilot.json`.

## Rebuild after a game update

Double-click `build.command`. It copies compile-time references from the installed game, compiles against the exact current `Assembly-CSharp.dll`, and replaces `NGUIdleAutopilot.dll`. Game/Unity assemblies are kept under the ignored `build/references` directory and are not required beside the finished bot at runtime.

## Credits

The injection and manager substrate is derived from rvazarkar's Apache-2.0-licensed NGUInjector 3.4.2. SharpMonoInjector is included as the process-injection component. See `LICENSE` and the upstream project history retained under `source`.
