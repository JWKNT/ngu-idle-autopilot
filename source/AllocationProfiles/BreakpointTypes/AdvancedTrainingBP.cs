using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NGUInjector.Autopilot;
using NGUInjector.Managers;
using UnityEngine;

/*
FILE PURPOSE

AdvancedTrainingBP allocates Energy to temporary Adventure-stat training targets. It solves the
native `1 + 0.1 * level^0.4` Power/Toughness multiplier for the minimum levels that open the next
unlocked Adventure zone, estimates their completion from the shipped 50 Hz progress formula, and
sets a finite native target before allocating. Levels reset on rebirth, so a target is admitted
only when both required tracks finish with enough time to farm the newly opened zone in this run.
*/
namespace NGUInjector.AllocationProfiles.BreakpointTypes
{
    internal class AdvancedTrainingBP : BaseBreakpoint
    {
        internal static string LastHorizonDecision { get; private set; }
            = "Advanced Training gate value has not been evaluated yet";
        internal static int LastTargetZone { get; private set; } = -1;
        internal static long LastAttackTarget { get; private set; }
        internal static long LastDefenseTarget { get; private set; }
        internal static int LastCompletionEtaSeconds { get; private set; } = -1;

        internal static string CurrentDecision(Character c)
        {
            if (c == null || c.advancedTrainingController == null)
                return "Blocked: Advanced Training controller is unavailable";
            if (!c.advancedTrainingController.advancedTrainingUnlocked())
            {
                var attack = c.training.attackTraining[4];
                var defense = c.training.defenseTraining[4];
                return "Locked: " + GameNames.AttackTraining(c, 4) + " Training " + attack
                       + "/25,000 and " + GameNames.DefenseTraining(c, 4) + " Training "
                       + defense + "/25,000 must both finish first";
            }
            return LastHorizonDecision;
        }

        protected override bool Unlocked()
        {
            return Index <= 4 && Character.advancedTrainingController.advancedTrainingUnlocked();
        }

        protected override bool TargetMet()
        {
            long target;
            if (!TryGetProgressionTarget(out target))
                return true;
            Character.advancedTraining.levelTarget[Index] = target;
            return Character.advancedTraining.level[Index] >= target;
        }

        internal override bool Allocate()
        {
            long target;
            if (!TryGetProgressionTarget(out target))
                return false;
            // levelTarget=0 means unlimited in the native controller. Always write
            // the finite, solved target so hitTarget/advanceEnergy cannot silently
            // return this allocation or train past its reset-horizon value.
            Character.advancedTraining.levelTarget[Index] = target;
            var allocation = CalculateATCap();
            if (allocation <= 0 || !SetInput(allocation))
                return false;
            switch (Index)
            {
                case 0:
                    Character.advancedTrainingController.defense.addEnergy();
                    break;
                case 1:
                    Character.advancedTrainingController.attack.addEnergy();
                    break;
                case 2:
                    Character.advancedTrainingController.block.addEnergy();
                    break;
                case 3:
                    Character.advancedTrainingController.wandoosEnergy.addEnergy();
                    break;
                case 4:
                    Character.advancedTrainingController.wandoosMagic.addEnergy();
                    break;
            }

            return true;
        }

