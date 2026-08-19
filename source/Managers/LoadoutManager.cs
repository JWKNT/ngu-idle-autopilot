using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NGUInjector.Autopilot;
using static NGUInjector.Main;

/*
FILE PURPOSE

LoadoutManager serializes transient physical-gear objectives and performs leased native slot swaps
without violating Titan, Yggdrasil, Gold, or Money Pit locks. It resolves exact Equipment object
references, preflights Titan combat against the intended Beast/version/loadout state before the
first slot mutation, snapshots rollback identity, and verifies every native postcondition. The
task-13 stage/capture/restore hooks let the event-driven Titan coordinator hold one exact common
physical loadout across an adjacent-frame native-autokill chain without treating manual puzzle
items as autokill prerequisites. Inputs are live inventory/controllers, config loadout IDs, Titan
clocks, and execution leases; outputs are confirmed gear transitions or throttled HOLD telemetry.
Direct Equipment assignment, partial transactions, topology mutation while locked, dry-run
mutation, and repeated infeasible Titan equip/rollback loops are forbidden. Strategy chooses
contexts elsewhere; this file owns physical transactions, feasibility admission, backoff, and
exact restoration only.
*/
namespace NGUInjector.Managers
{
    internal enum LockType
    {
        Titan,
        Yggdrasil,
        MoneyPit,
        Gold,
        None
    }
    internal static class LoadoutManager
    {
        private static int[] _savedLoadout;
        private static int[] _tempLoadout;
        private static ExactLoadout _savedExactLoadout;
        private static ExactLoadout _activeTitanExecutionExactLoadout;
        private static string _activeTitanExecutionStageId = string.Empty;
        private static string _activeTitanExecutionPhysicalFingerprint = string.Empty;
        internal static int PendingTitanMoneyTarget { get; private set; } = -1;
        private static int _pendingTitanKillsBefore = -1;
        internal static LockType CurrentLock { get; set; }
        private static readonly Dictionary<int, TitanPreflightHold> TitanPreflightHolds =
            new Dictionary<int, TitanPreflightHold>();

        private sealed class TitanPreflightHold
        {
            internal string StateSignature;
            internal string Reason;
            internal DateTime RetryAfterUtc;
        }

        internal static bool CanSwap()
        {
            return CurrentLock == LockType.None;
        }

        internal static void AcquireLock(LockType type)
        {
            CurrentLock = type;
        }

        internal static void ReleaseLock()
        {
            CurrentLock = LockType.None;
        }

        private static MutationClass MutationClassFor(LockType type)
        {
            switch (type)
            {
                case LockType.Titan: return MutationClass.TitanLoadout;
                case LockType.Yggdrasil: return MutationClass.YggdrasilLoadout;
                case LockType.MoneyPit: return MutationClass.MoneyPitLoadout;
                case LockType.Gold: return MutationClass.GoldLoadout;
                default: return MutationClass.Loadout;
            }
        }

        private static bool HasMutationLease(MutationClass mutationClass,
            MutationOwner owner, string context)
        {
            MutationLease lease;
            string reason;
            if (ExecutionSafety.TryAcquire(mutationClass, owner, out lease, out reason)
                && lease.IsCurrent)
                return true;
            ExecutionSafety.ReportHold("loadout-lease:" + context,
                "Loadout " + context + " held: " + (string.IsNullOrEmpty(reason)
                    ? "execution lease became stale" : reason));
            return false;
        }

        internal static bool RestoreGear()
        {
            if (!HasMutationLease(MutationClassFor(CurrentLock), MutationOwner.System,
                "restoration"))
                return false;
            if (!Main.HasExecutableAllocationOwner)
            {
                ExecutionSafety.ReportHold("loadout-restoration-no-allocation-owner",
                    "Loadout restoration held because no allocation profile can restore reclaimed resources");
                return false;
            }
            Log($"Restoring original loadout");
            if (_savedExactLoadout != null)
            {
                var restored = ApplyExactLoadout(_savedExactLoadout,
                    MutationClassFor(CurrentLock), MutationOwner.System);
                Main.LogAction(restored ? "GEAR" : "REJECTED", restored
                    ? "Restored the exact pre-event physical loadout [confirmed by reference identity]"
                    : "Could not verify restoration of the exact pre-event physical loadout");
                if (restored)
                {
                    _savedExactLoadout = null;
                    _activeTitanExecutionExactLoadout = null;
                    _activeTitanExecutionStageId = string.Empty;
                    _activeTitanExecutionPhysicalFingerprint = string.Empty;
                }
                return restored;
            }
            ExecutionSafety.ReportHold("loadout-restoration-no-snapshot",
                "Loadout restoration held because no exact physical rollback snapshot exists");
            return false;
        }

