using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.AllocationProfiles.RebirthStuff;

/*
FILE PURPOSE

AutopilotPlanner composes a Character snapshot and config into a progression stage, resource
breakpoints, and rebirth recommendation. Exact subsystem formulas stay in breakpoints/managers;
this layer sequences them. Prefer live events over fixed chapter-clock schedules.
*/
namespace NGUInjector.Autopilot
{
    internal static class AutopilotPlanner
    {
        internal static AutopilotPlan Build(Character c, AutopilotConfig config)
        {
            var currentDifficulty = c.settings.rebirthDifficulty;
            if (currentDifficulty == difficulty.sadistic)
                return BuildSadistic(c, config);
            if (currentDifficulty == difficulty.evil)
                return BuildEvil(c, config);
            return BuildNormal(c, config);
        }

        private static AutopilotPlan BuildNormal(Character c, AutopilotConfig config)
        {
            var list = c.inventory.itemList;
            var nguUnlocked = list.numberComplete || c.settings.nguOn;
            var t4Defeated = list.uugComplete;
            var t1Defeated = list.GRBComplete;
            var plan = NewPlan(c);

            plan.NGUDifficulties.Add(new TimedValue {Time = 0, Value = 0});
            plan.WandoosOS = ChooseWandoos(c, difficulty.normal);

            if (!nguUnlocked)
            {
                plan.Stage = "Normal / early game";
                plan.Objective = "reduce Basic Training caps, farm boss EXP, and finish the highest reachable gear set";
                // AP begins only after 4,100 seconds, not at one hour. Before T1 the
                // faster boss-EXP/BT-cap loop wins; once Boss 58 is reached, hold the
                // run for the one-hour GRB spawn instead of resetting underneath it.
                var rebirth = RebirthOptimizer.EarlyNormal(c);
                plan.RebirthSeconds = rebirth.TargetSeconds;
                plan.RebirthReason = rebirth.Reason;
                plan.RebirthRunnerUpSeconds = rebirth.RunnerUpSeconds;
                plan.RebirthRunnerUpDeltaSeconds = rebirth.RunnerUpDeltaSeconds;
                plan.RebirthRunnerUpReason = rebirth.RunnerUpReason;
                plan.RebirthSelectedScorePerHour = rebirth.SelectedScorePerHour;
                plan.RebirthRunnerUpScorePerHour = rebirth.RunnerUpScorePerHour;
                plan.RebirthProjectedMultiplier = rebirth.ProjectedMultiplier;
                plan.RebirthProjectedAP = rebirth.ProjectedAP;
                plan.RebirthCandidateSummary = rebirth.CandidateSummary;
                plan.RebirthCandidateCount = rebirth.CandidateCount;
                plan.RebirthRecoveryMode = rebirth.RecoveryMode;
                plan.RebirthRecoveryEtaSeconds = rebirth.RecoveryEtaSeconds;
                plan.RebirthRecoveryRemainingBosses = rebirth.RecoveryRemainingBosses;
                plan.RebirthRecoveryReason = rebirth.RecoveryReason;
                plan.RebirthExecutionHold = rebirth.ExecutionHold;
                if (c.highestBoss >= 30)
                {
                    Add(plan.Energy, 0, "CAPALLBT:12", "CAPTM:25", "CAPBESTAUG:28", "CAPAT-1:18", "CAPAT-0:18", "CAPWAN:8", "BESTAUG");
                    Add(plan.Energy, 1500, "CAPALLBT:10", "CAPTM:20", "CAPAT-1:18", "CAPAT-0:18", "CAPWAN:8", "BESTAUG");
                }
                else
                {
                    Add(plan.Energy, 0, "CAPALLBT:15", "CAPBESTAUG:35", "CAPAT-1:20", "CAPAT-0:20", "CAPWAN:10", "BESTAUG");
                    Add(plan.Energy, 1500, "CAPALLBT:12", "CAPAT-1:20", "CAPAT-0:20", "CAPWAN:10", "BESTAUG");
                }
                Add(plan.Magic, 0, "CAPTM:40", "BR", "CAPWAN:10");
                Add(plan.R3, 0, "BESTHACK");
                plan.Diggers = new[] {3, 0, 2};
                return plan;
            }

            plan.Stage = t4Defeated ? "Normal / NGU progression" : "Normal / Titans 1-4";
            plan.Objective = t4Defeated
                ? "grow permanent Adventure and Drop Chance NGUs, Yggdrasil/EXP, beards, and PP"
                : "finish titan sets while building Adventure NGU and Yggdrasil";
            var fruitCycle = HighestFruitMaturitySeconds(c);
            plan.RebirthSeconds = t4Defeated ? 86400 : Math.Max(3600, fruitCycle);
            plan.RebirthReason = t4Defeated
                ? "bank the 24-hour beard conversion maximum together with mature Yggdrasil and Titan events"
                : fruitCycle > 3600
                    ? "harvest the highest unlocked fruit at its exact tier-" + (fruitCycle / 3600)
                      + " maturity boundary instead of erasing partial growth on rebirth"
                    : "hold through the 3,600-second time-multiplier jump and the active Titan spawn cycle";
            plan.RebirthRunnerUpSeconds = t4Defeated ? 82800
                : plan.RebirthSeconds == 3600 ? 4100 : Math.Max(3600, plan.RebirthSeconds - 3600);
            plan.RebirthRunnerUpDeltaSeconds = Math.Abs(plan.RebirthRunnerUpSeconds - plan.RebirthSeconds);
            var t6CluesReady = c.adventure.clue1Complete && c.adventure.clue2Complete
                               && c.adventure.clue3Complete && c.adventure.clue4Complete;
            if (t6CluesReady && !c.adventure.titan6Unlocked)
            {
                var elapsed = (int)Math.Floor(c.rebirthTime.totalseconds);
                if (elapsed <= 2614)
                {
                    plan.RebirthSeconds = elapsed >= 2586 ? elapsed : 2586;
                    plan.RebirthReason = "trigger Titan 6's strict 2,585-2,615-second clue-completion rebirth window";
                    plan.RebirthRunnerUpSeconds = 2615;
                    plan.RebirthRunnerUpDeltaSeconds = Math.Max(0, 2615 - plan.RebirthSeconds);
                }
                else
                {
                    plan.RebirthSeconds = Math.Min(plan.RebirthSeconds, elapsed + 1);
                    plan.RebirthReason = "reset the missed Titan 6 clue window immediately, then target 2,586 seconds next run";
                    plan.RebirthRunnerUpSeconds = 2586;
                    plan.RebirthRunnerUpDeltaSeconds = 0;
                }
                // Starting a challenge is a hard reset and must not preempt this window.
                plan.Challenges.Clear();
            }

            Add(plan.Energy, 0, "CAPALLBT", "CAPTM", "CAPBESTAUG", "CAPAT-1:25", "CAPAT-0:20", "CAPWAN", "NGU-4", "NGU-6");
            Add(plan.Energy, 3600, "CAPALLBT", "CAPAT-1:25", "CAPAT-0:20", "CAPWAN", "NGU-4", "NGU-6");
            Add(plan.Magic, 0, "CAPTM", "BR", "CAPWAN", "NGU-0", "NGU-1");
            Add(plan.Magic, 3600, "BR", "CAPWAN", "NGU-0", "NGU-1");
            Add(plan.R3, 0, "BESTHACK");
            plan.Diggers = new[] {4, 5, 3, 0, 11};
            return plan;
        }

