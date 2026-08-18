using System;
using System.Collections.Generic;
using System.Linq;

/*
FILE PURPOSE

Purpose: ProgressionDependencyGraph is the typed terminal DAG and lower-bound heuristic shared by
the roadmap and task-28 shadow scheduler. It defines the verified END sequence plus every required
challenge completion as the terminal predicate, with explicit hard gates, rewards, provenance, and
shared-resource claims.

Mechanism: A fixed source-backed acyclic catalog evaluates typed OptimizationSnapshot facts. An
already-satisfied gate short-circuits its branch; otherwise dependency completion times combine by
maximum because independent prerequisites may progress concurrently, then the node's own work is
added. Evaluation publishes the parallel horizon, critical branch, branch slack, model completeness,
and outstanding shared-resource contention without parsing labels or strategy prose.

Inputs and outputs: Inputs are one immutable snapshot and typed per-node work estimates. Outputs are
immutable node/branch evaluations and resource summaries suitable for scheduler heuristics and UI.

Invariants and safety: All non-composite nodes have an explicit typed hard gate. Every END item uses
exactly-one ordinary ownership. Checker delivery, T12 ordered capacity, END Card Mayo/deck slack,
Blood, T14 retry delivery, and final 40-slot layout are separate gates. Terminal satisfaction means
verified END UI state AND the configured required challenge ledger, never either one alone.

Extension points and non-goals: Task 28 supplies state-transition/search estimates and resolves
shared resources chronologically. This graph is an ideal parallel lower bound plus audit surface; it
does not claim independent branches can all receive a scarce resource simultaneously and performs
no controller mutation or autonomous END action.
*/
namespace NGUInjector.Autopilot
{
    internal enum ProgressionNodeKey
    {
        Terminal,
        EndSequence,
        RequiredChallenges,
        SadisticDifficulty,
        SadisticBoss225,
        SadisticBoss248,
        SadisticBoss295,
        SadisticBoss300,
        Titan13Defeated,
        Move69Unlocked,
        Perk231Source,
        Quirk176Source,
        HacksZeroThroughFourteenCapped,
        EndHackComplete,
        Wish203Complete,
        ItopodFloor1450,
        EndCardHeld,
        EndBloodReady,
        EndFiltersClear,
        OneUsableInventorySlot,
        TwoUsableInventorySlots,
        FinalInventoryLayoutCapacity,
        Titan12CapacityFor483,
        Titan12CapacityFor489,
        Titan12CapacityFor493,
        Titan12CapacityFor484,
        EndCardDeckCapacity,
        EndCardMayoReserve,
        EndItem480,
        EndItem481,
        EndItem482,
        EndItem483,
        EndItem484,
        EndItem485,
        EndItem486,
        EndItem487,
        EndItem488,
        EndItem489,
        EndItem490,
        EndItem491,
        EndItem492,
        EndItem493,
        EndItem494,
        EndItem495,
        ChallengeBasic,
        ChallengeNoAugments,
        ChallengeTwentyFourHour,
        ChallengeOneHundredLevel,
        ChallengeNoEquipment,
        ChallengeTroll,
        ChallengeNoRebirth,
        ChallengeLaserSword,
        ChallengeBlind,
        ChallengeNoNgu,
        ChallengeNoTimeMachine
    }

    internal enum ProgressionNodeKind
    {
        Composite,
        PersistentGate,
        LongRunningBranch,
        StochasticOpportunity,
        Materialization,
        Challenge,
        FinalTransaction
    }

    internal enum ProgressionGateKind
    {
        DependenciesOnly,
        DifficultyAtLeast,
        FactAtLeast,
        FactsAtLeast,
        OrdinaryEndItemExactlyOne,
        RequiredChallengeComplete,
        EndSequenceVerified
    }

    internal enum ProgressionRewardKind
    {
        PersistentGate,
        SourceCompletion,
        OrdinaryEndItem,
        ChallengeCompletion,
        EndSequence,
        TerminalObjective
    }

    internal enum ProgressionSharedResourceKind
    {
        AdventureMode,
        PhysicalLoadout,
        OnlineTime,
        OrdinaryInventoryCapacity,
        CardDeckCapacity,
        MayoZero,
        MayoOne,
        MayoTwo,
        MayoThree,
        MayoFour,
        MayoFive,
        Blood,
        PerkPoints,
        QuirkPoints,
        Energy,
        Magic,
        ResourceThree,
        FightBoss,
        ResetBoundary
    }

    internal enum ProgressionEstimateProvenance
    {
        SourceKnown,
        DerivedFromSource,
        Empirical,
        Heuristic,
        ObjectiveConfigured,
        Unknown
    }

    internal sealed class ProgressionGate
    {
        private readonly OptimizationFactKey[] _factKeys;
        private readonly double[] _thresholds;
        internal readonly ProgressionGateKind Kind;
        internal readonly OptimizationDifficulty Difficulty;
        internal readonly int EndItemId;
        internal readonly OptimizationChallengeKind Challenge;

        private ProgressionGate(ProgressionGateKind kind,
            OptimizationDifficulty difficulty, int endItemId,
            OptimizationChallengeKind challenge, OptimizationFactKey[] factKeys,
            double[] thresholds)
        {
            Kind = kind;
            Difficulty = difficulty;
            EndItemId = endItemId;
            Challenge = challenge;
            _factKeys = factKeys == null ? new OptimizationFactKey[0]
                : (OptimizationFactKey[])factKeys.Clone();
            _thresholds = thresholds == null ? new double[0] : (double[])thresholds.Clone();
        }

        internal static ProgressionGate DependenciesOnly()
        {
            return new ProgressionGate(ProgressionGateKind.DependenciesOnly,
                OptimizationDifficulty.Normal, -1, default(OptimizationChallengeKind),
                null, null);
        }

        internal static ProgressionGate DifficultyAtLeast(OptimizationDifficulty difficulty)
        {
            return new ProgressionGate(ProgressionGateKind.DifficultyAtLeast,
                difficulty, -1, default(OptimizationChallengeKind), null, null);
        }

        internal static ProgressionGate FactAtLeast(OptimizationFactKey key, double threshold)
        {
            return FactsAtLeast(new[] {key}, new[] {threshold});
        }

