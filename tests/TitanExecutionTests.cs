/*
FILE PURPOSE

TitanExecutionTests is the isolated pure/fault-injection suite for reconciled task 13. It proves
pre-due staging wins the native same-frame crossing, simultaneous due Titans consume in ascending
one-per-call order under one aggregate capacity reservation, and the synchronous kill verifier
accepts exactly one selected-version Bestiary increment plus exact clock reset. Version/loadout/
native-AK gates are sequential, reset interlock spans executable-due/active commitments without
deadlocking on an unfightable due clock, Walderp and exact Glop
prerequisites remain manual-only, and T13/T14 stage/enter/wait until durable reward evidence. T12 v4
online loot is cumulative and offline progress emits no items. A fake runtime proves each task-1
root executes at most one exact atom while initial live authority stays disabled. The suite loads no
Unity/game assembly, save, or process.
*/
using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Autopilot;
using NGUInjector.Managers;

namespace NGUInjector.Autopilot
{
    internal sealed class AutopilotConfig
    {
        internal bool Enabled = true;
        internal string Mode = "full";
        internal bool AutoEnterGame = true;
        internal bool AllowLegacyFallback = true;
        internal bool ManageAllocations = true;
        internal bool ManageBosses = true;
        internal bool ManageAdventure = true;
        internal bool ManageInventory = true;
        internal bool ManageDiggers = true;
        internal bool ManageYggdrasil = true;
        internal bool ManageQuests = true;
        internal bool ManageWishes = true;
        internal bool ManageCards = true;
        internal bool ManageCooking = true;
        internal bool ManageMoneyPit = true;
        internal bool ManageDailySpin = true;
        internal bool ManageBloodMagic = true;
        internal bool ManageBeards = true;
        internal bool AllowExpSpending = true;
        internal bool AllowApSpending = true;
        internal bool AllowPerkSpending = true;
        internal bool AllowQuirkSpending = true;
        internal bool AllowCardYeeting = true;
        internal bool AllowRebirths = true;
        internal bool AllowChallenges = true;
        internal bool AllowDifficultyExecution;
        internal bool AllowEndSequence;

        internal bool IsDryRun { get { return Mode != "assist" && Mode != "full"; } }
        internal bool IsAssist { get { return Mode == "assist"; } }
        internal bool IsFull { get { return Mode == "full"; } }
        internal string ExecutionFingerprint()
        {
            return Enabled + "|" + Mode + "|" + ManageAdventure + "|" + ManageInventory;
        }
    }

    internal sealed class AutopilotManager
    {
        internal AutopilotConfig Config;
    }
}

namespace NGUInjector.Managers
{
    // Exact task-11 pure API stand-in; the production compile binds the landed CombatManager.
    internal static class CombatManager
    {
        internal static int SelectWaldoResponseMove(int requestedMove, bool waldoSays,
            bool regularReady, bool strongReady, bool pierceReady, bool ultimateReady)
        {
            if (requestedMove < 3 || requestedMove > 6) return 0;
            var ready = new[] {regularReady, strongReady, pierceReady, ultimateReady};
            if (waldoSays) return ready[requestedMove - 3] ? requestedMove : 0;
            for (var move = 6; move >= 3; move--)
                if (move != requestedMove && ready[move - 3]) return move;
            return 0;
        }
    }
}

internal static class TitanExecutionTests
{
    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static void Equal(int actual, int expected, string message)
    {
        Assert(actual == expected,
            message + " (actual " + actual + ", expected " + expected + ")");
    }

    private static OrdinaryInventoryTopology Topology(int freeSlots)
    {
        var length = Math.Max(1, freeSlots);
        var ids = new int[length];
        var identities = new object[length];
        if (freeSlots == 0)
        {
            ids[0] = 900;
            identities[0] = new object();
        }
        return PhysicalTopology.CaptureOrdinary(ids, identities, length, 0);
    }

