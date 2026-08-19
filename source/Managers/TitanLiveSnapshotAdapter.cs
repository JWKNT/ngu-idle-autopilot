using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

Purpose: TitanLiveSnapshotAdapter is the game-specific construction boundary for the otherwise
controller-agnostic LiveTitanExecutionRuntime. It lets Main instantiate one epoch-bound T1-T14
executor without rebuilding clocks, selected-version Bestiary evidence, native predicates,
terminal reward state, or physical-loadout callbacks in the scheduler.

Mechanism: Capture reads all fourteen source-of-truth Titan clocks and the live Character graph.
T1-T12 kill evidence comes from the Bestiary record for the desired version's exact enemy, never
Adventure.titanXKills (those persistent counters are advanced by offline progress, not online
manageFight). T6-T12 verification calls the public native version-specific autokill predicates;
T1-T5 mirror the exact private manageFight comparisons because no public predicate exists. The
factory wires build-pinned native adapters, InventoryManager topology, LoadoutManager's strongest
exact-reference staging transaction, persistent autokill access, and a caller-owned active-root
accessor into LiveTitanExecutionRuntime.

Inputs and outputs: Inputs are the synchronized Character, a function returning the currently open
one-second RootTransaction, and CombatManager's terminal-lethal-reservation predicate. Output is a
ready ITitanExecutionRuntime whose Capture returns immutable TitanExecutionSnapshot state and whose
mutations remain inside the supplied root/epoch lease.

Invariants and safety: Capture is read-only and must run on the Unity/main thread. Desired T6-T12
version is the highest version whose live native predicate currently passes (T12's missing END
piece target is honored when reachable); if none pass it falls back to v1 so strongest-loadout
staging can establish the easiest proof. T13 uses ratTitanDefeated as reward evidence. T14 ignores
finalTitanDefeated and settles only after ordinary item 495 exists. The terminal reservation is
never inferred from damage alone: CombatManager must attest it through the supplied predicate.

Extension points and non-goals: Main owns cadence, config authorization, root lifetime, reset
interlock enforcement, and CombatManager ordering. This adapter does not create a scheduler, mutate
combat, invoke reset, predict offline item drops, or restore a runtime across a game epoch.
*/
namespace NGUInjector.Managers
{
    internal sealed class TitanLiveSnapshotAdapter
    {
        private readonly Character _character;
        private readonly Func<int, bool> _terminalReservation;

        private TitanLiveSnapshotAdapter(Character character,
            Func<int, bool> terminalReservation)
        {
            if (character == null) throw new ArgumentNullException("character");
            if (terminalReservation == null)
                throw new ArgumentNullException("terminalReservation");
            _character = character;
            _terminalReservation = terminalReservation;
        }

        internal static LiveTitanExecutionRuntime Create(Character character,
            Func<RootTransaction> activeRoot,
            Func<int, bool> terminalReservation)
        {
            if (character == null) throw new ArgumentNullException("character");
            if (activeRoot == null) throw new ArgumentNullException("activeRoot");
            var adapter = new TitanLiveSnapshotAdapter(character, terminalReservation);
            var registry = NativeBindingRegistry.Create(typeof(Character).Assembly,
                Main.GameAssemblySha256);
            return new LiveTitanExecutionRuntime(registry,
                character.adventureController, character.adventure,
                character.adventureController.zoneSelector,
                adapter.Capture,
                delegate { return InventoryManager.CaptureOrdinaryTopology(character); },
                delegate { return character.settings.autoKillTitans; },
                value => character.settings.autoKillTitans = value,
                LoadoutManager.StageTitanExecutionLoadout,
                LoadoutManager.CaptureTitanExecutionLoadout,
                LoadoutManager.RestoreTitanExecutionLoadout,
                token => OwnsRoot(activeRoot(), token),
                token => OwnsRecovery(activeRoot(), token));
        }

