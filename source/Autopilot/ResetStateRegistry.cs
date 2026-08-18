/*
FILE PURPOSE

Purpose: This file is the typed reset-state registry for the progression optimizer.  It prevents the
bot from reducing NGU Idle to the unsafe binary "permanent versus reset-local" model by describing
persistent progress, reset conversions, banks, allocations, clocks, physical inventory, and derived
stochastic debt separately.

Mechanism: ResetStateRegistry maps each planner-visible state component to a state class and to an
effect for ordinary rebirth, challenge entry/completion, difficulty transition, and Titan kill.
ResetTransforms can apply scalar effects when the outcome is intrinsically zero/preserved or when a
caller supplies a source-derived resolved value for conversions and challenge-specific behavior.
Titan clock arrays are intentionally delegated to TitanMechanics.

Inputs and outputs: Inputs are enum keys/transitions and optional scalar values already computed by
native previews or exact subsystem oracles.  Outputs are immutable descriptor metadata or transformed
scalars.  The registry never reads Character and never executes a reset.

Invariants and safety: Persistent partial Wish/Hack/NGU progress must survive ordinary rebirth while
their allocations clear.  Titan clocks all reset on ordinary rebirth and only the killed Titan resets
on a Titan reward.  Physical inventory persists.  Conversions, challenge behavior, timer transforms,
and persistent reward awards fail closed unless a caller supplies the resolved post-transition value;
the registry must never fabricate it from an unrelated scalar.

Extension points and non-goals: Add a key when a solver begins pricing a new stock, then provide its
transition metadata and golden test.  Detailed challenge state machines and subsystem-specific
conversion formulas belong in dedicated oracles.  This file does not authorize rebirth/challenge
mutation or claim that every challenge has identical reset behavior.
*/
using System;

namespace NGUInjector.Autopilot
{
    internal enum ResetStateClass
    {
        PersistentStock,
        PersistentPartialProgress,
        ResetLocalStock,
        ResetTimeConversion,
        BankedTemporaryStock,
        ActivationAllocationState,
        ClockState,
        PhysicalInventoryState,
        StochasticDebt
    }

    internal enum ResetTransitionKind
    {
        OrdinaryRebirth,
        ChallengeEntry,
        ChallengeCompletion,
        DifficultyTransition,
        TitanKill
    }

    internal enum ResetEffectKind
    {
        Preserve,
        Clear,
        ReplaceWithResolvedValue,
        ConvertToPersistentBeforeClear,
        BankThenClear,
        ClearAllocationPreserveProgress,
        ResetActivationPreserveMaximum,
        ResetAllClocks,
        ResetTargetClock,
        TransformByNativeFactor,
        AwardPersistentReward,
        RecomputeDerived,
        ChallengeSpecific
    }

    internal enum ResetStateKey
    {
        ExperiencePurchases,
        AdventurePoints,
        PerkPoints,
        QuestPoints,
        CurrentNumber,
        FightBossProgress,
        BasicTrainingCaps,
        BasicTrainingRunLevels,
        BasicTrainingAllocations,
        AugmentRunState,
        TimeMachineRunState,
        CurrentGold,
        CurrentBlood,
        WandoosRunState,
        CurrentResourceFills,
        TitanClocks,
        TitanRunKillCounters,
        AdvancedTrainingTemporary,
        AdvancedTrainingBank,
        NguLevels,
        NguProgress,
        NguAllocations,
        BeardPermanentTrimmings,
        BeardTemporary,
        BeardBank,
        DiggerMaximumLevels,
        DiggerActivations,
        HackLevels,
        HackProgress,
        HackAllocations,
        WishLevels,
        WishProgress,
        WishAllocations,
        YggdrasilTiersAndSeeds,
        YggdrasilFruitTimers,
        MacGuffinLevels,
        CardsAndMayo,
        EquipmentInventory,
        ItemListFlags,
        ChallengeRewards,
        DifficultyUnlockState,
        TitanRewards,
        DerivedCollectionDebt
    }

