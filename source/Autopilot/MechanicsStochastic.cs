/*
FILE PURPOSE

Purpose: This file supplies small, pure probability helpers for NGU Idle rare-drop, repeated-copy,
and coupon-collection forecasts.  It turns a per-eligible-kill probability and measured trial cadence
into auditable mean/median/confidence estimates without coupling the mechanics layer to Adventure
routing or live telemetry.

Mechanism: Geometric helpers model the first success, negative-binomial means model repeated copies,
and the uniform coupon helper models useful drops whose type is uniformly selected.  Each public
name states its assumptions so a caller cannot accidentally present an approximation as a native
guarantee.

Inputs and outputs: Inputs are probabilities in [0,1], integer trial/copy counts, and optional
seconds-per-trial.  Outputs are trial counts, seconds, probabilities, or classical coupon-collector
expectations.  No random number generator, file, game object, or clock is used.

Invariants and safety: Impossible events return positive infinity only for expectation functions;
invalid probabilities throw.  Quantile functions never claim certainty for a non-certain event and
use integer ceiling so the returned trial count actually reaches the requested confidence.

Extension points and non-goals: Add calibrated distributions or source-derived drop correlations in
a separate routing model.  These helpers deliberately do not assume enemy composition, respawn
time, inventory capacity, independent equipment drops, or that expected time is an acceptable risk
bound for an irreversible action.
*/
using System;

namespace NGUInjector.Autopilot
{
    internal static class MechanicsStochastic
    {
        internal static double GeometricMeanTrials(double successProbability)
        {
            ValidateProbability(successProbability, "successProbability");
            return successProbability == 0.0 ? double.PositiveInfinity : 1.0 / successProbability;
        }

        internal static long GeometricMedianTrials(double successProbability)
        {
            return GeometricQuantileTrials(successProbability, 0.5);
        }

        internal static long GeometricQuantileTrials(double successProbability, double confidence)
        {
            ValidateProbability(successProbability, "successProbability");
            if (double.IsNaN(confidence) || confidence < 0.0 || confidence >= 1.0)
                throw new ArgumentOutOfRangeException("confidence");
            if (confidence == 0.0) return 0L;
            if (successProbability == 0.0) return long.MaxValue;
            if (successProbability == 1.0) return 1L;

            var trials = Math.Ceiling(Math.Log(1.0 - confidence) / Math.Log(1.0 - successProbability));
            return trials >= long.MaxValue ? long.MaxValue : Math.Max(1L, (long)trials);
        }

        internal static double ProbabilityAtLeastOne(long trials, double successProbability)
        {
            if (trials < 0L) throw new ArgumentOutOfRangeException("trials");
            ValidateProbability(successProbability, "successProbability");
            if (trials == 0L || successProbability == 0.0) return 0.0;
            if (successProbability == 1.0) return 1.0;
            return 1.0 - Math.Pow(1.0 - successProbability, trials);
        }

        internal static double GeometricMeanSeconds(double successProbability, double secondsPerTrial)
        {
            if (double.IsNaN(secondsPerTrial) || secondsPerTrial < 0.0)
                throw new ArgumentOutOfRangeException("secondsPerTrial");
            return GeometricMeanTrials(successProbability) * secondsPerTrial;
        }

        internal static double ExpectedTrialsForCopies(int requiredCopies, double copyProbability)
        {
            if (requiredCopies < 0) throw new ArgumentOutOfRangeException("requiredCopies");
            ValidateProbability(copyProbability, "copyProbability");
            if (requiredCopies == 0) return 0.0;
            return copyProbability == 0.0
                ? double.PositiveInfinity
                : requiredCopies / copyProbability;
        }

        internal static int AdditionalLevelZeroCopiesToMax(int heldItemLevel)
        {
            if (heldItemLevel < 0 || heldItemLevel > 100)
                throw new ArgumentOutOfRangeException("heldItemLevel");
            return 100 - heldItemLevel;
        }

        internal static int TotalLevelZeroCopiesForFreshMax()
        {
            // One fresh level-zero target plus 100 level-zero merge sources. Native merging adds
            // target level + source level + 1 and caps the result at level 100.
            return 101;
        }

        internal static double CouponCollectorMeanUsefulDrops(int remainingUniformTypes)
        {
            if (remainingUniformTypes < 0) throw new ArgumentOutOfRangeException("remainingUniformTypes");
            if (remainingUniformTypes == 0) return 0.0;
            var harmonic = 0.0;
            for (var i = 1; i <= remainingUniformTypes; i++) harmonic += 1.0 / i;
            return remainingUniformTypes * harmonic;
        }

        internal static double CouponCollectorMeanTrialsUniform(
            int remainingUniformTypes, double anyUsefulDropProbability)
        {
            ValidateProbability(anyUsefulDropProbability, "anyUsefulDropProbability");
            var usefulDrops = CouponCollectorMeanUsefulDrops(remainingUniformTypes);
            if (usefulDrops == 0.0) return 0.0;
            return anyUsefulDropProbability == 0.0
                ? double.PositiveInfinity
                : usefulDrops / anyUsefulDropProbability;
        }

        private static void ValidateProbability(double probability, string parameterName)
        {
            if (double.IsNaN(probability) || probability < 0.0 || probability > 1.0)
                throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
