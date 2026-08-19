using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Autopilot;
using static NGUInjector.Main;

/*
FILE PURPOSE

Purpose: YggdrasilManager owns exact fruit maturity, activation-horizon, eat/harvest, Poop-batch,
seed-purchase, and verified native mutation policy.  It replaces page-order/single-Poop heuristics
with event decisions that price exact seed previews and typed permanent reward targets.

Mechanism: YggdrasilEventController is pure and .NET 3.5-compatible.  It implements native tier
length/factor arithmetic, zero-factor reset maturity admission, first/free Poop modulo transitions,
exact-preview marginal ranking, and finite-horizon seed purchases.  The live adapter obtains seed
previews from FruitController.seedReward/harvestSeedReward, maps persistent outcomes to task-20
PermanentEffectTarget values, and executes task-1 child intents under a caller-owned root.

Inputs and outputs: Inputs include fruit timers/tiers/activation/harvest counts, live tier threshold,
reset horizon/factor, exact native seed previews, Poop stock/counter/modifier, permanent descriptors,
and the root transaction.  Outputs are typed activation/consume/Poop/purchase plans, verified YGG
logs through MutationCoordinator, and LastSeedDecision/LastFruitDecision/LastRewardPreview telemetry.

Invariants and safety: A non-permanent fruit is never activated when it cannot reach tier one before
a zero-factor reset.  The first MAXX-item-162 use is free even at zero stock; every selected mature
fruit is simulated in native ID order so stock and poopUsed remain exact.  One global consume call
replaces three page calls.  Native mutations never run through parameterless compatibility entry
points; task 29 must supply the active nonzero root and stage loadout/digger children separately.

Extension points and non-goals: The global scheduler may replace reward shadow prices and supply
fully exact fruit-specific currency previews.  This manager does not choose MacGuffin equipment,
Gold gear, Ygg loadouts/diggers, rebirth timing, or invoke saved RNG.
*/
namespace NGUInjector.Managers
{
    internal enum FruitConsumeKind
    {
        Eat,
        Harvest
    }

    internal sealed class FruitRewardPreview
    {
        internal int FruitId;
        internal int Tier;
        internal bool Mature;
        internal bool PoopEligible;
        internal long EatSeedsWithoutPoop;
        internal long EatSeedsWithPoop;
        internal long HarvestSeedsWithoutPoop;
        internal long HarvestSeedsWithPoop;
        internal double SpecificWithoutPoop;
        internal double SpecificWithPoop;
        internal double SpecificShadowValue = 1.0;
        internal double SeedShadowValue = 1.0;
        internal PermanentEffectTarget PermanentTarget;
        internal PermanentActionDescriptor PermanentAction;
        internal bool SourceExact;

        internal double Value(FruitConsumeKind kind, bool poop)
        {
            var seeds = kind == FruitConsumeKind.Eat
                ? (poop ? EatSeedsWithPoop : EatSeedsWithoutPoop)
                : (poop ? HarvestSeedsWithPoop : HarvestSeedsWithoutPoop);
            var specific = kind == FruitConsumeKind.Eat
                ? (poop ? SpecificWithPoop : SpecificWithoutPoop) : 0.0;
            return Math.Max(0.0, seeds) * Math.Max(0.0, SeedShadowValue)
                   + Math.Max(0.0, specific) * Math.Max(0.0, SpecificShadowValue);
        }
    }

    internal sealed class FruitConsumeDecision
    {
        internal int FruitId;
        internal FruitConsumeKind Kind;
        internal bool UsePoop;
        internal bool FreePoop;
        internal long PoopCounterBefore;
        internal long PoopCounterAfter;
        internal int StockBefore;
        internal int StockAfter;
        internal double MarginalPoopValue;
        internal PermanentEffectTarget PermanentTarget;
    }

    internal sealed class PoopBatchPlan
    {
        internal FruitConsumeDecision[] Decisions = new FruitConsumeDecision[0];
        internal int InitialStock;
        internal int FinalStock;
        internal long InitialCounter;
        internal long FinalCounter;
        internal double TotalMarginalValue;
    }

    internal sealed class FruitActivationCandidate
    {
        internal int FruitId;
        internal bool Permanent;
        internal bool Activated;
        internal double Seconds;
        internal double ActivationBenefit;
        internal PermanentActionDescriptor[] DisplacedPermanentActions =
            new PermanentActionDescriptor[0];
    }

    internal sealed class SeedPurchaseCandidate
    {
        internal int FruitId;
        internal long CurrentTier;
        internal long ExactCost;
        internal double FiniteHorizonValue;
        internal PermanentEffectTarget PermanentTarget;
    }

    /*
    PURE YGGDRASIL EVENT ORACLE

    Poop planning intentionally iterates in native fruit-ID consumption order.  A value-sort followed
    by consumeAll would be false because earlier toggled fruits mutate the shared stock/modulo state.
    Reward inputs are exact outcomes rather than one ID-agnostic utility, so rounding and First
    Harvest effects remain in the preview supplied by the live/native adapter.
    */
    internal static class YggdrasilEventController
    {
        internal const int BaseTierSeconds = 3600;

        internal static int TierThreshold(int quirk13Level)
        {
            return BaseTierSeconds - Math.Min(180, 60 * Math.Max(0, quirk13Level));
        }

