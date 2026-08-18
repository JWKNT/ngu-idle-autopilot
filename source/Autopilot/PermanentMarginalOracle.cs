using System;
using System.Collections.Generic;
using System.Linq;

/*
FILE PURPOSE

PermanentMarginalOracle is the pure, shadow-only mechanics boundary for permanent-growth choices.
It models exact Beard group dividers and reset floors, native MacGuffin conversion time, Wish slot
identity and concave three-resource portfolios, typed NGU/Hack/Wish marginal actions, and the
continuation value of a Digger max-level purchase.  It never reads Character state, parses plan
text, calls a controller, or authorizes a mutation.

All strategic inputs are typed numeric facts.  In particular, a Digger purchase receives the
highest current level attainable on the selected continuation route; buying max level alone does
not imply that the extra current level can be used.  Integration owners may put these descriptors
into the global scheduler after shadow traces have been validated.
*/
namespace NGUInjector.Autopilot
{
    internal enum PermanentSystemKind
    {
        Ngu = 0,
        Hack = 1,
        Wish = 2,
        Beard = 3,
        MacGuffin = 4,
        Digger = 5
    }

    internal enum PermanentTrackKind
    {
        None = 0,
        Normal = 1,
        Evil = 2,
        Sadistic = 3
    }

    internal enum PermanentEffectTarget
    {
        None = 0,
        FightBoss = 1,
        Adventure = 2,
        DropChance = 3,
        Number = 4,
        EnergyNgu = 5,
        MagicNgu = 6,
        Gold = 7,
        Beard = 8,
        WishSpeed = 9,
        HackSpeed = 10,
        Resource = 11,
        Terminal = 12,
        EnergyPower = 13,
        EnergyCap = 14,
        MagicPower = 15,
        MagicCap = 16,
        EnergyBar = 17,
        MagicBar = 18,
        EnergyBeard = 19,
        MagicBeard = 20,
        AugmentSpeed = 21,
        WandoosEnergy = 22,
        WandoosMagic = 23,
        Blood = 24,
        Res3Power = 25,
        Res3Cap = 26,
        Res3Bar = 27
    }

    internal enum PermanentCompletionKind
    {
        Level = 0,
        Milestone = 1,
        BinaryDependency = 2,
        ResetConversion = 3,
        MaxLevel = 4
    }

    internal enum PermanentDependencyKind
    {
        None = 0,
        RouteUnlock = 1,
        EndHack = 2,
        EndWish = 3
    }

    internal enum PermanentRecomputeKind
    {
        LevelComplete = 0,
        MilestoneComplete = 1,
        ResourceChanged = 2,
        SlotChanged = 3,
        ResetBoundary = 4
    }

    internal sealed class PermanentResourceVector
    {
        internal long Energy;
        internal long Magic;
        internal long Res3;

        internal PermanentResourceVector Clone()
        {
            return new PermanentResourceVector
            {
                Energy = Energy,
                Magic = Magic,
                Res3 = Res3
            };
        }
    }

    internal sealed class PermanentActionDescriptor
    {
        internal PermanentSystemKind System;
        internal int Id;
        internal PermanentTrackKind Track;
        internal PermanentEffectTarget EffectTarget;
        internal PermanentResourceVector Resources = new PermanentResourceVector();
        internal int NativeSlotFootprint;
        internal PermanentCompletionKind Completion;
        internal PermanentDependencyKind Dependency;
        internal double EtaSeconds;
        internal double PersistentDelta;
        internal double DeltaLogEffect;
        internal int TerminalDependencyDelta;
        internal PermanentRecomputeKind NextRecompute;
    }

    internal sealed class BeardMarginalInput
    {
        internal int Id;
        internal bool Eligible = true;
        internal bool UsesEnergy;
        internal long TemporaryLevel;
        internal double Progress;

        // Native progress before division by (temporary level + 1) and group count.
        internal double BaseProgressPerTick;
        internal double ValuePerBankedTrimming = 1.0;
        internal double ValuePerTemporaryLevel;
    }

    internal sealed class BeardProjection
    {
        internal int Id;
        internal bool UsesEnergy;
        internal double GroupDivider;
        internal long ProjectedTemporaryLevel;
        internal double ProjectedProgress;
        internal long BankDelta;
        internal double WeightedValue;
    }

    internal sealed class BeardSubsetDecision
    {
        internal int[] FinalActiveIds = new int[0];
        internal BeardProjection[] Projections = new BeardProjection[0];
        internal double TotalWeightedValue;
        internal long TotalBankDelta;
        internal bool IsFinalActiveSet = true;
    }

    internal sealed class MacGuffinConversionInput
    {
        internal int ItemId;
        internal int ItemLevel;
        internal PermanentEffectTarget EffectTarget;
        internal double HighLevelExponent;
        internal double HighLevelScale;
        internal double PersistentAccumulatorBefore;
        internal double RouteSensitivity = 1.0;
    }

