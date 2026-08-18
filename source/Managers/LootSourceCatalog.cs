/*
FILE PURPOSE

Purpose: Pure, build-pinned source catalog and collection-state model for ordinary Adventure and
Titan equipment. It gives collection routing exact source identity, per-ID merge contribution,
correlated branch shape, permanent reward transitions, online-only cadence evidence, Daycare
ownership, and inventory-service state without reading or mutating a Character.

Mechanism: Immutable zone/source/branch descriptors mirror the installed 1.260 loot and Item List
tables. CollectionItemState derives debt from itemMaxxed plus exact physical copies; itemDropped is
retained only as telemetry. Pirate loot is represented as one uniform one-of-eight branch per
eligible kill, while T12 END rolls retain their cumulative version predicates as independent
branches. CollectionServiceState consumes PhysicalTopology and LootCapacity proofs. Exact-signature
cadence samples never accept offline time.

Inputs and outputs: Pure observations (MAXX/drop flags, exact physical copies, reference demand),
rooted loot factor, combat signatures, and ordinary topology snapshots produce typed collection
items, sparse stochastic outcomes, numeric set rewards, cadence evidence, and capacity proofs.

Invariants and safety: A held level L has contribution deficit 100-L; a fresh level-zero object has
deficit 100 and separately needs its first physical arrival. Daycare is ownership, not ordinary
delivery capacity. Known sources exist before their first roll. Set rewards apply only on the
false->true completion edge. Pirate completion has zero progression value. Ordinary and Titan
equipment have zero offline eligible trials. No expected-value proof authorizes capacity.

Extension points and non-goals: Task 29 may wire report hooks and route-value seconds into the
global scheduler. This catalog does not invoke loot, merge, Daycare, filter, loadout, or Titan
controllers and does not pretend unknown ordinary probabilities are exact.
*/
using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Autopilot;

namespace NGUInjector.Managers
{
    internal enum LootSourceKind
    {
        OrdinaryZone,
        Titan
    }

    internal enum LootEnemyClass
    {
        Any,
        Ordinary,
        Boss,
        Titan
    }

    internal enum LootBranchShape
    {
        Unspecified,
        Independent,
        UniformOneOf
    }

    internal enum CollectionPhysicalLocation
    {
        OrdinaryInventory,
        Equipped,
        Daycare
    }

    internal enum CollectionRewardMetric
    {
        EnergySpeed,
        EnergyPower,
        MagicPower,
        MagicCap,
        MagicPerBar,
        AdventurePower,
        AdventureToughness,
        AdventureHp,
        AdventureRegen,
        Experience,
        SpawnRate,
        DropChance,
        BonusLootLevelChance,
        BoostEffectivenessMultiplier,
        PerkPointRate,
        NguSpeed,
        WishSpeed,
        Portrait
    }

    internal sealed class LootProbabilityLaw
    {
        internal readonly double Coefficient;
        internal readonly double Cap;
        internal readonly bool UsesRootedLootFactor;
        internal readonly string Formula;

        internal LootProbabilityLaw(double coefficient, double cap,
            bool usesRootedLootFactor, string formula)
        {
            if (double.IsNaN(coefficient) || double.IsInfinity(coefficient) || coefficient < 0.0)
                throw new ArgumentOutOfRangeException("coefficient");
            if (double.IsNaN(cap) || double.IsInfinity(cap) || cap < 0.0 || cap > 1.0)
                throw new ArgumentOutOfRangeException("cap");
            Coefficient = coefficient;
            Cap = cap;
            UsesRootedLootFactor = usesRootedLootFactor;
            Formula = formula ?? string.Empty;
        }

        internal double Evaluate(double lootFactor, double rootedLootFactor)
        {
            ValidateFactor(lootFactor, "lootFactor");
            ValidateFactor(rootedLootFactor, "rootedLootFactor");
            var factor = UsesRootedLootFactor ? rootedLootFactor : lootFactor;
            return Math.Min(Cap, Coefficient * factor);
        }

        private static void ValidateFactor(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                throw new ArgumentOutOfRangeException(name);
        }
    }

    internal sealed class LootBranchDescriptor
    {
        private readonly int[] _itemIds;
        internal readonly string Id;
        internal readonly int Zone;
        internal readonly LootSourceKind SourceKind;
        internal readonly LootEnemyClass EnemyClass;
        internal readonly LootBranchShape Shape;
        internal readonly int MinimumTitanVersion;
        internal readonly int BaseLevel;
        internal readonly int WorstCaseCatalogEmissions;
        internal readonly bool OnlineOnly;
        internal readonly LootProbabilityLaw Probability;

        internal LootBranchDescriptor(string id, int zone, LootSourceKind sourceKind,
            LootEnemyClass enemyClass, LootBranchShape shape, int minimumTitanVersion,
            int baseLevel, int worstCaseCatalogEmissions, bool onlineOnly,
            LootProbabilityLaw probability, int[] itemIds)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Branch ID required.", "id");
            if (zone < 0) throw new ArgumentOutOfRangeException("zone");
            if (minimumTitanVersion < 0 || minimumTitanVersion > 4)
                throw new ArgumentOutOfRangeException("minimumTitanVersion");
            if (baseLevel < 0 || baseLevel > 100) throw new ArgumentOutOfRangeException("baseLevel");
            if (worstCaseCatalogEmissions < 0)
                throw new ArgumentOutOfRangeException("worstCaseCatalogEmissions");
            if (probability == null) throw new ArgumentNullException("probability");
            if (itemIds == null || itemIds.Length == 0)
                throw new ArgumentException("At least one item ID is required.", "itemIds");
            if (itemIds.Any(x => x <= 0) || itemIds.Distinct().Count() != itemIds.Length)
                throw new ArgumentException("Branch item IDs must be distinct and positive.", "itemIds");
            Id = id;
            Zone = zone;
            SourceKind = sourceKind;
            EnemyClass = enemyClass;
            Shape = shape;
            MinimumTitanVersion = minimumTitanVersion;
            BaseLevel = baseLevel;
            WorstCaseCatalogEmissions = worstCaseCatalogEmissions;
            OnlineOnly = onlineOnly;
            Probability = probability;
            _itemIds = (int[])itemIds.Clone();
        }