        internal TitanExecutionSnapshot Capture()
        {
            if (!ReferenceEquals(Main.Character, _character)
                || _character.adventure == null || _character.adventureController == null)
                throw new InvalidOperationException("Titan capture Character/controllers are stale");
            var opportunities = new List<TitanExecutionOpportunity>(14);
            for (var titanId = 1; titanId <= 14; titanId++)
                opportunities.Add(CaptureOpportunity(titanId));
            var currentZone = _character.adventure.zone;
            var currentTitan = Array.IndexOf(ZoneHelpers.TitanZones, currentZone) + 1;
            var enemy = _character.adventureController.currentEnemy;
            var currentEnemyIsTarget = currentTitan >= 1 && enemy != null
                && TitanMechanics.IsTitanEnemyType(currentTitan, (int)enemy.enemyType);
            return new TitanExecutionSnapshot(Main.CurrentGameEpochFingerprint,
                Main.IsAutomationReady, _character.settings.autoKillTitans,
                string.Empty, false, opportunities, CaptureWalderp(), currentZone,
                currentEnemyIsTarget);
        }

        private TitanExecutionOpportunity CaptureOpportunity(int titanId)
        {
            var bossIndex = titanId - 1;
            var currentVersion = CurrentVersion(titanId);
            var desiredVersion = DesiredVersion(titanId, currentVersion);
            var remainingSeconds = ZoneHelpers.SecondsUntilTitanSpawn(bossIndex);
            var dueSeconds = SpawnSeconds(titanId);
            var remaining = remainingSeconds < 0.0 ? dueSeconds
                : remainingSeconds >= int.MaxValue ? int.MaxValue
                : Math.Max(0, (int)Math.Ceiling(remainingSeconds));
            var paused = ZoneHelpers.TitanClockPaused(bossIndex);
            var clock = new TitanClockProjection(titanId, dueSeconds, remaining, paused,
                paused ? "awaiting the next Walderp find phase" : string.Empty);
            var readiness = ZoneHelpers.EvaluateTitanCandidate(bossIndex,
                _character.totalAdvAttack(), _character.totalAdvDefense(),
                _character.totalAdvHP(), _character.totalAdvHPRegen(),
                _character.adventureController.hasBeastMode(), HasApathy(), true);
            var rewardActionable = TitanMechanics.IsRewardActionable(titanId,
                _character.adventure.ratTitanDefeated,
                _character.adventure.finalTitanDefeated, HasOrdinaryItem(495));
            var nativeReady = titanId <= 12
                              && NativeAutokillVerified(titanId, desiredVersion);
            var manual = TitanMechanics.EvaluateManualPrerequisites(titanId,
                desiredVersion, HasApathy(), RemovableGlopCopies(), 0);
            var killEvidence = titanId <= 12
                ? DesiredVersionBestiaryKills(titanId, desiredVersion)
                : titanId == 13 ? _character.adventure.ratTitanDefeated ? 1 : 0
                : HasOrdinaryItem(495) ? 1 : 0;
            return new TitanExecutionOpportunity(titanId, currentVersion, desiredVersion,
                clock, readiness.Unlocked, rewardActionable, nativeReady, nativeReady,
                readiness.ManualCombatReady, titanId < 13 || _terminalReservation(titanId),
                manual, killEvidence, Math.Max(0, readiness.CapacityRequiredSlots));
        }

        private WalderpExecutionSnapshot CaptureWalderp()
        {
            var ai = _character.adventureController.enemyAI;
            return new WalderpExecutionSnapshot(
                Math.Max(0, _character.adventure.waldoFinds),
                Math.Max(0, _character.adventure.waldoDefeats),
                ai != null && ai.inWaldoSaysLoop,
                ai == null ? 0 : ai.waldoAttackID,
                ai != null && ai.waldoSays,
                MoveReady(_character.adventureController.regularAttackMove.button),
                MoveReady(_character.adventureController.strongAttackMove.button),
                MoveReady(_character.adventureController.pierceMove.button),
                MoveReady(_character.adventureController.ultimateAttackMove.button));
        }

