using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NGUInjector.Autopilot;
using static NGUInjector.Main;

/*
FILE PURPOSE

ProgressionLoadoutOptimizer selects the best physical equipment objects for the active boss,
Adventure, major-unlock, or resource-refill context, including ordered weapons and constrained
accessories. One immutable objective/character snapshot feeds a Pareto branch-and-bound search over
canonical accessory combinations; results report incumbent seconds, an admissible lower bound, and
the remaining gap. Hard major-unlock combat uses target-enemy kill/survival math and excludes
unrelated production bonuses; routine contexts may accept lower raw combat stats for a proven ETA
improvement. It executes reference-identity native swap transactions, reclaims allocations before
cap-lowering gear, verifies the final layout, and rolls back on failure. ID-only equality and direct
field assignment are unsafe because duplicate copies and saved loadouts have physical identity.
*/
namespace NGUInjector.Managers
{
    // Chooses equipment as a complete progression set.  Native item contribution
    // methods are used so effectiveBossID scaling and per-item flooring stay exact.
    internal static class ProgressionLoadoutOptimizer
    {
        private const int SearchNodeBudget = 75000;
        private const double SwapSetupSeconds = 0.02;
        private const int MetricAttack = 0;
        private const int MetricDefense = 1;
        private const int MetricLoot = 2;
        private const int MetricRespawn = 3;
        private const int MetricEnergySpeed = 4;
        private const int MetricMagicSpeed = 5;
        private const int MetricEnergyBar = 6;
        private const int MetricMagicBar = 7;
        private const int MetricGeneral = 8;
        private const int MetricCount = 9;
        private const long TagApathy = 1L;
        private static int _lastFingerprint;
        private static double _lastRun;
        private static double _lastInventoryProbe;
        private static Plan _failedPlan;
        private static double _failedUntil;
        private static bool _searchExact;
        private static LoadoutSearchResult _lastSearchResult;
        private static BoundObjective _boundObjective;
        private static long _nextObjectiveEpoch;
        private static Plan _authoritativePlan;
        private static Plan _pendingPlan;
        private static string _authoritativeObjective = string.Empty;
        private static long _authoritativeEpoch = -1L;
        private static long _pendingEpoch = -1L;
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
        internal static long LastObjectiveEpoch { get; private set; }
        internal static double LastIncumbentSeconds { get; private set; } = double.PositiveInfinity;
        internal static double LastLowerBoundSeconds { get; private set; } = double.PositiveInfinity;
        internal static double LastOptimalityGap { get; private set; } = double.PositiveInfinity;
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

        private sealed class EnemySnapshot
        {
            internal readonly double Attack;
            internal readonly double Defense;
            internal readonly double MaxHp;
            internal readonly double Regen;
            internal readonly double AttackRate;
            internal readonly int Type;

            internal EnemySnapshot(Enemy enemy)
            {
                Attack = enemy == null ? 0.0 : enemy.attack;
                Defense = enemy == null ? 0.0 : enemy.defense;
                MaxHp = enemy == null ? 0.0 : enemy.maxHP;
                Regen = enemy == null ? 0.0 : enemy.regen;
                AttackRate = enemy == null ? 1.0 : Math.Max(0.02, enemy.attackRate);
                Type = enemy == null ? -1 : (int)enemy.enemyType;
            }
        }

        // Immutable affine baselines shared by every candidate projection in one objective
        // epoch. Search must not observe a different live character/resource/route state after
        // another candidate changes reach or after Unity advances a frame.
        private sealed class ProjectionSnapshot
        {
            internal readonly double FightAttack;
            internal readonly double FightDefense;
            internal readonly double CurrentAttackItems;
            internal readonly double CurrentDefenseItems;
            internal readonly double AttackBase;
            internal readonly double DefenseBase;
            internal readonly double AttackCommon;
            internal readonly double DefenseCommon;
            internal readonly double AdventureAttackBase;
            internal readonly double AdventureDefenseBase;
            internal readonly double AdventureMaxHpBase;
            internal readonly double CubePower;
            internal readonly double CubeToughness;
            internal readonly double AdventureAttack;
            internal readonly double AdventureDefense;
            internal readonly double AdventureMaxHp;
            internal readonly double AdventureHpRegen;
            internal readonly double CurrentRespawnGear;
            internal readonly int ItopodFloor;
            internal readonly bool ItopodClimbing;
            internal readonly bool ItopodManual;
            internal readonly double ItopodAttackCadence;
            internal readonly double EnergySpeed;
            internal readonly double MagicSpeed;
            internal readonly double EnergyBar;
            internal readonly double MagicBar;
            internal readonly double CurrentEnergyBarGear;
            internal readonly double CurrentMagicBarGear;
            internal readonly double EnergyRemaining;
            internal readonly double MagicRemaining;

            internal ProjectionSnapshot(Character c, int itopodFloor, bool itopodClimbing,
                bool itopodManual, double itopodAttackCadence)
            {
                var controller = c.inventoryController;
                FightAttack = Math.Max(0.0, c.attack);
                FightDefense = Math.Max(0.0, c.defense);
                CurrentAttackItems = Math.Max(0.0, controller.attackBonus());
                CurrentDefenseItems = Math.Max(0.0, controller.defenseBonus());
                AttackBase = Math.Max(0.0, c.training.getTotalAttack());
                DefenseBase = Math.Max(0.0, c.training.getTotalDefense());
                AttackCommon = c.attackMulti * c.adventureController.itopod.totalStatBonus()
                               * c.attackBoost;
                DefenseCommon = c.defenseMulti * c.adventureController.itopod.totalStatBonus()
                                * c.defenseBoost;
                AdventureAttackBase = c.adventure.attack;
                AdventureDefenseBase = c.adventure.defense;
                AdventureMaxHpBase = c.adventure.maxHP;
                CubePower = controller.cubePower();
                CubeToughness = controller.cubeToughness();
                AdventureAttack = Math.Max(0.0, c.totalAdvAttack());
                AdventureDefense = Math.Max(0.0, c.totalAdvDefense());
                AdventureMaxHp = Math.Max(0.0, c.totalAdvHP());
                AdventureHpRegen = Math.Max(0.0, c.totalAdvHPRegen());
                CurrentRespawnGear = Math.Max(0.0, controller.bonuses[specType.Respawn]);
                ItopodFloor = itopodFloor;
                ItopodClimbing = itopodClimbing;
                ItopodManual = itopodManual;
                ItopodAttackCadence = Math.Max(.02, itopodAttackCadence);
                EnergySpeed = c.energySpeed;
                MagicSpeed = c.magic.magicBarSpeed;
                EnergyBar = c.totalEnergyBar();
                MagicBar = c.totalMagicBar();
                CurrentEnergyBarGear = controller.bonuses[specType.EnergyPerBar]
                                       + controller.bonuses[specType.EnergyPerBar2]
                                       + controller.bonuses[specType.EnergyPerBar3]
                                       + controller.bonuses[specType.AllPerBar];
                CurrentMagicBarGear = controller.bonuses[specType.MagicPerBar]
                                      + controller.bonuses[specType.MagicPerBar2]
                                      + controller.bonuses[specType.MagicPerBar3]
                                      + controller.bonuses[specType.AllPerBar];
                EnergyRemaining = Math.Max(0.0, c.totalCapEnergy() - c.curEnergy);
                MagicRemaining = Math.Max(0.0, c.totalCapMagic() - c.magic.curMagic);
            }
        }

        // Every mutable live selector is copied before search.  Complete and lower-bound
        // evaluations receive this same object; an equipped candidate may change live reach but
        // cannot change the objective kind, target, version, current HP, or target records.
        private sealed class BoundObjective
        {
            internal readonly OptimizationObjective Objective;
            internal readonly MajorUnlockTarget Major;
            internal readonly EnemySnapshot[] Enemies;
            internal readonly ZoneStats TargetStats;
            internal readonly int BossId;
            internal readonly int HighestBoss;
            internal readonly double FightPlayerHp;
            internal readonly double BossAttack;
            internal readonly double BossDefense;
            internal readonly double BossMaxHp;
            internal readonly double BossRegen;
            internal readonly double RegularAttackPower;
            internal readonly double IdleAttackPower;
            internal readonly double LiveLootBonus;
            internal readonly double CubeLootBonus;
            internal readonly double LiveRespawnSeconds;
            internal readonly double SafeRecoveryHpPerSecond;
            internal readonly bool CapacityReady;
            internal readonly string CapacityReason;
            internal readonly string Key;
            internal readonly ProjectionSnapshot Projection;

