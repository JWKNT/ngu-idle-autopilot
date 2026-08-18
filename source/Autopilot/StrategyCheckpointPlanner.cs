using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NGUInjector.Managers;

/*
FILE PURPOSE

StrategyCheckpointPlanner turns source-derived rebirth events into comparable run checkpoints.
It reads one Character snapshot, enumerates exact local discontinuities (Number time factor, AP,
Basic Training, fruit maturity, Titan clocks, Fight Boss viability, Beard and MacGuffin banks),
and returns a scored recommendation plus runner-up evidence.  It never mutates the game.

The score is deliberately split into exact mechanics and declared policy weights.  Native preview
ratios, clock arithmetic, fruit tiers, and time factors are facts; their exchange rate against a
terminal-route second is not exposed by NGU Idle and is therefore a heuristic prior.  Callers must
preserve puzzle/challenge constraints and the executor must revalidate every discrete event before
committing a rebirth.  Adding a mechanic means adding a candidate boundary and a separately named
utility term, never hiding a new fixed schedule inside a stage branch.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class StrategyCheckpointRecommendation
    {
        internal int TargetSeconds = -1;
        internal string Reason = "No legal source-derived checkpoint was found";
        internal int RunnerUpSeconds = -1;
        internal string RunnerUpReason = string.Empty;
        internal double SelectedScorePerHour;
        internal double RunnerUpScorePerHour;
        internal int CandidateCount;
        internal string CandidateSummary = string.Empty;
        internal bool ExecutionHold = true;
        internal int NextPositiveEtaSeconds = -1;
        internal int NextEvaluationEtaSeconds = 1;
        internal string EtaReason = "checkpoint state unavailable; reevaluate in 1s";
    }

    internal static class StrategyCheckpointPlanner
    {
        private const int MaximumHorizonSeconds = 172800;

        private sealed class Candidate
        {
            internal int Target;
            internal string Label;
            internal string Provenance;
            internal double EventValue;
            internal double Score;
        }

        /*
        CHECKPOINT SELECTION

        The frozen-snapshot projection scales the live native Number preview only by the exact
        future time multiplier. Beard value includes temporary-level accumulation, while MacGuffin
        value is the exact per-rebirth time factor. This distinction prevents treating a one-hour
        Beard run and a one-hour Guff run as the same economic object. A reset-recovery penalty
        prices rebuilding reset-local systems; event bonuses prevent a reset just before a high
        value discrete action. The exchange weights are explicit here because no native controller
        supplies a universal AP/EXP/fruit/Titan-to-terminal-seconds conversion.
        */
        internal static StrategyCheckpointRecommendation Select(Character c, int baselineTarget,
            string baselineReason)
        {
            var result = new StrategyCheckpointRecommendation();
            if (c == null || c.rebirth == null || c.rebirthTime == null)
                return result;

            var elapsed = Math.Max(0, (int)Math.Floor(c.rebirthTime.totalseconds));
            var legal = Math.Max(elapsed,
                (int)Math.Ceiling(Math.Max(0.0, (double)c.rebirth.minRebirthTime())));
            var maximumHorizon = elapsed > int.MaxValue - MaximumHorizonSeconds
                ? int.MaxValue : elapsed + MaximumHorizonSeconds;
            var horizon = Math.Min(maximumHorizon,
                Math.Max(legal + 60, Math.Max(baselineTarget, elapsed + 86400)));
            var candidates = new List<Candidate>();

            Add(candidates, legal, "first legal ordinary rebirth", "native rule", 0.0,
                legal, horizon);
            Add(candidates, Math.Max(legal, baselineTarget), baselineReason,
                "guide/stage prior", 0.18, legal, horizon);

            // The native time multiplier is discontinuous at these exact run ages.
            foreach (var boundary in new[] {300, 420, 600, 720, 900, 1800, 3600})
                Add(candidates, boundary, "Number time-multiplier discontinuity at " + boundary + "s",
                    "native rule", 0.08, legal, horizon);

            AddApBoundaries(candidates, elapsed, legal, horizon);
            AddTrainingBoundary(c, candidates, elapsed, legal, horizon);
            AddFightBossBoundary(c, candidates, elapsed, legal, horizon);
            AddFruitBoundaries(c, candidates, elapsed, legal, horizon);
            AddTitanBoundaries(c, candidates, elapsed, legal, horizon);

            if (c.settings.beardsOn)
                Add(candidates, 86400, "Beard trim time factor reaches its native maximum",
                    "native rule", 0.25, legal, horizon);
            if (EquippedMacGuffinCount(c) > 0)
            {
                Add(candidates, 180, "MacGuffin bank begins producing levels", "native rule",
                    0.06, legal, horizon);
                Add(candidates, 1800, "MacGuffin time factor changes from quadratic to square-root",
                    "native rule", 0.18, legal, horizon);
                if (c.settings.rebirthDifficulty == difficulty.sadistic
                    && c.allChallenges.trollChallenge.sadisticCompletions() >= 2)
                    Add(candidates, 86400, "Sadistic Troll-2 MacGuffin time factor reaches its daily kink",
                        "native rule", 0.30, legal, horizon);
            }

            // Multiple facts can share one second. Preserve every reason and take the largest
            // discrete-event value rather than double-counting the same reset boundary.
            var merged = candidates.GroupBy(x => x.Target).Select(group => new Candidate
            {
                Target = group.Key,
                Label = string.Join("; ", group.Select(x => x.Label).Distinct().ToArray()),
                Provenance = string.Join(" + ", group.Select(x => x.Provenance).Distinct().ToArray()),
                EventValue = group.Max(x => x.EventValue)
            }).OrderBy(x => x.Target).ToList();

            foreach (var candidate in merged)
                candidate.Score = Score(c, candidate, elapsed);
            var ranked = merged.Where(x => !double.IsNaN(x.Score) && !double.IsInfinity(x.Score))
                .OrderByDescending(x => x.Score).ThenBy(x => x.Target).ToList();
            if (ranked.Count == 0)
            {
                result.TargetSeconds = legal;
                result.CandidateCount = 1;
                result.CandidateSummary = "HOLD baseline=0.000000/h; no finite reset projection";
                result.Reason = "hold: continuing the current run is the only finite counterfactual";
                result.EtaReason = "positive-value reset ETA unknown; reevaluate in 1s";
                return result;
            }

            var selected = ranked[0];
            var runner = ranked.Count > 1 ? ranked[1] : null;

            /*
            NO-RESET COUNTERFACTUAL

            The event queue contains reset times, but continuation is also a legal action. Give it
            an explicit zero incremental-utility baseline so an all-negative queue cannot select the
            least destructive reset. The caller must propagate ExecutionHold to the generated profile;
            TimeRebirth independently repeats the same comparison immediately before mutation.
            */
            if (!ResetBeatsHold(selected.Score))
            {
                var eta = FindNextPositiveEta(c, elapsed, maximumHorizon);
                result.TargetSeconds = legal;
                result.Reason = "hold: continuing this run (0.000000/h) beats every event-queue reset";
                result.RunnerUpSeconds = selected.Target;
                result.RunnerUpReason = selected.Label + " [" + selected.Provenance + "]";
                result.SelectedScorePerHour = 0.0;
                result.RunnerUpScorePerHour = selected.Score;
                result.CandidateCount = ranked.Count + 1;
                result.CandidateSummary = "HOLD baseline=0.000000/h | "
                    + string.Join(" | ", ranked.Take(8).Select(x => x.Target + "s="
                        + x.Score.ToString("0.000000") + "/h " + x.Label).ToArray());
                result.ExecutionHold = true;
                result.NextPositiveEtaSeconds = eta;
                result.NextEvaluationEtaSeconds = 1;
                result.EtaReason = eta >= 0
                    ? "first conservative positive-value reset probe in " + eta.ToString("N0") + "s"
                    : "positive-value reset ETA unknown outside the 48-hour modeled horizon; reevaluate in 1s";
                return result;
            }

            result.TargetSeconds = selected.Target;
            result.Reason = "event-queue winner: " + selected.Label + " [" + selected.Provenance + "]";
            result.SelectedScorePerHour = selected.Score;
            result.CandidateCount = ranked.Count + 1;
            result.CandidateSummary = "HOLD baseline=0.000000/h | " + string.Join(" | ", ranked.Take(8).Select(x =>
                x.Target + "s=" + x.Score.ToString("0.000000") + "/h " + x.Label).ToArray());
            result.ExecutionHold = false;
            result.NextPositiveEtaSeconds = Math.Max(0, selected.Target - elapsed);
            result.NextEvaluationEtaSeconds = 1;
            result.EtaReason = selected.Target <= elapsed
                ? "positive-value reset is eligible now, subject to final mutation preflight"
                : "selected positive-value checkpoint in "
                  + Math.Max(0, selected.Target - elapsed).ToString("N0") + "s";
            if (runner != null)
            {
                result.RunnerUpSeconds = runner.Target;
                result.RunnerUpReason = runner.Label + " [" + runner.Provenance + "]";
                result.RunnerUpScorePerHour = runner.Score;
            }
            return result;
        }

        internal static bool ResetBeatsHold(double selectedScorePerHour)
        {
            return RebirthOptimizer.ResetBeatsHold(selectedScorePerHour);
        }

        private static int FindNextPositiveEta(Character c, int elapsed, int horizon)
        {
            var previous = elapsed;
            for (var target = elapsed + 60; target > elapsed && target <= horizon; target += 60)
            {
                var probe = new Candidate
                {
                    Target = target,
                    Label = "positive-value ETA probe",
                    Provenance = "counterfactual",
                    EventValue = 0.0
                };
                probe.Score = Score(c, probe, elapsed);
                if (!ResetBeatsHold(probe.Score))
                {
                    previous = target;
                    continue;
                }
                for (var exact = Math.Max(elapsed, previous + 1); exact <= target; exact++)
                {
                    probe.Target = exact;
                    probe.Score = Score(c, probe, elapsed);
                    if (ResetBeatsHold(probe.Score)) return Math.Max(0, exact - elapsed);
                }
                return Math.Max(0, target - elapsed);
            }
            return -1;
        }

        private static void AddApBoundaries(ICollection<Candidate> candidates, int elapsed,
            int legal, int horizon)
        {
            var next = elapsed < 4100 ? 4100 : 4100 + 500 * (1 + (elapsed - 4100) / 500);
            for (var target = next; target <= horizon; target += 500)
                Add(candidates, target, "time-AP award tick", "native rule", 0.04, legal, horizon);
        }

        private static void AddTrainingBoundary(Character c, ICollection<Candidate> candidates,
            int elapsed, int legal, int horizon)
        {
            var remaining = RebirthOptimizer.SecondsToNextTrainingEvent(c);
            if (remaining == int.MaxValue || remaining < 0) return;
            Add(candidates, elapsed + remaining,
                "next exact Attack Basic-Training level/cap-compression event",
                "source-derived model", 0.12, legal, horizon);
        }

        private static void AddFightBossBoundary(Character c, ICollection<Candidate> candidates,
            int elapsed, int legal, int horizon)
        {
            var remainingHorizon = Math.Max(0, horizon - elapsed);
            var eta = AutopilotManager.SelectedBossDefeatEta(c, remainingHorizon);
            if (eta < 0) return;
            Add(candidates, elapsed + eta, "selected Fight Boss defeat becomes feasible",
                "source-derived model", 0.35, legal, horizon);
        }

        private static void AddFruitBoundaries(Character c, ICollection<Candidate> candidates,
            int elapsed, int legal, int horizon)
        {
            if (!c.settings.yggdrasilOn || c.yggdrasil == null || c.yggdrasil.fruits == null
                || c.yggdrasilController == null || c.yggdrasilController.fruits == null
                || c.yggdrasilController.fruits.Length == 0)
                return;
            var threshold = c.yggdrasilController.fruits[0].tierThreshold();
            if (threshold <= 0) return;
            for (var i = 0; i < c.yggdrasil.fruits.Count; i++)
            {
                var fruit = c.yggdrasil.fruits[i];
                if (fruit == null || !fruit.activated || fruit.maxTier <= 0) continue;
                var maxTier = Math.Min(24L, fruit.maxTier);
                var currentTier = Math.Min(maxTier, (long)Math.Floor(fruit.seconds / threshold));
                if (currentTier < maxTier)
                {
                    var nextSeconds = Math.Max(0.0, (currentTier + 1) * threshold - fruit.seconds);
                    Add(candidates, elapsed + (int)Math.Ceiling(nextSeconds),
                        "fruit " + i + " reaches tier " + (currentTier + 1),
                        "native rule", 0.08 + 0.01 * (currentTier + 1), legal, horizon);
                    var fullSeconds = Math.Max(0.0, maxTier * threshold - fruit.seconds);
                    Add(candidates, elapsed + (int)Math.Ceiling(fullSeconds),
                        "fruit " + i + " reaches max tier " + maxTier,
                        "native rule", 0.18 + 0.01 * maxTier, legal, horizon);
                }
            }
        }

        private static void AddTitanBoundaries(Character c, ICollection<Candidate> candidates,
            int elapsed, int legal, int horizon)
        {
            var normal = c.allChallenges.noRebirthChallenge.completions();
            var evil = c.allChallenges.noRebirthChallenge.evilCompletions();
            var sadistic = c.allChallenges.noRebirthChallenge.sadisticCompletions();
            var reachable = ZoneHelpers.GetMaxReachableZone(true);
            for (var titanId = 1; titanId <= ZoneHelpers.TitanZones.Length; titanId++)
            {
                if (ZoneHelpers.TitanZones[titanId - 1] > reachable) continue;
                double clockElapsed;
                if (!TryTitanClockElapsed(c, titanId, out clockElapsed)) continue;
                var remaining = TitanMechanics.SecondsUntilReady(titanId, clockElapsed,
                    normal, evil, sadistic);
                // A ready clock is an action branch, not permission to reset. The combat/Titan
                // transaction owns that decision, so only future clock boundaries are candidates.
                if (remaining <= 0) continue;
                Add(candidates, elapsed + remaining, "Titan " + titanId + " clock becomes ready",
                    "native rule", 0.65 + 0.04 * titanId, legal, horizon);
            }
        }

        private static bool TryTitanClockElapsed(Character c, int titanId, out double seconds)
        {
            seconds = 0.0;
            if (c == null || c.adventure == null) return false;
            try
            {
                var adventureType = c.adventure.GetType();
                var member = adventureType.GetField("boss" + titanId + "Spawn",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var clock = member == null ? null : member.GetValue(c.adventure);
                if (clock == null) return false;
                var clockType = clock.GetType();
                var field = clockType.GetField("totalseconds",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    seconds = Convert.ToDouble(field.GetValue(clock));
                    return !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0.0;
                }
                var property = clockType.GetProperty("totalseconds",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property == null) return false;
                seconds = Convert.ToDouble(property.GetValue(clock, null));
                return !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0.0;
            }
            catch
            {
                // Unknown clock state contributes no candidate. It never authorizes a reset or a
                // Titan mutation, both of which are independently revalidated at execution time.
                return false;
            }
        }

        private static double Score(Character c, Candidate candidate, int elapsed)
        {
            var target = Math.Max(1, candidate.Target);
            var recovery = ResetRecoverySeconds(c);
            var currentRatio = MinimumNativeNumberRatio(c);
            var currentTime = NumberTimeMultiplier(Math.Max(1, elapsed));
            var futureRatio = currentRatio * NumberTimeMultiplier(target) / Math.Max(1e-300, currentTime);
            var utility = Math.Log(Math.Max(1e-300, futureRatio));

            if (c.settings.beardsOn)
            {
                var bankedSeconds = Math.Min(86400.0, target);
                var beardFactor = Math.Min(8.0, 1.0 + bankedSeconds / 10800.0);
                utility += 1.8 * bankedSeconds / 86400.0 * beardFactor;
            }

            var guffs = EquippedMacGuffinCount(c);
            if (guffs > 0)
            {
                var sadTroll = c.settings.rebirthDifficulty == difficulty.sadistic
                               && c.allChallenges.trollChallenge.sadisticCompletions() >= 2;
                utility += (sadTroll ? 2.8 : 1.1) * Math.Sqrt(guffs)
                           * MacGuffinTimeFactor(target, sadTroll) / (sadTroll ? 48.0 : 20.0);
            }

            var ap = MechanicsProgression.TimeAp(target);
            utility += Math.Min(1.5, ap * 0.015);
            utility += FruitBankUtility(c, target - elapsed);
            utility += candidate.EventValue;
            return 3600.0 * utility / Math.Max(1.0, target + recovery);
        }

        private static double MinimumNativeNumberRatio(Character c)
        {
            if (c.attackMulti <= 0.0 || c.defenseMulti <= 0.0) return 1e-300;
            var ratio = Math.Min(c.nextAttackMulti / c.attackMulti,
                c.nextDefenseMulti / c.defenseMulti);
            return double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio <= 0.0
                ? 1e-300 : ratio;
        }

        private static double NumberTimeMultiplier(double seconds)
        {
            if (seconds < 300.0) return seconds / 2048.0 / 3600.0;
            if (seconds < 420.0) return seconds / 512.0 / 3600.0;
            if (seconds < 600.0) return seconds / 128.0 / 3600.0;
            if (seconds < 720.0) return seconds / 32.0 / 3600.0;
            if (seconds < 900.0) return seconds / 8.0 / 3600.0;
            if (seconds < 1800.0) return seconds / 4.0 / 3600.0;
            if (seconds < 3600.0) return seconds / 2.0 / 3600.0;
            return 1.0 + seconds / 172800.0;
        }

        private static double MacGuffinTimeFactor(double seconds, bool sadisticTrollTwo)
        {
            if (seconds < 180.0) return 0.0;
            if (sadisticTrollTwo)
            {
                if (seconds <= 86400.0) return seconds / 1800.0;
                return Math.Min(104.864100, 48.0 * Math.Pow(seconds / 86400.0, 0.4));
            }
            if (seconds < 1800.0) return Math.Pow(seconds / 1800.0, 2.0);
            return Math.Min(20.0, Math.Sqrt(seconds / 1800.0));
        }

        private static double FruitBankUtility(Character c, int additionalSeconds)
        {
            if (additionalSeconds < 0 || !c.settings.yggdrasilOn || c.yggdrasil == null
                || c.yggdrasil.fruits == null || c.yggdrasilController == null
                || c.yggdrasilController.fruits == null || c.yggdrasilController.fruits.Length == 0)
                return 0.0;
            var threshold = c.yggdrasilController.fruits[0].tierThreshold();
            if (threshold <= 0) return 0.0;
            var utility = 0.0;
            foreach (var fruit in c.yggdrasil.fruits)
            {
                if (fruit == null || !fruit.activated || fruit.maxTier <= 0) continue;
                var before = Math.Min(fruit.maxTier, (long)Math.Floor(fruit.seconds / threshold));
                var after = Math.Min(fruit.maxTier,
                    (long)Math.Floor((fruit.seconds + additionalSeconds) / threshold));
                utility += 0.025 * Math.Max(0L, after - before);
            }
            return utility;
        }

        private static int EquippedMacGuffinCount(Character c)
        {
            return c.inventory == null || c.inventory.macguffins == null
                ? 0 : c.inventory.macguffins.Count(x => x != null && x.id > 0);
        }

        private static int ResetRecoverySeconds(Character c)
        {
            if (c.settings.rebirthDifficulty == difficulty.sadistic) return 7200;
            if (c.settings.rebirthDifficulty == difficulty.evil) return 3600;
            return c.settings.nguOn ? 1200 : 120;
        }

        private static void Add(ICollection<Candidate> candidates, int target, string label,
            string provenance, double eventValue, int legal, int horizon)
        {
            if (target < legal || target > horizon || string.IsNullOrEmpty(label)) return;
            candidates.Add(new Candidate
            {
                Target = target,
                Label = label,
                Provenance = provenance,
                EventValue = eventValue
            });
        }
    }
}
