<!--
FILE PURPOSE

This is the plain-language strategy handbook for the NGU Idle autopilot. It explains what the
solver values, how its specialized planners cooperate, why it sometimes waits, which systems can
act in the live game, and which later-game paths remain deliberately blocked. Keep this document
in sync with any significant change to strategy, scoring, resource priority, execution authority,
or a safety rule. Small refactors that do not change a decision do not require an update.
-->

# Strategy handbook

## What the bot is trying to do

The bot's main goal is to finish NGU Idle as quickly as it safely can.

That does **not** mean “make the biggest number on the screen right now.” A smaller upgrade can be
better if it unlocks a useful feature sooner. Waiting can be better if a Titan is about to appear.
An Adventure item can be better than boss EXP if the item lets the bot climb ITOPOD and buy a strong
perk. A rebirth can be good even though it erases temporary work, but only when the permanent gain
repays what was lost.

The basic question behind most decisions is:

> Which safe action saves the most time on the path to the next important unlock and, eventually,
> the END?

The bot cannot answer that question perfectly for every system yet. It uses exact game formulas
where the code has them, measured estimates where enough evidence exists, and conservative rules
where two rewards cannot honestly be compared. It refuses risky actions when it cannot prove what
will happen.

## A short glossary

| Term | Plain meaning |
| --- | --- |
| Run | The time between two rebirths. |
| Rebirth | Banking permanent progress and starting a new run. |
| Reset-local | Progress that disappears at rebirth, such as Augment levels and most Advanced Training. |
| Permanent | Progress that survives rebirth, such as EXP purchases, perks, NGUs, hacks, and most item completion. |
| Gate | A requirement that opens the next useful action, zone, boss, Titan, or feature. |
| Event | A moment when a choice changes: a boss becomes killable, a fruit matures, a Titan becomes ready, or a bar finishes. |
| ETA | Estimated time until an event happens. |
| Native | The real game code or controller. The bot prefers asking the game to perform an action over editing saved fields. |
| Root | One protected one-second batch of bot actions. Every action in it must belong to the same game state. |
| MAXXED | The game has recorded an item at level 100 in the Item List and granted its permanent completion. |
| Shadow-only | Calculated and shown for study, but not allowed to control the game. |

## The most important truth about the solver

The bot is not one enormous perfect optimizer. It is a team of smaller solvers:

- a rebirth solver;
- an Energy, Magic, and Resource 3 allocator;
- a Fight Boss solver;
- an Adventure and collection planner;
- an ITOPOD and perk planner;
- a physical gear solver;
- an inventory manager;
- a Titan state machine;
- planners for EXP, Gold, Yggdrasil, quests, cards, cooking, and other systems.

The one-second controller gives those solvers a common snapshot and a safe order. A larger global
scheduler is being developed to compare every possible action directly in “seconds to the END,”
but it is currently shadow-only. Until it is complete, the live strategy combines exact local
comparisons with stage-specific priorities.

This matters when reading telemetry. “The model knows about this” does not always mean “the bot is
allowed to do this.” The live-authority table near the end of this document lists the difference.

## How one decision cycle works

About once per second, the bot does the following:

1. It proves that the game, save, injected build, and controllers are still the same ones it was
   watching a moment ago.
2. It takes a snapshot of resources, bosses, Adventure, inventory, timers, purchases, and the
   current run.
3. It chooses the current stage and the most useful near-term goals.
4. It opens one protected root transaction for that exact game state.
5. Specialized managers perform at most their allowed action inside that root.
6. Each action checks the real result. Spending 40 EXP is accepted only if EXP fell by exactly 40
   and the intended permanent stat rose by exactly the expected amount.
7. If an action fails safely, the bot restores the old state when possible. If the result is
   uncertain and cannot be undone, the affected action class is quarantined.
8. Only settled facts are written to the action log and decision telemetry.

A new save load, rebirth, challenge, difficulty change, config change, or game-process restart
cancels old plans. An old plan may never act on a new game state.

## How choices are compared

### Prefer lasting progress

The bot generally favors an improvement that keeps helping in future runs. Examples are a lower
Basic Training cap, an EXP purchase, a MAXXED item reward, a perk, a permanent NGU, a hack
milestone, or a new feature.

