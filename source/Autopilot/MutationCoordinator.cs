using System;
using System.Collections.Generic;
using System.Threading;

/*
FILE PURPOSE

MutationCoordinator is the single typed protocol for executing native NGU Idle mutations. It owns
a nonzero, exclusive root transaction; admits explicit child intents through ExecutionSafety;
captures before-state; invokes native work; requires an explicit postcondition; and publishes a
closed result union. Inputs are an immutable root policy snapshot, an optional save/run epoch
fingerprint, and typed subsystem intents. Outputs are per-child MutationResult objects plus an
in-memory journal containing root/step identity, binding, fingerprints, phase, compensation proof,
and timestamps. A normal return (including Boolean true) is never a commit by itself. A failed or
thrown apply is recaptured, compensated only by a root- and intent-bound RecoveryToken, and otherwise
quarantined. No child can run without the exact active nonzero token; nested roots and cross-thread
native execution are rejected. This file deliberately contains no game-specific strategy, native
reflection, or subsystem postconditions. Managers provide those as intents, while later lifecycle
integration supplies the authoritative epoch fingerprint and closes roots on reset/load transitions.
*/
namespace NGUInjector.Autopilot
{
    internal enum MutationResultKind
    {
        Held,
        NoOpVerified,
        Pending,
        Committed,
        CommittedWithException,
        RejectedUnchanged,
        Compensated,
        Quarantined,
        Indeterminate
    }

    internal enum MutationPhase
    {
        Admit,
        Capture,
        Precondition,
        Revalidate,
        Apply,
        Settle,
        Verify,
        Recapture,
        Compensate,
        Journal
    }

    internal enum MutationPreconditionKind
    {
        Ready,
        Held,
        NoOpVerified
    }

    internal sealed class PreconditionResult
    {
        internal readonly MutationPreconditionKind Kind;
        internal readonly string Reason;

        private PreconditionResult(MutationPreconditionKind kind, string reason)
        {
            Kind = kind;
            Reason = reason ?? string.Empty;
        }

        internal static PreconditionResult Ready()
        {
            return new PreconditionResult(MutationPreconditionKind.Ready, string.Empty);
        }

        internal static PreconditionResult Hold(string reason)
        {
            return new PreconditionResult(MutationPreconditionKind.Held, reason);
        }

        internal static PreconditionResult AlreadySatisfied(string reason)
        {
            return new PreconditionResult(MutationPreconditionKind.NoOpVerified, reason);
        }
    }

    internal enum MutationSettleKind
    {
        Immediate,
        Deferred
    }

    internal sealed class SettlePolicy
    {
        internal readonly MutationSettleKind Kind;
        internal readonly string ObservationKey;
        internal readonly DateTime ExpiryUtc;

        private SettlePolicy(MutationSettleKind kind, string observationKey, DateTime expiryUtc)
        {
            Kind = kind;
            ObservationKey = observationKey ?? string.Empty;
            ExpiryUtc = expiryUtc;
        }

        internal static SettlePolicy Immediate()
        {
            return new SettlePolicy(MutationSettleKind.Immediate, string.Empty, DateTime.MinValue);
        }

        internal static SettlePolicy Deferred(string observationKey, DateTime expiryUtc)
        {
            if (string.IsNullOrEmpty(observationKey))
                throw new ArgumentException("A deferred mutation requires an observation key.",
                    "observationKey");
            if (expiryUtc.Kind == DateTimeKind.Local) expiryUtc = expiryUtc.ToUniversalTime();
            return new SettlePolicy(MutationSettleKind.Deferred, observationKey, expiryUtc);
        }
    }

    internal sealed class MutationApplyObservation<TApply>
    {
        internal readonly bool ReturnedNormally;
        internal readonly TApply Value;
        internal readonly Exception Exception;

        private MutationApplyObservation(bool returnedNormally, TApply value, Exception exception)
        {
            ReturnedNormally = returnedNormally;
            Value = value;
            Exception = exception;
        }

        internal static MutationApplyObservation<TApply> Returned(TApply value)
        {
            return new MutationApplyObservation<TApply>(true, value, null);
        }

