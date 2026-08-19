using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using NGUInjector.Autopilot;
using static NGUInjector.Main;
using static NGUInjector.Managers.CombatHelpers;

/*
FILE PURPOSE

Purpose: CombatManager executes Adventure movement and source-backed tactical action state through
native controllers. It owns manual/idle selection, Walderp response windows, reactive defense,
terminal-Titan first-action reservation, observed fight timing, route/equipment-bound collection cadence,
and confirmed fight reconciliation.

Mechanism: Zone policy arrives from AutopilotManager. This manager classifies an objective enemy
through TitanMechanics, performs at most one zone transition per policy pass, selects only currently
interactable native move buttons, and confirms every cast through CombatHelpers. T13/T14 are entered
only after one exact ready move has a conservative live-state lethal proof; the reserved move is the
only action permitted until native enemy-clear reconciliation.

Inputs and outputs: Inputs are live Character/Adventure/PlayerController/EnemyAI state, immutable
settings, Titan descriptors, and controller button readiness. Outputs are native zone/toggle/move
calls, recovery/hold telemetry, fight samples, exact fought-floor ITOPOD trial outcomes, and
loadout-lock completion signals.

ITOPOD is a continuous native floor state: ordinary recovery never exits it because re-entry resets
the ten-kill counter. Enemy-free floor boundaries may spend ready Heal/Hyper Regen moves in place.
Native ITOPOD death actually keeps zone 1000 selected, resets the range, and leaves HP at zero; the
manager therefore detects the confirmed defeat, explicitly exits once, and holds a full-HP recovery
lease in Safe Zone before any retry. The same lease applies after every confirmed manual Adventure
defeat, independent of the next route's ordinary recovery threshold.
An open-ended record attempt also exits when enemy HP has made no new low for a full minute; that is
a failed combat attempt, not a healing loop, and it feeds the same empirical circuit breaker and
full-HP Safe-Zone recovery lease as death.

Invariants and safety: Regular Attack unlock is row 0 >= 5,000. A Walderp command permits exactly
the requested move when Walderp Says and only a different damaging move otherwise; no buff or MOVE
69 may consume that response. Three-second Block is reserved for a bounded near-impact window while
persistent Parry is preferred at early warnings. T13/T14 never spend their first action on MOVE 69
or a buff. Logical reservations and externally held input must have an epoch cancellation callback.
No method in this file claims an exact general Adventure simulator: independent Unity Update order,
RNG, bespoke AI, and cooldown progress remain live-state observations.

Extension points and non-goals: Add a source-backed local transition or conservative admission
predicate here; put global target selection and reward valuation in the planner. TitanExecutionManager
may consume the terminal-reservation hooks when it owns pre-staging. This file does not choose a
Titan version, project loot, buy upgrades, reset the run, or infer kills from intended actions.
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
        private int _fightItopodFloor = -1;
        private ItopodFightProgressWatch _itopodProgressWatch;
        private bool _fightWasTitan;
        private float _expectedFightDamage;
        private int _expectedFightDamageZone = -2;
        private float _recoveryTargetHP;
        private bool _forceFullRecoveryAfterFailure;
        private string _fightSignature = string.Empty;
        private CollectionCombatSignature _fightCollectionSignature;
        private string _nextPolicySignature = string.Empty;
        private TerminalAttackReservation _terminalReservation;

        private enum AttackMove
        {
            None = 0,
            Regular = 3,
            Strong = 4,
            Pierce = 5,
            Ultimate = 6
        }

        private sealed class TerminalAttackReservation
        {
            internal int Zone;
            internal int EnemyType;
            internal int SpriteId;
            internal AttackMove Move;
            internal float ProvenWorstDamage;
            internal bool Fired;
        }

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

        internal bool HasTerminalLethalReservation(int titanId)
        {
            if (titanId != 13 && titanId != 14) return false;
            var reservation = _terminalReservation;
            return reservation != null && !reservation.Fired
                   && reservation.Zone == TitanMechanics.Describe(titanId).Zone;
        }

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

        /*
        ADVENTURE ACTION-STATE SAFETY

        These helpers encode only locally proven transitions: the installed Regular Attack gate,
        counter-to-impact timing for generic Charger/Rapid AI, exact Walderp move identity, and a
        one-action conservative damage lower bound. They deliberately do not advance Unity time or
        predict an entire fight. Button interactivity and private AI factors are re-read immediately
        before every native move so stale reservations fail closed.
        */
        internal static bool RegularAttackUnlocked(long rowZeroLevel)
        {
            return rowZeroLevel >= 5000L;
        }

        internal static double SecondsUntilCounterImpact(int observedCounter, int impactCounter,
            double attackRate, double elapsedSinceLastEnemyAction)
        {
            if (observedCounter >= impactCounter) return 0.0;
            if (double.IsNaN(attackRate) || double.IsInfinity(attackRate) || attackRate <= 0.0
                || double.IsNaN(elapsedSinceLastEnemyAction)
                || double.IsInfinity(elapsedSinceLastEnemyAction))
                return double.PositiveInfinity;
            var firstInterval = Math.Max(0.0, attackRate - Math.Max(0.0,
                elapsedSinceLastEnemyAction));
            return firstInterval + Math.Max(0, impactCounter - observedCounter - 1) * attackRate;
        }

        internal static int SelectWaldoResponseMove(int requestedMove, bool waldoSays,
            bool regularReady, bool strongReady, bool pierceReady, bool ultimateReady)
        {
            if (requestedMove < (int)AttackMove.Regular
                || requestedMove > (int)AttackMove.Ultimate)
                return (int)AttackMove.None;
            var ready = new[] {regularReady, strongReady, pierceReady, ultimateReady};
            if (waldoSays)
                return ready[requestedMove - (int)AttackMove.Regular]
                    ? requestedMove : (int)AttackMove.None;

            // Any different damaging move is source-correct. Prefer the strongest ready response
            // so a successful puzzle answer also minimizes the time exposed to Walderp's growth.
            for (var move = (int)AttackMove.Ultimate;
                 move >= (int)AttackMove.Regular; move--)
                if (move != requestedMove && ready[move - (int)AttackMove.Regular])
                    return move;
            return (int)AttackMove.None;
        }

        internal static Action RegisterHeldInputCancellation(string id, Action releaseInput)
        {
            if (releaseInput == null) throw new ArgumentNullException("releaseInput");
            var released = 0;
            Action releaseOnce = delegate
            {
                if (Interlocked.Exchange(ref released, 1) == 0) releaseInput();
            };
            Main.RegisterEpochCancellation(string.IsNullOrEmpty(id)
                ? "combat-held-input" : id, releaseOnce);
            return releaseOnce;
        }

        private bool ManualCombatUnlocked()
        {
            var training = _character.training.attackTraining;
            return training != null && training.Length > 0
                   && RegularAttackUnlocked(training[0]);
        }

        private bool AnyOffensiveMoveReady()
        {
            var ac = _character.adventureController;
            return IsReady(ac.regularAttackMove.button) || IsReady(ac.strongAttackMove.button)
                   || IsReady(ac.pierceMove.button) || IsReady(ac.ultimateAttackMove.button);
        }

        private static bool IsReady(UnityEngine.UI.Button button)
        {
            return button != null && button.IsInteractable();
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

        private bool NeedsFullItopodRecovery()
        {
            _recoveryTargetHP = _character.totalAdvHP();
            if (!ItopodEntryRecoveryPolicy.RequiresFullHp(
                    _character.adventure.curHP, _recoveryTargetHP))
            {
                RecoveryReason = string.Empty;
                return false;
            }
            RecoveryReason = "Recovering to full Adventure HP before entering ITOPOD";
            return true;
        }

        private bool NeedsFullFailureRecovery()
        {
            _recoveryTargetHP = _character.totalAdvHP();
            if (!ItopodEntryRecoveryPolicy.RequiresFullHp(
                    _character.adventure.curHP, _recoveryTargetHP))
            {
                _forceFullRecoveryAfterFailure = false;
                RecoveryReason = string.Empty;
                return false;
            }
            RecoveryReason = "Recovering to full Adventure HP after a failed Adventure attempt";
            return true;
        }

        private static int TitanIdForZone(int zone)
        {
            for (var titan = 1; titan <= 14; titan++)
                if (TitanMechanics.Describe(titan).Zone == zone) return titan;
            return 0;
        }

        private static bool IsTerminalTitanZone(int zone)
        {
            return zone == TitanMechanics.Describe(13).Zone
                   || zone == TitanMechanics.Describe(14).Zone;
        }

        private static bool IsObjectiveEnemy(int zone, bool bossOnly, Enemy enemy)
        {
            if (enemy == null) return false;
            if (!bossOnly) return true;
            var titan = TitanIdForZone(zone);
            if (titan > 0)
                return TitanMechanics.IsTitanEnemyType(titan, (int)enemy.enemyType);
            return enemy.enemyType == enemyType.boss
                   || enemy.enemyType.ToString().Contains("bigBoss");
        }

        private bool IsCurrentWaldo()
        {
            var enemy = _character.adventureController.currentEnemy;
            return enemy != null && _character.adventure.zone == TitanMechanics.Describe(5).Zone
                   && TitanMechanics.IsTitanEnemyType(5, (int)enemy.enemyType);
        }

        private bool HandleWaldoResponse()
        {
            if (!IsCurrentWaldo()) return false;
            var ac = _character.adventureController;
            var ai = ac.enemyAI;
            if (ai == null || !ai.inWaldoSaysLoop) return false;

            var selected = (AttackMove)SelectWaldoResponseMove(ai.waldoAttackID, ai.waldoSays,
                IsReady(ac.regularAttackMove.button), IsReady(ac.strongAttackMove.button),
                IsReady(ac.pierceMove.button), IsReady(ac.ultimateAttackMove.button));
            if (selected == AttackMove.None)
            {
                // A missing exact/different response becomes a native 1000x explosion at the next
                // Walderp action. Abandon immediately; never substitute a buff, MOVE 69, or an
                // unavailable requested button and call it a puzzle solution.
                RecoveryReason = ai.waldoSays
                    ? "Walderp response held: the exact requested move is not currently ready"
                    : "Walderp response held: no different damaging move is currently ready";
                Main.LogAction("HOLD", RecoveryReason + "; returning to Safe Zone");
                _isFighting = false;
                _fightTimer = 0;
                MoveToZone(-1);
                return true;
            }

            var description = ai.waldoSays
                ? "Walderp Says — exact requested move"
                : "Walderp Says — deliberately different move";
            if (!ExecuteAttackMove(selected, description))
            {
                RecoveryReason = "Native controller rejected the selected Walderp response";
                Main.LogAction("HOLD", RecoveryReason + "; returning to Safe Zone without fallback");
                _isFighting = false;
                _fightTimer = 0;
                MoveToZone(-1);
            }
            return true;
        }

        private bool TryPrepareTerminalAttack(int zone)
        {
            if (!IsTerminalTitanZone(zone)) return true;
            if (_terminalReservation != null)
                return _terminalReservation.Zone == zone && !_terminalReservation.Fired;
            if (_character.adventure.zone != -1
                || _character.adventureController.currentEnemy != null)
                return false;
            if (!ManualCombatUnlocked() || !_pc.moveCheck() || _pc.moveTimer > 0f)
            {
                RecoveryReason = "Holding terminal Titan in Safe Zone until manual combat is ready";
                return false;
            }

            var ac = _character.adventureController;
            if (zone < 0 || zone >= ac.enemyList.Count || ac.enemyList[zone] == null
                || ac.enemyList[zone].Count == 0 || ac.enemyList[zone][0] == null)
            {
                RecoveryReason = "Holding terminal Titan: the source-pinned enemy record is unavailable";
                return false;
            }
            var expected = ac.enemyList[zone][0];
            var titan = TitanIdForZone(zone);
            if (titan < 13 || !TitanMechanics.IsTitanEnemyType(titan, (int)expected.enemyType))
            {
                RecoveryReason = "Holding terminal Titan: enemy type does not match the Titan oracle";
                return false;
            }

            float damage;
            var move = SelectLethalReadyMove(expected, 1f, false, 0, out damage);
            if (move == AttackMove.None)
            {
                if (ChargeUnlocked() && !ChargeActive() && ChargeReady())
                {
                    RecoveryReason = "Pre-casting Charge to establish a terminal lethal move";
                    CastCharge();
                    return false;
                }
                RecoveryReason = "Holding terminal Titan in Safe Zone until a specific ready move is lethal";
                return false;
            }

            _terminalReservation = new TerminalAttackReservation
            {
                Zone = zone,
                EnemyType = (int)expected.enemyType,
                SpriteId = expected.spriteID,
                Move = move,
                ProvenWorstDamage = damage,
                Fired = false
            };
            Main.RegisterEpochCancellation("combat-terminal-first-action", delegate
            {
                _terminalReservation = null;
            });
            RecoveryReason = "Reserved " + AttackMoveName(move)
                             + " as the terminal Titan's first lethal action";
            Main.LogAction("COMBAT", RecoveryReason + " [worst damage "
                + Math.Floor(damage) + " vs HP " + Math.Ceiling(expected.maxHP) + "]");
            return true;
        }

        private bool ExecuteTerminalAttackOrHold(int zone)
        {
            var reservation = _terminalReservation;
            var ac = _character.adventureController;
            var enemy = ac.currentEnemy;
            if (reservation == null || reservation.Zone != zone || enemy == null
                || reservation.EnemyType != (int)enemy.enemyType
                || reservation.SpriteId != enemy.spriteID)
            {
                RecoveryReason = "Terminal Titan first-action reservation no longer matches live state";
                Main.LogAction("HOLD", RecoveryReason + "; returning to Safe Zone");
                _terminalReservation = null;
                _isFighting = false;
                MoveToZone(-1);
                return false;
            }
            if (reservation.Fired)
                return true; // Wait for native player-first/enemy-second death reconciliation.

            var defenseFactor = ac.enemyAI.GetPV<float>("defenseFactor");
            var invincible = ac.enemyAI.invincible;
            var invincibleCount = ac.enemyAI.invincibleCount;
            float liveWorstDamage;
            if (!AttackMoveReady(reservation.Move)
                || !WorstCaseMoveDamage(reservation.Move, enemy, defenseFactor,
                    invincible, invincibleCount, out liveWorstDamage)
                || liveWorstDamage < enemy.curHP)
            {
                RecoveryReason = "Reserved terminal move is no longer ready and lethal in live state";
                Main.LogAction("HOLD", RecoveryReason + "; returning to Safe Zone without another action");
                _terminalReservation = null;
                _isFighting = false;
                MoveToZone(-1);
                return false;
            }

            var enemyHpBefore = enemy.curHP;
            if (!ExecuteAttackMove(reservation.Move, "Terminal Titan reserved first action"))
            {
                RecoveryReason = "Native controller rejected the reserved terminal move";
                _terminalReservation = null;
                _isFighting = false;
                MoveToZone(-1);
                return false;
            }
            if (enemy.curHP > 0f)
            {
                RecoveryReason = "Reserved terminal move executed but did not produce lethal enemy HP";
                Main.LogAction("REJECTED", RecoveryReason + " [HP "
                    + Math.Ceiling(enemyHpBefore) + " -> " + Math.Ceiling(enemy.curHP) + "]");
                _terminalReservation = null;
                _isFighting = false;
                MoveToZone(-1);
                return false;
            }
            reservation.Fired = true;
            Main.LogAction("PROGRESSION", "Fired reserved " + AttackMoveName(reservation.Move)
                + " at terminal Titan [live worst damage " + Math.Floor(liveWorstDamage)
                + ", Safe-Zone proof " + Math.Floor(reservation.ProvenWorstDamage)
                + " vs pre-hit HP " + Math.Ceiling(enemyHpBefore) + "]");
            return true;
        }

        private AttackMove SelectLethalReadyMove(Enemy enemy, float defenseFactor,
            bool invincible, int invincibleCount, out float selectedDamage)
        {
            selectedDamage = 0f;
            var selected = AttackMove.None;
            var moves = new[]
            {
                AttackMove.Regular, AttackMove.Strong, AttackMove.Pierce, AttackMove.Ultimate
            };
            for (var i = 0; i < moves.Length; i++)
            {
                var move = moves[i];
                float damage;
                if (!AttackMoveReady(move)
                    || !WorstCaseMoveDamage(move, enemy, defenseFactor, invincible,
                        invincibleCount, out damage)
                    || damage < enemy.maxHP)
                    continue;
                // Use the largest guaranteed margin. This is more robust to a live float/state
                // change between Safe-Zone proof and the first enemy-bearing policy pass.
                if (selected == AttackMove.None || damage > selectedDamage)
                {
                    selected = move;
                    selectedDamage = damage;
                }
            }
            return selected;
        }

        private bool WorstCaseMoveDamage(AttackMove move, Enemy enemy, float defenseFactor,
            bool invincible, int invincibleCount, out float damage)
        {
            damage = 0f;
            if (enemy == null || invincible || invincibleCount > 0
                || float.IsNaN(defenseFactor) || float.IsInfinity(defenseFactor)
                || defenseFactor <= 0f)
                return false;
            var pierceDivisor = move == AttackMove.Pierce ? 3f : 2f;
            var baseDamage = Math.Max(0f, _character.totalAdvAttack()
                - enemy.defense / pierceDivisor);
            var value = baseDamage * _pc.offenseBuffFactor;
            if (move == AttackMove.Regular) value *= _pc.offenseDebuffFactor;
            value *= _pc.chargeFactor;
            switch (move)
            {
                case AttackMove.Regular:
                    value *= _character.adventureController.regAttackMulti;
                    break;
                case AttackMove.Strong:
                case AttackMove.Pierce:
                    value *= _character.adventureController.strongAttackMulti;
                    break;
                case AttackMove.Ultimate:
                    value *= _character.ultimateAttackPower();
                    break;
                default:
                    return false;
            }
            value *= .8f;
            damage = (float)Math.Floor(value / defenseFactor);
            return !float.IsNaN(damage) && !float.IsInfinity(damage) && damage >= 0f;
        }

        private bool AttackMoveReady(AttackMove move)
        {
            var ac = _character.adventureController;
            if (!_pc.moveCheck() || _pc.moveTimer > 0f) return false;
            switch (move)
            {
                case AttackMove.Regular: return IsReady(ac.regularAttackMove.button);
                case AttackMove.Strong: return IsReady(ac.strongAttackMove.button);
                case AttackMove.Pierce: return IsReady(ac.pierceMove.button);
                case AttackMove.Ultimate: return IsReady(ac.ultimateAttackMove.button);
                default: return false;
            }
        }

        private bool ExecuteAttackMove(AttackMove move, string reason)
        {
            var ac = _character.adventureController;
            switch (move)
            {
                case AttackMove.Regular:
                    return ExecuteVerifiedMove(ac.regularAttackMove.button,
                        ac.regularAttackMove.doMove, reason + " — Regular Attack");
                case AttackMove.Strong:
                    return ExecuteVerifiedMove(ac.strongAttackMove.button,
                        ac.strongAttackMove.doMove, reason + " — Strong Attack");
                case AttackMove.Pierce:
                    return ExecuteVerifiedMove(ac.pierceMove.button,
                        ac.pierceMove.doMove, reason + " — Piercing Attack");
                case AttackMove.Ultimate:
                    return ExecuteVerifiedMove(ac.ultimateAttackMove.button,
                        ac.ultimateAttackMove.doMove, reason + " — Ultimate Attack");
                default:
                    return false;
            }
        }

        private static string AttackMoveName(AttackMove move)
        {
            switch (move)
            {
                case AttackMove.Regular: return "Regular Attack";
                case AttackMove.Strong: return "Strong Attack";
                case AttackMove.Pierce: return "Piercing Attack";
                case AttackMove.Ultimate: return "Ultimate Attack";
                default: return "no move";
            }
        }

        private void DoCombat(bool fastCombat)
        {
            // A Walderp response deadline is stricter than the global move lock. Let the response
            // state inspect readiness first so a locked/unavailable exact move exits safely instead
            // of waiting until the native 1000x explosion.
            if (HandleWaldoResponse())
                return;

            if (!_pc.moveCheck())
                return;

            if (Main.PlayerController.moveTimer > 0)
                return;

            // Fast combat suppresses optional setup, never source-backed imminent defenses.
            if (CombatCriticalReactions())
                return;

            if (!fastCombat)
            {
                if (CombatBuffs())
                    return;
            }

            CombatAttacks(fastCombat);
        }

        private bool CombatCriticalReactions()
        {
            var ac = _character.adventureController;
            var ai = ac.currentEnemy.AI;
            var eai = ac.enemyAI;

            if (ai == AI.charger)
            {
                var counter = eai.GetPV<int>("chargeCooldown");
                var enemyTimer = eai.GetPV<float>("enemyAttackTimer");
                var timeToImpact = SecondsUntilCounterImpact(counter, 5,
                    ac.currentEnemy.attackRate, enemyTimer);
                // The warning is two enemy actions before the 4x hit. Parry persists until that
                // hit; a three-second Block does not, so reserve Block for a bounded near-impact
                // window with scheduler/frame margin.
                if (counter >= 3 && counter < 5 && !_pc.isBlocking && !_pc.isParrying
                    && IsReady(ac.parryMove.button))
                {
                    return ExecuteVerifiedMove(ac.parryMove.button, ac.parryMove.doMove,
                        "Parry — persistent charger reaction");
                }

                if (counter >= 3 && counter < 5 && timeToImpact <= 2.65
                    && !_pc.isParrying && IsReady(ac.blockMove.button))
                {
                    return ExecuteVerifiedMove(ac.blockMove.button, ac.blockMove.doMove,
                        "Block — near-impact charger reaction");
                }
            }

            if (ai == AI.rapid)
            {
                var counter = eai.GetPV<int>("rapidEffect");
                var enemyTimer = eai.GetPV<float>("enemyAttackTimer");
                var timeToImpact = SecondsUntilCounterImpact(counter, 8,
                    ac.currentEnemy.attackRate, enemyTimer);
                if (counter >= 5 && counter < 8 && !_pc.isBlocking && !_pc.isParrying
                    && IsReady(ac.parryMove.button))
                {
                    return ExecuteVerifiedMove(ac.parryMove.button, ac.parryMove.doMove,
                        "Parry — persistent rapid-enemy warning reaction");
                }
                if ((counter >= 8 || counter >= 5 && timeToImpact <= 2.65)
                    && !_pc.isParrying && IsReady(ac.blockMove.button))
                {
                    return ExecuteVerifiedMove(ac.blockMove.button, ac.blockMove.doMove,
                        "Block — near-impact rapid-enemy reaction");
                }
            }

            if (ai == AI.exploder && ac.currentEnemy.attackRate - eai.GetPV<float>("enemyAttackTimer") < 1)
            {
                if (IsReady(ac.blockMove.button))
                {
                    return ExecuteVerifiedMove(ac.blockMove.button, ac.blockMove.doMove, "Block — exploder reaction");
                }
            }

            return false;
        }

        private bool CombatBuffs()
        {
            var ac = _character.adventureController;
            var ai = ac.currentEnemy.AI;
            var eai = ac.enemyAI;

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
                && !EndgameDependencyModel.IsOwned(_character, 481)
                && !IsTerminalTitanZone(_character.adventure.zone)
                && !(IsCurrentWaldo() && ac.enemyAI.inWaldoSaysLoop))
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
            if (_terminalReservation != null && zone != _terminalReservation.Zone)
                _terminalReservation = null;
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
            // Idle ITOPOD uses the same native death path as manual combat: zone 1000 remains
            // selected, the enemy disappears, and HP is exactly zero. Capture that source-exact
            // signature before the native respawn can throw the character back in unhealed.
            if (zone == 1000 && _character.adventure.zone == 1000
                && _character.adventureController.currentEnemy == null
                && _character.adventure.curHP <= 0.001f)
            {
                _forceFullRecoveryAfterFailure = true;
                RecoveryReason = "Confirmed idle ITOPOD defeat; entering Safe Zone for full recovery";
                MoveToZone(-1);
                return;
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

            if (_character.adventure.zone == -1 && _forceFullRecoveryAfterFailure
                && NeedsFullFailureRecovery())
            {
                if (CastHeal()) return;
                if (CastHyperRegen()) return;
                return;
            }
            if (_character.adventure.zone == -1 && recoverHealth)
            {
                if (zone == 1000 && NeedsFullItopodRecovery())
                {
                    if (CastHeal()) return;
                    if (CastHyperRegen()) return;
                    return;
                }
                if (zone != 1000 && NeedsRecoveryForNextFight())
                    return;
            }

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
                return;
            }

            //If we only want boss enemies
            if (bossOnly)
            {
                //Check the type of the enemy
                // If it is not the exact objective type, make one Safe-Zone hop. The next policy
                // pass owns recovery/precast and may then return; never consume two native zone
                // transitions (and two lootState advances) in one pass.
                if (!IsObjectiveEnemy(zone, true, _character.adventureController.currentEnemy))
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
                _terminalReservation = null;
                _isFighting = false;
                RecordObservedFight(true);
                _forceFullRecoveryAfterFailure = true;
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

            // Native Regular Attack unlocks from Basic Training row 0 at exactly 5,000. Manual
            // combat is never inferred from row 1 (Strong Attack's training). Ordinary fights may
            // fall back to idle while every manual attack is cooling down; Walderp and terminal
            // Titans may not, because an autonomous hit would violate their reserved action state.
            var manualUnlocked = ManualCombatUnlocked();
            var constrainedActionState = IsTerminalTitanZone(zone) || IsCurrentWaldo();
            if (!manualUnlocked)
            {
                if (IsTerminalTitanZone(zone))
                {
                    RecoveryReason = "Holding terminal Titan: Regular Attack is not unlocked at row 0 level 5,000";
                    if (_character.adventure.zone != -1) MoveToZone(-1);
                    return;
                }
                if (IsCurrentWaldo())
                {
                    RecoveryReason = "Holding Walderp: manual response moves are not unlocked";
                    _isFighting = false;
                    MoveToZone(-1);
                    return;
                }
                if (!_character.adventure.autoattacking)
                {
                    _character.adventureController.idleAttackMove.setToggle();
                    return;
                }
            }
            else if (_character.adventure.autoattacking)
            {
                if (_character.adventureController.currentEnemy == null
                    || AnyOffensiveMoveReady() || constrainedActionState)
                {
                    _character.adventureController.idleAttackMove.setToggle();
                    return;
                }
            }
            else if (_character.adventureController.currentEnemy != null
                     && !AnyOffensiveMoveReady() && !constrainedActionState)
            {
                _character.adventureController.idleAttackMove.setToggle();
                return;
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
            if (precastBuffs && !IsTerminalTitanZone(zone) && canPrecast && needsPrecast
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
                if (_forceFullRecoveryAfterFailure && NeedsFullFailureRecovery())
                {
                    if (CastHeal()) return;
                    if (CastHyperRegen()) return;
                    return;
                }
                if (recoverHealth && zone == 1000 && NeedsFullItopodRecovery())
                {
                    if (CastHeal()) return;
                    if (CastHyperRegen()) return;
                    return;
                }
                var highRiskPrecast = precastBuffs && fastCombat;
                if (precastBuffs && !IsTerminalTitanZone(zone))
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

                if (IsTerminalTitanZone(zone) && !TryPrepareTerminalAttack(zone))
                    return;
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
                    _terminalReservation = null;
                    _isFighting = false;
                    var playerDied = _character.adventure.curHP <= 0.001f;
                    RecordObservedFight(playerDied);
                    if (playerDied) _forceFullRecoveryAfterFailure = true;
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

                    // Ordinary death has already selected Safe Zone; ITOPOD death has not. Force
                    // both through the same explicit Safe-Zone recovery lease and return before
                    // the native zero-delay ITOPOD respawn can create another doomed attempt.
                    if (playerDied)
                    {
                        RecoveryReason = "Confirmed Adventure defeat; entering Safe Zone for full recovery";
                        MoveToZone(-1);
                        return;
                    }

                    // Natural enemy-free frame: apply a queued exact-reference gear
                    // improvement without discarding any live enemy or special target.
                    ProgressionLoadoutOptimizer.Manage();

                    // Safe-Zone re-entry restarts ITOPOD at its configured range start and clears
                    // the native ten-kill counter. Never turn a low-HP reading into a voluntary
                    // exit there; the bounded route proof plus the enemy-free Heal/Hyper Regen
                    // path below owns recovery, while an actual defeat still reconciles normally.
                    if (recoverHealth && zone != 1000 && NeedsRecoveryForNextFight())
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
                if (IsTerminalTitanZone(zone) && _terminalReservation != null)
                    return;
                if (zone == 1000 && _character.adventureController.itopodKillCount == 0
                    && GetHPPercentage() < .95)
                {
                    if (CastHeal()) return;
                    if (CastHyperRegen()) return;
                }
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
                _terminalReservation = null;
                _isFighting = false;
                MoveToZone(-1);
                return;
            }

            //We have an enemy. Lets check if we're in bossOnly mode
            if (bossOnly && zone < 1000)
            {
                if (!IsObjectiveEnemy(zone, true, _character.adventureController.currentEnemy))
                {
                    _terminalReservation = null;
                    _isFighting = false;
                    MoveToZone(-1);
                    return;
                }
            }

            if (!_isFighting)
            {
                _fightStartHP = _character.adventure.curHP;
                _fightZone = zone;
                _fightItopodFloor = zone >= 1000
                    ? _character.adventureController.itopodLevel : -1;
                _itopodProgressWatch = _fightItopodFloor >= 0
                    ? new ItopodFightProgressWatch(
                        _character.adventureController.currentEnemy.curHP)
                    : null;
                var enemyTypeName = _character.adventureController.currentEnemy.enemyType.ToString();
                var titanId = TitanIdForZone(zone);
                _fightWasTitan = titanId > 0 && TitanMechanics.IsTitanEnemyType(titanId,
                    (int)_character.adventureController.currentEnemy.enemyType);
                _fightCollectionSignature = zone < 1000
                                            && LootSourceCatalog.OrdinaryZone(zone) != null
                    ? AdventureCollectionPlanner.CaptureCadenceSignature(_character,
                        zone, bossOnly) : null;
                _fightSignature = PolicySignature(zone, bossOnly, fastCombat, beastMode) + "|enemy="
                                  + _character.adventureController.currentEnemy.spriteID + ":"
                                  + enemyTypeName + ":" + _character.adventureController.currentEnemy.name;
            }
            _isFighting = true;
            _enemyName = _character.adventureController.currentEnemy.name;
            if (_fightItopodFloor >= 0 && _itopodProgressWatch != null
                && ZoneHelpers.LastItopodRoute.Climbing
                && _itopodProgressWatch.Observe(_fightTimer,
                    _character.adventureController.currentEnemy.curHP,
                    _character.adventureController.currentEnemy.maxHP))
            {
                var stalledFloor = _fightItopodFloor;
                ZoneHelpers.RecordItopodNoProgressFailure(stalledFloor);
                _forceFullRecoveryAfterFailure = true;
                RecoveryReason = "Recovering to full Adventure HP after a failed ITOPOD attempt";
                LogCombat(_enemyName + " abandoned on ITOPOD floor " + stalledFloor
                    + " after enemy HP made no new low for "
                    + ItopodFightProgressWatch.NoProgressSeconds.ToString("0") + "s");
                _terminalReservation = null;
                _isFighting = false;
                _fightItopodFloor = -1;
                _itopodProgressWatch = null;
                _fightSignature = string.Empty;
                _fightWasTitan = false;
                _fightTimer = 0;
                MoveToZone(-1);
                return;
            }
            //We have an enemy and we're ready to fight. Run through our combat routine
            if (IsTerminalTitanZone(zone))
            {
                ExecuteTerminalAttackOrHold(zone);
                return;
            }
            if (ManualCombatUnlocked())
                DoCombat(fastCombat);
        }

        private void RecordObservedFight(bool died)
        {
            // Capture at enemy spawn, not after enemyDeath advances the native floor or a defeat
            // forces Safe Zone. This is the sole empirical input to the decade-climb breaker.
            if (_fightZone >= 1000 && _fightItopodFloor >= 0)
                ZoneHelpers.RecordItopodFightResult(_fightItopodFloor, died);
            if (!died && _fightCollectionSignature != null && _fightTimer > 0f)
                AdventureCollectionPlanner.RecordOnlineEligibleKill(
                    _fightCollectionSignature, _fightTimer);
            _fightCollectionSignature = null;
            _fightItopodFloor = -1;
            _itopodProgressWatch = null;
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
