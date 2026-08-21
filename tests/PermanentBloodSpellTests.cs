using System;
using System.IO;
using NGUInjector.Autopilot;

/*
FILE PURPOSE

PermanentBloodSpellTests is the controller-free golden suite for the typed live Iron Pill and
MacGuffin alpha/beta Blood child. It protects terminal-item reservation, exact cooldown boundaries,
one-spell priority, remaining-run windows, native gain formulas, full-pool debit, physical identity,
spell-specific permanent vectors, and the explicit exclusion of heuristic Loot/Gold casts. The
full-source compile supplies game types, but these cases invoke only the pure policy/verifier and
never load a save or call a native controller.
*/
internal static class PermanentBloodSpellTests
{
    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception(message);
    }

    private static PermanentBloodSpellState Ready()
    {
        var identities = new object[] {new object(), new object(), new object()};
        return new PermanentBloodSpellState
        {
            Blood = 20000000.0,
            Difficulty = (int)difficulty.evil,
            BloodFeatureUnlocked = true,
            EndBloodItemOwned = true,
            AlphaUnlocked = true,
            BetaUnlocked = true,
            AlphaFirstOnly = false,
            RemainingSeconds = int.MaxValue,
            MinimumIron = 100.0,
            MinimumAlpha = 1000000000.0,
            MinimumBeta = 1000000.0,
            IronCooldown = 3600,
            AlphaCooldown = 3600,
            BetaCooldown = 3600,
            IronElapsed = 3600.0,
            AlphaElapsed = 3600.0,
            BetaElapsed = 3600.0,
            IronPillBonus = 2f,
            BloodGuffBonus = 1.5f,
            AdventureAttack = 100f,
            AdventureDefense = 200f,
            AdventureMaxHp = 300f,
            AdventureRegen = 4f,
            MacGuffinIdentities = identities,
            MacGuffinIds = new[] {300, 301, 0},
            MacGuffinLevels = new[] {10, 20, 0},
            ValidMacGuffins = new[] {true, true, false}
        };
    }

    private static PermanentBloodSpellState Clone(PermanentBloodSpellState value)
    {
        return new PermanentBloodSpellState
        {
            Blood = value.Blood,
            Difficulty = value.Difficulty,
            BloodFeatureUnlocked = value.BloodFeatureUnlocked,
            EndBloodItemOwned = value.EndBloodItemOwned,
            AlphaUnlocked = value.AlphaUnlocked,
            BetaUnlocked = value.BetaUnlocked,
            AlphaFirstOnly = value.AlphaFirstOnly,
            RemainingSeconds = value.RemainingSeconds,
            MinimumIron = value.MinimumIron,
            MinimumAlpha = value.MinimumAlpha,
            MinimumBeta = value.MinimumBeta,
            IronCooldown = value.IronCooldown,
            AlphaCooldown = value.AlphaCooldown,
            BetaCooldown = value.BetaCooldown,
            IronElapsed = value.IronElapsed,
            AlphaElapsed = value.AlphaElapsed,
            BetaElapsed = value.BetaElapsed,
            IronPillBonus = value.IronPillBonus,
            BloodGuffBonus = value.BloodGuffBonus,
            AdventureAttack = value.AdventureAttack,
            AdventureDefense = value.AdventureDefense,
            AdventureMaxHp = value.AdventureMaxHp,
            AdventureRegen = value.AdventureRegen,
            MacGuffinIdentities = (object[])value.MacGuffinIdentities.Clone(),
            MacGuffinIds = (int[])value.MacGuffinIds.Clone(),
            MacGuffinLevels = (int[])value.MacGuffinLevels.Clone(),
            ValidMacGuffins = (bool[])value.ValidMacGuffins.Clone()
        };
    }

    private static void TestSelectionAndReservation()
    {
        var state = Ready();
        var selected = PermanentBloodSpellMechanics.Select(state);
        Assert(selected.Kind == PermanentBloodSpellKind.MacGuffinBeta
               && selected.ExpectedGain == 2,
            "ready beta must outrank alpha and Iron Pill by exact source priority");

        state.Difficulty = (int)difficulty.sadistic;
        state.EndBloodItemOwned = false;
        selected = PermanentBloodSpellMechanics.Select(state);
        Assert(selected.Kind == PermanentBloodSpellKind.None && selected.EndBloodReserved,
            "missing Sadistic END item 494 must reserve every Blood amount before repeatable spells");

        state = Ready();
        state.BetaElapsed = state.BetaCooldown - 0.001;
        state.Blood = 10000000000.0;
        selected = PermanentBloodSpellMechanics.Select(state);
        Assert(selected.Kind == PermanentBloodSpellKind.MacGuffinAlpha
               && selected.ExpectedGain == 3,
            "beta below exact cooldown boundary must yield to ready alpha");

        state.AlphaUnlocked = false;
        selected = PermanentBloodSpellMechanics.Select(state);
        Assert(selected.Kind == PermanentBloodSpellKind.IronPill,
            "ready Iron Pill must be the bounded fallback after unavailable MacGuffin spells");

        state.RemainingSeconds = 100;
        selected = PermanentBloodSpellMechanics.Select(state);
        Assert(selected.Kind == PermanentBloodSpellKind.None,
            "one-cast mid-window must retain the pool for a stronger boundary cast");
        state.RemainingSeconds = 5;
        Assert(PermanentBloodSpellMechanics.Select(state).Kind
               == PermanentBloodSpellKind.IronPill,
            "the final five-second boundary may consume the maximized pool");
        Assert(PermanentBloodSpellMechanics.WindowOpen(3606, 3600)
               && !PermanentBloodSpellMechanics.WindowOpen(3605, 3600),
            "cooldown-repayment window is strict and source-second exact");
    }

    private static void TestNativeGainFormulae()
    {
        Assert(PermanentBloodSpellMechanics.IronPillGain(10000.0, false, 99f) == 10f,
            "Normal Iron Pill is floor(Blood^0.25) without an Evil perk multiplier");
        Assert(PermanentBloodSpellMechanics.IronPillGain(10000.0, true, 2f) == 20f,
            "Evil Iron Pill applies the native perk multiplier after the fourth root floor");
        Assert(PermanentBloodSpellMechanics.IronPillGain(1e40, true, 10f) == 100000000f,
            "Iron Pill respects the native 1e8 permanent-stat cap");
        int gain;
        Assert(PermanentBloodSpellMechanics.TryMacGuffinGain(1e10, 1e9, 10.0,
                   1.5, out gain) && gain == 3,
            "alpha is int((log10(pool/min)+1)*BloodGuffBonus)");
        Assert(PermanentBloodSpellMechanics.TryMacGuffinGain(2e7, 1e6, 20.0,
                   1.0, out gain) && gain == 2,
            "beta is int(log20(pool/min)+1)");
        Assert(!PermanentBloodSpellMechanics.TryMacGuffinGain(99.0, 100.0, 10.0,
                   1.0, out gain),
            "below-minimum Blood cannot create a phantom MacGuffin level");
    }

    private static void TestIronSettlement()
    {
        var before = Ready();
        before.BetaUnlocked = false;
        before.AlphaUnlocked = false;
        before.Blood = 10000.0;
        var after = Clone(before);
        var gain = PermanentBloodSpellMechanics.IronPillGain(before.Blood, true,
            before.IronPillBonus);
        after.Blood = 0.0;
        after.IronElapsed = 0.0;
        after.AdventureAttack += gain;
        after.AdventureDefense += gain;
        after.AdventureMaxHp += gain * 3f;
        after.AdventureRegen += gain * 0.03f;
        after.IronElapsed = 0.02;
        after.AlphaElapsed += 0.02;
        after.BetaElapsed += 0.02;
        string reason;
        Assert(PermanentBloodSpellMechanics.Verify(PermanentBloodSpellKind.IronPill,
                   before, after, out reason),
            "exact Iron Pill vector must settle across one bounded native clock tick");
        after.Blood = 1.0;
        Assert(!PermanentBloodSpellMechanics.Verify(PermanentBloodSpellKind.IronPill,
                   before, after, out reason) && reason.Contains("full pool"),
            "partial Blood debit must never settle as a permanent spell");
        after.Blood = 0.0;
        after.AdventureDefense += 1f;
        Assert(!PermanentBloodSpellMechanics.Verify(PermanentBloodSpellKind.IronPill,
                   before, after, out reason),
            "one wrong Iron Pill permanent stat must fail the full vector");

        var stable = Clone(before);
        stable.RemainingSeconds = 100;
        var unchanged = Clone(stable);
        unchanged.IronElapsed += 0.02;
        unchanged.AlphaElapsed += 0.02;
        unchanged.BetaElapsed += 0.02;
        unchanged.RemainingSeconds--;
        Assert(PermanentBloodSpellMechanics.Same(stable, unchanged),
            "unchanged-state proof must ignore only bounded forward clock drift");
        unchanged.BetaElapsed = stable.BetaElapsed - 0.01;
        Assert(!PermanentBloodSpellMechanics.Same(stable, unchanged),
            "unchanged-state proof must reject a cooldown clock moving backward");
        unchanged = Clone(stable);
        unchanged.IronElapsed += 1.01;
        Assert(!PermanentBloodSpellMechanics.Same(stable, unchanged),
            "unchanged-state proof must reject clock drift outside settlement");

        // Regression copied verbatim from the first live typed Iron Pill settlement.  All four
        // native float fields moved by the source-defined vector, while the two still-locked
        // spell timers remained at zero.
        var liveBefore = Ready();
        liveBefore.Difficulty = (int)difficulty.normal;
        liveBefore.Blood = 185.0;
        liveBefore.IronPillBonus = 1f;
        liveBefore.IronElapsed = 41400.006944957771;
        liveBefore.AlphaElapsed = 0.0;
        liveBefore.BetaElapsed = 0.0;
        liveBefore.AdventureAttack = 153f;
        liveBefore.AdventureDefense = 149f;
        liveBefore.AdventureMaxHp = 291f;
        liveBefore.AdventureRegen = 3.75999951f;
        var liveAfter = Clone(liveBefore);
        liveAfter.Blood = 0.0;
        liveAfter.IronElapsed = 0.0;
        liveAfter.AdventureAttack = 156f;
        liveAfter.AdventureDefense = 152f;
        liveAfter.AdventureMaxHp = 300f;
        liveAfter.AdventureRegen = 3.84999943f;
        Assert(PermanentBloodSpellMechanics.Verify(PermanentBloodSpellKind.IronPill,
                   liveBefore, liveAfter, out reason),
            "the observed live Normal Iron Pill vector must settle exactly");

        liveBefore.Difficulty = (int)difficulty.evil;
        liveBefore.IronPillBonus = 2f;
        Assert(PermanentBloodSpellMechanics.Verify(PermanentBloodSpellKind.IronPill,
                   liveBefore, liveAfter, out reason)
               && reason.Contains("prediction quote drifted"),
            "a complete source-specific native vector must settle when only the perk quote drifted");
        liveAfter.AdventureDefense += 1f;
        Assert(!PermanentBloodSpellMechanics.Verify(PermanentBloodSpellKind.IronPill,
                   liveBefore, liveAfter, out reason),
            "quote-drift settlement must still reject an inconsistent permanent stat vector");
    }

    private static void TestMacGuffinSettlement()
    {
        string reason;
        var betaBefore = Ready();
        var betaAfter = Clone(betaBefore);
        betaAfter.Blood = 0.0;
        betaAfter.BetaElapsed = 0.02;
        betaAfter.IronElapsed += 0.02;
        betaAfter.AlphaElapsed += 0.02;
        betaAfter.MacGuffinLevels[0] += 2;
        betaAfter.MacGuffinLevels[1] += 2;
        Assert(PermanentBloodSpellMechanics.Verify(
                   PermanentBloodSpellKind.MacGuffinBeta, betaBefore, betaAfter, out reason),
            "beta must settle only after every valid equipped physical MacGuffin gains exactly two");
        betaAfter.MacGuffinLevels[1]--;
        Assert(!PermanentBloodSpellMechanics.Verify(
                   PermanentBloodSpellKind.MacGuffinBeta, betaBefore, betaAfter, out reason),
            "beta must reject one under-levelled equipped MacGuffin");

        var alphaBefore = Ready();
        alphaBefore.Blood = 1e10;
        alphaBefore.BetaUnlocked = false;
        var alphaAfter = Clone(alphaBefore);
        alphaAfter.Blood = 0.0;
        alphaAfter.AlphaElapsed = 0.02;
        alphaAfter.IronElapsed += 0.02;
        alphaAfter.BetaElapsed += 0.02;
        alphaAfter.MacGuffinLevels[1] += 3;
        Assert(PermanentBloodSpellMechanics.Verify(
                   PermanentBloodSpellKind.MacGuffinAlpha, alphaBefore, alphaAfter, out reason),
            "random alpha must settle one and only one valid physical target by the exact gain");

        alphaBefore.AlphaFirstOnly = true;
        alphaAfter = Clone(alphaBefore);
        alphaAfter.Blood = 0.0;
        alphaAfter.AlphaElapsed = 0.0;
        alphaAfter.MacGuffinLevels[0] += 3;
        Assert(PermanentBloodSpellMechanics.Verify(
                   PermanentBloodSpellKind.MacGuffinAlpha, alphaBefore, alphaAfter, out reason),
            "Wish-24 alpha must settle only the source-selected first equipped slot");
        alphaAfter.MacGuffinIdentities[0] = new object();
        Assert(!PermanentBloodSpellMechanics.Verify(
                   PermanentBloodSpellKind.MacGuffinAlpha, alphaBefore, alphaAfter, out reason)
               && reason.Contains("identity"),
            "physical MacGuffin replacement cannot masquerade as a spell level gain");
    }

    private static void TestLiveSourceBoundary()
    {
        var source = File.ReadAllText("source/Autopilot/PermanentBloodSpellManager.cs");
        Assert(source.Contains("root.ExecuteChild(new PermanentBloodSpellIntent")
               && source.Contains("MutationClass.BloodMagic")
               && source.Contains("CanCompensate { get { return false; } }"),
            "live permanent Blood path must be one non-compensable typed root child");
        Assert(source.Contains("castAdventurePowerupSpell()")
               && source.Contains("castMacguffin1Spell()")
               && source.Contains("castMacguffin2Spell()"),
            "bounded live path includes exactly the three permanent native spell controllers");
        Assert(!source.Contains(".castLootSpell(") && !source.Contains(".castGoldSpell(")
               && !source.Contains(".castRebirthSpell(") && !source.Contains(".castEndSpell("),
            "permanent manager must not execute heuristic run-local, NUMBER, or END Blood spells");
        Assert(!source.Contains("bloodPoints =") && !source.Contains("adventure.attack =")
               && !source.Contains("MacGuffinLevels[i] ="),
            "live permanent manager must not rewrite native Blood/effect fields");
        var main = File.ReadAllText("source/Main.cs");
        var permanent = main.IndexOf("PermanentBloodSpellManager.Manage(mutationRoot",
            StringComparison.Ordinal);
        var terminal = main.IndexOf("_endgameTransactions.TryDeliverEndBlood(mutationRoot)",
            StringComparison.Ordinal);
        var rebirth = main.IndexOf("Autopilot.ExecuteOrdinaryRebirth(mutationRoot)",
            StringComparison.Ordinal);
        Assert(permanent >= 0 && terminal > permanent && rebirth > terminal,
            "live root settles permanent Blood, then terminal item 494, before ordinary rebirth");
        Assert(source.Contains("c.settings == null")
               && source.Contains("c.adventureController == null")
               && source.Contains("c.adventureController.itopod == null")
               && source.Contains("c.wishesController == null")
               && source.Contains("c.bloodMagic.adventureSpellTime == null")
               && source.Contains("c.bloodMagic.macguffin1Time == null")
               && source.Contains("c.bloodMagic.macguffin2Time == null")
               && source.Contains("catch") && source.Contains("return null;"),
            "partial live controller/timer topology must fail snapshot capture closed");

        var coordinator = File.ReadAllText("source/Autopilot/MutationCoordinator.cs");
        Assert(coordinator.Contains("result.Risk == MutationRisk.Irreversible || !intent.CanCompensate")
               && coordinator.Contains("QuarantineClass(intent.Class")
               && coordinator.Contains("\" + failure")
               && coordinator.Contains("MutationResultKind.Quarantined"),
            "a changed failed postcondition with no compensation must quarantine BloodMagic even at FiniteResource risk");
        Assert(source.Contains("Permanent Blood \" + Label(_kind)")
               && source.Contains("verification failed: \" + failure")
               && source.Contains("Fingerprint(before)") && source.Contains("Fingerprint(after)"),
            "a rejected permanent Blood settlement must preserve its exact diagnostic evidence");
    }

    public static int Main()
    {
        TestSelectionAndReservation();
        TestNativeGainFormulae();
        TestIronSettlement();
        TestMacGuffinSettlement();
        TestLiveSourceBoundary();
        Console.WriteLine("Permanent Blood spell tests passed: " + _assertions + " assertions");
        return 0;
    }
}
