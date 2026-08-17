using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using static NGUInjector.Main;
using static NGUInjector.Managers.CombatHelpers;

/*
FILE PURPOSE

CombatManager executes Adventure movement and active/idle skill rotations through native
controllers, tracks observed fight timing, reports confirmed major-unlock attempt outcomes, and
exposes recovery state. It must not fabricate kills or abandon special enemies merely to swap gear.
Zone policy comes from AutopilotManager; this file owns tactical action sequencing and confirmation.
*/
namespace NGUInjector.Managers
{
    internal class CombatManager
    {
        private readonly Character _character;
        private readonly PlayerController _pc;
        private bool _isFighting = false;
        private float _fightTimer = 0;
        private string _enemyName;
        private float _fightStartHP;
        private int _fightZone = -1;
        private bool _fightWasTitan;
        private float _expectedFightDamage;
        private float _recoveryTargetHP;

        internal string RecoveryReason { get; private set; } = string.Empty;

        internal float RecoveryTargetHP
        {
            get { return _recoveryTargetHP; }
        }

        internal int RecoveryEtaSeconds
        {
            get
            {
                if (string.IsNullOrEmpty(RecoveryReason) || _character.adventure.zone != -1)
                    return 0;
                var missing = Math.Max(0, _recoveryTargetHP - _character.adventure.curHP);
                var safeZoneRegen = Math.Max(0.01, _character.totalAdvHPRegen() * 5.0);
                if (_character.inventory.itemList.GRBComplete) safeZoneRegen *= 2.0;
                if (_character.adventure.autoattacking) safeZoneRegen *= 1.2;
                if (_pc.hyperRegenTime >= 0) safeZoneRegen *= 5.0;
                return (int)Math.Ceiling(missing / safeZoneRegen);
            }
        }

        public CombatManager()
        {
            _character = Main.Character;
            _pc = Main.PlayerController;
        }

        internal void UpdateFightTimer(float diff)
        {
            _fightTimer += diff;
        }

        bool HasFullHP()
        {
            return Math.Abs(_character.totalAdvHP() - _character.adventure.curHP) < 5;
        }

        float GetHPPercentage()
        {
            return _character.adventure.curHP / _character.totalAdvHP();
        }

        private float RequiredHPForNextFight()
        {
            var maxHP = _character.totalAdvHP();
            // A routine encounter does not require a full heal.  Use observed damage
            // from this exact zone/run, with enough margin for the game's 0.8-1.2
            // damage roll and one delayed player input.  Before the first sample,
            // 55% HP is a conservative early-game starting point.
            if (_expectedFightDamage <= 0)
                return maxHP * .55f;
            return Math.Min(maxHP, Math.Max(maxHP * .30f,
                _expectedFightDamage * 1.30f + maxHP * .08f));
        }

        private bool NeedsRecoveryForNextFight()
        {
            _recoveryTargetHP = RequiredHPForNextFight();
            if (_character.adventure.curHP + 1 >= _recoveryTargetHP)
            {
                RecoveryReason = string.Empty;
                return false;
            }
            RecoveryReason = "Recovering only to the measured next-fight safety threshold";
            return true;
        }

        private void DoCombat(bool fastCombat)
        {
            if (!_pc.moveCheck())
                return;

            if (Main.PlayerController.moveTimer > 0)
                return;

            if (!fastCombat)
            {
                if (CombatBuffs())
                    return;
            }

            CombatAttacks(fastCombat);
        }

