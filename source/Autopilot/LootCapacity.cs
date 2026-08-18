/*
FILE PURPOSE

Purpose: This file is the authoritative pure admission model for native inventory loot and Card
deck spawns. It prevents reserved merge slots, stochastic expectations, or event-order omissions
from masquerading as guaranteed capacity for unique and irreversible rewards.

Mechanism: Integer requirements name exact worst-case batch, unique-delivery, and post-action
reserve slots. ProveOrdinary compares that requirement with a PhysicalTopology snapshot and returns
an immutable proof. Card helpers separately model native normal -> possible END -> Chonker ordering
and return an immutable deck proof. Source-backed factories expose the audited T12 and T14 bounds.

Inputs and outputs: Inputs are immutable ordinary topology snapshots or Card deck counts plus exact
integer requirements. Outputs contain scan bounds, free slots, required slots, capacity margin,
evidence grade, admission result, and a stable reason suitable for telemetry/preflight decisions.

Invariants and safety: Native ordinary insertion can use only [totalInvMergeSlots(), curSpaces()).
Batch and reserve components are additive because native insertion never merges on admission. A
requirement containing a unique delivery is admitted only with exact-worst-case evidence; expected
value, mean, percentile, or probability can describe risk but can never authorize that mutation.
Normal+possible-END needs two deck slots; a simultaneously due Chonker raises the bound to three.

Extension points and non-goals: Managers may build additional exact source catalogs and recheck a
proof immediately before mutation. This layer does not forecast service cadence, inspect filters,
invoke loot/Card controllers, claim physical postconditions, or authorize an execution lease.
*/
using System;

namespace NGUInjector.Autopilot
{
    internal enum CapacityEvidenceKind
    {
        ExactWorstCase,
        ExpectedValueOnly
    }

    internal sealed class LootCapacityRequirement
    {
        internal readonly string Key;
        internal readonly int ExactBatchSlots;
        internal readonly int UniqueDeliverySlots;
        internal readonly int PostActionReserveSlots;
        internal readonly CapacityEvidenceKind EvidenceKind;
        internal readonly double DescriptiveExpectedObjects;

        internal LootCapacityRequirement(
            string key,
            int exactBatchSlots,
            int uniqueDeliverySlots,
            int postActionReserveSlots,
            CapacityEvidenceKind evidenceKind,
            double descriptiveExpectedObjects)
        {
            if (exactBatchSlots < 0) throw new ArgumentOutOfRangeException("exactBatchSlots");
            if (uniqueDeliverySlots < 0) throw new ArgumentOutOfRangeException("uniqueDeliverySlots");
            if (postActionReserveSlots < 0) throw new ArgumentOutOfRangeException("postActionReserveSlots");
            if (double.IsNaN(descriptiveExpectedObjects) || descriptiveExpectedObjects < 0.0)
                throw new ArgumentOutOfRangeException("descriptiveExpectedObjects");

            checked
            {
                var ignored = exactBatchSlots + uniqueDeliverySlots + postActionReserveSlots;
            }
            Key = key ?? string.Empty;
            ExactBatchSlots = exactBatchSlots;
            UniqueDeliverySlots = uniqueDeliverySlots;
            PostActionReserveSlots = postActionReserveSlots;
            EvidenceKind = evidenceKind;
            DescriptiveExpectedObjects = descriptiveExpectedObjects;
        }

        internal int RequiredFreeSlots
        {
            get { return ExactBatchSlots + UniqueDeliverySlots + PostActionReserveSlots; }
        }

        internal bool ContainsUniqueDelivery
        {
            get { return UniqueDeliverySlots > 0; }
        }

        internal static LootCapacityRequirement ExactBatch(
            string key, int exactBatchSlots, int postActionReserveSlots)
        {
            return new LootCapacityRequirement(key, exactBatchSlots, 0, postActionReserveSlots,
                CapacityEvidenceKind.ExactWorstCase, exactBatchSlots);
        }

        internal static LootCapacityRequirement ExactUniqueDelivery(
            string key, int exactPrecedingBatchSlots, int uniqueDeliverySlots,
            int postActionReserveSlots)
        {
            return new LootCapacityRequirement(key, exactPrecedingBatchSlots, uniqueDeliverySlots,
                postActionReserveSlots, CapacityEvidenceKind.ExactWorstCase,
                exactPrecedingBatchSlots + uniqueDeliverySlots);
        }

