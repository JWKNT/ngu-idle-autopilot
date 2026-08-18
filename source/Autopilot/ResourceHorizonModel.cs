using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Managers;

/*
FILE PURPOSE

ResourceHorizonModel is the single read-only Gold ledger for Augment/Time-Machine working capital,
valuable Blood ritual completions, permanent Money Pit tiers, and permanent Digger upgrades. It
projects native net GPS to a concrete rebirth checkpoint and exposes ordered claims so each spender
can protect higher-priority demand without inventing its own reserve. Reset-local production is
valuable only when a named, reachable sink has a modeled shortfall.

Blood demand is inherited from an actual cast target: END item 494, a ready permanent MacGuffin
spell, or Iron Pill. A rolling safety hold is not treated as an infinite/one-hour ritual farm; only
the already-nearest completion of each admitted ritual may create a charge during an unscheduled
hold. The model never allocates, buys, casts, tosses, or mutates controller state.
*/
namespace NGUInjector.Autopilot
{
    internal enum GoldClaimKind
    {
        AugmentAndTimeMachine = 0,
        BloodSpell = 1,
        MoneyPitPermanentTier = 2,
        DiggerPermanentUpgrade = 3
    }

    internal sealed class GoldClaim
    {
        internal GoldClaimKind Kind;
        internal double Amount;
        internal string Label;
        internal bool Hard;
    }

    internal sealed class GoldHorizonEvaluation
    {
        internal double BaselineAtRebirth;
        internal double CommittedSpend;
        internal double Shortfall;
        internal double BloodSpend;
        internal double AugmentSpend;
        internal double PermanentSpend;
        internal double PitSpend;
        internal double DiggerSpend;
        internal double NextGoldLevelIncrement;
        internal double NextSpeedLevelIncrement;
        internal string TargetName = "no validated pre-rebirth Gold sink";
        internal string Decision = "Gold horizon has not been evaluated";
        internal bool TimeMachineUseful;
        internal readonly List<GoldClaim> Claims = new List<GoldClaim>();

        internal double ProtectedSpendBefore(GoldClaimKind actor)
        {
            return Claims.Where(x => x.Amount > 0 && (x.Hard || x.Kind < actor) && x.Kind != actor)
                .Sum(x => x.Amount);
        }
    }

    internal static class ResourceHorizonModel
    {
        private static readonly int EndBloodItemId = MechanicsEndgame.AllRequirements()
            .First(x => x.DependencyKind == EndDependencyKind.BloodSpell).ItemId;

        private sealed class RitualEvent
        {
            internal double Seconds;
            internal double Blood;
            internal double Gold;
        }

