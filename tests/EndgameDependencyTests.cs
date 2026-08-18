using System;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

This controller-free fixture exercises the pure END physical-state, checker, cumulative T12, and
T14 retry contracts. It constructs only copied item IDs and object identity tokens. It never creates
a Character, loads a save, invokes a game/Unity controller, or writes runtime/build/game state.
*/
internal static class EndgameDependencyTests
{
    private static int _assertions;

    private static void Assert(bool value, string message)
    {
        _assertions++;
        if (!value) throw new Exception("FAIL: " + message);
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        _assertions++;
        if (Math.Abs(expected - actual) > tolerance)
            throw new Exception("FAIL: " + message + " expected=" + expected + " actual=" + actual);
    }

    private static OrdinaryInventoryTopology Topology(int slotCount, params int[] occupiedPairs)
    {
        var ids = new int[slotCount];
        var identities = new object[slotCount];
        for (var i = 0; i < occupiedPairs.Length; i += 2)
        {
            var slot = occupiedPairs[i];
            ids[slot] = occupiedPairs[i + 1];
            identities[slot] = new object();
        }
        return PhysicalTopology.CaptureOrdinary(ids, identities, slotCount, 0);
    }

    private static OrdinaryInventoryTopology TopologyWithFreeSlots(
        int slotCount, int desiredFreeSlots, params int[] endItems)
    {
        var ids = new int[slotCount];
        var identities = new object[slotCount];
        var cursor = 0;
        for (var i = 0; i < endItems.Length; i++)
        {
            ids[cursor] = endItems[i];
            identities[cursor] = new object();
            cursor++;
        }
        while (slotCount - cursor > desiredFreeSlots)
        {
            ids[cursor] = 100 + cursor;
            identities[cursor] = new object();
            cursor++;
        }
        return PhysicalTopology.CaptureOrdinary(ids, identities, slotCount, 0);
    }

    private static void TestOrdinaryVersusRecoverableTruth()
    {
        var noneOrdinary = Topology(40);
        var daycareIdentity = new object();
        var daycare = new[]
        {
            new EndRecoverableCopy(490, EndRecoverableLocation.Daycare, 2, daycareIdentity)
        };
        Assert(!EndgameDependencyModel.HasTerminalPiece(noneOrdinary, 490),
            "a Daycare-only END copy is not a terminal ordinary piece");
        Assert(EndgameDependencyModel.HasRecoverableCopy(noneOrdinary, 490, daycare),
            "the same Daycare object is explicitly represented as recoverable");

        var recovery = EndgameDependencyModel.PlanCanonicalization(noneOrdinary, 490, daycare);
        Assert(recovery.NeedsRecoveryToOrdinary && recovery.RecoverySource != null,
            "Daycare-only physical state creates a recovery debt, not false completion");
        Assert(object.ReferenceEquals(recovery.RecoverySource.Identity, daycareIdentity),
            "the recovery plan preserves exact source identity");
        Assert(!recovery.HasTerminalPiece && recovery.HasRecoverableCopy,
            "split truth is retained on the immutable normalization plan");

        var exactlyOne = Topology(40, 7, 490);
        Assert(EndgameDependencyModel.HasTerminalPiece(exactlyOne, 490),
            "exactly one ordinary copy is terminal ownership even before final placement");
        var duplicate = Topology(40, 7, 490, 8, 490);
        Assert(!EndgameDependencyModel.HasTerminalPiece(duplicate, 490),
            "two ordinary copies do not satisfy the exactly-one canonical invariant");
    }

