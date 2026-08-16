using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace NGUInjector.Managers
{
    internal static class MoneyPitManager
    {
        internal static void CheckMoneyPit()
        {
            CheckMoneyPit(Main.Settings.MoneyPitThreshold);
        }

        internal static void CheckMoneyPit(double reserve)
        {
            if (!Main.Character.settings.pitUnlocked) return;
            if (Main.Character.pit.pitTime.totalseconds < Main.Character.pitController.currentPitTime()) return;
            if (Main.Character.realGold < reserve) return;
            if (Main.Character.realGold < 1e5) return;

            var gearSwapped = false;
            var diggersSwapped = false;
            if (Main.Settings.MoneyPitLoadout.Length > 0)
            {
                if (!LoadoutManager.TryMoneyPitSwap()) return;
                gearSwapped = true;
            }
            try
            {
                if (Main.Character.realGold >= 1e50 && Main.Settings.ManageMagic && Main.Character.wishes.wishes[4].level > 0)
                {
                    if (!DiggerManager.CanSwap())
                    {
                        Main.LogAction("REJECTED", "Money Pit postponed because digger state is locked");
                        return;
                    }
                    Main.Character.removeMostMagic();
                    for (var i = Main.Character.bloodMagic.ritual.Count - 1; i >= 0; i--)
                        Main.Character.bloodMagicController.bloodMagics[i].cap();

                    DiggerManager.SaveDiggers();
                    diggersSwapped = true;
                    DiggerManager.EquipDiggers(new[] {10});
                }
                DoMoneyPit();
            }
            finally
            {
                if (diggersSwapped)
                    DiggerManager.RestoreDiggers();
                if (gearSwapped)
                {
                    LoadoutManager.RestoreGear();
                    LoadoutManager.ReleaseLock();
                }
            }
        }

        private static void DoMoneyPit()
        {
            var controller = Main.Character.pitController;
            if (!controller.canToss())
                return;
            var timerBefore = Main.Character.pit.pitTime.totalseconds;
            var goldBefore = Main.Character.realGold;
            typeof(PitController).GetMethod("engage", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(controller, null);
            var confirmed = Main.Character.pit.pitTime.totalseconds < timerBefore
                            || Main.Character.realGold < goldBefore;
            Main.LogAction(confirmed ? "REWARD" : "REJECTED",
                confirmed
                    ? "Money Pit: " + controller.pitText.text + " [confirmed by timer/gold delta]"
                    : "Money Pit request produced no timer/gold transition");
        }

        internal static void DoDailySpin()
        {
            if (Main.Character.daily.spinTime.totalseconds < Main.Character.dailyController.targetSpinTime()
                && Main.Character.daily.freeSpins <= 0) return;

            var timerBefore = Main.Character.daily.spinTime.totalseconds;
            var freeSpinsBefore = Main.Character.daily.freeSpins;
            Main.Character.dailyController.startNoBullshitSpin();
            var result = Main.Character.dailyController.outcomeText.text;
            var confirmed = Main.Character.daily.spinTime.totalseconds < timerBefore
                            || Main.Character.daily.freeSpins < freeSpinsBefore;
            Main.LogAction(confirmed ? "REWARD" : "REJECTED",
                confirmed
                    ? "Daily Spin: " + result + " [confirmed by timer/free-spin delta]"
                    : "Daily Spin request produced no timer/free-spin transition");
        }
    }
}
