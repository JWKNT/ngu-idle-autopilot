using System;
using System.Collections.Generic;
using System.IO;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

This isolated executable verifies lifecycle/save/run identity without loading Unity, the NGU Idle
assembly, a game process, or any save. Fake controller integers and synthetic save fingerprints
exercise startup publication, synchronized-frame/plan barriers, native quickload false/partial/true
classification, same-Character and replaced-controller rebinds, stale queue/latch rejection,
run-transition plan invalidation, and cancellation on reset/load/unload. A disposable temp directory
also proves that durable publication retains the previous generation and validation failure never
overwrites it. Output is assertion diagnostics and an exit code only.

Inputs are the owned lifecycle source files plus synthetic values; no test path is under runtime or
a production save location. Tests never invoke a native method, deserialize BinaryFormatter input,
inject/eject, or contact Steam/cloud. Add scenarios here when a new multi-frame latch/queue adopts
GameEpoch; native copied-save differentials remain a separate, explicitly isolated test program.
*/
internal static class LifecycleEpochTests
{
    private static int _assertions;
    private static int _sessionSequence;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static SaveStateFingerprint Save(string content, long rebirth = 10,
        string difficulty = "normal", string run = null)
    {
        return new SaveStateFingerprint(EpochHash.Sha256(content), 1260, 123456,
            rebirth, difficulty, 100, 20, 0,
            run ?? rebirth + "|" + difficulty + "|none");
    }

    private static ControllerIdentity Controllers(int seed)
    {
        return new ControllerIdentity(seed, seed + 1, seed + 2);
    }

    private static GameEpochController Active(out GameEpochToken activeToken,
        int controllerSeed = 100)
    {
        var epoch = new GameEpochController();
        epoch.StartHost("test-session-" + (++_sessionSequence), Save("start"),
            Controllers(controllerSeed));
        string reason;
        Assert(!epoch.ObserveSynchronizedFrame(Controllers(controllerSeed), out reason),
            "first synchronized frame still waits for a plan");
        var planEpoch = epoch.Current;
        Assert(epoch.InstallPlan(planEpoch, "plan-A", out reason),
            "new-epoch plan installs after synchronization: " + reason);
        Assert(epoch.MutationOpen && epoch.Phase == GameEpochPhase.Active,
            "host becomes active only after sync plus plan");
        activeToken = epoch.Current;
        return epoch;
    }

    private static void TestStartupAndPlanBarrier()
    {
        var epoch = new GameEpochController();
        var published = epoch.StartHost("startup", Save("initial"), Controllers(1));
        Assert(published.HostGeneration > 0 && published.Generation > 0,
            "host publication creates nonzero identity");
        Assert(!epoch.MutationOpen
               && epoch.Phase == GameEpochPhase.AwaitingSynchronization,
            "startup cannot mutate before a later synchronized frame");
        string reason;
        Assert(!epoch.ObserveSynchronizedFrame(Controllers(1), out reason)
               && epoch.Phase == GameEpochPhase.AwaitingPlan,
            "synchronized frame advances to the plan barrier");
        Assert(!epoch.InstallPlan(published, "stale-plan", out reason),
            "plan captured before synchronization cannot install");
        var planEpoch = epoch.Current;
        Assert(epoch.InstallPlan(planEpoch, "fresh-plan", out reason),
            "plan captured in AwaitingPlan installs");
        Assert(epoch.MutationOpen, "fresh plan opens mutation authority");
    }

    private static void TestEpochQueuesLatchesAndRunCancellation()
    {
        GameEpochToken oldToken;
        var epoch = Active(out oldToken);
        var queue = new EpochActionQueue();
        var latch = new EpochLatch<string>();
        var calls = 0;
        var cancellations = 0;
        queue.Enqueue(oldToken, EpochWorkScope.ExactGameState, () => calls++);
        latch.Set(oldToken, "old-result");
        epoch.RegisterCancellation("pending-key-up", () => cancellations++);

        var nextRun = Save("after-reset", 11, "normal", "11|normal|challenge-basic");
        epoch.AdvanceRun(nextRun, Controllers(100), "rebirth committed");
        Assert(cancellations == 1, "run transition synchronously compensates pending work");
        Assert(!epoch.MutationOpen && epoch.Phase == GameEpochPhase.AwaitingPlan,
            "run transition closes old plan authority");
        var discarded = 0;
        queue.Drain(epoch.Current, 10, x => discarded++, x => { throw x; });
        Assert(calls == 0 && discarded == 1,
            "queued old-run action is discarded rather than replayed");
        string value;
        Assert(!latch.TryGet(epoch.Current, out value),
            "old-run latch cannot be observed in successor epoch");
        string reason;
        Assert(epoch.InstallPlan(epoch.Current, "plan-after-reset", out reason),
            "successor run requires and accepts a new plan");
    }

