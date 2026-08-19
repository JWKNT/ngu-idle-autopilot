using System;
using System.Globalization;
using System.Threading;
using NGUInjector.AllocationProfiles.RebirthStuff;

/*
FILE PURPOSE

Purpose: ChallengeRouteProofProducer is the fail-closed production bridge between a trusted
source/copy-state route model and ChallengeStrategyPlanner.RecordRouteProof. It does not estimate a
route itself. The installed bot currently has exact one-fight and reset kernels, but no complete
zero-to-Boss-58 replay builder and no same-objective continuation lower-bound model; treating current
Boss progress or raw run age as either bound caused the unsafe first-Basic incident.

Mechanism: A caller supplies IChallengeRouteBoundModel, which receives a fresh immutable reset
snapshot under the caller's existing root/epoch/thread lease. The returned bounds must be explicitly
source-formula or deterministic copied-state evidence, include positive pessimistic clear and
frontier-recovery times, quantify foregone ordinary-rebirth opportunity, restore the captured Boss
and both Number multipliers, and strictly beat the same objective's continuation lower bound after
the complete live Titan-clock vector is charged. State and root are recaptured before the proof is
recorded. Missing model coverage is a normal HOLD and performs no mutation.

Inputs and outputs: TryRecordLive accepts Character, the already-open one-second RootTransaction,
one safe Normal challenge type, and a read-only bound model. Evaluate is the controller-free proof
validator used by tests and copied-state tooling. Success records one ChallengeRouteProofCapture in
ChallengeStrategyPlanner; failure returns evidence and changes no game or planner state.

Invariants and safety: This adapter never starts a challenge, opens a root, reads native bestTime,
uses boss>=target as elapsed time, converts raw run age into a clear bound, or accepts empirical/
unknown provenance. It supports only the typed first-wave hard Normal challenges. Every accepted
proof is exact-build, exact-epoch, exact-state, current-thread, finite, positive where required, and
stale on any Boss/Number/persistent/state-version change.

Extension points and non-goals: A future copied-state runner or complete source replay implements
IChallengeRouteBoundModel. Until then, pass no model and challenge authority remains dormant even if
configuration is enabled. This file does not invent a continuation lower bound, persist samples,
edit configuration, or invoke Unity/native mutation methods.
*/
namespace NGUInjector.Autopilot
{
    internal enum ChallengeRouteBoundProvenance
    {
        Unknown = 0,
        SourceFormula = 1,
        DeterministicCopiedState = 2
    }

    internal sealed class ChallengeRouteModelInput
    {
        internal string AssemblySha256 = string.Empty;
        internal string Epoch = string.Empty;
        internal long RootTransactionId;
        internal long StateVersion;
        internal ChallengeType Type;
        internal int CompletedBefore;
        internal int ExactTarget;
        internal ResetExecutionSnapshot Reset;
        internal long TitanOpportunitySeconds;
    }

    internal sealed class ChallengeRouteBoundResult
    {
        internal bool ModelComplete;
        internal ChallengeRouteBoundProvenance Provenance;
        internal double ClearUpperSeconds = -1.0;
        internal double RecoveryUpperSeconds = -1.0;
        internal double ForegoneRebirthOpportunityUpperSeconds = -1.0;
        internal double ContinuationLowerBoundSeconds = -1.0;
        internal int RecoveredBossId = -1;
        internal double RecoveredAttackNumberLowerBound = -1.0;
        internal double RecoveredDefenseNumberLowerBound = -1.0;
        internal string ObjectiveSignature = string.Empty;
        internal string StartStateSignature = string.Empty;
        internal string AllocationSignature = string.Empty;
        internal string ResetSequenceSignature = string.Empty;
        internal string Evidence = string.Empty;

        internal static ChallengeRouteBoundResult Unavailable(string reason)
        {
            return new ChallengeRouteBoundResult
            {
                Evidence = string.IsNullOrEmpty(reason)
                    ? "route bounds are unavailable" : reason
            };
        }
    }

    internal interface IChallengeRouteBoundModel
    {
        ChallengeRouteBoundResult Evaluate(ChallengeRouteModelInput input);
    }

    internal sealed class ChallengeRouteProofProductionResult
    {
        internal bool Recorded;
        internal string Reason = string.Empty;
        internal double TotalChallengeUpperSeconds = -1.0;
        internal double ContinuationLowerBoundSeconds = -1.0;
        internal ChallengeRouteProofCapture Proof;
    }