        internal static MutationApplyObservation<TApply> Threw(Exception exception)
        {
            return new MutationApplyObservation<TApply>(false, default(TApply), exception);
        }
    }

    internal sealed class VerificationResult<TAfter>
    {
        internal readonly bool PostconditionSatisfied;
        internal readonly TAfter After;
        internal readonly string Reason;

        private VerificationResult(bool postconditionSatisfied, TAfter after, string reason)
        {
            PostconditionSatisfied = postconditionSatisfied;
            After = after;
            Reason = reason ?? string.Empty;
        }

        internal static VerificationResult<TAfter> Satisfied(TAfter after, string reason = null)
        {
            return new VerificationResult<TAfter>(true, after, reason);
        }

        internal static VerificationResult<TAfter> Failed(string reason)
        {
            return new VerificationResult<TAfter>(false, default(TAfter), reason);
        }
    }

    internal enum MutationCompensationKind
    {
        Restored,
        Failed,
        NotSupported
    }

    internal sealed class CompensationResult
    {
        internal readonly MutationCompensationKind Kind;
        internal readonly string Proof;

        private CompensationResult(MutationCompensationKind kind, string proof)
        {
            Kind = kind;
            Proof = proof ?? string.Empty;
        }

        internal static CompensationResult Restored(string proof)
        {
            return new CompensationResult(MutationCompensationKind.Restored, proof);
        }

        internal static CompensationResult Failed(string proof)
        {
            return new CompensationResult(MutationCompensationKind.Failed, proof);
        }

        internal static CompensationResult NotSupported(string reason)
        {
            return new CompensationResult(MutationCompensationKind.NotSupported, reason);
        }
    }

    /*
    CHILD INTENT CONTRACT

    CaptureBefore, CheckPreconditions, Apply, Verify, and Compensate execute in that order on the
    root's creating thread. Verify must observe native state independently of Apply's return value.
    BeforeStateMatches is the exact unchanged/rollback proof. Implementations must not hide other
    mutation classes inside Apply; prerequisites are separate child intents sharing the root token.
    */
    internal interface IMutationIntent<TBefore, TApply, TAfter>
    {
        string Id { get; }
        MutationClass Class { get; }
        MutationRisk Risk { get; }
        MutationOwner Owner { get; }
        string BindingId { get; }
        bool Required { get; }
        bool CanCompensate { get; }
        bool CreatesNewEpoch { get; }
        SettlePolicy Settle { get; }

        TBefore CaptureBefore(MutationContext context);
        PreconditionResult CheckPreconditions(MutationContext context, TBefore before);
        TApply Apply(MutationContext context, RootTransactionToken token, TBefore before);
        VerificationResult<TAfter> Verify(MutationContext context, TBefore before,
            MutationApplyObservation<TApply> apply);
        CompensationResult Compensate(MutationContext context, RecoveryToken token,
            TBefore before, MutationApplyObservation<TApply> apply);
        bool BeforeStateMatches(TBefore expected, TBefore observed);
        string FingerprintBefore(TBefore before);
        string FingerprintAfter(TAfter after);
    }

    internal sealed class MutationContext
    {
        internal readonly long RootTransactionId;
        internal readonly long StepId;
        internal readonly string EpochFingerprint;
        internal readonly object NativeContext;

        internal MutationContext(long rootTransactionId, long stepId, string epochFingerprint,
            object nativeContext)
        {
            RootTransactionId = rootTransactionId;
            StepId = stepId;
            EpochFingerprint = epochFingerprint ?? string.Empty;
            NativeContext = nativeContext;
        }
    }

    internal sealed class RootTransactionToken
    {
        internal readonly long RootTransactionId;
        internal readonly long StateVersion;
        internal readonly string EpochFingerprint;
        internal readonly DateTime StartedUtc;
        internal readonly int ManagedThreadId;
        internal readonly Guid CoordinatorId;