        /*
        RESET-HORIZON ADMISSION

        Adventure Power and Toughness use separate AT rows but a zone opens only
        when both thresholds pass. Evaluate the pair together with the same capped
        share used by this profile. The completion estimate reproduces native
        progressPerTick and its one-level-per-0.02-second ceiling; the reserved farm
        window is twelve native respawns (minimum 90 seconds), enough for the new
        route to produce more than a transient stat screenshot before the reset.
        */
        private bool TryGetProgressionTarget(out long target)
        {
            target = 0;
            if (Index != 0 && Index != 1 || Character == null || Main.Autopilot == null
                || Main.Autopilot.Plan == null || Main.Autopilot.Plan.RebirthSeconds <= 0
                || ZoneStatHelper.UserOverrides == null)
            {
                LastHorizonDecision = "Blocked: no finite next-zone/rebirth model is available";
                return false;
            }

            var front = ZoneStatHelper.GetBestZone();
            var frontZone = front == null ? -1 : front.Zone;
            var maxReachable = ZoneHelpers.GetMaxReachableZone(false);
            var next = ZoneStatHelper.UserOverrides
                .Where(x => x.Key > frontZone && x.Key <= maxReachable)
                .OrderBy(x => x.Key)
                .FirstOrDefault();
            if (next.Value == null)
            {
                LastHorizonDecision = "Blocked: no higher ordinary Adventure zone is unlocked";
                LastTargetZone = -1;
                return false;
            }

            var attackLevel = Character.advancedTraining.level[1];
            var defenseLevel = Character.advancedTraining.level[0];
            var attackTarget = RequiredLevel(Character.totalAdvAttack(), attackLevel, next.Value.MPower);
            var defenseTarget = RequiredLevel(Character.totalAdvDefense(), defenseLevel, next.Value.MToughness);
            LastTargetZone = next.Key;
            LastAttackTarget = attackTarget;
            LastDefenseTarget = defenseTarget;

            if (attackTarget <= attackLevel && defenseTarget <= defenseLevel)
            {
                LastHorizonDecision = "Met: current Adventure stats already satisfy " + next.Value.Name;
                return false;
            }

            var fraction = Math.Max(0L, (long)Math.Round(CapPercent * 1000000.0,
                MidpointRounding.AwayFromZero));
            var plannedEnergy = Math.Min(Character.curEnergy,
                ExactResourceAllocator.CeilingShare(Character.curEnergy, fraction, 1000000L));
            if (plannedEnergy <= 0)
            {
                LastHorizonDecision = "Blocked: Advanced Training has zero exact Energy budget";
                return false;
            }
            var attackEta = CompletionSeconds(1, attackTarget, plannedEnergy);
            var defenseEta = CompletionSeconds(0, defenseTarget, plannedEnergy);
            var completionEta = Math.Max(attackEta, defenseEta);
            LastCompletionEtaSeconds = completionEta >= int.MaxValue
                ? -1 : (int)Math.Ceiling(completionEta);
            var remaining = Main.Autopilot.Plan.EffectiveAllocationTarget(Character)
                            - Character.rebirthTime.totalseconds;
            double respawn;
            try
            {
                respawn = Math.Max(0.0, Character.adventureController.respawnTime());
            }
            catch
            {
                respawn = 4.0;
            }
            var farmWindow = Math.Max(90.0, Math.Min(300.0, 12.0 * Math.Max(1.0, respawn)));
            if (double.IsInfinity(completionEta) || completionEta + farmWindow > remaining)
            {
                LastHorizonDecision = "Blocked: " + next.Value.Name + " needs AT "
                    + attackTarget + " Power / " + defenseTarget + " Toughness in about "
                    + (LastCompletionEtaSeconds < 0 ? "an unbounded time" : LastCompletionEtaSeconds + "s")
                    + ", leaving less than the " + Math.Ceiling(farmWindow)
                    + "s productive farm window before rebirth; AT levels reset, so no Energy is spent on an unrepaid target";
                return false;
            }

            target = Index == 1 ? attackTarget : defenseTarget;
            if (target <= Character.advancedTraining.level[Index])
                return false;
            LastHorizonDecision = "Funded: minimum AT " + attackTarget + " Power / "
                + defenseTarget + " Toughness opens " + next.Value.Name + " in about "
                + LastCompletionEtaSeconds + "s with " + Math.Floor(remaining - completionEta)
                + "s left to exploit it";
            return true;
        }

        private static long RequiredLevel(double currentTotal, long currentLevel, double threshold)
        {
            if (currentTotal >= threshold) return currentLevel;
            var currentBonus = 1.0 + 0.1 * Math.Pow(Math.Max(0L, currentLevel), 0.4);
            var withoutThisTrack = currentTotal / Math.Max(1.0, currentBonus);
            var requiredBonus = threshold / Math.Max(1e-12, withoutThisTrack);
            if (requiredBonus <= 1.0) return currentLevel;
            var solved = Math.Pow((requiredBonus - 1.0) / 0.1, 2.5);
            if (double.IsNaN(solved) || double.IsInfinity(solved) || solved >= long.MaxValue)
                return long.MaxValue;
            return Math.Max(currentLevel, (long)Math.Ceiling(solved));
        }