    private static TitanExecutionOpportunity Opportunity(int titanId, int remaining,
        int currentVersion, int desiredVersion, bool rewardActionable,
        bool projectedAk, bool nativeAk, int kills, int slots,
        bool manualReady = true, bool terminalMove = true, bool paused = false,
        int removableGlop = 99, int projectedEnemyActions = 0)
    {
        return new TitanExecutionOpportunity(titanId, currentVersion, desiredVersion,
            new TitanClockProjection(titanId, 100, remaining, paused,
                paused ? "Walderp paused" : string.Empty), true, rewardActionable,
            projectedAk, nativeAk, manualReady, terminalMove,
            TitanMechanics.EvaluateManualPrerequisites(titanId, desiredVersion,
                true, removableGlop, projectedEnemyActions), kills, slots);
    }

    private static TitanExecutionSnapshot Snapshot(bool online, bool autoKill,
        string stage, IEnumerable<TitanExecutionOpportunity> opportunities,
        WalderpExecutionSnapshot walderp = null, bool bindings = true,
        int currentZone = -1, bool currentEnemyIsTargetTitan = false)
    {
        return new TitanExecutionSnapshot("save-A/run-1", online, autoKill,
            stage, bindings, opportunities, walderp, currentZone,
            currentEnemyIsTargetTitan);
    }

    private static void TestSameFrameCrossingAndPrestage()
    {
        var elapsed = new double[12];
        var kills = new int[12];
        var spawns = Enumerable.Repeat(100, 12).ToArray();
        var mask = new bool[12];
        elapsed[0] = 99.9;
        mask[0] = true;
        var crossed = TitanNativeFrameFixture.Advance(
            new TitanNativeFrameState(elapsed, kills), .2, spawns, true, mask);
        Equal(crossed.KilledTitanId, 1,
            "native increments the clock and consumes the due Titan in the same frame");
        Equal(crossed.State.Kills(1), 1,
            "a ready-time callback observes the kill only after native already consumed it");

        var held = TitanNativeFrameFixture.Advance(
            new TitanNativeFrameState(elapsed, kills), .2, spawns, false, mask);
        Equal(held.KilledTitanId, 0,
            "pre-due autokill disable preserves the crossing for staged state");
        Assert(held.State.Elapsed(1) == 100.0,
            "disabled crossing caps ready elapsed without resetting the clock");

        var manager = new TitanExecutionManager(2.0);
        var plan = manager.Plan(Snapshot(true, true, string.Empty,
            new[] {Opportunity(1, 1, 0, 0, true, true, false, 0, 1)}),
            Topology(1));
        Assert(plan.Kind == TitanExecutionActionKind.DisableAutokill,
            "one second before due opens a commitment and first disables native autokill");
    }

