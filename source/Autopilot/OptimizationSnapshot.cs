using System;
using System.Collections.Generic;
using System.Globalization;

/*
FILE PURPOSE

Purpose: OptimizationSnapshot is the immutable, Unity-free stationarity boundary for the global
progression scheduler. It gives task 28 one canonical identity/hash and typed observations for every
state family named by the global-scheduler audit, the sixteen physical END branches, and all eleven
challenge ledgers.

Mechanism: Integration supplies one opaque fingerprint for every OptimizationStateKey plus exact
typed gate facts. Constructors reject missing/duplicate keys and incomplete END/challenge arrays.
A deterministic FNV-1a snapshot hash is computed in enum/item order. Comparison returns typed
invalidation records; no label, objective prose, or telemetry string is ever parsed as strategy.

Inputs and outputs: Inputs are copied scalar records captured synchronously on the Unity thread.
Outputs are immutable clones, exact fact lookup, a stable state hash, and a complete typed diff.

Invariants and safety: Save, model, and objective hashes are independent. Exactly one ordinary END
copy proves terminal physical completion; recoverable or source-complete state does not. Every
required challenge must reach its declared completion target. Any named identity, state stamp,
physical END state, challenge ledger, or verified-END transition invalidates a leased plan.

Extension points and non-goals: Task 29 owns live capture and task 28 owns search/leases. Add a new
state key whenever a mechanic can alter legality, event rate, transition outcome, or the terminal
predicate. This file does not read Character, estimate ETAs, mutate controllers, or grant authority.
*/
namespace NGUInjector.Autopilot
{
    internal enum OptimizationDifficulty
    {
        Normal,
        Evil,
        Sadistic
    }

    internal enum OptimizationStateKey
    {
        Difficulty,
        ActiveChallenge,
        ChallengeLedger,
        RunAge,
        MinimumResetRule,
        NumberState,
        FightBossSelection,
        FightBossCombat,
        ResourceBalances,
        ResourcePowerCapsBars,
        ResourceAllocations,
        ResetLocalTracks,
        PersistentNgu,
        PersistentHacks,
        PersistentWishes,
        TitanClocks,
        FruitClocks,
        PitAndSpellClocks,
        DaycareClock,
        QuestClock,
        CardAndCookingClocks,
        AdventureMode,
        AdventureCombat,
        CollectionDebt,
        ItopodState,
        PermanentPurchases,
        ItemDiscoveryAndSets,
        EquipmentTopology,
        Macguffins,
        DiggerState,
        EndgameDependencies,
        StochasticCalibration,
        ExclusiveModes,
        PhysicalLoadout,
        WandoosOperatingSystem
    }

    internal sealed class OptimizationIdentity
    {
        internal readonly string SessionId;
        internal readonly string SaveHash;
        internal readonly string ModelHash;
        internal readonly string ObjectiveHash;

        internal OptimizationIdentity(string sessionId, string saveHash,
            string modelHash, string objectiveHash)
        {
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException(
                "session ID is required", "sessionId");
            if (string.IsNullOrEmpty(saveHash)) throw new ArgumentException(
                "save hash is required", "saveHash");
            if (string.IsNullOrEmpty(modelHash)) throw new ArgumentException(
                "model hash is required", "modelHash");
            if (string.IsNullOrEmpty(objectiveHash)) throw new ArgumentException(
                "objective hash is required", "objectiveHash");
            SessionId = sessionId;
            SaveHash = saveHash;
            ModelHash = modelHash;
            ObjectiveHash = objectiveHash;
        }
    }

    internal sealed class OptimizationStateStamp
    {
        internal readonly OptimizationStateKey Key;
        internal readonly string Fingerprint;

