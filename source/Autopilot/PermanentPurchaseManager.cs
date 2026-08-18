/*
FILE PURPOSE

PermanentPurchaseManager is the fail-closed planning and transactional settlement layer for the
sealed permanent-purchase catalog.  It accepts immutable boundary snapshots, exact native costs,
complete expected state vectors, a terminal-time value, and optional committed-bundle funding.
It emits HOLD or one exact plan.  Execution is a task-1 MutationCoordinator child intent with
MutationClass.PermanentSpend; a normal reflection return is never success, and commit requires the
exact currency debit, every declared effect, no undeclared captured-state change, selector/filter
restoration, and the audited build identity.

The live adapter is deliberately an interface.  Task 29 can implement it with task 5's
NativeMutationAdapters and task 6's live PhysicalTopology capture without adding reflection here.
All Heart calls receive only the target item ID for a temporary filter exemption, and must restore
the original filter synchronously.  An unknown SHA/MVID retains catalog telemetry but plans HOLD.
The default constructor leaves mutation authority disabled until integration/backtest explicitly
opts in; no source in this task wires or enables live spending.

Dynamic reserves are exact committed-bundle remainder minus guaranteed pre-boundary income, floored
by the user's reserve.  Each PurchasePlanningPass permits at most one native attempt and then
requires a fresh snapshot/replan.  AP reward projection classifies online/offline ITOPOD as direct
unmodified income; ordinary Character.addAP sources retain native ordered float32 multipliers.
*/
using System;
using System.Collections.Generic;

namespace NGUInjector.Autopilot
{
    internal sealed class PurchaseStateVector
    {
        private readonly Dictionary<string, long> _values;

        internal readonly long CurrencyBalance;

        internal PurchaseStateVector(long currencyBalance, IDictionary<string, long> values)
        {
            if (currencyBalance < 0L) throw new ArgumentOutOfRangeException("currencyBalance");
            CurrencyBalance = currencyBalance;
            _values = new Dictionary<string, long>(StringComparer.Ordinal);
            if (values == null) return;
            foreach (var pair in values)
            {
                if (string.IsNullOrEmpty(pair.Key)) throw new ArgumentException("State key is empty.");
                _values.Add(pair.Key, pair.Value);
            }
        }

        internal int Count { get { return _values.Count; } }

        internal bool TryGet(string key, out long value)
        {
            return _values.TryGetValue(key ?? string.Empty, out value);
        }

        internal string[] Keys()
        {
            var keys = new string[_values.Count];
            _values.Keys.CopyTo(keys, 0);
            Array.Sort(keys, StringComparer.Ordinal);
            return keys;
        }

        internal Dictionary<string, long> ValuesCopy()
        {
            return new Dictionary<string, long>(_values, StringComparer.Ordinal);
        }

        internal bool ExactEquals(PurchaseStateVector other)
        {
            if (other == null || CurrencyBalance != other.CurrencyBalance
                || _values.Count != other._values.Count) return false;
            foreach (var pair in _values)
            {
                long value;
                if (!other._values.TryGetValue(pair.Key, out value) || value != pair.Value)
                    return false;
            }
            return true;
        }

        internal string Fingerprint()
        {
            var keys = Keys();
            var text = "balance=" + CurrencyBalance;
            for (var i = 0; i < keys.Length; i++) text += "|" + keys[i] + "=" + _values[keys[i]];
            return text;
        }
    }

    internal sealed class PurchaseBoundarySnapshot
    {
        internal readonly string GameSha256;
        internal readonly Guid GameMvid;
        internal readonly int AmbientControllerId;
        internal readonly string AmbientControllerName;
        internal readonly long LiveCost;
        internal readonly PurchaseCostState CostState;
        internal readonly PurchaseStateVector State;
        internal readonly bool ExpAutoMergeOwned;
        internal readonly LootCapacityProof OrdinaryCapacity;
        internal readonly bool TargetItemFiltered;
        internal readonly bool SupportsTargetFilterTransaction;

