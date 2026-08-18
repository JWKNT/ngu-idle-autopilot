using System;
using System.IO;
using NGUInjector.AllocationProfiles;

/*
FILE PURPOSE

This isolated suite compiles allocation JSON without Unity or Assembly-CSharp. It proves complete
documents install monotonically, torn/invalid watcher snapshots preserve the prior in-memory source
and version, every mutation-capable section is required, and the production compiler has no game or
controller dependency. It writes no profile, runtime, save, or game file.
*/
internal static class AllocationReloadTests
{
    private static int _assertions;

    private const string ValidLegacy = @"{
  ""Breakpoints"": {
    ""Energy"": [{""Time"": {""m"": 1}, ""Priorities"": [""CAPALLBT:20"", ""CAPAT-1:10""]}],
    ""Magic"": [{""Time"": 0, ""Priorities"": [""BR""]}],
    ""R3"": [{""Time"": 0, ""Priorities"": [""BESTHACK""]}],
    ""Gear"": [{""Time"": 0, ""ID"": [1, 2]}],
    ""Diggers"": [{""Time"": 0, ""List"": [0]}],
    ""Wandoos"": [{""Time"": 0, ""OS"": 0}],
    ""NGUDiff"": [{""Time"": 0, ""Diff"": 0}],
    ""RebirthTime"": -1
  }
}";

    private const string ValidModern = @"{
  ""Breakpoints"": {
    ""Energy"": [{""Time"": 0, ""Priorities"": []}],
    ""Magic"": [{""Time"": 0, ""Priorities"": []}],
    ""R3"": [{""Time"": 0, ""Priorities"": []}],
    ""Gear"": [{""Time"": 0, ""ID"": []}],
    ""Diggers"": [{""Time"": 0, ""List"": []}],
    ""Wandoos"": [{""Time"": 0, ""OS"": 2}],
    ""NGUDiff"": [{""Time"": 0, ""Diff"": 2}],
    ""Rebirth"": {""Type"": ""TIME"", ""Target"": {""h"": 1, ""s"": 5}, ""Challenges"": [""BASIC-0""]}
  }
}";

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static void TestPureCompilation()
    {
        var result = AllocationPlanCompiler.Compile(ValidLegacy);
        Assert(result.Success, "complete legacy plan compiles: " + result.Error);
        Assert(result.Plan.Energy.Length == 1 && result.Plan.Energy[0].Time == 60,
            "h/m/s time is compiled without live state");
        Assert(result.Plan.Energy[0].Priorities.Length == 2,
            "validated priorities are preserved");
        Assert(result.Plan.Rebirth.UsesLegacyTime && result.Plan.Rebirth.Target == -1,
            "legacy no-rebirth is represented without constructing BaseRebirth");
        Assert(!string.IsNullOrEmpty(result.Plan.Fingerprint), "compiled plan has stable identity");

        var modern = AllocationPlanCompiler.Compile(ValidModern);
        Assert(modern.Success, "complete modern plan compiles: " + modern.Error);
        Assert(!modern.Plan.Rebirth.UsesLegacyTime && modern.Plan.Rebirth.Target == 3605,
            "modern TIME target is normalized to seconds");
        Assert(modern.Plan.Rebirth.Challenges.Length == 1
               && modern.Plan.Rebirth.Challenges[0] == "BASIC-0",
            "challenge target survives pure validation");
    }

    private static void TestTornAndInvalidDocuments()
    {
        var torn = AllocationPlanCompiler.Compile(ValidLegacy.Substring(0, ValidLegacy.Length - 2));
        Assert(!torn.Success && !string.IsNullOrEmpty(torn.Error),
            "truncated watcher snapshot is rejected explicitly");
        Assert(!AllocationPlanCompiler.Compile(ValidLegacy + " garbage").Success,
            "trailing data is rejected");
        Assert(!AllocationPlanCompiler.Compile(ValidLegacy.Replace("}],\n    \"Magic\"", "}]\n    \"Magic\"")).Success,
            "closed but syntactically invalid JSON is rejected");
        Assert(!AllocationPlanCompiler.Compile(ValidLegacy.Replace("\"Gear\"", "\"MissingGear\"")).Success,
            "missing mutation-capable section is rejected rather than defaulted");
        Assert(!AllocationPlanCompiler.Compile(ValidLegacy.Replace("CAPALLBT:20", "UNKNOWN:20")).Success,
            "unknown priority is rejected rather than silently dropped");
        Assert(!AllocationPlanCompiler.Compile(ValidLegacy.Replace("\"OS\": 0", "\"OS\": 9")).Success,
            "out-of-range native enum is rejected");
    }

    private static void TestLastGoodSlotAndVersioning()
    {
        var slot = new AllocationPlanSlot();
        string error;
        Assert(slot.TryInstall(ValidLegacy, out error), "first plan installs");
        var first = slot.Current;
        var firstSource = slot.LastGoodSource;
        Assert(slot.Version == 1 && first.InstallationVersion == 1,
            "first installed plan is version one");

        Assert(!slot.TryInstall("{\"Breakpoints\":", out error), "torn reload is refused");
        Assert(object.ReferenceEquals(first, slot.Current), "torn reload retains exact in-memory plan");
        Assert(slot.LastGoodSource == firstSource, "torn reload retains last-good disk source");
        Assert(slot.Version == 1, "rejected reload does not consume a plan version");

        Assert(slot.TryInstall(ValidModern, out error), "next complete plan installs");
        Assert(slot.Version == 2 && slot.Current.InstallationVersion == 2,
            "successful reload advances version exactly once");
        Assert(!object.ReferenceEquals(first, slot.Current), "successful reload replaces the candidate");
    }

    private static void TestProductionBoundaryIsControllerFree()
    {
        var compiler = File.ReadAllText("source/AllocationProfiles/AllocationPlanCompiler.cs");
        Assert(!compiler.Contains("Main.Character") && !compiler.Contains("Main.")
               && !compiler.Contains("Controller.") && !compiler.Contains("using UnityEngine"),
            "compiler source has no Character/Main/controller/Unity dependency");
        var custom = File.ReadAllText("source/AllocationProfiles/CustomAllocation.cs");
        Assert(!custom.Contains("this.DoAllocations()"),
            "reload no longer directly triggers an allocation sweep");
    }

    public static int Main()
    {
        try
        {
            TestPureCompilation();
            TestTornAndInvalidDocuments();
            TestLastGoodSlotAndVersioning();
            TestProductionBoundaryIsControllerFree();
            Console.WriteLine("Allocation reload tests passed: " + _assertions + " assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
