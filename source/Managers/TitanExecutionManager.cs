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

Mechanism: Before any target clock is due, the manager records exact kill counters and desired
versions, disables native autokill, selects versions, stages one common exact-reference loadout,
requires candidate autokill projections to agree with live native predicates, and proves aggregate
unswept capacity for every adjacent-frame kill.  Only then does it release autokill.  Native T1-T12
ascending one-per-frame order is observed through exact kill-counter deltas; the loadout remains
locked until every committed delta arrives, after which autokill is disabled, gear is restored, and
the prior setting is restored.  Walderp, manual Glop/Apathy, T13, and T14 are represented as typed
manual actions and consume task-11/task-12 proofs rather than pretending to be ordinary autokills.

Inputs and outputs: Inputs are immutable TitanExecutionSnapshot objects, ordinary inventory
topology, task-1 root transactions, and a build-pinned ITitanExecutionRuntime.  Outputs are typed
plans, capacity proofs, mutation results, online loot projections, offline bestiary/timer
projections, and a deterministic same-frame native-update fixture.  LoadoutManager supplies the
physical staging hook; integration supplies clocks, versions, native-predicate reads, and bindings.

Invariants and safety: Ready-only callbacks are never considered staging.  Release requires exact
versions, a staged loadout, current aggregate capacity, and live native verification for every
committed Titan.  T12 online v4 contains all four cumulative END opportunities; offline progress
calls no loot path.  Manual Glop copies are ceil(enemy actions/5); a native autokill consumes none.
T13 stops after the rat flag.  T14 remains actionable after its flag while ordinary item 495 is
missing.  Initial live authority is limited to projected-and-native-verified T1-T12 autokill
commitments; Walderp/manual/T13/T14 plans are telemetry-only.

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
                if (_titanIds[i] > 12) throw new ArgumentOutOfRangeException("titanIds");
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

        internal TitanExecutionSnapshot(string epoch, bool online, bool autoKillEnabled,
            string loadoutStageId, bool exactBindingsAvailable,
            IEnumerable<TitanExecutionOpportunity> opportunities,
            WalderpExecutionSnapshot walderp)
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
                   + "|stage=" + LoadoutStageId + "|" + string.Join(";",
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
        AwaitCommittedKills,
        RestoreLoadout,
        RestoreAutokillPreference,
        CommitmentComplete,
        AwaitWalderpFind,
        WalderpResponse,
        EnterManualTitan,
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
        }

        internal int[] TitanIds() { return (int[])_titanIds.Clone(); }
        internal int[] Versions() { return (int[])_versions.Clone(); }
        internal int KillCountBefore(int index) { return _killCountsBefore[index]; }
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
        internal readonly string Detail;

        internal TitanExecutionApplyResult(bool invocationAttempted, string detail)
        {
            InvocationAttempted = invocationAttempted;
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
        private bool _liveAuthorityEnabled;

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
        internal bool LiveAuthorityEnabled { get { return _liveAuthorityEnabled; } }

        internal void EnableSafeT1ThroughT12Authority(bool enabled)
        {
            _liveAuthorityEnabled = enabled;
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

            var safeAutomatic = urgent.Where(x => x.TitanId <= 12
                    && x.CandidateAutokillProjected).OrderBy(x => x.TitanId).ToArray();
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

            if (allKilled)
            {
                if (string.Equals(snapshot.LoadoutStageId, _commitment.Id,
                    StringComparison.Ordinal))
                {
                    if (snapshot.AutoKillEnabled)
                        return CommitmentAction(TitanExecutionActionKind.DisableAutokill,
                            snapshot, proof, 0, 0, false,
                            "all exact kill deltas arrived; disable autokill before restoring gear");
                    return CommitmentAction(TitanExecutionActionKind.RestoreLoadout,
                        snapshot, proof, 0, 0, false,
                        "all exact kill deltas arrived; restore the exact pre-Titan loadout");
                }
                if (_commitment.AutoKillWasEnabled && !snapshot.AutoKillEnabled)
                    return CommitmentAction(TitanExecutionActionKind.RestoreAutokillPreference,
                        snapshot, proof, 0, 0, true,
                        "restore the pre-commitment native autokill preference after gear restoration");
                var complete = CommitmentAction(TitanExecutionActionKind.CommitmentComplete,
                    snapshot, proof, 0, 0, snapshot.AutoKillEnabled,
                    "every intended Titan kill and cleanup postcondition is exact");
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
            if (opportunities.Any(x => !x.NativeAutokillVerified))
                return CommitmentAction(TitanExecutionActionKind.HoldNativeAutokillVerification,
                    snapshot, proof, 0, 0, false,
                    "candidate projection is staged but every live native autokill predicate must confirm");
            if (!snapshot.AutoKillEnabled)
                return CommitmentAction(TitanExecutionActionKind.ReleaseAutokill,
                    snapshot, proof, 0, 0, true,
                    "versions, common loadout, native predicates, and aggregate capacity are exact; release autokill");
            return CommitmentAction(TitanExecutionActionKind.AwaitCommittedKills,
                snapshot, proof, 0, 0, true,
                "hold the staged loadout while native consumes committed Titans one per frame in ascending order");
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
            return new TitanExecutionAction(TitanExecutionActionKind.EnterManualTitan,
                string.Empty, opportunity.TitanId, opportunity.DesiredVersion, 0, false,
                false, terminalCapacity, null, snapshot.Fingerprint(),
                opportunity.TitanId > 12
                    ? "manual terminal route is modeled but outside initial live authority"
                    : "manual/puzzle route is modeled but initial authority permits only verified native autokills",
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
            if (!_liveAuthorityEnabled || !runtime.LiveAuthority
                || !action.LiveMutationAuthorized)
                return new TitanExecutionResult(action, null,
                    "live authority is limited to explicitly enabled, verified-safe T1-T12 autokill staging");
            if (!runtime.BindingAvailable(action))
                return new TitanExecutionResult(action, null,
                    "installed-build binding unavailable; mutation remains read-only");
            var mutation = root.ExecuteChild(new TitanExecutionMutationIntent(runtime, action));
            return new TitanExecutionResult(action, mutation, mutation.Reason);
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
                    ? MutationRisk.Irreversible : MutationRisk.Reversible;
            }
        }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId { get { return _runtime.BindingId(_action) ?? string.Empty; } }
        public bool Required { get { return true; } }
        public bool CanCompensate
        {
            get { return _action.Kind != TitanExecutionActionKind.ReleaseAutokill; }
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
                return PreconditionResult.Hold("action is outside initial safe T1-T12 authority");
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
            if (!string.Equals(before.Epoch, after.Epoch, StringComparison.Ordinal)
                || !SynchronousCountersUnchanged(before, after))
                return VerificationResult<TitanExecutionSnapshot>.Failed(
                    "epoch/clock/kill counters changed inside one synchronous staging atom");
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
