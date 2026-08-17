using System;
using System.Collections.Generic;
using System.Linq;

/*
FILE PURPOSE

DiggerManager selects, upgrades, and temporarily locks Gold Diggers for progression, Titan gold,
and Money Pit contexts. It coordinates with LoadoutManager so transient objectives take precedence
over generic optimization. Gold reserves and rebirth-local value are supplied by higher policy.
*/
namespace NGUInjector.Managers
{
    internal static class DiggerManager
    {
        private static int[] _savedDiggers;
        private static int[] _tempDiggers;
        private static int _cheapestDigger;

        internal static LockType CurrentLock { get; set; }
        private static readonly int[] TitanDiggers = { 0, 3, 8, 11 };
        private static readonly int[] YggDiggers = {8, 11};

        internal static bool CanSwap()
        {
            return CurrentLock == LockType.None;
        }

        internal static void TryTitanSwap()
        {
            if (!CanAcquireOrHasLock(LockType.Titan))
                return;

            var ts = ZoneHelpers.TitansSpawningSoon();
            if (CurrentLock == LockType.Titan)
            {
                if (ts.SpawningSoon)
                    return;

                RestoreDiggers();
                ReleaseLock();
                return;
            }

            if (ts.SpawningSoon)
            {
                CurrentLock = LockType.Titan;
                SaveDiggers();
                EquipDiggers(TitanDiggers);
            }
        }

        internal static bool TryYggSwap()
        {
            if (!CanAcquireOrHasLock(LockType.Yggdrasil)) 
                return false;

            if (CurrentLock == LockType.Yggdrasil)
                return true;

            CurrentLock = LockType.Yggdrasil;
            SaveDiggers();
            EquipDiggers(YggDiggers);
            var expected = YggDiggers.Where(x => x >= 0 && x < Main.Character.diggers.diggers.Count)
                .Where(x => Main.Character.diggers.diggers[x].maxLevel > 0)
                .Take(Main.Character.allDiggers.maxDiggerSlots()).ToArray();
            var verified = expected.All(x => Main.Character.diggers.diggers[x].active)
                           && Main.Character.diggers.activeDiggers.All(expected.Contains);
            if (verified) return true;
            RestoreDiggers();
            ReleaseLock();
            Main.LogAction("REJECTED", "Yggdrasil digger swap did not match the requested active set; restored prior diggers");
            return false;
        }

        internal static void ReleaseLock()
        {
            CurrentLock = LockType.None;
        }

        internal static void SaveDiggers()
        {
            var temp = new List<int>();
            for (var i = 0; i < Main.Character.diggers.diggers.Count; i++)
            {
                if (Main.Character.diggers.diggers[i].active)
                {
                    temp.Add(i);
                }
            }

            _savedDiggers = temp.ToArray();
        }

        internal static void SaveTempDiggers()
        {
            var temp = new List<int>();
            for (var i = 0; i < Main.Character.diggers.diggers.Count; i++)
            {
                if (Main.Character.diggers.diggers[i].active)
                {
                    temp.Add(i);
                }
            }

            _tempDiggers = temp.ToArray();
        }

        internal static void RestoreTempDiggers()
        {
            EquipDiggers(_tempDiggers);
        }

        internal static void EquipDiggers(int[] diggers)
        {
            Main.Log($"Equipping Diggers: {string.Join(",", diggers.Select(x => x.ToString()).ToArray())}");
            Main.Character.allDiggers.clearAllActiveDiggers();
            var sorted = diggers.Where(x => x >= 0 && x < Main.Character.diggers.diggers.Count)
                .Distinct()
                .Where(x => Main.Character.diggers.diggers[x].maxLevel > 0)
                .Take(Main.Character.allDiggers.maxDiggerSlots())
                .ToArray();
            var gross = Main.Character.grossGoldPerSecond();
            var budgetPerDigger = sorted.Length > 0 ? gross / sorted.Length : 0.0;
            for (var i = 0; i < sorted.Length; i++)
            {
                SetLevelMaxAffordable(sorted[i], budgetPerDigger);
            }
            UpdateCheapestDigger();
        }