        private int DesiredVersion(int titanId, int currentVersion)
        {
            if (titanId < 6 || titanId > 12) return 0;
            var highestReady = -1;
            for (var version = 3; version >= 0; version--)
                if (NativeAutokillVerified(titanId, version))
                {
                    highestReady = version;
                    break;
                }
            if (titanId == 12)
            {
                var missing = EndgameDependencyModel.NextMissingTitan12Version(_character);
                if (missing >= 1 && missing <= 4 && missing - 1 <= highestReady)
                    return missing - 1;
            }
            // Version 0 is the least demanding post-stage gate. Remaining on an unqualified high
            // selector would strand a due Titan even when the strongest loadout can kill v1.
            return highestReady >= 0 ? highestReady : 0;
        }

        private bool NativeAutokillVerified(int titanId, int version)
        {
            var controller = _character.adventureController;
            switch (titanId)
            {
                case 1:
                    return _character.totalAdvAttack() > 3000f
                           && _character.totalAdvDefense() > 2500f;
                case 2:
                    return _character.totalAdvAttack() > 9000f
                           && _character.totalAdvDefense() > 7000f;
                case 3:
                    return _character.totalAdvAttack() > 25000f
                           && _character.totalAdvDefense() > 15000f;
                case 4:
                    return _character.totalAdvAttack() >= 800000f
                           && _character.totalAdvDefense() >= 400000f
                           && _character.totalAdvHPRegen() >= 14000f && ApathyMaxxed();
                case 5:
                    return _character.totalAdvAttack() >= 1.3E+07f
                           && _character.totalAdvDefense() >= 7000000f
                           && _character.totalAdvHPRegen() >= 150000f
                           && _character.adventure.boss5Kills >= 3;
                case 6:
                    return version == 0 ? controller.autokillTitan6V1Achieved()
                        : version == 1 ? controller.autokillTitan6V2Achieved()
                        : version == 2 ? controller.autokillTitan6V3Achieved()
                        : controller.autokillTitan6V4Achieved();
                case 7:
                    return version == 0 ? controller.autokillTitan7V1Achieved()
                        : version == 1 ? controller.autokillTitan7V2Achieved()
                        : version == 2 ? controller.autokillTitan7V3Achieved()
                        : controller.autokillTitan7V4Achieved();
                case 8:
                    return version == 0 ? controller.autokillTitan8V1Achieved()
                        : version == 1 ? controller.autokillTitan8V2Achieved()
                        : version == 2 ? controller.autokillTitan8V3Achieved()
                        : controller.autokillTitan8V4Achieved();
                case 9:
                    return version == 0 ? controller.autokillTitan9V1Achieved()
                        : version == 1 ? controller.autokillTitan9V2Achieved()
                        : version == 2 ? controller.autokillTitan9V3Achieved()
                        : controller.autokillTitan9V4Achieved();
                case 10:
                    return version == 0 ? controller.autokillTitan10V1Achieved()
                        : version == 1 ? controller.autokillTitan10V2Achieved()
                        : version == 2 ? controller.autokillTitan10V3Achieved()
                        : controller.autokillTitan10V4Achieved();
                case 11:
                    return version == 0 ? controller.autokillTitan11V1Achieved()
                        : version == 1 ? controller.autokillTitan11V2Achieved()
                        : version == 2 ? controller.autokillTitan11V3Achieved()
                        : controller.autokillTitan11V4Achieved();
                case 12:
                    return version == 0 ? controller.autokillTitan12V1Achieved()
                        : version == 1 ? controller.autokillTitan12V2Achieved()
                        : version == 2 ? controller.autokillTitan12V3Achieved()
                        : controller.autokillTitan12V4Achieved();
                default:
                    return false;
            }
        }

