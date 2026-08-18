using System;
using System.Collections.Generic;
using System.Linq;

/*
FILE PURPOSE

Purpose: EndgameDependencyModel is the authoritative bridge between pure END mechanics and live
physical state. It deliberately distinguishes a canonical ordinary-inventory terminal piece from a
copy that merely exists in Daycare/equipment, and source completion from delayed physical delivery.

Mechanism: Immutable normalization plans name one canonical ordinary object, exact ordinary
duplicates, and non-ordinary recovery debts without mutating them. Checker-backed branches expose a
four-state materialization projection. T12 planning combines cumulative native provenance with the
exact loot-capacity proof and chooses the highest version that is both combat-bounded and physically
safe. T14 retry uses ordinary item 495 as completion; the native finalTitanDefeated flag is only
evidence that a prior attempt reached its irreversible side effects.

Inputs and outputs: Pure entry points consume PhysicalTopology snapshots, recoverable-copy records,
source flags, boss gates, or T12 combat bounds. Live overloads read Character inventory/source state.
Outputs are immutable branch, normalization, grant, and T12 plans for later transaction owners.

Invariants and safety: HasTerminalPiece means exactly one ordinary copy. HasRecoverableCopy is broad
and never proves completion. Duplicate plans keep one exact ordinary identity and target only proven
extras; this file never trashes, swaps, retrieves, casts, fights, or triggers the ending. Dropped and
MAXX flags are intentionally absent because native records them before fallible physical insertion.

Extension points and non-goals: Inventory and terminal transaction managers must re-read topology
inside their mutation root, execute at most one planned identity operation, and verify the resulting
ordinary count. Scheduler/dashboard integrations can consume provenance and pending-grant state.
*/
namespace NGUInjector.Autopilot
{
    internal enum EndRecoverableLocation
    {
        Daycare,
        Accessory,
        Head,
        Chest,
        Legs,
        Boots,
        Weapon,
        Weapon2
    }

    internal sealed class EndRecoverableCopy
    {
        internal readonly int ItemId;
        internal readonly EndRecoverableLocation Location;
        internal readonly int LocationIndex;
        internal readonly object Identity;

        internal EndRecoverableCopy(
            int itemId, EndRecoverableLocation location, int locationIndex, object identity)
        {
            if (!MechanicsEndgame.IsProtectedItem(itemId))
                throw new ArgumentOutOfRangeException("itemId");
            if (locationIndex < 0) throw new ArgumentOutOfRangeException("locationIndex");
            if (identity == null) throw new ArgumentNullException("identity");
            ItemId = itemId;
            Location = location;
            LocationIndex = locationIndex;
            Identity = identity;
        }
    }

    internal sealed class EndgameCanonicalizationPlan
    {
        private readonly int[] _ordinaryDuplicateSlots;
        private readonly EndRecoverableCopy[] _nonOrdinaryCopies;
        private readonly EndRecoverableCopy[] _nonOrdinaryDuplicatesAfterRecovery;

        internal readonly int ItemId;
        internal readonly int TargetSlot;
        internal readonly int OrdinaryCopies;
        internal readonly int NonOrdinaryCopies;
        internal readonly int CanonicalOrdinarySlot;
        internal readonly object CanonicalOrdinaryIdentity;
        internal readonly EndRecoverableCopy RecoverySource;
        internal readonly bool HasTerminalPiece;
        internal readonly bool HasRecoverableCopy;
        internal readonly bool NeedsRecoveryToOrdinary;
        internal readonly bool NeedsDuplicateCleanup;
        internal readonly bool IsCanonical;

