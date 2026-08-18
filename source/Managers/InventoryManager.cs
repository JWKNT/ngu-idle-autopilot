using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NGUInjector.Autopilot;
using static NGUInjector.Main;

/*
FILE PURPOSE

InventoryManager owns merge, boost, conversion, MacGuffin, loot-filter, and conservative trash
operations. These are potentially irreversible: unMAXXED equipment, incomplete sets, puzzle keys,
quest drops, and END pieces are always protected. Trash requires confirmed Item List MAXX plus an
all-use dominance proof; equality is sufficient only after proving that every simultaneous current
and saved/configured loadout has enough same-ID physical copies. Its boost queue also develops a
proven future slot upgrade before feeding the Infinity Cube. Physical gear selection belongs to
ProgressionLoadoutOptimizer; this file maintains contents and capacity through native controller
calls and verifies every irreversible slot transition.
*/
namespace NGUInjector.Managers
{

    public class FixedSizedQueue
    {
        private Queue<decimal> queue = new Queue<decimal>();

        public int Size { get; private set; }

        public FixedSizedQueue(int size)
        {
            Size = size;
        }

        public void Enqueue(decimal obj)
        {
            queue.Enqueue(obj);

            while (queue.Count > Size)
            {
                queue.Dequeue();
            }
        }

        public void Reset()
        {
            queue.Clear();
        }

        public decimal Avg()
        {
            try
            {
                return queue.Average(x => x);
            }
            catch (Exception e)
            {
                Log(e.Message);
                return 0;
            }
        }
    }

