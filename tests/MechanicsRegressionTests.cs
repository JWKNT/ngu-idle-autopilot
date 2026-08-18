/*
FILE PURPOSE

Purpose: This standalone executable is the golden regression suite for the pure NGU Idle mechanics,
Titan clock, reset-state, stochastic, and END dependency oracles.  It catches boundary drift before
a planner change can turn an exact discontinuity into a moving heuristic or unsafe reset forecast.

Mechanism: A tiny dependency-free test runner invokes the same source files compiled into the bot,
checks fixed vectors and invalid-input behavior, prints one PASS/FAIL line per group, and exits
nonzero on any failure.  It does not load Assembly-CSharp, Unity, the bot DLL, or a save.

Inputs and outputs: There are no runtime inputs.  Output is console-only and the process exit code is
zero on success or one on failure.  The companion test-mechanics.command compiles this executable
into build/tests and runs it through the existing CrossOver toolchain.

Invariants and safety: Tests are read-only and must never inspect or mutate runtime/, save backups,
configuration, a game process, or injected assemblies.  Golden values use installed-build source
semantics, including the Basic Training leading one and T12-T14's 27,000-second base clock.

Extension points and non-goals: Every new pure mechanic or reset classification needs a focused
boundary vector here.  Live differential tests belong in a separately authorized read-only harness;
this executable is intentionally deterministic and offline.
*/
using System;
using System.Collections.Generic;
using NGUInjector.Autopilot;

internal static class MechanicsRegressionTests
{
    private static int _failures;
    private static int _assertions;