        internal static void TryTitanSwap()
        {
            if ((Settings.TitanLoadout == null || Settings.TitanLoadout.Length == 0)
                && (Settings.GoldDropLoadout == null || Settings.GoldDropLoadout.Length == 0)
                && !Main.AutopilotWants(x => x.ManageAdventure))
                return;
            var owner = Main.AutopilotWants(x => x.ManageAdventure)
                ? MutationOwner.Autopilot : MutationOwner.Legacy;
            if (!HasMutationLease(MutationClass.TitanLoadout, owner, "Titan event"))
                return;
            //Skip if we're currently locked for yggdrasil (although this generally shouldn't happen)
            if (!CanAcquireOrHasLock(LockType.Titan))
                return;

            //If we're currently holding the lock
            if (CurrentLock == LockType.Titan)
            {
                if (PendingTitanMoneyTarget >= 0)
                {
                    if (PendingTitanKillConfirmed())
                        CompleteTitanFight(false, ZoneHelpers.TitanZones[PendingTitanMoneyTarget]);
                    else if (ZoneHelpers.TitanSpawningSoon(PendingTitanMoneyTarget))
                        return;
                    else
                    {
                        Main.LogAction("REJECTED", "Titan money target clock changed without the exact native Titan kill delta; no completion recorded");
                        ClearPendingTitanMoney();
                    }
                }
                else if (ZoneHelpers.TitansSpawningSoon().SpawningSoon)
                    return;

                //Titans have been AKed, restore back to original gear
                if (RestoreGear()) ReleaseLock();
                return;
            }

            //No lock currently, check if titans are spawning
            var ts = ZoneHelpers.TitansSpawningSoon();
            if (ts.SpawningSoon)
            {
                var titanTarget = ts.MoneyTarget >= 0
                    ? ts.MoneyTarget : ZoneHelpers.HighestTitanLoadoutCandidate();
                if (titanTarget < 0) return;
                var targetLoadout = Settings.ManageGoldLoadouts && ts.RunMoneyLoadout
                    ? Settings.GoldDropLoadout
                    : Settings.TitanLoadout;
                if (targetLoadout == null)
                    return;
                var goldContext = Settings.ManageGoldLoadouts && ts.RunMoneyLoadout;
                var killsBefore = goldContext ? ZoneHelpers.TitanKillCount(titanTarget) : -1;
                if (goldContext && killsBefore < 0)
                {
                    ExecutionSafety.ReportHold("titan-kill-counter:" + titanTarget,
                        "Titan money loadout held because the native completion counter is unavailable");
                    return;
                }
                var titanContext = goldContext && targetLoadout.Length == 0
                    ? "gold-titan" : goldContext ? "gold" : "titan";
                string resolveReason;
                var desired = ResolveExactLoadout(targetLoadout, titanContext,
                    titanTarget, out resolveReason);
                if (desired == null)
                {
                    HoldTitanPreflight(titanTarget, "unresolved|" + resolveReason,
                        "Titan loadout held before mutation: " + resolveReason);
                    return;
                }
                string preflightReason;
                if (!TitanCandidatePreflight(titanTarget, desired, owner,
                    out preflightReason))
                    return;

                Log("Equipping Loadout for Titans after candidate preflight");
                if (!TryContextSwap(LockType.Titan, desired,
                    titanContext, owner))
                    return;
                // Gold/production specials have no value if the temporary set can no
                // longer kill the Titan. Require the authoritative post-swap combat
                // predicate, including native T6+ autokill and special-item checks.
                var intendedBeast = Main.Character.adventureController.hasBeastMode()
                                    && (owner == MutationOwner.Autopilot || Settings.BeastMode);
                if (!ZoneHelpers.TitanLoadoutReady(titanTarget, intendedBeast))
                {
                    var restored = RestoreGear();
                    if (restored) ReleaseLock();
                    Main.LogAction(restored ? "HOLD" : "REJECTED", restored
                        ? "Titan loadout postcondition changed after preflight; exact rollback confirmed and candidate held"
                        : "Temporary Titan loadout failed combat admission and exact rollback FAILED");
                    if (restored)
                        HoldTitanPreflight(titanTarget, "postcondition-changed",
                            "Titan candidate held because native post-swap admission changed after preflight");
                    return;
                }
                if (goldContext)
                {
                    if (ts.MoneyTarget >= 0)
                    {
                        PendingTitanMoneyTarget = ts.MoneyTarget;
                        _pendingTitanKillsBefore = killsBefore;
                        Main.LogAction("GEAR", "Verified Titan gold loadout for "
                                                     + GameNames.Titan(Main.Character, ts.MoneyTarget)
                                                     + "; completion remains pending until a confirmed kill");
                    }
                    else
                    {
                        if (RestoreGear()) ReleaseLock();
                        Main.LogAction("REJECTED", "Titan gold loadout was not verified; money event remains pending");
                    }
                }
            }
        }

        internal static void CompleteTitanFight(bool playerDied, int fightZone)
        {
            if (PendingTitanMoneyTarget < 0) return;
            var expectedZone = ZoneHelpers.TitanZones[PendingTitanMoneyTarget];
            if (fightZone != expectedZone)
            {
                Main.LogAction("HOLD", "Observed a different Titan zone while money target "
                    + GameNames.Titan(Main.Character, PendingTitanMoneyTarget)
                    + " remained pending; no completion was recorded");
                return;
            }
            if (!playerDied)
            {
                if (!PendingTitanKillConfirmed())
                {
                    Main.LogAction("REJECTED", "Observed Titan enemy-clear without the exact target native kill delta; money event remains uncompleted");
                    ClearPendingTitanMoney();
                    return;
                }
                var done = Settings.TitanMoneyDone.ToArray();
                if (PendingTitanMoneyTarget < done.Length)
                {
                    done[PendingTitanMoneyTarget] = true;
                    Settings.TitanMoneyDone = done;
                }
                Settings.DoGoldSwap = false;
                Main.LogAction("PROGRESSION", GameNames.Titan(Main.Character, PendingTitanMoneyTarget)
                                                       + " money event completed [confirmed enemy kill]");
                ClearPendingTitanMoney();
                return;
            }
            Main.LogAction("DEATH", "Titan gold attempt failed; money event remains pending for retry");
            ClearPendingTitanMoney();
        }

        private static bool PendingTitanKillConfirmed()
        {
            return PendingTitanMoneyTarget >= 0 && _pendingTitanKillsBefore >= 0
                   && ZoneHelpers.TitanKillCount(PendingTitanMoneyTarget) > _pendingTitanKillsBefore;
        }

        private static void ClearPendingTitanMoney()
        {
            PendingTitanMoneyTarget = -1;
            _pendingTitanKillsBefore = -1;
        }

        private static bool IsLoadoutEquipped(IEnumerable<int> ids)
        {
            if (ids == null) return false;
            var inv = Main.Character.inventory;
            var equipped = new List<int> {inv.head.id, inv.chest.id, inv.legs.id, inv.boots.id, inv.weapon.id};
            if (Controller.weapon2Unlocked()) equipped.Add(inv.weapon2.id);
            equipped.AddRange(inv.accs.Select(x => x.id));
            return ids.Where(x => x > 0).Distinct().All(equipped.Contains);
        }

