using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

AdventureCollectionPlanner converts fightable zones and Item List state into permanent MAXX debt.
It takes stronger forward gear first, then backfills older sets. Optional accessories and rare
equipment remain protected collection debt, but own Adventure only when their completed physical
copy improves a real combat/production loadout, finishes an immediately claimable permanent global
reward, or has a source-probability plus exact online-cadence value that beats ITOPOD. It also owns
collection-aware inventory pressure and protection queries. Drop-source tables are audited from
LootDrop; unknown/misc IDs are filtered by native item type. Never treat a set as disposable until
its game completion flag is confirmed, and never price inaccessible future set members as a reward.
*/
namespace NGUInjector.Managers
{
    internal sealed class AdventureCollectionTarget
    {
        internal ZoneTarget Target;
        internal bool IsBackfill;
        internal bool BossOnly;
        internal int RemainingItems;
        internal int ProjectedNewSlots;
        internal int RequiredFreeReserve;
        internal int IncompleteZones;
        internal int RemainingContribution;
        internal int OwnedInDaycare;
        internal int MergeServiceBacklog;
        internal int ReferenceProtectedCopies;
        internal int UsableInventoryFreeSlots;
        internal bool CapacityAdmitted;
        internal bool OnlineOnly = true;
        internal double UsefulBoostDebt;
        internal double UsefulBoostGain;
        internal double SetRewardNativeMagnitude;
        internal bool CoreSetIncomplete;
        internal bool StrategicDebt;
        internal double OptionalProgressionGain;
        internal double OptionalCombatGain;
        internal double OptionalProductionGain;
        internal int OptionalProgressionItemId;
        internal string OptionalProgressionTarget = string.Empty;
        internal string OptionalProgressionKind = string.Empty;
        internal string UsefulBoostTarget = string.Empty;
        internal string CadenceSignature = string.Empty;
        internal double ObservedKillSeconds = -1.0;
        internal double ExpectedTargetDropSeconds = -1.0;
        internal double TargetDropConfidenceSeconds = -1.0;
        internal bool NeedsCadenceProbe;
        internal double BossSpawnShare;
        internal string StochasticEvidence = "No calibrated drop forecast is available";
        internal string SetReward = "No unclaimed core-set reward";
        internal string Reason = "Collection planner is waiting for Adventure state";
        internal string MissingSummary = "unknown";
    }

    // Item List completion is permanent and survives rebirths.  The fastest route is
    // normally to snipe the newest usable gear first, then use the resulting Drop
    // Chance and one-hit kills to repay older collection debt.  This planner keeps
    // that debt explicit instead of assuming that the highest stat-safe zone is the
    // only useful Adventure target.
    internal static class AdventureCollectionPlanner
    {
        private static readonly CollectionCadenceLedger Cadence = new CollectionCadenceLedger();

        private sealed class OptionalValue
        {
            internal ZoneDebt Debt;
            internal int ItemId;
            internal double Gain;
            internal double CombatGain;
            internal double ProductionGain;
            internal double RewardMagnitude;
            internal string Target = string.Empty;
            internal string Kind = string.Empty;
            internal string Reward = string.Empty;
        }

