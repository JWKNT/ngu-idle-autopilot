/*
FILE PURPOSE

Purpose: This file is the pure, immutable model of NGU Idle's ordinary inventory topology. It keeps
physical object identity separate from item ID so future merge, swap, Daycare, terminal-placement,
and rollback transactions can prove that the same objects still occupy the intended locations.

Mechanism: Capture validates one item-ID and identity-token pair per serialized ordinary inventory
slot, canonicalizes empty ID-zero slots, rejects an object reference appearing in two occupied
slots, and clips the native current-space and merge-reserved-prefix boundaries. Comparison helpers
produce immutable identity proofs without reading or changing a game controller.

Inputs and outputs: Inputs are copied arrays of ordinary item IDs/object references plus native
curSpaces and totalInvMergeSlots values. Outputs are immutable slot/topology snapshots, ordinary-
only ownership queries, usable-slot indices, and exact identity-restoration proofs.

Invariants and safety: ID zero is empty. Every nonzero ordinary item requires a non-null, unique
reference-identity token. Only [usableStart, usableEnd) is native loot capacity; empty merge-prefix
slots and unpurchased trailing slots are never reported usable. All arrays returned to callers are
clones, and identity comparisons use ReferenceEquals rather than Equals or item-ID equality.

Extension points and non-goals: Live adapters may pass Equipment objects as identity tokens and may
attach saved-loadout/reference metadata outside this class. This file does not inspect equipment,
retarget a reference, select merge survivors, move an item, or treat Daycare/equipment as ordinary
inventory ownership.
*/
using System;
using System.Collections.Generic;

namespace NGUInjector.Autopilot
{
    internal sealed class OrdinaryInventorySlot
    {
        internal readonly int SlotIndex;
        internal readonly int ItemId;
        internal readonly object Identity;

        internal OrdinaryInventorySlot(int slotIndex, int itemId, object identity)
        {
            SlotIndex = slotIndex;
            ItemId = itemId;
            Identity = identity;
        }

        internal bool IsEmpty
        {
            get { return ItemId == 0; }
        }
    }

    internal sealed class OrdinaryInventoryTopology
    {
        private readonly OrdinaryInventorySlot[] _slots;
        private readonly int[] _usableFreeSlotIndices;

        internal readonly int DeclaredCurrentSpaces;
        internal readonly int DeclaredReservedPrefix;
        internal readonly int CurrentSpaces;
        internal readonly int UsableStart;
        internal readonly int UsableEnd;
        internal readonly int UsableSlotCount;
        internal readonly int UsableFreeSlotCount;

        internal OrdinaryInventoryTopology(
            OrdinaryInventorySlot[] slots,
            int declaredCurrentSpaces,
            int declaredReservedPrefix,
            int currentSpaces,
            int usableStart,
            int[] usableFreeSlotIndices)
        {
            _slots = (OrdinaryInventorySlot[])slots.Clone();
            _usableFreeSlotIndices = (int[])usableFreeSlotIndices.Clone();
            DeclaredCurrentSpaces = declaredCurrentSpaces;
            DeclaredReservedPrefix = declaredReservedPrefix;
            CurrentSpaces = currentSpaces;
            UsableStart = usableStart;
            UsableEnd = currentSpaces;
            UsableSlotCount = Math.Max(0, UsableEnd - UsableStart);
            UsableFreeSlotCount = _usableFreeSlotIndices.Length;
        }

        internal int SlotCount
        {
            get { return _slots.Length; }
        }

        internal OrdinaryInventorySlot SlotAt(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= _slots.Length)
                throw new ArgumentOutOfRangeException("slotIndex");
            return _slots[slotIndex];
        }

        internal int[] UsableFreeSlotIndices()
        {
            return (int[])_usableFreeSlotIndices.Clone();
        }

        internal bool HasOrdinaryItem(int itemId)
        {
            return CountOrdinaryItem(itemId) > 0;
        }

        internal int CountOrdinaryItem(int itemId)
        {
            if (itemId <= 0) throw new ArgumentOutOfRangeException("itemId");
            var count = 0;
            for (var i = 0; i < _slots.Length; i++)
                if (_slots[i].ItemId == itemId) count++;
            return count;
        }

        internal int[] OrdinarySlotsForItem(int itemId)
        {
            if (itemId <= 0) throw new ArgumentOutOfRangeException("itemId");
            var result = new List<int>();
            for (var i = 0; i < _slots.Length; i++)
                if (_slots[i].ItemId == itemId) result.Add(i);
            return result.ToArray();
        }

        internal int FindOrdinarySlotByIdentity(object identity)
        {
            if (identity == null) return -1;
            for (var i = 0; i < _slots.Length; i++)
                if (object.ReferenceEquals(_slots[i].Identity, identity)) return i;
            return -1;
        }

