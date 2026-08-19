<!--
FILE PURPOSE AND MAINTENANCE CONTRACT

This is the durable handoff contract for human and model contributors to the
NGU Idle autopilot.  The project mutates a live Unity save through native game
controllers, so an edit that looks locally reasonable can lose permanent items,
desynchronize telemetry, or waste a long rebirth.  These instructions require
the source to explain its policy boundaries and verification obligations before
a future contributor changes executable behavior.
-->

# Required heavy block-commenting policy

Every maintained source file must begin with a prominent `FILE PURPOSE` block.
The block must explain:

1. **Purpose:** the game subsystem or repository responsibility owned here.
2. **Mechanism:** the important control/data flow and native controllers used.
3. **Inputs and outputs:** live game fields, config, files, logs, or mutations that
   cross the file boundary.
4. **Invariants and safety:** facts that must remain true to avoid lost progress,
   false telemetry, duplicate assembly execution, or invalid optimization.
5. **Extension points and non-goals:** where new policy belongs and what this file
   deliberately does not decide.

Mechanism-heavy files also require section blocks at high-risk logic: inventory
destruction/filtering, physical gear transactions, combat viability, resource
allocation, rebirth selection, synchronization, and telemetry emission. Comments
must explain the system around the code, not paraphrase individual statements.

Use `/* ... */` in C#/Swift, `#` blocks in shell/config, XML comments in XML/plist,
HTML comments in Markdown, and `_comment` only in JSON that the game/parser safely
accepts. Generated designer/resource files, vendored `SimpleJson.cs`, binaries,
runtime state, save backups, and exact license text are exempt; their role is
documented by the nearest maintained parent file.

Never shorten a purpose block merely to reduce line count. Update it in the same
patch whenever behavior changes. A stale authoritative comment is worse than a
missing one.

# Plain-language strategy contract

`STRATEGY.md` is the player-readable authority for what the solver values and why.
Update it in the same patch whenever a change materially alters stage goals,
resource/currency priorities, scoring or event selection, Adventure/ITOPOD/Titan/
rebirth behavior, inventory policy, execution authority, or an irreversible safety
rule. Do not churn it for renames, formatting, test-only changes, or refactors that
provably leave decisions unchanged. The explanation must remain understandable to
an educated middle-school reader and must distinguish modeled, executable, and
deployment-disabled behavior.

# Live-bot safety contract

- Use native NGU Idle controller calls and verify the resulting state delta.
- Treat drop filtering, trashing, consuming, rebirth, and challenge entry as
  irreversible. Fail closed when ownership or synchronization is uncertain.
- Never filter or trash an equipment ID until Item List MAXX is confirmed. While a
  zone set is incomplete, retain all gear sourced from that set even if one piece
  was individually MAXXED earlier.
- Telemetry describes confirmed game state, not intended or simulated actions.
- Rebuild, clean-restart the game process, inject once, and verify a new build ID
  before calling a live deployment complete.

# Verification

Run `./build.command` first, then `./test-mechanics.command`, `git diff --check`, and
`./commentary-audit.command` after changes. The aggregate's reflection suites intentionally inspect
the DLL built by the first step, so reversing or skipping that order can test a stale artifact. For
live behavior, verify `runtime/decision.json`, recent confirmed action
logs, and exactly one game/monitor process.
