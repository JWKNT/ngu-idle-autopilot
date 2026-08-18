using System;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

Purpose: Move69Manager owns the pure scheduling policy and task-1 mutation intent for NGU Idle's
MOVE 69 terminal branch.  It keeps the private one-hour live timer useful while idle, fires only in
a short safe manual window, verifies the exact saved-use/timer/item transition, and recovers from a
lost item 481 by scheduling another legal use after count 69.

Mechanism: Evaluate distinguishes charging, ready, capacity/filter/combat holds, completion, and
unknown-binding read-only states.  A runtime hook captures the private component timer and invokes
one build-pinned native move while temporarily switching manual mode and restoring ambient mode and
filter state.  Move69MutationIntent executes that one atom through MutationCoordinator and accepts
only the exact timer reset, use-count delta, ambient restoration, and either exact item delivery or
the audited retryable full/filter loss.  Move69TimerTracker separately detects process/component
replacement, unexpected timer resets, successful uses, and cancellation-with-continued-charging.

Inputs and outputs: Inputs are exact runtime snapshots, ordinary-capacity proofs from LootCapacity,
the task-1 root transaction, and process/component identity tokens.  Outputs are decisions, lower-
bound ETAs, mutation results, delivery/retry classification, and restart-loss telemetry.  No game
type, reflection string, scheduler, config, save, or runtime file is accessed here.

Invariants and safety: The cooldown is exactly 3,600 live seconds and readiness is strictly greater
than 3,600.  Offline time never advances it.  Idle mode does not stop charging.  Uses below 69
increment by exactly one; uses at/above 69 remain at 69 and still retry item 481.  The 68->69 and
post-69 calls require one exact ordinary slot and a filter exemption.  Normal return is not success;
all permanent deltas are recaptured.  Process restart loses the unsaved timer and is never reported
as a cancellation or successful use.

Extension points and non-goals: The integration owner supplies an installed-build-pinned runtime
hook and explicitly enables live execution after backtesting.  Reboot orchestration may consume
DeferNonessentialRestart.  This file does not add native bindings, hold manual combat throughout the
69-hour route, credit offline/rebirth time, convert item 195/506, edit filters permanently, inject,
or restart the game.
*/
namespace NGUInjector.Managers
{
    internal enum Move69DecisionKind
    {
        Complete,
        Locked,
        ChargeInCurrentMode,
        HoldExactBinding,
        HoldCapacity,
        HoldFilter,
        HoldCombatWindow,
        HoldLiveAuthority,
        ReadyForOneUse
    }

    internal sealed class Move69Snapshot
    {
        internal readonly bool Unlocked;
        internal readonly int Used;
        internal readonly double TimerSeconds;
        internal readonly int OrdinaryItem481Count;
        internal readonly bool IdleMode;
        internal readonly bool MoveCheckPassed;
        internal readonly bool ExactBindingAvailable;
        internal readonly bool FilterAllowsItem481;
        internal readonly LootCapacityProof Capacity;
        internal readonly string ProcessEpoch;
        internal readonly string ComponentIdentity;
        internal readonly string FilterFingerprint;

        internal Move69Snapshot(bool unlocked, int used, double timerSeconds,
            int ordinaryItem481Count, bool idleMode, bool moveCheckPassed,
            bool exactBindingAvailable, bool filterAllowsItem481,
            LootCapacityProof capacity, string processEpoch, string componentIdentity,
            string filterFingerprint)
        {
            if (used < 0 || used > 69) throw new ArgumentOutOfRangeException("used");
            if (double.IsNaN(timerSeconds) || double.IsInfinity(timerSeconds)
                || timerSeconds < 0.0) throw new ArgumentOutOfRangeException("timerSeconds");
            if (ordinaryItem481Count < 0)
                throw new ArgumentOutOfRangeException("ordinaryItem481Count");
            Unlocked = unlocked;
            Used = used;
            TimerSeconds = timerSeconds;
            OrdinaryItem481Count = ordinaryItem481Count;
            IdleMode = idleMode;
            MoveCheckPassed = moveCheckPassed;
            ExactBindingAvailable = exactBindingAvailable;
            FilterAllowsItem481 = filterAllowsItem481;
            Capacity = capacity;
            ProcessEpoch = processEpoch ?? string.Empty;
            ComponentIdentity = componentIdentity ?? string.Empty;
            FilterFingerprint = filterFingerprint ?? string.Empty;
        }

        internal bool DeliveryExpectedOnNextUse
        {
            get { return Used >= 68; }
        }
    }

