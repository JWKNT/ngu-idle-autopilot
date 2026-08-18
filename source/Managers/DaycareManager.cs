using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

Purpose: DaycareManager materializes latent native Daycare levels, retrieves completed objects, and
fills open slots with safe high-value candidates without losing physical identity or loot capacity.

Mechanism: It reads `levelsAdded()` from each native Daycare timer, selects an exact ordinary
Equipment reference, and invokes `swapDaycare()` with snapshotted `Inventory.item1/item2` selector
registers. A completed item is always retrieved: it swaps directly with the next candidate, or with
an empty slot proven inside PhysicalTopology's native [totalInvMergeSlots(), curSpaces()) interval.

Inputs and outputs: Inputs are live Inventory, InventoryController/DaycareController state, Item
List rates, exact saved/optimizer/configured loadout references, and perk unlocks. Outputs are exact
Daycare/ordinary swaps, Item List bonus recognition, and confirmed action/hold telemetry.

Invariants and safety: A successful swap preserves the exact current and candidate references in
opposite locations, materializes a completed non-MacGuffin at level 100, retargets every native
saved-loadout reference, and restores ambient selectors in `finally`. Reserved-prefix and trailing
empties are never retrieval capacity. Puzzle, quest, transform, unlock (including 294/343/391/506),
END, configured, native-referenced, and optimizer-authoritative items never enter Daycare.

Extension points and non-goals: Candidate value can later consume the global terminal-seconds
oracle. General inventory merge/filter/trash policy, source probabilities, and cross-system schedule
authority remain outside this file.
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
                var retrievalOnly = false;
                if (candidate < 0 && current != null && current.id > 0)
                {
                    var topology = InventoryManager.CaptureOrdinaryTopology(c);
                    if (topology != null)
                    {
                        var capacity = LootCapacity.ProveOrdinary(topology,
                            LootCapacityRequirement.ExactBatch("completed-daycare-retrieval", 1, 0));
                        var usable = capacity.UsableFreeSlotIndices();
                        if (capacity.Admitted && usable.Length > 0)
                        {
                            candidate = usable[0];
                            retrievalOnly = true;
                        }
                    }
                    if (candidate < 0)
                    {
                        Main.LogAction("REJECTED", "Completed Daycare slot " + (slot + 1)
                            + " is held because no empty native loot-usable ordinary slot exists");
                        continue;
                    }
                }
                if (candidate < 0) continue;

                var candidateItem = inv.inventory[candidate];
                var candidateId = candidateItem == null ? 0 : candidateItem.id;
                var previousId = current == null ? 0 : current.id;
                var previousItem1 = inv.item1;
                var previousItem2 = inv.item2;
                var nativeReferencesBefore = CountNativeLoadoutReferences(c, 100000 + slot);
                Exception failure = null;
                try
                {
                    inv.item1 = 100000 + slot;
                    inv.item2 = candidate;
                    c.inventoryController.swapDaycare();
                }
                catch (Exception error)
                {
                    failure = error;
                }
                finally
                {
                    inv.item1 = previousItem1;
                    inv.item2 = previousItem2;
                }
                var exactCandidateArrived = slot < inv.daycare.Count
                    && ReferenceEquals(inv.daycare[slot], candidateItem);
                var exactPreviousArrived = candidate < inv.inventory.Count
                    && ReferenceEquals(inv.inventory[candidate], current);
                var levelMaterialized = current == null || current.id <= 0 || (int)current.type == 11
                                        || current.level >= 100;
                var referencesRetargeted = nativeReferencesBefore == 0
                                           || CountNativeLoadoutReferences(c, candidate)
                                           >= nativeReferencesBefore;
                var confirmed = failure == null && exactCandidateArrived && exactPreviousArrived
                                && levelMaterialized && referencesRetargeted;
                Main.LogAction(confirmed ? "DAYCARE" : "REJECTED",
                    confirmed
                        ? retrievalOnly
                            ? "Retrieved completed " + GameNames.Item(c, previousId)
                              + " from Daycare slot " + (slot + 1)
                              + " into proven loot-usable ordinary slot " + candidate
                              + " [confirmed by exact references, materialized level, and loadout retarget]"
                            : "Daycare slot " + (slot + 1) + ": " + GameNames.Item(c, previousId)
                              + " -> " + GameNames.Item(c, candidateId)
                              + " [confirmed by exact references and selector restoration]"
                        : "Daycare swap for " + GameNames.Item(c, candidateId)
                          + " failed exact topology verification"
                          + (failure == null ? string.Empty
                              : "; " + failure.GetType().Name + ": " + failure.Message));
                if (confirmed)
                {
                    c.allItemList.checkforBonuses();
                    if (candidateId > 0) occupied.Add(candidateId);
                }
            }
        }

        private static int CountNativeLoadoutReferences(Character c, int slot)
        {
            if (c == null || c.inventory == null || c.inventory.loadouts == null) return 0;
            var count = 0;
            foreach (var loadout in c.inventory.loadouts.Where(x => x != null))
            {
                if (loadout.head == slot) count++;
                if (loadout.chest == slot) count++;
                if (loadout.legs == slot) count++;
                if (loadout.boots == slot) count++;
                if (loadout.weapon == slot) count++;
                if (loadout.weapon2 == slot) count++;
                if (loadout.accessories != null)
                    count += loadout.accessories.Count(x => x == slot);
            }
            return count;
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
                   || id == 141 || id == 154 || id == 163 || id == 172 || id == 195
                   || id == 294 || id == 343 || id == 391 || id == 506;
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
