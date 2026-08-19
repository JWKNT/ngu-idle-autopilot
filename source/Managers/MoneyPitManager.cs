/*
FILE PURPOSE

Purpose: MoneyPitManager preflights and executes one root-coordinated native all-Gold Money Pit
toss. It also owns one root-coordinated daily-spin reward claim.

Mechanism: The manager consults ResourceHorizonModel's chronological hard-event ledger, models Pit
cumulative tiers with native float/log boundaries plus a saving margin, proves Looty's item-67
filter and usable-slot delivery conditions through task-6 topology/capacity APIs, invokes both
native reward controllers through build-pinned adapters, and settles from independently recaptured
state. Daily Spin proves its exact free-spin-or-86400-second debit, saved-RNG advance, and one
total-spin increment.

Inputs and outputs: Inputs are live Pit time/count/flags/Gold, active plan horizon, inventory filters
and ordinary topology, settings thresholds, and optional loadout locks. Outputs are HOLD/REJECTED or
confirmed reward telemetry. No save/config/runtime file is touched.

Invariants and safety: A toss consumes all current Gold and advances an ever-growing cooldown. It is
never attempted while a hard chronological charge owns stock. A Looty-tier toss requires both
filters open and one exact free slot inside [totalInvMergeSlots(), curSpaces()). Native reflection
must be build-pinned, and normal return is not success without exact postconditions. Conversely, a
native exception after dispatch is committed-with-exception only when the complete copied-state
postcondition is true; a partial debit is quarantined. Autonomous Pit authority remains gated by
the deployment-ceilinged AllowMoneyPitExecution policy.

Extension points and non-goals: The global scheduler may rank toss-now versus wait. This manager
does not assign stochastic reward value, change a loadout, mutate Magic to chase the 1-in-5 high
tier, or lift any deployment authority ceiling.
*/
using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NGUInjector.Autopilot;

namespace NGUInjector.Managers
{
    /*
    STOCHASTIC REWARD SETTLEMENT

    The reward payload is stochastic, but each native claim has deterministic accounting witnesses.
    These immutable copied-state records are deliberately controller-free so fault tests can prove
    full commit, no-op, and partial-prefix outcomes. Neither proof trusts Apply's return value: the
    coordinator may observe a reflection exception after native code already committed.
    */
    internal sealed class MoneyPitTransitionSnapshot
    {
        internal readonly double Timer;
        internal readonly double Gold;
        internal readonly double TotalGold;
        internal readonly long TossCount;
        internal readonly bool Tier1;
        internal readonly bool Tier2;
        internal readonly bool Tier3;
        internal readonly bool Tier4;
        internal readonly bool Tier5;
        internal readonly int LootyCount;

        internal MoneyPitTransitionSnapshot(double timer, double gold, double totalGold,
            long tossCount, bool tier1, bool tier2, bool tier3, bool tier4, bool tier5,
            int lootyCount)
        {
            Timer = timer;
            Gold = gold;
            TotalGold = totalGold;
            TossCount = tossCount;
            Tier1 = tier1;
            Tier2 = tier2;
            Tier3 = tier3;
            Tier4 = tier4;
            Tier5 = tier5;
            LootyCount = lootyCount;
        }
    }