        private double CompletionSeconds(int index, long target, long energy)
        {
            var level = Character.advancedTraining.level[index];
            if (target <= level) return 0.0;
            if (energy <= 0 || target == long.MaxValue) return double.PositiveInfinity;
            var controller = index == 0
                ? Character.advancedTrainingController.defense
                : Character.advancedTrainingController.attack;
            var factor = Math.Sqrt(Math.Max(0.0, Character.totalEnergyPower()))
                         * Character.totalAdvancedTrainingSpeedBonus();
            if (controller == null || controller.baseTime <= 0 || factor <= 0)
                return double.PositiveInfinity;

            var k = controller.baseTime / (energy * factor);
            var progress = Math.Max(0.0, Math.Min(0.999999, Character.advancedTraining.barProgress[index]));
            var firstPerTick = 1.0 / Math.Max(double.Epsilon,
                k * (level + 1.0) * 50.0);
            var seconds = ExactResourceAllocator.NativeCompletionSeconds(progress, firstPerTick);
            var firstFollowing = level + 1L;
            var lastFollowing = target - 1L;
            if (lastFollowing < firstFollowing) return seconds;

            // For a new level L, native time is ceil(k*(L+1)/0.02)*0.02. Retain
            // exact event chunks for ordinary ranges. For extreme target gaps use
            // a conservative upper bound (continuous sum plus one tick per event),
            // which may hold AT but can never admit reset-local work too late.
            var followingCount = lastFollowing - firstFollowing + 1L;
            if (followingCount <= 10000L)
            {
                for (var offset = 0L; offset < followingCount; offset++)
                {
                    var following = firstFollowing + offset;
                    var perTick = 1.0 / Math.Max(double.Epsilon,
                        k * (following + 1.0) * 50.0);
                    seconds += ExactResourceAllocator.NativeCompletionSeconds(0.0, perTick);
                }
                return seconds;
            }

            // Split the arithmetic series at the one-level-per-tick cap before
            // applying the conservative sub-tick rounding envelope.
            var cappedThrough = k <= 0 ? lastFollowing
                : (long)Math.Floor(0.02 / k - 1.0);
            var cappedLast = Math.Min(lastFollowing, Math.Max(firstFollowing - 1, cappedThrough));
            if (cappedLast >= firstFollowing)
                seconds += (cappedLast - firstFollowing + 1.0) * 0.02;
            var linearFirst = Math.Max(firstFollowing, cappedLast + 1L);
            if (linearFirst <= lastFollowing)
            {
                var count = lastFollowing - linearFirst + 1.0;
                var sumLevelsPlusOne = count * ((linearFirst + 1.0) + (lastFollowing + 1.0)) / 2.0;
                seconds += k * sumLevelsPlusOne + count * 0.02;
            }
            return seconds;
        }

        protected override bool CorrectResourceType()
        {
            return Type == ResourceType.Energy;
        }

        private long CalculateATCap()
        {
            var calcA = CalculateATCap(500);
            if (calcA.PPT < 1)
            {
                var calcB = CalculateATCap(calcA.GetOffset());
                return calcB.Num;
            }

            return calcA.Num;
        }

        private CapCalc CalculateATCap(int offset)
        {
            var ret = new CapCalc
            {
                Num = 0,
                PPT = 1
            };
            var divisor = GetDivisor(Index, offset);
            if (divisor == 0.0)
                return ret;

            if (Character.wishes.wishes[190].level >= 1)
                return ret;

            var factor = Math.Sqrt(Math.Max(0.0, Character.totalEnergyPower()))
                         * Character.totalAdvancedTrainingSpeedBonus();
            if (factor <= 0.0 || MaxAllocation <= 0)
                return ret;
            var formula = 50.0 * divisor / factor;

            if (formula >= Character.hardCap())
            {
                formula = Character.hardCap();
            }

            var num = ExactResourceAllocator.CapAtTickBoundary(formula, MaxAllocation,
                Character.idleEnergy);

            ret.Num = num;
            ret.PPT = formula > 0.0 ? (double)num / formula : 0.0;
            return ret;
        }

        private float GetDivisor(int index, int offset)
        {
            float baseTime;
            switch (index)
            {
                case 0:
                    baseTime = Character.advancedTrainingController.defense.baseTime;
                    break;
                case 1:
                    baseTime = Character.advancedTrainingController.attack.baseTime;
                    break;
                case 2:
                    baseTime = Character.advancedTrainingController.block.baseTime;
                    break;
                case 3:
                    baseTime = Character.advancedTrainingController.wandoosEnergy.baseTime;
                    break;
                case 4:
                    baseTime = Character.advancedTrainingController.wandoosMagic.baseTime;
                    break;
                default:
                    baseTime = 0.0f;
                    break;
            }

            return baseTime * (Character.advancedTraining.level[index] + offset + 1f);
        }
    }
}