        internal static AdventureCollectionTarget Evaluate(Character c, ZoneTarget front)
        {
            var result = new AdventureCollectionTarget();
            if (c == null || front == null || ZoneStatHelper.UserOverrides == null
                || c.inventory == null || c.inventory.itemList == null || c.itemInfo == null)
                return result;

            var reachable = ZoneStatHelper.UserOverrides.Keys
                .Where(zone => zone <= ZoneHelpers.GetMaxReachableZone(false))
                .Where(zone => ZoneStatHelper.UserOverrides[zone]
                    .FightType(c.totalAdvAttack(), c.totalAdvDefense()) > 0)
                .OrderByDescending(zone => zone).ToList();
            if (reachable.Count == 0) return result;

            var debts = reachable.Select(zone => DebtFor(c, zone)).Where(x => x.HasDebt).ToList();
            result.IncompleteZones = debts.Count;

            // Forward gear remains authoritative while the newest fightable set is
            // incomplete. Once core sets are finished, optional debt is ranked by an
            // immediately claimable global reward first, then by its best real combat or
            // production-loadout gain. A valueless optional is retained as telemetry and
            // inventory debt, but it cannot hide a valuable item in another reachable zone.
            var frontDebt = debts.FirstOrDefault(x => x.Zone == front.Zone);
            ZoneDebt selected = null;
            OptionalValue selectedOptional = null;
            if (frontDebt != null && frontDebt.CoreSetIncomplete)
                selected = frontDebt;
            if (selected == null)
                selected = debts.Where(x => x.CoreSetIncomplete).OrderBy(x => x.Zone).FirstOrDefault();
            if (selected == null)
            {
                var optional = debts.Where(x => !x.CoreSetIncomplete)
                    .Select(x => AssessOptionalValue(c, x)).ToList();
                selectedOptional = optional.Where(x => x.RewardMagnitude > 0.0)
                    .OrderByDescending(x => x.RewardMagnitude)
                    .ThenBy(x => x.Debt.Zone).FirstOrDefault();
                if (selectedOptional == null)
                    selectedOptional = optional.Where(x => x.Gain > 1e-7)
                        .OrderByDescending(x => x.Gain)
                        .ThenByDescending(x => x.Debt.Zone).FirstOrDefault();
                if (selectedOptional != null) selected = selectedOptional.Debt;
            }
            if (selected == null && frontDebt != null) selected = frontDebt;
            if (selected == null)
                selected = debts.OrderByDescending(x => x.Zone).FirstOrDefault();

            if (selected == null)
            {
                result.Target = front;
                result.Reason = "Every known obtainable equipment entry in all fightable zones is MAXXED; using the best progression zone";
                result.MissingSummary = "collection complete through " + ZoneName(front.Zone);
                return result;
            }

            var stats = ZoneStatHelper.UserOverrides[selected.Zone];
            result.Target = new ZoneTarget
            {
                Zone = selected.Zone,
                FightType = stats.FightType(c.totalAdvAttack(), c.totalAdvDefense())
            };
            result.IsBackfill = selected.Zone < front.Zone;
            result.CoreSetIncomplete = selected.CoreSetIncomplete;
            if (!selected.CoreSetIncomplete && selectedOptional == null)
                selectedOptional = AssessOptionalValue(c, selected);
            result.SetReward = selected.CoreSetIncomplete ? CoreSetReward(selected.Zone)
                : selectedOptional != null && selectedOptional.RewardMagnitude > 0.0
                    ? selectedOptional.Reward : "Core-set reward already claimed";
            result.SetRewardNativeMagnitude = selected.CoreSetIncomplete
                ? selected.SetRewardNativeMagnitude
                : selectedOptional == null ? 0.0 : selectedOptional.RewardMagnitude;
            double usefulBoostDebt = 0.0;
            double usefulBoostGain = 0.0;
            string usefulBoostTarget = string.Empty;
            var needsNormalEnemyBoosts = selected.CoreSetIncomplete
                && ProgressionLoadoutOptimizer.TryGetUsefulBoostDebt(c, out usefulBoostDebt,
                    out usefulBoostGain, out usefulBoostTarget);
            result.UsefulBoostDebt = needsNormalEnemyBoosts ? usefulBoostDebt : 0.0;
            result.UsefulBoostGain = needsNormalEnemyBoosts ? usefulBoostGain : 0.0;
            result.UsefulBoostTarget = needsNormalEnemyBoosts ? usefulBoostTarget : string.Empty;
            var optionalProgression = !selected.CoreSetIncomplete && selectedOptional != null
                                      && selectedOptional.Gain > 1e-7;
            result.OptionalProgressionGain = optionalProgression ? selectedOptional.Gain : 0.0;
            result.OptionalCombatGain = optionalProgression ? selectedOptional.CombatGain : 0.0;
            result.OptionalProductionGain = optionalProgression
                ? selectedOptional.ProductionGain : 0.0;
            result.OptionalProgressionItemId = selectedOptional == null
                ? 0 : selectedOptional.ItemId;
            result.OptionalProgressionTarget = optionalProgression
                ? selectedOptional.Target : string.Empty;
            result.OptionalProgressionKind = optionalProgression
                ? selectedOptional.Kind : string.Empty;
            result.StrategicDebt = StrategicDebtOwnsAdventure(selected.CoreSetIncomplete,
                result.SetRewardNativeMagnitude, result.UsefulBoostGain,
                result.OptionalProgressionGain);

            // Bosses are the fast source of duplicate set pieces and early-zone EXP, while ordinary
            // enemies are the source of Power/Toughness boost drops. Pure boss sniping is therefore
            // valid only when it does not cut off the supply needed to make an owned, demonstrably
            // better item win the complete loadout. Full-clear still encounters bosses naturally.
            // Native ordinary-zone loot gives relevant chances to both normal enemies and bosses.
            // A set being incomplete does not prove that discarding every normal spawn improves
            // time-to-MAXX. Keep the full-clear branch unless the exact remaining target is proven
            // boss-exclusive; one-time boss-only mechanics are modeled separately by
            // MajorUnlockPlanner with source-audited probabilities.
            result.BossOnly = selected.OnlyBossExclusiveDebt && !needsNormalEnemyBoosts;
            result.RemainingItems = selected.RemainingItems;
            result.RemainingContribution = selected.RemainingContribution;
            result.ProjectedNewSlots = selected.ProjectedNewSlots;
            result.OwnedInDaycare = selected.OwnedInDaycare;
            result.MergeServiceBacklog = selected.MergeServiceBacklog;
            result.ReferenceProtectedCopies = selected.ReferenceProtectedCopies;
            result.RequiredFreeReserve = selected.Service == null
                ? Math.Max(3, selected.WorstCaseTransientSlots + 2)
                : selected.Service.Capacity.RequiredFreeSlots;
            result.UsableInventoryFreeSlots = selected.Service == null
                ? FreeInventorySlots(c) : selected.Service.UsableFreeSlots;
            result.CapacityAdmitted = selected.Service != null && selected.Service.Capacity.Admitted;
            result.MissingSummary = selected.MissingSummary;
            PopulateStochasticEvidence(c, result, selected);
            result.Reason = selected.CoreSetIncomplete
                ? needsNormalEnemyBoosts
                    ? "Full-clearing for ordinary-enemy boosts while bosses advance the MAXX set: "
                      + Math.Ceiling(usefulBoostDebt) + " boost points on " + usefulBoostTarget
                      + " have a proven complete-loadout gain; unclaimed set reward is " + result.SetReward
                    : (result.IsBackfill ? "Full-clearing an older incomplete MAXX set because no source-derived boss-snipe advantage is proven; unclaimed set reward is " + result.SetReward
                        : "Full-clearing the newest incomplete MAXX set because no source-derived boss-snipe advantage is proven; unclaimed set reward is " + result.SetReward)
                : result.SetRewardNativeMagnitude > 0.0
                    ? "Completing " + (selectedOptional == null ? "this optional item"
                        : selectedOptional.Target) + " would immediately claim " + result.SetReward
                    : optionalProgression
                    ? "Optional MAXX debt is eligible for measured route comparison because completed "
                      + selectedOptional.Target + " improves the " + selectedOptional.Kind
                      + " loadout by " + selectedOptional.Gain.ToString("0.######")
                    : "Optional MAXX debt is tracked and protected, but has no proven equipped, set-reward, or progression value; it does not outrank ITOPOD";
            return result;
        }

