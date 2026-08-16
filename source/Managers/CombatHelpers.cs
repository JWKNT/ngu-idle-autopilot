using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine.UI;

namespace NGUInjector.Managers
{
    internal static class CombatHelpers
    {
        internal static bool CanNukeCurrentBoss(Character c)
        {
            return c != null && c.bossID <= 300 && (c.bossID < c.highestBoss || c.bossID >= 124)
                   && c.attack / 5.0 > c.bossDefense && c.defense / 5.0 > c.bossAttack;
        }

        internal static bool CanWinCurrentBoss(Character c, out double killSeconds)
        {
            killSeconds = double.PositiveInfinity;
            if (c == null || c.bossCurHP <= 0 || c.curHP <= 0)
                return false;
            double survivalSeconds;
            var viable = EvaluateFixedBossFight(c, c.attack, c.defense, c.curHP, c.bossCurHP,
                out killSeconds, out survivalSeconds);
            // "Eventually survivable" is not the same as progression-optimal. Holding a
            // boss fight for hours blocks rebirth and every later boss check. Wait for
            // allocations to improve instead unless the exact expected fight is short.
            if (killSeconds > 120.0)
                return false;
            return viable;
        }

        internal static bool EvaluateFixedBossFight(Character c, double attack, double defense,
            double playerHp, double bossHp, out double killSeconds, out double survivalSeconds)
        {
            killSeconds = double.PositiveInfinity;
            survivalSeconds = double.PositiveInfinity;
            if (c == null || playerHp <= 0 || bossHp <= 0) return false;

            // Native order is boss regen/cap, incoming damage and immediate player
            // death, then outgoing damage and boss death. Character.updateHP is a
            // separate 0.02-second callback; conservatively do not credit it before
            // the first hit, then use its exact per-tick amount thereafter.
            var outgoingDamage = 0.02 * Math.Max(0.0, attack - c.bossDefense);
            var preHitBossHp = Math.Min(c.bossMaxHP, bossHp + c.bossRegen);
            long killTick;
            if (outgoingDamage <= 0)
                killTick = long.MaxValue;
            else if (outgoingDamage >= preHitBossHp)
                killTick = 1;
            else
            {
                var netBossDamage = outgoingDamage - c.bossRegen;
                killTick = netBossDamage <= 0 ? long.MaxValue
                    : 1L + (long)Math.Ceiling((preHitBossHp - outgoingDamage) / netBossDamage);
            }

            var incomingDamage = 0.02 * Math.Max(0.0, c.bossAttack - defense);
            var playerRegen = 0.001 + 0.001 * defense;
            long deathTick;
            if (incomingDamage <= 0)
                deathTick = long.MaxValue;
            else if (incomingDamage >= playerHp)
                deathTick = 1;
            else
            {
                var netPlayerDamage = incomingDamage - playerRegen;
                deathTick = netPlayerDamage <= 0 ? long.MaxValue
                    : 1L + (long)Math.Ceiling((playerHp - incomingDamage) / netPlayerDamage);
            }

            if (killTick != long.MaxValue) killSeconds = killTick * 0.02;
            if (deathTick != long.MaxValue) survivalSeconds = deathTick * 0.02;
            return killTick < deathTick;
        }

        internal static bool ExecuteVerifiedMove(Button button, Action execute, string description)
        {
            if (!Main.IsAutomationReady || button == null || !button.IsInteractable())
                return false;
            var beforeMoveTimer = Main.PlayerController.moveTimer;
            var beforeCanUseMove = Main.PlayerController.canUseMove;
            execute();
            var confirmed = (beforeCanUseMove && !Main.PlayerController.canUseMove)
                            || Main.PlayerController.moveTimer > beforeMoveTimer
                            || !button.IsInteractable();
            Main.LogAction(confirmed ? "COMBAT" : "REJECTED",
                description + (confirmed ? " [confirmed by move state]" : " [controller produced no state transition]"));
            return confirmed;
        }

        internal static bool CastCharge()
        {
            var move = Main.Character.adventureController.chargeMove;
            return ExecuteVerifiedMove(move.button, move.doMove, "Charge");
        }

        internal static bool CastParry()
        {
            var move = Main.Character.adventureController.parryMove;
            return ExecuteVerifiedMove(move.button, move.doMove, "Parry");
        }

        internal static bool ChargeReady()
        { 
            return Main.Character.adventureController.chargeMove.button.IsInteractable();
        }

        internal static bool ParryReady()
        {
            return Main.Character.adventureController.parryMove.button.IsInteractable();
        }