        internal OptimizationStateStamp(OptimizationStateKey key, string fingerprint)
        {
            if (!Enum.IsDefined(typeof(OptimizationStateKey), key))
                throw new ArgumentOutOfRangeException("key");
            if (string.IsNullOrEmpty(fingerprint))
                throw new ArgumentException("state fingerprint is required", "fingerprint");
            Key = key;
            Fingerprint = fingerprint;
        }
    }

    internal enum OptimizationFactKey
    {
        HighestSadisticBoss,
        Titan13Defeated,
        HacksZeroThroughFourteenCapped,
        EndHackLevel,
        Move69Unlocked,
        Move69Uses,
        Perk231Level,
        Quirk176Level,
        Wish203Level,
        ItopodHighestFloor,
        HeldEndCards,
        MayoZero,
        MayoOne,
        MayoTwo,
        MayoThree,
        MayoFour,
        MayoFive,
        Blood,
        UsableInventoryFreeSlots,
        OrdinaryInventoryCurrentSpaces,
        DeckFreeSlots,
        EndFiltersClear
    }

    internal sealed class OptimizationFact
    {
        internal readonly OptimizationFactKey Key;
        internal readonly double Value;

        internal OptimizationFact(OptimizationFactKey key, double value)
        {
            if (!Enum.IsDefined(typeof(OptimizationFactKey), key))
                throw new ArgumentOutOfRangeException("key");
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                throw new ArgumentOutOfRangeException("value");
            Key = key;
            Value = value;
        }
    }

    internal sealed class OptimizationFactSet
    {
        private readonly double[] _values;

        internal OptimizationFactSet(IEnumerable<OptimizationFact> source)
        {
            if (source == null) throw new ArgumentNullException("source");
            var keys = OptimizationSnapshot.AllFactKeys();
            _values = new double[keys.Length];
            var seen = new bool[keys.Length];
            foreach (var fact in source)
            {
                if (fact == null) throw new ArgumentException("fact records cannot be null");
                var index = (int)fact.Key;
                if (index < 0 || index >= keys.Length || seen[index])
                    throw new ArgumentException("facts must contain each typed key exactly once");
                seen[index] = true;
                _values[index] = fact.Value;
            }
            for (var i = 0; i < seen.Length; i++)
                if (!seen[i]) throw new ArgumentException(
                    "facts must contain every typed hard-gate observation");
        }

        internal double Get(OptimizationFactKey key)
        {
            if (!Enum.IsDefined(typeof(OptimizationFactKey), key))
                throw new ArgumentOutOfRangeException("key");
            return _values[(int)key];
        }

        internal bool IsTrue(OptimizationFactKey key)
        {
            return Get(key) >= 1.0;
        }

        internal OptimizationFact[] Snapshot()
        {
            var result = new OptimizationFact[_values.Length];
            for (var i = 0; i < _values.Length; i++)
                result[i] = new OptimizationFact((OptimizationFactKey)i, _values[i]);
            return result;
        }
    }

    internal sealed class OptimizationEndItemState
    {
        internal readonly int ItemId;
        internal readonly int OrdinaryCopies;
        internal readonly int RecoverableCopies;
        internal readonly bool SourceSatisfied;
        internal readonly bool PendingGrant;
        internal readonly bool RetryLegal;

        internal OptimizationEndItemState(int itemId, int ordinaryCopies,
            int recoverableCopies, bool sourceSatisfied, bool pendingGrant,
            bool retryLegal)
        {
            if (!MechanicsEndgame.IsProtectedItem(itemId))
                throw new ArgumentOutOfRangeException("itemId");
            if (ordinaryCopies < 0) throw new ArgumentOutOfRangeException("ordinaryCopies");
            if (recoverableCopies < 0) throw new ArgumentOutOfRangeException("recoverableCopies");
            ItemId = itemId;
            OrdinaryCopies = ordinaryCopies;
            RecoverableCopies = recoverableCopies;
            SourceSatisfied = sourceSatisfied;
            PendingGrant = pendingGrant;
            RetryLegal = retryLegal;
        }

