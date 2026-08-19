using System;
using System.Linq;
using System.Reflection;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

ZoneHelpers is the live adapter around the pure TitanMechanics oracle and Adventure reachability.
It captures exact unlock flags, selected versions/bestiary counts, Walderp phase, usable ordinary
loot capacity, and candidate stats without equipping anything. Outputs keep reachable, unlocked,
due, native-autokill, manual-prerequisite, manual-combat, and capacity proofs separate so a caller
cannot mistake one for another. Candidate T6-T12 autokill uses projected Attack/Defense/HP regen,
not a predicate reading the current loadout; native Apathy/Glop rules apply only to manual fights.
T13 is one-shot reward state, while T14 remains recovery-actionable until ordinary item 495 exists.
No Titan mutation occurs here; the separately documented ITOPOD range optimizer is the only write.
That optimizer keeps repeatable Regular-Attack one-hit throughput and the conservative survivable
frontier visible, but neither formula caps the next ten-floor permanent-PP push. Repeated same-floor
deaths or no-HP-progress fights open a farming circuit breaker until Adventure capability improves.
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
        internal int FrontierFloor;
        internal int ModeledPositiveDamageFloor;
        internal int BlockedFloor = -1;
        internal int ConsecutiveFailures;
        internal double TargetKillSeconds = double.PositiveInfinity;
        internal bool Climbing;
        internal bool EmpiricalTrial;
        internal bool Confirmed;
        internal bool RequiresZoneReset;
        internal string Reason = "ITOPOD route has not been evaluated";
    }

    internal sealed class TitanReadiness
    {
        internal bool Ready;
        internal bool ActionableNow;
        internal bool Reachable;
        internal bool Unlocked;
        internal bool RewardActionable;
        internal bool Due;
        internal bool NativeAutokillReady;
        internal bool ManualPrerequisitesReady;
        internal bool ManualCombatReady;
        internal bool CapacityReady;
        internal int CapacityRequiredSlots;
        internal int CapacityFreeSlots;
        internal int TitanIndex = -1;
        internal int Version = -1;
        internal string CapacityReason = "Titan capacity has not been evaluated";
        internal string Reason = "Titan readiness has not been evaluated";
    }

    static class ZoneHelpers
    {
        private static readonly ItopodClimbTrialController ItopodTrials =
            new ItopodClimbTrialController();
        internal static readonly int[] TitanZones =
            { 6, 8, 11, 14, 16, 19, 23, 26, 30, 34, 38, 42, 44, 45 };

        internal static bool ZoneIsTitan(int zone)
        {
            return TitanZones.Contains(zone);
        }

        internal static bool IsTitanEnemy(int zone, enemyType type)
        {
            var bossId = Array.IndexOf(TitanZones, zone);
            return bossId >= 0 && TitanMechanics.IsTitanEnemyType(bossId + 1, (int)type);
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

        internal static bool TitanUnlockedForAttempt(int bossId)
        {
            return TitanNativeUnlocked(bossId) && TitanRewardActionable(bossId);
        }

        private static bool TitanNativeUnlocked(int bossId)
        {
            var c = Main.Character;
            if (bossId < 0 || bossId >= TitanZones.Length || c == null || c.adventure == null)
                return false;
            var unlockFlags = ReadVersionedTitanUnlockFlags(c);
            var apathyMaxxed = c.inventory != null && c.inventory.itemList != null
                               && c.inventory.itemList.itemMaxxed != null
                               && c.inventory.itemList.itemMaxxed.Count > 135
                               && c.inventory.itemList.itemMaxxed[135];
            var titanId = bossId + 1;
            return TitanMechanics.IsUnlocked(titanId, c.effectiveBossID(),
                unlockFlags, apathyMaxxed, c.adventure.ratTitanDefeated);
        }

        private static bool TitanRewardActionable(int bossId)
        {
            var c = Main.Character;
            return bossId >= 0 && bossId < TitanZones.Length && c != null
                   && c.adventure != null
                   && TitanMechanics.IsRewardActionable(bossId + 1,
                c.adventure.ratTitanDefeated, c.adventure.finalTitanDefeated,
                HasOrdinaryItem(c, MechanicsEndgame.FinalTriggerItemId));
        }

        private static bool[] ReadVersionedTitanUnlockFlags(Character c)
        {
            var result = new bool[7];
            if (c == null || c.adventure == null) return result;
            for (var titanId = 6; titanId <= 12; titanId++)
            {
                var field = c.adventure.GetType().GetField("titan" + titanId + "Unlocked",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                result[titanId - 6] = field != null && field.FieldType == typeof(bool)
                                      && (bool)field.GetValue(c.adventure);
            }
            return result;
        }

        internal static bool TitanCombatReady(int bossId)
        {
            var c = Main.Character;
            if (c == null) return false;
            var intendedBeast = c.adventureController.hasBeastMode();
            var hasApathy = c.inventoryController != null && c.inventoryController.apathyCheck() >= 100;
            return EvaluateTitanCandidate(bossId, c.totalAdvAttack(), c.totalAdvDefense(),
                c.totalAdvHP(), c.totalAdvHPRegen(), intendedBeast, hasApathy, true).Ready;
        }

        /*
        SIDE-EFFECT-FREE TITAN CANDIDATE ADMISSION

        Candidate totals are projected without equipping anything. The compatibility overload uses
        live regen; optimization callers should use the explicit-regen overload so the candidate's
        own stats are compared against the pure installed-source T6-T12 thresholds. A confirmed
        native autokill bypasses manual-only Apathy/Glop. Manual paths retain conservative combat
        admission and exact projected Glop charges. T13/T14 require a one-hit proof, a separately
        reserved ready attack in CombatManager, and exact usable loot capacity where applicable.
        */
        internal static TitanReadiness EvaluateTitanCandidate(int bossId, double candidateAttack,
            double candidateDefense, double candidateHp, bool intendedBeast, bool candidateHasApathy,
            bool candidateIsCurrentlyEquipped)
        {
            var regen = Main.Character == null ? 0.0 : Main.Character.totalAdvHPRegen();
            return EvaluateTitanCandidate(bossId, candidateAttack, candidateDefense, candidateHp,
                regen, intendedBeast, candidateHasApathy, candidateIsCurrentlyEquipped);
        }

        internal static TitanReadiness EvaluateTitanCandidate(int bossId, double candidateAttack,
            double candidateDefense, double candidateHp, double candidateHpRegen,
            bool intendedBeast, bool candidateHasApathy, bool candidateIsCurrentlyEquipped)
        {
            var result = new TitanReadiness {TitanIndex = bossId};
            var c = Main.Character;
            if (c == null || bossId < 0 || bossId >= TitanZones.Length)
            {
                result.Reason = "Titan index or Character controller is unavailable";
                return result;
            }
            result.Reachable = TitanMechanics.IsReachable(bossId + 1, GetMaxReachableZone(true));
            result.Due = CheckTitanSpawnTime(bossId);
            result.Unlocked = TitanNativeUnlocked(bossId);
            result.RewardActionable = TitanRewardActionable(bossId);
            if (!result.Reachable)
            {
                result.Reason = "Titan zone is not reachable";
                return result;
            }
            if (!result.Unlocked)
            {
                result.Reason = "native Titan effective-boss/unlock gate is not satisfied";
                return result;
            }
            if (!result.RewardActionable)
            {
                result.Reason = "Titan reward is already complete or no longer actionable";
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
            EvaluateTitanCapacity(bossId, version, result);
            if (!result.CapacityReady)
            {
                result.Reason = result.CapacityReason;
                return result;
            }

            var liveBeast = Math.Max(1e-9, c.adventureController.beastModeBonus());
            var targetBeast = intendedBeast
                ? c.inventory.itemList.purpleLiquidComplete ? 1.5 : 1.4
                : 1.0;
            var normalizedAttack = candidateAttack / liveBeast * targetBeast;
            TitanNativeAutokillProjection autokill = null;
            if (bossId >= 5 && bossId <= 11)
            {
                autokill = TitanMechanics.EvaluateNativeAutokill(bossId + 1, version,
                    normalizedAttack, candidateDefense, candidateHpRegen,
                    SelectedVersionBestiaryKills(enemy));
                result.NativeAutokillReady = autokill.Achieved;
                if (autokill.Achieved)
                {
                    result.ManualPrerequisitesReady = false;
                    result.ManualCombatReady = false;
                    result.Ready = true;
                    result.ActionableNow = result.Due;
                    result.Reason = "projected native autokill: " + autokill.Reason
                                    + "; manual Apathy/Glop prerequisites do not apply";
                    return result;
                }
            }

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
                result.ManualPrerequisitesReady = true;
                result.ManualCombatReady = true;
                result.Ready = true;
                result.ActionableNow = result.Due;
                result.Reason = "candidate one-hit proof defeats T13/T14 before its first stochastic AI action";
                return result;
            }
            var firstAttack = 1.5 * enemy.attackRate;
            var enemyAttacks = killSeconds < firstAttack
                ? 0
                : 1 + (int)Math.Floor((killSeconds - firstAttack) / Math.Max(.1, enemy.attackRate));
            var glopCopies = c.inventory == null || c.inventory.inventory == null ? 0
                : c.inventory.inventory.Count(x => x != null && x.id == 372 && x.removable);
            var manual = TitanMechanics.EvaluateManualPrerequisites(bossId + 1,
                Math.Max(0, version), candidateHasApathy, glopCopies, enemyAttacks);
            result.ManualPrerequisitesReady = manual.Ready;
            if (!manual.Ready)
            {
                result.Reason = manual.Reason;
                return result;
            }
            if (bossId == 4)
            {
                result.Reason = "Walderp manual execution requires its exact Says response state machine";
                return result;
            }
            // T6-T12 are admitted here only through exact native autokill. Their bespoke manual
            // AIs require the execution controller's source-specific state machine.
            if (bossId >= 5 && bossId <= 11)
            {
                result.Reason = autokill == null
                    ? "versioned Titan autokill projection is unavailable"
                    : autokill.Reason + "; manual bespoke-AI execution is not proven here";
                return result;
            }
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
            result.ManualCombatReady = true;
            result.Ready = true;
            result.ActionableNow = result.Due;
            result.Reason = "candidate passes unlock, manual prerequisite, capacity, and conservative combat proofs";
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
            var enemyIndex = TitanMechanics.EnemyIndexForVersion(bossId + 1, version);
            if (enemyIndex < 0 || enemyIndex >= enemies.Count) return false;
            enemy = enemies[enemyIndex];
            return enemy != null
                   && TitanMechanics.IsTitanEnemyType(bossId + 1, (int)enemy.enemyType);
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

        private static int SelectedVersionBestiaryKills(Enemy enemy)
        {
            var c = Main.Character;
            if (enemy == null || c == null || c.bestiary == null
                || c.bestiary.enemies == null || enemy.spriteID < 0
                || enemy.spriteID >= c.bestiary.enemies.Count
                || c.bestiary.enemies[enemy.spriteID] == null)
                return 0;
            return Math.Max(0, c.bestiary.enemies[enemy.spriteID].kills);
        }

        /*
        EXACT TITAN LOOT CAPACITY

        Native addLoot scans only after the merge-reserved prefix and never merges on insertion.
        T12 reserves every possible emission preceding its latest still-missing cumulative END roll;
        T14 reserves the guaranteed item 495 even after a prior flag-only failed delivery. Other
        Titans currently expose no unique terminal delivery here and therefore receive a vacuous
        capacity proof rather than an ad-hoc Count(empty) approximation.
        */
        private static void EvaluateTitanCapacity(int bossId, int version, TitanReadiness result)
        {
            result.CapacityReady = true;
            result.CapacityRequiredSlots = 0;
            result.CapacityFreeSlots = 0;
            result.CapacityReason = "no unique terminal Titan delivery requires a capacity proof";
            if (bossId != 11 && bossId != 13) return;

            OrdinaryInventoryTopology topology;
            string captureReason;
            if (!TryCaptureOrdinaryTopology(out topology, out captureReason))
            {
                result.CapacityReady = false;
                result.CapacityReason = captureReason;
                return;
            }

            LootCapacityRequirement requirement = null;
            if (bossId == 11)
            {
                var ordinaryIds = new int[topology.SlotCount];
                for (var i = 0; i < ordinaryIds.Length; i++)
                    ordinaryIds[i] = topology.SlotAt(i).ItemId;
                var latest = MechanicsEndgame.LatestMissingTitan12DropForVersion(
                    version + 1, ordinaryIds);
                if (latest > 0) requirement = LootCapacity.Titan12EndPiece(latest);
            }
            else if (!topology.HasOrdinaryItem(MechanicsEndgame.FinalTriggerItemId))
                requirement = LootCapacity.Titan14FinalPiece();

            result.CapacityFreeSlots = topology.UsableFreeSlotCount;
            if (requirement == null)
            {
                result.CapacityReason = "all cumulative unique drops covered by this Titan are already ordinary-owned";
                return;
            }
            var proof = LootCapacity.ProveOrdinary(topology, requirement);
            result.CapacityReady = proof.Admitted;
            result.CapacityRequiredSlots = proof.RequiredFreeSlots;
            result.CapacityFreeSlots = proof.UsableFreeSlotCount;
            result.CapacityReason = proof.Admitted
                ? "exact Titan loot-capacity proof admitted " + proof.RequiredFreeSlots + " slots"
                : "Titan held: exact loot needs " + proof.RequiredFreeSlots + " usable slots but only "
                  + proof.UsableFreeSlotCount + " are free after the merge-reserved prefix";
        }

        private static bool TryCaptureOrdinaryTopology(out OrdinaryInventoryTopology topology,
            out string reason)
        {
            topology = null;
            reason = string.Empty;
            var c = Main.Character;
            if (c == null || c.inventory == null || c.inventory.inventory == null
                || c.inventoryController == null)
            {
                reason = "ordinary inventory/controller is unavailable for exact Titan capacity";
                return false;
            }
            try
            {
                var count = c.inventory.inventory.Count;
                var ids = new int[count];
                var identities = new object[count];
                for (var i = 0; i < count; i++)
                {
                    var item = c.inventory.inventory[i];
                    if (item == null || item.id <= 0) continue;
                    ids[i] = item.id;
                    identities[i] = item;
                }
                topology = PhysicalTopology.CaptureOrdinary(ids, identities,
                    c.inventoryController.curSpaces(),
                    c.inventoryController.totalInvMergeSlots());
                return true;
            }
            catch (Exception error)
            {
                reason = "ordinary Titan capacity capture failed: " + error.GetType().Name;
                return false;
            }
        }

        private static bool HasOrdinaryItem(Character c, int itemId)
        {
            return c != null && c.inventory != null && c.inventory.inventory != null
                   && c.inventory.inventory.Any(x => x != null && x.id == itemId);
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
                       Main.Character.totalAdvHPRegen(),
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

        internal static bool TitanClockPaused(int bossId)
        {
            var c = Main.Character;
            return bossId == 4 && c != null && c.adventure != null
                   && TitanMechanics.IsWaldoClockPaused(c.adventure.waldoFinds,
                       c.adventure.waldoDefeats)
                   && SecondsUntilTitanSpawn(bossId) > 0.0;
        }

        internal static double TitanWallClockEtaSeconds(int bossId)
        {
            return TitanClockPaused(bossId) ? -1.0 : SecondsUntilTitanSpawn(bossId);
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
                   + "|wallEta=" + TitanWallClockEtaSeconds(bossId).ToString("0.###",
                       System.Globalization.CultureInfo.InvariantCulture)
                   + "|paused=" + TitanClockPaused(bossId)
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
        climb, the native-valid range must start at highest-1 and end above the record being earned. An open
        push aims directly at the next record divisible by ten. Confirmed repeated failures on one fought floor
        pause that push; necessary replay kills below it do not erase the evidence. Farming remains on the
        separate repeatable one-hit floor because PP/EXP throughput and first-clear reach are different
        objectives. Every input mutation is passed through the native verifier and checked against Adventure
        state before telemetry calls it active.
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
            // Admission is based on the best physical Adventure-combat set we can
            // actually equip, not whatever production/refill set happens to be live
            // when the one-second router runs. ProgressionLoadoutOptimizer stages
            // that exact set before entering a first-clear climb.
            OwnedItopodCombatSnapshot owned;
            var reach = CalculateBestOwnedItopodReach(out owned);
            var reachable = Math.Max(0, Math.Min(maxFloor, reach.OneHitFloor));
            var frontier = Math.Max(0, Math.Min(maxFloor, reach.FrontierFloor));
            var modeledPositiveDamage = Math.Max(0,
                Math.Min(maxFloor, reach.ModeledPositiveDamageFloor));
            // Range H-1..target fights every floor through target-1 and awards the target record
            // before wrapping; target is a sentinel, not a required kill. Non-decade records have
            // no first-clear PP value, so an open empirical push always aims directly at the next
            // multiple of ten. Observed outcomes, not the old 4.1-second proof ceiling, decide when
            // to stop and farm.
            ItopodTrialDecision trialDecision = null;
            if (owned != null)
            {
                trialDecision = ItopodTrials.Decide(highest, maxFloor,
                    TrialCapability(owned));
                if (trialDecision.Reopened)
                    Main.LogAction("ITOPOD", "Reopened empirical floor " + highest
                        + " climb after effective Adventure capability improved");
            }
            var climbing = trialDecision != null && trialDecision.ShouldClimb;
            var climbTarget = climbing ? trialDecision.TargetRecord : highest;
            var farm = Math.Max(0, Math.Min(reachable, highest - 1));
            var terminalDropMissing = c.settings.rebirthDifficulty == difficulty.sadistic
                                      && !EndgameDependencyModel.IsOwned(c, 491);
            if (terminalDropMissing && reachable >= MechanicsEndgame.ItopodDropMinimumFloor
                && highest >= MechanicsEndgame.ItopodDropMinimumFloor)
                farm = Math.Max(farm, MechanicsEndgame.ItopodDropMinimumFloor);
            var start = climbing ? Math.Max(0, highest - 1) : farm;
            var end = climbing ? climbTarget : Math.Max(1, farm);

            result.Mode = climbing ? "climb" : "farm";
            result.Start = start;
            result.End = end;
            result.FarmFloor = farm;
            result.ReachableFloor = reachable;
            result.FrontierFloor = frontier;
            result.ModeledPositiveDamageFloor = modeledPositiveDamage;
            result.BlockedFloor = trialDecision == null ? -1 : trialDecision.BlockedFloor;
            result.ConsecutiveFailures = trialDecision == null
                ? ItopodTrials.ConsecutiveFailures : trialDecision.ConsecutiveFailures;
            result.Climbing = climbing;
            result.EmpiricalTrial = climbing && climbTarget - 1 > frontier;
            if (owned != null)
            {
                var foughtFloor = climbing ? Math.Max(0, climbTarget - 1) : farm;
                result.TargetKillSeconds = ItopodCombatOracle.EvaluateFloor(foughtFloor,
                    owned.Attack, owned.Defense, owned.AvailableHp, owned.AttackPower,
                    owned.AttackCadence, owned.IncomingBeastFactor).KillSeconds;
            }
            result.Reason = climbing
                ? "empirically climb to record " + climbTarget
                  + " for its exact first-clear PP award"
                  + ", using native range " + start + "-" + end
                  + "; diagnostic Regular-Attack model " + modeledPositiveDamage
                  + ", bounded frontier " + frontier
                  + ", repeatable one-hit farm floor " + reachable
                : "farm floor " + farm + "; "
                  + (trialDecision == null ? "empirical climb state is unavailable"
                      : trialDecision.Reason)
                  + " (diagnostic Regular-Attack model " + modeledPositiveDamage
                  + ", bounded frontier " + frontier
                  + ", repeatable one-hit farm floor " + reachable + ")";

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
        factor, then project Attack, Defense, and HP by their exact affine numerator ratios.
        Repeatable one-hit reach prices steady farming; the conservative frontier and positive-damage
        formula remain diagnostic. Neither model is an admission ceiling for a decade push: live
        outcomes own that decision. The physical optimizer still equips and verifies an exact legal
        set before combat moves into the requested range.
        */
        internal static int CalculateBestOwnedItopodLevel()
        {
            OwnedItopodCombatSnapshot ignored;
            return CalculateBestOwnedItopodReach(out ignored).OneHitFloor;
        }

        internal static int CalculateBestOwnedItopodFrontierLevel()
        {
            OwnedItopodCombatSnapshot ignored;
            return CalculateBestOwnedItopodReach(out ignored).FrontierFloor;
        }

        internal static int CalculateBestItopodFrontierLevel()
        {
            var c = Main.Character;
            if (c == null || c.adventureController == null) return 0;
            var targetAttack = c.totalAdvAttack() * ItopodTargetAttackFactor();
            var attackPower = ItopodAttackPower(c);
            return ItopodCombatOracle.ProveReach(targetAttack, c.totalAdvDefense(),
                Math.Max(0.0, Math.Min(c.adventure.curHP, c.totalAdvHP())), attackPower,
                ItopodAttackCadence(c), ItopodTargetIncomingFactor(),
                c.adventureController.maxItopodLevel()).FrontierFloor;
        }

        internal static void RecordItopodFightResult(int foughtFloor, bool died)
        {
            if (foughtFloor < 0 || foughtFloor > ItopodPerkPlanner.MaximumFloor) return;
            OwnedItopodCombatSnapshot owned;
            CalculateBestOwnedItopodReach(out owned);
            if (owned == null) return;
            if (ItopodTrials.Observe(foughtFloor, died, TrialCapability(owned)))
                Main.LogAction("HOLD", "ITOPOD floor " + foughtFloor
                    + " reached the empirical failure limit after "
                    + ItopodTrials.ConsecutiveFailures
                    + " failed attempts; farming until effective Adventure capability improves");
        }

        internal static void RecordItopodNoProgressFailure(int foughtFloor)
        {
            if (foughtFloor < 0 || foughtFloor > ItopodPerkPlanner.MaximumFloor) return;
            OwnedItopodCombatSnapshot owned;
            CalculateBestOwnedItopodReach(out owned);
            if (owned == null) return;
            var blocked = ItopodTrials.Observe(foughtFloor, true, TrialCapability(owned));
            Main.LogAction(blocked ? "HOLD" : "ITOPOD",
                "ITOPOD floor " + foughtFloor + " made no new enemy-HP low for "
                + ItopodFightProgressWatch.NoProgressSeconds.ToString("0")
                + " seconds; failed attempt " + ItopodTrials.ConsecutiveFailures
                + "/" + ItopodClimbTrialController.FailureStreakLimit
                + (blocked ? ", farming until effective Adventure capability improves" : string.Empty));
        }

        private sealed class OwnedItopodCombatSnapshot
        {
            internal double Attack;
            internal double Defense;
            internal double AvailableHp;
            internal double MaximumHp;
            internal double AttackPower;
            internal double AttackCadence;
            internal double IncomingBeastFactor;
        }

        private static ItopodCombatReachProof CalculateBestOwnedItopodReach(
            out OwnedItopodCombatSnapshot snapshot)
        {
            snapshot = null;
            var bestOneHit = 0;
            var bestFrontier = 0;
            var bestTrial = 0;
            var selectedFrontier = -1;
            var bestFrontierSeconds = double.PositiveInfinity;
            var bestTrialSeconds = double.PositiveInfinity;
            var bestReason = "no physically realizable owned combat candidate was evaluated";
            // Each weight constructs one complete, physically realizable set. Taking the best
            // proven reach across those sets is safe; unlike unioning independent maxima, every
            // Attack/Defense/HP tuple came from the same actual objects and slot constraints.
            foreach (var defenseWeight in new[] {0.0, .25, .5, .75, 1.0})
            {
                OwnedItopodCombatSnapshot candidate;
                var proof = CalculateOwnedItopodReachForWeight(defenseWeight, out candidate);
                bestOneHit = Math.Max(bestOneHit, proof.OneHitFloor);
                bestFrontier = Math.Max(bestFrontier, proof.FrontierFloor);
                if (candidate != null && (snapshot == null
                    || proof.ModeledPositiveDamageFloor > bestTrial
                    || proof.ModeledPositiveDamageFloor == bestTrial
                       && proof.FrontierFloor > selectedFrontier
                    || proof.ModeledPositiveDamageFloor == bestTrial
                       && proof.FrontierFloor == selectedFrontier
                       && proof.ModeledPositiveDamageKillSeconds < bestTrialSeconds))
                {
                    snapshot = candidate;
                    bestFrontierSeconds = proof.FrontierKillSeconds;
                    bestTrial = proof.ModeledPositiveDamageFloor;
                    selectedFrontier = proof.FrontierFloor;
                    bestTrialSeconds = proof.ModeledPositiveDamageKillSeconds;
                    bestReason = proof.FrontierReason;
                }
            }
            return new ItopodCombatReachProof(bestOneHit, bestFrontier, bestTrial,
                bestFrontierSeconds, bestTrialSeconds, bestReason);
        }

        private static ItopodCombatReachProof CalculateOwnedItopodReachForWeight(
            double defenseWeight, out OwnedItopodCombatSnapshot snapshot)
        {
            snapshot = null;
            var c = Main.Character;
            if (c == null || c.inventory == null || c.inventoryController == null)
                return new ItopodCombatReachProof(0, 0, 0, double.PositiveInfinity,
                    double.PositiveInfinity, "owned ITOPOD combat topology is unavailable");
            var inv = c.inventory;
            var controller = c.inventoryController;
            var all = new[] {inv.head, inv.chest, inv.legs, inv.boots, inv.weapon, inv.weapon2}
                .Concat(inv.accs).Concat(inv.inventory)
                .Where(x => x != null && x.id > 0 && x.isEquipment())
                .Distinct().ToList();
            var usedIds = new System.Collections.Generic.HashSet<int>();
            var candidateItemAttack = 0.0;
            var candidateItemDefense = 0.0;
            foreach (var type in new[] {part.Head, part.Chest, part.Legs, part.Boots})
            {
                var best = all.Where(x => x.type == type && !usedIds.Contains(x.id))
                    .OrderByDescending(x => ItopodItemScore(controller, x, defenseWeight))
                    .FirstOrDefault();
                if (best == null) continue;
                usedIds.Add(best.id);
                candidateItemAttack += controller.equipAttackBonus(best);
                candidateItemDefense += controller.equipDefenseBonus(best);
            }
            var weapons = all.Where(x => x.type == part.Weapon && !usedIds.Contains(x.id))
                .GroupBy(x => x.id).Select(g => g.OrderByDescending(
                    x => ItopodItemScore(controller, x, defenseWeight)).First())
                .OrderByDescending(x => ItopodItemScore(controller, x, defenseWeight))
                .Take(controller.weapon2Unlocked() ? 2 : 1).ToList();
            if (weapons.Count > 0)
            {
                candidateItemAttack += controller.equipAttackBonus(weapons[0]);
                candidateItemDefense += controller.equipDefenseBonus(weapons[0]);
                usedIds.Add(weapons[0].id);
            }
            if (weapons.Count > 1)
            {
                candidateItemAttack += controller.equipAttackBonus(weapons[1]) * controller.weapon2Factor();
                candidateItemDefense += controller.equipDefenseBonus(weapons[1]) * controller.weapon2Factor();
                usedIds.Add(weapons[1].id);
            }
            var accessorySpaces = Math.Min(inv.accs.Count, Math.Max(0, controller.accessorySpaces()));
            var accessories = all.Where(x => x.type == part.Accessory && !usedIds.Contains(x.id))
                .GroupBy(x => x.id).Select(g => g.OrderByDescending(
                    x => ItopodItemScore(controller, x, defenseWeight)).First())
                .OrderByDescending(x => ItopodItemScore(controller, x, defenseWeight))
                .Take(accessorySpaces).ToList();
            candidateItemAttack += accessories.Sum(x => (double)controller.equipAttackBonus(x));
            candidateItemDefense += accessories.Sum(x => (double)controller.equipDefenseBonus(x));

            var currentNumerator = Math.Max(1e-9, c.adventure.attack
                + controller.cubePower() + Math.Max(0.0, controller.attackBonus()));
            var candidateNumerator = Math.Max(0.0, c.adventure.attack
                + controller.cubePower() + candidateItemAttack);
            var projectedTotalAttack = c.totalAdvAttack() * candidateNumerator / currentNumerator;
            var currentDefenseNumerator = Math.Max(1e-9, c.adventure.defense
                + controller.cubeToughness() + Math.Max(0.0, controller.defenseBonus()));
            var candidateDefenseNumerator = Math.Max(0.0, c.adventure.defense
                + controller.cubeToughness() + candidateItemDefense);
            var projectedTotalDefense = c.totalAdvDefense()
                                        * candidateDefenseNumerator / currentDefenseNumerator;
            var currentHpNumerator = Math.Max(1e-9, c.adventure.maxHP
                + 3.0 * (controller.cubePower() + Math.Max(0.0, controller.attackBonus())));
            var candidateHpNumerator = Math.Max(0.0, c.adventure.maxHP
                + 3.0 * (controller.cubePower() + candidateItemAttack));
            var projectedMaxHp = c.totalAdvHP() * candidateHpNumerator / currentHpNumerator;
            snapshot = new OwnedItopodCombatSnapshot
            {
                Attack = projectedTotalAttack * ItopodTargetAttackFactor(),
                Defense = projectedTotalDefense,
                AvailableHp = Math.Max(0.0, Math.Min(c.adventure.curHP, projectedMaxHp)),
                MaximumHp = projectedMaxHp,
                AttackPower = ItopodAttackPower(c),
                AttackCadence = ItopodAttackCadence(c),
                IncomingBeastFactor = ItopodTargetIncomingFactor()
            };
            return ItopodCombatOracle.ProveReach(snapshot.Attack, snapshot.Defense,
                snapshot.AvailableHp, snapshot.AttackPower, snapshot.AttackCadence,
                snapshot.IncomingBeastFactor, c.adventureController.maxItopodLevel());
        }

        private static ItopodTrialCapability TrialCapability(
            OwnedItopodCombatSnapshot snapshot)
        {
            return new ItopodTrialCapability(snapshot.Attack, snapshot.Defense,
                snapshot.MaximumHp, snapshot.AttackPower,
                snapshot.AttackCadence, snapshot.IncomingBeastFactor);
        }

        private static double ItopodItemScore(InventoryController controller,
            Equipment item, double defenseWeight)
        {
            return (1.0 - defenseWeight) * Math.Max(0.0, controller.equipAttackBonus(item))
                   + defenseWeight * Math.Max(0.0, controller.equipDefenseBonus(item));
        }

        private static int CalculateBestItopodLevel(double totalAdventureAttack)
        {
            var c = Main.Character;
            totalAdventureAttack *= ItopodTargetAttackFactor();
            // The configured Manual mode is executable for both record climbs and steady farms
            // once Regular Attack's exact row-0 gate is open.  Keep this admission formula
            // identical to AutopilotManager's fight type and the immutable loadout objective;
            // row 1 belongs to Strong Attack and is not the Regular Attack unlock.
            var training = c.training.attackTraining;
            var manual = Main.Settings.ITOPODCombatMode != 1 && training != null
                         && training.Length > 0 && training[0] >= 5000;
            var attackPower = manual ? c.regAttackPower() : c.idleAttackPower();
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

        private static double ItopodAttackPower(Character c)
        {
            var training = c.training.attackTraining;
            var manual = Main.Settings.ITOPODCombatMode != 1 && training != null
                         && training.Length > 0 && training[0] >= 5000;
            return manual ? c.regAttackPower() : c.idleAttackPower();
        }

        private static double ItopodAttackCadence(Character c)
        {
            var training = c.training.attackTraining;
            var manual = Main.Settings.ITOPODCombatMode != 1 && training != null
                         && training.Length > 0 && training[0] >= 5000;
            return manual ? .8 : Math.Max(.02, c.adventure.attackSpeed);
        }

        private static double ItopodTargetIncomingFactor()
        {
            var c = Main.Character;
            return c != null && c.adventureController != null
                   && Main.Settings.ITOPODBeastMode && c.adventureController.hasBeastMode()
                ? 3.0 : 1.0;
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