    public class Cube
    {
        internal float Power { get; set; }
        internal float Toughness { get; set; }
        protected bool Equals(Cube other)
        {
            return Power.Equals(other.Power) && Toughness.Equals(other.Toughness);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((Cube) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Power.GetHashCode() * 397) ^ Toughness.GetHashCode();
            }
        }
    }

    internal class InventoryManager
    {
        private readonly Character _character;
        private readonly InventoryController _controller;

        private readonly int[] _pendants = { 53, 76, 94, 142, 170, 229, 295, 388, 430, 504 };
        private readonly int[] _lootys = { 67, 128, 169, 230, 296, 389, 431, 505 };
        private readonly int[] _convertibles;
        private readonly int[] _wandoos = {66, 163};
        private readonly int[] _guffs = {198, 200, 199, 201, 202, 203, 204, 205, 206, 207, 208, 209, 210, 228, 211, 250, 291, 289, 290, 298, 299, 300};
        // Exile clues (335-341) are state-machine keys, not ordinary equipment.
        // Merging or filtering even one of these can permanently stall the puzzle.
        private readonly int[] _mergeBlacklist = { 335, 336, 337, 338, 339, 340, 341, 367, 368, 369, 370, 371, 372 };
        private BoostsNeeded _previousBoostsNeeded = null;
        private Cube _lastCube = null;
        private readonly FixedSizedQueue _invBoostAvg = new FixedSizedQueue(60);
        private readonly FixedSizedQueue _cubeBoostAvg = new FixedSizedQueue(60);
        private Equipment _developmentTarget;
        private bool _endSequenceStarted;
        internal static string LastTrashDecision { get; private set; }
            = "Waiting for the first conservative trash audit";
        internal static string LastFilterDecision { get; private set; }
            = "Waiting for the first collection-safe loot-filter reconciliation";
        internal static string LastBoostDecision { get; private set; }
            = "Waiting for the first equipment-development audit";


        //Wandoos 98, Giant Seed, Wandoos XL, Lonely Flubber, Wanderer's Cane, Guffs, Lemmi
        private readonly int[] _filterExcludes = { 66, 92, 163, 120, 154, 195, 278, 279, 280, 281, 282, 283, 284, 285, 286, 287,
            335, 336, 337, 338, 339, 340, 341 };
        public InventoryManager()
        {
            _character = Main.Character;
            _controller = Controller;
            var temp = _pendants.Concat(_lootys).ToList();
            //Wanderer's Cane
            temp.Add(154);
            //Lonely Flubber
            temp.Add(120);
            //A Giant Seed
            temp.Add(92);
            _convertibles = temp.ToArray();
        }

        internal void Reset()
        {
            _invBoostAvg.Reset();
            _cubeBoostAvg.Reset();
        }

        internal ih[] GetBoostSlots(ih[] ci)
        {
            return GetBoostSlots(ci, true);
        }

        internal ih[] GetImmediateBoostSlots(ih[] ci)
        {
            return GetBoostSlots(ci, false);
        }

        private ih[] GetBoostSlots(ih[] ci, bool includeSpeculativeLockedItems)
        {
            var result = new List<ih>();
            // Explicit user priorities remain an intentional override of the
            // optimizer. Full automation leaves this list empty by default.
            foreach (var id in Settings.PriorityBoosts)
            {
                if (Settings.BoostBlacklist.Contains(id))
                    continue;
                
                var f = FindItemSlot(ci, id);
                if (f != null)
                    result.Add(f);
            }

            /*
            OBJECTIVE-ORDERED BOOST ROUTING

            Equipped-first is not a value model: an obsolete equipped pendant could absorb every compatible
            boost before a Cave armor piece which actually opens the next zone. Rank equipped and unequipped
            objects together by complete-loadout score gained per remaining point of the boost categories
            physically available right now. FullyBoostedLoadoutGain uses real current merge level, native
            boss scaling, next-zone thresholds, and saturation-aware production rates. This also prevents a
            Power-only drop from being blamed for skipping an armor piece which only accepts Toughness.
            */
            var optimized = GetProgressionBoostSlots(ci).ToArray();
            result.AddRange(optimized);

            // Locked, unequipped gear can be useful later but is speculative. The
            // caller gives active/explicit gear first claim, then brings the always-
            // on Cube to its full-value softcap, then returns here for this tier.
            if (includeSpeculativeLockedItems)
            {
                var invItems = ci.Where(x => x.locked && x.equipment.isEquipment()
                    && !Settings.BoostBlacklist.Contains(x.id) && !Settings.PriorityBoosts.Contains(x.id));
                result = result.Concat(invItems).ToList();
            }

            //Make sure we filter out non-equips again, just in case one snuck into priorityboosts
            return result.Where(x => x.equipment.isEquipment())
                .Where(x => x.equipment.GetNeededBoosts().Total() > 0)
                .GroupBy(x => x.equipment).Select(x => x.First()).ToArray();
        }

        private sealed class BoostRoute
        {
            internal ih Item;
            internal BoostsNeeded Needed;
            internal double Gain;
            internal double RelevantNeed;
            internal double Score;
        }

        private IEnumerable<ih> GetProgressionBoostSlots(IEnumerable<ih> convertedInventory)
        {
            var c = _character;
            if (c == null || c.inventory == null || c.inventory.itemList == null)
                return Enumerable.Empty<ih>();

            var inventory = convertedInventory.ToArray();
            var powerAvailable = inventory.Any(x => x != null && x.equipment != null
                && !x.locked && x.equipment.type == part.atkBoost);
            var toughnessAvailable = inventory.Any(x => x != null && x.equipment != null
                && !x.locked && x.equipment.type == part.defBoost);
            var specialAvailable = inventory.Any(x => x != null && x.equipment != null
                && !x.locked && x.equipment.type == part.specBoost);
            var anyBoost = powerAvailable || toughnessAvailable || specialAvailable;

            var candidates = c.inventory.GetConvertedEquips().Concat(inventory)
                .Where(x => x != null && x.equipment != null
                && x.id > 0 && x.equipment.isEquipment() && !Settings.BoostBlacklist.Contains(x.id)
                && !Settings.PriorityBoosts.Contains(x.id))
                .GroupBy(x => x.equipment).Select(x => x.First())
                .Select(x =>
                {
                    var needed = x.equipment.GetNeededBoosts();
                    var relevant = anyBoost
                        ? (powerAvailable ? needed.Power : 0m)
                          + (toughnessAvailable ? needed.Toughness : 0m)
                          + (specialAvailable ? needed.Special : 0m)
                        : needed.Total();
                    var gain = ProgressionLoadoutOptimizer.AvailableBoostedLoadoutGain(c, x.equipment,
                        powerAvailable, toughnessAvailable, specialAvailable);
                    return new BoostRoute
                    {
                        Item = x,
                        Needed = needed,
                        Gain = gain,
                        RelevantNeed = (double)relevant,
                        Score = relevant > 0 ? gain / (double)relevant : 0.0
                    };
                })
                .Where(x => x.Needed.Total() > 0 && x.Gain > 1e-7
                            && (!anyBoost || x.RelevantNeed > 0.0))
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Item.equipment.bossRequired)
                .ThenBy(x => x.Item.id).ToArray();
            if (candidates.Length == 0)
            {
                _developmentTarget = null;
                LastBoostDecision = anyBoost
                    ? "No item with a proven complete-loadout gain accepts the currently available boost categories; preserving them for the Infinity Cube/conversion policy"
                    : "No owned item has a proven current-level boost path to improve the complete loadout";
                return Enumerable.Empty<ih>();
            }

            // Hysteresis prevents tiny floating-point changes from fragmenting two
            // nearly equal humps, but never preserves a target more than 5% below
            // the newly best compatible objective gain per point.
            var best = candidates[0];
            var retained = candidates.FirstOrDefault(x => ReferenceEquals(x.Item.equipment, _developmentTarget));
            var first = retained != null && retained.Score >= best.Score * 0.95 ? retained : best;
            _developmentTarget = first.Item.equipment;
            var kinds = string.Join("/", new[]
            {
                powerAvailable ? "Power" : string.Empty,
                toughnessAvailable ? "Toughness" : string.Empty,
                specialAvailable ? "Special" : string.Empty
            }.Where(x => x.Length > 0).ToArray());
            LastBoostDecision = "Routing " + (kinds.Length == 0 ? "the next compatible boost" : kinds)
                                + " to " + SanitizeName(first.Item.name) + ": "
                                + first.RelevantNeed.ToString("0.##") + " relevant points complete a proven "
                                + ProgressionLoadoutOptimizer.LastObjective + " loadout gain";
            return new[] {first}.Concat(candidates.Where(x => !ReferenceEquals(x.Item.equipment, first.Item.equipment)))
                .Select(x => x.Item);
        }

        internal void BoostInventory(ih[] boostSlots)
        {
            foreach (var item in boostSlots)
            {
                var removableBefore = _character.inventory.inventory.Count(x => x.id > 0 && x.id < 40 && x.removable);
                if (removableBefore == 0)
                    break;
                var equipment = item.equipment;
                var attackBefore = equipment.curAttack;
                var defenseBefore = equipment.curDefense;
                var specialBefore = equipment.spec1Cur + equipment.spec2Cur + equipment.spec3Cur;
                _controller.applyAllBoosts(item.slot);
                var removableAfter = _character.inventory.inventory.Count(x => x.id > 0 && x.id < 40 && x.removable);
                var confirmed = removableAfter < removableBefore || equipment.curAttack > attackBefore
                                || equipment.curDefense > defenseBefore
                                || equipment.spec1Cur + equipment.spec2Cur + equipment.spec3Cur > specialBefore;
                // applyAllBoosts is a selector probe: incompatible boost types are
                // expected to leave an item unchanged while a later candidate accepts
                // them. Only a confirmed mutation is an action worth surfacing.
                if (confirmed)
                {
                    LastBoostDecision = "Applied available compatible boosts to " + SanitizeName(item.name)
                                        + "; loadout value will be re-evaluated after the confirmed stat delta";
                    Main.LogAction("INVENTORY", "Applied boosts to " + SanitizeName(item.name)
                                                + " [confirmed by item/boost delta]");
                }
            }
        }

        private static ih FindItemSlot(IEnumerable<ih> ci, int id)
        {
            var inv = Main.Character.inventory;
            if (inv.head.id == id)
            {
                return inv.head.GetInventoryHelper(-1);
            }

            if (inv.chest.id == id)
            {
                return inv.chest.GetInventoryHelper(-2);
            }

            if (inv.legs.id == id)
            {
                return inv.legs.GetInventoryHelper(-3);
            }

            if (inv.boots.id == id)
            {
                return inv.boots.GetInventoryHelper(-4);
            }

            if (inv.weapon.id == id)
            {
                return inv.weapon.GetInventoryHelper(-5);
            }

            if (Controller.weapon2Unlocked())
            {
                if (inv.weapon2.id == id)
                {
                    return inv.weapon2.GetInventoryHelper(-6);
                }
            }

            for (var i = 0; i < inv.accs.Count; i++)
            {
                if (inv.accs[i].id == id)
                {
                    return inv.accs[i].GetInventoryHelper(i + 10000);
                }
            }

            var items = ci.Where(x => x.id == id).ToArray();
            if (items.Length != 0) return items.MaxItem();

            return null;
        }

        private int ChangePage(int slot)
        {
            var page = (int)Math.Floor((double)slot / 60);
            _controller.changePage(page);
            return slot - (page * 60);
        }

        internal void BoostInfinityCube()
        {
            var removableBefore = _character.inventory.inventory.Count(x => x.id > 0 && x.id < 40 && x.removable);
            if (removableBefore == 0)
                return;
            var powerBefore = _character.inventory.cubePower;
            var toughnessBefore = _character.inventory.cubeToughness;
            _controller.infinityCubeAll();
            _controller.updateInventory();
            var removableAfter = _character.inventory.inventory.Count(x => x.id > 0 && x.id < 40 && x.removable);
            var confirmed = removableAfter < removableBefore || _character.inventory.cubePower > powerBefore
                            || _character.inventory.cubeToughness > toughnessBefore;
            Main.LogAction(confirmed ? "INVENTORY" : "REJECTED",
                confirmed
                    ? "Routed surplus boosts to Infinity Cube [confirmed by cube/boost delta]"
                    : "Infinity Cube boost request produced no state transition");
        }

        // Raw Cube Power/Toughness are permanent and contribute in every Adventure
        // loadout. Below the native softcaps each point has full value. Active and
        // explicitly-prioritized gear gets first claim; the Cube then outranks only
        // speculative unequipped gear that can become obsolete.
        internal void BoostInfinityCubeToSoftcaps()
        {
            var inv = _character.inventory;
            if (inv == null || inv.inventory == null)
                return;
            var powerBefore = inv.cubePower;
            var toughnessBefore = inv.cubeToughness;
            for (var slot = 0; slot < inv.inventory.Count; slot++)
            {
                var boost = inv.inventory[slot];
                if (boost == null || !boost.removable || !boost.isBoost())
                    continue;
                var needPower = inv.cubePower < _controller.cubePowerSoftcap();
                var needToughness = inv.cubeToughness < _controller.cubeToughnessSoftcap();
                if (!needPower && !needToughness)
                    break;
                var compatible = boost.type == part.atkBoost && needPower
                                 || boost.type == part.defBoost && needToughness
                                 // Native Special-to-Cube splits the value equally; use it here
                                 // only while both halves remain below their full-value softcaps.
                                 || boost.type == part.specBoost && needPower && needToughness;
                if (!compatible)
                    continue;
                _controller.infinityCubeBoost(slot);
            }
            _controller.updateInventory();
            if (inv.cubePower > powerBefore || inv.cubeToughness > toughnessBefore)
                Main.LogAction("INVENTORY", "Prioritized permanent Infinity Cube softcap value: Power "
                                            + powerBefore.ToString("0.##") + " -> " + inv.cubePower.ToString("0.##")
                                            + ", Toughness " + toughnessBefore.ToString("0.##") + " -> "
                                            + inv.cubeToughness.ToString("0.##"));
        }

        internal void MergeEquipped(ih[] ci)
        {
            if (ci.Any(x => x.id == _character.inventory.head.id))
            {
                _controller.mergeAll(-1);
            }

            if (ci.Any(x => x.id == _character.inventory.chest.id))
            {
                _controller.mergeAll(-2);
            }

            if (ci.Any(x => x.id == _character.inventory.legs.id))
            {
                _controller.mergeAll(-3);
            }

            if (ci.Any(x => x.id == _character.inventory.boots.id))
            {
                _controller.mergeAll(-4);
            }

            if (ci.Any(x => x.id == _character.inventory.weapon.id))
            {
                _controller.mergeAll(-5);
            }

            if (_controller.weapon2Unlocked())
            {
                if (ci.Any(x => x.id == _character.inventory.weapon2.id))
                {
                    _controller.mergeAll(-6);
                }
            }

            //Boost Accessories
            for (var i = 10000; _controller.accessoryID(i) < _controller.accessorySpaces(); i++)
            {
                if (ci.Any(x => x.id == _character.inventory.accs[_controller.accessoryID(i)].id))
                {
                    _controller.mergeAll(i);
                }
            }
        }

        internal void MergeBoosts(ih[] ci)
        {
            var grouped = ci.Where(x => x.id <= 39 && !_character.inventory.itemList.itemMaxxed[x.id])
                .GroupBy(x => x.id)
                .Where(x => x.Count() > 1);

            foreach (var group in grouped)
            {
                var target = group.OrderByDescending(x => x.locked).ThenByDescending(x => x.level).First();
                Log($"Merging {target.name} in slot {target.slot}");
                _controller.mergeAll(target.slot);
            }
        }

        private string SanitizeName(string name)
        {
            if (name.Contains("\n"))
            {
                name = name.Split(new[] {'\n'}).Last();
            }

            return name;
        }

        /*
        QUEST-ITEM OWNERSHIP TRANSACTION

        Native consumeItem deletes every quest offering, including a wrong ID, an off-quest item, or an
        excess item after the target is already met. Keep one exact-reference development copy of each
        unMAXXED ID without changing the user's lock state, merge duplicates into it, and offer only another
        removable copy whose ID is the live questID. Re-read both quest progress and inventory after every
        native call because one high-level item can satisfy multiple drops. A missing reflection target or
        absent state delta is a rejection, never presumed success.
        */
        internal void ManageQuestItems(ih[] ci)
        {
            var curPage = (int)Math.Floor((double)_controller.inventory[0].id / 60);
            var list = _character.inventory.itemList;
            var developmentCopies = new Dictionary<int, Equipment>();

            foreach (var id in ci.Where(x => x.id >= 278 && x.id <= 287)
                         .Select(x => x.id).Distinct().OrderBy(x => x))
            {
                if (list.itemMaxxed != null && id < list.itemMaxxed.Count && list.itemMaxxed[id])
                    continue;
                var live = _character.inventory.inventory
                    .Select((item, slot) => new {item, slot})
                    .Where(x => x.item != null && x.item.id == id)
                    .OrderByDescending(x => !x.item.removable)
                    .ThenByDescending(x => x.item.level).ToArray();
                if (live.Length == 0) continue;
                var development = live[0];
                developmentCopies[id] = development.item;
            }

            if (!_character.beastQuest.inQuest || _character.beastQuest.targetDrops <= 0
                || _character.beastQuest.curDrops >= _character.beastQuest.targetDrops)
            {
                MergeQuestDevelopmentCopies(developmentCopies);
                _controller.changePage(curPage);
                return;
            }

            var questId = _character.beastQuest.questID;
            if (questId < 278 || questId > 287)
            {
                Main.LogAction("REJECTED", "Active Beast Quest exposed unexpected item ID " + questId
                                           + "; preserving every quest item");
                MergeQuestDevelopmentCopies(developmentCopies);
                _controller.changePage(curPage);
                return;
            }
            var consume = typeof(ItemController).GetMethod("consumeItem",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (consume == null)
            {
                Main.LogAction("REJECTED", "Native quest-item consumer was unavailable; preserving every quest item");
                MergeQuestDevelopmentCopies(developmentCopies);
                _controller.changePage(curPage);
                return;
            }

            while (_character.beastQuest.inQuest && _character.beastQuest.questID == questId
                   && _character.beastQuest.curDrops < _character.beastQuest.targetDrops)
            {
                Equipment protectedCopy;
                developmentCopies.TryGetValue(questId, out protectedCopy);
                var slot = _character.inventory.inventory.FindIndex(x => x != null && x.id == questId
                    && x.removable && !ReferenceEquals(x, protectedCopy));
                if (slot < 0) break;
                var dropsBefore = _character.beastQuest.curDrops;
                var countBefore = _character.inventory.inventory.Count(x => x != null && x.id == questId);
                var newSlot = ChangePage(slot);
                consume.Invoke(_controller.inventory[newSlot], null);
                var countAfter = _character.inventory.inventory.Count(x => x != null && x.id == questId);
                var progressConfirmed = _character.beastQuest.curDrops > dropsBefore;
                var itemLostWithoutProgress = countAfter < countBefore && !progressConfirmed;
                Main.LogAction(progressConfirmed ? "QUEST" : "REJECTED", progressConfirmed
                    ? "Offered one exact " + SafeItemName(questId) + " to the active quest; progress "
                      + dropsBefore + " -> " + _character.beastQuest.curDrops + " [confirmed by quest/item delta]"
                    : itemLostWithoutProgress
                        ? "Native consumer deleted an exact quest item without advancing quest progress; stopping all offerings"
                        : "Exact quest-item offering produced no verified quest-progress transition");
                if (!progressConfirmed) break;
            }
            // Only after the exact active demand is satisfied/exhausted may
            // leftovers be merged into the protected permanent-development copy.
            MergeQuestDevelopmentCopies(developmentCopies);
            _controller.changePage(curPage);
        }

        private void MergeQuestDevelopmentCopies(IDictionary<int, Equipment> developmentCopies)
        {
            foreach (var pair in developmentCopies.OrderBy(x => x.Key))
            {
                var id = pair.Key;
                var live = _character.inventory.inventory.Select((item, slot) => new {item, slot})
                    .Where(x => x.item != null && x.item.id == id).ToArray();
                if (live.Length <= 1) continue;
                var development = live.FirstOrDefault(x => ReferenceEquals(x.item, pair.Value));
                if (development == null)
                {
                    Main.LogAction("REJECTED", "The exact protected development copy disappeared for "
                                               + SafeItemName(id) + "; preserving duplicate items unmerged");
                    continue;
                }
                var levelBefore = development.item.level;
                var countBefore = live.Length;
                _controller.mergeAll(development.slot);
                var countAfter = _character.inventory.inventory.Count(x => x != null && x.id == id);
                var merged = development.item.level > levelBefore || countAfter < countBefore;
                Main.LogAction(merged ? "QUEST" : "REJECTED", merged
                    ? "Merged excess/off-quest " + SafeItemName(id)
                      + " copies into the exact protected development item [confirmed by level/count delta]"
                    : "Quest-item merge for " + SafeItemName(id) + " produced no verified state transition");
            }
        }

        internal void MergeInventory(ih[] ci)
        {
            var grouped =
                ci.Where(x => x.id >= 40 && x.level < 100 && !_mergeBlacklist.Contains(x.id)
                              && !_guffs.Contains(x.id) && !EndgameDependencyModel.IsEndItem(x.id)
                              && (x.id < 278 || x.id > 287)).GroupBy(x => x.id).Where(x => x.Count() > 1);

            foreach (var item in grouped)
            {
                if (item.All(x => x.locked))
                    continue;

                var target = item.MaxItem();

                Log($"Merging {SanitizeName(target.name)} in slot {target.slot}");
                _controller.mergeAll(target.slot);
            }
        }

        internal void MergeGuffs(ih[] ci)
        {
            // A dropped MacGuffin in ordinary inventory contributes no bonus and Blood
            // spells cannot level it. Optimize the equipped subset before Blood-spell
            // leveling; a first-come slot can otherwise compound the wrong permanent stat.
            OptimizeEquippedGuffs();
            // Swaps exchange exact physical objects with ordinary inventory. Rebuild
            // slot helpers before merging; the caller snapshot now names pre-swap IDs.
            ci = _character.inventory.GetConvertedInventory().ToArray();
            var equippedIds = new HashSet<int>(_character.inventory.macguffins
                .Where(x => x != null && x.id > 0).Select(x => x.id));
            for (var guffSlot = 0; guffSlot < _character.inventory.macguffins.Count; guffSlot++)
            {
                if (_character.inventory.macguffins[guffSlot].id > 0) continue;
                var candidate = ci.FirstOrDefault(x => _guffs.Contains(x.id)
                                                       && !equippedIds.Contains(x.id)
                                                       && x.slot >= 0
                                                       && x.slot < _character.inventory.inventory.Count);
                if (candidate == null) break;
                _character.inventory.item1 = _controller.globalMacguffinID(guffSlot);
                _character.inventory.item2 = candidate.slot;
                var expected = candidate.id;
                _controller.swapMacguffin();
                var confirmed = _character.inventory.macguffins[guffSlot].id == expected;
                Main.LogAction(confirmed ? "MACGUFFIN" : "REJECTED", confirmed
                    ? "Equipped " + GameNames.Item(_character, expected) + " into empty MacGuffin slot " + (guffSlot + 1)
                      + " [confirmed by slot state]"
                    : "MacGuffin equip produced no verified slot transition");
                if (confirmed) equippedIds.Add(expected);
            }

            for (var id = 0; id < _character.inventory.macguffins.Count; ++id)
            {
                var guffId = _character.inventory.macguffins[id].id;
                if (ci.Any(x => x.id == guffId))
                    _controller.mergeAll(_controller.globalMacguffinID(id));
            }

            var invGuffs = ci.Where(x => _guffs.Contains(x.id)).GroupBy(x => x.id).Where(x => x.Count() > 1);
            foreach (var guff in invGuffs)
            {
                if (guff.All(x => x.locked))
                    continue;
                var target = guff.MaxItem();
                _controller.mergeAll(target.slot);
            }
        }

        private void OptimizeEquippedGuffs()
        {
            var slots = _character.inventory.macguffins.Count;
            if (slots <= 0) return;
            var candidates = _character.inventory.macguffins.Concat(_character.inventory.inventory)
                .Where(x => x != null && _guffs.Contains(x.id))
                .GroupBy(x => x.id)
                .Select(g => g.OrderByDescending(x => GuffUtility(x)).ThenByDescending(x => x.level).First())
                .OrderByDescending(GuffUtility).ThenByDescending(x => x.level).Take(slots).ToList();
            if (candidates.Count == 0) return;
            var desiredIds = new HashSet<int>(candidates.Select(x => x.id));

            for (var slot = 0; slot < slots; slot++)
            {
                var current = _character.inventory.macguffins[slot];
                if (current != null && desiredIds.Contains(current.id)) continue;
                var equippedIds = new HashSet<int>(_character.inventory.macguffins
                    .Where(x => x != null && x.id > 0).Select(x => x.id));
                var desired = candidates.FirstOrDefault(x => !equippedIds.Contains(x.id));
                if (desired == null) break;
                var inventorySlot = _character.inventory.inventory.FindIndex(x => ReferenceEquals(x, desired));
                if (inventorySlot < 0) continue;
                var previousId = current == null ? 0 : current.id;
                _character.inventory.item1 = _controller.globalMacguffinID(slot);
                _character.inventory.item2 = inventorySlot;
                _controller.swapMacguffin();
                var confirmed = _character.inventory.macguffins[slot].id == desired.id;
                Main.LogAction(confirmed ? "MACGUFFIN" : "REJECTED", confirmed
                    ? "Equipped objective-optimal " + GameNames.Item(_character, desired.id)
                      + " over " + (previousId > 0 ? GameNames.Item(_character, previousId) : "an empty slot")
                      + " [confirmed by MacGuffin slot delta]"
                    : "Objective MacGuffin swap produced no verified slot transition");
                if (!confirmed) break;
            }
        }

        private double GuffUtility(Equipment item)
        {
            if (item == null) return 0.0;
            var weight = GuffObjectiveWeight(item.id);
            if (weight <= 0.0) return 0.0;
            var gain = GuffPerRebirthGain(item);
            return weight * Math.Log(1.0 + Math.Max(0.0, gain));
        }

        private double GuffObjectiveWeight(int id)
        {
            var c = _character;
            var challenge = c.challenges.inChallenge;
            switch (id)
            {
                case 198: return 8.0; // Energy Power
                case 199: return 6.0; // Energy Cap
                case 200: return 5.0; // Magic Power
                case 201: return 4.0; // Magic Cap
                case 202: return c.settings.nguOn ? 7.0 : 1.0;
                case 203: return c.settings.nguOn ? 5.0 : 1.0;
                case 204: return 4.0; // Energy Bars
                case 205: return 3.0; // Magic Bars
                case 206: return c.settings.beardsOn ? 3.0 : 0.5;
                case 207: return c.settings.beardsOn ? 2.5 : 0.5;
                case 208: return c.adventure.zone == 1000 ? 0.5 : 5.0;
                case 209: return 2.0;
                case 210: return challenge && c.challenges.noAugsChallenge.inChallenge ? 0.0 : 4.0;
                case 228: return challenge ? 10.0 : 6.0; // Fight Boss Power
                case 211:
                case 250: return challenge ? 8.0 : 2.0; // Wandoos E/M
                case 289: return challenge ? 9.0 : 3.0; // Number
                case 290: return c.buttons.bloodMagic.interactable ? 3.0 : 0.0;
                case 291: return 12.0; // Adventure
                case 298: return c.res3.res3On ? 7.0 : 0.0;
                case 299: return c.res3.res3On ? 6.0 : 0.0;
                case 300: return c.res3.res3On ? 5.0 : 0.0;
                default: return 0.0;
            }
        }

        private double GuffPerRebirthGain(Equipment item)
        {
            switch (item.id)
            {
                case 198: return _controller.energyPowerBonusPerRebirth(item);
                case 199: return _controller.energyCapBonusPerRebirth(item);
                case 200: return _controller.magicPowerBonusPerRebirth(item);
                case 201: return _controller.magicCapBonusPerRebirth(item);
                case 202: return _controller.energyNGUBonusPerRebirth(item);
                case 203: return _controller.magicNGUBonusPerRebirth(item);
                case 204: return _controller.energyBarBonusPerRebirth(item);
                case 205: return _controller.magicBarBonusPerRebirth(item);
                case 206: return _controller.energyBeardBonusPerRebirth(item);
                case 207: return _controller.magicBeardBonusPerRebirth(item);
                case 208: return _controller.dropChanceBonusPerRebirth(item);
                case 209: return _controller.goldBonusPerRebirth(item);
                case 210: return _controller.augSpeedBonusPerRebirth(item);
                case 228: return _controller.powerBonusPerRebirth(item);
                case 211: return _controller.energyWandoosBonusPerRebirth(item);
                case 250: return _controller.magicWandoosBonusPerRebirth(item);
                case 289: return _controller.numberBonusPerRebirth(item);
                case 290: return _controller.bloodBonusPerRebirth(item);
                case 291: return _controller.adventureBonusPerRebirth(item);
                case 298: return _controller.res3PowerBonusPerRebirth(item);
                case 299: return _controller.res3CapBonusPerRebirth(item);
                case 300: return _controller.res3BarBonusPerRebirth(item);
                default: return 0.0;
            }
        }

        internal void ManageConvertibles(ih[] ci)
        {
            if (TryAssembleExile())
                return;
            var curPage = (int)Math.Floor((double)_controller.inventory[0].id / 60);

            // One-use progression keys should not sit inert in inventory.
            var progression = ci.FirstOrDefault(x => x.slot >= 0
                && ((x.id == 102 && !_character.settings.nguOn)
                    || (x.id == 141 && !_character.settings.beardsOn)
                    || (x.id == 172 && !_character.settings.itopodOn)
                    || (x.id == 92 && !_character.settings.yggdrasilOn)
                    || (x.id == 506 && !_character.adventure.move69Unlocked)));
            if (progression != null && _character.inventory.inventory[progression.slot].removable)
            {
                var itemDebited = ConsumeInventorySlot(progression.slot);
                var featureEnabled = progression.id == 102 ? _character.settings.nguOn
                    : progression.id == 141 ? _character.settings.beardsOn
                    : progression.id == 172 ? _character.settings.itopodOn
                    : progression.id == 92 ? _character.settings.yggdrasilOn
                    : progression.id == 506 && _character.adventure.move69Unlocked;
                var confirmed = itemDebited && featureEnabled;
                Main.LogAction(confirmed ? "PROGRESSION" : "REJECTED", confirmed
                    ? "Consumed " + GameNames.Item(_character, progression.id)
                      + " [confirmed by item debit and exact feature toggle]"
                    : GameNames.Item(_character, progression.id)
                      + " consume lacked a verified item debit plus feature transition");
                _controller.changePage(curPage);
                return;
            }

            var wandoos = ci.FirstOrDefault(x => x.slot >= 0 && x.id == 66
                && _character.inventory.inventory[x.slot].removable
                && (!_character.settings.wandoos98On
                    || _character.wandoos98.installed && _character.wandoos98.installTime.totalseconds >= 86400
                    && x.level >= _character.wandoos98.OSlevel + 1));
            if (wandoos == null)
                wandoos = ci.FirstOrDefault(x => x.slot >= 0 && x.id == 163
                    && _character.inventory.inventory[x.slot].removable
                    && _character.settings.wandoos98On
                    && (_character.wandoos98.XLLevels == 0 || x.level >= _character.wandoos98.XLLevels + 1));
            if (wandoos != null)
            {
                var osBefore = _character.wandoos98.OSlevel;
                var xlBefore = _character.wandoos98.XLLevels;
                var wasEnabled = _character.settings.wandoos98On;
                var itemDebited = ConsumeInventorySlot(wandoos.slot);
                var confirmed = itemDebited && (wandoos.id == 66
                    ? wasEnabled ? _character.wandoos98.OSlevel > osBefore
                        : _character.settings.wandoos98On
                    : _character.wandoos98.XLLevels > xlBefore);
                Main.LogAction(confirmed ? "PROGRESSION" : "REJECTED", confirmed
                    ? "Consumed " + GameNames.Item(_character, wandoos.id) + " [confirmed by OS state]"
                    : GameNames.Item(_character, wandoos.id) + " produced no OS transition");
                _controller.changePage(curPage);
                return;
            }

            var grouped = ci.Where(x => _convertibles.Contains(x.id));
            foreach (var item in grouped)
            {
                if (item.level != 100) continue;
                var temp = _character.inventory.inventory[item.slot];
                if (!temp.removable) continue;
                var newSlot = ChangePage(item.slot);
                var ic = _controller.inventory[newSlot];
                var countBefore = _character.inventory.inventory.Count(x => x != null && x.id == item.id);
                var method = typeof(ItemController).GetMethod("consumeItem",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (method == null)
                {
                    Main.LogAction("REJECTED", "Native convertible consumer was unavailable for "
                                               + GameNames.Item(_character, item.id));
                    break;
                }
                method.Invoke(ic, null);
                var consumed = _character.inventory.inventory.Count(x => x != null && x.id == item.id) < countBefore;
                if (!consumed)
                {
                    Main.LogAction("REJECTED", "Convertible " + GameNames.Item(_character, item.id)
                                               + " produced no verified inventory debit");
                    break;
                }
            }
            _controller.changePage(curPage);
        }

        private bool ConsumeInventorySlot(int slot)
        {
            if (slot < 0 || slot >= _character.inventory.inventory.Count) return false;
            var target = _character.inventory.inventory[slot];
            if (target == null || target.id <= 0) return false;
            var id = target.id;
            var levelBefore = target.level;
            var countBefore = _character.inventory.inventory.Count(x => x != null && x.id == id);
            var newSlot = ChangePage(slot);
            var method = typeof(ItemController).GetMethod("consumeItem",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null) return false;
            method.Invoke(_controller.inventory[newSlot], null);
            return _character.inventory.inventory.Count(x => x != null && x.id == id) < countBefore
                   || !_character.inventory.inventory.Any(x => ReferenceEquals(x, target))
                   || target.level < levelBefore;
        }

        internal static bool ExileAssemblyReady(Character c)
        {
            if (c == null || c.inventory == null || c.inventory.inventory.Count < 24)
                return false;
            var required = new[] {340, 336, 338, 339, 337};
            if (required.Any(id => !c.inventory.inventory.Any(x => x != null && x.id == id)))
                return false;
            if (!c.adventure.titan9Unlocked) return true;
            return !c.adventure.titan9SpecialReward
                   && c.inventory.inventory.Any(x => x != null && x.id == 341);
        }

        /*
        TERMINAL PLACEMENT TRANSACTION

        The native END trigger accepts only sixteen exact IDs in sixteen sparse ordinary-inventory
        slots.  Arrange them with native inventory swaps, record every pair, and reverse all pairs
        if any postcondition fails.  This routine is called only behind the dedicated irreversible
        execution lease; missing branches are an expected HOLD, never a rejected mutation.
        */
        internal bool TryExecuteEndSequence()
        {
            if (_endSequenceStarted) return false;
            var inv = _character == null ? null : _character.inventory;
            if (inv == null || inv.inventory == null || inv.inventory.Count < 40
                || _controller == null || _controller.midDrag
                || LoadoutManager.CurrentLock != LockType.None)
            {
                ExecutionSafety.ReportHold("end-placement-state",
                    "END sequence held until ordinary inventory is stable, unlocked, and has at least 40 slots");
                return false;
            }

            var requirements = MechanicsEndgame.AllRequirements();
            var missing = requirements.Where(requirement =>
                !inv.inventory.Any(item => item != null && item.id == requirement.ItemId))
                .Select(requirement => requirement.ItemId).ToArray();
            if (missing.Length > 0)
            {
                ExecutionSafety.ReportHold("end-placement-missing:" + string.Join(",", missing),
                    "END sequence held; missing ordinary-inventory pieces "
                    + string.Join(", ", missing.Select(x => x.ToString()).ToArray()), 60);
                return false;
            }

            var before = inv.inventory.ToArray();
            var swaps = new List<int[]>();
            foreach (var requirement in requirements.OrderBy(x => x.TargetSlot))
            {
                var current = inv.inventory.FindIndex(item =>
                    item != null && item.id == requirement.ItemId);
                if (current < 0)
                    return RollBackEndPlacement(inv, before, swaps,
                        "END item disappeared while the placement transaction was running");
                if (current == requirement.TargetSlot) continue;
                inv.item1 = requirement.TargetSlot;
                inv.item2 = current;
                _controller.swapItems();
                swaps.Add(new[] {requirement.TargetSlot, current});
                if (inv.inventory[requirement.TargetSlot] == null
                    || inv.inventory[requirement.TargetSlot].id != requirement.ItemId)
                    return RollBackEndPlacement(inv, before, swaps,
                        "native inventory swap did not establish the required END placement");
            }

            var ids = inv.inventory.Select(item => item == null ? 0 : item.id).ToArray();
            if (!MechanicsEndgame.ValidatePlacement(ids))
                return RollBackEndPlacement(inv, before, swaps,
                    "END placement failed canonical validation after native swaps");

            var consume = typeof(ItemController).GetMethod("consumeItem",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (consume == null || _controller.inventory == null || _controller.inventory.Length <= 39)
                return RollBackEndPlacement(inv, before, swaps,
                    "native END trigger method or slot controller is unavailable");

            var oldPage = (int)Math.Floor((double)_controller.inventory[0].id / 60);
            try
            {
                _controller.changePage(0);
                consume.Invoke(_controller.inventory[MechanicsEndgame.FinalTriggerSlot], null);
                _endSequenceStarted = true;
            }
            finally
            {
                _controller.changePage(oldPage);
            }
            Main.LogAction("END", "Started the native END sequence after exact placement of items "
                                  + MechanicsEndgame.FirstEndItemId + "-"
                                  + MechanicsEndgame.LastEndItemId
                                  + " [canonical placement and native trigger preflight confirmed]");
            return true;
        }

        private bool RollBackEndPlacement(Inventory inv, Equipment[] before,
            IList<int[]> swaps, string reason)
        {
            for (var i = swaps.Count - 1; i >= 0; i--)
            {
                inv.item1 = swaps[i][0];
                inv.item2 = swaps[i][1];
                _controller.swapItems();
            }
            var restored = before.Length == inv.inventory.Count;
            for (var i = 0; restored && i < before.Length; i++)
                restored = ReferenceEquals(before[i], inv.inventory[i]);
            Main.LogAction("REJECTED", reason + (restored
                ? "; original inventory topology restored exactly"
                : "; exact rollback could not be verified"));
            return false;
        }

        private bool TryAssembleExile()
        {
            if (!ExileAssemblyReady(_character) || _character.adventure.zone != 1)
                return false;
            var special = _character.adventure.titan9Unlocked;
            var layout = special
                ? new Dictionary<int, int> {{0, 340}, {1, 336}, {2, 338}, {12, 339}, {13, 341}, {14, 337}}
                : new Dictionary<int, int> {{0, 340}, {1, 336}, {2, 338}, {12, 339}, {14, 337}};
            foreach (var pair in layout)
            {
                var current = _character.inventory.inventory.FindIndex(x => x != null && x.id == pair.Value);
                if (current < 0) return false;
                if (current == pair.Key) continue;
                _character.inventory.item1 = pair.Key;
                _character.inventory.item2 = current;
                _controller.swapItems();
                if (_character.inventory.inventory[pair.Key].id != pair.Value)
                {
                    Main.LogAction("REJECTED", GameNames.Item(_character, pair.Value)
                                               + " could not be moved to Exile assembly slot " + pair.Key);
                    return true;
                }
            }

            var oldPage = (int)Math.Floor((double)_controller.inventory[0].id / 60);
            _controller.changePage(0);
            var beforeUnlock = _character.adventure.titan9Unlocked;
            var beforeSpecial = _character.adventure.titan9SpecialReward;
            typeof(ItemController).GetMethod("consumeItem", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(_controller.inventory[0], null);
            _controller.changePage(oldPage);
            var confirmed = special
                ? !beforeSpecial && _character.adventure.titan9SpecialReward
                : !beforeUnlock && _character.adventure.titan9Unlocked;
            Main.LogAction(confirmed ? "PROGRESSION" : "REJECTED", confirmed
                ? (special ? "Completed the Exile special assembly" : "Completed the Exile unlock assembly")
                  + " [confirmed by Adventure puzzle state]"
                : "Exile assembly produced no verified puzzle-state transition");
            return true;
        }

        internal void ShowBoostProgress(ih[] boostSlots)
        {
            var needed = new BoostsNeeded();
            var cube = new Cube
            {
                Power = _character.inventory.cubePower,
                Toughness = _character.inventory.cubeToughness
            };

            foreach (var item in boostSlots)
            {
                needed.Add(item.equipment.GetNeededBoosts());
            }

            var current = needed.Power + needed.Toughness + needed.Special;

            if (current > 0)
            {
                if (_previousBoostsNeeded == null)
                {
                    Log($"Boosts Needed to Green: {needed.Power} Power, {needed.Toughness} Toughness, {needed.Special} Special");
                    _previousBoostsNeeded = needed;
                }
                else
                {
                    var old = _previousBoostsNeeded.Power + _previousBoostsNeeded.Toughness +
                              _previousBoostsNeeded.Special;

                    var diff = current - old;

                    if (diff == 0) return;

                    //If diff is > 0, then we either added another item to boost or we levelled something. Don't add the diff to average
                    if (diff <= 0)
                    {
                        _invBoostAvg.Enqueue(diff * -1);
                    }

                    Log($"Boosts Needed to Green: {needed.Power} Power, {needed.Toughness} Toughness, {needed.Special} Special");
                    var average = _invBoostAvg.Avg();
                    if (average > 0)
                    {
                        var eta = current / average;
                        Log($"Last Minute: {diff}. Average Per Minute: {average:0}. ETA: {eta:0} minutes.");
                    }
                    else
                    {
                        Log($"Last Minute: {diff}.");
                    }

                    _previousBoostsNeeded = needed;
                }
            }

            if (_lastCube == null)
            {
                _lastCube = cube;
            }
            else
            {
                if (!_lastCube.Equals(cube))
                {
                    var output = $"Cube Progress:";
                    var toughnessDiff = cube.Toughness - _lastCube.Toughness;
                    var powerDiff = cube.Power - _lastCube.Power;

                    output = toughnessDiff > 0 ? $"{output} {toughnessDiff} Toughness." : output;
                    output = powerDiff > 0 ? $"{output} {powerDiff} Power." : output;

                    _cubeBoostAvg.Enqueue((decimal)(toughnessDiff + powerDiff));
                    output = $"{output} Average Per Minute: {_cubeBoostAvg.Avg():0}";
                    Log(output);
                    Log($"Cube Power: {cube.Power} ({_character.inventoryController.cubePowerSoftcap()} softcap). Cube Toughness: {cube.Toughness} ({_character.inventoryController.cubeToughnessSoftcap()} softcap)");
                }

                _lastCube = cube;
            }
        }

        internal void ManageBoostConversion()
        {
            if (_character.challenges.levelChallenge10k.curCompletions <
                _character.challenges.levelChallenge10k.maxCompletions)
                return;

            if (!Settings.AutoConvertBoosts)
                return;

            var converted = _character.inventory.GetConvertedInventory();
            //If we have a boost locked, we want to stay on that until its maxxed
            var lockedBoosts = converted.Where(x => x.id < 40 && x.locked).ToArray();
            if (lockedBoosts.Any())
            {
                // Only one auto-transform mode can be active.  Continue the first
                // unfinished locked category deterministically instead of overwriting
                // the mode once for every locked boost in the array.
                foreach (var locked in lockedBoosts.OrderBy(x => x.slot))
                {
                    //Unlock level 100 boosts
                    if (locked.level == 100)
                    {
                        _character.inventory.inventory[locked.slot].removable = true;
                        continue;
                    }

                    if (locked.id <= 13)
                    {
                        _controller.selectAutoPowerTransform();
                        return;
                    }else if (locked.id <= 26)
                    {
                        _controller.selectAutoToughTransform();
                        return;
                    }else if (locked.id <= 39)
                    {
                        _controller.selectAutoSpecialTransform();
                        return;
                    }
                }

                return;
            }

            // Transformation is a mutually exclusive production choice. Compare
            // proven complete-loadout score per remaining point for each category;
            // list order (Power before Toughness before Special) is not an economic
            // model and can starve the actual progression bottleneck indefinitely.
            var categoryScores = new double[3];
            // Conversion chooses the category for future drops, so it must not
            // depend on boost objects that the preceding routing/Cube passes have
            // already consumed. Score every owned physical equipment object under
            // each hypothetical one-category stream instead.
            var equipment = _character.inventory.GetConvertedEquips()
                .Concat(_character.inventory.GetConvertedInventory())
                .Where(x => x != null && x.equipment != null && x.id > 0
                            && x.equipment.isEquipment()
                            && !Settings.BoostBlacklist.Contains(x.id))
                .Select(x => x.equipment).Distinct().ToArray();
            foreach (var item in equipment)
            {
                var needed = item.GetNeededBoosts();
                if (needed.Power > 0)
                    categoryScores[0] = Math.Max(categoryScores[0],
                        ProgressionLoadoutOptimizer.AvailableBoostedLoadoutGain(
                            _character, item, true, false, false) / (double)needed.Power);
                if (needed.Toughness > 0)
                    categoryScores[1] = Math.Max(categoryScores[1],
                        ProgressionLoadoutOptimizer.AvailableBoostedLoadoutGain(
                            _character, item, false, true, false) / (double)needed.Toughness);
                if (needed.Special > 0)
                    categoryScores[2] = Math.Max(categoryScores[2],
                        ProgressionLoadoutOptimizer.AvailableBoostedLoadoutGain(
                            _character, item, false, false, true) / (double)needed.Special);
            }
            var bestCategory = Enumerable.Range(0, categoryScores.Length)
                .OrderByDescending(x => categoryScores[x]).ThenBy(x => x).First();
            if (categoryScores[bestCategory] > 1e-12)
            {
                if (bestCategory == 0) _controller.selectAutoPowerTransform();
                else if (bestCategory == 1) _controller.selectAutoToughTransform();
                else _controller.selectAutoSpecialTransform();
                return;
            }

            var cube = new Cube
            {
                Power = _character.inventory.cubePower,
                Toughness = _character.inventory.cubeToughness
            };

            if (Settings.CubePriority > 0)
            {
                if (Settings.CubePriority == 1)
                {
                    if (cube.Power > cube.Toughness)
                    {
                        _controller.selectAutoToughTransform();
                    }
                    else if (cube.Toughness > cube.Power)
                    {
                        _controller.selectAutoPowerTransform();
                    }
                    else
                    {
                        _controller.selectAutoPowerTransform();
                    }
                }else if (Settings.CubePriority == 2)
                {
                    _controller.selectAutoPowerTransform();
                }
                else
                {
                    _controller.selectAutoToughTransform();
                }
                
                return;
            }

            _controller.selectAutoNoneTransform();
        }

        // Conservative inventory reclamation. Same-ID dominated copies are exact
        // redundancy. A different-ID item is eligible only in a fixed armor slot,
        // after its Item List entry is MAXXED, when it has no special effects and a
        // retained same-slot item dominates both current stats and all future caps.
        internal void TrashProvenRedundantItem()
        {
            var inv = _character.inventory;
            if (inv == null || inv.inventory == null || _controller.midDrag
                || inv.itemList == null || inv.itemList.itemMaxxed == null)
            {
                LastTrashDecision = _controller.midDrag
                    ? "Paused while an inventory drag is active"
                    : "Inventory or Item List state is not ready";
                return;
            }

            // NGU Idle intentionally keeps the most recently discarded item in this
            // rolling recovery slot. A new native trash action overwrites it; a
            // non-empty slot is normal state, not an ownership lock.

            var owned = new List<Equipment>
            {
                inv.head, inv.chest, inv.legs, inv.boots, inv.weapon, inv.weapon2
            };
            if (inv.accs != null) owned.AddRange(inv.accs);
            if (inv.inventory != null) owned.AddRange(inv.inventory);
            if (inv.daycare != null) owned.AddRange(inv.daycare);

            for (var slot = 0; slot < inv.inventory.Count; slot++)
            {
                var candidate = inv.inventory[slot];
                if (!CanProveTrashSafe(candidate, slot)) continue;
                var keeper = owned.FirstOrDefault(x => x != null
                    && !ReferenceEquals(x, candidate) && x.id == candidate.id
                    && DominatesForAllUses(x, candidate));
                var sameIdProof = keeper != null;
                if (sameIdProof
                    && OwnedCopyCount(candidate.id, owned) <= RequiredPhysicalCopyCount(candidate.id))
                {
                    keeper = null;
                    sameIdProof = false;
                }
                var equalitySafeProof = false;
                if (keeper == null && AllOwnedCopiesMaxxed(candidate.id, owned)
                    && OwnedCopyCount(candidate.id, owned) > RequiredPhysicalCopyCount(candidate.id))
                {
                    keeper = owned.FirstOrDefault(x => x != null
                        && !ReferenceEquals(x, candidate) && x.id == candidate.id
                        && EquivalentForAllUses(x, candidate));
                    sameIdProof = keeper != null;
                    equalitySafeProof = keeper != null;
                }
                if (keeper == null && !IsConfiguredLoadoutItem(candidate.id))
                    keeper = owned.FirstOrDefault(x => x != null
                        && !ReferenceEquals(x, candidate)
                        && DominatesFixedSlotForAllFuture(x, candidate));
                if (keeper == null) continue;

                var id = candidate.id;
                var level = candidate.level;
                var attack = candidate.curAttack;
                var defense = candidate.curDefense;
                _controller.trash.trashItem(slot);
                _controller.updateTrash();
                _controller.updateInventory();
                var confirmed = inv.inventory[slot].id == 0
                                && ReferenceEquals(inv.trash, candidate);
                LastTrashDecision = confirmed
                    ? sameIdProof
                        ? equalitySafeProof
                            ? "Trashed one equal same-ID MAXXED surplus after simultaneous physical-copy demand was proven"
                            : "Trashed one proven-redundant same-ID dominated MAXXED duplicate; rolling recovery slot overwritten by design"
                        : "Trashed one obsolete fixed-slot MAXXED item dominated now and at every future boost level; rolling recovery slot overwritten by design"
                    : "Native trash request did not produce a verified recoverable-slot transition";
                Main.LogAction(confirmed ? "TRASH" : "REJECTED", confirmed
                    ? "Trashed " + (sameIdProof ? "redundant" : "provably obsolete fixed-slot")
                      + " item " + SafeItemName(id) + " (ID " + id + ", level " + level
                      + "): Item List is MAXXED and retained " + (sameIdProof ? "same-ID" : "same-slot")
                      + (equalitySafeProof ? " copy is equivalent and all simultaneous uses retain enough copies; ATK " : " copy dominates ATK ")
                      + attack.ToString("0.##") + "/DEF " + defense.ToString("0.##")
                      + " plus every relevant current/future cap and special field [confirmed; native recovery slot intentionally overwritten]"
                    : "Redundant-item trash request for ID " + id + " produced no verified slot transition");
                return; // preserve a full second of native one-step recovery
            }
            LastTrashDecision = "Nothing is provably disposable: preserving non-MAXXED Item List progress, specials, future multi-slot gear, and any item without an all-future dominance proof";
        }

        private bool CanProveTrashSafe(Equipment item, int slot)
        {
            if (item == null || item.id < 40 || !item.removable || item.id <= 0
                || !item.isEquipment())
                return false;
            var id = item.id;
            if (id >= _character.inventory.itemList.itemMaxxed.Count
                || !_character.inventory.itemList.itemMaxxed[id])
                return false;
            // A set-completion flag is the authoritative collection checkpoint. Even
            // if one piece's individual Item List entry is already MAXXED, retain all
            // drops from that source while another piece keeps the set incomplete.
            // This prevents a stale/partial set from repeatedly farming and deleting
            // its own merge material.
            if (AdventureCollectionPlanner.IsProtectedCollectionItem(_character, id))
                return false;
            if (_pendants.Contains(id) || _lootys.Contains(id) || _wandoos.Contains(id)
                || _filterExcludes.Contains(id) || _guffs.Contains(id)
                || _mergeBlacklist.Contains(id) || _convertibles.Contains(id)
                || EndgameDependencyModel.IsEndItem(id)
                || id >= 278 && id <= 287 || id == 102 || id == 141 || id == 172)
                return false;
            if (IsNativeLoadoutReference(_character, slot)) return false;
            return true;
        }

        internal static bool IsNativeLoadoutReference(Character character, int slot)
        {
            if (character == null || character.inventory == null) return false;
            var loadouts = character.inventory.loadouts;
            if (loadouts == null) return false;
            foreach (var loadout in loadouts)
            {
                if (loadout == null) continue;
                if (loadout.head == slot || loadout.chest == slot || loadout.legs == slot
                    || loadout.boots == slot || loadout.weapon == slot || loadout.weapon2 == slot
                    || loadout.accessories != null && loadout.accessories.Contains(slot))
                    return true;
            }
            return false;
        }

        private static bool IsConfiguredLoadoutItem(int id)
        {
            return Settings.TitanLoadout.Contains(id)
                   || Settings.YggdrasilLoadout.Contains(id)
                   || Settings.GoldDropLoadout.Contains(id)
                   || Settings.MoneyPitLoadout.Contains(id)
                   || Settings.QuickLoadout.Contains(id);
        }

        private static bool DominatesForAllUses(Equipment keeper, Equipment candidate)
        {
            const double epsilon = 1e-6;
            var noWorse = keeper.level >= candidate.level
                          && keeper.curAttack + epsilon >= candidate.curAttack
                          && keeper.curDefense + epsilon >= candidate.curDefense
                          && keeper.capAttack + epsilon >= candidate.capAttack
                          && keeper.capDefense + epsilon >= candidate.capDefense
                          && keeper.spec1Cur + epsilon >= candidate.spec1Cur
                          && keeper.spec2Cur + epsilon >= candidate.spec2Cur
                          && keeper.spec3Cur + epsilon >= candidate.spec3Cur
                          && keeper.spec1Cap + epsilon >= candidate.spec1Cap
                          && keeper.spec2Cap + epsilon >= candidate.spec2Cap
                          && keeper.spec3Cap + epsilon >= candidate.spec3Cap;
            if (!noWorse) return false;
            return keeper.level > candidate.level
                   || keeper.curAttack > candidate.curAttack + epsilon
                   || keeper.curDefense > candidate.curDefense + epsilon
                   || keeper.capAttack > candidate.capAttack + epsilon
                   || keeper.capDefense > candidate.capDefense + epsilon
                   || keeper.spec1Cur > candidate.spec1Cur + epsilon
                   || keeper.spec2Cur > candidate.spec2Cur + epsilon
                   || keeper.spec3Cur > candidate.spec3Cur + epsilon
                   || keeper.spec1Cap > candidate.spec1Cap + epsilon
                   || keeper.spec2Cap > candidate.spec2Cap + epsilon
                   || keeper.spec3Cap > candidate.spec3Cap + epsilon;
        }

        private static bool EquivalentForAllUses(Equipment keeper, Equipment candidate)
        {
            if (keeper == null || candidate == null || keeper.id != candidate.id) return false;
            const double epsilon = 1e-6;
            return keeper.level == candidate.level
                   && Math.Abs(keeper.curAttack - candidate.curAttack) <= epsilon
                   && Math.Abs(keeper.curDefense - candidate.curDefense) <= epsilon
                   && Math.Abs(keeper.capAttack - candidate.capAttack) <= epsilon
                   && Math.Abs(keeper.capDefense - candidate.capDefense) <= epsilon
                   && keeper.spec1Type == candidate.spec1Type
                   && keeper.spec2Type == candidate.spec2Type
                   && keeper.spec3Type == candidate.spec3Type
                   && Math.Abs(keeper.spec1Cur - candidate.spec1Cur) <= epsilon
                   && Math.Abs(keeper.spec2Cur - candidate.spec2Cur) <= epsilon
                   && Math.Abs(keeper.spec3Cur - candidate.spec3Cur) <= epsilon
                   && Math.Abs(keeper.spec1Cap - candidate.spec1Cap) <= epsilon
                   && Math.Abs(keeper.spec2Cap - candidate.spec2Cap) <= epsilon
                   && Math.Abs(keeper.spec3Cap - candidate.spec3Cap) <= epsilon;
        }

        private static int OwnedCopyCount(int id, IEnumerable<Equipment> owned)
        {
            return owned.Count(x => x != null && x.id == id);
        }

        private static bool AllOwnedCopiesMaxxed(int id, IEnumerable<Equipment> owned)
        {
            var copies = owned.Where(x => x != null && x.id == id).ToArray();
            return copies.Length > 1 && copies.All(x => x.level >= 100);
        }

        /*
        PHYSICAL COPY DEMAND

        Identical MAXXED accessories/weapons can be genuinely redundant, but only after counting
        multiplicity. Daycare is simultaneous with a combat/loadout use and is added. Active gear,
        each configured purpose loadout, and each native saved loadout are mutually exclusive, so
        the largest per-context multiplicity is retained rather than summing every hypothetical.
        Candidate slots referenced by a native loadout are separately ineligible above.
        */
        private int RequiredPhysicalCopyCount(int id)
        {
            if (EndgameDependencyModel.IsEndItem(id)) return 1;
            var inv = _character.inventory;
            var daycare = inv.daycare == null ? 0 : inv.daycare.Count(x => x != null && x.id == id);
            var active = new[] {inv.head, inv.chest, inv.legs, inv.boots, inv.weapon, inv.weapon2}
                .Count(x => x != null && x.id == id);
            if (inv.accs != null) active += inv.accs.Count(x => x != null && x.id == id);

            var configured = new[]
            {
                CountInArray(Settings.TitanLoadout, id),
                CountInArray(Settings.YggdrasilLoadout, id),
                CountInArray(Settings.GoldDropLoadout, id),
                CountInArray(Settings.MoneyPitLoadout, id),
                CountInArray(Settings.QuickLoadout, id)
            }.Max();
            var native = 0;
            if (inv.loadouts != null)
            {
                foreach (var loadout in inv.loadouts)
                {
                    if (loadout == null) continue;
                    var count = CountInventorySlotId(inv, loadout.head, id)
                                + CountInventorySlotId(inv, loadout.chest, id)
                                + CountInventorySlotId(inv, loadout.legs, id)
                                + CountInventorySlotId(inv, loadout.boots, id)
                                + CountInventorySlotId(inv, loadout.weapon, id)
                                + CountInventorySlotId(inv, loadout.weapon2, id);
                    if (loadout.accessories != null)
                        count += loadout.accessories.Sum(slot => CountInventorySlotId(inv, slot, id));
                    native = Math.Max(native, count);
                }
            }
            return daycare + Math.Max(1, Math.Max(active, Math.Max(configured, native)));
        }

        private static int CountInArray(int[] ids, int id)
        {
            return ids == null ? 0 : ids.Count(x => x == id);
        }

        private static int CountInventorySlotId(Inventory inv, int slot, int id)
        {
            return inv.inventory != null && slot >= 0 && slot < inv.inventory.Count
                   && inv.inventory[slot] != null && inv.inventory[slot].id == id ? 1 : 0;
        }

        private static bool DominatesFixedSlotForAllFuture(Equipment keeper, Equipment candidate)
        {
            if (keeper.id <= 0 || keeper.id == candidate.id || keeper.type != candidate.type)
                return false;
            if (candidate.type != part.Head && candidate.type != part.Chest
                && candidate.type != part.Legs && candidate.type != part.Boots)
                return false;
            // Any special can be a later resource/loadout bottleneck even when its
            // present scalar looks weak, so cross-ID proof is deliberately disallowed.
            if (candidate.spec1Type != specType.None || candidate.spec2Type != specType.None
                || candidate.spec3Type != specType.None)
                return false;
            const double epsilon = 1e-6;
            return keeper.curAttack + epsilon >= candidate.curAttack
                   && keeper.curDefense + epsilon >= candidate.curDefense
                   && keeper.capAttack + epsilon >= candidate.capAttack
                   && keeper.capDefense + epsilon >= candidate.capDefense
                   && keeper.bossRequired <= candidate.bossRequired;
        }

        private string SafeItemName(int id)
        {
            try { return _controller.itemInfo.itemName[id]; }
            catch { return "item"; }
        }

        #region Filtering
        internal void EnsureFiltered(ih[] ci)
        {
            var settings = _character.settings;
            var typeFilterChanged = settings.filterHead || settings.filterChest || settings.filterLegs
                                    || settings.filterBoots || settings.filterWeapon || settings.filterAccessory
                                    || settings.filterBoosts || settings.filterBoostAtk || settings.filterBoostDef
                                    || settings.filterBoostSpec || settings.filterMisc || settings.filterTitan;
            // Every coarse filter is destructive before the bot can inspect an ID. In particular, Misc
            // includes progression, puzzle, and quest state-machine items, while Titan can discard unique
            // set pieces. Full automation therefore owns all coarse toggles and keeps them disabled.
            settings.filterHead = false;
            settings.filterChest = false;
            settings.filterLegs = false;
            settings.filterBoots = false;
            settings.filterWeapon = false;
            settings.filterAccessory = false;
            settings.filterBoosts = false;
            settings.filterBoostAtk = false;
            settings.filterBoostDef = false;
            settings.filterBoostSpec = false;
            settings.filterMisc = false;
            settings.filterTitan = false;

            if (!Main.Character.arbitrary.lootFilter)
            {
                LastFilterDecision = typeFilterChanged
                    ? "Disabled coarse equipment-type filters so unMAXXED collection drops cannot be destroyed"
                    : "Improved item filter not owned; all equipment types remain enabled for MAXX collection";
                return;
            }

            var list = _character.inventory.itemList;
            var count = Math.Min(list.itemFiltered.Count, list.itemMaxxed.Count);
            var unfiltered = 0;
            for (var id = 0; id < count; id++)
            {
                if (CanFilterItem(id)) continue;
                if (!list.itemFiltered[id]) continue;
                list.itemFiltered[id] = false;
                unfiltered++;
            }

            var targets = ci.Where(x => x.level == 100 && x.id < list.itemMaxxed.Count
                                        && list.itemMaxxed[x.id]
                                        && !AdventureCollectionPlanner.IsProtectedCollectionItem(_character, x.id));
            foreach (var target in targets)
            {
                FilterItem(target.id);
            }

            FilterEquip(_character.inventory.head);
            FilterEquip(_character.inventory.boots);
            FilterEquip(_character.inventory.chest);
            FilterEquip(_character.inventory.legs);
            FilterEquip(_character.inventory.weapon);
            if (_character.inventoryController.weapon2Unlocked())
                FilterEquip(_character.inventory.weapon2);

            foreach (var acc in _character.inventory.accs)
            {
                FilterEquip(acc);
            }
            LastFilterDecision = unfiltered > 0 || typeFilterChanged
                ? "Reopened " + unfiltered + " unsafe exact IDs and disabled every coarse filter; only confirmed MAXXED ordinary collection-complete equipment remains filtered"
                : "Only confirmed MAXXED ordinary collection-complete equipment is filtered; boosts, misc, Titan, puzzle, quest, and progression drops remain enabled";
        }

        void FilterItem(int id)
        {
            if (CanFilterItem(id))
                _character.inventory.itemList.itemFiltered[id] = true;
        }

        private bool CanFilterItem(int id)
        {
            var list = _character.inventory.itemList;
            if (id <= 0 || list.itemMaxxed == null || id >= list.itemMaxxed.Count
                || !list.itemMaxxed[id] || id >= _controller.itemInfo.type.Length)
                return false;
            if (_controller.itemInfo.type[id] < part.Head || _controller.itemInfo.type[id] > part.Accessory)
                return false;
            if (!AdventureCollectionPlanner.IsKnownCompletedOrdinaryItem(_character, id))
                return false;
            if (_pendants.Contains(id) || _lootys.Contains(id) || _wandoos.Contains(id)
                || _filterExcludes.Contains(id) || _guffs.Contains(id) || _mergeBlacklist.Contains(id)
                || _convertibles.Contains(id) || EndgameDependencyModel.IsEndItem(id)
                || id >= 278 && id <= 287)
                return false;
            return !IsConfiguredLoadoutItem(id);
        }

        void FilterEquip(Equipment e)
        {
            if (e.level == 100)
            {
                FilterItem(e.id);
            }
        }
        #endregion

    }

    public class ih
    {
        internal int slot { get; set; }
        internal string name { get; set; }
        internal int level { get; set; }
        internal bool locked { get; set; }
        internal int id { get; set; }
        internal Equipment equipment { get; set; }
    }

    public class BoostsNeeded
    {
        internal decimal Power { get; set; }
        internal decimal Toughness { get; set; }
        internal decimal Special { get; set; }

        public BoostsNeeded()
        {
            Power = 0;
            Toughness = 0;
            Special = 0;
        }

        public void Add(BoostsNeeded other)
        {
            Power += other.Power;
            Toughness += other.Toughness;
            Special += other.Special;
        }

        public decimal Total()
        {
            return Power + Toughness + Special;
        }
    }
}
