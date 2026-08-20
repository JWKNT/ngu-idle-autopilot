using System;
using System.Collections.Generic;
using System.Linq;

/*
FILE PURPOSE

EnergyPortfolioOptimizer is the controller-free policy kernel for Energy allocation.  The game
controllers still own exact caps, completion-time checks, native mutations, and rollback; this
kernel replaces serialized list order with a small event auction.  A candidate describes what its
next admitted event helps (Fight Boss, Adventure, Gold, or permanent growth), whether that event is
a proven progression gate, and whether its gain survives rebirth.  The kernel groups equal-value
events and returns a deterministic order so the live adapter can offer each group the remaining
Energy, then move to the next group only after the better events decline or hit native caps.

This is deliberately not a claim that unlike game multipliers have an exact universal exchange
rate.  Exact binary gates are ordered before heuristic downstream growth; persistent work outranks
unrelated reset-local work; and every uncertain candidate remains below an objective-compatible
one.  The separation keeps the policy testable without loading Unity or mutating a save.
*/
namespace NGUInjector.Autopilot
{
    internal enum EnergyPortfolioObjective
    {
        FightBoss,
        Adventure,
        Gold,
        PermanentGrowth
    }

    internal enum EnergyPortfolioSink
    {
        BasicTraining,
        AdvancedTraining,
        Augment,
        TimeMachine,
        Wandoos,
        Ngu,
        Wish,
        Unknown
    }

    internal sealed class EnergyPortfolioCandidate
    {
        internal readonly string Key;
        internal readonly EnergyPortfolioSink Sink;
        internal readonly int Index;
        internal readonly bool ExactGate;
        internal readonly bool Persistent;
        internal readonly int OriginalOrder;

        internal EnergyPortfolioCandidate(string key, EnergyPortfolioSink sink, int index,
            bool exactGate, bool persistent, int originalOrder)
        {
            Key = key ?? string.Empty;
            Sink = sink;
            Index = index;
            ExactGate = exactGate;
            Persistent = persistent;
            OriginalOrder = originalOrder;
        }
    }

    internal sealed class EnergyPortfolioRankedCandidate
    {
        internal readonly EnergyPortfolioCandidate Candidate;
        internal readonly int Tier;
        internal readonly string Reason;

        internal EnergyPortfolioRankedCandidate(EnergyPortfolioCandidate candidate, int tier,
            string reason)
        {
            Candidate = candidate;
            Tier = tier;
            Reason = reason ?? string.Empty;
        }
    }

    internal static class EnergyPortfolioOptimizer
    {
        internal static EnergyPortfolioObjective ChooseObjective(bool activeChallengeBossGate,
            bool selectedBossBlocked, bool dueTitanOrItopodPush, bool exactGoldGate,
            bool adventurePlan)
        {
            if (activeChallengeBossGate)
                return EnergyPortfolioObjective.FightBoss;
            if (exactGoldGate) return EnergyPortfolioObjective.Gold;
            if (dueTitanOrItopodPush || adventurePlan)
                return EnergyPortfolioObjective.Adventure;
            if (selectedBossBlocked) return EnergyPortfolioObjective.FightBoss;
            return EnergyPortfolioObjective.PermanentGrowth;
        }

        internal static double AdvancedTrainingBonus(long level)
        {
            return 1.0 + 0.1 * Math.Pow(Math.Max(0L, level), 0.4);
        }

        internal static long AdvancedTrainingLevelForRelativeGain(long currentLevel,
            double relativeGain)
        {
            if (double.IsNaN(relativeGain) || double.IsInfinity(relativeGain)
                || relativeGain <= 0.0) return currentLevel;
            var requiredBonus = AdvancedTrainingBonus(currentLevel) * (1.0 + relativeGain);
            var solved = Math.Pow((requiredBonus - 1.0) / 0.1, 2.5);
            if (double.IsNaN(solved) || double.IsInfinity(solved) || solved >= long.MaxValue)
                return long.MaxValue;
            return Math.Max(currentLevel + 1L, (long)Math.Ceiling(solved));
        }

        internal static IList<EnergyPortfolioRankedCandidate> Rank(
            EnergyPortfolioObjective objective, IEnumerable<EnergyPortfolioCandidate> candidates)
        {
            if (candidates == null) throw new ArgumentNullException("candidates");
            return candidates.Select(x => RankOne(objective, x))
                .OrderBy(x => x.Tier)
                .ThenBy(x => x.Candidate.OriginalOrder)
                .ThenBy(x => x.Candidate.Key, StringComparer.Ordinal)
                .ToList();
        }

