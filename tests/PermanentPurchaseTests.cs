/*
FILE PURPOSE

PermanentPurchaseTests is the isolated pure/fault-injection suite for task 21.  It checks all AP
IDs 0-81 against installed-build method/token/cost goldens, every Heart mapping, AP/EXP formula
boundaries, ID-69, method/ID split-brain rejection before dispatch, unknown-MVID read-only behavior,
exact dynamic reserves, direct ITOPOD AP semantics, Heart filter/capacity handling, exact
currency/effect settlement, quarantine on partial effects, default-disabled authority, and the
one-atom/replan latch.  It loads no game assembly, controller, save, Unity UI, or runtime process.
*/
using System;
using System.Collections.Generic;
using NGUInjector.Autopilot;

namespace NGUInjector.Autopilot
{
    // Minimal task-1 policy stubs for this standalone executable.
    internal sealed class AutopilotConfig
    {
        internal bool Enabled = true;
        internal string Mode = "full";
        internal bool AutoEnterGame = true;
        internal bool AllowLegacyFallback = true;
        internal bool ManageAllocations = true;
        internal bool ManageBosses = true;
        internal bool ManageAdventure = true;
        internal bool ManageInventory = true;
        internal bool ManageDiggers = true;
        internal bool ManageYggdrasil = true;
        internal bool ManageQuests = true;
        internal bool ManageWishes = true;
        internal bool ManageCards = true;
        internal bool ManageCooking = true;
        internal bool ManageMoneyPit = true;
        internal bool ManageDailySpin = true;
        internal bool ManageBloodMagic = true;
        internal bool ManageBeards = true;
        internal bool AllowExpSpending = true;
        internal bool AllowApSpending = true;
        internal bool AllowPerkSpending = true;
        internal bool AllowQuirkSpending = true;
        internal bool AllowCardYeeting = true;
        internal bool AllowRebirths = true;
        internal bool AllowChallenges = true;
        internal bool AllowDifficultyExecution;
        internal bool AllowEndSequence = false;

        internal bool IsDryRun { get { return Mode != "assist" && Mode != "full"; } }
        internal bool IsAssist { get { return Mode == "assist"; } }
        internal bool IsFull { get { return Mode == "full"; } }

        internal string ExecutionFingerprint()
        {
            return Enabled + "|" + Mode + "|" + AllowExpSpending + "|" + AllowApSpending;
        }
    }

    internal sealed class AutopilotManager
    {
        internal AutopilotConfig Config;
    }
}

internal static class PermanentPurchaseTests
{
    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static void Equal(long actual, long expected, string message)
    {
        Assert(actual == expected, message + " (actual " + actual + ", expected " + expected + ")");
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        _assertions++;
        try { action(); }
        catch (T) { return; }
        throw new Exception("FAIL: " + message);
    }

    private static readonly string[] ApMethods =
    {
        "buyEnergyPotion1AP", "buyEnergyPotion2AP", "buyMagicPotion1AP", "buyMagicPotion2AP",
        "buyLootCharm1AP", "buyEnergyBarBar1AP", "buyMagicBarBar1AP", "buyLootFilterAP",
        "buyAutoBoostMergeAP", "buyInstaTrainAP", "buy500ExpAP", "buyHeartAP",
        "buyCustomPercent1AP", "buyCustomPercent2AP", "buyYellowHeartAP", "buyInventoryAP",
        "buyStarterPackAP", "buyAcc4AP", "buyPoop1AP", "buyPoop10AP", "buyPoop100AP",
        "buyYggReminderAP", "buyExtendedSpinBankAP", "buy200ExpAP", "buy2KExpAP",
        "buyLoadoutSlotAP", "buyEnergyPotion3", "buyMagicPotion3", "buyBeardAP",
        "buyCubeFilterAP", "buyLootCharm2AP", "buyHeartBrown", "buyDaycareSpeedAP",
        "buyHeartGreenAP", "buyAcc5AP", "buyPill1AP", "buyPill10AP", "buyPill100AP",
        "buyHeartBlueAP", "buyLazyITOPODAP", "buyDiggerSlotAP", "buyMacguffinSlotAP",
        "buyHeartPurpleAP", "buyMacguffinBooster1AP", "buyBeastButter1AP",
        "buyBeastButter10AP", "buyBeastButter100AP", "buyQuestLightAP",
        "buyFasterQuests1AP", "buyExtendedQuestBankAP", "buyHeartOrangeAP", "buy25ppAP",
        "buy100ppAP", "buy500ppAP", "buyAcc6AP", "buyCustomIdlePercent1AP", "buyAutoNukeAP",
        "buyDaycareArtAP", "buyNGUCapModifierAP", "buyRes3Potion1", "buyRes3Potion2",
        "buyRes3Potion3", "buyAcc7AP", "buyHeartGreyAP", "buyRes3Percent1AP",
        "buyRes3Percent2AP", "buyRes3IdlePercent1AP", "buyRes3NameGeneratorAP",
        "buyFasterWishAP", "buyInvMergeSlotAP", "buyHeartPinkAP", "buyAdvLightAP",
        "buyAdvAdvancerAP", "buyGoToQuestAP", "buyAcc8AP", "buyDeckSlotAP", "buyMayoGenAP",
        "buyTagSlotAP", "buyCardTierConsumableAP", "buyMayoSpeedConsumableAP",
        "buyHeartRainbowAP", "buyAcc9AP"
    };