        internal static float GetChargeCooldown()
        {
            var ua = Main.Character.adventureController.chargeMove;
            var type = ua.GetType().GetField("chargeTimer",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var val = type?.GetValue(ua);
            if (val == null)
            {
                return 0;
            }

            return (float)val / Main.Character.chargeCooldown();
        }

        internal static bool HealReady()
        {
            return Main.Character.adventureController.healMove.button.IsInteractable();
        }

        internal static bool CastHeal()
        {
            var move = Main.Character.adventureController.healMove;
            return ExecuteVerifiedMove(move.button, move.doMove, "Heal");
        }

        internal static bool CastHyperRegen()
        {
            var move = Main.Character.adventureController.hyperRegenMove;
            return ExecuteVerifiedMove(move.button, move.doMove, "Hyper Regen");
        }

        internal static bool ParryActive()
        {
            return Main.PlayerController.isParrying;
        }

        internal static bool ChargeActive()
        {
            return Main.PlayerController.chargeFactor > 1.05;
        }

        internal static bool UltimateBuffActive()
        {
            return Main.PlayerController.ultimateBuffTime > 0 && Main.PlayerController.ultimateBuffTime < Main.Character.ultimateBuffDuration();
        }

        internal static bool DefenseBuffActive()
        {
            return Main.PlayerController.defenseBuffTime > 0 && Main.PlayerController.defenseBuffTime < Main.Character.defenseBuffDuration();
        }

        internal static float GetUltimateAttackCooldown()
        {
            var ua = Main.Character.adventureController.ultimateAttackMove;
            var type = ua.GetType().GetField("ultimateAttackTimer",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var val = type?.GetValue(ua);
            if (val == null)
            {
                return 0;
            }

            return (float)val / Main.Character.ultimateAttackCooldown();
        }

        internal static bool CastUltimateBuff()
        {
            var move = Main.Character.adventureController.ultimateBuffMove;
            return ExecuteVerifiedMove(move.button, move.doMove, "Ultimate Buff");
        }

        internal static bool CastMegaBuff()
        {
            var move = Main.Character.adventureController.megaBuffMove;
            return ExecuteVerifiedMove(move.button, move.doMove, "Mega Buff");
        }

        internal static bool CastOffensiveBuff()
        {
            var move = Main.Character.adventureController.offenseBuffMove;
            return ExecuteVerifiedMove(move.button, move.doMove, "Offensive Buff");
        }

        internal static bool BlockActive()
        {
            return Main.PlayerController.isBlocking;
        }

        internal static bool CastBlock()
        {
            var move = Main.Character.adventureController.blockMove;
            return ExecuteVerifiedMove(move.button, move.doMove, "Block");
        }

        internal static bool CastDefensiveBuff()
        {
            var move = Main.Character.adventureController.defenseBuffMove;
            return ExecuteVerifiedMove(move.button, move.doMove, "Defensive Buff");
        }

        internal static bool CastParalyze(AI ai, EnemyAI eai)
        {
            if (!Main.Character.adventureController.paralyzeMove.button.IsInteractable())
            {
                return false;
            }

            if (ai == AI.charger && eai.GetPV<int>("chargeCooldown") == 0)
            {
                var move = Main.Character.adventureController.paralyzeMove;
                return ExecuteVerifiedMove(move.button, move.doMove, "Paralyze charger");
            }

            if (ai == AI.rapid && eai.GetPV<int>("rapidEffect") < 5)
            {
                var move = Main.Character.adventureController.paralyzeMove;
                return ExecuteVerifiedMove(move.button, move.doMove, "Paralyze rapid enemy");
            }

            if (ai != AI.rapid && ai != AI.charger)
            {
                var move = Main.Character.adventureController.paralyzeMove;
                return ExecuteVerifiedMove(move.button, move.doMove, "Paralyze");
            }
            return false;
        }

        internal static bool UltimateAttackReady()
        {
            return Main.Character.adventureController.ultimateAttackMove.button.IsInteractable();
        }

        internal static bool PierceReady()
        {
            return Main.Character.adventureController.pierceMove.button.IsInteractable();
        }

        internal static bool ChargeUnlocked()
        {
            return Main.Character.training.defenseTraining[3] >= 20000L;
        }

        internal static bool ParryUnlocked()
        {
            return Main.Character.training.attackTraining[2] >= 15000L;
        }

        internal static bool UltimateBuffUnlocked()
        {
            return Main.Character.training.defenseTraining[4] >= 25000L;
        }

        internal static bool UltimateBuffReady()
        {
            return Main.Character.adventureController.ultimateBuffMove.button.IsInteractable();
        }

        internal static bool DefensiveBuffUnlocked()
        {
            return Main.Character.training.defenseTraining[0] >= 5000L;
        }

        internal static bool DefensiveBuffReady()
        {
            return Main.Character.adventureController.defenseBuffMove.button.IsInteractable();
        }

        internal static bool MegaBuffUnlocked()
        {
            return Main.Character.training.defenseTraining[4] >= 25000L && Main.Character.wishes.wishes[8].level >= 1;
        }

        internal static bool MegaBuffReady()
        {
            return Main.Character.adventureController.megaBuffMove.button.IsInteractable();
        }

        internal static bool OhShitUnlocked()
        {
            return Main.Character.wishes.wishes[58].level >= 1 && Main.Character.allChallenges.hasParalyze() &&
                   Main.Character.training.defenseTraining[1] >= 10000L && Main.Character.settings.hasHyperRegen;
        }

        internal static bool OhShitReady()
        {
            return Main.Character.adventureController.ohShitMove.button.IsInteractable();
        }

        internal static bool CastOhShit()
        {
            var move = Main.Character.adventureController.ohShitMove;
            return ExecuteVerifiedMove(move.button, move.doMove, "Emergency skill");
        }
    }
}