    internal sealed class ResetStateDescriptor
    {
        internal readonly ResetStateKey Key;
        internal readonly ResetStateClass StateClass;
        internal readonly ResetEffectKind OrdinaryRebirth;
        internal readonly ResetEffectKind ChallengeEntry;
        internal readonly ResetEffectKind ChallengeCompletion;
        internal readonly ResetEffectKind DifficultyTransition;
        internal readonly ResetEffectKind TitanKill;
        internal readonly string PlanningNote;

        internal ResetStateDescriptor(
            ResetStateKey key,
            ResetStateClass stateClass,
            ResetEffectKind ordinaryRebirth,
            ResetEffectKind challengeEntry,
            ResetEffectKind challengeCompletion,
            ResetEffectKind difficultyTransition,
            ResetEffectKind titanKill,
            string planningNote)
        {
            Key = key;
            StateClass = stateClass;
            OrdinaryRebirth = ordinaryRebirth;
            ChallengeEntry = challengeEntry;
            ChallengeCompletion = challengeCompletion;
            DifficultyTransition = difficultyTransition;
            TitanKill = titanKill;
            PlanningNote = planningNote ?? string.Empty;
        }

        internal ResetEffectKind EffectFor(ResetTransitionKind transition)
        {
            switch (transition)
            {
                case ResetTransitionKind.OrdinaryRebirth: return OrdinaryRebirth;
                case ResetTransitionKind.ChallengeEntry: return ChallengeEntry;
                case ResetTransitionKind.ChallengeCompletion: return ChallengeCompletion;
                case ResetTransitionKind.DifficultyTransition: return DifficultyTransition;
                case ResetTransitionKind.TitanKill: return TitanKill;
                default: throw new ArgumentOutOfRangeException("transition");
            }
        }
    }

    internal static class ResetStateRegistry
    {
        private static readonly ResetStateDescriptor[] Descriptors = BuildDescriptors();

        internal static ResetStateDescriptor Find(ResetStateKey key)
        {
            for (var i = 0; i < Descriptors.Length; i++)
                if (Descriptors[i].Key == key) return Descriptors[i];
            throw new ArgumentOutOfRangeException("key");
        }

        internal static ResetStateDescriptor[] All()
        {
            return (ResetStateDescriptor[])Descriptors.Clone();
        }