    private static readonly int[] ApTokens =
    {
        0x0600033b,0x0600033d,0x06000341,0x06000343,0x0600034d,0x0600034f,0x06000351,
        0x06000353,0x06000355,0x06000357,0x06000359,0x0600035f,0x06000361,0x06000363,
        0x0600036d,0x0600036f,0x06000373,0x06000375,0x06000381,0x06000383,0x06000385,
        0x06000387,0x06000389,0x0600035b,0x0600035d,0x0600038b,0x0600033f,0x06000345,
        0x0600038d,0x0600038f,0x06000391,0x06000393,0x06000395,0x06000397,0x06000377,
        0x0600039a,0x0600039c,0x0600039e,0x060003a0,0x060003a2,0x060003a4,0x060003a6,
        0x060003a8,0x060003ac,0x060003ae,0x060003b0,0x060003b2,0x060003b4,0x060003b6,
        0x060003b8,0x060003ba,0x060003bc,0x060003be,0x060003c0,0x06000379,0x06000365,
        0x060003c2,0x060003c4,0x060003c6,0x06000347,0x06000349,0x0600034b,0x0600037b,
        0x060003aa,0x06000367,0x06000369,0x0600036b,0x060003c8,0x060003ca,0x060003cc,
        0x060003ce,0x060003d0,0x060003d2,0x060003d4,0x0600037d,0x060003d6,0x060003d8,
        0x060003da,0x060003de,0x060003dc,0x060003e0,0x0600037f
    };

    // -1 = live serialized; -2 = stateful ladder.
    private static readonly long[] ApCosts =
    {
        5000,10000,5000,10000,-1,-1,-1,100000,100000,10000,100000,225000,25000,100000,
        150000,-2,75000,225000,3000,25000,225000,50000,100000,40000,400000,-2,100000,
        100000,-2,15000,50000,225000,125000,225000,225000,2500,20000,175000,225000,225000,
        -2,-2,225000,50000,10000,90000,800000,50000,250000,125000,225000,100000,400000,
        2000000,500000,125000,65000,250000,100000,4000,40000,40000,500000,225000,50000,
        150000,150000,85000,250000,-2,175000,75000,65000,100000,500000,-2,-2,250000,
        40000,40000,500000,675000
    };

    private static void TestAllApIdMethodCostEffectGoldens()
    {
        var descriptors = PurchaseDescriptorCatalog.AllAp();
        Assert(descriptors.Length == 82, "AP catalog covers exactly IDs 0-81");
        for (var id = 0; id <= 81; id++)
        {
            PurchaseDescriptor descriptor;
            Assert(PurchaseDescriptorCatalog.TryGetAp(id, out descriptor), "AP ID exists: " + id);
            Assert(descriptor.NativeId == id && descriptor.Key == "ap." + id,
                "AP descriptor keeps exact ID/key: " + id);
            Assert(descriptor.NativeMethodName == ApMethods[id],
                "AP method golden: " + id);
            Assert(descriptor.MetadataToken == ApTokens[id],
                "AP metadata token golden: " + id);
            Assert(descriptor.DeclaringTypeName == "ArbitraryController"
                   && descriptor.Currency == PermanentCurrency.ArbitraryPoints,
                "AP controller/currency sealed: " + id);
            Assert(descriptor.Effects().Length > 0, "AP complete effect vector is nonempty: " + id);
            if (ApCosts[id] > 0)
            {
                Assert(descriptor.Cost.Kind == PurchaseCostKind.Fixed,
                    "fixed AP cost model: " + id);
                Equal(descriptor.Cost.Evaluate(PurchaseCostState.Fixed()), ApCosts[id],
                    "fixed AP cost golden: " + id);
            }
            else if (ApCosts[id] == -1)
                Assert(descriptor.Cost.Kind == PurchaseCostKind.LiveSerialized,
                    "serialized AP cost remains live: " + id);
            else
                Assert(descriptor.Cost.Kind == PurchaseCostKind.ApInventorySpace
                       || descriptor.Cost.Kind == PurchaseCostKind.CounterLadder,
                    "stateful AP cost model: " + id);
        }
        Equal(PurchaseDescriptorCatalog.AllAp()[77].Cost.Evaluate(PurchaseCostState.Fixed()),
            250000, "installed Tag Slot is 250k, never the guide's 20k");
    }