    internal sealed class MacGuffinCurve
    {
        internal int ItemId;
        internal PermanentEffectTarget EffectTarget;
        internal double LinearCoefficient;
        internal bool DiminishesAfterLevel100;
        internal double HighLevelExponent;
        internal double HighLevelScale;
    }

    internal sealed class MacGuffinBankResult
    {
        internal int ItemId;
        internal PermanentEffectTarget EffectTarget;
        internal double TimeFactor;
        internal double AccumulatorDelta;
        internal double DeltaLogEffect;
        internal double WeightedDeltaLogEffect;
    }

    internal sealed class WishMarginalInput
    {
        internal int Id;
        internal bool Eligible = true;
        internal bool BinaryDependency;
        internal PermanentDependencyKind Dependency;
        internal PermanentEffectTarget EffectTarget;
        internal long CurrentLevel;
        internal double Progress;
        internal double EffectBefore = 1.0;
        internal double EffectAfter = 1.0;

        // Coefficient multiplying E^0.17 * M^0.17 * R3^0.17 in native progress/tick.
        internal double ProgressCoefficient;
        internal double MinimumTimeProgressPerTick = double.PositiveInfinity;
    }

    internal sealed class WishPortfolioAllocation
    {
        internal int WishId;
        internal PermanentResourceVector Resources = new PermanentResourceVector();
        internal double ProgressPerTick;
        internal PermanentActionDescriptor Action;
    }

    internal sealed class WishPortfolioDecision
    {
        internal WishPortfolioAllocation[] Allocations = new WishPortfolioAllocation[0];
        internal int DistinctNativeSlots;
        internal bool BinaryConcentrated;
        internal double AggregateWeightedProgressPerTick;
    }

    internal sealed class DiggerMaxContinuationInput
    {
        internal int DiggerId;
        internal long CurrentLevel;
        internal long MaxLevelBefore;
        internal long MaxLevelAfter;

        // Highest current level feasible on the selected continuation with this Digger active,
        // before applying either max-level cap.  This is the required task-19 handoff.
        internal long ContinuationAttainableLevel;
        internal bool ContinuationHasSlot;
        internal bool CubicDirectBonus;
        internal double StartingBoost;
        internal double BoostPerLevel;
        internal double TotalMaxBonusBefore = 1.0;
        internal double TotalMaxBonusAfter = 1.0;
        internal double DirectEffectSensitivity = 1.0;
        internal double ActiveContinuationSensitivity = 1.0;
        internal double GoldCost;
    }

    internal sealed class DiggerMaxContinuationValue
    {
        internal int DiggerId;
        internal long EffectiveLevelBefore;
        internal long EffectiveLevelAfter;
        internal bool ExtraCurrentLevelAttainable;
        internal double GlobalDeltaLog;
        internal double DirectDeltaLog;
        internal double TotalValue;
        internal double ValuePerGold;
    }

    internal static class PermanentMarginalOracle
    {
        internal const double NativeTickSeconds = 0.02;
        internal const double WishResourceExponent = 0.17;
        internal const double WishPortfolioExponent = 0.49;
        internal const double WishNativeRawCutoff = 1e-8;

        private static readonly MacGuffinCurve[] MacGuffinCurves =
        {
            Curve(198, PermanentEffectTarget.EnergyPower, 1e-5, 0.3, 25.12e-5),
            Curve(199, PermanentEffectTarget.EnergyCap, 1e-5, 0.2, 39.81e-5),
            Curve(200, PermanentEffectTarget.MagicPower, 1e-5, 0.3, 25.12e-5),
            Curve(201, PermanentEffectTarget.MagicCap, 1e-5, 0.2, 39.81e-5),
            Curve(202, PermanentEffectTarget.EnergyNgu, 1e-5, 0.2, 39.81e-5),
            Curve(203, PermanentEffectTarget.MagicNgu, 1e-5, 0.2, 39.81e-5),
            Curve(204, PermanentEffectTarget.EnergyBar, 1e-5, 0.2, 39.81e-5),
            Curve(205, PermanentEffectTarget.MagicBar, 1e-5, 0.2, 39.81e-5),
            Curve(206, PermanentEffectTarget.EnergyBeard, 1e-5, 0.2, 39.81e-5),
            Curve(207, PermanentEffectTarget.MagicBeard, 1e-5, 0.2, 39.81e-5),
            Curve(208, PermanentEffectTarget.DropChance, 1e-5, 0.2, 39.81e-5),
            LinearCurve(209, PermanentEffectTarget.Gold, 5e-5),
            LinearCurve(210, PermanentEffectTarget.AugmentSpeed, 1e-5),
            LinearCurve(228, PermanentEffectTarget.FightBoss, 1e-4),
            Curve(211, PermanentEffectTarget.WandoosEnergy, 2e-5, 0.25, 31.63 * 2e-5),
            Curve(250, PermanentEffectTarget.WandoosMagic, 2e-5, 0.25, 31.63 * 2e-5),
            Curve(289, PermanentEffectTarget.Number, 5e-5, 0.25, 31.63 * 5e-5),
            Curve(290, PermanentEffectTarget.Blood, 3e-5, 0.2, 39.81 * 3e-5),
            Curve(291, PermanentEffectTarget.Adventure, 1e-5, 0.2, 39.81e-5),
            Curve(298, PermanentEffectTarget.Res3Power, 5e-6, 0.3, 25.12 * 5e-6),
            Curve(299, PermanentEffectTarget.Res3Cap, 5e-6, 0.2, 39.81 * 5e-6),
            Curve(300, PermanentEffectTarget.Res3Bar, 5e-6, 0.2, 39.81 * 5e-6)
        };