        internal EndgameCanonicalizationPlan(
            int itemId,
            int targetSlot,
            int ordinaryCopies,
            int nonOrdinaryCopies,
            int canonicalOrdinarySlot,
            object canonicalOrdinaryIdentity,
            EndRecoverableCopy recoverySource,
            int[] ordinaryDuplicateSlots,
            EndRecoverableCopy[] nonOrdinaryCopiesSnapshot,
            EndRecoverableCopy[] nonOrdinaryDuplicatesAfterRecovery)
        {
            ItemId = itemId;
            TargetSlot = targetSlot;
            OrdinaryCopies = ordinaryCopies;
            NonOrdinaryCopies = nonOrdinaryCopies;
            CanonicalOrdinarySlot = canonicalOrdinarySlot;
            CanonicalOrdinaryIdentity = canonicalOrdinaryIdentity;
            RecoverySource = recoverySource;
            _ordinaryDuplicateSlots = (int[])ordinaryDuplicateSlots.Clone();
            _nonOrdinaryCopies = (EndRecoverableCopy[])nonOrdinaryCopiesSnapshot.Clone();
            _nonOrdinaryDuplicatesAfterRecovery =
                (EndRecoverableCopy[])nonOrdinaryDuplicatesAfterRecovery.Clone();
            HasTerminalPiece = ordinaryCopies == 1;
            HasRecoverableCopy = ordinaryCopies + nonOrdinaryCopies > 0;
            NeedsRecoveryToOrdinary = ordinaryCopies == 0 && recoverySource != null;
            NeedsDuplicateCleanup = _ordinaryDuplicateSlots.Length > 0
                                    || _nonOrdinaryDuplicatesAfterRecovery.Length > 0;
            IsCanonical = ordinaryCopies == 1 && nonOrdinaryCopies == 0;
        }

        internal int[] OrdinaryDuplicateSlots()
        {
            return (int[])_ordinaryDuplicateSlots.Clone();
        }

        internal EndRecoverableCopy[] NonOrdinaryCopiesSnapshot()
        {
            return (EndRecoverableCopy[])_nonOrdinaryCopies.Clone();
        }

        internal EndRecoverableCopy[] NonOrdinaryDuplicatesAfterRecovery()
        {
            return (EndRecoverableCopy[])_nonOrdinaryDuplicatesAfterRecovery.Clone();
        }
    }

    internal enum EndGrantMaterializationState
    {
        NotCheckerDelivered,
        SourceIncomplete,
        WaitingForBoss225,
        PendingChecker,
        Delivered,
        NeedsNormalization
    }

    internal sealed class EndgameGrantProjection
    {
        internal readonly int ItemId;
        internal readonly EndGrantMaterializationState State;
        internal readonly bool SourceSatisfied;
        internal readonly bool CheckerEligible;
        internal readonly bool PendingGrant;
        internal readonly double NextCheckerEtaSeconds;
        internal readonly string Reason;

