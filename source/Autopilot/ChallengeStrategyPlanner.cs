using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NGUInjector.AllocationProfiles.RebirthStuff;
using NGUInjector.Managers;

/*
FILE PURPOSE

Purpose: ChallengeStrategyPlanner adapts live NGU Idle 1.260 challenge state to task 16's pure
challenge mechanics. It admits only comparable bot-owned timing evidence and publishes exactly one
epoch-bound challenge intent; runner-ups remain diagnostics and can never become fallback entries.

Mechanism: Recommend validates the global menu, native unlock/count/max/target facts for all eleven
controllers, builds the minimum exact timing key, captures the complete valuable Titan clock vector,
and sends only admission-grade routes to ChallengeIntentSelector. Admission also requires a
same-state opportunity proof: pessimistic challenge clear plus current-run recovery and Titan loss
must be strictly smaller than a source-modelled lower bound for continuing. ActivePolicy reports the
exact offline/deadline/budget/cadence/paired-track contract and preserves an already-admitted
ordinary-rebirth checkpoint after it becomes due.

Inputs and outputs: Inputs are Character/controller snapshots, Main's installed assembly hash,
ExecutionSafety's state version, bot-owned timing samples, an optional Laser route comparison, and
an optional exact rebirth event. Outputs are zero or one ChallengeAdmission plus telemetry, or an
ActiveChallengePolicy. This file never enters, quits, completes, or rebirths a challenge.

Invariants and safety: Native bestTime is never timing evidence. Live serialized maxima are
authoritative and native targets must equal the exact installed formula. Every valued Titan clock,
including a time-ready clock, contributes its exact reset-loss vector; the typed Titan, fruit, and
Blood boundary is part of admission and is revalidated at mutation time. Reaching a target in the
current run is never timing evidence and cannot bootstrap entry. Recovery evidence must restore at
least the captured Boss and both current Number multipliers. A 24-Hour route requires positive
active-time slack. No-Rebirth is continuous, no probability label is emitted without calibrated
coverage, and missing route or continuation evidence freezes every destructive reset.

Extension points and non-goals: Task 28 records formula-simulation/observed samples and supplies
exact reset/Laser comparisons; tasks 17/29 validate the intent epoch and own entry/allocation
transactions. Persistence, terminal reward valuation, live mutation, modal service, and allocation
quota enforcement are deliberately outside this read-only adapter.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class ChallengeAdmission
    {
        internal ChallengeType Type;
        internal string Code = string.Empty;
        internal int Completion;
        internal int CompletedBefore;
        internal int MaxCompletions;
        internal int TargetBoss = -1;
        internal int TargetLevel = -1;
        internal double PessimisticClearSeconds;
        internal double RecoverySeconds;
        internal double TitanOpportunitySeconds;
        internal string Constraints = string.Empty;
        internal string Reward = string.Empty;
        internal string Evidence = string.Empty;
        internal double Score;
        internal ChallengeIntent Intent;
        internal ChallengeTimingEstimate Timing;
        internal TitanVectorCost TitanCost;
        internal ChallengeDeadlineProjection Deadline;
        internal ChallengeOpportunityDecision Opportunity;

        internal string ProfileCode { get { return Code + "-" + Completion; } }

        internal string EtaText
        {
            get
            {
                if (!Finite(PessimisticClearSeconds)) return "unknown";
                var seconds = (int)Math.Min(int.MaxValue,
                    Math.Ceiling(PessimisticClearSeconds));
                if (seconds < 3600) return Math.Max(1, seconds / 60) + "m";
                return (seconds / 3600.0).ToString("0.0", CultureInfo.InvariantCulture) + "h";
            }
        }

        private static bool Finite(double value)
        {
            return value >= 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    internal sealed class ActiveChallengePolicy
    {
        internal ChallengeType Type;
        internal string Code = string.Empty;
        internal int CompletedBefore;
        internal int MaxCompletions;
        internal int TargetBoss = -1;
        internal int TargetLevel = -1;
        internal int RebirthSeconds = -1;
        internal bool ForbidRebirth = true;
        internal bool MechanicallyAllowsRebirth;
        internal string RulesSummary = string.Empty;
        internal string RebirthPolicySummary = string.Empty;
        internal bool RequiresLaserSwordAllocation;
        internal bool RequiresTrollDialogService;
        internal bool RequiresHundredLevelBudget;
        internal int HundredLevelSpent;
        internal int HundredLevelRemaining;
        internal int EtaSeconds = -1;
        internal double PessimisticTotalSeconds = -1.0;
        internal string Objective = string.Empty;
        internal string EtaReason = string.Empty;
        internal ChallengeOfflineTransformKind OfflineMode;
        internal TrollCadenceProjection NextTrollEvent;
        internal LaserPhaseDecision LaserPhase;
        internal ChallengeDeadlineProjection Deadline;
        internal ChallengeTimingEstimate Timing;
    }

    /*
    EXACT SAME-STATE OPPORTUNITY PROOF

    Challenge timing alone answers only "how long might the challenge take?" It does not price the
    run destroyed by hard entry. This evidence is produced by a bounded replay/formula model for the
    exact live reset fingerprint. ContinuationLowerBoundSeconds is deliberately a lower bound while
    CurrentRunRecoveryUpperSeconds and the foregone ordinary-rebirth opportunity are upper bounds:
    only upper(challenge+recovery+rebirth-opportunity+Titan) strictly below lower(continue) can
    authorize an irreversible entry.
    */
    internal sealed class ChallengeOpportunityEvidence
    {
        internal ChallengeTimingKey Key;
        internal ChallengeTimingEvidenceKind EvidenceKind;
        internal string ExpectedStateVersion = string.Empty;
        internal string CurrentProgressionFingerprint = string.Empty;
        internal string ObjectiveSignature = string.Empty;
        internal double ContinuationLowerBoundSeconds = -1.0;
        internal double CurrentRunRecoveryUpperSeconds = -1.0;
        internal double ForegoneRebirthOpportunityUpperSeconds = -1.0;
        internal int CurrentBossId = -1;
        internal int HighestBossId = -1;
        internal double CurrentAttackNumber = -1.0;
        internal double CurrentDefenseNumber = -1.0;
        internal int RecoveredBossId = -1;
        internal double RecoveredAttackNumberLowerBound = -1.0;
        internal double RecoveredDefenseNumberLowerBound = -1.0;

        internal ChallengeOpportunityEvidence Clone()
        {
            var copy = (ChallengeOpportunityEvidence)MemberwiseClone();
            copy.Key = Key == null ? null : Key.Clone();
            return copy;
        }
    }

    internal sealed class ChallengeOpportunityDecision
    {
        internal bool Admitted;
        internal double ChallengeClearUpperSeconds = -1.0;
        internal double CurrentRunRecoveryUpperSeconds = -1.0;
        internal double ForegoneRebirthOpportunityUpperSeconds = -1.0;
        internal double TitanOpportunitySeconds = -1.0;
        internal double TotalChallengeUpperSeconds = -1.0;
        internal double ContinuationLowerBoundSeconds = -1.0;
        internal string Reason = string.Empty;
    }

    /*
    ATOMIC PRODUCTION EVIDENCE INPUT

    This is intentionally a proof payload, not a guess payload. A copied-state deterministic replay
    or native-formula simulator supplies a pessimistic clear/recovery upper bound and an optimistic
    continuation lower bound for one exact live reset snapshot. RecordRouteProof installs the keyed
    timing and opportunity halves together.
    */
    internal sealed class ChallengeRouteProofCapture
    {
        internal ChallengeType Type;
        internal int CompletedBefore;
        internal int ExactTarget;
        internal ChallengeTimingEvidenceKind EvidenceKind;
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
    }

    internal static class ChallengeStrategyPlanner
    {
        private const double DeadlineSafetyMarginSeconds = 1.0;
        private static readonly object TimingGate = new object();
        private static readonly ChallengeTimingLedger TimingLedger =
            new ChallengeTimingLedger();
        private static readonly Dictionary<string, ChallengeOpportunityEvidence>
            OpportunityEvidence = new Dictionary<string, ChallengeOpportunityEvidence>(
                StringComparer.Ordinal);

        private sealed class LiveCandidate
        {
            internal ChallengeType Type;
            internal int Complete;
            internal int Maximum;
            internal int NativeTarget;
            internal bool LevelTarget;
            internal long ExpectedExp;
            internal string Constraints = string.Empty;
            internal string Reward = string.Empty;
        }

        internal static void RecordTimingSample(ChallengeTimingSample sample)
        {
            lock (TimingGate) TimingLedger.Record(sample);
        }

        internal static bool TryTimingEstimate(ChallengeTimingKey key,
            out ChallengeTimingEstimate estimate)
        {
            lock (TimingGate) return TimingLedger.TryEstimate(key, out estimate);
        }

        /*
        ADMISSION EVIDENCE INGEST

        A producer may record a proof only for the exact state version and complete reset snapshot
        it modelled. This is the production hook for a copied-state replay or exact native-formula
        simulation; merely observing that the current run has passed a target is not a proof.
        */
        internal static void RecordOpportunityEvidence(ChallengeOpportunityEvidence evidence)
        {
            if (evidence == null || evidence.Key == null)
                throw new ArgumentNullException("evidence");
            if (!ExactEvidence(evidence.EvidenceKind))
                throw new ArgumentException("opportunity evidence must be exact deterministic or native-formula simulation");
            if (string.IsNullOrEmpty(evidence.ExpectedStateVersion)
                || string.IsNullOrEmpty(evidence.CurrentProgressionFingerprint)
                || string.IsNullOrEmpty(evidence.ObjectiveSignature))
                throw new ArgumentException("opportunity evidence identity is incomplete");
            ValidateFiniteNonNegative(evidence.ContinuationLowerBoundSeconds,
                "ContinuationLowerBoundSeconds");
            ValidateFiniteNonNegative(evidence.CurrentRunRecoveryUpperSeconds,
                "CurrentRunRecoveryUpperSeconds");
            ValidateFiniteNonNegative(evidence.ForegoneRebirthOpportunityUpperSeconds,
                "ForegoneRebirthOpportunityUpperSeconds");
            ValidateFinitePositive(evidence.CurrentAttackNumber, "CurrentAttackNumber");
            ValidateFinitePositive(evidence.CurrentDefenseNumber, "CurrentDefenseNumber");
            ValidateFinitePositive(evidence.RecoveredAttackNumberLowerBound,
                "RecoveredAttackNumberLowerBound");
            ValidateFinitePositive(evidence.RecoveredDefenseNumberLowerBound,
                "RecoveredDefenseNumberLowerBound");
            if (evidence.CurrentBossId < 0 || evidence.HighestBossId < evidence.CurrentBossId
                || evidence.RecoveredBossId < 0)
                throw new ArgumentOutOfRangeException("evidence", "Boss recovery evidence is invalid");
            lock (TimingGate)
                OpportunityEvidence[OpportunityKey(evidence.Key,
                    evidence.ExpectedStateVersion, evidence.CurrentProgressionFingerprint)] =
                    evidence.Clone();
        }

        internal static void RecordRouteProof(Character c,
            ResetExecutionSnapshot resetSnapshot, ChallengeRouteProofCapture proof)
        {
            if (c == null || c.settings == null || c.rebirthTime == null
                || resetSnapshot == null || proof == null)
                throw new ArgumentNullException("proof");
            if (!ExactEvidence(proof.EvidenceKind))
                throw new ArgumentException("route proof must be deterministic or native-formula evidence");
            var liveReset = LiveResetSnapshot.Capture(c);
            if (proof.CompletedBefore < 0 || proof.ExactTarget < 0
                || resetSnapshot.Number == null || resetSnapshot.BossId != c.bossID
                || resetSnapshot.HighestBoss != c.highestBoss
                || liveReset == null
                || !string.Equals(OpportunityProgressionFingerprint(resetSnapshot),
                    OpportunityProgressionFingerprint(liveReset), StringComparison.Ordinal)
                || proof.CompletedBefore != CurrentCompletions(c, proof.Type)
                || proof.ExactTarget != ChallengeMechanics.ExactTarget(proof.Type,
                    proof.CompletedBefore))
                throw new ArgumentException("route proof does not describe the current reset snapshot");
            ValidateFiniteNonNegative(proof.ClearUpperSeconds, "ClearUpperSeconds");
            ValidateFiniteNonNegative(proof.RecoveryUpperSeconds, "RecoveryUpperSeconds");
            ValidateFiniteNonNegative(proof.ForegoneRebirthOpportunityUpperSeconds,
                "ForegoneRebirthOpportunityUpperSeconds");
            ValidateFiniteNonNegative(proof.ContinuationLowerBoundSeconds,
                "ContinuationLowerBoundSeconds");
            ValidateFinitePositive(resetSnapshot.Number.CurrentAttack, "CurrentAttackNumber");
            ValidateFinitePositive(resetSnapshot.Number.CurrentDefense, "CurrentDefenseNumber");
            ValidateFinitePositive(proof.RecoveredAttackNumberLowerBound,
                "RecoveredAttackNumberLowerBound");
            ValidateFinitePositive(proof.RecoveredDefenseNumberLowerBound,
                "RecoveredDefenseNumberLowerBound");
            if (string.IsNullOrEmpty(proof.ObjectiveSignature)
                || resetSnapshot.HighestBoss < resetSnapshot.BossId
                || proof.RecoveredBossId < 0)
                throw new ArgumentException("route proof recovery/objective identity is incomplete");
            var difficulty = DifficultyOf(c.settings.rebirthDifficulty);
            var key = CreateTimingKey(proof.Type, difficulty,
                proof.CompletedBefore, proof.ExactTarget);
            RecordTimingSample(new ChallengeTimingSample
            {
                Key = key, EvidenceKind = proof.EvidenceKind,
                ObservedOnlineSeconds = proof.ClearUpperSeconds,
                ObservedOfflineSeconds = 0.0,
                RecoverySeconds = proof.RecoveryUpperSeconds,
                PredictedUpperSeconds = proof.ClearUpperSeconds,
                StartStateSignature = proof.StartStateSignature ?? string.Empty,
                AllocationSignature = proof.AllocationSignature ?? string.Empty,
                ResetSequenceSignature = proof.ResetSequenceSignature ?? string.Empty,
                FinishedUtcTicks = DateTime.UtcNow.Ticks
            });
            RecordOpportunityEvidence(new ChallengeOpportunityEvidence
            {
                Key = key, EvidenceKind = proof.EvidenceKind,
                ExpectedStateVersion = ExpectedStateVersion(c, proof.Type,
                    difficulty, proof.CompletedBefore, proof.ExactTarget),
                CurrentProgressionFingerprint = OpportunityProgressionFingerprint(resetSnapshot),
                ObjectiveSignature = proof.ObjectiveSignature ?? string.Empty,
                ContinuationLowerBoundSeconds = proof.ContinuationLowerBoundSeconds,
                CurrentRunRecoveryUpperSeconds = proof.RecoveryUpperSeconds,
                ForegoneRebirthOpportunityUpperSeconds =
                    proof.ForegoneRebirthOpportunityUpperSeconds,
                CurrentBossId = resetSnapshot.BossId,
                HighestBossId = resetSnapshot.HighestBoss,
                CurrentAttackNumber = resetSnapshot.Number.CurrentAttack,
                CurrentDefenseNumber = resetSnapshot.Number.CurrentDefense,
                RecoveredBossId = proof.RecoveredBossId,
                RecoveredAttackNumberLowerBound = proof.RecoveredAttackNumberLowerBound,
                RecoveredDefenseNumberLowerBound = proof.RecoveredDefenseNumberLowerBound
            });
        }

        internal static ChallengeOpportunityDecision EvaluateOpportunity(ChallengeType type,
            ChallengeTimingEstimate timing, TitanVectorCost titanCost,
            ChallengeOpportunityEvidence evidence, ResetBoundarySnapshot boundary,
            string expectedStateVersion, string currentProgressionFingerprint,
            int currentBossId, int highestBossId, double currentAttackNumber,
            double currentDefenseNumber)
        {
            var result = new ChallengeOpportunityDecision();
            if (ChallengeMechanics.EntryKind(type) != ChallengeEntryTransformKind.HardReset)
            {
                result.Reason = "HOLD: this entry needs its specialized soft-reset opportunity model";
                return result;
            }
            var resetGate = ResetBoundaryGate.Evaluate(boundary);
            if (!resetGate.Clear)
            {
                result.Reason = "HOLD: " + resetGate.Reason;
                return result;
            }
            if (timing == null || !timing.AdmissionGrade
                || !Finite(timing.UpperClearSeconds) || !Finite(timing.RecoverySeconds))
            {
                result.Reason = "HOLD: finite admission-grade clear/recovery timing is missing";
                return result;
            }
            if (titanCost == null || titanCost.TotalCycleDelaySeconds < 0L)
            {
                result.Reason = "HOLD: the complete Titan reset-loss vector is missing";
                return result;
            }
            if (evidence == null)
            {
                result.Reason = "HOLD: exact same-state continuation/recovery evidence is missing";
                return result;
            }
            if (!ExactEvidence(evidence.EvidenceKind) || evidence.Key == null
                || timing.Key == null || !evidence.Key.Equals(timing.Key)
                || !string.Equals(evidence.ExpectedStateVersion,
                    expectedStateVersion ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(evidence.CurrentProgressionFingerprint,
                    currentProgressionFingerprint ?? string.Empty, StringComparison.Ordinal)
                || string.IsNullOrEmpty(evidence.ObjectiveSignature))
            {
                result.Reason = "HOLD: opportunity evidence is not exact-key/current-state comparable";
                return result;
            }
            if (evidence.CurrentBossId != currentBossId
                || evidence.HighestBossId != highestBossId
                || !Same(evidence.CurrentAttackNumber, currentAttackNumber)
                || !Same(evidence.CurrentDefenseNumber, currentDefenseNumber))
            {
                result.Reason = "HOLD: current Boss/Number changed after opportunity modelling";
                return result;
            }
            if (!Finite(evidence.ContinuationLowerBoundSeconds)
                || !Finite(evidence.CurrentRunRecoveryUpperSeconds)
                || !Finite(evidence.ForegoneRebirthOpportunityUpperSeconds)
                || evidence.RecoveredBossId < currentBossId
                || !FinitePositive(evidence.RecoveredAttackNumberLowerBound)
                || !FinitePositive(evidence.RecoveredDefenseNumberLowerBound)
                || evidence.RecoveredAttackNumberLowerBound + 1e-12 < currentAttackNumber
                || evidence.RecoveredDefenseNumberLowerBound + 1e-12 < currentDefenseNumber)
            {
                result.Reason = "HOLD: recovery does not restore the captured Boss and both Number multipliers";
                return result;
            }
            var recoveryUpper = Math.Max(timing.RecoverySeconds,
                evidence.CurrentRunRecoveryUpperSeconds);
            double total;
            try
            {
                total = checked(timing.UpperClearSeconds + recoveryUpper
                                + evidence.ForegoneRebirthOpportunityUpperSeconds
                                + titanCost.TotalCycleDelaySeconds);
            }
            catch (OverflowException)
            {
                result.Reason = "HOLD: challenge opportunity total overflowed";
                return result;
            }
            result.ChallengeClearUpperSeconds = timing.UpperClearSeconds;
            result.CurrentRunRecoveryUpperSeconds = recoveryUpper;
            result.ForegoneRebirthOpportunityUpperSeconds =
                evidence.ForegoneRebirthOpportunityUpperSeconds;
            result.TitanOpportunitySeconds = titanCost.TotalCycleDelaySeconds;
            result.TotalChallengeUpperSeconds = total;
            result.ContinuationLowerBoundSeconds = evidence.ContinuationLowerBoundSeconds;
            if (!Finite(total) || total + 1e-12 >= evidence.ContinuationLowerBoundSeconds)
            {
                result.Reason = "HOLD: challenge upper " + FormatSeconds(total)
                                + " does not strictly beat continuation lower "
                                + FormatSeconds(evidence.ContinuationLowerBoundSeconds)
                                + " for " + evidence.ObjectiveSignature;
                return result;
            }
            result.Admitted = true;
            result.Reason = "exact opportunity proof: clear upper "
                            + FormatSeconds(timing.UpperClearSeconds) + ", current-run recovery upper "
                            + FormatSeconds(recoveryUpper) + ", foregone ordinary-rebirth opportunity "
                            + FormatSeconds(evidence.ForegoneRebirthOpportunityUpperSeconds)
                            + ", Titan loss "
                            + FormatSeconds(titanCost.TotalCycleDelaySeconds) + " = "
                            + FormatSeconds(total) + " < continuation lower "
                            + FormatSeconds(evidence.ContinuationLowerBoundSeconds) + " for "
                            + evidence.ObjectiveSignature + "; recovers Boss " + currentBossId
                            + " and Number A/D " + currentAttackNumber.ToString("R",
                                CultureInfo.InvariantCulture) + "/"
                            + currentDefenseNumber.ToString("R", CultureInfo.InvariantCulture);
            return result;
        }

        internal static IList<ChallengeAdmission> Recommend(Character c,
            out string evidenceSummary)
        {
            return Recommend(c, null, null, out evidenceSummary);
        }

        /*
        MUTATION-GRADE RECOMMENDATION

        The no-boundary overload above remains useful for read-only telemetry, but deliberately
        cannot produce an executable admission. The reset runtime supplies one atomic reset and
        loss-boundary snapshot so the opportunity proof is tied to the exact state it may destroy.
        */
        internal static IList<ChallengeAdmission> Recommend(Character c,
            ResetBoundarySnapshot resetBoundary, ResetExecutionSnapshot resetSnapshot,
            out string evidenceSummary)
        {
            evidenceSummary = "Challenge HOLD: live state unavailable";
            var empty = new List<ChallengeAdmission>();
            if (c == null || c.challenges == null || c.allChallenges == null
                || c.settings == null || c.rebirth == null || c.rebirthTime == null)
                return empty;
            if (c.challenges.inChallenge)
            {
                evidenceSummary = "Challenge HOLD: a challenge is already active";
                return empty;
            }
            if (!c.challenges.unlocked)
            {
                evidenceSummary = "Challenge HOLD: the global challenge menu is not unlocked";
                return empty;
            }
            if (c.bossID <= 0 || c.rebirthTime.totalseconds + 1e-12
                                 < c.rebirth.minRebirthTime())
            {
                evidenceSummary = "Challenge HOLD: native entry requires Boss progress and the minimum rebirth time";
                return empty;
            }

            TitanVectorCost titanCost;
            string titanEvidence;
            if (!TryCaptureTitanVector(c, out titanCost, out titanEvidence))
            {
                evidenceSummary = "Challenge HOLD: " + titanEvidence;
                return empty;
            }
            var difficulty = DifficultyOf(c.settings.rebirthDifficulty);
            var intents = new List<ChallengeIntent>();
            var admissions = new Dictionary<string, ChallengeAdmission>(StringComparer.Ordinal);
            var rejected = new List<string>();
            foreach (var live in LiveCandidates(c))
            {
                if (live.Maximum <= 0 || live.Complete < 0 || live.Complete >= live.Maximum
                    || !BaseRebirth.ChallengeUnlocked(c.allChallenges, live.Type)) continue;
                var exactTarget = ChallengeMechanics.ExactTarget(live.Type, live.Complete);
                if (live.NativeTarget != exactTarget)
                {
                    rejected.Add(ChallengeMechanics.Code(live.Type) + " target mismatch native="
                                 + live.NativeTarget + " exact=" + exactTarget);
                    continue;
                }
                var key = CreateTimingKey(live.Type, difficulty, live.Complete, exactTarget);
                ChallengeTimingEstimate timing;
                if (!TryTimingEstimate(key, out timing) || timing == null
                    || !timing.AdmissionGrade
                    || !Finite(timing.UpperClearSeconds) || !Finite(timing.RecoverySeconds))
                {
                    rejected.Add(ChallengeMechanics.Code(live.Type)
                                 + " lacks comparable admission-grade timing");
                    continue;
                }
                var stateVersion = ExpectedStateVersion(c, live.Type,
                    difficulty, live.Complete, exactTarget);
                var progressionFingerprint = resetSnapshot == null
                    ? string.Empty : OpportunityProgressionFingerprint(resetSnapshot);
                ChallengeOpportunityEvidence opportunityEvidence;
                lock (TimingGate)
                    OpportunityEvidence.TryGetValue(OpportunityKey(key, stateVersion,
                        progressionFingerprint), out opportunityEvidence);
                var opportunity = EvaluateOpportunity(live.Type, timing, titanCost,
                    opportunityEvidence, resetBoundary, stateVersion,
                    progressionFingerprint, c.bossID, c.highestBoss,
                    c.attackMulti, c.defenseMulti);
                if (!opportunity.Admitted)
                {
                    rejected.Add(ChallengeMechanics.Code(live.Type) + " "
                                 + opportunity.Reason);
                    continue;
                }
                ChallengeDeadlineProjection deadline = null;
                if (live.Type == ChallengeType.TwentyFourHour)
                {
                    deadline = ChallengeMechanics.EvaluateTwentyFourHourDeadline(0.0,
                        timing.UpperClearSeconds, DeadlineSafetyMarginSeconds);
                    if (deadline.DeadlineSlackSeconds <= 0.0)
                    {
                        rejected.Add("24HR has non-positive deadline slack "
                                     + FormatSeconds(deadline.DeadlineSlackSeconds));
                        continue;
                    }
                }
                var code = ChallengeMechanics.Code(live.Type);
                var intent = new ChallengeIntent
                {
                    Type = live.Type,
                    Completion = live.Complete + 1,
                    ProfileCode = code + "-" + (live.Complete + 1),
                    ExpectedStateVersion = stateVersion,
                    TimingKey = key,
                    TotalRouteSeconds = opportunity.TotalChallengeUpperSeconds,
                    Evidence = timing.EvidenceLabel + "; " + opportunity.Reason
                };
                var evidence = timing.EvidenceLabel + " key=" + key + ", n="
                               + timing.SampleCount + "; " + opportunity.Reason;
                if (timing.P90LabelAllowed)
                    evidence += ", " + timing.QuantileLabel + " calibrated coverage="
                                + timing.EmpiricalCoverage.ToString("0.000",
                                    CultureInfo.InvariantCulture);
                var admission = new ChallengeAdmission
                {
                    Type = live.Type, Code = code, Completion = live.Complete + 1,
                    CompletedBefore = live.Complete, MaxCompletions = live.Maximum,
                    TargetBoss = live.LevelTarget ? -1 : exactTarget,
                    TargetLevel = live.LevelTarget ? exactTarget : -1,
                    PessimisticClearSeconds = timing.UpperClearSeconds,
                    RecoverySeconds = opportunity.CurrentRunRecoveryUpperSeconds,
                    TitanOpportunitySeconds = titanCost.TotalCycleDelaySeconds,
                    Constraints = live.Constraints, Reward = live.Reward,
                    Evidence = evidence, Score = -intent.TotalRouteSeconds,
                    Intent = intent, Timing = timing, TitanCost = titanCost,
                    Deadline = deadline, Opportunity = opportunity
                };
                intents.Add(intent);
                admissions[intent.ProfileCode] = admission;
            }
            var selection = ChallengeIntentSelector.SelectOne(intents);
            if (selection.Selected == null)
            {
                evidenceSummary = "Challenge HOLD: no admission-grade exact-key route; "
                                  + titanEvidence + RejectionSuffix(rejected);
                return empty;
            }
            var selected = admissions[selection.Selected.ProfileCode];
            var alternatives = selection.Alternatives.Length == 0 ? "none"
                : string.Join(", ", selection.Alternatives.Select(x => x.ProfileCode
                    + "=" + FormatSeconds(x.TotalRouteSeconds)).ToArray());
            evidenceSummary = AdmissionSummary(selected) + " | diagnostic alternatives: "
                              + alternatives + " | " + titanEvidence;
            return new List<ChallengeAdmission> {selected};
        }

        internal static ActiveChallengePolicy ActivePolicy(Character c,
            LaserPhaseInput laserInput, int exactRebirthSeconds)
        {
            if (c == null || c.challenges == null || c.allChallenges == null
                || c.settings == null || !c.challenges.inChallenge) return null;
            ChallengeType type;
            if (!TryOneActiveType(c, out type)) return null;
            var difficulty = DifficultyOf(c.settings.rebirthDifficulty);
            var complete = CurrentCompletions(c, type);
            var maximum = Maximum(c, type);
            var exactTarget = ChallengeMechanics.ExactTarget(type, complete);
            var nativeTarget = NativeTarget(c, type);
            var p = new ActiveChallengePolicy
            {
                Type = type, Code = ChallengeMechanics.Code(type),
                CompletedBefore = complete, MaxCompletions = maximum,
                TargetBoss = type == ChallengeType.LaserSword ? -1 : exactTarget,
                TargetLevel = type == ChallengeType.LaserSword ? exactTarget : -1,
                OfflineMode = ChallengeMechanics.OfflineKind(type),
                MechanicallyAllowsRebirth = AllowsOrdinaryRebirth(type),
                RulesSummary = RulesSummary(type),
                RebirthPolicySummary = AllowsOrdinaryRebirth(type)
                    ? "Ordinary rebirths are allowed; no valid strategic checkpoint is available yet."
                    : "This challenge forbids ordinary rebirths.",
                ForbidRebirth = true, RebirthSeconds = -1
            };
            if (nativeTarget != exactTarget)
            {
                p.Objective = "hold: native target does not match the installed exact formula";
                p.EtaReason = p.Objective + " (native " + nativeTarget
                              + ", exact " + exactTarget + ")";
                return p;
            }

            if (type == ChallengeType.LaserSword)
            {
                p.RequiresLaserSwordAllocation = true;
                var aug = c.augments.augs[6];
                var input = laserInput ?? new LaserPhaseInput
                {
                    AugmentLevel = aug.augLevel, UpgradeLevel = aug.upgradeLevel
                };
                p.LaserPhase = LaserChallengeMechanics.Evaluate(input);
                p.ForbidRebirth = p.LaserPhase.ForbidRebirth
                                  || !ValidExactRebirthEvent(c, exactRebirthSeconds);
                if (!p.ForbidRebirth) p.RebirthSeconds = exactRebirthSeconds;
                p.Objective = "raise both Laser tracks to " + exactTarget + "; "
                              + p.LaserPhase.Reason;
            }
            else if (type == ChallengeType.NoRebirth)
            {
                p.ForbidRebirth = true;
                p.Objective = "continuous no-reset path to Boss " + (exactTarget + 1);
            }
            else if (type == ChallengeType.Troll)
            {
                p.RequiresTrollDialogService = true;
                p.NextTrollEvent = TrollChallengeMechanics.NextEvent(
                    c.challenges.trollCounter, complete);
                if (ValidExactRebirthEvent(c, exactRebirthSeconds))
                {
                    var untilReset = Math.Max(0, exactRebirthSeconds
                        - (int)Math.Floor(c.rebirthTime.totalseconds));
                    var reset = TrollChallengeMechanics.EvaluatePlannedReset(
                        c.challenges.trollCounter, complete, untilReset, 0, false);
                    p.ForbidRebirth = !reset.Allowed;
                    if (!p.ForbidRebirth) p.RebirthSeconds = exactRebirthSeconds;
                }
                p.Objective = "reach Boss " + (exactTarget + 1) + "; Troll counter "
                              + c.challenges.trollCounter + ", factor "
                              + p.NextTrollEvent.FactorSeconds + ", next "
                              + p.NextTrollEvent.Kind + " in "
                              + p.NextTrollEvent.SecondsUntilEvent + "s";
            }
            else
            {
                if (ValidExactRebirthEvent(c, exactRebirthSeconds))
                {
                    p.ForbidRebirth = false;
                    p.RebirthSeconds = exactRebirthSeconds;
                }
                p.Objective = type == ChallengeType.Basic
                    ? "reach Boss " + (exactTarget + 1) + "; no systems are disabled"
                    : "reach Boss " + (exactTarget + 1)
                      + " under " + p.Code + " restrictions";
            }

            if (type == ChallengeType.OneHundredLC)
            {
                p.RequiresHundredLevelBudget = true;
                p.HundredLevelSpent = (int)Math.Min(int.MaxValue,
                    Math.Max(0L, c.settings.rebirthLevels));
                p.HundredLevelRemaining = HundredLevelBudget.TrueRemaining(
                    p.HundredLevelSpent);
                p.Objective += "; shared 100-Level budget " + p.HundredLevelSpent
                               + "/100, exact remaining " + p.HundredLevelRemaining;
            }
            ApplyActiveTiming(c, p, difficulty, exactTarget);
            if (p.ForbidRebirth && type != ChallengeType.NoRebirth
                && (p.LaserPhase == null
                    || p.LaserPhase.Phase != LaserChallengePhase.Commit))
                p.EtaReason += "; destructive reset frozen until an exact successor route is supplied";
            p.RebirthPolicySummary = !p.MechanicallyAllowsRebirth
                ? "This challenge forbids ordinary rebirths."
                : !p.ForbidRebirth
                    ? "Ordinary rebirths are allowed; the normal optimizer selected run age "
                      + p.RebirthSeconds + "s."
                    : type == ChallengeType.LaserSword && p.LaserPhase != null
                      && p.LaserPhase.Phase == LaserChallengePhase.Commit
                        ? "Ordinary rebirths are allowed, but the current paired Laser progress is being protected."
                        : "Ordinary rebirths are allowed; the bot is waiting for a valid strategic checkpoint.";
            return p;
        }

        /*
        PLAYER-FACING CHALLENGE RULES

        A negative planner countdown is not a game rule.  Native challenge legality permits an
        ordinary rebirth in every challenge except No-Rebirth; Laser and Troll can still make a
        particular reset strategically wasteful.  Keep that mechanical fact separate from the
        current policy hold so telemetry and dashboards never relabel Basic as a no-reset mode.
        */
        internal static bool AllowsOrdinaryRebirth(ChallengeType type)
        {
            return type != ChallengeType.NoRebirth;
        }

        internal static string RulesSummary(ChallengeType type)
        {
            switch (type)
            {
                case ChallengeType.Basic:
                    return "No systems are disabled. Ordinary rebirths are allowed.";
                case ChallengeType.NoAug:
                    return "Augments and upgrades are disabled. Ordinary rebirths are allowed.";
                case ChallengeType.TwentyFourHour:
                    return "Finish within 24 hours of active challenge time. Ordinary rebirths are allowed.";
                case ChallengeType.OneHundredLC:
                    return "Each rebirth has one shared 100-level budget. Ordinary rebirths are allowed.";
                case ChallengeType.NoEquip:
                    return "Equipment effects are disabled. Ordinary rebirths are allowed.";
                case ChallengeType.Troll:
                    return "Troll effects follow their native counter. Ordinary rebirths are allowed; they clear active penalties but preserve that counter.";
                case ChallengeType.NoRebirth:
                    return "Reach the target in one continuous run; ordinary rebirths are forbidden.";
                case ChallengeType.LaserSword:
                    return "Raise both Laser Sword tracks to the target. Rebirths are allowed but reset run-local track progress.";
                case ChallengeType.Blind:
                    return "The challenge hides normal feedback and its timer does not advance offline. Ordinary rebirths are allowed.";
                case ChallengeType.NoNGU:
                    return "NGU effects and progress are disabled. Ordinary rebirths are allowed.";
                case ChallengeType.NoTimeMachine:
                    return "The Time Machine is unavailable. Ordinary rebirths are allowed.";
                default:
                    throw new ArgumentOutOfRangeException("type");
            }
        }

        private static void ApplyActiveTiming(Character c, ActiveChallengePolicy p,
            ChallengeDifficultyBand difficulty, int exactTarget)
        {
            var elapsed = ActiveElapsedSeconds(c, p.Type);
            var key = CreateTimingKey(p.Type, difficulty, p.CompletedBefore, exactTarget);
            ChallengeTimingEstimate timing;
            if (!TryTimingEstimate(key, out timing) || !timing.AdmissionGrade
                || !Finite(timing.UpperClearSeconds))
            {
                p.EtaSeconds = -1;
                p.PessimisticTotalSeconds = -1.0;
                p.EtaReason = "ETA unknown: no admission-grade exact-key route; " + p.Objective;
                if (p.Type == ChallengeType.TwentyFourHour)
                {
                    var reserve = ChallengeMechanics.TwentyFourHourDeadlineSeconds - elapsed;
                    p.Deadline = new ChallengeDeadlineProjection
                    {
                        ActiveSeconds = elapsed,
                        RemainingUpperSeconds = -1.0,
                        DeadlineSlackSeconds = reserve,
                        Missed = reserve <= 0.0,
                        AtRisk = true,
                        Evidence = reserve <= 0.0
                            ? "MISSED: native active-time deadline reached"
                            : "AT RISK: remaining upper bound unavailable; raw time reserve only"
                    };
                    p.EtaReason += "; deadline reserve " + FormatSeconds(reserve)
                                   + " but route slack is unknown";
                }
                return;
            }
            p.Timing = timing;
            p.PessimisticTotalSeconds = timing.UpperClearSeconds;
            var remaining = Math.Max(0.0, timing.UpperClearSeconds - elapsed);
            p.EtaSeconds = (int)Math.Min(int.MaxValue, Math.Ceiling(remaining));
            p.EtaReason = p.EtaSeconds + "s remaining from " + timing.EvidenceLabel
                          + " exact key; " + p.Objective;
            if (timing.P90LabelAllowed)
                p.EtaReason += "; " + timing.QuantileLabel + " coverage "
                               + timing.EmpiricalCoverage.ToString("0.000",
                                   CultureInfo.InvariantCulture);
            if (p.Type == ChallengeType.TwentyFourHour)
            {
                p.Deadline = ChallengeMechanics.EvaluateTwentyFourHourDeadline(
                    elapsed, remaining, DeadlineSafetyMarginSeconds);
                p.EtaReason += "; deadline slack "
                               + FormatSeconds(p.Deadline.DeadlineSlackSeconds)
                               + " " + p.Deadline.Evidence;
            }
        }

        private static List<LiveCandidate> LiveCandidates(Character c)
        {
            var a = c.allChallenges;
            return new List<LiveCandidate>
            {
                C(ChallengeType.Basic, a.basicChallenge.currentCompletions(),
                    a.basicChallenge.maxCompletions, a.basicChallenge.targetBoss(), false,
                    "hard entry; no systems disabled", a.basicChallenge.expectedEXP(),
                    a.basicChallenge.expectedAPReward(), a.basicChallenge.specialRewards()),
                C(ChallengeType.NoAug, a.noAugsChallenge.currentCompletions(),
                    a.noAugsChallenge.maxCompletions, a.noAugsChallenge.targetBoss(), false,
                    "hard entry; Augments and Upgrades disabled", a.noAugsChallenge.expectedEXP(),
                    a.noAugsChallenge.expectedAPReward(), a.noAugsChallenge.specialRewards()),
                C(ChallengeType.TwentyFourHour, a.hour24Challenge.currentCompletions(),
                    a.hour24Challenge.maxCompletions, a.hour24Challenge.targetBoss(), false,
                    "hard entry; active-time deadline; offline frozen", a.hour24Challenge.expectedEXP(),
                    a.hour24Challenge.expectedAPReward(), a.hour24Challenge.specialRewards()),
                C(ChallengeType.OneHundredLC, a.level100Challenge.currentCompletions(),
                    a.level100Challenge.maxCompletions, a.level100Challenge.targetBoss(), false,
                    "hard entry; one shared 100-completed-level budget per rebirth", a.level100Challenge.expectedEXP(),
                    a.level100Challenge.expectedAPReward(), a.level100Challenge.specialRewards()),
                C(ChallengeType.NoEquip, a.noEquipmentChallenge.currentCompletions(),
                    a.noEquipmentChallenge.maxCompletions, a.noEquipmentChallenge.targetBoss(), false,
                    "hard entry; equipment effects disabled", a.noEquipmentChallenge.expectedEXP(),
                    a.noEquipmentChallenge.expectedAPReward(), a.noEquipmentChallenge.specialRewards()),
                C(ChallengeType.Troll, a.trollChallenge.currentCompletions(),
                    a.trollChallenge.maxCompletions, a.trollChallenge.targetBoss(), false,
                    "hard entry; exact persistent-counter Troll cadence; offline frozen", a.trollChallenge.expectedEXP(),
                    a.trollChallenge.expectedAPReward(), a.trollChallenge.specialRewards()),
                C(ChallengeType.NoRebirth, a.noRebirthChallenge.currentCompletions(),
                    a.noRebirthChallenge.maxCompletions, a.noRebirthChallenge.targetBoss(), false,
                    "hard entry; one continuous no-reset path", a.noRebirthChallenge.expectedEXP(),
                    a.noRebirthChallenge.expectedAPReward(), a.noRebirthChallenge.specialRewards()),
                C(ChallengeType.LaserSword, a.laserSwordChallenge.currentCompletions(),
                    a.laserSwordChallenge.maxCompletions,
                    a.laserSwordChallenge.laserSwordTarget(), true,
                    "soft entry; both pair tracks; build then commit", a.laserSwordChallenge.expectedEXP(),
                    a.laserSwordChallenge.expectedAPReward(), a.laserSwordChallenge.specialRewards()),
                C(ChallengeType.Blind, a.blindChallenge.currentCompletions(),
                    a.blindChallenge.maxCompletions, a.blindChallenge.targetBoss(), false,
                    "hard entry; offline progress without challenge-timer advance", a.blindChallenge.expectedEXP(),
                    a.blindChallenge.expectedAPReward(), a.blindChallenge.specialRewards()),
                C(ChallengeType.NoNGU, a.NGUChallenge.currentCompletions(),
                    a.NGUChallenge.maxCompletions, a.NGUChallenge.targetBoss(), false,
                    "hard entry; NGU effects and progress disabled", a.NGUChallenge.expectedEXP(),
                    a.NGUChallenge.expectedAPReward(), a.NGUChallenge.specialRewards()),
                C(ChallengeType.NoTimeMachine, a.timeMachineChallenge.currentCompletions(),
                    a.timeMachineChallenge.maxCompletions, a.timeMachineChallenge.targetBoss(), false,
                    "hard entry; Time Machine unavailable", a.timeMachineChallenge.expectedEXP(),
                    a.timeMachineChallenge.expectedAPReward(), a.timeMachineChallenge.specialRewards())
            };
        }

        private static LiveCandidate C(ChallengeType type, int complete, int maximum,
            int target, bool level, string constraints, long expectedExp,
            string expectedAp, string special)
        {
            return new LiveCandidate
            {
                Type = type, Complete = complete, Maximum = maximum,
                NativeTarget = target, LevelTarget = level,
                ExpectedExp = expectedExp, Constraints = constraints,
                Reward = NativeReward(expectedExp, expectedAp, special)
            };
        }

        internal static bool TryCaptureTitanVector(Character c, out TitanVectorCost cost,
            out string evidence)
        {
            cost = null;
            evidence = "Titan vector unavailable";
            if (c.adventure == null || c.allChallenges == null) return false;
            var elapsed = new double[14];
            var valued = new bool[14];
            var futureKills = new int[14];
            var reachable = ZoneHelpers.GetMaxReachableZone(true);
            var normal = c.allChallenges.noRebirthChallenge.completions();
            var evil = c.allChallenges.noRebirthChallenge.evilCompletions();
            var sadistic = c.allChallenges.noRebirthChallenge.sadisticCompletions();
            for (var titanId = 1; titanId <= 14; titanId++)
            {
                if (TitanMechanics.Describe(titanId).Zone > reachable
                    || !ZoneHelpers.TitanUnlockedForAttempt(titanId - 1)) continue;
                var remaining = ZoneHelpers.SecondsUntilTitanSpawn(titanId - 1);
                if (!Finite(remaining))
                {
                    evidence = "valuable Titan " + titanId + " clock could not be read";
                    return false;
                }
                var due = TitanMechanics.SpawnSeconds(titanId, normal, evil, sadistic);
                elapsed[titanId - 1] = Math.Max(0.0, due - Math.Min(due, remaining));
                valued[titanId - 1] = true;
                futureKills[titanId - 1] = 1;
            }
            cost = ChallengeMechanics.EvaluateTitanClockLoss(
                new TitanClockSnapshot(elapsed), valued, futureKills,
                normal, evil, sadistic);
            evidence = "Titan reset vector cost=" + cost.TotalCycleDelaySeconds
                       + "s, valued=" + cost.Items.Length + ", ready=" + cost.AnyReady;
            return true;
        }

        internal static ChallengeTimingKey CreateTimingKey(ChallengeType type,
            ChallengeDifficultyBand difficulty, int completedBefore, int target)
        {
            return new ChallengeTimingKey
            {
                AssemblySha256 = Main.GameAssemblySha256 ?? string.Empty,
                Type = type, Difficulty = difficulty,
                CompletedBefore = completedBefore, ExactTarget = target,
                ResetPolicySignature = ResetPolicySignature(type)
            };
        }

        internal static string ResetPolicySignature(ChallengeType type)
        {
            return "challenge-route-v1|entry=" + ChallengeMechanics.EntryKind(type)
                   + "|offline=" + ChallengeMechanics.OfflineKind(type)
                   + "|rebirth=task15-exact|allocation=task18-exact|gold=task19-ledger";
        }

        internal static string ExpectedStateVersion(Character c, ChallengeType type,
            ChallengeDifficultyBand difficulty, int completedBefore, int target)
        {
            return (Main.GameAssemblySha256 ?? string.Empty) + "|s="
                   + ExecutionSafety.StateVersion + "|d=" + difficulty + "|t=" + type
                   + "|c=" + completedBefore + "|target=" + target + "|boss=" + c.bossID
                   + "|run=" + Math.Floor(c.rebirthTime.totalseconds);
        }

        /* Titan clocks and sub-second run time are priced/revalidated separately. Excluding those
           continuously moving values lets one same-frame source model survive until Apply while
           Boss, Number, challenge, difficulty, and persistent progression remain exact. */
        internal static string OpportunityProgressionFingerprint(ResetExecutionSnapshot value)
        {
            if (value == null || value.Number == null) return string.Empty;
            var inv = CultureInfo.InvariantCulture;
            return value.RebirthNumber + "|difficulty=" + value.CurrentDifficulty + "/"
                   + value.NextDifficulty + "/" + value.NguLevelTrack + "|number="
                   + value.Number.CurrentAttack.ToString("R", inv) + ","
                   + value.Number.CurrentDefense.ToString("R", inv) + ","
                   + value.Number.NextAttack.ToString("R", inv) + ","
                   + value.Number.NextDefense.ToString("R", inv) + ","
                   + value.Number.BossMultiplier.ToString("R", inv) + ","
                   + value.Number.TimeMultiplier.ToString("R", inv) + ","
                   + value.Number.OldBossMultiplier.ToString("R", inv) + ","
                   + value.Number.OldTimeMultiplier.ToString("R", inv) + "|boss="
                   + value.BossId + "/" + value.CurrentHighestBoss + "|records="
                   + value.HighestBoss + "/" + value.HighestHardBoss + "/"
                   + value.HighestSadisticBoss + "|challenge=" + value.InChallenge + ":"
                   + (value.CurrentChallengeTypeToken ?? string.Empty) + ":"
                   + (value.ChallengeFlags == null ? "missing"
                       : string.Join(",", value.ChallengeFlags)) + "|persistent="
                   + (value.PersistentStateFingerprint ?? string.Empty);
        }

        private static bool TryOneActiveType(Character c, out ChallengeType type)
        {
            var active = new List<ChallengeType>();
            if (c.challenges.basicChallenge.inChallenge) active.Add(ChallengeType.Basic);
            if (c.challenges.noAugsChallenge.inChallenge) active.Add(ChallengeType.NoAug);
            if (c.challenges.hour24Challenge.inChallenge) active.Add(ChallengeType.TwentyFourHour);
            if (c.challenges.levelChallenge10k.inChallenge) active.Add(ChallengeType.OneHundredLC);
            if (c.challenges.noEquipmentChallenge.inChallenge) active.Add(ChallengeType.NoEquip);
            if (c.challenges.trollChallenge.inChallenge) active.Add(ChallengeType.Troll);
            if (c.challenges.noRebirthChallenge.inChallenge) active.Add(ChallengeType.NoRebirth);
            if (c.challenges.laserSwordChallenge.inChallenge) active.Add(ChallengeType.LaserSword);
            if (c.challenges.blindChallenge.inChallenge) active.Add(ChallengeType.Blind);
            if (c.challenges.nguChallenge.inChallenge) active.Add(ChallengeType.NoNGU);
            if (c.challenges.timeMachineChallenge.inChallenge) active.Add(ChallengeType.NoTimeMachine);
            type = active.Count == 1 ? active[0] : ChallengeType.Basic;
            return active.Count == 1;
        }

        internal static int CurrentCompletions(Character c, ChallengeType type)
        {
            switch (type)
            {
                case ChallengeType.Basic: return c.allChallenges.basicChallenge.currentCompletions();
                case ChallengeType.NoAug: return c.allChallenges.noAugsChallenge.currentCompletions();
                case ChallengeType.TwentyFourHour: return c.allChallenges.hour24Challenge.currentCompletions();
                case ChallengeType.OneHundredLC: return c.allChallenges.level100Challenge.currentCompletions();
                case ChallengeType.NoEquip: return c.allChallenges.noEquipmentChallenge.currentCompletions();
                case ChallengeType.Troll: return c.allChallenges.trollChallenge.currentCompletions();
                case ChallengeType.NoRebirth: return c.allChallenges.noRebirthChallenge.currentCompletions();
                case ChallengeType.LaserSword: return c.allChallenges.laserSwordChallenge.currentCompletions();
                case ChallengeType.Blind: return c.allChallenges.blindChallenge.currentCompletions();
                case ChallengeType.NoNGU: return c.allChallenges.NGUChallenge.currentCompletions();
                case ChallengeType.NoTimeMachine: return c.allChallenges.timeMachineChallenge.currentCompletions();
                default: throw new ArgumentOutOfRangeException("type");
            }
        }

        private static int Maximum(Character c, ChallengeType type)
        {
            switch (type)
            {
                case ChallengeType.Basic: return c.allChallenges.basicChallenge.maxCompletions;
                case ChallengeType.NoAug: return c.allChallenges.noAugsChallenge.maxCompletions;
                case ChallengeType.TwentyFourHour: return c.allChallenges.hour24Challenge.maxCompletions;
                case ChallengeType.OneHundredLC: return c.allChallenges.level100Challenge.maxCompletions;
                case ChallengeType.NoEquip: return c.allChallenges.noEquipmentChallenge.maxCompletions;
                case ChallengeType.Troll: return c.allChallenges.trollChallenge.maxCompletions;
                case ChallengeType.NoRebirth: return c.allChallenges.noRebirthChallenge.maxCompletions;
                case ChallengeType.LaserSword: return c.allChallenges.laserSwordChallenge.maxCompletions;
                case ChallengeType.Blind: return c.allChallenges.blindChallenge.maxCompletions;
                case ChallengeType.NoNGU: return c.allChallenges.NGUChallenge.maxCompletions;
                case ChallengeType.NoTimeMachine: return c.allChallenges.timeMachineChallenge.maxCompletions;
                default: throw new ArgumentOutOfRangeException("type");
            }
        }

        private static int NativeTarget(Character c, ChallengeType type)
        {
            switch (type)
            {
                case ChallengeType.Basic: return c.allChallenges.basicChallenge.targetBoss();
                case ChallengeType.NoAug: return c.allChallenges.noAugsChallenge.targetBoss();
                case ChallengeType.TwentyFourHour: return c.allChallenges.hour24Challenge.targetBoss();
                case ChallengeType.OneHundredLC: return c.allChallenges.level100Challenge.targetBoss();
                case ChallengeType.NoEquip: return c.allChallenges.noEquipmentChallenge.targetBoss();
                case ChallengeType.Troll: return c.allChallenges.trollChallenge.targetBoss();
                case ChallengeType.NoRebirth: return c.allChallenges.noRebirthChallenge.targetBoss();
                case ChallengeType.LaserSword: return c.allChallenges.laserSwordChallenge.laserSwordTarget();
                case ChallengeType.Blind: return c.allChallenges.blindChallenge.targetBoss();
                case ChallengeType.NoNGU: return c.allChallenges.NGUChallenge.targetBoss();
                case ChallengeType.NoTimeMachine: return c.allChallenges.timeMachineChallenge.targetBoss();
                default: throw new ArgumentOutOfRangeException("type");
            }
        }

        private static double ActiveElapsedSeconds(Character c, ChallengeType type)
        {
            switch (type)
            {
                case ChallengeType.Basic: return c.challenges.basicChallenge.challengeTime.totalseconds;
                case ChallengeType.NoAug: return c.challenges.noAugsChallenge.challengeTime.totalseconds;
                case ChallengeType.TwentyFourHour: return c.challenges.hour24Challenge.challengeTime.totalseconds;
                case ChallengeType.OneHundredLC: return c.challenges.levelChallenge10k.challengeTime.totalseconds;
                case ChallengeType.NoEquip: return c.challenges.noEquipmentChallenge.challengeTime.totalseconds;
                case ChallengeType.Troll: return c.challenges.trollChallenge.challengeTime.totalseconds;
                case ChallengeType.NoRebirth: return c.challenges.noRebirthChallenge.challengeTime.totalseconds;
                case ChallengeType.LaserSword: return c.challenges.laserSwordChallenge.challengeTime.totalseconds;
                case ChallengeType.Blind: return c.challenges.blindChallenge.challengeTime.totalseconds;
                case ChallengeType.NoNGU: return c.challenges.nguChallenge.challengeTime.totalseconds;
                case ChallengeType.NoTimeMachine: return c.challenges.timeMachineChallenge.challengeTime.totalseconds;
                default: throw new ArgumentOutOfRangeException("type");
            }
        }

        private static ChallengeDifficultyBand DifficultyOf(difficulty value)
        {
            return value == difficulty.normal ? ChallengeDifficultyBand.Normal
                : value == difficulty.evil ? ChallengeDifficultyBand.Evil
                : ChallengeDifficultyBand.Sadistic;
        }

        private static bool ValidExactRebirthEvent(Character c, int targetSeconds)
        {
            if (c == null || c.rebirth == null || c.rebirthTime == null
                || targetSeconds < 0) return false;
            var minimum = Math.Ceiling((double)c.rebirth.minRebirthTime());
            // The stage optimizer owns admission. Once its checkpoint is due, the integer target
            // is normally below the live fractional run timer; requiring target >= now made the
            // checkpoint invalid precisely when it was executable and rolled it forward forever.
            // Active challenge policy checks only native reset legality here. The transaction
            // re-captures the root and all reset boundaries immediately before mutation.
            return AdmittedRebirthCheckpointIsLegal(minimum, targetSeconds);
        }

        internal static bool AdmittedRebirthCheckpointIsLegal(double nativeMinimumSeconds,
            int targetSeconds)
        {
            return !double.IsNaN(nativeMinimumSeconds)
                   && !double.IsInfinity(nativeMinimumSeconds)
                   && nativeMinimumSeconds >= 0.0
                   && targetSeconds >= 0
                   && targetSeconds + 1e-12 >= Math.Ceiling(nativeMinimumSeconds);
        }

        private static string AdmissionSummary(ChallengeAdmission x)
        {
            var target = x.TargetLevel >= 0 ? "both levels " + x.TargetLevel
                : "Boss " + (x.TargetBoss + 1);
            return x.ProfileCode + " selected [" + x.CompletedBefore + "/"
                   + x.MaxCompletions + ", " + target + ", upper " + x.EtaText
                   + ", recovery " + FormatSeconds(x.RecoverySeconds)
                   + ", Titan-vector " + FormatSeconds(x.TitanOpportunitySeconds)
                   + "]: " + x.Evidence + "; " + x.Reward;
        }

        private static string RejectionSuffix(ICollection<string> rejected)
        {
            return rejected == null || rejected.Count == 0 ? string.Empty
                : " | " + string.Join("; ", rejected.Take(11).ToArray());
        }

        private static string FormatSeconds(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "unknown";
            return seconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
        }

        private static string NativeReward(long expectedExp, string expectedAp, string special)
        {
            return "native next reward " + expectedExp + " EXP, AP "
                   + Compact(expectedAp) + ", " + Compact(special);
        }

        private static string Compact(string value)
        {
            return string.IsNullOrEmpty(value) ? "none"
                : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static bool Finite(double value)
        {
            return value >= 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool FinitePositive(double value)
        {
            return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool ExactEvidence(ChallengeTimingEvidenceKind kind)
        {
            return kind == ChallengeTimingEvidenceKind.ExactDeterministic
                   || kind == ChallengeTimingEvidenceKind.NativeFormulaSimulation;
        }

        private static bool Same(double left, double right)
        {
            return left.ToString("R", CultureInfo.InvariantCulture) ==
                   right.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string OpportunityKey(ChallengeTimingKey key,
            string stateVersion, string progressionFingerprint)
        {
            return (key == null ? "missing" : key.ToString()) + "|state="
                   + (stateVersion ?? string.Empty) + "|progression="
                   + (progressionFingerprint ?? string.Empty);
        }

        private static void ValidateFiniteNonNegative(double value, string name)
        {
            if (!Finite(value)) throw new ArgumentOutOfRangeException(name);
        }

        private static void ValidateFinitePositive(double value, string name)
        {
            if (!FinitePositive(value)) throw new ArgumentOutOfRangeException(name);
        }
    }
}
