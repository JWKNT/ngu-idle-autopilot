/*
FILE PURPOSE

Purpose: ResourceHorizonModel is the read-only, chronological Gold/Blood planning boundary. It
models current stock, one net or gross production stream, exact 20 ms bar events, liquidity gates,
actual debits, Blood completions, optional Money Pit/Digger bundles, Counterfeit feedback, Gold-drop
records, and ordinary-versus-challenge Time Machine banking.

Mechanism: Pure GoldEventLedger and GoldMechanics APIs order events by timestamp and native offline
phase, accrue production once, check undiscounted liquidity before applying discounted debits, and
preserve native early-return behavior within each offline subsystem. The live adapter snapshots
Character state, projects exact ritual charge/completion ticks through ExactResourceAllocator, and
emits actor-owned spend bundles; it never invokes a controller.

Inputs and outputs: Inputs are immutable event records or live Character fields including Gold,
gross/net GPS, Digger drain, bar progress/allocation, Blood pools, Pit tiers, base Gold, and Time
Machine levels. Outputs are ledger feasibility/projection records, claims, selected bundle IDs,
banked-level projections, and explanatory telemetry.

Invariants and safety: Future production is counted exactly once. Online bars complete at most once
per native tick and discard overfill. A ritual with progress greater than zero is already charged;
Ritual.paid is deliberately never read. Liquidity and debit are distinct, all-Gold events zero the
post-event stock, an unreachable Blood target creates no charge claim, and offline Augment/Blood/TM
early returns starve only the remainder of their native subsystem in audited source order.

Extension points and non-goals: Global route value may rank the typed optional bundles later. This
file provides exact local transitions and conservative current-policy selection only; it does not
allocate, buy, cast, toss, mutate a loadout, predict RNG outcomes as certain, or authorize a reset.
*/
using System;
using System.Collections.Generic;
using System.Linq;
#if !GOLD_LEDGER_TESTS
using NGUInjector.Managers;
#endif

namespace NGUInjector.Autopilot
{
    internal enum GoldLedgerPhase
    {
        Online = 0,
        OfflineGoldCredit = 10,
        OfflineAugments = 20,
        OfflineBlood = 30,
        OfflineTimeMachine = 40
    }

    internal sealed class GoldLedgerEvent
    {
        internal string Id = string.Empty;
        internal string Label = string.Empty;
        internal double Seconds;
        internal GoldLedgerPhase Phase;
        internal int Sequence;
        internal double RequiredLiquidity;
        internal double Debit;
        internal double Credit;
        internal double NetRateAfter = double.NaN;
        internal bool SpendAll;
        internal bool AbortOfflinePhaseOnFailure;

        internal static GoldLedgerEvent Charge(string id, string label, double seconds,
            double requiredLiquidity, double debit, int sequence)
        {
            return new GoldLedgerEvent
            {
                Id = id ?? string.Empty,
                Label = label ?? string.Empty,
                Seconds = Math.Max(0.0, seconds),
                Phase = GoldLedgerPhase.Online,
                Sequence = sequence,
                RequiredLiquidity = Math.Max(0.0, requiredLiquidity),
                Debit = Math.Max(0.0, debit)
            };
        }

        internal static GoldLedgerEvent Offline(string id, GoldLedgerPhase phase, int sequence,
            double requiredLiquidity, double debit, double credit)
        {
            if (phase == GoldLedgerPhase.Online)
                throw new ArgumentException("An offline event requires an offline phase.", "phase");
            return new GoldLedgerEvent
            {
                Id = id ?? string.Empty,
                Label = id ?? string.Empty,
                Seconds = 0.0,
                Phase = phase,
                Sequence = sequence,
                RequiredLiquidity = Math.Max(0.0, requiredLiquidity),
                Debit = Math.Max(0.0, debit),
                Credit = Math.Max(0.0, credit),
                AbortOfflinePhaseOnFailure = phase != GoldLedgerPhase.OfflineGoldCredit
            };
        }
    }

    internal sealed class GoldLedgerResult
    {
        internal bool Feasible = true;
        internal double FinalGold;
        internal double FinalNetRate;
        internal string FirstBlockedEventId = string.Empty;
        internal string Reason = string.Empty;
        internal readonly List<string> AppliedEventIds = new List<string>();
        internal readonly List<string> SkippedEventIds = new List<string>();
    }

    internal sealed class GoldSpendBundle
    {
        internal GoldClaimKind Kind;
        internal string ActionId = string.Empty;
        internal string Label = string.Empty;
        internal int ActorId = -1;
        internal double AtSeconds;
        internal double RequiredLiquidity;
        internal double Debit;
        internal double ValueScore;
        internal bool SpendAll;

