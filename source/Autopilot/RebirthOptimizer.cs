using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Managers;

/*
FILE PURPOSE

RebirthOptimizer contains two deliberately separated policies. RebirthTransitionKernel and
RebirthRouteEvaluator are pure source-order mechanics: Number is replaced by the exact preview,
reset replay starts at Boss 0, every supplied Boss and Attack-training event is applied, and Beard
and MacGuffin conversion uses PermanentMarginalOracle. The incumbent one-run score remains only as
the live fallback until tasks 28/29 transfer authority from shadow route traces.

The pure route API consumes deterministic Boss replay durations produced by the exact combat
projection layer. It never invents repeated geometric Number growth. An unreachable terminal has
ETA=-1 and a separate finite next-continuation event, so unknown recovery cannot authorize reset.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class RebirthPreviewResult
    {
        internal double Attack;
        internal double Defense;
    }

    internal sealed class RebirthBankInput
    {
        internal long[] ActiveBeardTemporaryLevels = new long[0];
        internal long BeardPerk21Level;
        internal MacGuffinConversionInput[] EquippedMacGuffins = new MacGuffinConversionInput[0];
        internal bool SadisticTrollTwo;
        internal double MacGuffinBoosterMultiplier = 1.0;
    }

    internal sealed class RebirthBankResult
    {
        internal long BeardTrimmings;
        internal double MacGuffinAccumulatorDelta;
        internal double MacGuffinDeltaLogEffect;
        internal double NumberMacGuffinDeltaLogEffect;
    }

    /*
    PURE NATIVE-EQUIVALENT REBIRTH STATE

    PersistentNumberFactor is every preview factor other than boss/time/training/Blood. Keeping
    Blood explicit makes the same-frame Blood preview fixture auditable. Both native preview fields
    use total Attack Training levels; Defense Training is intentionally absent.
    */
    internal sealed class RebirthTransitionState
    {
        internal double CurrentAttackNumber = 1.0;
        internal double CurrentDefenseNumber = 1.0;
        internal double BossMulti = 1.0;
        internal double OldBossMulti = 1.0;
        internal double TimeMulti;
        internal double OldTimeMulti = 1.0;
        internal long TotalAttackTrainingLevels;
        internal double AttackPersistentNumberFactor = 1.0;
        internal double DefensePersistentNumberFactor = 1.0;
        internal double BloodPower = 1.0;
        internal double RunSeconds;
        internal int BossId;
        internal long CumulativeBeardTrimmings;
        internal double CumulativeMacGuffinAccumulatorDelta;
        internal double CumulativeMacGuffinDeltaLogEffect;

        internal RebirthTransitionState Clone()
        {
            return (RebirthTransitionState)MemberwiseClone();
        }
    }

    internal static class RebirthTransitionKernel
    {
        internal static readonly double[] TimeMultiplierBoundaries =
            {60.0, 120.0, 180.0, 240.0, 300.0, 420.0, 600.0, 720.0, 900.0, 1800.0, 3600.0};

        internal static double ExactTimeMultiplier(double seconds)
        {
            var t = Math.Max(0.0, seconds);
            if (t < 60.0) return t / 34359738368.0 / 3600.0;
            if (t < 120.0) return t / 33554432.0 / 3600.0;
            if (t < 180.0) return t / 518144.0 / 3600.0;
            if (t < 240.0) return t / 16192.0 / 3600.0;
            if (t < 300.0) return t / 2048.0 / 3600.0;
            if (t < 420.0) return t / 512.0 / 3600.0;
            if (t < 600.0) return t / 128.0 / 3600.0;
            if (t < 720.0) return t / 32.0 / 3600.0;
            if (t < 900.0) return t / 8.0 / 3600.0;
            if (t < 1800.0) return t / 4.0 / 3600.0;
            if (t < 3600.0) return t / 2.0 / 3600.0;
            return 1.0 + t / 172800.0;
        }

        internal static long AttackTrainingStep(long totalAttackLevels)
        {
            return Math.Max(0L, totalAttackLevels) / 10000L + 1L;
        }

        internal static RebirthPreviewResult Preview(RebirthTransitionState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            var step = AttackTrainingStep(state.TotalAttackTrainingLevels);
            var common = Positive(state.BossMulti) * Positive(state.OldBossMulti)
                         * Positive(state.OldTimeMulti) * step * Positive(state.TimeMulti)
                         * Positive(state.BloodPower);
            return new RebirthPreviewResult
            {
                Attack = 1.0 + common * Positive(state.AttackPersistentNumberFactor),
                Defense = 1.0 + common * Positive(state.DefensePersistentNumberFactor)
            };
        }

        internal static RebirthBankResult EvaluateBank(RebirthBankInput input, double rebirthSeconds)
        {
            var result = new RebirthBankResult();
            if (input == null) return result;
            var beards = input.ActiveBeardTemporaryLevels ?? new long[0];
            for (var i = 0; i < beards.Length; i++)
                result.BeardTrimmings += PermanentMarginalOracle.BeardBankDelta(
                    beards[i], rebirthSeconds, input.BeardPerk21Level);
            var guffs = input.EquippedMacGuffins ?? new MacGuffinConversionInput[0];
            for (var i = 0; i < guffs.Length; i++)
            {
                if (guffs[i] == null) continue;
                var bank = PermanentMarginalOracle.EvaluateMacGuffinBank(guffs[i],
                    rebirthSeconds, input.SadisticTrollTwo,
                    input.MacGuffinBoosterMultiplier);
                result.MacGuffinAccumulatorDelta += bank.AccumulatorDelta;
                result.MacGuffinDeltaLogEffect += bank.WeightedDeltaLogEffect;
                if (bank.EffectTarget == PermanentEffectTarget.Number)
                    result.NumberMacGuffinDeltaLogEffect += bank.DeltaLogEffect;
            }
            return result;
        }

        internal static RebirthTransitionState ApplyOrdinaryRebirth(
            RebirthTransitionState finishedRun, RebirthBankInput bankInput)
        {
            if (finishedRun == null) throw new ArgumentNullException("finishedRun");
            var preview = Preview(finishedRun);
            if (!FinitePositive(preview.Attack) || !FinitePositive(preview.Defense))
                throw new InvalidOperationException("rebirth preview must be finite and positive");
            var bank = EvaluateBank(bankInput, finishedRun.RunSeconds);
            var successor = finishedRun.Clone();
            // Native setNewMultis is assignment, never current * (preview/current).
            successor.CurrentAttackNumber = preview.Attack;
            successor.CurrentDefenseNumber = preview.Defense;
            successor.OldBossMulti = finishedRun.BossMulti;
            successor.OldTimeMulti = finishedRun.TimeMulti;
            successor.BossMulti = 1.0;
            successor.TimeMulti = 0.0;
            successor.TotalAttackTrainingLevels = 0L;
            successor.BloodPower = 1.0;
            successor.RunSeconds = 0.0;
            successor.BossId = 0;
            successor.CumulativeBeardTrimmings += bank.BeardTrimmings;
            successor.CumulativeMacGuffinAccumulatorDelta += bank.MacGuffinAccumulatorDelta;
            successor.CumulativeMacGuffinDeltaLogEffect += bank.MacGuffinDeltaLogEffect;
            // applyAllMacguffinBonuses precedes setNewMultis, but engage never recalculates the
            // already-published preview. The Number Guff therefore affects later previews only.
            var numberGuffMultiplier = Math.Exp(bank.NumberMacGuffinDeltaLogEffect);
            if (FinitePositive(numberGuffMultiplier))
            {
                successor.AttackPersistentNumberFactor *= numberGuffMultiplier;
                successor.DefensePersistentNumberFactor *= numberGuffMultiplier;
            }
            return successor;
        }

        private static double Positive(double value)
        {
            return FinitePositive(value) ? value : 0.0;
        }

        internal static bool FinitePositive(double value)
        {
            return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    internal sealed class RebirthRouteBossStep
    {
        internal int FromBossId;
        internal int ToBossId;
        internal double ReplaySeconds;
        internal double MinimumAttackNumber = 1.0;
        internal double MinimumDefenseNumber = 1.0;
        internal double BossMultiFactor = 2.0;
    }

    internal sealed class RebirthRouteTrainingStep
    {
        internal double AtRunSeconds;
        internal long AttackLevelsGained;
    }

    internal sealed class RebirthRouteProblem
    {
        internal RebirthTransitionState InitialState = new RebirthTransitionState();
        internal RebirthRouteBossStep[] BossSteps = new RebirthRouteBossStep[0];
        internal RebirthRouteTrainingStep[] TrainingSteps = new RebirthRouteTrainingStep[0];
        internal double[] ResetCandidateAges = new double[0];
        internal double MinimumRebirthSeconds;
        internal double HorizonSeconds = 172800.0;
        internal int TargetBossId;
        internal int MaximumResets = 3;
        internal int MaximumEvents = 64;
        internal RebirthBankInput BankInput;
    }

    internal sealed class RebirthRouteEstimate
    {
        internal bool TerminalReached;
        internal double EtaSeconds = -1.0;
        internal double NextContinuationEventSeconds = -1.0;
        internal bool FirstActionIsReset;
        internal int ResetCount;
        internal string[] Actions = new string[0];
        internal RebirthTransitionState FinalState;
    }

    internal sealed class RebirthRouteComparison
    {
        internal RebirthRouteEstimate Continue;
        internal RebirthRouteEstimate Reset;
        internal RebirthRouteEstimate Preferred;
        internal string Reason = string.Empty;
    }

    /*
    BOUNDED EVENT ROUTE EVALUATOR

    BossSteps are deterministic replay macro-edges. Their durations can be supplied by task 10's
    source-order combat kernel without exposing Unity objects here. Attack-training events are
    applied at every crossed run age and alter only the next native Number preview. Reset branches
    always call ApplyOrdinaryRebirth, which returns to Boss 0 and preserves only native one-run
    boss/time memory.
    */
    internal static class RebirthRouteEvaluator
    {
        private sealed class SearchResult
        {
            internal bool Reached;
            internal double Seconds = double.PositiveInfinity;
            internal int Resets;
            internal List<string> Actions = new List<string>();
            internal RebirthTransitionState Final;
        }

        internal static RebirthRouteComparison Compare(RebirthRouteProblem problem)
        {
            Validate(problem);
            var continuation = EvaluateContinuation(problem);
            RebirthRouteEstimate bestReset = null;
            foreach (var age in CandidateAges(problem))
            {
                if (age + 1e-12 < Math.Max(problem.MinimumRebirthSeconds,
                        problem.InitialState.RunSeconds)) continue;
                var candidate = EvaluateForcedReset(problem, age);
                if (Better(candidate, bestReset)) bestReset = candidate;
            }
            if (bestReset == null)
                bestReset = new RebirthRouteEstimate
                {
                    NextContinuationEventSeconds = NextContinuationEvent(problem,
                        problem.InitialState),
                    FinalState = problem.InitialState.Clone()
                };
            var preferred = Better(bestReset, continuation) ? bestReset : continuation;
            return new RebirthRouteComparison
            {
                Continue = continuation,
                Reset = bestReset,
                Preferred = preferred,
                Reason = preferred.TerminalReached
                    ? (preferred.FirstActionIsReset
                        ? "shadow reset route reaches the terminal first through exact Boss-0 replay"
                        : "shadow continuation route reaches the terminal first")
                    : "terminal is outside the bounded model; preserve the finite continuation edge"
            };
        }

        internal static RebirthRouteEstimate EvaluateContinuation(RebirthRouteProblem problem)
        {
            Validate(problem);
            var initial = problem.InitialState.Clone();
            if (initial.BossId >= problem.TargetBossId)
                return Estimate(problem, Search(problem, initial, 0.0, 0, 0, true),
                    initial, false);

            // A continuation is a real finite edge, not the incumbent score's invented HOLD=0.
            // Advance through exactly the next chronological event, then value the complete
            // successor with the same reset/Boss replay search used by the reset branch.
            var edgeSeconds = NextContinuationEvent(problem, initial);
            if (edgeSeconds < 0.0 || edgeSeconds > problem.HorizonSeconds)
                return new RebirthRouteEstimate
                {
                    NextContinuationEventSeconds = edgeSeconds,
                    FinalState = initial
                };
            var successor = initial.Clone();
            var actions = new List<string>();
            var boss = BossStep(problem, successor.BossId);
            var bossIsNext = boss != null
                             && successor.CurrentAttackNumber + 1e-12
                                >= boss.MinimumAttackNumber
                             && successor.CurrentDefenseNumber + 1e-12
                                >= boss.MinimumDefenseNumber
                             && Math.Abs(Math.Max(0.0, boss.ReplaySeconds) - edgeSeconds) <= 1e-12;
            if (bossIsNext)
            {
                AdvanceRun(successor, successor.RunSeconds + edgeSeconds,
                    problem.TrainingSteps);
                successor.BossId = boss.ToBossId;
                successor.BossMulti *= boss.BossMultiFactor;
                actions.Add("boss:" + boss.FromBossId + "->" + boss.ToBossId);
            }
            else
            {
                AdvanceRun(successor, successor.RunSeconds + edgeSeconds,
                    problem.TrainingSteps);
                actions.Add("continue@" + successor.RunSeconds.ToString("0.###") + "s");
            }
            var search = Search(problem, successor, edgeSeconds, 0, 1, true);
            for (var i = actions.Count - 1; i >= 0; i--)
                search.Actions.Insert(0, actions[i]);
            var estimate = Estimate(problem, search, successor, false);
            estimate.NextContinuationEventSeconds = edgeSeconds;
            return estimate;
        }

        internal static RebirthRouteEstimate EvaluateForcedReset(RebirthRouteProblem problem,
            double resetAge)
        {
            Validate(problem);
            var initial = problem.InitialState.Clone();
            if (resetAge + 1e-12 < initial.RunSeconds
                || resetAge + 1e-12 < problem.MinimumRebirthSeconds)
                return new RebirthRouteEstimate
                {
                    NextContinuationEventSeconds = NextContinuationEvent(problem, initial),
                    FinalState = initial
                };
            var wait = resetAge - initial.RunSeconds;
            if (wait > problem.HorizonSeconds) return new RebirthRouteEstimate
            {
                NextContinuationEventSeconds = NextContinuationEvent(problem, initial),
                FinalState = initial
            };
            var preResetActions = new List<string>();
            AdvanceToResetAge(problem, initial, resetAge, preResetActions);
            var successor = RebirthTransitionKernel.ApplyOrdinaryRebirth(initial, problem.BankInput);
            ApplyTrainingAtRunStart(successor, problem.TrainingSteps);
            var search = Search(problem, successor, wait, 1,
                1 + preResetActions.Count, true);
            search.Actions.Insert(0, "reset@" + resetAge.ToString("0.###") + "s");
            for (var i = preResetActions.Count - 1; i >= 0; i--)
                search.Actions.Insert(0, preResetActions[i]);
            search.Resets = Math.Max(1, search.Resets);
            return Estimate(problem, search, successor, true);
        }

        private static void AdvanceToResetAge(RebirthRouteProblem problem,
            RebirthTransitionState state, double resetAge, IList<string> actions)
        {
            // Fight Boss progresses independently while the run waits for its checkpoint. Apply
            // every deterministic supplied replay edge which both fits before the reset age and
            // is viable with the currently banked Number. This replaces the former one-Boss flag.
            while (true)
            {
                var boss = BossStep(problem, state.BossId);
                if (boss == null || boss.ToBossId <= boss.FromBossId
                    || boss.ReplaySeconds < 0.0
                    || state.RunSeconds + boss.ReplaySeconds > resetAge + 1e-12
                    || state.CurrentAttackNumber + 1e-12 < boss.MinimumAttackNumber
                    || state.CurrentDefenseNumber + 1e-12 < boss.MinimumDefenseNumber)
                    break;
                AdvanceRun(state, state.RunSeconds + boss.ReplaySeconds,
                    problem.TrainingSteps);
                state.BossId = boss.ToBossId;
                state.BossMulti *= boss.BossMultiFactor;
                actions.Add("boss:" + boss.FromBossId + "->" + boss.ToBossId);
            }
            AdvanceRun(state, resetAge, problem.TrainingSteps);
        }

        private static SearchResult Search(RebirthRouteProblem problem,
            RebirthTransitionState state, double absoluteSeconds, int resets, int events,
            bool allowFurtherResets)
        {
            if (state.BossId >= problem.TargetBossId)
                return new SearchResult
                {
                    Reached = true,
                    Seconds = absoluteSeconds,
                    Resets = resets,
                    Final = state.Clone()
                };
            if (events >= problem.MaximumEvents || absoluteSeconds >= problem.HorizonSeconds)
                return new SearchResult
                {
                    Seconds = absoluteSeconds,
                    Final = state.Clone(),
                    Resets = resets
                };

            SearchResult best = null;
            var boss = BossStep(problem, state.BossId);
            if (boss != null && boss.ToBossId > boss.FromBossId
                && boss.ReplaySeconds >= 0.0
                && state.CurrentAttackNumber + 1e-12 >= boss.MinimumAttackNumber
                && state.CurrentDefenseNumber + 1e-12 >= boss.MinimumDefenseNumber
                && absoluteSeconds + boss.ReplaySeconds <= problem.HorizonSeconds)
            {
                var next = state.Clone();
                AdvanceRun(next, next.RunSeconds + boss.ReplaySeconds, problem.TrainingSteps);
                next.BossId = boss.ToBossId;
                next.BossMulti *= boss.BossMultiFactor;
                var branch = Search(problem, next, absoluteSeconds + boss.ReplaySeconds,
                    resets, events + 1, allowFurtherResets);
                branch.Actions.Insert(0, "boss:" + boss.FromBossId + "->" + boss.ToBossId);
                if (Better(branch, best)) best = branch;
            }

            if (allowFurtherResets && resets < problem.MaximumResets)
            {
                foreach (var age in CandidateAges(problem))
                {
                    if (age <= state.RunSeconds + 1e-12
                        || age + 1e-12 < problem.MinimumRebirthSeconds) continue;
                    var wait = age - state.RunSeconds;
                    if (absoluteSeconds + wait > problem.HorizonSeconds) continue;
                    var finished = state.Clone();
                    var preResetActions = new List<string>();
                    AdvanceToResetAge(problem, finished, age, preResetActions);
                    var reset = RebirthTransitionKernel.ApplyOrdinaryRebirth(
                        finished, problem.BankInput);
                    ApplyTrainingAtRunStart(reset, problem.TrainingSteps);
                    var branch = Search(problem, reset, absoluteSeconds + wait,
                        resets + 1, events + 1 + preResetActions.Count, true);
                    branch.Actions.Insert(0, "reset@" + age.ToString("0.###") + "s");
                    for (var i = preResetActions.Count - 1; i >= 0; i--)
                        branch.Actions.Insert(0, preResetActions[i]);
                    branch.Resets = Math.Max(branch.Resets, resets + 1);
                    if (Better(branch, best)) best = branch;
                }
            }
            return best ?? new SearchResult
            {
                Seconds = absoluteSeconds,
                Final = state.Clone(),
                Resets = resets
            };
        }

        private static RebirthRouteEstimate Estimate(RebirthRouteProblem problem,
            SearchResult search, RebirthTransitionState fallback, bool firstReset)
        {
            var final = search.Final ?? fallback.Clone();
            return new RebirthRouteEstimate
            {
                TerminalReached = search.Reached,
                EtaSeconds = search.Reached ? search.Seconds : -1.0,
                NextContinuationEventSeconds = search.Reached ? search.Seconds
                    : search.Seconds + NextContinuationEvent(problem, final),
                FirstActionIsReset = firstReset,
                ResetCount = search.Resets,
                Actions = search.Actions.ToArray(),
                FinalState = final
            };
        }

        private static void AdvanceRun(RebirthTransitionState state, double newRunSeconds,
            IEnumerable<RebirthRouteTrainingStep> source)
        {
            var old = state.RunSeconds;
            foreach (var step in (source ?? Enumerable.Empty<RebirthRouteTrainingStep>())
                         .Where(x => x != null).OrderBy(x => x.AtRunSeconds))
            {
                if (step.AtRunSeconds <= old + 1e-12
                    || step.AtRunSeconds > newRunSeconds + 1e-12) continue;
                var gained = Math.Max(0L, step.AttackLevelsGained);
                state.TotalAttackTrainingLevels = state.TotalAttackTrainingLevels
                                                  > long.MaxValue - gained
                    ? long.MaxValue : state.TotalAttackTrainingLevels + gained;
            }
            state.RunSeconds = Math.Max(old, newRunSeconds);
            state.TimeMulti = RebirthTransitionKernel.ExactTimeMultiplier(state.RunSeconds);
        }

        private static void ApplyTrainingAtRunStart(RebirthTransitionState state,
            IEnumerable<RebirthRouteTrainingStep> source)
        {
            // Native cap compression/insta-train occurs after the reset has banked Number. Encode
            // it as an age-zero event so it changes the following preview without retroactively
            // changing the Number just assigned by setNewMultis.
            foreach (var step in (source ?? Enumerable.Empty<RebirthRouteTrainingStep>())
                         .Where(x => x != null && x.AtRunSeconds <= 1e-12)
                         .OrderBy(x => x.AtRunSeconds))
            {
                var gained = step.AttackLevelsGained;
                state.TotalAttackTrainingLevels = state.TotalAttackTrainingLevels
                                                  > long.MaxValue - gained
                    ? long.MaxValue : state.TotalAttackTrainingLevels + gained;
            }
        }

        private static double NextContinuationEvent(RebirthRouteProblem problem,
            RebirthTransitionState state)
        {
            var bestAge = double.PositiveInfinity;
            var boss = BossStep(problem, state.BossId);
            if (boss != null && state.CurrentAttackNumber + 1e-12 >= boss.MinimumAttackNumber
                && state.CurrentDefenseNumber + 1e-12 >= boss.MinimumDefenseNumber)
                bestAge = state.RunSeconds + Math.Max(0.0, boss.ReplaySeconds);
            foreach (var step in problem.TrainingSteps ?? new RebirthRouteTrainingStep[0])
                if (step != null && step.AtRunSeconds > state.RunSeconds + 1e-12)
                    bestAge = Math.Min(bestAge, step.AtRunSeconds);
            foreach (var boundary in RebirthTransitionKernel.TimeMultiplierBoundaries)
                if (boundary > state.RunSeconds + 1e-12)
                {
                    bestAge = Math.Min(bestAge, boundary);
                    break;
                }
            var nextAp = state.RunSeconds < 4100.0 ? 4100.0
                : 4100.0 + 500.0 * (Math.Floor((state.RunSeconds - 4100.0) / 500.0) + 1.0);
            bestAge = Math.Min(bestAge, nextAp);
            return double.IsInfinity(bestAge) ? -1.0 : Math.Max(0.0, bestAge - state.RunSeconds);
        }

        private static RebirthRouteBossStep BossStep(RebirthRouteProblem problem, int bossId)
        {
            return (problem.BossSteps ?? new RebirthRouteBossStep[0])
                .Where(x => x != null && x.FromBossId == bossId)
                .OrderBy(x => x.ToBossId).FirstOrDefault();
        }

        private static double[] CandidateAges(RebirthRouteProblem problem)
        {
            return (problem.ResetCandidateAges ?? new double[0])
                .Where(x => x >= 0.0 && !double.IsNaN(x) && !double.IsInfinity(x))
                .Distinct().OrderBy(x => x).ToArray();
        }

        private static bool Better(RebirthRouteEstimate left, RebirthRouteEstimate right)
        {
            if (left == null) return false;
            if (right == null) return true;
            if (left.TerminalReached != right.TerminalReached) return left.TerminalReached;
            if (left.TerminalReached && Math.Abs(left.EtaSeconds - right.EtaSeconds) > 1e-12)
                return left.EtaSeconds < right.EtaSeconds;
            var leftNext = left.NextContinuationEventSeconds < 0.0
                ? double.PositiveInfinity : left.NextContinuationEventSeconds;
            var rightNext = right.NextContinuationEventSeconds < 0.0
                ? double.PositiveInfinity : right.NextContinuationEventSeconds;
            return leftNext < rightNext - 1e-12;
        }

        private static bool Better(SearchResult left, SearchResult right)
        {
            if (left == null) return false;
            if (right == null) return true;
            if (left.Reached != right.Reached) return left.Reached;
            if (!left.Reached) return false;
            if (Math.Abs(left.Seconds - right.Seconds) > 1e-12)
                return left.Seconds < right.Seconds;
            return left.Resets < right.Resets;
        }

        private static void Validate(RebirthRouteProblem problem)
        {
            if (problem == null) throw new ArgumentNullException("problem");
            if (problem.InitialState == null) throw new ArgumentException(
                "route initial state is required", "problem");
            if (problem.HorizonSeconds < 0.0 || double.IsNaN(problem.HorizonSeconds)
                || double.IsInfinity(problem.HorizonSeconds))
                throw new ArgumentOutOfRangeException("problem", "route horizon must be finite");
            if (problem.MaximumResets < 0 || problem.MaximumEvents <= 0)
                throw new ArgumentOutOfRangeException("problem", "route bounds must be positive");
            if (double.IsNaN(problem.InitialState.RunSeconds)
                || double.IsInfinity(problem.InitialState.RunSeconds)
                || problem.InitialState.RunSeconds < 0.0)
                throw new ArgumentOutOfRangeException("problem", "initial run age must be finite");
            foreach (var boss in problem.BossSteps ?? new RebirthRouteBossStep[0])
            {
                if (boss == null) continue;
                if (boss.FromBossId < 0 || boss.ToBossId <= boss.FromBossId
                    || double.IsNaN(boss.ReplaySeconds)
                    || double.IsInfinity(boss.ReplaySeconds) || boss.ReplaySeconds < 0.0
                    || !RebirthTransitionKernel.FinitePositive(boss.MinimumAttackNumber)
                    || !RebirthTransitionKernel.FinitePositive(boss.MinimumDefenseNumber)
                    || !RebirthTransitionKernel.FinitePositive(boss.BossMultiFactor))
                    throw new ArgumentOutOfRangeException("problem",
                        "Boss replay edges must be finite, forward, and positive");
            }
            foreach (var training in problem.TrainingSteps ?? new RebirthRouteTrainingStep[0])
            {
                if (training == null) continue;
                if (double.IsNaN(training.AtRunSeconds)
                    || double.IsInfinity(training.AtRunSeconds)
                    || training.AtRunSeconds < 0.0 || training.AttackLevelsGained < 0L)
                    throw new ArgumentOutOfRangeException("problem",
                        "Attack-training events must be finite and non-negative");
            }
        }
    }

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

            var currentTimeMulti = RebirthTransitionKernel.ExactTimeMultiplier(Math.Max(1, elapsed));
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
                            * RebirthTransitionKernel.ExactTimeMultiplier(candidate.Time);
            var projectedDefense = 1.0 + staticDefenseFactor * projectedBossMulti * projectedNumberStep
                                   * RebirthTransitionKernel.ExactTimeMultiplier(candidate.Time);
            var currentMultiplier = Math.Max(1e-300, (double)c.attackMulti);
            candidate.ProjectedMultiplier = projected;
            candidate.ProjectedGainRatio = Math.Min(projected / currentMultiplier,
                projectedDefense / Math.Max(1e-300, (double)c.defenseMulti));
            var recoveryStart = c.bossID + (includesBoss ? 1 : 0);
            candidate.RemainingCatchupBosses = Math.Max(0, c.highestBoss - recoveryStart);
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
            otherwise the shortest candidate wins merely by amortizing the inherited baseline. Boss-record
            recovery is intentionally absent here; only RebirthRouteEvaluator may publish that route ETA.
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

        // Compatibility hook retained for telemetry callers. The former implementation
        // geometrically compounded next/current Number and therefore had mutation authority over
        // a different dynamical system. Callers must build a RebirthRouteProblem and use Compare;
        // without that full Boss-0 replay input this method fails closed.
        internal static bool RecoveryResetEfficient(Character c, int selectedBossEta,
            out int resetRouteEta, out int continueRouteEta, out string reason)
        {
            resetRouteEta = -1;
            continueRouteEta = selectedBossEta < 0 ? -1 : selectedBossEta;
            if (c == null || c.bossID >= c.highestBoss)
            {
                reason = "boss record already caught up; normal checkpoint objective applies";
                return true;
            }
            reason = "hold: exact recovery needs a bounded RebirthRouteProblem with Boss-0 replay; geometric Number authority is disabled";
            return false;
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