        internal RootTransactionToken(long rootTransactionId, long stateVersion,
            string epochFingerprint, Guid coordinatorId)
        {
            if (rootTransactionId <= 0) throw new ArgumentOutOfRangeException("rootTransactionId");
            RootTransactionId = rootTransactionId;
            StateVersion = stateVersion;
            EpochFingerprint = epochFingerprint ?? string.Empty;
            CoordinatorId = coordinatorId;
            StartedUtc = DateTime.UtcNow;
            ManagedThreadId = Thread.CurrentThread.ManagedThreadId;
        }
    }

    internal sealed class RecoveryToken
    {
        internal readonly long RootTransactionId;
        internal readonly long StepId;
        internal readonly string IntentId;
        internal readonly string EpochFingerprint;
        internal readonly Guid CoordinatorId;

        internal RecoveryToken(RootTransactionToken root, long stepId, string intentId)
        {
            RootTransactionId = root.RootTransactionId;
            StepId = stepId;
            IntentId = intentId ?? string.Empty;
            EpochFingerprint = root.EpochFingerprint;
            CoordinatorId = root.CoordinatorId;
        }
    }

    internal abstract class MutationResult
    {
        internal long RootTransactionId;
        internal long StepId;
        internal string IntentId;
        internal MutationClass Class;
        internal MutationRisk Risk;
        internal MutationOwner Owner;
        internal string BindingId;
        internal MutationResultKind Kind;
        internal MutationPhase Phase;
        internal string Reason;
        internal string ExceptionDetail;
        internal string BeforeFingerprint;
        internal string AfterFingerprint;
        internal string CompensationProof;
        internal string ObservationKey;
        internal DateTime PendingExpiryUtc;
        internal DateTime StartedUtc;
        internal DateTime FinishedUtc;
        internal bool Required;

        // The audit intentionally defines this narrowly. CommittedWithException is observable and
        // must not be retried blindly, but a required bundle needs an explicit reconciliation step.
        internal bool RequiredStepSatisfied
        {
            get
            {
                return Kind == MutationResultKind.Committed
                       || Kind == MutationResultKind.NoOpVerified;
            }
        }
    }

    internal sealed class MutationResult<TBefore, TAfter> : MutationResult
    {
        internal bool HasBefore;
        internal TBefore Before;
        internal bool HasAfter;
        internal TAfter After;
    }

    internal sealed class MutationJournalEntry
    {
        internal readonly long RootTransactionId;
        internal readonly long StepId;
        internal readonly string IntentId;
        internal readonly MutationClass Class;
        internal readonly MutationRisk Risk;
        internal readonly MutationOwner Owner;
        internal readonly string BindingId;
        internal readonly MutationResultKind Kind;
        internal readonly MutationPhase Phase;
        internal readonly string EpochFingerprint;
        internal readonly string BeforeFingerprint;
        internal readonly string AfterFingerprint;
        internal readonly string CompensationProof;
        internal readonly string Reason;
        internal readonly string ExceptionDetail;
        internal readonly DateTime StartedUtc;
        internal readonly DateTime FinishedUtc;

        internal MutationJournalEntry(MutationResult result, string epochFingerprint)
        {
            RootTransactionId = result.RootTransactionId;
            StepId = result.StepId;
            IntentId = result.IntentId ?? string.Empty;
            Class = result.Class;
            Risk = result.Risk;
            Owner = result.Owner;
            BindingId = result.BindingId ?? string.Empty;
            Kind = result.Kind;
            Phase = result.Phase;
            EpochFingerprint = epochFingerprint ?? string.Empty;
            BeforeFingerprint = result.BeforeFingerprint ?? string.Empty;
            AfterFingerprint = result.AfterFingerprint ?? string.Empty;
            CompensationProof = result.CompensationProof ?? string.Empty;
            Reason = result.Reason ?? string.Empty;
            ExceptionDetail = result.ExceptionDetail ?? string.Empty;
            StartedUtc = result.StartedUtc;
            FinishedUtc = result.FinishedUtc;
        }
    }

    internal enum RootBeginStatus
    {
        Begun,
        Held
    }

    internal sealed class RootBeginResult
    {
        internal readonly RootBeginStatus Status;
        internal readonly RootTransaction Transaction;
        internal readonly string Reason;