        private static AutopilotPlan BuildEvil(Character c, AutopilotConfig config)
        {
            var plan = NewPlan(c);
            var early = c.highestHardBoss < 125 || !c.adventure.titan7Unlocked;
            var wishes = c.wishes.wishesOn;

            plan.Stage = early ? "Evil / climb to T7" : wishes ? "Evil / wishes and hacks" : "Evil / Titans 7-8";
            plan.Objective = early
                ? "climb bosses with TM and augments, then grow Normal NGUs and finish with Evil NGUs"
                : wishes
                    ? "balance permanent Adventure gains, hack milestones, wishes, quests, and beard banking"
                    : "grow Adventure NGUs and hacks toward the next titan";
            if (!c.adventure.titan7Unlocked && c.highestHardBoss >= 125)
            {
                plan.RebirthSeconds = 16260;
                plan.RebirthReason = "preserve the per-run Adventure clock through the 16,200-second Godmother spawn gate";
            }
            else if (!c.adventure.titan8Unlocked && c.highestHardBoss >= 166)
            {
                plan.RebirthSeconds = 18060;
                plan.RebirthReason = "preserve the per-run Adventure clock through the 18,000-second Exile spawn gate";
            }
            else if (!c.adventure.titan9Unlocked && c.highestHardBoss >= 190)
            {
                plan.RebirthSeconds = 19860;
                plan.RebirthReason = "preserve the per-run Adventure clock through the 19,800-second Titan 9 spawn gate";
            }
            else
            {
                plan.RebirthSeconds = early && c.highestHardBoss < 80 ? 3600 : 86400;
                plan.RebirthReason = plan.RebirthSeconds == 3600
                    ? "take the 3,600-second Number discontinuity while iterating the early Evil boss climb"
                    : "bank the current Evil NGU/hack/beard cycle without resetting an imminent progression window";
            }
            plan.RebirthRunnerUpSeconds = plan.RebirthSeconds == 3600 ? 4100 : 86400;
            plan.RebirthRunnerUpDeltaSeconds = Math.Abs(plan.RebirthRunnerUpSeconds - plan.RebirthSeconds);
            if (c.adventure.titan7questStarted && !c.adventure.titan7questComplete)
            {
                var sequence = Math.Max(0, Math.Min(4, c.adventure.titan7QuestSequence));
                var targetBosses = new[] {24, 41, 62, 81, 120};
                if (c.bossID > targetBosses[sequence])
                {
                    var elapsed = (int)Math.Floor(c.rebirthTime.totalseconds);
                    plan.RebirthSeconds = Math.Max((int)Math.Ceiling((double)c.rebirth.minRebirthTime()), elapsed + 1);
                    plan.RebirthReason = "reset the overshot Fight Boss sequence and retry Titan 7 puzzle letter at Boss "
                                         + targetBosses[sequence];
                    plan.RebirthRunnerUpSeconds = targetBosses[sequence];
                    plan.RebirthRunnerUpDeltaSeconds = 0;
                    plan.Challenges.Clear();
                }
            }
            plan.WandoosOS = ChooseWandoos(c, difficulty.evil);
            plan.NGUDifficulties.Add(new TimedValue {Time = 0, Value = 0});
            plan.NGUDifficulties.Add(new TimedValue {Time = early ? 82800 : 79200, Value = 1});

            if (wishes)
            {
                Add(plan.Energy, 0, "CAPALLBT", "CAPTM", "CAPAT-1:30", "CAPAT-0:20", "CAPWAN", "CAPWISH-0:10", "CAPWISH-1:10", "CAPWISH-2:10", "CAPWISH-3:10", "TM");
                Add(plan.Energy, 1800, "CAPALLBT", "CAPBESTAUG", "CAPAT-1:30", "CAPAT-0:20", "CAPWAN", "CAPWISH-0:10", "CAPWISH-1:10", "CAPWISH-2:10", "CAPWISH-3:10");
                Add(plan.Energy, 10800, "CAPWISH-0:10", "CAPWISH-1:10", "CAPWISH-2:10", "CAPWISH-3:10", "CAPAT-1:30", "CAPAT-0:20", "NGU-4", "NGU-6", "NGU-8");
                Add(plan.Magic, 0, "CAPTM", "CAPWAN", "CAPWISH-0:10", "CAPWISH-1:10", "CAPWISH-2:10", "CAPWISH-3:10", "TM");
                Add(plan.Magic, 1800, "BR", "CAPWAN", "CAPWISH-0:10", "CAPWISH-1:10", "CAPWISH-2:10", "CAPWISH-3:10", "NGU-0", "NGU-1");
                Add(plan.Magic, 10800, "BR", "CAPWISH-0:10", "CAPWISH-1:10", "CAPWISH-2:10", "CAPWISH-3:10", "NGU-0", "NGU-1", "NGU-6");
            }
            else
            {
                Add(plan.Energy, 0, "CAPALLBT", "CAPTM", "CAPAT-1:30", "CAPAT-0:20", "CAPWAN", "TM");
                Add(plan.Energy, 1800, "CAPALLBT", "CAPBESTAUG", "CAPAT-1:30", "CAPAT-0:20", "CAPWAN");
                Add(plan.Energy, 10800, "CAPAT-1:30", "CAPAT-0:20", "NGU-4", "NGU-6", "NGU-8");
                Add(plan.Magic, 0, "CAPTM", "CAPWAN", "TM");
                Add(plan.Magic, 1800, "BR", "CAPWAN", "NGU-0", "NGU-1");
                Add(plan.Magic, 10800, "BR", "NGU-0", "NGU-1", "NGU-6");
            }

            if (wishes)
                Add(plan.R3, 0, "CAPWISH-0:15", "CAPWISH-1:15", "CAPWISH-2:15", "CAPWISH-3:15", "BESTHACK");
            else if (early)
                Add(plan.R3, 0, "HACK-0", "HACK-1");
            else
                Add(plan.R3, 0, "HACK-1", "BESTHACK");

            plan.Diggers = new[] {4, 5, 3, 11, 8, 0};
            return plan;
        }

