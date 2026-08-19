/*
FILE PURPOSE

ItopodPerkTests is the isolated pure/fault-injection suite for reconciled task 26.  It proves the
record-4-to-10 continuous range, separate one-hit farm/conservative diagnostic reach,
the same-floor failure circuit breaker and all-source capability readmission, exact decade awards
and fought/drop-floor ordering, 8.4% boost
table, clue-four naked session, online/offline estimator split, floor-1600 bounds and END forecast,
typed perk/ID/Fibonacci behavior, asynchronous perk-231 slot lifetime, MOVE69 idle charging/strict
cooldown/ETA/capacity/post-69 retry, timer cancellation/restart telemetry, and exactly one verified
task-1 mutation atom. It also guards the live policy against spending post-sequence PP through
name/effect heuristics. It loads no game assembly, Unity UI, controller, save, or runtime process.
*/
using System;
using System.Collections.Generic;
using System.IO;
using NGUInjector.Autopilot;
using NGUInjector.Managers;

namespace NGUInjector.Autopilot
{
    // Minimal task-1 policy surface for this standalone executable.
    internal sealed class AutopilotConfig
    {
        internal bool Enabled = true;
        internal string Mode = "full";
        internal bool AutoEnterGame = true;
        internal bool AllowLegacyFallback = true;
        internal bool ManageAllocations = true;
        internal bool ManageBosses = true;
        internal bool ManageAdventure = true;
        internal bool ManageInventory = true;
        internal bool ManageDiggers = true;
        internal bool ManageYggdrasil = true;
        internal bool ManageQuests = true;
        internal bool ManageWishes = true;
        internal bool ManageCards = true;
        internal bool ManageCooking = true;
        internal bool ManageMoneyPit = true;
        internal bool ManageDailySpin = true;
        internal bool ManageBloodMagic = true;
        internal bool ManageBeards = true;
        internal bool AllowExpSpending = true;
        internal bool AllowApSpending = true;
        internal bool AllowPerkSpending = true;
        internal bool AllowQuirkSpending = true;
        internal bool AllowCardYeeting = true;
        internal bool AllowRebirths = true;
        internal bool AllowChallenges = true;
        internal bool AllowDifficultyExecution;
        internal bool AllowEndSequence = false;

        internal bool IsDryRun { get { return Mode != "assist" && Mode != "full"; } }
        internal bool IsAssist { get { return Mode == "assist"; } }
        internal bool IsFull { get { return Mode == "full"; } }

        internal string ExecutionFingerprint()
        {
            return Enabled + "|" + Mode + "|" + ManageAdventure + "|" + ManageInventory;
        }
    }

    internal sealed class AutopilotManager
    {
        internal AutopilotConfig Config;
    }
}

internal static class ItopodPerkTests
{
    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static void Equal(long actual, long expected, string message)
    {
        Assert(actual == expected,
            message + " (actual " + actual + ", expected " + expected + ")");
    }

    private static void Near(double actual, double expected, double tolerance, string message)
    {
        Assert(Math.Abs(actual - expected) <= tolerance,
            message + " (actual " + actual + ", expected " + expected + ")");
    }

    private static int Count(string source, string value)
    {
        return source.Split(new[] {value}, StringSplitOptions.None).Length - 1;
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        _assertions++;
        try { action(); }
        catch (T) { return; }
        throw new Exception("FAIL: " + message);
    }

    private static OrdinaryInventoryTopology Topology(int freeSlots)
    {
        var ids = new int[Math.Max(1, freeSlots)];
        var identities = new object[ids.Length];
        if (freeSlots == 0)
        {
            ids[0] = 999;
            identities[0] = new object();
        }
        return PhysicalTopology.CaptureOrdinary(ids, identities, ids.Length, 0);
    }

    private static LootCapacityProof MoveCapacity(int freeSlots)
    {
        return LootCapacity.ProveOrdinary(Topology(freeSlots),
            Move69Manager.TerminalDeliveryRequirement());
    }

    private static ItopodEconomy Economy(ItopodDifficulty difficulty)
    {
        return new ItopodEconomy(difficulty, 1.0, 0L, false, 0.0,
            true, ItopodEconomy.MacguffinCadence(false, false, false, false));
    }

    private static ItopodOnlineState State(int start, int end, int live,
        int counter, int record, long globalKills)
    {
        return new ItopodOnlineState
        {
            SavedStart = start,
            SavedEnd = end,
            LiveFloor = live,
            KillCounter = counter,
            HighestRecord = record,
            EnemiesKilled = globalKills
        };
    }

    private static void TestContinuousClimbAndSentinelAtom()
    {
        var plan = ItopodPerkPlanner.PlanContinuousClimb(4, 9, 10);
        Assert(plan.Climbing && plan.Start == 3 && plan.End == 10,
            "record 4 uses the only legal climb start H-1 and decade target 10");
        Equal(plan.FreshEntryKillsToTarget, 70L,
            "fresh record 4 to record 10 is floors 3 through 9, exactly 70 kills");
        Equal(ItopodPerkPlanner.FreshEntryKillsToRecord(3, 10, 4, 10), 70L,
            "standalone range simulator agrees with continuous plan");

        var before = State(3, 10, 9, 9, 9, 69L);
        var transition = ItopodPerkPlanner.SimulateOnlineKill(before,
            Economy(ItopodDifficulty.Normal));
        var gate = new ItopodPostKillGate();
        var directive = gate.Observe(transition.Before, transition.After, plan);
        Assert(directive.Kind == PostKillDirectiveKind.ExitSynchronouslyAndReplan
               && directive.BeforeNextRespawn,
            "target record leaves live floor 10 and synchronously exits before sentinel respawn");
        Assert(gate.Observe(transition.Before, transition.After, plan).Kind
               == PostKillDirectiveKind.DuplicateObservation,
            "one native kill can cause only one post-kill Adventure atom");
    }

