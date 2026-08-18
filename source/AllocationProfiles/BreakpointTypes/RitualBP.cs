using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/*
FILE PURPOSE

RitualBP allocates Magic to Blood Magic rituals, respecting unlocks, caps, and native controller
state. Ritual levels and Blood have different reset/persistence semantics, so spell/reserve
strategy lives in AutopilotManager; this breakpoint only realizes an admitted ritual target.
*/
using NGUInjector.Autopilot;

namespace NGUInjector.AllocationProfiles.BreakpointTypes
{
    internal class RitualBP : BaseBreakpoint
    {
        protected override bool Unlocked()
        {
            return Index < Character.bloodMagicController.ritualsUnlocked() && Character.buttons.bloodMagic.interactable;
        }

        protected override bool TargetMet()
        {
            return false;
        }

        internal override bool Allocate()
        {
            var goldCost = Character.bloodMagicController.bloodMagics[Index].baseCost * Character.totalDiscount();
            if (goldCost > Character.realGold && Character.bloodMagic.ritual[Index].progress <= 0)
            {
                if (Character.bloodMagic.ritual[Index].magic > 0)
                {
                    Character.bloodMagicController.bloodMagics[Index].removeAllMagic();
                }

                return true;
            }

            var cap = GetRitualCap(Index);
            var allocation = Math.Min(cap, MaxAllocation);
            if (allocation <= 0 || !SetInput(allocation))
                return false;
            Character.bloodMagicController.bloodMagics[Index].add();
            return true;
        }

        protected override bool CorrectResourceType()
        {
            return Type == ResourceType.Magic;
        }

        private long GetRitualCap(int index)
        {
            if (MaxAllocation <= 0)
                return 0L;
            if (Character.settings.rebirthDifficulty == difficulty.normal)
            {
                var num = Math.Ceiling(50000.0 * Character.bloodMagicController.normalSpeedDividers[index] / (Character.totalMagicPower() * (double)Character.bloodMagicController.bloodMagics[index].totalBloodMagicSpeedBonus())) * 1.000002;
                if (num < 1.0)
                    num = 1.0;
                if (num > Character.hardCap())
                    num = Character.hardCap();
                return ExactResourceAllocator.CapAtTickBoundary(num, MaxAllocation,
                    Character.magic.idleMagic);
            }
            if (Character.settings.rebirthDifficulty == difficulty.evil)
            {
                var num = Math.Ceiling(50000.0 * Character.bloodMagicController.evilSpeedDividers[index] / (Character.totalMagicPower() * (double)Character.bloodMagicController.bloodMagics[index].totalBloodMagicSpeedBonus())) * 1.00000202655792;
                if (num < 1.0)
                    num = 1.0;
                if (num > Character.hardCap())
                    num = Character.hardCap();
                return ExactResourceAllocator.CapAtTickBoundary(num, MaxAllocation,
                    Character.magic.idleMagic);
            }
            if (Character.settings.rebirthDifficulty == difficulty.sadistic)
            {
                var num = Math.Ceiling(Character.bloodMagicController.bloodMagics[index].sadisticDivider() * (double)Character.bloodMagicController.sadisticSpeedDividers[index] / (Character.totalMagicPower() * (double)Character.bloodMagicController.bloodMagics[index].totalBloodMagicSpeedBonus())) * 1.00000202655792;
                if (num < 1.0)
                    num = 1.0;
                if (num > Character.hardCap())
                    num = Character.hardCap();
                return ExactResourceAllocator.CapAtTickBoundary(num, MaxAllocation,
                    Character.magic.idleMagic);
            }

            return 0;
        }
    }
}
