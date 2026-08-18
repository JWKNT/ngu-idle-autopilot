/*
FILE PURPOSE

Purpose: GoldEventLedgerTests is the standalone pure regression suite for task 19's chronological
Gold/Blood planning mechanics. It never loads Unity, a save, or the injected bot assembly.

Mechanism: In-memory event fixtures exercise production bounds, online tick quantization, ritual
bar-start charging, raw-liquidity/discounted-debit splits, all-Gold actions, native offline phase
ordering and early returns, Money Pit delivery admission, Counterfeit/Gold-drop/TM continuation,
and the shared optional-bundle selector used by planner and actor.

Inputs and outputs: Inputs are numeric fixtures and immutable event/bundle records. Output is a
single assertion count or a thrown exception naming the violated invariant.

Invariants and safety: Tests are pure, deterministic, and process/save independent. They compile
the production ResourceHorizonModel pure surface under GOLD_LEDGER_TESTS together with task 18's
ExactResourceAllocator; no controller mutation path is present in the test executable.

Extension points and non-goals: Integration tests may add Character/native-controller fixtures,
but this suite does not grant Money Pit authority or claim live mutation safety.
*/
using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Autopilot;

internal static class GoldEventLedgerTests
{
    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static void Near(double actual, double expected, string message)
    {
        var tolerance = Math.Max(1e-9, Math.Abs(expected) * 1e-12);
        Assert(Math.Abs(actual - expected) <= tolerance,
            message + " (actual " + actual + ", expected " + expected + ")");
    }

    private static void TestGrossProductionCountedOnce()
    {
        Near(GoldMechanics.GrossUpperBound(100.0, 10.0, 30.0), 400.0,
            "gross upper bound must count one production stream");
        var online = GoldEventLedger.Evaluate(100.0, 10.0, 30.0,
            new GoldLedgerEvent[0]);
        Near(online.FinalGold, 400.0,
            "chronological ledger must not add net and gross for the same horizon");
    }

    private static void TestProgressChargeAndLiquidityDebitSplit()
    {
        Assert(GoldMechanics.BarNeedsStartCharge(0.0),
            "zero-progress ritual must charge when its bar starts");
        Assert(!GoldMechanics.BarNeedsStartCharge(0.000001),
            "positive ritual progress proves the current bar is already charged");

        var tm = GoldLedgerEvent.Charge("tm-speed", "TM Speed", 0.0,
            100.0, 50.0, 0);
        var belowRaw = GoldEventLedger.Evaluate(99.0, 0.0, 0.0, new[] {tm});
        Assert(!belowRaw.Feasible && belowRaw.FirstBlockedEventId == "tm-speed",
            "discounted-but-below-raw Gold must fail the native liquidity gate");
        var atRaw = GoldEventLedger.Evaluate(100.0, 0.0, 0.0, new[] {tm});
        Assert(atRaw.Feasible, "raw liquidity equality must be admitted");
        Near(atRaw.FinalGold, 50.0,
            "TM event must debit the discounted amount after passing raw liquidity");
        var horizon = new GoldHorizonEvaluation();
        horizon.Claims.Add(new GoldClaim {Kind = GoldClaimKind.AugmentAndTimeMachine,
            Amount = 50.0, RequiredLiquidity = 100.0, Hard = true});
        Near(horizon.ProtectedSpendBefore(GoldClaimKind.DiggerPermanentUpgrade), 100.0,
            "other actors must protect the raw start-liquidity gate, not only the debit");
    }

    private static void TestNativeTickSaturation()
    {
        Assert(GoldMechanics.OnlineBarCompletions(0.0, 0.25, 1.0) == 12L,
            "sub-tick progress must use exact completion tick ceilings");
        Assert(GoldMechanics.OnlineBarCompletions(0.0, 1.0, 1.0) == 50L,
            "one completion per tick must reach exactly 50 per second");
        Assert(GoldMechanics.OnlineBarCompletions(0.0, 1000.0, 1.0) == 50L,
            "overfill must be discarded and never exceed 50 completions per second");
        Assert(GoldMechanics.OnlineBarCompletions(0.5, 1000.0, 0.02) == 1L,
            "an already-paid partial bar still completes no faster than one tick");
    }

    private static void TestAllGoldEventChronology()
    {
        var pit = GoldLedgerEvent.Charge("money-pit", "Money Pit", 2.0,
            120.0, 120.0, 0);
        pit.SpendAll = true;
        var result = GoldEventLedger.Evaluate(100.0, 10.0, 5.0, new[] {pit});
        Assert(result.Feasible && result.AppliedEventIds.Single() == "money-pit",
            "all-Gold event must apply at its chronological liquidity point");
        Near(result.FinalGold, 30.0,
            "all-Gold event must zero the event-time stock before later production resumes");
    }

    private static void TestLootyDeliveryPreflight()
    {
        var noSlot = MoneyPitDeliveryPreflight.Evaluate(true, false, false, false);
        Assert(!noSlot.Admitted && noSlot.LootyDeliveryDue,
            "Looty-tier toss with no usable slot must hold before Gold/timer mutation");
        Assert(!MoneyPitDeliveryPreflight.Evaluate(true, true, false, true).Admitted,
            "coarse accessory filtering must hold unique Looty delivery");
        Assert(!MoneyPitDeliveryPreflight.Evaluate(true, false, true, true).Admitted,
            "exact item-67 filtering must hold unique Looty delivery");
        Assert(MoneyPitDeliveryPreflight.Evaluate(true, false, false, true).Admitted,
            "open filters plus exact usable capacity must admit Looty delivery");
        Assert(MoneyPitDeliveryPreflight.Evaluate(false, true, true, false).Admitted,
            "unrelated tosses do not require a phantom Looty slot");
        Assert(GoldMechanics.NativePitTierReached(
                GoldMechanics.SafePitThreshold(1e11), 10),
            "safety-margined cumulative threshold must cross native float/log tier test");
        Assert(GoldMechanics.NativePitTierReached(1e11, 10),
            "native float Log10 rounding at nominal Looty total must be mirrored exactly");
    }