        internal GoldLedgerEvent ToLedgerEvent(int sequence)
        {
            var result = GoldLedgerEvent.Charge(ActionId, Label, AtSeconds,
                RequiredLiquidity, Debit, sequence);
            result.SpendAll = SpendAll;
            return result;
        }
    }

    internal static class GoldEventLedger
    {
        private const double BalanceTolerance = 1e-9;

        internal static GoldLedgerResult Evaluate(double startingGold, double netGoldPerSecond,
            double horizonSeconds, IEnumerable<GoldLedgerEvent> source)
        {
            var result = new GoldLedgerResult
            {
                FinalGold = Math.Max(0.0, startingGold),
                FinalNetRate = Math.Max(0.0, netGoldPerSecond)
            };
            var horizon = Math.Max(0.0, horizonSeconds);
            var events = (source ?? Enumerable.Empty<GoldLedgerEvent>())
                .Where(x => x != null && x.Seconds <= horizon + 1e-12)
                .OrderBy(x => x.Seconds)
                .ThenBy(x => (int)x.Phase)
                .ThenBy(x => x.Sequence)
                .ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
            var elapsed = 0.0;
            GoldLedgerPhase? abortedOfflinePhase = null;
            foreach (var item in events)
            {
                var eventSeconds = Math.Max(elapsed, Math.Max(0.0, item.Seconds));
                if (eventSeconds > elapsed)
                {
                    result.FinalGold += result.FinalNetRate * (eventSeconds - elapsed);
                    elapsed = eventSeconds;
                }
                if (abortedOfflinePhase.HasValue
                    && item.Phase == abortedOfflinePhase.Value)
                {
                    result.SkippedEventIds.Add(item.Id);
                    continue;
                }
                if (abortedOfflinePhase.HasValue
                    && item.Phase != abortedOfflinePhase.Value)
                    abortedOfflinePhase = null;

                result.FinalGold += Math.Max(0.0, item.Credit);
                var tolerance = Math.Max(BalanceTolerance,
                    Math.Max(result.FinalGold, item.RequiredLiquidity) * 1e-12);
                if (result.FinalGold + tolerance < item.RequiredLiquidity)
                {
                    result.SkippedEventIds.Add(item.Id);
                    if (item.Phase != GoldLedgerPhase.Online
                        && item.AbortOfflinePhaseOnFailure)
                    {
                        abortedOfflinePhase = item.Phase;
                        continue;
                    }
                    result.Feasible = false;
                    result.FirstBlockedEventId = item.Id;
                    result.Reason = "Insufficient liquidity before " + item.Id;
                    break;
                }
                if (item.Debit > item.RequiredLiquidity + tolerance
                    || result.FinalGold + tolerance < item.Debit)
                {
                    result.Feasible = false;
                    result.FirstBlockedEventId = item.Id;
                    result.Reason = "Invalid or unaffordable debit at " + item.Id;
                    break;
                }
                result.FinalGold = item.SpendAll ? 0.0
                    : Math.Max(0.0, result.FinalGold - item.Debit);
                if (!double.IsNaN(item.NetRateAfter))
                    result.FinalNetRate = Math.Max(0.0, item.NetRateAfter);
                result.AppliedEventIds.Add(item.Id);
            }
            if (result.Feasible && elapsed < horizon)
                result.FinalGold += result.FinalNetRate * (horizon - elapsed);
            return result;
        }

        internal static GoldSpendBundle SelectBestBundle(
            IEnumerable<GoldSpendBundle> bundles, double reachableGold)
        {
            return (bundles ?? Enumerable.Empty<GoldSpendBundle>())
                .Where(x => x != null && x.RequiredLiquidity >= 0.0 && x.Debit >= 0.0
                            && x.RequiredLiquidity <= Math.Max(0.0, reachableGold))
                .OrderByDescending(x => x.ValueScore)
                .ThenBy(x => x.Debit)
                .ThenBy(x => x.ActionId, StringComparer.Ordinal)
                .FirstOrDefault();
        }
    }

    internal static class GoldMechanics
    {
        internal const double NativeTickSeconds = 0.02;
        internal const double PitThresholdSafetyMargin = 1.000001;

        internal static double GrossUpperBound(double currentGold, double grossGps,
            double horizonSeconds)
        {
            return Math.Max(0.0, currentGold)
                   + Math.Max(0.0, grossGps) * Math.Max(0.0, horizonSeconds);
        }

        internal static bool BarNeedsStartCharge(double progress)
        {
            return !(progress > 0.0);
        }

