/*
FILE PURPOSE

Purpose: ProgressionGraphTests is the dependency-free regression suite for reconciled task 27. It
proves the immutable optimization boundary, deterministic identity-bound hash, typed invalidation
surface, exact END/challenge terminal predicate, source-backed terminal DAG, parallel max/slack
calculus, evidence provenance, and shared-resource declarations.

Mechanism: Pure fixtures construct complete typed snapshots and feed deterministic per-node work
estimates to ProgressionDependencyGraph. Assertions inspect enum keys, gates, rewards, dependencies,
branch evaluations, and invalidations directly; no ID, label, or strategy text is parsed.

Inputs and outputs: The executable has no external inputs. It prints one assertion count or throws
on the first failed invariant. It loads no game assembly, Unity controller, save, config, or runtime.

Invariants and safety: Exactly one ordinary copy—not a source flag or recoverable copy—satisfies an
END item. All configured challenge targets are mandatory. Every stationarity stamp and hard-gate
fact invalidates by typed key. Unknown estimates remain incomplete and cannot masquerade as a zero
ETA. Evaluation is read-only and all caller-owned arrays are copied.

Extension points and non-goals: Add a golden whenever the terminal catalog or scheduler state enum
grows. Live capture, transition search, resource scheduling, END authority, and controller mutation
are deliberately outside this isolated suite.
*/
using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Autopilot;

internal static class ProgressionGraphTests
{
    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static void Equal(double actual, double expected, string message)
    {
        Assert(Math.Abs(actual - expected) < 0.000001,
            message + " (actual " + actual + ", expected " + expected + ")");
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        _assertions++;
        try { action(); }
        catch (T) { return; }
        throw new Exception("FAIL: " + message);
    }

    private static OptimizationIdentity Identity(string session = "session-A",
        string save = "save-A", string model = "model-A",
        string objective = "objective-A")
    {
        return new OptimizationIdentity(session, save, model, objective);
    }

    private static OptimizationStateStamp[] Stamps(
        OptimizationStateKey? changed = null)
    {
        return OptimizationSnapshot.AllStateKeys().Select(key =>
            new OptimizationStateStamp(key,
                "stamp-" + (int)key + (changed == key ? "-changed" : ""))).ToArray();
    }

    private static OptimizationFactSet Facts(
        IDictionary<OptimizationFactKey, double> overrides = null)
    {
        return new OptimizationFactSet(OptimizationSnapshot.AllFactKeys().Select(key =>
        {
            double value;
            return new OptimizationFact(key,
                overrides != null && overrides.TryGetValue(key, out value) ? value : 0.0);
        }));
    }

    private static OptimizationEndItemState[] Items(int ordinaryCopies,
        int changedItem = -1, int changedOrdinary = -1, int changedRecoverable = 0,
        bool sourceSatisfied = true, bool pendingGrant = false, bool retryLegal = true)
    {
        var result = new List<OptimizationEndItemState>();
        for (var itemId = MechanicsEndgame.FirstEndItemId;
             itemId <= MechanicsEndgame.LastEndItemId; itemId++)
            result.Add(new OptimizationEndItemState(itemId,
                itemId == changedItem && changedOrdinary >= 0
                    ? changedOrdinary : ordinaryCopies,
                itemId == changedItem ? changedRecoverable : 0,
                itemId == changedItem ? sourceSatisfied : true,
                itemId == changedItem && pendingGrant,
                itemId != changedItem || retryLegal));
        return result.ToArray();
    }

    private static OptimizationChallengeState[] Challenges(bool allRequired,
        int completed, OptimizationChallengeKind? changed = null,
        int changedCompleted = 0, bool changedRequired = true)
    {
        return OptimizationSnapshot.AllChallengeKinds().Select(kind =>
            new OptimizationChallengeState(kind,
                changed == kind ? changedRequired : allRequired,
                changed == kind ? changedCompleted : completed,
                changed == kind ? (changedRequired ? 1 : 0) : (allRequired ? 1 : 0)))
            .ToArray();
    }