        private bool CombatBuffs()
        {
            var ac = _character.adventureController;
            var ai = ac.currentEnemy.AI;
            var eai = ac.enemyAI;

            if (ai == AI.charger && eai.GetPV<int>("chargeCooldown") >= 3)
            {
                if (ac.blockMove.button.IsInteractable() && !_pc.isParrying)
                {
                    return ExecuteVerifiedMove(ac.blockMove.button, ac.blockMove.doMove, "Block — charger reaction");
                }

                if (ac.parryMove.button.IsInteractable() && !_pc.isBlocking && !_pc.isParrying)
                {
                    return ExecuteVerifiedMove(ac.parryMove.button, ac.parryMove.doMove, "Parry — charger reaction");
                }
            }

            if (ai == AI.rapid && eai.GetPV<int>("rapidEffect") >= 6)
            {
                if (ac.blockMove.button.IsInteractable())
                {
                    return ExecuteVerifiedMove(ac.blockMove.button, ac.blockMove.doMove, "Block — rapid-enemy reaction");
                }
            }

            if (ai == AI.exploder && ac.currentEnemy.attackRate - eai.GetPV<float>("enemyAttackTimer") < 1)
            {
                if (ac.blockMove.button.IsInteractable())
                {
                    return ExecuteVerifiedMove(ac.blockMove.button, ac.blockMove.doMove, "Block — exploder reaction");
                }
            }

            if (ac.currentEnemy.curHP / ac.currentEnemy.maxHP < .2)
            {
                return false;
            }

            if (OhShitUnlocked() && GetHPPercentage() < .5 && OhShitReady())
            {
                if (CastOhShit())
                {
                    return true;
                }
            }

            if (GetHPPercentage() < .5)
            {
                if (CastHeal())
                {
                    return true;
                }
            }

            if (GetHPPercentage() < .5 && !HealReady())
            {
                if (CastHyperRegen())
                {
                    return true;
                }
            }

            if (CastMegaBuff())
            {
                return true;
            }

            if (!MegaBuffUnlocked())
            {
                if (!DefenseBuffActive())
                {
                    if (CastUltimateBuff())
                    {
                        return true;
                    }
                }

                if (UltimateBuffActive())
                {
                    if (CastOffensiveBuff())
                        return true;
                }

                if (GetHPPercentage() < .75 && !UltimateBuffActive() && !BlockActive())
                {
                    if (CastDefensiveBuff())
                        return true;
                }
            }

            if (ai != AI.charger && ai != AI.rapid && ai != AI.exploder && (Settings.MoreBlockParry || !UltimateBuffActive() && !DefenseBuffActive()))
            {
                if (!ParryActive() && !BlockActive())
                {
                    if (CastBlock())
                        return true;
                }

                if (!BlockActive() && !ParryActive())
                {
                    if (CastParry())
                        return true;
                }
            }

            if (_pc.isBlocking || _pc.isParrying)
            {
                return false;
            }

            if (CastParalyze(ai, eai))
                return true;


            if (ChargeReady())
            {
                if (UltimateAttackReady())
                {
                    if (CastCharge())
                        return true;
                }

                if (GetUltimateAttackCooldown() > .45 && PierceReady())
                {
                    if (CastCharge())
                        return true;
                }
            }

            return false;
        }

        //private bool ParalyzeBoss()
        //{
        //    var ac = _character.adventureController;
        //    var ai = ac.currentEnemy.AI;
        //    var eai = ac.enemyAI;

        //    if (!ac.paralyzeMove.button.IsInteractable())
        //        return false;

        //    if (GetHPPercentage() < .2)
        //        return false;

        //    if (UltimateBuffActive())
        //        return false;

        //    if (ai == AI.charger && eai.GetPV<int>("chargeCooldown") == 0)
        //    {
        //        ac.paralyzeMove.doMove();
        //        return true;
        //    }

        //    if (ai == AI.rapid && eai.GetPV<int>("rapidEffect") < 5)
        //    {
        //        ac.paralyzeMove.doMove();
        //        return true;
        //    }

        //    if (ai != AI.rapid && ai != AI.charger)
        //    {
        //        ac.paralyzeMove.doMove();
        //        return true;
        //    }

        //    return false;
        //}