Temporary work is still valuable when it opens a gate before rebirth. Advanced Training that opens
a much better zone can be excellent. Advanced Training that finishes ten seconds before the run
ends is usually waste.

### Compare rates, not just reward sizes

A large reward that takes a week may lose to a smaller reward that takes ten minutes and makes the
next ten minutes faster. When compatible measurements exist, the bot estimates permanent gain per
second or seconds saved per currency point.

It often uses logarithms for multipliers. In plain language, doubling from 1 to 2 is treated like
doubling from 1,000 to 2,000. The percentage improvement matters more than the number of digits.

### Reconsider at events

The best choice usually changes when something finishes, not because an arbitrary clock minute
passed. Important events include:

- a Basic Training cap reduction;
- an Augment or Upgrade level;
- a boss becoming killable;
- a new Adventure zone becoming safe;
- a set item or Bonus Accessory dropping;
- an ITOPOD first-clear PP boundary;
- a Titan clock becoming ready;
- a fruit becoming mature;
- a Beard trim or MacGuffin bank point;
- enough currency arriving for a permanent purchase;
- the selected rebirth checkpoint becoming due.

Stage schedules such as one-hour or one-day runs are starting guesses. Event planners may shorten
or extend them when a nearby event is more valuable.

### Unknown is not zero

If a route has no trustworthy ETA, the bot does not pretend it is immediate. If two rewards use
incompatible units and neither closes a known gate, it does not invent a conversion rate merely to
force a choice. This makes some decisions conservative, but it prevents confident-looking nonsense.

## Resource allocation

The allocator handles Energy, Magic, and Resource 3 as complete physical vectors. It records every
place the resource was allocated before and after a pass, proves that the totals are conserved, and
rejects a layout that puts the right total in the wrong systems.

Priorities are ceilings and opportunities, not promises that every named system receives an equal
share. The allocator repeatedly funds the best useful breakpoint, then moves to the next one.

### Energy

Energy normally competes among these jobs:

1. **Basic Training:** fund exact native speed breakpoints and useful cap reductions. The bot does
   not split Energy equally and does not blindly overfill a capped row.
2. **Advanced Training:** buy only the minimum levels that open a named Adventure gate and leave
   enough time to use the new zone before rebirth.
3. **Time Machine:** fund it only when the resulting Gold can pay a named expense in this run.
4. **Augments:** choose an Augment/Upgrade completion whose combat improvement and useful time after
   completion repay its Energy and Gold.
5. **Wandoos:** build a run-local multiplier when enough time remains for it to matter.
6. **NGUs and Wishes:** build permanent progress when unlocked and selected by the current stage.

Basic Training also has a long-term cap-compression budget. The bot may spend Energy now when a
lower permanent cap is expected to repay its cost within roughly the next two runs.

After every useful finite target has declined more Energy, leftovers go to an unlocked no-cost NGU
or Wandoos target. If neither exists, telemetry names that missing game feature as the blocker.
The fallback never starts a paid Time Machine bar simply to make the idle number disappear.

### Magic

Magic normally competes among:

- Time Machine Gold levels with a real use before rebirth;
- Blood rituals that can finish before rebirth and can afford their start-up Gold;
- Wandoos;
- Magic NGUs;
- Wishes.

A Blood ritual charges Gold when a new bar starts. If the Gold is missing, Magic cannot honestly be
placed there. The allocator instead uses a no-cost NGU or Wandoos sink when one is available.

### Resource 3

Resource 3 mainly goes to permanent Hacks and Wishes. Hacks are valued at their exact milestone
breaks rather than by smooth average growth. Wishes receive a balanced Energy/Magic/R3 portfolio
because their formula rewards all three resources together and has strong diminishing returns.

After every selected cap is filled, remaining Resource 3 goes through a no-cost fallback. The bot
first tries an unlocked Hack below its native hard cap. If that controller accepts nothing, it may
use a valid Wish, preferring one that already has Energy and Magic. It trusts only the Resource 3
actually removed from the idle pool by the native controller. If neither system exists or accepts
the resource, telemetry names that exact missing sink instead of claiming the resource was placed.

### Refill speed

Energy and Magic refill in steps. A small Speed purchase can do nothing until it crosses the next
native refill breakpoint. The EXP buyer therefore values the next real breakpoint, not a smooth
percentage that the game does not award.