        private static bool CanAcquireOrHasLock(LockType requestor)
        {
            if (CurrentLock == requestor)
            {
                return true;
            }

            if (CurrentLock == LockType.None)
            {
                return true;
            }

            return false;
        }

        internal static void RecapDiggers()
        {
            if (CurrentLock != LockType.None)
                return;
            var gross = Main.Character.grossGoldPerSecond();
            var activeCount = Main.Character.diggers.diggers.Count(x => x.active);
            var budgetPerDigger = activeCount > 0 ? gross / activeCount : 0.0;
            for (var i = Main.Character.diggers.diggers.Count-1; i >= 0 ; i--)
            {
                if (Main.Character.diggers.diggers[i].active)
                {
                    SetLevelMaxAffordable(i, budgetPerDigger);
                }
            }
            UpgradeCheapestDigger();
            Main.Character.allDiggers.refreshMenu();
        }

        private static void SetLevelMaxAffordable(int id, double cap)
        {
            if (id < 0 || id >= Main.Character.diggers.diggers.Count)
                return;
            var digger = Main.Character.diggers.diggers[id];
            var curLevel = digger.curLevel;
            var wasActive = digger.active;
            var activeSnapshot = Main.Character.diggers.activeDiggers.ToList();
            try
            {
                digger.curLevel = 0L;
                if (Main.Character.goldPerSecond() < Main.Character.allDiggers.drain(id, 1, true))
                {
                    RestoreDiggerState(id, curLevel, wasActive, activeSnapshot);
                    return;
                }
                var num1 = cap;
                var num2 = Main.Character.allDiggers.baseGPSDrain[id];
                var a = Main.Character.allDiggers.gpsGrowthRate[id];
                var num3 = num2 <= 0
                    ? Main.Character.diggers.diggers[id].maxLevel
                    : a <= 1.0
                        ? (num1 >= num2 ? Main.Character.diggers.diggers[id].maxLevel : 0L)
                        : Math.Min((long)Math.Floor(Math.Log(num1 / num2) / Math.Log(a)),
                            Main.Character.diggers.diggers[id].maxLevel) + 1L;
                if (num3 < 0L)
                    num3 = 0L;
                if (num3 > Main.Character.diggers.diggers[id].maxLevel)
                    num3 = Main.Character.diggers.diggers[id].maxLevel;
                digger.curLevel = num3;
                if (digger.curLevel == 0L && digger.active)
                {
                    digger.active = false;
                    var activeIndex = Main.Character.diggers.activeDiggers.IndexOf(id);
                    if (activeIndex >= 0)
                        Main.Character.diggers.activeDiggers.RemoveAt(activeIndex);
                }
                if (Main.Character.grossGoldPerSecond() < Main.Character.allDiggers.totalGPSDrain())
                {
                    RestoreDiggerState(id, curLevel, wasActive, activeSnapshot);
                }
                else if (!digger.active && digger.curLevel > 0L && Main.Character.diggers.activeDiggers.Count < Main.Character.allDiggers.maxDiggerSlots())
                    Main.Character.allDiggers.activateDigger(id);
            }
            catch
            {
                RestoreDiggerState(id, curLevel, wasActive, activeSnapshot);
                throw;
            }
        }

        private static void RestoreDiggerState(int id, long level, bool active, List<int> activeSnapshot)
        {
            var digger = Main.Character.diggers.diggers[id];
            digger.curLevel = level;
            digger.active = active;
            Main.Character.diggers.activeDiggers.Clear();
            Main.Character.diggers.activeDiggers.AddRange(activeSnapshot);
        }

        internal static void RestoreDiggers()
        {
            if (_savedDiggers == null)
                return;
            Main.Character.allDiggers.clearAllActiveDiggers();
            EquipDiggers(_savedDiggers);
        }