        internal static bool TryYggdrasilSwap()
        {
            if (!CanAcquireOrHasLock(LockType.Yggdrasil))
                return false;

            if (CurrentLock == LockType.Yggdrasil)
                return true;

            Log("Equipping Yggdrasil Loadout");
            return TryContextSwap(LockType.Yggdrasil, Settings.YggdrasilLoadout, "yggdrasil");
        }

        internal static bool TryMoneyPitSwap()
        {
            if (!CanAcquireOrHasLock(LockType.MoneyPit))
                return false;

            if (CurrentLock == LockType.MoneyPit)
                return true;

            Log("Equipping Money Pit");
            return TryContextSwap(LockType.MoneyPit, Settings.MoneyPitLoadout, "money-pit");
        }

        internal static bool TryGoldDropSwap()
        {
            if (!CanAcquireOrHasLock(LockType.Gold))
                return false;

            //We already hold the lock so just return true
            if (CurrentLock == LockType.Gold)
            {
                return true;
            }

            Log("Equipping Gold Loadout");
            return TryContextSwap(LockType.Gold, Settings.GoldDropLoadout, "gold");
        }

        private sealed class ExactLoadout
        {
            internal Equipment Head;
            internal Equipment Chest;
            internal Equipment Legs;
            internal Equipment Boots;
            internal Equipment Weapon;
            internal Equipment Weapon2;
            internal readonly List<Equipment> Accessories = new List<Equipment>();
        }

        private static MutationOwner OwnerForLock(LockType lockType)
        {
            return ExecutionSafety.OwnerFor(MutationClassFor(lockType));
        }

        private static ExactLoadout ResolveExactLoadout(int[] configuredIds, string context,
            int titanTarget, out string reason, bool nativeAutokill = false)
        {
            reason = string.Empty;
            var desired = configuredIds != null && configuredIds.Length > 0
                ? BuildConfiguredExactLoadout(configuredIds, context)
                : BuildDynamicExactLoadout(context);
            desired = EnforceTitanRequirements(desired, titanTarget, nativeAutokill);
            if (desired != null && ValidateExactLoadout(desired)) return desired;
            reason = "no complete, legal exact-reference " + context
                     + " loadout (including required Titan puzzle items) could be resolved";
            return null;
        }

        private static bool TryContextSwap(LockType lockType, int[] configuredIds, string context,
            int titanTarget = -1)
        {
            if (!CanAcquireOrHasLock(lockType)) return false;
            if (CurrentLock == lockType) return true;
            var owner = OwnerForLock(lockType);
            if (!HasMutationLease(MutationClassFor(lockType), owner, context)) return false;
            string reason;
            var desired = ResolveExactLoadout(configuredIds, context, titanTarget, out reason);
            if (desired == null)
            {
                ExecutionSafety.ReportHold("loadout-unresolved:" + context,
                    "Loadout " + context + " held: " + reason);
                return false;
            }
            return TryContextSwap(lockType, desired, context, owner);
        }

        /*
        EVENT-DRIVEN TITAN PHYSICAL STAGE

        The execution controller has already disabled native autokill and selected every target
        version before calling this hook.  Resolution and candidate feasibility finish before the
        existing exact-reference transaction captures rollback state.  A stage ID is idempotent,
        but a second coordinator can never inherit or replace another Titan lock.
        */
        internal static TitanLoadoutStageResult StageTitanExecutionLoadout(
            TitanLoadoutStageRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (CurrentLock == LockType.Titan)
            {
                var sameStage = string.Equals(_activeTitanExecutionStageId, request.StageId,
                    StringComparison.Ordinal) && _savedExactLoadout != null
                    && _activeTitanExecutionExactLoadout != null
                    && MatchesExactLoadout(_activeTitanExecutionExactLoadout)
                    && !string.IsNullOrEmpty(_activeTitanExecutionPhysicalFingerprint);
                return new TitanLoadoutStageResult(sameStage,
                    _activeTitanExecutionStageId,
                    _activeTitanExecutionPhysicalFingerprint,
                    sameStage
                        ? "exact Titan execution loadout is already staged"
                        : "another Titan physical transaction owns the lock");
            }
            if (!CanAcquireOrHasLock(LockType.Titan))
                return new TitanLoadoutStageResult(false, string.Empty, string.Empty,
                    "another physical loadout objective owns the lock");
            if (!HasMutationLease(MutationClass.TitanLoadout, MutationOwner.Autopilot,
                    "Titan execution prestage"))
                return new TitanLoadoutStageResult(false, string.Empty, string.Empty,
                    "Titan execution mutation lease is unavailable");

            var ids = request.TitanIds();
            var configured = request.ConfiguredItemIds();
            var target = ids[ids.Length - 1] - 1;
            var context = request.ValuesGold
                ? configured.Length == 0 ? "gold-titan" : "gold"
                : "titan";
            string resolveReason;
            var desired = ResolveExactLoadout(configured, context, target,
                out resolveReason, true);
            if (desired == null)
                return new TitanLoadoutStageResult(false, string.Empty, string.Empty,
                    resolveReason);
            // The typed Titan controller deliberately stages the strongest legal loadout before
            // it asks the live native autokill predicate whether the due Titan is executable.
            // Applying the ordinary unattended-fight ETA admission here prevents that proof from
            // ever being observed: a weak pre-stage loadout rejects the staging atom, the root is
            // marked failed, and the same commitment retries forever.  Resolution above is exact
            // and requireStrongest=true; after this reversible swap, TitanExecutionManager either
            // executes a source-proven native kill or restores immediately and abandons the
            // commitment so a reset can proceed with the clock loss made explicit.
            if (!TryContextSwap(LockType.Titan, desired,
                    "Titan execution prestage", MutationOwner.Autopilot))
                return new TitanLoadoutStageResult(false, string.Empty, string.Empty,
                    "exact Titan execution physical transaction was rejected");

            _activeTitanExecutionStageId = request.StageId;
            _activeTitanExecutionExactLoadout = desired;
            _activeTitanExecutionPhysicalFingerprint = CandidateSignature(desired);
            return new TitanLoadoutStageResult(true,
                _activeTitanExecutionStageId,
                _activeTitanExecutionPhysicalFingerprint,
                "exact Titan execution loadout staged and verified by reference identity");
        }

