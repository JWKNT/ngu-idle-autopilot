using System;
using System.Collections.Generic;
using System.Linq;

/*
FILE PURPOSE

AdventureCollectionPlanner converts fightable zones and Item List state into permanent MAXX debt.
It takes stronger forward gear first, then backfills older sets, known Bonus Accessories, and
discovered equipment. It also owns collection-aware inventory pressure and protection queries.
Drop-source tables are audited from LootDrop; unknown/misc IDs are filtered by native item type.
Never treat a set as disposable until its game completion flag is confirmed.
*/
namespace NGUInjector.Managers
{
    internal sealed class AdventureCollectionTarget
    {
        internal ZoneTarget Target;
        internal bool IsBackfill;
        internal bool BossOnly;
        internal int RemainingItems;
        internal int ProjectedNewSlots;
        internal int RequiredFreeReserve;
        internal int IncompleteZones;
        internal double UsefulBoostDebt;
        internal double UsefulBoostGain;
        internal string UsefulBoostTarget = string.Empty;
        internal string SetReward = "No unclaimed core-set reward";
        internal string Reason = "Collection planner is waiting for Adventure state";
        internal string MissingSummary = "unknown";
    }

    // Item List completion is permanent and survives rebirths.  The fastest route is
    // normally to snipe the newest usable gear first, then use the resulting Drop
    // Chance and one-hit kills to repay older collection debt.  This planner keeps
    // that debt explicit instead of assuming that the highest stat-safe zone is the
    // only useful Adventure target.
    internal static class AdventureCollectionPlanner
    {
        // Immutable drop-source data audited from LootDrop.zone*Drop in the shipped
        // Assembly-CSharp.  At runtime we additionally require ItemNameDesc.type to
        // be actual equipment, so misc/secret consumables in these tables can never
        // become collection targets by accident.
        private static readonly Dictionary<int, int[]> ZoneLootIds = new Dictionary<int, int[]>
        {
            {0, new[] {120, 75, 62, 65, 64, 63}},
            {1, new[] {40, 41, 42, 43, 44, 45, 46, 77, 278}},
            {2, new[] {135, 47, 48, 49, 50, 51, 52, 53, 432, 281}},
            {3, new[] {54, 55, 56, 57, 58, 59, 60, 61, 53, 433}},
            {4, new[] {66, 67, 172, 53, 434}},
            {5, new[] {68, 69, 70, 71, 72, 73, 74, 53, 66, 435, 283}},
            {7, new[] {85, 86, 87, 88, 89, 90, 91, 66, 436, 368}},
            {9, new[] {95, 96, 97, 98, 99, 100, 101, 437, 279}},
            {10, new[] {103, 104, 105, 106, 107, 108, 109, 110, 66, 438}},
            {12, new[] {122, 123, 124, 125, 126, 127, 66, 439, 282}},
            {13, new[] {130, 131, 132, 133, 134, 339, 76, 440, 287}},
            {15, new[] {143, 144, 145, 146, 147, 148, 76, 441, 367, 285}},
            {17, new[] {164, 165, 166, 167, 168, 67, 128, 94, 163, 442}},
            {18, new[] {173, 174, 175, 176, 177, 94, 163, 128, 178, 443}},
            {20, new[] {221, 222, 223, 224, 225, 226, 227, 142, 444, 369, 280}},
            {21, new[] {213, 214, 215, 216, 217, 218, 219, 220, 142, 445, 284}},
            {22, new[] {231, 232, 233, 234, 235, 236, 142, 446, 370, 286}},
            {24, new[] {251, 252, 253, 254, 255, 256, 257, 142, 128, 447}},
            {25, new[] {258, 259, 260, 261, 262, 263, 264, 142, 128, 448}},
            {27, new[] {301, 302, 303, 304, 305, 306, 307, 142, 128, 449}},
            {28, new[] {308, 309, 310, 311, 312, 313, 314, 142, 128, 450}},
            {29, new[] {315, 316, 317, 318, 319, 320, 321, 142, 128, 451, 371}},
            {31, new[] {345, 346, 347, 348, 349, 350, 351, 170, 169, 452}},
            {32, new[] {352, 353, 354, 355, 356, 357, 358, 229, 230}},
            {33, new[] {359, 360, 361, 362, 363, 364, 365, 366, 229, 230}},
            {35, new[] {392, 393, 394, 395, 396, 397, 398, 399, 229, 230}},
            {36, new[] {400, 401, 402, 403, 404, 405, 406, 407, 229, 230}},
            {37, new[] {408, 409, 410, 411, 412, 413, 414, 415, 229, 230}},
            {39, new[] {453, 454, 455, 456, 457, 458, 459, 460, 295, 296}},
            {40, new[] {496, 497, 498, 499, 500, 501, 502, 503, 295, 296}},
            {41, new[] {461, 462, 463, 464, 465, 466, 467, 468, 295, 296}}
        };

