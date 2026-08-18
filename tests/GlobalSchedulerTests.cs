/*
FILE PURPOSE

Purpose: GlobalSchedulerTests is the dependency-free task-28 suite. It proves bounded shadow search
against tiny exhaustive worlds, real wait/reset counterexamples, simultaneous event ordering and
deduplication, chronological currencies, mutual exclusion, dominance/discontinuity safety, rollout
fallbacks, task-27 terminal estimates, trace residuals, and archived snapshot replay.

Mechanism: Pure fixture adapters expose typed action/event/transition graphs over immutable planner
states. The suite compares scheduler choices with a brute-force oracle and injects invalid bundles,
unknown timing, budget limits, duplicate observations, and resource shortfalls. No production
manager, Character, Unity controller, save, runtime process, or mutation root is loaded.

Inputs and outputs: There are no external inputs. The executable prints one assertion count or
throws on the first mismatch. Temporary compilation is the only artifact produced by the caller.

Invariants and safety: Every decision is ShadowOnly and CanExecute=false. Unknown irreversible work
never wins. A planned action has a finite typed next event or a named blocker. Dominance never crosses
a discontinuity signature, and archived replay rejects a mismatched snapshot binding.

Extension points and non-goals: Add a fixture when a new adapter/resource/mode is introduced by task
29. Live integration, persistence, authority transfer, controller calls, and END execution remain
outside this suite.
*/
using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Autopilot;

internal static class GlobalSchedulerTests
{
    private static int _assertions;
    private static readonly OptimizationSnapshot RootProjection = Projection(false, false);

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static void Equal(double actual, double expected, string message)
    {
        Assert(Math.Abs(actual - expected) <= 0.000001,
            message + " (actual " + actual + ", expected " + expected + ")");
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        _assertions++;
        try { action(); }
        catch (T) { return; }
        throw new Exception("FAIL: " + message);
    }

    private static OptimizationSnapshot Projection(bool complete, bool requireBasic)
    {
        var identity = new OptimizationIdentity("session", "save", "model", "objective");
        var stamps = OptimizationSnapshot.AllStateKeys().Select(x =>
            new OptimizationStateStamp(x, "state-" + (int)x)).ToArray();
        var values = OptimizationSnapshot.AllFactKeys().ToDictionary(x => x, x => 0.0);
        values[OptimizationFactKey.HighestSadisticBoss] = 300.0;
        values[OptimizationFactKey.Titan13Defeated] = 1.0;
        values[OptimizationFactKey.HacksZeroThroughFourteenCapped] = 1.0;
        values[OptimizationFactKey.EndHackLevel] = 1.0;
        values[OptimizationFactKey.Move69Unlocked] = 1.0;
        values[OptimizationFactKey.Perk231Level] = 1.0;
        values[OptimizationFactKey.Quirk176Level] = 1.0;
        values[OptimizationFactKey.Wish203Level] = 1.0;
        values[OptimizationFactKey.ItopodHighestFloor] = 1450.0;
        values[OptimizationFactKey.HeldEndCards] = 1.0;
        values[OptimizationFactKey.MayoZero] = 99.0;
        values[OptimizationFactKey.MayoOne] = 99.0;
        values[OptimizationFactKey.MayoTwo] = 99.0;
        values[OptimizationFactKey.MayoThree] = 99.0;
        values[OptimizationFactKey.MayoFour] = 99.0;
        values[OptimizationFactKey.MayoFive] = 99.0;
        values[OptimizationFactKey.Blood] = MechanicsEndgame.EndBloodCost;
        values[OptimizationFactKey.UsableInventoryFreeSlots] = 40.0;
        values[OptimizationFactKey.OrdinaryInventoryCurrentSpaces] = 40.0;
        values[OptimizationFactKey.DeckFreeSlots] = 2.0;
        values[OptimizationFactKey.EndFiltersClear] = 1.0;
        var facts = new OptimizationFactSet(values.Select(x =>
            new OptimizationFact(x.Key, x.Value)));
        var items = Enumerable.Range(480, 16).Select(id =>
            new OptimizationEndItemState(id, complete ? 1 : 0, 0, true, false, true));
        var challenges = OptimizationSnapshot.AllChallengeKinds().Select(kind =>
            new OptimizationChallengeState(kind,
                requireBasic && kind == OptimizationChallengeKind.Basic,
                complete ? 1 : 0,
                requireBasic && kind == OptimizationChallengeKind.Basic ? 1 : 0));
        return new OptimizationSnapshot(1L, identity,
            OptimizationDifficulty.Sadistic, stamps, facts, items, challenges, complete);
    }

