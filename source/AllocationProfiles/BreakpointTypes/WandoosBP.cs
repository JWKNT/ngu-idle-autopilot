using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/*
FILE PURPOSE

WandoosBP allocates Energy or Magic to the installed operating-system progress bar through native
controllers. Wandoos levels vanish at rebirth, so the breakpoint refuses terminal allocations that
cannot leave a useful multiplier window. The planner owns OS-switch payback; this class owns cap
calculation and native mutation.
*/
namespace NGUInjector.AllocationProfiles.BreakpointTypes
{
    internal class WandoosBP : BaseBreakpoint
    {
        protected override bool Unlocked()
        {
            return Character.buttons.wandoos.interactable && !Character.wandoos98.disabled
                   && RemainingRebirthHorizon() > 60.0;
        }

        protected override bool TargetMet()
        {
            return false;
        }

        internal override bool Allocate()
        {
            if (Type == ResourceType.Energy)
            {
                AllocateEnergy();
            }
            else
            {
                AllocateMagic();
            }

            return true;
        }

        private void AllocateEnergy()
        {
            var cap = Character.wandoos98Controller.capAmountEnergy();
            SetInput(Math.Min(cap, MaxAllocation));
            Character.wandoos98Controller.addEnergy();
        }

        private void AllocateMagic()
        {
            var cap = Character.wandoos98Controller.capAmountMagic();
            SetInput(Math.Min(cap, MaxAllocation));
            Character.wandoos98Controller.addMagic();
        }

        protected override bool CorrectResourceType()
        {
            return Type == ResourceType.Energy || Type == ResourceType.Magic;
        }
    }
}
