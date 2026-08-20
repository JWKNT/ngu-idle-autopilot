using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

/*
FILE PURPOSE

PermanentBloodSpellManager is the sole root-coordinated live owner of repeatable permanent Blood
spells. It admits only Iron Pill, Blood MacGuffin alpha, and Blood MacGuffin beta; Loot Spaghetti,
Counterfeit Gold, NUMBER, and END Blood remain separate route/boundary owners. The manager captures
the complete Blood debit, native cooldown timers, permanent Adventure stats, and physical equipped
MacGuffin identities/levels, selects at most one spell, calls its public native controller, and
requires the source-exact full-pool debit plus spell-specific permanent postcondition.

Inputs are the live Character, Autopilot config/plan, and active root. Output is at most one typed
BloodMagic child result and confirmed BLOOD telemetry. A missing Sadistic END Blood item 494 reserves
the entire pool ahead of every repeatable spell, even before the 5e22 delivery cost is reached.
Cooldown, perk, difficulty, physical-target, overflow, and remaining-run-window gates are rechecked
inside the child immediately before Apply. Native spell effects have no safe inverse, so any partial
or unrecognized state quarantines BloodMagic rather than inventing compensation.

New permanent Blood spells belong in the pure selection/verification kernel below and need an exact
native formula plus a named route value. Run-local Loot/Gold spells are deliberate non-goals until a
planner can prove their downstream seconds saved. This file never rewrites Blood, timers, Adventure
stats, or MacGuffin levels directly.
*/
namespace NGUInjector.Autopilot
{
    internal enum PermanentBloodSpellKind
    {
        None = 0,
        IronPill = 1,
        MacGuffinAlpha = 2,
        MacGuffinBeta = 3
    }

    internal sealed class PermanentBloodSpellDecision
    {
        internal readonly PermanentBloodSpellKind Kind;
        internal readonly bool EndBloodReserved;
        internal readonly int ExpectedGain;
        internal readonly string Reason;