        internal bool TerminalPiecePresent { get { return OrdinaryCopies == 1; } }
        internal bool HasRecoveryDebt
        {
            get { return OrdinaryCopies == 0 && RecoverableCopies > 0; }
        }
        internal bool NeedsDuplicateCleanup
        {
            get { return OrdinaryCopies > 1 || OrdinaryCopies + RecoverableCopies > 1; }
        }
    }

    internal enum OptimizationChallengeKind
    {
        Basic,
        NoAugments,
        TwentyFourHour,
        OneHundredLevel,
        NoEquipment,
        Troll,
        NoRebirth,
        LaserSword,
        Blind,
        NoNgu,
        NoTimeMachine
    }

    internal sealed class OptimizationChallengeState
    {
        internal readonly OptimizationChallengeKind Kind;
        internal readonly bool Required;
        internal readonly int Completed;
        internal readonly int RequiredCompletions;

        internal OptimizationChallengeState(OptimizationChallengeKind kind,
            bool required, int completed, int requiredCompletions)
        {
            if (!Enum.IsDefined(typeof(OptimizationChallengeKind), kind))
                throw new ArgumentOutOfRangeException("kind");
            if (completed < 0) throw new ArgumentOutOfRangeException("completed");
            if (requiredCompletions < 0)
                throw new ArgumentOutOfRangeException("requiredCompletions");
            if (required && requiredCompletions == 0)
                throw new ArgumentException("a required challenge needs a positive target");
            Kind = kind;
            Required = required;
            Completed = completed;
            RequiredCompletions = requiredCompletions;
        }

        internal bool Complete
        {
            get { return !Required || Completed >= RequiredCompletions; }
        }
    }

    internal enum OptimizationInvalidationKind
    {
        Session,
        SaveHash,
        ModelHash,
        ObjectiveHash,
        NamedState,
        HardGateFact,
        EndPhysicalOrSourceState,
        ChallengeLedger,
        EndSequence
    }

    internal sealed class OptimizationInvalidation
    {
        internal readonly OptimizationInvalidationKind Kind;
        internal readonly OptimizationStateKey StateKey;
        internal readonly OptimizationFactKey FactKey;
        internal readonly int ItemId;
        internal readonly OptimizationChallengeKind Challenge;

        internal OptimizationInvalidation(OptimizationInvalidationKind kind,
            OptimizationStateKey stateKey, int itemId,
            OptimizationChallengeKind challenge)
        {
            Kind = kind;
            StateKey = stateKey;
            FactKey = default(OptimizationFactKey);
            ItemId = itemId;
            Challenge = challenge;
        }

        internal OptimizationInvalidation(OptimizationFactKey factKey)
        {
            Kind = OptimizationInvalidationKind.HardGateFact;
            StateKey = default(OptimizationStateKey);
            FactKey = factKey;
            ItemId = -1;
            Challenge = default(OptimizationChallengeKind);
        }
    }

    internal sealed class OptimizationSnapshot
    {
        private readonly string[] _stateFingerprints;
        private readonly OptimizationEndItemState[] _endItems;
        private readonly OptimizationChallengeState[] _challenges;

        internal readonly long CaptureVersion;
        internal readonly OptimizationIdentity Identity;
        internal readonly OptimizationDifficulty Difficulty;
        internal readonly OptimizationFactSet Facts;
        internal readonly bool EndSequenceVerified;
        internal readonly string SnapshotHash;

