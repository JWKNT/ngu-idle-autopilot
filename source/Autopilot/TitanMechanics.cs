/*
FILE PURPOSE

Purpose: This file is the dependency-free, installed-source oracle for all fourteen NGU Idle
Titans. It owns the exact zone/gate/type/version tables, spawn-clock transforms, Walderp clock
pause, candidate native-autokill thresholds, manual-only puzzle prerequisites, one-time/retryable
terminal eligibility, and cumulative T12 END-piece coverage.

Mechanism: Immutable descriptors encode native enemy types and zone gates. ClockProjection keeps
arithmetic time-until-ready separate from a meaningful wall ETA when T5 is paused. Unlock,
autokill, manual-prerequisite, and T12-selection functions consume only copied scalars/arrays and
return explicit proof objects. Candidate autokill comparisons round through float because the
native totalAdvAttack/Defense/HPRegen methods and constants are float32.

Inputs and outputs: Inputs are Titan IDs, effective boss/quest flags, elapsed clocks, Walderp phase,
candidate Adventure stats, exact selected-version bestiary kills, manual Apathy/Glop facts, and
ordinary ownership IDs. Outputs are immutable descriptors and independent reachability, unlock,
clock, autokill, prerequisite, terminal-retry, enemy-index, and T12-coverage decisions.

Invariants and safety: Reachable, unlocked, due, native-autokill, manual-ready, and loot-capacity are
different proofs and must never substitute for one another. T5 can have a finite arithmetic
remainder but no wall-clock ETA. T9's installed source uses 24 bestiary kills; T10-T12 use five.
Manual Apathy/Glop requirements never block a proven native autokill. T13 stops after its rat flag;
T14 remains recovery-actionable while ordinary item 495 is missing even if finalTitanDefeated was
already latched. T12 higher versions cumulatively include every lower-version END roll.

Extension points and non-goals: ZoneHelpers adapts live Character state to these pure functions;
LootCapacity supplies a separate exact capacity proof, and TitanExecutionManager owns pre-staging
and mutations. This file does not reflect, equip, fight, change versions, route Adventure, inspect
filters, invoke controllers, or authorize terminal execution.
*/
using System;

namespace NGUInjector.Autopilot
{
    internal sealed class TitanDescriptor
    {
        private readonly int[] _enemyTypes;

        internal readonly int TitanId;
        internal readonly int Zone;
        internal readonly int EffectiveBossGate;
        internal readonly int BaseSpawnSeconds;
        internal readonly string Name;

        internal TitanDescriptor(int titanId, int zone, int effectiveBossGate,
            int baseSpawnSeconds, string name, int[] enemyTypes)
        {
            TitanId = titanId;
            Zone = zone;
            EffectiveBossGate = effectiveBossGate;
            BaseSpawnSeconds = baseSpawnSeconds;
            Name = name ?? string.Empty;
            _enemyTypes = (int[])enemyTypes.Clone();
        }

        internal int[] EnemyTypes()
        {
            return (int[])_enemyTypes.Clone();
        }
    }

    internal sealed class TitanClockProjection
    {
        internal readonly int TitanId;
        internal readonly int DueSeconds;
        internal readonly int ArithmeticRemainingSeconds;
        internal readonly bool Due;
        internal readonly bool Paused;
        internal readonly bool HasWallClockEta;
        internal readonly double WallClockEtaSeconds;
        internal readonly string PauseReason;

        internal TitanClockProjection(int titanId, int dueSeconds, int remainingSeconds,
            bool paused, string pauseReason)
        {
            TitanId = titanId;
            DueSeconds = dueSeconds;
            ArithmeticRemainingSeconds = remainingSeconds;
            Due = remainingSeconds == 0;
            Paused = paused && !Due;
            HasWallClockEta = Due || !Paused;
            WallClockEtaSeconds = HasWallClockEta ? remainingSeconds : -1.0;
            PauseReason = Paused ? pauseReason ?? string.Empty : string.Empty;
        }
    }

