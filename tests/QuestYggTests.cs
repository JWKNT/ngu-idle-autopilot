using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Autopilot;
using NGUInjector.Managers;

/*
FILE PURPOSE

QuestYggTests is the isolated pure regression suite for reconciled task 22.  It exercises the
Antlers truth table/tick guard/capacity hold, distinct completion and free-minor MAXX modes, active
major Idle fallback, exact bank-overflow dates, negative-binomial manual tails, Butter-at-ready
timing, native fruit tier arithmetic, zero-stock/free and multi-fruit Poop batches, typed exact
reward previews, finite-horizon seed choice, and activation refusal before a zero-factor reset.

The executable creates no Character/controller, loads no save, invokes no native mutation, does not
inject/restart the game, and writes no runtime/config state.  Root-intent settlement remains covered
by task-1 tests; these fixtures prove the controller-free decisions those intents receive.
*/
internal static class QuestYggTests
{
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
            throw new Exception("FAIL: " + message + " expected=" + expected + " actual=" + actual);
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        _assertions++;
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            throw new Exception("FAIL: " + message + " expected=" + expected + " actual=" + actual);
    }

    private static QuestPolicySnapshot Quest(bool minor)
    {
        return new QuestPolicySnapshot
        {
            Active = true,
            Minor = minor,
            QuestId = 278,
            Target = 50,
            Current = 0,
            Banked = 0,
            BankCapacity = 10,
            BankTimerSeconds = 0.0,
            BankIntervalSeconds = 28200,
            MaxxTargetId = -1,
            Manual = new QuestManualFeasibility()
        };
    }

    private static FruitRewardPreview Fruit(int id, long eat, long eatPoop,
        long harvest, long harvestPoop, double specific = 0.0, double specificPoop = 0.0)
    {
        return new FruitRewardPreview
        {
            FruitId = id,
            Tier = 24,
            Mature = true,
            PoopEligible = true,
            EatSeedsWithoutPoop = eat,
            EatSeedsWithPoop = eatPoop,
            HarvestSeedsWithoutPoop = harvest,
            HarvestSeedsWithPoop = harvestPoop,
            SpecificWithoutPoop = specific,
            SpecificWithPoop = specificPoop,
            SeedShadowValue = 1.0,
            SpecificShadowValue = 1.0,
            PermanentTarget = PermanentEffectTarget.Terminal,
            SourceExact = true
        };
    }

    private static void TestAntlersTruthTableAndTickGuard()
    {
        Assert(!QuestEventController.AntlersCompletionEligible(true, false, true, .899),
            "0.899 is below the native Antlers window");
        Assert(QuestEventController.AntlersCompletionEligible(true, false, true, .900),
            "0.900 is the inclusive lower Antlers boundary");
        Assert(QuestEventController.AntlersCompletionEligible(true, false, true, 1.000),
            "1.000 is the inclusive upper Antlers boundary");
        Assert(!QuestEventController.AntlersCompletionEligible(true, false, true, 1.001),
            "1.001 is above the native Antlers window");
        Assert(!QuestEventController.AntlersCompletionEligible(true, true, true, .95),
            "Idle mode fails Antlers even inside the fraction window");
        Assert(!QuestEventController.AntlersCompletionEligible(true, false, false, .95),
            "allActive=false fails Antlers");
        Assert(!QuestEventController.AntlersCompletionEligible(false, false, true, .95),
            "item 337 is mandatory");
        Assert(QuestEventController.CanSafelyAdvanceAntlersIdle(.89, .02),
            "a tick ending at .91 is safe");
        Assert(!QuestEventController.CanSafelyAdvanceAntlersIdle(.979, .002),
            "staging ceiling rejects a tick ending above .98");
        Assert(!QuestEventController.CanSafelyAdvanceAntlersIdle(.99, .02),
            "a tick crossing one is never authorized");

        var state = Quest(false);
        state.NeedAntlers = true;
        state.Item337Dropped = true;
        state.AntlersCapacityAdmitted = true;
        state.AllActive = true;
        state.IdleProgress = .95;
        state.IdleMode = true;
        Equal(QuestEventAction.DisableIdle, QuestEventController.Evaluate(state).Action,
            "scheduler switches manual inside the valid window");
        state.IdleMode = false;
        Equal(QuestEventAction.RouteManual, QuestEventController.Evaluate(state).Action,
            "manual physical completion follows Antlers staging");
        state.Ready = true;
        Equal(QuestEventAction.Complete, QuestEventController.Evaluate(state).Action,
            "ready Antlers truth-table state completes");
        state.AntlersCapacityAdmitted = false;
        Equal(QuestEventAction.Hold, QuestEventController.Evaluate(state).Action,
            "unique Antlers delivery requires exact usable capacity");
    }

    private static void TestMaxxRerollSkipAndMajorFallback()
    {
        var state = Quest(true);
        state.MaxxTargetId = 278;
        state.QuestId = 279;
        var reroll = QuestEventController.Evaluate(state);
        Equal(QuestExecutionMode.MaxxAndSkipMinor, reroll.Mode,
            "unMAXXed eligible target selects dedicated campaign mode");
        Equal(QuestEventAction.RerollMinor, reroll.Action,
            "wrong free minor is skipped/rerolled");

        state.QuestId = 278;
        Equal(QuestEventAction.RouteManual, QuestEventController.Evaluate(state).Action,
            "matching target farms physical manual drops without offerings");
        state.MaxxTargetComplete = true;
        Equal(QuestEventAction.SkipMinor, QuestEventController.Evaluate(state).Action,
            "MAXX completion skips the minor instead of turning it in");
        state.UsedButter = true;
        Equal(QuestEventAction.Hold, QuestEventController.Evaluate(state).Action,
            "Buttered minor is never skipped");

        var major = Quest(false);
        major.MaxxTargetId = 278;
        major.Manual.ZoneUnlocked = false;
        Equal(QuestEventAction.EnableIdle, QuestEventController.Evaluate(major).Action,
            "active major falls back to Idle while its zone is locked");
        major.IdleMode = true;
        Equal(QuestEventAction.Hold, QuestEventController.Evaluate(major).Action,
            "already-idle blocked major keeps progressing");
        major.Manual.ZoneUnlocked = true;
        major.Manual.TitanPreempted = true;
        Equal(QuestEventAction.Hold, QuestEventController.Evaluate(major).Action,
            "Titan-preempted major remains Idle");
        major.Manual.TitanPreempted = false;
        major.Manual.Online = false;
        Equal(QuestEventAction.Hold, QuestEventController.Evaluate(major).Action,
            "offline major explicitly remains in Idle mode");

        var empty = Quest(false);
        empty.Active = false;
        empty.Banked = 1;
        Equal(QuestEventAction.StartMajor, QuestEventController.Evaluate(empty).Action,
            "banked major is started before a free minor campaign");
        empty.Banked = 0;
        empty.MaxxTargetId = 280;
        Equal(QuestEventAction.StartMinor, QuestEventController.Evaluate(empty).Action,
            "zero-bank campaign starts a free minor");
    }

    private static void TestBankDeadlineManualForecastAndButter()
    {
        Equal(28200, QuestEventController.BankIntervalSeconds(false, false),
            "base bank interval is 28,200 seconds");
        Equal(25380, QuestEventController.BankIntervalSeconds(false, true),
            "Fad interval follows native truncation");
        Equal(22560, QuestEventController.BankIntervalSeconds(true, false),
            "Faster Quests interval follows native truncation");
        Equal(20304, QuestEventController.BankIntervalSeconds(true, true),
            "combined interval follows sequential native truncation");
        Near(28200.0, QuestEventController.SecondsToBankOverflow(10, 10, 0.0, 28200),
            0.0, "full-bank arrival deadline");
        Near(56400.0, QuestEventController.SecondsToBankOverflow(9, 10, 0.0, 28200),
            0.0, "cap-1 is two arrivals from an actual lost major");

        var minor = Quest(true);
        minor.Banked = 10;
        minor.CompletionMeanSeconds = 28201.0;
        minor.MinorRewardValue = 10.0;
        minor.LostMajorValue = 50.0;
        Equal(QuestEventAction.SkipMinor, QuestEventController.Evaluate(minor).Action,
            "minor missing the dated full-bank deadline is skipped");
        minor.CompletionMeanSeconds = 28199.0;
        Equal(QuestEventAction.EnableIdle, QuestEventController.Evaluate(minor).Action,
            "minor completing before the arrival is retained");

        var capacity = ForecastCapacityProof.Prove(1, 1, false, true, "one usable slot");
        var forecast = QuestEventController.ForecastManual(2, .25, 1.0, capacity);
        Near(8.0, forecast.MeanSeconds, 1e-12,
            "manual quest mean is exact negative binomial");
        Near(15.0, forecast.P90Seconds, 1e-12,
            "manual quest p90 is a joint repeated-success tail");
        Assert(forecast.Trials.Exact && forecast.Trials.Capacity.Admitted,
            "manual forecast carries stochastic exactness and capacity proof");

        Assert(!QuestEventController.ShouldApplyButter(false, false, 1, false, 50.0),
            "Butter is not used at target-minus-two or any other non-ready state");
        Assert(QuestEventController.ShouldApplyButter(true, false, 1, false, 50.0),
            "positive incremental QP applies Butter at ready turn-in");
        Assert(!QuestEventController.ShouldApplyButter(true, false, 1, true, 50.0),
            "a planned skip never consumes Butter");
        Assert(!QuestEventController.ShouldApplyButter(true, true, 1, false, 50.0),
            "already-Buttered quest cannot debit twice");

        Equal(1, QuestEventController.HandInCredit(9, 10), "level 9 credit at ratio 10");
        Equal(11, QuestEventController.HandInCredit(100, 10), "level 100 credit at ratio 10");
        Equal(1, QuestEventController.HandInCredit(101, 10), "level above 100 falls back to one");
    }

    private static void TestTierMaturityAndActivationHorizon()
    {
        Equal(3600, YggdrasilEventController.TierThreshold(0), "quirk 0 tier length");
        Equal(3540, YggdrasilEventController.TierThreshold(1), "quirk 1 tier length");
        Equal(3480, YggdrasilEventController.TierThreshold(2), "quirk 2 tier length");
        Equal(3420, YggdrasilEventController.TierThreshold(3), "quirk 3 tier length");
        Equal(3420, YggdrasilEventController.TierThreshold(4), "tier reduction caps at 180 seconds");
        Equal(24, YggdrasilEventController.HarvestTier(82080.0, 24, 3420),
            "tier 24 maturity is exactly 82,080 seconds");
        Equal(24, YggdrasilEventController.HarvestTier(double.MaxValue, 24, 3420),
            "extreme accumulated time clamps before its integer conversion");
        Near(82080.0, YggdrasilEventController.SecondsToTier(0.0, 24, 3420), 0.0,
            "exact tier-24 horizon replaces 86,400 seconds");
        Equal(118, YggdrasilEventController.TierFactor(24), "tier-24 native factor");

        Assert(!YggdrasilEventController.CanMatureBeforeReset(0.0, 3419.0, 0.0, 3420),
            "zero-factor reset rejects activation one second short of tier one");
        Assert(YggdrasilEventController.CanMatureBeforeReset(0.0, 3420.0, 0.0, 3420),
            "activation is feasible at the exact tier-one horizon");
        Assert(YggdrasilEventController.CanMatureBeforeReset(0.0, 1.0, 1.0, 3420),
            "a positive floored reset factor preserves growth");
        var candidate = new FruitActivationCandidate
        {
            FruitId = 3,
            Seconds = 0.0,
            ActivationBenefit = 10.0
        };
        Assert(!YggdrasilEventController.ShouldActivate(candidate, 3419.0, 0.0, 3420),
            "planner makes no resource sacrifice for an impossible maturity");
        Assert(YggdrasilEventController.ShouldActivate(candidate, 3420.0, 0.0, 3420),
            "planner activates when exact maturity and positive payoff are proven");

        var competing = PermanentMarginalOracle.DescribeHack(15, 1, 10.0,
            PermanentEffectTarget.Terminal, 1.0, 2.0, true);
        candidate.DisplacedPermanentActions = new[] {competing};
        candidate.ActivationBenefit = .1;
        Assert(!YggdrasilEventController.ShouldActivate(candidate, 3420.0, 0.0, 3420),
            "typed permanent opportunity value can defeat fruit activation");
    }

    private static void TestPoopBatchesAndExactPreviews()
    {
        var preview = Fruit(4, 200, 300, 200, 300);
        var free = YggdrasilEventController.PlanPoopBatch(new[] {preview}, 0, 0, true);
        Assert(free.Decisions.Single().UsePoop && free.Decisions.Single().FreePoop,
            "MAXX item 162 permits the first free use at zero stock");
        Equal(0, free.FinalStock, "free zero-stock use has no debit");
        Equal(1L, free.FinalCounter, "free zero-stock use increments persistent counter");

        var blocked = YggdrasilEventController.PlanPoopBatch(new[] {preview}, 0, 1, true);
        Assert(!blocked.Decisions.Single().UsePoop,
            "zero stock at non-free modulo does nothing");
        Equal(1L, blocked.FinalCounter, "rejected Poop does not increment counter");

        var tenth = YggdrasilEventController.PlanPoopBatch(new[] {preview}, 1, 10, true);
        Assert(tenth.Decisions.Single().FreePoop, "counter 10 use is free");
        Equal(1, tenth.FinalStock, "tenth free use does not debit positive stock");
        Equal(11L, tenth.FinalCounter, "tenth free use increments counter");

        var two = YggdrasilEventController.PlanPoopBatch(new[]
        {
            Fruit(7, 10, 20, 15, 30),
            Fruit(3, 10, 20, 15, 30)
        }, 1, 0, true);
        Assert(two.Decisions.Select(x => x.FruitId).SequenceEqual(new[] {3, 7}),
            "simultaneous batch follows native fruit-ID order");
        Assert(two.Decisions.All(x => x.UsePoop) && two.Decisions.First().FreePoop
               && !two.Decisions.Last().FreePoop,
            "first batch action is free and second consumes the one stock item");
        Equal(0, two.FinalStock, "two-action batch exact stock transition");
        Equal(2L, two.FinalCounter, "two-action batch exact counter transition");

        var scarce = YggdrasilEventController.PlanPoopBatch(new[]
        {
            Fruit(0, 10, 11, 10, 11),
            Fruit(1, 10, 50, 10, 50)
        }, 1, 1, false);
        Assert(!scarce.Decisions[0].UsePoop && scarce.Decisions[1].UsePoop,
            "one paid Poop is reserved for the greater exact marginal reward");
        Equal(40.0, scarce.TotalMarginalValue,
            "limited-stock batch maximizes exact reward while preserving native order");

        var partial = Fruit(5, 10, 20, 20, 40);
        partial.PoopEligible = false;
        var noPartialPoop = YggdrasilEventController.PlanPoopBatch(new[] {partial}, 10, 0, true);
        Assert(!noPartialPoop.Decisions.Single().UsePoop,
            "poopOnlyMaxTier prevents partial-tier Poop consumption");

        var exactEat = Fruit(3, 100, 150, 200, 300, 250.0, 375.0);
        Equal(FruitConsumeKind.Eat, YggdrasilEventController.SelectConsumeKind(exactEat),
            "exact specific reward preview can dominate the extra harvest seeds");
        Assert(exactEat.SourceExact && exactEat.PermanentTarget == PermanentEffectTarget.Terminal,
            "reward preview retains exact/source and typed permanent provenance");
        Near(1.0, YggdrasilEventController.ClampPoopModifier(.5), 0.0,
            "Poop modifier lower clamp");
        Near(1.65, YggdrasilEventController.ClampPoopModifier(2.0), 0.0,
            "Poop modifier upper clamp");
    }

    private static void TestFiniteHorizonSeedChoiceAndCapacityApi()
    {
        var purchase = YggdrasilEventController.SelectSeedPurchase(new[]
        {
            new SeedPurchaseCandidate {FruitId = 1, ExactCost = 10, FiniteHorizonValue = 5},
            new SeedPurchaseCandidate {FruitId = 2, ExactCost = 5, FiniteHorizonValue = 4},
            new SeedPurchaseCandidate {FruitId = 3, ExactCost = 100, FiniteHorizonValue = 1000}
        }, 20);
        Equal(2, purchase.FruitId,
            "affordable tier with best finite-horizon value per exact cost is selected");

        var ids = new[] {999, 0};
        var identities = new object[] {new object(), null};
        var topology = PhysicalTopology.CaptureOrdinary(ids, identities, 2, 1);
        var proof = LootCapacity.ProveOrdinary(topology,
            LootCapacityRequirement.ExactUniqueDelivery("antlers-338", 0, 1, 0));
        Assert(proof.Admitted && proof.UsableFreeSlotCount == 1,
            "Antlers admission uses the task-6 native ordinary scan interval");
    }

    public static int Main()
    {
        TestAntlersTruthTableAndTickGuard();
        TestMaxxRerollSkipAndMajorFallback();
        TestBankDeadlineManualForecastAndButter();
        TestTierMaturityAndActivationHorizon();
        TestPoopBatchesAndExactPreviews();
        TestFiniteHorizonSeedChoiceAndCapacityApi();
        Console.WriteLine("Quest/Ygg tests passed: " + _assertions + " assertions");
        return 0;
    }
}