        internal static long OnlineBarCompletions(double progress, double progressPerTick,
            double horizonSeconds)
        {
            if (horizonSeconds <= 0.0) return 0L;
            var ticksAvailableDouble = Math.Floor(horizonSeconds / NativeTickSeconds + 1e-9);
            if (ticksAvailableDouble <= 0.0) return 0L;
            var ticksAvailable = ticksAvailableDouble >= long.MaxValue
                ? long.MaxValue : (long)ticksAvailableDouble;
            var first = ExactResourceAllocator.NativeCompletionTicks(progress, progressPerTick);
            if (first == long.MaxValue || first > ticksAvailable) return 0L;
            var later = ExactResourceAllocator.NativeCompletionTicks(0.0, progressPerTick);
            if (later == long.MaxValue || later <= 0L) return 1L;
            return 1L + (ticksAvailable - first) / later;
        }

        internal static double CounterfeitBonus(double investedBlood, double minimumBlood)
        {
            if (minimumBlood <= 0.0 || investedBlood < minimumBlood) return 1.0;
            var exponent = Math.Log(investedBlood / minimumBlood, 2.0) + 1.0;
            return 1.0 + 0.01 * Math.Floor(exponent * exponent);
        }

        internal static double NextCounterfeitInvestmentBreakpoint(double investedBlood,
            double minimumBlood)
        {
            if (minimumBlood <= 0.0) return double.PositiveInfinity;
            if (investedBlood < minimumBlood) return minimumBlood;
            var currentStep = Math.Floor(Math.Pow(
                Math.Log(investedBlood / minimumBlood, 2.0) + 1.0, 2.0));
            var next = minimumBlood * Math.Pow(2.0, Math.Sqrt(currentStep + 1.0) - 1.0);
            return Math.Max(minimumBlood, next * (1.0 + 1e-12));
        }

        internal static double ProjectCounterfeitGross(double currentGross,
            double currentBonus, double projectedBonus)
        {
            if (currentGross <= 0.0 || currentBonus <= 0.0 || projectedBonus <= currentBonus)
                return Math.Max(0.0, currentGross);
            return currentGross * projectedBonus / currentBonus;
        }

        internal static double ProjectGoldDropRecord(double currentRecord, double baseZoneGold,
            double totalGoldBonus, int currentFightBossId)
        {
            if (currentFightBossId <= 29 || baseZoneGold <= 0.0 || totalGoldBonus <= 0.0)
                return Math.Max(0.0, currentRecord);
            var expectedDrop = baseZoneGold * 4.5 * totalGoldBonus;
            return Math.Max(Math.Max(0.0, currentRecord), expectedDrop);
        }

        internal static double ProjectGoldDropGross(double currentGross, double currentRecord,
            double projectedRecord)
        {
            if (currentGross <= 0.0 || currentRecord <= 0.0
                || projectedRecord <= currentRecord)
                return Math.Max(0.0, currentGross);
            return currentGross * projectedRecord / currentRecord;
        }

        internal static long ProjectBankedTimeMachineLevel(long completedLevel,
            double bankFraction, bool challengeReset)
        {
            if (challengeReset || completedLevel <= 0L || bankFraction <= 0.0) return 0L;
            var projected = Math.Floor(completedLevel * bankFraction);
            return projected >= long.MaxValue ? long.MaxValue : Math.Max(0L, (long)projected);
        }

        internal static double SafePitThreshold(double nominalThreshold)
        {
            return nominalThreshold <= 0.0 ? 0.0
                : nominalThreshold * PitThresholdSafetyMargin;
        }

        internal static bool NativePitTierReached(double cumulativeGold, int strictLogThreshold)
        {
            if (cumulativeGold <= 0.0) return false;
            var nativeFloat = (float)cumulativeGold;
            var nativeLog10 = (float)Math.Log10(nativeFloat);
            return !float.IsNaN(nativeFloat) && !float.IsInfinity(nativeFloat)
                   && !float.IsNaN(nativeLog10) && !float.IsInfinity(nativeLog10)
                   && Math.Floor(nativeLog10) > strictLogThreshold;
        }
    }

    internal sealed class MoneyPitDeliveryPreflight
    {
        internal bool Admitted;
        internal bool LootyDeliveryDue;
        internal string Reason = string.Empty;

