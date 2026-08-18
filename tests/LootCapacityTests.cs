using System;

/*
FILE PURPOSE

This isolated executable regression-tests the pure ordinary-inventory topology, exact loot-capacity
proofs, and Card deck-slack proofs. It constructs only in-memory item IDs and identity tokens; it
does not load Unity, read a save, invoke native controllers, or mutate runtime/build/game state.
*/
internal static class LootCapacityTests
{
    private static int _assertions;

    private static void Assert(bool value, string message)
    {
        _assertions++;
        if (!value) throw new Exception("FAIL: " + message);
    }

    private static void AssertThrows<T>(Action action, string message) where T : Exception
    {
        _assertions++;
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception("FAIL: " + message);
    }

    private static NGUInjector.Autopilot.OrdinaryInventoryTopology EmptyTopology(
        int slots, int currentSpaces, int reservedPrefix)
    {
        return NGUInjector.Autopilot.PhysicalTopology.CaptureOrdinary(
            new int[slots], new object[slots], currentSpaces, reservedPrefix);
    }

    private static void TestReservedPrefixAndCurrentSpaceBoundaries()
    {
        var ids = new[] { 0, 0, 7, 0, 0, 0 };
        var identities = new object[] { null, null, new object(), null, null, null };
        var topology = NGUInjector.Autopilot.PhysicalTopology.CaptureOrdinary(
            ids, identities, 5, 2);

        Assert(topology.UsableStart == 2, "merge-reserved prefix starts the native scan at slot 2");
        Assert(topology.UsableEnd == 5, "curSpaces excludes the unpurchased trailing slot");
        Assert(topology.UsableSlotCount == 3, "usable total is hi minus lo");
        Assert(topology.UsableFreeSlotCount == 2,
            "empty reserved slots and empty trailing slots are not loot capacity");
        var free = topology.UsableFreeSlotIndices();
        Assert(free.Length == 2 && free[0] == 3 && free[1] == 4,
            "only empty slots inside the native interval are returned");

        var allReserved = EmptyTopology(5, 5, 99);
        Assert(allReserved.UsableStart == 5 && allReserved.UsableFreeSlotCount == 0,
            "oversized reserved prefix clips to current spaces");
        var noReserved = EmptyTopology(5, 99, -4);
        Assert(noReserved.CurrentSpaces == 5 && noReserved.UsableStart == 0
            && noReserved.UsableFreeSlotCount == 5,
            "current spaces and negative prefix clip to serialized boundaries");
    }

    private static void TestOrdinaryOwnershipAndIdentity()
    {
        var a = new object();
        var b = new object();
        var ids = new[] { 480, 0, 480, 12 };
        var identities = new[] { a, null, b, new object() };
        var original = NGUInjector.Autopilot.PhysicalTopology.CaptureOrdinary(
            ids, identities, 4, 0);

        ids[0] = 0;
        identities[0] = null;
        Assert(original.CountOrdinaryItem(480) == 2,
            "topology copies source arrays and reports ordinary-only duplicate count");
        Assert(original.FindOrdinarySlotByIdentity(a) == 0,
            "physical object identity is reference-based and retained");

        var swapped = NGUInjector.Autopilot.PhysicalTopology.CaptureOrdinary(
            new[] { 480, 0, 480, 12 }, new[] { b, null, a, original.SlotAt(3).Identity }, 4, 0);
        var swapProof = NGUInjector.Autopilot.PhysicalTopology.ProveOrdinaryIdentity(original, swapped);
        Assert(!swapProof.ExactSlotIdentityRestored && swapProof.OccupiedObjectMultisetPreserved,
            "a swap preserves the exact object multiset but changes slot identity");
        Assert(swapProof.ChangedSlots().Length == 2,
            "identity proof identifies both changed ordinary slots");

        var removed = NGUInjector.Autopilot.PhysicalTopology.CaptureOrdinary(
            new[] { 480, 0, 0, 12 }, new[] { a, null, null, original.SlotAt(3).Identity }, 4, 0);
        var removalProof = NGUInjector.Autopilot.PhysicalTopology.ProveOrdinaryIdentity(original, removed);
        Assert(!removalProof.OccupiedObjectMultisetPreserved
            && removalProof.MissingBeforeSlots().Length == 1,
            "deleting an object cannot pass the multiset-preservation proof");

        AssertThrows<ArgumentException>(() =>
            NGUInjector.Autopilot.PhysicalTopology.CaptureOrdinary(
                new[] { 1, 2 }, new[] { a, a }, 2, 0),
            "one object reference cannot occupy two slots");
        AssertThrows<ArgumentException>(() =>
            NGUInjector.Autopilot.PhysicalTopology.CaptureOrdinary(
                new[] { 1 }, new object[] { null }, 1, 0),
            "occupied item without identity fails closed");
    }