        internal EndgameGrantProjection(
            int itemId, EndGrantMaterializationState state,
            bool sourceSatisfied, bool checkerEligible, bool pendingGrant,
            double nextCheckerEtaSeconds, string reason)
        {
            ItemId = itemId;
            State = state;
            SourceSatisfied = sourceSatisfied;
            CheckerEligible = checkerEligible;
            PendingGrant = pendingGrant;
            NextCheckerEtaSeconds = nextCheckerEtaSeconds;
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class EndgameTitan12Plan
    {
        private readonly int[] _missingCoveredItems;

        internal readonly int MaximumSafelyKillableVersion;
        internal readonly int SelectedVersion;
        internal readonly int LatestMissingItemId;
        internal readonly bool Complete;
        internal readonly bool Actionable;
        internal readonly LootCapacityProof CapacityProof;
        internal readonly string Provenance;
        internal readonly string Reason;

        internal EndgameTitan12Plan(
            int maximumSafelyKillableVersion,
            int selectedVersion,
            int latestMissingItemId,
            int[] missingCoveredItems,
            bool complete,
            bool actionable,
            LootCapacityProof capacityProof,
            string provenance,
            string reason)
        {
            MaximumSafelyKillableVersion = maximumSafelyKillableVersion;
            SelectedVersion = selectedVersion;
            LatestMissingItemId = latestMissingItemId;
            _missingCoveredItems = (int[])missingCoveredItems.Clone();
            Complete = complete;
            Actionable = actionable;
            CapacityProof = capacityProof;
            Provenance = provenance ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        internal int[] MissingCoveredItems()
        {
            return (int[])_missingCoveredItems.Clone();
        }
    }

    internal sealed class EndgameBranchState
    {
        internal int ItemId;
        internal int RequiredInventorySlot;
        internal string Branch;
        // Compatibility name: Owned now means exactly one ordinary copy, never broad possession.
        internal bool Owned;
        internal bool TerminalPiecePresent;
        internal bool RecoverableCopyPresent;
        internal int OrdinaryCopies;
        internal int NonOrdinaryCopies;
        internal int CanonicalOrdinarySlot;
        internal int OrdinaryDuplicateCount;
        internal bool RecoveryDebt;
        internal bool SourceSatisfied;
        internal bool PendingGrant;
        internal bool CheckerEligible;
        internal double NextCheckerEtaSeconds;
        internal bool RetryLegal;
        internal int Titan12MinimumVersion;
        internal string Provenance;
    }

    internal static class EndgameDependencyModel
    {
        private static readonly int SadisticBossItemId = MechanicsEndgame.AllRequirements()
            .First(x => x.DependencyKind == EndDependencyKind.SadisticBoss).ItemId;

        internal static bool IsEndItem(int id)
        {
            return MechanicsEndgame.IsProtectedItem(id);
        }

        internal static int RequiredInventorySlot(int id)
        {
            return IsEndItem(id) ? MechanicsEndgame.TargetSlotForItem(id) : -1;
        }

        internal static string BranchForItem(int id)
        {
            return IsEndItem(id) ? MechanicsEndgame.FindByItemId(id).Description : string.Empty;
        }

        internal static int TitanVersionItem(int version)
        {
            if (version < MechanicsEndgame.MinimumTitan12Version
                || version > MechanicsEndgame.MaximumTitan12Version)
                return -1;
            var requirements = MechanicsEndgame.Titan12ItemsForVersion(version);
            for (var i = requirements.Length - 1; i >= 0; i--)
                if (MechanicsEndgame.Titan12MinimumVersionForItem(requirements[i]) == version)
                    return requirements[i];
            return -1;
        }

        internal static bool HasTerminalPiece(OrdinaryInventoryTopology topology, int id)
        {
            if (topology == null) throw new ArgumentNullException("topology");
            if (!IsEndItem(id)) return false;
            return topology.CountOrdinaryItem(id) == 1;
        }

        internal static bool HasRecoverableCopy(
            OrdinaryInventoryTopology topology, int id, EndRecoverableCopy[] recoverableCopies)
        {
            if (topology == null) throw new ArgumentNullException("topology");
            if (!IsEndItem(id)) return false;
            ValidateRecoverableCopies(id, recoverableCopies);
            return topology.CountOrdinaryItem(id) + recoverableCopies.Length > 0;
        }

        internal static EndgameCanonicalizationPlan PlanCanonicalization(
            OrdinaryInventoryTopology topology, int id, EndRecoverableCopy[] recoverableCopies)
        {
            if (topology == null) throw new ArgumentNullException("topology");
            if (!IsEndItem(id)) throw new ArgumentOutOfRangeException("id");
            ValidateRecoverableCopies(id, recoverableCopies);
            for (var i = 0; i < recoverableCopies.Length; i++)
                if (topology.FindOrdinarySlotByIdentity(recoverableCopies[i].Identity) >= 0)
                    throw new ArgumentException(
                        "One physical identity cannot be both ordinary and recoverable.");

            var ordinarySlots = topology.OrdinarySlotsForItem(id);
            var canonicalSlot = -1;
            var targetSlot = MechanicsEndgame.TargetSlotForItem(id);
            for (var i = 0; i < ordinarySlots.Length; i++)
                if (ordinarySlots[i] == targetSlot) canonicalSlot = targetSlot;
            if (canonicalSlot < 0 && ordinarySlots.Length > 0) canonicalSlot = ordinarySlots[0];

            object canonicalIdentity = null;
            if (canonicalSlot >= 0) canonicalIdentity = topology.SlotAt(canonicalSlot).Identity;

            var duplicateSlots = new List<int>();
            for (var i = 0; i < ordinarySlots.Length; i++)
                if (ordinarySlots[i] != canonicalSlot) duplicateSlots.Add(ordinarySlots[i]);

            EndRecoverableCopy recoverySource = null;
            if (ordinarySlots.Length == 0 && recoverableCopies.Length > 0)
            {
                // Daycare has a normal native retrieval path. Prefer it over defensive recovery
                // from a legacy equipped/accessory location.
                for (var i = 0; i < recoverableCopies.Length; i++)
                    if (recoverableCopies[i].Location == EndRecoverableLocation.Daycare)
                    {
                        recoverySource = recoverableCopies[i];
                        break;
                    }
                if (recoverySource == null) recoverySource = recoverableCopies[0];
            }

            var nonOrdinaryDuplicates = new List<EndRecoverableCopy>();
            for (var i = 0; i < recoverableCopies.Length; i++)
                if (!object.ReferenceEquals(recoverableCopies[i], recoverySource))
                    nonOrdinaryDuplicates.Add(recoverableCopies[i]);

            return new EndgameCanonicalizationPlan(
                id, targetSlot, ordinarySlots.Length, recoverableCopies.Length,
                canonicalSlot, canonicalIdentity, recoverySource,
                duplicateSlots.ToArray(), recoverableCopies,
                nonOrdinaryDuplicates.ToArray());
        }

        internal static EndgameGrantProjection EvaluateCheckerGrant(
            int itemId, bool sourceSatisfied, int highestSadisticBoss,
            int ordinaryCopies, double secondsSinceLastChecker)
        {
            if (!MechanicsEndgame.IsCheckerDeliveredItem(itemId))
                return new EndgameGrantProjection(itemId,
                    EndGrantMaterializationState.NotCheckerDelivered,
                    sourceSatisfied, false, false, -1.0,
                    "This END branch is not delivered by the native 30-second checker.");
            if (ordinaryCopies < 0) throw new ArgumentOutOfRangeException("ordinaryCopies");

            if (ordinaryCopies == 1)
                return new EndgameGrantProjection(itemId,
                    EndGrantMaterializationState.Delivered,
                    sourceSatisfied, highestSadisticBoss >= MechanicsEndgame.EndCheckerMinimumSadisticBoss,
                    false, 0.0, "Exactly one ordinary END piece is physically delivered.");
            if (ordinaryCopies > 1)
                return new EndgameGrantProjection(itemId,
                    EndGrantMaterializationState.NeedsNormalization,
                    sourceSatisfied, highestSadisticBoss >= MechanicsEndgame.EndCheckerMinimumSadisticBoss,
                    false, -1.0, "Multiple ordinary copies require canonical duplicate cleanup.");
            if (!sourceSatisfied)
                return new EndgameGrantProjection(itemId,
                    EndGrantMaterializationState.SourceIncomplete,
                    false, false, false, -1.0,
                    "The persistent source has not completed.");
            if (highestSadisticBoss < MechanicsEndgame.EndCheckerMinimumSadisticBoss)
                return new EndgameGrantProjection(itemId,
                    EndGrantMaterializationState.WaitingForBoss225,
                    true, false, false, -1.0,
                    "Source is complete; native materialization waits for Sadistic Boss 225.");

            var eta = MechanicsEndgame.EndCheckerMaximumDelaySeconds;
            if (!double.IsNaN(secondsSinceLastChecker) && secondsSinceLastChecker >= 0.0)
                eta = Math.Max(0.0, MechanicsEndgame.EndCheckerMaximumDelaySeconds
                                    - Math.Min(MechanicsEndgame.EndCheckerMaximumDelaySeconds,
                                        secondsSinceLastChecker));
            return new EndgameGrantProjection(itemId,
                EndGrantMaterializationState.PendingChecker,
                true, true, true, eta,
                "Persistent source is complete; preserve filter/capacity and await the native checker.");
        }

        internal static EndgameTitan12Plan PlanTitan12(
            OrdinaryInventoryTopology topology, int maximumSafelyKillableVersion)
        {
            if (topology == null) throw new ArgumentNullException("topology");
            var ordinaryIds = OrdinaryItemIds(topology);
            var allMissing = MechanicsEndgame.MissingTitan12ItemsForVersion(
                MechanicsEndgame.MaximumTitan12Version, ordinaryIds);
            if (allMissing.Length == 0)
                return new EndgameTitan12Plan(maximumSafelyKillableVersion, -1, -1,
                    new int[0], true, false, null,
                    "Native zone42Drop cumulative T12 END order is 483,489,493,484.",
                    "All four T12 END pieces have ordinary physical copies.");

            var maximum = Math.Min(MechanicsEndgame.MaximumTitan12Version,
                maximumSafelyKillableVersion);
            EndgameTitan12Plan highestHeld = null;
            for (var version = maximum; version >= MechanicsEndgame.MinimumTitan12Version; version--)
            {
                var missing = MechanicsEndgame.MissingTitan12ItemsForVersion(version, ordinaryIds);
                if (missing.Length == 0) continue;
                var latest = missing[missing.Length - 1];
                var proof = LootCapacity.ProveOrdinary(topology,
                    LootCapacity.Titan12EndPiece(latest));
                var provenance = "T12 v" + version + " cumulatively rolls ["
                                 + string.Join(",", MechanicsEndgame.Titan12ItemsForVersion(version)
                                     .Select(x => x.ToString()).ToArray()) + "] in native order.";
                var candidate = new EndgameTitan12Plan(
                    maximumSafelyKillableVersion, version, latest, missing,
                    false, proof.Admitted, proof, provenance,
                    proof.Admitted
                        ? "Highest combat-bounded version with an exact capacity proof selected."
                        : "Version covers missing pieces but its latest missing roll lacks exact capacity.");
                if (highestHeld == null) highestHeld = candidate;
                if (candidate.Actionable) return candidate;
            }

            if (highestHeld != null) return highestHeld;
            return new EndgameTitan12Plan(maximumSafelyKillableVersion, -1, -1,
                new int[0], false, false, null,
                "Native zone42Drop cumulative T12 END order is 483,489,493,484.",
                "No missing T12 END piece is covered by a safely killable version.");
        }

        internal static bool HasTerminalPiece(Character c, int id)
        {
            if (c == null || c.inventory == null || !IsEndItem(id)) return false;
            return CountOrdinaryCopies(c, id) == 1;
        }

        internal static bool HasRecoverableCopy(Character c, int id)
        {
            if (c == null || c.inventory == null || !IsEndItem(id)) return false;
            return CountOrdinaryCopies(c, id) + RecoverableCopiesFor(c, id).Length > 0;
        }

        // Compatibility shim for existing planners. "Owned" is now terminal ordinary ownership;
        // broad possession is available only through the explicitly named recovery predicate.
        internal static bool IsOwned(Character c, int id)
        {
            return HasTerminalPiece(c, id);
        }

        internal static bool IsTerminalCombatCritical(Character c)
        {
            return c != null && c.settings.rebirthDifficulty == difficulty.sadistic
                   && (!HasTerminalPiece(c, SadisticBossItemId)
                       || !HasTerminalPiece(c, MechanicsEndgame.FinalTriggerItemId));
        }

        internal static IList<EndgameBranchState> Snapshot(Character c)
        {
            var result = new List<EndgameBranchState>();
            foreach (var requirement in MechanicsEndgame.AllRequirements())
            {
                var ordinaryCopies = CountOrdinaryCopies(c, requirement.ItemId);
                var recoverableCopies = RecoverableCopiesFor(c, requirement.ItemId);
                var terminal = ordinaryCopies == 1;
                var sourceSatisfied = SourceSatisfied(c, requirement);
                var grant = EvaluateCheckerGrant(requirement.ItemId, sourceSatisfied,
                    c == null ? 0 : c.highestSadisticBoss, ordinaryCopies, -1.0);
                result.Add(new EndgameBranchState
                {
                    ItemId = requirement.ItemId,
                    RequiredInventorySlot = requirement.TargetSlot,
                    Branch = requirement.Description,
                    Owned = terminal,
                    TerminalPiecePresent = terminal,
                    RecoverableCopyPresent = ordinaryCopies + recoverableCopies.Length > 0,
                    OrdinaryCopies = ordinaryCopies,
                    NonOrdinaryCopies = recoverableCopies.Length,
                    CanonicalOrdinarySlot = CanonicalOrdinarySlot(c, requirement.ItemId),
                    OrdinaryDuplicateCount = Math.Max(0, ordinaryCopies - 1),
                    RecoveryDebt = ordinaryCopies == 0 && recoverableCopies.Length > 0,
                    SourceSatisfied = sourceSatisfied,
                    PendingGrant = grant.PendingGrant,
                    CheckerEligible = grant.CheckerEligible,
                    NextCheckerEtaSeconds = grant.NextCheckerEtaSeconds,
                    RetryLegal = requirement.DependencyKind == EndDependencyKind.Titan14Kill
                                 && c != null
                                 && MechanicsEndgame.Titan14RetryActionable(c.effectiveBossID(),
                                     c.adventure.ratTitanDefeated,
                                     c.adventure.finalTitanDefeated, terminal),
                    Titan12MinimumVersion = requirement.MinimumTitanVersion,
                    Provenance = Provenance(requirement)
                });
            }
            return result;
        }

        internal static IEnumerable<EndgameBranchState> MissingBranches(Character c)
        {
            return Snapshot(c).Where(x => !x.TerminalPiecePresent);
        }

        internal static int NextMissingTitan12Version(Character c)
        {
            var ordinary = OrdinaryItemIds(c);
            // With no combat/capacity snapshot available, publish the highest useful cumulative
            // version. Execution owners should prefer PlanTitan12, which also proves capacity.
            return MechanicsEndgame.HighestUsefulTitan12Version(
                MechanicsEndgame.MaximumTitan12Version, ordinary);
        }

        internal static bool Titan14RetryActionable(Character c)
        {
            return c != null && MechanicsEndgame.Titan14RetryActionable(
                c.effectiveBossID(), c.adventure.ratTitanDefeated,
                c.adventure.finalTitanDefeated,
                HasTerminalPiece(c, MechanicsEndgame.FinalTriggerItemId));
        }

        private static int CountOrdinaryCopies(Character c, int id)
        {
            if (c == null || c.inventory == null || c.inventory.inventory == null) return 0;
            var count = 0;
            for (var i = 0; i < c.inventory.inventory.Count; i++)
            {
                var item = c.inventory.inventory[i];
                if (item != null && item.id == id) count++;
            }
            return count;
        }

        private static int CanonicalOrdinarySlot(Character c, int id)
        {
            if (c == null || c.inventory == null || c.inventory.inventory == null) return -1;
            var first = -1;
            var target = MechanicsEndgame.TargetSlotForItem(id);
            for (var i = 0; i < c.inventory.inventory.Count; i++)
            {
                var item = c.inventory.inventory[i];
                if (item == null || item.id != id) continue;
                if (i == target) return target;
                if (first < 0) first = i;
            }
            return first;
        }

        private static EndRecoverableCopy[] RecoverableCopiesFor(Character c, int id)
        {
            var copies = new List<EndRecoverableCopy>();
            if (c == null || c.inventory == null) return copies.ToArray();
            var inventory = c.inventory;
            if (inventory.daycare != null)
                for (var i = 0; i < inventory.daycare.Count; i++)
                {
                    var item = inventory.daycare[i];
                    if (item != null && item.id == id)
                        copies.Add(new EndRecoverableCopy(id,
                            EndRecoverableLocation.Daycare, i, item));
                }
            if (inventory.accs != null)
                for (var i = 0; i < inventory.accs.Count; i++)
                {
                    var item = inventory.accs[i];
                    if (item != null && item.id == id)
                        copies.Add(new EndRecoverableCopy(id,
                            EndRecoverableLocation.Accessory, i, item));
                }
            AddEquippedCopy(copies, id, inventory.head, EndRecoverableLocation.Head);
            AddEquippedCopy(copies, id, inventory.chest, EndRecoverableLocation.Chest);
            AddEquippedCopy(copies, id, inventory.legs, EndRecoverableLocation.Legs);
            AddEquippedCopy(copies, id, inventory.boots, EndRecoverableLocation.Boots);
            AddEquippedCopy(copies, id, inventory.weapon, EndRecoverableLocation.Weapon);
            AddEquippedCopy(copies, id, inventory.weapon2, EndRecoverableLocation.Weapon2);
            return copies.ToArray();
        }

        private static void AddEquippedCopy(
            IList<EndRecoverableCopy> copies, int id, object item,
            EndRecoverableLocation location)
        {
            var equipment = item as Equipment;
            if (equipment != null && equipment.id == id)
                copies.Add(new EndRecoverableCopy(id, location, 0, equipment));
        }

        private static bool SourceSatisfied(Character c, EndItemRequirement requirement)
        {
            if (c == null) return false;
            switch (requirement.DependencyKind)
            {
                case EndDependencyKind.PerkPurchase:
                    return c.adventure.itopod.perkLevel.Count > requirement.DependencyId
                           && c.adventure.itopod.perkLevel[requirement.DependencyId] >= 1;
                case EndDependencyKind.QuirkPurchase:
                    return c.beastQuest.quirkLevel.Count > requirement.DependencyId
                           && c.beastQuest.quirkLevel[requirement.DependencyId] >= 1;
                case EndDependencyKind.SadisticBoss:
                    return c.highestSadisticBoss >= requirement.DependencyId;
                case EndDependencyKind.EndHack:
                    return c.hacks.hacks.Count > 15 && c.hacks.hacks[15].level >= 1;
                case EndDependencyKind.WishCompletion:
                    return c.wishes.wishes.Count > requirement.DependencyId
                           && c.wishes.wishes[requirement.DependencyId].level >= 1;
                case EndDependencyKind.GerbilMove:
                    return c.adventure.move69Used >= 69;
                case EndDependencyKind.Titan14Kill:
                    return c.adventure.finalTitanDefeated;
                default:
                    return HasTerminalPiece(c, requirement.ItemId);
            }
        }

        private static string Provenance(EndItemRequirement requirement)
        {
            if (requirement.DependencyKind == EndDependencyKind.Titan12VersionDrop)
                return "Native zone42Drop cumulative roll; minimum version v"
                       + requirement.MinimumTitanVersion + ", all higher versions also roll it.";
            if (MechanicsEndgame.IsCheckerDeliveredItem(requirement.ItemId))
                return "Persistent source state plus native <=30-second checker after Sadistic Boss 225.";
            if (requirement.DependencyKind == EndDependencyKind.Titan14Kill)
                return "Repeatable T14 delivery; finalTitanDefeated records an attempt, ordinary 495 records completion.";
            return requirement.Description;
        }

        private static int[] OrdinaryItemIds(OrdinaryInventoryTopology topology)
        {
            var ids = new List<int>();
            for (var i = 0; i < topology.SlotCount; i++)
            {
                var itemId = topology.SlotAt(i).ItemId;
                if (itemId > 0) ids.Add(itemId);
            }
            return ids.ToArray();
        }

        private static int[] OrdinaryItemIds(Character c)
        {
            var ids = new List<int>();
            if (c == null || c.inventory == null || c.inventory.inventory == null)
                return ids.ToArray();
            for (var i = 0; i < c.inventory.inventory.Count; i++)
            {
                var item = c.inventory.inventory[i];
                if (item != null && item.id > 0) ids.Add(item.id);
            }
            return ids.ToArray();
        }

        private static void ValidateRecoverableCopies(
            int itemId, EndRecoverableCopy[] recoverableCopies)
        {
            if (!IsEndItem(itemId)) throw new ArgumentOutOfRangeException("itemId");
            if (recoverableCopies == null) throw new ArgumentNullException("recoverableCopies");
            for (var i = 0; i < recoverableCopies.Length; i++)
            {
                if (recoverableCopies[i] == null)
                    throw new ArgumentException("Recoverable-copy records cannot contain null.");
                if (recoverableCopies[i].ItemId != itemId)
                    throw new ArgumentException("Recoverable-copy item IDs must match the plan item.");
                for (var j = 0; j < i; j++)
                    if (object.ReferenceEquals(recoverableCopies[j].Identity,
                            recoverableCopies[i].Identity))
                        throw new ArgumentException("One non-ordinary identity cannot be counted twice.");
            }
        }
    }
}
