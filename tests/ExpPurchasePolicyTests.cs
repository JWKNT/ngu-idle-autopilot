/*
FILE PURPOSE

ExpPurchasePolicyTests is a controller-free regression executable for pre-50 Energy Speed ranking,
Magic's discrete refill breakpoint, and the narrow forward-Fight-Boss exception.  It loads no game,
save, or Unity assembly and performs no mutation.
*/
using System;
using NGUInjector.Autopilot;

internal static class ExpPurchasePolicyTests
{
    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception(message);
    }

    public static int Main()
    {
        var choice = ExpPurchasePolicy.ChoosePre50EnergySpeed(10.0, false, false, false,
            1, 2, 3, 2, 20, 100);
        Assert(choice != null && choice.DescriptorKey == "exp.energy.speed-special1"
               && choice.ExactCost == 1 && choice.DeltaHundredths == 20,
            "highest productive Energy special ROI must execute first");
        choice = ExpPurchasePolicy.ChoosePre50EnergySpeed(10.0, true, true, true,
            1, 2, 3, 2, 20, 20);
        Assert(choice.DescriptorKey == "exp.energy.speed100" && choice.FullyFunded,
            "funded non-overshooting +1.0 atom may reduce decision-cycle latency");
        choice = ExpPurchasePolicy.ChoosePre50EnergySpeed(10.0, true, true, true,
            1, 2, 3, 2, 20, 2);
        Assert(choice.DescriptorKey == "exp.energy.speed10" && choice.FullyFunded,
            "unfunded large atom must not delay an affordable equal-ROI +0.1 atom");
        choice = ExpPurchasePolicy.ChoosePre50EnergySpeed(49.9, false, false, false,
            1, 2, 3, 2, 20, 100);
        Assert(choice.DescriptorKey == "exp.energy.speed-special1"
               && Math.Abs(choice.ProductiveGain - .1) < 1e-9,
            "Energy special value must be clipped to productive cap headroom");

        int atoms;
        double rate;
        Assert(ExpPurchasePolicy.TryMagicDiscreteBreakpoint(1.0, 1.0, 10, 10.0, 10,
                out atoms, out rate) && atoms > 0 && rate > 10.0,
            "Magic Speed solver must find the first native ceil-division rate breakpoint");
        Assert(!ExpPurchasePolicy.TryMagicDiscreteBreakpoint(50.0, 50.0, 10, 500.0, 10,
                out atoms, out rate),
            "Magic Speed at its productive cap must expose no purchase breakpoint");

        double gate;
        double permanent;
        Assert(!ExpPurchasePolicy.FightBossGateOutranksPermanent(false, 600, 60, 30,
                1, .1, 15, out gate, out permanent),
            "an already-cleared/repeat Fight Boss can never preempt permanent growth");
        Assert(!ExpPurchasePolicy.FightBossGateOutranksPermanent(true,
                double.PositiveInfinity, 60, 30, 1, .1, 15, out gate, out permanent),
            "an unproven natural rollout ETA cannot justify direct Fight Boss stats");
        Assert(ExpPurchasePolicy.FightBossGateOutranksPermanent(true, 600, 60, 30,
                1000, .1, 15, out gate, out permanent) && gate > permanent,
            "a finite forward gate with greater per-EXP time value may preempt growth once");

        Console.WriteLine("EXP purchase policy assertions passed: " + _assertions);
        return 0;
    }
}
