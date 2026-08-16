using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/*
FILE PURPOSE

NumberRebirth is the explicit-profile strategy that waits for a requested Number multiplier
threshold. It reads the game's preview multiplier and delegates irreversible execution to
BaseRebirth. The adaptive full-mode timing model belongs to RebirthOptimizer, not this class.
*/
namespace NGUInjector.AllocationProfiles.RebirthStuff
{
    internal class NumberRebirth : BaseRebirth
    {
        internal double MultTarget { get; set; }
        internal override bool RebirthAvailable()
        {
            if (!Main.Settings.AutoRebirth && !Main.AutopilotWants(x => x.AllowRebirths))
                return false;

            if (!BaseRebirthChecks())
                return false;

            if (!CharObj.challenges.inChallenge && AnyChallengesValid())
                return true;

            var target = CharObj.attackMulti * MultTarget;

            return CharObj.nextAttackMulti > target;
        }
    }
}
