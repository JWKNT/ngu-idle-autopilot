using System;
using System.Collections.Generic;
using System.Linq;
#if !ENDGAME_TRANSACTION_TEST_STUBS
using NGUInjector.Managers;
#endif

/*
FILE PURPOSE

Purpose: EndgameTransactionManager is the fail-closed owner of the three irreversible END
materialization boundaries and the reversible final inventory placement. It consumes task 25's
typed END-Card handoff, task 6's physical-topology/capacity proofs, task 8's ordinary-versus-
recoverable ownership model, task 5's pinned native adapters, and task 1's root transaction
protocol. It deliberately grants no scheduler authority and does not change the conservative
AllowEndSequence=false configuration default.

Mechanism: A narrow port separates controller-free proofs/fault injection from the live Character
adapter. END Card and Blood intents snapshot filters and exact resources, temporarily exempt only
their delivery ID in try/finally, invoke one native operation, and commit only on a new level-100
ordinary object. Sparse placement records no optimistic inverse log: compensation recaptures the
live topology and derives identity-based restoration swaps, so even a swap which commits and then
throws is recoverable. The final UI call is a separate EndSequence-class intent. It commits only
when panel zero is exactly (0,0), every other panel is exactly (-5000,-5000), and the inventory
topology did not change. Any failed panel attempt is followed by a separate Inventory-class restore.

Inputs and outputs: Inputs are an active nonzero RootTransaction, an IEndgameTransactionPort, task
25's EndCardHandoffPlan, a post-reset-work Blood observation, and an explicit AutopilotConfig for
the final gate. Outputs are typed MutationResult values, immutable Blood commitment/report records,
and an epoch-keyed in-memory success latch. Callers can publish these report hooks without guessing
success from a native return value.

Invariants and safety: Every unique END delivery uses the same exact one-slot physical-capacity
proof. A Card consumes exactly one selected END and exactly 99 of each of six Mayos. Debit without
new ordinary credit is irreversible and quarantines Cards. Lost Blood without item 494 quarantines
BloodMagic and leaves its cross-rebirth commitment active. Final placement requires exactly one
ordinary identity for every ID 480..495; rollback proves exact identity-at-slot restoration.
Normal-return panel no-ops never latch. Partial panel motion quarantines EndSequence. Filters and
ambient inventory selectors are restored in finally blocks.

Extension points and non-goals: Main calls the fully funded item-494 Blood delivery before reset;
later integration may call the Card handoff/final placement methods, surface Last* reports, and feed
BloodCommitment.BlocksReset into broader reset scheduling. This file does not enable final ending
permission, clear coordinator quarantines, normalize Daycare/equipment copies, synthesize Ctrl
input, inject/restart, or infer physical delivery from itemDropped/itemMaxxed flags.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class EndgameNativeCall
    {
        internal readonly bool InvocationAttempted;
        internal readonly bool ReturnedNormally;
        internal readonly string Reason;
        internal readonly Exception Exception;

        internal EndgameNativeCall(bool attempted, bool returnedNormally, string reason,
            Exception exception = null)
        {
            InvocationAttempted = attempted;
            ReturnedNormally = returnedNormally;
            Reason = reason ?? string.Empty;
            Exception = exception;
        }

        internal static EndgameNativeCall FromNative(NativeInvocationResult invocation)
        {
            return invocation == null
                ? new EndgameNativeCall(false, false, "native adapter returned no result")
                : new EndgameNativeCall(invocation.InvocationAttempted,
                    invocation.ReturnedNormally, invocation.Reason, invocation.Exception);
        }
    }

    internal sealed class EndgamePanelState
    {
        private readonly float[] _x;
        private readonly float[] _y;

        internal EndgamePanelState(float[] x, float[] y)
        {
            if (x == null) throw new ArgumentNullException("x");
            if (y == null) throw new ArgumentNullException("y");
            if (x.Length != y.Length)
                throw new ArgumentException("Panel coordinate arrays must have equal length.");
            _x = (float[])x.Clone();
            _y = (float[])y.Clone();
        }

        internal int Count { get { return _x.Length; } }
        internal float X(int index) { return _x[index]; }
        internal float Y(int index) { return _y[index]; }

        internal bool ExactEquals(EndgamePanelState other)
        {
            if (other == null || Count != other.Count) return false;
            for (var i = 0; i < Count; i++)
                if (_x[i] != other._x[i] || _y[i] != other._y[i]) return false;
            return true;
        }
    }

    internal sealed class EndgameSparseSwap
    {
        internal readonly int FirstSlot;
        internal readonly int SecondSlot;

        internal EndgameSparseSwap(int firstSlot, int secondSlot)
        {
            if (firstSlot < 0) throw new ArgumentOutOfRangeException("firstSlot");
            if (secondSlot < 0) throw new ArgumentOutOfRangeException("secondSlot");
            FirstSlot = firstSlot;
            SecondSlot = secondSlot;
        }
    }

    internal sealed class EndgameSparsePlacementPlan
    {
        internal readonly bool Actionable;
        internal readonly bool AlreadyPlaced;
        internal readonly EndgameSparseSwap[] Swaps;
        internal readonly string Reason;

        internal EndgameSparsePlacementPlan(bool actionable, bool alreadyPlaced,
            EndgameSparseSwap[] swaps, string reason)
        {
            Actionable = actionable;
            AlreadyPlaced = alreadyPlaced;
            Swaps = swaps == null ? new EndgameSparseSwap[0]
                : (EndgameSparseSwap[])swaps.Clone();
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class EndBloodCommitmentPlan
    {
        internal readonly bool Active;
        internal readonly bool OpenedNow;
        internal readonly bool SatisfiedNow;
        internal readonly bool BlocksReset;
        internal readonly bool BlocksChallenge;
        internal readonly bool BlocksOtherBloodSpells;
        internal readonly string EpochFingerprint;
        internal readonly string Reason;

        internal EndBloodCommitmentPlan(bool active, bool openedNow, bool satisfiedNow,
            string epochFingerprint, string reason)
        {
            Active = active;
            OpenedNow = openedNow;
            SatisfiedNow = satisfiedNow;
            BlocksReset = active;
            BlocksChallenge = active;
            BlocksOtherBloodSpells = active;
            EpochFingerprint = epochFingerprint ?? string.Empty;
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class EndSequenceTransactionReport
    {
        internal readonly bool Attempted;
        internal readonly bool Latched;
        internal readonly bool PlacementRestored;
        internal readonly bool TerminalQuarantine;
        internal readonly MutationResult Placement;
        internal readonly MutationResult Panel;
        internal readonly MutationResult Rollback;
        internal readonly string Reason;

        internal EndSequenceTransactionReport(bool attempted, bool latched,
            bool placementRestored, bool terminalQuarantine, MutationResult placement,
            MutationResult panel, MutationResult rollback, string reason)
        {
            Attempted = attempted;
            Latched = latched;
            PlacementRestored = placementRestored;
            TerminalQuarantine = terminalQuarantine;
            Placement = placement;
            Panel = panel;
            Rollback = rollback;
            Reason = reason ?? string.Empty;
        }
    }

    internal interface IEndgameTransactionPort
    {
        string EpochFingerprint { get; }
        bool InventoryStable { get; }
        OrdinaryInventoryTopology CaptureOrdinaryTopology();
        bool HasRecoverableCopy(int itemId);
        int OrdinaryLevel(object identity);

        EndCardFilterSnapshot CaptureDeliveryFilters(int itemId);
        void InstallDeliveryFilterExemption(int itemId);
        void RestoreDeliveryFilters(int itemId, EndCardFilterSnapshot snapshot);

        object[] CaptureEndCards();
        bool CardIsProtected(object cardIdentity);
        int[] CaptureCardCost(object cardIdentity);
        int CardDeckCount { get; }
        int[] CaptureMayoAmounts();
        EndgameNativeCall ToggleCardProtection(object cardIdentity);
        EndgameNativeCall ConsumeCard(object cardIdentity);

        double BloodPoints { get; }
        double EndBloodCost { get; }
        EndgameNativeCall CastEndBlood();

        void SwapOrdinary(int firstSlot, int secondSlot);
        EndgamePanelState CaptureEndPanels();
        EndgameNativeCall TriggerEndPanel();
    }

    internal static class EndgameTransactionMechanics
    {
        internal const int EndCardItemId = 492;
        internal const int EndBloodItemId = 494;
        internal const int DeliveredLevel = 100;
        internal const int PanelOffscreenCoordinate = -5000;
        internal const int MayoCurrencyCount = 6;
        internal const int EndMayoCost = 99;

        internal static LootCapacityProof ProveUniqueDelivery(
            OrdinaryInventoryTopology topology, int itemId)
        {
            if (!MechanicsEndgame.IsProtectedItem(itemId))
                throw new ArgumentOutOfRangeException("itemId");
            return LootCapacity.ProveOrdinary(topology,
                LootCapacityRequirement.ExactUniqueDelivery(
                    "end-unique-delivery-" + itemId, 0, 1, 0));
        }

        internal static bool IsExactEndMayoVector(int[] costs)
        {
            if (costs == null || costs.Length != MayoCurrencyCount) return false;
            for (var i = 0; i < costs.Length; i++)
                if (costs[i] != EndMayoCost) return false;
            return true;
        }

        internal static bool ExactMayoDebit(int[] before, int[] after)
        {
            if (before == null || after == null || before.Length != MayoCurrencyCount
                || after.Length != MayoCurrencyCount) return false;
            for (var i = 0; i < MayoCurrencyCount; i++)
                if (before[i] - after[i] != EndMayoCost) return false;
            return true;
        }

        internal static bool FiltersEqual(EndCardFilterSnapshot left,
            EndCardFilterSnapshot right)
        {
            return left != null && right != null
                   && left.StateKnown == right.StateKnown
                   && left.ItemFiltered == right.ItemFiltered
                   && left.LootFilter == right.LootFilter
                   && left.FilterOn == right.FilterOn
                   && left.FilterMisc == right.FilterMisc;
        }

        internal static object FindNewExactOrdinaryDelivery(OrdinaryInventoryTopology before,
            OrdinaryInventoryTopology after, int itemId, Func<object, int> levelOf)
        {
            if (before == null || after == null || levelOf == null) return null;
            if (before.CountOrdinaryItem(itemId) != 0 || after.CountOrdinaryItem(itemId) != 1)
                return null;
            var slots = after.OrdinarySlotsForItem(itemId);
            if (slots.Length != 1) return null;
            var identity = after.SlotAt(slots[0]).Identity;
            if (identity == null || before.FindOrdinarySlotByIdentity(identity) >= 0) return null;
            return levelOf(identity) == DeliveredLevel ? identity : null;
        }

        internal static bool ExactPanelPostcondition(EndgamePanelState state)
        {
            if (state == null || state.Count == 0) return false;
            for (var i = 0; i < state.Count; i++)
            {
                var expected = i == 0 ? 0f : PanelOffscreenCoordinate;
                if (state.X(i) != expected || state.Y(i) != expected) return false;
            }
            return true;
        }

        internal static EndBloodCommitmentPlan EvaluateBloodCommitment(bool wasActive,
            bool resetWorkFinished, bool hasTerminalPiece, bool hasRecoverableCopy,
            double bloodPoints, string epochFingerprint)
        {
            if (hasTerminalPiece || hasRecoverableCopy)
                return new EndBloodCommitmentPlan(false, false, wasActive,
                    epochFingerprint, "Physical item 494 exists; END Blood commitment is satisfied.");
            if (wasActive)
                return new EndBloodCommitmentPlan(true, false, false,
                    epochFingerprint, "END Blood commitment remains active until physical item 494.");
            var open = resetWorkFinished && bloodPoints > 0.0;
            return new EndBloodCommitmentPlan(open, open, false, epochFingerprint,
                open
                    ? "Post-reset work is complete; retained Blood is committed to item 494."
                    : "Commitment has not opened: finish planned reset work with positive retained Blood.");
        }

        internal static EndgameSparsePlacementPlan PlanSparsePlacement(
            OrdinaryInventoryTopology topology)
        {
            if (topology == null) throw new ArgumentNullException("topology");
            var requirements = MechanicsEndgame.AllRequirements()
                .OrderBy(x => x.TargetSlot).ToArray();
            if (topology.SlotCount <= MechanicsEndgame.FinalTriggerSlot)
                return new EndgameSparsePlacementPlan(false, false, null,
                    "Ordinary inventory has fewer than forty physical slots.");
            for (var i = 0; i < requirements.Length; i++)
                if (topology.CountOrdinaryItem(requirements[i].ItemId) != 1)
                    return new EndgameSparsePlacementPlan(false, false, null,
                        "END item " + requirements[i].ItemId
                        + " does not have exactly one ordinary identity.");

            var ids = new int[topology.SlotCount];
            var identities = new object[topology.SlotCount];
            for (var i = 0; i < topology.SlotCount; i++)
            {
                ids[i] = topology.SlotAt(i).ItemId;
                identities[i] = topology.SlotAt(i).Identity;
            }

            var swaps = new List<EndgameSparseSwap>();
            for (var i = 0; i < requirements.Length; i++)
            {
                var requirement = requirements[i];
                var current = IndexOfId(ids, requirement.ItemId);
                if (current == requirement.TargetSlot) continue;
                Swap(ids, requirement.TargetSlot, current);
                Swap(identities, requirement.TargetSlot, current);
                swaps.Add(new EndgameSparseSwap(requirement.TargetSlot, current));
            }
            return new EndgameSparsePlacementPlan(true, swaps.Count == 0, swaps.ToArray(),
                swaps.Count == 0 ? "All sixteen END identities are already in sparse slots."
                    : "Exact sparse placement requires " + swaps.Count + " identity swaps.");
        }

        internal static EndgameSparsePlacementPlan PlanIdentityRestoration(
            OrdinaryInventoryTopology expected, OrdinaryInventoryTopology current)
        {
            if (expected == null) throw new ArgumentNullException("expected");
            if (current == null) throw new ArgumentNullException("current");
            var identity = PhysicalTopology.ProveOrdinaryIdentity(expected, current);
            if (!identity.OccupiedObjectMultisetPreserved || expected.SlotCount != current.SlotCount)
                return new EndgameSparsePlacementPlan(false, false, null,
                    "Occupied ordinary identity multiset changed; swap-only restoration is impossible.");
            if (identity.ExactSlotIdentityRestored)
                return new EndgameSparsePlacementPlan(true, true, null,
                    "Exact ordinary topology is already restored.");

            var live = new object[current.SlotCount];
            for (var i = 0; i < current.SlotCount; i++) live[i] = current.SlotAt(i).Identity;
            var swaps = new List<EndgameSparseSwap>();
            for (var slot = 0; slot < expected.SlotCount; slot++)
            {
                var desired = expected.SlotAt(slot).Identity;
                if (object.ReferenceEquals(live[slot], desired)) continue;
                if (desired == null) continue;
                var source = IndexOfIdentity(live, desired);
                if (source < 0)
                    return new EndgameSparsePlacementPlan(false, false, null,
                        "Expected ordinary identity disappeared during restoration planning.");
                Swap(live, slot, source);
                swaps.Add(new EndgameSparseSwap(slot, source));
            }
            for (var i = 0; i < expected.SlotCount; i++)
                if (!object.ReferenceEquals(live[i], expected.SlotAt(i).Identity))
                    return new EndgameSparsePlacementPlan(false, false, null,
                        "Identity restoration planner could not reproduce the exact topology.");
            return new EndgameSparsePlacementPlan(true, false, swaps.ToArray(),
                "Identity-derived restoration requires " + swaps.Count + " swaps.");
        }

        internal static bool ExactTopology(OrdinaryInventoryTopology expected,
            OrdinaryInventoryTopology observed)
        {
            return expected != null && observed != null
                   && PhysicalTopology.ProveOrdinaryIdentity(expected, observed)
                       .ExactSlotIdentityRestored;
        }

        internal static bool CanonicalPlacement(OrdinaryInventoryTopology topology)
        {
            if (topology == null) return false;
            var ids = new int[topology.SlotCount];
            for (var i = 0; i < ids.Length; i++) ids[i] = topology.SlotAt(i).ItemId;
            return MechanicsEndgame.ValidatePlacement(ids);
        }

        private static int IndexOfId(int[] ids, int id)
        {
            for (var i = 0; i < ids.Length; i++) if (ids[i] == id) return i;
            return -1;
        }

        private static int IndexOfIdentity(object[] identities, object identity)
        {
            for (var i = 0; i < identities.Length; i++)
                if (object.ReferenceEquals(identities[i], identity)) return i;
            return -1;
        }

        private static void Swap<T>(T[] values, int first, int second)
        {
            var value = values[first];
            values[first] = values[second];
            values[second] = value;
        }
    }

    internal sealed class EndgameTransactionManager
    {
        private readonly IEndgameTransactionPort _port;
        private bool _bloodCommitmentActive;
        private string _bloodCommitmentEpoch = string.Empty;
        private bool _endSequenceLatched;
        private string _endSequenceEpoch = string.Empty;

        internal EndgameTransactionManager(IEndgameTransactionPort port)
        {
            _port = port ?? throw new ArgumentNullException("port");
        }

        internal EndBloodCommitmentPlan LastBloodCommitment { get; private set; }
        internal MutationResult LastEndCardDelivery { get; private set; }
        internal MutationResult LastEndBloodDelivery { get; private set; }
        internal EndSequenceTransactionReport LastEndSequence { get; private set; }

        internal bool BloodCommitmentActive { get { return _bloodCommitmentActive; } }
        internal bool EndSequenceLatched
        {
            get
            {
                RefreshEpochLatches();
                return _endSequenceLatched;
            }
        }

        internal EndBloodCommitmentPlan ObserveBloodCommitment(bool plannedResetWorkFinished)
        {
            RefreshEpochLatches();
            var topology = _port.CaptureOrdinaryTopology();
            var terminal = topology != null
                           && topology.CountOrdinaryItem(EndgameTransactionMechanics.EndBloodItemId) == 1;
            var recoverable = _port.HasRecoverableCopy(
                EndgameTransactionMechanics.EndBloodItemId);
            LastBloodCommitment = EndgameTransactionMechanics.EvaluateBloodCommitment(
                _bloodCommitmentActive, plannedResetWorkFinished, terminal, recoverable,
                _port.BloodPoints, _port.EpochFingerprint);
            _bloodCommitmentActive = LastBloodCommitment.Active;
            _bloodCommitmentEpoch = _bloodCommitmentActive
                ? _port.EpochFingerprint ?? string.Empty : string.Empty;
            return LastBloodCommitment;
        }

        internal MutationResult TryDeliverEndCard(RootTransaction root,
            EndCardHandoffPlan handoff)
        {
            if (root == null) throw new ArgumentNullException("root");
            var result = root.ExecuteChild(new EndCardDeliveryIntent(_port, handoff), _port);
            LastEndCardDelivery = result;
            return result;
        }

        internal MutationResult TryDeliverEndBlood(RootTransaction root)
        {
            if (root == null) throw new ArgumentNullException("root");
            RefreshEpochLatches();
            var result = root.ExecuteChild(
                new EndBloodDeliveryIntent(_port, _bloodCommitmentActive), _port);
            LastEndBloodDelivery = result;
            var topology = _port.CaptureOrdinaryTopology();
            if (topology != null
                && topology.CountOrdinaryItem(EndgameTransactionMechanics.EndBloodItemId) == 1)
            {
                _bloodCommitmentActive = false;
                _bloodCommitmentEpoch = string.Empty;
            }
            return result;
        }

        internal EndSequenceTransactionReport TryStartEndSequence(RootTransaction root,
            AutopilotConfig config)
        {
            if (root == null) throw new ArgumentNullException("root");
            RefreshEpochLatches();
            if (config == null || !config.AllowEndSequence)
                return PublishEndReport(false, false, true, false, null, null, null,
                    "AllowEndSequence is false; no placement or panel mutation was attempted.");
            if (_endSequenceLatched)
                return PublishEndReport(false, true, true, false, null, null, null,
                    "The exact END panel postcondition is already latched for this epoch.");
            if (!_port.InventoryStable)
                return PublishEndReport(false, false, true, false, null, null, null,
                    "Ordinary inventory is not stable for terminal placement.");

            var original = _port.CaptureOrdinaryTopology();
            var placement = root.ExecuteChild(new SparsePlacementIntent(_port), _port);
            if (!Satisfied(placement))
                return PublishEndReport(true, false,
                    EndgameTransactionMechanics.ExactTopology(original,
                        _port.CaptureOrdinaryTopology()),
                    IsTerminalQuarantine(placement, null, null), placement, null, null,
                    "Sparse END placement did not establish the canonical topology.");

            var panel = root.ExecuteChild(new EndPanelIntent(_port), _port);
            if (Satisfied(panel))
            {
                _endSequenceLatched = true;
                _endSequenceEpoch = _port.EpochFingerprint ?? string.Empty;
                return PublishEndReport(true, true, false, false, placement, panel, null,
                    "Exact END panel postcondition and unchanged terminal identities observed.");
            }

            var rollback = root.ExecuteChild(new TopologyRestoreIntent(_port, original), _port);
            var restored = EndgameTransactionMechanics.ExactTopology(original,
                _port.CaptureOrdinaryTopology());
            return PublishEndReport(true, false, restored,
                IsTerminalQuarantine(placement, panel, rollback), placement, panel, rollback,
                restored
                    ? "END panel was not proven; original identity topology was restored."
                    : "END panel was not proven and exact inventory rollback failed.");
        }

        private void RefreshEpochLatches()
        {
            var epoch = _port.EpochFingerprint ?? string.Empty;
            if (_bloodCommitmentActive
                && !string.Equals(epoch, _bloodCommitmentEpoch, StringComparison.Ordinal))
            {
                // The commitment itself crosses rebirth epochs by design. Re-key it to the newly
                // observed save/run epoch; physical item observation is the only success release.
                _bloodCommitmentEpoch = epoch;
            }
            if (_endSequenceLatched
                && !string.Equals(epoch, _endSequenceEpoch, StringComparison.Ordinal))
            {
                _endSequenceLatched = false;
                _endSequenceEpoch = string.Empty;
            }
        }

        private EndSequenceTransactionReport PublishEndReport(bool attempted, bool latched,
            bool restored, bool quarantine, MutationResult placement, MutationResult panel,
            MutationResult rollback, string reason)
        {
            LastEndSequence = new EndSequenceTransactionReport(attempted, latched, restored,
                quarantine, placement, panel, rollback, reason);
            return LastEndSequence;
        }

        private static bool Satisfied(MutationResult result)
        {
            return result != null && (result.Kind == MutationResultKind.Committed
                                      || result.Kind == MutationResultKind.CommittedWithException
                                      || result.Kind == MutationResultKind.NoOpVerified);
        }

        private static bool IsTerminalQuarantine(params MutationResult[] results)
        {
            for (var i = 0; i < results.Length; i++)
                if (results[i] != null
                    && (results[i].Kind == MutationResultKind.Quarantined
                        || results[i].Kind == MutationResultKind.Indeterminate)) return true;
            return false;
        }
    }

    internal sealed class EndCardDeliveryState
    {
        internal readonly OrdinaryInventoryTopology Topology;
        internal readonly EndCardFilterSnapshot Filters;
        internal readonly object[] EndCards;
        internal readonly bool[] EndCardProtection;
        internal readonly int DeckCount;
        internal readonly int[] Mayo;
        internal readonly bool Recoverable;

        internal EndCardDeliveryState(OrdinaryInventoryTopology topology,
            EndCardFilterSnapshot filters, object[] endCards, bool[] endCardProtection,
            int deckCount, int[] mayo, bool recoverable)
        {
            Topology = topology;
            Filters = filters;
            EndCards = endCards == null ? new object[0] : (object[])endCards.Clone();
            EndCardProtection = endCardProtection == null ? new bool[0]
                : (bool[])endCardProtection.Clone();
            DeckCount = deckCount;
            Mayo = mayo == null ? new int[0] : (int[])mayo.Clone();
            Recoverable = recoverable;
        }
    }

    internal sealed class EndCardDeliveryIntent :
        IMutationIntent<EndCardDeliveryState, EndgameNativeCall, EndCardDeliveryState>
    {
        private readonly IEndgameTransactionPort _port;
        private readonly EndCardHandoffPlan _handoff;
        private object _selected;

        internal EndCardDeliveryIntent(IEndgameTransactionPort port, EndCardHandoffPlan handoff)
        {
            _port = port;
            _handoff = handoff;
        }

        public string Id { get { return "end-card-492-delivery"; } }
        public MutationClass Class { get { return MutationClass.Cards; } }
        public MutationRisk Risk { get { return MutationRisk.Irreversible; } }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId { get { return NativeBindingKeys.CardConsume; } }
        public bool Required { get { return true; } }
        public bool CanCompensate { get { return false; } }
        public bool CreatesNewEpoch { get { return false; } }
        public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

        public EndCardDeliveryState CaptureBefore(MutationContext context)
        {
            return Capture();
        }

        public PreconditionResult CheckPreconditions(MutationContext context,
            EndCardDeliveryState before)
        {
            if (_handoff == null || !_handoff.ReadyForTerminalTransaction
                || _handoff.StopDuplicateConsume)
                return PreconditionResult.Hold(_handoff == null
                    ? "Task-25 END Card handoff is unavailable." : _handoff.Reason);
            if (before.Topology == null || before.Filters == null || !before.Filters.StateKnown)
                return PreconditionResult.Hold("Exact topology/filter state is unavailable.");
            if (before.Topology.CountOrdinaryItem(EndgameTransactionMechanics.EndCardItemId) != 0
                || before.Recoverable)
                return PreconditionResult.AlreadySatisfied(
                    "A physical/recoverable item 492 exists; duplicate consume is forbidden.");
            if (!EndgameTransactionMechanics.ProveUniqueDelivery(before.Topology,
                    EndgameTransactionMechanics.EndCardItemId).Admitted)
                return PreconditionResult.Hold("No exact ordinary slot exists for item 492.");
            if (before.EndCards.Length == 0)
                return PreconditionResult.Hold("No held END Card identity exists.");
            if (before.EndCardProtection.Length != before.EndCards.Length
                || before.EndCardProtection.Any(x => !x))
                return PreconditionResult.Hold(
                    "Every held END Card must be protected outside the transaction.");
            _selected = before.EndCards[0];
            if (!_port.CardIsProtected(_selected))
                return PreconditionResult.Hold("Selected END Card is not protected at handoff.");
            if (!EndgameTransactionMechanics.IsExactEndMayoVector(
                    _port.CaptureCardCost(_selected)))
                return PreconditionResult.Hold("Selected END Card cost is not exactly 99x6.");
            if (before.Mayo.Length != EndgameTransactionMechanics.MayoCurrencyCount
                || before.Mayo.Any(x => x < EndgameTransactionMechanics.EndMayoCost))
                return PreconditionResult.Hold("Exact six-currency END Mayo reserve is not funded.");
            return PreconditionResult.Ready();
        }

        public EndgameNativeCall Apply(MutationContext context, RootTransactionToken token,
            EndCardDeliveryState before)
        {
            EndgameNativeCall unprotect = null;
            try
            {
                _port.InstallDeliveryFilterExemption(
                    EndgameTransactionMechanics.EndCardItemId);
                unprotect = _port.ToggleCardProtection(_selected);
                if (unprotect != null && unprotect.Exception != null)
                    throw unprotect.Exception;
                if (unprotect == null || !unprotect.ReturnedNormally
                    || _port.CardIsProtected(_selected))
                    return unprotect ?? new EndgameNativeCall(false, false,
                        "Selected END Card could not be unprotected.");
                var consume = _port.ConsumeCard(_selected);
                if (consume != null && consume.Exception != null) throw consume.Exception;
                return consume;
            }
            finally
            {
                try
                {
                    var held = _port.CaptureEndCards();
                    if (ContainsIdentity(held, _selected) && !_port.CardIsProtected(_selected))
                    {
                        var restore = _port.ToggleCardProtection(_selected);
                        if (restore != null && restore.Exception != null)
                            throw restore.Exception;
                        if (restore == null || !restore.ReturnedNormally
                            || !_port.CardIsProtected(_selected))
                            throw new InvalidOperationException(
                                "Selected END Card protection could not be restored.");
                    }
                }
                finally
                {
                    _port.RestoreDeliveryFilters(
                        EndgameTransactionMechanics.EndCardItemId, before.Filters);
                }
            }
        }

        public VerificationResult<EndCardDeliveryState> Verify(MutationContext context,
            EndCardDeliveryState before, MutationApplyObservation<EndgameNativeCall> apply)
        {
            var after = Capture();
            var delivered = EndgameTransactionMechanics.FindNewExactOrdinaryDelivery(
                before.Topology, after.Topology, EndgameTransactionMechanics.EndCardItemId,
                _port.OrdinaryLevel);
            var removed = !ContainsIdentity(after.EndCards, _selected);
            var exact = removed && after.DeckCount == before.DeckCount - 1
                        && EndgameTransactionMechanics.ExactMayoDebit(before.Mayo, after.Mayo)
                        && delivered != null
                        && after.EndCardProtection.All(x => x)
                        && EndgameTransactionMechanics.FiltersEqual(before.Filters, after.Filters);
            return exact
                ? VerificationResult<EndCardDeliveryState>.Satisfied(after,
                    "Exact END Card identity, 99x6 Mayo, new level-100 item 492, and filters verified.")
                : VerificationResult<EndCardDeliveryState>.Failed(
                    "END Card debit/removal/new ordinary delivery/filter restoration was not exact.");
        }

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            EndCardDeliveryState before, MutationApplyObservation<EndgameNativeCall> apply)
        {
            return CompensationResult.NotSupported("END Card debit/deletion is irreversible.");
        }

        public bool BeforeStateMatches(EndCardDeliveryState expected,
            EndCardDeliveryState observed)
        {
            return expected != null && observed != null
                   && expected.DeckCount == observed.DeckCount
                   && expected.Recoverable == observed.Recoverable
                   && EqualInts(expected.Mayo, observed.Mayo)
                   && EqualBools(expected.EndCardProtection, observed.EndCardProtection)
                   && SameIdentitySequence(expected.EndCards, observed.EndCards)
                   && EndgameTransactionMechanics.FiltersEqual(expected.Filters, observed.Filters)
                   && EndgameTransactionMechanics.ExactTopology(expected.Topology,
                       observed.Topology);
        }

        public string FingerprintBefore(EndCardDeliveryState before) { return Fingerprint(before); }
        public string FingerprintAfter(EndCardDeliveryState after) { return Fingerprint(after); }

        private EndCardDeliveryState Capture()
        {
            var cards = _port.CaptureEndCards();
            var protection = cards.Select(_port.CardIsProtected).ToArray();
            return new EndCardDeliveryState(_port.CaptureOrdinaryTopology(),
                _port.CaptureDeliveryFilters(EndgameTransactionMechanics.EndCardItemId),
                cards, protection, _port.CardDeckCount, _port.CaptureMayoAmounts(),
                _port.HasRecoverableCopy(EndgameTransactionMechanics.EndCardItemId));
        }

        private static string Fingerprint(EndCardDeliveryState state)
        {
            return state == null ? "null" : "deck=" + state.DeckCount + ";ends="
                + state.EndCards.Length + ";mayo=" + string.Join(",",
                    state.Mayo.Select(x => x.ToString()).ToArray()) + ";ordinary492="
                + (state.Topology == null ? -1
                    : state.Topology.CountOrdinaryItem(
                        EndgameTransactionMechanics.EndCardItemId));
        }

        private static bool ContainsIdentity(object[] values, object expected)
        {
            if (values == null) return false;
            for (var i = 0; i < values.Length; i++)
                if (object.ReferenceEquals(values[i], expected)) return true;
            return false;
        }

        private static bool SameIdentitySequence(object[] left, object[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++)
                if (!object.ReferenceEquals(left[i], right[i])) return false;
            return true;
        }

        private static bool EqualInts(int[] left, int[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static bool EqualBools(bool[] left, bool[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }
    }

    internal sealed class EndBloodDeliveryState
    {
        internal readonly OrdinaryInventoryTopology Topology;
        internal readonly EndCardFilterSnapshot Filters;
        internal readonly double Blood;
        internal readonly bool Recoverable;
        internal readonly bool InventoryStable;

        internal EndBloodDeliveryState(OrdinaryInventoryTopology topology,
            EndCardFilterSnapshot filters, double blood, bool recoverable, bool inventoryStable)
        {
            Topology = topology;
            Filters = filters;
            Blood = blood;
            Recoverable = recoverable;
            InventoryStable = inventoryStable;
        }
    }

    internal sealed class EndBloodDeliveryIntent :
        IMutationIntent<EndBloodDeliveryState, EndgameNativeCall, EndBloodDeliveryState>
    {
        private readonly IEndgameTransactionPort _port;
        private readonly bool _commitmentActive;

        internal EndBloodDeliveryIntent(IEndgameTransactionPort port, bool commitmentActive)
        {
            _port = port;
            _commitmentActive = commitmentActive;
        }

        public string Id { get { return "end-blood-494-delivery"; } }
        public MutationClass Class { get { return MutationClass.BloodMagic; } }
        public MutationRisk Risk { get { return MutationRisk.Irreversible; } }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId { get { return "blood.cast-end/public-exact"; } }
        public bool Required { get { return true; } }
        public bool CanCompensate { get { return false; } }
        public bool CreatesNewEpoch { get { return false; } }
        public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

        public EndBloodDeliveryState CaptureBefore(MutationContext context) { return Capture(); }

        public PreconditionResult CheckPreconditions(MutationContext context,
            EndBloodDeliveryState before)
        {
            if (!_commitmentActive)
                return PreconditionResult.Hold("Cross-rebirth END Blood commitment is not active.");
            if (before.Topology == null || before.Filters == null || !before.Filters.StateKnown)
                return PreconditionResult.Hold("Exact topology/filter state is unavailable.");
            if (before.Topology.CountOrdinaryItem(EndgameTransactionMechanics.EndBloodItemId) == 1
                || before.Recoverable)
                return PreconditionResult.AlreadySatisfied(
                    "A physical/recoverable item 494 exists; duplicate cast is forbidden.");
            if (!before.InventoryStable)
                return PreconditionResult.Hold("Inventory is not stable at the END Blood boundary.");
            if (!EndgameTransactionMechanics.ProveUniqueDelivery(before.Topology,
                    EndgameTransactionMechanics.EndBloodItemId).Admitted)
                return PreconditionResult.Hold("No exact ordinary slot exists for item 494.");
            if (_port.EndBloodCost != MechanicsEndgame.EndBloodCost)
                return PreconditionResult.Hold("Live END Blood cost differs from audited 5e22.");
            if (before.Blood < MechanicsEndgame.EndBloodCost)
                return PreconditionResult.Hold("END Blood commitment has not reached 5e22.");
            return PreconditionResult.Ready();
        }

        public EndgameNativeCall Apply(MutationContext context, RootTransactionToken token,
            EndBloodDeliveryState before)
        {
            try
            {
                _port.InstallDeliveryFilterExemption(
                    EndgameTransactionMechanics.EndBloodItemId);
                var cast = _port.CastEndBlood();
                if (cast != null && cast.Exception != null) throw cast.Exception;
                return cast;
            }
            finally
            {
                _port.RestoreDeliveryFilters(
                    EndgameTransactionMechanics.EndBloodItemId, before.Filters);
            }
        }

        public VerificationResult<EndBloodDeliveryState> Verify(MutationContext context,
            EndBloodDeliveryState before, MutationApplyObservation<EndgameNativeCall> apply)
        {
            var after = Capture();
            var delivered = EndgameTransactionMechanics.FindNewExactOrdinaryDelivery(
                before.Topology, after.Topology, EndgameTransactionMechanics.EndBloodItemId,
                _port.OrdinaryLevel);
            var exact = before.Blood >= MechanicsEndgame.EndBloodCost && after.Blood == 0.0
                        && delivered != null
                        && EndgameTransactionMechanics.FiltersEqual(before.Filters, after.Filters);
            return exact
                ? VerificationResult<EndBloodDeliveryState>.Satisfied(after,
                    "Entire END Blood pool reached zero and new level-100 ordinary item 494 exists.")
                : VerificationResult<EndBloodDeliveryState>.Failed(
                    "END Blood debit/new ordinary delivery/filter restoration was not exact.");
        }

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            EndBloodDeliveryState before, MutationApplyObservation<EndgameNativeCall> apply)
        {
            return CompensationResult.NotSupported("Native END Blood cast drains the whole pool.");
        }

        public bool BeforeStateMatches(EndBloodDeliveryState expected,
            EndBloodDeliveryState observed)
        {
            return expected != null && observed != null && expected.Blood == observed.Blood
                   && expected.Recoverable == observed.Recoverable
                   && expected.InventoryStable == observed.InventoryStable
                   && EndgameTransactionMechanics.FiltersEqual(expected.Filters, observed.Filters)
                   && EndgameTransactionMechanics.ExactTopology(expected.Topology,
                       observed.Topology);
        }

        public string FingerprintBefore(EndBloodDeliveryState before) { return Fingerprint(before); }
        public string FingerprintAfter(EndBloodDeliveryState after) { return Fingerprint(after); }

        private EndBloodDeliveryState Capture()
        {
            return new EndBloodDeliveryState(_port.CaptureOrdinaryTopology(),
                _port.CaptureDeliveryFilters(EndgameTransactionMechanics.EndBloodItemId),
                _port.BloodPoints,
                _port.HasRecoverableCopy(EndgameTransactionMechanics.EndBloodItemId),
                _port.InventoryStable);
        }

        private static string Fingerprint(EndBloodDeliveryState state)
        {
            return state == null ? "null" : "blood=" + state.Blood.ToString("R")
                + ";ordinary494=" + (state.Topology == null ? -1
                    : state.Topology.CountOrdinaryItem(
                        EndgameTransactionMechanics.EndBloodItemId));
        }
    }

    internal abstract class TopologyMutationIntentBase :
        IMutationIntent<OrdinaryInventoryTopology, bool, OrdinaryInventoryTopology>
    {
        protected readonly IEndgameTransactionPort Port;

        protected TopologyMutationIntentBase(IEndgameTransactionPort port) { Port = port; }

        public abstract string Id { get; }
        public MutationClass Class { get { return MutationClass.Inventory; } }
        public MutationRisk Risk { get { return MutationRisk.Reversible; } }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId { get { return "inventory.swap-items/public-exact"; } }
        public bool Required { get { return true; } }
        public bool CanCompensate { get { return true; } }
        public bool CreatesNewEpoch { get { return false; } }
        public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

        public OrdinaryInventoryTopology CaptureBefore(MutationContext context)
        {
            return Port.CaptureOrdinaryTopology();
        }

        public abstract PreconditionResult CheckPreconditions(MutationContext context,
            OrdinaryInventoryTopology before);
        public abstract bool Apply(MutationContext context, RootTransactionToken token,
            OrdinaryInventoryTopology before);
        public abstract VerificationResult<OrdinaryInventoryTopology> Verify(
            MutationContext context, OrdinaryInventoryTopology before,
            MutationApplyObservation<bool> apply);

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            OrdinaryInventoryTopology before, MutationApplyObservation<bool> apply)
        {
            var current = Port.CaptureOrdinaryTopology();
            var restore = EndgameTransactionMechanics.PlanIdentityRestoration(before, current);
            if (!restore.Actionable)
                return CompensationResult.Failed(restore.Reason);
            try
            {
                ApplySwaps(restore.Swaps);
            }
            catch (Exception ex)
            {
                return CompensationResult.Failed("Identity rollback threw: " + ex.Message);
            }
            return EndgameTransactionMechanics.ExactTopology(before,
                    Port.CaptureOrdinaryTopology())
                ? CompensationResult.Restored("Exact identity-at-slot topology restored.")
                : CompensationResult.Failed("Rollback returned without exact identity restoration.");
        }

        public bool BeforeStateMatches(OrdinaryInventoryTopology expected,
            OrdinaryInventoryTopology observed)
        {
            return EndgameTransactionMechanics.ExactTopology(expected, observed);
        }

        public string FingerprintBefore(OrdinaryInventoryTopology before)
        {
            return Fingerprint(before);
        }

        public string FingerprintAfter(OrdinaryInventoryTopology after)
        {
            return Fingerprint(after);
        }

        protected void ApplySwaps(EndgameSparseSwap[] swaps)
        {
            for (var i = 0; i < swaps.Length; i++)
                Port.SwapOrdinary(swaps[i].FirstSlot, swaps[i].SecondSlot);
        }

        private static string Fingerprint(OrdinaryInventoryTopology topology)
        {
            if (topology == null) return "null";
            var ids = new int[topology.SlotCount];
            for (var i = 0; i < ids.Length; i++) ids[i] = topology.SlotAt(i).ItemId;
            return string.Join(",", ids.Select(x => x.ToString()).ToArray());
        }
    }

    internal sealed class SparsePlacementIntent : TopologyMutationIntentBase
    {
        internal SparsePlacementIntent(IEndgameTransactionPort port) : base(port) { }
        public override string Id { get { return "end-sparse-placement"; } }

        public override PreconditionResult CheckPreconditions(MutationContext context,
            OrdinaryInventoryTopology before)
        {
            if (!Port.InventoryStable)
                return PreconditionResult.Hold("Inventory is not stable for sparse placement.");
            var plan = EndgameTransactionMechanics.PlanSparsePlacement(before);
            if (!plan.Actionable) return PreconditionResult.Hold(plan.Reason);
            return plan.AlreadyPlaced
                ? PreconditionResult.AlreadySatisfied(plan.Reason)
                : PreconditionResult.Ready();
        }

        public override bool Apply(MutationContext context, RootTransactionToken token,
            OrdinaryInventoryTopology before)
        {
            var plan = EndgameTransactionMechanics.PlanSparsePlacement(before);
            ApplySwaps(plan.Swaps);
            return true;
        }

        public override VerificationResult<OrdinaryInventoryTopology> Verify(
            MutationContext context, OrdinaryInventoryTopology before,
            MutationApplyObservation<bool> apply)
        {
            var after = Port.CaptureOrdinaryTopology();
            var identity = PhysicalTopology.ProveOrdinaryIdentity(before, after);
            return apply.ReturnedNormally
                   && EndgameTransactionMechanics.CanonicalPlacement(after)
                   && identity.OccupiedObjectMultisetPreserved
                ? VerificationResult<OrdinaryInventoryTopology>.Satisfied(after,
                    "All sixteen exact identities occupy the canonical sparse slots.")
                : VerificationResult<OrdinaryInventoryTopology>.Failed(
                    "Canonical sparse placement or occupied identity multiset was not preserved.");
        }
    }

    internal sealed class TopologyRestoreIntent : TopologyMutationIntentBase
    {
        private readonly OrdinaryInventoryTopology _target;

        internal TopologyRestoreIntent(IEndgameTransactionPort port,
            OrdinaryInventoryTopology target) : base(port)
        {
            _target = target;
        }

        public override string Id { get { return "end-placement-restore"; } }

        public override PreconditionResult CheckPreconditions(MutationContext context,
            OrdinaryInventoryTopology before)
        {
            var plan = EndgameTransactionMechanics.PlanIdentityRestoration(_target, before);
            if (!plan.Actionable) return PreconditionResult.Hold(plan.Reason);
            return plan.AlreadyPlaced
                ? PreconditionResult.AlreadySatisfied(plan.Reason)
                : PreconditionResult.Ready();
        }

        public override bool Apply(MutationContext context, RootTransactionToken token,
            OrdinaryInventoryTopology before)
        {
            var plan = EndgameTransactionMechanics.PlanIdentityRestoration(_target, before);
            ApplySwaps(plan.Swaps);
            return true;
        }

        public override VerificationResult<OrdinaryInventoryTopology> Verify(
            MutationContext context, OrdinaryInventoryTopology before,
            MutationApplyObservation<bool> apply)
        {
            var after = Port.CaptureOrdinaryTopology();
            return EndgameTransactionMechanics.ExactTopology(_target, after)
                ? VerificationResult<OrdinaryInventoryTopology>.Satisfied(after,
                    "Original exact identity-at-slot topology restored.")
                : VerificationResult<OrdinaryInventoryTopology>.Failed(
                    "Original exact identity-at-slot topology was not restored.");
        }
    }

    internal sealed class EndPanelMutationState
    {
        internal readonly OrdinaryInventoryTopology Topology;
        internal readonly EndgamePanelState Panels;

        internal EndPanelMutationState(OrdinaryInventoryTopology topology,
            EndgamePanelState panels)
        {
            Topology = topology;
            Panels = panels;
        }
    }

    internal sealed class EndPanelIntent :
        IMutationIntent<EndPanelMutationState, EndgameNativeCall, EndPanelMutationState>
    {
        private readonly IEndgameTransactionPort _port;

        internal EndPanelIntent(IEndgameTransactionPort port) { _port = port; }

        public string Id { get { return "end-panel-trigger"; } }
        public MutationClass Class { get { return MutationClass.EndSequence; } }
        public MutationRisk Risk { get { return MutationRisk.Irreversible; } }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId { get { return NativeBindingKeys.ItemConsume; } }
        public bool Required { get { return true; } }
        public bool CanCompensate { get { return false; } }
        public bool CreatesNewEpoch { get { return false; } }
        public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

        public EndPanelMutationState CaptureBefore(MutationContext context)
        {
            return new EndPanelMutationState(_port.CaptureOrdinaryTopology(),
                _port.CaptureEndPanels());
        }

        public PreconditionResult CheckPreconditions(MutationContext context,
            EndPanelMutationState before)
        {
            if (!_port.InventoryStable)
                return PreconditionResult.Hold("Inventory became unstable before END panel trigger.");
            if (!EndgameTransactionMechanics.CanonicalPlacement(before.Topology))
                return PreconditionResult.Hold("Exact sparse END placement is absent.");
            return EndgameTransactionMechanics.ExactPanelPostcondition(before.Panels)
                ? PreconditionResult.AlreadySatisfied("Exact END panel state already exists.")
                : PreconditionResult.Ready();
        }

        public EndgameNativeCall Apply(MutationContext context, RootTransactionToken token,
            EndPanelMutationState before)
        {
            var trigger = _port.TriggerEndPanel();
            if (trigger != null && trigger.Exception != null) throw trigger.Exception;
            return trigger;
        }

        public VerificationResult<EndPanelMutationState> Verify(MutationContext context,
            EndPanelMutationState before, MutationApplyObservation<EndgameNativeCall> apply)
        {
            var after = new EndPanelMutationState(_port.CaptureOrdinaryTopology(),
                _port.CaptureEndPanels());
            return apply.ReturnedNormally && apply.Value != null
                   && apply.Value.ReturnedNormally
                   && EndgameTransactionMechanics.ExactPanelPostcondition(after.Panels)
                   && EndgameTransactionMechanics.ExactTopology(before.Topology, after.Topology)
                ? VerificationResult<EndPanelMutationState>.Satisfied(after,
                    "Panel zero is visible, every other END panel is offscreen, and identities are unchanged.")
                : VerificationResult<EndPanelMutationState>.Failed(
                    "Exact END panel postcondition or unchanged inventory topology was false.");
        }

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            EndPanelMutationState before, MutationApplyObservation<EndgameNativeCall> apply)
        {
            return CompensationResult.NotSupported("END panel motion has no audited inverse.");
        }

        public bool BeforeStateMatches(EndPanelMutationState expected,
            EndPanelMutationState observed)
        {
            return expected != null && observed != null
                   && EndgameTransactionMechanics.ExactTopology(expected.Topology,
                       observed.Topology)
                   && expected.Panels != null && expected.Panels.ExactEquals(observed.Panels);
        }

        public string FingerprintBefore(EndPanelMutationState before)
        {
            return PanelFingerprint(before);
        }

        public string FingerprintAfter(EndPanelMutationState after)
        {
            return PanelFingerprint(after);
        }

        private static string PanelFingerprint(EndPanelMutationState state)
        {
            if (state == null || state.Panels == null) return "null";
            var values = new string[state.Panels.Count];
            for (var i = 0; i < values.Length; i++)
                values[i] = state.Panels.X(i).ToString("R") + ":"
                            + state.Panels.Y(i).ToString("R");
            return string.Join(",", values);
        }
    }

#if !ENDGAME_TRANSACTION_TEST_STUBS
    /*
    LIVE ADAPTER

    This adapter contains only installed-build field reads and narrow native calls. Main constructs
    it per game epoch for the fully funded item-494 Blood delivery; final END placement/panel
    execution remains separately authority-disabled. The port restores every changed filter and
    selector, while the intents above own permission, preconditions, postconditions, recovery, and
    quarantine.
    */
    internal sealed class CharacterEndgameTransactionPort : IEndgameTransactionPort
    {
        private readonly Character _character;
        private readonly NativeMutationAdapters _native;
        private readonly Func<string> _epoch;

        internal CharacterEndgameTransactionPort(Character character,
            NativeMutationAdapters native, Func<string> epochFingerprint)
        {
            _character = character ?? throw new ArgumentNullException("character");
            _native = native ?? throw new ArgumentNullException("native");
            _epoch = epochFingerprint;
        }

        internal CharacterEndgameTransactionPort(Character character,
            Func<string> epochFingerprint = null)
            : this(character, NativeBindingRegistry.Create(typeof(Card).Assembly,
                Main.GameAssemblySha256).CreateMutationAdapters(), epochFingerprint
                    ?? (() => Main.CurrentGameEpochFingerprint)) { }

        public string EpochFingerprint
        {
            get { return _epoch == null ? string.Empty : _epoch() ?? string.Empty; }
        }

        public bool InventoryStable
        {
            get
            {
                return _character.inventory != null
                       && _character.inventory.inventory != null
                       && _character.inventory.inventory.Count > MechanicsEndgame.FinalTriggerSlot
                       && _character.inventoryController != null
                       && !_character.inventoryController.midDrag
                       && LoadoutManager.CurrentLock == LockType.None;
            }
        }

        public OrdinaryInventoryTopology CaptureOrdinaryTopology()
        {
            return InventoryManager.CaptureOrdinaryTopology(_character);
        }

        public bool HasRecoverableCopy(int itemId)
        {
            return EndgameDependencyModel.HasRecoverableCopy(_character, itemId);
        }

        public int OrdinaryLevel(object identity)
        {
            var item = identity as Equipment;
            return item == null ? -1 : item.level;
        }

        public EndCardFilterSnapshot CaptureDeliveryFilters(int itemId)
        {
            try
            {
                return new EndCardFilterSnapshot(true,
                    _character.inventory.itemList.itemFiltered[itemId],
                    _character.arbitrary.lootFilter, _character.settings.filterOn,
                    _character.settings.filterMisc);
            }
            catch
            {
                return new EndCardFilterSnapshot(false, false, false, false, false);
            }
        }

        public void InstallDeliveryFilterExemption(int itemId)
        {
            _character.inventory.itemList.itemFiltered[itemId] = false;
            _character.settings.filterMisc = false;
        }

        public void RestoreDeliveryFilters(int itemId, EndCardFilterSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.StateKnown) return;
            _character.inventory.itemList.itemFiltered[itemId] = snapshot.ItemFiltered;
            _character.arbitrary.lootFilter = snapshot.LootFilter;
            _character.settings.filterOn = snapshot.FilterOn;
            _character.settings.filterMisc = snapshot.FilterMisc;
        }

        public object[] CaptureEndCards()
        {
            return _character.cards.cards.Where(x => x != null && x.type == cardType.end)
                .Cast<object>().ToArray();
        }

        public bool CardIsProtected(object cardIdentity)
        {
            var card = cardIdentity as Card;
            return card != null && card.isProtected;
        }

        public int[] CaptureCardCost(object cardIdentity)
        {
            var result = new int[EndgameTransactionMechanics.MayoCurrencyCount];
            var card = cardIdentity as Card;
            if (card == null || card.manaCosts == null) return result;
            for (var i = 0; i < result.Length && i < card.manaCosts.Count; i++)
                result[i] = card.manaCosts[i];
            return result;
        }

        public int CardDeckCount { get { return _character.cards.cards.Count; } }

        public int[] CaptureMayoAmounts()
        {
            var result = new int[EndgameTransactionMechanics.MayoCurrencyCount];
            for (var i = 0; i < result.Length && i < _character.cards.manas.Count; i++)
                result[i] = _character.cards.manas[i].amount;
            return result;
        }

        public EndgameNativeCall ToggleCardProtection(object cardIdentity)
        {
            var index = FindEndCard(cardIdentity);
            if (index < 0)
                return new EndgameNativeCall(false, false,
                    "Exact END Card identity is absent from the deck.");
            try
            {
                _character.cardsController.protectCard(index);
                return new EndgameNativeCall(true, true, string.Empty);
            }
            catch (Exception ex)
            {
                return new EndgameNativeCall(true, false, ex.Message, ex);
            }
        }

        public EndgameNativeCall ConsumeCard(object cardIdentity)
        {
            var index = FindEndCard(cardIdentity);
            return index < 0
                ? new EndgameNativeCall(false, false,
                    "Exact END Card identity is absent immediately before invoke.")
                : EndgameNativeCall.FromNative(
                    _native.ConsumeCard(_character.cardsController, index));
        }

        public double BloodPoints { get { return _character.bloodMagic.bloodPoints; } }
        public double EndBloodCost { get { return _character.bloodSpells.endSpellBlood(); } }

        public EndgameNativeCall CastEndBlood()
        {
            try
            {
                _character.bloodSpells.castEndSpell();
                return new EndgameNativeCall(true, true, string.Empty);
            }
            catch (Exception ex)
            {
                return new EndgameNativeCall(true, false, ex.Message, ex);
            }
        }

        public void SwapOrdinary(int firstSlot, int secondSlot)
        {
            var inventory = _character.inventory;
            var oldFirst = inventory.item1;
            var oldSecond = inventory.item2;
            try
            {
                inventory.item1 = firstSlot;
                inventory.item2 = secondSlot;
                _character.inventoryController.swapItems();
            }
            finally
            {
                inventory.item1 = oldFirst;
                inventory.item2 = oldSecond;
            }
        }

        public EndgamePanelState CaptureEndPanels()
        {
            if (_character.endPanels == null) return null;
            var x = new float[_character.endPanels.Count];
            var y = new float[_character.endPanels.Count];
            for (var i = 0; i < x.Length; i++)
            {
                if (_character.endPanels[i] == null) return null;
                x[i] = _character.endPanels[i].transform.localPosition.x;
                y[i] = _character.endPanels[i].transform.localPosition.y;
            }
            return new EndgamePanelState(x, y);
        }

        public EndgameNativeCall TriggerEndPanel()
        {
            var controller = _character.inventoryController;
            if (controller.inventory == null
                || controller.inventory.Length <= MechanicsEndgame.FinalTriggerSlot)
                return new EndgameNativeCall(false, false,
                    "Final item controller slot 39 is unavailable.");
            var oldPage = controller.inventory.Length == 0 ? 0
                : (int)Math.Floor((double)controller.inventory[0].id / 60.0);
            try
            {
                controller.changePage(0);
                return EndgameNativeCall.FromNative(
                    _native.ConsumeItem(controller.inventory[
                        MechanicsEndgame.FinalTriggerSlot]));
            }
            finally
            {
                controller.changePage(oldPage);
            }
        }

        private int FindEndCard(object identity)
        {
            for (var i = 0; i < _character.cards.cards.Count; i++)
                if (object.ReferenceEquals(_character.cards.cards[i], identity)) return i;
            return -1;
        }
    }
#endif
}