    private static void TestHeartsAndCostLadders()
    {
        var ids = new[] {11,14,31,33,38,42,50,63,70,80};
        var items = new[] {119,129,162,171,196,212,293,297,344,390};
        var costs = new long[] {225000,150000,225000,225000,225000,225000,225000,225000,175000,500000};
        for (var i = 0; i < ids.Length; i++)
        {
            PurchaseDescriptor descriptor;
            PurchaseDescriptorCatalog.TryGetAp(ids[i], out descriptor);
            Assert(descriptor.IsHeart && descriptor.HeartItemId == items[i]
                   && descriptor.HeartDeliveryLevel == 10,
                "Heart item/level mapping for AP ID " + ids[i]);
            Equal(descriptor.Cost.Evaluate(PurchaseCostState.Fixed()), costs[i],
                "Heart cost mapping for AP ID " + ids[i]);
            var effects = descriptor.Effects();
            Assert(effects.Length == 2 && effects[0].Amount == 1 && effects[1].Amount == 11,
                "Heart requires physical count plus level-10 contribution");
        }

        PurchaseDescriptor inventory;
        PurchaseDescriptorCatalog.TryGetAp(15, out inventory);
        Equal(inventory.Cost.Evaluate(PurchaseCostState.ApInventory(0, false)), 3000,
            "AP inventory first cost");
        Equal(inventory.Cost.Evaluate(PurchaseCostState.ApInventory(0, true)), 1800,
            "Newbie Pack applies the distinct 1,200 discount");
        Equal(inventory.Cost.Evaluate(PurchaseCostState.ApInventory(70, false)), 10000,
            "AP inventory price caps at 10k");
        Equal(inventory.Cost.Evaluate(PurchaseCostState.ApInventory(165, false)), 10000,
            "AP inventory counter 165 remains last legal atom");
        Throws<InvalidOperationException>(() => inventory.Cost.Evaluate(
            PurchaseCostState.ApInventory(166, false)), "AP inventory counter 166 is capped");

        AssertCostAt(25, 0, 50000); AssertCostAt(25, 6, 110000);
        AssertCostAt(28, 0, 110000); AssertCostAt(28, 1, 225000);
        AssertCostAt(40, 0, 110000); AssertCostAt(40, 5, 225000);
        AssertCostAt(41, 0, 100000); AssertCostAt(41, 1, 100000); AssertCostAt(41, 2, 225000);
        AssertCostAt(69, 0, 50000); AssertCostAt(69, 1, 150000);
        AssertCostAt(69, 2, 250000); AssertCostAt(69, 3, 500000);
        AssertCostAt(75, 49, 25000); AssertCostAt(76, 1, 250000);
    }

    private static void AssertCostAt(int id, long counter, long expected)
    {
        PurchaseDescriptor descriptor;
        PurchaseDescriptorCatalog.TryGetAp(id, out descriptor);
        Equal(descriptor.Cost.Evaluate(PurchaseCostState.WithCounter(counter)), expected,
            "stateful AP cost ID " + id + " counter " + counter);
    }

