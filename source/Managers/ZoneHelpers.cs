using System;
using System.Linq;
using System.Reflection;

/*
FILE PURPOSE

ZoneHelpers owns native Adventure unlock/reachability checks and Titan spawn/combat gates. It
mirrors puzzle-item and version-specific invincibility requirements before declaring a Titan
available, and exposes a separately labeled recoverable first-kill gate for major mechanic unlocks.
It supplies facts to routing/loadouts and never chooses ordinary collection policy.
*/
namespace NGUInjector.Managers
{
    static class ZoneHelpers
    {
        internal static readonly int[] TitanZones = { 6, 8, 11, 14, 16, 19, 23, 26, 30, 34, 38, 42 };

        internal static bool ZoneIsTitan(int zone)
        {
            return TitanZones.Contains(zone);
        }

        internal static TitanSpawn TitansSpawningSoon()
        {
            var result = new TitanSpawn();

            if (!Main.Character.buttons.adventure.IsInteractable())
                return result;
            // Adventure routing chooses the highest ready Titan; aggregate loadout
            // intent in the same order so a simultaneous lower timer cannot select
            // the wrong money target.
            for (var i = TitanZones.Length - 1; i >= 0; i--)
            {
                result.Merge(GetTitanSpawn(i));
            }
            return result;
        }

        internal static bool TitanSpawningSoon(int boss)
        {
            return Main.Character.buttons.adventure.IsInteractable() && CheckTitanSpawnTime(boss);
        }

        internal static int HighestAvailableTitan()
        {
            if (!Main.Character.buttons.adventure.IsInteractable())
                return -1;
            var reachable = GetMaxReachableZone(true);
            for (var bossId = TitanZones.Length - 1; bossId >= 0; bossId--)
            {
                if (TitanZones[bossId] <= reachable && TitanUnlockedForAttempt(bossId)
                                                        && CheckTitanSpawnTime(bossId)
                                                        && TitanCombatReady(bossId))
                    return TitanZones[bossId];
            }
            return -1;
        }

        /*
        MAJOR-UNLOCK TITAN PUSH

        The normal selector requires a comfortable <=120 second, <90% HP projection because it is
        suitable for repeated unattended Titan farming. The first four Titans unlock entire game
        systems. For those one-time gates, admit a slower recoverable attempt when regular attacks
        still deal positive damage and the first conservative enemy hit is not lethal. Native
        combat, spawn, puzzle, and item requirements remain authoritative.
        */
        internal static int HighestMajorUnlockTitan(out int titanIndex)
        {
            titanIndex = -1;
            var c = Main.Character;
            if (c == null || !c.buttons.adventure.IsInteractable()) return -1;
            var unlocked = new[]
            {
                c.settings.nguOn,
                c.settings.yggdrasilOn,
                c.settings.diggersOn,
                c.settings.beardsOn
            };
            var reachable = GetMaxReachableZone(true);
            for (var id = Math.Min(3, TitanZones.Length - 1); id >= 0; id--)
            {
                if (unlocked[id] || TitanZones[id] > reachable
                    || !TitanUnlockedForAttempt(id) || !CheckTitanSpawnTime(id)
                    || !TitanUnlockAttemptable(id))
                    continue;
                titanIndex = id;
                return TitanZones[id];
            }
            return -1;
        }

        private static bool TitanUnlockAttemptable(int bossId)
        {
            var c = Main.Character;
            var zone = TitanZones[bossId];
            if (c.adventureController.enemyList == null
                || zone < 0 || zone >= c.adventureController.enemyList.Count
                || c.adventureController.enemyList[zone] == null
                || c.adventureController.enemyList[zone].Count == 0)
                return false;
            var enemy = c.adventureController.enemyList[zone][0];
            var outgoing = .8 * Math.Max(0.0, c.totalAdvAttack() - enemy.defense / 2.0)
                           * c.regAttackPower();
            var conservativeHit = 1.2 * Math.Max(enemy.attack * .1,
                enemy.attack - c.totalAdvDefense() / 2.0);
            return outgoing > 0 && conservativeHit < c.totalAdvHP() * .95;
        }

