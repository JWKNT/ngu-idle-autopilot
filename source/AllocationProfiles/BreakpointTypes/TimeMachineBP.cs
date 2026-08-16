using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/*
FILE PURPOSE

TimeMachineBP allocates Energy/Magic only when reset-local gold generation can repay its resource
cost before the selected rebirth. It publishes the horizon decision for telemetry and uses native
Time Machine caps. Do not fund it merely because it is unlocked; gold and TM levels reset.
*/
namespace NGUInjector.AllocationProfiles.BreakpointTypes
{
    internal class TimeMachineBP : BaseBreakpoint
    {
        internal static string LastHorizonDecision { get; private set; }
            = "Time Machine reset-horizon value has not been evaluated yet";

        protected override bool Unlocked()
        {
            return Character.buttons.brokenTimeMachine.interactable && !Character.challenges.timeMachineChallenge.inChallenge;
        }

        protected override bool TargetMet()
        {
            var target = Type == ResourceType.Energy ? Character.machine.speedTarget : Character.machine.multiTarget;
            var level = Type == ResourceType.Energy ? Character.machine.levelSpeed : Character.machine.levelGoldMulti;
            if (target == -1 || target > 0 && level >= target)
                return true;
            if (!HasPreRebirthGoldValue())
                return true;
            return false;
        }

        private bool HasPreRebirthGoldValue()
        {
            var plan = Main.Autopilot == null ? null : Main.Autopilot.Plan;
            if (plan == null || plan.RebirthSeconds <= 0)
            {
                LastHorizonDecision = "Allowed: no finite rebirth horizon is available, so reset-local value cannot yet be bounded";
                return true;
            }
            var remaining = plan.RebirthSeconds - (int)Math.Floor(Character.rebirthTime.totalseconds);
            if (remaining <= 0)
            {
                LastHorizonDecision = "Blocked: the selected rebirth checkpoint has arrived; additional reset-local gold cannot complete a sink";
                return false;
            }

            // An allocated Augment/Upgrade can charge again on a later completion;
            // preserving its working capital is a concrete pre-reset use of gold.
            if (Character.augments != null && Character.augments.augs != null
                && Character.augments.augs.Any(x => x.augEnergy > 0 || x.upgradeEnergy > 0))
            {
                LastHorizonDecision = "Allowed: active Augment/Upgrade work can consume gold before the selected rebirth";
                return true;
            }

            if (Character.settings.pitUnlocked && Character.pitController != null)
            {
                var pitWait = Character.pitController.currentPitTime() - Character.pit.pitTime.totalseconds;
                if (pitWait <= remaining)
                {
                    LastHorizonDecision = "Allowed: Money Pit becomes available before rebirth, converting reset-local gold into persistent rewards";
                    return true;
                }
            }

            if (Character.buttons.bloodMagic.interactable && Character.bloodMagicController != null
                && Character.bloodMagicController.ritualsUnlocked() > 0)
            {
                LastHorizonDecision = "Allowed: unlocked Blood Magic rituals convert gold into blood/spells before rebirth";
                return true;
            }

            if (Character.allDiggers != null && Character.diggers != null
                && Character.diggers.diggers != null)
            {
                var projectedBaselineGold = Character.realGold
                                            + Math.Max(0.0, Character.grossGoldPerSecond()) * remaining;
                for (var i = 0; i < Character.diggers.diggers.Count; i++)
                {
                    if (Character.diggers.diggers[i].maxLevel >= Character.allDiggers.hardCapLevel(i))
                        continue;
                    var cost = Character.allDiggers.upgradeCost(i);
                    if (cost > 0 && cost <= projectedBaselineGold)
                    {
                        LastHorizonDecision = "Allowed: projected pre-reset gold reaches a permanent Digger max-level upgrade";
                        return true;
                    }
                }
            }

            LastHorizonDecision = "Blocked: no Augment charge, Money Pit toss, or reachable permanent Digger upgrade exists before rebirth; Time Machine levels and unspent gold would reset";
            return false;
        }