        private static MacGuffinCurve Curve(int itemId, PermanentEffectTarget target,
            double linearCoefficient, double exponent, double highScale)
        {
            return new MacGuffinCurve
            {
                ItemId = itemId,
                EffectTarget = target,
                LinearCoefficient = linearCoefficient,
                DiminishesAfterLevel100 = true,
                HighLevelExponent = exponent,
                HighLevelScale = highScale
            };
        }

        private static MacGuffinCurve LinearCurve(int itemId, PermanentEffectTarget target,
            double coefficient)
        {
            return new MacGuffinCurve
            {
                ItemId = itemId,
                EffectTarget = target,
                LinearCoefficient = coefficient
            };
        }

        internal static PermanentActionDescriptor DescribeNgu(int id,
            PermanentTrackKind track, bool usesEnergy, long allocation, double etaSeconds,
            PermanentEffectTarget effectTarget, double effectBefore, double effectAfter,
            PermanentDependencyKind dependency)
        {
            var resources = new PermanentResourceVector();
            if (usesEnergy) resources.Energy = Math.Max(0L, allocation);
            else resources.Magic = Math.Max(0L, allocation);
            return Describe(PermanentSystemKind.Ngu, id, track, effectTarget, resources, 0,
                PermanentCompletionKind.Level, dependency, etaSeconds, effectBefore, effectAfter,
                dependency == PermanentDependencyKind.None ? 0 : 1,
                PermanentRecomputeKind.LevelComplete);
        }

        internal static PermanentActionDescriptor DescribeHack(int id, long res3,
            double etaSeconds, PermanentEffectTarget effectTarget, double effectBefore,
            double effectAfter, bool terminalHack)
        {
            return Describe(PermanentSystemKind.Hack, id, PermanentTrackKind.None, effectTarget,
                new PermanentResourceVector {Res3 = Math.Max(0L, res3)}, 0,
                terminalHack ? PermanentCompletionKind.BinaryDependency
                    : PermanentCompletionKind.Milestone,
                terminalHack ? PermanentDependencyKind.EndHack : PermanentDependencyKind.None,
                etaSeconds, effectBefore, effectAfter, terminalHack ? 1 : 0,
                terminalHack ? PermanentRecomputeKind.LevelComplete
                    : PermanentRecomputeKind.MilestoneComplete);
        }

        internal static PermanentActionDescriptor DescribeWish(WishMarginalInput input,
            PermanentResourceVector resources, double etaSeconds)
        {
            if (input == null) throw new ArgumentNullException("input");
            return Describe(PermanentSystemKind.Wish, input.Id, PermanentTrackKind.None,
                input.EffectTarget, resources == null ? new PermanentResourceVector()
                    : resources.Clone(), 1,
                input.BinaryDependency ? PermanentCompletionKind.BinaryDependency
                    : PermanentCompletionKind.Level,
                input.Dependency, etaSeconds, input.EffectBefore, input.EffectAfter,
                input.BinaryDependency ? 1 : 0, PermanentRecomputeKind.LevelComplete);
        }

        private static PermanentActionDescriptor Describe(PermanentSystemKind system, int id,
            PermanentTrackKind track, PermanentEffectTarget effectTarget,
            PermanentResourceVector resources, int slots, PermanentCompletionKind completion,
            PermanentDependencyKind dependency, double etaSeconds, double effectBefore,
            double effectAfter, int terminalDelta, PermanentRecomputeKind recompute)
        {
            var before = FiniteOrZero(effectBefore);
            var after = FiniteOrZero(effectAfter);
            var deltaLog = before > 0.0 && after > 0.0
                ? Math.Log(after / before) : 0.0;
            return new PermanentActionDescriptor
            {
                System = system,
                Id = id,
                Track = track,
                EffectTarget = effectTarget,
                Resources = resources ?? new PermanentResourceVector(),
                NativeSlotFootprint = Math.Max(0, slots),
                Completion = completion,
                Dependency = dependency,
                EtaSeconds = ValidSeconds(etaSeconds),
                PersistentDelta = after - before,
                DeltaLogEffect = FiniteOrZero(deltaLog),
                TerminalDependencyDelta = Math.Max(0, terminalDelta),
                NextRecompute = recompute
            };
        }

