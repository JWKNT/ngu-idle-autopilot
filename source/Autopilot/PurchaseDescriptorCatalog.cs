/*
FILE PURPOSE

PurchaseDescriptorCatalog is the immutable, installed-build contract for permanent EXP and AP
purchases.  It binds one AP ID to one native method token, exact cost model, typed effect vector,
unlock requirements, and (for Hearts) physical item delivery.  Inputs are pure cost/effect state;
outputs are descriptors and exact costs.  The catalog never reads Character, invokes reflection,
or grants spending authority.  The authoritative identity is NGU Idle 1.260, Assembly-CSharp SHA
f138...71eaf71 and MVID 5ba2e26b-de64-4a2e-b83a-4a5324f3752e.  Serialized prefab costs remain
live inputs deliberately; unknown builds may inspect this catalog but may not execute it.

AP inventory, slot ladders, Starter Pack clamping, all ten Hearts, and the native ID-69 Auto Merge
prerequisite are represented explicitly.  EXP formulas retain native integer division and checked
overflow.  Effects use stable adapter keys so a live integration can capture every touched
persistent field and prove an exact before/expected/after vector rather than accepting a currency
delta.  UI text, labels, and keyword weights are intentionally absent from purchase economics.
*/
using System;
using System.Collections.Generic;

namespace NGUInjector.Autopilot
{
    internal enum PermanentCurrency
    {
        Experience,
        ArbitraryPoints
    }

    internal enum PurchaseCostKind
    {
        Fixed,
        LiveSerialized,
        ApInventorySpace,
        CounterLadder,
        EnergyPower,
        EnergyCap,
        EnergyBar,
        MagicPower,
        MagicCap,
        MagicBar,
        Resource3Power,
        Resource3Cap,
        Resource3Bar,
        AdventurePower,
        AdventureToughness,
        AdventureHitPoints,
        AdventureRegen,
        EnergySpeed10,
        EnergySpeed100,
        ExpInventorySpace
    }

    internal enum PurchaseEffectKind
    {
        ExactDelta,
        SetOne,
        CappedDelta,
        CostStateAmountDelta,
        PositiveNativePreview,
        HeartItemCount,
        HeartLevelContribution
    }

    internal enum PurchaseUnlockRequirement
    {
        None,
        ExpAutoMerge
    }

    internal enum ApIncomeSourceKind
    {
        CharacterAddAp,
        OnlineItopodDirect,
        OfflineItopodDirect
    }

    internal sealed class PurchaseCostState
    {
        internal readonly long Counter;
        internal readonly long Amount;
        internal readonly long LiveSerializedCost;
        internal readonly bool BoughtNewbiePack;
        internal readonly double Scalar;

        internal PurchaseCostState(long counter, long amount, long liveSerializedCost,
            bool boughtNewbiePack, double scalar)
        {
            Counter = counter;
            Amount = amount;
            LiveSerializedCost = liveSerializedCost;
            BoughtNewbiePack = boughtNewbiePack;
            Scalar = scalar;
        }

        internal static PurchaseCostState Fixed()
        {
            return new PurchaseCostState(0L, 0L, 0L, false, 0.0);
        }

        internal static PurchaseCostState Live(long cost)
        {
            return new PurchaseCostState(0L, 0L, cost, false, 0.0);
        }

        internal static PurchaseCostState WithCounter(long counter)
        {
            return new PurchaseCostState(counter, 0L, 0L, false, 0.0);
        }

        internal static PurchaseCostState ApInventory(long spaces, bool boughtNewbiePack)
        {
            return new PurchaseCostState(spaces, 0L, 0L, boughtNewbiePack, 0.0);
        }

        internal static PurchaseCostState WithAmount(long amount)
        {
            return new PurchaseCostState(0L, amount, 0L, false, 0.0);
        }

        internal static PurchaseCostState WithScalar(double scalar)
        {
            return new PurchaseCostState(0L, 0L, 0L, false, scalar);
        }
    }

    internal sealed class PurchaseCostDescriptor
    {
        private readonly long[] _ladder;

        internal readonly PurchaseCostKind Kind;
        internal readonly long FixedCost;
        internal readonly long MaximumCounterExclusive;

        private PurchaseCostDescriptor(PurchaseCostKind kind, long fixedCost,
            long maximumCounterExclusive, long[] ladder)
        {
            Kind = kind;
            FixedCost = fixedCost;
            MaximumCounterExclusive = maximumCounterExclusive;
            _ladder = ladder == null ? new long[0] : (long[])ladder.Clone();
        }

        internal static PurchaseCostDescriptor Fixed(long cost)
        {
            if (cost <= 0L) throw new ArgumentOutOfRangeException("cost");
            return new PurchaseCostDescriptor(PurchaseCostKind.Fixed, cost, long.MaxValue, null);
        }

        internal static PurchaseCostDescriptor LiveSerialized()
        {
            return new PurchaseCostDescriptor(PurchaseCostKind.LiveSerialized, 0L,
                long.MaxValue, null);
        }

        internal static PurchaseCostDescriptor ApInventorySpace()
        {
            return new PurchaseCostDescriptor(PurchaseCostKind.ApInventorySpace, 0L, 166L, null);
        }

