/*
FILE PURPOSE

Purpose: ApQpPermanentSpend is the source-exact, root-coordinated execution boundary for permanent
Arbitrary Point upgrades and one-level Quest Point quirks. It replaces name-weighted shop heuristics
with immutable native snapshots and externally computed exact downstream-seconds quotes.

Mechanism: the planner accepts only the conservative persistent AP subset sealed by
PurchaseDescriptorCatalog and any native quirk that passes tryLevelUp's cap, difficulty, and feature
gates. Live capture records the installed build, exact balance/cost, AP selector state, every declared
persistent AP field, or the complete quirk-level vector. Apply uses build-pinned
NativeMutationAdapters. MutationCoordinator commits only an exact debit plus exact declared effect;
one root can attempt at most one permanent atom.

Inputs and outputs: inputs are Character/native controllers, explicit AP/QP authority flags and
reserves, an existing RootTransaction, and PermanentSpendMarginalQuote values produced by the global
mechanics projection for the exact captured fingerprint. Outputs are HOLD/planned/attempted results
and normal MutationCoordinator evidence. No configuration flag is enabled here.

Invariants and safety: unknown builds, unsupported AP effects, stale quotes, nonpositive or shadow
values, reserve violations, selector drift, native no-ops, and any unexpected state delta fail closed.
QP calls the public tryLevelUp primitive pinned to token 0x0600051c; doLevelUp is never called because
it bypasses native gates. Quirk 176 is held unless item 486 is already ordinary: its native delivery
is deferred up to 30 seconds, gated by Sadistic Boss 225, and needs a separate filter/capacity lease
that this immediate transaction deliberately does not pretend to own.

Extension points and non-goals: the global scheduler may create source-exact marginal quotes and may
call CaptureCandidate/Plan/ExecuteOne after integration backtests. Static names, keyword weights,
consumable AP purchases, AP currency conversions, Hearts, Starter Pack, END checker service, and
automatic authority/config wiring do not belong here.
*/
using System;
using System.Collections.Generic;
using NGUInjector.Managers;

namespace NGUInjector.Autopilot
{
    internal enum ExactPermanentCurrency
    {
        ArbitraryPoints,
        QuestPoints
    }

    internal enum PermanentSpendValueEvidence
    {
        ShadowOnly,
        SourceExactDownstreamProjection
    }

    internal sealed class PermanentSpendMarginalQuote
    {
        internal readonly ExactPermanentCurrency Currency;
        internal readonly int NativeId;
        internal readonly long ExactCost;
        internal readonly string BoundaryFingerprint;
        internal readonly double ExactDownstreamSecondsSavedAfterFunding;
        internal readonly PermanentSpendValueEvidence Evidence;
        internal readonly string ProjectionId;

        internal PermanentSpendMarginalQuote(ExactPermanentCurrency currency, int nativeId,
            long exactCost, string boundaryFingerprint,
            double exactDownstreamSecondsSavedAfterFunding,
            PermanentSpendValueEvidence evidence, string projectionId)
        {
            Currency = currency;
            NativeId = nativeId;
            ExactCost = exactCost;
            BoundaryFingerprint = boundaryFingerprint ?? string.Empty;
            ExactDownstreamSecondsSavedAfterFunding = exactDownstreamSecondsSavedAfterFunding;
            Evidence = evidence;
            ProjectionId = projectionId ?? string.Empty;
        }
    }

    internal sealed class ExactPermanentSpendSnapshot
    {
        private readonly Dictionary<string, long> _state;

        internal readonly ExactPermanentCurrency Currency;
        internal readonly int NativeId;
        internal readonly string NativeMethodName;
        internal readonly int NativeMetadataToken;
        internal readonly string GameSha256;
        internal readonly Guid GameMvid;
        internal readonly long Balance;
        internal readonly long ExactCost;
        internal readonly int AmbientApId;
        internal readonly string AmbientApName;
        internal readonly bool Eligible;
        internal readonly string HoldReason;
        internal readonly bool EndItemPhysicallyPresent;
        internal readonly LootCapacityProof EndItemCapacity;