        internal static bool StrategicDebtOwnsAdventure(bool coreSetIncomplete,
            double setRewardMagnitude, double usefulBoostGain,
            double optionalProgressionGain)
        {
            return coreSetIncomplete || setRewardMagnitude > 0.0
                   || usefulBoostGain > 0.0 || optionalProgressionGain > 1e-7;
        }

        private static OptionalValue AssessOptionalValue(Character c, ZoneDebt debt)
        {
            var result = new OptionalValue {Debt = debt};
            if (c == null || debt == null || debt.Items == null) return result;
            foreach (var state in debt.Items)
            {
                if (state == null) continue;
                var sources = state.Sources();
                if (sources.Length == 0 || sources.Any(x => x.IsCoreSetItem)) continue;

                CollectionSetRewardDescriptor globalReward;
                if (TryGetImmediateGlobalReward(c, state.ItemId, out globalReward)
                    && globalReward.NativeProgressionMagnitude > result.RewardMagnitude)
                {
                    result.ItemId = state.ItemId;
                    result.Target = ItemName(c, state.ItemId);
                    result.Gain = 0.0;
                    result.CombatGain = 0.0;
                    result.ProductionGain = 0.0;
                    result.Kind = string.Empty;
                    result.RewardMagnitude = globalReward.NativeProgressionMagnitude;
                    result.Reward = globalReward.Description + " [becomes claimable when "
                                    + result.Target + " reaches MAXX]";
                }

                if (result.RewardMagnitude > 0.0 && result.ItemId != state.ItemId)
                    continue;

                foreach (var copy in PhysicalCopiesFor(c, state.ItemId))
                {
                    var equipment = copy.Identity as Equipment;
                    if (equipment == null) continue;
                    var combat = ProgressionLoadoutOptimizer.MaxxedFullyBoostedLoadoutGain(
                        c, equipment);
                    var production = ProgressionLoadoutOptimizer
                        .MaxxedFullyBoostedProductionLoadoutGain(c, equipment);
                    var projected = Math.Max(combat, production);
                    if (result.RewardMagnitude > 0.0 && result.ItemId == state.ItemId)
                    {
                        result.CombatGain = Math.Max(result.CombatGain, combat);
                        result.ProductionGain = Math.Max(result.ProductionGain, production);
                        result.Gain = Math.Max(result.Gain, projected);
                        result.Kind = production > combat ? "resource-production" : "combat";
                        continue;
                    }
                    if (projected <= result.Gain) continue;
                    result.ItemId = state.ItemId;
                    result.Gain = projected;
                    result.CombatGain = combat;
                    result.ProductionGain = production;
                    result.Target = ItemName(c, state.ItemId);
                    result.Kind = production > combat ? "resource-production" : "combat";
                }
            }
            return result;
        }

        private static bool TryGetImmediateGlobalReward(Character c, int developedItemId,
            out CollectionSetRewardDescriptor reward)
        {
            reward = null;
            if (c == null || c.inventory == null || c.inventory.itemList == null)
                return false;
            foreach (var global in LootSourceCatalog.GlobalSetsForItem(developedItemId))
            {
                if (GlobalSetRewardClaimed(c, global.SetKey)) continue;
                if (!global.WouldComplete(developedItemId, id => IsMaxxed(c, id))) continue;
                reward = global.Reward;
                return reward != null && reward.NumericSourceExact;
            }
            return false;
        }

        private static bool GlobalSetRewardClaimed(Character c, string setKey)
        {
            if (c == null || c.inventory == null || c.inventory.itemList == null) return true;
            switch (setKey)
            {
                case "normal-bonus-accessories":
                    return c.inventory.itemList.normalBonusAccComplete;
                default:
                    // Unknown completion storage cannot safely authorize a repeated reward.
                    return true;
            }
        }

        internal static int FreeInventorySlots(Character c)
        {
            var topology = InventoryManager.CaptureOrdinaryTopology(c);
            return topology == null ? 0 : topology.UsableFreeSlotCount;
        }

