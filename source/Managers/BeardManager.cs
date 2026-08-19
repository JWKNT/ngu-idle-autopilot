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
        private sealed class BeardTogglePlan
        {
            internal int Id;
            internal bool Activate;
        }

        private sealed class BeardToggleState
        {
            internal int[] Active;
            internal long[] Levels;
            internal float[] Progress;
        }

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
            ExecutionSafety.ReportHold("beards-root-required",
                "Beard-set changes require the caller-owned nonzero root transaction.");
        }

        internal static MutationResult Manage(RootTransaction root)
        {
            var c = Main.Character;
            if (root == null || root.IsClosed || c == null || !c.settings.beardsOn
                || c.allBeards == null || c.beards == null
                || c.beards.disabled || c.beards.beards == null)
                return null;

            var size = Math.Min(c.allBeards.beardSize(), c.beards.beards.Count);
            var slots = Math.Min(size, c.allBeards.capBeards());
            if (slots <= 0) return null;

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
                var energy = ranked.Where(id => c.allBeards.usesEnergy[id])
                    .Select(id => (int?)id).FirstOrDefault();
                var magic = ranked.Where(id => !c.allBeards.usesEnergy[id])
                    .Select(id => (int?)id).FirstOrDefault();
                if (energy.HasValue) desired.Add(energy.Value);
                if (magic.HasValue && !desired.Contains(magic.Value)) desired.Add(magic.Value);
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
                    return root.ExecuteChild(new BeardToggleIntent(c,
                        new BeardTogglePlan {Id = id, Activate = false}));
                }
            }

            foreach (var id in desired)
            {
                if (c.beards.activeBeards.Count >= slots) break;
                if (c.beards.activeBeards.Contains(id)) continue;
                return root.ExecuteChild(new BeardToggleIntent(c,
                    new BeardTogglePlan {Id = id, Activate = true}));
            }
            return null;
        }

        /*
        ONE EXACT BEARD TOGGLE

        Activation/deactivation is reversible and must not smuggle a Digger recap into the Beard
        child.  Golden Beard is never selected for deactivation because native deactivation clears
        every active Digger.  The postcondition proves one exact membership delta while every
        synchronous temporary level/progress value remains unchanged.
        */
        private sealed class BeardToggleIntent :
            IMutationIntent<BeardToggleState, bool, BeardToggleState>
        {
            private readonly Character _character;
            private readonly BeardTogglePlan _plan;

            internal BeardToggleIntent(Character character, BeardTogglePlan plan)
            {
                _character = character;
                _plan = plan;
            }

            public string Id { get { return "beards." + (_plan.Activate ? "activate." : "deactivate.") + _plan.Id; } }
            public MutationClass Class { get { return MutationClass.Beards; } }
            public MutationRisk Risk { get { return MutationRisk.Reversible; } }
            public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
            public string BindingId { get { return "AllBeards." + (_plan.Activate ? "activateBeard" : "deactivateBeard") + "(int)/public-exact"; } }
            public bool Required { get { return false; } }
            public bool CanCompensate { get { return true; } }
            public bool CreatesNewEpoch { get { return false; } }
            public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

            public BeardToggleState CaptureBefore(MutationContext context) { return Capture(); }

            public PreconditionResult CheckPreconditions(MutationContext context,
                BeardToggleState before)
            {
                if (!Main.IsAutomationReady)
                    return PreconditionResult.Hold("gameplay synchronization is not current");
                if (before == null || _plan.Id < 0 || _plan.Id >= before.Levels.Length)
                    return PreconditionResult.Hold("Beard state/ID is unavailable");
                var active = before.Active.Contains(_plan.Id);
                if (_plan.Activate == active)
                    return PreconditionResult.AlreadySatisfied("requested Beard membership already holds");
                if (!_plan.Activate && _plan.Id == 6)
                    return PreconditionResult.Hold("Golden Beard deactivation would clear Diggers");
                if (_plan.Activate && before.Active.Length >= _character.allBeards.capBeards())
                    return PreconditionResult.Hold("no native Beard slot is open");
                return PreconditionResult.Ready();
            }

            public bool Apply(MutationContext context, RootTransactionToken token,
                BeardToggleState before)
            {
                if (_plan.Activate) _character.allBeards.activateBeard(_plan.Id);
                else _character.allBeards.deactivateBeard(_plan.Id);
                return true;
            }

            public VerificationResult<BeardToggleState> Verify(MutationContext context,
                BeardToggleState before, MutationApplyObservation<bool> apply)
            {
                var after = Capture();
                var expected = before.Active.Where(x => x != _plan.Id).ToList();
                if (_plan.Activate) expected.Add(_plan.Id);
                expected.Sort();
                var valid = apply.ReturnedNormally && apply.Value && after != null
                            && expected.SequenceEqual(after.Active)
                            && before.Levels.SequenceEqual(after.Levels)
                            && before.Progress.SequenceEqual(after.Progress);
                if (!valid)
                    return VerificationResult<BeardToggleState>.Failed(
                        "Beard toggle lacked its one-membership/no-progress-delta postcondition");
                Main.LogAction("BEARD", (_plan.Activate ? "Activated " : "Deactivated ")
                    + GameNames.Beard(_character, _plan.Id)
                    + " [one exact active-set delta confirmed]");
                return VerificationResult<BeardToggleState>.Satisfied(after,
                    "one exact Beard membership transition confirmed");
            }

            public CompensationResult Compensate(MutationContext context, RecoveryToken token,
                BeardToggleState before, MutationApplyObservation<bool> apply)
            {
                if (_plan.Activate) _character.allBeards.deactivateBeard(_plan.Id);
                else _character.allBeards.activateBeard(_plan.Id);
                return BeforeStateMatches(before, Capture())
                    ? CompensationResult.Restored("Beard membership restored")
                    : CompensationResult.Failed("Beard membership could not be restored exactly");
            }

            public bool BeforeStateMatches(BeardToggleState expected, BeardToggleState observed)
            {
                return expected != null && observed != null
                       && expected.Active.SequenceEqual(observed.Active)
                       && expected.Levels.SequenceEqual(observed.Levels)
                       && expected.Progress.SequenceEqual(observed.Progress);
            }

            public string FingerprintBefore(BeardToggleState state) { return Fingerprint(state); }
            public string FingerprintAfter(BeardToggleState state) { return Fingerprint(state); }

            private BeardToggleState Capture()
            {
                if (_character == null || _character.beards == null
                    || _character.beards.beards == null) return null;
                return new BeardToggleState
                {
                    Active = _character.beards.activeBeards.OrderBy(x => x).ToArray(),
                    Levels = _character.beards.beards.Select(x => x.beardLevel).ToArray(),
                    Progress = _character.beards.beards.Select(x => x.progress).ToArray()
                };
            }

            private static string Fingerprint(BeardToggleState state)
            {
                return state == null ? "missing"
                    : string.Join(",", state.Active.Select(x => x.ToString()).ToArray()) + ":"
                      + string.Join(",", state.Levels.Select(x => x.ToString()).ToArray()) + ":"
                      + string.Join(",", state.Progress.Select(x => x.ToString("R")).ToArray());
            }
        }
    }
}