    internal static class ChallengeRouteProofProducer
    {
        internal static ChallengeRouteProofProductionResult TryRecordLive(Character c,
            RootTransaction root, ChallengeType type, IChallengeRouteBoundModel model)
        {
            string leaseReason;
            if (!ValidRoot(c, root, out leaseReason)) return Hold(leaseReason);
            if (!ResetProgressionAuthority.SafeNormalChallenge(type)
                || c.settings.rebirthDifficulty != difficulty.normal)
                return Hold("only first-wave hard Normal challenges have route-proof authority");
            if (c.challenges == null || c.challenges.inChallenge)
                return Hold("a challenge is active or challenge state is unavailable");
            if (model == null)
                return Hold("no complete source/copy-state route model supplies the required continuation lower bound");

            int completedBefore;
            int exactTarget;
            try
            {
                completedBefore = ChallengeStrategyPlanner.CurrentCompletions(c, type);
                exactTarget = ChallengeMechanics.ExactTarget(type, completedBefore);
            }
            catch (Exception error)
            {
                return Hold("challenge identity capture failed: " + error.GetType().Name
                            + ": " + error.Message);
            }
            TitanVectorCost titan;
            string titanEvidence;
            if (!ChallengeStrategyPlanner.TryCaptureTitanVector(c, out titan, out titanEvidence)
                || titan == null || titan.TotalCycleDelaySeconds < 0L)
                return Hold("complete Titan opportunity cost is unavailable: " + titanEvidence);
            var reset = LiveResetSnapshot.Capture(c);
            if (reset == null || reset.Number == null)
                return Hold("fresh reset/Number snapshot is unavailable");
            var stateVersion = ChallengeStrategyPlanner.ExpectedStateVersion(c, type,
                ChallengeDifficultyBand.Normal, completedBefore, exactTarget);
            var input = new ChallengeRouteModelInput
            {
                AssemblySha256 = Main.GameAssemblySha256 ?? string.Empty,
                Epoch = root.Token.EpochFingerprint,
                RootTransactionId = root.Token.RootTransactionId,
                StateVersion = root.Token.StateVersion,
                Type = type, CompletedBefore = completedBefore, ExactTarget = exactTarget,
                Reset = reset.Clone(), TitanOpportunitySeconds = titan.TotalCycleDelaySeconds
            };
            ChallengeRouteBoundResult bounds;
            try { bounds = model.Evaluate(input); }
            catch (Exception error)
            {
                return Hold("route bound model threw: " + error.GetType().Name + ": "
                            + error.Message);
            }
            var evaluated = Evaluate(type, completedBefore, exactTarget, reset.BossId,
                reset.HighestBoss, reset.Number.CurrentAttack, reset.Number.CurrentDefense,
                titan.TotalCycleDelaySeconds, bounds);
            if (!evaluated.Recorded) return evaluated;

            if (!ValidRoot(c, root, out leaseReason))
                return Hold("route proof became stale after modelling: " + leaseReason);
            var fresh = LiveResetSnapshot.Capture(c);
            var freshStateVersion = ChallengeStrategyPlanner.ExpectedStateVersion(c, type,
                ChallengeDifficultyBand.Normal, completedBefore, exactTarget);
            if (fresh == null || !string.Equals(
                    ChallengeStrategyPlanner.OpportunityProgressionFingerprint(reset),
                    ChallengeStrategyPlanner.OpportunityProgressionFingerprint(fresh),
                    StringComparison.Ordinal)
                || !string.Equals(stateVersion, freshStateVersion, StringComparison.Ordinal))
                return Hold("Boss/Number/persistent state changed while route bounds were modelled");
            try { ChallengeStrategyPlanner.RecordRouteProof(c, fresh, evaluated.Proof); }
            catch (Exception error)
            {
                return Hold("validated route proof could not be recorded: "
                            + error.GetType().Name + ": " + error.Message);
            }
            evaluated.Reason = "recorded exact same-root route proof; " + evaluated.Reason;
            return evaluated;
        }

