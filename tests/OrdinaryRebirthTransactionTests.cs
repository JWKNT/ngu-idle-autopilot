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
        var transaction = File.ReadAllText(Path.Combine("source", "Autopilot",
            "OrdinaryRebirthTransaction.cs"));

        Assert(config.IndexOf("config.AllowRebirths = false;", StringComparison.Ordinal) < 0,
            "deployment ceiling does not erase explicit ordinary-rebirth authority");
        Assert(config.IndexOf("config.AllowChallenges = false;", StringComparison.Ordinal) >= 0,
            "challenge entry remains independently fail-closed");
        Assert(main.IndexOf("ExecuteOrdinaryRebirth(mutationRoot)",
                   StringComparison.Ordinal) >= 0,
            "Main dispatches ordinary rebirth only through the caller-owned root");
        Assert(manager.IndexOf("OrdinaryRebirthTransaction.Execute(root",
                   StringComparison.Ordinal) >= 0,
            "AutopilotManager bridges the live plan to the typed transaction");
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