    private static void TestExactDeathOrderingAndDecades()
    {
        var boundary = State(49, 50, 49, 9, 49, 37L);
        var kill = ItopodPerkPlanner.SimulateOnlineKill(boundary,
            Economy(ItopodDifficulty.Normal));
        Assert(kill.FoughtFloor == 49 && kill.DropFloor == 50,
            "ordinary reward retains fought floor while drop table sees post-move floor");
        Equal(kill.OrdinaryProgress, 249L, "normal ordinary PP progress uses fought floor 49");
        Assert(kill.NewRecord && kill.After.HighestRecord == 50,
            "record saves after ten-kill floor movement");
        Equal(kill.FirstClearPerkPoints, 1L, "new decade 50 awards one spendable PP");
        Assert(kill.RewardTier == 2 && kill.RewardDivisor == 38 && kill.ApAwarded,
            "AP/EXP cadence uses post-move floor 50 and global counter 38");
        Equal(kill.After.CurrentAp, 1L, "online ITOPOD AP is a direct exact +1");
        Equal(kill.After.LifetimeAp, 1L, "online ITOPOD lifetime AP is a direct exact +1");
        Equal(kill.BaseExpAwarded, 2L, "tier-two base EXP is two before Character.addExp modifiers");

        Equal(MechanicsItopod.FirstClearPerkPoints(10, true), 1L, "floor 10 decade award");
        Equal(MechanicsItopod.FirstClearPerkPoints(99, true), 0L, "non-decade has no award");
        Equal(MechanicsItopod.FirstClearPerkPoints(100, true), 10L, "floor 100 super-decade");
        Equal(MechanicsItopod.FirstClearPerkPoints(110, true), 2L, "floor 110 award");
        Equal(MechanicsItopod.FirstClearPerkPoints(200, true), 20L, "floor 200 super-decade");
        Equal(MechanicsItopod.FirstClearPerkPoints(1600, true), 160L, "floor 1600 award");
        Equal(MechanicsItopod.FirstClearPerkPoints(100, false), 0L,
            "old records never repeat a decade award");

        var fixedFarm = State(1599, 1599, 1599, 9, 1600, 0L);
        var wrapped = ItopodPerkPlanner.SimulateOnlineKill(fixedFarm,
            Economy(ItopodDifficulty.Sadistic));
        Assert(wrapped.FoughtFloor == 1599 && wrapped.DropFloor == 1599
               && wrapped.After.KillCounter == 0,
            "fixed floor increments then wraps before drop settlement");
    }

    private static void TestGlobalAdventureRouteValue()
    {
        var gate = ItopodPerkPlanner.ChooseAdventureRoute(9, 9, 1.25,
            4L, 0L, 5L, .10, true, false, false,
            -1.0, .20, false, false);
        Assert(gate.Choice == AdventureRouteChoice.ItopodFrontier
               && gate.AwardFloor == 10 && gate.FirstClearPerkPoints == 1L
               && gate.KillsToAward == 20L && gate.CompletesPerkGate,
            "an exact floor-10 award preempts uncalibrated collection when it closes the next perk gate");
        Near(gate.SecondsToAward, 25.0, 1e-12,
            "record ETA uses the native H-1 entry tax and complete kill cycle");
        var live = ItopodPerkPlanner.ChooseAdventureRoute(9, 9, 1.25,
            4L, 0L, 5L, .10, true, false, false,
            -1.0, .20, false, false, 9, 9, 8, 10);
        Assert(live.KillsToAward == 1L,
            "an already-active compatible range preserves exact live floor/counter progress");

        var conservative = ItopodPerkPlanner.ChooseAdventureRoute(9, 9, 1.25,
            0L, 0L, 50L, .10, true, false, true,
            -1.0, .20, false, false);
        Assert(conservative.Choice == AdventureRouteChoice.CollectionFarm
               && conservative.Reason.Contains("not source-calibrated"),
            "unknown core-set time is retained when a small record award does not close a perk gate");

        var optionalUnknown = ItopodPerkPlanner.ChooseAdventureRoute(4, 4, 2.0,
            0L, 0L, 1L, .10, true, false, false,
            -1.0, 1.6, false, false, -1, 0, -1, -1, .0001, true);
        Assert(optionalUnknown.Choice == AdventureRouteChoice.ItopodFarm
               && optionalUnknown.Reason.Contains("optional collection"),
            "unknown optional-item time loses to exact steady ITOPOD perk progress");

        var optionalFast = ItopodPerkPlanner.ChooseAdventureRoute(4, 4, 2.0,
            0L, 0L, 1L, .10, true, false, false,
            10.0, 1.6, false, false, -1, 0, -1, -1, .0001, true);
        Assert(optionalFast.Choice == AdventureRouteChoice.CollectionFarm,
            "a source-timed optional item may farm when its completed value rate beats ITOPOD");

        var boss = ItopodPerkPlanner.ChooseAdventureRoute(49, 49, 1.0,
            0L, 0L, 999L, .001, true, true, false,
            10.0, 5.0, false, false);
        Assert(boss.Choice == AdventureRouteChoice.BossSnipe,
            "a source-proven boss-exclusive collection target remains a distinct route");

        var complete = ItopodPerkPlanner.ChooseAdventureRoute(99, 99, 1.0,
            0L, 0L, 500L, .01, false, false, false,
            -1.0, 0.0, false, false);
        Assert(complete.Choice == AdventureRouteChoice.ItopodFrontier
               && complete.AwardFloor == 100 && complete.FirstClearPerkPoints == 10L,
            "completed ordinary debt values the super-decade award globally");

        var oversizedAward = ItopodPerkPlanner.ChooseAdventureRoute(99, 99, 1.0,
            98L, 0L, 100L, .40, true, false, false,
            1000.0, .01, false, false);
        Assert(oversizedAward.CompletesPerkGate
               && oversizedAward.FirstClearPerkPoints == 10L,
            "a super-decade award larger than the two-PP gap closes exactly one perk gate");
        Near(oversizedAward.ItopodProgressionRate,
            .40 / oversizedAward.SecondsToAward, 1e-15,
            "multi-PP award credits at most the whole next perk level, never gain times PP");

        var terminal = ItopodPerkPlanner.ChooseAdventureRoute(1600, 1600, 1.0,
            0L, 0L, 0L, 0.0, true, false, false,
            1.0, 100.0, false, true);
        Assert(terminal.Choice == AdventureRouteChoice.ItopodFarm,
            "the exclusive Sadistic END source dominates even a high-valued collection candidate");
    }