        internal static LootCapacityRequirement ExpectedValueDescription(
            string key, double expectedObjects, int proposedReserveSlots, bool containsUniqueDelivery)
        {
            return new LootCapacityRequirement(key, proposedReserveSlots,
                containsUniqueDelivery ? 1 : 0, 0,
                CapacityEvidenceKind.ExpectedValueOnly, expectedObjects);
        }
    }

    internal sealed class LootCapacityProof
    {
        private readonly int[] _usableFreeSlotIndices;

        internal readonly string RequirementKey;
        internal readonly int UsableStart;
        internal readonly int UsableEnd;
        internal readonly int UsableSlotCount;
        internal readonly int UsableFreeSlotCount;
        internal readonly int ExactBatchSlots;
        internal readonly int UniqueDeliverySlots;
        internal readonly int PostActionReserveSlots;
        internal readonly int RequiredFreeSlots;
        internal readonly int CapacityMargin;
        internal readonly CapacityEvidenceKind EvidenceKind;
        internal readonly bool Admitted;
        internal readonly string Reason;

        internal LootCapacityProof(
            OrdinaryInventoryTopology topology,
            LootCapacityRequirement requirement,
            bool admitted,
            string reason)
        {
            RequirementKey = requirement.Key;
            UsableStart = topology.UsableStart;
            UsableEnd = topology.UsableEnd;
            UsableSlotCount = topology.UsableSlotCount;
            UsableFreeSlotCount = topology.UsableFreeSlotCount;
            ExactBatchSlots = requirement.ExactBatchSlots;
            UniqueDeliverySlots = requirement.UniqueDeliverySlots;
            PostActionReserveSlots = requirement.PostActionReserveSlots;
            RequiredFreeSlots = requirement.RequiredFreeSlots;
            CapacityMargin = UsableFreeSlotCount - RequiredFreeSlots;
            EvidenceKind = requirement.EvidenceKind;
            Admitted = admitted;
            Reason = reason ?? string.Empty;
            _usableFreeSlotIndices = topology.UsableFreeSlotIndices();
        }

        internal int[] UsableFreeSlotIndices()
        {
            return (int[])_usableFreeSlotIndices.Clone();
        }
    }

    internal sealed class CardDeckRequirement
    {
        internal readonly string Key;
        internal readonly int NormalSpawns;
        internal readonly int ChonkerSpawns;
        internal readonly bool ReservePossibleEndDelivery;

        internal CardDeckRequirement(
            string key, int normalSpawns, int chonkerSpawns, bool reservePossibleEndDelivery)
        {
            if (normalSpawns < 0) throw new ArgumentOutOfRangeException("normalSpawns");
            if (chonkerSpawns < 0) throw new ArgumentOutOfRangeException("chonkerSpawns");
            if (reservePossibleEndDelivery && normalSpawns == 0)
                throw new ArgumentException("An END Card opportunity requires a normal spawn.");
            checked
            {
                var ignored = normalSpawns + chonkerSpawns
                    + (reservePossibleEndDelivery ? 1 : 0);
            }
            Key = key ?? string.Empty;
            NormalSpawns = normalSpawns;
            ChonkerSpawns = chonkerSpawns;
            ReservePossibleEndDelivery = reservePossibleEndDelivery;
        }

        internal int RequiredFreeSlots
        {
            get
            {
                return NormalSpawns + ChonkerSpawns
                    + (ReservePossibleEndDelivery ? 1 : 0);
            }
        }

        internal static CardDeckRequirement LiveFrame(
            bool normalDue, bool chonkerDue, bool protectPossibleEndDelivery)
        {
            return new CardDeckRequirement("live-card-frame",
                normalDue ? 1 : 0,
                chonkerDue ? 1 : 0,
                normalDue && protectPossibleEndDelivery);
        }
    }

