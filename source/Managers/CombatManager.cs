using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using NGUInjector.Autopilot;
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
        private int _expectedFightDamageZone = -2;
        private float _recoveryTargetHP;
        private string _fightSignature = string.Empty;
        private string _nextPolicySignature = string.Empty;

        private sealed class FightSample
        {
            internal float ExpectedDamage;
            internal double ExpectedSeconds;
            internal int Kills;
            internal int Deaths;
        }

        // Recovery evidence is keyed by the facts which change incoming damage and tactical
        // cadence. Zone-only aggregation mixed Beast/non-Beast and unrelated physical loadouts,
        // causing a safe sample from one controller branch to authorize another.
        private static readonly Dictionary<string, FightSample> FightSamples =
            new Dictionary<string, FightSample>();

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
            var signatureDamage = ExpectedDamageForNextPolicy();
            var expectedDamage = signatureDamage > 0 ? signatureDamage : _expectedFightDamage;
            // A routine encounter does not require a full heal.  Use observed damage
            // from this exact zone/run, with enough margin for the game's 0.8-1.2
            // damage roll and one delayed player input.  Before the first sample,
            // 55% HP is a conservative early-game starting point.
            if (expectedDamage <= 0)
                return maxHP * .55f;
            return Math.Min(maxHP, Math.Max(maxHP * .30f,
                expectedDamage * 1.30f + maxHP * .08f));
        }

        private float ExpectedDamageForNextPolicy()
        {
            if (string.IsNullOrEmpty(_nextPolicySignature)) return 0f;
            return FightSamples.Where(x => x.Key.StartsWith(_nextPolicySignature + "|",
                    StringComparison.Ordinal))
                .Select(x => x.Value.ExpectedDamage).DefaultIfEmpty(0f).Max();
        }

        internal static double ObservedKillSeconds(int zone, bool bossOnly)
        {
            var prefix = zone + "|" + (bossOnly ? "boss" : "all") + "|";
            var samples = FightSamples.Where(x => x.Key.StartsWith(prefix,
                    StringComparison.Ordinal) && x.Value.Kills > 0)
                .Select(x => x.Value).ToList();
            if (samples.Count == 0) return -1.0;
            var weight = samples.Sum(x => x.Kills);
            return weight <= 0 ? -1.0 : samples.Sum(x => x.ExpectedSeconds * x.Kills) / weight;
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

            if (_character.adventure.move69Unlocked
                && _character.adventure.move69Used < 69
                && !EndgameDependencyModel.IsOwned(_character, 481))
            {
                var move = UnityEngine.Object.FindObjectOfType<Move69>();
                if (move != null && move.button != null && move.button.IsInteractable())
                {
                    var before = _character.adventure.move69Used;
                    move.doMove();
                    var confirmed = _character.adventure.move69Used > before
                                    || EndgameDependencyModel.IsOwned(_character, 481);
                    Main.LogAction(confirmed ? "PROGRESSION" : "REJECTED", confirmed
                        ? "Used MOVE 69 for END item 481 [confirmed " + before + " -> "
                          + _character.adventure.move69Used + "]"
                        : "MOVE 69 was interactable but produced no use-count or END-item transition");
                    return;
                }
            }

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
            if (confirmed && zone >= 0 && zone != _expectedFightDamageZone)
            {
                _expectedFightDamage = 0;
                _expectedFightDamageZone = zone;
            }
            Main.LogAction(confirmed ? "ZONE" : "REJECTED",
                confirmed
                    ? "Changed Adventure zone " + GameNames.Zone(_character, before) + " -> "
                      + GameNames.Zone(_character, zone) + " [confirmed by game state]"
                    : "Adventure zone request " + GameNames.Zone(_character, before) + " -> "
                      + GameNames.Zone(_character, zone) + " was rejected");
        }

        internal void IdleZone(int zone, bool bossOnly, bool recoverHealth, bool? beastMode = null)
        {
            var intendedBeast = (beastMode ?? Settings.BeastMode)
                                && _character.adventureController.hasBeastMode();
            _nextPolicySignature = PolicySignature(zone, bossOnly, false, intendedBeast);
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
            _nextPolicySignature = PolicySignature(zone, bossOnly, fastCombat, beastMode);
            if (zone == -1)
            {
                if (_character.adventure.zone != -1)
                {
                    MoveToZone(-1);
                    return;
                }
            }

            /*
            NATIVE-DEATH RECONCILIATION

            Adventure death clears currentEnemy and moves the player to Safe Zone before the next
            automation pass. Handle that transition before normal Safe-Zone preparation; otherwise
            the later target-zone move resets _isFighting and silently loses the confirmed failure.
            Bot-requested recovery and spawn rerolls already clear _isFighting before reaching this
            state, so this branch identifies an involuntary defeat without guessing from low HP.
            */
            if (_isFighting && zone >= 0 && _character.adventure.zone == -1
                && _character.adventureController.currentEnemy == null)
            {
                _isFighting = false;
                RecordObservedFight(true);
                if (_fightTimer > 1)
                    LogCombat($"{_enemyName} defeated the player after {_fightTimer:00.0}s");
                Main.LogAction("DEATH", "Adventure defeat by " + _enemyName
                    + " [confirmed by native enemy-clear and forced Safe-Zone transition]");
                MajorUnlockPlanner.RecordFightResult(_character, _fightZone, true);
                _fightTimer = 0;

                if (LoadoutManager.CurrentLock == LockType.Gold)
                {
                    if (LoadoutManager.RestoreGear())
                        LoadoutManager.ReleaseLock();
                }
                if (_fightWasTitan && LoadoutManager.CurrentLock == LockType.Titan)
                {
                    LoadoutManager.CompleteTitanFight(true, _fightZone);
                    if (LoadoutManager.RestoreGear())
                        LoadoutManager.ReleaseLock();
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
                    // Adventure uptime. A high-risk target may wait only for the two
                    // effects the native game actually allows us to pre-cast here.
                    // An already-active Charge/Parry satisfies the gate even though
                    // its button remains on cooldown; combat-only buffs are cast by
                    // DoCombat after an enemy exists and must never pin Safe Zone.
                    if (highRiskPrecast)
                    {
                        RecoveryReason = "Waiting for the high-risk target's pre-cast package";
                        _recoveryTargetHP = _character.totalAdvHP() * .95f;
                        if (ChargeUnlocked() && !ChargeActive() && !ChargeReady()) return;
                        if (ParryUnlocked() && !ParryActive() && !ParryReady()) return;
                        if (_character.adventure.curHP < _recoveryTargetHP)
                        {
                            RecoveryReason = "Recovering to 95% HP for the high-risk unlock attempt";
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
                    RecordObservedFight(playerDied);
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
                        if (LoadoutManager.RestoreGear())
                            LoadoutManager.ReleaseLock();
                        MoveToZone(-1);
                        return;
                    }

                    if (_fightWasTitan && LoadoutManager.CurrentLock == LockType.Titan)
                    {
                        LoadoutManager.CompleteTitanFight(playerDied, _fightZone);
                        if (LoadoutManager.RestoreGear())
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
                _fightSignature = PolicySignature(zone, bossOnly, fastCombat, beastMode) + "|enemy="
                                  + _character.adventureController.currentEnemy.spriteID + ":"
                                  + enemyTypeName + ":" + _character.adventureController.currentEnemy.name;
            }
            _isFighting = true;
            _enemyName = _character.adventureController.currentEnemy.name;
            //We have an enemy and we're ready to fight. Run through our combat routine
            if (_character.training.attackTraining[1] > 0)
                DoCombat(fastCombat);
        }

        private void RecordObservedFight(bool died)
        {
            var observedDamage = Math.Max(0f, _fightStartHP - _character.adventure.curHP);
            if (observedDamage > 0f)
            {
                _expectedFightDamageZone = _fightZone;
                _expectedFightDamage = _expectedFightDamage <= 0f
                    ? observedDamage : _expectedFightDamage * .65f + observedDamage * .35f;
            }
            if (string.IsNullOrEmpty(_fightSignature)) return;
            FightSample sample;
            if (!FightSamples.TryGetValue(_fightSignature, out sample))
            {
                sample = new FightSample();
                FightSamples[_fightSignature] = sample;
            }
            if (observedDamage > 0f)
                sample.ExpectedDamage = sample.ExpectedDamage <= 0f ? observedDamage
                    : sample.ExpectedDamage * .65f + observedDamage * .35f;
            if (died)
                sample.Deaths++;
            else
            {
                sample.Kills++;
                if (_fightTimer > 0f)
                    sample.ExpectedSeconds = sample.ExpectedSeconds <= 0.0 ? _fightTimer
                        : sample.ExpectedSeconds * .65 + _fightTimer * .35;
            }
            // A single session cannot encounter enough distinct meaningful signatures to need an
            // unbounded cache. Retain the newest evidence by clearing only after pathological churn;
            // correctness falls back to the conservative unsampled recovery threshold.
            if (FightSamples.Count > 256)
                FightSamples.Clear();
        }

        private string PolicySignature(int zone, bool bossOnly, bool fastCombat, bool beastMode)
        {
            var items = new[]
            {
                _character.inventory.head, _character.inventory.chest, _character.inventory.legs,
                _character.inventory.boots, _character.inventory.weapon, _character.inventory.weapon2
            }.Concat(_character.inventory.accs).Where(x => x != null && x.id > 0)
                .Select(x => x.id + ":" + x.level).ToArray();
            return zone + "|" + (bossOnly ? "boss" : "all") + "|"
                   + (fastCombat ? "fast" : "full") + "|"
                   + (beastMode ? "beast" : "normal") + "|gear=" + string.Join(",", items);
        }
    }
}
