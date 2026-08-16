using System;
using System.Collections.Generic;
using System.Linq;

/*
FILE PURPOSE

DaycareManager rotates persistent item-leveling slots, retrieves completed items, and fills open
slots with safe high-value candidates. It uses native Daycare state and must preserve unique,
puzzle, and loadout-critical physical items. General inventory trash/merge policy is separate.
*/
namespace NGUInjector.Managers
{
    internal static class DaycareManager
    {
        internal static void Manage()
        {
            var c = Main.Character;
            if (c == null || !c.purchases.hasDaycare || c.inventoryController == null)
                return;

            var inv = c.inventory;
            var slots = Math.Min(c.inventoryController.daycareSpaces(), inv.daycare.Count);
            if (slots <= 0)
                return;

            var occupied = new HashSet<int>(inv.daycare.Where(x => x != null && x.id > 0).Select(x => x.id));
            for (var slot = 0; slot < slots; slot++)
            {
                var current = inv.daycare[slot];
                var effectiveLevel = current == null ? 0 : current.level;
                if (current != null && current.id > 0
                    && c.inventoryController.daycares != null
                    && slot < c.inventoryController.daycares.Count
                    && c.inventoryController.daycares[slot] != null)
                {
                    effectiveLevel += c.inventoryController.daycares[slot].levelsAdded();
                }

                // Ordinary items are complete at 100. MacGuffins have no level cap and
                // remain assigned until a future marginal-value policy explicitly replaces them.
                if (current != null && current.id > 0 && ((int)current.type == 11 || effectiveLevel < 100))
                    continue;

                if (current != null && current.id > 0)
                    occupied.Remove(current.id);
                var candidate = BestCandidate(c, occupied);
                if (candidate < 0)
                    continue;

                var candidateId = inv.inventory[candidate].id;
                var previousId = current == null ? 0 : current.id;
                inv.item1 = 100000 + slot;
                inv.item2 = candidate;
                c.inventoryController.swapDaycare();
                var confirmed = slot < inv.daycare.Count && inv.daycare[slot].id == candidateId;
                Main.LogAction(confirmed ? "DAYCARE" : "REJECTED",
                    confirmed
                        ? "Daycare slot " + (slot + 1) + ": item " + previousId + " -> " + candidateId
                          + " [confirmed by daycare state]"
                        : "Daycare swap for item " + candidateId + " produced no state transition");
                if (confirmed)
                    occupied.Add(candidateId);
            }
        }

        private static int BestCandidate(Character c, ISet<int> occupied)
        {
            var allowMacGuffins = c.adventure.itopod.perkLevel.Count > 56
                                  && c.adventure.itopod.perkLevel[56] >= 1;
            var bestIndex = -1;
            var bestScore = double.MinValue;
            for (var i = 0; i < c.inventory.inventory.Count; i++)
            {
                var item = c.inventory.inventory[i];
                if (item == null || item.id <= 0 || !item.removable || occupied.Contains(item.id))
                    continue;
                var isMacGuffin = (int)item.type == 11;
                if (isMacGuffin || (item.id >= 335 && item.id <= 341))
                    continue;
                if (item.level >= 100 || item.id >= c.itemInfo.daycareRate.Length)
                    continue;

                var secondsPerLevel = Math.Max(1.0, c.itemInfo.daycareRate[item.id]);
                var completionSeconds = (100.0 - item.level) * secondsPerLevel;
                var gateWeight = IsHeart(item.id) ? 20.0 : 1.0;
                // Reward per daycare-second is the correct comparison.  Heart items
                // receive their native permanent-progression shadow price, while the
                // deterministic ID epsilon only breaks exact ties.
                var score = gateWeight / completionSeconds + item.id * 1e-15;
                if (score <= bestScore)
                    continue;
                bestScore = score;
                bestIndex = i;
            }
            if (bestIndex >= 0 || !allowMacGuffins)
                return bestIndex;

            // MacGuffins only use otherwise-idle daycare slots.  Balance the lowest
            // level first because all equipped bonuses are multiplicative and exhibit
            // diminishing marginal value.
            long lowestLevel = long.MaxValue;
            for (var i = 0; i < c.inventory.inventory.Count; i++)
            {
                var item = c.inventory.inventory[i];
                if (item == null || item.id <= 0 || !item.removable || occupied.Contains(item.id)
                    || (int)item.type != 11 || item.level >= lowestLevel)
                    continue;
                lowestLevel = item.level;
                bestIndex = i;
            }
            return bestIndex;
        }

        private static bool IsHeart(int id)
        {
            switch (id)
            {
                case 119:
                case 129:
                case 162:
                case 171:
                case 196:
                case 212:
                case 293:
                case 297:
                case 344:
                case 390:
                    return true;
                default:
                    return false;
            }
        }
    }
}
