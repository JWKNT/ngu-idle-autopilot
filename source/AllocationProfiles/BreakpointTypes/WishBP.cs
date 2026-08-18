using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

WishBP allocates Energy, Magic, and Resource 3 to one permanent Wish while enforcing unlock,
difficulty, slot, and attainability guards. Every resource call computes total desired allocation,
subtracts the Wish's existing amount, and clamps to its joint minimum-time frontier. Duplicate
logical slots therefore cannot add the same desired total repeatedly. Impossible serialized
requirements fail closed instead of trapping resources forever. Ranking belongs to the planner.
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

            var c = Character.wishesController;
            var allocation = DesiredHeadroom(id);
            if (allocation <= 0 || !SetInput(allocation))
                return true;
            switch (Type)
            {
                case ResourceType.Energy:
                    c.addEnergy(id);
                    break;
                case ResourceType.Magic:
                    c.addMagic(id);
                    break;
                case ResourceType.R3:
                    c.addRes3(id);
                    break;
            }

            return true;
        }

        private long DesiredHeadroom(int id)
        {
            if (id < 0 || id >= Character.wishes.wishes.Count || MaxAllocation <= 0)
                return 0L;
            if (Character.curEnergy <= 0 || Character.magic.curMagic <= 0
                || Character.res3.curRes3 <= 0)
                return 0L;
            var wish = Character.wishes.wishes[id];
            long current;
            long total;
            long idle;
            double power;
            double otherA;
            double otherB;
            switch (Type)
            {
                case ResourceType.Energy:
                    current = wish.energy;
                    total = Character.curEnergy;
                    idle = Character.idleEnergy;
                    power = Character.totalEnergyPower();
                    otherA = Character.wishesController.magicFactor(id);
                    otherB = Character.wishesController.res3Factor(id);
                    break;
                case ResourceType.Magic:
                    current = wish.magic;
                    total = Character.magic.curMagic;
                    idle = Character.magic.idleMagic;
                    power = Character.totalMagicPower();
                    otherA = Character.wishesController.energyFactor(id);
                    otherB = Character.wishesController.res3Factor(id);
                    break;
                default:
                    current = wish.res3;
                    total = Character.res3.curRes3;
                    idle = Character.res3.idleRes3;
                    power = Character.totalRes3Power();
                    otherA = Character.wishesController.energyFactor(id);
                    otherB = Character.wishesController.magicFactor(id);
                    break;
            }
            if (idle <= 0 || total <= 0 || power <= 0.0)
                return 0L;

            var distinct = new HashSet<int>();
            var slots = Math.Max(0, Character.wishesController.curWishSlots());
            for (var slot = 0; slot < slots; slot++)
            {
                var selected = Main.WishManager.GetSlot(slot);
                if (selected >= 0) distinct.Add(selected);
            }
            var fairTotal = ExactResourceAllocator.CeilingShare(total, 1L,
                Math.Max(1, distinct.Count));
            var desiredTotal = fairTotal;

            // Native progressPerTick is min(minimumWishTime, rawProgress). Solve the exact
            // raw frontier for this factor while holding the other two live factors fixed.
            // If either other factor is zero, use the coherent fair target to bootstrap; the
            // later resource passes will make all three factors positive in the same sweep.
            var speed = Character.wishesController.totalWishSpeedBonuses();
            var divider = Character.wishesController.wishSpeedDivider(id);
            var minimum = Character.wishesController.minimumWishTime();
            if (otherA > 0.0 && otherB > 0.0 && speed > 0.0 && divider > 0.0
                && minimum > 0.0)
            {
                var powered = minimum * divider / (otherA * otherB * speed);
                var solved = Math.Pow(powered, 1.0 / 0.17) / power;
                if (!double.IsNaN(solved) && !double.IsInfinity(solved) && solved > 0.0)
                {
                    var frontier = solved >= long.MaxValue ? long.MaxValue
                        : Math.Max(1L, (long)Math.Ceiling(solved));
                    desiredTotal = Math.Min(fairTotal, frontier);
                }
            }
            var budget = Math.Min(MaxAllocation, idle);
            var headroom = ExactResourceAllocator.Headroom(desiredTotal, current, budget);
            Main.LogAllocation("Wish " + id + " " + Type + " desired total " + desiredTotal
                               + ", existing " + current + ", exact headroom " + headroom);
            return headroom;
        }

        protected override bool CorrectResourceType()
        {
            return true;
        }
    }
}
