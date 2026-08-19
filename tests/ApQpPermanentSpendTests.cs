/*
FILE PURPOSE

Purpose: ApQpPermanentSpendTests protects the exact AP/QP catalog surface, source-pinned quirk
binding, downstream-seconds planner, and strict debit/effect postcondition kernels.

Mechanism: in-memory immutable snapshots exercise stale/shadow quote rejection, reserve admission,
cross-currency ranking, AP selector restoration, complete quirk-vector settlement, and fail-closed
deferred END delivery. Metadata inspection checks the installed tryLevelUp token without Unity.

Inputs and outputs: inputs are maintained source types and the read-only Assembly-CSharp reference;
output is an assertion count/process status. The suite never creates Character, invokes a native
controller, mutates a save, builds the bot, or deploys/injects anything.

Invariants and safety: consumables, Hearts, conversions and Starter Pack remain outside live AP
authority; shadow/stale values cannot select a purchase; one wrong currency unit, selector, target
level, or unrelated quirk level rejects settlement.

Extension points and non-goals: copied-save live invocation remains an integration backtest. Add a
golden here whenever another AP atom is moved onto the source-exact persistent surface.
*/
using System;
using System.Collections.Generic;
using NGUInjector.Autopilot;

internal static class ApQpPermanentSpendTests
{
    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static ExactPermanentSpendSnapshot Snapshot(ExactPermanentCurrency currency,
        int id, long balance, long cost, IDictionary<string, long> state,
        bool eligible = true, int selector = 41, string selectorName = "ambient",
        bool endPresent = false, LootCapacityProof capacity = null)
    {
        PurchaseDescriptor descriptor = null;
        var method = currency == ExactPermanentCurrency.ArbitraryPoints
                     && PurchaseDescriptorCatalog.TryGetAp(id, out descriptor)
            ? descriptor.NativeMethodName : "BeastQuestPerkController.tryLevelUp";
        var token = currency == ExactPermanentCurrency.ArbitraryPoints && descriptor != null
            ? descriptor.MetadataToken : 0x0600051c;
        return new ExactPermanentSpendSnapshot(currency, id, method, token,
            PurchaseDescriptorCatalog.AuditedGameSha256,
            PurchaseDescriptorCatalog.AuditedGameMvid, balance, cost,
            currency == ExactPermanentCurrency.ArbitraryPoints ? selector : -1,
            currency == ExactPermanentCurrency.ArbitraryPoints ? selectorName : string.Empty,
            state, eligible, eligible ? string.Empty : "held", endPresent, capacity);
    }

    private static PermanentSpendMarginalQuote Quote(ExactPermanentSpendSnapshot snapshot,
        double seconds, PermanentSpendValueEvidence evidence =
            PermanentSpendValueEvidence.SourceExactDownstreamProjection)
    {
        return new PermanentSpendMarginalQuote(snapshot.Currency, snapshot.NativeId,
            snapshot.ExactCost, snapshot.BoundaryFingerprint(), seconds, evidence,
            "exact-route-fixture");
    }

    private static void TestConservativeApSurface()
    {
        var allowed = new HashSet<int>(new[]
        {
            7,8,9,12,13,15,17,21,22,25,28,29,32,34,39,40,41,47,48,49,54,
            55,56,57,58,62,64,65,66,67,68,69,71,72,73,74,75,76,77,81
        });
        for (var id = 0; id <= 81; id++)
        {
            PurchaseDescriptor descriptor;
            Assert(PurchaseDescriptorCatalog.TryGetAp(id, out descriptor), "catalog AP " + id);
            Assert(PurchaseDescriptorCatalog.IsSourceExactLiveApPermanent(descriptor)
                   == allowed.Contains(id), "exact persistent AP authorization " + id);
        }
        PurchaseDescriptor custom;
        PurchaseDescriptorCatalog.TryGetAp(12, out custom);
        var effects = custom.Effects();
        Assert(effects.Length == 2
               && effects[0].StateKey == "ap.hasCustomEnergyPercent1"
               && effects[1].StateKey == "ap.hasCustomMagicPercent1",
            "custom-percent AP captures both exact native Boolean writes");
        Assert(!PurchaseDescriptorCatalog.IsSourceExactLiveApPermanent(
                PurchaseDescriptorCatalog.AllAp()[16]),
            "Starter Pack multi-effect preview remains fail closed");
        Assert(!PurchaseDescriptorCatalog.IsSourceExactLiveApPermanent(
                PurchaseDescriptorCatalog.AllAp()[14]),
            "Heart physical delivery remains fail closed");
    }

