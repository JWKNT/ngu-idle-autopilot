/*
FILE PURPOSE

Purpose: This file is the pure terminal dependency registry for NGU Idle's valid ending.  It owns the
sixteen END item IDs, their independent source dependencies, T12 version requirements, exact
inventory-slot placement, T13/T14 access gates, and the final Ctrl-click trigger metadata.

Mechanism: Immutable requirement records are built from the installed game source.  Lookup and
validation helpers let inventory protection, the dependency scheduler, telemetry, and the final
transaction preflight share one canonical graph without sorting or moving a live item.

Inputs and outputs: Inputs are item IDs, inventory slot numbers, or a read-only item-ID snapshot.
Outputs are requirement metadata, protection predicates, and placement validation results.  This
file performs no controller call, inventory move, merge, transform, daycare action, or end trigger.

Invariants and safety: IDs 480..495 are globally protected even before their branch becomes critical.
The required slots are 0..3, 12..15, 24..27, and 36..39 in ascending item order.  Item 495 must be
in slot 39 and Ctrl-clicked only after all sixteen positions and the native prerequisites are
verified.  T12 pieces require versions 1, 4, 2, and 3 for IDs 483, 484, 489, and 493 respectively.

Extension points and non-goals: The scheduler may attach ETA/shadow-price state to these stable keys.
Live inventory identity, transaction rollback, boss/Titan combat, drop probabilities, and permission
to show the ending belong outside this registry.
*/
using System;
using System.Collections.Generic;

namespace NGUInjector.Autopilot
{
    internal enum EndDependencyKind
    {
        PendantTransformation,
        GerbilMove,
        PerkPurchase,
        Titan12VersionDrop,
        LootyTransformation,
        QuirkPurchase,
        SadisticBoss,
        EndHack,
        WishCompletion,
        ItopodDrop,
        EndCard,
        BloodSpell,
        Titan14Kill
    }

    internal sealed class EndItemRequirement
    {
        internal readonly int ItemId;
        internal readonly int TargetSlot;
        internal readonly EndDependencyKind DependencyKind;
        internal readonly int DependencyId;
        internal readonly double NumericRequirement;
        internal readonly int TitanVersion;
        internal readonly string Description;

        internal EndItemRequirement(
            int itemId, int targetSlot, EndDependencyKind dependencyKind,
            int dependencyId, double numericRequirement, int titanVersion, string description)
        {
            ItemId = itemId;
            TargetSlot = targetSlot;
            DependencyKind = dependencyKind;
            DependencyId = dependencyId;
            NumericRequirement = numericRequirement;
            TitanVersion = titanVersion;
            Description = description ?? string.Empty;
        }
    }

    internal enum EndGateKey
    {
        Titan13Access,
        Titan14Access,
        FinalSequence
    }

    internal sealed class EndGateRequirement
    {
        internal readonly EndGateKey Key;
        internal readonly int RequiredSadisticBoss;
        internal readonly bool RequiresTitan13Defeated;
        internal readonly bool RequiresAllEndItemsPlaced;
        internal readonly string Description;

        internal EndGateRequirement(
            EndGateKey key, int requiredSadisticBoss, bool requiresTitan13Defeated,
            bool requiresAllEndItemsPlaced, string description)
        {
            Key = key;
            RequiredSadisticBoss = requiredSadisticBoss;
            RequiresTitan13Defeated = requiresTitan13Defeated;
            RequiresAllEndItemsPlaced = requiresAllEndItemsPlaced;
            Description = description ?? string.Empty;
        }
    }

    internal static class MechanicsEndgame
    {
        internal const int FirstEndItemId = 480;
        internal const int LastEndItemId = 495;
        internal const int FinalTriggerItemId = 495;
        internal const int FinalTriggerSlot = 39;
        internal const int ShutDownWishId = 203;
        internal const int ItopodDropMinimumFloor = 1450;
        internal const double EndBloodCost = 5.0e22;

        private static readonly EndItemRequirement[] Requirements = BuildRequirements();
        private static readonly EndGateRequirement[] Gates =
        {
            new EndGateRequirement(EndGateKey.Titan13Access, 295, false, false,
                "T13 requires Sadistic Fight Boss 295."),
            new EndGateRequirement(EndGateKey.Titan14Access, 300, true, false,
                "T14 requires Sadistic Fight Boss 300 and the confirmed T13-defeated flag."),
            new EndGateRequirement(EndGateKey.FinalSequence, 300, true, true,
                "The final sequence requires every END item in its exact slot, then Ctrl-click item 495 in slot 39.")
        };

