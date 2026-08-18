using System;
using System.Collections.Generic;
using System.Linq;

/*
FILE PURPOSE

Purpose: Own the exact Cards/Cooking planning boundary. The pure mechanics below model six
non-fungible Mayos, live/offline quantization, componentwise card portfolios, END delivery
readiness, native deck event-order slack, and the source-exact disjoint Cooking-pair score.

Mechanism: Card management first protects every held END, reserves 99 of each Mayo until physical
item 492 is terminally owned, installs tags/generators, and establishes two live deck slots (three
when Chonker is also due) by positive-value casts or minimum-loss recycling. Ordinary casts and
Cooking meals are typed finite-resource child intents when a task-1 RootTransaction is supplied;
the compatibility overload uses the same task-5 exact Card adapter and postconditions until task 29
wires the shared root. END conversion itself is deliberately a handoff plan: task 9 owns the filter
override/restore transaction and exact inventory-credit proof.

Inputs and outputs: Live Character/config state produces native tag/generator changes, verified
ordinary Card or Cooking mutations, and LastEndHandoff telemetry. Pure immutable inputs produce
portfolio, deck-service, Mayo-rate, END-forecast, and Cooking-pair plans used by isolated tests.

Invariants and safety: Six Mayo coordinates are never summed for affordability. Missing item 492
always reserves [99,99,99,99,99,99]. END is protected before all other Card work and duplicate
conversion stops on either terminal ordinary ownership or any recoverable physical copy. Foils and
Chonkers use the same value/cost/recycle model as normal Cards; rarity and tier never gate a
positive cast. RNG state is neither captured nor steered. Cooking never consumes at stored/applied
bonus 3, optimizes locked levels exactly, counts legs twice and weapon 2 zero times, and caps the
equipment factor at 1.5.

Extension points and non-goals: Task 29 should pass its active RootTransaction to the overloads and
publish LastEndHandoff to task 9. A future loadout lease may use CookingEquipmentMultiplier before a
synchronous meal; this file never equips gear by itself. Card preview/RNG-aware steering remains
off. This controller does not spend AP, inject code, modify filters, or perform terminal END consume.
*/
namespace NGUInjector.Autopilot
{
    internal enum HeldCardKind
    {
        Normal,
        Foil,
        BigChonker,
        End
    }

    internal sealed class CardPortfolioCandidate
    {
        internal readonly int Id;
        internal readonly double Value;
        internal readonly int[] MayoCost;
        internal readonly HeldCardKind Kind;
        internal readonly bool IsProtected;