    private static void TestExpExactCosts()
    {
        Equal(PurchaseCostDescriptor.Formula(PurchaseCostKind.EnergyPower)
            .Evaluate(PurchaseCostState.WithAmount(2)), 300, "Energy power formula");
        Equal(PurchaseCostDescriptor.Formula(PurchaseCostKind.EnergyCap)
            .Evaluate(PurchaseCostState.WithAmount(10000)), 40, "Energy cap integer floor formula");
        Equal(PurchaseCostDescriptor.Formula(PurchaseCostKind.EnergyBar)
            .Evaluate(PurchaseCostState.WithAmount(2)), 160, "Energy bar formula");
        Equal(PurchaseCostDescriptor.Formula(PurchaseCostKind.MagicPower)
            .Evaluate(PurchaseCostState.WithAmount(2)), 900, "Magic power formula");
        Equal(PurchaseCostDescriptor.Formula(PurchaseCostKind.MagicCap)
            .Evaluate(PurchaseCostState.WithAmount(10000)), 120, "Magic cap formula");
        Equal(PurchaseCostDescriptor.Formula(PurchaseCostKind.Resource3Power)
            .Evaluate(PurchaseCostState.WithAmount(2)), 30000000, "R3 power formula");
        Equal(PurchaseCostDescriptor.Formula(PurchaseCostKind.Resource3Cap)
            .Evaluate(PurchaseCostState.WithAmount(10000)), 4000000, "R3 cap formula");
        Equal(PurchaseCostDescriptor.Formula(PurchaseCostKind.Resource3Bar)
            .Evaluate(PurchaseCostState.WithAmount(2)), 16000000, "R3 bar formula");
        Throws<InvalidOperationException>(() => PurchaseCostDescriptor.Formula(
            PurchaseCostKind.EnergyCap).Evaluate(PurchaseCostState.WithAmount(249)),
            "sub-quantum cap call is rejected instead of exposing a zero-cost reflection atom");
        Equal(PurchaseCostDescriptor.Formula(PurchaseCostKind.AdventureHitPoints)
            .Evaluate(PurchaseCostState.WithAmount(10)), 3, "Adventure HP exact multiple-of-ten cost");
        Throws<InvalidOperationException>(() => PurchaseCostDescriptor.Formula(
            PurchaseCostKind.AdventureHitPoints).Evaluate(PurchaseCostState.WithAmount(9)),
            "Adventure HP normalization enforced");
        Equal(PurchaseCostDescriptor.Formula(PurchaseCostKind.EnergySpeed10)
            .Evaluate(PurchaseCostState.WithScalar(49.99)), 2, "speed10 below 50");
        Equal(PurchaseCostDescriptor.Formula(PurchaseCostKind.EnergySpeed10)
            .Evaluate(PurchaseCostState.WithScalar(50)), 20, "speed10 at 50");
        Equal(PurchaseCostDescriptor.Formula(PurchaseCostKind.EnergySpeed10)
            .Evaluate(PurchaseCostState.WithScalar(100)), 200, "speed10 at 100");
        Equal(PurchaseCostDescriptor.Formula(PurchaseCostKind.EnergySpeed100)
            .Evaluate(PurchaseCostState.WithScalar(499)), 200, "speed100 below 500");
        Equal(PurchaseCostDescriptor.Formula(PurchaseCostKind.EnergySpeed100)
            .Evaluate(PurchaseCostState.WithScalar(500)), 2000, "speed100 at 500");
        var inv = PurchaseCostDescriptor.Formula(PurchaseCostKind.ExpInventorySpace);
        Equal(inv.Evaluate(PurchaseCostState.WithCounter(24)), 2, "EXP inventory 24");
        Equal(inv.Evaluate(PurchaseCostState.WithCounter(35)), 2, "EXP inventory 35");
        Equal(inv.Evaluate(PurchaseCostState.WithCounter(36)), 4, "EXP inventory 36");
        Equal(inv.Evaluate(PurchaseCostState.WithCounter(59)), 96, "EXP inventory 59");
        Throws<InvalidOperationException>(() => inv.Evaluate(PurchaseCostState.WithCounter(23)),
            "EXP inventory 23 closed");
        Throws<InvalidOperationException>(() => inv.Evaluate(PurchaseCostState.WithCounter(60)),
            "EXP inventory 60 closed");
        Assert(PurchaseDescriptorCatalog.AllExp().Length >= 20,
            "sealed EXP descriptor catalog covers resource atoms and strategic permanents");

        PurchaseDescriptor special;
        Assert(PurchaseDescriptorCatalog.TryGet("exp.energy.speed-special1", out special)
               && special.MetadataToken == 0x0600095b
               && special.Cost.Kind == PurchaseCostKind.LiveSerialized,
            "Energy Speed special 1 is a build-pinned exact live-cost descriptor");
        var specialEffects = special.Effects();
        Assert(specialEffects.Length == 2 && specialEffects[0].Amount == 20
               && specialEffects[1].Kind == PurchaseEffectKind.SetOne,
            "Energy Speed special seals both +0.2 speed and one-time ownership flag");
        PurchaseDescriptor magicSpeed;
        Assert(PurchaseDescriptorCatalog.TryGet("exp.magic.speed10", out magicSpeed)
               && magicSpeed.MetadataToken == 0x06000992
               && magicSpeed.Cost.Evaluate(PurchaseCostState.Live(3)) == 3
               && magicSpeed.Effects()[0].Amount == 10,
            "Magic Speed +0.1 seals installed method, live native cost, and hundredth delta");
        PurchaseDescriptor fightAttack;
        Assert(PurchaseDescriptorCatalog.TryGet("exp.fight-boss.attack10", out fightAttack)
               && fightAttack.MetadataToken == 0x060009e1
               && fightAttack.Cost.Evaluate(PurchaseCostState.Fixed()) == 30,
            "the narrow forward Fight Boss atom is build-pinned and exactly priced");

        PurchaseDescriptor energyPower;
        Assert(PurchaseDescriptorCatalog.TryGet("exp.energy.custom-power", out energyPower),
            "custom Energy descriptor exists");
        var expValues = new Dictionary<string, long>
        {
            {"permanent.energyPower", 10}, {"unrelated.exact", 7}
        };
        var expBefore = new PurchaseStateVector(1000, expValues);
        expValues["permanent.energyPower"] = 12;
        var expExpected = new PurchaseStateVector(700, expValues);
        var expSnapshot = Snapshot(energyPower, expBefore,
            PurchaseCostState.WithAmount(2), 300, true, null, false, false);
        var expManager = new PermanentPurchaseManager();
        Assert(expManager.Plan(expSnapshot, energyPower, expExpected, 0, null, 1.0).Status
               == PurchasePlanStatus.Planned,
            "custom EXP atom verifies exact amount, cost, and effect together");
        expValues["permanent.energyPower"] = 11;
        Assert(expManager.Plan(expSnapshot, energyPower,
                   new PurchaseStateVector(700, expValues), 0, null, 1.0).Status
               == PurchasePlanStatus.Held,
            "custom EXP atom rejects a cost/effect amount mismatch before spend");
    }

