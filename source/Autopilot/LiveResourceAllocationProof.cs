/*
FILE PURPOSE

LiveResourceAllocationProof is the installed-build adapter between CustomAllocation's native
resource sweep and ExactResourceAllocator's pure full-vector settlement proof.  It captures every
Energy, Magic, and Resource 3 allocation target reached by Character.removeAllEnergy,
removeAllMagic, and removeAllRes3, plus Advanced Training's allocation-control targets.  It emits
an immutable snapshot suitable for a MutationCoordinator before/requested-after receipt.

Recovery uses only native remove/add and target-parser controller calls.  It first reclaims all
three resources, then replays the exact prior target vector and confirms exact conservation/equality.
It never rewrites resource-allocation fields and never claims recovery from an aggregate idle value. A missing target,
changed collection shape, native clamp/no-op, or UI-parser mismatch fails restoration so the owning
Allocation mutation class remains quarantined.  Strategic target selection and resource valuation
remain in CustomAllocation/the global planner; this file only proves and restores live settlement.
*/
using System;
using System.Collections.Generic;

namespace NGUInjector.Autopilot
{
    internal sealed class LiveResourceAllocationSnapshot
    {
        internal readonly ExactAllocationVector Energy;
        internal readonly ExactAllocationVector Magic;
        internal readonly ExactAllocationVector Resource3;
        internal readonly long[] AdvancedTrainingLevelTargets;
        internal readonly long PlanVersion;
        internal readonly string PlanFingerprint;

        internal LiveResourceAllocationSnapshot(ExactAllocationVector energy,
            ExactAllocationVector magic, ExactAllocationVector resource3,
            long[] advancedTrainingLevelTargets, long planVersion, string planFingerprint)
        {
            Energy = energy;
            Magic = magic;
            Resource3 = resource3;
            AdvancedTrainingLevelTargets = advancedTrainingLevelTargets == null
                ? new long[0] : (long[])advancedTrainingLevelTargets.Clone();
            PlanVersion = planVersion;
            PlanFingerprint = planFingerprint ?? string.Empty;
        }

        internal bool IsComplete(out string reason)
        {
            if (PlanVersion <= 0L || string.IsNullOrEmpty(PlanFingerprint))
            {
                reason = "no verified generated allocation plan is installed";
                return false;
            }
            if (Energy == null || Magic == null || Resource3 == null)
            {
                reason = "one or more full native resource vectors are unavailable";
                return false;
            }
            if (!Energy.IsConserved() || !Magic.IsConserved() || !Resource3.IsConserved())
            {
                reason = "captured native target vectors do not conserve Energy/Magic/Resource 3";
                return false;
            }
            for (var i = 0; i < AdvancedTrainingLevelTargets.Length; i++)
            {
                if (AdvancedTrainingLevelTargets[i] < 0L)
                {
                    reason = "Advanced Training contains a negative native level target";
                    return false;
                }
            }
            reason = string.Empty;
            return true;
        }

        internal bool ExactEquals(LiveResourceAllocationSnapshot other)
        {
            if (other == null || PlanVersion != other.PlanVersion
                || !string.Equals(PlanFingerprint, other.PlanFingerprint,
                    StringComparison.Ordinal)
                || Energy == null || !Energy.ExactEquals(other.Energy)
                || Magic == null || !Magic.ExactEquals(other.Magic)
                || Resource3 == null || !Resource3.ExactEquals(other.Resource3)
                || AdvancedTrainingLevelTargets.Length
                   != other.AdvancedTrainingLevelTargets.Length)
                return false;
            for (var i = 0; i < AdvancedTrainingLevelTargets.Length; i++)
                if (AdvancedTrainingLevelTargets[i] != other.AdvancedTrainingLevelTargets[i])
                    return false;
            return true;
        }

        internal string Fingerprint()
        {
            var controls = string.Join(",", Array.ConvertAll(AdvancedTrainingLevelTargets,
                x => x.ToString()));
            return PlanVersion + ":" + PlanFingerprint + "|" + Energy.Fingerprint()
                   + "|" + Magic.Fingerprint() + "|" + Resource3.Fingerprint()
                   + "|at-targets=" + controls;
        }
    }

