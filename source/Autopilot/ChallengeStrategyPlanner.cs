using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.AllocationProfiles.RebirthStuff;
using NGUInjector.Managers;

/*
FILE PURPOSE

Purpose: ChallengeStrategyPlanner is the source-derived, fail-closed admission and active-run
policy for every NGU Idle 1.260 challenge in Normal, Evil, and Sadistic. It replaces stage tables
and cross-challenge timing guesses with the native controller's unlock, current-completion, maximum,
target-Boss/level, and persistent same-type best-time state.

Mechanism: Recommend enumerates all eleven native challenge controllers. An entry is eligible only
when the native unlock predicate is true, the difficulty-local completion is below the serialized
maximum, the next target is read from that controller, a same-type clear sample or deliberately
conservative first-clear proof exists, and an imminent Titan window is not being discarded. Repeats
use their own best time plus a rising-target margin. ActivePolicy describes the challenge-specific
reset/allocation contract needed after entry; shared planner code consumes that read-only result.

Inputs and outputs: Inputs are Character, AllChallengesController, the persistent Challenge records,
native target methods, current record Bosses, and read-only Titan clocks. Outputs are ordered profile
codes, pessimistic ETAs, opportunity/recovery cost, exact completion/max state, and telemetry reasons.
This file never enters, quits, completes, or rebirths a challenge and never writes the save.

Invariants and safety: Counts are always difficulty-local. A controller's serialized max is the only
completion cap. `bestTime` comes from the matching persistent Challenge, never a UI controller or a
different challenge. A 24-Hour admission must retain six hours of deadline reserve. Laser Sword
must never rebirth before both native level targets are met; No-Rebirth must never rebirth at all.
Ready/near-ready Titans preempt challenge entry because challenge entry resets every Titan clock.

Extension points and non-goals: Active allocation tokens and Troll confirmation-box servicing belong
in AutopilotPlanner/Manager. This model publishes the exact required contract for those owners. A
first clear deliberately requires excessive historical headroom plus an observed Basic clear; it is
not a promise that historical Boss record alone predicts a challenge clear. Future replay fixtures
may safely tighten those first-clear envelopes.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class ChallengeAdmission
    {
        internal ChallengeType Type;
        internal string Code = string.Empty;
        internal int Completion;
        internal int CompletedBefore;
        internal int MaxCompletions;
        internal int TargetBoss;
        internal int TargetLevel;
        internal double PessimisticClearSeconds;
        internal double RecoverySeconds;
        internal double TitanOpportunitySeconds;
        internal double BenefitWeight;
        internal string Constraints = string.Empty;
        internal string Reward = string.Empty;
        internal string Evidence = string.Empty;
        internal double Score;

        internal string ProfileCode { get { return Code + "-" + Completion; } }

        internal string EtaText
        {
            get
            {
                var seconds = (int)Math.Ceiling(PessimisticClearSeconds);
                if (seconds < 3600) return Math.Max(1, seconds / 60) + "m";
                return (seconds / 3600.0).ToString("0.0") + "h";
            }
        }
    }

    internal sealed class ActiveChallengePolicy
    {
        internal ChallengeType Type;
        internal string Code = string.Empty;
        internal int TargetBoss = -1;
        internal int TargetLevel = -1;
        internal int RebirthSeconds = 900;
        internal bool ForbidRebirth;
        internal bool RequiresLaserSwordAllocation;
        internal bool RequiresTrollDialogService;
        internal int EtaSeconds = -1;
        internal double PessimisticTotalSeconds;
        internal string Objective = string.Empty;
        internal string EtaReason = string.Empty;
    }

    internal static class ChallengeStrategyPlanner
    {
        private const double TitanEntryGuardSeconds = 300.0;
        private const double FirstClearBoundSeconds = 64800.0;

        internal static IList<ChallengeAdmission> Recommend(Character c, out string evidenceSummary)
        {
            evidenceSummary = "Challenge admission unavailable";
            var result = new List<ChallengeAdmission>();
            if (c == null || c.challenges == null || c.challenges.inChallenge
                || c.allChallenges == null || c.settings == null)
                return result;

            var nearestTitan = NearestTitanSeconds();
            if (nearestTitan >= 0.0 && nearestTitan <= TitanEntryGuardSeconds)
            {
                evidenceSummary = "Challenge HOLD: native Titan clock is due in "
                                  + Math.Ceiling(nearestTitan) + "s; entry would reset every Titan clock";
                return result;
            }

            var all = c.allChallenges;
            var highest = ActiveHighestBoss(c);
            var basicBest = ValidBestTime(c.challenges.basicChallenge.bestTime);

            Add(c, result, ChallengeType.Basic, "BASIC",
                all.basicChallenge.currentCompletions(), all.basicChallenge.maxCompletions,
                all.basicChallenge.targetBoss(), -1, c.challenges.basicChallenge.bestTime,
                highest, basicBest, 68, 300.0,
                "ordinary challenge reset; short Number-banking climb",
                NativeReward(all.basicChallenge.expectedEXP(), all.basicChallenge.expectedAPReward(),
                    all.basicChallenge.specialRewards()));
            Add(c, result, ChallengeType.NoAug, "NOAUG",
                all.noAugsChallenge.currentCompletions(), all.noAugsChallenge.maxCompletions,
                all.noAugsChallenge.targetBoss(), -1, c.challenges.noAugsChallenge.bestTime,
                highest, basicBest, 100, 260.0,
                "Augments and Upgrades are disabled for the entire run",
                NativeReward(all.noAugsChallenge.expectedEXP(), all.noAugsChallenge.expectedAPReward(),
                    all.noAugsChallenge.specialRewards()));
            Add(c, result, ChallengeType.TwentyFourHour, "24HR",
                all.hour24Challenge.currentCompletions(), all.hour24Challenge.maxCompletions,
                all.hour24Challenge.targetBoss(), -1, c.challenges.hour24Challenge.bestTime,
                highest, basicBest, 180, 190.0,
                "native hard 24-hour deadline; zero completion reward after expiry",
                NativeReward(all.hour24Challenge.expectedEXP(), all.hour24Challenge.expectedAPReward(),
                    all.hour24Challenge.specialRewards()));
            Add(c, result, ChallengeType.OneHundredLC, "100LC",
                all.level100Challenge.currentCompletions(), all.level100Challenge.maxCompletions,
                all.level100Challenge.targetBoss(), -1, c.challenges.levelChallenge10k.bestTime,
                highest, basicBest, 140, 230.0,
                "every Augment and Upgrade is capped at 100 levels; use short Number cycles",
                NativeReward(all.level100Challenge.expectedEXP(), all.level100Challenge.expectedAPReward(),
                    all.level100Challenge.specialRewards()));
            Add(c, result, ChallengeType.NoEquip, "NOEC",
                all.noEquipmentChallenge.currentCompletions(), all.noEquipmentChallenge.maxCompletions,
                all.noEquipmentChallenge.targetBoss(), -1, c.challenges.noEquipmentChallenge.bestTime,
                highest, basicBest, 220, 290.0,
                "all equipment stats and specials are disabled; inventory remains intact",
                NativeReward(all.noEquipmentChallenge.expectedEXP(), all.noEquipmentChallenge.expectedAPReward(),
                    all.noEquipmentChallenge.specialRewards()));
            Add(c, result, ChallengeType.Troll, "TC",
                all.trollChallenge.currentCompletions(), all.trollChallenge.maxCompletions,
                all.trollChallenge.targetBoss(), -1, c.challenges.trollChallenge.bestTime,
                highest, basicBest, 240, 420.0,
                "small trolls arrive at the native tier interval; every fifth is a big troll",
                NativeReward(all.trollChallenge.expectedEXP(), all.trollChallenge.expectedAPReward(),
                    all.trollChallenge.specialRewards()));
            Add(c, result, ChallengeType.NoRebirth, "NORB",
                all.noRebirthChallenge.currentCompletions(), all.noRebirthChallenge.maxCompletions,
                all.noRebirthChallenge.targetBoss(), -1, c.challenges.noRebirthChallenge.bestTime,
                highest, basicBest, 130, RemainingTitanClockBenefit(c),
                "ordinary rebirth is forbidden; solve one continuously compounded run",
                NativeReward(all.noRebirthChallenge.expectedEXP(), all.noRebirthChallenge.expectedAPReward(),
                    all.noRebirthChallenge.specialRewards()));
            Add(c, result, ChallengeType.LaserSword, "LSC",
                all.laserSwordChallenge.currentCompletions(), all.laserSwordChallenge.maxCompletions,
                -1, all.laserSwordChallenge.laserSwordTarget(), c.challenges.laserSwordChallenge.bestTime,
                highest, basicBest, 180, 280.0,
                "raise both Laser Sword Augment and Upgrade to the exact native target in one run",
                NativeReward(all.laserSwordChallenge.expectedEXP(), all.laserSwordChallenge.expectedAPReward(),
                    all.laserSwordChallenge.specialRewards()));
            Add(c, result, ChallengeType.Blind, "BLIND",
                all.blindChallenge.currentCompletions(), all.blindChallenge.maxCompletions,
                all.blindChallenge.targetBoss(), -1, c.challenges.blindChallenge.bestTime,
                highest, basicBest, 100, 210.0,
                "UI values are hidden; automation must use native state, not rendered text",
                NativeReward(all.blindChallenge.expectedEXP(), all.blindChallenge.expectedAPReward(),
                    all.blindChallenge.specialRewards()));
            Add(c, result, ChallengeType.NoNGU, "NONGU",
                all.NGUChallenge.currentCompletions(), all.NGUChallenge.maxCompletions,
                all.NGUChallenge.targetBoss(), -1, c.challenges.nguChallenge.bestTime,
                highest, basicBest, 160, 250.0,
                "all NGU effects and in-run NGU progress are disabled",
                NativeReward(all.NGUChallenge.expectedEXP(), all.NGUChallenge.expectedAPReward(),
                    all.NGUChallenge.specialRewards()));
            Add(c, result, ChallengeType.NoTimeMachine, "NOTM",
                all.timeMachineChallenge.currentCompletions(), all.timeMachineChallenge.maxCompletions,
                all.timeMachineChallenge.targetBoss(), -1, c.challenges.timeMachineChallenge.bestTime,
                highest, basicBest, 140, 245.0,
                "Time Machine levels, GPS, Gold support, and its Number term are unavailable",
                NativeReward(all.timeMachineChallenge.expectedEXP(), all.timeMachineChallenge.expectedAPReward(),
                    all.timeMachineChallenge.specialRewards()));

            var ordered = result.OrderByDescending(x => x.Score)
                .ThenBy(x => x.PessimisticClearSeconds).ThenBy(x => x.Code).ToList();
            var ledger = ChallengeLedger(c);
            evidenceSummary = ordered.Count == 0
                ? RemainingStateSummary(c, highest, basicBest) + " | " + ledger
                : string.Join(" | ", ordered.Select(AdmissionSummary).ToArray()) + " | " + ledger;
            return ordered;
        }

        internal static ActiveChallengePolicy ActivePolicy(Character c)
        {
            if (c == null || c.challenges == null || !c.challenges.inChallenge
                || c.allChallenges == null)
                return null;
            var p = new ActiveChallengePolicy();
            if (c.challenges.noRebirthChallenge.inChallenge)
            {
                p.Type = ChallengeType.NoRebirth; p.Code = "NORB";
                p.TargetBoss = c.allChallenges.noRebirthChallenge.targetBoss();
                p.ForbidRebirth = true;
                p.Objective = "compound one run to Boss " + (p.TargetBoss + 1) + "; native No-Rebirth forbids resets";
            }
            else if (c.challenges.laserSwordChallenge.inChallenge)
            {
                p.Type = ChallengeType.LaserSword; p.Code = "LSC";
                p.TargetLevel = c.allChallenges.laserSwordChallenge.laserSwordTarget();
                p.ForbidRebirth = true;
                p.RequiresLaserSwordAllocation = true;
                var aug = c.augments.augs[6];
                p.Objective = "Laser Sword Augment " + aug.augLevel + "/" + p.TargetLevel
                              + " and Upgrade " + aug.upgradeLevel + "/" + p.TargetLevel
                              + "; rebirth would erase both";
            }
            else if (c.challenges.trollChallenge.inChallenge)
            {
                p.Type = ChallengeType.Troll; p.Code = "TC";
                p.TargetBoss = c.allChallenges.trollChallenge.targetBoss();
                p.RebirthSeconds = c.bossID < 30 ? 180 : 600;
                p.RequiresTrollDialogService = true;
                p.Objective = "reach Boss " + (p.TargetBoss + 1) + " while servicing native troll dialogs; counter "
                              + c.challenges.trollCounter + "/" + c.allChallenges.trollChallenge.trollFactor();
            }
            else if (c.challenges.levelChallenge10k.inChallenge)
            {
                p.Type = ChallengeType.OneHundredLC; p.Code = "100LC";
                p.TargetBoss = c.allChallenges.level100Challenge.targetBoss();
                p.RebirthSeconds = 180;
                p.Objective = "reach Boss " + (p.TargetBoss + 1)
                              + " with native 100-level caps; bank Number every three minutes";
            }
            else
            {
                AssignBossChallenge(c, p);
                p.RebirthSeconds = c.bossID < 30 ? 180 : 900;
                p.Objective = "reach Boss " + (p.TargetBoss + 1)
                              + " using short challenge Number cycles under " + p.Code + " restrictions";
            }
            var remaining = p.TargetBoss >= 0 ? Math.Max(0, p.TargetBoss + 1 - c.bossID) : 0;
            ApplyActiveEta(c, p, remaining);
            return p;
        }

        private static void ApplyActiveEta(Character c, ActiveChallengePolicy p, int remainingBosses)
        {
            var elapsed = ActiveElapsedSeconds(c, p.Type);
            var sample = ValidBestTime(ActiveBestTime(c, p.Type));
            var observedProjection = 0.0;
            if (p.TargetLevel >= 0)
            {
                var progress = Math.Min(c.augments.augs[6].augLevel,
                    c.augments.augs[6].upgradeLevel);
                if (progress > 0)
                    observedProjection = elapsed * Math.Pow(p.TargetLevel / (double)progress, 2.0) * 1.50;
            }
            else if (c.bossID > 0 && p.TargetBoss >= 0)
            {
                observedProjection = elapsed * (p.TargetBoss + 1.0) / c.bossID * 3.0;
            }
            var sampleProjection = sample > 0 ? sample * 1.50 : 0.0;
            var evidenceProjection = Math.Max(observedProjection, sampleProjection);
            if (evidenceProjection <= 0.0) evidenceProjection = FirstClearBoundSeconds;
            var total = Math.Max(elapsed + 60.0, evidenceProjection);
            if (p.Type == ChallengeType.TwentyFourHour)
                total = Math.Min(86400.0, total);
            p.PessimisticTotalSeconds = total;
            p.EtaSeconds = (int)Math.Max(0.0, Math.Ceiling(total - elapsed));
            p.EtaReason = p.TargetLevel >= 0
                ? p.EtaSeconds + "s p90 remaining from quadratic native level-cost progress; " + p.Objective
                : remainingBosses == 0
                    ? "native completion predicate is due on the controller Update"
                    : p.EtaSeconds + "s p90 remaining; " + remainingBosses
                      + " Bosses remain and ETA recalibrates after each Boss/rebirth transition";
        }

        private static void Add(Character c, ICollection<ChallengeAdmission> result,
            ChallengeType type, string code, int complete, int maxCompletions, int targetBoss,
            int targetLevel, int rawBestTime, int highestBoss, double basicBest,
            int firstClearHeadroom, double benefitWeight, string constraints, string reward)
        {
            if (maxCompletions <= 0 || complete < 0 || complete >= maxCompletions
                || !BaseRebirth.ChallengeUnlocked(c.allChallenges, type))
                return;

            var sameTypeBest = ValidBestTime(rawBestTime);
            double pessimistic;
            string evidence;
            if (sameTypeBest > 0.0
                && (c.settings.rebirthDifficulty == difficulty.normal || complete > 0))
            {
                var targetGrowth = targetBoss < 0 ? Math.Max(0, targetLevel - 2)
                    : Math.Max(0, targetBoss - BaseTarget(type));
                pessimistic = sameTypeBest * 1.50 * (1.0 + .05 * targetGrowth);
                if (c.settings.rebirthDifficulty != difficulty.normal)
                    pessimistic = Math.Max(28800.0, pessimistic);
                evidence = "global same-type native best " + FormatDuration(sameTypeBest)
                           + ", 50% tail + " + targetGrowth + " exact target steps";
                if (targetBoss >= 0 && highestBoss < targetBoss + 10) return;
            }
            else if (type == ChallengeType.Basic && c.settings.rebirthDifficulty == difficulty.normal
                     && complete == 0 && highestBoss >= targetBoss + firstClearHeadroom)
            {
                pessimistic = 28800.0;
                evidence = "first Normal Basic: historical record exceeds target by "
                           + (highestBoss - targetBoss) + " Bosses; conservative 8h envelope";
            }
            else if (type == ChallengeType.Basic && complete == 0
                     && c.settings.rebirthDifficulty != difficulty.normal
                     && highestBoss >= targetBoss + 25)
            {
                pessimistic = 28800.0;
                evidence = "first " + c.settings.rebirthDifficulty
                           + " Basic is attached to a post-transition fresh climb with 25+ Boss headroom";
            }
            else
            {
                if (basicBest <= 0.0 || targetBoss >= 0 && highestBoss < targetBoss + firstClearHeadroom)
                    return;
                if (targetLevel >= 0 && highestBoss < 57 + firstClearHeadroom)
                    return;
                pessimistic = FirstClearBoundSeconds;
                evidence = "first-clear proof: native Basic clear " + FormatDuration(basicBest)
                           + ", difficulty record headroom "
                           + (targetBoss >= 0 ? highestBoss - targetBoss : highestBoss - 57)
                           + ", fail-closed 18h envelope";
            }

            if (type == ChallengeType.TwentyFourHour && pessimistic > FirstClearBoundSeconds)
                return;
            var recovery = RecoverySeconds(c, type, complete);
            var titanCost = EstimatedTitanOpportunitySeconds(pessimistic);
            var score = benefitWeight / Math.Max(.25, (pessimistic + recovery + titanCost) / 3600.0);
            result.Add(new ChallengeAdmission
            {
                Type = type, Code = code, Completion = complete + 1,
                CompletedBefore = complete, MaxCompletions = maxCompletions,
                TargetBoss = targetBoss, TargetLevel = targetLevel,
                PessimisticClearSeconds = pessimistic, RecoverySeconds = recovery,
                TitanOpportunitySeconds = titanCost, BenefitWeight = benefitWeight,
                Constraints = constraints, Reward = reward, Evidence = evidence, Score = score
            });
        }

        private static void AssignBossChallenge(Character c, ActiveChallengePolicy p)
        {
            if (c.challenges.basicChallenge.inChallenge)
            { p.Type = ChallengeType.Basic; p.Code = "BASIC"; p.TargetBoss = c.allChallenges.basicChallenge.targetBoss(); }
            else if (c.challenges.noAugsChallenge.inChallenge)
            { p.Type = ChallengeType.NoAug; p.Code = "NOAUG"; p.TargetBoss = c.allChallenges.noAugsChallenge.targetBoss(); }
            else if (c.challenges.hour24Challenge.inChallenge)
            { p.Type = ChallengeType.TwentyFourHour; p.Code = "24HR"; p.TargetBoss = c.allChallenges.hour24Challenge.targetBoss(); }
            else if (c.challenges.noEquipmentChallenge.inChallenge)
            { p.Type = ChallengeType.NoEquip; p.Code = "NOEC"; p.TargetBoss = c.allChallenges.noEquipmentChallenge.targetBoss(); }
            else if (c.challenges.blindChallenge.inChallenge)
            { p.Type = ChallengeType.Blind; p.Code = "BLIND"; p.TargetBoss = c.allChallenges.blindChallenge.targetBoss(); }
            else if (c.challenges.nguChallenge.inChallenge)
            { p.Type = ChallengeType.NoNGU; p.Code = "NONGU"; p.TargetBoss = c.allChallenges.NGUChallenge.targetBoss(); }
            else if (c.challenges.timeMachineChallenge.inChallenge)
            { p.Type = ChallengeType.NoTimeMachine; p.Code = "NOTM"; p.TargetBoss = c.allChallenges.timeMachineChallenge.targetBoss(); }
        }

        private static double ActiveElapsedSeconds(Character c, ChallengeType type)
        {
            switch (type)
            {
                case ChallengeType.Basic: return c.challenges.basicChallenge.challengeTime.totalseconds;
                case ChallengeType.NoAug: return c.challenges.noAugsChallenge.challengeTime.totalseconds;
                case ChallengeType.TwentyFourHour: return c.challenges.hour24Challenge.challengeTime.totalseconds;
                case ChallengeType.OneHundredLC: return c.challenges.levelChallenge10k.challengeTime.totalseconds;
                case ChallengeType.NoEquip: return c.challenges.noEquipmentChallenge.challengeTime.totalseconds;
                case ChallengeType.Troll: return c.challenges.trollChallenge.challengeTime.totalseconds;
                case ChallengeType.NoRebirth: return c.challenges.noRebirthChallenge.challengeTime.totalseconds;
                case ChallengeType.LaserSword: return c.challenges.laserSwordChallenge.challengeTime.totalseconds;
                case ChallengeType.Blind: return c.challenges.blindChallenge.challengeTime.totalseconds;
                case ChallengeType.NoNGU: return c.challenges.nguChallenge.challengeTime.totalseconds;
                case ChallengeType.NoTimeMachine: return c.challenges.timeMachineChallenge.challengeTime.totalseconds;
                default: return 0.0;
            }
        }

        private static int ActiveBestTime(Character c, ChallengeType type)
        {
            switch (type)
            {
                case ChallengeType.Basic: return c.challenges.basicChallenge.bestTime;
                case ChallengeType.NoAug: return c.challenges.noAugsChallenge.bestTime;
                case ChallengeType.TwentyFourHour: return c.challenges.hour24Challenge.bestTime;
                case ChallengeType.OneHundredLC: return c.challenges.levelChallenge10k.bestTime;
                case ChallengeType.NoEquip: return c.challenges.noEquipmentChallenge.bestTime;
                case ChallengeType.Troll: return c.challenges.trollChallenge.bestTime;
                case ChallengeType.NoRebirth: return c.challenges.noRebirthChallenge.bestTime;
                case ChallengeType.LaserSword: return c.challenges.laserSwordChallenge.bestTime;
                case ChallengeType.Blind: return c.challenges.blindChallenge.bestTime;
                case ChallengeType.NoNGU: return c.challenges.nguChallenge.bestTime;
                case ChallengeType.NoTimeMachine: return c.challenges.timeMachineChallenge.bestTime;
                default: return int.MaxValue;
            }
        }

        private static int BaseTarget(ChallengeType type)
        {
            if (type == ChallengeType.NoRebirth) return 39;
            if (type == ChallengeType.NoEquip) return 65;
            if (type == ChallengeType.Troll) return 68;
            return 57;
        }

        private static int ActiveHighestBoss(Character c)
        {
            return c.settings.rebirthDifficulty == difficulty.normal ? c.highestBoss
                : c.settings.rebirthDifficulty == difficulty.evil ? c.highestHardBoss
                : c.highestSadisticBoss;
        }

        private static double ValidBestTime(int raw)
        {
            return raw > 0 && raw < int.MaxValue ? raw : -1.0;
        }

        private static double RecoverySeconds(Character c, ChallengeType type, int complete)
        {
            if (type == ChallengeType.Basic && complete == 0
                && c.settings.rebirthDifficulty != difficulty.normal)
                return 600.0;
            return c.settings.rebirthDifficulty == difficulty.normal ? 3600.0 : 7200.0;
        }

        private static double NearestTitanSeconds()
        {
            var nearest = double.PositiveInfinity;
            var reachable = ZoneHelpers.GetMaxReachableZone(true);
            for (var i = 0; i < 14; i++)
            {
                if (ZoneHelpers.TitanZones[i] > reachable
                    || !ZoneHelpers.TitanStateSignature(i, false).Contains("|unlock=True|"))
                    continue;
                var seconds = ZoneHelpers.SecondsUntilTitanSpawn(i);
                if (seconds >= 0.0) nearest = Math.Min(nearest, seconds);
            }
            return double.IsPositiveInfinity(nearest) ? -1.0 : nearest;
        }

        private static double EstimatedTitanOpportunitySeconds(double clearSeconds)
        {
            return Math.Min(TitanMechanics.BaseSeconds(12), Math.Max(3600.0, clearSeconds * .20));
        }

        private static double RemainingTitanClockBenefit(Character c)
        {
            var remaining = 0;
            if (!c.adventure.titan7Unlocked) remaining++;
            if (!c.adventure.titan8Unlocked) remaining++;
            if (!c.adventure.titan9Unlocked) remaining++;
            if (!c.adventure.titan10Unlocked) remaining++;
            if (!c.adventure.titan11Unlocked) remaining++;
            if (!c.adventure.titan12Unlocked) remaining++;
            return 300.0 + 90.0 * remaining;
        }

        private static string AdmissionSummary(ChallengeAdmission x)
        {
            var target = x.TargetLevel >= 0 ? "levels " + x.TargetLevel + "/" + x.TargetLevel
                : "Boss " + (x.TargetBoss + 1);
            return x.ProfileCode + " [" + x.CompletedBefore + "/" + x.MaxCompletions
                   + " -> " + x.Completion + ", " + target + ", p90 " + x.EtaText
                   + ", recovery " + FormatDuration(x.RecoverySeconds) + "]: " + x.Evidence
                   + "; " + x.Reward;
        }

        private static string RemainingStateSummary(Character c, int highest, double basicBest)
        {
            return "No safe challenge entry now: difficulty " + c.settings.rebirthDifficulty
                   + ", record Boss " + (highest + 1) + ", Basic sample "
                   + (basicBest > 0 ? FormatDuration(basicBest) : "none")
                   + "; locked/maxed entries or first clears lack conservative headroom";
        }

        private static string ChallengeLedger(Character c)
        {
            var a = c.allChallenges;
            return "ledger " + c.settings.rebirthDifficulty + ": "
                   + LedgerEntry(c, ChallengeType.Basic, "B", a.basicChallenge.currentCompletions(), a.basicChallenge.maxCompletions)
                   + LedgerEntry(c, ChallengeType.NoAug, "A", a.noAugsChallenge.currentCompletions(), a.noAugsChallenge.maxCompletions)
                   + LedgerEntry(c, ChallengeType.TwentyFourHour, "24", a.hour24Challenge.currentCompletions(), a.hour24Challenge.maxCompletions)
                   + LedgerEntry(c, ChallengeType.OneHundredLC, "100", a.level100Challenge.currentCompletions(), a.level100Challenge.maxCompletions)
                   + LedgerEntry(c, ChallengeType.NoEquip, "E", a.noEquipmentChallenge.currentCompletions(), a.noEquipmentChallenge.maxCompletions)
                   + LedgerEntry(c, ChallengeType.Troll, "T", a.trollChallenge.currentCompletions(), a.trollChallenge.maxCompletions)
                   + LedgerEntry(c, ChallengeType.NoRebirth, "R", a.noRebirthChallenge.currentCompletions(), a.noRebirthChallenge.maxCompletions)
                   + LedgerEntry(c, ChallengeType.LaserSword, "L", a.laserSwordChallenge.currentCompletions(), a.laserSwordChallenge.maxCompletions)
                   + LedgerEntry(c, ChallengeType.Blind, "D", a.blindChallenge.currentCompletions(), a.blindChallenge.maxCompletions)
                   + LedgerEntry(c, ChallengeType.NoNGU, "N", a.NGUChallenge.currentCompletions(), a.NGUChallenge.maxCompletions)
                   + LedgerEntry(c, ChallengeType.NoTimeMachine, "M", a.timeMachineChallenge.currentCompletions(), a.timeMachineChallenge.maxCompletions);
        }

        private static string LedgerEntry(Character c, ChallengeType type, string code, int complete, int max)
        {
            return code + "=" + complete + "/" + max
                   + (BaseRebirth.ChallengeUnlocked(c.allChallenges, type) ? "U" : "L") + ",";
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds < 3600.0) return Math.Ceiling(seconds / 60.0) + "m";
            return (seconds / 3600.0).ToString("0.0") + "h";
        }

        private static string NativeReward(long expectedExp, string expectedAp, string special)
        {
            return "native next reward " + expectedExp + " EXP, AP "
                   + Compact(expectedAp) + ", " + Compact(special);
        }

        private static string Compact(string value)
        {
            return string.IsNullOrEmpty(value) ? "none"
                : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }
}