    private static void TestPinnedQuirkBinding()
    {
        var registry = NativeBindingRegistry.Create(typeof(Character).Assembly,
            NativeBindingRegistry.AuditedGameSha256);
        NativeBindingDescriptor descriptor;
        Assert(registry.TryGetDescriptor(NativeBindingKeys.QuirkTryLevelUp, out descriptor),
            "quirk tryLevelUp binding is catalogued");
        Assert(descriptor.DeclaringTypeName == "BeastQuestPerkController"
               && descriptor.MemberName == "tryLevelUp"
               && descriptor.MetadataToken == 0x0600051c
               && descriptor.ParameterTypeNames.Length == 1
               && descriptor.ParameterTypeNames[0] == "System.Int32"
               && descriptor.Scope == NativeBindingScope.IrreversibleMutation,
            "quirk binding pins exact public one-level gate primitive");
    }

    private static void TestExactDownstreamPlanner()
    {
        var ap = Snapshot(ExactPermanentCurrency.ArbitraryPoints, 9, 50000, 10000,
            new Dictionary<string, long> { { "ap.hasInstaTraining", 0 } });
        var qp = Snapshot(ExactPermanentCurrency.QuestPoints, 3, 100, 20,
            new Dictionary<string, long>
            {
                { "quirk.level.0", 0 }, { "quirk.level.1", 2 },
                { "quirk.level.2", 0 }, { "quirk.level.3", 0 }
            });
        var chosen = ExactPermanentSpendPlanner.Choose(new[] { ap, qp },
            new[] { Quote(ap, 30), Quote(qp, 90) }, 0, 0);
        Assert(chosen.Planned && chosen.Plan.Before.Currency
               == ExactPermanentCurrency.QuestPoints,
            "planner chooses larger exact downstream seconds, not AP ID/name ordering");

        var shadow = ExactPermanentSpendPlanner.Choose(new[] { ap },
            new[] { Quote(ap, 1000, PermanentSpendValueEvidence.ShadowOnly) }, 0, 0);
        Assert(!shadow.Planned, "shadow-only value cannot authorize permanent spending");
        var stale = new PermanentSpendMarginalQuote(ap.Currency, ap.NativeId, ap.ExactCost,
            ap.BoundaryFingerprint() + "/stale", 1000,
            PermanentSpendValueEvidence.SourceExactDownstreamProjection, "fixture");
        Assert(!ExactPermanentSpendPlanner.Choose(new[] { ap }, new[] { stale }, 0, 0).Planned,
            "stale boundary quote fails closed");
        Assert(!ExactPermanentSpendPlanner.Choose(new[] { ap }, new[] { Quote(ap, 30) },
                45000, 0).Planned,
            "AP reserve is checked against exact post-debit balance");
        Assert(ExactPermanentSpendPlanner.Choose(new[] { ap }, new[] { Quote(ap, 30) },
                40000, 0).Planned,
            "AP reserve equality is affordable like native currency comparison");
    }

