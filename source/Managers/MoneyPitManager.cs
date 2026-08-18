/*
FILE PURPOSE

Purpose: MoneyPitManager preflights and, when separately authorized, executes the native all-Gold
Money Pit toss. It also owns the unrelated safe daily-spin wrapper.

Mechanism: The manager consults ResourceHorizonModel's chronological hard-event ledger, models Pit
cumulative tiers with native float/log boundaries plus a saving margin, proves Looty's item-67
filter and usable-slot delivery conditions through task-6 topology/capacity APIs, invokes the Pit
through task-5's build-pinned adapter, and verifies exact Gold/toss/cumulative/tier deltas.

Inputs and outputs: Inputs are live Pit time/count/flags/Gold, active plan horizon, inventory filters
and ordinary topology, settings thresholds, and optional loadout locks. Outputs are HOLD/REJECTED or
confirmed reward telemetry. No save/config/runtime file is touched.

Invariants and safety: A toss consumes all current Gold and advances an ever-growing cooldown. It is
never attempted while a hard chronological charge owns stock. A Looty-tier toss requires both
filters open and one exact free slot inside [totalInvMergeSlots(), curSpaces()). Native reflection
must be build-pinned, and normal return is not success without exact postconditions. Autonomous Pit
authority remains off for this implementation wave until integration branch tests grant it.

Extension points and non-goals: Task 29 may enable the authority constant after integration tests
and wrap the typed call in the root coordinator. Global task 28 may rank toss-now versus wait. This
manager does not assign stochastic Pit reward value or mutate Magic to chase the 1-in-5 high tier.
*/
using System;
using System.Linq;
using NGUInjector.Autopilot;

namespace NGUInjector.Managers
{
    internal static class MoneyPitManager
    {
        internal static readonly bool AutonomousTossAuthorityEnabled = false;
        private const int LootyItemId = 67;
        private static string _lastHoldReason = string.Empty;
        private static DateTime _lastHoldLog = DateTime.MinValue;

        internal static void CheckMoneyPit()
        {
            CheckMoneyPit(Main.Settings.MoneyPitThreshold);
        }

