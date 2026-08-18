using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

/*
FILE PURPOSE

Purpose: GlobalEventScheduler is the pure, bounded, shadow-only composition layer for task 28. It
compares finite continuation, reset, challenge, purchase, allocation, Adventure, Titan, collection,
Card/Cooking, ITOPOD, and terminal routes in seconds-to-terminal without granting mutation authority.

Mechanism: Subsystem owners expose source-ordered behavior through IPlannerTransitionAdapter. The
scheduler validates typed action bundles, rejects mutually exclusive modes, evaluates all declared
resource credits/debits chronologically, deduplicates simultaneous typed events, applies one pure
transition batch, and searches immutable successors with a bounded best-first frontier, dominance
pruning, and rollout estimates. Task 27's terminal DAG is available through
ProgressionGraphRolloutPolicy; no strategy is recovered from labels or IDs.

Inputs and outputs: Inputs are an immutable PlannerSearchState bound to an OptimizationSnapshot,
pure transition adapters, a rollout policy, and explicit node/depth/time budgets. Output is one
ScheduleDecision containing a finite next event or a typed outside-model blocker, terminal
mean/p50/p90/lower/upper estimates, lower-bound gap, runner-up regret, expected delta, and search
diagnostics.

Invariants and safety: Authority is always ShadowOnly and ScheduleDecision.CanExecute is always
false. An irreversible first action cannot be selected from an incomplete rollout. Gold and every
other declared currency must remain non-negative at every timestamp. One Adventure mode, physical
loadout owner, OS, Digger set, and irreversible boundary may exist per bundle. A material command
is last, forcing replan after its successor. Unknown time is never represented as zero.

Extension points and non-goals: Task 29 supplies read-only live capture and adapters over the landed
task 10/12/15-27 mechanics. This file does not call Character, controllers, reflection, managers,
mutation roots, saves, runtime files, or END execution. Search is intentionally bounded and may
return a named model blocker or reversible rollout fallback rather than inventing precision.
*/
namespace NGUInjector.Autopilot
{
    internal enum PlannerAuthority
    {
        ShadowOnly
    }

    internal enum PlannerAdapterKind
    {
        FightBoss,
        Rebirth,
        Titan,
        Challenge,
        Difficulty,
        Allocation,
        GoldBlood,
        PermanentProgress,
        PermanentPurchase,
        QuestYggdrasil,
        Collection,
        Stochastic,
        CardCooking,
        ItopodMove,
        Terminal,
        Fixture
    }

    internal enum PlannerActionKind
    {
        Continue,
        OrdinaryReset,
        EnterChallenge,
        CompleteChallenge,
        ChangeDifficulty,
        FightBoss,
        FightTitan,
        AdventureMode,
        AllocateResources,
        SpendGold,
        BuyPermanent,
        QuestOrFruit,
        Collect,
        CardOrCooking,
        ItopodOrMove,
        StartEndSequence,
        Measure,
        SaveCurrency,
        Fixture
    }

    internal enum PlannerCommandKind
    {
        Wait,
        Observe,
        SetAdventureMode,
        SetPhysicalLoadout,
        SetWandoos,
        SetDiggers,
        SetAllocation,
        SpendResource,
        InvestResetLocal,
        BuyPermanent,
        FightBoss,
        FightTitan,
        EnterChallenge,
        CompleteChallenge,
        OrdinaryReset,
        ChangeDifficulty,
        StartEndSequence,
        Fixture
    }

    internal enum PlannerEventKind
    {
        Timer,
        FightBossDefeat,
        AdventureKill,
        Drop,
        TitanReady,
        TitanDefeat,
        ResourceAffordable,
        ResourceCompletion,
        PermanentPurchase,
        Quest,
        Fruit,
        Card,
        Cooking,
        Itopod,
        Challenge,
        Difficulty,
        Rebirth,
        EndDependency,
        Terminal,
        Observation,
        Fixture
    }

    internal enum PlannerTransitionKind
    {
        Continue,
        DurableReward,
        OrdinaryReset,
        ChallengeEntry,
        ChallengeCompletion,
        DifficultyChange,
        Purchase,
        ResourceCompletion,
        Terminal,
        Observation,
        Fixture
    }

    internal enum PlannerModeDimension
    {
        Adventure,
        PhysicalLoadoutOwner,
        Wandoos,
        DiggerSet,
        ResetBoundary
    }

    internal enum PlannerResourceKind
    {
        Gold,
        Blood,
        Energy,
        Magic,
        ResourceThree,
        Experience,
        AdventurePoints,
        PerkPoints,
        QuirkPoints,
        DiggerGps,
        OrdinaryInventorySlots,
        CardDeckSlots,
        MayoZero,
        MayoOne,
        MayoTwo,
        MayoThree,
        MayoFour,
        MayoFive
    }

    internal enum PlannerMetricKind
    {
        DurableProgress,
        PersistentPower,
        ResetLocalProgress,
        InventorySlack,
        RiskMargin
    }

    internal enum PlannerDeltaKind
    {
        DurableProgress,
        PersistentProgress,
        ResetLocalProgress,
        ResourceBalance,
        ChallengeCompletion,
        EndItem,
        Mode,
        Observation,
        Fixture
    }

    internal enum PlannerBlockerKind
    {
        None,
        NoActions,
        NoFiniteNextEvent,
        DuplicateAdapter,
        MissingAdapter,
        IncompatibleModes,
        ConflictingIrreversibleBoundaries,
        MaterialCommandNotLast,
        MissingNamedPayoff,
        ChronologicalResourceViolation,
        ResourceEventBeyondNextEvent,
        TransitionRejected,
        UnknownIrreversibleModel,
        MissingTerminalProjection,
        TerminalModelIncomplete,
        DepthBudgetExhausted,
        NodeBudgetExhausted,
        TimeBudgetExhausted,
        OutsideModel
    }

    internal enum ScheduleDecisionStatus
    {
        Terminal,
        ShadowPlan,
        RolloutFallback,
        Blocked
    }

    internal struct PlannerActionKey : IEquatable<PlannerActionKey>,
        IComparable<PlannerActionKey>
    {
        internal readonly PlannerAdapterKind Adapter;
        internal readonly PlannerActionKind Kind;
        internal readonly int LocalId;

        internal PlannerActionKey(PlannerAdapterKind adapter,
            PlannerActionKind kind, int localId)
        {
            if (!Enum.IsDefined(typeof(PlannerAdapterKind), adapter))
                throw new ArgumentOutOfRangeException("adapter");
            if (!Enum.IsDefined(typeof(PlannerActionKind), kind))
                throw new ArgumentOutOfRangeException("kind");
            if (localId < 0) throw new ArgumentOutOfRangeException("localId");
            Adapter = adapter;
            Kind = kind;
            LocalId = localId;
        }

        public bool Equals(PlannerActionKey other)
        {
            return Adapter == other.Adapter && Kind == other.Kind
                   && LocalId == other.LocalId;
        }

        public override bool Equals(object obj)
        {
            return obj is PlannerActionKey && Equals((PlannerActionKey)obj);
        }

        public override int GetHashCode()
        {
            return ((int)Adapter * 397) ^ ((int)Kind * 31) ^ LocalId;
        }

        public int CompareTo(PlannerActionKey other)
        {
            var value = ((int)Adapter).CompareTo((int)other.Adapter);
            if (value != 0) return value;
            value = ((int)Kind).CompareTo((int)other.Kind);
            return value != 0 ? value : LocalId.CompareTo(other.LocalId);
        }

        public override string ToString()
        {
            return Adapter + "/" + Kind + "/" + LocalId;
        }
    }

    internal struct PlannerEventKey : IEquatable<PlannerEventKey>,
        IComparable<PlannerEventKey>
    {
        internal readonly PlannerEventKind Kind;
        internal readonly int LocalId;

        internal PlannerEventKey(PlannerEventKind kind, int localId)
        {
            if (!Enum.IsDefined(typeof(PlannerEventKind), kind))
                throw new ArgumentOutOfRangeException("kind");
            if (localId < 0) throw new ArgumentOutOfRangeException("localId");
            Kind = kind;
            LocalId = localId;
        }

        public bool Equals(PlannerEventKey other)
        {
            return Kind == other.Kind && LocalId == other.LocalId;
        }

        public override bool Equals(object obj)
        {
            return obj is PlannerEventKey && Equals((PlannerEventKey)obj);
        }

        public override int GetHashCode() { return ((int)Kind * 397) ^ LocalId; }

        public int CompareTo(PlannerEventKey other)
        {
            var value = ((int)Kind).CompareTo((int)other.Kind);
            return value != 0 ? value : LocalId.CompareTo(other.LocalId);
        }

        public override string ToString() { return Kind + "/" + LocalId; }
    }