    private static LootCapacityProof HeartCapacity(bool hasFreeSlot, int itemId)
    {
        var ids = hasFreeSlot ? new[] {0} : new[] {999};
        var identities = hasFreeSlot ? new object[] {null} : new[] {new object()};
        var topology = PhysicalTopology.CaptureOrdinary(ids, identities, 1, 0);
        return LootCapacity.ProveOrdinary(topology,
            PurchaseDescriptorCatalog.HeartCapacityRequirement(itemId));
    }

    private static PurchaseStateVector BeforeState(PurchaseDescriptor descriptor, long balance)
    {
        var values = new Dictionary<string, long>();
        var effects = descriptor.Effects();
        for (var i = 0; i < effects.Length; i++) values[effects[i].StateKey] = 0L;
        values["unrelated.exact"] = 7L;
        return new PurchaseStateVector(balance, values);
    }

    private static PurchaseStateVector ExpectedState(PurchaseDescriptor descriptor,
        PurchaseStateVector before, long cost)
    {
        var values = before.ValuesCopy();
        var effects = descriptor.Effects();
        for (var i = 0; i < effects.Length; i++)
        {
            var effect = effects[i];
            var old = values[effect.StateKey];
            switch (effect.Kind)
            {
                case PurchaseEffectKind.ExactDelta:
                case PurchaseEffectKind.HeartItemCount:
                case PurchaseEffectKind.HeartLevelContribution:
                    values[effect.StateKey] = old + effect.Amount;
                    break;
                case PurchaseEffectKind.SetOne:
                    values[effect.StateKey] = 1;
                    break;
                case PurchaseEffectKind.CappedDelta:
                    values[effect.StateKey] = Math.Min(effect.Maximum, old + effect.Amount);
                    break;
                case PurchaseEffectKind.CostStateAmountDelta:
                    throw new InvalidOperationException("Test AP helper received an EXP amount effect.");
                case PurchaseEffectKind.PositiveNativePreview:
                    values[effect.StateKey] = old + 123;
                    break;
            }
        }
        return new PurchaseStateVector(before.CurrencyBalance - cost, values);
    }

    private static PurchaseBoundarySnapshot Snapshot(PurchaseDescriptor descriptor,
        PurchaseStateVector state, PurchaseCostState costState, long liveCost,
        bool autoMerge, LootCapacityProof capacity, bool filtered, bool filterTransaction,
        Guid? mvid = null)
    {
        return new PurchaseBoundarySnapshot(PurchaseDescriptorCatalog.AuditedGameSha256,
            mvid ?? PurchaseDescriptorCatalog.AuditedGameMvid, 71, "ambient-shop-card", liveCost,
            costState, state, autoMerge, capacity, filtered, filterTransaction);
    }