        internal ExactPermanentSpendSnapshot(ExactPermanentCurrency currency, int nativeId,
            string nativeMethodName, int nativeMetadataToken, string gameSha256, Guid gameMvid,
            long balance, long exactCost, int ambientApId, string ambientApName,
            IDictionary<string, long> state, bool eligible, string holdReason,
            bool endItemPhysicallyPresent, LootCapacityProof endItemCapacity)
        {
            Currency = currency;
            NativeId = nativeId;
            NativeMethodName = nativeMethodName ?? string.Empty;
            NativeMetadataToken = nativeMetadataToken;
            GameSha256 = NormalizeHash(gameSha256);
            GameMvid = gameMvid;
            Balance = balance;
            ExactCost = exactCost;
            AmbientApId = ambientApId;
            AmbientApName = ambientApName ?? string.Empty;
            Eligible = eligible;
            HoldReason = holdReason ?? string.Empty;
            EndItemPhysicallyPresent = endItemPhysicallyPresent;
            EndItemCapacity = endItemCapacity;
            _state = new Dictionary<string, long>(StringComparer.Ordinal);
            if (state != null)
                foreach (var pair in state) _state.Add(pair.Key, pair.Value);
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

        internal Dictionary<string, long> StateCopy()
        {
            return new Dictionary<string, long>(_state, StringComparer.Ordinal);
        }

        internal bool TryGet(string key, out long value)
        {
            return _state.TryGetValue(key ?? string.Empty, out value);
        }

        internal string BoundaryFingerprint()
        {
            var keys = new string[_state.Count];
            _state.Keys.CopyTo(keys, 0);
            Array.Sort(keys, StringComparer.Ordinal);
            var text = GameSha256 + "|" + GameMvid + "|" + Currency + ":" + NativeId
                       + "|method=" + NativeMethodName + "/0x"
                       + NativeMetadataToken.ToString("x8") + "|balance=" + Balance
                       + "|cost=" + ExactCost + "|selector=" + AmbientApId + ":"
                       + AmbientApName + "|end=" + EndItemPhysicallyPresent;
            if (EndItemCapacity != null)
                text += "|capacity=" + EndItemCapacity.UsableStart + ":"
                        + EndItemCapacity.UsableEnd + ":"
                        + EndItemCapacity.UsableFreeSlotCount + ":"
                        + EndItemCapacity.RequiredFreeSlots;
            for (var i = 0; i < keys.Length; i++)
                text += "|" + keys[i] + "=" + _state[keys[i]];
            return text;
        }

        internal bool ExactBoundaryEquals(ExactPermanentSpendSnapshot other)
        {
            if (other == null || Currency != other.Currency || NativeId != other.NativeId
                || NativeMetadataToken != other.NativeMetadataToken || GameMvid != other.GameMvid
                || Balance != other.Balance || ExactCost != other.ExactCost
                || AmbientApId != other.AmbientApId
                || !string.Equals(NativeMethodName, other.NativeMethodName, StringComparison.Ordinal)
                || !string.Equals(GameSha256, other.GameSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(AmbientApName, other.AmbientApName, StringComparison.Ordinal)
                || EndItemPhysicallyPresent != other.EndItemPhysicallyPresent
                || _state.Count != other._state.Count)
                return false;
            foreach (var pair in _state)
            {
                long value;
                if (!other._state.TryGetValue(pair.Key, out value) || value != pair.Value)
                    return false;
            }
            if (EndItemCapacity == null || other.EndItemCapacity == null)
                return EndItemCapacity == null && other.EndItemCapacity == null;
            return EndItemCapacity.Admitted == other.EndItemCapacity.Admitted
                   && EndItemCapacity.UsableStart == other.EndItemCapacity.UsableStart
                   && EndItemCapacity.UsableEnd == other.EndItemCapacity.UsableEnd
                   && EndItemCapacity.UsableFreeSlotCount
                      == other.EndItemCapacity.UsableFreeSlotCount
                   && EndItemCapacity.RequiredFreeSlots
                      == other.EndItemCapacity.RequiredFreeSlots;
        }

        private static string NormalizeHash(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty
                : value.Trim().Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    internal sealed class ExactPermanentSpendPlan
    {
        internal readonly ExactPermanentSpendSnapshot Before;
        internal readonly PermanentSpendMarginalQuote Quote;
        internal readonly long Reserve;

        internal ExactPermanentSpendPlan(ExactPermanentSpendSnapshot before,
            PermanentSpendMarginalQuote quote, long reserve)
        {
            Before = before;
            Quote = quote;
            Reserve = reserve;
        }
    }

    internal sealed class ExactPermanentSpendPlanResult
    {
        internal readonly ExactPermanentSpendPlan Plan;
        internal readonly string Reason;
        internal bool Planned { get { return Plan != null; } }

        internal ExactPermanentSpendPlanResult(ExactPermanentSpendPlan plan, string reason)
        {
            Plan = plan;
            Reason = reason ?? string.Empty;
        }
    }

    internal static class ExactPermanentSpendPlanner
    {
        /*
        VALUE ADMISSION

        A quote is bound to the full live fingerprint and exact native cost. Its value is terminal
        seconds saved after funding, already including the global scheduler's opportunity cost.
        This layer deliberately has no item-name, ID-order, or static multiplier fallback.
        */
        internal static ExactPermanentSpendPlanResult Choose(
            IEnumerable<ExactPermanentSpendSnapshot> candidates,
            IEnumerable<PermanentSpendMarginalQuote> quotes,
            long apReserve, long qpReserve)
        {
            if (apReserve < 0L || qpReserve < 0L)
                return Held("Permanent currency reserves must be nonnegative.");
            var quoteList = quotes == null
                ? new List<PermanentSpendMarginalQuote>()
                : new List<PermanentSpendMarginalQuote>(quotes);
            ExactPermanentSpendPlan best = null;
            if (candidates != null)
                foreach (var candidate in candidates)
                {
                    if (candidate == null || !candidate.IsAuditedBuild || !candidate.Eligible
                        || candidate.ExactCost <= 0L)
                        continue;
                    var reserve = candidate.Currency == ExactPermanentCurrency.ArbitraryPoints
                        ? apReserve : qpReserve;
                    if (candidate.Balance < candidate.ExactCost
                        || candidate.Balance - candidate.ExactCost < reserve)
                        continue;
                    PermanentSpendMarginalQuote quote = null;
                    for (var i = 0; i < quoteList.Count; i++)
                    {
                        var proposed = quoteList[i];
                        if (proposed != null && proposed.Currency == candidate.Currency
                            && proposed.NativeId == candidate.NativeId
                            && proposed.ExactCost == candidate.ExactCost
                            && string.Equals(proposed.BoundaryFingerprint,
                                candidate.BoundaryFingerprint(), StringComparison.Ordinal))
                        {
                            quote = proposed;
                            break;
                        }
                    }
                    if (quote == null
                        || quote.Evidence != PermanentSpendValueEvidence.SourceExactDownstreamProjection
                        || string.IsNullOrEmpty(quote.ProjectionId)
                        || double.IsNaN(quote.ExactDownstreamSecondsSavedAfterFunding)
                        || double.IsInfinity(quote.ExactDownstreamSecondsSavedAfterFunding)
                        || quote.ExactDownstreamSecondsSavedAfterFunding <= 0.0)
                        continue;
                    if (best == null
                        || quote.ExactDownstreamSecondsSavedAfterFunding
                           > best.Quote.ExactDownstreamSecondsSavedAfterFunding
                        || quote.ExactDownstreamSecondsSavedAfterFunding.Equals(
                               best.Quote.ExactDownstreamSecondsSavedAfterFunding)
                           && candidate.ExactCost < best.Before.ExactCost
                        || quote.ExactDownstreamSecondsSavedAfterFunding.Equals(
                               best.Quote.ExactDownstreamSecondsSavedAfterFunding)
                           && candidate.ExactCost == best.Before.ExactCost
                           && candidate.NativeId < best.Before.NativeId)
                        best = new ExactPermanentSpendPlan(candidate, quote, reserve);
                }
            return best == null
                ? Held("No affordable source-exact permanent atom has a fresh positive downstream-seconds quote.")
                : new ExactPermanentSpendPlanResult(best, string.Empty);
        }

        private static ExactPermanentSpendPlanResult Held(string reason)
        {
            return new ExactPermanentSpendPlanResult(null, reason);
        }
    }

    internal enum ExactSpendInvocationStatus
    {
        Held,
        Invoked,
        ThrewAfterDispatch
    }

    internal sealed class ExactSpendInvocation
    {
        internal readonly ExactSpendInvocationStatus Status;
        internal readonly string BindingKey;
        internal readonly string Reason;
        internal readonly Exception Exception;

        internal ExactSpendInvocation(ExactSpendInvocationStatus status, string bindingKey,
            string reason, Exception exception)
        {
            Status = status;
            BindingKey = bindingKey ?? string.Empty;
            Reason = reason ?? string.Empty;
            Exception = exception;
        }
    }

    internal interface IExactPermanentSpendRuntime
    {
        ExactPermanentSpendSnapshot Capture(ExactPermanentCurrency currency, int nativeId);
        ExactSpendInvocation Invoke(RootTransactionToken token,
            ExactPermanentSpendSnapshot plannedBoundary);
    }

    internal sealed class ExactPermanentSpendExecutionResult
    {
        internal readonly bool Attempted;
        internal readonly string Reason;
        internal readonly MutationResult<ExactPermanentSpendSnapshot,
            ExactPermanentSpendSnapshot> Mutation;

        internal ExactPermanentSpendExecutionResult(bool attempted, string reason,
            MutationResult<ExactPermanentSpendSnapshot, ExactPermanentSpendSnapshot> mutation)
        {
            Attempted = attempted;
            Reason = reason ?? string.Empty;
            Mutation = mutation;
        }
    }

    internal sealed class ExactPermanentSpendManager
    {
        private bool _attemptedThisPass;

        internal ExactPermanentSpendExecutionResult ExecuteOne(RootTransaction root,
            ExactPermanentSpendPlan plan, IExactPermanentSpendRuntime runtime,
            bool allowApSpending, bool allowQuirkSpending)
        {
            if (root == null || plan == null || runtime == null)
                return Held("Root, exact plan, and runtime are required.");
            if (plan.Before.Currency == ExactPermanentCurrency.ArbitraryPoints
                ? !allowApSpending : !allowQuirkSpending)
                return Held("The exact permanent-currency authority flag is disabled.");
            if (_attemptedThisPass)
                return Held("One permanent atom was already attempted; capture and replan.");
            _attemptedThisPass = true;
            var mutation = root.ExecuteChild(new ExactPermanentSpendIntent(plan, runtime));
            return new ExactPermanentSpendExecutionResult(true, mutation.Reason, mutation);
        }

        private static ExactPermanentSpendExecutionResult Held(string reason)
        {
            return new ExactPermanentSpendExecutionResult(false, reason, null);
        }

        private sealed class ExactPermanentSpendIntent :
            IMutationIntent<ExactPermanentSpendSnapshot, ExactSpendInvocation,
                ExactPermanentSpendSnapshot>
        {
            private readonly ExactPermanentSpendPlan _plan;
            private readonly IExactPermanentSpendRuntime _runtime;

            internal ExactPermanentSpendIntent(ExactPermanentSpendPlan plan,
                IExactPermanentSpendRuntime runtime)
            {
                _plan = plan;
                _runtime = runtime;
            }

            public string Id
            {
                get { return "permanent-" + _plan.Before.Currency + "/" + _plan.Before.NativeId; }
            }
            public MutationClass Class { get { return MutationClass.PermanentSpend; } }
            public MutationRisk Risk { get { return MutationRisk.Irreversible; } }
            public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
            public string BindingId
            {
                get
                {
                    return _plan.Before.NativeMethodName + "/token=0x"
                           + _plan.Before.NativeMetadataToken.ToString("x8");
                }
            }
            public bool Required { get { return true; } }
            public bool CanCompensate { get { return false; } }
            public bool CreatesNewEpoch { get { return false; } }
            public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

            public ExactPermanentSpendSnapshot CaptureBefore(MutationContext context)
            {
                return _runtime.Capture(_plan.Before.Currency, _plan.Before.NativeId);
            }

            public PreconditionResult CheckPreconditions(MutationContext context,
                ExactPermanentSpendSnapshot before)
            {
                if (before == null || !before.ExactBoundaryEquals(_plan.Before))
                    return PreconditionResult.Hold("Permanent-spend boundary changed; replan.");
                if (!before.IsAuditedBuild)
                    return PreconditionResult.Hold("Build or native purchase gate is no longer valid.");
                if (!ExactPermanentSpendTransitions.DeferredEndDeliveryIsSettled(before))
                    return PreconditionResult.Hold(
                        "END quirk 176 awaits a two-phase checker-delivery owner; item 486 is not settled ordinary inventory.");
                if (!before.Eligible)
                    return PreconditionResult.Hold("Native purchase gate is no longer valid.");
                if (before.Balance < before.ExactCost
                    || before.Balance - before.ExactCost < _plan.Reserve)
                    return PreconditionResult.Hold("Fresh debit would violate the exact reserve.");
                return PreconditionResult.Ready();
            }

            public ExactSpendInvocation Apply(MutationContext context,
                RootTransactionToken token, ExactPermanentSpendSnapshot before)
            {
                var invocation = _runtime.Invoke(token, before);
                if (invocation == null)
                    return new ExactSpendInvocation(ExactSpendInvocationStatus.Held,
                        BindingId, "Runtime returned no invocation evidence.", null);
                if (invocation.Status == ExactSpendInvocationStatus.ThrewAfterDispatch)
                    throw invocation.Exception ?? new InvalidOperationException(invocation.Reason);
                return invocation;
            }

            public VerificationResult<ExactPermanentSpendSnapshot> Verify(
                MutationContext context, ExactPermanentSpendSnapshot before,
                MutationApplyObservation<ExactSpendInvocation> apply)
            {
                var after = _runtime.Capture(before.Currency, before.NativeId);
                if (after == null)
                    return VerificationResult<ExactPermanentSpendSnapshot>.Failed(
                        "Post-spend capture is unavailable.");
                if (apply.Value == null
                    || apply.Value.Status != ExactSpendInvocationStatus.Invoked)
                    return VerificationResult<ExactPermanentSpendSnapshot>.Failed(
                        "The exact native purchase was not dispatched.");
                string reason;
                if (!ExactPermanentSpendTransitions.Verify(before, after, out reason))
                    return VerificationResult<ExactPermanentSpendSnapshot>.Failed(reason);
                return VerificationResult<ExactPermanentSpendSnapshot>.Satisfied(after,
                    "Exact permanent-currency debit and exact one-atom effect verified.");
            }

            public CompensationResult Compensate(MutationContext context, RecoveryToken token,
                ExactPermanentSpendSnapshot before,
                MutationApplyObservation<ExactSpendInvocation> apply)
            {
                return CompensationResult.NotSupported(
                    "Permanent AP/QP debits cannot be compensated by field rewriting.");
            }

            public bool BeforeStateMatches(ExactPermanentSpendSnapshot expected,
                ExactPermanentSpendSnapshot observed)
            {
                return expected != null && expected.ExactBoundaryEquals(observed);
            }

            public string FingerprintBefore(ExactPermanentSpendSnapshot before)
            {
                return before == null ? "<null>" : before.BoundaryFingerprint();
            }

            public string FingerprintAfter(ExactPermanentSpendSnapshot after)
            {
                return after == null ? "<null>" : after.BoundaryFingerprint();
            }
        }
    }

    internal static class ExactPermanentSpendTransitions
    {
        internal static bool DeferredEndDeliveryIsSettled(ExactPermanentSpendSnapshot snapshot)
        {
            return snapshot != null && (snapshot.Currency != ExactPermanentCurrency.QuestPoints
                || snapshot.NativeId != 176 || snapshot.EndItemPhysicallyPresent);
        }

        internal static bool Verify(ExactPermanentSpendSnapshot before,
            ExactPermanentSpendSnapshot after, out string reason)
        {
            reason = string.Empty;
            if (before == null || after == null || !after.IsAuditedBuild
                || before.Currency != after.Currency || before.NativeId != after.NativeId
                || before.NativeMetadataToken != after.NativeMetadataToken
                || !string.Equals(before.NativeMethodName, after.NativeMethodName,
                    StringComparison.Ordinal))
            {
                reason = "Build identity or native atom identity changed across the spend.";
                return false;
            }
            if (after.Balance != before.Balance - before.ExactCost)
            {
                reason = "Permanent currency did not decrease by the exact captured cost.";
                return false;
            }
            if (before.Currency == ExactPermanentCurrency.ArbitraryPoints
                && (before.AmbientApId != after.AmbientApId
                    || !string.Equals(before.AmbientApName, after.AmbientApName,
                        StringComparison.Ordinal)))
            {
                reason = "AP ID/name selectors were not restored exactly.";
                return false;
            }
            var expected = before.StateCopy();
            if (before.Currency == ExactPermanentCurrency.ArbitraryPoints)
            {
                PurchaseDescriptor descriptor;
                if (!PurchaseDescriptorCatalog.TryGetAp(before.NativeId, out descriptor)
                    || !PurchaseDescriptorCatalog.IsSourceExactLiveApPermanent(descriptor))
                {
                    reason = "AP descriptor is outside the source-exact persistent subset.";
                    return false;
                }
                var effects = descriptor.Effects();
                for (var i = 0; i < effects.Length; i++)
                {
                    long current;
                    if (!expected.TryGetValue(effects[i].StateKey, out current))
                    {
                        reason = "AP effect key was not captured: " + effects[i].StateKey;
                        return false;
                    }
                    if (effects[i].Kind == PurchaseEffectKind.SetOne)
                    {
                        if (current != 0L) { reason = "AP Boolean was already owned."; return false; }
                        expected[effects[i].StateKey] = 1L;
                    }
                    else if (effects[i].Kind == PurchaseEffectKind.ExactDelta)
                        expected[effects[i].StateKey] = checked(current + effects[i].Amount);
                    else
                    {
                        reason = "AP effect kind is not exact on the authorized surface.";
                        return false;
                    }
                }
            }
            else
            {
                var key = "quirk.level." + before.NativeId;
                long current;
                if (!expected.TryGetValue(key, out current))
                {
                    reason = "Target quirk level was not captured.";
                    return false;
                }
                expected[key] = checked(current + 1L);
            }
            var actual = after.StateCopy();
            if (expected.Count != actual.Count)
            {
                reason = "Captured persistent state-vector shape changed.";
                return false;
            }
            foreach (var pair in expected)
            {
                long value;
                if (!actual.TryGetValue(pair.Key, out value) || value != pair.Value)
                {
                    reason = "Unexpected permanent-state delta at " + pair.Key + ".";
                    return false;
                }
            }
            if (before.Currency == ExactPermanentCurrency.QuestPoints
                && before.NativeId == 176
                && after.EndItemPhysicallyPresent != before.EndItemPhysicallyPresent)
            {
                reason = "Quirk 176 unexpectedly conflated later checker delivery with purchase.";
                return false;
            }
            return true;
        }
    }

    internal sealed class LiveApQpPermanentSpendRuntime : IExactPermanentSpendRuntime
    {
        private readonly Character _character;
        private readonly ArbitraryController _apController;
        private readonly BeastQuestPerkController _qpController;
        private readonly NativeMutationAdapters _native;

        internal LiveApQpPermanentSpendRuntime(Character character,
            ArbitraryController apController, BeastQuestPerkController qpController)
        {
            _character = character;
            _apController = apController;
            _qpController = qpController;
            _native = NativeBindingRegistry.Create(typeof(Character).Assembly,
                Main.GameAssemblySha256).CreateMutationAdapters();
        }

        public ExactPermanentSpendSnapshot Capture(ExactPermanentCurrency currency, int nativeId)
        {
            try
            {
                return currency == ExactPermanentCurrency.ArbitraryPoints
                    ? CaptureAp(nativeId) : CaptureQp(nativeId);
            }
            catch
            {
                return null;
            }
        }

        public ExactSpendInvocation Invoke(RootTransactionToken token,
            ExactPermanentSpendSnapshot plannedBoundary)
        {
            if (token == null || plannedBoundary == null)
                return Held("A root token and exact planned boundary are required.");
            NativeInvocationResult result;
            string key;
            if (plannedBoundary.Currency == ExactPermanentCurrency.ArbitraryPoints)
            {
                PurchaseDescriptor descriptor;
                if (_apController == null
                    || !PurchaseDescriptorCatalog.TryGetAp(plannedBoundary.NativeId,
                        out descriptor)
                    || !PurchaseDescriptorCatalog.IsSourceExactLiveApPermanent(descriptor))
                    return Held("AP atom is outside the source-exact persistent subset.");
                key = descriptor.NativeBindingKey;
                result = _native.BuyApUpgrade(_apController, descriptor.NativeId,
                    descriptor.DisplayName, descriptor.NativeMethodName);
            }
            else
            {
                if (_qpController == null) return Held("Quirk controller is unavailable.");
                key = NativeBindingKeys.QuirkTryLevelUp;
                result = _native.BuyOneQuirkLevel(_qpController, plannedBoundary.NativeId);
            }
            if (result == null) return Held("Native adapter returned no result.");
            if (result.Status == NativeInvocationStatus.ThrewAfterInvocation)
                return new ExactSpendInvocation(ExactSpendInvocationStatus.ThrewAfterDispatch,
                    result.BindingKey, result.Reason, result.Exception);
            return result.ReturnedNormally
                ? new ExactSpendInvocation(ExactSpendInvocationStatus.Invoked,
                    result.BindingKey, result.Reason, null)
                : new ExactSpendInvocation(ExactSpendInvocationStatus.Held,
                    string.IsNullOrEmpty(result.BindingKey) ? key : result.BindingKey,
                    result.Reason, null);
        }

        private ExactPermanentSpendSnapshot CaptureAp(int id)
        {
            PurchaseDescriptor descriptor;
            if (_character == null || _character.arbitrary == null || _apController == null
                || !PurchaseDescriptorCatalog.TryGetAp(id, out descriptor)
                || !PurchaseDescriptorCatalog.IsSourceExactLiveApPermanent(descriptor))
                return null;
            var state = new Dictionary<string, long>(StringComparer.Ordinal);
            var effects = descriptor.Effects();
            for (var i = 0; i < effects.Length; i++)
            {
                long value;
                if (!TryReadApState(effects[i].StateKey, out value)) return null;
                state.Add(effects[i].StateKey, value);
            }
            var costState = ApCostState(id);
            if (costState == null) return null;
            var cost = descriptor.Cost.Evaluate(costState);
            var eligible = ApGate(id) && ApTransitionAvailable(descriptor, state);
            return new ExactPermanentSpendSnapshot(ExactPermanentCurrency.ArbitraryPoints,
                id, descriptor.NativeMethodName, descriptor.MetadataToken,
                Main.GameAssemblySha256,
                typeof(Character).Assembly.ManifestModule.ModuleVersionId,
                _character.arbitrary.curArbitraryPoints, cost,
                _apController.id, _apController.itemName, state, eligible,
                eligible ? string.Empty : "AP feature gate is locked or atom is already owned/capped.",
                false, null);
        }

        private ExactPermanentSpendSnapshot CaptureQp(int id)
        {
            if (_character == null || _character.beastQuest == null || _qpController == null
                || _character.beastQuest.quirkLevel == null || id < 0
                || id >= _character.beastQuest.quirkLevel.Count
                || _qpController.maxLevel == null || id >= _qpController.maxLevel.Count
                || _qpController.cost == null || id >= _qpController.cost.Count
                || _qpController.quirkDifficultyReq == null
                || id >= _qpController.quirkDifficultyReq.Count
                || _qpController.quirkType == null || id >= _qpController.quirkType.Count)
                return null;
            var state = new Dictionary<string, long>(StringComparer.Ordinal);
            for (var i = 0; i < _character.beastQuest.quirkLevel.Count; i++)
                state.Add("quirk.level." + i, _character.beastQuest.quirkLevel[i]);
            var cost = _qpController.quirkCost(id);
            var level = _character.beastQuest.quirkLevel[id];
            var cap = _qpController.capLevel(id);
            var gate = level < cap && cost > 0L
                       && _character.settings.rebirthDifficulty
                          >= _qpController.quirkDifficultyReq[id]
                       && QuirkFeatureGate((int)_qpController.quirkType[id]);
            var topology = id == 176
                ? InventoryManager.CaptureOrdinaryTopology(_character) : null;
            var present = id == 176 && topology != null
                          && topology.CountOrdinaryItem(486) > 0;
            LootCapacityProof capacity = null;
            if (id == 176 && !present)
            {
                if (topology != null)
                    capacity = LootCapacity.ProveOrdinary(topology,
                        LootCapacityRequirement.ExactUniqueDelivery(
                            "end-quirk-176-item-486", 0, 1, 0));
                // tryLevelUp commits QP immediately, but item 486 arrives later from the native
                // 30-second checker only after Sadistic Boss 225. Until a persistent two-phase
                // owner reserves the slot and filter across that delay, exact immediate authority
                // must remain closed even when a point-in-time capacity proof happens to pass.
                gate = false;
            }
            return new ExactPermanentSpendSnapshot(ExactPermanentCurrency.QuestPoints,
                id, "BeastQuestPerkController.tryLevelUp", 0x0600051c,
                Main.GameAssemblySha256,
                typeof(Character).Assembly.ManifestModule.ModuleVersionId,
                _character.beastQuest.quirkPoints, cost, -1, string.Empty,
                state, gate, gate ? string.Empty
                    : "Quirk cap, cost, difficulty, feature, or deferred END settlement gate is closed.",
                present, capacity);
        }

        private PurchaseCostState ApCostState(int id)
        {
            switch (id)
            {
                case 15:
                    return PurchaseCostState.ApInventory(_character.arbitrary.inventorySpaces,
                        _character.arbitrary.boughtNewbiePack);
                case 25: return PurchaseCostState.WithCounter(_character.arbitrary.curLoadoutSlots);
                case 28: return PurchaseCostState.WithCounter(_character.arbitrary.beardSlots);
                case 40: return PurchaseCostState.WithCounter(_character.arbitrary.diggerSlots);
                case 41: return PurchaseCostState.WithCounter(_character.arbitrary.macguffinSlots);
                case 69: return PurchaseCostState.WithCounter(_character.arbitrary.invMergeSlots);
                case 75: return PurchaseCostState.WithCounter(_character.arbitrary.deckSpaceBought);
                case 76: return PurchaseCostState.WithCounter(_character.arbitrary.mayoGenSlots);
                default: return PurchaseCostState.Fixed();
            }
        }

        private bool ApTransitionAvailable(PurchaseDescriptor descriptor,
            IDictionary<string, long> state)
        {
            var effects = descriptor.Effects();
            for (var i = 0; i < effects.Length; i++)
            {
                long value;
                if (!state.TryGetValue(effects[i].StateKey, out value)) return false;
                if (effects[i].Kind == PurchaseEffectKind.SetOne && value != 0L) return false;
                if (effects[i].Kind != PurchaseEffectKind.SetOne
                    && effects[i].Kind != PurchaseEffectKind.ExactDelta) return false;
            }
            return true;
        }

        private bool ApGate(int id)
        {
            switch (id)
            {
                case 21: return _character.settings.yggdrasilOn;
                case 28: return _character.settings.beardsOn;
                case 32: return _character.purchases.hasDaycare;
                case 39: return _character.settings.itopodOn;
                case 40: return _character.settings.diggersOn;
                case 41: return _character.achievements.achievementComplete != null
                                && _character.achievements.achievementComplete.Count > 145
                                && _character.achievements.achievementComplete[145];
                case 47: case 48: case 49: return _character.settings.beastOn;
                case 55: return _character.highestBoss >= 37;
                case 58: return _character.settings.nguOn;
                case 64: case 65: case 66: case 67: return _character.res3.res3On;
                case 68: return _character.wishes.wishesOn;
                case 69: return _character.purchases.hasAutoMerge;
                case 71: case 72: return _character.highestBoss >= 4;
                case 73: return _character.beastQuest.questsUnlocked;
                case 74: case 81:
                    return _character.settings.rebirthDifficulty >= difficulty.evil;
                case 75: case 76: case 77: return _character.cards.cardsOn;
                default: return true;
            }
        }

        private bool QuirkFeatureGate(int type)
        {
            if (type == 20) return _character.res3.res3On;
            if (type == 21) return _character.wishes.wishesOn;
            if (type == 23) return _character.cards.cardsOn;
            return true;
        }

        private bool TryReadApState(string key, out long value)
        {
            value = 0L;
            switch (key)
            {
                case "ap.hasImprovedLootFilter": value = _character.arbitrary.lootFilter ? 1 : 0; return true;
                case "ap.hasImprovedAutoBoostMerge": value = _character.arbitrary.improvedAutoBoostMerge ? 1 : 0; return true;
                case "ap.hasInstaTraining": value = _character.arbitrary.instaTrain ? 1 : 0; return true;
                case "ap.hasCustomEnergyPercent1": value = _character.purchases.hasCustomEnergyPercent1 ? 1 : 0; return true;
                case "ap.hasCustomMagicPercent1": value = _character.purchases.hasCustomMagicPercent1 ? 1 : 0; return true;
                case "ap.hasCustomEnergyPercent2": value = _character.purchases.hasCustomEnergyPercent2 ? 1 : 0; return true;
                case "ap.hasCustomMagicPercent2": value = _character.purchases.hasCustomMagicPercent2 ? 1 : 0; return true;
                case "ap.inventorySpaces": value = _character.arbitrary.inventorySpaces; return true;
                case "ap.hasAcc4": value = _character.arbitrary.hasAcc4 ? 1 : 0; return true;
                case "ap.hasAcc5": value = _character.arbitrary.hasAcc5 ? 1 : 0; return true;
                case "ap.hasAcc6": value = _character.arbitrary.hasAcc6 ? 1 : 0; return true;
                case "ap.hasAcc7": value = _character.arbitrary.hasAcc7 ? 1 : 0; return true;
                case "ap.hasAcc8": value = _character.arbitrary.hasAcc8 ? 1 : 0; return true;
                case "ap.hasAcc9": value = _character.arbitrary.hasAcc9 ? 1 : 0; return true;
                case "ap.hasYggdrasilReminder": value = _character.arbitrary.hasYggdrasilReminder ? 1 : 0; return true;
                case "ap.hasExtendedSpinBank": value = _character.arbitrary.hasExtendedSpinBank ? 1 : 0; return true;
                case "ap.loadoutSlots": value = _character.arbitrary.curLoadoutSlots; return true;
                case "ap.beardSlots": value = _character.arbitrary.beardSlots; return true;
                case "ap.hasCubeFilter": value = _character.arbitrary.hasCubeFilter ? 1 : 0; return true;
                case "ap.hasDaycareSpeed": value = _character.arbitrary.hasDaycareSpeed ? 1 : 0; return true;
                case "ap.hasLazyItopod": value = _character.arbitrary.boughtLazyITOPOD ? 1 : 0; return true;
                case "ap.diggerSlots": value = _character.arbitrary.diggerSlots; return true;
                case "ap.macguffinSlots": value = _character.arbitrary.macguffinSlots; return true;
                case "ap.hasQuestLight": value = _character.arbitrary.hasQuestLight ? 1 : 0; return true;
                case "ap.hasFasterQuests": value = _character.arbitrary.hasFasterQuests ? 1 : 0; return true;
                case "ap.hasExtendedQuestBank": value = _character.arbitrary.hasExtendedQuestBank ? 1 : 0; return true;
                case "ap.hasCustomIdleEnergyPercent1": value = _character.purchases.hasCustomIdleEnergyPercent1 ? 1 : 0; return true;
                case "ap.hasCustomIdleMagicPercent1": value = _character.purchases.hasCustomIdleMagicPercent1 ? 1 : 0; return true;
                case "ap.hasAutoNuke": value = _character.arbitrary.boughtAutoNuke ? 1 : 0; return true;
                case "ap.hasDaycareArt": value = _character.arbitrary.boughtDaycareArt ? 1 : 0; return true;
                case "ap.hasNguCapModifier": value = _character.arbitrary.hasNGUCapModifier ? 1 : 0; return true;
                case "ap.hasCustomRes3Percent1": value = _character.purchases.hasCustomRes3Percent1 ? 1 : 0; return true;
                case "ap.hasCustomRes3Percent2": value = _character.purchases.hasCustomRes3Percent2 ? 1 : 0; return true;
                case "ap.hasCustomIdleRes3Percent1": value = _character.purchases.hasCustomIdleRes3Percent1 ? 1 : 0; return true;
                case "ap.hasRes3NameGenerator": value = _character.arbitrary.res3NameGeneratorBought ? 1 : 0; return true;
                case "ap.hasFasterWishes": value = _character.arbitrary.wishSpeedBoster ? 1 : 0; return true;
                case "ap.inventoryMergeSlots": value = _character.arbitrary.invMergeSlots; return true;
                case "ap.hasAdventureLight": value = _character.arbitrary.advLightBought ? 1 : 0; return true;
                case "ap.hasAdventureAdvancer": value = _character.arbitrary.advAdvancerBought ? 1 : 0; return true;
                case "ap.hasGoToQuest": value = _character.arbitrary.goToQuestZoneBought ? 1 : 0; return true;
                case "ap.deckSpaces": value = _character.arbitrary.deckSpaceBought; return true;
                case "ap.mayoGenerators": value = _character.arbitrary.mayoGenSlots; return true;
                case "ap.hasTagSlot": value = _character.arbitrary.gotTagslot1 ? 1 : 0; return true;
                default: return false;
            }
        }

        private static ExactSpendInvocation Held(string reason)
        {
            return new ExactSpendInvocation(ExactSpendInvocationStatus.Held,
                string.Empty, reason, null);
        }
    }
}