        internal static GoldHorizonEvaluation EvaluateGold(Character c, int remainingSeconds)
        {
            var result = new GoldHorizonEvaluation();
            if (c == null || remainingSeconds <= 0)
            {
                result.Decision = "Blocked: the selected rebirth checkpoint has arrived";
                return result;
            }

            result.BaselineAtRebirth = Math.Max(0.0, c.realGold)
                                       + Math.Max(0.0, c.goldPerSecond()) * remainingSeconds;
            result.AugmentSpend = AugmentAndMachineWorkingCapital(c);
            AddClaim(result, GoldClaimKind.AugmentAndTimeMachine, result.AugmentSpend,
                "active Augment/Upgrade/Time Machine charge", true);

            string bloodTarget;
            result.BloodSpend = ProjectValuedBloodCharges(c, remainingSeconds, out bloodTarget);
            AddClaim(result, GoldClaimKind.BloodSpell, result.BloodSpend, bloodTarget, true);

            var optimisticReach = result.BaselineAtRebirth
                                  + Math.Max(0.0, c.grossGoldPerSecond()) * remainingSeconds;
            string pitLabel;
            result.PitSpend = ReachablePitStep(c, remainingSeconds, optimisticReach, out pitLabel);
            AddClaim(result, GoldClaimKind.MoneyPitPermanentTier, result.PitSpend, pitLabel, false);

            result.DiggerSpend = ReachableDiggerStep(c, optimisticReach);
            AddClaim(result, GoldClaimKind.DiggerPermanentUpgrade, result.DiggerSpend,
                "next permanent Digger max-level upgrade", false);

            // Pit and Digger are alternative permanent uses of the same marginal Gold. Reserve the
            // cheaper reachable option for Time-Machine valuation instead of summing both as if a
            // single coin could fund them simultaneously.
            var optional = new[] {result.PitSpend, result.DiggerSpend}.Where(x => x > 0).ToArray();
            result.PermanentSpend = optional.Length == 0 ? 0.0 : optional.Min();
            result.CommittedSpend = result.AugmentSpend + result.BloodSpend + result.PermanentSpend;
            result.Shortfall = Math.Max(0.0, result.CommittedSpend - result.BaselineAtRebirth);

            var target = result.Claims.Where(x => x.Amount > 0)
                .OrderBy(x => x.Kind).FirstOrDefault();
            if (target != null) result.TargetName = target.Label;

            var gross = Math.Max(0.0, c.grossGoldPerSecond());
            result.NextGoldLevelIncrement = gross / Math.Max(1.0, c.machine.levelGoldMulti + 1.0);
            var speedLevel = c.machine.levelSpeed;
            var currentSpeed = TimeMachineSpeedFactor(speedLevel);
            var nextSpeed = TimeMachineSpeedFactor(speedLevel + 1);
            result.NextSpeedLevelIncrement = currentSpeed <= 0 ? 0.0
                : gross * Math.Max(0.0, nextSpeed / currentSpeed - 1.0);
            var recoverable = Math.Max(result.NextGoldLevelIncrement,
                result.NextSpeedLevelIncrement) * remainingSeconds;
            result.TimeMachineUseful = result.Shortfall > 0 && result.Shortfall <= recoverable;

            if (result.CommittedSpend <= 0)
                result.Decision = "Blocked: no named Gold sink can complete before rebirth";
            else if (result.Shortfall <= 0)
                result.Decision = "Blocked: baseline Gold already funds " + result.TargetName;
            else if (!result.TimeMachineUseful)
                result.Decision = "Blocked: " + result.TargetName + " is short by "
                                  + FormatGold(result.Shortfall) + " beyond conservative recovery";
            else
                result.Decision = "Allowed: " + result.TargetName + " has a modeled shortfall of "
                                  + FormatGold(result.Shortfall);
            return result;
        }

        private static void AddClaim(GoldHorizonEvaluation result, GoldClaimKind kind,
            double amount, string label, bool hard)
        {
            if (amount <= 0) return;
            result.Claims.Add(new GoldClaim
            {
                Kind = kind,
                Amount = amount,
                Label = string.IsNullOrEmpty(label) ? kind.ToString() : label,
                Hard = hard
            });
        }

        private static double AugmentAndMachineWorkingCapital(Character c)
        {
            var reserve = AutopilotManager.RequiredAugmentWorkingCapital(c);
            if (c.machine == null || c.timeMachineController == null) return reserve;
            if (c.machine.speedEnergy > 0 && c.machine.speedProgress <= 0)
                reserve += c.timeMachineController.machineSpeedGoldCost();
            if (c.machine.goldMultiMagic > 0 && c.machine.goldMultiProgress <= 0)
                reserve += c.timeMachineController.machineGoldMultiCost();
            return reserve;
        }

