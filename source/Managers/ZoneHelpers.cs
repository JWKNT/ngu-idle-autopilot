using System;
using System.Linq;
using System.Reflection;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

ZoneHelpers owns native Adventure reachability and all fourteen Titan clock, unlock, version,
puzzle-item, intended-Beast, and combat-admission facts. It supplies read-only countdown/state
signatures and side-effect-free candidate-loadout evaluation so LoadoutManager can fail before a
physical swap. Inputs are live Character/Adventure controllers plus projected candidate stats;
outputs are route facts and explicit readiness reasons—never controller mutations except the
separately documented ITOPOD range optimizer. A ready clock is not combat readiness, versioned
Titans require native autokill evidence where exposed, Beast triples incoming damage, and T13/T14
require a pre-first-action one-hit proof so their stochastic bespoke AI never executes. Collection strategy,
gear selection, and irreversible END activation do not belong here.
*/
namespace NGUInjector.Managers
{
    internal sealed class ItopodRoute
    {
        internal string Mode = "unavailable";
        internal int Start;
        internal int End;
        internal int FarmFloor;
        internal int ReachableFloor;
        internal bool Climbing;
        internal bool Confirmed;
        internal bool RequiresZoneReset;
        internal string Reason = "ITOPOD route has not been evaluated";
    }

    internal sealed class TitanReadiness
    {
        internal bool Ready;
        internal int TitanIndex = -1;
        internal int Version = -1;
        internal string Reason = "Titan readiness has not been evaluated";
    }

    static class ZoneHelpers
    {
        internal static readonly int[] TitanZones =
            { 6, 8, 11, 14, 16, 19, 23, 26, 30, 34, 38, 42, 44, 45 };

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
            return boss >= 0 && boss < TitanZones.Length && Main.Character != null
                   && Main.Character.buttons.adventure.IsInteractable() && CheckTitanSpawnTime(boss);
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
            if (bossId < 0 || bossId >= TitanZones.Length || c == null || c.adventure == null)
                return false;
            // The Ring of Apathy gates UUG in zone 14 (Titan index 3). The previous
            // index incorrectly blocked Jake in zone 11.
            if (bossId == 3 && (c.inventory.itemList.itemMaxxed.Count <= 135
                                || !c.inventory.itemList.itemMaxxed[135]))
                return false;
            // UUG's native AI reads the equipped Ring of Apathy level, not merely
            // the persistent "ever MAXXED" flag.  Without level 100 equipped it can
            // become effectively unwinnable while the generic stat model says yes.
            if (bossId == 3 && (c.inventoryController == null || c.inventoryController.apathyCheck() < 100))
            {
                // The event loadout may be responsible for equipping the ring. Admit
                // pre-staging only when an exact level-100 physical copy is owned;
                // TitanCombatReady re-checks the native equipped predicate afterward.
                var ownsReadyRing = c.inventory.inventory.Concat(c.inventory.accs)
                    .Concat(new[] {c.inventory.head, c.inventory.chest, c.inventory.legs,
                        c.inventory.boots, c.inventory.weapon, c.inventory.weapon2})
                    .Any(x => x != null && x.id == 135 && x.level >= 100);
                if (!ownsReadyRing) return false;
            }

            // IT HUNGERS sets itself invincible when checkAndUseGlop cannot consume
            // item 372.  Never enter and burn a spawn without the actual inventory
            // consumable available.
            if (bossId == 9 && !c.inventory.inventory.Any(x => x != null && x.id == 372))
                return false;

            // Titans 6+ have explicit quest/unlock flags. Reflecting the source field keeps
            // this gate valid across the later Titans without treating a ready clock as proof.
            if (bossId < 5)
                return true;
            // The two END Titans are not represented by titan13/14Unlocked fields.
            // Their native zone/timer UI uses exact Sadistic effective-boss gates;
            // the Traitor additionally requires the Rat Titan/Tippi completion flag.
            if (bossId == 12)
                return c.effectiveBossID() >= 897 || c.adventure.ratTitanDefeated;
            if (bossId == 13)
                return c.effectiveBossID() >= 902 && c.adventure.ratTitanDefeated
                       && !c.adventure.finalTitanDefeated;
            var field = c.adventure.GetType().GetField("titan" + (bossId + 1) + "Unlocked",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null)
                return false;
            var value = field.GetValue(c.adventure);
            return value is bool && (bool)value;
        }