        internal static double BeardCountDivider(int sameResourceCount,
            bool beardverseComplete)
        {
            var divider = Math.Max(1.0, sameResourceCount);
            if (beardverseComplete && divider >= 1.9)
                divider *= 0.9;
            return Math.Max(1.0, divider);
        }

        internal static double BeardTimeFactor(double rebirthSeconds, long perk21Level)
        {
            if (double.IsNaN(rebirthSeconds) || rebirthSeconds < 3600.0) return 0.0;
            var denominator = 24.0 - perk21Level;
            if (denominator <= 0.0) return 8.0;
            return Math.Min(8.0, Math.Max(0.0,
                rebirthSeconds / 10800.0 * 24.0 / denominator));
        }

        internal static long BeardBankDelta(long temporaryLevel, double rebirthSeconds,
            long perk21Level)
        {
            if (temporaryLevel <= 0L) return 0L;
            var factor = BeardTimeFactor(rebirthSeconds, perk21Level);
            if (factor <= 0.0) return 0L;
            var projected = Math.Floor(Math.Sqrt(temporaryLevel) * factor);
            if (double.IsNaN(projected) || projected <= 0.0) return 0L;
            if (projected >= temporaryLevel) return temporaryLevel;
            return (long)projected;
        }

        internal static BeardSubsetDecision SelectBeardSubset(
            IEnumerable<BeardMarginalInput> source, int slots, double horizonSeconds,
            double rebirthSecondsAtConversion, long perk21Level, bool beardverseComplete)
        {
            var candidates = (source ?? Enumerable.Empty<BeardMarginalInput>())
                .Where(x => x != null && x.Eligible && x.Id >= 0)
                .GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.Id).ToArray();
            var maxSlots = Math.Max(0, Math.Min(slots, candidates.Length));
            if (maxSlots == 0) return new BeardSubsetDecision();

            BeardSubsetDecision best = null;
            var selected = new List<BeardMarginalInput>();
            EnumerateBeardSubsets(candidates, 0, maxSlots, selected, horizonSeconds,
                rebirthSecondsAtConversion, perk21Level, beardverseComplete, ref best);
            return best ?? new BeardSubsetDecision();
        }

        private static void EnumerateBeardSubsets(BeardMarginalInput[] candidates, int index,
            int maxSlots, IList<BeardMarginalInput> selected, double horizonSeconds,
            double rebirthSeconds, long perk21Level, bool beardverseComplete,
            ref BeardSubsetDecision best)
        {
            if (selected.Count > 0)
            {
                var candidate = ProjectBeardSubset(selected, horizonSeconds, rebirthSeconds,
                    perk21Level, beardverseComplete);
                if (BetterBeardDecision(candidate, best)) best = candidate;
            }
            if (index >= candidates.Length || selected.Count >= maxSlots) return;
            for (var i = index; i < candidates.Length; i++)
            {
                selected.Add(candidates[i]);
                EnumerateBeardSubsets(candidates, i + 1, maxSlots, selected, horizonSeconds,
                    rebirthSeconds, perk21Level, beardverseComplete, ref best);
                selected.RemoveAt(selected.Count - 1);
            }
        }

        private static BeardSubsetDecision ProjectBeardSubset(
            IEnumerable<BeardMarginalInput> selected, double horizonSeconds,
            double rebirthSeconds, long perk21Level, bool beardverseComplete)
        {
            var items = selected.OrderBy(x => x.Id).ToArray();
            var energyCount = items.Count(x => x.UsesEnergy);
            var magicCount = items.Length - energyCount;
            var result = new BeardSubsetDecision
            {
                FinalActiveIds = items.Select(x => x.Id).ToArray(),
                Projections = new BeardProjection[items.Length]
            };
            for (var i = 0; i < items.Length; i++)
            {
                var input = items[i];
                var divider = BeardCountDivider(input.UsesEnergy ? energyCount : magicCount,
                    beardverseComplete);
                long level;
                double progress;
                ProjectBeard(input, divider, horizonSeconds, out level, out progress);
                var bank = BeardBankDelta(level, rebirthSeconds, perk21Level);
                var value = bank * Math.Max(0.0, FiniteOrZero(input.ValuePerBankedTrimming))
                            + Math.Max(0L, level - Math.Max(0L, input.TemporaryLevel))
                            * Math.Max(0.0, FiniteOrZero(input.ValuePerTemporaryLevel));
                result.Projections[i] = new BeardProjection
                {
                    Id = input.Id,
                    UsesEnergy = input.UsesEnergy,
                    GroupDivider = divider,
                    ProjectedTemporaryLevel = level,
                    ProjectedProgress = progress,
                    BankDelta = bank,
                    WeightedValue = value
                };
                result.TotalBankDelta += bank;
                result.TotalWeightedValue += value;
            }
            return result;
        }