        internal static int TotalInventorySlots(Character c)
        {
            var topology = InventoryManager.CaptureOrdinaryTopology(c);
            return topology == null ? 0 : topology.UsableSlotCount;
        }

        internal static bool InventoryPressureHigh(Character c, AdventureCollectionTarget collection)
        {
            var total = TotalInventorySlots(c);
            var free = FreeInventorySlots(c);
            if (total <= 0) return false;
            // Native admission never merges.  The source's exact per-call batch and two service
            // buffers therefore remain reserved even when a physical merge target already exists.
            var debtReserve = collection == null ? 3 : Math.Max(3, collection.RequiredFreeReserve);
            return free <= Math.Max(debtReserve, (int)Math.Ceiling(total * .10));
        }

        internal static bool InventoryPressureCritical(Character c)
        {
            return FreeInventorySlots(c) <= 2;
        }

        internal static string InventoryPressure(Character c, AdventureCollectionTarget collection)
        {
            var total = TotalInventorySlots(c);
            var free = FreeInventorySlots(c);
            if (total <= 0) return "unavailable";
            if (free <= 2) return "critical";
            return InventoryPressureHigh(c, collection) ? "high" : free <= Math.Ceiling(total * .20) ? "watch" : "healthy";
        }

        internal static bool HasFightableCollectionDebt(Character c)
        {
            if (c == null) return false;
            try
            {
                var front = ZoneStatHelper.GetBestZone();
                return front != null && Evaluate(c, front).IncompleteZones > 0;
            }
            catch
            {
                // Filtering is destructive at drop time.  If collection state cannot
                // be proven complete, the safe answer is to keep equipment enabled.
                return true;
            }
        }

        internal static bool IsProtectedCollectionItem(Character c, int id)
        {
            if (c == null || id <= 0) return true;
            var sources = LootSourceCatalog.SourcesForItem(id);
            // MAXX proves this exact ID's permanent entry, but it does not prove that the source
            // set is complete. Until the authoritative set flag flips, every piece from that zone
            // remains merge/service material. Unknown source identity also fails closed because the
            // bot cannot distinguish an ordinary repeat drop from a progression/state-machine item.
            return CollectionCopyRequiresRetention(IsMaxxed(c, id), sources.Length,
                sources.Length > 0 && sources.All(x => SourceSetComplete(c, x)));
        }

        internal static bool CollectionCopyRequiresRetention(bool exactIdMaxxed,
            int knownSourceCount, bool allSourceSetsComplete)
        {
            return !exactIdMaxxed || knownSourceCount <= 0 || !allSourceSetsComplete;
        }

        internal static bool IsKnownCompletedOrdinaryItem(Character c, int id)
        {
            if (c == null || id <= 0 || !IsMaxxed(c, id)) return false;
            var sources = LootSourceCatalog.SourcesForItem(id);
            return sources.Length > 0 && sources.All(x => x.SafeExactFilterOnceMaxxed)
                   && sources.All(x => SourceSetComplete(c, x));
        }

        private static bool SourceSetComplete(Character c, LootItemSourceMetadata source)
        {
            if (c == null || c.inventory == null || c.inventory.itemList == null || source == null)
                return false;
            var descriptor = source.SourceKind == LootSourceKind.OrdinaryZone
                ? LootSourceCatalog.OrdinaryZone(source.Zone)
                : LootSourceCatalog.TitanZone(source.Zone);
            if (descriptor == null) return false;
            if (!descriptor.HasCoreSet) return true;
            if (source.SourceKind == LootSourceKind.OrdinaryZone)
                return CoreSetComplete(c, source.Zone);

            var list = c.inventory.itemList;
            switch (source.Zone)
            {
                case 6: return list.GRBComplete;
                case 11: return list.jakeComplete;
                case 14: return list.uugComplete;
                case 16: return list.waldoComplete;
                case 19: return list.beast1complete;
                case 23: return list.nerdComplete;
                case 26: return list.godmotherComplete;
                case 30: return list.exileComplete;
                case 34: return list.spaceComplete;
                case 38: return list.rockLobsterComplete;
                case 42: return list.amalgamateComplete;
                default: return false;
            }
        }

        internal static bool CoreSetComplete(Character c, int zone)
        {
            if (!HasCoreSet(zone)) return true;
            var list = c.inventory.itemList;
            switch (zone)
            {
                case 0: return list.trainingComplete;
                case 1: return list.sewersComplete;
                case 2: return list.forestComplete;
                case 3: return list.caveComplete;
                case 5: return list.HSBComplete;
                case 7: return list.clockComplete;
                case 9: return list.twoDComplete;
                case 10: return list.ghostComplete;
                case 12: return list.gaudyComplete;
                case 13: return list.megaComplete;
                case 15: return list.beardverseComplete;
                case 17: return list.badlyDrawnComplete;
                case 18: return list.stealthComplete;
                case 20: return list.chocoComplete;
                case 21: return list.edgyComplete;
                case 22: return list.prettyComplete;
                case 24: return list.metaComplete;
                case 25: return list.partyComplete;
                case 27: return list.typoComplete;
                case 28: return list.fadComplete;
                case 29: return list.jrpgComplete;
                case 31: return list.radComplete;
                case 32: return list.schoolComplete;
                case 33: return list.westernComplete;
                case 35: return list.breadverseComplete;
                case 36: return list.that70sComplete;
                case 37: return list.halloweeniesComplete;
                case 39: return list.constructionComplete;
                case 40: return list.duckComplete;
                case 41: return list.netherComplete;
                case 43: return list.pirateComplete;
                default: return true;
            }
        }