        internal static TitanLoadoutStageResult CaptureTitanExecutionLoadout(string stageId)
        {
            if (string.IsNullOrEmpty(stageId))
                return new TitanLoadoutStageResult(false, string.Empty, string.Empty,
                    "stage ID is required");
            var satisfied = CurrentLock == LockType.Titan && _savedExactLoadout != null
                            && _activeTitanExecutionExactLoadout != null
                            && MatchesExactLoadout(_activeTitanExecutionExactLoadout)
                            && string.Equals(stageId, _activeTitanExecutionStageId,
                                StringComparison.Ordinal)
                            && !string.IsNullOrEmpty(
                                _activeTitanExecutionPhysicalFingerprint);
            return new TitanLoadoutStageResult(satisfied,
                _activeTitanExecutionStageId,
                _activeTitanExecutionPhysicalFingerprint,
                satisfied
                    ? "exact Titan execution stage is still physically owned"
                    : "requested Titan execution stage is not physically owned");
        }

        internal static TitanLoadoutStageResult RestoreTitanExecutionLoadout(string stageId)
        {
            var captured = CaptureTitanExecutionLoadout(stageId);
            if (!captured.Satisfied) return captured;
            if (!HasMutationLease(MutationClass.TitanLoadout,
                    MutationOwner.Autopilot, "Titan execution restoration"))
                return new TitanLoadoutStageResult(false, stageId,
                    captured.PhysicalFingerprint,
                    "Titan execution restoration mutation lease is unavailable");
            if (!Main.HasExecutableAllocationOwner)
                return new TitanLoadoutStageResult(false, stageId,
                    captured.PhysicalFingerprint,
                    "no exclusive allocation profile can restore reclaimed resources");
            var restored = ApplyExactLoadout(_savedExactLoadout,
                MutationClass.TitanLoadout, MutationOwner.Autopilot);
            Main.LogAction(restored ? "GEAR" : "REJECTED", restored
                ? "Restored the exact pre-Titan execution physical loadout [confirmed by reference identity]"
                : "Could not verify restoration of the exact pre-Titan execution physical loadout");
            if (!restored)
                return new TitanLoadoutStageResult(false, stageId,
                    captured.PhysicalFingerprint,
                    "exact pre-Titan physical loadout could not be verified after restoration");
            _savedExactLoadout = null;
            _activeTitanExecutionExactLoadout = null;
            _activeTitanExecutionStageId = string.Empty;
            _activeTitanExecutionPhysicalFingerprint = string.Empty;
            ReleaseLock();
            return new TitanLoadoutStageResult(true, stageId,
                captured.PhysicalFingerprint,
                "exact pre-Titan physical loadout restored; Titan lock released");
        }

        /*
        PHYSICAL LOADOUT TRANSACTION

        Admission and exact-reference resolution must finish before this method captures rollback
        state or removes any resource allocation. One sticky class/owner lease covers the forward
        swap and any rollback. Success means every equipped object is reference-equal to the plan;
        failure is a true rejected native mutation only after a forward attempt has begun.
        */
        private static bool TryContextSwap(LockType lockType, ExactLoadout desired,
            string context, MutationOwner owner)
        {
            if (!CanAcquireOrHasLock(lockType) || CurrentLock == lockType) return CurrentLock == lockType;
            if (!HasMutationLease(MutationClassFor(lockType), owner, context)) return false;
            if (!Main.HasExecutableAllocationOwner)
            {
                ExecutionSafety.ReportHold("loadout-no-allocation-owner:" + context,
                    "Loadout " + context + " held because no exclusive allocation profile can restore reclaimed resources");
                return false;
            }
            _savedExactLoadout = CaptureExactLoadout();
            AcquireLock(lockType);
            if (ApplyExactLoadout(desired, MutationClassFor(lockType), owner))
            {
                Main.LogAction("GEAR", "Equipped exact-reference " + context
                                           + " loadout [confirmed by every physical slot]");
                return true;
            }
            var restored = ApplyExactLoadout(_savedExactLoadout,
                MutationClassFor(lockType), owner);
            if (restored)
            {
                _savedExactLoadout = null;
                ReleaseLock();
            }
            Main.LogAction("REJECTED", restored
                ? "Rejected " + context + " loadout and verified exact rollback"
                : "Rejected " + context + " loadout; exact rollback verification FAILED");
            return false;
        }

        private static bool TitanCandidatePreflight(int titanTarget, ExactLoadout desired,
            MutationOwner owner, out string reason)
        {
            reason = string.Empty;
            var intendedBeast = Main.Character.adventureController.hasBeastMode()
                                && (owner == MutationOwner.Autopilot || Settings.BeastMode);
            var candidateSignature = CandidateSignature(desired);
            var stateSignature = ZoneHelpers.TitanStateSignature(titanTarget, intendedBeast)
                                 + "|gear=" + candidateSignature;
            TitanPreflightHold prior;
            if (TitanPreflightHolds.TryGetValue(titanTarget, out prior)
                && string.Equals(prior.StateSignature, stateSignature, StringComparison.Ordinal)
                && DateTime.UtcNow < prior.RetryAfterUtc)
            {
                reason = prior.Reason;
                return false;
            }

            double attack;
            double defense;
            double hp;
            ProjectAdventureStats(desired, out attack, out defense, out hp);
            var hasApathy = DesiredItems(desired).Any(x => x.id == 135 && x.level >= 100);
            var readiness = ZoneHelpers.EvaluateTitanCandidate(titanTarget, attack, defense, hp,
                intendedBeast, hasApathy, MatchesExactLoadout(desired));
            if (!readiness.Ready)
            {
                reason = readiness.Reason;
                HoldTitanPreflight(titanTarget, stateSignature, readiness.Reason);
                return false;
            }
            TitanPreflightHolds.Remove(titanTarget);
            return true;
        }

