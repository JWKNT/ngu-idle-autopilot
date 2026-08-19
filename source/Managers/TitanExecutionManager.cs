using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

Purpose: TitanExecutionManager is the event-driven prestage and execution coordinator for Titans
1-14.  It converts the pure Titan oracle, exact ordinary capacity, task-11 combat action state, and
task-14 physical loadout objective into a pre-due commitment that cannot lose the native
clock-crossing frame.

Mechanism: Before any target clock is due, the manager records exact selected-version Bestiary/reward
counters and desired versions, disables native autokill, selects versions, stages one common
strongest exact-reference loadout, and proves aggregate unswept capacity.  It never releases native
autokill into the Unity update loop.  Instead one irreversible action temporarily enables the
setting only around one synchronous build-pinned AdventureController.manageFight call; native order
therefore consumes at most one due T1-T12, and the transaction proves the exact target counter,
clock reset, and absence of every other Titan delta before committing. T13/T14 use the same physical
staging commitment, typed zone entry, task-11's reserved lethal first move, and durable reward
evidence before cleanup. Walderp and manual Glop/Apathy remain typed holds/delegations.

Inputs and outputs: Inputs are immutable TitanExecutionSnapshot objects, ordinary inventory
topology, task-1 root transactions, and a build-pinned ITitanExecutionRuntime.  Outputs are typed
plans, capacity proofs, mutation results, online loot projections, offline bestiary/timer
projections, and a deterministic same-frame native-update fixture.  LoadoutManager supplies the
physical staging hook; integration supplies clocks, versions, native-predicate reads, and bindings.

Invariants and safety: Ready-only callbacks are never considered staging. A kill atom requires exact
versions, a staged loadout, current aggregate capacity, a due target, and live native verification.
Persistent titan1Kills-style offline counters are never valid online evidence: T1-T12 runtime
snapshots must expose selected-version Bestiary kills; T13/T14 expose rat/item-495 reward evidence.
T12 online v4 contains all four cumulative END opportunities; offline progress
calls no loot path.  Manual Glop copies are ceil(enemy actions/5); a native autokill consumes none.
T13 stops after the rat flag.  T14 remains actionable after its flag while ordinary item 495 is
missing. Reset policy must honor ResetInterlock: a due source-proven executable Titan or any active staged/kill
commitment blocks reset until its clock/reward and loadout cleanup are observed. A merely due clock
without a source-proven native/manual execution path is an explicit rebirth cost, not an interlock;
after one strongest-loadout test fails, its commitment restores gear and abandons cleanly.

Extension points and non-goals: Task 29 wires the runtime, epoch cancellation, scheduler cadence,
and telemetry.  Additional exact per-Titan batch bounds may be supplied in opportunity snapshots.
This file does not reflect native members, choose equipment objects, solve the global route, infer
kills from intended calls, mutate filters, run END, inject, restart, or credit offline item drops.
*/
namespace NGUInjector.Managers
{
    internal sealed class TitanLoadoutStageRequest
    {
        private readonly int[] _titanIds;
        private readonly int[] _versions;
        private readonly int[] _configuredItemIds;

        internal readonly string StageId;
        internal readonly bool ValuesGold;

        internal TitanLoadoutStageRequest(string stageId, int[] titanIds, int[] versions,
            int[] configuredItemIds, bool valuesGold)
        {
            if (string.IsNullOrEmpty(stageId)) throw new ArgumentException("stage ID required", "stageId");
            if (titanIds == null || versions == null || titanIds.Length == 0
                || titanIds.Length != versions.Length)
                throw new ArgumentException("Titan IDs and versions must be nonempty and aligned.");
            _titanIds = (int[])titanIds.Clone();
            _versions = (int[])versions.Clone();
            for (var i = 0; i < _titanIds.Length; i++)
            {
                TitanMechanics.ValidateTitanId(_titanIds[i]);
                if (_versions[i] < 0 || _versions[i] > 3)
                    throw new ArgumentOutOfRangeException("versions");
                if (i > 0 && _titanIds[i] <= _titanIds[i - 1])
                    throw new ArgumentException("Titan stage order must be strictly ascending.");
            }
            if (configuredItemIds != null && configuredItemIds.Any(x => x < 0))
                throw new ArgumentOutOfRangeException("configuredItemIds");
            StageId = stageId;
            ValuesGold = valuesGold;
            _configuredItemIds = configuredItemIds == null
                ? new int[0] : (int[])configuredItemIds.Clone();
        }

        internal int[] TitanIds() { return (int[])_titanIds.Clone(); }
        internal int[] Versions() { return (int[])_versions.Clone(); }
        internal int[] ConfiguredItemIds() { return (int[])_configuredItemIds.Clone(); }
    }

    internal sealed class TitanLoadoutStageResult
    {
        internal readonly bool Satisfied;
        internal readonly string StageId;
        internal readonly string PhysicalFingerprint;
        internal readonly string Reason;

