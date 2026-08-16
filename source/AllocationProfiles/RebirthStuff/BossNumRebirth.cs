using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/*
FILE PURPOSE

BossNumRebirth is the legacy/profile strategy that waits for a configured boss-number condition
before delegating to BaseRebirth. It exists for explicit profiles, not the full autopilot's live
optimizer. Preserve base safety gates and do not use it to skip native boss progression.
*/
namespace NGUInjector.AllocationProfiles.RebirthStuff
{
    internal class BossNumRebirth : BaseRebirth
    {
        internal double NumBosses { get; set; }
        internal override bool RebirthAvailable()
        {
            if (!Main.Settings.AutoRebirth && !Main.AutopilotWants(x => x.AllowRebirths))
                return false;

            if (!BaseRebirthChecks())
                return false;

            if (!CharObj.challenges.inChallenge && AnyChallengesValid())
                return true;

            var bosses = Math.Round(Math.Log10(CharObj.nextAttackMulti / CharObj.attackMulti));
            return bosses > NumBosses;
        }
    }
}