        private static void HoldTitanPreflight(int titanTarget, string stateSignature, string reason)
        {
            var seconds = Main.CurrentAutopilotConfig == null
                ? 15 : Math.Max(1, Main.CurrentAutopilotConfig.TitanPreflightBackoffSeconds);
            TitanPreflightHolds[titanTarget] = new TitanPreflightHold
            {
                StateSignature = stateSignature ?? string.Empty,
                Reason = reason ?? "Titan candidate is not feasible",
                RetryAfterUtc = DateTime.UtcNow.AddSeconds(seconds)
            };
            ExecutionSafety.ReportHold("titan-preflight:" + titanTarget + ":" + stateSignature,
                GameNames.Titan(Main.Character, titanTarget) + " loadout held before mutation: " + reason,
                seconds);
        }

        private static IEnumerable<Equipment> DesiredItems(ExactLoadout desired)
        {
            if (desired == null) return Enumerable.Empty<Equipment>();
            var result = new List<Equipment>
                {desired.Head, desired.Chest, desired.Legs, desired.Boots, desired.Weapon};
            if (desired.Weapon2 != null) result.Add(desired.Weapon2);
            result.AddRange(desired.Accessories);
            return result.Where(x => x != null && x.id > 0);
        }

        private static string CandidateSignature(ExactLoadout desired)
        {
            return string.Join(",", DesiredItems(desired).Select(x =>
                x.id + ":" + x.level + ":" + x.curAttack + ":" + x.curDefense).ToArray());
        }

        private static void ProjectAdventureStats(ExactLoadout desired, out double attack,
            out double defense, out double hp)
        {
            var c = Main.Character;
            var controller = c.inventoryController;
            var primary = new[] {desired.Head, desired.Chest, desired.Legs, desired.Boots,
                desired.Weapon}.Concat(desired.Accessories)
                .Where(x => x != null && x.id > 0).ToList();
            var attackItems = primary.Sum(x => (double)controller.equipAttackBonus(x));
            var defenseItems = primary.Sum(x => (double)controller.equipDefenseBonus(x));
            if (desired.Weapon2 != null && desired.Weapon2.id > 0)
            {
                attackItems += controller.equipAttackBonus(desired.Weapon2) * controller.weapon2Factor();
                defenseItems += controller.equipDefenseBonus(desired.Weapon2) * controller.weapon2Factor();
            }
            var currentAttackItems = Math.Max(0.0, controller.attackBonus());
            var currentDefenseItems = Math.Max(0.0, controller.defenseBonus());
            var currentAttackNumerator = Math.Max(1e-9,
                c.adventure.attack + controller.cubePower() + currentAttackItems);
            var currentDefenseNumerator = Math.Max(1e-9,
                c.adventure.defense + controller.cubeToughness() + currentDefenseItems);
            var currentHpNumerator = Math.Max(1e-9,
                c.adventure.maxHP + 3.0 * (controller.cubePower() + currentAttackItems));
            attack = c.totalAdvAttack()
                     * (c.adventure.attack + controller.cubePower() + attackItems)
                     / currentAttackNumerator;
            defense = c.totalAdvDefense()
                      * (c.adventure.defense + controller.cubeToughness() + defenseItems)
                      / currentDefenseNumerator;
            hp = c.totalAdvHP()
                 * (c.adventure.maxHP + 3.0 * (controller.cubePower() + attackItems))
                 / currentHpNumerator;
        }

