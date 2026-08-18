using System;
using System.Collections.Generic;
using System.Linq;

/*
FILE PURPOSE

EndgameDependencyModel overlays live Character ownership on MechanicsEndgame, the canonical pure
registry for NGU Idle's sixteen END pieces, exact final slots, and source branches. Inventory and
Card automation use this live view to fail closed around unique terminal state; telemetry/planning
can consume the same branch snapshot without duplicating magic IDs or inferring completion from a
late-game difficulty alone.

The model never moves items, casts spells, fights zones, or starts Wishes. Ownership is a physical
inventory fact and branch readiness is advisory scheduler data. A missing piece remains protected
work even if its source branch is unlocked. Add future terminal pieces here before permitting any
generic merge/filter/trash policy to handle them.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class EndgameBranchState
    {
        internal int ItemId;
        internal int RequiredInventorySlot;
        internal string Branch;
        internal bool Owned;
    }

    internal static class EndgameDependencyModel
    {
        private static readonly int SadisticBossItemId = MechanicsEndgame.AllRequirements()
            .First(x => x.DependencyKind == EndDependencyKind.SadisticBoss).ItemId;

        internal static bool IsEndItem(int id)
        {
            return MechanicsEndgame.IsProtectedItem(id);
        }

        internal static int RequiredInventorySlot(int id)
        {
            return IsEndItem(id) ? MechanicsEndgame.TargetSlotForItem(id) : -1;
        }

        internal static string BranchForItem(int id)
        {
            return IsEndItem(id) ? MechanicsEndgame.FindByItemId(id).Description : string.Empty;
        }

        internal static int TitanVersionItem(int version)
        {
            var requirement = MechanicsEndgame.AllRequirements().FirstOrDefault(x =>
                x.DependencyKind == EndDependencyKind.Titan12VersionDrop
                && x.TitanVersion == version);
            return requirement == null ? -1 : requirement.ItemId;
        }

        internal static bool IsOwned(Character c, int id)
        {
            if (c == null || c.inventory == null || !IsEndItem(id)) return false;
            var inv = c.inventory;
            if (inv.inventory != null && inv.inventory.Any(x => x != null && x.id == id))
                return true;
            if (inv.daycare != null && inv.daycare.Any(x => x != null && x.id == id))
                return true;
            if (inv.accs != null && inv.accs.Any(x => x != null && x.id == id))
                return true;
            return new[] {inv.head, inv.chest, inv.legs, inv.boots, inv.weapon, inv.weapon2}
                .Any(x => x != null && x.id == id);
        }

        internal static bool IsTerminalCombatCritical(Character c)
        {
            return c != null && c.settings.rebirthDifficulty == difficulty.sadistic
                   && (!IsOwned(c, SadisticBossItemId)
                       || !IsOwned(c, MechanicsEndgame.FinalTriggerItemId));
        }

        internal static IList<EndgameBranchState> Snapshot(Character c)
        {
            var result = new List<EndgameBranchState>();
            foreach (var requirement in MechanicsEndgame.AllRequirements())
            {
                result.Add(new EndgameBranchState
                {
                    ItemId = requirement.ItemId,
                    RequiredInventorySlot = requirement.TargetSlot,
                    Branch = requirement.Description,
                    Owned = IsOwned(c, requirement.ItemId)
                });
            }
            return result;
        }

        internal static IEnumerable<EndgameBranchState> MissingBranches(Character c)
        {
            return Snapshot(c).Where(x => !x.Owned);
        }

        internal static int NextMissingTitan12Version(Character c)
        {
            var missing = MechanicsEndgame.AllRequirements()
                .Where(x => x.DependencyKind == EndDependencyKind.Titan12VersionDrop
                            && !IsOwned(c, x.ItemId))
                .OrderBy(x => x.TitanVersion).FirstOrDefault();
            return missing == null ? -1 : missing.TitanVersion;
        }
    }
}
