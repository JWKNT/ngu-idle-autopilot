using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NGUInjector.Managers;

/*
FILE PURPOSE

BestAug ranks currently unlocked Augment/Upgrade completions by marginal combat multiplier,
finish time, gold availability, and remaining rebirth horizon, then funds a completable track.
Partial reset-local work must not masquerade as value. Cross-system Energy comparison belongs to
CustomAllocation; this file owns only the internal Augment choice.
*/
namespace NGUInjector.AllocationProfiles.BreakpointTypes
{
    internal class BestAug : BaseBreakpoint
    {
        internal int RebirthTime { get; set; }

        protected override bool Unlocked()
        {
            return Character.buttons.augmentation.interactable && !Character.challenges.noAugsChallenge.inChallenge;
        }

        protected override bool TargetMet()
        {
            return false;
        }

        internal override bool Allocate()
        {
            AllocatePairs();
            return true;
        }

        private void AllocatePairs()
        {
            var gold = Character.realGold;
            var bestPair = -1;
            var bestIsUpgrade = false;
            var bestScore = 0d;
            var available = (long)MaxAllocation;
            if (available <= 0)
                return;
            var horizon = RebirthTime > 0
                ? Math.Max(0.0, RebirthTime - Character.rebirthTime.totalseconds)
                : double.PositiveInfinity;
            double currentBossKill;
            var bossProgressionBlocked = Character.bossID <= ActiveHighestBoss(Character)
                                         && !CombatHelpers.CanNukeCurrentBoss(Character)
                                         && !CombatHelpers.CanWinCurrentBoss(Character, out currentBossKill);

            for (var i = 0; i < 7; i++)
            {
                var aug = Character.augmentsController.augments[i];
                var state = Character.augments.augs[i];

                if (!aug.augLocked() && !aug.hitAugmentTarget()
                    && (!bossProgressionBlocked || state.augProgress > 0f))
                {
                    var time = Math.Max(0.0001, aug.AugTimeLeftEnergy(available));
                    var affordable = aug.AugProgress() > 0f || aug.getAugCost() <= gold;
                    if (affordable && time <= horizon)
                    {
                        var level = (double)state.augLevel;
                        var upgrade = (double)state.upgradeLevel;
                        var marginal = aug.baseBoost * (upgrade * upgrade + 1.0)
                                       * (Math.Pow(level + 1.0, aug.augTierBonus())
                                          - Math.Pow(level, aug.augTierBonus()));
                        // Completing already-paid progress dominates starting another
                        // partial level that will be erased at rebirth.
                        var score = marginal / time * (state.augProgress > 0 ? 4.0 : 1.0);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestPair = i;
                            bestIsUpgrade = false;
                        }
                    }
                }

                if (!aug.upgradeLocked() && !aug.hitUpgradeTarget() && state.augLevel > 0
                    && (!bossProgressionBlocked || state.upgradeProgress > 0f))
                {
                    var time = Math.Max(0.0001, aug.UpgradeTimeLeftEnergy(available));
                    var affordable = aug.UpgradeProgress() > 0f || aug.getUpgradeCost() <= gold;
                    if (affordable && time <= horizon)
                    {
                        var level = (double)state.augLevel;
                        var upgrade = (double)state.upgradeLevel;
                        var marginal = aug.baseBoost * (2.0 * upgrade + 1.0)
                                       * Math.Pow(level, aug.augTierBonus());
                        var score = marginal / time * (state.upgradeProgress > 0 ? 4.0 : 1.0);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestPair = i;
                            bestIsUpgrade = true;
                        }
                    }
                }
            }