        private static void ProjectBeard(BeardMarginalInput input, double divider,
            double horizonSeconds, out long level, out double progress)
        {
            level = Math.Max(0L, input.TemporaryLevel);
            progress = Math.Max(0.0, Math.Min(0.9999999999999999,
                FiniteOrZero(input.Progress)));
            if (horizonSeconds <= 0.0 || input.BaseProgressPerTick <= 0.0
                || double.IsNaN(input.BaseProgressPerTick)
                || double.IsInfinity(input.BaseProgressPerTick)) return;
            var rawTicks = Math.Floor(horizonSeconds / NativeTickSeconds + 1e-9);
            var ticks = rawTicks >= long.MaxValue ? long.MaxValue : Math.Max(0L, (long)rawTicks);
            var baseRate = input.BaseProgressPerTick / Math.Max(1.0, divider);
            while (ticks > 0L && level < long.MaxValue)
            {
                var rate = baseRate / (level + 1.0);
                if (rate <= 0.0 || double.IsNaN(rate)) break;
                var required = Math.Ceiling((1.0 - progress) / rate);
                var completionTicks = required >= long.MaxValue
                    ? long.MaxValue : Math.Max(1L, (long)required);
                if (completionTicks > ticks)
                {
                    progress = Math.Min(0.9999999999999999,
                        progress + ticks * rate);
                    break;
                }
                ticks -= completionTicks;
                level++;
                progress = 0.0; // Native discards overfill and awards at most one level/tick.

                // Batch the native one-level-per-tick saturated region.
                var lastSaturatedLevel = Math.Floor(baseRate) - 1.0;
                if (ticks > 0L && level <= lastSaturatedLevel)
                {
                    var available = lastSaturatedLevel >= long.MaxValue
                        ? long.MaxValue : (long)lastSaturatedLevel - level + 1L;
                    var advance = Math.Min(ticks, Math.Max(0L, available));
                    if (advance > long.MaxValue - level) advance = long.MaxValue - level;
                    level += advance;
                    ticks -= advance;
                }
            }
        }

        private static bool BetterBeardDecision(BeardSubsetDecision candidate,
            BeardSubsetDecision current)
        {
            if (current == null) return true;
            var tolerance = Math.Max(1e-12,
                Math.Max(Math.Abs(candidate.TotalWeightedValue),
                    Math.Abs(current.TotalWeightedValue)) * 1e-12);
            if (candidate.TotalWeightedValue > current.TotalWeightedValue + tolerance) return true;
            if (candidate.TotalWeightedValue + tolerance < current.TotalWeightedValue) return false;
            if (candidate.TotalBankDelta != current.TotalBankDelta)
                return candidate.TotalBankDelta > current.TotalBankDelta;
            if (candidate.FinalActiveIds.Length != current.FinalActiveIds.Length)
                return candidate.FinalActiveIds.Length < current.FinalActiveIds.Length;
            for (var i = 0; i < candidate.FinalActiveIds.Length; i++)
            {
                if (candidate.FinalActiveIds[i] == current.FinalActiveIds[i]) continue;
                return candidate.FinalActiveIds[i] < current.FinalActiveIds[i];
            }
            return false;
        }

        internal static double MacGuffinTimeFactor(double rebirthSeconds,
            bool sadisticTrollTwo, double boosterMultiplier)
        {
            if (double.IsNaN(rebirthSeconds) || rebirthSeconds < 180.0) return 0.0;
            double factor;
            if (sadisticTrollTwo)
            {
                if (rebirthSeconds <= 1800.0)
                    factor = Math.Pow(rebirthSeconds / 1800.0, 2.0);
                else if (rebirthSeconds <= 86400.0)
                    factor = rebirthSeconds / 1800.0;
                else
                    factor = 48.0 * Math.Pow(rebirthSeconds / 86400.0, 0.4);
                factor = Math.Min(104.86, factor);
            }
            else
            {
                factor = rebirthSeconds <= 1800.0
                    ? Math.Pow(rebirthSeconds / 1800.0, 2.0)
                    : Math.Sqrt(rebirthSeconds / 1800.0);
                factor = Math.Min(20.0, factor);
            }
            var booster = double.IsNaN(boosterMultiplier)
                          || double.IsInfinity(boosterMultiplier)
                ? 1.0 : Math.Max(0.0, boosterMultiplier);
            return factor * booster;
        }