    private static PlannerSearchState State(string key, int progress, bool terminal = false,
        string durable = "durable", string discontinuity = "regime",
        double elapsed = 0.0, double gold = 100.0, double goldRate = 0.0,
        OptimizationSnapshot projection = null,
        IEnumerable<ProgressionWorkEstimate> terminalEstimates = null)
    {
        return new PlannerSearchState(key, durable, discontinuity, elapsed, terminal,
            projection ?? RootProjection,
            new[] {new PlannerResourceAmount(PlannerResourceKind.Gold, gold, goldRate)},
            new[] {new PlannerMetricValue(PlannerMetricKind.DurableProgress, progress)},
            null, terminalEstimates);
    }

    private static PlannerSearchBudget Budget(int depth = 8, int nodes = 1000)
    {
        return new PlannerSearchBudget(nodes, depth, 10000, 32, 1.0);
    }

    private static PlannerRouteEstimate Duration(double seconds)
    {
        return new PlannerRouteEstimate(seconds, seconds, seconds + 1.0,
            Math.Max(0.0, seconds - 1.0), seconds + 2.0,
            ProgressionEstimateProvenance.SourceKnown, true);
    }

    private static PlannerCommandKind CommandFor(PlannerActionKind kind)
    {
        switch (kind)
        {
            case PlannerActionKind.OrdinaryReset: return PlannerCommandKind.OrdinaryReset;
            case PlannerActionKind.EnterChallenge: return PlannerCommandKind.EnterChallenge;
            case PlannerActionKind.ChangeDifficulty: return PlannerCommandKind.ChangeDifficulty;
            case PlannerActionKind.StartEndSequence: return PlannerCommandKind.StartEndSequence;
            case PlannerActionKind.Measure: return PlannerCommandKind.Observe;
            default: return PlannerCommandKind.Wait;
        }
    }

    private static PlannerActionBundle ActionBundle(PlannerAdapterKind adapter,
        PlannerActionKind kind, int id, bool irreversible = false,
        bool fallback = false, IEnumerable<PlannerModeClaim> modes = null,
        IEnumerable<PlannerResourceEvent> resources = null,
        IEnumerable<PlannerCommand> commands = null, bool namedPayoff = false)
    {
        var key = new PlannerActionKey(adapter, kind, id);
        return new PlannerActionBundle(key, "action-" + adapter + "-" + id,
            commands ?? new[] {new PlannerCommand(CommandFor(kind), id, 0,
                kind != PlannerActionKind.Continue && kind != PlannerActionKind.Measure)},
            modes, resources, irreversible, kind == PlannerActionKind.Measure,
            fallback, namedPayoff,
            new PlannerEventKey(PlannerEventKind.ResourceCompletion, id));
    }

    private static PlannerEvent Event(int id, double seconds,
        int sourceOrder = 0, PlannerEventKind kind = PlannerEventKind.Fixture)
    {
        return new PlannerEvent(new PlannerEventKey(kind, id), "event-" + id,
            sourceOrder, Duration(seconds), false, true);
    }

    private static FixtureEdge Edge(string from, PlannerActionBundle action,
        double seconds, Func<PlannerSearchState, PlannerSearchState> successor,
        int eventId = 0)
    {
        return new FixtureEdge(from, action, new[] {Event(eventId, seconds)},
            successor, PlannerTransitionKind.Fixture);
    }

    private sealed class FixtureEdge
    {
        internal readonly string From;
        internal readonly PlannerActionBundle Action;
        internal readonly PlannerEvent[] Events;
        internal readonly Func<PlannerSearchState, PlannerSearchState> Successor;
        internal readonly PlannerTransitionKind TransitionKind;

        internal FixtureEdge(string from, PlannerActionBundle action,
            PlannerEvent[] events, Func<PlannerSearchState, PlannerSearchState> successor,
            PlannerTransitionKind transitionKind)
        {
            From = from;
            Action = action;
            Events = events;
            Successor = successor;
            TransitionKind = transitionKind;
        }
    }

    private sealed class FixtureAdapter : IPlannerTransitionAdapter
    {
        private readonly FixtureEdge[] _edges;
        public PlannerAdapterKind Kind { get; private set; }
        internal int LastBatchLength;
        internal PlannerEventKey[] LastBatch = new PlannerEventKey[0];

        internal FixtureAdapter(PlannerAdapterKind kind, params FixtureEdge[] edges)
        {
            Kind = kind;
            _edges = edges;
        }