        internal static void CheckMoneyPit(double reserve)
        {
            if (Main.Character == null || Main.Character.settings == null
                || Main.Character.pit == null || Main.Character.pitController == null)
                return;
            if (!Main.Character.settings.pitUnlocked) return;
            if (Main.Character.pit.pitTime.totalseconds < Main.Character.pitController.currentPitTime()) return;
            if (Main.Character.realGold < reserve) return;
            if (Main.Character.realGold < 1e5) return;

            if (Main.Autopilot != null && Main.Autopilot.CanExecuteSafe)
            {
                var plan = Main.Autopilot.Plan;
                var remaining = plan != null && !plan.RebirthExecutionHold
                    ? (int)Math.Max(0.0, Math.Ceiling(plan.EffectiveAllocationTarget(Main.Character)
                                                     - Main.Character.rebirthTime.totalseconds))
                    : 0;
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

            string preflightReason;
            if (!TryPreflightDelivery(Main.Character, out preflightReason))
            {
                LogHold("Money Pit held before all-Gold debit: " + preflightReason);
                return;
            }
            if (!AutonomousTossAuthorityEnabled)
            {
                LogHold("Money Pit branch is preflighted but autonomous toss authority remains "
                        + "disabled pending integration branch tests");
                return;
            }

            var gearSwapped = false;
            if (Main.Settings.MoneyPitLoadout.Length > 0
                || Main.AutopilotWants(x => x.ManageMoneyPit))
            {
                if (!LoadoutManager.TryMoneyPitSwap()) return;
                gearSwapped = true;
            }
            try
            {
                DoMoneyPit();
            }
            finally
            {
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
                currentGoldRequired = Math.Max(1e5,
                    GoldMechanics.SafePitThreshold(thresholds[i])
                    - Math.Max(0.0, c.pit.totalGold));
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

        internal static bool TryPreflightDelivery(Character c, out string reason)
        {
            reason = string.Empty;
            if (c == null || c.pit == null || c.inventory == null
                || c.inventory.itemList == null || c.settings == null)
            {
                reason = "Pit/inventory snapshot is incomplete";
                return false;
            }
            var lootyDue = c.pit.tier1TRewarded && c.pit.tier2TRewarded
                           && !c.pit.tier3TRewarded
                           && GoldMechanics.NativePitTierReached(
                               Math.Max(0.0, c.pit.totalGold) + Math.Max(0.0, c.realGold), 10);
            var exactFilter = c.inventory.itemList.itemFiltered != null
                              && c.inventory.itemList.itemFiltered.Count > LootyItemId
                              && c.inventory.itemList.itemFiltered[LootyItemId];
            var topology = InventoryManager.CaptureOrdinaryTopology(c);
            var capacity = topology == null ? null : LootCapacity.ProveOrdinary(topology,
                LootCapacityRequirement.ExactUniqueDelivery(
                    "money-pit-looty-67", 0, 1, 0));
            var preflight = MoneyPitDeliveryPreflight.Evaluate(lootyDue,
                c.settings.filterAccessory, exactFilter,
                capacity != null && capacity.Admitted);
            reason = preflight.Reason;
            return preflight.Admitted;
        }

        private static void DoMoneyPit()
        {
            var controller = Main.Character.pitController;
            if (!controller.canToss())
                return;
            MutationLease tossLease;
            string tossHold;
            var tossOwner = ExecutionSafety.OwnerFor(MutationClass.MoneyPit);
            if (!ExecutionSafety.TryAcquire(MutationClass.MoneyPit, MutationRisk.Irreversible,
                    tossOwner, out tossLease, out tossHold) || !tossLease.IsCurrent)
            {
                LogHold("Money Pit native debit held: "
                        + (string.IsNullOrEmpty(tossHold)
                            ? "execution lease became stale" : tossHold));
                return;
            }
            var timerBefore = Main.Character.pit.pitTime.totalseconds;
            var goldBefore = Main.Character.realGold;
            var totalBefore = Main.Character.pit.totalGold;
            var tossCountBefore = Main.Character.pit.tossCount;
            var tier1FlagBefore = Main.Character.pit.tier1TRewarded;
            var tier2FlagBefore = Main.Character.pit.tier2TRewarded;
            var lootyFlagBefore = Main.Character.pit.tier3TRewarded;
            var lootyCountBefore = Main.Character.inventory.inventory.Count(x =>
                x != null && x.id == LootyItemId);
            var native = NativeBindingRegistry.Create(typeof(Character).Assembly,
                Main.GameAssemblySha256).CreateMutationAdapters();
            var invocation = native.TossMoneyPit(controller);
            var expectedTotal = totalBefore + goldBefore;
            var totalTolerance = Math.Max(1e-6, Math.Abs(expectedTotal) * 1e-12);
            var lootyWasDue = tier1FlagBefore && tier2FlagBefore && !lootyFlagBefore
                              && GoldMechanics.NativePitTierReached(expectedTotal, 10);
            var lootyDelivered = !lootyWasDue
                                 || (Main.Character.pit.tier3TRewarded
                                     && Main.Character.inventory.inventory.Count(x =>
                                         x != null && x.id == LootyItemId) > lootyCountBefore);
            var confirmed = invocation.ReturnedNormally
                            && Main.Character.pit.tossCount == tossCountBefore + 1
                            && Main.Character.pit.pitTime.totalseconds <= timerBefore
                            && Math.Abs(Main.Character.pit.totalGold - expectedTotal)
                               <= totalTolerance
                            && Main.Character.realGold == 0.0
                            && lootyDelivered;
            Main.LogAction(confirmed ? "REWARD" : "REJECTED",
                confirmed
                    ? "Money Pit: " + controller.pitText.text
                      + " [confirmed all-Gold debit, cumulative total, +1 toss/cooldown"
                      + (lootyWasDue ? ", and Looty delivery" : string.Empty) + "]"
                    : "Money Pit request failed exact postconditions; native binding status "
                      + invocation.Status + ": " + invocation.Reason);
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
