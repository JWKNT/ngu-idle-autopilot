using System;
using System.Collections.Generic;
using System.Linq;

/*
FILE PURPOSE

This manager owns late-game Card and Cooking automation: tag membership, six-currency Mayo
scheduling, permanent Card consumption, conservative deck reclamation, recipe selection, and dish
execution. Native Mayo throughput is aggregate-constant: with N active generators each receives
1/N of the base rate. Generator policy therefore allocates a fixed production stream among six
currencies by shadow shortage; it must not treat generator slots as independent throughput.

Card casts, END-card conversion, Chonker protection, and yeeting are irreversible. Every mutation
is verified from deck, Mayo, bonus, or inventory state. Sadistic Attack/Defense Cards remain in the
value model because Fight Boss 300 and T13/T14 can make them terminal-critical. Yeeting fails
closed unless a low-rarity, non-foil Card has no admitted current/terminal use; uncertain future
tier, Mayo, recycling, and END option value is retained.
*/
namespace NGUInjector.Autopilot
{
    internal static class CardCookingManager
    {
        private static readonly int EndCardItemId = MechanicsEndgame.AllRequirements()
            .First(x => x.DependencyKind == EndDependencyKind.EndCard).ItemId;
        private static int _lastCookingDish = -1;
        private static int _lastCookingUnlockMask = -1;
        private static string _lastCookingPairSignature = string.Empty;
        private static readonly cardBonus[] EvilPriorities =
        {
            cardBonus.adventureStat, cardBonus.hackSpeed, cardBonus.wishSpeed
        };

        private static readonly cardBonus[] SadisticPriorities =
        {
            cardBonus.adventureStat, cardBonus.atkDefStats, cardBonus.PP, cardBonus.QP, cardBonus.wishSpeed,
            cardBonus.hackSpeed, cardBonus.energyNGUSpeed, cardBonus.magicNGUSpeed,
            cardBonus.dropChance
        };

        private sealed class MayoShadow
        {
            internal int Id;
            internal double Price;
        }

        internal static void ManageCards(Character c, AutopilotConfig config, bool fullControl)
        {
            if (c.cards == null || !c.cards.cardsOn || c.cardsController == null)
                return;

            var priorities = c.settings.rebirthDifficulty == difficulty.sadistic ? SadisticPriorities : EvilPriorities;
            SetTags(c, priorities);
            SetManaGenerators(c, priorities);
            if (fullControl)
                ConsumeEndCards(c);
            ProtectPermanentCards(c, fullControl);

            if (!fullControl)
                return;

            CastBestAffordableCard(c, priorities);
            if (config.AllowCardYeeting)
                YeetWorstCardIfFull(c, priorities);
        }

        private static void SetTags(Character c, IEnumerable<cardBonus> priorities)
        {
            // Native generateBonusType gives equal-width acceptance bands to every tagged member.
            // Order does not prioritize one member over another; keep a stable value-ordered list.
            var desired = priorities.Take(c.cardsController.maxTagSize()).ToArray();
            if (c.cards.taggedBonuses.SequenceEqual(desired))
                return;
            c.cards.taggedBonuses.Clear();
            c.cards.taggedBonuses.AddRange(desired);
            c.cardsController.updateMenu();
            Main.Log("Autopilot cards: tags=" + string.Join(",", desired.Select(x => x.ToString()).ToArray()));
        }

        private static void SetManaGenerators(Character c, IEnumerable<cardBonus> priorities)
        {
            var order = priorities.ToArray();
            var shadows = ComputeMayoShadowPrices(c, order);
            var slots = Math.Max(1, c.cardsController.maxManaGenSize());
            var top = shadows.Length == 0 ? 0.0 : shadows[0].Price;
            // One active generator produces the same aggregate Mayo as all available generators.
            // Concentrate when one currency clearly blocks the best Card; diversify only across
            // comparable shortages so several near-critical Cards can approach affordability.
            var chosenCount = shadows.Length > 1 && shadows[1].Price >= top * .80
                ? Math.Min(slots, shadows.Length) : Math.Min(1, shadows.Length);
            var chosen = shadows.Take(chosenCount).Select(x => x.Id).ToArray();
            var changed = false;
            for (var i = 0; i < c.cards.manas.Count; i++)
            {
                var running = chosen.Contains(i);
                if (c.cards.manas[i].running == running) continue;
                c.cards.manas[i].running = running;
                changed = true;
            }
            if (changed) c.cardsController.updateMenu();
        }