    private static OptimizationSnapshot Snapshot(long captureVersion = 1L,
        OptimizationIdentity identity = null,
        OptimizationDifficulty difficulty = OptimizationDifficulty.Sadistic,
        OptimizationStateStamp[] stamps = null, OptimizationFactSet facts = null,
        OptimizationEndItemState[] items = null,
        OptimizationChallengeState[] challenges = null,
        bool endVerified = false)
    {
        return new OptimizationSnapshot(captureVersion, identity ?? Identity(), difficulty,
            stamps ?? Stamps(), facts ?? Facts(ReadyFacts()), items ?? Items(0),
            challenges ?? Challenges(false, 0), endVerified);
    }

    private static Dictionary<OptimizationFactKey, double> ReadyFacts()
    {
        var values = OptimizationSnapshot.AllFactKeys()
            .ToDictionary(key => key, key => 0.0);
        values[OptimizationFactKey.HighestSadisticBoss] = 300.0;
        values[OptimizationFactKey.Titan13Defeated] = 1.0;
        values[OptimizationFactKey.HacksZeroThroughFourteenCapped] = 1.0;
        values[OptimizationFactKey.EndHackLevel] = 1.0;
        values[OptimizationFactKey.Move69Unlocked] = 1.0;
        values[OptimizationFactKey.Move69Uses] = 69.0;
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
        return values;
    }

    private static ProgressionWorkEstimate Estimate(ProgressionNodeKey node,
        double mean, ProgressionEstimateProvenance provenance =
            ProgressionEstimateProvenance.SourceKnown)
    {
        return new ProgressionWorkEstimate(node, mean, mean + 1.0,
            Math.Max(0.0, mean - 1.0), mean + 2.0, provenance,
            provenance == ProgressionEstimateProvenance.Empirical ? 20 : 0,
            provenance == ProgressionEstimateProvenance.Empirical ? 0.9 : 1.0);
    }

    private static ProgressionNodeKey ItemNode(int itemId)
    {
        return (ProgressionNodeKey)((int)ProgressionNodeKey.EndItem480
                                    + itemId - MechanicsEndgame.FirstEndItemId);
    }

    private static ProgressionNodeKey ChallengeNode(OptimizationChallengeKind kind)
    {
        return (ProgressionNodeKey)((int)ProgressionNodeKey.ChallengeBasic + (int)kind);
    }

    private static void TestSnapshotCompletenessImmutabilityAndHash()
    {
        var stamps = Stamps();
        var items = Items(1);
        var challenges = Challenges(true, 1);
        var snapshot = Snapshot(7L, stamps: stamps, items: items,
            challenges: challenges, endVerified: true);
        var hash = snapshot.SnapshotHash;
        stamps[0] = new OptimizationStateStamp(OptimizationStateKey.Difficulty, "mutated");
        items[0] = new OptimizationEndItemState(480, 0, 1, true, false, true);
        challenges[0] = new OptimizationChallengeState(
            OptimizationChallengeKind.Basic, true, 0, 1);
        Assert(snapshot.SnapshotHash == hash && snapshot.TerminalSatisfied,
            "snapshot copies all caller-owned arrays before hashing and evaluation");

        var recaptured = Snapshot(99L, items: Items(1),
            challenges: Challenges(true, 1), endVerified: true);
        Assert(snapshot.SnapshotHash == recaptured.SnapshotHash
               && snapshot.CanReusePlanFor(recaptured),
            "capture sequence is not semantic state and does not perturb the stable hash");

        var missingStamp = Stamps().Take(Stamps().Length - 1).ToArray();
        Throws<ArgumentException>(() => Snapshot(stamps: missingStamp),
            "snapshot rejects a missing named stationarity category");
        var duplicateStamps = Stamps().Concat(new[] {Stamps()[0]}).ToArray();
        Throws<ArgumentException>(() => Snapshot(stamps: duplicateStamps),
            "snapshot rejects duplicate stationarity categories");
        Throws<ArgumentException>(() => new OptimizationFactSet(
                OptimizationSnapshot.AllFactKeys().Skip(1).Select(
                    key => new OptimizationFact(key, 0.0))),
            "fact set rejects a missing typed hard-gate observation");
        Throws<ArgumentException>(() => Snapshot(items: Items(0).Take(15).ToArray()),
            "snapshot requires all sixteen END item records");
        Throws<ArgumentException>(() => Snapshot(
                challenges: Challenges(false, 0).Take(10).ToArray()),
            "snapshot requires all eleven challenge ledgers");
    }