            internal BoundObjective(OptimizationObjective objective, MajorUnlockTarget major,
                EnemySnapshot[] enemies, ZoneStats targetStats, int bossId, int highestBoss,
                double fightPlayerHp, double bossAttack, double bossDefense, double bossMaxHp,
                double bossRegen, double regularAttackPower, double idleAttackPower,
                double liveLootBonus, double cubeLootBonus, double liveRespawnSeconds,
                double safeRecoveryHpPerSecond, bool capacityReady, string capacityReason,
                string key, ProjectionSnapshot projection)
            {
                Objective = objective;
                Major = major;
                Enemies = enemies ?? new EnemySnapshot[0];
                TargetStats = targetStats;
                BossId = bossId;
                HighestBoss = highestBoss;
                FightPlayerHp = fightPlayerHp;
                BossAttack = bossAttack;
                BossDefense = bossDefense;
                BossMaxHp = bossMaxHp;
                BossRegen = bossRegen;
                RegularAttackPower = regularAttackPower;
                IdleAttackPower = idleAttackPower;
                LiveLootBonus = liveLootBonus;
                CubeLootBonus = cubeLootBonus;
                LiveRespawnSeconds = liveRespawnSeconds;
                SafeRecoveryHpPerSecond = safeRecoveryHpPerSecond;
                CapacityReady = capacityReady;
                CapacityReason = capacityReason ?? string.Empty;
                Key = key ?? string.Empty;
                Projection = projection;
            }
        }

        private sealed class CandidateProjection
        {
            internal double FightAttack;
            internal double FightDefense;
            internal double AdventureAttack;
            internal double AdventureDefense;
            internal double AdventureMaxHp;
            internal double AdventureCurrentHp;
            internal double AdventureHpRegen;
            internal double LootBonus;
            internal double RespawnBonus;
            internal double EnergySpeedBonus;
            internal double MagicSpeedBonus;
            internal double EnergyBarBonus;
            internal double MagicBarBonus;
            internal double General;
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
            if (string.IsNullOrEmpty(_leaseKind))
            {
                _leaseKind = "routine";
                _leaseUntil = now + 15.0;
            }
            var boundObjective = BindObjective(c, bossObjective, itopodObjective,
                majorUnlock, now, false);
            var objective = boundObjective.Objective.DisplayName;
            _leasedRoutineObjective = _leaseKind == "routine" ? objective : string.Empty;
            LastObjective = objective;
            LastObjectiveEpoch = boundObjective.Objective.Epoch;

