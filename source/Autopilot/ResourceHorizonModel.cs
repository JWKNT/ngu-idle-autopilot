using System;
using System.Linq;
using NGUInjector.Managers;

/*
FILE PURPOSE

ResourceHorizonModel prices resources that disappear on rebirth against concrete sinks that can
finish before the selected checkpoint. Its first responsibility is Gold: project the no-further-
investment balance from the native net GPS, reserve exact active Augment charges, estimate Blood
ritual charges from native 50 Hz formulas, and admit only reachable permanent Pit/Digger steps.

The model is read-only. It never buys, allocates, casts, or mutates controller state. Allocation
breakpoints and telemetry consume the same evaluation so the monitor cannot claim that Gold has
value while the allocator is applying a different rule. A reset-local producer is useful only
when the named committed sink exceeds baseline Gold; merely unlocking a feature is not value.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class GoldHorizonEvaluation
    {
        internal double BaselineAtRebirth;
        internal double CommittedSpend;
        internal double Shortfall;
        internal double BloodSpend;
        internal double AugmentSpend;
        internal double PermanentSpend;
        internal string TargetName = "no validated pre-rebirth Gold sink";
        internal string Decision = "Gold horizon has not been evaluated";
        internal bool TimeMachineUseful;
    }

    internal static class ResourceHorizonModel
    {
        internal static GoldHorizonEvaluation EvaluateGold(Character c, int remainingSeconds)
        {
            var result = new GoldHorizonEvaluation();
            if (c == null || remainingSeconds <= 0)
            {
                result.Decision = "Blocked: the selected rebirth checkpoint has arrived";
                return result;
            }

            var netGoldRate = Math.Max(0.0, c.goldPerSecond());
            result.BaselineAtRebirth = Math.Max(0.0, c.realGold) + netGoldRate * remainingSeconds;
            result.AugmentSpend = AutopilotManager.RequiredAugmentWorkingCapital(c);
            string permanentName;
            result.PermanentSpend = ReachablePermanentGoldStep(c, remainingSeconds,
                result.BaselineAtRebirth, out permanentName);
            var optimisticTotalGold = result.BaselineAtRebirth
                                      + Math.Max(1.0, c.grossGoldPerSecond()) * remainingSeconds;
            var bloodBudget = Math.Max(0.0, optimisticTotalGold
                                             - result.AugmentSpend - result.PermanentSpend);
            result.BloodSpend = ProjectBloodCharges(c, remainingSeconds, bloodBudget);
            result.CommittedSpend = result.AugmentSpend + result.BloodSpend + result.PermanentSpend;
            result.Shortfall = Math.Max(0.0, result.CommittedSpend - result.BaselineAtRebirth);

            if (result.AugmentSpend > 0)
                result.TargetName = "active Augment/Upgrade charge";
            if (result.BloodSpend > result.AugmentSpend)
                result.TargetName = "Blood ritual completions";
            if (result.PermanentSpend > 0)
                result.TargetName = permanentName;

            // A shortfall must also be plausibly recoverable. One extra current-GPS
            // horizon is an intentionally conservative upper bound; targets beyond
            // it cannot justify sacrificing Energy/Magic to a reset-local machine.
            var recoverableIncrement = Math.Max(1.0, c.grossGoldPerSecond()) * remainingSeconds;
            result.TimeMachineUseful = result.Shortfall > 0 && result.Shortfall <= recoverableIncrement;
            if (result.CommittedSpend <= 0)
            {
                result.Decision = "Blocked: no named Gold sink can complete before rebirth; unspent Gold and Time Machine levels reset";
            }
            else if (result.Shortfall <= 0)
            {
                result.Decision = "Blocked: baseline Gold already funds " + result.TargetName
                                  + "; further Time Machine levels add only reset-local surplus";
            }
            else if (!result.TimeMachineUseful)
            {
                result.Decision = "Blocked: " + result.TargetName + " is short by "
                                  + FormatGold(result.Shortfall)
                                  + ", beyond the remaining run's conservative recoverable range";
            }
            else
            {
                result.Decision = "Allowed: " + result.TargetName + " has a modeled pre-rebirth shortfall of "
                                  + FormatGold(result.Shortfall) + " after baseline GPS";
            }
            return result;
        }

        /*
        BLOOD GOLD DEMAND

        BR allocates highest unlocked rituals first. Recreate its cap/rate math without touching
        the game: progress is added at 50 Hz, overshoot is discarded, and a level consumes the
        ritual's discounted constant currentCost. We assume Magic rejected by the Time Machine is
        available to Blood, preventing a circular decision based on the previous sweep's split.
        Only tracks that can complete inside the actual remaining horizon create Gold demand.
        */
        private static double ProjectBloodCharges(Character c, int remainingSeconds,
            double reachableGoldBudget)
        {
            if (c.bloodMagic == null || c.bloodMagicController == null
                || c.bloodMagic.ritual == null || c.bloodMagicController.bloodMagics == null
                || !c.buttons.bloodMagic.interactable)
                return 0;

            var availableMagic = Math.Max(0L, c.magic.curMagic);
            var unlocked = Math.Min(c.bloodMagicController.ritualsUnlocked(),
                Math.Min(c.bloodMagic.ritual.Count, c.bloodMagicController.bloodMagics.Length));
            var demand = 0.0;
            for (var i = unlocked - 1; i >= 0 && availableMagic > 0; i--)
            {
                var controller = c.bloodMagicController.bloodMagics[i];
                var costPerCompletion = controller.baseCost * c.totalDiscount();
                var track = c.bloodMagic.ritual[i];
                // BR skips an unaffordable unstarted ritual. Mirror that choice
                // against the whole recoverable envelope so a 10B ritual cannot
                // hide a reachable 30M ritual below it or create a fictitious sink.
                if (track.progress <= 0 && costPerCompletion > reachableGoldBudget - demand)
                    continue;
                var cap = Math.Max(1L, controller.capValue());
                var allocated = Math.Min(availableMagic, cap);
                var progressPerTick = RitualProgressPerTick(c, i, allocated);
                if (progressPerTick <= 0) continue;
                var clampedProgress = Math.Max(0.0, Math.Min(.999999999, track.progress));
                var ticksPerLevel = Math.Ceiling(1.0 / Math.Min(1.0, progressPerTick));
                var firstTicks = Math.Ceiling((1.0 - clampedProgress)
                                              / Math.Min(1.0, progressPerTick));
                var ticksAvailable = (long)remainingSeconds * 50L;
                if (firstTicks > ticksAvailable) continue;
                var completions = 1.0 + Math.Floor((ticksAvailable - firstTicks) / ticksPerLevel);

                // Non-zero progress proves the current level was already charged.
                // Reserve Gold only for later fully completable levels, matching
                // the native charge-on-first-advancing-tick semantics.
                var alreadyPaid = clampedProgress > 0;
                var chargeableCompletions = Math.Max(0.0, completions - (alreadyPaid ? 1.0 : 0.0));
                var affordableCharges = costPerCompletion <= 0 ? 0
                    : Math.Floor(Math.Max(0.0, reachableGoldBudget - demand) / costPerCompletion);
                chargeableCompletions = Math.Min(chargeableCompletions, affordableCharges);
                if (chargeableCompletions <= 0 && !alreadyPaid) continue;
                demand += chargeableCompletions * costPerCompletion;
                availableMagic -= allocated;
            }
            return Math.Max(0.0, demand);
        }

        private static double RitualProgressPerTick(Character c, int id, long magic)
        {
            double divider;
            if (c.settings.rebirthDifficulty == difficulty.normal)
                divider = 50000.0 * c.bloodMagicController.normalSpeedDividers[id];
            else if (c.settings.rebirthDifficulty == difficulty.evil)
                divider = 50000.0 * c.bloodMagicController.evilSpeedDividers[id];
            else
                divider = c.bloodMagicController.sadisticSpeedDividers[id]
                          * c.bloodMagicController.bloodMagics[id].sadisticDivider();
            if (divider <= 0) return 0;
            return magic * (double)c.totalMagicPower() / divider
                   * c.bloodMagicController.bloodMagics[id].totalBloodMagicSpeedBonus();
        }

        private static double ReachablePermanentGoldStep(Character c, int remainingSeconds,
            double baselineGold, out string label)
        {
            label = string.Empty;
            var optimisticLimit = baselineGold + Math.Max(1.0, c.grossGoldPerSecond()) * remainingSeconds;

            double pitTarget;
            string pitLabel;
            if (c.settings.pitUnlocked && c.pitController != null
                && c.pitController.currentPitTime() - c.pit.pitTime.totalseconds <= remainingSeconds
                && MoneyPitManager.TryGetPermanentTierTarget(out pitTarget, out pitLabel)
                && pitTarget > 0 && pitTarget <= optimisticLimit)
            {
                label = pitLabel;
                return pitTarget;
            }

            if (c.allDiggers == null || c.diggers == null || c.diggers.diggers == null)
                return 0;
            var cheapest = double.PositiveInfinity;
            for (var i = 0; i < c.diggers.diggers.Count; i++)
            {
                if (c.diggers.diggers[i].maxLevel >= c.allDiggers.hardCapLevel(i)) continue;
                var cost = c.allDiggers.upgradeCost(i);
                if (cost > 0 && cost <= optimisticLimit)
                    cheapest = Math.Min(cheapest, cost);
            }
            if (double.IsInfinity(cheapest)) return 0;
            label = "the next permanent Digger max-level upgrade";
            return cheapest;
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