        internal RootBeginResult(RootBeginStatus status, RootTransaction transaction, string reason)
        {
            Status = status;
            Transaction = transaction;
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class RootTransaction : IDisposable
    {
        private readonly MutationCoordinator _coordinator;
        private readonly ExecutionCycle _cycle;
        private readonly List<MutationResult> _results = new List<MutationResult>();
        private bool _closed;

        internal readonly RootTransactionToken Token;
        internal readonly string Name;

        internal RootTransaction(MutationCoordinator coordinator, ExecutionCycle cycle,
            RootTransactionToken token, string name)
        {
            _coordinator = coordinator;
            _cycle = cycle;
            Token = token;
            Name = name ?? string.Empty;
        }

        internal long Id { get { return Token.RootTransactionId; } }
        internal bool IsClosed { get { return _closed; } }

        internal IList<MutationResult> Results
        {
            get { return _results.AsReadOnly(); }
        }

        internal bool RequiredStepsSatisfied
        {
            get
            {
                foreach (var result in _results)
                    if (result.Required && !result.RequiredStepSatisfied) return false;
                return true;
            }
        }

        internal MutationResult<TBefore, TAfter> ExecuteChild<TBefore, TApply, TAfter>(
            IMutationIntent<TBefore, TApply, TAfter> intent, object nativeContext = null)
        {
            return _coordinator.ExecuteChild(Token, intent, nativeContext);
        }

        internal void Record(MutationResult result)
        {
            _results.Add(result);
        }

        internal void CloseFromCoordinator()
        {
            if (_closed) return;
            _closed = true;
            _cycle.Dispose();
        }

        public void Dispose()
        {
            _coordinator.EndRoot(this);
        }
    }

    internal sealed class MutationCoordinator
    {
        private readonly object _gate = new object();
        private readonly Guid _coordinatorId = Guid.NewGuid();
        private readonly Func<string> _epochFingerprintProvider;
        private readonly List<MutationJournalEntry> _journal =
            new List<MutationJournalEntry>();
        private readonly Dictionary<MutationClass, string> _quarantines =
            new Dictionary<MutationClass, string>();
        private RootTransaction _activeRoot;
        private long _stepSequence;
        private string _globalQuarantineReason = string.Empty;

        private static readonly object SharedEpochGate = new object();
        private static Func<string> _sharedEpochFingerprintProvider;
        internal static readonly MutationCoordinator Shared = new MutationCoordinator(
            ReadSharedEpochFingerprint);

        internal static void BindSharedEpochProvider(Func<string> provider)
        {
            lock (SharedEpochGate) _sharedEpochFingerprintProvider = provider;
        }

        private static string ReadSharedEpochFingerprint()
        {
            Func<string> provider;
            lock (SharedEpochGate) provider = _sharedEpochFingerprintProvider;
            if (provider == null)
                throw new InvalidOperationException(
                    "the shared mutation coordinator has no bound game-epoch provider");
            var fingerprint = provider() ?? string.Empty;
            if (fingerprint.Length == 0)
                throw new InvalidOperationException(
                    "the bound game epoch has not published a fingerprint");
            return fingerprint;
        }

        internal MutationCoordinator(Func<string> epochFingerprintProvider = null)
        {
            _epochFingerprintProvider = epochFingerprintProvider;
        }

        internal RootBeginResult BeginRoot(string name, AutopilotConfig config)
        {
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(_globalQuarantineReason))
                    return new RootBeginResult(RootBeginStatus.Held, null,
                        "GlobalQuarantine: " + _globalQuarantineReason);
                if (_activeRoot != null && !_activeRoot.IsClosed)
                    return new RootBeginResult(RootBeginStatus.Held, null,
                        "NestedRootTransaction: root " + _activeRoot.Id + " is already active");

                string epochFingerprint;
                try
                {
                    epochFingerprint = ReadEpochFingerprint();
                }
                catch (Exception ex)
                {
                    return new RootBeginResult(RootBeginStatus.Held, null,
                        "EpochCaptureFailed: " + DescribeException(ex));
                }

                ExecutionCycle cycle;
                string reason;
                if (!ExecutionSafety.TryBeginCycle(name, config, out cycle, out reason))
                    return new RootBeginResult(RootBeginStatus.Held, null, reason);
                if (cycle == null || cycle.CycleId <= 0)
                {
                    if (cycle != null) cycle.Dispose();
                    return new RootBeginResult(RootBeginStatus.Held, null,
                        "InvalidRootTransaction: a root ID must be greater than zero");
                }

                var token = new RootTransactionToken(cycle.CycleId, cycle.StateVersion,
                    epochFingerprint, _coordinatorId);
                _activeRoot = new RootTransaction(this, cycle, token, name);
                return new RootBeginResult(RootBeginStatus.Begun, _activeRoot, string.Empty);
            }
        }