        private static double ProjectValuedBloodCharges(Character c, int remainingSeconds,
            out string label)
        {
            label = string.Empty;
            double target;
            if (!TryGetValuedBloodTarget(c, remainingSeconds, out target, out label)
                || c.bloodMagic.bloodPoints >= target)
                return 0.0;

            var hold = Main.Autopilot != null && Main.Autopilot.Plan != null
                       && Main.Autopilot.Plan.RebirthExecutionHold;
            var events = ProjectRitualEvents(c, remainingSeconds, hold);
            var missing = target - c.bloodMagic.bloodPoints;
            var gained = 0.0;
            var gold = 0.0;
            foreach (var ritualEvent in events.OrderBy(x => x.Seconds))
            {
                gold += ritualEvent.Gold;
                gained += ritualEvent.Blood;
                if (gained >= missing) break;
            }
            return gained > 0 ? Math.Max(0.0, gold) : 0.0;
        }

        private static bool TryGetValuedBloodTarget(Character c, int remainingSeconds,
            out double target, out string label)
        {
            target = 0.0;
            label = string.Empty;
            if (c.bloodMagic == null || c.bloodSpells == null || c.bloodMagicController == null
                || c.inventory == null || !c.buttons.bloodMagic.interactable)
                return false;

            if (c.settings.rebirthDifficulty == difficulty.sadistic
                && !EndgameDependencyModel.IsOwned(c, EndBloodItemId))
            {
                target = c.bloodSpells.endSpellBlood();
                label = "END Blood spell for item " + EndBloodItemId;
                return target > 0;
            }

            var guff = c.inventory.macguffins != null
                       && c.inventory.macguffins.Any(x => x != null && x.id > 0);
            if (guff && c.settings.rebirthDifficulty >= difficulty.evil
                && c.adventure.itopod.perkLevel.Count > 73
                && c.adventure.itopod.perkLevel[73] >= 1
                && SpellReadyWithin(c.bloodMagic.macguffin2Time.totalseconds,
                    c.bloodMagicController.spells.macguffin2Cooldown, remainingSeconds))
            {
                target = c.bloodSpells.minMacguffin2Blood();
                label = "permanent Blood MacGuffin beta spell";
                return target > 0;
            }
            if (guff && c.adventure.itopod.perkLevel.Count > 72
                && c.adventure.itopod.perkLevel[72] >= 1
                && SpellReadyWithin(c.bloodMagic.macguffin1Time.totalseconds,
                    c.bloodMagicController.spells.macguffin1Cooldown, remainingSeconds))
            {
                target = c.bloodSpells.minMacguffin1Blood();
                label = "permanent Blood MacGuffin alpha spell";
                return target > 0;
            }
            if (SpellReadyWithin(c.bloodMagic.adventureSpellTime.totalseconds,
                c.bloodSpells.adventureSpellCooldown, remainingSeconds))
            {
                target = c.bloodSpells.minAdventureBlood();
                label = "permanent Iron Pill spell";
                return target > 0;
            }
            return false;
        }

        internal static bool TryGetValuedBloodDemand(Character c, int remainingSeconds,
            out double target, out string label)
        {
            return TryGetValuedBloodTarget(c, remainingSeconds, out target, out label)
                   && c != null && c.bloodMagic != null && c.bloodMagic.bloodPoints < target;
        }

        private static bool SpellReadyWithin(double elapsed, int cooldown, int remainingSeconds)
        {
            return Math.Max(0.0, cooldown - elapsed) <= remainingSeconds;
        }

