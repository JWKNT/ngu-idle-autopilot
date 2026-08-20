using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

WandoosBP allocates Energy or Magic to the installed operating-system progress bar through native
controllers. Wandoos levels vanish at rebirth, so the breakpoint refuses terminal allocations that
cannot complete a level and leave a useful multiplier window. The planner owns OS-switch payback;
this class owns exact next-level admission, cap calculation, and native mutation.
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
                return AllocateEnergy();
            }
            if (Type == ResourceType.Magic)
            {
                return AllocateMagic();
            }
            return false;
        }

        private bool AllocateEnergy()
        {
            var cap = Character.wandoos98Controller.capAmountEnergy();
            var allocation = Math.Min(cap, MaxAllocation);
            double completion;
            if (!ExactResourceAllocator.ResetLocalLevelHasUseWindow(
                    Character.wandoos98.energyProgress, allocation,
                    Character.totalWandoosEnergySpeed(), BaseTime(),
                    RemainingRebirthHorizon(), out completion)
                || !SetInput(allocation))
                return false;
            Character.wandoos98Controller.addEnergy();
            return true;
        }

        private bool AllocateMagic()
        {
            var cap = Character.wandoos98Controller.capAmountMagic();
            var allocation = Math.Min(cap, MaxAllocation);
            double completion;
            if (!ExactResourceAllocator.ResetLocalLevelHasUseWindow(
                    Character.wandoos98.magicProgress, allocation,
                    Character.totalWandoosMagicSpeed(), BaseTime(),
                    RemainingRebirthHorizon(), out completion)
                || !SetInput(allocation))
                return false;
            Character.wandoos98Controller.addMagic();
            return true;
        }

        private double BaseTime()
        {
            var os = (int)Character.wandoos98.os;
            if (Character.settings.rebirthDifficulty == difficulty.normal)
                return os == 2 ? 1e15 : os == 1 ? 1e12 : 1e9;
            return os == 2 ? 1e33 : os == 1 ? 1e27 : 1e21;
        }

        protected override bool CorrectResourceType()
        {
            return Type == ResourceType.Energy || Type == ResourceType.Magic;
        }
    }
}