        private static MayoShadow[] ComputeMayoShadowPrices(Character c, cardBonus[] priorities)
        {
            var prices = new double[c.cards.manas.Count];
            var useful = c.cards.cards.Where(card => card.type != cardType.end
                && EligibleAtCurrentTier(c, card)).ToArray();
            foreach (var card in useful)
            {
                var value = CardPermanentValue(c, card, priorities);
                var totalShortage = 0.0;
                for (var i = 0; i < prices.Length && i < card.manaCosts.Count; i++)
                    totalShortage += Math.Max(0, card.manaCosts[i] - c.cards.manas[i].amount);
                if (totalShortage <= 0.0) continue;
                for (var i = 0; i < prices.Length && i < card.manaCosts.Count; i++)
                {
                    var shortage = Math.Max(0, card.manaCosts[i] - c.cards.manas[i].amount);
                    prices[i] += value * shortage / totalShortage;
                }
            }

            // A small reserve shadow price values the option to cast the next useful spawned Card.
            // It is based on the per-currency held-deck cost distribution, never on the sum of Mayo
            // balances (the six currencies are not fungible).
            for (var i = 0; i < prices.Length; i++)
            {
                var costs = useful.Where(x => x.manaCosts.Count > i && x.manaCosts[i] > 0)
                    .Select(x => x.manaCosts[i]).OrderBy(x => x).ToArray();
                if (costs.Length > 0)
                {
                    var reserve = costs[costs.Length / 2];
                    if (reserve > c.cards.manas[i].amount)
                        prices[i] += .05 * (reserve - c.cards.manas[i].amount) / Math.Max(1.0, reserve);
                }
                if (prices[i] <= 0.0)
                    prices[i] = 1.0 / Math.Max(1.0, c.cards.manas[i].amount + 1.0);
            }
            return prices.Select((price, id) => new MayoShadow {Id = id, Price = price})
                .OrderByDescending(x => x.Price).ThenBy(x => x.Id).ToArray();
        }

        private static void ProtectPermanentCards(Character c, bool fullControl)
        {
            for (var i = 0; i < c.cards.cards.Count; i++)
            {
                var card = c.cards.cards[i];
                if (!fullControl) continue;
                if (card.type == cardType.end)
                {
                    var shouldProtect = !c.inventoryController.freeSpace();
                    if (card.isProtected != shouldProtect)
                        c.cardsController.protectCard(i);
                    continue;
                }
                var terminalStats = c.settings.rebirthDifficulty == difficulty.sadistic
                                    && card.bonusType == cardBonus.atkDefStats
                                    && EndgameDependencyModel.IsTerminalCombatCritical(c);
                if (card.cardRarity != rarity.BigChonker && !terminalStats) continue;
                // Before recycling, an off-plan Chonker still banks a future 25% spawn-timer
                // refund. A useful/terminal Card is protected only while unaffordable so the cast
                // selector can consume it immediately when its exact Mayo vector is ready.
                var useful = EligibleAtCurrentTier(c, card) || terminalStats;
                var protect = useful ? !Affordable(c, card) : !HasChonkerRecycling(c);
                if (card.isProtected != protect)
                    c.cardsController.protectCard(i);
            }
        }

        private static void ConsumeEndCards(Character c)
        {
            // End cards grant item 492 level 100 and otherwise occupy a deck slot
            // forever. They use no Mayo but require one free inventory slot.
            for (var i = c.cards.cards.Count - 1; i >= 0; i--)
            {
                var card = c.cards.cards[i];
                if (card.type != cardType.end || !c.inventoryController.freeSpace())
                    continue;
                if (card.isProtected)
                    c.cardsController.protectCard(i);
                var before = c.cards.cards.Count;
                var piecesBefore = CountInventoryItem(c, EndCardItemId);
                c.cardsController.tryConsumeCard(i);
                var confirmed = c.cards.cards.Count < before
                                && CountInventoryItem(c, EndCardItemId) > piecesBefore;
                Main.LogAction(confirmed ? "CARD" : "REJECTED",
                    confirmed
                        ? "Consumed End card for item " + EndCardItemId
                          + " [confirmed by deck debit and exact inventory credit]"
                        : "End-card consume request lacked a verified deck debit plus exact END-item credit");
            }
        }