        private static AutopilotPlan BuildSadistic(Character c, AutopilotConfig config)
        {
            var plan = NewPlan(c);
            plan.Stage = "Sadistic";
            plan.Objective = "MacGuffin growth, Sadistic NGUs, Adventure cards, PP/QP, wishes, and milestone-efficient hacks";
            plan.RebirthSeconds = 86400;
            plan.RebirthReason = "bank the current Sadistic MacGuffin, card, wish, hack, and NGU cycle at the daily event boundary";
            plan.RebirthRunnerUpSeconds = 82800;
            plan.RebirthRunnerUpDeltaSeconds = 3600;
            plan.WandoosOS = ChooseWandoos(c, difficulty.sadistic);
            plan.NGUDifficulties.Add(new TimedValue {Time = 0, Value = 2});

            Add(plan.Energy, 0, "CAPALLBT", "CAPTM", "CAPBESTAUG", "CAPAT-1:25", "CAPAT-0:20", "CAPWAN", "CAPWISH-0:10", "CAPWISH-1:10", "CAPWISH-2:10", "CAPWISH-3:10", "NGU-4", "NGU-6", "NGU-8");
            Add(plan.Energy, 7200, "CAPWISH-0:10", "CAPWISH-1:10", "CAPWISH-2:10", "CAPWISH-3:10", "CAPAT-1:25", "CAPAT-0:20", "NGU-4", "NGU-6", "NGU-8");
            Add(plan.Magic, 0, "CAPTM", "BR", "CAPWAN", "CAPWISH-0:10", "CAPWISH-1:10", "CAPWISH-2:10", "CAPWISH-3:10", "NGU-0", "NGU-1", "NGU-6");
            Add(plan.Magic, 7200, "BR", "CAPWISH-0:10", "CAPWISH-1:10", "CAPWISH-2:10", "CAPWISH-3:10", "NGU-0", "NGU-1", "NGU-6");
            Add(plan.R3, 0, "CAPWISH-0:15", "CAPWISH-1:15", "CAPWISH-2:15", "CAPWISH-3:15", "BESTHACK");
            plan.Diggers = new[] {3, 8, 11, 0, 9};
            return plan;
        }