## Permanent currency spending

### EXP

The default long-term goal is a balanced set of Power, Cap, and Bars. Power makes each allocated
unit stronger. Cap supplies more units. Bars refill the cap faster. The slowest member of that team
usually has the best next purchase.

Important rules:

- Reach useful Energy and Magic Speed breakpoints first when their payback wins.
- Compare exact Power/Cap/Bar atoms; there is no bulk-purchase discount to exploit.
- Respect the configured EXP reserve.
- Direct Fight Boss Attack or Defense is allowed only when a finite new-record boss rollout saves
  more downstream time per EXP than the best permanent resource purchase. It is not a general
  shortcut for every difficult boss.
- Buy at most one permanent atom per root and verify its exact debit and effect.

### Perk Points and ITOPOD

ITOPOD is a permanent-growth engine, not an optional place to dump spare time.

Every floor needs ten kills before the live floor moves. A new record divisible by ten gives a
first-clear PP award. The award is not always one point:

- floors 10 through 90 give 1 PP at each ten-floor boundary;
- floor 100 gives 10 PP;
- floors 110 through 190 give 2 PP each;
- floor 200 gives 20 PP;
- this pattern continues through floor 1600.

The bot calculates the exact remaining kills from the current floor and kill counter. It then asks
what specific affordable perk that PP would buy. A first-clear push gets especially high value when
the award completes a strong perk gate.

The bot compares that value against ordinary zone collection when both sides have usable ETAs. It
chooses ITOPOD immediately when:

- the next reachable award completes the selected perk purchase;
- its proven permanent gain per second beats the selected collection reward;
- all fightable collection debt is complete; or
- a required END item can only come from ITOPOD.

The bot treats climbing and farming as different jobs. For steady farming, it chooses a floor that
even a minimum-damage Regular Attack can defeat in one hit, so PP, EXP, AP, and boosts arrive quickly.
This number is about kill speed, not health: being at full HP on floor 48 can mean the armor is very
safe even though each enemy still takes several attacks. Strong, Piercing, and Ultimate Attack can
one-shot higher floors, but their cooldowns mean they are not ready for every new enemy. The dashboard
therefore calls this the “repeatable farm” floor instead of implying it is the highest survivable floor.

For a record push, the bot aims directly at the next record divisible by ten, because that is where
the permanent first-clear PP is paid. No damage or survival formula is allowed to become a trial
ceiling. The formulas are still shown as useful estimates, but the bot keeps attempting the next
ten-floor reward until live combat supplies evidence that the attempt is not working.

An unlucky enemy—especially an explosive one—can kill a character that is normally strong enough,
so one death is not proof. The bot records the exact floor that was being fought. It also watches the
enemy's HP. There is no maximum fight length while that HP continues reaching new lows. If it does
not reach any new low for a full minute, the enemy is healing as fast as the current attacks can hurt
it, so that attempt counts as a failure even if the player stays at full HP. After eight failures on
the same floor without a kill there, the bot pauses the push and returns to its fast repeatable farm.
Death or abandoning a stuck enemy restarts the range, so wins on lower replay floors do not erase the
evidence for the difficult floor. A real kill on the difficult floor does erase it.

The paused push is tried again after effective offense or durability improves by at least five
percent. “Improves” means the resulting Adventure combat stats, not merely a new piece of gear:
gear levels, Power/Toughness boosts, the Cube, EXP purchases, perks, fruit, Advanced Training,
Titans, challenge rewards, and any other system can all reopen it. This creates a simple loop:
push for the next ten-floor reward, learn from repeated defeats, farm efficiently, then retry after
the character has materially grown.

The end of an ITOPOD range is a sentinel: the record is awarded when the floor moves, before that
sentinel must be fought. The live gear check therefore proves the hardest floor that will actually
be fought, which is one below the range end. The bot disables Lazy ITOPOD when it would overwrite an
intentional range.

Once a climb or farm begins, it stays in ITOPOD. It does not hop back to Safe Zone every time
Charge or Parry becomes ready: re-entering at the saved range start resets the current ten-kill
floor counter. Low health also cannot trigger a voluntary mid-floor exit. At the enemy-free boundary
between floors the combat controller may use Heal or Hyper Regen in place; otherwise ordinary
in-fight moves keep the climb moving. Only an actual defeat may force Safe Zone recovery. A
voluntary mid-floor Safe Zone visit is forbidden because it would repeatedly erase partial progress.