        internal static MoneyPitDeliveryPreflight Evaluate(bool lootyDeliveryDue,
            bool accessoryFilterEnabled, bool exactLootyFilterEnabled,
            bool exactCapacityAdmitted)
        {
            if (!lootyDeliveryDue)
                return new MoneyPitDeliveryPreflight {Admitted = true,
                    Reason = "No Looty delivery occurs on this toss."};
            if (accessoryFilterEnabled || exactLootyFilterEnabled)
                return new MoneyPitDeliveryPreflight {LootyDeliveryDue = true,
                    Reason = "Looty delivery is filtered."};
            if (!exactCapacityAdmitted)
                return new MoneyPitDeliveryPreflight {LootyDeliveryDue = true,
                    Reason = "Looty delivery has no proven usable ordinary slot."};
            return new MoneyPitDeliveryPreflight {Admitted = true, LootyDeliveryDue = true,
                Reason = "Looty filter and usable-slot preconditions are proven."};
        }
    }

    internal enum GoldClaimKind
    {
        AugmentAndTimeMachine = 0,
        BloodSpell = 1,
        MoneyPitPermanentTier = 2,
        DiggerPermanentUpgrade = 3
    }

    internal sealed class GoldClaim
    {
        internal GoldClaimKind Kind;
        internal double Amount;
        internal double RequiredLiquidity;
        internal double AtSeconds;
        internal string ActionId;
        internal string Label;
        internal bool Hard;
    }

    internal sealed class GoldHorizonEvaluation
    {
        internal double BaselineAtRebirth;
        internal double CommittedSpend;
        internal double Shortfall;
        internal double BloodSpend;
        internal double AugmentSpend;
        internal double PermanentSpend;
        internal double PitSpend;
        internal double DiggerSpend;
        internal double OptimisticReach;
        internal double ProjectedGoldAfterHardEvents;
        internal double CounterfeitGoldMultiplier = 1.0;
        internal double ProjectedCounterfeitGross;
        internal double GoldDropExpectedFactor = 4.5;
        internal double NextGoldLevelIncrement;
        internal double NextSpeedLevelIncrement;
        internal long ProjectedBankedSpeedLevels;
        internal long ProjectedBankedGoldMultiLevels;
        internal string TargetName = "no validated pre-rebirth Gold sink";
        internal string Decision = "Gold horizon has not been evaluated";
        internal bool TimeMachineUseful;
        internal bool ChronologicalHardEventsFeasible;
        internal bool GoldDropRecordEligible;
        internal bool TimeMachineBankingAvailable;
        internal string FirstBlockedEventId = string.Empty;
        internal GoldLedgerResult Ledger;
        internal GoldSpendBundle PitBundle;
        internal GoldSpendBundle DiggerBundle;
        internal readonly List<GoldClaim> Claims = new List<GoldClaim>();

        internal double ProtectedSpendBefore(GoldClaimKind actor)
        {
            return Claims.Where(x => x.Amount > 0 && x.Hard && x.Kind != actor)
                .Sum(x => Math.Max(x.Amount, x.RequiredLiquidity));
        }
    }

#if !GOLD_LEDGER_TESTS
    internal static class ResourceHorizonModel
    {
        private static readonly int EndBloodItemId = MechanicsEndgame.AllRequirements()
            .First(x => x.DependencyKind == EndDependencyKind.BloodSpell).ItemId;

        private sealed class RitualEvent
        {
            internal int TrackId;
            internal int CompletionIndex;
            internal double CompletionSeconds;
            internal double ChargeSeconds = -1.0;
            internal double Blood;
            internal double Gold;
        }