        internal static ProgressionGate FactsAtLeast(OptimizationFactKey[] keys,
            double[] thresholds)
        {
            if (keys == null || thresholds == null || keys.Length == 0
                || keys.Length != thresholds.Length)
                throw new ArgumentException("aligned fact keys and thresholds are required");
            for (var i = 0; i < keys.Length; i++)
            {
                if (!Enum.IsDefined(typeof(OptimizationFactKey), keys[i]))
                    throw new ArgumentOutOfRangeException("keys");
                if (double.IsNaN(thresholds[i]) || double.IsInfinity(thresholds[i])
                    || thresholds[i] < 0.0)
                    throw new ArgumentOutOfRangeException("thresholds");
            }
            return new ProgressionGate(keys.Length == 1
                    ? ProgressionGateKind.FactAtLeast : ProgressionGateKind.FactsAtLeast,
                OptimizationDifficulty.Normal, -1, default(OptimizationChallengeKind),
                keys, thresholds);
        }

        internal static ProgressionGate EndItemExactlyOne(int itemId)
        {
            if (!MechanicsEndgame.IsProtectedItem(itemId))
                throw new ArgumentOutOfRangeException("itemId");
            return new ProgressionGate(ProgressionGateKind.OrdinaryEndItemExactlyOne,
                OptimizationDifficulty.Normal, itemId,
                default(OptimizationChallengeKind), null, null);
        }

        internal static ProgressionGate ChallengeComplete(OptimizationChallengeKind challenge)
        {
            if (!Enum.IsDefined(typeof(OptimizationChallengeKind), challenge))
                throw new ArgumentOutOfRangeException("challenge");
            return new ProgressionGate(ProgressionGateKind.RequiredChallengeComplete,
                OptimizationDifficulty.Normal, -1, challenge, null, null);
        }

        internal static ProgressionGate EndSequenceVerified()
        {
            return new ProgressionGate(ProgressionGateKind.EndSequenceVerified,
                OptimizationDifficulty.Normal, -1, default(OptimizationChallengeKind),
                null, null);
        }

        internal OptimizationFactKey[] FactKeys() { return (OptimizationFactKey[])_factKeys.Clone(); }
        internal double[] Thresholds() { return (double[])_thresholds.Clone(); }

        internal bool IsSatisfied(OptimizationSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            switch (Kind)
            {
                case ProgressionGateKind.DependenciesOnly: return false;
                case ProgressionGateKind.DifficultyAtLeast:
                    return snapshot.Difficulty >= Difficulty;
                case ProgressionGateKind.FactAtLeast:
                case ProgressionGateKind.FactsAtLeast:
                    for (var i = 0; i < _factKeys.Length; i++)
                        if (snapshot.Facts.Get(_factKeys[i]) < _thresholds[i]) return false;
                    return true;
                case ProgressionGateKind.OrdinaryEndItemExactlyOne:
                    return snapshot.EndItem(EndItemId).TerminalPiecePresent;
                case ProgressionGateKind.RequiredChallengeComplete:
                    return snapshot.Challenge(Challenge).Complete;
                case ProgressionGateKind.EndSequenceVerified:
                    return snapshot.EndSequenceVerified;
                default: throw new ArgumentOutOfRangeException();
            }
        }
    }

    internal sealed class ProgressionReward
    {
        internal readonly ProgressionRewardKind Kind;
        internal readonly int EndItemId;
        internal readonly OptimizationChallengeKind Challenge;

        internal ProgressionReward(ProgressionRewardKind kind, int endItemId = -1,
            OptimizationChallengeKind challenge = default(OptimizationChallengeKind))
        {
            if (!Enum.IsDefined(typeof(ProgressionRewardKind), kind))
                throw new ArgumentOutOfRangeException("kind");
            if (kind == ProgressionRewardKind.OrdinaryEndItem
                && !MechanicsEndgame.IsProtectedItem(endItemId))
                throw new ArgumentOutOfRangeException("endItemId");
            if (kind == ProgressionRewardKind.ChallengeCompletion
                && !Enum.IsDefined(typeof(OptimizationChallengeKind), challenge))
                throw new ArgumentOutOfRangeException("challenge");
            Kind = kind;
            EndItemId = endItemId;
            Challenge = challenge;
        }
    }

    internal sealed class ProgressionSharedResourceClaim
    {
        internal readonly ProgressionSharedResourceKind Resource;
        internal readonly double Units;
        internal readonly bool Exclusive;

        internal ProgressionSharedResourceClaim(ProgressionSharedResourceKind resource,
            double units, bool exclusive)
        {
            if (!Enum.IsDefined(typeof(ProgressionSharedResourceKind), resource))
                throw new ArgumentOutOfRangeException("resource");
            if (double.IsNaN(units) || double.IsInfinity(units) || units <= 0.0)
                throw new ArgumentOutOfRangeException("units");
            Resource = resource;
            Units = units;
            Exclusive = exclusive;
        }
    }

    internal sealed class ProgressionNode
    {
        private readonly ProgressionNodeKey[] _dependencies;
        private readonly ProgressionReward[] _rewards;
        private readonly ProgressionSharedResourceClaim[] _resources;
        internal readonly ProgressionNodeKey Key;
        internal readonly ProgressionNodeKind Kind;
        internal readonly ProgressionGate Gate;
        internal readonly bool RequiresOwnEstimate;
        internal readonly ProgressionEstimateProvenance DefinitionProvenance;

        internal ProgressionNode(ProgressionNodeKey key, ProgressionNodeKind kind,
            ProgressionGate gate, bool requiresOwnEstimate,
            ProgressionEstimateProvenance definitionProvenance,
            ProgressionNodeKey[] dependencies, ProgressionReward[] rewards,
            ProgressionSharedResourceClaim[] resources)
        {
            if (!Enum.IsDefined(typeof(ProgressionNodeKey), key))
                throw new ArgumentOutOfRangeException("key");
            if (!Enum.IsDefined(typeof(ProgressionNodeKind), kind))
                throw new ArgumentOutOfRangeException("kind");
            if (gate == null) throw new ArgumentNullException("gate");
            if (kind != ProgressionNodeKind.Composite
                && gate.Kind == ProgressionGateKind.DependenciesOnly)
                throw new ArgumentException("every non-composite hard gate must be typed");
            Key = key;
            Kind = kind;
            Gate = gate;
            RequiresOwnEstimate = requiresOwnEstimate;
            DefinitionProvenance = definitionProvenance;
            _dependencies = dependencies == null ? new ProgressionNodeKey[0]
                : (ProgressionNodeKey[])dependencies.Clone();
            _rewards = rewards == null ? new ProgressionReward[0]
                : (ProgressionReward[])rewards.Clone();
            _resources = resources == null ? new ProgressionSharedResourceClaim[0]
                : (ProgressionSharedResourceClaim[])resources.Clone();
            if (_dependencies.Distinct().Count() != _dependencies.Length)
                throw new ArgumentException("node dependencies cannot contain duplicates");
            if (_rewards.Any(x => x == null) || _resources.Any(x => x == null))
                throw new ArgumentException("node rewards/resources cannot contain null");
        }

