/*
FILE PURPOSE

Purpose: Dependency-free boundary tests for the source-order Fight Boss tick and explicit recovery
projection.  These tests protect the exact player-death-before-hit rule and prevent candidate max-HP
changes from being treated as healing.

Mechanism: This executable compiles beside MechanicsOracle.cs, invokes only pure scalar APIs, and
uses fixed installed-build vectors.  It never loads Unity, Assembly-CSharp, a save, or runtime state.

Inputs and outputs: There are no external inputs.  PASS/FAIL diagnostics are written to stdout and a
nonzero exit code identifies a regression.

Invariants and safety: Combat admission is bounded to 6,000 source-order ticks.  A horizon result is
not a win; pre-fight recovery is reported only by EvaluateRecovery and never applied by Evaluate.
*/
using System;
using NGUInjector.Autopilot;

internal static class FightBossOracleTests
{
    private static int _assertions;
    private static int _failures;

    private static int Main()
    {
        Run("native source order and strict lethal tie", TestNativeOrder);
        Run("bounded horizon and regeneration caps", TestBoundsAndCaps);
        Run("current HP and explicit recovery semantics", TestCurrentHpAndRecovery);
        Run("input contracts", TestInputs);
        Console.WriteLine(_failures == 0
            ? "PASS: " + _assertions + " Fight Boss oracle assertions"
            : "FAIL: " + _failures + " group(s), " + _assertions + " assertions");
        return _failures == 0 ? 0 : 1;
    }

    private static void TestNativeOrder()
    {
        var tie = MechanicsFightBoss.Evaluate(100.0, 0.0, 1.0,
            100.0, 0.0, 1.0, 1.0, 0.0);
        False(tie.PlayerWins, "same-tick lethal incoming damage loses");
        Equal(1L, tie.DeathTick, "player dies on tick one");
        Equal(1L, tie.KillTick, "counterfactual boss-only lethal tick is retained for compatibility");
        Equal(1.0, tie.BossHpAtEnd, "boss HP is unchanged on lethal incoming tick");

        // At this neighboring-double boundary, .02 * (attack - defense) is positive while
        // native attack*.02 - defense*.02 rounds to zero.  The oracle must use native order.
        const double defense = 2.1622218340346888e229;
        const double attack = 2.162221834034689e229;
        True(0.02 * (attack - defense) > 0.0, "reassociated expression would claim damage");
        var nativeBoundary = MechanicsFightBoss.Evaluate(attack, 0.0, 10.0,
            0.0, defense, 1.0, 1.0, 0.0);
        Equal(0.0, nativeBoundary.OutgoingDamagePerTick,
            "native multiply-then-subtract rounds to zero");
        False(nativeBoundary.PlayerWins, "zero native damage cannot win");
        True(nativeBoundary.HorizonReached, "zero-damage fight reaches the bounded horizon");
    }

    private static void TestBoundsAndCaps()
    {
        var cap = MechanicsFightBoss.Evaluate(100.0, 100.0, 100.0, 100.0,
            0.0, 0.0, 9.0, 10.0, 1.0,
            MechanicsFightBoss.DefaultCombatHorizonTicks);
        True(cap.PlayerWins, "capped boss regeneration remains beatable");
        Equal(9L, cap.KillTick, "boss regeneration caps before each outgoing hit");

        var longFight = MechanicsFightBoss.Evaluate(1.0, 1.0, 10.0, 10.0,
            0.0, 0.0, 200.0, 200.0, 0.0,
            MechanicsFightBoss.DefaultCombatHorizonTicks);
        False(longFight.PlayerWins, "fight beyond 120 seconds is not admitted");
        True(longFight.HorizonReached, "unfinished fight is explicitly horizon-bounded");
        Equal(MechanicsFightBoss.DefaultCombatHorizonTicks, longFight.TicksSimulated,
            "exactly 6,000 ticks are simulated");
        Equal(long.MaxValue, longFight.KillTick, "horizon is not exposed as an estimated kill");

        var sustained = MechanicsFightBoss.Evaluate(100.0, 1000.0, 5.0, 5.0,
            1001.0, 0.0, 100.0, 100.0, 0.0,
            MechanicsFightBoss.DefaultCombatHorizonTicks);
        True(sustained.PlayerWins, "post-first-hit regen sustains the player");
        True(sustained.PlayerHpAtEnd <= 5.0, "in-fight regen never exceeds candidate max HP");
    }