    internal static class MoneyPitTransitionProof
    {
        internal static bool Verify(MoneyPitTransitionSnapshot before,
            MoneyPitTransitionSnapshot after, out int awardedTier, out string reason)
        {
            awardedTier = 0;
            reason = string.Empty;
            if (before == null || after == null || before.Gold < 0.0)
            {
                reason = "Money Pit copied state is incomplete.";
                return false;
            }
            var expectedTotal = before.TotalGold + before.Gold;
            var tolerance = Math.Max(1e-6, Math.Abs(expectedTotal) * 1e-12);
            awardedTier = MoneyPitManager.NextCumulativeTierIndex(before.Tier1, before.Tier2,
                before.Tier3, before.Tier4, before.Tier5, expectedTotal);
            var flagsExact = after.Tier1 == (before.Tier1 || awardedTier == 1)
                             && after.Tier2 == (before.Tier2 || awardedTier == 2)
                             && after.Tier3 == (before.Tier3 || awardedTier == 3)
                             && after.Tier4 == (before.Tier4 || awardedTier == 4)
                             && after.Tier5 == (before.Tier5 || awardedTier == 5);
            var lootyExact = awardedTier == 3
                ? after.LootyCount == before.LootyCount + 1
                : after.LootyCount == before.LootyCount;
            if (after.Timer != 0.0 || after.Gold != 0.0
                || after.TossCount != before.TossCount + 1L
                || Math.Abs(after.TotalGold - expectedTotal) > tolerance
                || !flagsExact || !lootyExact)
            {
                reason = "Money Pit lacked exact timer/Gold/toss/cumulative/tier/delivery state.";
                return false;
            }
            reason = "Exact Money Pit copied-state transition observed.";
            return true;
        }
    }

    internal sealed class DailySpinTransitionSnapshot
    {
        internal readonly double Timer;
        internal readonly long FreeSpins;
        internal readonly long TotalSpins;
        internal readonly string SavedRngFingerprint;

        internal DailySpinTransitionSnapshot(double timer, long freeSpins, long totalSpins,
            string savedRngFingerprint)
        {
            Timer = timer;
            FreeSpins = freeSpins;
            TotalSpins = totalSpins;
            SavedRngFingerprint = savedRngFingerprint ?? string.Empty;
        }
    }

    internal static class DailySpinTransitionProof
    {
        internal static bool Verify(DailySpinTransitionSnapshot before,
            DailySpinTransitionSnapshot after, out string reason)
        {
            reason = string.Empty;
            if (before == null || after == null
                || string.IsNullOrEmpty(before.SavedRngFingerprint)
                || string.IsNullOrEmpty(after.SavedRngFingerprint))
            {
                reason = "Daily Spin copied state or saved RNG witness is incomplete.";
                return false;
            }
            var exactDebit = before.FreeSpins > 0L
                ? after.FreeSpins == before.FreeSpins - 1L && after.Timer == before.Timer
                : after.FreeSpins == before.FreeSpins
                  && Math.Abs(after.Timer - (before.Timer - 86400.0)) <= 1e-6;
            if (!exactDebit || after.TotalSpins != before.TotalSpins + 1L
                || string.Equals(after.SavedRngFingerprint, before.SavedRngFingerprint,
                    StringComparison.Ordinal))
            {
                reason = "Daily Spin lacked exact debit, +1 total spin, or saved-RNG advance.";
                return false;
            }
            reason = "Exact Daily Spin copied-state transition observed.";
            return true;
        }
    }

    internal static class MoneyPitManager
    {
        private const int LootyItemId = 67;
        private static string _lastHoldReason = string.Empty;
        private static DateTime _lastHoldLog = DateTime.MinValue;

        internal static void CheckMoneyPit()
        {
            ExecutionSafety.ReportHold("money-pit-root-required",
                "Money Pit tosses require the caller-owned nonzero root transaction.");
        }

        internal static void CheckMoneyPit(double reserve)
        {
            CheckMoneyPit();
        }

        internal static MutationResult CheckMoneyPit(RootTransaction root, double reserve)
        {
            if (Main.Character == null || Main.Character.settings == null
                || Main.Character.pit == null || Main.Character.pitController == null)
                return null;
            if (root == null || root.IsClosed || !Main.Character.settings.pitUnlocked)
                return null;
            if (Main.Autopilot == null || Main.Autopilot.Config == null
                || !Main.Autopilot.Config.AllowMoneyPitExecution)
            {
                LogHold("Money Pit is root-ready, but autonomous toss authority remains "
                        + "deployment-ceilinged off");
                return null;
            }
            if (Main.Character.pit.pitTime.totalseconds
                < Main.Character.pitController.currentPitTime()) return null;
            if (Main.Character.realGold < Math.Max(1e5, reserve)) return null;

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
                    return null;
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
                return null;
            }