        internal static GoldHorizonEvaluation EvaluateGold(Character c, int remainingSeconds)
        {
            var result = new GoldHorizonEvaluation();
            if (c == null)
            {
                result.Decision = "Blocked: no Character snapshot is available";
                return result;
            }
            var horizon = Math.Max(0, remainingSeconds);

            result.BaselineAtRebirth = Math.Max(0.0, c.realGold)
                                       + Math.Max(0.0, c.goldPerSecond()) * horizon;
            result.OptimisticReach = GoldMechanics.GrossUpperBound(c.realGold,
                c.grossGoldPerSecond(), horizon);
            var hardEvents = AugmentAndMachineEvents(c);
            result.AugmentSpend = hardEvents.Sum(x => x.Debit);
            foreach (var item in hardEvents)
                AddClaim(result, GoldClaimKind.AugmentAndTimeMachine, item.Debit,
                    item.RequiredLiquidity, item.Seconds, item.Id, item.Label, true);

            string bloodTarget;
            List<GoldLedgerEvent> bloodEvents;
            result.BloodSpend = ProjectValuedBloodCharges(c, horizon, out bloodTarget,
                out bloodEvents);
            hardEvents.AddRange(bloodEvents);
            foreach (var item in bloodEvents)
                AddClaim(result, GoldClaimKind.BloodSpell, item.Debit,
                    item.RequiredLiquidity, item.Seconds, item.Id, bloodTarget, true);

            result.Ledger = GoldEventLedger.Evaluate(c.realGold, c.goldPerSecond(), horizon,
                hardEvents);
            result.ChronologicalHardEventsFeasible = result.Ledger.Feasible;
            result.ProjectedGoldAfterHardEvents = result.Ledger.FinalGold;
            result.FirstBlockedEventId = result.Ledger.FirstBlockedEventId;

            string pitLabel;
            result.PitSpend = ReachablePitStep(c, horizon, result.OptimisticReach, out pitLabel);
            if (result.PitSpend > 0.0)
                result.PitBundle = new GoldSpendBundle
                {
                    Kind = GoldClaimKind.MoneyPitPermanentTier,
                    ActionId = "money-pit-permanent-tier",
                    Label = pitLabel,
                    AtSeconds = 0.0,
                    RequiredLiquidity = result.PitSpend,
                    Debit = result.PitSpend,
                    ValueScore = 0.0,
                    SpendAll = true
                };
            AddClaim(result, GoldClaimKind.MoneyPitPermanentTier, result.PitSpend, pitLabel, false);

            result.DiggerBundle = DiggerManager.SelectUpgradeBundle(c, result.OptimisticReach);
            result.DiggerSpend = result.DiggerBundle == null ? 0.0 : result.DiggerBundle.Debit;
            AddClaim(result, GoldClaimKind.DiggerPermanentUpgrade, result.DiggerSpend,
                result.DiggerBundle == null ? "next permanent Digger max-level upgrade"
                    : result.DiggerBundle.Label, false);

            // Optional actions are mutually exclusive event bundles, not committed reserves. The
            // current authority may execute only its exact actor bundle; task 28 will compare their
            // terminal values. Counting either here would again make one coin fund two branches.
            result.PermanentSpend = 0.0;
            result.CommittedSpend = result.AugmentSpend + result.BloodSpend + result.PermanentSpend;
            result.Shortfall = Math.Max(0.0, result.CommittedSpend - result.BaselineAtRebirth);

            var target = result.Claims.Where(x => x.Amount > 0)
                .OrderBy(x => x.Kind).FirstOrDefault();
            if (target != null) result.TargetName = target.Label;

            var gross = Math.Max(0.0, c.grossGoldPerSecond());
            result.CounterfeitGoldMultiplier = c.bloodMagicController == null ? 1.0
                : Math.Max(1.0, c.bloodMagicController.goldBonus());
            result.ProjectedCounterfeitGross = gross;
            if (c.bloodMagic != null && c.bloodMagicController != null
                && c.bloodMagicController.spells != null)
            {
                var minimumGoldBlood = c.bloodMagicController.spells.minGoldBlood();
                var nextInvestment = GoldMechanics.NextCounterfeitInvestmentBreakpoint(
                    c.bloodMagic.goldSpellBlood, minimumGoldBlood);
                if (minimumGoldBlood > 0.0 && !double.IsNaN(nextInvestment)
                    && !double.IsInfinity(nextInvestment))
                {
                    var castAmount = Math.Max(minimumGoldBlood,
                        nextInvestment - c.bloodMagic.goldSpellBlood);
                    var projectedBonus = GoldMechanics.CounterfeitBonus(
                        c.bloodMagic.goldSpellBlood + castAmount, minimumGoldBlood);
                    result.ProjectedCounterfeitGross = GoldMechanics.ProjectCounterfeitGross(
                        gross, result.CounterfeitGoldMultiplier, projectedBonus);
                }
            }
            result.GoldDropRecordEligible = c.bossID > 29;
            result.NextGoldLevelIncrement = gross / Math.Max(1.0, c.machine.levelGoldMulti + 1.0);
            var speedLevel = c.machine.levelSpeed;
            var currentSpeed = TimeMachineSpeedFactor(speedLevel);
            var nextSpeed = TimeMachineSpeedFactor(speedLevel + 1);
            result.NextSpeedLevelIncrement = currentSpeed <= 0 ? 0.0
                : gross * Math.Max(0.0, nextSpeed / currentSpeed - 1.0);
            var recoverable = Math.Max(result.NextGoldLevelIncrement,
                result.NextSpeedLevelIncrement) * horizon;
            result.TimeMachineUseful = result.Shortfall > 0 && result.Shortfall <= recoverable;

            var plan = Main.Autopilot == null ? null : Main.Autopilot.Plan;
            var challengeReset = plan != null && plan.ChallengeAdmitted;
            var bankFraction = c.adventureController == null || c.adventureController.itopod == null
                ? 0.0 : c.adventureController.itopod.totalBankedTimeMachine();
            result.TimeMachineBankingAvailable = !challengeReset && bankFraction > 0.0;
            result.ProjectedBankedSpeedLevels = GoldMechanics.ProjectBankedTimeMachineLevel(
                c.machine.levelSpeed, bankFraction, challengeReset);
            result.ProjectedBankedGoldMultiLevels = GoldMechanics.ProjectBankedTimeMachineLevel(
                c.machine.levelGoldMulti, bankFraction, challengeReset);

            if (!result.ChronologicalHardEventsFeasible)
                result.Decision = "Blocked: chronological liquidity fails before "
                                  + result.FirstBlockedEventId;
            else if (result.CommittedSpend <= 0)
                result.Decision = "Blocked: no named Gold sink can complete before rebirth";
            else if (result.Shortfall <= 0)
                result.Decision = "Blocked: baseline Gold already funds " + result.TargetName;
            else if (!result.TimeMachineUseful)
                result.Decision = "Blocked: " + result.TargetName + " is short by "
                                  + FormatGold(result.Shortfall) + " beyond conservative recovery";
            else
                result.Decision = "Allowed: " + result.TargetName + " has a modeled shortfall of "
                                  + FormatGold(result.Shortfall);
            return result;
        }

