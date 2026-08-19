/*
FILE PURPOSE

ResetExecutionTests is task 17's isolated copied-state suite. It loads a temporary bot DLL and
proves the exact Normal-to-Evil/Evil-to-Sadistic gate tables, selector/start sequencing, feature
holds, +1/timer reset rule, all eleven hard-versus-Laser challenge proofs, wrong/multiple-flag
quarantine inputs, target-difficulty/Number=1 postconditions, the typed hard-reset persistence
transform, and the narrower first-wave root executor authority/reset-loss boundary. Optional
source-root input adds static anti-bypass checks. It never loads Unity state, invokes a native
method, injects a process, or reads/writes a save.
*/
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

internal static class ResetExecutionTests
{
    private static Assembly _assembly;
    private static int _assertions;
    private static int _failures;

    private static int Main(string[] args)
    {
        if (args.Length < 1 || args.Length > 2)
        {
            Console.Error.WriteLine("usage: ResetExecutionTests <temporary bot dll> [repo root]");
            return 2;
        }
        _assembly = Assembly.LoadFrom(args[0]);
        Run("Normal-to-Evil exact selector truth table", EvilGateTable);
        Run("Evil-to-Sadistic exact selector truth table", SadisticGateTable);
        Run("common final preflight matrix", FinalPreflightTable);
        Run("exact +1 and timer proof", ExactIncrementAndTimer);
        Run("all eleven challenge reset types", ChallengeResetMatrix);
        Run("wrong and multiple challenge flags fail proof", WrongFlagMatrix);
        Run("typed target-difficulty hard reset transforms", DifficultyTransforms);
        Run("selector, second gate, start, and epoch sequencing", ExecutorSequence);
        Run("feature authority remains closed", FeatureAuthority);
        Run("first-wave challenge and difficulty authority", FirstWaveAuthority);
        Run("typed reset-loss boundary matrix", ResetBoundaryMatrix);
        Run("typed irreversible intent contracts", TypedIntentContracts);
        if (args.Length == 2) Run("static anti-bypass contracts", () => StaticContracts(args[1]));
        Console.WriteLine(_failures == 0
            ? "PASS: " + _assertions + " reset execution assertions"
            : "FAIL: " + _failures + " group(s), " + _assertions + " assertions");
        return _failures == 0 ? 0 : 1;
    }

    private static void EvilGateTable()
    {
        var transition = EnumValue("NGUInjector.Autopilot.DifficultyTransitionKind",
            "NormalToEvil");
        var gate = LegalGate("Normal");
        Set(gate, "HighestBoss", 299);
        False(Legal(Selector(transition, gate)), "Boss record 299 fails");
        Set(gate, "HighestBoss", 300);
        True(Legal(Selector(transition, gate)), "Boss record 300 passes");

        Set(gate, "Achievement151", false);
        False(Legal(Selector(transition, gate)), "achievement 151 is exact first-entry gate");
        Set(gate, "Achievement151", true);
        Set(gate, "AttackBoost", 99.999999);
        Set(gate, "ItopodTotalStatBonus", 100.0);
        False(Legal(Selector(transition, gate)), "finite stat product immediately below 10000 fails");
        Set(gate, "AttackBoost", 100.0);
        True(Legal(Selector(transition, gate)), "finite stat product exactly 10000 passes");

        foreach (var invalid in new[] {double.NaN, double.PositiveInfinity, -1.0})
        {
            Set(gate, "AttackBoost", invalid);
            False(Legal(Selector(transition, gate)), "invalid AttackBoost fails closed: " + invalid);
            Set(gate, "AttackBoost", 100.0);
            Set(gate, "ItopodTotalStatBonus", invalid);
            False(Legal(Selector(transition, gate)), "invalid totalStatBonus fails closed: " + invalid);
            Set(gate, "ItopodTotalStatBonus", 100.0);
        }

        Set(gate, "Achievement152", true);
        Set(gate, "HighestBoss", -1);
        Set(gate, "Achievement151", false);
        Set(gate, "AttackBoost", double.NaN);
        Set(gate, "ItopodTotalStatBonus", -1.0);
        True(Legal(Selector(transition, gate)), "achievement 152 bypasses all first-entry gates");
        Set(gate, "InChallenge", true);
        False(Legal(Selector(transition, gate)), "achievement 152 never bypasses active challenge");
    }

    private static void SadisticGateTable()
    {
        var transition = EnumValue("NGUInjector.Autopilot.DifficultyTransitionKind",
            "EvilToSadistic");
        var gate = LegalGate("Evil");
        Set(gate, "HighestHardBoss", 299);
        False(Legal(Selector(transition, gate)), "Evil record 299 fails");
        Set(gate, "HighestHardBoss", 300);
        Set(gate, "ExileV4Defeated", false);
        False(Legal(Selector(transition, gate)), "Exile v4 false fails");
        Set(gate, "ExileV4Defeated", true);
        True(Legal(Selector(transition, gate)), "record 300 plus Exile v4 passes");
        Set(gate, "AttackBoost", double.NaN);
        Set(gate, "ItopodTotalStatBonus", -1.0);
        True(Legal(Selector(transition, gate)), "Sadistic has no advisory stat threshold");
        Set(gate, "InChallenge", true);
        False(Legal(Selector(transition, gate)), "Sadistic selector refuses active challenge");
    }