    internal sealed class PlannerRouteEstimate
    {
        internal readonly double MeanSeconds;
        internal readonly double P50Seconds;
        internal readonly double P90Seconds;
        internal readonly double LowerBoundSeconds;
        internal readonly double UpperBoundSeconds;
        internal readonly ProgressionEstimateProvenance Provenance;
        internal readonly int SampleCount;
        internal readonly double Confidence;
        internal readonly bool ModelComplete;

        internal PlannerRouteEstimate(double meanSeconds, double p50Seconds,
            double p90Seconds, double lowerBoundSeconds, double upperBoundSeconds,
            ProgressionEstimateProvenance provenance, bool modelComplete,
            int sampleCount = 0, double confidence = 1.0)
        {
            if (!Enum.IsDefined(typeof(ProgressionEstimateProvenance), provenance))
                throw new ArgumentOutOfRangeException("provenance");
            if (sampleCount < 0) throw new ArgumentOutOfRangeException("sampleCount");
            if (!FiniteUnit(confidence)) throw new ArgumentOutOfRangeException("confidence");
            if (modelComplete)
            {
                if (!FiniteNonNegative(meanSeconds) || !FiniteNonNegative(p50Seconds)
                    || !FiniteNonNegative(p90Seconds)
                    || !FiniteNonNegative(lowerBoundSeconds)
                    || !FiniteNonNegative(upperBoundSeconds)
                    || lowerBoundSeconds > meanSeconds || upperBoundSeconds < meanSeconds
                    || upperBoundSeconds < p50Seconds || upperBoundSeconds < p90Seconds
                    || provenance == ProgressionEstimateProvenance.Unknown)
                    throw new ArgumentOutOfRangeException("meanSeconds");
                if (provenance == ProgressionEstimateProvenance.Empirical
                    && (sampleCount <= 0 || confidence <= 0.0))
                    throw new ArgumentException(
                        "empirical estimates require positive samples and confidence");
            }
            else if (meanSeconds != -1.0 || p50Seconds != -1.0
                     || p90Seconds != -1.0 || upperBoundSeconds != -1.0
                     || !FiniteNonNegative(lowerBoundSeconds))
                throw new ArgumentException(
                    "an incomplete estimate uses -1 for unknown statistics and a typed lower bound");
            MeanSeconds = meanSeconds;
            P50Seconds = p50Seconds;
            P90Seconds = p90Seconds;
            LowerBoundSeconds = lowerBoundSeconds;
            UpperBoundSeconds = upperBoundSeconds;
            Provenance = modelComplete ? provenance : ProgressionEstimateProvenance.Unknown;
            SampleCount = sampleCount;
            Confidence = confidence;
            ModelComplete = modelComplete;
        }

        internal static PlannerRouteEstimate Exact(double seconds)
        {
            return new PlannerRouteEstimate(seconds, seconds, seconds, seconds, seconds,
                ProgressionEstimateProvenance.SourceKnown, true);
        }

        internal static PlannerRouteEstimate Unavailable(double lowerBoundSeconds)
        {
            return new PlannerRouteEstimate(-1.0, -1.0, -1.0,
                Math.Max(0.0, lowerBoundSeconds), -1.0,
                ProgressionEstimateProvenance.Unknown, false, 0, 0.0);
        }

        internal static PlannerRouteEstimate Add(PlannerRouteEstimate left,
            PlannerRouteEstimate right)
        {
            if (left == null) throw new ArgumentNullException("left");
            if (right == null) throw new ArgumentNullException("right");
            var lower = left.LowerBoundSeconds + right.LowerBoundSeconds;
            if (!left.ModelComplete || !right.ModelComplete)
                return Unavailable(lower);
            var provenance = Weakest(left.Provenance, right.Provenance);
            return new PlannerRouteEstimate(left.MeanSeconds + right.MeanSeconds,
                left.P50Seconds + right.P50Seconds,
                left.P90Seconds + right.P90Seconds,
                lower, left.UpperBoundSeconds + right.UpperBoundSeconds,
                provenance, true, EmpiricalSamples(provenance, left, right),
                EmpiricalConfidence(provenance, left, right));
        }

        internal static PlannerRouteEstimate Max(IEnumerable<PlannerRouteEstimate> source)
        {
            if (source == null) throw new ArgumentNullException("source");
            var values = source.ToArray();
            if (values.Length == 0) return Exact(0.0);
            if (values.Any(x => x == null))
                throw new ArgumentException("route estimates cannot contain null", "source");
            var lower = values.Max(x => x.LowerBoundSeconds);
            if (values.Any(x => !x.ModelComplete)) return Unavailable(lower);
            var provenance = values[0].Provenance;
            for (var i = 1; i < values.Length; i++)
                provenance = Weakest(provenance, values[i].Provenance);
            return new PlannerRouteEstimate(values.Max(x => x.MeanSeconds),
                values.Max(x => x.P50Seconds), values.Max(x => x.P90Seconds), lower,
                values.Max(x => x.UpperBoundSeconds), provenance, true,
                EmpiricalSamples(provenance, values),
                EmpiricalConfidence(provenance, values));
        }

        private static int EmpiricalSamples(ProgressionEstimateProvenance provenance,
            params PlannerRouteEstimate[] values)
        {
            if (provenance != ProgressionEstimateProvenance.Empirical) return 0;
            return values.Where(x => x.Provenance
                                     == ProgressionEstimateProvenance.Empirical)
                .Min(x => x.SampleCount);
        }

        private static double EmpiricalConfidence(
            ProgressionEstimateProvenance provenance,
            params PlannerRouteEstimate[] values)
        {
            if (provenance != ProgressionEstimateProvenance.Empirical) return 0.0;
            return values.Where(x => x.Provenance
                                     == ProgressionEstimateProvenance.Empirical)
                .Min(x => x.Confidence);
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
            if (left == ProgressionEstimateProvenance.ObjectiveConfigured) return right;
            if (right == ProgressionEstimateProvenance.ObjectiveConfigured) return left;
            return ProgressionEstimateProvenance.SourceKnown;
        }

        private static bool FiniteNonNegative(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0.0;
        }

