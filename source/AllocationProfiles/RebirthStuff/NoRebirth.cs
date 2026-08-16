using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/*
FILE PURPOSE

NoRebirth is the explicit null strategy used by profiles that must never reset. It always denies
availability and performs no mutation. Keeping a concrete type avoids accidental fallback to a
timed rebirth when a challenge or manual session intentionally disables resets.
*/
namespace NGUInjector.AllocationProfiles.RebirthStuff
{
    internal class NoRebirth : BaseRebirth
    {
        internal override bool RebirthAvailable()
        {
            return false;
        }
    }
}