        private static void AddClaim(GoldHorizonEvaluation result, GoldClaimKind kind,
            double amount, string label, bool hard)
        {
            AddClaim(result, kind, amount, amount, 0.0, kind.ToString(), label, hard);
        }

        private static void AddClaim(GoldHorizonEvaluation result, GoldClaimKind kind,
            double amount, double requiredLiquidity, double atSeconds, string actionId,
            string label, bool hard)
        {
            if (amount <= 0) return;
            result.Claims.Add(new GoldClaim
            {
                Kind = kind,
                Amount = amount,
                RequiredLiquidity = requiredLiquidity,
                AtSeconds = atSeconds,
                ActionId = actionId ?? string.Empty,
                Label = string.IsNullOrEmpty(label) ? kind.ToString() : label,
                Hard = hard
            });
        }

        private static List<GoldLedgerEvent> AugmentAndMachineEvents(Character c)
        {
            var events = new List<GoldLedgerEvent>();
            var reserve = AutopilotManager.RequiredAugmentWorkingCapital(c);
            if (reserve > 0.0)
                events.Add(GoldLedgerEvent.Charge("active-augment-bars",
                    "active Augment/Upgrade start charge", 0.0, reserve, reserve, 0));
            if (c.machine == null || c.timeMachineController == null) return events;
            var discount = Math.Max(0.0, c.totalDiscount());
            if (c.machine.speedEnergy > 0 && GoldMechanics.BarNeedsStartCharge(
                    c.machine.speedProgress))
            {
                var raw = Math.Max(0.0, c.timeMachineController.machineSpeedGoldCost());
                events.Add(GoldLedgerEvent.Charge("time-machine-speed-start",
                    "active Time Machine Speed start charge", 0.0, raw, raw * discount, 1));
            }
            if (c.machine.goldMultiMagic > 0 && GoldMechanics.BarNeedsStartCharge(
                    c.machine.goldMultiProgress))
            {
                var raw = Math.Max(0.0, c.timeMachineController.machineGoldMultiCost());
                events.Add(GoldLedgerEvent.Charge("time-machine-multiplier-start",
                    "active Time Machine Multiplier start charge", 0.0, raw,
                    raw * discount, 2));
            }
            return events;
        }

        private static double ProjectValuedBloodCharges(Character c, int remainingSeconds,
            out string label, out List<GoldLedgerEvent> chargeEvents)
        {
            label = string.Empty;
            chargeEvents = new List<GoldLedgerEvent>();
            double target;
            if (!TryGetValuedBloodTarget(c, remainingSeconds, out target, out label)
                || c.bloodMagic.bloodPoints >= target)
                return 0.0;

            var hold = Main.Autopilot != null && Main.Autopilot.Plan != null
                       && Main.Autopilot.Plan.RebirthExecutionHold;
            var events = ProjectRitualEvents(c, remainingSeconds, hold);
            var missing = target - c.bloodMagic.bloodPoints;
            var gained = 0.0;
            var gold = 0.0;
            var selected = new List<RitualEvent>();
            foreach (var ritualEvent in events.OrderBy(x => x.CompletionSeconds)
                         .ThenBy(x => x.TrackId).ThenBy(x => x.CompletionIndex))
            {
                gold += ritualEvent.Gold;
                gained += ritualEvent.Blood;
                selected.Add(ritualEvent);
                if (gained >= missing) break;
            }
            // Reset-local Blood has no target payoff unless the complete target is reachable.
            if (gained < missing) return 0.0;
            var sequence = 100;
            foreach (var ritualEvent in selected.Where(x => x.Gold > 0.0
                         && x.ChargeSeconds >= 0.0).OrderBy(x => x.ChargeSeconds)
                         .ThenBy(x => x.TrackId).ThenBy(x => x.CompletionIndex))
                chargeEvents.Add(GoldLedgerEvent.Charge(
                    "blood-ritual-" + ritualEvent.TrackId + "-bar-"
                    + ritualEvent.CompletionIndex,
                    label, ritualEvent.ChargeSeconds, ritualEvent.Gold,
                    ritualEvent.Gold, sequence++));
            return Math.Max(0.0, gold);
        }

