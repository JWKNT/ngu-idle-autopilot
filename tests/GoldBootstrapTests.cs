/*
FILE PURPOSE

Purpose: Pure regression coverage for the post-rebirth ordinary-Adventure Gold bootstrap policy.
The suite verifies exact drop constants, Boss-30 record semantics, horizon gates, and the handoff
back to ITOPOD after either liquid Gold or passive GPS resolves the deadlock.

Mechanism: Immutable numeric snapshots exercise GoldBootstrapPlanner without Unity or a game save.
Output is an assertion count or a thrown exception naming the failed policy invariant.

Invariants and safety: Tests never load or mutate NGU Idle. Titan zones have no ordinary base-Gold
entry, admission uses the native 4x random lower endpoint, and zero-GPS farming requires a concrete
finishable sink.
*/
using System;
using System.IO;
using NGUInjector.Autopilot;

internal static class GoldBootstrapTests
{
    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static void Near(double actual, double expected, string message)
    {
        Assert(Math.Abs(actual - expected) <= Math.Max(1e-9, Math.Abs(expected) * 1e-12),
            message + " (actual " + actual + ", expected " + expected + ")");
    }

    private static GoldBootstrapSnapshot Base()
    {
        return new GoldBootstrapSnapshot
        {
            CurrentGold = 0.0,
            SinkCost = 5000000.0,
            SinkName = "Augment level",
            SelectedBoss = 12,
            HighestBoss = 59,
            RemainingSeconds = 1800.0,
            TargetZone = 5,
            TargetFightType = 2,
            TargetMobBaseGold = GoldBootstrapPlanner.OrdinaryMobBaseGold(5),
            TotalGoldDropMultiplier = 1.0,
            KillCycleSeconds = 3.0
        };
    }

    private static void TestPinnedBaseGoldTable()
    {
        Near(GoldBootstrapPlanner.OrdinaryMobBaseGold(4), 4000.0,
            "Sky normal enemies must use the shipped 4,000 base Gold");
        Near(GoldBootstrapPlanner.OrdinaryMobBaseGold(5), 10000.0,
            "HSB normal enemies must use the shipped 10,000 base Gold");
        Near(GoldBootstrapPlanner.OrdinaryMobBaseGold(43), 8e16,
            "Pirate Ship lower ordinary base Gold must remain build-pinned");
        Near(GoldBootstrapPlanner.OrdinaryMobBaseGold(6), 0.0,
            "Titan zones must not masquerade as ordinary Gold routes");
    }

    private static void TestNoSinkNeverDetours()
    {
        var s = Base();
        s.SinkCost = 0.0;
        Assert(!GoldBootstrapPlanner.Evaluate(s).ShouldRoute,
            "zero Gold alone must not steal Adventure from ITOPOD");
    }

    private static void TestPreBossThirtyLiquidBootstrap()
    {
        var d = GoldBootstrapPlanner.Evaluate(Base());
        Assert(d.ShouldRoute && d.Mode == GoldBootstrapMode.LiquidGold,
            "before Boss 30, an unfunded finishable Augment must farm liquid Gold");
        Near(d.ConservativeDrop, 40000.0,
            "admission must use the exact native 4x lower random endpoint");
        Near(d.EtaSeconds, 375.0,
            "liquid ETA must count enough complete conservative enemy drops");
    }

    private static void TestLiquidFundingHandsBackToItopod()
    {
        var s = Base();
        s.CurrentGold = s.SinkCost;
        Assert(!GoldBootstrapPlanner.Evaluate(s).ShouldRoute,
            "enough liquid Gold must immediately release ordinary Adventure");
    }

    private static void TestBossThirtySeedsGps()
    {
        var s = Base();
        s.SelectedBoss = 30;
        var d = GoldBootstrapPlanner.Evaluate(s);
        Assert(d.ShouldRoute && d.Mode == GoldBootstrapMode.SeedTimeMachine,
            "selected Boss 30 plus zero base record must request one ordinary drop");
        Near(d.ConservativeGps, 1280000.0,
            "HSB minimum record times the shipped highest-Boss multiplier must bound GPS");
        Assert(d.EtaSeconds < 7.0,
            "one HSB drop must seed and fund the first 5M sink within seconds");
    }