        private static ResetStateDescriptor[] BuildDescriptors()
        {
            var preserve = ResetEffectKind.Preserve;
            var clear = ResetEffectKind.Clear;
            var challenge = ResetEffectKind.ChallengeSpecific;
            return new[]
            {
                D(ResetStateKey.ExperiencePurchases, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve, preserve,
                    "Purchased EXP power/cap/bars survive ordinary resets."),
                D(ResetStateKey.AdventurePoints, ResetStateClass.PersistentStock,
                    ResetEffectKind.AwardPersistentReward, preserve, challenge, preserve, preserve,
                    "AP persists and ordinary rebirth adds source-derived time/all-active awards."),
                D(ResetStateKey.PerkPoints, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve, preserve,
                    "PP and purchased perks persist."),
                D(ResetStateKey.QuestPoints, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve, preserve,
                    "QP and quirks persist."),
                D(ResetStateKey.CurrentNumber, ResetStateClass.ResetLocalStock, ResetEffectKind.ReplaceWithResolvedValue,
                    challenge, challenge, challenge, preserve,
                    "Ordinary rebirth assigns native preview Number; non-regression is not a native gate."),
                D(ResetStateKey.FightBossProgress, ResetStateClass.ResetLocalStock, clear, clear, challenge, challenge, preserve,
                    "Current Boss climb resets and must be replayed."),
                D(ResetStateKey.BasicTrainingCaps, ResetStateClass.ResetTimeConversion,
                    ResetEffectKind.ConvertToPersistentBeforeClear, challenge, preserve, challenge, preserve,
                    "Each track applies the exact cap compression before run Training is cleared."),
                D(ResetStateKey.BasicTrainingRunLevels, ResetStateClass.ResetLocalStock, clear, clear, preserve, challenge, preserve,
                    "Attack and Defense run levels clear."),
                D(ResetStateKey.BasicTrainingAllocations, ResetStateClass.ActivationAllocationState,
                    ResetEffectKind.ClearAllocationPreserveProgress, clear, preserve, challenge, preserve,
                    "Allocated Energy clears with run Training."),
                D(ResetStateKey.AugmentRunState, ResetStateClass.ResetLocalStock, clear, clear, preserve, challenge, preserve,
                    "Augment/upgrade run levels and progress are horizon-limited."),
                D(ResetStateKey.TimeMachineRunState, ResetStateClass.ResetLocalStock, clear, clear, preserve, challenge, preserve,
                    "TM run levels/progress and current-run Gold basis reset."),
                D(ResetStateKey.CurrentGold, ResetStateClass.ResetLocalStock, clear, clear, preserve, challenge, preserve,
                    "Gold shared by TM/Aug/Blood/Diggers/Pit is reset-local."),
                D(ResetStateKey.CurrentBlood, ResetStateClass.ResetLocalStock, clear, clear, preserve, challenge, preserve,
                    "Blood and ritual run work must earn a named spell payoff before reset."),
                D(ResetStateKey.WandoosRunState, ResetStateClass.ResetLocalStock, clear, clear, preserve, challenge, preserve,
                    "Energy/Magic allocation, progress, and run levels clear; installed OS fields are outside this key."),
                D(ResetStateKey.CurrentResourceFills, ResetStateClass.ResetLocalStock, clear, clear, preserve, challenge, preserve,
                    "Current Energy/Magic/R3 fills are replaced by post-reset generation."),
                D(ResetStateKey.TitanClocks, ResetStateClass.ClockState, ResetEffectKind.ResetAllClocks,
                    ResetEffectKind.ResetAllClocks, preserve, challenge, ResetEffectKind.ResetTargetClock,
                    "Use TitanMechanics for array transforms."),
                D(ResetStateKey.TitanRunKillCounters, ResetStateClass.ResetLocalStock, clear, clear, preserve, challenge,
                    ResetEffectKind.ReplaceWithResolvedValue,
                    "Ordinary reset clears run counters; Titan reward changes only its applicable counter."),
                D(ResetStateKey.AdvancedTrainingTemporary, ResetStateClass.BankedTemporaryStock,
                    ResetEffectKind.BankThenClear, clear, preserve, challenge, preserve,
                    "Native banking can seed the next run; challenge reset clears the temporary bank path."),
                D(ResetStateKey.AdvancedTrainingBank, ResetStateClass.ResetTimeConversion,
                    ResetEffectKind.ConvertToPersistentBeforeClear, clear, preserve, challenge, preserve,
                    "Resolve eligible bank capture from the native perk/challenge state."),
                D(ResetStateKey.NguLevels, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve, preserve,
                    "NGU levels persist."),
                D(ResetStateKey.NguProgress, ResetStateClass.PersistentPartialProgress, preserve, preserve, preserve, preserve, preserve,
                    "Partial NGU progress persists."),
                D(ResetStateKey.NguAllocations, ResetStateClass.ActivationAllocationState,
                    ResetEffectKind.ClearAllocationPreserveProgress, clear, preserve, challenge, preserve,
                    "Energy/Magic assignment clears without discarding levels/progress."),
                D(ResetStateKey.BeardPermanentTrimmings, ResetStateClass.ResetTimeConversion,
                    ResetEffectKind.ConvertToPersistentBeforeClear, preserve, preserve, preserve, preserve,
                    "Permanent trimmings are converted before ordinary Beard reset."),
                D(ResetStateKey.BeardTemporary, ResetStateClass.BankedTemporaryStock,
                    ResetEffectKind.BankThenClear, clear, preserve, challenge, preserve,
                    "Temporary Beard levels/progress clear after conversion; eligible bank seeds need resolution."),
                D(ResetStateKey.BeardBank, ResetStateClass.ResetTimeConversion,
                    ResetEffectKind.ConvertToPersistentBeforeClear, clear, preserve, challenge, preserve,
                    "Challenge reset clears eligible temporary Beard bank state."),
                D(ResetStateKey.DiggerMaximumLevels, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve, preserve,
                    "Purchased maximum levels persist."),
                D(ResetStateKey.DiggerActivations, ResetStateClass.ActivationAllocationState,
                    ResetEffectKind.ResetActivationPreserveMaximum, clear, preserve, challenge, preserve,
                    "All active Diggers turn off; the selected set must be re-equipped."),
                D(ResetStateKey.HackLevels, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve, preserve,
                    "Hack levels persist."),
                D(ResetStateKey.HackProgress, ResetStateClass.PersistentPartialProgress, preserve, preserve, preserve, preserve, preserve,
                    "Partial Hack progress persists."),
                D(ResetStateKey.HackAllocations, ResetStateClass.ActivationAllocationState,
                    ResetEffectKind.ClearAllocationPreserveProgress, clear, preserve, challenge, preserve,
                    "R3 assignment clears while level/progress remains."),
                D(ResetStateKey.WishLevels, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve, preserve,
                    "Wish levels persist."),
                D(ResetStateKey.WishProgress, ResetStateClass.PersistentPartialProgress, preserve, preserve, preserve, preserve, preserve,
                    "Partial Wish progress persists and is preemptible sunk state."),
                D(ResetStateKey.WishAllocations, ResetStateClass.ActivationAllocationState,
                    ResetEffectKind.ClearAllocationPreserveProgress, clear, preserve, challenge, preserve,
                    "Wish Energy/Magic/R3 allocations clear."),
                D(ResetStateKey.YggdrasilTiersAndSeeds, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve, preserve,
                    "Fruit tiers, seeds, and permanent effects persist."),
                D(ResetStateKey.YggdrasilFruitTimers, ResetStateClass.ClockState,
                    ResetEffectKind.TransformByNativeFactor, challenge, preserve, challenge, preserve,
                    "Resolve the native reset-factor transformation; do not zero timers generically."),
                D(ResetStateKey.MacGuffinLevels, ResetStateClass.ResetTimeConversion,
                    ResetEffectKind.ConvertToPersistentBeforeClear, challenge, preserve, preserve, preserve,
                    "Rebirth time factor converts into persistent MacGuffin growth."),
                D(ResetStateKey.CardsAndMayo, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve, preserve,
                    "Deck and six mayo currencies persist."),
                D(ResetStateKey.EquipmentInventory, ResetStateClass.PhysicalInventoryState, preserve, preserve, preserve, preserve, preserve,
                    "Gear persists but can be consumed, transformed, moved, or destroyed only by separate verified actions."),
                D(ResetStateKey.ItemListFlags, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve, preserve,
                    "Discovery/MAXX/set flags persist."),
                D(ResetStateKey.ChallengeRewards, ResetStateClass.PersistentStock, preserve, preserve,
                    ResetEffectKind.AwardPersistentReward, preserve, preserve,
                    "Completion-specific reward must be resolved by the challenge oracle."),
                D(ResetStateKey.DifficultyUnlockState, ResetStateClass.PersistentStock, preserve, preserve, preserve,
                    ResetEffectKind.ReplaceWithResolvedValue, preserve,
                    "Difficulty transition updates explicit unlock/current-difficulty state."),
                D(ResetStateKey.TitanRewards, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve,
                    ResetEffectKind.AwardPersistentReward,
                    "EXP/PP/QP/drop/version reward is resolved only after a confirmed kill."),
                D(ResetStateKey.DerivedCollectionDebt, ResetStateClass.StochasticDebt,
                    ResetEffectKind.RecomputeDerived, ResetEffectKind.RecomputeDerived,
                    ResetEffectKind.RecomputeDerived, ResetEffectKind.RecomputeDerived,
                    ResetEffectKind.RecomputeDerived,
                    "Recompute from persistent inventory, set deficits, rare outcomes, and the new action state.")
            };
        }

