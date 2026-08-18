using System;
using System.Reflection;

/*
FILE PURPOSE

This isolated executable regression-tests the compiled rebirth policy kernel without starting Unity,
opening a save, or invoking a controller mutation. Reflection reaches internal production methods so
the golden cases exercise the exact shipped all-negative baseline and final recovery admission logic.
The test reads NGUIdleAutopilot.dll from the repository root and writes no runtime/configuration data.
*/
internal static class RebirthPolicyGoldenTests
{
    private static int _assertions;
    private static Assembly _assembly;

    private static void Assert(bool value, string message)
    {
        _assertions++;
        if (!value) throw new Exception("FAIL: " + message);
    }

    private static object Call(string typeName, string methodName, params object[] args)
    {
        var type = _assembly.GetType(typeName, true);
        var method = type.GetMethod(methodName,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert(method != null, typeName + "." + methodName + " exists");
        return method.Invoke(null, args);
    }

    private static object Field(object target, string name)
    {
        var field = target.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert(field != null, target.GetType().Name + "." + name + " exists");
        return field.GetValue(target);
    }

    private static void TestExplicitHoldBaseline()
    {
        Assert(!(bool)Call("NGUInjector.Autopilot.RebirthOptimizer", "ResetBeatsHold", -0.57),
            "observed -0.57/h all-negative winner cannot beat no-reset");
        Assert(!(bool)Call("NGUInjector.Autopilot.RebirthOptimizer", "ResetBeatsHold", 0.0),
            "zero is a tie and continuation wins the tie");
        Assert((bool)Call("NGUInjector.Autopilot.RebirthOptimizer", "ResetBeatsHold", 0.000001),
            "strictly positive finite reset utility beats hold");
        Assert(!(bool)Call("NGUInjector.Autopilot.StrategyCheckpointPlanner", "ResetBeatsHold", -0.01),
            "later-stage event queue uses the same hold baseline");
    }

    private static void TestObservedAllNegativeMutationCase()
    {
        var decision = Call("NGUInjector.Autopilot.RebirthOptimizer", "EvaluateMutationPolicy",
            -0.57, true, 0.0044, false, -1, -1);
        Assert(!(bool)Field(decision, "Authorized"),
            "all-negative ordinary reset is denied at mutation boundary");
        Assert(((string)Field(decision, "Reason")).Contains("no-reset baseline"),
            "denial explains the controlling counterfactual");
    }

    private static void TestLowerNumberPositivePersistentCase()
    {
        // Number ratio is intentionally not an admission argument. Outside record recovery, a
        // lower Number may be repaid by modeled persistent AP/EXP/cap value; the positive aggregate
        // score is the proof. This prevents accidentally restoring a synthetic non-regression gate.
        var decision = Call("NGUInjector.Autopilot.RebirthOptimizer", "EvaluateMutationPolicy",
            0.125, true, 0.5, false, -1, -1);
        Assert((bool)Field(decision, "Authorized"),
            "positive persistent-value reset remains legal even when Number itself is lower");
        Assert((int)Field(decision, "PreferredRouteEtaSeconds") == 0,
            "non-recovery positive reset is actionable now");
        Assert(((string)Field(decision, "Reason")).Contains("lower Number"),
            "authorization makes the priced Number loss explicit");
    }

    private static void TestRecoveryCounterfactuals()
    {
        var unknown = Call("NGUInjector.Autopilot.RebirthOptimizer", "EvaluateMutationPolicy",
            0.5, true, 1.1, true, -1, 900);
        Assert(!(bool)Field(unknown, "Authorized"), "unknown reset recovery ETA fails closed");

        var continueWins = Call("NGUInjector.Autopilot.RebirthOptimizer", "EvaluateMutationPolicy",
            0.5, true, 1.1, true, 1800, 900);
        Assert(!(bool)Field(continueWins, "Authorized"), "faster continuation blocks reset");
        Assert((int)Field(continueWins, "PreferredRouteEtaSeconds") == 900,
            "hold publishes actionable continuation ETA");

        var resetWins = Call("NGUInjector.Autopilot.RebirthOptimizer", "EvaluateMutationPolicy",
            0.5, true, 1.1, true, 600, 1200);
        Assert((bool)Field(resetWins, "Authorized"), "faster finite reset route is authorized");
        Assert((int)Field(resetWins, "PreferredRouteEtaSeconds") == 600,
            "authorization publishes reset recovery ETA");

        var staleBlood = Call("NGUInjector.Autopilot.RebirthOptimizer", "EvaluateMutationPolicy",
            0.5, false, 1.1, false, -1, -1);
        Assert(!(bool)Field(staleBlood, "Authorized"),
            "unreflected Blood-adjusted preview blocks mutation");
    }

    private static void TestDueProfileSignatureIsStable()
    {
        Assert((string)Call("NGUInjector.Autopilot.AutopilotPlan", "RebirthSignatureFor",
                3600, false, 3599.9) == "3600",
            "future checkpoint retains its exact generated TIME target");
        Assert((string)Call("NGUInjector.Autopilot.AutopilotPlan", "RebirthSignatureFor",
                3600, false, 3600.0) == "DUE",
            "due checkpoint canonicalizes to a stable signature");
        Assert((string)Call("NGUInjector.Autopilot.AutopilotPlan", "RebirthSignatureFor",
                3601, false, 9000.0) == "DUE",
            "moving current-second optimizer targets stay canonical after due");
        Assert((string)Call("NGUInjector.Autopilot.AutopilotPlan", "RebirthSignatureFor",
                3660, true, 9000.0) == "UNSCHEDULED-HOLD",
            "execution hold remains distinct from a due checkpoint");
    }

    private static void TestGeneratedProfileWatcherNormalization()
    {
        Assert((bool)Call("NGUInjector.Autopilot.AutopilotPlan", "IsGeneratedAllocationPath",
                "autopilot.generated.json"), "generated leaf name is recognized");
        Assert((bool)Call("NGUInjector.Autopilot.AutopilotPlan", "IsGeneratedAllocationPath",
                @"runtime\profiles\autopilot.generated.json"),
            "generated relative Wine path is recognized");
        Assert((bool)Call("NGUInjector.Autopilot.AutopilotPlan", "IsGeneratedAllocationPath",
                "/tmp/runtime/profiles/AUTOPILOT.GENERATED.JSON"),
            "generated absolute path comparison is case-insensitive");
        Assert(!(bool)Call("NGUInjector.Autopilot.AutopilotPlan", "IsGeneratedAllocationPath",
                "default.json"), "legacy profile remains watcher-managed");
    }

    public static int Main()
    {
        try
        {
            _assembly = Assembly.LoadFrom("NGUIdleAutopilot.dll");
            TestExplicitHoldBaseline();
            TestDueProfileSignatureIsStable();
            TestGeneratedProfileWatcherNormalization();
            TestObservedAllNegativeMutationCase();
            TestLowerNumberPositivePersistentCase();
            TestRecoveryCounterfactuals();
            Console.WriteLine("Rebirth policy golden tests passed: " + _assertions + " assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