    internal static class LiveResourceAllocationProof
    {
        internal static LiveResourceAllocationSnapshot Capture(Character character,
            long planVersion, string planFingerprint)
        {
            try
            {
                if (!HasRequiredState(character)) return null;
                var energy = new SortedDictionary<string, long>(StringComparer.Ordinal);
                var magic = new SortedDictionary<string, long>(StringComparer.Ordinal);
                var res3 = new SortedDictionary<string, long>(StringComparer.Ordinal);

                for (var i = 0; i < character.training.attackEnergy.Length; i++)
                    energy.Add("training.attack." + i, character.training.attackEnergy[i]);
                for (var i = 0; i < character.training.defenseEnergy.Length; i++)
                    energy.Add("training.defense." + i, character.training.defenseEnergy[i]);
                for (var i = 0; i < character.augments.augs.Length; i++)
                {
                    energy.Add("augment." + i + ".augment", character.augments.augs[i].augEnergy);
                    energy.Add("augment." + i + ".upgrade", character.augments.augs[i].upgradeEnergy);
                }
                for (var i = 0; i < character.advancedTraining.energy.Length; i++)
                    energy.Add("advanced-training." + i, character.advancedTraining.energy[i]);
                energy.Add("wandoos.energy", character.wandoos98.wandoosEnergy);
                energy.Add("time-machine.speed", character.machine.speedEnergy);
                for (var i = 0; i < character.NGU.skills.Count; i++)
                    energy.Add("ngu.energy." + i, character.NGU.skills[i].energy);
                for (var i = 0; i < character.wishes.wishes.Count; i++)
                    energy.Add("wish." + i, character.wishes.wishes[i].energy);

                magic.Add("wandoos.magic", character.wandoos98.wandoosMagic);
                magic.Add("time-machine.gold", character.machine.goldMultiMagic);
                for (var i = 0; i < character.bloodMagic.ritual.Count; i++)
                    magic.Add("blood." + i, character.bloodMagic.ritual[i].magic);
                for (var i = 0; i < character.NGU.magicSkills.Count; i++)
                    magic.Add("ngu.magic." + i, character.NGU.magicSkills[i].magic);
                for (var i = 0; i < character.wishes.wishes.Count; i++)
                    magic.Add("wish." + i, character.wishes.wishes[i].magic);

                for (var i = 0; i < character.hacks.hacks.Count; i++)
                    res3.Add("hack." + i, character.hacks.hacks[i].res3);
                for (var i = 0; i < character.wishes.wishes.Count; i++)
                    res3.Add("wish." + i, character.wishes.wishes[i].res3);

                return new LiveResourceAllocationSnapshot(
                    new ExactAllocationVector(ExactResourceKind.Energy, character.curEnergy,
                        character.idleEnergy, energy),
                    new ExactAllocationVector(ExactResourceKind.Magic, character.magic.curMagic,
                        character.magic.idleMagic, magic),
                    new ExactAllocationVector(ExactResourceKind.Resource3, character.res3.curRes3,
                        character.res3.idleRes3, res3),
                    character.advancedTraining.levelTarget, planVersion, planFingerprint);
            }
            catch
            {
                return null;
            }
        }