    private static void FinalPreflightTable()
    {
        var transition = EnumValue("NGUInjector.Autopilot.DifficultyTransitionKind",
            "NormalToEvil");
        var gate = LegalGate("Normal");
        True(Legal(FinalGate(transition, gate)), "complete final preflight passes");
        foreach (var mutation in new Action<object>[]
        {
            x => Set(x, "BossId", 0),
            x => Set(x, "BossFightInProgress", true),
            x => Set(x, "BossNukeInProgress", true),
            x => Set(x, "NoRebirthChallengeActive", true),
            x => Set(x, "RebirthSeconds", 179.999),
            x => Set(x, "RebirthSeconds", double.NaN),
            x => Set(x, "MinimumRebirthSeconds", double.PositiveInfinity),
            x => Set(x, "GameplaySynchronized", false),
            x => Set(x, "MutationLeaseCurrent", false)
        })
        {
            var changed = Clone(gate);
            mutation(changed);
            False(Legal(FinalGate(transition, changed)), "each common final gate fails independently");
        }
    }

    private static void ExactIncrementAndTimer()
    {
        var before = Snapshot("Normal", "Normal", false);
        var after = OrdinaryAfter(before);
        True(Satisfied(Call("NGUInjector.Autopilot.ResetPostconditions", "VerifyOrdinary",
            before, after)), "ordinary exact +1 and zero timer passes");

        var timerOnly = Clone(before);
        Set(timerOnly, "RebirthSeconds", 0.0);
        False(Satisfied(Call("NGUInjector.Autopilot.ResetPostconditions", "VerifyOrdinary",
            before, timerOnly)), "timer-only change is rejected");
        var counterOnly = Clone(before);
        Set(counterOnly, "RebirthNumber", 11L);
        False(Satisfied(Call("NGUInjector.Autopilot.ResetPostconditions", "VerifyOrdinary",
            before, counterOnly)), "counter-only change is rejected");
        var skipped = Clone(after);
        Set(skipped, "RebirthNumber", 12L);
        False(Satisfied(Call("NGUInjector.Autopilot.ResetPostconditions", "VerifyOrdinary",
            before, skipped)), "greater-than-one rebirth delta is rejected");
        var prematureTimeUpdate = Clone(after);
        var changedNumber = Field(prematureTimeUpdate, "Number");
        Set(changedNumber, "TimeMultiplier", 0.0);
        Set(prematureTimeUpdate, "Number", changedNumber);
        False(Satisfied(Call("NGUInjector.Autopilot.ResetPostconditions", "VerifyOrdinary",
            before, prematureTimeUpdate)),
            "synchronous reset proof preserves current timeMulti until the later Unity Update");
    }

    private static void ChallengeResetMatrix()
    {
        var names = new[] {"Basic", "NoAug", "TwentyFourHour", "OneHundredLC", "NoEquip",
            "Troll", "NoRebirth", "LaserSword", "Blind", "NoNGU", "NoTimeMachine"};
        for (var i = 0; i < names.Length; i++)
        {
            var type = EnumValue("NGUInjector.AllocationProfiles.RebirthStuff.ChallengeType",
                names[i]);
            var before = Snapshot("Normal", "Normal", false);
            var token = "native-token-" + i;
            var after = ChallengeAfter(before, i, token, i == 7);
            var proof = Call("NGUInjector.Autopilot.ResetPostconditions", "VerifyChallenge",
                before, after, type, token);
            True(Satisfied(proof), names[i] + " exact entry proof passes");
            True((bool)Field(proof, "ExactOneHotChallenge"), names[i] + " is one-hot/type exact");
            True((bool)Field(proof, "ExactResetType"), names[i] + " reset type is exact");
            var number = Field(after, "Number");
            if (i == 7)
            {
                Near(17.0, Number(number, "CurrentAttack"), "Laser banks Attack preview");
                Near(19.0, Number(number, "CurrentDefense"), "Laser banks Defense preview");
            }
            else
                True((bool)Property(number, "AllExactlyOne"), names[i] + " hard entry sets all eight Number fields to 1");
        }
    }

    private static void WrongFlagMatrix()
    {
        var type = EnumValue("NGUInjector.AllocationProfiles.RebirthStuff.ChallengeType", "Basic");
        var before = Snapshot("Normal", "Normal", false);
        var wrong = ChallengeAfter(before, 1, "wrong", false);
        False(Satisfied(Call("NGUInjector.Autopilot.ResetPostconditions", "VerifyChallenge",
            before, wrong, type, "basic")), "wrong intended flag/type fails proof");

        var multiple = ChallengeAfter(before, 0, "basic", false);
        var flags = (bool[])Field(multiple, "ChallengeFlags");
        flags[1] = true;
        Set(multiple, "ChallengeFlags", flags);
        False(Satisfied(Call("NGUInjector.Autopilot.ResetPostconditions", "VerifyChallenge",
            before, multiple, type, "basic")), "multiple flags fail proof");

        var badTimer = ChallengeAfter(before, 0, "basic", false);
        var timers = (double[])Field(badTimer, "ChallengeTimers");
        timers[0] = 0.001;
        Set(badTimer, "ChallengeTimers", timers);
        False(Satisfied(Call("NGUInjector.Autopilot.ResetPostconditions", "VerifyChallenge",
            before, badTimer, type, "basic")), "nonzero intended challenge timer fails proof");
    }

