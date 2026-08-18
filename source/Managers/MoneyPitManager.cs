using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

MoneyPitManager decides when reset-local Gold can be safely tossed, chooses required special
loadouts/diggers, invokes the native Pit/Spin controllers, and verifies cooldown/reward deltas.
Because the Pit consumes all Gold, it consults ResourceHorizonModel's shared claim ledger and will
not toss while an active Augment/Time-Machine charge or valued Blood target owns working capital.
Cumulative permanent tiers are source-proven from native flags; a plan horizon, never the legacy
AutoRebirth checkbox or a fictitious hour, decides whether a not-yet-funded tier is reachable.
*/
namespace NGUInjector.Managers
{
    internal static class MoneyPitManager
    {
        private static string _lastHoldReason = string.Empty;
        private static DateTime _lastHoldLog = DateTime.MinValue;

        internal static void CheckMoneyPit()
        {
            CheckMoneyPit(Main.Settings.MoneyPitThreshold);
        }

        internal static void CheckMoneyPit(double reserve)
        {
            if (!Main.Character.settings.pitUnlocked) return;
            if (Main.Character.pit.pitTime.totalseconds < Main.Character.pitController.currentPitTime()) return;
            if (Main.Character.realGold < reserve) return;
            if (Main.Character.realGold < 1e5) return;

            if (Main.Autopilot != null && Main.Autopilot.CanExecuteSafe)
            {
                var plan = Main.Autopilot.Plan;
                var remaining = plan != null && !plan.RebirthExecutionHold
                    ? (int)Math.Max(1.0, Math.Ceiling(plan.EffectiveAllocationTarget(Main.Character)
                                                     - Main.Character.rebirthTime.totalseconds))
                    : 1;
                var ledger = ResourceHorizonModel.EvaluateGold(Main.Character, remaining);
                var protectedSpend = ledger.ProtectedSpendBefore(GoldClaimKind.MoneyPitPermanentTier);
                if (protectedSpend > 0)
                {
                    LogHold("Money Pit ready, but the joint Gold ledger protects "
                            + protectedSpend.ToString("0.###e+0") + " Gold for "
                            + string.Join(" + ", ledger.Claims.Where(x => x.Hard)
                                .Select(x => x.Label).ToArray()));
                    return;
                }
            }

            double tierTarget;
            string tierLabel;
            if (TryGetPermanentTierTarget(out tierTarget, out tierLabel)
                && Main.Character.realGold < tierTarget && TierIsReachableThisRun(tierTarget))
            {
                var reason = "Money Pit ready, but preserving gold for " + tierLabel
                             + " at " + tierTarget.ToString("0.###e+0")
                             + " current-run gold (permanent one-time reward)";
                LogHold(reason);
                return;
            }

            var gearSwapped = false;
            var diggersSwapped = false;
            if (Main.Settings.MoneyPitLoadout.Length > 0
                || Main.AutopilotWants(x => x.ManageMoneyPit))
            {
                if (!LoadoutManager.TryMoneyPitSwap()) return;
                gearSwapped = true;
            }
            try
            {
                if (Main.Character.realGold >= 1e50 && Main.Settings.ManageMagic && Main.Character.wishes.wishes[4].level > 0)
                {
                    if (!DiggerManager.CanSwap())
                    {
                        Main.LogAction("REJECTED", "Money Pit postponed because digger state is locked");
                        return;
                    }
                    Main.Character.removeMostMagic();
                    for (var i = Main.Character.bloodMagic.ritual.Count - 1; i >= 0; i--)
                        Main.Character.bloodMagicController.bloodMagics[i].cap();

                    DiggerManager.SaveDiggers();
                    diggersSwapped = true;
                    if (!DiggerManager.EquipDiggers(new[] {10}))
                    {
                        Main.LogAction("REJECTED", "Money Pit postponed because the Blood Digger transaction rolled back");
                        return;
                    }
                }
                DoMoneyPit();
            }
            finally
            {
                if (diggersSwapped)
                    DiggerManager.RestoreDiggers();
                if (gearSwapped)
                {
                    if (LoadoutManager.RestoreGear())
                        LoadoutManager.ReleaseLock();
                }
            }
        }