        private static bool TitanUnlockedForAttempt(int bossId)
        {
            var c = Main.Character;
            // The Ring of Apathy gates UUG in zone 14 (Titan index 3). The previous
            // index incorrectly blocked Jake in zone 11.
            if (bossId == 3 && (c.inventory.itemList.itemMaxxed.Count <= 135
                                || !c.inventory.itemList.itemMaxxed[135]))
                return false;
            // UUG's native AI reads the equipped Ring of Apathy level, not merely
            // the persistent "ever MAXXED" flag.  Without level 100 equipped it can
            // become effectively unwinnable while the generic stat model says yes.
            if (bossId == 3 && (c.inventoryController == null || c.inventoryController.apathyCheck() < 100))
                return false;

            // IT HUNGERS sets itself invincible when checkAndUseGlop cannot consume
            // item 372.  Never enter and burn a spawn without the actual inventory
            // consumable available.
            if (bossId == 9 && !c.inventory.inventory.Any(x => x != null && x.id == 372))
                return false;

            // Titans 6+ have explicit quest/unlock flags. Reflecting the source field keeps
            // this gate valid across the later Titans without treating a ready clock as proof.
            if (bossId < 5)
                return true;
            var field = c.adventure.GetType().GetField("titan" + (bossId + 1) + "Unlocked",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null)
                return true;
            var value = field.GetValue(c.adventure);
            return value is bool && (bool)value;
        }