        internal static double MacGuffinGainAtUnitTime(int itemLevel,
            double highLevelExponent, double highLevelScale)
        {
            var adjusted = (long)itemLevel + 1L;
            if (adjusted <= 0L) return 0.0;
            if (adjusted <= 100L) return adjusted * 1e-5;
            if (highLevelExponent <= 0.0 || highLevelScale <= 0.0
                || double.IsNaN(highLevelExponent) || double.IsNaN(highLevelScale)) return 0.0;
            return Math.Pow(adjusted, highLevelExponent) * highLevelScale * 1e-5;
        }

        internal static bool TryGetMacGuffinCurve(int itemId, out MacGuffinCurve curve)
        {
            var match = MacGuffinCurves.FirstOrDefault(x => x.ItemId == itemId);
            if (match == null)
            {
                curve = null;
                return false;
            }
            curve = new MacGuffinCurve
            {
                ItemId = match.ItemId,
                EffectTarget = match.EffectTarget,
                LinearCoefficient = match.LinearCoefficient,
                DiminishesAfterLevel100 = match.DiminishesAfterLevel100,
                HighLevelExponent = match.HighLevelExponent,
                HighLevelScale = match.HighLevelScale
            };
            return true;
        }

        internal static double MacGuffinGainAtUnitTime(int itemId, int itemLevel)
        {
            MacGuffinCurve curve;
            if (!TryGetMacGuffinCurve(itemId, out curve)) return 0.0;
            var adjusted = (long)itemLevel + 1L;
            if (adjusted <= 0L) return 0.0;
            if (!curve.DiminishesAfterLevel100 || adjusted <= 100L)
                return adjusted * curve.LinearCoefficient;
            return Math.Pow(adjusted, curve.HighLevelExponent) * curve.HighLevelScale;
        }

        internal static MacGuffinBankResult EvaluateMacGuffinBank(
            MacGuffinConversionInput input, double rebirthSeconds,
            bool sadisticTrollTwo, double boosterMultiplier)
        {
            if (input == null) throw new ArgumentNullException("input");
            var factor = MacGuffinTimeFactor(rebirthSeconds, sadisticTrollTwo,
                boosterMultiplier);
            MacGuffinCurve nativeCurve;
            var known = TryGetMacGuffinCurve(input.ItemId, out nativeCurve);
            var unitGain = known ? MacGuffinGainAtUnitTime(input.ItemId, input.ItemLevel)
                : MacGuffinGainAtUnitTime(input.ItemLevel, input.HighLevelExponent,
                    input.HighLevelScale);
            var delta = unitGain * factor;
            var before = Math.Max(0.0, FiniteOrZero(input.PersistentAccumulatorBefore));
            var deltaLog = before > 0.0 && delta >= 0.0
                ? Math.Log((before + delta) / before) : 0.0;
            return new MacGuffinBankResult
            {
                ItemId = input.ItemId,
                EffectTarget = known ? nativeCurve.EffectTarget : input.EffectTarget,
                TimeFactor = factor,
                AccumulatorDelta = delta,
                DeltaLogEffect = FiniteOrZero(deltaLog),
                WeightedDeltaLogEffect = FiniteOrZero(deltaLog)
                                         * Math.Max(0.0, FiniteOrZero(input.RouteSensitivity))
            };
        }

        internal static double WishEffect(long level, double effectPerLevel,
            bool difficultyRequirementMet)
        {
            if (!difficultyRequirementMet) return 1.0;
            var result = 1.0 + Math.Max(0L, level) * FiniteOrZero(effectPerLevel);
            return Math.Max(1.0, result);
        }

        internal static int NativeWishSlots(bool evilTrollSeven, bool pinkHeartComplete,
            bool quirk56Purchased)
        {
            return Math.Min(4, 1 + (evilTrollSeven ? 1 : 0)
                + (pinkHeartComplete ? 1 : 0) + (quirk56Purchased ? 1 : 0));
        }

        internal static int CountAllocatedWishSlots(
            IEnumerable<WishPortfolioAllocation> allocations)
        {
            return (allocations ?? Enumerable.Empty<WishPortfolioAllocation>())
                .Where(x => x != null && x.WishId >= 0 && x.Resources != null
                            && (x.Resources.Energy > 0L || x.Resources.Magic > 0L
                                || x.Resources.Res3 > 0L))
                .Select(x => x.WishId).Distinct().Count();
        }

        internal static double WishEqualSplitThroughputFactor(int distinctWishes)
        {
            return distinctWishes <= 0 ? 0.0
                : Math.Pow(distinctWishes, WishPortfolioExponent);
        }

        internal static double WishRawProgressPerTick(double coefficient,
            PermanentResourceVector resources)
        {
            if (resources == null || coefficient <= 0.0 || double.IsNaN(coefficient)
                || double.IsInfinity(coefficient) || resources.Energy <= 0L
                || resources.Magic <= 0L || resources.Res3 <= 0L) return 0.0;
            return coefficient
                   * Math.Pow(resources.Energy, WishResourceExponent)
                   * Math.Pow(resources.Magic, WishResourceExponent)
                   * Math.Pow(resources.Res3, WishResourceExponent);
        }

