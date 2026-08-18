using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NGUInjector.Autopilot;

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
        internal static double LastBaselineGold { get; private set; }
        internal static double LastCommittedGold { get; private set; }
        internal static double LastGoldShortfall { get; private set; }

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
            var remaining = (int)Math.Floor(plan.EffectiveAllocationTarget(Character)
                                            - Character.rebirthTime.totalseconds);
            if (remaining <= 0)
            {
                LastHorizonDecision = "Blocked: the selected rebirth checkpoint has arrived; additional reset-local gold cannot complete a sink";
                return false;
            }

            var evaluation = ResourceHorizonModel.EvaluateGold(Character, remaining);
            LastBaselineGold = evaluation.BaselineAtRebirth;
            LastCommittedGold = evaluation.CommittedSpend;
            LastGoldShortfall = evaluation.Shortfall;
            LastHorizonDecision = evaluation.Decision;
            if (!evaluation.TimeMachineUseful)
                return false;
            var marginalGps = Type == ResourceType.Energy
                ? evaluation.NextSpeedLevelIncrement : evaluation.NextGoldLevelIncrement;
            if (marginalGps <= 0)
            {
                LastHorizonDecision = "Blocked: the next Time Machine "
                                      + (Type == ResourceType.Energy ? "speed" : "gold-multiplier")
                                      + " level has zero native marginal GPS at the current discrete level";
                return false;
            }

            /*
            COMPLETION GATE

            Time Machine levels and partial bar progress reset. A Gold shortfall alone therefore
            cannot justify an allocation: at least the very next level must finish inside the real
            remaining horizon. Use the exact native hypothetical-rate overload and the profile's
            actual cap budget. This prevents a late-run 40% reservation from doing literally
            nothing while a Blood ritual (or another persistent sink) can still use the resource.
            */
            double completionSeconds;
            if (!NextLevelCompletesBeforeRebirth(remaining, out completionSeconds))
            {
                var resource = Type == ResourceType.Energy ? "Energy" : "Magic";
                LastHorizonDecision = "Blocked: the next Time Machine "
                                      + (Type == ResourceType.Energy ? "speed" : "gold-multiplier")
                                      + " level needs " + FormatDuration(completionSeconds)
                                      + " at the profile's " + resource + " budget, beyond the "
                                      + remaining + "s rebirth horizon; partial progress resets";
                return false;
            }
            LastHorizonDecision += "; next Time Machine level completes in "
                                   + FormatDuration(completionSeconds) + " and adds "
                                   + marginalGps.ToString("0.###") + " native GPS";
            return true;
        }

        private bool NextLevelCompletesBeforeRebirth(int remainingSeconds, out double seconds)
        {
            var total = Type == ResourceType.Energy ? Character.curEnergy : Character.magic.curMagic;
            var budget = IsCap
                ? (long)Math.Ceiling(Math.Max(0L, total) * CapPercent)
                : Math.Max(0L, total);
            budget = Math.Max(1L, Math.Min(Math.Max(0L, total), budget));
            var progress = Type == ResourceType.Energy
                ? Character.machine.speedProgress : Character.machine.goldMultiProgress;
            var perTick = Type == ResourceType.Energy
                ? Character.timeMachineController.speedProgressPerTick(budget)
                : Character.timeMachineController.goldMultiProgressPerTick(budget);
            if (perTick <= 0)
            {
                seconds = double.PositiveInfinity;
                return false;
            }
            var remainingProgress = Math.Max(0.0, 1.0 - Math.Max(0.0, progress));
            seconds = Math.Ceiling(remainingProgress / perTick) / 50.0;
            return seconds <= remainingSeconds;
        }

        private static string FormatDuration(double seconds)
        {
            if (double.IsInfinity(seconds) || double.IsNaN(seconds)) return "an unbounded time";
            if (seconds >= 3600) return (seconds / 3600.0).ToString("0.##") + "h";
            if (seconds >= 60) return (seconds / 60.0).ToString("0.##") + "m";
            return seconds.ToString("0.##") + "s";
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
            if (toAllocate <= 0 || !SetInput(toAllocate))
                return;
            Character.timeMachineController.addEnergy();
        }

        private void AllocateMagic()
        {
            var toAllocate = CalculateTMMagicCap();
            if (toAllocate <= 0 || !SetInput(toAllocate))
                return;
            Character.timeMachineController.addMagic();
        }

        protected override bool CorrectResourceType()
        {
            return Type == ResourceType.Energy || Type == ResourceType.Magic;
        }

        private long CalculateTMMagicCap()
        {
            var calcA = CalculateMagicTM(500);
            if (calcA.PPT < 1)
            {
                var calcB = CalculateMagicTM(calcA.GetOffset());
                return calcB.Num;
            }

            return calcA.Num;
        }

        private long CalculateTMEnergyCap()
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
            var num = ExactResourceAllocator.CapAtTickBoundary(num4, MaxAllocation,
                Character.idleEnergy);

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
            var num = ExactResourceAllocator.CapAtTickBoundary(num4, MaxAllocation,
                Character.magic.idleMagic);
            ret.Num = num;
            ret.PPT = (double)num / num4;
            return ret;
        }


        #endregion

    }
}