    private static PurchasePlan PlanHeart(PermanentPurchaseManager manager,
        PurchaseDescriptor heart, bool hasSlot, bool filtered, bool filterTransaction)
    {
        var cost = heart.Cost.Evaluate(PurchaseCostState.Fixed());
        var before = BeforeState(heart, cost + 1000000);
        var snapshot = Snapshot(heart, before, PurchaseCostState.Fixed(), cost, true,
            HeartCapacity(hasSlot, heart.HeartItemId), filtered, filterTransaction);
        var expected = ExpectedState(heart, before, cost);
        var result = manager.PlanAp(snapshot, heart.NativeId, heart.NativeMethodName, expected,
            0, null, 10.0);
        return result.Plan;
    }

    private static void TestPreSpendRejectionsAndReserves()
    {
        var manager = new PermanentPurchaseManager();
        PurchaseDescriptor yellow;
        PurchaseDescriptorCatalog.TryGetAp(14, out yellow);
        var cost = yellow.Cost.Evaluate(PurchaseCostState.Fixed());
        var before = BeforeState(yellow, 1000000);
        var expected = ExpectedState(yellow, before, cost);
        var snapshot = Snapshot(yellow, before, PurchaseCostState.Fixed(), cost, true,
            HeartCapacity(true, yellow.HeartItemId), false, true);

        var mismatch = manager.PlanAp(snapshot, 14, "buyAutoBoostMergeAP", expected,
            0, null, 10.0);
        Assert(mismatch.Status == PurchasePlanStatus.Held && mismatch.Reason.Contains("mismatch"),
            "method/ID mismatch is rejected before spend");

        var unknown = Snapshot(yellow, before, PurchaseCostState.Fixed(), cost, true,
            HeartCapacity(true, yellow.HeartItemId), false, true, Guid.NewGuid());
        var unknownPlan = manager.PlanAp(unknown, 14, yellow.NativeMethodName, expected,
            0, null, 10.0);
        Assert(unknownPlan.Status == PurchasePlanStatus.Held
               && unknownPlan.Reason.Contains("read-only")
               && PurchaseDescriptorCatalog.AllAp().Length == 82,
            "unknown MVID keeps read-only catalog telemetry and disables mutations");

        var noSlot = manager.PlanAp(Snapshot(yellow, before, PurchaseCostState.Fixed(), cost,
                true, HeartCapacity(false, yellow.HeartItemId), false, true),
            14, yellow.NativeMethodName, expected, 0, null, 10.0);
        Assert(noSlot.Status == PurchasePlanStatus.Held && noSlot.Reason.Contains("slot"),
            "Heart with no loot-usable ordinary slot holds");
        var filteredNoTransaction = manager.PlanAp(Snapshot(yellow, before,
                PurchaseCostState.Fixed(), cost, true, HeartCapacity(true, yellow.HeartItemId),
                true, false), 14, yellow.NativeMethodName, expected, 0, null, 10.0);
        Assert(filteredNoTransaction.Status == PurchasePlanStatus.Held
               && filteredNoTransaction.Reason.Contains("filtered"),
            "filtered Heart requires target-specific restoration transaction");
        var filteredSafe = manager.PlanAp(Snapshot(yellow, before, PurchaseCostState.Fixed(),
                cost, true, HeartCapacity(true, yellow.HeartItemId), true, true),
            14, yellow.NativeMethodName, expected, 0, null, 10.0);
        Assert(filteredSafe.Status == PurchasePlanStatus.Planned,
            "filtered Heart is legal only with exact temporary exemption/restoration support");

        PurchaseDescriptor invMerge;
        PurchaseDescriptorCatalog.TryGetAp(69, out invMerge);
        var invCost = invMerge.Cost.Evaluate(PurchaseCostState.WithCounter(0));
        var invBefore = BeforeState(invMerge, 1000000);
        var invExpected = ExpectedState(invMerge, invBefore, invCost);
        var blocked69 = manager.PlanAp(Snapshot(invMerge, invBefore,
                PurchaseCostState.WithCounter(0), invCost, false, null, false, false),
            69, invMerge.NativeMethodName, invExpected, 0, null, 10.0);
        Assert(blocked69.Status == PurchasePlanStatus.Held && blocked69.Reason.Contains("Auto Merge"),
            "ID 69 cannot bypass the EXP Auto Merge prerequisite");
        var allowed69 = manager.PlanAp(Snapshot(invMerge, invBefore,
                PurchaseCostState.WithCounter(0), invCost, true, null, false, false),
            69, invMerge.NativeMethodName, invExpected, 0, null, 10.0);
        Assert(allowed69.Status == PurchasePlanStatus.Planned,
            "ID 69 is eligible only with EXP Auto Merge");

        Equal(DynamicPurchaseReserve.Calculate(20, PermanentCurrency.ArbitraryPoints,
            new PurchaseBundleCommitment("heart-bundle", PermanentCurrency.ArbitraryPoints,
                100, 30)), 70, "dynamic reserve subtracts guaranteed pre-boundary income");
        Equal(DynamicPurchaseReserve.Calculate(20, PermanentCurrency.ArbitraryPoints,
            new PurchaseBundleCommitment("heart-bundle", PermanentCurrency.ArbitraryPoints,
                100, 100)), 20, "dynamic reserve falls only to configured hard floor");

        var wrongExpectedValues = expected.ValuesCopy();
        wrongExpectedValues["unrelated.exact"] = 8;
        var wrongExpected = new PurchaseStateVector(expected.CurrencyBalance, wrongExpectedValues);
        Assert(manager.PlanAp(snapshot, 14, yellow.NativeMethodName, wrongExpected,
                   0, null, 10.0).Status == PurchasePlanStatus.Held,
            "undeclared effect is rejected before spend");
        Assert(manager.PlanAp(snapshot, 14, yellow.NativeMethodName, expected,
                   0, null, 0.0).Status == PurchasePlanStatus.Held,
            "zero terminal improvement loses to HOLD");
    }