        private static void CastBestAffordableCard(Character c, IEnumerable<cardBonus> priorities)
        {
            var order = priorities.ToArray();
            var candidate = c.cards.cards.Select((card, index) => new {card, index})
                .Where(x => !x.card.isProtected && x.card.cardRarity >= rarity.Meh
                            && order.Contains(x.card.bonusType) && EligibleAtCurrentTier(c, x.card)
                            && Affordable(c, x.card))
                .OrderByDescending(x => CardUtility(x.card, order))
                .FirstOrDefault();
            if (candidate == null) return;
            var countBefore = c.cards.cards.Count;
            var manaBefore = c.cards.manas.Select(x => x.amount).ToArray();
            var exactCosts = candidate.card.manaCosts.ToArray();
            c.cardsController.tryConsumeCard(candidate.index);
            var exactMayoDebit = Enumerable.Range(0, c.cards.manas.Count).All(i =>
                c.cards.manas[i].amount == manaBefore[i]
                - (i < exactCosts.Length ? exactCosts[i] : 0));
            var confirmed = c.cards.cards.Count == countBefore - 1 && exactMayoDebit;
            Main.LogAction(confirmed ? "CARD" : "REJECTED",
                confirmed
                    ? "Cast " + candidate.card.cardName + " [confirmed by exact deck and six-Mayo debit]"
                    : "Card cast request for " + candidate.card.cardName
                      + " lacked an exact deck plus six-Mayo debit transition");
        }

        private static bool Affordable(Character c, Card card)
        {
            for (var i = 0; i < card.manaCosts.Count; i++)
                if (card.manaCosts[i] > c.cards.manas[i].amount) return false;
            return true;
        }

        private static double CardUtility(Card card, cardBonus[] priorities)
        {
            return CardPermanentValue(Main.Character, card, priorities)
                   / Math.Max(1L, card.manaCosts.Sum(x => (long)x));
        }

        private static double CardPermanentValue(Character c, Card card, cardBonus[] priorities)
        {
            var index = Array.IndexOf(priorities, card.bonusType);
            if (index < 0)
                return 0;
            var strategicWeight = 1.0 + (priorities.Length - index) * 0.35;
            if (c != null && card.bonusType == cardBonus.atkDefStats
                && EndgameDependencyModel.IsTerminalCombatCritical(c))
                strategicWeight *= 12.0;
            // effectAmount already incorporates the quality/rarity roll.
            return Math.Max(0.0, card.effectAmount) * strategicWeight;
        }

        private static bool EligibleAtCurrentTier(Character c, Card card)
        {
            if (c.settings.rebirthDifficulty < difficulty.sadistic)
                return card.bonusType == cardBonus.adventureStat
                       || card.bonusType == cardBonus.hackSpeed
                       || card.bonusType == cardBonus.wishSpeed;
            // PP/QP do not repay their Mayo opportunity cost until tier 4; NGU
            // cards need tier 8.  Adventure remains progression-critical, while
            // Hack/Wish retain value until their late-game saturation limits.
            if (card.bonusType == cardBonus.PP || card.bonusType == cardBonus.QP)
                return card.tier >= 4;
            if (card.bonusType == cardBonus.energyNGUSpeed || card.bonusType == cardBonus.magicNGUSpeed)
                return card.tier >= 8;
            return card.bonusType == cardBonus.adventureStat
                   || card.bonusType == cardBonus.atkDefStats
                   || card.bonusType == cardBonus.hackSpeed
                   || card.bonusType == cardBonus.wishSpeed
                   || card.bonusType == cardBonus.dropChance;
        }

        private static void YeetWorstCardIfFull(Character c, ICollection<cardBonus> priorities)
        {
            if (c.cards.cards.Count < c.cardsController.maxDeckSize()) return;
            var candidate = c.cards.cards.Select((card, index) => new {card, index})
                .Where(x => CanProveCardYeetSafe(c, x.card))
                .OrderBy(x => CardUtility(x.card, priorities.ToArray()))
                .ThenBy(x => x.card.cardRarity)
                .ThenBy(x => x.card.effectAmount)
                .FirstOrDefault();
            if (candidate == null) return;
            var countBefore = c.cards.cards.Count;
            c.cardsController.trashCard(candidate.index);
            Main.LogAction(c.cards.cards.Count < countBefore ? "CARD" : "REJECTED",
                c.cards.cards.Count < countBefore
                    ? "Yeeted " + candidate.card.cardName + " [confirmed by deck count]"
                    : "Card yeet request for " + candidate.card.cardName + " produced no deck change");
        }

        private static bool CanProveCardYeetSafe(Character c, Card card)
        {
            if (card == null || card.isProtected || card.type != cardType.normal)
                return false;
            if (card.cardRarity > rarity.Bad || card.cardRarity == rarity.BigChonker)
                return false;
            // Preserve every bonus admitted anywhere on the route, not merely at today's tier.
            // The only provable yeet class is a low-rarity normal card whose bonus is absent from
            // the full Sadistic value set; this retains A/D and later PP/QP/NGU option value.
            return !SadisticPriorities.Contains(card.bonusType);
        }

