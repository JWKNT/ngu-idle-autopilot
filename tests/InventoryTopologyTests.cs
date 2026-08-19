using System;
using System.Collections.Generic;
using System.IO;
using NGUInjector.Managers;

/*
FILE PURPOSE

Purpose: This isolated executable regression-tests the pure inventory topology policy and statically
guards the live Inventory/Daycare/progression integrations that protect irreversible physical item
state and the routed ITOPOD range.

    Mechanism: Primitive arrays exercise all-39 boost gating, native merge arithmetic, transform-chain
    successors, collection retention, per-loadout retarget legality, and exact progression-unlock
    postconditions. Read-only source checks ensure the live managers use pairwise merges, build-pinned
    consumption, usable-slot Daycare retrieval, selector restoration, every audited state-machine ID,
    and exact ITOPOD range/Lazy ownership settlement.

Inputs and outputs: Inputs are in-memory Boolean/integer fixtures and maintained integration sources.
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

    private static void TestTransformAndCollectionRetention()
    {
        int next;
        Assert(InventoryTopologyPolicy.TryNextTransformItemId(53, false, out next) && next == 76,
            "MAXXED Forest Pendant advances to Ascended Forest Pendant");
        Assert(InventoryTopologyPolicy.TryNextTransformItemId(504, false, out next) && next == 480,
            "final Pendant-chain input advances to its END successor");
        Assert(InventoryTopologyPolicy.TryNextTransformItemId(67, false, out next) && next == 128,
            "MAXXED base Looty advances to the next exact Looty ID");
        Assert(InventoryTopologyPolicy.TryNextTransformItemId(505, false, out next) && next == 485,
            "final Looty-chain input advances to its END successor");
        Assert(InventoryTopologyPolicy.TryNextTransformItemId(120, false, out next) && next == 121,
            "MAXXED Lonely Flubber is always consumed into Triple Flubber");
        Assert(InventoryTopologyPolicy.TryNextTransformItemId(154, false, out next) && next == 159,
            "MAXXED Wanderer's Cane is always consumed into Candy Cane of Destiny");
        Assert(!InventoryTopologyPolicy.TryNextTransformItemId(195, false, out next) && next == 0,
            "Small Gerbil transform is held outside Sadistic");
        Assert(InventoryTopologyPolicy.TryNextTransformItemId(195, true, out next) && next == 506,
            "Sadistic Small Gerbil produces the exact MOVE69 unlock item");
        Assert(!InventoryTopologyPolicy.TryNextTransformItemId(480, true, out next) && next == 0,
            "terminal END Pendant is never offered as transform input");
        Assert(!InventoryTopologyPolicy.TryNextTransformItemId(121, true, out next) && next == 0
               && !InventoryTopologyPolicy.TryNextTransformItemId(159, true, out next) && next == 0
               && !InventoryTopologyPolicy.TryNextTransformItemId(506, true, out next) && next == 0,
            "special transform successors are never consumed as inputs");
        Assert(!AdventureCollectionPlanner.CollectionCopyRequiresRetention(true, 1, true),
            "a MAXXED copy with a known completed source set is not collection-protected forever");
        Assert(AdventureCollectionPlanner.CollectionCopyRequiresRetention(false, 1, true),
            "an unMAXXED exact ID remains protected after its source set completes");
        Assert(AdventureCollectionPlanner.CollectionCopyRequiresRetention(true, 0, true),
            "unknown source identity fails closed");
        Assert(AdventureCollectionPlanner.CollectionCopyRequiresRetention(true, 1, false),
            "one incomplete source set protects every item emitted by that source");
    }

    private static void TestBoostAndStateMachineIdentity()
    {
        foreach (var id in new[] {1, 13, 14, 26, 27, 39})
            Assert(InventoryTopologyPolicy.IsAuditedBoost(id,
                    id <= 13 ? 6 : id <= 26 ? 7 : 8),
                "every audited family boundary requires its native boost type: " + id);
        Assert(!InventoryTopologyPolicy.IsAuditedBoost(0, 6)
               && !InventoryTopologyPolicy.IsAuditedBoost(40, 8)
               && !InventoryTopologyPolicy.IsAuditedBoost(13, 7),
            "numeric aliases and native-type mismatches are not consumable boosts");
        Assert(InventoryTopologyPolicy.RequiresStateMachineCopy(75, false),
            "A Stick is retained until Tree clue two is complete");
        Assert(!InventoryTopologyPolicy.RequiresStateMachineCopy(75, true)
               && !InventoryTopologyPolicy.RequiresStateMachineCopy(74, false),
            "Stick protection relaxes only after the exact clue and never applies to other IDs");
        Assert(InventoryTopologyPolicy.StateMachineCopySatisfiedForFilter(75, false, 1, 1)
               && !InventoryTopologyPolicy.StateMachineCopyIsSurplus(75, false, 1, 1)
               && InventoryTopologyPolicy.StateMachineCopyIsSurplus(75, false, 2, 1),
            "one retained Stick satisfies the clue while only additional physical copies are disposable");
        Assert(InventoryTopologyPolicy.Wandoos98RequiredLevel(false, 0, false, 0.0) == 0
               && InventoryTopologyPolicy.Wandoos98RequiredLevel(true, 4, true, 0.0) == 5
               && InventoryTopologyPolicy.Wandoos98RequiredLevel(true, 4, false, 86400.0) == 5,
            "Wandoos 98 unlock/install readiness uses native OR timing and exact next level");
        Assert(InventoryTopologyPolicy.Wandoos98RequiredLevel(true, 4, false, 86399.0) < 0
               && InventoryTopologyPolicy.Wandoos98RequiredLevel(true, 100, true, 999999.0) < 0,
            "unready and capped Wandoos 98 disks are held from destructive consumption");
        Assert(InventoryTopologyPolicy.WandoosXlRequiredLevel(true, 0) == 0
               && InventoryTopologyPolicy.WandoosXlRequiredLevel(true, 4) == 5
               && InventoryTopologyPolicy.WandoosXlRequiredLevel(false, 0) < 0
               && InventoryTopologyPolicy.WandoosXlRequiredLevel(true, 100) < 0,
            "Wandoos XL uses exact unlock, next-level, prerequisite, and cap gates");
        Assert(InventoryTopologyPolicy.GiantSeedGain(false, 100) == 1L
               && InventoryTopologyPolicy.GiantSeedGain(true, 0) == 1L
               && InventoryTopologyPolicy.GiantSeedGain(true, 100) == 200L,
            "first Giant Seed avoids merge waste and later MAXXED seeds yield native 200 cap");
    }

    private static void TestStrategicCollectionAndBoostValue()
    {
        Assert(Math.Abs(InventoryManager.BoostDevelopmentScore(2.0, 10.0, 5.0) - 2.0)
               < 1e-12,
            "boost routing prices the larger completed/MAXX loadout gain per compatible point");
        Assert(InventoryManager.BoostDevelopmentScore(10.0, 2.0, 5.0) == 2.0,
            "an already-strong immediate upgrade can outrank its MAXX projection");
        Assert(InventoryManager.BoostDevelopmentScore(10.0, 20.0, 0.0) == 0.0,
            "an item that accepts no compatible boost has no development score");

        Assert(!AdventureCollectionPlanner.StrategicDebtOwnsAdventure(false, 0.0, 0.0, 0.0),
            "optional MAXX debt with no proven payoff cannot take Adventure from ITOPOD");
        Assert(AdventureCollectionPlanner.StrategicDebtOwnsAdventure(true, 0.0, 0.0, 0.0),
            "an unfinished core set remains strategic debt");
        Assert(AdventureCollectionPlanner.StrategicDebtOwnsAdventure(false, 1.0, 0.0, 0.0)
               && AdventureCollectionPlanner.StrategicDebtOwnsAdventure(false, 0.0, 1.0, 0.0)
               && AdventureCollectionPlanner.StrategicDebtOwnsAdventure(false, 0.0, 0.0, 1.0),
            "set rewards, useful boost supply, and completed loadout gain each justify Adventure");
        Assert(!ProgressionLoadoutOptimizer.ShouldValueProductionInDevelopment(true, false, false)
               && !ProgressionLoadoutOptimizer.ShouldValueProductionInDevelopment(false, true, false)
               && !ProgressionLoadoutOptimizer.ShouldValueProductionInDevelopment(false, false, true),
            "hard Boss, ITOPOD, and major-unlock development ignore unrelated production specials");
        Assert(ProgressionLoadoutOptimizer.ShouldValueProductionInDevelopment(false, false, false),
            "routine farming development may value resource-production specials");
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
        Assert(inventory.Contains("AdvanceOneMaxxedTransform(converted)")
               && inventory.Contains("successorCountAfter == successorCountBefore + 1"),
            "live maintenance advances one exact MAXXED transform with a successor postcondition");
        Assert(inventory.Contains("TryConsumeProgressionUnlock(ci, 294")
               && inventory.Contains("TryConsumeProgressionUnlock(ci, 343")
               && inventory.Contains("TryConsumeProgressionUnlock(ci, 391"),
            "unlock consumers are present in exact dependency order");
        var transactions = File.ReadAllText("source/Autopilot/ProgressionTransactions.cs");
        Assert(transactions.Contains("new ProgressionConsumableIntent(character, inventory)")
               && transactions.Contains("IdentityPresent")
               && transactions.Contains("Wandoos98"),
            "Wandoos/Seed consumers are reachable as their own exact one-item root child");
        Assert(transactions.Contains("after.ItopodStart != route.Start")
               && transactions.Contains("after.ItopodEnd != route.End")
               && transactions.Contains("after.LazyItopodOn"),
            "typed Adventure settlement proves the native ITOPOD range and Lazy ownership state");
        Assert(transactions.Contains("_character.bossController.isFighting")
               && transactions.Contains("Fight Boss owns this root"),
            "Adventure holds rather than quarantining when the prior child started Fight Boss");
        var autopilot = File.ReadAllText("source/Autopilot/AutopilotManager.cs");
        var safeZoneHop = autopilot.IndexOf("combat.MoveToZone(-1);", StringComparison.Ordinal);
        var itopodStage = autopilot.IndexOf("ProgressionLoadoutOptimizer.PrepareItopodRoute()",
            StringComparison.Ordinal);
        Assert(safeZoneHop >= 0 && itopodStage > safeZoneHop
               && autopilot.Contains("Main.Character.adventure.zone != -1"),
            "ITOPOD entry deliberately takes a Safe-Zone frame before loadout staging");
        Assert(autopilot.Contains("var manualItopod = Main.Settings.ITOPODCombatMode != 1")
               && autopilot.Contains("var fightType = move69Pending || manualItopod ? 2 : 0"),
            "configured Manual ITOPOD combat is retained for farms as well as record climbs");
        var loadout = File.ReadAllText("source/Managers/ProgressionLoadoutOptimizer.cs");
        Assert(loadout.Contains("StrongestAdventureAttackPlan(c, all)")
               && loadout.Contains("strongestEvaluation.Feasible")
               && loadout.Contains("bestEvaluation = strongestEvaluation;")
               && loadout.Contains("objective.Projection.ItopodTargetAttackFactor")
               && loadout.Contains("itopodManual = Main.Settings.ITOPODCombatMode != 1")
               && loadout.Contains("failed its live ")
               && loadout.Contains("? \"bounded frontier\" : \"one-hit farm\""),
            "ITOPOD staging evaluates physical combat in the requested Beast state and requires the route-specific live proof");
        Assert(inventory.Contains("AllBoostEntriesMaxxed(maxxed)")
               && inventory.Contains("selectAutoNoneTransform"),
            "live auto-transform is guarded by the all-39 permanent gate");
        Assert(!inventory.Contains("var invItems = ci.Where(x => x.locked"),
            "arbitrary locked keepsakes are not appended to the boost-consumption route");
        Assert(inventory.Contains("StateMachineCopyIsSurplus(id")
               && inventory.Contains("StateMachineCopySatisfiedForFilter(id")
               && inventory.Contains("CurrentOwnedCopyCount(id)")
               && inventory.Contains("_character.adventure.clue2Complete"),
            "live filter/trash paths retain the exact Tree clue demand but reclaim surplus copies");
        Assert(inventory.Contains("item.spec1Cap > 0f")
               && inventory.Contains("item.spec2Cap > 0f")
               && inventory.Contains("item.spec3Cap > 0f"),
            "MAXX gear with any native special profile remains physically retained");
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
            TestTransformAndCollectionRetention();
            TestBoostAndStateMachineIdentity();
            TestStrategicCollectionAndBoostValue();
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