        /*
        APPLY / VERIFY / RECOVER

        The coordinator never infers success from Apply. Immediate work reaches Committed only via
        VerificationResult.Satisfied while the root/epoch remains valid. Any failed postcondition
        recaptures exact before-state first; unchanged work is RejectedUnchanged, reversible partial
        work receives a narrowly scoped recovery token, and all other partial states are quarantined.
        */
        internal MutationResult<TBefore, TAfter> ExecuteChild<TBefore, TApply, TAfter>(
            RootTransactionToken token, IMutationIntent<TBefore, TApply, TAfter> intent,
            object nativeContext = null)
        {
            if (intent == null) throw new ArgumentNullException("intent");
            var started = DateTime.UtcNow;
            var stepId = NextStepId();
            var result = NewResult(token, stepId, intent, started);
            RootTransaction root;
            string invalidReason;
            if (!TryValidateToken(token, out root, out invalidReason))
                return Finish(root, token, result, MutationResultKind.Held, MutationPhase.Admit,
                    invalidReason, null);
            if (Thread.CurrentThread.ManagedThreadId != token.ManagedThreadId)
                return Finish(root, token, result, MutationResultKind.Held, MutationPhase.Admit,
                    "WrongThread: native mutation children must execute on their root thread", null);

            string quarantineReason;
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(_globalQuarantineReason))
                    quarantineReason = "GlobalQuarantine: " + _globalQuarantineReason;
                else if (_quarantines.TryGetValue(intent.Class, out quarantineReason))
                    quarantineReason = "ClassQuarantine: " + quarantineReason;
            }
            if (!string.IsNullOrEmpty(quarantineReason))
                return Finish(root, token, result, MutationResultKind.Held, MutationPhase.Admit,
                    quarantineReason, null);

            MutationLease lease;
            string admissionReason;
            if (!ExecutionSafety.TryAcquire(intent.Class, intent.Risk, intent.Owner, out lease,
                    out admissionReason))
                return Finish(root, token, result, MutationResultKind.Held, MutationPhase.Admit,
                    admissionReason, null);
            if (lease.RootTransactionId <= 0 || lease.RootTransactionId != token.RootTransactionId)
                return Finish(root, token, result, MutationResultKind.Held, MutationPhase.Admit,
                    "RootMismatch: the lease was not issued to this root transaction", null);
            result.Risk = lease.Risk;

            var context = new MutationContext(token.RootTransactionId, stepId,
                token.EpochFingerprint, nativeContext);
            TBefore before;
            try
            {
                before = intent.CaptureBefore(context);
                result.HasBefore = true;
                result.Before = before;
                result.BeforeFingerprint = SafeBeforeFingerprint(intent, before);
            }
            catch (Exception ex)
            {
                return Finish(root, token, result, MutationResultKind.Held, MutationPhase.Capture,
                    "Before-state capture failed; native Apply was not invoked", ex);
            }

            PreconditionResult precondition;
            try
            {
                precondition = intent.CheckPreconditions(context, before);
            }
            catch (Exception ex)
            {
                return Finish(root, token, result, MutationResultKind.RejectedUnchanged,
                    MutationPhase.Precondition,
                    "Precondition evaluation failed; captured state remains unchanged", ex);
            }
            if (precondition == null)
                return Finish(root, token, result, MutationResultKind.RejectedUnchanged,
                    MutationPhase.Precondition,
                    "Precondition returned no typed result; native Apply was not invoked", null);
            if (precondition.Kind == MutationPreconditionKind.Held)
                return Finish(root, token, result, MutationResultKind.Held,
                    MutationPhase.Precondition, precondition.Reason, null);
            if (precondition.Kind == MutationPreconditionKind.NoOpVerified)
                return Finish(root, token, result, MutationResultKind.NoOpVerified,
                    MutationPhase.Precondition, precondition.Reason, null);