        internal ProgressionNodeKey[] Dependencies() { return (ProgressionNodeKey[])_dependencies.Clone(); }
        internal ProgressionReward[] Rewards() { return (ProgressionReward[])_rewards.Clone(); }
        internal ProgressionSharedResourceClaim[] Resources()
        {
            return (ProgressionSharedResourceClaim[])_resources.Clone();
        }
    }

    internal sealed class ProgressionWorkEstimate
    {
        internal readonly ProgressionNodeKey Node;
        internal readonly double MeanSeconds;
        internal readonly double P90Seconds;
        internal readonly double LowerBoundSeconds;
        internal readonly double UpperBoundSeconds;
        internal readonly ProgressionEstimateProvenance Provenance;
        internal readonly int SampleCount;
        internal readonly double Confidence;
        internal readonly bool Available;

        internal ProgressionWorkEstimate(ProgressionNodeKey node, double meanSeconds,
            double p90Seconds, double lowerBoundSeconds, double upperBoundSeconds,
            ProgressionEstimateProvenance provenance, int sampleCount = 0,
            double confidence = 1.0)
        {
            if (!Enum.IsDefined(typeof(ProgressionNodeKey), node))
                throw new ArgumentOutOfRangeException("node");
            if (!FiniteNonNegative(meanSeconds) || !FiniteNonNegative(p90Seconds)
                || !FiniteNonNegative(lowerBoundSeconds)
                || !FiniteNonNegative(upperBoundSeconds)
                || lowerBoundSeconds > meanSeconds || upperBoundSeconds < meanSeconds
                || upperBoundSeconds < p90Seconds)
                throw new ArgumentOutOfRangeException("meanSeconds");
            if (!Enum.IsDefined(typeof(ProgressionEstimateProvenance), provenance)
                || provenance == ProgressionEstimateProvenance.Unknown)
                throw new ArgumentOutOfRangeException("provenance");
            if (sampleCount < 0) throw new ArgumentOutOfRangeException("sampleCount");
            if (double.IsNaN(confidence) || double.IsInfinity(confidence)
                || confidence < 0.0 || confidence > 1.0)
                throw new ArgumentOutOfRangeException("confidence");
            if (provenance == ProgressionEstimateProvenance.Empirical
                && (sampleCount <= 0 || confidence <= 0.0))
                throw new ArgumentException(
                    "empirical estimates require positive samples and confidence");
            Node = node;
            MeanSeconds = meanSeconds;
            P90Seconds = p90Seconds;
            LowerBoundSeconds = lowerBoundSeconds;
            UpperBoundSeconds = upperBoundSeconds;
            Provenance = provenance;
            SampleCount = sampleCount;
            Confidence = confidence;
            Available = true;
        }

        private static bool FiniteNonNegative(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0.0;
        }
    }

    internal sealed class ProgressionNodeEvaluation
    {
        internal readonly ProgressionNodeKey Node;
        internal readonly bool GateSatisfied;
        internal readonly bool ModelComplete;
        internal readonly double MeanSeconds;
        internal readonly double P90Seconds;
        internal readonly double LowerBoundSeconds;
        internal readonly double UpperBoundSeconds;
        internal readonly ProgressionEstimateProvenance Provenance;
        internal readonly ProgressionNodeKey DominantBranch;

        internal ProgressionNodeEvaluation(ProgressionNodeKey node, bool gateSatisfied,
            bool modelComplete, double meanSeconds, double p90Seconds,
            double lowerBoundSeconds, double upperBoundSeconds,
            ProgressionEstimateProvenance provenance,
            ProgressionNodeKey dominantBranch)
        {
            Node = node;
            GateSatisfied = gateSatisfied;
            ModelComplete = modelComplete;
            MeanSeconds = meanSeconds;
            P90Seconds = p90Seconds;
            LowerBoundSeconds = lowerBoundSeconds;
            UpperBoundSeconds = upperBoundSeconds;
            Provenance = provenance;
            DominantBranch = dominantBranch;
        }
    }

    internal sealed class ProgressionParallelBranch
    {
        internal readonly ProgressionNodeKey Node;
        internal readonly bool Required;
        internal readonly bool Complete;
        internal readonly bool ModelComplete;
        internal readonly double FinishMeanSeconds;
        internal readonly double SlackSeconds;
        internal readonly ProgressionEstimateProvenance Provenance;

        internal ProgressionParallelBranch(ProgressionNodeKey node, bool required,
            ProgressionNodeEvaluation evaluation, double parallelHorizon)
        {
            Node = node;
            Required = required;
            Complete = evaluation.GateSatisfied;
            ModelComplete = evaluation.ModelComplete;
            FinishMeanSeconds = evaluation.MeanSeconds;
            SlackSeconds = evaluation.ModelComplete && !double.IsInfinity(parallelHorizon)
                ? Math.Max(0.0, parallelHorizon - evaluation.MeanSeconds) : -1.0;
            Provenance = evaluation.Provenance;
        }
    }

    internal sealed class ProgressionSharedResourceSummary
    {
        internal readonly ProgressionSharedResourceKind Resource;
        internal readonly int OutstandingClaimCount;
        internal readonly int ExclusiveClaimCount;
        internal readonly double DeclaredUnits;
        internal readonly bool TouchesCriticalBranch;

        internal ProgressionSharedResourceSummary(ProgressionSharedResourceKind resource,
            int count, int exclusiveCount, double declaredUnits, bool touchesCriticalBranch)
        {
            Resource = resource;
            OutstandingClaimCount = count;
            ExclusiveClaimCount = exclusiveCount;
            DeclaredUnits = declaredUnits;
            TouchesCriticalBranch = touchesCriticalBranch;
        }
    }

    internal sealed class ProgressionGraphEvaluation
    {
        private readonly ProgressionParallelBranch[] _branches;
        private readonly ProgressionSharedResourceSummary[] _resources;
        internal readonly string SnapshotHash;
        internal readonly ProgressionNodeEvaluation Terminal;
        internal readonly ProgressionNodeEvaluation EndSequence;
        internal readonly double ParallelHorizonSeconds;
        internal readonly ProgressionNodeKey CriticalBranch;

