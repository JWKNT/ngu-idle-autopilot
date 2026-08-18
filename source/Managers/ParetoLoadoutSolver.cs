/*
FILE PURPOSE

Purpose: Pure canonical branch-and-bound for exact-reference equipment selection.  The solver is
independent of Unity and Assembly-CSharp so its completeness, accessory de-duplication, and bound
semantics can be tested without a game process.

Mechanism: A problem supplies immutable per-slot candidates, one immutable objective, a complete
cost evaluator, and an admissible optimistic lower-bound evaluator.  Fixed slots and the ordered
primary/secondary weapon slots are searched independently.  Accessories are selected only in
strict canonical-key order, so a physical set is visited once rather than once per permutation.
Best-bound-first expansion retains a valid frontier lower bound when a deterministic node budget
interrupts the search.  Pareto dominance is applied only to nodes with the same future-conflict
signature and to same-ID candidate copies with no reference obligation.

Inputs and outputs: Inputs contain exact physical reference keys, item IDs, nonnegative monotone
metric vectors, setup seconds, tag masks, and slot arrays.  Results contain the incumbent exact
selection/evaluation, optimistic lower bound, absolute/relative gap, proof flag, and search counts.

Invariants: A nonzero item ID and a physical reference may appear at most once.  Empty ID-zero
objects remain distinct by reference.  Higher metric values and more tags must never hurt the
caller's objective; lower setup seconds must never hurt it.  The caller's bound must be no greater
than every completion below the supplied optimistic vector.  IsProvenOptimal is true only after
the frontier is exhausted or every frontier bound is dominated by the incumbent.
*/
using System;
using System.Collections.Generic;
using System.Linq;

namespace NGUInjector.Managers
{
    internal enum LoadoutObjectiveKind
    {
        FightBoss,
        MajorUnlock,
        TitanAutokill,
        Itopod,
        ResourceRefill,
        AdventureProgression,
        ContinuousAdventure
    }

    internal enum LoadoutSlotKind
    {
        Head,
        Chest,
        Legs,
        Boots,
        PrimaryWeapon,
        SecondaryWeapon,
        Accessory
    }

    internal sealed class OptimizationObjective
    {
        internal readonly string Id;
        internal readonly long Epoch;
        internal readonly LoadoutObjectiveKind Kind;
        internal readonly string DisplayName;
        internal readonly int TargetZone;
        internal readonly int TargetEnemy;
        internal readonly int TitanIndex;
        internal readonly int TitanVersion;
        internal readonly bool BossOnly;
        internal readonly bool ValuesLoot;
        internal readonly string IntendedCombatMode;
        internal readonly double LiveFightBossHp;
        internal readonly double LiveAdventureHp;
        internal readonly double DropChance;
        internal readonly string OutcomeModel;
        internal readonly string HardGateModel;
        internal readonly string TerminalValueModel;

        internal OptimizationObjective(string id, long epoch, LoadoutObjectiveKind kind,
            string displayName, int targetZone, int targetEnemy, int titanIndex, int titanVersion,
            bool bossOnly, bool valuesLoot, string intendedCombatMode,
            double liveFightBossHp, double liveAdventureHp, double dropChance)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Objective ID is required.", "id");
            if (epoch < 0L) throw new ArgumentOutOfRangeException("epoch");
            ValidateFiniteNonNegative(liveFightBossHp, "liveFightBossHp");
            ValidateFiniteNonNegative(liveAdventureHp, "liveAdventureHp");
            if (double.IsNaN(dropChance) || double.IsInfinity(dropChance)
                || dropChance < 0.0 || dropChance > 1.0)
                throw new ArgumentOutOfRangeException("dropChance");
            Id = id;
            Epoch = epoch;
            Kind = kind;
            DisplayName = displayName ?? id;
            TargetZone = targetZone;
            TargetEnemy = targetEnemy;
            TitanIndex = titanIndex;
            TitanVersion = titanVersion;
            BossOnly = bossOnly;
            ValuesLoot = valuesLoot;
            IntendedCombatMode = intendedCombatMode ?? string.Empty;
            LiveFightBossHp = liveFightBossHp;
            LiveAdventureHp = liveAdventureHp;
            DropChance = dropChance;
            OutcomeModel = kind + " setup+recovery+action seconds";
            HardGateModel = "exact-reference uniqueness, physical capacity, target combat feasibility";
            TerminalValueModel = valuesLoot ? "candidate native loot progress" : "fixed target completion";
        }

