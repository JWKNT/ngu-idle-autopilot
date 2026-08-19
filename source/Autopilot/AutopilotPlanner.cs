using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.AllocationProfiles.RebirthStuff;

/*
FILE PURPOSE

AutopilotPlanner composes a Character snapshot and config into a progression stage, resource
breakpoints, and rebirth recommendation. Exact subsystem formulas stay in breakpoints/managers;
this layer sequences them and stamps the task-29 staged-authority ceiling onto the immutable plan.
Prefer live events over fixed chapter-clock schedules. Number loss is an explicit counterfactual
cost, never an invented admission rule; only invalid native previews or an unverified mutation
boundary may turn a scheduled ordinary rebirth into a hard hold. The task-28 scheduler remains a
typed shadow output and never replaces incumbent execution here.
*/
namespace NGUInjector.Autopilot
{
    internal static class AutopilotPlanner
    {
        internal static AutopilotPlan Build(Character c, AutopilotConfig config)
        {
            var currentDifficulty = c.settings.rebirthDifficulty;
            AutopilotPlan plan;
            if (currentDifficulty == difficulty.sadistic)
                plan = BuildSadistic(c, config);
            else if (currentDifficulty == difficulty.evil)
                plan = BuildEvil(c, config);
            else
                plan = BuildNormal(c, config);
            ApplyProgressionCheckpoint(c, plan);
            ApplyActiveChallengePlan(c, plan);
            FinalizeOrdinaryRebirthProjection(c, plan);
            plan.ApplyDeploymentAuthority(config);
            return plan;
        }

        /*
        ORDINARY REBIRTH PROJECTION FINALIZATION

        Native NGU Idle assigns the previewed Number even when it is below the current multiplier.
        Early persistent Boss EXP and Basic Training cap compression can repay that loss, so Number
        belongs in the branch score rather than a synthetic safety predicate. This pass records the
        exact ratio for telemetry and holds only when the native preview itself is not finite/usable.
        Mutation synchronization, challenge admission, and imminent discrete events remain separate
        hard gates at the executor.
        */
        private static void FinalizeOrdinaryRebirthProjection(Character c, AutopilotPlan plan)
        {
            if (c == null || plan == null || plan.RebirthSeconds < 0) return;
            var attackRatio = c.attackMulti > 0 ? c.nextAttackMulti / c.attackMulti : 0.0;
            var defenseRatio = c.defenseMulti > 0 ? c.nextDefenseMulti / c.defenseMulti : 0.0;
            var minimumRatio = Math.Min(attackRatio, defenseRatio);
            var finite = !double.IsNaN(minimumRatio) && !double.IsInfinity(minimumRatio);
            if (plan.RebirthMinimumNumberRatio <= 0.0)
                plan.RebirthMinimumNumberRatio = minimumRatio;
            if (plan.RebirthProjectedMultiplier <= 0.0)
                plan.RebirthProjectedMultiplier = c.nextAttackMulti;
            if (plan.RebirthProjectedAP <= 0 && plan.RebirthSeconds >= 4100)
                plan.RebirthProjectedAP = 1 + (plan.RebirthSeconds - 4100) / 500;
            if (plan.RebirthExpectedCatchupExp <= 0.0)
            {
                plan.RebirthExpectedCatchupExp = RebirthOptimizer.ExpectedRecurringBossExp(c,
                    Math.Max(0, c.bossID - 1));
                plan.RebirthExpectedCatchupExpPerHour = 3600.0 * plan.RebirthExpectedCatchupExp
                                                        / Math.Max(1, plan.RebirthSeconds);
            }
            if (plan.RebirthCandidateCount <= 0)
            {
                plan.RebirthCandidateCount = 1;
                plan.RebirthCandidateSummary = plan.RebirthSeconds + "s event boundary; native minimum Number ratio "
                                               + minimumRatio.ToString("0.000000");
            }

            if (!finite || c.nextAttackMulti <= 0 || c.nextDefenseMulti <= 0)
            {
                plan.RebirthExecutionHold = true;
                plan.RebirthReason += "; native Number preview is invalid, so the mutation boundary is held pending a clean snapshot";
            }
        }