        internal static bool TryGetPermanentTierTarget(out double currentGoldRequired, out string label)
        {
            currentGoldRequired = 0;
            label = string.Empty;
            var c = Main.Character;
            if (c == null || c.pit == null) return false;
            // Native totalReward() tests floor(log10(total cumulative Pit gold)).
            // These flags prove a one-time reward was actually collected.
            var thresholds = new[] {1e8, 1e10, 1e11, 1e12};
            var claimed = new[]
            {
                c.pit.tier1TRewarded, c.pit.tier2TRewarded,
                c.pit.tier3TRewarded, c.pit.tier4TRewarded
            };
            var labels = new[]
            {
                "the 1e8 permanent Adventure-stat reward",
                "the 1e10 permanent Energy/Magic bar reward",
                "the 1e11 one-time Looty reward",
                "the 1e12 one-time 100 EXP reward"
            };
            for (var i = 0; i < thresholds.Length; i++)
            {
                if (claimed[i]) continue;
                currentGoldRequired = Math.Max(1e5, thresholds[i] - Math.Max(0.0, c.pit.totalGold));
                label = labels[i];
                return true;
            }
            return false;
        }

        private static bool TierIsReachableThisRun(double target)
        {
            var c = Main.Character;
            var rate = Math.Max(0.0, c.goldPerSecond());
            if (rate <= 0) return false;
            var seconds = Math.Max(0.0, (target - c.realGold) / rate);
            var plan = Main.Autopilot == null ? null : Main.Autopilot.Plan;
            if (plan == null || plan.RebirthSeconds < 0 || plan.RebirthExecutionHold)
                return target <= c.realGold;
            var remaining = Math.Max(0.0,
                plan.EffectiveAllocationTarget(c) - c.rebirthTime.totalseconds);
            return seconds <= remaining;
        }

        private static void LogHold(string reason)
        {
            if (reason == _lastHoldReason && (DateTime.UtcNow - _lastHoldLog).TotalSeconds < 30)
                return;
            Main.LogAction("HOLD", reason);
            _lastHoldReason = reason;
            _lastHoldLog = DateTime.UtcNow;
        }

        private static void DoMoneyPit()
        {
            var controller = Main.Character.pitController;
            if (!controller.canToss())
                return;
            var timerBefore = Main.Character.pit.pitTime.totalseconds;
            var goldBefore = Main.Character.realGold;
            typeof(PitController).GetMethod("engage", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(controller, null);
            var confirmed = Main.Character.pit.pitTime.totalseconds < timerBefore
                            || Main.Character.realGold < goldBefore;
            Main.LogAction(confirmed ? "REWARD" : "REJECTED",
                confirmed
                    ? "Money Pit: " + controller.pitText.text + " [confirmed by timer/gold delta]"
                    : "Money Pit request produced no timer/gold transition");
        }

        internal static void DoDailySpin()
        {
            if (Main.Character.daily.spinTime.totalseconds < Main.Character.dailyController.targetSpinTime()
                && Main.Character.daily.freeSpins <= 0) return;

            var timerBefore = Main.Character.daily.spinTime.totalseconds;
            var freeSpinsBefore = Main.Character.daily.freeSpins;
            Main.Character.dailyController.startNoBullshitSpin();
            var result = Main.Character.dailyController.outcomeText.text;
            var confirmed = Main.Character.daily.spinTime.totalseconds < timerBefore
                            || Main.Character.daily.freeSpins < freeSpinsBefore;
            Main.LogAction(confirmed ? "REWARD" : "REJECTED",
                confirmed
                    ? "Daily Spin: " + result + " [confirmed by timer/free-spin delta]"
                    : "Daily Spin request produced no timer/free-spin transition");
        }
    }
}