        internal OptimizationSnapshot(long captureVersion, OptimizationIdentity identity,
            OptimizationDifficulty difficulty, IEnumerable<OptimizationStateStamp> stateStamps,
            OptimizationFactSet facts, IEnumerable<OptimizationEndItemState> endItems,
            IEnumerable<OptimizationChallengeState> challenges, bool endSequenceVerified)
        {
            if (captureVersion < 0L) throw new ArgumentOutOfRangeException("captureVersion");
            if (identity == null) throw new ArgumentNullException("identity");
            if (!Enum.IsDefined(typeof(OptimizationDifficulty), difficulty))
                throw new ArgumentOutOfRangeException("difficulty");
            if (stateStamps == null) throw new ArgumentNullException("stateStamps");
            if (facts == null) throw new ArgumentNullException("facts");
            CaptureVersion = captureVersion;
            Identity = identity;
            Difficulty = difficulty;
            Facts = facts;
            EndSequenceVerified = endSequenceVerified;

            var stateKeys = AllStateKeys();
            _stateFingerprints = new string[stateKeys.Length];
            var stateSeen = new bool[stateKeys.Length];
            foreach (var stamp in stateStamps)
            {
                if (stamp == null) throw new ArgumentException("state stamps cannot contain null");
                var index = (int)stamp.Key;
                if (index < 0 || index >= stateKeys.Length || stateSeen[index])
                    throw new ArgumentException("state stamps must contain each key exactly once");
                stateSeen[index] = true;
                _stateFingerprints[index] = stamp.Fingerprint;
            }
            RequireComplete(stateSeen, "state stamps");

            _endItems = CopyEndItems(endItems);
            _challenges = CopyChallenges(challenges);
            SnapshotHash = ComputeHash();
        }

        internal static OptimizationStateKey[] AllStateKeys()
        {
            return (OptimizationStateKey[])Enum.GetValues(typeof(OptimizationStateKey));
        }

        internal static OptimizationFactKey[] AllFactKeys()
        {
            return (OptimizationFactKey[])Enum.GetValues(typeof(OptimizationFactKey));
        }

        internal static OptimizationChallengeKind[] AllChallengeKinds()
        {
            return (OptimizationChallengeKind[])Enum.GetValues(
                typeof(OptimizationChallengeKind));
        }

        internal string StateFingerprint(OptimizationStateKey key)
        {
            if (!Enum.IsDefined(typeof(OptimizationStateKey), key))
                throw new ArgumentOutOfRangeException("key");
            return _stateFingerprints[(int)key];
        }

        internal OptimizationEndItemState EndItem(int itemId)
        {
            if (!MechanicsEndgame.IsProtectedItem(itemId))
                throw new ArgumentOutOfRangeException("itemId");
            return _endItems[itemId - MechanicsEndgame.FirstEndItemId];
        }

        internal OptimizationEndItemState[] EndItems()
        {
            return (OptimizationEndItemState[])_endItems.Clone();
        }

        internal OptimizationChallengeState Challenge(OptimizationChallengeKind kind)
        {
            if (!Enum.IsDefined(typeof(OptimizationChallengeKind), kind))
                throw new ArgumentOutOfRangeException("kind");
            return _challenges[(int)kind];
        }

        internal OptimizationChallengeState[] Challenges()
        {
            return (OptimizationChallengeState[])_challenges.Clone();
        }

        internal bool TerminalSatisfied
        {
            get
            {
                if (!EndSequenceVerified) return false;
                for (var i = 0; i < _endItems.Length; i++)
                    if (!_endItems[i].TerminalPiecePresent) return false;
                for (var i = 0; i < _challenges.Length; i++)
                    if (!_challenges[i].Complete) return false;
                return true;
            }
        }