        internal static bool VerifySettlement(LiveResourceAllocationSnapshot before,
            LiveResourceAllocationSnapshot requestedAfter,
            LiveResourceAllocationSnapshot observedAfter, out string reason)
        {
            if (before == null || requestedAfter == null || observedAfter == null)
            {
                reason = "allocation before/requested/observed snapshot is unavailable";
                return false;
            }
            if (before.PlanVersion != requestedAfter.PlanVersion
                || before.PlanVersion != observedAfter.PlanVersion
                || !string.Equals(before.PlanFingerprint, requestedAfter.PlanFingerprint,
                    StringComparison.Ordinal)
                || !string.Equals(before.PlanFingerprint, observedAfter.PlanFingerprint,
                    StringComparison.Ordinal))
            {
                reason = "generated allocation plan changed during its native sweep";
                return false;
            }
            if (!VerifyResource(before.Energy, requestedAfter.Energy, observedAfter.Energy,
                    out reason)
                || !VerifyResource(before.Magic, requestedAfter.Magic, observedAfter.Magic,
                    out reason)
                || !VerifyResource(before.Resource3, requestedAfter.Resource3,
                    observedAfter.Resource3, out reason))
                return false;
            if (!requestedAfter.ExactEquals(observedAfter))
            {
                reason = "accepted native allocation/control vector changed after Apply returned";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        internal static bool Restore(Character character, LiveResourceAllocationSnapshot expected,
            out string reason)
        {
            reason = string.Empty;
            if (character == null || expected == null)
            {
                reason = "character/prior allocation snapshot is unavailable";
                return false;
            }
            if (!expected.IsComplete(out reason))
                return false;
            if (!MatchesLiveSchema(character, expected, out reason)) return false;

            var originalInput = character.energyMagicPanel.energyMagicInput;
            try
            {
                character.removeAllEnergy();
                character.removeAllMagic();
                character.removeAllRes3();
                if (!RestoreAdvancedTrainingTargets(character,
                        expected.AdvancedTrainingLevelTargets, out reason))
                    return false;

                if (!RestoreEnergy(character, expected.Energy, out reason)
                    || !RestoreMagic(character, expected.Magic, out reason)
                    || !RestoreRes3(character, expected.Resource3, out reason))
                    return false;

                var observed = Capture(character, expected.PlanVersion, expected.PlanFingerprint);
                if (!expected.ExactEquals(observed))
                {
                    reason = "native replay returned without restoring the exact prior target vector";
                    return false;
                }
                reason = "exact prior target vector restored through native controllers";
                return true;
            }
            catch (Exception error)
            {
                reason = "native allocation restore threw: " + error.Message;
                return false;
            }
            finally
            {
                character.energyMagicPanel.energyRequested.text =
                    ExactResourceAllocator.FormatExactInput(originalInput);
                character.energyMagicPanel.validateInput();
            }
        }

        private static bool VerifyResource(ExactAllocationVector before,
            ExactAllocationVector requested, ExactAllocationVector observed, out string reason)
        {
            return new ExactAllocationSettlement(before, requested)
                .VerifyAcceptedNativeState(observed, out reason);
        }

        private static bool RestoreEnergy(Character c, ExactAllocationVector vector,
            out string reason)
        {
            long amount;
            for (var i = 0; i < c.training.attackEnergy.Length; i++)
            {
                vector.TryGet("training.attack." + i, out amount);
                if (amount > 0L) c.allOffenseController.trains[i].addEnergy(amount);
            }
            for (var i = 0; i < c.training.defenseEnergy.Length; i++)
            {
                vector.TryGet("training.defense." + i, out amount);
                if (amount > 0L) c.allDefenseController.trains[i].addEnergy(amount);
            }
            for (var i = 0; i < c.augments.augs.Length; i++)
            {
                vector.TryGet("augment." + i + ".augment", out amount);
                if (amount > 0L)
                {
                    if (!SetInput(c, amount, out reason)) return false;
                    c.augmentsController.augments[i].addEnergyAug();
                }
                vector.TryGet("augment." + i + ".upgrade", out amount);
                if (amount > 0L)
                {
                    if (!SetInput(c, amount, out reason)) return false;
                    c.augmentsController.augments[i].addEnergyUpgrade();
                }
            }
            for (var i = 0; i < c.advancedTraining.energy.Length; i++)
            {
                vector.TryGet("advanced-training." + i, out amount);
                if (amount <= 0L) continue;
                if (!SetInput(c, amount, out reason)) return false;
                if (i == 0) c.advancedTrainingController.defense.addEnergy();
                else if (i == 1) c.advancedTrainingController.attack.addEnergy();
                else if (i == 2) c.advancedTrainingController.block.addEnergy();
                else if (i == 3) c.advancedTrainingController.wandoosEnergy.addEnergy();
                else if (i == 4) c.advancedTrainingController.wandoosMagic.addEnergy();
                else
                {
                    reason = "unknown Advanced Training allocation target " + i;
                    return false;
                }
            }
            vector.TryGet("wandoos.energy", out amount);
            if (amount > 0L)
            {
                if (!SetInput(c, amount, out reason)) return false;
                c.wandoos98Controller.addEnergy();
            }
            vector.TryGet("time-machine.speed", out amount);
            if (amount > 0L)
            {
                if (!SetInput(c, amount, out reason)) return false;
                c.timeMachineController.addEnergy();
            }
            for (var i = 0; i < c.NGU.skills.Count; i++)
            {
                vector.TryGet("ngu.energy." + i, out amount);
                if (amount <= 0L) continue;
                if (!SetInput(c, amount, out reason)) return false;
                c.NGUController.NGU[i].add();
            }
            for (var i = 0; i < c.wishes.wishes.Count; i++)
            {
                vector.TryGet("wish." + i, out amount);
                if (amount <= 0L) continue;
                if (!SetInput(c, amount, out reason)) return false;
                c.wishesController.addEnergy(i);
            }
            reason = string.Empty;
            return true;
        }

        private static bool RestoreMagic(Character c, ExactAllocationVector vector,
            out string reason)
        {
            long amount;
            vector.TryGet("wandoos.magic", out amount);
            if (amount > 0L)
            {
                if (!SetInput(c, amount, out reason)) return false;
                c.wandoos98Controller.addMagic();
            }
            vector.TryGet("time-machine.gold", out amount);
            if (amount > 0L)
            {
                if (!SetInput(c, amount, out reason)) return false;
                c.timeMachineController.addMagic();
            }
            for (var i = 0; i < c.bloodMagic.ritual.Count; i++)
            {
                vector.TryGet("blood." + i, out amount);
                if (amount <= 0L) continue;
                if (!SetInput(c, amount, out reason)) return false;
                c.bloodMagicController.bloodMagics[i].add();
            }
            for (var i = 0; i < c.NGU.magicSkills.Count; i++)
            {
                vector.TryGet("ngu.magic." + i, out amount);
                if (amount <= 0L) continue;
                if (!SetInput(c, amount, out reason)) return false;
                c.NGUController.NGUMagic[i].add();
            }
            for (var i = 0; i < c.wishes.wishes.Count; i++)
            {
                vector.TryGet("wish." + i, out amount);
                if (amount <= 0L) continue;
                if (!SetInput(c, amount, out reason)) return false;
                c.wishesController.addMagic(i);
            }
            reason = string.Empty;
            return true;
        }

        private static bool RestoreRes3(Character c, ExactAllocationVector vector,
            out string reason)
        {
            long amount;
            for (var i = 0; i < c.hacks.hacks.Count; i++)
            {
                vector.TryGet("hack." + i, out amount);
                if (amount > 0L) c.hacksController.addR3(i, amount);
            }
            for (var i = 0; i < c.wishes.wishes.Count; i++)
            {
                vector.TryGet("wish." + i, out amount);
                if (amount <= 0L) continue;
                if (!SetInput(c, amount, out reason)) return false;
                c.wishesController.addRes3(i);
            }
            reason = string.Empty;
            return true;
        }

        private static bool SetInput(Character c, long amount, out string reason)
        {
            if (amount <= 0L)
            {
                reason = "native allocation replay received a nonpositive request";
                return false;
            }
            c.energyMagicPanel.energyRequested.text =
                ExactResourceAllocator.FormatExactInput(amount);
            c.energyMagicPanel.validateInput();
            // This is the native request field, not a resource/allocation field.  The installed UI
            // parser narrows decimal text through double above 2^53; restore the already bounded
            // exact Int64 request just as BaseBreakpoint does before the native controller consumes
            // it.  Post-settlement proof still comes exclusively from controller-owned targets.
            if (c.energyMagicPanel.energyMagicInput != amount)
            {
                c.energyMagicPanel.energyMagicInput = amount;
                c.energyMagicPanel.energyRequested.text =
                    ExactResourceAllocator.FormatExactInput(amount);
            }
            if (c.energyMagicPanel.energyMagicInput != amount)
            {
                reason = "native exact-input parser did not preserve the Int64 request";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        private static bool RestoreAdvancedTrainingTargets(Character c, long[] targets,
            out string reason)
        {
            if (targets == null || targets.Length != 5)
            {
                reason = "installed Advanced Training target topology is not the audited five rows";
                return false;
            }
            var controllers = new[]
            {
                c.advancedTrainingController.defense,
                c.advancedTrainingController.attack,
                c.advancedTrainingController.block,
                c.advancedTrainingController.wandoosEnergy,
                c.advancedTrainingController.wandoosMagic
            };
            for (var i = 0; i < targets.Length; i++)
            {
                controllers[i].target.text = ExactResourceAllocator.FormatExactInput(targets[i]);
                controllers[i].checkTargetInput();
                if (c.advancedTraining.levelTarget[i] != targets[i])
                {
                    reason = "native Advanced Training target parser rejected row " + i;
                    return false;
                }
            }
            reason = string.Empty;
            return true;
        }

        private static bool HasRequiredState(Character c)
        {
            return c != null && c.training != null && c.training.attackEnergy != null
                   && c.training.defenseEnergy != null && c.augments != null
                   && c.augments.augs != null && c.advancedTraining != null
                   && c.advancedTraining.energy != null
                   && c.advancedTraining.levelTarget != null && c.wandoos98 != null
                   && c.machine != null && c.NGU != null && c.NGU.skills != null
                   && c.NGU.magicSkills != null && c.magic != null && c.bloodMagic != null
                   && c.bloodMagic.ritual != null && c.res3 != null && c.hacks != null
                   && c.hacks.hacks != null && c.wishes != null && c.wishes.wishes != null
                   && c.energyMagicPanel != null && c.energyMagicPanel.energyRequested != null;
        }

        private static bool MatchesLiveSchema(Character c,
            LiveResourceAllocationSnapshot expected, out string reason)
        {
            var observed = Capture(c, expected.PlanVersion, expected.PlanFingerprint);
            if (observed == null || !expected.Energy.HasSameSchema(observed.Energy)
                || !expected.Magic.HasSameSchema(observed.Magic)
                || !expected.Resource3.HasSameSchema(observed.Resource3)
                || expected.AdvancedTrainingLevelTargets.Length
                   != observed.AdvancedTrainingLevelTargets.Length)
            {
                reason = "live native allocation target schema changed before recovery";
                return false;
            }
            if (c.allOffenseController == null || c.allDefenseController == null
                || c.allOffenseController.trains == null || c.allDefenseController.trains == null
                || c.allOffenseController.trains.Length != c.training.attackEnergy.Length
                || c.allDefenseController.trains.Length != c.training.defenseEnergy.Length
                || c.augmentsController == null || c.augmentsController.augments == null
                || c.augmentsController.augments.Length != c.augments.augs.Length
                || c.advancedTrainingController == null || c.NGUController == null
                || c.NGUController.NGU == null || c.NGUController.NGUMagic == null
                || c.NGUController.NGU.Length != c.NGU.skills.Count
                || c.NGUController.NGUMagic.Length != c.NGU.magicSkills.Count
                || c.wandoos98Controller == null || c.timeMachineController == null
                || c.bloodMagicController == null || c.bloodMagicController.bloodMagics == null
                || c.bloodMagicController.bloodMagics.Length != c.bloodMagic.ritual.Count
                || c.hacksController == null || c.wishesController == null)
            {
                reason = "native allocation controller topology is incomplete or mismatched";
                return false;
            }
            reason = string.Empty;
            return true;
        }
    }
}