    private static void TestCombatReachAndEmpiricalBreaker()
    {
        var reach = ItopodCombatOracle.ProveReach(542.495, 450.135, 1734.5,
            1.5, .8, 3.0, 50);
        Assert(reach.OneHitFloor == 1,
            "the observed early-game stats retain the native-compatible one-hit farm ceiling");
        Assert(reach.FrontierFloor >= 9 && reach.FrontierFloor > reach.OneHitFloor,
            "the same stats prove a conservative multi-hit climb through the first PP decade");
        Assert(reach.ModeledPositiveDamageFloor > reach.FrontierFloor,
            "the Regular-Attack diagnostic extends beyond the conservative frontier without capping exploration");
        var floorTen = ItopodCombatOracle.EvaluateFloor(10, 542.495, 450.135,
            1734.5, 1.5, .8, 3.0);
        Assert(!floorTen.OneHit && floorTen.FrontierClear && floorTen.Hits == 2
               && floorTen.KillSeconds <= ItopodCombatOracle.FrontierKillHorizonSeconds,
            "floor ten is admitted as a finite two-hit frontier, never mislabeled one-hit farm");
        var floorTenBeastIncoming = ItopodCombatOracle.EvaluateFloor(10, 542.495, 450.135,
            1734.5, 1.5, 1.0, 3.0);
        var floorTenNoBeast = ItopodCombatOracle.EvaluateFloor(10, 542.495, 450.135,
            1734.5, 1.5, 1.0, 1.0);
        var enemyAttack = 10.0 * Math.Pow(1.05, 10) * 1.02;
        var directPoisonBound = enemyAttack * .2 * 1.2;
        Near(floorTenBeastIncoming.WorstIncomingDamage,
            (floorTenNoBeast.WorstIncomingDamage - directPoisonBound) * 3.0
            + directPoisonBound, 1e-10,
            "Beast triples PlayerController damage but not poison's native direct Adventure HP subtraction");

        var regenerationWall = ItopodCombatOracle.EvaluateFloor(0, 20.0, 1000.0,
            10000.0, .1, 10.0, 1.0);
        Assert(!regenerationWall.FrontierClear
               && regenerationWall.Reason.Contains("regeneration"),
            "non-positive post-regen damage fails closed instead of promising a finite clear");
        var fragile = ItopodCombatOracle.EvaluateFloor(10, 542.495, 0.0,
            1.0, 1.5, 1.0, 3.0);
        Assert(!fragile.FrontierClear && fragile.WorstIncomingDamage > 0.0,
            "a finite multi-hit kill is rejected when conservative incoming damage is lethal");
        var tooSlow = ItopodCombatOracle.EvaluateFloor(10, 542.495, 10000.0,
            10000.0, 1.5, 2.2, 1.0);
        Assert(!tooSlow.FrontierClear && tooSlow.PositiveDamageModel
               && tooSlow.Reason.Contains("4.1s"),
            "a slow fight stays outside the conservative frontier but remains modeled as positive damage");

        var baseCapability = new ItopodTrialCapability(700.0, 650.0,
            2000.0, 1.5, .8, 3.0);
        var trial = new ItopodClimbTrialController();
        var open = trial.Decide(40, 1600, baseCapability);
        Assert(open.ShouldClimb && open.TargetRecord == 50,
            "record forty opens a direct empirical push to the next valuable decade, floor fifty");
        for (var failure = 1; failure <= ItopodClimbTrialController.FailureStreakLimit; failure++)
        {
            var blockedNow = trial.Observe(49, true, baseCapability);
            if (failure < ItopodClimbTrialController.FailureStreakLimit)
                Assert(!blockedNow, "a small number of RNG deaths does not abandon the decade push");
            // Native death restarts the range. Replayed lower-floor wins are not evidence that
            // floor 49 itself became viable and therefore must not clear its death streak.
            trial.Observe(39, false, baseCapability);
            if (failure == ItopodClimbTrialController.FailureStreakLimit)
                Assert(blockedNow, "eight confirmed deaths on the same deep floor open the farm breaker");
        }
        var held = trial.Decide(40, 1600, baseCapability);
        Assert(!held.ShouldClimb && held.BlockedFloor == 49
               && held.ConsecutiveFailures == ItopodClimbTrialController.FailureStreakLimit
               && trial.ObservedFailureFloor == 49,
            "the blocked route farms while preserving the exact failed floor and evidence count");

        var tooSmall = new ItopodTrialCapability(700.0, 650.0,
            2000.0 * 1.04, 1.5, .8, 3.0);
        Assert(!trial.Decide(40, 1600, tooSmall).ShouldClimb,
            "sub-material Adventure growth does not thrash a known failed climb");
        var improvedElsewhere = new ItopodTrialCapability(700.0, 650.0,
            2000.0 * 1.06, 1.5, .8, 3.0);
        var reopened = trial.Decide(40, 1600, improvedElsewhere);
        Assert(reopened.ShouldClimb && reopened.Reopened && reopened.TargetRecord == 50,
            "a material durability gain reopens the push regardless of which game system supplied it");

        var exactFloorSuccess = new ItopodClimbTrialController();
        for (var failure = 0; failure < ItopodClimbTrialController.FailureStreakLimit - 1; failure++)
            exactFloorSuccess.Observe(49, true, baseCapability);
        exactFloorSuccess.Observe(49, false, baseCapability);
        Assert(!exactFloorSuccess.Observe(49, true, baseCapability)
               && exactFloorSuccess.ConsecutiveFailures == 1,
            "an actual kill on the difficult floor clears its failure streak");
        Assert(trial.Decide(43, 1600, improvedElsewhere).TargetRecord == 50
               && trial.Decide(50, 1600, improvedElsewhere).TargetRecord == 60,
            "non-decade records finish their current boundary and completed decades advance by ten");

        var noModeledDamage = new ItopodTrialCapability(0.0, 1.0,
            1.0, 0.0, .8, 1.0);
        Assert(new ItopodClimbTrialController().Decide(100, 1600, noModeledDamage).ShouldClimb,
            "a formula that predicts no Regular-Attack damage cannot impose a trial ceiling");
        var zeroWall = new ItopodClimbTrialController();
        for (var failure = 0; failure < ItopodClimbTrialController.FailureStreakLimit; failure++)
            zeroWall.Observe(109, true, noModeledDamage);
        Assert(!zeroWall.Decide(100, 1600, noModeledDamage).ShouldClimb,
            "zero net damage compared with the same zero does not instantly reopen a learned wall");
        var crossedWall = new ItopodTrialCapability(10000.0, 1.0,
            1.0, 1.5, .8, 1.0);
        Assert(zeroWall.Decide(100, 1600, crossedWall).ShouldClimb,
            "crossing from zero to positive net damage is a real all-source capability improvement");

        var stalled = new ItopodFightProgressWatch(1000.0);
        Assert(!stalled.Observe(59.99, 1000.0, 1000.0)
               && stalled.Observe(60.0, 1000.0, 1000.0),
            "one minute with no new enemy-HP low is a failed empirical attempt");
        var slowProgress = new ItopodFightProgressWatch(1000.0);
        for (var step = 1; step <= 10; step++)
            Assert(!slowProgress.Observe(step * 59.0, 1000.0 - step, 1000.0),
                "continued slow HP progress has no total fight-time ceiling");
        Assert(slowProgress.Observe(650.0, 990.0, 1000.0),
            "the no-progress timer starts from the last meaningful new low");
    }

