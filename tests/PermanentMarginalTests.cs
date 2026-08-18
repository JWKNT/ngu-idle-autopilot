/*
FILE PURPOSE

PermanentMarginalTests is the standalone pure regression suite for task 20. It covers native
MacGuffin discontinuities, Beard reset floors and joint same-resource subsets, Wish native slots
and n^0.49 portfolios, typed NGU/Hack/Wish descriptors, and continuation-attainable Digger max
value. It loads no Unity/game assembly, touches no save, and grants no mutation authority.
*/
using System;
using System.Linq;
using System.Reflection;
using NGUInjector.Autopilot;

internal static class PermanentMarginalTests
{
    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static void Near(double actual, double expected, string message)
    {
        var tolerance = Math.Max(1e-12, Math.Abs(expected) * 1e-11);
        Assert(Math.Abs(actual - expected) <= tolerance,
            message + " (actual " + actual + ", expected " + expected + ")");
    }

    private static void TestMacGuffinBoundariesAndTrueDelta()
    {
        Near(PermanentMarginalOracle.MacGuffinTimeFactor(179.0, true, 1.0), 0.0,
            "179 seconds must remain below the native Guff conversion gate");
        Near(PermanentMarginalOracle.MacGuffinTimeFactor(180.0, true, 1.0), 0.01,
            "180-second Sadistic Guff factor must use the squared curve");
        Near(PermanentMarginalOracle.MacGuffinTimeFactor(900.0, true, 1.0), 0.25,
            "900-second Sadistic Guff factor must not use the former linear shortcut");
        Near(PermanentMarginalOracle.MacGuffinTimeFactor(1799.0, true, 1.0),
            Math.Pow(1799.0 / 1800.0, 2.0),
            "1799-second Guff factor must remain on the squared segment");
        Near(PermanentMarginalOracle.MacGuffinTimeFactor(1800.0, true, 1.0), 1.0,
            "1800 seconds must join the Sadistic square and linear segments exactly");
        Near(PermanentMarginalOracle.MacGuffinTimeFactor(86400.0, true, 1.0), 48.0,
            "one-day enhanced Sadistic factor must equal 48");
        Near(PermanentMarginalOracle.MacGuffinTimeFactor(86400.0, false, 1.0),
            Math.Sqrt(48.0),
            "ordinary one-day Guff factor must remain on the square-root curve");
        Near(PermanentMarginalOracle.MacGuffinTimeFactor(900.0, false, 2.0), 0.5,
            "booster multiplication must occur after the native time curve");

        var conversion = PermanentMarginalOracle.EvaluateMacGuffinBank(
            new MacGuffinConversionInput
            {
                ItemId = 198,
                ItemLevel = 0,
                EffectTarget = PermanentEffectTarget.Resource,
                HighLevelExponent = 0.3,
                HighLevelScale = 25.12,
                PersistentAccumulatorBefore = 1.0
            }, 900.0, true, 1.0);
        Near(conversion.AccumulatorDelta, 0.00001 * 0.25,
            "Guff reset must add the true accumulator delta, not an invented item level");
        Near(conversion.TimeFactor, 0.25,
            "Guff bank result must retain its exact native factor provenance");
        Near(PermanentMarginalOracle.MacGuffinGainAtUnitTime(198, 100),
            Math.Pow(101.0, 0.3) * 25.12e-5,
            "Energy Power Guff must use its native post-100 diminishing curve");
        Near(PermanentMarginalOracle.MacGuffinGainAtUnitTime(209, 100),
            101.0 * 5e-5,
            "Gold Guff remains linear above level 100");
        var guffIds = new[] {198, 199, 200, 201, 202, 203, 204, 205, 206, 207, 208,
            209, 210, 211, 228, 250, 289, 290, 291, 298, 299, 300};
        MacGuffinCurve curve;
        Assert(guffIds.All(x => PermanentMarginalOracle.TryGetMacGuffinCurve(x, out curve)),
            "every native applyAllMacguffinBonuses item type needs a typed curve");
        PermanentMarginalOracle.TryGetMacGuffinCurve(198, out curve);
        Assert(curve.EffectTarget == PermanentEffectTarget.EnergyPower,
            "Guff curves must expose typed effect targets rather than item-name parsing");
    }

