using System;
using System.Collections.Generic;
using System.Globalization;
using NGUInjector.Managers;

/*
FILE PURPOSE

ProgressionGoalEngine converts mechanics into structured roadmap nodes with progress, targets,
ETAs, and dependencies. Nodes explain projections; they do not authorize mutations. Keep boss
scope, unlocks, and set events truthful so the monitor never labels catch-up as record progress.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class GoalNode
    {
        internal string Id;
        internal string Label;
        internal string Family;
        internal double Current;
        internal double Target;
        internal int EtaSeconds = -1;
        internal string Priority;
    }

    internal static class ProgressionGoalEngine
    {
        // These mirror the game's canonical achievement thresholds: 96 resource
        // milestones plus 30 boss milestones. Feature, Titan, training, challenge,
        // and difficulty nodes below take the catalog well beyond one hundred events.
        private static readonly double[] PowerThresholds =
        {
            10, 30, 100, 300, 1e3, 3e3, 1e4, 3e4, 1e5, 3e5, 1e6, 3e6, 1e7, 3e7, 1e8, 3e8
        };

        private static readonly double[] CapThresholds =
        {
            1e4, 1e5, 3e5, 1e6, 3e6, 1e7, 3e7, 1e8, 3e8, 1e9, 3e9, 1e10, 3e10, 1e11, 3e11, 1e12
        };

        private static readonly double[] BarThresholds =
        {
            3, 10, 30, 100, 300, 1e3, 3e3, 1e4, 3e4, 1e5, 3e5, 1e6, 3e6, 1e7, 3e7, 1e8
        };

        internal static List<GoalNode> ActiveGoals(Character c, string trainingGoal, int trainingEta,
            int bossViabilityEta, int rebirthTarget, string rebirthReason)
        {
            var goals = new List<GoalNode>();
            AddNext(goals, "energy-power", "Energy Power milestone", "resources", c.totalEnergyPower(), PowerThresholds, "throughput");
            AddNext(goals, "energy-cap", "Energy Cap milestone", "resources", c.totalCapEnergy(), CapThresholds, "throughput");
            AddNext(goals, "energy-bars", "Energy Bars milestone", "resources", c.totalEnergyBar(), BarThresholds, "throughput");
            if (c.highestBoss >= 37)
            {
                AddNext(goals, "magic-power", "Magic Power milestone", "resources", c.totalMagicPower(), PowerThresholds, "throughput");
                AddNext(goals, "magic-cap", "Magic Cap milestone", "resources", c.totalCapMagic(), CapThresholds, "throughput");
                AddNext(goals, "magic-bars", "Magic Bars milestone", "resources", c.totalMagicBar(), BarThresholds, "throughput");
            }

            var activeHighestBoss = c.settings.rebirthDifficulty == difficulty.normal ? c.highestBoss
                : c.settings.rebirthDifficulty == difficulty.evil ? c.highestHardBoss
                : c.highestSadisticBoss;
            var nextBossDecade = Math.Min(300, (activeHighestBoss / 10 + 1) * 10);
            var selectedMatchesRecord = c.bossID == activeHighestBoss;
            if (activeHighestBoss < 300)
                goals.Add(new GoalNode {Id = "boss-next", Label = "Defeat Fight Boss " + (activeHighestBoss + 1),
                    Family = "boss", Current = activeHighestBoss, Target = activeHighestBoss + 1,
                    EtaSeconds = selectedMatchesRecord ? bossViabilityEta : -1, Priority = "immediate"});
            if (!selectedMatchesRecord && c.bossID < activeHighestBoss)
                goals.Add(new GoalNode {Id = "boss-catch-up", Label = "Catch up Fight Boss " + (c.bossID + 1)
                    + " toward record target " + (activeHighestBoss + 1), Family = "boss",
                    Current = c.bossID, Target = activeHighestBoss, EtaSeconds = bossViabilityEta,
                    Priority = "immediate"});
            if (activeHighestBoss < 300)
                goals.Add(new GoalNode {Id = "boss-" + nextBossDecade, Label = "Defeat Fight Boss " + nextBossDecade,
                    Family = "boss", Current = activeHighestBoss, Target = nextBossDecade, Priority = "gate"});

            goals.Add(new GoalNode {Id = "training-next", Label = trainingGoal, Family = "training",
                Current = 0, Target = 1, EtaSeconds = trainingEta, Priority = "gate"});
            goals.Add(new GoalNode {Id = "rebirth-current", Label = "Rebirth: " + rebirthReason, Family = "rebirth",
                Current = c.rebirthTime.totalseconds, Target = rebirthTarget,
                EtaSeconds = Math.Max(0, rebirthTarget - (int)c.rebirthTime.totalseconds), Priority = "gate"});

            AddFeatureGoals(c, goals);
            return goals;
        }

        private static void AddFeatureGoals(Character c, ICollection<GoalNode> goals)
        {
            AddFeature(goals, "unlock-adventure", "Unlock Adventure", c.highestBoss >= 4, c.highestBoss, 4);
            AddFeature(goals, "unlock-augments", "Unlock Augments and custom EXP purchases", c.highestBoss >= 17, c.highestBoss, 17);
            AddFeature(goals, "unlock-tm", "Unlock Time Machine and Money Pit gold loop", c.highestBoss >= 30, c.highestBoss, 30);
            AddFeature(goals, "unlock-magic", "Unlock Magic and Blood Magic", c.highestBoss >= 37, c.highestBoss, 37);
            AddFeature(goals, "unlock-ngu", "Complete the Number set and unlock NGUs",
                c.inventory.itemList.numberComplete || c.settings.nguOn, c.inventory.itemList.numberComplete ? 1 : 0, 1);
            var items = c.inventory.itemList;
            if (items.waldoComplete || c.adventure.titan6Unlocked)
            {
                AddFeature(goals, "t6-clue-1", "Complete Titan 6 clue 1", c.adventure.clue1Complete, c.adventure.clue1Complete ? 1 : 0, 1);
                AddFeature(goals, "t6-clue-2", "Complete Titan 6 clue 2", c.adventure.clue2Complete, c.adventure.clue2Complete ? 1 : 0, 1);
                AddFeature(goals, "t6-clue-3", "Complete Titan 6 clue 3", c.adventure.clue3Complete, c.adventure.clue3Complete ? 1 : 0, 1);
                AddFeature(goals, "t6-clue-4", "Complete Titan 6 clue 4", c.adventure.clue4Complete, c.adventure.clue4Complete ? 1 : 0, 1);
            }
            if (items.beast1complete && !c.adventure.titan7questStarted)
                AddFeature(goals, "t7-quest-start", "Defeat Greasy Nerd's Mom in zone 19 to start Titan 7's puzzle",
                    false, 0, 1);
            else if (c.adventure.titan7questStarted && !c.adventure.titan7questComplete)
            {
                var sequence = Math.Max(0, Math.Min(4, c.adventure.titan7QuestSequence));
                var letters = new[] {"F", "A", "R", "T", "S"};
                var bosses = new[] {24, 41, 62, 81, 120};
                goals.Add(new GoalNode {Id = "t7-farts-" + sequence,
                    Label = "Titan 7 puzzle: enter " + letters[sequence] + " at Fight Boss " + bosses[sequence],
                    Family = "puzzle", Current = c.adventure.titan7QuestSequence, Target = sequence + 1,
                    Priority = "critical"});
            }
            if (c.adventure.titan8questStarted && !c.adventure.titan8Unlocked)
            {
                AddFeature(goals, "t8-note-skeleton", "Death Note: defeat Skeleton in zone 2",
                    c.adventure.skeletonWhacked, c.adventure.skeletonWhacked ? 1 : 0, 1);
                AddFeature(goals, "t8-note-icarus", "Death Note: defeat Icarus Proudbottom in zone 4",
                    c.adventure.icarusWhacked, c.adventure.icarusWhacked ? 1 : 0, 1);
                AddFeature(goals, "t8-note-circle", "Death Note: defeat King Circle in zone 9",
                    c.adventure.kingCircleWhacked, c.adventure.kingCircleWhacked ? 1 : 0, 1);
                AddFeature(goals, "t8-note-empty", "Death Note: defeat the empty-name enemy in zone 10",
                    c.adventure.emptyNameWhacked, c.adventure.emptyNameWhacked ? 1 : 0, 1);
                AddFeature(goals, "t8-note-rob", "Death Note: defeat Rob Boss in zone 15",
                    c.adventure.robBossWhacked, c.adventure.robBossWhacked ? 1 : 0, 1);
            }
            // Per-run titan kill counters reset at every rebirth. Persistent set flags
            // are the correct long-range progression gates for the roadmap.
            if (!items.GRBComplete && c.highestBoss >= 58)
                AddFeature(goals, "titan-1", "Complete " + GameNames.Titan(c, 0) + " set", false, 0, 1);
            else if (items.GRBComplete && !items.seedComplete)
                AddFeature(goals, "titan-2", "Complete " + GameNames.Titan(c, 1) + " seed set", false, 0, 1);
            else if (items.seedComplete && !items.jakeComplete)
                AddFeature(goals, "titan-3", "Complete " + GameNames.Titan(c, 2) + " set", false, 0, 1);
            else if (items.jakeComplete && !items.uugComplete)
                AddFeature(goals, "titan-4", "Complete " + GameNames.Titan(c, 3) + " set", false, 0, 1);
            else if (items.uugComplete && !items.waldoComplete)
                AddFeature(goals, "titan-5", "Complete " + GameNames.Titan(c, 4) + " set", false, 0, 1);
            else if (items.waldoComplete && !items.beast1complete)
                AddFeature(goals, "titan-6", "Complete " + GameNames.Titan(c, 5) + " set", false, 0, 1);
            else if (items.beast1complete && !items.nerdComplete)
                AddFeature(goals, "titan-7", "Complete " + GameNames.Titan(c, 6) + " set", false, 0, 1);
            else if (items.nerdComplete && !items.godmotherComplete)
                AddFeature(goals, "titan-8", "Complete " + GameNames.Titan(c, 7) + " set", false, 0, 1);
            else if (items.godmotherComplete && !items.exileComplete)
                AddFeature(goals, "titan-9", "Complete " + GameNames.Titan(c, 8) + " set", false, 0, 1);
            else if (items.exileComplete && !items.spaceComplete)
                AddFeature(goals, "titan-10", "Complete " + GameNames.Titan(c, 9) + " Space set", false, 0, 1);
            else if (items.spaceComplete && !items.rockLobsterComplete)
                AddFeature(goals, "titan-11", "Complete " + GameNames.Titan(c, 10) + " set", false, 0, 1);
            else if (items.rockLobsterComplete && !items.amalgamateComplete)
                AddFeature(goals, "titan-12", "Complete " + GameNames.Titan(c, 11) + " set", false, 0, 1);
        }

        private static void AddFeature(ICollection<GoalNode> goals, string id, string label, bool complete,
            double current, double target)
        {
            if (!complete)
                goals.Add(new GoalNode {Id = id, Label = label, Family = "progression", Current = current,
                    Target = target, Priority = "critical"});
        }

        private static void AddNext(ICollection<GoalNode> goals, string id, string label, string family,
            double current, IEnumerable<double> thresholds, string priority)
        {
            foreach (var target in thresholds)
            {
                if (current >= target) continue;
                goals.Add(new GoalNode {Id = id + "-" + target.ToString("0", CultureInfo.InvariantCulture), Label = label,
                    Family = family, Current = current, Target = target, Priority = priority});
                return;
            }
        }

        internal static string ToJson(IEnumerable<GoalNode> goals)
        {
            var parts = new List<string>();
            foreach (var goal in goals)
            {
                parts.Add("{\"id\":\"" + Escape(goal.Id) + "\",\"label\":\"" + Escape(goal.Label)
                          + "\",\"family\":\"" + Escape(goal.Family) + "\",\"current\":"
                          + goal.Current.ToString("R", CultureInfo.InvariantCulture) + ",\"target\":"
                          + goal.Target.ToString("R", CultureInfo.InvariantCulture) + ",\"etaSeconds\":"
                          + goal.EtaSeconds + ",\"priority\":\"" + Escape(goal.Priority) + "\"}");
            }
            return "[" + string.Join(",", parts.ToArray()) + "]";
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
