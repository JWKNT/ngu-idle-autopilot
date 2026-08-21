using System;
using System.Globalization;
using NGUInjector.AllocationProfiles.RebirthStuff;

/*
FILE PURPOSE

Purpose: This file supplies the first production challenge route-bound model. It turns a native
successful challenge time, or the freshly observed unrestricted ordinary run for Basic, into a
cautious replay budget only when the next route has the same source target and restrictions, the
bot has already reached that target in the current run, and an ordinary rebirth checkpoint is due
anyway. It also owns the general reward-payback catalog for the audited Normal challenge wave.

Mechanism: ChallengeHistoricalRoutePolicy is controller-free. It checks route identity, inflates
the best comparable clear evidence by 50 percent plus five minutes, reserves a separately inflated
current-frontier recovery leg, adds the complete Titan-clock vector, and compares that cost with
conservative time saved by the next permanent reward over a 180-day planning horizon. Basic has no
restrictions, so its current ordinary run is a stronger comparable upper bound than a stale native
high score; restricted routes use their own native successful time. LiveChallengeRouteBoundModel
captures the installed challenge high score, current plan, target, and current run on the Unity
thread, then emits the immutable proof fields consumed by ChallengeRouteProofProducer.

Inputs and outputs: Inputs are the current Character, installed AutopilotPlan, exact reset snapshot,
native bestTime, completion ordinal/target, and Titan opportunity seconds. Output is either a
SourceAuditedHistoricalReplay bound or a human-readable HOLD. The model performs no mutation.

Invariants and safety: A challenge cannot interrupt a run before its admitted ordinary rebirth is
due. A mere Boss record is not enough: a finite prior successful route with the same target is
required. Growing-target challenges hold until a stronger source/copy-state route model exists.
The model ignores one-time EXP/AP and several special rewards, so its benefit estimate is biased
downward. Its 180-day horizon and system-use shares are strategy assumptions, not native formulas;
telemetry labels them as modeled historical replay. Native reset/root/postcondition gates remain
independent and are rechecked after this read-only proof.

Extension points and non-goals: Add exact reward fractions or a deterministic copied-state model
here without weakening the common mutation boundary. Special challenges, Laser soft resets,
growing-target extrapolation, and difficulty transitions are deliberately not authorized here.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class ChallengeHistoricalRouteInput
    {
        internal ChallengeType Type;
        internal int CompletedBefore;
        internal int ExactTarget;
        internal int PreviousTarget = -1;
        internal int CurrentBossId;
        internal int HighestBossId;
        internal int HistoricalBestSeconds = int.MaxValue;
        internal double CurrentRunSeconds;
        internal long TitanOpportunitySeconds;
        internal bool OrdinaryCheckpointDue;
        internal double CurrentAttackNumber;
        internal double CurrentDefenseNumber;
    }

    internal sealed class ChallengeHistoricalRouteDecision
    {
        internal bool Admitted;
        internal double ClearUpperSeconds = -1.0;
        internal double RecoveryUpperSeconds = -1.0;
        internal double RewardTimeSavedBudgetSeconds = -1.0;
        internal double EffectivePermanentSpeedFraction;
        internal string Reason = string.Empty;
    }

    internal static class ChallengeHistoricalRoutePolicy
    {
        internal const double PlanningHorizonSeconds = 180.0 * 86400.0;

        internal static ChallengeHistoricalRouteDecision Evaluate(
            ChallengeHistoricalRouteInput input)
        {
            var result = new ChallengeHistoricalRouteDecision();
            if (input == null) return Hold(result, "historical route input is missing");
            if (!input.OrdinaryCheckpointDue)
                return Hold(result, "wait for the already-selected ordinary rebirth checkpoint");
            if (input.CompletedBefore <= 0)
                return Hold(result, "no successful route exists for this challenge yet");
            if (input.PreviousTarget != input.ExactTarget)
                return Hold(result, "the next target grew; the prior completion is not a comparable route");
            if (input.HistoricalBestSeconds <= 0
                || input.HistoricalBestSeconds == int.MaxValue)
                return Hold(result, "native successful-route time is unavailable");
            if (input.CurrentBossId <= input.ExactTarget
                || input.HighestBossId < input.CurrentBossId)
                return Hold(result, "the current run has not freshly reached the challenge target");
            if (!FinitePositive(input.CurrentRunSeconds)
                || !FinitePositive(input.CurrentAttackNumber)
                || !FinitePositive(input.CurrentDefenseNumber)
                || input.TitanOpportunitySeconds < 0L)
                return Hold(result, "run, Number, or Titan cost is unavailable");
            var fraction = EffectivePermanentSpeedFraction(input.Type,
                input.CompletedBefore);
            if (!FinitePositive(fraction))
                return Hold(result, "the next permanent reward has no audited time value yet");

            // Basic disables nothing, so the ordinary run that just reached its target is a
            // source-comparable clear even when the native historical best came from a much older,
            // weaker save. Restricted challenges must use their own successful route instead.
            var observedOrdinaryUpper = input.CurrentRunSeconds * 1.5 + 300.0;
            var clearEvidence = input.Type == ChallengeType.Basic
                ? observedOrdinaryUpper
                : input.HistoricalBestSeconds * 1.5 + 300.0;
            var clear = Math.Max(3600.0, clearEvidence);
            var recovery = Math.Max(3600.0, observedOrdinaryUpper);
            var total = clear + recovery + input.TitanOpportunitySeconds;
            var budget = PlanningHorizonSeconds * fraction;
            result.ClearUpperSeconds = clear;
            result.RecoveryUpperSeconds = recovery;
            result.RewardTimeSavedBudgetSeconds = budget;
            result.EffectivePermanentSpeedFraction = fraction;
            if (!FinitePositive(total) || total + 1e-12 >= budget)
                return Hold(result, "modeled replay+recovery " + Seconds(total)
                    + " does not repay within the 180-day reward budget " + Seconds(budget));
            result.Admitted = true;
            var basis = input.Type == ChallengeType.Basic
                ? "fresh unrestricted run"
                : "same-target native success";
            result.Reason = basis + " replayed with 50%+5m margin; clear "
                            + Seconds(clear) + ", recovery " + Seconds(recovery)
                            + ", Titan loss " + Seconds(input.TitanOpportunitySeconds)
                            + " < conservative permanent-reward budget " + Seconds(budget);
            return result;
        }

        internal static double EffectivePermanentSpeedFraction(ChallengeType type,
            int completedBefore)
        {
            // Fractions deliberately count only a conservative share of the installed source
            // reward. Immediate EXP/AP, final unlocks, and most convenience rewards are ignored.
            switch (type)
            {
                case ChallengeType.Basic:
                    return .05 / 1.05 * .35;
                case ChallengeType.NoAug:
                    return .25 / 1.25 * .25;
                case ChallengeType.NoEquip:
                    return .01;
                case ChallengeType.Blind:
                    return .01 / 1.01 * .10;
                case ChallengeType.NoNGU:
                    return .05 / 1.05 * .50;
                case ChallengeType.NoTimeMachine:
                    return 1.0 / 2.0 * .25;
                default:
                    return 0.0;
            }
        }

        private static ChallengeHistoricalRouteDecision Hold(
            ChallengeHistoricalRouteDecision result, string reason)
        {
            result.Reason = reason ?? string.Empty;
            return result;
        }

        private static bool FinitePositive(double value)
        {
            return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string Seconds(double value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture) + "s";
        }
    }

    internal sealed class LiveChallengeRouteBoundModel : IChallengeRouteBoundModel
    {
        private readonly Character _character;
        private readonly Func<AutopilotPlan> _plan;

        internal LiveChallengeRouteBoundModel(Character character,
            Func<AutopilotPlan> plan)
        {
            if (character == null) throw new ArgumentNullException("character");
            if (plan == null) throw new ArgumentNullException("plan");
            _character = character;
            _plan = plan;
        }

        public ChallengeRouteBoundResult Evaluate(ChallengeRouteModelInput input)
        {
            if (input == null || input.Reset == null || input.Reset.Number == null)
                return ChallengeRouteBoundResult.Unavailable("fresh reset snapshot is missing");
            if (!ReferenceEquals(Main.Character, _character)
                || _character.rebirthTime == null)
                return ChallengeRouteBoundResult.Unavailable("live Character changed");
            var plan = _plan();
            var due = plan != null && plan.RebirthSeconds >= 0
                      && !plan.RebirthExecutionHold && !plan.RebirthBoundaryHold
                      && _character.rebirthTime.totalseconds + 1e-12
                         >= plan.RebirthSeconds;
            int best;
            try { best = BestTime(_character, input.Type); }
            catch (Exception error)
            {
                return ChallengeRouteBoundResult.Unavailable("native successful-route capture failed: "
                    + error.GetType().Name + ": " + error.Message);
            }
            if (input.CompletedBefore >= Maximum(_character, input.Type))
                return ChallengeRouteBoundResult.Unavailable("challenge is already complete");
            var previous = input.CompletedBefore <= 0 ? -1
                : ChallengeMechanics.ExactTarget(input.Type, input.CompletedBefore - 1);
            var policy = ChallengeHistoricalRoutePolicy.Evaluate(
                new ChallengeHistoricalRouteInput
                {
                    Type = input.Type, CompletedBefore = input.CompletedBefore,
                    ExactTarget = input.ExactTarget, PreviousTarget = previous,
                    CurrentBossId = input.Reset.BossId,
                    HighestBossId = input.Reset.HighestBoss,
                    HistoricalBestSeconds = best,
                    CurrentRunSeconds = input.Reset.RebirthSeconds,
                    TitanOpportunitySeconds = input.TitanOpportunitySeconds,
                    OrdinaryCheckpointDue = due,
                    CurrentAttackNumber = input.Reset.Number.CurrentAttack,
                    CurrentDefenseNumber = input.Reset.Number.CurrentDefense
                });
            if (!policy.Admitted)
                return ChallengeRouteBoundResult.Unavailable(policy.Reason);
            return new ChallengeRouteBoundResult
            {
                ModelComplete = true,
                Provenance = ChallengeRouteBoundProvenance.SourceAuditedHistoricalReplay,
                ClearUpperSeconds = policy.ClearUpperSeconds,
                RecoveryUpperSeconds = policy.RecoveryUpperSeconds,
                ForegoneRebirthOpportunityUpperSeconds = 0.0,
                ContinuationLowerBoundSeconds = policy.RewardTimeSavedBudgetSeconds,
                RecoveredBossId = input.Reset.BossId,
                RecoveredAttackNumberLowerBound = 1.0,
                RecoveredDefenseNumberLowerBound = 1.0,
                NumberReplacementPriced = true,
                ObjectiveSignature = "180-day conservative permanent-reward payback",
                StartStateSignature = ChallengeStrategyPlanner.OpportunityProgressionFingerprint(
                    input.Reset),
                AllocationSignature = "installed-autopilot-plan|"
                    + (plan == null ? "missing" : plan.Signature(_character)),
                ResetSequenceSignature = "hard-reset|historical-same-target|ordinary-boundary-due",
                Evidence = policy.Reason
            };
        }

        internal static string PreviewNext(Character c, AutopilotPlan plan)
        {
            if (c == null || plan == null || c.challenges == null
                || c.challenges.inChallenge || c.rebirthTime == null)
                return string.Empty;
            TitanVectorCost titan;
            string titanEvidence;
            if (!ChallengeStrategyPlanner.TryCaptureTitanVector(c, out titan,
                    out titanEvidence) || titan == null)
                return "replay model: " + titanEvidence;
            var reset = LiveResetSnapshot.Capture(c);
            if (reset == null || reset.Number == null)
                return "replay model: reset snapshot unavailable";
            var due = plan.RebirthSeconds >= 0 && !plan.RebirthExecutionHold
                      && !plan.RebirthBoundaryHold
                      && c.rebirthTime.totalseconds + 1e-12 >= plan.RebirthSeconds;
            var types = new[]
            {
                ChallengeType.Basic, ChallengeType.NoAug, ChallengeType.NoEquip,
                ChallengeType.Blind, ChallengeType.NoNGU, ChallengeType.NoTimeMachine
            };
            foreach (var type in types)
            {
                var completed = ChallengeStrategyPlanner.CurrentCompletions(c, type);
                if (completed <= 0 || completed >= Maximum(c, type)) continue;
                var target = ChallengeMechanics.ExactTarget(type, completed);
                var decision = ChallengeHistoricalRoutePolicy.Evaluate(
                    new ChallengeHistoricalRouteInput
                    {
                        Type = type, CompletedBefore = completed, ExactTarget = target,
                        PreviousTarget = ChallengeMechanics.ExactTarget(type, completed - 1),
                        CurrentBossId = reset.BossId, HighestBossId = reset.HighestBoss,
                        HistoricalBestSeconds = BestTime(c, type),
                        CurrentRunSeconds = reset.RebirthSeconds,
                        TitanOpportunitySeconds = titan.TotalCycleDelaySeconds,
                        OrdinaryCheckpointDue = due,
                        CurrentAttackNumber = reset.Number.CurrentAttack,
                        CurrentDefenseNumber = reset.Number.CurrentDefense
                    });
                return ChallengeMechanics.Code(type) + " repeat " + completed + "/"
                       + Maximum(c, type) + ", native best " + BestTime(c, type)
                       + "s: " + decision.Reason;
            }
            return "no completed same-target challenge is currently eligible for replay";
        }

        internal static int BestTime(Character c, ChallengeType type)
        {
            switch (type)
            {
                case ChallengeType.Basic: return c.challenges.basicChallenge.bestTime;
                case ChallengeType.NoAug: return c.challenges.noAugsChallenge.bestTime;
                case ChallengeType.NoEquip: return c.challenges.noEquipmentChallenge.bestTime;
                case ChallengeType.Blind: return c.challenges.blindChallenge.bestTime;
                case ChallengeType.NoNGU: return c.challenges.nguChallenge.bestTime;
                case ChallengeType.NoTimeMachine: return c.challenges.timeMachineChallenge.bestTime;
                default: return int.MaxValue;
            }
        }

        private static int Maximum(Character c, ChallengeType type)
        {
            switch (type)
            {
                case ChallengeType.Basic: return c.allChallenges.basicChallenge.maxCompletions;
                case ChallengeType.NoAug: return c.allChallenges.noAugsChallenge.maxCompletions;
                case ChallengeType.NoEquip: return c.allChallenges.noEquipmentChallenge.maxCompletions;
                case ChallengeType.Blind: return c.allChallenges.blindChallenge.maxCompletions;
                case ChallengeType.NoNGU: return c.allChallenges.NGUChallenge.maxCompletions;
                case ChallengeType.NoTimeMachine: return c.allChallenges.timeMachineChallenge.maxCompletions;
                default: return 0;
            }
        }
    }
}