    private static void TestExistingGpsKeepsItopodPriority()
    {
        var s = Base();
        s.SelectedBoss = 30;
        s.BaseGoldRecord = 40000.0;
        s.GrossGoldPerSecond = 1280000.0;
        Assert(!GoldBootstrapPlanner.Evaluate(s).ShouldRoute,
            "positive passive Gold must own later shortfalls without ordinary farming");
    }

    private static void TestTimeMachineChallengeUsesLiquidOnly()
    {
        var s = Base();
        s.SelectedBoss = 30;
        s.TimeMachineChallenge = true;
        var d = GoldBootstrapPlanner.Evaluate(s);
        Assert(d.ShouldRoute && d.Mode == GoldBootstrapMode.LiquidGold
               && d.ConservativeGps == 0.0,
            "Time Machine challenge must never claim that a drop creates passive GPS");
    }

    private static void TestHorizonGate()
    {
        var s = Base();
        s.RemainingSeconds = 400.0;
        Assert(!GoldBootstrapPlanner.Evaluate(s).ShouldRoute,
            "a Gold detour without a 30-second payoff window must hold");
        Assert(GoldBootstrapPlanner.HasPayoffWindow(20.0, 50.0),
            "a completion with the minimum payoff window is useful");
        Assert(!GoldBootstrapPlanner.HasPayoffWindow(20.0, 49.9),
            "the payoff boundary must be strict about missing horizon");
    }

    private static void TestLiveIntegrationOrderAndGoldLoadout()
    {
        var manager = File.ReadAllText("source/Autopilot/AutopilotManager.cs");
        var bootstrap = manager.IndexOf("_goldBootstrapDecision = EvaluateGoldBootstrap",
            StringComparison.Ordinal);
        var routeCache = manager.IndexOf("if (_adventureTarget == null ||",
            bootstrap, StringComparison.Ordinal);
        var itopod = manager.IndexOf("ZoneHelpers.ConfigureITOPOD()",
            bootstrap, StringComparison.Ordinal);
        Assert(bootstrap >= 0 && routeCache > bootstrap && itopod > routeCache,
            "live Gold bootstrap must preempt the ordinary route cache and ITOPOD selector");
        Assert(manager.Contains("ProgressionLoadoutOptimizer.SetAdventureRouteObjective(\n                    _goldBootstrapDecision.TargetZone, false, false, false, true)"),
            "live route must publish a distinct Gold-bootstrap loadout objective");
        Assert(manager.Contains("c.machine.realBaseGold"),
            "live admission and telemetry must observe the native Time Machine base record");
        Assert(manager.Contains("c.totalGoldbonus()"),
            "live admission must use the native total Adventure Gold-drop multiplier");

        var loadout = File.ReadAllText("source/Managers/ProgressionLoadoutOptimizer.cs");
        Assert(loadout.Contains("LoadoutObjectiveKind.GoldBootstrap"),
            "equipment selection must have a named Gold-bootstrap objective");
        Assert(loadout.Contains("specType.GoldDropAmount")
               && loadout.Contains("specType.GoldDrop2"),
            "Gold loadout must use the two specials in native totalGoldbonus");
        Assert(loadout.Contains("meanSeconds = trialSeconds / Math.Max(1e-9, goldRatio)"),
            "Gold loadout must rank safe complete sets by guaranteed drop rate");
    }

    public static int Main()
    {
        TestPinnedBaseGoldTable();
        TestNoSinkNeverDetours();
        TestPreBossThirtyLiquidBootstrap();
        TestLiquidFundingHandsBackToItopod();
        TestBossThirtySeedsGps();
        TestExistingGpsKeepsItopodPriority();
        TestTimeMachineChallengeUsesLiquidOnly();
        TestHorizonGate();
        TestLiveIntegrationOrderAndGoldLoadout();
        Console.WriteLine("Gold bootstrap assertions passed: " + _assertions);
        return 0;
    }
}