        internal ProgressionGraphEvaluation(string snapshotHash,
            ProgressionNodeEvaluation terminal, ProgressionNodeEvaluation endSequence,
            double parallelHorizonSeconds,
            ProgressionNodeKey criticalBranch, ProgressionParallelBranch[] branches,
            ProgressionSharedResourceSummary[] resources)
        {
            SnapshotHash = snapshotHash;
            Terminal = terminal;
            EndSequence = endSequence;
            ParallelHorizonSeconds = parallelHorizonSeconds;
            CriticalBranch = criticalBranch;
            _branches = (ProgressionParallelBranch[])branches.Clone();
            _resources = (ProgressionSharedResourceSummary[])resources.Clone();
        }

        internal ProgressionParallelBranch[] ParallelBranches()
        {
            return (ProgressionParallelBranch[])_branches.Clone();
        }

        internal ProgressionSharedResourceSummary[] SharedResources()
        {
            return (ProgressionSharedResourceSummary[])_resources.Clone();
        }
    }

    internal sealed class ProgressionDependencyGraph
    {
        private readonly ProgressionNode[] _nodes;
        private readonly Dictionary<ProgressionNodeKey, ProgressionNode> _byKey;

        private ProgressionDependencyGraph(ProgressionNode[] nodes)
        {
            _nodes = (ProgressionNode[])nodes.Clone();
            _byKey = new Dictionary<ProgressionNodeKey, ProgressionNode>();
            for (var i = 0; i < _nodes.Length; i++)
            {
                if (_nodes[i] == null || _byKey.ContainsKey(_nodes[i].Key))
                    throw new ArgumentException("graph nodes must be non-null and unique");
                _byKey.Add(_nodes[i].Key, _nodes[i]);
            }
            foreach (var node in _nodes)
                foreach (var dependency in node.Dependencies())
                    if (!_byKey.ContainsKey(dependency))
                        throw new ArgumentException("every dependency must name a graph node");
            ValidateAcyclic();
        }

        internal static ProgressionDependencyGraph CreateTerminalGraph()
        {
            return new ProgressionDependencyGraph(BuildTerminalNodes().ToArray());
        }

        internal ProgressionNode Node(ProgressionNodeKey key)
        {
            ProgressionNode node;
            if (!_byKey.TryGetValue(key, out node))
                throw new ArgumentOutOfRangeException("key");
            return node;
        }

        internal ProgressionNode[] Nodes() { return (ProgressionNode[])_nodes.Clone(); }

        internal ProgressionGraphEvaluation Evaluate(OptimizationSnapshot snapshot,
            IEnumerable<ProgressionWorkEstimate> estimates)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            if (estimates == null) throw new ArgumentNullException("estimates");
            var estimateMap = new Dictionary<ProgressionNodeKey, ProgressionWorkEstimate>();
            foreach (var estimate in estimates)
            {
                if (estimate == null) throw new ArgumentException("estimates cannot contain null");
                if (estimateMap.ContainsKey(estimate.Node))
                    throw new ArgumentException("estimates cannot contain duplicate node keys");
                estimateMap.Add(estimate.Node, estimate);
            }
            var memo = new Dictionary<ProgressionNodeKey, ProgressionNodeEvaluation>();
            var terminal = EvaluateNode(ProgressionNodeKey.Terminal, snapshot,
                estimateMap, memo);
            var endSequence = EvaluateNode(ProgressionNodeKey.EndSequence, snapshot,
                estimateMap, memo);

            var branchKeys = ParallelBranchKeys();
            var branchEvaluations = new List<KeyValuePair<ProgressionNodeKey,
                ProgressionNodeEvaluation>>();
            var horizon = 0.0;
            var horizonKnown = true;
            var critical = ProgressionNodeKey.EndItem480;
            for (var i = 0; i < branchKeys.Length; i++)
            {
                if (IsChallengeNode(branchKeys[i])
                    && !snapshot.Challenge(ChallengeForNode(branchKeys[i])).Required)
                    continue;
                var evaluation = EvaluateNode(branchKeys[i], snapshot, estimateMap, memo);
                branchEvaluations.Add(new KeyValuePair<ProgressionNodeKey,
                    ProgressionNodeEvaluation>(branchKeys[i], evaluation));
                if (!evaluation.ModelComplete) horizonKnown = false;
                if (evaluation.MeanSeconds >= horizon)
                {
                    horizon = evaluation.MeanSeconds;
                    critical = branchKeys[i];
                }
            }
            if (!horizonKnown) horizon = double.PositiveInfinity;
            var branches = branchEvaluations.Select(x => new ProgressionParallelBranch(
                x.Key, true, x.Value, horizon)).ToArray();
            var resources = BuildResourceSummaries(snapshot, critical);
            return new ProgressionGraphEvaluation(snapshot.SnapshotHash, terminal, endSequence,
                horizon, critical, branches, resources);
        }

        private ProgressionNodeEvaluation EvaluateNode(ProgressionNodeKey key,
            OptimizationSnapshot snapshot,
            IDictionary<ProgressionNodeKey, ProgressionWorkEstimate> estimates,
            IDictionary<ProgressionNodeKey, ProgressionNodeEvaluation> memo)
        {
            ProgressionNodeEvaluation cached;
            if (memo.TryGetValue(key, out cached)) return cached;
            var node = Node(key);
            var gateSatisfied = node.Gate.Kind != ProgressionGateKind.DependenciesOnly
                                && node.Gate.IsSatisfied(snapshot);
            if (gateSatisfied && key != ProgressionNodeKey.EndSequence)
            {
                cached = new ProgressionNodeEvaluation(key, true, true,
                    0.0, 0.0, 0.0, 0.0,
                    node.DefinitionProvenance, key);
                memo[key] = cached;
                return cached;
            }

            var dependencyMean = 0.0;
            var dependencyP90 = 0.0;
            var dependencyLower = 0.0;
            var dependencyUpper = 0.0;
            var complete = true;
            var dependenciesSatisfied = true;
            var provenance = node.DefinitionProvenance;
            var dominant = key;
            foreach (var dependency in node.Dependencies())
            {
                var value = EvaluateNode(dependency, snapshot, estimates, memo);
                complete &= value.ModelComplete;
                dependenciesSatisfied &= value.GateSatisfied;
                provenance = Weakest(provenance, value.Provenance);
                if (value.MeanSeconds >= dependencyMean)
                {
                    dependencyMean = value.MeanSeconds;
                    dominant = value.DominantBranch;
                }
                dependencyP90 = Math.Max(dependencyP90, value.P90Seconds);
                dependencyLower = Math.Max(dependencyLower, value.LowerBoundSeconds);
                dependencyUpper = Math.Max(dependencyUpper, value.UpperBoundSeconds);
            }

            var ownMean = 0.0;
            var ownP90 = 0.0;
            var ownLower = 0.0;
            var ownUpper = 0.0;
            if (node.RequiresOwnEstimate && !gateSatisfied)
            {
                ProgressionWorkEstimate own;
                if (!estimates.TryGetValue(key, out own))
                {
                    complete = false;
                    provenance = ProgressionEstimateProvenance.Unknown;
                }
                else
                {
                    ownMean = own.MeanSeconds;
                    ownP90 = own.P90Seconds;
                    ownLower = own.LowerBoundSeconds;
                    ownUpper = own.UpperBoundSeconds;
                    provenance = Weakest(provenance, own.Provenance);
                }
            }
            var mean = dependencyMean + ownMean;
            var p90 = dependencyP90 + ownP90;
            var lower = dependencyLower + ownLower;
            var upper = dependencyUpper + ownUpper;
            var effectiveGateSatisfied = node.Gate.Kind
                                         == ProgressionGateKind.DependenciesOnly
                ? dependenciesSatisfied : gateSatisfied && dependenciesSatisfied;
            cached = new ProgressionNodeEvaluation(key, effectiveGateSatisfied, complete,
                mean, p90, lower, upper, provenance, dominant);
            memo[key] = cached;
            return cached;
        }

