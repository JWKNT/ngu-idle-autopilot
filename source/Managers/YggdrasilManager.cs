using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using static NGUInjector.Main;

/*
FILE PURPOSE

YggdrasilManager manages activation, seed saving/spending, poop, maturity-aware eat versus harvest,
and rebirth-cycle collection through native fruit controllers. Fruit/seed decisions are permanent
economics and are emitted to telemetry. Never harvest blindly at a fixed time or spend seeds that
delay the next high-return permanent tier.
*/
namespace NGUInjector.Managers
{
    internal class YggdrasilManager
    {
        private readonly Character _character;
        internal static string LastSeedDecision { get; private set; } = "Yggdrasil is not yet evaluated";
        internal static string LastFruitDecision { get; private set; } = "Fruit eat/harvest policy is not yet evaluated";

        public YggdrasilManager()
        {
            _character = Main.Character;
        }

        internal static bool AnyHarvestable()
        {
            for (var i = 0; i < Main.Character.yggdrasil.fruits.Count; i++)
            {
                if (Main.Character.yggdrasilController.fruits[0].harvestTier(i) > 0)
                    return true;
            }

            return false;
        }

        internal bool NeedsHarvest()
        {
            var threshold = _character.yggdrasilController.fruits[0].tierThreshold();
            if (_character.yggdrasil.fruits.Any(fruit => fruit.maxTier > 0
                                                         && fruit.seconds >= fruit.maxTier * threshold))
                return true;

            // Fruit timers reset on rebirth.  Consume a non-zero partial tier rather
            // than discard it when the selected checkpoint is imminent.
            var plan = Main.Autopilot == null ? null : Main.Autopilot.Plan;
            var remaining = plan == null || plan.RebirthSeconds < 0
                ? int.MaxValue
                : (int)Math.Floor(plan.EffectiveAllocationTarget(_character)
                                  - _character.rebirthTime.totalseconds);
            return remaining <= 5 && _character.yggdrasil.fruits.Any(fruit =>
                fruit.maxTier > 0 && fruit.seconds >= threshold);
        }

        internal bool NeedsSwap()
        {
            var thresh = Math.Max(1, Settings.YggSwapThreshold);
            for (var i = 0; i < Main.Character.yggdrasil.fruits.Count; i++)
            {
                if (Main.Character.yggdrasilController.fruits[0].harvestTier(i) >= thresh && Main.Character.yggdrasilController.fruits[0].fruitMaxxed(i))
                    return true;
            }

            return false;
        }

        internal void ManageYggHarvest()
        {
            //We need to harvest but we dont have a loadout to manage OR we're not managing loadout
            if (!Settings.SwapYggdrasilLoadouts || Settings.YggdrasilLoadout.Length == 0)
            {
                //Not sure why this would be true, but safety first
                if (LoadoutManager.CurrentLock == LockType.Yggdrasil)
                {
                    LoadoutManager.RestoreGear();
                    LoadoutManager.ReleaseLock();
                }

                if (DiggerManager.CurrentLock == LockType.Yggdrasil)
                {
                    DiggerManager.RestoreDiggers();
                    DiggerManager.ReleaseLock();
                }
                ActuallyHarvest();
                return;
            }

            //We dont need to harvest anymore and we've already swapped, so swap back
            if (!NeedsHarvest() && LoadoutManager.CurrentLock == LockType.Yggdrasil)
            {
                LoadoutManager.RestoreGear();
                LoadoutManager.ReleaseLock();
            }

            if (!NeedsHarvest() && DiggerManager.CurrentLock == LockType.Yggdrasil)
            {
                DiggerManager.RestoreDiggers();
                DiggerManager.ReleaseLock();
            }

            //We're managing loadouts
            if (NeedsHarvest())
            {
                if (NeedsSwap())
                {
                    if (!LoadoutManager.TryYggdrasilSwap())
                        return;
                    if (!DiggerManager.TryYggSwap())
                    {
                        LoadoutManager.RestoreGear();
                        LoadoutManager.ReleaseLock();
                        Main.LogAction("REJECTED", "Yggdrasil digger lock was unavailable; restored the already-swapped gear");
                        return;
                    }

                    Log("Equipping Loadout for Yggdrasil and Harvesting");
                }
                else
                {
                    Log("Harvesting without swap because threshold not met");
                }

                //Harvest stuff
                ActuallyHarvest();
            }
        }