    private static void DifficultyTransforms()
    {
        var normal = DifficultyTransformSource("Normal", "Normal");
        var evilResolution = Resolution();
        var evil = Call("NGUInjector.Autopilot.DifficultyResetTransform", "Apply", normal,
            EnumValue("NGUInjector.Autopilot.ResetDifficulty", "Evil"), evilResolution);
        CheckTransform(normal, evil, "Evil", true);
        Equal(0, EnumIndex(Field(evil, "NguLevelTrack")), "Normal NGU track remains Normal on first Evil entry");
        True((bool)Field(evil, "Achievement152"), "Normal-to-Evil sets achievement 152");

        var sourceEvil = DifficultyTransformSource("Evil", "Evil");
        Set(sourceEvil, "NguLevelTrack", EnumValue("NGUInjector.Autopilot.ResetDifficulty", "Normal"));
        Set(sourceEvil, "Achievement152", false);
        var sadistic = Call("NGUInjector.Autopilot.DifficultyResetTransform", "Apply", sourceEvil,
            EnumValue("NGUInjector.Autopilot.ResetDifficulty", "Sadistic"), Resolution());
        CheckTransform(sourceEvil, sadistic, "Sadistic", false);
        Equal(0, EnumIndex(Field(sadistic, "NguLevelTrack")), "Sadistic preserves arbitrary existing NGU track");
        False((bool)Field(sadistic, "Achievement152"), "Sadistic does not fabricate an achievement");

        var corruptTrack = DifficultyTransformSource("Normal", "Sadistic");
        var clamped = Call("NGUInjector.Autopilot.DifficultyResetTransform", "Apply", corruptTrack,
            EnumValue("NGUInjector.Autopilot.ResetDifficulty", "Evil"), Resolution());
        Equal(1, EnumIndex(Field(clamped, "NguLevelTrack")), "Evil clamps only a track above target");
    }

    private static void ExecutorSequence()
    {
        var transition = EnumValue("NGUInjector.Autopilot.DifficultyTransitionKind", "NormalToEvil");
        var before = Snapshot("Normal", "Normal", false);
        var selected = Clone(before);
        Set(selected, "NextDifficulty", EnumValue("NGUInjector.Autopilot.ResetDifficulty", "Evil"));
        var after = DifficultyAfter(before, "Evil", true);
        var boundary = ScriptedBoundary(before, selected, after, "Evil", true, true);
        var epoch = New("NGUInjector.Autopilot.ScriptedResetEpochBoundary");
        var executor = NewArgs("NGUInjector.Autopilot.DifficultyTransitionExecutor", boundary, epoch);
        var result = CallInstance(executor, "Execute", transition, true);
        Equal(2, EnumIndex(Field(result, "Kind")), "exact sequence commits");
        Equal(1, Convert.ToInt32(Field(boundary, "SelectorCalls")), "one selector call");
        Equal(1, Convert.ToInt32(Field(boundary, "StartCalls")), "one start call");
        Equal(1, Convert.ToInt32(Field(epoch, "CloseCalls")), "epoch closes synchronously once");
        True((bool)Field(result, "EpochClosed"), "commit reports synchronous epoch close");

        var staleBoundary = ScriptedBoundary(before, selected, after, "Evil", true, true);
        var staleGate = LegalGate("Normal");
        Set(staleGate, "BossFightInProgress", true);
        Set(staleBoundary, "SecondGate", staleGate);
        var stale = CallInstance(NewArgs("NGUInjector.Autopilot.DifficultyTransitionExecutor",
            staleBoundary, New("NGUInjector.Autopilot.ScriptedResetEpochBoundary")),
            "Execute", transition, true);
        Equal(0, EnumIndex(Field(stale, "Kind")), "stale second preflight holds");
        Equal(0, Convert.ToInt32(Field(staleBoundary, "StartCalls")), "stale second gate never starts");

        var wrongTarget = ScriptedBoundary(before, selected, after, "Normal", true, true);
        var wrong = CallInstance(NewArgs("NGUInjector.Autopilot.DifficultyTransitionExecutor",
            wrongTarget, New("NGUInjector.Autopilot.ScriptedResetEpochBoundary")),
            "Execute", transition, true);
        Equal(1, EnumIndex(Field(wrong, "Kind")), "wrong selected target is rejected");
        Equal(0, Convert.ToInt32(Field(wrongTarget, "StartCalls")), "wrong target never starts");

        var failedSelector = ScriptedBoundary(before, before, after, "Normal", false, true);
        var failed = CallInstance(NewArgs("NGUInjector.Autopilot.DifficultyTransitionExecutor",
            failedSelector, New("NGUInjector.Autopilot.ScriptedResetEpochBoundary")),
            "Execute", transition, true);
        Equal(1, EnumIndex(Field(failed, "Kind")), "failed unchanged selector is rejected unchanged");
        Equal(0, Convert.ToInt32(Field(failedSelector, "StartCalls")), "failed selector never starts");

        var partial = Clone(after);
        var challengeFlags = new bool[11];
        challengeFlags[0] = true;
        challengeFlags[1] = true;
        Set(partial, "InChallenge", true);
        Set(partial, "ChallengeFlags", challengeFlags);
        var partialBoundary = ScriptedBoundary(before, selected, partial, "Evil", true, true);
        var partialEpoch = New("NGUInjector.Autopilot.ScriptedResetEpochBoundary");
        var quarantined = CallInstance(NewArgs("NGUInjector.Autopilot.DifficultyTransitionExecutor",
            partialBoundary, partialEpoch), "Execute", transition, true);
        Equal(4, EnumIndex(Field(quarantined, "Kind")), "partial/multiple-flag transition quarantines");
        Equal(1, Convert.ToInt32(Field(partialEpoch, "QuarantineCalls")), "quarantine callback runs once");
    }