    private static void TestSimultaneousDueAndAggregateCapacity()
    {
        var due = new[]
        {
            Opportunity(1, 1, 0, 0, true, true, true, 0, 3),
            Opportunity(2, 1, 0, 0, true, true, true, 0, 4)
        };
        var manager = new TitanExecutionManager(2.0);
        var plan = manager.Plan(Snapshot(true, true, string.Empty, due), Topology(7));
        Assert(plan.Kind == TitanExecutionActionKind.DisableAutokill
               && plan.Capacity.Admitted && plan.Capacity.RequiredFreeSlots == 7,
            "simultaneous due Titans reserve the sum of every adjacent-frame unswept batch");
        Assert(plan.TitanIds().SequenceEqual(new[] {1, 2}),
            "commitment order is native ascending Titan order");

        var heldManager = new TitanExecutionManager(2.0);
        var held = heldManager.Plan(Snapshot(true, true, string.Empty, due), Topology(6));
        Assert(held.Kind == TitanExecutionActionKind.Hold && !held.Capacity.Admitted,
            "aggregate capacity failure holds before disabling autokill or staging gear");

        var configuredManager = new TitanExecutionManager(2.0, new[] {77, 88}, true);
        var configuredStage = configuredManager.Plan(
            Snapshot(true, false, string.Empty, new[] {due[0]}), Topology(3));
        Assert(configuredStage.Kind == TitanExecutionActionKind.StageLoadout
               && configuredStage.LoadoutRequest.ValuesGold
               && configuredStage.LoadoutRequest.ConfiguredItemIds()
                   .SequenceEqual(new[] {77, 88}),
            "task-14/configured exact item IDs and Gold objective are pinned into the physical stage request");

        var elapsed = new double[12];
        elapsed[0] = elapsed[1] = 100.0;
        var masks = new bool[12];
        masks[0] = masks[1] = true;
        var spawns = Enumerable.Repeat(100, 12).ToArray();
        var first = TitanNativeFrameFixture.Advance(
            new TitanNativeFrameState(elapsed, new int[12]), 0, spawns, true, masks);
        var second = TitanNativeFrameFixture.Advance(first.State, 0, spawns, true, masks);
        Equal(first.KilledTitanId, 1, "first simultaneous Titan is consumed first");
        Equal(second.KilledTitanId, 2, "second due Titan is consumed on the adjacent frame");
    }

    private static void TestWalderpAndGlop()
    {
        var manager = new TitanExecutionManager(2.0);
        var waldo = Opportunity(5, 50, 0, 0, true, false, false, 0, 0,
            true, true, true);
        var paused = manager.Plan(Snapshot(true, false, string.Empty, new[] {waldo},
            new WalderpExecutionSnapshot(0, 1, false, 0, false,
                false, false, false, false)), Topology(0));
        Assert(paused.Kind == TitanExecutionActionKind.AwaitWalderpFind,
            "defeats ahead of finds before find four produces an explicit paused phase");

        var response = manager.Plan(Snapshot(true, false, string.Empty, new[] {waldo},
            new WalderpExecutionSnapshot(0, 1, true, 4, true,
                false, true, false, false)), Topology(0));
        Assert(response.Kind == TitanExecutionActionKind.WalderpResponse
               && response.WalderpMove == 4 && !response.LiveMutationAuthorized,
            "Waldo Says chooses exactly the requested ready task-11 move as telemetry-only policy");
        var different = manager.Plan(Snapshot(true, false, string.Empty, new[] {waldo},
            new WalderpExecutionSnapshot(1, 1, true, 4, false,
                false, true, false, true)), Topology(0));
        Equal(different.WalderpMove, 6,
            "without Says the strongest different ready damaging move is selected");

        var five = TitanMechanics.EvaluateManualPrerequisites(10, 0,
            false, 1, 5);
        var six = TitanMechanics.EvaluateManualPrerequisites(10, 0,
            false, 1, 6);
        Assert(five.Ready && five.RequiredGlopCopies == 1,
            "five projected enemy actions require exactly one removable Glop");
        Assert(!six.Ready && six.RequiredGlopCopies == 2,
            "six projected enemy actions require ceil(6/5), exactly two Glops");

        var nativeManager = new TitanExecutionManager(2.0);
        var native = nativeManager.Plan(Snapshot(true, true, string.Empty,
            new[] {Opportunity(10, 1, 0, 0, true, true, false, 0, 1,
                true, true, false, 0, 100)}), Topology(1));
        Assert(native.Kind == TitanExecutionActionKind.DisableAutokill,
            "a projected native autokill bypasses the manual Glop prerequisite");
    }

