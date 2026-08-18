using System;
using System.Collections.Generic;
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

    public static int Main()
    {
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

        Console.WriteLine("Exact allocation assertions passed: " + _assertions);
        return 0;
    }
}
