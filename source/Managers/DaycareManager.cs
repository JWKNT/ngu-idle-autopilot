using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

Purpose: DaycareManager materializes latent native Daycare levels, retrieves completed objects, and
fills open slots with safe high-value candidates without losing physical identity or loot capacity.

Mechanism: It reads `levelsAdded()` from each native Daycare timer, selects an exact ordinary
Equipment reference, and invokes `swapDaycare()` with snapshotted `Inventory.item1/item2` selector
registers. A completed item is always retrieved: it swaps directly with the next candidate, or with
an empty slot proven inside PhysicalTopology's native [totalInvMergeSlots(), curSpaces()) interval.

Inputs and outputs: Inputs are live Inventory, InventoryController/DaycareController state, Item
List rates, exact saved/optimizer/configured loadout references, and perk unlocks. Outputs are one
exact Daycare/ordinary swap and confirmed action/hold telemetry.

Invariants and safety: A successful swap preserves the exact current and candidate references in
opposite locations, materializes a completed non-MacGuffin at level 100, retargets every native
saved-loadout reference, and restores ambient selectors in `finally`. Reserved-prefix and trailing
empties are never retrieval capacity. Puzzle, quest, transform, unlock (including 294/343/391/506),
END, configured, native-referenced, and optimizer-authoritative items never enter Daycare.

Extension points and non-goals: Candidate value can later consume the global terminal-seconds
oracle. General inventory merge/filter/trash policy, source probabilities, and cross-system schedule
authority remain outside this file.
*/
namespace NGUInjector.Managers
{
    internal sealed class DaycareSwapPlan
    {
        internal int DaycareSlot;
        internal int OrdinarySlot;
        internal Equipment DaycareIdentity;
        internal Equipment OrdinaryIdentity;
        internal int LevelsToMaterialize;
        internal bool RetrievalOnly;
        internal int NativeReferencesBefore;
    }

    internal sealed class DaycareSwapState
    {
        internal Equipment DaycareIdentity;
        internal Equipment OrdinaryIdentity;
        internal int DaycareLevel;
        internal int OrdinaryLevel;
        internal int Item1;
        internal int Item2;
        internal double TimerSeconds;
        internal int ReferencesAtDaycare;
        internal int ReferencesAtOrdinary;
    }

    internal static class DaycareManager
    {
        internal static void Manage()
        {
            ExecutionSafety.ReportHold("daycare-root-required",
                "Daycare rotation requires the caller-owned nonzero root transaction.");
        }

        internal static MutationResult Manage(RootTransaction root)
        {
            var c = Main.Character;
            if (root == null || root.IsClosed || c == null || !c.purchases.hasDaycare
                || c.inventoryController == null)
                return null;
            DaycareSwapPlan plan;
            string hold;
            if (!TryPlanOne(c, out plan, out hold))
            {
                if (!string.IsNullOrEmpty(hold))
                    ExecutionSafety.ReportHold("daycare-plan", hold);
                return null;
            }
            return root.ExecuteChild(new DaycareSwapIntent(c, plan));
        }