        internal static PurchaseCostDescriptor CounterLadder(long maximumCounterExclusive,
            params long[] ladder)
        {
            if (maximumCounterExclusive <= 0L) throw new ArgumentOutOfRangeException("maximumCounterExclusive");
            if (ladder == null || ladder.Length == 0) throw new ArgumentException("ladder");
            for (var i = 0; i < ladder.Length; i++)
                if (ladder[i] <= 0L) throw new ArgumentOutOfRangeException("ladder");
            return new PurchaseCostDescriptor(PurchaseCostKind.CounterLadder, 0L,
                maximumCounterExclusive, ladder);
        }

        internal static PurchaseCostDescriptor Formula(PurchaseCostKind kind)
        {
            if (kind == PurchaseCostKind.Fixed || kind == PurchaseCostKind.LiveSerialized
                || kind == PurchaseCostKind.ApInventorySpace
                || kind == PurchaseCostKind.CounterLadder)
                throw new ArgumentException("Use the dedicated cost factory.", "kind");
            return new PurchaseCostDescriptor(kind, 0L, long.MaxValue, null);
        }

        internal long Evaluate(PurchaseCostState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            switch (Kind)
            {
                case PurchaseCostKind.Fixed:
                    return FixedCost;
                case PurchaseCostKind.LiveSerialized:
                    if (state.LiveSerializedCost <= 0L)
                        throw new InvalidOperationException("Serialized native cost is unavailable or nonpositive.");
                    return state.LiveSerializedCost;
                case PurchaseCostKind.ApInventorySpace:
                    if (state.Counter < 0L || state.Counter >= 166L)
                        throw new InvalidOperationException("AP inventory-space ladder is exhausted or malformed.");
                    return Math.Min(10000L, checked(3000L + 100L * state.Counter
                        - (state.BoughtNewbiePack ? 1200L : 0L)));
                case PurchaseCostKind.CounterLadder:
                    if (state.Counter < 0L || state.Counter >= MaximumCounterExclusive)
                        throw new InvalidOperationException("Purchase counter is capped or malformed.");
                    var index = state.Counter < _ladder.Length ? (int)state.Counter : _ladder.Length - 1;
                    return _ladder[index];
                case PurchaseCostKind.EnergyPower:
                    return CheckedPositiveProduct(state.Amount, 150L);
                case PurchaseCostKind.EnergyCap:
                    return PositiveFloorUnits(state.Amount, 250L, 1L);
                case PurchaseCostKind.EnergyBar:
                    return CheckedPositiveProduct(state.Amount, 80L);
                case PurchaseCostKind.MagicPower:
                    return CheckedPositiveProduct(state.Amount, 450L);
                case PurchaseCostKind.MagicCap:
                    return PositiveFloorUnits(state.Amount, 250L, 3L);
                case PurchaseCostKind.MagicBar:
                    return CheckedPositiveProduct(state.Amount, 240L);
                case PurchaseCostKind.Resource3Power:
                    return CheckedPositiveProduct(state.Amount, 15000000L);
                case PurchaseCostKind.Resource3Cap:
                    return PositiveFloorUnits(state.Amount, 250L, 100000L);
                case PurchaseCostKind.Resource3Bar:
                    return CheckedPositiveProduct(state.Amount, 8000000L);
                case PurchaseCostKind.AdventurePower:
                case PurchaseCostKind.AdventureToughness:
                    return CheckedPositiveProduct(state.Amount, 3L);
                case PurchaseCostKind.AdventureHitPoints:
                    if (state.Amount <= 0L || state.Amount % 10L != 0L)
                        throw new InvalidOperationException("Adventure HP purchases require a positive multiple of ten.");
                    return checked(state.Amount * 3L) / 10L;
                case PurchaseCostKind.AdventureRegen:
                    return CheckedPositiveProduct(state.Amount, 50L);
                case PurchaseCostKind.EnergySpeed10:
                    if (double.IsNaN(state.Scalar) || double.IsInfinity(state.Scalar) || state.Scalar < 0.0)
                        throw new InvalidOperationException("Energy speed is unavailable or malformed.");
                    return state.Scalar < 50.0 ? 2L : state.Scalar < 100.0 ? 20L : 200L;
                case PurchaseCostKind.EnergySpeed100:
                    if (double.IsNaN(state.Scalar) || double.IsInfinity(state.Scalar) || state.Scalar < 0.0)
                        throw new InvalidOperationException("Energy speed is unavailable or malformed.");
                    return state.Scalar < 50.0 ? 20L : state.Scalar < 500.0 ? 200L : 2000L;
                case PurchaseCostKind.ExpInventorySpace:
                    if (state.Counter < 24L || state.Counter >= 60L)
                        throw new InvalidOperationException("EXP inventory-space ladder is closed.");
                    return state.Counter <= 35L ? 2L : checked((state.Counter - 35L) * 4L);
                default:
                    throw new InvalidOperationException("Unknown cost model.");
            }
        }

        internal long[] Ladder()
        {
            return (long[])_ladder.Clone();
        }