    internal sealed class TitanNativeAutokillProjection
    {
        internal readonly int TitanId;
        internal readonly int Version;
        internal readonly float RequiredAttack;
        internal readonly float RequiredDefense;
        internal readonly float RequiredHpRegen;
        internal readonly int RequiredBestiaryKills;
        internal readonly bool ViaStats;
        internal readonly bool ViaBestiary;
        internal readonly bool Achieved;
        internal readonly string Reason;

        internal TitanNativeAutokillProjection(int titanId, int version,
            float requiredAttack, float requiredDefense, float requiredHpRegen,
            int requiredBestiaryKills, bool viaStats, bool viaBestiary, string reason)
        {
            TitanId = titanId;
            Version = version;
            RequiredAttack = requiredAttack;
            RequiredDefense = requiredDefense;
            RequiredHpRegen = requiredHpRegen;
            RequiredBestiaryKills = requiredBestiaryKills;
            ViaStats = viaStats;
            ViaBestiary = viaBestiary;
            Achieved = viaStats || viaBestiary;
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class TitanManualPrerequisiteProjection
    {
        internal readonly int TitanId;
        internal readonly int Version;
        internal readonly bool RequiresApathy;
        internal readonly int RequiredGlopCopies;
        internal readonly bool Ready;
        internal readonly string Reason;

        internal TitanManualPrerequisiteProjection(int titanId, int version,
            bool requiresApathy, int requiredGlopCopies, bool ready, string reason)
        {
            TitanId = titanId;
            Version = version;
            RequiresApathy = requiresApathy;
            RequiredGlopCopies = requiredGlopCopies;
            Ready = ready;
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class TitanClockSnapshot
    {
        private readonly double[] _elapsedSeconds;

        internal TitanClockSnapshot()
        {
            _elapsedSeconds = new double[14];
        }

        internal TitanClockSnapshot(double[] elapsedSeconds)
        {
            if (elapsedSeconds == null) throw new ArgumentNullException("elapsedSeconds");
            if (elapsedSeconds.Length != 14)
                throw new ArgumentException("A Titan clock snapshot must contain fourteen values.", "elapsedSeconds");
            _elapsedSeconds = (double[])elapsedSeconds.Clone();
            for (var i = 0; i < _elapsedSeconds.Length; i++)
            {
                if (double.IsNaN(_elapsedSeconds[i]) || double.IsInfinity(_elapsedSeconds[i])
                    || _elapsedSeconds[i] < 0.0)
                    throw new ArgumentOutOfRangeException("elapsedSeconds");
            }
        }

        internal double ElapsedSeconds(int titanId)
        {
            TitanMechanics.ValidateTitanId(titanId);
            return _elapsedSeconds[titanId - 1];
        }

        internal double[] ToArray()
        {
            return (double[])_elapsedSeconds.Clone();
        }
    }

    internal static class TitanMechanics
    {
        internal const int MinimumSpawnSeconds = 3600;

        /*
        SOURCE-PINNED TITAN TABLE

        These are installed enemyType integer values. T5 accepts four Walderp search phases plus
        bigBoss5. Versioned Titans list only their Titan records; guardian types are deliberately
        excluded so the same classifier can authorize boss-only combat and kill reconciliation.
        */
        private static readonly TitanDescriptor[] Descriptors =
        {
            D(1, 6, 58, 3600, "GRB", 2),
            D(2, 8, 66, 3600, "Grand Corrupted Tree", 3),
            D(3, 11, 82, 7200, "Jake", 4),
            D(4, 14, 100, 7200, "UUG", 5),
            D(5, 16, 116, 10800, "Walderp", 6, 7, 8, 9, 10),
            D(6, 19, 132, 12600, "The Beast", 13, 15, 16, 17),
            D(7, 23, 426, 16200, "Greasy Nerd", 18, 19, 20, 21),
            D(8, 26, 467, 18000, "Godmother", 23, 24, 25, 26),
            D(9, 30, 491, 19800, "The Exile", 28, 29, 30, 31),
            D(10, 34, 777, 23400, "IT HUNGERS", 33, 34, 35, 36),
            D(11, 38, 826, 25200, "Rock Lobster", 37, 38, 39, 40),
            D(12, 42, 850, 27000, "AMALGAMATE", 42, 43, 44, 45),
            D(13, 44, 897, 27000, "TIPPI THE TUTORIAL MOUSE", 46),
            D(14, 45, 902, 27000, "THE TRAITOR", 47)
        };

        // [Titan 6..12, zero-based selected version, attack/defense/HP regen].
        private static readonly float[,,] NativeAutokillThresholds =
        {
            {{2.5E+09f,1.6E+09f,2.5E+07f},{2.5E+10f,1.6E+10f,2.5E+08f},{2.5E+11f,1.6E+11f,2.5E+09f},{2.5E+12f,1.6E+12f,2.5E+10f}},
            {{5E+14f,2.5E+14f,5E+12f},{1E+16f,5E+15f,1E+14f},{2E+17f,1E+17f,2E+15f},{5E+18f,2.5E+18f,5E+16f}},
            {{5E+18f,2.5E+18f,5E+16f},{1E+20f,5E+19f,1E+18f},{2E+21f,1E+21f,2E+19f},{5E+22f,2.5E+22f,5E+20f}},
            {{1E+23f,5E+22f,1E+21f},{2E+24f,1E+24f,2E+22f},{4E+25f,2E+25f,4E+23f},{7.5E+26f,3.7E+26f,7.5E+24f}},
            {{4E+28f,2E+28f,4E+26f},{3.2E+29f,1.6E+29f,1.6E+27f},{2E+30f,1E+30f,9.999999E+27f},{1E+31f,5E+30f,5E+28f}},
            {{1.8E+31f,6E+30f,1.2E+29f},{9E+31f,3E+31f,6E+29f},{3.6E+32f,1.2E+32f,2.5E+30f},{1.1E+33f,3.6E+32f,7.5E+30f}},
            {{3E+33f,1E+33f,2E+31f},{1.2E+34f,4E+33f,8E+31f},{3.6E+34f,1.2E+34f,2.4E+32f},{7.2E+34f,2.4E+34f,4.8E+32f}}
        };

        private static readonly int[] Titan12EndDropOrder = {483, 489, 493, 484};
        private static readonly int[] Titan12MinimumVersions = {1, 2, 3, 4};

        internal static TitanDescriptor Describe(int titanId)
        {
            ValidateTitanId(titanId);
            return Descriptors[titanId - 1];
        }

        internal static bool IsReachable(int titanId, int highestReachableZone)
        {
            return highestReachableZone >= Describe(titanId).Zone;
        }

        internal static bool IsUnlocked(int titanId, int effectiveBossId,
            bool[] titan6Through12Unlocked, bool apathyItemMaxxed, bool ratTitanDefeated)
        {
            ValidateTitanId(titanId);
            if (titan6Through12Unlocked == null)
                throw new ArgumentNullException("titan6Through12Unlocked");
            if (titan6Through12Unlocked.Length != 7)
                throw new ArgumentException("Exactly seven T6-T12 unlock flags are required.",
                    "titan6Through12Unlocked");
            if (effectiveBossId < Describe(titanId).EffectiveBossGate) return false;
            if (titanId == 4 && !apathyItemMaxxed) return false;
            if (titanId >= 6 && titanId <= 12
                && !titan6Through12Unlocked[titanId - 6]) return false;
            if (titanId == 14 && !ratTitanDefeated) return false;
            return true;
        }

        internal static bool IsRewardActionable(int titanId, bool ratTitanDefeated,
            bool finalTitanDefeated, bool hasOrdinaryItem495)
        {
            ValidateTitanId(titanId);
            // finalTitanDefeated is deliberately not an admission gate: native latches it before
            // fallible addLoot. Keeping it explicit prevents callers from silently omitting that
            // important contradictory state from their snapshot.
            if (titanId == 13) return !ratTitanDefeated;
            if (titanId == 14) return !hasOrdinaryItem495;
            return true;
        }

        internal static int SpawnSeconds(int titanId, int normalNoRebirthCompletions,
            int evilNoRebirthCompletions, int sadisticNoRebirthCompletions)
        {
            ValidateTitanId(titanId);
            ValidateCompletions(normalNoRebirthCompletions, "normalNoRebirthCompletions");
            ValidateCompletions(evilNoRebirthCompletions, "evilNoRebirthCompletions");
            ValidateCompletions(sadisticNoRebirthCompletions, "sadisticNoRebirthCompletions");

            var baseSeconds = BaseSeconds(titanId);
            int applicableCompletions;
            if (titanId <= 2) applicableCompletions = 0;
            else if (titanId <= 6) applicableCompletions = normalNoRebirthCompletions;
            else if (titanId <= 9)
                applicableCompletions = SaturatingAdd(normalNoRebirthCompletions,
                    evilNoRebirthCompletions);
            else
                applicableCompletions = SaturatingAdd(
                    SaturatingAdd(normalNoRebirthCompletions, evilNoRebirthCompletions),
                    sadisticNoRebirthCompletions);

            var reducibleSeconds = baseSeconds - MinimumSpawnSeconds;
            var reduction = Math.Min((long)reducibleSeconds, applicableCompletions * 900L);
            return Math.Max(MinimumSpawnSeconds, baseSeconds - (int)reduction);
        }

        internal static int SecondsUntilReady(int titanId, double elapsedSinceClockReset,
            int normalNoRebirthCompletions, int evilNoRebirthCompletions,
            int sadisticNoRebirthCompletions)
        {
            ValidateElapsed(elapsedSinceClockReset);
            var due = SpawnSeconds(titanId, normalNoRebirthCompletions,
                evilNoRebirthCompletions, sadisticNoRebirthCompletions);
            if (elapsedSinceClockReset >= due) return 0;
            var remaining = Math.Ceiling(due - elapsedSinceClockReset);
            return remaining >= int.MaxValue ? int.MaxValue : (int)remaining;
        }

        internal static TitanClockProjection EvaluateClock(int titanId,
            double elapsedSinceClockReset, int normalNoRebirthCompletions,
            int evilNoRebirthCompletions, int sadisticNoRebirthCompletions,
            int waldoFinds, int waldoDefeats)
        {
            if (waldoFinds < 0) throw new ArgumentOutOfRangeException("waldoFinds");
            if (waldoDefeats < 0) throw new ArgumentOutOfRangeException("waldoDefeats");
            var due = SpawnSeconds(titanId, normalNoRebirthCompletions,
                evilNoRebirthCompletions, sadisticNoRebirthCompletions);
            var remaining = SecondsUntilReady(titanId, elapsedSinceClockReset,
                normalNoRebirthCompletions, evilNoRebirthCompletions,
                sadisticNoRebirthCompletions);
            var paused = titanId == 5 && IsWaldoClockPaused(waldoFinds, waldoDefeats);
            return new TitanClockProjection(titanId, due, remaining, paused,
                "awaiting the next Walderp find phase");
        }

        internal static bool IsWaldoClockPaused(int waldoFinds, int waldoDefeats)
        {
            if (waldoFinds < 0) throw new ArgumentOutOfRangeException("waldoFinds");
            if (waldoDefeats < 0) throw new ArgumentOutOfRangeException("waldoDefeats");
            return waldoDefeats > waldoFinds && waldoFinds < 4;
        }

        internal static bool IsReady(int titanId, double elapsedSinceClockReset,
            int normalNoRebirthCompletions, int evilNoRebirthCompletions,
            int sadisticNoRebirthCompletions)
        {
            return SecondsUntilReady(titanId, elapsedSinceClockReset,
                normalNoRebirthCompletions, evilNoRebirthCompletions,
                sadisticNoRebirthCompletions) == 0;
        }

        /*
        CANDIDATE NATIVE AUTOKILL

        This projection never invokes a live predicate. A speculative loadout is judged from its
        own attack, defense, and HP regen plus the exact selected record's bestiary count. That
        removes the old circular requirement that candidate gear already be equipped. Installed
        T9 source uses kills >= 24; only T10-T12 use kills > 4.
        */
        internal static TitanNativeAutokillProjection EvaluateNativeAutokill(
            int titanId, int zeroBasedVersion, double candidateAttack,
            double candidateDefense, double candidateHpRegen, int selectedVersionBestiaryKills)
        {
            if (titanId < 6 || titanId > 12)
                throw new ArgumentOutOfRangeException("titanId");
            ValidateVersion(zeroBasedVersion);
            ValidateFiniteNonNegative(candidateAttack, "candidateAttack");
            ValidateFiniteNonNegative(candidateDefense, "candidateDefense");
            ValidateFiniteNonNegative(candidateHpRegen, "candidateHpRegen");
            if (selectedVersionBestiaryKills < 0)
                throw new ArgumentOutOfRangeException("selectedVersionBestiaryKills");

            var row = titanId - 6;
            var attack = NativeAutokillThresholds[row, zeroBasedVersion, 0];
            var defense = NativeAutokillThresholds[row, zeroBasedVersion, 1];
            var regen = NativeAutokillThresholds[row, zeroBasedVersion, 2];
            var killRequirement = titanId == 9 ? 24 : titanId >= 10 ? 5 : 0;
            var viaBestiary = killRequirement > 0
                              && selectedVersionBestiaryKills >= killRequirement;
            var viaStats = (float)candidateAttack >= attack
                           && (float)candidateDefense >= defense
                           && (float)candidateHpRegen >= regen;
            var reason = viaBestiary
                ? "selected-version native bestiary shortcut is achieved"
                : viaStats ? "candidate float32 stats meet the native threshold"
                    : "candidate stats and selected-version bestiary count are below native autokill";
            return new TitanNativeAutokillProjection(titanId, zeroBasedVersion,
                attack, defense, regen, killRequirement, viaStats, viaBestiary, reason);
        }

        internal static int HighestNativeAutokillVersion(int titanId, double candidateAttack,
            double candidateDefense, double candidateHpRegen, int[] bestiaryKillsByVersion)
        {
            if (bestiaryKillsByVersion == null)
                throw new ArgumentNullException("bestiaryKillsByVersion");
            if (bestiaryKillsByVersion.Length != 4)
                throw new ArgumentException("Exactly four selected-version kill counts are required.",
                    "bestiaryKillsByVersion");
            for (var version = 3; version >= 0; version--)
                if (EvaluateNativeAutokill(titanId, version, candidateAttack,
                        candidateDefense, candidateHpRegen, bestiaryKillsByVersion[version]).Achieved)
                    return version;
            return -1;
        }

        internal static TitanManualPrerequisiteProjection EvaluateManualPrerequisites(
            int titanId, int zeroBasedVersion, bool hasEquippedLevel100Apathy,
            int removableGlopCopies, int projectedEnemyActions)
        {
            ValidateTitanId(titanId);
            ValidateVersion(zeroBasedVersion);
            if (removableGlopCopies < 0)
                throw new ArgumentOutOfRangeException("removableGlopCopies");
            if (projectedEnemyActions < 0)
                throw new ArgumentOutOfRangeException("projectedEnemyActions");

            var requiresApathy = titanId == 4 || titanId == 12 && zeroBasedVersion == 3;
            var glops = titanId == 10 && projectedEnemyActions > 0
                ? (projectedEnemyActions + 4) / 5 : 0;
            var ready = (!requiresApathy || hasEquippedLevel100Apathy)
                        && removableGlopCopies >= glops;
            var reason = ready ? "manual-only Titan prerequisites are satisfied"
                : requiresApathy && !hasEquippedLevel100Apathy
                    ? "manual fight requires an equipped level-100 Ring of Apathy"
                    : "manual fight requires " + glops + " removable Glop copies";
            return new TitanManualPrerequisiteProjection(titanId, zeroBasedVersion,
                requiresApathy, glops, ready, reason);
        }

        internal static bool IsTitanEnemyType(int titanId, int enemyTypeValue)
        {
            var types = Describe(titanId).EnemyTypes();
            for (var i = 0; i < types.Length; i++)
                if (types[i] == enemyTypeValue) return true;
            return false;
        }

        internal static int EnemyTypeForVersion(int titanId, int zeroBasedVersion)
        {
            ValidateTitanId(titanId);
            if (titanId >= 6 && titanId <= 12)
            {
                ValidateVersion(zeroBasedVersion);
                return Descriptors[titanId - 1].EnemyTypes()[zeroBasedVersion];
            }
            if (zeroBasedVersion != 0) throw new ArgumentOutOfRangeException("zeroBasedVersion");
            if (titanId == 5)
                throw new InvalidOperationException("Walderp has five phase types, not a selected version.");
            return Descriptors[titanId - 1].EnemyTypes()[0];
        }

        internal static int EnemyIndexForVersion(int titanId, int zeroBasedVersion)
        {
            ValidateTitanId(titanId);
            if (titanId >= 6 && titanId <= 10)
            {
                ValidateVersion(zeroBasedVersion);
                return zeroBasedVersion + 1;
            }
            if (titanId == 11 || titanId == 12)
            {
                ValidateVersion(zeroBasedVersion);
                return zeroBasedVersion;
            }
            if (zeroBasedVersion != 0) throw new ArgumentOutOfRangeException("zeroBasedVersion");
            return 0;
        }

        internal static int[] Titan12EndItemsForVersion(int oneBasedVersion)
        {
            if (oneBasedVersion < 1 || oneBasedVersion > 4)
                throw new ArgumentOutOfRangeException("oneBasedVersion");
            var result = new int[oneBasedVersion];
            Array.Copy(Titan12EndDropOrder, result, oneBasedVersion);
            return result;
        }

        internal static int HighestUsefulTitan12Version(int maximumSafelyKillableVersion,
            int[] ordinaryOwnedItemIds)
        {
            if (ordinaryOwnedItemIds == null)
                throw new ArgumentNullException("ordinaryOwnedItemIds");
            var maximum = Math.Min(4, maximumSafelyKillableVersion);
            if (maximum < 1) return -1;
            var selected = -1;
            for (var i = 0; i < Titan12EndDropOrder.Length; i++)
            {
                if (Contains(ordinaryOwnedItemIds, Titan12EndDropOrder[i])) continue;
                var provenance = Titan12MinimumVersions[i];
                if (provenance <= maximum) selected = Math.Max(selected, provenance);
            }
            return selected;
        }

        internal static TitanClockSnapshot ApplyOrdinaryRebirth(TitanClockSnapshot current)
        {
            if (current == null) throw new ArgumentNullException("current");
            return new TitanClockSnapshot();
        }

        internal static TitanClockSnapshot ApplyTitanKill(TitanClockSnapshot current, int titanId)
        {
            if (current == null) throw new ArgumentNullException("current");
            ValidateTitanId(titanId);
            var elapsed = current.ToArray();
            elapsed[titanId - 1] = 0.0;
            return new TitanClockSnapshot(elapsed);
        }

        internal static int BaseSeconds(int titanId)
        {
            return Describe(titanId).BaseSpawnSeconds;
        }

        internal static void ValidateTitanId(int titanId)
        {
            if (titanId < 1 || titanId > 14) throw new ArgumentOutOfRangeException("titanId");
        }

        private static TitanDescriptor D(int titanId, int zone, int gate, int baseSeconds,
            string name, params int[] enemyTypes)
        {
            return new TitanDescriptor(titanId, zone, gate, baseSeconds, name, enemyTypes);
        }

        private static bool Contains(int[] values, int expected)
        {
            for (var i = 0; i < values.Length; i++)
                if (values[i] == expected) return true;
            return false;
        }

        private static void ValidateCompletions(int completions, string parameterName)
        {
            if (completions < 0) throw new ArgumentOutOfRangeException(parameterName);
        }

        private static void ValidateElapsed(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                throw new ArgumentOutOfRangeException("elapsedSinceClockReset");
        }

        private static void ValidateFiniteNonNegative(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private static void ValidateVersion(int zeroBasedVersion)
        {
            if (zeroBasedVersion < 0 || zeroBasedVersion > 3)
                throw new ArgumentOutOfRangeException("zeroBasedVersion");
        }

        private static int SaturatingAdd(int left, int right)
        {
            var sum = (long)left + right;
            return sum >= int.MaxValue ? int.MaxValue : (int)sum;
        }
    }
}