Perks are bought one level at a time. The live buyer currently owns the source-audited early Normal
sequence—one level in Newbie perks 0 through 4, two Instant Advanced Training levels, then balanced
Energy Power/Cap before T4—and the terminal perk-item delivery. After that sequence it saves PP.
The broader typed selector exists, but it does not receive live downstream-seconds quotes yet, so
the bot will not spend permanent PP using tooltip names or guessed weights. The configured PP
reserve is never crossed.

### AP and QP

The repository contains source-exact planners and native purchase proof for a conservative set of
permanent AP upgrades and one-level QP quirks. Live automatic spending is currently disabled. The
missing piece is a trusted producer that quotes the downstream seconds saved by each exact purchase.
The bot will not replace that evidence with name-based guesses such as “anything with Adventure in
the title is worth 10 points.” END quirk 176 has an additional safety boundary: buying the quirk
does not immediately create item 486. The game checks later, so the bot holds that purchase until a
future two-phase owner can preserve the item filter and a real inventory slot across the delay (or
item 486 is already ordinary inventory).

### Gold

Gold is treated like a dated budget. The planner accounts for when Gold arrives and when it will be
charged by:

- Augments and Upgrades;
- Blood rituals;
- Time Machine levels;
- active Diggers and permanent Digger upgrades;
- Money Pit tosses;
- other named progression expenses.

An action that can be afforded eventually may still be wrong if it steals Gold from an earlier,
more important charge. Money Pit may toss only when both its manager and separate irreversible
permission are enabled, the chronological Gold ledger leaves no earlier named expense unfunded,
and the live action can prove that it consumed exactly the whole Gold pool and delivered the exact
native reward tier. Daily Spin separately proves the exact free-spin/timer debit, one total-spin
increment, and advancement of the save's reward random state. If either native call throws after
dispatch, complete copied state may still prove a commit; a partial debit is quarantined.

## Bosses, Adventure, and gear

### Fight Boss

The boss solver models the game's tick order, damage, defense, regeneration, current HP, and future
Training/Augment events. It starts a fight only when the selected route has a proven win or a
source-backed exact-viability plan.

Boss sniping is useful when the next boss gives important EXP, a record, an unlock, or a required
item. It is not automatically better than Adventure farming.

### Adventure route order

Adventure routing first checks for hard obligations:

1. a quest that owns the current zone;
2. a one-time major unlock or puzzle step;
3. a due executable Titan commitment;
4. the best-valued choice among progression push, boss-only gear sniping, full-zone collection,
   backfill, and ITOPOD.

The collection planner tracks permanent Item List debt, but collection debt is not automatically a
farming obligation. An unfinished core set, an unclaimed useful set reward, ordinary-enemy boosts
for proven better gear, or a completed item that improves a real loadout may own Adventure. A rare
optional item stays protected in inventory without taking combat time when even its MAXXED,
fully-boosted version would not help the current route. For example, an unfinished Looty entry does
not justify farming Sky merely because Looty can be completed; its real maximum value must beat the
ITOPOD route the bot would give up.

Boss-only mode is used when only a boss drop, important EXP, or a named unlock matters. Full-zone
farming is used when ordinary enemies provide needed gear or boosts. Old zones are revisited only
as explicit valuable backfill, not because every missing Item List entry deserves immediate time.

### Combat gear versus production gear

The gear solver chooses physical item objects, not just item IDs. Two copies with the same name can
have different levels or obligations.

For a push, boss, ITOPOD climb, major unlock, or Titan, it equips the strongest legal combat set for
that exact target. For an easy farm, it may use weaker gear with better Gold, Energy, Magic, drop,
or other production bonuses—but only when the target remains safely beatable.

An ITOPOD climb never breaks an equal-kill-time tie with production bonuses. It prefers Adventure
Attack first, because more Attack is what breaks an enemy-healing wall, then uses survival and other
combat stats as tie-breakers. Thus a currently stronger Gouda chest can replace Forest armor for a
climb. A one-time gear-swap frame is not charged as though it recurred on every enemy in the range;
the Forest chest remains available for refill work.