    private static void TestTerminalOnceAndRetry()
    {
        var firstRat = TitanMechanics.IsRewardActionable(13, false, false, false);
        var ratDone = TitanMechanics.IsRewardActionable(13, true, false, false);
        var t13 = new TitanExecutionManager(2.0).Plan(
            Snapshot(true, false, string.Empty,
                new[] {Opportunity(13, 0, 0, 0, firstRat, false, false, 0, 0)}),
            Topology(0));
        var t13Done = new TitanExecutionManager(2.0).Plan(
            Snapshot(true, false, string.Empty,
                new[] {Opportunity(13, 0, 0, 0, ratDone, false, false, 0, 0)}),
            Topology(0));
        Assert(t13.Kind == TitanExecutionActionKind.StageLoadout
               && t13.LiveMutationAuthorized
               && t13.LoadoutRequest.TitanIds().SequenceEqual(new[] {13}),
            "T13 opens a typed strongest-loadout commitment exactly once before the rat flag");
        Assert(t13Done.Kind == TitanExecutionActionKind.Idle,
            "T13 stops being actionable after the rat flag");

        var retry = TitanMechanics.IsRewardActionable(14, true, true, false);
        var delivered = TitanMechanics.IsRewardActionable(14, true, true, true);
        var t14 = new TitanExecutionManager(2.0).Plan(
            Snapshot(true, false, string.Empty,
                new[] {Opportunity(14, 0, 0, 0, retry, false, false, 0, 0)}),
            Topology(1));
        var t14Done = new TitanExecutionManager(2.0).Plan(
            Snapshot(true, false, string.Empty,
                new[] {Opportunity(14, 0, 0, 0, delivered, false, false, 0, 0)}),
            Topology(1));
        Assert(t14.Kind == TitanExecutionActionKind.StageLoadout
               && t14.LiveMutationAuthorized
               && t14.LoadoutRequest.TitanIds().SequenceEqual(new[] {14}),
            "T14 stages and retries despite its flag while ordinary item 495 is absent");
        Assert(t14Done.Kind == TitanExecutionActionKind.Idle,
            "ordinary item 495, not the final flag, completes the T14 reward");
        var noSlot = new TitanExecutionManager(2.0).Plan(
            Snapshot(true, false, string.Empty,
                new[] {Opportunity(14, 0, 0, 0, true, false, false, 0, 0)}),
            Topology(0));
        Assert(noSlot.Kind == TitanExecutionActionKind.Hold
               && noSlot.Capacity != null && !noSlot.Capacity.Admitted,
            "T14 unique item 495 delivery requires one exact usable ordinary slot");

        var noReservation = new TitanExecutionManager(2.0).Plan(
            Snapshot(true, false, string.Empty,
                new[] {Opportunity(14, 0, 0, 0, true, false, false, 0, 0,
                    true, false)}), Topology(1));
        Assert(noReservation.Kind == TitanExecutionActionKind.Hold,
            "terminal entry requires task-11's exact ready lethal first-move reservation");
    }

