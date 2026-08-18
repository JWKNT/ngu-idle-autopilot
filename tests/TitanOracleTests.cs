/*
FILE PURPOSE

Purpose: This standalone executable locks the pure installed-source Titan oracle for T1-T14.

Mechanism: It compiles only with TitanMechanics.cs and exercises exact descriptor, clock, unlock,
enemy-type/index, candidate native-autokill, manual-prerequisite, terminal retry, and cumulative T12
boundaries using in-memory scalars. Float predecessors test the native float32 comparisons.

Inputs and outputs: There are no external inputs. Assertion diagnostics go to stdout/stderr and the
process returns nonzero on failure.

Invariants and safety: Tests never load Unity or Assembly-CSharp, read a save, inspect runtime state,
invoke a controller, mutate inventory, build the injector DLL, or touch the running game.
*/
using System;
using NGUInjector.Autopilot;

internal static class TitanOracleTests
{
    private static int _assertions;

    private static readonly int[] Zones =
        {6, 8, 11, 14, 16, 19, 23, 26, 30, 34, 38, 42, 44, 45};
    private static readonly int[] Gates =
        {58, 66, 82, 100, 116, 132, 426, 467, 491, 777, 826, 850, 897, 902};
    private static readonly int[] BaseClocks =
        {3600, 3600, 7200, 7200, 10800, 12600, 16200, 18000, 19800,
            23400, 25200, 27000, 27000, 27000};
    private static readonly float[,,] AutokillGoldens =
    {
        {{2.5E+09f,1.6E+09f,2.5E+07f},{2.5E+10f,1.6E+10f,2.5E+08f},{2.5E+11f,1.6E+11f,2.5E+09f},{2.5E+12f,1.6E+12f,2.5E+10f}},
        {{5E+14f,2.5E+14f,5E+12f},{1E+16f,5E+15f,1E+14f},{2E+17f,1E+17f,2E+15f},{5E+18f,2.5E+18f,5E+16f}},
        {{5E+18f,2.5E+18f,5E+16f},{1E+20f,5E+19f,1E+18f},{2E+21f,1E+21f,2E+19f},{5E+22f,2.5E+22f,5E+20f}},
        {{1E+23f,5E+22f,1E+21f},{2E+24f,1E+24f,2E+22f},{4E+25f,2E+25f,4E+23f},{7.5E+26f,3.7E+26f,7.5E+24f}},
        {{4E+28f,2E+28f,4E+26f},{3.2E+29f,1.6E+29f,1.6E+27f},{2E+30f,1E+30f,9.999999E+27f},{1E+31f,5E+30f,5E+28f}},
        {{1.8E+31f,6E+30f,1.2E+29f},{9E+31f,3E+31f,6E+29f},{3.6E+32f,1.2E+32f,2.5E+30f},{1.1E+33f,3.6E+32f,7.5E+30f}},
        {{3E+33f,1E+33f,2E+31f},{1.2E+34f,4E+33f,8E+31f},{3.6E+34f,1.2E+34f,2.4E+32f},{7.2E+34f,2.4E+34f,4.8E+32f}}
    };