    private static void TestExactOfflineOrderAndEarlyReturns()
    {
        var events = new List<GoldLedgerEvent>
        {
            GoldLedgerEvent.Offline("tm-multiplier", GoldLedgerPhase.OfflineTimeMachine,
                1, 1.0, 1.0, 0.0),
            GoldLedgerEvent.Offline("blood-0", GoldLedgerPhase.OfflineBlood,
                0, 5.0, 5.0, 0.0),
            GoldLedgerEvent.Offline("augment-1", GoldLedgerPhase.OfflineAugments,
                1, 30.0, 30.0, 0.0),
            GoldLedgerEvent.Offline("gold-lump", GoldLedgerPhase.OfflineGoldCredit,
                0, 0.0, 0.0, 100.0),
            GoldLedgerEvent.Offline("tm-speed", GoldLedgerPhase.OfflineTimeMachine,
                0, 20.0, 20.0, 0.0),
            GoldLedgerEvent.Offline("augment-0", GoldLedgerPhase.OfflineAugments,
                0, 80.0, 80.0, 0.0)
        };
        var result = GoldEventLedger.Evaluate(0.0, 0.0, 0.0, events);
        Assert(result.Feasible, "native offline subsystem early returns are modeled, not fatal");
        Assert(result.AppliedEventIds.SequenceEqual(new[] {"gold-lump", "augment-0", "blood-0"}),
            "offline order must be Gold -> Augments -> Blood -> TM regardless of input order");
        Assert(result.SkippedEventIds.SequenceEqual(new[]
               {"augment-1", "tm-speed", "tm-multiplier"}),
            "unaffordable track must starve only later tracks in the same offline subsystem");
        Near(result.FinalGold, 15.0,
            "offline ledger must preserve the exact post-order Gold balance");
    }

    private static void TestCounterfeitGoldDropAndBanking()
    {
        var minimum = 1e6;
        Near(GoldMechanics.CounterfeitBonus(minimum, minimum), 1.01,
            "first Counterfeit breakpoint must add one percent gross GPS");
        var next = GoldMechanics.NextCounterfeitInvestmentBreakpoint(minimum, minimum);
        Assert(next > minimum, "Counterfeit planner must target the next discrete output step");
        Near(GoldMechanics.ProjectCounterfeitGross(100.0, 1.0, 1.01), 101.0,
            "Counterfeit feedback must update gross GPS multiplicatively");

        Near(GoldMechanics.ProjectGoldDropRecord(100.0, 100.0, 2.0, 29), 100.0,
            "Fight Boss 29 cannot update the Time Machine base-Gold record");
        Near(GoldMechanics.ProjectGoldDropRecord(100.0, 100.0, 2.0, 30), 900.0,
            "eligible Gold Drop record uses expected native 4.5 random factor");
        Near(GoldMechanics.ProjectGoldDropGross(100.0, 100.0, 900.0), 900.0,
            "higher base-Gold record must feed downstream gross GPS");

        Assert(GoldMechanics.ProjectBankedTimeMachineLevel(101L, 0.25, false) == 25L,
            "ordinary reset must retain the floored perk-scaled TM bank");
        Assert(GoldMechanics.ProjectBankedTimeMachineLevel(101L, 0.25, true) == 0L,
            "challenge reset must clear the TM bank");
    }

    private static void TestActorLedgerBundleAgreement()
    {
        var candidates = new[]
        {
            new GoldSpendBundle {Kind = GoldClaimKind.DiggerPermanentUpgrade,
                ActionId = "digger-max-2", ActorId = 2, RequiredLiquidity = 120.0,
                Debit = 120.0, ValueScore = 9.0},
            new GoldSpendBundle {Kind = GoldClaimKind.DiggerPermanentUpgrade,
                ActionId = "digger-max-4", ActorId = 4, RequiredLiquidity = 80.0,
                Debit = 80.0, ValueScore = 5.0},
            new GoldSpendBundle {Kind = GoldClaimKind.DiggerPermanentUpgrade,
                ActionId = "digger-max-5", ActorId = 5, RequiredLiquidity = 70.0,
                Debit = 70.0, ValueScore = 4.0}
        };
        var planner = GoldEventLedger.SelectBestBundle(candidates, 100.0);
        var actor = GoldEventLedger.SelectBestBundle(candidates, 100.0);
        Assert(planner != null && actor != null && planner.ActionId == actor.ActionId
               && planner.ActorId == actor.ActorId && planner.ActionId == "digger-max-4",
            "planner and actor must resolve the same affordable typed Digger bundle");
        var applied = GoldEventLedger.Evaluate(100.0, 0.0, 0.0,
            new[] {actor.ToLedgerEvent(0)});
        Assert(applied.Feasible && applied.AppliedEventIds.Single() == planner.ActionId,
            "ledger must apply the exact action ID selected by the actor");
        Near(applied.FinalGold, 20.0,
            "selected actor bundle must debit its exact ledger amount once");
    }

    public static int Main()
    {
        TestGrossProductionCountedOnce();
        TestProgressChargeAndLiquidityDebitSplit();
        TestNativeTickSaturation();
        TestAllGoldEventChronology();
        TestLootyDeliveryPreflight();
        TestExactOfflineOrderAndEarlyReturns();
        TestCounterfeitGoldDropAndBanking();
        TestActorLedgerBundleAgreement();
        Console.WriteLine("Gold event ledger assertions passed: " + _assertions);
        return 0;
    }
}