    private static void TestBeardFloorsAndJointSubsets()
    {
        Assert(PermanentMarginalOracle.BeardBankDelta(1000000L, 3599.0, 0L) == 0L,
            "a pre-hour Beard reset must bank exactly zero");
        Assert(PermanentMarginalOracle.BeardBankDelta(8L, 3600.0, 0L) == 0L,
            "the Beard square-root floor must permit an exact zero bank");
        Assert(PermanentMarginalOracle.BeardBankDelta(9L, 3600.0, 0L) == 1L,
            "the first Beard floor boundary at one hour is 9 temporary levels");
        Assert(PermanentMarginalOracle.BeardBankDelta(9L, 10800.0, 0L) == 3L,
            "three-hour Beard factor must bank floor(sqrt(level))");
        Assert(PermanentMarginalOracle.BeardBankDelta(9L, 86400.0, 0L) == 9L,
            "native added-trimmings must cap at the temporary level");
        Near(PermanentMarginalOracle.BeardCountDivider(1, true), 1.0,
            "Beardverse must not reduce a single Beard below divisor one");
        Near(PermanentMarginalOracle.BeardCountDivider(2, false), 2.0,
            "two same-resource Beards use the exact ordinary divisor");
        Near(PermanentMarginalOracle.BeardCountDivider(2, true), 1.8,
            "Beardverse applies its native 0.9 reduction to counts of two or more");

        // Each candidate starts just below the 9-level bank discontinuity. Two Energy Beards
        // divide each other and miss it; one Energy plus one Magic do not divide each other.
        var choice = PermanentMarginalOracle.SelectBeardSubset(new[]
        {
            new BeardMarginalInput {Id = 0, UsesEnergy = true, TemporaryLevel = 8,
                Progress = 0.85, BaseProgressPerTick = 1.5,
                ValuePerBankedTrimming = 1.0},
            new BeardMarginalInput {Id = 1, UsesEnergy = true, TemporaryLevel = 8,
                Progress = 0.85, BaseProgressPerTick = 1.5,
                ValuePerBankedTrimming = 1.0},
            new BeardMarginalInput {Id = 2, UsesEnergy = false, TemporaryLevel = 8,
                Progress = 0.85, BaseProgressPerTick = 1.5,
                ValuePerBankedTrimming = 1.0}
        }, 2, 0.02, 10800.0, 0L, false);
        Assert(choice.FinalActiveIds.SequenceEqual(new[] {0, 2}),
            "joint Beard enumeration must choose different resources at the floor boundary");
        Assert(choice.TotalBankDelta == 6L,
            "joint Beard choice must score exact final active-set trimmings");
        Assert(choice.Projections.Single(x => x.Id == 0).GroupDivider == 1.0
               && choice.Projections.Single(x => x.Id == 2).GroupDivider == 1.0,
            "Energy and Magic Beards must never divide one another");
    }

    private static WishMarginalInput Wish(int id, bool binary)
    {
        return new WishMarginalInput
        {
            Id = id,
            Eligible = true,
            BinaryDependency = binary,
            Dependency = binary ? PermanentDependencyKind.EndWish
                : PermanentDependencyKind.None,
            EffectTarget = binary ? PermanentEffectTarget.Terminal
                : PermanentEffectTarget.Resource,
            EffectBefore = 1.0,
            EffectAfter = binary ? 1.0 : 1.01,
            ProgressCoefficient = 1e-4,
            MinimumTimeProgressPerTick = double.PositiveInfinity
        };
    }

    private static void TestWishEffectsSlotsAndPortfolios()
    {
        Near(PermanentMarginalOracle.WishEffect(10L, 0.02, true), 1.2,
            "native Wish effect is 1 + level * serialized effect");
        Near(PermanentMarginalOracle.WishEffect(10L, 0.02, false), 1.0,
            "unavailable-difficulty Wish effect must be neutral");
        Assert(PermanentMarginalOracle.NativeWishSlots(false, false, false) == 1,
            "base native Wish slot count is one");
        Assert(PermanentMarginalOracle.NativeWishSlots(true, true, true) == 4,
            "Evil Troll, Pink Heart, and Quirk 56 must cap native slots at four");

        var budgets = new PermanentResourceVector {Energy = 400, Magic = 400, Res3 = 400};
        var split = PermanentMarginalOracle.PlanWishPortfolio(new[]
        {
            Wish(10, false), Wish(11, false), Wish(12, false), Wish(13, false)
        }, 4, budgets);
        Assert(split.DistinctNativeSlots == 4 && split.Allocations.Length == 4,
            "ordinary Wish portfolio must use four distinct native records");
        Assert(split.Allocations.All(x => x.Resources.Energy == 100
                                          && x.Resources.Magic == 100
                                          && x.Resources.Res3 == 100),
            "identical ordinary Wishes must receive an equal three-resource split");
        Near(PermanentMarginalOracle.WishEqualSplitThroughputFactor(4),
            Math.Pow(4.0, 0.49), "Wish split throughput must scale as n^0.49");
        var singleRate = PermanentMarginalOracle.WishRawProgressPerTick(1e-4, budgets);
        Near(split.Allocations.Sum(x => x.ProgressPerTick) / singleRate,
            Math.Pow(4.0, 0.49),
            "the actual four-Wish resource split must realize n^0.49 throughput");
        var large = PermanentMarginalOracle.PlanWishPortfolio(new[]
        {
            Wish(20, false), Wish(21, false), Wish(22, false), Wish(23, false)
        }, 4, new PermanentResourceVector
        {
            Energy = 9000000000000000000L,
            Magic = 8999999999999999999L,
            Res3 = 8999999999999999998L
        });
        Assert(large.Allocations.Sum(x => (decimal)x.Resources.Energy)
               == 9000000000000000000m
               && large.Allocations.Sum(x => (decimal)x.Resources.Magic)
               == 8999999999999999999m
               && large.Allocations.Sum(x => (decimal)x.Resources.Res3)
               == 8999999999999999998m,
            "Wish KKT integer shares must conserve all three Int64 budgets exactly");

        var concentrated = PermanentMarginalOracle.PlanWishPortfolio(new[]
        {
            Wish(203, true), Wish(10, false), Wish(11, false), Wish(12, false)
        }, 4, budgets);
        Assert(concentrated.BinaryConcentrated && concentrated.Allocations.Length == 1,
            "a proven binary Wish dependency must concentrate into one coherent vector");
        Assert(concentrated.Allocations[0].WishId == 203
               && concentrated.Allocations[0].Resources.Energy == 400
               && concentrated.Allocations[0].Resources.Magic == 400
               && concentrated.Allocations[0].Resources.Res3 == 400,
            "binary concentration must assign every resource once to the critical ID");
        Assert(PermanentMarginalOracle.CountAllocatedWishSlots(new[]
        {
            concentrated.Allocations[0], concentrated.Allocations[0]
        }) == 1, "repeated logical references to one Wish consume one native slot");
    }

