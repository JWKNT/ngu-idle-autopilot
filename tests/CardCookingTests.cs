using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

Purpose: Regression-test the pure Cards/Cooking controller contracts reconciled in audit task 25.

Mechanism: Controller-free fixtures exercise the six-coordinate END reserve and portfolio, exact
live/offline Mayo arithmetic, task-6 deck slack/service, task-8 duplicate-stop handoff, task-24 END
forecast, source-exact Cooking cross-local/pair optimization, applied cap, and equipment quirks.

Inputs and outputs: Deterministic in-memory candidates, inventory topologies, and ingredient models
produce immutable plans. Assertion diagnostics are the only output.

Invariants and safety: This executable never creates Character/controllers, consumes a Card/meal,
changes filters, loads a save, injects a DLL, or inspects/steers Unity RNG. Filter tests prove only
that the handoff snapshots all bits and admits task 9 to override/restore them transactionally.
*/
internal static class CardCookingTests
{
    private static int _assertions;

    private static void Assert(bool value, string message)
    {
        _assertions++;
        if (!value) throw new Exception("FAIL: " + message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        _assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception("FAIL: " + message + " expected=" + expected + " actual=" + actual);
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        _assertions++;
        if (Math.Abs(expected - actual) > tolerance)
            throw new Exception("FAIL: " + message + " expected=" + expected + " actual=" + actual);
    }

    private static int[] Six(int value)
    {
        return Enumerable.Repeat(value, CardCookingMechanics.MayoCurrencyCount).ToArray();
    }

    private static int[] Cost(int red, int green = 0, int blue = 0,
        int white = 0, int black = 0, int beige = 0)
    {
        return new[] {red, green, blue, white, black, beige};
    }

    private static OrdinaryInventoryTopology Topology(int slots, int usableStart,
        params int[] occupiedSlots)
    {
        var ids = new int[slots];
        var identities = new object[slots];
        foreach (var slot in occupiedSlots)
        {
            ids[slot] = 1000 + slot;
            identities[slot] = new object();
        }
        return PhysicalTopology.CaptureOrdinary(ids, identities, slots, usableStart);
    }

    private static CardPortfolioCandidate Candidate(int id, double value, int[] cost,
        HeldCardKind kind = HeldCardKind.Normal, bool isProtected = false)
    {
        return new CardPortfolioCandidate(id, value, cost, kind, isProtected);
    }

    private static void TestEndReserveAndSixCurrencyPortfolio()
    {
        Assert(CardCookingMechanics.EndMayoReserve(true).All(x => x == 99),
            "missing ordinary item 492 reserves exactly 99 of each of six Mayos");
        Assert(CardCookingMechanics.EndMayoReserve(false).All(x => x == 0),
            "terminal ordinary ownership releases the reserve");
        Assert(!CardCookingMechanics.CanSpendWithoutBreakingReserve(Six(100), Cost(2), Six(99)),
            "a positive-value cast cannot borrow one unit from END reserve");
        Assert(CardCookingMechanics.CanSpendWithoutBreakingReserve(Six(98), Cost(0), Six(99)),
            "a zero-cost action does not worsen an existing END reserve deficit");

        var candidates = new[]
        {
            Candidate(1, 5.0, Cost(2)),
            Candidate(2, 5.0, Cost(0, 2)),
            Candidate(3, 9.0, Cost(2, 2)),
            Candidate(4, 100.0, Cost(3)),
            Candidate(5, 1000.0, Cost(0, 0, 0, 0, 0, 1), HeldCardKind.End),
            Candidate(6, 1000.0, Cost(1), HeldCardKind.Normal, true)
        };
        var plan = CardCookingMechanics.SolveCardPortfolio(candidates, Six(101), Six(99));
        Assert(plan.Exact, "small componentwise label frontier is exact");
        Assert(plan.SelectedIds.SequenceEqual(new[] {1, 2}),
            "componentwise optimizer chooses two disjoint-currency cards over lower joint value");
        Near(10.0, plan.Value, 1e-12, "portfolio value");
        Assert(plan.Spent.SequenceEqual(Cost(2, 2)), "six-coordinate spend is retained");
        Assert(plan.Reserve.SequenceEqual(Six(99)), "portfolio publishes its protected reserve");

        var wrongCurrency = CardCookingMechanics.SolveCardPortfolio(
            new[] {Candidate(7, 50.0, Cost(0, 10))}, Cost(109, 99, 99, 99, 99, 99), Six(99));
        Equal(0, wrongCurrency.SelectedIds.Length,
            "surplus red Mayo cannot pay a green Mayo cost despite equal aggregate balance");

        var bounded = CardCookingMechanics.SolveCardPortfolio(candidates, Six(200), Six(0), 1);
        Assert(!bounded.Exact, "declared label bound is exposed instead of silently claiming exactness");
    }

    private static void TestMayoQuantizationAndGeneratorPortfolio()
    {
        Near(50.0, CardCookingMechanics.LiveMayoAggregateRate(400000.0, 1), 1e-12,
            "one live generator has the native 50-per-second tick cap");
        Near(100.0, CardCookingMechanics.LiveMayoAggregateRate(400000.0, 2), 1e-12,
            "two live generators lift the aggregate cap to 100 per second");
        Near(1.0, CardCookingMechanics.LiveMayoAggregateRate(3600.0, 1), 1e-12,
            "ordinary native speed yields one Mayo per second");

        var live = CardCookingMechanics.AdvanceLiveMayo(0, 0.0, 400000.0, 1, 10);
        Equal(10L, live.Awarded, "live path awards at most one integer per currency per tick");
        Near(0.0, live.Progress, 1e-12, "live completion resets progress and discards overshoot");

        var offline = CardCookingMechanics.AdvanceOfflineMayo(0, 0.0, 400000.0, 1, 1.0);
        Equal(111L, offline.Awarded, "offline big-progress path may exceed the live tick cap");
        Near(1.0 / 9.0, offline.Progress, 1e-10,
            "offline big-progress floors all wholes and retains modulo-one progress");

        Near(.04, CardCookingMechanics.LiveMayoSecondsToNextInteger(9000.0, 1, .9), 1e-12,
            "existing fractional progress participates in the next live completion");

        var highSpeed = CardCookingMechanics.ChooseMayoGenerators(400000.0, 2,
            new[] {10.0, 10.0, 0.0, 0.0, 0.0, 0.0}, new double[6]);
        Assert(highSpeed.CurrencyIds.SequenceEqual(new[] {0, 1}),
            "generator scheduler diversifies when extra active count lifts quantized throughput");
        Near(100.0, highSpeed.AggregateRatePerSecond, 1e-12,
            "generator plan publishes exact live aggregate tick rate");

        var shadows = CardCookingMechanics.DiscreteMayoShadowValues(
            new CardPortfolioCandidate[0], Cost(98, 99, 99, 99, 99, 99), Six(99));
        Assert(shadows[0] > 1000000.0 && shadows.Skip(1).All(x => x < 1.0),
            "END deficit is lexicographically ahead of ordinary marginal portfolio value");
    }

    private static void TestDeckSlackAndFoilChonkerService()
    {
        Equal(2, CardCookingMechanics.RequiredLiveDeckSlack(true, false, false),
            "Sadistic normal frame reserves normal plus possible protected END delivery");
        Equal(3, CardCookingMechanics.RequiredLiveDeckSlack(true, false, true),
            "simultaneously due Chonker raises live normal-first slack to three");
        Equal(1, CardCookingMechanics.RequiredLiveDeckSlack(true, true, false),
            "secured END reduces ordinary live slack to one");
        Equal(2, CardCookingMechanics.RequiredLiveDeckSlack(true, true, true),
            "secured END plus due Chonker needs two slots");

        var service = new[]
        {
            new DeckServiceCandidate(1, .01, 0.0, true, false, HeldCardKind.Normal),
            new DeckServiceCandidate(2, 5.0, .2, false, false, HeldCardKind.Foil),
            new DeckServiceCandidate(3, 6.0, .5, false, false, HeldCardKind.BigChonker),
            new DeckServiceCandidate(4, 0.0, 99.0, false, true, HeldCardKind.Normal),
            new DeckServiceCandidate(5, 0.0, 99.0, false, false, HeldCardKind.End)
        };
        var plan = CardCookingMechanics.PlanDeckService(service, 10, 10, 3, true, false);
        Assert(plan.Admitted && plan.Actions.Length == 3,
            "one proactive pass establishes all required slack before the native frame");
        Assert(plan.Actions[0].Kind == DeckServiceActionKind.Cast
               && plan.Actions[0].CandidateId == 1,
            "every affordable positive Card is cast without rarity/tier gating");
        Assert(plan.Actions.Any(x => x.CandidateId == 2 && x.Kind == DeckServiceActionKind.Recycle)
               && plan.Actions.Any(x => x.CandidateId == 3 && x.Kind == DeckServiceActionKind.Recycle),
            "foil and Chonker cannot deadlock deck reclamation");
        Assert(plan.Actions.All(x => x.CandidateId != 4 && x.CandidateId != 5),
            "protected Cards and a unique END remain unavailable to reclamation");

        var redundant = CardCookingMechanics.PlanDeckService(
            new[] {new DeckServiceCandidate(5, 0.0, 0.0, false, true, HeldCardKind.End)},
            10, 10, 1, true, true);
        Assert(redundant.Admitted && redundant.Actions.Single().CandidateId == 5,
            "even protected END recycling is unlocked only after physical ownership proves redundancy");
    }

    private static void TestFilterSafeEndHandoffAndDuplicateStop()
    {
        var filtersOn = new EndCardFilterSnapshot(true, true, true, true, true);
        var ready = CardCookingMechanics.EvaluateEndCardHandoff(false, false, 1, Six(99),
            Topology(3, 2), filtersOn);
        Assert(ready.ReadyForTerminalTransaction && !ready.StopDuplicateConsume,
            "active individual/broad filters do not block a handoff whose exact states are snapshotted");
        Assert(ready.Filters.ItemFiltered && ready.Filters.LootFilter && ready.Filters.FilterOn
               && ready.Filters.FilterMisc,
            "handoff preserves every individual and broad filter bit for task-9 restoration");
        Assert(ready.InventoryCapacity.Admitted,
            "task-6 exact unique-delivery proof admits one usable ordinary slot");

        var deficitAmounts = Six(99);
        deficitAmounts[4] = 98;
        var deficit = CardCookingMechanics.EvaluateEndCardHandoff(false, false, 1, deficitAmounts,
            Topology(3, 2), filtersOn);
        Assert(!deficit.ReadyForTerminalTransaction && deficit.MayoDeficits[4] == 1,
            "98 in any one Mayo rejects END handoff without collapsing currencies");

        var unknownFilters = CardCookingMechanics.EvaluateEndCardHandoff(false, false, 1, Six(99),
            Topology(3, 2), new EndCardFilterSnapshot(false, false, false, false, false));
        Assert(!unknownFilters.ReadyForTerminalTransaction,
            "unknown filter state fails closed before terminal Card conversion");

        var terminal = CardCookingMechanics.EvaluateEndCardHandoff(true, true, 2, Six(999),
            Topology(3, 2), filtersOn);
        Assert(terminal.StopDuplicateConsume && !terminal.ReadyForTerminalTransaction,
            "terminal ordinary ownership forbids duplicate END conversion");
        var recoverable = CardCookingMechanics.EvaluateEndCardHandoff(false, true, 1, Six(999),
            Topology(3, 2), filtersOn);
        Assert(recoverable.StopDuplicateConsume && !recoverable.ReadyForTerminalTransaction,
            "Daycare/equipment/recoverable physical copy also stops destructive duplicate conversion");

        var noCapacity = CardCookingMechanics.EvaluateEndCardHandoff(false, false, 1, Six(99),
            Topology(2, 1, 1), filtersOn);
        Assert(!noCapacity.ReadyForTerminalTransaction && !noCapacity.InventoryCapacity.Admitted,
            "END handoff requires task-6 usable ordinary capacity, not raw list length");
    }

    private static void TestEndForecastUsesStableStochasticKernel()
    {
        var forecast = CardCookingMechanics.EndRollForecast("audited-hash");
        Near(100.0, forecast.MeanNormalRolls, 1e-12, "1% END geometric mean");
        Equal(69L, forecast.MedianNormalRolls, "1% END geometric median");
        Equal(230L, forecast.P90NormalRolls, "1% END geometric P90");
        Equal(299L, forecast.P95NormalRolls, "1% END geometric P95");
        Equal(459L, forecast.P99NormalRolls, "1% END geometric P99");
        Equal(ForecastEvidenceGrade.SourceExact, forecast.Evidence.Grade,
            "END forecast identifies source-exact evidence");
        Assert(forecast.Evidence.Notes.IndexOf("No RNG", StringComparison.Ordinal) >= 0,
            "forecast explicitly keeps RNG-aware steering off");
    }

    private static CookingIngredientModel Ingredient(int current, int target, double weight,
        double pairedWeight, bool unlocked)
    {
        return new CookingIngredientModel(current, target, weight, pairedWeight, unlocked);
    }

    private static void TestCookingSourceExactPairOptimizer()
    {
        var exact = Ingredient(7, 7, 10.0, 20.0, true);
        Near(10.0, CardCookingMechanics.CookingLocalScore(exact, 7), 1e-12,
            "local score equals weight at exact target");
        Near(20.0, CardCookingMechanics.CookingPairBonus(exact, 14, 7, 7), 1e-12,
            "pair score uses first member paired weight at exact sum target");

        var first = Ingredient(0, 0, 4.0, 8.0, true);
        var lockedPartner = Ingredient(7, 20, 14.0, 30.0, false);
        var cross = CardCookingMechanics.OptimizeCookingPair(first, lockedPartner, 5, 20);
        Equal(20, cross.FirstLevel,
            "unlocked A receives both local(A,a) and local(B,a), including locked partner local curve");
        Equal(7, cross.SecondLevel, "locked partner level is honored exactly");

        var bothLocked = CardCookingMechanics.OptimizeCookingPair(
            Ingredient(3, 0, 4, 8, false), Ingredient(11, 20, 14, 30, false), 14, 20);
        Equal(3, bothLocked.FirstLevel, "locked first ingredient cannot move");
        Equal(11, bothLocked.SecondLevel, "locked second ingredient cannot move");
        Near(0.0, bothLocked.Score, 1e-12, "a fully locked pair contributes no native score");

        var random = new Random(2501);
        for (var trial = 0; trial < 100; trial++)
        {
            var a = Ingredient(random.Next(21), random.Next(21), 4 + random.NextDouble() * 10,
                8 + random.NextDouble() * 22, random.Next(2) == 1);
            var b = Ingredient(random.Next(21), random.Next(21), 4 + random.NextDouble() * 10,
                8 + random.NextDouble() * 22, random.Next(2) == 1);
            var target = random.Next(5, 35);
            var optimized = CardCookingMechanics.OptimizeCookingPair(a, b, target, 20);
            var brute = double.NegativeInfinity;
            for (var x = a.Unlocked ? 0 : a.CurrentLevel; x <= (a.Unlocked ? 20 : a.CurrentLevel); x++)
                for (var y = b.Unlocked ? 0 : b.CurrentLevel; y <= (b.Unlocked ? 20 : b.CurrentLevel); y++)
                    brute = Math.Max(brute, CardCookingMechanics.CookingPairScore(a, b, target, x, y));
            Near(brute, optimized.Score, 1e-10,
                "pure disjoint-pair optimizer matches exhaustive source score trial " + trial);
        }
    }

    private static void TestCookingCapAndEquipmentQuirks()
    {
        Equal(2, CardCookingMechanics.CookingAffixEffectiveCount(CookingEquipmentSlot.Legs, 1),
            "each leg Cooking affix is counted twice by native source");
        Equal(0, CardCookingMechanics.CookingAffixEffectiveCount(CookingEquipmentSlot.Weapon2, 3),
            "weapon 2 Cooking affixes are never checked by native source");
        Equal(3, CardCookingMechanics.CookingAffixEffectiveCount(CookingEquipmentSlot.Accessory, 3),
            "checked equipment slots count each of three affixes once");
        Near(Math.Pow(1.03, 13), CardCookingMechanics.CookingEquipmentMultiplier(13), 1e-12,
            "thirteen effective affixes remain below equipment cap");
        Near(1.5, CardCookingMechanics.CookingEquipmentMultiplier(14), 1e-12,
            "fourteen effective affixes clamp to native x1.5 cap");
        Assert(CardCookingMechanics.ShouldConsumeCookingMeal(2.999999),
            "meal that may cross applied cap still has positive marginal gain");
        Assert(!CardCookingMechanics.ShouldConsumeCookingMeal(3.0)
               && !CardCookingMechanics.ShouldConsumeCookingMeal(3.5),
            "no second banked meal is consumed at/above applied multiplier cap 3");
    }

    public static int Main()
    {
        try
        {
            TestEndReserveAndSixCurrencyPortfolio();
            TestMayoQuantizationAndGeneratorPortfolio();
            TestDeckSlackAndFoilChonkerService();
            TestFilterSafeEndHandoffAndDuplicateStop();
            TestEndForecastUsesStableStochasticKernel();
            TestCookingSourceExactPairOptimizer();
            TestCookingCapAndEquipmentQuirks();
            Console.WriteLine("Card/Cooking tests passed: " + _assertions + " assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