    private static void TestExactTransitions()
    {
        var before = Snapshot(ExactPermanentCurrency.ArbitraryPoints, 9, 50000, 10000,
            new Dictionary<string, long> { { "ap.hasInstaTraining", 0 } });
        var after = Snapshot(ExactPermanentCurrency.ArbitraryPoints, 9, 40000, 10000,
            new Dictionary<string, long> { { "ap.hasInstaTraining", 1 } });
        string reason;
        Assert(ExactPermanentSpendTransitions.Verify(before, after, out reason),
            "AP exact debit plus Boolean set commits");
        var badDebit = Snapshot(ExactPermanentCurrency.ArbitraryPoints, 9, 40001, 10000,
            new Dictionary<string, long> { { "ap.hasInstaTraining", 1 } });
        Assert(!ExactPermanentSpendTransitions.Verify(before, badDebit, out reason),
            "one AP debit error rejects settlement");
        var badSelector = Snapshot(ExactPermanentCurrency.ArbitraryPoints, 9, 40000, 10000,
            new Dictionary<string, long> { { "ap.hasInstaTraining", 1 } },
            true, 42, "ambient");
        Assert(!ExactPermanentSpendTransitions.Verify(before, badSelector, out reason),
            "AP selector drift rejects settlement");

        var qpBefore = Snapshot(ExactPermanentCurrency.QuestPoints, 3, 100, 20,
            new Dictionary<string, long>
            {
                { "quirk.level.0", 1 }, { "quirk.level.1", 2 },
                { "quirk.level.2", 3 }, { "quirk.level.3", 4 }
            });
        var qpAfter = Snapshot(ExactPermanentCurrency.QuestPoints, 3, 80, 20,
            new Dictionary<string, long>
            {
                { "quirk.level.0", 1 }, { "quirk.level.1", 2 },
                { "quirk.level.2", 3 }, { "quirk.level.3", 5 }
            });
        Assert(ExactPermanentSpendTransitions.Verify(qpBefore, qpAfter, out reason),
            "QP exact debit and one target level commits");
        var qpCollateral = Snapshot(ExactPermanentCurrency.QuestPoints, 3, 80, 20,
            new Dictionary<string, long>
            {
                { "quirk.level.0", 1 }, { "quirk.level.1", 3 },
                { "quirk.level.2", 3 }, { "quirk.level.3", 5 }
            });
        Assert(!ExactPermanentSpendTransitions.Verify(qpBefore, qpCollateral, out reason),
            "unrelated quirk-level mutation rejects settlement");
    }

    private static void TestEndCapacityFingerprint()
    {
        var occupied = new object();
        var topology = PhysicalTopology.CaptureOrdinary(new[] { 1, 0 },
            new[] { occupied, null }, 2, 1);
        var proof = LootCapacity.ProveOrdinary(topology,
            LootCapacityRequirement.ExactUniqueDelivery("end-quirk-176-item-486", 0, 1, 0));
        Assert(proof.Admitted && proof.UsableStart == 1 && proof.UsableFreeSlotCount == 1,
            "quirk 176 uses exact ordinary native scan interval");
        var end = Snapshot(ExactPermanentCurrency.QuestPoints, 176, 1000, 100,
            new Dictionary<string, long> { { "quirk.level.176", 0 } },
            true, -1, string.Empty, false, proof);
        Assert(end.BoundaryFingerprint().Contains("capacity=1:2:1:1"),
            "END capacity is quote- and boundary-fingerprinted");
        Assert(!ExactPermanentSpendTransitions.DeferredEndDeliveryIsSettled(end),
            "a point-in-time slot cannot authorize quirk 176 across the deferred checker delay");
        var delivered = Snapshot(ExactPermanentCurrency.QuestPoints, 176, 1000, 100,
            new Dictionary<string, long> { { "quirk.level.176", 0 } },
            true, -1, string.Empty, true, null);
        Assert(ExactPermanentSpendTransitions.DeferredEndDeliveryIsSettled(delivered),
            "an already ordinary item 486 removes the deferred-delivery hazard");
    }

    public static int Main()
    {
        TestConservativeApSurface();
        TestPinnedQuirkBinding();
        TestExactDownstreamPlanner();
        TestExactTransitions();
        TestEndCapacityFingerprint();
        Console.WriteLine("PASS: AP/QP permanent spending (" + _assertions + " assertions)");
        return 0;
    }
}
