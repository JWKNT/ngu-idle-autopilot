using System;
using NGUInjector.Autopilot;
using NGUInjector.Managers;

/*
FILE PURPOSE

TimeRebirth bridges the optimizer's exact selected run age to BaseRebirth's final safety checks.
It revalidates the no-reset counterfactual, aggregate persistent score, final Blood-adjusted native Number
preview, nearby boss events, synchronization, and discrete Titan events at execution time. Ordinary
rebirth and challenge entry are distinct authorizations: challenge policy can cross a reset boundary
without inheriting the ordinary utility score, while ordinary rebirth can never use challenge
eligibility to bypass a hold. Invalid or stale preview state fails closed.
*/
namespace NGUInjector.AllocationProfiles.RebirthStuff
{
    internal class TimeRebirth : BaseRebirth
    {
        internal double RebirthTime { get; set; }

        internal override bool RebirthAvailable()
        {
            if (!Main.Settings.AutoRebirth && !Main.AutopilotWants(x => x.AllowRebirths))
                return false;

            if (RebirthTime < 0)
                return false;

            if (!BaseRebirthChecks())
                return false;

            var time = CharObj.rebirthTime.totalseconds;
            if (time < RebirthTime)
                return false;

            // This is the final derived-state publication boundary. Native engage does not call
            // either calculator, and TimerUp/Rebirth Update order is not constrained. Always call
            // the build-pinned native methods in native order immediately before policy reads.
            if (!SynchronizeFinalPreview())
                return false;

            /*
            CHALLENGE VS ORDINARY AUTHORIZATION

            ChallengeTargets are populated only by the challenge planner/user profile. In full
            autopilot, AllowChallenges is an additional explicit gate; an ordinary reset hold must
            not be bypassed merely because a challenge happens to be valid, and challenge entry is
            not rejected merely because its intentionally destructive reset has negative ordinary
            cycle utility.
            */
            var challengeReset = !CharObj.challenges.inChallenge && AnyChallengesValid();
            if (challengeReset && Main.AutopilotWants(x => x.AllowRebirths)
                && !Main.AutopilotWants(x => x.AllowChallenges))
                return false;

            if (double.IsNaN((double)CharObj.nextAttackMulti)
                || double.IsInfinity((double)CharObj.nextAttackMulti)
                || double.IsNaN((double)CharObj.nextDefenseMulti)
                || double.IsInfinity((double)CharObj.nextDefenseMulti)
                || CharObj.nextAttackMulti <= 0 || CharObj.nextDefenseMulti <= 0)
                return false;

            if (!challengeReset)
            {
                // Legacy profiles and explicitly locked puzzle checkpoints carry authorization
                // outside the optimizer score; use a finite positive sentinel, never Infinity
                // (which the pure admission kernel correctly treats as invalid evidence).
                var selectedScore = double.MaxValue;
                var plan = Main.Autopilot == null ? null : Main.Autopilot.Plan;
                var autopilotOrdinary = Main.AutopilotWants(x => x.AllowRebirths);
                if (autopilotOrdinary && plan != null)
                {
                    if (plan.RebirthExecutionHold) return false;
                    if (!plan.RebirthTargetLocked)
                        selectedScore = plan.RebirthSelectedScorePerHour;

                    // Recompute from the final live preview. This is especially important when
                    // Blood NUMBER was cast earlier in this same automation transaction.
                    var earlyNormal = CharObj.settings.rebirthDifficulty == difficulty.normal
                                      && !(CharObj.inventory.itemList.numberComplete
                                           || CharObj.settings.nguOn);
                    if (earlyNormal)
                    {
                        var live = RebirthOptimizer.EarlyNormal(CharObj);
                        if (live.ExecutionHold || live.TargetSeconds > (int)Math.Floor(time))
                            return false;
                        selectedScore = live.SelectedScorePerHour;
                    }
                    else if (!plan.RebirthTargetLocked)
                    {
                        var live = StrategyCheckpointPlanner.Select(CharObj,
                            plan.RebirthSeconds, plan.RebirthReason);
                        if (live.ExecutionHold || live.TargetSeconds > (int)Math.Floor(time))
                            return false;
                        selectedScore = live.SelectedScorePerHour;
                    }
                }

                var minimumNumberRatio = Math.Min(
                    CharObj.attackMulti > 0.0 ? CharObj.nextAttackMulti / CharObj.attackMulti : 0.0,
                    CharObj.defenseMulti > 0.0 ? CharObj.nextDefenseMulti / CharObj.defenseMulti : 0.0);

                // Native engage replaces Number; it does not multiply the new preview by the
                // current Number. The former recovery branch geometrically compounded the same
                // projected/current ratio across repeated cycles and could therefore veto a
                // positive EXP/AP/training-cap reset forever while below the Boss record. The
                // live aggregate score already prices the one-run Number loss. Recheck that score
                // here, but do not impose the invalid repeated-ratio counterfactual.
                var decision = RebirthOptimizer.EvaluateMutationPolicy(selectedScore, true,
                    minimumNumberRatio, false, -1, -1);
                if (!decision.Authorized) return false;

                // A kill inside the next controller tick is a discrete strict improvement not
                // represented safely by a one-second planner race. Bank it before reconsidering.
                if (CharObj.settings.rebirthDifficulty == difficulty.normal)
                {
                    var imminentEta = AutopilotManager.SelectedBossDefeatEta(CharObj, 5);
                    if (imminentEta >= 0 && imminentEta <= 2) return false;
                }
            }
            // Every challenge start performs the common rebirth reset and must obey the same
            // checkpoint/Titan boundary. Ten challenge wrappers hard-reset Number; Laser Sword
            // alone uses the ordinary soft transition and banks this synchronized preview.
            // Adventure Titans are also discrete persistent progression events.
            // Never reset between their ready spawn and controller dispatch, or
            // during the fight itself.
            if (ZoneHelpers.HighestAvailableTitan() >= 0
                || (ZoneHelpers.ZoneIsTitan(CharObj.adventure.zone)
                    && (CharObj.adventureController.currentEnemy != null
                        || CharObj.adventureController.fightInProgress)))
                return false;

            // A boss-record transition can move early Normal from the 30-minute
            // cycle to the GRB one-hour window between planner refreshes.
            if (CharObj.settings.rebirthDifficulty == difficulty.normal
                && CharObj.highestBoss >= 58 && RebirthTime < 3600 && time < 3600)
                return false;

            return true;
        }