            if (bestPair < 0)
            {
                if (bossProgressionBlocked)
                    Main.LogAllocation("Held new Augment starts because the selected Fight Boss is not yet viable; residual Energy returns to Basic Training");
                return;
            }
            var selected = Character.augmentsController.augments[bestPair];
            var selectedIndex = bestPair * 2 + (bestIsUpgrade ? 1 : 0);
            var allocation = CalculateAugCap(selectedIndex, MaxAllocation);
            if (allocation <= 0)
                return;
            var actualTime = bestIsUpgrade
                ? selected.UpgradeTimeLeftEnergy((long)allocation)
                : selected.AugTimeLeftEnergy((long)allocation);
            if (actualTime > horizon)
            {
                Main.LogAction("HOLD", "Held " + GameNames.Augment(Character, bestPair, bestIsUpgrade)
                    + ": exact allocated-energy ETA "
                    + Math.Ceiling(actualTime) + "s exceeds rebirth horizon " + Math.Ceiling(horizon) + "s");
                return;
            }
            SetInput(allocation);
            if (bestIsUpgrade)
            {
                selected.addEnergyUpgrade();
            }
            else
            {
                selected.addEnergyAug();
            }
            Main.LogAllocation("BestAug exact marginal ROI: "
                               + GameNames.Augment(Character, bestPair, bestIsUpgrade)
                               + ", score " + bestScore);
        }

        private static int ActiveHighestBoss(Character c)
        {
            return c.settings.rebirthDifficulty == difficulty.normal ? c.highestBoss
                : c.settings.rebirthDifficulty == difficulty.evil ? c.highestHardBoss
                : c.highestSadisticBoss;
        }

        protected override bool CorrectResourceType()
        {
            return Type == ResourceType.Energy;
        }

        internal float CalculateAugCap(int index, float allocation)
        {
            var calcA = CalculateAugCapCalc(500, index, allocation);
            if (calcA.PPT < 1)
            {
                var calcB = CalculateAugCapCalc(calcA.GetOffset(), index, allocation);
                return calcB.Num;
            }

            return calcA.Num;
        }

        internal CapCalc CalculateAugCapCalc(int offset, int index, float allocation)
        {
            int augIndex;
            var ret = new CapCalc
            {
                Num = 0,
                PPT = 1
            };
            double formula = 0;
            if (index % 2 == 0)
            {
                augIndex = index / 2;
                formula = 50000 * (1f + Character.augments.augs[augIndex].augLevel + offset) /
                    (Character.totalEnergyPower() *
                    (1 + Character.inventoryController.bonuses[specType.Augs]) *
                    Character.inventory.macguffinBonuses[12] *
                    Character.hacksController.totalAugSpeedBonus() *
                    Character.cardsController.getBonus(cardBonus.augSpeed) *
                    Character.adventureController.itopod.totalAugSpeedBonus() *
                    (1.0 + Character.allChallenges.noAugsChallenge.evilCompletions() * 0.0500000007450581));

                if (Character.allChallenges.noAugsChallenge.completions() >= 1)
                {
                    formula /= 1.10000002384186;
                }
                if (Character.allChallenges.noAugsChallenge.evilCompletions() >= Character.allChallenges.noAugsChallenge.maxCompletions)
                {
                    formula /= 1.25;
                }
                if (Character.settings.rebirthDifficulty >= difficulty.sadistic)
                {
                    formula *= Character.augmentsController.augments[augIndex].sadisticDivider();
                }
                if (Character.settings.rebirthDifficulty == difficulty.normal)
                {
                    formula *= Character.augmentsController.normalAugSpeedDividers[augIndex];
                }
                else if (Character.settings.rebirthDifficulty == difficulty.evil)
                {
                    formula *= Character.augmentsController.evilAugSpeedDividers[augIndex];
                }
                else if (Character.settings.rebirthDifficulty == difficulty.sadistic)
                {
                    formula *= Character.augmentsController.sadisticAugSpeedDividers[augIndex];
                }
            }
            else
            {
                augIndex = (index - 1) / 2;
                formula = 50000 * (1f + Character.augments.augs[augIndex].upgradeLevel + offset) /
                    (Character.totalEnergyPower() *
                    (1 + Character.inventoryController.bonuses[specType.Augs]) *
                    Character.inventory.macguffinBonuses[12] *
                    Character.hacksController.totalAugSpeedBonus() *
                    Character.cardsController.getBonus(cardBonus.augSpeed) *
                    Character.adventureController.itopod.totalAugSpeedBonus() *
                    (1.0 + Character.allChallenges.noAugsChallenge.evilCompletions() * 0.0500000007450581));

                if (Character.allChallenges.noAugsChallenge.completions() >= 1)
                {
                    formula /= 1.10000002384186;
                }
                if (Character.allChallenges.noAugsChallenge.evilCompletions() >= Character.allChallenges.noAugsChallenge.maxCompletions)
                {
                    formula /= 1.25;
                }
                if (Character.settings.rebirthDifficulty >= difficulty.sadistic)
                {
                    formula *= Character.augmentsController.augments[augIndex].sadisticDivider();
                }
                if (Character.settings.rebirthDifficulty == difficulty.normal)
                {
                    formula *= Character.augmentsController.normalUpgradeSpeedDividers[augIndex];

                }
                else if (Character.settings.rebirthDifficulty == difficulty.evil)
                {
                    formula *= Character.augmentsController.evilUpgradeSpeedDividers[augIndex];

                }
                else if (Character.settings.rebirthDifficulty == difficulty.sadistic)
                {
                    formula *= Character.augmentsController.sadisticUpgradeSpeedDividers[augIndex];
                }
            }
            if (formula >= Character.hardCap())
                formula = Character.hardCap();
            var num4 = formula <= 1.0 ? 1L : (long)formula;
            var num = (long)(num4 / (long)Math.Ceiling(num4 / (double)allocation) * 1.00000202655792);
            if (num + 1L <= long.MaxValue)
                ++num;
            if (num > Character.idleEnergy)
                num = Character.idleEnergy;
            if (num < 0L)
                num = 0L;
            var ppt = (double)num / num4;
            ret.Num = num;
            ret.PPT = ppt;
            return ret;
        }
    }
}
