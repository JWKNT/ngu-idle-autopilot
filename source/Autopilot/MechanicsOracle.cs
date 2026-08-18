/*
FILE PURPOSE

Purpose: This file is the dependency-free mechanics oracle for deterministic NGU Idle arithmetic
that is shared by routing, rebirth, allocation, and forecasting policy.  It owns the native 50 Hz
cadence, Fight Boss tick order, time-AP boundaries, Basic Training cap compression, Wish rate/cap
arithmetic, and ITOPOD progress awards extracted from the installed 1.260 game assembly.

Mechanism: Callers pass already-read scalar state into pure functions.  No function reads Character,
Unity state, settings, files, clocks, or telemetry, and no function mutates the game.  Integer floors,
fixed-second reductions, and discontinuities are deliberately visible instead of being smoothed into
heuristics.

Inputs and outputs: Inputs are primitive snapshots such as elapsed seconds, Training level/cap,
Wish resources, difficulty, and PP bonus.  Outputs are scalar forecasts or immutable result values
that can be compared with read-only native controller values and used in offline regression tests.

Invariants and safety: Invalid probabilities, negative levels, nonpositive dividers, and invalid
Titan-like identifiers must never silently become a plausible progression estimate.  Boundary
semantics mirror native ordering: the first time AP is at 4,100 seconds, Basic Training subtracts at
least one cap even at zero run levels, Wish cost includes current level plus one, and ITOPOD
first-clear PP exists only for a new record divisible by ten.

Extension points and non-goals: Add source-verified deterministic formulas here when they can remain
free of game references.  Strategy weights, action selection, controller mutation, telemetry, and
live differential sampling belong elsewhere.  Titan clocks, reset metadata, END dependencies, and
stochastic estimates live in their dedicated pure files.
*/
using System;

namespace NGUInjector.Autopilot
{
    internal static class MechanicsCadence
    {
        internal const int TicksPerSecond = 50;
        internal const double SecondsPerTick = 1.0 / TicksPerSecond;

        internal static long CompletedTicks(double elapsedSeconds)
        {
            if (double.IsNaN(elapsedSeconds) || elapsedSeconds <= 0.0) return 0L;
            if (double.IsPositiveInfinity(elapsedSeconds)) return long.MaxValue;
            var ticks = Math.Floor(elapsedSeconds * TicksPerSecond);
            return ticks >= long.MaxValue ? long.MaxValue : (long)ticks;
        }

        internal static long TicksNeeded(double durationSeconds)
        {
            if (double.IsNaN(durationSeconds) || durationSeconds <= 0.0) return 0L;
            if (double.IsPositiveInfinity(durationSeconds)) return long.MaxValue;
            var ticks = Math.Ceiling(durationSeconds * TicksPerSecond);
            return ticks >= long.MaxValue ? long.MaxValue : (long)ticks;
        }

        internal static double SecondsForTicks(long ticks)
        {
            if (ticks < 0L) throw new ArgumentOutOfRangeException("ticks");
            return ticks / (double)TicksPerSecond;
        }

        internal static double QuantizeDurationUp(double durationSeconds)
        {
            return SecondsForTicks(TicksNeeded(durationSeconds));
        }
    }

    internal sealed class BasicTrainingCapResult
    {
        internal long RawReduction;
        internal long Reduction;
        internal long NewCap;
    }

    internal static class MechanicsProgression
    {
        internal const int TimeApStartSeconds = 3600;
        internal const int TimeApFirstAwardSeconds = 4100;
        internal const int TimeApIntervalSeconds = 500;

