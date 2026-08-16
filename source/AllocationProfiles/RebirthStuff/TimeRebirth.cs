using System;
using NGUInjector.Autopilot;
using NGUInjector.Managers;

/*
FILE PURPOSE

TimeRebirth bridges the optimizer's exact selected run age to BaseRebirth's final safety checks.
It revalidates the active recommendation and nearby boss events at execution time so a stale plan
cannot reset through a valuable reachable kill. It must not substitute a rounded 30/60-minute
constant when full autopilot telemetry supplies a live target.
*/
namespace NGUInjector.AllocationProfiles.RebirthStuff
{
    internal class TimeRebirth : BaseRebirth
    {
        private static int _recoveryEtaSecond = -1;
        private static int _recoveryEtaBoss = -1;
        private static int _recoveryEta = -1;

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

            // Ordinary Normal rebirths must never lower Number. Catch-up itself is
            // not an absolute gate: requiring the old record can make recovery
            // exponentially slow. Below the record, compare continuing for the
            // selected boss with resetting to the higher Number and replaying the
            // multiplicative boss chain. Challenge starts remain separately exempt.
            var challengeReset = !CharObj.challenges.inChallenge && AnyChallengesValid();
            if (CharObj.settings.rebirthDifficulty == difficulty.normal && !challengeReset)
            {
                if (CharObj.nextAttackMulti <= CharObj.attackMulti
                    || CharObj.nextDefenseMulti <= CharObj.defenseMulti)
                    return false;

                if (CharObj.bossID < CharObj.highestBoss)
                {
                    var elapsedSecond = (int)Math.Floor(time);
                    if (_recoveryEtaSecond != elapsedSecond || _recoveryEtaBoss != CharObj.bossID)
                    {
                        _recoveryEtaSecond = elapsedSecond;
                        _recoveryEtaBoss = CharObj.bossID;
                        _recoveryEta = AutopilotManager.SelectedBossDefeatEta(CharObj, 7200);
                    }
                    int resetRouteEta;
                    int continueRouteEta;
                    string recoveryReason;
                    if (!RebirthOptimizer.RecoveryResetEfficient(CharObj, _recoveryEta,
                            out resetRouteEta, out continueRouteEta, out recoveryReason))
                        return false;
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
    }
}