        private static bool TryGetValuedBloodTarget(Character c, int remainingSeconds,
            out double target, out string label)
        {
            target = 0.0;
            label = string.Empty;
            if (c == null || c.bloodMagic == null || c.bloodSpells == null
                || c.bloodMagicController == null || c.inventory == null || c.buttons == null
                || c.buttons.bloodMagic == null || !c.buttons.bloodMagic.interactable)
                return false;

            if (c.settings.rebirthDifficulty == difficulty.sadistic
                && !EndgameDependencyModel.IsOwned(c, EndBloodItemId))
            {
                target = c.bloodSpells.endSpellBlood();
                label = "END Blood spell for item " + EndBloodItemId;
                return target > 0;
            }

            var guff = c.inventory.macguffins != null
                       && c.inventory.macguffins.Any(x => x != null && x.id > 0);
            if (guff && c.settings.rebirthDifficulty >= difficulty.evil
                && c.adventure.itopod.perkLevel.Count > 73
                && c.adventure.itopod.perkLevel[73] >= 1
                && SpellReadyWithin(c.bloodMagic.macguffin2Time.totalseconds,
                    c.bloodMagicController.spells.macguffin2Cooldown, remainingSeconds))
            {
                target = c.bloodSpells.minMacguffin2Blood();
                label = "permanent Blood MacGuffin beta spell";
                return target > 0;
            }
            if (guff && c.adventure.itopod.perkLevel.Count > 72
                && c.adventure.itopod.perkLevel[72] >= 1
                && SpellReadyWithin(c.bloodMagic.macguffin1Time.totalseconds,
                    c.bloodMagicController.spells.macguffin1Cooldown, remainingSeconds))
            {
                target = c.bloodSpells.minMacguffin1Blood();
                label = "permanent Blood MacGuffin alpha spell";
                return target > 0;
            }
            if (c.bloodMagicController.spells != null && c.grossGoldPerSecond() > 0.0)
            {
                var minimum = c.bloodMagicController.spells.minGoldBlood();
                var nextInvestment = GoldMechanics.NextCounterfeitInvestmentBreakpoint(
                    c.bloodMagic.goldSpellBlood, minimum);
                target = Math.Max(minimum,
                    nextInvestment - c.bloodMagic.goldSpellBlood);
                if (!double.IsNaN(target) && !double.IsInfinity(target) && target > 0.0)
                {
                    var currentBonus = Math.Max(1.0,
                        c.bloodMagicController.goldBonus());
                    var nextBonus = GoldMechanics.CounterfeitBonus(
                        c.bloodMagic.goldSpellBlood + target, minimum);
                    label = "Counterfeit Gold breakpoint " + currentBonus.ToString("0.##")
                            + "x -> " + nextBonus.ToString("0.##") + "x gross GPS";
                    return true;
                }
            }
            if (SpellReadyWithin(c.bloodMagic.adventureSpellTime.totalseconds,
                c.bloodSpells.adventureSpellCooldown, remainingSeconds))
            {
                target = c.bloodSpells.minAdventureBlood();
                label = "permanent Iron Pill spell";
                return target > 0;
            }
            return false;
        }

        internal static bool TryGetValuedBloodDemand(Character c, int remainingSeconds,
            out double target, out string label)
        {
            return TryGetValuedBloodTarget(c, remainingSeconds, out target, out label)
                   && c != null && c.bloodMagic != null && c.bloodMagic.bloodPoints < target;
        }

        private static bool SpellReadyWithin(double elapsed, int cooldown, int remainingSeconds)
        {
            return Math.Max(0.0, cooldown - elapsed) <= remainingSeconds;
        }

