using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

BR allocates otherwise-uncommitted Magic to Blood Magic rituals that can actually finish before
the selected rebirth checkpoint. Ritual progress and levels reset, so an unfinished level has no
run value; a newly started level also needs its native Gold charge. This class is the authoritative
reason source for Blood allocation telemetry so a Gold wait is not mislabeled as idle Magic waste.
Rebirth selection itself belongs to RebirthOptimizer/TimeRebirth.
*/
namespace NGUInjector.AllocationProfiles.BreakpointTypes
{
    internal class BR : BaseBreakpoint
    {
        internal static string LastDecision { get; private set; } = "Blood Magic has not been evaluated";
        internal static double LastGoldShortfall { get; private set; }
        internal int RebirthTime { get; set; }
        protected override bool Unlocked()
        {
            return Character.buttons.bloodMagic.interactable;
        }

        protected override bool TargetMet()
        {
            return false;
        }

        internal override bool Allocate()
        {
            if (Index == 0)
            {
                CastRituals();
            }
            else
            {
                CastRitualEndTime(Index);
            }

            return true;
        }

        protected override bool CorrectResourceType()
        {
            return Type == ResourceType.Magic;
        }

        private void CastRituals()
        {
            LastGoldShortfall = 0.0;
            double valuedTarget;
            string valuedTargetLabel;
            if (!ResourceHorizonModel.TryGetValuedBloodDemand(Character,
                    Math.Max(1, SecondsRemaining()), out valuedTarget, out valuedTargetLabel))
            {
                ClearRitualAllocations();
                LastDecision = "Holding Magic: no concrete Blood spell or terminal item demand "
                               + "can be funded before the selected rebirth checkpoint";
                return;
            }
            var allocationLeft = (long)MaxAllocation;
            var allocated = 0L;
            var allocatedTracks = new List<string>();
            var goldBlocked = 0;
            var horizonBlocked = 0;
            var durationBlocked = 0;
            var nearestGoldShortfall = double.PositiveInfinity;
            var nearestGoldTrack = -1;
            var nearestGoldCost = 0.0;
            var effectiveTarget = EffectiveRebirthTarget();
            for (var i = Character.bloodMagic.ritual.Count - 1; i >= 0; i--)
            {
                if (allocationLeft <= 0)
                    break;
                if (Character.magic.idleMagic == 0)
                    break;
                if (i >= Character.bloodMagicController.ritualsUnlocked())
                    continue;
                var goldCost = Character.bloodMagicController.bloodMagics[i].baseCost * Character.totalDiscount();
                if (goldCost > Character.realGold && Character.bloodMagic.ritual[i].progress <= 0.0)
                {
                    goldBlocked++;
                    var shortfall = goldCost - Character.realGold;
                    if (shortfall < nearestGoldShortfall)
                    {
                        nearestGoldShortfall = shortfall;
                        nearestGoldTrack = i;
                        nearestGoldCost = goldCost;
                    }
                    if (Character.bloodMagic.ritual[i].magic > 0)
                    {
                        Character.bloodMagicController.bloodMagics[i].removeAllMagic();
                    }

                    continue;
                }

                var tLeft = RitualTimeLeft(i, allocationLeft);

                if (tLeft > 3600)
                {
                    durationBlocked++;
                    continue;
                }

                // Ritual progress and levels reset. The former comparison used
                // elapsed-tLeft, which answered whether this level could have
                // finished since run start rather than whether it can finish before
                // the chosen checkpoint. Admit only a fully realizable Blood gain.
                if (effectiveTarget > 0)
                {
                    if (Character.rebirthTime.totalseconds + tLeft > effectiveTarget)
                    {
                        horizonBlocked++;
                        continue;
                    }
                }

                var cap = CalculateMaxAllocation(i, allocationLeft);
                SetInput(cap);
                var before = Character.bloodMagic.ritual[i].magic;
                Character.bloodMagicController.bloodMagics[i].add();
                var accepted = Math.Max(0L, Character.bloodMagic.ritual[i].magic - before);
                allocated += accepted;
                allocationLeft -= accepted;
                if (accepted > 0)
                    allocatedTracks.Add("ritual " + (i + 1) + " (" + tLeft.ToString("0.0") + "s)");
            }

            if (allocated > 0)
            {
                LastDecision = "Allocated " + allocated + " Magic to "
                               + string.Join(", ", allocatedTracks.ToArray())
                               + "; each admitted level finishes before rebirth";
            }
            else if (goldBlocked > 0 && nearestGoldTrack >= 0)
            {
                LastGoldShortfall = nearestGoldShortfall;
                var gps = Math.Max(0.0, Character.grossGoldPerSecond());
                var eta = gps > 0 ? " (~" + Math.Ceiling(nearestGoldShortfall / gps) + "s at current gross GPS)" : string.Empty;
                LastDecision = "Waiting for another " + FormatGold(nearestGoldShortfall)
                               + eta + " to start Blood ritual " + (nearestGoldTrack + 1)
                               + " (cost " + FormatGold(nearestGoldCost) + "); idle Magic cannot bypass the native Gold charge";
            }
            else if (horizonBlocked > 0)
            {
                LastDecision = "Holding Magic: " + horizonBlocked
                               + " unlocked Blood ritual target(s) cannot finish before the selected rebirth checkpoint";
            }
            else if (durationBlocked > 0)
            {
                LastDecision = "Holding Magic: unlocked Blood rituals require more than one hour per level at the available allocation";
            }
            else
            {
                LastDecision = "No unlocked Blood ritual can productively accept the available Magic";
            }
        }