        // Normal Bonus Accessories do not belong to the local zone set.  They must
        // be seeded as known debt even before the first copy drops; merely looking at
        // itemDropped would otherwise make the bot leave the zone forever.
        private static readonly Dictionary<int, int> KnownBonusAccessory = new Dictionary<int, int>
        {
            {2, 432}, {3, 433}, {4, 434}, {5, 435}, {7, 436}, {9, 437},
            {10, 438}, {12, 439}, {13, 440}, {15, 441}, {17, 442}, {18, 443},
            {20, 444}, {21, 445}, {22, 446}, {24, 447}, {25, 448}, {27, 449},
            {28, 450}, {29, 451}, {31, 452}
        };

        internal static AdventureCollectionTarget Evaluate(Character c, ZoneTarget front)
        {
            var result = new AdventureCollectionTarget();
            if (c == null || front == null || ZoneStatHelper.UserOverrides == null
                || c.inventory == null || c.inventory.itemList == null || c.itemInfo == null)
                return result;

            var reachable = ZoneStatHelper.UserOverrides.Keys
                .Where(zone => zone <= ZoneHelpers.GetMaxReachableZone(false))
                .Where(zone => ZoneStatHelper.UserOverrides[zone]
                    .FightType(c.totalAdvAttack(), c.totalAdvDefense()) > 0)
                .OrderByDescending(zone => zone).ToList();
            if (reachable.Count == 0) return result;

            var debts = reachable.Select(zone => DebtFor(c, zone)).Where(x => x.HasDebt).ToList();
            result.IncompleteZones = debts.Count;

            // Forward gear remains authoritative while the newest fightable set is
            // incomplete.  Once it is finished (or the front has no set, like Sky),
            // repay the oldest missing set before optional/bonus-item debt.  This is
            // the guide's "snipe ahead, come back stronger" strategy rather than a
            // greedy insistence on finishing weak gear before taking a major upgrade.
            var frontDebt = debts.FirstOrDefault(x => x.Zone == front.Zone);
            ZoneDebt selected = null;
            if (frontDebt != null && frontDebt.CoreSetIncomplete)
                selected = frontDebt;
            if (selected == null)
                selected = debts.Where(x => x.CoreSetIncomplete).OrderBy(x => x.Zone).FirstOrDefault();
            if (selected == null && frontDebt != null)
                selected = frontDebt;
            if (selected == null)
                selected = debts.OrderByDescending(x => x.Zone).FirstOrDefault();

            if (selected == null)
            {
                result.Target = front;
                result.Reason = "Every known obtainable equipment entry in all fightable zones is MAXXED; using the best progression zone";
                result.MissingSummary = "collection complete through " + ZoneName(front.Zone);
                return result;
            }

            var stats = ZoneStatHelper.UserOverrides[selected.Zone];
            result.Target = new ZoneTarget
            {
                Zone = selected.Zone,
                FightType = stats.FightType(c.totalAdvAttack(), c.totalAdvDefense())
            };
            result.IsBackfill = selected.Zone < front.Zone;
            result.SetReward = selected.CoreSetIncomplete ? CoreSetReward(selected.Zone)
                : "Core-set reward already claimed";
            double usefulBoostDebt = 0.0;
            double usefulBoostGain = 0.0;
            string usefulBoostTarget = string.Empty;
            var needsNormalEnemyBoosts = selected.CoreSetIncomplete
                && ProgressionLoadoutOptimizer.TryGetUsefulBoostDebt(c, out usefulBoostDebt,
                    out usefulBoostGain, out usefulBoostTarget);
            result.UsefulBoostDebt = needsNormalEnemyBoosts ? usefulBoostDebt : 0.0;
            result.UsefulBoostGain = needsNormalEnemyBoosts ? usefulBoostGain : 0.0;
            result.UsefulBoostTarget = needsNormalEnemyBoosts ? usefulBoostTarget : string.Empty;

            // Bosses are the fast source of duplicate set pieces and early-zone EXP, while ordinary
            // enemies are the source of Power/Toughness boost drops. Pure boss sniping is therefore
            // valid only when it does not cut off the supply needed to make an owned, demonstrably
            // better item win the complete loadout. Full-clear still encounters bosses naturally.
            result.BossOnly = selected.CoreSetIncomplete && !needsNormalEnemyBoosts;
            result.RemainingItems = selected.RemainingItems;
            result.ProjectedNewSlots = selected.ProjectedNewSlots;
            result.RequiredFreeReserve = Math.Min(8, Math.Max(3, selected.ProjectedNewSlots + 2));
            result.MissingSummary = selected.MissingSummary;
            result.Reason = selected.CoreSetIncomplete
                ? needsNormalEnemyBoosts
                    ? "Full-clearing for ordinary-enemy boosts while bosses advance the MAXX set: "
                      + Math.Ceiling(usefulBoostDebt) + " boost points on " + usefulBoostTarget
                      + " have a proven complete-loadout gain; unclaimed set reward is " + result.SetReward
                    : (result.IsBackfill ? "Boss-sniping an older incomplete MAXX set; ordinary-enemy boosts have no proven loadout target; unclaimed set reward is " + result.SetReward
                        : "Boss-sniping the newest incomplete MAXX set; ordinary-enemy boosts have no proven loadout target; unclaimed set reward is " + result.SetReward)
                : (result.IsBackfill ? "Backtracking for permanent Item List MAXX collection debt"
                    : "Collecting non-set equipment and the zone bonus accessory before moving to optional farming");
            return result;
        }