        /*
        SOURCE-EXACT DISCONTINUITIES

        Time AP floors the displayed run age before subtracting the one-hour origin.  Basic Training
        uses `1 + Pow(...)` before integer conversion, then clamps the reduction to `cap/10 + 1` and
        the resulting cap to one.  The leading one is present in Rebirth.resetTraining in the
        installed assembly even though abbreviated audit prose may show only floor(Pow(...)).
        */
        internal static long TimeAp(double rebirthSeconds)
        {
            if (double.IsNaN(rebirthSeconds) || rebirthSeconds < TimeApFirstAwardSeconds) return 0L;
            if (double.IsPositiveInfinity(rebirthSeconds)) return long.MaxValue;
            var wholeSeconds = Math.Floor(rebirthSeconds);
            var awards = Math.Floor(Math.Max(0.0, wholeSeconds - TimeApStartSeconds)
                                    / TimeApIntervalSeconds);
            return awards >= long.MaxValue ? long.MaxValue : (long)awards;
        }

        internal static BasicTrainingCapResult BasicTrainingCap(long trainingLevel, long oldCap, int tier)
        {
            if (trainingLevel < 0L) throw new ArgumentOutOfRangeException("trainingLevel");
            if (oldCap < 1L) throw new ArgumentOutOfRangeException("oldCap");
            if (tier < 0 || tier > 5) throw new ArgumentOutOfRangeException("tier");

            // Rebirth.resetTraining performs this pipeline as float32 (Mathf.Pow and conv.r4).
            // Round each stage back to float so threshold scans agree with native precision rather
            // than an idealized double formula. Negative pre-tier offsets clamp to the same final
            // minimum-one result that native obtains after its reduction guards.
            var shifted = Math.Max(0.0f, (float)trainingLevel - 500.0f * tier);
            var powered = (float)Math.Pow(shifted, 1.2f);
            var scaled = 1.0f + powered / 500.0f * ((float)oldCap / 1000.0f);
            var raw = float.IsPositiveInfinity(scaled) ? long.MaxValue : (long)scaled;
            var maximum = oldCap / 10L + 1L;
            var reduction = Math.Max(1L, Math.Min(maximum, raw));
            var newCap = Math.Max(1L, oldCap - reduction);
            return new BasicTrainingCapResult
            {
                RawReduction = raw,
                Reduction = reduction,
                NewCap = newCap
            };
        }

        internal static long BasicTrainingLevelForReduction(long oldCap, int tier, long targetReduction)
        {
            if (oldCap < 1L) throw new ArgumentOutOfRangeException("oldCap");
            if (tier < 0 || tier > 5) throw new ArgumentOutOfRangeException("tier");
            var maximum = oldCap / 10L + 1L;
            if (targetReduction < 1L || targetReduction > maximum)
                throw new ArgumentOutOfRangeException("targetReduction");
            if (BasicTrainingCap(0L, oldCap, tier).Reduction >= targetReduction) return 0L;

            var low = 0L;
            var high = Math.Max(1L, 500L * tier + 1L);
            while (BasicTrainingCap(high, oldCap, tier).Reduction < targetReduction)
            {
                if (high >= long.MaxValue / 2L)
                {
                    high = long.MaxValue;
                    break;
                }
                high *= 2L;
            }
            while (low + 1L < high)
            {
                var middle = low + (high - low) / 2L;
                if (BasicTrainingCap(middle, oldCap, tier).Reduction >= targetReduction)
                    high = middle;
                else
                    low = middle;
            }
            return high;
        }

        internal static long BasicTrainingLevelForMaximumReduction(long oldCap, int tier)
        {
            if (oldCap < 1L) throw new ArgumentOutOfRangeException("oldCap");
            return BasicTrainingLevelForReduction(oldCap, tier, oldCap / 10L + 1L);
        }
    }

    internal sealed class FightBossProjection
    {
        internal bool PlayerWins;
        internal long KillTick;
        internal long DeathTick;
        internal double KillSeconds;
        internal double SurvivalSeconds;
        internal double OutgoingDamagePerTick;
        internal double IncomingDamagePerTick;
    }