        /*
        SYNCHRONIZED NATIVE PREVIEW

        calculateTimeMulti and calculateNextMultis are derived-state writes, not the reset itself.
        Both are nevertheless build-pinned because accepting a name-only overload at this boundary
        could bank stale Number. Capture every formula input which the callback pair must not change,
        invoke in exact native order, verify the time branch and stable snapshot, then allow the
        caller to inspect preview. Unknown build, missing binding, throw, non-finite output, or an
        input change fails closed without falling back to ordinary reflection.
        */
        private bool SynchronizeFinalPreview()
        {
            if (CharObj == null || CharObj.rebirth == null || CharObj.rebirthTime == null
                || CharObj.training == null || CharObj.bloodMagic == null)
                return false;

            var run = CharObj.stats == null ? -1L : CharObj.stats.rebirthNumber;
            var time = CharObj.rebirthTime.totalseconds;
            var bloodPower = CharObj.bloodMagic.rebirthPower;
            var bossMulti = (double)CharObj.bossMulti;
            var oldBossMulti = (double)CharObj.oldBossMulti;
            var oldTimeMulti = (double)CharObj.oldTimeMulti;
            var attackLevels = CharObj.training.totalAttackLevels;
            try
            {
                var registry = NativeBindingRegistry.Create(typeof(Character).Assembly,
                    Main.GameAssemblySha256);
                var native = registry.CreateMutationAdapters();
                var timeResult = native.RefreshRebirthTimeMultiplier(CharObj.rebirth);
                if (!timeResult.ReturnedNormally) return false;
                var previewResult = native.RefreshRebirthPreview(CharObj.rebirth);
                if (!previewResult.ReturnedNormally) return false;
            }
            catch
            {
                return false;
            }

            var expectedTimeMulti = RebirthTransitionKernel.ExactTimeMultiplier(time);
            if (!NearlyEqual((double)CharObj.timeMulti, expectedTimeMulti)
                || !Stable(time, CharObj.rebirthTime.totalseconds)
                || run != (CharObj.stats == null ? -1L : CharObj.stats.rebirthNumber)
                || !Stable(bloodPower, CharObj.bloodMagic.rebirthPower)
                || !Stable(bossMulti, (double)CharObj.bossMulti)
                || !Stable(oldBossMulti, (double)CharObj.oldBossMulti)
                || !Stable(oldTimeMulti, (double)CharObj.oldTimeMulti)
                || attackLevels != CharObj.training.totalAttackLevels
                || !RebirthTransitionKernel.FinitePositive((double)CharObj.nextAttackMulti)
                || !RebirthTransitionKernel.FinitePositive((double)CharObj.nextDefenseMulti))
                return false;

            // Full autopilot reserves the entire remaining pool for Blood NUMBER at the selected
            // checkpoint. A non-empty pool means that cast/verification has not completed yet.
            if (Main.AutopilotWants(x => x.ManageBloodMagic)
                && CharObj.bloodMagic != null && CharObj.bloodMagic.bloodPoints > 0.0)
                return false;
            return true;
        }

        private static bool Stable(double left, double right)
        {
            return !double.IsNaN(left) && !double.IsNaN(right)
                   && !double.IsInfinity(left) && !double.IsInfinity(right)
                   && left == right;
        }

        private static bool NearlyEqual(double left, double right)
        {
            if (double.IsNaN(left) || double.IsNaN(right)
                || double.IsInfinity(left) || double.IsInfinity(right)) return false;
            return Math.Abs(left - right) <= Math.Max(1e-12,
                Math.Max(Math.Abs(left), Math.Abs(right)) * 2e-7);
        }
    }
}