    private static void TestBoostsClueAndFloorBounds()
    {
        Near(ItopodPerkPlanner.AnyBoostProbability, 0.084, 1e-15,
            "two-stage native boost path is exactly 8.4 percent");
        Near(ItopodPerkPlanner.EachBoostFamilyProbability, 0.028, 1e-15,
            "each Power/Toughness/Special family is exactly 2.8 percent");
        Assert(ItopodPerkPlanner.BoostMagnitudeIndex(499) == 10
               && ItopodPerkPlanner.BoostMagnitudeIndex(500) == 10
               && ItopodPerkPlanner.BoostMagnitudeIndex(700) == 11
               && ItopodPerkPlanner.BoostMagnitudeIndex(850) == 12
               && ItopodPerkPlanner.BoostMagnitudeIndex(1150) == 13
               && ItopodPerkPlanner.BoostMagnitudeIndex(1600) == 13,
            "compressed boost magnitudes follow every native tier boundary");

        var cluePlan = ItopodPerkPlanner.PlanClueFour();
        Equal(cluePlan.FreshEntryKillsToTarget, 1001L,
            "clue route includes 1000 moves to floor 100 plus one live-floor-100 kill");
        var clue = new ClueFourSession();
        Assert(clue.Enter(true, 0, true).SessionEligible,
            "clues 1-3, saved start zero, and naked entry arm the session");
        Assert(!clue.ObserveKill(0, 100, 0, true, false).QualifyingKill,
            "99-to-100 transition counter zero does not qualify");
        Assert(clue.ObserveKill(0, 100, 1, true, false).QualifyingKill,
            "first kill while live at floor 100 and counter one qualifies");
        clue.ObserveKill(0, 50, 1, false, false);
        Assert(!clue.ObserveKill(0, 100, 1, true, false).SessionEligible,
            "seeing any equipped slot permanently invalidates this session");

        var capPlan = ItopodPerkPlanner.PlanContinuousClimb(1599, 1599, 1600);
        Assert(capPlan.Start == 1598 && capPlan.End == 1600 && capPlan.TargetRecord == 1600,
            "record 1600 is reachable by fighting only through proved floor 1599");
        Throws<ArgumentOutOfRangeException>(() =>
            ItopodPerkPlanner.PlanContinuousClimb(1600, 1600, 1601),
            "native floor 1601 is rejected before planning");
        Near(ItopodPerkPlanner.EndItem491Chance(1449), 0.0, 0.0,
            "floor 1449 is below END eligibility");
        Near(ItopodPerkPlanner.EndItem491Chance(1450), 0.00005, 1e-15,
            "floor 1450 END probability");
        Near(ItopodPerkPlanner.EndItem491Chance(1600), 0.00755, 1e-15,
            "floor 1600 END probability");

        var oneSlot = ItopodPerkPlanner.ForecastEndItem491(1600, Topology(1));
        var twoSlots = ItopodPerkPlanner.ForecastEndItem491(1600, Topology(2));
        Assert(!oneSlot.Capacity.Admitted && twoSlots.Capacity.Admitted
               && twoSlots.Capacity.RequiredFreeSlots == 2,
            "scheduled MacGuffin before item 491 requires two exact ordinary slots");
        Near(twoSlots.MeanKills, 1.0 / 0.00755, 1e-9, "floor-1600 geometric mean");
        Assert(twoSlots.MedianKills == 92L && twoSlots.P95Kills == 396L,
            "floor-1600 geometric median and 95th-percentile kill bounds");
    }