    internal static class MechanicsFightBoss
    {
        /*
        NATIVE FIXED-FIGHT ORDER

        Each 0.02-second Fight Boss tick first regenerates/caps Boss HP, then damages the player
        and resolves player death, then damages the Boss and resolves victory.  Character HP regen
        is a separate callback: conservatively do not credit it before the first incoming hit and
        use its exact per-tick amount after that.  A same-tick death therefore loses.
        */
        internal static FightBossProjection Evaluate(
            double playerAttack, double playerDefense, double playerHp,
            double bossAttack, double bossDefense, double bossHp,
            double bossMaxHp, double bossRegen)
        {
            foreach (var value in new[]
                     {
                         playerAttack, playerDefense, playerHp, bossAttack, bossDefense,
                         bossHp, bossMaxHp, bossRegen
                     })
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                    throw new ArgumentOutOfRangeException("Fight Boss inputs must be finite and non-negative");

            var result = new FightBossProjection
            {
                KillTick = long.MaxValue,
                DeathTick = long.MaxValue,
                KillSeconds = double.PositiveInfinity,
                SurvivalSeconds = double.PositiveInfinity
            };
            if (playerHp <= 0.0 || bossHp <= 0.0)
                return result;

            result.OutgoingDamagePerTick = MechanicsCadence.SecondsPerTick
                                           * Math.Max(0.0, playerAttack - bossDefense);
            var preHitBossHp = Math.Min(bossMaxHp, bossHp + bossRegen);
            if (result.OutgoingDamagePerTick > 0.0)
            {
                if (result.OutgoingDamagePerTick >= preHitBossHp)
                    result.KillTick = 1L;
                else
                {
                    var netBossDamage = result.OutgoingDamagePerTick - bossRegen;
                    if (netBossDamage > 0.0)
                        result.KillTick = 1L + (long)Math.Ceiling(
                            (preHitBossHp - result.OutgoingDamagePerTick) / netBossDamage);
                }
            }

            result.IncomingDamagePerTick = MechanicsCadence.SecondsPerTick
                                           * Math.Max(0.0, bossAttack - playerDefense);
            var playerRegen = 0.001 + 0.001 * playerDefense;
            if (result.IncomingDamagePerTick > 0.0)
            {
                if (result.IncomingDamagePerTick >= playerHp)
                    result.DeathTick = 1L;
                else
                {
                    var netPlayerDamage = result.IncomingDamagePerTick - playerRegen;
                    if (netPlayerDamage > 0.0)
                        result.DeathTick = 1L + (long)Math.Ceiling(
                            (playerHp - result.IncomingDamagePerTick) / netPlayerDamage);
                }
            }

            if (result.KillTick != long.MaxValue)
                result.KillSeconds = MechanicsCadence.SecondsForTicks(result.KillTick);
            if (result.DeathTick != long.MaxValue)
                result.SurvivalSeconds = MechanicsCadence.SecondsForTicks(result.DeathTick);
            result.PlayerWins = result.KillTick < result.DeathTick;
            return result;
        }
    }

    internal static class MechanicsWish
    {
        internal const double ResourceExponent = 0.17;
        internal const int BaseMinimumSeconds = 14400;
        internal const int FixedReducerSeconds = 24;
        internal const double SinglePrecisionSafeSeconds = 666720.0;

        internal static double RawProgressPerTick(
            double energyPower, double allocatedEnergy,
            double magicPower, double allocatedMagic,
            double resource3Power, double allocatedResource3,
            double totalWishSpeed, double baseDivider, long currentLevel)
        {
            if (currentLevel < 0L) throw new ArgumentOutOfRangeException("currentLevel");
            if (!(baseDivider > 0.0) || double.IsNaN(baseDivider))
                throw new ArgumentOutOfRangeException("baseDivider");
            if (energyPower <= 0.0 || allocatedEnergy <= 0.0
                || magicPower <= 0.0 || allocatedMagic <= 0.0
                || resource3Power <= 0.0 || allocatedResource3 <= 0.0
                || totalWishSpeed <= 0.0)
                return 0.0;

            return Math.Pow(energyPower * allocatedEnergy, ResourceExponent)
                   * Math.Pow(magicPower * allocatedMagic, ResourceExponent)
                   * Math.Pow(resource3Power * allocatedResource3, ResourceExponent)
                   * totalWishSpeed / baseDivider / (currentLevel + 1.0);
        }