    public static int Main()
    {
        try
        {
            TestDescriptorsAndClocks();
            TestUnlockAndTerminalState();
            TestEnemyTypesAndIndexes();
            TestAutokillThresholds();
            TestManualPrerequisites();
            TestCumulativeTitan12();
            TestClockTransforms();
            Console.WriteLine("Titan oracle tests passed: " + _assertions + " assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void TestDescriptorsAndClocks()
    {
        for (var titan = 1; titan <= 14; titan++)
        {
            var descriptor = TitanMechanics.Describe(titan);
            Equal(Zones[titan - 1], descriptor.Zone, "exact zone T" + titan);
            Equal(Gates[titan - 1], descriptor.EffectiveBossGate, "exact gate T" + titan);
            Equal(BaseClocks[titan - 1], descriptor.BaseSpawnSeconds, "base clock T" + titan);
            True(!string.IsNullOrEmpty(descriptor.Name), "Titan name is present T" + titan);
            Equal(BaseClocks[titan - 1], TitanMechanics.SpawnSeconds(titan, 0, 0, 0),
                "unreduced clock T" + titan);
            Equal(0, TitanMechanics.SecondsUntilReady(titan, BaseClocks[titan - 1], 0, 0, 0),
                "at-due boundary T" + titan);
            Equal(1, TitanMechanics.SecondsUntilReady(titan, BaseClocks[titan - 1] - .01, 0, 0, 0),
                "one fractional second before due T" + titan);
            Equal(3600, TitanMechanics.SpawnSeconds(titan, 100, 100, 100),
                "one-hour floor T" + titan);
            Equal(Math.Max(3600, BaseClocks[titan - 1] - (titan >= 3 ? 900 : 0)),
                TitanMechanics.SpawnSeconds(titan, 1, 0, 0),
                "Normal No-Rebirth applicability T" + titan);
            Equal(Math.Max(3600, BaseClocks[titan - 1] - (titan >= 7 ? 900 : 0)),
                TitanMechanics.SpawnSeconds(titan, 0, 1, 0),
                "Evil No-Rebirth applicability T" + titan);
            Equal(Math.Max(3600, BaseClocks[titan - 1] - (titan >= 10 ? 900 : 0)),
                TitanMechanics.SpawnSeconds(titan, 0, 0, 1),
                "Sadistic No-Rebirth applicability T" + titan);
        }

        var paused = TitanMechanics.EvaluateClock(5, 100.0, 0, 0, 0, 0, 1);
        True(paused.Paused && !paused.HasWallClockEta && paused.WallClockEtaSeconds == -1.0,
            "T5 paused phase has arithmetic remainder but no wall ETA");
        True(paused.ArithmeticRemainingSeconds > 0 && paused.PauseReason.Contains("Walderp"),
            "paused projection retains audited arithmetic and reason");
        False(TitanMechanics.IsWaldoClockPaused(0, 0), "equal find/defeat advances");
        True(TitanMechanics.IsWaldoClockPaused(0, 1), "defeat ahead of find pauses");
        True(TitanMechanics.IsWaldoClockPaused(3, 4), "fourth defeated phase can pause before find four");
        False(TitanMechanics.IsWaldoClockPaused(4, 5), "find four permanently resumes clock");
        var duePaused = TitanMechanics.EvaluateClock(5, 10800, 0, 0, 0, 0, 1);
        True(duePaused.Due && !duePaused.Paused && duePaused.HasWallClockEta,
            "already-due clock is not presented as paused");
    }

    private static void TestUnlockAndTerminalState()
    {
        var all = new[] {true, true, true, true, true, true, true};
        for (var titan = 1; titan <= 14; titan++)
        {
            var rat = titan == 14;
            True(TitanMechanics.IsUnlocked(titan, Gates[titan - 1], all, true, rat),
                "exact gate unlocks T" + titan);
            False(TitanMechanics.IsUnlocked(titan, Gates[titan - 1] - 1, all, true, rat),
                "one boss below gate holds T" + titan);
            True(TitanMechanics.IsReachable(titan, Zones[titan - 1]),
                "exact highest zone reaches T" + titan);
            False(TitanMechanics.IsReachable(titan, Zones[titan - 1] - 1),
                "one zone below does not reach T" + titan);
        }

        var none = new bool[7];
        False(TitanMechanics.IsUnlocked(6, 999, none, true, false),
            "effective boss gate cannot replace T6 quest flag");
        for (var titan = 6; titan <= 12; titan++)
        {
            var flags = new[] {true, true, true, true, true, true, true};
            flags[titan - 6] = false;
            False(TitanMechanics.IsUnlocked(titan, 999, flags, true, false),
                "each versioned Titan reads its own unlock flag T" + titan);
        }
        False(TitanMechanics.IsUnlocked(4, 999, all, false, false),
            "T4 requires persistent Apathy MAXX unlock");
        False(TitanMechanics.IsUnlocked(14, 999, all, true, false),
            "T14 requires rat completion");

        True(TitanMechanics.IsRewardActionable(13, false, false, false),
            "T13 is useful before rat flag");
        False(TitanMechanics.IsRewardActionable(13, true, false, false),
            "T13 stops after rat flag");
        True(TitanMechanics.IsRewardActionable(14, true, true, false),
            "T14 flag-only failed delivery remains retry-actionable");
        False(TitanMechanics.IsRewardActionable(14, true, false, true),
            "ordinary item 495, not final flag, completes T14 reward");
    }

    private static void TestEnemyTypesAndIndexes()
    {
        var firstTypes = new[] {2, 3, 4, 5};
        for (var i = 0; i < firstTypes.Length; i++)
            True(TitanMechanics.IsTitanEnemyType(i + 1, firstTypes[i]),
                "exact early bigBoss type T" + (i + 1));
        for (var type = 6; type <= 10; type++)
            True(TitanMechanics.IsTitanEnemyType(5, type), "Walderp phase type " + type);
        True(TitanMechanics.IsTitanEnemyType(13, 46), "T13 finalBoss type");
        True(TitanMechanics.IsTitanEnemyType(14, 47), "T14 finalfinalboss type");
        False(TitanMechanics.IsTitanEnemyType(13, 1), "ordinary boss is not T13");

        var expectedTypes = new[]
        {
            new[] {13,15,16,17}, new[] {18,19,20,21}, new[] {23,24,25,26},
            new[] {28,29,30,31}, new[] {33,34,35,36}, new[] {37,38,39,40},
            new[] {42,43,44,45}
        };
        for (var titan = 6; titan <= 12; titan++)
        for (var version = 0; version < 4; version++)
        {
            Equal(expectedTypes[titan - 6][version],
                TitanMechanics.EnemyTypeForVersion(titan, version),
                "exact version enemy type T" + titan + "v" + (version + 1));
            var expectedIndex = titan <= 10 ? version + 1 : version;
            Equal(expectedIndex, TitanMechanics.EnemyIndexForVersion(titan, version),
                "exact native enemy list index T" + titan + "v" + (version + 1));
        }
    }

    private static void TestAutokillThresholds()
    {
        for (var titan = 6; titan <= 12; titan++)
        for (var version = 0; version < 4; version++)
        {
            var seed = TitanMechanics.EvaluateNativeAutokill(titan, version,
                double.MaxValue, double.MaxValue, double.MaxValue, 0);
            Equal((double)AutokillGoldens[titan - 6, version, 0], seed.RequiredAttack,
                "source-golden attack threshold T" + titan + "v" + (version + 1));
            Equal((double)AutokillGoldens[titan - 6, version, 1], seed.RequiredDefense,
                "source-golden defense threshold T" + titan + "v" + (version + 1));
            Equal((double)AutokillGoldens[titan - 6, version, 2], seed.RequiredHpRegen,
                "source-golden regen threshold T" + titan + "v" + (version + 1));
            var exact = TitanMechanics.EvaluateNativeAutokill(titan, version,
                seed.RequiredAttack, seed.RequiredDefense, seed.RequiredHpRegen, 0);
            True(exact.ViaStats && exact.Achieved,
                "inclusive native threshold T" + titan + "v" + (version + 1));
            var belowAttack = TitanMechanics.EvaluateNativeAutokill(titan, version,
                PreviousFloat(seed.RequiredAttack), seed.RequiredDefense,
                seed.RequiredHpRegen, 0);
            False(belowAttack.Achieved,
                "one float below attack holds T" + titan + "v" + (version + 1));
            var belowRegen = TitanMechanics.EvaluateNativeAutokill(titan, version,
                seed.RequiredAttack, seed.RequiredDefense,
                PreviousFloat(seed.RequiredHpRegen), 0);
            False(belowRegen.Achieved,
                "candidate HP regen participates T" + titan + "v" + (version + 1));
        }

        False(TitanMechanics.EvaluateNativeAutokill(9, 0, 0, 0, 0, 23).ViaBestiary,
            "installed T9 source does not shortcut at 23 kills");
        True(TitanMechanics.EvaluateNativeAutokill(9, 0, 0, 0, 0, 24).ViaBestiary,
            "installed T9 source shortcuts at 24 kills");
        False(TitanMechanics.EvaluateNativeAutokill(10, 0, 0, 0, 0, 4).ViaBestiary,
            "T10 four kills is below shortcut");
        True(TitanMechanics.EvaluateNativeAutokill(10, 0, 0, 0, 0, 5).ViaBestiary,
            "T10 five kills shortcuts");
        Equal(3, TitanMechanics.HighestNativeAutokillVersion(12, 0, 0, 0,
            new[] {0, 0, 0, 5}), "highest candidate AK version uses per-version kills");
    }

    private static void TestManualPrerequisites()
    {
        False(TitanMechanics.EvaluateManualPrerequisites(4, 0, false, 0, 1).Ready,
            "manual UUG requires equipped Apathy");
        True(TitanMechanics.EvaluateManualPrerequisites(4, 0, true, 0, 1).Ready,
            "manual UUG accepts equipped Apathy");
        True(TitanMechanics.EvaluateManualPrerequisites(12, 2, false, 0, 1).Ready,
            "manual T12 v3 does not require Apathy");
        False(TitanMechanics.EvaluateManualPrerequisites(12, 3, false, 0, 1).Ready,
            "manual T12 v4 requires Apathy");
        Equal(1, TitanMechanics.EvaluateManualPrerequisites(10, 0, false, 1, 5)
            .RequiredGlopCopies, "five enemy actions consume one Glop copy");
        Equal(2, TitanMechanics.EvaluateManualPrerequisites(10, 0, false, 2, 6)
            .RequiredGlopCopies, "six enemy actions require two Glop copies");
        False(TitanMechanics.EvaluateManualPrerequisites(10, 0, false, 1, 6).Ready,
            "one Glop cannot admit six projected actions");
    }

    private static void TestCumulativeTitan12()
    {
        var expected = new[] {483, 489, 493, 484};
        for (var version = 1; version <= 4; version++)
        {
            var drops = TitanMechanics.Titan12EndItemsForVersion(version);
            Equal(version, drops.Length, "T12 cumulative count v" + version);
            for (var i = 0; i < drops.Length; i++)
                Equal(expected[i], drops[i], "T12 cumulative order v" + version);
        }
        Equal(4, TitanMechanics.HighestUsefulTitan12Version(4, new int[0]),
            "all missing chooses v4");
        Equal(3, TitanMechanics.HighestUsefulTitan12Version(4, new[] {484}),
            "owning v4-only piece chooses highest remaining provenance");
        Equal(1, TitanMechanics.HighestUsefulTitan12Version(4, new[] {489, 493, 484}),
            "only 483 missing chooses v1");
        Equal(3, TitanMechanics.HighestUsefulTitan12Version(3, new[] {483, 489}),
            "combat bound selects highest safe useful version");
        Equal(-1, TitanMechanics.HighestUsefulTitan12Version(4, expected),
            "all ordinary-owned is complete");
    }

    private static void TestClockTransforms()
    {
        var values = new double[14];
        for (var i = 0; i < values.Length; i++) values[i] = i + 1;
        var snapshot = new TitanClockSnapshot(values);
        var killed = TitanMechanics.ApplyTitanKill(snapshot, 12);
        Equal(0.0, killed.ElapsedSeconds(12), "kill resets exactly its Titan clock");
        Equal(11.0, killed.ElapsedSeconds(11), "neighboring clock survives Titan kill");
        Equal(12.0, snapshot.ElapsedSeconds(12), "source snapshot remains immutable");
        var reset = TitanMechanics.ApplyOrdinaryRebirth(snapshot);
        for (var titan = 1; titan <= 14; titan++)
            Equal(0.0, reset.ElapsedSeconds(titan), "ordinary rebirth resets T" + titan);
    }

    private static float PreviousFloat(float value)
    {
        var bytes = BitConverter.GetBytes(value);
        var bits = BitConverter.ToInt32(bytes, 0);
        return BitConverter.ToSingle(BitConverter.GetBytes(bits - 1), 0);
    }

    private static void True(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static void False(bool condition, string message)
    {
        True(!condition, message);
    }

    private static void Equal(int expected, int actual, string message)
    {
        _assertions++;
        if (expected != actual)
            throw new Exception("FAIL: " + message + "; expected " + expected + ", actual " + actual);
    }

    private static void Equal(double expected, double actual, string message)
    {
        _assertions++;
        if (expected != actual)
            throw new Exception("FAIL: " + message + "; expected " + expected + ", actual " + actual);
    }
}
