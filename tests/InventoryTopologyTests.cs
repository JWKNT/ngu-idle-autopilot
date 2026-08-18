using System;
using System.Collections.Generic;
using System.IO;
using NGUInjector.Managers;

/*
FILE PURPOSE

Purpose: This isolated executable regression-tests the pure inventory topology policy and statically
guards the live Inventory/Daycare integrations that protect irreversible physical item state.

Mechanism: Primitive arrays exercise all-39 boost gating, native merge arithmetic, per-loadout
retarget legality, and exact progression-unlock postconditions. Read-only source checks ensure the
live managers use pairwise merges, build-pinned consumption, usable-slot Daycare retrieval, selector
restoration, and every audited state-machine ID.

Inputs and outputs: Inputs are in-memory Boolean/integer fixtures and the two maintained source files.
Output is an assertion count/process status. The suite does not load Unity, a save, runtime telemetry,
the game process, or invoke any native mutation.

Invariants and safety: Reserved slots cannot become retrieval capacity; a loadout context cannot be
retargeted onto a survivor it already uses; a duplicated source reference fails closed; Cards unlock
requires exactly one first Card; and auto-transform remains disabled until IDs 1-39 are all complete.

Extension points and non-goals: Live copied-save fault injection belongs to the integration owner and
root MutationCoordinator suites. Add pure cases here whenever InventoryTopologyPolicy gains a new
admission/postcondition rule.
*/
internal static class InventoryTopologyTests
{
    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static void TestBoostCompletionGate()
    {
        Assert(!InventoryTopologyPolicy.AllBoostEntriesMaxxed(null),
            "missing Item List state fails closed");
        Assert(!InventoryTopologyPolicy.AllBoostEntriesMaxxed(new bool[39]),
            "a list without ID 39 fails closed");
        var maxxed = new bool[40];
        for (var id = 1; id <= 39; id++) maxxed[id] = true;
        Assert(InventoryTopologyPolicy.AllBoostEntriesMaxxed(maxxed),
            "all exact boost IDs 1-39 admit auto-transform");
        for (var missing = 1; missing <= 39; missing++)
        {
            maxxed[missing] = false;
            Assert(!InventoryTopologyPolicy.AllBoostEntriesMaxxed(maxxed),
                "each individual unfinished boost ID blocks auto-transform: " + missing);
            maxxed[missing] = true;
        }
    }

    private static void TestMergeArithmetic()
    {
        Assert(InventoryTopologyPolicy.MergedLevel(0, 0, false) == 1,
            "fresh level-zero copy contributes one level");
        Assert(InventoryTopologyPolicy.MergedLevel(40, 59, false) == 100,
            "non-MacGuffin merge includes the native plus-one contribution");
        Assert(InventoryTopologyPolicy.MergedLevel(99, 99, false) == 100,
            "ordinary equipment caps at 100");
        Assert(InventoryTopologyPolicy.MergedLevel(99, 99, true) == 199,
            "MacGuffin levels do not cap at 100");
        Assert(InventoryTopologyPolicy.MergedLevel(int.MaxValue, 10, true) == int.MaxValue,
            "native integer overflow guard saturates MacGuffin level");
    }

    private static void TestReferenceRetargetPolicy()
    {
        Assert(InventoryTopologyPolicy.CanRetargetContext(
                new[] {7, 8, 9}, 7, 4),
            "one source use can retarget to an unused survivor");
        Assert(!InventoryTopologyPolicy.CanRetargetContext(
                new[] {7, 4, 9}, 7, 4),
            "one context cannot use the survivor twice");
        Assert(!InventoryTopologyPolicy.CanRetargetContext(
                new[] {7, 7, 9}, 7, 4),
            "a duplicated source reference is malformed and fails closed");
        Assert(InventoryTopologyPolicy.CanRetargetContext(
                new[] {2, 4, 9}, 7, 4),
            "an unrelated context does not block consolidation in another loadout");
        Assert(InventoryTopologyPolicy.CanRetargetContext(
                new[] {7}, 7, 7),
            "a no-op identity mapping is always safe");
    }

    private static void TestUnlockPostconditions()
    {
        Assert(InventoryTopologyPolicy.UnlockPostcondition(ProgressionUnlockKind.Hacks,
                true, false, true, true, 0, 0),
            "294 requires debit plus Hacks and Resource 3");
        Assert(!InventoryTopologyPolicy.UnlockPostcondition(ProgressionUnlockKind.Hacks,
                true, false, true, false, 0, 0),
            "Hacks without Resource 3 is a partial failure");
        Assert(InventoryTopologyPolicy.UnlockPostcondition(ProgressionUnlockKind.Wishes,
                true, false, true, true, 0, 0),
            "343 requires exact debit and Wishes false-to-true");
        Assert(!InventoryTopologyPolicy.UnlockPostcondition(ProgressionUnlockKind.Wishes,
                false, false, true, true, 0, 0),
            "a flag without exact object debit is not success");
        Assert(InventoryTopologyPolicy.UnlockPostcondition(ProgressionUnlockKind.Cards,
                true, false, true, true, 2, 3),
            "391 requires debit, Cards flag, and exactly one first Card");
        Assert(!InventoryTopologyPolicy.UnlockPostcondition(ProgressionUnlockKind.Cards,
                true, false, true, true, 2, 2),
            "Cards flag without first-Card delivery is rejected");
        Assert(!InventoryTopologyPolicy.UnlockPostcondition(ProgressionUnlockKind.Cards,
                true, true, true, true, 2, 3),
            "an already-enabled feature is not reported as a new unlock");
    }

    private static void TestLiveIntegrationStructure()
    {
        var inventory = File.ReadAllText("source/Managers/InventoryManager.cs");
        var daycare = File.ReadAllText("source/Managers/DaycareManager.cs");
        Assert(!inventory.Contains("_controller.mergeAll("),
            "InventoryManager has no bulk native merge call that can erase references");
        Assert(inventory.Contains("MergeOrdinarySourcesPairwise")
               && inventory.Contains("RetargetNativeLoadoutReferences"),
            "live merge path is explicitly pairwise and reference-aware");
        Assert(inventory.Contains("CreateNativeMutations().ConsumeItem"),
            "irreversible item consumers use the build-pinned adapter");
        Assert(inventory.Contains("TryConsumeProgressionUnlock(ci, 294")
               && inventory.Contains("TryConsumeProgressionUnlock(ci, 343")
               && inventory.Contains("TryConsumeProgressionUnlock(ci, 391"),
            "unlock consumers are present in exact dependency order");
        Assert(inventory.Contains("AllBoostEntriesMaxxed(maxxed)")
               && inventory.Contains("selectAutoNoneTransform"),
            "live auto-transform is guarded by the all-39 permanent gate");
        Assert(daycare.Contains("CaptureOrdinaryTopology")
               && daycare.Contains("LootCapacity.ProveOrdinary")
               && daycare.Contains("completed-daycare-retrieval"),
            "completed Daycare retrieval uses a usable-slot capacity proof");
        Assert(daycare.Contains("finally") && daycare.Contains("previousItem1")
               && daycare.Contains("previousItem2"),
            "Daycare ambient selector registers restore unconditionally");
        foreach (var id in new[] {294, 343, 391, 506})
            Assert(daycare.Contains("id == " + id),
                "Daycare excludes progression state-machine ID " + id);
    }

    public static int Main()
    {
        try
        {
            TestBoostCompletionGate();
            TestMergeArithmetic();
            TestReferenceRetargetPolicy();
            TestUnlockPostconditions();
            TestLiveIntegrationStructure();
            Console.WriteLine("Inventory topology tests passed: " + _assertions + " assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
