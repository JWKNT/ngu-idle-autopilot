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
                    ResetEffectKind.AwardPersistentReward, preserve, challenge,
                    ResetEffectKind.AwardPersistentReward, preserve,
                    "AP persists and ordinary rebirth adds source-derived time/all-active awards."),
                D(ResetStateKey.PerkPoints, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve, preserve,
                    "PP and purchased perks persist."),
                D(ResetStateKey.QuestPoints, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve, preserve,
                    "QP and quirks persist."),
                D(ResetStateKey.CurrentNumber, ResetStateClass.ResetLocalStock, ResetEffectKind.ReplaceWithResolvedValue,
                    challenge, challenge, ResetEffectKind.ReplaceWithResolvedValue, preserve,
                    "Ordinary rebirth assigns native preview Number; non-regression is not a native gate."),
                D(ResetStateKey.FightBossProgress, ResetStateClass.ResetLocalStock, clear, clear, challenge, clear, preserve,
                    "Current Boss climb resets and must be replayed."),
                D(ResetStateKey.BasicTrainingCaps, ResetStateClass.ResetTimeConversion,
                    ResetEffectKind.ConvertToPersistentBeforeClear, challenge, preserve,
                    ResetEffectKind.ConvertToPersistentBeforeClear, preserve,
                    "Each track applies the exact cap compression before run Training is cleared."),
                D(ResetStateKey.BasicTrainingRunLevels, ResetStateClass.ResetLocalStock, clear, clear, preserve, clear, preserve,
                    "Attack and Defense run levels clear."),
                D(ResetStateKey.BasicTrainingAllocations, ResetStateClass.ActivationAllocationState,
                    ResetEffectKind.ClearAllocationPreserveProgress, clear, preserve,
                    ResetEffectKind.ClearAllocationPreserveProgress, preserve,
                    "Allocated Energy clears with run Training."),
                D(ResetStateKey.AugmentRunState, ResetStateClass.ResetLocalStock, clear, clear, preserve, clear, preserve,
                    "Augment/upgrade run levels and progress are horizon-limited."),
                D(ResetStateKey.TimeMachineRunState, ResetStateClass.ResetLocalStock, clear, clear, preserve, clear, preserve,
                    "TM run levels/progress and current-run Gold basis reset."),
                D(ResetStateKey.CurrentGold, ResetStateClass.ResetLocalStock, clear, clear, preserve, clear, preserve,
                    "Gold shared by TM/Aug/Blood/Diggers/Pit is reset-local."),
                D(ResetStateKey.CurrentBlood, ResetStateClass.ResetLocalStock, clear, clear, preserve, clear, preserve,
                    "Blood and ritual run work must earn a named spell payoff before reset."),
                D(ResetStateKey.WandoosRunState, ResetStateClass.ResetLocalStock, clear, clear, preserve, clear, preserve,
                    "Energy/Magic allocation, progress, and run levels clear; installed OS fields are outside this key."),
                D(ResetStateKey.CurrentResourceFills, ResetStateClass.ResetLocalStock, clear, clear, preserve, clear, preserve,
                    "Current Energy/Magic/R3 fills are replaced by post-reset generation."),
                D(ResetStateKey.TitanClocks, ResetStateClass.ClockState, ResetEffectKind.ResetAllClocks,
                    ResetEffectKind.ResetAllClocks, preserve, ResetEffectKind.ResetAllClocks,
                    ResetEffectKind.ResetTargetClock,
                    "Use TitanMechanics for array transforms."),
                D(ResetStateKey.TitanRunKillCounters, ResetStateClass.ResetLocalStock, clear, clear, preserve, clear,
                    ResetEffectKind.ReplaceWithResolvedValue,
                    "Ordinary reset clears run counters; Titan reward changes only its applicable counter."),
                D(ResetStateKey.AdvancedTrainingTemporary, ResetStateClass.BankedTemporaryStock,
                    ResetEffectKind.BankThenClear, clear, preserve, clear, preserve,
                    "Native banking can seed the next run; challenge reset clears the temporary bank path."),
                D(ResetStateKey.AdvancedTrainingBank, ResetStateClass.ResetTimeConversion,
                    ResetEffectKind.ConvertToPersistentBeforeClear, clear, preserve, clear, preserve,
                    "Resolve eligible bank capture from the native perk/challenge state."),
                D(ResetStateKey.NguLevels, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve, preserve,
                    "NGU levels persist."),
                D(ResetStateKey.NguProgress, ResetStateClass.PersistentPartialProgress, preserve, preserve, preserve, preserve, preserve,
                    "Partial NGU progress persists."),
                D(ResetStateKey.NguAllocations, ResetStateClass.ActivationAllocationState,
                    ResetEffectKind.ClearAllocationPreserveProgress, clear, preserve,
                    ResetEffectKind.ClearAllocationPreserveProgress, preserve,
                    "Energy/Magic assignment clears without discarding levels/progress."),
                D(ResetStateKey.BeardPermanentTrimmings, ResetStateClass.ResetTimeConversion,
                    ResetEffectKind.ConvertToPersistentBeforeClear, preserve, preserve,
                    ResetEffectKind.ConvertToPersistentBeforeClear, preserve,
                    "Permanent trimmings are converted before ordinary Beard reset."),
                D(ResetStateKey.BeardTemporary, ResetStateClass.BankedTemporaryStock,
                    ResetEffectKind.BankThenClear, clear, preserve, clear, preserve,
                    "Temporary Beard levels/progress clear after conversion; eligible bank seeds need resolution."),
                D(ResetStateKey.BeardBank, ResetStateClass.ResetTimeConversion,
                    ResetEffectKind.ConvertToPersistentBeforeClear, clear, preserve, clear, preserve,
                    "Challenge reset clears eligible temporary Beard bank state."),
                D(ResetStateKey.DiggerMaximumLevels, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve, preserve,
                    "Purchased maximum levels persist."),
                D(ResetStateKey.DiggerActivations, ResetStateClass.ActivationAllocationState,
                    ResetEffectKind.ResetActivationPreserveMaximum, clear, preserve,
                    ResetEffectKind.ResetActivationPreserveMaximum, preserve,
                    "All active Diggers turn off; the selected set must be re-equipped."),
                D(ResetStateKey.HackLevels, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve, preserve,
                    "Hack levels persist."),
                D(ResetStateKey.HackProgress, ResetStateClass.PersistentPartialProgress, preserve, preserve, preserve, preserve, preserve,
                    "Partial Hack progress persists."),
                D(ResetStateKey.HackAllocations, ResetStateClass.ActivationAllocationState,
                    ResetEffectKind.ClearAllocationPreserveProgress, clear, preserve,
                    ResetEffectKind.ClearAllocationPreserveProgress, preserve,
                    "R3 assignment clears while level/progress remains."),
                D(ResetStateKey.WishLevels, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve, preserve,
                    "Wish levels persist."),
                D(ResetStateKey.WishProgress, ResetStateClass.PersistentPartialProgress, preserve, preserve, preserve, preserve, preserve,
                    "Partial Wish progress persists and is preemptible sunk state."),
                D(ResetStateKey.WishAllocations, ResetStateClass.ActivationAllocationState,
                    ResetEffectKind.ClearAllocationPreserveProgress, clear, preserve,
                    ResetEffectKind.ClearAllocationPreserveProgress, preserve,
                    "Wish Energy/Magic/R3 allocations clear."),
                D(ResetStateKey.YggdrasilTiersAndSeeds, ResetStateClass.PersistentStock, preserve, preserve, preserve, preserve, preserve,
                    "Fruit tiers, seeds, and permanent effects persist."),
                D(ResetStateKey.YggdrasilFruitTimers, ResetStateClass.ClockState,
                    ResetEffectKind.TransformByNativeFactor, challenge, preserve,
                    ResetEffectKind.TransformByNativeFactor, preserve,
                    "Resolve the native reset-factor transformation; do not zero timers generically."),
                D(ResetStateKey.MacGuffinLevels, ResetStateClass.ResetTimeConversion,
                    ResetEffectKind.ConvertToPersistentBeforeClear, challenge, preserve,
                    ResetEffectKind.ConvertToPersistentBeforeClear, preserve,
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

    internal enum ResetDifficulty
    {
        Unknown = -1,
        Normal = 0,
        Evil = 1,
        Sadistic = 2
    }

    internal sealed class ResetNumberSnapshot
    {
        internal double CurrentAttack = 1.0;
        internal double CurrentDefense = 1.0;
        internal double NextAttack = 1.0;
        internal double NextDefense = 1.0;
        internal double BossMultiplier = 1.0;
        internal double TimeMultiplier = 1.0;
        internal double OldBossMultiplier = 1.0;
        internal double OldTimeMultiplier = 1.0;

        internal ResetNumberSnapshot Clone()
        {
            return (ResetNumberSnapshot)MemberwiseClone();
        }

        internal bool AllExactlyOne
        {
            get
            {
                return CurrentAttack == 1.0 && CurrentDefense == 1.0
                       && NextAttack == 1.0 && NextDefense == 1.0
                       && BossMultiplier == 1.0 && TimeMultiplier == 1.0
                       && OldBossMultiplier == 1.0 && OldTimeMultiplier == 1.0;
            }
        }
    }

    /*
    TYPED HARD-DIFFICULTY TRANSITION

    The native start wrappers install the target before the common reset.  This snapshot keeps the
    state families whose ordering or persistence differs from an ordinary soft rebirth.  Values
    produced by native conversion formulas are explicit resolution inputs: this registry never
    guesses a Basic Training cap, Beard trimming, MacGuffin delta, AP award, or Ygg timer factor.
    */
    internal sealed class DifficultyResetSnapshot
    {
        internal ResetDifficulty CurrentDifficulty;
        internal ResetDifficulty NextDifficulty;
        internal ResetDifficulty NguLevelTrack;
        internal ResetDifficulty DifficultyObservedDuringConversions;
        internal ResetNumberSnapshot Number = new ResetNumberSnapshot();
        internal int BossId;
        internal int CurrentHighestBoss;
        internal int HighestBoss;
        internal int HighestHardBoss;
        internal int HighestSadisticBoss;
        internal double RebirthSeconds;
        internal long RebirthNumber;
        internal bool Achievement152;
        internal bool InChallenge;
        internal bool[] ChallengeFlags = new bool[11];
        internal double[] TitanClocks = new double[14];
        internal int[] TitanRunKillCounters = new int[14];
        internal long[] BasicTrainingCaps = new long[0];
        internal long[] BasicTrainingRunLevels = new long[0];
        internal long[] BasicTrainingAllocations = new long[0];
        internal double AdvancedTrainingTemporary;
        internal double AdvancedTrainingBank;
        internal double BeardPermanentTrimmings;
        internal double BeardTemporary;
        internal double BeardBank;
        internal double TimeMachineBank;
        internal double MacGuffinPersistentValue;
        internal double AdventurePoints;
        internal double CurrentGold;
        internal double CurrentBlood;
        internal double CurrentEnergy;
        internal double CurrentMagic;
        internal double CurrentResource3;
        internal double[] NguLevels = new double[0];
        internal double[] NguProgress = new double[0];
        internal double[] NguAllocations = new double[0];
        internal double[] HackLevels = new double[0];
        internal double[] HackProgress = new double[0];
        internal double[] HackAllocations = new double[0];
        internal double[] WishLevels = new double[0];
        internal double[] WishProgress = new double[0];
        internal double[] WishAllocations = new double[0];
        internal double[] YggdrasilFruitTimers = new double[0];
        internal string InventoryIdentity = string.Empty;
        internal string PersistentUnlockIdentity = string.Empty;
        internal string[] TransitionOrder = new string[0];

        internal DifficultyResetSnapshot Clone()
        {
            var copy = (DifficultyResetSnapshot)MemberwiseClone();
            copy.Number = Number == null ? null : Number.Clone();
            copy.ChallengeFlags = Clone(ChallengeFlags);
            copy.TitanClocks = Clone(TitanClocks);
            copy.TitanRunKillCounters = Clone(TitanRunKillCounters);
            copy.BasicTrainingCaps = Clone(BasicTrainingCaps);
            copy.BasicTrainingRunLevels = Clone(BasicTrainingRunLevels);
            copy.BasicTrainingAllocations = Clone(BasicTrainingAllocations);
            copy.NguLevels = Clone(NguLevels);
            copy.NguProgress = Clone(NguProgress);
            copy.NguAllocations = Clone(NguAllocations);
            copy.HackLevels = Clone(HackLevels);
            copy.HackProgress = Clone(HackProgress);
            copy.HackAllocations = Clone(HackAllocations);
            copy.WishLevels = Clone(WishLevels);
            copy.WishProgress = Clone(WishProgress);
            copy.WishAllocations = Clone(WishAllocations);
            copy.YggdrasilFruitTimers = Clone(YggdrasilFruitTimers);
            copy.TransitionOrder = Clone(TransitionOrder);
            return copy;
        }

        private static T[] Clone<T>(T[] value)
        {
            return value == null ? new T[0] : (T[])value.Clone();
        }
    }

    internal sealed class DifficultyResetResolution
    {
        internal long[] BasicTrainingCapsAfterCompression = new long[0];
        internal double BeardPermanentAfterConversion;
        internal double MacGuffinPersistentAfterConversion;
        internal double AdventurePointsAfterAward;
        internal double[] YggdrasilFruitTimersAfterFactor = new double[0];
    }

    internal static class DifficultyResetTransform
    {
        internal static DifficultyResetSnapshot Apply(DifficultyResetSnapshot source,
            ResetDifficulty target, DifficultyResetResolution resolved)
        {
            if (source == null || source.Number == null)
                throw new ArgumentNullException("source");
            if (resolved == null) throw new ArgumentNullException("resolved");
            if (!LegalForwardTransition(source.CurrentDifficulty, target))
                throw new InvalidOperationException("Only Normal-to-Evil and Evil-to-Sadistic are legal forward transitions.");
            if (source.RebirthNumber == long.MaxValue)
                throw new InvalidOperationException("The exact rebirth increment would overflow.");
            RequireFourteen(source.TitanClocks, "TitanClocks");
            RequireFourteen(source.TitanRunKillCounters, "TitanRunKillCounters");
            RequireSameLength(source.BasicTrainingCaps,
                resolved.BasicTrainingCapsAfterCompression, "BasicTrainingCapsAfterCompression");
            RequireSameLength(source.YggdrasilFruitTimers,
                resolved.YggdrasilFruitTimersAfterFactor, "YggdrasilFruitTimersAfterFactor");
            RequireFiniteNonNegative(resolved.BeardPermanentAfterConversion,
                "BeardPermanentAfterConversion");
            RequireFiniteNonNegative(resolved.MacGuffinPersistentAfterConversion,
                "MacGuffinPersistentAfterConversion");
            RequireFiniteNonNegative(resolved.AdventurePointsAfterAward,
                "AdventurePointsAfterAward");

            var result = source.Clone();
            // Target first: every following source-sensitive conversion observes this value.
            result.CurrentDifficulty = target;
            result.NextDifficulty = target;
            result.DifficultyObservedDuringConversions = result.CurrentDifficulty;
            result.TransitionOrder = new[]
            {
                "install-target-difficulty", "award-time-ap", "compress-basic-training-caps",
                "convert-beards", "convert-macguffins", "clear-common-run-state",
                "hard-reset-number-and-banks", "increment-rebirth"
            };

            result.AdventurePoints = resolved.AdventurePointsAfterAward;
            result.BasicTrainingCaps = Clone(resolved.BasicTrainingCapsAfterCompression);
            result.BeardPermanentTrimmings = resolved.BeardPermanentAfterConversion;
            result.MacGuffinPersistentValue = resolved.MacGuffinPersistentAfterConversion;
            result.YggdrasilFruitTimers = Clone(resolved.YggdrasilFruitTimersAfterFactor);

            result.BossId = 0;
            result.CurrentHighestBoss = 0;
            result.RebirthSeconds = 0.0;
            result.TitanClocks = new double[14];
            result.TitanRunKillCounters = new int[14];
            result.BasicTrainingRunLevels = new long[source.BasicTrainingRunLevels == null
                ? 0 : source.BasicTrainingRunLevels.Length];
            result.BasicTrainingAllocations = new long[source.BasicTrainingAllocations == null
                ? 0 : source.BasicTrainingAllocations.Length];
            result.CurrentGold = 0.0;
            result.CurrentBlood = 0.0;
            result.CurrentEnergy = 0.0;
            result.CurrentMagic = 0.0;
            result.CurrentResource3 = 0.0;
            result.NguAllocations = Zeros(source.NguAllocations);
            result.HackAllocations = Zeros(source.HackAllocations);
            result.WishAllocations = Zeros(source.WishAllocations);
            result.AdvancedTrainingTemporary = 0.0;
            result.AdvancedTrainingBank = 0.0;
            result.BeardTemporary = 0.0;
            result.BeardBank = 0.0;
            result.TimeMachineBank = 0.0;
            result.Number = ExactlyOneNumber();
            result.InChallenge = false;
            result.ChallengeFlags = new bool[11];
            result.RebirthNumber = source.RebirthNumber + 1L;
            if (target == ResetDifficulty.Evil)
            {
                if (result.NguLevelTrack > ResetDifficulty.Evil)
                    result.NguLevelTrack = ResetDifficulty.Evil;
                result.Achievement152 = true;
            }
            // Sadistic leaves the existing NGU track and achievement vector unchanged.
            return result;
        }

        internal static bool LegalForwardTransition(ResetDifficulty source,
            ResetDifficulty target)
        {
            return source == ResetDifficulty.Normal && target == ResetDifficulty.Evil
                   || source == ResetDifficulty.Evil && target == ResetDifficulty.Sadistic;
        }

        private static ResetNumberSnapshot ExactlyOneNumber()
        {
            return new ResetNumberSnapshot();
        }

        private static double[] Zeros(double[] source)
        {
            return new double[source == null ? 0 : source.Length];
        }

        private static T[] Clone<T>(T[] source)
        {
            return source == null ? new T[0] : (T[])source.Clone();
        }

        private static void RequireFourteen(Array values, string name)
        {
            if (values == null || values.Length != 14)
                throw new ArgumentException(name + " must contain exactly fourteen values.", name);
        }

        private static void RequireSameLength(Array before, Array after, string name)
        {
            if (before == null || after == null || before.Length != after.Length)
                throw new ArgumentException(name + " must resolve every source element.", name);
        }

        private static void RequireFiniteNonNegative(double value, string name)
        {
            if (value < 0.0 || double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name);
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