            if (!lease.IsCurrent || !EpochMatches(token))
                return Finish(root, token, result, MutationResultKind.Held, MutationPhase.Revalidate,
                    "Root or game epoch changed after capture; native Apply was not invoked", null);

            MutationApplyObservation<TApply> apply;
            try
            {
                apply = MutationApplyObservation<TApply>.Returned(
                    intent.Apply(context, token, before));
            }
            catch (Exception ex)
            {
                apply = MutationApplyObservation<TApply>.Threw(ex);
            }

            SettlePolicy settle;
            try
            {
                settle = intent.Settle ?? SettlePolicy.Immediate();
            }
            catch (Exception ex)
            {
                return RecoverFailure(root, token, result, intent, context, before, apply,
                    "Settlement policy evaluation threw after Apply", ex);
            }
            if (apply.ReturnedNormally && settle.Kind == MutationSettleKind.Deferred)
            {
                if (!lease.IsCurrent || !EpochMatches(token))
                    return RecoverFailure(root, token, result, intent, context, before, apply,
                        "Epoch changed while starting deferred work", null);
                result.ObservationKey = settle.ObservationKey;
                result.PendingExpiryUtc = settle.ExpiryUtc;
                return Finish(root, token, result, MutationResultKind.Pending,
                    MutationPhase.Settle,
                    "Native work started; exact settlement is pending observation "
                    + settle.ObservationKey, null);
            }

            VerificationResult<TAfter> verification;
            try
            {
                verification = intent.Verify(context, before, apply);
            }
            catch (Exception ex)
            {
                return RecoverFailure(root, token, result, intent, context, before, apply,
                    "Postcondition verification threw", ex);
            }

            if (verification != null && verification.PostconditionSatisfied
                && (lease.IsCurrent && EpochMatches(token) || intent.CreatesNewEpoch))
            {
                result.HasAfter = true;
                result.After = verification.After;
                result.AfterFingerprint = SafeAfterFingerprint(intent, verification.After);
                var committed = Finish(root, token, result,
                    apply.ReturnedNormally ? MutationResultKind.Committed
                        : MutationResultKind.CommittedWithException,
                    MutationPhase.Verify, verification.Reason, apply.Exception);
                if (intent.CreatesNewEpoch) AbortRoot(root);
                return committed;
            }