        private void CombatAttacks(bool fastCombat)
        {
            var ac = _character.adventureController;

            if (ac.ultimateAttackMove.button.IsInteractable())
            {
                var description = ChargeActive() ? "Ultimate Attack — Charge active"
                    : GetChargeCooldown() > .45 ? "Ultimate Attack — before cooldown reset"
                    : "Ultimate Attack";
                if ((fastCombat || ChargeActive() || GetChargeCooldown() > .45)
                    && ExecuteVerifiedMove(ac.ultimateAttackMove.button, ac.ultimateAttackMove.doMove, description))
                    return;
            }

            if (ac.pierceMove.button.IsInteractable())
            {
                ExecuteVerifiedMove(ac.pierceMove.button, ac.pierceMove.doMove, "Piercing Attack");
                return;
            }

            if (ac.strongAttackMove.button.IsInteractable())
            {
                ExecuteVerifiedMove(ac.strongAttackMove.button, ac.strongAttackMove.doMove, "Strong Attack");
                return;
            }

            if (ac.regularAttackMove.button.IsInteractable())
            {
                ExecuteVerifiedMove(ac.regularAttackMove.button, ac.regularAttackMove.doMove, "Regular Attack");
                return;
            }
        }

        internal static bool IsZoneUnlocked(int zone)
        {
            return zone <= ZoneHelpers.GetMaxReachableZone(true);
        }

        internal void MoveToZone(int zone)
        {
            if (!Main.IsAutomationReady || _character.adventure.zone == zone)
                return;
            var before = _character.adventure.zone;
            _character.adventureController.zoneSelector.changeZone(zone);
            var confirmed = _character.adventure.zone == zone;
            Main.LogAction(confirmed ? "ZONE" : "REJECTED",
                confirmed
                    ? "Changed Adventure zone " + GameNames.Zone(_character, before) + " -> "
                      + GameNames.Zone(_character, zone) + " [confirmed by game state]"
                    : "Adventure zone request " + GameNames.Zone(_character, before) + " -> "
                      + GameNames.Zone(_character, zone) + " was rejected");
        }

        internal void IdleZone(int zone, bool bossOnly, bool recoverHealth, bool? beastMode = null)
        {
            if (zone == -1)
            {
                if (_character.adventure.zone != -1)
                {
                    MoveToZone(-1);
                    return;
                }
            }
            //Enable idle attack if its not on
            if (!_character.adventure.autoattacking)
            {
                _character.adventureController.idleAttackMove.setToggle();
                return;
            }

            var useBeastMode = (beastMode ?? Settings.BeastMode) && _character.adventureController.hasBeastMode();
            //Turn on beast mode depending
            if (_character.adventure.beastModeOn && !useBeastMode && _character.adventureController.beastModeMove.button.interactable)
            {
                _character.adventureController.beastModeMove.doMove();
                return;
            }

            //Turn off beast mode depending
            if (!_character.adventure.beastModeOn && useBeastMode &&
                _character.adventureController.beastModeMove.button.interactable)
            {
                _character.adventureController.beastModeMove.doMove();
                return;
            }

            if (_character.adventure.zone == -1 && recoverHealth && NeedsRecoveryForNextFight())
                return;

            //Check if we're in not in the right zone and not in safe zone, if not move to safe zone first
            if (_character.adventure.zone != zone && _character.adventure.zone != -1)
            {
                MoveToZone(-1);
                return;
            }

            //Move to the zone
            if (_character.adventure.zone != zone)
            {
                MoveToZone(zone);
                return;
            }

            //Wait for an enemy to spawn
            if (_character.adventureController.currentEnemy == null)
                return;

            if (zone < 1000 && Settings.BlacklistedBosses.Contains(_character.adventureController.currentEnemy.spriteID))
            {
                MoveToZone(-1);
                MoveToZone(zone);
                return;
            }

            //If we only want boss enemies
            if (bossOnly)
            {
                //Check the type of the enemy
                var ec = _character.adventureController.currentEnemy.enemyType;
                //If its not a boss, move back to safe zone. Next loop will put us back in the right zone.
                if (ec != enemyType.boss && !ec.ToString().Contains("bigBoss"))
                {
                    MoveToZone(-1);
                }
            }
        }

