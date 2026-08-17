<!--
FILE PURPOSE

This document records which Sayolove guide strategies were adopted, adapted, or rejected after
checking the game source and live optimizer goals. It prevents future contributors from treating
human chapter heuristics as exact formulas or unknowingly reintroducing already-audited shortcuts.
-->

# Sayolove guide strategy audit

The automation has been compared against the complete 52-page English tree at
<https://sayolove.github.io/ngu-guide/en/intro/>. The guide is a planning input,
not an authority: controller code and observed state transitions remain the source
of truth for formulas, unlocks, costs, and mutation verification.

## Guide ideas incorporated

- Yggdrasil seeds are reserved for dependency-ordered compounding breakpoints.
  Early Fruit of Gold/Pomegranate tiers are funded before lower-payback side
  purchases. Fruit eating, harvesting, and poop use change as the seed engine
  matures.
- Normal rebirths after Yggdrasil unlock align to the highest unlocked fruit's
  exact maturity boundary. The 24-hour post-T4 cycle is tied to beard conversion,
  mature fruit, and Titan events rather than presented as an unexplained timer.
- Permanent EXP unlocks use the guide's 10%-of-lifetime-EXP idea as an admission
  test. Native P/C/B bundles have no discount, so the bot buys the lagging 40-EXP
  minimum custom Cap, 15-EXP Power, or 80-EXP Bar purchase as soon as useful. It
  reserves only when a higher-value discrete unlock enters a short modeled funding
  window. Adventure/Fight-Boss stats may preempt generation only when the exact
  purchase crosses a verified zone or combat gate. QoL that duplicates the active
  bot (filtering, merging, loadouts, custom buttons, Auto Advance) is deferred;
  it becomes eligible only when its corresponding automation subsystem is disabled.
- The Money Pit preserves gold for its native cumulative one-time thresholds when
  the next threshold is reachable before rebirth.
- Gold production now has a shared finite-horizon ledger. Native net GPS projects
  the no-further-investment balance; exact active Augment charges, 50 Hz Blood
  ritual completions, and reachable permanent Pit/Digger steps form the committed
  spend. Time Machine Energy/Magic is admitted only for the modeled shortfall.
  An unlocked Gold sink or a permanent target already funded by baseline GPS is
  not, by itself, a reason to build reset-local Time Machine levels.
- Evil cards are restricted to Adventure/Hack/Wish; Sadistic PP/QP and NGU cards
  require the guide's useful tier breakpoints before consuming Mayo.
- Wishes whose best-case level time exceeds the game's known single-precision
  completion boundary are excluded and have resources reclaimed.

## Guide ideas already covered by stronger game-derived policies

- Basic Training allocation uses discrete native tick rates, unlock state, cap
  compression, and boss-time marginal value rather than equal shares or “move to
  the strongest attack.”
- Augments are selected by finishable marginal multiplier within the rebirth
  horizon rather than by a fixed chapter name.
- Inventory retention is stricter than chapter keep-lists: non-MAXXED progress,
  every special, puzzle/transform items, MacGuffins, and non-dominated physical
  copies are preserved.
- Adventure routing evaluates live reachability and set state; guide stat tables
  are conservative hints, not combat truth.
- The guide's "snipe ahead, backfill stronger" route is represented as explicit
  Item List debt: newest usable set first, then older sets and per-zone Bonus
  Accessories. Full inventory is lost-drop risk, so safe cleanup and repeatable AP
  inventory spaces receive dynamic pressure-based priority.
- Fight Boss readiness uses the game's discrete damage, regeneration, and death
  ordering rather than static recommended-stat tables.

## EXP purchase taxonomy

EXP is re-priced after every successful purchase; a round-number package is never
treated as a prerequisite. The live priority is:

1. **Exact progression gates.** Buy the smallest Adventure Power/Toughness atom
   that opens the selected zone, a Fight Boss percentage atom only when it changes
   a discrete loss into a short win, recovery Regen only when its measured time
   saving beats its funding delay, or one inventory slot when loot-loss risk is
   immediate.
2. **Energy generation.** Reach the native effective Energy-speed cap because it
   compounds nearly every early system and costs only tiny atomic purchases.
3. **Real permanent systems.** Admit Boost Recycling, Daycare, accessory slots,
   Digger/Beard slots, and MacGuffin slots only after their native feature is
   unlocked and their cost is no more than 10% of lifetime EXP. Rank admitted
   purchases by weighted cost and reserve only near the funding boundary.
4. **Magic generation and Yggdrasil.** Price the smallest number of 3-EXP Magic
   Speed atoms that crosses the next native discrete generation-rate breakpoint,
   then amortize its refill-only benefit over the selected run. It is purchased
   only when that logarithmic gain per EXP beats the best permanent P/C/B atom;
   a full Magic cap receives no fictitious steady-state speed benefit. Buy
   permanent fruit unlocks by activation value per EXP.
5. **Fallback convenience.** Loot Filter, Auto Merge, Inventory Merge, and Basic
   Training Auto Advance qualify only when their matching bot controller is off.
   Native loadout slots and custom-allocation buttons remain unnecessary because
   the bot directly owns those operations. Even eligible convenience waits only
   inside a short funding window and never displaces a concrete gate.
6. **P/C/B growth.** Spend the remainder on the currently lagging permanent
   Power/Cap/Bar dimension using executable native purchase sizes and the current
   difficulty's resource mix.

The old repeatable Boost Combine purchase is deliberately excluded: current save
migration code resets/refunds that retired field, so invoking its leftover private
method would not represent a supported shop choice.

## Heuristics intentionally not copied

- Fixed clock schedules such as “one hour TM, one hour AT, then NGUs” are not
  blindly applied. Reset-local work must finish or open a concrete gate before the
  selected rebirth.
- Static Adventure-stat purchases, fixed item loadouts, and fixed Titan stats are
  not used when a live marginal calculation or native controller check is
  available.
- Documented exploits and accidental ending triggers are not automated.