    private static void FeatureAuthority()
    {
        var transition = EnumValue("NGUInjector.Autopilot.DifficultyTransitionKind", "NormalToEvil");
        var before = Snapshot("Normal", "Normal", false);
        var boundary = ScriptedBoundary(before, before, before, "Normal", true, true);
        var epoch = New("NGUInjector.Autopilot.ScriptedResetEpochBoundary");
        var result = CallInstance(NewArgs("NGUInjector.Autopilot.DifficultyTransitionExecutor",
            boundary, epoch), "Execute", transition, false);
        Equal(0, EnumIndex(Field(result, "Kind")), "difficulty authority false holds");
        Equal(0, Convert.ToInt32(Field(boundary, "SelectorCalls")), "feature hold makes zero selector calls");
        Equal(0, Convert.ToInt32(Field(boundary, "StartCalls")), "feature hold makes zero start calls");
    }

    private static void FirstWaveAuthority()
    {
        var safe = new[] {"Basic", "NoAug", "NoEquip", "Blind", "NoNGU", "NoTimeMachine"};
        var held = new[] {"TwentyFourHour", "OneHundredLC", "Troll", "NoRebirth", "LaserSword"};
        foreach (var name in safe)
            True((bool)Call("NGUInjector.Autopilot.ResetProgressionAuthority",
                    "SafeNormalChallenge", EnumValue(
                        "NGUInjector.AllocationProfiles.RebirthStuff.ChallengeType", name)),
                name + " is in the first Normal batch");
        foreach (var name in held)
            False((bool)Call("NGUInjector.Autopilot.ResetProgressionAuthority",
                    "SafeNormalChallenge", EnumValue(
                        "NGUInjector.AllocationProfiles.RebirthStuff.ChallengeType", name)),
                name + " remains outside first-wave authority");
        True((bool)Call("NGUInjector.Autopilot.ResetProgressionAuthority", "SafeDifficulty",
                EnumValue("NGUInjector.Autopilot.DifficultyTransitionKind", "NormalToEvil")),
            "Normal-to-Evil is the only first-wave difficulty transition");
        False((bool)Call("NGUInjector.Autopilot.ResetProgressionAuthority", "SafeDifficulty",
                EnumValue("NGUInjector.Autopilot.DifficultyTransitionKind", "EvilToSadistic")),
            "Evil-to-Sadistic remains outside first-wave authority");
    }

    private static void ResetBoundaryMatrix()
    {
        var allClear = New("NGUInjector.Autopilot.ResetBoundarySnapshot");
        Set(allClear, "GameplaySynchronized", true);
        Set(allClear, "RootLeaseCurrent", true);
        Set(allClear, "TitanBoundaryClear", true);
        Set(allClear, "FruitBoundaryClear", true);
        Set(allClear, "BloodBoundaryClear", true);
        True((bool)Field(Call("NGUInjector.Autopilot.ResetBoundaryGate", "Evaluate", allClear),
            "Clear"), "complete root/Titan/fruit/Blood boundary passes");

        foreach (var field in new[] {"GameplaySynchronized", "RootLeaseCurrent",
                     "TitanBoundaryClear", "FruitBoundaryClear", "BloodBoundaryClear"})
        {
            var changed = New("NGUInjector.Autopilot.ResetBoundarySnapshot");
            Set(changed, "GameplaySynchronized", true);
            Set(changed, "RootLeaseCurrent", true);
            Set(changed, "TitanBoundaryClear", true);
            Set(changed, "FruitBoundaryClear", true);
            Set(changed, "BloodBoundaryClear", true);
            Set(changed, field, false);
            False((bool)Field(Call("NGUInjector.Autopilot.ResetBoundaryGate", "Evaluate", changed),
                "Clear"), field + " independently holds every reset-like action");
        }
    }

    private static void TypedIntentContracts()
    {
        foreach (var typeName in new[] {"NGUInjector.Autopilot.ChallengeEntryMutationIntent",
                     "NGUInjector.Autopilot.NormalToEvilMutationIntent"})
        {
            var intent = FormatterServices.GetUninitializedObject(Type(typeName));
            True((bool)Property(intent, "CreatesNewEpoch"),
                typeName + " creates and closes a run epoch");
            True((bool)Property(intent, "Required"),
                typeName + " is a required root child");
            False((bool)Property(intent, "CanCompensate"),
                typeName + " never claims an inverse for reset state");
            Equal(2, EnumIndex(Property(intent, "Risk")),
                typeName + " is explicitly irreversible");
        }
        Equal("Challenge", Property(FormatterServices.GetUninitializedObject(Type(
                "NGUInjector.Autopilot.ChallengeEntryMutationIntent")), "Class").ToString(),
            "challenge intent uses the Challenge lease class");
        Equal("Difficulty", Property(FormatterServices.GetUninitializedObject(Type(
                "NGUInjector.Autopilot.NormalToEvilMutationIntent")), "Class").ToString(),
            "difficulty intent uses the Difficulty lease class");
    }