    internal sealed class Move69Decision
    {
        internal readonly Move69DecisionKind Kind;
        internal readonly double NextUseEtaSeconds;
        internal readonly double CompletionEtaSeconds;
        internal readonly int RemainingSuccessfulUses;
        internal readonly bool TemporarilySwitchToManual;
        internal readonly bool DeferNonessentialRestart;
        internal readonly double RestartLossSeconds;
        internal readonly string Reason;

        internal Move69Decision(Move69DecisionKind kind, double nextUseEtaSeconds,
            double completionEtaSeconds, int remainingSuccessfulUses,
            bool temporarilySwitchToManual, bool deferNonessentialRestart,
            double restartLossSeconds, string reason)
        {
            Kind = kind;
            NextUseEtaSeconds = nextUseEtaSeconds;
            CompletionEtaSeconds = completionEtaSeconds;
            RemainingSuccessfulUses = remainingSuccessfulUses;
            TemporarilySwitchToManual = temporarilySwitchToManual;
            DeferNonessentialRestart = deferNonessentialRestart;
            RestartLossSeconds = restartLossSeconds;
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class Move69ApplyResult
    {
        internal readonly bool InvocationAttempted;
        internal readonly string Detail;

        internal Move69ApplyResult(bool invocationAttempted, string detail)
        {
            InvocationAttempted = invocationAttempted;
            Detail = detail ?? string.Empty;
        }
    }

    internal interface IMove69Runtime
    {
        string ExactBindingId { get; }
        bool LiveMutationAuthority { get; }
        Move69Snapshot Capture();
        Move69ApplyResult InvokeOneUseWithTemporaryManualMode(RootTransactionToken token);
    }

    internal enum Move69DeliveryOutcome
    {
        NotAttempted,
        NoItemDueYet,
        Delivered,
        RetryAfterCooldown,
        Indeterminate
    }

    internal sealed class Move69ExecutionResult
    {
        internal readonly Move69Decision Decision;
        internal readonly MutationResult<Move69Snapshot, Move69Snapshot> Mutation;
        internal readonly Move69DeliveryOutcome Delivery;
        internal readonly string Reason;

        internal Move69ExecutionResult(Move69Decision decision,
            MutationResult<Move69Snapshot, Move69Snapshot> mutation,
            Move69DeliveryOutcome delivery, string reason)
        {
            Decision = decision;
            Mutation = mutation;
            Delivery = delivery;
            Reason = reason ?? string.Empty;
        }
    }

    internal enum Move69TimerEventKind
    {
        FirstObservation,
        Charging,
        CancelledButStillCharging,
        SuccessfulUse,
        ProcessRestartLostTimer,
        UnexpectedTimerReset
    }

    internal sealed class Move69TimerTelemetry
    {
        internal readonly Move69TimerEventKind Kind;
        internal readonly double TimerSeconds;
        internal readonly double EstimatedLostSeconds;
        internal readonly bool ScheduleCancelled;
        internal readonly string CancellationReason;
        internal readonly string Reason;

        internal Move69TimerTelemetry(Move69TimerEventKind kind, double timerSeconds,
            double estimatedLostSeconds, bool scheduleCancelled,
            string cancellationReason, string reason)
        {
            Kind = kind;
            TimerSeconds = timerSeconds;
            EstimatedLostSeconds = estimatedLostSeconds;
            ScheduleCancelled = scheduleCancelled;
            CancellationReason = cancellationReason ?? string.Empty;
            Reason = reason ?? string.Empty;
        }
    }

    /*
    TIMER/RESTART TELEMETRY

    A scheduler cancellation cancels only the pending action; it cannot and must not reset the
    private native timer.  Component/process identity replacement is the authoritative restart
    signal.  A same-component timer drop is a successful use only when the use counter advances or
    a post-69 delivery attempt is observed; otherwise it is surfaced as an unexpected reset.
    */
    internal sealed class Move69TimerTracker
    {
        private Move69Snapshot _previous;
        private bool _cancelled;
        private string _cancellationReason = string.Empty;

        internal void CancelScheduledUse(string reason)
        {
            _cancelled = true;
            _cancellationReason = reason ?? "scheduler cancellation";
        }

        internal void ResumeScheduledUse()
        {
            _cancelled = false;
            _cancellationReason = string.Empty;
        }

        internal Move69TimerTelemetry Observe(Move69Snapshot current)
        {
            if (current == null) throw new ArgumentNullException("current");
            if (_previous == null)
            {
                _previous = current;
                return Telemetry(Move69TimerEventKind.FirstObservation, current, 0.0,
                    "captured private MOVE69 timer and process/component identity");
            }

            var replaced = !string.Equals(_previous.ProcessEpoch, current.ProcessEpoch,
                               StringComparison.Ordinal)
                           || !string.Equals(_previous.ComponentIdentity,
                               current.ComponentIdentity, StringComparison.Ordinal);
            Move69TimerEventKind kind;
            double lost = 0.0;
            string reason;
            if (replaced)
            {
                kind = Move69TimerEventKind.ProcessRestartLostTimer;
                lost = _previous.TimerSeconds;
                reason = "private MOVE69 component was recreated; unsaved live charge was lost";
            }
            else if (current.TimerSeconds < _previous.TimerSeconds)
            {
                var successfulUse = current.Used == Math.Min(69, _previous.Used + 1)
                                    || (_previous.Used >= 69
                                        && current.OrdinaryItem481Count
                                        >= _previous.OrdinaryItem481Count);
                kind = successfulUse ? Move69TimerEventKind.SuccessfulUse
                    : Move69TimerEventKind.UnexpectedTimerReset;
                reason = successfulUse
                    ? "same-component timer reset agrees with a verified MOVE69 use"
                    : "same-component timer fell without the exact saved-use transition";
            }
            else if (_cancelled && current.TimerSeconds >= _previous.TimerSeconds)
            {
                kind = Move69TimerEventKind.CancelledButStillCharging;
                reason = "scheduled action is cancelled but native live charge continues";
            }
            else
            {
                kind = Move69TimerEventKind.Charging;
                reason = "private timer is charging in the current Adventure mode";
            }
            _previous = current;
            return Telemetry(kind, current, lost, reason);
        }

        private Move69TimerTelemetry Telemetry(Move69TimerEventKind kind,
            Move69Snapshot snapshot, double lost, string reason)
        {
            return new Move69TimerTelemetry(kind, snapshot.TimerSeconds, lost,
                _cancelled, _cancellationReason, reason);
        }
    }

    internal sealed class Move69Manager
    {
        internal const double CooldownSeconds = 3600.0;
        internal const int TerminalItemId = 481;

        private bool _liveExecutionEnabled;

        internal bool LiveExecutionEnabled
        {
            get { return _liveExecutionEnabled; }
        }

        internal void EnableLiveExecutionForIntegratedCaller(bool enabled)
        {
            _liveExecutionEnabled = enabled;
        }

        internal static LootCapacityRequirement TerminalDeliveryRequirement()
        {
            return LootCapacityRequirement.ExactUniqueDelivery(
                "move69-terminal-item-481", 0, 1, 0);
        }

        internal static Move69Decision Evaluate(Move69Snapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            if (!snapshot.Unlocked)
                return Decision(Move69DecisionKind.Locked, snapshot,
                    "Grey Liquid item 506 has not unlocked MOVE69");
            if (snapshot.OrdinaryItem481Count > 0)
                return new Move69Decision(Move69DecisionKind.Complete, 0.0, 0.0, 0,
                    false, false, 0.0, "ordinary terminal item 481 is physically present");
            if (!snapshot.ExactBindingAvailable)
                return Decision(Move69DecisionKind.HoldExactBinding, snapshot,
                    "installed-build MOVE69 binding is unavailable; remain read-only");
            if (snapshot.TimerSeconds <= CooldownSeconds)
                return Decision(Move69DecisionKind.ChargeInCurrentMode, snapshot,
                    snapshot.IdleMode
                        ? "idle Adventure still charges the private timer; do not hold manual mode"
                        : "manual Adventure charges the same private timer");
            if (snapshot.DeliveryExpectedOnNextUse)
            {
                if (snapshot.Capacity == null || !snapshot.Capacity.Admitted
                    || snapshot.Capacity.RequiredFreeSlots != 1)
                    return Decision(Move69DecisionKind.HoldCapacity, snapshot,
                        "68->69 and post-69 retries require one exact usable ordinary slot");
                if (!snapshot.FilterAllowsItem481)
                    return Decision(Move69DecisionKind.HoldFilter, snapshot,
                        "item 481 must be exempt from exact/misc filtering before the use");
            }
            if (!snapshot.MoveCheckPassed)
                return Decision(Move69DecisionKind.HoldCombatWindow, snapshot,
                    "wait for a safe live enemy/manual moveCheck window and fire MOVE69 first");
            var ready = Decision(Move69DecisionKind.ReadyForOneUse, snapshot,
                snapshot.IdleMode
                    ? "temporarily switch manual, invoke exactly one use, then restore idle"
                    : "invoke exactly one ready MOVE69 use in the current manual window");
            return new Move69Decision(ready.Kind, ready.NextUseEtaSeconds,
                ready.CompletionEtaSeconds, ready.RemainingSuccessfulUses,
                snapshot.IdleMode, ready.DeferNonessentialRestart,
                ready.RestartLossSeconds, ready.Reason);
        }

        internal Move69ExecutionResult ExecuteOneReadyUse(RootTransaction root,
            IMove69Runtime runtime)
        {
            if (root == null) throw new ArgumentNullException("root");
            if (runtime == null) throw new ArgumentNullException("runtime");
            var captured = runtime.Capture();
            var decision = Evaluate(captured);
            if (!_liveExecutionEnabled || !runtime.LiveMutationAuthority)
            {
                var hold = new Move69Decision(Move69DecisionKind.HoldLiveAuthority,
                    decision.NextUseEtaSeconds, decision.CompletionEtaSeconds,
                    decision.RemainingSuccessfulUses, false,
                    decision.DeferNonessentialRestart, decision.RestartLossSeconds,
                    "live MOVE69 execution remains disabled until integration/backtest authority");
                return new Move69ExecutionResult(hold, null,
                    Move69DeliveryOutcome.NotAttempted, hold.Reason);
            }
            if (decision.Kind != Move69DecisionKind.ReadyForOneUse)
                return new Move69ExecutionResult(decision, null,
                    Move69DeliveryOutcome.NotAttempted, decision.Reason);

            var intent = new Move69MutationIntent(runtime);
            var mutation = root.ExecuteChild(intent);
            var delivery = Move69DeliveryOutcome.Indeterminate;
            if (mutation.Kind == MutationResultKind.Committed && mutation.HasAfter)
            {
                if (!captured.DeliveryExpectedOnNextUse)
                    delivery = Move69DeliveryOutcome.NoItemDueYet;
                else if (mutation.After.OrdinaryItem481Count
                         == captured.OrdinaryItem481Count + 1)
                    delivery = Move69DeliveryOutcome.Delivered;
                else if (mutation.After.OrdinaryItem481Count
                         == captured.OrdinaryItem481Count)
                    delivery = Move69DeliveryOutcome.RetryAfterCooldown;
            }
            return new Move69ExecutionResult(decision, mutation, delivery,
                delivery == Move69DeliveryOutcome.RetryAfterCooldown
                    ? "use state committed but item delivery was lost; charge and retry at used 69"
                    : mutation.Reason);
        }

        private static Move69Decision Decision(Move69DecisionKind kind,
            Move69Snapshot snapshot, string reason)
        {
            var remaining = snapshot.OrdinaryItem481Count > 0 ? 0
                : snapshot.Used < 69 ? 69 - snapshot.Used : 1;
            var next = Math.Max(0.0, CooldownSeconds - snapshot.TimerSeconds);
            var completion = remaining == 0 ? 0.0
                : next + Math.Max(0, remaining - 1) * CooldownSeconds;
            var restartLoss = snapshot.Unlocked && snapshot.OrdinaryItem481Count == 0
                ? Math.Min(CooldownSeconds, snapshot.TimerSeconds) : 0.0;
            return new Move69Decision(kind, next, completion, remaining, false,
                restartLoss > 0.0, restartLoss, reason);
        }
    }

    /*
    EXACT MOVE INTENT

    Missing item 481 after a verified 68->69/post-69 use is an audited retryable delivery loss, not
    an indeterminate mutation: timer/use state is still exact and native permits another attempt.
    Any other item delta, selector/mode leak, epoch change, use-count mismatch, or nonzero timer is
    rejected as an irreversible partial and MutationCoordinator applies its quarantine policy.
    */
    internal sealed class Move69MutationIntent :
        IMutationIntent<Move69Snapshot, Move69ApplyResult, Move69Snapshot>
    {
        private readonly IMove69Runtime _runtime;

        internal Move69MutationIntent(IMove69Runtime runtime)
        {
            if (runtime == null) throw new ArgumentNullException("runtime");
            _runtime = runtime;
        }

        public string Id { get { return "move69.one-ready-use"; } }
        public MutationClass Class { get { return MutationClass.Adventure; } }
        public MutationRisk Risk { get { return MutationRisk.Irreversible; } }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId { get { return _runtime.ExactBindingId ?? string.Empty; } }
        public bool Required { get { return true; } }
        public bool CanCompensate { get { return false; } }
        public bool CreatesNewEpoch { get { return false; } }
        public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

        public Move69Snapshot CaptureBefore(MutationContext context)
        {
            return _runtime.Capture();
        }

        public PreconditionResult CheckPreconditions(MutationContext context,
            Move69Snapshot before)
        {
            if (!_runtime.LiveMutationAuthority)
                return PreconditionResult.Hold("runtime has not granted live MOVE69 authority");
            var decision = Move69Manager.Evaluate(before);
            return decision.Kind == Move69DecisionKind.ReadyForOneUse
                ? PreconditionResult.Ready() : PreconditionResult.Hold(decision.Reason);
        }

        public Move69ApplyResult Apply(MutationContext context,
            RootTransactionToken token, Move69Snapshot before)
        {
            return _runtime.InvokeOneUseWithTemporaryManualMode(token);
        }

        public VerificationResult<Move69Snapshot> Verify(MutationContext context,
            Move69Snapshot before, MutationApplyObservation<Move69ApplyResult> apply)
        {
            var after = _runtime.Capture();
            if (!SameEpochAndAmbient(before, after))
                return VerificationResult<Move69Snapshot>.Failed(
                    "process/component/mode/filter state changed across MOVE69 atom");
            var expectedUsed = before.Used < 69 ? before.Used + 1 : 69;
            if (after.Used != expectedUsed || after.TimerSeconds != 0.0)
                return VerificationResult<Move69Snapshot>.Failed(
                    "saved use count or private timer reset did not match the exact native effect");
            var itemDelta = after.OrdinaryItem481Count - before.OrdinaryItem481Count;
            if (!before.DeliveryExpectedOnNextUse && itemDelta != 0)
                return VerificationResult<Move69Snapshot>.Failed(
                    "item 481 changed before the 69th native use");
            if (before.DeliveryExpectedOnNextUse && itemDelta != 0 && itemDelta != 1)
                return VerificationResult<Move69Snapshot>.Failed(
                    "terminal item delta is neither exact delivery nor audited retryable loss");
            if (!apply.ReturnedNormally || apply.Value == null || !apply.Value.InvocationAttempted)
                return VerificationResult<Move69Snapshot>.Failed(
                    "runtime did not attest that the exact native invocation was attempted");
            return VerificationResult<Move69Snapshot>.Satisfied(after,
                before.DeliveryExpectedOnNextUse && itemDelta == 0
                    ? "exact use committed; item loss is retryable after another live cooldown"
                    : "exact MOVE69 timer/use/item transition verified");
        }

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            Move69Snapshot before, MutationApplyObservation<Move69ApplyResult> apply)
        {
            return CompensationResult.NotSupported("MOVE69 use count/timer effects are irreversible");
        }

        public bool BeforeStateMatches(Move69Snapshot expected, Move69Snapshot observed)
        {
            return expected != null && observed != null
                   && expected.Unlocked == observed.Unlocked
                   && expected.Used == observed.Used
                   && expected.TimerSeconds == observed.TimerSeconds
                   && expected.OrdinaryItem481Count == observed.OrdinaryItem481Count
                   && SameEpochAndAmbient(expected, observed);
        }

        public string FingerprintBefore(Move69Snapshot before)
        {
            return Fingerprint(before);
        }

        public string FingerprintAfter(Move69Snapshot after)
        {
            return Fingerprint(after);
        }

        private static bool SameEpochAndAmbient(Move69Snapshot left, Move69Snapshot right)
        {
            return left.Unlocked == right.Unlocked
                   && left.IdleMode == right.IdleMode
                   && left.FilterAllowsItem481 == right.FilterAllowsItem481
                   && left.ExactBindingAvailable == right.ExactBindingAvailable
                   && string.Equals(left.ProcessEpoch, right.ProcessEpoch,
                       StringComparison.Ordinal)
                   && string.Equals(left.ComponentIdentity, right.ComponentIdentity,
                       StringComparison.Ordinal)
                   && string.Equals(left.FilterFingerprint, right.FilterFingerprint,
                       StringComparison.Ordinal);
        }

        private static string Fingerprint(Move69Snapshot state)
        {
            if (state == null) return "<null>";
            return state.ProcessEpoch + "|" + state.ComponentIdentity + "|u=" + state.Used
                   + "|t=" + state.TimerSeconds.ToString("R",
                       System.Globalization.CultureInfo.InvariantCulture)
                   + "|i481=" + state.OrdinaryItem481Count + "|idle=" + state.IdleMode
                   + "|filter=" + state.FilterFingerprint;
        }
    }
}