    private static void TestLoadFalseAndPartialAreQuarantined()
    {
        GameEpochToken token;
        var unchanged = Active(out token, 200);
        var cancelled = 0;
        unchanged.RegisterCancellation("pending-load-key", () => cancelled++);
        var loading = unchanged.BeginLoad("test false");
        Assert(cancelled == 1, "load transition synchronously compensates pending work");
        string reason;
        Assert(!unchanged.CommitLoad(loading, false, Save("payload"), Save("before"),
                   Controllers(200), out reason)
               && unchanged.Phase == GameEpochPhase.Quarantined
               && reason.Contains("returned false"),
            "native false quarantines even when before state could be unchanged");

        var partial = Active(out token, 300);
        loading = partial.BeginLoad("test partial false");
        var partialObserved = Save("partial-change");
        Assert(!partial.CommitLoad(loading, false, Save("payload"), partialObserved,
                   Controllers(300), out reason)
               && partial.Phase == GameEpochPhase.Quarantined
               && partial.Current.SaveContentHash == partialObserved.EffectiveContentHash,
            "native false records observed partial identity but never blesses it");
    }

    private static void TestLoadTrueSameAndReplacedControllers()
    {
        GameEpochToken old;
        var same = Active(out old, 400);
        var expected = Save("serialized-input", 50, "evil", "50|evil|none");
        // Offline reconciliation can change the exact bytes while the imported persistent fields
        // and run signature remain equal.
        var actual = Save("post-offline-state", 50, "evil", "50|evil|none");
        var loading = same.BeginLoad("same Character");
        string reason;
        Assert(same.CommitLoad(loading, true, expected, actual, Controllers(400), out reason),
            "native true with stable fields accepts the same controller identities: " + reason);
        Assert(!old.Matches(same.Current, EpochWorkScope.ExactGameState)
               && same.Phase == GameEpochPhase.AwaitingSynchronization,
            "successful load creates a distinct save generation before sync");
        Assert(!same.ObserveSynchronizedFrame(Controllers(400), out reason)
               && same.Phase == GameEpochPhase.AwaitingPlan,
            "successful load requires a later synchronized frame and then a plan");
        Assert(same.InstallPlan(same.Current, "same-character-plan", out reason)
               && same.MutationOpen,
            "same-Character load reopens only after the new plan");

        var replaced = Active(out old, 500);
        loading = replaced.BeginLoad("replacement controllers");
        Assert(replaced.CommitLoad(loading, true, expected, actual, Controllers(900), out reason),
            "native true accepts explicitly rebound replacement controller identities");
        Assert(!replaced.ObserveSynchronizedFrame(Controllers(500), out reason)
               && replaced.Phase == GameEpochPhase.Quarantined,
            "old controller identities cannot synchronize the replaced graph");
    }

    private static void TestLoadFingerprintMismatchAndUnloadCancellation()
    {
        GameEpochToken old;
        var mismatch = Active(out old, 600);
        var loading = mismatch.BeginLoad("mismatch");
        string reason;
        Assert(!mismatch.CommitLoad(loading, true, Save("input", 5, "normal"),
                   Save("after", 6, "normal"), Controllers(600), out reason)
               && mismatch.Phase == GameEpochPhase.Quarantined
               && reason.Contains("rebirth number"),
            "native true cannot override a false persistent-state postcondition");

        var unloading = Active(out old, 700);
        var released = 0;
        unloading.RegisterCancellation("os-key", () => released++);
        unloading.BeginUnload("ejection");
        Assert(released == 1 && unloading.Phase == GameEpochPhase.Unloading
               && !unloading.MutationOpen,
            "unload releases pending compensation before closing the host");
    }