        private static AutopilotPlan NewPlan(Character c)
        {
            var plan = new AutopilotPlan {Stage = "Unknown", Objective = "wait for game state", RebirthSeconds = -1};
            AddChallengeRecommendation(c, plan);
            return plan;
        }

        private static void AddChallengeRecommendation(Character c, AutopilotPlan plan)
        {
            if (c.challenges.inChallenge) return;
            var cc = c.allChallenges;
            var highest = c.settings.rebirthDifficulty == difficulty.normal ? c.highestBoss
                : c.settings.rebirthDifficulty == difficulty.evil ? c.highestHardBoss
                : c.highestSadisticBoss;
            var candidates = new List<KeyValuePair<double, string>>();

            AddChallengeCandidate(candidates, cc, ChallengeType.NoAug, "NOAUG", 58, highest, 240,
                cc.noAugsChallenge.currentCompletions(), cc.noAugsChallenge.maxCompletions);
            AddChallengeCandidate(candidates, cc, ChallengeType.NoEquip, "NOEC", 65, highest, 220,
                cc.noEquipmentChallenge.currentCompletions(), cc.noEquipmentChallenge.maxCompletions);
            AddChallengeCandidate(candidates, cc, ChallengeType.NoRebirth, "NORB",
                39 + 5 * cc.noRebirthChallenge.currentCompletions(), highest, 200,
                cc.noRebirthChallenge.currentCompletions(), cc.noRebirthChallenge.maxCompletions);
            // Basic always ends at Boss 57; its target does not scale with completions.
            AddChallengeCandidate(candidates, cc, ChallengeType.Basic, "BASIC", 57, highest, 180,
                cc.basicChallenge.currentCompletions(), cc.basicChallenge.maxCompletions);
            AddChallengeCandidate(candidates, cc, ChallengeType.Troll, "TC",
                68 + 15 * cc.trollChallenge.currentCompletions(), highest, 260,
                cc.trollChallenge.currentCompletions(), cc.trollChallenge.maxCompletions);
            AddChallengeCandidate(candidates, cc, ChallengeType.OneHundredLC, "100LC", 57, highest, 165,
                cc.level100Challenge.currentCompletions(), cc.level100Challenge.maxCompletions);
            AddChallengeCandidate(candidates, cc, ChallengeType.NoTimeMachine, "NOTM",
                57 + 15 * cc.timeMachineChallenge.currentCompletions(), highest, 145,
                cc.timeMachineChallenge.currentCompletions(), cc.timeMachineChallenge.maxCompletions);
            AddChallengeCandidate(candidates, cc, ChallengeType.NoNGU, "NONGU",
                57 + 10 * cc.NGUChallenge.currentCompletions(), highest, 140,
                cc.NGUChallenge.currentCompletions(), cc.NGUChallenge.maxCompletions);
            AddChallengeCandidate(candidates, cc, ChallengeType.Blind, "BLIND",
                57 + 10 * cc.blindChallenge.currentCompletions(), highest, 130,
                cc.blindChallenge.currentCompletions(), cc.blindChallenge.maxCompletions);
            // A failed 24-hour run awards nothing.  A prior Basic clear is the only
            // source-backed runtime sample of a reset boss climb; require a large
            // safety margin instead of starting from a mere highest-boss threshold.
            if (c.challenges.basicChallenge.bestTime > 0
                && c.challenges.basicChallenge.bestTime < 43200)
                AddChallengeCandidate(candidates, cc, ChallengeType.TwentyFourHour, "24HR",
                    Math.Min(299, 57 + 26 * cc.hour24Challenge.currentCompletions()), highest, 90,
                    cc.hour24Challenge.currentCompletions(), cc.hour24Challenge.maxCompletions);

            foreach (var candidate in candidates.OrderByDescending(x => x.Key))
                plan.Challenges.Add(candidate.Value);
        }