    private static void StaticContracts(string root)
    {
        var baseSource = File.ReadAllText(Path.Combine(root,
            "source/AllocationProfiles/RebirthStuff/BaseRebirth.cs"));
        var executor = File.ReadAllText(Path.Combine(root,
            "source/Autopilot/DifficultyTransitionExecutor.cs"));
        var typed = File.ReadAllText(Path.Combine(root,
            "source/Autopilot/ResetProgressionTransaction.cs"));
        True(executor.Contains("Rebirth.setEvilNextRebirth")
             && executor.Contains("Rebirth.setSadisticNextRebirth"),
            "both source-exact gated selectors are named");
        True(executor.Contains("SelectDifficulty(") && executor.Contains("StartDifficulty("),
            "executor uses pinned selector then pinned start adapters");
        False(executor.Contains("Generic3Toggle"), "legacy weak selector class is absent");
        False(executor.Contains("startHardRebirth") || executor.Contains("startSadisticRebirth"),
            "owned executor has no direct public start method names");
        False(Regex.IsMatch(executor, @"nextRebirthDifficulty\s*=(?!=)"),
            "owned executor never writes next difficulty directly");
        False(baseSource.Contains("GetPrivateMethod(\"engage")
              || baseSource.Contains("GetMethods(")
              || baseSource.Contains("engageBasicChallenge")
              || baseSource.Contains("engageLaserSwordChallenge"),
            "BaseRebirth has no name-only irreversible reflection path");
        True(baseSource.Contains("ChallengeIntentSelector.StillValid")
             && baseSource.Contains("ChallengeTargets.Length != 1"),
            "BaseRebirth consumes exactly one task-16 intent");
        True(baseSource.Contains("no runner-up or ordinary reset was attempted"),
            "failed selected challenge cannot substitute another reset");
        True(baseSource.Contains("AllowChallenges")
             && executor.Contains("featureAuthority"),
            "challenge and difficulty authority have explicit feature gates");
        True(typed.Contains("RootTransaction root")
             && typed.Contains("CreatesNewEpoch { get { return true; }"),
            "first-wave challenge/difficulty actions require a root and create a successor epoch");
        True(typed.Contains("ChallengeStrategyPlanner.Recommend")
             && typed.Contains("ResetPostconditions.VerifyChallenge")
             && typed.Contains("ResetPostconditions.VerifyDifficulty"),
            "typed executor reuses exact admission and postcondition proofs");
        True(typed.Contains("before.Admission.Opportunity")
             && typed.Contains("winning same-state opportunity proof"),
            "challenge mutation requires the planner's same-state opportunity proof again");
        True(typed.Contains("TitanBoundaryClear") && typed.Contains("FruitBoundaryClear")
             && typed.Contains("BloodBoundaryClear"),
            "every first-wave reset shares Titan, fruit, and Blood boundaries");
        False(typed.Contains("new RebirthBloodSpendIntent"),
            "hard challenge entry never burns Blood on a NUMBER effect it resets to one");
        False(typed.Contains("NativeDifficultyCall.Sadistic")
              || typed.Contains("NativeChallengeCall.Troll")
              || typed.Contains("NativeChallengeCall.LaserSword"),
            "special challenges and Sadistic native execution remain absent");
        False(typed.Contains("Move69") || typed.Contains("EndSequence"),
            "MOVE69 and final END execution remain outside first-wave runtime authority");
    }

    private static object LegalGate(string difficulty)
    {
        var gate = New("NGUInjector.Autopilot.DifficultyGateSnapshot");
        Set(gate, "CurrentDifficulty", EnumValue("NGUInjector.Autopilot.ResetDifficulty", difficulty));
        Set(gate, "InChallenge", false);
        Set(gate, "Achievement151", true);
        Set(gate, "HighestBoss", 300);
        Set(gate, "HighestHardBoss", 300);
        Set(gate, "AttackBoost", 100.0);
        Set(gate, "ItopodTotalStatBonus", 100.0);
        Set(gate, "ExileV4Defeated", true);
        Set(gate, "BossId", 1);
        Set(gate, "RebirthSeconds", 180.0);
        Set(gate, "MinimumRebirthSeconds", 180.0);
        Set(gate, "GameplaySynchronized", true);
        Set(gate, "MutationLeaseCurrent", true);
        return gate;
    }

    private static object Selector(object transition, object gate)
    {
        return Call("NGUInjector.Autopilot.DifficultyTransitionGate", "EvaluateSelector",
            transition, gate);
    }

    private static object FinalGate(object transition, object gate)
    {
        return Call("NGUInjector.Autopilot.DifficultyTransitionGate", "EvaluateFinalPreflight",
            transition, gate);
    }

