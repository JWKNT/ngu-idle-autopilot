using System;
using System.Collections.Generic;

/*
FILE PURPOSE

Purpose: GoldBootstrapPlanner decides when a reset-local run should briefly leave ITOPOD for an
ordinary Adventure zone. Ordinary enemies provide liquid Gold; after selected Boss 30, the same
drop also establishes the Time Machine base-Gold record which turns passive Gold generation on.

Mechanism: The pure policy accepts one already-proved, finishable Gold sink, the strongest safe
ordinary-zone drop, the remaining reset horizon, and live Time Machine state. It routes only until
the sink has enough liquid Gold before Boss 30, or until one eligible post-Boss-30 drop can seed
positive GPS. Once GPS exists, passive generation owns later shortfalls and ITOPOD immediately
regains Adventure. Native random Gold drops use their exact 4x lower endpoint for admission.

Inputs and outputs: Inputs are immutable numeric/boolean snapshots. Output names the route mode,
target zone, conservative drop/GPS bounds, ETA, and a human-readable reason. OrdinaryMobBaseGold
is the build-pinned lower base-Gold table for non-Titan normal enemies.

Invariants and safety: No useful sink means no detour. No safe ordinary zone, non-finite input,
Time Machine challenge, or insufficient payoff horizon can never be mislabeled as passive-GPS
bootstrap. The policy never mutates a controller and never assumes an average RNG roll.

Extension points and non-goals: A future global scheduler may compare optional base-record upgrades
against PP throughput. This policy deliberately owns only the zero-GPS/liquidity deadlock after a
rebirth; it does not repeatedly chase larger random Gold records once income is positive.
*/
namespace NGUInjector.Autopilot
{
    internal enum GoldBootstrapMode
    {
        None,
        LiquidGold,
        SeedTimeMachine
    }

    internal sealed class GoldBootstrapSnapshot
    {
        internal double CurrentGold;
        internal double SinkCost;
        internal string SinkName = string.Empty;
        internal int SelectedBoss;
        internal int HighestBoss;
        internal bool TimeMachineChallenge;
        internal double BaseGoldRecord;
        internal double GrossGoldPerSecond;
        internal double RemainingSeconds;
        internal int TargetZone = -1;
        internal int TargetFightType;
        internal double TargetMobBaseGold;
        internal double TotalGoldDropMultiplier = 1.0;
        internal double KillCycleSeconds;
    }

    internal sealed class GoldBootstrapDecision
    {
        internal bool ShouldRoute;
        internal GoldBootstrapMode Mode;
        internal int TargetZone = -1;
        internal int TargetFightType;
        internal string SinkName = string.Empty;
        internal double SinkCost;
        internal double ConservativeDrop;
        internal double ConservativeGps;
        internal double EtaSeconds = double.PositiveInfinity;
        internal string Reason = "Gold bootstrap has not been evaluated";

        internal static GoldBootstrapDecision Hold(string reason)
        {
            return new GoldBootstrapDecision {Reason = reason ?? string.Empty};
        }
    }

    internal static class GoldBootstrapPlanner
    {
        private static readonly Dictionary<int, double> OrdinaryBaseGold =
            new Dictionary<int, double>
            {
                {0, 100.0}, {1, 400.0}, {2, 900.0}, {3, 2200.0}, {4, 4000.0},
                {5, 10000.0}, {7, 30000.0}, {9, 65000.0}, {10, 100000.0},
                {12, 180000.0}, {13, 220000.0}, {15, 220000.0}, {17, 220000.0},
                {18, 280000.0}, {20, 600000.0}, {21, 2.8e8}, {22, 1e9},
                {24, 5e9}, {25, 1e10}, {27, 3e10}, {28, 6e10}, {29, 1e11},
                {31, 2e11}, {32, 1.5e14}, {33, 3e14}, {35, 1.2e15},
                {36, 2.5e15}, {37, 5e15}, {39, 1e16}, {40, 2e16},
                {41, 4e16}, {43, 8e16}
            };

        internal static double OrdinaryMobBaseGold(int zone)
        {
            double value;
            return OrdinaryBaseGold.TryGetValue(zone, out value) ? value : 0.0;
        }

        internal static bool HasPayoffWindow(double completionSeconds, double horizonSeconds)
        {
            if (!FiniteNonNegative(completionSeconds) || !FiniteNonNegative(horizonSeconds))
                return false;
            var payoff = Math.Max(30.0, Math.Min(300.0, completionSeconds));
            return completionSeconds + payoff <= horizonSeconds;
        }