        private ProgressionSharedResourceSummary[] BuildResourceSummaries(
            OptimizationSnapshot snapshot, ProgressionNodeKey critical)
        {
            var counts = new Dictionary<ProgressionSharedResourceKind, int>();
            var exclusive = new Dictionary<ProgressionSharedResourceKind, int>();
            var units = new Dictionary<ProgressionSharedResourceKind, double>();
            var criticalResources = new HashSet<ProgressionSharedResourceKind>();
            var criticalPath = new HashSet<ProgressionNodeKey>();
            AddDependencyClosure(critical, criticalPath);
            foreach (var node in _nodes)
            {
                if (node.Gate.Kind != ProgressionGateKind.DependenciesOnly
                    && node.Gate.IsSatisfied(snapshot)) continue;
                foreach (var claim in node.Resources())
                {
                    if (!counts.ContainsKey(claim.Resource))
                    {
                        counts[claim.Resource] = 0;
                        exclusive[claim.Resource] = 0;
                        units[claim.Resource] = 0.0;
                    }
                    counts[claim.Resource]++;
                    if (claim.Exclusive) exclusive[claim.Resource]++;
                    units[claim.Resource] += claim.Units;
                    if (criticalPath.Contains(node.Key))
                        criticalResources.Add(claim.Resource);
                }
            }
            return counts.Keys.OrderBy(x => (int)x).Select(x =>
                new ProgressionSharedResourceSummary(x, counts[x], exclusive[x], units[x],
                    criticalResources.Contains(x))).ToArray();
        }

        private void AddDependencyClosure(ProgressionNodeKey key,
            HashSet<ProgressionNodeKey> result)
        {
            if (!result.Add(key)) return;
            foreach (var dependency in Node(key).Dependencies())
                AddDependencyClosure(dependency, result);
        }

        private void ValidateAcyclic()
        {
            var state = new Dictionary<ProgressionNodeKey, int>();
            foreach (var node in _nodes) Visit(node.Key, state);
        }

        private void Visit(ProgressionNodeKey key,
            IDictionary<ProgressionNodeKey, int> state)
        {
            int value;
            if (state.TryGetValue(key, out value))
            {
                if (value == 1) throw new ArgumentException("progression graph contains a cycle");
                if (value == 2) return;
            }
            state[key] = 1;
            foreach (var dependency in Node(key).Dependencies()) Visit(dependency, state);
            state[key] = 2;
        }