        private int DesiredVersionBestiaryKills(int titanId, int desiredVersion)
        {
            var zone = TitanMechanics.Describe(titanId).Zone;
            var enemies = _character.adventureController.enemyList;
            if (enemies == null || zone < 0 || zone >= enemies.Count || enemies[zone] == null)
                return 0;
            var index = TitanMechanics.EnemyIndexForVersion(titanId, desiredVersion);
            if (index < 0 || index >= enemies[zone].Count || enemies[zone][index] == null)
                return 0;
            var sprite = enemies[zone][index].spriteID;
            return _character.bestiary == null || _character.bestiary.enemies == null
                   || sprite < 0 || sprite >= _character.bestiary.enemies.Count
                   || _character.bestiary.enemies[sprite] == null
                ? 0 : Math.Max(0, _character.bestiary.enemies[sprite].kills);
        }

        private int CurrentVersion(int titanId)
        {
            switch (titanId)
            {
                case 6: return _character.adventure.titan6Version;
                case 7: return _character.adventure.titan7Version;
                case 8: return _character.adventure.titan8Version;
                case 9: return _character.adventure.titan9Version;
                case 10: return _character.adventure.titan10Version;
                case 11: return _character.adventure.titan11Version;
                case 12: return _character.adventure.titan12Version;
                default: return 0;
            }
        }

        private int SpawnSeconds(int titanId)
        {
            var controller = _character.adventureController;
            var seconds = titanId == 1 ? controller.boss1SpawnTime()
                : titanId == 2 ? controller.boss2SpawnTime()
                : titanId == 3 ? controller.boss3SpawnTime()
                : titanId == 4 ? controller.boss4SpawnTime()
                : titanId == 5 ? controller.boss5SpawnTime()
                : titanId == 6 ? controller.boss6SpawnTime()
                : titanId == 7 ? controller.boss7SpawnTime()
                : titanId == 8 ? controller.boss8SpawnTime()
                : titanId == 9 ? controller.boss9SpawnTime()
                : titanId == 10 ? controller.boss10SpawnTime()
                : titanId == 11 ? controller.boss11SpawnTime()
                : titanId == 12 ? controller.boss12SpawnTime()
                : titanId == 13 ? controller.boss13SpawnTime()
                : controller.boss14SpawnTime();
            return Math.Max(1, (int)Math.Ceiling(seconds));
        }

        private bool HasApathy()
        {
            return _character.inventoryController != null
                   && _character.inventoryController.apathyCheck() >= 100;
        }

        private bool ApathyMaxxed()
        {
            return _character.inventory != null && _character.inventory.itemList != null
                   && _character.inventory.itemList.itemMaxxed != null
                   && _character.inventory.itemList.itemMaxxed.Count > 135
                   && _character.inventory.itemList.itemMaxxed[135];
        }

        private int RemovableGlopCopies()
        {
            return _character.inventory == null || _character.inventory.inventory == null ? 0
                : _character.inventory.inventory.Count(x => x != null && x.id == 372
                                                              && x.removable);
        }

        private bool HasOrdinaryItem(int itemId)
        {
            return _character.inventory != null && _character.inventory.inventory != null
                   && _character.inventory.inventory.Any(x => x != null && x.id == itemId);
        }

        private static bool MoveReady(UnityEngine.UI.Button button)
        {
            return button != null && button.IsInteractable();
        }

        private static bool OwnsRoot(RootTransaction root, RootTransactionToken token)
        {
            return root != null && token != null && !root.IsClosed
                   && ReferenceEquals(root.Token, token)
                   && string.Equals(token.EpochFingerprint,
                       Main.CurrentGameEpochFingerprint, StringComparison.Ordinal);
        }

        private static bool OwnsRecovery(RootTransaction root, RecoveryToken token)
        {
            return root != null && token != null && !root.IsClosed
                   && token.RootTransactionId == root.Token.RootTransactionId
                   && token.CoordinatorId == root.Token.CoordinatorId
                   && string.Equals(token.EpochFingerprint,
                       root.Token.EpochFingerprint, StringComparison.Ordinal)
                   && string.Equals(token.EpochFingerprint,
                       Main.CurrentGameEpochFingerprint, StringComparison.Ordinal);
        }
    }
}
