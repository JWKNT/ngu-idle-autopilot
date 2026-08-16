using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

/*
FILE PURPOSE

WishBP allocates Energy, Magic, and Resource 3 to one permanent Wish while enforcing unlock,
difficulty, slot, and attainability guards. Impossible serialized requirements must fail closed
instead of trapping resources forever. Wish ranking belongs to WishManager/planner policy.
*/
namespace NGUInjector.AllocationProfiles.BreakpointTypes
{
    internal class WishBP : BaseBreakpoint
    {
        protected override bool Unlocked()
        {
            if (Main.Autopilot != null && Main.Autopilot.CanExecuteSafe
                && Main.Autopilot.Config != null && !Main.Autopilot.Config.ManageWishes)
                return false;
            return Index <= Character.wishesController.curWishSlots() - 1 && Character.buttons.wishes.interactable;
        }

        protected override bool TargetMet()
        {
            return false;
        }

        internal override bool Allocate()
        {
            var id = Main.WishManager.GetSlot(Index);
            if (id == -1)
                return true;

            SetInput(MaxAllocation);
            var c = Character.wishesController;
            switch (Type)
            {
                case ResourceType.Energy:
                    c.addEnergy(id);
                    break;
                case ResourceType.Magic:
                    c.addMagic(id);
                    break;
                case ResourceType.R3:
                    var cap = Mathf.Ceil(Mathf.Pow(
                        c.minimumWishTime() * c.wishSpeedDivider(id) /
                        c.energyFactor(id) / c.magicFactor(id) /
                        c.totalWishSpeedBonuses(), (float)(1.0 / 0.17)) / Character.totalRes3Power());
                    Main.LogAllocation($"Calculated Wish R3 Cap: {cap}");
                    if (Character.energyMagicPanel.energyMagicInput > cap)
                    {
                        Main.LogAllocation($"Input was: {MaxAllocation}");
                        SetInput(cap);
                        c.addRes3(id);
                        return true;
                    }

                    c.addRes3(id);
                    break;
            }

            return true;
        }

        protected override bool CorrectResourceType()
        {
            return true;
        }
    }
}