        internal CardPortfolioCandidate(int id, double value, int[] mayoCost,
            HeldCardKind kind, bool isProtected)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                throw new ArgumentOutOfRangeException("value");
            CardCookingMechanics.ValidateSix(mayoCost, "mayoCost");
            Id = id;
            Value = value;
            MayoCost = (int[])mayoCost.Clone();
            Kind = kind;
            IsProtected = isProtected;
        }
    }

    internal sealed class CardPortfolioPlan
    {
        internal readonly int[] SelectedIds;
        internal readonly int[] Spent;
        internal readonly int[] AvailableAfterReserve;
        internal readonly int[] Reserve;
        internal readonly double Value;
        internal readonly bool Exact;
        internal readonly int LabelsExamined;
        internal readonly string Reason;

        internal CardPortfolioPlan(int[] selectedIds, int[] spent, int[] available,
            int[] reserve, double value, bool exact, int labelsExamined, string reason)
        {
            SelectedIds = (int[])selectedIds.Clone();
            Spent = (int[])spent.Clone();
            AvailableAfterReserve = (int[])available.Clone();
            Reserve = (int[])reserve.Clone();
            Value = value;
            Exact = exact;
            LabelsExamined = labelsExamined;
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class MayoAdvanceResult
    {
        internal readonly long Amount;
        internal readonly double Progress;
        internal readonly long Awarded;

        internal MayoAdvanceResult(long amount, double progress, long awarded)
        {
            Amount = amount;
            Progress = progress;
            Awarded = awarded;
        }
    }

    internal sealed class MayoGeneratorPlan
    {
        internal readonly int[] CurrencyIds;
        internal readonly double AggregateRatePerSecond;
        internal readonly double WeightedRate;
        internal readonly double SecondsToFirstCompletion;

        internal MayoGeneratorPlan(int[] ids, double aggregateRate, double weightedRate,
            double secondsToFirstCompletion)
        {
            CurrencyIds = (int[])ids.Clone();
            AggregateRatePerSecond = aggregateRate;
            WeightedRate = weightedRate;
            SecondsToFirstCompletion = secondsToFirstCompletion;
        }
    }

    internal sealed class DeckServiceCandidate
    {
        internal readonly int Id;
        internal readonly double PermanentValue;
        internal readonly double RecycleValue;
        internal readonly bool Affordable;
        internal readonly bool IsProtected;
        internal readonly HeldCardKind Kind;

        internal DeckServiceCandidate(int id, double permanentValue, double recycleValue,
            bool affordable, bool isProtected, HeldCardKind kind)
        {
            Id = id;
            PermanentValue = Math.Max(0.0, permanentValue);
            RecycleValue = Math.Max(0.0, recycleValue);
            Affordable = affordable;
            IsProtected = isProtected;
            Kind = kind;
        }
    }

    internal enum DeckServiceActionKind
    {
        Cast,
        Recycle
    }

    internal sealed class DeckServiceAction
    {
        internal readonly int CandidateId;
        internal readonly DeckServiceActionKind Kind;
        internal readonly double Loss;

        internal DeckServiceAction(int candidateId, DeckServiceActionKind kind, double loss)
        {
            CandidateId = candidateId;
            Kind = kind;
            Loss = loss;
        }
    }

    internal sealed class DeckServicePlan
    {
        internal readonly DeckServiceAction[] Actions;
        internal readonly int InitialFreeSlots;
        internal readonly int RequiredFreeSlots;
        internal readonly bool Admitted;
        internal readonly string Reason;

        internal DeckServicePlan(DeckServiceAction[] actions, int initialFreeSlots,
            int requiredFreeSlots, bool admitted, string reason)
        {
            Actions = (DeckServiceAction[])actions.Clone();
            InitialFreeSlots = initialFreeSlots;
            RequiredFreeSlots = requiredFreeSlots;
            Admitted = admitted;
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class EndCardFilterSnapshot
    {
        internal readonly bool StateKnown;
        internal readonly bool ItemFiltered;
        internal readonly bool LootFilter;
        internal readonly bool FilterOn;
        internal readonly bool FilterMisc;

        internal EndCardFilterSnapshot(bool stateKnown, bool itemFiltered, bool lootFilter,
            bool filterOn, bool filterMisc)
        {
            StateKnown = stateKnown;
            ItemFiltered = itemFiltered;
            LootFilter = lootFilter;
            FilterOn = filterOn;
            FilterMisc = filterMisc;
        }
    }

    internal sealed class EndCardHandoffPlan
    {
        internal readonly bool HasTerminalPiece;
        internal readonly bool HasRecoverableCopy;
        internal readonly int HeldEndCards;
        internal readonly int[] MayoAmounts;
        internal readonly int[] MayoDeficits;
        internal readonly LootCapacityProof InventoryCapacity;
        internal readonly EndCardFilterSnapshot Filters;
        internal readonly bool ReadyForTerminalTransaction;
        internal readonly bool StopDuplicateConsume;
        internal readonly string Reason;

        internal EndCardHandoffPlan(bool terminal, bool recoverable, int heldEndCards,
            int[] amounts, int[] deficits, LootCapacityProof inventoryCapacity,
            EndCardFilterSnapshot filters, bool ready, bool stopDuplicate, string reason)
        {
            HasTerminalPiece = terminal;
            HasRecoverableCopy = recoverable;
            HeldEndCards = heldEndCards;
            MayoAmounts = (int[])amounts.Clone();
            MayoDeficits = (int[])deficits.Clone();
            InventoryCapacity = inventoryCapacity;
            Filters = filters;
            ReadyForTerminalTransaction = ready;
            StopDuplicateConsume = stopDuplicate;
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class EndCardRollForecast
    {
        internal readonly double MeanNormalRolls;
        internal readonly long MedianNormalRolls;
        internal readonly long P90NormalRolls;
        internal readonly long P95NormalRolls;
        internal readonly long P99NormalRolls;
        internal readonly ForecastEvidence Evidence;

        internal EndCardRollForecast(double mean, long median, long p90, long p95, long p99,
            ForecastEvidence evidence)
        {
            MeanNormalRolls = mean;
            MedianNormalRolls = median;
            P90NormalRolls = p90;
            P95NormalRolls = p95;
            P99NormalRolls = p99;
            Evidence = evidence;
        }
    }

    internal sealed class CookingIngredientModel
    {
        internal readonly int CurrentLevel;
        internal readonly int TargetLevel;
        internal readonly double Weight;
        internal readonly double PairedWeight;
        internal readonly bool Unlocked;

        internal CookingIngredientModel(int currentLevel, int targetLevel, double weight,
            double pairedWeight, bool unlocked)
        {
            CurrentLevel = currentLevel;
            TargetLevel = targetLevel;
            Weight = weight;
            PairedWeight = pairedWeight;
            Unlocked = unlocked;
        }
    }

    internal sealed class CookingPairPlan
    {
        internal readonly int FirstLevel;
        internal readonly int SecondLevel;
        internal readonly double Score;

        internal CookingPairPlan(int firstLevel, int secondLevel, double score)
        {
            FirstLevel = firstLevel;
            SecondLevel = secondLevel;
            Score = score;
        }
    }

    internal enum CookingEquipmentSlot
    {
        Head,
        Chest,
        Legs,
        Boots,
        Weapon1,
        Weapon2,
        Accessory
    }

    internal static class CardCookingMechanics
    {
        internal const int MayoCurrencyCount = 6;
        internal const int EndMayoPerCurrency = 99;
        internal const double EndRollProbability = 0.01;
        internal const double LiveTicksPerSecond = 50.0;
        internal const double MayoNativeDivisor = 180000.0;
        internal const double CookingAppliedCap = 3.0;
        internal const double CookingAffixFactor = 1.03;
        internal const double CookingEquipmentCap = 1.5;

        private sealed class PortfolioLabel
        {
            internal int[] Spent;
            internal List<int> Selected;
            internal double Value;
        }

        internal static int[] EndMayoReserve(bool endTerminalPieceMissing)
        {
            return Enumerable.Repeat(endTerminalPieceMissing ? EndMayoPerCurrency : 0,
                MayoCurrencyCount).ToArray();
        }

        internal static bool CanSpendWithoutBreakingReserve(int[] balances, int[] cost, int[] reserve)
        {
            ValidateSix(balances, "balances");
            ValidateSix(cost, "cost");
            ValidateSix(reserve, "reserve");
            for (var i = 0; i < MayoCurrencyCount; i++)
                if (cost[i] > Math.Max(0, balances[i] - reserve[i])) return false;
            return true;
        }

        /*
        SIX-DIMENSIONAL PORTFOLIO

        Labels are keyed by the full componentwise spend vector, never total Mayo. Exact-cost
        duplicates retain only the greatest value. maxLabels is a declared bounded-controller
        limit; trimming changes Exact to false instead of silently claiming an exact optimum.
        */
        internal static CardPortfolioPlan SolveCardPortfolio(IEnumerable<CardPortfolioCandidate> source,
            int[] balances, int[] reserve, int maxLabels = int.MaxValue)
        {
            ValidateSix(balances, "balances");
            ValidateSix(reserve, "reserve");
            if (source == null) throw new ArgumentNullException("source");
            if (maxLabels <= 0) throw new ArgumentOutOfRangeException("maxLabels");
            var available = new int[MayoCurrencyCount];
            for (var i = 0; i < MayoCurrencyCount; i++)
                available[i] = Math.Max(0, balances[i] - reserve[i]);

            var labels = new List<PortfolioLabel>
            {
                new PortfolioLabel {Spent = new int[MayoCurrencyCount], Selected = new List<int>(), Value = 0.0}
            };
            var exact = true;
            var labelsExamined = 1;
            foreach (var candidate in source.Where(x => x != null && !x.IsProtected
                                                         && x.Kind != HeldCardKind.End
                                                         && x.Value > 0.0))
            {
                var merged = new Dictionary<string, PortfolioLabel>();
                Action<PortfolioLabel> admit = label =>
                {
                    var key = string.Join(",", label.Spent.Select(x => x.ToString()).ToArray());
                    PortfolioLabel old;
                    if (!merged.TryGetValue(key, out old) || label.Value > old.Value + 1e-12
                        || Math.Abs(label.Value - old.Value) <= 1e-12
                           && LexicographicallyBefore(label.Selected, old.Selected))
                        merged[key] = label;
                };
                for (var j = 0; j < labels.Count; j++)
                {
                    var label = labels[j];
                    admit(label);
                    var spent = new int[MayoCurrencyCount];
                    var feasible = true;
                    for (var k = 0; k < MayoCurrencyCount; k++)
                    {
                        spent[k] = label.Spent[k] + candidate.MayoCost[k];
                        if (spent[k] > available[k]) feasible = false;
                    }
                    if (!feasible) continue;
                    var selected = new List<int>(label.Selected) {candidate.Id};
                    admit(new PortfolioLabel {Spent = spent, Selected = selected,
                        Value = label.Value + candidate.Value});
                }
                labelsExamined += merged.Count;
                labels = merged.Values.ToList();
                if (labels.Count > maxLabels)
                {
                    exact = false;
                    labels = labels.OrderByDescending(x => x.Value)
                        .ThenBy(x => x.Spent.Sum(y => (long)y)).Take(maxLabels).ToList();
                }
            }
            var best = labels.OrderByDescending(x => x.Value)
                .ThenBy(x => x.Spent.Sum(y => (long)y))
                .ThenBy(x => string.Join(",", x.Selected.Select(y => y.ToString()).ToArray()))
                .First();
            return new CardPortfolioPlan(best.Selected.ToArray(), best.Spent, available, reserve,
                best.Value, exact, labelsExamined,
                exact ? "Exact six-coordinate label frontier." : "Bounded six-coordinate frontier; replan after one action.");
        }

        internal static double[] DiscreteMayoShadowValues(IEnumerable<CardPortfolioCandidate> source,
            int[] balances, int[] reserve, int maxLabels = 4096)
        {
            var candidates = source == null ? new CardPortfolioCandidate[0] : source.ToArray();
            var basis = SolveCardPortfolio(candidates, balances, reserve, maxLabels);
            var prices = new double[MayoCurrencyCount];
            for (var i = 0; i < MayoCurrencyCount; i++)
            {
                if (balances[i] < reserve[i])
                {
                    prices[i] = 1000000.0 + reserve[i] - balances[i];
                    continue;
                }
                var plus = (int[])balances.Clone();
                plus[i]++;
                var improved = SolveCardPortfolio(candidates, plus, reserve, maxLabels);
                prices[i] = Math.Max(0.0, improved.Value - basis.Value);
                if (prices[i] == 0.0) prices[i] = 1e-9 / (1.0 + balances[i]);
            }
            return prices;
        }

        internal static double LiveMayoAggregateRate(double totalMayoSpeed, int activeCount)
        {
            if (double.IsNaN(totalMayoSpeed) || double.IsInfinity(totalMayoSpeed)
                || totalMayoSpeed < 0.0) throw new ArgumentOutOfRangeException("totalMayoSpeed");
            if (activeCount <= 0 || totalMayoSpeed == 0.0) return 0.0;
            var ticks = Math.Max(1.0, Math.Ceiling(MayoNativeDivisor * activeCount / totalMayoSpeed));
            return LiveTicksPerSecond * activeCount / ticks;
        }

        internal static double LiveMayoSecondsToNextInteger(double totalMayoSpeed, int activeCount,
            double progress)
        {
            if (activeCount <= 0 || totalMayoSpeed <= 0.0) return double.PositiveInfinity;
            if (progress < 0.0 || progress >= 1.0) throw new ArgumentOutOfRangeException("progress");
            var perTick = totalMayoSpeed / (MayoNativeDivisor * activeCount);
            var ticks = Math.Max(1.0, Math.Ceiling((1.0 - progress) / perTick));
            return ticks / LiveTicksPerSecond;
        }

        internal static MayoAdvanceResult AdvanceLiveMayo(long amount, double progress,
            double totalMayoSpeed, int activeCount, long ticks, long maximum = long.MaxValue)
        {
            if (amount < 0 || maximum < amount || ticks < 0) throw new ArgumentOutOfRangeException();
            if (progress < 0.0 || progress >= 1.0) throw new ArgumentOutOfRangeException("progress");
            if (activeCount <= 0 || totalMayoSpeed <= 0.0 || ticks == 0)
                return new MayoAdvanceResult(amount, progress, 0);
            var step = totalMayoSpeed / (MayoNativeDivisor * activeCount);
            var awarded = 0L;
            for (var i = 0L; i < ticks; i++)
            {
                progress += step;
                if (progress < 1.0) continue;
                progress = 0.0; // Native live path discards overshoot and awards at most one/tick.
                if (amount < maximum) { amount++; awarded++; }
            }
            return new MayoAdvanceResult(amount, progress, awarded);
        }

        internal static MayoAdvanceResult AdvanceOfflineMayo(long amount, double progress,
            double totalMayoSpeed, int activeCount, double seconds, long maximum = long.MaxValue)
        {
            if (amount < 0 || maximum < amount || seconds < 0.0) throw new ArgumentOutOfRangeException();
            if (progress < 0.0 || progress >= 1.0) throw new ArgumentOutOfRangeException("progress");
            if (activeCount <= 0 || totalMayoSpeed <= 0.0 || seconds == 0.0)
                return new MayoAdvanceResult(amount, progress, 0);
            var combined = progress + totalMayoSpeed * seconds / (3600.0 * activeCount);
            var whole = (long)Math.Floor(combined);
            var awarded = Math.Min(whole, maximum - amount);
            return new MayoAdvanceResult(amount + awarded, combined - Math.Floor(combined), awarded);
        }

        internal static MayoGeneratorPlan ChooseMayoGenerators(double totalMayoSpeed, int maximumSlots,
            double[] shadowValues, double[] progress)
        {
            if (maximumSlots <= 0) return new MayoGeneratorPlan(new int[0], 0.0, 0.0,
                double.PositiveInfinity);
            if (shadowValues == null || progress == null || shadowValues.Length != MayoCurrencyCount
                || progress.Length != MayoCurrencyCount) throw new ArgumentException("Six Mayo coordinates required.");
            var bestMask = 0;
            var bestWeighted = double.NegativeInfinity;
            var bestFirst = double.PositiveInfinity;
            var bestRate = 0.0;
            for (var mask = 1; mask < 1 << MayoCurrencyCount; mask++)
            {
                var ids = Enumerable.Range(0, MayoCurrencyCount).Where(i => (mask & 1 << i) != 0).ToArray();
                if (ids.Length > maximumSlots) continue;
                var aggregate = LiveMayoAggregateRate(totalMayoSpeed, ids.Length);
                var each = aggregate / ids.Length;
                var weighted = ids.Sum(i => Math.Max(0.0, shadowValues[i]) * each);
                var first = ids.Min(i => LiveMayoSecondsToNextInteger(totalMayoSpeed, ids.Length, progress[i]));
                if (weighted > bestWeighted + 1e-12
                    || Math.Abs(weighted - bestWeighted) <= 1e-12 && first < bestFirst - 1e-12
                    || Math.Abs(weighted - bestWeighted) <= 1e-12 && Math.Abs(first - bestFirst) <= 1e-12
                       && mask < bestMask)
                {
                    bestMask = mask;
                    bestWeighted = weighted;
                    bestFirst = first;
                    bestRate = aggregate;
                }
            }
            var selected = Enumerable.Range(0, MayoCurrencyCount)
                .Where(i => (bestMask & 1 << i) != 0).ToArray();
            return new MayoGeneratorPlan(selected, bestRate, bestWeighted, bestFirst);
        }

        internal static int RequiredLiveDeckSlack(bool sadistic, bool endSecured,
            bool chonkerImminent)
        {
            var requirement = CardDeckRequirement.LiveFrame(true, chonkerImminent,
                sadistic && !endSecured);
            return requirement.RequiredFreeSlots;
        }

        /*
        PROACTIVE DECK SERVICE

        Every affordable positive Card may be cast, including Crappy/Bad, Foil, and Chonker. If
        slots remain short, the plan recycles the smallest nonnegative permanent-value loss.
        Protected Cards and a unique END are excluded; redundant END is eligible only after broad
        physical ownership has already been proven by task 8 and passed as redundantEndAllowed.
        */
        internal static DeckServicePlan PlanDeckService(IEnumerable<DeckServiceCandidate> source,
            int deckCount, int maximumDeckSize, int requiredFreeSlots, bool allowRecycle,
            bool redundantEndAllowed)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (deckCount < 0 || maximumDeckSize < deckCount || requiredFreeSlots < 0)
                throw new ArgumentOutOfRangeException();
            var free = maximumDeckSize - deckCount;
            var actions = new List<DeckServiceAction>();
            var candidates = source.Where(x => x != null).ToArray();
            foreach (var candidate in candidates.Where(x => !x.IsProtected && x.Affordable
                                                             && x.Kind != HeldCardKind.End
                                                             && x.PermanentValue > 0.0)
                         .OrderByDescending(x => x.PermanentValue).ThenBy(x => x.Id))
            {
                if (free >= requiredFreeSlots) break;
                actions.Add(new DeckServiceAction(candidate.Id, DeckServiceActionKind.Cast, 0.0));
                free++;
            }
            if (allowRecycle && free < requiredFreeSlots)
            {
                var already = new HashSet<int>(actions.Select(x => x.CandidateId));
                foreach (var candidate in candidates.Where(x => (!x.IsProtected
                                                                  || x.Kind == HeldCardKind.End
                                                                     && redundantEndAllowed)
                                                                 && !already.Contains(x.Id)
                                                                 && (x.Kind != HeldCardKind.End || redundantEndAllowed))
                             .OrderBy(x => Math.Max(0.0, x.PermanentValue - x.RecycleValue))
                             .ThenBy(x => x.Id))
                {
                    if (free >= requiredFreeSlots) break;
                    actions.Add(new DeckServiceAction(candidate.Id, DeckServiceActionKind.Recycle,
                        Math.Max(0.0, candidate.PermanentValue - candidate.RecycleValue)));
                    free++;
                }
            }
            var admitted = free >= requiredFreeSlots;
            return new DeckServicePlan(actions.ToArray(), maximumDeckSize - deckCount,
                requiredFreeSlots, admitted, admitted ? "Exact live-frame deck slack established."
                    : "No sufficient set of admitted casts/recycles can establish live-frame slack.");
        }

        internal static EndCardHandoffPlan EvaluateEndCardHandoff(bool hasTerminalPiece,
            bool hasRecoverableCopy, int heldEndCards, int[] mayoAmounts,
            OrdinaryInventoryTopology topology, EndCardFilterSnapshot filters)
        {
            ValidateSix(mayoAmounts, "mayoAmounts");
            if (heldEndCards < 0) throw new ArgumentOutOfRangeException("heldEndCards");
            if (topology == null) throw new ArgumentNullException("topology");
            if (filters == null) throw new ArgumentNullException("filters");
            var deficits = mayoAmounts.Select(x => Math.Max(0, EndMayoPerCurrency - x)).ToArray();
            var capacity = LootCapacity.ProveOrdinary(topology, LootCapacity.EndCardInventoryPiece());
            var stop = hasTerminalPiece || hasRecoverableCopy;
            var ready = !stop && heldEndCards > 0 && deficits.All(x => x == 0)
                        && capacity.Admitted && filters.StateKnown;
            var reason = stop ? "Physical item 492 already exists; duplicate END consume is forbidden."
                : heldEndCards == 0 ? "Awaiting a held END Card."
                : deficits.Any(x => x > 0) ? "Awaiting the exact 99x6 Mayo vector."
                : !capacity.Admitted ? capacity.Reason
                : !filters.StateKnown ? "Cannot prove exact filter snapshot/restoration."
                : "Ready for task-9 filter-safe terminal transaction.";
            return new EndCardHandoffPlan(hasTerminalPiece, hasRecoverableCopy, heldEndCards,
                mayoAmounts, deficits, capacity, filters, ready, stop, reason);
        }

        internal static EndCardRollForecast EndRollForecast(string sourceHash)
        {
            var evidence = new ForecastEvidence
            {
                Grade = ForecastEvidenceGrade.SourceExact,
                ProbabilitySource = "CardsController.generateCard: 1% END branch in Sadistic",
                CadenceSource = "normal Card rolls",
                SourceHash = sourceHash ?? string.Empty,
                OnlineOnly = false,
                Notes = "No RNG state inspection or steering; geometric independent-roll model."
            };
            return new EndCardRollForecast(MechanicsStochastic.GeometricMeanTrials(EndRollProbability),
                MechanicsStochastic.GeometricQuantileTrials(EndRollProbability, .5),
                MechanicsStochastic.GeometricQuantileTrials(EndRollProbability, .9),
                MechanicsStochastic.GeometricQuantileTrials(EndRollProbability, .95),
                MechanicsStochastic.GeometricQuantileTrials(EndRollProbability, .99), evidence);
        }

        internal static double CookingLocalScore(CookingIngredientModel ingredient, int level)
        {
            if (ingredient == null) throw new ArgumentNullException("ingredient");
            return Math.Pow(1.0 - .03 * Math.Abs(ingredient.TargetLevel - level), 30.0)
                   * ingredient.Weight;
        }

        internal static double CookingPairBonus(CookingIngredientModel first, int pairTarget,
            int firstLevel, int secondLevel)
        {
            if (first == null) throw new ArgumentNullException("first");
            return Math.Pow(1.0 - .02 * Math.Abs(pairTarget - firstLevel - secondLevel), 40.0)
                   * first.PairedWeight;
        }

        internal static double CookingPairScore(CookingIngredientModel first,
            CookingIngredientModel second, int pairTarget, int firstLevel, int secondLevel)
        {
            if (first == null || second == null) throw new ArgumentNullException();
            var score = 0.0;
            if (first.Unlocked)
                score += CookingLocalScore(first, firstLevel) + CookingLocalScore(second, firstLevel);
            if (second.Unlocked)
                score += CookingLocalScore(first, secondLevel) + CookingLocalScore(second, secondLevel);
            if (first.Unlocked && second.Unlocked)
                score += CookingPairBonus(first, pairTarget, firstLevel, secondLevel);
            return score;
        }

        internal static CookingPairPlan OptimizeCookingPair(CookingIngredientModel first,
            CookingIngredientModel second, int pairTarget, int maximumLevel)
        {
            if (first == null || second == null) throw new ArgumentNullException();
            if (maximumLevel < 0) throw new ArgumentOutOfRangeException("maximumLevel");
            var bestFirst = first.CurrentLevel;
            var bestSecond = second.CurrentLevel;
            var bestScore = double.NegativeInfinity;
            var firstMin = first.Unlocked ? 0 : first.CurrentLevel;
            var firstMax = first.Unlocked ? maximumLevel : first.CurrentLevel;
            var secondMin = second.Unlocked ? 0 : second.CurrentLevel;
            var secondMax = second.Unlocked ? maximumLevel : second.CurrentLevel;
            for (var a = firstMin; a <= firstMax; a++)
                for (var b = secondMin; b <= secondMax; b++)
                {
                    var score = CookingPairScore(first, second, pairTarget, a, b);
                    if (score > bestScore + 1e-12
                        || Math.Abs(score - bestScore) <= 1e-12
                           && (a < bestFirst || a == bestFirst && b < bestSecond))
                    {
                        bestScore = score;
                        bestFirst = a;
                        bestSecond = b;
                    }
                }
            return new CookingPairPlan(bestFirst, bestSecond, bestScore);
        }

        internal static int CookingAffixEffectiveCount(CookingEquipmentSlot slot, int affixCount)
        {
            if (affixCount < 0 || affixCount > 3) throw new ArgumentOutOfRangeException("affixCount");
            if (slot == CookingEquipmentSlot.Weapon2) return 0;
            return slot == CookingEquipmentSlot.Legs ? affixCount * 2 : affixCount;
        }

        internal static double CookingEquipmentMultiplier(int effectiveAffixCount)
        {
            if (effectiveAffixCount < 0) throw new ArgumentOutOfRangeException("effectiveAffixCount");
            return Math.Min(CookingEquipmentCap,
                Math.Pow(CookingAffixFactor, effectiveAffixCount));
        }

        internal static bool ShouldConsumeCookingMeal(double storedExpBonus)
        {
            if (double.IsNaN(storedExpBonus) || double.IsInfinity(storedExpBonus)) return false;
            return storedExpBonus < CookingAppliedCap;
        }

        internal static void ValidateSix(int[] values, string parameter)
        {
            if (values == null || values.Length != MayoCurrencyCount)
                throw new ArgumentException("Exactly six Mayo coordinates are required.", parameter);
            if (values.Any(x => x < 0)) throw new ArgumentOutOfRangeException(parameter);
        }

        private static bool LexicographicallyBefore(IList<int> left, IList<int> right)
        {
            var count = Math.Min(left.Count, right.Count);
            for (var i = 0; i < count; i++)
                if (left[i] != right[i]) return left[i] < right[i];
            return left.Count < right.Count;
        }
    }

    internal static class CardCookingManager
    {
        private const double CookingPerfectTolerance = 1e-6;
        private static readonly int EndCardItemId = MechanicsEndgame.AllRequirements()
            .First(x => x.DependencyKind == EndDependencyKind.EndCard).ItemId;
        private static string _lastGeneratorSignature = string.Empty;
        private static string _lastTagSignature = string.Empty;
        private static readonly Dictionary<string, string> LastStateMessages =
            new Dictionary<string, string>();

        internal static EndCardHandoffPlan LastEndHandoff { get; private set; }
        internal static EndCardRollForecast EndForecast
        {
            get { return CardCookingMechanics.EndRollForecast(Main.GameAssemblySha256); }
        }

        internal static void ManageCards(Character c, AutopilotConfig config, bool fullControl)
        {
            ManageCards(c, config, fullControl, null);
        }

        internal static void ManageCards(Character c, AutopilotConfig config, bool fullControl,
            RootTransaction root)
        {
            if (c == null || c.cards == null || !c.cards.cardsOn || c.cardsController == null)
                return;

            // END protection is the first Card-side mutation. It precedes tags, generators, deck
            // reclamation, and handoff publication so no full-deck frame can expose item 492.
            ProtectEveryEndImmediately(c);
            var terminal = EndgameDependencyModel.HasTerminalPiece(c, EndCardItemId);
            var recoverable = EndgameDependencyModel.HasRecoverableCopy(c, EndCardItemId);
            var heldMap = c.cards.cards.Select((card, id) => new {card, id})
                .ToDictionary(x => x.id, x => x.card);
            var balances = CaptureMayoAmounts(c);
            var reserve = CardCookingMechanics.EndMayoReserve(!terminal);
            var candidates = BuildPortfolioCandidates(c, heldMap);
            SetTags(c, candidates);
            SetMayoGenerators(c, candidates, balances, reserve);

            if (fullControl)
            {
                var endSecured = terminal || recoverable || heldMap.Values.Any(x => x.type == cardType.end);
                var chonkerDue = ChonkerDue(c);
                var required = CardCookingMechanics.RequiredLiveDeckSlack(
                    c.settings.rebirthDifficulty == difficulty.sadistic, endSecured, chonkerDue);
                var capacity = LootCapacity.ProveDeck(c.cards.cards.Count,
                    c.cardsController.maxDeckSize(), CardDeckRequirement.LiveFrame(true, chonkerDue,
                        c.settings.rebirthDifficulty == difficulty.sadistic && !endSecured));
                if (!capacity.Admitted)
                    ServiceDeck(c, config, candidates, heldMap, required, recoverable, reserve, root);

                // Rebuild after service because native deletes shift exact indices. Object identity
                // remains stable inside heldMap, and the intent resolves the current index just-in-time.
                balances = CaptureMayoAmounts(c);
                candidates = BuildPortfolioCandidates(c, heldMap);
                CastOnePortfolioCard(c, candidates, heldMap, balances, reserve, root);
            }

            var heldEnds = c.cards.cards.Count(x => x.type == cardType.end);
            LastEndHandoff = BuildEndHandoff(c, terminal, recoverable, heldEnds);
            LogStateChange("cards-end-handoff", LastEndHandoff.Reason);
        }

        private static void ProtectEveryEndImmediately(Character c)
        {
            for (var i = 0; i < c.cards.cards.Count; i++)
                if (c.cards.cards[i].type == cardType.end && !c.cards.cards[i].isProtected)
                    c.cardsController.protectCard(i);
        }

        private static void SetTags(Character c, CardPortfolioCandidate[] candidates)
        {
            var tagCount = Math.Max(0, c.cardsController.maxTagSize());
            var desired = c.cards.cards.Where(x => x.type != cardType.end)
                .GroupBy(x => x.bonusType)
                .Select(g => new {Type = g.Key, Value = g.Sum(x => RouteCardValue(c, x))})
                .OrderByDescending(x => x.Value).ThenBy(x => (int)x.Type)
                .Take(tagCount).Select(x => x.Type).ToList();
            // A quiet/empty deck still gets stable route-positive tags. No tier gate is applied.
            var fallbacks = new[] {cardBonus.adventureStat, cardBonus.atkDefStats, cardBonus.wishSpeed,
                cardBonus.hackSpeed, cardBonus.PP, cardBonus.QP, cardBonus.energyNGUSpeed,
                cardBonus.magicNGUSpeed, cardBonus.dropChance};
            foreach (var type in fallbacks)
                if (desired.Count < tagCount && !desired.Contains(type)) desired.Add(type);
            if (c.cards.taggedBonuses.SequenceEqual(desired)) return;
            c.cards.taggedBonuses.Clear();
            c.cards.taggedBonuses.AddRange(desired);
            c.cardsController.updateMenu();
            var signature = string.Join(",", desired.Select(x => x.ToString()).ToArray());
            if (_lastTagSignature != signature)
            {
                _lastTagSignature = signature;
                Main.Log("Autopilot cards: route-value tags=" + signature + "; RNG-aware steering=off");
            }
        }

        private static void SetMayoGenerators(Character c, CardPortfolioCandidate[] candidates,
            int[] balances, int[] reserve)
        {
            if (c.cards.manas == null || c.cards.manas.Count < CardCookingMechanics.MayoCurrencyCount)
                return;
            var shadows = CardCookingMechanics.DiscreteMayoShadowValues(candidates, balances, reserve, 1024);
            var progress = c.cards.manas.Take(CardCookingMechanics.MayoCurrencyCount)
                .Select(x => Math.Max(0.0, Math.Min(.999999999, x.progress))).ToArray();
            var plan = CardCookingMechanics.ChooseMayoGenerators(c.cardsController.totalMayoSpeed(),
                c.cardsController.maxManaGenSize(), shadows, progress);
            var selected = new HashSet<int>(plan.CurrencyIds);
            var changed = false;
            for (var i = 0; i < c.cards.manas.Count; i++)
            {
                var shouldRun = selected.Contains(i);
                if (c.cards.manas[i].running == shouldRun) continue;
                c.cards.manas[i].running = shouldRun;
                changed = true;
            }
            if (changed) c.cardsController.updateMenu();
            var signature = string.Join(",", plan.CurrencyIds.Select(x => x.ToString()).ToArray());
            if (_lastGeneratorSignature != signature)
            {
                _lastGeneratorSignature = signature;
                Main.Log("Autopilot cards: six-Mayo generators=" + signature
                         + ", exact-live-rate=" + plan.AggregateRatePerSecond.ToString("0.######") + "/s");
            }
        }

        private static void ServiceDeck(Character c, AutopilotConfig config,
            CardPortfolioCandidate[] portfolio, IDictionary<int, Card> heldMap,
            int requiredFreeSlots, bool redundantEndAllowed, int[] reserve, RootTransaction root)
        {
            var admittedCasts = new HashSet<int>(CardCookingMechanics.SolveCardPortfolio(portfolio,
                CaptureMayoAmounts(c), reserve, 4096).SelectedIds);
            var service = portfolio.Select(x => new DeckServiceCandidate(x.Id, x.Value,
                RecycleValue(c, heldMap[x.Id]), admittedCasts.Contains(x.Id),
                heldMap[x.Id].isProtected, x.Kind)).ToArray();
            var plan = CardCookingMechanics.PlanDeckService(service, c.cards.cards.Count,
                c.cardsController.maxDeckSize(), requiredFreeSlots,
                config != null && config.AllowCardYeeting, redundantEndAllowed);
            foreach (var action in plan.Actions)
            {
                Card card;
                if (!heldMap.TryGetValue(action.CandidateId, out card)) continue;
                var index = FindCardByIdentity(c, card);
                if (index < 0) continue;
                if (action.Kind == DeckServiceActionKind.Cast)
                    ExecuteCardConsume(c, card, reserve, root, "deck-slack");
                else
                    ExecuteCardRecycle(c, card, redundantEndAllowed, root);
            }
            if (!plan.Admitted)
                LogStateChange("cards-deck-hold", plan.Reason);
        }

        private static void LogStateChange(string key, string message)
        {
            string previous;
            if (LastStateMessages.TryGetValue(key, out previous) && previous == message) return;
            LastStateMessages[key] = message;
            Main.Log(message);
        }

        private static void CastOnePortfolioCard(Character c, CardPortfolioCandidate[] candidates,
            IDictionary<int, Card> heldMap, int[] balances, int[] reserve, RootTransaction root)
        {
            var plan = CardCookingMechanics.SolveCardPortfolio(candidates, balances, reserve, 4096);
            foreach (var id in plan.SelectedIds.OrderByDescending(x => candidates.First(y => y.Id == x).Value))
            {
                Card card;
                if (!heldMap.TryGetValue(id, out card) || FindCardByIdentity(c, card) < 0
                    || card.type == cardType.end || card.isProtected || !Affordable(c, card)) continue;
                ExecuteCardConsume(c, card, reserve, root, "portfolio");
                return; // Replan after each settled finite-resource debit.
            }
        }

        private static CardPortfolioCandidate[] BuildPortfolioCandidates(Character c,
            IDictionary<int, Card> heldMap)
        {
            return heldMap.Where(x => FindCardByIdentity(c, x.Value) >= 0).Select(x =>
            {
                var card = x.Value;
                var costs = new int[CardCookingMechanics.MayoCurrencyCount];
                for (var i = 0; i < costs.Length && i < card.manaCosts.Count; i++)
                    costs[i] = Math.Max(0, card.manaCosts[i]);
                return new CardPortfolioCandidate(x.Key, RouteCardValue(c, card), costs,
                    CardKind(card), card.isProtected);
            }).ToArray();
        }

        private static double RouteCardValue(Character c, Card card)
        {
            if (card == null || card.type == cardType.end || card.effectAmount <= 0.0) return 0.0;
            var type = Math.Max(0, (int)card.bonusType);
            var current = 1.0;
            if (c.cards.bonuses != null && type < c.cards.bonuses.Count)
                current = Math.Max(1e-12, c.cards.bonuses[type]);
            var weight = .25;
            if (card.bonusType == cardBonus.adventureStat) weight = 8.0;
            else if (card.bonusType == cardBonus.atkDefStats)
                weight = EndgameDependencyModel.IsTerminalCombatCritical(c) ? 100.0 : 6.0;
            else if (card.bonusType == cardBonus.wishSpeed || card.bonusType == cardBonus.hackSpeed)
                weight = 5.0;
            else if (card.bonusType == cardBonus.PP || card.bonusType == cardBonus.QP) weight = 3.0;
            else if (card.bonusType == cardBonus.energyNGUSpeed
                     || card.bonusType == cardBonus.magicNGUSpeed) weight = 2.0;
            else if (card.bonusType == cardBonus.dropChance) weight = 1.5;
            return weight * Math.Log((current + card.effectAmount) / current);
        }

        private static double RecycleValue(Character c, Card card)
        {
            var value = .01; // Slot option value; exact timer/progress refunds only improve this floor.
            if (card.cardRarity == rarity.BigChonker && HasChonkerRecycling(c)) value += .25;
            if (HasMayoRecycling(c) && card.manaCosts.Any(x => x > 0)) value += .20;
            return value;
        }

        private static HeldCardKind CardKind(Card card)
        {
            if (card.type == cardType.end) return HeldCardKind.End;
            if (card.cardRarity == rarity.BigChonker) return HeldCardKind.BigChonker;
            // Native foil is mechanically ordinary; the kind only preserves deck telemetry.
            return card.type == cardType.foil ? HeldCardKind.Foil : HeldCardKind.Normal;
        }

        private static bool Affordable(Character c, Card card)
        {
            if (card == null || card.manaCosts.Count > c.cards.manas.Count) return false;
            for (var i = 0; i < card.manaCosts.Count; i++)
                if (card.manaCosts[i] > c.cards.manas[i].amount) return false;
            return true;
        }

        private static int FindCardByIdentity(Character c, Card card)
        {
            for (var i = 0; i < c.cards.cards.Count; i++)
                if (ReferenceEquals(c.cards.cards[i], card)) return i;
            return -1;
        }

        private static void ExecuteCardConsume(Character c, Card card, int[] reserve,
            RootTransaction root, string purpose)
        {
            var intent = new OrdinaryCardConsumeIntent(c, card, reserve, purpose);
            if (root != null)
            {
                var result = root.ExecuteChild(intent, c.cardsController);
                var committed = result.Kind == MutationResultKind.Committed
                                || result.Kind == MutationResultKind.CommittedWithException;
                Main.LogAction(committed ? "CARD" : "REJECTED",
                    purpose + " Card cast: " + result.Kind + " - " + result.Reason);
                return;
            }
            var before = intent.CaptureDirect();
            var precondition = intent.CheckPreconditions(null, before);
            if (precondition.Kind != MutationPreconditionKind.Ready)
            {
                Main.LogAction("REJECTED", "Card cast held: " + precondition.Reason);
                return;
            }
            var invocation = intent.ApplyDirect();
            var verified = intent.VerifyDirect(before, invocation);
            Main.LogAction(verified ? "CARD" : "REJECTED", verified
                ? "Cast " + card.cardName + " [exact deck identity and six-Mayo debit]"
                : "Card cast failed exact deck/Mayo postcondition; adapter=" + invocation.Status);
        }

        private static void ExecuteCardRecycle(Character c, Card card, bool redundantEndAllowed,
            RootTransaction root)
        {
            var intent = new CardRecycleIntent(c, card, redundantEndAllowed);
            if (root != null)
            {
                var result = root.ExecuteChild(intent, c.cardsController);
                var committed = result.Kind == MutationResultKind.Committed
                                || result.Kind == MutationResultKind.CommittedWithException;
                Main.LogAction(committed ? "CARD" : "REJECTED",
                    "Card recycle: " + result.Kind + " - " + result.Reason);
                return;
            }
            var count = c.cards.cards.Count;
            var index = FindCardByIdentity(c, card);
            if (index < 0 || card.isProtected
                && !(card.type == cardType.end && redundantEndAllowed)) return;
            var wasProtected = card.isProtected;
            if (wasProtected) c.cardsController.protectCard(index);
            index = FindCardByIdentity(c, card);
            if (index < 0) return;
            c.cardsController.trashCard(index);
            var removed = c.cards.cards.Count == count - 1 && FindCardByIdentity(c, card) < 0;
            if (!removed && wasProtected && !card.isProtected)
            {
                index = FindCardByIdentity(c, card);
                if (index >= 0) c.cardsController.protectCard(index);
            }
            Main.LogAction(removed ? "CARD" : "REJECTED",
                "Recycled " + card.cardName + " for proactive deck slack");
        }

        private static EndCardHandoffPlan BuildEndHandoff(Character c, bool terminal,
            bool recoverable, int heldEnds)
        {
            var topology = CaptureOrdinaryTopology(c);
            var filters = CaptureEndFilters(c);
            return CardCookingMechanics.EvaluateEndCardHandoff(terminal, recoverable, heldEnds,
                CaptureMayoAmounts(c), topology, filters);
        }

        private static OrdinaryInventoryTopology CaptureOrdinaryTopology(Character c)
        {
            var items = c.inventory.inventory;
            var ids = items.Select(x => x == null ? 0 : x.id).ToArray();
            var identities = items.Select(x => x == null || x.id == 0 ? null : (object)x).ToArray();
            return PhysicalTopology.CaptureOrdinary(ids, identities,
                c.inventoryController.curSpaces(), c.inventoryController.totalInvMergeSlots());
        }

        private static EndCardFilterSnapshot CaptureEndFilters(Character c)
        {
            try
            {
                var itemFiltered = c.inventory.itemList.itemFiltered[EndCardItemId];
                return new EndCardFilterSnapshot(true, itemFiltered, c.arbitrary.lootFilter,
                    c.settings.filterOn, c.settings.filterMisc);
            }
            catch
            {
                return new EndCardFilterSnapshot(false, false, false, false, false);
            }
        }

        private static int[] CaptureMayoAmounts(Character c)
        {
            var result = new int[CardCookingMechanics.MayoCurrencyCount];
            for (var i = 0; i < result.Length && i < c.cards.manas.Count; i++)
                result[i] = Math.Max(0, c.cards.manas[i].amount);
            return result;
        }

        private static bool ChonkerDue(Character c)
        {
            return c.cardsController.unlockedChonkers()
                   && c.cards.chonkerSpawnTimer.totalseconds >= c.cardsController.chonkerSpawnTime();
        }

        private static bool HasChonkerRecycling(Character c)
        {
            return c.adventure.itopod.perkLevel.Count > 216
                   && c.adventure.itopod.perkLevel[216] >= 1;
        }

        private static bool HasMayoRecycling(Character c)
        {
            return c.beastQuest.quirkLevel.Count > 156 && c.beastQuest.quirkLevel[156] >= 1;
        }

        internal static void ManageCooking(Character c, bool fullControl)
        {
            ManageCooking(c, fullControl, null);
        }

        internal static void ManageCooking(Character c, bool fullControl, RootTransaction root)
        {
            if (c == null || c.cooking == null || !c.cooking.unlocked || c.cookingController == null)
                return;
            if (!CardCookingMechanics.ShouldConsumeCookingMeal(c.cooking.expBonus)) return;
            if (!OptimizeCooking(c)) return;
            while (fullControl && CardCookingMechanics.ShouldConsumeCookingMeal(c.cooking.expBonus)
                   && c.cooking.cookTimer >= c.cookingController.eatRate())
            {
                var intent = new CookingConsumeIntent(c);
                bool committed;
                if (root != null)
                {
                    var result = root.ExecuteChild(intent, c.cookingController);
                    committed = result.Kind == MutationResultKind.Committed
                                || result.Kind == MutationResultKind.CommittedWithException;
                    Main.LogAction(committed ? "COOKING" : "REJECTED",
                        "Cooking meal: " + result.Kind + " - " + result.Reason);
                }
                else
                {
                    var beforeTimer = c.cooking.cookTimer;
                    var beforeBonus = c.cooking.expBonus;
                    c.cookingController.consumeDish();
                    committed = c.cooking.cookTimer < beforeTimer && c.cooking.expBonus > beforeBonus;
                    Main.LogAction(committed ? "COOKING" : "REJECTED", committed
                        ? "Consumed pure-pair optimized dish [timer debit and bonus credit]"
                        : "Cooking consume lacked exact timer debit plus bonus credit");
                }
                if (!committed || !CardCookingMechanics.ShouldConsumeCookingMeal(c.cooking.expBonus)) break;
                if (!OptimizeCooking(c)) break; // Native randomized the next dish; solve it anew.
            }
        }

        /*
        PURE COOKING APPLICATION

        Each native pair is disjoint. Read immutable ingredient models and all four pair targets,
        solve at most 4*441 candidates, then apply every chosen level once. If native percent is not
        exactly optimal within float tolerance, restore all original levels before returning false.
        */
        private static bool OptimizeCooking(Character c)
        {
            var ingredients = c.cooking.ingredients;
            var pairs = new[] {c.cooking.pair1, c.cooking.pair2, c.cooking.pair3, c.cooking.pair4};
            var targets = new[] {c.cooking.pair1Target, c.cooking.pair2Target,
                c.cooking.pair3Target, c.cooking.pair4Target};
            var original = ingredients.Select(x => x.curLevel).ToArray();
            var desired = (int[])original.Clone();
            var maximum = c.cookingController.maxIngredientLevel();
            for (var i = 0; i < pairs.Length; i++)
            {
                var pair = pairs[i];
                if (pair == null || pair.Count != 2 || pair[0] < 0 || pair[1] < 0
                    || pair[0] >= ingredients.Count || pair[1] >= ingredients.Count) return false;
                var first = IngredientModel(ingredients[pair[0]]);
                var second = IngredientModel(ingredients[pair[1]]);
                var plan = CardCookingMechanics.OptimizeCookingPair(first, second, targets[i], maximum);
                desired[pair[0]] = plan.FirstLevel;
                desired[pair[1]] = plan.SecondLevel;
            }
            for (var i = 0; i < ingredients.Count; i++) ingredients[i].curLevel = desired[i];
            var percent = c.cookingController.getCurPercentofMaxScore();
            if (percent + CookingPerfectTolerance >= 1.0)
            {
                c.cookingController.updateMenu();
                return true;
            }
            for (var i = 0; i < ingredients.Count; i++) ingredients[i].curLevel = original[i];
            c.cookingController.updateMenu();
            Main.LogAction("REJECTED", "Pure Cooking pair plan failed native 100% score verification; levels restored");
            return false;
        }

        private static CookingIngredientModel IngredientModel(Ingredient ingredient)
        {
            return new CookingIngredientModel(ingredient.curLevel, ingredient.targetLevel,
                ingredient.weight, ingredient.pairedWeight, ingredient.unlocked);
        }

        internal static int CountEquippedCookingAffixes(Character c)
        {
            if (c == null || c.inventory == null) return 0;
            var count = CountAffixes(c.inventory.head)
                        + CountAffixes(c.inventory.chest)
                        + 2 * CountAffixes(c.inventory.legs)
                        + CountAffixes(c.inventory.boots)
                        + CountAffixes(c.inventory.weapon);
            if (c.inventory.accs != null)
                count += c.inventory.accs.Sum(CountAffixes);
            // Source quirk: weapon 2 is intentionally not checked by invCookCheck.
            return count;
        }

        private static int CountAffixes(Equipment equipment)
        {
            if (equipment == null) return 0;
            var count = 0;
            if (equipment.spec1Type == specType.Cooking) count++;
            if (equipment.spec2Type == specType.Cooking) count++;
            if (equipment.spec3Type == specType.Cooking) count++;
            return count;
        }

        private sealed class CardState
        {
            internal Card Card;
            internal int DeckCount;
            internal int[] Mayo;
            internal bool Protected;
        }

        private sealed class OrdinaryCardConsumeIntent : IMutationIntent<CardState,
            NativeInvocationResult, CardState>
        {
            private readonly Character _character;
            private readonly Card _card;
            private readonly int[] _reserve;
            private readonly string _purpose;

            internal OrdinaryCardConsumeIntent(Character character, Card card, int[] reserve,
                string purpose)
            {
                _character = character;
                _card = card;
                _reserve = (int[])reserve.Clone();
                _purpose = purpose ?? "ordinary";
            }

            public string Id { get { return "cards.consume." + _purpose; } }
            public MutationClass Class { get { return MutationClass.Cards; } }
            public MutationRisk Risk { get { return MutationRisk.FiniteResource; } }
            public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
            public string BindingId { get { return NativeBindingKeys.CardConsume; } }
            public bool Required { get { return true; } }
            public bool CanCompensate { get { return false; } }
            public bool CreatesNewEpoch { get { return false; } }
            public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

            public CardState CaptureBefore(MutationContext context) { return CaptureDirect(); }
            internal CardState CaptureDirect()
            {
                return new CardState {Card = _card, DeckCount = _character.cards.cards.Count,
                    Mayo = CaptureMayoAmounts(_character), Protected = _card != null && _card.isProtected};
            }

            public PreconditionResult CheckPreconditions(MutationContext context, CardState before)
            {
                if (_card == null || _card.type == cardType.end)
                    return PreconditionResult.Hold("Ordinary intent cannot consume END.");
                if (_card.isProtected || FindCardByIdentity(_character, _card) < 0)
                    return PreconditionResult.Hold("Exact Card identity is absent or protected.");
                var costs = new int[CardCookingMechanics.MayoCurrencyCount];
                for (var i = 0; i < costs.Length && i < _card.manaCosts.Count; i++)
                    costs[i] = _card.manaCosts[i];
                return CardCookingMechanics.CanSpendWithoutBreakingReserve(
                        CaptureMayoAmounts(_character), costs, _reserve)
                    ? PreconditionResult.Ready()
                    : PreconditionResult.Hold("Exact six-Mayo debit would break END reserve.");
            }

            public NativeInvocationResult Apply(MutationContext context, RootTransactionToken token,
                CardState before) { return ApplyDirect(); }
            internal NativeInvocationResult ApplyDirect()
            {
                var index = FindCardByIdentity(_character, _card);
                if (index < 0) return new NativeInvocationResult(NativeInvocationStatus.TargetMismatch,
                    NativeBindingKeys.CardConsume, "Card identity moved out of held deck.", null, null);
                try
                {
                    return NativeBindingRegistry.Create(typeof(Card).Assembly, Main.GameAssemblySha256)
                        .CreateMutationAdapters().ConsumeCard(_character.cardsController, index);
                }
                catch (Exception ex)
                {
                    return new NativeInvocationResult(NativeInvocationStatus.BindingUnavailable,
                        NativeBindingKeys.CardConsume, ex.Message, null, ex);
                }
            }

            public VerificationResult<CardState> Verify(MutationContext context, CardState before,
                MutationApplyObservation<NativeInvocationResult> apply)
            {
                var invocation = apply.ReturnedNormally ? apply.Value : null;
                CardState after;
                return VerifyCore(before, invocation, out after)
                    ? VerificationResult<CardState>.Satisfied(after, "Exact identity deletion and six-Mayo debit.")
                    : VerificationResult<CardState>.Failed("Card postcondition mismatch.");
            }

            internal bool VerifyDirect(CardState before, NativeInvocationResult invocation)
            {
                CardState after;
                return VerifyCore(before, invocation, out after);
            }

            private bool VerifyCore(CardState before, NativeInvocationResult invocation, out CardState after)
            {
                after = CaptureDirect();
                if (invocation == null || !invocation.InvocationAttempted
                    || after.DeckCount != before.DeckCount - 1
                    || FindCardByIdentity(_character, _card) >= 0) return false;
                for (var i = 0; i < CardCookingMechanics.MayoCurrencyCount; i++)
                {
                    var cost = i < _card.manaCosts.Count ? _card.manaCosts[i] : 0;
                    if (after.Mayo[i] != before.Mayo[i] - cost) return false;
                }
                return true;
            }

            public CompensationResult Compensate(MutationContext context, RecoveryToken token,
                CardState before, MutationApplyObservation<NativeInvocationResult> apply)
            { return CompensationResult.NotSupported("Permanent Card consume has no safe inverse."); }
            public bool BeforeStateMatches(CardState expected, CardState observed)
            {
                return expected.DeckCount == observed.DeckCount && ReferenceEquals(expected.Card, observed.Card)
                       && expected.Protected == observed.Protected && expected.Mayo.SequenceEqual(observed.Mayo);
            }
            public string FingerprintBefore(CardState before) { return Fingerprint(before); }
            public string FingerprintAfter(CardState after) { return Fingerprint(after); }
            private static string Fingerprint(CardState state)
            { return state.DeckCount + ":" + string.Join(",", state.Mayo.Select(x => x.ToString()).ToArray()); }
        }

        private sealed class CardRecycleIntent : IMutationIntent<CardState, bool, CardState>
        {
            private readonly Character _character;
            private readonly Card _card;
            private readonly bool _redundantEndAllowed;
            internal CardRecycleIntent(Character character, Card card, bool redundantEndAllowed)
            { _character = character; _card = card; _redundantEndAllowed = redundantEndAllowed; }
            public string Id { get { return "cards.recycle.deck-slack"; } }
            public MutationClass Class { get { return MutationClass.Cards; } }
            public MutationRisk Risk { get { return MutationRisk.FiniteResource; } }
            public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
            public string BindingId { get { return "CardsController.trashCard(int)"; } }
            public bool Required { get { return true; } }
            public bool CanCompensate { get { return true; } }
            public bool CreatesNewEpoch { get { return false; } }
            public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }
            public CardState CaptureBefore(MutationContext context)
            { return new CardState {Card = _card, DeckCount = _character.cards.cards.Count,
                Mayo = CaptureMayoAmounts(_character), Protected = _card != null && _card.isProtected}; }
            public PreconditionResult CheckPreconditions(MutationContext context, CardState before)
            {
                return _card != null && (!_card.isProtected || _card.type == cardType.end
                                                            && _redundantEndAllowed)
                                     && FindCardByIdentity(_character, _card) >= 0
                    ? PreconditionResult.Ready() : PreconditionResult.Hold("Card identity absent/protected.");
            }
            public bool Apply(MutationContext context, RootTransactionToken token, CardState before)
            {
                var index = FindCardByIdentity(_character, _card);
                if (index < 0) return false;
                if (_card.isProtected) _character.cardsController.protectCard(index);
                index = FindCardByIdentity(_character, _card);
                if (index < 0) return false;
                _character.cardsController.trashCard(index);
                return true;
            }
            public VerificationResult<CardState> Verify(MutationContext context, CardState before,
                MutationApplyObservation<bool> apply)
            {
                var after = CaptureBefore(context);
                return after.DeckCount == before.DeckCount - 1 && FindCardByIdentity(_character, _card) < 0
                    ? VerificationResult<CardState>.Satisfied(after, "Exact Card identity removed.")
                    : VerificationResult<CardState>.Failed("Recycle did not remove exactly one intended Card.");
            }
            public CompensationResult Compensate(MutationContext context, RecoveryToken token,
                CardState before, MutationApplyObservation<bool> apply)
            {
                if (FindCardByIdentity(_character, _card) < 0)
                    return CompensationResult.Failed("Removed Card cannot be reconstructed.");
                if (before.Protected && !_card.isProtected)
                {
                    var index = FindCardByIdentity(_character, _card);
                    _character.cardsController.protectCard(index);
                }
                return _card.isProtected == before.Protected
                    ? CompensationResult.Restored("Original Card protection restored.")
                    : CompensationResult.Failed("Original Card protection was not restored.");
            }
            public bool BeforeStateMatches(CardState expected, CardState observed)
            { return expected.DeckCount == observed.DeckCount && FindCardByIdentity(_character, _card) >= 0
                     && expected.Protected == _card.isProtected; }
            public string FingerprintBefore(CardState before) { return before.DeckCount.ToString(); }
            public string FingerprintAfter(CardState after) { return after.DeckCount.ToString(); }
        }

        private sealed class CookingState
        {
            internal float Timer;
            internal float Bonus;
        }

        private sealed class CookingConsumeIntent : IMutationIntent<CookingState, bool, CookingState>
        {
            private readonly Character _character;
            internal CookingConsumeIntent(Character character) { _character = character; }
            public string Id { get { return "cooking.consume.optimized-meal"; } }
            public MutationClass Class { get { return MutationClass.Cooking; } }
            public MutationRisk Risk { get { return MutationRisk.FiniteResource; } }
            public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
            public string BindingId { get { return "CookingController.consumeDish()"; } }
            public bool Required { get { return true; } }
            public bool CanCompensate { get { return false; } }
            public bool CreatesNewEpoch { get { return false; } }
            public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }
            public CookingState CaptureBefore(MutationContext context)
            { return new CookingState {Timer = _character.cooking.cookTimer,
                Bonus = _character.cooking.expBonus}; }
            public PreconditionResult CheckPreconditions(MutationContext context, CookingState before)
            {
                if (!CardCookingMechanics.ShouldConsumeCookingMeal(before.Bonus))
                    return PreconditionResult.AlreadySatisfied("Applied Cooking multiplier is capped at 3.");
                return before.Timer >= _character.cookingController.eatRate()
                    ? PreconditionResult.Ready() : PreconditionResult.Hold("No full meal banked.");
            }
            public bool Apply(MutationContext context, RootTransactionToken token, CookingState before)
            { _character.cookingController.consumeDish(); return true; }
            public VerificationResult<CookingState> Verify(MutationContext context, CookingState before,
                MutationApplyObservation<bool> apply)
            {
                var after = CaptureBefore(context);
                return after.Timer < before.Timer && after.Bonus > before.Bonus
                    ? VerificationResult<CookingState>.Satisfied(after, "Timer debit and Cooking bonus credit.")
                    : VerificationResult<CookingState>.Failed("Meal postcondition mismatch.");
            }
            public CompensationResult Compensate(MutationContext context, RecoveryToken token,
                CookingState before, MutationApplyObservation<bool> apply)
            { return CompensationResult.NotSupported("Consumed Cooking meal has no safe inverse."); }
            public bool BeforeStateMatches(CookingState expected, CookingState observed)
            { return expected.Timer == observed.Timer && expected.Bonus == observed.Bonus; }
            public string FingerprintBefore(CookingState before) { return before.Timer + ":" + before.Bonus; }
            public string FingerprintAfter(CookingState after) { return after.Timer + ":" + after.Bonus; }
        }
    }
}
