using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static NGUInjector.Main;

/*
FILE PURPOSE

ProgressionLoadoutOptimizer selects the best physical equipment objects for the active boss,
Adventure, major-unlock, or resource-refill context, including ordered weapons and constrained
accessories. Hard major-unlock combat uses target-enemy kill/survival math and excludes unrelated
production bonuses; routine contexts may accept lower raw combat stats for a proven generation
ETA improvement. It executes reference-identity
native swap transactions, reclaims allocations before cap-lowering gear, verifies the final layout,
and rolls back on failure. ID-only equality and direct field assignment are unsafe because duplicate
copies and saved loadouts have physical identity.
*/
namespace NGUInjector.Managers
{
    // Chooses equipment as a complete progression set.  Native item contribution
    // methods are used so effectiveBossID scaling and per-item flooring stay exact.
    internal static class ProgressionLoadoutOptimizer
    {
        private const int BeamWidth = 384;
        private static int _lastFingerprint;
        private static double _lastRun;
        private static double _lastInventoryProbe;
        private static Plan _failedPlan;
        private static double _failedUntil;
        private static bool _searchExact;
        private static Plan _authoritativePlan;
        private static Plan _pendingPlan;
        private static string _authoritativeObjective = string.Empty;
        private static string _pendingContext = string.Empty;
        private static MajorUnlockTarget _scoreMajorUnlock;
        private static bool _scoreItopod;
        private static bool _probingBossLoadout;
        private static bool _cachedBossObjective;
        private static int _cachedBossId = int.MinValue;
        private static int _cachedHighestBoss = int.MinValue;
        private static double _cachedBossObjectiveAt = double.NegativeInfinity;
        private static string _leaseKind = string.Empty;
        private static string _leasedRoutineObjective = string.Empty;
        private static int _leasedBossId = -1;
        private static int _leasedHighestBoss = -1;
        private static MajorUnlockTarget _leasedMajorUnlock;
        private static double _leaseUntil;

