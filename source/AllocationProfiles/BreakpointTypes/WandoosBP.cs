using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/*
FILE PURPOSE

WandoosBP allocates Energy or Magic to the installed operating-system progress bar through native
controllers. The planner decides whether its reset-local multiplier can pay before rebirth; this
class owns cap calculation and mutation only.
*/
namespace NGUInjector.AllocationProfiles.BreakpointTypes
{
    internal class WandoosBP : BaseBreakpoint
    {
        protected override bool Unlocked()
        {
            return Character.buttons.wandoos.interactable && !Character.wandoos98.disabled;
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
