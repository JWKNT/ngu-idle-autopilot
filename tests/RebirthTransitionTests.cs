/*
FILE PURPOSE

RebirthTransitionTests is the isolated, reflection-based golden suite for task 15's pure rebirth
transition and bounded route evaluator. It loads a temporary bot DLL, never Unity/game state, and
proves replacement recurrence, all native time branches, Blood/Attack steps, source-order bank
floors, Boss-0 replay, a finite continuation edge, and a tiny exhaustive route result.
*/
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

internal static class RebirthTransitionTests
{
    private static Assembly _assembly;
    private static int _assertions;
    private static int _failures;

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: RebirthTransitionTests <temporary bot dll>");
            return 2;
        }
        _assembly = Assembly.LoadFrom(args[0]);
        Run("replacement recurrence 5 -> 17 -> 17", TestReplacementRecurrence);
        Run("native time staircase and strict boundaries", TestTimeStaircase);
        Run("Blood preview, +1, and Attack-only step", TestPreviewFactors);
        Run("build-pinned final preview call order", TestPinnedPreflightContract);
        Run("exact Beard and Guff zero floors", TestBankFloors);
        Run("Boss-0 replay and multi-Boss chain", TestBossReplay);
        Run("finite continuation and tiny brute force", TestRouteOracle);
        Console.WriteLine(_failures == 0
            ? "PASS: " + _assertions + " rebirth transition assertions"
            : "FAIL: " + _failures + " group(s), " + _assertions + " assertions");
        return _failures == 0 ? 0 : 1;
    }

    private static void TestReplacementRecurrence()
    {
        var state = State(100.0, 4.0, 1.0, 1.0, 1.0, 0L, 1.0);
        Near(5.0, Number(Preview(state), "Attack"), "first preview is 1 + 4");
        var first = Call("NGUInjector.Autopilot.RebirthTransitionKernel",
            "ApplyOrdinaryRebirth", state, null);
        Near(5.0, Number(first, "CurrentAttackNumber"), "first reset replaces Number with 5");
        Near(4.0, Number(first, "OldBossMulti"), "finished Boss multiplier becomes one-run memory");
        Near(1.0, Number(first, "BossMulti"), "reset starts with Boss multiplier one");
        Equal(0, Convert.ToInt32(Field(first, "BossId")), "reset replay starts at Boss 0");

        Set(first, "BossMulti", 4.0);
        Set(first, "TimeMulti", 1.0);
        Set(first, "RunSeconds", 3600.0);
        Near(17.0, Number(Preview(first), "Attack"), "second identical run previews 17");
        var second = Call("NGUInjector.Autopilot.RebirthTransitionKernel",
            "ApplyOrdinaryRebirth", first, null);
        Set(second, "BossMulti", 4.0);
        Set(second, "TimeMulti", 1.0);
        Set(second, "RunSeconds", 3600.0);
        Near(17.0, Number(Preview(second), "Attack"),
            "third identical run remains 17 instead of compounding geometrically");
    }

    private static void TestTimeStaircase()
    {
        var boundaries = new[] {60.0, 120.0, 180.0, 240.0, 300.0, 420.0,
            600.0, 720.0, 900.0, 1800.0, 3600.0};
        var belowDivisors = new[] {34359738368.0, 33554432.0, 518144.0, 16192.0,
            2048.0, 512.0, 128.0, 32.0, 8.0, 4.0, 2.0};
        var atDivisors = new[] {33554432.0, 518144.0, 16192.0, 2048.0,
            512.0, 128.0, 32.0, 8.0, 4.0, 2.0, 0.0};
        const double epsilon = 0.000001;
        for (var i = 0; i < boundaries.Length; i++)
        {
            var before = boundaries[i] - epsilon;
            Near(before / belowDivisors[i] / 3600.0, Time(before),
                "strict branch immediately below " + boundaries[i]);
            var expected = boundaries[i] == 3600.0
                ? 1.0 + boundaries[i] / 172800.0
                : boundaries[i] / atDivisors[i] / 3600.0;
            Near(expected, Time(boundaries[i]), "exact branch at " + boundaries[i]);
        }
        Near(1.0 + 7200.0 / 172800.0, Time(7200.0),
            "post-hour segment remains linear with native offset");
        Near(Time(120.0), Convert.ToDouble(Call(
                "NGUInjector.Autopilot.StrategyCheckpointPlanner",
                "NumberTimeMultiplier", 120.0)),
            "later-stage planner delegates its formerly collapsed sub-300 branch to the kernel");
    }

    private static void TestPreviewFactors()
    {
        var state = State(999.0, 2.0, 3.0, 5.0, 4.0, 9999L, 7.0);
        Set(state, "BloodPower", 11.0);
        var before = Number(Preview(state), "Attack");
        Near(1.0 + 2.0 * 3.0 * 4.0 * 1.0 * 5.0 * 11.0 * 7.0, before,
            "native +1 is outside every multiplicative factor");
        Set(state, "BloodPower", 13.0);
        var afterBlood = Number(Preview(state), "Attack");
        Near(1.0 + (before - 1.0) * 13.0 / 11.0, afterBlood,
            "Blood preview scales only the product below +1");
        Set(state, "BloodPower", 11.0);
        Set(state, "TotalAttackTrainingLevels", 10000L);
        Near(1.0 + 2.0 * (before - 1.0), Number(Preview(state), "Attack"),
            "Attack 10,000 threshold advances the integer Number step exactly");

        var reset = Call("NGUInjector.Autopilot.RebirthTransitionKernel",
            "ApplyOrdinaryRebirth", state, null);
        Near(Number(Preview(state), "Attack"), Number(reset, "CurrentAttackNumber"),
            "reset assigns the exact +1 preview, not current Number times a ratio");
        Near(1.0, Number(reset, "BloodPower"),
            "Blood power is consumed into the banked preview and resets to native baseline one");
    }

    private static void TestBankFloors()
    {
        var input = New("NGUInjector.Autopilot.RebirthBankInput");
        Set(input, "ActiveBeardTemporaryLevels", new long[] {1000000L, 8L});
        var guffInput = New("NGUInjector.Autopilot.MacGuffinConversionInput");
        Set(guffInput, "ItemId", 198);
        Set(guffInput, "ItemLevel", 0);
        Set(guffInput, "PersistentAccumulatorBefore", 1.0);
        Set(input, "EquippedMacGuffins", ArrayOf(
            "NGUInjector.Autopilot.MacGuffinConversionInput", guffInput));

        var beforeGate = Call("NGUInjector.Autopilot.RebirthTransitionKernel",
            "EvaluateBank", input, 179.0);
        Equal(0L, Convert.ToInt64(Field(beforeGate, "BeardTrimmings")),
            "Beards bank zero before one hour");
        Near(0.0, Number(beforeGate, "MacGuffinAccumulatorDelta"),
            "Guff banks zero before 180 seconds");

        Set(input, "EquippedMacGuffins", Array.CreateInstance(
            Type("NGUInjector.Autopilot.MacGuffinConversionInput"), 0));
        Set(input, "ActiveBeardTemporaryLevels", new long[] {8L});
        var beardFloor = Call("NGUInjector.Autopilot.RebirthTransitionKernel",
            "EvaluateBank", input, 3600.0);
        Equal(0L, Convert.ToInt64(Field(beardFloor, "BeardTrimmings")),
            "level 8 at one hour floors to zero trimmings");

        Set(guffInput, "ItemId", 289);
        Set(input, "EquippedMacGuffins", ArrayOf(
            "NGUInjector.Autopilot.MacGuffinConversionInput", guffInput));
        var finished = State(999.0, 4.0, 1.0, 1.0, 1.0, 0L, 1.0);
        Set(finished, "RunSeconds", 1800.0);
        var banked = Call("NGUInjector.Autopilot.RebirthTransitionKernel",
            "ApplyOrdinaryRebirth", finished, input);
        Near(5.0, Number(banked, "CurrentAttackNumber"),
            "Guff conversion does not retroactively alter the already-published preview");
        True(Number(banked, "CumulativeMacGuffinAccumulatorDelta") > 0.0,
            "equipped Number Guff adds its exact permanent accumulator delta");
        True(Number(banked, "AttackPersistentNumberFactor") > 1.0,
            "Number Guff bank affects later previews only");
    }

    private static void TestPinnedPreflightContract()
    {
        var preflight = Type("NGUInjector.AllocationProfiles.RebirthStuff.TimeRebirth")
            .GetMethod("SynchronizeFinalPreview", BindingFlags.Instance | BindingFlags.NonPublic);
        var adapters = Type("NGUInjector.Autopilot.NativeMutationAdapters");
        var time = adapters.GetMethod("RefreshRebirthTimeMultiplier",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var preview = adapters.GetMethod("RefreshRebirthPreview",
            BindingFlags.Instance | BindingFlags.NonPublic);
        True(preflight != null && time != null && preview != null,
            "preflight and both exact native adapters exist");
        var il = preflight.GetMethodBody().GetILAsByteArray();
        var timeAt = CallTokenIndex(il, time.MetadataToken);
        var previewAt = CallTokenIndex(il, preview.MetadataToken);
        True(timeAt >= 0, "preflight calls the pinned calculateTimeMulti adapter");
        True(previewAt > timeAt,
            "calculateTimeMulti is unconditionally dispatched before calculateNextMultis");
    }

    private static void TestBossReplay()
    {
        var state = State(100.0, 4.0, 1.0, Time(3600.0), 1.0, 0L, 100.0);
        Set(state, "RunSeconds", 3600.0);
        Set(state, "BossId", 2);
        var problem = Problem(state, new[]
        {
            Boss(0, 1, 1.0, 1.0, 2.0),
            Boss(1, 2, 1.0, 1.0, 2.0),
            Boss(2, 3, 1.0, 1.0, 2.0)
        }, new double[] {3600.0}, 3, 1);
        var estimate = Call("NGUInjector.Autopilot.RebirthRouteEvaluator",
            "EvaluateForcedReset", problem, 3600.0);
        True((bool)Field(estimate, "TerminalReached"), "forced reset replay reaches Boss 3");
        Near(3.0, Number(estimate, "EtaSeconds"), "three replay Boss edges cost three seconds");
        var actions = (string[])Field(estimate, "Actions");
        var reset = Array.IndexOf(actions, "reset@3600s");
        True(reset >= 0 && reset + 1 < actions.Length && actions[reset + 1] == "boss:0->1",
            "post-reset chain begins at Boss 0 even when the finished run was at Boss 2");
        Equal(3, actions.Skip(reset + 1).Count(x => x.StartsWith("boss:")),
            "all three replayable Bosses are applied, not one Boolean reward");
    }

    private static void TestRouteOracle()
    {
        var idle = State(1.0, 1.0, 1.0, Time(100.0), 1.0, 0L, 1.0);
        Set(idle, "RunSeconds", 100.0);
        var idleProblem = Problem(idle, new object[0], new double[0], 1, 0);
        var continuation = Call("NGUInjector.Autopilot.RebirthRouteEvaluator",
            "EvaluateContinuation", idleProblem);
        True(!(bool)Field(continuation, "TerminalReached"),
            "unreachable continuation does not fabricate a terminal ETA");
        Near(-1.0, Number(continuation, "EtaSeconds"), "unknown terminal ETA is explicit");
        Near(20.0, Number(continuation, "NextContinuationEventSeconds"),
            "next native 120-second time boundary is a finite continuation edge");

        // Tiny exhaustive world: the finished run has killed Boss 0 and has enough Boss/time
        // factors to bank Number > 5. Reset-now replays Boss 0 and clears Boss 1; continuing to
        // the next event cannot return to the already-due reset checkpoint and remains stuck.
        var start = State(1.0, 4.0, 1.0, Time(3600.0), 1.0, 0L, 1.0);
        Set(start, "RunSeconds", 3600.0);
        Set(start, "BossId", 1);
        var tiny = Problem(start, new[]
        {
            Boss(0, 1, 1.0, 1.0, 4.0),
            Boss(1, 2, 1.0, 5.0, 2.0)
        }, new double[] {3600.0}, 2, 1);
        var comparison = Call("NGUInjector.Autopilot.RebirthRouteEvaluator", "Compare", tiny);
        var preferred = Field(comparison, "Preferred");
        True((bool)Field(preferred, "FirstActionIsReset"),
            "bounded route solver agrees with exhaustive reset-versus-continue enumeration");
        Near(2.0, Number(preferred, "EtaSeconds"),
            "tiny brute-force terminal time is the two-Boss replay from an immediate reset");

        // Attack training at an event boundary is applied before the reset preview.
        var trainingStart = State(1.0, 1.0, 1.0, 0.0, 1.0, 0L, 1.0);
        var trainingProblem = Problem(trainingStart, new object[0],
            new double[] {3600.0}, 1, 1);
        Set(trainingProblem, "TrainingSteps", ArrayOf(
            "NGUInjector.Autopilot.RebirthRouteTrainingStep",
            Training(0.0, 10000L), Training(100.0, 10000L)));
        var trainingReset = Call("NGUInjector.Autopilot.RebirthRouteEvaluator",
            "EvaluateForcedReset", trainingProblem, 3600.0);
        var final = Field(trainingReset, "FinalState");
        Near(1.0 + 2.0 * Time(3600.0), Number(final, "CurrentAttackNumber"),
            "crossed Attack-training step changes the replacement preview before reset");
        Equal(10000L, Convert.ToInt64(Field(final, "TotalAttackTrainingLevels")),
            "post-reset age-zero insta-training is applied after Number is banked");
    }

    private static object State(double current, double boss, double oldBoss, double time,
        double oldTime, long attackLevels, double persistent)
    {
        var state = New("NGUInjector.Autopilot.RebirthTransitionState");
        Set(state, "CurrentAttackNumber", current);
        Set(state, "CurrentDefenseNumber", current);
        Set(state, "BossMulti", boss);
        Set(state, "OldBossMulti", oldBoss);
        Set(state, "TimeMulti", time);
        Set(state, "OldTimeMulti", oldTime);
        Set(state, "TotalAttackTrainingLevels", attackLevels);
        Set(state, "AttackPersistentNumberFactor", persistent);
        Set(state, "DefensePersistentNumberFactor", persistent);
        Set(state, "BloodPower", 1.0);
        return state;
    }

    private static object Boss(int from, int to, double seconds, double minimum,
        double reward)
    {
        var boss = New("NGUInjector.Autopilot.RebirthRouteBossStep");
        Set(boss, "FromBossId", from);
        Set(boss, "ToBossId", to);
        Set(boss, "ReplaySeconds", seconds);
        Set(boss, "MinimumAttackNumber", minimum);
        Set(boss, "MinimumDefenseNumber", minimum);
        Set(boss, "BossMultiFactor", reward);
        return boss;
    }

    private static object Training(double at, long gained)
    {
        var step = New("NGUInjector.Autopilot.RebirthRouteTrainingStep");
        Set(step, "AtRunSeconds", at);
        Set(step, "AttackLevelsGained", gained);
        return step;
    }

    private static object Problem(object state, IEnumerable bosses, double[] resetAges,
        int target, int maxResets)
    {
        var problem = New("NGUInjector.Autopilot.RebirthRouteProblem");
        Set(problem, "InitialState", state);
        Set(problem, "BossSteps", ArrayOf("NGUInjector.Autopilot.RebirthRouteBossStep",
            bosses.Cast<object>().ToArray()));
        Set(problem, "ResetCandidateAges", resetAges);
        Set(problem, "MinimumRebirthSeconds", 0.0);
        Set(problem, "HorizonSeconds", 20000.0);
        Set(problem, "TargetBossId", target);
        Set(problem, "MaximumResets", maxResets);
        Set(problem, "MaximumEvents", 32);
        return problem;
    }

    private static object Preview(object state)
    {
        return Call("NGUInjector.Autopilot.RebirthTransitionKernel", "Preview", state);
    }

    private static double Time(double seconds)
    {
        return Convert.ToDouble(Call("NGUInjector.Autopilot.RebirthTransitionKernel",
            "ExactTimeMultiplier", seconds));
    }

    private static Type Type(string name)
    {
        var type = _assembly.GetType(name, false);
        if (type == null) throw new Exception("missing type " + name);
        return type;
    }

    private static object New(string name)
    {
        return Activator.CreateInstance(Type(name), true);
    }

    private static object Call(string typeName, string methodName, params object[] args)
    {
        var methods = Type(typeName).GetMethods(BindingFlags.Static | BindingFlags.Public
                                                 | BindingFlags.NonPublic)
            .Where(x => x.Name == methodName && x.GetParameters().Length == args.Length).ToArray();
        if (methods.Length != 1)
            throw new Exception(typeName + "." + methodName + " overload is ambiguous/missing");
        return methods[0].Invoke(null, args);
    }

    private static void Set(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public
                                                    | BindingFlags.NonPublic);
        if (field == null) throw new Exception("missing field " + target.GetType().Name + "." + name);
        field.SetValue(target, value);
    }

    private static object Field(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public
                                                    | BindingFlags.NonPublic);
        if (field == null) throw new Exception("missing field " + target.GetType().Name + "." + name);
        return field.GetValue(target);
    }

    private static double Number(object target, string name)
    {
        return Convert.ToDouble(Field(target, name));
    }

    private static Array ArrayOf(string elementType, params object[] values)
    {
        var array = Array.CreateInstance(Type(elementType), values.Length);
        for (var i = 0; i < values.Length; i++) array.SetValue(values[i], i);
        return array;
    }

    private static int CallTokenIndex(byte[] il, int token)
    {
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6f) continue; // call / callvirt
            if (BitConverter.ToInt32(il, i + 1) == token) return i;
        }
        return -1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception error)
        {
            _failures++;
            var current = error is TargetInvocationException && error.InnerException != null
                ? error.InnerException : error;
            Console.WriteLine("FAIL " + name + ": " + current.Message);
        }
    }

    private static void True(bool value, string message)
    {
        _assertions++;
        if (!value) throw new Exception(message);
    }

    private static void Equal(long expected, long actual, string message)
    {
        _assertions++;
        if (expected != actual)
            throw new Exception(message + ": expected " + expected + ", actual " + actual);
    }

    private static void Near(double expected, double actual, string message)
    {
        _assertions++;
        var tolerance = Math.Max(1e-11, Math.Abs(expected) * 1e-10);
        if (Math.Abs(expected - actual) > tolerance)
            throw new Exception(message + ": expected " + expected + ", actual " + actual);
    }
}