        internal static void UpdateCheapestDigger()
        {
            if (!Main.Settings.UpgradeDiggers && !Main.AutopilotWants(x => x.ManageDiggers)) return;
            _cheapestDigger = -1;
            for (var i = 0; i < Main.Character.diggers.diggers.Count; i++)
            {
                if (Main.Character.diggers.diggers[i].maxLevel >= Main.Character.allDiggers.hardCapLevel(i))
                    continue;
                if (_cheapestDigger == -1)
                {
                    _cheapestDigger = i;
                }
                if (Main.Character.allDiggers.upgradeCost(i) < Main.Character.allDiggers.upgradeCost(_cheapestDigger))
                {
                    _cheapestDigger = i;
                }
            }
        }

        internal static void UpgradeCheapestDigger()
        {
            if (CurrentLock != LockType.None) return;
            if (!Main.Settings.UpgradeDiggers && !Main.AutopilotWants(x => x.ManageDiggers)) return;
            var reserve = Main.Settings.MoneyPitThreshold;
            if (Main.AutopilotWants(x => x.ManageDiggers))
            {
                // A static Money Pit reserve starves diggers for the entire cooldown.
                // Reserve the toss threshold only when the Pit can actually consume it;
                // otherwise protect the next charged Augment level as working capital.
                reserve = RequiredAugmentWorkingCapital();
                if (Main.Character.settings.pitUnlocked
                    && Main.Character.pit.pitTime.totalseconds >= Main.Character.pitController.currentPitTime()
                    && Main.Character.pitController.canToss())
                    reserve = Math.Max(reserve, Main.Autopilot.Config.MoneyPitReserve);
            }
            for (var purchases = 0; purchases < 100; purchases++)
            {
                UpdateCheapestDigger();
                if (_cheapestDigger == -1)
                    return;
                var cost = Main.Character.allDiggers.upgradeCost(_cheapestDigger);
                if (cost <= 0 || cost + reserve >= Main.Character.realGold)
                    return;
                var levelBefore = Main.Character.diggers.diggers[_cheapestDigger].maxLevel;
                var goldBefore = Main.Character.realGold;
                Main.Character.allDiggers.upgradeMaxLevel(_cheapestDigger);
                var confirmed = Main.Character.diggers.diggers[_cheapestDigger].maxLevel > levelBefore
                                && Main.Character.realGold < goldBefore;
                Main.LogAction(confirmed ? "PURCHASE" : "REJECTED",
                    confirmed
                        ? "Upgraded " + GameNames.Digger(Main.Character, _cheapestDigger)
                          + " max level [confirmed by level/gold delta]"
                        : GameNames.Digger(Main.Character, _cheapestDigger)
                          + " upgrade produced no state transition");
                if (!confirmed)
                    return;
            }
        }

        private static double RequiredAugmentWorkingCapital()
        {
            var c = Main.Character;
            if (c.augments == null || c.augmentsController == null)
                return 0;
            var reserve = 0.0;
            for (var i = 0; i < c.augments.augs.Length && i < c.augmentsController.augments.Length; i++)
            {
                var state = c.augments.augs[i];
                var controller = c.augmentsController.augments[i];
                if (state.augEnergy > 0 && state.augProgress <= 0)
                    reserve += controller.getAugCost();
                if (state.upgradeEnergy > 0 && state.upgradeProgress <= 0)
                    reserve += controller.getUpgradeCost();
            }
            if (c.settings.pitUnlocked && c.machine != null && c.timeMachineController != null)
            {
                if (c.machine.speedEnergy > 0 && c.machine.speedProgress <= 0)
                    reserve += c.timeMachineController.machineSpeedGoldCost();
                if (c.machine.goldMultiMagic > 0 && c.machine.goldMultiProgress <= 0)
                    reserve += c.timeMachineController.machineGoldMultiCost();
            }
            return reserve;
        }
    }
}