    private static bool Legal(object result) { return (bool)Field(result, "Legal"); }
    private static bool Satisfied(object result) { return (bool)Field(result, "Satisfied"); }

    private static object Snapshot(string current, string next, bool inChallenge)
    {
        var s = New("NGUInjector.Autopilot.ResetExecutionSnapshot");
        Set(s, "RebirthNumber", 10L);
        Set(s, "RebirthSeconds", 180.0);
        Set(s, "CurrentDifficulty", EnumValue("NGUInjector.Autopilot.ResetDifficulty", current));
        Set(s, "NextDifficulty", EnumValue("NGUInjector.Autopilot.ResetDifficulty", next));
        Set(s, "NguLevelTrack", EnumValue("NGUInjector.Autopilot.ResetDifficulty", "Normal"));
        Set(s, "Number", SentinelNumber());
        Set(s, "BossId", 7);
        Set(s, "CurrentHighestBoss", 7);
        Set(s, "HighestBoss", 300);
        Set(s, "HighestHardBoss", 300);
        Set(s, "HighestSadisticBoss", 22);
        Set(s, "Achievement152", false);
        Set(s, "InChallenge", inChallenge);
        Set(s, "ChallengeFlags", new bool[11]);
        Set(s, "ChallengeTimers", Enumerable.Repeat(77.0, 11).ToArray());
        Set(s, "TitanClocks", Enumerable.Range(1, 14).Select(x => (double)x).ToArray());
        Set(s, "TitanRunKillCounters", Enumerable.Range(1, 8).ToArray());
        Set(s, "PersistentStateFingerprint", "persistent-sentinel");
        return s;
    }

    private static object SentinelNumber()
    {
        var n = New("NGUInjector.Autopilot.ResetNumberSnapshot");
        Set(n, "CurrentAttack", 5.0); Set(n, "CurrentDefense", 6.0);
        Set(n, "NextAttack", 17.0); Set(n, "NextDefense", 19.0);
        Set(n, "BossMultiplier", 4.0); Set(n, "TimeMultiplier", 2.0);
        Set(n, "OldBossMultiplier", 3.0); Set(n, "OldTimeMultiplier", 8.0);
        return n;
    }

    private static object OneNumber()
    {
        return New("NGUInjector.Autopilot.ResetNumberSnapshot");
    }

    private static object SoftNumber(object beforeSnapshot)
    {
        var before = Field(beforeSnapshot, "Number");
        var n = New("NGUInjector.Autopilot.ResetNumberSnapshot");
        Set(n, "CurrentAttack", Number(before, "NextAttack"));
        Set(n, "CurrentDefense", Number(before, "NextDefense"));
        Set(n, "NextAttack", Number(before, "NextAttack"));
        Set(n, "NextDefense", Number(before, "NextDefense"));
        Set(n, "BossMultiplier", 1.0);
        Set(n, "TimeMultiplier", Number(before, "TimeMultiplier"));
        Set(n, "OldBossMultiplier", Number(before, "BossMultiplier"));
        Set(n, "OldTimeMultiplier", Number(before, "TimeMultiplier"));
        return n;
    }

    private static object OrdinaryAfter(object before)
    {
        var after = Clone(before);
        Set(after, "RebirthNumber", 11L); Set(after, "RebirthSeconds", 0.0);
        Set(after, "BossId", 0); Set(after, "Number", SoftNumber(before));
        Set(after, "TitanClocks", new double[14]); Set(after, "TitanRunKillCounters", new int[8]);
        return after;
    }

    private static object ChallengeAfter(object before, int index, string token, bool soft)
    {
        var after = OrdinaryAfter(before);
        var flags = new bool[11]; flags[index] = true;
        var timers = Enumerable.Repeat(77.0, 11).ToArray(); timers[index] = 0.0;
        Set(after, "InChallenge", true); Set(after, "ChallengeFlags", flags);
        Set(after, "CurrentChallengeTypeToken", token); Set(after, "ChallengeTimers", timers);
        Set(after, "Number", soft ? SoftNumber(before) : OneNumber());
        return after;
    }

    private static object DifficultyAfter(object before, string target, bool achievement152)
    {
        var after = Clone(before);
        var targetValue = EnumValue("NGUInjector.Autopilot.ResetDifficulty", target);
        Set(after, "RebirthNumber", 11L); Set(after, "RebirthSeconds", 0.0);
        Set(after, "CurrentDifficulty", targetValue); Set(after, "NextDifficulty", targetValue);
        Set(after, "Number", OneNumber()); Set(after, "BossId", 0);
        Set(after, "CurrentHighestBoss", 0); Set(after, "Achievement152", achievement152);
        Set(after, "InChallenge", false); Set(after, "ChallengeFlags", new bool[11]);
        Set(after, "TitanClocks", new double[14]); Set(after, "TitanRunKillCounters", new int[8]);
        return after;
    }