        private static void AddChallengeCandidate(List<KeyValuePair<double, string>> candidates,
            AllChallengesController all, ChallengeType type, string code,
            int targetBoss, int highestBoss, double rewardWeight, int complete, int maxCompletions)
        {
            if (complete >= maxCompletions
                || !BaseRebirth.ChallengeUnlocked(all, type)
                || highestBoss < targetBoss + 5)
                return;
            var expectedDifficulty = Math.Max(1, targetBoss - 30);
            candidates.Add(new KeyValuePair<double, string>(rewardWeight / expectedDifficulty,
                code + "-" + (complete + 1)));
        }

        private static void Add(System.Collections.Generic.ICollection<PlanBreakpoint> list, int time, params string[] priorities)
        {
            list.Add(new PlanBreakpoint {Time = time, Priorities = priorities});
        }

        private static int ChooseWandoos(Character c, difficulty diff)
        {
            if (diff == difficulty.normal && c.wandoos98.XLLevels > 0 && c.curEnergy >= 1000000000000000L && c.magic.curMagic >= 1000000000000000L)
                return 2;
            if (diff == difficulty.normal && c.inventory.itemList.jakeComplete && c.curEnergy >= 1000000000000L && c.magic.curMagic >= 1000000000000L)
                return 1;
            return 0;
        }

        private static int HighestFruitMaturitySeconds(Character c)
        {
            if (!c.settings.yggdrasilOn || c.yggdrasil == null || c.yggdrasil.fruits == null)
                return 0;
            var tier = c.yggdrasil.fruits.Where(x => x.maxTier > 0)
                .Select(x => (int)Math.Min(24L, x.maxTier)).DefaultIfEmpty(0).Max();
            return tier * 3600;
        }
    }
}
