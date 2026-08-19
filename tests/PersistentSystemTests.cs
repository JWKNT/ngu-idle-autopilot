using System;
using System.Collections.Generic;
using System.IO;
using NGUInjector.Autopilot;
using NGUInjector.Managers;

/*
FILE PURPOSE

Purpose: PersistentSystemTests regression-checks the source-exact pure boundaries and live wiring
shape for Daycare, Beards, Diggers, Wandoos OS, Money Pit, Daily Spin, Augments, and Yggdrasil.

Mechanism: Pure assertions exercise native unlock/float-boundary arithmetic and immutable copied-
state settlement for both stochastic reward paths. Read-only source checks require each persistent
mutation to be a caller-root child with an explicit postcondition, and guard against reintroducing
known direct/unverified mutations and arbitrary valuation factors.

Inputs and outputs: Inputs are in-memory fixtures and maintained source text. Output is an assertion
count/process status. The suite never instantiates Character, loads a save, invokes a controller,
starts the bot, or writes runtime/configuration state.

Invariants and safety: The 1e13 cumulative Pit branch is represented as a flag-only tier, not the
Wish-gated 1e50 one-toss reward. Wandoos locked targets fail closed. Ygg tier arithmetic preserves
the native float32 intermediate. Structural checks are supplementary to full-source type checking.

Extension points and non-goals: These fixtures inject copied-state faults without constructing live
Unity controllers. A real copied-save differential remains an integration concern.
*/
internal static class PersistentSystemTests
{
    private struct FakeRandomState
    {
        private int s0;
        private int s1;

