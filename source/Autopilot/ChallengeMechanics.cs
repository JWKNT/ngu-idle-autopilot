using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.AllocationProfiles.RebirthStuff;

/*
FILE PURPOSE

Purpose: ChallengeMechanics is the pure installed-source contract for all eleven NGU Idle 1.260
challenge state machines. It owns exact target/count rules, hard-versus-Laser entry transforms,
completion/offline behavior, keyed timing evidence, the shared 100-Level budget, Troll cadence,
Laser build/commit decisions, 24-Hour slack, Titan-clock loss vectors, and short-batch cost.

Mechanism: Callers copy live state into the small records below. Entry reuses task 15's ordinary
rebirth transition, applies task 12's fourteen-clock reset, then performs the hard Number overwrite
for ten controllers or preserves the soft Laser result. Timing observations are keyed by build,
type, difficulty, ordinal, target, and reset policy; an exact-one selector keeps alternatives
diagnostic-only. Dedicated helpers operate only at native event boundaries.

Inputs and outputs: Inputs are typed scalar snapshots, RebirthTransitionState, TitanClockSnapshot,
per-track level requests, observed clear records, exact route ETAs, and configured maxima. Outputs
are successor states, completion predicates/count deltas, finite cadence/deadline/budget proofs,
timing estimates, one executable intent, and explicit evidence labels.

Invariants and safety: Ten entries hard-reset all eight Number fields to one; Laser alone banks the
synchronized preview. Every entry resets all Titan clocks. Basic Evil/Sadistic clears also increment
raw Normal Basic. Native global bestTime is never an input. The 100-Level cap is one shared budget,
Troll rebirth preserves its counter, No-Rebirth forbids reset, and no output says p90 without an
exact-key empirical coverage audit.

Extension points and non-goals: ChallengeStrategyPlanner adapts Character state, task 28 supplies
complete successor-route values, and tasks 17/29 own irreversible entry and manager quota wiring.
This file does not reflect, call controllers, write samples to disk, allocate resources, service
dialogs, enter/quit a challenge, or grant mutation authority.
*/
namespace NGUInjector.Autopilot
{
    internal enum ChallengeDifficultyBand
    {
        Normal = 0,
        Evil = 1,
        Sadistic = 2
    }

    internal enum ChallengeEntryTransformKind
    {
        HardReset = 0,
        LaserSoftReset = 1
    }

    internal enum ChallengeOfflineTransformKind
    {
        Frozen = 0,
        ProgressWithoutChallengeTimer = 1,
        ProgressAndChallengeTimer = 2
    }

    internal enum ChallengeTimeWriteKind
    {
        GlobalMinimum = 0,
        GlobalLatest = 1
    }

    internal enum ChallengeTimingEvidenceKind
    {
        HeuristicUnknown = 0,
        ExactDeterministic = 1,
        NativeFormulaSimulation = 2,
        EmpiricalObservation = 3
    }

    internal enum HundredLevelTrack
    {
        AdvancedTraining = 0,
        Augment = 1,
        Upgrade = 2,
        Beard = 3,
        BloodRitual = 4,
        TimeMachineSpeed = 5,
        TimeMachineGold = 6,
        WandoosEnergy = 7,
        WandoosMagic = 8,
        BasicTraining = 9,
        Ngu = 10
    }

    internal enum TrollEventKind
    {
        Small = 0,
        Big = 1
    }

    internal enum LaserChallengePhase
    {
        Unknown = 0,
        NumberBuilding = 1,
        Commit = 2
    }

    internal sealed class ChallengeCompletionCounts
    {
        internal int RawNormal;
        internal int RawEvil;
        internal int RawSadistic;
        internal int SerializedMaximum;

        internal ChallengeCompletionCounts Clone()
        {
            return (ChallengeCompletionCounts)MemberwiseClone();
        }

        internal int Current(ChallengeDifficultyBand difficulty)
        {
            var raw = difficulty == ChallengeDifficultyBand.Normal ? RawNormal
                : difficulty == ChallengeDifficultyBand.Evil ? RawEvil : RawSadistic;
            return Math.Max(0, Math.Min(Math.Max(0, SerializedMaximum), raw));
        }
    }

    internal sealed class ChallengeTransitionState
    {
        internal ChallengeType Type;
        internal ChallengeDifficultyBand Difficulty;
        internal ChallengeCompletionCounts Counts = new ChallengeCompletionCounts();
        internal RebirthTransitionState Rebirth = new RebirthTransitionState();
        internal TitanClockSnapshot TitanClocks = new TitanClockSnapshot();
        internal bool[] ActiveFlags = new bool[11];
        internal bool InChallenge;
        internal double PublishedNextAttack = 1.0;
        internal double PublishedNextDefense = 1.0;
        internal double ChallengeSeconds;
        internal double OrdinaryOfflineProgressSeconds;
        internal long RebirthLevels;
        internal long ResetLocalProgress;
        internal int TrollCounter;
        internal bool TrollEquipmentDisabled;
        internal bool TrollNguDisabled;
        internal bool TrollBeardsDisabled;
        internal bool TrollWandoosDisabled;
        internal bool TrollMenuSwapped;
        internal bool TrollBossDivided;