        internal static string LastDecision { get; private set; } = "Waiting for an inventory snapshot";
        internal static double LastScoreGain { get; private set; }
        internal static string LastObjective { get; private set; } = "unresolved";
        internal static bool LastSearchExact { get; private set; }
        private static readonly MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod(
            "MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static bool IsAuthoritativeItem(Equipment item)
        {
            return item != null && (_authoritativePlan != null && UsesReference(_authoritativePlan, item)
                                    || _pendingPlan != null && UsesReference(_pendingPlan, item));
        }

        private sealed class Plan
        {
            internal Equipment Head;
            internal Equipment Chest;
            internal Equipment Legs;
            internal Equipment Boots;
            internal Equipment Weapon;
            internal Equipment Weapon2;
            internal readonly List<Equipment> Accessories = new List<Equipment>();

            internal Plan Clone()
            {
                var copy = new Plan
                {
                    Head = Head, Chest = Chest, Legs = Legs, Boots = Boots,
                    Weapon = Weapon, Weapon2 = Weapon2
                };
                copy.Accessories.AddRange(Accessories);
                return copy;
            }

            internal IEnumerable<Equipment> PrimaryItems()
            {
                if (Head != null) yield return Head;
                if (Chest != null) yield return Chest;
                if (Legs != null) yield return Legs;
                if (Boots != null) yield return Boots;
                if (Weapon != null) yield return Weapon;
                foreach (var accessory in Accessories) yield return accessory;
            }

            internal int[] IDs()
            {
                var result = PrimaryItems().Where(x => x != null && x.id > 0).Select(x => x.id).ToList();
                if (Weapon2 != null && Weapon2.id > 0)
                {
                    var weaponIndex = result.FindIndex(x => Weapon != null && x == Weapon.id);
                    result.Insert(weaponIndex < 0 ? result.Count : weaponIndex + 1, Weapon2.id);
                }
                return result.ToArray();
            }

        }

        internal static void Manage()
        {
            var c = Main.Character;
            var controller = Controller;
            if (c == null || controller == null || !Main.IsAutomationReady)
                return;
            if (c.challenges.inChallenge
                && c.challenges.curChallengeType.ToString().IndexOf("equip", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                LastDecision = "No Equipment Challenge is active; equipment bonuses are intentionally unavailable";
                return;
            }
            if (!LoadoutManager.CanSwap() || controller.midDrag || c.bossController.isFighting
                || c.bossController.nukeBoss)
            {
                LastDecision = "Holding the progression set until the boss/drag/loadout lock ends";
                return;
            }

            // Rebirth time moves backwards at every reset.  Cadence and retry
            // suppression must use a monotonic process clock instead.
            var now = (double)UnityEngine.Time.realtimeSinceStartup;
            var bossObjective = UseBossObjective(c);
            var itopodObjective = c.adventure.zone == 1000;
            // Evaluate major unlocks independently of the currently equipped set. Once an
            // admissible target exists, its lease must survive a combat-set swap which makes the
            // generic Fight Boss predicate momentarily true; this was the HSB/Wandoos feedback loop.
            var majorUnlock = itopodObjective ? null : MajorUnlockPlanner.Evaluate(c);
            ResolveObjectiveLease(c, now, ref bossObjective, ref itopodObjective, ref majorUnlock);
            _scoreItopod = itopodObjective;
            _scoreMajorUnlock = majorUnlock;
            var objective = Objective(c, bossObjective, _scoreMajorUnlock);
            if (_leaseKind == "routine" && now < _leaseUntil
                && !string.IsNullOrEmpty(_leasedRoutineObjective))
                objective = _leasedRoutineObjective;
            else if (string.IsNullOrEmpty(_leaseKind))
            {
                _leaseKind = "routine";
                _leasedRoutineObjective = objective;
                _leaseUntil = now + 15.0;
            }
            LastObjective = objective;

            // Full automation owns the progression loadout just as it owns resource
            // allocations. Reassert the last verified exact-reference plan before
            // the expensive search throttle: a manual equip swap changes topology,
            // not the inventory multiset, and must not survive for five seconds.
            string authoritativeReason;
            if (_authoritativePlan != null && _authoritativeObjective == objective
                && ValidatePlan(c, _authoritativePlan, out authoritativeReason))
            {
                var live = CurrentPlan(c, true);
                if (!SameLayout(live, _authoritativePlan))
                {
                    _pendingPlan = _authoritativePlan.Clone();
                    _pendingContext = ContextKey(c, objective);
                    if (c.adventureController.currentEnemy != null)
                    {
                        LastDecision = "Manual/foreign gear change detected; authoritative " + objective
                                       + " set queued for the next natural post-kill frame";
                        return;
                    }
                    ApplyChosenPlan(c, _pendingPlan, now, "Restored authoritative");
                    return;
                }
            }
            else if (_authoritativePlan != null)
            {
                _authoritativePlan = null;
                _pendingPlan = null;
                _pendingContext = string.Empty;
                _authoritativeObjective = string.Empty;
            }

            // This path deliberately runs before the inventory-probe cadence. The
            // enemy-free frame can be shorter than one second under continuous
            // Adventure automation.
            if (_pendingPlan != null && _pendingContext != ContextKey(c, objective))
            {
                _pendingPlan = null;
                _pendingContext = string.Empty;
                _lastFingerprint = int.MinValue;
                LastDecision = "Discarded a queued gear plan because its combat/resource context changed";
            }
            if (_pendingPlan != null && c.adventureController.currentEnemy == null)
            {
                ApplyChosenPlan(c, _pendingPlan, now, "Equipped queued");
                return;
            }

            // The fast allocation loop is 5 Hz, but an inventory topology search
            // gains nothing from rebuilding LINQ snapshots that often. Combat still
            // gates an already-computed plan on every fast tick.
            if (now - _lastInventoryProbe < 0.2)
                return;
            _lastInventoryProbe = now;
            var all = c.inventory.GetConvertedEquips().Concat(c.inventory.GetConvertedInventory())
                .Where(x => x != null && x.equipment != null && x.id > 0 && x.equipment.isEquipment())
                .Select(x => x.equipment).Distinct().ToList();
            var fingerprint = Fingerprint(all, c.inventoryController.accessorySpaces());
            if (fingerprint == _lastFingerprint && now - _lastRun < 5.0)
                return;
            _lastFingerprint = fingerprint;
            _lastRun = now;

            var current = CurrentPlan(c, true);
            var currentScore = Score(c, current);
            var best = Optimize(c, all);
            LastSearchExact = _searchExact;
            var bestScore = Score(c, best);
            LastScoreGain = bestScore - currentScore;
            var materialGain = Math.Max(0.05, Math.Abs(currentScore) * 0.005);
            if (SameLayout(best, current)
                || !(bestScore > currentScore + materialGain))
            {
                _authoritativePlan = current.Clone();
                _authoritativeObjective = objective;
                _pendingPlan = null;
                _pendingContext = string.Empty;
                LastDecision = (LastSearchExact ? "Globally optimal " : "Best verified bounded-search ")
                               + LastObjective + " set active: " + Describe(current);
                return;
            }
            if (_failedPlan != null && SameLayout(best, _failedPlan) && now < _failedUntil)
            {
                LastDecision = "Holding a rejected physical gear plan for backoff; retry in "
                               + Math.Ceiling(_failedUntil - now) + "s";
                return;
            }

            if (c.adventureController.currentEnemy != null)
            {
                _pendingPlan = best.Clone();
                _pendingContext = ContextKey(c, objective);
                LastDecision = "Verified equipment upgrade queued for the next natural post-kill frame";
                return;
            }

            ApplyChosenPlan(c, best, now, "Equipped");
        }

        /*
        STICKY OBJECTIVE LEASE

        Gear changes alter Fight Boss, Adventure, and resource predicates. Recomputing ownership
        from those post-swap predicates every second creates a controller feedback loop. A lease
        therefore owns one objective until its success state changes, its route becomes invalid,
        or a hard catch-up branch preempts it. Routine production leases are short; major unlock
        and selected-boss leases end on the exact unlock/boss transition. The lease chooses policy
        only and never bypasses native loadout/Titan/combat preflight.
        */
        private static void ResolveObjectiveLease(Character c, double now, ref bool bossObjective,
            ref bool itopodObjective, ref MajorUnlockTarget majorUnlock)
        {
            var highest = c.settings.rebirthDifficulty == difficulty.normal ? c.highestBoss
                : c.settings.rebirthDifficulty == difficulty.evil ? c.highestHardBoss
                : c.highestSadisticBoss;
            var catchupBoss = c.bossID < highest;

            if (_leaseKind == "boss" && c.bossID == _leasedBossId
                && highest == _leasedHighestBoss)
            {
                bossObjective = true;
                itopodObjective = false;
                majorUnlock = null;
                return;
            }
            if (_leaseKind == "major" && _leasedMajorUnlock != null
                && now < _leaseUntil && !MajorUnlockComplete(c, _leasedMajorUnlock))
            {
                bossObjective = false;
                itopodObjective = false;
                majorUnlock = _leasedMajorUnlock;
                return;
            }
            if (_leaseKind == "itopod" && c.adventure.zone == 1000)
            {
                bossObjective = false;
                itopodObjective = true;
                majorUnlock = null;
                return;
            }
            if (_leaseKind == "routine" && now < _leaseUntil && !catchupBoss
                && majorUnlock == null && !itopodObjective)
            {
                bossObjective = false;
                return;
            }

            ClearObjectiveLease();
            // Sequential catch-up is a finite exact target. For record pushes, a one-time mechanic
            // unlock dominates an interchangeable next boss and receives the lease first.
            if (catchupBoss || bossObjective && majorUnlock == null && !itopodObjective)
            {
                _leaseKind = "boss";
                _leasedBossId = c.bossID;
                _leasedHighestBoss = highest;
                bossObjective = true;
                itopodObjective = false;
                majorUnlock = null;
                return;
            }
            if (majorUnlock != null)
            {
                _leaseKind = "major";
                _leasedMajorUnlock = majorUnlock;
                _leaseUntil = now + 120.0;
                bossObjective = false;
                itopodObjective = false;
                return;
            }
            if (itopodObjective)
            {
                _leaseKind = "itopod";
                bossObjective = false;
                majorUnlock = null;
                return;
            }
            if (bossObjective)
            {
                _leaseKind = "boss";
                _leasedBossId = c.bossID;
                _leasedHighestBoss = highest;
                majorUnlock = null;
                return;
            }
        }

        private static bool MajorUnlockComplete(Character c, MajorUnlockTarget target)
        {
            if (target == null) return true;
            switch (target.Mechanic)
            {
                case "NGU": return c.settings.nguOn;
                case "Yggdrasil": return c.settings.yggdrasilOn;
                case "Gold Diggers": return c.settings.diggersOn;
                case "Beards": return c.settings.beardsOn;
                case "THE ITOPOD": return c.settings.itopodOn;
                case "Wandoos 98": return c.settings.wandoos98On;
                default:
                    return target.ItemId > 0 && c.inventory.itemList.itemDropped.Count > target.ItemId
                           && c.inventory.itemList.itemDropped[target.ItemId];
            }
        }

        private static void ClearObjectiveLease()
        {
            _leaseKind = string.Empty;
            _leasedRoutineObjective = string.Empty;
            _leasedBossId = -1;
            _leasedHighestBoss = -1;
            _leasedMajorUnlock = null;
            _leaseUntil = 0.0;
        }

        /*
        PRE-COMBAT ITOPOD STAGING

        The Adventure router can prove that an owned set reaches the next record floor while the
        currently equipped refill/production set does not. Entering first and waiting for a
        post-kill frame is circular: the weak set may never earn that frame. Stage and verify the
        exact ITOPOD plan while Adventure is enemy-free, then let the router re-check live reach.
        */
        internal static bool PrepareItopodRoute()
        {
            var c = Main.Character;
            if (c == null || Controller == null || !Main.IsAutomationReady
                || !LoadoutManager.CanSwap() || Controller.midDrag
                || c.bossController.isFighting || c.bossController.nukeBoss)
                return false;
            if (c.challenges.inChallenge
                && c.challenges.curChallengeType.ToString().IndexOf("equip", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (c.adventureController.currentEnemy != null)
            {
                LastDecision = "Waiting for the natural enemy-free frame before staging the ITOPOD route set";
                return false;
            }

            _scoreItopod = true;
            _scoreMajorUnlock = null;
            var objective = Objective(c, false, null);
            LastObjective = objective;
            var all = c.inventory.GetConvertedEquips().Concat(c.inventory.GetConvertedInventory())
                .Where(x => x != null && x.equipment != null && x.id > 0 && x.equipment.isEquipment())
                .Select(x => x.equipment).Distinct().ToList();
            var current = CurrentPlan(c, true);
            var best = Optimize(c, all);
            var currentScore = Score(c, current);
            var bestScore = Score(c, best);
            var now = (double)UnityEngine.Time.realtimeSinceStartup;
            if (!SameLayout(best, current)
                && bestScore > currentScore + Math.Max(1e-7, Math.Abs(currentScore) * 1e-7))
                ApplyChosenPlan(c, best, now, "Staged");
            else
            {
                _authoritativePlan = current.Clone();
                _authoritativeObjective = objective;
                _pendingPlan = null;
                _pendingContext = string.Empty;
                LastDecision = "Verified live ITOPOD route set before Adventure entry: " + Describe(current);
            }
            var route = ZoneHelpers.LastItopodRoute;
            var targetFloor = route.Climbing
                ? Math.Max(1, c.adventure.highestItopodLevel) : Math.Max(0, route.FarmFloor);
            return ZoneHelpers.CalculateBestItopodLevel() >= targetFloor;
        }

        private static void ApplyChosenPlan(Character c, Plan best, double now, string action)
        {
            var ids = best.IDs();
            var displaySignature = string.Join(",", ids.Select(x => x.ToString()).ToArray());
            var beforeAttack = c.inventoryController.attackBonus();
            var beforeDefense = c.inventoryController.defenseBonus();
            var confirmed = ApplyPhysicalPlan(c, best);
            if (confirmed)
            {
                _failedPlan = null;
                _failedUntil = 0;
                _authoritativePlan = best.Clone();
                _authoritativeObjective = LastObjective;
                _pendingPlan = null;
                _pendingContext = string.Empty;
                _lastFingerprint = int.MinValue;
                _lastRun = 0;
            }
            else
            {
                _failedPlan = best.Clone();
                _failedUntil = now + 30.0;
                _authoritativePlan = null;
                _authoritativeObjective = string.Empty;
                _pendingPlan = null;
                _pendingContext = string.Empty;
                _lastFingerprint = int.MinValue;
                _lastRun = 0;
            }
            LastDecision = (confirmed ? action : "Rejected") + " optimized " + LastObjective + " set [" + displaySignature
                           + "]; native item attack " + beforeAttack.ToString("0.##") + " -> "
                           + c.inventoryController.attackBonus().ToString("0.##") + ", defense "
                           + beforeDefense.ToString("0.##") + " -> "
                           + c.inventoryController.defenseBonus().ToString("0.##");
            Main.LogAction(confirmed ? "GEAR" : "REJECTED", LastDecision);
        }

        private static bool SameLayout(Plan a, Plan b)
        {
            if (a == null || b == null) return false;
            if (!ReferenceEquals(a.Head, b.Head) || !ReferenceEquals(a.Chest, b.Chest)
                || !ReferenceEquals(a.Legs, b.Legs) || !ReferenceEquals(a.Boots, b.Boots)
                || !ReferenceEquals(a.Weapon, b.Weapon) || !ReferenceEquals(a.Weapon2, b.Weapon2)
                || a.Accessories.Count != b.Accessories.Count)
                return false;
            for (var i = 0; i < a.Accessories.Count; i++)
                if (!ReferenceEquals(a.Accessories[i], b.Accessories[i])) return false;
            return true;
        }

        private static string ContextKey(Character c, string objective)
        {
            var route = ZoneHelpers.LastItopodRoute;
            return objective + "|boss=" + c.bossID + "/" + c.highestBoss
                   + "|zone=" + c.adventure.zone
                   + "|itopod=" + route.Start + ":" + route.End + ":" + route.FarmFloor;
        }

        private static Plan Optimize(Character c, List<Equipment> all)
        {
            _searchExact = true;
            var plans = new List<Plan> {new Plan()};
            plans = AddFixed(c, plans, all, part.Head, (p, e) => p.Head = e);
            plans = AddFixed(c, plans, all, part.Chest, (p, e) => p.Chest = e);
            plans = AddFixed(c, plans, all, part.Legs, (p, e) => p.Legs = e);
            plans = AddFixed(c, plans, all, part.Boots, (p, e) => p.Boots = e);

            var weapons = TrimCandidates(c, all.Where(x => x.type == part.Weapon), 18).ToList();
            var weaponPlans = new List<Plan>();
            if (weapons.Count == 0) weaponPlans.AddRange(plans);
            foreach (var p in plans)
            foreach (var primary in weapons)
            {
                if (!c.inventoryController.weapon2Unlocked())
                {
                    var copy = p.Clone();
                    copy.Weapon = primary;
                    weaponPlans.Add(copy);
                    continue;
                }
                var secondaries = weapons.Where(x => !ReferenceEquals(x, primary) && x.id != primary.id).ToList();
                // An unlocked second weapon slot is still a real physical slot when
                // fewer than two distinct weapons are owned. Include its empty
                // object so the plan remains complete and verifiable.
                if (c.inventory.weapon2 != null && c.inventory.weapon2.id <= 0)
                    secondaries.Add(c.inventory.weapon2);
                else
                {
                    var emptyWeaponSlot = c.inventory.inventory.FirstOrDefault(x => x != null && x.id <= 0);
                    if (emptyWeaponSlot != null) secondaries.Add(emptyWeaponSlot);
                }
                foreach (var secondary in secondaries)
                {
                    var copy = p.Clone();
                    copy.Weapon = primary;
                    copy.Weapon2 = secondary;
                    weaponPlans.Add(copy);
                }
            }
            plans = KeepBest(c, weaponPlans);

            var spaces = Math.Min(Math.Max(0, c.inventoryController.accessorySpaces()), c.inventory.accs.Count);
            var accessories = TrimCandidates(c, all.Where(x => x.type == part.Accessory), 32).ToList();
            accessories.AddRange(c.inventory.accs.Concat(c.inventory.inventory)
                .Where(x => x != null && x.id <= 0).Take(spaces));
            for (var slot = 0; slot < spaces && accessories.Count > 0; slot++)
            {
                var next = new List<Plan>();
                foreach (var p in plans)
                foreach (var accessory in accessories)
                {
                    if (UsesReference(p, accessory)
                        || p.Accessories.Any(x => accessory.id > 0 && x.id == accessory.id)) continue;
                    var copy = p.Clone();
                    copy.Accessories.Add(accessory);
                    next.Add(copy);
                }
                if (next.Count == 0) break;
                plans = KeepBest(c, next);
            }
            // A partial plan is not a complete-set optimization: its unrepresented
            // live slots would contribute stats that were never scored.
            var complete = plans.Where(x => x.Accessories.Count == spaces).ToList();
            return complete.OrderByDescending(x => Score(c, x)).FirstOrDefault() ?? CurrentPlan(c, true);
        }

        private static bool UsesReference(Plan p, Equipment item)
        {
            return ReferenceEquals(p.Head, item) || ReferenceEquals(p.Chest, item)
                   || ReferenceEquals(p.Legs, item) || ReferenceEquals(p.Boots, item)
                   || ReferenceEquals(p.Weapon, item) || ReferenceEquals(p.Weapon2, item)
                   || p.Accessories.Any(x => ReferenceEquals(x, item));
        }

        private static List<Plan> AddFixed(Character c, List<Plan> plans, List<Equipment> all,
            part type, Action<Plan, Equipment> assign)
        {
            var candidates = TrimCandidates(c, all.Where(x => x.type == type), 14).ToList();
            if (candidates.Count == 0) return plans;
            var next = new List<Plan>();
            foreach (var p in plans)
            foreach (var candidate in candidates)
            {
                var copy = p.Clone();
                assign(copy, candidate);
                next.Add(copy);
            }
            return KeepBest(c, next);
        }

        private static IEnumerable<Equipment> TrimCandidates(Character c, IEnumerable<Equipment> source, int count)
        {
            var groups = source.GroupBy(x => x.id).ToList();
            if (groups.Any(g => g.Count() > 2)) _searchExact = false;
            var representatives = groups.SelectMany(g =>
            {
                var rankedGroup = g.OrderByDescending(x => TrimUtility(c, x)).Take(2);
                if (!_scoreItopod) return rankedGroup;
                // Same-ID copies cannot be equipped together, but the maximum-Attack
                // or maximum-Respawn physical copy can be the unique route witness.
                return rankedGroup.Concat(new[]
                    {
                        g.OrderByDescending(c.inventoryController.equipAttackBonus).First(),
                        g.OrderByDescending(x => c.inventoryController.equipSpecBonus(specType.Respawn, x)).First()
                    }).Distinct();
            }).Distinct().ToList();
            var ranked = representatives
                .OrderByDescending(x => TrimUtility(c, x)).ToList();
            if (ranked.Count > count) _searchExact = false;
            if (!_scoreItopod) return ranked.Take(count);

            // Keep the exact per-slot attack witnesses used by owned-floor admission,
            // plus the defense and Respawn Pareto extremes that define survival and
            // cycle time. Fill the remaining bounded beam by joint utility.
            var sample = ranked.FirstOrDefault();
            var needed = sample != null && sample.type == part.Accessory
                ? Math.Max(1, c.inventoryController.accessorySpaces())
                : sample != null && sample.type == part.Weapon && c.inventoryController.weapon2Unlocked() ? 2 : 1;
            var mandatory = representatives.OrderByDescending(c.inventoryController.equipAttackBonus).Take(needed)
                .Concat(representatives.OrderByDescending(c.inventoryController.equipDefenseBonus).Take(needed))
                .Concat(representatives.OrderByDescending(x =>
                    c.inventoryController.equipSpecBonus(specType.Respawn, x)).Take(needed))
                .Distinct().ToList();
            return mandatory.Concat(ranked).Distinct().Take(Math.Max(count, mandatory.Count));
        }

        private static double TrimUtility(Character c, Equipment e)
        {
            var attack = c.inventoryController.equipAttackBonus(e);
            var defense = c.inventoryController.equipDefenseBonus(e);
            if (_scoreMajorUnlock != null || _probingBossLoadout || _scoreItopod)
            {
                // A combat target must never be removed from the bounded search
                // because an Energy/Gold item had a larger generic utility score.
                var respawn = Math.Max(0.0, c.inventoryController.equipSpecBonus(specType.Respawn, e));
                return 8.0 * Math.Log(1.0 + Math.Max(0, attack))
                       + 8.0 * Math.Log(1.0 + Math.Max(0, defense))
                       + (_scoreItopod ? 20.0 * respawn : 0.0);
            }
            return ItemUtility(c, e);
        }

        private static double ItemUtility(Character c, Equipment e)
        {
            var attack = c.inventoryController.equipAttackBonus(e);
            var defense = c.inventoryController.equipDefenseBonus(e);
            return 4.0 * Math.Log(1.0 + Math.Max(0, attack))
                   + 3.0 * Math.Log(1.0 + Math.Max(0, defense)) + SpecialUtility(c, e, 1.0)
                   + ProductionTrimUtility(c, e);
        }

        /*
        GEAR-DEVELOPMENT VALUE

        Loadout selection must use the item's stats right now, otherwise it would equip a nominally
        higher-tier piece before that piece is actually useful. Boost routing needs the complementary
        question: can a completed boost bar make this exact physical item win its slot? A shallow
        calculation copy lets the native bossRequired/effectiveBossID contribution methods answer that
        without mutating the save. The real object remains the only object ever passed to inventory
        controller methods. Collection level is deliberately not projected here: an incomplete item may
        win at its real current level, but speculative future duplicate drops cannot justify boosts.
        */
        internal static double CurrentItemUtility(Character c, Equipment e)
        {
            return c == null || e == null || e.id <= 0 ? 0.0 : ItemUtility(c, e);
        }

        internal static double FullyBoostedItemUtility(Character c, Equipment e)
        {
            if (c == null || e == null || e.id <= 0 || MemberwiseCloneMethod == null)
                return CurrentItemUtility(c, e);
            try
            {
                var projected = (Equipment)MemberwiseCloneMethod.Invoke(e, null);
                projected.curAttack = BoostCap(projected.capAttack, projected.level);
                projected.curDefense = BoostCap(projected.capDefense, projected.level);
                projected.spec1Cur = BoostCap(projected.spec1Cap, projected.level);
                projected.spec2Cur = BoostCap(projected.spec2Cap, projected.level);
                projected.spec3Cur = BoostCap(projected.spec3Cap, projected.level);
                return ItemUtility(c, projected);
            }
            catch
            {
                // Failing closed here means the Cube/legacy locked-item tier retains
                // the boost. It must never justify a mutation from an unproven model.
                return CurrentItemUtility(c, e);
            }
        }

        internal static double FullyBoostedLoadoutGain(Character c, Equipment e)
        {
            return AvailableBoostedLoadoutGain(c, e, true, true, true);
        }

        internal static double AvailableBoostedLoadoutGain(Character c, Equipment e,
            bool powerAvailable, bool toughnessAvailable, bool specialAvailable)
        {
            if (c == null || e == null || e.id <= 0 || MemberwiseCloneMethod == null)
                return 0.0;
            try
            {
                var projected = (Equipment)MemberwiseCloneMethod.Invoke(e, null);
                if (powerAvailable)
                    projected.curAttack = BoostCap(projected.capAttack, projected.level);
                if (toughnessAvailable)
                    projected.curDefense = BoostCap(projected.capDefense, projected.level);
                if (specialAvailable)
                {
                    projected.spec1Cur = BoostCap(projected.spec1Cap, projected.level);
                    projected.spec2Cur = BoostCap(projected.spec2Cap, projected.level);
                    projected.spec3Cur = BoostCap(projected.spec3Cap, projected.level);
                }
                var current = CurrentPlan(c, true);
                var baseline = Score(c, current);
                var best = baseline;
                if (e.type == part.Head) { var p = current.Clone(); p.Head = projected; best = Math.Max(best, Score(c, p)); }
                else if (e.type == part.Chest) { var p = current.Clone(); p.Chest = projected; best = Math.Max(best, Score(c, p)); }
                else if (e.type == part.Legs) { var p = current.Clone(); p.Legs = projected; best = Math.Max(best, Score(c, p)); }
                else if (e.type == part.Boots) { var p = current.Clone(); p.Boots = projected; best = Math.Max(best, Score(c, p)); }
                else if (e.type == part.Weapon)
                {
                    if (!PlanContainsIdOutside(current, e.id, current.Weapon))
                    {
                        var p = current.Clone(); p.Weapon = projected; best = Math.Max(best, Score(c, p));
                    }
                    if (c.inventoryController.weapon2Unlocked()
                        && !PlanContainsIdOutside(current, e.id, current.Weapon2))
                    {
                        var p = current.Clone(); p.Weapon2 = projected; best = Math.Max(best, Score(c, p));
                    }
                }
                else if (e.type == part.Accessory)
                {
                    for (var i = 0; i < current.Accessories.Count; i++)
                    {
                        if (PlanContainsIdOutside(current, e.id, current.Accessories[i])) continue;
                        var p = current.Clone();
                        p.Accessories[i] = projected;
                        best = Math.Max(best, Score(c, p));
                    }
                }
                return Math.Max(0.0, best - baseline);
            }
            catch
            {
                return 0.0;
            }
        }

        /*
        USEFUL BOOST DEBT

        Adventure boss-only routing is allowed to skip normal enemies, but normal enemies are the
        renewable source of Power/Toughness/Special boost drops in ordinary zones. Expose the same
        complete-loadout proof used by InventoryManager so the Adventure planner can refuse a route
        which would strand a genuinely useful MAXX/higher-tier item below its level-scaled boost cap.
        A level-100 item's native cap is twice its base cap; FullyBoostedLoadoutGain already evaluates
        that exact current-level cap through BoostCap. Speculative future merge levels are excluded.
        */
        internal static bool TryGetUsefulBoostDebt(Character c, out double needed, out double gain,
            out string itemName)
        {
            needed = 0.0;
            gain = 0.0;
            itemName = string.Empty;
            if (c == null || c.inventory == null)
                return false;
            try
            {
                var candidates = c.inventory.GetConvertedEquips().Concat(c.inventory.GetConvertedInventory())
                    .Where(x => x != null && x.equipment != null && x.id > 0
                                && x.equipment.isEquipment()
                                && (Main.Settings == null || !Main.Settings.BoostBlacklist.Contains(x.id)))
                    .Select(x => new
                    {
                        Item = x.equipment,
                        Name = x.name,
                        Needed = (double)x.equipment.GetNeededBoosts().Total(),
                        Gain = FullyBoostedLoadoutGain(c, x.equipment)
                    })
                    .Where(x => x.Needed > 0.0 && x.Gain > 1e-7)
                    .OrderByDescending(x => x.Gain / x.Needed)
                    .ThenByDescending(x => x.Item.bossRequired)
                    .ThenBy(x => x.Item.id).ToArray();
                if (candidates.Length == 0)
                    return false;
                var best = candidates[0];
                needed = best.Needed;
                gain = best.Gain;
                itemName = string.IsNullOrEmpty(best.Name) ? "item " + best.Item.id
                    : best.Name.Replace("\n", " ").Trim();
                return true;
            }
            catch
            {
                // This is a routing proof, not permission to destroy or mutate an item. On an
                // incomplete snapshot, keep the previous conservative boss-only behavior.
                return false;
            }
        }

        private static bool PlanContainsIdOutside(Plan plan, int id, Equipment replaced)
        {
            if (id <= 0) return false;
            return plan.PrimaryItems().Concat(plan.Weapon2 == null
                    ? Enumerable.Empty<Equipment>() : new[] {plan.Weapon2})
                .Any(x => x != null && !ReferenceEquals(x, replaced) && x.id == id);
        }

        private static float BoostCap(float baseCap, float level)
        {
            return baseCap <= 0f ? 0f : UnityEngine.Mathf.Floor(baseCap * (1f + level / 100f));
        }

        private static List<Plan> KeepBest(Character c, IEnumerable<Plan> source)
        {
            var ranked = source.OrderByDescending(x => Score(c, x)).ToList();
            if (ranked.Count > BeamWidth) _searchExact = false;
            return ranked.Take(BeamWidth).ToList();
        }

        private static double Score(Character c, Plan plan)
        {
            var controller = c.inventoryController;
            var scoringItems = plan.PrimaryItems().Where(x => x != null && x.id > 0).ToList();
            var attackItems = scoringItems.Sum(x => (double)controller.equipAttackBonus(x));
            var defenseItems = scoringItems.Sum(x => (double)controller.equipDefenseBonus(x));
            var weapon2Factor = plan.Weapon2 == null || plan.Weapon2.id <= 0 ? 0.0 : controller.weapon2Factor();
            if (plan.Weapon2 != null && plan.Weapon2.id > 0)
            {
                attackItems += controller.equipAttackBonus(plan.Weapon2) * weapon2Factor;
                defenseItems += controller.equipDefenseBonus(plan.Weapon2) * weapon2Factor;
            }

            var currentAttackItems = Math.Max(0.0, controller.attackBonus());
            var currentDefenseItems = Math.Max(0.0, controller.defenseBonus());
            var attackBase = Math.Max(0.0, c.training.getTotalAttack());
            var defenseBase = Math.Max(0.0, c.training.getTotalDefense());
            var attackCommon = c.attackMulti * c.adventureController.itopod.totalStatBonus() * c.attackBoost;
            var defenseCommon = c.defenseMulti * c.adventureController.itopod.totalStatBonus() * c.defenseBoost;
            var currentAttackCore = 100.0 + attackBase * attackCommon * (1.0 + currentAttackItems / 100.0);
            var currentDefenseCore = 100.0 + defenseBase * defenseCommon * (1.0 + currentDefenseItems / 100.0);
            var candidateAttackCore = 100.0 + attackBase * attackCommon * (1.0 + attackItems / 100.0);
            var candidateDefenseCore = 100.0 + defenseBase * defenseCommon * (1.0 + defenseItems / 100.0);
            var projectedAttack = c.attack * candidateAttackCore / Math.Max(1e-9, currentAttackCore);
            var projectedDefense = c.defense * candidateDefenseCore / Math.Max(1e-9, currentDefenseCore);
            var bossObjective = UseBossObjective(c);
            var majorUnlock = bossObjective ? null : _scoreMajorUnlock;
            var score = bossObjective
                ? 7.0 * Math.Log(1.0 + Math.Max(0.0, projectedAttack))
                  + 4.0 * Math.Log(1.0 + Math.Max(0.0, projectedDefense))
                : 0.0;

            if (bossObjective && c.bossMaxHP > 0)
            {
                double kill;
                double survival;
                var viable = CombatHelpers.EvaluateFixedBossFight(c, projectedAttack, projectedDefense,
                    10.0 + projectedAttack * 10.0, c.bossMaxHP, out kill, out survival);
                if (!double.IsInfinity(kill))
                {
                    score += 1000000.0 + 1000000.0 / (1.0 + kill);
                    if (viable) score += 10000000.0;
                }
                else
                {
                    score -= 900000.0;
                }
            }

            // Native Adventure totals are affine in the item contribution before
            // every later multiplier.  Ratio against the current numerator gives
            // the exact candidate totals without mutating equipment.
            var advAttackNumerator = Math.Max(1e-9, c.adventure.attack
                + c.inventoryController.cubePower() + currentAttackItems);
            var advDefenseNumerator = Math.Max(1e-9, c.adventure.defense
                + c.inventoryController.cubeToughness() + currentDefenseItems);
            var candidateAdvAttack = c.totalAdvAttack()
                                     * (c.adventure.attack + c.inventoryController.cubePower() + attackItems)
                                     / advAttackNumerator;
            var candidateAdvDefense = c.totalAdvDefense()
                                      * (c.adventure.defense + c.inventoryController.cubeToughness() + defenseItems)
                                      / advDefenseNumerator;
            // Native adventureHPBonus is attackBonus*3 and totalAdvHP adds
            // cubePower*3. Toughness/cubeToughness do not contribute HP.
            var advHpNumerator = Math.Max(1e-9, c.adventure.maxHP
                + 3.0 * (c.inventoryController.cubePower() + currentAttackItems));
            var candidateAdvHP = c.totalAdvHP()
                                 * (c.adventure.maxHP
                                    + 3.0 * (c.inventoryController.cubePower() + attackItems))
                                 / advHpNumerator;
            if (!bossObjective && _scoreItopod)
            {
                // Staging occurs before CombatManager toggles the configured ITOPOD
                // Beast state. Normalize the attack projection out of the live state
                // and into the target state before evaluating hit plateaus.
                candidateAdvAttack *= ZoneHelpers.ItopodTargetAttackFactor();
                score += ItopodThroughputUtility(c, plan, candidateAdvAttack,
                    candidateAdvDefense, candidateAdvHP);
            }
            else if (!bossObjective)
            {
                score += 6.0 * Math.Log(1.0 + Math.Max(0.0, candidateAdvAttack));
                score += 5.0 * Math.Log(1.0 + Math.Max(0.0, candidateAdvDefense));

                if (majorUnlock != null)
                {
                    score += MajorUnlockUtility(c, plan, majorUnlock,
                        candidateAdvAttack, candidateAdvDefense, candidateAdvHP);
                }
                else
                {
                    /*
                    NEXT-ZONE THRESHOLD OBJECTIVE

                    Raw log stats are smooth, but Adventure progression is not: a set that crosses both
                    manual Power/Toughness requirements unlocks a new loot table immediately. Make that
                    discontinuity primary, then maximize the weaker normalized requirement while below it.
                    This does not equip unfinished Cave armor merely because it is newer; it equips it as
                    soon as its real boosted contribution improves the limiting route to the next zone.
                    */
                    var front = ZoneStatHelper.GetBestZone();
                    ZoneStats frontStats;
                    if (front != null && ZoneStatHelper.UserOverrides.TryGetValue(front.Zone, out frontStats)
                        && (candidateAdvAttack < frontStats.MPower || candidateAdvDefense < frontStats.MToughness))
                    {
                        // A smoother ratio toward the following zone cannot justify
                        // giving up the strongest route that is already stat-safe.
                        score -= 200000000.0;
                    }
                    int nextZone;
                    ZoneStats nextStats;
                    if (ZoneStatHelper.TryGetNextUnlockedZone(front == null ? -1 : front.Zone,
                        out nextZone, out nextStats))
                    {
                        var bottleneck = Math.Min(candidateAdvAttack / Math.Max(1.0, nextStats.MPower),
                            candidateAdvDefense / Math.Max(1.0, nextStats.MToughness));
                        score += 10000.0 * Math.Min(1.0, Math.Max(0.0, bottleneck));
                        if (candidateAdvAttack >= nextStats.MPower && candidateAdvDefense >= nextStats.MToughness)
                            score += 100000000.0;
                    }
                }
            }
            else
            {
                // Adventure remains the secondary objective among equally viable
                // boss sets so the post-fight gear is not needlessly brittle.
                score += 0.3 * Math.Log(1.0 + Math.Max(0.0, candidateAdvAttack));
                score += 0.3 * Math.Log(1.0 + Math.Max(0.0, candidateAdvDefense));
            }
            // During hard Adventure combat, Energy/Gold/general specials are not
            // combat stats and may not displace an item that shortens the target
            // fight or makes it survivable. RNG-gated unlock loot is valued inside
            // MajorUnlockUtility only after the combat constraint is satisfied.
            if (majorUnlock == null && !_scoreItopod)
            {
                score += ProductionRateUtility(c, plan);
                foreach (var item in scoringItems) score += SpecialUtility(c, item, 1.0);
                if (plan.Weapon2 != null && plan.Weapon2.id > 0)
                    score += SpecialUtility(c, plan.Weapon2, weapon2Factor);
            }
            return score;
        }

        private static bool UseBossObjective(Character c)
        {
            var highest = c.settings.rebirthDifficulty == difficulty.normal ? c.highestBoss
                : c.settings.rebirthDifficulty == difficulty.evil ? c.highestHardBoss
                : c.highestSadisticBoss;
            if (c.bossID < highest) return true; // native sequential catch-up
            if (_probingBossLoadout) return true;
            double killSeconds;
            if (CombatHelpers.CanNukeCurrentBoss(c)
                || CombatHelpers.CanWinCurrentBoss(c, out killSeconds) && killSeconds <= 120.0)
                return true;

            // Searching only after the live set can win is circular: a production
            // accessory can hide a two-minute boss set forever. Probe the complete
            // owned topology under the boss objective, then accept that objective
            // only when the best bounded-search plan itself passes the exact fight
            // model. Cache briefly because Score calls this method thousands of
            // times during a beam search.
            var now = (double)UnityEngine.Time.realtimeSinceStartup;
            if (_cachedBossId == c.bossID && _cachedHighestBoss == highest
                && now - _cachedBossObjectiveAt < 1.0)
                return _cachedBossObjective;

            var previousMajor = _scoreMajorUnlock;
            var previousItopod = _scoreItopod;
            try
            {
                _probingBossLoadout = true;
                _scoreMajorUnlock = null;
                _scoreItopod = false;
                var all = c.inventory.GetConvertedEquips().Concat(c.inventory.GetConvertedInventory())
                    .Where(x => x != null && x.equipment != null && x.id > 0
                                && x.equipment.isEquipment())
                    .Select(x => x.equipment).Distinct().ToList();
                var best = Optimize(c, all);
                _cachedBossObjective = PlanCanBeatSelectedBoss(c, best, out killSeconds)
                                       && killSeconds <= 120.0;
            }
            catch
            {
                _cachedBossObjective = false;
            }
            finally
            {
                _probingBossLoadout = false;
                _scoreMajorUnlock = previousMajor;
                _scoreItopod = previousItopod;
                _cachedBossId = c.bossID;
                _cachedHighestBoss = highest;
                _cachedBossObjectiveAt = now;
            }
            return _cachedBossObjective;
        }

        private static bool PlanCanBeatSelectedBoss(Character c, Plan plan, out double killSeconds)
        {
            killSeconds = double.PositiveInfinity;
            if (plan == null || c.bossMaxHP <= 0) return false;
            var controller = c.inventoryController;
            var scoringItems = plan.PrimaryItems().Where(x => x != null && x.id > 0).ToList();
            var attackItems = scoringItems.Sum(x => (double)controller.equipAttackBonus(x));
            var defenseItems = scoringItems.Sum(x => (double)controller.equipDefenseBonus(x));
            var weapon2Factor = plan.Weapon2 == null || plan.Weapon2.id <= 0 ? 0.0 : controller.weapon2Factor();
            if (plan.Weapon2 != null && plan.Weapon2.id > 0)
            {
                attackItems += controller.equipAttackBonus(plan.Weapon2) * weapon2Factor;
                defenseItems += controller.equipDefenseBonus(plan.Weapon2) * weapon2Factor;
            }
            var currentAttackItems = Math.Max(0.0, controller.attackBonus());
            var currentDefenseItems = Math.Max(0.0, controller.defenseBonus());
            var attackBase = Math.Max(0.0, c.training.getTotalAttack());
            var defenseBase = Math.Max(0.0, c.training.getTotalDefense());
            var attackCommon = c.attackMulti * c.adventureController.itopod.totalStatBonus() * c.attackBoost;
            var defenseCommon = c.defenseMulti * c.adventureController.itopod.totalStatBonus() * c.defenseBoost;
            var currentAttackCore = 100.0 + attackBase * attackCommon * (1.0 + currentAttackItems / 100.0);
            var currentDefenseCore = 100.0 + defenseBase * defenseCommon * (1.0 + currentDefenseItems / 100.0);
            var projectedAttack = c.attack
                                  * (100.0 + attackBase * attackCommon * (1.0 + attackItems / 100.0))
                                  / Math.Max(1e-9, currentAttackCore);
            var projectedDefense = c.defense
                                   * (100.0 + defenseBase * defenseCommon * (1.0 + defenseItems / 100.0))
                                   / Math.Max(1e-9, currentDefenseCore);
            double survivalSeconds;
            return CombatHelpers.EvaluateFixedBossFight(c, projectedAttack, projectedDefense,
                10.0 + projectedAttack * 10.0, c.bossMaxHP, out killSeconds, out survivalSeconds);
        }

        private static string Objective(Character c, bool bossObjective, MajorUnlockTarget major)
        {
            if (bossObjective) return "selected Fight Boss defeat";
            if (_scoreItopod)
            {
                var route = ZoneHelpers.LastItopodRoute;
                return route.Climbing
                    ? "ITOPOD first-clear climb to floor " + route.End
                    : "ITOPOD PP/AP/EXP throughput at floor " + route.FarmFloor;
            }
            if (major != null) return "major unlock: " + major.Mechanic + " via " + major.Goal;
            var energyFill = c.energyPerSecond() <= 0 ? 0.0
                : Math.Max(0.0, c.totalCapEnergy() - c.curEnergy) / c.energyPerSecond();
            var magicFill = c.magicPerSecond() <= 0 ? 0.0
                : Math.Max(0.0, c.totalCapMagic() - c.magic.curMagic) / c.magicPerSecond();
            if (Math.Max(energyFill, magicFill) >= 30.0)
                return "resource refill: minimize time to full Energy and Magic";
            if (AllocationProfiles.BreakpointTypes.BR.LastDecision.StartsWith("Waiting for another ",
                    StringComparison.OrdinalIgnoreCase))
                return "Gold working capital for the next Blood ritual";
            var front = ZoneStatHelper.GetBestZone();
            int nextZone;
            ZoneStats nextStats;
            return ZoneStatHelper.TryGetNextUnlockedZone(front == null ? -1 : front.Zone,
                out nextZone, out nextStats)
                ? "Adventure progression toward " + nextStats.Name
                : "continuous Adventure progression";
        }

        /*
        GOAL-SPECIFIC LOADOUT VALUE

        A major unlock is not scored like routine highest-zone farming. Evaluate the target zone's
        actual boss attack, defense, HP, attack cadence, active-attack power, and candidate maximum
        HP. This replaces the coarse Toughness threshold as the loadout objective. An RNG-gated
        mechanic additionally values the native aggregate loot multiplier only after combat works.
        Guaranteed drops (the first Pissed Off Key) deliberately assign no Drop Chance value.
        */
        private static double MajorUnlockUtility(Character c, Plan plan, MajorUnlockTarget target,
            double candidateAdvAttack, double candidateAdvDefense, double candidateAdvHP)
        {
            var score = TargetCombatUtility(c, target, candidateAdvAttack,
                candidateAdvDefense, candidateAdvHP);
            if (!target.ValuesLoot) return score;

            var controller = c.inventoryController;
            var candidateLoot = PlanBonus(controller, plan, specType.Looting)
                                + PlanBonus(controller, plan, specType.Looting2);
            var currentLoot = controller.bonuses[specType.Looting]
                              + controller.bonuses[specType.Looting2];
            var cubeLoot = controller.cubeLootBonus();
            var ratio = (1.0 + candidateLoot + cubeLoot)
                        / Math.Max(1e-9, 1.0 + currentLoot + cubeLoot);
            // Expected attempts to the unlock are inversely proportional to loot
            // factor. Large scaling is safe only after the combat-floor constraint.
            return score + 20000.0 * Math.Log(Math.Max(1e-9, ratio));
        }

        private static double TargetCombatUtility(Character c, MajorUnlockTarget target,
            double attack, double defense, double maxHP)
        {
            if (target.Zone < 0 || c.adventureController.enemyList == null
                || target.Zone >= c.adventureController.enemyList.Count
                || c.adventureController.enemyList[target.Zone] == null)
                return -500000000.0;
            var all = c.adventureController.enemyList[target.Zone];
            var enemies = all.Where(x => x.enemyType == enemyType.boss
                || x.enemyType.ToString().IndexOf("bigBoss", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (enemies.Count == 0) enemies = all.ToList();
            var worst = double.PositiveInfinity;
            foreach (var enemy in enemies)
            {
                var outgoing = .8 * Math.Max(0.0, attack - enemy.defense / 2.0)
                               * c.regAttackPower();
                if (outgoing <= 0)
                {
                    worst = Math.Min(worst, -500000000.0);
                    continue;
                }
                var killSeconds = Math.Ceiling(enemy.maxHP / outgoing);
                var firstAttack = 1.5 * enemy.attackRate;
                var enemyAttacks = killSeconds < firstAttack ? 0
                    : 1 + (int)Math.Floor((killSeconds - firstAttack) / Math.Max(.1, enemy.attackRate));
                var incoming = 1.2 * Math.Max(enemy.attack * .1, enemy.attack - defense / 2.0);
                var projectedDamage = enemyAttacks * incoming;
                var survivalMargin = maxHP / Math.Max(1.0, projectedDamage);
                var viable = killSeconds <= 120.0 && projectedDamage < maxHP * .95;
                var value = viable ? 100000000.0 : -200000000.0;
                value += 1000000.0 / (1.0 + killSeconds);
                value += 20000.0 * Math.Log(Math.Max(1e-9, survivalMargin));
                worst = Math.Min(worst, value);
            }
            return double.IsPositiveInfinity(worst) ? -500000000.0 : worst;
        }

        /*
        ITOPOD CYCLE OBJECTIVE

        ITOPOD Drop Chance is fixed, so ordinary Looting gear has zero value there.  Equipment can
        improve rewards only by crossing a hit-count/survival plateau or shortening native respawn.
        Evaluate those quantities jointly at the configured farm/climb floor.  A one-shot candidate
        receives no Toughness value; a multi-hit candidate must survive the expected enemy attacks.
        */
        private static double ItopodThroughputUtility(Character c, Plan plan, double attack,
            double defense, double maxHP)
        {
            var route = ZoneHelpers.LastItopodRoute;
            var floor = Math.Max(0, Math.Min(1600, route.Climbing ? route.End - 1 : route.FarmFloor));
            var scale = Math.Pow(1.05, floor);
            // Native spawn independently rolls HP/Defense in [0.98,1.02] and the
            // player hit in [0.8,1.2]. Optimize the guaranteed one-hit plateau so
            // poison/charger/paralyze/rapid/grower AI never receives an action.
            var enemyHP = 600.0 * scale * 1.02;
            var enemyDefense = 10.0 * scale * 1.02;
            var manualClimb = route.Climbing && Main.Settings.ITOPODCombatMode != 1
                              && c.training.attackTraining[1] != 0;
            var attackPower = manualClimb ? c.regAttackPower() : c.idleAttackPower();
            var damage = 0.8 * Math.Max(0.0, attack - enemyDefense / 2.0) * attackPower;
            if (damage <= 0.0) return -1000000000.0;
            var hits = Math.Max(1.0, Math.Ceiling(enemyHP / damage));
            var attackInterval = Math.Max(0.02, c.adventure.attackSpeed);
            var killSeconds = hits * attackInterval;
            if (hits > 1.0)
                return -500000000.0 - 1000000.0 * hits;

            var controller = c.inventoryController;
            var currentRespawnGear = Math.Max(0.0, controller.bonuses[specType.Respawn]);
            var candidateRespawnGear = Math.Max(0.0, PlanBonus(controller, plan, specType.Respawn));
            var currentRespawnFactor = Math.Max(0.2, 1.0 - currentRespawnGear);
            var candidateRespawnFactor = Math.Max(0.2, 1.0 - candidateRespawnGear);
            var respawn = c.adventureController.respawnTime()
                          * candidateRespawnFactor / Math.Max(1e-9, currentRespawnFactor);
            var cycle = Math.Max(0.02, killSeconds + Math.Max(0.0, respawn));
            var progress = (c.settings.rebirthDifficulty == difficulty.normal ? 200.0
                : c.settings.rebirthDifficulty == difficulty.evil ? 700.0 : 2000.0) + floor;
            // First-clear climbing is a discrete permanent award, so reaching the requested floor
            // dominates small farm-rate differences.  Farming maximizes exact cycle throughput.
            // First-clear routing is justified by a one-shot proof. Preserve that
            // constraint lexicographically; respawn bonuses may optimize only among
            // candidate plans that retain it.
            var viableClimb = !route.Climbing || damage >= enemyHP;
            if (!viableClimb)
                return -1000000000.0 + 1000.0 / hits;
            return (route.Climbing ? 200000000.0 : 0.0)
                   + 10000000.0 * progress / cycle
                   - 1000.0 * hits;
        }

        private static double SpecialUtility(Character c, Equipment e, double slotFactor)
        {
            return new[] {e.spec1Type, e.spec2Type, e.spec3Type}.Distinct()
                .Sum(type => Special(c, e, type, slotFactor));
        }

        private static double Special(Character c, Equipment e, specType type, double slotFactor)
        {
            if (type == specType.None) return 0.0;
            var amount = Math.Max(0.0, c.inventoryController.equipSpecBonus(type, e)) * slotFactor;
            var weight = 0.35;
            switch (type)
            {
                // These bonuses are evaluated once across the complete loadout by
                // ProductionRateUtility. Per-item logs would value Energy Speed
                // beyond its native 50 cap and would miss discrete generation ticks.
                case specType.EnergySpeed:
                case specType.MagicSpeed:
                case specType.Looting:
                case specType.Looting2:
                    return 0.0;
                case specType.EnergyPower:
                case specType.EnergyPower2:
                case specType.EnergyPower3:
                case specType.EnergyPerBar:
                case specType.EnergyPerBar2:
                case specType.EnergyPerBar3:
                case specType.EnergyCap:
                case specType.EnergyCap3:
                case specType.AllPower:
                case specType.AllPerBar:
                case specType.AllCap:
                    weight = 1.4;
                    break;
                case specType.Respawn:
                case specType.Augs:
                case specType.AdvTraining:
                case specType.AdvTraining2:
                    weight = 1.0;
                    break;
                case specType.EXP:
                case specType.AP:
                    weight = 1.2;
                    break;
            }
            return weight * Math.Log(1.0 + amount);
        }

        /*
        COMPLETE-LOADOUT PRODUCTION VALUE

        Special percentages are not interchangeable raw stats. Energy and Magic generation use a
        discrete 50 Hz bar formula and Energy Speed hard-caps at 50; Drop Chance multiplies the whole
        loot factor. Evaluate those native outcomes after aggregating the candidate set. This makes an
        80% Energy Speed accessory worth exactly zero when the player is already speed-capped, while
        still crediting its Magic and loot effects when productive resource sinks or collection/boost
        debt exist. Other special systems retain their separate horizon-aware policy weights above.
        */
        private static double ProductionRateUtility(Character c, Plan plan)
        {
            var controller = c.inventoryController;
            var energySpeedBonus = PlanBonus(controller, plan, specType.EnergySpeed);
            var magicSpeedBonus = PlanBonus(controller, plan, specType.MagicSpeed);
            var energyBarBonus = PlanBonus(controller, plan, specType.EnergyPerBar)
                                 + PlanBonus(controller, plan, specType.EnergyPerBar2)
                                 + PlanBonus(controller, plan, specType.EnergyPerBar3)
                                 + PlanBonus(controller, plan, specType.AllPerBar);
            var magicBarBonus = PlanBonus(controller, plan, specType.MagicPerBar)
                                + PlanBonus(controller, plan, specType.MagicPerBar2)
                                + PlanBonus(controller, plan, specType.MagicPerBar3)
                                + PlanBonus(controller, plan, specType.AllPerBar);
            var lootBonus = PlanBonus(controller, plan, specType.Looting)
                            + PlanBonus(controller, plan, specType.Looting2);

            var candidateEnergySpeed = Math.Max(1.0,
                Math.Min(50.0, c.energySpeed * (1.0 + energySpeedBonus)));
            var candidateMagicSpeed = Math.Max(1.0,
                Math.Min(50.0, c.magic.magicBarSpeed * (1.0 + magicSpeedBonus)));
            var currentEnergyBarBonus = controller.bonuses[specType.EnergyPerBar]
                                        + controller.bonuses[specType.EnergyPerBar2]
                                        + controller.bonuses[specType.EnergyPerBar3]
                                        + controller.bonuses[specType.AllPerBar];
            var currentMagicBarBonus = controller.bonuses[specType.MagicPerBar]
                                       + controller.bonuses[specType.MagicPerBar2]
                                       + controller.bonuses[specType.MagicPerBar3]
                                       + controller.bonuses[specType.AllPerBar];
            var candidateEnergyBar = Math.Max(1L, (long)Math.Floor(c.totalEnergyBar()
                * (1.0 + energyBarBonus) / Math.Max(1e-9, 1.0 + currentEnergyBarBonus)));
            var candidateMagicBar = Math.Max(1L, (long)Math.Floor(c.totalMagicBar()
                * (1.0 + magicBarBonus) / Math.Max(1e-9, 1.0 + currentMagicBarBonus)));
            var candidateEnergyRate = DiscreteResourceRate(candidateEnergySpeed, candidateEnergyBar);
            var candidateMagicRate = DiscreteResourceRate(candidateMagicSpeed, candidateMagicBar);
            var currentEnergyRate = Math.Max(1e-9, c.energyPerSecond());
            var currentMagicRate = Math.Max(1e-9, c.magicPerSecond());

            // A rate which only grows an already-idle pool has little immediate
            // value. It is not zero because caps and allocations can change later
            // in the same run; fully utilized resources get the full shadow price.
            var energyUse = c.curEnergy <= 0 ? 0.25
                : Math.Max(0.25, Math.Min(1.0, (c.curEnergy - c.idleEnergy) / (double)c.curEnergy));
            var magicUse = c.magic.curMagic <= 0 ? 0.25
                : Math.Max(0.25, Math.Min(1.0,
                    (c.magic.curMagic - c.magic.idleMagic) / (double)c.magic.curMagic));
            var utility = 6.0 * energyUse * Math.Log(Math.Max(1e-9, candidateEnergyRate / currentEnergyRate))
                          + 4.0 * magicUse * Math.Log(Math.Max(1e-9, candidateMagicRate / currentMagicRate));

            // During post-rebirth refill, a resource-specialized set can be the
            // fastest progression set even when its raw Adventure stats are lower.
            // Price the exact seconds removed from the outstanding refill, capped at
            // a ten-minute decision horizon; once both pools are full this term is 0
            // and combat/loot gear immediately regains authority.
            var missingEnergy = Math.Max(0.0, c.totalCapEnergy() - c.curEnergy);
            var missingMagic = Math.Max(0.0, c.totalCapMagic() - c.magic.curMagic);
            var currentEnergyFill = Math.Min(600.0, missingEnergy / currentEnergyRate);
            var candidateEnergyFill = Math.Min(600.0, missingEnergy / Math.Max(1e-9, candidateEnergyRate));
            var currentMagicFill = Math.Min(600.0, missingMagic / currentMagicRate);
            var candidateMagicFill = Math.Min(600.0, missingMagic / Math.Max(1e-9, candidateMagicRate));
            utility += 20.0 * ((currentEnergyFill - candidateEnergyFill)
                               + (currentMagicFill - candidateMagicFill));

            // Gold Drop specials affect Adventure enemy drops, not Character.grossGoldPerSecond(),
            // whose native formula is entirely Time Machine and permanent multipliers. Do not equip
            // a Gold Drop set to resolve a Blood/TM GPS shortfall; it has exactly zero modeled effect.

            var currentLootBonus = controller.bonuses[specType.Looting]
                                   + controller.bonuses[specType.Looting2];
            var cubeLoot = controller.cubeLootBonus();
            var lootRatio = (1.0 + lootBonus + cubeLoot)
                            / Math.Max(1e-9, 1.0 + currentLootBonus + cubeLoot);
            // Loot is a persistent throughput input for collection, boosts, EXP and AP.
            // Do not query AdventureCollectionPlanner here: that planner itself asks
            // this scorer about future gear and would create a recursive evaluation.
            utility += 2.5 * Math.Log(Math.Max(1e-9, lootRatio));
            return utility;
        }

        private static double PlanBonus(InventoryController controller, Plan plan, specType type)
        {
            var total = plan.PrimaryItems().Where(x => x != null && x.id > 0)
                .Sum(x => (double)controller.equipSpecBonus(type, x));
            if (plan.Weapon2 != null && plan.Weapon2.id > 0)
                total += controller.equipSpecBonus(type, plan.Weapon2) * controller.weapon2Factor();
            return Math.Max(0.0, total);
        }

        private static double DiscreteResourceRate(double speed, long perBar)
        {
            return 50.0 / Math.Max(1.0, Math.Ceiling(50.0 / Math.Max(1.0, speed)))
                   * Math.Max(1L, perBar);
        }

        // Candidate trimming must retain production accessories before the full
        // plan is available. This is only a generous upper-bound ranking; final
        // selection always uses the complete, saturation-aware calculation above.
        private static double ProductionTrimUtility(Character c, Equipment e)
        {
            var controller = c.inventoryController;
            if (_scoreItopod)
            {
                var respawn = Math.Max(0.0, controller.equipSpecBonus(specType.Respawn, e));
                return 12.0 * Math.Log(1.0 + respawn);
            }
            var energySpeed = Math.Max(0.0, controller.equipSpecBonus(specType.EnergySpeed, e));
            var magicSpeed = Math.Max(0.0, controller.equipSpecBonus(specType.MagicSpeed, e));
            var loot = Math.Max(0.0, controller.equipSpecBonus(specType.Looting, e)
                                      + controller.equipSpecBonus(specType.Looting2, e));
            var energyBars = Math.Max(0.0, controller.equipSpecBonus(specType.EnergyPerBar, e)
                + controller.equipSpecBonus(specType.EnergyPerBar2, e)
                + controller.equipSpecBonus(specType.EnergyPerBar3, e)
                + controller.equipSpecBonus(specType.AllPerBar, e));
            var magicBars = Math.Max(0.0, controller.equipSpecBonus(specType.MagicPerBar, e)
                + controller.equipSpecBonus(specType.MagicPerBar2, e)
                + controller.equipSpecBonus(specType.MagicPerBar3, e)
                + controller.equipSpecBonus(specType.AllPerBar, e));
            var gold = Math.Max(0.0, controller.equipSpecBonus(specType.GoldDropAmount, e)
                + controller.equipSpecBonus(specType.GoldDrop2, e)
                + controller.equipSpecBonus(specType.GoldDropRNG, e));
            return 2.0 * Math.Log(1.0 + energySpeed)
                   + 2.0 * Math.Log(1.0 + magicSpeed)
                   + 2.0 * Math.Log(1.0 + energyBars)
                   + 2.0 * Math.Log(1.0 + magicBars)
                   + Math.Log(1.0 + gold) + Math.Log(1.0 + loot);
        }

        private static Plan CurrentPlan(Character c, bool includeEmpty = false)
        {
            var inv = c.inventory;
            var p = new Plan
            {
                Head = Valid(inv.head, includeEmpty), Chest = Valid(inv.chest, includeEmpty),
                Legs = Valid(inv.legs, includeEmpty), Boots = Valid(inv.boots, includeEmpty),
                Weapon = Valid(inv.weapon, includeEmpty),
                Weapon2 = c.inventoryController.weapon2Unlocked() ? Valid(inv.weapon2, includeEmpty) : null
            };
            var activeAccessories = inv.accs.Take(Math.Min(inv.accs.Count,
                Math.Max(0, c.inventoryController.accessorySpaces())));
            p.Accessories.AddRange(includeEmpty ? activeAccessories
                : activeAccessories.Where(x => Valid(x, false) != null));
            return p;
        }

        private static Equipment Valid(Equipment e, bool includeEmpty)
        {
            return e != null && (includeEmpty || e.id > 0) ? e : null;
        }

        private static string Describe(Plan plan)
        {
            var labels = new List<string>();
            foreach (var item in plan.PrimaryItems().Concat(plan.Weapon2 == null
                         ? Enumerable.Empty<Equipment>() : new[] {plan.Weapon2}))
            {
                if (item == null || item.id <= 0) continue;
                try { labels.Add(Controller.itemInfo.itemName[item.id]); }
                catch { labels.Add("item " + item.id); }
            }
            return labels.Count == 0 ? "no usable equipment owned" : string.Join(", ", labels.ToArray());
        }

        private static int Fingerprint(IEnumerable<Equipment> items, int accessorySpaces)
        {
            unchecked
            {
                var hash = 17 * 31 + accessorySpaces;
                foreach (var e in items.OrderBy(x => x.id).ThenBy(x => x.level))
                {
                    hash = hash * 31 + e.id;
                    hash = hash * 31 + e.level;
                    hash = hash * 31 + (int)e.curAttack;
                    hash = hash * 31 + (int)e.curDefense;
                    hash = hash * 31 + (int)(e.spec1Cur + e.spec2Cur + e.spec3Cur);
                }
                // Include the ordered physical layout. The former multiset-only
                // fingerprint could not see an equipped item being swapped with an
                // inventory item, so manual changes remained cached as "optimal".
                var c = Main.Character;
                if (c != null && c.inventory != null)
                {
                    var inv = c.inventory;
                    foreach (var e in new[] {inv.head, inv.chest, inv.legs, inv.boots, inv.weapon, inv.weapon2})
                        hash = hash * 31 + (e == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e));
                    foreach (var e in inv.accs)
                        hash = hash * 31 + (e == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e));
                    foreach (var e in inv.inventory)
                        hash = hash * 31 + (e == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e));
                }
                return hash;
            }
        }

        private static bool ApplyPhysicalPlan(Character c, Plan desired)
        {
            var snapshot = CurrentPlan(c, true);
            try
            {
                string preflightReason;
                if (!ValidatePlan(c, desired, out preflightReason) || !LoadoutManager.CanSwap()
                    || Controller.midDrag || c.bossController.isFighting || c.bossController.nukeBoss
                    || c.adventureController.currentEnemy != null)
                {
                    Main.LogAction("REJECTED", "Physical loadout preflight blocked: "
                                                      + (string.IsNullOrEmpty(preflightReason)
                                                          ? "combat/drag/loadout state changed" : preflightReason));
                    return false;
                }
                // Native equipment swaps can reject a lower-cap set while resources
                // are allocated. Reclaim allocations (not earned levels) first; the
                // 0.2-second allocator restores them immediately after this transaction.
                c.removeAllEnergy();
                c.removeMostMagic();
                c.removeAllRes3();
                if (!ExecutePhysical(c, desired) || !Matches(c, desired))
                {
                    var rolledBack = ExecutePhysical(c, snapshot) && Matches(c, snapshot);
                    c.inventoryController.updateBonuses();
                    c.inventoryController.updateInventory();
                    Main.RestoreAllocationsAfterGearSwap();
                    Main.LogAction("REJECTED", rolledBack
                        ? "Physical loadout rejected; original physical slots verified after rollback"
                        : "Physical loadout rejected and rollback verification FAILED");
                    return false;
                }
                c.inventoryController.updateBonuses();
                c.inventoryController.updateInventory();
                Main.RestoreAllocationsAfterGearSwap();
                return Matches(c, desired);
            }
            catch (Exception ex)
            {
                try
                {
                    var rolledBack = ExecutePhysical(c, snapshot) && Matches(c, snapshot);
                    c.inventoryController.updateBonuses();
                    c.inventoryController.updateInventory();
                    Main.RestoreAllocationsAfterGearSwap();
                    Main.LogAction("REJECTED", rolledBack
                        ? "Exception path restored and verified the original physical slots"
                        : "Exception path could not verify the original physical slots");
                }
                catch { /* the verified rejection below remains truthful */ }
                Main.LogAction("REJECTED", "Physical loadout transaction failed and rollback was attempted: " + ex.Message);
                return false;
            }
        }

        private static bool ValidatePlan(Character c, Plan p, out string reason)
        {
            reason = string.Empty;
            var inv = c.inventory;
            var desired = new List<Equipment> {p.Head, p.Chest, p.Legs, p.Boots, p.Weapon, p.Weapon2};
            desired.AddRange(p.Accessories);
            var seen = new List<Equipment>();
            var ids = new HashSet<int>();
            foreach (var item in desired.Where(x => x != null))
            {
                if (seen.Any(x => ReferenceEquals(x, item)))
                {
                    reason = "one physical object was assigned to multiple slots";
                    return false;
                }
                seen.Add(item);
                if (item.id > 0 && !ids.Add(item.id))
                {
                    reason = "duplicate non-zero item ID violates the game's equipped-set rule";
                    return false;
                }
            }
            if (!CorrectType(p.Head, part.Head) || !CorrectType(p.Chest, part.Chest)
                || !CorrectType(p.Legs, part.Legs) || !CorrectType(p.Boots, part.Boots)
                || !CorrectType(p.Weapon, part.Weapon) || !CorrectType(p.Weapon2, part.Weapon)
                || p.Accessories.Any(x => !CorrectType(x, part.Accessory)))
            {
                reason = "an item does not match its target equipment slot";
                return false;
            }
            var physical = new List<Equipment>
                {inv.head, inv.chest, inv.legs, inv.boots, inv.weapon, inv.weapon2};
            physical.AddRange(inv.accs);
            physical.AddRange(inv.inventory);
            if (seen.Any(item => !physical.Any(x => ReferenceEquals(x, item))))
            {
                reason = "a selected physical item is no longer in inventory/equipment";
                return false;
            }
            var activeAccessories = Math.Min(inv.accs.Count,
                Math.Max(0, c.inventoryController.accessorySpaces()));
            if (p.Accessories.Count != activeAccessories)
            {
                reason = "accessory plan does not cover every active slot";
                return false;
            }
            return true;
        }

        private static bool CorrectType(Equipment item, part expected)
        {
            return item == null || item.id <= 0 || item.type == expected;
        }

        private static bool ExecutePhysical(Character c, Plan p)
        {
            var inv = c.inventory;
            if (!SwapFixed(inv, p.Head, () => inv.head, inv.swapHead)) return false;
            if (!SwapFixed(inv, p.Chest, () => inv.chest, inv.swapChest)) return false;
            if (!SwapFixed(inv, p.Legs, () => inv.legs, inv.swapLegs)) return false;
            if (!SwapFixed(inv, p.Boots, () => inv.boots, inv.swapBoots)) return false;

            if (p.Weapon != null && !ReferenceEquals(inv.weapon, p.Weapon))
            {
                if (ReferenceEquals(inv.weapon2, p.Weapon)) inv.swapWeapons();
                else
                {
                    var index = InventoryIndex(inv, p.Weapon);
                    if (index < 0) return false;
                    inv.item2 = index;
                    inv.swapWeapon();
                }
            }
            if (p.Weapon2 != null && !ReferenceEquals(inv.weapon2, p.Weapon2))
            {
                if (ReferenceEquals(inv.weapon, p.Weapon2)) inv.swapWeapons();
                else
                {
                    var index = InventoryIndex(inv, p.Weapon2);
                    if (index < 0) return false;
                    inv.item2 = index;
                    inv.swapWeapon2();
                }
            }

            for (var i = 0; i < p.Accessories.Count && i < inv.accs.Count; i++)
            {
                var target = p.Accessories[i];
                if (ReferenceEquals(inv.accs[i], target)) continue;
                var equippedIndex = inv.accs.FindIndex(x => ReferenceEquals(x, target));
                if (equippedIndex >= 0) inv.swapAccs(i, equippedIndex);
                else
                {
                    var itemIndex = InventoryIndex(inv, target);
                    if (itemIndex < 0) return false;
                    inv.swapAccWithItem(i, itemIndex);
                }
            }
            return true;
        }

        private static bool SwapFixed(Inventory inv, Equipment desired, Func<Equipment> current, Action swap)
        {
            if (desired == null || ReferenceEquals(current(), desired)) return true;
            var index = InventoryIndex(inv, desired);
            if (index < 0) return false;
            inv.item2 = index;
            swap();
            return ReferenceEquals(current(), desired);
        }

        private static int InventoryIndex(Inventory inv, Equipment target)
        {
            for (var i = 0; i < inv.inventory.Count; i++)
                if (ReferenceEquals(inv.inventory[i], target)) return i;
            return -1;
        }

        private static bool Matches(Character c, Plan p)
        {
            var inv = c.inventory;
            if (p.Head != null && !ReferenceEquals(inv.head, p.Head)) return false;
            if (p.Chest != null && !ReferenceEquals(inv.chest, p.Chest)) return false;
            if (p.Legs != null && !ReferenceEquals(inv.legs, p.Legs)) return false;
            if (p.Boots != null && !ReferenceEquals(inv.boots, p.Boots)) return false;
            if (p.Weapon != null && !ReferenceEquals(inv.weapon, p.Weapon)) return false;
            if (p.Weapon2 != null && !ReferenceEquals(inv.weapon2, p.Weapon2)) return false;
            var activeCount = Math.Min(inv.accs.Count, Math.Max(0, c.inventoryController.accessorySpaces()));
            return p.Accessories.Count == activeCount
                   && p.Accessories.Select((x, i) => ReferenceEquals(inv.accs[i], x)).All(x => x);
        }
    }
}