        public void AddActions(PlannerSearchState state, IList<PlannerActionBundle> output)
        {
            foreach (var edge in _edges.Where(x => x.From == state.StateKey))
                output.Add(edge.Action);
        }

        public void AddEvents(PlannerSearchState state, PlannerActionBundle action,
            IList<PlannerEvent> output)
        {
            var edge = _edges.First(x => x.From == state.StateKey
                                         && x.Action.Key.Equals(action.Key));
            foreach (var item in edge.Events) output.Add(item);
        }

        public PlannerTransition Apply(PlannerSearchState state,
            PlannerActionBundle action, PlannerEvent[] simultaneousEvents)
        {
            LastBatchLength = simultaneousEvents.Length;
            LastBatch = simultaneousEvents.Select(x => x.Key).ToArray();
            var edge = _edges.First(x => x.From == state.StateKey
                                         && x.Action.Key.Equals(action.Key));
            var delta = new PlannerDelta(new[] {new PlannerDeltaValue(
                PlannerDeltaKind.Fixture, action.Key.LocalId,
                simultaneousEvents.Length)});
            return new PlannerTransition(edge.TransitionKind,
                edge.Successor(state), delta);
        }
    }

    private sealed class TerminalOnlyRollout : IPlannerRolloutPolicy
    {
        public PlannerRolloutEstimate Evaluate(PlannerSearchState state)
        {
            return state.TerminalFlag
                ? new PlannerRolloutEstimate(true, PlannerRouteEstimate.Exact(0.0),
                    PlannerBlocker.None())
                : new PlannerRolloutEstimate(false,
                    PlannerRouteEstimate.Unavailable(0.0),
                    new PlannerBlocker(PlannerBlockerKind.TerminalModelIncomplete,
                        "fixture terminal route unknown"));
        }
    }

    private static ScheduleDecision Plan(PlannerSearchState root,
        IEnumerable<IPlannerTransitionAdapter> adapters, int depth = 8)
    {
        return new GlobalEventScheduler().Plan(root, adapters,
            new TerminalOnlyRollout(), Budget(depth));
    }

    private static void TestRouteEstimateAndShadowInvariant()
    {
        Throws<ArgumentException>(() => new PlannerRouteEstimate(-1, -1, -1,
                0, -1, ProgressionEstimateProvenance.Unknown, true),
            "complete route estimate cannot encode unknown time as negative or zero convention");
        Throws<ArgumentException>(() => new PlannerRouteEstimate(10, 10, 12,
                9, 13, ProgressionEstimateProvenance.Empirical, true),
            "empirical route estimate requires samples and confidence");
        var empirical = new PlannerRouteEstimate(10, 9, 12, 8, 14,
            ProgressionEstimateProvenance.Empirical, true, 20, 0.9);
        var sum = PlannerRouteEstimate.Add(PlannerRouteEstimate.Exact(5), empirical);
        Equal(sum.MeanSeconds, 15, "route means add from now, with no sunk-time denominator");
        Equal(sum.P90Seconds, 17, "route p90 remains explicit");
        Assert(sum.Provenance == ProgressionEstimateProvenance.Empirical,
            "weakest evidence provenance propagates");

        var root = State("terminal", 1, true);
        var decision = Plan(root, new IPlannerTransitionAdapter[0]);
        Assert(decision.Status == ScheduleDecisionStatus.Terminal
               && decision.Authority == PlannerAuthority.ShadowOnly
               && !decision.CanExecute,
            "even a terminal decision is permanently shadow-only");
    }