        internal ChallengeTransitionState Clone()
        {
            var copy = (ChallengeTransitionState)MemberwiseClone();
            copy.Counts = Counts == null ? new ChallengeCompletionCounts() : Counts.Clone();
            copy.Rebirth = Rebirth == null ? new RebirthTransitionState() : Rebirth.Clone();
            copy.TitanClocks = TitanClocks == null
                ? new TitanClockSnapshot() : new TitanClockSnapshot(TitanClocks.ToArray());
            copy.ActiveFlags = ActiveFlags == null ? new bool[11] : (bool[])ActiveFlags.Clone();
            return copy;
        }
    }

    internal sealed class ChallengeDeadlineProjection
    {
        internal double ActiveSeconds;
        internal double RemainingUpperSeconds;
        internal double DeadlineSlackSeconds;
        internal bool Missed;
        internal bool AtRisk;
        internal string Evidence = string.Empty;
    }

    internal sealed class TwentyFourHourFrameResult
    {
        internal bool FailureDispatched;
        internal bool CompletionDispatched;
        internal bool NativeSameFrameRace;
    }

    internal sealed class ChallengeTimingKey : IEquatable<ChallengeTimingKey>
    {
        internal string AssemblySha256 = string.Empty;
        internal ChallengeType Type;
        internal ChallengeDifficultyBand Difficulty;
        internal int CompletedBefore;
        internal int ExactTarget;
        internal string ResetPolicySignature = string.Empty;

        internal ChallengeTimingKey Clone()
        {
            return (ChallengeTimingKey)MemberwiseClone();
        }

        public bool Equals(ChallengeTimingKey other)
        {
            return other != null
                   && string.Equals(AssemblySha256, other.AssemblySha256,
                       StringComparison.OrdinalIgnoreCase)
                   && Type == other.Type && Difficulty == other.Difficulty
                   && CompletedBefore == other.CompletedBefore && ExactTarget == other.ExactTarget
                   && string.Equals(ResetPolicySignature, other.ResetPolicySignature,
                       StringComparison.Ordinal);
        }

        public override bool Equals(object value)
        {
            return Equals(value as ChallengeTimingKey);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(AssemblySha256 ?? string.Empty);
                hash = hash * 397 ^ (int)Type;
                hash = hash * 397 ^ (int)Difficulty;
                hash = hash * 397 ^ CompletedBefore;
                hash = hash * 397 ^ ExactTarget;
                hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(
                    ResetPolicySignature ?? string.Empty);
                return hash;
            }
        }