    private static void TestOnlineOfflineSplit()
    {
        var online = TitanExecutionManager.ProjectOnlineKill(12, 3);
        Assert(online.CallsNativeLoot && online.EquipmentEmissionPossible
               && online.CumulativeEndItems().SequenceEqual(new[] {483, 489, 493, 484}),
            "online T12 v4 invokes all four cumulative END opportunities in source order");
        Equal(TitanExecutionManager.T12WorstCaseTransientSlots(1), 11,
            "T12 v1 exact transient capacity");
        Equal(TitanExecutionManager.T12WorstCaseTransientSlots(2), 14,
            "T12 v2 exact transient capacity");
        Equal(TitanExecutionManager.T12WorstCaseTransientSlots(3), 16,
            "T12 v3 exact transient capacity");
        Equal(TitanExecutionManager.T12WorstCaseTransientSlots(4), 18,
            "T12 v4 exact transient capacity");
        var sourcePinnedCapacity = new TitanExecutionManager(2.0).Plan(
            Snapshot(true, true, string.Empty,
                new[] {Opportunity(12, 1, 3, 3, true, true, true, 0, 0)}),
            Topology(17));
        Assert(sourcePinnedCapacity.Kind == TitanExecutionActionKind.Hold
               && sourcePinnedCapacity.Capacity.RequiredFreeSlots == 18,
            "T12 v4 source-pinned 18-slot bound overrides an understated runtime hint");

        var offline = TitanExecutionManager.ProjectOffline(12, 3,
            0, 27000 * 5.0, 27000, true, 0);
        Assert(offline.CreditedKills == 5 && offline.SelectedVersionBestiaryAfter == 5,
            "offline v1 qualification credits five kills to the selected v4 bestiary record");
        Assert(!offline.CallsNativeLoot && !offline.EquipmentEmissionPossible,
            "offline Titan progress never calls the online loot path or emits equipment");
        var noV1 = TitanExecutionManager.ProjectOffline(12, 3,
            0, 27000 * 5.0, 27000, false, 0);
        Assert(noV1.CreditedKills == 0 && noV1.ClockElapsedAfter == 27000,
            "without v1 qualification offline merely caps the clock at ready");

        var preselect = new TitanExecutionManager(2.0).Plan(
            Snapshot(false, false, string.Empty,
                new[] {Opportunity(12, 100, 0, 3, true, false, false, 0, 18)}),
            Topology(18));
        Assert(preselect.Kind == TitanExecutionActionKind.OfflinePreselectVersion
               && preselect.Version == 3 && !preselect.LiveMutationAuthorized,
            "planned offline bootstrap exposes typed high-version preselection without live authority");
    }

    private static void TestTypedOneDueKillAndPostconditions()
    {
        var before = Snapshot(true, false, "stage-A", new[]
        {
            Opportunity(1, 0, 0, 0, true, true, true, 10, 1),
            Opportunity(2, 0, 0, 0, true, true, true, 20, 1)
        });
        var exact = Snapshot(true, false, "stage-A", new[]
        {
            Opportunity(1, 100, 0, 0, true, true, true, 11, 1),
            Opportunity(2, 0, 0, 0, true, true, true, 20, 1)
        });
        Assert(TitanExecutionMutationIntent.ExactOneTitanKillDelta(before, exact, 1),
            "one selected-version Bestiary increment plus exact target clock reset is accepted");

        var wrongTarget = Snapshot(true, false, "stage-A", new[]
        {
            Opportunity(1, 0, 0, 0, true, true, true, 10, 1),
            Opportunity(2, 100, 0, 0, true, true, true, 21, 1)
        });
        Assert(!TitanExecutionMutationIntent.ExactOneTitanKillDelta(before, wrongTarget, 1),
            "a different native-order Titan delta cannot verify the requested target");
        var twoKills = Snapshot(true, false, "stage-A", new[]
        {
            Opportunity(1, 100, 0, 0, true, true, true, 11, 1),
            Opportunity(2, 100, 0, 0, true, true, true, 21, 1)
        });
        Assert(!TitanExecutionMutationIntent.ExactOneTitanKillDelta(before, twoKills, 1),
            "two Titan deltas in one synchronous call fail the one-frame contract");
        var noClockReset = Snapshot(true, false, "stage-A", new[]
        {
            Opportunity(1, 0, 0, 0, true, true, true, 11, 1),
            Opportunity(2, 0, 0, 0, true, true, true, 20, 1)
        });
        Assert(!TitanExecutionMutationIntent.ExactOneTitanKillDelta(before, noClockReset, 1),
            "a counter increment without the exact target clock reset is not a verified kill");

        var manager = new TitanExecutionManager(2.0);
        var due = new[]
        {
            Opportunity(1, 0, 0, 0, true, false, true, 10, 1),
            Opportunity(2, 0, 0, 0, true, false, true, 20, 1)
        };
        var stage = manager.Plan(Snapshot(true, false, string.Empty, due), Topology(2));
        Assert(stage.Kind == TitanExecutionActionKind.StageLoadout,
            "a weak pre-stage projection still stages the strongest combat loadout before the live gate");
        var first = manager.Plan(Snapshot(true, false, stage.CommitmentId, due), Topology(2));
        Assert(first.Kind == TitanExecutionActionKind.KillOneDueTitan && first.TitanId == 1,
            "the first transaction requests only the lowest native-order due Titan");
        var afterFirst = new[]
        {
            Opportunity(1, 100, 0, 0, true, true, true, 11, 1),
            Opportunity(2, 0, 0, 0, true, true, true, 20, 1)
        };
        var second = manager.Plan(Snapshot(true, false, stage.CommitmentId, afterFirst),
            Topology(2));
        Assert(second.Kind == TitanExecutionActionKind.KillOneDueTitan && second.TitanId == 2,
            "the next scheduler frame requests only the next still-due committed Titan");
    }