        internal static int FreeInventorySlots(Character c)
        {
            return c == null || c.inventory == null || c.inventory.inventory == null
                ? 0 : c.inventory.inventory.Count(x => x == null || x.id == 0);
        }

        internal static int TotalInventorySlots(Character c)
        {
            return c == null || c.inventory == null || c.inventory.inventory == null
                ? 0 : c.inventory.inventory.Count;
        }

        internal static bool InventoryPressureHigh(Character c, AdventureCollectionTarget collection)
        {
            var total = TotalInventorySlots(c);
            var free = FreeInventorySlots(c);
            if (total <= 0) return false;
            // Remaining MAXX debt is not equivalent to required slots: another
            // copy normally merges into an already-owned physical item. Reserve
            // capacity only for missing physical IDs plus two drop/sweep buffers.
            var debtReserve = collection == null ? 3 : Math.Max(3, collection.RequiredFreeReserve);
            return free <= Math.Max(debtReserve, (int)Math.Ceiling(total * .10));
        }

        internal static bool InventoryPressureCritical(Character c)
        {
            return FreeInventorySlots(c) <= 2;
        }

        internal static string InventoryPressure(Character c, AdventureCollectionTarget collection)
        {
            var total = TotalInventorySlots(c);
            var free = FreeInventorySlots(c);
            if (total <= 0) return "unavailable";
            if (free <= 2) return "critical";
            return InventoryPressureHigh(c, collection) ? "high" : free <= Math.Ceiling(total * .20) ? "watch" : "healthy";
        }

