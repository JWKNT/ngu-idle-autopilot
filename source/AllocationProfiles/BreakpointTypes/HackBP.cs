using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/*
FILE PURPOSE

HackBP allocates Resource 3 to a specified permanent Hack level/cap using native hack controllers.
It validates unlocks and target bounds before mutation. Strategy for which Hack wins belongs to
BestHackBP/plan composition; this class owns exact per-track execution.
*/
namespace NGUInjector.AllocationProfiles.BreakpointTypes
{
    internal class HackBP : BaseBreakpoint
    {
        protected override bool Unlocked()
        {
            return Index <= 14 && Character.buttons.hacks.interactable;
        }

        protected override bool TargetMet()
        {
            return Character.hacksController.hitTarget(Index);
        }

        internal override bool Allocate()
        {
            var alloc = MaxAllocation;
            Character.hacksController.addR3(Index, (long)alloc);
            return true;
        }

        protected override bool CorrectResourceType()
        {
            return Type == ResourceType.R3;
        }
    }
}