        internal static int MinimumSeconds(int perk109Level, int perk110Level, int quirk54Level)
        {
            if (perk109Level < 0) throw new ArgumentOutOfRangeException("perk109Level");
            if (perk110Level < 0) throw new ArgumentOutOfRangeException("perk110Level");
            if (quirk54Level < 0) throw new ArgumentOutOfRangeException("quirk54Level");
            var reductions = (long)perk109Level + perk110Level + quirk54Level;
            var seconds = BaseMinimumSeconds - reductions * FixedReducerSeconds;
            return seconds <= 0L ? 0 : seconds >= int.MaxValue ? int.MaxValue : (int)seconds;
        }

        internal static double MaximumProgressPerTick(int minimumSeconds)
        {
            if (minimumSeconds <= 0) return double.PositiveInfinity;
            return 1.0 / minimumSeconds / MechanicsCadence.TicksPerSecond;
        }

        internal static double CappedProgressPerTick(double rawProgressPerTick, int minimumSeconds)
        {
            if (double.IsNaN(rawProgressPerTick) || rawProgressPerTick <= 0.0) return 0.0;
            return Math.Min(rawProgressPerTick, MaximumProgressPerTick(minimumSeconds));
        }

        internal static double EqualSplitPerWishRateScale(int activeWishCount)
        {
            if (activeWishCount < 1) throw new ArgumentOutOfRangeException("activeWishCount");
            return Math.Pow(activeWishCount, -3.0 * ResourceExponent);
        }

        internal static double EqualSplitAggregateRateScale(int activeWishCount)
        {
            return activeWishCount * EqualSplitPerWishRateScale(activeWishCount);
        }

        internal static bool IsSinglePrecisionDurationSafe(double secondsPerLevel)
        {
            return !double.IsNaN(secondsPerLevel) && secondsPerLevel >= 0.0
                   && secondsPerLevel <= SinglePrecisionSafeSeconds;
        }
    }

    internal enum ItopodDifficulty
    {
        Normal = 0,
        Evil = 1,
        Sadistic = 2
    }

    internal static class MechanicsItopod
    {
        internal const long ProgressPerPerkPoint = 1000000L;
        internal const int MaximumFloor = 1600;

        internal static long OrdinaryProgressPerKill(
            ItopodDifficulty difficulty, int floor, double progressBonus, long improvedBasePp)
        {
            if (floor < 0 || floor > MaximumFloor) throw new ArgumentOutOfRangeException("floor");
            if (double.IsNaN(progressBonus) || progressBonus < 0.0)
                throw new ArgumentOutOfRangeException("progressBonus");
            if (improvedBasePp < 0L) throw new ArgumentOutOfRangeException("improvedBasePp");

            long baseProgress;
            switch (difficulty)
            {
                case ItopodDifficulty.Normal:
                    baseProgress = 200L + floor;
                    break;
                case ItopodDifficulty.Evil:
                    baseProgress = 700L + floor;
                    break;
                case ItopodDifficulty.Sadistic:
                    baseProgress = 2000L + floor + improvedBasePp;
                    break;
                default:
                    throw new ArgumentOutOfRangeException("difficulty");
            }

            var progress = baseProgress * progressBonus;
            return progress >= long.MaxValue ? long.MaxValue : (long)Math.Floor(progress);
        }

        internal static long FirstClearPerkPoints(int floor, bool isNewRecord)
        {
            if (floor < 0 || floor > MaximumFloor) throw new ArgumentOutOfRangeException("floor");
            if (!isNewRecord || floor == 0 || floor % 10 != 0) return 0L;
            var q = floor / 10;
            var award = (q + 9L) / 10L;
            if (q % 10 == 0) award *= 10L;
            return award;
        }

        internal static long CompletedPerkPoints(long accumulatedProgress)
        {
            if (accumulatedProgress < 0L) throw new ArgumentOutOfRangeException("accumulatedProgress");
            return accumulatedProgress / ProgressPerPerkPoint;
        }
    }
}