    private static void TestOnlineOfflineEstimatorSplit()
    {
        var start = State(1600, 1600, 1600, 0, 1600, 19L);
        var online = ItopodPerkPlanner.EstimateOnline(start,
            Economy(ItopodDifficulty.Sadistic), 20);
        Equal(online.ApAwards, 1L, "online persistent modulo at divisor 20 awards once");
        Near(online.ExpectedBoosts, 1.68, 1e-12,
            "online estimate carries exact 8.4-percent expected boosts");
        Assert(online.ProbabilityAtLeastOneEndItem491 > 0.0,
            "online eligible floor has nonzero END progress");
        var onlineTime = ItopodPerkPlanner.EstimateOnlineIdle(start,
            Economy(ItopodDifficulty.Sadistic), 22.0, 1.0, 0.1);
        Equal(onlineTime.KillEstimate.Kills, 20L,
            "online wall-time estimator uses supplied live attack speed plus respawn");
        Assert(onlineTime.OrdinaryPpPerSecond > 0.0
               && onlineTime.ExpectedBoostsPerSecond > 0.0,
            "online rate output remains distinct from native offline session arithmetic");

        var offlineEconomy = new ItopodEconomy(ItopodDifficulty.Sadistic, 1.0,
            0L, true, 0.5, true, 5000);
        var offline = ItopodPerkPlanner.EstimateOffline(1600, 42.0, 0.1, false,
            offlineEconomy, 0L, 8999L, true);
        Equal(offline.Kills, 38L, "offline native one-second plus respawn cycle floors kills");
        Equal(offline.ApAwards, 1L,
            "offline AP uses kills/divisor and ignores a pre-session global remainder");
        Equal(offline.DeterministicPoopAwards, 1L,
            "offline includes deterministic perk-30 progress with carry");
        Equal(offline.CubeBoostBatches, 4L, "offline cube filter uses deterministic kills/8");
        Assert(!offline.SpecialDropsPossible && !offline.FirstClearAwardsPossible,
            "offline forecast promises no random boosts, clue, Exile, END, or record awards");
    }

    private static void TestTypedPerksAndAsyncDelivery()
    {
        Assert(ItopodPerkPlanner.IsFibonacciMilestone(89)
               && ItopodPerkPlanner.IsFibonacciMilestone(1597)
               && !ItopodPerkPlanner.IsFibonacciMilestone(90),
            "Fibonacci perk values only exact native milestone levels");
        var terminal = ItopodPerkPlanner.TerminalPerk231(0L, 1000000.0);
        Equal(terminal.FlatCost, 2500000000L, "perk 231 exact flat serialized cost");
        var choices = new List<PerkCandidate>
        {
            new PerkCandidate(232, "id==Count bug", 1L, 0L, 1L,
                ItopodDifficulty.Normal, PerkEffectClass.FeatureUnlock, 999999999.0, 0),
            terminal
        };
        var invalidFirst = ItopodPerkPlanner.ChoosePerk(choices, 232,
            3000000000L, 100000000L, ItopodDifficulty.Sadistic);
        Assert(invalidFirst.Status == PerkPlanStatus.Planned
               && invalidFirst.Candidate.Id == 231
               && invalidFirst.HoldOrdinarySlotUntilDelivery,
            "id==Count is rejected while typed terminal perk and async slot are selected");
        Assert(ItopodPerkPlanner.ChoosePerk(new[] {terminal}, 232,
                   2500000000L, 1L, ItopodDifficulty.Sadistic).Status
               == PerkPlanStatus.HeldReserve,
            "flat cost respects exact post-purchase PP reserve");
        Assert(ItopodPerkPlanner.ChoosePerk(new[] {terminal}, 232,
                   3000000000L, 0L, ItopodDifficulty.Evil).Status
               == PerkPlanStatus.HeldDifficulty,
            "Sadistic perk cannot be purchased in Evil");

        var boss224 = ItopodPerkPlanner.EvaluatePerk231Grant(1L, 224, false,
            Topology(1), true, 12.0);
        Assert(boss224.Status == AsyncPerkGrantStatus.WaitingForBoss225
               && boss224.ReservedOrdinarySlots == 1,
            "source purchase reserves a slot even before the Boss-225 checker gate");
        var full = ItopodPerkPlanner.EvaluatePerk231Grant(1L, 225, false,
            Topology(0), true, 0.0);
        Assert(full.Status == AsyncPerkGrantStatus.WaitingForCapacity
               && full.ReservedOrdinarySlots == 1,
            "full checker attempt remains pending and holds one future slot");
        Assert(ItopodPerkPlanner.EvaluatePerk231Grant(1L, 225, false,
                   Topology(1), false, 0.0).Status == AsyncPerkGrantStatus.WaitingForFilter,
            "filter denial cannot complete asynchronous delivery");
        var waiting = ItopodPerkPlanner.EvaluatePerk231Grant(1L, 225, false,
            Topology(1), true, 42.0);
        Assert(waiting.Status == AsyncPerkGrantStatus.WaitingForChecker
               && waiting.NextCheckerEtaSeconds == 30.0,
            "checker ETA is bounded to its native 30-second cadence");
        Assert(ItopodPerkPlanner.EvaluatePerk231Grant(1L, 225, false,
                   Topology(1), true, 0.0).Status == AsyncPerkGrantStatus.EligibleOnNextChecker,
            "boss/capacity/filter-ready source waits for actual checker delivery");
        var delivered = ItopodPerkPlanner.EvaluatePerk231Grant(1L, 225, true,
            Topology(0), false, 20.0);
        Assert(delivered.Status == AsyncPerkGrantStatus.Delivered
               && delivered.ReservedOrdinarySlots == 0,
            "only ordinary physical item 482 releases the asynchronous slot");
    }