        private static List<RitualEvent> ProjectRitualEvents(Character c, int horizon, bool hold)
        {
            var events = new List<RitualEvent>();
            if (c.bloodMagic.ritual == null || c.bloodMagicController.bloodMagics == null)
                return events;
            var unlocked = Math.Min(c.bloodMagicController.ritualsUnlocked(),
                Math.Min(c.bloodMagic.ritual.Count, c.bloodMagicController.bloodMagics.Length));
            var prospectiveIdle = Math.Max(0L, c.magic.idleMagic);
            for (var i = unlocked - 1; i >= 0; i--)
            {
                var track = c.bloodMagic.ritual[i];
                var magic = Math.Max(0L, track.magic);
                if (magic <= 0 && prospectiveIdle > 0)
                {
                    magic = Math.Min(prospectiveIdle, Math.Max(1L,
                        c.bloodMagicController.bloodMagics[i].capValue()));
                    prospectiveIdle -= magic;
                }
                var rate = RitualProgressPerTick(c, i, magic);
                if (rate <= 0) continue;
                var firstTicks = ExactResourceAllocator.NativeCompletionTicks(
                    track.progress, rate);
                var laterTicks = ExactResourceAllocator.NativeCompletionTicks(0.0, rate);
                if (firstTicks == long.MaxValue || laterTicks == long.MaxValue) continue;
                var completions = GoldMechanics.OnlineBarCompletions(
                    track.progress, rate, horizon);
                if (hold) completions = Math.Min(1L, completions);
                // Bound allocation and sort work without inventing completion rate. If a target
                // needs more than this conservative event frontier it is reported unreachable and
                // creates no charge claim; the global scheduler can expand the frontier offline.
                completions = Math.Min(100000L, completions);
                for (var completion = 0L; completion < completions; completion++)
                {
                    var completionTicks = firstTicks + completion * laterTicks;
                    var chargeSeconds = completion == 0L
                        ? (GoldMechanics.BarNeedsStartCharge(track.progress) ? 0.0 : -1.0)
                        : (firstTicks + (completion - 1L) * laterTicks + 1L)
                          * GoldMechanics.NativeTickSeconds;
                    events.Add(new RitualEvent
                    {
                        TrackId = i,
                        CompletionIndex = completion >= int.MaxValue
                            ? int.MaxValue : (int)completion,
                        CompletionSeconds = completionTicks * GoldMechanics.NativeTickSeconds,
                        ChargeSeconds = chargeSeconds,
                        Blood = Math.Max(0.0, c.bloodMagicController.bloodMagics[i].bloodAdded()),
                        Gold = chargeSeconds < 0.0 ? 0.0
                            : Math.Max(0.0,
                                c.bloodMagicController.bloodMagics[i].currentCost())
                    });
                }
            }
            return events;
        }

        private static double RitualProgressPerTick(Character c, int id, long magic)
        {
            if (magic <= 0) return 0;
            double divider;
            if (c.settings.rebirthDifficulty == difficulty.normal)
                divider = 50000.0 * c.bloodMagicController.normalSpeedDividers[id];
            else if (c.settings.rebirthDifficulty == difficulty.evil)
                divider = 50000.0 * c.bloodMagicController.evilSpeedDividers[id];
            else
                divider = c.bloodMagicController.sadisticSpeedDividers[id]
                          * c.bloodMagicController.bloodMagics[id].sadisticDivider();
            return divider <= 0 ? 0 : magic * (double)c.totalMagicPower() / divider
                   * c.bloodMagicController.bloodMagics[id].totalBloodMagicSpeedBonus();
        }

        private static double ReachablePitStep(Character c, int remainingSeconds,
            double baselineGold, out string label)
        {
            label = string.Empty;
            double target;
            if (!c.settings.pitUnlocked || c.pitController == null
                || c.pitController.currentPitTime() - c.pit.pitTime.totalseconds > remainingSeconds
                || !MoneyPitManager.TryGetPermanentTierTarget(out target, out label)
                || target <= 0 || target > baselineGold)
                return 0;
            return target;
        }

        private static double TimeMachineSpeedFactor(long level)
        {
            return Math.Min(50L, Math.Max(1L, level + 1L))
                   * (double)Math.Max(1L, level - 48L);
        }

        private static string FormatGold(double amount)
        {
            if (amount >= 1e12) return (amount / 1e12).ToString("0.###") + "T Gold";
            if (amount >= 1e9) return (amount / 1e9).ToString("0.###") + "B Gold";
            if (amount >= 1e6) return (amount / 1e6).ToString("0.###") + "M Gold";
            if (amount >= 1e3) return (amount / 1e3).ToString("0.###") + "K Gold";
            return amount.ToString("0") + " Gold";
        }
    }
#endif
}
