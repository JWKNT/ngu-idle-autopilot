#define ENDGAME_TRANSACTION_TEST_STUBS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

Purpose: This controller-free executable regression-tests audit task 9's terminal mutation
boundaries without loading Unity, a save, or the installed game assembly.

Mechanism: A deterministic IEndgameTransactionPort models physical ordinary identities, exact
filters, a protected END Card, six Mayo balances, Blood, ambient swaps, and END panels. The real
MutationCoordinator executes production intents. Fault injection can throw before or after every
individual sparse swap; every case must finish with the original object reference in every slot.

Inputs and outputs: In-memory topologies, explicit native-call modes, and a read-only Main source
integration check are the inputs. Assertions
cover task-6 capacity, filter restoration, exact debits/credits, cross-rebirth Blood commitment,
typed quarantine, sparse rollback, panel postconditions, and END-Blood-before-rebirth wiring. A
success line with assertion count is the only output.

Invariants and safety: Tests never call Character/controllers, mutate runtime configuration, enable
the checked-in AllowEndSequence default, inject a DLL, steer RNG, or restart the game. Test-local
permission stubs grant full mode only to isolated MutationCoordinator roots.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class NativeInvocationResult
    {
        internal bool InvocationAttempted;
        internal bool ReturnedNormally;
        internal string Reason;
        internal Exception Exception;
    }

    internal static class NativeBindingKeys
    {
        internal const string CardConsume = "cards.consume";
        internal const string ItemConsume = "inventory.item.consume";
    }

    internal sealed class EndCardFilterSnapshot
    {
        internal readonly bool StateKnown;
        internal readonly bool ItemFiltered;
        internal readonly bool LootFilter;
        internal readonly bool FilterOn;
        internal readonly bool FilterMisc;

        internal EndCardFilterSnapshot(bool known, bool item, bool loot, bool on, bool misc)
        {
            StateKnown = known;
            ItemFiltered = item;
            LootFilter = loot;
            FilterOn = on;
            FilterMisc = misc;
        }
    }

    internal sealed class EndCardHandoffPlan
    {
        internal readonly bool ReadyForTerminalTransaction;
        internal readonly bool StopDuplicateConsume;
        internal readonly string Reason;

        internal EndCardHandoffPlan(bool ready, bool stop, string reason)
        {
            ReadyForTerminalTransaction = ready;
            StopDuplicateConsume = stop;
            Reason = reason ?? string.Empty;
        }
    }

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
            return Enabled + "|" + Mode + "|" + ManageInventory + "|" + ManageCards
                   + "|" + ManageBloodMagic + "|" + AllowEndSequence;
        }
    }

    internal sealed class AutopilotManager
    {
        internal AutopilotConfig Config;
    }

    internal enum FakeDeliveryMode
    {
        Success,
        DebitWithoutCredit,
        NoOp
    }

    internal enum FakePanelMode
    {
        Success,
        NoOp,
        Partial
    }

    internal sealed class FakeEndCard
    {
        internal bool Protected = true;
        internal readonly int[] Cost = Enumerable.Repeat(99, 6).ToArray();
    }

    internal sealed class FakeEndgamePort : IEndgameTransactionPort
    {
        private int[] _ids;
        private object[] _identities;
        private readonly Dictionary<object, int> _levels = new Dictionary<object, int>();
        private readonly Dictionary<int, EndCardFilterSnapshot> _filters =
            new Dictionary<int, EndCardFilterSnapshot>();
        private readonly HashSet<int> _recoverable = new HashSet<int>();
        private readonly List<FakeEndCard> _cards = new List<FakeEndCard>();
        private float[] _panelX = {-5000f, -5000f, -5000f, -5000f};
        private float[] _panelY = {-5000f, -5000f, -5000f, -5000f};
        private int _swapCall;
        private bool _swapFaultSpent;

        internal string Epoch = "save-A/run-1";
        internal bool Stable = true;
        internal int CurrentSpaces;
        internal int ReservedPrefix;
        internal int[] Mayo = Enumerable.Repeat(99, 6).ToArray();
        internal double Blood;
        internal double BloodCost = MechanicsEndgame.EndBloodCost;
        internal FakeDeliveryMode CardMode = FakeDeliveryMode.Success;
        internal FakeDeliveryMode BloodMode = FakeDeliveryMode.Success;
        internal FakePanelMode PanelMode = FakePanelMode.Success;
        internal int CardCalls;
        internal int BloodCalls;
        internal int PanelCalls;
        internal bool SawCardFilterExemption;
        internal bool SawBloodFilterExemption;
        internal int SwapFaultCall = -1;
        internal bool SwapFaultAfterMutation;

        internal FakeEndgamePort(int slots, bool fill = false)
        {
            _ids = new int[slots];
            _identities = new object[slots];
            CurrentSpaces = slots;
            for (var i = 0; fill && i < slots; i++) Put(i, 1000 + i, 1);
            SetFilter(492, new EndCardFilterSnapshot(true, true, true, true, true));
            SetFilter(494, new EndCardFilterSnapshot(true, true, true, true, true));
        }

        public string EpochFingerprint { get { return Epoch; } }
        public bool InventoryStable { get { return Stable; } }
        public double BloodPoints { get { return Blood; } }
        public double EndBloodCost { get { return BloodCost; } }
        public int CardDeckCount { get { return _cards.Count; } }

        internal object Put(int slot, int id, int level)
        {
            var identity = new object();
            _ids[slot] = id;
            _identities[slot] = identity;
            _levels[identity] = level;
            return identity;
        }

        internal void Clear(int slot)
        {
            if (_identities[slot] != null) _levels.Remove(_identities[slot]);
            _ids[slot] = 0;
            _identities[slot] = null;
        }

        internal FakeEndCard AddEndCard()
        {
            var card = new FakeEndCard();
            _cards.Add(card);
            return card;
        }

        internal void AddRecoverable(int id) { _recoverable.Add(id); }

        internal void SetFilter(int id, EndCardFilterSnapshot filter) { _filters[id] = filter; }

        internal OrdinaryInventoryTopology Topology() { return CaptureOrdinaryTopology(); }

        internal int[] Ids() { return (int[])_ids.Clone(); }

        internal object[] Identities() { return (object[])_identities.Clone(); }

        public OrdinaryInventoryTopology CaptureOrdinaryTopology()
        {
            return PhysicalTopology.CaptureOrdinary(_ids, _identities,
                CurrentSpaces, ReservedPrefix);
        }

        public bool HasRecoverableCopy(int itemId) { return _recoverable.Contains(itemId); }
        public int OrdinaryLevel(object identity)
        {
            int level;
            return identity != null && _levels.TryGetValue(identity, out level) ? level : -1;
        }

        public EndCardFilterSnapshot CaptureDeliveryFilters(int itemId)
        {
            EndCardFilterSnapshot value;
            return _filters.TryGetValue(itemId, out value)
                ? new EndCardFilterSnapshot(value.StateKnown, value.ItemFiltered,
                    value.LootFilter, value.FilterOn, value.FilterMisc)
                : new EndCardFilterSnapshot(false, false, false, false, false);
        }

        public void InstallDeliveryFilterExemption(int itemId)
        {
            var value = CaptureDeliveryFilters(itemId);
            _filters[itemId] = new EndCardFilterSnapshot(value.StateKnown, false,
                value.LootFilter, value.FilterOn, false);
            if (itemId == 492) SawCardFilterExemption = !CurrentFiltered(itemId);
            if (itemId == 494) SawBloodFilterExemption = !CurrentFiltered(itemId);
        }

        public void RestoreDeliveryFilters(int itemId, EndCardFilterSnapshot snapshot)
        {
            SetFilter(itemId, snapshot);
        }

        public object[] CaptureEndCards() { return _cards.Cast<object>().ToArray(); }
        public bool CardIsProtected(object cardIdentity)
        {
            var card = cardIdentity as FakeEndCard;
            return card != null && _cards.Contains(card) && card.Protected;
        }

        public int[] CaptureCardCost(object cardIdentity)
        {
            var card = cardIdentity as FakeEndCard;
            return card == null ? new int[0] : (int[])card.Cost.Clone();
        }

        public int[] CaptureMayoAmounts() { return (int[])Mayo.Clone(); }

        public EndgameNativeCall ToggleCardProtection(object cardIdentity)
        {
            var card = cardIdentity as FakeEndCard;
            if (card == null || !_cards.Contains(card))
                return new EndgameNativeCall(false, false, "missing card");
            card.Protected = !card.Protected;
            return new EndgameNativeCall(true, true, string.Empty);
        }

        public EndgameNativeCall ConsumeCard(object cardIdentity)
        {
            CardCalls++;
            var card = cardIdentity as FakeEndCard;
            if (card == null || !_cards.Contains(card) || card.Protected)
                return new EndgameNativeCall(false, false, "card unavailable/protected");
            if (CardMode == FakeDeliveryMode.NoOp)
                return new EndgameNativeCall(true, true, "normal-return no-op");
            for (var i = 0; i < Mayo.Length; i++) Mayo[i] -= card.Cost[i];
            _cards.Remove(card);
            if (CardMode == FakeDeliveryMode.Success) Deliver(492);
            return new EndgameNativeCall(true, true, string.Empty);
        }

        public EndgameNativeCall CastEndBlood()
        {
            BloodCalls++;
            if (BloodMode == FakeDeliveryMode.NoOp)
                return new EndgameNativeCall(true, true, "normal-return no-op");
            Blood = 0.0;
            if (BloodMode == FakeDeliveryMode.Success) Deliver(494);
            return new EndgameNativeCall(true, true, string.Empty);
        }

        public void SwapOrdinary(int firstSlot, int secondSlot)
        {
            var call = _swapCall++;
            if (!_swapFaultSpent && call == SwapFaultCall && !SwapFaultAfterMutation)
            {
                _swapFaultSpent = true;
                throw new InvalidOperationException("swap-before-" + call);
            }
            Swap(_ids, firstSlot, secondSlot);
            Swap(_identities, firstSlot, secondSlot);
            if (!_swapFaultSpent && call == SwapFaultCall && SwapFaultAfterMutation)
            {
                _swapFaultSpent = true;
                throw new InvalidOperationException("swap-after-" + call);
            }
        }

        public EndgamePanelState CaptureEndPanels()
        {
            return new EndgamePanelState(_panelX, _panelY);
        }

        public EndgameNativeCall TriggerEndPanel()
        {
            PanelCalls++;
            if (PanelMode == FakePanelMode.Success)
            {
                _panelX[0] = 0f;
                _panelY[0] = 0f;
                for (var i = 1; i < _panelX.Length; i++)
                {
                    _panelX[i] = -5000f;
                    _panelY[i] = -5000f;
                }
            }
            else if (PanelMode == FakePanelMode.Partial)
            {
                _panelX[0] = 0f;
                _panelY[0] = 0f;
                _panelX[1] = -4000f;
            }
            return new EndgameNativeCall(true, true, string.Empty);
        }

        private bool CurrentFiltered(int itemId)
        {
            var f = CaptureDeliveryFilters(itemId);
            return f.LootFilter && f.ItemFiltered || f.FilterOn && f.FilterMisc;
        }

        private void Deliver(int itemId)
        {
            if (CurrentFiltered(itemId)) return;
            for (var i = ReservedPrefix; i < CurrentSpaces; i++)
            {
                if (_ids[i] != 0) continue;
                Put(i, itemId, 100);
                return;
            }
        }

        private static void Swap<T>(T[] values, int first, int second)
        {
            var value = values[first];
            values[first] = values[second];
            values[second] = value;
        }
    }
}