        private static ResetStateDescriptor D(
            ResetStateKey key, ResetStateClass stateClass,
            ResetEffectKind ordinary, ResetEffectKind challengeEntry,
            ResetEffectKind challengeCompletion, ResetEffectKind difficulty,
            ResetEffectKind titanKill, string note)
        {
            return new ResetStateDescriptor(key, stateClass, ordinary, challengeEntry,
                challengeCompletion, difficulty, titanKill, note);
        }
    }

    internal static class ResetTransforms
    {
        /*
        PURE FAIL-CLOSED SCALAR TRANSFORM

        Zero and preserve effects are mechanically complete.  A resolved value is mandatory for
        previews, conversions, banking, challenge behavior, timer factors, rewards, and recomputed
        planner debt.  Clock-array effects cannot be represented by one scalar and must go through
        TitanMechanics, which knows whether all clocks or one selected clock resets.
        */
        internal static double ApplyOrdinaryRebirth(
            ResetStateKey key, double currentValue, double? resolvedValue)
        {
            return ApplyScalar(ResetStateRegistry.Find(key), ResetTransitionKind.OrdinaryRebirth,
                currentValue, resolvedValue);
        }

        internal static double ApplyChallengeEntry(
            ResetStateKey key, double currentValue, double? resolvedValue)
        {
            return ApplyScalar(ResetStateRegistry.Find(key), ResetTransitionKind.ChallengeEntry,
                currentValue, resolvedValue);
        }