        internal static int TierFactor(int tier)
        {
            if (tier < 0) throw new ArgumentOutOfRangeException("tier");
            // Native uses Mathf.Pow(float,float) followed by Mathf.CeilToInt.  Preserve the
            // intermediate float32 rounding instead of silently substituting double arithmetic.
            var nativePow = (float)Math.Pow((float)tier, 1.5f);
            return (int)Math.Ceiling(nativePow);
        }

        internal static int HarvestTier(double seconds, long maximumTier, int tierThreshold)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
                throw new ArgumentOutOfRangeException("seconds");
            if (maximumTier < 0 || maximumTier > int.MaxValue)
                throw new ArgumentOutOfRangeException("maximumTier");
            if (tierThreshold <= 0) throw new ArgumentOutOfRangeException("tierThreshold");
            var rawTier = Math.Floor(seconds / tierThreshold);
            return rawTier >= maximumTier ? (int)maximumTier : (int)rawTier;
        }

        internal static double SecondsToTier(double seconds, int targetTier, int tierThreshold)
        {
            if (targetTier < 0) throw new ArgumentOutOfRangeException("targetTier");
            if (tierThreshold <= 0) throw new ArgumentOutOfRangeException("tierThreshold");
            return Math.Max(0.0, targetTier * (double)tierThreshold - seconds);
        }

        internal static bool CanMatureBeforeReset(double currentSeconds,
            double remainingRunSeconds, double resetFactor, int tierThreshold)
        {
            if (double.IsNaN(currentSeconds) || currentSeconds < 0.0
                || double.IsNaN(remainingRunSeconds) || remainingRunSeconds < 0.0
                || double.IsNaN(resetFactor) || double.IsInfinity(resetFactor)
                || resetFactor < 0.0 || tierThreshold <= 0) return false;
            if (Math.Floor(resetFactor) >= 1.0) return true;
            return currentSeconds + remainingRunSeconds >= tierThreshold;
        }

        internal static double PermanentOpportunityCost(
            IEnumerable<PermanentActionDescriptor> actions)
        {
            if (actions == null) return 0.0;
            var value = 0.0;
            foreach (var action in actions)
            {
                if (action == null) continue;
                value += Math.Max(0.0, action.DeltaLogEffect);
                if (action.Dependency != PermanentDependencyKind.None)
                    value += Math.Max(0, action.TerminalDependencyDelta);
            }
            return value;
        }

        internal static bool ShouldActivate(FruitActivationCandidate fruit,
            double remainingRunSeconds, double resetFactor, int tierThreshold)
        {
            if (fruit == null) throw new ArgumentNullException("fruit");
            if (fruit.Permanent || fruit.Activated) return false;
            if (!CanMatureBeforeReset(fruit.Seconds, remainingRunSeconds,
                    resetFactor, tierThreshold)) return false;
            return fruit.ActivationBenefit > PermanentOpportunityCost(
                fruit.DisplacedPermanentActions);
        }

        internal static FruitConsumeKind SelectConsumeKind(FruitRewardPreview preview)
        {
            if (preview == null) throw new ArgumentNullException("preview");
            return preview.Value(FruitConsumeKind.Eat, false)
                   >= preview.Value(FruitConsumeKind.Harvest, false)
                ? FruitConsumeKind.Eat : FruitConsumeKind.Harvest;
        }

        internal static bool IsFreePoopUse(bool item162Maxxed, long poopUsed)
        {
            if (poopUsed < 0L) throw new ArgumentOutOfRangeException("poopUsed");
            return item162Maxxed && poopUsed % 10L == 0L;
        }

        internal static bool CanUsePoop(bool item162Maxxed, long poopUsed, int stock)
        {
            if (stock < 0) throw new ArgumentOutOfRangeException("stock");
            return stock > 0 || IsFreePoopUse(item162Maxxed, poopUsed);
        }

        internal static double ClampPoopModifier(double modifier)
        {
            if (double.IsNaN(modifier) || double.IsInfinity(modifier)) return 1.0;
            return Math.Max(1.0, Math.Min(1.65, modifier));
        }

        internal static PoopBatchPlan PlanPoopBatch(IEnumerable<FruitRewardPreview> previews,
            int stock, long poopUsed, bool item162Maxxed)
        {
            if (stock < 0) throw new ArgumentOutOfRangeException("stock");
            if (poopUsed < 0L) throw new ArgumentOutOfRangeException("poopUsed");
            var plan = new PoopBatchPlan
            {
                InitialStock = stock,
                FinalStock = stock,
                InitialCounter = poopUsed,
                FinalCounter = poopUsed
            };
            if (previews == null) return plan;
            var options = previews.Where(x => x != null && x.Mature)
                .OrderBy(x => x.FruitId).Select(BuildPoopOption).ToArray();
            var selected = OptimizePoopUses(options, 0, stock, poopUsed, item162Maxxed,
                new Dictionary<string, PoopChoicePath>()).Uses;
            var decisions = new List<FruitConsumeDecision>();
            for (var index = 0; index < options.Length; index++)
            {
                var option = options[index];
                var preview = option.Preview;
                var free = IsFreePoopUse(item162Maxxed, plan.FinalCounter);
                var use = selected[index];
                var beforeStock = plan.FinalStock;
                var beforeCounter = plan.FinalCounter;
                if (use)
                {
                    if (!free) plan.FinalStock--;
                    plan.FinalCounter++;
                    plan.TotalMarginalValue += option.Marginal;
                }
                decisions.Add(new FruitConsumeDecision
                {
                    FruitId = preview.FruitId,
                    Kind = use ? option.PoopKind : option.NormalKind,
                    UsePoop = use,
                    FreePoop = use && free,
                    StockBefore = beforeStock,
                    StockAfter = plan.FinalStock,
                    PoopCounterBefore = beforeCounter,
                    PoopCounterAfter = plan.FinalCounter,
                    MarginalPoopValue = use ? option.Marginal : 0.0,
                    PermanentTarget = preview.PermanentTarget
                });
            }
            plan.Decisions = decisions.ToArray();
            return plan;
        }

