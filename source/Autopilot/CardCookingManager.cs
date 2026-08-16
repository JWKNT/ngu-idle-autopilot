using System;
using System.Collections.Generic;
using System.Linq;

/*
FILE PURPOSE

This manager owns late-game Card and Cooking automation: tagging/consumption, mayo assignment,
recipe selection, and dish execution. It reads live deck/kitchen economics and verifies native
controller transitions. Ambiguous permanent cards are retained; general spending is out of scope.
*/
namespace NGUInjector.Autopilot
{
    internal static class CardCookingManager
    {
        private static int _lastCookingDish = -1;
        private static int _lastCookingUnlockMask = -1;
        private static string _lastCookingPairSignature = string.Empty;
        private static readonly cardBonus[] EvilPriorities =
        {
            cardBonus.adventureStat, cardBonus.hackSpeed, cardBonus.wishSpeed
        };

        private static readonly cardBonus[] SadisticPriorities =
        {
            cardBonus.adventureStat, cardBonus.PP, cardBonus.QP, cardBonus.wishSpeed,
            cardBonus.hackSpeed, cardBonus.energyNGUSpeed, cardBonus.magicNGUSpeed,
            cardBonus.dropChance
        };

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
            // Native tag acceptance scales by the one-based tag position, so the
            // most valuable bonus belongs in the last (highest-probability) slot.
            var desired = priorities.Take(c.cardsController.maxTagSize()).Reverse().ToArray();
            if (c.cards.taggedBonuses.SequenceEqual(desired))
                return;
            c.cards.taggedBonuses.Clear();
            c.cards.taggedBonuses.AddRange(desired);
            c.cardsController.updateMenu();
            Main.Log("Autopilot cards: tags=" + string.Join(",", desired.Select(x => x.ToString()).ToArray()));
        }

        private static void SetManaGenerators(Character c, IEnumerable<cardBonus> priorities)
        {
            var desiredMana = new HashSet<int>();
            var order = priorities.ToArray();
            var target = c.cards.cards
                .Where(card => priorities.Contains(card.bonusType))
                .OrderByDescending(card => CardUtility(card, order))
                .FirstOrDefault();
            if (target != null)
            {
                for (var i = 0; i < target.manaCosts.Count; i++)
                    if (target.manaCosts[i] > c.cards.manas[i].amount) desiredMana.Add(i);
            }
            if (desiredMana.Count == 0)
                foreach (var item in c.cards.manas.Select((mana, index) => new {mana, index}).OrderBy(x => x.mana.amount))
                    desiredMana.Add(item.index);

            var slots = c.cardsController.maxManaGenSize();
            var chosen = desiredMana.Take(slots).ToArray();
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

        private static void ProtectPermanentCards(Character c, bool fullControl)
        {
            for (var i = 0; i < c.cards.cards.Count; i++)
            {
                var card = c.cards.cards[i];
                if (fullControl && card.type != cardType.end && card.cardRarity == rarity.BigChonker)
                {
                    // Before the recycling perk, even an off-plan Chonker banks a
                    // future 25% spawn-timer refund and must not be lost. Afterwards
                    // only a progression-useful Chonker waits for Mayo.
                    var useful = EligibleAtCurrentTier(c, card);
                    var shouldProtect = useful ? !Affordable(c, card) : !HasChonkerRecycling(c);
                    if (card.isProtected != shouldProtect)
                        c.cardsController.protectCard(i);
                }
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
                c.cardsController.tryConsumeCard(i);
                var confirmed = c.cards.cards.Count < before;
                Main.LogAction(confirmed ? "CARD" : "REJECTED",
                    confirmed
                        ? "Consumed End card for its level-100 progression item [confirmed by deck count]"
                        : "End-card consume request produced no deck transition");
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
            var manaBefore = c.cards.manas.Sum(x => x.amount);
            c.cardsController.tryConsumeCard(candidate.index);
            var confirmed = c.cards.cards.Count < countBefore || c.cards.manas.Sum(x => x.amount) < manaBefore;
            Main.LogAction(confirmed ? "CARD" : "REJECTED",
                confirmed
                    ? "Cast " + candidate.card.cardName + " [confirmed by deck/mana state]"
                    : "Card cast request for " + candidate.card.cardName + " produced no state transition");
        }

        private static bool Affordable(Character c, Card card)
        {
            for (var i = 0; i < card.manaCosts.Count; i++)
                if (card.manaCosts[i] > c.cards.manas[i].amount) return false;
            return true;
        }

        private static double CardUtility(Card card, cardBonus[] priorities)
        {
            var index = Array.IndexOf(priorities, card.bonusType);
            if (index < 0)
                return 0;
            var strategicWeight = 1.0 + (priorities.Length - index) * 0.35;
            var mana = Math.Max(1L, card.manaCosts.Sum(x => (long)x));
            // effectAmount already incorporates the quality/rarity roll.
            return card.effectAmount * strategicWeight / mana;
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
                   || card.bonusType == cardBonus.hackSpeed
                   || card.bonusType == cardBonus.wishSpeed
                   || card.bonusType == cardBonus.dropChance;
        }

        private static void YeetWorstCardIfFull(Character c, ICollection<cardBonus> priorities)
        {
            if (c.cards.cards.Count < c.cardsController.maxDeckSize()) return;
            var candidate = c.cards.cards.Select((card, index) => new {card, index})
                .Where(x => !x.card.isProtected && x.card.type != cardType.end
                            && (x.card.cardRarity < rarity.Great
                                || x.card.cardRarity == rarity.BigChonker
                                && HasChonkerRecycling(c) && !EligibleAtCurrentTier(c, x.card)))
                .OrderBy(x => CardUtility(x.card, priorities.ToArray()))
                .FirstOrDefault();
            if (candidate == null) return;
            var countBefore = c.cards.cards.Count;
            c.cardsController.trashCard(candidate.index);
            Main.LogAction(c.cards.cards.Count < countBefore ? "CARD" : "REJECTED",
                c.cards.cards.Count < countBefore
                    ? "Yeeted " + candidate.card.cardName + " [confirmed by deck count]"
                    : "Card yeet request for " + candidate.card.cardName + " produced no deck change");
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
