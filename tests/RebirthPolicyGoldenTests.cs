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

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert(field != null, target.GetType().Name + "." + name + " exists");
        field.SetValue(target, value);
    }

    private static object Property(object target, string name)
    {
        var property = target.GetType().GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert(property != null, target.GetType().Name + "." + name + " exists");
        return property.GetValue(target, null);
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
        Assert(!(bool)Call("NGUInjector.Autopilot.RebirthOptimizer",
                "RecoveryCandidateHasProof", true, 0.999, -1),
            "planner rejects the same unknown lower-Number recovery route as the final gate");
        Assert((bool)Call("NGUInjector.Autopilot.RebirthOptimizer",
                "RecoveryCandidateHasProof", true, 1.0, -1),
            "planner accepts non-regressive native Number without inventing a replay ETA");
        Assert((bool)Call("NGUInjector.Autopilot.RebirthOptimizer",
                "RecoveryCandidateHasProof", true, 0.9, 600),
            "planner may accept lower Number only with a finite bounded replay ETA");
        Assert((bool)Call("NGUInjector.Autopilot.RebirthOptimizer",
                "RecoveryCandidateHasProof", false, 0.1, -1),
            "outside record recovery, the aggregate positive-value policy remains unchanged");

        var nonRegressive = Call("NGUInjector.Autopilot.RebirthOptimizer",
            "EvaluateMutationPolicy",
            0.5, true, 1.0, true, -1, 900);
        Assert((bool)Field(nonRegressive, "Authorized"),
            "non-regressive recovery Number is a bounded replay proof even without an ETA");
        Assert(((string)Field(nonRegressive, "Reason")).Contains("non-regressive"),
            "non-regressive recovery authorization is explicit");

        var unknownRegression = Call("NGUInjector.Autopilot.RebirthOptimizer",
            "EvaluateMutationPolicy", 0.5, true, 0.9, true, -1, 900);
        Assert(!(bool)Field(unknownRegression, "Authorized"),
            "lower-Number recovery still fails closed without reset ETA");

        var continueWins = Call("NGUInjector.Autopilot.RebirthOptimizer", "EvaluateMutationPolicy",
            0.5, true, 0.9, true, 1800, 900);
        Assert(!(bool)Field(continueWins, "Authorized"), "faster continuation blocks reset");
        Assert((int)Field(continueWins, "PreferredRouteEtaSeconds") == 900,
            "hold publishes actionable continuation ETA");

        var resetWins = Call("NGUInjector.Autopilot.RebirthOptimizer", "EvaluateMutationPolicy",
            0.5, true, 0.9, true, 600, 1200);
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

    private static void TestDueCheckpointLeaseAndAllocationHorizon()
    {
        Assert((bool)Call("NGUInjector.Autopilot.RebirthOptimizer",
                "ShouldKeepAdmittedCheckpoint", 3600, 3600, 0.25, true),
            "a reached positive checkpoint remains admitted after replanning");
        Assert((bool)Call("NGUInjector.Autopilot.RebirthOptimizer",
                "ShouldKeepAdmittedCheckpoint", 3600, 7200, 0.25, false),
            "a delayed positive checkpoint cannot roll forward forever");
        Assert(!(bool)Call("NGUInjector.Autopilot.RebirthOptimizer",
                "ShouldKeepAdmittedCheckpoint", 3600, 7200, 0.0, false),
            "the checkpoint lease never overrides the no-reset baseline");
        Assert(!(bool)Call("NGUInjector.Autopilot.RebirthOptimizer",
                "ShouldKeepAdmittedCheckpoint", 1800, 1800, 0.25, true),
            "the first-GRB legality window invalidates an early checkpoint");

        var boundaryTarget = (double)Call("NGUInjector.Autopilot.AutopilotPlan",
            "EffectiveAllocationTargetFor", 7200, false, true, 25200.0);
        Assert(Math.Abs(boundaryTarget - 28800.0) < 1e-9,
            "a final-gate hold grants reset-local sinks a rolling one-hour horizon");
        var ordinaryTarget = (double)Call("NGUInjector.Autopilot.AutopilotPlan",
            "EffectiveAllocationTargetFor", 7200, false, false, 25200.0);
        Assert(Math.Abs(ordinaryTarget - 7200.0) < 1e-9,
            "an executable checkpoint preserves its exact target");
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

    private static void TestTask29AuthorityCeilingAndBridge()
    {
        var configType = _assembly.GetType("NGUInjector.Autopilot.AutopilotConfig", true);
        var config = Activator.CreateInstance(configType, true);
        var held = new[]
        {
            "AllowApSpending",
            "AllowQuirkSpending",
            "AllowGlobalSchedulerExecution",
            "AllowDifficultyExecution",
            "AllowTitanThirteenFourteenExecution",
            "AllowMove69Execution", "AllowEndSequence"
        };
        SetField(config, "AllowRebirths", true);
        SetField(config, "AllowExpSpending", true);
        SetField(config, "AllowPerkSpending", true);
        SetField(config, "AllowTitanOneThroughTwelveExecution", true);
        SetField(config, "ManageMoneyPit", true);
        SetField(config, "AllowMoneyPitExecution", true);
        SetField(config, "AllowChallenges", true);
        foreach (var name in held) SetField(config, name, true);
        var ceiling = configType.GetMethod("ApplyDeploymentAuthorityCeiling",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert(ceiling != null, "deployment authority ceiling exists");
        ceiling.Invoke(null, new[] {config});
        foreach (var name in held)
            Assert(!(bool)Field(config, name), name + " remains fail-closed after normalization");
        Assert((bool)Field(config, "AllowExpSpending")
               && (bool)Field(config, "AllowPermanentPurchaseExecution"),
            "audited EXP atoms preserve explicit authority and publish the staged purchase gate");
        Assert((bool)Field(config, "AllowRebirths"),
            "explicit ordinary rebirth authority survives the deployment ceiling");
        Assert((bool)Field(config, "AllowPerkSpending"),
            "typed exact one-level perk authority preserves the operator's explicit choice");
        Assert((bool)Field(config, "AllowTitanOneThroughTwelveExecution"),
            "typed T1-T12 authority preserves the operator's explicit choice");
        Assert((bool)Field(config, "ManageMoneyPit")
               && (bool)Field(config, "AllowMoneyPitExecution"),
            "typed exact Money Pit policy and irreversible authority preserve explicit choice");
        Assert((bool)Field(config, "AllowChallenges"),
            "typed challenge authority preserves the operator's explicit choice");
        Assert((bool)Property(config, "GlobalSchedulerIsShadowOnly"),
            "global scheduler is hard shadow-only independent of serialized input");

        var planType = _assembly.GetType("NGUInjector.Autopilot.AutopilotPlan", true);
        var plan = Activator.CreateInstance(planType, true);
        Assert(!(bool)Property(plan, "GlobalSchedulerCanExecute"),
            "plan cannot promote the shadow scheduler to execution");

        var managerType = _assembly.GetType("NGUInjector.Autopilot.AutopilotManager", true);
        Assert(managerType.GetMethod("BeginAutomationRoot",
                   BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Main bridge can open one typed root");
        Assert(managerType.GetMethod("ExecutePlannedMutations",
                   BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Main bridge can dispatch typed child intents");
        Assert(managerType.GetMethod("RecordAutomationRoot",
                   BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Main bridge publishes exact root settlement telemetry");
        Assert(managerType.GetMethod("ExecuteOrdinaryRebirth",
                   BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Main bridge exposes the typed ordinary-rebirth transaction");
    }

    public static int Main(string[] args)
    {
        try
        {
            _assembly = Assembly.LoadFrom(args != null && args.Length > 0
                ? args[0] : "NGUIdleAutopilot.dll");
            TestExplicitHoldBaseline();
            TestDueProfileSignatureIsStable();
            TestDueCheckpointLeaseAndAllocationHorizon();
            TestGeneratedProfileWatcherNormalization();
            TestObservedAllNegativeMutationCase();
            TestLowerNumberPositivePersistentCase();
            TestRecoveryCounterfactuals();
            TestTask29AuthorityCeilingAndBridge();
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