        private static bool HasCoreSet(int zone)
        {
            var source = LootSourceCatalog.OrdinaryZone(zone);
            return source != null && source.HasCoreSet;
        }

        private static ZoneDebt DebtFor(Character c, int zone)
        {
            var catalog = LootSourceCatalog.OrdinaryZone(zone);
            var debt = new ZoneDebt
            {
                Zone = zone,
                CoreSetIncomplete = !CoreSetComplete(c, zone),
                WorstCaseTransientSlots = catalog == null ? 1 : catalog.WorstCaseTransientSlots,
                SetRewardNativeMagnitude = catalog == null || catalog.SetReward == null
                    ? 0.0 : catalog.SetReward.NativeProgressionMagnitude
            };
            if (catalog == null) return debt;
            var missing = new List<string>();
            var states = new List<CollectionItemState>();
            foreach (var source in catalog.Items().GroupBy(x => x.ItemId).Select(x => x.First()))
            {
                if (!IsEquipment(c, source.ItemId)) continue;
                var copies = PhysicalCopiesFor(c, source.ItemId);
                // Active/configured/native loadout contexts are mutually exclusive and need one
                // survivor; every Daycare copy is simultaneous with that survivor.
                var requiredCopies = 1
                    + copies.Count(x => x.Location == CollectionPhysicalLocation.Daycare);
                var state = CollectionItemState.Build(new CollectionItemObservation(
                    source.ItemId, IsMaxxed(c, source.ItemId), IsDropped(c, source.ItemId),
                    requiredCopies, copies.ToArray()), LootSourceCatalog.SourcesForItem(source.ItemId));
                states.Add(state);
                // itemDropped is deliberately not an availability gate. A source-known optional
                // that has never rolled remains permanent MAXX debt until policy values it at zero.
                if (!state.HasSourceBackedDebt) continue;
                missing.Add(ItemName(c, source.ItemId));
            }
            var outstanding = states.Where(x => x.HasSourceBackedDebt).ToList();
            debt.Items = outstanding;
            debt.RemainingItems = outstanding.Count;
            debt.RemainingContribution = outstanding.Sum(x => x.RemainingContribution);
            debt.ProjectedNewSlots = outstanding.Sum(x => x.ProjectedPersistentSlots);
            debt.OwnedInDaycare = outstanding.Count(x => x.OwnedInDaycare);
            debt.MergeServiceBacklog = outstanding.Sum(x => x.MergeServiceBacklog);
            debt.ReferenceProtectedCopies = outstanding.Sum(x => x.ReferenceProtectedCopies);
            // Neither currently audited ordinary branch is boss-exclusive. Zone 43 explicitly has
            // separate normal and boss one-of-eight laws, so a full clear remains valid.
            debt.OnlyBossExclusiveDebt = false;
            var topology = InventoryManager.CaptureOrdinaryTopology(c);
            if (topology != null)
                debt.Service = new CollectionServiceState(topology, outstanding,
                    debt.WorstCaseTransientSlots, 2);
            if (debt.CoreSetIncomplete && debt.RemainingItems == 0)
            {
                // Fail closed if a future game version changes the set table without updating this
                // audit: retain one explicit unit of debt and one reserve slot rather than declaring
                // a native-incomplete set complete.
                debt.RemainingItems = 1;
                debt.ProjectedNewSlots = Math.Max(1, debt.ProjectedNewSlots);
            }
            debt.HasDebt = debt.CoreSetIncomplete || missing.Count > 0;
            var preview = string.Join(", ", missing.Take(3).ToArray());
            if (missing.Count > 3) preview += " +" + (missing.Count - 3) + " more";
            debt.MissingSummary = debt.CoreSetIncomplete
                ? "incomplete " + ZoneName(zone) + " set" + (preview.Length > 0 ? "; " + preview : string.Empty)
                : preview.Length > 0 ? preview : "unresolved equipment entry";
            return debt;
        }