        private static void ValidateFiniteNonNegative(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                throw new ArgumentOutOfRangeException(name);
        }
    }

    internal sealed class LoadoutCandidate
    {
        private readonly double[] _metrics;
        internal readonly long ReferenceKey;
        internal readonly long CanonicalKey;
        internal readonly int ItemId;
        internal readonly LoadoutSlotKind Slot;
        internal readonly double SetupSeconds;
        internal readonly long Tags;
        internal readonly bool HasReferenceObligation;
        internal readonly object Token;

        internal LoadoutCandidate(long referenceKey, long canonicalKey, int itemId,
            LoadoutSlotKind slot, double[] metrics, double setupSeconds, long tags,
            bool hasReferenceObligation, object token)
        {
            if (referenceKey <= 0L) throw new ArgumentOutOfRangeException("referenceKey");
            if (canonicalKey <= 0L) throw new ArgumentOutOfRangeException("canonicalKey");
            if (itemId < 0) throw new ArgumentOutOfRangeException("itemId");
            if (metrics == null) throw new ArgumentNullException("metrics");
            if (double.IsNaN(setupSeconds) || double.IsInfinity(setupSeconds) || setupSeconds < 0.0)
                throw new ArgumentOutOfRangeException("setupSeconds");
            _metrics = (double[])metrics.Clone();
            for (var i = 0; i < _metrics.Length; i++)
                if (double.IsNaN(_metrics[i]) || double.IsInfinity(_metrics[i]) || _metrics[i] < 0.0)
                    throw new ArgumentOutOfRangeException("metrics");
            ReferenceKey = referenceKey;
            CanonicalKey = canonicalKey;
            ItemId = itemId;
            Slot = slot;
            SetupSeconds = setupSeconds;
            Tags = tags;
            HasReferenceObligation = hasReferenceObligation;
            Token = token;
        }

        internal int MetricCount { get { return _metrics.Length; } }
        internal double Metric(int index) { return _metrics[index]; }
        internal double[] Metrics() { return (double[])_metrics.Clone(); }
    }

    internal sealed class LoadoutTotals
    {
        private readonly double[] _metrics;
        internal readonly double SetupSeconds;
        internal readonly int SwitchCount;
        internal readonly long Tags;

        internal LoadoutTotals(double[] metrics, double setupSeconds, int switchCount, long tags)
        {
            _metrics = (double[])metrics.Clone();
            SetupSeconds = setupSeconds;
            SwitchCount = switchCount;
            Tags = tags;
        }

        internal int MetricCount { get { return _metrics.Length; } }
        internal double Metric(int index) { return _metrics[index]; }
        internal double[] Metrics() { return (double[])_metrics.Clone(); }

        internal LoadoutTotals Add(LoadoutCandidate candidate)
        {
            var metrics = (double[])_metrics.Clone();
            for (var i = 0; i < metrics.Length; i++) metrics[i] += candidate.Metric(i);
            return new LoadoutTotals(metrics, SetupSeconds + candidate.SetupSeconds,
                SwitchCount + (candidate.SetupSeconds > 0.0 ? 1 : 0), Tags | candidate.Tags);
        }
    }

    internal sealed class LoadoutHealthProjection
    {
        internal readonly double CandidateMaxHp;
        internal readonly double CurrentHpAfterSwap;
        internal readonly double RequiredStartHp;
        internal readonly double RecoverySeconds;
        internal readonly bool Recoverable;

        internal LoadoutHealthProjection(double candidateMaxHp, double currentHpAfterSwap,
            double requiredStartHp, double recoverySeconds, bool recoverable)
        {
            CandidateMaxHp = candidateMaxHp;
            CurrentHpAfterSwap = currentHpAfterSwap;
            RequiredStartHp = requiredStartHp;
            RecoverySeconds = recoverySeconds;
            Recoverable = recoverable;
        }
    }

    internal static class LoadoutHealth
    {
        internal static LoadoutHealthProjection Project(double liveCurrentHp,
            double candidateMaxHp, double requiredStartHp, double safeRecoveryHpPerSecond)
        {
            Validate(liveCurrentHp, "liveCurrentHp");
            Validate(candidateMaxHp, "candidateMaxHp");
            Validate(requiredStartHp, "requiredStartHp");
            Validate(safeRecoveryHpPerSecond, "safeRecoveryHpPerSecond");
            var current = Math.Min(liveCurrentHp, candidateMaxHp);
            if (requiredStartHp <= current)
                return new LoadoutHealthProjection(candidateMaxHp, current,
                    requiredStartHp, 0.0, true);
            if (requiredStartHp > candidateMaxHp || safeRecoveryHpPerSecond <= 0.0)
                return new LoadoutHealthProjection(candidateMaxHp, current,
                    requiredStartHp, double.PositiveInfinity, false);
            return new LoadoutHealthProjection(candidateMaxHp, current, requiredStartHp,
                (requiredStartHp - current) / safeRecoveryHpPerSecond, true);
        }

        private static void Validate(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                throw new ArgumentOutOfRangeException(name);
        }
    }

    internal sealed class LoadoutSelection
    {
        internal readonly LoadoutCandidate Head;
        internal readonly LoadoutCandidate Chest;
        internal readonly LoadoutCandidate Legs;
        internal readonly LoadoutCandidate Boots;
        internal readonly LoadoutCandidate PrimaryWeapon;
        internal readonly LoadoutCandidate SecondaryWeapon;
        private readonly LoadoutCandidate[] _accessories;

        internal LoadoutSelection(LoadoutCandidate head, LoadoutCandidate chest,
            LoadoutCandidate legs, LoadoutCandidate boots, LoadoutCandidate primaryWeapon,
            LoadoutCandidate secondaryWeapon, LoadoutCandidate[] accessories)
        {
            Head = head;
            Chest = chest;
            Legs = legs;
            Boots = boots;
            PrimaryWeapon = primaryWeapon;
            SecondaryWeapon = secondaryWeapon;
            _accessories = accessories == null
                ? new LoadoutCandidate[0] : (LoadoutCandidate[])accessories.Clone();
        }

        internal LoadoutCandidate[] Accessories()
        {
            return (LoadoutCandidate[])_accessories.Clone();
        }

        internal IEnumerable<LoadoutCandidate> All()
        {
            if (Head != null) yield return Head;
            if (Chest != null) yield return Chest;
            if (Legs != null) yield return Legs;
            if (Boots != null) yield return Boots;
            if (PrimaryWeapon != null) yield return PrimaryWeapon;
            if (SecondaryWeapon != null) yield return SecondaryWeapon;
            for (var i = 0; i < _accessories.Length; i++) yield return _accessories[i];
        }
    }

    internal sealed class LoadoutEvaluation
    {
        internal readonly bool Feasible;
        internal readonly double TotalSeconds;
        internal readonly double SetupSeconds;
        internal readonly double RecoverySeconds;
        internal readonly double ActionSeconds;
        internal readonly double P90Seconds;
        internal readonly double TieBreaker;
        internal readonly string Reason;

        internal LoadoutEvaluation(bool feasible, double totalSeconds, double setupSeconds,
            double recoverySeconds, double actionSeconds, double p90Seconds,
            double tieBreaker, string reason)
        {
            Feasible = feasible;
            TotalSeconds = totalSeconds;
            SetupSeconds = setupSeconds;
            RecoverySeconds = recoverySeconds;
            ActionSeconds = actionSeconds;
            P90Seconds = p90Seconds;
            TieBreaker = tieBreaker;
            Reason = reason ?? string.Empty;
        }

        internal static LoadoutEvaluation Infeasible(string reason)
        {
            return new LoadoutEvaluation(false, double.PositiveInfinity, 0.0, 0.0,
                double.PositiveInfinity, double.PositiveInfinity, 0.0, reason);
        }
    }

    internal delegate LoadoutEvaluation CompleteLoadoutEvaluator(
        OptimizationObjective objective, LoadoutSelection selection, LoadoutTotals totals);

    internal delegate double LoadoutLowerBoundEvaluator(
        OptimizationObjective objective, LoadoutTotals partial, LoadoutTotals optimisticCompletion);

    internal sealed class LoadoutSearchProblem
    {
        internal readonly OptimizationObjective Objective;
        internal readonly LoadoutCandidate[] Heads;
        internal readonly LoadoutCandidate[] Chests;
        internal readonly LoadoutCandidate[] Legs;
        internal readonly LoadoutCandidate[] Boots;
        internal readonly LoadoutCandidate[] PrimaryWeapons;
        internal readonly LoadoutCandidate[] SecondaryWeapons;
        internal readonly LoadoutCandidate[] Accessories;
        internal readonly int AccessorySlots;
        internal readonly int NodeBudget;
        internal readonly CompleteLoadoutEvaluator EvaluateComplete;
        internal readonly LoadoutLowerBoundEvaluator EvaluateLowerBound;
        internal readonly LoadoutSelection InitialSelection;

        internal LoadoutSearchProblem(OptimizationObjective objective,
            LoadoutCandidate[] heads, LoadoutCandidate[] chests, LoadoutCandidate[] legs,
            LoadoutCandidate[] boots, LoadoutCandidate[] primaryWeapons,
            LoadoutCandidate[] secondaryWeapons, LoadoutCandidate[] accessories,
            int accessorySlots, int nodeBudget,
            CompleteLoadoutEvaluator evaluateComplete,
            LoadoutLowerBoundEvaluator evaluateLowerBound,
            LoadoutSelection initialSelection)
        {
            if (objective == null) throw new ArgumentNullException("objective");
            if (accessorySlots < 0) throw new ArgumentOutOfRangeException("accessorySlots");
            if (nodeBudget <= 0) throw new ArgumentOutOfRangeException("nodeBudget");
            if (evaluateComplete == null) throw new ArgumentNullException("evaluateComplete");
            if (evaluateLowerBound == null) throw new ArgumentNullException("evaluateLowerBound");
            Objective = objective;
            Heads = CopyAndValidate(heads, LoadoutSlotKind.Head, "heads");
            Chests = CopyAndValidate(chests, LoadoutSlotKind.Chest, "chests");
            Legs = CopyAndValidate(legs, LoadoutSlotKind.Legs, "legs");
            Boots = CopyAndValidate(boots, LoadoutSlotKind.Boots, "boots");
            PrimaryWeapons = CopyAndValidate(primaryWeapons, LoadoutSlotKind.PrimaryWeapon, "primaryWeapons");
            SecondaryWeapons = secondaryWeapons == null || secondaryWeapons.Length == 0
                ? new LoadoutCandidate[0]
                : CopyAndValidate(secondaryWeapons, LoadoutSlotKind.SecondaryWeapon, "secondaryWeapons");
            Accessories = accessories == null || accessories.Length == 0
                ? new LoadoutCandidate[0] : CopyAndValidate(accessories,
                    LoadoutSlotKind.Accessory, "accessories")
                .OrderBy(x => x.CanonicalKey).ToArray();
            if (accessorySlots > Accessories.Length)
                throw new ArgumentException("Accessory slot count exceeds candidate count.", "accessorySlots");
            AccessorySlots = accessorySlots;
            NodeBudget = nodeBudget;
            EvaluateComplete = evaluateComplete;
            EvaluateLowerBound = evaluateLowerBound;
            InitialSelection = initialSelection;
            ValidateMetricCounts();
            ValidateCanonicalKeys();
        }

        internal int MetricCount { get { return Heads[0].MetricCount; } }

        private static LoadoutCandidate[] CopyAndValidate(LoadoutCandidate[] values,
            LoadoutSlotKind slot, string name)
        {
            if (values == null || values.Length == 0)
                throw new ArgumentException("Every required slot needs at least one candidate.", name);
            var copy = (LoadoutCandidate[])values.Clone();
            if (copy.Any(x => x == null || x.Slot != slot))
                throw new ArgumentException("Candidate has the wrong slot kind.", name);
            return ParetoPruneCopies(copy);
        }

        private static LoadoutCandidate[] ParetoPruneCopies(LoadoutCandidate[] source)
        {
            var keep = new List<LoadoutCandidate>();
            for (var i = 0; i < source.Length; i++)
            {
                var candidate = source[i];
                var dominated = false;
                if (!candidate.HasReferenceObligation && candidate.ItemId > 0)
                {
                    for (var j = 0; j < source.Length; j++)
                    {
                        var other = source[j];
                        if (i == j || other.HasReferenceObligation
                            || other.ItemId != candidate.ItemId) continue;
                        if (Dominates(other, candidate))
                        {
                            dominated = true;
                            break;
                        }
                    }
                }
                if (!dominated) keep.Add(candidate);
            }
            return keep.ToArray();
        }

        private static bool Dominates(LoadoutCandidate a, LoadoutCandidate b)
        {
            if (a.MetricCount != b.MetricCount || a.SetupSeconds > b.SetupSeconds
                || (a.Tags | b.Tags) != a.Tags) return false;
            var strict = a.SetupSeconds < b.SetupSeconds || a.Tags != b.Tags;
            for (var i = 0; i < a.MetricCount; i++)
            {
                if (a.Metric(i) < b.Metric(i)) return false;
                if (a.Metric(i) > b.Metric(i)) strict = true;
            }
            return strict;
        }

        private void ValidateMetricCounts()
        {
            var all = Heads.Concat(Chests).Concat(Legs).Concat(Boots)
                .Concat(PrimaryWeapons).Concat(SecondaryWeapons).Concat(Accessories);
            if (all.Any(x => x.MetricCount != MetricCount))
                throw new ArgumentException("Every candidate must use the same metric vector length.");
        }

        private void ValidateCanonicalKeys()
        {
            var seen = new HashSet<long>();
            foreach (var accessory in Accessories)
                if (!seen.Add(accessory.CanonicalKey))
                    throw new ArgumentException("Accessory canonical keys must be unique.");
        }
    }

    internal sealed class LoadoutSearchResult
    {
        internal readonly LoadoutSelection Selection;
        internal readonly LoadoutEvaluation Evaluation;
        internal readonly double IncumbentSeconds;
        internal readonly double OptimisticLowerBoundSeconds;
        internal readonly double AbsoluteGapSeconds;
        internal readonly double RelativeGap;
        internal readonly bool IsProvenOptimal;
        internal readonly int ExploredStates;
        internal readonly int PrunedStates;
        internal readonly int CompleteEvaluations;
        internal readonly int UniqueAccessoryCombinations;

        internal LoadoutSearchResult(LoadoutSelection selection, LoadoutEvaluation evaluation,
            double incumbentSeconds, double optimisticLowerBoundSeconds,
            double absoluteGapSeconds, double relativeGap, bool isProvenOptimal,
            int exploredStates, int prunedStates, int completeEvaluations,
            int uniqueAccessoryCombinations)
        {
            Selection = selection;
            Evaluation = evaluation;
            IncumbentSeconds = incumbentSeconds;
            OptimisticLowerBoundSeconds = optimisticLowerBoundSeconds;
            AbsoluteGapSeconds = absoluteGapSeconds;
            RelativeGap = relativeGap;
            IsProvenOptimal = isProvenOptimal;
            ExploredStates = exploredStates;
            PrunedStates = prunedStates;
            CompleteEvaluations = completeEvaluations;
            UniqueAccessoryCombinations = uniqueAccessoryCombinations;
        }
    }

    internal static class ParetoLoadoutSolver
    {
        private sealed class Node
        {
            internal int Stage;
            internal int AccessoryMinimumIndex;
            internal readonly List<LoadoutCandidate> Selected = new List<LoadoutCandidate>();
            internal readonly HashSet<int> UsedIds = new HashSet<int>();
            internal readonly HashSet<long> UsedReferences = new HashSet<long>();
            internal LoadoutTotals Totals;
            internal double LowerBound;
            internal bool Discarded;

            internal Node Clone()
            {
                var copy = new Node
                {
                    Stage = Stage,
                    AccessoryMinimumIndex = AccessoryMinimumIndex,
                    Totals = Totals,
                    LowerBound = LowerBound
                };
                copy.Selected.AddRange(Selected);
                foreach (var id in UsedIds) copy.UsedIds.Add(id);
                foreach (var key in UsedReferences) copy.UsedReferences.Add(key);
                return copy;
            }
        }

        private sealed class Frontier
        {
            private readonly SortedDictionary<double, Stack<Node>> _values =
                new SortedDictionary<double, Stack<Node>>();
            internal int Count { get; private set; }

            internal void Add(Node node)
            {
                Stack<Node> stack;
                if (!_values.TryGetValue(node.LowerBound, out stack))
                {
                    stack = new Stack<Node>();
                    _values.Add(node.LowerBound, stack);
                }
                // LIFO within an equal admissible bound reaches a complete incumbent before
                // breadth-first expansion exhausts a large accessory frontier.
                stack.Push(node);
                Count++;
            }

            internal Node Pop()
            {
                while (_values.Count > 0)
                {
                    var first = _values.First();
                    var node = first.Value.Pop();
                    if (first.Value.Count == 0) _values.Remove(first.Key);
                    Count--;
                    if (!node.Discarded) return node;
                }
                return null;
            }

            internal double MinimumBound()
            {
                while (_values.Count > 0)
                {
                    var first = _values.First();
                    while (first.Value.Count > 0 && first.Value.Peek().Discarded)
                    {
                        first.Value.Pop();
                        Count--;
                    }
                    if (first.Value.Count > 0) return first.Key;
                    _values.Remove(first.Key);
                }
                return double.PositiveInfinity;
            }
        }

        internal static LoadoutSearchResult Solve(LoadoutSearchProblem problem)
        {
            if (problem == null) throw new ArgumentNullException("problem");
            var empty = new LoadoutTotals(new double[problem.MetricCount], 0.0, 0, 0L);
            LoadoutSelection incumbentSelection = null;
            LoadoutEvaluation incumbentEvaluation = null;
            if (problem.InitialSelection != null)
            {
                var initialTotals = Sum(problem.InitialSelection, problem.MetricCount);
                if (IsLegal(problem.InitialSelection, problem.AccessorySlots))
                {
                    var initial = problem.EvaluateComplete(problem.Objective,
                        problem.InitialSelection, initialTotals);
                    if (initial != null && initial.Feasible)
                    {
                        incumbentSelection = problem.InitialSelection;
                        incumbentEvaluation = initial;
                    }
                }
            }

            var root = new Node {Stage = 0, AccessoryMinimumIndex = 0, Totals = empty};
            root.LowerBound = Bound(problem, root);
            var frontier = new Frontier();
            frontier.Add(root);
            var frontiers = new Dictionary<string, List<Node>>();
            var explored = 0;
            var pruned = 0;
            var completes = 0;
            var accessoryCombinations = 0;

            while (frontier.Count > 0 && explored < problem.NodeBudget)
            {
                var node = frontier.Pop();
                if (node == null) break;
                if (incumbentEvaluation != null
                    && node.LowerBound >= incumbentEvaluation.TotalSeconds)
                {
                    pruned++;
                    continue;
                }
                explored++;
                if (node.Stage == 6 + problem.AccessorySlots)
                {
                    var selection = ToSelection(node, problem.AccessorySlots);
                    var evaluation = problem.EvaluateComplete(problem.Objective,
                        selection, node.Totals);
                    completes++;
                    accessoryCombinations++;
                    if (evaluation != null && evaluation.Feasible
                        && Better(evaluation, selection, incumbentEvaluation, incumbentSelection))
                    {
                        incumbentSelection = selection;
                        incumbentEvaluation = evaluation;
                    }
                    continue;
                }

                var candidates = CandidatesForStage(problem, node.Stage);
                var accessoryStage = node.Stage >= 6;
                var accessoryRemainingAfter = accessoryStage
                    ? problem.AccessorySlots - (node.Stage - 6) - 1 : 0;
                for (var i = 0; i < candidates.Length; i++)
                {
                    if (accessoryStage)
                    {
                        if (i < node.AccessoryMinimumIndex) continue;
                        if (candidates.Length - (i + 1) < accessoryRemainingAfter) continue;
                    }
                    var candidate = candidates[i];
                    if (node.UsedReferences.Contains(candidate.ReferenceKey)
                        || candidate.ItemId > 0 && node.UsedIds.Contains(candidate.ItemId))
                        continue;
                    var child = node.Clone();
                    child.Stage++;
                    if (accessoryStage) child.AccessoryMinimumIndex = i + 1;
                    child.Selected.Add(candidate);
                    child.UsedReferences.Add(candidate.ReferenceKey);
                    if (candidate.ItemId > 0) child.UsedIds.Add(candidate.ItemId);
                    child.Totals = node.Totals.Add(candidate);
                    child.LowerBound = Bound(problem, child);
                    if (double.IsNaN(child.LowerBound)
                        || incumbentEvaluation != null
                        && child.LowerBound >= incumbentEvaluation.TotalSeconds)
                    {
                        pruned++;
                        continue;
                    }
                    if (ParetoDominated(child, frontiers))
                    {
                        pruned++;
                        continue;
                    }
                    frontier.Add(child);
                }
            }

            var frontierBound = frontier.MinimumBound();
            var incumbent = incumbentEvaluation == null
                ? double.PositiveInfinity : incumbentEvaluation.TotalSeconds;
            var budgetInterrupted = frontier.Count > 0
                                    && (double.IsPositiveInfinity(incumbent)
                                        || frontierBound < incumbent);
            var lower = budgetInterrupted ? Math.Min(incumbent, frontierBound) : incumbent;
            if (double.IsPositiveInfinity(incumbent) && !budgetInterrupted)
                lower = double.PositiveInfinity;
            var gap = double.IsPositiveInfinity(incumbent)
                ? budgetInterrupted ? double.PositiveInfinity : 0.0
                : Math.Max(0.0, incumbent - lower);
            var relative = double.IsPositiveInfinity(gap) ? double.PositiveInfinity
                : gap / Math.Max(1e-12, Math.Abs(incumbent));
            return new LoadoutSearchResult(incumbentSelection, incumbentEvaluation,
                incumbent, lower, gap, relative, !budgetInterrupted,
                explored, pruned, completes, accessoryCombinations);
        }

        private static LoadoutCandidate[] CandidatesForStage(LoadoutSearchProblem p, int stage)
        {
            switch (stage)
            {
                case 0: return p.Heads;
                case 1: return p.Chests;
                case 2: return p.Legs;
                case 3: return p.Boots;
                case 4: return p.PrimaryWeapons;
                case 5: return p.SecondaryWeapons.Length == 0
                    ? new[] {SyntheticEmptySecondary(p.MetricCount)} : p.SecondaryWeapons;
                default: return p.Accessories;
            }
        }

        private static LoadoutCandidate SyntheticEmptySecondary(int metricCount)
        {
            return new LoadoutCandidate(long.MaxValue, long.MaxValue, 0,
                LoadoutSlotKind.SecondaryWeapon, new double[metricCount], 0.0, 0L, true, null);
        }

        private static double Bound(LoadoutSearchProblem problem, Node node)
        {
            var metrics = node.Totals.Metrics();
            var tags = node.Totals.Tags;
            for (var stage = node.Stage; stage < 6 + problem.AccessorySlots; stage++)
            {
                var candidates = CandidatesForStage(problem, stage);
                var start = stage >= 6 ? node.AccessoryMinimumIndex : 0;
                for (var metric = 0; metric < metrics.Length; metric++)
                {
                    var best = 0.0;
                    for (var i = start; i < candidates.Length; i++)
                        if (candidates[i].Metric(metric) > best) best = candidates[i].Metric(metric);
                    metrics[metric] += best;
                }
                for (var i = start; i < candidates.Length; i++) tags |= candidates[i].Tags;
            }
            var optimistic = new LoadoutTotals(metrics, node.Totals.SetupSeconds,
                node.Totals.SwitchCount, tags);
            var bound = problem.EvaluateLowerBound(problem.Objective, node.Totals, optimistic);
            return double.IsNegativeInfinity(bound) ? 0.0 : bound;
        }

        private static bool ParetoDominated(Node candidate,
            Dictionary<string, List<Node>> frontiers)
        {
            var signature = candidate.Stage + ":" + candidate.AccessoryMinimumIndex + ":"
                            + string.Join(",", candidate.UsedIds.OrderBy(x => x)
                                .Select(x => x.ToString()).ToArray()) + ":refs="
                            + string.Join(",", candidate.UsedReferences.OrderBy(x => x)
                                .Select(x => x.ToString()).ToArray());
            List<Node> peers;
            if (!frontiers.TryGetValue(signature, out peers))
            {
                peers = new List<Node>();
                frontiers.Add(signature, peers);
            }
            for (var i = 0; i < peers.Count; i++)
                if (!peers[i].Discarded && Dominates(peers[i].Totals, candidate.Totals))
                    return true;
            for (var i = 0; i < peers.Count; i++)
                if (!peers[i].Discarded && Dominates(candidate.Totals, peers[i].Totals))
                    peers[i].Discarded = true;
            peers.Add(candidate);
            return false;
        }

        private static bool Dominates(LoadoutTotals a, LoadoutTotals b)
        {
            if (a.SetupSeconds > b.SetupSeconds || (a.Tags | b.Tags) != a.Tags) return false;
            var strict = a.SetupSeconds < b.SetupSeconds || a.Tags != b.Tags;
            for (var i = 0; i < a.MetricCount; i++)
            {
                if (a.Metric(i) < b.Metric(i)) return false;
                if (a.Metric(i) > b.Metric(i)) strict = true;
            }
            return strict;
        }

        private static LoadoutTotals Sum(LoadoutSelection selection, int metricCount)
        {
            var total = new LoadoutTotals(new double[metricCount], 0.0, 0, 0L);
            foreach (var candidate in selection.All()) total = total.Add(candidate);
            return total;
        }

        private static bool IsLegal(LoadoutSelection selection, int accessorySlots)
        {
            if (selection == null || selection.Head == null || selection.Chest == null
                || selection.Legs == null || selection.Boots == null
                || selection.PrimaryWeapon == null
                || selection.Accessories().Length != accessorySlots) return false;
            var ids = new HashSet<int>();
            var references = new HashSet<long>();
            foreach (var candidate in selection.All())
            {
                if (!references.Add(candidate.ReferenceKey)) return false;
                if (candidate.ItemId > 0 && !ids.Add(candidate.ItemId)) return false;
            }
            var accessories = selection.Accessories();
            for (var i = 1; i < accessories.Length; i++)
                if (accessories[i - 1].CanonicalKey >= accessories[i].CanonicalKey) return false;
            return true;
        }

        private static LoadoutSelection ToSelection(Node node, int accessorySlots)
        {
            var secondary = node.Selected[5].Token == null && node.Selected[5].ItemId == 0
                ? null : node.Selected[5];
            return new LoadoutSelection(node.Selected[0], node.Selected[1], node.Selected[2],
                node.Selected[3], node.Selected[4], secondary,
                node.Selected.Skip(6).Take(accessorySlots).ToArray());
        }

        private static bool Better(LoadoutEvaluation candidate, LoadoutSelection candidateSelection,
            LoadoutEvaluation incumbent, LoadoutSelection incumbentSelection)
        {
            if (incumbent == null) return true;
            var compare = candidate.TotalSeconds.CompareTo(incumbent.TotalSeconds);
            if (compare != 0) return compare < 0;
            compare = candidate.P90Seconds.CompareTo(incumbent.P90Seconds);
            if (compare != 0) return compare < 0;
            compare = candidate.TieBreaker.CompareTo(incumbent.TieBreaker);
            if (compare != 0) return compare < 0;
            var a = string.Join(",", candidateSelection.All().Select(x => x.CanonicalKey.ToString()).ToArray());
            var b = string.Join(",", incumbentSelection.All().Select(x => x.CanonicalKey.ToString()).ToArray());
            return string.CompareOrdinal(a, b) < 0;
        }
    }
}