        private static int CountInventoryItem(Character c, int id)
        {
            if (c == null || c.inventory == null) return 0;
            var count = c.inventory.inventory.Count(x => x != null && x.id == id);
            if (c.inventory.daycare != null)
                count += c.inventory.daycare.Count(x => x != null && x.id == id);
            return count;
        }

        private static bool HasChonkerRecycling(Character c)
        {
            return c.adventure.itopod.perkLevel.Count > 216
                   && c.adventure.itopod.perkLevel[216] >= 1;
        }

        internal static void ManageCooking(Character c, bool fullControl)
        {
            if (c.cooking == null || !c.cooking.unlocked || c.cookingController == null)
                return;

            OptimizeCooking(c);
            while (fullControl && c.cooking.cookTimer >= c.cookingController.eatRate())
            {
                var timerBefore = c.cooking.cookTimer;
                var bonusBefore = c.cooking.expBonus;
                c.cookingController.consumeDish();
                var confirmed = c.cooking.cookTimer < timerBefore || c.cooking.expBonus > bonusBefore;
                Main.LogAction(confirmed ? "COOKING" : "REJECTED",
                    confirmed
                        ? "Consumed globally optimized dish [confirmed by timer/bonus delta]"
                        : "Cooking consume request produced no state transition");
                if (!confirmed) break;
                OptimizeCooking(c);
            }
        }

        private static void OptimizeCooking(Character c)
        {
            var controller = c.cookingController;
            var ingredients = c.cooking.ingredients;
            var unlockMask = 0;
            for (var i = 0; i < ingredients.Count; i++)
                if (ingredients[i].unlocked) unlockMask |= 1 << i;
            var pairs = new[] {c.cooking.pair1, c.cooking.pair2, c.cooking.pair3, c.cooking.pair4};
            var pairSignature = string.Join("|", pairs.Select(pair => pair == null
                ? "null"
                : string.Join(",", pair.Select(x => x.ToString()).ToArray())).ToArray());
            if (_lastCookingDish == c.cooking.curDishIndex && _lastCookingUnlockMask == unlockMask
                && _lastCookingPairSignature == pairSignature
                && controller.getCurPercentofMaxScore() >= 0.999999f)
                return;
            var max = controller.maxIngredientLevel();
            var changed = false;
            foreach (var pair in pairs)
            {
                if (pair == null || pair.Count < 2) continue;
                var first = pair[0];
                var second = pair[1];
                if (first < 0 || second < 0 || first >= ingredients.Count || second >= ingredients.Count)
                    continue;
                var firstUnlocked = ingredients[first].unlocked;
                var secondUnlocked = ingredients[second].unlocked;
                if (!firstUnlocked && !secondUnlocked) continue;
                var originalFirst = ingredients[first].curLevel;
                var originalSecond = ingredients[second].curLevel;
                var bestFirst = originalFirst;
                var bestSecond = originalSecond;
                var bestScore = float.MinValue;
                try
                {
                    for (var firstLevel = firstUnlocked ? 0 : originalFirst;
                         firstLevel <= (firstUnlocked ? max : originalFirst); firstLevel++)
                    {
                        ingredients[first].curLevel = firstLevel;
                        for (var secondLevel = secondUnlocked ? 0 : originalSecond;
                             secondLevel <= (secondUnlocked ? max : originalSecond); secondLevel++)
                        {
                            ingredients[second].curLevel = secondLevel;
                            var score = controller.getCurScore();
                            if (score <= bestScore) continue;
                            bestScore = score;
                            bestFirst = firstLevel;
                            bestSecond = secondLevel;
                        }
                    }
                }
                catch
                {
                    ingredients[first].curLevel = originalFirst;
                    ingredients[second].curLevel = originalSecond;
                    throw;
                }
                if (originalFirst != bestFirst || originalSecond != bestSecond) changed = true;
                ingredients[first].curLevel = bestFirst;
                ingredients[second].curLevel = bestSecond;
            }
            _lastCookingDish = c.cooking.curDishIndex;
            _lastCookingUnlockMask = unlockMask;
            _lastCookingPairSignature = pairSignature;
            if (changed)
            {
                controller.updateMenu();
                Main.Log("Autopilot cooking: optimized recipe to " + controller.getCurPercentofMaxScore().ToString("P1"));
            }
        }
    }
}