        internal OptimizationInvalidation[] InvalidationsComparedTo(
            OptimizationSnapshot current)
        {
            if (current == null) throw new ArgumentNullException("current");
            var result = new List<OptimizationInvalidation>();
            if (!string.Equals(Identity.SessionId, current.Identity.SessionId,
                    StringComparison.Ordinal)) AddIdentity(result, OptimizationInvalidationKind.Session);
            if (!string.Equals(Identity.SaveHash, current.Identity.SaveHash,
                    StringComparison.Ordinal)) AddIdentity(result, OptimizationInvalidationKind.SaveHash);
            if (!string.Equals(Identity.ModelHash, current.Identity.ModelHash,
                    StringComparison.Ordinal)) AddIdentity(result, OptimizationInvalidationKind.ModelHash);
            if (!string.Equals(Identity.ObjectiveHash, current.Identity.ObjectiveHash,
                    StringComparison.Ordinal)) AddIdentity(result, OptimizationInvalidationKind.ObjectiveHash);
            if (Difficulty != current.Difficulty
                && string.Equals(
                    _stateFingerprints[(int)OptimizationStateKey.Difficulty],
                    current._stateFingerprints[(int)OptimizationStateKey.Difficulty],
                    StringComparison.Ordinal))
                result.Add(new OptimizationInvalidation(OptimizationInvalidationKind.NamedState,
                    OptimizationStateKey.Difficulty, -1, default(OptimizationChallengeKind)));
            for (var i = 0; i < _stateFingerprints.Length; i++)
                if (!string.Equals(_stateFingerprints[i], current._stateFingerprints[i],
                        StringComparison.Ordinal))
                    result.Add(new OptimizationInvalidation(
                        OptimizationInvalidationKind.NamedState,
                        (OptimizationStateKey)i, -1,
                        default(OptimizationChallengeKind)));
            foreach (var key in AllFactKeys())
                if (Facts.Get(key) != current.Facts.Get(key))
                    result.Add(new OptimizationInvalidation(key));
            for (var i = 0; i < _endItems.Length; i++)
                if (!Same(_endItems[i], current._endItems[i]))
                    result.Add(new OptimizationInvalidation(
                        OptimizationInvalidationKind.EndPhysicalOrSourceState,
                        default(OptimizationStateKey), _endItems[i].ItemId,
                        default(OptimizationChallengeKind)));
            for (var i = 0; i < _challenges.Length; i++)
                if (!Same(_challenges[i], current._challenges[i]))
                    result.Add(new OptimizationInvalidation(
                        OptimizationInvalidationKind.ChallengeLedger,
                        default(OptimizationStateKey), -1, _challenges[i].Kind));
            if (EndSequenceVerified != current.EndSequenceVerified)
                result.Add(new OptimizationInvalidation(
                    OptimizationInvalidationKind.EndSequence,
                    default(OptimizationStateKey), -1,
                    default(OptimizationChallengeKind)));
            return result.ToArray();
        }

        internal bool CanReusePlanFor(OptimizationSnapshot current)
        {
            return current != null && InvalidationsComparedTo(current).Length == 0;
        }

        private OptimizationEndItemState[] CopyEndItems(
            IEnumerable<OptimizationEndItemState> source)
        {
            if (source == null) throw new ArgumentNullException("endItems");
            var result = new OptimizationEndItemState[
                MechanicsEndgame.LastEndItemId - MechanicsEndgame.FirstEndItemId + 1];
            foreach (var item in source)
            {
                if (item == null) throw new ArgumentException("END item states cannot contain null");
                var index = item.ItemId - MechanicsEndgame.FirstEndItemId;
                if (result[index] != null)
                    throw new ArgumentException("END item states cannot contain duplicates");
                result[index] = item;
            }
            for (var i = 0; i < result.Length; i++)
                if (result[i] == null) throw new ArgumentException(
                    "snapshot requires every END item state from 480 through 495");
            return result;
        }

        private OptimizationChallengeState[] CopyChallenges(
            IEnumerable<OptimizationChallengeState> source)
        {
            if (source == null) throw new ArgumentNullException("challenges");
            var kinds = AllChallengeKinds();
            var result = new OptimizationChallengeState[kinds.Length];
            foreach (var challenge in source)
            {
                if (challenge == null)
                    throw new ArgumentException("challenge states cannot contain null");
                var index = (int)challenge.Kind;
                if (result[index] != null)
                    throw new ArgumentException("challenge states cannot contain duplicates");
                result[index] = challenge;
            }
            for (var i = 0; i < result.Length; i++)
                if (result[i] == null) throw new ArgumentException(
                    "snapshot requires all eleven typed challenge ledgers");
            return result;
        }

