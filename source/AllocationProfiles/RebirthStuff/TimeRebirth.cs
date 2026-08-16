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
        internal double RebirthTime { get; set; }

        internal override bool RebirthAvailable()
        {
            if (!Main.Settings.AutoRebirth && !Main.AutopilotWants(x => x.AllowRebirths))
                return false;

            if (RebirthTime < 0)
                return false;

            if (!BaseRebirthChecks())
                return false;

            // Ordinary Normal rebirths must never lower Number or reset again while
            // native boss catch-up is unfinished. Both failures were observed live:
            // short 197/316-second cycles compounded a lower Number and repeatedly
            // threw away the climb back to the persistent record boss. Challenge
            // starts are exempt because their permanent completion reward is the
            // explicit objective of that reset.
            var challengeReset = !CharObj.challenges.inChallenge && AnyChallengesValid();
            if (CharObj.settings.rebirthDifficulty == difficulty.normal && !challengeReset)
            {
                if (CharObj.bossID != CharObj.highestBoss)
                    return false;
                if (CharObj.nextAttackMulti <= CharObj.attackMulti
                    || CharObj.nextDefenseMulti <= CharObj.defenseMulti)
                    return false;
            }

            var time = CharObj.rebirthTime.totalseconds;
            // A challenge start is itself a hard rebirth.  It must obey the same
            // optimized checkpoint as an ordinary rebirth so it cannot preempt a
            // Titan spawn, puzzle window, or long-cycle permanent-growth harvest.
            if (time < RebirthTime)
                return false;

            // The planner and fight controller run on different cadences.  Recheck
            // the exact selected-boss event at the point of mutation so we cannot
            // throw away a catch-up/record kill whose training or pending Augment
            // breakpoint is at most two minutes away.
            if ((Main.Settings.AutoRebirth || Main.AutopilotWants(x => x.AllowRebirths))
                && AutopilotManager.SelectedBossDefeatEta(CharObj, 120) >= 0)
                return false;

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
