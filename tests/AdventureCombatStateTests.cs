/*
FILE PURPOSE

Purpose: This dependency-free source-contract executable protects the Adventure tactical safety
boundaries implemented in CombatManager without loading Unity or the installed game assembly.

Mechanism: It reads the maintained CombatManager source and requires the exact installed Regular
Attack gate, Walderp exact/different response branches, counter-to-impact defenses, terminal lethal
reservation, MOVE69 exclusions, one-hop rerolls, and epoch cancellation hook to remain connected to
the live ManualZone/DoCombat paths.

Inputs and outputs: The only input is source/Managers/CombatManager.cs in the repository working
tree. Assertion diagnostics are printed and a failure returns a nonzero process exit code.

Invariants and safety: The test never loads Assembly-CSharp, Unity, a save, runtime telemetry, or the
injector DLL; it never invokes a native controller or writes outside compiler output chosen by the
caller. These structural checks complement, rather than impersonate, disposable-save integration.
*/
using System;
using System.IO;
using System.Text.RegularExpressions;

internal static class AdventureCombatStateTests
{
    private static int _assertions;

    private static int Main()
    {
        try
        {
            var source = File.ReadAllText(Path.Combine("source", "Managers", "CombatManager.cs"));
            RegularAttackGate(source);
            WalderpState(source);
            ReactiveDefense(source);
            TerminalFirstAction(source);
            CancellationAndReroll(source);
            Console.WriteLine("Adventure combat state tests passed: " + _assertions
                              + " assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void RegularAttackGate(string source)
    {
        Has(source, "return rowZeroLevel >= 5000L;",
            "native Regular Attack boundary is row 0 level 5,000");
        Has(source, "RegularAttackUnlocked(training[0])",
            "live manual gate consumes Basic Training row 0");
        Lacks(source, "attackTraining[1]",
            "Strong Attack's row is not used as the manual-combat gate");
        Has(source, "!AnyOffensiveMoveReady() && !constrainedActionState",
            "ordinary manual combat has an idle fallback when no move is ready");
    }

    private static void WalderpState(string source)
    {
        Has(source, "SelectWaldoResponseMove(ai.waldoAttackID, ai.waldoSays",
            "live Walderp fields feed the response selector");
        Has(source, "if (waldoSays)", "Walderp Says uses an exact-response branch");
        Has(source, "move != requestedMove && ready[move - (int)AttackMove.Regular]",
            "non-Says response requires a different ready damaging move");
        Has(source, "if (HandleWaldoResponse())", "Walderp response precedes ordinary buffs");
        Has(source, "returning to Safe Zone", "unavailable response fails closed");
        Has(source, "returning to Safe Zone without fallback",
            "a rejected exact response cannot fall through to the ordinary rotation");
        Has(source, "!(IsCurrentWaldo() && ac.enemyAI.inWaldoSaysLoop)",
            "MOVE69 cannot consume a pending Walderp response");
    }

    private static void ReactiveDefense(string source)
    {
        Has(source, "SecondsUntilCounterImpact(counter, 5",
            "Charger impact is counter 5");
        Has(source, "SecondsUntilCounterImpact(counter, 8",
            "Rapid-mode impact is counter 8");
        Has(source, "Parry — persistent charger reaction",
            "persistent Parry is used at the early Charger warning");
        Has(source, "Block — near-impact charger reaction",
            "three-second Block is held for near impact");
        Has(source, "timeToImpact <= 2.65",
            "Block admission leaves a bounded frame/scheduler margin");
        Has(source, "if (CombatCriticalReactions())",
            "fast combat cannot bypass imminent source-backed defenses");
    }

    private static void TerminalFirstAction(string source)
    {
        Has(source, "TryPrepareTerminalAttack(zone)",
            "Safe-Zone path requires a terminal reservation before entry");
        Has(source, "SelectLethalReadyMove(expected, 1f, false, 0, out damage)",
            "terminal entry proves a specific ready lethal move against initial state");
        Has(source, "WorstCaseMoveDamage(reservation.Move, enemy, defenseFactor",
            "reserved damage is revalidated against live enemy state");
        Has(source, "ExecuteTerminalAttackOrHold(zone);",
            "terminal enemy dispatch bypasses the ordinary priority rotation");
        Has(source, "if (reservation.Fired)",
            "no second action runs before native death reconciliation");
        Has(source, "if (enemy.curHP > 0f)",
            "terminal move requires the synchronous lethal HP postcondition");
        Has(source, "&& !IsTerminalTitanZone(_character.adventure.zone)",
            "MOVE69 is forbidden during terminal combat");
        Has(source, "TitanMechanics.IsTitanEnemyType(titan",
            "terminal/Walderp classification uses exact installed enemy types");
    }

    private static void CancellationAndReroll(string source)
    {
        Has(source, "RegisterHeldInputCancellation(string id, Action releaseInput)",
            "external key-down owners have a shared cancellation hook");
        Has(source, "Interlocked.Exchange(ref released, 1)",
            "normal release and epoch cancellation share idempotent key-up");
        Has(source, "Main.RegisterEpochCancellation(\"combat-terminal-first-action\"",
            "logical terminal reservation is epoch-scoped");
        False(Regex.IsMatch(source,
                @"MoveToZone\(-1\);\s*MoveToZone\(zone\);",
                RegexOptions.CultureInvariant),
            "reroll never performs Safe Zone and target transitions in one pass");
    }

    private static void Has(string source, string expected, string message)
    {
        True(source.Contains(expected), message + " (missing `" + expected + "`)");
    }

    private static void Lacks(string source, string forbidden, string message)
    {
        True(!source.Contains(forbidden), message + " (found `" + forbidden + "`)");
    }

    private static void False(bool condition, string message)
    {
        True(!condition, message);
    }

    private static void True(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception(message);
    }
}
