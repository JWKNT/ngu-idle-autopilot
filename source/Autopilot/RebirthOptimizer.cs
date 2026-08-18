using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Managers;

/*
FILE PURPOSE

RebirthOptimizer searches one-second run ages and named mechanic events, scoring compounded
persistent growth, repeatable catch-up Boss EXP, AP, and cap compression. Ordinary candidates may
project an Attack or Defense Number below the currently banked multiplier because the native game
permits that reset and persistent rewards can repay it. The projected/current ratio is therefore a
cost in the counterfactual score, not an eligibility gate. Reset-local unfinished work, replay time,
and reachable boss chains must be modeled here rather than hidden behind fixed timers.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class RebirthRecommendation
    {
        internal int TargetSeconds;
        internal string Reason = string.Empty;
        internal int RunnerUpSeconds;
        internal int RunnerUpDeltaSeconds;
        internal string RunnerUpReason = string.Empty;
        internal double SelectedScorePerHour;
        internal double RunnerUpScorePerHour;
        internal double ProjectedMultiplier;
        internal int ProjectedAP;
        internal string CandidateSummary = string.Empty;
        internal int CandidateCount;
        internal bool RecoveryMode;
        internal int RecoveryEtaSeconds = -1;
        internal int RecoveryRemainingBosses;
        internal string RecoveryReason = string.Empty;
        internal double ExpectedCatchupExp;
        internal double ExpectedCatchupExpPerHour;
        internal double MinimumNumberRatio;
        internal bool ExecutionHold;
        internal int NextPositiveEtaSeconds = -1;
        internal int NextEvaluationEtaSeconds = 1;
        internal string EtaReason = string.Empty;
    }

    internal sealed class RebirthMutationDecision
    {
        internal bool Authorized;
        internal int PreferredRouteEtaSeconds = -1;
        internal string Reason = string.Empty;
    }

    internal static class RebirthOptimizer
    {
        private sealed class Candidate
        {
            internal int Time;
            internal string Kind = string.Empty;
            internal string Reason = string.Empty;
            internal double Score;
            internal double CapScore;
            internal double ProjectedMultiplier;
            internal double ProjectedGainRatio;
            internal int ProjectedAP;
            internal double RecoveryEta;
            internal int RemainingCatchupBosses;
            internal double ExpectedCatchupExp;
            internal double ExpectedCatchupExpPerHour;
        }

        private static readonly int[] TimeGates =
            {60, 120, 180, 240, 300, 420, 600, 720, 900, 1800, 3600};

        // Keep a nearly-tied choice stable so telemetry jitter does not reload the
        // allocation profile and move the checkpoint every planner pass.
        private static int _lastElapsed = -1;
        private static int _stickyTarget = -1;
        private static string _stickyKind = string.Empty;

        internal static RebirthRecommendation EarlyNormal(Character c)
        {
            var elapsed = Math.Max(0, (int)Math.Floor(c.rebirthTime.totalseconds));
            if (_lastElapsed >= 0 && elapsed + 5 < _lastElapsed)
            {
                _stickyTarget = -1;
                _stickyKind = string.Empty;
            }
            _lastElapsed = elapsed;

            // Keep candidates on the absolute run clock.  Advancing the lower bound
            // to elapsed+1 on every planner pass turns a selected checkpoint into a
            // moving target that can never be reached.
            var minimum = Math.Max(1, Math.Max((int)Math.Ceiling((double)c.rebirth.minRebirthTime()), elapsed));
            var grbWindowRequired = c.highestBoss >= 58 && !c.inventory.itemList.GRBComplete;
            if (grbWindowRequired) minimum = Math.Max(minimum, 3600);
            var horizon = Math.Max(7200, elapsed + 3600);

            var candidates = new List<Candidate>();
            AddCandidate(candidates, minimum, "reset-now",
                "rebirth at the first legal moment because another breakpoint does not repay its added run time");
            foreach (var gate in TimeGates)
            {
                if (gate < minimum) continue;
                AddCandidate(candidates, gate, "time-gate-" + gate,
                    gate == 3600 && grbWindowRequired
                        ? "hold through the 3,600-second Number jump and first GRB spawn window"
                        : "take the exact " + gate.ToString("N0") + "-second Number multiplier discontinuity");
            }

            // Time-based AP starts at 4,100 seconds, then repeats every 500 seconds. Long-running
            // saves must still see the next tick; the old fixed 7,200-second ceiling left them with
            // only an ever-moving reset candidate and could hold forever.
            var firstAp = minimum <= 4100 ? 4100 : 4100 + (int)Math.Ceiling((minimum - 4100) / 500.0) * 500;
            for (var apTime = firstAp; apTime <= horizon; apTime += 500)
            {
                AddCandidate(candidates, apTime, "ap-tick-" + apTime,
                    "bank the time-based AP tick at " + apTime.ToString("N0") + " seconds");
            }

            var trainingEvent = SecondsToNextTrainingEvent(c);
            if (trainingEvent >= 0)
            {
                var eventAt = Math.Max(minimum, elapsed + trainingEvent + 1);
                if (eventAt <= horizon)
                    AddCandidate(candidates, eventAt, "training-event",
                        "finish the next persistent Basic Training cap reduction or 10,000-level Number step");
            }

            // This projection includes discrete BT growth, pending Augment/Upgrade
            // completions, exact boss tick order, regeneration, and current gear.
            var bossEta = AutopilotManager.SelectedBossDefeatEta(c, Math.Max(0, horizon - elapsed));
            if (bossEta >= 0)
            {
                var bossAt = Math.Max(minimum, elapsed + bossEta + 2);
                if (bossAt <= horizon)
                    AddCandidate(candidates, bossAt, "boss-event",
                        "finish the projected Fight Boss kill and bank its EXP, unlocks, and boss multiplier");
            }

            AddCandidate(candidates, Math.Max(minimum, 3600), "one-hour-comparison",
                "compare the full one-hour Number multiplier against resetting now");
            AddCandidate(candidates, Math.Max(minimum, 4100), "first-ap-comparison",
                "compare the first time-based AP reward against resetting now");

            // Do not constrain the answer to the named mechanics breakpoints. Scan
            // every legal integer second in the modeled early-run horizon; named
            // event candidates retain their richer labels at the same timestamp.
            // This proves a round result such as 3,600 rather than assuming it.
            if (minimum <= 7200)
            {
                var occupied = new HashSet<int>(candidates.Select(x => x.Time));
                for (var second = minimum; second <= 7200; second++)
                {
                    if (!occupied.Add(second)) continue;
                    candidates.Add(new Candidate
                    {
                        Time = second,
                        Kind = "integer-second-scan",
                        Reason = "best one-second-resolution point between named progression events"
                    });
                }
            }

            foreach (var candidate in candidates)
                Score(c, candidate, elapsed, bossEta);

            var viable = candidates.Where(x => !double.IsNaN(x.Score)
                                                && !double.IsInfinity(x.Score)
                                                && x.ProjectedMultiplier > 0
                                                && x.ProjectedGainRatio > 0
                                                && !double.IsNaN(x.ProjectedGainRatio)
                                                && !double.IsInfinity(x.ProjectedGainRatio)).ToList();
            if (viable.Count == 0)
            {
                var holdUntil = minimum;
                _stickyTarget = -1;
                _stickyKind = string.Empty;
                return new RebirthRecommendation
                {
                    TargetSeconds = holdUntil,
                    Reason = "fail-closed hold: every counterfactual candidate has an invalid native projection",
                    RunnerUpSeconds = holdUntil,
                    RunnerUpReason = "wait one planner pass for native state to become numerically valid",
                    SelectedScorePerHour = 0,
                    RunnerUpScorePerHour = 0,
                    ProjectedMultiplier = c.nextAttackMulti,
                    ProjectedAP = holdUntil < 4100 ? 0 : 1 + (holdUntil - 4100) / 500,
                    CandidateSummary = "every modeled candidate had an invalid or non-positive native preview",
                    CandidateCount = candidates.Count,
                    MinimumNumberRatio = Math.Min(
                        c.attackMulti > 0 ? c.nextAttackMulti / c.attackMulti : 0.0,
                        c.defenseMulti > 0 ? c.nextDefenseMulti / c.defenseMulti : 0.0),
                    ExecutionHold = true,
                    NextEvaluationEtaSeconds = 1,
                    EtaReason = "native preview invalid; reevaluate from a fresh snapshot in 1s"
                };
            }

            var ordered = viable.OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.CapScore).ThenBy(x => x.Time).ToList();

            /*
            NO-RESET COUNTERFACTUAL

            Continuing the current run is a real branch with zero incremental reset utility.  It
            must participate in selection explicitly: choosing the least-bad negative reset still
            destroys Number and reset-local work.  When no modeled reset beats zero, publish a hold
            together with the first future positive-value probe (or an honest unknown) and replan on
            the next live snapshot.  TimeRebirth repeats this admission test at the mutation boundary.
            */
            if (!ResetBeatsHold(ordered[0].Score))
            {
                var bestRejected = ordered[0];
                var positiveEta = FindNextPositiveResetEta(c, elapsed, bossEta);
                _stickyTarget = -1;
                _stickyKind = string.Empty;
                return new RebirthRecommendation
                {
                    TargetSeconds = Math.Max(minimum, elapsed),
                    Reason = "hold: continuing this run (0.000000/h) beats every modeled reset",
                    RunnerUpSeconds = bestRejected.Time,
                    RunnerUpDeltaSeconds = Math.Abs(bestRejected.Time - elapsed),
                    RunnerUpReason = bestRejected.Reason,
                    SelectedScorePerHour = 0.0,
                    RunnerUpScorePerHour = bestRejected.Score,
                    ProjectedMultiplier = bestRejected.ProjectedMultiplier,
                    ProjectedAP = bestRejected.ProjectedAP,
                    CandidateSummary = "HOLD baseline=0.000000/h | " + string.Join(" | ",
                        ordered.Take(6).Select(x => x.Time + "s " + x.Kind + "="
                            + x.Score.ToString("0.000000") + "/h").ToArray()),
                    CandidateCount = candidates.Count + 1,
                    RecoveryMode = c.bossID < c.highestBoss,
                    RecoveryEtaSeconds = -1,
                    RecoveryRemainingBosses = Math.Max(0, c.highestBoss - c.bossID),
                    RecoveryReason = c.bossID < c.highestBoss
                        ? "reset recovery is not admitted while its total counterfactual value is non-positive"
                        : "continuation is the positive control branch",
                    ExpectedCatchupExp = bestRejected.ExpectedCatchupExp,
                    ExpectedCatchupExpPerHour = bestRejected.ExpectedCatchupExpPerHour,
                    MinimumNumberRatio = bestRejected.ProjectedGainRatio,
                    ExecutionHold = true,
                    NextPositiveEtaSeconds = positiveEta,
                    NextEvaluationEtaSeconds = 1,
                    EtaReason = positiveEta >= 0
                        ? "first conservative positive-value reset probe in " + positiveEta.ToString("N0") + "s"
                        : "positive-value reset ETA unknown outside the 48-hour modeled horizon; reevaluate in 1s"
                };
            }

            var selected = ordered[0];
            var sticky = viable.FirstOrDefault(x => x.Time == _stickyTarget);
            // Once an absolute checkpoint is due, execute the already-selected
            // transaction instead of chasing a newly-scored future second. A newly
            // discovered first-GRB requirement is the one safety invalidation.
            if (sticky != null && sticky.Time >= minimum
                && (elapsed >= sticky.Time && (!grbWindowRequired || sticky.Time >= 3600)
                    || sticky.Score >= selected.Score * 0.9995))
                selected = sticky;
            _stickyTarget = selected.Time;
            _stickyKind = selected.Kind;

            var runnerUp = ordered.FirstOrDefault(x => x != selected) ?? selected;
            var meaningful = ordered.Where(x => x.Kind != "integer-second-scan").Take(5).ToList();
            if (selected.Kind == "integer-second-scan") meaningful.Insert(0, selected);
            var summary = string.Join(" | ", meaningful.Take(6).Select(x =>
                x.Time + "s " + x.Kind + "=" + x.Score.ToString("0.0000") + "/h").ToArray());
            return new RebirthRecommendation
            {
                TargetSeconds = selected.Time,
                Reason = c.bossID < c.highestBoss
                    ? selected.Reason + "; aggregate persistent value remains positive while replaying toward Boss "
                      + (c.highestBoss + 1) + " even though native Number is replaced on reset"
                    : selected.Reason,
                RunnerUpSeconds = runnerUp.Time,
                RunnerUpDeltaSeconds = Math.Abs(runnerUp.Time - selected.Time),
                RunnerUpReason = c.bossID < c.highestBoss
                    ? runnerUp.Reason + "; alternate below-record persistent-value route"
                    : runnerUp.Reason,
                SelectedScorePerHour = selected.Score,
                RunnerUpScorePerHour = runnerUp.Score,
                ProjectedMultiplier = selected.ProjectedMultiplier,
                ProjectedAP = selected.ProjectedAP,
                CandidateSummary = summary,
                CandidateCount = candidates.Count,
                RecoveryMode = c.bossID < c.highestBoss,
                RecoveryEtaSeconds = -1,
                RecoveryRemainingBosses = selected.RemainingCatchupBosses,
                RecoveryReason = c.bossID < c.highestBoss
                    ? "native rebirth replaces Number, so record replay has no valid geometric ETA; aggregate one-run persistent value controls"
                    : "boss record is already caught up",
                ExpectedCatchupExp = selected.ExpectedCatchupExp,
                ExpectedCatchupExpPerHour = selected.ExpectedCatchupExpPerHour,
                MinimumNumberRatio = selected.ProjectedGainRatio,
                NextPositiveEtaSeconds = Math.Max(0, selected.Time - elapsed),
                NextEvaluationEtaSeconds = 1,
                EtaReason = selected.Time <= elapsed
                    ? "positive-value reset is eligible now, subject to final mutation preflight"
                    : "selected positive-value checkpoint in "
                      + Math.Max(0, selected.Time - elapsed).ToString("N0") + "s"
            };
        }

        internal static bool ResetBeatsHold(double selectedScorePerHour)
        {
            return !double.IsNaN(selectedScorePerHour)
                   && !double.IsInfinity(selectedScorePerHour)
                   && selectedScorePerHour > 1e-12;
        }

        /*
        FINAL MUTATION ADMISSION

        This pure policy kernel is shared by the optimizer tests and TimeRebirth's irreversible
        boundary.  A positive aggregate reset value may legitimately include a lower Number when
        persistent AP/EXP/cap gains repay it.  During boss-record recovery, however, an executable
        finite reset ETA must beat the finite continue ETA; unknown is a hold, never permission.
        Challenge entry deliberately does not call this ordinary-rebirth kernel.
        */
        internal static RebirthMutationDecision EvaluateMutationPolicy(double selectedScorePerHour,
            bool previewValid, double minimumNumberRatio, bool recoveryMode, int resetRouteEtaSeconds,
            int continueRouteEtaSeconds)
        {
            if (!previewValid || double.IsNaN(minimumNumberRatio)
                || double.IsInfinity(minimumNumberRatio) || minimumNumberRatio <= 0.0)
                return new RebirthMutationDecision
                {
                    Reason = "hold: final native Number preview is invalid or not yet Blood-adjusted"
                };
            if (!ResetBeatsHold(selectedScorePerHour))
                return new RebirthMutationDecision
                {
                    Reason = "hold: no-reset baseline (0/h) dominates the selected reset"
                };
            if (!recoveryMode)
                return new RebirthMutationDecision
                {
                    Authorized = true,
                    PreferredRouteEtaSeconds = 0,
                    Reason = minimumNumberRatio < 1.0
                        ? "lower Number is repaid by positive modeled persistent value; boss-record recovery is not active"
                        : "reset has positive persistent value; boss-record recovery is not active"
                };
            if (resetRouteEtaSeconds < 0)
                return new RebirthMutationDecision
                {
                    Reason = "hold: reset-route recovery ETA is unknown"
                };
            if (continueRouteEtaSeconds >= 0 && continueRouteEtaSeconds < resetRouteEtaSeconds)
                return new RebirthMutationDecision
                {
                    PreferredRouteEtaSeconds = continueRouteEtaSeconds,
                    Reason = "hold: continuing reaches the boss record sooner than resetting"
                };
            return new RebirthMutationDecision
            {
                Authorized = true,
                PreferredRouteEtaSeconds = resetRouteEtaSeconds,
                Reason = continueRouteEtaSeconds < 0
                    ? "reset has the only finite boss-record recovery ETA"
                    : "reset has the shorter finite boss-record recovery ETA"
            };
        }

        private static int FindNextPositiveResetEta(Character c, int elapsed, int bossEta)
        {
            var horizon = elapsed > int.MaxValue - 172800 ? int.MaxValue : elapsed + 172800;
            var previous = elapsed;
            for (var target = elapsed + 60; target > elapsed && target <= horizon; target += 60)
            {
                var probe = new Candidate {Time = target, Kind = "positive-value-eta-probe"};
                Score(c, probe, elapsed, bossEta);
                if (!ResetBeatsHold(probe.Score))
                {
                    previous = target;
                    continue;
                }
                for (var exact = Math.Max(elapsed, previous + 1); exact <= target; exact++)
                {
                    var exactProbe = new Candidate {Time = exact, Kind = "positive-value-eta-probe"};
                    Score(c, exactProbe, elapsed, bossEta);
                    if (ResetBeatsHold(exactProbe.Score)) return Math.Max(0, exact - elapsed);
                }
                return Math.Max(0, target - elapsed);
            }
            return -1;
        }

        private static void AddCandidate(ICollection<Candidate> candidates, int time, string kind, string reason)
        {
            if (time < 1 || candidates.Any(x => x.Time == time)) return;
            candidates.Add(new Candidate {Time = time, Kind = kind, Reason = reason});
        }

        private static void Score(Character c, Candidate candidate, int elapsed, int bossEta)
        {
            var duration = Math.Max(1, candidate.Time);
            var remaining = Math.Max(0, candidate.Time - elapsed);
            var currentNumberStep = Math.Max(1.0, Math.Floor(c.training.totalAttackLevels / 10000.0) + 1.0);
            var projectedAttackLevels = c.training.totalAttackLevels;
            for (var i = 0; i < 6; i++)
                projectedAttackLevels += (long)Math.Floor(TrainingRate(c,
                    c.training.attackEnergy[i], c.training.attackCaps[i]) * remaining);
            var projectedNumberStep = Math.Max(1.0, Math.Floor(projectedAttackLevels / 10000.0) + 1.0);

            var currentTimeMulti = ExactTimeMultiplier(Math.Max(1, elapsed));
            var currentBossMulti = Math.Max(1e-300, (double)c.bossMulti);
            var staticFactor = (c.nextAttackMulti - 1.0)
                               / Math.Max(1e-300, currentBossMulti * currentNumberStep * currentTimeMulti);
            if (double.IsNaN(staticFactor) || double.IsInfinity(staticFactor) || staticFactor <= 0)
                staticFactor = 1.0;
            var staticDefenseFactor = (c.nextDefenseMulti - 1.0)
                                      / Math.Max(1e-300, currentBossMulti * currentNumberStep * currentTimeMulti);
            if (double.IsNaN(staticDefenseFactor) || double.IsInfinity(staticDefenseFactor)
                || staticDefenseFactor <= 0)
                staticDefenseFactor = 1.0;

            var includesBoss = bossEta >= 0 && remaining >= bossEta + 1;
            var projectedBossMulti = currentBossMulti * (includesBoss ? 2.0 : 1.0);
            var projected = 1.0 + staticFactor * projectedBossMulti * projectedNumberStep
                            * ExactTimeMultiplier(candidate.Time);
            var projectedDefense = 1.0 + staticDefenseFactor * projectedBossMulti * projectedNumberStep
                                   * ExactTimeMultiplier(candidate.Time);
            var currentMultiplier = Math.Max(1e-300, (double)c.attackMulti);
            candidate.ProjectedMultiplier = projected;
            candidate.ProjectedGainRatio = Math.Min(projected / currentMultiplier,
                projectedDefense / Math.Max(1e-300, (double)c.defenseMulti));
            // While a damaged run is below its persistent record, the same
            // logarithmic-growth objective has a more useful interpretation: boss
            // requirements are multiplicative, so required log-stat distance divided
            // by log(Number gain) is the expected count of repeated cycles needed to
            // recover. Include the exact current-boss event (a native 2x bossMulti)
            // and the boss-array ratios instead of imposing "reach record first".
            var recoveryMode = c.bossID < c.highestBoss;
            var recoveryStart = c.bossID + (includesBoss ? 1 : 0);
            candidate.RemainingCatchupBosses = Math.Max(0, c.highestBoss - recoveryStart);
            var requiredBossLog = RequiredBossLogDistance(c, recoveryStart, c.highestBoss);
            candidate.RecoveryEta = candidate.ProjectedGainRatio <= 1.000001
                ? double.PositiveInfinity
                : remaining + duration * requiredBossLog / Math.Log(candidate.ProjectedGainRatio);
            candidate.ProjectedAP = candidate.Time < 4100 ? 0 : 1 + (candidate.Time - 4100) / 500;
            var capCompression = ProjectedCapCompression(c, remaining);
            candidate.CapScore = capCompression / duration;
            var replayableBoss = Math.Max(0, c.bossID - 1 + (includesBoss ? 1 : 0));
            candidate.ExpectedCatchupExp = ExpectedRecurringBossExp(c, replayableBoss);
            candidate.ExpectedCatchupExpPerHour = 3600.0 * candidate.ExpectedCatchupExp / duration;

            /*
            PERSISTENT-PROGRESSION OBJECTIVE

            Absolute Number already owned by the save is not a reward from this candidate. Score only the
            incremental projected/current multiplier ratio, plus AP and newly reached persistent cap progress;
            otherwise the shortest candidate wins merely by amortizing the inherited baseline. During record
            recovery, retain the exact repeated-cycle ETA when Number is improving.
            */
            // Catch-up Boss EXP is repeatable persistent income. Normalize it against
            // lifetime EXP so a replay is valuable early without dominating mature
            // multipliers. A Number loss remains visible through log(gain ratio): the
            // optimizer can accept it, but must pay the modeled replay/stat cost.
            var expScale = Math.Max(20.0, c.stats == null ? 20.0 : c.stats.totalExp);
            var catchupUtility = Math.Log(1.0 + candidate.ExpectedCatchupExp / expScale);
            var cycleUtility = Math.Log(Math.Max(1e-300, candidate.ProjectedGainRatio))
                               + candidate.ProjectedAP * 0.05
                               + capCompression * 8.0
                               + catchupUtility;
            var persistentRate = 3600.0 * cycleUtility / duration;
            candidate.Score = persistentRate;
        }

        /*
        REPEATABLE BOSS EXP

        BossController.rewardExp grants recurring EXP for Bosses 6-22 and the native scaled
        reward from Boss 23 onward. The first-Boss and currentHighestBoss branches are one-time
        discoveries and therefore are not counted as rebirth income. checkExpAdded is the game's
        read-only multiplier path; sampling it at a stable integer amount incorporates the save's
        current NGU/item/perk/digger/hack/wish/cooking EXP bonuses without mutating EXP.
        */
        internal static double ExpectedRecurringBossExp(Character c, int highestReplayableBoss)
        {
            if (c == null || highestReplayableBoss < 6) return 0.0;
            var baseExp = 0.0;
            for (var boss = 6; boss <= highestReplayableBoss; boss++)
            {
                if (boss < 23)
                {
                    baseExp += 1.0;
                    continue;
                }
                var completions = c.allChallenges == null || c.allChallenges.hour24Challenge == null
                    ? 0 : c.allChallenges.hour24Challenge.completions();
                var firstCompletionBonus = completions >= 1 ? 1.0 : 0.0;
                var reward = Math.Max(1.0, (boss - 13.0) / 10.0) + firstCompletionBonus;
                reward *= 1.0 + completions * 0.02;
                if (c.adventureController != null && c.adventureController.itopod != null)
                    reward *= c.adventureController.itopod.totalBossExp();
                baseExp += Math.Max(0.0, reward);
            }

            try
            {
                const long sample = 100000L;
                var multiplied = c.checkExpAdded(sample);
                if (multiplied > 0)
                    baseExp *= (double)multiplied / sample;
            }
            catch
            {
                // Base native Boss reward remains a conservative lower bound.
            }
            return baseExp;
        }

        // Compare the two routes at the actual mutation boundary. Route A resets
        // with the native Number preview and repeats the selected cycle. Route B
        // waits for the exact projected selected-boss defeat, banks its 2x Normal
        // bossMulti, then repeats the longer cycle with one fewer catch-up boss.
        // Both are expressed as remaining wall-clock seconds to remove the exact
        // multiplicative boss-stat distance. This is a recovery calculation, not a
        // catch-up safeguard; if waiting is exponentially bad, reset wins directly.
        internal static bool RecoveryResetEfficient(Character c, int selectedBossEta,
            out int resetRouteEta, out int continueRouteEta, out string reason)
        {
            resetRouteEta = -1;
            continueRouteEta = -1;
            reason = string.Empty;
            if (c == null || c.bossID >= c.highestBoss)
            {
                reason = "boss record already caught up; normal checkpoint objective applies";
                return true;
            }

            var elapsed = Math.Max(1, (int)Math.Floor(c.rebirthTime.totalseconds));
            var attackGain = c.attackMulti > 0 ? c.nextAttackMulti / c.attackMulti : 0.0;
            var defenseGain = c.defenseMulti > 0 ? c.nextDefenseMulti / c.defenseMulti : 0.0;
            var gain = Math.Min(attackGain, defenseGain);
            if (gain <= 1.000001 || double.IsNaN(gain) || double.IsInfinity(gain))
            {
                reason = "native Number preview is not a strict Attack/Defense improvement";
                return false;
            }

            var resetDistance = RequiredBossLogDistance(c, c.bossID, c.highestBoss);
            var resetEta = elapsed * resetDistance / Math.Log(gain);
            resetRouteEta = FiniteSeconds(resetEta);

            if (selectedBossEta < 0)
            {
                reason = "reset wins: selected boss has no finite current-run defeat ETA, while repeated higher-Number cycles remove the remaining log-stat distance";
                return true;
            }

            var wait = Math.Max(1, selectedBossEta + 2);
            var continueDistance = RequiredBossLogDistance(c, c.bossID + 1, c.highestBoss);
            var continueGain = gain * 2.0; // exact Normal advanceBoss bossMulti reward
            var continueEta = wait + (elapsed + wait) * continueDistance / Math.Log(continueGain);
            continueRouteEta = FiniteSeconds(continueEta);
            if (continueEta + 0.5 < resetEta)
            {
                reason = "continue wins: defeating selected Boss " + (c.bossID + 1)
                         + " first shortens modeled record recovery by "
                         + FiniteSeconds(resetEta - continueEta).ToString("N0") + "s";
                return false;
            }

            reason = "reset wins: higher Number plus replay reaches the record about "
                     + FiniteSeconds(continueEta - resetEta).ToString("N0")
                     + "s sooner than extending this run for the selected boss";
            return true;
        }

        private static int FiniteSeconds(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value >= int.MaxValue)
                return -1;
            return (int)Math.Ceiling(value);
        }

        private static double RequiredBossLogDistance(Character c, int startBossIndex, int recordIndex)
        {
            if (c == null || startBossIndex >= recordIndex) return 0.0;
            var total = 0.0;
            try
            {
                var boss = c.bossController == null ? null : c.bossController.boss;
                var attack = boss == null ? null : boss.bossAttack;
                var defense = boss == null ? null : boss.bossDefense;
                var hp = boss == null ? null : boss.bossMaxHP;
                for (var i = Math.Max(1, startBossIndex); i < recordIndex; i++)
                {
                    var ratio = Math.Max(StatRatio(attack, i),
                        Math.Max(StatRatio(defense, i), StatRatio(hp, i)));
                    total += Math.Log(Math.Max(1.000001, ratio));
                }
            }
            catch
            {
                var remaining = Math.Max(0, recordIndex - startBossIndex);
                total = remaining * Math.Log(startBossIndex >= 20 ? 10.0 : 5.0);
            }
            return total;
        }

        private static double StatRatio(double[] values, int index)
        {
            if (values == null || index <= 0 || index >= values.Length || values[index - 1] <= 0)
                return index >= 20 ? 10.0 : 5.0;
            return values[index] / values[index - 1];
        }

        private static double ProjectedCapCompression(Character c, int seconds)
        {
            var value = 0.0;
            for (var i = 0; i < 6; i++)
            {
                var attackLevel = c.training.attackTraining[i]
                                  + (long)Math.Floor(TrainingRate(c, c.training.attackEnergy[i], c.training.attackCaps[i]) * seconds);
                var defenseLevel = c.training.defenseTraining[i]
                                   + (long)Math.Floor(TrainingRate(c, c.training.defenseEnergy[i], c.training.defenseCaps[i]) * seconds);
                value += Compression(c.training.attackCaps[i], attackLevel, i);
                value += Compression(c.training.defenseCaps[i], defenseLevel, i);
            }
            return value;
        }

        private static double Compression(long cap, long level, int tier)
        {
            if (cap <= 1) return 0;
            var nextCap = Math.Max(1L, cap - CapReduction(level, cap, tier));
            return Math.Log((double)cap / nextCap);
        }

        private static double ExactTimeMultiplier(int seconds)
        {
            var t = Math.Max(0.0, seconds);
            if (t < 60) return t / 34359738368.0 / 3600.0;
            if (t < 120) return t / 33554432.0 / 3600.0;
            if (t < 180) return t / 518144.0 / 3600.0;
            if (t < 240) return t / 16192.0 / 3600.0;
            if (t < 300) return t / 2048.0 / 3600.0;
            if (t < 420) return t / 512.0 / 3600.0;
            if (t < 600) return t / 128.0 / 3600.0;
            if (t < 720) return t / 32.0 / 3600.0;
            if (t < 900) return t / 8.0 / 3600.0;
            if (t < 1800) return t / 4.0 / 3600.0;
            if (t < 3600) return t / 2.0 / 3600.0;
            return 1.0 + t / 172800.0;
        }

        internal static int SecondsToNextTrainingEvent(Character c)
        {
            var best = double.MaxValue;
            var totalRate = 0.0;
            for (var i = 0; i < 6; i++)
            {
                var attackRate = TrainingRate(c, c.training.attackEnergy[i], c.training.attackCaps[i]);
                var defenseRate = TrainingRate(c, c.training.defenseEnergy[i], c.training.defenseCaps[i]);
                totalRate += attackRate;
                ConsiderEvent(ref best, c.training.attackTraining[i],
                    MaxCapReductionLevel(c.training.attackCaps[i], i), attackRate);
                ConsiderEvent(ref best, c.training.defenseTraining[i],
                    MaxCapReductionLevel(c.training.defenseCaps[i], i), defenseRate);
            }
            if (totalRate > 0)
            {
                var nextNumberStep = (c.training.totalAttackLevels / 10000L + 1L) * 10000L;
                ConsiderEvent(ref best, c.training.totalAttackLevels, nextNumberStep, totalRate);
            }
            return best == double.MaxValue ? -1 : (int)Math.Ceiling(best);
        }

        private static double TrainingRate(Character c, long energy, long cap)
        {
            if (energy <= 0 || cap <= 0) return 0;
            var ticks = energy >= cap ? 1L : (long)Math.Ceiling((double)cap / energy);
            var levels = 1;
            if (c.adventure.itopod.perkLevel.Count > 15 && c.adventure.itopod.perkLevel[15] >= 1) levels++;
            if (c.beastQuest.quirkLevel.Count > 17 && c.beastQuest.quirkLevel[17] >= 1) levels++;
            if (c.wishes.wishes.Count > 23 && c.wishes.wishes[23].level >= 1) levels++;
            return 50.0 / ticks * levels;
        }

        private static void ConsiderEvent(ref double best, long current, long target, double perSecond)
        {
            if (current >= target || perSecond <= 0) return;
            best = Math.Min(best, (target - current) / perSecond);
        }

        internal static long MaxCapReductionLevel(long cap, int tier)
        {
            return cap <= 1 ? 0
                : MechanicsProgression.BasicTrainingLevelForMaximumReduction(cap, tier);
        }

        internal static long CapReduction(long level, long cap, int tier)
        {
            return MechanicsProgression.BasicTrainingCap(level, cap, tier).Reduction;
        }
    }
}