        internal static GoldBootstrapDecision Evaluate(GoldBootstrapSnapshot snapshot)
        {
            if (snapshot == null)
                return GoldBootstrapDecision.Hold("Gold bootstrap snapshot is unavailable");
            if (!FinitePositive(snapshot.SinkCost) || string.IsNullOrEmpty(snapshot.SinkName))
                return GoldBootstrapDecision.Hold(
                    "No finishable Augment or valued Blood purchase needs Gold before rebirth");
            if (snapshot.TargetZone < 0 || !FinitePositive(snapshot.TargetMobBaseGold))
                return GoldBootstrapDecision.Hold(
                    "No source-pinned ordinary enemy Gold drop is available in the safe route");
            if (!FinitePositive(snapshot.TotalGoldDropMultiplier)
                || !FinitePositive(snapshot.KillCycleSeconds)
                || !FiniteNonNegative(snapshot.RemainingSeconds))
                return GoldBootstrapDecision.Hold("Gold bootstrap inputs are not finite and positive");

            var drop = 4.0 * snapshot.TargetMobBaseGold * snapshot.TotalGoldDropMultiplier;
            if (!FinitePositive(drop))
                return GoldBootstrapDecision.Hold("The conservative ordinary-enemy Gold drop is zero");

            var selectedBossCanRecord = snapshot.SelectedBoss > 29;
            var gpsCanRun = selectedBossCanRecord && !snapshot.TimeMachineChallenge;
            var needsSeed = gpsCanRun
                            && (!FinitePositive(snapshot.BaseGoldRecord)
                                || !FinitePositive(snapshot.GrossGoldPerSecond));
            if (needsSeed)
            {
                var record = Math.Max(Math.Max(0.0, snapshot.BaseGoldRecord), drop);
                var bossFactor = Math.Max(1, snapshot.HighestBoss - 27);
                var gps = snapshot.BaseGoldRecord > 0.0
                          && snapshot.GrossGoldPerSecond > 0.0
                    ? snapshot.GrossGoldPerSecond * record / snapshot.BaseGoldRecord
                    : record * bossFactor;
                var shortfall = Math.Max(0.0, snapshot.SinkCost - snapshot.CurrentGold);
                var funding = gps > 0.0 ? shortfall / gps : double.PositiveInfinity;
                var eta = snapshot.KillCycleSeconds + funding;
                if (!FiniteNonNegative(eta) || eta + 30.0 > snapshot.RemainingSeconds)
                    return GoldBootstrapDecision.Hold(
                        "An ordinary drop could seed Time Machine Gold, but not early enough to fund "
                        + snapshot.SinkName + " before rebirth");
                return new GoldBootstrapDecision
                {
                    ShouldRoute = true,
                    Mode = GoldBootstrapMode.SeedTimeMachine,
                    TargetZone = snapshot.TargetZone,
                    TargetFightType = snapshot.TargetFightType,
                    SinkName = snapshot.SinkName,
                    SinkCost = snapshot.SinkCost,
                    ConservativeDrop = drop,
                    ConservativeGps = gps,
                    EtaSeconds = eta,
                    Reason = "Get one ordinary enemy Gold drop to seed Time Machine income for "
                             + snapshot.SinkName
                };
            }

            if (snapshot.CurrentGold >= snapshot.SinkCost)
                return GoldBootstrapDecision.Hold(
                    snapshot.SinkName + " already has enough liquid Gold; ITOPOD keeps priority");
            if (FinitePositive(snapshot.GrossGoldPerSecond))
                return GoldBootstrapDecision.Hold(
                    "Time Machine income is already funding " + snapshot.SinkName
                    + "; ITOPOD keeps priority");

            var missing = Math.Max(0.0, snapshot.SinkCost - snapshot.CurrentGold);
            var kills = Math.Max(1.0, Math.Ceiling(missing / drop));
            var liquidEta = kills * snapshot.KillCycleSeconds;
            if (!FiniteNonNegative(liquidEta) || liquidEta + 30.0 > snapshot.RemainingSeconds)
                return GoldBootstrapDecision.Hold(
                    "Ordinary farming cannot fund " + snapshot.SinkName
                    + " with a useful payoff window before rebirth");
            return new GoldBootstrapDecision
            {
                ShouldRoute = true,
                Mode = GoldBootstrapMode.LiquidGold,
                TargetZone = snapshot.TargetZone,
                TargetFightType = snapshot.TargetFightType,
                SinkName = snapshot.SinkName,
                SinkCost = snapshot.SinkCost,
                ConservativeDrop = drop,
                ConservativeGps = 0.0,
                EtaSeconds = liquidEta,
                Reason = snapshot.TimeMachineChallenge
                    ? "Farm ordinary enemies for liquid Gold for " + snapshot.SinkName
                      + "; this challenge disables passive Time Machine income"
                    : selectedBossCanRecord
                        ? "Farm ordinary enemies for liquid Gold for " + snapshot.SinkName
                        : "Farm enough ordinary-enemy Gold for " + snapshot.SinkName
                          + " while Fight Boss advances toward the Boss 30 Time Machine gate"
            };
        }

        private static bool FinitePositive(double value)
        {
            return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool FiniteNonNegative(double value)
        {
            return value >= 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