        internal int[] ItemIds() { return (int[])_itemIds.Clone(); }

        internal bool ContainsItem(int itemId)
        {
            return Array.IndexOf(_itemIds, itemId) >= 0;
        }

        internal double EligibleTrials(double elapsedSeconds, double secondsPerTrial, bool online)
        {
            if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds)
                || elapsedSeconds < 0.0)
                throw new ArgumentOutOfRangeException("elapsedSeconds");
            if (double.IsNaN(secondsPerTrial) || double.IsInfinity(secondsPerTrial)
                || secondsPerTrial <= 0.0)
                throw new ArgumentOutOfRangeException("secondsPerTrial");
            if (OnlineOnly && !online) return 0.0;
            return Math.Floor(elapsedSeconds / secondsPerTrial);
        }

        internal VectorOutcome[] BuildOutcomes(int[] orderedDebtItemIds,
            double lootFactor, double rootedLootFactor)
        {
            if (orderedDebtItemIds == null) throw new ArgumentNullException("orderedDebtItemIds");
            if (orderedDebtItemIds.Any(x => x <= 0)
                || orderedDebtItemIds.Distinct().Count() != orderedDebtItemIds.Length)
                throw new ArgumentException("Debt IDs must be distinct and positive.", "orderedDebtItemIds");
            var probability = Probability.Evaluate(lootFactor, rootedLootFactor);
            var outcomes = new List<VectorOutcome>();
            if (Shape == LootBranchShape.UniformOneOf)
            {
                outcomes.Add(new VectorOutcome(Id + ":none", 1.0 - probability,
                    new int[orderedDebtItemIds.Length]));
                var each = probability / _itemIds.Length;
                for (var item = 0; item < _itemIds.Length; item++)
                {
                    var contribution = new int[orderedDebtItemIds.Length];
                    var debtIndex = Array.IndexOf(orderedDebtItemIds, _itemIds[item]);
                    if (debtIndex >= 0) contribution[debtIndex] = BaseLevel + 1;
                    outcomes.Add(new VectorOutcome(Id + ":" + _itemIds[item], each, contribution));
                }
                return outcomes.ToArray();
            }

            if (Shape == LootBranchShape.Independent && _itemIds.Length == 1)
            {
                var contribution = new int[orderedDebtItemIds.Length];
                var debtIndex = Array.IndexOf(orderedDebtItemIds, _itemIds[0]);
                if (debtIndex >= 0) contribution[debtIndex] = BaseLevel + 1;
                outcomes.Add(new VectorOutcome(Id + ":none", 1.0 - probability,
                    new int[orderedDebtItemIds.Length]));
                outcomes.Add(new VectorOutcome(Id + ":" + _itemIds[0], probability, contribution));
                return outcomes.ToArray();
            }
            throw new InvalidOperationException("This branch shape requires a source-specific joint builder.");
        }
    }

    internal sealed class LootItemSourceMetadata
    {
        internal readonly int ItemId;
        internal readonly int Zone;
        internal readonly LootSourceKind SourceKind;
        internal readonly bool IsCoreSetItem;
        internal readonly bool IsOptional;
        internal readonly bool OnlineOnly;
        internal readonly bool SafeExactFilterOnceMaxxed;
        internal readonly int MinimumTitanVersion;
        internal readonly int BaseLevel;
        internal readonly string SourceGroupId;
        internal readonly string Evidence;

        internal LootItemSourceMetadata(int itemId, int zone, LootSourceKind sourceKind,
            bool isCoreSetItem, bool onlineOnly, bool safeExactFilterOnceMaxxed,
            int minimumTitanVersion, int baseLevel, string sourceGroupId, string evidence)
        {
            if (itemId <= 0) throw new ArgumentOutOfRangeException("itemId");
            ItemId = itemId;
            Zone = zone;
            SourceKind = sourceKind;
            IsCoreSetItem = isCoreSetItem;
            IsOptional = !isCoreSetItem;
            OnlineOnly = onlineOnly;
            SafeExactFilterOnceMaxxed = safeExactFilterOnceMaxxed;
            MinimumTitanVersion = minimumTitanVersion;
            BaseLevel = baseLevel;
            SourceGroupId = sourceGroupId ?? string.Empty;
            Evidence = evidence ?? string.Empty;
        }

        internal int ContributionPerBaseDrop { get { return BaseLevel + 1; } }
    }

    internal sealed class CollectionRewardEffect
    {
        internal readonly CollectionRewardMetric Metric;
        internal readonly double Amount;
        internal readonly bool IsMultiplier;

        internal CollectionRewardEffect(CollectionRewardMetric metric, double amount, bool isMultiplier)
        {
            if (double.IsNaN(amount) || double.IsInfinity(amount))
                throw new ArgumentOutOfRangeException("amount");
            Metric = metric;
            Amount = amount;
            IsMultiplier = isMultiplier;
        }
    }

    internal sealed class CollectionSetRewardDescriptor
    {
        private readonly CollectionRewardEffect[] _effects;
        internal readonly string SetKey;
        internal readonly string DisplayName;
        internal readonly bool NumericSourceExact;
        internal readonly bool CosmeticOnly;
        internal readonly string Description;

        internal CollectionSetRewardDescriptor(string setKey, string displayName,
            bool numericSourceExact, bool cosmeticOnly, string description,
            CollectionRewardEffect[] effects)
        {
            SetKey = setKey ?? string.Empty;
            DisplayName = displayName ?? SetKey;
            NumericSourceExact = numericSourceExact;
            CosmeticOnly = cosmeticOnly;
            Description = description ?? string.Empty;
            _effects = effects == null ? new CollectionRewardEffect[0]
                : (CollectionRewardEffect[])effects.Clone();
        }

        internal CollectionRewardEffect[] Effects()
        {
            return (CollectionRewardEffect[])_effects.Clone();
        }

        internal double NativeProgressionMagnitude
        {
            get
            {
                var sum = 0.0;
                for (var i = 0; i < _effects.Length; i++)
                    if (_effects[i].Metric != CollectionRewardMetric.Portrait)
                        sum += Math.Abs(_effects[i].IsMultiplier
                            ? _effects[i].Amount - 1.0 : _effects[i].Amount);
                return sum;
            }
        }
    }

    internal sealed class CollectionRewardTransition
    {
        internal readonly bool Applied;
        internal readonly double NativeProgressionMagnitude;
        internal readonly double UsefulGearSecondsSaved;
        internal readonly double TotalKnownSecondsSaved;
        internal readonly string Evidence;

        internal CollectionRewardTransition(bool applied, double nativeProgressionMagnitude,
            double usefulGearSecondsSaved, string evidence)
        {
            Applied = applied;
            NativeProgressionMagnitude = nativeProgressionMagnitude;
            UsefulGearSecondsSaved = usefulGearSecondsSaved;
            // Native effect dimensions are deliberately not fabricated into seconds. The task-14
            // objective supplies only the already-comparable useful-gear seconds component.
            TotalKnownSecondsSaved = usefulGearSecondsSaved;
            Evidence = evidence ?? string.Empty;
        }
    }

    internal static class CollectionRewardModel
    {
        internal static CollectionRewardTransition Evaluate(CollectionSetRewardDescriptor reward,
            bool wasComplete, bool isComplete, double usefulGearSecondsSaved,
            OptimizationObjective objective)
        {
            if (reward == null) throw new ArgumentNullException("reward");
            if (double.IsNaN(usefulGearSecondsSaved) || double.IsInfinity(usefulGearSecondsSaved)
                || usefulGearSecondsSaved < 0.0)
                throw new ArgumentOutOfRangeException("usefulGearSecondsSaved");
            var applied = !wasComplete && isComplete;
            var gearValue = objective != null && objective.ValuesLoot
                ? usefulGearSecondsSaved : 0.0;
            return new CollectionRewardTransition(applied,
                applied ? reward.NativeProgressionMagnitude : 0.0,
                gearValue,
                applied
                    ? reward.CosmeticOnly
                        ? "Cosmetic set transition has no terminal value; useful gear is valued separately."
                        : "Native numeric effects apply once on the incomplete-to-complete transition."
                    : "Set reward was already claimed or the set is not yet complete.");
        }
    }

    internal sealed class LootZoneDescriptor
    {
        private readonly LootItemSourceMetadata[] _items;
        private readonly int[] _coreItemIds;
        private readonly LootBranchDescriptor[] _branches;
        internal readonly int Zone;
        internal readonly LootSourceKind SourceKind;
        internal readonly string Name;
        internal readonly int WorstCaseTransientSlots;
        internal readonly CollectionSetRewardDescriptor SetReward;

        internal LootZoneDescriptor(int zone, LootSourceKind sourceKind, string name,
            LootItemSourceMetadata[] items, int[] coreItemIds,
            LootBranchDescriptor[] branches, int worstCaseTransientSlots,
            CollectionSetRewardDescriptor setReward)
        {
            Zone = zone;
            SourceKind = sourceKind;
            Name = name ?? ("zone " + zone);
            _items = items == null ? new LootItemSourceMetadata[0]
                : (LootItemSourceMetadata[])items.Clone();
            _coreItemIds = coreItemIds == null ? new int[0] : (int[])coreItemIds.Clone();
            _branches = branches == null ? new LootBranchDescriptor[0]
                : (LootBranchDescriptor[])branches.Clone();
            WorstCaseTransientSlots = worstCaseTransientSlots;
            SetReward = setReward;
        }

        internal LootItemSourceMetadata[] Items() { return (LootItemSourceMetadata[])_items.Clone(); }
        internal int[] CoreItemIds() { return (int[])_coreItemIds.Clone(); }
        internal LootBranchDescriptor[] Branches() { return (LootBranchDescriptor[])_branches.Clone(); }
        internal bool HasCoreSet { get { return _coreItemIds.Length > 0; } }
    }

    internal sealed class CollectionPhysicalCopy
    {
        internal readonly int ItemId;
        internal readonly int PhysicalLevel;
        internal readonly int EffectiveLevel;
        internal readonly CollectionPhysicalLocation Location;
        internal readonly object Identity;
        internal readonly bool HasReferenceObligation;

        internal CollectionPhysicalCopy(int itemId, int physicalLevel, int effectiveLevel,
            CollectionPhysicalLocation location, object identity, bool hasReferenceObligation)
        {
            if (itemId <= 0) throw new ArgumentOutOfRangeException("itemId");
            if (physicalLevel < 0 || physicalLevel > 100) throw new ArgumentOutOfRangeException("physicalLevel");
            if (effectiveLevel < physicalLevel || effectiveLevel > 100)
                throw new ArgumentOutOfRangeException("effectiveLevel");
            ItemId = itemId;
            PhysicalLevel = physicalLevel;
            EffectiveLevel = effectiveLevel;
            Location = location;
            Identity = identity;
            HasReferenceObligation = hasReferenceObligation;
        }
    }

    internal sealed class CollectionItemObservation
    {
        private readonly CollectionPhysicalCopy[] _copies;
        internal readonly int ItemId;
        internal readonly bool ItemMaxxed;
        internal readonly bool ItemDropped;
        internal readonly int RequiredSimultaneousCopies;

        internal CollectionItemObservation(int itemId, bool itemMaxxed, bool itemDropped,
            int requiredSimultaneousCopies, CollectionPhysicalCopy[] copies)
        {
            if (itemId <= 0) throw new ArgumentOutOfRangeException("itemId");
            if (requiredSimultaneousCopies < 1)
                throw new ArgumentOutOfRangeException("requiredSimultaneousCopies");
            _copies = copies == null ? new CollectionPhysicalCopy[0]
                : (CollectionPhysicalCopy[])copies.Clone();
            if (_copies.Any(x => x == null || x.ItemId != itemId))
                throw new ArgumentException("Every copy must match the observed item ID.", "copies");
            ItemId = itemId;
            ItemMaxxed = itemMaxxed;
            ItemDropped = itemDropped;
            RequiredSimultaneousCopies = requiredSimultaneousCopies;
        }

        internal CollectionPhysicalCopy[] Copies() { return (CollectionPhysicalCopy[])_copies.Clone(); }
    }

    internal sealed class CollectionItemState
    {
        private readonly LootItemSourceMetadata[] _sources;
        internal readonly int ItemId;
        internal readonly bool ItemMaxxed;
        internal readonly bool ItemDroppedTelemetry;
        internal readonly bool HasSourceBackedDebt;
        internal readonly bool PhysicallyOwned;
        internal readonly bool OwnedInDaycare;
        internal readonly bool NeedsDaycareMaterialization;
        internal readonly bool NeedsInitialCopy;
        internal readonly int BestEffectiveLevel;
        internal readonly int RemainingContribution;
        internal readonly int PhysicalCopyCount;
        internal readonly int RequiredSimultaneousCopies;
        internal readonly int ProjectedPersistentSlots;
        internal readonly int MergeServiceBacklog;
        internal readonly int ImmediatelyMergeableContribution;
        internal readonly int ReferenceProtectedCopies;

        private CollectionItemState(CollectionItemObservation observation,
            LootItemSourceMetadata[] sources)
        {
            var copies = observation.Copies();
            ItemId = observation.ItemId;
            ItemMaxxed = observation.ItemMaxxed;
            ItemDroppedTelemetry = observation.ItemDropped;
            _sources = sources == null ? new LootItemSourceMetadata[0]
                : (LootItemSourceMetadata[])sources.Clone();
            PhysicalCopyCount = copies.Length;
            PhysicallyOwned = PhysicalCopyCount > 0;
            OwnedInDaycare = copies.Any(x => x.Location == CollectionPhysicalLocation.Daycare);
            RequiredSimultaneousCopies = observation.RequiredSimultaneousCopies;
            ReferenceProtectedCopies = copies.Count(x => x.HasReferenceObligation);

            // Preserve every simultaneous/reference-obligated copy, then retain the strongest
            // remaining objects until physical demand is met. Only surplus ordinary/equipped
            // objects without a reference obligation are immediate merge service.
            var survivors = new List<CollectionPhysicalCopy>();
            survivors.AddRange(copies.Where(x => x.Location == CollectionPhysicalLocation.Daycare
                || x.HasReferenceObligation));
            foreach (var copy in copies.Where(x => !survivors.Contains(x))
                         .OrderByDescending(x => x.EffectiveLevel))
            {
                if (survivors.Count >= RequiredSimultaneousCopies) break;
                survivors.Add(copy);
            }
            var mergeable = copies.Where(x => !survivors.Contains(x)
                && x.Location != CollectionPhysicalLocation.Daycare
                && !x.HasReferenceObligation).ToArray();
            ImmediatelyMergeableContribution = mergeable.Sum(x => x.PhysicalLevel + 1);
            var projectedBest = copies.Length == 0 ? 0 : copies.Max(x => x.EffectiveLevel);
            var ordinarySurvivor = survivors.Where(x => x.Location != CollectionPhysicalLocation.Daycare)
                .OrderByDescending(x => x.EffectiveLevel).FirstOrDefault();
            if (ordinarySurvivor != null)
                projectedBest = Math.Max(projectedBest,
                    Math.Min(100, ordinarySurvivor.EffectiveLevel + ImmediatelyMergeableContribution));
            BestEffectiveLevel = projectedBest;
            RemainingContribution = ItemMaxxed ? 0
                : MechanicsStochastic.AdditionalLevelZeroCopiesToMax(BestEffectiveLevel);
            NeedsInitialCopy = !ItemMaxxed && copies.Length == 0;
            NeedsDaycareMaterialization = !ItemMaxxed && copies.Any(x =>
                x.Location == CollectionPhysicalLocation.Daycare && x.EffectiveLevel >= 100);
            HasSourceBackedDebt = !ItemMaxxed && _sources.Length > 0;
            ProjectedPersistentSlots = Math.Max(0, RequiredSimultaneousCopies - PhysicalCopyCount);
            MergeServiceBacklog = mergeable.Length;
        }

        internal static CollectionItemState Build(CollectionItemObservation observation,
            IEnumerable<LootItemSourceMetadata> sources)
        {
            if (observation == null) throw new ArgumentNullException("observation");
            var exactSources = sources == null ? new LootItemSourceMetadata[0]
                : sources.Where(x => x != null && x.ItemId == observation.ItemId).ToArray();
            return new CollectionItemState(observation, exactSources);
        }

        internal LootItemSourceMetadata[] Sources()
        {
            return (LootItemSourceMetadata[])_sources.Clone();
        }
    }

    internal sealed class CollectionServiceState
    {
        internal readonly int UsableFreeSlots;
        internal readonly int UsableTotalSlots;
        internal readonly int PersistentSlotDebt;
        internal readonly int MergeServiceBacklog;
        internal readonly int DaycareOwnedItems;
        internal readonly int ReferenceProtectedCopies;
        internal readonly int WorstCaseTransientSlots;
        internal readonly int PostActionReserveSlots;
        internal readonly LootCapacityProof Capacity;

        internal CollectionServiceState(OrdinaryInventoryTopology topology,
            IEnumerable<CollectionItemState> items, int worstCaseTransientSlots,
            int postActionReserveSlots)
        {
            if (topology == null) throw new ArgumentNullException("topology");
            if (worstCaseTransientSlots < 0) throw new ArgumentOutOfRangeException("worstCaseTransientSlots");
            if (postActionReserveSlots < 0) throw new ArgumentOutOfRangeException("postActionReserveSlots");
            var states = items == null ? new CollectionItemState[0]
                : items.Where(x => x != null).ToArray();
            UsableFreeSlots = topology.UsableFreeSlotCount;
            UsableTotalSlots = topology.UsableSlotCount;
            PersistentSlotDebt = states.Sum(x => x.ProjectedPersistentSlots);
            MergeServiceBacklog = states.Sum(x => x.MergeServiceBacklog);
            DaycareOwnedItems = states.Count(x => x.OwnedInDaycare);
            ReferenceProtectedCopies = states.Sum(x => x.ReferenceProtectedCopies);
            WorstCaseTransientSlots = worstCaseTransientSlots;
            PostActionReserveSlots = postActionReserveSlots;
            Capacity = LootCapacity.ProveOrdinary(topology,
                LootCapacityRequirement.ExactBatch("collection-service",
                    worstCaseTransientSlots, postActionReserveSlots));
        }

        internal ForecastCapacityProof ForecastProof()
        {
            return ForecastCapacityProof.Prove(Capacity.RequiredFreeSlots,
                Capacity.UsableFreeSlotCount, false, true, Capacity.Reason);
        }
    }

    internal sealed class CollectionCombatSignature
    {
        internal readonly int Zone;
        internal readonly bool BossOnly;
        internal readonly bool FastCombat;
        internal readonly bool BeastMode;
        internal readonly double Power;
        internal readonly double Toughness;
        internal readonly double CurrentHp;
        internal readonly double MaximumHp;
        internal readonly double Regen;
        internal readonly string LoadoutSignature;
        internal readonly long ObjectiveEpoch;
        internal readonly string Key;

        internal CollectionCombatSignature(int zone, bool bossOnly, bool fastCombat,
            bool beastMode, double power, double toughness, double currentHp,
            double maximumHp, double regen, string loadoutSignature, long objectiveEpoch)
        {
            if (zone < 0) throw new ArgumentOutOfRangeException("zone");
            Validate(power, "power");
            Validate(toughness, "toughness");
            Validate(currentHp, "currentHp");
            Validate(maximumHp, "maximumHp");
            Validate(regen, "regen");
            if (objectiveEpoch < 0L) throw new ArgumentOutOfRangeException("objectiveEpoch");
            Zone = zone;
            BossOnly = bossOnly;
            FastCombat = fastCombat;
            BeastMode = beastMode;
            Power = power;
            Toughness = toughness;
            CurrentHp = currentHp;
            MaximumHp = maximumHp;
            Regen = regen;
            LoadoutSignature = loadoutSignature ?? string.Empty;
            ObjectiveEpoch = objectiveEpoch;
            Key = zone + "|" + (bossOnly ? "boss" : "all") + "|"
                  + (fastCombat ? "fast" : "full") + "|"
                  + (beastMode ? "beast" : "normal") + "|p=" + power.ToString("R")
                  + "|t=" + toughness.ToString("R") + "|hp=" + currentHp.ToString("R")
                  + "/" + maximumHp.ToString("R") + "|r=" + regen.ToString("R")
                  + "|gear=" + LoadoutSignature + "|epoch=" + objectiveEpoch;
        }

        private static void Validate(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                throw new ArgumentOutOfRangeException(name);
        }
    }

    internal sealed class CollectionCadenceSample
    {
        internal readonly string SignatureKey;
        internal readonly double MeanSecondsPerTrial;
        internal readonly int OnlineSamples;

        internal CollectionCadenceSample(string signatureKey, double meanSecondsPerTrial,
            int onlineSamples)
        {
            SignatureKey = signatureKey ?? string.Empty;
            MeanSecondsPerTrial = meanSecondsPerTrial;
            OnlineSamples = onlineSamples;
        }
    }

    internal sealed class CollectionCadenceLedger
    {
        private sealed class MutableSample
        {
            internal double TotalSeconds;
            internal int Count;
        }

        private readonly Dictionary<string, MutableSample> _samples =
            new Dictionary<string, MutableSample>();

        internal bool Record(CollectionCombatSignature signature, double seconds, bool online)
        {
            if (signature == null) throw new ArgumentNullException("signature");
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0.0)
                throw new ArgumentOutOfRangeException("seconds");
            if (!online) return false;
            MutableSample sample;
            if (!_samples.TryGetValue(signature.Key, out sample))
            {
                sample = new MutableSample();
                _samples[signature.Key] = sample;
            }
            sample.TotalSeconds += seconds;
            sample.Count++;
            return true;
        }

        internal bool TryGet(CollectionCombatSignature signature, out CollectionCadenceSample result)
        {
            if (signature == null) throw new ArgumentNullException("signature");
            MutableSample sample;
            if (_samples.TryGetValue(signature.Key, out sample) && sample.Count > 0)
            {
                result = new CollectionCadenceSample(signature.Key,
                    sample.TotalSeconds / sample.Count, sample.Count);
                return true;
            }
            result = null;
            return false;
        }
    }

    internal static class LootSourceCatalog
    {
        internal const string SourceHash =
            "Assembly-CSharp:f138c8555f3e3aa9b6661b45e569258125a798ff77555d42eeeaa61fb71eaf71";

        private static readonly Dictionary<int, LootZoneDescriptor> Ordinary = BuildOrdinary();
        private static readonly Dictionary<int, LootZoneDescriptor> Titans = BuildTitans();
        private static readonly Dictionary<string, CollectionSetRewardDescriptor> GlobalRewards =
            BuildGlobalRewards();

        internal static LootZoneDescriptor OrdinaryZone(int zone)
        {
            LootZoneDescriptor result;
            return Ordinary.TryGetValue(zone, out result) ? result : null;
        }

        internal static LootZoneDescriptor TitanZone(int zone)
        {
            LootZoneDescriptor result;
            return Titans.TryGetValue(zone, out result) ? result : null;
        }

        internal static int[] OrdinaryZoneIds()
        {
            return Ordinary.Keys.OrderBy(x => x).ToArray();
        }

        internal static LootItemSourceMetadata[] SourcesForItem(int itemId)
        {
            if (itemId <= 0) return new LootItemSourceMetadata[0];
            return Ordinary.Values.Concat(Titans.Values)
                .SelectMany(x => x.Items()).Where(x => x.ItemId == itemId).ToArray();
        }

        internal static bool IsKnownSafeExactFilterItem(int itemId)
        {
            var sources = SourcesForItem(itemId);
            return sources.Length > 0 && sources.All(x => x.SafeExactFilterOnceMaxxed);
        }

        internal static CollectionSetRewardDescriptor GlobalSetReward(string setKey)
        {
            CollectionSetRewardDescriptor reward;
            return setKey != null && GlobalRewards.TryGetValue(setKey, out reward) ? reward : null;
        }

        internal static VectorOutcome[] PirateMixedOutcomes(int[] orderedDebtItemIds,
            double rootedLootFactor, double bossSpawnShare)
        {
            if (double.IsNaN(bossSpawnShare) || double.IsInfinity(bossSpawnShare)
                || bossSpawnShare < 0.0 || bossSpawnShare > 1.0)
                throw new ArgumentOutOfRangeException("bossSpawnShare");
            var zone = OrdinaryZone(43);
            var branches = zone.Branches();
            var normal = branches.First(x => x.EnemyClass == LootEnemyClass.Ordinary)
                .Probability.Evaluate(rootedLootFactor, rootedLootFactor);
            var boss = branches.First(x => x.EnemyClass == LootEnemyClass.Boss)
                .Probability.Evaluate(rootedLootFactor, rootedLootFactor);
            var groupChance = normal * (1.0 - bossSpawnShare) + boss * bossSpawnShare;
            var pirateIds = zone.CoreItemIds();
            var outcomes = new List<VectorOutcome>();
            outcomes.Add(new VectorOutcome("zone43-pirate:none", 1.0 - groupChance,
                new int[orderedDebtItemIds.Length]));
            var each = groupChance / pirateIds.Length;
            for (var i = 0; i < pirateIds.Length; i++)
            {
                var contribution = new int[orderedDebtItemIds.Length];
                var index = Array.IndexOf(orderedDebtItemIds, pirateIds[i]);
                if (index >= 0) contribution[index] = 1;
                outcomes.Add(new VectorOutcome("zone43-pirate:" + pirateIds[i], each, contribution));
            }
            return outcomes.ToArray();
        }

        private static Dictionary<int, LootZoneDescriptor> BuildOrdinary()
        {
            var data = new Dictionary<int, int[]>
            {
                {0, A(120,75,62,65,64,63)},
                {1, A(40,41,42,43,44,45,46,77,278)},
                {2, A(135,47,48,49,50,51,52,53,432,281)},
                {3, A(54,55,56,57,58,59,60,61,53,433)},
                {4, A(66,67,172,53,434)},
                {5, A(68,69,70,71,72,73,74,53,66,435,283)},
                {7, A(85,86,87,88,89,90,91,66,436,368)},
                {9, A(95,96,97,98,99,100,101,437,279)},
                {10, A(103,104,105,106,107,108,109,110,66,438)},
                {12, A(122,123,124,125,126,127,66,439,282)},
                {13, A(130,131,132,133,134,339,76,440,287)},
                {15, A(143,144,145,146,147,148,76,441,367,285)},
                {17, A(164,165,166,167,168,67,128,94,163,442)},
                {18, A(173,174,175,176,177,94,163,128,178,443)},
                {20, A(221,222,223,224,225,226,227,142,444,369,280)},
                {21, A(213,214,215,216,217,218,219,220,142,445,284)},
                {22, A(231,232,233,234,235,236,142,446,370,286)},
                {24, A(251,252,253,254,255,256,257,142,128,447)},
                {25, A(258,259,260,261,262,263,264,142,128,448)},
                {27, A(301,302,303,304,305,306,307,142,128,449)},
                {28, A(308,309,310,311,312,313,314,142,128,450)},
                {29, A(315,316,317,318,319,320,321,142,128,451,371)},
                {31, A(345,346,347,348,349,350,351,170,169,452)},
                {32, A(352,353,354,355,356,357,358,229,230)},
                {33, A(359,360,361,362,363,364,365,366,229,230)},
                {35, A(392,393,394,395,396,397,398,399,229,230)},
                {36, A(400,401,402,403,404,405,406,407,229,230)},
                {37, A(408,409,410,411,412,413,414,415,229,230)},
                {39, A(453,454,455,456,457,458,459,460,295,296)},
                {40, A(496,497,498,499,500,501,502,503,295,296)},
                {41, A(461,462,463,464,465,466,467,468,295,296)},
                {43, A(507,508,509,510,511,512,513,514,295,296)}
            };
            var core = new Dictionary<int, int[]>
            {
                {0,A(62,63,64,65,75)}, {1,Range(40,46)}, {2,Range(47,53)}, {3,Range(54,61)},
                {5,Range(68,74)}, {7,Range(85,91)}, {9,Range(95,101)}, {10,Range(103,109)},
                {12,Range(122,126)}, {13,Range(130,134)}, {15,Range(143,147)},
                {17,Range(164,168)}, {18,Range(173,177)}, {20,Range(221,225)},
                {21,A(213,214,215,216,217,218,219)}, {22,Range(231,236)},
                {24,Range(251,257)}, {25,Range(258,264)}, {27,Range(301,307)},
                {28,Range(308,314)}, {29,Range(315,321)}, {31,Range(345,351)},
                {32,Range(352,358)}, {33,Range(359,365)}, {35,Range(392,399)},
                {36,Range(400,407)}, {37,Range(408,415)}, {39,Range(453,460)},
                {40,Range(496,503)}, {41,Range(461,468)}, {43,Range(507,514)}
            };
            var result = new Dictionary<int, LootZoneDescriptor>();
            foreach (var pair in data)
            {
                int[] coreIds;
                if (!core.TryGetValue(pair.Key, out coreIds)) coreIds = new int[0];
                var branches = pair.Key == 43 ? PirateBranches() : new LootBranchDescriptor[0];
                var worstBatch = pair.Key == 43 ? 3 : 1;
                result[pair.Key] = BuildZone(pair.Key, LootSourceKind.OrdinaryZone,
                    "ordinary-zone-" + pair.Key, pair.Value, coreIds, branches, worstBatch,
                    RewardForZone(pair.Key));
            }
            return result;
        }

        private static Dictionary<string, CollectionSetRewardDescriptor> BuildGlobalRewards()
        {
            return new Dictionary<string, CollectionSetRewardDescriptor>
            {
                {
                    "normal-bonus-accessories",
                    Reward("normal-bonus-accessories", "Normal Bonus Accessories", true, false,
                        "+25% drop chance after IDs 432..444 are all MAXXED",
                        E(CollectionRewardMetric.DropChance, .25))
                }
            };
        }

        private static Dictionary<int, LootZoneDescriptor> BuildTitans()
        {
            var result = new Dictionary<int, LootZoneDescriptor>();
            AddTitan(result, 6, "GRB", Range(78,84), Range(78,84), 1, null);
            AddTitan(result, 8, "Titan 2", new int[0], new int[0], 1, null);
            AddTitan(result, 11, "Jake", Range(111,117), Range(111,117), 1, null);
            AddTitan(result, 14, "UUG", A(136,137,138,139,140,141), A(136,137,138,139,140), 1, null);
            AddTitan(result, 16, "Walderp", A(150,151,152,153,155,156,157,158),
                A(150,151,152,153,155,156,157,158), 1, null);
            AddTitan(result, 19, "Beast", Range(184,195), Range(184,188), 1, null);
            AddTitan(result, 23, "Nerd", Range(237,249), Range(237,241), 1, null);
            AddTitan(result, 26, "Godmother", Range(265,277), Range(265,271), 1, null);
            AddTitan(result, 30, "Exile", Range(322,334), Range(322,326), 1, null);
            AddTitan(result, 34, "Space", Range(373,386), Range(373,379), 1, null);
            AddTitan(result, 38, "Rock Lobster", Range(416,429), Range(416,423), 1, null);

            var t12Items = Range(469,479).Concat(A(483,489,493,484)).ToArray();
            var t12Branches = new[]
            {
                IndependentTitanBranch("t12-end-483", 483, 1, 1.4e-8),
                IndependentTitanBranch("t12-end-489", 489, 2, 1.0e-8),
                IndependentTitanBranch("t12-end-493", 493, 3, 8.0e-9),
                IndependentTitanBranch("t12-end-484", 484, 4, 6.0e-9)
            };
            AddTitan(result, 42, "Amalgamate", t12Items, Range(469,476), 1, t12Branches);
            return result;
        }

        private static void AddTitan(IDictionary<int, LootZoneDescriptor> result, int zone,
            string name, int[] items, int[] core, int minimumVersion,
            LootBranchDescriptor[] branches)
        {
            var sources = new List<LootItemSourceMetadata>();
            foreach (var id in items)
            {
                var version = id == 489 ? 2 : id == 493 ? 3 : id == 484 ? 4 : minimumVersion;
                var terminal = id == 483 || id == 489 || id == 493 || id == 484;
                sources.Add(new LootItemSourceMetadata(id, zone, LootSourceKind.Titan,
                    Array.IndexOf(core, id) >= 0, true, !terminal, version,
                    terminal ? 100 : 4, "titan-zone-" + zone,
                    SourceHash + ":LootDrop.zone" + zone + "Drop"));
            }
            result[zone] = new LootZoneDescriptor(zone, LootSourceKind.Titan, name,
                sources.ToArray(), core, branches, zone == 42 ? 18 : Math.Max(1, items.Length),
                RewardForTitanZone(zone));
        }

        private static LootZoneDescriptor BuildZone(int zone, LootSourceKind kind, string name,
            int[] items, int[] core, LootBranchDescriptor[] branches, int worstBatch,
            CollectionSetRewardDescriptor reward)
        {
            var metadata = items.Distinct().Select(id => new LootItemSourceMetadata(id, zone, kind,
                Array.IndexOf(core, id) >= 0, true, true, 0, 0,
                zone == 43 && id >= 507 && id <= 514 ? "zone43-pirate-one-of-eight"
                    : "ordinary-zone-" + zone,
                SourceHash + ":LootDrop.zone" + zone + "Drop")).ToArray();
            return new LootZoneDescriptor(zone, kind, name, metadata, core, branches,
                worstBatch, reward);
        }

        private static LootBranchDescriptor[] PirateBranches()
        {
            var ids = Range(507, 514);
            return new[]
            {
                new LootBranchDescriptor("zone43-pirate-normal", 43,
                    LootSourceKind.OrdinaryZone, LootEnemyClass.Ordinary,
                    LootBranchShape.UniformOneOf, 0, 0, 1, true,
                    new LootProbabilityLaw(4e-9, .05, true,
                        "min(4e-9 * lootFactorRooted(), 0.05); one uniform ID 507..514"), ids),
                new LootBranchDescriptor("zone43-pirate-boss", 43,
                    LootSourceKind.OrdinaryZone, LootEnemyClass.Boss,
                    LootBranchShape.UniformOneOf, 0, 0, 1, true,
                    new LootProbabilityLaw(1.2e-8, .15, true,
                        "min(1.2e-8 * lootFactorRooted(), 0.15); one uniform ID 507..514"), ids)
            };
        }

        private static LootBranchDescriptor IndependentTitanBranch(string id, int itemId,
            int minimumVersion, double coefficient)
        {
            return new LootBranchDescriptor(id, 42, LootSourceKind.Titan,
                LootEnemyClass.Titan, LootBranchShape.Independent, minimumVersion,
                100, 1, true, new LootProbabilityLaw(coefficient, .25, true,
                    "min(" + coefficient.ToString("R") + " * lootFactorRooted(), 0.25)"),
                A(itemId));
        }

        private static CollectionSetRewardDescriptor RewardForZone(int zone)
        {
            switch (zone)
            {
                case 0: return Reward("training", "Training", true, false,
                    "+2 Energy Speed and 10 EXP", E(CollectionRewardMetric.EnergySpeed,2), E(CollectionRewardMetric.Experience,10));
                case 1: return Reward("sewers", "Sewers", true, false,
                    "+5 Adventure Power/Toughness, +15 HP, +0.2 regen, and 20 EXP",
                    E(CollectionRewardMetric.AdventurePower,5), E(CollectionRewardMetric.AdventureToughness,5),
                    E(CollectionRewardMetric.AdventureHp,15), E(CollectionRewardMetric.AdventureRegen,.2), E(CollectionRewardMetric.Experience,20));
                case 2: return Reward("forest", "Forest", true, false,
                    "+5 Energy Power and 200 EXP", E(CollectionRewardMetric.EnergyPower,5), E(CollectionRewardMetric.Experience,200));
                case 3: return Reward("cave", "Cave", true, false,
                    "+2 Magic Power, +40,000 Magic Cap, +2 Magic Per Bar, and 300 EXP",
                    E(CollectionRewardMetric.MagicPower,2), E(CollectionRewardMetric.MagicCap,40000),
                    E(CollectionRewardMetric.MagicPerBar,2), E(CollectionRewardMetric.Experience,300));
                case 7: return Reward("clock", "Clock", true, false, "+5% spawn rate",
                    E(CollectionRewardMetric.SpawnRate,.05));
                case 9: return Reward("2d", "2D", true, false, "+7.43% drop chance",
                    E(CollectionRewardMetric.DropChance,.0743));
                case 12: return Reward("gaudy", "Gaudy", true, false, "+10% bonus loot-level chance",
                    E(CollectionRewardMetric.BonusLootLevelChance,.10));
                case 17: return Reward("badly-drawn", "Badly Drawn", true, false, "x1.2 boost effectiveness",
                    M(CollectionRewardMetric.BoostEffectivenessMultiplier,1.2));
                case 22: return Reward("pretty", "Pretty", true, false, "+10% PP",
                    E(CollectionRewardMetric.PerkPointRate,.10));
                case 24: return Reward("meta", "Meta", true, false, "+20% NGU speed",
                    E(CollectionRewardMetric.NguSpeed,.20));
                case 27: return Reward("typo", "Typo", true, false, "+20% Wish speed",
                    E(CollectionRewardMetric.WishSpeed,.20));
                case 37: return Reward("halloweenies", "Halloweenies", true, false, "+45% PP",
                    E(CollectionRewardMetric.PerkPointRate,.45));
                case 39: return Reward("construction", "Construction", true, false, "x1.2 boost effectiveness",
                    M(CollectionRewardMetric.BoostEffectivenessMultiplier,1.2));
                case 43: return Reward("pirate", "Pirate", true, true,
                    "Portrait 66 (Pride and Accomplishment); no progression multiplier",
                    E(CollectionRewardMetric.Portrait,66));
                default: return Reward("zone-" + zone, "Zone " + zone, false, false,
                    "Numeric native reward has not yet been converted into this catalog");
            }
        }

        private static CollectionSetRewardDescriptor RewardForTitanZone(int zone)
        {
            if (zone == 6)
                return Reward("grb", "GRB", true, false, "2,000 EXP and x2 safe-zone regeneration",
                    E(CollectionRewardMetric.Experience,2000), M(CollectionRewardMetric.AdventureRegen,2));
            return Reward("titan-" + zone, "Titan zone " + zone, false, false,
                "Numeric native reward has not yet been converted into this catalog");
        }

        private static CollectionSetRewardDescriptor Reward(string key, string name,
            bool exact, bool cosmetic, string description, params CollectionRewardEffect[] effects)
        {
            return new CollectionSetRewardDescriptor(key, name, exact, cosmetic,
                description, effects);
        }

        private static CollectionRewardEffect E(CollectionRewardMetric metric, double amount)
        {
            return new CollectionRewardEffect(metric, amount, false);
        }

        private static CollectionRewardEffect M(CollectionRewardMetric metric, double amount)
        {
            return new CollectionRewardEffect(metric, amount, true);
        }

        private static int[] A(params int[] values) { return values; }

        private static int[] Range(int first, int last)
        {
            return Enumerable.Range(first, last - first + 1).ToArray();
        }
    }
}