        private static List<RitualEvent> ProjectRitualEvents(Character c, int horizon, bool hold)
        {
            var events = new List<RitualEvent>();
            if (c.bloodMagic.ritual == null || c.bloodMagicController.bloodMagics == null)
                return events;
            var unlocked = Math.Min(c.bloodMagicController.ritualsUnlocked(),
                Math.Min(c.bloodMagic.ritual.Count, c.bloodMagicController.bloodMagics.Length));
            var prospectiveIdle = Math.Max(0L, c.magic.idleMagic);
            for (var i = unlocked - 1; i >= 0; i--)
            {
                var track = c.bloodMagic.ritual[i];
                var magic = Math.Max(0L, track.magic);
                if (magic <= 0 && prospectiveIdle > 0)
                {
                    magic = Math.Min(prospectiveIdle, Math.Max(1L,
                        c.bloodMagicController.bloodMagics[i].capValue()));
                    prospectiveIdle -= magic;
                }
                var rate = RitualProgressPerTick(c, i, magic);
                if (rate <= 0) continue;
                var first = (1.0 - Math.Max(0.0, Math.Min(.999999999, track.progress)))
                            / rate / 50.0;
                if (!hold && first > horizon) continue;
                var interval = 1.0 / rate / 50.0;
                var rawCompletions = 1.0 + Math.Floor((horizon - first)
                                                     / Math.Max(1e-12, interval));
                var completions = hold ? 1 : (int)Math.Min(100000.0,
                    Math.Max(1.0, rawCompletions));
                for (var completion = 0; completion < completions; completion++)
                {
                    events.Add(new RitualEvent
                    {
                        Seconds = first + completion * interval,
                        Blood = Math.Max(0.0, c.bloodMagicController.bloodMagics[i].bloodAdded()),
                        // Ritual.paid is the native charge state; progress alone is insufficient
                        // because a paid bar can still be observed at exactly zero progress.
                        Gold = completion == 0 && track.paid
                            ? 0.0 : Math.Max(0.0, c.bloodMagicController.bloodMagics[i].currentCost())
                    });
                }
            }
            return events;
        }

        private static double RitualProgressPerTick(Character c, int id, long magic)
        {
            if (magic <= 0) return 0;
            double divider;
            if (c.settings.rebirthDifficulty == difficulty.normal)
                divider = 50000.0 * c.bloodMagicController.normalSpeedDividers[id];
            else if (c.settings.rebirthDifficulty == difficulty.evil)
                divider = 50000.0 * c.bloodMagicController.evilSpeedDividers[id];
            else
                divider = c.bloodMagicController.sadisticSpeedDividers[id]
                          * c.bloodMagicController.bloodMagics[id].sadisticDivider();
            return divider <= 0 ? 0 : magic * (double)c.totalMagicPower() / divider
                   * c.bloodMagicController.bloodMagics[id].totalBloodMagicSpeedBonus();
        }

        private static double ReachablePitStep(Character c, int remainingSeconds,
            double baselineGold, out string label)
        {
            label = string.Empty;
            double target;
            if (!c.settings.pitUnlocked || c.pitController == null
                || c.pitController.currentPitTime() - c.pit.pitTime.totalseconds > remainingSeconds
                || !MoneyPitManager.TryGetPermanentTierTarget(out target, out label)
                || target <= 0 || target > baselineGold)
                return 0;
            return target;
        }

        private static double ReachableDiggerStep(Character c, double baselineGold)
        {
            if (c.allDiggers == null || c.diggers == null || c.diggers.diggers == null) return 0;
            var costs = Enumerable.Range(0, c.diggers.diggers.Count)
                .Where(i => c.diggers.diggers[i].maxLevel < c.allDiggers.hardCapLevel(i))
                .Select(i => c.allDiggers.upgradeCost(i))
                .Where(cost => cost > 0 && cost <= baselineGold).ToArray();
            return costs.Length == 0 ? 0.0 : costs.Min();
        }

        private static double TimeMachineSpeedFactor(long level)
        {
            return Math.Min(50L, Math.Max(1L, level + 1L))
                   * (double)Math.Max(1L, level - 48L);
        }

        private static string FormatGold(double amount)
        {
            if (amount >= 1e12) return (amount / 1e12).ToString("0.###") + "T Gold";
            if (amount >= 1e9) return (amount / 1e9).ToString("0.###") + "B Gold";
            if (amount >= 1e6) return (amount / 1e6).ToString("0.###") + "M Gold";
            if (amount >= 1e3) return (amount / 1e3).ToString("0.###") + "K Gold";
            return amount.ToString("0") + " Gold";
        }
    }
}