        private static ExactLoadout EnforceTitanRequirements(ExactLoadout desired,
            int titanTarget, bool nativeAutokill)
        {
            if (desired == null || titanTarget < 0) return desired;
            // Apathy and Glop are bespoke manual-AI prerequisites. Native autokill bypasses
            // those state machines and must not burn a physical slot on either item.
            if (nativeAutokill) return desired;
            var needsApathy = titanTarget == 3;
            if (titanTarget == 11)
            {
                var field = Main.Character.adventure.GetType().GetField("titan12Version",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                needsApathy = field != null && (int)field.GetValue(Main.Character.adventure) >= 3;
            }
            if (!needsApathy) return desired;
            var ring = AllPhysicalEquipment().Where(x => x.id == 135 && x.level >= 100)
                .OrderByDescending(x => ContextItemScore(x, "titan")).FirstOrDefault();
            if (ring == null) return null;
            return OverlaySelected(desired, new[] {ring});
        }

        private static ExactLoadout CaptureExactLoadout()
        {
            var c = Main.Character;
            var inv = c.inventory;
            var result = new ExactLoadout
            {
                Head = inv.head, Chest = inv.chest, Legs = inv.legs, Boots = inv.boots,
                Weapon = inv.weapon,
                Weapon2 = c.inventoryController.weapon2Unlocked() ? inv.weapon2 : null
            };
            result.Accessories.AddRange(inv.accs.Take(Math.Min(inv.accs.Count,
                Math.Max(0, c.inventoryController.accessorySpaces()))));
            return result;
        }

        private static List<Equipment> AllPhysicalEquipment()
        {
            var inv = Main.Character.inventory;
            var result = new List<Equipment> {inv.head, inv.chest, inv.legs, inv.boots, inv.weapon};
            if (Main.Controller.weapon2Unlocked()) result.Add(inv.weapon2);
            result.AddRange(inv.accs);
            result.AddRange(inv.inventory);
            return result.Where(x => x != null && x.id > 0 && x.isEquipment()).Distinct().ToList();
        }

        private static ExactLoadout BuildConfiguredExactLoadout(IEnumerable<int> ids, string context)
        {
            var current = CaptureExactLoadout();
            var all = AllPhysicalEquipment();
            var selected = new List<Equipment>();
            foreach (var id in ids.Where(x => x > 0).Distinct())
            {
                var item = all.Where(x => x.id == id)
                    .OrderByDescending(x => ContextItemScore(x, context)).ThenByDescending(x => x.level)
                    .FirstOrDefault();
                if (item == null)
                {
                    ExecutionSafety.ReportHold("configured-loadout-missing:" + context + ":" + id,
                        "Configured " + context + " item ID " + id
                        + " has no owned physical copy; loadout held before mutation");
                    return null;
                }
                selected.Add(item);
            }
            return OverlaySelected(current, selected);
        }

        private static ExactLoadout BuildDynamicExactLoadout(string context)
        {
            var current = CaptureExactLoadout();
            var candidates = AllPhysicalEquipment()
                .Where(x => ContextItemScore(x, context) > 0.0)
                .OrderByDescending(x => ContextItemScore(x, context)).ThenByDescending(x => x.level)
                .ToList();
            return OverlaySelected(current, candidates);
        }

        private static ExactLoadout OverlaySelected(ExactLoadout current, IEnumerable<Equipment> ordered)
        {
            var chosen = ordered.Where(x => x != null && x.id > 0).ToList();
            var result = new ExactLoadout
            {
                Head = BestFixed(chosen, current.Head, part.Head),
                Chest = BestFixed(chosen, current.Chest, part.Chest),
                Legs = BestFixed(chosen, current.Legs, part.Legs),
                Boots = BestFixed(chosen, current.Boots, part.Boots)
            };
            var usedIds = new HashSet<int>(new[] {result.Head, result.Chest, result.Legs, result.Boots}
                .Where(x => x != null && x.id > 0).Select(x => x.id));
            var weaponCount = Main.Controller.weapon2Unlocked() ? 2 : 1;
            var weapons = chosen.Where(x => x.type == part.Weapon && !usedIds.Contains(x.id))
                .Concat(new[] {current.Weapon, current.Weapon2}.Where(x => x != null && x.id > 0))
                .Where(x => usedIds.Add(x.id)).Take(weaponCount).ToList();
            while (weapons.Count < weaponCount)
                weapons.Add(weaponCount == 2 && weapons.Count == 1 ? current.Weapon2 : current.Weapon);
            result.Weapon = weapons[0];
            result.Weapon2 = weaponCount > 1 ? weapons[1] : null;

            var accessoryCount = current.Accessories.Count;
            var accessories = chosen.Where(x => x.type == part.Accessory && !usedIds.Contains(x.id))
                .Concat(current.Accessories.Where(x => x != null))
                .Where(x => x.id <= 0 || usedIds.Add(x.id)).Take(accessoryCount).ToList();
            foreach (var fallback in current.Accessories)
            {
                if (accessories.Count >= accessoryCount) break;
                if (!accessories.Any(x => ReferenceEquals(x, fallback))) accessories.Add(fallback);
            }
            if (accessories.Count != accessoryCount) return null;
            result.Accessories.AddRange(accessories);
            return result;
        }

        private static Equipment BestFixed(IEnumerable<Equipment> selected, Equipment current, part type)
        {
            return selected.FirstOrDefault(x => x.type == type) ?? current;
        }

        private static double ContextItemScore(Equipment item, string context)
        {
            if (item == null || item.id <= 0) return 0.0;
            var c = Main.Character;
            var controller = c.inventoryController;
            if (context == "yggdrasil")
                return Math.Max(0.0, controller.equipSpecBonus(specType.Seeds, item));
            if (context == "gold")
                return Math.Max(0.0, controller.equipSpecBonus(specType.GoldDropAmount, item)
                    + controller.equipSpecBonus(specType.GoldDrop2, item)
                    + controller.equipSpecBonus(specType.GoldDropRNG, item));
            if (context == "gold-titan")
            {
                // A dynamic money set is a constrained combat plan: retain the
                // strongest armor/weapons that make the kill feasible, then spend
                // accessory slots on the highest native Gold specials. The exact
                // target combat predicate is still required after the physical swap.
                if (item.type != part.Accessory)
                {
                    var combatAttack = Math.Max(0.0, controller.equipAttackBonus(item));
                    var combatDefense = Math.Max(0.0, controller.equipDefenseBonus(item));
                    return 2.0 * Math.Log(1.0 + combatAttack) + Math.Log(1.0 + combatDefense);
                }
                return Math.Max(0.0, controller.equipSpecBonus(specType.GoldDropAmount, item)
                    + controller.equipSpecBonus(specType.GoldDrop2, item)
                    + controller.equipSpecBonus(specType.GoldDropRNG, item))
                       + 1e-6 * (Math.Max(0.0, controller.equipAttackBonus(item))
                                 + Math.Max(0.0, controller.equipDefenseBonus(item)));
            }
            if (context == "money-pit")
            {
                var maxxed = item.id < c.inventory.itemList.itemMaxxed.Count
                             && c.inventory.itemList.itemMaxxed[item.id];
                return item.level >= 100 || maxxed ? 0.0
                    : 1000000.0 + item.bossRequired * 1000.0 - item.level;
            }
            var attack = Math.Max(0.0, controller.equipAttackBonus(item));
            var defense = Math.Max(0.0, controller.equipDefenseBonus(item));
            return 2.0 * Math.Log(1.0 + attack) + Math.Log(1.0 + defense);
        }

        private static bool ApplyExactLoadout(ExactLoadout desired,
            MutationClass mutationClass, MutationOwner owner, bool restoreAllocations = true)
        {
            if (!HasMutationLease(mutationClass, owner, "physical transaction"))
                return false;
            var c = Main.Character;
            if (c == null || desired == null || Controller == null || Controller.midDrag
                || c.bossController.isFighting || c.bossController.nukeBoss)
                return false;
            if (!ValidateExactLoadout(desired)) return false;
            c.removeAllEnergy();
            c.removeMostMagic();
            c.removeAllRes3();
            try
            {
                if (!ExecuteExactLoadout(desired) || !MatchesExactLoadout(desired))
                    return false;
                Controller.updateBonuses();
                Controller.updateInventory();
                if (restoreAllocations) Main.RestoreAllocationsAfterGearSwap();
                return MatchesExactLoadout(desired);
            }
            catch (Exception ex)
            {
                Main.LogAction("REJECTED", "Exact-reference loadout transaction threw "
                                           + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static bool ValidateExactLoadout(ExactLoadout desired)
        {
            var c = Main.Character;
            var inv = c.inventory;
            if (desired.Head == null || desired.Chest == null || desired.Legs == null
                || desired.Boots == null || desired.Weapon == null
                || c.inventoryController.weapon2Unlocked() && desired.Weapon2 == null)
                return false;
            if (desired.Head.id > 0 && desired.Head.type != part.Head
                || desired.Chest.id > 0 && desired.Chest.type != part.Chest
                || desired.Legs.id > 0 && desired.Legs.type != part.Legs
                || desired.Boots.id > 0 && desired.Boots.type != part.Boots
                || desired.Weapon.id > 0 && desired.Weapon.type != part.Weapon
                || desired.Weapon2 != null && desired.Weapon2.id > 0 && desired.Weapon2.type != part.Weapon
                || desired.Accessories.Any(x => x == null || x.id > 0 && x.type != part.Accessory))
                return false;
            var allDesired = new List<Equipment>
                {desired.Head, desired.Chest, desired.Legs, desired.Boots, desired.Weapon};
            if (desired.Weapon2 != null) allDesired.Add(desired.Weapon2);
            allDesired.AddRange(desired.Accessories);
            var nonEmpty = allDesired.Where(x => x != null && x.id > 0).ToList();
            if (nonEmpty.Select(x => x.id).Distinct().Count() != nonEmpty.Count
                || nonEmpty.Distinct().Count() != nonEmpty.Count)
                return false;
            var physical = AllPhysicalEquipment();
            if (nonEmpty.Any(x => !physical.Any(y => ReferenceEquals(x, y)))) return false;
            return desired.Accessories.Count == Math.Min(inv.accs.Count,
                Math.Max(0, c.inventoryController.accessorySpaces()));
        }

        private static bool ExecuteExactLoadout(ExactLoadout desired)
        {
            var inv = Main.Character.inventory;
            if (!SwapFixedExact(inv, desired.Head, () => inv.head, inv.swapHead)
                || !SwapFixedExact(inv, desired.Chest, () => inv.chest, inv.swapChest)
                || !SwapFixedExact(inv, desired.Legs, () => inv.legs, inv.swapLegs)
                || !SwapFixedExact(inv, desired.Boots, () => inv.boots, inv.swapBoots))
                return false;
            if (!ReferenceEquals(inv.weapon, desired.Weapon))
            {
                if (ReferenceEquals(inv.weapon2, desired.Weapon)) inv.swapWeapons();
                else
                {
                    var slot = InventoryIndex(inv, desired.Weapon);
                    if (slot < 0) return false;
                    inv.item2 = slot;
                    inv.swapWeapon();
                }
            }
            if (desired.Weapon2 != null && !ReferenceEquals(inv.weapon2, desired.Weapon2))
            {
                if (ReferenceEquals(inv.weapon, desired.Weapon2)) inv.swapWeapons();
                else
                {
                    var slot = InventoryIndex(inv, desired.Weapon2);
                    if (slot < 0) return false;
                    inv.item2 = slot;
                    inv.swapWeapon2();
                }
            }
            for (var i = 0; i < desired.Accessories.Count; i++)
            {
                var target = desired.Accessories[i];
                if (ReferenceEquals(inv.accs[i], target)) continue;
                var equipped = inv.accs.FindIndex(x => ReferenceEquals(x, target));
                if (equipped >= 0) inv.swapAccs(i, equipped);
                else
                {
                    var slot = InventoryIndex(inv, target);
                    if (slot < 0) return false;
                    inv.swapAccWithItem(i, slot);
                }
            }
            return true;
        }

        private static bool SwapFixedExact(Inventory inv, Equipment desired,
            Func<Equipment> current, Action swap)
        {
            if (ReferenceEquals(current(), desired)) return true;
            var slot = InventoryIndex(inv, desired);
            if (slot < 0) return false;
            inv.item2 = slot;
            swap();
            return ReferenceEquals(current(), desired);
        }

        private static int InventoryIndex(Inventory inv, Equipment target)
        {
            for (var i = 0; i < inv.inventory.Count; i++)
                if (ReferenceEquals(inv.inventory[i], target)) return i;
            return -1;
        }

        private static bool MatchesExactLoadout(ExactLoadout desired)
        {
            var inv = Main.Character.inventory;
            if (!ReferenceEquals(inv.head, desired.Head) || !ReferenceEquals(inv.chest, desired.Chest)
                || !ReferenceEquals(inv.legs, desired.Legs) || !ReferenceEquals(inv.boots, desired.Boots)
                || !ReferenceEquals(inv.weapon, desired.Weapon)
                || desired.Weapon2 != null && !ReferenceEquals(inv.weapon2, desired.Weapon2))
                return false;
            return desired.Accessories.Select((x, i) => ReferenceEquals(inv.accs[i], x)).All(x => x);
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

        private static bool CanResolveLoadout(int[] gearIds, bool moneyPit)
        {
            if (gearIds == null || gearIds.Length == 0)
                return true;
            var missing = gearIds.Where(itemId => FindItemSlot(itemId, moneyPit) == null).Distinct().ToArray();
            if (missing.Length == 0)
                return true;
            Main.LogAction("REJECTED", "Loadout unavailable; missing item IDs "
                                       + string.Join(",", missing.Select(x => x.ToString()).ToArray()));
            return false;
        }

        internal static void ChangeGear(int[] gearIds, bool moneyPit = false,
            MutationOwner? requestedOwner = null)
        {
            if (gearIds == null || gearIds.Length == 0)
                return;
            if (CurrentLock != LockType.None)
            {
                ExecutionSafety.ReportHold("profile-loadout-specialized-lock",
                    "Profile gear change held while specialized " + CurrentLock + " loadout owns physical slots");
                return;
            }
            if (requestedOwner == MutationOwner.User && !Main.HasExecutableAllocationOwner)
            {
                ExecutionSafety.ReportHold("manual-loadout-no-allocation-owner",
                    "Manual quick loadout held because no allocation profile can restore reclaimed resources");
                return;
            }
            var owner = requestedOwner ?? ExecutionSafety.OwnerFor(MutationClass.Loadout);
            if (!HasMutationLease(MutationClass.Loadout, owner, "profile gear change"))
                return;
            var context = moneyPit ? "money-pit" : "profile";
            var desired = BuildConfiguredExactLoadout(gearIds, context);
            if (desired == null || !ValidateExactLoadout(desired))
            {
                ExecutionSafety.ReportHold("profile-loadout-unresolved:" + context,
                    "Profile gear change held before mutation because not every configured physical item could be resolved");
                return;
            }
            if (MatchesExactLoadout(desired)) return;
            var before = CaptureExactLoadout();
            Log("Applying exact-reference " + context + " gear: "
                + string.Join(",", gearIds.Select(x => x.ToString()).ToArray()));
            if (ApplyExactLoadout(desired, MutationClass.Loadout, owner, false))
            {
                if (requestedOwner == MutationOwner.User)
                    Main.RestoreAllocationsAfterGearSwap();
                Main.LogAction("GEAR", "Applied exact-reference " + context
                    + " loadout [confirmed by every physical slot]");
                return;
            }
            var restored = ApplyExactLoadout(before, MutationClass.Loadout, owner, false);
            Main.LogAction("REJECTED", restored
                ? "Profile loadout native mutation failed; exact rollback confirmed"
                : "Profile loadout native mutation failed and exact rollback FAILED");
        }

        private static ih FindItemSlot(int id, bool moneyPit = false)
        {
            var inv = Main.Character.inventory;
            if (inv.head.id == id)
            {
                return inv.head.GetInventoryHelper(-1);
            }

            if (inv.chest.id == id)
            {
                return inv.chest.GetInventoryHelper(-2);
            }

            if (inv.legs.id == id)
            {
                return inv.legs.GetInventoryHelper(-3);
            }

            if (inv.boots.id == id)
            {
                return inv.boots.GetInventoryHelper(-4);
            }

            if (inv.weapon.id == id)
            {
                return inv.weapon.GetInventoryHelper(-5);
            }

            if (Controller.weapon2Unlocked())
            {
                if (inv.weapon2.id == id)
                {
                    return inv.weapon2.GetInventoryHelper(-6);
                }
            }

            for (var i = 0; i < inv.accs.Count; i++)
            {
                if (inv.accs[i].id == id)
                {
                    return inv.accs[i].GetInventoryHelper(i + 10000);
                }
            }

            var items = Main.Character.inventory.GetConvertedInventory()
                .Where(x => x.id == id && x.equipment.isEquipment()).ToArray();
            if (items.Length != 0)
            {
                return moneyPit ? items.OrderByDescending(x => x.level).First() : items.MaxItem();
            }

            return null;
        }

        private static void SaveCurrentLoadout()
        {
            var inv = Main.Character.inventory;
            var loadout = new List<int>
            {
                inv.head.id,
                inv.boots.id,
                inv.chest.id,
                inv.legs.id,
                inv.weapon.id
            };


            if (Main.Character.inventoryController.weapon2Unlocked())
            {
                loadout.Add(inv.weapon2.id);
            }

            for (var id = 10000; Controller.accessoryID(id) < Main.Character.inventory.accs.Count; ++id)
            {
                var index = Controller.accessoryID(id);
                loadout.Add(Main.Character.inventory.accs[index].id);
            }

            _savedLoadout = loadout.ToArray();
            Log($"Saved Loadout {string.Join(",", _savedLoadout.Select(x => x.ToString()).ToArray())}");
        }

        internal static void SaveTempLoadout()
        {
            var inv = Main.Character.inventory;
            var loadout = new List<int>
            {
                inv.head.id,
                inv.boots.id,
                inv.chest.id,
                inv.legs.id,
                inv.weapon.id
            };


            if (Main.Character.inventoryController.weapon2Unlocked())
            {
                loadout.Add(inv.weapon2.id);
            }

            for (var id = 10000; Controller.accessoryID(id) < Main.Character.inventory.accs.Count; ++id)
            {
                var index = Controller.accessoryID(id);
                loadout.Add(Main.Character.inventory.accs[index].id);
            }
            _tempLoadout = loadout.ToArray();
            Log($"Saved Loadout {string.Join(",", _tempLoadout.Select(x => x.ToString()).ToArray())}");
        }

        internal static void RestoreTempLoadout()
        {
            ChangeGear(_tempLoadout, false, MutationOwner.User);
        }

        //private static float GetSeedGain(Equipment e)
        //{
        //    var amount =
        //        typeof(ItemController).GetMethod("effectBonus", BindingFlags.NonPublic | BindingFlags.Instance);
        //    if (e.spec1Type == specType.Seeds)
        //    {
        //        var p = new object[] { e.spec1Cur, e.spec1Type };
        //        return (float)amount?.Invoke(Main.Controller, p);
        //    }
        //    if (e.spec2Type == specType.Seeds)
        //    {
        //        var p = new object[] { e.spec2Cur, e.spec2Type };
        //        return (float)amount?.Invoke(Main.Controller, p);
        //    }
        //    if (e.spec3Type == specType.Seeds)
        //    {
        //        var p = new object[] { e.spec3Cur, e.spec3Type };
        //        return (float)amount?.Invoke(Main.Controller, p);
        //    }

        //    return 0;
        //}
    }
}
