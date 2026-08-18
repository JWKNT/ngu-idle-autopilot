using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

BeardManager activates and levels unlocked Beards while respecting slot count, banked progress,
and rebirth-cycle intent. It mutates native Beard controllers from synchronized state. Broader
Magic allocation and rebirth timing remain planner responsibilities.
*/
namespace NGUInjector.Managers
{
    internal static class BeardManager
    {
        // Utility is progression-gate value, not the displayed percentage.  Adventure,
        // drop and NUMBER dominate early; NGU and gold rise once those systems exist.
        private static double Utility(Character c, int id)
        {
            switch (id)
            {
                case 5: return 12.0;
                case 1: return 9.0;
                case 2: return 8.0;
                case 3: return c.settings.nguOn ? 9.0 : 2.0;
                case 0: return 7.0;
                case 6: return c.settings.diggersOn ? 7.5 : 1.5;
                case 4: return c.highestBoss >= 37 ? 4.0 : 1.0;
                default: return 1.0;
            }
        }

        /*
        SHADOW PERMANENT-ORACLE ADAPTER

        This snapshots only source-derived numeric state and never changes the native active set.
        Task 15 can use EvaluateFinalActiveSetShadow immediately before a proposed ordinary reset;
        task 28 can use EvaluateSubsetShadow at a finite next event.  Existing Beard authority stays
        unchanged until the scheduler's shadow traces are accepted.
        */
        internal static BeardSubsetDecision EvaluateSubsetShadow(double horizonSeconds,
            double rebirthSecondsAtConversion)
        {
            var c = Main.Character;
            if (c == null || !c.settings.beardsOn || c.allBeards == null || c.beards == null
                || c.beards.disabled || c.beards.beards == null)
                return new BeardSubsetDecision();
            var size = Math.Min(c.allBeards.beardSize(), c.beards.beards.Count);
            var slots = Math.Min(size, c.allBeards.capBeards());
            var energyCount = Math.Max(0, c.beards.energyBeardCount);
            var magicCount = Math.Max(0, c.beards.magicBeardCount);
            var beardverse = c.inventory != null && c.inventory.itemList != null
                             && c.inventory.itemList.beardverseComplete;
            var candidates = new List<BeardMarginalInput>();
            for (var id = 0; id < size; id++)
            {
                if (id == 6 && c.allChallenges.trollChallenge.completions() < 7) continue;
                var beard = c.beards.beards[id];
                var currentDivider = PermanentMarginalOracle.BeardCountDivider(
                    c.allBeards.usesEnergy[id] ? energyCount : magicCount, beardverse);
                var currentRate = Math.Max(0.0, c.allBeards.beardProgressPerTick(id));
                candidates.Add(new BeardMarginalInput
                {
                    Id = id,
                    UsesEnergy = c.allBeards.usesEnergy[id],
                    TemporaryLevel = beard.beardLevel,
                    Progress = beard.progress,
                    BaseProgressPerTick = currentRate * (beard.beardLevel + 1.0)
                                          * currentDivider,
                    ValuePerBankedTrimming = Utility(c, id)
                });
            }
            var perk21 = c.adventure != null && c.adventure.itopod != null
                         && c.adventure.itopod.perkLevel != null
                         && c.adventure.itopod.perkLevel.Count > 21
                ? c.adventure.itopod.perkLevel[21] : 0L;
            return PermanentMarginalOracle.SelectBeardSubset(candidates, slots,
                horizonSeconds, rebirthSecondsAtConversion, perk21, beardverse);
        }

        internal static BeardSubsetDecision EvaluateFinalActiveSetShadow(
            double rebirthSecondsAtConversion)
        {
            return EvaluateSubsetShadow(0.0, rebirthSecondsAtConversion);
        }

        internal static void Manage()
        {
            var c = Main.Character;
            if (c == null || !c.settings.beardsOn || c.allBeards == null || c.beards == null
                || c.beards.disabled || c.beards.beards == null)
                return;

            var size = Math.Min(c.allBeards.beardSize(), c.beards.beards.Count);
            var slots = Math.Min(size, c.allBeards.capBeards());
            if (slots <= 0) return;

            var candidates = new List<KeyValuePair<double, int>>();
            for (var id = 0; id < size; id++)
            {
                if (id == 6 && c.allChallenges.trollChallenge.completions() < 7)
                    continue;
                var beard = c.beards.beards[id];
                var rate = c.allBeards.beardProgressPerTick(id);
                // If the relevant resource has not filled yet, retain a stable exact-
                // divider proxy rather than oscillating the selection at zero rate.
                var speed = rate > 0 ? rate : 1.0 / Math.Max(1.0, c.allBeards.speedDivider[id]);
                var marginalPermanent = speed / Math.Sqrt(Math.Max(1.0, beard.beardLevel + 1.0));
                candidates.Add(new KeyValuePair<double, int>(Utility(c, id) * marginalPermanent, id));
            }
            var ranked = candidates.OrderByDescending(x => x.Key).Select(x => x.Value).ToList();

            // With at least two slots, force one Energy and one Magic beard before
            // accepting a same-resource penalty.  This is an exact dominance relation:
            // different resources do not divide one another's growth rate.
            var desired = new List<int>();
            if (slots >= 2)
            {
                var energy = ranked.FirstOrDefault(id => c.allBeards.usesEnergy[id]);
                var magic = ranked.FirstOrDefault(id => !c.allBeards.usesEnergy[id]);
                if (ranked.Contains(energy)) desired.Add(energy);
                if (ranked.Contains(magic) && !desired.Contains(magic)) desired.Add(magic);
            }
            foreach (var id in ranked)
            {
                if (desired.Count >= slots) break;
                if (!desired.Contains(id)) desired.Add(id);
            }

            // Preserve accumulated temporary levels through the run.  Only normalize a
            // stale selection during the first minute, before meaningful trimmings accrue.
            if (c.rebirthTime.totalseconds < 60)
            {
                foreach (var id in c.beards.activeBeards.ToArray())
                {
                    if (desired.Contains(id) || id == 6) continue;
                    c.allBeards.deactivateBeard(id);
                    Main.LogAction(!c.beards.activeBeards.Contains(id) ? "BEARD" : "REJECTED",
                        !c.beards.activeBeards.Contains(id)
                            ? "Deactivated " + GameNames.Beard(c, id) + " during first-minute beard optimization"
                            : "Could not deactivate " + GameNames.Beard(c, id));
                }
            }

            foreach (var id in desired)
            {
                if (c.beards.activeBeards.Count >= slots) break;
                if (c.beards.activeBeards.Contains(id)) continue;
                c.allBeards.activateBeard(id);
                Main.LogAction(c.beards.activeBeards.Contains(id) ? "BEARD" : "REJECTED",
                    c.beards.activeBeards.Contains(id)
                        ? "Activated " + GameNames.Beard(c, id) + " [confirmed by active-beard state]"
                        : "Activation of " + GameNames.Beard(c, id) + " produced no state transition");
            }

            // Golden Beard deactivation clears every digger.  We never deactivate it
            // here, but recap also repairs a user/game-triggered clear deterministically.
            if (c.beards.activeBeards.Contains(6) && c.settings.diggersOn)
                DiggerManager.RecapDiggers();
        }
    }
}