    private static Move69Snapshot Move(int used, double timer, int itemCount,
        bool idle, bool moveCheck, int freeSlots, string epoch, string component)
    {
        return new Move69Snapshot(true, used, timer, itemCount, idle, moveCheck,
            true, true, MoveCapacity(freeSlots), epoch, component, "filters-A");
    }

    private static void TestMove69PolicyTimerAndRestart()
    {
        var fresh = Move(0, 0.0, 0, true, true, 1, "p1", "c1");
        var freshDecision = Move69Manager.Evaluate(fresh);
        Assert(freshDecision.Kind == Move69DecisionKind.ChargeInCurrentMode,
            "idle mode charges and does not force 69 hours of manual combat");
        Near(freshDecision.CompletionEtaSeconds, 69.0 * 3600.0, 0.0,
            "fresh theoretical lower bound is 69 live hours");
        Assert(Move69Manager.Evaluate(Move(10, 3600.0, 0, false, true, 1,
                   "p1", "c1")).Kind == Move69DecisionKind.ChargeInCurrentMode,
            "timer exactly 3600 is not usable because native predicate is strict greater-than");
        var readyIdle = Move69Manager.Evaluate(Move(10, 3600.001, 0, true, true, 1,
            "p1", "c1"));
        Assert(readyIdle.Kind == Move69DecisionKind.ReadyForOneUse
               && readyIdle.TemporarilySwitchToManual,
            "ready idle route requests only a temporary manual transition");
        Assert(Move69Manager.Evaluate(new Move69Snapshot(true, 68, 3601.0, 0,
                   true, true, true, true, MoveCapacity(0), "p1", "c1", "filters-A")).Kind
               == Move69DecisionKind.HoldCapacity,
            "68-to-69 item opportunity requires exact ordinary capacity");
        Assert(Move69Manager.Evaluate(Move(69, 3601.0, 0, true, true, 1,
                   "p1", "c1")).Kind == Move69DecisionKind.ReadyForOneUse,
            "used 69 with missing item remains a legal retry instead of deadlocking");
        Assert(Move69Manager.Evaluate(Move(69, 0.0, 1, true, true, 0,
                   "p1", "c1")).Kind == Move69DecisionKind.Complete,
            "route stops only after ordinary item 481 exists");

        var tracker = new Move69TimerTracker();
        Assert(tracker.Observe(Move(20, 1000.0, 0, true, true, 1,
                   "p1", "c1")).Kind == Move69TimerEventKind.FirstObservation,
            "tracker captures initial unsaved timer");
        tracker.CancelScheduledUse("Titan interruption");
        var cancelled = tracker.Observe(Move(20, 1005.0, 0, false, true, 1,
            "p1", "c1"));
        Assert(cancelled.Kind == Move69TimerEventKind.CancelledButStillCharging
               && cancelled.ScheduleCancelled,
            "cancelling an action does not cancel native MOVE69 charge");
        var restarted = tracker.Observe(Move(20, 0.0, 0, true, true, 1,
            "p2", "c2"));
        Assert(restarted.Kind == Move69TimerEventKind.ProcessRestartLostTimer,
            "process/component replacement is explicit restart telemetry");
        Near(restarted.EstimatedLostSeconds, 1005.0, 0.0,
            "restart reports the exact previously observed timer at risk");
    }

    private sealed class FakeMoveRuntime : IMove69Runtime
    {
        internal Move69Snapshot Current;
        internal bool LoseDelivery;
        internal int InvokeCalls;

        public string ExactBindingId { get { return "audited.move69"; } }
        public bool LiveMutationAuthority { get { return true; } }

        public Move69Snapshot Capture()
        {
            return Current;
        }

        public Move69ApplyResult InvokeOneUseWithTemporaryManualMode(RootTransactionToken token)
        {
            InvokeCalls++;
            var before = Current;
            var used = before.Used < 69 ? before.Used + 1 : 69;
            var item = before.OrdinaryItem481Count;
            if (before.DeliveryExpectedOnNextUse && !LoseDelivery) item++;
            Current = new Move69Snapshot(before.Unlocked, used, 0.0, item,
                before.IdleMode, before.MoveCheckPassed, before.ExactBindingAvailable,
                before.FilterAllowsItem481, before.Capacity, before.ProcessEpoch,
                before.ComponentIdentity, before.FilterFingerprint);
            return new Move69ApplyResult(true, "fake exact invocation");
        }
    }