    private static void TestTerminalCommitmentAndResetInterlock()
    {
        var unfightableManager = new TitanExecutionManager(2.0);
        var unfightable = Opportunity(1, 0, 0, 0, true, false, false, 0, 1,
            false, false);
        var unfightableSnapshot = Snapshot(true, false, string.Empty,
            new[] {unfightable});
        Assert(!unfightableManager.ResetInterlock(unfightableSnapshot).HoldReset,
            "a due Titan without source-proven native/manual execution is a clock-loss cost, not a permanent reset deadlock");
        var attemptedStage = unfightableManager.Plan(unfightableSnapshot, Topology(1));
        var abortRestore = unfightableManager.Plan(Snapshot(true, false,
            attemptedStage.CommitmentId, new[] {unfightable}), Topology(1));
        Assert(attemptedStage.Kind == TitanExecutionActionKind.StageLoadout
               && abortRestore.Kind == TitanExecutionActionKind.RestoreLoadout,
            "the executor may test the strongest loadout once, then cleans up immediately when the due native predicate is still false");

        var manager = new TitanExecutionManager(2.0);
        var due = Opportunity(14, 0, 0, 0, true, false, false, 0, 0,
            true, true);
        var dueSnapshot = Snapshot(true, false, string.Empty, new[] {due});
        Assert(manager.ResetInterlock(dueSnapshot).HoldReset,
            "a due actionable terminal Titan blocks reset even before a commitment opens");
        var stage = manager.Plan(dueSnapshot, Topology(1));
        Assert(stage.Kind == TitanExecutionActionKind.StageLoadout
               && stage.LoadoutRequest.TitanIds().SequenceEqual(new[] {14}),
            "T14 accepts a physical strongest-loadout stage request");
        Assert(manager.ResetInterlock(dueSnapshot).HoldReset,
            "an active terminal commitment keeps reset blocked across staging");

        var staged = Snapshot(true, false, stage.CommitmentId, new[] {due});
        var enter = manager.Plan(staged, Topology(1));
        Assert(enter.Kind == TitanExecutionActionKind.EnterManualTitan
               && enter.LiveMutationAuthorized,
            "staged due T14 emits one typed zone-entry mutation");
        var waiting = manager.Plan(Snapshot(true, false, stage.CommitmentId,
            new[] {due}, null, true, TitanMechanics.Describe(14).Zone, true), Topology(1));
        Assert(waiting.Kind == TitanExecutionActionKind.AwaitManualTitanKill,
            "after exact zone entry CombatManager owns the reserved lethal move while the stage stays locked");

        var delivered = Opportunity(14, 100, 0, 0, false, false, false, 1, 0,
            true, true);
        var restore = manager.Plan(Snapshot(true, false, stage.CommitmentId,
            new[] {delivered}), Topology(1));
        Assert(restore.Kind == TitanExecutionActionKind.RestoreLoadout,
            "ordinary item-495 reward evidence advances before terminal loadout cleanup");
        var complete = manager.Plan(Snapshot(true, false, string.Empty,
            new[] {delivered}), Topology(1));
        Assert(complete.Kind == TitanExecutionActionKind.CommitmentComplete,
            "terminal commitment completes only after durable reward and gear restoration evidence");
        Assert(!manager.ResetInterlock(Snapshot(true, false, string.Empty,
                new[] {delivered})).HoldReset,
            "reset is released only after the terminal reward and cleanup commitment settles");
    }