        internal PurchaseBoundarySnapshot(string gameSha256, Guid gameMvid,
            int ambientControllerId, string ambientControllerName, long liveCost,
            PurchaseCostState costState, PurchaseStateVector state, bool expAutoMergeOwned,
            LootCapacityProof ordinaryCapacity, bool targetItemFiltered,
            bool supportsTargetFilterTransaction)
        {
            GameSha256 = NormalizeHash(gameSha256);
            GameMvid = gameMvid;
            AmbientControllerId = ambientControllerId;
            AmbientControllerName = ambientControllerName ?? string.Empty;
            LiveCost = liveCost;
            CostState = costState;
            State = state;
            ExpAutoMergeOwned = expAutoMergeOwned;
            OrdinaryCapacity = ordinaryCapacity;
            TargetItemFiltered = targetItemFiltered;
            SupportsTargetFilterTransaction = supportsTargetFilterTransaction;
        }

        internal bool IsAuditedBuild
        {
            get
            {
                return string.Equals(GameSha256, PurchaseDescriptorCatalog.AuditedGameSha256,
                           StringComparison.OrdinalIgnoreCase)
                       && GameMvid == PurchaseDescriptorCatalog.AuditedGameMvid;
            }
        }

        internal string Fingerprint()
        {
            return GameSha256 + "|" + GameMvid + "|selector=" + AmbientControllerId + ":"
                   + AmbientControllerName + "|cost=" + LiveCost + "|autoMerge="
                   + ExpAutoMergeOwned + "|filtered=" + TargetItemFiltered + "|filterTx="
                   + SupportsTargetFilterTransaction + "|capacity="
                   + (OrdinaryCapacity == null ? "none" : OrdinaryCapacity.UsableFreeSlotCount.ToString())
                   + "|" + (State == null ? "state:none" : State.Fingerprint());
        }

        internal bool ExactBeforeEquals(PurchaseBoundarySnapshot other)
        {
            if (other == null || GameMvid != other.GameMvid
                || !string.Equals(GameSha256, other.GameSha256, StringComparison.OrdinalIgnoreCase)
                || AmbientControllerId != other.AmbientControllerId
                || !string.Equals(AmbientControllerName, other.AmbientControllerName,
                    StringComparison.Ordinal)
                || LiveCost != other.LiveCost || ExpAutoMergeOwned != other.ExpAutoMergeOwned
                || TargetItemFiltered != other.TargetItemFiltered
                || SupportsTargetFilterTransaction != other.SupportsTargetFilterTransaction)
                return false;
            if (State == null ? other.State != null : !State.ExactEquals(other.State)) return false;
            if (!CostStatesEqual(CostState, other.CostState)) return false;
            if (OrdinaryCapacity == null || other.OrdinaryCapacity == null)
                return OrdinaryCapacity == null && other.OrdinaryCapacity == null;
            return OrdinaryCapacity.Admitted == other.OrdinaryCapacity.Admitted
                   && OrdinaryCapacity.UsableStart == other.OrdinaryCapacity.UsableStart
                   && OrdinaryCapacity.UsableEnd == other.OrdinaryCapacity.UsableEnd
                   && OrdinaryCapacity.UsableFreeSlotCount == other.OrdinaryCapacity.UsableFreeSlotCount
                   && OrdinaryCapacity.RequiredFreeSlots == other.OrdinaryCapacity.RequiredFreeSlots
                   && OrdinaryCapacity.EvidenceKind == other.OrdinaryCapacity.EvidenceKind;
        }

        private static bool CostStatesEqual(PurchaseCostState left, PurchaseCostState right)
        {
            if (left == null || right == null) return left == null && right == null;
            return left.Counter == right.Counter && left.Amount == right.Amount
                   && left.LiveSerializedCost == right.LiveSerializedCost
                   && left.BoughtNewbiePack == right.BoughtNewbiePack
                   && left.Scalar.Equals(right.Scalar);
        }