    private sealed class FakeRuntime : IPermanentPurchaseRuntime
    {
        internal PurchaseBoundarySnapshot Current;
        internal PurchaseBoundarySnapshot After;
        internal int InvokeCalls;
        internal int ExemptedItemId;
        internal bool Dispatch = true;

        public PurchaseBoundarySnapshot Capture(PurchaseDescriptor descriptor)
        {
            return Current;
        }

        public PurchaseInvocation Invoke(RootTransactionToken token,
            PurchaseDescriptor descriptor, int temporaryHeartFilterExemptionItemId)
        {
            InvokeCalls++;
            ExemptedItemId = temporaryHeartFilterExemptionItemId;
            if (!Dispatch) return PurchaseInvocation.Held(descriptor.NativeBindingKey, "held fake");
            Current = After;
            return PurchaseInvocation.Invoked(descriptor.NativeBindingKey);
        }
    }

    private static AutopilotConfig FullConfig()
    {
        var config = new AutopilotConfig();
        NGUInjector.Main.Autopilot = new AutopilotManager {Config = config};
        return config;
    }

    private static PurchaseBoundarySnapshot AfterSnapshot(PurchaseBoundarySnapshot before,
        PurchaseStateVector state, long nextCost)
    {
        return new PurchaseBoundarySnapshot(before.GameSha256, before.GameMvid,
            before.AmbientControllerId, before.AmbientControllerName, nextCost,
            before.CostState, state, before.ExpAutoMergeOwned, before.OrdinaryCapacity,
            before.TargetItemFiltered, before.SupportsTargetFilterTransaction);
    }

    private static void TestExactSettlementAndOneAtom()
    {
        PurchaseDescriptor yellow;
        PurchaseDescriptorCatalog.TryGetAp(14, out yellow);
        var manager = new PermanentPurchaseManager(true);
        var plan = PlanHeart(manager, yellow, true, true, true);
        Assert(plan != null, "filtered Heart exact plan created");
        var runtime = new FakeRuntime
        {
            Current = plan.Before,
            After = AfterSnapshot(plan.Before, plan.ExpectedAfter, plan.ExactCost)
        };
        var coordinator = new MutationCoordinator(() => "save-A/run-1");
        var config = FullConfig();
        using (var root = coordinator.BeginRoot("permanent-purchase", config).Transaction)
        {
            var pass = new PurchasePlanningPass(1);
            var result = manager.ExecuteOne(pass, root, plan, runtime);
            Assert(result.Status == PurchaseExecutionStatus.Attempted
                   && result.Mutation.Kind == MutationResultKind.Committed,
                "exact AP debit plus complete Heart effect commits");
            Assert(runtime.InvokeCalls == 1 && runtime.ExemptedItemId == 129,
                "Heart adapter receives only the exact target filter exemption");
            Assert(runtime.Current.TargetItemFiltered,
                "Heart target filter is restored to its original state");
            var second = manager.ExecuteOne(pass, root, plan, runtime);
            Assert(second.Status == PurchaseExecutionStatus.Held
                   && second.Reason.Contains("replan") && runtime.InvokeCalls == 1,
                "one planning pass can attempt only one permanent atom");
        }

        var disabledRuntime = new FakeRuntime {Current = plan.Before, After = runtime.After};
        var disabledManager = new PermanentPurchaseManager();
        var disabledCoordinator = new MutationCoordinator(() => "save-A/run-2");
        using (var root = disabledCoordinator.BeginRoot("disabled-live-spend", config).Transaction)
        {
            var result = disabledManager.ExecuteOne(new PurchasePlanningPass(2), root,
                plan, disabledRuntime);
            Assert(result.Status == PurchaseExecutionStatus.Held && disabledRuntime.InvokeCalls == 0,
                "default manager keeps live spending disabled until integration/backtest");
        }
    }