        internal override bool Allocate()
        {
            if (Type == ResourceType.Energy)
            {
                AllocateEnergy();
            }
            else
            {
                AllocateMagic();
            }
            return true;
        }

        private void AllocateEnergy()
        {
            var toAllocate = CalculateTMEnergyCap();
            SetInput(toAllocate);
            Character.timeMachineController.addEnergy();
        }

        private void AllocateMagic()
        {
            var toAllocate = CalculateTMMagicCap();
            SetInput(toAllocate);
            Character.timeMachineController.addMagic();
        }

        protected override bool CorrectResourceType()
        {
            return Type == ResourceType.Energy || Type == ResourceType.Magic;
        }

        private float CalculateTMMagicCap()
        {
            var calcA = CalculateMagicTM(500);
            if (calcA.PPT < 1)
            {
                var calcB = CalculateMagicTM(calcA.GetOffset());
                return calcB.Num;
            }

            return calcA.Num;
        }

        private float CalculateTMEnergyCap()
        {
            var calcA = CalculateEnergyTM(500);
            if (calcA.PPT < 1)
            {
                var calcB = CalculateEnergyTM(calcA.GetOffset());
                return calcB.Num;
            }

            return calcA.Num;
        }

        #region Hidden
        private CapCalc CalculateEnergyTM(int offset)
        {
            var ret = new CapCalc
            {
                Num = 0,
                PPT = 1
            };
            var formula = 50000 * Character.timeMachineController.baseSpeedDivider() * (1f + Character.machine.levelSpeed + offset) / (
                Character.totalEnergyPower() * Character.hacksController.totalTMSpeedBonus() *
                Character.allChallenges.timeMachineChallenge.TMSpeedBonus() *
                Character.cardsController.getBonus(cardBonus.TMSpeed));

            if (Character.settings.rebirthDifficulty >= difficulty.sadistic)
            {
                formula *= Character.timeMachineController.sadisticDivider();
            }

            if (formula >= Character.hardCap())
                formula = Character.hardCap();

            var num4 = formula <= 1.0 ? 1L : (long)formula;
            var num = (long)(num4 / (long)Math.Ceiling(num4 / (double)MaxAllocation) * 1.00000202655792);
            if (num + 1L <= long.MaxValue)
                ++num;
            if (num > Character.idleEnergy)
                num = Character.idleEnergy;
            if (num < 0L)
                num = 0L;

            ret.Num = num;
            ret.PPT = (double)num / num4;
            return ret;
        }

        private CapCalc CalculateMagicTM(int offset)
        {
            var ret = new CapCalc
            {
                Num = 0,
                PPT = 1
            };
            var formula = 50000 * Character.timeMachineController.baseGoldMultiDivider() *
                (1f + Character.machine.levelGoldMulti + offset) / (
                    Character.totalMagicPower() * Character.hacksController.totalTMSpeedBonus() *
                    Character.allChallenges.timeMachineChallenge.TMSpeedBonus() *
                    Character.cardsController.getBonus(cardBonus.TMSpeed));

            if (Character.settings.rebirthDifficulty >= difficulty.sadistic)
            {
                formula *= Character.timeMachineController.sadisticDivider();
            }

            if (formula >= Character.hardCap())
                formula = Character.hardCap();


            var num4 = formula <= 1.0 ? 1L : (long)formula;
            var num = (long)(num4 / (long)Math.Ceiling(num4 / (double)MaxAllocation) * 1.00000202655792);
            if (num + 1L <= long.MaxValue)
                ++num;
            if (num > Character.magic.idleMagic)
                num = Character.magic.idleMagic;
            if (num < 0L)
                num = 0L;
            ret.Num = num;
            ret.PPT = (double)num / num4;
            return ret;
        }


        #endregion

    }
}