    private sealed class FakeTitanRuntime : ITitanExecutionRuntime
    {
        internal bool Authority = true;
        internal bool Bindings = true;
        internal bool Online = true;
        internal bool AutoKill = true;
        internal string Stage = string.Empty;
        internal bool NativeVerified;
        internal int Version;
        internal int DesiredVersion = 3;
        internal int Kills;
        internal int Remaining;
        internal int ApplyCalls;

        public bool LiveAuthority { get { return Authority; } }
        public string BindingId(TitanExecutionAction action)
        {
            return "test.titan." + action.Kind;
        }
        public bool BindingAvailable(TitanExecutionAction action) { return Bindings; }
        public OrdinaryInventoryTopology CaptureOrdinaryTopology() { return Topology(18); }
        public TitanExecutionSnapshot Capture()
        {
            return Snapshot(Online, AutoKill, Stage,
                new[] {Opportunity(12, Remaining, Version, DesiredVersion, true,
                    true, NativeVerified, Kills, 18)} , null, Bindings);
        }
        public TitanExecutionApplyResult Apply(TitanExecutionAction action,
            RootTransactionToken token)
        {
            ApplyCalls++;
            switch (action.Kind)
            {
                case TitanExecutionActionKind.DisableAutokill:
                    AutoKill = false;
                    break;
                case TitanExecutionActionKind.SelectVersion:
                    Version = action.Version;
                    break;
                case TitanExecutionActionKind.StageLoadout:
                    Stage = action.CommitmentId;
                    break;
                case TitanExecutionActionKind.RestoreAutokillPreference:
                    AutoKill = true;
                    break;
                case TitanExecutionActionKind.KillOneDueTitan:
                    // Model the adapter's synchronous true/invoke/finally-false boundary. The
                    // externally visible setting never rises, exactly one Bestiary record moves,
                    // and capture observes the freshly reset clock.
                    AutoKill = true;
                    Kills++;
                    Remaining = 100;
                    AutoKill = false;
                    break;
                case TitanExecutionActionKind.RestoreLoadout:
                    Stage = string.Empty;
                    break;
            }
            return new TitanExecutionApplyResult(true, "fake exact invocation");
        }
        public CompensationResult Compensate(TitanExecutionAction action,
            TitanExecutionSnapshot before, RecoveryToken token)
        {
            AutoKill = before.AutoKillEnabled;
            Stage = before.LoadoutStageId;
            var opportunity = before.Find(12);
            if (opportunity != null) Version = opportunity.CurrentVersion;
            return CompensationResult.Restored("fake exact restoration");
        }
    }

    private static TitanExecutionResult ExecuteAtom(TitanExecutionManager manager,
        FakeTitanRuntime runtime, MutationCoordinator coordinator,
        AutopilotConfig config, string name)
    {
        using (var root = coordinator.BeginRoot(name, config).Transaction)
            return manager.ExecuteNext(root, runtime);
    }

