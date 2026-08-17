using System;
using System.Collections.Generic;
using System.Linq;

/*
FILE PURPOSE

MajorUnlockPlanner identifies one-time mechanics whose acquisition dominates routine Adventure
farming: ordinary-zone progression items such as the Pissed Off Key/Wandoos, and early Titan
victories that open Yggdrasil, NGU, Diggers, or Beards. It may route slightly below conservative
continuous-farming thresholds when one recovered active kill has high permanent value.

Inputs are live unlock flags, Item List state, native zone reachability, Adventure stats, and
confirmed fight outcomes reported by CombatManager. Output is a read-only MajorUnlockTarget used
by Adventure routing, contextual gear scoring, and telemetry. This class never grants an item,
writes an unlock flag, consumes inventory, or bypasses native combat/drop controllers.

Safety invariant: an aggressive push still needs positive modeled damage and must survive the
target enemy's conservative first hit; static Power/Toughness guide thresholds are descriptive,
not a combat proof. Three consecutive deaths suspend the exact target until stats improve or a
short monotonic backoff expires. InventoryManager remains the sole native consumer of acquired
keys. Add future mechanics here only with an audited source zone/drop or native unlock condition.
*/
namespace NGUInjector.Managers
{
    internal sealed class MajorUnlockTarget
    {
        internal string Mechanic = string.Empty;
        internal string Goal = string.Empty;
        internal string Reason = string.Empty;
        internal int Zone = -1;
        internal int ItemId;
        internal int FightType = 2;
        internal bool BossOnly;
        internal bool GuaranteedFirstDrop;
        internal bool ValuesLoot;
        internal double DropChance;
        internal double MinimumPower;
        internal double MinimumToughness;
        internal int ConsecutiveFailures;
        internal int RetryEtaSeconds;

        internal ZoneTarget AsZoneTarget()
        {
            return new ZoneTarget {Zone = Zone, FightType = FightType};
        }
    }

    internal static class MajorUnlockPlanner
    {
        private sealed class FailureState
        {
            internal int Count;
            internal double SuppressedUntil;
            internal float AttackAtFailure;
            internal float DefenseAtFailure;
        }

        private static readonly Dictionary<int, FailureState> Failures = new Dictionary<int, FailureState>();
        private static int _lastTargetZone = -1;

        internal static MajorUnlockTarget Evaluate(Character c)
        {
            if (c == null || c.adventure == null || c.adventureController == null
                || c.inventory == null || c.inventory.itemList == null
                || !c.buttons.adventure.IsInteractable())
                return null;

            // A ready early Titan unlock is rarer than an ordinary-zone spawn. The
            // normal Titan selector already handles comfortably farmable versions;
            // this path admits a recoverable one-time push below that conservative bar.
            int titanIndex;
            var titanZone = ZoneHelpers.HighestMajorUnlockTitan(out titanIndex);
            if (titanZone >= 0)
            {
                var titan = TitanTarget(c, titanIndex, titanZone);
                if (CanAttempt(c, titan)) return Remember(titan);
            }

            // Sky's first Pissed Off Key is guaranteed by LootDrop.zone4Drop. The
            // static 600/400 manual threshold describes repeat farming, not the value
            // of one active kill followed by Safe-Zone recovery.
            if (!c.settings.itopodOn && !HasPhysicalItem(c, 172))
            {
                var first = c.inventory.itemList.itemDropped.Count <= 172
                            || !c.inventory.itemList.itemDropped[172];
                var key = OrdinaryTarget(c, 4, 172, "THE ITOPOD",
                    first ? "defeat a Sky boss for the guaranteed first Pissed Off Key" : "recover another Pissed Off Key from a Sky boss",
                    first, !first, first ? 1.0 : Math.Min(1.0, .01 * c.lootFactor()));
                if (key != null)
                {
                    key.BossOnly = true;
                    key.Reason += "; rerolling normal spawns because the native key branch is boss-only";
                }
                if (CanAttempt(c, key)) return Remember(key);
            }

            // Wandoos is a persistent mechanic rather than ordinary collection debt.
            // Its Sky drop is RNG-gated, so a stat-safe loadout may deliberately use
            // Drop Chance even when that set is weaker in raw Power/Toughness.
            if (c.settings.itopodOn
                && (c.adventure.highestItopodLevel > 1 || c.adventure.itopod.perkPoints > 0)
                && !c.settings.wandoos98On && !HasPhysicalItem(c, 66))
            {
                var wandoos = OrdinaryTarget(c, 4, 66, "Wandoos 98",
                    "obtain and install Wandoos 98 from a Sky boss", false, true,
                    Math.Min(1.0, .003 * c.lootFactor()));
                if (wandoos != null) wandoos.BossOnly = true;
                if (CanAttempt(c, wandoos)) return Remember(wandoos);
            }

            _lastTargetZone = -1;
            return null;
        }

        internal static void RecordFightResult(Character c, int zone, bool died)
        {
            if (zone < 0 || zone != _lastTargetZone) return;
            FailureState state;
            if (!Failures.TryGetValue(zone, out state))
            {
                state = new FailureState();
                Failures[zone] = state;
            }
            if (!died)
            {
                state.Count = 0;
                state.SuppressedUntil = 0;
                return;
            }
            state.Count++;
            state.AttackAtFailure = c.totalAdvAttack();
            state.DefenseAtFailure = c.totalAdvDefense();
            if (state.Count >= 3)
                state.SuppressedUntil = UnityEngine.Time.realtimeSinceStartup + 60.0;
        }