    private static void TestTypedInvalidationSurface()
    {
        var baseline = Snapshot();
        foreach (var key in OptimizationSnapshot.AllStateKeys())
        {
            var changed = Snapshot(stamps: Stamps(key));
            var invalidations = baseline.InvalidationsComparedTo(changed);
            Assert(invalidations.Length == 1
                   && invalidations[0].Kind == OptimizationInvalidationKind.NamedState
                   && invalidations[0].StateKey == key,
                "named state invalidates by typed key " + key);
        }
        var difficultyChanged = Snapshot(difficulty: OptimizationDifficulty.Evil);
        var difficultyDiff = baseline.InvalidationsComparedTo(difficultyChanged);
        Assert(difficultyDiff.Length == 1
               && difficultyDiff[0].Kind == OptimizationInvalidationKind.NamedState
               && difficultyDiff[0].StateKey == OptimizationStateKey.Difficulty,
            "typed difficulty cannot change behind an unchanged integration stamp");

        foreach (var key in OptimizationSnapshot.AllFactKeys())
        {
            var values = ReadyFacts();
            values[key] = values[key] == 0.0 ? 0.5 : values[key] * 1.1;
            var changed = Snapshot(facts: Facts(values));
            var invalidations = baseline.InvalidationsComparedTo(changed);
            Assert(invalidations.Length == 1
                   && invalidations[0].Kind == OptimizationInvalidationKind.HardGateFact
                   && invalidations[0].FactKey == key,
                "hard-gate observation invalidates by typed key " + key);
        }

        AssertOneIdentityInvalidation(baseline,
            Snapshot(identity: Identity("session-B", "save-A", "model-A", "objective-A")),
            OptimizationInvalidationKind.Session);
        AssertOneIdentityInvalidation(baseline,
            Snapshot(identity: Identity("session-A", "save-B", "model-A", "objective-A")),
            OptimizationInvalidationKind.SaveHash);
        AssertOneIdentityInvalidation(baseline,
            Snapshot(identity: Identity("session-A", "save-A", "model-B", "objective-A")),
            OptimizationInvalidationKind.ModelHash);
        AssertOneIdentityInvalidation(baseline,
            Snapshot(identity: Identity("session-A", "save-A", "model-A", "objective-B")),
            OptimizationInvalidationKind.ObjectiveHash);

        var itemChanged = Snapshot(items: Items(0, 487, 0, 1, true, true, false));
        var itemDiff = baseline.InvalidationsComparedTo(itemChanged);
        Assert(itemDiff.Length == 1
               && itemDiff[0].Kind
               == OptimizationInvalidationKind.EndPhysicalOrSourceState
               && itemDiff[0].ItemId == 487,
            "recoverability/source/pending/retry item state invalidates by END item ID");

        var challengeChanged = Snapshot(challenges: Challenges(false, 0,
            OptimizationChallengeKind.NoNgu, 0, true));
        var challengeDiff = baseline.InvalidationsComparedTo(challengeChanged);
        Assert(challengeDiff.Length == 1
               && challengeDiff[0].Kind == OptimizationInvalidationKind.ChallengeLedger
               && challengeDiff[0].Challenge == OptimizationChallengeKind.NoNgu,
            "objective challenge changes invalidate by typed challenge kind");

        var endChanged = Snapshot(endVerified: true);
        var endDiff = baseline.InvalidationsComparedTo(endChanged);
        Assert(endDiff.Length == 1
               && endDiff[0].Kind == OptimizationInvalidationKind.EndSequence,
            "verified END transition has its own typed invalidation");
        Assert(!baseline.CanReusePlanFor(endChanged),
            "any typed difference rejects plan reuse");
    }