        private static List<ProgressionNode> BuildTerminalNodes()
        {
            var nodes = new List<ProgressionNode>();
            var itemNodes = EndItemNodes();
            var challengeNodes = ChallengeNodes();
            nodes.Add(N(ProgressionNodeKey.Terminal, ProgressionNodeKind.Composite,
                ProgressionGate.DependenciesOnly(), false,
                ProgressionEstimateProvenance.ObjectiveConfigured,
                new[] {ProgressionNodeKey.EndSequence, ProgressionNodeKey.RequiredChallenges},
                new[] {new ProgressionReward(ProgressionRewardKind.TerminalObjective)}));
            nodes.Add(N(ProgressionNodeKey.RequiredChallenges, ProgressionNodeKind.Composite,
                ProgressionGate.DependenciesOnly(), false,
                ProgressionEstimateProvenance.ObjectiveConfigured,
                challengeNodes, null));
            nodes.Add(N(ProgressionNodeKey.EndSequence, ProgressionNodeKind.FinalTransaction,
                ProgressionGate.EndSequenceVerified(), true,
                ProgressionEstimateProvenance.SourceKnown,
                itemNodes.Concat(new[] {ProgressionNodeKey.FinalInventoryLayoutCapacity,
                    ProgressionNodeKey.SadisticBoss300,
                    ProgressionNodeKey.Titan13Defeated}).ToArray(),
                new[] {new ProgressionReward(ProgressionRewardKind.EndSequence)},
                R(ProgressionSharedResourceKind.OrdinaryInventoryCapacity, true)));

            nodes.Add(N(ProgressionNodeKey.SadisticDifficulty,
                ProgressionNodeKind.PersistentGate,
                ProgressionGate.DifficultyAtLeast(OptimizationDifficulty.Sadistic), true,
                ProgressionEstimateProvenance.SourceKnown, null,
                Reward(ProgressionRewardKind.PersistentGate),
                R(ProgressionSharedResourceKind.ResetBoundary, true)));
            AddBoss(nodes, ProgressionNodeKey.SadisticBoss225, 225,
                ProgressionNodeKey.SadisticDifficulty);
            AddBoss(nodes, ProgressionNodeKey.SadisticBoss248, 248,
                ProgressionNodeKey.SadisticBoss225);
            AddBoss(nodes, ProgressionNodeKey.SadisticBoss295, 295,
                ProgressionNodeKey.SadisticBoss248);
            AddBoss(nodes, ProgressionNodeKey.SadisticBoss300, 300,
                ProgressionNodeKey.SadisticBoss295);
            nodes.Add(N(ProgressionNodeKey.Titan13Defeated,
                ProgressionNodeKind.PersistentGate,
                ProgressionGate.FactAtLeast(OptimizationFactKey.Titan13Defeated, 1.0), true,
                ProgressionEstimateProvenance.SourceKnown,
                new[] {ProgressionNodeKey.SadisticBoss295},
                Reward(ProgressionRewardKind.PersistentGate),
                R(ProgressionSharedResourceKind.AdventureMode, true,
                    ProgressionSharedResourceKind.PhysicalLoadout,
                    ProgressionSharedResourceKind.OnlineTime)));

            AddFactNode(nodes, ProgressionNodeKey.Move69Unlocked,
                OptimizationFactKey.Move69Unlocked, 1.0,
                ProgressionNodeKind.LongRunningBranch,
                new[] {ProgressionNodeKey.SadisticDifficulty},
                R(ProgressionSharedResourceKind.AdventureMode, true,
                    ProgressionSharedResourceKind.OnlineTime));
            AddFactNode(nodes, ProgressionNodeKey.Perk231Source,
                OptimizationFactKey.Perk231Level, 1.0,
                ProgressionNodeKind.LongRunningBranch,
                new[] {ProgressionNodeKey.SadisticDifficulty},
                R(ProgressionSharedResourceKind.PerkPoints, false));
            AddFactNode(nodes, ProgressionNodeKey.Quirk176Source,
                OptimizationFactKey.Quirk176Level, 1.0,
                ProgressionNodeKind.LongRunningBranch,
                new[] {ProgressionNodeKey.SadisticDifficulty},
                R(ProgressionSharedResourceKind.QuirkPoints, false));
            AddFactNode(nodes, ProgressionNodeKey.HacksZeroThroughFourteenCapped,
                OptimizationFactKey.HacksZeroThroughFourteenCapped, 1.0,
                ProgressionNodeKind.LongRunningBranch,
                new[] {ProgressionNodeKey.SadisticDifficulty},
                R(ProgressionSharedResourceKind.ResourceThree, false));
            AddFactNode(nodes, ProgressionNodeKey.EndHackComplete,
                OptimizationFactKey.EndHackLevel, 1.0,
                ProgressionNodeKind.LongRunningBranch,
                new[] {ProgressionNodeKey.HacksZeroThroughFourteenCapped},
                R(ProgressionSharedResourceKind.ResourceThree, false,
                    ProgressionSharedResourceKind.OnlineTime));
            AddFactNode(nodes, ProgressionNodeKey.Wish203Complete,
                OptimizationFactKey.Wish203Level, 1.0,
                ProgressionNodeKind.LongRunningBranch,
                new[] {ProgressionNodeKey.SadisticDifficulty},
                R(ProgressionSharedResourceKind.Energy, false,
                    ProgressionSharedResourceKind.Magic,
                    ProgressionSharedResourceKind.ResourceThree));
            AddFactNode(nodes, ProgressionNodeKey.ItopodFloor1450,
                OptimizationFactKey.ItopodHighestFloor, 1450.0,
                ProgressionNodeKind.LongRunningBranch,
                new[] {ProgressionNodeKey.SadisticDifficulty},
                R(ProgressionSharedResourceKind.AdventureMode, true));
            AddFactNode(nodes, ProgressionNodeKey.EndCardHeld,
                OptimizationFactKey.HeldEndCards, 1.0,
                ProgressionNodeKind.StochasticOpportunity,
                new[] {ProgressionNodeKey.SadisticDifficulty,
                    ProgressionNodeKey.EndCardDeckCapacity},
                R(ProgressionSharedResourceKind.CardDeckCapacity, false));
            AddFactNode(nodes, ProgressionNodeKey.EndBloodReady,
                OptimizationFactKey.Blood, MechanicsEndgame.EndBloodCost,
                ProgressionNodeKind.LongRunningBranch,
                new[] {ProgressionNodeKey.SadisticDifficulty},
                R(ProgressionSharedResourceKind.Blood, true,
                    ProgressionSharedResourceKind.ResetBoundary));
            AddFactNode(nodes, ProgressionNodeKey.EndFiltersClear,
                OptimizationFactKey.EndFiltersClear, 1.0,
                ProgressionNodeKind.PersistentGate, null, null);
            AddFactNode(nodes, ProgressionNodeKey.OneUsableInventorySlot,
                OptimizationFactKey.UsableInventoryFreeSlots, 1.0,
                ProgressionNodeKind.PersistentGate, null,
                R(ProgressionSharedResourceKind.OrdinaryInventoryCapacity, false));
            AddFactNode(nodes, ProgressionNodeKey.TwoUsableInventorySlots,
                OptimizationFactKey.UsableInventoryFreeSlots, 2.0,
                ProgressionNodeKind.PersistentGate, null,
                R(ProgressionSharedResourceKind.OrdinaryInventoryCapacity, false));
            AddFactNode(nodes, ProgressionNodeKey.FinalInventoryLayoutCapacity,
                OptimizationFactKey.OrdinaryInventoryCurrentSpaces, 40.0,
                ProgressionNodeKind.PersistentGate, null,
                R(ProgressionSharedResourceKind.OrdinaryInventoryCapacity, true));
            AddFactNode(nodes, ProgressionNodeKey.Titan12CapacityFor483,
                OptimizationFactKey.UsableInventoryFreeSlots, 11.0,
                ProgressionNodeKind.PersistentGate, null,
                R(ProgressionSharedResourceKind.OrdinaryInventoryCapacity, false));
            AddFactNode(nodes, ProgressionNodeKey.Titan12CapacityFor489,
                OptimizationFactKey.UsableInventoryFreeSlots, 14.0,
                ProgressionNodeKind.PersistentGate, null,
                R(ProgressionSharedResourceKind.OrdinaryInventoryCapacity, false));
            AddFactNode(nodes, ProgressionNodeKey.Titan12CapacityFor493,
                OptimizationFactKey.UsableInventoryFreeSlots, 16.0,
                ProgressionNodeKind.PersistentGate, null,
                R(ProgressionSharedResourceKind.OrdinaryInventoryCapacity, false));
            AddFactNode(nodes, ProgressionNodeKey.Titan12CapacityFor484,
                OptimizationFactKey.UsableInventoryFreeSlots, 18.0,
                ProgressionNodeKind.PersistentGate, null,
                R(ProgressionSharedResourceKind.OrdinaryInventoryCapacity, false));
            AddFactNode(nodes, ProgressionNodeKey.EndCardDeckCapacity,
                OptimizationFactKey.DeckFreeSlots, 2.0,
                ProgressionNodeKind.PersistentGate, null,
                R(ProgressionSharedResourceKind.CardDeckCapacity, false));
            nodes.Add(N(ProgressionNodeKey.EndCardMayoReserve,
                ProgressionNodeKind.LongRunningBranch,
                ProgressionGate.FactsAtLeast(
                    new[] {OptimizationFactKey.MayoZero, OptimizationFactKey.MayoOne,
                        OptimizationFactKey.MayoTwo, OptimizationFactKey.MayoThree,
                        OptimizationFactKey.MayoFour, OptimizationFactKey.MayoFive},
                    new[] {99.0, 99.0, 99.0, 99.0, 99.0, 99.0}), true,
                ProgressionEstimateProvenance.SourceKnown, null,
                Reward(ProgressionRewardKind.PersistentGate),
                R(ProgressionSharedResourceKind.MayoZero, false,
                    ProgressionSharedResourceKind.MayoOne,
                    ProgressionSharedResourceKind.MayoTwo,
                    ProgressionSharedResourceKind.MayoThree,
                    ProgressionSharedResourceKind.MayoFour,
                    ProgressionSharedResourceKind.MayoFive)));

            AddEndItems(nodes);
            AddChallenges(nodes);
            return nodes;
        }

