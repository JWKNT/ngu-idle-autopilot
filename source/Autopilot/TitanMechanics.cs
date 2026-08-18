/*
FILE PURPOSE

Purpose: This file is the source-derived oracle for all fourteen Titan spawn clocks and their reset
semantics in NGU Idle 1.260.  It replaces static T7-T9 waits and guide-table constants with the
installed game's Normal/Evil/Sadistic No-Rebirth reductions and universal one-hour floor.

Mechanism: SpawnSeconds selects the native base time and the difficulty bands whose No-Rebirth
completions reduce that Titan by 900 seconds each.  Readiness helpers compare a supplied elapsed
clock snapshot with that due time.  TitanClockSnapshot and the two Apply methods model only clock
reset transitions: ordinary rebirth resets every Titan; a successful Titan kill resets that Titan.

Inputs and outputs: Inputs are Titan IDs 1..14, No-Rebirth completion counts by difficulty, and
elapsed seconds since each clock reset.  Outputs are pure due/remaining/readiness values or cloned
clock snapshots.  Nothing reads or writes Character, a controller, the save, runtime files, or gear.

Invariants and safety: All target clocks bottom at 3,600 seconds.  T1-T2 are never reduced; T3-T6 use
Normal completions; T7-T9 use Normal+Evil; T10-T14 use all three.  T12-T14 use the installed DLL's
27,000 seconds, not the guide's stale 26,000-second T12 value.  A ready clock does not imply unlock,
version, puzzle, combat, Beast-mode, or reward eligibility.

Extension points and non-goals: Action admission should combine this clock oracle with explicit
version, prerequisite, combat, and reward-value gates.  Do not add loadout swaps, controller calls,
or Titan AI simulation here.
*/
using System;

namespace NGUInjector.Autopilot
{
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
                if (double.IsNaN(_elapsedSeconds[i]) || _elapsedSeconds[i] < 0.0)
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

        internal static int SpawnSeconds(
            int titanId,
            int normalNoRebirthCompletions,
            int evilNoRebirthCompletions,
            int sadisticNoRebirthCompletions)
        {
            ValidateTitanId(titanId);
            ValidateCompletions(normalNoRebirthCompletions, "normalNoRebirthCompletions");
            ValidateCompletions(evilNoRebirthCompletions, "evilNoRebirthCompletions");
            ValidateCompletions(sadisticNoRebirthCompletions, "sadisticNoRebirthCompletions");

            var baseSeconds = BaseSeconds(titanId);
            int applicableCompletions;
            if (titanId <= 2)
                applicableCompletions = 0;
            else if (titanId <= 6)
                applicableCompletions = normalNoRebirthCompletions;
            else if (titanId <= 9)
                applicableCompletions = SaturatingAdd(
                    normalNoRebirthCompletions, evilNoRebirthCompletions);
            else
                applicableCompletions = SaturatingAdd(
                    SaturatingAdd(normalNoRebirthCompletions, evilNoRebirthCompletions),
                    sadisticNoRebirthCompletions);

            var reducibleSeconds = baseSeconds - MinimumSpawnSeconds;
            var reduction = Math.Min((long)reducibleSeconds, applicableCompletions * 900L);
            return Math.Max(MinimumSpawnSeconds, baseSeconds - (int)reduction);
        }

        internal static int SecondsUntilReady(
            int titanId,
            double elapsedSinceClockReset,
            int normalNoRebirthCompletions,
            int evilNoRebirthCompletions,
            int sadisticNoRebirthCompletions)
        {
            if (double.IsNaN(elapsedSinceClockReset) || elapsedSinceClockReset < 0.0)
                throw new ArgumentOutOfRangeException("elapsedSinceClockReset");
            var due = SpawnSeconds(titanId, normalNoRebirthCompletions,
                evilNoRebirthCompletions, sadisticNoRebirthCompletions);
            if (elapsedSinceClockReset >= due) return 0;
            var remaining = Math.Ceiling(due - elapsedSinceClockReset);
            return remaining >= int.MaxValue ? int.MaxValue : (int)remaining;
        }

        internal static bool IsReady(
            int titanId,
            double elapsedSinceClockReset,
            int normalNoRebirthCompletions,
            int evilNoRebirthCompletions,
            int sadisticNoRebirthCompletions)
        {
            return SecondsUntilReady(titanId, elapsedSinceClockReset,
                normalNoRebirthCompletions, evilNoRebirthCompletions,
                sadisticNoRebirthCompletions) == 0;
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
            ValidateTitanId(titanId);
            if (titanId <= 2) return 3600;
            if (titanId <= 4) return 7200;
            if (titanId == 5) return 10800;
            if (titanId == 6) return 12600;
            if (titanId == 7) return 16200;
            if (titanId == 8) return 18000;
            if (titanId == 9) return 19800;
            if (titanId == 10) return 23400;
            if (titanId == 11) return 25200;
            return 27000;
        }

        internal static void ValidateTitanId(int titanId)
        {
            if (titanId < 1 || titanId > 14) throw new ArgumentOutOfRangeException("titanId");
        }

        private static void ValidateCompletions(int completions, string parameterName)
        {
            if (completions < 0) throw new ArgumentOutOfRangeException(parameterName);
        }

        private static int SaturatingAdd(int left, int right)
        {
            var sum = (long)left + right;
            return sum >= int.MaxValue ? int.MaxValue : (int)sum;
        }
    }
}
