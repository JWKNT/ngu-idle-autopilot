using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static NGUInjector.Main;

/*
FILE PURPOSE

WishManager ranks permanent Wishes and maps logical allocation breakpoints onto the native Wish
slots. Ranking uses the native current-level divider, including `(level + 1)`, remaining partial
progress, and the 50 Hz completion cadence. First-level system gates and the terminal Shut Down
Wish may temporarily concentrate several allocation shares on one native Wish; ordinary marginal
levels fill separate slots so concavity increases aggregate permanent progress.

This file is read-only except for removing resources from a source-proven floating-point-stalled
Wish. WishBP performs normal Energy/Magic/R3 mutations. Explicit unsorted user priorities remain
an override. IDs are deterministic tie-breakers only: they must never be added to an economic
score. Fixed Wish-minimum reducers are seconds, not multipliers; the pure helpers below expose
their exact 24-second marginal semantics to purchase policy.
*/
namespace NGUInjector.Managers
{
    internal class WishManager
    {
        private readonly Character _character;
        private readonly List<int> _curValidUpgradesList = new List<int>();
        private int _rankingFrame = -1;
        // First levels open/accelerate whole systems. They are candidates for concentration, not
        // an instruction to max every listed Wish before considering anything else.
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
            // Allocation asks once per resource for every Wish slot during the same Unity frame.
            // Rank a coherent snapshot once; rebuilding all 231 Wishes for every query created a
            // late-game hot loop and could return different targets within one allocation sweep.
            if (_rankingFrame != Time.frameCount)
                BuildWishList();
            if (slotId + 1 > _curValidUpgradesList.Count)
            {
                return -1;
            }
            return _curValidUpgradesList[slotId];
        }

        public void BuildWishList()
        {
            _curValidUpgradesList.Clear();
            _rankingFrame = Time.frameCount;

            for (var i = 0; i < _character.wishes.wishes.Count; i++)
            {
                var wish = _character.wishes.wishes[i];
                if (!PrecisionImpossible(i) || wish.energy <= 0 && wish.magic <= 0 && wish.res3 <= 0)
                    continue;
                _character.wishesController.removeAllResources(i);
                Main.LogAction("HOLD", "Removed resources from " + GameNames.Wish(_character, i)
                                       + " because its best-case level time exceeds the native floating-point completion limit");
            }

            // Partial progress persists. It is therefore sunk state, not a reason to pin a scarce
            // slot indefinitely; normal priority and marginal-time ranking may preempt it safely.

            var explicitOrdered = new List<int>();
            var explicitRanked = new List<int>();
            for (var i = 0; i < Settings.WishPriorities.Length; i++)
            {
                var id = Settings.WishPriorities[i];
                if (explicitOrdered.Contains(id) || explicitRanked.Contains(id))
                    continue;
                if (!isValidWish(id)) continue;
                if (Settings.WishSortPriorities) explicitRanked.Add(id);
                else explicitOrdered.Add(id);
            }

            _curValidUpgradesList.AddRange(explicitOrdered);
            _curValidUpgradesList.AddRange(explicitRanked
                .OrderBy(ExactMarginalRank)
                .ThenBy(id => id));

            var candidates = Enumerable.Range(0, _character.wishes.wishes.Count)
                .Where(isValidWish)
                .Where(id => !_curValidUpgradesList.Contains(id))
                .OrderBy(ExactMarginalRank)
                .ThenBy(id => id)
                .ToList();

            // Concentrating all configured CAPWISH shares is useful for a binary dependency: the
            // native sixth-root factors are concave, but finishing one critical gate can unlock a
            // whole system while four partially advanced gates unlock nothing. Repeating an ID
            // here still consumes only one native Wish slot; WishBP's independent percentage shares
            // accumulate on that same Wish. Explicit user ordering is never rewritten this way.
            if (explicitOrdered.Count == 0 && explicitRanked.Count == 0)
            {
                var criticalCandidates = candidates.Where(IsCriticalBinaryGate).ToArray();
                if (criticalCandidates.Length > 0)
                {
                    var critical = criticalCandidates[0];
                    _curValidUpgradesList.Clear();
                    var shares = Math.Max(1, _character.wishesController.curWishSlots());
                    for (var slot = 0; slot < shares; slot++)
                        _curValidUpgradesList.Add(critical);
                    _curValidUpgradesList.AddRange(candidates.Where(id => id != critical));
                    return;
                }
            }

            _curValidUpgradesList.AddRange(candidates);
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
            if (wishId < 0 || wishId >= _character.wishes.wishes.Count)
                return double.PositiveInfinity;
            // Native wishSpeedDivider is serializedDivider * (currentLevel + 1). The old base-only
            // path made an already-levelled Wish look increasingly cheap and corrupted every rank.
            return _character.wishesController.wishSpeedDivider(wishId);
        }

        private double ExactMarginalRank(int wishId)
        {
            var seconds = RemainingSecondsAtMax(wishId);
            if (double.IsInfinity(seconds)) return seconds;
            var gateIndex = Array.IndexOf(ProgressionGateOrder, wishId);
            var firstLevelGateWeight = gateIndex >= 0 && _character.wishes.wishes[wishId].level == 0
                ? 4.0 + (ProgressionGateOrder.Length - gateIndex) / (double)ProgressionGateOrder.Length
                : 1.0;
            var terminalWeight = wishId == 203 && _character.settings.rebirthDifficulty == difficulty.sadistic
                && !HasInventoryItem(490) ? 1000000.0 : 1.0;
            return seconds / Math.Max(1.0, firstLevelGateWeight * terminalWeight);
        }

        private double RemainingSecondsAtMax(int wishId)
        {
            if (!isValidWish(wishId)) return double.PositiveInfinity;
            var rate = _character.wishesController.progressPerTickMax(wishId);
            if (rate <= 0f) return double.PositiveInfinity;
            var progress = Math.Max(0.0, Math.Min(.999999999, _character.wishes.wishes[wishId].progress));
            return (1.0 - progress) / rate / 50.0;
        }

        private bool IsCriticalBinaryGate(int wishId)
        {
            if (!isValidWish(wishId)) return false;
            if (wishId == 203 && _character.settings.rebirthDifficulty == difficulty.sadistic
                && !HasInventoryItem(490))
                return true;
            return _character.wishes.wishes[wishId].level == 0
                   && ProgressionGateOrder.Contains(wishId);
        }

        private bool HasInventoryItem(int id)
        {
            var inv = _character.inventory;
            if (inv == null) return false;
            return inv.inventory != null && inv.inventory.Any(x => x != null && x.id == id)
                   || inv.daycare != null && inv.daycare.Any(x => x != null && x.id == id)
                   || new[] {inv.head, inv.chest, inv.legs, inv.boots, inv.weapon, inv.weapon2}
                       .Any(x => x != null && x.id == id)
                   || inv.accs != null && inv.accs.Any(x => x != null && x.id == id);
        }

        internal static double FixedMinimumReductionSeconds(int purchasedLevels)
        {
            return Math.Max(0, purchasedLevels) * 24.0;
        }

        internal static double SecondsSavedByOneMinimumReducerLevel(bool minimumTimeBound,
            int affectedFutureLevels)
        {
            if (!minimumTimeBound || affectedFutureLevels <= 0) return 0.0;
            // A reducer only changes levels whose raw resource rate is already at the native
            // minimum-time cap. It saves exactly 24 seconds on each such level, never 24 percent.
            return FixedMinimumReductionSeconds(affectedFutureLevels);
        }
    }
}