        private static void AddEndItems(ICollection<ProgressionNode> nodes)
        {
            AddItem(nodes, 480, ProgressionNodeKind.LongRunningBranch, null,
                R(ProgressionSharedResourceKind.AdventureMode, true));
            AddItem(nodes, 481, ProgressionNodeKind.Materialization,
                new[] {ProgressionNodeKey.Move69Unlocked,
                    ProgressionNodeKey.OneUsableInventorySlot,
                    ProgressionNodeKey.EndFiltersClear},
                R(ProgressionSharedResourceKind.OnlineTime, false,
                    ProgressionSharedResourceKind.OrdinaryInventoryCapacity));
            AddItem(nodes, 482, ProgressionNodeKind.Materialization,
                CheckerDependencies(ProgressionNodeKey.Perk231Source),
                R(ProgressionSharedResourceKind.OrdinaryInventoryCapacity, false));
            AddT12Item(nodes, 483, ProgressionNodeKey.Titan12CapacityFor483);
            AddT12Item(nodes, 484, ProgressionNodeKey.Titan12CapacityFor484);
            AddItem(nodes, 485, ProgressionNodeKind.LongRunningBranch, null,
                R(ProgressionSharedResourceKind.AdventureMode, true));
            AddItem(nodes, 486, ProgressionNodeKind.Materialization,
                CheckerDependencies(ProgressionNodeKey.Quirk176Source),
                R(ProgressionSharedResourceKind.OrdinaryInventoryCapacity, false));
            AddItem(nodes, 487, ProgressionNodeKind.Materialization,
                CheckerDependencies(ProgressionNodeKey.SadisticBoss300),
                R(ProgressionSharedResourceKind.OrdinaryInventoryCapacity, false));
            AddItem(nodes, 488, ProgressionNodeKind.Materialization,
                CheckerDependencies(ProgressionNodeKey.EndHackComplete),
                R(ProgressionSharedResourceKind.OrdinaryInventoryCapacity, false));
            AddT12Item(nodes, 489, ProgressionNodeKey.Titan12CapacityFor489);
            AddItem(nodes, 490, ProgressionNodeKind.Materialization,
                CheckerDependencies(ProgressionNodeKey.Wish203Complete),
                R(ProgressionSharedResourceKind.OrdinaryInventoryCapacity, false));
            AddItem(nodes, 491, ProgressionNodeKind.StochasticOpportunity,
                new[] {ProgressionNodeKey.ItopodFloor1450,
                    ProgressionNodeKey.TwoUsableInventorySlots,
                    ProgressionNodeKey.EndFiltersClear},
                R(ProgressionSharedResourceKind.AdventureMode, true,
                    ProgressionSharedResourceKind.OnlineTime,
                    ProgressionSharedResourceKind.OrdinaryInventoryCapacity));
            AddItem(nodes, 492, ProgressionNodeKind.Materialization,
                new[] {ProgressionNodeKey.EndCardHeld,
                    ProgressionNodeKey.EndCardMayoReserve,
                    ProgressionNodeKey.OneUsableInventorySlot,
                    ProgressionNodeKey.EndFiltersClear},
                R(ProgressionSharedResourceKind.CardDeckCapacity, false,
                    ProgressionSharedResourceKind.OrdinaryInventoryCapacity));
            AddT12Item(nodes, 493, ProgressionNodeKey.Titan12CapacityFor493);
            AddItem(nodes, 494, ProgressionNodeKind.Materialization,
                new[] {ProgressionNodeKey.EndBloodReady,
                    ProgressionNodeKey.OneUsableInventorySlot,
                    ProgressionNodeKey.EndFiltersClear},
                R(ProgressionSharedResourceKind.Blood, true,
                    ProgressionSharedResourceKind.ResetBoundary,
                    ProgressionSharedResourceKind.OrdinaryInventoryCapacity));
            AddItem(nodes, 495, ProgressionNodeKind.Materialization,
                new[] {ProgressionNodeKey.SadisticBoss300,
                    ProgressionNodeKey.Titan13Defeated,
                    ProgressionNodeKey.OneUsableInventorySlot,
                    ProgressionNodeKey.EndFiltersClear},
                R(ProgressionSharedResourceKind.AdventureMode, true,
                    ProgressionSharedResourceKind.PhysicalLoadout,
                    ProgressionSharedResourceKind.OrdinaryInventoryCapacity));
        }

        private static void AddChallenges(ICollection<ProgressionNode> nodes)
        {
            foreach (OptimizationChallengeKind challenge in Enum.GetValues(
                typeof(OptimizationChallengeKind)))
                nodes.Add(N(NodeForChallenge(challenge), ProgressionNodeKind.Challenge,
                    ProgressionGate.ChallengeComplete(challenge), true,
                    ProgressionEstimateProvenance.ObjectiveConfigured,
                    null,
                    new[] {new ProgressionReward(
                        ProgressionRewardKind.ChallengeCompletion, -1, challenge)},
                    R(ProgressionSharedResourceKind.ResetBoundary, true,
                        ProgressionSharedResourceKind.FightBoss,
                        ProgressionSharedResourceKind.AdventureMode)));
        }

