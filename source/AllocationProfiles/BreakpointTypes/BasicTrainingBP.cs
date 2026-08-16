using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Text;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

BasicTrainingBP models one attack/defense training pair, including asymmetric unlocks, Sync
Training's two-Energy cost, native discrete rates, boss marginal value, and permanent cap-
compression payoff. It allocates through explicit side overloads and verifies idle/side deltas.
Never replace this with equal shares or a strongest-skill-only shortcut.
*/
namespace NGUInjector.AllocationProfiles.BreakpointTypes
{
    internal class BasicTrainingBP : BaseBreakpoint
    {
        private int BTIndex => Index <= 5 ? Index : Index - 6;

        private static readonly string[] AttackNames =
            { "Basic Attack", "Strong Attack", "Parry", "Piercing Attack", "Ultimate Attack", "Mega Buff" };
        private static readonly string[] DefenseNames =
            { "Basic Defense", "Defensive Buff", "Heal", "Block", "Ultimate Buff", "Oh Shit" };

        internal string Label
        {
            get
            {
                if (Character.settings.syncTraining && Index <= 5)
                {
                    if (AttackUnlocked && DefenseUnlocked)
                        return AttackNames[BTIndex] + " + " + DefenseNames[BTIndex];
                    return AttackUnlocked ? AttackNames[BTIndex] : DefenseNames[BTIndex];
                }
                return Index <= 5 ? AttackNames[BTIndex] : DefenseNames[BTIndex];
            }
        }

        internal double PriorityScore
        {
            get
            {
                var isAttack = Index <= 5;
                var attackUtility = isAttack && AttackUnlocked ? MarginalBossUtility(true) : 0.0;
                var defenseUtility = !isAttack && DefenseUnlocked ? MarginalBossUtility(false) : 0.0;

                // Sync Training spends a paired unit of energy and advances both sides. The
                // candidate must therefore be valued by the combined attack + defense change.
                if (Character.settings.syncTraining && isAttack && DefenseUnlocked)
                    defenseUtility = MarginalBossUtility(false);

                var unlockBoost = UnlockGateBoost(isAttack);
                if (Character.settings.syncTraining && isAttack)
                    unlockBoost = Math.Max(unlockBoost, UnlockGateBoost(false));
                var combined = attackUtility + defenseUtility;
                if (Character.settings.syncTraining && isAttack && AttackUnlocked && DefenseUnlocked)
                    combined /= 2.0;
                return combined * unlockBoost;
            }
        }

        internal double ConfiguredFraction { get { return CapPercent; } }

        // A locally greedy boss derivative can permanently starve a newly unlocked,
        // high-cap row.  This reservation asks a different question: can a constant
        // allocation reach the row's maximum cap reduction before this rebirth, and
        // will the permanently smaller cap repay that allocation within two future
        // runs?  The allocator funds these investments before ranking the remaining
        // Energy by immediate boss value.
        internal long LongHorizonReservation
        {
            get
            {
                if (Main.Autopilot == null || Main.Autopilot.Plan == null
                    || Main.Autopilot.Plan.RebirthSeconds <= 0)
                    return 0;
                var remaining = Main.Autopilot.Plan.RebirthSeconds
                                - (int)Math.Floor(Character.rebirthTime.totalseconds);
                if (remaining <= 0)
                    return 0;

                var attack = Index <= 5 && AttackUnlocked
                    ? SideReservation(true, remaining) : new HorizonReservation();
                var defense = ((Index > 5 && DefenseUnlocked)
                               || (Character.settings.syncTraining && Index <= 5 && DefenseUnlocked))
                    ? SideReservation(false, remaining) : new HorizonReservation();
                var required = attack.Energy + defense.Energy;
                var permanentSaving = attack.PermanentCapSaving + defense.PermanentCapSaving;
                if (required <= 0 || permanentSaving <= 0)
                    return 0;

                // Allocation is not consumed, but it displaces current-run work.  A
                // two-run energy-cap payback is a conservative finite horizon that
                // admits durable investments without sacrificing a run for tiny gains.
                return required <= 2L * permanentSaving ? required : 0;
            }
        }