        private void CastRitualEndTime(int endTime)
        {
            double valuedTarget;
            string valuedTargetLabel;
            if (!ResourceHorizonModel.TryGetValuedBloodDemand(Character,
                    Math.Max(1, SecondsRemaining()), out valuedTarget, out valuedTargetLabel))
            {
                ClearRitualAllocations();
                LastDecision = "Holding Magic: no route-valued Blood target is reachable before "
                               + "the configured ritual deadline";
                return;
            }
            var allocationLeft = (long)MaxAllocation;
            for (var i = Character.bloodMagic.ritual.Count - 1; i >= 0; i--)
            {
                if (allocationLeft <= 0)
                    break;
                if (Character.magic.idleMagic == 0)
                    break;
                if (i >= Character.bloodMagicController.ritualsUnlocked())
                    continue;
                var goldCost = Character.bloodMagicController.bloodMagics[i].baseCost * Character.totalDiscount();
                if (goldCost > Character.realGold && Character.bloodMagic.ritual[i].progress <= 0.0)
                {
                    if (Character.bloodMagic.ritual[i].magic > 0)
                    {
                        Character.bloodMagicController.bloodMagics[i].removeAllMagic();
                    }

                    continue;
                }

                var tLeft = RitualTimeLeft(i, allocationLeft);

                var effectiveTarget = EffectiveRebirthTarget();
                if (effectiveTarget > 0)
                {
                    if (Character.rebirthTime.totalseconds + tLeft > effectiveTarget)
                        continue;
                }

                if (Character.rebirthTime.totalseconds + tLeft > endTime)
                    continue;

                var cap = CalculateMaxAllocation(i, allocationLeft);
                SetInput(cap);
                Character.bloodMagicController.bloodMagics[i].add();
                allocationLeft -= cap;
            }
        }

        private float RitualProgressPerTick(int id, long remaining)
        {
            var num1 = 0.0;
            if (Character.settings.rebirthDifficulty == difficulty.normal)
                num1 = remaining * (double)Character.totalMagicPower() / 50000.0 /
                       Character.bloodMagicController.normalSpeedDividers[id];
            else if (Character.settings.rebirthDifficulty == difficulty.evil)
                num1 = remaining * (double)Character.totalMagicPower() / 50000.0 /
                       Character.bloodMagicController.evilSpeedDividers[id];
            else if (Character.settings.rebirthDifficulty == difficulty.sadistic)
                num1 = remaining * (double)Character.totalMagicPower() /
                       Character.bloodMagicController.sadisticSpeedDividers[id];
            if (Character.settings.rebirthDifficulty >= difficulty.sadistic)
                num1 /= Character.bloodMagicController.bloodMagics[id].sadisticDivider();
            var num2 = num1 * Character.bloodMagicController.bloodMagics[id].totalBloodMagicSpeedBonus();
            if (num2 <= -3.40282346638529E+38)
                num2 = 0.0;
            if (num2 >= 3.40282346638529E+38)
                num2 = 3.40282346638529E+38;
            return (float)num2;
        }

        public float RitualTimeLeft(int id, long remaining)
        {
            return (float)((1.0 - Character.bloodMagic.ritual[id].progress) /
                           RitualProgressPerTick(id, remaining) / 50.0);
        }

        private long CalculateMaxAllocation(int id, long remaining)
        {
            var num1 = Character.bloodMagicController.bloodMagics[id].capValue();
            if (remaining > num1)
            {
                return num1;
            }

            var num2 = (long) ((double) num1 / Math.Ceiling((double) num1 / (double) remaining)) + 1L;
            return num2;
        }

        private int EffectiveRebirthTarget()
        {
            // Full autopilot deliberately does not depend on the legacy AutoRebirth
            // checkbox. Its selected plan is the effective reset horizon.
            if (Main.Autopilot != null && Main.Autopilot.CanExecuteSafe
                && Main.Autopilot.Plan != null && Main.Autopilot.Plan.RebirthSeconds > 0)
                return (int)Math.Ceiling(Main.Autopilot.Plan.EffectiveAllocationTarget(Character));
            return RebirthTime;
        }

        private int SecondsRemaining()
        {
            var target = EffectiveRebirthTarget();
            if (target <= 0) return 86400;
            return Math.Max(1, target - (int)Math.Floor(Character.rebirthTime.totalseconds));
        }

        private void ClearRitualAllocations()
        {
            if (Character == null || Character.bloodMagic == null
                || Character.bloodMagic.ritual == null || Character.bloodMagicController == null
                || Character.bloodMagicController.bloodMagics == null)
                return;
            var count = Math.Min(Character.bloodMagic.ritual.Count,
                Character.bloodMagicController.bloodMagics.Length);
            for (var i = 0; i < count; i++)
                if (Character.bloodMagic.ritual[i].magic > 0)
                    Character.bloodMagicController.bloodMagics[i].removeAllMagic();
        }

        private static string FormatGold(double amount)
        {
            if (amount >= 1e12) return (amount / 1e12).ToString("0.###") + "T Gold";
            if (amount >= 1e9) return (amount / 1e9).ToString("0.###") + "B Gold";
            if (amount >= 1e6) return (amount / 1e6).ToString("0.###") + "M Gold";
            if (amount >= 1e3) return (amount / 1e3).ToString("0.###") + "K Gold";
            return amount.ToString("0") + " Gold";
        }
    }
}