        internal static double WishEffectiveProgressPerTick(double rawProgressPerTick,
            double minimumTimeProgressPerTick)
        {
            if (rawProgressPerTick < WishNativeRawCutoff
                || double.IsNaN(rawProgressPerTick)) return 0.0;
            if (minimumTimeProgressPerTick > 0.0
                && !double.IsNaN(minimumTimeProgressPerTick))
                return Math.Min(rawProgressPerTick, minimumTimeProgressPerTick);
            return rawProgressPerTick;
        }

        internal static WishPortfolioDecision PlanWishPortfolio(
            IEnumerable<WishMarginalInput> source, int nativeSlots,
            PermanentResourceVector budgets)
        {
            var result = new WishPortfolioDecision();
            if (budgets == null || budgets.Energy <= 0L || budgets.Magic <= 0L
                || budgets.Res3 <= 0L || nativeSlots <= 0) return result;
            var inputs = (source ?? Enumerable.Empty<WishMarginalInput>())
                .Where(x => x != null && x.Eligible && x.Id >= 0
                            && x.ProgressCoefficient > 0.0)
                .GroupBy(x => x.Id).Select(x => x.First())
                .Where(x => x.BinaryDependency || WishMarginalValue(x) > 0.0).ToArray();
            if (inputs.Length == 0) return result;

            var binary = inputs.Where(x => x.BinaryDependency)
                .OrderByDescending(x => (int)x.Dependency)
                .ThenBy(x => CompletionSeconds(x, budgets)).ThenBy(x => x.Id).FirstOrDefault();
            if (binary != null)
            {
                var allocation = BuildWishAllocation(binary, budgets.Clone());
                result.Allocations = new[] {allocation};
                result.DistinctNativeSlots = 1;
                result.BinaryConcentrated = true;
                result.AggregateWeightedProgressPerTick = allocation.ProgressPerTick
                    * WishMarginalValue(binary);
                return result;
            }

            var maximumByResources = (int)Math.Min(int.MaxValue,
                Math.Min(budgets.Energy, Math.Min(budgets.Magic, budgets.Res3)));
            var count = Math.Min(inputs.Length, Math.Min(Math.Max(0, nativeSlots),
                maximumByResources));
            if (count <= 0) return result;
            var chosen = inputs.OrderByDescending(x =>
                    WishMarginalValue(x) * WishRawProgressPerTick(x.ProgressCoefficient, budgets))
                .ThenBy(x => x.Id).Take(count).ToArray();
            var weights = chosen.Select(x => Math.Pow(Math.Max(1e-300,
                WishMarginalValue(x) * x.ProgressCoefficient),
                1.0 / WishPortfolioExponent)).ToArray();
            var energy = AllocateWeightedLong(budgets.Energy, weights);
            var magic = AllocateWeightedLong(budgets.Magic, weights);
            var res3 = AllocateWeightedLong(budgets.Res3, weights);
            var allocations = new List<WishPortfolioAllocation>();
            for (var i = 0; i < chosen.Length; i++)
            {
                var allocation = BuildWishAllocation(chosen[i],
                    new PermanentResourceVector
                    {
                        Energy = energy[i],
                        Magic = magic[i],
                        Res3 = res3[i]
                    });
                if (allocation.Resources.Energy <= 0L || allocation.Resources.Magic <= 0L
                    || allocation.Resources.Res3 <= 0L) continue;
                allocations.Add(allocation);
                result.AggregateWeightedProgressPerTick += allocation.ProgressPerTick
                    * WishMarginalValue(chosen[i]);
            }
            result.Allocations = allocations.ToArray();
            result.DistinctNativeSlots = CountAllocatedWishSlots(result.Allocations);
            return result;
        }

        private static WishPortfolioAllocation BuildWishAllocation(WishMarginalInput input,
            PermanentResourceVector resources)
        {
            var raw = WishRawProgressPerTick(input.ProgressCoefficient, resources);
            var rate = WishEffectiveProgressPerTick(raw, input.MinimumTimeProgressPerTick);
            var eta = rate <= 0.0 ? double.PositiveInfinity
                : Math.Max(1.0, Math.Ceiling((1.0 - Math.Max(0.0,
                    Math.Min(0.9999999999999999, input.Progress))) / rate))
                  * NativeTickSeconds;
            return new WishPortfolioAllocation
            {
                WishId = input.Id,
                Resources = resources,
                ProgressPerTick = rate,
                Action = DescribeWish(input, resources, eta)
            };
        }

        private static double CompletionSeconds(WishMarginalInput input,
            PermanentResourceVector resources)
        {
            return BuildWishAllocation(input, resources.Clone()).Action.EtaSeconds;
        }