    private static int Main()
    {
        Run("50 Hz cadence and time AP", TestCadenceAndTimeAp);
        Run("Basic Training cap compression", TestBasicTraining);
        Run("Fight Boss discrete tick order", TestFightBoss);
        Run("Wish arithmetic and split scaling", TestWishes);
        Run("ITOPOD progress and first-clear awards", TestItopod);
        Run("Titan clock table and reset transitions", TestTitanClocks);
        Run("reset-state registry and scalar transforms", TestResetRegistry);
        Run("END dependency and placement registry", TestEndgame);
        Run("stochastic and coupon estimates", TestStochastic);

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "PASS: " + _assertions + " mechanics assertions"
            : "FAIL: " + _failures + " test group(s), " + _assertions + " assertions");
        return _failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine("FAIL " + name + ": " + ex.Message);
        }
    }

    private static void TestCadenceAndTimeAp()
    {
        Equal(50, MechanicsCadence.TicksPerSecond, "ticks per second");
        Equal(0L, MechanicsCadence.CompletedTicks(0.019), "no completed tick before 0.02s");
        Equal(1L, MechanicsCadence.CompletedTicks(0.02), "first completed tick at 0.02s");
        Equal(1L, MechanicsCadence.TicksNeeded(0.001), "positive duration needs a tick");
        Equal(2L, MechanicsCadence.TicksNeeded(0.021), "duration rounds up to next tick");
        Near(0.04, MechanicsCadence.QuantizeDurationUp(0.021), 1e-12, "duration quantization");
        Near(1.0, MechanicsCadence.SecondsForTicks(50), 1e-12, "ticks to seconds");

        Equal(0L, MechanicsProgression.TimeAp(3599.999), "before one hour");
        Equal(0L, MechanicsProgression.TimeAp(3600.0), "one hour is not an award");
        Equal(0L, MechanicsProgression.TimeAp(4099.999), "before first AP boundary");
        Equal(1L, MechanicsProgression.TimeAp(4100.0), "first AP at 4100");
        Equal(1L, MechanicsProgression.TimeAp(4599.999), "between AP boundaries");
        Equal(2L, MechanicsProgression.TimeAp(4600.0), "second AP at 4600");
        Equal(3L, MechanicsProgression.TimeAp(5100.9), "time AP floors whole seconds");
    }

    private static void TestBasicTraining()
    {
        var zero = MechanicsProgression.BasicTrainingCap(0L, 100L, 0);
        Equal(1L, zero.RawReduction, "native leading one at zero levels");
        Equal(1L, zero.Reduction, "minimum cap reduction");
        Equal(99L, zero.NewCap, "zero-level run still compresses cap by one");

        var shifted = MechanicsProgression.BasicTrainingCap(1000L, 1000L, 1);
        Equal(4L, shifted.RawReduction, "tier shift and 1.2 exponent");
        Equal(4L, shifted.Reduction, "unclamped reduction");
        Equal(996L, shifted.NewCap, "unclamped new cap");

        var maximum = MechanicsProgression.BasicTrainingCap(100000L, 10L, 0);
        Equal(2L, maximum.Reduction, "ten-percent-plus-one maximum");
        Equal(8L, maximum.NewCap, "maximum reduction result");

        var floor = MechanicsProgression.BasicTrainingCap(100000L, 1L, 0);
        Equal(1L, floor.NewCap, "cap never falls below one");
        Equal(0L, MechanicsProgression.BasicTrainingLevelForReduction(100L, 0, 1L),
            "minimum reduction already exists at zero levels");
        var maximumLevel = MechanicsProgression.BasicTrainingLevelForMaximumReduction(10L, 0);
        Equal(2L, MechanicsProgression.BasicTrainingCap(maximumLevel, 10L, 0).Reduction,
            "threshold reaches maximum reduction");
        if (maximumLevel > 0L)
            True(MechanicsProgression.BasicTrainingCap(maximumLevel - 1L, 10L, 0).Reduction < 2L,
                "threshold is the first maximum-reduction level");
        Throws<ArgumentOutOfRangeException>(delegate
        {
            MechanicsProgression.BasicTrainingCap(-1L, 100L, 0);
        }, "negative Training level rejected");
    }

    private static void TestFightBoss()
    {
        var oneHit = MechanicsFightBoss.Evaluate(100, 100, 100,
            0, 0, 1, 1, 0);
        True(oneHit.PlayerWins, "unopposed first-tick kill wins");
        Equal(1L, oneHit.KillTick, "one-hit kill tick");
        Near(0.02, oneHit.KillSeconds, 1e-12, "one-hit kill seconds");

        var simultaneous = MechanicsFightBoss.Evaluate(100, 0, 1,
            100, 0, 1, 1, 0);
        False(simultaneous.PlayerWins, "same-tick player death resolves before outgoing hit");
        Equal(1L, simultaneous.KillTick, "same-tick boss lethal tick");
        Equal(1L, simultaneous.DeathTick, "same-tick player lethal tick");

        var regenWall = MechanicsFightBoss.Evaluate(10, 100, 100,
            0, 0, 100, 100, 1);
        False(regenWall.PlayerWins, "Boss regen at least outgoing damage prevents progress");
        Equal(long.MaxValue, regenWall.KillTick, "regen wall has no finite kill");

        var playerSustain = MechanicsFightBoss.Evaluate(100, 1000, 100,
            1001, 0, 100, 100, 0);
        True(playerSustain.PlayerWins, "post-hit player regen can sustain nonlethal damage");
        Equal(long.MaxValue, playerSustain.DeathTick, "sustained player has no finite death");

        var bossCap = MechanicsFightBoss.Evaluate(100, 100, 100,
            0, 0, 9, 10, 1);
        Equal(9L, bossCap.KillTick, "Boss regen is capped before the first hit");
        Throws<ArgumentOutOfRangeException>(delegate
        {
            MechanicsFightBoss.Evaluate(double.NaN, 0, 1, 0, 0, 1, 1, 0);
        }, "non-finite Fight Boss state rejected");
    }

    private static void TestWishes()
    {
        Near(1.0, MechanicsWish.RawProgressPerTick(1, 1, 1, 1, 1, 1, 1, 1, 0),
            1e-12, "normalized Wish rate");
        Near(0.25, MechanicsWish.RawProgressPerTick(1, 1, 1, 1, 1, 1, 1, 1, 3),
            1e-12, "Wish level plus one divider");
        Equal(0.0, MechanicsWish.RawProgressPerTick(1, 1, 1, 1, 1, 0, 1, 1, 0),
            "all three resources must be positive");

        Equal(14400, MechanicsWish.MinimumSeconds(0, 0, 0), "base Wish minimum");
        Equal(14328, MechanicsWish.MinimumSeconds(1, 1, 1), "three fixed 24-second reducers");
        Equal(14184, MechanicsWish.MinimumSeconds(2, 3, 4), "reducer levels add linearly");
        Near(1.0 / 14400.0 / 50.0, MechanicsWish.MaximumProgressPerTick(14400),
            1e-18, "minimum-time per-tick cap");
        Near(MechanicsWish.MaximumProgressPerTick(14400),
            MechanicsWish.CappedProgressPerTick(1.0, 14400), 1e-18, "raw rate capped");

        Near(Math.Pow(4.0, -0.51), MechanicsWish.EqualSplitPerWishRateScale(4),
            1e-12, "per-Wish n^-0.51 split");
        Near(Math.Pow(4.0, 0.49), MechanicsWish.EqualSplitAggregateRateScale(4),
            1e-12, "aggregate n^0.49 split");
        True(MechanicsWish.IsSinglePrecisionDurationSafe(666720.0), "precision boundary is allowed");
        False(MechanicsWish.IsSinglePrecisionDurationSafe(666720.001), "above precision boundary rejected");
    }

    private static void TestItopod()
    {
        Equal(203L, MechanicsItopod.OrdinaryProgressPerKill(
            ItopodDifficulty.Normal, 3, 1.0, 0), "Normal progress");
        Equal(703L, MechanicsItopod.OrdinaryProgressPerKill(
            ItopodDifficulty.Evil, 3, 1.0, 0), "Evil progress");
        Equal(2053L, MechanicsItopod.OrdinaryProgressPerKill(
            ItopodDifficulty.Sadistic, 3, 1.0, 50), "Sadistic progress with improved base PP");
        Equal(304L, MechanicsItopod.OrdinaryProgressPerKill(
            ItopodDifficulty.Normal, 3, 1.5, 0), "native positive truncation");

        Equal(0L, MechanicsItopod.FirstClearPerkPoints(9, true), "not divisible by ten");
        Equal(0L, MechanicsItopod.FirstClearPerkPoints(10, false), "not a new record");
        Equal(1L, MechanicsItopod.FirstClearPerkPoints(10, true), "floor 10 award");
        Equal(1L, MechanicsItopod.FirstClearPerkPoints(90, true), "floor 90 award");
        Equal(10L, MechanicsItopod.FirstClearPerkPoints(100, true), "floor 100 decade bonus");
        Equal(2L, MechanicsItopod.FirstClearPerkPoints(110, true), "floor 110 award");
        Equal(2L, MechanicsItopod.FirstClearPerkPoints(190, true), "floor 190 award");
        Equal(20L, MechanicsItopod.FirstClearPerkPoints(200, true), "floor 200 decade bonus");
        Equal(160L, MechanicsItopod.FirstClearPerkPoints(1600, true), "installed floor cap award");
        Equal(2L, MechanicsItopod.CompletedPerkPoints(2500000L), "whole PP conversion");
    }

    private static void TestTitanClocks()
    {
        var bases = new[]
            {3600, 3600, 7200, 7200, 10800, 12600, 16200, 18000, 19800, 23400, 25200, 27000, 27000, 27000};
        for (var titan = 1; titan <= 14; titan++)
            Equal(bases[titan - 1], TitanMechanics.SpawnSeconds(titan, 0, 0, 0),
                "base clock T" + titan);

        Equal(6300, TitanMechanics.SpawnSeconds(3, 1, 99, 99), "T3 uses Normal only");
        Equal(3600, TitanMechanics.SpawnSeconds(3, 4, 0, 0), "T3 floor");
        Equal(3600, TitanMechanics.SpawnSeconds(6, 10, 0, 0), "T6 floor");
        Equal(13500, TitanMechanics.SpawnSeconds(7, 1, 2, 99), "T7 uses Normal plus Evil");
        Equal(20700, TitanMechanics.SpawnSeconds(10, 1, 1, 1), "T10 uses all difficulties");
        Equal(3600, TitanMechanics.SpawnSeconds(12, 30, 30, 30), "T12 universal floor");
        Equal(1, TitanMechanics.SecondsUntilReady(7, 13499.1, 1, 2, 0),
            "fractional remaining time rounds up");
        True(TitanMechanics.IsReady(7, 13500.0, 1, 2, 0), "ready at exact boundary");

        var source = new double[14];
        for (var i = 0; i < source.Length; i++) source[i] = i + 1;
        var snapshot = new TitanClockSnapshot(source);
        var killed = TitanMechanics.ApplyTitanKill(snapshot, 7);
        Equal(7.0, snapshot.ElapsedSeconds(7), "Titan kill transform does not mutate input");
        Equal(0.0, killed.ElapsedSeconds(7), "killed Titan clock resets");
        Equal(8.0, killed.ElapsedSeconds(8), "other Titan clock remains");
        var reborn = TitanMechanics.ApplyOrdinaryRebirth(snapshot);
        for (var titan = 1; titan <= 14; titan++)
            Equal(0.0, reborn.ElapsedSeconds(titan), "ordinary rebirth resets T" + titan);
    }

    private static void TestResetRegistry()
    {
        var all = ResetStateRegistry.All();
        Equal(Enum.GetValues(typeof(ResetStateKey)).Length, all.Length, "one descriptor per key");
        var seenKeys = new HashSet<ResetStateKey>();
        var seenClasses = new HashSet<ResetStateClass>();
        for (var i = 0; i < all.Length; i++)
        {
            True(seenKeys.Add(all[i].Key), "unique reset key " + all[i].Key);
            seenClasses.Add(all[i].StateClass);
        }
        Equal(Enum.GetValues(typeof(ResetStateClass)).Length, seenClasses.Count,
            "all typed state classes represented");

        var wishProgress = ResetStateRegistry.Find(ResetStateKey.WishProgress);
        Equal(ResetStateClass.PersistentPartialProgress, wishProgress.StateClass,
            "Wish progress typed persistent partial");
        Equal(12.5, ResetTransforms.ApplyScalar(wishProgress,
            ResetTransitionKind.OrdinaryRebirth, 12.5, null), "Wish progress survives");
        Equal(12.5, ResetTransforms.ApplyOrdinaryRebirth(
            ResetStateKey.WishProgress, 12.5, null), "named ordinary transform delegates to registry");

        var wishAllocation = ResetStateRegistry.Find(ResetStateKey.WishAllocations);
        Equal(0.0, ResetTransforms.ApplyScalar(wishAllocation,
            ResetTransitionKind.OrdinaryRebirth, 100.0, null), "Wish allocations clear");

        var number = ResetStateRegistry.Find(ResetStateKey.CurrentNumber);
        Throws<InvalidOperationException>(delegate
        {
            ResetTransforms.ApplyScalar(number, ResetTransitionKind.OrdinaryRebirth, 100.0, null);
        }, "Number preview is mandatory");
        Equal(0.25, ResetTransforms.ApplyScalar(number,
            ResetTransitionKind.OrdinaryRebirth, 100.0, 0.25), "Number may legitimately decrease");

        var adventurePoints = ResetStateRegistry.Find(ResetStateKey.AdventurePoints);
        Throws<InvalidOperationException>(delegate
        {
            ResetTransforms.ApplyScalar(adventurePoints,
                ResetTransitionKind.OrdinaryRebirth, 10.0, null);
        }, "time AP award must be resolved");
        Equal(11.0, ResetTransforms.ApplyScalar(adventurePoints,
            ResetTransitionKind.OrdinaryRebirth, 10.0, 11.0), "resolved AP balance includes award");

        var trainingCaps = ResetStateRegistry.Find(ResetStateKey.BasicTrainingCaps);
        Throws<InvalidOperationException>(delegate
        {
            ResetTransforms.ApplyScalar(trainingCaps, ResetTransitionKind.OrdinaryRebirth, 100.0, null);
        }, "cap conversion must be resolved");
        Equal(99.0, ResetTransforms.ApplyScalar(trainingCaps,
            ResetTransitionKind.OrdinaryRebirth, 100.0, 99.0), "resolved cap conversion");

        var titanClocks = ResetStateRegistry.Find(ResetStateKey.TitanClocks);
        Throws<InvalidOperationException>(delegate
        {
            ResetTransforms.ApplyScalar(titanClocks, ResetTransitionKind.OrdinaryRebirth, 1.0, null);
        }, "clock arrays route to Titan mechanics");

        var advancedBank = ResetStateRegistry.Find(ResetStateKey.AdvancedTrainingBank);
        Equal(0.0, ResetTransforms.ApplyScalar(advancedBank,
            ResetTransitionKind.ChallengeEntry, 42.0, null), "challenge entry clears AT bank");
    }

    private static void TestEndgame()
    {
        var requirements = MechanicsEndgame.AllRequirements();
        Equal(16, requirements.Length, "sixteen END pieces");
        var slots = new[] {0, 1, 2, 3, 12, 13, 14, 15, 24, 25, 26, 27, 36, 37, 38, 39};
        for (var i = 0; i < requirements.Length; i++)
        {
            Equal(480 + i, requirements[i].ItemId, "END item sequence " + i);
            Equal(slots[i], requirements[i].TargetSlot, "END slot sequence " + i);
            Equal(480 + i, MechanicsEndgame.RequiredItemForSlot(slots[i]), "reverse slot map " + i);
        }
        False(MechanicsEndgame.IsProtectedItem(479), "item before END range not protected here");
        True(MechanicsEndgame.IsProtectedItem(480), "first END item protected");
        True(MechanicsEndgame.IsProtectedItem(495), "last END item protected");
        False(MechanicsEndgame.IsProtectedItem(496), "item after END range not protected here");
        Equal(1, MechanicsEndgame.FindByItemId(483).TitanVersion, "T12 v1 item");
        Equal(4, MechanicsEndgame.FindByItemId(484).TitanVersion, "T12 v4 item");
        Equal(2, MechanicsEndgame.FindByItemId(489).TitanVersion, "T12 v2 item");
        Equal(3, MechanicsEndgame.FindByItemId(493).TitanVersion, "T12 v3 item");

        var inventory = new int[40];
        for (var slot = 0; slot < inventory.Length; slot++) inventory[slot] = -1;
        for (var i = 0; i < requirements.Length; i++)
            inventory[requirements[i].TargetSlot] = requirements[i].ItemId;
        True(MechanicsEndgame.ValidatePlacement(inventory), "exact END placement accepted");
        inventory[25] = -1;
        False(MechanicsEndgame.ValidatePlacement(inventory), "missing T12 v2 rejected");
        var missing = MechanicsEndgame.MisplacedOrMissingItems(inventory);
        Equal(1, missing.Length, "one missing piece reported");
        Equal(489, missing[0], "correct missing piece reported");

        var gates = MechanicsEndgame.AllGates();
        Equal(295, gates[0].RequiredSadisticBoss, "T13 Boss gate");
        Equal(300, gates[1].RequiredSadisticBoss, "T14 Boss gate");
        True(gates[1].RequiresTitan13Defeated, "T14 needs T13 flag");
        True(gates[2].RequiresAllEndItemsPlaced, "final sequence needs placement");
    }

    private static void TestStochastic()
    {
        Near(4.0, MechanicsStochastic.GeometricMeanTrials(0.25), 1e-12, "geometric mean");
        Equal(3L, MechanicsStochastic.GeometricMedianTrials(0.25), "geometric median ceiling");
        Near(0.578125, MechanicsStochastic.ProbabilityAtLeastOne(3, 0.25),
            1e-12, "geometric CDF");
        Equal(1L, MechanicsStochastic.GeometricMedianTrials(1.0), "certain event median");
        Equal(0L, MechanicsStochastic.GeometricQuantileTrials(0.25, 0.0), "zero confidence needs no trials");
        Equal(long.MaxValue, MechanicsStochastic.GeometricQuantileTrials(0.0, 0.5),
            "impossible quantile sentinel");
        Near(1000.0, MechanicsStochastic.ExpectedTrialsForCopies(100, 0.1),
            1e-9, "negative-binomial copy mean");
        Equal(101, MechanicsStochastic.TotalLevelZeroCopiesForFreshMax(), "fresh MAXX total copies");
        Equal(100, MechanicsStochastic.AdditionalLevelZeroCopiesToMax(0), "fresh held target sources");
        Equal(0, MechanicsStochastic.AdditionalLevelZeroCopiesToMax(100), "MAXX needs no copies");
        Near(5.5, MechanicsStochastic.CouponCollectorMeanUsefulDrops(3),
            1e-12, "three-type coupon mean");
        Near(11.0, MechanicsStochastic.CouponCollectorMeanTrialsUniform(3, 0.5),
            1e-12, "coupon trials with useful-drop chance");
        Throws<ArgumentOutOfRangeException>(delegate
        {
            MechanicsStochastic.GeometricQuantileTrials(0.5, 1.0);
        }, "certainty rejected for non-certain quantile");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        _assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception(message + ": expected " + expected + ", got " + actual);
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        _assertions++;
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            throw new Exception(message + ": expected " + expected + " +/- " + tolerance + ", got " + actual);
    }

    private static void True(bool value, string message)
    {
        _assertions++;
        if (!value) throw new Exception(message + ": expected true");
    }

    private static void False(bool value, string message)
    {
        _assertions++;
        if (value) throw new Exception(message + ": expected false");
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        _assertions++;
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new Exception(message + ": expected " + typeof(T).Name + ", got " + ex.GetType().Name);
        }
        throw new Exception(message + ": expected " + typeof(T).Name + ", no exception was thrown");
    }
}