    private static void AssertOneIdentityInvalidation(OptimizationSnapshot baseline,
        OptimizationSnapshot changed, OptimizationInvalidationKind expected)
    {
        var invalidations = baseline.InvalidationsComparedTo(changed);
        Assert(invalidations.Length == 1 && invalidations[0].Kind == expected,
            expected + " changes independently invalidate the snapshot");
    }

    private static void TestExactTerminalPredicate()
    {
        var complete = Snapshot(items: Items(1), challenges: Challenges(true, 1),
            endVerified: true);
        Assert(complete.TerminalSatisfied,
            "verified END plus every exact physical item and required challenge is terminal");
        Assert(!Snapshot(items: Items(1, 480, 0, 1),
                challenges: Challenges(true, 1), endVerified: true).TerminalSatisfied,
            "a recoverable END copy is not an ordinary physical terminal item");
        Assert(!Snapshot(items: Items(1, 480, 0, 0, true),
                challenges: Challenges(true, 1), endVerified: true).TerminalSatisfied,
            "source completion without a physical copy is not terminal");
        Assert(!Snapshot(items: Items(1, 480, 2),
                challenges: Challenges(true, 1), endVerified: true).TerminalSatisfied,
            "duplicate ordinary END copies fail exact-one terminal ownership");
        Assert(!Snapshot(items: Items(1), challenges: Challenges(true, 1,
                OptimizationChallengeKind.Troll, 0, true), endVerified: true)
                .TerminalSatisfied,
            "one incomplete required challenge keeps the objective open");
        Assert(Snapshot(items: Items(1), challenges: Challenges(true, 1,
                OptimizationChallengeKind.Troll, 0, false), endVerified: true)
                .TerminalSatisfied,
            "an explicitly optional challenge is not silently made mandatory");
        Assert(!Snapshot(items: Items(1), challenges: Challenges(true, 1),
                endVerified: false).TerminalSatisfied,
            "physical layout alone does not replace verified END UI state");
    }