        internal void ManualZone(int zone, bool bossOnly, bool recoverHealth, bool precastBuffs, bool fastCombat, bool beastMode)
        {
            if (zone == -1)
            {
                if (_character.adventure.zone != -1)
                {
                    MoveToZone(-1);
                    return;
                }
            }

            //Start by turning off auto attack if its on unless we can only idle attack
            if (!_character.adventure.autoattacking)
            {
                if (_character.training.attackTraining[1] == 0)
                {
                    _character.adventureController.idleAttackMove.setToggle();
                    return;
                }
            }
            else
            {
                if (_character.training.attackTraining[1] > 0)
                {
                    _character.adventureController.idleAttackMove.setToggle();
                }
            }

            var useBeastMode = beastMode && _character.adventureController.hasBeastMode();
            if (_character.adventure.beastModeOn && !useBeastMode && _character.adventureController.beastModeMove.button.interactable)
            {
                _character.adventureController.beastModeMove.doMove();
                return;
            }

            if (!_character.adventure.beastModeOn && useBeastMode &&
                _character.adventureController.beastModeMove.button.interactable)
            {
                _character.adventureController.beastModeMove.doMove();
                return;
            }

            //Move back to safe zone if we're in the wrong zone
            if (_character.adventure.zone != zone && _character.adventure.zone != -1)
            {
                MoveToZone(-1);
                return;
            }

            // Do not bounce out of a zone while waiting for its enemy spawn when the
            // character has no pre-cast skill yet. That previously caused a 10 Hz
            // Safe Zone <-> target loop throughout the early game.
            var canPrecast = ChargeUnlocked() || ParryUnlocked();
            var needsPrecast = ChargeUnlocked() && !ChargeActive()
                               || ParryUnlocked() && !ParryActive();
            var readyPrecast = ChargeUnlocked() && !ChargeActive() && ChargeReady()
                               || ParryUnlocked() && !ParryActive() && ParryReady();
            if (precastBuffs && canPrecast && needsPrecast
                && readyPrecast
                && _character.adventureController.currentEnemy == null
                && _character.adventure.zone != -1)
            {
                RecoveryReason = "Entering Safe Zone to prepare unlocked combat skills";
                MoveToZone(-1);
                return;
            }

            //If we're in safe zone, recover health if needed. Also precast buffs
            if (_character.adventure.zone == -1)
            {
                var highRiskPrecast = precastBuffs && fastCombat;
                if (precastBuffs)
                {
                    if (ChargeUnlocked() && !ChargeActive())
                    {
                        RecoveryReason = "Pre-casting Charge before the next Adventure fight";
                        if (CastCharge()) return;
                    }

                    if (ParryUnlocked() && !ParryActive())
                    {
                        RecoveryReason = "Pre-casting Parry before the next Adventure fight";
                        if (CastParry()) return;
                    }

                    // Waiting for every cooldown after every trash kill destroys
                    // Adventure uptime.  Only a high-risk fast/manual target (Titans)
                    // waits for a complete pre-cast package. Routine push combat casts
                    // whatever is ready and immediately resumes fighting.
                    if (highRiskPrecast)
                    {
                        RecoveryReason = "Waiting for the high-risk target's pre-cast package";
                        _recoveryTargetHP = _character.totalAdvHP() * .95f;
                        if (ChargeUnlocked() && !ChargeReady()) return;
                        if (ParryUnlocked() && !ParryReady()) return;
                        if (MegaBuffUnlocked() && !MegaBuffReady()) return;
                        if (UltimateBuffUnlocked() && !UltimateBuffReady()) return;
                        if (DefensiveBuffUnlocked() && !DefensiveBuffReady()) return;
                        if (_character.adventure.curHP < _recoveryTargetHP)
                        {
                            RecoveryReason = "Recovering to 95% HP for the high-risk Titan window";
                            return;
                        }
                    }
                }

                if (recoverHealth && NeedsRecoveryForNextFight())
                {
                    if (ChargeUnlocked() && !ChargeActive())
                    {
                        if (CastCharge()) return;
                    }

                    if (ParryUnlocked() && !ParryActive())
                    {
                        if (CastParry()) return;
                    }
                    return;
                }
                RecoveryReason = string.Empty;
            }
            
            //Move to the zone
            if (_character.adventure.zone != zone)
            {
                _isFighting = false;
                MoveToZone(zone);
                return;
            }

            //Wait for an enemy to spawn
            if (_character.adventureController.currentEnemy == null)
            {
                if (_isFighting)
                {
                    _isFighting = false;
                    var playerDied = _character.adventure.curHP <= 0.001f;
                    var observedDamage = Math.Max(0, _fightStartHP - _character.adventure.curHP);
                    if (observedDamage > 0)
                        _expectedFightDamage = _expectedFightDamage <= 0
                            ? observedDamage
                            : _expectedFightDamage * .65f + observedDamage * .35f;
                    if (_fightTimer > 1)
                        LogCombat(playerDied
                            ? $"{_enemyName} defeated the player after {_fightTimer:00.0}s"
                            : $"{_enemyName} killed in {_fightTimer:00.0}s");
                    if (playerDied)
                        Main.LogAction("DEATH", "Adventure defeat by " + _enemyName
                                                   + " [confirmed by HP=0 and enemy-clear state]");
                    MajorUnlockPlanner.RecordFightResult(_character, _fightZone, playerDied);

                    _fightTimer = 0;
                    if (LoadoutManager.CurrentLock == LockType.Gold)
                    {
                        Log(playerDied
                            ? "Gold Loadout fight failed; restoring progression gear before retry"
                            : "Gold Loadout kill done. Turning off setting and swapping gear");
                        if (!playerDied) Settings.DoGoldSwap = false;
                        LoadoutManager.RestoreGear();
                        LoadoutManager.ReleaseLock();
                        MoveToZone(-1);
                        return;
                    }

                    if (_fightWasTitan && LoadoutManager.CurrentLock == LockType.Titan)
                    {
                        LoadoutManager.CompleteTitanFight(playerDied);
                        LoadoutManager.RestoreGear();
                        LoadoutManager.ReleaseLock();
                    }

                    // Natural enemy-free frame: apply a queued exact-reference gear
                    // improvement without discarding any live enemy or special target.
                    ProgressionLoadoutOptimizer.Manage();

                    if (recoverHealth && NeedsRecoveryForNextFight())
                    {
                        Main.LogAction("RECOVERY", RecoveryReason + ": HP "
                            + Math.Floor(_character.adventure.curHP) + "/"
                            + Math.Floor(_character.totalAdvHP()) + ", resume at "
                            + Math.Ceiling(_recoveryTargetHP));
                        MoveToZone(-1);
                        return;
                    }
                }
                _fightTimer = 0;
                if (!precastBuffs && bossOnly)
                {
                    if (!ChargeActive())
                    {
                        if (CastCharge())
                        {
                            return;
                        }
                    }

                    if (!ParryActive())
                    {
                        if (CastParry())
                        {
                            return;
                        }
                    }

                    if (GetHPPercentage() < .75)
                    {
                        if (CastHeal())
                            return;
                    }
                }

                if (fastCombat)
                {
                    if (GetHPPercentage() < .75)
                    {
                        if (CastHeal())
                            return;
                    }

                    if (GetHPPercentage() < .60)
                    {
                        if (CastHyperRegen())
                            return;
                    }
                }

                
                return;
            }

            if (zone < 1000 && Settings.BlacklistedBosses.Contains(_character.adventureController.currentEnemy.spriteID))
            {
                MoveToZone(-1);
                MoveToZone(zone);
                return;
            }

            //We have an enemy. Lets check if we're in bossOnly mode
            if (bossOnly && zone < 1000)
            {
                var ec = _character.adventureController.currentEnemy.enemyType;
                if (ec != enemyType.boss && !ec.ToString().Contains("bigBoss"))
                {
                    MoveToZone(-1);
                    MoveToZone(zone);
                    return;
                }
            }

            if (!_isFighting)
            {
                _fightStartHP = _character.adventure.curHP;
                _fightZone = zone;
                var enemyTypeName = _character.adventureController.currentEnemy.enemyType.ToString();
                _fightWasTitan = ZoneHelpers.ZoneIsTitan(zone)
                                 && (enemyTypeName.Contains("bigBoss") || enemyTypeName.Contains("guardian"));
            }
            _isFighting = true;
            _enemyName = _character.adventureController.currentEnemy.name;
            //We have an enemy and we're ready to fight. Run through our combat routine
            if (_character.training.attackTraining[1] > 0)
                DoCombat(fastCombat);
        }
    }
}