        internal TitanLoadoutStageResult(bool satisfied, string stageId,
            string physicalFingerprint, string reason)
        {
            Satisfied = satisfied;
            StageId = stageId ?? string.Empty;
            PhysicalFingerprint = physicalFingerprint ?? string.Empty;
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class WalderpExecutionSnapshot
    {
        internal readonly int Finds;
        internal readonly int Defeats;
        internal readonly bool InResponseLoop;
        internal readonly int RequestedMove;
        internal readonly bool WalderpSays;
        internal readonly bool RegularReady;
        internal readonly bool StrongReady;
        internal readonly bool PierceReady;
        internal readonly bool UltimateReady;

        internal WalderpExecutionSnapshot(int finds, int defeats, bool inResponseLoop,
            int requestedMove, bool walderpSays, bool regularReady, bool strongReady,
            bool pierceReady, bool ultimateReady)
        {
            if (finds < 0 || defeats < 0) throw new ArgumentOutOfRangeException("finds");
            Finds = finds;
            Defeats = defeats;
            InResponseLoop = inResponseLoop;
            RequestedMove = requestedMove;
            WalderpSays = walderpSays;
            RegularReady = regularReady;
            StrongReady = strongReady;
            PierceReady = pierceReady;
            UltimateReady = ultimateReady;
        }
    }

    internal sealed class TitanExecutionOpportunity
    {
        internal readonly int TitanId;
        internal readonly int CurrentVersion;
        internal readonly int DesiredVersion;
        internal readonly TitanClockProjection Clock;
        internal readonly bool Unlocked;
        internal readonly bool RewardActionable;
        internal readonly bool CandidateAutokillProjected;
        internal readonly bool NativeAutokillVerified;
        internal readonly bool ManualFightReady;
        internal readonly bool TerminalLethalMoveReserved;
        internal readonly TitanManualPrerequisiteProjection ManualPrerequisites;
        internal readonly int KillCount;
        internal readonly int WorstCaseTransientSlots;

        internal TitanExecutionOpportunity(int titanId, int currentVersion, int desiredVersion,
            TitanClockProjection clock, bool unlocked, bool rewardActionable,
            bool candidateAutokillProjected, bool nativeAutokillVerified,
            bool manualFightReady, bool terminalLethalMoveReserved,
            TitanManualPrerequisiteProjection manualPrerequisites, int killCount,
            int worstCaseTransientSlots)
        {
            TitanMechanics.ValidateTitanId(titanId);
            if (currentVersion < 0 || currentVersion > 3)
                throw new ArgumentOutOfRangeException("currentVersion");
            if (desiredVersion < 0 || desiredVersion > 3)
                throw new ArgumentOutOfRangeException("desiredVersion");
            if (clock == null || clock.TitanId != titanId)
                throw new ArgumentException("clock must describe the same Titan", "clock");
            if (manualPrerequisites == null || manualPrerequisites.TitanId != titanId)
                throw new ArgumentException("manual proof must describe the same Titan",
                    "manualPrerequisites");
            if (killCount < 0) throw new ArgumentOutOfRangeException("killCount");
            if (worstCaseTransientSlots < 0)
                throw new ArgumentOutOfRangeException("worstCaseTransientSlots");
            TitanId = titanId;
            CurrentVersion = currentVersion;
            DesiredVersion = desiredVersion;
            Clock = clock;
            Unlocked = unlocked;
            RewardActionable = rewardActionable;
            CandidateAutokillProjected = candidateAutokillProjected;
            NativeAutokillVerified = nativeAutokillVerified;
            ManualFightReady = manualFightReady;
            TerminalLethalMoveReserved = terminalLethalMoveReserved;
            ManualPrerequisites = manualPrerequisites;
            KillCount = killCount;
            WorstCaseTransientSlots = worstCaseTransientSlots;
        }

        internal bool WithinLead(double leadSeconds)
        {
            return Unlocked && RewardActionable && Clock.HasWallClockEta
                   && Clock.WallClockEtaSeconds <= leadSeconds;
        }
    }

    internal sealed class TitanExecutionSnapshot
    {
        private readonly TitanExecutionOpportunity[] _opportunities;
        internal readonly string Epoch;
        internal readonly bool Online;
        internal readonly bool AutoKillEnabled;
        internal readonly string LoadoutStageId;
        internal readonly bool ExactBindingsAvailable;
        internal readonly WalderpExecutionSnapshot Walderp;
        internal readonly int CurrentZone;
        internal readonly bool CurrentEnemyIsTargetTitan;

        internal TitanExecutionSnapshot(string epoch, bool online, bool autoKillEnabled,
            string loadoutStageId, bool exactBindingsAvailable,
            IEnumerable<TitanExecutionOpportunity> opportunities,
            WalderpExecutionSnapshot walderp, int currentZone = -1,
            bool currentEnemyIsTargetTitan = false)
        {
            if (string.IsNullOrEmpty(epoch)) throw new ArgumentException("epoch required", "epoch");
            if (opportunities == null) throw new ArgumentNullException("opportunities");
            _opportunities = opportunities.OrderBy(x => x.TitanId).ToArray();
            if (_opportunities.Any(x => x == null)
                || _opportunities.Select(x => x.TitanId).Distinct().Count() != _opportunities.Length)
                throw new ArgumentException("opportunities must have unique Titan IDs");
            Epoch = epoch;
            Online = online;
            AutoKillEnabled = autoKillEnabled;
            LoadoutStageId = loadoutStageId ?? string.Empty;
            ExactBindingsAvailable = exactBindingsAvailable;
            Walderp = walderp;
            CurrentZone = currentZone;
            CurrentEnemyIsTargetTitan = currentEnemyIsTargetTitan;
        }

        internal TitanExecutionOpportunity[] Opportunities()
        {
            return (TitanExecutionOpportunity[])_opportunities.Clone();
        }

        internal TitanExecutionOpportunity Find(int titanId)
        {
            for (var i = 0; i < _opportunities.Length; i++)
                if (_opportunities[i].TitanId == titanId) return _opportunities[i];
            return null;
        }

        internal string Fingerprint()
        {
            return Epoch + "|online=" + Online + "|ak=" + AutoKillEnabled
                   + "|stage=" + LoadoutStageId + "|zone=" + CurrentZone
                   + "|target=" + CurrentEnemyIsTargetTitan + "|" + string.Join(";",
                       _opportunities.Select(x => x.TitanId + ":v" + x.CurrentVersion
                           + ":k" + x.KillCount + ":r" + x.Clock.ArithmeticRemainingSeconds
                           + ":n" + x.NativeAutokillVerified).ToArray());
        }
    }

    internal enum TitanExecutionActionKind
    {
        Idle,
        Hold,
        DisableAutokill,
        SelectVersion,
        StageLoadout,
        HoldNativeAutokillVerification,
        ReleaseAutokill,
        KillOneDueTitan,
        AwaitCommittedKills,
        RestoreLoadout,
        RestoreAutokillPreference,
        CommitmentComplete,
        AwaitWalderpFind,
        WalderpResponse,
        EnterManualTitan,
        AwaitManualTitanKill,
        OfflinePreselectVersion
    }

    internal sealed class TitanExecutionAction
    {
        private readonly int[] _titanIds;
        internal readonly TitanExecutionActionKind Kind;
        internal readonly string CommitmentId;
        internal readonly int TitanId;
        internal readonly int Version;
        internal readonly int WalderpMove;
        internal readonly bool TargetAutokillValue;
        internal readonly bool LiveMutationAuthorized;
        internal readonly LootCapacityProof Capacity;
        internal readonly TitanLoadoutStageRequest LoadoutRequest;
        internal readonly string BeforeFingerprint;
        internal readonly string Reason;

        internal TitanExecutionAction(TitanExecutionActionKind kind, string commitmentId,
            int titanId, int version, int walderpMove, bool targetAutokillValue,
            bool liveMutationAuthorized, LootCapacityProof capacity,
            TitanLoadoutStageRequest loadoutRequest, string beforeFingerprint,
            string reason, int[] titanIds)
        {
            Kind = kind;
            CommitmentId = commitmentId ?? string.Empty;
            TitanId = titanId;
            Version = version;
            WalderpMove = walderpMove;
            TargetAutokillValue = targetAutokillValue;
            LiveMutationAuthorized = liveMutationAuthorized;
            Capacity = capacity;
            LoadoutRequest = loadoutRequest;
            BeforeFingerprint = beforeFingerprint ?? string.Empty;
            Reason = reason ?? string.Empty;
            _titanIds = titanIds == null ? new int[0] : (int[])titanIds.Clone();
        }

        internal int[] TitanIds() { return (int[])_titanIds.Clone(); }

        internal bool IsMutation
        {
            get
            {
                return Kind == TitanExecutionActionKind.DisableAutokill
                       || Kind == TitanExecutionActionKind.SelectVersion
                       || Kind == TitanExecutionActionKind.StageLoadout
                       || Kind == TitanExecutionActionKind.ReleaseAutokill
                       || Kind == TitanExecutionActionKind.KillOneDueTitan
                       || Kind == TitanExecutionActionKind.EnterManualTitan
                       || Kind == TitanExecutionActionKind.RestoreLoadout
                       || Kind == TitanExecutionActionKind.RestoreAutokillPreference;
            }
        }
    }

    internal sealed class TitanExecutionCommitment
    {
        private readonly int[] _titanIds;
        private readonly int[] _versions;
        private readonly int[] _killCountsBefore;
        internal readonly string Id;
        internal readonly string Epoch;
        internal readonly bool AutoKillWasEnabled;
        internal readonly int RequiredTransientSlots;
        internal readonly bool ManualExecution;
        internal bool Abandoned { get; private set; }

        internal TitanExecutionCommitment(string id, string epoch, bool autoKillWasEnabled,
            int[] titanIds, int[] versions, int[] killCountsBefore,
            int requiredTransientSlots)
        {
            Id = id;
            Epoch = epoch;
            AutoKillWasEnabled = autoKillWasEnabled;
            _titanIds = (int[])titanIds.Clone();
            _versions = (int[])versions.Clone();
            _killCountsBefore = (int[])killCountsBefore.Clone();
            RequiredTransientSlots = requiredTransientSlots;
            ManualExecution = _titanIds.Any(x => x >= 13);
            Abandoned = false;
        }

        internal int[] TitanIds() { return (int[])_titanIds.Clone(); }
        internal int[] Versions() { return (int[])_versions.Clone(); }
        internal int KillCountBefore(int index) { return _killCountsBefore[index]; }
        internal void MarkAbandoned() { Abandoned = true; }
    }

    internal sealed class TitanResetInterlock
    {
        internal readonly bool HoldReset;
        internal readonly int TitanId;
        internal readonly string Reason;

        internal TitanResetInterlock(bool holdReset, int titanId, string reason)
        {
            HoldReset = holdReset;
            TitanId = titanId;
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class TitanOnlineLootProjection
    {
        private readonly int[] _endItems;
        internal readonly int TitanId;
        internal readonly int Version;
        internal readonly bool CallsNativeLoot;
        internal readonly bool EquipmentEmissionPossible;

        internal TitanOnlineLootProjection(int titanId, int version, bool callsNativeLoot,
            bool equipmentEmissionPossible, int[] endItems)
        {
            TitanId = titanId;
            Version = version;
            CallsNativeLoot = callsNativeLoot;
            EquipmentEmissionPossible = equipmentEmissionPossible;
            _endItems = endItems == null ? new int[0] : (int[])endItems.Clone();
        }

        internal int[] CumulativeEndItems() { return (int[])_endItems.Clone(); }
    }

    internal sealed class TitanOfflineProjection
    {
        internal readonly int TitanId;
        internal readonly int SelectedVersion;
        internal readonly long CreditedKills;
        internal readonly long SelectedVersionBestiaryAfter;
        internal readonly double ClockElapsedAfter;
        internal readonly bool CallsNativeLoot;
        internal readonly bool EquipmentEmissionPossible;

        internal TitanOfflineProjection(int titanId, int selectedVersion, long creditedKills,
            long selectedVersionBestiaryAfter, double clockElapsedAfter)
        {
            TitanId = titanId;
            SelectedVersion = selectedVersion;
            CreditedKills = creditedKills;
            SelectedVersionBestiaryAfter = selectedVersionBestiaryAfter;
            ClockElapsedAfter = clockElapsedAfter;
            CallsNativeLoot = false;
            EquipmentEmissionPossible = false;
        }
    }

    internal sealed class TitanExecutionApplyResult
    {
        internal readonly bool InvocationAttempted;
        internal readonly bool NativeReturnedNormally;
        internal readonly string Detail;

        internal TitanExecutionApplyResult(bool invocationAttempted, string detail,
            bool nativeReturnedNormally = true)
        {
            InvocationAttempted = invocationAttempted;
            NativeReturnedNormally = nativeReturnedNormally;
            Detail = detail ?? string.Empty;
        }
    }

    internal interface ITitanExecutionRuntime
    {
        bool LiveAuthority { get; }
        string BindingId(TitanExecutionAction action);
        bool BindingAvailable(TitanExecutionAction action);
        TitanExecutionSnapshot Capture();
        OrdinaryInventoryTopology CaptureOrdinaryTopology();
        TitanExecutionApplyResult Apply(TitanExecutionAction action,
            RootTransactionToken token);
        CompensationResult Compensate(TitanExecutionAction action,
            TitanExecutionSnapshot before, RecoveryToken token);
    }

    /*
    LIVE TITAN EXECUTION RUNTIME

    This is the only production mutation port needed by TitanExecutionManager. Main supplies pure
    capture/topology callbacks and the existing LoadoutManager hooks; the port owns the build-pinned
    native adapters and the autoKillTitans synchronous guard. Every Apply must carry the currently
    owned root token, match the captured game epoch, run on the root's managed thread, and pass the
    caller's lease predicate. Recovery has a separate token predicate. This deliberately avoids a
    second scheduler or combat route: Main invokes ExecuteNext once from its existing one-second
    root, while CombatManager remains the sole owner of terminal-Titan attacks.

    The kill boundary raises autoKillTitans only inside try/finally around one private manageFight
    invocation and verifies that it is low again before returning. The coordinator independently
    recaptures selected-version Bestiary counters and clocks. Native normal return is material: each
    successful branch calls loot after resetting the clock and before returning. A reflection
    exception is therefore indeterminate and never automatically retried.
    */
    internal sealed class LiveTitanExecutionRuntime : ITitanExecutionRuntime
    {
        private readonly NativeBindingRegistry _registry;
        private readonly NativeMutationAdapters _native;
        private readonly object _adventureController;
        private readonly object _adventure;
        private readonly object _zoneSelector;
        private readonly Func<TitanExecutionSnapshot> _capture;
        private readonly Func<OrdinaryInventoryTopology> _captureTopology;
        private readonly Func<bool> _readAutokill;
        private readonly Action<bool> _writeAutokill;
        private readonly Func<TitanLoadoutStageRequest, TitanLoadoutStageResult> _stageLoadout;
        private readonly Func<string, TitanLoadoutStageResult> _captureLoadout;
        private readonly Func<string, TitanLoadoutStageResult> _restoreLoadout;
        private readonly Func<RootTransactionToken, bool> _ownsRootLease;
        private readonly Func<RecoveryToken, bool> _ownsRecoveryLease;
        private string _activeStageId;

        internal LiveTitanExecutionRuntime(NativeBindingRegistry registry,
            object adventureController, object adventure, object zoneSelector,
            Func<TitanExecutionSnapshot> capture,
            Func<OrdinaryInventoryTopology> captureTopology,
            Func<bool> readAutokill, Action<bool> writeAutokill,
            Func<TitanLoadoutStageRequest, TitanLoadoutStageResult> stageLoadout,
            Func<string, TitanLoadoutStageResult> captureLoadout,
            Func<string, TitanLoadoutStageResult> restoreLoadout,
            Func<RootTransactionToken, bool> ownsRootLease,
            Func<RecoveryToken, bool> ownsRecoveryLease)
        {
            if (registry == null) throw new ArgumentNullException("registry");
            if (adventureController == null) throw new ArgumentNullException("adventureController");
            if (adventure == null) throw new ArgumentNullException("adventure");
            if (zoneSelector == null) throw new ArgumentNullException("zoneSelector");
            if (capture == null || captureTopology == null || readAutokill == null
                || writeAutokill == null || stageLoadout == null || captureLoadout == null
                || restoreLoadout == null || ownsRootLease == null
                || ownsRecoveryLease == null)
                throw new ArgumentNullException("runtime callbacks");
            _registry = registry;
            _native = registry.CreateMutationAdapters();
            _adventureController = adventureController;
            _adventure = adventure;
            _zoneSelector = zoneSelector;
            _capture = capture;
            _captureTopology = captureTopology;
            _readAutokill = readAutokill;
            _writeAutokill = writeAutokill;
            _stageLoadout = stageLoadout;
            _captureLoadout = captureLoadout;
            _restoreLoadout = restoreLoadout;
            _ownsRootLease = ownsRootLease;
            _ownsRecoveryLease = ownsRecoveryLease;
            _activeStageId = string.Empty;
        }

        public bool LiveAuthority
        {
            get
            {
                return _registry.IsKnownBuild && _registry.IrreversibleActionsEnabled
                       && ExactBindingsAvailable;
            }
        }

        internal bool ExactBindingsAvailable
        {
            get
            {
                if (!_registry.HasBinding(NativeBindingKeys.TitanManageOneFrame)
                    || !_registry.HasBinding(NativeBindingKeys.TitanEnterZone)) return false;
                for (var titanId = 6; titanId <= 12; titanId++)
                    if (!_registry.HasBinding(NativeBindingKeys.TitanVersion(titanId))) return false;
                return true;
            }
        }

        public string BindingId(TitanExecutionAction action)
        {
            if (action == null) return string.Empty;
            switch (action.Kind)
            {
                case TitanExecutionActionKind.SelectVersion:
                    return action.TitanId >= 6 && action.TitanId <= 12
                        ? NativeBindingKeys.TitanVersion(action.TitanId) : string.Empty;
                case TitanExecutionActionKind.KillOneDueTitan:
                    return NativeBindingKeys.TitanManageOneFrame;
                case TitanExecutionActionKind.EnterManualTitan:
                    return NativeBindingKeys.TitanEnterZone;
                case TitanExecutionActionKind.StageLoadout:
                case TitanExecutionActionKind.RestoreLoadout:
                    return "loadout.titan-execution.exact-reference";
                case TitanExecutionActionKind.DisableAutokill:
                case TitanExecutionActionKind.RestoreAutokillPreference:
                    return "settings.auto-kill-titans.typed";
                default:
                    return string.Empty;
            }
        }

        public bool BindingAvailable(TitanExecutionAction action)
        {
            if (action == null || !LiveAuthority) return false;
            var key = BindingId(action);
            if (string.IsNullOrEmpty(key)) return false;
            if (action.Kind == TitanExecutionActionKind.SelectVersion
                || action.Kind == TitanExecutionActionKind.KillOneDueTitan
                || action.Kind == TitanExecutionActionKind.EnterManualTitan)
                return _registry.HasBinding(key);
            return true;
        }

        public TitanExecutionSnapshot Capture()
        {
            var raw = _capture();
            if (raw == null) throw new InvalidOperationException("Titan snapshot callback returned null");
            var stage = string.Empty;
            if (!string.IsNullOrEmpty(_activeStageId))
            {
                var physical = _captureLoadout(_activeStageId);
                if (physical != null && physical.Satisfied) stage = _activeStageId;
            }
            return new TitanExecutionSnapshot(raw.Epoch, raw.Online, _readAutokill(),
                stage, ExactBindingsAvailable, raw.Opportunities(), raw.Walderp,
                raw.CurrentZone, raw.CurrentEnemyIsTargetTitan);
        }

        public OrdinaryInventoryTopology CaptureOrdinaryTopology()
        {
            var topology = _captureTopology();
            if (topology == null)
                throw new InvalidOperationException("ordinary topology callback returned null");
            return topology;
        }

        public TitanExecutionApplyResult Apply(TitanExecutionAction action,
            RootTransactionToken token)
        {
            if (action == null) return Failed("Titan action is null");
            string leaseReason;
            if (!OwnsRoot(token, out leaseReason)) return Failed(leaseReason);
            switch (action.Kind)
            {
                case TitanExecutionActionKind.DisableAutokill:
                    _writeAutokill(false);
                    return Attempted("native autokill setting lowered");
                case TitanExecutionActionKind.RestoreAutokillPreference:
                    _writeAutokill(action.TargetAutokillValue);
                    return Attempted("native autokill preference restored");
                case TitanExecutionActionKind.SelectVersion:
                    return FromNative(_native.SelectTitanVersion(_adventure,
                        action.TitanId, action.Version));
                case TitanExecutionActionKind.StageLoadout:
                {
                    var result = _stageLoadout(action.LoadoutRequest);
                    if (result != null && result.Satisfied)
                        _activeStageId = action.CommitmentId;
                    return new TitanExecutionApplyResult(true,
                        result == null ? "loadout stage callback returned null" : result.Reason,
                        result != null && result.Satisfied);
                }
                case TitanExecutionActionKind.RestoreLoadout:
                {
                    var result = _restoreLoadout(action.CommitmentId);
                    if (result != null && result.Satisfied) _activeStageId = string.Empty;
                    return new TitanExecutionApplyResult(true,
                        result == null ? "loadout restore callback returned null" : result.Reason,
                        result != null && result.Satisfied);
                }
                case TitanExecutionActionKind.KillOneDueTitan:
                    return InvokeOneTitanFrameWithGuard();
                case TitanExecutionActionKind.EnterManualTitan:
                    return FromNative(_native.EnterTitanZone(_zoneSelector,
                        TitanMechanics.Describe(action.TitanId).Zone));
                default:
                    return Failed("action is not a live Titan mutation");
            }
        }

        public CompensationResult Compensate(TitanExecutionAction action,
            TitanExecutionSnapshot before, RecoveryToken token)
        {
            if (action == null || before == null)
                return CompensationResult.Failed("Titan compensation state is missing");
            if (token == null || token.RootTransactionId <= 0
                || token.CoordinatorId == Guid.Empty
                || !string.Equals(token.EpochFingerprint, before.Epoch,
                    StringComparison.Ordinal) || !_ownsRecoveryLease(token))
                return CompensationResult.Failed("Titan recovery token/epoch lease is not owned");
            try
            {
                switch (action.Kind)
                {
                    case TitanExecutionActionKind.DisableAutokill:
                    case TitanExecutionActionKind.RestoreAutokillPreference:
                        _writeAutokill(before.AutoKillEnabled);
                        return _readAutokill() == before.AutoKillEnabled
                            ? CompensationResult.Restored("autokill setting restored exactly")
                            : CompensationResult.Failed("autokill setting restoration did not verify");
                    case TitanExecutionActionKind.SelectVersion:
                    {
                        var prior = before.Find(action.TitanId);
                        var result = prior == null ? null : _native.SelectTitanVersion(_adventure,
                            action.TitanId, prior.CurrentVersion);
                        return result != null && result.ReturnedNormally
                            ? CompensationResult.Restored("Titan selected version restored exactly")
                            : CompensationResult.Failed("Titan selected version restoration failed");
                    }
                    case TitanExecutionActionKind.StageLoadout:
                    {
                        var result = _restoreLoadout(action.CommitmentId);
                        if (result != null && result.Satisfied) _activeStageId = string.Empty;
                        return result != null && result.Satisfied
                            ? CompensationResult.Restored(result.Reason)
                            : CompensationResult.Failed(result == null
                                ? "loadout restoration returned null" : result.Reason);
                    }
                    default:
                        return CompensationResult.NotSupported(
                            "irreversible Titan kill/entry or completed loadout restoration has no safe inverse");
                }
            }
            catch (Exception error)
            {
                return CompensationResult.Failed("Titan compensation threw: "
                    + error.GetType().Name);
            }
        }

        private bool OwnsRoot(RootTransactionToken token, out string reason)
        {
            reason = string.Empty;
            if (token == null || token.RootTransactionId <= 0
                || token.CoordinatorId == Guid.Empty)
            {
                reason = "Titan mutation requires a nonempty root transaction token";
                return false;
            }
            if (token.ManagedThreadId != System.Threading.Thread.CurrentThread.ManagedThreadId)
            {
                reason = "Titan mutation token is not owned by the current managed thread";
                return false;
            }
            var snapshot = Capture();
            if (!string.Equals(token.EpochFingerprint, snapshot.Epoch, StringComparison.Ordinal))
            {
                reason = "Titan mutation token epoch does not match the live snapshot";
                return false;
            }
            if (!_ownsRootLease(token))
            {
                reason = "Titan mutation root lease is not owned by the current scheduler root";
                return false;
            }
            return true;
        }

        private TitanExecutionApplyResult InvokeOneTitanFrameWithGuard()
        {
            if (_readAutokill())
                return Failed("typed Titan frame requires persistent autokill to be disabled");
            NativeInvocationResult result = null;
            Exception guardError = null;
            try
            {
                _writeAutokill(true);
                if (!_readAutokill())
                    return Failed("autokill guard could not be raised for the synchronous call");
                result = _native.InvokeOneTitanFrame(_adventureController);
            }
            catch (Exception error)
            {
                guardError = error;
            }
            finally
            {
                try { _writeAutokill(false); }
                catch (Exception error) { guardError = guardError ?? error; }
            }
            var restored = !_readAutokill();
            if (guardError != null)
                return new TitanExecutionApplyResult(true,
                    "synchronous Titan guard threw: " + guardError.GetType().Name,
                    false);
            if (result == null)
                return new TitanExecutionApplyResult(false,
                    "native Titan adapter returned no result", false);
            return new TitanExecutionApplyResult(result.InvocationAttempted,
                result.Reason + "; autokill-restored-low=" + restored,
                result.ReturnedNormally && restored);
        }

        private static TitanExecutionApplyResult FromNative(NativeInvocationResult result)
        {
            return result == null
                ? new TitanExecutionApplyResult(false, "native adapter returned null", false)
                : new TitanExecutionApplyResult(result.InvocationAttempted,
                    result.Reason, result.ReturnedNormally);
        }

        private static TitanExecutionApplyResult Attempted(string detail)
        {
            return new TitanExecutionApplyResult(true, detail, true);
        }

        private static TitanExecutionApplyResult Failed(string detail)
        {
            return new TitanExecutionApplyResult(false, detail, false);
        }
    }

    internal sealed class TitanExecutionResult
    {
        internal readonly TitanExecutionAction Action;
        internal readonly MutationResult<TitanExecutionSnapshot, TitanExecutionSnapshot> Mutation;
        internal readonly string Reason;

        internal TitanExecutionResult(TitanExecutionAction action,
            MutationResult<TitanExecutionSnapshot, TitanExecutionSnapshot> mutation,
            string reason)
        {
            Action = action;
            Mutation = mutation;
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class TitanExecutionManager
    {
        private readonly double _prestageLeadSeconds;
        private readonly int[] _configuredLoadoutItemIds;
        private readonly bool _valuesGold;
        private TitanExecutionCommitment _commitment;
        private bool _automaticAuthorityEnabled;
        private bool _terminalAuthorityEnabled;

        internal TitanExecutionManager(double prestageLeadSeconds,
            int[] configuredLoadoutItemIds = null, bool valuesGold = false)
        {
            if (double.IsNaN(prestageLeadSeconds) || double.IsInfinity(prestageLeadSeconds)
                || prestageLeadSeconds <= 0.0)
                throw new ArgumentOutOfRangeException("prestageLeadSeconds");
            _prestageLeadSeconds = prestageLeadSeconds;
            if (configuredLoadoutItemIds != null
                && configuredLoadoutItemIds.Any(x => x < 0))
                throw new ArgumentOutOfRangeException("configuredLoadoutItemIds");
            _configuredLoadoutItemIds = configuredLoadoutItemIds == null
                ? new int[0] : (int[])configuredLoadoutItemIds.Clone();
            _valuesGold = valuesGold;
        }

        internal TitanExecutionCommitment ActiveCommitment { get { return _commitment; } }
        internal bool LiveAuthorityEnabled
        {
            get { return _automaticAuthorityEnabled || _terminalAuthorityEnabled; }
        }

        internal void EnableSafeT1ThroughT12Authority(bool enabled)
        {
            _automaticAuthorityEnabled = enabled;
        }

        internal void EnableSafeT1ThroughT14Authority(bool enabled)
        {
            _automaticAuthorityEnabled = enabled;
            _terminalAuthorityEnabled = enabled;
        }

        internal void EnableSafeT13AndT14Authority(bool enabled)
        {
            _terminalAuthorityEnabled = enabled;
        }

        internal TitanResetInterlock ResetInterlock(TitanExecutionSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            if (_commitment != null)
                return new TitanResetInterlock(true, _commitment.TitanIds()[0],
                    "active Titan commitment must finish reward verification and physical loadout cleanup before reset");
            var due = snapshot.Opportunities().Where(x => snapshot.Online && x.Unlocked
                    && x.RewardActionable && x.Clock.Due
                    && (x.TitanId <= 12 ? x.NativeAutokillVerified
                        : x.ManualFightReady && x.ManualPrerequisites.Ready
                          && x.TerminalLethalMoveReserved))
                .OrderBy(x => x.TitanId).FirstOrDefault();
            return due == null
                ? new TitanResetInterlock(false, 0,
                    "no due source-proven executable Titan or active Titan execution commitment")
                : new TitanResetInterlock(true, due.TitanId,
                    "due actionable T" + due.TitanId + " must settle before reset");
        }

        internal TitanExecutionAction Plan(TitanExecutionSnapshot snapshot,
            OrdinaryInventoryTopology topology)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            if (topology == null) throw new ArgumentNullException("topology");
            if (_commitment != null && !string.Equals(_commitment.Epoch, snapshot.Epoch,
                    StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(snapshot.LoadoutStageId))
                    return Action(TitanExecutionActionKind.Hold, snapshot,
                        "epoch changed while an exact Titan loadout remains staged; lifecycle cancellation must restore it");
                _commitment = null;
            }
            if (_commitment != null && !snapshot.Online)
                return Action(TitanExecutionActionKind.Hold, snapshot,
                    "online Titan commitment crossed an offline boundary; lifecycle cancellation must restore staged state");
            if (_commitment != null) return PlanCommitment(snapshot, topology);

            if (!snapshot.Online)
            {
                var preselect = snapshot.Opportunities().Where(x => x.TitanId >= 6
                        && x.TitanId <= 12 && x.Unlocked && x.RewardActionable
                        && x.CurrentVersion != x.DesiredVersion)
                    .OrderByDescending(x => x.TitanId).FirstOrDefault();
                if (preselect == null)
                    return Action(TitanExecutionActionKind.Idle, snapshot,
                        "offline transition has no selected-version bootstrap change; equipment loot remains impossible");
                return new TitanExecutionAction(TitanExecutionActionKind.OfflinePreselectVersion,
                    string.Empty, preselect.TitanId, preselect.DesiredVersion, 0, false,
                    false, null, null, snapshot.Fingerprint(),
                    "preselect the intended version before a planned offline interval; offline uses only v1 qualification and emits no items",
                    new[] {preselect.TitanId});
            }

            var walderp = snapshot.Find(5);
            if (walderp != null && walderp.Unlocked && walderp.RewardActionable
                && snapshot.Walderp != null
                && (snapshot.Walderp.InResponseLoop
                    || TitanMechanics.IsWaldoClockPaused(snapshot.Walderp.Finds,
                        snapshot.Walderp.Defeats)))
                return PlanManual(snapshot, walderp, topology);

            var urgent = snapshot.Opportunities().Where(x => x.WithinLead(_prestageLeadSeconds))
                .OrderBy(x => x.Clock.WallClockEtaSeconds).ThenBy(x => x.TitanId).ToArray();
            if (urgent.Length == 0)
                return Action(TitanExecutionActionKind.Idle, snapshot,
                    "no actionable Titan is inside the measured prestage lead window");

            // The pre-stage candidate may be weaker than inventory's strongest combat set. Open
            // the commitment for every urgent T1-T12, stage first, then require the live native
            // predicate. A false pre-stage projection must not prevent the gear swap that makes it
            // true, while a false post-stage native predicate still fails closed.
            var safeAutomatic = urgent.Where(x => x.TitanId <= 12)
                .OrderBy(x => x.TitanId).ToArray();
            if (safeAutomatic.Length > 0)
            {
                _commitment = CreateCommitment(snapshot, safeAutomatic);
                return PlanCommitment(snapshot, topology);
            }
            return PlanManual(snapshot, urgent[0], topology);
        }

        private TitanExecutionAction PlanCommitment(TitanExecutionSnapshot snapshot,
            OrdinaryInventoryTopology topology)
        {
            if (_commitment.ManualExecution)
                return PlanManualCommitment(snapshot, topology);
            var ids = _commitment.TitanIds();
            var versions = _commitment.Versions();
            var opportunities = new TitanExecutionOpportunity[ids.Length];
            for (var i = 0; i < ids.Length; i++)
            {
                opportunities[i] = snapshot.Find(ids[i]);
                if (opportunities[i] == null)
                    return Action(TitanExecutionActionKind.Hold, snapshot,
                        "committed Titan disappeared from the synchronized snapshot");
            }
            var proof = LootCapacity.ProveOrdinary(topology,
                LootCapacityRequirement.ExactBatch("simultaneous-titan-unswept-chain",
                    _commitment.RequiredTransientSlots, 0));
            var allKilled = true;
            for (var i = 0; i < opportunities.Length; i++)
                if (opportunities[i].KillCount <= _commitment.KillCountBefore(i)) allKilled = false;

            if (allKilled || _commitment.Abandoned)
            {
                if (string.Equals(snapshot.LoadoutStageId, _commitment.Id,
                    StringComparison.Ordinal))
                {
                    if (snapshot.AutoKillEnabled)
                        return CommitmentAction(TitanExecutionActionKind.DisableAutokill,
                            snapshot, proof, 0, 0, false,
                            _commitment.Abandoned
                                ? "unfightable due commitment was abandoned; disable autokill before restoring gear"
                                : "all exact kill deltas arrived; disable autokill before restoring gear");
                    return CommitmentAction(TitanExecutionActionKind.RestoreLoadout,
                        snapshot, proof, 0, 0, false,
                        _commitment.Abandoned
                            ? "source-proven execution is unavailable; restore gear and preserve reset liveness"
                            : "all exact kill deltas arrived; restore the exact pre-Titan loadout");
                }
                if (_commitment.AutoKillWasEnabled && !snapshot.AutoKillEnabled)
                    return CommitmentAction(TitanExecutionActionKind.RestoreAutokillPreference,
                        snapshot, proof, 0, 0, true,
                        "restore the pre-commitment native autokill preference after gear restoration");
                var complete = CommitmentAction(TitanExecutionActionKind.CommitmentComplete,
                    snapshot, proof, 0, 0, snapshot.AutoKillEnabled,
                    _commitment.Abandoned
                        ? "unfightable Titan commitment cleaned up; its clock loss is an explicit reset cost"
                        : "every intended Titan kill and cleanup postcondition is exact");
                _commitment = null;
                return complete;
            }

            if (!snapshot.ExactBindingsAvailable)
                return CommitmentAction(TitanExecutionActionKind.Hold, snapshot, proof, 0, 0,
                    false, "one or more installed-build Titan bindings are unavailable");
            if (!proof.Admitted)
                return CommitmentAction(TitanExecutionActionKind.Hold, snapshot, proof, 0, 0,
                    false, "aggregate adjacent-frame Titan batch does not fit usable ordinary capacity");
            if (snapshot.AutoKillEnabled
                && !string.Equals(snapshot.LoadoutStageId, _commitment.Id,
                    StringComparison.Ordinal))
                return CommitmentAction(TitanExecutionActionKind.DisableAutokill,
                    snapshot, proof, 0, 0, false,
                    "disable before version/loadout staging so the crossing frame cannot consume stale state");
            for (var i = 0; i < opportunities.Length; i++)
                if (opportunities[i].CurrentVersion != versions[i])
                    return CommitmentAction(TitanExecutionActionKind.SelectVersion,
                        snapshot, proof, ids[i], versions[i], false,
                        "select and verify the committed Titan version while autokill is disabled");
            if (!string.Equals(snapshot.LoadoutStageId, _commitment.Id, StringComparison.Ordinal))
            {
                if (snapshot.AutoKillEnabled)
                    return CommitmentAction(TitanExecutionActionKind.DisableAutokill,
                        snapshot, proof, 0, 0, false,
                        "autokill must remain disabled until exact physical staging completes");
                var request = new TitanLoadoutStageRequest(_commitment.Id, ids, versions,
                    _configuredLoadoutItemIds, _valuesGold);
                return new TitanExecutionAction(TitanExecutionActionKind.StageLoadout,
                    _commitment.Id, ids[ids.Length - 1], versions[versions.Length - 1], 0,
                    false, true, proof, request, snapshot.Fingerprint(),
                    "stage one common candidate-AK loadout and preserve it across the due chain", ids);
            }
            var nextDue = opportunities.Select((x, index) => new {Opportunity = x, Index = index})
                .Where(x => x.Opportunity.KillCount <= _commitment.KillCountBefore(x.Index)
                            && x.Opportunity.Clock.Due)
                .OrderBy(x => x.Opportunity.TitanId).FirstOrDefault();
            if (nextDue == null)
                return CommitmentAction(TitanExecutionActionKind.AwaitCommittedKills,
                    snapshot, proof, 0, 0, false,
                    "strongest combat loadout remains staged until the next committed clock is due");
            if (!nextDue.Opportunity.NativeAutokillVerified)
            {
                _commitment.MarkAbandoned();
                return CommitmentAction(TitanExecutionActionKind.RestoreLoadout,
                    snapshot, proof, nextDue.Opportunity.TitanId,
                    nextDue.Opportunity.DesiredVersion, false,
                    "staged strongest loadout fails the due native predicate; restore immediately so reset cannot deadlock");
            }
            if (snapshot.AutoKillEnabled)
                return CommitmentAction(TitanExecutionActionKind.DisableAutokill,
                    snapshot, proof, 0, 0, false,
                    "typed Titan execution owns the setting; native autokill must be low between synchronous calls");
            return CommitmentAction(TitanExecutionActionKind.KillOneDueTitan,
                snapshot, proof, nextDue.Opportunity.TitanId,
                nextDue.Opportunity.DesiredVersion, false,
                "invoke exactly one synchronous native Titan frame and verify its counter, clock, and loot-return boundary");
        }

        /*
        TERMINAL TITAN COMMITMENT

        T13/T14 are not branches of AdventureController.manageFight. They are staged like native
        Titans, but execution is a typed zone entry followed by CombatManager's already-reserved
        one-hit terminal move. The loadout remains locked while the enemy is live and until durable
        reward evidence advances: ratTitanDefeated for T13 and ordinary item 495 for T14. This makes
        a reset during the entry/fight/reward gap impossible when the caller honors ResetInterlock.
        */
        private TitanExecutionAction PlanManualCommitment(TitanExecutionSnapshot snapshot,
            OrdinaryInventoryTopology topology)
        {
            var ids = _commitment.TitanIds();
            var titanId = ids[0];
            var opportunity = snapshot.Find(titanId);
            if (opportunity == null)
                return Action(TitanExecutionActionKind.Hold, snapshot,
                    "committed terminal Titan disappeared from the synchronized snapshot");
            var proof = titanId == 14
                ? LootCapacity.ProveOrdinary(topology, LootCapacity.Titan14FinalPiece())
                : LootCapacity.ProveOrdinary(topology,
                    LootCapacityRequirement.ExactBatch("terminal-titan-no-unique-slot", 0, 0));
            var rewardSettled = opportunity.KillCount > _commitment.KillCountBefore(0)
                                || !opportunity.RewardActionable;
            if (rewardSettled)
            {
                if (string.Equals(snapshot.LoadoutStageId, _commitment.Id,
                    StringComparison.Ordinal))
                {
                    if (snapshot.AutoKillEnabled)
                        return CommitmentAction(TitanExecutionActionKind.DisableAutokill,
                            snapshot, proof, 0, 0, false,
                            "terminal reward settled; disable autokill before restoring gear");
                    return CommitmentAction(TitanExecutionActionKind.RestoreLoadout,
                        snapshot, proof, 0, 0, false,
                        "terminal reward evidence settled; restore the exact pre-Titan loadout");
                }
                if (_commitment.AutoKillWasEnabled && !snapshot.AutoKillEnabled)
                    return CommitmentAction(TitanExecutionActionKind.RestoreAutokillPreference,
                        snapshot, proof, 0, 0, true,
                        "restore the pre-commitment native autokill preference after terminal cleanup");
                var complete = CommitmentAction(TitanExecutionActionKind.CommitmentComplete,
                    snapshot, proof, titanId, 0, snapshot.AutoKillEnabled,
                    "terminal Titan reward and cleanup postconditions are exact");
                _commitment = null;
                return complete;
            }
            if (!snapshot.ExactBindingsAvailable)
                return CommitmentAction(TitanExecutionActionKind.Hold, snapshot, proof,
                    titanId, 0, false,
                    "terminal Titan entry binding is unavailable on the installed build");
            if (!proof.Admitted)
                return CommitmentAction(TitanExecutionActionKind.Hold, snapshot, proof,
                    titanId, 0, false,
                    "terminal Titan reward does not fit exact usable ordinary capacity");
            if (snapshot.AutoKillEnabled)
                return CommitmentAction(TitanExecutionActionKind.DisableAutokill,
                    snapshot, proof, 0, 0, false,
                    "disable native autokill before terminal Titan physical staging");
            if (!string.Equals(snapshot.LoadoutStageId, _commitment.Id, StringComparison.Ordinal))
            {
                var request = new TitanLoadoutStageRequest(_commitment.Id, ids,
                    _commitment.Versions(), _configuredLoadoutItemIds, _valuesGold);
                return new TitanExecutionAction(TitanExecutionActionKind.StageLoadout,
                    _commitment.Id, titanId, 0, 0, false, true, proof, request,
                    snapshot.Fingerprint(),
                    "stage the strongest exact terminal-Titan combat loadout", ids);
            }
            if (!opportunity.ManualPrerequisites.Ready || !opportunity.ManualFightReady
                || !opportunity.TerminalLethalMoveReserved)
            {
                if (opportunity.Clock.Due)
                {
                    _commitment.MarkAbandoned();
                    return CommitmentAction(TitanExecutionActionKind.RestoreLoadout,
                        snapshot, proof, titanId, 0, false,
                        "staged terminal loadout lost its source-proven lethal reservation; restore so reset remains live");
                }
                return CommitmentAction(TitanExecutionActionKind.Hold, snapshot, proof,
                    titanId, 0, false,
                    "staged terminal Titan waits for the live one-hit and reserved-move proof");
            }
            if (!opportunity.Clock.Due)
                return CommitmentAction(TitanExecutionActionKind.AwaitManualTitanKill,
                    snapshot, proof, titanId, 0, false,
                    "terminal combat loadout is staged before due time");
            var zone = TitanMechanics.Describe(titanId).Zone;
            if (snapshot.CurrentZone == zone)
                return CommitmentAction(TitanExecutionActionKind.AwaitManualTitanKill,
                    snapshot, proof, titanId, 0, false,
                    snapshot.CurrentEnemyIsTargetTitan
                        ? "CombatManager owns the reserved terminal lethal move; await durable reward evidence"
                        : "terminal zone is selected; await native enemy spawn and CombatManager handoff");
            return CommitmentAction(TitanExecutionActionKind.EnterManualTitan,
                snapshot, proof, titanId, 0, false,
                "enter the exact terminal Titan zone after strongest-loadout and lethal-move proof");
        }

        private TitanExecutionAction PlanManual(TitanExecutionSnapshot snapshot,
            TitanExecutionOpportunity opportunity, OrdinaryInventoryTopology topology)
        {
            if (opportunity.TitanId == 5 && snapshot.Walderp != null)
            {
                if (snapshot.Walderp.InResponseLoop)
                {
                    var move = CombatManager.SelectWaldoResponseMove(
                        snapshot.Walderp.RequestedMove, snapshot.Walderp.WalderpSays,
                        snapshot.Walderp.RegularReady, snapshot.Walderp.StrongReady,
                        snapshot.Walderp.PierceReady, snapshot.Walderp.UltimateReady);
                    return new TitanExecutionAction(move == 0
                            ? TitanExecutionActionKind.Hold : TitanExecutionActionKind.WalderpResponse,
                        string.Empty, 5, 0, move, false, false, null, null,
                        snapshot.Fingerprint(), move == 0
                            ? "no legal exact/different Walderp response is ready; fail closed"
                            : "delegate the exact task-11 Walderp response as the only damaging action",
                        new[] {5});
                }
                if (TitanMechanics.IsWaldoClockPaused(snapshot.Walderp.Finds,
                        snapshot.Walderp.Defeats))
                    return Action(TitanExecutionActionKind.AwaitWalderpFind, snapshot,
                        "Walderp clock is paused awaiting the next native find phase");
            }
            if (!opportunity.ManualPrerequisites.Ready)
                return Action(TitanExecutionActionKind.Hold, snapshot,
                    opportunity.ManualPrerequisites.Reason);
            if (!opportunity.ManualFightReady)
                return Action(TitanExecutionActionKind.Hold, snapshot,
                    "manual Titan combat state is not ready");
            if (opportunity.TitanId >= 13 && !opportunity.TerminalLethalMoveReserved)
                return Action(TitanExecutionActionKind.Hold, snapshot,
                    "terminal Titan waits in Safe Zone for task-11's exact lethal first-move reservation");
            LootCapacityProof terminalCapacity = null;
            if (opportunity.TitanId == 14)
            {
                terminalCapacity = LootCapacity.ProveOrdinary(topology,
                    LootCapacity.Titan14FinalPiece());
                if (!terminalCapacity.Admitted)
                    return new TitanExecutionAction(TitanExecutionActionKind.Hold,
                        string.Empty, 14, opportunity.DesiredVersion, 0, false,
                        false, terminalCapacity, null, snapshot.Fingerprint(),
                        "T14 waits for one exact usable ordinary slot so item 495 cannot be lost",
                        new[] {14});
            }
            if (opportunity.TitanId >= 13)
            {
                _commitment = CreateCommitment(snapshot, new[] {opportunity});
                return PlanManualCommitment(snapshot, topology);
            }
            return new TitanExecutionAction(TitanExecutionActionKind.EnterManualTitan,
                string.Empty, opportunity.TitanId, opportunity.DesiredVersion, 0, false,
                false, terminalCapacity, null, snapshot.Fingerprint(),
                "manual/puzzle route is delegated until its source-specific live state machine is implemented",
                new[] {opportunity.TitanId});
        }

        internal TitanExecutionResult ExecuteNext(RootTransaction root,
            ITitanExecutionRuntime runtime)
        {
            if (root == null) throw new ArgumentNullException("root");
            if (runtime == null) throw new ArgumentNullException("runtime");
            var snapshot = runtime.Capture();
            var action = Plan(snapshot, runtime.CaptureOrdinaryTopology());
            if (!action.IsMutation)
                return new TitanExecutionResult(action, null, action.Reason);
            if (!AuthorityAllows(action) || !runtime.LiveAuthority
                || !action.LiveMutationAuthorized)
                return new TitanExecutionResult(action, null,
                    "live Titan authority is disabled or the typed action remains telemetry-only");
            if (!runtime.BindingAvailable(action))
                return new TitanExecutionResult(action, null,
                    "installed-build binding unavailable; mutation remains read-only");
            var mutation = root.ExecuteChild(new TitanExecutionMutationIntent(runtime, action));
            return new TitanExecutionResult(action, mutation, mutation.Reason);
        }

        private bool AuthorityAllows(TitanExecutionAction action)
        {
            if (action == null) return false;
            var ids = action.TitanIds();
            var terminal = action.TitanId >= 13 || ids.Any(x => x >= 13)
                           || _commitment != null && _commitment.ManualExecution;
            // Once a commitment exists, these actions are cleanup safety work rather than new
            // progression. They remain authorized after a config reload so physical gear and the
            // user's setting cannot be stranded behind an abandoned commitment.
            if (action.Kind == TitanExecutionActionKind.RestoreLoadout
                || action.Kind == TitanExecutionActionKind.RestoreAutokillPreference)
                return _commitment != null || _automaticAuthorityEnabled
                       || _terminalAuthorityEnabled;
            return terminal ? _terminalAuthorityEnabled : _automaticAuthorityEnabled;
        }

        private TitanExecutionCommitment CreateCommitment(TitanExecutionSnapshot snapshot,
            TitanExecutionOpportunity[] opportunities)
        {
            var ids = opportunities.Select(x => x.TitanId).ToArray();
            var versions = opportunities.Select(x => x.DesiredVersion).ToArray();
            var kills = opportunities.Select(x => x.KillCount).ToArray();
            var slots = opportunities.Sum(x => x.TitanId == 12
                ? Math.Max(x.WorstCaseTransientSlots,
                    T12WorstCaseTransientSlots(x.DesiredVersion + 1))
                : x.WorstCaseTransientSlots);
            var id = snapshot.Epoch + "|titans=" + string.Join(",", ids.Select(x => x.ToString()).ToArray())
                     + "|versions=" + string.Join(",", versions.Select(x => x.ToString()).ToArray())
                     + "|kills=" + string.Join(",", kills.Select(x => x.ToString()).ToArray());
            return new TitanExecutionCommitment(id, snapshot.Epoch, snapshot.AutoKillEnabled,
                ids, versions, kills, slots);
        }

        private TitanExecutionAction CommitmentAction(TitanExecutionActionKind kind,
            TitanExecutionSnapshot snapshot, LootCapacityProof proof, int titanId,
            int version, bool targetAutokill, string reason)
        {
            return new TitanExecutionAction(kind, _commitment.Id, titanId, version, 0,
                targetAutokill, true, proof, null, snapshot.Fingerprint(), reason,
                _commitment.TitanIds());
        }

        private static TitanExecutionAction Action(TitanExecutionActionKind kind,
            TitanExecutionSnapshot snapshot, string reason)
        {
            return new TitanExecutionAction(kind, string.Empty, 0, 0, 0, false,
                false, null, null, snapshot.Fingerprint(), reason, new int[0]);
        }

        internal static int T12WorstCaseTransientSlots(int selectedOneBasedVersion)
        {
            switch (selectedOneBasedVersion)
            {
                case 1: return LootCapacity.Titan12EndPiece(483).RequiredFreeSlots;
                case 2: return LootCapacity.Titan12EndPiece(489).RequiredFreeSlots;
                case 3: return LootCapacity.Titan12EndPiece(493).RequiredFreeSlots;
                case 4: return LootCapacity.Titan12EndPiece(484).RequiredFreeSlots;
                default: throw new ArgumentOutOfRangeException("selectedOneBasedVersion");
            }
        }

        internal static TitanOnlineLootProjection ProjectOnlineKill(int titanId,
            int zeroBasedVersion)
        {
            TitanMechanics.ValidateTitanId(titanId);
            if (zeroBasedVersion < 0 || zeroBasedVersion > 3)
                throw new ArgumentOutOfRangeException("zeroBasedVersion");
            var end = titanId == 12
                ? TitanMechanics.Titan12EndItemsForVersion(zeroBasedVersion + 1)
                : new int[0];
            return new TitanOnlineLootProjection(titanId, zeroBasedVersion,
                true, titanId <= 12 || titanId == 14, end);
        }

        internal static TitanOfflineProjection ProjectOffline(int titanId,
            int selectedVersion, double elapsedBefore, double offlineSeconds,
            double spawnSeconds, bool v1AutokillQualified,
            long selectedVersionBestiaryBefore)
        {
            TitanMechanics.ValidateTitanId(titanId);
            if (selectedVersion < 0 || selectedVersion > 3)
                throw new ArgumentOutOfRangeException("selectedVersion");
            if (double.IsNaN(elapsedBefore) || double.IsInfinity(elapsedBefore)
                || elapsedBefore < 0.0) throw new ArgumentOutOfRangeException("elapsedBefore");
            if (double.IsNaN(offlineSeconds) || double.IsInfinity(offlineSeconds)
                || offlineSeconds < 0.0) throw new ArgumentOutOfRangeException("offlineSeconds");
            if (double.IsNaN(spawnSeconds) || double.IsInfinity(spawnSeconds)
                || spawnSeconds <= 0.0) throw new ArgumentOutOfRangeException("spawnSeconds");
            if (selectedVersionBestiaryBefore < 0L)
                throw new ArgumentOutOfRangeException("selectedVersionBestiaryBefore");
            var total = elapsedBefore + offlineSeconds;
            long kills = 0L;
            double elapsedAfter;
            if (titanId >= 6 && titanId <= 12 && v1AutokillQualified)
            {
                var raw = Math.Floor(total / spawnSeconds);
                kills = raw >= long.MaxValue ? long.MaxValue : (long)raw;
                elapsedAfter = total - kills * spawnSeconds;
            }
            else elapsedAfter = Math.Min(total, spawnSeconds);
            var bestiary = selectedVersionBestiaryBefore > long.MaxValue - kills
                ? long.MaxValue : selectedVersionBestiaryBefore + kills;
            return new TitanOfflineProjection(titanId, selectedVersion, kills,
                bestiary, elapsedAfter);
        }
    }

    internal sealed class TitanExecutionMutationIntent :
        IMutationIntent<TitanExecutionSnapshot, TitanExecutionApplyResult, TitanExecutionSnapshot>
    {
        private readonly ITitanExecutionRuntime _runtime;
        private readonly TitanExecutionAction _action;

        internal TitanExecutionMutationIntent(ITitanExecutionRuntime runtime,
            TitanExecutionAction action)
        {
            _runtime = runtime;
            _action = action;
        }

        public string Id { get { return "titan-execution." + _action.Kind; } }
        public MutationClass Class
        {
            get
            {
                return _action.Kind == TitanExecutionActionKind.StageLoadout
                       || _action.Kind == TitanExecutionActionKind.RestoreLoadout
                    ? MutationClass.TitanLoadout : MutationClass.Adventure;
            }
        }
        public MutationRisk Risk
        {
            get
            {
                return _action.Kind == TitanExecutionActionKind.ReleaseAutokill
                       || _action.Kind == TitanExecutionActionKind.KillOneDueTitan
                       || _action.Kind == TitanExecutionActionKind.EnterManualTitan
                       || _action.Kind == TitanExecutionActionKind.RestoreLoadout
                    ? MutationRisk.Irreversible : MutationRisk.Reversible;
            }
        }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId { get { return _runtime.BindingId(_action) ?? string.Empty; } }
        public bool Required { get { return true; } }
        public bool CanCompensate
        {
            get
            {
                return _action.Kind != TitanExecutionActionKind.ReleaseAutokill
                       && _action.Kind != TitanExecutionActionKind.KillOneDueTitan
                       && _action.Kind != TitanExecutionActionKind.EnterManualTitan
                       && _action.Kind != TitanExecutionActionKind.RestoreLoadout;
            }
        }
        public bool CreatesNewEpoch { get { return false; } }
        public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

        public TitanExecutionSnapshot CaptureBefore(MutationContext context)
        {
            return _runtime.Capture();
        }

        public PreconditionResult CheckPreconditions(MutationContext context,
            TitanExecutionSnapshot before)
        {
            if (!_runtime.LiveAuthority || !_runtime.BindingAvailable(_action))
                return PreconditionResult.Hold("runtime authority/binding unavailable");
            if (!string.Equals(before.Fingerprint(), _action.BeforeFingerprint,
                    StringComparison.Ordinal))
                return PreconditionResult.Hold("Titan action snapshot became stale before invocation");
            if (!_action.LiveMutationAuthorized)
                return PreconditionResult.Hold("action is outside typed live Titan authority");
            if (_action.Capacity != null)
            {
                var currentCapacity = LootCapacity.ProveOrdinary(
                    _runtime.CaptureOrdinaryTopology(),
                    LootCapacityRequirement.ExactBatch(
                        _action.Capacity.RequirementKey,
                        _action.Capacity.RequiredFreeSlots, 0));
                if (!currentCapacity.Admitted)
                    return PreconditionResult.Hold(
                        "exact Titan capacity changed before native invocation");
            }
            return PreconditionResult.Ready();
        }

        public TitanExecutionApplyResult Apply(MutationContext context,
            RootTransactionToken token, TitanExecutionSnapshot before)
        {
            return _runtime.Apply(_action, token);
        }

        public VerificationResult<TitanExecutionSnapshot> Verify(MutationContext context,
            TitanExecutionSnapshot before,
            MutationApplyObservation<TitanExecutionApplyResult> apply)
        {
            var after = _runtime.Capture();
            if (!apply.ReturnedNormally || apply.Value == null
                || !apply.Value.InvocationAttempted)
                return VerificationResult<TitanExecutionSnapshot>.Failed(
                    "runtime did not attest the exact native invocation");
            if (!string.Equals(before.Epoch, after.Epoch, StringComparison.Ordinal))
                return VerificationResult<TitanExecutionSnapshot>.Failed(
                    "Titan execution crossed an epoch boundary");
            if (_action.Kind == TitanExecutionActionKind.KillOneDueTitan)
            {
                if (!apply.Value.NativeReturnedNormally)
                    return VerificationResult<TitanExecutionSnapshot>.Failed(
                        "native Titan frame did not return through its clock-reset/loot boundary");
                if (!ExactOneTitanKillDelta(before, after, _action.TitanId))
                    return VerificationResult<TitanExecutionSnapshot>.Failed(
                        "native Titan frame did not produce exactly one target Bestiary/clock delta");
            }
            else if (!SynchronousCountersUnchanged(before, after))
                return VerificationResult<TitanExecutionSnapshot>.Failed(
                    "clock/kill counters changed inside one synchronous staging atom");
            if (!ExactPostcondition(before, after))
                return VerificationResult<TitanExecutionSnapshot>.Failed(
                    "Titan execution atom did not reach its exact postcondition");
            return VerificationResult<TitanExecutionSnapshot>.Satisfied(after,
                "exact Titan execution staging postcondition verified");
        }

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            TitanExecutionSnapshot before,
            MutationApplyObservation<TitanExecutionApplyResult> apply)
        {
            return _runtime.Compensate(_action, before, token);
        }

        public bool BeforeStateMatches(TitanExecutionSnapshot expected,
            TitanExecutionSnapshot observed)
        {
            return expected != null && observed != null
                   && string.Equals(expected.Fingerprint(), observed.Fingerprint(),
                       StringComparison.Ordinal);
        }

        public string FingerprintBefore(TitanExecutionSnapshot before)
        {
            return before == null ? "<null>" : before.Fingerprint();
        }

        public string FingerprintAfter(TitanExecutionSnapshot after)
        {
            return after == null ? "<null>" : after.Fingerprint();
        }

        private bool ExactPostcondition(TitanExecutionSnapshot before,
            TitanExecutionSnapshot after)
        {
            switch (_action.Kind)
            {
                case TitanExecutionActionKind.DisableAutokill:
                    return before.AutoKillEnabled && !after.AutoKillEnabled
                           && SameVersions(before, after)
                           && string.Equals(before.LoadoutStageId, after.LoadoutStageId,
                               StringComparison.Ordinal);
                case TitanExecutionActionKind.SelectVersion:
                    return !after.AutoKillEnabled && VersionChangedOnly(before, after,
                               _action.TitanId, _action.Version)
                           && string.Equals(before.LoadoutStageId, after.LoadoutStageId,
                               StringComparison.Ordinal);
                case TitanExecutionActionKind.StageLoadout:
                    return !after.AutoKillEnabled
                           && string.Equals(after.LoadoutStageId, _action.CommitmentId,
                               StringComparison.Ordinal) && SameVersions(before, after);
                case TitanExecutionActionKind.ReleaseAutokill:
                    return !before.AutoKillEnabled && after.AutoKillEnabled
                           && string.Equals(after.LoadoutStageId, _action.CommitmentId,
                               StringComparison.Ordinal) && SameVersions(before, after);
                case TitanExecutionActionKind.KillOneDueTitan:
                    return !before.AutoKillEnabled && !after.AutoKillEnabled
                           && string.Equals(before.LoadoutStageId, _action.CommitmentId,
                               StringComparison.Ordinal)
                           && string.Equals(after.LoadoutStageId, _action.CommitmentId,
                               StringComparison.Ordinal)
                           && SameVersions(before, after)
                           && ExactOneTitanKillDelta(before, after, _action.TitanId);
                case TitanExecutionActionKind.EnterManualTitan:
                    return !after.AutoKillEnabled
                           && string.Equals(after.LoadoutStageId, _action.CommitmentId,
                               StringComparison.Ordinal)
                           && after.CurrentZone == TitanMechanics.Describe(_action.TitanId).Zone
                           && SameVersions(before, after);
                case TitanExecutionActionKind.RestoreLoadout:
                    return !after.AutoKillEnabled && string.IsNullOrEmpty(after.LoadoutStageId)
                           && SameVersions(before, after);
                case TitanExecutionActionKind.RestoreAutokillPreference:
                    return after.AutoKillEnabled && string.IsNullOrEmpty(after.LoadoutStageId)
                           && SameVersions(before, after);
                default:
                    return false;
            }
        }

        private static bool SynchronousCountersUnchanged(TitanExecutionSnapshot before,
            TitanExecutionSnapshot after)
        {
            var left = before.Opportunities();
            var right = after.Opportunities();
            if (left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++)
                if (left[i].TitanId != right[i].TitanId
                    || left[i].KillCount != right[i].KillCount
                    || left[i].Clock.ArithmeticRemainingSeconds
                       != right[i].Clock.ArithmeticRemainingSeconds)
                    return false;
            return true;
        }

        internal static bool ExactOneTitanKillDelta(TitanExecutionSnapshot before,
            TitanExecutionSnapshot after, int targetTitanId)
        {
            var left = before.Opportunities();
            var right = after.Opportunities();
            if (left.Length != right.Length) return false;
            var sawTarget = false;
            for (var i = 0; i < left.Length; i++)
            {
                if (left[i].TitanId != right[i].TitanId) return false;
                if (left[i].TitanId == targetTitanId)
                {
                    sawTarget = true;
                    if (!left[i].Clock.Due || left[i].KillCount == int.MaxValue
                        || right[i].KillCount != left[i].KillCount + 1
                        || right[i].Clock.Due
                        || right[i].Clock.ArithmeticRemainingSeconds
                           != right[i].Clock.DueSeconds)
                        return false;
                }
                else if (left[i].KillCount != right[i].KillCount
                         || left[i].Clock.ArithmeticRemainingSeconds
                            != right[i].Clock.ArithmeticRemainingSeconds)
                    return false;
            }
            return sawTarget;
        }

        private static bool SameVersions(TitanExecutionSnapshot before,
            TitanExecutionSnapshot after)
        {
            var left = before.Opportunities();
            var right = after.Opportunities();
            if (left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++)
                if (left[i].TitanId != right[i].TitanId
                    || left[i].CurrentVersion != right[i].CurrentVersion) return false;
            return true;
        }

        private static bool VersionChangedOnly(TitanExecutionSnapshot before,
            TitanExecutionSnapshot after, int titanId, int expectedVersion)
        {
            var left = before.Opportunities();
            var right = after.Opportunities();
            if (left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++)
            {
                if (left[i].TitanId != right[i].TitanId) return false;
                var expected = left[i].TitanId == titanId
                    ? expectedVersion : left[i].CurrentVersion;
                if (right[i].CurrentVersion != expected) return false;
            }
            return true;
        }
    }

    internal sealed class TitanNativeFrameState
    {
        private readonly double[] _elapsed;
        private readonly int[] _kills;
        internal TitanNativeFrameState(double[] elapsed, int[] kills)
        {
            if (elapsed == null || kills == null || elapsed.Length != 12 || kills.Length != 12)
                throw new ArgumentException("online frame state requires twelve Titans");
            _elapsed = (double[])elapsed.Clone();
            _kills = (int[])kills.Clone();
        }
        internal double Elapsed(int titanId) { return _elapsed[titanId - 1]; }
        internal int Kills(int titanId) { return _kills[titanId - 1]; }
        internal double[] ElapsedArray() { return (double[])_elapsed.Clone(); }
        internal int[] KillArray() { return (int[])_kills.Clone(); }
    }

    internal sealed class TitanNativeFrameResult
    {
        internal readonly TitanNativeFrameState State;
        internal readonly int KilledTitanId;
        internal TitanNativeFrameResult(TitanNativeFrameState state, int killedTitanId)
        {
            State = state;
            KilledTitanId = killedTitanId;
        }
    }

    internal static class TitanNativeFrameFixture
    {
        internal static TitanNativeFrameResult Advance(TitanNativeFrameState before,
            double deltaSeconds, int[] spawnSeconds, bool autoKillEnabled,
            bool[] nativeAutokillMask)
        {
            if (before == null) throw new ArgumentNullException("before");
            if (spawnSeconds == null || spawnSeconds.Length != 12
                || nativeAutokillMask == null || nativeAutokillMask.Length != 12)
                throw new ArgumentException("frame fixture requires twelve spawn/mask values");
            if (double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds)
                || deltaSeconds < 0.0) throw new ArgumentOutOfRangeException("deltaSeconds");
            var elapsed = before.ElapsedArray();
            var kills = before.KillArray();
            for (var i = 0; i < 12; i++)
            {
                if (spawnSeconds[i] <= 0) throw new ArgumentOutOfRangeException("spawnSeconds");
                if (elapsed[i] < spawnSeconds[i])
                    elapsed[i] = Math.Min(spawnSeconds[i], elapsed[i] + deltaSeconds);
            }
            var killed = 0;
            if (autoKillEnabled)
            {
                for (var i = 0; i < 12; i++)
                {
                    if (elapsed[i] < spawnSeconds[i] || !nativeAutokillMask[i]) continue;
                    elapsed[i] = 0.0;
                    kills[i]++;
                    killed = i + 1;
                    break;
                }
            }
            return new TitanNativeFrameResult(new TitanNativeFrameState(elapsed, kills), killed);
        }
    }
}