When moving from an ordinary zone into ITOPOD, the bot deliberately visits Safe Zone for one
control step. That creates a dependable enemy-free moment to equip the chosen ITOPOD set; it does
not wait and hope that a once-per-second controller notices the tiny gap between enemy spawns.
Before routing, the reach estimate checks several complete physical sets ranging from Attack-heavy
to Defense-heavy. The full gear solver then searches the legal combinations for the chosen floor.
If that bounded search runs out of time, its narrow emergency fallback uses the strongest
Adventure-Attack set. A steady farm must still pass the repeatable one-hit check, while a record
climb is judged by the live failure circuit breaker instead of a formula. No step combines stats
from gear that cannot be equipped together. Both the route estimate and the gear check use the Beast Mode
state that will actually be enabled in ITOPOD, even though gear staging itself happens one step
earlier in Safe Zone.

Manual versus Idle combat is also one decision, not three disagreeing guesses. When ITOPOD is set
to Manual and Regular Attack is unlocked, both record climbs and steady farms use Manual combat;
the reach estimate and gear proof use that same attack. Otherwise, all three use Idle attack.

The solver searches weapons, armor, and accessory combinations and reports whether it proved the
best answer or merely found the best answer within its search budget. Hard progression fights use
combat survival and kill time; unrelated production stats do not make a weak combat set look strong.

### Titans

Titan clocks are valuable because a rebirth resets every Titan timer. The bot tries to collect a
due, safely executable Titan before resetting.

For Titans 1 through 12, the typed controller:

1. records the exact desired version and Bestiary kill count;
2. disables background native autokill;
3. stages the strongest exact physical loadout;
4. checks the real native kill predicate after staging;
5. calls at most one native Titan frame;
6. proves exactly one intended kill and clock reset;
7. restores the exact old loadout and autokill preference.

If the strongest set still cannot kill the Titan, the bot restores the old gear, records the lost
clock as an explicit rebirth cost, and releases the reset interlock. It does not retry forever and
strand the run.

T13 and T14 are modeled and have guarded staging support, but live execution is disabled until all
terminal combat dependencies are proven.

## Inventory strategy

Inventory mistakes can permanently destroy progress, so the rules are strict.

- Never filter or trash an equipment ID before its Item List entry is MAXXED.
- While a zone set is incomplete, keep gear from that set even if one member was MAXXED earlier.
- Merge same-ID copies into the retained development copy when the exact references and levels are
  known.
- Consume Power, Toughness, and Special boosts immediately into the gear that best serves the
  current combat objective. The comparison includes both the next boost's immediate complete-
  loadout gain and the item's level-100, fully-boosted potential per remaining compatible boost
  point. During a Boss push, ITOPOD climb, Titan, or major unlock, production-only bonuses cannot
  make weaker combat gear win this development score. Route boosts with no useful gear target to
  the Infinity Cube according to its native softcaps.
- Keep MAXXED lower-tier gear when its special bonuses are still useful for production loadouts.
- Replace or remove dominated ordinary combat gear only when future set and reference obligations
  are satisfied.
- Keep exactly the number of Sticks required by active loadouts and the unfinished clue (normally
  one). Once that copy is secured, filter new Stick drops and reclaim a weaker duplicate; do not
  lock the retained copy so tightly that the clue cannot equip it.
- Consume permanent progression items such as Wandoos disks and the Giant Seed only when their exact
  source-specific permanent state change can be proven.
- Perform known item transformations only with their exact ID, difficulty, level, and ownership
  requirements.

The rolling trash-recovery slot is not treated as permanent storage. Destructive cleanup remains
more conservative than ordinary merging and boost use.

## Rebirth strategy

### Choosing a checkpoint

Early Normal rebirth candidates come from real game events, including:

- the minimum legal rebirth time;
- Number multiplier changes;
- a projected boss victory and its EXP;
- a persistent Basic Training cap reduction;
- an Augment completion;
- an AP tick;
- a Titan or first-GRB window;
- a nearby planned checkpoint.

The solver scores lasting percentage growth and useful permanent rewards per hour, subtracting the
cost of rebuilding the route. It keeps a nearly tied current choice sticky so tiny estimate changes
do not make the target jump every second.

When a positive checkpoint becomes due, it is latched to that run. Normal replanning cannot quietly
move it into the future. The bot must either execute that exact checkpoint or cancel it for a named,
freshly proven reason.