        internal static ChallengeRouteProofProductionResult Evaluate(ChallengeType type,
            int completedBefore, int exactTarget, int currentBossId, int highestBossId,
            double currentAttackNumber, double currentDefenseNumber,
            long titanOpportunitySeconds, ChallengeRouteBoundResult bounds)
        {
            if (!ResetProgressionAuthority.SafeNormalChallenge(type)
                || ChallengeMechanics.EntryKind(type) != ChallengeEntryTransformKind.HardReset)
                return Hold("challenge is outside the source-audited hard Normal subset");
            if (completedBefore < 0 || exactTarget != ChallengeMechanics.ExactTarget(type,
                    completedBefore))
                return Hold("challenge completion/target identity is not exact");
            if (bounds == null || !bounds.ModelComplete)
                return Hold(bounds == null || string.IsNullOrEmpty(bounds.Evidence)
                    ? "route model is incomplete" : bounds.Evidence);
            if (bounds.Provenance != ChallengeRouteBoundProvenance.SourceFormula
                && bounds.Provenance != ChallengeRouteBoundProvenance.DeterministicCopiedState)
                return Hold("route model provenance is unknown or non-deterministic");
            if (!FinitePositive(bounds.ClearUpperSeconds)
                || !FinitePositive(bounds.RecoveryUpperSeconds)
                || !FiniteNonNegative(bounds.ForegoneRebirthOpportunityUpperSeconds)
                || !FinitePositive(bounds.ContinuationLowerBoundSeconds)
                || titanOpportunitySeconds < 0L
                || currentBossId < 0 || highestBossId < currentBossId
                || !FinitePositive(currentAttackNumber)
                || !FinitePositive(currentDefenseNumber)
                || bounds.RecoveredBossId < currentBossId
                || !FinitePositive(bounds.RecoveredAttackNumberLowerBound)
                || !FinitePositive(bounds.RecoveredDefenseNumberLowerBound)
                || bounds.RecoveredAttackNumberLowerBound + 1e-12 < currentAttackNumber
                || bounds.RecoveredDefenseNumberLowerBound + 1e-12 < currentDefenseNumber
                || string.IsNullOrEmpty(bounds.ObjectiveSignature)
                || string.IsNullOrEmpty(bounds.StartStateSignature)
                || string.IsNullOrEmpty(bounds.AllocationSignature)
                || string.IsNullOrEmpty(bounds.ResetSequenceSignature))
                return Hold("route bounds are non-finite, zero-time, incomplete, or fail frontier recovery");
            var total = bounds.ClearUpperSeconds + bounds.RecoveryUpperSeconds
                        + bounds.ForegoneRebirthOpportunityUpperSeconds
                        + titanOpportunitySeconds;
            if (!FinitePositive(total)
                || total + 1e-12 >= bounds.ContinuationLowerBoundSeconds)
                return new ChallengeRouteProofProductionResult
                {
                    TotalChallengeUpperSeconds = total,
                    ContinuationLowerBoundSeconds = bounds.ContinuationLowerBoundSeconds,
                    Reason = "challenge upper " + Seconds(total)
                             + " does not strictly beat continuation lower "
                             + Seconds(bounds.ContinuationLowerBoundSeconds)
                };
            var proof = new ChallengeRouteProofCapture
            {
                Type = type, CompletedBefore = completedBefore, ExactTarget = exactTarget,
                EvidenceKind = bounds.Provenance == ChallengeRouteBoundProvenance.SourceFormula
                    ? ChallengeTimingEvidenceKind.NativeFormulaSimulation
                    : ChallengeTimingEvidenceKind.ExactDeterministic,
                ClearUpperSeconds = bounds.ClearUpperSeconds,
                RecoveryUpperSeconds = bounds.RecoveryUpperSeconds,
                ForegoneRebirthOpportunityUpperSeconds =
                    bounds.ForegoneRebirthOpportunityUpperSeconds,
                ContinuationLowerBoundSeconds = bounds.ContinuationLowerBoundSeconds,
                RecoveredBossId = bounds.RecoveredBossId,
                RecoveredAttackNumberLowerBound = bounds.RecoveredAttackNumberLowerBound,
                RecoveredDefenseNumberLowerBound = bounds.RecoveredDefenseNumberLowerBound,
                ObjectiveSignature = bounds.ObjectiveSignature,
                StartStateSignature = bounds.StartStateSignature,
                AllocationSignature = bounds.AllocationSignature,
                ResetSequenceSignature = bounds.ResetSequenceSignature
            };
            return new ChallengeRouteProofProductionResult
            {
                Recorded = true, Proof = proof,
                TotalChallengeUpperSeconds = total,
                ContinuationLowerBoundSeconds = bounds.ContinuationLowerBoundSeconds,
                Reason = "finite source/copy-state upper " + Seconds(total)
                         + " strictly beats same-objective continuation lower "
                         + Seconds(bounds.ContinuationLowerBoundSeconds)
            };
        }

        private static bool ValidRoot(Character c, RootTransaction root, out string reason)
        {
            if (c == null || !ReferenceEquals(Main.Character, c) || !Main.IsAutomationReady)
            {
                reason = "Character/gameplay synchronization is not current";
                return false;
            }
            if (root == null || root.IsClosed || root.Token == null
                || root.Token.RootTransactionId <= 0
                || root.Token.ManagedThreadId != Thread.CurrentThread.ManagedThreadId
                || !ExecutionSafety.IsRootCurrent(root.Token.RootTransactionId,
                    root.Token.StateVersion)
                || !string.Equals(root.Token.EpochFingerprint,
                    Main.CurrentGameEpochFingerprint, StringComparison.Ordinal))
            {
                reason = "caller-owned root/thread/epoch lease is not current";
                return false;
            }
            reason = "current";
            return true;
        }

        private static ChallengeRouteProofProductionResult Hold(string reason)
        {
            return new ChallengeRouteProofProductionResult
            {
                Reason = string.IsNullOrEmpty(reason) ? "route proof held" : reason
            };
        }

        private static bool FiniteNonNegative(double value)
        {
            return value >= 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool FinitePositive(double value)
        {
            return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string Seconds(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? "unknown"
                : value.ToString("0.###", CultureInfo.InvariantCulture) + "s";
        }
    }
}