    private static void TestExactBatchAndUniqueAdmission()
    {
        var eighteen = EmptyTopology(20, 20, 2);
        var t12 = NGUInjector.Autopilot.LootCapacity.ProveOrdinary(eighteen,
            NGUInjector.Autopilot.LootCapacity.Titan12EndPiece(484));
        Assert(t12.Admitted && t12.RequiredFreeSlots == 18 && t12.CapacityMargin == 0,
            "T12 item 484 requires and admits exactly eighteen usable free slots");

        var seventeen = EmptyTopology(20, 20, 3);
        var blockedT12 = NGUInjector.Autopilot.LootCapacity.ProveOrdinary(seventeen,
            NGUInjector.Autopilot.LootCapacity.Titan12EndPiece(484));
        Assert(!blockedT12.Admitted && blockedT12.CapacityMargin == -1,
            "seventeen usable slots cannot guarantee the ordered T12 batch");
        Assert(NGUInjector.Autopilot.LootCapacity.Titan12EndPiece(483).RequiredFreeSlots == 11
            && NGUInjector.Autopilot.LootCapacity.Titan12EndPiece(489).RequiredFreeSlots == 14
            && NGUInjector.Autopilot.LootCapacity.Titan12EndPiece(493).RequiredFreeSlots == 16,
            "all earlier T12 END-piece ordered batch bounds are source-backed");

        var one = EmptyTopology(1, 1, 0);
        var t14 = NGUInjector.Autopilot.LootCapacity.ProveOrdinary(one,
            NGUInjector.Autopilot.LootCapacity.Titan14FinalPiece());
        Assert(t14.Admitted && t14.UniqueDeliverySlots == 1,
            "T14's guaranteed item requires one exact unique-delivery slot");
        var noUsable = EmptyTopology(1, 1, 1);
        Assert(!NGUInjector.Autopilot.LootCapacity.ProveOrdinary(noUsable,
                NGUInjector.Autopilot.LootCapacity.Titan14FinalPiece()).Admitted,
            "a reserved-only empty slot cannot authorize T14");

        var batchAndReserve = NGUInjector.Autopilot.LootCapacityRequirement.ExactUniqueDelivery(
            "batch-unique-reserve", 4, 1, 2);
        Assert(batchAndReserve.RequiredFreeSlots == 7,
            "preceding batch, unique delivery, and post-action reserve are additive");
    }

    private static void TestExpectedValueCannotAuthorizeUnique()
    {
        var plenty = EmptyTopology(100, 100, 0);
        var expectedUnique = NGUInjector.Autopilot.LootCapacityRequirement.ExpectedValueDescription(
            "mean-is-not-proof", 0.01, 1, true);
        var proof = NGUInjector.Autopilot.LootCapacity.ProveOrdinary(plenty, expectedUnique);
        Assert(!proof.Admitted,
            "expected value cannot admit a unique drop even with abundant physical space");
        Assert(proof.Reason.Contains("Expected-value"),
            "proof reports that the evidence grade, not free-space count, blocked admission");

        var free = proof.UsableFreeSlotIndices();
        free[0] = 99;
        Assert(proof.UsableFreeSlotIndices()[0] == 0,
            "proof free-slot evidence is immutable through cloned arrays");
    }

    private static void TestCardDeckSlack()
    {
        var normalEnd = NGUInjector.Autopilot.CardDeckRequirement.LiveFrame(true, false, true);
        Assert(normalEnd.RequiredFreeSlots == 2,
            "normal plus possible END delivery requires two deck slots");
        Assert(NGUInjector.Autopilot.LootCapacity.ProveDeck(8, 10, normalEnd).Admitted,
            "two free deck slots admit normal plus END");
        Assert(!NGUInjector.Autopilot.LootCapacity.ProveDeck(9, 10, normalEnd).Admitted,
            "one free deck slot cannot protect the END opportunity");

        var normalEndChonker = NGUInjector.Autopilot.CardDeckRequirement.LiveFrame(true, true, true);
        Assert(normalEndChonker.RequiredFreeSlots == 3,
            "simultaneously due Chonker raises normal plus END slack to three");
        Assert(NGUInjector.Autopilot.LootCapacity.ProveDeck(7, 10, normalEndChonker).Admitted,
            "three free slots admit normal, END, and Chonker");
        Assert(!NGUInjector.Autopilot.LootCapacity.ProveDeck(8, 10, normalEndChonker).Admitted,
            "two free slots cannot guarantee all three ordered additions");

        var offline = new NGUInjector.Autopilot.CardDeckRequirement(
            "offline-chonker-first", 4, 2, true);
        Assert(offline.RequiredFreeSlots == 7,
            "offline batch reserve is C plus N plus one protected END slot");
    }

    public static int Main()
    {
        try
        {
            TestReservedPrefixAndCurrentSpaceBoundaries();
            TestOrdinaryOwnershipAndIdentity();
            TestExactBatchAndUniqueAdmission();
            TestExpectedValueCannotAuthorizeUnique();
            TestCardDeckSlack();
            Console.WriteLine("Loot capacity tests passed: " + _assertions + " assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