        private static string NormalizeHash(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty
                : value.Trim().Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    internal sealed class PurchaseBundleCommitment
    {
        internal readonly string BundleId;
        internal readonly PermanentCurrency Currency;
        internal readonly long RemainingExactCostAfterCandidate;
        internal readonly long GuaranteedIncomeBeforeBoundary;

        internal PurchaseBundleCommitment(string bundleId, PermanentCurrency currency,
            long remainingExactCostAfterCandidate, long guaranteedIncomeBeforeBoundary)
        {
            if (remainingExactCostAfterCandidate < 0L)
                throw new ArgumentOutOfRangeException("remainingExactCostAfterCandidate");
            if (guaranteedIncomeBeforeBoundary < 0L)
                throw new ArgumentOutOfRangeException("guaranteedIncomeBeforeBoundary");
            BundleId = bundleId ?? string.Empty;
            Currency = currency;
            RemainingExactCostAfterCandidate = remainingExactCostAfterCandidate;
            GuaranteedIncomeBeforeBoundary = guaranteedIncomeBeforeBoundary;
        }
    }

    internal static class DynamicPurchaseReserve
    {
        internal static long Calculate(long configuredFloor, PermanentCurrency currency,
            PurchaseBundleCommitment commitment)
        {
            if (configuredFloor < 0L) throw new ArgumentOutOfRangeException("configuredFloor");
            if (commitment == null) return configuredFloor;
            if (commitment.Currency != currency)
                throw new InvalidOperationException("Committed bundle currency does not match the purchase.");
            var unfunded = commitment.RemainingExactCostAfterCandidate
                           <= commitment.GuaranteedIncomeBeforeBoundary
                ? 0L : commitment.RemainingExactCostAfterCandidate
                       - commitment.GuaranteedIncomeBeforeBoundary;
            return Math.Max(configuredFloor, unfunded);
        }
    }

    internal sealed class PurchasePlan
    {
        internal readonly PurchaseDescriptor Descriptor;
        internal readonly PurchaseBoundarySnapshot Before;
        internal readonly PurchaseStateVector ExpectedAfter;
        internal readonly long ExactCost;
        internal readonly long DynamicReserve;
        internal readonly double TerminalSecondsSavedAfterFunding;
        internal readonly string BundleId;

        internal PurchasePlan(PurchaseDescriptor descriptor, PurchaseBoundarySnapshot before,
            PurchaseStateVector expectedAfter, long exactCost, long dynamicReserve,
            double terminalSecondsSavedAfterFunding, string bundleId)
        {
            Descriptor = descriptor;
            Before = before;
            ExpectedAfter = expectedAfter;
            ExactCost = exactCost;
            DynamicReserve = dynamicReserve;
            TerminalSecondsSavedAfterFunding = terminalSecondsSavedAfterFunding;
            BundleId = bundleId ?? string.Empty;
        }
    }

    internal enum PurchasePlanStatus
    {
        Planned,
        Held
    }

    internal sealed class PurchasePlanResult
    {
        internal readonly PurchasePlanStatus Status;
        internal readonly PurchasePlan Plan;
        internal readonly string Reason;

        internal PurchasePlanResult(PurchasePlanStatus status, PurchasePlan plan, string reason)
        {
            Status = status;
            Plan = plan;
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class PurchasePlanningPass
    {
        internal readonly long PassId;
        private bool _attempted;

        internal PurchasePlanningPass(long passId)
        {
            if (passId <= 0L) throw new ArgumentOutOfRangeException("passId");
            PassId = passId;
        }

        internal bool Attempted { get { return _attempted; } }

        internal bool TryMarkAttempt()
        {
            if (_attempted) return false;
            _attempted = true;
            return true;
        }
    }

    internal enum PurchaseInvocationStatus
    {
        NotAttempted,
        Invoked,
        ThrewAfterDispatch
    }

    internal sealed class PurchaseInvocation
    {
        internal readonly PurchaseInvocationStatus Status;
        internal readonly string BindingKey;
        internal readonly string Reason;
        internal readonly Exception Exception;

        internal PurchaseInvocation(PurchaseInvocationStatus status, string bindingKey,
            string reason, Exception exception)
        {
            Status = status;
            BindingKey = bindingKey ?? string.Empty;
            Reason = reason ?? string.Empty;
            Exception = exception;
        }

        internal bool Attempted
        {
            get { return Status == PurchaseInvocationStatus.Invoked
                         || Status == PurchaseInvocationStatus.ThrewAfterDispatch; }
        }

        internal static PurchaseInvocation Held(string bindingKey, string reason)
        {
            return new PurchaseInvocation(PurchaseInvocationStatus.NotAttempted,
                bindingKey, reason, null);
        }

        internal static PurchaseInvocation Invoked(string bindingKey)
        {
            return new PurchaseInvocation(PurchaseInvocationStatus.Invoked,
                bindingKey, "Native method returned; settlement is still unproven.", null);
        }

        internal static PurchaseInvocation Threw(string bindingKey, Exception exception)
        {
            return new PurchaseInvocation(PurchaseInvocationStatus.ThrewAfterDispatch,
                bindingKey, "Native dispatch threw; recapture before retry.", exception);
        }
    }

    internal interface IPermanentPurchaseRuntime
    {
        PurchaseBoundarySnapshot Capture(PurchaseDescriptor descriptor);
        PurchaseInvocation Invoke(RootTransactionToken token, PurchaseDescriptor descriptor,
            int temporaryHeartFilterExemptionItemId);
    }

    internal enum PurchaseExecutionStatus
    {
        Held,
        Attempted
    }

    internal sealed class PurchaseExecutionResult
    {
        internal readonly PurchaseExecutionStatus Status;
        internal readonly string Reason;
        internal readonly MutationResult<PurchaseBoundarySnapshot, PurchaseBoundarySnapshot> Mutation;

        internal PurchaseExecutionResult(PurchaseExecutionStatus status, string reason,
            MutationResult<PurchaseBoundarySnapshot, PurchaseBoundarySnapshot> mutation)
        {
            Status = status;
            Reason = reason ?? string.Empty;
            Mutation = mutation;
        }
    }

    internal sealed class PermanentPurchaseManager
    {
        private readonly bool _mutationAuthorityAfterIntegrationBacktest;

        internal PermanentPurchaseManager()
            : this(false)
        {
        }

        internal PermanentPurchaseManager(bool mutationAuthorityAfterIntegrationBacktest)
        {
            _mutationAuthorityAfterIntegrationBacktest = mutationAuthorityAfterIntegrationBacktest;
        }

        internal bool MutationAuthorityEnabled
        {
            get { return _mutationAuthorityAfterIntegrationBacktest; }
        }

        internal PurchasePlanResult PlanAp(PurchaseBoundarySnapshot snapshot, int id,
            string exactMethodName, PurchaseStateVector expectedAfter, long configuredReserveFloor,
            PurchaseBundleCommitment commitment, double terminalSecondsSavedAfterFunding)
        {
            PurchaseDescriptor descriptor;
            string reason;
            if (!PurchaseDescriptorCatalog.TryResolveAp(id, exactMethodName, out descriptor, out reason))
                return Held(reason);
            return Plan(snapshot, descriptor, expectedAfter, configuredReserveFloor,
                commitment, terminalSecondsSavedAfterFunding);
        }

        internal PurchasePlanResult Plan(PurchaseBoundarySnapshot snapshot,
            PurchaseDescriptor descriptor, PurchaseStateVector expectedAfter,
            long configuredReserveFloor, PurchaseBundleCommitment commitment,
            double terminalSecondsSavedAfterFunding)
        {
            if (snapshot == null || descriptor == null || expectedAfter == null)
                return Held("Snapshot, sealed descriptor, and exact expected state are required.");
            PurchaseDescriptor catalogDescriptor;
            if (!PurchaseDescriptorCatalog.TryGet(descriptor.Key, out catalogDescriptor)
                || !object.ReferenceEquals(descriptor, catalogDescriptor))
                return Held("Purchase descriptor is not the exact sealed catalog instance.");
            if (!snapshot.IsAuditedBuild)
                return Held("Unknown game SHA/MVID: permanent purchase catalog is read-only.");
            if (snapshot.State == null || snapshot.CostState == null)
                return Held("Exact live currency/effect state or cost state is unavailable.");
            if (double.IsNaN(terminalSecondsSavedAfterFunding)
                || double.IsInfinity(terminalSecondsSavedAfterFunding)
                || terminalSecondsSavedAfterFunding <= 0.0)
                return Held("HOLD wins because conservative terminal seconds saved is not positive.");
            if (descriptor.Unlock == PurchaseUnlockRequirement.ExpAutoMerge
                && !snapshot.ExpAutoMergeOwned)
                return Held("ID 69 requires the native EXP Auto Merge purchase.");

            long exactCost;
            try { exactCost = descriptor.Cost.Evaluate(snapshot.CostState); }
            catch (Exception error) { return Held("Exact native cost is invalid: " + error.Message); }
            if (exactCost <= 0L || snapshot.LiveCost != exactCost)
                return Held("Live native cost does not match the sealed descriptor cost.");

            long reserve;
            try
            {
                reserve = DynamicPurchaseReserve.Calculate(configuredReserveFloor,
                    descriptor.Currency, commitment);
            }
            catch (Exception error) { return Held("Dynamic reserve is invalid: " + error.Message); }
            if (snapshot.State.CurrencyBalance < exactCost
                || snapshot.State.CurrencyBalance - exactCost < reserve)
                return Held("Exact debit would violate the committed dynamic reserve.");

            if (descriptor.IsHeart)
            {
                if (snapshot.OrdinaryCapacity == null || !snapshot.OrdinaryCapacity.Admitted
                    || snapshot.OrdinaryCapacity.EvidenceKind != CapacityEvidenceKind.ExactWorstCase
                    || snapshot.OrdinaryCapacity.RequiredFreeSlots < 1)
                    return Held("Heart delivery lacks one exact ordinary loot-usable slot.");
                if (snapshot.TargetItemFiltered && !snapshot.SupportsTargetFilterTransaction)
                    return Held("Heart target is filtered and no target-specific restoration transaction is available.");
            }

            var reason = ValidateExpectedTransition(descriptor, snapshot.State, expectedAfter,
                exactCost, snapshot.CostState);
            if (!string.IsNullOrEmpty(reason)) return Held(reason);
            return new PurchasePlanResult(PurchasePlanStatus.Planned,
                new PurchasePlan(descriptor, snapshot, expectedAfter, exactCost, reserve,
                    terminalSecondsSavedAfterFunding,
                    commitment == null ? string.Empty : commitment.BundleId), string.Empty);
        }

        internal PurchaseExecutionResult ExecuteOne(PurchasePlanningPass pass,
            RootTransaction root, PurchasePlan plan, IPermanentPurchaseRuntime runtime)
        {
            if (!_mutationAuthorityAfterIntegrationBacktest)
                return new PurchaseExecutionResult(PurchaseExecutionStatus.Held,
                    "Live permanent spending remains disabled pending task-29 integration/backtest.", null);
            if (pass == null || root == null || plan == null || runtime == null)
                return new PurchaseExecutionResult(PurchaseExecutionStatus.Held,
                    "A live pass, root transaction, exact plan, and runtime adapter are required.", null);
            if (!pass.TryMarkAttempt())
                return new PurchaseExecutionResult(PurchaseExecutionStatus.Held,
                    "One permanent-purchase atom was already attempted; replan from fresh state.", null);
            var mutation = root.ExecuteChild(
                new PermanentPurchaseIntent(plan, runtime));
            return new PurchaseExecutionResult(PurchaseExecutionStatus.Attempted,
                mutation.Reason, mutation);
        }

        internal static long ProjectApReward(long baseAmount, ApIncomeSourceKind source,
            params float[] nativeOrderedCharacterAddApModifiers)
        {
            if (baseAmount < 0L) throw new ArgumentOutOfRangeException("baseAmount");
            if (source == ApIncomeSourceKind.OnlineItopodDirect
                || source == ApIncomeSourceKind.OfflineItopodDirect)
                return baseAmount;
            if (source != ApIncomeSourceKind.CharacterAddAp)
                throw new ArgumentOutOfRangeException("source");
            var value = (float)baseAmount;
            var modifiers = nativeOrderedCharacterAddApModifiers ?? new float[0];
            for (var i = 0; i < modifiers.Length; i++)
            {
                var modifier = modifiers[i];
                if (float.IsNaN(modifier) || float.IsInfinity(modifier) || modifier < 0f)
                    throw new ArgumentOutOfRangeException("nativeOrderedCharacterAddApModifiers");
                value *= modifier;
            }
            if (float.IsPositiveInfinity(value) || value >= long.MaxValue) return long.MaxValue;
            if (value <= 0f) return 0L;
            return (long)Math.Floor(value);
        }

        private static string ValidateExpectedTransition(PurchaseDescriptor descriptor,
            PurchaseStateVector before, PurchaseStateVector expectedAfter, long exactCost,
            PurchaseCostState costState)
        {
            if (before.CurrencyBalance < exactCost
                || expectedAfter.CurrencyBalance != before.CurrencyBalance - exactCost)
                return "Expected state does not contain the exact native currency debit.";
            var beforeKeys = before.Keys();
            var afterKeys = expectedAfter.Keys();
            if (beforeKeys.Length != afterKeys.Length)
                return "Expected state vector does not cover the same complete key set as before-state.";
            for (var i = 0; i < beforeKeys.Length; i++)
                if (!string.Equals(beforeKeys[i], afterKeys[i], StringComparison.Ordinal))
                    return "Expected state vector key set changed across the purchase.";

            var effects = descriptor.Effects();
            var effectKeys = new Dictionary<string, PurchaseEffectDescriptor>(StringComparer.Ordinal);
            for (var i = 0; i < effects.Length; i++)
            {
                if (effectKeys.ContainsKey(effects[i].StateKey))
                    return "Descriptor contains duplicate effect key " + effects[i].StateKey + ".";
                effectKeys.Add(effects[i].StateKey, effects[i]);
                long left;
                long right;
                if (!before.TryGet(effects[i].StateKey, out left)
                    || !expectedAfter.TryGet(effects[i].StateKey, out right))
                    return "Complete effect key is missing: " + effects[i].StateKey + ".";
                if (!effects[i].IsExpectedTransition(left, right, costState))
                    return "Expected transition is not exact for " + effects[i].StateKey + ".";
            }

            for (var i = 0; i < beforeKeys.Length; i++)
            {
                long left;
                long right;
                before.TryGet(beforeKeys[i], out left);
                expectedAfter.TryGet(beforeKeys[i], out right);
                if (left != right && !effectKeys.ContainsKey(beforeKeys[i]))
                    return "Expected state changes undeclared key " + beforeKeys[i] + ".";
            }
            return string.Empty;
        }

        private static PurchasePlanResult Held(string reason)
        {
            return new PurchasePlanResult(PurchasePlanStatus.Held, null, reason);
        }

        private sealed class PermanentPurchaseIntent :
            IMutationIntent<PurchaseBoundarySnapshot, PurchaseInvocation, PurchaseBoundarySnapshot>
        {
            private readonly PurchasePlan _plan;
            private readonly IPermanentPurchaseRuntime _runtime;

            internal PermanentPurchaseIntent(PurchasePlan plan, IPermanentPurchaseRuntime runtime)
            {
                _plan = plan;
                _runtime = runtime;
            }

            public string Id { get { return "permanent-purchase/" + _plan.Descriptor.Key; } }
            public MutationClass Class { get { return MutationClass.PermanentSpend; } }
            public MutationRisk Risk { get { return MutationRisk.Irreversible; } }
            public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
            public string BindingId
            {
                get
                {
                    return _plan.Descriptor.NativeBindingKey + "/token=0x"
                           + _plan.Descriptor.MetadataToken.ToString("x8");
                }
            }
            public bool Required { get { return true; } }
            public bool CanCompensate { get { return false; } }
            public bool CreatesNewEpoch { get { return false; } }
            public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

            public PurchaseBoundarySnapshot CaptureBefore(MutationContext context)
            {
                return _runtime.Capture(_plan.Descriptor);
            }

            public PreconditionResult CheckPreconditions(MutationContext context,
                PurchaseBoundarySnapshot before)
            {
                if (before == null || !before.ExactBeforeEquals(_plan.Before))
                    return PreconditionResult.Hold(
                        "Purchase boundary changed after planning; replan before any debit.");
                long cost;
                try { cost = _plan.Descriptor.Cost.Evaluate(before.CostState); }
                catch (Exception error) { return PreconditionResult.Hold(error.Message); }
                if (!before.IsAuditedBuild || cost != _plan.ExactCost || before.LiveCost != cost)
                    return PreconditionResult.Hold(
                        "Build identity or exact native cost changed at the mutation boundary.");
                if (before.State.CurrencyBalance - cost < _plan.DynamicReserve)
                    return PreconditionResult.Hold(
                        "Fresh balance no longer funds the exact debit plus dynamic reserve.");
                if (_plan.Descriptor.Unlock == PurchaseUnlockRequirement.ExpAutoMerge
                    && !before.ExpAutoMergeOwned)
                    return PreconditionResult.Hold("ID 69 lost its EXP Auto Merge prerequisite.");
                if (_plan.Descriptor.IsHeart
                    && (before.OrdinaryCapacity == null || !before.OrdinaryCapacity.Admitted))
                    return PreconditionResult.Hold("Heart ordinary-slot proof is no longer valid.");
                return PreconditionResult.Ready();
            }

            public PurchaseInvocation Apply(MutationContext context, RootTransactionToken token,
                PurchaseBoundarySnapshot before)
            {
                var invocation = _runtime.Invoke(token, _plan.Descriptor,
                    _plan.Descriptor.IsHeart ? _plan.Descriptor.HeartItemId : 0);
                if (invocation == null)
                    return PurchaseInvocation.Held(_plan.Descriptor.NativeBindingKey,
                        "Runtime adapter returned no typed invocation result.");
                if (invocation.Status == PurchaseInvocationStatus.ThrewAfterDispatch)
                    throw invocation.Exception ?? new InvalidOperationException(invocation.Reason);
                return invocation;
            }

            public VerificationResult<PurchaseBoundarySnapshot> Verify(MutationContext context,
                PurchaseBoundarySnapshot before,
                MutationApplyObservation<PurchaseInvocation> apply)
            {
                var after = _runtime.Capture(_plan.Descriptor);
                if (after == null) return VerificationResult<PurchaseBoundarySnapshot>.Failed(
                    "Post-purchase state capture returned null.");
                if (!after.IsAuditedBuild || after.AmbientControllerId != before.AmbientControllerId
                    || !string.Equals(after.AmbientControllerName, before.AmbientControllerName,
                        StringComparison.Ordinal))
                    return VerificationResult<PurchaseBoundarySnapshot>.Failed(
                        "Build or AP controller selector was not restored exactly.");
                if (after.TargetItemFiltered != before.TargetItemFiltered)
                    return VerificationResult<PurchaseBoundarySnapshot>.Failed(
                        "Temporary Heart filter exemption was not restored exactly.");
                if (after.State == null || !after.State.ExactEquals(_plan.ExpectedAfter))
                    return VerificationResult<PurchaseBoundarySnapshot>.Failed(
                        "Exact currency plus complete effect vector did not match expected state.");
                if (apply.ReturnedNormally
                    && (apply.Value == null || !apply.Value.Attempted))
                    return VerificationResult<PurchaseBoundarySnapshot>.Failed(
                        "Runtime did not dispatch the sealed native purchase method.");
                return VerificationResult<PurchaseBoundarySnapshot>.Satisfied(after,
                    "Exact debit, complete effect vector, and temporary-state restoration verified.");
            }

            public CompensationResult Compensate(MutationContext context, RecoveryToken token,
                PurchaseBoundarySnapshot before,
                MutationApplyObservation<PurchaseInvocation> apply)
            {
                return CompensationResult.NotSupported(
                    "Permanent currency mutations cannot be rolled back by field rewriting.");
            }

            public bool BeforeStateMatches(PurchaseBoundarySnapshot expected,
                PurchaseBoundarySnapshot observed)
            {
                return expected != null && expected.ExactBeforeEquals(observed);
            }

            public string FingerprintBefore(PurchaseBoundarySnapshot before)
            {
                return before == null ? "<null>" : before.Fingerprint();
            }

            public string FingerprintAfter(PurchaseBoundarySnapshot after)
            {
                return after == null ? "<null>" : after.Fingerprint();
            }
        }
    }
}