    private static void TestMove69MutationAndRetry()
    {
        var config = new AutopilotConfig();
        var manager = new Move69Manager();
        var disabledRuntime = new FakeMoveRuntime
        {
            Current = Move(68, 3601.0, 0, true, true, 1, "p1", "c1")
        };
        var disabledCoordinator = new MutationCoordinator(() => "save-A/run-disabled");
        using (var root = disabledCoordinator.BeginRoot("move69-disabled", config).Transaction)
        {
            var held = manager.ExecuteOneReadyUse(root, disabledRuntime);
            Assert(held.Decision.Kind == Move69DecisionKind.HoldLiveAuthority
                   && disabledRuntime.InvokeCalls == 0,
                "manager defaults live MOVE69 execution off until integration/backtest");
        }

        manager.EnableLiveExecutionForIntegratedCaller(true);
        var runtime = new FakeMoveRuntime
        {
            Current = Move(68, 3601.0, 0, true, true, 1, "p1", "c1")
        };
        var coordinator = new MutationCoordinator(() => "save-A/run-1");
        using (var root = coordinator.BeginRoot("move69-delivery", config).Transaction)
        {
            var result = manager.ExecuteOneReadyUse(root, runtime);
            Assert(result.Mutation.Kind == MutationResultKind.Committed
                   && result.Delivery == Move69DeliveryOutcome.Delivered,
                "68-to-69 exact timer/use/item state commits through task-1 protocol");
            Assert(runtime.InvokeCalls == 1 && result.Mutation.After.IdleMode,
                "one scheduling pass invokes one atom and restores ambient idle mode");
        }

        var lostRuntime = new FakeMoveRuntime
        {
            Current = Move(68, 3601.0, 0, true, true, 1, "p1", "c-loss"),
            LoseDelivery = true
        };
        var lostCoordinator = new MutationCoordinator(() => "save-A/run-2");
        using (var root = lostCoordinator.BeginRoot("move69-lost", config).Transaction)
        {
            var lost = manager.ExecuteOneReadyUse(root, lostRuntime);
            Assert(lost.Mutation.Kind == MutationResultKind.Committed
                   && lost.Delivery == Move69DeliveryOutcome.RetryAfterCooldown
                   && lost.Mutation.After.Used == 69,
                "verified lost final item is classified for cooldown retry, not deadlocked/quarantined");
        }
        lostRuntime.LoseDelivery = false;
        lostRuntime.Current = Move(69, 3601.0, 0, true, true, 1, "p1", "c-loss");
        var retryCoordinator = new MutationCoordinator(() => "save-A/run-2");
        using (var root = retryCoordinator.BeginRoot("move69-retry", config).Transaction)
        {
            var retry = manager.ExecuteOneReadyUse(root, lostRuntime);
            Assert(retry.Mutation.Kind == MutationResultKind.Committed
                   && retry.Delivery == Move69DeliveryOutcome.Delivered
                   && retry.Mutation.After.Used == 69,
                "post-69 native use retries item 481 without incrementing saved use count");
        }
    }