    private static void TestCandidateNativeVerificationAndAtoms()
    {
        var config = new AutopilotConfig();
        var runtime = new FakeTitanRuntime();
        var manager = new TitanExecutionManager(2.0);
        var coordinator = new MutationCoordinator(() => "save-A/run-1");

        var disabled = ExecuteAtom(manager, runtime, coordinator, config, "authority-off");
        Assert(disabled.Action.Kind == TitanExecutionActionKind.DisableAutokill
               && disabled.Mutation == null && runtime.ApplyCalls == 0,
            "initial live authority is disabled and invokes no native method");

        manager.EnableSafeT1ThroughT12Authority(true);
        var disable = ExecuteAtom(manager, runtime, coordinator, config, "disable-ak");
        Assert(disable.Mutation.Kind == MutationResultKind.Committed
               && !runtime.AutoKill && runtime.ApplyCalls == 1,
            "disable-autokill is one exact task-1 atom");
        var select = ExecuteAtom(manager, runtime, coordinator, config, "select-version");
        Assert(select.Action.Kind == TitanExecutionActionKind.SelectVersion
               && select.Mutation.Kind == MutationResultKind.Committed
               && runtime.Version == 3 && runtime.ApplyCalls == 2,
            "version selection is a separate exact atom followed by replanning");
        var stage = ExecuteAtom(manager, runtime, coordinator, config, "stage-loadout");
        Assert(stage.Action.Kind == TitanExecutionActionKind.StageLoadout
               && stage.Mutation.Kind == MutationResultKind.Committed
               && !string.IsNullOrEmpty(runtime.Stage) && runtime.ApplyCalls == 3,
            "common exact-reference loadout staging is one separate verified atom");

        runtime.Remaining = 1;
        var waiting = ExecuteAtom(manager, runtime, coordinator, config, "not-due-yet");
        Assert(waiting.Action.Kind == TitanExecutionActionKind.AwaitCommittedKills
               && waiting.Mutation == null && runtime.ApplyCalls == 3 && !runtime.AutoKill,
            "physical staging remains locked while the committed clock has not reached due");
        runtime.Remaining = 0;
        runtime.NativeVerified = true;
        var kill = ExecuteAtom(manager, runtime, coordinator, config, "native-true");
        Assert(kill.Action.Kind == TitanExecutionActionKind.KillOneDueTitan
               && kill.Mutation.Kind == MutationResultKind.Committed
               && !runtime.AutoKill && runtime.Kills == 1 && runtime.Remaining == 100
               && runtime.ApplyCalls == 4,
            "live native predicate authorizes one synchronous verified kill while persistent autokill stays low");

        var restore = ExecuteAtom(manager, runtime, coordinator, config, "restore-loadout");
        var restoreAk = ExecuteAtom(manager, runtime, coordinator, config, "restore-ak");
        var complete = ExecuteAtom(manager, runtime, coordinator, config, "complete");
        Assert(restore.Action.Kind == TitanExecutionActionKind.RestoreLoadout
               && restoreAk.Action.Kind == TitanExecutionActionKind.RestoreAutokillPreference
               && complete.Action.Kind == TitanExecutionActionKind.CommitmentComplete,
            "exact synchronous kill delta drives physical restore, preference restore, completion");
        Equal(runtime.ApplyCalls, 6,
            "six mutation plans each invoke exactly one atom; holds/completion invoke none");

        var missingBinding = new TitanExecutionManager(2.0).Plan(
            Snapshot(true, true, string.Empty,
                new[] {Opportunity(1, 1, 0, 0, true, true, true, 0, 1)},
                null, false), Topology(1));
        Assert(missingBinding.Kind == TitanExecutionActionKind.Hold,
            "unknown installed-build bindings stay read-only before any autokill change");
    }

    public static int Main()
    {
        try
        {
            TestSameFrameCrossingAndPrestage();
            TestSimultaneousDueAndAggregateCapacity();
            TestWalderpAndGlop();
            TestTerminalOnceAndRetry();
            TestOnlineOfflineSplit();
            TestTypedOneDueKillAndPostconditions();
            TestTerminalCommitmentAndResetInterlock();
            TestCandidateNativeVerificationAndAtoms();
            Console.WriteLine("Titan execution tests passed: " + _assertions + " assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}

namespace NGUInjector
{
    internal sealed class SettingsStub
    {
        internal bool GlobalEnabled = true;
    }

    internal static class Main
    {
        internal static NGUInjector.Autopilot.AutopilotManager Autopilot;
        internal static SettingsStub Settings = new SettingsStub();
        internal static void LogAction(string category, string detail) { }
    }
}