    internal sealed class CardDeckCapacityProof
    {
        internal readonly string RequirementKey;
        internal readonly int DeckCount;
        internal readonly int MaximumDeckSize;
        internal readonly int FreeSlots;
        internal readonly int NormalSpawns;
        internal readonly int ChonkerSpawns;
        internal readonly bool ReservePossibleEndDelivery;
        internal readonly int RequiredFreeSlots;
        internal readonly int CapacityMargin;
        internal readonly bool Admitted;
        internal readonly string Reason;

        internal CardDeckCapacityProof(
            int deckCount,
            int maximumDeckSize,
            CardDeckRequirement requirement)
        {
            RequirementKey = requirement.Key;
            DeckCount = deckCount;
            MaximumDeckSize = maximumDeckSize;
            FreeSlots = maximumDeckSize - deckCount;
            NormalSpawns = requirement.NormalSpawns;
            ChonkerSpawns = requirement.ChonkerSpawns;
            ReservePossibleEndDelivery = requirement.ReservePossibleEndDelivery;
            RequiredFreeSlots = requirement.RequiredFreeSlots;
            CapacityMargin = FreeSlots - RequiredFreeSlots;
            Admitted = CapacityMargin >= 0;
            Reason = Admitted
                ? "Exact Card event-order slack is available."
                : "Insufficient Card deck slack for the exact event-order requirement.";
        }
    }

    internal static class LootCapacity
    {
        internal const int Titan12Item483RequiredFreeSlots = 11;
        internal const int Titan12Item489RequiredFreeSlots = 14;
        internal const int Titan12Item493RequiredFreeSlots = 16;
        internal const int Titan12Item484RequiredFreeSlots = 18;

        internal static LootCapacityProof ProveOrdinary(
            OrdinaryInventoryTopology topology,
            LootCapacityRequirement requirement)
        {
            if (topology == null) throw new ArgumentNullException("topology");
            if (requirement == null) throw new ArgumentNullException("requirement");

            if (requirement.ContainsUniqueDelivery
                && requirement.EvidenceKind != CapacityEvidenceKind.ExactWorstCase)
            {
                return new LootCapacityProof(topology, requirement, false,
                    "Expected-value capacity cannot authorize a unique delivery.");
            }

            var admitted = topology.UsableFreeSlotCount >= requirement.RequiredFreeSlots;
            return new LootCapacityProof(topology, requirement, admitted,
                admitted
                    ? "Exact native ordinary-loot capacity is available."
                    : "Insufficient empty slots in the native ordinary-loot scan interval.");
        }

        internal static LootCapacityRequirement Titan12EndPiece(int itemId)
        {
            int required;
            switch (itemId)
            {
                case 483: required = Titan12Item483RequiredFreeSlots; break;
                case 489: required = Titan12Item489RequiredFreeSlots; break;
                case 493: required = Titan12Item493RequiredFreeSlots; break;
                case 484: required = Titan12Item484RequiredFreeSlots; break;
                default: throw new ArgumentOutOfRangeException("itemId");
            }

            // The audited bound includes the requested unique piece and every possible earlier
            // object in native zone42Drop ordering. Keep the final slot typed as unique so a future
            // caller cannot weaken this factory to expected-value evidence without being denied.
            return LootCapacityRequirement.ExactUniqueDelivery(
                "titan12-end-piece-" + itemId, required - 1, 1, 0);
        }

        internal static LootCapacityRequirement Titan14FinalPiece()
        {
            return LootCapacityRequirement.ExactUniqueDelivery(
                "titan14-final-piece-495", 0, 1, 0);
        }

        internal static LootCapacityRequirement EndCardInventoryPiece()
        {
            return LootCapacityRequirement.ExactUniqueDelivery(
                "end-card-inventory-piece-492", 0, 1, 0);
        }

        internal static CardDeckCapacityProof ProveDeck(
            int deckCount,
            int maximumDeckSize,
            CardDeckRequirement requirement)
        {
            if (maximumDeckSize < 0) throw new ArgumentOutOfRangeException("maximumDeckSize");
            if (deckCount < 0 || deckCount > maximumDeckSize)
                throw new ArgumentOutOfRangeException("deckCount");
            if (requirement == null) throw new ArgumentNullException("requirement");
            return new CardDeckCapacityProof(deckCount, maximumDeckSize, requirement);
        }
    }
}