    private static void TestMismatchedPostconditionQuarantines()
    {
        PurchaseDescriptor yellow;
        PurchaseDescriptorCatalog.TryGetAp(14, out yellow);
        var manager = new PermanentPurchaseManager(true);
        var plan = PlanHeart(manager, yellow, true, false, true);
        var wrongValues = plan.ExpectedAfter.ValuesCopy();
        wrongValues["inventory.item.129.levelContribution"] = 10; // native level 10 contributes 11
        var wrongAfter = new PurchaseStateVector(plan.ExpectedAfter.CurrencyBalance, wrongValues);
        var runtime = new FakeRuntime
        {
            Current = plan.Before,
            After = AfterSnapshot(plan.Before, wrongAfter, plan.ExactCost)
        };
        var config = FullConfig();
        var coordinator = new MutationCoordinator(() => "save-A/run-3");
        using (var root = coordinator.BeginRoot("partial-heart", config).Transaction)
        {
            var result = manager.ExecuteOne(new PurchasePlanningPass(3), root, plan, runtime);
            Assert(result.Mutation.Kind == MutationResultKind.Quarantined,
                "exact debit with incomplete Heart delivery quarantines PermanentSpend");
            string reason;
            Assert(coordinator.IsQuarantined(MutationClass.PermanentSpend, out reason),
                "partial permanent effect opens the task-1 class circuit breaker");
        }
    }

    private static void TestItopodModifierException()
    {
        Equal(PermanentPurchaseManager.ProjectApReward(100,
            ApIncomeSourceKind.CharacterAddAp, 1.2f), 120,
            "Yellow/normal addAP modifier applies to ordinary AP event");
        Equal(PermanentPurchaseManager.ProjectApReward(100,
            ApIncomeSourceKind.OnlineItopodDirect, 1.2f), 100,
            "online ITOPOD direct AP bypasses Yellow");
        Equal(PermanentPurchaseManager.ProjectApReward(100,
            ApIncomeSourceKind.OfflineItopodDirect, 1.2f), 100,
            "offline ITOPOD direct AP bypasses Yellow");
        var eventLocal = PermanentPurchaseManager.ProjectApReward(1,
                             ApIncomeSourceKind.CharacterAddAp, 1.5f)
                         + PermanentPurchaseManager.ProjectApReward(1,
                             ApIncomeSourceKind.CharacterAddAp, 1.5f);
        var incorrectlyAggregated = PermanentPurchaseManager.ProjectApReward(2,
            ApIncomeSourceKind.CharacterAddAp, 1.5f);
        Assert(eventLocal == 2 && incorrectlyAggregated == 3,
            "Character.addAP floors each event instead of an aggregate rate");
    }

    public static int Main()
    {
        try
        {
            TestAllApIdMethodCostEffectGoldens();
            TestHeartsAndCostLadders();
            TestExpExactCosts();
            TestPreSpendRejectionsAndReserves();
            TestExactSettlementAndOneAtom();
            TestMismatchedPostconditionQuarantines();
            TestItopodModifierException();
            Console.WriteLine("Permanent purchase tests passed: " + _assertions + " assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}

namespace NGUInjector
{
    internal sealed class SettingsStub
    {
        internal bool GlobalEnabled = true;
    }

    internal static class Main
    {
        internal static NGUInjector.Autopilot.AutopilotManager Autopilot;
        internal static SettingsStub Settings = new SettingsStub();
        internal static void LogAction(string category, string detail) { }
    }
}
