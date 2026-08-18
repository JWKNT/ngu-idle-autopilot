using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/*
FILE PURPOSE

HackBP allocates Resource 3 to a specified permanent Hack level/cap using the native long overload.
It validates installed bounds including ID 15 before mutation. Strategy for which Hack wins belongs
to BestHackBP/plan composition; this class owns exact per-track execution.
*/
namespace NGUInjector.AllocationProfiles.BreakpointTypes
{
    internal class HackBP : BaseBreakpoint
    {
        protected override bool Unlocked()
        {
            return Autopilot.ExactResourceAllocator.IsSupportedHackId(Index,
                       Character.hacks.hacks.Count)
                   && Character.buttons.hacks.interactable;
        }

        protected override bool TargetMet()
        {
            return Character.hacksController.hitTarget(Index);
        }

        internal override bool Allocate()
        {
            var alloc = MaxAllocation;
            if (alloc <= 0)
                return false;
            Character.hacksController.addR3(Index, alloc);
            return true;
        }

        protected override bool CorrectResourceType()
        {
            return Type == ResourceType.R3;
        }
    }
}