        private static bool FiniteUnit(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value)
                   && value >= 0.0 && value <= 1.0;
        }
    }

    internal sealed class PlannerResourceAmount
    {
        internal readonly PlannerResourceKind Kind;
        internal readonly double Balance;
        internal readonly double ProductionPerSecond;

        internal PlannerResourceAmount(PlannerResourceKind kind, double balance,
            double productionPerSecond)
        {
            if (!Enum.IsDefined(typeof(PlannerResourceKind), kind))
                throw new ArgumentOutOfRangeException("kind");
            if (!FiniteNonNegative(balance) || !FiniteNonNegative(productionPerSecond))
                throw new ArgumentOutOfRangeException("balance");
            Kind = kind;
            Balance = balance;
            ProductionPerSecond = productionPerSecond;
        }

        private static bool FiniteNonNegative(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0.0;
        }
    }

    internal sealed class PlannerMetricValue
    {
        internal readonly PlannerMetricKind Kind;
        internal readonly double Value;

        internal PlannerMetricValue(PlannerMetricKind kind, double value)
        {
            if (!Enum.IsDefined(typeof(PlannerMetricKind), kind))
                throw new ArgumentOutOfRangeException("kind");
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException("value");
            Kind = kind;
            Value = value;
        }
    }

    internal sealed class PlannerModeAssignment
    {
        internal readonly PlannerModeDimension Dimension;
        internal readonly int Value;

        internal PlannerModeAssignment(PlannerModeDimension dimension, int value)
        {
            if (!Enum.IsDefined(typeof(PlannerModeDimension), dimension))
                throw new ArgumentOutOfRangeException("dimension");
            if (value < 0) throw new ArgumentOutOfRangeException("value");
            Dimension = dimension;
            Value = value;
        }
    }

    internal sealed class PlannerSearchState
    {
        private readonly double[] _balances;
        private readonly double[] _production;
        private readonly double[] _metrics;
        private readonly bool[] _metricSeen;
        private readonly PlannerModeAssignment[] _modes;
        private readonly ProgressionWorkEstimate[] _terminalEstimates;

        internal readonly string StateKey;
        internal readonly string DurableSignature;
        internal readonly string DiscontinuitySignature;
        internal readonly double ElapsedRunSeconds;
        internal readonly bool TerminalFlag;
        internal readonly OptimizationSnapshot Projection;

        internal PlannerSearchState(string stateKey, string durableSignature,
            string discontinuitySignature, double elapsedRunSeconds, bool terminalFlag,
            OptimizationSnapshot projection, IEnumerable<PlannerResourceAmount> resources,
            IEnumerable<PlannerMetricValue> metrics,
            IEnumerable<PlannerModeAssignment> modes,
            IEnumerable<ProgressionWorkEstimate> terminalEstimates = null)
        {
            if (string.IsNullOrEmpty(stateKey))
                throw new ArgumentException("state key is required", "stateKey");
            if (string.IsNullOrEmpty(durableSignature))
                throw new ArgumentException("durable signature is required", "durableSignature");
            if (string.IsNullOrEmpty(discontinuitySignature))
                throw new ArgumentException("discontinuity signature is required",
                    "discontinuitySignature");
            if (double.IsNaN(elapsedRunSeconds) || double.IsInfinity(elapsedRunSeconds)
                || elapsedRunSeconds < 0.0)
                throw new ArgumentOutOfRangeException("elapsedRunSeconds");
            StateKey = stateKey;
            DurableSignature = durableSignature;
            DiscontinuitySignature = discontinuitySignature;
            ElapsedRunSeconds = elapsedRunSeconds;
            TerminalFlag = terminalFlag;
            Projection = projection;

            var resourceCount = Enum.GetValues(typeof(PlannerResourceKind)).Length;
            _balances = new double[resourceCount];
            _production = new double[resourceCount];
            var resourceSeen = new bool[resourceCount];
            foreach (var item in resources ?? Enumerable.Empty<PlannerResourceAmount>())
            {
                if (item == null || resourceSeen[(int)item.Kind])
                    throw new ArgumentException("resource records must be non-null and unique");
                resourceSeen[(int)item.Kind] = true;
                _balances[(int)item.Kind] = item.Balance;
                _production[(int)item.Kind] = item.ProductionPerSecond;
            }

            var metricCount = Enum.GetValues(typeof(PlannerMetricKind)).Length;
            _metrics = new double[metricCount];
            _metricSeen = new bool[metricCount];
            foreach (var item in metrics ?? Enumerable.Empty<PlannerMetricValue>())
            {
                if (item == null || _metricSeen[(int)item.Kind])
                    throw new ArgumentException("metric records must be non-null and unique");
                _metricSeen[(int)item.Kind] = true;
                _metrics[(int)item.Kind] = item.Value;
            }

            var modeList = (modes ?? Enumerable.Empty<PlannerModeAssignment>()).ToArray();
            if (modeList.Any(x => x == null)
                || modeList.GroupBy(x => x.Dimension).Any(x => x.Count() > 1))
                throw new ArgumentException("state modes must be non-null and unique by dimension");
            _modes = modeList.OrderBy(x => (int)x.Dimension).ToArray();
            _terminalEstimates = (terminalEstimates
                                  ?? Enumerable.Empty<ProgressionWorkEstimate>()).ToArray();
            if (_terminalEstimates.Any(x => x == null))
                throw new ArgumentException("terminal estimates cannot contain null");
        }

        internal double Balance(PlannerResourceKind kind)
        {
            ValidateResource(kind);
            return _balances[(int)kind];
        }

        internal double Production(PlannerResourceKind kind)
        {
            ValidateResource(kind);
            return _production[(int)kind];
        }

        internal double Metric(PlannerMetricKind kind)
        {
            if (!Enum.IsDefined(typeof(PlannerMetricKind), kind))
                throw new ArgumentOutOfRangeException("kind");
            return _metrics[(int)kind];
        }

        internal bool HasMetric(PlannerMetricKind kind)
        {
            if (!Enum.IsDefined(typeof(PlannerMetricKind), kind))
                throw new ArgumentOutOfRangeException("kind");
            return _metricSeen[(int)kind];
        }

        internal PlannerResourceAmount[] Resources()
        {
            var result = new PlannerResourceAmount[_balances.Length];
            for (var i = 0; i < result.Length; i++)
                result[i] = new PlannerResourceAmount((PlannerResourceKind)i,
                    _balances[i], _production[i]);
            return result;
        }

        internal PlannerMetricValue[] Metrics()
        {
            var result = new List<PlannerMetricValue>();
            for (var i = 0; i < _metrics.Length; i++)
                if (_metricSeen[i])
                    result.Add(new PlannerMetricValue((PlannerMetricKind)i, _metrics[i]));
            return result.ToArray();
        }

        internal PlannerModeAssignment[] Modes()
        {
            return (PlannerModeAssignment[])_modes.Clone();
        }

        internal ProgressionWorkEstimate[] TerminalEstimates()
        {
            return (ProgressionWorkEstimate[])_terminalEstimates.Clone();
        }

        internal PlannerSearchState WithResources(
            IEnumerable<PlannerResourceAmount> resources)
        {
            return new PlannerSearchState(StateKey, DurableSignature,
                DiscontinuitySignature, ElapsedRunSeconds, TerminalFlag, Projection,
                resources, Metrics(), Modes(), TerminalEstimates());
        }

        private static void ValidateResource(PlannerResourceKind kind)
        {
            if (!Enum.IsDefined(typeof(PlannerResourceKind), kind))
                throw new ArgumentOutOfRangeException("kind");
        }
    }

    internal sealed class PlannerModeClaim
    {
        internal readonly PlannerModeDimension Dimension;
        internal readonly int Value;

        internal PlannerModeClaim(PlannerModeDimension dimension, int value)
        {
            if (!Enum.IsDefined(typeof(PlannerModeDimension), dimension))
                throw new ArgumentOutOfRangeException("dimension");
            if (value < 0) throw new ArgumentOutOfRangeException("value");
            Dimension = dimension;
            Value = value;
        }
    }

    internal sealed class PlannerCommand
    {
        internal readonly PlannerCommandKind Kind;
        internal readonly int LocalId;
        internal readonly int SourceOrder;
        internal readonly bool MaterialStateDelta;

        internal PlannerCommand(PlannerCommandKind kind, int localId,
            int sourceOrder, bool materialStateDelta)
        {
            if (!Enum.IsDefined(typeof(PlannerCommandKind), kind))
                throw new ArgumentOutOfRangeException("kind");
            if (localId < 0) throw new ArgumentOutOfRangeException("localId");
            Kind = kind;
            LocalId = localId;
            SourceOrder = sourceOrder;
            MaterialStateDelta = materialStateDelta;
        }
    }

    internal sealed class PlannerResourceEvent
    {
        internal readonly PlannerResourceKind Resource;
        internal readonly int LocalId;
        internal readonly string StableId;
        internal readonly double Seconds;
        internal readonly int SourceOrder;
        internal readonly double RequiredBalance;
        internal readonly double Debit;
        internal readonly double Credit;
        internal readonly bool SpendAll;

        internal PlannerResourceEvent(PlannerResourceKind resource, int localId,
            string stableId, double seconds, int sourceOrder, double requiredBalance,
            double debit, double credit, bool spendAll)
        {
            if (!Enum.IsDefined(typeof(PlannerResourceKind), resource))
                throw new ArgumentOutOfRangeException("resource");
            if (localId < 0) throw new ArgumentOutOfRangeException("localId");
            if (string.IsNullOrEmpty(stableId))
                throw new ArgumentException("stable event ID is required", "stableId");
            if (!FiniteNonNegative(seconds) || !FiniteNonNegative(requiredBalance)
                || !FiniteNonNegative(debit) || !FiniteNonNegative(credit)
                || debit > requiredBalance)
                throw new ArgumentOutOfRangeException("seconds");
            Resource = resource;
            LocalId = localId;
            StableId = stableId;
            Seconds = seconds;
            SourceOrder = sourceOrder;
            RequiredBalance = requiredBalance;
            Debit = debit;
            Credit = credit;
            SpendAll = spendAll;
        }

        private static bool FiniteNonNegative(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0.0;
        }
    }

    internal sealed class PlannerActionBundle
    {
        private readonly PlannerCommand[] _commands;
        private readonly PlannerModeClaim[] _modes;
        private readonly PlannerResourceEvent[] _resourceEvents;
        internal readonly PlannerActionKey Key;
        internal readonly string StableId;
        internal readonly bool Irreversible;
        internal readonly bool InformationOnly;
        internal readonly bool RolloutFallback;
        internal readonly bool HasNamedPayoffEvent;
        internal readonly PlannerEventKey NamedPayoffEvent;

        internal PlannerActionBundle(PlannerActionKey key, string stableId,
            IEnumerable<PlannerCommand> commands,
            IEnumerable<PlannerModeClaim> modes,
            IEnumerable<PlannerResourceEvent> resourceEvents,
            bool irreversible, bool informationOnly, bool rolloutFallback,
            bool hasNamedPayoffEvent = false,
            PlannerEventKey namedPayoffEvent = default(PlannerEventKey))
        {
            if (string.IsNullOrEmpty(stableId))
                throw new ArgumentException("stable action ID is required", "stableId");
            Key = key;
            StableId = stableId;
            Irreversible = irreversible;
            InformationOnly = informationOnly;
            RolloutFallback = rolloutFallback;
            HasNamedPayoffEvent = hasNamedPayoffEvent;
            NamedPayoffEvent = namedPayoffEvent;
            _commands = (commands ?? Enumerable.Empty<PlannerCommand>())
                .OrderBy(x => x == null ? int.MaxValue : x.SourceOrder).ToArray();
            _modes = (modes ?? Enumerable.Empty<PlannerModeClaim>()).ToArray();
            _resourceEvents = (resourceEvents
                               ?? Enumerable.Empty<PlannerResourceEvent>()).ToArray();
            if (_commands.Length == 0 || _commands.Any(x => x == null)
                || _modes.Any(x => x == null) || _resourceEvents.Any(x => x == null))
                throw new ArgumentException(
                    "action commands are required and action records cannot contain null");
        }

        internal PlannerCommand[] Commands() { return (PlannerCommand[])_commands.Clone(); }
        internal PlannerModeClaim[] Modes() { return (PlannerModeClaim[])_modes.Clone(); }
        internal PlannerResourceEvent[] ResourceEvents()
        {
            return (PlannerResourceEvent[])_resourceEvents.Clone();
        }
    }

    internal sealed class PlannerEvent
    {
        internal readonly PlannerEventKey Key;
        internal readonly string StableId;
        internal readonly int SourceOrder;
        internal readonly PlannerRouteEstimate Duration;
        internal readonly bool Stochastic;
        internal readonly bool Interruptible;

        internal PlannerEvent(PlannerEventKey key, string stableId, int sourceOrder,
            PlannerRouteEstimate duration, bool stochastic, bool interruptible)
        {
            if (string.IsNullOrEmpty(stableId))
                throw new ArgumentException("stable event ID is required", "stableId");
            if (duration == null) throw new ArgumentNullException("duration");
            Key = key;
            StableId = stableId;
            SourceOrder = sourceOrder;
            Duration = duration;
            Stochastic = stochastic;
            Interruptible = interruptible;
        }
    }

    internal sealed class PlannerDeltaValue
    {
        internal readonly PlannerDeltaKind Kind;
        internal readonly int LocalId;
        internal readonly double Value;

        internal PlannerDeltaValue(PlannerDeltaKind kind, int localId, double value)
        {
            if (!Enum.IsDefined(typeof(PlannerDeltaKind), kind))
                throw new ArgumentOutOfRangeException("kind");
            if (localId < 0) throw new ArgumentOutOfRangeException("localId");
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException("value");
            Kind = kind;
            LocalId = localId;
            Value = value;
        }
    }

    internal sealed class PlannerDelta
    {
        private readonly PlannerDeltaValue[] _values;

        internal PlannerDelta(IEnumerable<PlannerDeltaValue> values)
        {
            _values = (values ?? Enumerable.Empty<PlannerDeltaValue>()).ToArray();
            if (_values.Any(x => x == null)
                || _values.GroupBy(x => new DeltaKey(x.Kind, x.LocalId))
                    .Any(x => x.Count() > 1))
                throw new ArgumentException("delta values must be non-null and uniquely typed");
        }

        internal PlannerDeltaValue[] Values()
        {
            return (PlannerDeltaValue[])_values.Clone();
        }

        internal double DistanceFrom(PlannerDelta other)
        {
            if (other == null) throw new ArgumentNullException("other");
            var values = other._values.ToDictionary(x => new DeltaKey(x.Kind, x.LocalId),
                x => x.Value);
            var distance = 0.0;
            foreach (var item in _values)
            {
                var key = new DeltaKey(item.Kind, item.LocalId);
                double value;
                distance += Math.Abs(item.Value
                                     - (values.TryGetValue(key, out value) ? value : 0.0));
                values.Remove(key);
            }
            distance += values.Values.Sum(Math.Abs);
            return distance;
        }

        private struct DeltaKey : IEquatable<DeltaKey>
        {
            internal readonly PlannerDeltaKind Kind;
            internal readonly int Id;
            internal DeltaKey(PlannerDeltaKind kind, int id) { Kind = kind; Id = id; }
            public bool Equals(DeltaKey other) { return Kind == other.Kind && Id == other.Id; }
            public override bool Equals(object obj)
            {
                return obj is DeltaKey && Equals((DeltaKey)obj);
            }
            public override int GetHashCode() { return ((int)Kind * 397) ^ Id; }
        }
    }

    internal sealed class PlannerBlocker
    {
        internal readonly PlannerBlockerKind Kind;
        internal readonly PlannerAdapterKind Adapter;
        internal readonly PlannerActionKey Action;
        internal readonly PlannerEventKey Event;
        internal readonly string Detail;

        internal PlannerBlocker(PlannerBlockerKind kind, string detail,
            PlannerAdapterKind adapter = default(PlannerAdapterKind),
            PlannerActionKey action = default(PlannerActionKey),
            PlannerEventKey plannerEvent = default(PlannerEventKey))
        {
            if (!Enum.IsDefined(typeof(PlannerBlockerKind), kind))
                throw new ArgumentOutOfRangeException("kind");
            Kind = kind;
            Adapter = adapter;
            Action = action;
            Event = plannerEvent;
            Detail = detail ?? string.Empty;
        }

        internal static PlannerBlocker None()
        {
            return new PlannerBlocker(PlannerBlockerKind.None, string.Empty);
        }
    }

    internal sealed class PlannerTransition
    {
        internal readonly PlannerTransitionKind Kind;
        internal readonly PlannerSearchState Successor;
        internal readonly PlannerDelta Delta;
        internal readonly PlannerBlocker Blocker;

        internal bool Applied { get { return Successor != null && Blocker.Kind == PlannerBlockerKind.None; } }

        internal PlannerTransition(PlannerTransitionKind kind,
            PlannerSearchState successor, PlannerDelta delta)
        {
            if (!Enum.IsDefined(typeof(PlannerTransitionKind), kind))
                throw new ArgumentOutOfRangeException("kind");
            if (successor == null) throw new ArgumentNullException("successor");
            Kind = kind;
            Successor = successor;
            Delta = delta ?? new PlannerDelta(null);
            Blocker = PlannerBlocker.None();
        }

        private PlannerTransition(PlannerBlocker blocker)
        {
            if (blocker == null || blocker.Kind == PlannerBlockerKind.None)
                throw new ArgumentException("a rejected transition needs a named blocker");
            Kind = PlannerTransitionKind.Observation;
            Successor = null;
            Delta = new PlannerDelta(null);
            Blocker = blocker;
        }

        internal static PlannerTransition Rejected(PlannerBlocker blocker)
        {
            return new PlannerTransition(blocker);
        }
    }

    internal interface IPlannerTransitionAdapter
    {
        PlannerAdapterKind Kind { get; }
        void AddActions(PlannerSearchState state, IList<PlannerActionBundle> output);
        void AddEvents(PlannerSearchState state, PlannerActionBundle action,
            IList<PlannerEvent> output);
        PlannerTransition Apply(PlannerSearchState state, PlannerActionBundle action,
            PlannerEvent[] simultaneousEvents);
    }

    internal sealed class PlannerRolloutEstimate
    {
        internal readonly bool Terminal;
        internal readonly PlannerRouteEstimate Remaining;
        internal readonly PlannerBlocker Blocker;
        internal readonly bool HasCriticalBranch;
        internal readonly ProgressionNodeKey CriticalBranch;

        internal PlannerRolloutEstimate(bool terminal, PlannerRouteEstimate remaining,
            PlannerBlocker blocker, bool hasCriticalBranch = false,
            ProgressionNodeKey criticalBranch = default(ProgressionNodeKey))
        {
            if (remaining == null) throw new ArgumentNullException("remaining");
            if (blocker == null) throw new ArgumentNullException("blocker");
            if (terminal && !remaining.ModelComplete)
                throw new ArgumentException("a terminal state needs a complete zero route");
            Terminal = terminal;
            Remaining = remaining;
            Blocker = blocker;
            HasCriticalBranch = hasCriticalBranch;
            CriticalBranch = criticalBranch;
        }
    }

    internal interface IPlannerRolloutPolicy
    {
        PlannerRolloutEstimate Evaluate(PlannerSearchState state);
    }

    internal sealed class ProgressionGraphRolloutPolicy : IPlannerRolloutPolicy
    {
        private readonly ProgressionDependencyGraph _graph;

        internal ProgressionGraphRolloutPolicy(ProgressionDependencyGraph graph)
        {
            _graph = graph ?? throw new ArgumentNullException("graph");
        }

        public PlannerRolloutEstimate Evaluate(PlannerSearchState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            if (state.Projection == null)
                return new PlannerRolloutEstimate(false,
                    PlannerRouteEstimate.Unavailable(0.0),
                    new PlannerBlocker(PlannerBlockerKind.MissingTerminalProjection,
                        "the projected successor has no task-27 optimization snapshot"));
            var estimates = state.TerminalEstimates();
            var evaluation = _graph.Evaluate(state.Projection, estimates);
            if (evaluation.Terminal.GateSatisfied)
                return new PlannerRolloutEstimate(true, PlannerRouteEstimate.Exact(0.0),
                    PlannerBlocker.None(), true, evaluation.CriticalBranch);
            if (!evaluation.Terminal.ModelComplete)
                return new PlannerRolloutEstimate(false,
                    PlannerRouteEstimate.Unavailable(
                        evaluation.Terminal.LowerBoundSeconds),
                    new PlannerBlocker(PlannerBlockerKind.TerminalModelIncomplete,
                        "task-27 terminal branch estimates are incomplete"),
                    true, evaluation.CriticalBranch);
            var empirical = estimates.Where(x => x.Provenance
                                                  == ProgressionEstimateProvenance.Empirical)
                .ToArray();
            if (evaluation.Terminal.Provenance
                    == ProgressionEstimateProvenance.Empirical
                && empirical.Length == 0)
                return new PlannerRolloutEstimate(false,
                    PlannerRouteEstimate.Unavailable(
                        evaluation.Terminal.LowerBoundSeconds),
                    new PlannerBlocker(PlannerBlockerKind.TerminalModelIncomplete,
                        "task-27 empirical terminal estimate has no sample evidence"),
                    true, evaluation.CriticalBranch);
            var route = new PlannerRouteEstimate(evaluation.Terminal.MeanSeconds,
                evaluation.Terminal.MeanSeconds, evaluation.Terminal.P90Seconds,
                evaluation.Terminal.LowerBoundSeconds,
                evaluation.Terminal.UpperBoundSeconds,
                evaluation.Terminal.Provenance, true,
                empirical.Length == 0 ? 0 : empirical.Min(x => x.SampleCount),
                empirical.Length == 0 ? 0.0 : empirical.Min(x => x.Confidence));
            return new PlannerRolloutEstimate(false, route, PlannerBlocker.None(),
                true, evaluation.CriticalBranch);
        }
    }

    internal sealed class PlannerResourceLedgerResult
    {
        private readonly PlannerResourceAmount[] _resources;
        internal readonly bool Feasible;
        internal readonly PlannerBlocker Blocker;

        internal PlannerResourceLedgerResult(bool feasible,
            IEnumerable<PlannerResourceAmount> resources, PlannerBlocker blocker)
        {
            Feasible = feasible;
            _resources = (resources ?? Enumerable.Empty<PlannerResourceAmount>()).ToArray();
            Blocker = blocker ?? PlannerBlocker.None();
        }

        internal PlannerResourceAmount[] Resources()
        {
            return (PlannerResourceAmount[])_resources.Clone();
        }
    }

    internal static class PlannerResourceLedger
    {
        internal static PlannerResourceLedgerResult Evaluate(PlannerSearchState state,
            double horizonSeconds, IEnumerable<PlannerResourceEvent> source,
            PlannerActionKey action)
        {
            if (state == null) throw new ArgumentNullException("state");
            if (double.IsNaN(horizonSeconds) || double.IsInfinity(horizonSeconds)
                || horizonSeconds < 0.0)
                throw new ArgumentOutOfRangeException("horizonSeconds");
            var events = (source ?? Enumerable.Empty<PlannerResourceEvent>()).ToArray();
            if (events.Any(x => x == null))
                throw new ArgumentException("resource events cannot contain null");
            if (events.GroupBy(x => new ResourceEventKey(x.Resource, x.LocalId))
                .Any(x => x.Count() > 1))
                return Block(state, PlannerBlockerKind.ChronologicalResourceViolation,
                    "duplicate typed resource event", action);
            var beyond = events.FirstOrDefault(x => x.Seconds > horizonSeconds + 1e-9);
            if (beyond != null)
                return Block(state, PlannerBlockerKind.ResourceEventBeyondNextEvent,
                    "resource event " + beyond.StableId
                    + " occurs after the next replan boundary", action);

            var resources = state.Resources();
            var balances = resources.Select(x => x.Balance).ToArray();
            var rates = resources.Select(x => x.ProductionPerSecond).ToArray();
            var elapsed = 0.0;
            foreach (var item in events.OrderBy(x => x.Seconds)
                         .ThenBy(x => x.SourceOrder).ThenBy(x => (int)x.Resource)
                         .ThenBy(x => x.LocalId))
            {
                Accrue(balances, rates, item.Seconds - elapsed);
                elapsed = item.Seconds;
                var index = (int)item.Resource;
                balances[index] += item.Credit;
                var tolerance = Math.Max(1e-9,
                    Math.Max(balances[index], item.RequiredBalance) * 1e-12);
                if (balances[index] + tolerance < item.RequiredBalance
                    || balances[index] + tolerance < item.Debit)
                    return Block(state,
                        PlannerBlockerKind.ChronologicalResourceViolation,
                        "insufficient " + item.Resource + " before " + item.StableId,
                        action, item.Resource, item.LocalId);
                balances[index] = item.SpendAll ? 0.0
                    : Math.Max(0.0, balances[index] - item.Debit);
            }
            Accrue(balances, rates, horizonSeconds - elapsed);
            var result = new PlannerResourceAmount[balances.Length];
            for (var i = 0; i < result.Length; i++)
                result[i] = new PlannerResourceAmount((PlannerResourceKind)i,
                    balances[i], rates[i]);
            return new PlannerResourceLedgerResult(true, result, PlannerBlocker.None());
        }

        private static void Accrue(double[] balances, double[] rates, double seconds)
        {
            if (seconds <= 0.0) return;
            for (var i = 0; i < balances.Length; i++)
                balances[i] += rates[i] * seconds;
        }

        private static PlannerResourceLedgerResult Block(PlannerSearchState state,
            PlannerBlockerKind kind, string detail, PlannerActionKey action,
            PlannerResourceKind resource = default(PlannerResourceKind), int id = 0)
        {
            return new PlannerResourceLedgerResult(false, state.Resources(),
                new PlannerBlocker(kind, detail, action.Adapter, action,
                    new PlannerEventKey(PlannerEventKind.ResourceAffordable,
                        Math.Max(0, (int)resource * 100000 + id))));
        }

        private struct ResourceEventKey : IEquatable<ResourceEventKey>
        {
            internal readonly PlannerResourceKind Resource;
            internal readonly int Id;
            internal ResourceEventKey(PlannerResourceKind resource, int id)
            {
                Resource = resource;
                Id = id;
            }
            public bool Equals(ResourceEventKey other)
            {
                return Resource == other.Resource && Id == other.Id;
            }
            public override bool Equals(object obj)
            {
                return obj is ResourceEventKey && Equals((ResourceEventKey)obj);
            }
            public override int GetHashCode() { return ((int)Resource * 397) ^ Id; }
        }
    }

    internal static class PlannerActionValidator
    {
        internal static PlannerBlocker Validate(PlannerActionBundle action)
        {
            if (action == null) throw new ArgumentNullException("action");
            var modes = action.Modes();
            if (modes.GroupBy(x => x.Dimension).Any(x => x.Count() > 1))
                return new PlannerBlocker(PlannerBlockerKind.IncompatibleModes,
                    "an action bundle assigns one mode dimension more than once",
                    action.Key.Adapter, action.Key);
            var commands = action.Commands();
            var irreversibleBoundaries = commands.Count(x =>
                x.Kind == PlannerCommandKind.OrdinaryReset
                || x.Kind == PlannerCommandKind.EnterChallenge
                || x.Kind == PlannerCommandKind.ChangeDifficulty
                || x.Kind == PlannerCommandKind.StartEndSequence);
            if (irreversibleBoundaries > 1)
                return new PlannerBlocker(
                    PlannerBlockerKind.ConflictingIrreversibleBoundaries,
                    "reset, challenge, difficulty, and END boundaries are mutually exclusive",
                    action.Key.Adapter, action.Key);
            for (var i = 0; i < commands.Length - 1; i++)
                if (commands[i].MaterialStateDelta)
                    return new PlannerBlocker(PlannerBlockerKind.MaterialCommandNotLast,
                        "a material command must stop the bundle and force replan",
                        action.Key.Adapter, action.Key);
            if (commands.Any(x => x.Kind == PlannerCommandKind.InvestResetLocal)
                && !action.HasNamedPayoffEvent)
                return new PlannerBlocker(PlannerBlockerKind.MissingNamedPayoff,
                    "reset-local investment requires a typed payoff event",
                    action.Key.Adapter, action.Key);
            return PlannerBlocker.None();
        }
    }

    internal sealed class PlannerSearchBudget
    {
        internal readonly int MaximumExpandedNodes;
        internal readonly int MaximumDepth;
        internal readonly int MaximumMilliseconds;
        internal readonly int MaximumLabelsPerSignature;
        internal readonly double HeuristicWeight;

        internal PlannerSearchBudget(int maximumExpandedNodes, int maximumDepth,
            int maximumMilliseconds, int maximumLabelsPerSignature,
            double heuristicWeight)
        {
            if (maximumExpandedNodes <= 0 || maximumDepth <= 0
                || maximumMilliseconds <= 0 || maximumLabelsPerSignature <= 0)
                throw new ArgumentOutOfRangeException("maximumExpandedNodes");
            if (double.IsNaN(heuristicWeight) || double.IsInfinity(heuristicWeight)
                || heuristicWeight < 1.0)
                throw new ArgumentOutOfRangeException("heuristicWeight");
            MaximumExpandedNodes = maximumExpandedNodes;
            MaximumDepth = maximumDepth;
            MaximumMilliseconds = maximumMilliseconds;
            MaximumLabelsPerSignature = maximumLabelsPerSignature;
            HeuristicWeight = heuristicWeight;
        }

        internal static PlannerSearchBudget Default()
        {
            return new PlannerSearchBudget(2000, 8, 20, 16, 1.1);
        }
    }

    internal sealed class ScheduleDecision
    {
        internal readonly PlannerAuthority Authority;
        internal readonly ScheduleDecisionStatus Status;
        internal readonly string PlanStateHash;
        internal readonly string ModelHash;
        internal readonly string ObjectiveHash;
        internal readonly PlannerActionBundle Selected;
        internal readonly PlannerEvent ExpectedNextEvent;
        internal readonly PlannerDelta ExpectedDelta;
        internal readonly PlannerRouteEstimate TerminalEta;
        internal readonly bool HasRunnerUp;
        internal readonly PlannerActionKey RunnerUp;
        internal readonly double RegretSeconds;
        internal readonly double LowerBoundSeconds;
        internal readonly double OptimalityGapSeconds;
        internal readonly PlannerBlocker Blocker;
        internal readonly bool UsedRolloutFallback;
        internal readonly int ExpandedNodes;
        internal readonly int GeneratedTransitions;
        internal readonly int DominancePruned;

        internal bool CanExecute { get { return false; } }

        internal ScheduleDecision(ScheduleDecisionStatus status,
            OptimizationSnapshot rootSnapshot, PlannerActionBundle selected,
            PlannerEvent expectedNextEvent, PlannerDelta expectedDelta,
            PlannerRouteEstimate terminalEta, bool hasRunnerUp,
            PlannerActionKey runnerUp, double regretSeconds,
            double lowerBoundSeconds, double optimalityGapSeconds,
            PlannerBlocker blocker, bool usedRolloutFallback,
            int expandedNodes, int generatedTransitions, int dominancePruned)
        {
            if (rootSnapshot == null) throw new ArgumentNullException("rootSnapshot");
            if (!Enum.IsDefined(typeof(ScheduleDecisionStatus), status))
                throw new ArgumentOutOfRangeException("status");
            if (terminalEta == null) throw new ArgumentNullException("terminalEta");
            if (blocker == null) throw new ArgumentNullException("blocker");
            if ((status == ScheduleDecisionStatus.ShadowPlan
                 || status == ScheduleDecisionStatus.RolloutFallback)
                && (selected == null || expectedNextEvent == null
                    || !expectedNextEvent.Duration.ModelComplete))
                throw new ArgumentException("a plan must name one finite next event");
            if (status == ScheduleDecisionStatus.Blocked
                && blocker.Kind == PlannerBlockerKind.None)
                throw new ArgumentException("a blocked plan must name its model blocker");
            Authority = PlannerAuthority.ShadowOnly;
            Status = status;
            PlanStateHash = rootSnapshot.SnapshotHash;
            ModelHash = rootSnapshot.Identity.ModelHash;
            ObjectiveHash = rootSnapshot.Identity.ObjectiveHash;
            Selected = selected;
            ExpectedNextEvent = expectedNextEvent;
            ExpectedDelta = expectedDelta ?? new PlannerDelta(null);
            TerminalEta = terminalEta;
            HasRunnerUp = hasRunnerUp;
            RunnerUp = runnerUp;
            RegretSeconds = regretSeconds;
            LowerBoundSeconds = lowerBoundSeconds;
            OptimalityGapSeconds = optimalityGapSeconds;
            Blocker = blocker;
            UsedRolloutFallback = usedRolloutFallback;
            ExpandedNodes = expandedNodes;
            GeneratedTransitions = generatedTransitions;
            DominancePruned = dominancePruned;
        }
    }

    internal sealed class GlobalEventScheduler
    {
        internal PlannerAuthority Authority { get { return PlannerAuthority.ShadowOnly; } }

        internal ScheduleDecision Plan(PlannerSearchState root,
            IEnumerable<IPlannerTransitionAdapter> adapterSource,
            IPlannerRolloutPolicy rolloutPolicy, PlannerSearchBudget budget)
        {
            if (root == null) throw new ArgumentNullException("root");
            if (root.Projection == null)
                throw new ArgumentException(
                    "the root must be bound to a task-27 optimization snapshot", "root");
            if (adapterSource == null) throw new ArgumentNullException("adapterSource");
            if (rolloutPolicy == null) throw new ArgumentNullException("rolloutPolicy");
            if (budget == null) throw new ArgumentNullException("budget");
            var adapters = adapterSource.Where(x => x != null)
                .OrderBy(x => (int)x.Kind).ToArray();
            var duplicate = adapters.GroupBy(x => x.Kind).FirstOrDefault(x => x.Count() > 1);
            if (duplicate != null)
                return Blocked(root, new PlannerBlocker(PlannerBlockerKind.DuplicateAdapter,
                    "more than one adapter owns " + duplicate.Key, duplicate.Key),
                    0, 0, 0);

            var rootRollout = rolloutPolicy.Evaluate(root);
            if (rootRollout.Terminal)
                return new ScheduleDecision(ScheduleDecisionStatus.Terminal,
                    root.Projection, null, null, new PlannerDelta(null),
                    PlannerRouteEstimate.Exact(0.0), false,
                    default(PlannerActionKey), -1.0, 0.0, 0.0,
                    PlannerBlocker.None(), false, 0, 0, 0);

            var watch = Stopwatch.StartNew();
            var heap = new SearchHeap();
            long nextSequence = 0;
            var rootNode = new SearchNode(root, PlannerRouteEstimate.Exact(0.0), 0,
                null, null, null, false, rootRollout.LowerBound(), nextSequence++);
            heap.Push(rootNode, Priority(rootNode, rootRollout, budget));
            var labels = new Dictionary<DominanceKey, List<DominanceLabel>>();
            AddLabel(labels, rootNode, budget.MaximumLabelsPerSignature);
            var candidates = new Dictionary<PlannerActionKey, RouteCandidate>();
            RouteCandidate fallback = null;
            var expanded = 0;
            var generated = 0;
            var pruned = 0;
            var sawAction = false;
            var sawFiniteEvent = false;
            var depthStopped = false;
            var lastBlocker = rootRollout.Blocker.Kind == PlannerBlockerKind.None
                ? new PlannerBlocker(PlannerBlockerKind.OutsideModel,
                    "no complete route has been produced") : rootRollout.Blocker;
            var globalLower = rootRollout.Remaining.LowerBoundSeconds;

            while (heap.Count > 0 && expanded < budget.MaximumExpandedNodes
                   && watch.ElapsedMilliseconds < budget.MaximumMilliseconds)
            {
                var node = heap.Pop();
                if (IsDominated(labels, node)) { pruned++; continue; }
                if (node.Depth >= budget.MaximumDepth)
                {
                    depthStopped = true;
                    continue;
                }
                expanded++;
                var actions = new List<PlannerActionBundle>();
                for (var i = 0; i < adapters.Length; i++)
                    adapters[i].AddActions(node.State, actions);
                actions = actions.Where(x => x != null).OrderBy(x => x.Key).ToList();
                sawAction |= actions.Count > 0;
                var seenActions = new HashSet<PlannerActionKey>();
                foreach (var action in actions)
                {
                    if (!seenActions.Add(action.Key)) continue;
                    var validation = PlannerActionValidator.Validate(action);
                    if (validation.Kind != PlannerBlockerKind.None)
                    {
                        lastBlocker = validation;
                        continue;
                    }
                    var adapter = adapters.FirstOrDefault(x => x.Kind == action.Key.Adapter);
                    if (adapter == null)
                    {
                        lastBlocker = new PlannerBlocker(PlannerBlockerKind.MissingAdapter,
                            "no transition adapter owns the action", action.Key.Adapter,
                            action.Key);
                        continue;
                    }
                    var events = new List<PlannerEvent>();
                    adapter.AddEvents(node.State, action, events);
                    var batch = EarliestBatch(events);
                    if (batch.Length == 0)
                    {
                        lastBlocker = new PlannerBlocker(
                            PlannerBlockerKind.NoFiniteNextEvent,
                            "action " + action.StableId + " has no finite next event",
                            action.Key.Adapter, action.Key);
                        continue;
                    }
                    sawFiniteEvent = true;
                    var duration = PlannerRouteEstimate.Max(batch.Select(x => x.Duration));
                    if (!duration.ModelComplete)
                    {
                        lastBlocker = new PlannerBlocker(
                            action.Irreversible
                                ? PlannerBlockerKind.UnknownIrreversibleModel
                                : PlannerBlockerKind.NoFiniteNextEvent,
                            "next-event timing is incomplete", action.Key.Adapter,
                            action.Key, batch[0].Key);
                        continue;
                    }
                    var ledger = PlannerResourceLedger.Evaluate(node.State,
                        duration.MeanSeconds, action.ResourceEvents(), action.Key);
                    if (!ledger.Feasible)
                    {
                        lastBlocker = ledger.Blocker;
                        continue;
                    }
                    var transition = adapter.Apply(node.State, action, batch);
                    if (transition == null || !transition.Applied)
                    {
                        lastBlocker = transition == null
                            ? new PlannerBlocker(PlannerBlockerKind.TransitionRejected,
                                "transition adapter returned no successor",
                                action.Key.Adapter, action.Key, batch[0].Key)
                            : transition.Blocker;
                        continue;
                    }
                    generated++;
                    var successorState = transition.Successor;
                    var successor = successorState.WithResources(ledger.Resources()
                        .Select(x => new PlannerResourceAmount(x.Kind, x.Balance,
                            successorState.Production(x.Kind))));
                    var path = PlannerRouteEstimate.Add(node.Path, duration);
                    var firstAction = node.FirstAction ?? action;
                    var firstEvent = node.FirstEvent ?? batch[0];
                    var firstDelta = node.FirstDelta ?? transition.Delta;
                    var rollout = rolloutPolicy.Evaluate(successor);
                    var lower = path.LowerBoundSeconds
                                + rollout.Remaining.LowerBoundSeconds;
                    globalLower = Math.Min(globalLower, lower);
                    var next = new SearchNode(successor, path, node.Depth + 1,
                        firstAction, firstEvent, firstDelta,
                        node.UsedRollout || !rollout.Terminal, lower, nextSequence++);

                    if (rollout.Terminal)
                        Consider(candidates, new RouteCandidate(firstAction, firstEvent,
                            firstDelta, path, false, lower));
                    else if (rollout.Remaining.ModelComplete)
                    {
                        var route = PlannerRouteEstimate.Add(path, rollout.Remaining);
                        if (!firstAction.Irreversible || route.ModelComplete)
                            Consider(candidates, new RouteCandidate(firstAction, firstEvent,
                                firstDelta, route, true, lower));
                    }
                    else
                    {
                        lastBlocker = rollout.Blocker.Kind == PlannerBlockerKind.None
                            ? new PlannerBlocker(PlannerBlockerKind.TerminalModelIncomplete,
                                "rollout cannot complete the terminal route",
                                firstAction.Key.Adapter, firstAction.Key, firstEvent.Key)
                            : rollout.Blocker;
                        if (firstAction.RolloutFallback && !firstAction.Irreversible)
                        {
                            var currentFallback = new RouteCandidate(firstAction,
                                firstEvent, firstDelta,
                                PlannerRouteEstimate.Unavailable(lower), true, lower);
                            if (fallback == null || CompareFallback(currentFallback, fallback) < 0)
                                fallback = currentFallback;
                        }
                    }

                    if (next.Depth < budget.MaximumDepth)
                    {
                        if (!AddLabel(labels, next,
                                budget.MaximumLabelsPerSignature))
                        {
                            pruned++;
                            continue;
                        }
                        heap.Push(next, Priority(next, rollout, budget));
                    }
                    else depthStopped = true;
                }
            }

            var stopped = expanded >= budget.MaximumExpandedNodes
                ? PlannerBlockerKind.NodeBudgetExhausted
                : watch.ElapsedMilliseconds >= budget.MaximumMilliseconds
                    ? PlannerBlockerKind.TimeBudgetExhausted
                    : depthStopped ? PlannerBlockerKind.DepthBudgetExhausted
                    : PlannerBlockerKind.None;
            if (candidates.Count > 0)
                return CompleteDecision(root, candidates.Values, globalLower,
                    expanded, generated, pruned);
            if (fallback != null)
            {
                var blockerKind = stopped == PlannerBlockerKind.None
                    ? PlannerBlockerKind.TerminalModelIncomplete : stopped;
                return new ScheduleDecision(ScheduleDecisionStatus.RolloutFallback,
                    root.Projection, fallback.Action, fallback.Event, fallback.Delta,
                    fallback.Route, false, default(PlannerActionKey), -1.0,
                    fallback.LowerBound, -1.0,
                    new PlannerBlocker(blockerKind,
                        "bounded search returned a reversible finite-event fallback",
                        fallback.Action.Key.Adapter, fallback.Action.Key,
                        fallback.Event.Key), true, expanded, generated, pruned);
            }
            if (stopped != PlannerBlockerKind.None)
                lastBlocker = new PlannerBlocker(stopped,
                    "bounded search exhausted its configured frontier");
            else if (!sawAction)
                lastBlocker = new PlannerBlocker(PlannerBlockerKind.NoActions,
                    "no transition adapter proposed an action");
            else if (!sawFiniteEvent)
                lastBlocker = new PlannerBlocker(
                    PlannerBlockerKind.NoFiniteNextEvent,
                    "no proposed action exposed a finite next event");
            return Blocked(root, lastBlocker, expanded, generated, pruned);
        }

        private static ScheduleDecision CompleteDecision(PlannerSearchState root,
            IEnumerable<RouteCandidate> source, double globalLower,
            int expanded, int generated, int pruned)
        {
            var candidates = source.OrderBy(x => x, RouteCandidateComparer.Instance).ToArray();
            var winner = candidates[0];
            var hasRunner = candidates.Length > 1;
            var regret = hasRunner
                ? Math.Max(0.0, candidates[1].Route.MeanSeconds
                                - winner.Route.MeanSeconds) : -1.0;
            var lower = Math.Max(0.0, Math.Min(globalLower,
                winner.Route.LowerBoundSeconds));
            var gap = Math.Max(0.0, winner.Route.MeanSeconds - lower);
            return new ScheduleDecision(ScheduleDecisionStatus.ShadowPlan,
                root.Projection, winner.Action, winner.Event, winner.Delta,
                winner.Route, hasRunner,
                hasRunner ? candidates[1].Action.Key : default(PlannerActionKey),
                regret, lower, gap, PlannerBlocker.None(), winner.UsedRollout,
                expanded, generated, pruned);
        }

        private static ScheduleDecision Blocked(PlannerSearchState root,
            PlannerBlocker blocker, int expanded, int generated, int pruned)
        {
            return new ScheduleDecision(ScheduleDecisionStatus.Blocked,
                root.Projection, null, null, new PlannerDelta(null),
                PlannerRouteEstimate.Unavailable(0.0), false,
                default(PlannerActionKey), -1.0, 0.0, -1.0,
                blocker, false, expanded, generated, pruned);
        }

        private static PlannerEvent[] EarliestBatch(IEnumerable<PlannerEvent> source)
        {
            var events = (source ?? Enumerable.Empty<PlannerEvent>())
                .Where(x => x != null && x.Duration.ModelComplete
                            && !double.IsNaN(x.Duration.MeanSeconds)
                            && !double.IsInfinity(x.Duration.MeanSeconds))
                .GroupBy(x => x.Key).Select(x => x.OrderBy(y => y.SourceOrder).First())
                .ToArray();
            if (events.Length == 0) return new PlannerEvent[0];
            var earliest = events.Min(x => x.Duration.MeanSeconds);
            return events.Where(x => Math.Abs(x.Duration.MeanSeconds - earliest) <= 1e-9)
                .OrderBy(x => x.SourceOrder).ThenBy(x => x.Key).ToArray();
        }

        private static double Priority(SearchNode node, PlannerRolloutEstimate rollout,
            PlannerSearchBudget budget)
        {
            return node.Path.MeanSeconds + budget.HeuristicWeight
                   * (rollout.Remaining.ModelComplete
                       ? rollout.Remaining.MeanSeconds
                       : rollout.Remaining.LowerBoundSeconds);
        }

        private static void Consider(
            IDictionary<PlannerActionKey, RouteCandidate> candidates,
            RouteCandidate candidate)
        {
            RouteCandidate current;
            if (!candidates.TryGetValue(candidate.Action.Key, out current)
                || RouteCandidateComparer.Instance.Compare(candidate, current) < 0)
                candidates[candidate.Action.Key] = candidate;
        }

        private static int CompareFallback(RouteCandidate left, RouteCandidate right)
        {
            var value = left.Event.Duration.MeanSeconds.CompareTo(
                right.Event.Duration.MeanSeconds);
            return value != 0 ? value : left.Action.Key.CompareTo(right.Action.Key);
        }

        private static bool AddLabel(
            IDictionary<DominanceKey, List<DominanceLabel>> labels,
            SearchNode node, int maximum)
        {
            var key = new DominanceKey(node.State.DurableSignature,
                node.State.DiscontinuitySignature);
            List<DominanceLabel> group;
            if (!labels.TryGetValue(key, out group))
            {
                group = new List<DominanceLabel>();
                labels.Add(key, group);
            }
            var candidate = new DominanceLabel(node);
            if (group.Any(x => x.Dominates(candidate))) return false;
            group.RemoveAll(x => candidate.Dominates(x));
            group.Add(candidate);
            if (group.Count <= maximum) return true;
            var keep = group.OrderBy(x => x.PathMean)
                .ThenByDescending(x => x.TotalResources)
                .Take(maximum).ToList();
            var retained = keep.Contains(candidate);
            labels[key] = keep;
            return retained;
        }

        private static bool IsDominated(
            IDictionary<DominanceKey, List<DominanceLabel>> labels,
            SearchNode node)
        {
            var key = new DominanceKey(node.State.DurableSignature,
                node.State.DiscontinuitySignature);
            List<DominanceLabel> group;
            if (!labels.TryGetValue(key, out group)) return false;
            var candidate = new DominanceLabel(node);
            return group.Any(x => !x.Same(candidate) && x.Dominates(candidate));
        }

        private sealed class SearchNode
        {
            internal readonly PlannerSearchState State;
            internal readonly PlannerRouteEstimate Path;
            internal readonly int Depth;
            internal readonly PlannerActionBundle FirstAction;
            internal readonly PlannerEvent FirstEvent;
            internal readonly PlannerDelta FirstDelta;
            internal readonly bool UsedRollout;
            internal readonly double LowerBound;
            internal readonly long Sequence;

            internal SearchNode(PlannerSearchState state, PlannerRouteEstimate path,
                int depth, PlannerActionBundle firstAction, PlannerEvent firstEvent,
                PlannerDelta firstDelta, bool usedRollout, double lowerBound,
                long sequence)
            {
                State = state;
                Path = path;
                Depth = depth;
                FirstAction = firstAction;
                FirstEvent = firstEvent;
                FirstDelta = firstDelta;
                UsedRollout = usedRollout;
                LowerBound = lowerBound;
                Sequence = sequence;
            }
        }

        private sealed class SearchHeap
        {
            private readonly List<HeapItem> _items = new List<HeapItem>();
            internal int Count { get { return _items.Count; } }

            internal void Push(SearchNode node, double priority)
            {
                var item = new HeapItem(node, priority);
                _items.Add(item);
                var index = _items.Count - 1;
                while (index > 0)
                {
                    var parent = (index - 1) / 2;
                    if (Compare(_items[parent], item) <= 0) break;
                    _items[index] = _items[parent];
                    index = parent;
                }
                _items[index] = item;
            }

            internal SearchNode Pop()
            {
                var result = _items[0].Node;
                var tail = _items[_items.Count - 1];
                _items.RemoveAt(_items.Count - 1);
                if (_items.Count == 0) return result;
                var index = 0;
                while (true)
                {
                    var left = index * 2 + 1;
                    if (left >= _items.Count) break;
                    var right = left + 1;
                    var child = right < _items.Count
                                && Compare(_items[right], _items[left]) < 0 ? right : left;
                    if (Compare(tail, _items[child]) <= 0) break;
                    _items[index] = _items[child];
                    index = child;
                }
                _items[index] = tail;
                return result;
            }

            private static int Compare(HeapItem left, HeapItem right)
            {
                var value = left.Priority.CompareTo(right.Priority);
                return value != 0 ? value : left.Node.Sequence.CompareTo(right.Node.Sequence);
            }

            private sealed class HeapItem
            {
                internal readonly SearchNode Node;
                internal readonly double Priority;
                internal HeapItem(SearchNode node, double priority)
                {
                    Node = node;
                    Priority = priority;
                }
            }
        }

        private sealed class RouteCandidate
        {
            internal readonly PlannerActionBundle Action;
            internal readonly PlannerEvent Event;
            internal readonly PlannerDelta Delta;
            internal readonly PlannerRouteEstimate Route;
            internal readonly bool UsedRollout;
            internal readonly double LowerBound;

            internal RouteCandidate(PlannerActionBundle action, PlannerEvent plannerEvent,
                PlannerDelta delta, PlannerRouteEstimate route, bool usedRollout,
                double lowerBound)
            {
                Action = action;
                Event = plannerEvent;
                Delta = delta;
                Route = route;
                UsedRollout = usedRollout;
                LowerBound = lowerBound;
            }
        }

        private sealed class RouteCandidateComparer : IComparer<RouteCandidate>
        {
            internal static readonly RouteCandidateComparer Instance =
                new RouteCandidateComparer();
            public int Compare(RouteCandidate left, RouteCandidate right)
            {
                if (ReferenceEquals(left, right)) return 0;
                if (left == null) return 1;
                if (right == null) return -1;
                if (!left.Route.ModelComplete) return right.Route.ModelComplete ? 1 :
                    left.Action.Key.CompareTo(right.Action.Key);
                if (!right.Route.ModelComplete) return -1;
                var value = left.Route.MeanSeconds.CompareTo(right.Route.MeanSeconds);
                if (value != 0) return value;
                value = left.Route.P90Seconds.CompareTo(right.Route.P90Seconds);
                if (value != 0) return value;
                if (left.UsedRollout != right.UsedRollout)
                    return left.UsedRollout ? 1 : -1;
                return left.Action.Key.CompareTo(right.Action.Key);
            }
        }

        private struct DominanceKey : IEquatable<DominanceKey>
        {
            internal readonly string Durable;
            internal readonly string Discontinuity;
            internal DominanceKey(string durable, string discontinuity)
            {
                Durable = durable;
                Discontinuity = discontinuity;
            }
            public bool Equals(DominanceKey other)
            {
                return string.Equals(Durable, other.Durable, StringComparison.Ordinal)
                       && string.Equals(Discontinuity, other.Discontinuity,
                           StringComparison.Ordinal);
            }
            public override bool Equals(object obj)
            {
                return obj is DominanceKey && Equals((DominanceKey)obj);
            }
            public override int GetHashCode()
            {
                return ((Durable == null ? 0 : Durable.GetHashCode()) * 397)
                       ^ (Discontinuity == null ? 0 : Discontinuity.GetHashCode());
            }
        }

        private sealed class DominanceLabel
        {
            private readonly double[] _resources;
            private readonly double[] _production;
            private readonly double[] _metrics;
            private readonly bool[] _metricSeen;
            private readonly PlannerModeAssignment[] _modes;
            internal readonly string StateKey;
            internal readonly double PathMean;
            internal readonly double PathP90;
            internal readonly double TotalResources;

            internal DominanceLabel(SearchNode node)
            {
                StateKey = node.State.StateKey;
                PathMean = node.Path.MeanSeconds;
                PathP90 = node.Path.P90Seconds;
                var resources = node.State.Resources();
                _resources = resources.Select(x => x.Balance).ToArray();
                _production = resources.Select(x => x.ProductionPerSecond).ToArray();
                TotalResources = _resources.Sum() + _production.Sum();
                _modes = node.State.Modes();
                var metricCount = Enum.GetValues(typeof(PlannerMetricKind)).Length;
                _metrics = new double[metricCount];
                _metricSeen = new bool[metricCount];
                foreach (var metric in node.State.Metrics())
                {
                    _metrics[(int)metric.Kind] = metric.Value;
                    _metricSeen[(int)metric.Kind] = true;
                }
            }

            internal bool Same(DominanceLabel other)
            {
                return other != null && string.Equals(StateKey, other.StateKey,
                    StringComparison.Ordinal) && PathMean == other.PathMean
                       && PathP90 == other.PathP90;
            }

            internal bool Dominates(DominanceLabel other)
            {
                if (other == null || PathMean > other.PathMean + 1e-9
                    || PathP90 > other.PathP90 + 1e-9)
                    return false;
                if (_modes.Length != other._modes.Length) return false;
                for (var i = 0; i < _modes.Length; i++)
                    if (_modes[i].Dimension != other._modes[i].Dimension
                        || _modes[i].Value != other._modes[i].Value)
                        return false;
                var strict = PathMean < other.PathMean - 1e-9
                             || PathP90 < other.PathP90 - 1e-9;
                for (var i = 0; i < _resources.Length; i++)
                {
                    if (_resources[i] + 1e-9 < other._resources[i]) return false;
                    if (_resources[i] > other._resources[i] + 1e-9) strict = true;
                    if (_production[i] + 1e-9 < other._production[i]) return false;
                    if (_production[i] > other._production[i] + 1e-9) strict = true;
                }
                for (var i = 0; i < _metrics.Length; i++)
                {
                    if (_metricSeen[i] != other._metricSeen[i]) return false;
                    if (!_metricSeen[i]) continue;
                    if (_metrics[i] + 1e-9 < other._metrics[i]) return false;
                    if (_metrics[i] > other._metrics[i] + 1e-9) strict = true;
                }
                return strict;
            }
        }
    }

    internal static class PlannerRolloutExtensions
    {
        internal static double LowerBound(this PlannerRolloutEstimate rollout)
        {
            return rollout == null ? 0.0 : rollout.Remaining.LowerBoundSeconds;
        }
    }
}