    private static object DifficultyTransformSource(string difficulty, string track)
    {
        var s = New("NGUInjector.Autopilot.DifficultyResetSnapshot");
        Set(s, "CurrentDifficulty", EnumValue("NGUInjector.Autopilot.ResetDifficulty", difficulty));
        Set(s, "NextDifficulty", EnumValue("NGUInjector.Autopilot.ResetDifficulty", difficulty));
        Set(s, "NguLevelTrack", EnumValue("NGUInjector.Autopilot.ResetDifficulty", track));
        Set(s, "Number", SentinelNumber());
        Set(s, "BossId", 20); Set(s, "CurrentHighestBoss", 20);
        Set(s, "HighestBoss", 300); Set(s, "HighestHardBoss", 123); Set(s, "HighestSadisticBoss", 4);
        Set(s, "RebirthSeconds", 180.0); Set(s, "RebirthNumber", 10L);
        Set(s, "TitanClocks", Enumerable.Range(1, 14).Select(x => (double)x).ToArray());
        Set(s, "TitanRunKillCounters", Enumerable.Range(1, 14).ToArray());
        Set(s, "BasicTrainingCaps", new long[] {10, 20});
        Set(s, "BasicTrainingRunLevels", new long[] {30, 40});
        Set(s, "BasicTrainingAllocations", new long[] {50, 60});
        Set(s, "AdvancedTrainingTemporary", 11.0); Set(s, "AdvancedTrainingBank", 12.0);
        Set(s, "BeardPermanentTrimmings", 13.0); Set(s, "BeardTemporary", 14.0);
        Set(s, "BeardBank", 15.0); Set(s, "TimeMachineBank", 16.0);
        Set(s, "MacGuffinPersistentValue", 17.0); Set(s, "AdventurePoints", 18.0);
        Set(s, "CurrentGold", 19.0); Set(s, "CurrentBlood", 20.0);
        Set(s, "CurrentEnergy", 21.0); Set(s, "CurrentMagic", 22.0); Set(s, "CurrentResource3", 23.0);
        Set(s, "NguLevels", new[] {1.0}); Set(s, "NguProgress", new[] {2.0}); Set(s, "NguAllocations", new[] {3.0});
        Set(s, "HackLevels", new[] {4.0}); Set(s, "HackProgress", new[] {5.0}); Set(s, "HackAllocations", new[] {6.0});
        Set(s, "WishLevels", new[] {7.0}); Set(s, "WishProgress", new[] {8.0}); Set(s, "WishAllocations", new[] {9.0});
        Set(s, "YggdrasilFruitTimers", new[] {100.0, 200.0});
        Set(s, "InventoryIdentity", "inventory"); Set(s, "PersistentUnlockIdentity", "unlocks");
        return s;
    }

    private static object Resolution()
    {
        var r = New("NGUInjector.Autopilot.DifficultyResetResolution");
        Set(r, "BasicTrainingCapsAfterCompression", new long[] {9, 18});
        Set(r, "BeardPermanentAfterConversion", 113.0);
        Set(r, "MacGuffinPersistentAfterConversion", 117.0);
        Set(r, "AdventurePointsAfterAward", 118.0);
        Set(r, "YggdrasilFruitTimersAfterFactor", new[] {50.0, 100.0});
        return r;
    }

    private static void CheckTransform(object before, object after, string target,
        bool expectAchievement)
    {
        Equal(EnumIndex(EnumValue("NGUInjector.Autopilot.ResetDifficulty", target)),
            EnumIndex(Field(after, "CurrentDifficulty")), target + " current difficulty installed");
        Equal(EnumIndex(Field(after, "CurrentDifficulty")), EnumIndex(Field(after, "NextDifficulty")),
            target + " current and next target match");
        Equal(EnumIndex(Field(after, "CurrentDifficulty")),
            EnumIndex(Field(after, "DifficultyObservedDuringConversions")),
            target + " is installed before conversions");
        True((bool)Property(Field(after, "Number"), "AllExactlyOne"), target + " sets all eight Number fields 1");
        Equal(0, Convert.ToInt32(Field(after, "BossId")), target + " clears current Boss");
        Equal(300, Convert.ToInt32(Field(after, "HighestBoss")), target + " preserves Normal record");
        Equal(123, Convert.ToInt32(Field(after, "HighestHardBoss")), target + " preserves Evil record");
        Equal(4, Convert.ToInt32(Field(after, "HighestSadisticBoss")), target + " preserves Sadistic record");
        Equal(11L, Convert.ToInt64(Field(after, "RebirthNumber")), target + " increments exactly one");
        True(((double[])Field(after, "TitanClocks")).All(x => x == 0.0), target + " clears fourteen clocks");
        True(((int[])Field(after, "TitanRunKillCounters")).All(x => x == 0), target + " clears run counters");
        True(((long[])Field(after, "BasicTrainingCaps")).SequenceEqual(new long[] {9, 18}), target + " installs resolved BT caps");
        Near(113.0, Number(after, "BeardPermanentTrimmings"), target + " converts Beard permanent before clear");
        Near(117.0, Number(after, "MacGuffinPersistentValue"), target + " converts MacGuffin before clear");
        Near(0.0, Number(after, "BeardTemporary"), target + " clears Beard temporary");
        Near(0.0, Number(after, "BeardBank"), target + " does not seed ordinary Beard bank");
        Near(0.0, Number(after, "AdvancedTrainingBank"), target + " challenge-resets AT bank");
        Near(0.0, Number(after, "TimeMachineBank"), target + " challenge-resets TM bank");
        Near(1.0, ((double[])Field(after, "NguLevels"))[0], target + " preserves NGU level");
        Near(2.0, ((double[])Field(after, "NguProgress"))[0], target + " preserves NGU progress");
        Near(0.0, ((double[])Field(after, "NguAllocations"))[0], target + " clears NGU allocation");
        Near(4.0, ((double[])Field(after, "HackLevels"))[0], target + " preserves Hack level");
        Near(5.0, ((double[])Field(after, "HackProgress"))[0], target + " preserves Hack progress");
        Near(0.0, ((double[])Field(after, "HackAllocations"))[0], target + " clears Hack allocation");
        Near(7.0, ((double[])Field(after, "WishLevels"))[0], target + " preserves Wish level");
        Near(8.0, ((double[])Field(after, "WishProgress"))[0], target + " preserves Wish progress");
        Near(0.0, ((double[])Field(after, "WishAllocations"))[0], target + " clears Wish allocation");
        Equal("inventory", (string)Field(after, "InventoryIdentity"), target + " preserves inventory");
        Equal("unlocks", (string)Field(after, "PersistentUnlockIdentity"), target + " preserves unlocks");
        Equal(expectAchievement, (bool)Field(after, "Achievement152"), target + " achievement rule");
        var order = (string[])Field(after, "TransitionOrder");
        Equal("install-target-difficulty", order[0], target + " target is first transition step");
        True(Array.IndexOf(order, "convert-beards") < Array.IndexOf(order, "clear-common-run-state"),
            target + " conversion precedes clear");
    }