    private static void TestCanonicalDuplicatePlan()
    {
        var topology = Topology(40, 3, 483, 10, 483);
        var daycare = new EndRecoverableCopy(
            483, EndRecoverableLocation.Daycare, 0, new object());
        var accessory = new EndRecoverableCopy(
            483, EndRecoverableLocation.Accessory, 1, new object());
        var plan = EndgameDependencyModel.PlanCanonicalization(topology, 483,
            new[] {accessory, daycare});

        Assert(plan.CanonicalOrdinarySlot == 3
               && object.ReferenceEquals(plan.CanonicalOrdinaryIdentity,
                   topology.SlotAt(3).Identity),
            "the exact final slot wins over lower-value duplicate placement choices");
        var duplicateSlots = plan.OrdinaryDuplicateSlots();
        Assert(duplicateSlots.Length == 1 && duplicateSlots[0] == 10,
            "only the proven noncanonical ordinary identity is a duplicate target");
        Assert(plan.NonOrdinaryDuplicatesAfterRecovery().Length == 2,
            "when an ordinary canonical exists every non-ordinary copy is cleanup debt");
        Assert(plan.NeedsDuplicateCleanup && !plan.IsCanonical,
            "duplicate pressure remains explicit until a transaction verifies cleanup");

        duplicateSlots[0] = 99;
        Assert(plan.OrdinaryDuplicateSlots()[0] == 10,
            "callers cannot mutate stored duplicate targets through returned arrays");

        var noOrdinary = Topology(40);
        var recovery = EndgameDependencyModel.PlanCanonicalization(noOrdinary, 483,
            new[] {accessory, daycare});
        Assert(recovery.RecoverySource.Location == EndRecoverableLocation.Daycare,
            "normal Daycare retrieval is preferred to defensive equipped recovery");
        Assert(recovery.NonOrdinaryDuplicatesAfterRecovery().Length == 1,
            "the selected recovery source is never simultaneously targeted as a duplicate");

        var normalized = EndgameDependencyModel.PlanCanonicalization(
            Topology(40, 3, 483), 483, new EndRecoverableCopy[0]);
        Assert(normalized.HasTerminalPiece && normalized.IsCanonical
               && !normalized.NeedsDuplicateCleanup,
            "one ordinary copy and zero non-ordinary copies is the stable canonical state");
    }

    private static void TestCheckerDelayedGrantState()
    {
        var incomplete = EndgameDependencyModel.EvaluateCheckerGrant(482,
            false, 300, 0, 10.0);
        Assert(incomplete.State == EndGrantMaterializationState.SourceIncomplete
               && !incomplete.PendingGrant,
            "the physical checker cannot replace the persistent perk source transition");

        var gated = EndgameDependencyModel.EvaluateCheckerGrant(482,
            true, 224, 0, 10.0);
        Assert(gated.State == EndGrantMaterializationState.WaitingForBoss225
               && gated.SourceSatisfied && !gated.CheckerEligible && !gated.PendingGrant,
            "source completion at Boss 224 is retained without claiming an eligible attempt");

        var pending = EndgameDependencyModel.EvaluateCheckerGrant(482,
            true, 225, 0, 7.0);
        Assert(pending.State == EndGrantMaterializationState.PendingChecker
               && pending.PendingGrant && pending.CheckerEligible,
            "Boss 225 converts missing physical delivery into a pending native checker grant");
        Near(23.0, pending.NextCheckerEtaSeconds, 1e-12,
            "known checker phase reports its remaining bounded delay");

        var conservative = EndgameDependencyModel.EvaluateCheckerGrant(486,
            true, 300, 0, -1.0);
        Near(30.0, conservative.NextCheckerEtaSeconds, 1e-12,
            "unknown checker phase publishes the conservative 30-second bound");

        var delivered = EndgameDependencyModel.EvaluateCheckerGrant(490,
            true, 300, 1, 0.0);
        Assert(delivered.State == EndGrantMaterializationState.Delivered
               && !delivered.PendingGrant,
            "persistent source plus exactly one ordinary object closes the grant debt");
        var duplicates = EndgameDependencyModel.EvaluateCheckerGrant(487,
            true, 300, 2, 0.0);
        Assert(duplicates.State == EndGrantMaterializationState.NeedsNormalization,
            "checker duplicates route to normalization rather than source repetition");
    }

