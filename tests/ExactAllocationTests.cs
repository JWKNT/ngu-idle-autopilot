using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

ExactAllocationTests is a standalone pure regression executable for long resource arithmetic,
aggregate caps, joint Wish headroom, 20 ms completion floors, conservation, and same-frame OS
ordering. It never loads Unity, a save, or the injected assembly and performs no native mutation.
*/
internal static class ExactAllocationTests
{
    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception(message);
    }

    private static ExactAllocationVector Vector(ExactResourceKind resource, long capacity,
        long idle, params long[] targets)
    {
        var values = new Dictionary<string, long>();
        for (var i = 0; i < targets.Length; i++) values["target." + i] = targets[i];
        return new ExactAllocationVector(resource, capacity, idle, values);
    }

    private static void TestSealedFullTargetSettlement()
    {
        var before = Vector(ExactResourceKind.Energy, 100, 20, 30, 50);
        var requested = Vector(ExactResourceKind.Energy, 100, 10, 40, 50);
        var settlement = new ExactAllocationSettlement(before, requested);
        string reason;
        Assert(settlement.IsAdmissible(out reason),
            "complete conserved before/requested-after vectors are admissible");
        Assert(settlement.VerifyAcceptedNativeState(
                Vector(ExactResourceKind.Energy, 100, 10, 40, 50), out reason),
            "accepted native vector must equal every sealed target");
        Assert(!settlement.VerifyAcceptedNativeState(
                Vector(ExactResourceKind.Energy, 100, 10, 39, 51), out reason)
               && reason.Contains("differs"),
            "same capacity and idle cannot hide a misrouted target allocation");

        var omitted = new ExactAllocationSettlement(
            Vector(ExactResourceKind.Magic, 100, 20, 30, 49),
            Vector(ExactResourceKind.Magic, 100, 20, 30, 50));
        Assert(!omitted.IsAdmissible(out reason) && reason.Contains("before-state"),
            "a missing before-state allocation fails conservation");
        var overdrawn = new ExactAllocationSettlement(
            Vector(ExactResourceKind.Resource3, 100, 50, 25, 25),
            Vector(ExactResourceKind.Resource3, 100, 0, 60, 50));
        Assert(!overdrawn.IsAdmissible(out reason) && reason.Contains("requested-after"),
            "an overdrawn requested-after vector fails conservation");

        var changedSchema = new ExactAllocationVector(ExactResourceKind.Energy, 100, 10,
            new Dictionary<string, long> {{"target.0", 40}, {"replacement", 50}});
        Assert(!new ExactAllocationSettlement(before, changedSchema).IsAdmissible(out reason)
               && reason.Contains("schema"),
            "target schema cannot change within one allocation transaction");
        Assert(!new ExactAllocationSettlement(before,
                Vector(ExactResourceKind.Energy, 101, 11, 40, 50)).IsAdmissible(out reason)
               && reason.Contains("capacity"),
            "capacity mutation cannot settle as resource allocation");
        Assert(requested.Keys().SequenceEqual(new[] {"target.0", "target.1"}),
            "target keys are stable and sorted for deterministic receipts");
    }

    private static void TestNoCurrencyIdleFallback()
    {
        Assert(ExactResourceAllocator.SelectNoCurrencyFallback(true, true,
                   true, true, true, false) == NoCurrencyFallbackKind.Ngu,
            "an unlocked persistent NGU must outrank the reset-local Wandoos fallback");
        Assert(ExactResourceAllocator.SelectNoCurrencyFallback(false, false,
                   true, true, true, false) == NoCurrencyFallbackKind.Wandoos,
            "active installed Wandoos must absorb a remainder when NGU is unavailable");
        Assert(ExactResourceAllocator.SelectNoCurrencyFallback(false, false,
                   true, false, true, false) == NoCurrencyFallbackKind.None,
            "an uninstalled Wandoos bar is not a live fallback sink");
        Assert(ExactResourceAllocator.SelectNoCurrencyFallback(false, false,
                   true, true, false, false) == NoCurrencyFallbackKind.None,
            "a disabled Wandoos feature is not an active fallback sink");
        Assert(ExactResourceAllocator.SelectNoCurrencyFallback(false, false,
                   true, true, true, true) == NoCurrencyFallbackKind.None,
            "a challenge-disabled Wandoos bar is not a fallback sink");
    }

    private static void TestResetLocalCompletionAdmission()
    {
        double completion;
        Assert(ExactResourceAllocator.ResetLocalLevelHasUseWindow(
                   0.0, 1L, 1.0, 100.0, 62.0, out completion)
               && Math.Abs(completion - 2.0) < 1e-12,
            "a Wandoos level with a full minute of post-completion use is admissible");
        Assert(!ExactResourceAllocator.ResetLocalLevelHasUseWindow(
                   0.0, 1L, 1.0, 100.0, 3.9, out completion),
            "a reset-local level that finishes near rebirth must not consume resources");
        Assert(!ExactResourceAllocator.ResetLocalLevelHasUseWindow(
                   0.03, 78000L, 0.0136, 1e9, 9000.0, out completion)
               && completion > 9000.0,
            "the observed early-game Wandoos shape must reject a level beyond the run horizon");
        Assert(ExactResourceAllocator.ResetLocalLevelHasUseWindow(
                   0.90, 100L, 1.0, 100.0, 61.0, out completion),
            "nearly completed reset-local work may finish and retain a useful window");
    }

    private static void TestTrainingAndChallengeSourceContract()
    {
        var allocation = File.ReadAllText("source/AllocationProfiles/CustomAllocation.cs");
        var frontier = allocation.IndexOf("AllocateUnlockFrontier(", StringComparison.Ordinal);
        var advanced = allocation.IndexOf("var cappedAdvancedTraining", StringComparison.Ordinal);
        Assert(frontier >= 0 && advanced > frontier,
            "Basic Training's finite ability frontier must be funded before Advanced Training");

        var basic = File.ReadAllText(
            "source/AllocationProfiles/BreakpointTypes/BasicTrainingBP.cs");
        Assert(basic.Contains("BTIndex >= 5")
               && basic.Contains("seconds + 120.0 > remainingSeconds")
               && basic.Contains("Energy = cap"),
            "ability unlock reservation must stop after row four, speed-cap natively, and leave a use window");

        var wandoos = File.ReadAllText(
            "source/AllocationProfiles/BreakpointTypes/WandoosBP.cs");
        var admission = wandoos.IndexOf("ResetLocalLevelHasUseWindow(",
            StringComparison.Ordinal);
        var nativeAdd = wandoos.IndexOf("addEnergy()", StringComparison.Ordinal);
        Assert(admission >= 0 && nativeAdd > admission,
            "Wandoos must prove next-level completion/payback before its native allocation");

        var planner = File.ReadAllText("source/Autopilot/AutopilotPlanner.cs");
        var basicChallenge = planner.IndexOf("active.Type == ChallengeType.Basic",
            StringComparison.Ordinal);
        var clearAllocations = planner.IndexOf("plan.Energy.Clear()", basicChallenge,
            StringComparison.Ordinal);
        Assert(basicChallenge >= 0 && clearAllocations > basicChallenge,
            "Basic Challenge must preserve the ordinary allocation plan because it disables nothing");
    }

    private static void TestResource3FallbackSourceContract()
    {
        var source = File.ReadAllText("source/AllocationProfiles/CustomAllocation.cs");
        var r3Method = source.IndexOf("public override void AllocateR3()",
            StringComparison.Ordinal);
        var valuedSweep = source.IndexOf("foreach (var prio in temp)",
            r3Method, StringComparison.Ordinal);
        var fallback = source.IndexOf("AllocateR3NoCurrencyFallback(",
            valuedSweep, StringComparison.Ordinal);
        Assert(r3Method >= 0 && valuedSweep > r3Method && fallback > valuedSweep,
            "Resource 3 fallback must run only after valued Hack/Wish priorities");
        Assert(source.IndexOf("_character.hacksController.addR3(hackId, before)",
                   fallback, StringComparison.Ordinal) > fallback
               && source.IndexOf("_character.wishesController.addRes3(wishId)",
                   fallback, StringComparison.Ordinal) > fallback,
            "Resource 3 fallback must use only the native Hack/Wish add controllers");
        Assert(source.IndexOf("TryObservedAcceptance(before,",
                   fallback, StringComparison.Ordinal) > fallback
               && source.IndexOf("_character.res3.idleRes3, before, out accepted)",
                   fallback, StringComparison.Ordinal) > fallback,
            "Resource 3 fallback must report only observed accepted idle-pool deltas");
        Assert(source.IndexOf("idle-topology-blocker=", valuedSweep,
                   StringComparison.Ordinal) > fallback
               && source.IndexOf("DescribeR3NoCurrencyFallback", fallback,
                   StringComparison.Ordinal) > fallback,
            "Resource 3 fallback must expose an exact live-topology blocker when idle remains");
        Assert(source.IndexOf("_character.hacks.hacks[hackId].res3 =",
                   fallback, StringComparison.Ordinal) < 0
               && source.IndexOf("_character.wishes.wishes[wishId].res3 =",
                   fallback, StringComparison.Ordinal) < 0,
            "Resource 3 fallback must never rewrite native allocation fields directly");
    }

    public static int Main()
    {
        TestResource3FallbackSourceContract();
        var values = new[] {1L << 24, 1L << 53, 1000000000000000000L,
            9000000000000000000L, 8999999999999999999L, long.MaxValue - 1L};
        foreach (var value in values)
        {
            long parsed;
            Assert(ExactResourceAllocator.TryParseExactInput(
                ExactResourceAllocator.FormatExactInput(value), out parsed) && parsed == value,
                "exact long input round-trip failed for " + value);
        }
        var parserBoundary = 8999999999999999999L;
        Assert((long)(float)parserBoundary != parserBoundary,
            "regression fixture must detect the former float narrowing");
        Assert((long)double.Parse(ExactResourceAllocator.FormatExactInput(parserBoundary),
                   System.Globalization.CultureInfo.InvariantCulture) != parserBoundary,
            "regression fixture must detect native UI double narrowing at cap-1");

        Assert(ExactResourceAllocator.AggregatePercentBudget(1000L, 12) == 120L,
            "CAPALLBT:12 must be one aggregate 12% budget");
        Assert(ExactResourceAllocator.AggregatePercentBudget(9000000000000000000L, 12)
               == 1080000000000000000L, "large aggregate cap lost precision");
        Assert(ExactResourceAllocator.CeilingShare(11L, 1L, 3L) == 4L,
            "percentage ceiling must retain the rounding unit");
        Assert(ExactResourceAllocator.CapAtTickBoundary(100.0, 0L, 100L) == 0L,
            "zero maximum must not divide");
        Assert(ExactResourceAllocator.CapAtTickBoundary(100.0, 50L, 0L) == 0L,
            "zero idle must not create a phantom request");
        Assert(ExactResourceAllocator.NextCompletionAllocationChunk(100.0, 60L, 60L) == 51L,
            "event chunk must end at the next discrete tick boundary");
        Assert(ExactResourceAllocator.CompletionHeadroomForTicks(1000L, 0L, 0.5, 10) == 50L,
            "training chunk must fund only the next heartbeat event");
        Assert(ExactResourceAllocator.CompletionHeadroomForTicks(1000L, 50L, 0.5, 10) == 0L,
            "already-funded event must expose no extra headroom");
        Assert(ExactResourceAllocator.CompletionHeadroomForTicks(1000L, 0L, 1.0, 10) == 0L,
            "already-complete event must not receive a phantom unit");
        Assert(ExactResourceAllocator.ProductiveSpeedCapHeadroom(1000L, 50L, 10000L) == 950L,
            "idle fallback must fill all productive training headroom");
        Assert(ExactResourceAllocator.ProductiveSpeedCapHeadroom(1000L, 1000L, 10000L) == 0L,
            "idle fallback must not overfill a native speed cap");
        Assert(ExactResourceAllocator.ProductiveSpeedCapHeadroom(1000L, 50L, 25L) == 25L,
            "idle fallback must conserve the available idle budget");

        Assert(ExactResourceAllocator.NativeCompletionTicks(0.0, 1000.0) == 1L,
            "online completion is floored to one native tick");
        Assert(Math.Abs(ExactResourceAllocator.NativeCompletionSeconds(0.5, 1000.0) - 0.02) < 1e-12,
            "completion seconds must retain the 20ms floor");
        Assert(Math.Abs(ExactResourceAllocator.HackMilestoneSeconds(0L, 4L, 0.5, 1e9) - 0.08) < 1e-12,
            "each Hack level must consume at least one event tick");
        Assert(ExactResourceAllocator.IsSupportedHackId(15, 16),
            "terminal Hack 15 must remain selectable");
        Assert(!ExactResourceAllocator.IsSupportedHackId(16, 17),
            "unknown Hack IDs must fail closed");

        var wishes = new[]
        {
            new WishAllocationTarget {WishId = 7, DesiredEnergy = 100, DesiredMagic = 80,
                DesiredRes3 = 60},
            new WishAllocationTarget {WishId = 7, DesiredEnergy = 100, DesiredMagic = 80,
                DesiredRes3 = 60}
        };
        var deltas = ExactResourceAllocator.PlanWishHeadroom(wishes, 1000, 1000, 1000);
        Assert(deltas.Count == 1, "duplicate logical Wish slots must merge");
        Assert(deltas[0].Energy == 100 && deltas[0].Magic == 80 && deltas[0].Res3 == 60,
            "duplicate Wish slots must not add their headroom repeatedly");
        var incomplete = ExactResourceAllocator.PlanWishHeadroom(new[]
        {
            new WishAllocationTarget {WishId = 8, DesiredEnergy = 10, DesiredMagic = 10,
                DesiredRes3 = 10}
        }, 10, 10, 0);
        Assert(incomplete.Count == 0, "a zero-factor Wish must not strand two resources");

        Assert(ExactResourceAllocator.Conserves(1000, 100,
            new long[] {120, 300, 480}), "valid portfolio failed conservation");
        Assert(!ExactResourceAllocator.Conserves(1000, 100,
            new long[] {120, 300, 481}), "overallocated portfolio passed conservation");
        long accepted;
        Assert(ExactResourceAllocator.TryObservedAcceptance(1000, 880, 120, out accepted)
               && accepted == 120, "exact native delta acceptance was not confirmed");
        Assert(!ExactResourceAllocator.TryObservedAcceptance(1000, 879, 120, out accepted),
            "native over-acceptance must fail verification");
        Assert(!ExactResourceAllocator.CanSnapshot(true, false),
            "OS-due frame must not snapshot old mode");
        Assert(ExactResourceAllocator.CanSnapshot(true, true),
            "confirmed OS switch must permit same-frame snapshot");
        var order = ExactResourceAllocator.FrameOrder();
        Assert(order[0] == ExactAllocationPhase.ModeChanges
               && order[1] == ExactAllocationPhase.Snapshot,
            "mode changes must precede allocation snapshot");
        for (var i = 1; i < order.Length; i++)
            Assert(ExactResourceAllocator.IsValidPhaseTransition(order[i - 1], order[i]),
                "allocation phase order skipped a safety boundary");

        TestSealedFullTargetSettlement();
        TestNoCurrencyIdleFallback();
        TestResetLocalCompletionAdmission();
        TestTrainingAndChallengeSourceContract();

        Console.WriteLine("Exact allocation assertions passed: " + _assertions);
        return 0;
    }
}