        internal static bool HasFightableCollectionDebt(Character c)
        {
            if (c == null) return false;
            try
            {
                var front = ZoneStatHelper.GetBestZone();
                return front != null && Evaluate(c, front).IncompleteZones > 0;
            }
            catch
            {
                // Filtering is destructive at drop time.  If collection state cannot
                // be proven complete, the safe answer is to keep equipment enabled.
                return true;
            }
        }

        internal static bool IsProtectedCollectionItem(Character c, int id)
        {
            if (c == null || id <= 0) return true;
            if (!IsMaxxed(c, id)) return true;
            foreach (var pair in ZoneLootIds)
            {
                if (!pair.Value.Contains(id)) continue;
                if (HasCoreSet(pair.Key) && !CoreSetComplete(c, pair.Key))
                    return true;
            }
            return false;
        }

        internal static bool CoreSetComplete(Character c, int zone)
        {
            if (!HasCoreSet(zone)) return true;
            var list = c.inventory.itemList;
            switch (zone)
            {
                case 0: return list.trainingComplete;
                case 1: return list.sewersComplete;
                case 2: return list.forestComplete;
                case 3: return list.caveComplete;
                case 5: return list.HSBComplete;
                case 7: return list.clockComplete;
                case 9: return list.twoDComplete;
                case 10: return list.gaudyComplete;
                case 12: return list.ghostComplete;
                case 13: return list.megaComplete;
                case 15: return list.beardverseComplete;
                case 17: return list.badlyDrawnComplete;
                case 18: return list.stealthComplete;
                case 20: return list.chocoComplete;
                case 21: return list.edgyComplete;
                case 22: return list.prettyComplete;
                case 24: return list.metaComplete;
                case 25: return list.partyComplete;
                case 27: return list.typoComplete;
                case 28: return list.fadComplete;
                case 29: return list.jrpgComplete;
                case 31: return list.radComplete;
                case 32: return list.schoolComplete;
                case 33: return list.westernComplete;
                case 35: return list.breadverseComplete;
                case 36: return list.that70sComplete;
                case 37: return list.halloweeniesComplete;
                case 39: return list.constructionComplete;
                case 40: return list.duckComplete;
                case 41: return list.netherComplete;
                default: return true;
            }
        }

        private static bool HasCoreSet(int zone)
        {
            return zone != 4 && ZoneLootIds.ContainsKey(zone);
        }

        private static ZoneDebt DebtFor(Character c, int zone)
        {
            var debt = new ZoneDebt {Zone = zone, CoreSetIncomplete = !CoreSetComplete(c, zone)};
            var missing = new List<string>();
            var missingIds = new HashSet<int>();
            var physicallyOwned = PhysicalEquipmentIds(c);
            int bonus;
            if (KnownBonusAccessory.TryGetValue(zone, out bonus) && IsEquipment(c, bonus) && !IsMaxxed(c, bonus))
            {
                missing.Add(ItemName(c, bonus));
                missingIds.Add(bonus);
            }

            int[] ids;
            if (ZoneLootIds.TryGetValue(zone, out ids))
            {
                foreach (var id in ids.Distinct())
                {
                    if (!IsEquipment(c, id) || IsMaxxed(c, id)) continue;
                    // Core-set flags already represent undiscovered set pieces.  For
                    // other rares, create debt once the game proves the item can drop;
                    // known Bonus Accessories are the deliberate exception above.
                    if (!IsDropped(c, id) && (!KnownBonusAccessory.ContainsKey(zone)
                                               || KnownBonusAccessory[zone] != id))
                        continue;
                    var name = ItemName(c, id);
                    if (!missing.Contains(name)) missing.Add(name);
                    missingIds.Add(id);
                }
            }
            debt.RemainingItems = missing.Count + (debt.CoreSetIncomplete ? 1 : 0);
            debt.ProjectedNewSlots = missingIds.Count(id => !physicallyOwned.Contains(id))
                                     + (debt.CoreSetIncomplete ? 1 : 0);
            debt.HasDebt = debt.CoreSetIncomplete || missing.Count > 0;
            var preview = string.Join(", ", missing.Take(3).ToArray());
            if (missing.Count > 3) preview += " +" + (missing.Count - 3) + " more";
            debt.MissingSummary = debt.CoreSetIncomplete
                ? "incomplete " + ZoneName(zone) + " set" + (preview.Length > 0 ? "; " + preview : string.Empty)
                : preview.Length > 0 ? preview : "unresolved equipment entry";
            return debt;
        }

