/*
FILE PURPOSE

Purpose: Dependency-free exhaustive goldens for the canonical Pareto loadout solver and its
current-health/setup-time contracts.

Mechanism: Tiny synthetic inventories are solved both by ParetoLoadoutSolver and direct exhaustive
enumeration.  Tests cover accessory combinations, ordered weapons, complementary thresholds,
same-ID exact copies, deterministic interruption bounds, immutable objective epochs, and max-HP
changes which do not heal current HP.

Invariants: This executable never loads Unity, Assembly-CSharp, a save, or a game process.  Every
cost is an explicit scalar number of seconds and every test lower bound is mathematically
admissible for its synthetic objective.
*/
using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Managers;

internal static class LoadoutSolverTests
{
    private static int _assertions;
    private static long _nextKey = 1L;

    public static int Main()
    {
        try
        {
            TestCanonicalAccessoryCombinations();
            TestJointThresholdAndSameIdCopies();
            TestOrderedWeapons();
            TestSmallExhaustiveGoldens();
            TestBudgetedBoundAndGap();
            TestHealthAndImmutableObjective();
            Console.WriteLine("Loadout solver tests passed: " + _assertions + " assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void TestCanonicalAccessoryCombinations()
    {
        var accessories = new[]
        {
            Candidate(101, LoadoutSlotKind.Accessory, 1.0),
            Candidate(102, LoadoutSlotKind.Accessory, 2.0),
            Candidate(103, LoadoutSlotKind.Accessory, 3.0),
            Candidate(104, LoadoutSlotKind.Accessory, 4.0)
        };
        var result = Solve(accessories, 2, 10000, null,
            delegate(OptimizationObjective objective, LoadoutSelection selection, LoadoutTotals totals)
            {
                return Seconds(100.0 - totals.Metric(0), totals.SetupSeconds);
            },
            delegate(OptimizationObjective objective, LoadoutTotals partial, LoadoutTotals optimistic)
            {
                return 0.0;
            });
        True(result.IsProvenOptimal, "small accessory search is proven exact");
        Equal(6, result.UniqueAccessoryCombinations,
            "four accessories in two slots produce C(4,2), never twelve permutations");
        var ids = result.Selection.Accessories().Select(x => x.ItemId).ToArray();
        Sequence(new[] {103, 104}, ids, "best canonical accessory set is selected");
    }

    private static void TestJointThresholdAndSameIdCopies()
    {
        // Each complementary item is individually below the threshold. A scalar partial-plan beam
        // can discard both; exact branch-and-bound retains their joint completion.
        var accessories = new[]
        {
            Candidate(201, LoadoutSlotKind.Accessory, 6.0, 5.0),
            Candidate(202, LoadoutSlotKind.Accessory, 5.0, 6.0),
            Candidate(203, LoadoutSlotKind.Accessory, 5.0, 5.0),
            Candidate(204, LoadoutSlotKind.Accessory, 5.0, 4.0)
        };
        var result = Solve(accessories, 2, 10000, null,
            delegate(OptimizationObjective objective, LoadoutSelection selection, LoadoutTotals totals)
            {
                var passes = totals.Metric(0) >= 11.0 && totals.Metric(1) >= 11.0;
                return Seconds(passes ? 1.0 : 1000.0, totals.SetupSeconds);
            },
            delegate(OptimizationObjective objective, LoadoutTotals partial, LoadoutTotals optimistic)
            {
                return optimistic.Metric(0) < 11.0 || optimistic.Metric(1) < 11.0
                    ? 1000.0 : partial.SetupSeconds;
            });
        Sequence(new[] {201, 202}, result.Selection.Accessories().Select(x => x.ItemId).ToArray(),
            "joint Attack/Toughness threshold winner survives search");
        Equal(1.0, result.Evaluation.ActionSeconds, "joint threshold gets the one-second outcome");

        var betterCopy = CandidateWithKeys(5001, 5001, 301,
            LoadoutSlotKind.Accessory, new[] {9.0}, 0.0, false);
        var weakerCopy = CandidateWithKeys(5002, 5002, 301,
            LoadoutSlotKind.Accessory, new[] {2.0}, 1.0, false);
        var other = Candidate(302, LoadoutSlotKind.Accessory, 1.0);
        var copies = Solve(new[] {weakerCopy, betterCopy, other}, 1, 1000, null,
            delegate(OptimizationObjective o, LoadoutSelection s, LoadoutTotals t)
            { return Seconds(100.0 - t.Metric(0), t.SetupSeconds); },
            delegate(OptimizationObjective o, LoadoutTotals p, LoadoutTotals optimistic)
            { return 0.0; });
        True(object.ReferenceEquals(betterCopy, copies.Selection.Accessories()[0]),
            "safe same-ID Pareto pruning retains the dominating exact copy");
    }

    private static void TestOrderedWeapons()
    {
        var aPrimary = CandidateWithKeys(6001, 6001, 401,
            LoadoutSlotKind.PrimaryWeapon, new[] {10.0}, 0.0, false);
        var bPrimary = CandidateWithKeys(6002, 6002, 402,
            LoadoutSlotKind.PrimaryWeapon, new[] {6.0}, 0.0, false);
        // Secondary wrappers share the exact reference keys, but include the native slot factor.
        var aSecondary = CandidateWithKeys(6001, 6001, 401,
            LoadoutSlotKind.SecondaryWeapon, new[] {5.0}, 0.0, false);
        var bSecondary = CandidateWithKeys(6002, 6002, 402,
            LoadoutSlotKind.SecondaryWeapon, new[] {3.0}, 0.0, false);
        var problem = Problem(new LoadoutCandidate[0], 0, 1000, null,
            delegate(OptimizationObjective o, LoadoutSelection s, LoadoutTotals t)
            { return Seconds(100.0 - t.Metric(0), t.SetupSeconds); },
            delegate(OptimizationObjective o, LoadoutTotals p, LoadoutTotals optimistic)
            { return 0.0; },
            new[] {aPrimary, bPrimary}, new[] {aSecondary, bSecondary});
        var result = ParetoLoadoutSolver.Solve(problem);
        True(result.Selection.PrimaryWeapon.ItemId == 401
             && result.Selection.SecondaryWeapon.ItemId == 402,
            "primary/secondary reversal is evaluated as an ordered choice");
        Equal(13.0, result.Selection.All().Sum(x => x.Metric(0)),
            "ordered weapon contribution includes the secondary factor exactly once");
    }

    private static void TestSmallExhaustiveGoldens()
    {
        var rng = new Random(180814);
        for (var fixture = 0; fixture < 40; fixture++)
        {
            var accessories = new List<LoadoutCandidate>();
            for (var i = 0; i < 6; i++)
                accessories.Add(Candidate(7000 + fixture * 10 + i,
                    LoadoutSlotKind.Accessory, rng.Next(0, 20), rng.Next(0, 20)));
            CompleteLoadoutEvaluator complete = delegate(
                OptimizationObjective objective, LoadoutSelection selection, LoadoutTotals totals)
            {
                var attack = totals.Metric(0);
                var defense = totals.Metric(1);
                var action = attack >= 20.0 && defense >= 20.0
                    ? 1000.0 / (1.0 + attack + defense)
                    : 5000.0 + (20.0 - Math.Min(20.0, attack))
                      + (20.0 - Math.Min(20.0, defense));
                return Seconds(action, totals.SetupSeconds);
            };
            LoadoutLowerBoundEvaluator bound = delegate(
                OptimizationObjective objective, LoadoutTotals partial, LoadoutTotals optimistic)
            {
                // Zero is deliberately loose but globally admissible. Exhaustion must still prove
                // and match full enumeration; it cannot silently rely on a heuristic partial score.
                return 0.0;
            };
            var result = Solve(accessories.ToArray(), 3, 100000, null, complete, bound);
            var exhaustive = double.PositiveInfinity;
            for (var a = 0; a < accessories.Count; a++)
            for (var b = a + 1; b < accessories.Count; b++)
            for (var d = b + 1; d < accessories.Count; d++)
            {
                var metrics = BaseMetricTotals(2);
                metrics[0] += accessories[a].Metric(0) + accessories[b].Metric(0) + accessories[d].Metric(0);
                metrics[1] += accessories[a].Metric(1) + accessories[b].Metric(1) + accessories[d].Metric(1);
                var action = metrics[0] >= 20.0 && metrics[1] >= 20.0
                    ? 1000.0 / (1.0 + metrics[0] + metrics[1])
                    : 5000.0 + (20.0 - Math.Min(20.0, metrics[0]))
                      + (20.0 - Math.Min(20.0, metrics[1]));
                exhaustive = Math.Min(exhaustive, action);
            }
            True(result.IsProvenOptimal, "random tiny fixture is proven exact " + fixture);
            Near(exhaustive, result.Evaluation.TotalSeconds, 1e-10,
                "branch-and-bound equals exhaustive fixture " + fixture);
            Equal(20, result.UniqueAccessoryCombinations,
                "six choose three canonical completions fixture " + fixture);
        }
    }

    private static void TestBudgetedBoundAndGap()
    {
        var accessories = Enumerable.Range(0, 10)
            .Select(i => Candidate(8000 + i, LoadoutSlotKind.Accessory, i + 1.0)).ToArray();
        var initialAccessories = accessories.Take(4).ToArray();
        var initial = Initial(initialAccessories);
        var result = Solve(accessories, 4, 3, initial,
            delegate(OptimizationObjective o, LoadoutSelection s, LoadoutTotals t)
            { return Seconds(100.0 - t.Metric(0), t.SetupSeconds); },
            delegate(OptimizationObjective o, LoadoutTotals partial, LoadoutTotals optimistic)
            { return Math.Max(0.0, 100.0 - optimistic.Metric(0)); });
        False(result.IsProvenOptimal, "tiny deterministic budget cannot claim exactness");
        True(result.Selection != null && !double.IsInfinity(result.IncumbentSeconds),
            "budgeted search preserves a valid initial incumbent");
        True(result.OptimisticLowerBoundSeconds <= result.IncumbentSeconds,
            "frontier lower bound is no greater than incumbent");
        Near(result.IncumbentSeconds - result.OptimisticLowerBoundSeconds,
            result.AbsoluteGapSeconds, 1e-12, "reported absolute gap is auditable");
        True(result.AbsoluteGapSeconds > 0.0, "interrupted fixture has a nonzero proof gap");
    }

    private static void TestHealthAndImmutableObjective()
    {
        var raised = LoadoutHealth.Project(10.0, 1000.0, 20.0, 5.0);
        Equal(10.0, raised.CurrentHpAfterSwap, "raising max HP does not heal current HP");
        Equal(2.0, raised.RecoverySeconds, "explicit recovery seconds price missing HP");
        var lowered = LoadoutHealth.Project(100.0, 50.0, 40.0, 5.0);
        Equal(50.0, lowered.CurrentHpAfterSwap, "lower max HP conservatively clamps current HP");
        var impossible = LoadoutHealth.Project(10.0, 50.0, 60.0, 5.0);
        False(impossible.Recoverable, "required HP above candidate maximum fails closed");

        var objective = Objective(77L, LoadoutObjectiveKind.MajorUnlock, "major-A");
        var liveSelectorChanged = true;
        var result = Solve(new[] {Candidate(9001, LoadoutSlotKind.Accessory, 1.0)}, 1,
            1000, null,
            delegate(OptimizationObjective fixedObjective, LoadoutSelection s, LoadoutTotals t)
            {
                // A changed external selector must not rewrite the already-bound objective.
                True(liveSelectorChanged, "fixture selector changed during evaluation");
                True(fixedObjective.Kind == LoadoutObjectiveKind.MajorUnlock,
                    "complete evaluation consumes immutable bound kind");
                Equal(77L, fixedObjective.Epoch, "objective epoch remains fixed across candidates");
                return Seconds(1.0, t.SetupSeconds);
            },
            delegate(OptimizationObjective fixedObjective, LoadoutTotals p, LoadoutTotals optimistic)
            {
                True(fixedObjective.Id == "major-A", "bound evaluation consumes fixed target identity");
                return 0.0;
            }, objective);
        True(result.IsProvenOptimal, "immutable-objective fixture completes");
    }

    private static LoadoutSearchResult Solve(LoadoutCandidate[] accessories, int slots,
        int budget, LoadoutSelection initial, CompleteLoadoutEvaluator complete,
        LoadoutLowerBoundEvaluator bound)
    {
        return Solve(accessories, slots, budget, initial, complete, bound,
            Objective(1L, LoadoutObjectiveKind.AdventureProgression, "test"));
    }

    private static LoadoutSearchResult Solve(LoadoutCandidate[] accessories, int slots,
        int budget, LoadoutSelection initial, CompleteLoadoutEvaluator complete,
        LoadoutLowerBoundEvaluator bound, OptimizationObjective objective)
    {
        return ParetoLoadoutSolver.Solve(Problem(accessories, slots, budget,
            initial, complete, bound, null, null, objective));
    }

    private static LoadoutSearchProblem Problem(LoadoutCandidate[] accessories, int slots,
        int budget, LoadoutSelection initial, CompleteLoadoutEvaluator complete,
        LoadoutLowerBoundEvaluator bound, LoadoutCandidate[] primaries,
        LoadoutCandidate[] secondaries, OptimizationObjective objective = null)
    {
        var metricCount = accessories.Length > 0 ? accessories[0].MetricCount
            : primaries != null && primaries.Length > 0 ? primaries[0].MetricCount : 1;
        var head = Candidate(1, LoadoutSlotKind.Head, new double[metricCount]);
        var chest = Candidate(2, LoadoutSlotKind.Chest, new double[metricCount]);
        var legs = Candidate(3, LoadoutSlotKind.Legs, new double[metricCount]);
        var boots = Candidate(4, LoadoutSlotKind.Boots, new double[metricCount]);
        var weapon = primaries == null
            ? new[] {Candidate(5, LoadoutSlotKind.PrimaryWeapon, new double[metricCount])}
            : primaries;
        return new LoadoutSearchProblem(objective ?? Objective(1L,
                LoadoutObjectiveKind.AdventureProgression, "test"),
            new[] {head}, new[] {chest}, new[] {legs}, new[] {boots}, weapon,
            secondaries ?? new LoadoutCandidate[0], accessories, slots, budget,
            complete, bound, initial);
    }

    private static LoadoutSelection Initial(LoadoutCandidate[] accessories)
    {
        var metricCount = accessories[0].MetricCount;
        return new LoadoutSelection(
            Candidate(1, LoadoutSlotKind.Head, new double[metricCount]),
            Candidate(2, LoadoutSlotKind.Chest, new double[metricCount]),
            Candidate(3, LoadoutSlotKind.Legs, new double[metricCount]),
            Candidate(4, LoadoutSlotKind.Boots, new double[metricCount]),
            Candidate(5, LoadoutSlotKind.PrimaryWeapon, new double[metricCount]),
            null, accessories);
    }

    private static double[] BaseMetricTotals(int metricCount)
    {
        return new double[metricCount];
    }

    private static LoadoutEvaluation Seconds(double actionSeconds, double setupSeconds)
    {
        return new LoadoutEvaluation(true, actionSeconds + setupSeconds, setupSeconds,
            0.0, actionSeconds, actionSeconds + setupSeconds, 0.0, "synthetic seconds");
    }

    private static OptimizationObjective Objective(long epoch,
        LoadoutObjectiveKind kind, string id)
    {
        return new OptimizationObjective(id, epoch, kind, id, -1, -1, -1, -1,
            false, false, "test", 0.0, 10.0, 0.0);
    }

    private static LoadoutCandidate Candidate(int id, LoadoutSlotKind slot,
        params double[] metrics)
    {
        return CandidateWithKeys(_nextKey, _nextKey++, id, slot, metrics, 0.0, true);
    }

    private static LoadoutCandidate CandidateWithKeys(long referenceKey, long canonicalKey,
        int id, LoadoutSlotKind slot, double[] metrics, double setup, bool obligation)
    {
        return new LoadoutCandidate(referenceKey, canonicalKey, id, slot,
            metrics, setup, 0L, obligation, new object());
    }

    private static void True(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception(message);
    }

    private static void False(bool condition, string message)
    {
        True(!condition, message);
    }

    private static void Equal(int expected, int actual, string message)
    {
        _assertions++;
        if (expected != actual)
            throw new Exception(message + ": expected " + expected + ", actual " + actual);
    }

    private static void Equal(long expected, long actual, string message)
    {
        _assertions++;
        if (expected != actual)
            throw new Exception(message + ": expected " + expected + ", actual " + actual);
    }

    private static void Equal(double expected, double actual, string message)
    {
        Near(expected, actual, 0.0, message);
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        _assertions++;
        if (Math.Abs(expected - actual) > tolerance)
            throw new Exception(message + ": expected " + expected + ", actual " + actual);
    }

    private static void Sequence(int[] expected, int[] actual, string message)
    {
        _assertions++;
        if (!expected.SequenceEqual(actual))
            throw new Exception(message + ": expected [" + string.Join(",", expected.Select(x => x.ToString()).ToArray())
                                + "], actual [" + string.Join(",", actual.Select(x => x.ToString()).ToArray()) + "]");
    }
}