    private static void TestCurrentHpAndRecovery()
    {
        Equal(10.0, MechanicsFightBoss.CurrentHpAfterMaxChange(10.0, 100.0),
            "raising max HP does not heal");
        Equal(50.0, MechanicsFightBoss.CurrentHpAfterMaxChange(100.0, 50.0),
            "lowering max HP clamps live current HP");

        var clamped = MechanicsFightBoss.Evaluate(100.0, 9.0, 100.0, 50.0,
            0.0, 0.0, 1.0, 1.0, 0.0,
            MechanicsFightBoss.DefaultCombatHorizonTicks);
        Equal(50.0, clamped.PlayerStartHp, "Evaluate exposes current HP after max-change clamp");

        var recovery = MechanicsFightBoss.EvaluateRecovery(100.0, 9.0,
            90.0, 100.0,
            100.0, 0.0, 100.0, 100.0, 0.0,
            MechanicsFightBoss.DefaultCombatHorizonTicks, 120L);
        Equal(90.0, recovery.CurrentHpAfterSwap,
            "candidate max increase preserves the live current numerator");
        False(recovery.Immediate.PlayerWins, "no implicit recovery is applied");
        True(recovery.CanWinAtFullHp, "full candidate HP can win");
        True(recovery.RecoveryWithinHorizon, "explicit native recovery reaches a winning start");
        True(recovery.RecoveryTicks > 0L, "recovery has a positive wait");
        Equal(MechanicsCadence.SecondsForTicks(recovery.RecoveryTicks), recovery.RecoverySeconds,
            "recovery ETA is tick-quantized");
        True(recovery.AfterRecovery.PlayerWins, "reported recovery endpoint wins");
        True(recovery.AfterRecovery.PlayerStartHp >= recovery.RequiredStartHp,
            "recovered HP meets the first winning representable start");

        var previousHp = MechanicsFightBoss.RecoverHp(recovery.CurrentHpAfterSwap,
            recovery.CandidateMaxHp, 9.0, recovery.RecoveryTicks - 1L);
        var previousFight = MechanicsFightBoss.Evaluate(100.0, 9.0,
            previousHp, recovery.CandidateMaxHp,
            100.0, 0.0, 100.0, 100.0, 0.0,
            MechanicsFightBoss.DefaultCombatHorizonTicks);
        False(previousFight.PlayerWins, "one fewer recovery tick is insufficient");

        var tooShort = MechanicsFightBoss.EvaluateRecovery(100.0, 9.0,
            90.0, 100.0,
            100.0, 0.0, 100.0, 100.0, 0.0,
            MechanicsFightBoss.DefaultCombatHorizonTicks, 1L);
        True(tooShort.CanWinAtFullHp, "full-HP viability is distinct from recovery horizon");
        False(tooShort.RecoveryWithinHorizon, "insufficient recovery horizon is explicit");
        True(double.IsPositiveInfinity(tooShort.RecoverySeconds),
            "out-of-horizon recovery is not presented as a finite ETA");
    }

    private static void TestInputs()
    {
        Throws<ArgumentOutOfRangeException>(delegate
        {
            MechanicsFightBoss.Evaluate(double.NaN, 0.0, 1.0,
                0.0, 0.0, 1.0, 1.0, 0.0);
        }, "non-finite state rejected");
        Throws<ArgumentOutOfRangeException>(delegate
        {
            MechanicsFightBoss.Evaluate(1.0, 1.0, 1.0, 1.0,
                0.0, 0.0, 1.0, 1.0, 0.0,
                MechanicsFightBoss.DefaultCombatHorizonTicks + 1L);
        }, "unbounded combat request rejected");
        Throws<ArgumentOutOfRangeException>(delegate
        {
            MechanicsFightBoss.RecoverHp(1.0, 1.0, 1.0, -1L);
        }, "negative recovery horizon rejected");
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

    private static void True(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception(message);
    }

    private static void False(bool condition, string message)
    {
        True(!condition, message);
    }

    private static void Equal(long expected, long actual, string message)
    {
        _assertions++;
        if (expected != actual)
            throw new Exception(message + ": expected " + expected + ", actual " + actual);
    }

    private static void Equal(double expected, double actual, string message)
    {
        _assertions++;
        if (expected != actual)
            throw new Exception(message + ": expected " + expected + ", actual " + actual);
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
        throw new Exception(message + ": expected " + typeof(T).Name);
    }
}