    private static void TestLiveRouteWiring()
    {
        Assert(ItopodEntryRecoveryPolicy.RequiresFullHp(0.0, 2000.0)
               && ItopodEntryRecoveryPolicy.RequiresFullHp(1750.0, 2000.0)
               && !ItopodEntryRecoveryPolicy.RequiresFullHp(1999.5, 2000.0)
               && !ItopodEntryRecoveryPolicy.RequiresFullHp(2000.0, 2000.0),
            "ITOPOD Safe-Zone entry waits for full HP with only a one-HP float tolerance");
        var manager = File.ReadAllText("source/Autopilot/AutopilotManager.cs");
        Assert(manager.Contains("if (Main.Character.settings.itopodOn)")
               && !manager.Contains("route.Climbing || Main.Character.adventure.titan4Kills > 0"),
            "a confirmed native ITOPOD unlock executes fixed farms before T4 instead of falling through to ordinary zones");
        Assert(manager.Contains("_adventureTarget = new ZoneTarget {Zone = 1000")
               && manager.Contains("combat.MoveToZone(-1);")
               && manager.Contains("!ProgressionLoadoutOptimizer.PrepareItopodRoute())"),
            "ITOPOD staging publishes the target, forces a Safe-Zone frame, then equips the route set");
        Assert(manager.Contains("var manualItopod = Main.Settings.ITOPODCombatMode != 1")
               && manager.Contains("move69Pending || manualItopod ? 2 : 0"),
            "manual ITOPOD configuration controls steady farms, not only first-clear climbs");
        Assert(manager.Contains("combat.ManualZone(1000, false, true, false, true")
               && manager.Contains("native ten-kill floor counter"),
            "continuous ITOPOD combat does not reset its floor counter to recycle pre-cast buffs");
        var combatManager = File.ReadAllText("source/Managers/CombatManager.cs");
        Assert(combatManager.Contains("recoverHealth && zone != 1000")
               && combatManager.Contains("itopodKillCount == 0")
               && combatManager.Contains("if (CastHeal()) return;")
               && combatManager.Contains("if (CastHyperRegen()) return;"),
            "ITOPOD recovery heals in place at a floor boundary and never takes a voluntary Safe-Zone hop");
        Assert(Count(combatManager, "zone == 1000 && NeedsFullItopodRecovery()") == 2
               && combatManager.Contains("Recovering to full Adventure HP before entering ITOPOD")
               && combatManager.Contains("if (CastHeal()) return;")
               && combatManager.Contains("if (CastHyperRegen()) return;")
               && manager.Contains("\\\"itopodRequiresFullHpOnEntry\\\": true"),
            "manual and idle ITOPOD retries hold Safe Zone for full HP and publish that exact policy");
        var loadout = File.ReadAllText("source/Managers/ProgressionLoadoutOptimizer.cs");
        Assert(loadout.Contains("if (itopodObjective)")
               && loadout.Contains("if (_leaseKind != \"itopod\") ClearObjectiveLease();")
               && loadout.Contains("different combat systems"),
            "an active ITOPOD route preempts stale selected-boss gear ownership");
        Assert(loadout.Contains("projection.AdventureCurrentHp - combat.WorstIncomingDamage")
               && loadout.Contains("objective.Projection.ItopodClimbing ? combatTieBreaker")
               && loadout.Contains("var rankedTotal = objective.Projection.ItopodClimbing")
               && loadout.Contains("MateriallyBetterForObjective(boundObjective")
               && loadout.Contains("candidate.TieBreaker + 1e-6 < current.TieBreaker"),
            "equal-cycle record-climb sets prefer combat reserve and can replace production gear");
        Assert(loadout.Contains("StrongestAdventureAttackPlan(c, all)")
               && loadout.Contains("No owned physical set proves the configured ITOPOD combat floor")
               && loadout.Contains("ItopodTargetAttackFactor = ZoneHelpers.ItopodTargetAttackFactor()")
               && loadout.Contains("targetAttack = projection.AdventureAttack")
               && loadout.Contains("objective.Projection.ItopodClimbing || combat.OneHit")
               && loadout.Contains("if (!route.Climbing && liveReach < targetFloor)")
               && loadout.Contains("route.Climbing ? route.End - 1 : route.FarmFloor"),
            "a search miss uses strongest Attack under the target Beast state while only steady farming has a modeled reach gate");
        var zones = File.ReadAllText("source/Managers/ZoneHelpers.cs");
        Assert(zones.Contains("training[0] >= 5000")
               && zones.Contains("manual ? c.regAttackPower() : c.idleAttackPower()")
               && !zones.Contains("c.training.attackTraining[1] == 0"),
            "ITOPOD reach uses the exact Regular Attack unlock and matches the configured executor mode");
        Assert(zones.Contains("result.FrontierFloor = frontier")
               && zones.Contains("result.ModeledPositiveDamageFloor = modeledPositiveDamage")
               && zones.Contains("ItopodTrials.Decide(highest, maxFloor")
               && zones.Contains("trialDecision.TargetRecord")
               && zones.Contains("var farm = Math.Max(0, Math.Min(reachable, highest - 1))")
               && manager.Contains("var valuedItopodReach = route.Climbing")
               && manager.Contains("valuedItopodReach,")
               && manager.Contains("route.TargetKillSeconds")
               && manager.Contains("\\\"itopodReachableOneHitFloor\\\"")
               && manager.Contains("\\\"itopodFrontierFloor\\\"")
               && manager.Contains("\\\"itopodModeledPositiveDamageFloor\\\"")
               && manager.Contains("\\\"itopodModelLimitsClimb\\\": false")
               && manager.Contains("\\\"itopodFailureLimit\\\"")
               && manager.Contains("\\\"itopodNoProgressSeconds\\\"")
               && manager.Contains("\\\"itopodRetryImprovementFraction\\\"")
               && manager.Contains("\\\"itopodFailureFloor\\\"")
               && manager.Contains("\\\"itopodBlockedFloor\\\"")
               && manager.Contains("\\\"itopodEmpiricalTrial\\\""),
            "open pushes value the next decade while telemetry keeps farm, diagnostic, and breaker state separate");
        Assert(combatManager.Contains("_fightItopodFloor = zone >= 1000")
               && combatManager.Contains("ZoneHelpers.RecordItopodFightResult(_fightItopodFloor, died)")
               && zones.Contains("ItopodTrials.Observe(foughtFloor, died")
               && combatManager.Contains("ItopodFightProgressWatch")
               && combatManager.Contains("RecordItopodNoProgressFailure(stalledFloor)"),
            "the breaker consumes exact spawn-floor outcomes and a bounded no-HP-progress failure");
        Assert(manager.Contains("Later purchases remain held until ChoosePerk receives")
               && manager.Contains("Do not feed the ITOPOD frontier score a later perk value"),
            "post-sequence PP and frontier value hold instead of using tooltip-name heuristics");
    }

    public static int Main()
    {
        try
        {
            TestContinuousClimbAndSentinelAtom();
            TestExactDeathOrderingAndDecades();
            TestGlobalAdventureRouteValue();
            TestCombatReachAndEmpiricalBreaker();
            TestBoostsClueAndFloorBounds();
            TestOnlineOfflineEstimatorSplit();
            TestTypedPerksAndAsyncDelivery();
            TestMove69PolicyTimerAndRestart();
            TestMove69MutationAndRetry();
            TestLiveRouteWiring();
            Console.WriteLine("ITOPOD/perk tests passed: " + _assertions + " assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}

namespace NGUInjector
{
    internal sealed class SettingsStub
    {
        internal bool GlobalEnabled = true;
    }

    internal static class Main
    {
        internal static NGUInjector.Autopilot.AutopilotManager Autopilot;
        internal static SettingsStub Settings = new SettingsStub();
        internal static void LogAction(string category, string detail) { }
    }
}