        internal PermanentBloodSpellDecision(PermanentBloodSpellKind kind,
            bool endBloodReserved, int expectedGain, string reason)
        {
            Kind = kind;
            EndBloodReserved = endBloodReserved;
            ExpectedGain = expectedGain;
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class PermanentBloodSpellState
    {
        internal double Blood;
        internal int Difficulty;
        internal bool BloodFeatureUnlocked;
        internal bool EndBloodItemOwned;
        internal bool AlphaUnlocked;
        internal bool BetaUnlocked;
        internal bool AlphaFirstOnly;
        internal int RemainingSeconds;

        internal double MinimumIron;
        internal double MinimumAlpha;
        internal double MinimumBeta;
        internal int IronCooldown;
        internal int AlphaCooldown;
        internal int BetaCooldown;
        internal double IronElapsed;
        internal double AlphaElapsed;
        internal double BetaElapsed;
        internal float IronPillBonus;
        internal float BloodGuffBonus;

        internal float AdventureAttack;
        internal float AdventureDefense;
        internal float AdventureMaxHp;
        internal float AdventureRegen;

        internal object[] MacGuffinIdentities = new object[0];
        internal int[] MacGuffinIds = new int[0];
        internal int[] MacGuffinLevels = new int[0];
        internal bool[] ValidMacGuffins = new bool[0];
    }

    internal static class PermanentBloodSpellMechanics
    {
        // PlayerTime values continue to advance on Unity's clock while a synchronous native spell
        // call is dispatched and its post-state is captured. The debit and permanent effect vector
        // remain exact; only these clocks may move forward by this small settlement window.
        private const double TimerSettlementSeconds = 1.0;

        internal static PermanentBloodSpellDecision Select(PermanentBloodSpellState state)
        {
            if (state == null)
                return Decision(PermanentBloodSpellKind.None, false, 0,
                    "Blood spell state is unavailable.");
            if (state.Difficulty >= (int)difficulty.sadistic && !state.EndBloodItemOwned)
                return Decision(PermanentBloodSpellKind.None, true, 0,
                    "All Blood is reserved for missing Sadistic END item 494.");
            if (!state.BloodFeatureUnlocked || !FinitePositive(state.Blood))
                return Decision(PermanentBloodSpellKind.None, false, 0,
                    "Blood Magic is locked or the pool is not finite positive.");

            int gain;
            if (state.BetaUnlocked && state.Difficulty >= (int)difficulty.evil
                && Ready(state.BetaElapsed, state.BetaCooldown)
                && state.Blood >= state.MinimumBeta
                && WindowOpen(state.RemainingSeconds, state.BetaCooldown)
                && TryMacGuffinGain(state.Blood, state.MinimumBeta, 20.0, 1.0,
                    out gain) && CanApplyMacGuffinGain(state,
                    PermanentBloodSpellKind.MacGuffinBeta, gain))
                return Decision(PermanentBloodSpellKind.MacGuffinBeta, false, gain,
                    "Blood MacGuffin beta is source-ready and has a safe equipped target vector.");

            if (state.AlphaUnlocked && Ready(state.AlphaElapsed, state.AlphaCooldown)
                && state.Blood >= state.MinimumAlpha
                && WindowOpen(state.RemainingSeconds, state.AlphaCooldown)
                && TryMacGuffinGain(state.Blood, state.MinimumAlpha, 10.0,
                    state.BloodGuffBonus, out gain) && CanApplyMacGuffinGain(state,
                    PermanentBloodSpellKind.MacGuffinAlpha, gain))
                return Decision(PermanentBloodSpellKind.MacGuffinAlpha, false, gain,
                    "Blood MacGuffin alpha is source-ready and has a safe equipped target.");

            var iron = IronPillGain(state.Blood,
                state.Difficulty >= (int)difficulty.evil, state.IronPillBonus);
            if (Ready(state.IronElapsed, state.IronCooldown)
                && state.Blood >= state.MinimumIron && iron > 0f
                && WindowOpen(state.RemainingSeconds, state.IronCooldown))
                return Decision(PermanentBloodSpellKind.IronPill, false, 0,
                    "Iron Pill is source-ready for an exact permanent Adventure-stat gain.");

            return Decision(PermanentBloodSpellKind.None, false, 0,
                "No permanent Blood spell passed unlock, cooldown, pool, target, overflow, and run-window gates.");
        }

        internal static bool WindowOpen(int remainingSeconds, int cooldownSeconds)
        {
            return remainingSeconds == int.MaxValue || remainingSeconds <= 5
                   || cooldownSeconds >= 0 && remainingSeconds > cooldownSeconds + 5;
        }

        internal static float IronPillGain(double blood, bool evilOrLater, float perkBonus)
        {
            if (!FinitePositive(blood)) return 0f;
            var gain = (float)Math.Floor(Math.Pow(blood, 0.25));
            if (evilOrLater) gain *= perkBonus;
            if (gain >= 100000000f) gain = 100000000f;
            if (gain < 0f || float.IsNaN(gain)) gain = 0f;
            return gain;
        }

        internal static bool TryMacGuffinGain(double blood, double minimum, double basis,
            double multiplier, out int gain)
        {
            gain = 0;
            if (!FinitePositive(blood) || !FinitePositive(minimum) || basis <= 1.0
                || !FinitePositive(multiplier) || blood < minimum)
                return false;
            var raw = (Math.Log(blood / minimum, basis) + 1.0) * multiplier;
            if (double.IsNaN(raw) || double.IsInfinity(raw) || raw < 1.0
                || raw > int.MaxValue)
                return false;
            gain = (int)raw;
            return gain > 0;
        }

        internal static bool Verify(PermanentBloodSpellKind kind,
            PermanentBloodSpellState before, PermanentBloodSpellState after,
            out string reason)
        {
            reason = string.Empty;
            if (kind == PermanentBloodSpellKind.None || before == null || after == null)
            {
                reason = "A selected spell and complete before/after states are required.";
                return false;
            }
            if (after.Blood != 0.0)
            {
                reason = "The native permanent Blood spell did not debit the exact full pool.";
                return false;
            }
            if (!SamePhysicalTopology(before, after))
            {
                reason = "Equipped MacGuffin identity/ID topology changed during the spell.";
                return false;
            }

            switch (kind)
            {
                case PermanentBloodSpellKind.IronPill:
                    return VerifyIron(before, after, out reason);
                case PermanentBloodSpellKind.MacGuffinAlpha:
                    return VerifyAlpha(before, after, out reason);
                case PermanentBloodSpellKind.MacGuffinBeta:
                    return VerifyBeta(before, after, out reason);
                default:
                    reason = "Unknown permanent Blood spell kind.";
                    return false;
            }
        }

        internal static bool Same(PermanentBloodSpellState a, PermanentBloodSpellState b)
        {
            if (a == null || b == null || !SamePhysicalTopology(a, b)) return false;
            if (a.Blood != b.Blood || a.Difficulty != b.Difficulty
                || a.BloodFeatureUnlocked != b.BloodFeatureUnlocked
                || a.EndBloodItemOwned != b.EndBloodItemOwned
                || a.AlphaUnlocked != b.AlphaUnlocked || a.BetaUnlocked != b.BetaUnlocked
                || a.AlphaFirstOnly != b.AlphaFirstOnly
                || !SameRemainingWindow(a.RemainingSeconds, b.RemainingSeconds)
                || a.MinimumIron != b.MinimumIron || a.MinimumAlpha != b.MinimumAlpha
                || a.MinimumBeta != b.MinimumBeta || a.IronCooldown != b.IronCooldown
                || a.AlphaCooldown != b.AlphaCooldown || a.BetaCooldown != b.BetaCooldown
                || !TimerAdvancedWithinSettlement(a.IronElapsed, b.IronElapsed)
                || !TimerAdvancedWithinSettlement(a.AlphaElapsed, b.AlphaElapsed)
                || !TimerAdvancedWithinSettlement(a.BetaElapsed, b.BetaElapsed)
                || a.IronPillBonus != b.IronPillBonus
                || a.BloodGuffBonus != b.BloodGuffBonus
                || a.AdventureAttack != b.AdventureAttack
                || a.AdventureDefense != b.AdventureDefense
                || a.AdventureMaxHp != b.AdventureMaxHp
                || a.AdventureRegen != b.AdventureRegen)
                return false;
            for (var i = 0; i < a.MacGuffinLevels.Length; i++)
                if (a.MacGuffinLevels[i] != b.MacGuffinLevels[i]) return false;
            return true;
        }

        private static bool VerifyIron(PermanentBloodSpellState before,
            PermanentBloodSpellState after, out string reason)
        {
            var gain = IronPillGain(before.Blood,
                before.Difficulty >= (int)difficulty.evil, before.IronPillBonus);
            if (gain <= 0f || !ResetTimerObserved(after.IronElapsed)
                || !TimerAdvancedWithinSettlement(before.AlphaElapsed, after.AlphaElapsed)
                || !TimerAdvancedWithinSettlement(before.BetaElapsed, after.BetaElapsed)
                || after.AdventureAttack != before.AdventureAttack + gain
                || after.AdventureDefense != before.AdventureDefense + gain
                || after.AdventureMaxHp != before.AdventureMaxHp + gain * 3f
                || after.AdventureRegen != before.AdventureRegen + gain * 0.03f
                || !SameMacGuffinLevels(before, after))
            {
                reason = "Iron Pill lacked its exact attack/defense/HP/regen gain or cooldown reset.";
                return false;
            }
            reason = "Exact full-pool Iron Pill debit and permanent Adventure-stat vector confirmed.";
            return true;
        }

        private static bool VerifyAlpha(PermanentBloodSpellState before,
            PermanentBloodSpellState after, out string reason)
        {
            int gain;
            if (!TryMacGuffinGain(before.Blood, before.MinimumAlpha, 10.0,
                    before.BloodGuffBonus, out gain)
                || !ResetTimerObserved(after.AlphaElapsed)
                || !TimerAdvancedWithinSettlement(before.IronElapsed, after.IronElapsed)
                || !TimerAdvancedWithinSettlement(before.BetaElapsed, after.BetaElapsed)
                || !SameAdventure(before, after))
            {
                reason = "MacGuffin alpha lacked its exact gain inputs, cooldown reset, or state isolation.";
                return false;
            }
            var changed = 0;
            for (var i = 0; i < before.MacGuffinLevels.Length; i++)
            {
                var expectedTarget = before.AlphaFirstOnly ? i == 0 : before.ValidMacGuffins[i];
                var delta = (long)after.MacGuffinLevels[i] - before.MacGuffinLevels[i];
                if (before.AlphaFirstOnly)
                {
                    if (delta != (expectedTarget ? gain : 0L))
                    {
                        reason = "MacGuffin alpha did not level only the source-selected first slot.";
                        return false;
                    }
                    if (expectedTarget) changed++;
                }
                else if (delta == gain && expectedTarget) changed++;
                else if (delta != 0L)
                {
                    reason = "MacGuffin alpha changed more than one valid random target or the wrong amount.";
                    return false;
                }
            }
            if (changed != 1)
            {
                reason = "MacGuffin alpha did not produce exactly one permanent equipped-item gain.";
                return false;
            }
            reason = "Exact full-pool MacGuffin alpha debit, one-item gain, and cooldown reset confirmed.";
            return true;
        }

        private static bool VerifyBeta(PermanentBloodSpellState before,
            PermanentBloodSpellState after, out string reason)
        {
            int gain;
            if (!TryMacGuffinGain(before.Blood, before.MinimumBeta, 20.0, 1.0,
                    out gain) || !ResetTimerObserved(after.BetaElapsed)
                || !TimerAdvancedWithinSettlement(before.IronElapsed, after.IronElapsed)
                || !TimerAdvancedWithinSettlement(before.AlphaElapsed, after.AlphaElapsed)
                || !SameAdventure(before, after))
            {
                reason = "MacGuffin beta lacked its exact gain inputs, cooldown reset, or state isolation.";
                return false;
            }
            var targets = 0;
            for (var i = 0; i < before.MacGuffinLevels.Length; i++)
            {
                var delta = (long)after.MacGuffinLevels[i] - before.MacGuffinLevels[i];
                if (delta != (before.ValidMacGuffins[i] ? gain : 0L))
                {
                    reason = "MacGuffin beta did not level every and only valid equipped MacGuffin by the exact gain.";
                    return false;
                }
                if (before.ValidMacGuffins[i]) targets++;
            }
            if (targets <= 0)
            {
                reason = "MacGuffin beta had no permanent physical target.";
                return false;
            }
            reason = "Exact full-pool MacGuffin beta debit, all-item gain vector, and cooldown reset confirmed.";
            return true;
        }

        private static bool CanApplyMacGuffinGain(PermanentBloodSpellState state,
            PermanentBloodSpellKind kind, int gain)
        {
            if (gain <= 0 || !MacArraysComplete(state)) return false;
            var targets = 0;
            for (var i = 0; i < state.MacGuffinLevels.Length; i++)
            {
                var target = kind == PermanentBloodSpellKind.MacGuffinBeta
                    ? state.ValidMacGuffins[i]
                    : state.AlphaFirstOnly ? i == 0 && state.ValidMacGuffins[i]
                    : state.ValidMacGuffins[i];
                if (!target) continue;
                targets++;
                if (state.MacGuffinLevels[i] < 0
                    || state.MacGuffinLevels[i] > int.MaxValue - gain) return false;
            }
            return targets > 0;
        }

        private static bool SamePhysicalTopology(PermanentBloodSpellState a,
            PermanentBloodSpellState b)
        {
            if (!MacArraysComplete(a) || !MacArraysComplete(b)
                || a.MacGuffinIdentities.Length != b.MacGuffinIdentities.Length)
                return false;
            for (var i = 0; i < a.MacGuffinIdentities.Length; i++)
                if (!object.ReferenceEquals(a.MacGuffinIdentities[i], b.MacGuffinIdentities[i])
                    || a.MacGuffinIds[i] != b.MacGuffinIds[i]
                    || a.ValidMacGuffins[i] != b.ValidMacGuffins[i]) return false;
            return true;
        }

        private static bool SameMacGuffinLevels(PermanentBloodSpellState a,
            PermanentBloodSpellState b)
        {
            if (!SamePhysicalTopology(a, b)) return false;
            for (var i = 0; i < a.MacGuffinLevels.Length; i++)
                if (a.MacGuffinLevels[i] != b.MacGuffinLevels[i]) return false;
            return true;
        }

        private static bool SameAdventure(PermanentBloodSpellState a,
            PermanentBloodSpellState b)
        {
            return a.AdventureAttack == b.AdventureAttack
                   && a.AdventureDefense == b.AdventureDefense
                   && a.AdventureMaxHp == b.AdventureMaxHp
                   && a.AdventureRegen == b.AdventureRegen;
        }

        private static bool MacArraysComplete(PermanentBloodSpellState state)
        {
            return state != null && state.MacGuffinIdentities != null
                   && state.MacGuffinIds != null && state.MacGuffinLevels != null
                   && state.ValidMacGuffins != null
                   && state.MacGuffinIdentities.Length == state.MacGuffinIds.Length
                   && state.MacGuffinIdentities.Length == state.MacGuffinLevels.Length
                   && state.MacGuffinIdentities.Length == state.ValidMacGuffins.Length;
        }

        private static bool Ready(double elapsed, int cooldown)
        {
            return !double.IsNaN(elapsed) && !double.IsInfinity(elapsed)
                   && cooldown >= 0 && elapsed >= cooldown;
        }

        internal static bool ResetTimerObserved(double elapsed)
        {
            return !double.IsNaN(elapsed) && !double.IsInfinity(elapsed)
                   && elapsed >= 0.0 && elapsed <= TimerSettlementSeconds;
        }

        internal static bool TimerAdvancedWithinSettlement(double before, double after)
        {
            return !double.IsNaN(before) && !double.IsInfinity(before)
                   && !double.IsNaN(after) && !double.IsInfinity(after)
                   && after >= before && after - before <= TimerSettlementSeconds;
        }

        private static bool SameRemainingWindow(int before, int after)
        {
            if (before == int.MaxValue || after == int.MaxValue) return before == after;
            return after <= before && (long)before - after <= 1L;
        }

        private static bool FinitePositive(double value)
        {
            return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static PermanentBloodSpellDecision Decision(PermanentBloodSpellKind kind,
            bool reserved, int gain, string reason)
        {
            return new PermanentBloodSpellDecision(kind, reserved, gain, reason);
        }
    }

    internal static class PermanentBloodSpellManager
    {
        internal static MutationResult Manage(RootTransaction root, Character character,
            AutopilotConfig config, AutopilotPlan plan)
        {
            if (root == null || root.IsClosed || character == null || config == null
                || !config.ManageBloodMagic)
                return null;
            var before = Capture(character, plan);
            var decision = PermanentBloodSpellMechanics.Select(before);
            if (decision.EndBloodReserved)
            {
                ExecutionSafety.ReportHold("permanent-blood:end-reserve", decision.Reason, 60);
                return null;
            }
            if (decision.Kind == PermanentBloodSpellKind.None) return null;
            return root.ExecuteChild(new PermanentBloodSpellIntent(character, config, plan,
                decision.Kind));
        }

        private sealed class PermanentBloodSpellIntent :
            IMutationIntent<PermanentBloodSpellState, bool, PermanentBloodSpellState>
        {
            private readonly Character _character;
            private readonly AutopilotConfig _config;
            private readonly AutopilotPlan _plan;
            private readonly PermanentBloodSpellKind _kind;

            internal PermanentBloodSpellIntent(Character character, AutopilotConfig config,
                AutopilotPlan plan, PermanentBloodSpellKind kind)
            {
                _character = character;
                _config = config;
                _plan = plan;
                _kind = kind;
            }

            public string Id { get { return "permanent-blood." + _kind; } }
            public MutationClass Class { get { return MutationClass.BloodMagic; } }
            public MutationRisk Risk { get { return MutationRisk.FiniteResource; } }
            public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
            public string BindingId
            {
                get
                {
                    return "RebirthPowerSpell."
                           + (_kind == PermanentBloodSpellKind.IronPill
                               ? "castAdventurePowerupSpell()"
                               : _kind == PermanentBloodSpellKind.MacGuffinAlpha
                                   ? "castMacguffin1Spell()" : "castMacguffin2Spell()")
                           + "/public-exact";
                }
            }
            public bool Required { get { return false; } }
            public bool CanCompensate { get { return false; } }
            public bool CreatesNewEpoch { get { return false; } }
            public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

            public PermanentBloodSpellState CaptureBefore(MutationContext context)
            {
                return Capture(_character, _plan);
            }

            public PreconditionResult CheckPreconditions(MutationContext context,
                PermanentBloodSpellState before)
            {
                if (!Main.IsAutomationReady || _config == null || !_config.ManageBloodMagic)
                    return PreconditionResult.Hold(
                        "Gameplay synchronization or ManageBloodMagic authority is unavailable.");
                var decision = PermanentBloodSpellMechanics.Select(before);
                if (decision.EndBloodReserved)
                    return PreconditionResult.Hold(decision.Reason);
                return decision.Kind == _kind
                    ? PreconditionResult.Ready()
                    : PreconditionResult.Hold("Selected permanent Blood spell changed before Apply: "
                                              + decision.Reason);
            }

            public bool Apply(MutationContext context, RootTransactionToken token,
                PermanentBloodSpellState before)
            {
                switch (_kind)
                {
                    case PermanentBloodSpellKind.IronPill:
                        _character.bloodSpells.castAdventurePowerupSpell();
                        break;
                    case PermanentBloodSpellKind.MacGuffinAlpha:
                        _character.bloodSpells.castMacguffin1Spell();
                        break;
                    case PermanentBloodSpellKind.MacGuffinBeta:
                        _character.bloodSpells.castMacguffin2Spell();
                        break;
                    default:
                        return false;
                }
                return true;
            }

            public VerificationResult<PermanentBloodSpellState> Verify(MutationContext context,
                PermanentBloodSpellState before, MutationApplyObservation<bool> apply)
            {
                var after = Capture(_character, _plan);
                var reason = string.Empty;
                if (!apply.ReturnedNormally || !apply.Value
                    || !PermanentBloodSpellMechanics.Verify(_kind, before, after, out reason))
                {
                    var failure = string.IsNullOrEmpty(reason)
                        ? "Native permanent Blood spell did not return normally." : reason;
                    Main.LogAction("REJECTED", "Permanent Blood " + Label(_kind)
                        + " verification failed: " + failure
                        + " [before " + Fingerprint(before) + "; after "
                        + Fingerprint(after) + "]");
                    return VerificationResult<PermanentBloodSpellState>.Failed(
                        failure);
                }
                Main.LogAction("BLOOD", "Cast " + Label(_kind) + " using the exact full Blood pool"
                    + (_kind == PermanentBloodSpellKind.IronPill ? string.Empty
                        : "; exact MacGuffin gain="
                          + PermanentBloodSpellMechanics.Select(before).ExpectedGain)
                    + " [typed debit, cooldown, identity, and permanent effect confirmed]");
                return VerificationResult<PermanentBloodSpellState>.Satisfied(after, reason);
            }

            public CompensationResult Compensate(MutationContext context, RecoveryToken token,
                PermanentBloodSpellState before, MutationApplyObservation<bool> apply)
            {
                return CompensationResult.NotSupported(
                    "A consumed full Blood pool and permanent spell effect have no exact inverse.");
            }

            public bool BeforeStateMatches(PermanentBloodSpellState expected,
                PermanentBloodSpellState observed)
            {
                return PermanentBloodSpellMechanics.Same(expected, observed);
            }

            public string FingerprintBefore(PermanentBloodSpellState state)
            {
                return Fingerprint(state);
            }

            public string FingerprintAfter(PermanentBloodSpellState state)
            {
                return Fingerprint(state);
            }
        }

        private static PermanentBloodSpellState Capture(Character c, AutopilotPlan plan)
        {
            if (c == null || c.bloodMagic == null || c.bloodSpells == null
                || c.bloodMagic.adventureSpellTime == null
                || c.bloodMagic.macguffin1Time == null
                || c.bloodMagic.macguffin2Time == null
                || c.settings == null || c.buttons == null || c.buttons.bloodMagic == null
                || c.adventure == null || c.adventure.itopod == null
                || c.adventureController == null || c.adventureController.itopod == null
                || c.inventory == null || c.inventory.macguffins == null
                || c.inventoryController == null || c.wishes == null
                || c.wishes.wishes == null || c.wishes.wishes.Count <= 24
                || c.wishesController == null)
                return null;
            try
            {
                var equipped = c.inventory.macguffins;
                var count = equipped.Count;
                var identities = new object[count];
                var ids = new int[count];
                var levels = new int[count];
                var valid = new bool[count];
                for (var i = 0; i < count; i++)
                {
                    var item = equipped[i];
                    identities[i] = item;
                    ids[i] = item == null ? 0 : item.id;
                    levels[i] = item == null ? 0 : item.level;
                    valid[i] = item != null && item.id > 0 && item.isMacGuffin();
                }

                var remaining = int.MaxValue;
                if (plan != null && !plan.RebirthExecutionHold && plan.RebirthSeconds > 0)
                {
                    if (c.rebirthTime == null) return null;
                    var raw = (long)plan.RebirthSeconds
                              - (long)Math.Floor(c.rebirthTime.totalseconds);
                    remaining = raw <= int.MinValue ? int.MinValue
                        : raw >= int.MaxValue ? int.MaxValue : (int)raw;
                }
                var perks = c.adventure.itopod.perkLevel;
                var wishes = c.wishes.wishes;
                if (perks == null) return null;
                return new PermanentBloodSpellState
                {
                    Blood = c.bloodMagic.bloodPoints,
                    Difficulty = (int)c.settings.rebirthDifficulty,
                    BloodFeatureUnlocked = c.highestBoss >= 37
                                           && c.buttons.bloodMagic.interactable,
                    EndBloodItemOwned = EndgameDependencyModel.IsOwned(c,
                        EndgameTransactionMechanics.EndBloodItemId),
                    AlphaUnlocked = perks.Count > 72 && perks[72] >= 1,
                    BetaUnlocked = perks.Count > 73 && perks[73] >= 1,
                    AlphaFirstOnly = wishes[24].level > 0,
                    RemainingSeconds = remaining,
                    MinimumIron = c.bloodSpells.minAdventureBlood(),
                    MinimumAlpha = c.bloodSpells.minMacguffin1Blood(),
                    MinimumBeta = c.bloodSpells.minMacguffin2Blood(),
                    IronCooldown = c.bloodSpells.adventureSpellCooldown,
                    AlphaCooldown = c.bloodSpells.macguffin1Cooldown,
                    BetaCooldown = c.bloodSpells.macguffin2Cooldown,
                    IronElapsed = c.bloodMagic.adventureSpellTime.totalseconds,
                    AlphaElapsed = c.bloodMagic.macguffin1Time.totalseconds,
                    BetaElapsed = c.bloodMagic.macguffin2Time.totalseconds,
                    IronPillBonus = c.settings.rebirthDifficulty >= difficulty.evil
                        ? c.adventureController.itopod.ironPillBonus() : 1f,
                    BloodGuffBonus = c.wishesController.totalBloodGuffbonus(),
                    AdventureAttack = c.adventure.attack,
                    AdventureDefense = c.adventure.defense,
                    AdventureMaxHp = c.adventure.maxHP,
                    AdventureRegen = c.adventure.regen,
                    MacGuffinIdentities = identities,
                    MacGuffinIds = ids,
                    MacGuffinLevels = levels,
                    ValidMacGuffins = valid
                };
            }
            catch
            {
                // Initial selection and coordinator recapture both fail closed on partial Unity
                // topology. No spell controller is invoked from an incomplete snapshot.
                return null;
            }
        }

        private static string Label(PermanentBloodSpellKind kind)
        {
            return kind == PermanentBloodSpellKind.IronPill ? "Iron Pill"
                : kind == PermanentBloodSpellKind.MacGuffinAlpha
                    ? "Blood MacGuffin alpha" : "Blood MacGuffin beta";
        }

        private static string Fingerprint(PermanentBloodSpellState state)
        {
            if (state == null) return "missing";
            var text = new StringBuilder();
            text.Append("blood=").Append(state.Blood.ToString("R", CultureInfo.InvariantCulture))
                .Append(";timers=").Append(state.IronElapsed.ToString("R",
                    CultureInfo.InvariantCulture)).Append(',')
                .Append(state.AlphaElapsed.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(state.BetaElapsed.ToString("R", CultureInfo.InvariantCulture))
                .Append(";adventure=").Append(state.AdventureAttack.ToString("R",
                    CultureInfo.InvariantCulture)).Append(',')
                .Append(state.AdventureDefense.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(state.AdventureMaxHp.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(state.AdventureRegen.ToString("R", CultureInfo.InvariantCulture));
            for (var i = 0; i < state.MacGuffinLevels.Length; i++)
                text.Append(";guff.").Append(i).Append('=')
                    .Append(state.MacGuffinIdentities[i] == null ? 0
                        : RuntimeHelpers.GetHashCode(state.MacGuffinIdentities[i]))
                    .Append('/').Append(state.MacGuffinIds[i]).Append('/')
                    .Append(state.MacGuffinLevels[i]).Append('/')
                    .Append(state.ValidMacGuffins[i] ? '1' : '0');
            return text.ToString();
        }
    }
}