        private static List<CollectionPhysicalCopy> PhysicalCopiesFor(Character c, int itemId)
        {
            var result = new List<CollectionPhysicalCopy>();
            if (c == null || c.inventory == null) return result;
            Action<Equipment, CollectionPhysicalLocation, bool, int> add = (item, location, referenced, effective) =>
            {
                if (item == null || item.id != itemId) return;
                result.Add(new CollectionPhysicalCopy(item.id, Math.Min(100, Math.Max(0, item.level)),
                    Math.Min(100, Math.Max(item.level, effective)), location, item, referenced));
            };
            add(c.inventory.head, CollectionPhysicalLocation.Equipped,
                ProgressionLoadoutOptimizer.IsAuthoritativeItem(c.inventory.head), c.inventory.head == null ? 0 : c.inventory.head.level);
            add(c.inventory.chest, CollectionPhysicalLocation.Equipped,
                ProgressionLoadoutOptimizer.IsAuthoritativeItem(c.inventory.chest), c.inventory.chest == null ? 0 : c.inventory.chest.level);
            add(c.inventory.legs, CollectionPhysicalLocation.Equipped,
                ProgressionLoadoutOptimizer.IsAuthoritativeItem(c.inventory.legs), c.inventory.legs == null ? 0 : c.inventory.legs.level);
            add(c.inventory.boots, CollectionPhysicalLocation.Equipped,
                ProgressionLoadoutOptimizer.IsAuthoritativeItem(c.inventory.boots), c.inventory.boots == null ? 0 : c.inventory.boots.level);
            add(c.inventory.weapon, CollectionPhysicalLocation.Equipped,
                ProgressionLoadoutOptimizer.IsAuthoritativeItem(c.inventory.weapon), c.inventory.weapon == null ? 0 : c.inventory.weapon.level);
            add(c.inventory.weapon2, CollectionPhysicalLocation.Equipped,
                ProgressionLoadoutOptimizer.IsAuthoritativeItem(c.inventory.weapon2), c.inventory.weapon2 == null ? 0 : c.inventory.weapon2.level);
            if (c.inventory.accs != null)
                foreach (var item in c.inventory.accs)
                    add(item, CollectionPhysicalLocation.Equipped,
                        ProgressionLoadoutOptimizer.IsAuthoritativeItem(item), item == null ? 0 : item.level);
            if (c.inventory.inventory != null)
                for (var i = 0; i < c.inventory.inventory.Count; i++)
                {
                    var item = c.inventory.inventory[i];
                    add(item, CollectionPhysicalLocation.OrdinaryInventory,
                        item != null && (ProgressionLoadoutOptimizer.IsAuthoritativeItem(item)
                            || InventoryManager.IsNativeLoadoutReference(c, i)), item == null ? 0 : item.level);
                }
            if (c.inventory.daycare != null)
                for (var i = 0; i < c.inventory.daycare.Count; i++)
                {
                    var item = c.inventory.daycare[i];
                    var effective = item == null ? 0 : item.level;
                    try
                    {
                        if (item != null && c.inventoryController != null
                            && c.inventoryController.daycares != null
                            && i < c.inventoryController.daycares.Count
                            && c.inventoryController.daycares[i] != null)
                            effective += c.inventoryController.daycares[i].levelsAdded();
                    }
                    catch { }
                    add(item, CollectionPhysicalLocation.Daycare, true, Math.Min(100, effective));
                }
            return result;
        }

        private static bool IsEquipment(Character c, int id)
        {
            return id > 0 && c.itemInfo.type != null && id < c.itemInfo.type.Length
                   && c.itemInfo.type[id] >= part.Head && c.itemInfo.type[id] <= part.Accessory;
        }

        private static bool IsMaxxed(Character c, int id)
        {
            return c.inventory.itemList.itemMaxxed != null && id < c.inventory.itemList.itemMaxxed.Count
                   && c.inventory.itemList.itemMaxxed[id];
        }

        private static bool IsDropped(Character c, int id)
        {
            return c.inventory.itemList.itemDropped != null && id < c.inventory.itemList.itemDropped.Count
                   && c.inventory.itemList.itemDropped[id];
        }

        private static string ItemName(Character c, int id)
        {
            return c.itemInfo.itemName != null && id < c.itemInfo.itemName.Length
                ? c.itemInfo.itemName[id] : "item " + id;
        }

        private static string ZoneName(int zone)
        {
            return ZoneStatHelper.UserOverrides != null && ZoneStatHelper.UserOverrides.ContainsKey(zone)
                ? ZoneStatHelper.UserOverrides[zone].Name : "zone " + zone;
        }