Later stages start from fruit, Beard, MacGuffin, Titan, and daily event boundaries. The event-driven
checkpoint planner may replace a simple one-day guess when another live event has better value.

### Final reset checks

Immediately before rebirth, the bot checks again that:

- the same run and checkpoint are still active;
- the native Number preview is valid;
- boss-record recovery policy allows the reset;
- no executable Titan or active Titan cleanup is being lost;
- no protected fight, quest, challenge, fruit, or progression transition owns the boundary;
- the remaining managed Blood can be settled;
- the game is still synchronized.

The bot then spends the exact remaining Blood pool on Blood NUMBER, checks the debit and permanent
Number effect, refreshes the native preview, and performs the reset. Any uncertainty stops the reset.

### MacGuffins, Beards, and long runs

MacGuffins bank their equipped effects at rebirth. Very short runs can give no bank at all. Beards
bank only active Beards at trim/rebirth and have important hour/day breakpoints. Yggdrasil fruits
lose incomplete run progress when reset.

This is why later runs often gather events near a common boundary: mature fruit, a Beard bank, due
Titans, and MacGuffin value. “Always rebirth at 24 hours” is a useful starting idea, not an absolute
rule.

## Other persistent systems

### Wandoos

Wandoos levels reset at rebirth. Changing the operating system also erases both current Wandoos
level bars immediately. The bot projects the final multiplier under every installed OS and switches
only when the winner is at least about 10% better after repaying the lost progress. It never switches
in the final minute of a run. Installation disks are handled separately as permanent consumables.

### Augments and Time Machine

Both are reset-local. The bot values completed useful work, not sunk progress. A nearly finished bar
is attractive because its remaining time is short; it does not receive an extra made-up bonus merely
for being partly filled. Time Machine work must produce spendable Gold before reset.

### Blood Magic

Rituals create Blood during the run. Spells spend the pool. Blood NUMBER is tied to the exact rebirth
boundary so the pool cannot block reset forever. The live permanent-spell manager may cast one
source-ready Iron Pill, MacGuffin alpha, or MacGuffin beta. It spends the complete pool only when
the permanent effect, cooldown reset, and physical MacGuffin identity/level changes can all be
proved. It waits to grow a stronger one-cast pool unless the cooldown can repay before rebirth or
the run is at its final boundary.

In Sadistic, a missing END Blood item reserves every drop of Blood ahead of repeatable spells. Once
the exact 5e22 cost and an ordinary inventory slot are available, a separate typed delivery casts
item 494 before rebirth and accepts it only after the pool is zero and a new level-100 physical item
exists. Loot Spaghetti and Counterfeit Gold are not cast automatically yet: they are run-local and
need a named route-value proof. Equal automatic splitting is never assumed optimal.

### Yggdrasil

The bot tracks exact fruit maturity, tier, Poop use, activation, harvest, and seed purchases. A
non-permanent fruit is not activated when it cannot reach tier one before a zero-factor reset. Seed
purchases use the real fruit-specific native reward preview instead of assuming every fruit scales
the same way.

### Beards, Daycare, and Diggers

These managers run as separate protected actions:

- Beards choose useful active effects and preserve their banking rules.
- Daycare places eligible development items without stealing protected inventory objects.
- Diggers choose a useful set, then consider one upgrade on a later plan after a set change settles.

They cannot bypass Gold, slot, equipment, or transaction ownership rules.

### Quests

Major quests are never skipped. Butter is spent only after the quest is ready to hand in. Minor
quests can be rerolled for the free-minor Item List campaign when the exact policy allows it. Quest
items and special Antlers delivery use ordinary-inventory capacity proofs.

### NGUs, Hacks, and Wishes

- NGUs are permanent and use stage-specific target lists. The current live planner still relies on
  some fixed target IDs; replacing every list with exact downstream marginal value is future work.
- Hacks are ranked by exact next milestones and completion chunks.
- Wishes are ranked using their current level, partial progress, and three-resource formula. Joint
  allocation is preferred because concentrating one resource alone has strong diminishing returns.

### Cards and Cooking

Cards protect END pieces first, reserve Mayo needed for terminal delivery, keep enough deck space,
and cast or recycle through typed finite-resource actions. The card value model is still partly
heuristic and is not claimed to be a full time-to-END solution.