    private static object ScriptedBoundary(object before, object selected, object after,
        string selectedTarget, bool selectorNormal, bool startNormal)
    {
        var b = New("NGUInjector.Autopilot.ScriptedDifficultyBoundary");
        Set(b, "FirstGate", LegalGate("Normal")); Set(b, "SecondGate", LegalGate("Normal"));
        Set(b, "Before", before); Set(b, "SelectedState", selected); Set(b, "After", after);
        Set(b, "SelectedTarget", EnumValue("NGUInjector.Autopilot.ResetDifficulty", selectedTarget));
        Set(b, "SelectorResult", NativeResult(selectorNormal));
        Set(b, "StartResult", NativeResult(startNormal));
        return b;
    }

    private static object NativeResult(bool normal)
    {
        var value = New("NGUInjector.Autopilot.ResetNativeObservation");
        Set(value, "InvocationAttempted", true); Set(value, "ReturnedNormally", normal);
        Set(value, "Reason", normal ? "normal" : "failed");
        return value;
    }

    private static object Clone(object value) { return CallInstance(value, "Clone"); }

    private static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS: " + name); }
        catch (Exception error)
        {
            _failures++;
            Console.Error.WriteLine("FAIL: " + name + " — " + Unwrap(error).Message);
        }
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException && error.InnerException != null)
            error = error.InnerException;
        return error;
    }

    private static Type Type(string name)
    {
        return _assembly.GetType(name, true);
    }

    private static object New(string type)
    {
        return Activator.CreateInstance(Type(type), true);
    }

    private static object NewArgs(string type, params object[] args)
    {
        return Activator.CreateInstance(Type(type), BindingFlags.Instance | BindingFlags.Public
            | BindingFlags.NonPublic, null, args, null);
    }

    private static object EnumValue(string type, string name)
    {
        return Enum.Parse(Type(type), name);
    }

    private static int EnumIndex(object value) { return Convert.ToInt32(value); }

    private static object Call(string type, string method, params object[] args)
    {
        return Invoke(Type(type), null, method, args);
    }

    private static object CallInstance(object target, string method, params object[] args)
    {
        return Invoke(target.GetType(), target, method, args);
    }

    private static object Invoke(Type type, object target, string method, object[] args)
    {
        var candidates = type.GetMethods(BindingFlags.Static | BindingFlags.Instance
                                         | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(x => x.Name == method && x.GetParameters().Length == args.Length).ToArray();
        if (candidates.Length != 1) throw new Exception("method resolution " + type + "." + method
                                                       + " found " + candidates.Length);
        return candidates[0].Invoke(target, args);
    }

    private static object Field(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Static
                                                    | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null) throw new Exception("missing field " + target.GetType() + "." + name);
        return field.GetValue(target);
    }

    private static object Property(object target, string name)
    {
        var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Static
                                                          | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null) throw new Exception("missing property " + target.GetType() + "." + name);
        return property.GetValue(target, null);
    }

    private static void Set(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public
                                                    | BindingFlags.NonPublic);
        if (field == null) throw new Exception("missing field " + target.GetType() + "." + name);
        field.SetValue(target, value);
    }

    private static double Number(object target, string name)
    {
        return Convert.ToDouble(Field(target, name));
    }

    private static void True(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception(message);
    }

    private static void False(bool condition, string message) { True(!condition, message); }

    private static void Equal<T>(T expected, T actual, string message)
    {
        _assertions++;
        if (!object.Equals(expected, actual))
            throw new Exception(message + " expected=" + expected + " actual=" + actual);
    }

    private static void Near(double expected, double actual, string message)
    {
        _assertions++;
        if (Math.Abs(expected - actual) > 1e-12)
            throw new Exception(message + " expected=" + expected + " actual=" + actual);
    }
}
