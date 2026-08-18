/*
FILE PURPOSE

Pure regression executable for the source-backed collection model. It loads no Unity/game assembly,
save, runtime config, or process. Goldens cover Pirate's correlated one-of-eight law, merge deficits,
unseen optional debt, once-only numeric rewards, cosmetic Pirate valuation through the immutable
loadout objective, exact-signature online cadence, Daycare ownership, capacity service state, and
the audited zero offline equipment-trial rule.
*/
using System;
using System.Linq;
using NGUInjector.Autopilot;
using NGUInjector.Managers;

internal static class CollectionModelTests
{
    private static int _assertions;

    private static void True(bool value, string message)
    {
        _assertions++;
        if (!value) throw new Exception("FAIL: " + message);
    }

    private static void False(bool value, string message) { True(!value, message); }

    private static void Equal(int expected, int actual, string message)
    {
        _assertions++;
        if (expected != actual)
            throw new Exception("FAIL: " + message + ": expected " + expected + ", got " + actual);
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        _assertions++;
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            throw new Exception("FAIL: " + message + ": expected " + expected + ", got " + actual);
    }

    private static void PirateOneOfEight()
    {
        var zone = LootSourceCatalog.OrdinaryZone(43);
        True(zone != null && zone.HasCoreSet, "zone 43 is a source-catalogued ordinary set");
        True(zone.CoreItemIds().SequenceEqual(Enumerable.Range(507, 8)),
            "Pirate core is exactly IDs 507..514");
        var normal = zone.Branches().Single(x => x.EnemyClass == LootEnemyClass.Ordinary);
        var boss = zone.Branches().Single(x => x.EnemyClass == LootEnemyClass.Boss);
        Near(.05, normal.Probability.Evaluate(1.0, 1e20), 0.0,
            "normal Pirate group probability caps at .05");
        Near(.15, boss.Probability.Evaluate(1.0, 1e20), 0.0,
            "boss Pirate group probability caps at .15");

        var outcomes = LootSourceCatalog.PirateMixedOutcomes(Enumerable.Range(507, 8).ToArray(),
            1e20, .25);
        Near(1.0, outcomes.Sum(x => x.Probability), 1e-12, "Pirate outcome mass normalizes");
        Equal(9, outcomes.Length, "Pirate branch is none plus eight mutually exclusive IDs");
        foreach (var outcome in outcomes.Skip(1))
            Equal(1, outcome.Contributions().Count(x => x > 0),
                "one zone-43 trial cannot emit two Pirate pieces");
        Near((.05 * .75 + .15 * .25) / 8.0, outcomes[1].Probability, 1e-15,
            "each Pirate ID receives one eighth of the enemy-class mixed group chance");
        Equal(3, zone.WorstCaseTransientSlots,
            "independent pendant/Looty calls remain outside the one-of-eight branch batch");
    }

    private static CollectionItemState State(int id, int level, bool dropped,
        CollectionPhysicalLocation location)
    {
        var copy = new CollectionPhysicalCopy(id, level, level, location, new object(), false);
        return CollectionItemState.Build(new CollectionItemObservation(id, false, dropped, 1,
            new[] {copy}), LootSourceCatalog.SourcesForItem(id));
    }

    private static void DeficitsAndUnseenOptionalDebt()
    {
        var almost = State(507, 99, true, CollectionPhysicalLocation.OrdinaryInventory);
        var fresh = State(508, 0, true, CollectionPhysicalLocation.OrdinaryInventory);
        Equal(1, almost.RemainingContribution, "held level 99 has contribution deficit one");
        Equal(100, fresh.RemainingContribution, "held level zero has contribution deficit 100");
        var survivor = new CollectionPhysicalCopy(509, 0, 0,
            CollectionPhysicalLocation.OrdinaryInventory, new object(), true);
        var levelFiftySource = new CollectionPhysicalCopy(509, 50, 50,
            CollectionPhysicalLocation.OrdinaryInventory, new object(), false);
        var serviced = CollectionItemState.Build(new CollectionItemObservation(509,
            false, true, 1, new[] {survivor, levelFiftySource}),
            LootSourceCatalog.SourcesForItem(509));
        Equal(51, serviced.ImmediatelyMergeableContribution,
            "a level-50 source contributes source level plus one");
        Equal(49, serviced.RemainingContribution,
            "per-ID debt includes immediately safe merge contribution");

        var optional = CollectionItemState.Build(new CollectionItemObservation(432,
            false, false, 1, new CollectionPhysicalCopy[0]),
            LootSourceCatalog.SourcesForItem(432));
        True(optional.HasSourceBackedDebt,
            "source-known optional remains debt before its first itemDropped telemetry event");
        True(optional.NeedsInitialCopy && optional.ItemDroppedTelemetry == false,
            "unseen optional separates physical acquisition from dropped telemetry");
        Equal(100, optional.RemainingContribution,
            "unseen optional retains its post-acquisition level contribution deficit");
    }

    private static OptimizationObjective Objective(bool valuesLoot)
    {
        return new OptimizationObjective("collection-test", 7,
            LoadoutObjectiveKind.AdventureProgression, "collection", 43, -1, -1, 0,
            false, valuesLoot, "full", 1.0, 1.0, .5);
    }