        private static bool TryPlanOne(Character c, out DaycareSwapPlan selected,
            out string hold)
        {
            selected = null;
            hold = string.Empty;
            var inv = c.inventory;
            var slots = Math.Min(c.inventoryController.daycareSpaces(), inv.daycare.Count);
            slots = Math.Min(slots, inv.daycareTimers.Count);
            if (slots <= 0) return false;

            var occupied = new HashSet<int>(inv.daycare.Where(x => x != null && x.id > 0).Select(x => x.id));
            for (var slot = 0; slot < slots; slot++)
            {
                var current = inv.daycare[slot];
                var effectiveLevel = current == null ? 0 : current.level;
                if (current != null && current.id > 0
                    && c.inventoryController.daycares != null
                    && slot < c.inventoryController.daycares.Count
                    && c.inventoryController.daycares[slot] != null)
                {
                    effectiveLevel += c.inventoryController.daycares[slot].levelsAdded();
                }

                // Ordinary items stay until MAXX. MacGuffins have no cap, so bank at
                // least one native daycare level and then rotate toward the lowest-
                // level available guff (or an unfinished permanent item). This keeps
                // otherwise-idle slots balanced instead of pinning the first guff forever.
                if (current != null && current.id > 0)
                {
                    var isGuff = (int)current.type == 11;
                    var previewAdded = c.inventoryController.daycares != null
                                && slot < c.inventoryController.daycares.Count
                                && c.inventoryController.daycares[slot] != null
                        ? c.inventoryController.daycares[slot].levelsAdded() : 0;
                    if (!isGuff && effectiveLevel < 100 || isGuff && previewAdded < 1)
                        continue;
                }

                if (current != null && current.id > 0)
                    occupied.Remove(current.id);
                var candidate = BestCandidate(c, occupied);
                var retrievalOnly = false;
                if (candidate < 0 && current != null && current.id > 0)
                {
                    var topology = InventoryManager.CaptureOrdinaryTopology(c);
                    if (topology != null)
                    {
                        var capacity = LootCapacity.ProveOrdinary(topology,
                            LootCapacityRequirement.ExactBatch("completed-daycare-retrieval", 1, 0));
                        var usable = capacity.UsableFreeSlotIndices();
                        if (capacity.Admitted && usable.Length > 0)
                        {
                            candidate = usable[0];
                            retrievalOnly = true;
                        }
                    }
                    if (candidate < 0)
                    {
                        hold = "Completed Daycare slot " + (slot + 1)
                               + " is held because no empty native loot-usable ordinary slot exists";
                        continue;
                    }
                }
                if (candidate < 0) continue;
                var added = current != null && current.id > 0
                            && c.inventoryController.daycares != null
                            && slot < c.inventoryController.daycares.Count
                            && c.inventoryController.daycares[slot] != null
                    ? c.inventoryController.daycares[slot].levelsAdded() : 0;
                selected = new DaycareSwapPlan
                {
                    DaycareSlot = slot,
                    OrdinarySlot = candidate,
                    DaycareIdentity = current,
                    OrdinaryIdentity = inv.inventory[candidate],
                    LevelsToMaterialize = added,
                    RetrievalOnly = retrievalOnly,
                    NativeReferencesBefore = CountNativeLoadoutReferences(c, 100000 + slot)
                };
                return true;
            }
            return false;
        }

        internal static int CountNativeLoadoutReferences(Character c, int slot)
        {
            if (c == null || c.inventory == null || c.inventory.loadouts == null) return 0;
            var count = 0;
            foreach (var loadout in c.inventory.loadouts.Where(x => x != null))
            {
                if (loadout.head == slot) count++;
                if (loadout.chest == slot) count++;
                if (loadout.legs == slot) count++;
                if (loadout.boots == slot) count++;
                if (loadout.weapon == slot) count++;
                if (loadout.weapon2 == slot) count++;
                if (loadout.accessories != null)
                    count += loadout.accessories.Count(x => x == slot);
            }
            return count;
        }

        private static int BestCandidate(Character c, ISet<int> occupied)
        {
            var allowMacGuffins = c.adventure.itopod.perkLevel.Count > 56
                                  && c.adventure.itopod.perkLevel[56] >= 1;
            var bestIndex = -1;
            var bestScore = double.MinValue;
            for (var i = 0; i < c.inventory.inventory.Count; i++)
            {
                var item = c.inventory.inventory[i];
                if (item == null || item.id <= 0 || !item.removable || occupied.Contains(item.id))
                    continue;
                // Daycare removes a physical object from the pool that native and
                // optimizer loadouts can equip. Preserve both the exact object in
                // the active authoritative plan and every explicit user loadout.
                if (ProgressionLoadoutOptimizer.IsAuthoritativeItem(item)
                    || InventoryManager.IsNativeLoadoutReference(c, i)
                    || IsConfiguredLoadoutItem(item.id))
                    continue;
                var isMacGuffin = (int)item.type == 11;
                if (isMacGuffin || IsStateMachineItem(c, item.id))
                    continue;
                if (item.level >= 100 || item.id >= c.itemInfo.daycareRate.Length)
                    continue;

                var secondsPerLevel = Math.Max(1.0, c.itemInfo.daycareRate[item.id]);
                var completionSeconds = (100.0 - item.level) * secondsPerLevel;
                var gateWeight = IsHeart(item.id) ? 20.0 : 1.0;
                // Reward per daycare-second is the correct comparison.  Heart items
                // receive their native permanent-progression shadow price, while the
                // deterministic ID epsilon only breaks exact ties.
                var score = gateWeight / completionSeconds + item.id * 1e-15;
                if (score <= bestScore)
                    continue;
                bestScore = score;
                bestIndex = i;
            }
            if (bestIndex >= 0 || !allowMacGuffins)
                return bestIndex;

            // MacGuffins only use otherwise-idle daycare slots.  Balance the lowest
            // level first because all equipped bonuses are multiplicative and exhibit
            // diminishing marginal value.
            long lowestLevel = long.MaxValue;
            for (var i = 0; i < c.inventory.inventory.Count; i++)
            {
                var item = c.inventory.inventory[i];
                if (item == null || item.id <= 0 || !item.removable || occupied.Contains(item.id)
                    || (int)item.type != 11 || item.level >= lowestLevel)
                    continue;
                lowestLevel = item.level;
                bestIndex = i;
            }
            return bestIndex;
        }