        private string ComputeHash()
        {
            var hash = 14695981039346656037UL;
            AddHash(ref hash, Identity.SessionId);
            AddHash(ref hash, Identity.SaveHash);
            AddHash(ref hash, Identity.ModelHash);
            AddHash(ref hash, Identity.ObjectiveHash);
            AddHash(ref hash, ((int)Difficulty).ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < _stateFingerprints.Length; i++)
            {
                AddHash(ref hash, i.ToString(CultureInfo.InvariantCulture));
                AddHash(ref hash, _stateFingerprints[i]);
            }
            foreach (var fact in Facts.Snapshot())
            {
                AddHash(ref hash, ((int)fact.Key).ToString(CultureInfo.InvariantCulture));
                AddHash(ref hash, fact.Value.ToString("R", CultureInfo.InvariantCulture));
            }
            for (var i = 0; i < _endItems.Length; i++)
            {
                var item = _endItems[i];
                AddHash(ref hash, item.ItemId.ToString(CultureInfo.InvariantCulture));
                AddHash(ref hash, item.OrdinaryCopies.ToString(CultureInfo.InvariantCulture));
                AddHash(ref hash, item.RecoverableCopies.ToString(CultureInfo.InvariantCulture));
                AddHash(ref hash, item.SourceSatisfied ? "1" : "0");
                AddHash(ref hash, item.PendingGrant ? "1" : "0");
                AddHash(ref hash, item.RetryLegal ? "1" : "0");
            }
            for (var i = 0; i < _challenges.Length; i++)
            {
                var challenge = _challenges[i];
                AddHash(ref hash, ((int)challenge.Kind).ToString(CultureInfo.InvariantCulture));
                AddHash(ref hash, challenge.Required ? "1" : "0");
                AddHash(ref hash, challenge.Completed.ToString(CultureInfo.InvariantCulture));
                AddHash(ref hash, challenge.RequiredCompletions.ToString(CultureInfo.InvariantCulture));
            }
            AddHash(ref hash, EndSequenceVerified ? "1" : "0");
            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        private static void AddHash(ref ulong hash, string value)
        {
            var text = value ?? string.Empty;
            for (var i = 0; i < text.Length; i++)
            {
                hash ^= (byte)(text[i] & 0xff);
                hash *= 1099511628211UL;
                hash ^= (byte)((text[i] >> 8) & 0xff);
                hash *= 1099511628211UL;
            }
            hash ^= 0xff;
            hash *= 1099511628211UL;
        }

        private static void RequireComplete(bool[] seen, string name)
        {
            for (var i = 0; i < seen.Length; i++)
                if (!seen[i]) throw new ArgumentException(
                    name + " must contain every enum value exactly once");
        }

        private static void AddIdentity(ICollection<OptimizationInvalidation> result,
            OptimizationInvalidationKind kind)
        {
            result.Add(new OptimizationInvalidation(kind,
                default(OptimizationStateKey), -1,
                default(OptimizationChallengeKind)));
        }

        private static bool Same(OptimizationEndItemState left,
            OptimizationEndItemState right)
        {
            return left.ItemId == right.ItemId
                   && left.OrdinaryCopies == right.OrdinaryCopies
                   && left.RecoverableCopies == right.RecoverableCopies
                   && left.SourceSatisfied == right.SourceSatisfied
                   && left.PendingGrant == right.PendingGrant
                   && left.RetryLegal == right.RetryLegal;
        }

        private static bool Same(OptimizationChallengeState left,
            OptimizationChallengeState right)
        {
            return left.Kind == right.Kind && left.Required == right.Required
                   && left.Completed == right.Completed
                   && left.RequiredCompletions == right.RequiredCompletions;
        }
    }
}