        private static void AddBoss(ICollection<ProgressionNode> nodes,
            ProgressionNodeKey key, int boss, ProgressionNodeKey dependency)
        {
            nodes.Add(N(key, ProgressionNodeKind.PersistentGate,
                ProgressionGate.FactAtLeast(OptimizationFactKey.HighestSadisticBoss, boss),
                true, ProgressionEstimateProvenance.SourceKnown,
                new[] {dependency}, Reward(ProgressionRewardKind.PersistentGate),
                R(ProgressionSharedResourceKind.FightBoss, true)));
        }

        private static void AddFactNode(ICollection<ProgressionNode> nodes,
            ProgressionNodeKey key, OptimizationFactKey fact, double threshold,
            ProgressionNodeKind kind, ProgressionNodeKey[] dependencies,
            ProgressionSharedResourceClaim[] resources)
        {
            nodes.Add(N(key, kind, ProgressionGate.FactAtLeast(fact, threshold), true,
                ProgressionEstimateProvenance.SourceKnown, dependencies,
                Reward(ProgressionRewardKind.SourceCompletion), resources));
        }

        private static void AddItem(ICollection<ProgressionNode> nodes, int itemId,
            ProgressionNodeKind kind, ProgressionNodeKey[] dependencies,
            ProgressionSharedResourceClaim[] resources)
        {
            nodes.Add(N(NodeForEndItem(itemId), kind,
                ProgressionGate.EndItemExactlyOne(itemId), true,
                ProgressionEstimateProvenance.SourceKnown, dependencies,
                new[] {new ProgressionReward(
                    ProgressionRewardKind.OrdinaryEndItem, itemId)}, resources));
        }

        private static void AddT12Item(ICollection<ProgressionNode> nodes, int itemId,
            ProgressionNodeKey capacity)
        {
            AddItem(nodes, itemId, ProgressionNodeKind.StochasticOpportunity,
                new[] {ProgressionNodeKey.SadisticBoss248, capacity,
                    ProgressionNodeKey.EndFiltersClear},
                R(ProgressionSharedResourceKind.AdventureMode, true,
                    ProgressionSharedResourceKind.PhysicalLoadout,
                    ProgressionSharedResourceKind.OnlineTime,
                    ProgressionSharedResourceKind.OrdinaryInventoryCapacity));
        }

        private static ProgressionNodeKey[] CheckerDependencies(ProgressionNodeKey source)
        {
            return new[] {source, ProgressionNodeKey.SadisticBoss225,
                ProgressionNodeKey.OneUsableInventorySlot,
                ProgressionNodeKey.EndFiltersClear};
        }

        private static ProgressionNode N(ProgressionNodeKey key, ProgressionNodeKind kind,
            ProgressionGate gate, bool estimate,
            ProgressionEstimateProvenance provenance,
            ProgressionNodeKey[] dependencies, ProgressionReward[] rewards,
            ProgressionSharedResourceClaim[] resources = null)
        {
            return new ProgressionNode(key, kind, gate, estimate, provenance,
                dependencies, rewards, resources);
        }

        private static ProgressionReward[] Reward(ProgressionRewardKind kind)
        {
            return new[] {new ProgressionReward(kind)};
        }

        private static ProgressionSharedResourceClaim[] R(
            ProgressionSharedResourceKind first, bool firstExclusive,
            params ProgressionSharedResourceKind[] rest)
        {
            var result = new List<ProgressionSharedResourceClaim>
            {
                new ProgressionSharedResourceClaim(first, 1.0, firstExclusive)
            };
            if (rest != null)
                for (var i = 0; i < rest.Length; i++)
                    result.Add(new ProgressionSharedResourceClaim(rest[i], 1.0, false));
            return result.ToArray();
        }

        private static ProgressionNodeKey NodeForEndItem(int itemId)
        {
            if (!MechanicsEndgame.IsProtectedItem(itemId))
                throw new ArgumentOutOfRangeException("itemId");
            return (ProgressionNodeKey)((int)ProgressionNodeKey.EndItem480
                                        + itemId - MechanicsEndgame.FirstEndItemId);
        }

        private static ProgressionNodeKey[] EndItemNodes()
        {
            var result = new ProgressionNodeKey[16];
            for (var i = 0; i < result.Length; i++)
                result[i] = NodeForEndItem(MechanicsEndgame.FirstEndItemId + i);
            return result;
        }

        private static ProgressionNodeKey NodeForChallenge(
            OptimizationChallengeKind challenge)
        {
            return (ProgressionNodeKey)((int)ProgressionNodeKey.ChallengeBasic
                                        + (int)challenge);
        }

        private static OptimizationChallengeKind ChallengeForNode(
            ProgressionNodeKey node)
        {
            if (!IsChallengeNode(node)) throw new ArgumentOutOfRangeException("node");
            return (OptimizationChallengeKind)((int)node
                - (int)ProgressionNodeKey.ChallengeBasic);
        }

        private static bool IsChallengeNode(ProgressionNodeKey node)
        {
            return node >= ProgressionNodeKey.ChallengeBasic
                   && node <= ProgressionNodeKey.ChallengeNoTimeMachine;
        }

        private static ProgressionNodeKey[] ChallengeNodes()
        {
            return Enum.GetValues(typeof(OptimizationChallengeKind))
                .Cast<OptimizationChallengeKind>().Select(NodeForChallenge).ToArray();
        }

        private static ProgressionNodeKey[] ParallelBranchKeys()
        {
            return EndItemNodes().Concat(ChallengeNodes()).ToArray();
        }

        private static ProgressionEstimateProvenance Weakest(
            ProgressionEstimateProvenance left,
            ProgressionEstimateProvenance right)
        {
            if (left == ProgressionEstimateProvenance.Unknown
                || right == ProgressionEstimateProvenance.Unknown)
                return ProgressionEstimateProvenance.Unknown;
            if (left == ProgressionEstimateProvenance.Heuristic
                || right == ProgressionEstimateProvenance.Heuristic)
                return ProgressionEstimateProvenance.Heuristic;
            if (left == ProgressionEstimateProvenance.Empirical
                || right == ProgressionEstimateProvenance.Empirical)
                return ProgressionEstimateProvenance.Empirical;
            if (left == ProgressionEstimateProvenance.DerivedFromSource
                || right == ProgressionEstimateProvenance.DerivedFromSource)
                return ProgressionEstimateProvenance.DerivedFromSource;
            if (left == ProgressionEstimateProvenance.ObjectiveConfigured)
                return right;
            if (right == ProgressionEstimateProvenance.ObjectiveConfigured)
                return left;
            return ProgressionEstimateProvenance.SourceKnown;
        }
    }
}
