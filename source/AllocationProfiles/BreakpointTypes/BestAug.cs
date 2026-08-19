using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NGUInjector.Managers;

/*
FILE PURPOSE

BestAug ranks currently unlocked Augment/Upgrade completions by marginal combat multiplier,
finish time, gold availability, and remaining rebirth horizon, then funds a completable track.
Because Augment levels vanish at rebirth, a completion must also leave a real combat-payoff window;
partial or terminal reset-local work must not masquerade as value. Cross-system Energy comparison
belongs to CustomAllocation; this file owns only the internal Augment choice.
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
            var available = MaxAllocation;
            if (available <= 0)
                return;
            var horizon = RemainingRebirthHorizon();
            if (double.IsPositiveInfinity(horizon) && RebirthTime > 0)
                horizon = Math.Max(0.0, RebirthTime - Character.rebirthTime.totalseconds);
            // A reset-local level that completes on the rebirth boundary produces no
            // boss, Adventure, or permanent value. Preserve a minimum observation/
            // combat window, and prefer the current selected-boss ETA when one is
            // already provable from native stats.
            var finiteHorizon = !double.IsPositiveInfinity(horizon);
            if (finiteHorizon && horizon <= 30.0)
            {
                Main.LogAllocation("Held reset-local Augments: <=30s remain before the selected rebirth target");
                return;
            }
            var bossEta = finiteHorizon
                ? Autopilot.AutopilotManager.SelectedBossDefeatEta(Character,
                    Math.Max(0, (int)Math.Floor(horizon)))
                : -1;
            double currentBossKill;
            var bossProgressionBlocked = Character.bossID <= ActiveHighestBoss(Character)
                                         && !CombatHelpers.CanNukeCurrentBoss(Character)
                                         && !CombatHelpers.CanWinCurrentBoss(Character, out currentBossKill);

            for (var i = 0; i < 7; i++)
            {
                var aug = Character.augmentsController.augments[i];
                var state = Character.augments.augs[i];

                if (!aug.augLocked() && !aug.hitAugmentTarget())
                {
                    var time = Math.Max(0.0001, aug.AugTimeLeftEnergy(available));
                    var affordable = aug.AugProgress() > 0f || aug.getAugCost() <= gold;
                    if (affordable && HasPayoffWindow(time, horizon, bossEta))
                    {
                        var level = (double)state.augLevel;
                        var upgrade = (double)state.upgradeLevel;
                        var marginal = aug.baseBoost * (upgrade * upgrade + 1.0)
                                       * (Math.Pow(level + 1.0, aug.augTierBonus())
                                          - Math.Pow(level, aug.augTierBonus()));
                        // AugTimeLeftEnergy already prices the exact remaining progress.  Applying
                        // another arbitrary multiplier to a nonzero bar double-counts sunk work and
                        // can displace a track with greater native marginal gain per remaining second.
                        var score = marginal / time;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestPair = i;
                            bestIsUpgrade = false;
                        }
                    }
                }

                if (!aug.upgradeLocked() && !aug.hitUpgradeTarget() && state.augLevel > 0)
                {
                    var time = Math.Max(0.0001, aug.UpgradeTimeLeftEnergy(available));
                    var affordable = aug.UpgradeProgress() > 0f || aug.getUpgradeCost() <= gold;
                    if (affordable && HasPayoffWindow(time, horizon, bossEta))
                    {
                        var level = (double)state.augLevel;
                        var upgrade = (double)state.upgradeLevel;
                        var marginal = aug.baseBoost * (2.0 * upgrade + 1.0)
                                       * Math.Pow(level, aug.augTierBonus());
                        // UpgradeTimeLeftEnergy already contains the partial-bar advantage exactly.
                        var score = marginal / time;
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
                    Main.LogAllocation("No affordable Augment completion fits the allocation horizon while the selected Fight Boss remains blocked");
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
            if (!HasPayoffWindow(actualTime, horizon, bossEta))
            {
                Main.LogAction("HOLD", "Held " + GameNames.Augment(Character, bestPair, bestIsUpgrade)
                    + ": exact allocated-energy ETA "
                    + Math.Ceiling(actualTime) + "s leaves no proved payoff window inside rebirth horizon "
                    + Math.Ceiling(horizon) + "s");
                return;
            }
            if (!SetInput(allocation))
                return;
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

        private static bool HasPayoffWindow(double completionSeconds, double horizonSeconds, int bossEta)
        {
            if (double.IsNaN(completionSeconds) || double.IsInfinity(completionSeconds)
                || completionSeconds < 0.0)
                return false;
            if (double.IsPositiveInfinity(horizonSeconds))
                return true;
            // If a selected Fight Boss is already forecast, completion must precede
            // it. Otherwise require enough post-completion time for at least one
            // meaningful Adventure/combat cycle instead of valuing the bar itself.
            var payoffBoundary = bossEta >= 0
                ? Math.Min(horizonSeconds - 5.0, bossEta)
                : horizonSeconds - Math.Max(30.0, Math.Min(300.0, completionSeconds));
            return completionSeconds <= payoffBoundary;
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

        internal long CalculateAugCap(int index, long allocation)
        {
            var calcA = CalculateAugCapCalc(500, index, allocation);
            if (calcA.PPT < 1)
            {
                var calcB = CalculateAugCapCalc(calcA.GetOffset(), index, allocation);
                return calcB.Num;
            }

            return calcA.Num;
        }

        internal CapCalc CalculateAugCapCalc(int offset, int index, long allocation)
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
            var num = Autopilot.ExactResourceAllocator.CapAtTickBoundary(num4, allocation,
                Character.idleEnergy);
            var ppt = (double)num / num4;
            ret.Num = num;
            ret.PPT = ppt;
            return ret;
        }
    }
}