        private static HashSet<int> PhysicalEquipmentIds(Character c)
        {
            var result = new HashSet<int>();
            if (c == null || c.inventory == null) return result;
            Action<Equipment> add = item =>
            {
                if (item != null && item.id > 0) result.Add(item.id);
            };
            add(c.inventory.head);
            add(c.inventory.chest);
            add(c.inventory.legs);
            add(c.inventory.boots);
            add(c.inventory.weapon);
            add(c.inventory.weapon2);
            if (c.inventory.accs != null)
                foreach (var item in c.inventory.accs) add(item);
            if (c.inventory.inventory != null)
                foreach (var item in c.inventory.inventory) add(item);
            return result;
        }

        private static bool IsEquipment(Character c, int id)
        {
            return id > 0 && c.itemInfo.type != null && id < c.itemInfo.type.Length
                   && c.itemInfo.type[id] >= part.Head && c.itemInfo.type[id] <= part.Accessory;
        }

        private static bool IsMaxxed(Character c, int id)
        {
            return c.inventory.itemList.itemMaxxed != null && id < c.inventory.itemList.itemMaxxed.Count
                   && c.inventory.itemList.itemMaxxed[id];
        }

        private static bool IsDropped(Character c, int id)
        {
            return c.inventory.itemList.itemDropped != null && id < c.inventory.itemList.itemDropped.Count
                   && c.inventory.itemList.itemDropped[id];
        }

        private static string ItemName(Character c, int id)
        {
            return c.itemInfo.itemName != null && id < c.itemInfo.itemName.Length
                ? c.itemInfo.itemName[id] : "item " + id;
        }

        private static string ZoneName(int zone)
        {
            return ZoneStatHelper.UserOverrides != null && ZoneStatHelper.UserOverrides.ContainsKey(zone)
                ? ZoneStatHelper.UserOverrides[zone].Name : "zone " + zone;
        }

        /*
        NATIVE EARLY SET REWARDS

        A level-100 copy is not valued only as an equip candidate. Completing every required item in
        a zone invokes AllItemListController.checkforBonuses and grants a permanent set reward. These
        early values are mirrored from that shipped method and are surfaced in the decision model;
        unknown later sets remain collection debt but are not assigned a fabricated numeric reward.
        */
        private static string CoreSetReward(int zone)
        {
            switch (zone)
            {
                case 0: return "+2 Energy Speed and 10 EXP";
                case 1: return "+5 Adventure Power/Toughness, +15 HP, +0.2 regen, and 20 EXP";
                case 2: return "+5 Energy Power, 200 EXP, and six Energy consumables";
                case 3: return "+2 Magic Power, +40,000 Magic Cap, +2 Magic Per Bar, and 300 EXP";
                default: return "the zone's native permanent Item List set bonus";
            }
        }

        private sealed class ZoneDebt
        {
            internal int Zone;
            internal bool CoreSetIncomplete;
            internal bool HasDebt;
            internal int RemainingItems;
            internal int ProjectedNewSlots;
            internal string MissingSummary;
        }
    }
}