    private static void TestCumulativeTitan12Provenance()
    {
        var v1 = MechanicsEndgame.Titan12ItemsForVersion(1);
        var v2 = MechanicsEndgame.Titan12ItemsForVersion(2);
        var v3 = MechanicsEndgame.Titan12ItemsForVersion(3);
        var v4 = MechanicsEndgame.Titan12ItemsForVersion(4);
        Assert(v1.Length == 1 && v1[0] == 483,
            "T12 v1 rolls item 483");
        Assert(v2.Length == 2 && v2[0] == 483 && v2[1] == 489,
            "T12 v2 retains v1 provenance and adds 489");
        Assert(v3.Length == 3 && v3[2] == 493,
            "T12 v3 cumulatively adds 493");
        Assert(v4.Length == 4 && v4[0] == 483 && v4[1] == 489
               && v4[2] == 493 && v4[3] == 484,
            "T12 v4 rolls all four END pieces in exact native order");
        Assert(MechanicsEndgame.Titan12MinimumVersionForItem(483) == 1
               && MechanicsEndgame.Titan12MinimumVersionForItem(489) == 2
               && MechanicsEndgame.Titan12MinimumVersionForItem(493) == 3
               && MechanicsEndgame.Titan12MinimumVersionForItem(484) == 4,
            "registry fields represent minimum version, not exclusive version");

        var probabilities = new[] {.25, .25, .25, .25};
        Near(7.7417760618,
            MechanicsEndgame.ExpectedTitan12WindowsForMissing(4, new int[0], probabilities),
            1e-9,
            "four cumulative capped rolls use maximum-of-geometric expectation, not sum of means");
        Near(4.0,
            MechanicsEndgame.ExpectedTitan12WindowsForMissing(
                4, new[] {483, 489, 493}, probabilities),
            1e-12,
            "one remaining v4-only item has the ordinary geometric expectation");
    }

    private static void TestHighestSafeTitan12Selection()
    {
        var ample = TopologyWithFreeSlots(40, 18);
        var v4 = EndgameDependencyModel.PlanTitan12(ample, 4);
        Assert(v4.Actionable && v4.SelectedVersion == 4
               && v4.LatestMissingItemId == 484,
            "all missing plus v4 combat/capacity feasibility selects cumulative v4");
        Assert(v4.MissingCoveredItems().Length == 4
               && v4.CapacityProof.RequiredFreeSlots == 18,
            "v4 selection carries all missing provenance and exact latest-roll capacity");

        var sixteen = TopologyWithFreeSlots(40, 16);
        var fallbackV3 = EndgameDependencyModel.PlanTitan12(sixteen, 4);
        Assert(fallbackV3.Actionable && fallbackV3.SelectedVersion == 3
               && fallbackV3.LatestMissingItemId == 493,
            "when v4 capacity is unsafe, the highest exact-safe cumulative version is v3");

        var only484ButSeventeenSlots = TopologyWithFreeSlots(
            40, 17, 483, 489, 493);
        var held = EndgameDependencyModel.PlanTitan12(only484ButSeventeenSlots, 4);
        Assert(!held.Actionable && held.SelectedVersion == 4
               && held.LatestMissingItemId == 484
               && held.CapacityProof.RequiredFreeSlots == 18,
            "once only item 484 is missing, lower versions cannot masquerade as progress");

        var combatBound = EndgameDependencyModel.PlanTitan12(ample, 3);
        Assert(combatBound.Actionable && combatBound.SelectedVersion == 3,
            "the selector never exceeds the externally proven combat version bound");

        var complete = EndgameDependencyModel.PlanTitan12(
            TopologyWithFreeSlots(40, 20, 483, 489, 493, 484), 4);
        Assert(complete.Complete && !complete.Actionable && complete.SelectedVersion == -1,
            "ordinary ownership of all four pieces closes the T12 acquisition branch");
    }

    private static void TestT14RetryIgnoresAttemptFlag()
    {
        Assert(MechanicsEndgame.Titan14RetryActionable(902, true, false, false),
            "first T14 delivery attempt is actionable at the native gates");
        Assert(MechanicsEndgame.Titan14RetryActionable(902, true, true, false),
            "finalTitanDefeated true plus missing ordinary 495 remains actionable");
        Assert(!MechanicsEndgame.Titan14RetryActionable(902, true, true, true),
            "ordinary item 495, not the attempt flag, closes T14 delivery");
        Assert(!MechanicsEndgame.Titan14RetryActionable(901, true, true, false)
               && !MechanicsEndgame.Titan14RetryActionable(902, false, true, false),
            "retry still requires effective Boss 902 and the T13 rat flag");
    }

    public static int Main()
    {
        try
        {
            TestOrdinaryVersusRecoverableTruth();
            TestCanonicalDuplicatePlan();
            TestCheckerDelayedGrantState();
            TestCumulativeTitan12Provenance();
            TestHighestSafeTitan12Selection();
            TestT14RetryIgnoresAttemptFlag();
            Console.WriteLine("Endgame dependency tests passed: " + _assertions + " assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