        /*
        RESET-LOCAL FINALIZATION

        Build first composes the full resource timeline; DiggerManager then chooses the actual
        planned digger set. Only after both facts exist is it safe to compare an OS switch, because
        changing OS destroys both current bars. This second pass replaces provisional choices made
        while individual stage builders were still incomplete.
        */
        internal static void FinalizeResetLocalChoices(Character c, AutopilotPlan plan)
        {
            if (c == null || plan == null) return;
            plan.WandoosOS = ChooseWandoos(c, c.settings.rebirthDifficulty, plan);
        }

        /*
        EVENT-DRIVEN REBIRTH FINALIZATION

        Early Normal already has a one-second source-derived optimizer. Later stages previously
        promoted human-friendly chapter clocks to policy. Keep those clocks as priors, enumerate
        the live native event queue, and publish both winner and runner-up. Puzzle targets remain
        locked because their legal window is not an exchangeable utility preference. An active
        challenge still receives this ordinary checkpoint: its own policy then accepts it, delays
        it for Troll/Laser timing, or rejects it only for the actual No-Rebirth rule.
        */
        private static void ApplyProgressionCheckpoint(Character c, AutopilotPlan plan)
        {
            if (c == null || plan == null || plan.RebirthTargetLocked)
                return;
            var earlyNormal = c.settings.rebirthDifficulty == difficulty.normal
                              && !(c.inventory.itemList.numberComplete || c.settings.nguOn);
            if (earlyNormal || plan.RebirthSeconds < 0) return;
            var recommendation = StrategyCheckpointPlanner.Select(c, plan.RebirthSeconds,
                plan.RebirthReason);
            plan.RebirthSeconds = recommendation.TargetSeconds;
            plan.RebirthReason = recommendation.Reason;
            plan.RebirthRunnerUpSeconds = recommendation.RunnerUpSeconds;
            plan.RebirthRunnerUpDeltaSeconds = recommendation.RunnerUpSeconds < 0 ? -1
                : Math.Abs(recommendation.RunnerUpSeconds - recommendation.TargetSeconds);
            plan.RebirthRunnerUpReason = recommendation.RunnerUpReason;
            plan.RebirthSelectedScorePerHour = recommendation.SelectedScorePerHour;
            plan.RebirthRunnerUpScorePerHour = recommendation.RunnerUpScorePerHour;
            plan.RebirthCandidateCount = recommendation.CandidateCount;
            plan.RebirthCandidateSummary = recommendation.CandidateSummary;
            plan.RebirthExecutionHold = recommendation.ExecutionHold;
            plan.RebirthNextPositiveEtaSeconds = recommendation.NextPositiveEtaSeconds;
            plan.RebirthNextEvaluationEtaSeconds = recommendation.NextEvaluationEtaSeconds;
            plan.RebirthEtaReason = recommendation.EtaReason;
        }

        private static AutopilotPlan BuildNormal(Character c, AutopilotConfig config)
        {
            var list = c.inventory.itemList;
            var nguUnlocked = list.numberComplete || c.settings.nguOn;
            var t4Defeated = list.uugComplete;
            var t1Defeated = list.GRBComplete;
            var plan = NewPlan(c);

            plan.NGUDifficulties.Add(new TimedValue {Time = 0, Value = 0});
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
                plan.RebirthExpectedCatchupExp = rebirth.ExpectedCatchupExp;
                plan.RebirthExpectedCatchupExpPerHour = rebirth.ExpectedCatchupExpPerHour;
                plan.RebirthMinimumNumberRatio = rebirth.MinimumNumberRatio;
                plan.RebirthExecutionHold = rebirth.ExecutionHold;
                plan.RebirthNextPositiveEtaSeconds = rebirth.NextPositiveEtaSeconds;
                plan.RebirthNextEvaluationEtaSeconds = rebirth.NextEvaluationEtaSeconds;
                plan.RebirthEtaReason = rebirth.EtaReason;
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
                plan.WandoosOS = ChooseWandoos(c, difficulty.normal, plan.RebirthSeconds);
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
                    plan.RebirthSeconds = Math.Min(plan.RebirthSeconds,
                        Math.Max((int)Math.Ceiling((double)c.rebirth.minRebirthTime()), elapsed));
                    plan.RebirthReason = "reset the missed Titan 6 clue window immediately, then target 2,586 seconds next run";
                    plan.RebirthRunnerUpSeconds = 2586;
                    plan.RebirthRunnerUpDeltaSeconds = 0;
                }
                // Starting a challenge is a hard reset and must not preempt this window.
                plan.Challenges.Clear();
                plan.RebirthTargetLocked = true;
            }

