using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Managers;

/*
FILE PURPOSE

RebirthOptimizer searches one-second run ages and named mechanic events, scoring compounded
persistent growth, bosses, AP, and cap compression. It returns winner/runner-up evidence while
TimeRebirth mutates the game. Reset-local unfinished work and reachable boss chains must be
modeled here rather than hidden behind fixed 30/60-minute timers.
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

            var minimum = Math.Max((int)Math.Ceiling((double)c.rebirth.minRebirthTime()), elapsed + 1);
            var grbWindowRequired = c.highestBoss >= 58 && !c.inventory.itemList.GRBComplete;
            if (grbWindowRequired) minimum = Math.Max(minimum, 3600);

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

            // Time-based AP starts at 4,100 seconds, then repeats every 500 seconds.
            for (var apTime = 4100; apTime <= 7200; apTime += 500)
            {
                if (apTime < minimum) continue;
                AddCandidate(candidates, apTime, "ap-tick-" + apTime,
                    "bank the time-based AP tick at " + apTime.ToString("N0") + " seconds");
            }

            var trainingEvent = SecondsToNextTrainingEvent(c);
            if (trainingEvent >= 0)
            {
                var eventAt = Math.Max(minimum, elapsed + trainingEvent + 1);
                if (eventAt <= 7200)
                    AddCandidate(candidates, eventAt, "training-event",
                        "finish the next persistent Basic Training cap reduction or 10,000-level Number step");
            }

            // This projection includes discrete BT growth, pending Augment/Upgrade
            // completions, exact boss tick order, regeneration, and current gear.
            var bossEta = AutopilotManager.SelectedBossDefeatEta(c, Math.Max(0, 7200 - elapsed));
            if (bossEta >= 0)
            {
                var bossAt = Math.Max(minimum, elapsed + bossEta + 2);
                if (bossAt <= 7200)
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

            var viable = candidates.Where(x => x.ProjectedGainRatio > 1.000001
                                                && !double.IsNaN(x.Score)
                                                && !double.IsInfinity(x.Score)).ToList();
            if (viable.Count == 0)
            {
                var holdUntil = Math.Max(7200, elapsed + 60);
                _stickyTarget = -1;
                _stickyKind = string.Empty;
                return new RebirthRecommendation
                {
                    TargetSeconds = holdUntil,
                    Reason = "hold: no modeled checkpoint preserves or increases the current Number multiplier",
                    RunnerUpSeconds = holdUntil,
                    RunnerUpReason = "wait for native Number preview, boss catch-up, or permanent growth to improve",
                    SelectedScorePerHour = 0,
                    RunnerUpScorePerHour = 0,
                    ProjectedMultiplier = c.nextAttackMulti,
                    ProjectedAP = holdUntil < 4100 ? 0 : 1 + (holdUntil - 4100) / 500,
                    CandidateSummary = "all modeled candidates rejected by monotonic Number constraint",
                    CandidateCount = candidates.Count
                };
            }

            var ordered = viable.OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.CapScore).ThenBy(x => x.Time).ToList();
            var selected = ordered[0];
            var sticky = viable.FirstOrDefault(x => x.Time == _stickyTarget && x.Kind == _stickyKind);
            if (sticky != null && sticky.Time >= minimum && sticky.Score >= selected.Score * 0.9995)
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
                Reason = selected.Reason,
                RunnerUpSeconds = runnerUp.Time,
                RunnerUpDeltaSeconds = Math.Abs(runnerUp.Time - selected.Time),
                RunnerUpReason = runnerUp.Reason,
                SelectedScorePerHour = selected.Score,
                RunnerUpScorePerHour = runnerUp.Score,
                ProjectedMultiplier = selected.ProjectedMultiplier,
                ProjectedAP = selected.ProjectedAP,
                CandidateSummary = summary,
                CandidateCount = candidates.Count
            };
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

            var includesBoss = bossEta >= 0 && remaining >= bossEta + 1;
            var projectedBossMulti = currentBossMulti * (includesBoss ? 2.0 : 1.0);
            var projected = 1.0 + staticFactor * projectedBossMulti * projectedNumberStep
                            * ExactTimeMultiplier(candidate.Time);
            var currentMultiplier = Math.Max(1e-300, (double)c.attackMulti);
            candidate.ProjectedMultiplier = projected;
            candidate.ProjectedGainRatio = projected / currentMultiplier;
            // Explicit objective: maximize compounded logarithmic Attack/Defense
            // multiplier growth per wall-clock hour.
            candidate.Score = candidate.ProjectedGainRatio <= 1.000001 ? double.NegativeInfinity
                : 3600.0 * Math.Log(candidate.ProjectedGainRatio) / duration;
            candidate.ProjectedAP = candidate.Time < 4100 ? 0 : 1 + (candidate.Time - 4100) / 500;
            candidate.CapScore = ProjectedCapCompression(c, remaining) / duration;
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

        private static int SecondsToNextTrainingEvent(Character c)
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
            if (cap <= 1) return 0;
            var maxReduction = cap / 10L + 1L;
            var requiredPow = Math.Max(0.0, (maxReduction - 1.0) * 500.0 * 1000.0 / cap);
            var estimate = 500L * tier + (long)Math.Ceiling(Math.Pow(requiredPow, 1.0 / 1.2));
            while (estimate > 0 && CapReduction(estimate - 1, cap, tier) >= maxReduction) estimate--;
            while (CapReduction(estimate, cap, tier) < maxReduction) estimate++;
            return estimate;
        }

        internal static long CapReduction(long level, long cap, int tier)
        {
            var shifted = Math.Max(0.0, level - 500.0 * tier);
            var raw = (long)(1.0 + Math.Pow(shifted, 1.2) / 500.0 * cap / 1000.0);
            return Math.Max(1L, Math.Min(cap / 10L + 1L, raw));
        }
    }
}