        private void ActuallyHarvest()
        {
            ReadTooltipLog(false);
            var currentPage = _character.yggdrasilController.curPage;
            _character.yggdrasilController.changePage(0);
            _character.yggdrasilController.consumeAll();
            _character.yggdrasilController.changePage(1);
            _character.yggdrasilController.consumeAll();
            _character.yggdrasilController.changePage(2);
            _character.yggdrasilController.consumeAll();
            _character.yggdrasilController.changePage(currentPage);
            _character.yggdrasilController.refreshMenu();
            ReadTooltipLog(true);
        }

        internal static void HarvestAll()
        {
            ReadTooltipLog(false);
            var currentPage = Main.Character.yggdrasilController.curPage;
            Main.Character.yggdrasilController.changePage(0);
            Main.Character.yggdrasilController.consumeAll(true);
            Main.Character.yggdrasilController.changePage(1);
            Main.Character.yggdrasilController.consumeAll(true);
            Main.Character.yggdrasilController.changePage(2);
            Main.Character.yggdrasilController.consumeAll(true);
            Main.Character.yggdrasilController.changePage(currentPage);
            Main.Character.yggdrasilController.refreshMenu();
            ReadTooltipLog(true);
        }

        internal static void ReadTooltipLog(bool doLog)
        {
            var bLog = Main.Character.tooltip.log;
            var type = bLog.GetType().GetField("Eventlog",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            var val = type?.GetValue(bLog);

            if (val != null)
            {
                //Add something to the end of our logs to mark them as complete
                var log = (List<string>)val;
                for (var i = log.Count - 1; i >= 0; i--)
                {
                    var line = log[i];
                    if (line.EndsWith("<b></b>")) continue;
                    if (doLog)
                    {
                        LogPitSpin(line);
                    }
                    log[i] = $"{line}<b></b>";
                }
            }
        }

        internal void CheckFruits()
        {
            var autopilotYgg = Main.Autopilot != null && Main.Autopilot.CanExecuteSafe
                               && Main.Autopilot.Config.ManageYggdrasil;
            if (!Settings.ActivateFruits && !autopilotYgg)
                return;
            if (autopilotYgg)
            {
                ManageSeedUpgrades();
                ConfigureFruitOptions();
            }
            var curPage = _character.yggdrasilController.curPage;
            for (var i = 0; i < _character.yggdrasil.fruits.Count; i++)
            {
                var fruit = _character.yggdrasil.fruits[i];
                //Skip inactive fruits
                if (fruit.maxTier == 0L)
                    continue;

                //Skip fruits that are permed
                if (fruit.permCostPaid)
                    continue;

                if (fruit.activated)
                    continue;

                if (_character.yggdrasilController.usesEnergy[i] &&
                    _character.curEnergy >= _character.yggdrasilController.activationCost[i])
                {
                    if (_character.idleEnergy < _character.yggdrasilController.activationCost[i])
                        _character.removeMostEnergy();
                    if (_character.idleEnergy < _character.yggdrasilController.activationCost[i])
                        continue;
                    var slot = ChangePage(i);
                    _character.yggdrasilController.fruits[slot].activate(i);
                    Main.LogAction(fruit.activated ? "YGG" : "REJECTED",
                        fruit.activated
                            ? "Activated " + GameNames.Fruit(_character, i) + " [confirmed by fruit state]"
                            : GameNames.Fruit(_character, i) + " activation produced no state transition");
                    continue;
                }

                if (!_character.yggdrasilController.usesEnergy[i] &&
                    _character.magic.curMagic >= _character.yggdrasilController.activationCost[i])
                {
                    if (_character.magic.idleMagic < _character.yggdrasilController.activationCost[i])
                        _character.removeMostMagic();
                    if (_character.magic.idleMagic < _character.yggdrasilController.activationCost[i])
                        continue;
                    var slot = ChangePage(i);
                    _character.yggdrasilController.fruits[slot].activate(i);
                    Main.LogAction(fruit.activated ? "YGG" : "REJECTED",
                        fruit.activated
                            ? "Activated " + GameNames.Fruit(_character, i) + " [confirmed by fruit state]"
                            : GameNames.Fruit(_character, i) + " activation produced no state transition");
                }
            }
            _character.yggdrasilController.changePage(curPage);
        }

        private void ManageSeedUpgrades()
        {
            var all = _character.yggdrasilController;
            if (all == null || all.baseSeedCost == null || _character.yggdrasil.seeds <= 0)
                return;
            var cap = all.capTier();
            var count = Math.Min(_character.yggdrasil.fruits.Count, all.baseSeedCost.Count);
            var best = StrategicSeedTarget(count, cap);
            if (best < 0)
            {
                LastSeedDecision = "Every unlocked strategic fruit breakpoint is complete or native-capped";
                return;
            }

            var tier = _character.yggdrasil.fruits[best].maxTier;
            var bestCost = all.baseSeedCost[best] * (tier + 1L) * (tier + 1L);
            // A cheaper side purchase can delay Pomegranate/Fruit of Gold and
            // reduce every later seed harvest.  Preserve the seed balance for the
            // first unmet compounding target instead of greedily buying whatever is
            // affordable this tick.
            if (bestCost <= 0 || bestCost > _character.yggdrasil.seeds)
            {
                LastSeedDecision = "Saving " + _character.yggdrasil.seeds + "/" + bestCost
                                   + " seeds for " + GameNames.Fruit(_character, best) + " tier " + (tier + 1)
                                   + "; cheaper side upgrades would delay the compounding target";
                return;
            }

            var oldPage = all.curPage;
            all.changePage(best / 9);
            var controller = all.fruits[best % 9];
            var seedsBefore = _character.yggdrasil.seeds;
            var tierBefore = _character.yggdrasil.fruits[best].maxTier;
            controller.upgrade();
            all.changePage(oldPage);
            var confirmed = _character.yggdrasil.fruits[best].maxTier == tierBefore + 1
                            && _character.yggdrasil.seeds == seedsBefore - bestCost;
            LastSeedDecision = confirmed
                ? "Bought " + GameNames.Fruit(_character, best) + " tier " + (tierBefore + 1)
                  + " at the strategic seed breakpoint"
                : GameNames.Fruit(_character, best) + " strategic seed purchase was rejected by native state validation";
            Main.LogAction(confirmed ? "YGG" : "REJECTED", confirmed
                ? "Upgraded " + GameNames.Fruit(_character, best) + " tier " + tierBefore + " -> " + (tierBefore + 1)
                  + " for " + bestCost + " seeds [confirmed by tier and seed deltas]"
                : GameNames.Fruit(_character, best) + " seed upgrade produced no verified transition");
        }

        private int StrategicSeedTarget(int count, long cap)
        {
            // These are dependency breakpoints, not clock-based chapter scripts.
            // The early entries build the seed engine; later entries realize the
            // permanent rewards only after that engine has repaid its opportunity
            // cost.  Targets above the native challenge cap are clamped.
            var targets = new List<KeyValuePair<int, int>>
            {
                Pair(0, 2), Pair(1, 1), Pair(2, 1), Pair(4, 1),
                Pair(1, 2), Pair(0, 4), Pair(4, 3), Pair(0, 10),
                Pair(4, 5), Pair(3, 1), Pair(5, 1), Pair(4, 10), Pair(5, 5),
                Pair(0, 24), Pair(4, 24), Pair(3, 24), Pair(5, 24),
                Pair(1, 24), Pair(2, 24), Pair(7, 24), Pair(6, 24),
                Pair(8, 24), Pair(9, 24)
            };
            if (_character.settings.rebirthDifficulty >= difficulty.evil)
            {
                targets.Add(Pair(10, 12));
                targets.Add(Pair(13, 1));
                targets.Add(Pair(14, 4));
                targets.Add(Pair(12, 24));
                targets.Add(Pair(14, 12));
                targets.Add(Pair(10, 24));
                targets.Add(Pair(14, 24));
                targets.Add(Pair(11, 24));
                targets.Add(Pair(13, 24));
            }
            if (_character.settings.rebirthDifficulty >= difficulty.sadistic)
                for (var i = 15; i <= 20; i++) targets.Add(Pair(i, 24));

            foreach (var target in targets)
            {
                if (target.Key < 0 || target.Key >= count || !FruitUnlockEligible(target.Key))
                    continue;
                var desired = Math.Min(cap, target.Value);
                if (_character.yggdrasil.fruits[target.Key].maxTier < desired)
                    return target.Key;
            }

            // Once every strategic breakpoint is complete, retain the old exact
            // marginal seed-return comparison for uncapped future fruit tiers.
            var best = -1;
            var bestScore = 0.0;
            for (var i = 0; i < count; i++)
            {
                var tier = _character.yggdrasil.fruits[i].maxTier;
                if (tier >= cap || !FruitUnlockEligible(i)) continue;
                var cost = _character.yggdrasilController.baseSeedCost[i] * (tier + 1L) * (tier + 1L);
                if (cost <= 0) continue;
                var before = Math.Ceiling(Math.Pow(Math.Max(0L, tier), 1.5));
                var after = Math.Ceiling(Math.Pow(tier + 1.0, 1.5));
                var score = FruitUtility(i) * Math.Max(1.0, after - before) / cost;
                if (score <= bestScore) continue;
                bestScore = score;
                best = i;
            }
            return best;
        }

        private static KeyValuePair<int, int> Pair(int id, int tier)
        {
            return new KeyValuePair<int, int>(id, tier);
        }

        private void ConfigureFruitOptions()
        {
            _character.settings.poopOnlyMaxTier = true;
            var seedEngineComplete = _character.yggdrasil.fruits.Count > 4
                                     && _character.yggdrasil.fruits[0].maxTier >= Math.Min(24, _character.yggdrasilController.capTier())
                                     && _character.yggdrasil.fruits[4].maxTier >= Math.Min(24, _character.yggdrasilController.capTier());
            for (var i = 0; i < _character.yggdrasil.fruits.Count; i++)
            {
                var fruit = _character.yggdrasil.fruits[i];
                if (fruit.maxTier <= 0) continue;
                // Before the seed engine is mature, doubling seeds compounds faster
                // than taking small temporary rewards.  Permanent fruits are eaten
                // once their tier is meaningful; Pomegranate is safe either way.
                var permanentReward = i == 2 || i == 3 || i == 5 || i == 6 || i == 7
                                      || i == 8 || i == 9 || i == 10 || i == 11
                                      || i == 13 || i == 14 || i >= 15;
                fruit.eatFruit = i == 4 || seedEngineComplete
                                 || permanentReward && fruit.maxTier >= 12;
            }

            var bestPoop = SelectPoopTarget(seedEngineComplete);
            for (var i = 0; i < _character.yggdrasil.fruits.Count; i++)
                _character.yggdrasil.fruits[i].usePoop = i == bestPoop;
            LastFruitDecision = seedEngineComplete
                ? "Seed engine mature: eat permanent fruit rewards; poop target "
                  + (bestPoop < 0 ? "reserved" : bestPoop.ToString())
                : "Seed engine building: harvest low-tier temporary fruits for double seeds; "
                  + (bestPoop < 0 ? "reserve poop" : "poop " + GameNames.Fruit(_character, bestPoop));
        }

        private int SelectPoopTarget(bool seedEngineComplete)
        {
            if (_character.arbitrary.poop1Count <= 0)
                return -1;
            // Early poop on a mature Pomegranate accelerates the compounding seed
            // engine.  Afterwards reserve it for the strongest mature permanent
            // fruits instead of spending it on the first ready fruit in page order.
            if (!seedEngineComplete && _character.yggdrasil.fruits.Count > 4
                && _character.yggdrasil.fruits[4].maxTier >= 6
                && _character.yggdrasilController.fruits[0].harvestTier(4) >= _character.yggdrasil.fruits[4].maxTier)
                return 4;
            var order = _character.settings.rebirthDifficulty >= difficulty.evil
                ? new[] {12, 3, 5, 7, 6, 8, 9, 14, 11, 10, 13, 2}
                : new[] {3, 5, 7, 6, 8, 9, 2};
            foreach (var id in order)
            {
                if (id < 0 || id >= _character.yggdrasil.fruits.Count) continue;
                var fruit = _character.yggdrasil.fruits[id];
                if (fruit.maxTier > 0
                    && _character.yggdrasilController.fruits[0].harvestTier(id) >= fruit.maxTier)
                    return id;
            }
            return -1;
        }

        private bool FruitUnlockEligible(int id)
        {
            if (id == 8 && _character.yggdrasil.fruits[id].maxTier == 0)
                return _character.allChallenges.trollChallenge.completions() >= 5;
            if (id == 9) return _character.settings.itopodOn;
            if (id == 10) return _character.achievements.achievementComplete.Count > 145
                                 && _character.achievements.achievementComplete[145];
            if (id == 14) return _character.settings.beastOn;
            if (id >= 15 && id <= 20) return _character.cards.cardsOn;
            return true;
        }

        private static double FruitUtility(int id)
        {
            switch (id)
            {
                case 7: return 10.0; // AP income
                case 3: return 9.0;  // EXP
                case 6:
                case 8:
                case 11: return 8.0; // permanent multipliers
                case 2: return 7.5;  // permanent Adventure base stats
                case 4: return 7.0;  // seed engine
                case 5: return 6.0;  // permanent drop chance
                case 9:
                case 14: return 5.5; // PP/QP
                case 10:
                case 13: return 5.0; // MacGuffins
                case 1: return 4.0;
                case 12: return 3.5;
                case 0: return 2.5;
                default: return id >= 15 && id <= 20 ? 3.0 : 1.0;
            }
        }

        private int ChangePage(int slot)
        {
            var page = (int)Math.Floor((double)slot / 9);
            _character.yggdrasilController.changePage(page);
            return slot - (page * 9);
        }
    }
}