        internal static double ApplyChallengeCompletion(
            ResetStateKey key, double currentValue, double? resolvedValue)
        {
            return ApplyScalar(ResetStateRegistry.Find(key), ResetTransitionKind.ChallengeCompletion,
                currentValue, resolvedValue);
        }

        internal static double ApplyDifficultyTransition(
            ResetStateKey key, double currentValue, double? resolvedValue)
        {
            return ApplyScalar(ResetStateRegistry.Find(key), ResetTransitionKind.DifficultyTransition,
                currentValue, resolvedValue);
        }

        internal static double ApplyTitanKill(
            ResetStateKey key, double currentValue, double? resolvedValue)
        {
            return ApplyScalar(ResetStateRegistry.Find(key), ResetTransitionKind.TitanKill,
                currentValue, resolvedValue);
        }

        internal static double ApplyScalar(
            ResetStateDescriptor descriptor,
            ResetTransitionKind transition,
            double currentValue,
            double? resolvedValue)
        {
            if (descriptor == null) throw new ArgumentNullException("descriptor");
            if (double.IsNaN(currentValue)) throw new ArgumentOutOfRangeException("currentValue");
            var effect = descriptor.EffectFor(transition);
            switch (effect)
            {
                case ResetEffectKind.Preserve:
                    return currentValue;
                case ResetEffectKind.Clear:
                case ResetEffectKind.ClearAllocationPreserveProgress:
                case ResetEffectKind.ResetActivationPreserveMaximum:
                    return 0.0;
                case ResetEffectKind.ResetAllClocks:
                case ResetEffectKind.ResetTargetClock:
                    throw new InvalidOperationException("Use TitanMechanics for clock-array reset effects.");
                default:
                    if (!resolvedValue.HasValue || double.IsNaN(resolvedValue.Value))
                        throw new InvalidOperationException(
                            "This reset effect requires an explicit source-derived resolved value: " + effect);
                    return resolvedValue.Value;
            }
        }
    }
}