        private static void PopulateStochasticEvidence(Character c, AdventureCollectionTarget target,
            ZoneDebt debt)
        {
            if (target == null || target.Target == null || c.adventureController == null) return;
            var zone = target.Target.Zone;
            var signature = CaptureCadenceSignature(c, zone, target.BossOnly);
            target.CadenceSignature = signature == null ? string.Empty : signature.Key;
            CollectionCadenceSample cadence = null;
            var hasCadence = signature != null
                             && Cadence.TryGetConservativeCompatible(signature, out cadence);
            target.ObservedKillSeconds = hasCadence ? cadence.MeanSecondsPerTrial : -1.0;
            try
            {
                var enemies = c.adventureController.enemyList[zone];
                if (enemies != null && enemies.Count > 0)
                {
                    var bosses = enemies.Count(enemy => enemy.enemyType == enemyType.boss
                        || enemy.enemyType.ToString().IndexOf("bigBoss",
                            StringComparison.OrdinalIgnoreCase) >= 0);
                    target.BossSpawnShare = bosses / (double)enemies.Count;
                }
            }
            catch
            {
                target.BossSpawnShare = 0.0;
            }
            if (!hasCadence)
            {
                target.NeedsCadenceProbe = target.OptionalProgressionGain > 1e-7
                    && target.Target != null && target.Target.FightType == 2
                    && HasIndependentOptionalProbability(target.Target.Zone,
                        target.OptionalProgressionItemId);
                var broadFallback = CombatManager.ObservedKillSeconds(zone, target.BossOnly);
                target.StochasticEvidence = target.NeedsCadenceProbe
                    ? "One easy online kill is needed to measure this route and equipped item set before optional farming can compete with ITOPOD"
                    : broadFallback > 0.0
                    ? "A broad zone sample exists (" + broadFallback.ToString("0.00")
                      + "s/kill) but does not prove the current route/equipment capability; no ETA is asserted"
                    : "No compatible online route/equipment sample; no equipment ETA is asserted";
                return;
            }

            if (TryPopulateIndependentOptionalForecast(c, target, debt, cadence))
                return;

            if (zone != 43 || debt == null || debt.Items == null || debt.Items.Count == 0)
            {
                target.StochasticEvidence = "Conservative compatible online cadence is "
                    + cadence.MeanSecondsPerTrial.ToString("0.00")
                    + "s/kill; this source has no exact branch probability model, so no ETA is asserted";
                return;
            }

            var pirate = debt.Items.Where(x => x.ItemId >= 507 && x.ItemId <= 514).ToList();
            if (pirate.Count == 0)
            {
                target.StochasticEvidence = "Exact online signature is available; remaining zone-43 debt is outside the Pirate probability branch";
                return;
            }
            var ids = pirate.Select(x => x.ItemId).ToArray();
            // An absent physical target needs its first level-zero arrival plus 100 merge
            // contributions. Keep public RemainingContribution at the audited 100 while the
            // stochastic state prices that separate acquisition event as deficit 101.
            var deficits = pirate.Select(x => (byte)Math.Min(101,
                x.RemainingContribution + (x.NeedsInitialCopy ? 1 : 0))).ToArray();
            double rootedLoot;
            try { rootedLoot = Math.Max(0.0, c.lootFactorRooted()); }
            catch { rootedLoot = 0.0; }
            var outcomes = LootSourceCatalog.PirateMixedOutcomes(ids, rootedLoot,
                target.BossSpawnShare);
            var evidence = new ForecastEvidence
            {
                Grade = ForecastEvidenceGrade.SourceExact,
                ProbabilitySource = "LootDrop.zone43Drop Pirate one-of-eight",
                CadenceSource = "same-route, same-equipped-items online cadence; current capability no weaker",
                SourceHash = LootSourceCatalog.SourceHash,
                SampleCount = cadence.OnlineSamples,
                OnlineOnly = true,
                Notes = "Normal and boss group probabilities are mixed by the current enemy-list boss share."
            };
            var capacity = debt.Service == null
                ? ForecastCapacityProof.Prove(debt.WorstCaseTransientSlots, 0, false, true,
                    "No live ordinary topology was available.")
                : debt.Service.ForecastProof();
            var forecast = MechanicsStochastic.SparseMonotoneForecast(deficits,
                outcomes, 50000, evidence, capacity);
            if (!forecast.Valid || double.IsInfinity(forecast.MeanTrials))
            {
                target.StochasticEvidence = "Pirate probability is source-exact and online-only, but "
                    + (forecast.InvalidReason.Length > 0 ? forecast.InvalidReason
                        : "the current collection state has no finite admitted forecast");
                return;
            }
            target.ExpectedTargetDropSeconds = forecast.MeanTrials * cadence.MeanSecondsPerTrial;
            target.TargetDropConfidenceSeconds = forecast.P90Trials == long.MaxValue
                ? -1.0 : forecast.P90Trials * cadence.MeanSecondsPerTrial;
            target.StochasticEvidence = (forecast.Exact ? "Exact" : "Bounded")
                + " source-backed Pirate forecast from " + cadence.OnlineSamples
                + " compatible online samples; ordinary/Titan offline progress contributes zero trials";
        }

        private static bool TryPopulateIndependentOptionalForecast(Character c,
            AdventureCollectionTarget target, ZoneDebt debt, CollectionCadenceSample cadence)
        {
            if (c == null || target == null || target.Target == null || debt == null
                || debt.Items == null || cadence == null || target.OptionalProgressionItemId <= 0)
                return false;
            var item = debt.Items.FirstOrDefault(x => x != null
                && x.ItemId == target.OptionalProgressionItemId);
            var catalog = LootSourceCatalog.OrdinaryZone(target.Target.Zone);
            var branch = catalog == null ? null : catalog.Branches().FirstOrDefault(x =>
                x.Shape == LootBranchShape.Independent
                && x.ContainsItem(target.OptionalProgressionItemId));
            if (item == null || branch == null) return false;

            double loot;
            double rootedLoot;
            try
            {
                loot = Math.Max(0.0, c.lootFactor());
                rootedLoot = Math.Max(0.0, c.lootFactorRooted());
            }
            catch
            {
                loot = 0.0;
                rootedLoot = 0.0;
            }
            var deficit = (byte)Math.Min(101,
                item.RemainingContribution + (item.NeedsInitialCopy ? 1 : 0));
            var outcomes = branch.BuildOutcomes(new[] {item.ItemId}, loot, rootedLoot);
            var evidence = new ForecastEvidence
            {
                Grade = ForecastEvidenceGrade.SourceExact,
                ProbabilitySource = branch.Id,
                CadenceSource = "same-route, same-equipped-items online cadence; current capability no weaker",
                SourceHash = LootSourceCatalog.SourceHash,
                SampleCount = cadence.OnlineSamples,
                OnlineOnly = true,
                Notes = "Independent bonus-accessory roll with native level-two merge contribution."
            };
            var capacity = debt.Service == null
                ? ForecastCapacityProof.Prove(debt.WorstCaseTransientSlots, 0, false, true,
                    "No live ordinary topology was available.")
                : debt.Service.ForecastProof();
            var forecast = MechanicsStochastic.SparseMonotoneForecast(new[] {deficit},
                outcomes, 50000, evidence, capacity);
            if (!forecast.Valid || double.IsInfinity(forecast.MeanTrials))
            {
                target.StochasticEvidence = "The optional-item probability is source-exact and online-only, but "
                    + (forecast.InvalidReason.Length > 0 ? forecast.InvalidReason
                        : "the current collection state has no finite admitted forecast");
                return true;
            }
            target.ExpectedTargetDropSeconds = forecast.MeanTrials * cadence.MeanSecondsPerTrial;
            target.TargetDropConfidenceSeconds = forecast.P90Trials == long.MaxValue
                ? -1.0 : forecast.P90Trials * cadence.MeanSecondsPerTrial;
            target.StochasticEvidence = (forecast.Exact ? "Exact" : "Bounded")
                + " source-backed optional-item forecast from " + cadence.OnlineSamples
                + " compatible online samples; offline Adventure contributes zero trials";
            return true;
        }

