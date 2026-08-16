using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using static NGUInjector.Main;

namespace NGUInjector.Managers
{
    internal class WishManager
    {
        private readonly Character _character;
        private readonly List<int> _curValidUpgradesList = new List<int>();
        // Early Wish effects that open/accelerate entire systems.  Once those gates
        // are complete the marginal-speed ordering below takes over all 231 Wishes.
        private static readonly int[] ProgressionGateOrder =
        {
            0, 1, 2, 3, 4, 8, 6, 5, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20,
            21, 23, 24, 25
        };

        public WishManager()
        {
            _character = Main.Character;
        }

        public int GetSlot(int slotId)
        {
            BuildWishList();
            if (slotId + 1 > _curValidUpgradesList.Count)
            {
                return -1;
            }
            return _curValidUpgradesList[slotId];
        }

        public void BuildWishList()
        {
            var dictDouble = new Dictionary<int, double>();

            _curValidUpgradesList.Clear();

            for (var i = 0; i < _character.wishes.wishes.Count; i++)
            {
                var wish = _character.wishes.wishes[i];
                if (!PrecisionImpossible(i) || wish.energy <= 0 && wish.magic <= 0 && wish.res3 <= 0)
                    continue;
                _character.wishesController.removeAllResources(i);
                Main.LogAction("HOLD", "Removed resources from Wish " + i
                                       + " because its best-case level time exceeds the native floating-point completion limit");
            }

            // Never throw away partial progress merely because another Wish's raw
            // divider changed. Native leveling discards overflow, so finishing an
            // active partial level first avoids fragmentation and returns its slot.
            for (var i = 0; i < _character.wishes.wishes.Count; i++)
            {
                var wish = _character.wishes.wishes[i];
                if ((wish.energy > 0 || wish.magic > 0 || wish.res3 > 0 || wish.progress > 0)
                    && isValidWish(i))
                    _curValidUpgradesList.Add(i);
            }

            for (var i = 0; i < Settings.WishPriorities.Length; i++)
            {
                if (_curValidUpgradesList.Contains(Settings.WishPriorities[i])
                    || dictDouble.ContainsKey(Settings.WishPriorities[i]))
                    continue;
                if (isValidWish(Settings.WishPriorities[i]))
                {
                    if (Settings.WishSortPriorities)
                    {
                        dictDouble.Add(Settings.WishPriorities[i], sortValue(Settings.WishPriorities[i]) + i);
                    } else
                    {
                        _curValidUpgradesList.Add(Settings.WishPriorities[i]);
                    }
                }
            }
            for (var i = 0; i < ProgressionGateOrder.Length; i++)
            {
                var id = ProgressionGateOrder[i];
                if (!_curValidUpgradesList.Contains(id) && isValidWish(id))
                    _curValidUpgradesList.Add(id);
            }
            if (Settings.WishSortPriorities)
            {
                dictDouble = (from x in dictDouble
                              orderby x.Value
                              select x).ToDictionary(x => x.Key, x => x.Value);
                for (var j = 0; j < dictDouble.Count; j++)
                {
                    _curValidUpgradesList.Add(dictDouble.ElementAt(j).Key);
                }
                dictDouble = new Dictionary<int, double>();
            }
            for (var i = 0; i < _character.wishes.wishes.Count; i++)
            {
                if (_curValidUpgradesList.Contains(i))
                {
                    continue;
                }
                if (isValidWish(i))
                {
                    dictDouble.Add(i, this.sortValue(i) + i);
                }
            }            
            dictDouble = (from x in dictDouble
                               orderby x.Value
                               select x).ToDictionary(x => x.Key, x => x.Value);
            for (var j = 0; j < dictDouble.Count; j++)
            {
                _curValidUpgradesList.Add(dictDouble.ElementAt(j).Key);
            }
        }

        public bool isValidWish(int wishId)
        {
            if (wishId < 0 || wishId >= _character.wishes.wishSize())
            {
                return false;
            }
            if (_character.wishesController.wishLocked(wishId))
            {
                return false;
            }
            if (_character.wishesController.properties[wishId].difficultyRequirement > _character.wishesController.character.settings.rebirthDifficulty)
            {
                return false;
            }
            if (_character.wishesController.progressPerTickMax(wishId) <= 0f)
            {
                return false;
            }
            if (PrecisionImpossible(wishId))
            {
                return false;
            }
            if (_character.wishesController.character.wishes.wishes[wishId].level >= _character.wishesController.properties[wishId].maxLevel)
            {
                return false;
            }
            return true;          
        }

        private bool PrecisionImpossible(int wishId)
        {
            if (wishId < 0 || wishId >= _character.wishes.wishes.Count)
                return true;
            var rate = _character.wishesController.progressPerTickMax(wishId);
            if (rate <= 0f) return true;
            var bestCaseSeconds = 1.0 / rate / 50.0;
            // Native single-precision progress can stop advancing near 50% when a
            // full level is longer than 7d17h12m.  Treat the documented boundary
            // conservatively; an impossible Wish has zero progression value.
            return bestCaseSeconds > 666720.0;
        }

        public double sortValue(int wishId)
        {
            if (Settings.WishSortOrder)
            {
                return _character.wishesController.wishSpeedDivider(wishId);
            }
            return _character.wishesController.properties[wishId].wishSpeedDivider;
        }
    }
}