    private static void TestDurableLastGoodGenerations()
    {
        var root = Path.Combine(Path.GetTempPath(), "ngu-lifecycle-test-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "snapshot.txt");
            var first = DurableGenerationWriter.WriteText(path, "generation-one",
                candidate => File.ReadAllText(candidate).Contains("one"));
            Assert(File.ReadAllText(path) == "generation-one"
                   && string.IsNullOrEmpty(first.PreviousGenerationPath),
                "first durable generation publishes without a fabricated backup");

            var second = DurableGenerationWriter.WriteText(path, "generation-two",
                candidate => File.ReadAllText(candidate).Contains("two"));
            Assert(File.ReadAllText(path) == "generation-two",
                "second durable generation becomes current");
            Assert(File.Exists(second.PreviousGenerationPath)
                   && File.ReadAllText(second.PreviousGenerationPath) == "generation-one",
                "atomic replacement retains the exact prior good generation");

            var rejected = false;
            try
            {
                DurableGenerationWriter.WriteText(path, "torn", candidate => false);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            Assert(rejected && File.ReadAllText(path) == "generation-two"
                   && File.ReadAllText(second.PreviousGenerationPath) == "generation-one",
                "validation failure preserves current and previous generations");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void TestMainLifecycleSourceContract()
    {
        var main = File.ReadAllText(Path.Combine("source", "Main.cs"));
        var loader = File.ReadAllText(Path.Combine("source", "Loader.cs"));
        var awake = main.IndexOf("public void Awake()", StringComparison.Ordinal);
        var publish = main.IndexOf("reference = this;", awake, StringComparison.Ordinal);
        var schedule = main.IndexOf("InvokeRepeating(\"GameplaySyncRoutine\"",
            StringComparison.Ordinal);
        Assert(awake >= 0 && publish > awake && schedule > publish,
            "Main publishes its instance before scheduling any repeating callback");

        var quickSave = main.IndexOf("private void QuickSave()", StringComparison.Ordinal);
        var stamp = main.IndexOf("Character.lastTime = snapshotTime;", quickSave,
            StringComparison.Ordinal);
        var serialize = main.IndexOf("Character.importExport.getBase64Data()", quickSave,
            StringComparison.Ordinal);
        Assert(quickSave >= 0 && stamp > quickSave && serialize > stamp,
            "F3 advances native lastTime before snapshot serialization");
        Assert(main.IndexOf("DurableGenerationWriter.WriteText", StringComparison.Ordinal) >= 0,
            "Main routes snapshots through durable last-good generation publication");

        var quickLoad = main.IndexOf("private void QuickLoad()", StringComparison.Ordinal);
        Assert(main.IndexOf("native.LoadSave", quickLoad,
                   StringComparison.Ordinal) > quickLoad,
            "F7 uses the build-pinned native Boolean load adapter");
        Assert(main.IndexOf("invocation.ReturnValue is bool", quickLoad,
                   StringComparison.Ordinal) > quickLoad,
            "F7 explicitly checks the native Boolean result");
        Assert(main.IndexOf("TryRebindGameControllers", quickLoad,
                   StringComparison.Ordinal) > quickLoad
               && main.IndexOf("CommitLoad(loadingEpoch", quickLoad,
                   StringComparison.Ordinal) > quickLoad,
            "F7 rebinds controllers and requires the epoch fingerprint postcondition");
        Assert(main.IndexOf("ObserveGameEpochTransitions();", StringComparison.Ordinal) >= 0,
            "scheduler callbacks observe reset epochs before mutation");
        Assert(main.IndexOf("gameEpochFingerprint", StringComparison.Ordinal) >= 0
               && main.IndexOf("gameEpochSaveGeneration", StringComparison.Ordinal) >= 0,
            "deployment telemetry carries the lifecycle identity needed for process handshake");

        var fastAllocation = main.IndexOf("void FastAllocationRoutine()",
            StringComparison.Ordinal);
        var quickStuff = main.IndexOf("void QuickStuff()", fastAllocation,
            StringComparison.Ordinal);
        var fastAllocationBody = fastAllocation >= 0 && quickStuff > fastAllocation
            ? main.Substring(fastAllocation, quickStuff - fastAllocation)
            : string.Empty;
        Assert(fastAllocationBody.IndexOf("TransactionComplete = false",
                   StringComparison.Ordinal) < 0
               && fastAllocationBody.IndexOf("TransactionError =",
                   StringComparison.Ordinal) < 0,
            "a deliberately held fast path cannot invalidate a successfully closed typed root");
        Assert(fastAllocationBody.IndexOf("PublishDecisionAfterAutomation",
                   StringComparison.Ordinal) >= 0,
            "the held fast path still publishes the exact completed root envelope");

        var unloadCall = loader.IndexOf("Main.reference.Unload();", StringComparison.Ordinal);
        var invalidate = loader.IndexOf("ExecutionSafety.Invalidate(\"injector lifecycle Unload\")",
            StringComparison.Ordinal);
        Assert(unloadCall >= 0 && invalidate > unloadCall,
            "Loader lets Main compensate/close its epoch before final lease invalidation");
    }

    public static int Main()
    {
        try
        {
            TestStartupAndPlanBarrier();
            TestEpochQueuesLatchesAndRunCancellation();
            TestLoadFalseAndPartialAreQuarantined();
            TestLoadTrueSameAndReplacedControllers();
            TestLoadFingerprintMismatchAndUnloadCancellation();
            TestDurableLastGoodGenerations();
            TestMainLifecycleSourceContract();
            Console.WriteLine("Lifecycle epoch tests passed: " + _assertions + " assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