        private static bool HasIndependentOptionalProbability(int zone, int itemId)
        {
            if (zone < 0 || itemId <= 0) return false;
            var catalog = LootSourceCatalog.OrdinaryZone(zone);
            return catalog != null && catalog.Branches().Any(x =>
                x.Shape == LootBranchShape.Independent && x.ContainsItem(itemId));
        }

        // Report hooks: combat integration may record only a confirmed eligible online kill. The
        // signature is supplied by CaptureCadenceSignature before the fight and is never collapsed
        // to zone-only evidence. Retrieval may reuse it only for the same route/mode/equipped item
        // identities when the live character is no weaker than the recorded fight.
        internal static bool RecordOnlineEligibleKill(CollectionCombatSignature signature,
            double seconds)
        {
            return Cadence.Record(signature, seconds, true);
        }

        internal static CollectionCombatSignature CaptureCadenceSignature(Character c,
            int zone, bool bossOnly)
        {
            if (c == null || zone < 0) return null;
            var items = new[]
            {
                c.inventory == null ? null : c.inventory.head,
                c.inventory == null ? null : c.inventory.chest,
                c.inventory == null ? null : c.inventory.legs,
                c.inventory == null ? null : c.inventory.boots,
                c.inventory == null ? null : c.inventory.weapon,
                c.inventory == null ? null : c.inventory.weapon2
            }.Concat(c.inventory == null || c.inventory.accs == null
                ? Enumerable.Empty<Equipment>() : c.inventory.accs)
                .Where(x => x != null && x.id > 0)
                .Select(x => x.id.ToString()).ToArray();
            var fast = ZoneStatHelper.UserOverrides != null
                       && ZoneStatHelper.UserOverrides.ContainsKey(zone)
                       && ZoneStatHelper.UserOverrides[zone]
                           .FightType(c.totalAdvAttack(), c.totalAdvDefense()) == 2;
            var beast = c.adventure != null && c.adventure.beastModeOn;
            return new CollectionCombatSignature(zone, bossOnly, fast, beast,
                c.totalAdvAttack(), c.totalAdvDefense(),
                c.adventure == null ? 0.0 : c.adventure.curHP,
                c.totalAdvHP(), c.totalAdvHPRegen(), string.Join(",", items),
                Math.Max(0L, ProgressionLoadoutOptimizer.LastObjectiveEpoch));
        }

        /*
        NATIVE EARLY SET REWARDS

        A level-100 copy is not valued only as an equip candidate. Completing every required item in
        a zone invokes AllItemListController.checkforBonuses and grants a permanent set reward. These
        early values are mirrored from that shipped method and are surfaced in the decision model;
        unknown later sets remain collection debt but are not assigned a fabricated numeric reward.
        */
        private static string CoreSetReward(int zone)
        {
            var source = LootSourceCatalog.OrdinaryZone(zone);
            if (source == null || source.SetReward == null)
                return "no source-catalogued set reward";
            return source.SetReward.Description
                   + (source.SetReward.NumericSourceExact ? " [numeric source-exact]"
                       : " [numeric conversion pending]");
        }

        private sealed class ZoneDebt
        {
            internal int Zone;
            internal bool CoreSetIncomplete;
            internal bool HasDebt;
            internal int RemainingItems;
            internal int RemainingContribution;
            internal int ProjectedNewSlots;
            internal int OwnedInDaycare;
            internal int MergeServiceBacklog;
            internal int ReferenceProtectedCopies;
            internal int WorstCaseTransientSlots;
            internal double SetRewardNativeMagnitude;
            internal bool OnlyBossExclusiveDebt;
            internal string MissingSummary;
            internal List<CollectionItemState> Items;
            internal CollectionServiceState Service;
        }
    }
}