        internal object[] OccupiedOrdinaryIdentities()
        {
            var result = new List<object>();
            for (var i = 0; i < _slots.Length; i++)
                if (!_slots[i].IsEmpty) result.Add(_slots[i].Identity);
            return result.ToArray();
        }
    }

    internal sealed class OrdinaryIdentityProof
    {
        private readonly int[] _changedSlots;
        private readonly int[] _missingBeforeSlots;
        private readonly int[] _unexpectedAfterSlots;

        internal readonly bool ExactSlotIdentityRestored;
        internal readonly bool OccupiedObjectMultisetPreserved;
        internal readonly int BeforeOccupiedCount;
        internal readonly int AfterOccupiedCount;

        internal OrdinaryIdentityProof(
            bool exactSlotIdentityRestored,
            bool occupiedObjectMultisetPreserved,
            int beforeOccupiedCount,
            int afterOccupiedCount,
            int[] changedSlots,
            int[] missingBeforeSlots,
            int[] unexpectedAfterSlots)
        {
            ExactSlotIdentityRestored = exactSlotIdentityRestored;
            OccupiedObjectMultisetPreserved = occupiedObjectMultisetPreserved;
            BeforeOccupiedCount = beforeOccupiedCount;
            AfterOccupiedCount = afterOccupiedCount;
            _changedSlots = (int[])changedSlots.Clone();
            _missingBeforeSlots = (int[])missingBeforeSlots.Clone();
            _unexpectedAfterSlots = (int[])unexpectedAfterSlots.Clone();
        }

        internal int[] ChangedSlots()
        {
            return (int[])_changedSlots.Clone();
        }

        internal int[] MissingBeforeSlots()
        {
            return (int[])_missingBeforeSlots.Clone();
        }

        internal int[] UnexpectedAfterSlots()
        {
            return (int[])_unexpectedAfterSlots.Clone();
        }
    }

    internal static class PhysicalTopology
    {
        internal static OrdinaryInventoryTopology CaptureOrdinary(
            int[] itemIds,
            object[] identities,
            int currentSpaces,
            int reservedPrefix)
        {
            if (itemIds == null) throw new ArgumentNullException("itemIds");
            if (identities == null) throw new ArgumentNullException("identities");
            if (itemIds.Length != identities.Length)
                throw new ArgumentException("itemIds and identities must describe the same slots");

            var slots = new OrdinaryInventorySlot[itemIds.Length];
            var occupiedIdentities = new List<object>();
            for (var i = 0; i < itemIds.Length; i++)
            {
                var itemId = itemIds[i];
                if (itemId < 0) throw new ArgumentOutOfRangeException("itemIds");
                if (itemId == 0)
                {
                    slots[i] = new OrdinaryInventorySlot(i, 0, null);
                    continue;
                }

                var identity = identities[i];
                if (identity == null)
                    throw new ArgumentException("Every occupied ordinary slot requires an identity token.");
                for (var j = 0; j < occupiedIdentities.Count; j++)
                    if (object.ReferenceEquals(occupiedIdentities[j], identity))
                        throw new ArgumentException("One physical object cannot occupy two ordinary slots.");
                occupiedIdentities.Add(identity);
                slots[i] = new OrdinaryInventorySlot(i, itemId, identity);
            }

            var clippedCurrentSpaces = Clamp(currentSpaces, 0, itemIds.Length);
            var usableStart = Clamp(reservedPrefix, 0, clippedCurrentSpaces);
            var free = new List<int>();
            for (var i = usableStart; i < clippedCurrentSpaces; i++)
                if (slots[i].IsEmpty) free.Add(i);

            return new OrdinaryInventoryTopology(slots, currentSpaces, reservedPrefix,
                clippedCurrentSpaces, usableStart, free.ToArray());
        }

        internal static OrdinaryIdentityProof ProveOrdinaryIdentity(
            OrdinaryInventoryTopology before,
            OrdinaryInventoryTopology after)
        {
            if (before == null) throw new ArgumentNullException("before");
            if (after == null) throw new ArgumentNullException("after");

            var changedSlots = new List<int>();
            var comparedSlots = Math.Max(before.SlotCount, after.SlotCount);
            for (var i = 0; i < comparedSlots; i++)
            {
                if (i >= before.SlotCount || i >= after.SlotCount)
                {
                    changedSlots.Add(i);
                    continue;
                }
                var left = before.SlotAt(i);
                var right = after.SlotAt(i);
                if (left.ItemId != right.ItemId
                    || !object.ReferenceEquals(left.Identity, right.Identity))
                    changedSlots.Add(i);
            }

            var beforeIdentities = before.OccupiedOrdinaryIdentities();
            var afterIdentities = after.OccupiedOrdinaryIdentities();
            var missing = MissingIdentitySlots(before, after);
            var unexpected = MissingIdentitySlots(after, before);
            return new OrdinaryIdentityProof(
                changedSlots.Count == 0,
                beforeIdentities.Length == afterIdentities.Length
                    && missing.Length == 0 && unexpected.Length == 0,
                beforeIdentities.Length,
                afterIdentities.Length,
                changedSlots.ToArray(),
                missing,
                unexpected);
        }

        private static int[] MissingIdentitySlots(
            OrdinaryInventoryTopology expected,
            OrdinaryInventoryTopology actual)
        {
            var missing = new List<int>();
            for (var i = 0; i < expected.SlotCount; i++)
            {
                var slot = expected.SlotAt(i);
                if (!slot.IsEmpty && actual.FindOrdinarySlotByIdentity(slot.Identity) < 0)
                    missing.Add(i);
            }
            return missing.ToArray();
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            if (value < minimum) return minimum;
            return value > maximum ? maximum : value;
        }
    }
}