    private static void TestGraphCatalogGoldens()
    {
        var graph = ProgressionDependencyGraph.CreateTerminalGraph();
        Assert(graph.Nodes().Length == Enum.GetValues(typeof(ProgressionNodeKey)).Length,
            "terminal DAG contains exactly one node for every typed catalog key");
        foreach (var node in graph.Nodes())
            Assert(node.Kind == ProgressionNodeKind.Composite
                   || node.Gate.Kind != ProgressionGateKind.DependenciesOnly,
                "every non-composite hard gate is typed: " + node.Key);

        for (var itemId = 480; itemId <= 495; itemId++)
        {
            var node = graph.Node(ItemNode(itemId));
            var rewards = node.Rewards();
            Assert(node.Gate.Kind == ProgressionGateKind.OrdinaryEndItemExactlyOne
                   && node.Gate.EndItemId == itemId,
                "END branch gate is exact-one ordinary item " + itemId);
            Assert(rewards.Length == 1
                   && rewards[0].Kind == ProgressionRewardKind.OrdinaryEndItem
                   && rewards[0].EndItemId == itemId,
                "END branch reward is typed item " + itemId);
        }

        foreach (var challenge in OptimizationSnapshot.AllChallengeKinds())
        {
            var node = graph.Node(ChallengeNode(challenge));
            Assert(node.Gate.Kind == ProgressionGateKind.RequiredChallengeComplete
                   && node.Gate.Challenge == challenge,
                "challenge ledger gate is typed: " + challenge);
            Assert(node.Dependencies().Length == 0,
                "required challenge is a parallel objective branch, not Sadistic-serialized");
        }

        AssertThreshold(graph, ProgressionNodeKey.Titan12CapacityFor483,
            OptimizationFactKey.UsableInventoryFreeSlots, 11.0);
        AssertThreshold(graph, ProgressionNodeKey.Titan12CapacityFor489,
            OptimizationFactKey.UsableInventoryFreeSlots, 14.0);
        AssertThreshold(graph, ProgressionNodeKey.Titan12CapacityFor493,
            OptimizationFactKey.UsableInventoryFreeSlots, 16.0);
        AssertThreshold(graph, ProgressionNodeKey.Titan12CapacityFor484,
            OptimizationFactKey.UsableInventoryFreeSlots, 18.0);
        AssertThreshold(graph, ProgressionNodeKey.FinalInventoryLayoutCapacity,
            OptimizationFactKey.OrdinaryInventoryCurrentSpaces, 40.0);
        AssertThreshold(graph, ProgressionNodeKey.EndCardDeckCapacity,
            OptimizationFactKey.DeckFreeSlots, 2.0);
        var mayo = graph.Node(ProgressionNodeKey.EndCardMayoReserve).Gate;
        Assert(mayo.FactKeys().Length == 6
               && mayo.Thresholds().All(value => value == 99.0),
            "END Card gate reserves 99 of each of six typed Mayo resources");

        var checker = graph.Node(ProgressionNodeKey.EndItem487).Dependencies();
        Assert(checker.Contains(ProgressionNodeKey.SadisticBoss300)
               && checker.Contains(ProgressionNodeKey.SadisticBoss225)
               && checker.Contains(ProgressionNodeKey.OneUsableInventorySlot)
               && checker.Contains(ProgressionNodeKey.EndFiltersClear),
            "checker item 487 keeps boss-source plus checker delivery/capacity/filter gates");
        var final = graph.Node(ProgressionNodeKey.EndSequence).Dependencies();
        Assert(final.Length == 19
               && final.Contains(ProgressionNodeKey.FinalInventoryLayoutCapacity)
               && final.Contains(ProgressionNodeKey.SadisticBoss300)
               && final.Contains(ProgressionNodeKey.Titan13Defeated),
            "END sequence requires sixteen pieces, 40-space layout, boss 300, and T13");

        var clone = graph.Nodes();
        clone[0] = null;
        Assert(graph.Node(ProgressionNodeKey.Terminal) != null,
            "graph catalog access returns a defensive copy");
    }

    private static void AssertThreshold(ProgressionDependencyGraph graph,
        ProgressionNodeKey nodeKey, OptimizationFactKey factKey, double threshold)
    {
        var gate = graph.Node(nodeKey).Gate;
        Assert(gate.FactKeys().Length == 1 && gate.FactKeys()[0] == factKey
               && gate.Thresholds()[0] == threshold,
            nodeKey + " has exact typed threshold " + threshold);
    }

