using System;
using System.Collections.Generic;

/*
FILE PURPOSE

Purpose: ItopodPerkPlanner is the pure mechanics and policy authority for continuous ITOPOD
climbing, online/offline reward forecasts, the T6 clue-four naked session, typed perk choices, and
asynchronous perk-231 item delivery.  It complements MechanicsItopod's installed-build PP formulas
without reading or changing a live Character.

Mechanism: A climb plan uses the native-valid H-1 start and a target no higher than a bounded
finite-clear/survival frontier plus one; steady farming still uses the separate guaranteed one-hit
ceiling.  The online transition simulator preserves native enemy-death ordering:
ordinary PP is calculated from the fought floor; the ten-kill move, wrap, and record award happen
next; AP, EXP, boosts, clues, and special-drop probabilities use the post-move floor.  A de-duplicated
post-kill gate requests a synchronous exit before an unproved sentinel can respawn.  Offline
forecasting is intentionally separate and implements session-local integer divisions with none of
the online special-drop paths.

Inputs and outputs: Inputs are immutable range/economy/counter snapshots, exact ordinary-inventory
topology, perk table rows, and checker state.  Outputs are range plans, one-kill transitions,
online/offline forecasts, typed perk plans, capacity requirements/proofs, and pending-grant status.
The file exposes integration hooks but invokes no native method and emits no live telemetry.

Invariants and safety: Floors are bounded to 0..1600; record awards occur only after the live floor
moves; direct ITOPOD AP is never modified by the general AP multiplier; any online boost is 8.4%
(2.8% per family), not 14%; offline forecasts promise no clue/Exile/END/random-boost progress.  A
clue session is permanently invalid after any equipped slot is seen.  Perk IDs use 0 <= id < Count,
unknown effect classes are never spend-authorized, and item 482 keeps one exact ordinary slot
reserved from source purchase until physical delivery.

Extension points and non-goals: Live Adventure hooks may translate ItopodRangePlan and
PostKillDirective into build-pinned native range/reset intents; the integration owner must supply
those bindings.  Perk scoring may add more typed effect models.  This file does not equip gear,
purchase perks, poll once per second for a sentinel, simulate manual-combat cooldowns, or treat
daycare/equipment ownership as ordinary inventory ownership.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class ItopodFloorCombatProof
    {
        internal readonly int Floor;
        internal readonly bool OneHit;
        internal readonly bool FrontierClear;
        internal readonly int Hits;
        internal readonly double KillSeconds;
        internal readonly double WorstIncomingDamage;
        internal readonly string Reason;

        internal ItopodFloorCombatProof(int floor, bool oneHit, bool frontierClear,
            int hits, double killSeconds, double worstIncomingDamage, string reason)
        {
            Floor = floor;
            OneHit = oneHit;
            FrontierClear = frontierClear;
            Hits = hits;
            KillSeconds = killSeconds;
            WorstIncomingDamage = worstIncomingDamage;
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class ItopodCombatReachProof
    {
        internal readonly int OneHitFloor;
        internal readonly int FrontierFloor;
        internal readonly double FrontierKillSeconds;
        internal readonly string FrontierReason;

        internal ItopodCombatReachProof(int oneHitFloor, int frontierFloor,
            double frontierKillSeconds, string frontierReason)
        {
            OneHitFloor = oneHitFloor;
            FrontierFloor = frontierFloor;
            FrontierKillSeconds = frontierKillSeconds;
            FrontierReason = frontierReason ?? string.Empty;
        }
    }

    /*
    BOUNDED ITOPOD FRONTIER PROOF

    Native ITOPOD enemies scale their 10/10/1/600 base stats by 1.05^floor and independently roll
    each stat through [0.98,1.02].  The old router required one minimum-roll player hit to exceed
    worst-roll HP.  That is the right farm-throughput plateau but it stranded record climbing even
    when two or three hits were trivially safe.  This pure proof keeps that one-hit value intact and
    adds a deliberately short multi-hit window.  It proves positive damage after continuous enemy
    regeneration, completes before the third native enemy action (where paralyze/charger/rapid
    escalation begins), and survives worst-roll normal damage, grower's second-action 1.2x hit,
    and poison's defense-bypassing direct Adventure-HP rider without using player regeneration.
    Target Beast Mode's native 3x PlayerController multiplier applies to ordinary/grower hits but
    not that direct poison subtraction. A ten-percent HP reserve covers frame-order uncertainty.
    */
    internal static class ItopodCombatOracle
    {
        internal const double FrontierKillHorizonSeconds = 4.1;
        private const double EnemyFirstActionSeconds = 1.8;
        private const double EnemyActionSeconds = 1.2;
        private const double SurvivalFraction = .9;

        internal static ItopodFloorCombatProof EvaluateFloor(int floor,
            double adventureAttack, double adventureDefense, double availableHp,
            double attackPower, double attackCadence, double incomingBeastFactor)
        {
            if (floor < 0 || floor > ItopodPerkPlanner.MaximumFloor)
                throw new ArgumentOutOfRangeException("floor");
            RequireFiniteNonNegative(adventureAttack, "adventureAttack");
            RequireFiniteNonNegative(adventureDefense, "adventureDefense");
            RequireFiniteNonNegative(availableHp, "availableHp");
            RequireFiniteNonNegative(attackPower, "attackPower");
            if (double.IsNaN(attackCadence) || double.IsInfinity(attackCadence)
                || attackCadence <= 0.0)
                throw new ArgumentOutOfRangeException("attackCadence");
            if (double.IsNaN(incomingBeastFactor) || double.IsInfinity(incomingBeastFactor)
                || incomingBeastFactor < 1.0)
                throw new ArgumentOutOfRangeException("incomingBeastFactor");

            var scale = Math.Pow(1.05, floor);
            var enemyAttack = 10.0 * scale * 1.02;
            var enemyDefense = 10.0 * scale * 1.02;
            var enemyRegen = 1.0 * scale * 1.02;
            var enemyHp = 600.0 * scale * 1.02;
            var minimumHit = .8 * Math.Max(0.0,
                adventureAttack - enemyDefense / 2.0) * attackPower;
            if (minimumHit <= 0.0)
                return Rejected(floor, "minimum-roll player damage is zero");

            var oneHit = minimumHit >= enemyHp;
            var hits = 1;
            if (!oneHit)
            {
                // Between player actions the native enemy regenerates continuously. The first hit
                // occurs against full HP, then every later hit must overcome one cadence of regen.
                var netLaterHit = minimumHit - enemyRegen * attackCadence;
                if (netLaterHit <= 0.0)
                    return Rejected(floor, "enemy regeneration meets or exceeds later minimum-roll hits");
                hits += (int)Math.Ceiling(Math.Max(0.0, enemyHp - minimumHit) / netLaterHit);
            }
            var killSeconds = hits * attackCadence;
            if (killSeconds > FrontierKillHorizonSeconds)
                return new ItopodFloorCombatProof(floor, oneHit, false, hits,
                    killSeconds, 0.0, "finite clear exceeds the pre-escalation 4.1s horizon");

            var enemyActions = killSeconds < EnemyFirstActionSeconds ? 0
                : 1 + (int)Math.Floor((killSeconds - EnemyFirstActionSeconds)
                                      / EnemyActionSeconds);
            var baseWorstHit = 1.2 * Math.Max(enemyAttack * .1,
                enemyAttack - adventureDefense / 2.0);
            var worstIncoming = 0.0;
            for (var action = 1; action <= enemyActions; action++)
            {
                // Grower is the strongest ITOPOD base-AI multiplier before 4.1 seconds: its
                // second action is 1.2x. Poison may also add 20% raw Attack at a 1.2 roll.
                var aiFactor = action >= 2 ? 1.2 : 1.0;
                worstIncoming += baseWorstHit * aiFactor * incomingBeastFactor;
                worstIncoming += enemyAttack * .2 * 1.2;
            }
            var survives = availableHp > 0.0
                           && worstIncoming <= availableHp * SurvivalFraction;
            return new ItopodFloorCombatProof(floor, oneHit, survives, hits,
                killSeconds, worstIncoming, survives
                    ? oneHit ? "guaranteed one-hit farm plateau and bounded survivable frontier"
                      : "bounded multi-hit clear before special-AI escalation with 10% HP reserve"
                    : "worst pre-escalation incoming damage exceeds the 90% HP budget");
        }

        internal static ItopodCombatReachProof ProveReach(double adventureAttack,
            double adventureDefense, double availableHp, double attackPower,
            double attackCadence, double incomingBeastFactor, int maximumFloor)
        {
            if (maximumFloor < 0 || maximumFloor > ItopodPerkPlanner.MaximumFloor)
                throw new ArgumentOutOfRangeException("maximumFloor");
            var oneHitFloor = 0;
            var frontierFloor = 0;
            var frontierSeconds = attackCadence;
            var frontierReason = "floor zero has not been evaluated";
            var frontierOpen = true;
            for (var floor = 0; floor <= maximumFloor; floor++)
            {
                var proof = EvaluateFloor(floor, adventureAttack, adventureDefense,
                    availableHp, attackPower, attackCadence, incomingBeastFactor);
                if (proof.OneHit) oneHitFloor = floor;
                if (frontierOpen && !proof.FrontierClear)
                {
                    frontierReason = proof.Reason;
                    frontierOpen = false;
                }
                if (frontierOpen)
                {
                    frontierFloor = floor;
                    frontierSeconds = proof.KillSeconds;
                    frontierReason = proof.Reason;
                }
                if (!frontierOpen && !proof.OneHit) break;
            }
            return new ItopodCombatReachProof(oneHitFloor, frontierFloor,
                frontierSeconds, frontierReason);
        }

        private static ItopodFloorCombatProof Rejected(int floor, string reason)
        {
            return new ItopodFloorCombatProof(floor, false, false, 0,
                double.PositiveInfinity, 0.0, reason);
        }

        private static void RequireFiniteNonNegative(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                throw new ArgumentOutOfRangeException(name);
        }
    }

    internal enum ItopodObjective
    {
        ContinuousClimb,
        FixedFarm,
        ClueFour,
        EndDrop
    }

    internal sealed class ItopodRangePlan
    {
        internal readonly ItopodObjective Objective;
        internal readonly int StartingRecord;
        internal readonly int TargetRecord;
        internal readonly int ProvenFoughtFloor;
        internal readonly int Start;
        internal readonly int End;
        internal readonly bool Climbing;
        internal readonly bool RequiresPostKillHook;
        internal readonly long FreshEntryKillsToTarget;
        internal readonly string Reason;

        internal ItopodRangePlan(ItopodObjective objective, int startingRecord, int targetRecord,
            int provenFoughtFloor, int start, int end, bool climbing,
            bool requiresPostKillHook, long freshEntryKillsToTarget, string reason)
        {
            Objective = objective;
            StartingRecord = startingRecord;
            TargetRecord = targetRecord;
            ProvenFoughtFloor = provenFoughtFloor;
            Start = start;
            End = end;
            Climbing = climbing;
            RequiresPostKillHook = requiresPostKillHook;
            FreshEntryKillsToTarget = freshEntryKillsToTarget;
            Reason = reason ?? string.Empty;
        }
    }

    internal enum PostKillDirectiveKind
    {
        Continue,
        ExitSynchronouslyAndReplan,
        DuplicateObservation,
        InvalidObservation
    }

    internal sealed class PostKillDirective
    {
        internal readonly PostKillDirectiveKind Kind;
        internal readonly bool BeforeNextRespawn;
        internal readonly string Reason;

        internal PostKillDirective(PostKillDirectiveKind kind, bool beforeNextRespawn, string reason)
        {
            Kind = kind;
            BeforeNextRespawn = beforeNextRespawn;
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class ItopodEconomy
    {
        internal readonly ItopodDifficulty Difficulty;
        internal readonly double TotalPpBonus;
        internal readonly long ImprovedBasePp;
        internal readonly bool Perk30Owned;
        internal readonly double RandomPoopChancePerKill;
        internal readonly bool MacguffinsEnabled;
        internal readonly int MacguffinDivisor;

        internal ItopodEconomy(ItopodDifficulty difficulty, double totalPpBonus,
            long improvedBasePp, bool perk30Owned, double randomPoopChancePerKill,
            bool macguffinsEnabled, int macguffinDivisor)
        {
            if (double.IsNaN(totalPpBonus) || double.IsInfinity(totalPpBonus)
                || totalPpBonus < 0.0) throw new ArgumentOutOfRangeException("totalPpBonus");
            if (improvedBasePp < 0L) throw new ArgumentOutOfRangeException("improvedBasePp");
            if (double.IsNaN(randomPoopChancePerKill) || randomPoopChancePerKill < 0.0
                || randomPoopChancePerKill > 1.0)
                throw new ArgumentOutOfRangeException("randomPoopChancePerKill");
            if (macguffinsEnabled && macguffinDivisor <= 0)
                throw new ArgumentOutOfRangeException("macguffinDivisor");
            Difficulty = difficulty;
            TotalPpBonus = totalPpBonus;
            ImprovedBasePp = improvedBasePp;
            Perk30Owned = perk30Owned;
            RandomPoopChancePerKill = randomPoopChancePerKill;
            MacguffinsEnabled = macguffinsEnabled;
            MacguffinDivisor = macguffinsEnabled ? macguffinDivisor : 0;
        }

        internal static int MacguffinCadence(bool perk69, bool perk70, bool perk71,
            bool purpleHeartComplete)
        {
            var cadence = 5000;
            if (perk69) cadence = cadence * 4 / 5;
            if (perk70) cadence = cadence * 3 / 4;
            if (perk71) cadence = cadence * 3 / 4;
            if (purpleHeartComplete) cadence = cadence * 4 / 5;
            return cadence;
        }
    }

    internal sealed class ItopodOnlineState
    {
        internal int SavedStart;
        internal int SavedEnd;
        internal int LiveFloor;
        internal int KillCounter;
        internal int HighestRecord;
        internal long EnemiesKilled;
        internal long PointProgress;
        internal long SpendablePerkPoints;
        internal long LifetimePerkPoints;
        internal long CurrentAp;
        internal long LifetimeAp;
        internal long BaseExpAwarded;
        internal long PoopProgress;
        internal long PoopAwarded;

        internal ItopodOnlineState Clone()
        {
            return (ItopodOnlineState)MemberwiseClone();
        }
    }

    internal sealed class ItopodKillTransition
    {
        internal readonly ItopodOnlineState Before;
        internal readonly ItopodOnlineState After;
        internal readonly int FoughtFloor;
        internal readonly int DropFloor;
        internal readonly long OrdinaryProgress;
        internal readonly long OrdinaryPerkPoints;
        internal readonly long FirstClearPerkPoints;
        internal readonly bool NewRecord;
        internal readonly int RewardTier;
        internal readonly int RewardDivisor;
        internal readonly bool ApAwarded;
        internal readonly long BaseExpAwarded;
        internal readonly bool MacguffinScheduled;
        internal readonly bool DeterministicPoopAwarded;
        internal readonly double ExpectedRandomPoop;
        internal readonly double AnyBoostProbability;
        internal readonly double EachBoostFamilyProbability;
        internal readonly int BoostMagnitudeIndex;
        internal readonly double EndItem491Probability;
        internal readonly double ExileItem337Probability;

        internal ItopodKillTransition(ItopodOnlineState before, ItopodOnlineState after,
            int foughtFloor, int dropFloor, long ordinaryProgress, long ordinaryPerkPoints,
            long firstClearPerkPoints, bool newRecord, int rewardTier, int rewardDivisor,
            bool apAwarded, long baseExpAwarded, bool macguffinScheduled,
            bool deterministicPoopAwarded, double expectedRandomPoop,
            int boostMagnitudeIndex, double endItem491Probability,
            double exileItem337Probability)
        {
            Before = before;
            After = after;
            FoughtFloor = foughtFloor;
            DropFloor = dropFloor;
            OrdinaryProgress = ordinaryProgress;
            OrdinaryPerkPoints = ordinaryPerkPoints;
            FirstClearPerkPoints = firstClearPerkPoints;
            NewRecord = newRecord;
            RewardTier = rewardTier;
            RewardDivisor = rewardDivisor;
            ApAwarded = apAwarded;
            BaseExpAwarded = baseExpAwarded;
            MacguffinScheduled = macguffinScheduled;
            DeterministicPoopAwarded = deterministicPoopAwarded;
            ExpectedRandomPoop = expectedRandomPoop;
            AnyBoostProbability = ItopodPerkPlanner.AnyBoostProbability;
            EachBoostFamilyProbability = ItopodPerkPlanner.EachBoostFamilyProbability;
            BoostMagnitudeIndex = boostMagnitudeIndex;
            EndItem491Probability = endItem491Probability;
            ExileItem337Probability = exileItem337Probability;
        }
    }

    internal sealed class ItopodOnlineEstimate
    {
        internal readonly ItopodOnlineState FinalState;
        internal readonly long Kills;
        internal readonly long OrdinaryProgress;
        internal readonly long OrdinaryPerkPoints;
        internal readonly long FirstClearPerkPoints;
        internal readonly long ApAwards;
        internal readonly long BaseExpAwards;
        internal readonly long MacguffinsScheduled;
        internal readonly double ExpectedBoosts;
        internal readonly double ExpectedRandomPoop;
        internal readonly double ProbabilityAtLeastOneEndItem491;

        internal ItopodOnlineEstimate(ItopodOnlineState finalState, long kills,
            long ordinaryProgress, long ordinaryPerkPoints, long firstClearPerkPoints, long apAwards,
            long baseExpAwards, long macguffinsScheduled, double expectedBoosts,
            double expectedRandomPoop, double probabilityAtLeastOneEndItem491)
        {
            FinalState = finalState;
            Kills = kills;
            OrdinaryProgress = ordinaryProgress;
            OrdinaryPerkPoints = ordinaryPerkPoints;
            FirstClearPerkPoints = firstClearPerkPoints;
            ApAwards = apAwards;
            BaseExpAwards = baseExpAwards;
            MacguffinsScheduled = macguffinsScheduled;
            ExpectedBoosts = expectedBoosts;
            ExpectedRandomPoop = expectedRandomPoop;
            ProbabilityAtLeastOneEndItem491 = probabilityAtLeastOneEndItem491;
        }
    }

    internal sealed class ItopodOnlineTimeEstimate
    {
        internal readonly double WindowSeconds;
        internal readonly double KillCycleSeconds;
        internal readonly ItopodOnlineEstimate KillEstimate;
        internal readonly double OrdinaryPpPerSecond;
        internal readonly double ExpectedBoostsPerSecond;

        internal ItopodOnlineTimeEstimate(double windowSeconds, double killCycleSeconds,
            ItopodOnlineEstimate killEstimate)
        {
            WindowSeconds = windowSeconds;
            KillCycleSeconds = killCycleSeconds;
            KillEstimate = killEstimate;
            OrdinaryPpPerSecond = windowSeconds == 0.0 ? 0.0
                : killEstimate.OrdinaryProgress
                  / (double)MechanicsItopod.ProgressPerPerkPoint / windowSeconds;
            ExpectedBoostsPerSecond = windowSeconds == 0.0 ? 0.0
                : killEstimate.ExpectedBoosts / windowSeconds;
        }
    }

    internal sealed class OfflineItopodEstimate
    {
        internal readonly int BestFloor;
        internal readonly double KillCycleSeconds;
        internal readonly long Kills;
        internal readonly long OrdinaryProgress;
        internal readonly long OrdinaryPerkPoints;
        internal readonly long RemainingPointProgress;
        internal readonly long ApAwards;
        internal readonly long BaseExpAwards;
        internal readonly long MacguffinsScheduled;
        internal readonly long DeterministicPoopAwards;
        internal readonly long CubeBoostBatches;
        internal readonly bool SpecialDropsPossible;
        internal readonly bool FirstClearAwardsPossible;
        internal readonly string Warning;

        internal OfflineItopodEstimate(int bestFloor, double killCycleSeconds, long kills,
            long ordinaryProgress, long ordinaryPerkPoints, long remainingPointProgress,
            long apAwards, long baseExpAwards, long macguffinsScheduled,
            long deterministicPoopAwards, long cubeBoostBatches)
        {
            BestFloor = bestFloor;
            KillCycleSeconds = killCycleSeconds;
            Kills = kills;
            OrdinaryProgress = ordinaryProgress;
            OrdinaryPerkPoints = ordinaryPerkPoints;
            RemainingPointProgress = remainingPointProgress;
            ApAwards = apAwards;
            BaseExpAwards = baseExpAwards;
            MacguffinsScheduled = macguffinsScheduled;
            DeterministicPoopAwards = deterministicPoopAwards;
            CubeBoostBatches = cubeBoostBatches;
            SpecialDropsPossible = false;
            FirstClearAwardsPossible = false;
            Warning = "Native offline ITOPOD uses session-local integer divisions and awards no online specials.";
        }
    }

    internal sealed class ItopodDropForecast
    {
        internal readonly int Floor;
        internal readonly double ChancePerKill;
        internal readonly double MeanKills;
        internal readonly long MedianKills;
        internal readonly long P90Kills;
        internal readonly long P95Kills;
        internal readonly LootCapacityProof Capacity;

        internal ItopodDropForecast(int floor, double chancePerKill, double meanKills,
            long medianKills, long p90Kills, long p95Kills, LootCapacityProof capacity)
        {
            Floor = floor;
            ChancePerKill = chancePerKill;
            MeanKills = meanKills;
            MedianKills = medianKills;
            P90Kills = p90Kills;
            P95Kills = p95Kills;
            Capacity = capacity;
        }
    }

    /*
    POST-KILL SENTINEL GATE

    The native record is saved before loot and before the enemy reference is cleared.  An
    integration must call this gate from a post-enemy-death hook and synchronously leave/re-range
    when requested; a one-second scheduler is not an acceptable substitute.  The persistent global
    kill counter is the event identity, preventing two callbacks from producing two Adventure
    mutations for the same native kill.
    */
    internal sealed class ItopodPostKillGate
    {
        private long _lastObservedGlobalKill = -1L;

        internal PostKillDirective Observe(ItopodOnlineState before, ItopodOnlineState after,
            ItopodRangePlan plan)
        {
            if (before == null || after == null || plan == null)
                return new PostKillDirective(PostKillDirectiveKind.InvalidObservation, false,
                    "before, after, and plan are required");
            if (after.EnemiesKilled == _lastObservedGlobalKill)
                return new PostKillDirective(PostKillDirectiveKind.DuplicateObservation, false,
                    "this native kill was already consumed as one scheduler atom");
            if (after.EnemiesKilled != before.EnemiesKilled + 1L)
                return new PostKillDirective(PostKillDirectiveKind.InvalidObservation, false,
                    "post-kill hook requires exactly one global-kill transition");
            _lastObservedGlobalKill = after.EnemiesKilled;

            if (after.LiveFloor > plan.ProvenFoughtFloor
                || (plan.Climbing && after.HighestRecord >= plan.TargetRecord))
            {
                return new PostKillDirective(PostKillDirectiveKind.ExitSynchronouslyAndReplan,
                    true, "record/drop settlement completed on an unproved or terminal sentinel");
            }
            return new PostKillDirective(PostKillDirectiveKind.Continue, false,
                "post-move floor remains inside the proved fought-floor range");
        }

        internal void ResetForNewRunEpoch()
        {
            _lastObservedGlobalKill = -1L;
        }
    }

    internal sealed class ClueFourObservation
    {
        internal readonly bool SessionArmed;
        internal readonly bool SessionEligible;
        internal readonly bool QualifyingKill;
        internal readonly bool Complete;
        internal readonly string Reason;

        internal ClueFourObservation(bool sessionArmed, bool sessionEligible,
            bool qualifyingKill, bool complete, string reason)
        {
            SessionArmed = sessionArmed;
            SessionEligible = sessionEligible;
            QualifyingKill = qualifyingKill;
            Complete = complete;
            Reason = reason ?? string.Empty;
        }
    }

    /*
    CLUE-FOUR SESSION

    Eligibility is session state, not a loadout goal that can be restored immediately before floor
    100.  Once any nonzero weapon/armor/accessory is observed after entry, this object cannot be
    re-armed; the integration must leave ITOPOD, re-enter naked with saved start zero, and create a
    new session.  The 99->100 movement has counter zero and does not qualify.  The following kill,
    made while live on 100 and leaving live=100/counter=1, is the qualifying event.
    */
    internal sealed class ClueFourSession
    {
        private bool _armed;
        private bool _eligible;
        private bool _complete;

        internal ClueFourObservation Enter(bool cluesOneThroughThreeComplete,
            int savedStart, bool everyEquipmentSlotEmpty)
        {
            _armed = cluesOneThroughThreeComplete && savedStart == 0 && everyEquipmentSlotEmpty;
            _eligible = _armed;
            _complete = false;
            return Snapshot(false, _armed
                ? "naked start-zero ITOPOD session armed"
                : "clues 1-3, saved start zero, and complete nakedness are required at entry");
        }

        internal ClueFourObservation ObserveKill(int savedStart, int postMoveLiveFloor,
            int postMoveKillCounter, bool everyEquipmentSlotEmpty, bool nativeClueComplete)
        {
            if (!_armed) return Snapshot(false, "session was not armed at ITOPOD entry");
            if (!everyEquipmentSlotEmpty) _eligible = false;
            var qualifies = _eligible && savedStart == 0 && postMoveLiveFloor == 100
                            && postMoveKillCounter == 1;
            if (nativeClueComplete) _complete = true;
            return Snapshot(qualifies, !_eligible
                ? "equipped gear permanently invalidated this ITOPOD session"
                : qualifies ? "first kill while live on floor 100 satisfies the native clue predicate"
                : "continue naked until post-move floor 100 and counter 1");
        }

        private ClueFourObservation Snapshot(bool qualifyingKill, string reason)
        {
            return new ClueFourObservation(_armed, _eligible, qualifyingKill, _complete, reason);
        }
    }

    internal enum PerkEffectClass
    {
        Unknown,
        RateMultiplier,
        ResourceGeneration,
        FeatureUnlock,
        CapacityUnlock,
        FibonacciMilestone,
        TerminalAsyncOrdinaryItem
    }

    internal sealed class PerkCandidate
    {
        internal readonly int Id;
        internal readonly string Name;
        internal readonly long FlatCost;
        internal readonly long CurrentLevel;
        internal readonly long MaximumLevel;
        internal readonly ItopodDifficulty RequiredDifficulty;
        internal readonly PerkEffectClass EffectClass;
        internal readonly double TerminalSecondsSaved;
        internal readonly int AsyncItemId;

        internal PerkCandidate(int id, string name, long flatCost, long currentLevel,
            long maximumLevel, ItopodDifficulty requiredDifficulty, PerkEffectClass effectClass,
            double terminalSecondsSaved, int asyncItemId)
        {
            if (id < 0) throw new ArgumentOutOfRangeException("id");
            if (flatCost < 0L) throw new ArgumentOutOfRangeException("flatCost");
            if (currentLevel < 0L || maximumLevel < 0L)
                throw new ArgumentOutOfRangeException("currentLevel");
            if (double.IsNaN(terminalSecondsSaved) || terminalSecondsSaved < 0.0)
                throw new ArgumentOutOfRangeException("terminalSecondsSaved");
            Id = id;
            Name = name ?? string.Empty;
            FlatCost = flatCost;
            CurrentLevel = currentLevel;
            MaximumLevel = maximumLevel;
            RequiredDifficulty = requiredDifficulty;
            EffectClass = effectClass;
            TerminalSecondsSaved = terminalSecondsSaved;
            AsyncItemId = asyncItemId;
        }

        internal bool IsAtCap
        {
            get { return MaximumLevel != 0L && CurrentLevel >= MaximumLevel; }
        }
    }

    internal enum PerkPlanStatus
    {
        Planned,
        HeldNoTypedCandidate,
        HeldReserve,
        HeldDifficulty,
        HeldInvalidId
    }

    internal sealed class TypedPerkPlan
    {
        internal readonly PerkPlanStatus Status;
        internal readonly PerkCandidate Candidate;
        internal readonly long SpendableAfter;
        internal readonly double SecondsSavedPerPoint;
        internal readonly bool HoldOrdinarySlotUntilDelivery;
        internal readonly string Reason;

        internal TypedPerkPlan(PerkPlanStatus status, PerkCandidate candidate,
            long spendableAfter, double secondsSavedPerPoint,
            bool holdOrdinarySlotUntilDelivery, string reason)
        {
            Status = status;
            Candidate = candidate;
            SpendableAfter = spendableAfter;
            SecondsSavedPerPoint = secondsSavedPerPoint;
            HoldOrdinarySlotUntilDelivery = holdOrdinarySlotUntilDelivery;
            Reason = reason ?? string.Empty;
        }
    }

    internal enum AsyncPerkGrantStatus
    {
        SourceNotPurchased,
        WaitingForBoss225,
        WaitingForCapacity,
        WaitingForFilter,
        WaitingForChecker,
        EligibleOnNextChecker,
        Delivered
    }

    internal sealed class AsyncPerkGrantState
    {
        internal readonly AsyncPerkGrantStatus Status;
        internal readonly int ItemId;
        internal readonly int ReservedOrdinarySlots;
        internal readonly double NextCheckerEtaSeconds;
        internal readonly LootCapacityProof Capacity;
        internal readonly string Reason;

        internal AsyncPerkGrantState(AsyncPerkGrantStatus status, int itemId,
            int reservedOrdinarySlots, double nextCheckerEtaSeconds,
            LootCapacityProof capacity, string reason)
        {
            Status = status;
            ItemId = itemId;
            ReservedOrdinarySlots = reservedOrdinarySlots;
            NextCheckerEtaSeconds = nextCheckerEtaSeconds;
            Capacity = capacity;
            Reason = reason ?? string.Empty;
        }
    }

    internal enum AdventureRouteChoice
    {
        ProgressionPush,
        BossSnipe,
        CollectionFarm,
        ItopodFrontier,
        ItopodFarm
    }

    internal sealed class AdventureRoutePlan
    {
        internal readonly AdventureRouteChoice Choice;
        internal readonly int AwardFloor;
        internal readonly long FirstClearPerkPoints;
        internal readonly long KillsToAward;
        internal readonly double SecondsToAward;
        internal readonly bool CompletesPerkGate;
        internal readonly double ItopodProgressionRate;
        internal readonly double CollectionProgressionRate;
        internal readonly string Reason;

        internal AdventureRoutePlan(AdventureRouteChoice choice, int awardFloor,
            long firstClearPerkPoints, long killsToAward, double secondsToAward,
            bool completesPerkGate, double itopodProgressionRate,
            double collectionProgressionRate, string reason)
        {
            Choice = choice;
            AwardFloor = awardFloor;
            FirstClearPerkPoints = firstClearPerkPoints;
            KillsToAward = killsToAward;
            SecondsToAward = secondsToAward;
            CompletesPerkGate = completesPerkGate;
            ItopodProgressionRate = itopodProgressionRate;
            CollectionProgressionRate = collectionProgressionRate;
            Reason = reason ?? string.Empty;
        }
    }

    internal static class ItopodPerkPlanner
    {
        internal const double AnyBoostProbability = 0.084;
        internal const double EachBoostFamilyProbability = 0.028;
        internal const int Perk231Id = 231;
        internal const long Perk231Cost = 2500000000L;
        internal const int Perk231ItemId = 482;
        internal const int EndItem491Id = 491;
        internal const int MaximumFloor = 1600;

        private static readonly int[] FibonacciMilestones =
            { 1, 2, 3, 5, 8, 13, 21, 34, 55, 89, 144, 233, 377, 610, 987, 1597 };

        /*
        CROSS-SUBSYSTEM ADVENTURE ROUTE VALUE

        An ITOPOD record is not merely another farm drop.  The installed build awards direct,
        unmodified PP on decade records, with a tenfold super-decade award.  This pure selector
        prices the next reachable award against the selected collection debt and, most
        importantly, detects when that exact award crosses the next typed perk-purchase gate.
        Unknown core-set ETAs remain conservative: they retain their ordinary route unless a
        discrete perk gate or terminal END route proves ITOPOD should preempt it. Optional item
        debt is stricter: a merely positive finished-item score cannot own Adventure without a
        source-calibrated ETA that beats exact ordinary ITOPOD PP progress toward the next typed
        perk. Rates compare logarithmic permanent progression gain per second only when both sides
        have source-backed time/value inputs; incomparable native reward units are never silently
        added together.
        */
        internal static AdventureRoutePlan ChooseAdventureRoute(int highestRecord,
            int provenFrontierFloor, double itopodCycleSeconds, long currentPerkPoints,
            long reservePerkPoints, long nextPerkCost, double nextPerkLogGain,
            bool hasCollectionDebt, bool collectionBossOnly, bool collectionBackfill,
            double collectionExpectedSeconds, double collectionNativeMagnitude,
            bool progressionPush, bool terminalItopodDropMissing,
            int liveFloor = -1, int killCounter = 0,
            int savedStart = -1, int savedEnd = -1,
            double ordinaryItopodPpPerSecond = 0.0,
            bool collectionOptionalOnly = false)
        {
            ValidateFloor(highestRecord, "highestRecord");
            ValidateFloor(provenFrontierFloor, "provenFrontierFloor");
            if (double.IsNaN(itopodCycleSeconds) || double.IsInfinity(itopodCycleSeconds)
                || itopodCycleSeconds <= 0.0)
                throw new ArgumentOutOfRangeException("itopodCycleSeconds");
            if (currentPerkPoints < 0L || reservePerkPoints < 0L || nextPerkCost < 0L)
                throw new ArgumentOutOfRangeException("currentPerkPoints");
            if (double.IsNaN(nextPerkLogGain) || double.IsInfinity(nextPerkLogGain)
                || nextPerkLogGain < 0.0)
                throw new ArgumentOutOfRangeException("nextPerkLogGain");
            if (double.IsNaN(collectionExpectedSeconds)
                || double.IsNaN(collectionNativeMagnitude)
                || collectionNativeMagnitude < 0.0)
                throw new ArgumentOutOfRangeException("collectionExpectedSeconds");
            if (double.IsNaN(ordinaryItopodPpPerSecond)
                || double.IsInfinity(ordinaryItopodPpPerSecond)
                || ordinaryItopodPpPerSecond < 0.0)
                throw new ArgumentOutOfRangeException("ordinaryItopodPpPerSecond");

            var awardFloor = NextReachableAwardFloor(highestRecord, provenFrontierFloor);
            long award = 0L;
            long kills = 0L;
            var seconds = double.PositiveInfinity;
            if (awardFloor > 0)
            {
                award = MechanicsItopod.FirstClearPerkPoints(awardFloor, true);
                var canContinueLive = liveFloor >= 0 && liveFloor <= MaximumFloor
                                      && killCounter >= 0 && killCounter < 10
                                      && savedStart >= 0 && savedStart <= liveFloor
                                      && savedEnd >= awardFloor;
                kills = canContinueLive
                    ? KillsToRecord(savedStart, savedEnd, liveFloor, killCounter,
                        highestRecord, awardFloor)
                    : FreshEntryKillsToRecord(Math.Max(0, highestRecord - 1),
                        awardFloor, highestRecord, awardFloor);
                seconds = kills * itopodCycleSeconds;
            }

            var spendable = Math.Max(0L, currentPerkPoints - reservePerkPoints);
            var gap = nextPerkCost <= spendable ? 0L : nextPerkCost - spendable;
            var completesGate = gap > 0L && award >= gap;
            // nextPerkLogGain describes the whole next level, not one PP.  Credit only the
            // fraction of that discrete gate supplied by this award and cap a gate-closing
            // super-decade at one whole level; otherwise a ten-PP award is spuriously priced
            // as ten complete copies of the same perk effect.
            var creditedFraction = gap > 0L
                ? Math.Min(1.0, award / (double)Math.Max(1L, gap)) : 0.0;
            var firstClearRate = awardFloor > 0 && seconds > 0.0
                ? nextPerkLogGain * creditedFraction / seconds : 0.0;
            var steadyRate = gap > 0L
                ? nextPerkLogGain * ordinaryItopodPpPerSecond / Math.Max(1L, gap) : 0.0;
            var itopodRate = Math.Max(firstClearRate, steadyRate);
            var collectionRate = collectionExpectedSeconds > 0.0
                                 && !double.IsInfinity(collectionExpectedSeconds)
                                 && collectionNativeMagnitude > 0.0
                ? Math.Log(1.0 + collectionNativeMagnitude) / collectionExpectedSeconds : -1.0;

            if (terminalItopodDropMissing)
                return Route(AdventureRouteChoice.ItopodFarm,
                    "Sadistic END item 491 is missing; its only source is the eligible ITOPOD farm",
                    awardFloor, award, kills, seconds, completesGate, itopodRate, collectionRate);
            if (!hasCollectionDebt)
                return Route(awardFloor > 0 ? AdventureRouteChoice.ItopodFrontier
                        : AdventureRouteChoice.ItopodFarm,
                    awardFloor > 0
                        ? "all fightable collection debt is complete; take the next exact first-clear PP award"
                        : "all fightable collection debt is complete; maximize steady ITOPOD rewards",
                    awardFloor, award, kills, seconds, completesGate, itopodRate, collectionRate);
            if (completesGate)
                return Route(AdventureRouteChoice.ItopodFrontier,
                    "the next reachable first-clear award supplies " + award
                    + " PP in " + seconds.ToString("0.##")
                    + "s and completes the next typed perk gate",
                    awardFloor, award, kills, seconds, true, itopodRate, collectionRate);
            if (collectionOptionalOnly && itopodRate > 0.0 && collectionRate < 0.0)
                return Route(awardFloor > 0 ? AdventureRouteChoice.ItopodFrontier
                        : AdventureRouteChoice.ItopodFarm,
                    "optional collection has no source-calibrated completion ETA, while exact "
                    + "ordinary ITOPOD PP progress advances the next typed perk",
                    awardFloor, award, kills, seconds, false, itopodRate, collectionRate);
            if (awardFloor > 0 && collectionRate >= 0.0 && itopodRate > collectionRate)
                return Route(AdventureRouteChoice.ItopodFrontier,
                    "source-backed ITOPOD permanent gain per second exceeds the selected collection reward",
                    awardFloor, award, kills, seconds, false, itopodRate, collectionRate);
            if (collectionOptionalOnly && collectionRate >= 0.0 && itopodRate > collectionRate)
                return Route(awardFloor > 0 ? AdventureRouteChoice.ItopodFrontier
                        : AdventureRouteChoice.ItopodFarm,
                    "exact ordinary ITOPOD PP value per second exceeds the optional completed-item value per second",
                    awardFloor, award, kills, seconds, false, itopodRate, collectionRate);

            var ordinary = progressionPush ? AdventureRouteChoice.ProgressionPush
                : collectionBossOnly ? AdventureRouteChoice.BossSnipe
                : AdventureRouteChoice.CollectionFarm;
            var ordinaryReason = collectionExpectedSeconds <= 0.0
                                 || double.IsInfinity(collectionExpectedSeconds)
                ? "collection ETA is not source-calibrated and no reachable PP award closes a perk gate"
                : "selected collection progression rate is at least the next first-clear PP rate";
            if (collectionBackfill) ordinaryReason += "; route is explicit permanent backfill";
            return Route(ordinary, ordinaryReason, awardFloor, award, kills, seconds,
                false, itopodRate, collectionRate);
        }

        private static AdventureRoutePlan Route(AdventureRouteChoice choice, string reason,
            int floor, long award, long kills, double seconds, bool gate,
            double itopodRate, double collectionRate)
        {
            return new AdventureRoutePlan(choice, floor, award, kills, seconds, gate,
                itopodRate, collectionRate, reason);
        }

        private static int NextReachableAwardFloor(int highestRecord,
            int provenFrontierFloor)
        {
            if (highestRecord >= MaximumFloor) return 0;
            var next = ((highestRecord / 10) + 1) * 10;
            if (next > MaximumFloor || provenFrontierFloor < next - 1) return 0;
            return next;
        }

        internal static ItopodRangePlan PlanContinuousClimb(int highestRecord,
            int provenOneHitFloor, int desiredRecord)
        {
            ValidateFloor(highestRecord, "highestRecord");
            ValidateFloor(provenOneHitFloor, "provenOneHitFloor");
            ValidateFloor(desiredRecord, "desiredRecord");
            var maximumTarget = Math.Min(MaximumFloor, provenOneHitFloor + 1);
            var target = Math.Min(desiredRecord, maximumTarget);
            if (target <= highestRecord)
            {
                var farm = Math.Min(provenOneHitFloor, Math.Max(0, highestRecord - 1));
                return new ItopodRangePlan(ItopodObjective.FixedFarm, highestRecord,
                    highestRecord, provenOneHitFloor, farm, Math.Max(1, farm), false, false, 0L,
                    "desired record is not above the saved record inside the one-hit proof");
            }

            var start = Math.Max(0, highestRecord - 1);
            var end = Math.Max(1, target);
            var kills = FreshEntryKillsToRecord(start, end, highestRecord, target);
            return new ItopodRangePlan(ItopodObjective.ContinuousClimb, highestRecord,
                target, provenOneHitFloor, start, end, true, true, kills,
                target < desiredRecord
                    ? "climb is capped at the proved fought-floor ceiling plus one sentinel"
                    : "continuous native range reaches the economic target then exits on its sentinel");
        }

        internal static ItopodRangePlan PlanClueFour()
        {
            return new ItopodRangePlan(ItopodObjective.ClueFour, 0, 100, 100,
                0, 100, true, false, 1001L,
                "enter range 0..100 naked and include the first kill while live on floor 100");
        }

        internal static long FreshEntryKillsToRecord(int start, int end,
            int initialRecord, int targetRecord)
        {
            ValidateNativeRange(start, end);
            ValidateFloor(initialRecord, "initialRecord");
            ValidateFloor(targetRecord, "targetRecord");
            if (targetRecord <= initialRecord) return 0L;
            var live = start;
            var counter = 0;
            var record = initialRecord;
            long kills = 0L;
            while (record < targetRecord)
            {
                if (kills >= 200000L)
                    throw new InvalidOperationException("target record is unreachable in the supplied range");
                kills++;
                counter++;
                if (counter < 10) continue;
                counter = 0;
                live++;
                if (live > end) live = start;
                if (live > record) record = live;
            }
            return kills;
        }

        internal static long KillsToRecord(int start, int end, int liveFloor,
            int killCounter, int initialRecord, int targetRecord)
        {
            ValidateNativeRange(start, end);
            ValidateFloor(liveFloor, "liveFloor");
            ValidateFloor(initialRecord, "initialRecord");
            ValidateFloor(targetRecord, "targetRecord");
            if (liveFloor < start || liveFloor > end)
                throw new ArgumentOutOfRangeException("liveFloor");
            if (killCounter < 0 || killCounter >= 10)
                throw new ArgumentOutOfRangeException("killCounter");
            if (targetRecord <= initialRecord) return 0L;
            var live = liveFloor;
            var counter = killCounter;
            var record = initialRecord;
            long kills = 0L;
            while (record < targetRecord)
            {
                if (kills >= 200000L)
                    throw new InvalidOperationException("target record is unreachable in the supplied live range");
                kills++;
                counter++;
                if (counter < 10) continue;
                counter = 0;
                live++;
                if (live > end) live = start;
                if (live > record) record = live;
            }
            return kills;
        }

        /*
        EXACT ONLINE KILL ATOM

        This method deliberately keeps foughtFloor and dropFloor as separate values.  Moving their
        calculations together would silently break tenth-kill PP, decade awards, AP/EXP divisors,
        the floor-100 clue, and the 1450+ END probability.  Direct AP increments both AP counters by
        exactly one; no general AP modifier is accepted as an input.
        */
        internal static ItopodKillTransition SimulateOnlineKill(ItopodOnlineState input,
            ItopodEconomy economy)
        {
            if (input == null) throw new ArgumentNullException("input");
            if (economy == null) throw new ArgumentNullException("economy");
            ValidateOnlineState(input);
            var before = input.Clone();
            var after = input.Clone();
            var foughtFloor = after.LiveFloor;
            var progress = MechanicsItopod.OrdinaryProgressPerKill(economy.Difficulty,
                foughtFloor, economy.TotalPpBonus, economy.ImprovedBasePp);
            var combinedProgress = SaturatingAdd(after.PointProgress, progress);
            var ordinaryPoints = combinedProgress / MechanicsItopod.ProgressPerPerkPoint;
            after.PointProgress = combinedProgress % MechanicsItopod.ProgressPerPerkPoint;
            after.SpendablePerkPoints = SaturatingAdd(after.SpendablePerkPoints, ordinaryPoints);
            after.LifetimePerkPoints = SaturatingAdd(after.LifetimePerkPoints, ordinaryPoints);

            after.EnemiesKilled = SaturatingAdd(after.EnemiesKilled, 1L);
            after.KillCounter++;
            if (economy.Perk30Owned)
            {
                after.PoopProgress = SaturatingAdd(after.PoopProgress, 1L);
                var poop = after.PoopProgress / 9000L;
                after.PoopProgress %= 9000L;
                after.PoopAwarded = SaturatingAdd(after.PoopAwarded, poop);
            }

            if (after.KillCounter >= 10)
            {
                after.KillCounter = 0;
                after.LiveFloor++;
                if (after.LiveFloor > after.SavedEnd) after.LiveFloor = after.SavedStart;
            }
            var newRecord = after.LiveFloor > after.HighestRecord;
            var firstClear = MechanicsItopod.FirstClearPerkPoints(after.LiveFloor, newRecord);
            if (newRecord)
            {
                after.SpendablePerkPoints = SaturatingAdd(after.SpendablePerkPoints, firstClear);
                after.HighestRecord = after.LiveFloor;
            }

            var dropFloor = after.LiveFloor;
            var tier = RewardTier(dropFloor);
            var divisor = RewardDivisor(dropFloor);
            var scheduled = after.EnemiesKilled % divisor == 0L;
            var baseExp = scheduled ? BaseExpPerAward(tier) : 0L;
            if (scheduled)
            {
                after.CurrentAp = SaturatingAdd(after.CurrentAp, 1L);
                after.LifetimeAp = SaturatingAdd(after.LifetimeAp, 1L);
                after.BaseExpAwarded = SaturatingAdd(after.BaseExpAwarded, baseExp);
            }
            var guff = economy.MacguffinsEnabled
                       && after.EnemiesKilled % economy.MacguffinDivisor == 0L;
            return new ItopodKillTransition(before, after, foughtFloor, dropFloor,
                progress, ordinaryPoints, firstClear, newRecord, tier, divisor, scheduled,
                baseExp, guff, economy.Perk30Owned && after.PoopAwarded > before.PoopAwarded,
                economy.RandomPoopChancePerKill, BoostMagnitudeIndex(dropFloor),
                EndItem491Chance(dropFloor), ExileItem337Chance(dropFloor));
        }

        internal static ItopodOnlineEstimate EstimateOnline(ItopodOnlineState initial,
            ItopodEconomy economy, int kills)
        {
            if (initial == null) throw new ArgumentNullException("initial");
            if (kills < 0) throw new ArgumentOutOfRangeException("kills");
            var state = initial.Clone();
            long progress = 0L;
            long ordinary = 0L;
            long firstClear = 0L;
            long ap = 0L;
            long exp = 0L;
            long guffs = 0L;
            var endFailure = 1.0;
            for (var i = 0; i < kills; i++)
            {
                var transition = SimulateOnlineKill(state, economy);
                state = transition.After;
                progress = SaturatingAdd(progress, transition.OrdinaryProgress);
                ordinary = SaturatingAdd(ordinary, transition.OrdinaryPerkPoints);
                firstClear = SaturatingAdd(firstClear, transition.FirstClearPerkPoints);
                if (transition.ApAwarded) ap++;
                exp = SaturatingAdd(exp, transition.BaseExpAwarded);
                if (transition.MacguffinScheduled) guffs++;
                endFailure *= 1.0 - transition.EndItem491Probability;
            }
            return new ItopodOnlineEstimate(state, kills, progress, ordinary, firstClear, ap, exp,
                guffs, kills * AnyBoostProbability,
                kills * economy.RandomPoopChancePerKill, 1.0 - endFailure);
        }

        internal static ItopodOnlineTimeEstimate EstimateOnlineIdle(ItopodOnlineState initial,
            ItopodEconomy economy, double windowSeconds, double attackSpeedSeconds,
            double respawnSeconds)
        {
            if (double.IsNaN(windowSeconds) || double.IsInfinity(windowSeconds)
                || windowSeconds < 0.0) throw new ArgumentOutOfRangeException("windowSeconds");
            if (double.IsNaN(attackSpeedSeconds) || double.IsInfinity(attackSpeedSeconds)
                || attackSpeedSeconds <= 0.0)
                throw new ArgumentOutOfRangeException("attackSpeedSeconds");
            if (double.IsNaN(respawnSeconds) || double.IsInfinity(respawnSeconds)
                || respawnSeconds < 0.0) throw new ArgumentOutOfRangeException("respawnSeconds");
            var cycle = attackSpeedSeconds + respawnSeconds;
            var rawKills = Math.Floor(windowSeconds / cycle);
            if (rawKills > int.MaxValue)
                throw new ArgumentOutOfRangeException("windowSeconds",
                    "online exact range simulation requires a bounded integer-kill horizon");
            return new ItopodOnlineTimeEstimate(windowSeconds, cycle,
                EstimateOnline(initial, economy, (int)rawKills));
        }

        internal static OfflineItopodEstimate EstimateOffline(int bestFloor,
            double offlineSeconds, double respawnSeconds, bool redLiquidComplete,
            ItopodEconomy economy, long startingPointProgress, long startingPoopProgress,
            bool cubeFilterEnabled)
        {
            ValidateFloor(bestFloor, "bestFloor");
            if (double.IsNaN(offlineSeconds) || double.IsInfinity(offlineSeconds)
                || offlineSeconds < 0.0) throw new ArgumentOutOfRangeException("offlineSeconds");
            if (double.IsNaN(respawnSeconds) || double.IsInfinity(respawnSeconds)
                || respawnSeconds < 0.0) throw new ArgumentOutOfRangeException("respawnSeconds");
            if (economy == null) throw new ArgumentNullException("economy");
            if (startingPointProgress < 0L
                || startingPointProgress >= MechanicsItopod.ProgressPerPerkPoint)
                throw new ArgumentOutOfRangeException("startingPointProgress");
            if (startingPoopProgress < 0L || startingPoopProgress >= 9000L)
                throw new ArgumentOutOfRangeException("startingPoopProgress");
            var cycle = (redLiquidComplete ? 0.8 : 1.0) + respawnSeconds;
            var rawKills = Math.Floor(offlineSeconds / cycle);
            var kills = rawKills >= long.MaxValue ? long.MaxValue : (long)rawKills;
            var perKill = MechanicsItopod.OrdinaryProgressPerKill(economy.Difficulty,
                bestFloor, economy.TotalPpBonus, economy.ImprovedBasePp);
            var totalProgress = SaturatingAdd(startingPointProgress,
                SaturatingMultiply(perKill, kills));
            var points = totalProgress / MechanicsItopod.ProgressPerPerkPoint;
            var remainder = totalProgress % MechanicsItopod.ProgressPerPerkPoint;
            var divisor = RewardDivisor(bestFloor);
            var rewardEvents = kills / divisor;
            var exp = SaturatingMultiply(rewardEvents, BaseExpPerAward(RewardTier(bestFloor)));
            var guffs = economy.MacguffinsEnabled ? kills / economy.MacguffinDivisor : 0L;
            var poop = economy.Perk30Owned
                ? SaturatingAdd(startingPoopProgress, kills) / 9000L : 0L;
            return new OfflineItopodEstimate(bestFloor, cycle, kills,
                SaturatingMultiply(perKill, kills), points, remainder, rewardEvents, exp,
                guffs, poop, cubeFilterEnabled ? kills / 8L : 0L);
        }

        internal static int RewardTier(int postMoveFloor)
        {
            ValidateFloor(postMoveFloor, "postMoveFloor");
            return 1 + postMoveFloor / 50;
        }

        internal static int RewardDivisor(int postMoveFloor)
        {
            return Math.Max(40 - RewardTier(postMoveFloor), 20);
        }

        internal static long BaseExpPerAward(int rewardTier)
        {
            if (rewardTier < 1) throw new ArgumentOutOfRangeException("rewardTier");
            return rewardTier <= 2 ? rewardTier
                : SaturatingAdd(SaturatingMultiply(rewardTier - 1L, rewardTier - 2L), 2L);
        }

        internal static int BoostMagnitudeIndex(int postMoveFloor)
        {
            var tier = RewardTier(postMoveFloor);
            if (tier <= 10) return tier;
            if (tier <= 14) return 10;
            if (tier <= 17) return 11;
            if (tier <= 23) return 12;
            return 13;
        }

        internal static double ExileItem337Chance(int postMoveFloor)
        {
            ValidateFloor(postMoveFloor, "postMoveFloor");
            return postMoveFloor < 950 || postMoveFloor > 999
                ? 0.0 : (postMoveFloor - 949) * 0.0001;
        }

        internal static double EndItem491Chance(int postMoveFloor)
        {
            ValidateFloor(postMoveFloor, "postMoveFloor");
            return postMoveFloor < 1450 ? 0.0 : (postMoveFloor - 1449) * 0.00005;
        }

        internal static LootCapacityRequirement EndItem491CapacityRequirement()
        {
            // A scheduled MacGuffin is inserted before checkEndDrop on the same native kill.
            return LootCapacityRequirement.ExactUniqueDelivery(
                "itopod-macguffin-before-end-491", 1, 1, 0);
        }

        internal static ItopodDropForecast ForecastEndItem491(int fixedPostMoveFloor,
            OrdinaryInventoryTopology topology)
        {
            if (topology == null) throw new ArgumentNullException("topology");
            var chance = EndItem491Chance(fixedPostMoveFloor);
            var proof = LootCapacity.ProveOrdinary(topology, EndItem491CapacityRequirement());
            return new ItopodDropForecast(fixedPostMoveFloor, chance,
                MechanicsStochastic.GeometricMeanTrials(chance),
                MechanicsStochastic.GeometricMedianTrials(chance),
                MechanicsStochastic.GeometricQuantileTrials(chance, 0.90),
                MechanicsStochastic.GeometricQuantileTrials(chance, 0.95), proof);
        }

        internal static bool IsFibonacciMilestone(long level)
        {
            for (var i = 0; i < FibonacciMilestones.Length; i++)
                if (FibonacciMilestones[i] == level) return true;
            return false;
        }

        internal static PerkCandidate TerminalPerk231(long currentLevel,
            double terminalSecondsSaved)
        {
            return new PerkCandidate(Perk231Id, "ERROR", Perk231Cost, currentLevel, 1L,
                ItopodDifficulty.Sadistic, PerkEffectClass.TerminalAsyncOrdinaryItem,
                terminalSecondsSaved, Perk231ItemId);
        }

        /*
        TYPED PERK SELECTION

        Scores compare only candidates whose effects have an explicit class and whose exact flat
        cost/difficulty/cap/ID predicates are satisfied.  This is intentionally not a generic
        tooltip-number scorer: feature unlocks, Fibonacci milestone transitions, capacity, and
        terminal item delivery remain distinguishable in the returned plan.
        */
        internal static TypedPerkPlan ChoosePerk(IList<PerkCandidate> candidates,
            int nativePerkCount, long spendablePoints, long reservePoints,
            ItopodDifficulty currentDifficulty)
        {
            if (candidates == null) throw new ArgumentNullException("candidates");
            if (nativePerkCount < 0) throw new ArgumentOutOfRangeException("nativePerkCount");
            if (spendablePoints < 0L || reservePoints < 0L)
                throw new ArgumentOutOfRangeException("spendablePoints");
            PerkCandidate best = null;
            var bestScore = double.NegativeInfinity;
            var sawInvalid = false;
            var sawDifficulty = false;
            var sawReserve = false;
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (candidate == null) continue;
                if (candidate.Id < 0 || candidate.Id >= nativePerkCount)
                {
                    sawInvalid = true;
                    continue;
                }
                if (candidate.EffectClass == PerkEffectClass.Unknown || candidate.IsAtCap)
                    continue;
                if (currentDifficulty < candidate.RequiredDifficulty)
                {
                    sawDifficulty = true;
                    continue;
                }
                if (candidate.FlatCost > spendablePoints
                    || spendablePoints - candidate.FlatCost < reservePoints)
                {
                    sawReserve = true;
                    continue;
                }
                var score = candidate.FlatCost == 0L
                    ? (candidate.TerminalSecondsSaved > 0.0 ? double.PositiveInfinity : 0.0)
                    : candidate.TerminalSecondsSaved / candidate.FlatCost;
                if (best == null || score > bestScore
                    || (score == bestScore && candidate.FlatCost < best.FlatCost)
                    || (score == bestScore && candidate.FlatCost == best.FlatCost
                        && candidate.Id < best.Id))
                {
                    best = candidate;
                    bestScore = score;
                }
            }
            if (best != null)
                return new TypedPerkPlan(PerkPlanStatus.Planned, best,
                    spendablePoints - best.FlatCost, bestScore,
                    best.EffectClass == PerkEffectClass.TerminalAsyncOrdinaryItem,
                    "highest exact terminal-seconds-saved per flat PP selected");
            var status = sawInvalid ? PerkPlanStatus.HeldInvalidId
                : sawReserve ? PerkPlanStatus.HeldReserve
                : sawDifficulty ? PerkPlanStatus.HeldDifficulty
                : PerkPlanStatus.HeldNoTypedCandidate;
            return new TypedPerkPlan(status, null, spendablePoints, 0.0, false,
                "no legal typed perk atom satisfies ID, difficulty, cap, and reserve predicates");
        }

        internal static LootCapacityRequirement Perk231DeliveryRequirement()
        {
            return LootCapacityRequirement.ExactUniqueDelivery(
                "perk-231-async-item-482", 0, 1, 0);
        }

        internal static AsyncPerkGrantState EvaluatePerk231Grant(long perk231Level,
            int highestSadisticBoss, bool ordinaryItem482Owned,
            OrdinaryInventoryTopology topology, bool filterAllowsItem482,
            double secondsUntilNextChecker)
        {
            if (perk231Level < 0L) throw new ArgumentOutOfRangeException("perk231Level");
            if (highestSadisticBoss < 0) throw new ArgumentOutOfRangeException("highestSadisticBoss");
            if (topology == null) throw new ArgumentNullException("topology");
            if (double.IsNaN(secondsUntilNextChecker) || double.IsInfinity(secondsUntilNextChecker))
                throw new ArgumentOutOfRangeException("secondsUntilNextChecker");
            var proof = LootCapacity.ProveOrdinary(topology, Perk231DeliveryRequirement());
            if (ordinaryItem482Owned)
                return new AsyncPerkGrantState(AsyncPerkGrantStatus.Delivered,
                    Perk231ItemId, 0, 0.0, proof, "ordinary item 482 is physically present");
            if (perk231Level < 1L)
                return new AsyncPerkGrantState(AsyncPerkGrantStatus.SourceNotPurchased,
                    Perk231ItemId, 0, 0.0, proof, "perk 231 source state is not complete");
            if (highestSadisticBoss < 225)
                return new AsyncPerkGrantState(AsyncPerkGrantStatus.WaitingForBoss225,
                    Perk231ItemId, 1, Math.Max(0.0, secondsUntilNextChecker), proof,
                    "source is complete; native END checker is gated by Sadistic Boss 225");
            if (!proof.Admitted)
                return new AsyncPerkGrantState(AsyncPerkGrantStatus.WaitingForCapacity,
                    Perk231ItemId, 1, Math.Max(0.0, secondsUntilNextChecker), proof,
                    "hold one usable ordinary slot until a checker successfully creates item 482");
            if (!filterAllowsItem482)
                return new AsyncPerkGrantState(AsyncPerkGrantStatus.WaitingForFilter,
                    Perk231ItemId, 1, Math.Max(0.0, secondsUntilNextChecker), proof,
                    "item 482 filter exemption must persist through asynchronous delivery");
            if (secondsUntilNextChecker > 0.0)
                return new AsyncPerkGrantState(AsyncPerkGrantStatus.WaitingForChecker,
                    Perk231ItemId, 1, Math.Min(30.0, secondsUntilNextChecker), proof,
                    "source verified; physical delivery is pending the native 30-second checker");
            return new AsyncPerkGrantState(AsyncPerkGrantStatus.EligibleOnNextChecker,
                Perk231ItemId, 1, 0.0, proof,
                "capacity/filter/boss gate are ready for the next checker invocation");
        }

        private static void ValidateOnlineState(ItopodOnlineState state)
        {
            ValidateNativeRange(state.SavedStart, state.SavedEnd);
            ValidateFloor(state.LiveFloor, "LiveFloor");
            ValidateFloor(state.HighestRecord, "HighestRecord");
            if (state.LiveFloor < state.SavedStart || state.LiveFloor > state.SavedEnd)
                throw new ArgumentOutOfRangeException("LiveFloor");
            if (state.KillCounter < 0 || state.KillCounter > 9)
                throw new ArgumentOutOfRangeException("KillCounter");
            if (state.EnemiesKilled < 0L || state.PointProgress < 0L
                || state.PointProgress >= MechanicsItopod.ProgressPerPerkPoint)
                throw new ArgumentOutOfRangeException("PointProgress");
            if (state.SpendablePerkPoints < 0L || state.LifetimePerkPoints < 0L
                || state.CurrentAp < 0L || state.LifetimeAp < 0L
                || state.BaseExpAwarded < 0L || state.PoopAwarded < 0L)
                throw new ArgumentOutOfRangeException("persistent counters");
            if (state.PoopProgress < 0L || state.PoopProgress >= 9000L)
                throw new ArgumentOutOfRangeException("PoopProgress");
        }

        private static void ValidateNativeRange(int start, int end)
        {
            ValidateFloor(start, "start");
            if (end < 1 || end > MaximumFloor) throw new ArgumentOutOfRangeException("end");
            if (start > end) throw new ArgumentException("native ITOPOD start cannot exceed end");
        }

        private static void ValidateFloor(int floor, string name)
        {
            if (floor < 0 || floor > MaximumFloor) throw new ArgumentOutOfRangeException(name);
        }

        private static long SaturatingAdd(long left, long right)
        {
            if (left < 0L || right < 0L) throw new ArgumentOutOfRangeException("saturating operands");
            return long.MaxValue - left < right ? long.MaxValue : left + right;
        }

        private static long SaturatingMultiply(long left, long right)
        {
            if (left < 0L || right < 0L) throw new ArgumentOutOfRangeException("saturating operands");
            if (left == 0L || right == 0L) return 0L;
            return left > long.MaxValue / right ? long.MaxValue : left * right;
        }
    }
}
