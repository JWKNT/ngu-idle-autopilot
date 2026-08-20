using System;
using System.IO;
using System.Linq;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

EnergyPortfolioTests is the controller-free regression suite for the objective-aware Energy event
auction.  It proves objective selection, exact-gate precedence, persistence preference, ITOPOD NGU
ordering, and deterministic tiers without loading Unity or a save.
*/
internal static class EnergyPortfolioTests
{
    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception(message);
    }

    private static EnergyPortfolioCandidate C(string key, EnergyPortfolioSink sink, int index,
        bool gate, bool persistent, int order)
    {
        return new EnergyPortfolioCandidate(key, sink, index, gate, persistent, order);
    }

    public static int Main()
    {
        Assert(EnergyPortfolioOptimizer.ChooseObjective(true, false, true, true, true)
               == EnergyPortfolioObjective.FightBoss,
            "an active Boss-clear challenge owns the Energy objective");
        Assert(EnergyPortfolioOptimizer.ChooseObjective(false, true, false, false, false)
               == EnergyPortfolioObjective.FightBoss,
            "an unwinnable selected Boss owns Energy when no discrete route gate preempts it");
        Assert(EnergyPortfolioOptimizer.ChooseObjective(false, true, true, false, false)
               == EnergyPortfolioObjective.Adventure,
            "a due Titan or blocked ITOPOD retry can preempt an ordinary selected-Boss wall");
        Assert(EnergyPortfolioOptimizer.ChooseObjective(false, false, true, true, true)
               == EnergyPortfolioObjective.Gold,
            "an exact Gold working-capital gate outranks an Adventure push");
        Assert(EnergyPortfolioOptimizer.ChooseObjective(false, false, true, false, false)
               == EnergyPortfolioObjective.Adventure,
            "a due Titan or ITOPOD push selects Adventure");
        Assert(EnergyPortfolioOptimizer.ChooseObjective(false, false, false, false, false)
               == EnergyPortfolioObjective.PermanentGrowth,
            "a run without a live gate compounds permanent growth");

        var boss = EnergyPortfolioOptimizer.Rank(EnergyPortfolioObjective.FightBoss, new[]
        {
            C("wish", EnergyPortfolioSink.Wish, 0, false, true, 0),
            C("aug", EnergyPortfolioSink.Augment, 0, false, false, 1),
            C("wandoos", EnergyPortfolioSink.Wandoos, 0, false, false, 2),
            C("at-gate", EnergyPortfolioSink.AdvancedTraining, 1, true, false, 3)
        }).ToArray();
        Assert(boss[0].Candidate.Key == "at-gate" && boss[0].Tier == 0,
            "a source-proved finite gate beats heuristic sink weights");
        Assert(boss[1].Candidate.Key == "aug" && boss[2].Candidate.Key == "wandoos",
            "Boss progression funds a finishable Augment event before Wandoos");

        var adventure = EnergyPortfolioOptimizer.Rank(EnergyPortfolioObjective.Adventure, new[]
        {
            C("aug", EnergyPortfolioSink.Augment, 0, false, false, 0),
            C("generic-ngu", EnergyPortfolioSink.Ngu, 0, false, true, 1),
            C("adventure-ngu", EnergyPortfolioSink.Ngu, 4, false, true, 2),
            C("drop-ngu", EnergyPortfolioSink.Ngu, 6, false, true, 3),
            C("at", EnergyPortfolioSink.AdvancedTraining, 1, false, false, 4)
        }).ToArray();
        Assert(adventure.Select(x => x.Candidate.Key).SequenceEqual(
                new[] {"at", "adventure-ngu", "drop-ngu", "generic-ngu", "aug"}),
            "Adventure progression ranks AT and its permanent NGUs above reset-local Augments");

        var permanent = EnergyPortfolioOptimizer.Rank(EnergyPortfolioObjective.PermanentGrowth,
            new[]
            {
                C("aug", EnergyPortfolioSink.Augment, 0, false, false, 0),
                C("ngu", EnergyPortfolioSink.Ngu, 4, false, true, 1),
                C("wish", EnergyPortfolioSink.Wish, 0, false, true, 2)
            }).ToArray();
        Assert(permanent[0].Candidate.Key == "wish"
               && permanent[1].Candidate.Key == "ngu"
               && permanent[2].Candidate.Key == "aug",
            "permanent mode cannot be captured by a long reset-local bar");

        var ties = EnergyPortfolioOptimizer.Rank(EnergyPortfolioObjective.Adventure, new[]
        {
            C("later", EnergyPortfolioSink.Ngu, 4, false, true, 7),
            C("earlier", EnergyPortfolioSink.Ngu, 6, false, true, 3)
        }).ToArray();
        Assert(ties[0].Candidate.Key == "earlier",
            "equal event tiers retain deterministic profile order");

        var firstAt = EnergyPortfolioOptimizer.AdvancedTrainingLevelForRelativeGain(0L, .05);
        Assert(firstAt == 1L
               && EnergyPortfolioOptimizer.AdvancedTrainingBonus(firstAt) >= 1.05,
            "the first AT retry event is the exact first level, not a fixed Energy share");
        var laterAt = EnergyPortfolioOptimizer.AdvancedTrainingLevelForRelativeGain(100L, .05);
        Assert(laterAt > 100L
               && EnergyPortfolioOptimizer.AdvancedTrainingBonus(laterAt)
                  / EnergyPortfolioOptimizer.AdvancedTrainingBonus(100L) >= 1.05,
            "later ITOPOD retry targets solve the level needed for a five-percent capability gain");

        var allocation = File.ReadAllText("source/AllocationProfiles/CustomAllocation.cs");
        var objective = allocation.IndexOf("CurrentEnergyPortfolioObjective(_character, temp)",
            StringComparison.Ordinal);
        var groups = allocation.IndexOf(".GroupBy(x => x.Ranked.Tier)", objective,
            StringComparison.Ordinal);
        var nativeDispatch = allocation.IndexOf("prio.Allocate();", groups,
            StringComparison.Ordinal);
        Assert(objective >= 0 && groups > objective && nativeDispatch > groups,
            "the live Energy sweep must dispatch native allocations by objective event tier");
        Assert(allocation.Contains("FirstOrDefault(y => !y.IsCapPrio())")
               && allocation.Contains("x.PortfolioKey"),
            "serialized capped/fallback aliases must collapse to one logical Energy sink");
        Assert(allocation.Contains("energyObjective != EnergyPortfolioObjective.Adventure")
               && allocation.Contains("!(x is AdvancedTrainingBP)"),
            "a preliminary AT candidate cannot bypass the final Gold/Boss portfolio objective");

        var advanced = File.ReadAllText(
            "source/AllocationProfiles/BreakpointTypes/AdvancedTrainingBP.cs");
        Assert(advanced.Contains("TryGetTitanProgressionTarget")
               && advanced.Contains("TryGetItopodRetryTarget")
               && advanced.Contains("ItopodClimbTrialController.FailureStreakLimit")
               && advanced.Contains("AdvancedTrainingLevelForRelativeGain"),
            "live AT admission must cover source-proved Titan and empirical ITOPOD retry events");

        Console.WriteLine("PASS: " + _assertions + " Energy portfolio assertions");
        return 0;
    }
}
