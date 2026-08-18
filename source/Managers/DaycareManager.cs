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

                // Ordinary items stay until MAXX. MacGuffins have no cap, so bank at
                // least one native daycare level and then rotate toward the lowest-
                // level available guff (or an unfinished permanent item). This keeps
                // otherwise-idle slots balanced instead of pinning the first guff forever.
                if (current != null && current.id > 0)
                {
                    var isGuff = (int)current.type == 11;
                    var added = c.inventoryController.daycares != null
                                && slot < c.inventoryController.daycares.Count
                                && c.inventoryController.daycares[slot] != null
                        ? c.inventoryController.daycares[slot].levelsAdded() : 0;
                    if (!isGuff && effectiveLevel < 100 || isGuff && added < 1)
                        continue;
                }

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
                        ? "Daycare slot " + (slot + 1) + ": " + GameNames.Item(c, previousId)
                          + " -> " + GameNames.Item(c, candidateId)
                          + " [confirmed by daycare state]"
                        : "Daycare swap for " + GameNames.Item(c, candidateId)
                          + " produced no state transition");
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
                // Daycare removes a physical object from the pool that native and
                // optimizer loadouts can equip. Preserve both the exact object in
                // the active authoritative plan and every explicit user loadout.
                if (ProgressionLoadoutOptimizer.IsAuthoritativeItem(item)
                    || InventoryManager.IsNativeLoadoutReference(c, i)
                    || IsConfiguredLoadoutItem(item.id))
                    continue;
                var isMacGuffin = (int)item.type == 11;
                if (isMacGuffin || IsStateMachineItem(item.id))
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

        private static bool IsStateMachineItem(int id)
        {
            // Quest offerings, Exile clues/puzzle pieces, and one-use mechanic unlock keys must
            // remain in ordinary inventory where their native consumers can find them. Daycare is
            // not a safe storage location for an item whose presence drives a game state machine.
            return id >= 278 && id <= 287
                   || id >= 335 && id <= 341
                   || id >= 367 && id <= 372
                   || id == 66 || id == 92 || id == 102 || id == 120
                   || id == 141 || id == 154 || id == 163 || id == 172 || id == 195;
        }

        private static bool IsConfiguredLoadoutItem(int id)
        {
            return Main.Settings != null
                   && (Main.Settings.TitanLoadout.Contains(id)
                       || Main.Settings.YggdrasilLoadout.Contains(id)
                       || Main.Settings.GoldDropLoadout.Contains(id)
                       || Main.Settings.MoneyPitLoadout.Contains(id)
                       || Main.Settings.QuickLoadout.Contains(id));
        }
    }
}