        private static long CheckedPositiveProduct(long amount, long multiplier)
        {
            if (amount <= 0L) throw new InvalidOperationException("Purchase amount must be positive.");
            var value = checked(amount * multiplier);
            if (value <= 0L) throw new OverflowException("Purchase cost overflowed.");
            return value;
        }

        private static long PositiveFloorUnits(long amount, long divisor, long multiplier)
        {
            if (amount <= 0L) throw new InvalidOperationException("Purchase amount must be positive.");
            var units = amount / divisor;
            if (units <= 0L)
                throw new InvalidOperationException("Amount is below the native legal cost quantum.");
            return checked(units * multiplier);
        }
    }

    internal sealed class PurchaseEffectDescriptor
    {
        internal readonly string StateKey;
        internal readonly PurchaseEffectKind Kind;
        internal readonly long Amount;
        internal readonly long Maximum;

        internal PurchaseEffectDescriptor(string stateKey, PurchaseEffectKind kind,
            long amount, long maximum)
        {
            if (string.IsNullOrEmpty(stateKey)) throw new ArgumentException("stateKey");
            StateKey = stateKey;
            Kind = kind;
            Amount = amount;
            Maximum = maximum;
        }

        internal bool IsExpectedTransition(long before, long after, PurchaseCostState costState)
        {
            switch (Kind)
            {
                case PurchaseEffectKind.ExactDelta:
                case PurchaseEffectKind.HeartItemCount:
                case PurchaseEffectKind.HeartLevelContribution:
                    return before <= long.MaxValue - Amount && after == before + Amount;
                case PurchaseEffectKind.SetOne:
                    return before == 0L && after == 1L;
                case PurchaseEffectKind.CappedDelta:
                    return before >= 0L && before <= Maximum
                           && after == Math.Min(Maximum, before + Amount);
                case PurchaseEffectKind.CostStateAmountDelta:
                    return costState != null && costState.Amount > 0L
                           && before <= long.MaxValue - costState.Amount
                           && after == before + costState.Amount;
                case PurchaseEffectKind.PositiveNativePreview:
                    return after > before;
                default:
                    return false;
            }
        }
    }

    internal sealed class PurchaseDescriptor
    {
        private readonly PurchaseEffectDescriptor[] _effects;

        internal readonly string Key;
        internal readonly PermanentCurrency Currency;
        internal readonly int NativeId;
        internal readonly string DeclaringTypeName;
        internal readonly string NativeMethodName;
        internal readonly int MetadataToken;
        internal readonly string DisplayName;
        internal readonly PurchaseCostDescriptor Cost;
        internal readonly PurchaseUnlockRequirement Unlock;
        internal readonly int HeartItemId;
        internal readonly int HeartDeliveryLevel;

        internal PurchaseDescriptor(string key, PermanentCurrency currency, int nativeId,
            string declaringTypeName, string nativeMethodName, int metadataToken,
            string displayName, PurchaseCostDescriptor cost, PurchaseUnlockRequirement unlock,
            int heartItemId, int heartDeliveryLevel, PurchaseEffectDescriptor[] effects)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key");
            if (string.IsNullOrEmpty(declaringTypeName)) throw new ArgumentException("declaringTypeName");
            if (string.IsNullOrEmpty(nativeMethodName)) throw new ArgumentException("nativeMethodName");
            if (metadataToken == 0) throw new ArgumentOutOfRangeException("metadataToken");
            if (cost == null) throw new ArgumentNullException("cost");
            if (effects == null || effects.Length == 0) throw new ArgumentException("effects");
            Key = key;
            Currency = currency;
            NativeId = nativeId;
            DeclaringTypeName = declaringTypeName;
            NativeMethodName = nativeMethodName;
            MetadataToken = metadataToken;
            DisplayName = displayName ?? key;
            Cost = cost;
            Unlock = unlock;
            HeartItemId = heartItemId;
            HeartDeliveryLevel = heartDeliveryLevel;
            _effects = (PurchaseEffectDescriptor[])effects.Clone();
        }

        internal bool IsHeart { get { return HeartItemId > 0; } }

        internal PurchaseEffectDescriptor[] Effects()
        {
            return (PurchaseEffectDescriptor[])_effects.Clone();
        }

