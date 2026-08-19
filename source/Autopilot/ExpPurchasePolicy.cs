/*
FILE PURPOSE

ExpPurchasePolicy is the pure decision boundary for permanent EXP speed atoms and direct Fight
Boss-stat exceptions.  It ranks the three installed pre-50 Energy Speed specials by productive
headroom per exact live EXP, chooses the ordinary +0.1/+1.0 atom without starting an unfunded
large atom, and solves Magic Speed's native discrete refill-rate breakpoint.

Direct Fight Boss Attack/Defense is admitted only for a new-record gate with a finite current
rollout ETA, a source-proven post-purchase win, and per-EXP time value above the best permanent
resource-growth atom.  This suppresses repeated percentage-stat spending against already-cleared
Bosses.  The class has no Character/controller dependency and never authorizes or executes a
purchase; the build-pinned descriptor/runtime remains the mutation authority.
*/
using System;
using System.Collections.Generic;
using System.Linq;

namespace NGUInjector.Autopilot
{
    internal sealed class EnergySpeedPurchaseChoice
    {
        internal string DescriptorKey = string.Empty;
        internal long ExactCost;
        internal int DeltaHundredths;
        internal bool FullyFunded;
        internal double ProductiveGain;
    }

    internal static class ExpPurchasePolicy
    {
        internal static EnergySpeedPurchaseChoice ChoosePre50EnergySpeed(double currentSpeed,
            bool special1Owned, bool special2Owned, bool special3Owned,
            long special1Cost, long special2Cost, long special3Cost,
            long speed10Cost, long speed100Cost, long spendableExp)
        {
            if (!Finite(currentSpeed) || currentSpeed < 0.0 || currentSpeed >= 49.91)
                return null;
            var headroom = Math.Max(0.0, 50.0 - currentSpeed);
            var specials = new List<EnergySpeedPurchaseChoice>();
            AddSpecial(specials, special1Owned, "exp.energy.speed-special1", special1Cost,
                20, headroom, spendableExp);
            AddSpecial(specials, special2Owned, "exp.energy.speed-special2", special2Cost,
                30, headroom, spendableExp);
            AddSpecial(specials, special3Owned, "exp.energy.speed-special3", special3Cost,
                40, headroom, spendableExp);
            var special = specials.OrderByDescending(x => x.ProductiveGain / x.ExactCost)
                .ThenBy(x => x.ExactCost).ThenBy(x => x.DescriptorKey,
                    StringComparer.Ordinal).FirstOrDefault();
            if (special != null) return special;

            // +1.0 and ten +0.1 atoms have the same pre-50 EXP/unit price. Use +1.0 only when it
            // is already funded and cannot overshoot the productive cap; otherwise the cheaper
            // +0.1 atom begins compounding immediately without reserving an arbitrary bundle.
            if (currentSpeed <= 49.000001 && speed100Cost > 0L
                && speed100Cost <= spendableExp)
                return Choice("exp.energy.speed100", speed100Cost, 100, headroom, spendableExp);
            return speed10Cost > 0L
                ? Choice("exp.energy.speed10", speed10Cost, 10, headroom, spendableExp) : null;
        }

        internal static bool TryMagicDiscreteBreakpoint(double baseSpeed, double totalSpeed,
            long bars, double currentRate, int maximumAtoms, out int atoms,
            out double projectedRate)
        {
            atoms = 0;
            projectedRate = 0.0;
            if (!Finite(baseSpeed) || !Finite(totalSpeed) || !Finite(currentRate)
                || baseSpeed <= 0.0 || totalSpeed <= 0.0 || bars <= 0L
                || maximumAtoms <= 0 || baseSpeed >= 49.91)
                return false;
            var multiplier = totalSpeed / baseSpeed;
            for (var n = 1; n <= maximumAtoms && baseSpeed + .1 * n <= 50.001; n++)
            {
                var futureSpeed = Math.Min(50.0, totalSpeed + .1 * n * multiplier);
                var futureRate = 50.0 / Math.Ceiling(50.0 / futureSpeed) * bars;
                if (futureRate <= currentRate + 1e-6) continue;
                atoms = n;
                projectedRate = futureRate;
                return true;
            }
            return false;
        }

        internal static bool FightBossGateOutranksPermanent(bool isForwardNewRecord,
            double naturalRolloutSeconds, double purchasedKillSeconds, long gateCost,
            double permanentNormalizedLevel, double permanentNormalizedStep,
            long permanentCost, out double gateScore, out double permanentScore)
        {
            gateScore = 0.0;
            permanentScore = 0.0;
            if (!isForwardNewRecord || !Finite(naturalRolloutSeconds)
                || !Finite(purchasedKillSeconds) || naturalRolloutSeconds <= purchasedKillSeconds
                || purchasedKillSeconds < 0.0 || purchasedKillSeconds > 120.0 || gateCost <= 0L)
                return false;
            var secondsSaved = naturalRolloutSeconds - purchasedKillSeconds;
            gateScore = Math.Log(1.0 + secondsSaved
                / Math.Max(60.0, purchasedKillSeconds)) / gateCost;
            if (permanentCost > 0L && permanentNormalizedLevel > 0.0
                && permanentNormalizedStep > 0.0 && Finite(permanentNormalizedLevel)
                && Finite(permanentNormalizedStep))
                permanentScore = Math.Log(1.0 + permanentNormalizedStep
                    / permanentNormalizedLevel) / permanentCost;
            return gateScore > permanentScore;
        }

        private static void AddSpecial(ICollection<EnergySpeedPurchaseChoice> candidates,
            bool owned, string key, long cost, int deltaHundredths, double headroom,
            long spendable)
        {
            if (!owned && cost > 0L)
                candidates.Add(Choice(key, cost, deltaHundredths, headroom, spendable));
        }

        private static EnergySpeedPurchaseChoice Choice(string key, long cost,
            int deltaHundredths, double headroom, long spendable)
        {
            return new EnergySpeedPurchaseChoice
            {
                DescriptorKey = key,
                ExactCost = cost,
                DeltaHundredths = deltaHundredths,
                FullyFunded = cost <= Math.Max(0L, spendable),
                ProductiveGain = Math.Min(headroom, deltaHundredths / 100.0)
            };
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