Cooking searches legal ingredient pairs and levels using the native score rules. It does not equip
gear on its own and does not use the early-ending exploit automatically.

## Progression stages

### Early Normal

The focus is Basic Training cap reduction, boss EXP, permanent EXP purchases, the best reachable
gear, Wandoos installation, ITOPOD perk gates, and the first Titan. Rebirths are event-scored rather
than fixed at exactly 30 or 60 minutes.

### Normal after NGUs

Runs become longer. Adventure and Drop Chance NGUs, Yggdrasil, Titan sets, Beards, PP, and full
collection matter more. The planner often groups fruit, Titan, Beard, and Number events.

### Evil

Early Evil climbs bosses with Time Machine and Augments, then shifts toward permanent NGUs and
Hacks. T7, T8, and T9 clocks can own a reset boundary. Once Wishes unlock, Energy, Magic, and
Resource 3 are balanced across Wishes, NGUs, Hacks, Adventure Training, and the remaining run-local
systems.

### Sadistic and the END route

MacGuffins, Sadistic NGUs, Adventure cards, PP/QP, Wishes, and milestone-efficient Hacks dominate.
The dependency model tracks the sixteen terminal pieces and their sources, including T12 versions,
ITOPOD, perks, quirks, Wishes, Blood, Hacks, Cards, later Titans, and final item placement.

Much of this route is currently planning-only. The bot does not fire the final END merely because a
model says it is ready.

## Challenges and difficulty changes

Challenge mechanics include exact reset effects, completion targets, Titan-clock losses, special
rules, and keyed timing evidence. The repository has typed support for a conservative first wave of
Normal challenges and Normal-to-Evil.

Basic Challenge disables no systems and allows ordinary rebirths. More generally, only the
No-Rebirth Challenge mechanically forbids an ordinary rebirth. No-Augments, 24-Hour, 100-Level,
No-Equipment, Troll, Laser Sword, Blind, No-NGU, and No-Time-Machine still allow rebirths while
applying their named restrictions. The planner keeps this game rule separate from a strategic
hold: for example, it may postpone a legal Troll or Laser rebirth to avoid wasting useful progress.
During an active Basic Challenge it continues using the ordinary event-scored rebirth optimizer.

Every challenge entry requires admission-grade timing and same-state opportunity evidence. Merely
having reached its target boss before proves that the target is possible; it does **not** prove that
the reset costs zero time. For the first Normal Basic Challenge, the reward model includes the
source-guaranteed EXP/AP plus the permanent +10% Boost recycling and +10% Adventure-stat rewards.
The cost model includes a pessimistic challenge clear, recovery to the current boss and both Number
multipliers, the ordinary rebirth opportunity being given up, and every Titan-clock delay. The
challenge may start only when that complete upper-bound cost is strictly better than continuing in
the same captured state. If any time bound is missing, it waits.

When challenge authority is explicitly enabled, the typed reset controller can enter that Basic
route and the other audited unrestricted Normal routes only after their own evidence and fresh
Titan, fruit, Blood, native-binding, root, and exact-postcondition checks pass. A selected challenge
cannot silently turn into an ordinary rebirth if its preparation changes state. Special challenges,
Normal-to-Evil, Evil-to-Sadistic, and puzzle-heavy transitions remain planning-only or separately
disabled as described below.

A Titan clock that merely says “ready” is not an automatic challenge veto. The typed Titan child
first tries the strongest legal gear. If the Titan is actually killable or already active, the reset
waits and collects it. If the strongest proof says it cannot be killed, the challenge score keeps
the full lost-clock cost but may proceed instead of retrying forever.

## What can act today

“Supported” below means the source has a typed live path. The operator's config must still enable
it, the installed-build bindings must match, and every fresh precondition must pass.