            // Full automation owns the progression loadout just as it owns resource
            // allocations. Reassert the last verified exact-reference plan before
            // the expensive search throttle: a manual equip swap changes topology,
            // not the inventory multiset, and must not survive for five seconds.
            string authoritativeReason;
            if (_authoritativePlan != null && _authoritativeObjective == objective
                && _authoritativeEpoch == boundObjective.Objective.Epoch
                && ValidatePlan(c, _authoritativePlan, out authoritativeReason))
            {
                var live = CurrentPlan(c, true);
                if (!SameLayout(live, _authoritativePlan))
                {
                    _pendingPlan = _authoritativePlan.Clone();
                    _pendingEpoch = boundObjective.Objective.Epoch;
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
                _authoritativeEpoch = -1L;
                _pendingEpoch = -1L;
            }

            // This path deliberately runs before the inventory-probe cadence. The
            // enemy-free frame can be shorter than one second under continuous
            // Adventure automation.
            if (_pendingPlan != null && (_pendingEpoch != boundObjective.Objective.Epoch
                || _pendingContext != ContextKey(c, objective)))
            {
                _pendingPlan = null;
                _pendingContext = string.Empty;
                _pendingEpoch = -1L;
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
            var currentEvaluation = EvaluateTotals(boundObjective,
                SnapshotPlanTotals(c, current, 0.0));
            var best = Optimize(c, all, boundObjective);
            LastSearchExact = _lastSearchResult != null && _lastSearchResult.IsProvenOptimal;
            LastIncumbentSeconds = _lastSearchResult == null
                ? double.PositiveInfinity : _lastSearchResult.IncumbentSeconds;
            LastLowerBoundSeconds = _lastSearchResult == null
                ? double.PositiveInfinity : _lastSearchResult.OptimisticLowerBoundSeconds;
            LastOptimalityGap = _lastSearchResult == null
                ? double.PositiveInfinity : _lastSearchResult.AbsoluteGapSeconds;
            var bestEvaluation = _lastSearchResult == null ? null : _lastSearchResult.Evaluation;
            var currentSeconds = currentEvaluation != null && currentEvaluation.Feasible
                ? currentEvaluation.TotalSeconds : double.PositiveInfinity;
            var bestSeconds = bestEvaluation != null && bestEvaluation.Feasible
                ? bestEvaluation.TotalSeconds : double.PositiveInfinity;
            LastScoreGain = double.IsInfinity(currentSeconds)
                ? double.IsInfinity(bestSeconds) ? 0.0 : double.MaxValue
                : currentSeconds - bestSeconds;
            var materialGain = 0.02; // explicit model/transaction uncertainty after setup is priced
            if (SameLayout(best, current)
                || !(bestSeconds + materialGain < currentSeconds))
            {
                _authoritativePlan = current.Clone();
                _authoritativeObjective = objective;
                _authoritativeEpoch = boundObjective.Objective.Epoch;
                _pendingPlan = null;
                _pendingContext = string.Empty;
                _pendingEpoch = -1L;
                LastDecision = (LastSearchExact ? "Proven optimal " : "Bounded ")
                               + LastObjective + " set active: " + Describe(current)
                               + "; incumbent " + FormatSeconds(LastIncumbentSeconds)
                               + ", lower bound " + FormatSeconds(LastLowerBoundSeconds)
                               + ", gap " + FormatSeconds(LastOptimalityGap);
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
                _pendingEpoch = boundObjective.Objective.Epoch;
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
            _boundObjective = null;
        }

        private static BoundObjective BindObjective(Character c, bool bossObjective,
            bool itopodObjective, MajorUnlockTarget majorUnlock, double now, bool forceNew)
        {
            var highest = c.settings.rebirthDifficulty == difficulty.normal ? c.highestBoss
                : c.settings.rebirthDifficulty == difficulty.evil ? c.highestHardBoss
                : c.highestSadisticBoss;
            if (!forceNew && _boundObjective != null)
            {
                if (_leaseKind == "routine" && now < _leaseUntil)
                    return _boundObjective;
                if (_leaseKind == "boss" && _boundObjective.Objective.Kind == LoadoutObjectiveKind.FightBoss
                    && _boundObjective.BossId == c.bossID
                    && _boundObjective.HighestBoss == highest)
                    return _boundObjective;
                if (_leaseKind == "itopod" && _boundObjective.Objective.Kind == LoadoutObjectiveKind.Itopod)
                {
                    var route = ZoneHelpers.LastItopodRoute;
                    var routeId = "itopod:" + route.Start + ":" + route.End + ":"
                                  + route.FarmFloor + ":climb=" + route.Climbing;
                    if (_boundObjective.Objective.Id == routeId) return _boundObjective;
                }
                if (_leaseKind == "major" && majorUnlock != null
                    && _boundObjective.Objective.Kind == LoadoutObjectiveKind.MajorUnlock
                    && _boundObjective.Objective.Id == MajorKey(majorUnlock))
                    return _boundObjective;
            }

            var kind = LoadoutObjectiveKind.ContinuousAdventure;
            var display = "continuous Adventure progression";
            var id = "routine:continuous";
            var targetZone = -1;
            var targetEnemy = -1;
            var titanIndex = -1;
            var titanVersion = -1;
            var bossOnly = false;
            var valuesLoot = false;
            var dropChance = 0.0;
            var itopodFloor = 0;
            var itopodClimbing = false;
            var itopodManual = false;
            var itopodAttackCadence = Math.Max(.02, c.adventure.attackSpeed);
            ZoneStats targetStats = null;
            MajorUnlockTarget major = null;

            if (bossObjective)
            {
                kind = LoadoutObjectiveKind.FightBoss;
                id = "fight-boss:" + c.bossID + ":record=" + highest;
                display = "selected Fight Boss defeat";
            }
            else if (itopodObjective)
            {
                var route = ZoneHelpers.LastItopodRoute;
                kind = LoadoutObjectiveKind.Itopod;
                targetZone = 1000;
                itopodClimbing = route.Climbing;
                itopodFloor = Math.Max(0, Math.Min(1600,
                    route.Climbing ? route.End - 1 : route.FarmFloor));
                itopodManual = route.Climbing && Main.Settings.ITOPODCombatMode != 1
                                && c.training.attackTraining[0] >= 5000;
                itopodAttackCadence = itopodManual ? .8 : Math.Max(.02, c.adventure.attackSpeed);
                id = "itopod:" + route.Start + ":" + route.End + ":" + route.FarmFloor
                     + ":climb=" + route.Climbing;
                display = route.Climbing
                    ? "ITOPOD first-clear climb to floor " + route.End
                    : "ITOPOD PP/AP/EXP throughput at floor " + route.FarmFloor;
            }
            else if (majorUnlock != null)
            {
                major = CopyMajor(majorUnlock);
                kind = LoadoutObjectiveKind.MajorUnlock;
                id = MajorKey(major);
                display = "major unlock: " + major.Mechanic + " via " + major.Goal;
                targetZone = major.Zone;
                titanIndex = major.TitanIndex;
                titanVersion = major.TitanVersion;
                bossOnly = major.BossOnly;
                valuesLoot = major.ValuesLoot;
                dropChance = Math.Max(0.0, Math.Min(1.0, major.DropChance));
                if (ZoneStatHelper.UserOverrides != null)
                    ZoneStatHelper.UserOverrides.TryGetValue(targetZone, out targetStats);
            }
            else
            {
                var energyFill = c.energyPerSecond() <= 0 ? 0.0
                    : Math.Max(0.0, c.totalCapEnergy() - c.curEnergy) / c.energyPerSecond();
                var magicFill = c.magicPerSecond() <= 0 ? 0.0
                    : Math.Max(0.0, c.totalCapMagic() - c.magic.curMagic) / c.magicPerSecond();
                if (Math.Max(energyFill, magicFill) >= 30.0)
                {
                    kind = LoadoutObjectiveKind.ResourceRefill;
                    id = "routine:resource-refill";
                    display = "resource refill: minimize time to full Energy and Magic";
                }
                else
                {
                    var front = ZoneStatHelper.GetBestZone();
                    int nextZone;
                    ZoneStats nextStats;
                    if (ZoneStatHelper.TryGetNextUnlockedZone(front == null ? -1 : front.Zone,
                        out nextZone, out nextStats))
                    {
                        kind = LoadoutObjectiveKind.AdventureProgression;
                        targetZone = nextZone;
                        targetStats = nextStats;
                        id = "routine:adventure:" + nextZone;
                        display = "Adventure progression toward " + nextStats.Name;
                    }
                    else if (front != null)
                    {
                        targetZone = front.Zone;
                        if (ZoneStatHelper.UserOverrides != null)
                            ZoneStatHelper.UserOverrides.TryGetValue(targetZone, out targetStats);
                        id = "routine:adventure-farm:" + targetZone;
                        display = "continuous Adventure progression in "
                                  + GameNames.Zone(c, targetZone);
                        valuesLoot = true;
                    }
                }
            }

            var enemies = CaptureEnemies(c, targetZone, bossOnly, titanIndex);
            if (enemies.Length > 0) targetEnemy = enemies[0].Type;
            var capacityReady = true;
            var capacityReason = "objective has no physical unique-delivery requirement";
            if (major != null && major.ItemId > 0)
            {
                try
                {
                    var topology = InventoryManager.CaptureOrdinaryTopology(c);
                    var requirement = LootCapacityRequirement.ExactUniqueDelivery(
                        "major-unlock-item-" + major.ItemId, 0, 1, 0);
                    var proof = LootCapacity.ProveOrdinary(topology, requirement);
                    capacityReady = proof.Admitted;
                    capacityReason = proof.Reason;
                }
                catch (Exception error)
                {
                    capacityReady = false;
                    capacityReason = "major-unlock capacity capture failed: " + error.GetType().Name;
                }
            }

            var safeRegen = Math.Max(0.0, c.totalAdvHPRegen()) * 5.0;
            if (c.inventory != null && c.inventory.itemList != null
                && c.inventory.itemList.GRBComplete) safeRegen *= 2.0;
            var epoch = forceNew ? 0L : ++_nextObjectiveEpoch;
            var objective = new OptimizationObjective(id, epoch, kind, display,
                targetZone, targetEnemy, titanIndex, titanVersion, bossOnly, valuesLoot,
                titanIndex >= 0 ? "active-safe-titan" : bossOnly ? "active-boss-only" : "active-safe",
                Math.Max(0.0, c.bossCurHP), Math.Max(0.0, c.adventure.curHP), dropChance);
            var projection = new ProjectionSnapshot(c, itopodFloor, itopodClimbing,
                itopodManual, itopodAttackCadence);
            var bound = new BoundObjective(objective, major, enemies, targetStats,
                c.bossID, highest, Math.Max(0.0, c.curHP), c.bossAttack, c.bossDefense,
                c.bossMaxHP, c.bossRegen, c.regAttackPower(), c.idleAttackPower(),
                c.inventoryController.bonuses[specType.Looting]
                + c.inventoryController.bonuses[specType.Looting2],
                c.inventoryController.cubeLootBonus(),
                Math.Max(0.0, c.adventureController.respawnTime()), safeRegen,
                capacityReady, capacityReason, id, projection);
            if (!forceNew) _boundObjective = bound;
            return bound;
        }

        private static EnemySnapshot[] CaptureEnemies(Character c, int zone,
            bool bossOnly, int titanIndex)
        {
            if (zone < 0 || c.adventureController == null
                || c.adventureController.enemyList == null
                || zone >= c.adventureController.enemyList.Count
                || c.adventureController.enemyList[zone] == null)
                return new EnemySnapshot[0];
            var enemies = c.adventureController.enemyList[zone]
                .Where(x => x != null && (!bossOnly || (titanIndex >= 0
                    ? ZoneHelpers.IsTitanEnemy(zone, x.enemyType)
                    : x.enemyType == enemyType.boss
                      || x.enemyType.ToString().IndexOf("bigBoss",
                          StringComparison.OrdinalIgnoreCase) >= 0))).ToList();
            if (enemies.Count == 0 && !bossOnly)
                enemies = c.adventureController.enemyList[zone].Where(x => x != null).ToList();
            return enemies.Select(x => new EnemySnapshot(x)).ToArray();
        }

        private static MajorUnlockTarget CopyMajor(MajorUnlockTarget target)
        {
            if (target == null) return null;
            return new MajorUnlockTarget
            {
                Mechanic = target.Mechanic, Goal = target.Goal, Reason = target.Reason,
                Zone = target.Zone, ItemId = target.ItemId, FightType = target.FightType,
                BossOnly = target.BossOnly, GuaranteedFirstDrop = target.GuaranteedFirstDrop,
                ValuesLoot = target.ValuesLoot, DropChance = target.DropChance,
                ExpectedDropSeconds = target.ExpectedDropSeconds,
                P90DropSeconds = target.P90DropSeconds,
                EligibleTrialSeconds = target.EligibleTrialSeconds,
                MinimumPower = target.MinimumPower, MinimumToughness = target.MinimumToughness,
                TitanIndex = target.TitanIndex, TitanVersion = target.TitanVersion,
                ConsecutiveFailures = target.ConsecutiveFailures,
                RetryEtaSeconds = target.RetryEtaSeconds
            };
        }

        private static string MajorKey(MajorUnlockTarget target)
        {
            return target == null ? "major:none"
                : "major:" + target.Mechanic + ":zone=" + target.Zone + ":item=" + target.ItemId
                  + ":titan=" + target.TitanIndex + ":version=" + target.TitanVersion;
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
            var now = (double)UnityEngine.Time.realtimeSinceStartup;
            var boundObjective = BindObjective(c, false, true, null, now, true);
            var objective = boundObjective.Objective.DisplayName;
            LastObjective = objective;
            LastObjectiveEpoch = boundObjective.Objective.Epoch;
            var all = c.inventory.GetConvertedEquips().Concat(c.inventory.GetConvertedInventory())
                .Where(x => x != null && x.equipment != null && x.id > 0 && x.equipment.isEquipment())
                .Select(x => x.equipment).Distinct().ToList();
            var current = CurrentPlan(c, true);
            var currentEvaluation = EvaluateTotals(boundObjective,
                SnapshotPlanTotals(c, current, 0.0));
            var best = Optimize(c, all, boundObjective);
            var bestEvaluation = _lastSearchResult == null ? null : _lastSearchResult.Evaluation;
            if (!SameLayout(best, current)
                && bestEvaluation != null && bestEvaluation.Feasible
                && (currentEvaluation == null || !currentEvaluation.Feasible
                    || bestEvaluation.TotalSeconds + 0.02 < currentEvaluation.TotalSeconds))
                ApplyChosenPlan(c, best, now, "Staged");
            else
            {
                _authoritativePlan = current.Clone();
                _authoritativeObjective = objective;
                _authoritativeEpoch = boundObjective.Objective.Epoch;
                _pendingPlan = null;
                _pendingContext = string.Empty;
                _pendingEpoch = -1L;
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
                _authoritativeEpoch = LastObjectiveEpoch;
                _pendingPlan = null;
                _pendingContext = string.Empty;
                _pendingEpoch = -1L;
                _lastFingerprint = int.MinValue;
                _lastRun = 0;
            }
            else
            {
                _failedPlan = best.Clone();
                _failedUntil = now + 30.0;
                _authoritativePlan = null;
                _authoritativeObjective = string.Empty;
                _authoritativeEpoch = -1L;
                _pendingPlan = null;
                _pendingContext = string.Empty;
                _pendingEpoch = -1L;
                _lastFingerprint = int.MinValue;
                _lastRun = 0;
            }
            LastDecision = (confirmed ? action : "Rejected") + " optimized " + LastObjective + " set [" + displaySignature
                           + "]; native item attack " + beforeAttack.ToString("0.##") + " -> "
                           + c.inventoryController.attackBonus().ToString("0.##") + ", defense "
                           + beforeDefense.ToString("0.##") + " -> "
                           + c.inventoryController.defenseBonus().ToString("0.##")
                           + (_lastSearchResult != null && _lastSearchResult.Evaluation != null
                               ? "; setup " + FormatSeconds(_lastSearchResult.Evaluation.SetupSeconds)
                                 + ", recovery " + FormatSeconds(_lastSearchResult.Evaluation.RecoverySeconds)
                                 + ", incumbent " + FormatSeconds(_lastSearchResult.IncumbentSeconds)
                                 + ", lower bound " + FormatSeconds(_lastSearchResult.OptimisticLowerBoundSeconds)
                                 + ", gap " + FormatSeconds(_lastSearchResult.AbsoluteGapSeconds)
                               : string.Empty);
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

        private static Plan Optimize(Character c, List<Equipment> all, BoundObjective objective)
        {
            var current = CurrentPlan(c, true);
            var references = new List<Equipment>();
            long nextReference = 1L;
            Func<Equipment, long> keyFor = delegate(Equipment item)
            {
                if (item == null) return nextReference++;
                for (var i = 0; i < references.Count; i++)
                    if (ReferenceEquals(references[i], item)) return i + 1L;
                references.Add(item);
                return references.Count;
            };

            var heads = FixedCandidates(c, all, current.Head, part.Head,
                LoadoutSlotKind.Head, keyFor);
            var chests = FixedCandidates(c, all, current.Chest, part.Chest,
                LoadoutSlotKind.Chest, keyFor);
            var legs = FixedCandidates(c, all, current.Legs, part.Legs,
                LoadoutSlotKind.Legs, keyFor);
            var boots = FixedCandidates(c, all, current.Boots, part.Boots,
                LoadoutSlotKind.Boots, keyFor);
            var weaponObjects = DistinctReferences(all.Where(x => x != null && x.type == part.Weapon)
                .Concat(new[] {current.Weapon, current.Weapon2})
                .Concat(c.inventory.inventory.Where(x => x != null && x.id <= 0).Take(2)));
            if (weaponObjects.Count == 0) weaponObjects.Add(current.Weapon);
            var primaryWeapons = weaponObjects.Select(x => BuildCandidateWithKey(c, x,
                LoadoutSlotKind.PrimaryWeapon, 1.0, ReferenceEquals(x, current.Weapon),
                keyFor)).ToArray();
            var secondaries = c.inventoryController.weapon2Unlocked()
                ? weaponObjects.Select(x => BuildCandidateWithKey(c, x,
                    LoadoutSlotKind.SecondaryWeapon, c.inventoryController.weapon2Factor(),
                    ReferenceEquals(x, current.Weapon2), keyFor)).ToArray()
                : new LoadoutCandidate[0];

            var spaces = Math.Min(Math.Max(0, c.inventoryController.accessorySpaces()),
                c.inventory.accs.Count);
            var currentAccessories = current.Accessories.ToArray();
            var accessoryObjects = DistinctReferences(all.Where(x => x != null
                    && x.type == part.Accessory)
                .Concat(currentAccessories)
                .Concat(c.inventory.inventory.Where(x => x != null && x.id <= 0).Take(spaces)));
            var accessories = accessoryObjects.Select(x => BuildCandidateWithKey(c, x,
                LoadoutSlotKind.Accessory, 1.0,
                currentAccessories.Any(y => ReferenceEquals(x, y)), keyFor)).ToArray();

            var initial = BuildInitialSelection(current, heads, chests, legs, boots,
                primaryWeapons, secondaries, accessories);
            var problem = new LoadoutSearchProblem(objective.Objective,
                heads, chests, legs, boots, primaryWeapons, secondaries, accessories,
                spaces, SearchNodeBudget,
                delegate(OptimizationObjective fixedObjective, LoadoutSelection selection,
                    LoadoutTotals totals)
                {
                    // All native equip contribution calls happened while the immutable
                    // LoadoutCandidates above were built. Complete search nodes project only
                    // their accumulated numeric vector; they never re-read a candidate object.
                    return EvaluateTotals(objective, totals);
                },
                delegate(OptimizationObjective fixedObjective, LoadoutTotals partial,
                    LoadoutTotals optimistic)
                {
                    return EvaluateLowerBound(objective, partial, optimistic);
                }, initial);
            _lastSearchResult = ParetoLoadoutSolver.Solve(problem);
            _searchExact = _lastSearchResult.IsProvenOptimal;
            return _lastSearchResult.Selection == null
                ? current : PlanFromSelection(c, _lastSearchResult.Selection);
        }

        private static LoadoutCandidate[] FixedCandidates(Character c, IEnumerable<Equipment> all,
            Equipment current, part expected, LoadoutSlotKind slot, Func<Equipment, long> keyFor)
        {
            var objects = DistinctReferences(all.Where(x => x != null && x.type == expected)
                .Concat(new[] {current}));
            if (objects.Count == 0) objects.Add(current);
            return objects.Select(x => BuildCandidateWithKey(c, x, slot, 1.0,
                ReferenceEquals(x, current), keyFor)).ToArray();
        }

        private static List<Equipment> DistinctReferences(IEnumerable<Equipment> source)
        {
            var result = new List<Equipment>();
            foreach (var item in source)
                if (!result.Any(x => ReferenceEquals(x, item))) result.Add(item);
            return result;
        }

        private static LoadoutCandidate BuildCandidate(Character c, Equipment item,
            LoadoutSlotKind slot, double slotFactor, bool currentlyEquipped,
            long referenceKey, long canonicalKey)
        {
            var metrics = CandidateMetrics(c, item, slotFactor);
            var id = item == null ? 0 : Math.Max(0, item.id);
            var tags = item != null && item.id == 135 && item.level >= 100 ? TagApathy : 0L;
            var inventorySlot = item == null || c.inventory == null
                || c.inventory.inventory == null ? -1
                : c.inventory.inventory.FindIndex(x => ReferenceEquals(x, item));
            var obligation = item == null || currentlyEquipped || IsAuthoritativeItem(item)
                             || item != null && !item.removable
                             || inventorySlot >= 0
                             && InventoryManager.IsNativeLoadoutReference(c, inventorySlot);
            return new LoadoutCandidate(referenceKey, canonicalKey, id, slot, metrics,
                currentlyEquipped ? 0.0 : SwapSetupSeconds, tags, obligation, item);
        }

        private static double[] CandidateMetrics(Character c, Equipment item, double slotFactor)
        {
            var metrics = new double[MetricCount];
            if (item == null || item.id <= 0) return metrics;
            var controller = c.inventoryController;
            metrics[MetricAttack] = Math.Max(0.0, controller.equipAttackBonus(item) * slotFactor);
            metrics[MetricDefense] = Math.Max(0.0, controller.equipDefenseBonus(item) * slotFactor);
            metrics[MetricLoot] = Math.Max(0.0,
                (controller.equipSpecBonus(specType.Looting, item)
                 + controller.equipSpecBonus(specType.Looting2, item)) * slotFactor);
            metrics[MetricRespawn] = Math.Max(0.0,
                controller.equipSpecBonus(specType.Respawn, item) * slotFactor);
            metrics[MetricEnergySpeed] = Math.Max(0.0,
                controller.equipSpecBonus(specType.EnergySpeed, item) * slotFactor);
            metrics[MetricMagicSpeed] = Math.Max(0.0,
                controller.equipSpecBonus(specType.MagicSpeed, item) * slotFactor);
            metrics[MetricEnergyBar] = Math.Max(0.0,
                (controller.equipSpecBonus(specType.EnergyPerBar, item)
                 + controller.equipSpecBonus(specType.EnergyPerBar2, item)
                 + controller.equipSpecBonus(specType.EnergyPerBar3, item)
                 + controller.equipSpecBonus(specType.AllPerBar, item)) * slotFactor);
            metrics[MetricMagicBar] = Math.Max(0.0,
                (controller.equipSpecBonus(specType.MagicPerBar, item)
                 + controller.equipSpecBonus(specType.MagicPerBar2, item)
                 + controller.equipSpecBonus(specType.MagicPerBar3, item)
                 + controller.equipSpecBonus(specType.AllPerBar, item)) * slotFactor);
            metrics[MetricGeneral] = Math.Max(0.0,
                SpecialUtility(c, item, slotFactor) + ProductionTrimUtility(c, item));
            return metrics;
        }

        private static LoadoutTotals SnapshotPlanTotals(Character c, Plan plan,
            double setupSeconds)
        {
            var metrics = new double[MetricCount];
            Action<Equipment, double> add = delegate(Equipment item, double factor)
            {
                var itemMetrics = CandidateMetrics(c, item, factor);
                for (var i = 0; i < metrics.Length; i++) metrics[i] += itemMetrics[i];
            };
            add(plan.Head, 1.0);
            add(plan.Chest, 1.0);
            add(plan.Legs, 1.0);
            add(plan.Boots, 1.0);
            add(plan.Weapon, 1.0);
            add(plan.Weapon2, c.inventoryController.weapon2Factor());
            foreach (var accessory in plan.Accessories) add(accessory, 1.0);
            return new LoadoutTotals(metrics, setupSeconds,
                setupSeconds > 0.0 ? 1 : 0, 0L);
        }

        private static LoadoutCandidate BuildCandidateWithKey(Character c, Equipment item,
            LoadoutSlotKind slot, double slotFactor, bool currentlyEquipped,
            Func<Equipment, long> keyFor)
        {
            var key = keyFor(item);
            return BuildCandidate(c, item, slot, slotFactor, currentlyEquipped,
                key, key);
        }

        private static LoadoutSelection BuildInitialSelection(Plan current,
            LoadoutCandidate[] heads, LoadoutCandidate[] chests, LoadoutCandidate[] legs,
            LoadoutCandidate[] boots, LoadoutCandidate[] primaries,
            LoadoutCandidate[] secondaries, LoadoutCandidate[] accessories)
        {
            var selectedAccessories = current.Accessories.Select(x => FindCandidate(accessories, x))
                .Where(x => x != null).OrderBy(x => x.CanonicalKey).ToArray();
            if (selectedAccessories.Length != current.Accessories.Count) return null;
            var head = FindCandidate(heads, current.Head);
            var chest = FindCandidate(chests, current.Chest);
            var legsCandidate = FindCandidate(legs, current.Legs);
            var bootsCandidate = FindCandidate(boots, current.Boots);
            var primary = FindCandidate(primaries, current.Weapon);
            var secondary = secondaries.Length == 0 ? null : FindCandidate(secondaries, current.Weapon2);
            return head == null || chest == null || legsCandidate == null
                   || bootsCandidate == null || primary == null
                   || secondaries.Length > 0 && secondary == null
                ? null : new LoadoutSelection(head, chest, legsCandidate, bootsCandidate,
                    primary, secondary, selectedAccessories);
        }

        private static LoadoutCandidate FindCandidate(IEnumerable<LoadoutCandidate> candidates,
            Equipment item)
        {
            return candidates.FirstOrDefault(x => ReferenceEquals(x.Token, item));
        }

        private static Plan PlanFromSelection(Character c, LoadoutSelection selection)
        {
            var plan = new Plan
            {
                Head = selection.Head == null ? null : selection.Head.Token as Equipment,
                Chest = selection.Chest == null ? null : selection.Chest.Token as Equipment,
                Legs = selection.Legs == null ? null : selection.Legs.Token as Equipment,
                Boots = selection.Boots == null ? null : selection.Boots.Token as Equipment,
                Weapon = selection.PrimaryWeapon == null ? null
                    : selection.PrimaryWeapon.Token as Equipment,
                Weapon2 = selection.SecondaryWeapon == null ? null
                    : selection.SecondaryWeapon.Token as Equipment
            };
            var selected = selection.Accessories().Select(x => x.Token as Equipment).ToList();
            var activeCount = Math.Min(c.inventory.accs.Count,
                Math.Max(0, c.inventoryController.accessorySpaces()));
            var arranged = Enumerable.Repeat<Equipment>(null, activeCount).ToArray();
            for (var i = 0; i < activeCount; i++)
            {
                var current = c.inventory.accs[i];
                var match = selected.FindIndex(x => ReferenceEquals(x, current));
                if (match < 0) continue;
                arranged[i] = selected[match];
                selected.RemoveAt(match);
            }
            var remaining = selected.OrderBy(x => x == null ? 0 : x.id)
                .ThenBy(x => x == null ? 0 : x.level).ToList();
            for (var i = 0; i < arranged.Length; i++)
                if (arranged[i] == null && remaining.Count > 0)
                {
                    arranged[i] = remaining[0];
                    remaining.RemoveAt(0);
                }
            plan.Accessories.AddRange(arranged);
            return plan;
        }

        internal static bool CanSupportMajorUnlock(Character c, MajorUnlockTarget target,
            out string projectionReason)
        {
            projectionReason = "candidate loadout has not been evaluated";
            if (c == null || target == null) return false;
            var savedResult = _lastSearchResult;
            var savedExact = _searchExact;
            try
            {
                var bound = BindObjective(c, false, false, target,
                    UnityEngine.Time.realtimeSinceStartup, true);
                var all = c.inventory.GetConvertedEquips().Concat(c.inventory.GetConvertedInventory())
                    .Where(x => x != null && x.equipment != null && x.id > 0
                                && x.equipment.isEquipment())
                    .Select(x => x.equipment).Distinct().ToList();
                Optimize(c, all, bound);
                var evaluation = _lastSearchResult == null ? null : _lastSearchResult.Evaluation;
                if (evaluation == null || !evaluation.Feasible)
                {
                    projectionReason = evaluation == null
                        ? "no complete exact-reference candidate was found"
                        : evaluation.Reason;
                    return false;
                }
                projectionReason = "owned candidate path: setup "
                                   + FormatSeconds(evaluation.SetupSeconds) + ", recovery "
                                   + FormatSeconds(evaluation.RecoverySeconds) + ", action "
                                   + FormatSeconds(evaluation.ActionSeconds);
                return true;
            }
            catch (Exception error)
            {
                projectionReason = "candidate solve failed closed: " + error.GetType().Name;
                return false;
            }
            finally
            {
                _lastSearchResult = savedResult;
                _searchExact = savedExact;
            }
        }

        private static LoadoutEvaluation EvaluateTotals(BoundObjective objective,
            LoadoutTotals totals)
        {
            if (totals == null || objective == null)
                return LoadoutEvaluation.Infeasible("loadout/objective snapshot is unavailable");
            if (!objective.CapacityReady)
                return LoadoutEvaluation.Infeasible(objective.CapacityReason);
            var projection = ProjectFromMetrics(objective, totals);
            switch (objective.Objective.Kind)
            {
                case LoadoutObjectiveKind.FightBoss:
                    return EvaluateFightBoss(objective, projection, totals.SetupSeconds);
                case LoadoutObjectiveKind.MajorUnlock:
                    return EvaluateAdventureObjective(objective, projection,
                        totals.SetupSeconds, true);
                case LoadoutObjectiveKind.TitanAutokill:
                    return EvaluateTitanAutokill(objective, projection, totals.SetupSeconds);
                case LoadoutObjectiveKind.Itopod:
                    return EvaluateItopod(objective, projection, totals.SetupSeconds);
                case LoadoutObjectiveKind.ResourceRefill:
                    return EvaluateResourceRefill(objective, projection, totals.SetupSeconds);
                case LoadoutObjectiveKind.AdventureProgression:
                    return EvaluateAdventureObjective(objective, projection,
                        totals.SetupSeconds, false);
                default:
                    return EvaluateAdventureObjective(objective, projection,
                        totals.SetupSeconds, false);
            }
        }

        private static LoadoutEvaluation EvaluateFightBoss(BoundObjective objective,
            CandidateProjection projection, double setupSeconds)
        {
            var canNuke = objective.BossId <= 300
                          && (objective.BossId < objective.HighestBoss || objective.BossId >= 124)
                          && projection.FightAttack / 5.0 > objective.BossDefense
                          && projection.FightDefense / 5.0 > objective.BossAttack;
            if (canNuke)
                return new LoadoutEvaluation(true, setupSeconds, setupSeconds, 0.0, 0.0,
                    setupSeconds, -projection.General, "candidate satisfies exact native nuke predicate");
            if (objective.Objective.LiveFightBossHp <= 0.0 || objective.BossMaxHp <= 0.0)
                return LoadoutEvaluation.Infeasible("selected Fight Boss HP snapshot is unavailable");
            var maxHp = Math.Max(0.0, 10.0 + projection.FightAttack * 10.0);
            var recovery = MechanicsFightBoss.EvaluateRecovery(
                projection.FightAttack, projection.FightDefense,
                objective.FightPlayerHp, maxHp,
                objective.BossAttack, objective.BossDefense,
                objective.Objective.LiveFightBossHp, objective.BossMaxHp,
                objective.BossRegen, MechanicsFightBoss.DefaultCombatHorizonTicks,
                MechanicsFightBoss.DefaultRecoveryHorizonTicks);
            FightBossProjection fight;
            double recoverySeconds;
            if (recovery.Immediate.PlayerWins)
            {
                fight = recovery.Immediate;
                recoverySeconds = 0.0;
            }
            else if (recovery.RecoveryWithinHorizon && recovery.AfterRecovery.PlayerWins)
            {
                fight = recovery.AfterRecovery;
                recoverySeconds = recovery.RecoverySeconds;
            }
            else
                return LoadoutEvaluation.Infeasible(recovery.CanWinAtFullHp
                    ? "Fight Boss requires recovery beyond the bounded horizon"
                    : "candidate cannot defeat the selected Fight Boss at full HP");
            var total = setupSeconds + recoverySeconds + fight.KillSeconds;
            return new LoadoutEvaluation(true, total, setupSeconds, recoverySeconds,
                fight.KillSeconds, total, -projection.General,
                "exact source-order Fight Boss projection with conservative current HP");
        }

        private static LoadoutEvaluation EvaluateAdventureObjective(BoundObjective objective,
            CandidateProjection projection, double setupSeconds,
            bool major)
        {
            if (major && objective.Major != null && objective.Major.TitanIndex >= 5)
            {
                var ak = TitanMechanics.EvaluateNativeAutokill(
                    objective.Major.TitanIndex + 1, objective.Major.TitanVersion,
                    projection.AdventureAttack, projection.AdventureDefense,
                    projection.AdventureHpRegen, 0);
                if (ak.Achieved)
                {
                    // Native autokill is global and bypasses survival/recovery. Exact capacity was
                    // already fixed in the bound objective and execution still confirms natively.
                    return new LoadoutEvaluation(true, setupSeconds, setupSeconds, 0.0, 0.0,
                        setupSeconds, -projection.General, "pure candidate Titan autokill: " + ak.Reason);
                }
                return LoadoutEvaluation.Infeasible(ak.Reason
                    + "; versioned manual bespoke-AI execution is not proven by this solver");
            }
            if (objective.Enemies.Length == 0)
            {
                if (objective.TargetStats == null)
                    return LoadoutEvaluation.Infeasible("target enemy/stat snapshot is unavailable");
                var bottleneck = Math.Min(
                    projection.AdventureAttack / Math.Max(1.0, objective.TargetStats.MPower),
                    projection.AdventureDefense / Math.Max(1.0, objective.TargetStats.MToughness));
                if (bottleneck <= 0.0)
                    return LoadoutEvaluation.Infeasible("candidate has no positive target-zone stat path");
                var seconds = 1000000.0 / Math.Min(1.0, bottleneck);
                return new LoadoutEvaluation(true, setupSeconds + seconds, setupSeconds,
                    0.0, seconds, setupSeconds + seconds, -projection.General,
                    "source enemy records unavailable; conservative zone-threshold seconds proxy");
            }

            var worstKill = 0.0;
            var worstRecovery = 0.0;
            foreach (var enemy in objective.Enemies)
            {
                var outgoing = .8 * Math.Max(0.0,
                    projection.AdventureAttack - enemy.Defense / 2.0)
                               * objective.RegularAttackPower;
                // Enemy regen occurs continuously. A one-second manual cadence is conservative
                // relative to the global move lock and never treats HP/outgoing as a DPS quotient.
                var progressPerAttack = outgoing - Math.Max(0.0, enemy.Regen);
                if (progressPerAttack <= 0.0)
                    return LoadoutEvaluation.Infeasible("candidate cannot overcome target enemy defense/regen");
                var attacks = Math.Ceiling(enemy.MaxHp / progressPerAttack);
                var killSeconds = Math.Max(1.0, attacks);
                if (killSeconds > 120.0)
                    return LoadoutEvaluation.Infeasible("candidate target fight exceeds the 120-second bound");
                var firstAttack = enemy.AttackRate; // fail-closed for bespoke early-Titan AIs
                var enemyAttacks = killSeconds < firstAttack ? 0.0
                    : 1.0 + Math.Floor((killSeconds - firstAttack) / enemy.AttackRate);
                var incoming = 1.2 * Math.Max(enemy.Attack * .1,
                    enemy.Attack - projection.AdventureDefense / 2.0);
                var projectedDamage = enemyAttacks * incoming;
                var requiredStartHp = projectedDamage <= 0.0 ? 0.0
                    : Math.Min(double.MaxValue, projectedDamage / .95 + 1e-9);
                var health = LoadoutHealth.Project(objective.Objective.LiveAdventureHp,
                    projection.AdventureMaxHp, requiredStartHp,
                    objective.SafeRecoveryHpPerSecond);
                if (!health.Recoverable)
                    return LoadoutEvaluation.Infeasible("candidate target fight cannot survive even after Safe-Zone recovery");
                worstKill = Math.Max(worstKill, killSeconds);
                worstRecovery = Math.Max(worstRecovery, health.RecoverySeconds);
            }

            var respawn = objective.LiveRespawnSeconds
                          * Math.Max(.2, 1.0 - projection.RespawnBonus)
                          / Math.Max(.2, 1.0 - objective.Projection.CurrentRespawnGear);
            var trialSeconds = worstRecovery + worstKill + Math.Max(0.0, respawn);
            var meanSeconds = trialSeconds;
            var p90Seconds = trialSeconds;
            if (major && objective.Objective.DropChance > 0.0)
            {
                var lootRatio = (1.0 + projection.LootBonus + objective.CubeLootBonus)
                                / Math.Max(1e-9,
                                    1.0 + objective.LiveLootBonus + objective.CubeLootBonus);
                var probability = Math.Min(1.0,
                    objective.Objective.DropChance * Math.Max(0.0, lootRatio));
                if (probability <= 0.0)
                    return LoadoutEvaluation.Infeasible("candidate has zero major-unlock drop probability");
                meanSeconds = MechanicsStochastic.GeometricMeanSeconds(probability, trialSeconds);
                var p90Trials = MechanicsStochastic.GeometricQuantileTrials(probability, .90);
                p90Seconds = p90Trials == long.MaxValue ? double.PositiveInfinity
                    : p90Trials * trialSeconds;
            }
            else if (!major && objective.Objective.ValuesLoot)
            {
                var lootRatio = (1.0 + projection.LootBonus + objective.CubeLootBonus)
                                / Math.Max(1e-9,
                                    1.0 + objective.LiveLootBonus + objective.CubeLootBonus);
                meanSeconds = trialSeconds / Math.Max(1e-9, lootRatio);
                p90Seconds = meanSeconds;
            }
            var total = setupSeconds + meanSeconds;
            return new LoadoutEvaluation(true, total, setupSeconds, worstRecovery,
                meanSeconds, setupSeconds + p90Seconds, -projection.General,
                "candidate-aware combat/drop route with explicit setup and recovery");
        }

        private static LoadoutEvaluation EvaluateTitanAutokill(BoundObjective objective,
            CandidateProjection projection, double setupSeconds)
        {
            if (objective.Objective.TitanIndex < 5 || objective.Objective.TitanIndex > 11)
                return LoadoutEvaluation.Infeasible("pure candidate AK is defined for T6-T12");
            var ak = TitanMechanics.EvaluateNativeAutokill(
                objective.Objective.TitanIndex + 1, objective.Objective.TitanVersion,
                projection.AdventureAttack, projection.AdventureDefense,
                projection.AdventureHpRegen, 0);
            return ak.Achieved
                ? new LoadoutEvaluation(true, setupSeconds, setupSeconds, 0.0, 0.0,
                    setupSeconds, -projection.General, ak.Reason)
                : LoadoutEvaluation.Infeasible(ak.Reason);
        }

        private static LoadoutEvaluation EvaluateItopod(BoundObjective objective,
            CandidateProjection projection, double setupSeconds)
        {
            var scale = Math.Pow(1.05, objective.Projection.ItopodFloor);
            var hp = 600.0 * scale * 1.02;
            var defense = 10.0 * scale * 1.02;
            var damage = .8 * Math.Max(0.0,
                projection.AdventureAttack - defense / 2.0)
                         * (objective.Projection.ItopodManual
                             ? objective.RegularAttackPower : objective.IdleAttackPower);
            if (damage < hp)
                return LoadoutEvaluation.Infeasible("candidate does not retain the guaranteed ITOPOD one-hit plateau");
            var respawn = objective.LiveRespawnSeconds
                          * Math.Max(.2, 1.0 - projection.RespawnBonus)
                          / Math.Max(.2, 1.0 - objective.Projection.CurrentRespawnGear);
            var action = objective.Projection.ItopodAttackCadence
                         + Math.Max(0.0, respawn);
            return new LoadoutEvaluation(true, setupSeconds + action, setupSeconds, 0.0,
                action, setupSeconds + action, -projection.General,
                "ITOPOD one-hit cycle seconds including native respawn special");
        }

        private static LoadoutEvaluation EvaluateResourceRefill(BoundObjective objective,
            CandidateProjection projection, double setupSeconds)
        {
            var energySpeed = Math.Max(1.0,
                Math.Min(50.0, objective.Projection.EnergySpeed
                    * (1.0 + projection.EnergySpeedBonus)));
            var magicSpeed = Math.Max(1.0,
                Math.Min(50.0, objective.Projection.MagicSpeed
                    * (1.0 + projection.MagicSpeedBonus)));
            var energyBar = Math.Max(1L, (long)Math.Floor(objective.Projection.EnergyBar
                * (1.0 + projection.EnergyBarBonus)
                / Math.Max(1e-9, 1.0 + objective.Projection.CurrentEnergyBarGear)));
            var magicBar = Math.Max(1L, (long)Math.Floor(objective.Projection.MagicBar
                * (1.0 + projection.MagicBarBonus)
                / Math.Max(1e-9, 1.0 + objective.Projection.CurrentMagicBarGear)));
            var energyRate = DiscreteResourceRate(energySpeed, energyBar);
            var magicRate = DiscreteResourceRate(magicSpeed, magicBar);
            var energySeconds = objective.Projection.EnergyRemaining
                                / Math.Max(1e-9, energyRate);
            var magicSeconds = objective.Projection.MagicRemaining
                               / Math.Max(1e-9, magicRate);
            var action = Math.Max(energySeconds, magicSeconds);
            return new LoadoutEvaluation(true, setupSeconds + action, setupSeconds, 0.0,
                action, setupSeconds + action, -projection.General,
                "exact discrete Energy/Magic refill seconds");
        }

        private static double EvaluateLowerBound(BoundObjective objective,
            LoadoutTotals partial, LoadoutTotals optimistic)
        {
            // Setup already paid by the partial node cannot be removed by any completion. Future
            // setup is optimistically zero. The domain-specific additions below use independently
            // best metric suffixes, so they remain optimistic even when that vector is impossible.
            var lower = partial.SetupSeconds;
            if (objective.Objective.Kind == LoadoutObjectiveKind.FightBoss)
            {
                var projection = ProjectFromMetrics(objective, optimistic);
                var canNuke = objective.BossId <= 300
                              && (objective.BossId < objective.HighestBoss || objective.BossId >= 124)
                              && projection.FightAttack / 5.0 > objective.BossDefense
                              && projection.FightDefense / 5.0 > objective.BossAttack;
                if (canNuke) return lower;
                var maxHp = Math.Max(0.0, 10.0 + projection.FightAttack * 10.0);
                var fight = MechanicsFightBoss.Evaluate(projection.FightAttack,
                    projection.FightDefense, maxHp, maxHp,
                    objective.BossAttack, objective.BossDefense,
                    objective.Objective.LiveFightBossHp, objective.BossMaxHp,
                    objective.BossRegen, MechanicsFightBoss.DefaultCombatHorizonTicks);
                return fight.PlayerWins ? lower + fight.KillSeconds : double.PositiveInfinity;
            }
            if (objective.Objective.Kind == LoadoutObjectiveKind.ResourceRefill)
            {
                var projection = ProjectFromMetrics(objective, optimistic);
                return EvaluateResourceRefill(objective, projection, lower).TotalSeconds;
            }
            return lower;
        }

        private static CandidateProjection ProjectFromMetrics(BoundObjective objective,
            LoadoutTotals totals)
        {
            var snapshot = objective.Projection;
            var attackItems = totals.Metric(MetricAttack);
            var defenseItems = totals.Metric(MetricDefense);
            var currentAttackCore = 100.0 + snapshot.AttackBase * snapshot.AttackCommon
                * (1.0 + snapshot.CurrentAttackItems / 100.0);
            var currentDefenseCore = 100.0 + snapshot.DefenseBase * snapshot.DefenseCommon
                * (1.0 + snapshot.CurrentDefenseItems / 100.0);
            var advAttackNumerator = Math.Max(1e-9, snapshot.AdventureAttackBase
                + snapshot.CubePower + snapshot.CurrentAttackItems);
            var advDefenseNumerator = Math.Max(1e-9, snapshot.AdventureDefenseBase
                + snapshot.CubeToughness + snapshot.CurrentDefenseItems);
            var hpNumerator = Math.Max(1e-9, snapshot.AdventureMaxHpBase
                + 3.0 * (snapshot.CubePower + snapshot.CurrentAttackItems));
            var maxHp = Math.Max(0.0, snapshot.AdventureMaxHp
                * (snapshot.AdventureMaxHpBase + 3.0 * (snapshot.CubePower + attackItems))
                / hpNumerator);
            return new CandidateProjection
            {
                FightAttack = Math.Max(0.0, snapshot.FightAttack
                    * (100.0 + snapshot.AttackBase * snapshot.AttackCommon
                       * (1.0 + attackItems / 100.0))
                    / Math.Max(1e-9, currentAttackCore)),
                FightDefense = Math.Max(0.0, snapshot.FightDefense
                    * (100.0 + snapshot.DefenseBase * snapshot.DefenseCommon
                       * (1.0 + defenseItems / 100.0))
                    / Math.Max(1e-9, currentDefenseCore)),
                AdventureAttack = Math.Max(0.0, snapshot.AdventureAttack
                    * (snapshot.AdventureAttackBase + snapshot.CubePower + attackItems)
                    / advAttackNumerator),
                AdventureDefense = Math.Max(0.0, snapshot.AdventureDefense
                    * (snapshot.AdventureDefenseBase + snapshot.CubeToughness + defenseItems)
                    / advDefenseNumerator),
                AdventureMaxHp = maxHp,
                AdventureCurrentHp = Math.Min(objective.Objective.LiveAdventureHp, maxHp),
                AdventureHpRegen = snapshot.AdventureHpRegen,
                LootBonus = totals.Metric(MetricLoot),
                RespawnBonus = totals.Metric(MetricRespawn),
                EnergySpeedBonus = totals.Metric(MetricEnergySpeed),
                MagicSpeedBonus = totals.Metric(MetricMagicSpeed),
                EnergyBarBonus = totals.Metric(MetricEnergyBar),
                MagicBarBonus = totals.Metric(MetricMagicBar),
                General = totals.Metric(MetricGeneral)
            };
        }

        private static string FormatSeconds(double seconds)
        {
            return double.IsNaN(seconds) ? "unavailable"
                : double.IsPositiveInfinity(seconds) ? "infinite"
                : Math.Max(0.0, seconds).ToString("0.###") + "s";
        }

        private static bool UsesReference(Plan p, Equipment item)
        {
            return ReferenceEquals(p.Head, item) || ReferenceEquals(p.Chest, item)
                   || ReferenceEquals(p.Legs, item) || ReferenceEquals(p.Boots, item)
                   || ReferenceEquals(p.Weapon, item) || ReferenceEquals(p.Weapon2, item)
                   || p.Accessories.Any(x => ReferenceEquals(x, item));
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
            // This legacy marginal utility is used only for boost-development ranking. It consumes
            // the already-bound objective and never calls a live selector per candidate.
            var bossObjective = _boundObjective != null
                                && _boundObjective.Objective.Kind == LoadoutObjectiveKind.FightBoss;
            var itopodObjective = _boundObjective != null
                                  && _boundObjective.Objective.Kind == LoadoutObjectiveKind.Itopod;
            var majorUnlock = bossObjective ? null
                : _boundObjective != null ? _boundObjective.Major : _scoreMajorUnlock;
            var score = bossObjective
                ? 7.0 * Math.Log(1.0 + Math.Max(0.0, projectedAttack))
                  + 4.0 * Math.Log(1.0 + Math.Max(0.0, projectedDefense))
                : 0.0;

            if (bossObjective && c.bossMaxHP > 0)
            {
                FightBossProjection fightProjection;
                var viable = CombatHelpers.EvaluateFixedBossFight(c, projectedAttack,
                    projectedDefense, c.bossMaxHP, out fightProjection);
                var kill = fightProjection == null
                    ? double.PositiveInfinity : fightProjection.KillSeconds;
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
            // A higher candidate maximum is capacity, not healing. This legacy marginal
            // scorer is deliberately fail-closed on the health that exists at the snapshot.
            var candidateAdvCurrentHP = Math.Min(Math.Max(0.0, c.adventure.curHP),
                Math.Max(0.0, candidateAdvHP));
            if (!bossObjective && itopodObjective)
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
                        candidateAdvAttack, candidateAdvDefense, candidateAdvCurrentHP);
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
                    // This marginal boost scorer consumes the coordinator's already-bound
                    // target. It must not reselect a live Adventure front for each candidate.
                    var nextStats = _boundObjective == null ? null : _boundObjective.TargetStats;
                    if (nextStats != null)
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
            if (majorUnlock == null && !itopodObjective)
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
            // only when the Pareto solver's best complete candidate passes the exact
            // current-HP fight model. Cache briefly because the objective coordinator
            // probes this proof on its fast cadence.
            var now = (double)UnityEngine.Time.realtimeSinceStartup;
            if (_cachedBossId == c.bossID && _cachedHighestBoss == highest
                && now - _cachedBossObjectiveAt < 1.0)
                return _cachedBossObjective;

            var previousResult = _lastSearchResult;
            var previousExact = _searchExact;
            try
            {
                _probingBossLoadout = true;
                var fixedBoss = BindObjective(c, true, false, null, now, true);
                var all = c.inventory.GetConvertedEquips().Concat(c.inventory.GetConvertedInventory())
                    .Where(x => x != null && x.equipment != null && x.id > 0
                                && x.equipment.isEquipment())
                    .Select(x => x.equipment).Distinct().ToList();
                Optimize(c, all, fixedBoss);
                var evaluation = _lastSearchResult == null ? null : _lastSearchResult.Evaluation;
                _cachedBossObjective = evaluation != null && evaluation.Feasible
                                       && evaluation.ActionSeconds <= 120.0;
            }
            catch
            {
                _cachedBossObjective = false;
            }
            finally
            {
                _probingBossLoadout = false;
                _lastSearchResult = previousResult;
                _searchExact = previousExact;
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
            FightBossProjection projection;
            var viable = CombatHelpers.EvaluateFixedBossFight(c, projectedAttack,
                projectedDefense, c.bossMaxHP, out projection);
            killSeconds = projection == null ? double.PositiveInfinity : projection.KillSeconds;
            return viable;
        }

        /*
        GOAL-SPECIFIC LOADOUT VALUE

        A major unlock is not scored like routine highest-zone farming. Evaluate the target zone's
        actual boss attack, defense, HP, attack cadence, active-attack power, and candidate current
        HP. This replaces the coarse Toughness threshold as the loadout objective. An RNG-gated
        mechanic additionally values the native aggregate loot multiplier only after combat works.
        Guaranteed drops (the first Pissed Off Key) deliberately assign no Drop Chance value.
        */
        private static double MajorUnlockUtility(Character c, Plan plan, MajorUnlockTarget target,
            double candidateAdvAttack, double candidateAdvDefense, double candidateCurrentHP)
        {
            var score = TargetCombatUtility(c, target, candidateAdvAttack,
                candidateAdvDefense, candidateCurrentHP);
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
            double attack, double defense, double currentHP)
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
                var survivalMargin = currentHP / Math.Max(1.0, projectedDamage);
                var viable = killSeconds <= 120.0 && projectedDamage < currentHP * .95;
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
            var fixedObjective = _boundObjective;
            if (fixedObjective == null
                || fixedObjective.Objective.Kind != LoadoutObjectiveKind.Itopod)
                return -1000000000.0;
            var floor = fixedObjective.Projection.ItopodFloor;
            var scale = Math.Pow(1.05, floor);
            // Native spawn independently rolls HP/Defense in [0.98,1.02] and the
            // player hit in [0.8,1.2]. Optimize the guaranteed one-hit plateau so
            // poison/charger/paralyze/rapid/grower AI never receives an action.
            var enemyHP = 600.0 * scale * 1.02;
            var enemyDefense = 10.0 * scale * 1.02;
            var attackPower = fixedObjective.Projection.ItopodManual
                ? fixedObjective.RegularAttackPower : fixedObjective.IdleAttackPower;
            var damage = 0.8 * Math.Max(0.0, attack - enemyDefense / 2.0) * attackPower;
            if (damage <= 0.0) return -1000000000.0;
            var hits = Math.Max(1.0, Math.Ceiling(enemyHP / damage));
            var attackInterval = fixedObjective.Projection.ItopodAttackCadence;
            var killSeconds = hits * attackInterval;
            if (hits > 1.0)
                return -500000000.0 - 1000000.0 * hits;

            var controller = c.inventoryController;
            var candidateRespawnGear = Math.Max(0.0, PlanBonus(controller, plan, specType.Respawn));
            var currentRespawnFactor = Math.Max(0.2,
                1.0 - fixedObjective.Projection.CurrentRespawnGear);
            var candidateRespawnFactor = Math.Max(0.2, 1.0 - candidateRespawnGear);
            var respawn = fixedObjective.LiveRespawnSeconds
                          * candidateRespawnFactor / Math.Max(1e-9, currentRespawnFactor);
            var cycle = Math.Max(0.02, killSeconds + Math.Max(0.0, respawn));
            var progress = (c.settings.rebirthDifficulty == difficulty.normal ? 200.0
                : c.settings.rebirthDifficulty == difficulty.evil ? 700.0 : 2000.0) + floor;
            // First-clear climbing is a discrete permanent award, so reaching the requested floor
            // dominates small farm-rate differences.  Farming maximizes exact cycle throughput.
            // First-clear routing is justified by a one-shot proof. Preserve that
            // constraint lexicographically; respawn bonuses may optimize only among
            // candidate plans that retain it.
            var viableClimb = !fixedObjective.Projection.ItopodClimbing || damage >= enemyHP;
            if (!viableClimb)
                return -1000000000.0 + 1000.0 / hits;
            return (fixedObjective.Projection.ItopodClimbing ? 200000000.0 : 0.0)
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