        internal FakeRandomState(int first, int second)
        {
            s0 = first;
            s1 = second;
        }
    }

    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        _assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception("FAIL: " + message + " expected=" + expected
                                + " actual=" + actual);
    }

    private static void TestWandoosUnlocks()
    {
        Assert(WandoosRunManager.IsTargetUnlocked(0, false, 0),
            "Wandoos 98 is the installed base target");
        Assert(!WandoosRunManager.IsTargetUnlocked(1, false, 100),
            "MEH requires Jake regardless of XL state");
        Assert(WandoosRunManager.IsTargetUnlocked(1, true, 0),
            "Jake unlocks MEH");
        Assert(!WandoosRunManager.IsTargetUnlocked(2, true, 0),
            "XL requires a positive installed XL level");
        Assert(WandoosRunManager.IsTargetUnlocked(2, false, 1),
            "a positive XL level unlocks XL");
        Assert(!WandoosRunManager.IsTargetUnlocked(-1, true, 1)
               && !WandoosRunManager.IsTargetUnlocked(3, true, 1),
            "out-of-range OS selectors fail closed");
    }

    private static void TestPitCumulativeOrder()
    {
        var at1e8 = GoldMechanics.SafePitThreshold(1e8);
        var at1e10 = GoldMechanics.SafePitThreshold(1e10);
        var at1e11 = GoldMechanics.SafePitThreshold(1e11);
        var at1e12 = GoldMechanics.SafePitThreshold(1e12);
        var at1e13 = GoldMechanics.SafePitThreshold(1e13);
        Equal(1, MoneyPitManager.NextCumulativeTierIndex(false, false, false, false, false,
            at1e13), "a huge toss still awards only earliest unclaimed cumulative tier");
        Equal(2, MoneyPitManager.NextCumulativeTierIndex(true, false, false, false, false,
            at1e10), "1e10 cumulative branch follows tier one");
        Equal(3, MoneyPitManager.NextCumulativeTierIndex(true, true, false, false, false,
            at1e11), "1e11 cumulative branch is Looty");
        Equal(4, MoneyPitManager.NextCumulativeTierIndex(true, true, true, false, false,
            at1e12), "1e12 cumulative branch is 100 EXP");
        Equal(5, MoneyPitManager.NextCumulativeTierIndex(true, true, true, true, false,
            at1e13), "1e13 cumulative branch is the native flag-only fifth tier");
        Equal(0, MoneyPitManager.NextCumulativeTierIndex(true, true, true, true, true,
            1e50), "1e50 is not another cumulative permanent tier");
        Equal(1, MoneyPitManager.NextCumulativeTierIndex(false, false, false, false, false,
            at1e8), "float-safe 1e8 margin reaches strict native threshold");
    }

    private static void TestMoneyPitCopiedStateSettlement()
    {
        var threshold = GoldMechanics.SafePitThreshold(1e8);
        var before = new MoneyPitTransitionSnapshot(7200.0, threshold, 0.0, 9L,
            false, false, false, false, false, 0);
        var committed = new MoneyPitTransitionSnapshot(0.0, 0.0, threshold, 10L,
            true, false, false, false, false, 0);
        int tier;
        string reason;
        Assert(MoneyPitTransitionProof.Verify(before, committed, out tier, out reason)
               && tier == 1,
            "full copied state settles even when the native caller reported an exception");
        var partialDebit = new MoneyPitTransitionSnapshot(0.0, 0.0, threshold, 9L,
            true, false, false, false, false, 0);
        Assert(!MoneyPitTransitionProof.Verify(before, partialDebit, out tier, out reason),
            "Gold debit without toss-count settlement is quarantined");
        var staleTimer = new MoneyPitTransitionSnapshot(1.0, 0.0, threshold, 10L,
            true, false, false, false, false, 0);
        Assert(!MoneyPitTransitionProof.Verify(before, staleTimer, out tier, out reason),
            "Money Pit requires the source-exact timer reset to zero");

        var lootyGold = GoldMechanics.SafePitThreshold(1e11);
        var beforeLooty = new MoneyPitTransitionSnapshot(1.0, lootyGold, 0.0, 2L,
            true, true, false, false, false, 4);
        var withLooty = new MoneyPitTransitionSnapshot(0.0, 0.0, lootyGold, 3L,
            true, true, true, false, false, 5);
        Assert(MoneyPitTransitionProof.Verify(beforeLooty, withLooty, out tier, out reason)
               && tier == 3,
            "Looty cumulative tier requires exactly one physical item-67 delivery");
        var missingLooty = new MoneyPitTransitionSnapshot(0.0, 0.0, lootyGold, 3L,
            true, true, true, false, false, 4);
        Assert(!MoneyPitTransitionProof.Verify(beforeLooty, missingLooty, out tier, out reason),
            "tier-three flag without Looty delivery is rejected");
    }

    private static void TestDailySpinCopiedStateSettlement()
    {
        string reason;
        var freeBefore = new DailySpinTransitionSnapshot(123.0, 2L, 7L, "rng-a");
        var freeAfter = new DailySpinTransitionSnapshot(123.0, 1L, 8L, "rng-b");
        Assert(DailySpinTransitionProof.Verify(freeBefore, freeAfter, out reason),
            "free-spin path proves exact debit, count, and RNG advance");
        var timerBefore = new DailySpinTransitionSnapshot(86400.0, 0L, 8L, "rng-b");
        var timerAfter = new DailySpinTransitionSnapshot(0.0, 0L, 9L, "rng-c");
        Assert(DailySpinTransitionProof.Verify(timerBefore, timerAfter, out reason),
            "exact 86400-second boundary subtracts to zero rather than resetting heuristically");
        var partial = new DailySpinTransitionSnapshot(0.0, 0L, 8L, "rng-c");
        Assert(!DailySpinTransitionProof.Verify(timerBefore, partial, out reason),
            "timer debit without +1 total spin is quarantined");
        var staleRng = new DailySpinTransitionSnapshot(0.0, 0L, 9L, "rng-b");
        Assert(!DailySpinTransitionProof.Verify(timerBefore, staleRng, out reason),
            "reward accounting without saved-RNG advance is rejected");

        var rng1 = MoneyPitManager.FingerprintSavedRandomState(new FakeRandomState(1, 2));
        var rng2 = MoneyPitManager.FingerprintSavedRandomState(new FakeRandomState(1, 3));
        Assert(!string.IsNullOrEmpty(rng1) && rng1 != rng2,
            "private saved-RNG fields form a deterministic independent witness");
    }

    private static void TestNativeYggTierVector()
    {
        var expected = new[]
        {
            0, 1, 3, 6, 8, 12, 15, 19, 23, 27, 32, 37, 42,
            47, 53, 59, 64, 71, 77, 83, 90, 97, 104, 111, 118
        };
        for (var tier = 0; tier < expected.Length; tier++)
            Equal(expected[tier], YggdrasilEventController.TierFactor(tier),
                "native float32 tier factor " + tier);
    }

    private static void TestTypedManagerShape()
    {
        var daycare = File.ReadAllText("source/Managers/DaycareManager.cs");
        var beard = File.ReadAllText("source/Managers/BeardManager.cs");
        var digger = File.ReadAllText("source/Managers/DiggerManager.cs");
        var wandoos = File.ReadAllText("source/Managers/WandoosRunManager.cs");
        var pit = File.ReadAllText("source/Managers/MoneyPitManager.cs");
        var ygg = File.ReadAllText("source/Managers/YggdrasilManager.cs");
        var aug = File.ReadAllText("source/AllocationProfiles/BreakpointTypes/BestAug.cs");

        Assert(daycare.Contains("Manage(RootTransaction root)")
               && daycare.Contains("new DaycareSwapIntent")
               && daycare.Contains("finally") && daycare.Contains("previousItem1")
               && daycare.Contains("id == 75") && daycare.Contains("clue2Complete"),
            "Daycare is one rooted exact swap and retains the Tree clue weapon");
        Assert(beard.Contains("Manage(RootTransaction root)")
               && beard.Contains("new BeardToggleIntent")
               && beard.Contains("_plan.Id == 6")
               && !beard.Contains("RecapDiggers"),
            "Beard toggles are rooted and Golden Beard cannot hide a Digger clear");
        Assert(digger.Contains("ManageSet(RootTransaction root, AutopilotPlan plan)")
               && digger.Contains("ManageUpgrade(RootTransaction root, AutopilotPlan plan)")
               && digger.Contains("(before.Gold - after.Gold) - before.Cost")
               && digger.Contains("before.MaxLevels[_bundle.ActorId] + 1L"),
            "Digger set and one-level permanent upgrade have exact root postconditions");
        Assert(wandoos.Contains("NativeBindingKeys.WandoosSetOs")
               && wandoos.Contains("after.EnergyProgress == 0f")
               && wandoos.Contains("after.EnergyAllocated == before.EnergyAllocated"),
            "Wandoos OS switch proves exact progress reset and allocation preservation");
        Assert(pit.Contains("CheckMoneyPit(RootTransaction root, double reserve)")
               && pit.Contains("new MoneyPitIntent")
               && pit.Contains("MoneyPitTransitionProof.Verify")
               && pit.Contains("DailySpinTransitionProof.Verify")
               && pit.Contains("NativeBindingKeys.DailySpinClaim"),
            "Pit and Daily Spin use root children with exact debit/effect checks");
        Assert(ygg.Contains("fruitController.tierFactor")
               && ygg.Contains("afterSeeds - beforeSeeds")
               && !ygg.Contains("Math.Max(1, after - before) * permanent"),
            "live Ygg seed purchases use native per-fruit reward previews");
        Assert(!aug.Contains("progress > 0 ? 4.0")
               && !aug.Contains("AugProgress() > 0f ? 4")
               && aug.Contains("marginal / time"),
            "BestAug does not double-count partial progress with an arbitrary multiplier");
    }

    public static int Main()
    {
        try
        {
            TestWandoosUnlocks();
            TestPitCumulativeOrder();
            TestMoneyPitCopiedStateSettlement();
            TestDailySpinCopiedStateSettlement();
            TestNativeYggTierVector();
            TestTypedManagerShape();
            Console.WriteLine("Persistent system tests passed: " + _assertions + " assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