        public override string ToString()
        {
            return (AssemblySha256 ?? string.Empty) + "|" + Type + "|" + Difficulty + "|"
                   + CompletedBefore + "|" + ExactTarget + "|"
                   + (ResetPolicySignature ?? string.Empty);
        }
    }

    internal sealed class ChallengeTimingSample
    {
        internal ChallengeTimingKey Key;
        internal ChallengeTimingEvidenceKind EvidenceKind;
        internal double ObservedOnlineSeconds;
        internal double ObservedOfflineSeconds;
        internal double RecoverySeconds;
        internal double PredictedUpperSeconds = -1.0;
        internal string StartStateSignature = string.Empty;
        internal string AllocationSignature = string.Empty;
        internal string ResetSequenceSignature = string.Empty;
        internal long FinishedUtcTicks;
    }

    internal sealed class ChallengeTimingEstimate
    {
        internal ChallengeTimingKey Key;
        internal int SampleCount;
        internal double MeanClearSeconds = -1.0;
        internal double UpperClearSeconds = -1.0;
        internal double RecoverySeconds = -1.0;
        internal double EmpiricalCoverage;
        internal bool AdmissionGrade;
        internal bool P90LabelAllowed;
        internal string EvidenceLabel = "heuristic/unknown";
        internal string QuantileLabel = string.Empty;
    }

    internal sealed class ChallengeTimingLedger
    {
        private readonly Dictionary<ChallengeTimingKey, List<ChallengeTimingSample>> _samples =
            new Dictionary<ChallengeTimingKey, List<ChallengeTimingSample>>();

        internal void Record(ChallengeTimingSample sample)
        {
            if (sample == null || sample.Key == null)
                throw new ArgumentNullException("sample");
            ValidateFiniteNonNegative(sample.ObservedOnlineSeconds, "ObservedOnlineSeconds");
            ValidateFiniteNonNegative(sample.ObservedOfflineSeconds, "ObservedOfflineSeconds");
            ValidateFiniteNonNegative(sample.RecoverySeconds, "RecoverySeconds");
            if (sample.PredictedUpperSeconds >= 0.0)
                ValidateFiniteNonNegative(sample.PredictedUpperSeconds, "PredictedUpperSeconds");
            if (sample.FinishedUtcTicks < 0L)
                throw new ArgumentOutOfRangeException("FinishedUtcTicks");
            var key = sample.Key.Clone();
            List<ChallengeTimingSample> values;
            if (!_samples.TryGetValue(key, out values))
            {
                values = new List<ChallengeTimingSample>();
                _samples.Add(key, values);
            }
            values.Add(new ChallengeTimingSample
            {
                Key = key,
                EvidenceKind = sample.EvidenceKind,
                ObservedOnlineSeconds = sample.ObservedOnlineSeconds,
                ObservedOfflineSeconds = sample.ObservedOfflineSeconds,
                RecoverySeconds = sample.RecoverySeconds,
                PredictedUpperSeconds = sample.PredictedUpperSeconds,
                StartStateSignature = sample.StartStateSignature ?? string.Empty,
                AllocationSignature = sample.AllocationSignature ?? string.Empty,
                ResetSequenceSignature = sample.ResetSequenceSignature ?? string.Empty,
                FinishedUtcTicks = sample.FinishedUtcTicks
            });
        }

        internal bool TryEstimate(ChallengeTimingKey key, out ChallengeTimingEstimate estimate)
        {
            estimate = null;
            if (key == null) return false;
            List<ChallengeTimingSample> source;
            if (!_samples.TryGetValue(key, out source) || source.Count == 0) return false;
            var clear = source.Select(x => x.ObservedOnlineSeconds + x.ObservedOfflineSeconds)
                .OrderBy(x => x).ToArray();
            var exact = source.Where(x => x.EvidenceKind ==
                                                ChallengeTimingEvidenceKind.ExactDeterministic
                                            || x.EvidenceKind ==
                                                ChallengeTimingEvidenceKind.NativeFormulaSimulation)
                .ToArray();
            var empirical = source.Count(x => x.EvidenceKind ==
                                                ChallengeTimingEvidenceKind.EmpiricalObservation);
            var covered = source.Count(x => x.PredictedUpperSeconds >= 0.0
                                            && x.ObservedOnlineSeconds
                                               + x.ObservedOfflineSeconds
                                               <= x.PredictedUpperSeconds + 1e-12);
            var eligibleCoverage = source.Count(x => x.PredictedUpperSeconds >= 0.0);
            var coverage = eligibleCoverage == 0 ? 0.0 : covered / (double)eligibleCoverage;
            var calibrated = exact.Length == 0 && empirical == source.Count && source.Count >= 20
                             && eligibleCoverage == source.Count && coverage >= 0.90;
            var upper = exact.Length > 0
                ? exact.Max(x => x.PredictedUpperSeconds >= 0.0
                    ? x.PredictedUpperSeconds
                    : x.ObservedOnlineSeconds + x.ObservedOfflineSeconds)
                : calibrated ? clear[Math.Max(0,
                    Math.Min(clear.Length - 1, (int)Math.Ceiling(clear.Length * .90) - 1))]
                : source.Where(x => x.PredictedUpperSeconds >= 0.0)
                    .Select(x => x.PredictedUpperSeconds).DefaultIfEmpty(-1.0).Max();
            estimate = new ChallengeTimingEstimate
            {
                Key = key.Clone(),
                SampleCount = source.Count,
                MeanClearSeconds = clear.Average(),
                UpperClearSeconds = upper,
                RecoverySeconds = source.Max(x => x.RecoverySeconds),
                EmpiricalCoverage = coverage,
                AdmissionGrade = exact.Length > 0 || calibrated,
                P90LabelAllowed = calibrated,
                EvidenceLabel = exact.Length > 0
                    ? (exact.Any(x => x.EvidenceKind ==
                                     ChallengeTimingEvidenceKind.ExactDeterministic)
                        ? "exact deterministic" : "native-formula simulation")
                    : calibrated ? "calibrated empirical interval"
                    : "heuristic/uncalibrated empirical",
                QuantileLabel = calibrated ? "p90" : string.Empty
            };
            return true;
        }

        internal int CountFor(ChallengeTimingKey key)
        {
            List<ChallengeTimingSample> values;
            return key != null && _samples.TryGetValue(key, out values) ? values.Count : 0;
        }

        private static void ValidateFiniteNonNegative(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                throw new ArgumentOutOfRangeException(name);
        }
    }

    internal sealed class ChallengeIntent
    {
        internal ChallengeType Type;
        internal int Completion;
        internal string ProfileCode = string.Empty;
        internal string ExpectedStateVersion = string.Empty;
        internal ChallengeTimingKey TimingKey;
        internal double TotalRouteSeconds = double.PositiveInfinity;
        internal string Evidence = string.Empty;
    }

    internal sealed class ChallengeIntentSelection
    {
        internal ChallengeIntent Selected;
        internal ChallengeIntent[] Executable = new ChallengeIntent[0];
        internal ChallengeIntent[] Alternatives = new ChallengeIntent[0];
    }

    internal static class ChallengeIntentSelector
    {
        internal static ChallengeIntentSelection SelectOne(IEnumerable<ChallengeIntent> source)
        {
            var ordered = (source ?? Enumerable.Empty<ChallengeIntent>())
                .Where(x => x != null && x.TimingKey != null
                            && x.Completion > 0
                            && !string.IsNullOrEmpty(x.ExpectedStateVersion)
                            && FiniteNonNegative(x.TotalRouteSeconds))
                .OrderBy(x => x.TotalRouteSeconds)
                .ThenBy(x => x.ProfileCode, StringComparer.Ordinal).ToArray();
            var selected = ordered.FirstOrDefault();
            return new ChallengeIntentSelection
            {
                Selected = selected,
                Executable = selected == null ? new ChallengeIntent[0]
                    : new[] {selected},
                Alternatives = selected == null ? new ChallengeIntent[0]
                    : ordered.Skip(1).ToArray()
            };
        }

        internal static bool StillValid(ChallengeIntent intent, ChallengeType type,
            int completion, string stateVersion)
        {
            return intent != null && intent.Type == type && intent.Completion == completion
                   && string.Equals(intent.ExpectedStateVersion, stateVersion,
                       StringComparison.Ordinal);
        }

        private static bool FiniteNonNegative(double value)
        {
            return value >= 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    internal sealed class HundredLevelBudgetRequest
    {
        internal HundredLevelTrack Track;
        internal int RequestedLevels;
        internal int AuthorizedQuota;
        internal int Priority;
    }

    internal sealed class HundredLevelBudgetGrant
    {
        internal HundredLevelTrack Track;
        internal int GrantedLevels;
        internal int BudgetSpent;
    }

    internal sealed class HundredLevelBudgetDecision
    {
        internal long SpentBefore;
        internal long SpentAfter;
        internal int Remaining;
        internal HundredLevelBudgetGrant[] Grants = new HundredLevelBudgetGrant[0];
    }

    internal static class HundredLevelBudget
    {
        internal const int MaximumLevelsPerRebirth = 100;

        internal static bool ConsumesSharedSlot(HundredLevelTrack track)
        {
            return track >= HundredLevelTrack.AdvancedTraining
                   && track <= HundredLevelTrack.WandoosMagic;
        }

        internal static bool CanLevel(long rebirthLevels)
        {
            return rebirthLevels >= 0L && rebirthLevels < MaximumLevelsPerRebirth;
        }

        internal static int TrueRemaining(long rebirthLevels)
        {
            return (int)Math.Max(0L, MaximumLevelsPerRebirth
                                     - Math.Max(0L, rebirthLevels));
        }

        internal static int NativeDisplayRemaining(long rebirthLevels)
        {
            return Math.Max(1, TrueRemaining(rebirthLevels));
        }

        internal static long ApplyOrdinaryRebirth(long rebirthLevels)
        {
            if (rebirthLevels < 0L) throw new ArgumentOutOfRangeException("rebirthLevels");
            return 0L;
        }

        internal static HundredLevelBudgetDecision Allocate(long spent,
            IEnumerable<HundredLevelBudgetRequest> source)
        {
            if (spent < 0L || spent > MaximumLevelsPerRebirth)
                throw new ArgumentOutOfRangeException("spent");
            var budgetSpent = spent;
            var grants = new List<HundredLevelBudgetGrant>();
            var ordered = (source ?? Enumerable.Empty<HundredLevelBudgetRequest>())
                .Where(x => x != null).OrderByDescending(x => x.Priority)
                .ThenBy(x => (int)x.Track).ToArray();
            var seen = new bool[11];
            foreach (var request in ordered)
            {
                var track = (int)request.Track;
                if (track < 0 || track >= seen.Length || seen[track])
                    throw new InvalidOperationException(
                        "100-Level requests require one unique known subsystem track");
                if (request.RequestedLevels < 0 || request.AuthorizedQuota < 0)
                    throw new ArgumentOutOfRangeException("source");
                seen[track] = true;
                var requested = request.RequestedLevels;
                if (!ConsumesSharedSlot(request.Track))
                {
                    grants.Add(new HundredLevelBudgetGrant
                    {
                        Track = request.Track,
                        GrantedLevels = requested,
                        BudgetSpent = 0
                    });
                    continue;
                }
                var quota = request.AuthorizedQuota;
                var available = TrueRemaining(budgetSpent);
                var granted = Math.Min(Math.Min(requested, quota), available);
                budgetSpent += granted;
                grants.Add(new HundredLevelBudgetGrant
                {
                    Track = request.Track,
                    GrantedLevels = granted,
                    BudgetSpent = granted
                });
            }
            return new HundredLevelBudgetDecision
            {
                SpentBefore = spent,
                SpentAfter = budgetSpent,
                Remaining = TrueRemaining(budgetSpent),
                Grants = grants.ToArray()
            };
        }
    }

    internal sealed class TrollCadenceProjection
    {
        internal int FactorSeconds;
        internal int CounterBefore;
        internal int EventCounter;
        internal int SecondsUntilEvent;
        internal int EventOrdinal;
        internal TrollEventKind Kind;
    }

    internal sealed class TrollRunState
    {
        internal int Counter;
        internal bool EquipmentDisabled;
        internal bool NguDisabled;
        internal bool BeardsDisabled;
        internal bool WandoosDisabled;
        internal bool MenuSwapped;
        internal bool BossDivided;
    }

    internal sealed class TrollResetDecision
    {
        internal bool Allowed;
        internal TrollCadenceProjection NextEvent;
        internal string Reason = string.Empty;
    }

    internal static class TrollChallengeMechanics
    {
        private static readonly int[] Factors = {120, 110, 100, 90, 85, 80, 75};

        internal static int Factor(int completedBefore)
        {
            return Factors[Math.Max(0, Math.Min(Factors.Length - 1, completedBefore))];
        }

        internal static TrollCadenceProjection NextEvent(int counter, int completedBefore)
        {
            if (counter < 0) throw new ArgumentOutOfRangeException("counter");
            var factor = Factor(completedBefore);
            var ordinal = counter / factor + 1;
            var eventCounterLong = (long)ordinal * factor;
            var eventCounter = eventCounterLong >= int.MaxValue ? int.MaxValue
                : (int)eventCounterLong;
            return new TrollCadenceProjection
            {
                FactorSeconds = factor,
                CounterBefore = counter,
                EventCounter = eventCounter,
                SecondsUntilEvent = Math.Max(0, eventCounter - counter),
                EventOrdinal = ordinal,
                Kind = ordinal % 5 == 0 ? TrollEventKind.Big : TrollEventKind.Small
            };
        }

        internal static TrollRunState ApplyOrdinaryRebirth(TrollRunState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            return new TrollRunState {Counter = state.Counter};
        }

        internal static TrollRunState ApplyEntryCompletionOrFailure(TrollRunState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            return new TrollRunState();
        }

        internal static bool BigTrollOutcomeReachable(int nativeSwitchValue)
        {
            // Random.Range(1,7) is upper-exclusive and the switch does not subtract one.
            return nativeSwitchValue >= 1 && nativeSwitchValue <= 6;
        }

        internal static TrollResetDecision EvaluatePlannedReset(int counter,
            int completedBefore, int secondsUntilReset, int recoverySeconds,
            bool terminalBeforeEvent)
        {
            var next = NextEvent(counter, completedBefore);
            var reset = Math.Max(0, secondsUntilReset);
            var recovery = Math.Max(0, recoverySeconds);
            var strandsBig = next.Kind == TrollEventKind.Big && !terminalBeforeEvent
                             && reset < next.SecondsUntilEvent
                             && next.SecondsUntilEvent <= reset + recovery;
            return new TrollResetDecision
            {
                Allowed = !strandsBig,
                NextEvent = next,
                Reason = strandsBig
                    ? "hold: Troll counter survives reset and the fifth-event big troll lands during recovery"
                    : terminalBeforeEvent
                        ? "reset route reaches the terminal before the next Troll event"
                        : "planned reset does not strand the next big Troll inside recovery"
            };
        }

        internal static bool PopupChoosesNo(int boxCounter, int switcherooBox)
        {
            if (switcherooBox < 1 || switcherooBox > 48)
                throw new ArgumentOutOfRangeException("switcherooBox");
            return boxCounter == switcherooBox;
        }

        internal static bool PopupComplete(int boxCounter)
        {
            return boxCounter >= 50;
        }
    }

    internal sealed class LaserPhaseInput
    {
        internal long AugmentLevel;
        internal long UpgradeLevel;
        internal double AugmentProgress;
        internal double UpgradeProgress;
        internal double AugmentFinishSeconds = -1.0;
        internal double UpgradeFinishSeconds = -1.0;
        internal double ResetAndRebuildSeconds = -1.0;
        internal bool DirectGoldLedgerFeasible = true;
    }

    internal sealed class LaserPhaseDecision
    {
        internal LaserChallengePhase Phase;
        internal bool ForbidRebirth = true;
        internal double DirectFinishSeconds = -1.0;
        internal double ResetAndRebuildSeconds = -1.0;
        internal string Reason = string.Empty;
    }

    internal static class LaserChallengeMechanics
    {
        internal static LaserPhaseDecision Evaluate(LaserPhaseInput input)
        {
            if (input == null) throw new ArgumentNullException("input");
            var directKnown = Finite(input.AugmentFinishSeconds)
                              && Finite(input.UpgradeFinishSeconds)
                              && input.DirectGoldLedgerFeasible;
            var resetKnown = Finite(input.ResetAndRebuildSeconds);
            var direct = directKnown
                ? Math.Max(input.AugmentFinishSeconds, input.UpgradeFinishSeconds) : -1.0;
            var material = input.AugmentLevel > 0L || input.UpgradeLevel > 0L
                           || input.AugmentProgress > 0.0 || input.UpgradeProgress > 0.0;
            if (directKnown && resetKnown)
            {
                var commit = direct <= input.ResetAndRebuildSeconds + 1e-12;
                return new LaserPhaseDecision
                {
                    Phase = commit ? LaserChallengePhase.Commit
                        : LaserChallengePhase.NumberBuilding,
                    ForbidRebirth = commit,
                    DirectFinishSeconds = direct,
                    ResetAndRebuildSeconds = input.ResetAndRebuildSeconds,
                    Reason = commit
                        ? "commit: direct paired-track finish is no slower than reset and rebuild"
                        : "build Number: reset-and-rebuild reaches both Laser tracks sooner"
                };
            }
            if (material)
                return new LaserPhaseDecision
                {
                    Phase = LaserChallengePhase.Commit,
                    ForbidRebirth = true,
                    DirectFinishSeconds = direct,
                    ResetAndRebuildSeconds = input.ResetAndRebuildSeconds,
                    Reason = "commit fail-closed: material pair progress exists but a complete reset route is unknown"
                };
            if (resetKnown)
                return new LaserPhaseDecision
                {
                    Phase = LaserChallengePhase.NumberBuilding,
                    ForbidRebirth = false,
                    DirectFinishSeconds = direct,
                    ResetAndRebuildSeconds = input.ResetAndRebuildSeconds,
                    Reason = "build Number: no pair investment exists and a finite rebuild route is available"
                };
            return new LaserPhaseDecision
            {
                Phase = LaserChallengePhase.Unknown,
                Reason = "hold: neither complete direct nor reset-and-rebuild route is available"
            };
        }

        private static bool Finite(double value)
        {
            return value >= 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    internal sealed class TitanClockLossItem
    {
        internal int TitanId;
        internal int SpawnSeconds;
        internal int RemainingBeforeSeconds;
        internal int RemainingAfterSeconds;
        internal int LostMaturitySeconds;
        internal int FutureValuedKills;
        internal long CycleDelaySeconds;
        internal bool WasReady;
    }

    internal sealed class TitanVectorCost
    {
        internal TitanClockLossItem[] Items = new TitanClockLossItem[0];
        internal long TotalCycleDelaySeconds;
        internal bool AnyReady;
    }

    internal sealed class ChallengeBatchStep
    {
        internal double ClearSeconds;
        internal double TitanClockResetCostSeconds;
        internal double DownstreamTimeSavedSeconds;
    }

    internal sealed class ChallengeBatchEstimate
    {
        internal double ClearSeconds;
        internal double TitanCostSeconds;
        internal double DownstreamSavedSeconds;
        internal double FinalRecoverySeconds;
        internal double TotalSeconds;
        internal int RecoveryCharges;
    }

    internal static class ChallengeMechanics
    {
        internal const double TwentyFourHourDeadlineSeconds = 86400.0;

        internal static ChallengeEntryTransformKind EntryKind(ChallengeType type)
        {
            ValidateType(type);
            return type == ChallengeType.LaserSword
                ? ChallengeEntryTransformKind.LaserSoftReset
                : ChallengeEntryTransformKind.HardReset;
        }

        internal static bool ShouldCastBloodNumberBeforeEntry(ChallengeType type)
        {
            return EntryKind(type) == ChallengeEntryTransformKind.LaserSoftReset;
        }

        internal static ChallengeOfflineTransformKind OfflineKind(ChallengeType type)
        {
            ValidateType(type);
            if (type == ChallengeType.OneHundredLC || type == ChallengeType.Troll
                || type == ChallengeType.TwentyFourHour)
                return ChallengeOfflineTransformKind.Frozen;
            if (type == ChallengeType.Blind)
                return ChallengeOfflineTransformKind.ProgressWithoutChallengeTimer;
            return ChallengeOfflineTransformKind.ProgressAndChallengeTimer;
        }

        internal static ChallengeTimeWriteKind NativeTimeWriteKind(ChallengeType type)
        {
            ValidateType(type);
            return type == ChallengeType.Basic || type == ChallengeType.NoAug
                   || type == ChallengeType.OneHundredLC || type == ChallengeType.NoEquip
                ? ChallengeTimeWriteKind.GlobalMinimum : ChallengeTimeWriteKind.GlobalLatest;
        }

        internal static int ApplyNativeBestTimeWrite(ChallengeType type, int existing,
            double completedSeconds)
        {
            if (double.IsNaN(completedSeconds) || double.IsInfinity(completedSeconds)
                || completedSeconds < 0.0)
                throw new ArgumentOutOfRangeException("completedSeconds");
            var observed = completedSeconds >= int.MaxValue ? int.MaxValue
                : (int)Math.Floor(completedSeconds);
            return NativeTimeWriteKind(type) == ChallengeTimeWriteKind.GlobalMinimum
                ? Math.Min(existing, observed) : observed;
        }

        internal static int DefaultMaximum(ChallengeType type)
        {
            ValidateType(type);
            if (type == ChallengeType.Basic || type == ChallengeType.NoAug
                || type == ChallengeType.OneHundredLC || type == ChallengeType.NoEquip)
                return 5;
            if (type == ChallengeType.Troll) return 7;
            if (type == ChallengeType.LaserSword) return 20;
            return 10;
        }

        internal static int ExactTarget(ChallengeType type, int completedBefore)
        {
            ValidateType(type);
            var c = Math.Max(0, completedBefore);
            switch (type)
            {
                case ChallengeType.Basic: return 57;
                case ChallengeType.NoAug: return 58;
                case ChallengeType.TwentyFourHour:
                    return (int)Math.Min(299L, 57L + 26L * c);
                case ChallengeType.OneHundredLC: return 57;
                case ChallengeType.NoEquip: return 65;
                case ChallengeType.Troll: return SaturatingTarget(68L + 15L * c);
                case ChallengeType.NoRebirth: return SaturatingTarget(39L + 5L * c);
                case ChallengeType.LaserSword: return SaturatingTarget(2L + c);
                case ChallengeType.Blind:
                case ChallengeType.NoNGU: return SaturatingTarget(57L + 10L * c);
                case ChallengeType.NoTimeMachine: return SaturatingTarget(57L + 15L * c);
                default: throw new ArgumentOutOfRangeException("type");
            }
        }

        internal static bool CompletionSatisfied(ChallengeType type, int bossId,
            long laserAugmentLevel, long laserUpgradeLevel, int completedBefore)
        {
            var target = ExactTarget(type, completedBefore);
            return type == ChallengeType.LaserSword
                ? laserAugmentLevel >= target && laserUpgradeLevel >= target
                : bossId > target;
        }

        internal static ChallengeTransitionState ApplyEntry(ChallengeTransitionState source,
            ChallengeType type, RebirthBankInput bankInput)
        {
            if (source == null || source.Rebirth == null || source.TitanClocks == null)
                throw new ArgumentNullException("source");
            if (source.InChallenge) throw new InvalidOperationException(
                "challenge entry requires no active challenge");
            var result = source.Clone();
            var preview = RebirthTransitionKernel.Preview(result.Rebirth);
            result.Rebirth = RebirthTransitionKernel.ApplyOrdinaryRebirth(
                result.Rebirth, bankInput);
            result.TitanClocks = TitanMechanics.ApplyOrdinaryRebirth(result.TitanClocks);
            result.ResetLocalProgress = 0L;
            result.RebirthLevels = 0L;
            result.ChallengeSeconds = 0.0;
            ClearTrollPenalties(result);
            if (EntryKind(type) == ChallengeEntryTransformKind.HardReset)
            {
                result.Rebirth.CurrentAttackNumber = 1.0;
                result.Rebirth.CurrentDefenseNumber = 1.0;
                result.Rebirth.BossMulti = 1.0;
                result.Rebirth.TimeMulti = 1.0;
                result.Rebirth.OldBossMulti = 1.0;
                result.Rebirth.OldTimeMulti = 1.0;
                result.PublishedNextAttack = 1.0;
                result.PublishedNextDefense = 1.0;
            }
            else
            {
                result.PublishedNextAttack = preview.Attack;
                result.PublishedNextDefense = preview.Defense;
            }
            result.Type = type;
            result.InChallenge = true;
            result.ActiveFlags = new bool[11];
            result.ActiveFlags[(int)type] = true;
            if (type == ChallengeType.Troll) result.TrollCounter = 0;
            return result;
        }

        internal static ChallengeTransitionState ApplyCompletion(
            ChallengeTransitionState source)
        {
            if (source == null || source.Counts == null) throw new ArgumentNullException("source");
            if (!source.InChallenge) throw new InvalidOperationException(
                "challenge completion requires an active challenge");
            var result = source.Clone();
            if (result.Type == ChallengeType.Basic)
            {
                result.Counts.RawNormal = SaturatingIncrement(result.Counts.RawNormal);
                if (result.Difficulty == ChallengeDifficultyBand.Evil)
                    result.Counts.RawEvil = SaturatingIncrement(result.Counts.RawEvil);
                else if (result.Difficulty == ChallengeDifficultyBand.Sadistic)
                    result.Counts.RawSadistic = SaturatingIncrement(result.Counts.RawSadistic);
            }
            else if (result.Difficulty == ChallengeDifficultyBand.Normal)
                result.Counts.RawNormal = SaturatingIncrement(result.Counts.RawNormal);
            else if (result.Difficulty == ChallengeDifficultyBand.Evil)
                result.Counts.RawEvil = SaturatingIncrement(result.Counts.RawEvil);
            else
                result.Counts.RawSadistic = SaturatingIncrement(result.Counts.RawSadistic);
            result.InChallenge = false;
            result.ActiveFlags = new bool[11];
            result.ChallengeSeconds = 0.0;
            if (result.Type == ChallengeType.Troll)
            {
                result.TrollCounter = 0;
                ClearTrollPenalties(result);
            }
            return result;
        }

        internal static ChallengeTransitionState ApplyOffline(ChallengeTransitionState source,
            double seconds)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
                throw new ArgumentOutOfRangeException("seconds");
            var result = source.Clone();
            var mode = OfflineKind(result.Type);
            if (mode == ChallengeOfflineTransformKind.Frozen) return result;
            result.OrdinaryOfflineProgressSeconds += seconds;
            result.Rebirth.RunSeconds += seconds;
            result.Rebirth.TimeMulti = RebirthTransitionKernel.ExactTimeMultiplier(
                result.Rebirth.RunSeconds);
            if (mode == ChallengeOfflineTransformKind.ProgressAndChallengeTimer)
                result.ChallengeSeconds += seconds;
            return result;
        }

        internal static ChallengeDeadlineProjection EvaluateTwentyFourHourDeadline(
            double activeSeconds, double remainingUpperSeconds, double safetyMarginSeconds)
        {
            ValidateFiniteNonNegative(activeSeconds, "activeSeconds");
            ValidateFiniteNonNegative(remainingUpperSeconds, "remainingUpperSeconds");
            ValidateFiniteNonNegative(safetyMarginSeconds, "safetyMarginSeconds");
            var slack = TwentyFourHourDeadlineSeconds - activeSeconds - remainingUpperSeconds;
            return new ChallengeDeadlineProjection
            {
                ActiveSeconds = activeSeconds,
                RemainingUpperSeconds = remainingUpperSeconds,
                DeadlineSlackSeconds = slack,
                Missed = activeSeconds >= TwentyFourHourDeadlineSeconds || slack < 0.0,
                AtRisk = slack <= safetyMarginSeconds,
                Evidence = slack < 0.0 ? "MISSED: negative active-time deadline slack"
                    : slack <= safetyMarginSeconds ? "AT RISK: deadline reserve is below safety margin"
                    : "positive active-time deadline slack"
            };
        }

        internal static TwentyFourHourFrameResult EvaluateTwentyFourHourFrame(
            double activeSecondsAfterTick, int bossId, int targetBoss)
        {
            ValidateFiniteNonNegative(activeSecondsAfterTick, "activeSecondsAfterTick");
            var failed = activeSecondsAfterTick >= TwentyFourHourDeadlineSeconds;
            var completed = bossId > targetBoss;
            return new TwentyFourHourFrameResult
            {
                FailureDispatched = failed,
                CompletionDispatched = completed,
                NativeSameFrameRace = failed && completed
            };
        }

        internal static TitanVectorCost EvaluateTitanClockLoss(TitanClockSnapshot clocks,
            bool[] valuedTitans, int[] futureValuedKills,
            int normalNoRebirthCompletions, int evilNoRebirthCompletions,
            int sadisticNoRebirthCompletions)
        {
            if (clocks == null) throw new ArgumentNullException("clocks");
            if (valuedTitans == null || valuedTitans.Length != 14)
                throw new ArgumentException("fourteen valued-Titan flags are required",
                    "valuedTitans");
            if (futureValuedKills == null || futureValuedKills.Length != 14)
                throw new ArgumentException("fourteen future kill counts are required",
                    "futureValuedKills");
            if (futureValuedKills.Any(x => x < 0))
                throw new ArgumentOutOfRangeException("futureValuedKills");
            var items = new List<TitanClockLossItem>();
            long total = 0L;
            var anyReady = false;
            for (var titanId = 1; titanId <= 14; titanId++)
            {
                if (!valuedTitans[titanId - 1] || futureValuedKills[titanId - 1] <= 0)
                    continue;
                var due = TitanMechanics.SpawnSeconds(titanId,
                    normalNoRebirthCompletions, evilNoRebirthCompletions,
                    sadisticNoRebirthCompletions);
                var before = TitanMechanics.SecondsUntilReady(titanId,
                    clocks.ElapsedSeconds(titanId), normalNoRebirthCompletions,
                    evilNoRebirthCompletions, sadisticNoRebirthCompletions);
                var lost = Math.Max(0, due - before);
                var cycle = (long)lost * futureValuedKills[titanId - 1];
                total = total > long.MaxValue - cycle ? long.MaxValue : total + cycle;
                anyReady |= before == 0;
                items.Add(new TitanClockLossItem
                {
                    TitanId = titanId,
                    SpawnSeconds = due,
                    RemainingBeforeSeconds = before,
                    RemainingAfterSeconds = due,
                    LostMaturitySeconds = lost,
                    FutureValuedKills = futureValuedKills[titanId - 1],
                    CycleDelaySeconds = cycle,
                    WasReady = before == 0
                });
            }
            return new TitanVectorCost
            {
                Items = items.ToArray(),
                TotalCycleDelaySeconds = total,
                AnyReady = anyReady
            };
        }

        internal static ChallengeBatchEstimate EvaluateBatch(
            IEnumerable<ChallengeBatchStep> source, double finalRecoverySeconds)
        {
            ValidateFiniteNonNegative(finalRecoverySeconds, "finalRecoverySeconds");
            var steps = (source ?? Enumerable.Empty<ChallengeBatchStep>())
                .Where(x => x != null).ToArray();
            foreach (var step in steps)
            {
                ValidateFiniteNonNegative(step.ClearSeconds, "ClearSeconds");
                ValidateFiniteNonNegative(step.TitanClockResetCostSeconds,
                    "TitanClockResetCostSeconds");
                ValidateFiniteNonNegative(step.DownstreamTimeSavedSeconds,
                    "DownstreamTimeSavedSeconds");
            }
            var clear = steps.Sum(x => x.ClearSeconds);
            var titan = steps.Sum(x => x.TitanClockResetCostSeconds);
            var saved = steps.Sum(x => x.DownstreamTimeSavedSeconds);
            return new ChallengeBatchEstimate
            {
                ClearSeconds = clear,
                TitanCostSeconds = titan,
                DownstreamSavedSeconds = saved,
                FinalRecoverySeconds = finalRecoverySeconds,
                TotalSeconds = Math.Max(0.0, clear + titan + finalRecoverySeconds - saved),
                RecoveryCharges = steps.Length == 0 ? 0 : 1
            };
        }

        internal static string Code(ChallengeType type)
        {
            switch (type)
            {
                case ChallengeType.Basic: return "BASIC";
                case ChallengeType.NoAug: return "NOAUG";
                case ChallengeType.TwentyFourHour: return "24HR";
                case ChallengeType.OneHundredLC: return "100LC";
                case ChallengeType.NoEquip: return "NOEC";
                case ChallengeType.Troll: return "TC";
                case ChallengeType.NoRebirth: return "NORB";
                case ChallengeType.LaserSword: return "LSC";
                case ChallengeType.Blind: return "BLIND";
                case ChallengeType.NoNGU: return "NONGU";
                case ChallengeType.NoTimeMachine: return "NOTM";
                default: throw new ArgumentOutOfRangeException("type");
            }
        }

        private static void ClearTrollPenalties(ChallengeTransitionState state)
        {
            state.TrollEquipmentDisabled = false;
            state.TrollNguDisabled = false;
            state.TrollBeardsDisabled = false;
            state.TrollWandoosDisabled = false;
            state.TrollMenuSwapped = false;
            state.TrollBossDivided = false;
        }

        private static int SaturatingIncrement(int value)
        {
            return value == int.MaxValue ? int.MaxValue : value + 1;
        }

        private static int SaturatingTarget(long value)
        {
            return value >= int.MaxValue ? int.MaxValue : (int)value;
        }

        private static void ValidateType(ChallengeType type)
        {
            if ((int)type < 0 || (int)type > (int)ChallengeType.NoTimeMachine)
                throw new ArgumentOutOfRangeException("type");
        }

        private static void ValidateFiniteNonNegative(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                throw new ArgumentOutOfRangeException(name);
        }
    }
}
