using System;
using System.Reflection;
using NGUInjector.Autopilot;
using NGUInjector.Managers;

/*
FILE PURPOSE

TimeRebirth bridges the optimizer's exact selected run age to BaseRebirth's final safety checks.
It revalidates the no-reset counterfactual, recovery route, final Blood-adjusted native Number
preview, nearby boss events, synchronization, and discrete Titan events at execution time. Ordinary
rebirth and challenge entry are distinct authorizations: challenge policy can cross a reset boundary
without inheriting the ordinary utility score, while ordinary rebirth can never use challenge
eligibility to bypass a hold. Invalid or stale preview state fails closed.
*/
namespace NGUInjector.AllocationProfiles.RebirthStuff
{
    internal class TimeRebirth : BaseRebirth
    {
        private static int _recoveryEtaSecond = -1;
        private static int _recoveryEtaBoss = -1;
        private static int _recoveryEta = -1;
        private static long _bloodPreviewRun = -1;
        private static double _bloodPreviewPower = double.NaN;
        private static double _bloodPreviewAttack = double.NaN;
        private static double _bloodPreviewDefense = double.NaN;
        private static double _bloodAwaitAttack = double.NaN;
        private static double _bloodAwaitDefense = double.NaN;
        private static bool _bloodAwaitingReflection;

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
            var previewReady = ObserveFinalBloodPreview(time >= RebirthTime);
            if (time < RebirthTime)
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
                || CharObj.nextAttackMulti <= 0 || CharObj.nextDefenseMulti <= 0
                || !previewReady)
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

                var recoveryMode = CharObj.settings.rebirthDifficulty == difficulty.normal
                                   && CharObj.bossID < CharObj.highestBoss;
                var resetRouteEta = -1;
                var continueRouteEta = -1;
                if (recoveryMode)
                {
                    var elapsedSecond = (int)Math.Floor(time);
                    if (_recoveryEtaSecond != elapsedSecond || _recoveryEtaBoss != CharObj.bossID)
                    {
                        _recoveryEtaSecond = elapsedSecond;
                        _recoveryEtaBoss = CharObj.bossID;
                        _recoveryEta = AutopilotManager.SelectedBossDefeatEta(CharObj, 172800);
                    }
                    string recoveryReason;
                    RebirthOptimizer.RecoveryResetEfficient(CharObj, _recoveryEta,
                        out resetRouteEta, out continueRouteEta, out recoveryReason);
                }

                var minimumNumberRatio = Math.Min(
                    CharObj.attackMulti > 0.0 ? CharObj.nextAttackMulti / CharObj.attackMulti : 0.0,
                    CharObj.defenseMulti > 0.0 ? CharObj.nextDefenseMulti / CharObj.defenseMulti : 0.0);
                var decision = RebirthOptimizer.EvaluateMutationPolicy(selectedScore, true,
                    minimumNumberRatio, recoveryMode, resetRouteEta, continueRouteEta);
                if (!decision.Authorized) return false;

                // A kill inside the next controller tick is a discrete strict improvement not
                // represented safely by a one-second planner race. Bank it before reconsidering.
                if (CharObj.settings.rebirthDifficulty == difficulty.normal)
                {
                    var imminentEta = AutopilotManager.SelectedBossDefeatEta(CharObj, 5);
                    if (imminentEta >= 0 && imminentEta <= 2) return false;
                }
            }
            // A challenge start is itself a hard rebirth.  It must obey the same
            // optimized checkpoint as an ordinary rebirth so it cannot preempt a
            // Titan spawn, puzzle window, or long-cycle permanent-growth harvest.
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
        BLOOD-ADJUSTED NATIVE PREVIEW

        RebirthPowerSpell.castRebirthSpell changes bloodMagic.rebirthPower but does not call
        Rebirth.calculateNextMultis; the private calculation normally runs from Unity Update. The
        automation sweep can cast Blood NUMBER and reach this method in the same frame, leaving
        nextAttackMulti/nextDefenseMulti stale. Observe every pre-checkpoint pass, invoke that exact
        native calculator when a power delta is seen, and require a verified preview delta. If the
        method is unavailable or the delta is not reflected, hold for a later Unity frame.
        */
        private bool ObserveFinalBloodPreview(bool due)
        {
            var run = CharObj.stats == null ? -1L : CharObj.stats.rebirthNumber;
            var power = CharObj.bloodMagic == null ? 0.0 : CharObj.bloodMagic.rebirthPower;
            var attack = (double)CharObj.nextAttackMulti;
            var defense = (double)CharObj.nextDefenseMulti;
            if (_bloodPreviewRun != run || double.IsNaN(_bloodPreviewPower))
            {
                _bloodPreviewRun = run;
                _bloodPreviewPower = power;
                _bloodPreviewAttack = attack;
                _bloodPreviewDefense = defense;
                _bloodAwaitingReflection = due && power > 0.0;
                _bloodAwaitAttack = attack;
                _bloodAwaitDefense = defense;
                return !_bloodAwaitingReflection;
            }

            if (_bloodAwaitingReflection
                && (MeaningfullyDifferent(attack, _bloodAwaitAttack)
                    || MeaningfullyDifferent(defense, _bloodAwaitDefense)))
                _bloodAwaitingReflection = false;

            var powerChanged = MeaningfullyDifferent(power, _bloodPreviewPower);
            if (powerChanged)
            {
                _bloodAwaitAttack = _bloodPreviewAttack;
                _bloodAwaitDefense = _bloodPreviewDefense;
                try
                {
                    var calculate = CharObj.rebirth.GetType().GetMethod("calculateNextMultis",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, Type.EmptyTypes, null);
                    if (calculate != null) calculate.Invoke(CharObj.rebirth, null);
                }
                catch
                {
                    // Unity Update may still publish the preview next frame; the awaiting latch
                    // below keeps the irreversible boundary closed until that is observable.
                }
                attack = (double)CharObj.nextAttackMulti;
                defense = (double)CharObj.nextDefenseMulti;
                _bloodAwaitingReflection = !MeaningfullyDifferent(attack, _bloodAwaitAttack)
                                           && !MeaningfullyDifferent(defense, _bloodAwaitDefense);
            }

            _bloodPreviewPower = power;
            _bloodPreviewAttack = attack;
            _bloodPreviewDefense = defense;

            // Full autopilot reserves the entire remaining pool for Blood NUMBER at the selected
            // checkpoint. A non-empty pool means that cast/verification has not completed yet.
            if (due && Main.AutopilotWants(x => x.ManageBloodMagic)
                && CharObj.bloodMagic != null && CharObj.bloodMagic.bloodPoints > 0.0)
                return false;
            return !_bloodAwaitingReflection;
        }

        private static bool MeaningfullyDifferent(double left, double right)
        {
            if (double.IsNaN(left) || double.IsNaN(right)) return false;
            if (double.IsInfinity(left) || double.IsInfinity(right)) return left != right;
            return Math.Abs(left - right) > Math.Max(1e-12,
                Math.Max(Math.Abs(left), Math.Abs(right)) * 1e-12);
        }
    }
}