        internal double LongHorizonPaybackRuns
        {
            get
            {
                if (Main.Autopilot == null || Main.Autopilot.Plan == null)
                    return double.MaxValue;
                var remaining = Main.Autopilot.Plan.RebirthSeconds
                                - (int)Math.Floor(Character.rebirthTime.totalseconds);
                if (remaining <= 0)
                    return double.MaxValue;
                var attack = Index <= 5 && AttackUnlocked
                    ? SideReservation(true, remaining) : new HorizonReservation();
                var defense = ((Index > 5 && DefenseUnlocked)
                               || (Character.settings.syncTraining && Index <= 5 && DefenseUnlocked))
                    ? SideReservation(false, remaining) : new HorizonReservation();
                var saving = attack.PermanentCapSaving + defense.PermanentCapSaving;
                return saving <= 0 ? double.MaxValue : (double)(attack.Energy + defense.Energy) / saving;
            }
        }

        internal long AllocateLongHorizonReservation(long availableBudget)
        {
            if (availableBudget <= 0 || Main.Autopilot == null || Main.Autopilot.Plan == null)
                return 0;
            var remaining = Main.Autopilot.Plan.RebirthSeconds
                            - (int)Math.Floor(Character.rebirthTime.totalseconds);
            if (remaining <= 0)
                return 0;
            var attack = Index <= 5 && AttackUnlocked
                ? SideReservation(true, remaining) : new HorizonReservation();
            var defense = ((Index > 5 && DefenseUnlocked)
                           || (Character.settings.syncTraining && Index <= 5 && DefenseUnlocked))
                ? SideReservation(false, remaining) : new HorizonReservation();
            var required = attack.Energy + defense.Energy;
            if (required <= 0 || required > availableBudget || required > Character.idleEnergy)
                return 0;

            var idleBefore = Character.idleEnergy;
            if (Character.settings.syncTraining && Index <= 5)
            {
                var paired = Math.Min(attack.Energy, defense.Energy);
                if (paired > 0)
                {
                    Character.allOffenseController.trains[BTIndex].addEnergy(paired);
                    Character.allDefenseController.trains[BTIndex].addEnergy(paired);
                }
                if (attack.Energy > paired)
                    Character.allOffenseController.trains[BTIndex].addEnergy(attack.Energy - paired);
                if (defense.Energy > paired)
                    Character.allDefenseController.trains[BTIndex].addEnergy(defense.Energy - paired);
            }
            else if (Index <= 5 && attack.Energy > 0)
            {
                Character.allOffenseController.trains[BTIndex].addEnergy(attack.Energy);
            }
            else if (Index > 5 && defense.Energy > 0)
            {
                Character.allDefenseController.trains[BTIndex].addEnergy(defense.Energy);
            }
            return Math.Max(0L, idleBefore - Character.idleEnergy);
        }

        private HorizonReservation SideReservation(bool attackSide, int remainingSeconds)
        {
            var level = attackSide ? Character.training.attackTraining[BTIndex]
                : Character.training.defenseTraining[BTIndex];
            var cap = attackSide ? Character.training.attackCaps[BTIndex]
                : Character.training.defenseCaps[BTIndex];
            if (cap <= 1)
                return new HorizonReservation();
            var target = RebirthOptimizer.MaxCapReductionLevel(cap, BTIndex);
            if (level >= target)
                return new HorizonReservation();

            var levelMultiplier = 1L;
            if (Character.adventure.itopod.perkLevel.Count > 15
                && Character.adventure.itopod.perkLevel[15] >= 1) levelMultiplier++;
            if (Character.beastQuest.quirkLevel.Count > 17
                && Character.beastQuest.quirkLevel[17] >= 1) levelMultiplier++;
            if (Character.wishes.wishes.Count > 23
                && Character.wishes.wishes[23].level >= 1) levelMultiplier++;
            var completions = (long)Math.Ceiling((target - level) / (double)levelMultiplier);
            var ticksAvailable = 50L * remainingSeconds;
            if (completions <= 0 || ticksAvailable < completions)
                return new HorizonReservation();
            var ticksPerCompletion = ticksAvailable / completions;
            if (ticksPerCompletion <= 0)
                return new HorizonReservation();
            var energy = Math.Min(cap, (long)Math.Ceiling(cap / (double)ticksPerCompletion));
            var currentReduction = RebirthOptimizer.CapReduction(level, cap, BTIndex);
            var targetReduction = RebirthOptimizer.CapReduction(target, cap, BTIndex);
            return new HorizonReservation
            {
                Energy = energy,
                PermanentCapSaving = Math.Max(0L, targetReduction - currentReduction)
            };
        }

