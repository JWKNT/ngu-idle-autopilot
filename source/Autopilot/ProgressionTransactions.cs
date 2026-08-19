using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using NGUInjector.AllocationProfiles;
using NGUInjector.Managers;

/*
FILE PURPOSE

Own the typed progression mutations that must precede higher-level strategy: generated
Energy/Magic/R3 allocation, Fight Boss recovery, persistent all-difficulty Adventure routing,
one exact permanent inventory consumable, exact one-level PP/perk purchases, and conservative
inventory maintenance. All execute as children of the one-second, epoch-bound root. The resource
allocation child owns only Energy/Magic/R3 allocation and seals every native target after its
resource-specific phase; commit requires full-vector equality plus conservation, while a partial
settlement replays the exact before-vector through native controllers or quarantines Allocation. It
does not own gear, diggers, purchases, or Adventure (Wandoos quantities are included only as part
of the complete resource proof). The Boss child uses
the source-exact combat oracle and proves either an active native fight or synchronous progression.
Permanent consumables and perk purchases each prove their exact finite-resource debit and native
effect before commit; aggregate inventory maintenance cannot conceal either irreversible action.
Adventure settlement includes the native ITOPOD start/end range and Lazy-ITOPOD ownership fields,
so an accepted route cannot hide a rejected range or an automatic range overwrite.
Fight Boss and Adventure are mutually exclusive within a root: once the Boss child starts a native
fight, Adventure holds until the next root instead of mistaking the unchanged zone for a rejection.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class CriticalProgressionOutcome
    {
        internal MutationResult Allocation;
        internal MutationResult Wandoos;
        internal MutationResult Boss;
        internal MutationResult Adventure;
        internal MutationResult ProgressionConsumable;
        internal MutationResult Inventory;

        internal bool Failed
        {
            get { return IsFailure(Wandoos) || IsFailure(Allocation)
                         || IsFailure(Boss) || IsFailure(Adventure)
                         || IsFailure(ProgressionConsumable)
                         || IsFailure(Inventory); }
        }

        internal string FailureReason
        {
            get
            {
                if (IsFailure(Wandoos)) return "Wandoos OS: " + Wandoos.Reason;
                if (IsFailure(Allocation)) return "allocation: " + Allocation.Reason;
                if (IsFailure(Boss)) return "Fight Boss: " + Boss.Reason;
                if (IsFailure(Adventure)) return "Adventure: " + Adventure.Reason;
                if (IsFailure(ProgressionConsumable))
                    return "progression consumable: " + ProgressionConsumable.Reason;
                return IsFailure(Inventory) ? "Inventory: " + Inventory.Reason : string.Empty;
            }
        }

        private static bool IsFailure(MutationResult result)
        {
            if (result == null) return false;
            return result.Kind == MutationResultKind.CommittedWithException
                   || result.Kind == MutationResultKind.RejectedUnchanged
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
            // OS changes erase run-local Wandoos levels/progress, so settle the planner's typed
            // OS transition before the resource vector is reclaimed and reapplied. Permanent
            // installation disks remain a separate inventory child later in this bundle; a newly
            // installed OS therefore becomes eligible on the next one-second root.
            if (config.ManageAllocations && autopilot != null)
                outcome.Wandoos = WandoosRunManager.Manage(root, autopilot.Plan);
            if (config.ManageAllocations && allocation != null)
                outcome.Allocation = root.ExecuteChild(
                    new ResourceAllocationIntent(character, allocation));
            if (!root.IsClosed && config.ManageBosses
                && FightBossIntent.HasExecutableAction(character))
                outcome.Boss = root.ExecuteChild(new FightBossIntent(character));
            if (!root.IsClosed && EarlyAdventureIntent.IsEligible(character, config, quests))
                outcome.Adventure = root.ExecuteChild(
                    new EarlyAdventureIntent(character, config, autopilot, combat, quests));
            if (!root.IsClosed && config.ManageInventory && inventory != null
                && inventory.HasProgressionConsumable())
                outcome.ProgressionConsumable = root.ExecuteChild(
                    new ProgressionConsumableIntent(character, inventory));
            if (!root.IsClosed && config.ManageInventory && inventory != null)
                outcome.Inventory = root.ExecuteChild(
                    new InventoryMaintenanceIntent(character, inventory));
            LogNonSuccess("Wandoos OS", outcome.Wandoos);
            LogNonSuccess("resource allocation", outcome.Allocation);
            LogNonSuccess("Fight Boss", outcome.Boss);
            LogNonSuccess("Adventure", outcome.Adventure);
            LogNonSuccess("progression consumable", outcome.ProgressionConsumable);
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

    internal sealed class ProgressionConsumableState
    {
        internal ProgressionConsumableKind Kind;
        internal Equipment Identity;
        internal int Slot;
        internal int ItemId;
        internal int Level;
        internal bool IdentityPresent;
        internal bool Removable;
        internal bool YggdrasilOn;
        internal long Seeds;
        internal long GoldFruitTier;
        internal bool WandoosOn;
        internal long OsLevel;
        internal long XlLevel;
    }

    internal sealed class PerkPurchaseState
    {
        internal int PerkId;
        internal long Points;
        internal long Level;
        internal long Cost;
        internal bool TerminalItemOwned;
    }

    internal sealed class PerkPurchaseIntent :
        IMutationIntent<PerkPurchaseState, bool, PerkPurchaseState>
    {
        private readonly Character _character;
        private readonly int _perkId;
        private readonly long _reserve;

        internal PerkPurchaseIntent(Character character, int perkId, long reserve)
        {
            _character = character;
            _perkId = perkId;
            _reserve = Math.Max(0L, reserve);
        }

        public string Id { get { return "progression.perk-purchase." + _perkId; } }
        public MutationClass Class { get { return MutationClass.PermanentSpend; } }
        public MutationRisk Risk { get { return MutationRisk.Irreversible; } }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId { get { return "ItopodPerkController.tryLevelUp(int)/public-exact"; } }
        public bool Required { get { return false; } }
        public bool CanCompensate { get { return false; } }
        public bool CreatesNewEpoch { get { return false; } }
        public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

        public PerkPurchaseState CaptureBefore(MutationContext context) { return Capture(); }

        public PreconditionResult CheckPreconditions(MutationContext context,
            PerkPurchaseState before)
        {
            if (!Main.IsAutomationReady)
                return PreconditionResult.Hold("gameplay synchronization is not current");
            if (before == null || before.PerkId < 0 || before.Cost <= 0L)
                return PreconditionResult.Hold("perk ID/cost state is unavailable");
            if (before.Points - before.Cost < _reserve)
                return PreconditionResult.Hold("exact PP reserve would be crossed");
            if (_perkId == ItopodPerkPlanner.Perk231Id && before.TerminalItemOwned)
                return PreconditionResult.Hold("terminal perk item is already owned");
            return PreconditionResult.Ready();
        }

        public bool Apply(MutationContext context, RootTransactionToken token,
            PerkPurchaseState before)
        {
            _character.adventureController.itopod.tryLevelUp(_perkId);
            return true;
        }

        public VerificationResult<PerkPurchaseState> Verify(MutationContext context,
            PerkPurchaseState before, MutationApplyObservation<bool> apply)
        {
            var after = Capture();
            var confirmed = apply.ReturnedNormally && apply.Value && after != null
                            && after.Level == before.Level + 1L
                            && after.Points == before.Points - before.Cost
                            && (_perkId != ItopodPerkPlanner.Perk231Id
                                || !before.TerminalItemOwned && after.TerminalItemOwned);
            if (!confirmed)
                return VerificationResult<PerkPurchaseState>.Failed(
                    "perk purchase lacked exact PP debit, level increment, or terminal delivery");
            Main.LogAction("PURCHASE", "Bought perk " + _perkId + " for " + before.Cost
                + " PP [confirmed by exact debit/level delta]");
            return VerificationResult<PerkPurchaseState>.Satisfied(after,
                "exact PP debit and one perk level confirmed");
        }

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            PerkPurchaseState before, MutationApplyObservation<bool> apply)
        {
            return CompensationResult.NotSupported("a permanent perk purchase has no safe inverse");
        }

        public bool BeforeStateMatches(PerkPurchaseState a, PerkPurchaseState b)
        {
            return a != null && b != null && a.PerkId == b.PerkId
                   && a.Points == b.Points && a.Level == b.Level && a.Cost == b.Cost
                   && a.TerminalItemOwned == b.TerminalItemOwned;
        }

        public string FingerprintBefore(PerkPurchaseState state) { return Fingerprint(state); }
        public string FingerprintAfter(PerkPurchaseState state) { return Fingerprint(state); }

        private PerkPurchaseState Capture()
        {
            if (_character == null || _character.adventure == null
                || _character.adventure.itopod == null
                || _character.adventureController == null
                || _character.adventureController.itopod == null
                || _perkId < 0
                || _perkId >= _character.adventure.itopod.perkLevel.Count)
                return null;
            return new PerkPurchaseState
            {
                PerkId = _perkId,
                Points = _character.adventure.itopod.perkPoints,
                Level = _character.adventure.itopod.perkLevel[_perkId],
                Cost = _character.adventureController.itopod.perkCost(_perkId),
                TerminalItemOwned = EndgameDependencyModel.IsOwned(_character,
                    ItopodPerkPlanner.Perk231ItemId)
            };
        }

        private static string Fingerprint(PerkPurchaseState state)
        {
            return state == null ? "missing" : state.PerkId + ":" + state.Points + ":"
                + state.Level + ":" + state.Cost + ":" + state.TerminalItemOwned;
        }
    }

    /*
    VERIFIED ONE-ITEM PROGRESSION CONSUMPTION

    IDs 66/92/163 are permanent-state upgrades, not ordinary inventory maintenance. Native
    consumeItem can delete them with no benefit when level/cap/install predicates are wrong, so
    InventoryManager admits one exact removable identity and this irreversible child proves its
    source-specific debit plus OS/Ygg transition. One item per root bounds any partial native
    failure; no field rewrite or compensating duplicate consumption is attempted.
    */
    internal sealed class ProgressionConsumableIntent :
        IMutationIntent<ProgressionConsumableState, bool, ProgressionConsumableState>
    {
        private readonly Character _character;
        private readonly InventoryManager _inventory;
        private ProgressionConsumableCandidate _selected;

        internal ProgressionConsumableIntent(Character character, InventoryManager inventory)
        {
            _character = character;
            _inventory = inventory;
        }

        public string Id { get { return "progression.inventory-consumable"; } }
        public MutationClass Class { get { return MutationClass.Inventory; } }
        public MutationRisk Risk { get { return MutationRisk.Irreversible; } }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId { get { return NativeBindingKeys.ItemConsume; } }
        public bool Required { get { return false; } }
        public bool CanCompensate { get { return false; } }
        public bool CreatesNewEpoch { get { return false; } }
        public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

        public ProgressionConsumableState CaptureBefore(MutationContext context)
        {
            if (!_inventory.TrySelectProgressionConsumable(out _selected)) return null;
            return Capture(_selected);
        }

        public PreconditionResult CheckPreconditions(MutationContext context,
            ProgressionConsumableState before)
        {
            if (!Main.IsAutomationReady)
                return PreconditionResult.Hold("gameplay synchronization is not current");
            if (before == null || _selected == null || !before.IdentityPresent
                || !before.Removable)
                return PreconditionResult.Hold("no exact removable progression consumable is admitted");
            return PreconditionResult.Ready();
        }

        public bool Apply(MutationContext context, RootTransactionToken token,
            ProgressionConsumableState before)
        {
            return _inventory.ConsumeProgressionConsumable(_selected);
        }

        public VerificationResult<ProgressionConsumableState> Verify(MutationContext context,
            ProgressionConsumableState before, MutationApplyObservation<bool> apply)
        {
            var after = Capture(_selected);
            if (!apply.ReturnedNormally || !apply.Value || before == null || after == null)
                return VerificationResult<ProgressionConsumableState>.Failed(
                    "native progression consume did not return a complete state");
            bool confirmed;
            string label;
            if (before.Kind == ProgressionConsumableKind.GiantSeed)
            {
                var gain = before.YggdrasilOn
                    ? Math.Max(1L, Math.Min(200L, (long)Math.Floor(before.Level
                        * (1.0 + before.Level / 100f)))) : 1L;
                confirmed = !after.IdentityPresent && after.YggdrasilOn
                            && after.Seeds == before.Seeds + gain
                            && after.GoldFruitTier >= Math.Max(1L, before.GoldFruitTier);
                label = "Giant Seed for " + gain + " exact seeds/Ygg unlock";
            }
            else if (before.Kind == ProgressionConsumableKind.Wandoos98)
            {
                var debit = before.WandoosOn ? before.OsLevel + 1 : 1;
                var objectDebit = before.WandoosOn
                    ? before.Level == debit ? !after.IdentityPresent
                        : after.IdentityPresent && after.Level == before.Level - debit
                    : before.Level == 0 ? !after.IdentityPresent
                        : after.IdentityPresent && after.Level == before.Level - 1;
                confirmed = objectDebit && after.WandoosOn
                            && (before.WandoosOn
                                ? after.OsLevel == before.OsLevel + 1
                                : after.OsLevel == before.OsLevel);
                label = "Wandoos 98 disk";
            }
            else
            {
                var debit = before.XlLevel == 0 ? 1 : before.XlLevel + 1;
                var objectDebit = before.XlLevel == 0
                    ? before.Level == 0 ? !after.IdentityPresent
                        : after.IdentityPresent && after.Level == before.Level - 1
                    : before.Level == debit ? !after.IdentityPresent
                        : after.IdentityPresent && after.Level == before.Level - debit;
                confirmed = objectDebit && after.XlLevel == before.XlLevel + 1;
                label = "Wandoos XL disk";
            }
            if (!confirmed)
                return VerificationResult<ProgressionConsumableState>.Failed(
                    label + " lacked its exact identity/resource postcondition");
            Main.LogAction("PROGRESSION", "Consumed " + label
                + " [confirmed by exact identity and permanent-state delta]");
            return VerificationResult<ProgressionConsumableState>.Satisfied(after,
                label + " exact debit and permanent transition confirmed");
        }

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            ProgressionConsumableState before, MutationApplyObservation<bool> apply)
        {
            return CompensationResult.NotSupported(
                "native item consumption cannot be reversed without a save rollback");
        }

        public bool BeforeStateMatches(ProgressionConsumableState a,
            ProgressionConsumableState b)
        {
            return a != null && b != null && ReferenceEquals(a.Identity, b.Identity)
                   && a.Slot == b.Slot && a.Level == b.Level && a.Removable == b.Removable
                   && a.YggdrasilOn == b.YggdrasilOn && a.Seeds == b.Seeds
                   && a.WandoosOn == b.WandoosOn && a.OsLevel == b.OsLevel
                   && a.XlLevel == b.XlLevel;
        }

        public string FingerprintBefore(ProgressionConsumableState state)
        {
            return Fingerprint(state);
        }

        public string FingerprintAfter(ProgressionConsumableState state)
        {
            return Fingerprint(state);
        }

        private ProgressionConsumableState Capture(ProgressionConsumableCandidate selected)
        {
            if (selected == null || _character == null || _character.inventory == null)
                return null;
            var present = _character.inventory.inventory.Any(x => ReferenceEquals(x,
                selected.Identity));
            var item = present ? selected.Identity : null;
            var fruits = _character.yggdrasil == null ? null : _character.yggdrasil.fruits;
            return new ProgressionConsumableState
            {
                Kind = selected.Kind,
                Identity = selected.Identity,
                Slot = selected.Slot,
                ItemId = selected.ItemId,
                Level = item == null ? -1 : item.level,
                IdentityPresent = present,
                Removable = item != null && item.removable,
                YggdrasilOn = _character.settings.yggdrasilOn,
                Seeds = _character.yggdrasil == null ? 0L : _character.yggdrasil.seeds,
                GoldFruitTier = fruits == null || fruits.Count == 0 ? 0L : fruits[0].maxTier,
                WandoosOn = _character.settings.wandoos98On,
                OsLevel = _character.wandoos98.OSlevel,
                XlLevel = _character.wandoos98.XLLevels
            };
        }

        private static string Fingerprint(ProgressionConsumableState state)
        {
            return state == null ? "missing" : state.Kind + ":" + state.ItemId + ":"
                + state.Slot + ":" + RuntimeHelpers.GetHashCode(state.Identity) + ":"
                + state.Level + ":" + state.IdentityPresent + ":" + state.YggdrasilOn
                + ":" + state.Seeds + ":" + state.WandoosOn + ":" + state.OsLevel
                + ":" + state.XlLevel;
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
                // IDs 1-39 are the source-audited physical boost families. Full automation
                // deliberately converts them into stronger gear/Cube immediately; their native
                // stat delta is verified inside InventoryManager rather than treated as lost
                // equipment contribution by this outer topology invariant.
                if (before.Maxxed[id] || after.Maxxed[id] || id >= 1 && id <= 39) continue;
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
        internal int ItopodStart;
        internal int ItopodEnd;
        internal bool LazyItopodOn;
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
                || c.settings == null || c.adventure == null
                || c.adventureController == null)
                return false;
            try
            {
                // ControlAdventure itself orders Exile assembly, Death Note, ready Titans,
                // active quests, major unlocks, collection, and ITOPOD. Keeping those states out
                // of this sole call-site made every corresponding branch unreachable and allowed
                // rebirth to approach a ready Titan clock without an Adventure owner.
                return true;
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
            if (_character.bossController != null
                && (_character.bossController.isFighting
                    || _character.bossController.nukeBoss))
                return PreconditionResult.Hold(
                    "Fight Boss owns this root; Adventure routing waits for the next settled frame");
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
            if (after.TargetZone == 1000)
            {
                var route = ZoneHelpers.LastItopodRoute;
                if (route == null || !route.Confirmed
                    || after.ItopodStart != route.Start || after.ItopodEnd != route.End)
                    return VerificationResult<EarlyAdventureState>.Failed(
                        "native ITOPOD range does not match the confirmed solver route");
                if (after.LazyItopodOn)
                    return VerificationResult<EarlyAdventureState>.Failed(
                        "Lazy ITOPOD still owns and may overwrite the solver range");
            }
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
                   && a.EnemyIdentity == b.EnemyIdentity
                   && a.ItopodStart == b.ItopodStart && a.ItopodEnd == b.ItopodEnd
                   && a.LazyItopodOn == b.LazyItopodOn;
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
                    : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(enemy),
                ItopodStart = _character.adventure == null
                    ? -1 : _character.adventure.itopodStart,
                ItopodEnd = _character.adventure == null
                    ? -1 : _character.adventure.itopodEnd,
                LazyItopodOn = _character.arbitrary != null
                               && _character.arbitrary.lazyITOPODOn
            };
        }

        private static string Fingerprint(EarlyAdventureState state)
        {
            return state == null ? "missing" : "zone=" + state.Zone + ":target="
                   + state.TargetZone + ":type=" + state.FightType + ":fight="
                   + state.FightInProgress + ":idle=" + state.AutoAttacking
                   + ":ak=" + state.AutoKillTitans + ":enemy=" + state.EnemyIdentity
                   + ":itopod=" + state.ItopodStart + "-" + state.ItopodEnd
                   + ":lazy=" + state.LazyItopodOn;
        }
    }

    internal sealed class ResourceAllocationState
    {
        internal LiveResourceAllocationSnapshot Snapshot;
    }

    internal sealed class ResourceAllocationApply
    {
        internal bool Completed;
        internal LiveResourceAllocationSnapshot EnergyAccepted;
        internal LiveResourceAllocationSnapshot MagicAccepted;
        internal LiveResourceAllocationSnapshot Resource3Accepted;
        internal LiveResourceAllocationSnapshot RequestedAfter;
    }

    internal sealed class ResourceAllocationIntent :
        IMutationIntent<ResourceAllocationState, ResourceAllocationApply, ResourceAllocationState>
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
        public string BindingId { get { return "CustomAllocation.resource-only.full-vector.v2"; } }
        public bool Required { get { return false; } }
        public bool CanCompensate { get { return true; } }
        public bool CreatesNewEpoch { get { return false; } }
        public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

        public ResourceAllocationState CaptureBefore(MutationContext context) { return Capture(); }

        public PreconditionResult CheckPreconditions(MutationContext context,
            ResourceAllocationState before)
        {
            if (!Main.IsAutomationReady)
                return PreconditionResult.Hold("gameplay synchronization is not current");
            var reason = string.Empty;
            return before != null && before.Snapshot != null
                   && before.Snapshot.IsComplete(out reason)
                ? PreconditionResult.Ready()
                : PreconditionResult.Hold(before == null || before.Snapshot == null
                    ? "full native resource-allocation snapshot is unavailable" : reason);
        }

        public ResourceAllocationApply Apply(MutationContext context, RootTransactionToken token,
            ResourceAllocationState before)
        {
            // The old DoAllocations aggregate also mutates gear, diggers, Wandoos, and NGU mode.
            // Those remain separate transaction classes; this child is resource-only.
            var originalInput = _character.energyMagicPanel.energyMagicInput;
            try
            {
                _allocation.AllocateEnergy();
                var energyAccepted = CaptureLive();
                _allocation.AllocateMagic();
                var magicAccepted = CaptureLive();
                _allocation.AllocateR3();
                var resource3Accepted = CaptureLive();
                var phaseReason = ValidateAcceptedPhases(before.Snapshot, energyAccepted,
                    magicAccepted, resource3Accepted);
                var requested = string.IsNullOrEmpty(phaseReason)
                    ? new LiveResourceAllocationSnapshot(energyAccepted.Energy,
                        magicAccepted.Magic, resource3Accepted.Resource3,
                        energyAccepted.AdvancedTrainingLevelTargets,
                        energyAccepted.PlanVersion, energyAccepted.PlanFingerprint)
                    : null;
                return new ResourceAllocationApply
                {
                    Completed = requested != null,
                    // Each resource is captured immediately after its own native reclaim/apply
                    // phase.  Its full target amounts are therefore the accepted per-target deltas
                    // from the empty native layout, not a post-hoc copy of Verify's final state.
                    EnergyAccepted = energyAccepted,
                    MagicAccepted = magicAccepted,
                    Resource3Accepted = resource3Accepted,
                    RequestedAfter = requested
                };
            }
            finally
            {
                _character.energyMagicPanel.energyRequested.text =
                    ExactResourceAllocator.FormatExactInput(originalInput);
                _character.energyMagicPanel.validateInput();
            }
        }

        public VerificationResult<ResourceAllocationState> Verify(MutationContext context,
            ResourceAllocationState before, MutationApplyObservation<ResourceAllocationApply> apply)
        {
            var after = Capture();
            if (!apply.ReturnedNormally || apply.Value == null || !apply.Value.Completed
                || apply.Value.RequestedAfter == null)
                return VerificationResult<ResourceAllocationState>.Failed(
                    "resource allocation did not return a sealed requested-after vector");
            var reason = string.Empty;
            if (after == null || !LiveResourceAllocationProof.VerifySettlement(before.Snapshot,
                    apply.Value.RequestedAfter, after.Snapshot, out reason))
                return VerificationResult<ResourceAllocationState>.Failed(reason);
            return VerificationResult<ResourceAllocationState>.Satisfied(after,
                "full target vectors, per-resource conservation, and accepted native deltas verified");
        }

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            ResourceAllocationState before,
            MutationApplyObservation<ResourceAllocationApply> apply)
        {
            var reason = string.Empty;
            return before != null && before.Snapshot != null
                   && LiveResourceAllocationProof.Restore(_character, before.Snapshot, out reason)
                ? CompensationResult.Restored(reason)
                : CompensationResult.Failed(string.IsNullOrEmpty(reason)
                    ? "exact native allocation replay failed" : reason);
        }

        public bool BeforeStateMatches(ResourceAllocationState a, ResourceAllocationState b)
        {
            return a != null && b != null && a.Snapshot != null
                   && a.Snapshot.ExactEquals(b.Snapshot);
        }

        public string FingerprintBefore(ResourceAllocationState state) { return Fingerprint(state); }
        public string FingerprintAfter(ResourceAllocationState state) { return Fingerprint(state); }

        private ResourceAllocationState Capture()
        {
            return new ResourceAllocationState
            {
                Snapshot = CaptureLive()
            };
        }

        private LiveResourceAllocationSnapshot CaptureLive()
        {
            return LiveResourceAllocationProof.Capture(_character,
                _allocation.InstalledPlanVersion,
                _allocation.InstalledPlanFingerprint);
        }

        private static string ValidateAcceptedPhases(LiveResourceAllocationSnapshot before,
            LiveResourceAllocationSnapshot energy, LiveResourceAllocationSnapshot magic,
            LiveResourceAllocationSnapshot res3)
        {
            string reason;
            if (before == null || energy == null || magic == null || res3 == null)
                return "one or more native allocation phase captures are unavailable";
            if (!energy.IsComplete(out reason) || !magic.IsComplete(out reason)
                || !res3.IsComplete(out reason)) return reason;
            // A resource-specific sweep may mutate only that resource (and Energy's native AT
            // level-target controls).  This catches cross-resource or later-phase motion even when
            // capacities/idle totals remain conserved.
            if (!before.Magic.ExactEquals(energy.Magic)
                || !before.Resource3.ExactEquals(energy.Resource3))
                return "Energy phase changed a Magic/Resource 3 target";
            if (!energy.Energy.ExactEquals(magic.Energy)
                || !before.Resource3.ExactEquals(magic.Resource3))
                return "Magic phase changed an Energy/Resource 3 target";
            if (!magic.Energy.ExactEquals(res3.Energy)
                || !magic.Magic.ExactEquals(res3.Magic))
                return "Resource 3 phase changed an Energy/Magic target";
            return string.Empty;
        }

        private static string Fingerprint(ResourceAllocationState state)
        {
            return state == null || state.Snapshot == null
                ? "missing" : state.Snapshot.Fingerprint();
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

        internal static bool HasExecutableAction(Character character)
        {
            if (character == null || character.bossController == null
                || character.bossID < 0 || character.bossController.isFighting
                || character.bossController.nukeBoss)
                return false;
            double seconds;
            return CombatHelpers.CanNukeCurrentBoss(character)
                   || CombatHelpers.CanWinCurrentBoss(character, out seconds);
        }

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
