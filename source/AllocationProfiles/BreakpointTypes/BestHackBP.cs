using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/*
FILE PURPOSE

BestHackBP specializes Hack allocation by selecting the best live permanent Hack target rather
than a fixed index. It inherits native cap/allocation behavior from HackBP. Wish/NGU policy and
Resource 3 budgeting remain outside this selector.
*/
namespace NGUInjector.AllocationProfiles.BreakpointTypes
{
    internal class BestHackBP : HackBP
    {
        protected override bool Unlocked()
        {
            for (int i = 0; i < Character.hacks.hacks.Count; i++)
            {
                if (this.Unlocked(i))
                {
                    return true;
                }
            }
            return false;
        }

        protected bool Unlocked(int id) { 
            return id <= 14 && Character.buttons.hacks.interactable && !AtHardCap(id);
        }

        protected override bool TargetMet()
        {
            for (int i = 0; i < Character.hacks.hacks.Count && i <= 14; i++)
            {
                if (!this.TargetMet(i))
                {
                    return false;
                }
            }
            return true;
        }

        protected bool TargetMet(int id) { 
            return Character.hacksController.hitTarget(id);
        }

        internal override bool Allocate()
        {
            var alloc = MaxAllocation;

            // Exile antennae puzzle: once item 339 has dropped, the native
            // 60-second check awards item 340 only when Hack 13 holds exactly
            // total Resource 3 cap minus one.  This is a progression gate, so it
            // temporarily dominates ordinary hack ROI when the exact allocation
            // is feasible.
            var dropped = Character.inventory.itemList.itemDropped;
            var exileAlloc = Math.Max(0L, Character.totalCapRes3() - 1L);
            if (dropped != null && dropped.Count > 340 && dropped[339] && !dropped[340]
                && exileAlloc > 0 && alloc >= exileAlloc && !AtHardCap(13))
            {
                Character.hacksController.addR3(13, exileAlloc);
                Main.LogAllocation("Exile antennae gate: routed exactly cap - 1 Resource 3 to Hack 13 (" + exileAlloc + ")");
                return true;
            }

            int bestHack = -1;
            double bestScore = 0.0;
            double bestHackTime = double.PositiveInfinity;
            var autopilotAdvance = Main.AutopilotWants(x => x.ManageAllocations);
            for (int i = 0; i < Character.hacks.hacks.Count; i++)
            {
                if (!this.Unlocked(i)) {
                    continue;
                }
                if (this.TargetMet(i) && !Main.Settings.HackAdvance && !autopilotAdvance)
                {
                    continue;
                }
                var time = TimeToNextMilestone(i, alloc);
                var value = MilestoneValue(i);
                var score = time > 0 && !double.IsInfinity(time) ? value / time : 0.0;
                if (score > bestScore)
                {
                    bestHack = i;
                    bestHackTime = time;
                    bestScore = score;
                }
                Main.LogAllocation($"Hack {i}: exact milestone ETA {time}, value {value}, score {score}");
            }
            if (bestHack != -1)
            {
                Main.LogAllocation($"Best hack: {bestHack}, ETA {bestHackTime}, score {bestScore}, allocation {alloc}");
                if ((Main.Settings.HackAdvance || autopilotAdvance)
                    && (Character.hacks.hacks[bestHack].target <= Character.hacks.hacks[bestHack].level
                        || Character.hacks.hacks[bestHack].target <= 0
                        || Character.hacks.hacks[bestHack].target - Character.hacks.hacks[bestHack].level < 20))
                {
                    Character.hacksController.setToNextMilestone(bestHack);
                }                        
                Character.hacksController.addR3(bestHack, (long)alloc);
            }
            return true;
        }

        private bool AtHardCap(int id)
        {
            if (id < 0 || id >= Character.hacks.hacks.Count)
                return true;
            // The native updater refuses to advance at this public cap. Its 1e38
            // boundary and two-stage approximation are intentionally authoritative;
            // a custom float.MaxValue solver can allocate forever to a frozen bar.
            return Character.hacks.hacks[id].level >= Character.hacksController.hardCapLevel(id);
        }

        private double TimeToNextMilestone(int id, float r3)
        {
            if (r3 <= 0)
                return double.PositiveInfinity;
            var speed = r3 * Character.totalRes3Power() * Character.hacksController.totalHackSpeedBonus() * 50.0
                        / Character.hacksController.properties[id].baseDivider;
            if (speed <= 0)
                return double.PositiveInfinity;
            var hack = Character.hacks.hacks[id];
            var levels = Math.Max(1L, Character.hacksController.levelsToNextMilestone(id));
            var target = hack.level + levels;
            var total = 0.0;
            for (var level = hack.level; level < target; level++)
            {
                var divider = Math.Pow(1.0078, level) * (level + 1.0);
                total += divider / speed * (level == hack.level ? Math.Max(0.0, 1.0 - hack.progress) : 1.0);
            }
            return total;
        }

        private double MilestoneValue(int id)
        {
            var hack = Character.hacks.hacks[id];
            var properties = Character.hacksController.properties[id];
            var threshold = Math.Max(1L, Character.hacksController.milestoneThreshold(id));
            var target = hack.level + Math.Max(1L, Character.hacksController.levelsToNextMilestone(id));
            var before = (1.0 + hack.level * properties.baseEffectPerLevel)
                         * Math.Pow(properties.milestoneEffect, hack.level / threshold);
            var after = (1.0 + target * properties.baseEffectPerLevel)
                        * Math.Pow(properties.milestoneEffect, target / threshold);
            var relative = before > 0 ? after / before - 1.0 : after;

            // Convert the exact effect delta into current progression relevance.
            var relevance = 1.0;
            if (id == 0) relevance = 4.0; // Fight-boss stats
            else if (id == 1) relevance = 4.0; // Adventure/Titan stats
            else if (id == 13) relevance = 3.0; // Compounds all future hacks
            else if (id == 14 && Character.wishes.wishesOn) relevance = 2.5;
            else if (id == 2 && Character.buttons.brokenTimeMachine.interactable) relevance = 2.0;
            return Math.Max(0.0, relative) * relevance;
        }

        protected override bool CorrectResourceType()
        {
            return Type == ResourceType.R3;
        }

        public float time(int id, float r3)
        {
            double num = (double)((float)r3 * Character.totalRes3Power() * Character.hacksController.totalHackSpeedBonus() / (Character.hacksController.properties[id].baseDivider * this.levelDivider(id)));
            if (num >= 3.4028234663852886E+38)
            {
                return float.MaxValue;
            }
            if (num <= -3.4028234663852886E+38)
            {
                return 0f;
            }
            return 1f / (float)num / 50f;
        }

        public float levelDivider(int id)
        {
            long target = Character.hacks.hacks[id].level + Character.hacksController.levelsToNextMilestone(id);
            double num = Math.Pow(1.0078, (double)target) * (double)(target + 1L);
            if (num > 3.4028234663852886E+38)
            {
                return float.MaxValue;
            }
            return (float)num;
        }
    }
}