        /*
        EXACT POOP SUBSET

        Native consume order is fixed by fruit ID, but the toggles can select any subset.  This
        dynamic program prices both stock debits and the shared modulo counter, so a cheap earlier
        fruit cannot consume the only paid item ahead of a more valuable later fruit, and a free
        transition can still deliberately advance the counter.  Twenty-one fruits keep the state
        space tiny; ties retain the counter/stock instead of performing an unnecessary mutation.
        */
        private sealed class PoopOption
        {
            internal FruitRewardPreview Preview;
            internal FruitConsumeKind NormalKind;
            internal FruitConsumeKind PoopKind;
            internal double Marginal;
        }

        private sealed class PoopChoicePath
        {
            internal double Value;
            internal bool[] Uses = new bool[0];
        }

        private static PoopOption BuildPoopOption(FruitRewardPreview preview)
        {
            var normalKind = SelectConsumeKind(preview);
            var poopKind = preview.Value(FruitConsumeKind.Eat, true)
                           >= preview.Value(FruitConsumeKind.Harvest, true)
                ? FruitConsumeKind.Eat : FruitConsumeKind.Harvest;
            return new PoopOption
            {
                Preview = preview,
                NormalKind = normalKind,
                PoopKind = poopKind,
                Marginal = preview.Value(poopKind, true) - preview.Value(normalKind, false)
            };
        }

        private static PoopChoicePath OptimizePoopUses(PoopOption[] options, int index,
            int stock, long poopUsed, bool item162Maxxed,
            IDictionary<string, PoopChoicePath> memo)
        {
            if (index >= options.Length) return new PoopChoicePath();
            var key = index + ":" + stock + ":" + poopUsed % 10L;
            PoopChoicePath cached;
            if (memo.TryGetValue(key, out cached)) return cached;

            var noTail = OptimizePoopUses(options, index + 1, stock, poopUsed,
                item162Maxxed, memo);
            var best = new PoopChoicePath
            {
                Value = noTail.Value,
                Uses = PrependChoice(false, noTail.Uses)
            };
            var option = options[index];
            var free = IsFreePoopUse(item162Maxxed, poopUsed);
            if (option.Preview.PoopEligible && option.Marginal > 0.0
                && (free || stock > 0))
            {
                var useTail = OptimizePoopUses(options, index + 1,
                    free ? stock : stock - 1, poopUsed + 1L, item162Maxxed, memo);
                var useValue = option.Marginal + useTail.Value;
                if (useValue > best.Value + 1e-12)
                {
                    best.Value = useValue;
                    best.Uses = PrependChoice(true, useTail.Uses);
                }
            }
            memo[key] = best;
            return best;
        }

        private static bool[] PrependChoice(bool choice, bool[] tail)
        {
            var result = new bool[tail.Length + 1];
            result[0] = choice;
            Array.Copy(tail, 0, result, 1, tail.Length);
            return result;
        }

