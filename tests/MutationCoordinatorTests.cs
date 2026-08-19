using System;
using System.Collections.Generic;

/*
FILE PURPOSE

This isolated executable fault-injection-tests the root mutation protocol without loading Unity,
the installed game assembly, a save, or runtime state. Minimal AutopilotConfig/Main stubs support
ExecutionSafety while a fake native integer supplies exact before/after fingerprints. The suite
proves nonzero exclusive roots, zero invocation without a root, child-token identity, typed journal
publication, postcondition-only commit, pending settlement, state-version revalidation, exception
classification, exact compensation, class/global quarantine, and refusal to compensate across a
save/run epoch. It is deliberately not project-wired here; the integration owner adds the source and
test command after all exclusive implementation tasks reconcile their APIs.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class AutopilotConfig
    {
        internal bool Enabled = true;
        internal string Mode = "full";
        internal bool AutoEnterGame = true;
        internal bool AllowLegacyFallback = true;
        internal bool ManageAllocations = true;
        internal bool ManageBosses = true;
        internal bool ManageAdventure = true;
        internal bool ManageInventory = true;
        internal bool ManageDiggers = true;
        internal bool ManageYggdrasil = true;
        internal bool ManageQuests = true;
        internal bool ManageWishes = true;
        internal bool ManageCards = true;
        internal bool ManageCooking = true;
        internal bool ManageMoneyPit = true;
        internal bool ManageDailySpin = true;
        internal bool ManageBloodMagic = true;
        internal bool ManageBeards = true;
        internal bool AllowExpSpending = true;
        internal bool AllowApSpending = true;
        internal bool AllowPerkSpending = true;
        internal bool AllowQuirkSpending = true;
        internal bool AllowCardYeeting = true;
        internal bool AllowRebirths = true;
        internal bool AllowChallenges = true;
        internal bool AllowDifficultyExecution;
        internal bool AllowEndSequence = true;

        internal bool IsDryRun { get { return Mode != "assist" && Mode != "full"; } }
        internal bool IsAssist { get { return Mode == "assist"; } }
        internal bool IsFull { get { return Mode == "full"; } }

        internal string ExecutionFingerprint()
        {
            return Enabled + "|" + Mode + "|" + ManageAllocations + "|" + ManageDiggers
                   + "|" + ManageInventory + "|" + AllowRebirths + "|" + AllowEndSequence;
        }
    }

    internal sealed class AutopilotManager
    {
        internal AutopilotConfig Config;
    }

    internal sealed class FakeNativeState
    {
        internal int Value;
        internal int ApplyCalls;
        internal int CompensationCalls;
        internal int CaptureCalls;
        internal string Epoch = "save-A/run-1";
    }

    internal enum FaultPoint
    {
        None,
        CaptureInitial,
        CaptureRecovery,
        Precondition,
        ApplyBeforeMutation,
        ApplyAfterMutation,
        Settle,
        Verify,
        Compensate
    }

    internal sealed class FakeIntent : IMutationIntent<int, bool, int>
    {
        private readonly FakeNativeState _state;

        internal FaultPoint Fault;
        internal int Target = 1;
        internal int AppliedValue = 1;
        internal bool ApplyReturn = true;
        internal bool ForceFalsePostcondition;
        internal bool Hold;
        internal bool AlreadySatisfied;
        internal bool InvalidateDuringPrecondition;
        internal bool InvalidateDuringApply;
        internal bool ChangeEpochDuringApply;
        internal bool CompensationClaimsFailure;
        internal bool Deferred;
        internal bool CreatesEpoch;
        internal MutationRisk IntentRisk = MutationRisk.FiniteResource;
        internal bool CompensationEnabled = true;
        internal MutationClass IntentClass = MutationClass.Allocation;
        internal string IntentName = "fake-allocation";

        internal FakeIntent(FakeNativeState state)
        {
            _state = state;
        }

        public string Id { get { return IntentName; } }
        public MutationClass Class { get { return IntentClass; } }
        public MutationRisk Risk { get { return IntentRisk; } }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId { get { return "fake.binding/v1"; } }
        public bool Required { get { return true; } }
        public bool CanCompensate { get { return CompensationEnabled; } }
        public bool CreatesNewEpoch { get { return CreatesEpoch; } }
        public SettlePolicy Settle
        {
            get
            {
                if (Fault == FaultPoint.Settle)
                    throw new InvalidOperationException("settle");
                return Deferred
                    ? SettlePolicy.Deferred("fake-observation", DateTime.UtcNow.AddMinutes(1))
                    : SettlePolicy.Immediate();
            }
        }

        public int CaptureBefore(MutationContext context)
        {
            _state.CaptureCalls++;
            if (Fault == FaultPoint.CaptureInitial && _state.CaptureCalls == 1)
                throw new InvalidOperationException("capture-initial");
            if (Fault == FaultPoint.CaptureRecovery && _state.CaptureCalls > 1)
                throw new InvalidOperationException("capture-recovery");
            return _state.Value;
        }

        public PreconditionResult CheckPreconditions(MutationContext context, int before)
        {
            if (Fault == FaultPoint.Precondition)
                throw new InvalidOperationException("precondition");
            if (InvalidateDuringPrecondition)
                ExecutionSafety.Invalidate("fault-injected before apply");
            if (Hold) return PreconditionResult.Hold("fake hold");
            if (AlreadySatisfied) return PreconditionResult.AlreadySatisfied("already exact");
            return PreconditionResult.Ready();
        }

        public bool Apply(MutationContext context, RootTransactionToken token, int before)
        {
            _state.ApplyCalls++;
            if (Fault == FaultPoint.ApplyBeforeMutation)
                throw new InvalidOperationException("apply-before");
            _state.Value = AppliedValue;
            if (InvalidateDuringApply)
                ExecutionSafety.Invalidate("fault-injected during apply");
            if (ChangeEpochDuringApply)
                _state.Epoch = "save-B/run-1";
            if (Fault == FaultPoint.ApplyAfterMutation)
                throw new InvalidOperationException("apply-after");
            return ApplyReturn;
        }

        public VerificationResult<int> Verify(MutationContext context, int before,
            MutationApplyObservation<bool> apply)
        {
            if (Fault == FaultPoint.Verify)
                throw new InvalidOperationException("verify");
            if (!ForceFalsePostcondition && _state.Value == Target)
                return VerificationResult<int>.Satisfied(_state.Value, "exact target observed");
            return VerificationResult<int>.Failed("exact target was not observed");
        }

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            int before, MutationApplyObservation<bool> apply)
        {
            _state.CompensationCalls++;
            if (Fault == FaultPoint.Compensate)
                throw new InvalidOperationException("compensate");
            if (CompensationClaimsFailure)
                return CompensationResult.Failed("fake restore refused");
            _state.Value = before;
            return CompensationResult.Restored("integer restored exactly");
        }

        public bool BeforeStateMatches(int expected, int observed)
        {
            return expected == observed;
        }

        public string FingerprintBefore(int before)
        {
            return "value=" + before;
        }

        public string FingerprintAfter(int after)
        {
            return "value=" + after;
        }
    }
}

namespace NGUInjector
{
    using NGUInjector.Autopilot;

    internal sealed class SavedSettings
    {
        internal bool GlobalEnabled = true;
    }

    internal static class Main
    {
        internal static AutopilotManager Autopilot;
        internal static SavedSettings Settings = new SavedSettings();
        internal static readonly List<string> Holds = new List<string>();

        internal static void LogAction(string category, string detail)
        {
            Holds.Add(category + ":" + detail);
        }
    }

    internal static class MutationCoordinatorTests
    {
        private static int _assertions;

        private static void Assert(bool value, string message)
        {
            _assertions++;
            if (!value) throw new Exception("FAIL: " + message);
        }

        private static AutopilotConfig FullConfig()
        {
            var config = new AutopilotConfig {Mode = "full"};
            NGUInjector.Main.Autopilot = new AutopilotManager {Config = config};
            return config;
        }

        public static int Main()
        {
            TestNoActiveRootAndRootZero();
            TestNonzeroExclusiveRoots();
            TestImmutableOwnershipSnapshot();
            TestNoOpAndCommit();
            TestPreApplyExceptionsAreClassified();
            TestNormalReturnIsNotCommit();
            TestStaleBeforeApply();
            TestApplyExceptions();
            TestEpochCreatingCommitClosesRoot();
            TestCompensationAndQuarantine();
            TestIrreversiblePartialQuarantine();
            TestRiskCannotBeDowngraded();
            TestRecaptureFailureIsIndeterminate();
            TestEpochCrossingRefusesCompensation();
            TestPendingAndJournal();
            TestDryRunAndStaleToken();
            Console.WriteLine("PASS: " + _assertions + " mutation-coordinator assertions");
            return 0;
        }

        private static void TestNoActiveRootAndRootZero()
        {
            FullConfig();
            var state = new FakeNativeState();
            var coordinator = new MutationCoordinator(() => state.Epoch);
            var intent = new FakeIntent(state);
            var noRoot = coordinator.ExecuteChild<int, bool, int>(null, intent);
            Assert(noRoot.Kind == MutationResultKind.Held, "no root must return Held");
            Assert(noRoot.RootTransactionId == 0, "missing root is journalled as root zero");
            Assert(state.ApplyCalls == 0, "no root must make zero native calls");
            Assert(coordinator.SnapshotJournal().Count == 1,
                "rootless rejection must be a typed journal entry");

            MutationLease lease;
            string reason;
            Assert(!ExecutionSafety.TryAcquire(MutationClass.Allocation,
                    MutationOwner.Autopilot, out lease, out reason),
                "lease admission outside a root must fail");
            Assert(reason.IndexOf("NoActiveTransaction", StringComparison.Ordinal) >= 0,
                "unscoped lease rejection must name NoActiveTransaction");

            var rejectedZero = false;
            try
            {
                new RootTransactionToken(0, ExecutionSafety.StateVersion,
                    state.Epoch, Guid.NewGuid());
            }
            catch (ArgumentOutOfRangeException)
            {
                rejectedZero = true;
            }
            Assert(rejectedZero, "a root-zero token must be rejected at construction");
            Assert(state.ApplyCalls == 0, "root zero must never invoke Apply");
        }

        private static void TestNonzeroExclusiveRoots()
        {
            var config = FullConfig();
            var state = new FakeNativeState();
            var firstCoordinator = new MutationCoordinator(() => state.Epoch);
            var secondCoordinator = new MutationCoordinator(() => state.Epoch);
            var first = firstCoordinator.BeginRoot("first", config);
            Assert(first.Status == RootBeginStatus.Begun, "first root should begin");
            Assert(first.Transaction.Id > 0, "root ID must be nonzero");
            var nested = firstCoordinator.BeginRoot("nested", config);
            Assert(nested.Status == RootBeginStatus.Held,
                "same-coordinator nested root must be rejected");
            var overlapping = secondCoordinator.BeginRoot("overlap", config);
            Assert(overlapping.Status == RootBeginStatus.Held,
                "cross-coordinator overlapping root must be rejected");
            Assert(overlapping.Reason.IndexOf("NestedRootTransaction", StringComparison.Ordinal) >= 0,
                "overlap rejection should be deterministic");
            first.Transaction.Dispose();
            var after = secondCoordinator.BeginRoot("after", config);
            Assert(after.Status == RootBeginStatus.Begun,
                "a new root should begin after exact prior disposal");
            after.Transaction.Dispose();
        }

        private static void TestNoOpAndCommit()
        {
            var config = FullConfig();
            var state = new FakeNativeState();
            var coordinator = new MutationCoordinator(() => state.Epoch);
            using (var root = coordinator.BeginRoot("noop-and-commit", config).Transaction)
            {
                var noOpIntent = new FakeIntent(state) {AlreadySatisfied = true};
                var noOp = root.ExecuteChild(noOpIntent);
                Assert(noOp.Kind == MutationResultKind.NoOpVerified,
                    "explicit exact precondition may publish NoOpVerified");
                Assert(state.ApplyCalls == 0, "NoOpVerified must not invoke Apply");

                var commitIntent = new FakeIntent(state);
                var committed = root.ExecuteChild(commitIntent);
                Assert(committed.Kind == MutationResultKind.Committed,
                    "exact postcondition should commit");
                Assert(committed.HasBefore && committed.Before == 0,
                    "commit should preserve typed before-state");
                Assert(committed.HasAfter && committed.After == 1,
                    "commit should preserve typed after-state");
                Assert(committed.RequiredStepSatisfied,
                    "ordinary verified commit satisfies a required step");
                Assert(root.RequiredStepsSatisfied,
                    "NoOpVerified plus Committed should complete required children");
            }
        }

        private static void TestImmutableOwnershipSnapshot()
        {
            var config = FullConfig();
            var state = new FakeNativeState();
            var coordinator = new MutationCoordinator(() => state.Epoch);
            using (var root = coordinator.BeginRoot("sticky-owner", config).Transaction)
            {
                config.ManageAllocations = false;
                var committed = root.ExecuteChild(new FakeIntent(state));
                Assert(committed.Kind == MutationResultKind.Committed,
                    "feature ownership must remain frozen for the active root");
            }

            state.Value = 0;
            using (var root = coordinator.BeginRoot("new-owner", config).Transaction)
            {
                var held = root.ExecuteChild(new FakeIntent(state));
                Assert(held.Kind == MutationResultKind.Held,
                    "a fresh root must observe relinquished feature ownership");
                Assert(state.ApplyCalls == 1,
                    "new ownership denial must make zero additional native calls");
            }
        }

        private static void TestPreApplyExceptionsAreClassified()
        {
            var config = FullConfig();
            var captureState = new FakeNativeState();
            var captureCoordinator = new MutationCoordinator(() => captureState.Epoch);
            using (var root = captureCoordinator.BeginRoot("capture-fault", config).Transaction)
            {
                var result = root.ExecuteChild(new FakeIntent(captureState)
                {
                    Fault = FaultPoint.CaptureInitial
                });
                Assert(result.Kind == MutationResultKind.Held
                       && result.Phase == MutationPhase.Capture,
                    "initial capture exception must be classified Held at Capture");
                Assert(captureState.ApplyCalls == 0,
                    "capture exception must make zero native calls");
            }

            var preconditionState = new FakeNativeState();
            var preconditionCoordinator = new MutationCoordinator(() => preconditionState.Epoch);
            using (var root = preconditionCoordinator.BeginRoot("precondition-fault", config).Transaction)
            {
                var result = root.ExecuteChild(new FakeIntent(preconditionState)
                {
                    Fault = FaultPoint.Precondition
                });
                Assert(result.Kind == MutationResultKind.RejectedUnchanged
                       && result.Phase == MutationPhase.Precondition,
                    "precondition exception must be RejectedUnchanged");
                Assert(preconditionState.ApplyCalls == 0,
                    "precondition exception must make zero native calls");

                var hold = root.ExecuteChild(new FakeIntent(preconditionState) {Hold = true});
                Assert(hold.Kind == MutationResultKind.Held,
                    "explicit precondition hold must publish Held");
                Assert(preconditionState.ApplyCalls == 0,
                    "explicit precondition hold must make zero native calls");
            }
        }

        private static void TestNormalReturnIsNotCommit()
        {
            var config = FullConfig();
            foreach (var nativeReturn in new[] {false, true})
            {
                var state = new FakeNativeState();
                var coordinator = new MutationCoordinator(() => state.Epoch);
                using (var root = coordinator.BeginRoot("normal-return", config).Transaction)
                {
                    var intent = new FakeIntent(state)
                    {
                        AppliedValue = 0,
                        ApplyReturn = nativeReturn,
                        ForceFalsePostcondition = true
                    };
                    var result = root.ExecuteChild(intent);
                    Assert(result.Kind == MutationResultKind.RejectedUnchanged,
                        "normal Boolean return without postcondition cannot commit");
                    Assert(state.ApplyCalls == 1, "false postcondition case invokes Apply once");
                    Assert(!result.RequiredStepSatisfied,
                        "RejectedUnchanged cannot satisfy a required child");
                }
            }
        }

        private static void TestStaleBeforeApply()
        {
            var config = FullConfig();
            var state = new FakeNativeState();
            var coordinator = new MutationCoordinator(() => state.Epoch);
            using (var root = coordinator.BeginRoot("stale", config).Transaction)
            {
                var result = root.ExecuteChild(new FakeIntent(state)
                {
                    InvalidateDuringPrecondition = true
                });
                Assert(result.Kind == MutationResultKind.Held,
                    "state invalidation after capture must hold before Apply");
                Assert(result.Phase == MutationPhase.Revalidate,
                    "stale capture should be classified at Revalidate");
                Assert(state.ApplyCalls == 0, "stale before Apply must make zero native calls");
            }
        }

        private static void TestApplyExceptions()
        {
            var config = FullConfig();
            var unchangedState = new FakeNativeState();
            var unchangedCoordinator = new MutationCoordinator(() => unchangedState.Epoch);
            using (var root = unchangedCoordinator.BeginRoot("throw-before", config).Transaction)
            {
                var result = root.ExecuteChild(new FakeIntent(unchangedState)
                {
                    Fault = FaultPoint.ApplyBeforeMutation
                });
                Assert(result.Kind == MutationResultKind.RejectedUnchanged,
                    "throw before mutation should recapture as unchanged");
                Assert(result.ExceptionDetail.IndexOf("apply-before", StringComparison.Ordinal) >= 0,
                    "apply exception must be journalled in the typed result");
            }

            var committedState = new FakeNativeState();
            var committedCoordinator = new MutationCoordinator(() => committedState.Epoch);
            using (var root = committedCoordinator.BeginRoot("throw-after", config).Transaction)
            {
                var result = root.ExecuteChild(new FakeIntent(committedState)
                {
                    Fault = FaultPoint.ApplyAfterMutation
                });
                Assert(result.Kind == MutationResultKind.CommittedWithException,
                    "throw after an exact full postcondition must be classified distinctly");
                Assert(result.HasAfter && result.After == 1,
                    "CommittedWithException must retain exact observed after-state");
                Assert(!result.RequiredStepSatisfied,
                    "exceptional commit requires explicit bundle reconciliation");
            }

            var verifyState = new FakeNativeState();
            var verifyCoordinator = new MutationCoordinator(() => verifyState.Epoch);
            using (var root = verifyCoordinator.BeginRoot("verify-throw", config).Transaction)
            {
                var result = root.ExecuteChild(new FakeIntent(verifyState)
                {
                    Fault = FaultPoint.Verify
                });
                Assert(result.Kind == MutationResultKind.Compensated,
                    "verify exception after a reversible mutation should compensate");
                Assert(verifyState.Value == 0, "verify-exception compensation restores exact state");
            }

            var settleState = new FakeNativeState();
            var settleCoordinator = new MutationCoordinator(() => settleState.Epoch);
            using (var root = settleCoordinator.BeginRoot("settle-throw", config).Transaction)
            {
                var result = root.ExecuteChild(new FakeIntent(settleState)
                {
                    Fault = FaultPoint.Settle
                });
                Assert(result.Kind == MutationResultKind.Compensated,
                    "settlement-policy exception after reversible Apply should compensate");
                Assert(settleState.Value == 0,
                    "settlement-policy exception must restore exact state");
            }
        }

        private static void TestEpochCreatingCommitClosesRoot()
        {
            var config = FullConfig();
            var state = new FakeNativeState();
            var coordinator = new MutationCoordinator(() => state.Epoch);
            var root = coordinator.BeginRoot("epoch-transition", config).Transaction;
            var result = root.ExecuteChild(new FakeIntent(state)
            {
                CreatesEpoch = true,
                ChangeEpochDuringApply = true
            });
            Assert(result.Kind == MutationResultKind.Committed,
                "exact epoch-creating postcondition may commit across its intended transition");
            Assert(root.IsClosed,
                "a confirmed epoch-creating mutation must synchronously close the old root");
            var stale = coordinator.ExecuteChild(root.Token, new FakeIntent(state));
            Assert(stale.Kind == MutationResultKind.Held,
                "children cannot execute on a root closed by an epoch transition");
        }

        private static void TestCompensationAndQuarantine()
        {
            var config = FullConfig();
            var restoredState = new FakeNativeState();
            var restoredCoordinator = new MutationCoordinator(() => restoredState.Epoch);
            using (var root = restoredCoordinator.BeginRoot("compensate", config).Transaction)
            {
                var result = root.ExecuteChild(new FakeIntent(restoredState)
                {
                    AppliedValue = 2,
                    ForceFalsePostcondition = true,
                    InvalidateDuringApply = true
                });
                Assert(result.Kind == MutationResultKind.Compensated,
                    "local state-version invalidation may use captured recovery capability");
                Assert(restoredState.Value == 0, "compensation must restore exact before-state");
                Assert(restoredState.CompensationCalls == 1,
                    "partial reversible mutation should compensate exactly once");
                Assert(result.CompensationProof.IndexOf("restored", StringComparison.Ordinal) >= 0,
                    "compensation proof must be published");
            }

            var failedState = new FakeNativeState();
            var failedCoordinator = new MutationCoordinator(() => failedState.Epoch);
            using (var root = failedCoordinator.BeginRoot("failed-compensation", config).Transaction)
            {
                var failing = new FakeIntent(failedState)
                {
                    AppliedValue = 2,
                    ForceFalsePostcondition = true,
                    CompensationClaimsFailure = true
                };
                var result = root.ExecuteChild(failing);
                Assert(result.Kind == MutationResultKind.Quarantined,
                    "failed compensation must quarantine the mutation class");
                var calls = failedState.ApplyCalls;
                var held = root.ExecuteChild(new FakeIntent(failedState));
                Assert(held.Kind == MutationResultKind.Held,
                    "quarantined class must reject dependent children");
                Assert(failedState.ApplyCalls == calls,
                    "quarantined class rejection must make zero additional native calls");
            }

            var thrownState = new FakeNativeState();
            var thrownCoordinator = new MutationCoordinator(() => thrownState.Epoch);
            using (var root = thrownCoordinator.BeginRoot("throw-compensation", config).Transaction)
            {
                var result = root.ExecuteChild(new FakeIntent(thrownState)
                {
                    AppliedValue = 2,
                    ForceFalsePostcondition = true,
                    Fault = FaultPoint.Compensate
                });
                Assert(result.Kind == MutationResultKind.Quarantined,
                    "thrown compensation must be classified as Quarantined");
            }
        }

        private static void TestIrreversiblePartialQuarantine()
        {
            var config = FullConfig();
            var state = new FakeNativeState();
            var coordinator = new MutationCoordinator(() => state.Epoch);
            using (var root = coordinator.BeginRoot("irreversible", config).Transaction)
            {
                var intent = new FakeIntent(state)
                {
                    IntentClass = MutationClass.Rebirth,
                    IntentRisk = MutationRisk.Irreversible,
                    CompensationEnabled = false,
                    AppliedValue = 2,
                    ForceFalsePostcondition = true
                };
                var result = root.ExecuteChild(intent);
                Assert(result.Kind == MutationResultKind.Quarantined,
                    "irreversible partial state must quarantine without fictional rollback");
                Assert(state.CompensationCalls == 0,
                    "irreversible work must not invoke compensation");
            }
        }

        private static void TestRiskCannotBeDowngraded()
        {
            var config = FullConfig();
            var state = new FakeNativeState();
            var coordinator = new MutationCoordinator(() => state.Epoch);
            using (var root = coordinator.BeginRoot("canonical-risk", config).Transaction)
            {
                var result = root.ExecuteChild(new FakeIntent(state)
                {
                    IntentClass = MutationClass.Rebirth,
                    IntentRisk = MutationRisk.Reversible,
                    CompensationEnabled = true,
                    AppliedValue = 2,
                    ForceFalsePostcondition = true
                });
                Assert(result.Kind == MutationResultKind.Quarantined,
                    "an intent cannot downgrade its class's irreversible risk");
                Assert(result.Risk == MutationRisk.Irreversible,
                    "typed result must publish the effective canonical risk");
                Assert(state.CompensationCalls == 0,
                    "downgraded irreversible class must not receive fictional compensation");
            }

            var assist = FullConfig();
            assist.Mode = "assist";
            NGUInjector.Main.Autopilot.Config = assist;
            var cardState = new FakeNativeState();
            var cardCoordinator = new MutationCoordinator(() => cardState.Epoch);
            using (var root = cardCoordinator.BeginRoot("assist-declared-risk", assist).Transaction)
            {
                var held = root.ExecuteChild(new FakeIntent(cardState)
                {
                    IntentClass = MutationClass.Cards,
                    IntentRisk = MutationRisk.Irreversible,
                    CompensationEnabled = false
                });
                Assert(held.Kind == MutationResultKind.Held,
                    "assist must deny an irreversible typed intent in a reversible policy class");
                Assert(cardState.ApplyCalls == 0,
                    "assist typed-risk denial must make zero native calls");
            }
        }

        private static void TestRecaptureFailureIsIndeterminate()
        {
            var config = FullConfig();
            var state = new FakeNativeState();
            var coordinator = new MutationCoordinator(() => state.Epoch);
            var root = coordinator.BeginRoot("indeterminate", config).Transaction;
            var result = root.ExecuteChild(new FakeIntent(state)
            {
                AppliedValue = 2,
                ForceFalsePostcondition = true,
                Fault = FaultPoint.CaptureRecovery
            });
            Assert(result.Kind == MutationResultKind.Indeterminate,
                "failed post-apply recapture must be Indeterminate");
            Assert(root.IsClosed, "Indeterminate state must close the old root immediately");
            var next = coordinator.BeginRoot("blocked-by-global-quarantine", config);
            Assert(next.Status == RootBeginStatus.Held,
                "Indeterminate state must block fresh roots until explicit reconciliation");
        }

        private static void TestEpochCrossingRefusesCompensation()
        {
            var config = FullConfig();
            var state = new FakeNativeState();
            var coordinator = new MutationCoordinator(() => state.Epoch);
            var root = coordinator.BeginRoot("cross-epoch", config).Transaction;
            var result = root.ExecuteChild(new FakeIntent(state)
            {
                AppliedValue = 2,
                ForceFalsePostcondition = true,
                ChangeEpochDuringApply = true
            });
            Assert(result.Kind == MutationResultKind.Indeterminate,
                "partial work across a save/run epoch must be Indeterminate");
            Assert(state.CompensationCalls == 0,
                "recovery capability must not cross the captured epoch");
            Assert(root.IsClosed, "cross-epoch indeterminate work closes the old root");
        }

        private static void TestPendingAndJournal()
        {
            var config = FullConfig();
            var state = new FakeNativeState();
            var coordinator = new MutationCoordinator(() => state.Epoch);
            using (var root = coordinator.BeginRoot("journal", config).Transaction)
            {
                var pending = root.ExecuteChild(new FakeIntent(state)
                {
                    Deferred = true
                });
                Assert(pending.Kind == MutationResultKind.Pending,
                    "deferred native work should publish Pending, not Committed");
                Assert(pending.ObservationKey == "fake-observation",
                    "Pending result must carry its observation key");
                Assert(!root.RequiredStepsSatisfied,
                    "required Pending work cannot complete the root bundle");

                var entries = coordinator.SnapshotJournal();
                var entry = entries[entries.Count - 1];
                Assert(entry.RootTransactionId == root.Id && entry.RootTransactionId > 0,
                    "journal must bind a child to its nonzero root");
                Assert(entry.StepId == pending.StepId && entry.StepId > 0,
                    "journal must bind the exact child step");
                Assert(entry.BindingId == "fake.binding/v1",
                    "journal must publish the exact binding ID");
                Assert(entry.BeforeFingerprint == "value=0",
                    "journal must publish the before fingerprint");
                Assert(entry.Kind == MutationResultKind.Pending,
                    "journal result kind must derive from the typed result");
            }
        }

        private static void TestDryRunAndStaleToken()
        {
            var dryRun = FullConfig();
            dryRun.Mode = "dry-run";
            NGUInjector.Main.Autopilot.Config = dryRun;
            var dryState = new FakeNativeState();
            var dryCoordinator = new MutationCoordinator(() => dryState.Epoch);
            using (var root = dryCoordinator.BeginRoot("dry-run", dryRun).Transaction)
            {
                var result = root.ExecuteChild(new FakeIntent(dryState));
                Assert(result.Kind == MutationResultKind.Held,
                    "dry-run admission must remain a typed Held result");
                Assert(dryState.ApplyCalls == 0, "dry-run must invoke no native work");
            }

            var config = FullConfig();
            var state = new FakeNativeState();
            var coordinator = new MutationCoordinator(() => state.Epoch);
            var begun = coordinator.BeginRoot("stale-token", config).Transaction;
            var token = begun.Token;
            begun.Dispose();
            var stale = coordinator.ExecuteChild(token, new FakeIntent(state));
            Assert(stale.Kind == MutationResultKind.Held,
                "disposed root token must be rejected as stale");
            Assert(state.ApplyCalls == 0, "stale token must make zero native calls");
        }
    }
}