    private static void TestChronologicalResources()
    {
        var state = State("r", 0, gold: 100, goldRate: 10);
        var action = new PlannerActionKey(PlannerAdapterKind.GoldBlood,
            PlannerActionKind.SpendGold, 1);
        var feasible = PlannerResourceLedger.Evaluate(state, 3.0, new[]
        {
            new PlannerResourceEvent(PlannerResourceKind.Gold, 1, "blood", 2.0,
                0, 110, 110, 0, false),
            new PlannerResourceEvent(PlannerResourceKind.Gold, 2, "pit", 3.0,
                1, 10, 10, 0, false)
        }, action);
        Assert(feasible.Feasible, "chronological production funds later declared debits");
        Equal(feasible.Resources().Single(x => x.Kind == PlannerResourceKind.Gold).Balance,
            10.0, "production is counted exactly once across chronological events");

        var blocked = PlannerResourceLedger.Evaluate(state, 3.0, new[]
        {
            new PlannerResourceEvent(PlannerResourceKind.Gold, 1, "early-blood", 0.0,
                0, 110, 110, 0, false)
        }, action);
        Assert(!blocked.Feasible
               && blocked.Blocker.Kind
               == PlannerBlockerKind.ChronologicalResourceViolation,
            "a future affordable charge is rejected when liquidity is absent at its timestamp");

        var tossThenSpend = PlannerResourceLedger.Evaluate(state, 2.0, new[]
        {
            new PlannerResourceEvent(PlannerResourceKind.Gold, 3, "pit-all", 1.0,
                0, 110, 0, 0, true),
            new PlannerResourceEvent(PlannerResourceKind.Gold, 4, "digger", 2.0,
                1, 15, 15, 0, false)
        }, action);
        Assert(!tossThenSpend.Feasible,
            "all-Gold toss prevents a later spend unless post-toss production really funds it");

        var beyond = PlannerResourceLedger.Evaluate(state, 1.0, new[]
        {
            new PlannerResourceEvent(PlannerResourceKind.Gold, 5, "after-replan", 2.0,
                0, 1, 1, 0, false)
        }, action);
        Assert(beyond.Blocker.Kind == PlannerBlockerKind.ResourceEventBeyondNextEvent,
            "bundle cannot promise a debit beyond its next mandatory replan boundary");

        var startProduction = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Continue, 20);
        var spendProduction = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.SpendGold, 21, resources: new[]
            {
                new PlannerResourceEvent(PlannerResourceKind.Gold, 21, "new-rate", 1,
                    0, 10, 10, 0, false)
            });
        var productionAdapter = new FixtureAdapter(PlannerAdapterKind.Fixture,
            Edge("rate-root", startProduction, 1,
                x => State("rate-active", 0, gold: 0, goldRate: 10), 20),
            Edge("rate-active", spendProduction, 1,
                x => State("rate-done", 1, true, gold: 0, goldRate: 10), 21));
        var productionRoute = Plan(State("rate-root", 0, gold: 0, goldRate: 0),
            new[] {productionAdapter});
        Assert(productionRoute.Status == ScheduleDecisionStatus.ShadowPlan,
            "a transition's new production rate is preserved after ledger balance overlay");
    }

    private static void TestTypedBundleConflicts()
    {
        var duplicateAdventure = ActionBundle(PlannerAdapterKind.Collection,
            PlannerActionKind.AdventureMode, 1, modes: new[]
            {
                new PlannerModeClaim(PlannerModeDimension.Adventure, 1),
                new PlannerModeClaim(PlannerModeDimension.Adventure, 2)
            });
        Assert(PlannerActionValidator.Validate(duplicateAdventure).Kind
               == PlannerBlockerKind.IncompatibleModes,
            "two zones in one bundle are rejected before search");

        var resetChallenge = ActionBundle(PlannerAdapterKind.Challenge,
            PlannerActionKind.EnterChallenge, 2, true, commands: new[]
            {
                new PlannerCommand(PlannerCommandKind.OrdinaryReset, 1, 0, false),
                new PlannerCommand(PlannerCommandKind.EnterChallenge, 2, 1, true)
            });
        Assert(PlannerActionValidator.Validate(resetChallenge).Kind
               == PlannerBlockerKind.ConflictingIrreversibleBoundaries,
            "challenge entry and ordinary reset cannot coexist");

        var materialThenWork = ActionBundle(PlannerAdapterKind.PermanentPurchase,
            PlannerActionKind.BuyPermanent, 3, commands: new[]
            {
                new PlannerCommand(PlannerCommandKind.BuyPermanent, 3, 0, true),
                new PlannerCommand(PlannerCommandKind.Wait, 4, 1, false)
            });
        Assert(PlannerActionValidator.Validate(materialThenWork).Kind
               == PlannerBlockerKind.MaterialCommandNotLast,
            "material successor stops the bundle and forces replan");

        var fictitiousHold = ActionBundle(PlannerAdapterKind.GoldBlood,
            PlannerActionKind.Continue, 4, commands: new[]
            {
                new PlannerCommand(PlannerCommandKind.InvestResetLocal, 4, 0, false)
            });
        Assert(PlannerActionValidator.Validate(fictitiousHold).Kind
               == PlannerBlockerKind.MissingNamedPayoff,
            "unscheduled hold cannot admit reset-local investment without a named payoff event");
        var named = ActionBundle(PlannerAdapterKind.GoldBlood,
            PlannerActionKind.Continue, 5, commands: new[]
            {
                new PlannerCommand(PlannerCommandKind.InvestResetLocal, 5, 0, false)
            }, namedPayoff: true);
        Assert(PlannerActionValidator.Validate(named).Kind == PlannerBlockerKind.None,
            "typed payoff event admits bounded reset-local work");
    }

    private static void TestSimultaneousEventsAndDeduplication()
    {
        var action = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Fixture, 1);
        var edge = new FixtureEdge("root", action, new[]
        {
            Event(1, 3, 20, PlannerEventKind.Timer),
            Event(2, 3, 10, PlannerEventKind.Drop),
            Event(1, 3, 5, PlannerEventKind.Timer),
            Event(3, 4, 0, PlannerEventKind.Observation)
        }, state => State("done", 2, true), PlannerTransitionKind.Fixture);
        var adapter = new FixtureAdapter(PlannerAdapterKind.Fixture, edge);
        var decision = Plan(State("root", 0), new[] {adapter});
        Assert(decision.Status == ScheduleDecisionStatus.ShadowPlan
               && adapter.LastBatchLength == 2,
            "two independent events at earliest timestamp both apply and duplicate typed key once");
        Assert(adapter.LastBatch[0].Equals(
                   new PlannerEventKey(PlannerEventKind.Timer, 1))
               && adapter.LastBatch[1].Equals(
                   new PlannerEventKey(PlannerEventKind.Drop, 2)),
            "simultaneous batch uses source order after typed deduplication");
        Equal(decision.TerminalEta.MeanSeconds, 3,
            "later event is not invented into the earliest successor");
    }

    private static void TestTinyWorldMatchesExhaustive()
    {
        var a0 = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Continue, 1);
        var b0 = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Continue, 2);
        var a1 = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Continue, 3);
        var edges = new[]
        {
            Edge("root", a0, 1, x => State("a", 1), 1),
            Edge("root", b0, 6, x => State("done-b", 1, true), 2),
            Edge("a", a1, 4, x => State("done-a", 2, true), 3)
        };
        var adapter = new FixtureAdapter(PlannerAdapterKind.Fixture, edges);
        var decision = Plan(State("root", 0), new[] {adapter});
        var brute = Exhaustive("root", edges, new HashSet<string>());
        Equal(decision.TerminalEta.MeanSeconds, brute,
            "bounded search equals exhaustive optimum in a tiny deterministic world");
        Assert(decision.Selected.Key.Equals(a0.Key) && decision.HasRunnerUp,
            "tiny world selects the globally shorter successor route and retains runner-up");
        Equal(decision.RegretSeconds, 1, "runner-up regret is complete-route seconds");
        Assert(decision.OptimalityGapSeconds >= 0,
            "shadow decision publishes an explicit lower-bound gap");
    }

    private static double Exhaustive(string state, IEnumerable<FixtureEdge> edges,
        HashSet<string> path)
    {
        if (state.StartsWith("done", StringComparison.Ordinal)) return 0.0;
        if (!path.Add(state)) return double.PositiveInfinity;
        var best = double.PositiveInfinity;
        foreach (var edge in edges.Where(x => x.From == state))
        {
            var successor = edge.Successor(State(state, 0));
            var duration = edge.Events.Min(x => x.Duration.MeanSeconds);
            best = Math.Min(best, duration + Exhaustive(successor.StateKey,
                edges, new HashSet<string>(path)));
        }
        return best;
    }

    private static void TestRealWaitResetCounterexamplesAndSunkTime()
    {
        var wait = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Continue, 1, fallback: true);
        var reset = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.OrdinaryReset, 2, true);
        var recovery = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Continue, 3);
        var first = new FixtureAdapter(PlannerAdapterKind.Fixture,
            Edge("root", wait, 2, x => State("wait-done", 1, true), 1),
            Edge("root", reset, 1, x => State("reset", 0,
                discontinuity: "post-reset", elapsed: 0), 2),
            Edge("reset", recovery, 10, x => State("reset-done", 1, true,
                discontinuity: "post-reset"), 3));
        var decision = Plan(State("root", 0, elapsed: 100), new[] {first});
        Assert(decision.Selected.Key.Equals(wait.Key),
            "positive local reset heuristic loses when continuing reaches durable gate first");
        Equal(decision.TerminalEta.MeanSeconds, 2,
            "continuation is a real finite successor, never HOLD=0");

        var later = Plan(State("root", 0, elapsed: 101), new[] {first});
        Assert(later.Selected.Key.Equals(wait.Key)
               && later.TerminalEta.MeanSeconds == decision.TerminalEta.MeanSeconds,
            "one additional sunk second does not change route ordering absent modeled clock effects");

        var second = new FixtureAdapter(PlannerAdapterKind.Fixture,
            Edge("root2", wait, 20, x => State("wait2-done", 1, true), 1),
            Edge("root2", reset, 1, x => State("reset2", 0,
                discontinuity: "post-reset-2", elapsed: 0), 2),
            Edge("reset2", recovery, 2, x => State("reset2-done", 1, true,
                discontinuity: "post-reset-2"), 3));
        var resetWins = Plan(State("root2", 0), new[] {second});
        Assert(resetWins.Selected.Key.Equals(reset.Key),
            "lower-Number reset can win when its complete recovery successor is faster");
        Equal(resetWins.TerminalEta.MeanSeconds, 3,
            "reset comparison includes finite post-reset recovery path");
    }

    private static void TestDominanceAndDiscontinuities()
    {
        var slow = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Continue, 1, resources: new[]
            {
                new PlannerResourceEvent(PlannerResourceKind.Gold, 1, "slow-cost", 0,
                    0, 20, 20, 0, false)
            });
        var fast = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Continue, 2);
        var finishSlow = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Continue, 3);
        var finishFast = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Continue, 4);
        var adapter = new FixtureAdapter(PlannerAdapterKind.Fixture,
            Edge("root", slow, 5, x => State("same-slow", 1, gold: 100), 1),
            Edge("root", fast, 2, x => State("same-fast", 1, gold: 100), 2),
            Edge("same-slow", finishSlow, 5, x => State("done-slow", 2, true), 3),
            Edge("same-fast", finishFast, 5, x => State("done-fast", 2, true), 4));
        var decision = Plan(State("root", 0), new[] {adapter});
        Assert(decision.Selected.Key.Equals(fast.Key) && decision.DominancePruned > 0,
            "strictly slower, poorer state with no durable advantage is dominance-pruned");

        var left = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Continue, 10);
        var right = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Continue, 11);
        var leftEnd = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Continue, 12);
        var rightEnd = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Continue, 13);
        var discontinuous = new FixtureAdapter(PlannerAdapterKind.Fixture,
            Edge("r2", left, 1, x => State("left", 1,
                discontinuity: "before-cap"), 10),
            Edge("r2", right, 1, x => State("right", 1,
                discontinuity: "after-cap"), 11),
            Edge("left", leftEnd, 9, x => State("done-left", 2, true), 12),
            Edge("right", rightEnd, 2, x => State("done-right", 2, true), 13));
        var discontinuityDecision = Plan(State("r2", 0), new[] {discontinuous});
        Assert(discontinuityDecision.Selected.Key.Equals(right.Key)
               && discontinuityDecision.GeneratedTransitions == 4,
            "states on opposite sides of a discontinuity are both expanded, never merged");

        var modeOne = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.AdventureMode, 30);
        var modeTwo = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.AdventureMode, 31);
        var modeFinish = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Continue, 32);
        var modeAdapter = new FixtureAdapter(PlannerAdapterKind.Fixture,
            Edge("mode-root", modeOne, 1, x => new PlannerSearchState("mode-one",
                "same", "same", 0, false, RootProjection, null,
                new[] {new PlannerMetricValue(PlannerMetricKind.DurableProgress, 0)},
                new[] {new PlannerModeAssignment(PlannerModeDimension.Adventure, 1)}), 30),
            Edge("mode-root", modeTwo, 1, x => new PlannerSearchState("mode-two",
                "same", "same", 0, false, RootProjection, null,
                new[] {new PlannerMetricValue(PlannerMetricKind.DurableProgress, 0)},
                new[] {new PlannerModeAssignment(PlannerModeDimension.Adventure, 2)}), 31),
            Edge("mode-two", modeFinish, 1,
                x => State("mode-done", 1, true), 32));
        var typedModes = Plan(State("mode-root", 0), new[] {modeAdapter});
        Assert(typedModes.Status == ScheduleDecisionStatus.ShadowPlan
               && typedModes.Selected.Key.Equals(modeTwo.Key),
            "dominance retains states with distinct typed mutually-exclusive modes");
    }

    private static void TestFallbackUnknownIrreversibleAndBlocker()
    {
        var fallback = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Measure, 1, false, true);
        var adapter = new FixtureAdapter(PlannerAdapterKind.Fixture,
            Edge("root", fallback, 2, x => State("measured", 0), 1));
        var decision = Plan(State("root", 0), new[] {adapter}, 1);
        Assert(decision.Status == ScheduleDecisionStatus.RolloutFallback
               && decision.ExpectedNextEvent != null
               && decision.Blocker.Kind == PlannerBlockerKind.DepthBudgetExhausted
               && !decision.CanExecute,
            "depth-bounded search returns reversible finite-event fallback with named blocker");

        var irreversible = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.OrdinaryReset, 2, true);
        var unknownEvent = new PlannerEvent(new PlannerEventKey(
            PlannerEventKind.Rebirth, 2), "unknown-reset", 0,
            PlannerRouteEstimate.Unavailable(0), false, false);
        var mixed = new FixtureAdapter(PlannerAdapterKind.Fixture,
            new FixtureEdge("r2", irreversible, new[] {unknownEvent},
                x => State("bad", 0), PlannerTransitionKind.OrdinaryReset),
            Edge("r2", fallback, 2, x => State("observed", 0), 1));
        var held = Plan(State("r2", 0), new[] {mixed}, 1);
        Assert(held.Selected.Key.Equals(fallback.Key),
            "unknown irreversible reset is held while reversible measurement remains selectable");

        var noEvent = new FixtureAdapter(PlannerAdapterKind.Fixture,
            new FixtureEdge("none", fallback, new PlannerEvent[0],
                x => State("never", 0), PlannerTransitionKind.Observation));
        var blocked = Plan(State("none", 0), new[] {noEvent});
        Assert(blocked.Status == ScheduleDecisionStatus.Blocked
               && blocked.Blocker.Kind == PlannerBlockerKind.NoFiniteNextEvent,
            "outside-model action returns named no-finite-event blocker");
    }

    private static void TestAdapterPermutationAndResourceRejection()
    {
        var a = ActionBundle(PlannerAdapterKind.Collection,
            PlannerActionKind.Collect, 1);
        var b = ActionBundle(PlannerAdapterKind.PermanentProgress,
            PlannerActionKind.BuyPermanent, 1);
        var adapterA = new FixtureAdapter(PlannerAdapterKind.Collection,
            Edge("root", a, 5, x => State("done-a", 1, true), 1));
        var adapterB = new FixtureAdapter(PlannerAdapterKind.PermanentProgress,
            Edge("root", b, 3, x => State("done-b", 1, true), 2));
        var scheduler = new GlobalEventScheduler();
        var forward = scheduler.Plan(State("root", 0),
            new IPlannerTransitionAdapter[] {adapterA, adapterB},
            new TerminalOnlyRollout(), Budget());
        var reverse = scheduler.Plan(State("root", 0),
            new IPlannerTransitionAdapter[] {adapterB, adapterA},
            new TerminalOnlyRollout(), Budget());
        Assert(forward.Selected.Key.Equals(b.Key)
               && reverse.Selected.Key.Equals(b.Key)
               && forward.TerminalEta.MeanSeconds == reverse.TerminalEta.MeanSeconds,
            "adapter/manager registration permutation cannot reinterpret strategy");

        var unaffordable = ActionBundle(PlannerAdapterKind.GoldBlood,
            PlannerActionKind.SpendGold, 3, resources: new[]
            {
                new PlannerResourceEvent(PlannerResourceKind.Gold, 1, "too-early", 0,
                    0, 150, 150, 0, false)
            });
        var wait = ActionBundle(PlannerAdapterKind.GoldBlood,
            PlannerActionKind.Continue, 4);
        var goldAdapter = new FixtureAdapter(PlannerAdapterKind.GoldBlood,
            Edge("g", unaffordable, 1, x => State("bad-gold", 1, true), 3),
            Edge("g", wait, 2, x => State("good-gold", 1, true), 4));
        var goldDecision = Plan(State("g", 0, gold: 100), new[] {goldAdapter});
        Assert(goldDecision.Selected.Key.Equals(wait.Key),
            "chronological ledger removes unaffordable action before route ranking");
    }

    private static void TestTask27RolloutBridge()
    {
        var estimates = new List<ProgressionWorkEstimate>();
        for (var id = 480; id <= 495; id++)
        {
            var node = (ProgressionNodeKey)((int)ProgressionNodeKey.EndItem480 + id - 480);
            estimates.Add(new ProgressionWorkEstimate(node, id - 479,
                id - 478, id - 480, id - 477,
                ProgressionEstimateProvenance.SourceKnown));
        }
        estimates.Add(new ProgressionWorkEstimate(ProgressionNodeKey.EndSequence,
            1, 2, 0, 3, ProgressionEstimateProvenance.SourceKnown));
        var policy = new ProgressionGraphRolloutPolicy(
            ProgressionDependencyGraph.CreateTerminalGraph());
        var open = policy.Evaluate(State("end-open", 0, projection:
            Projection(false, false), terminalEstimates: estimates));
        Assert(!open.Terminal && open.Remaining.ModelComplete,
            "task-27 terminal DAG supplies a complete typed rollout when every branch is modeled");
        Equal(open.Remaining.MeanSeconds, 17,
            "terminal rollout is max sixteen END branches plus final transaction");
        Assert(open.HasCriticalBranch
               && open.CriticalBranch == ProgressionNodeKey.EndItem495,
            "rollout exposes task-27 typed critical branch");

        estimates[15] = new ProgressionWorkEstimate(ProgressionNodeKey.EndItem495,
            16, 17, 15, 18, ProgressionEstimateProvenance.Empirical, 40, 0.8);
        var empirical = policy.Evaluate(State("end-empirical", 0, projection:
            Projection(false, false), terminalEstimates: estimates));
        Assert(empirical.Remaining.Provenance
               == ProgressionEstimateProvenance.Empirical
               && empirical.Remaining.SampleCount == 40
               && Math.Abs(empirical.Remaining.Confidence - 0.8) < 0.000001,
            "task-27 rollout preserves empirical sample count and confidence");

        var complete = policy.Evaluate(State("end-done", 1, projection:
            Projection(true, false)));
        Assert(complete.Terminal && complete.Remaining.MeanSeconds == 0,
            "completed task-27 snapshot is exact zero remaining time");
    }

    private static void TestTraceAndArchivedBacktests()
    {
        var wait = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.Continue, 1);
        var reset = ActionBundle(PlannerAdapterKind.Fixture,
            PlannerActionKind.OrdinaryReset, 2, true);
        var adapter = new FixtureAdapter(PlannerAdapterKind.Fixture,
            Edge("archive", wait, 2, x => State("done-wait", 1, true), 1),
            Edge("archive", reset, 5, x => State("done-reset", 1, true), 2));
        var root = State("archive", 0);
        var decision = Plan(root, new[] {adapter});
        var observation = new PlannerObservation(decision.PlanStateHash,
            decision.ExpectedNextEvent.Key,
            decision.ExpectedNextEvent.Duration.MeanSeconds + 1.0,
            decision.ExpectedDelta);
        var trace = PlannerTraceRecord.Observe(decision, observation);
        Assert(trace.ObservationStatus == PlannerObservationStatus.Matched,
            "typed observed event matches without parsing event label");
        Equal(trace.TimingResidualSeconds, 1.0,
            "trace records observed-minus-expected timing residual");
        Equal(trace.DeltaResidual, 0.0,
            "trace compares complete typed delta vectors");
        var json = trace.ToJson();
        Assert(json.Contains("\"canExecute\":false")
               && json.Contains("\"p90Seconds\"")
               && json.Contains("\"regretSeconds\""),
            "trace JSON carries shadow authority and uncertainty/regret fields");
        var stale = PlannerTraceRecord.Observe(decision,
            new PlannerObservation("other-snapshot", observation.Event,
                observation.ObservedSeconds, observation.Delta));
        Assert(stale.ObservationStatus == PlannerObservationStatus.StaleSnapshot,
            "observation from another snapshot cannot validate the current plan");
        Assert(double.IsNaN(stale.TimingResidualSeconds)
               && double.IsNaN(stale.DeltaResidual),
            "stale observations cannot leak residuals into later calibration");

        var archives = new[]
        {
            new PlannerArchivedSnapshot("improves", root, true, reset.Key, 6.0),
            new PlannerArchivedSnapshot("regresses", root, true, reset.Key, 1.0)
        };
        var backtest = PlannerBacktestRunner.Replay(archives,
            item => Plan(item.State, new[] {adapter}));
        Assert(backtest.Total == 2 && backtest.ImprovedOrEqual == 1
               && backtest.Regressed == 1 && backtest.BranchSwitches == 2,
            "archived snapshots report improvement, regression, and typed branch switches");
        Equal(backtest.AggregateMeanDeltaSeconds, -3.0,
            "archive aggregate preserves signed seconds-to-terminal residual");
    }

    public static int Main()
    {
        TestRouteEstimateAndShadowInvariant();
        TestChronologicalResources();
        TestTypedBundleConflicts();
        TestSimultaneousEventsAndDeduplication();
        TestTinyWorldMatchesExhaustive();
        TestRealWaitResetCounterexamplesAndSunkTime();
        TestDominanceAndDiscontinuities();
        TestFallbackUnknownIrreversibleAndBlocker();
        TestAdapterPermutationAndResourceRejection();
        TestTask27RolloutBridge();
        TestTraceAndArchivedBacktests();
        Console.WriteLine("Global scheduler tests passed: " + _assertions);
        return 0;
    }
}