        private static double WishMarginalValue(WishMarginalInput input)
        {
            if (input.BinaryDependency) return 1.0;
            var before = Math.Max(1e-300, FiniteOrZero(input.EffectBefore));
            var after = Math.Max(1e-300, FiniteOrZero(input.EffectAfter));
            var delta = Math.Abs(Math.Log(after / before));
            return delta > 0.0 && !double.IsInfinity(delta) ? delta : 0.0;
        }

        private static long[] AllocateWeightedLong(long total, double[] weights)
        {
            var result = new long[weights.Length];
            if (total <= 0L || weights.Length == 0) return result;
            weights = weights.Select(x => x > 0.0 && !double.IsInfinity(x)
                                             && !double.IsNaN(x) ? x : 0.0).ToArray();
            var maximum = weights.Length == 0 ? 0.0 : weights.Max();
            if (maximum <= 0.0)
                weights = Enumerable.Repeat(1.0, weights.Length).ToArray();
            else
                weights = weights.Select(x => x / maximum).ToArray();
            var sum = weights.Sum();
            var ratios = weights.Select(x => (decimal)(x / sum)).ToArray();
            var ratioSum = ratios.Sum();
            var fractions = new double[weights.Length];
            long used = 0L;
            for (var i = 0; i < weights.Length; i++)
            {
                var exact = ratioSum <= 0m ? 0m
                    : (decimal)total * ratios[i] / ratioSum;
                var floor = (long)decimal.Floor(exact);
                result[i] = Math.Max(0L, Math.Min(total - used, floor));
                fractions[i] = (double)(exact - floor);
                used += result[i];
            }
            var remainder = total - used;
            var order = Enumerable.Range(0, weights.Length)
                .OrderByDescending(x => fractions[x]).ThenBy(x => x).ToArray();
            var next = 0;
            while (remainder > 0L)
            {
                var i = order[next++ % order.Length];
                result[i]++;
                remainder--;
            }
            return result;
        }

        internal static DiggerMaxContinuationValue EvaluateDiggerMaxContinuation(
            DiggerMaxContinuationInput input)
        {
            if (input == null) throw new ArgumentNullException("input");
            var maxBefore = Math.Max(0L, input.MaxLevelBefore);
            var maxAfter = Math.Max(maxBefore, input.MaxLevelAfter);
            var current = Math.Max(0L, input.CurrentLevel);
            var attainable = input.ContinuationHasSlot
                ? Math.Max(current, input.ContinuationAttainableLevel) : current;
            var effectiveBefore = Math.Min(maxBefore, attainable);
            var effectiveAfter = Math.Min(maxAfter, attainable);
            var globalBefore = Math.Max(1e-300, input.TotalMaxBonusBefore);
            var globalAfter = Math.Max(1e-300, input.TotalMaxBonusAfter);
            var globalDelta = Math.Max(0.0, FiniteOrZero(input.ActiveContinuationSensitivity))
                              * FiniteOrZero(Math.Log(globalAfter / globalBefore));
            globalDelta = Math.Max(0.0, globalDelta);
            var directBefore = DiggerDirectBonus(effectiveBefore, input.StartingBoost,
                input.BoostPerLevel, input.CubicDirectBonus);
            var directAfter = DiggerDirectBonus(effectiveAfter, input.StartingBoost,
                input.BoostPerLevel, input.CubicDirectBonus);
            var directDelta = input.ContinuationHasSlot
                ? Math.Max(0.0, FiniteOrZero(input.DirectEffectSensitivity))
                  * FiniteOrZero(Math.Log(directAfter / directBefore)) : 0.0;
            var total = globalDelta + directDelta;
            return new DiggerMaxContinuationValue
            {
                DiggerId = input.DiggerId,
                EffectiveLevelBefore = effectiveBefore,
                EffectiveLevelAfter = effectiveAfter,
                ExtraCurrentLevelAttainable = effectiveAfter > effectiveBefore,
                GlobalDeltaLog = globalDelta,
                DirectDeltaLog = directDelta,
                TotalValue = total,
                ValuePerGold = input.GoldCost > 0.0 ? total / input.GoldCost : 0.0
            };
        }

        private static double DiggerDirectBonus(long level, double startingBoost,
            double boostPerLevel, bool cubic)
        {
            var term = cubic ? Math.Pow(Math.Max(0L, level), 3.0) : Math.Max(0L, level);
            return Math.Max(1.0, 1.0 + FiniteOrZero(startingBoost)
                                 + term * FiniteOrZero(boostPerLevel));
        }

        private static double FiniteOrZero(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : value;
        }

        private static double ValidSeconds(double value)
        {
            if (double.IsNaN(value) || value < 0.0) return double.PositiveInfinity;
            return value;
        }
    }
}