        internal static SeedPurchaseCandidate SelectSeedPurchase(
            IEnumerable<SeedPurchaseCandidate> candidates, long availableSeeds)
        {
            if (availableSeeds < 0L) throw new ArgumentOutOfRangeException("availableSeeds");
            if (candidates == null) return null;
            return candidates.Where(x => x != null && x.ExactCost > 0L
                                          && x.ExactCost <= availableSeeds
                                          && x.FiniteHorizonValue > 0.0)
                .OrderByDescending(x => x.FiniteHorizonValue / x.ExactCost)
                .ThenBy(x => x.ExactCost).ThenBy(x => x.FruitId).FirstOrDefault();
        }
    }

    internal sealed class YggdrasilManager
    {
        private readonly Character _character;
        internal static string LastSeedDecision { get; private set; } = "Yggdrasil is not yet evaluated";
        internal static string LastFruitDecision { get; private set; } = "Fruit policy is not yet evaluated";
        internal static string LastRewardPreview { get; private set; } = "Fruit rewards are not yet previewed";

        public YggdrasilManager()
        {
            _character = Main.Character;
        }

        internal static bool AnyHarvestable()
        {
            if (Main.Character == null || Main.Character.yggdrasilController == null) return false;
            var threshold = (int)Main.Character.yggdrasilController.fruits[0].tierThreshold();
            return Main.Character.yggdrasil.fruits.Any(x => x.maxTier > 0
                && YggdrasilEventController.HarvestTier(x.seconds, x.maxTier, threshold) > 0);
        }

        internal bool NeedsHarvest()
        {
            var threshold = (int)_character.yggdrasilController.fruits[0].tierThreshold();
            if (_character.yggdrasil.fruits.Any(x => x.maxTier > 0
                && YggdrasilEventController.HarvestTier(x.seconds, x.maxTier, threshold)
                   >= x.maxTier)) return true;
            var remaining = SecondsUntilReset();
            return remaining <= 5.0 && _character.yggdrasil.fruits.Any(x => x.maxTier > 0
                && YggdrasilEventController.HarvestTier(x.seconds, x.maxTier, threshold) > 0);
        }

        internal bool NeedsSwap()
        {
            var threshold = Math.Max(1, Settings.YggSwapThreshold);
            var tierSeconds = (int)_character.yggdrasilController.fruits[0].tierThreshold();
            return _character.yggdrasil.fruits.Any(x => x.maxTier > 0
                && YggdrasilEventController.HarvestTier(x.seconds, x.maxTier, tierSeconds)
                   >= Math.Min(x.maxTier, threshold));
        }

        internal void ManageYggHarvest()
        {
            ExecutionSafety.ReportHold("ygg-root-required",
                "Yggdrasil consumption requires the caller-owned nonzero root transaction.");
        }

        internal void CheckFruits()
        {
            ExecutionSafety.ReportHold("ygg-root-required",
                "Yggdrasil activation/upgrades require the caller-owned nonzero root transaction.");
        }

        internal static void HarvestAll()
        {
            ExecutionSafety.ReportHold("ygg-root-required",
                "Partial-fruit collection requires the caller-owned nonzero root transaction.");
        }

        internal void ManageYggHarvest(RootTransaction root)
        {
            if (root == null || root.IsClosed || !NeedsHarvest()) return;
            var plan = BuildConsumePlan(false);
            var owner = AutopilotOwnsYgg();
            var configured = root.ExecuteChild(new YggNativeIntent(_character,
                YggNativeAction.Configure, -1, owner, false, plan));
            if (!configured.RequiredStepSatisfied) return;
            root.ExecuteChild(new YggNativeIntent(_character,
                YggNativeAction.ConsumeMax, -1, owner, false, plan));
        }

        internal static void HarvestAll(RootTransaction root)
        {
            if (root == null || root.IsClosed || Main.Character == null) return;
            var manager = new YggdrasilManager();
            var plan = manager.BuildConsumePlan(true);
            var owner = AutopilotOwnsYgg();
            var configured = root.ExecuteChild(new YggNativeIntent(Main.Character,
                YggNativeAction.Configure, -1, owner, true, plan));
            if (!configured.RequiredStepSatisfied) return;
            root.ExecuteChild(new YggNativeIntent(Main.Character,
                YggNativeAction.ConsumePartial, -1, owner, true, plan));
        }

        internal void CheckFruits(RootTransaction root)
        {
            if (root == null || root.IsClosed) return;
            var autopilot = AutopilotOwnsYgg();
            if (!Settings.ActivateFruits && !autopilot) return;
            if (autopilot)
            {
                var seed = SelectLiveSeedPurchase();
                if (seed != null)
                {
                    var purchase = root.ExecuteChild(new YggNativeIntent(_character,
                        YggNativeAction.Upgrade, seed.FruitId, true, false, null));
                    LastSeedDecision = purchase.RequiredStepSatisfied
                        ? "Bought exact finite-horizon tier for " + GameNames.Fruit(_character, seed.FruitId)
                        : "Exact seed purchase was held/rejected; replan required";
                    if (!purchase.RequiredStepSatisfied) return;
                }
            }

            var consumePlan = BuildConsumePlan(false);
            root.ExecuteChild(new YggNativeIntent(_character,
                YggNativeAction.Configure, -1, autopilot, false, consumePlan));
            var threshold = (int)_character.yggdrasilController.fruits[0].tierThreshold();
            var remaining = SecondsUntilReset();
            for (var id = 0; id < _character.yggdrasil.fruits.Count; id++)
            {
                var fruit = _character.yggdrasil.fruits[id];
                if (fruit.maxTier <= 0 || fruit.permCostPaid || fruit.activated) continue;
                var candidate = new FruitActivationCandidate
                {
                    FruitId = id,
                    Seconds = fruit.seconds,
                    ActivationBenefit = LiveActivationBenefit(id, threshold, remaining)
                };
                if (!YggdrasilEventController.ShouldActivate(candidate, remaining,
                        _character.yggdrasil.resetFactor, threshold))
                    continue;
                root.ExecuteChild(new YggNativeIntent(_character,
                    YggNativeAction.Activate, id, autopilot, false, null));
            }
        }

        internal static void ReadTooltipLog(bool doLog)
        {
            // Historical code marked every native event-log line by mutating it.  Reward telemetry
            // now comes from exact typed previews/postconditions, so this intentionally does nothing.
        }

        private SeedPurchaseCandidate SelectLiveSeedPurchase()
        {
            var controller = _character.yggdrasilController;
            var fruitController = ControllerFor();
            if (fruitController == null) return null;
            var cap = controller.capTier();
            var count = Math.Min(_character.yggdrasil.fruits.Count, controller.baseSeedCost.Count);
            var threshold = (int)fruitController.tierThreshold();
            var remaining = SecondsUntilReset();
            var candidates = new List<SeedPurchaseCandidate>();
            for (var id = 0; id < count; id++)
            {
                var tier = _character.yggdrasil.fruits[id].maxTier;
                if (tier >= cap || !FruitUnlockEligible(id)) continue;
                var cost = controller.baseSeedCost[id] * (tier + 1L) * (tier + 1L);
                var projectedSeconds = double.IsPositiveInfinity(remaining)
                    || remaining == double.MaxValue ? double.MaxValue
                    : _character.yggdrasil.fruits[id].seconds + remaining;
                var beforeTier = YggdrasilEventController.HarvestTier(projectedSeconds,
                    tier, threshold);
                var afterTier = YggdrasilEventController.HarvestTier(projectedSeconds,
                    tier + 1L, threshold);
                var beforeFactor = fruitController.tierFactor(beforeTier);
                var afterFactor = fruitController.tierFactor(afterTier);
                // Exact native seed previews include the fruit-specific baseSeedReward and every
                // live equipment/NGU/Quest/First-Harvest multiplier.  Use the better selectable
                // eat/harvest seed branch; non-seed fruit rewards remain scheduler-unpriced rather
                // than receiving the old arbitrary .25/1 multiplier.
                var beforeSeeds = Math.Max(fruitController.seedReward(id, beforeFactor, 1f),
                    fruitController.harvestSeedReward(id, beforeFactor, 1f));
                var afterSeeds = Math.Max(fruitController.seedReward(id, afterFactor, 1f),
                    fruitController.harvestSeedReward(id, afterFactor, 1f));
                candidates.Add(new SeedPurchaseCandidate
                {
                    FruitId = id,
                    CurrentTier = tier,
                    ExactCost = cost,
                    FiniteHorizonValue = Math.Max(0L, afterSeeds - beforeSeeds),
                    PermanentTarget = FruitPermanentTarget(id)
                });
            }
            var selected = YggdrasilEventController.SelectSeedPurchase(candidates,
                _character.yggdrasil.seeds);
            if (selected == null)
            {
                var next = candidates.OrderBy(x => x.ExactCost).FirstOrDefault();
                LastSeedDecision = next == null
                    ? "Every eligible fruit tier is native-capped"
                    : "Saving " + _character.yggdrasil.seeds + "/" + next.ExactCost
                      + " seeds for the next exact finite-horizon tier";
            }
            return selected;
        }

        private PoopBatchPlan BuildConsumePlan(bool includePartial)
        {
            var allPreviews = BuildLiveRewardPreviews();
            var previews = allPreviews.Where(x => includePartial || x.PoopEligible).ToList();
            var item162 = IsItemMaxxed(162);
            var plan = YggdrasilEventController.PlanPoopBatch(previews,
                _character.arbitrary.poop1Count, _character.stats.poopUsed, item162);
            LastFruitDecision = plan.Decisions.Length == 0
                ? "No mature fruit event"
                : "Exact maturity batch: " + string.Join(", ", plan.Decisions.Select(x =>
                    x.FruitId + ":" + x.Kind + (x.UsePoop ? x.FreePoop ? "+free-poop" : "+poop" : ""))
                    .ToArray());
            LastRewardPreview = previews.Count == 0 ? "No mature reward preview"
                : string.Join("; ", previews.Select(x => "fruit " + x.FruitId + " tier " + x.Tier
                    + " seeds eat/harvest=" + x.EatSeedsWithoutPoop + "/"
                    + x.HarvestSeedsWithoutPoop + " target=" + x.PermanentTarget).ToArray());
            return plan;
        }

        private List<FruitRewardPreview> BuildLiveRewardPreviews()
        {
            var result = new List<FruitRewardPreview>();
            var threshold = (int)_character.yggdrasilController.fruits[0].tierThreshold();
            var poopModifier = (float)YggdrasilEventController.ClampPoopModifier(
                _character.allArbitrary.poopModifier());
            for (var id = 0; id < _character.yggdrasil.fruits.Count; id++)
            {
                var fruit = _character.yggdrasil.fruits[id];
                if (fruit.maxTier <= 0) continue;
                var tier = YggdrasilEventController.HarvestTier(fruit.seconds,
                    fruit.maxTier, threshold);
                if (tier <= 0) continue;
                var controller = ControllerFor();
                if (controller == null) continue;
                var factor = controller.tierFactor(tier);
                var eatSeeds = controller.seedReward(id, factor, 1f);
                var eatPoopSeeds = controller.seedReward(id, factor, poopModifier);
                var harvestSeeds = controller.harvestSeedReward(id, factor, 1f);
                var harvestPoopSeeds = controller.harvestSeedReward(id, factor, poopModifier);
                // The native seed calls include live First Harvest, equipment, NGU, and Quest
                // bonuses exactly.  Specific reward value remains a typed scheduler shadow input;
                // Pomegranate/Watermelon already include their native double seeds in eat.
                var specific = id == 4 || id == 12 ? 0.0
                    : FruitSpecificShadow(id) * factor;
                result.Add(new FruitRewardPreview
                {
                    FruitId = id,
                    Tier = tier,
                    Mature = true,
                    PoopEligible = tier >= fruit.maxTier,
                    EatSeedsWithoutPoop = id == 4 || id == 12 ? harvestSeeds : eatSeeds,
                    EatSeedsWithPoop = id == 4 || id == 12 ? harvestPoopSeeds : eatPoopSeeds,
                    HarvestSeedsWithoutPoop = harvestSeeds,
                    HarvestSeedsWithPoop = harvestPoopSeeds,
                    SpecificWithoutPoop = specific,
                    SpecificWithPoop = specific * poopModifier,
                    SpecificShadowValue = 1.0,
                    SeedShadowValue = 1.0,
                    PermanentTarget = FruitPermanentTarget(id),
                    SourceExact = id == 4 || id == 12
                });
            }
            return result;
        }

        private FruitController ControllerFor()
        {
            return _character.yggdrasilController.fruits == null
                   || _character.yggdrasilController.fruits.Length == 0
                ? null : _character.yggdrasilController.fruits[0];
        }

        private double LiveActivationBenefit(int id, int threshold, double remaining)
        {
            var fruit = _character.yggdrasil.fruits[id];
            if (!YggdrasilEventController.CanMatureBeforeReset(fruit.seconds, remaining,
                    _character.yggdrasil.resetFactor, threshold)) return 0.0;
            var possibleTier = Math.Min(fruit.maxTier,
                YggdrasilEventController.HarvestTier(fruit.seconds + remaining,
                    fruit.maxTier, threshold));
            return FruitSpecificShadow(id) * Math.Max(1, possibleTier);
        }

        private double SecondsUntilReset()
        {
            var plan = Main.Autopilot == null ? null : Main.Autopilot.Plan;
            if (plan == null || plan.RebirthSeconds < 0) return double.MaxValue;
            return Math.Max(0.0, plan.EffectiveAllocationTarget(_character)
                                 - _character.rebirthTime.totalseconds);
        }

        private bool FruitUnlockEligible(int id)
        {
            if (id == 8 && _character.yggdrasil.fruits[id].maxTier == 0)
                return _character.allChallenges.trollChallenge.completions() >= 5;
            if (id == 9) return _character.settings.itopodOn;
            if (id == 10) return _character.achievements.achievementComplete.Count > 145
                                 && _character.achievements.achievementComplete[145];
            if (id == 14) return _character.settings.beastOn;
            if (id >= 15 && id <= 20) return _character.cards.cardsOn;
            return true;
        }

        private bool IsItemMaxxed(int id)
        {
            var flags = _character.inventory.itemList.itemMaxxed;
            return flags != null && id >= 0 && id < flags.Count && flags[id];
        }

        private static double FruitSpecificShadow(int id)
        {
            switch (id)
            {
                case 3: return 10.0; // EXP
                case 7: return 9.0;  // AP
                case 9: return 8.0;  // PP
                case 14: return 8.0; // QP
                case 2:
                case 5:
                case 6:
                case 8:
                case 11: return 7.0;
                case 10:
                case 13: return 6.0;
                default: return id >= 15 && id <= 20 ? 5.0 : 1.0;
            }
        }

        private static PermanentEffectTarget FruitPermanentTarget(int id)
        {
            switch (id)
            {
                case 0: return PermanentEffectTarget.Gold;
                case 2:
                case 6:
                case 8: return PermanentEffectTarget.Adventure;
                case 5: return PermanentEffectTarget.DropChance;
                case 10:
                case 13: return PermanentEffectTarget.Resource;
                case 11: return PermanentEffectTarget.Number;
                case 3:
                case 7:
                case 9:
                case 14: return PermanentEffectTarget.Terminal;
                default: return PermanentEffectTarget.None;
            }
        }

        private static bool AutopilotOwnsYgg()
        {
            return Main.Autopilot != null && Main.Autopilot.CanExecuteSafe
                   && Main.Autopilot.Config.ManageYggdrasil;
        }

        private enum YggNativeAction
        {
            Configure,
            Activate,
            Upgrade,
            ConsumeMax,
            ConsumePartial
        }

        private sealed class YggMutationState
        {
            internal long Seeds;
            internal int PoopStock;
            internal long PoopUsed;
            internal long[] Tiers;
            internal float[] Seconds;
            internal bool[] Active;
            internal int[] Harvests;
            internal bool[] Eat;
            internal bool[] UsePoop;
            internal bool PoopOnlyMax;
            internal long IdleEnergy;
            internal long IdleMagic;
        }

        /*
        VERIFIED YGG NATIVE INTENT

        Configure is reversible and snapshots every global toggle.  Activation, upgrade, and
        consumption are finite-resource actions with no invented inverse.  Consume verification
        checks each planned mature fruit's native harvest counter/timer and exact planned Poop
        stock/counter transition; one global native call is the only consumption mutation.
        */
        private sealed class YggNativeIntent :
            IMutationIntent<YggMutationState, bool, YggMutationState>
        {
            private readonly Character _character;
            private readonly YggNativeAction _action;
            private readonly int _fruitId;
            private readonly bool _autopilot;
            private readonly bool _partial;
            private readonly PoopBatchPlan _plan;

            internal YggNativeIntent(Character character, YggNativeAction action, int fruitId,
                bool autopilot, bool partial, PoopBatchPlan plan)
            {
                _character = character;
                _action = action;
                _fruitId = fruitId;
                _autopilot = autopilot;
                _partial = partial;
                _plan = plan;
            }

            public string Id { get { return "ygg/" + _action.ToString().ToLowerInvariant(); } }
            public MutationClass Class { get { return MutationClass.Yggdrasil; } }
            public MutationRisk Risk
            {
                get { return _action == YggNativeAction.Configure
                    ? MutationRisk.Reversible : MutationRisk.FiniteResource; }
            }
            public MutationOwner Owner { get { return _autopilot ? MutationOwner.Autopilot : MutationOwner.Legacy; } }
            public string BindingId { get { return "Yggdrasil." + _action; } }
            public bool Required { get { return true; } }
            public bool CanCompensate { get { return _action == YggNativeAction.Configure; } }
            public bool CreatesNewEpoch { get { return false; } }
            public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

            public YggMutationState CaptureBefore(MutationContext context)
            {
                return Capture();
            }

            private YggMutationState Capture()
            {
                var fruits = _character.yggdrasil.fruits;
                return new YggMutationState
                {
                    Seeds = _character.yggdrasil.seeds,
                    PoopStock = _character.arbitrary.poop1Count,
                    PoopUsed = _character.stats.poopUsed,
                    Tiers = fruits.Select(x => x.maxTier).ToArray(),
                    Seconds = fruits.Select(x => x.seconds).ToArray(),
                    Active = fruits.Select(x => x.activated).ToArray(),
                    Harvests = fruits.Select(x => x.harvests).ToArray(),
                    Eat = fruits.Select(x => x.eatFruit).ToArray(),
                    UsePoop = fruits.Select(x => x.usePoop).ToArray(),
                    PoopOnlyMax = _character.settings.poopOnlyMaxTier,
                    IdleEnergy = _character.idleEnergy,
                    IdleMagic = _character.magic.idleMagic
                };
            }

            public PreconditionResult CheckPreconditions(MutationContext context,
                YggMutationState before)
            {
                if (_action == YggNativeAction.Configure)
                    return _plan != null && _plan.InitialStock == before.PoopStock
                                         && _plan.InitialCounter == before.PoopUsed
                        ? PreconditionResult.Ready()
                        : PreconditionResult.Hold(
                            "A fresh typed consume plan with exact Poop state is required.");
                if (_action == YggNativeAction.Activate)
                {
                    if (!ValidFruit(before) || before.Tiers[_fruitId] <= 0
                        || before.Active[_fruitId])
                        return PreconditionResult.Hold("Fruit is absent/already active.");
                    var fruit = _character.yggdrasil.fruits[_fruitId];
                    var cost = _character.yggdrasilController.activationCost[_fruitId];
                    var funded = _character.yggdrasilController.usesEnergy[_fruitId]
                        ? _character.curEnergy >= cost : _character.magic.curMagic >= cost;
                    if (!funded) return PreconditionResult.Hold(
                        "Activation cost is not funded by the live resource total.");
                    return !fruit.permCostPaid ? PreconditionResult.Ready()
                        : PreconditionResult.AlreadySatisfied("Permanent fruit autoactivates for free.");
                }
                if (_action == YggNativeAction.Upgrade)
                {
                    if (!ValidFruit(before)) return PreconditionResult.Hold("Fruit ID is invalid.");
                    var tier = before.Tiers[_fruitId];
                    if (tier >= _character.yggdrasilController.capTier())
                        return PreconditionResult.Hold("Fruit is native-capped.");
                    var cost = _character.yggdrasilController.baseSeedCost[_fruitId]
                               * (tier + 1L) * (tier + 1L);
                    return cost > 0 && before.Seeds >= cost ? PreconditionResult.Ready()
                        : PreconditionResult.Hold("Exact seed cost is not funded.");
                }
                if (_plan == null || _plan.Decisions.Length == 0)
                    return PreconditionResult.AlreadySatisfied("No fruit has a completed tier.");
                if (_plan.InitialStock != before.PoopStock
                    || _plan.InitialCounter != before.PoopUsed)
                    return PreconditionResult.Hold("Poop stock/counter changed after planning.");
                var threshold = (int)_character.yggdrasilController.fruits[0].tierThreshold();
                var actual = new List<int>();
                for (var id = 0; id < before.Tiers.Length; id++)
                {
                    var tier = YggdrasilEventController.HarvestTier(before.Seconds[id],
                        before.Tiers[id], threshold);
                    if (tier > 0 && (_partial || tier >= before.Tiers[id])) actual.Add(id);
                }
                if (!actual.SequenceEqual(_plan.Decisions.Select(x => x.FruitId)))
                    return PreconditionResult.Hold(
                        "Fruit maturity set changed after exact reward planning.");
                return PreconditionResult.Ready();
            }

            public bool Apply(MutationContext context, RootTransactionToken token,
                YggMutationState before)
            {
                if (_action == YggNativeAction.Configure)
                {
                    _character.settings.poopOnlyMaxTier = true;
                    var byId = _plan.Decisions.ToDictionary(x => x.FruitId);
                    for (var id = 0; id < _character.yggdrasil.fruits.Count; id++)
                    {
                        FruitConsumeDecision decision;
                        if (!byId.TryGetValue(id, out decision))
                        {
                            _character.yggdrasil.fruits[id].usePoop = false;
                            continue;
                        }
                        _character.yggdrasil.fruits[id].eatFruit = decision.Kind == FruitConsumeKind.Eat;
                        _character.yggdrasil.fruits[id].usePoop = decision.UsePoop;
                    }
                    return true;
                }
                if (_action == YggNativeAction.Activate)
                {
                    var all = _character.yggdrasilController;
                    var cost = all.activationCost[_fruitId];
                    if (all.usesEnergy[_fruitId] && _character.idleEnergy < cost)
                        _character.removeMostEnergy();
                    if (!all.usesEnergy[_fruitId] && _character.magic.idleMagic < cost)
                        _character.removeMostMagic();
                    if (_character.yggdrasilController.fruits == null
                        || _character.yggdrasilController.fruits.Length == 0) return false;
                    _character.yggdrasilController.fruits[0].activate(_fruitId);
                    return true;
                }
                if (_action == YggNativeAction.Upgrade)
                {
                    var page = _fruitId / 9;
                    var slot = _fruitId - page * 9;
                    var old = _character.yggdrasilController.curPage;
                    try
                    {
                        _character.yggdrasilController.changePage(page);
                        if (_character.yggdrasilController.fruits == null
                            || slot < 0 || slot >= _character.yggdrasilController.fruits.Length)
                            return false;
                        _character.yggdrasilController.fruits[slot].upgrade();
                        return true;
                    }
                    finally
                    {
                        _character.yggdrasilController.changePage(old);
                    }
                }
                if (_action == YggNativeAction.ConsumePartial)
                    _character.yggdrasilController.consumeAll(_partial);
                else
                    _character.yggdrasilController.consumeAll();
                _character.yggdrasilController.refreshMenu();
                return true;
            }

            public VerificationResult<YggMutationState> Verify(MutationContext context,
                YggMutationState before, MutationApplyObservation<bool> apply)
            {
                var after = Capture();
                var valid = false;
                if (_action == YggNativeAction.Configure)
                {
                    valid = after.PoopOnlyMax;
                    foreach (var decision in _plan.Decisions)
                        valid &= after.Eat[decision.FruitId] == (decision.Kind == FruitConsumeKind.Eat)
                                 && after.UsePoop[decision.FruitId] == decision.UsePoop;
                }
                else if (_action == YggNativeAction.Activate)
                    valid = after.Active[_fruitId] && !before.Active[_fruitId];
                else if (_action == YggNativeAction.Upgrade)
                {
                    var cost = _character.yggdrasilController.baseSeedCost[_fruitId]
                               * (before.Tiers[_fruitId] + 1L) * (before.Tiers[_fruitId] + 1L);
                    valid = after.Tiers[_fruitId] == before.Tiers[_fruitId] + 1L
                            && after.Seeds == before.Seeds - cost;
                }
                else
                {
                    valid = true;
                    foreach (var decision in _plan.Decisions)
                    {
                        if (!_partial && decision.FruitId < before.Tiers.Length
                            && YggdrasilEventController.HarvestTier(before.Seconds[decision.FruitId],
                                before.Tiers[decision.FruitId],
                                (int)_character.yggdrasilController.fruits[0].tierThreshold())
                               < before.Tiers[decision.FruitId]) continue;
                        valid &= after.Seconds[decision.FruitId] == 0f
                                 && after.Harvests[decision.FruitId]
                                    == before.Harvests[decision.FruitId] + 1;
                    }
                    valid &= after.PoopStock == _plan.FinalStock
                             && after.PoopUsed == _plan.FinalCounter;
                }
                return valid ? VerificationResult<YggMutationState>.Satisfied(after,
                        "Exact Yggdrasil state transition verified.")
                    : VerificationResult<YggMutationState>.Failed(
                        "Yggdrasil mutation lacked its exact tier/resource/toggle postcondition.");
            }

            public CompensationResult Compensate(MutationContext context, RecoveryToken token,
                YggMutationState before, MutationApplyObservation<bool> apply)
            {
                if (!CanCompensate)
                    return CompensationResult.NotSupported("Fruit resources/rewards have no safe inverse.");
                _character.settings.poopOnlyMaxTier = before.PoopOnlyMax;
                for (var id = 0; id < _character.yggdrasil.fruits.Count; id++)
                {
                    _character.yggdrasil.fruits[id].eatFruit = before.Eat[id];
                    _character.yggdrasil.fruits[id].usePoop = before.UsePoop[id];
                }
                return BeforeStateMatches(before, Capture())
                    ? CompensationResult.Restored("Fruit toggles restored.")
                    : CompensationResult.Failed("Fruit toggles were not restored.");
            }

            public bool BeforeStateMatches(YggMutationState expected, YggMutationState observed)
            {
                return expected.Seeds == observed.Seeds
                       && expected.PoopStock == observed.PoopStock
                       && expected.PoopUsed == observed.PoopUsed
                       && expected.Tiers.SequenceEqual(observed.Tiers)
                       && expected.Seconds.SequenceEqual(observed.Seconds)
                       && expected.Active.SequenceEqual(observed.Active)
                       && expected.Harvests.SequenceEqual(observed.Harvests)
                       && expected.Eat.SequenceEqual(observed.Eat)
                       && expected.UsePoop.SequenceEqual(observed.UsePoop)
                       && expected.PoopOnlyMax == observed.PoopOnlyMax;
            }

            public string FingerprintBefore(YggMutationState state) { return Fingerprint(state); }
            public string FingerprintAfter(YggMutationState state) { return Fingerprint(state); }

            private bool ValidFruit(YggMutationState state)
            {
                return _fruitId >= 0 && _fruitId < state.Tiers.Length;
            }

            private static string Fingerprint(YggMutationState state)
            {
                return state.Seeds + ":" + state.PoopStock + ":" + state.PoopUsed + ":"
                       + string.Join(",", state.Tiers.Select(x => x.ToString()).ToArray()) + ":"
                       + string.Join(",", state.Harvests.Select(x => x.ToString()).ToArray());
            }

        }
    }
}