            var failure = verification == null
                ? "Verify returned no typed postcondition result"
                : verification.Reason;
            if (verification != null && verification.PostconditionSatisfied)
                failure = "Postcondition was observed but the root/epoch was no longer valid";
            return RecoverFailure(root, token, result, intent, context, before, apply,
                string.IsNullOrEmpty(failure) ? "Exact postcondition was false" : failure,
                apply.Exception);
        }

        private MutationResult<TBefore, TAfter> RecoverFailure<TBefore, TApply, TAfter>(
            RootTransaction root, RootTransactionToken token,
            MutationResult<TBefore, TAfter> result,
            IMutationIntent<TBefore, TApply, TAfter> intent, MutationContext context,
            TBefore before, MutationApplyObservation<TApply> apply, string failure,
            Exception verificationException)
        {
            TBefore recaptured;
            try
            {
                recaptured = intent.CaptureBefore(context);
            }
            catch (Exception ex)
            {
                var detail = verificationException ?? apply.Exception ?? ex;
                QuarantineGlobal("Recapture failed after Apply for " + intent.Id);
                var indeterminate = Finish(root, token, result,
                    MutationResultKind.Indeterminate, MutationPhase.Recapture,
                    failure + "; state recapture failed: " + DescribeException(ex), detail);
                AbortRoot(root);
                return indeterminate;
            }

            bool unchanged;
            try
            {
                unchanged = intent.BeforeStateMatches(before, recaptured);
            }
            catch (Exception ex)
            {
                QuarantineGlobal("Unchanged-state proof failed after Apply for " + intent.Id);
                var indeterminate = Finish(root, token, result,
                    MutationResultKind.Indeterminate, MutationPhase.Recapture,
                    failure + "; unchanged-state proof threw", ex);
                AbortRoot(root);
                return indeterminate;
            }
            if (unchanged)
                return Finish(root, token, result, MutationResultKind.RejectedUnchanged,
                    MutationPhase.Recapture, failure + "; exact before-state was retained",
                    verificationException ?? apply.Exception);

            if (result.Risk == MutationRisk.Irreversible || !intent.CanCompensate)
            {
                QuarantineClass(intent.Class, "Partial/unrecognized state after " + intent.Id);
                return Finish(root, token, result, MutationResultKind.Quarantined,
                    MutationPhase.Recapture,
                    failure + "; state changed and no valid compensation exists",
                    verificationException ?? apply.Exception);
            }

            // ExecutionSafety state-version invalidation may be local to the failed apply. Recovery
            // remains authorized by the captured token, but it may never cross a save/run epoch.
            if (!EpochMatches(token))
            {
                QuarantineGlobal("Game epoch changed before compensation for " + intent.Id);
                var indeterminate = Finish(root, token, result,
                    MutationResultKind.Indeterminate, MutationPhase.Compensate,
                    failure + "; compensation refused across a game epoch", null);
                AbortRoot(root);
                return indeterminate;
            }

            var recovery = new RecoveryToken(token, result.StepId, intent.Id);
            CompensationResult compensation;
            try
            {
                compensation = intent.Compensate(context, recovery, before, apply);
            }
            catch (Exception ex)
            {
                QuarantineClass(intent.Class, "Compensation threw for " + intent.Id);
                return Finish(root, token, result, MutationResultKind.Quarantined,
                    MutationPhase.Compensate, failure + "; compensation threw", ex);
            }
            if (compensation == null || compensation.Kind != MutationCompensationKind.Restored)
            {
                var proof = compensation == null ? "no typed compensation result" : compensation.Proof;
                result.CompensationProof = proof;
                QuarantineClass(intent.Class, "Compensation failed for " + intent.Id);
                return Finish(root, token, result, MutationResultKind.Quarantined,
                    MutationPhase.Compensate, failure + "; " + proof, null);
            }

            result.CompensationProof = compensation.Proof;
            try
            {
                var restored = intent.CaptureBefore(context);
                if (intent.BeforeStateMatches(before, restored))
                    return Finish(root, token, result, MutationResultKind.Compensated,
                        MutationPhase.Compensate, failure + "; exact before-state restored", null);
            }
            catch (Exception ex)
            {
                QuarantineClass(intent.Class, "Compensation proof threw for " + intent.Id);
                return Finish(root, token, result, MutationResultKind.Quarantined,
                    MutationPhase.Compensate,
                    failure + "; compensation returned normally but proof capture threw", ex);
            }

            QuarantineClass(intent.Class, "Compensation proof failed for " + intent.Id);
            return Finish(root, token, result, MutationResultKind.Quarantined,
                MutationPhase.Compensate,
                failure + "; compensation returned normally but exact restoration was false", null);
        }

        internal IList<MutationJournalEntry> SnapshotJournal()
        {
            lock (_gate) return new List<MutationJournalEntry>(_journal).AsReadOnly();
        }

        internal bool IsQuarantined(MutationClass mutationClass, out string reason)
        {
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(_globalQuarantineReason))
                {
                    reason = _globalQuarantineReason;
                    return true;
                }
                return _quarantines.TryGetValue(mutationClass, out reason);
            }
        }

        // Recovery UI/lifecycle code may call this only after independently reconciling native
        // state. Clearing a quarantine never authorizes work by itself; a fresh root is still needed.
        internal void ClearQuarantine(MutationClass mutationClass)
        {
            lock (_gate) _quarantines.Remove(mutationClass);
        }

        internal void ClearGlobalQuarantine()
        {
            lock (_gate) _globalQuarantineReason = string.Empty;
        }

        internal void EndRoot(RootTransaction root)
        {
            if (root == null) return;
            lock (_gate)
            {
                if (ReferenceEquals(_activeRoot, root)) _activeRoot = null;
                root.CloseFromCoordinator();
            }
        }

        private void AbortRoot(RootTransaction root)
        {
            EndRoot(root);
        }

        private bool TryValidateToken(RootTransactionToken token, out RootTransaction root,
            out string reason)
        {
            lock (_gate)
            {
                root = _activeRoot;
                if (token == null)
                {
                    reason = "NoActiveTransaction: a child intent requires a root token";
                    return false;
                }
                if (token.RootTransactionId <= 0)
                {
                    reason = "InvalidRootTransaction: root ID zero is never executable";
                    return false;
                }
                if (token.CoordinatorId != _coordinatorId || root == null || root.IsClosed
                    || !ReferenceEquals(root.Token, token)
                    || root.Id != token.RootTransactionId)
                {
                    reason = "StaleOrForeignRoot: the token is not the exact active root";
                    return false;
                }
                reason = string.Empty;
                return true;
            }
        }

        private MutationResult<TBefore, TAfter> NewResult<TBefore, TAfter, TApply>(
            RootTransactionToken token, long stepId,
            IMutationIntent<TBefore, TApply, TAfter> intent, DateTime started)
        {
            return new MutationResult<TBefore, TAfter>
            {
                RootTransactionId = token == null ? 0 : token.RootTransactionId,
                StepId = stepId,
                IntentId = intent.Id ?? string.Empty,
                Class = intent.Class,
                Risk = intent.Risk,
                Owner = intent.Owner,
                BindingId = intent.BindingId ?? string.Empty,
                Required = intent.Required,
                StartedUtc = started
            };
        }

        private MutationResult<TBefore, TAfter> Finish<TBefore, TAfter>(RootTransaction root,
            RootTransactionToken token, MutationResult<TBefore, TAfter> result,
            MutationResultKind kind, MutationPhase phase, string reason, Exception exception)
        {
            result.Kind = kind;
            result.Phase = phase;
            result.Reason = reason ?? string.Empty;
            result.ExceptionDetail = DescribeException(exception);
            result.FinishedUtc = DateTime.UtcNow;
            lock (_gate)
            {
                if (root != null) root.Record(result);
                _journal.Add(new MutationJournalEntry(result,
                    token == null ? string.Empty : token.EpochFingerprint));
            }
            return result;
        }

        private long NextStepId()
        {
            var next = Interlocked.Increment(ref _stepSequence);
            if (next > 0) return next;
            lock (_gate)
            {
                if (_stepSequence <= 0) _stepSequence = 1;
                return _stepSequence;
            }
        }

        private string ReadEpochFingerprint()
        {
            return _epochFingerprintProvider == null
                ? string.Empty : _epochFingerprintProvider() ?? string.Empty;
        }

        private bool EpochMatches(RootTransactionToken token)
        {
            try
            {
                return string.Equals(token.EpochFingerprint, ReadEpochFingerprint(),
                    StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private void QuarantineClass(MutationClass mutationClass, string reason)
        {
            lock (_gate) _quarantines[mutationClass] = reason ?? "unclassified mutation failure";
        }

        private void QuarantineGlobal(string reason)
        {
            lock (_gate) _globalQuarantineReason = reason ?? "unclassified epoch failure";
        }

        private static string SafeBeforeFingerprint<TBefore, TApply, TAfter>(
            IMutationIntent<TBefore, TApply, TAfter> intent, TBefore before)
        {
            try { return intent.FingerprintBefore(before) ?? string.Empty; }
            catch (Exception ex) { return "<fingerprint-error:" + DescribeException(ex) + ">"; }
        }

        private static string SafeAfterFingerprint<TBefore, TApply, TAfter>(
            IMutationIntent<TBefore, TApply, TAfter> intent, TAfter after)
        {
            try { return intent.FingerprintAfter(after) ?? string.Empty; }
            catch (Exception ex) { return "<fingerprint-error:" + DescribeException(ex) + ">"; }
        }

        private static string DescribeException(Exception exception)
        {
            return exception == null ? string.Empty
                : exception.GetType().Name + ": " + exception.Message;
        }
    }
}
