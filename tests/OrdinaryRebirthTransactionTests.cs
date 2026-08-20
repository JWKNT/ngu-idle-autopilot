using System;
using System.IO;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

Purpose: Regression-test the live ordinary-rebirth authority and its pure final admission matrix.
Mechanism: The suite varies one copied gate fact at a time and inspects source wiring that cannot be
exercised without a live Unity save. Inputs are synthetic scalar snapshots plus maintained source;
outputs are assertion failures only. It never loads a save or calls a native game controller.
Safety: No runtime/config/injection/process file is read or written. The full-source compile proves
the typed intents remain compatible with the installed read-only game references.
*/
internal static class OrdinaryRebirthTransactionTests
{
    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
    }

    private static OrdinaryRebirthGateInput Ready(bool preview = true)
    {
        return new OrdinaryRebirthGateInput
        {
            Authority = true,
            FullMode = true,
            GameplaySynchronized = true,
            PlanPresent = true,
            PlanExecutionHold = false,
            TargetSeconds = 3600,
            ElapsedSeconds = 3600,
            MinimumSeconds = 3,
            BossId = 58,
            DifficultySelectorClear = true,
            TitanBoundaryClear = true,
            HarvestBoundaryClear = true,
            BloodBoundaryClear = true,
            GrbWindowClear = true,
            ImminentBossClear = true,
            RequirePreview = preview,
            PreviewValid = true,
            PolicyAuthorized = true,
            PolicyReason = "positive exact route"
        };
    }

    private static void AssertHeld(OrdinaryRebirthGateInput input, string contains)
    {
        var result = OrdinaryRebirthGate.Evaluate(input);
        Assert(!result.Ready, contains + " is held");
        Assert(result.Reason.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0,
            contains + " hold is explicit");
    }

    private static void TestAdmissionMatrix()
    {
        Assert(OrdinaryRebirthGate.Evaluate(Ready()).Ready,
            "fully proven due reset is admitted");
        Assert(OrdinaryRebirthGate.Evaluate(Ready(false)).Ready,
            "due reset is admitted to the preview child before preview proof");

        var input = Ready(); input.Authority = false; AssertHeld(input, "authority");
        input = Ready(); input.FullMode = false; AssertHeld(input, "full mode");
        input = Ready(); input.GameplaySynchronized = false; AssertHeld(input, "synchronization");
        input = Ready(); input.PlanPresent = false; AssertHeld(input, "plan is missing");
        input = Ready(); input.PlanExecutionHold = true; AssertHeld(input, "continuation/hold");
        input = Ready(); input.TargetSeconds = double.NaN; AssertHeld(input, "not finite");
        input = Ready(); input.ElapsedSeconds = 3599; AssertHeld(input, "not due");
        input = Ready(); input.ElapsedSeconds = 2; input.TargetSeconds = 2;
        AssertHeld(input, "minimum");
        input = Ready(); input.BossId = 0;
        Assert(OrdinaryRebirthGate.Evaluate(input).Ready,
            "zero-based Boss ID 0 is the playable Boss 1");
        input = Ready(); input.BossId = -1; AssertHeld(input, "selection is invalid");
        input = Ready(); input.BossFight = true; AssertHeld(input, "Fight Boss is active");
        input = Ready(); input.BossNuke = true; AssertHeld(input, "Fight Boss is active");
        input = Ready(); input.NoRebirthChallenge = true; AssertHeld(input, "No Rebirth");
        input = Ready(); input.DifficultySelectorClear = false; AssertHeld(input, "difficulty");
        input = Ready(); input.TitanBoundaryClear = false; AssertHeld(input, "Titan");
        input = Ready(); input.TitanBoundaryClear = false;
        input.TitanBoundaryReason = "typed Titan 3 commitment is still active";
        var typedTitan = OrdinaryRebirthGate.Evaluate(input);
        Assert(!typedTitan.Ready && typedTitan.Reason == input.TitanBoundaryReason,
            "typed Titan interlock reason reaches the final rebirth gate unchanged");
        input = Ready(); input.HarvestBoundaryClear = false; AssertHeld(input, "mature fruit");
        input = Ready(); input.BloodBoundaryClear = false; AssertHeld(input, "Blood");
        input = Ready(); input.GrbWindowClear = false; AssertHeld(input, "GRB");
        input = Ready(); input.ImminentBossClear = false; AssertHeld(input, "two seconds");
        input = Ready(); input.PreviewValid = false; AssertHeld(input, "preview");
        input = Ready(); input.PolicyAuthorized = false; input.PolicyReason = "no-reset wins";
        AssertHeld(input, "no-reset wins");
    }

    private static void TestSourceWiring()
    {
        var config = File.ReadAllText(Path.Combine("source", "Autopilot",
            "AutopilotConfig.cs"));
        var main = File.ReadAllText(Path.Combine("source", "Main.cs"));
        var manager = File.ReadAllText(Path.Combine("source", "Autopilot",
            "AutopilotManager.cs"));
        var planner = File.ReadAllText(Path.Combine("source", "Autopilot",
            "AutopilotPlanner.cs"));
        var optimizer = File.ReadAllText(Path.Combine("source", "Autopilot",
            "RebirthOptimizer.cs"));
        var transaction = File.ReadAllText(Path.Combine("source", "Autopilot",
            "OrdinaryRebirthTransaction.cs"));
        var legacyBoundary = File.ReadAllText(Path.Combine("source", "AllocationProfiles",
            "RebirthStuff", "TimeRebirth.cs"));

        Assert(config.IndexOf("config.AllowRebirths = false;", StringComparison.Ordinal) < 0,
            "deployment ceiling does not erase explicit ordinary-rebirth authority");
        Assert(config.IndexOf("config.AllowChallenges = false;", StringComparison.Ordinal) < 0,
            "deployment normalization preserves explicit typed challenge authority");
        Assert(config.IndexOf("config.ManageMoneyPit = false;", StringComparison.Ordinal) < 0
               && config.IndexOf("config.AllowMoneyPitExecution = false;",
                   StringComparison.Ordinal) < 0,
            "deployment normalization preserves explicit typed Money Pit authority");
        Assert(main.IndexOf("ExecuteOrdinaryRebirth(mutationRoot)",
                   StringComparison.Ordinal) >= 0,
            "Main dispatches ordinary rebirth only through the caller-owned root");
        Assert(manager.IndexOf("OrdinaryRebirthTransaction.Execute(root",
                   StringComparison.Ordinal) >= 0,
            "AutopilotManager bridges the live plan to the typed transaction");
        Assert(planner.IndexOf("var ordinaryRebirthSeconds = plan.RebirthSeconds;",
                   StringComparison.Ordinal) >= 0
               && planner.IndexOf("var ordinaryRebirthHold = plan.RebirthExecutionHold;",
                   StringComparison.Ordinal) >= 0
               && planner.IndexOf("ordinaryRebirthHold ? -1 : ordinaryRebirthSeconds",
                   StringComparison.Ordinal) >= 0
               && planner.IndexOf("var noRebirth = !active.MechanicallyAllowsRebirth;",
                   StringComparison.Ordinal) >= 0
               && planner.IndexOf("plan.RebirthSeconds = ordinaryRebirthSeconds;",
                   StringComparison.Ordinal) >= 0,
            "only native No-Rebirth suppresses the ordinary checkpoint; all other challenges preserve it");
        var forecast = optimizer.IndexOf("Kind = \"first-positive-forecast\"",
            StringComparison.Ordinal);
        Assert(forecast >= 0
               && optimizer.IndexOf("TargetSeconds = forecastTarget", forecast,
                   StringComparison.Ordinal) > forecast
               && optimizer.IndexOf("ExecutionHold = false", forecast,
                   StringComparison.Ordinal) > forecast,
            "the first finite positive forecast is published as an executable rolling countdown");
        Assert(optimizer.IndexOf("Math.Min(_stickyTarget, proposedTarget)",
                   StringComparison.Ordinal) >= 0
               && optimizer.IndexOf("elapsed >= _stickyTarget",
                   StringComparison.Ordinal) >= 0
               && optimizer.IndexOf("Kind = \"latched-forecast-due\"",
                   StringComparison.Ordinal) >= 0
               && optimizer.IndexOf("NextPositiveEtaSeconds = 0",
                   StringComparison.Ordinal) >= 0,
            "an admitted rolling estimate can move earlier but cannot roll past zero");
        Assert(manager.IndexOf("var recoveryPolicy = RebirthOptimizer.EvaluateMutationPolicy(",
                   StringComparison.Ordinal) >= 0
               && manager.IndexOf("recoveryMode && !recoveryPolicy.Authorized",
                   StringComparison.Ordinal) >= 0,
            "runtime telemetry mirrors the same positive-value mutation admission as execution");
        Assert(transaction.IndexOf("RefreshRebirthTimeMultiplier", StringComparison.Ordinal)
               < transaction.IndexOf("RefreshRebirthPreview", StringComparison.Ordinal),
            "preview child preserves native calculateTimeMulti then calculateNextMultis order");
        Assert(transaction.IndexOf("ResetPostconditions.VerifyOrdinary",
                   StringComparison.Ordinal) >= 0,
            "reset child requires the canonical exact ordinary postcondition");
        Assert(transaction.IndexOf("public bool CreatesNewEpoch { get { return true; } }",
                   StringComparison.Ordinal) >= 0,
            "ordinary reset child declares its synchronous epoch transition");
        Assert(transaction.IndexOf("EnterChallenge", StringComparison.Ordinal) < 0,
            "ordinary rebirth transaction cannot enter a challenge");
        Assert(transaction.IndexOf("ratio, recoveryMode, resetRouteEtaSeconds",
                   StringComparison.Ordinal) >= 0
               && transaction.IndexOf("plan.RebirthRecoveryMode", StringComparison.Ordinal) >= 0,
            "typed ordinary final admission preserves recovery mode and finite-route evidence");
        var firstPreview = transaction.IndexOf(
            "root.ExecuteChild(new RebirthPreviewIntent(character))", StringComparison.Ordinal);
        var policyPreview = transaction.IndexOf(
            "var preBloodPolicy = EvaluateLive", StringComparison.Ordinal);
        var bloodSpend = transaction.IndexOf(
            "root.ExecuteChild(new RebirthBloodSpendIntent(character))",
            StringComparison.Ordinal);
        var secondPreview = transaction.IndexOf(
            "post-Blood rebirth preview", StringComparison.Ordinal);
        Assert(firstPreview >= 0 && policyPreview > firstPreview && bloodSpend > policyPreview
               && secondPreview > bloodSpend,
            "Blood is spent only after recovery/value admission and is followed by a fresh native preview");
        Assert(transaction.IndexOf(
                   "_character.bloodSpells.castRebirthSpell(before.Blood)",
                   StringComparison.Ordinal) >= 0
               && transaction.IndexOf("after.Blood == 0.0", StringComparison.Ordinal) >= 0
               && transaction.IndexOf("after.RebirthPower == expectedPower",
                   StringComparison.Ordinal) >= 0,
            "typed boundary Blood spend proves exact full-pool debit and rebirth-power credit");
        Assert(transaction.IndexOf(
                   "before.Blood >= MechanicsEndgame.EndBloodCost",
                   StringComparison.Ordinal) >= 0,
            "ordinary NUMBER spend cannot destroy a fully-funded END Blood delivery");
        Assert(legacyBoundary.IndexOf("minimumNumberRatio, recoveryMode, resetRouteEtaSeconds",
                   StringComparison.Ordinal) >= 0
               && legacyBoundary.IndexOf("minimumNumberRatio, false, -1, -1",
                   StringComparison.Ordinal) < 0,
            "legacy TimeRebirth cannot erase recovery state at the native engage boundary");
    }

    public static int Main()
    {
        try
        {
            TestAdmissionMatrix();
            TestSourceWiring();
            Console.WriteLine("Ordinary rebirth transaction tests passed: "
                              + _assertions + " assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