        private static bool TitanCombatReady(int bossId)
        {
            var c = Main.Character;
            var zone = TitanZones[bossId];
            if (c.adventureController.enemyList == null
                || zone < 0 || zone >= c.adventureController.enemyList.Count
                || c.adventureController.enemyList[zone] == null
                || c.adventureController.enemyList[zone].Count == 0)
                return false;

            var enemies = c.adventureController.enemyList[zone];
            var enemyIndex = 0;
            if (bossId >= 5)
            {
                var versionField = c.adventure.GetType().GetField("titan" + (bossId + 1) + "Version",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var version = versionField == null ? 0 : (int)versionField.GetValue(c.adventure);
                enemyIndex = Math.Max(0, Math.Min(enemies.Count - 1, version + 1));

                // AMALGAMATE v4 (enemy index/version 4) reuses the native Apathy
                // check and can enter an invincible/growth branch when the ring is
                // absent. Earlier versions do not have that hard gate.
                if (bossId == 11 && version >= 3
                    && (c.inventoryController == null || c.inventoryController.apathyCheck() < 100))
                    return false;
            }
            var enemy = enemies[enemyIndex];
            var outgoing = .8 * Math.Max(0.0, c.totalAdvAttack() - enemy.defense / 2.0)
                           * c.regAttackPower();
            if (outgoing <= 0)
                return false;
            var killSeconds = Math.Ceiling(enemy.maxHP / outgoing);
            var firstAttack = 1.5 * enemy.attackRate;
            var enemyAttacks = killSeconds < firstAttack
                ? 0
                : 1 + (int)Math.Floor((killSeconds - firstAttack) / Math.Max(.1, enemy.attackRate));
            var incoming = 1.2 * Math.Max(enemy.attack * .1, enemy.attack - c.totalAdvDefense() / 2.0);
            var projectedDamage = enemyAttacks * incoming;
            return killSeconds <= 120 && projectedDamage < c.totalAdvHP() * .90;
        }

        private static TitanSpawn GetTitanSpawn(int bossId)
        {
            var result = new TitanSpawn();

            if (TitanZones[bossId] > GetMaxReachableZone(true))
                return result;

            // Loadout intent must be allowed before the combat check: the configured
            // Titan set itself may be what makes a borderline spawn winnable.
            if (!TitanUnlockedForAttempt(bossId))
                return result;
            if (!CheckTitanSpawnTime(bossId)) return result;

            // Run money once for each boss
            result.RunMoneyLoadout = Main.Settings.ManageGoldLoadouts && Main.Settings.TitanGoldTargets[bossId] && !Main.Settings.TitanMoneyDone[bossId];
            result.MoneyTarget = result.RunMoneyLoadout ? bossId : -1;
            result.SpawningSoon = Main.Settings.SwapTitanLoadouts && Main.Settings.TitanSwapTargets[bossId]
                                  || result.RunMoneyLoadout;

            return result;
        }

        private static bool CheckTitanSpawnTime(int bossId)
        {
            if (Main.Test) return true;
            var controller = Main.Character.adventureController;
            var adventure = Main.Character.adventure;

            var spawnMethod = controller.GetType().GetMethod($"boss{bossId + 1}SpawnTime",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var spawnTimeObj = spawnMethod?.Invoke(controller, null);
            if (spawnTimeObj == null)
                return false;
            var spawnTime = (float) spawnTimeObj;

            var spawnField = adventure.GetType().GetField($"boss{bossId + 1}Spawn",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var spawnObj = spawnField?.GetValue(adventure);

            if (spawnObj == null)
                return false;

            var spawn = (PlayerTime) spawnObj;
            return spawn.totalseconds >= spawnTime;
        }

        internal static int GetMaxReachableZone(bool includingTitans)
        {
            var options = Main.Character.adventureController.zoneDropdown.options;
            var highestOption = options.Count - 1;
            if (highestOption >= 0)
            {
                var finalLabel = options[highestOption].text ?? string.Empty;
                if (finalLabel.IndexOf("ITOPOD", StringComparison.OrdinalIgnoreCase) >= 0
                    || finalLabel.IndexOf("INFINITE TOWER", StringComparison.OrdinalIgnoreCase) >= 0)
                    highestOption--;
            }

            // Dropdown option 0 is Safe Zone (-1); every ordinary/titan option maps to
            // actual zone ID = option index - 1.
            for (var i = highestOption - 1; i >= 0; i--)
            {
                if (!ZoneIsTitan(i))
                    return i;
                if (includingTitans)
                    return i;
            }
            return 0;
        }

        internal static void OptimizeITOPOD()
        {
            if (!Main.Settings.OptimizeITOPODFloor) return;
            if (Main.Character.arbitrary.boughtLazyITOPOD && Main.Character.arbitrary.lazyITOPODOn) return;
            if (Main.Character.adventure.zone < 1000) return;
            var controller = Main.Character.adventureController;
            var level = controller.itopodLevel;
            var optimal = Math.Max(0, Math.Min(Main.Character.calculateBestItopodLevel(),
                Main.Character.adventure.highestItopodLevel - 1));
            if (level == optimal) return; // we are on optimal floor
            var highestOpen = Main.Character.adventure.highestItopodLevel;
            var climbing = (level < optimal && level >= highestOpen - 1);
            controller.itopodStartInput.text = optimal.ToString();
            if (climbing)
                optimal++;
            controller.itopodEndInput.text = optimal.ToString();
            controller.verifyItopodInputs();
            if (!climbing)
                controller.zoneSelector.changeZone(1000);
        }

        internal static int CalculateBestItopodLevel()
        {
            var c = Main.Character;
            var num1 = c.totalAdvAttack() / 765f * (Main.Settings.ITOPODCombatMode == 1 || c.training.attackTraining[1] == 0 ? c.idleAttackPower() : c.regAttackPower());
            if (c.totalAdvAttack() < 700.0)
                return 0;
            var num2 = Convert.ToInt32(Math.Floor(Math.Log(num1, 1.05)));
            if (num2 < 1)
                return 1;
            var maxLevel = c.adventureController.maxItopodLevel();
            if (num2 > maxLevel)
                num2 = maxLevel;
            return num2;
        }
    }

    public class TitanSpawn
    {
        internal bool SpawningSoon { get; set; }
        internal bool RunMoneyLoadout { get; set; }
        internal int MoneyTarget { get; set; } = -1;

        internal void Merge(TitanSpawn other)
        {
            SpawningSoon = SpawningSoon || other.SpawningSoon;
            RunMoneyLoadout = RunMoneyLoadout || other.RunMoneyLoadout;
            if (MoneyTarget < 0 && other.MoneyTarget >= 0)
                MoneyTarget = other.MoneyTarget;
        }
    }
}