namespace NGUInjector
{
    using NGUInjector.Autopilot;

    internal sealed class SavedSettings
    {
        internal bool GlobalEnabled = true;
    }

    internal static class Main
    {
        internal static AutopilotManager Autopilot;
        internal static SavedSettings Settings = new SavedSettings();
        internal static readonly List<string> Holds = new List<string>();

        internal static void Log(string message) { Holds.Add(message); }
        internal static void LogAction(string category, string message)
        {
            Holds.Add(category + ":" + message);
        }
    }
}

internal static class EndgameTransactionTests
{
    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        _assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception("FAIL: " + message + " expected=" + expected
                                + " actual=" + actual);
    }

    private static AutopilotConfig Config(bool allowEnd = false)
    {
        return new AutopilotConfig {AllowEndSequence = allowEnd};
    }

    private static RootTransaction Begin(MutationCoordinator coordinator,
        AutopilotConfig config)
    {
        var begin = coordinator.BeginRoot("endgame-tests", config);
        Assert(begin.Status == RootBeginStatus.Begun && begin.Transaction != null,
            "isolated full-mode root begins");
        return begin.Transaction;
    }

    private static EndCardHandoffPlan ReadyHandoff()
    {
        return new EndCardHandoffPlan(true, false, "ready");
    }

    private static FakeEndgamePort SparsePort()
    {
        var port = new FakeEndgamePort(48);
        for (var i = 0; i < 16; i++) port.Put(39 - i, 480 + i, 100);
        return port;
    }

    private static void TestEveryUniqueDeliveryUsesPhysicalCapacity()
    {
        var free = new FakeEndgamePort(1).Topology();
        var full = new FakeEndgamePort(1, true).Topology();
        for (var id = MechanicsEndgame.FirstEndItemId;
             id <= MechanicsEndgame.LastEndItemId; id++)
        {
            var admitted = EndgameTransactionMechanics.ProveUniqueDelivery(free, id);
            Assert(admitted.Admitted && admitted.RequiredFreeSlots == 1,
                "END unique delivery " + id + " requires and admits one physical slot");
            Assert(!EndgameTransactionMechanics.ProveUniqueDelivery(full, id).Admitted,
                "END unique delivery " + id + " rejects zero physical slots");
        }
    }

    private static void TestEndCardFilterSafeExactDebitAndNoSlot()
    {
        var port = new FakeEndgamePort(2);
        port.AddEndCard();
        var coordinator = new MutationCoordinator(() => port.Epoch);
        using (var root = Begin(coordinator, Config()))
        {
            var result = new EndgameTransactionManager(port)
                .TryDeliverEndCard(root, ReadyHandoff());
            Equal(MutationResultKind.Committed, result.Kind,
                "filter-safe END Card commits only with exact ordinary delivery");
        }
        Assert(port.SawCardFilterExemption,
            "END Card invoke observes both exact and coarse filters exempted");
        Assert(port.CaptureDeliveryFilters(492).ItemFiltered
               && port.CaptureDeliveryFilters(492).FilterMisc,
            "unsafe END Card filters are restored after success");
        Assert(port.Mayo.All(x => x == 0), "END Card debits exact 99x6 Mayo");
        Equal(1, port.Topology().CountOrdinaryItem(492),
            "END Card produces one ordinary item 492");

        var noSlot = new FakeEndgamePort(1, true);
        noSlot.AddEndCard();
        var heldCoordinator = new MutationCoordinator(() => noSlot.Epoch);
        using (var root = Begin(heldCoordinator, Config()))
        {
            var result = new EndgameTransactionManager(noSlot)
                .TryDeliverEndCard(root, ReadyHandoff());
            Equal(MutationResultKind.Held, result.Kind,
                "END Card is held without an exact ordinary slot");
        }
        Equal(0, noSlot.CardCalls, "no-slot END Card never reaches native consume");
        Assert(noSlot.Mayo.All(x => x == 99), "no-slot END Card preserves all six Mayos");
    }

    private static void TestEndCardDebitWithoutCreditQuarantines()
    {
        var port = new FakeEndgamePort(2) {CardMode = FakeDeliveryMode.DebitWithoutCredit};
        port.AddEndCard();
        var coordinator = new MutationCoordinator(() => port.Epoch);
        using (var root = Begin(coordinator, Config()))
        {
            var manager = new EndgameTransactionManager(port);
            var result = manager.TryDeliverEndCard(root, ReadyHandoff());
            Equal(MutationResultKind.Quarantined, result.Kind,
                "END Card debit without physical credit quarantines Cards");
            string reason;
            Assert(coordinator.IsQuarantined(MutationClass.Cards, out reason),
                "Cards class records irreversible-loss quarantine");
            var retry = manager.TryDeliverEndCard(root, ReadyHandoff());
            Equal(MutationResultKind.Held, retry.Kind,
                "quarantined Cards class refuses automatic retry");
        }
        Equal(1, port.CardCalls, "debit-without-credit never consumes a second END Card");
        Assert(port.CaptureDeliveryFilters(492).ItemFiltered
               && port.CaptureDeliveryFilters(492).FilterMisc,
            "filters restore even after irreversible Card loss");
    }

    private static void TestBloodCommitmentAndLostDeliveryQuarantine()
    {
        var port = new FakeEndgamePort(2) {Blood = MechanicsEndgame.EndBloodCost};
        var manager = new EndgameTransactionManager(port);
        var early = manager.ObserveBloodCommitment(false);
        Assert(!early.Active, "Blood commitment does not open before planned reset work finishes");
        var open = manager.ObserveBloodCommitment(true);
        Assert(open.Active && open.OpenedNow && open.BlocksReset && open.BlocksChallenge
               && open.BlocksOtherBloodSpells,
            "post-reset positive Blood opens all three commitment interlocks");

        port.BloodMode = FakeDeliveryMode.DebitWithoutCredit;
        var coordinator = new MutationCoordinator(() => port.Epoch);
        using (var root = Begin(coordinator, Config()))
        {
            var result = manager.TryDeliverEndBlood(root);
            Equal(MutationResultKind.Quarantined, result.Kind,
                "lost Blood without item 494 quarantines BloodMagic");
            string reason;
            Assert(coordinator.IsQuarantined(MutationClass.BloodMagic, out reason),
                "BloodMagic class records lost-delivery quarantine");
        }
        Assert(manager.BloodCommitmentActive,
            "lost-no-item quarantine leaves cross-rebirth Blood commitment active");
        Assert(port.SawBloodFilterExemption,
            "Blood invoke observes both exact and coarse filters exempted");
        Assert(port.CaptureDeliveryFilters(494).ItemFiltered
               && port.CaptureDeliveryFilters(494).FilterMisc,
            "Blood delivery filters restore after irreversible loss");

        var successPort = new FakeEndgamePort(2) {Blood = MechanicsEndgame.EndBloodCost};
        var successManager = new EndgameTransactionManager(successPort);
        successManager.ObserveBloodCommitment(true);
        var successCoordinator = new MutationCoordinator(() => successPort.Epoch);
        using (var root = Begin(successCoordinator, Config()))
        {
            var result = successManager.TryDeliverEndBlood(root);
            Equal(MutationResultKind.Committed, result.Kind,
                "Blood commits only with zero pool and new ordinary level-100 item 494");
        }
        Assert(!successManager.BloodCommitmentActive,
            "physical item 494 releases the cross-rebirth commitment");
    }

    private static void TestEverySparseSwapFaultRestoresIdentity()
    {
        var seed = SparsePort();
        var plan = EndgameTransactionMechanics.PlanSparsePlacement(seed.Topology());
        Assert(plan.Actionable && plan.Swaps.Length > 1,
            "scrambled fixture requires multiple sparse swaps");
        for (var fault = 0; fault < plan.Swaps.Length; fault++)
        {
            for (var after = 0; after < 2; after++)
            {
                var port = SparsePort();
                var original = port.Topology();
                port.SwapFaultCall = fault;
                port.SwapFaultAfterMutation = after == 1;
                var coordinator = new MutationCoordinator(() => port.Epoch);
                using (var root = Begin(coordinator, Config()))
                {
                    var result = root.ExecuteChild(new SparsePlacementIntent(port), port);
                    Assert(result.Kind == MutationResultKind.Compensated
                           || fault == 0 && after == 0
                              && result.Kind == MutationResultKind.RejectedUnchanged,
                        "swap fault " + fault + " (after=" + after
                        + ") is compensated or proven unchanged");
                }
                Assert(EndgameTransactionMechanics.ExactTopology(original, port.Topology()),
                    "swap fault " + fault + " (after=" + after
                    + ") restores every exact identity-at-slot");
            }
        }
    }

    private static void TestPanelNoOpRollsBackAndNeverLatches()
    {
        var port = SparsePort();
        port.PanelMode = FakePanelMode.NoOp;
        var original = port.Topology();
        var coordinator = new MutationCoordinator(() => port.Epoch);
        var manager = new EndgameTransactionManager(port);
        using (var root = Begin(coordinator, Config(true)))
        {
            var report = manager.TryStartEndSequence(root, Config(true));
            Assert(report.Attempted && !report.Latched && report.PlacementRestored,
                "normal-return panel no-op rolls sparse placement back and does not latch");
            Equal(MutationResultKind.RejectedUnchanged, report.Panel.Kind,
                "normal-return panel no-op is rejected by exact UI postcondition");
        }
        Assert(!manager.EndSequenceLatched, "panel no-op leaves session END latch false");
        Assert(EndgameTransactionMechanics.ExactTopology(original, port.Topology()),
            "panel no-op restores original exact inventory topology");
    }

    private static void TestPartialPanelQuarantinesButInventoryStillRollsBack()
    {
        var port = SparsePort();
        port.PanelMode = FakePanelMode.Partial;
        var original = port.Topology();
        var coordinator = new MutationCoordinator(() => port.Epoch);
        var manager = new EndgameTransactionManager(port);
        using (var root = Begin(coordinator, Config(true)))
        {
            var report = manager.TryStartEndSequence(root, Config(true));
            Assert(!report.Latched && report.TerminalQuarantine && report.PlacementRestored,
                "partial END panel state quarantines terminal execution and restores inventory");
            Equal(MutationResultKind.Quarantined, report.Panel.Kind,
                "partial panel mutation is an irreversible EndSequence quarantine");
        }
        Assert(EndgameTransactionMechanics.ExactTopology(original, port.Topology()),
            "EndSequence quarantine does not prevent separate Inventory rollback");
    }

    private static void TestExactPanelPostconditionLatches()
    {
        var port = SparsePort();
        var coordinator = new MutationCoordinator(() => port.Epoch);
        var manager = new EndgameTransactionManager(port);
        using (var root = Begin(coordinator, Config(true)))
        {
            var report = manager.TryStartEndSequence(root, Config(true));
            Assert(report.Attempted && report.Latched,
                "successful native trigger latches only after the exact panel postcondition");
            Equal(MutationResultKind.Committed, report.Panel.Kind,
                "exact panel transition commits the EndSequence child");
        }
        Assert(manager.EndSequenceLatched,
            "exact panel success remains latched within the same game epoch");
        Assert(EndgameTransactionMechanics.ExactPanelPostcondition(port.CaptureEndPanels()),
            "panel zero is exactly visible and every other panel exactly offscreen");
    }

    private static void TestDefaultEndGateMakesNoMutation()
    {
        var port = SparsePort();
        var original = port.Topology();
        var coordinator = new MutationCoordinator(() => port.Epoch);
        var manager = new EndgameTransactionManager(port);
        using (var root = Begin(coordinator, Config(false)))
        {
            var report = manager.TryStartEndSequence(root, Config(false));
            Assert(!report.Attempted && !report.Latched,
                "AllowEndSequence=false remains a pre-placement hard gate");
        }
        Equal(0, port.PanelCalls, "default gate never invokes the END panel binding");
        Assert(EndgameTransactionMechanics.ExactTopology(original, port.Topology()),
            "default gate leaves inventory identities unchanged");
    }

    private static void TestLiveEndBloodBridgePrecedesOrdinaryRebirth()
    {
        var main = File.ReadAllText("source/Main.cs");
        var create = main.IndexOf("new CharacterEndgameTransactionPort(Character",
            StringComparison.Ordinal);
        var deliver = main.IndexOf("_endgameTransactions.TryDeliverEndBlood(mutationRoot)",
            StringComparison.Ordinal);
        var rebirth = main.IndexOf("Autopilot.ExecuteOrdinaryRebirth(mutationRoot)",
            StringComparison.Ordinal);
        Assert(create >= 0 && deliver > create && rebirth > deliver,
            "live root constructs the exact END port and settles item 494 before ordinary rebirth");
        Assert(main.Contains("Character.bloodMagic.bloodPoints >= MechanicsEndgame.EndBloodCost")
               && main.Contains("END Blood item delivery failed"),
            "live END-Blood bridge opens only when fully funded and propagates failed settlement");
    }

    public static int Main()
    {
        TestEveryUniqueDeliveryUsesPhysicalCapacity();
        TestEndCardFilterSafeExactDebitAndNoSlot();
        TestEndCardDebitWithoutCreditQuarantines();
        TestBloodCommitmentAndLostDeliveryQuarantine();
        TestEverySparseSwapFaultRestoresIdentity();
        TestPanelNoOpRollsBackAndNeverLatches();
        TestPartialPanelQuarantinesButInventoryStillRollsBack();
        TestExactPanelPostconditionLatches();
        TestDefaultEndGateMakesNoMutation();
        TestLiveEndBloodBridgePrecedesOrdinaryRebirth();
        Console.WriteLine("PASS EndgameTransactionTests assertions=" + _assertions);
        return 0;
    }
}