        internal static bool TitanCombatReady(int bossId)
        {
            var c = Main.Character;
            if (c == null) return false;
            var intendedBeast = c.adventureController.hasBeastMode();
            var hasApathy = c.inventoryController != null && c.inventoryController.apathyCheck() >= 100;
            return EvaluateTitanCandidate(bossId, c.totalAdvAttack(), c.totalAdvDefense(),
                c.totalAdvHP(), intendedBeast, hasApathy, true).Ready;
        }

        /*
        SIDE-EFFECT-FREE TITAN CANDIDATE ADMISSION

        Candidate totals are projected by LoadoutManager without equipping anything. Normalize
        attack from the live Beast state into the state the Adventure router will request, and
        multiply incoming damage by native Beast's exact 3x penalty. T6-T12 bespoke AI additionally
        requires the controller's per-version autokill predicate; when candidate gear is not live,
        that predicate is only a necessary current-state proof and the post-swap gate verifies it
        again before routing. T13/T14 expose no pure autokill API and have bespoke growth/random AI,
        so they require one regular hit to exceed maximum HP before their delayed first action.
        */
        internal static TitanReadiness EvaluateTitanCandidate(int bossId, double candidateAttack,
            double candidateDefense, double candidateHp, bool intendedBeast, bool candidateHasApathy,
            bool candidateIsCurrentlyEquipped)
        {
            var result = new TitanReadiness {TitanIndex = bossId};
            var c = Main.Character;
            if (c == null || bossId < 0 || bossId >= TitanZones.Length)
            {
                result.Reason = "Titan index or Character controller is unavailable";
                return result;
            }
            if (!TitanUnlockedForAttempt(bossId))
            {
                result.Reason = "native Titan unlock, boss gate, or required consumable is not satisfied";
                return result;
            }
            if (bossId == 3 && !candidateHasApathy)
            {
                result.Reason = "UUG requires a level-100 Ring of Apathy in the candidate loadout";
                return result;
            }

            int version;
            Enemy enemy;
            if (!TryGetTitanEnemy(bossId, out version, out enemy))
            {
                result.Reason = "native Titan enemy/version record is unavailable";
                return result;
            }
            result.Version = version;
            var terminalDropId = bossId == 11
                ? EndgameDependencyModel.TitanVersionItem(version + 1)
                : bossId == 13 ? MechanicsEndgame.FinalTriggerItemId : -1;
            if (terminalDropId > 0 && !EndgameDependencyModel.IsOwned(c, terminalDropId)
                && (c.inventory == null || c.inventory.inventory == null
                    || !c.inventory.inventory.Any(x => x == null || x.id <= 0)))
            {
                result.Reason = "terminal Titan drop " + terminalDropId
                                + " requires an empty ordinary inventory slot";
                return result;
            }
            if (bossId == 11 && version >= 3 && !candidateHasApathy)
            {
                result.Reason = "AMALGAMATE v4 requires a level-100 Ring of Apathy in the candidate loadout";
                return result;
            }
            if (bossId >= 5)
            {
                string nativeReason;
                if (bossId <= 11 && !NativeAutokillAchieved(bossId, version, out nativeReason))
                {
                    result.Reason = nativeReason + (candidateIsCurrentlyEquipped
                        ? string.Empty : "; candidate gear was not mutated for speculative evaluation");
                    return result;
                }
            }

            var liveBeast = Math.Max(1e-9, c.adventureController.beastModeBonus());
            var targetBeast = intendedBeast
                ? c.inventory.itemList.purpleLiquidComplete ? 1.5 : 1.4
                : 1.0;
            var normalizedAttack = candidateAttack / liveBeast * targetBeast;
            var outgoing = .8 * Math.Max(0.0, normalizedAttack - enemy.defense / 2.0)
                           * c.regAttackPower();
            if (outgoing <= 0)
            {
                result.Reason = "candidate regular attack cannot penetrate the selected Titan version";
                return result;
            }
            var killSeconds = Math.Ceiling(enemy.maxHP / outgoing);
            if (bossId >= 12)
            {
                if (outgoing < enemy.maxHP)
                {
                    result.Reason = "T13/T14 stochastic AI is fail-closed unless one regular hit ("
                                    + outgoing.ToString("0.###e+0") + ") exceeds max HP ("
                                    + enemy.maxHP.ToString("0.###e+0") + ")";
                    return result;
                }
                result.Ready = true;
                result.Reason = "candidate one-hit proof defeats T13/T14 before its first stochastic AI action";
                return result;
            }
            var firstAttack = 1.5 * enemy.attackRate;
            var enemyAttacks = killSeconds < firstAttack
                ? 0
                : 1 + (int)Math.Floor((killSeconds - firstAttack) / Math.Max(.1, enemy.attackRate));
            var beastIncoming = intendedBeast ? 3.0 : 1.0;
            var incoming = beastIncoming * 1.2
                           * Math.Max(enemy.attack * .1, enemy.attack - candidateDefense / 2.0);
            var projectedDamage = enemyAttacks * incoming;
            if (killSeconds > 120)
            {
                result.Reason = "candidate kill projection is " + killSeconds.ToString("0")
                                + "s, above the unattended 120s admission bound";
                return result;
            }
            if (projectedDamage >= candidateHp * .90)
            {
                result.Reason = "candidate damage projection including intended Beast state is "
                                + projectedDamage.ToString("0.###") + " of "
                                + candidateHp.ToString("0.###") + " HP";
                return result;
            }
            result.Ready = true;
            result.Reason = "candidate passes native unlock/version requirements and conservative combat projection";
            return result;
        }