        private struct HorizonReservation
        {
            internal long Energy;
            internal long PermanentCapSaving;
        }

        private double MarginalBossUtility(bool attackSide)
        {
            var levels = attackSide
                ? Character.training.attackTraining[BTIndex]
                : Character.training.defenseTraining[BTIndex];
            var cap = attackSide
                ? Character.training.attackCaps[BTIndex]
                : Character.training.defenseCaps[BTIndex];
            if (cap <= 0)
                return 0.0;

            var baseTotal = attackSide
                ? Character.training.getTotalAttack()
                : Character.training.getTotalDefense();
            var inventoryFactor = 1.0 + (attackSide
                ? Character.inventoryController.attackBonus()
                : Character.inventoryController.defenseBonus()) / 100.0;
            var nativeTrainingMultiplier = (attackSide ? Character.attackMulti : Character.defenseMulti)
                                           * Character.adventureController.itopod.totalStatBonus()
                                           * inventoryFactor
                                           * (attackSide ? Character.attackBoost : Character.defenseBoost);
            var currentCore = Math.Max(1.0, 100.0 + baseTotal * nativeTrainingMultiplier);
            var finalTotal = attackSide ? Character.attack : Character.defense;
            // Preserve every downstream native multiplier while handling the additive
            // base-100 core exactly; final/base incorrectly overvalues early levels.
            var finalPerBaseStat = finalTotal / currentCore * nativeTrainingMultiplier;
            // The game awards the discrete L -> L+1 difference. Using a derivative
            // systematically overstates low-level training and can change the chosen pair.
            var statPerLevel = Character.training.trainFactor[BTIndex]
                               * (Math.Pow(levels + 1.0, 1.3) - Math.Pow(levels, 1.3));
            var statPerEnergy = finalPerBaseStat * statPerLevel / cap;

            var outgoing = 0.02 * Math.Max(0.0, Character.attack - Character.bossDefense)
                           - Character.bossRegen;
            if (attackSide)
            {
                // Until damage exceeds regen, attack is a hard feasibility constraint.
                if (outgoing <= 0)
                    return statPerEnergy * (1e12 / Math.Max(1.0, -outgoing));
                var killMarginDerivative = Character.bossCurHP * 0.0004 / (outgoing * outgoing);
                return statPerEnergy * killMarginDerivative;
            }

            var incoming = 0.02 * Math.Max(0.0, Character.bossAttack - Character.defense)
                           - (0.001 + 0.001 * Character.defense);
            if (incoming <= 0 || outgoing <= 0)
                return 0.0;
            var killSeconds = Character.bossCurHP / outgoing * 0.02;
            var survivalSeconds = Character.curHP / incoming * 0.02;
            // Once the current fight already survives, more Defense cannot shorten it;
            // all marginal boss energy should move to Attack or a progression gate.
            if (survivalSeconds > killSeconds)
                return 0.0;
            var survivalMarginDerivative = Character.curHP * 0.00042 / (incoming * incoming);
            return statPerEnergy * survivalMarginDerivative;
        }

        private double UnlockGateBoost(bool attackSide)
        {
            if (BTIndex >= 5)
                return 1.0;
            var levels = attackSide
                ? Character.training.attackTraining[BTIndex]
                : Character.training.defenseTraining[BTIndex];
            var target = 5000L * (BTIndex + 1) + 1L;
            if (levels >= target)
                return 1.0;
            var completion = (double)levels / target;
            // Unlock value rises sharply only inside the near-horizon window, without
            // overriding a hard boss-damage feasibility constraint at low completion.
            return 1.0 + 20.0 * Math.Pow(completion, 6.0);
        }

