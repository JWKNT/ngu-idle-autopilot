using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using NGUInjector.AllocationProfiles;
using NGUInjector.Managers;

/*
FILE PURPOSE

Restore the two progression mutations that must precede higher-level strategy: generated resource
allocation and Fight Boss recovery. Both execute as children of the one-second, epoch-bound root.
The allocation child invokes only Energy/Magic/R3 allocation (never gear, diggers, Wandoos,
purchases, or Adventure) and proves exact capacity/idle bounds. The Boss child uses the source-
exact combat oracle and proves either an active native fight or synchronous Boss progression.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class CriticalProgressionOutcome
    {
        internal MutationResult Allocation;
        internal MutationResult Boss;
        internal MutationResult Adventure;
        internal MutationResult Inventory;

        internal bool Failed
        {
            get { return IsFailure(Allocation) || IsFailure(Boss) || IsFailure(Adventure)
                         || IsFailure(Inventory); }
        }

        internal string FailureReason
        {
            get
            {
                if (IsFailure(Allocation)) return "allocation: " + Allocation.Reason;
                if (IsFailure(Boss)) return "Fight Boss: " + Boss.Reason;
                if (IsFailure(Adventure)) return "Adventure: " + Adventure.Reason;
                return IsFailure(Inventory) ? "Inventory: " + Inventory.Reason : string.Empty;
            }
        }

        private static bool IsFailure(MutationResult result)
        {
            if (result == null) return false;
            return result.Kind == MutationResultKind.RejectedUnchanged
                   || result.Kind == MutationResultKind.Compensated
                   || result.Kind == MutationResultKind.Quarantined
                   || result.Kind == MutationResultKind.Indeterminate;
        }
    }

    internal static class ProgressionTransactions
    {
        internal static CriticalProgressionOutcome Execute(RootTransaction root,
            Character character, CustomAllocation allocation, AutopilotConfig config,
            AutopilotManager autopilot, CombatManager combat, QuestManager quests,
            InventoryManager inventory)
        {
            var outcome = new CriticalProgressionOutcome();
            if (root == null || root.IsClosed || character == null || config == null)
                return outcome;
            if (config.ManageAllocations && allocation != null)
                outcome.Allocation = root.ExecuteChild(
                    new ResourceAllocationIntent(character, allocation));
            if (!root.IsClosed && config.ManageBosses)
                outcome.Boss = root.ExecuteChild(new FightBossIntent(character));
            if (!root.IsClosed && EarlyAdventureIntent.IsEligible(character, config, quests))
                outcome.Adventure = root.ExecuteChild(
                    new EarlyAdventureIntent(character, config, autopilot, combat, quests));
            if (!root.IsClosed && config.ManageInventory && inventory != null)
                outcome.Inventory = root.ExecuteChild(
                    new InventoryMaintenanceIntent(character, inventory));
            LogNonSuccess("resource allocation", outcome.Allocation);
            LogNonSuccess("Fight Boss", outcome.Boss);
            LogNonSuccess("Adventure", outcome.Adventure);
            LogNonSuccess("Inventory", outcome.Inventory);
            return outcome;
        }

        private static void LogNonSuccess(string label, MutationResult result)
        {
            if (result == null || result.Kind == MutationResultKind.Committed
                || result.Kind == MutationResultKind.NoOpVerified
                || result.Kind == MutationResultKind.Held)
                return;
            Main.LogAction("REJECTED", label + " intent " + result.Kind + ": " + result.Reason);
        }
    }

    internal sealed class InventoryMaintenanceState
    {
        internal int CurSpaces;
        internal int MergePrefix;
        internal int Occupied;
        internal bool MidDrag;
        internal bool[] Maxxed;
        internal Dictionary<int, long> Contributions;
        internal string Fingerprint = string.Empty;
    }

    internal sealed class InventoryMaintenanceIntent :
        IMutationIntent<InventoryMaintenanceState, bool, InventoryMaintenanceState>
    {
        private readonly Character _character;
        private readonly InventoryManager _inventory;

        internal InventoryMaintenanceIntent(Character character, InventoryManager inventory)
        {
            _character = character;
            _inventory = inventory;
        }

        public string Id { get { return "progression.inventory-maintenance"; } }
        public MutationClass Class { get { return MutationClass.Inventory; } }
        public MutationRisk Risk { get { return MutationRisk.FiniteResource; } }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId { get { return "InventoryManager.conservative-maintenance.v1"; } }
        public bool Required { get { return false; } }
        public bool CanCompensate { get { return false; } }
        public bool CreatesNewEpoch { get { return false; } }
        public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

        public InventoryMaintenanceState CaptureBefore(MutationContext context) { return Capture(); }

        public PreconditionResult CheckPreconditions(MutationContext context,
            InventoryMaintenanceState before)
        {
            if (!Main.IsAutomationReady)
                return PreconditionResult.Hold("gameplay synchronization is not current");
            if (before == null || before.MidDrag)
                return PreconditionResult.Hold("inventory drag/controller state is not stable");
            if (before.CurSpaces <= before.MergePrefix || before.Occupied < 0)
                return PreconditionResult.Hold("ordinary inventory topology is unavailable");
            return PreconditionResult.Ready();
        }

        public bool Apply(MutationContext context, RootTransactionToken token,
            InventoryMaintenanceState before)
        {
            _inventory.RunConservativeMaintenance();
            return true;
        }

        public VerificationResult<InventoryMaintenanceState> Verify(MutationContext context,
            InventoryMaintenanceState before, MutationApplyObservation<bool> apply)
        {
            var after = Capture();
            if (!apply.ReturnedNormally || !apply.Value || after == null)
                return VerificationResult<InventoryMaintenanceState>.Failed(
                    "inventory maintenance did not return a complete post-state");
            if (after.MidDrag || after.CurSpaces != before.CurSpaces
                || after.MergePrefix != before.MergePrefix)
                return VerificationResult<InventoryMaintenanceState>.Failed(
                    "inventory maintenance changed capacity or left a drag active");
            var count = Math.Min(before.Maxxed.Length, after.Maxxed.Length);
            for (var id = 0; id < count; id++)
            {
                if (before.Maxxed[id] && !after.Maxxed[id])
                    return VerificationResult<InventoryMaintenanceState>.Failed(
                        "Item List MAXX regressed for ID " + id);
                if (before.Maxxed[id] || after.Maxxed[id]) continue;
                long beforeContribution;
                long afterContribution;
                before.Contributions.TryGetValue(id, out beforeContribution);
                after.Contributions.TryGetValue(id, out afterContribution);
                if (afterContribution < beforeContribution)
                    return VerificationResult<InventoryMaintenanceState>.Failed(
                        "un-MAXXED physical level contribution decreased for ID " + id);
            }
            return VerificationResult<InventoryMaintenanceState>.Satisfied(after,
                "capacity stable, Item List monotone, and un-MAXXED contributions preserved");
        }

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            InventoryMaintenanceState before, MutationApplyObservation<bool> apply)
        {
            return CompensationResult.NotSupported(
                "verified native merges/boosts/trash cannot be reversed by field rewriting");
        }

        public bool BeforeStateMatches(InventoryMaintenanceState a, InventoryMaintenanceState b)
        {
            return a != null && b != null && string.Equals(a.Fingerprint, b.Fingerprint,
                StringComparison.Ordinal);
        }

        public string FingerprintBefore(InventoryMaintenanceState state)
        {
            return state == null ? "missing" : state.Fingerprint;
        }

        public string FingerprintAfter(InventoryMaintenanceState state)
        {
            return state == null ? "missing" : state.Fingerprint;
        }

        private InventoryMaintenanceState Capture()
        {
            var inv = _character == null ? null : _character.inventory;
            var controller = _character == null ? null : _character.inventoryController;
            if (inv == null || controller == null || inv.inventory == null
                || inv.itemList == null || inv.itemList.itemMaxxed == null)
                return null;
            var all = new List<Equipment>();
            all.Add(inv.head); all.Add(inv.chest); all.Add(inv.legs); all.Add(inv.boots);
            all.Add(inv.weapon); all.Add(inv.weapon2);
            if (inv.accs != null) all.AddRange(inv.accs);
            all.AddRange(inv.inventory);
            if (inv.daycare != null) all.AddRange(inv.daycare);
            if (inv.macguffins != null) all.AddRange(inv.macguffins);
            var contributions = new Dictionary<int, long>();
            foreach (var item in all.Where(x => x != null && x.id > 0))
            {
                long current;
                contributions.TryGetValue(item.id, out current);
                contributions[item.id] = current + Math.Max(1, item.level + 1);
            }
            var slots = inv.inventory.Select((item, slot) => item == null
                    ? slot + ":null"
                    : slot + ":" + RuntimeHelpers.GetHashCode(item) + ":" + item.id
                      + ":" + item.level + ":" + item.removable)
                .ToArray();
            return new InventoryMaintenanceState
            {
                CurSpaces = controller.curSpaces(),
                MergePrefix = controller.totalInvMergeSlots(),
                Occupied = inv.inventory.Count(x => x != null && x.id > 0),
                MidDrag = controller.midDrag,
                Maxxed = inv.itemList.itemMaxxed.ToArray(),
                Contributions = contributions,
                Fingerprint = string.Join("|", slots)
                              + ":max=" + string.Join("", inv.itemList.itemMaxxed
                                  .Select(x => x ? "1" : "0").ToArray())
            };
        }
    }

    internal sealed class EarlyAdventureState
    {
        internal int Zone;
        internal int TargetZone;
        internal int FightType;
        internal bool FightInProgress;
        internal bool AutoAttacking;
        internal bool AutoKillTitans;
        internal int EnemyIdentity;
    }

    internal sealed class EarlyAdventureIntent :
        IMutationIntent<EarlyAdventureState, bool, EarlyAdventureState>
    {
        private readonly Character _character;
        private readonly AutopilotConfig _config;
        private readonly AutopilotManager _autopilot;
        private readonly CombatManager _combat;
        private readonly QuestManager _quests;

        internal EarlyAdventureIntent(Character character, AutopilotConfig config,
            AutopilotManager autopilot, CombatManager combat, QuestManager quests)
        {
            _character = character;
            _config = config;
            _autopilot = autopilot;
            _combat = combat;
            _quests = quests;
        }

        internal static bool IsEligible(Character c, AutopilotConfig config,
            QuestManager quests)
        {
            if (c == null || config == null || !config.ManageAdventure || quests == null
                || c.settings == null || c.settings.rebirthDifficulty != difficulty.normal)
                return false;
            try
            {
                // This adapter owns ordinary early-game collection only. Titan, puzzle,
                // endgame, and active Beast Quest routes retain their dedicated authorities.
                return ZoneHelpers.HighestAvailableTitan() < 0
                       && quests.IsQuesting() <= 0
                       && !InventoryManager.ExileAssemblyReady(c)
                       && !c.adventure.titan7Unlocked;
            }
            catch { return false; }
        }

        public string Id { get { return "progression.early-adventure"; } }
        public MutationClass Class { get { return MutationClass.Adventure; } }
        public MutationRisk Risk { get { return MutationRisk.Reversible; } }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId { get { return "CombatManager.verified-zone-api.v1"; } }
        public bool Required { get { return false; } }
        public bool CanCompensate { get { return false; } }
        public bool CreatesNewEpoch { get { return false; } }
        public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

        public EarlyAdventureState CaptureBefore(MutationContext context) { return Capture(); }

        public PreconditionResult CheckPreconditions(MutationContext context,
            EarlyAdventureState before)
        {
            if (!Main.IsAutomationReady)
                return PreconditionResult.Hold("gameplay synchronization is not current");
            if (!IsEligible(_character, _config, _quests))
                return PreconditionResult.Hold(
                    "early Adventure route no longer owns the live progression state");
            if (_autopilot == null || _combat == null)
                return PreconditionResult.Hold("Adventure strategy or combat controller is missing");
            return PreconditionResult.Ready();
        }

        public bool Apply(MutationContext context, RootTransactionToken token,
            EarlyAdventureState before)
        {
            return _autopilot.ControlAdventure(_combat, _quests);
        }

        public VerificationResult<EarlyAdventureState> Verify(MutationContext context,
            EarlyAdventureState before, MutationApplyObservation<bool> apply)
        {
            var after = Capture();
            if (!apply.ReturnedNormally || !apply.Value)
                return VerificationResult<EarlyAdventureState>.Failed(
                    "Adventure planner did not produce a routed target");
            if (after.TargetZone < 0 && after.Zone < 0 && !after.FightInProgress
                && !after.AutoAttacking)
                return VerificationResult<EarlyAdventureState>.Failed(
                    "Adventure planner returned without a target, zone, or combat state");
            if (after.TargetZone >= 0 && after.Zone >= 0
                && after.Zone != after.TargetZone && !after.FightInProgress)
                return VerificationResult<EarlyAdventureState>.Failed(
                    "native Adventure controller did not retain the selected target zone");
            return VerificationResult<EarlyAdventureState>.Satisfied(after,
                "Adventure target " + after.TargetZone + " is routed through verified combat state");
        }

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            EarlyAdventureState before, MutationApplyObservation<bool> apply)
        {
            return CompensationResult.NotSupported(
                "Adventure route changes settle through the native Safe Zone and combat controller");
        }

        public bool BeforeStateMatches(EarlyAdventureState a, EarlyAdventureState b)
        {
            return a != null && b != null && a.Zone == b.Zone && a.TargetZone == b.TargetZone
                   && a.FightType == b.FightType && a.FightInProgress == b.FightInProgress
                   && a.AutoAttacking == b.AutoAttacking
                   && a.AutoKillTitans == b.AutoKillTitans
                   && a.EnemyIdentity == b.EnemyIdentity;
        }

        public string FingerprintBefore(EarlyAdventureState state) { return Fingerprint(state); }
        public string FingerprintAfter(EarlyAdventureState state) { return Fingerprint(state); }

        private EarlyAdventureState Capture()
        {
            var enemy = _character.adventureController == null
                ? null : _character.adventureController.currentEnemy;
            return new EarlyAdventureState
            {
                Zone = _character.adventure == null ? -1 : _character.adventure.zone,
                TargetZone = _autopilot == null ? -1 : _autopilot.CurrentAdventureTargetZone,
                FightType = _autopilot == null ? -1 : _autopilot.CurrentAdventureFightType,
                FightInProgress = _character.adventureController != null
                                  && _character.adventureController.fightInProgress,
                AutoAttacking = _character.adventure != null
                                && _character.adventure.autoattacking,
                AutoKillTitans = _character.settings != null
                                 && _character.settings.autoKillTitans,
                EnemyIdentity = enemy == null ? 0
                    : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(enemy)
            };
        }

        private static string Fingerprint(EarlyAdventureState state)
        {
            return state == null ? "missing" : "zone=" + state.Zone + ":target="
                   + state.TargetZone + ":type=" + state.FightType + ":fight="
                   + state.FightInProgress + ":idle=" + state.AutoAttacking
                   + ":ak=" + state.AutoKillTitans + ":enemy=" + state.EnemyIdentity;
        }
    }

    internal sealed class ResourceAllocationState
    {
        internal long Energy;
        internal long IdleEnergy;
        internal long Magic;
        internal long IdleMagic;
        internal long Res3;
        internal long IdleRes3;
        internal long PlanVersion;
        internal string PlanFingerprint = string.Empty;
    }

    internal sealed class ResourceAllocationIntent :
        IMutationIntent<ResourceAllocationState, bool, ResourceAllocationState>
    {
        private readonly Character _character;
        private readonly CustomAllocation _allocation;

        internal ResourceAllocationIntent(Character character, CustomAllocation allocation)
        {
            _character = character;
            _allocation = allocation;
        }

        public string Id { get { return "progression.resource-allocation"; } }
        public MutationClass Class { get { return MutationClass.Allocation; } }
        public MutationRisk Risk { get { return MutationRisk.Reversible; } }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId { get { return "CustomAllocation.resource-only.v1"; } }
        public bool Required { get { return false; } }
        public bool CanCompensate { get { return false; } }
        public bool CreatesNewEpoch { get { return false; } }
        public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

        public ResourceAllocationState CaptureBefore(MutationContext context) { return Capture(); }

        public PreconditionResult CheckPreconditions(MutationContext context,
            ResourceAllocationState before)
        {
            if (!Main.IsAutomationReady)
                return PreconditionResult.Hold("gameplay synchronization is not current");
            if (before.PlanVersion <= 0 || string.IsNullOrEmpty(before.PlanFingerprint))
                return PreconditionResult.Hold("no verified generated allocation plan is installed");
            return Valid(before) ? PreconditionResult.Ready()
                : PreconditionResult.Hold("resource snapshot is outside exact Int64 bounds");
        }

        public bool Apply(MutationContext context, RootTransactionToken token,
            ResourceAllocationState before)
        {
            // The old DoAllocations aggregate also mutates gear, diggers, Wandoos, and NGU mode.
            // Those remain separate transaction classes; this child is resource-only.
            var originalInput = _character.energyMagicPanel.energyMagicInput;
            try
            {
                _allocation.AllocateEnergy();
                _allocation.AllocateMagic();
                _allocation.AllocateR3();
                return true;
            }
            finally
            {
                _character.energyMagicPanel.energyRequested.text = originalInput.ToString();
                _character.energyMagicPanel.validateInput();
            }
        }

        public VerificationResult<ResourceAllocationState> Verify(MutationContext context,
            ResourceAllocationState before, MutationApplyObservation<bool> apply)
        {
            var after = Capture();
            if (!apply.ReturnedNormally || !apply.Value)
                return VerificationResult<ResourceAllocationState>.Failed(
                    "resource allocation did not return normally");
            if (!Valid(after))
                return VerificationResult<ResourceAllocationState>.Failed(
                    "resource allocation produced an out-of-bounds idle pool");
            if (before.Energy != after.Energy || before.Magic != after.Magic
                || before.Res3 != after.Res3)
                return VerificationResult<ResourceAllocationState>.Failed(
                    "resource allocation changed a permanent resource capacity");
            if (before.PlanVersion != after.PlanVersion
                || !string.Equals(before.PlanFingerprint, after.PlanFingerprint,
                    StringComparison.Ordinal))
                return VerificationResult<ResourceAllocationState>.Failed(
                    "generated allocation plan changed during its native sweep");
            return VerificationResult<ResourceAllocationState>.Satisfied(after,
                "resource capacities conserved and idle pools remain in exact bounds");
        }

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            ResourceAllocationState before, MutationApplyObservation<bool> apply)
        {
            return CompensationResult.NotSupported(
                "exact rollback needs the complete per-controller allocation vector");
        }

        public bool BeforeStateMatches(ResourceAllocationState a, ResourceAllocationState b)
        {
            return a != null && b != null && a.Energy == b.Energy
                   && a.IdleEnergy == b.IdleEnergy && a.Magic == b.Magic
                   && a.IdleMagic == b.IdleMagic && a.Res3 == b.Res3
                   && a.IdleRes3 == b.IdleRes3 && a.PlanVersion == b.PlanVersion
                   && string.Equals(a.PlanFingerprint, b.PlanFingerprint,
                       StringComparison.Ordinal);
        }

        public string FingerprintBefore(ResourceAllocationState state) { return Fingerprint(state); }
        public string FingerprintAfter(ResourceAllocationState state) { return Fingerprint(state); }

        private ResourceAllocationState Capture()
        {
            return new ResourceAllocationState
            {
                Energy = _character.curEnergy,
                IdleEnergy = _character.idleEnergy,
                Magic = _character.magic.curMagic,
                IdleMagic = _character.magic.idleMagic,
                Res3 = _character.res3.curRes3,
                IdleRes3 = _character.res3.idleRes3,
                PlanVersion = _allocation.InstalledPlanVersion,
                PlanFingerprint = _allocation.InstalledPlanFingerprint
            };
        }

        private static bool Valid(ResourceAllocationState state)
        {
            return state != null && state.Energy >= 0 && state.Magic >= 0 && state.Res3 >= 0
                   && state.IdleEnergy >= 0 && state.IdleEnergy <= state.Energy
                   && state.IdleMagic >= 0 && state.IdleMagic <= state.Magic
                   && state.IdleRes3 >= 0 && state.IdleRes3 <= state.Res3;
        }

        private static string Fingerprint(ResourceAllocationState state)
        {
            return state == null ? "missing" : state.PlanVersion + ":" + state.PlanFingerprint
                   + ":E=" + state.IdleEnergy + "/" + state.Energy
                   + ":M=" + state.IdleMagic + "/" + state.Magic
                   + ":R3=" + state.IdleRes3 + "/" + state.Res3;
        }
    }

    internal enum FightBossAction { None, Nuke, Fight }

    internal sealed class FightBossState
    {
        internal long RebirthNumber;
        internal int BossId;
        internal int HighestBoss;
        internal bool Fighting;
        internal bool Nuke;
        internal double BossCurrentHp;
        internal double PlayerCurrentHp;
    }

    internal sealed class FightBossApply
    {
        internal FightBossAction Action;
        internal double ExpectedKillSeconds;
    }

    internal sealed class FightBossIntent :
        IMutationIntent<FightBossState, FightBossApply, FightBossState>
    {
        private readonly Character _character;
        private FightBossAction _action;
        private double _expectedKillSeconds;

        internal FightBossIntent(Character character) { _character = character; }

        public string Id { get { return "progression.fight-boss"; } }
        public MutationClass Class { get { return MutationClass.Combat; } }
        public MutationRisk Risk { get { return MutationRisk.Reversible; } }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId { get { return "BossController.public-fight-api@NGU-1.260"; } }
        public bool Required { get { return false; } }
        public bool CanCompensate { get { return false; } }
        public bool CreatesNewEpoch { get { return false; } }
        public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

        public FightBossState CaptureBefore(MutationContext context) { return Capture(); }

        public PreconditionResult CheckPreconditions(MutationContext context, FightBossState before)
        {
            if (!Main.IsAutomationReady)
                return PreconditionResult.Hold("gameplay synchronization is not current");
            if (_character.bossController == null || before.BossId < 0)
                return PreconditionResult.Hold("Fight Boss controller is unavailable");
            if (before.Fighting || before.Nuke)
                return PreconditionResult.AlreadySatisfied("Fight Boss is already active");
            if (CombatHelpers.CanNukeCurrentBoss(_character))
            {
                _action = FightBossAction.Nuke;
                return PreconditionResult.Ready();
            }
            if (CombatHelpers.CanWinCurrentBoss(_character, out _expectedKillSeconds))
            {
                _action = FightBossAction.Fight;
                return PreconditionResult.Ready();
            }
            return PreconditionResult.Hold(
                "source-exact Fight Boss oracle does not yet prove a win");
        }

        public FightBossApply Apply(MutationContext context, RootTransactionToken token,
            FightBossState before)
        {
            if (_action == FightBossAction.Nuke)
                _character.bossController.startNuke();
            else if (_action == FightBossAction.Fight)
            {
                _character.bossController.beginFight();
                if (_character.bossController.stopButton != null)
                    _character.bossController.stopButton.gameObject.SetActive(true);
            }
            return new FightBossApply
            {
                Action = _action,
                ExpectedKillSeconds = _expectedKillSeconds
            };
        }

        public VerificationResult<FightBossState> Verify(MutationContext context,
            FightBossState before, MutationApplyObservation<FightBossApply> apply)
        {
            var after = Capture();
            if (!apply.ReturnedNormally || apply.Value == null
                || apply.Value.Action == FightBossAction.None)
                return VerificationResult<FightBossState>.Failed(
                    "Fight Boss start did not return a typed action");
            if (after.RebirthNumber != before.RebirthNumber)
                return VerificationResult<FightBossState>.Failed(
                    "run changed during Fight Boss start");
            if (after.HighestBoss < before.HighestBoss || after.BossId < before.BossId)
                return VerificationResult<FightBossState>.Failed(
                    "Fight Boss state regressed during start");
            if (!after.Fighting && !after.Nuke && after.BossId == before.BossId
                && after.HighestBoss == before.HighestBoss)
                return VerificationResult<FightBossState>.Failed(
                    "native Fight Boss request produced no active fight or Boss progression");
            var label = apply.Value.Action == FightBossAction.Nuke
                ? "Boss nuke started" : "exact-viability Boss fight started; ETA "
                  + apply.Value.ExpectedKillSeconds.ToString("0.00") + "s";
            Main.LogAction("BOSS", label + " [confirmed by BossController state]");
            return VerificationResult<FightBossState>.Satisfied(after, label);
        }

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            FightBossState before, MutationApplyObservation<FightBossApply> apply)
        {
            return CompensationResult.NotSupported(
                "a started reversible Fight Boss action settles through native combat");
        }

        public bool BeforeStateMatches(FightBossState a, FightBossState b)
        {
            return a != null && b != null && a.RebirthNumber == b.RebirthNumber
                   && a.BossId == b.BossId && a.HighestBoss == b.HighestBoss
                   && a.Fighting == b.Fighting && a.Nuke == b.Nuke
                   && a.BossCurrentHp.Equals(b.BossCurrentHp)
                   && a.PlayerCurrentHp.Equals(b.PlayerCurrentHp);
        }

        public string FingerprintBefore(FightBossState state) { return Fingerprint(state); }
        public string FingerprintAfter(FightBossState state) { return Fingerprint(state); }

        private FightBossState Capture()
        {
            return new FightBossState
            {
                RebirthNumber = _character.stats == null ? -1 : _character.stats.rebirthNumber,
                BossId = _character.bossID,
                HighestBoss = _character.highestBoss,
                Fighting = _character.bossController != null
                           && _character.bossController.isFighting,
                Nuke = _character.bossController != null && _character.bossController.nukeBoss,
                BossCurrentHp = _character.bossCurHP,
                PlayerCurrentHp = _character.curHP
            };
        }

        private static string Fingerprint(FightBossState state)
        {
            return state == null ? "missing" : state.RebirthNumber + ":boss=" + state.BossId
                   + ":record=" + state.HighestBoss + ":fight=" + state.Fighting
                   + ":nuke=" + state.Nuke + ":hp=" + state.PlayerCurrentHp
                   + "/" + state.BossCurrentHp;
        }
    }
}