        private static bool TryGetTitanEnemy(int bossId, out int version, out Enemy enemy)
        {
            version = bossId >= 5 && bossId <= 11 ? GetTitanVersion(bossId) : 0;
            enemy = null;
            if (version < 0) return false;
            var c = Main.Character;
            var zone = TitanZones[bossId];
            if (c.adventureController.enemyList == null || zone < 0
                || zone >= c.adventureController.enemyList.Count
                || c.adventureController.enemyList[zone] == null
                || c.adventureController.enemyList[zone].Count == 0)
                return false;
            var enemies = c.adventureController.enemyList[zone];
            var enemyIndex = bossId >= 5 && bossId <= 9 ? version + 1
                : bossId >= 10 && bossId <= 11 ? version : 0;
            if (enemyIndex < 0 || enemyIndex >= enemies.Count) return false;
            enemy = enemies[enemyIndex];
            return enemy != null;
        }

        private static int GetTitanVersion(int bossId)
        {
            if (bossId < 5 || bossId > 11) return 0;
            var field = Main.Character.adventure.GetType().GetField(
                "titan" + (bossId + 1) + "Version",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null || field.FieldType != typeof(int)) return -1;
            return (int)field.GetValue(Main.Character.adventure);
        }

        private static bool NativeAutokillAchieved(int bossId, int version, out string reason)
        {
            reason = string.Empty;
            var method = Main.Character.adventureController.GetType().GetMethod(
                "autokillTitan" + (bossId + 1) + "V" + (version + 1) + "Achieved",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (method == null || method.ReturnType != typeof(bool))
            {
                reason = "native per-version autokill predicate is unavailable";
                return false;
            }
            try
            {
                if ((bool)method.Invoke(Main.Character.adventureController, null)) return true;
                reason = "native per-version autokill predicate is not achieved";
                return false;
            }
            catch (Exception e)
            {
                reason = "native autokill predicate threw " + e.GetType().Name;
                return false;
            }
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
            result.RunMoneyLoadout = Main.Settings.ManageGoldLoadouts
                                     && Main.Settings.TitanGoldTargets != null
                                     && Main.Settings.TitanMoneyDone != null
                                     && bossId < Main.Settings.TitanGoldTargets.Length
                                     && bossId < Main.Settings.TitanMoneyDone.Length
                                     && Main.Settings.TitanGoldTargets[bossId]
                                     && !Main.Settings.TitanMoneyDone[bossId];
            result.MoneyTarget = result.RunMoneyLoadout ? bossId : -1;
            result.SpawningSoon = Main.Settings.SwapTitanLoadouts
                                  && Main.Settings.TitanSwapTargets != null
                                  && bossId < Main.Settings.TitanSwapTargets.Length
                                  && Main.Settings.TitanSwapTargets[bossId]
                                  || result.RunMoneyLoadout
                                  || Main.AutopilotWants(x => x.ManageAdventure);

            return result;
        }

        internal static int HighestTitanLoadoutCandidate()
        {
            var reachable = GetMaxReachableZone(true);
            for (var bossId = TitanZones.Length - 1; bossId >= 0; bossId--)
                if (TitanZones[bossId] <= reachable && TitanUnlockedForAttempt(bossId)
                                                        && CheckTitanSpawnTime(bossId))
                    return bossId;
            return -1;
        }

        internal static bool TitanLoadoutReady(int bossId)
        {
            return TitanLoadoutReady(bossId,
                Main.Character != null && Main.Character.adventureController.hasBeastMode());
        }

        internal static bool TitanLoadoutReady(int bossId, bool intendedBeast)
        {
            return bossId >= 0 && bossId < TitanZones.Length
                   && TitanUnlockedForAttempt(bossId)
                   && EvaluateTitanCandidate(bossId, Main.Character.totalAdvAttack(),
                       Main.Character.totalAdvDefense(), Main.Character.totalAdvHP(),
                       intendedBeast,
                       Main.Character.inventoryController != null
                       && Main.Character.inventoryController.apathyCheck() >= 100, true).Ready;
        }

        internal static int TitanKillCount(int bossId)
        {
            if (bossId < 0 || bossId >= TitanZones.Length) return -1;
            var c = Main.Character;
            if (c == null || c.adventure == null) return -1;
            if (bossId == 12) return c.adventure.ratTitanDefeated ? 1 : 0;
            if (bossId == 13) return c.adventure.finalTitanDefeated ? 1 : 0;
            var field = c.adventure.GetType().GetField("titan" + (bossId + 1) + "Kills",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null || field.FieldType != typeof(int)) return -1;
            return (int)field.GetValue(c.adventure);
        }

        private static bool CheckTitanSpawnTime(int bossId)
        {
            if (Main.Test) return true;
            return SecondsUntilTitanSpawn(bossId) == 0.0;
        }

        internal static double SecondsUntilTitanSpawn(int bossId)
        {
            if (bossId < 0 || bossId >= TitanZones.Length || Main.Character == null
                || Main.Character.adventureController == null || Main.Character.adventure == null)
                return -1.0;
            try
            {
            var controller = Main.Character.adventureController;
            var adventure = Main.Character.adventure;

            var spawnMethod = controller.GetType().GetMethod($"boss{bossId + 1}SpawnTime",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var spawnTimeObj = spawnMethod?.Invoke(controller, null);
            if (spawnTimeObj == null)
                return -1.0;
            var spawnTime = (float) spawnTimeObj;

            var spawnField = adventure.GetType().GetField($"boss{bossId + 1}Spawn",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var spawnObj = spawnField?.GetValue(adventure);

            if (spawnObj == null)
                return -1.0;

            var spawn = (PlayerTime) spawnObj;
            return Math.Max(0.0, spawnTime - spawn.totalseconds);
            }
            catch
            {
                return -1.0;
            }
        }

        internal static string TitanStateSignature(int bossId, bool intendedBeast)
        {
            var c = Main.Character;
            if (c == null || bossId < 0 || bossId >= TitanZones.Length)
                return "unavailable:" + bossId;
            var version = GetTitanVersion(bossId);
            var apathy = c.inventoryController == null ? -1 : c.inventoryController.apathyCheck();
            var glop = c.inventory == null || c.inventory.inventory == null
                ? -1 : c.inventory.inventory.Count(x => x != null && x.id == 372);
            return bossId + "|v=" + version
                   + "|unlock=" + TitanUnlockedForAttempt(bossId)
                   + "|clock=" + SecondsUntilTitanSpawn(bossId).ToString("0.###",
                       System.Globalization.CultureInfo.InvariantCulture)
                   + "|atk=" + c.totalAdvAttack().ToString("R",
                       System.Globalization.CultureInfo.InvariantCulture)
                   + "|def=" + c.totalAdvDefense().ToString("R",
                       System.Globalization.CultureInfo.InvariantCulture)
                   + "|hp=" + c.totalAdvHP().ToString("R",
                       System.Globalization.CultureInfo.InvariantCulture)
                   + "|liveBeast=" + c.adventure.beastModeOn
                   + "|targetBeast=" + intendedBeast
                   + "|apathy=" + apathy + "|glop=" + glop;
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

        internal static ItopodRoute LastItopodRoute { get; private set; } = new ItopodRoute();

        /*
        ITOPOD CLIMB/FARM STATE MACHINE

        The native optimal-floor button always writes start=end and clamps start to highest-1. It therefore
        cannot reach a new record: after ten kills the floor wraps before highestItopodLevel is awarded. To
        climb, the native-valid range must start at highest-1 and end at least highest+1. Advance only one new
        record at a time, using the uncapped one-hit formula, then immediately re-plan. When the next floor is
        not one-hit capable, farm the best reachable floor. Every input mutation is passed through the native
        verifier and checked against Adventure state before telemetry calls it active.
        */
        internal static ItopodRoute ConfigureITOPOD()
        {
            var result = new ItopodRoute();
            var c = Main.Character;
            if (c == null || c.adventureController == null || !c.settings.itopodOn)
            {
                result.Reason = "ITOPOD is not unlocked or its native controller is unavailable";
                LastItopodRoute = result;
                return result;
            }
            var controller = c.adventureController;
            var highest = Math.Max(1, c.adventure.highestItopodLevel);
            var maxFloor = Math.Min(1600, controller.maxItopodLevel());
            // Admission is based on the best physical Adventure-attack set we can
            // actually equip, not whatever production/refill set happens to be live
            // when the one-second router runs. ProgressionLoadoutOptimizer stages
            // that exact set before entering a first-clear climb.
            var reachable = Math.Max(0, Math.Min(maxFloor, CalculateBestOwnedItopodLevel()));
            // Range H-1..H+1 fights H after the native increment and awards the
            // record before wrapping; H+1 is a sentinel, not a required kill.
            var climbing = highest < maxFloor && reachable >= highest;
            var farm = Math.Max(0, Math.Min(reachable, highest - 1));
            var terminalDropMissing = c.settings.rebirthDifficulty == difficulty.sadistic
                                      && !EndgameDependencyModel.IsOwned(c, 491);
            if (terminalDropMissing && reachable >= MechanicsEndgame.ItopodDropMinimumFloor
                && highest >= MechanicsEndgame.ItopodDropMinimumFloor)
                farm = Math.Max(farm, MechanicsEndgame.ItopodDropMinimumFloor);
            var start = climbing ? Math.Max(0, highest - 1) : farm;
            var end = climbing ? highest + 1 : Math.Max(1, farm);

            result.Mode = climbing ? "climb" : "farm";
            result.Start = start;
            result.End = end;
            result.FarmFloor = farm;
            result.ReachableFloor = reachable;
            result.Climbing = climbing;
            result.Reason = climbing
                ? "climb one record floor for first-clear PP, using native range " + start + "-" + end
                : "farm floor " + farm + "; next record floor " + (highest + 1)
                  + " is above the current one-hit ceiling " + reachable;

            // Lazy ITOPOD invokes setOptimalFloor after deaths and can overwrite the deliberate climb range.
            // Full optimization owns the range, so disable that reversible toggle and confirm the live field.
            if (c.arbitrary.boughtLazyITOPOD && c.arbitrary.lazyITOPODOn)
            {
                c.arbitrary.lazyITOPODOn = false;
                controller.updateShifterUI();
                if (c.arbitrary.lazyITOPODOn)
                {
                    result.Reason = "could not disable Lazy ITOPOD; preserving its native range ownership";
                    LastItopodRoute = result;
                    Main.LogAction("REJECTED", result.Reason);
                    return result;
                }
            }

            var changed = c.adventure.itopodStart != start || c.adventure.itopodEnd != end;
            // Native record award leaves the live floor on the H+1 sentinel. Merely
            // verifying new inputs does not move an already-active ITOPOD zone, so
            // the Adventure router must make one verified Safe-Zone round trip on
            // the enemy-free range-transition frame before another spawn occurs.
            result.RequiresZoneReset = changed && c.adventure.zone >= 1000
                                       && controller.currentEnemy == null
                                       && controller.itopodLevel != start;
            if (changed)
            {
                controller.itopodStartInput.text = start.ToString();
                controller.itopodEndInput.text = end.ToString();
                controller.verifyItopodInputs();
            }
            result.Confirmed = c.adventure.itopodStart == start && c.adventure.itopodEnd == end;
            if (changed)
            {
                Main.LogAction(result.Confirmed ? "ITOPOD" : "REJECTED", result.Confirmed
                    ? "Set ITOPOD " + result.Mode + " range " + start + "-" + end
                      + " [confirmed by native Adventure range]"
                    : "ITOPOD verifier did not accept requested range " + start + "-" + end);
            }
            LastItopodRoute = result;
            return result;
        }

        internal static void OptimizeITOPOD()
        {
            if (!Main.Settings.OptimizeITOPODFloor) return;
            if (Main.Character.adventure.zone < 1000) return;
            ConfigureITOPOD();
        }

        internal static int CalculateBestItopodLevel()
        {
            var c = Main.Character;
            return CalculateBestItopodLevel(c.totalAdvAttack());
        }

        /*
        OWNED ITOPOD REACH

        Adventure attack is affine in the sum of native per-item contributions. Select the
        strongest legal physical object for every slot, including the native second-weapon
        factor, then project totalAdvAttack by the exact numerator ratio. This is deliberately
        an admission ceiling only; the physical optimizer must still equip and verify the set
        before combat may move into a record-floor range.
        */
        internal static int CalculateBestOwnedItopodLevel()
        {
            var c = Main.Character;
            if (c == null || c.inventory == null || c.inventoryController == null)
                return 0;
            var inv = c.inventory;
            var controller = c.inventoryController;
            var all = new[] {inv.head, inv.chest, inv.legs, inv.boots, inv.weapon, inv.weapon2}
                .Concat(inv.accs).Concat(inv.inventory)
                .Where(x => x != null && x.id > 0 && x.isEquipment())
                .Distinct().ToList();
            var usedIds = new System.Collections.Generic.HashSet<int>();
            var candidateItemAttack = 0.0;
            foreach (var type in new[] {part.Head, part.Chest, part.Legs, part.Boots})
            {
                var best = all.Where(x => x.type == type && !usedIds.Contains(x.id))
                    .OrderByDescending(controller.equipAttackBonus).FirstOrDefault();
                if (best == null) continue;
                usedIds.Add(best.id);
                candidateItemAttack += controller.equipAttackBonus(best);
            }
            var weapons = all.Where(x => x.type == part.Weapon && !usedIds.Contains(x.id))
                .GroupBy(x => x.id).Select(g => g.OrderByDescending(controller.equipAttackBonus).First())
                .OrderByDescending(controller.equipAttackBonus)
                .Take(controller.weapon2Unlocked() ? 2 : 1).ToList();
            if (weapons.Count > 0)
            {
                candidateItemAttack += controller.equipAttackBonus(weapons[0]);
                usedIds.Add(weapons[0].id);
            }
            if (weapons.Count > 1)
            {
                candidateItemAttack += controller.equipAttackBonus(weapons[1]) * controller.weapon2Factor();
                usedIds.Add(weapons[1].id);
            }
            var accessorySpaces = Math.Min(inv.accs.Count, Math.Max(0, controller.accessorySpaces()));
            var accessories = all.Where(x => x.type == part.Accessory && !usedIds.Contains(x.id))
                .GroupBy(x => x.id).Select(g => g.OrderByDescending(controller.equipAttackBonus).First())
                .OrderByDescending(controller.equipAttackBonus).Take(accessorySpaces);
            candidateItemAttack += accessories.Sum(x => (double)controller.equipAttackBonus(x));

            var currentNumerator = Math.Max(1e-9, c.adventure.attack
                + controller.cubePower() + Math.Max(0.0, controller.attackBonus()));
            var candidateNumerator = Math.Max(0.0, c.adventure.attack
                + controller.cubePower() + candidateItemAttack);
            var projectedTotalAttack = c.totalAdvAttack() * candidateNumerator / currentNumerator;
            return CalculateBestItopodLevel(projectedTotalAttack);
        }

        private static int CalculateBestItopodLevel(double totalAdventureAttack)
        {
            var c = Main.Character;
            totalAdventureAttack *= ItopodTargetAttackFactor();
            var attackPower = Main.Settings.ITOPODCombatMode == 1 || c.training.attackTraining[1] == 0
                ? c.idleAttackPower() : c.regAttackPower();
            var maxLevel = c.adventureController.maxItopodLevel();
            var best = 0;
            for (var floor = 0; floor <= maxLevel; floor++)
            {
                var scale = Math.Pow(1.05, floor);
                var worstHP = 600.0 * scale * 1.02;
                var worstDefense = 10.0 * scale * 1.02;
                var minimumHit = 0.8 * Math.Max(0.0,
                    totalAdventureAttack - worstDefense / 2.0) * attackPower;
                if (minimumHit < worstHP) break;
                best = floor;
            }
            return best;
        }

        internal static double ItopodTargetAttackFactor()
        {
            var c = Main.Character;
            if (c == null || c.adventureController == null) return 1.0;
            var live = Math.Max(1e-9, c.adventureController.beastModeBonus());
            var targetOn = Main.Settings.ITOPODBeastMode && c.adventureController.hasBeastMode();
            var target = targetOn
                ? c.inventory.itemList.purpleLiquidComplete ? 1.5 : 1.4
                : 1.0;
            return target / live;
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