    private static void TestParallelMaxSlackProvenanceAndResources()
    {
        var graph = ProgressionDependencyGraph.CreateTerminalGraph();
        var estimates = new List<ProgressionWorkEstimate>();
        for (var itemId = 480; itemId <= 495; itemId++)
        {
            var seconds = itemId == 491 ? 100.0 : 10.0 + itemId - 480;
            estimates.Add(Estimate(ItemNode(itemId), seconds,
                itemId == 491 ? ProgressionEstimateProvenance.Empirical
                    : ProgressionEstimateProvenance.SourceKnown));
        }
        estimates.Add(Estimate(ProgressionNodeKey.ChallengeBasic, 80.0));
        estimates.Add(Estimate(ProgressionNodeKey.EndSequence, 5.0));
        var snapshot = Snapshot(items: Items(0), challenges: Challenges(false, 0,
            OptimizationChallengeKind.Basic, 0, true));
        var evaluation = graph.Evaluate(snapshot, estimates);
        Equal(evaluation.ParallelHorizonSeconds, 100.0,
            "parallel END/challenge horizon is the maximum branch finish, never the sum");
        Assert(evaluation.CriticalBranch == ProgressionNodeKey.EndItem491,
            "the maximum branch itself is the typed critical branch");
        var branches = evaluation.ParallelBranches();
        var item480 = branches.Single(x => x.Node == ProgressionNodeKey.EndItem480);
        var item491 = branches.Single(x => x.Node == ProgressionNodeKey.EndItem491);
        var basic = branches.Single(x => x.Node == ProgressionNodeKey.ChallengeBasic);
        Equal(item480.SlackSeconds, 90.0, "short END branch publishes parallel slack");
        Equal(item491.SlackSeconds, 0.0, "critical END branch has zero slack");
        Equal(basic.SlackSeconds, 20.0, "required challenge shares the same parallel horizon");
        Assert(item491.Provenance == ProgressionEstimateProvenance.Empirical
               && evaluation.Terminal.Provenance
               == ProgressionEstimateProvenance.Empirical,
            "empirical evidence is explicit and propagates through the terminal path");
        Equal(evaluation.EndSequence.MeanSeconds, 105.0,
            "END transaction adds only after the slowest physical branch");
        Equal(evaluation.Terminal.MeanSeconds, 105.0,
            "terminal is max(END sequence, required challenge), not their sum");
        Assert(evaluation.Terminal.ModelComplete && !evaluation.Terminal.GateSatisfied,
            "fully estimated unfinished terminal remains modeled but unsatisfied");

        var resources = evaluation.SharedResources();
        var adventure = resources.Single(x =>
            x.Resource == ProgressionSharedResourceKind.AdventureMode);
        Assert(adventure.OutstandingClaimCount > 1 && adventure.ExclusiveClaimCount > 0,
            "shared Adventure mode contention is declared instead of assumed independent");
        Assert(adventure.TouchesCriticalBranch,
            "resource summaries include the typed critical branch dependency closure");
        var inventory = resources.Single(x =>
            x.Resource == ProgressionSharedResourceKind.OrdinaryInventoryCapacity);
        Assert(inventory.OutstandingClaimCount > 1,
            "shared ordinary inventory claims remain visible to task-28 scheduling");
    }

    private static void TestUnknownEstimateAndVerifiedEndGuard()
    {
        var graph = ProgressionDependencyGraph.CreateTerminalGraph();
        var missing = graph.Evaluate(Snapshot(), new ProgressionWorkEstimate[0]);
        Assert(!missing.Terminal.ModelComplete
               && double.IsPositiveInfinity(missing.ParallelHorizonSeconds)
               && missing.ParallelBranches().All(x => x.SlackSeconds < 0.0),
            "missing work evidence produces incomplete model and unknown slack, never zero ETA");

        var estimates = new List<ProgressionWorkEstimate>();
        for (var itemId = 480; itemId <= 495; itemId++)
            estimates.Add(Estimate(ItemNode(itemId), 1.0));
        var falseVerified = Snapshot(items: Items(1, 480, 0),
            challenges: Challenges(true, 1), endVerified: true);
        var guarded = graph.Evaluate(falseVerified, estimates);
        Assert(!guarded.EndSequence.GateSatisfied && !guarded.Terminal.GateSatisfied,
            "verified-END flag cannot mask a missing physical END item");

        var completed = graph.Evaluate(Snapshot(items: Items(1),
            challenges: Challenges(true, 1), endVerified: true),
            new ProgressionWorkEstimate[0]);
        Assert(completed.EndSequence.GateSatisfied
               && completed.Terminal.GateSatisfied
               && completed.Terminal.ModelComplete,
            "graph terminal agrees with the exact completed snapshot without estimates");

        Throws<ArgumentException>(() => new ProgressionWorkEstimate(
                ProgressionNodeKey.EndItem491, 10.0, 11.0, 9.0, 12.0,
                ProgressionEstimateProvenance.Empirical),
            "empirical evidence requires samples and confidence");
    }

    public static int Main()
    {
        TestSnapshotCompletenessImmutabilityAndHash();
        TestTypedInvalidationSurface();
        TestExactTerminalPredicate();
        TestGraphCatalogGoldens();
        TestParallelMaxSlackProvenanceAndResources();
        TestUnknownEstimateAndVerifiedEndGuard();
        Console.WriteLine("Progression graph tests passed: " + _assertions);
        return 0;
    }
}