| System | Current source status |
| --- | --- |
| Energy, Magic, and Resource 3 allocation | Live, with full-vector proof, native rollback, and no-cost residual fallbacks; a genuinely missing/rejecting sink is reported. |
| Basic/Advanced Training, Augments, Time Machine, NGUs, Hacks, Wishes, rituals, Wandoos allocation | Live through the allocation plan when unlocked and selected. |
| Fight Boss and ordinary Adventure routing | Live. |
| Inventory merging, boosts, safe cleanup, transforms, permanent consumables | Live. |
| ITOPOD range/route planning and PP perk purchases | ITOPOD routing is live. PP purchases are live only for the audited early sequence and terminal delivery; later PP is saved pending exact downstream-time quotes. |
| EXP purchases | Live for the build-pinned exact subset when enabled. |
| Titans 1–12 | Live-capable when explicitly enabled. |
| Yggdrasil, quests, cards, cooking, Beards, Daycare, Diggers, Daily Spin | Live through typed managers. |
| Iron Pill, Blood MacGuffin alpha/beta, END Blood item 494, and rebirth-boundary Blood NUMBER | Live through separate typed full-pool intents when their exact gates pass. Loot/Gold Blood spells remain held. |
| AP spending and QP quirks | Source-ready, but deployment-disabled until exact downstream quotes exist. |
| Money Pit | Live when both explicit flags are enabled and the exact Gold/delivery proof passes. |
| Audited Normal challenge entry | Typed execution is live-capable when explicitly enabled, but every route—including the first Basic—holds until a fresh finite same-state clear/recovery/opportunity proof exists. |
| Normal-to-Evil | Source-ready, but deployment-disabled pending explicit authority and route evidence. |
| T13/T14, MOVE69, Evil-to-Sadistic, special challenges, final END | Modeled/guarded; live authority disabled. |
| Global time-to-END scheduler | Shadow-only. It cannot control the game. |

Dry-run never mutates. Assist does not gain finite-resource or irreversible authority. Full mode can
use only the actions allowed by both configuration and the compiled deployment ceiling.

## How to read a surprising decision

When the bot appears to make a strange choice, check these questions in order:

1. **What stage and objective are active?** A collection backfill and a progression push use
   different gear and Adventure rules.
2. **What exact event is next?** A Titan, fruit, perk boundary, or latched rebirth may be closer than
   it looks.
3. **Is the route executable or only modeled?** Look at staged authority and the live-status table.
4. **Is an ETA unknown?** Unknown evidence often causes a conservative hold.
5. **Is the resource truly idle?** A tiny amount labeled `between-allocation-sweeps` was generated
   after the last proven vector and will be placed on the next pass.
6. **Is a safety obligation active?** Inventory capacity, a non-MAXXED set, a staged Titan loadout,
   Blood, a quest, or an epoch change can temporarily own the decision.
7. **Did the action settle?** Trust confirmed action logs and closed-root telemetry, not a planned
   target by itself.

## Known limits of the strategy

- The global scheduler is not yet the live authority, so some cross-system choices remain
  stage-specific rather than truly global.
- Some NGU and Card values still use fixed schedules or local weights.
- Later PP choices, AP/QP, later challenge timing, late Titans, difficulty completion, MOVE69, and
  END need more live evidence before they are enabled. Money Pit and the audited Normal challenge
  executor have narrow typed authority; that is not blanket permission without a fresh route proof.
- ITOPOD climb learning is session-local. Restarting the injected bot clears its remembered failed
  floor, so the new session may spend up to eight fresh attempts proving that floor again.
- Static zone thresholds are routing hints; exact combat simulation owns important fights.
- No strategy can promise a mathematically perfect speedrun without a complete future-state model,
  including random drops and every later decision.

These are honesty boundaries, not invitations to bypass safety checks.

## Maintenance rule for future changes

Update this document in the **same change** whenever code changes any of the following:

- the main progression objective or stage plan;
- the order in which systems compete for Energy, Magic, Resource 3, Gold, EXP, AP, PP, or QP;
- a score, formula, threshold, reserve, or event used to choose between strategies;
- Adventure, ITOPOD, boss, gear, collection, Titan, or rebirth route selection;
- what an inventory item may be merged into, consumed, protected, transformed, filtered, or trashed;
- which systems are live, shadow-only, or deployment-disabled;
- an irreversible-action precondition or safety guarantee;
- a new major game system or terminal dependency.

An update is usually **not** needed for a rename, formatting change, test-only refactor, logging text
cleanup, or performance improvement that provably leaves every strategy decision unchanged.

When updating, explain the behavior in player terms first. Mention class or method names only when
they help a maintainer find the implementation. If a new policy cannot be explained clearly here,
it is probably not ready to control a live save.
