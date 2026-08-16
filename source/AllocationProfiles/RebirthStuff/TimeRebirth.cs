using System;
using NGUInjector.Autopilot;
using NGUInjector.Managers;

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