    private static void RewardOnceAndPirateCosmeticValue()
    {
        var pirate = LootSourceCatalog.OrdinaryZone(43).SetReward;
        True(pirate.NumericSourceExact && pirate.CosmeticOnly,
            "Pirate portrait reward is source-exact and classified cosmetic");
        Near(0.0, pirate.NativeProgressionMagnitude, 0.0,
            "portrait 66 has zero numeric progression magnitude");
        var first = CollectionRewardModel.Evaluate(pirate, false, true, 0.0, Objective(true));
        True(first.Applied, "set reward applies on the incomplete-to-complete edge");
        Near(0.0, first.TotalKnownSecondsSaved, 0.0,
            "Pirate completion alone has zero terminal seconds value");
        var usefulCompletion = CollectionRewardModel.Evaluate(pirate, false, true, 100.0,
            Objective(true));
        Near(100.0, usefulCompletion.TotalKnownSecondsSaved, 0.0,
            "Pirate completion has value only when its physical gear is loadout-useful");
        var repeated = CollectionRewardModel.Evaluate(pirate, true, true, 100.0, Objective(true));
        False(repeated.Applied, "set reward is not applied twice");
        Near(100.0, repeated.TotalKnownSecondsSaved, 0.0,
            "useful Pirate gear is valued separately even after cosmetic completion");
        var nonLootObjective = CollectionRewardModel.Evaluate(pirate, false, true, 100.0,
            Objective(false));
        Near(0.0, nonLootObjective.TotalKnownSecondsSaved, 0.0,
            "a fixed non-loot objective does not invent Pirate gear value");
        var bonusAccessories = LootSourceCatalog.GlobalSetReward("normal-bonus-accessories");
        True(bonusAccessories != null && bonusAccessories.NumericSourceExact,
            "cross-zone Normal Bonus Accessory reward is numeric source metadata");
        Near(.25, bonusAccessories.NativeProgressionMagnitude, 1e-15,
            "Normal Bonus Accessory completion carries its exact +25% drop reward");
    }

    private static CollectionCombatSignature Signature(double power, string gear)
    {
        return new CollectionCombatSignature(43, false, true, false,
            power, 20, 30, 40, 1, gear, 7);
    }

    private static void SignatureCadenceAndNoOfflineTrials()
    {
        var ledger = new CollectionCadenceLedger();
        var a = Signature(10, "507:99");
        var b = Signature(11, "507:99");
        True(ledger.Record(a, 2.0, true), "online eligible kill records cadence");
        True(ledger.Record(a, 4.0, true), "second matching online kill records cadence");
        False(ledger.Record(a, 1000.0, false), "offline time cannot record equipment cadence");
        CollectionCadenceSample sample;
        True(ledger.TryGet(a, out sample), "exact signature retrieves its cadence sample");
        Near(3.0, sample.MeanSecondsPerTrial, 0.0,
            "offline time is absent from exact online cadence mean");
        Equal(2, sample.OnlineSamples, "only confirmed online samples count");
        False(ledger.TryGet(b, out sample),
            "materially changed combat signature cannot reuse stale cadence");

        var pirateBranch = LootSourceCatalog.OrdinaryZone(43).Branches()[0];
        Near(0.0, pirateBranch.EligibleTrials(86400, 2.0, false), 0.0,
            "offline Adventure supplies zero Pirate equipment trials");
        Near(10.0, pirateBranch.EligibleTrials(20, 2.0, true), 0.0,
            "online eligible time converts to trials");
        var t12 = LootSourceCatalog.TitanZone(42).Branches()[0];
        Near(0.0, t12.EligibleTrials(86400, 3600, false), 0.0,
            "offline Titan clock progress supplies zero equipment trials");
    }

    private static void DaycareOwnershipAndCapacityService()
    {
        var daycareCopy = new CollectionPhysicalCopy(507, 50, 75,
            CollectionPhysicalLocation.Daycare, new object(), true);
        var item = CollectionItemState.Build(new CollectionItemObservation(507,
            false, true, 2, new[] {daycareCopy}), LootSourceCatalog.SourcesForItem(507));
        True(item.PhysicallyOwned && item.OwnedInDaycare,
            "Daycare object is owned collection development state");
        Equal(25, item.RemainingContribution,
            "Daycare latent effective level contributes to collection deficit");
        Equal(1, item.ProjectedPersistentSlots,
            "Daycare-simultaneous demand retains a separate future ordinary/equipped copy");
        Equal(1, item.ReferenceProtectedCopies,
            "Daycare physical identity is reference-protected service state");

        var ids = new[] {0, 0, 1, 2, 0, 0};
        var refs = new object[] {null, null, new object(), new object(), null, null};
        var topology = PhysicalTopology.CaptureOrdinary(ids, refs, 6, 2);
        var service = new CollectionServiceState(topology, new[] {item}, 3, 1);
        Equal(2, service.UsableFreeSlots,
            "reserved-prefix empty slots are excluded from collection service capacity");
        False(service.Capacity.Admitted,
            "exact three-object Pirate/optional batch plus reserve is not admitted by two slots");
        False(service.ForecastProof().Admitted,
            "capacity rejection invalidates stochastic collection forecast support");
    }

    public static int Main()
    {
        PirateOneOfEight();
        DeficitsAndUnseenOptionalDebt();
        RewardOnceAndPirateCosmeticValue();
        SignatureCadenceAndNoOfflineTrials();
        DaycareOwnershipAndCapacityService();
        Console.WriteLine("Collection model tests passed: " + _assertions + " assertions");
        return 0;
    }
}
