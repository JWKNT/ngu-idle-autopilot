using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static NGUInjector.Main;

/*
FILE PURPOSE

ProgressionLoadoutOptimizer selects the best physical equipment objects for the active boss,
Adventure, Titan, or resource context, including ordered weapons and constrained accessories. It
executes reference-identity native swap transactions, reclaims allocations before cap-lowering
gear, verifies the final layout, and rolls back on failure. ID-only equality and direct field
assignment are unsafe because duplicate copies and saved loadouts have physical identity.
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

        internal static string LastDecision { get; private set; } = "Waiting for an inventory snapshot";
        internal static double LastScoreGain { get; private set; }
        internal static string LastObjective { get; private set; } = "unresolved";
        internal static bool LastSearchExact { get; private set; }
        private static readonly MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod(
            "MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

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
            var objective = Objective(c);
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
                _authoritativeObjective = string.Empty;
            }

            // This path deliberately runs before the inventory-probe cadence. The
            // enemy-free frame can be shorter than one second under continuous
            // Adventure automation.
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
            if (SameLayout(best, current)
                || !(bestScore > currentScore + Math.Max(1e-7, Math.Abs(currentScore) * 1e-7)))
            {
                _authoritativePlan = current.Clone();
                _authoritativeObjective = objective;
                _pendingPlan = null;
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
                LastDecision = "Verified equipment upgrade queued for the next natural post-kill frame";
                return;
            }

            ApplyChosenPlan(c, best, now, "Equipped");
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
            var ranked = groups.SelectMany(g => g.OrderByDescending(x => ItemUtility(c, x)).Take(2))
                .OrderByDescending(x => ItemUtility(c, x)).ToList();
            if (ranked.Count > count) _searchExact = false;
            return ranked.Take(count);
        }

        private static double ItemUtility(Character c, Equipment e)
        {
            var attack = c.inventoryController.equipAttackBonus(e);
            var defense = c.inventoryController.equipDefenseBonus(e);
            return 4.0 * Math.Log(1.0 + Math.Max(0, attack))
                   + 3.0 * Math.Log(1.0 + Math.Max(0, defense)) + SpecialUtility(c, e, 1.0);
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
            if (c == null || e == null || e.id <= 0 || MemberwiseCloneMethod == null)
                return 0.0;
            try
            {
                var projected = (Equipment)MemberwiseCloneMethod.Invoke(e, null);
                projected.curAttack = BoostCap(projected.capAttack, projected.level);
                projected.curDefense = BoostCap(projected.capDefense, projected.level);
                projected.spec1Cur = BoostCap(projected.spec1Cap, projected.level);
                projected.spec2Cur = BoostCap(projected.spec2Cap, projected.level);
                projected.spec3Cur = BoostCap(projected.spec3Cap, projected.level);
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
            if (!bossObjective)
            {
                score += 6.0 * Math.Log(1.0 + Math.Max(0.0, candidateAdvAttack));
                score += 5.0 * Math.Log(1.0 + Math.Max(0.0, candidateAdvDefense));

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
            else
            {
                // Adventure remains the secondary objective among equally viable
                // boss sets so the post-fight gear is not needlessly brittle.
                score += 0.3 * Math.Log(1.0 + Math.Max(0.0, candidateAdvAttack));
                score += 0.3 * Math.Log(1.0 + Math.Max(0.0, candidateAdvDefense));
            }
            foreach (var item in scoringItems) score += SpecialUtility(c, item, 1.0);
            if (plan.Weapon2 != null && plan.Weapon2.id > 0)
                score += SpecialUtility(c, plan.Weapon2, weapon2Factor);
            return score;
        }

        private static bool UseBossObjective(Character c)
        {
            var highest = c.settings.rebirthDifficulty == difficulty.normal ? c.highestBoss
                : c.settings.rebirthDifficulty == difficulty.evil ? c.highestHardBoss
                : c.highestSadisticBoss;
            if (c.bossID < highest) return true; // native sequential catch-up
            double killSeconds;
            return CombatHelpers.CanNukeCurrentBoss(c)
                   || (CombatHelpers.CanWinCurrentBoss(c, out killSeconds) && killSeconds <= 120.0);
        }

        private static string Objective(Character c)
        {
            if (UseBossObjective(c)) return "selected Fight Boss defeat";
            var front = ZoneStatHelper.GetBestZone();
            int nextZone;
            ZoneStats nextStats;
            return ZoneStatHelper.TryGetNextUnlockedZone(front == null ? -1 : front.Zone,
                out nextZone, out nextStats)
                ? "Adventure progression toward " + nextStats.Name
                : "continuous Adventure progression";
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
                case specType.Looting:
                case specType.Looting2:
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
                    Main.LogAction("REJECTED", rolledBack
                        ? "Physical loadout rejected; original physical slots verified after rollback"
                        : "Physical loadout rejected and rollback verification FAILED");
                    return false;
                }
                c.inventoryController.updateBonuses();
                c.inventoryController.updateInventory();
                return Matches(c, desired);
            }
            catch (Exception ex)
            {
                try
                {
                    var rolledBack = ExecutePhysical(c, snapshot) && Matches(c, snapshot);
                    c.inventoryController.updateBonuses();
                    c.inventoryController.updateInventory();
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