        internal string NativeBindingKey
        {
            get { return NativeBindingKeys.PurchaseMethod(DeclaringTypeName, NativeMethodName); }
        }
    }

    internal static class PurchaseDescriptorCatalog
    {
        internal const string AuditedGameSha256 = NativeBindingRegistry.AuditedGameSha256;
        internal static readonly Guid AuditedGameMvid = NativeBindingRegistry.AuditedGameMvid;
        internal const string CatalogVersion = "ngu-idle-1.260/permanent-purchases-v1";

        private static readonly PurchaseDescriptor[] ApDescriptors = BuildApDescriptors();
        private static readonly PurchaseDescriptor[] ExpDescriptors = BuildExpDescriptors();
        private static readonly Dictionary<int, PurchaseDescriptor> ApById = IndexAp();
        private static readonly Dictionary<string, PurchaseDescriptor> ByKey = IndexAll();

        internal static PurchaseDescriptor[] AllAp()
        {
            return (PurchaseDescriptor[])ApDescriptors.Clone();
        }

        internal static PurchaseDescriptor[] AllExp()
        {
            return (PurchaseDescriptor[])ExpDescriptors.Clone();
        }

        internal static bool TryGetAp(int id, out PurchaseDescriptor descriptor)
        {
            return ApById.TryGetValue(id, out descriptor);
        }

        internal static bool TryGet(string key, out PurchaseDescriptor descriptor)
        {
            return ByKey.TryGetValue(key ?? string.Empty, out descriptor);
        }

        internal static bool TryResolveAp(int id, string exactMethodName,
            out PurchaseDescriptor descriptor, out string reason)
        {
            reason = string.Empty;
            if (!ApById.TryGetValue(id, out descriptor))
            {
                reason = "AP ID is outside the sealed installed-build catalog.";
                return false;
            }
            if (!string.Equals(descriptor.NativeMethodName, exactMethodName,
                    StringComparison.Ordinal))
            {
                reason = "AP ID/method mismatch: ID " + id + " requires "
                         + descriptor.NativeMethodName + ".";
                descriptor = null;
                return false;
            }
            return true;
        }

        internal static LootCapacityRequirement HeartCapacityRequirement(int itemId)
        {
            if (!IsHeartItem(itemId)) throw new ArgumentOutOfRangeException("itemId");
            return LootCapacityRequirement.ExactUniqueDelivery(
                "ap-heart-item-" + itemId, 0, 1, 0);
        }

        internal static bool IsHeartItem(int itemId)
        {
            return itemId == 119 || itemId == 129 || itemId == 162 || itemId == 171
                   || itemId == 196 || itemId == 212 || itemId == 293 || itemId == 297
                   || itemId == 344 || itemId == 390;
        }

        private static Dictionary<int, PurchaseDescriptor> IndexAp()
        {
            var result = new Dictionary<int, PurchaseDescriptor>();
            for (var i = 0; i < ApDescriptors.Length; i++) result.Add(ApDescriptors[i].NativeId, ApDescriptors[i]);
            if (result.Count != 82) throw new InvalidOperationException("AP catalog must cover IDs 0 through 81 exactly.");
            for (var id = 0; id <= 81; id++)
                if (!result.ContainsKey(id)) throw new InvalidOperationException("Missing AP ID " + id + ".");
            return result;
        }

        private static Dictionary<string, PurchaseDescriptor> IndexAll()
        {
            var result = new Dictionary<string, PurchaseDescriptor>(StringComparer.Ordinal);
            for (var i = 0; i < ApDescriptors.Length; i++) result.Add(ApDescriptors[i].Key, ApDescriptors[i]);
            for (var i = 0; i < ExpDescriptors.Length; i++) result.Add(ExpDescriptors[i].Key, ExpDescriptors[i]);
            return result;
        }

        private static PurchaseDescriptor[] BuildApDescriptors()
        {
            var result = new PurchaseDescriptor[82];
            AddAp(result, 0, "Energy potion I", "buyEnergyPotion1AP", 0x0600033b, Fixed(5000), Delta("ap.energyPotion1", 1));
            AddAp(result, 1, "Energy potion II", "buyEnergyPotion2AP", 0x0600033d, Fixed(10000), Delta("ap.energyPotion2", 1));
            AddAp(result, 2, "Magic potion I", "buyMagicPotion1AP", 0x06000341, Fixed(5000), Delta("ap.magicPotion1", 1));
            AddAp(result, 3, "Magic potion II", "buyMagicPotion2AP", 0x06000343, Fixed(10000), Delta("ap.magicPotion2", 1));
            AddAp(result, 4, "Loot charm I", "buyLootCharm1AP", 0x0600034d, PurchaseCostDescriptor.LiveSerialized(), Delta("ap.lootCharm1", 1));
            AddAp(result, 5, "Energy bar-bar", "buyEnergyBarBar1AP", 0x0600034f, PurchaseCostDescriptor.LiveSerialized(), Delta("ap.energyBarBar", 1));
            AddAp(result, 6, "Magic bar-bar", "buyMagicBarBar1AP", 0x06000351, PurchaseCostDescriptor.LiveSerialized(), Delta("ap.magicBarBar", 1));
            AddAp(result, 7, "Improved Loot Filter", "buyLootFilterAP", 0x06000353, Fixed(100000), Set("ap.hasImprovedLootFilter"));
            AddAp(result, 8, "Improved Auto Boost/Merge", "buyAutoBoostMergeAP", 0x06000355, Fixed(100000), Set("ap.hasImprovedAutoBoostMerge"));
            AddAp(result, 9, "Insta Training Caps", "buyInstaTrainAP", 0x06000357, Fixed(10000), Set("ap.hasInstaTraining"));
            AddAp(result, 10, "500 base EXP", "buy500ExpAP", 0x06000359, Fixed(100000), NativeGain("currency.exp"));
            AddHeart(result, 11, "Red Heart", "buyHeartAP", 0x0600035f, 225000, 119);
            AddAp(result, 12, "Custom percent set 1", "buyCustomPercent1AP", 0x06000361, Fixed(25000), Set("ap.hasCustomPercent1"));
            AddAp(result, 13, "Custom percent set 2", "buyCustomPercent2AP", 0x06000363, Fixed(100000), Set("ap.hasCustomPercent2"));
            AddHeart(result, 14, "Yellow Heart", "buyYellowHeartAP", 0x0600036d, 150000, 129);
            AddAp(result, 15, "Inventory space", "buyInventoryAP", 0x0600036f,
                PurchaseCostDescriptor.ApInventorySpace(), Delta("ap.inventorySpaces", 1));
            AddAp(result, 16, "Starter Pack", "buyStarterPackAP", 0x06000373, Fixed(75000),
                NativeGain("currency.exp"), Capped("ap.inventorySpaces", 5, 166), Set("ap.hasStarterPack"));
            AddAp(result, 17, "Accessory slot 4", "buyAcc4AP", 0x06000375, Fixed(225000), Delta("ap.accessorySlots", 1));
            AddAp(result, 18, "Poop 1", "buyPoop1AP", 0x06000381, Fixed(3000), Delta("consumable.poop", 1));
            AddAp(result, 19, "Poop 10", "buyPoop10AP", 0x06000383, Fixed(25000), Delta("consumable.poop", 10));
            AddAp(result, 20, "Poop 100", "buyPoop100AP", 0x06000385, Fixed(225000), Delta("consumable.poop", 100));
            AddAp(result, 21, "Yggdrasil reminder", "buyYggReminderAP", 0x06000387, Fixed(50000), Set("ap.hasYggdrasilReminder"));
            AddAp(result, 22, "Extended daily-spin bank", "buyExtendedSpinBankAP", 0x06000389, Fixed(100000), Set("ap.hasExtendedSpinBank"));
            AddAp(result, 23, "200 base EXP", "buy200ExpAP", 0x0600035b, Fixed(40000), NativeGain("currency.exp"));
            AddAp(result, 24, "2,000 base EXP", "buy2KExpAP", 0x0600035d, Fixed(400000), NativeGain("currency.exp"));
            AddAp(result, 25, "Loadout slot", "buyLoadoutSlotAP", 0x0600038b,
                PurchaseCostDescriptor.CounterLadder(7, 50000, 60000, 70000, 80000, 90000, 100000, 110000), Delta("ap.loadoutSlots", 1));
            AddAp(result, 26, "Large Energy potion", "buyEnergyPotion3", 0x0600033f, Fixed(100000), Delta("ap.energyPotion3", 1));
            AddAp(result, 27, "Large Magic potion", "buyMagicPotion3", 0x06000345, Fixed(100000), Delta("ap.magicPotion3", 1));
            AddAp(result, 28, "Beard slot", "buyBeardAP", 0x0600038d,
                PurchaseCostDescriptor.CounterLadder(4, 110000, 225000), Delta("ap.beardSlots", 1));
            AddAp(result, 29, "Infinity Cube filter", "buyCubeFilterAP", 0x0600038f, Fixed(15000), Set("ap.hasCubeFilter"));
            AddAp(result, 30, "Loot charm II", "buyLootCharm2AP", 0x06000391, Fixed(50000), Delta("ap.lootCharm2", 1));
            AddHeart(result, 31, "Brown Heart", "buyHeartBrown", 0x06000393, 225000, 162);
            AddAp(result, 32, "Daycare speed", "buyDaycareSpeedAP", 0x06000395, Fixed(125000), Set("ap.hasDaycareSpeed"));
            AddHeart(result, 33, "Green Heart", "buyHeartGreenAP", 0x06000397, 225000, 171);
            AddAp(result, 34, "Accessory slot 5", "buyAcc5AP", 0x06000377, Fixed(225000), Delta("ap.accessorySlots", 1));
            AddAp(result, 35, "Iron Pill 1", "buyPill1AP", 0x0600039a, Fixed(2500), Delta("consumable.ironPill", 1000));
            AddAp(result, 36, "Iron Pill 10", "buyPill10AP", 0x0600039c, Fixed(20000), Delta("consumable.ironPill", 10000));
            AddAp(result, 37, "Iron Pill 100", "buyPill100AP", 0x0600039e, Fixed(175000), Delta("consumable.ironPill", 100000));
            AddHeart(result, 38, "Blue Heart", "buyHeartBlueAP", 0x060003a0, 225000, 196);
            AddAp(result, 39, "Lazy ITOPOD", "buyLazyITOPODAP", 0x060003a2, Fixed(225000), Set("ap.hasLazyItopod"));
            AddAp(result, 40, "Digger slot", "buyDiggerSlotAP", 0x060003a4,
                PurchaseCostDescriptor.CounterLadder(6, 110000, 225000), Delta("ap.diggerSlots", 1));
            AddAp(result, 41, "MacGuffin slot", "buyMacguffinSlotAP", 0x060003a6,
                PurchaseCostDescriptor.CounterLadder(11, 100000, 100000, 225000), Delta("ap.macguffinSlots", 1));
            AddHeart(result, 42, "Purple Heart", "buyHeartPurpleAP", 0x060003a8, 225000, 212);
            AddAp(result, 43, "MacGuffin booster", "buyMacguffinBooster1AP", 0x060003ac, Fixed(50000), Delta("ap.macguffinBoosters", 1));
            AddAp(result, 44, "Beast butter 1", "buyBeastButter1AP", 0x060003ae, Fixed(10000), Delta("consumable.beastButter", 1));
            AddAp(result, 45, "Beast butter 10", "buyBeastButter10AP", 0x060003b0, Fixed(90000), Delta("consumable.beastButter", 10));
            AddAp(result, 46, "Beast butter 100", "buyBeastButter100AP", 0x060003b2, Fixed(800000), Delta("consumable.beastButter", 100));
            AddAp(result, 47, "Quest light", "buyQuestLightAP", 0x060003b4, Fixed(50000), Set("ap.hasQuestLight"));
            AddAp(result, 48, "Faster quests", "buyFasterQuests1AP", 0x060003b6, Fixed(250000), Set("ap.hasFasterQuests"));
            AddAp(result, 49, "Extended quest bank", "buyExtendedQuestBankAP", 0x060003b8, Fixed(125000), Set("ap.hasExtendedQuestBank"));
            AddHeart(result, 50, "Orange Heart", "buyHeartOrangeAP", 0x060003ba, 225000, 293);
            AddAp(result, 51, "25 PP", "buy25ppAP", 0x060003bc, Fixed(100000), Delta("currency.pp", 25));
            AddAp(result, 52, "100 PP", "buy100ppAP", 0x060003be, Fixed(400000), Delta("currency.pp", 100));
            AddAp(result, 53, "500 PP", "buy500ppAP", 0x060003c0, Fixed(2000000), Delta("currency.pp", 500));
            AddAp(result, 54, "Accessory slot 6", "buyAcc6AP", 0x06000379, Fixed(500000), Delta("ap.accessorySlots", 1));
            AddAp(result, 55, "Custom idle-percent set", "buyCustomIdlePercent1AP", 0x06000365, Fixed(125000), Set("ap.hasCustomIdlePercent"));
            AddAp(result, 56, "Auto Nuke", "buyAutoNukeAP", 0x060003c2, Fixed(65000), Set("ap.hasAutoNuke"));
            AddAp(result, 57, "Daycare kitty art", "buyDaycareArtAP", 0x060003c4, Fixed(250000), Set("ap.hasDaycareArt"));
            AddAp(result, 58, "NGU cap modifier", "buyNGUCapModifierAP", 0x060003c6, Fixed(100000), Set("ap.hasNguCapModifier"));
            AddAp(result, 59, "R3 potion I", "buyRes3Potion1", 0x06000347, Fixed(4000), Delta("ap.res3Potion1", 1));
            AddAp(result, 60, "R3 potion II", "buyRes3Potion2", 0x06000349, Fixed(40000), Delta("ap.res3Potion2", 1));
            AddAp(result, 61, "R3 potion III", "buyRes3Potion3", 0x0600034b, Fixed(40000), Delta("ap.res3Potion3", 1));
            AddAp(result, 62, "Accessory slot 7", "buyAcc7AP", 0x0600037b, Fixed(500000), Delta("ap.accessorySlots", 1));
            AddHeart(result, 63, "Grey Heart", "buyHeartGreyAP", 0x060003aa, 225000, 297);
            AddAp(result, 64, "R3 custom percent set 1", "buyRes3Percent1AP", 0x06000367, Fixed(50000), Set("ap.hasRes3Percent1"));
            AddAp(result, 65, "R3 custom percent set 2", "buyRes3Percent2AP", 0x06000369, Fixed(150000), Set("ap.hasRes3Percent2"));
            AddAp(result, 66, "R3 custom idle-percent set", "buyRes3IdlePercent1AP", 0x0600036b, Fixed(150000), Set("ap.hasRes3IdlePercent"));
            AddAp(result, 67, "R3 name generator", "buyRes3NameGeneratorAP", 0x060003c8, Fixed(85000), Set("ap.hasRes3NameGenerator"));
            AddAp(result, 68, "Faster wishes", "buyFasterWishAP", 0x060003ca, Fixed(250000), Set("ap.hasFasterWishes"));
            AddAp(result, 69, "Inventory Merge slot", "buyInvMergeSlotAP", 0x060003cc,
                PurchaseCostDescriptor.CounterLadder(4, 50000, 150000, 250000, 500000),
                PurchaseUnlockRequirement.ExpAutoMerge, Delta("ap.inventoryMergeSlots", 1));
            AddHeart(result, 70, "Pink Heart", "buyHeartPinkAP", 0x060003ce, 175000, 344);
            AddAp(result, 71, "Adventure light", "buyAdvLightAP", 0x060003d0, Fixed(75000), Set("ap.hasAdventureLight"));
            AddAp(result, 72, "Adventure advancer", "buyAdvAdvancerAP", 0x060003d2, Fixed(65000), Set("ap.hasAdventureAdvancer"));
            AddAp(result, 73, "Go-to-quest-zone", "buyGoToQuestAP", 0x060003d4, Fixed(100000), Set("ap.hasGoToQuest"));
            AddAp(result, 74, "Accessory slot 8", "buyAcc8AP", 0x0600037d, Fixed(500000), Delta("ap.accessorySlots", 1));
            AddAp(result, 75, "Deck space", "buyDeckSlotAP", 0x060003d6,
                PurchaseCostDescriptor.CounterLadder(50, 25000), Delta("ap.deckSpaces", 1));
            AddAp(result, 76, "Mayo generator", "buyMayoGenAP", 0x060003d8,
                PurchaseCostDescriptor.CounterLadder(2, 250000), Delta("ap.mayoGenerators", 1));
            AddAp(result, 77, "Tag slot", "buyTagSlotAP", 0x060003da, Fixed(250000), Delta("ap.tagSlots", 1));
            AddAp(result, 78, "Card-tier consumable", "buyCardTierConsumableAP", 0x060003de, Fixed(40000), Delta("consumable.cardTier", 1));
            AddAp(result, 79, "Mayo-speed consumable", "buyMayoSpeedConsumableAP", 0x060003dc, Fixed(40000), Delta("consumable.mayoSpeed", 1));
            AddHeart(result, 80, "Rainbow Heart", "buyHeartRainbowAP", 0x060003e0, 500000, 390);
            AddAp(result, 81, "Accessory slot 9", "buyAcc9AP", 0x0600037f, Fixed(675000), Delta("ap.accessorySlots", 1));
            return result;
        }

        private static PurchaseDescriptor[] BuildExpDescriptors()
        {
            return new[]
            {
                Exp("exp.energy.custom-power", "EnergyPurchases", "buyCustomPower", 0x06000973, PurchaseCostKind.EnergyPower, "permanent.energyPower"),
                Exp("exp.energy.custom-cap", "EnergyPurchases", "buyCustomCap", 0x06000977, PurchaseCostKind.EnergyCap, "permanent.energyCap"),
                Exp("exp.energy.custom-bar", "EnergyPurchases", "buyCustomBar", 0x06000975, PurchaseCostKind.EnergyBar, "permanent.energyBars"),
                Exp("exp.magic.custom-power", "MagicPurchases", "buyCustomPower", 0x060009aa, PurchaseCostKind.MagicPower, "permanent.magicPower"),
                Exp("exp.magic.custom-cap", "MagicPurchases", "buyCustomCap", 0x060009ae, PurchaseCostKind.MagicCap, "permanent.magicCap"),
                Exp("exp.magic.custom-bar", "MagicPurchases", "buyCustomBar", 0x060009ac, PurchaseCostKind.MagicBar, "permanent.magicBars"),
                Exp("exp.resource3.custom-power", "Resource3Purchases", "buyCustomPower", 0x06000aed, PurchaseCostKind.Resource3Power, "permanent.res3Power"),
                Exp("exp.resource3.custom-cap", "Resource3Purchases", "buyCustomCap", 0x06000af1, PurchaseCostKind.Resource3Cap, "permanent.res3Cap"),
                Exp("exp.resource3.custom-bar", "Resource3Purchases", "buyCustomBar", 0x06000aef, PurchaseCostKind.Resource3Bar, "permanent.res3Bars"),
                Exp("exp.adventure.power", "AdventurePurchases", "buyCustomPower", 0x06000939, PurchaseCostKind.AdventurePower, "permanent.adventurePower"),
                Exp("exp.adventure.toughness", "AdventurePurchases", "buyCustomToughness", 0x0600093b, PurchaseCostKind.AdventureToughness, "permanent.adventureToughness"),
                Exp("exp.adventure.hit-points", "AdventurePurchases", "buyCustomHP", 0x0600093d, PurchaseCostKind.AdventureHitPoints, "permanent.adventureHitPoints"),
                Exp("exp.adventure.regen", "AdventurePurchases", "buyCustomregen", 0x0600093f, PurchaseCostKind.AdventureRegen, "permanent.adventureRegen"),
                new PurchaseDescriptor("exp.energy.speed10", PermanentCurrency.Experience, -1,
                    "EnergyPurchases", "buyEnergySpeed10", 0x06000955, "Energy speed +10",
                    PurchaseCostDescriptor.Formula(PurchaseCostKind.EnergySpeed10), PurchaseUnlockRequirement.None,
                    0, 0, new[] { Delta("permanent.energySpeed", 10) }),
                new PurchaseDescriptor("exp.energy.speed100", PermanentCurrency.Experience, -1,
                    "EnergyPurchases", "buyEnergySpeed100", 0x06000957, "Energy speed +100",
                    PurchaseCostDescriptor.Formula(PurchaseCostKind.EnergySpeed100), PurchaseUnlockRequirement.None,
                    0, 0, new[] { Delta("permanent.energySpeed", 100) }),
                new PurchaseDescriptor("exp.adventure.inventory-space", PermanentCurrency.Experience, -1,
                    "AdventurePurchases", "buyInventorySpace", 0x06000917, "EXP inventory space",
                    PurchaseCostDescriptor.Formula(PurchaseCostKind.ExpInventorySpace), PurchaseUnlockRequirement.None,
                    0, 0, new[] { Delta("exp.inventorySpaces", 1) }),
                ExpFixed("exp.adventure.filter", "AdventurePurchases", "buyFilter", 0x06000919, 20, "exp.hasBasicFilter"),
                ExpFixed("exp.adventure.recycle", "AdventurePurchases", "buyRecycleBoost", 0x0600091d, 100, "exp.hasRecycle"),
                ExpFixed("exp.adventure.accessory3", "AdventurePurchases", "buyAcc3", 0x0600091b, 3000, "exp.hasAccessory3"),
                ExpFixed("exp.adventure.auto-merge", "AdventurePurchases", "buyAutoMerge", 0x0600091f, 200, "exp.hasAutoMerge"),
                ExpFixed("exp.adventure.accessory5", "AdventurePurchases", "buyAcc5", 0x06000923, 30000, "exp.hasAccessory5"),
                ExpFixed("exp.adventure.daycare", "AdventurePurchases", "buyDaycare", 0x06000929, 250, "exp.hasDaycare"),
                ExpFixed("exp.adventure.daycare-slot2", "AdventurePurchases", "buyDaycareSlot2", 0x0600092b, 25000, "exp.hasDaycareSlot2"),
                ExpFixed("exp.adventure.daycare-slot3", "AdventurePurchases", "buyDaycareSlot3", 0x0600092d, 500000, "exp.hasDaycareSlot3"),
                ExpFixed("exp.adventure.inventory-merge", "AdventurePurchases", "buyInvMergeUnlock", 0x0600092f, 1000, "exp.hasInventoryMerge")
            };
        }

        private static PurchaseDescriptor Exp(string key, string type, string method, int token,
            PurchaseCostKind costKind, string effectKey)
        {
            return new PurchaseDescriptor(key, PermanentCurrency.Experience, -1, type, method,
                token, key, PurchaseCostDescriptor.Formula(costKind), PurchaseUnlockRequirement.None,
                0, 0, new[] { AmountGain(effectKey) });
        }

        private static PurchaseDescriptor ExpFixed(string key, string type, string method,
            int token, long cost, string stateKey)
        {
            return new PurchaseDescriptor(key, PermanentCurrency.Experience, -1, type, method,
                token, key, Fixed(cost), PurchaseUnlockRequirement.None, 0, 0,
                new[] { Set(stateKey) });
        }

        private static void AddAp(PurchaseDescriptor[] target, int id, string display,
            string method, int token, PurchaseCostDescriptor cost,
            params PurchaseEffectDescriptor[] effects)
        {
            AddAp(target, id, display, method, token, cost,
                PurchaseUnlockRequirement.None, effects);
        }

        private static void AddAp(PurchaseDescriptor[] target, int id, string display,
            string method, int token, PurchaseCostDescriptor cost,
            PurchaseUnlockRequirement unlock, params PurchaseEffectDescriptor[] effects)
        {
            target[id] = new PurchaseDescriptor("ap." + id, PermanentCurrency.ArbitraryPoints,
                id, "ArbitraryController", method, token, display, cost, unlock,
                0, 0, effects);
        }

        private static void AddHeart(PurchaseDescriptor[] target, int id, string display,
            string method, int token, long cost, int itemId)
        {
            target[id] = new PurchaseDescriptor("ap." + id, PermanentCurrency.ArbitraryPoints,
                id, "ArbitraryController", method, token, display, Fixed(cost),
                PurchaseUnlockRequirement.None, itemId, 10, new[]
                {
                    new PurchaseEffectDescriptor("inventory.item." + itemId + ".count",
                        PurchaseEffectKind.HeartItemCount, 1, long.MaxValue),
                    new PurchaseEffectDescriptor("inventory.item." + itemId + ".levelContribution",
                        PurchaseEffectKind.HeartLevelContribution, 11, long.MaxValue)
                });
        }

        private static PurchaseCostDescriptor Fixed(long cost)
        {
            return PurchaseCostDescriptor.Fixed(cost);
        }

        private static PurchaseEffectDescriptor Delta(string key, long amount)
        {
            return new PurchaseEffectDescriptor(key, PurchaseEffectKind.ExactDelta,
                amount, long.MaxValue);
        }

        private static PurchaseEffectDescriptor Set(string key)
        {
            return new PurchaseEffectDescriptor(key, PurchaseEffectKind.SetOne, 1, 1);
        }

        private static PurchaseEffectDescriptor Capped(string key, long amount, long maximum)
        {
            return new PurchaseEffectDescriptor(key, PurchaseEffectKind.CappedDelta,
                amount, maximum);
        }

        private static PurchaseEffectDescriptor NativeGain(string key)
        {
            return new PurchaseEffectDescriptor(key, PurchaseEffectKind.PositiveNativePreview,
                0, long.MaxValue);
        }

        private static PurchaseEffectDescriptor AmountGain(string key)
        {
            return new PurchaseEffectDescriptor(key, PurchaseEffectKind.CostStateAmountDelta,
                0, long.MaxValue);
        }
    }
}
