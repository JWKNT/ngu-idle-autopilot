using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static NGUInjector.Main;

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
        private Equipment _lastBotTrashed;
        internal static string LastTrashDecision { get; private set; }
            = "Waiting for the first conservative trash audit";


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
            var result = new List<ih>();
            //First, find items in our priority list
            foreach (var id in Settings.PriorityBoosts)
            {
                if (Settings.BoostBlacklist.Contains(id))
                    continue;
                
                var f = FindItemSlot(ci, id);
                if (f != null)
                    result.Add(f);
            }

            //Next, get equipped items that aren't in our priority list and aren't blacklisted
            var equipped = Main.Character.inventory.GetConvertedEquips()
                .Where(x => !Settings.PriorityBoosts.Contains(x.id) && !Settings.BoostBlacklist.Contains(x.id));
            result = result.Concat(equipped).ToList();

            //Finally, find locked items in inventory that aren't blacklisted
            var invItems = ci.Where(x => x.locked && x.equipment.isEquipment() && !Settings.BoostBlacklist.Contains(x.id) && !Settings.PriorityBoosts.Contains(x.id));
            result = result.Concat(invItems).ToList();

            //Make sure we filter out non-equips again, just in case one snuck into priorityboosts
            return result.Where(x => x.equipment.isEquipment()).Where(x => x.equipment.GetNeededBoosts().Total() > 0).ToArray();
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
                    Main.LogAction("INVENTORY", "Applied boosts to " + SanitizeName(item.name)
                                                + " [confirmed by item/boost delta]");
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

        internal void ManageQuestItems(ih[] ci)
        {
            var curPage = (int)Math.Floor((double)_controller.inventory[0].id / 60);
            //Merge quest items first
            var toMerge = ci.Where(x =>
                x.id >= 278 && x.id <= 287 && !_character.inventory.inventory[x.slot].removable &&
                !_character.inventory.itemList.itemMaxxed[x.id]);

            foreach (var target in toMerge)
            {
                if (ci.Count(x => x.id == target.id) <= 1) continue;
                Log($"Merging {SanitizeName(target.name)} in slot {target.slot}");
                _controller.mergeAll(target.slot);
            }

            //Consume quest items that dont need to be merged
            var questItems = ci.Where(x =>
                x.id >= 278 && x.id <= 287 && _character.inventory.inventory[x.slot].removable).ToArray();

            if (questItems.Length > 0)
                Log($"Turning in {questItems.Length} quest items");
            foreach (var target in questItems)
            {
                var newSlot = ChangePage(target.slot);
                var ic = _controller.inventory[newSlot];
                typeof(ItemController).GetMethod("consumeItem", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.Invoke(ic, null);
            }
            _controller.changePage(curPage);
        }

        internal void MergeInventory(ih[] ci)
        {
            var grouped =
                ci.Where(x => x.id >= 40 && x.level < 100 && !_mergeBlacklist.Contains(x.id) && !_guffs.Contains(x.id) && (x.id < 278 || x.id > 287)).GroupBy(x => x.id).Where(x => x.Count() > 1);

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
            // spells cannot level it.  Fill every empty unlocked slot before merging.
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
                    ? "Equipped MacGuffin " + expected + " into empty slot " + (guffSlot + 1)
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
                    || (x.id == 92 && !_character.settings.yggdrasilOn)));
            if (progression != null && _character.inventory.inventory[progression.slot].removable)
            {
                ConsumeInventorySlot(progression.slot);
                Main.LogAction("PROGRESSION", "Consumed progression item " + progression.id
                    + " through the game's ItemController");
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
                ConsumeInventorySlot(wandoos.slot);
                var confirmed = !_character.settings.wandoos98On || wandoos.id == 66
                    ? _character.settings.wandoos98On || _character.wandoos98.OSlevel > osBefore
                    : _character.wandoos98.XLLevels > xlBefore;
                Main.LogAction(confirmed ? "PROGRESSION" : "REJECTED", confirmed
                    ? "Consumed Wandoos item " + wandoos.id + " [confirmed by OS state]"
                    : "Wandoos item " + wandoos.id + " produced no OS transition");
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
                typeof(ItemController).GetMethod("consumeItem", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.Invoke(ic, null);
            }
            _controller.changePage(curPage);
        }

        private void ConsumeInventorySlot(int slot)
        {
            var newSlot = ChangePage(slot);
            typeof(ItemController).GetMethod("consumeItem", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(_controller.inventory[newSlot], null);
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
                    Main.LogAction("REJECTED", "Exile clue " + pair.Value + " could not be moved to slot " + pair.Key);
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

        internal void ManageBoostConversion(ih[] boostSlots)
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

            var needed = new BoostsNeeded();

            foreach (var item in boostSlots)
            {
                needed.Add(item.equipment.GetNeededBoosts());
            }

            if (needed.Power > 0)
            {
                _controller.selectAutoPowerTransform();
                return;
            }

            if (needed.Toughness > 0)
            {
                _controller.selectAutoToughTransform();
                return;
            }

            if (needed.Special > 0)
            {
                _controller.selectAutoSpecialTransform();
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

        // Conservative inventory reclamation.  Different item IDs are never
        // compared: an apparently weaker item can carry a unique set/progression
        // role.  We only discard a maxed duplicate when another physical copy of
        // the exact same ID is at least as good in every present and future stat.
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

            // The native trash slot is the user's one-step undo. Never overwrite an
            // item we did not put there. Once our own proven-useless item occupies it,
            // replacing it with the next proven-useless copy is intentional.
            if (inv.trash != null && inv.trash.id > 0
                && !ReferenceEquals(inv.trash, _lastBotTrashed))
            {
                LastTrashDecision = "Blocked: the recoverable trash slot contains an item not placed there by the bot";
                return;
            }

            var owned = new List<Equipment>
            {
                inv.head, inv.chest, inv.legs, inv.boots, inv.weapon, inv.weapon2
            };
            owned.AddRange(inv.accs);
            owned.AddRange(inv.inventory);

            for (var slot = 0; slot < inv.inventory.Count; slot++)
            {
                var candidate = inv.inventory[slot];
                if (!CanProveTrashSafe(candidate, slot)) continue;
                var keeper = owned.FirstOrDefault(x => x != null
                    && !ReferenceEquals(x, candidate) && x.id == candidate.id
                    && DominatesForAllUses(x, candidate));
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
                if (confirmed) _lastBotTrashed = candidate;
                LastTrashDecision = confirmed
                    ? "Trashed one proven-redundant same-ID dominated MAXXED duplicate"
                    : "Native trash request did not produce a verified recoverable-slot transition";
                Main.LogAction(confirmed ? "TRASH" : "REJECTED", confirmed
                    ? "Trashed redundant item " + SafeItemName(id) + " (ID " + id + ", level " + level
                      + "): Item List is MAXXED and retained same-ID copy dominates ATK "
                      + attack.ToString("0.##") + "/DEF " + defense.ToString("0.##")
                      + " plus every cap/special field [confirmed in native recoverable trash slot]"
                    : "Redundant-item trash request for ID " + id + " produced no verified slot transition");
                return; // preserve a full second of native one-step recovery
            }
            LastTrashDecision = "Nothing is provably disposable: requires a MAXXED equipment ID and a retained same-ID physical copy that dominates every stat/cap field";
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
            if (_pendants.Contains(id) || _lootys.Contains(id) || _wandoos.Contains(id)
                || _filterExcludes.Contains(id) || _guffs.Contains(id)
                || _mergeBlacklist.Contains(id) || _convertibles.Contains(id)
                || id >= 278 && id <= 287 || id == 102 || id == 141 || id == 172)
                return false;
            if (IsNativeLoadoutReference(slot)) return false;
            return true;
        }

        private bool IsNativeLoadoutReference(int slot)
        {
            var loadouts = _character.inventory.loadouts;
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

        private string SafeItemName(int id)
        {
            try { return _controller.itemInfo.itemName[id]; }
            catch { return "item"; }
        }

        #region Filtering
        internal void EnsureFiltered(ih[] ci)
        {
            if (!Main.Character.arbitrary.lootFilter)
                return;

            var targets = ci.Where(x => x.level == 100);
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
        }

        void FilterItem(int id)
        {
            if (_pendants.Contains(id) || _lootys.Contains(id) || _wandoos.Contains(id) ||
                _filterExcludes.Contains(id) || _guffs.Contains(id) || id < 40 || _mergeBlacklist.Contains(id))
                return;

            //Dont filter quest items
            if (id >= 278 && id <= 287)
                return;

            _character.inventory.itemList.itemFiltered[id] = true;
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