        private static bool IsHeart(int id)
        {
            switch (id)
            {
                case 119:
                case 129:
                case 162:
                case 171:
                case 196:
                case 212:
                case 293:
                case 297:
                case 344:
                case 390:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsStateMachineItem(Character c, int id)
        {
            // Quest offerings, Exile clues/puzzle pieces, and one-use mechanic unlock keys must
            // remain in ordinary inventory where their native consumers can find them. Daycare is
            // not a safe storage location for an item whose presence drives a game state machine.
            return id == 75 && c != null && c.adventure != null
                              && !c.adventure.clue2Complete
                   || id >= 278 && id <= 287
                   || id >= 335 && id <= 341
                   || id >= 367 && id <= 372
                   || id == 66 || id == 92 || id == 102 || id == 120
                   || id == 141 || id == 154 || id == 163 || id == 172 || id == 195
                   || id == 294 || id == 343 || id == 391 || id == 506;
        }

        private static bool IsConfiguredLoadoutItem(int id)
        {
            return Main.Settings != null
                   && (Main.Settings.TitanLoadout.Contains(id)
                       || Main.Settings.YggdrasilLoadout.Contains(id)
                       || Main.Settings.GoldDropLoadout.Contains(id)
                       || Main.Settings.MoneyPitLoadout.Contains(id)
                       || Main.Settings.QuickLoadout.Contains(id));
        }

        /*
        ONE EXACT DAYCARE SWAP

        Native swapDaycare materializes the timer into the outgoing physical object, retargets
        saved-loadout slot IDs, swaps two identities, and resets one timer.  The intent captures
        every one of those observable effects plus the ambient selector registers.  It performs
        only one slot per root so an exception after a native partial swap is quarantined rather
        than hidden by a later Daycare action.
        */
        private sealed class DaycareSwapIntent :
            IMutationIntent<DaycareSwapState, bool, DaycareSwapState>
        {
            private readonly Character _character;
            private readonly DaycareSwapPlan _plan;

            internal DaycareSwapIntent(Character character, DaycareSwapPlan plan)
            {
                _character = character;
                _plan = plan;
            }

            public string Id { get { return "daycare.swap." + _plan.DaycareSlot; } }
            public MutationClass Class { get { return MutationClass.Daycare; } }
            public MutationRisk Risk { get { return MutationRisk.FiniteResource; } }
            public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
            public string BindingId { get { return "InventoryController.swapDaycare()/public-exact"; } }
            public bool Required { get { return false; } }
            public bool CanCompensate { get { return false; } }
            public bool CreatesNewEpoch { get { return false; } }
            public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

            public DaycareSwapState CaptureBefore(MutationContext context) { return Capture(); }

            public PreconditionResult CheckPreconditions(MutationContext context,
                DaycareSwapState before)
            {
                if (!Main.IsAutomationReady)
                    return PreconditionResult.Hold("gameplay synchronization is not current");
                if (before == null || !ReferenceEquals(before.DaycareIdentity,
                        _plan.DaycareIdentity)
                    || !ReferenceEquals(before.OrdinaryIdentity, _plan.OrdinaryIdentity))
                    return PreconditionResult.Hold("planned Daycare identities moved before apply");
                return PreconditionResult.Ready();
            }

            public bool Apply(MutationContext context, RootTransactionToken token,
                DaycareSwapState before)
            {
                var inv = _character.inventory;
                var previousItem1 = before.Item1;
                var previousItem2 = before.Item2;
                try
                {
                    inv.item1 = 100000 + _plan.DaycareSlot;
                    inv.item2 = _plan.OrdinarySlot;
                    _character.inventoryController.swapDaycare();
                    return true;
                }
                finally
                {
                    inv.item1 = previousItem1;
                    inv.item2 = previousItem2;
                }
            }

            public VerificationResult<DaycareSwapState> Verify(MutationContext context,
                DaycareSwapState before, MutationApplyObservation<bool> apply)
            {
                var after = Capture();
                var expectedLevel = before.DaycareIdentity == null
                                    || before.DaycareIdentity.id <= 0 ? before.DaycareLevel
                    : (int)before.DaycareIdentity.type == 11
                        ? before.DaycareLevel + _plan.LevelsToMaterialize
                        : Math.Min(100, before.DaycareLevel + _plan.LevelsToMaterialize);
                var valid = apply.ReturnedNormally && apply.Value && after != null
                            && ReferenceEquals(after.DaycareIdentity, before.OrdinaryIdentity)
                            && ReferenceEquals(after.OrdinaryIdentity, before.DaycareIdentity)
                            && (before.DaycareIdentity == null
                                || before.DaycareIdentity.level == expectedLevel)
                            && after.Item1 == before.Item1 && after.Item2 == before.Item2
                            && after.TimerSeconds <= 0.0
                            && (_plan.NativeReferencesBefore == 0
                                || after.ReferencesAtOrdinary >= _plan.NativeReferencesBefore);
                if (!valid)
                    return VerificationResult<DaycareSwapState>.Failed(
                        "Daycare swap lacked exact identity/materialization/timer/reference postconditions");
                var previousId = before.DaycareIdentity == null ? 0 : before.DaycareIdentity.id;
                var candidateId = before.OrdinaryIdentity == null ? 0 : before.OrdinaryIdentity.id;
                Main.LogAction("DAYCARE", _plan.RetrievalOnly
                    ? "Retrieved completed " + GameNames.Item(_character, previousId)
                      + " from Daycare slot " + (_plan.DaycareSlot + 1)
                      + " into proven ordinary slot " + _plan.OrdinarySlot
                      + " [exact identities, level, timer, and loadout retarget confirmed]"
                    : "Daycare slot " + (_plan.DaycareSlot + 1) + ": "
                      + GameNames.Item(_character, previousId) + " -> "
                      + GameNames.Item(_character, candidateId)
                      + " [exact identities, level, timer, and selectors confirmed]");
                return VerificationResult<DaycareSwapState>.Satisfied(after,
                    "one exact Daycare swap confirmed");
            }

            public CompensationResult Compensate(MutationContext context, RecoveryToken token,
                DaycareSwapState before, MutationApplyObservation<bool> apply)
            {
                return CompensationResult.NotSupported(
                    "materialized Daycare time has no exact inverse");
            }

            public bool BeforeStateMatches(DaycareSwapState expected,
                DaycareSwapState observed)
            {
                return expected != null && observed != null
                       && ReferenceEquals(expected.DaycareIdentity, observed.DaycareIdentity)
                       && ReferenceEquals(expected.OrdinaryIdentity, observed.OrdinaryIdentity)
                       && expected.DaycareLevel == observed.DaycareLevel
                       && expected.OrdinaryLevel == observed.OrdinaryLevel
                       && expected.Item1 == observed.Item1 && expected.Item2 == observed.Item2
                       && expected.TimerSeconds == observed.TimerSeconds
                       && expected.ReferencesAtDaycare == observed.ReferencesAtDaycare
                       && expected.ReferencesAtOrdinary == observed.ReferencesAtOrdinary;
            }

            public string FingerprintBefore(DaycareSwapState state) { return Fingerprint(state); }
            public string FingerprintAfter(DaycareSwapState state) { return Fingerprint(state); }

            private DaycareSwapState Capture()
            {
                if (_character == null || _character.inventory == null
                    || _plan.DaycareSlot < 0
                    || _plan.DaycareSlot >= _character.inventory.daycare.Count
                    || _plan.OrdinarySlot < 0
                    || _plan.OrdinarySlot >= _character.inventory.inventory.Count)
                    return null;
                var daycare = _character.inventory.daycare[_plan.DaycareSlot];
                var ordinary = _character.inventory.inventory[_plan.OrdinarySlot];
                return new DaycareSwapState
                {
                    DaycareIdentity = daycare,
                    OrdinaryIdentity = ordinary,
                    DaycareLevel = daycare == null ? -1 : daycare.level,
                    OrdinaryLevel = ordinary == null ? -1 : ordinary.level,
                    Item1 = _character.inventory.item1,
                    Item2 = _character.inventory.item2,
                    TimerSeconds = _character.inventory.daycareTimers[_plan.DaycareSlot].totalseconds,
                    ReferencesAtDaycare = CountNativeLoadoutReferences(_character,
                        100000 + _plan.DaycareSlot),
                    ReferencesAtOrdinary = CountNativeLoadoutReferences(_character,
                        _plan.OrdinarySlot)
                };
            }

            private static string Fingerprint(DaycareSwapState state)
            {
                if (state == null) return "missing";
                return IdentityHash(state.DaycareIdentity) + ":"
                       + IdentityHash(state.OrdinaryIdentity) + ":"
                       + state.DaycareLevel + ":" + state.OrdinaryLevel + ":"
                       + state.Item1 + ":" + state.Item2 + ":" + state.TimerSeconds + ":"
                       + state.ReferencesAtDaycare + ":" + state.ReferencesAtOrdinary;
            }

            private static int IdentityHash(object identity)
            {
                return identity == null ? 0 : RuntimeHelpers.GetHashCode(identity);
            }
        }
    }
}