            Add(plan.Energy, 0, "CAPALLBT", "CAPTM", "CAPBESTAUG", "CAPAT-1:25", "CAPAT-0:20", "CAPWAN", "NGU-4", "NGU-6");
            Add(plan.Energy, 3600, "CAPALLBT", "CAPAT-1:25", "CAPAT-0:20", "CAPWAN", "NGU-4", "NGU-6");
            Add(plan.Magic, 0, "CAPTM", "BR", "CAPWAN", "NGU-0", "NGU-1");
            Add(plan.Magic, 3600, "BR", "CAPWAN", "NGU-0", "NGU-1");
            Add(plan.R3, 0, "BESTHACK");
            plan.Diggers = new[] {4, 5, 3, 0, 11};
            plan.WandoosOS = ChooseWandoos(c, difficulty.normal, plan.RebirthSeconds);
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
                plan.RebirthSeconds = TitanSpawnSeconds(c, 7);
                plan.RebirthReason = "preserve the exact No-Rebirth-reduced Adventure clock through the Godmother spawn gate";
            }
            else if (!c.adventure.titan8Unlocked && c.highestHardBoss >= 166)
            {
                plan.RebirthSeconds = TitanSpawnSeconds(c, 8);
                plan.RebirthReason = "preserve the exact No-Rebirth-reduced Adventure clock through the Exile spawn gate";
            }
            else if (!c.adventure.titan9Unlocked && c.highestHardBoss >= 190)
            {
                plan.RebirthSeconds = TitanSpawnSeconds(c, 9);
                plan.RebirthReason = "preserve the exact No-Rebirth-reduced Adventure clock through the Titan 9 spawn gate";
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
                    plan.RebirthSeconds = Math.Max((int)Math.Ceiling((double)c.rebirth.minRebirthTime()), elapsed);
                    plan.RebirthReason = "reset the overshot Fight Boss sequence and retry Titan 7 puzzle letter at Boss "
                                         + targetBosses[sequence];
                    plan.RebirthRunnerUpSeconds = targetBosses[sequence];
                    plan.RebirthRunnerUpDeltaSeconds = 0;
                    plan.Challenges.Clear();
                    plan.RebirthTargetLocked = true;
                }
            }
            plan.WandoosOS = ChooseWandoos(c, difficulty.evil, plan.RebirthSeconds);
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
            plan.WandoosOS = ChooseWandoos(c, difficulty.sadistic, plan.RebirthSeconds);
            plan.NGUDifficulties.Add(new TimedValue {Time = 0, Value = 2});

            Add(plan.Energy, 0, "CAPALLBT", "CAPTM", "CAPBESTAUG", "CAPAT-1:25", "CAPAT-0:20", "CAPWAN", "CAPWISH-0:10", "CAPWISH-1:10", "CAPWISH-2:10", "CAPWISH-3:10", "NGU-4", "NGU-6", "NGU-8");
            Add(plan.Energy, 7200, "CAPWISH-0:10", "CAPWISH-1:10", "CAPWISH-2:10", "CAPWISH-3:10", "CAPAT-1:25", "CAPAT-0:20", "NGU-4", "NGU-6", "NGU-8");
            Add(plan.Magic, 0, "CAPTM", "BR", "CAPWAN", "CAPWISH-0:10", "CAPWISH-1:10", "CAPWISH-2:10", "CAPWISH-3:10", "NGU-0", "NGU-1", "NGU-6");
            Add(plan.Magic, 7200, "BR", "CAPWISH-0:10", "CAPWISH-1:10", "CAPWISH-2:10", "CAPWISH-3:10", "NGU-0", "NGU-1", "NGU-6");
            Add(plan.R3, 0, "CAPWISH-0:15", "CAPWISH-1:15", "CAPWISH-2:15", "CAPWISH-3:15", "BESTHACK");
            plan.Diggers = new[] {3, 8, 11, 0, 9};
            ApplyEndgameDependencyPlan(c, plan);
            return plan;
        }

        private static void ApplyEndgameDependencyPlan(Character c, AutopilotPlan plan)
        {
            var missing = MechanicsEndgame.AllRequirements()
                .Where(x => !EndgameDependencyModel.IsOwned(c, x.ItemId)).ToArray();
            plan.EndgameMissingSummary = missing.Length == 0 ? "none"
                : string.Join(", ", missing.Select(x => x.ItemId + ":" + x.DependencyKind).ToArray());
            plan.Titan12VersionTarget = EndgameDependencyModel.NextMissingTitan12Version(c);
            plan.EndgameReadyToTrigger = missing.Length == 0;
            if (missing.Length == 0)
            {
                plan.EndgameObjective = "place all sixteen END pieces in canonical slots and execute the opt-in native terminal sequence";
                plan.Objective = "THE END: " + plan.EndgameObjective;
                return;
            }

            // Terminal branches advance in parallel. Publish the most constrained currently
            // actionable dependency as the route owner while Wish/Card/Blood/Hack managers keep
            // progressing their independent branches in the same scheduler cycle.
            var next = missing.OrderBy(EndgameDependencyPriority)
                .ThenBy(x => x.ItemId).First();
            plan.EndgameObjective = next.Description;
            plan.Objective = "THE END critical path: " + next.Description
                             + " (" + missing.Length + " pieces remain)";
        }

        private static int EndgameDependencyPriority(EndItemRequirement requirement)
        {
            switch (requirement.DependencyKind)
            {
                case EndDependencyKind.PerkPurchase:
                case EndDependencyKind.QuirkPurchase:
                case EndDependencyKind.WishCompletion:
                case EndDependencyKind.BloodSpell:
                    return 0;
                case EndDependencyKind.EndHack:
                case EndDependencyKind.EndCard:
                case EndDependencyKind.ItopodDrop:
                case EndDependencyKind.Titan12VersionDrop:
                    return 1;
                case EndDependencyKind.SadisticBoss:
                case EndDependencyKind.Titan14Kill:
                    return 2;
                default:
                    return 3;
            }
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
            string evidence;
            var admissions = ChallengeStrategyPlanner.Recommend(c, out evidence);
            foreach (var admission in admissions)
                plan.Challenges.Add(admission.ProfileCode);
            plan.ChallengeEvidenceSummary = evidence;
            var next = admissions.FirstOrDefault();
            if (next == null) return;
            plan.ChallengeAdmitted = true;
            plan.ChallengeName = next.ProfileCode;
            plan.ChallengeAllowsRebirth = ChallengeStrategyPlanner.AllowsOrdinaryRebirth(next.Type);
            plan.ChallengeRulesSummary = ChallengeStrategyPlanner.RulesSummary(next.Type);
            plan.ChallengeRebirthPolicy = "Not active; entry still requires its exact opportunity proof.";
            plan.ChallengeClearEtaSeconds = (int)Math.Ceiling(next.PessimisticClearSeconds);
            plan.ChallengeRecoveryEtaSeconds = (int)Math.Ceiling(next.RecoverySeconds);
            plan.ChallengeTargetBoss = next.TargetBoss < 0 ? -1 : next.TargetBoss + 1;
            plan.ChallengeTargetLevel = next.TargetLevel;
            plan.ChallengeCompletedBefore = next.CompletedBefore;
            plan.ChallengeMaxCompletions = next.MaxCompletions;
            plan.ChallengeEtaReason = next.Evidence;
        }

        private static void ApplyActiveChallengePlan(Character c, AutopilotPlan plan)
        {
            if (!c.challenges.inChallenge) return;
            // The base stage has already selected an exact ordinary-rebirth checkpoint.  Passing
            // it into the active challenge policy is essential: omitting it made every challenge,
            // including unrestricted Basic, look like a native no-reset mode.
            var active = ChallengeStrategyPlanner.ActivePolicy(c, null, plan.RebirthSeconds);
            if (active == null) return;
            var ordinaryRebirthSeconds = plan.RebirthSeconds;
            plan.Challenges.Clear();
            plan.Stage += " / active challenge";
            plan.Objective = active.Objective;
            plan.ChallengeActive = true;
            plan.ChallengeAdmitted = false;
            plan.ChallengeName = active.Code;
            plan.ChallengeAllowsRebirth = active.MechanicallyAllowsRebirth;
            plan.ChallengeRulesSummary = active.RulesSummary;
            plan.ChallengeRebirthPolicy = active.RebirthPolicySummary;
            plan.ChallengeTargetBoss = active.TargetBoss < 0 ? -1 : active.TargetBoss + 1;
            plan.ChallengeTargetLevel = active.TargetLevel;
            plan.ChallengeClearEtaSeconds = active.EtaSeconds;
            plan.ChallengeEtaReason = active.EtaReason;
            plan.ChallengeEvidenceSummary = active.Code + " active. " + active.RulesSummary
                                            + " " + active.RebirthPolicySummary
                                            + " Clear estimate: " + active.EtaReason;
            plan.Energy.Clear();
            plan.Magic.Clear();
            plan.R3.Clear();

            var noRebirth = active.ForbidRebirth;
            var noAugs = c.challenges.noAugsChallenge.inChallenge;
            var noTM = c.challenges.timeMachineChallenge.inChallenge;
            if (noRebirth)
            {
                plan.RebirthSeconds = -1;
                plan.RebirthReason = active.Objective;
                plan.RebirthExecutionHold = true;
                plan.RebirthNextPositiveEtaSeconds = -1;
                plan.RebirthNextEvaluationEtaSeconds = 1;
                plan.RebirthEtaReason = active.EtaReason;
                plan.RebirthRunnerUpSeconds = -1;
                plan.RebirthRunnerUpDeltaSeconds = -1;
            }
            else
            {
                plan.RebirthSeconds = active.RebirthSeconds;
                plan.RebirthExecutionHold = false;
                plan.RebirthNextPositiveEtaSeconds = Math.Max(0,
                    active.RebirthSeconds - (int)Math.Floor(c.rebirthTime.totalseconds));
                plan.RebirthNextEvaluationEtaSeconds = 1;
                // An unrestricted active challenge owns the long-term objective, not the
                // ordinary checkpoint's evidence fields.  Basic previously replaced a valid
                // reset countdown/reason with its unrelated (and often unknown) challenge-clear
                // ETA, making dashboards claim the reset ETA was unknown.  Preserve the ordinary
                // optimizer explanation unless challenge policy actually changed the checkpoint.
                if (active.RebirthSeconds != ordinaryRebirthSeconds)
                {
                    plan.RebirthReason = active.Objective + "; " + active.RebirthPolicySummary;
                    plan.RebirthEtaReason = active.EtaReason;
                    plan.RebirthRunnerUpSeconds = -1;
                    plan.RebirthRunnerUpDeltaSeconds = -1;
                }
            }

            var energy = new List<string> {"CAPALLBT:20"};
            if (!noTM) energy.Add("CAPTM:25");
            if (active.RequiresLaserSwordAllocation)
            {
                energy.Add("CAPAUG-12:50");
                energy.Add("CAPAUG-13:50");
            }
            else if (!noAugs) energy.Add("CAPBESTAUG:35");
            energy.Add("CAPWAN:20");
            if (!noAugs && !active.RequiresLaserSwordAllocation) energy.Add("BESTAUG");
            Add(plan.Energy, 0, energy.ToArray());

            var magic = new List<string>();
            if (!noTM) magic.Add("CAPTM:45");
            magic.Add("CAPWAN:25");
            magic.Add("BR");
            Add(plan.Magic, 0, magic.ToArray());
            Add(plan.R3, 0, "BESTHACK");
            plan.Diggers = noTM ? new[] {2, 3, 1, 10, 11} : new[] {2, 3, 1, 11, 0};
            plan.WandoosOS = ChooseWandoos(c, c.settings.rebirthDifficulty,
                noRebirth ? (int)Math.Min(int.MaxValue, c.rebirthTime.totalseconds + 86400.0)
                    : plan.RebirthSeconds);
        }

        private static void Add(System.Collections.Generic.ICollection<PlanBreakpoint> list, int time, params string[] priorities)
        {
            list.Add(new PlanBreakpoint {Time = time, Priorities = priorities});
        }

        private static int ChooseWandoos(Character c, difficulty diff, int targetRebirthSeconds)
        {
            var current = (int)c.wandoos98.os;
            if (!c.settings.wandoos98On || !c.wandoos98.installed || c.wandoos98.disabled)
                return current;

            // Changing OS destroys both reset-local level bars.  Compare the exact native bonus
            // equations at the planned end of the run, retaining current levels only for the
            // installed OS.  This replaces raw-cap thresholds that selected Wandoos 98 in every
            // Evil/Sadistic run even when MEH or XL had already repaid their slower base divider.
            var remaining = targetRebirthSeconds < 0 ? double.PositiveInfinity
                : Math.Max(0.0, targetRebirthSeconds - c.rebirthTime.totalseconds);
            // changeOS destroys both level bars immediately. Never mutate inside
            // the final minute or at an overdue checkpoint; there is no safe time
            // to repay the reset before Number is banked.
            if (remaining <= 60.0)
                return current;
            var available = new List<int> {0};
            if (c.inventory.itemList.jakeComplete) available.Add(1);
            if (c.wandoos98.XLLevels > 0) available.Add(2);

            var currentScore = ProjectedWandoosLogBonus(c, diff, current, current, remaining);
            var best = current;
            var bestScore = currentScore;
            foreach (var os in available)
            {
                var score = ProjectedWandoosLogBonus(c, diff, os, current, remaining);
                if (score > bestScore)
                {
                    best = os;
                    bestScore = score;
                }
            }

            // Avoid destroying progress for an effectively tied projection and absorb small
            // model error from future breakpoint competition.  A switch must improve the final
            // Wandoos multiplier by at least ten percent.
            return best != current && bestScore >= currentScore + Math.Log(1.10) ? best : current;
        }

        private static double ProjectedWandoosLogBonus(Character c, difficulty diff, int os,
            int currentOS, double remainingSeconds)
        {
            var keep = os == currentOS;
            var energyLevel = keep ? (double)c.wandoos98.energyLevel : 0.0;
            var magicLevel = keep ? (double)c.wandoos98.magicLevel : 0.0;
            var energyProgress = keep ? Math.Max(0.0, c.wandoos98.energyProgress) : 0.0;
            var magicProgress = keep ? Math.Max(0.0, c.wandoos98.magicProgress) : 0.0;

            // CAPWAN shares resources with the rest of the plan.  Reserve a conservative ten
            // percent for projection; the native controller advances at 50 Hz and can add at
            // most one level per tick even when the bar is overcapped.
            var energyBudget = Math.Max((double)c.wandoos98.wandoosEnergy, c.curEnergy * 0.10);
            var magicBudget = Math.Max((double)c.wandoos98.wandoosMagic, c.magic.curMagic * 0.10);
            var energyPerTick = Math.Min(1.0, energyBudget * c.totalWandoosEnergySpeed()
                                               / WandoosBaseTime(diff, os));
            var magicPerTick = Math.Min(1.0, magicBudget * c.totalWandoosMagicSpeed()
                                              / WandoosBaseTime(diff, os));
            var ticks = Math.Max(0L, (long)Math.Floor(remainingSeconds * 50.0));
            energyLevel += ProjectWandoosLevels(energyProgress, energyPerTick, ticks);
            magicLevel += ProjectWandoosLevels(magicProgress, magicPerTick, ticks);

            if (os == 1)
                return Math.Log(1.0 + energyLevel / 5.0) + Math.Log(1.0 + 2.0 * magicLevel);
            if (os == 2)
                return 1.05 * (Math.Log(1.0 + 6.0 * energyLevel) + Math.Log(1.0 + 40.0 * magicLevel));
            return 0.8 * (Math.Log(1.0 + energyLevel / 100.0) + Math.Log(1.0 + magicLevel / 25.0));
        }

        private static int ChooseWandoos(Character c, difficulty diff, AutopilotPlan plan)
        {
            var current = (int)c.wandoos98.os;
            if (!c.settings.wandoos98On || !c.wandoos98.installed || c.wandoos98.disabled)
                return current;
            var target = plan.EffectiveAllocationTarget(c);
            var remaining = target < 0 ? 0.0 : Math.Max(0.0, target - c.rebirthTime.totalseconds);
            if (remaining <= 60.0) return current;
            var available = new List<int> {0};
            if (c.inventory.itemList.jakeComplete) available.Add(1);
            if (c.wandoos98.XLLevels > 0) available.Add(2);
            var currentScore = ProjectedPlannedWandoosLogBonus(c, diff, current, current, remaining, plan);
            var best = current;
            var bestScore = currentScore;
            foreach (var os in available)
            {
                var score = ProjectedPlannedWandoosLogBonus(c, diff, os, current, remaining, plan);
                if (score <= bestScore) continue;
                best = os;
                bestScore = score;
            }
            return best != current && bestScore >= currentScore + Math.Log(1.10) ? best : current;
        }

        private static double ProjectedPlannedWandoosLogBonus(Character c, difficulty diff, int os,
            int currentOS, double remainingSeconds, AutopilotPlan plan)
        {
            var keep = os == currentOS;
            var energyLevel = keep ? (double)c.wandoos98.energyLevel : 0.0;
            var magicLevel = keep ? (double)c.wandoos98.magicLevel : 0.0;
            var energyProgress = keep ? Math.Max(0.0, c.wandoos98.energyProgress) : 0.0;
            var magicProgress = keep ? Math.Max(0.0, c.wandoos98.magicProgress) : 0.0;

            // A planned Digger activation is not yet funded at this irreversible
            // decision boundary. Normalize the live Wandoos Digger out entirely;
            // a later planner pass may switch once native allocations prove the
            // base-rate case already repays the destroyed OS levels.
            var liveDigger = Math.Max(1e-9, c.allDiggers.totalWandoosSpeedBonus());
            var energySpeed = c.totalWandoosEnergySpeed() / liveDigger;
            var magicSpeed = c.totalWandoosMagicSpeed() / liveDigger;
            ProjectWandoosTimeline(c, diff, os, plan.Energy, remainingSeconds,
                c.totalCapEnergy(), Math.Max(0.0, c.wandoos98.wandoosEnergy), energySpeed,
                ref energyLevel, ref energyProgress);
            ProjectWandoosTimeline(c, diff, os, plan.Magic, remainingSeconds,
                c.totalCapMagic(), Math.Max(0.0, c.wandoos98.wandoosMagic), magicSpeed,
                ref magicLevel, ref magicProgress);

            if (os == 1)
                return Math.Log(1.0 + energyLevel / 5.0) + Math.Log(1.0 + 2.0 * magicLevel);
            if (os == 2)
                return 1.05 * (Math.Log(1.0 + 6.0 * energyLevel) + Math.Log(1.0 + 40.0 * magicLevel));
            return 0.8 * (Math.Log(1.0 + energyLevel / 100.0) + Math.Log(1.0 + magicLevel / 25.0));
        }

        private static void ProjectWandoosTimeline(Character c, difficulty diff, int os,
            IList<PlanBreakpoint> breakpoints, double remainingSeconds, double totalCap,
            double confirmedAllocation, double speed, ref double level, ref double progress)
        {
            if (remainingSeconds <= 0.0 || totalCap <= 0.0
                || confirmedAllocation <= 0.0 || speed <= 0.0) return;
            var start = c.rebirthTime.totalseconds;
            var end = start + remainingSeconds;
            var boundaries = new List<double> {start, end};
            boundaries.AddRange(breakpoints.Where(x => x.Time > start && x.Time < end)
                .Select(x => (double)x.Time));
            boundaries = boundaries.Distinct().OrderBy(x => x).ToList();
            for (var i = 0; i + 1 < boundaries.Count; i++)
            {
                var fraction = PlannedWandoosFraction(breakpoints, boundaries[i]);
                if (fraction <= 0.0) continue;
                // CAPWAN:x is a ceiling on whatever idle resource survives every
                // earlier priority, not a guaranteed x% allocation. Project no more
                // than the allocation already confirmed in native Wandoos state.
                var budget = Math.Min(confirmedAllocation, totalCap * fraction);
                var perTick = Math.Min(1.0, budget * speed / WandoosBaseTime(diff, os));
                var ticks = Math.Max(0L,
                    (long)Math.Floor((boundaries[i + 1] - boundaries[i]) * 50.0));
                ApplyWandoosTicks(ref level, ref progress, perTick, ticks);
            }
        }

        private static double PlannedWandoosFraction(IList<PlanBreakpoint> breakpoints, double time)
        {
            var point = breakpoints.Where(x => x.Time <= time).OrderBy(x => x.Time).LastOrDefault();
            if (point == null || point.Priorities == null) return 0.0;
            var result = 0.0;
            foreach (var raw in point.Priorities)
            {
                var value = raw ?? string.Empty;
                if (!value.StartsWith("WAN", StringComparison.OrdinalIgnoreCase)
                    && !value.StartsWith("CAPWAN", StringComparison.OrdinalIgnoreCase))
                    continue;
                var fraction = 1.0;
                // Unity's installed mscorlib does not expose the newer
                // String.Split(char, StringSplitOptions) overload selected by the build-time
                // compiler for a scalar char. Use the legacy char[] overload explicitly so the
                // Wandoos projection runs on the same framework surface as the game.
                var split = value.Split(new[] {':'}, StringSplitOptions.None);
                int percent;
                if (split.Length > 1 && int.TryParse(split[1], out percent))
                    fraction = Math.Max(0.0, Math.Min(1.0, percent / 100.0));
                result = Math.Max(result, fraction);
            }
            return result;
        }

        private static void ApplyWandoosTicks(ref double level, ref double progress,
            double progressPerTick, long ticks)
        {
            if (ticks <= 0 || progressPerTick <= 0.0) return;
            progress = Math.Max(0.0, Math.Min(0.999999999, progress));
            var first = Math.Max(1L,
                (long)Math.Ceiling((1.0 - progress) / progressPerTick));
            if (ticks < first)
            {
                progress += ticks * progressPerTick;
                return;
            }
            level += 1.0;
            progress = 0.0;
            ticks -= first;
            var perLevel = Math.Max(1L, (long)Math.Ceiling(1.0 / progressPerTick));
            level += ticks / perLevel;
            progress = ticks % perLevel * progressPerTick;
        }

        private static long ProjectWandoosLevels(double progress, double progressPerTick, long ticks)
        {
            if (ticks <= 0 || progressPerTick <= 0.0) return 0;
            // Native updateWandoos resets progress to exactly zero after one level
            // and discards overshoot; it does not carry a continuous accumulated
            // bar. Model the first partial bar and then the integer tick cadence.
            var firstTicks = Math.Max(1L,
                (long)Math.Ceiling(Math.Max(0.0, 1.0 - progress) / progressPerTick));
            if (ticks < firstTicks) return 0;
            var ticksPerLevel = Math.Max(1L, (long)Math.Ceiling(1.0 / progressPerTick));
            return 1L + (ticks - firstTicks) / ticksPerLevel;
        }

        private static double WandoosBaseTime(difficulty diff, int os)
        {
            if (diff == difficulty.normal)
                return os == 2 ? 1e15 : os == 1 ? 1e12 : 1e9;
            return os == 2 ? 1e33 : os == 1 ? 1e27 : 1e21;
        }

        private static int HighestFruitMaturitySeconds(Character c)
        {
            if (!c.settings.yggdrasilOn || c.yggdrasil == null || c.yggdrasil.fruits == null)
                return 0;
            var tier = c.yggdrasil.fruits.Where(x => x.maxTier > 0)
                .Select(x => (int)Math.Min(24L, x.maxTier)).DefaultIfEmpty(0).Max();
            return tier * 3600;
        }

        private static int TitanSpawnSeconds(Character c, int titanId)
        {
            return TitanMechanics.SpawnSeconds(titanId,
                c.allChallenges.noRebirthChallenge.completions(),
                c.allChallenges.noRebirthChallenge.evilCompletions(),
                c.allChallenges.noRebirthChallenge.sadisticCompletions());
        }
    }
}