        protected override bool Unlocked()
        {
            if (Index < 0 || Index > 11)
                return false;
            if (Character.settings.syncTraining && Index <= 5)
                return AttackUnlocked || DefenseUnlocked;
            return Index <= 5 ? AttackUnlocked : DefenseUnlocked;
        }

        private bool AttackUnlocked
        {
            get { return BTIndex == 0 || Character.training.attackTraining[BTIndex - 1] > 5000 * BTIndex; }
        }

        private bool DefenseUnlocked
        {
            get { return BTIndex == 0 || Character.training.defenseTraining[BTIndex - 1] > 5000 * BTIndex; }
        }

        protected override bool TargetMet()
        {
            return false;
        }

        internal override bool Allocate()
        {
            AllocateResidual(Math.Max(0L, (long)Math.Floor(MaxAllocation)));
            return true;
        }

        internal long AllocateResidual(long idleBudget)
        {
            if (idleBudget <= 0 || !IsValid()) return 0;
            var idleBefore = Character.idleEnergy;
            if (Index <= 5)
            {
                var headroom = Math.Max(0L,
                    Character.training.attackCaps[BTIndex] - Character.training.attackEnergy[BTIndex]);
                if (Character.settings.syncTraining)
                {
                    var attackHeadroom = AttackUnlocked ? headroom : 0L;
                    var defenseHeadroom = DefenseUnlocked ? Math.Max(0L,
                        Character.training.defenseCaps[BTIndex] - Character.training.defenseEnergy[BTIndex]) : 0L;
                    var remainingBudget = Math.Min(idleBudget, Character.idleEnergy);
                    // Pair only the mutually productive part. Once the lower-cap
                    // side is full, explicit native side overloads let the remaining
                    // Energy accelerate the higher-cap side without wasting a mirrored
                    // over-cap copy.
                    if (AttackUnlocked && DefenseUnlocked)
                    {
                        var pairedAmount = Math.Min(Math.Min(attackHeadroom, defenseHeadroom),
                            remainingBudget / 2L);
                        if (pairedAmount > 0)
                        {
                            Character.allOffenseController.trains[BTIndex].addEnergy(pairedAmount);
                            Character.allDefenseController.trains[BTIndex].addEnergy(pairedAmount);
                            remainingBudget -= 2L * pairedAmount;
                            attackHeadroom -= pairedAmount;
                            defenseHeadroom -= pairedAmount;
                        }
                    }
                    if (remainingBudget > 0 && attackHeadroom > 0 && defenseHeadroom > 0)
                    {
                        // This can only be the odd unit left after pairing; choose its
                        // exact current boss marginal rather than an arbitrary side.
                        if (MarginalBossUtility(true) >= MarginalBossUtility(false))
                        {
                            Character.allOffenseController.trains[BTIndex].addEnergy(1L);
                            attackHeadroom--;
                        }
                        else
                        {
                            Character.allDefenseController.trains[BTIndex].addEnergy(1L);
                            defenseHeadroom--;
                        }
                        remainingBudget--;
                    }
                    if (remainingBudget > 0 && attackHeadroom > 0)
                    {
                        var single = Math.Min(attackHeadroom, remainingBudget);
                        Character.allOffenseController.trains[BTIndex].addEnergy(single);
                        remainingBudget -= single;
                    }
                    if (remainingBudget > 0 && defenseHeadroom > 0)
                    {
                        var single = Math.Min(defenseHeadroom, remainingBudget);
                        Character.allDefenseController.trains[BTIndex].addEnergy(single);
                    }
                    return Math.Max(0L, idleBefore - Character.idleEnergy);
                }
                var amount = Math.Min(headroom, idleBudget);
                if (amount <= 0) return 0;
                Character.allOffenseController.trains[BTIndex].addEnergy(amount);
            }
            else
            {
                var headroom = Math.Max(0L,
                    Character.training.defenseCaps[BTIndex] - Character.training.defenseEnergy[BTIndex]);
                var amount = Math.Min(headroom, idleBudget);
                if (amount <= 0) return 0;
                Character.allDefenseController.trains[BTIndex].addEnergy(amount);
            }
            return Math.Max(0L, idleBefore - Character.idleEnergy);
        }

        protected override bool CorrectResourceType()
        {
            return Type == ResourceType.Energy;
        }
    }
}