    private static void TestTypedDescriptorsAndDiggerContinuation()
    {
        var ngu = PermanentMarginalOracle.DescribeNgu(4, PermanentTrackKind.Sadistic,
            true, 123L, 2.0, PermanentEffectTarget.Adventure, 10.0, 11.0,
            PermanentDependencyKind.None);
        var hack = PermanentMarginalOracle.DescribeHack(15, 456L, 0.02,
            PermanentEffectTarget.Terminal, 1.0, 1.0, true);
        var wishInput = Wish(203, true);
        var wish = PermanentMarginalOracle.DescribeWish(wishInput,
            new PermanentResourceVector {Energy = 1, Magic = 2, Res3 = 3}, 1.0);
        Assert(ngu.System == PermanentSystemKind.Ngu && ngu.Track == PermanentTrackKind.Sadistic
               && ngu.Resources.Energy == 123L && ngu.NativeSlotFootprint == 0,
            "NGU marginal action must retain typed track/resource facts");
        Assert(hack.System == PermanentSystemKind.Hack && hack.Id == 15
               && hack.Dependency == PermanentDependencyKind.EndHack
               && hack.Completion == PermanentCompletionKind.BinaryDependency,
            "terminal Hack action must be a typed one-level dependency");
        Assert(wish.System == PermanentSystemKind.Wish && wish.NativeSlotFootprint == 1
               && wish.Dependency == PermanentDependencyKind.EndWish,
            "Wish marginal action must carry its distinct-record slot footprint");

        var descriptorStrings = typeof(PermanentActionDescriptor)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(x => x.FieldType == typeof(string)).ToArray();
        Assert(descriptorStrings.Length == 0,
            "permanent action economics must not accept prose-string weights");

        var cannotReachExtra = PermanentMarginalOracle.EvaluateDiggerMaxContinuation(
            new DiggerMaxContinuationInput
            {
                DiggerId = 2, CurrentLevel = 8, MaxLevelBefore = 10, MaxLevelAfter = 11,
                ContinuationAttainableLevel = 10, ContinuationHasSlot = true,
                CubicDirectBonus = true, BoostPerLevel = 0.001,
                TotalMaxBonusBefore = 1.25, TotalMaxBonusAfter = 1.2505,
                GoldCost = 100.0
            });
        Assert(!cannotReachExtra.ExtraCurrentLevelAttainable
               && cannotReachExtra.DirectDeltaLog == 0.0
               && cannotReachExtra.GlobalDeltaLog > 0.0,
            "Digger max purchase must credit only global value when continuation cannot use level 11");
        var canReachExtra = PermanentMarginalOracle.EvaluateDiggerMaxContinuation(
            new DiggerMaxContinuationInput
            {
                DiggerId = 2, CurrentLevel = 10, MaxLevelBefore = 10, MaxLevelAfter = 11,
                ContinuationAttainableLevel = 11, ContinuationHasSlot = true,
                CubicDirectBonus = true, BoostPerLevel = 0.001,
                TotalMaxBonusBefore = 1.25, TotalMaxBonusAfter = 1.2505,
                GoldCost = 100.0
            });
        Assert(canReachExtra.ExtraCurrentLevelAttainable && canReachExtra.DirectDeltaLog > 0.0,
            "Digger direct max value requires a continuation-attainable extra current level");
    }

    public static int Main()
    {
        TestMacGuffinBoundariesAndTrueDelta();
        TestBeardFloorsAndJointSubsets();
        TestWishEffectsSlotsAndPortfolios();
        TestTypedDescriptorsAndDiggerContinuation();
        Console.WriteLine("Permanent marginal assertions passed: " + _assertions);
        return 0;
    }
}
