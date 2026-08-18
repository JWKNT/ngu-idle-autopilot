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
        internal bool HorizonReached;
        // KillTick is the exact counterfactual boss-only kill tick within the bounded horizon.
        // PlayerWins says whether native player-death-before-hit ordering actually reaches it.
        internal long KillTick;
        internal long DeathTick;
        internal long TicksSimulated;
        internal double KillSeconds;
        internal double SurvivalSeconds;
        internal double OutgoingDamagePerTick;
        internal double IncomingDamagePerTick;
        internal double PlayerStartHp;
        internal double PlayerMaxHp;
        internal double PlayerHpAtEnd;
        internal double BossHpAtEnd;
    }

    internal sealed class FightBossRecoveryProjection
    {
        // Immediate never includes pre-fight healing.  CurrentHpAfterSwap is the live current HP
        // clamped down to the candidate maximum; raising the maximum cannot raise this value.
        internal FightBossProjection Immediate;
        internal FightBossProjection AfterRecovery;
        internal FightBossProjection AtFullHp;
        internal double CurrentHpAfterSwap;
        internal double CandidateMaxHp;
        internal double RequiredStartHp;
        internal long RecoveryTicks;
        internal double RecoverySeconds;
        internal bool CanWinAtFullHp;
        internal bool RecoveryWithinHorizon;
    }

    internal static class MechanicsFightBoss
    {
        internal const long DefaultCombatHorizonTicks = 120L * MechanicsCadence.TicksPerSecond;
        internal const long DefaultRecoveryHorizonTicks = 120L * MechanicsCadence.TicksPerSecond;

        /*
        NATIVE FIXED-FIGHT ORDER

        Each 0.02-second Fight Boss tick first regenerates/caps Boss HP, then damages the player
        and resolves player death, then damages the Boss and resolves victory.  Character HP regen
        is a separate callback: conservatively do not credit it before the first incoming hit and
        use its exact per-tick amount after each completed, nonterminal fight tick.  A same-tick
        death therefore loses and the outgoing hit is never applied.

        This intentionally uses a bounded tick loop instead of an algebraic DPS quotient.  The
        multiplication/subtraction and repeated add/subtract order is the order in BossController;
        reassociating `attack * .02 - defense * .02` as `.02 * (attack - defense)` changes real
        double boundaries.  A horizon result is not a victory estimate.
        */
        internal static FightBossProjection Evaluate(
            double playerAttack, double playerDefense, double playerHp,
            double bossAttack, double bossDefense, double bossHp,
            double bossMaxHp, double bossRegen)
        {
            // Compatibility overload: without an independently supplied maximum, current HP is
            // also the cap.  In particular, this overload can never manufacture pre-fight healing.
            return Evaluate(playerAttack, playerDefense, playerHp, playerHp,
                bossAttack, bossDefense, bossHp, bossMaxHp, bossRegen,
                DefaultCombatHorizonTicks);
        }

        internal static FightBossProjection Evaluate(
            double playerAttack, double playerDefense, double playerCurrentHp, double playerMaxHp,
            double bossAttack, double bossDefense, double bossHp,
            double bossMaxHp, double bossRegen, long maxTicks)
        {
            ValidateNonNegativeFinite(playerAttack, "playerAttack");
            ValidateNonNegativeFinite(playerDefense, "playerDefense");
            ValidateNonNegativeFinite(playerCurrentHp, "playerCurrentHp");
            ValidateNonNegativeFinite(playerMaxHp, "playerMaxHp");
            ValidateNonNegativeFinite(bossAttack, "bossAttack");
            ValidateNonNegativeFinite(bossDefense, "bossDefense");
            ValidateNonNegativeFinite(bossHp, "bossHp");
            ValidateNonNegativeFinite(bossMaxHp, "bossMaxHp");
            ValidateNonNegativeFinite(bossRegen, "bossRegen");
            ValidateHorizon(maxTicks, "maxTicks");

            var currentPlayerHp = CurrentHpAfterMaxChange(playerCurrentHp, playerMaxHp);
            var currentBossHp = bossHp;
            var outgoingDamage = playerAttack * MechanicsCadence.SecondsPerTick;
            outgoingDamage -= bossDefense * MechanicsCadence.SecondsPerTick;
            if (outgoingDamage < 0.0) outgoingDamage = 0.0;
            var incomingDamage = bossAttack * MechanicsCadence.SecondsPerTick;
            incomingDamage -= playerDefense * MechanicsCadence.SecondsPerTick;
            if (incomingDamage < 0.0) incomingDamage = 0.0;
            var result = new FightBossProjection
            {
                KillTick = long.MaxValue,
                DeathTick = long.MaxValue,
                KillSeconds = double.PositiveInfinity,
                SurvivalSeconds = double.PositiveInfinity,
                OutgoingDamagePerTick = outgoingDamage,
                IncomingDamagePerTick = incomingDamage,
                PlayerStartHp = currentPlayerHp,
                PlayerMaxHp = playerMaxHp,
                PlayerHpAtEnd = currentPlayerHp,
                BossHpAtEnd = currentBossHp
            };
            if (currentPlayerHp <= 0.0 || currentBossHp <= 0.0)
                return result;

            var playerRegen = 0.001 + 0.001 * playerDefense;
            for (var tick = 1L; tick <= maxTicks; tick++)
            {
                // BossController.fight regenerates even before resolving the current fight frame.
                currentBossHp += bossRegen;
                if (currentBossHp > bossMaxHp) currentBossHp = bossMaxHp;

                // Native resolves player death and returns before the outgoing hit.
                currentPlayerHp -= incomingDamage;
                result.TicksSimulated = tick;
                result.PlayerHpAtEnd = currentPlayerHp;
                result.BossHpAtEnd = currentBossHp;
                if (currentPlayerHp <= 0.0)
                {
                    result.DeathTick = tick;
                    result.SurvivalSeconds = MechanicsCadence.SecondsForTicks(tick);
                    result.KillTick = PotentialKillTickAfterLethalIncoming(currentBossHp,
                        bossMaxHp, bossRegen, outgoingDamage, tick, maxTicks);
                    if (result.KillTick != long.MaxValue)
                        result.KillSeconds = MechanicsCadence.SecondsForTicks(result.KillTick);
                    return result;
                }

                currentBossHp -= outgoingDamage;
                result.BossHpAtEnd = currentBossHp;
                if (currentBossHp <= 0.0)
                {
                    result.PlayerWins = true;
                    result.KillTick = tick;
                    result.KillSeconds = MechanicsCadence.SecondsForTicks(tick);
                    return result;
                }

                // Character.updateHP is a separate 0.02-second callback.  Giving it no credit
                // before tick one is fail-closed under Unity's unspecified cross-component order.
                currentPlayerHp += playerRegen;
                if (currentPlayerHp > playerMaxHp) currentPlayerHp = playerMaxHp;
                result.PlayerHpAtEnd = currentPlayerHp;
            }

            result.HorizonReached = true;
            return result;
        }

        private static long PotentialKillTickAfterLethalIncoming(
            double bossHp, double bossMaxHp, double bossRegen,
            double outgoingDamage, long deathTick, long maxTicks)
        {
            if (bossHp <= 0.0 || outgoingDamage <= 0.0) return long.MaxValue;
            // The native hit did not happen.  Apply it only to this counterfactual state so
            // KillTick remains useful to legacy callers without changing BossHpAtEnd.
            bossHp -= outgoingDamage;
            if (bossHp <= 0.0) return deathTick;
            for (var tick = deathTick + 1L; tick <= maxTicks; tick++)
            {
                bossHp += bossRegen;
                if (bossHp > bossMaxHp) bossHp = bossMaxHp;
                bossHp -= outgoingDamage;
                if (bossHp <= 0.0) return tick;
            }
            return long.MaxValue;
        }

        /*
        RECOVERY SEMANTICS

        Equipping a candidate changes maximum Fight Boss HP through Attack but does not change live
        current HP.  This route projection first clamps current HP down when the candidate maximum
        is lower, evaluates combat immediately, and only then evaluates explicitly waited native HP
        callbacks.  Recovery never occurs implicitly inside Immediate.  RequiredStartHp is the
        first representable double start HP that wins within the combat horizon.
        */
        internal static FightBossRecoveryProjection EvaluateRecovery(
            double playerAttack, double playerDefense,
            double liveCurrentHp, double candidateMaxHp,
            double bossAttack, double bossDefense, double bossHp,
            double bossMaxHp, double bossRegen)
        {
            return EvaluateRecovery(playerAttack, playerDefense, liveCurrentHp, candidateMaxHp,
                bossAttack, bossDefense, bossHp, bossMaxHp, bossRegen,
                DefaultCombatHorizonTicks, DefaultRecoveryHorizonTicks);
        }

        internal static FightBossRecoveryProjection EvaluateRecovery(
            double playerAttack, double playerDefense,
            double liveCurrentHp, double candidateMaxHp,
            double bossAttack, double bossDefense, double bossHp,
            double bossMaxHp, double bossRegen,
            long maxCombatTicks, long maxRecoveryTicks)
        {
            ValidateHorizon(maxCombatTicks, "maxCombatTicks");
            ValidateHorizon(maxRecoveryTicks, "maxRecoveryTicks");
            var currentHp = CurrentHpAfterMaxChange(liveCurrentHp, candidateMaxHp);
            var immediate = Evaluate(playerAttack, playerDefense, currentHp, candidateMaxHp,
                bossAttack, bossDefense, bossHp, bossMaxHp, bossRegen, maxCombatTicks);
            var full = Evaluate(playerAttack, playerDefense, candidateMaxHp, candidateMaxHp,
                bossAttack, bossDefense, bossHp, bossMaxHp, bossRegen, maxCombatTicks);
            var route = new FightBossRecoveryProjection
            {
                Immediate = immediate,
                AtFullHp = full,
                AfterRecovery = immediate,
                CurrentHpAfterSwap = currentHp,
                CandidateMaxHp = candidateMaxHp,
                RequiredStartHp = double.PositiveInfinity,
                RecoveryTicks = long.MaxValue,
                RecoverySeconds = double.PositiveInfinity,
                CanWinAtFullHp = full.PlayerWins
            };
            if (!full.PlayerWins) return route;

            route.RequiredStartHp = FirstWinningStartHp(playerAttack, playerDefense, candidateMaxHp,
                bossAttack, bossDefense, bossHp, bossMaxHp, bossRegen, maxCombatTicks);
            if (immediate.PlayerWins)
            {
                route.AfterRecovery = immediate;
                route.RecoveryTicks = 0L;
                route.RecoverySeconds = 0.0;
                route.RecoveryWithinHorizon = true;
                return route;
            }

            var hpAtRecoveryHorizon = RecoverHp(currentHp, candidateMaxHp, playerDefense,
                maxRecoveryTicks);
            var horizonFight = Evaluate(playerAttack, playerDefense, hpAtRecoveryHorizon,
                candidateMaxHp, bossAttack, bossDefense, bossHp, bossMaxHp, bossRegen,
                maxCombatTicks);
            if (!horizonFight.PlayerWins) return route;

            // Winning is monotone in start HP, hence in the number of positive recovery ticks.
            var low = 0L;
            var high = maxRecoveryTicks;
            while (low + 1L < high)
            {
                var middle = low + (high - low) / 2L;
                var recoveredHp = RecoverHp(currentHp, candidateMaxHp, playerDefense, middle);
                var projection = Evaluate(playerAttack, playerDefense, recoveredHp, candidateMaxHp,
                    bossAttack, bossDefense, bossHp, bossMaxHp, bossRegen, maxCombatTicks);
                if (projection.PlayerWins) high = middle;
                else low = middle;
            }

            var requiredRecoveredHp = RecoverHp(currentHp, candidateMaxHp, playerDefense, high);
            route.AfterRecovery = Evaluate(playerAttack, playerDefense, requiredRecoveredHp,
                candidateMaxHp, bossAttack, bossDefense, bossHp, bossMaxHp, bossRegen,
                maxCombatTicks);
            route.RecoveryTicks = high;
            route.RecoverySeconds = MechanicsCadence.SecondsForTicks(high);
            route.RecoveryWithinHorizon = route.AfterRecovery.PlayerWins;
            return route;
        }

        internal static double CurrentHpAfterMaxChange(double liveCurrentHp, double candidateMaxHp)
        {
            ValidateNonNegativeFinite(liveCurrentHp, "liveCurrentHp");
            ValidateNonNegativeFinite(candidateMaxHp, "candidateMaxHp");
            return Math.Min(liveCurrentHp, candidateMaxHp);
        }

        internal static double RecoverHp(
            double currentHp, double maxHp, double playerDefense, long recoveryTicks)
        {
            ValidateNonNegativeFinite(currentHp, "currentHp");
            ValidateNonNegativeFinite(maxHp, "maxHp");
            ValidateNonNegativeFinite(playerDefense, "playerDefense");
            ValidateHorizon(recoveryTicks, "recoveryTicks");
            var hp = CurrentHpAfterMaxChange(currentHp, maxHp);
            var regen = 0.001 + 0.001 * playerDefense;
            for (var tick = 0L; tick < recoveryTicks; tick++)
            {
                hp += regen;
                if (hp > maxHp) hp = maxHp;
            }
            return hp;
        }

        private static double FirstWinningStartHp(
            double playerAttack, double playerDefense, double playerMaxHp,
            double bossAttack, double bossDefense, double bossHp,
            double bossMaxHp, double bossRegen, long maxCombatTicks)
        {
            var lowBits = BitConverter.DoubleToInt64Bits(0.0);
            var highBits = BitConverter.DoubleToInt64Bits(playerMaxHp);
            while (lowBits + 1L < highBits)
            {
                var middleBits = lowBits + (highBits - lowBits) / 2L;
                var startHp = BitConverter.Int64BitsToDouble(middleBits);
                var projection = Evaluate(playerAttack, playerDefense, startHp, playerMaxHp,
                    bossAttack, bossDefense, bossHp, bossMaxHp, bossRegen, maxCombatTicks);
                if (projection.PlayerWins) highBits = middleBits;
                else lowBits = middleBits;
            }
            return BitConverter.Int64BitsToDouble(highBits);
        }

        private static void ValidateNonNegativeFinite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                throw new ArgumentOutOfRangeException(name,
                    "Fight Boss inputs must be finite and non-negative");
        }

        private static void ValidateHorizon(long ticks, string name)
        {
            if (ticks < 0L || ticks > DefaultCombatHorizonTicks)
                throw new ArgumentOutOfRangeException(name,
                    "Fight Boss tick horizons must be between zero and 6,000 ticks");
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