            string preflightReason;
            if (!TryPreflightDelivery(Main.Character, out preflightReason))
            {
                LogHold("Money Pit held before all-Gold debit: " + preflightReason);
                return null;
            }
            return root.ExecuteChild(new MoneyPitIntent(Main.Character));
        }

        // totalReward uses float32 floor(log10(totalGold)) and strict greater-than comparisons.
        // The fifth 1e13 branch only flips tier5TRewarded: tier5TotalReward is an empty native body.
        internal static int NextCumulativeTierIndex(bool tier1, bool tier2, bool tier3,
            bool tier4, bool tier5, double cumulativeAfter)
        {
            if (!tier1 && GoldMechanics.NativePitTierReached(cumulativeAfter, 7)) return 1;
            if (!tier2 && GoldMechanics.NativePitTierReached(cumulativeAfter, 9)) return 2;
            if (!tier3 && GoldMechanics.NativePitTierReached(cumulativeAfter, 10)) return 3;
            if (!tier4 && GoldMechanics.NativePitTierReached(cumulativeAfter, 11)) return 4;
            if (!tier5 && GoldMechanics.NativePitTierReached(cumulativeAfter, 12)) return 5;
            return 0;
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

        private sealed class MoneyPitIntent :
            IMutationIntent<MoneyPitTransitionSnapshot, NativeInvocationResult,
                MoneyPitTransitionSnapshot>
        {
            private readonly Character _character;

            internal MoneyPitIntent(Character character) { _character = character; }

            public string Id { get { return "money-pit.toss"; } }
            public MutationClass Class { get { return MutationClass.MoneyPit; } }
            public MutationRisk Risk { get { return MutationRisk.Irreversible; } }
            public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
            public string BindingId { get { return NativeBindingKeys.MoneyPitEngage; } }
            public bool Required { get { return false; } }
            public bool CanCompensate { get { return false; } }
            public bool CreatesNewEpoch { get { return false; } }
            public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

            public MoneyPitTransitionSnapshot CaptureBefore(MutationContext context)
            {
                return Capture();
            }

            public PreconditionResult CheckPreconditions(MutationContext context,
                MoneyPitTransitionSnapshot before)
            {
                if (!Main.IsAutomationReady)
                    return PreconditionResult.Hold("gameplay synchronization is not current");
                if (before == null || before.Gold < 1e5
                    || !_character.pitController.canToss())
                    return PreconditionResult.Hold("Money Pit is no longer toss-ready");
                string reason;
                return TryPreflightDelivery(_character, out reason)
                    ? PreconditionResult.Ready() : PreconditionResult.Hold(reason);
            }

            public NativeInvocationResult Apply(MutationContext context,
                RootTransactionToken token, MoneyPitTransitionSnapshot before)
            {
                var result = NativeBindingRegistry.Create(typeof(Character).Assembly,
                    Main.GameAssemblySha256).CreateMutationAdapters()
                    .TossMoneyPit(_character.pitController);
                if (result != null
                    && result.Status == NativeInvocationStatus.ThrewAfterInvocation)
                    throw result.Exception ?? new InvalidOperationException(result.Reason);
                return result;
            }

            public VerificationResult<MoneyPitTransitionSnapshot> Verify(MutationContext context,
                MoneyPitTransitionSnapshot before,
                MutationApplyObservation<NativeInvocationResult> apply)
            {
                var after = Capture();
                int tier;
                string reason;
                if (!MoneyPitTransitionProof.Verify(before, after, out tier, out reason))
                    return VerificationResult<MoneyPitTransitionSnapshot>.Failed(reason);
                Main.LogAction("REWARD", "Money Pit: " + _character.pitController.pitText.text
                    + " [confirmed all-Gold debit, cumulative total, +1 toss, exact tier flags"
                    + (tier == 3 ? ", and Looty delivery" : string.Empty) + "]");
                return VerificationResult<MoneyPitTransitionSnapshot>.Satisfied(after,
                    "exact Money Pit debit and deterministic effects confirmed");
            }

            public CompensationResult Compensate(MutationContext context, RecoveryToken token,
                MoneyPitTransitionSnapshot before,
                MutationApplyObservation<NativeInvocationResult> apply)
            {
                return CompensationResult.NotSupported(
                    "Money Pit Gold debit and stochastic reward have no exact inverse");
            }

            public bool BeforeStateMatches(MoneyPitTransitionSnapshot expected,
                MoneyPitTransitionSnapshot observed)
            {
                return Same(expected, observed);
            }

            public string FingerprintBefore(MoneyPitTransitionSnapshot state)
            {
                return Fingerprint(state);
            }
            public string FingerprintAfter(MoneyPitTransitionSnapshot state)
            {
                return Fingerprint(state);
            }

            private MoneyPitTransitionSnapshot Capture()
            {
                if (_character == null || _character.pit == null
                    || _character.inventory == null) return null;
                var pit = _character.pit;
                return new MoneyPitTransitionSnapshot(pit.pitTime.totalseconds,
                    _character.realGold, pit.totalGold, pit.tossCount,
                    pit.tier1TRewarded, pit.tier2TRewarded, pit.tier3TRewarded,
                    pit.tier4TRewarded, pit.tier5TRewarded,
                    _character.inventory.inventory.Count(x =>
                        x != null && x.id == LootyItemId));
            }

            private static bool Same(MoneyPitTransitionSnapshot a,
                MoneyPitTransitionSnapshot b)
            {
                return a != null && b != null && a.Timer == b.Timer && a.Gold == b.Gold
                       && a.TotalGold == b.TotalGold && a.TossCount == b.TossCount
                       && a.Tier1 == b.Tier1 && a.Tier2 == b.Tier2
                       && a.Tier3 == b.Tier3 && a.Tier4 == b.Tier4
                       && a.Tier5 == b.Tier5 && a.LootyCount == b.LootyCount;
            }

            private static string Fingerprint(MoneyPitTransitionSnapshot state)
            {
                return state == null ? "missing" : state.Timer.ToString("R") + ":"
                    + state.Gold.ToString("R") + ":" + state.TotalGold.ToString("R") + ":"
                    + state.TossCount + ":" + state.Tier1 + state.Tier2 + state.Tier3
                    + state.Tier4 + state.Tier5 + ":" + state.LootyCount;
            }
        }

        internal static void DoDailySpin()
        {
            ExecutionSafety.ReportHold("daily-spin-root-required",
                "Daily Spin claims require the caller-owned nonzero root transaction.");
        }

        internal static MutationResult DoDailySpin(RootTransaction root)
        {
            var c = Main.Character;
            if (root == null || root.IsClosed || c == null || c.daily == null
                || c.dailyController == null || c.dailyController.inSpinLoop
                || !c.dailyController.canSpin()) return null;
            return root.ExecuteChild(new DailySpinIntent(c));
        }

        internal static string FingerprintSavedRandomState(object state)
        {
            if (state == null) return string.Empty;
            try
            {
                var type = state.GetType();
                var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public
                                            | BindingFlags.NonPublic)
                    .OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
                if (fields.Length == 0) return string.Empty;
                return type.FullName + ":" + string.Join("|", fields.Select(field =>
                {
                    var value = field.GetValue(state);
                    var formattable = value as IFormattable;
                    return field.Name + "=" + (formattable == null
                        ? Convert.ToString(value, CultureInfo.InvariantCulture)
                        : formattable.ToString(null, CultureInfo.InvariantCulture));
                }).ToArray());
            }
            catch
            {
                return string.Empty;
            }
        }

