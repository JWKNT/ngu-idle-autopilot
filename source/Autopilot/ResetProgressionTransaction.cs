using System;
using System.Globalization;
using System.Threading;
using NGUInjector.AllocationProfiles.RebirthStuff;
using NGUInjector.Managers;

/*
FILE PURPOSE

Purpose: ResetProgressionTransaction is the typed, root-owned production boundary for the first
safe wave of challenge and difficulty resets. It can execute one admission-grade Normal challenge
from a deliberately narrow catalog, or the source-exact Normal-to-Evil transition. Later challenge
batches, Evil-to-Sadistic, MOVE 69, and the final END sequence remain outside this authority.

Mechanism: LiveResetProgressionRuntime adapts Character state, ChallengeStrategyPlanner's exact
one-intent admission, DifficultyTransitionGate, build-pinned NativeMutationAdapters, the live Titan
reset interlock, harvestable fruit, remaining Blood, and the caller's currently open RootTransaction.
Challenge admission receives that exact reset/loss snapshot and must prove that pessimistic clear,
current-run recovery, and Titan opportunity time strictly beat either a source-modelled continuation
bound or the audited historical route's conservative permanent-reward payback budget. Positive Blood is a HOLD;
hard challenge entry sets Number to one, so casting the ordinary-rebirth NUMBER spell here would
destroy Blood without preserving its effect. ChallengeEntryMutationIntent and
NormalToEvilMutationIntent revalidate all boundary and opportunity facts immediately
before native work, invoke only the audited controller wrappers, and accept a commit only through
ResetPostconditions. Both intents declare CreatesNewEpoch and publish the verified successor through
ResetEpochTransition before MutationCoordinator closes the old root.

Inputs and outputs: Main supplies a synchronized Character, an accessor for its one-second active
root, and explicit feature authority on each call. Challenge timing samples enter only through
ChallengeStrategyPlanner after the same-root route producer validates them. Outputs are
ResetProgressionExecutionResult records backed by the typed
mutation journal; native calls, epoch publication, and HOLD/quarantine evidence are the only effects.

Invariants and safety: No reset crosses an active/source-proven executable Titan, a harvestable
fruit, positive or non-finite Blood, a stale root/epoch/thread lease, an active challenge, a pending
difficulty selector, or the native minimum-rebirth boundary. A challenge must still be the exact
fresh planner admission at Apply time. Only Basic, No Augments, No Equipment, Blind, No NGU, and No
Time Machine are executable here, and only in Normal. Difficulty execution is Normal-to-Evil only.
Native normal return is not success; exact +1/timer/Number/Boss/challenge/difficulty/Titan proofs are.

Extension points and non-goals: Main owns cadence and configuration. ExecutionSafety must grant the
Difficulty class only from its independently default-false difficulty flag before the difficulty
entry point can run. Exact copied-state tests and a separate policy review are required before
expanding either catalog. This file does not synthesize challenge timing, harvest fruit, fight
Titans, complete challenges, execute Evil-to-Sadistic, MOVE 69, or deliver final END state.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class ResetBoundarySnapshot
    {
        internal bool GameplaySynchronized;
        internal bool RootLeaseCurrent;
        internal bool TitanBoundaryClear;
        internal bool FruitBoundaryClear;
        internal bool BloodBoundaryClear;
        internal string TitanReason = string.Empty;
        internal double Blood;

        internal string Fingerprint
        {
            get
            {
                return GameplaySynchronized + "|" + RootLeaseCurrent + "|"
                       + TitanBoundaryClear + "|" + FruitBoundaryClear + "|"
                       + BloodBoundaryClear + "|" + Blood.ToString("R",
                           CultureInfo.InvariantCulture) + "|" + (TitanReason ?? string.Empty);
            }
        }
    }

    internal sealed class ResetBoundaryResult
    {
        internal bool Clear;
        internal string Reason = string.Empty;
    }

    /*
    SHARED RESET-LOSS GATE

    These are resources whose live value is destroyed or deferred by every reset in this file.
    The gate is intentionally independent of feature-manager ownership: disabling Yggdrasil or
    Blood automation does not authorize the reset executor to discard a harvest or Blood pool.
    */
    internal static class ResetBoundaryGate
    {
        internal static ResetBoundaryResult Evaluate(ResetBoundarySnapshot state)
        {
            if (state == null) return Hold("reset boundary snapshot is missing");
            if (!state.GameplaySynchronized)
                return Hold("gameplay/controller synchronization is not current");
            if (!state.RootLeaseCurrent)
                return Hold("the caller-owned root/epoch lease is not current");
            if (!state.TitanBoundaryClear)
                return Hold(string.IsNullOrEmpty(state.TitanReason)
                    ? "a killable or active Titan must resolve before reset"
                    : state.TitanReason);
            if (!state.FruitBoundaryClear)
                return Hold("a harvestable Yggdrasil fruit must resolve before reset");
            if (!state.BloodBoundaryClear)
                return Hold("valued Blood is positive, non-finite, or unavailable at the reset boundary");
            return new ResetBoundaryResult {Clear = true, Reason = "all reset-loss boundaries are clear"};
        }

        private static ResetBoundaryResult Hold(string reason)
        {
            return new ResetBoundaryResult {Clear = false, Reason = reason ?? string.Empty};
        }
    }

    internal static class ResetProgressionAuthority
    {
        /* The first live wave excludes special service/deadline/shared-budget/reset-semantics. */
        internal static bool SafeNormalChallenge(ChallengeType type)
        {
            switch (type)
            {
                case ChallengeType.Basic:
                case ChallengeType.NoAug:
                case ChallengeType.NoEquip:
                case ChallengeType.Blind:
                case ChallengeType.NoNGU:
                case ChallengeType.NoTimeMachine:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool SafeDifficulty(DifficultyTransitionKind transition)
        {
            return transition == DifficultyTransitionKind.NormalToEvil;
        }
    }

    internal sealed class ChallengeExecutionSnapshot
    {
        internal string Epoch = string.Empty;
        internal ResetExecutionSnapshot Reset;
        internal ResetBoundarySnapshot Boundary;
        internal ChallengeAdmission Admission;
        internal string NativeTypeToken = string.Empty;
        internal string PlannerEvidence = string.Empty;

        internal string Fingerprint
        {
            get
            {
                return (Reset == null ? "reset-missing" : Reset.ExactFingerprint) + "|boundary="
                       + (Boundary == null ? "missing" : Boundary.Fingerprint) + "|admission="
                       + AdmissionFingerprint(Admission) + "|native="
                       + (NativeTypeToken ?? string.Empty) + "|epoch=" + (Epoch ?? string.Empty);
            }
        }

        internal static string AdmissionFingerprint(ChallengeAdmission admission)
            {
                if (admission == null || admission.Intent == null) return "none";
                var opportunity = admission.Opportunity;
                return admission.Type + ":" + admission.ProfileCode + ":"
                   + admission.CompletedBefore + ":" + admission.MaxCompletions + ":"
                   + admission.TargetBoss + ":" + admission.TargetLevel + ":"
                   + admission.Intent.ExpectedStateVersion + ":"
                   + admission.Intent.TotalRouteSeconds.ToString("R", CultureInfo.InvariantCulture)
                   + ":opportunity=" + (opportunity == null ? "missing"
                       : opportunity.Admitted + ":"
                         + opportunity.ChallengeClearUpperSeconds.ToString("R",
                             CultureInfo.InvariantCulture) + ":"
                         + opportunity.CurrentRunRecoveryUpperSeconds.ToString("R",
                             CultureInfo.InvariantCulture) + ":"
                         + opportunity.TitanOpportunitySeconds.ToString("R",
                             CultureInfo.InvariantCulture) + ":"
                         + opportunity.ContinuationLowerBoundSeconds.ToString("R",
                             CultureInfo.InvariantCulture));
            }
    }

    internal sealed class DifficultyExecutionSnapshot
    {
        internal string Epoch = string.Empty;
        internal ResetExecutionSnapshot Reset;
        internal ResetBoundarySnapshot Boundary;
        internal DifficultyGateSnapshot Gate;
        internal ResetDifficulty SelectedTarget;

        internal string Fingerprint
        {
            get
            {
                return (Reset == null ? "reset-missing" : Reset.ExactFingerprint) + "|boundary="
                       + (Boundary == null ? "missing" : Boundary.Fingerprint) + "|gate="
                       + GateFingerprint(Gate) + "|selected=" + SelectedTarget + "|epoch="
                       + (Epoch ?? string.Empty);
            }
        }

        private static string GateFingerprint(DifficultyGateSnapshot gate)
        {
            if (gate == null) return "missing";
            return gate.CurrentDifficulty + ":" + gate.InChallenge + ":"
                   + gate.Achievement151 + ":" + gate.Achievement152 + ":"
                   + gate.HighestBoss + ":" + gate.HighestHardBoss + ":"
                   + gate.AttackBoost.ToString("R", CultureInfo.InvariantCulture) + ":"
                   + gate.ItopodTotalStatBonus.ToString("R", CultureInfo.InvariantCulture) + ":"
                   + gate.ExileV4Defeated + ":" + gate.BossId + ":"
                   + gate.BossFightInProgress + ":" + gate.BossNukeInProgress + ":"
                   + gate.NoRebirthChallengeActive + ":"
                   + gate.RebirthSeconds.ToString("R", CultureInfo.InvariantCulture) + ":"
                   + gate.MinimumRebirthSeconds.ToString("R", CultureInfo.InvariantCulture) + ":"
                   + gate.GameplaySynchronized + ":" + gate.MutationLeaseCurrent;
        }
    }

    internal interface IResetProgressionRuntime
    {
        bool LiveAuthority { get; }
        ChallengeExecutionSnapshot CaptureChallenge();
        DifficultyExecutionSnapshot CaptureDifficulty();
        bool ChallengeBindingAvailable(ChallengeType type);
        ResetNativeObservation EnterChallenge(ChallengeType type, RootTransactionToken token);
        ResetNativeObservation SelectDifficulty(DifficultyTransitionKind transition,
            RootTransactionToken token);
        ResetNativeObservation StartDifficulty(DifficultyTransitionKind transition,
            RootTransactionToken token);
        void PublishVerifiedEpoch(ResetExecutionSnapshot after, string reason);
    }

    /*
    LIVE ROOT/CONTROLLER ADAPTER

    The active-root callback is an ownership attestation, not a way to create roots. Every native
    call verifies identity, state version, epoch, managed thread, and synchronized Character again.
    Selector and start are separate so a selector-only partial state is detected and quarantined by
    the irreversible coordinator intent instead of being reported as a harmless HOLD.
    */
    internal sealed class LiveResetProgressionRuntime : IResetProgressionRuntime
    {
        private readonly Character _character;
        private readonly Func<RootTransaction> _activeRoot;
        private readonly NativeBindingRegistry _registry;
        private readonly NativeMutationAdapters _native;

        private LiveResetProgressionRuntime(Character character,
            Func<RootTransaction> activeRoot, NativeBindingRegistry registry)
        {
            if (character == null) throw new ArgumentNullException("character");
            if (activeRoot == null) throw new ArgumentNullException("activeRoot");
            if (registry == null) throw new ArgumentNullException("registry");
            _character = character;
            _activeRoot = activeRoot;
            _registry = registry;
            _native = registry.CreateMutationAdapters();
        }

        internal static LiveResetProgressionRuntime Create(Character character,
            Func<RootTransaction> activeRoot)
        {
            return new LiveResetProgressionRuntime(character, activeRoot,
                NativeBindingRegistry.Create(typeof(Character).Assembly,
                    Main.GameAssemblySha256));
        }

        public bool LiveAuthority
        {
            get { return _registry.IsKnownBuild && _registry.IrreversibleActionsEnabled; }
        }

        public ChallengeExecutionSnapshot CaptureChallenge()
        {
            ValidateCharacter();
            var reset = LiveResetSnapshot.Capture(_character);
            var boundary = CaptureBoundary();
            string evidence;
            var recommendations = ChallengeStrategyPlanner.Recommend(_character,
                boundary, reset, out evidence);
            var admission = recommendations == null || recommendations.Count != 1
                ? null : recommendations[0];
            return new ChallengeExecutionSnapshot
            {
                Epoch = Main.CurrentGameEpochFingerprint,
                Reset = reset,
                Boundary = boundary,
                Admission = admission,
                NativeTypeToken = admission == null ? string.Empty
                    : LiveResetSnapshot.NativeChallengeTypeToken(_character, admission.Type),
                PlannerEvidence = evidence ?? string.Empty
            };
        }

        public DifficultyExecutionSnapshot CaptureDifficulty()
        {
            ValidateCharacter();
            var boundary = CaptureBoundary();
            var nativeBoundary = new LiveDifficultyTransitionBoundary(_character, _native,
                boundary.GameplaySynchronized, boundary.RootLeaseCurrent);
            return new DifficultyExecutionSnapshot
            {
                Epoch = Main.CurrentGameEpochFingerprint,
                Reset = nativeBoundary.CaptureState(),
                Boundary = boundary,
                Gate = nativeBoundary.CaptureGate(),
                SelectedTarget = nativeBoundary.ReadSelectedTarget()
            };
        }

        public bool ChallengeBindingAvailable(ChallengeType type)
        {
            var key = ChallengeBinding(type);
            return LiveAuthority && !string.IsNullOrEmpty(key) && _registry.HasBinding(key);
        }

        public ResetNativeObservation EnterChallenge(ChallengeType type,
            RootTransactionToken token)
        {
            ValidateRoot(token);
            if (!ChallengeBindingAvailable(type))
                return new ResetNativeObservation {Reason = "exact challenge binding is unavailable"};
            return ResetNativeObservation.From(_native.EnterChallenge(_character.rebirth,
                NativeChallenge(type)));
        }

        public ResetNativeObservation SelectDifficulty(DifficultyTransitionKind transition,
            RootTransactionToken token)
        {
            ValidateRoot(token);
            if (!ResetProgressionAuthority.SafeDifficulty(transition))
                return new ResetNativeObservation {Reason = "difficulty transition is outside first-wave authority"};
            return ResetNativeObservation.From(_native.SelectDifficulty(_character.rebirth,
                NativeDifficultyCall.Evil));
        }

        public ResetNativeObservation StartDifficulty(DifficultyTransitionKind transition,
            RootTransactionToken token)
        {
            ValidateRoot(token);
            if (!ResetProgressionAuthority.SafeDifficulty(transition))
                return new ResetNativeObservation {Reason = "difficulty transition is outside first-wave authority"};
            return ResetNativeObservation.From(_native.StartDifficulty(_character.rebirth,
                NativeDifficultyCall.Evil));
        }

        public void PublishVerifiedEpoch(ResetExecutionSnapshot after, string reason)
        {
            ResetEpochTransition.Close(_character, after, reason);
        }

        private ResetBoundarySnapshot CaptureBoundary()
        {
            string titanReason;
            var titanClear = CaptureTitanBoundary(out titanReason);
            bool fruitClear;
            try { fruitClear = !YggdrasilManager.AnyHarvestable(); }
            catch { fruitClear = false; }
            var blood = _character.bloodMagic == null
                ? 0.0 : _character.bloodMagic.bloodPoints;
            var bloodClear = !double.IsNaN(blood) && !double.IsInfinity(blood) && blood <= 0.0;
            return new ResetBoundarySnapshot
            {
                GameplaySynchronized = Main.IsAutomationReady,
                RootLeaseCurrent = OwnsActiveRoot(),
                TitanBoundaryClear = titanClear,
                FruitBoundaryClear = fruitClear,
                BloodBoundaryClear = bloodClear,
                Blood = blood,
                TitanReason = titanReason
            };
        }

        private bool CaptureTitanBoundary(out string reason)
        {
            TitanResetInterlock interlock;
            if (Main.TryGetTitanResetInterlock(out interlock))
            {
                if (interlock == null)
                {
                    reason = "typed Titan reset interlock returned no state";
                    return false;
                }
                reason = interlock.Reason;
                return !interlock.HoldReset;
            }
            try
            {
                var clear = ZoneHelpers.HighestAvailableTitan() < 0
                            && !(ZoneHelpers.ZoneIsTitan(_character.adventure.zone)
                                 && _character.adventureController != null
                                 && (_character.adventureController.currentEnemy != null
                                     || _character.adventureController.fightInProgress));
                reason = clear ? "legacy viability-aware Titan boundary is clear"
                    : "a source-proven executable/active Titan boundary must resolve before reset";
                return clear;
            }
            catch
            {
                reason = "Titan reset boundary capture failed";
                return false;
            }
        }

        private bool OwnsActiveRoot()
        {
            var root = _activeRoot();
            return root != null && !root.IsClosed && root.Token != null
                   && root.Token.RootTransactionId > 0
                   && root.Token.ManagedThreadId == Thread.CurrentThread.ManagedThreadId
                   && ExecutionSafety.IsRootCurrent(root.Token.RootTransactionId,
                       root.Token.StateVersion)
                   && string.Equals(root.Token.EpochFingerprint,
                       Main.CurrentGameEpochFingerprint, StringComparison.Ordinal);
        }

        private void ValidateRoot(RootTransactionToken token)
        {
            ValidateCharacter();
            var root = _activeRoot();
            if (token == null || root == null || root.IsClosed || root.Token == null
                || !ReferenceEquals(root.Token, token) || token.RootTransactionId <= 0
                || token.ManagedThreadId != Thread.CurrentThread.ManagedThreadId
                || !ExecutionSafety.IsRootCurrent(token.RootTransactionId, token.StateVersion)
                || !string.Equals(token.EpochFingerprint,
                    Main.CurrentGameEpochFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "reset mutation token is not the exact active root/thread/epoch lease");
        }

        private void ValidateCharacter()
        {
            if (!ReferenceEquals(Main.Character, _character) || _character.rebirth == null
                || _character.challenges == null || _character.settings == null)
                throw new InvalidOperationException("reset progression Character/controllers are stale");
        }

        private static NativeChallengeCall NativeChallenge(ChallengeType type)
        {
            switch (type)
            {
                case ChallengeType.Basic: return NativeChallengeCall.Basic;
                case ChallengeType.NoAug: return NativeChallengeCall.NoAugs;
                case ChallengeType.NoEquip: return NativeChallengeCall.NoEquipment;
                case ChallengeType.Blind: return NativeChallengeCall.Blind;
                case ChallengeType.NoNGU: return NativeChallengeCall.NoNgu;
                case ChallengeType.NoTimeMachine: return NativeChallengeCall.NoTimeMachine;
                default: throw new ArgumentOutOfRangeException("type");
            }
        }

        private static string ChallengeBinding(ChallengeType type)
        {
            switch (type)
            {
                case ChallengeType.Basic: return NativeBindingKeys.ChallengeBasic;
                case ChallengeType.NoAug: return NativeBindingKeys.ChallengeNoAugs;
                case ChallengeType.NoEquip: return NativeBindingKeys.ChallengeNoEquipment;
                case ChallengeType.Blind: return NativeBindingKeys.ChallengeBlind;
                case ChallengeType.NoNGU: return NativeBindingKeys.ChallengeNoNgu;
                case ChallengeType.NoTimeMachine: return NativeBindingKeys.ChallengeNoTimeMachine;
                default: return string.Empty;
            }
        }
    }

    internal sealed class ResetProgressionExecutionResult
    {
        internal MutationResult Mutation;
        internal string Reason = string.Empty;
        internal bool Selected;

        internal bool Committed
        {
            get
            {
                return Mutation != null && (Mutation.Kind == MutationResultKind.Committed
                    || Mutation.Kind == MutationResultKind.CommittedWithException);
            }
        }
    }

    internal static class ResetProgressionTransaction
    {
        internal static ResetProgressionExecutionResult ExecuteNormalChallenge(
            RootTransaction root, IResetProgressionRuntime runtime, bool featureAuthority)
        {
            if (root == null || root.IsClosed) return Hold("an open caller-owned root is required");
            if (runtime == null || !runtime.LiveAuthority)
                return Hold("the build-pinned reset runtime has no live authority");
            if (!featureAuthority) return Hold("Normal challenge execution is feature-disabled");
            ChallengeExecutionSnapshot candidate;
            try { candidate = runtime.CaptureChallenge(); }
            catch (Exception error)
            {
                return Hold("challenge capture failed: " + error.GetType().Name + ": " + error.Message);
            }
            if (candidate == null || candidate.Admission == null)
                return Hold(candidate == null ? "challenge snapshot is missing"
                    : candidate.PlannerEvidence);
            // Once the exact planner has selected an irreversible challenge route, this frame
            // belongs to that route even if final recapture holds. Main must not silently
            // substitute an ordinary rebirth for a selected same-state opportunity proof.
            var selected = true;
            if (!ResetProgressionAuthority.SafeNormalChallenge(candidate.Admission.Type))
                return Hold("the exact planner admission is outside the first Normal challenge batch",
                    selected);
            if (!runtime.ChallengeBindingAvailable(candidate.Admission.Type))
                return Hold("the exact challenge binding is unavailable",
                    selected);
            var boundary = ResetBoundaryGate.Evaluate(candidate.Boundary);
            if (!boundary.Clear) return Hold(boundary.Reason, selected);
            var mutation = root.ExecuteChild(new ChallengeEntryMutationIntent(runtime,
                candidate.Admission));
            return From(mutation, selected);
        }

        internal static ResetProgressionExecutionResult ExecuteNormalToEvil(
            RootTransaction root, IResetProgressionRuntime runtime, bool featureAuthority)
        {
            if (root == null || root.IsClosed) return Hold("an open caller-owned root is required");
            if (runtime == null || !runtime.LiveAuthority)
                return Hold("the build-pinned reset runtime has no live authority");
            if (!featureAuthority) return Hold("Normal-to-Evil execution is feature-disabled");
            var mutation = root.ExecuteChild(new NormalToEvilMutationIntent(runtime));
            return From(mutation);
        }

        private static ResetProgressionExecutionResult From(MutationResult mutation,
            bool selected = false)
        {
            return new ResetProgressionExecutionResult
            {
                Mutation = mutation,
                Selected = selected,
                Reason = mutation == null ? "typed mutation returned no result" : mutation.Reason
            };
        }

        private static ResetProgressionExecutionResult Hold(string reason,
            bool selected = false)
        {
            return new ResetProgressionExecutionResult
            {
                Reason = reason ?? string.Empty,
                Selected = selected
            };
        }
    }

    internal sealed class ChallengeEntryMutationIntent :
        IMutationIntent<ChallengeExecutionSnapshot, ResetNativeObservation,
            ChallengeExecutionSnapshot>
    {
        private readonly IResetProgressionRuntime _runtime;
        private readonly ChallengeType _type;
        private readonly string _admissionFingerprint;

        internal ChallengeEntryMutationIntent(IResetProgressionRuntime runtime,
            ChallengeAdmission admission)
        {
            if (runtime == null) throw new ArgumentNullException("runtime");
            if (admission == null) throw new ArgumentNullException("admission");
            _runtime = runtime;
            _type = admission.Type;
            _admissionFingerprint = ChallengeExecutionSnapshot.AdmissionFingerprint(admission);
        }

        public string Id { get { return "normal-challenge-entry-" + _type; } }
        public MutationClass Class { get { return MutationClass.Challenge; } }
        public MutationRisk Risk { get { return MutationRisk.Irreversible; } }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId { get { return "Rebirth.engage" + _type + "Challenge()"; } }
        public bool Required { get { return true; } }
        public bool CanCompensate { get { return false; } }
        public bool CreatesNewEpoch { get { return true; } }
        public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

        public ChallengeExecutionSnapshot CaptureBefore(MutationContext context)
        {
            return _runtime.CaptureChallenge();
        }

        public PreconditionResult CheckPreconditions(MutationContext context,
            ChallengeExecutionSnapshot before)
        {
            if (!_runtime.LiveAuthority || !_runtime.ChallengeBindingAvailable(_type))
                return PreconditionResult.Hold("exact challenge binding/authority is unavailable");
            if (!ResetProgressionAuthority.SafeNormalChallenge(_type))
                return PreconditionResult.Hold("challenge is outside the first Normal batch");
            if (before == null || before.Reset == null || before.Admission == null)
                return PreconditionResult.Hold("fresh exact challenge admission is missing");
            if (before.Admission.Opportunity == null
                || !before.Admission.Opportunity.Admitted)
                return PreconditionResult.Hold(
                    "fresh challenge admission lacks a winning same-state opportunity proof");
            if (before.Reset.CurrentDifficulty != ResetDifficulty.Normal
                || before.Reset.NextDifficulty != ResetDifficulty.Normal)
                return PreconditionResult.Hold("first-wave challenges require Normal current/next difficulty");
            if (before.Reset.InChallenge)
                return PreconditionResult.Hold("a challenge is already active");
            if (!string.Equals(_admissionFingerprint,
                ChallengeExecutionSnapshot.AdmissionFingerprint(before.Admission),
                StringComparison.Ordinal))
                return PreconditionResult.Hold("the exact planner admission changed before capture");
            if (string.IsNullOrEmpty(before.NativeTypeToken))
                return PreconditionResult.Hold("native challenge type token is missing");
            var boundary = ResetBoundaryGate.Evaluate(before.Boundary);
            return boundary.Clear ? PreconditionResult.Ready()
                : PreconditionResult.Hold(boundary.Reason);
        }

        public ResetNativeObservation Apply(MutationContext context, RootTransactionToken token,
            ChallengeExecutionSnapshot before)
        {
            var fresh = _runtime.CaptureChallenge();
            var gate = CheckPreconditions(context, fresh);
            if (gate.Kind != MutationPreconditionKind.Ready)
                throw new InvalidOperationException("challenge Apply revalidation failed: " + gate.Reason);
            var invocation = _runtime.EnterChallenge(_type, token);
            if (invocation != null && !invocation.ReturnedNormally && invocation.Exception != null)
                throw invocation.Exception;
            return invocation;
        }

        public VerificationResult<ChallengeExecutionSnapshot> Verify(MutationContext context,
            ChallengeExecutionSnapshot before,
            MutationApplyObservation<ResetNativeObservation> apply)
        {
            var after = _runtime.CaptureChallenge();
            var proof = ResetPostconditions.VerifyChallenge(before.Reset, after.Reset, _type,
                before.NativeTypeToken);
            if (proof.Satisfied)
            {
                _runtime.PublishVerifiedEpoch(after.Reset,
                    "Entered " + _type + " challenge [typed exact postcondition]");
                return VerificationResult<ChallengeExecutionSnapshot>.Satisfied(after, proof.Reason);
            }
            if (!ResetPostconditions.ExactStateMatches(before.Reset, after.Reset))
                ResetEpochTransition.Quarantine("challenge entry produced a partial/wrong poststate: "
                                                + proof.Reason);
            return VerificationResult<ChallengeExecutionSnapshot>.Failed(proof.Reason);
        }

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            ChallengeExecutionSnapshot before,
            MutationApplyObservation<ResetNativeObservation> apply)
        {
            return CompensationResult.NotSupported("challenge entry is irreversible");
        }

        public bool BeforeStateMatches(ChallengeExecutionSnapshot expected,
            ChallengeExecutionSnapshot observed)
        {
            return expected != null && observed != null
                   && ResetPostconditions.ExactStateMatches(expected.Reset, observed.Reset);
        }

        public string FingerprintBefore(ChallengeExecutionSnapshot before)
        {
            return before == null ? string.Empty : before.Fingerprint;
        }

        public string FingerprintAfter(ChallengeExecutionSnapshot after)
        {
            return after == null ? string.Empty : after.Fingerprint;
        }
    }

    internal sealed class DifficultyNativeApply
    {
        internal ResetNativeObservation Selector;
        internal ResetNativeObservation Start;
    }

    internal sealed class NormalToEvilMutationIntent :
        IMutationIntent<DifficultyExecutionSnapshot, DifficultyNativeApply,
            DifficultyExecutionSnapshot>
    {
        private readonly IResetProgressionRuntime _runtime;

        internal NormalToEvilMutationIntent(IResetProgressionRuntime runtime)
        {
            if (runtime == null) throw new ArgumentNullException("runtime");
            _runtime = runtime;
        }

        public string Id { get { return "difficulty-normal-to-evil"; } }
        public MutationClass Class { get { return MutationClass.Difficulty; } }
        public MutationRisk Risk { get { return MutationRisk.Irreversible; } }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId
        {
            get { return DifficultyTransitionExecutor.EvilSelectorContract + "+Rebirth.engageEvil()"; }
        }
        public bool Required { get { return true; } }
        public bool CanCompensate { get { return false; } }
        public bool CreatesNewEpoch { get { return true; } }
        public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

        public DifficultyExecutionSnapshot CaptureBefore(MutationContext context)
        {
            return _runtime.CaptureDifficulty();
        }

        public PreconditionResult CheckPreconditions(MutationContext context,
            DifficultyExecutionSnapshot before)
        {
            if (!_runtime.LiveAuthority)
                return PreconditionResult.Hold("exact difficulty binding/authority is unavailable");
            if (before == null || before.Reset == null || before.Gate == null)
                return PreconditionResult.Hold("difficulty snapshot is missing");
            if (before.Reset.NextDifficulty != ResetDifficulty.Normal
                || before.SelectedTarget != ResetDifficulty.Normal)
                return PreconditionResult.Hold("a pending difficulty selector already exists");
            var boundary = ResetBoundaryGate.Evaluate(before.Boundary);
            if (!boundary.Clear) return PreconditionResult.Hold(boundary.Reason);
            var gate = DifficultyTransitionGate.EvaluateFinalPreflight(
                DifficultyTransitionKind.NormalToEvil, before.Gate);
            return gate.Legal ? PreconditionResult.Ready()
                : PreconditionResult.Hold(gate.Evidence);
        }

        public DifficultyNativeApply Apply(MutationContext context, RootTransactionToken token,
            DifficultyExecutionSnapshot before)
        {
            var fresh = _runtime.CaptureDifficulty();
            var first = CheckPreconditions(context, fresh);
            if (first.Kind != MutationPreconditionKind.Ready)
                throw new InvalidOperationException("difficulty Apply revalidation failed: " + first.Reason);
            var selector = _runtime.SelectDifficulty(DifficultyTransitionKind.NormalToEvil, token);
            ThrowIfNativeFailed(selector, "difficulty selector");
            var selected = _runtime.CaptureDifficulty();
            if (selected.SelectedTarget != ResetDifficulty.Evil
                || selected.Reset == null || selected.Reset.NextDifficulty != ResetDifficulty.Evil)
                throw new InvalidOperationException("difficulty selector did not install exact Evil target");
            var boundary = ResetBoundaryGate.Evaluate(selected.Boundary);
            if (!boundary.Clear)
                throw new InvalidOperationException("post-selector reset boundary became stale: "
                                                    + boundary.Reason);
            var gate = DifficultyTransitionGate.EvaluateFinalPreflight(
                DifficultyTransitionKind.NormalToEvil, selected.Gate);
            if (!gate.Legal)
                throw new InvalidOperationException("post-selector difficulty gate became stale: "
                                                    + gate.Evidence);
            var start = _runtime.StartDifficulty(DifficultyTransitionKind.NormalToEvil, token);
            ThrowIfNativeFailed(start, "difficulty start");
            return new DifficultyNativeApply {Selector = selector, Start = start};
        }

        public VerificationResult<DifficultyExecutionSnapshot> Verify(MutationContext context,
            DifficultyExecutionSnapshot before,
            MutationApplyObservation<DifficultyNativeApply> apply)
        {
            var after = _runtime.CaptureDifficulty();
            var proof = ResetPostconditions.VerifyDifficulty(before.Reset, after.Reset,
                DifficultyTransitionKind.NormalToEvil);
            if (proof.Satisfied)
            {
                _runtime.PublishVerifiedEpoch(after.Reset,
                    "Normal-to-Evil transition [typed exact postcondition]");
                return VerificationResult<DifficultyExecutionSnapshot>.Satisfied(after, proof.Reason);
            }
            if (!ResetPostconditions.ExactStateMatches(before.Reset, after.Reset))
                ResetEpochTransition.Quarantine("difficulty transition produced a partial/wrong poststate: "
                                                + proof.Reason);
            return VerificationResult<DifficultyExecutionSnapshot>.Failed(proof.Reason);
        }

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            DifficultyExecutionSnapshot before,
            MutationApplyObservation<DifficultyNativeApply> apply)
        {
            return CompensationResult.NotSupported("difficulty transition is irreversible");
        }

        public bool BeforeStateMatches(DifficultyExecutionSnapshot expected,
            DifficultyExecutionSnapshot observed)
        {
            return expected != null && observed != null
                   && ResetPostconditions.ExactStateMatches(expected.Reset, observed.Reset);
        }

        public string FingerprintBefore(DifficultyExecutionSnapshot before)
        {
            return before == null ? string.Empty : before.Fingerprint;
        }

        public string FingerprintAfter(DifficultyExecutionSnapshot after)
        {
            return after == null ? string.Empty : after.Fingerprint;
        }

        private static void ThrowIfNativeFailed(ResetNativeObservation value, string label)
        {
            if (value != null && value.InvocationAttempted && value.ReturnedNormally) return;
            if (value != null && value.Exception != null) throw value.Exception;
            throw new InvalidOperationException(label + " did not return normally: "
                                                + (value == null ? "missing result" : value.Reason));
        }
    }
}