        internal static EnergyPortfolioRankedCandidate RankOne(
            EnergyPortfolioObjective objective, EnergyPortfolioCandidate candidate)
        {
            if (candidate == null) throw new ArgumentNullException("candidate");

            // These candidates have already passed a source-specific completion, horizon, and
            // payoff test in the live adapter.  They are discrete blockers, not percentage bets.
            if (candidate.ExactGate)
                return new EnergyPortfolioRankedCandidate(candidate, 0,
                    "source-proved finite progression gate");

            var tier = ObjectiveTier(objective, candidate.Sink, candidate.Index);
            // A permanent gain is preferable to reset-local work when neither directly advances
            // the current objective.  Do not apply the premium to an objective-compatible event:
            // a needed Boss or Adventure gate is allowed to beat slow compounding.
            if (candidate.Persistent && tier >= 40) tier -= 5;
            return new EnergyPortfolioRankedCandidate(candidate, tier,
                ObjectiveReason(objective, candidate.Sink, candidate.Persistent));
        }

        private static int ObjectiveTier(EnergyPortfolioObjective objective,
            EnergyPortfolioSink sink, int index)
        {
            switch (objective)
            {
                case EnergyPortfolioObjective.FightBoss:
                    if (sink == EnergyPortfolioSink.Augment) return 10;
                    if (sink == EnergyPortfolioSink.BasicTraining) return 15;
                    if (sink == EnergyPortfolioSink.Wandoos) return 25;
                    if (sink == EnergyPortfolioSink.TimeMachine) return 30;
                    if (sink == EnergyPortfolioSink.Ngu) return 40;
                    if (sink == EnergyPortfolioSink.Wish) return 45;
                    if (sink == EnergyPortfolioSink.AdvancedTraining) return 60;
                    return 90;

                case EnergyPortfolioObjective.Adventure:
                    if (sink == EnergyPortfolioSink.AdvancedTraining) return 10;
                    // Energy NGU 4 is Adventure, 6 is Drop Chance, and 8 is PP.  These directly
                    // tighten the Adventure/ITOPOD progression loop and retain their levels.
                    if (sink == EnergyPortfolioSink.Ngu && (index == 4 || index == 6 || index == 8))
                        return 15;
                    if (sink == EnergyPortfolioSink.Wish) return 20;
                    if (sink == EnergyPortfolioSink.Ngu) return 25;
                    if (sink == EnergyPortfolioSink.TimeMachine) return 35;
                    if (sink == EnergyPortfolioSink.BasicTraining) return 45;
                    if (sink == EnergyPortfolioSink.Wandoos) return 50;
                    if (sink == EnergyPortfolioSink.Augment) return 55;
                    return 90;

                case EnergyPortfolioObjective.Gold:
                    if (sink == EnergyPortfolioSink.TimeMachine) return 10;
                    // Normal Energy NGU 3 is the direct Gold multiplier row.
                    if (sink == EnergyPortfolioSink.Ngu && index == 3) return 15;
                    if (sink == EnergyPortfolioSink.Wandoos) return 25;
                    if (sink == EnergyPortfolioSink.Ngu || sink == EnergyPortfolioSink.Wish)
                        return 35;
                    if (sink == EnergyPortfolioSink.Augment) return 45;
                    if (sink == EnergyPortfolioSink.BasicTraining) return 50;
                    if (sink == EnergyPortfolioSink.AdvancedTraining) return 55;
                    return 90;

                default:
                    if (sink == EnergyPortfolioSink.Wish) return 10;
                    if (sink == EnergyPortfolioSink.Ngu) return 15;
                    if (sink == EnergyPortfolioSink.BasicTraining) return 30;
                    if (sink == EnergyPortfolioSink.AdvancedTraining) return 35;
                    if (sink == EnergyPortfolioSink.TimeMachine) return 45;
                    if (sink == EnergyPortfolioSink.Wandoos) return 50;
                    if (sink == EnergyPortfolioSink.Augment) return 55;
                    return 90;
            }
        }

        private static string ObjectiveReason(EnergyPortfolioObjective objective,
            EnergyPortfolioSink sink, bool persistent)
        {
            return (persistent ? "persistent " : "reset-local ") + sink
                   + " event ranked for " + objective;
        }
    }
}