        internal static EndItemRequirement[] AllRequirements()
        {
            return (EndItemRequirement[])Requirements.Clone();
        }

        internal static EndGateRequirement[] AllGates()
        {
            return (EndGateRequirement[])Gates.Clone();
        }

        internal static bool IsProtectedItem(int itemId)
        {
            return itemId >= FirstEndItemId && itemId <= LastEndItemId;
        }

        internal static EndItemRequirement FindByItemId(int itemId)
        {
            for (var i = 0; i < Requirements.Length; i++)
                if (Requirements[i].ItemId == itemId) return Requirements[i];
            throw new ArgumentOutOfRangeException("itemId");
        }

        internal static int TargetSlotForItem(int itemId)
        {
            return FindByItemId(itemId).TargetSlot;
        }

        internal static int RequiredItemForSlot(int slot)
        {
            if (slot < 0) throw new ArgumentOutOfRangeException("slot");
            for (var i = 0; i < Requirements.Length; i++)
                if (Requirements[i].TargetSlot == slot) return Requirements[i].ItemId;
            return -1;
        }

        internal static bool ValidatePlacement(int[] inventoryItemIds)
        {
            return MisplacedOrMissingItems(inventoryItemIds).Length == 0;
        }

        internal static int[] MisplacedOrMissingItems(int[] inventoryItemIds)
        {
            if (inventoryItemIds == null) throw new ArgumentNullException("inventoryItemIds");
            var missing = new List<int>();
            for (var i = 0; i < Requirements.Length; i++)
            {
                var requirement = Requirements[i];
                if (requirement.TargetSlot >= inventoryItemIds.Length
                    || inventoryItemIds[requirement.TargetSlot] != requirement.ItemId)
                    missing.Add(requirement.ItemId);
            }
            return missing.ToArray();
        }

        private static EndItemRequirement[] BuildRequirements()
        {
            return new[]
            {
                R(480, 0, EndDependencyKind.PendantTransformation, -1, 0, 0,
                    "Complete the final Ascended Pendant transformation chain."),
                R(481, 1, EndDependencyKind.GerbilMove, 69, 69, 0,
                    "Transform Gerbil, unlock move 69, and use it 69 times."),
                R(482, 2, EndDependencyKind.PerkPurchase, 231, 1, 0,
                    "Buy Error perk 231."),
                R(483, 3, EndDependencyKind.Titan12VersionDrop, 12, 1, 1,
                    "Obtain the T12 version-1 random END piece."),
                R(484, 12, EndDependencyKind.Titan12VersionDrop, 12, 1, 4,
                    "Obtain the T12 version-4 random END piece."),
                R(485, 13, EndDependencyKind.LootyTransformation, -1, 0, 0,
                    "Complete the final Looty transformation chain."),
                R(486, 14, EndDependencyKind.QuirkPurchase, 176, 1, 0,
                    "Buy Problem quirk 176."),
                R(487, 15, EndDependencyKind.SadisticBoss, 300, 1, 0,
                    "Kill Fight Boss 300 in Sadistic."),
                R(488, 24, EndDependencyKind.EndHack, -1, 1, 0,
                    "Max the ordinary Hacks and complete the unlocked END Hack."),
                R(489, 25, EndDependencyKind.Titan12VersionDrop, 12, 1, 2,
                    "Obtain the T12 version-2 random END piece."),
                R(490, 26, EndDependencyKind.WishCompletion, ShutDownWishId, 1, 0,
                    "Complete Shut Down Wish 203."),
                R(491, 27, EndDependencyKind.ItopodDrop, ItopodDropMinimumFloor, 1, 0,
                    "Obtain the random ITOPOD END drop at floor 1450 or above."),
                R(492, 36, EndDependencyKind.EndCard, -1, 1, 0,
                    "Cast the special END Card."),
                R(493, 37, EndDependencyKind.Titan12VersionDrop, 12, 1, 3,
                    "Obtain the T12 version-3 random END piece."),
                R(494, 38, EndDependencyKind.BloodSpell, -1, EndBloodCost, 0,
                    "Cast the 5e22-Blood END spell."),
                R(495, 39, EndDependencyKind.Titan14Kill, 14, 1, 0,
                    "Kill T14; the END piece is guaranteed.")
            };
        }

        private static EndItemRequirement R(
            int itemId, int slot, EndDependencyKind kind, int dependencyId,
            double requirement, int titanVersion, string description)
        {
            return new EndItemRequirement(itemId, slot, kind, dependencyId,
                requirement, titanVersion, description);
        }
    }
}