        private static MajorUnlockTarget OrdinaryTarget(Character c, int zone, int itemId,
            string mechanic, string goal, bool guaranteed, bool valuesLoot, double chance)
        {
            ZoneStats stats;
            if (ZoneStatHelper.UserOverrides == null
                || !ZoneStatHelper.UserOverrides.TryGetValue(zone, out stats))
                return null;
            return new MajorUnlockTarget
            {
                Mechanic = mechanic,
                Goal = goal,
                Zone = zone,
                ItemId = itemId,
                GuaranteedFirstDrop = guaranteed,
                ValuesLoot = valuesLoot,
                DropChance = chance,
                MinimumPower = stats.MPower * .70,
                MinimumToughness = stats.MToughness * .50,
                Reason = "Major unlock push: " + goal + " in " + stats.Name
                         + "; accepting Safe-Zone recovery between active fights"
            };
        }

        private static MajorUnlockTarget TitanTarget(Character c, int titanIndex, int zone)
        {
            var mechanic = titanIndex == 0 ? "NGU"
                : titanIndex == 1 ? "Yggdrasil"
                : titanIndex == 2 ? "Gold Diggers"
                : titanIndex == 3 ? "Beards" : "Titan progression";
            return new MajorUnlockTarget
            {
                Mechanic = mechanic,
                Goal = "defeat " + GameNames.Titan(c, titanIndex) + " to unlock " + mechanic,
                Zone = zone,
                BossOnly = true,
                MinimumPower = c.totalAdvAttack() * .95,
                MinimumToughness = c.totalAdvDefense() * .95,
                Reason = "Major unlock Titan push: recover and use active combat for " + mechanic
            };
        }

        private static bool CanAttempt(Character c, MajorUnlockTarget target)
        {
            if (target == null || target.Zone < 0
                || target.Zone > ZoneHelpers.GetMaxReachableZone(true))
                return false;
            if (!HasRecoverableCombatPath(c, target))
                return false;

            FailureState state;
            if (!Failures.TryGetValue(target.Zone, out state) || state.Count < 3)
                return true;
            var statsImproved = c.totalAdvAttack() >= state.AttackAtFailure * 1.05f
                                || c.totalAdvDefense() >= state.DefenseAtFailure * 1.05f;
            if (statsImproved)
            {
                state.Count = 0;
                state.SuppressedUntil = 0;
                return true;
            }
            target.ConsecutiveFailures = state.Count;
            target.RetryEtaSeconds = Math.Max(0,
                (int)Math.Ceiling(state.SuppressedUntil - UnityEngine.Time.realtimeSinceStartup));
            return target.RetryEtaSeconds <= 0;
        }

        /*
        MAJOR-UNLOCK COMBAT ADMISSION

        Guide Power/Toughness numbers describe comfortable farming, but the bot deliberately pushes
        a one-time mechanic with active skills and Safe-Zone recovery. Admission therefore uses the
        actual target enemy records: current active-attack damage must be positive and the strongest
        relevant boss's conservative first hit must not be lethal. Confirmed deaths still feed the
        monotonic retry/backoff policy above. This method is read-only and never spawns an enemy.
        */
        private static bool HasRecoverableCombatPath(Character c, MajorUnlockTarget target)
        {
            if (c.adventureController.enemyList == null
                || target.Zone < 0 || target.Zone >= c.adventureController.enemyList.Count
                || c.adventureController.enemyList[target.Zone] == null
                || c.adventureController.enemyList[target.Zone].Count == 0)
                return false;
            var all = c.adventureController.enemyList[target.Zone];
            var relevant = all.Where(x => x.enemyType == enemyType.boss
                || x.enemyType.ToString().IndexOf("bigBoss", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (relevant.Count == 0) relevant = all.ToList();
            return relevant.All(enemy =>
            {
                var outgoing = .8 * Math.Max(0.0, c.totalAdvAttack() - enemy.defense / 2.0)
                               * c.regAttackPower();
                var conservativeFirstHit = 1.2 * Math.Max(enemy.attack * .1,
                    enemy.attack - c.totalAdvDefense() / 2.0);
                return outgoing > 0.0 && conservativeFirstHit < c.totalAdvHP() * .95;
            });
        }

        private static MajorUnlockTarget Remember(MajorUnlockTarget target)
        {
            _lastTargetZone = target.Zone;
            FailureState state;
            if (Failures.TryGetValue(target.Zone, out state))
            {
                target.ConsecutiveFailures = state.Count;
                target.RetryEtaSeconds = Math.Max(0,
                    (int)Math.Ceiling(state.SuppressedUntil - UnityEngine.Time.realtimeSinceStartup));
            }
            return target;
        }

        private static bool HasPhysicalItem(Character c, int id)
        {
            return c.inventory.inventory.Any(x => x != null && x.id == id)
                   || c.inventory.head != null && c.inventory.head.id == id
                   || c.inventory.chest != null && c.inventory.chest.id == id
                   || c.inventory.legs != null && c.inventory.legs.id == id
                   || c.inventory.boots != null && c.inventory.boots.id == id
                   || c.inventory.weapon != null && c.inventory.weapon.id == id
                   || c.inventory.weapon2 != null && c.inventory.weapon2.id == id
                   || c.inventory.accs.Any(x => x != null && x.id == id);
        }
    }
}