        private sealed class DailySpinIntent :
            IMutationIntent<DailySpinTransitionSnapshot, NativeInvocationResult,
                DailySpinTransitionSnapshot>
        {
            private readonly Character _character;
            internal DailySpinIntent(Character character) { _character = character; }

            public string Id { get { return "daily-spin.claim"; } }
            public MutationClass Class { get { return MutationClass.DailySpin; } }
            public MutationRisk Risk { get { return MutationRisk.FiniteResource; } }
            public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
            public string BindingId { get { return NativeBindingKeys.DailySpinClaim; } }
            public bool Required { get { return false; } }
            public bool CanCompensate { get { return false; } }
            public bool CreatesNewEpoch { get { return false; } }
            public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

            public DailySpinTransitionSnapshot CaptureBefore(MutationContext context)
            {
                return Capture();
            }

            public PreconditionResult CheckPreconditions(MutationContext context,
                DailySpinTransitionSnapshot before)
            {
                if (!Main.IsAutomationReady)
                    return PreconditionResult.Hold("gameplay synchronization is not current");
                return before != null && !string.IsNullOrEmpty(before.SavedRngFingerprint)
                       && !_character.dailyController.inSpinLoop
                       && _character.dailyController.canSpin()
                    ? PreconditionResult.Ready()
                    : PreconditionResult.Hold("Daily Spin is no longer claimable");
            }

            public NativeInvocationResult Apply(MutationContext context,
                RootTransactionToken token, DailySpinTransitionSnapshot before)
            {
                var result = NativeBindingRegistry.Create(typeof(Character).Assembly,
                    Main.GameAssemblySha256).CreateMutationAdapters()
                    .ClaimDailySpin(_character.dailyController);
                if (result != null
                    && result.Status == NativeInvocationStatus.ThrewAfterInvocation)
                    throw result.Exception ?? new InvalidOperationException(result.Reason);
                return result;
            }

            public VerificationResult<DailySpinTransitionSnapshot> Verify(MutationContext context,
                DailySpinTransitionSnapshot before,
                MutationApplyObservation<NativeInvocationResult> apply)
            {
                var after = Capture();
                string reason;
                if (!DailySpinTransitionProof.Verify(before, after, out reason))
                    return VerificationResult<DailySpinTransitionSnapshot>.Failed(reason);
                Main.LogAction("REWARD", "Daily Spin: "
                    + _character.dailyController.outcomeText.text
                    + " [confirmed exact " + (before.FreeSpins > 0L ? "free-spin" : "86400-second")
                    + " debit, +1 total spin, and saved-RNG advance]");
                return VerificationResult<DailySpinTransitionSnapshot>.Satisfied(after,
                    "exact Daily Spin debit/reward execution confirmed");
            }

            public CompensationResult Compensate(MutationContext context, RecoveryToken token,
                DailySpinTransitionSnapshot before,
                MutationApplyObservation<NativeInvocationResult> apply)
            {
                return CompensationResult.NotSupported(
                    "Daily Spin source debit and stochastic reward have no exact inverse");
            }

            public bool BeforeStateMatches(DailySpinTransitionSnapshot a,
                DailySpinTransitionSnapshot b)
            {
                return a != null && b != null && a.Timer == b.Timer
                       && a.FreeSpins == b.FreeSpins && a.TotalSpins == b.TotalSpins
                       && string.Equals(a.SavedRngFingerprint, b.SavedRngFingerprint,
                           StringComparison.Ordinal);
            }

            public string FingerprintBefore(DailySpinTransitionSnapshot state)
            {
                return Fingerprint(state);
            }
            public string FingerprintAfter(DailySpinTransitionSnapshot state)
            {
                return Fingerprint(state);
            }

            private DailySpinTransitionSnapshot Capture()
            {
                var daily = _character == null ? null : _character.daily;
                return daily == null ? null : new DailySpinTransitionSnapshot(
                    daily.spinTime.totalseconds, daily.freeSpins, daily.totalSpins,
                    FingerprintSavedRandomState(daily.dailyRewardState));
            }

            private static string Fingerprint(DailySpinTransitionSnapshot state)
            {
                return state == null ? "missing" : state.Timer.ToString("R") + ":"
                    + state.FreeSpins + ":" + state.TotalSpins + ":"
                    + state.SavedRngFingerprint;
            }
        }
    }
}
