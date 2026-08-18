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
and sends only admission-grade routes to ChallengeIntentSelector. ActivePolicy reports the exact
offline/deadline/budget/cadence/paired-track contract and accepts future exact route inputs through
an overload while the current integration remains fail-closed.

Inputs and outputs: Inputs are Character/controller snapshots, Main's installed assembly hash,
ExecutionSafety's state version, bot-owned timing samples, an optional Laser route comparison, and
an optional exact rebirth event. Outputs are zero or one ChallengeAdmission plus telemetry, or an
ActiveChallengePolicy. This file never enters, quits, completes, or rebirths a challenge.

Invariants and safety: Native bestTime is never timing evidence. Live serialized maxima are
authoritative and native targets must equal the exact installed formula. Ready valuable Titans
preempt entry; every other valued clock contributes its exact reset-loss vector. A 24-Hour route
requires positive active-time slack. No-Rebirth is continuous, no probability label is emitted
without calibrated coverage, and missing route evidence freezes destructive resets.

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

    internal static class ChallengeStrategyPlanner
    {
        private const double DeadlineSafetyMarginSeconds = 1.0;
        private static readonly object TimingGate = new object();
        private static readonly ChallengeTimingLedger TimingLedger =
            new ChallengeTimingLedger();

        private sealed class LiveCandidate
        {
            internal ChallengeType Type;
            internal int Complete;
            internal int Maximum;
            internal int NativeTarget;
            internal bool LevelTarget;
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

        internal static IList<ChallengeAdmission> Recommend(Character c,
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
            if (titanCost.AnyReady)
            {
                evidenceSummary = "Challenge HOLD: consume the ready valuable Titan vector before an entry reset; "
                                  + titanEvidence;
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
                if (!TryTimingEstimate(key, out timing) || !timing.AdmissionGrade
                    || !Finite(timing.UpperClearSeconds) || !Finite(timing.RecoverySeconds))
                {
                    rejected.Add(ChallengeMechanics.Code(live.Type)
                                 + " lacks comparable admission-grade timing");
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
                    ExpectedStateVersion = ExpectedStateVersion(c, live.Type,
                        difficulty, live.Complete, exactTarget),
                    TimingKey = key,
                    TotalRouteSeconds = timing.UpperClearSeconds + timing.RecoverySeconds
                                        + titanCost.TotalCycleDelaySeconds,
                    Evidence = timing.EvidenceLabel
                };
                var evidence = timing.EvidenceLabel + " key=" + key + ", n="
                               + timing.SampleCount;
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
                    RecoverySeconds = timing.RecoverySeconds,
                    TitanOpportunitySeconds = titanCost.TotalCycleDelaySeconds,
                    Constraints = live.Constraints, Reward = live.Reward,
                    Evidence = evidence, Score = -intent.TotalRouteSeconds,
                    Intent = intent, Timing = timing, TitanCost = titanCost,
                    Deadline = deadline
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

        internal static ActiveChallengePolicy ActivePolicy(Character c)
        {
            return ActivePolicy(c, null, -1);
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
                p.Objective = "reach Boss " + (exactTarget + 1)
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
            return p;
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
                    "hard entry", NativeReward(a.basicChallenge.expectedEXP(),
                        a.basicChallenge.expectedAPReward(), a.basicChallenge.specialRewards())),
                C(ChallengeType.NoAug, a.noAugsChallenge.currentCompletions(),
                    a.noAugsChallenge.maxCompletions, a.noAugsChallenge.targetBoss(), false,
                    "hard entry; Augments and Upgrades disabled",
                    NativeReward(a.noAugsChallenge.expectedEXP(),
                        a.noAugsChallenge.expectedAPReward(), a.noAugsChallenge.specialRewards())),
                C(ChallengeType.TwentyFourHour, a.hour24Challenge.currentCompletions(),
                    a.hour24Challenge.maxCompletions, a.hour24Challenge.targetBoss(), false,
                    "hard entry; active-time deadline; offline frozen",
                    NativeReward(a.hour24Challenge.expectedEXP(),
                        a.hour24Challenge.expectedAPReward(), a.hour24Challenge.specialRewards())),
                C(ChallengeType.OneHundredLC, a.level100Challenge.currentCompletions(),
                    a.level100Challenge.maxCompletions, a.level100Challenge.targetBoss(), false,
                    "hard entry; one shared 100-completed-level budget per rebirth",
                    NativeReward(a.level100Challenge.expectedEXP(),
                        a.level100Challenge.expectedAPReward(), a.level100Challenge.specialRewards())),
                C(ChallengeType.NoEquip, a.noEquipmentChallenge.currentCompletions(),
                    a.noEquipmentChallenge.maxCompletions, a.noEquipmentChallenge.targetBoss(), false,
                    "hard entry; equipment effects disabled",
                    NativeReward(a.noEquipmentChallenge.expectedEXP(),
                        a.noEquipmentChallenge.expectedAPReward(), a.noEquipmentChallenge.specialRewards())),
                C(ChallengeType.Troll, a.trollChallenge.currentCompletions(),
                    a.trollChallenge.maxCompletions, a.trollChallenge.targetBoss(), false,
                    "hard entry; exact persistent-counter Troll cadence; offline frozen",
                    NativeReward(a.trollChallenge.expectedEXP(),
                        a.trollChallenge.expectedAPReward(), a.trollChallenge.specialRewards())),
                C(ChallengeType.NoRebirth, a.noRebirthChallenge.currentCompletions(),
                    a.noRebirthChallenge.maxCompletions, a.noRebirthChallenge.targetBoss(), false,
                    "hard entry; one continuous no-reset path",
                    NativeReward(a.noRebirthChallenge.expectedEXP(),
                        a.noRebirthChallenge.expectedAPReward(), a.noRebirthChallenge.specialRewards())),
                C(ChallengeType.LaserSword, a.laserSwordChallenge.currentCompletions(),
                    a.laserSwordChallenge.maxCompletions,
                    a.laserSwordChallenge.laserSwordTarget(), true,
                    "soft entry; both pair tracks; build then commit",
                    NativeReward(a.laserSwordChallenge.expectedEXP(),
                        a.laserSwordChallenge.expectedAPReward(), a.laserSwordChallenge.specialRewards())),
                C(ChallengeType.Blind, a.blindChallenge.currentCompletions(),
                    a.blindChallenge.maxCompletions, a.blindChallenge.targetBoss(), false,
                    "hard entry; offline progress without challenge-timer advance",
                    NativeReward(a.blindChallenge.expectedEXP(),
                        a.blindChallenge.expectedAPReward(), a.blindChallenge.specialRewards())),
                C(ChallengeType.NoNGU, a.NGUChallenge.currentCompletions(),
                    a.NGUChallenge.maxCompletions, a.NGUChallenge.targetBoss(), false,
                    "hard entry; NGU effects and progress disabled",
                    NativeReward(a.NGUChallenge.expectedEXP(),
                        a.NGUChallenge.expectedAPReward(), a.NGUChallenge.specialRewards())),
                C(ChallengeType.NoTimeMachine, a.timeMachineChallenge.currentCompletions(),
                    a.timeMachineChallenge.maxCompletions, a.timeMachineChallenge.targetBoss(), false,
                    "hard entry; Time Machine unavailable",
                    NativeReward(a.timeMachineChallenge.expectedEXP(),
                        a.timeMachineChallenge.expectedAPReward(), a.timeMachineChallenge.specialRewards()))
            };
        }

        private static LiveCandidate C(ChallengeType type, int complete, int maximum,
            int target, bool level, string constraints, string reward)
        {
            return new LiveCandidate
            {
                Type = type, Complete = complete, Maximum = maximum,
                NativeTarget = target, LevelTarget = level,
                Constraints = constraints, Reward = reward
            };
        }

        private static bool TryCaptureTitanVector(Character c, out TitanVectorCost cost,
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

        private static string ExpectedStateVersion(Character c, ChallengeType type,
            ChallengeDifficultyBand difficulty, int completedBefore, int target)
        {
            return (Main.GameAssemblySha256 ?? string.Empty) + "|s="
                   + ExecutionSafety.StateVersion + "|d=" + difficulty + "|t=" + type
                   + "|c=" + completedBefore + "|target=" + target + "|boss=" + c.bossID
                   + "|run=" + Math.Floor(c.rebirthTime.totalseconds);
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

        private static int CurrentCompletions(Character c, ChallengeType type)
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
            return targetSeconds + 1e-12 >= minimum
                   && targetSeconds + 1e-12 >= c.rebirthTime.totalseconds;
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
    }
}
