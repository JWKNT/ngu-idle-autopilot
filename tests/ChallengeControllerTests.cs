/*
FILE PURPOSE

ChallengeControllerTests is task 16's isolated reflection suite. It loads only a temporary bot DLL
and proves all eleven challenge target/entry/offline/completion transforms, keyed timing evidence,
one-hot intent selection, the shared 100-Level budget, Troll cadence/reset preservation, Laser's
build-versus-commit switch, No-Rebirth continuity, 24-Hour slack/race, Titan-vector cost, and the
single final recovery charge. It never loads Unity state or mutates a save.
*/
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

internal static class ChallengeControllerTests
{
    private static Assembly _assembly;
    private static int _assertions;
    private static int _failures;

    private static readonly string[] Types =
    {
        "Basic", "NoAug", "TwentyFourHour", "OneHundredLC", "NoEquip", "Troll",
        "NoRebirth", "LaserSword", "Blind", "NoNGU", "NoTimeMachine"
    };

    private static readonly int[] Maxima = {5, 5, 10, 5, 5, 7, 10, 20, 10, 10, 10};

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: ChallengeControllerTests <temporary bot dll>");
            return 2;
        }
        _assembly = Assembly.LoadFrom(args[0]);
        Run("all difficulty-local target and completion matrices", TargetMatrix);
        Run("all eleven hard/soft entry transforms are one-hot", EntryMatrix);
        Run("all eleven offline transforms", OfflineMatrix);
        Run("all eleven completion transforms and Basic double count", CompletionMatrix);
        Run("native minimum/latest time writes", NativeTimeWrites);
        Run("bot-owned keys and calibrated labels", TimingEvidence);
        Run("exactly one epoch-bound intent", IntentSelection);
        Run("shared 100-Level competing budget", HundredLevelBudget);
        Run("Troll fifth event and rebirth preservation", TrollCadence);
        Run("Laser build versus commit", LaserPhases);
        Run("24-Hour slack and same-frame race", DeadlineAndRace);
        Run("Titan reset vector and one final recovery", TitanAndBatch);
        Console.WriteLine(_failures == 0
            ? "PASS: " + _assertions + " challenge controller assertions"
            : "FAIL: " + _failures + " group(s), " + _assertions + " assertions");
        return _failures == 0 ? 0 : 1;
    }

    private static void TargetMatrix()
    {
        for (var difficulty = 0; difficulty < 3; difficulty++)
        for (var typeIndex = 0; typeIndex < Types.Length; typeIndex++)
        {
            var type = EnumValue("NGUInjector.AllocationProfiles.RebirthStuff.ChallengeType",
                Types[typeIndex]);
            Equal(Maxima[typeIndex], Convert.ToInt32(Call("NGUInjector.Autopilot.ChallengeMechanics",
                    "DefaultMaximum", type)), Types[typeIndex] + " serialized-scene default");
            for (var completed = 0; completed < Maxima[typeIndex]; completed++)
            {
                var target = ExpectedTarget(typeIndex, completed);
                Equal(target, Convert.ToInt32(Call("NGUInjector.Autopilot.ChallengeMechanics",
                        "ExactTarget", type, completed)),
                    Types[typeIndex] + " target d=" + difficulty + " c=" + completed);
                var below = (bool)Call("NGUInjector.Autopilot.ChallengeMechanics",
                    "CompletionSatisfied", type, target, (long)target,
                    typeIndex == 7 ? (long)target - 1L : (long)target, completed);
                True(!below, Types[typeIndex] + " strict/paired predicate below completion");
                var met = typeIndex == 7
                    ? (bool)Call("NGUInjector.Autopilot.ChallengeMechanics",
                        "CompletionSatisfied", type, 0, (long)target, (long)target, completed)
                    : (bool)Call("NGUInjector.Autopilot.ChallengeMechanics",
                        "CompletionSatisfied", type, target + 1, 0L, 0L, completed);
                True(met, Types[typeIndex] + " exact completion predicate");
                if (typeIndex == 7)
                    True(!(bool)Call("NGUInjector.Autopilot.ChallengeMechanics",
                            "CompletionSatisfied", type, 0, (long)target, (long)target - 1L,
                            completed), "Laser requires both pair tracks");
            }
        }
    }

    private static void EntryMatrix()
    {
        for (var i = 0; i < Types.Length; i++)
        {
            var type = ChallengeType(i);
            Equal(i == 7 ? 1 : 0, EnumIndex(Call(
                    "NGUInjector.Autopilot.ChallengeMechanics", "EntryKind", type)),
                Types[i] + " exact hard/soft entry classification");
            True((bool)Call("NGUInjector.Autopilot.ChallengeMechanics",
                    "ShouldCastBloodNumberBeforeEntry", type) == (i == 7),
                Types[i] + " Blood Number preparation has value only for soft Laser entry");
            var state = TransitionState(type, 0);
            var rebirth = Field(state, "Rebirth");
            Set(rebirth, "CurrentAttackNumber", 9.0);
            Set(rebirth, "CurrentDefenseNumber", 8.0);
            Set(rebirth, "BossMulti", 4.0);
            Set(rebirth, "OldBossMulti", 3.0);
            Set(rebirth, "TimeMulti", 2.0);
            Set(rebirth, "OldTimeMulti", 5.0);
            Set(rebirth, "BloodPower", 7.0);
            Set(rebirth, "RunSeconds", 3600.0);
            Set(state, "PublishedNextAttack", 19.0);
            Set(state, "PublishedNextDefense", 18.0);
            Set(state, "RebirthLevels", 87L);
            Set(state, "ResetLocalProgress", 99L);
            Set(state, "TrollCounter", 444);
            var elapsed = Enumerable.Range(1, 14).Select(x => (double)x * 100.0).ToArray();
            Set(state, "TitanClocks", NewArgs("NGUInjector.Autopilot.TitanClockSnapshot", elapsed));
            var preview = Call("NGUInjector.Autopilot.RebirthTransitionKernel", "Preview", rebirth);
            var result = Call("NGUInjector.Autopilot.ChallengeMechanics", "ApplyEntry",
                state, type, null);
            var flags = (bool[])Field(result, "ActiveFlags");
            Equal(1, flags.Count(x => x), Types[i] + " entry produces one active flag");
            True(flags[i], Types[i] + " entry selects its own flag");
            var clocks = (double[])CallInstance(Field(result, "TitanClocks"), "ToArray");
            True(clocks.All(x => x == 0.0), Types[i] + " entry resets all fourteen Titan clocks");
            Equal(0L, Convert.ToInt64(Field(result, "RebirthLevels")),
                Types[i] + " entry renews the 100-Level budget");
            if (i == 7)
            {
                Near(Number(preview, "Attack"), Number(Field(result, "Rebirth"),
                    "CurrentAttackNumber"), "Laser banks ordinary Attack preview");
                Near(Number(preview, "Defense"), Number(result, "PublishedNextDefense"),
                    "Laser publishes ordinary Defense preview");
            }
            else
            {
                var hard = Field(result, "Rebirth");
                foreach (var field in new[] {"CurrentAttackNumber", "CurrentDefenseNumber",
                             "BossMulti", "TimeMulti", "OldBossMulti", "OldTimeMulti"})
                    Near(1.0, Number(hard, field), Types[i] + " hard-resets " + field);
                Near(1.0, Number(result, "PublishedNextAttack"),
                    Types[i] + " hard-resets next Attack");
                Near(1.0, Number(result, "PublishedNextDefense"),
                    Types[i] + " hard-resets next Defense");
            }
            if (i == 5) Equal(0, Convert.ToInt32(Field(result, "TrollCounter")),
                "Troll entry resets its cadence counter");
        }
    }

    private static void OfflineMatrix()
    {
        for (var i = 0; i < Types.Length; i++)
        {
            var state = TransitionState(ChallengeType(i), 0);
            Set(state, "InChallenge", true);
            Set(state, "ChallengeSeconds", 10.0);
            Set(state, "OrdinaryOfflineProgressSeconds", 20.0);
            var rebirth = Field(state, "Rebirth");
            Set(rebirth, "RunSeconds", 100.0);
            var result = Call("NGUInjector.Autopilot.ChallengeMechanics", "ApplyOffline",
                state, 5.0);
            var frozen = i == 2 || i == 3 || i == 5;
            var blind = i == 8;
            Near(frozen ? 20.0 : 25.0, Number(result, "OrdinaryOfflineProgressSeconds"),
                Types[i] + " ordinary offline matrix");
            Near(frozen ? 100.0 : 105.0, Number(Field(result, "Rebirth"), "RunSeconds"),
                Types[i] + " reset-local offline matrix");
            Near(frozen || blind ? 10.0 : 15.0, Number(result, "ChallengeSeconds"),
                Types[i] + " challenge-timer offline matrix");
        }
    }

    private static void CompletionMatrix()
    {
        for (var i = 0; i < Types.Length; i++)
        for (var difficulty = 0; difficulty < 3; difficulty++)
        {
            var state = TransitionState(ChallengeType(i), difficulty);
            Set(state, "InChallenge", true);
            Set(state, "ChallengeSeconds", 77.0);
            Set(state, "ActiveFlags", Enumerable.Range(0, 11).Select(x => x == i).ToArray());
            var rebirth = Field(state, "Rebirth");
            Set(rebirth, "BossId", 123);
            Set(rebirth, "CurrentAttackNumber", 456.0);
            var counts = Field(state, "Counts");
            Set(counts, "RawNormal", 1);
            Set(counts, "RawEvil", 2);
            Set(counts, "RawSadistic", 3);
            Set(counts, "SerializedMaximum", Maxima[i]);
            var result = Call("NGUInjector.Autopilot.ChallengeMechanics",
                "ApplyCompletion", state);
            Equal(123, Convert.ToInt32(Field(Field(result, "Rebirth"), "BossId")),
                Types[i] + " completion is not a rebirth");
            Near(456.0, Number(Field(result, "Rebirth"), "CurrentAttackNumber"),
                Types[i] + " completion preserves Number");
            True(!(bool)Field(result, "InChallenge"), Types[i] + " completion clears global flag");
            Equal(0, ((bool[])Field(result, "ActiveFlags")).Count(x => x),
                Types[i] + " completion clears one-hot flags");
            var after = Field(result, "Counts");
            var expectedNormal = 1 + (difficulty == 0 || i == 0 ? 1 : 0);
            var expectedEvil = 2 + (difficulty == 1 ? 1 : 0);
            var expectedSad = 3 + (difficulty == 2 ? 1 : 0);
            Equal(expectedNormal, Convert.ToInt32(Field(after, "RawNormal")),
                Types[i] + " Normal raw completion delta");
            Equal(expectedEvil, Convert.ToInt32(Field(after, "RawEvil")),
                Types[i] + " Evil raw completion delta");
            Equal(expectedSad, Convert.ToInt32(Field(after, "RawSadistic")),
                Types[i] + " Sadistic raw completion delta");
        }
    }

    private static void NativeTimeWrites()
    {
        for (var i = 0; i < Types.Length; i++)
        {
            var expected = i == 0 || i == 1 || i == 3 || i == 4 ? 50 : 80;
            Equal(expected, Convert.ToInt32(Call("NGUInjector.Autopilot.ChallengeMechanics",
                    "ApplyNativeBestTimeWrite", ChallengeType(i), 50, 80.9)),
                Types[i] + " native min/latest integer write");
        }
    }

    private static void TimingEvidence()
    {
        var ledger = New("NGUInjector.Autopilot.ChallengeTimingLedger");
        var key = TimingKey("build-a", 0, 0, 57, "policy-a");
        Record(ledger, key, "EmpiricalObservation", 90.0, 10.0, 20.0, 100.0);
        var estimate = Estimate(ledger, key);
        True(!(bool)Field(estimate, "AdmissionGrade"),
            "one empirical sample cannot authorize admission");
        True(!(bool)Field(estimate, "P90LabelAllowed"),
            "uncalibrated sample cannot claim a probability label");
        Equal(0, ((string)Field(estimate, "QuantileLabel")).Length,
            "uncalibrated quantile label is empty");
        for (var i = 1; i < 20; i++)
            Record(ledger, key, "EmpiricalObservation", i < 18 ? 100.0 : 101.0,
                0.0, 20.0, 100.0);
        estimate = Estimate(ledger, key);
        True((bool)Field(estimate, "AdmissionGrade"),
            "twenty exact-key samples with 90% upper coverage are calibrated");
        True((bool)Field(estimate, "P90LabelAllowed"),
            "calibrated coverage permits the probability label");
        Equal("p90", (string)Field(estimate, "QuantileLabel"),
            "calibrated interval owns its explicit label");
        Near(.9, Number(estimate, "EmpiricalCoverage"), "coverage is keyed and exact");

        var otherDifficulty = TimingKey("build-a", 0, 1, 57, "policy-a");
        Equal(0, Convert.ToInt32(CallInstance(ledger, "CountFor", otherDifficulty)),
            "difficulty separates bot-owned timing samples");
        var otherOrdinal = TimingKey("build-a", 0, 0, 58, "policy-a");
        Equal(0, Convert.ToInt32(CallInstance(ledger, "CountFor", otherOrdinal)),
            "target separates bot-owned timing samples");

        var exactKey = TimingKey("build-a", 1, 0, 58, "policy-b");
        Record(ledger, exactKey, "NativeFormulaSimulation", 30.0, 0.0, 4.0, 31.0);
        var exact = Estimate(ledger, exactKey);
        True((bool)Field(exact, "AdmissionGrade"),
            "native-formula simulation is admission grade immediately");
        True(!(bool)Field(exact, "P90LabelAllowed"),
            "deterministic evidence is not mislabeled as a quantile");
    }

    private static void IntentSelection()
    {
        var key = TimingKey("build", 0, 0, 57, "policy");
        var intents = new[]
        {
            Intent(0, 1, "BASIC-1", "epoch", key, 50.0),
            Intent(1, 1, "NOAUG-1", "epoch", key, 20.0),
            Intent(2, 1, "24HR-1", "epoch", key, 30.0)
        };
        var selection = Call("NGUInjector.Autopilot.ChallengeIntentSelector", "SelectOne",
            ArrayOf("NGUInjector.Autopilot.ChallengeIntent", intents));
        Equal(1, ((Array)Field(selection, "Executable")).Length,
            "multiple candidates produce exactly one executable intent");
        Equal(2, ((Array)Field(selection, "Alternatives")).Length,
            "runner-ups stay diagnostic-only");
        Equal("NOAUG-1", (string)Field(Field(selection, "Selected"), "ProfileCode"),
            "shortest exact route is selected");
        True((bool)Call("NGUInjector.Autopilot.ChallengeIntentSelector", "StillValid",
                intents[1], ChallengeType(1), 1, "epoch"),
            "matching state version retains intent validity");
        True(!(bool)Call("NGUInjector.Autopilot.ChallengeIntentSelector", "StillValid",
                intents[1], ChallengeType(1), 1, "epoch-new"),
            "stale state version invalidates without a fallback");
    }

    private static void HundredLevelBudget()
    {
        True((bool)Call("NGUInjector.Autopilot.HundredLevelBudget", "CanLevel", 99L),
            "99 shared levels leaves one slot");
        True(!(bool)Call("NGUInjector.Autopilot.HundredLevelBudget", "CanLevel", 100L),
            "100 shared levels makes native canLevel false");
        Equal(0, Convert.ToInt32(Call("NGUInjector.Autopilot.HundredLevelBudget",
                "TrueRemaining", 100L)), "true remaining clamps to zero");
        Equal(1, Convert.ToInt32(Call("NGUInjector.Autopilot.HundredLevelBudget",
                "NativeDisplayRemaining", 100L)), "native display misleadingly remains one");
        Equal(0L, Convert.ToInt64(Call("NGUInjector.Autopilot.HundredLevelBudget",
                "ApplyOrdinaryRebirth", 100L)), "ordinary rebirth renews the shared budget");

        var requests = new List<object>();
        for (var i = 0; i < 11; i++)
        {
            requests.Add(BudgetRequest(i, 20, 20, 0));
            True((bool)Call("NGUInjector.Autopilot.HundredLevelBudget",
                    "ConsumesSharedSlot", Enum.ToObject(
                        Type("NGUInjector.Autopilot.HundredLevelTrack"), i)) == (i < 9),
                "100-Level shared consumer matrix track " + i);
        }
        var decision = Call("NGUInjector.Autopilot.HundredLevelBudget", "Allocate", 0L,
            ArrayOf("NGUInjector.Autopilot.HundredLevelBudgetRequest", requests.ToArray()));
        Equal(100L, Convert.ToInt64(Field(decision, "SpentAfter")),
            "nine consumers share one 100-level cap");
        var grants = ((Array)Field(decision, "Grants")).Cast<object>().ToArray();
        Equal(20, Convert.ToInt32(Field(grants.Single(x => EnumIndex(Field(x, "Track")) == 9),
                "GrantedLevels")), "Basic Training does not consume the shared budget");
        Equal(20, Convert.ToInt32(Field(grants.Single(x => EnumIndex(Field(x, "Track")) == 10),
                "GrantedLevels")), "NGU does not consume the shared budget");

        var low = BudgetRequest(0, 1, 1, 1);
        var high = BudgetRequest(1, 1, 1, 2);
        var forward = BudgetGrants(99L, low, high);
        var reverse = BudgetGrants(99L, high, low);
        Equal(1, forward[1], "higher-priority competing consumer wins the final slot");
        Equal(forward[0], reverse[0], "budget result is independent of request order");
        Equal(forward[1], reverse[1], "priority result is deterministic after reversal");
        var zeroQuota = BudgetGrants(0L, BudgetRequest(3, 10, 0, 99));
        Equal(0, zeroQuota[3], "zero quota stops a consuming subsystem explicitly");
    }

    private static void TrollCadence()
    {
        var factors = new[] {120, 110, 100, 90, 85, 80, 75};
        for (var completed = 0; completed < factors.Length; completed++)
        {
            Equal(factors[completed], Convert.ToInt32(Call(
                    "NGUInjector.Autopilot.TrollChallengeMechanics", "Factor", completed)),
                "Troll exact factor " + completed);
            for (var ordinal = 1; ordinal <= 5; ordinal++)
            {
                var next = Call("NGUInjector.Autopilot.TrollChallengeMechanics", "NextEvent",
                    (ordinal - 1) * factors[completed], completed);
                Equal(ordinal * factors[completed], Convert.ToInt32(Field(next, "EventCounter")),
                    "Troll event counter " + ordinal);
                Equal(ordinal == 5 ? 1 : 0, EnumIndex(Field(next, "Kind")),
                    "every fifth Troll event is big");
            }
        }
        var run = New("NGUInjector.Autopilot.TrollRunState");
        Set(run, "Counter", 480);
        foreach (var field in new[] {"EquipmentDisabled", "NguDisabled", "BeardsDisabled",
                     "WandoosDisabled", "MenuSwapped", "BossDivided"}) Set(run, field, true);
        var reset = Call("NGUInjector.Autopilot.TrollChallengeMechanics",
            "ApplyOrdinaryRebirth", run);
        Equal(480, Convert.ToInt32(Field(reset, "Counter")),
            "ordinary rebirth preserves Troll counter");
        True(new[] {"EquipmentDisabled", "NguDisabled", "BeardsDisabled", "WandoosDisabled",
                "MenuSwapped", "BossDivided"}.All(x => !(bool)Field(reset, x)),
            "ordinary rebirth clears all active Troll penalties");
        var terminal = Call("NGUInjector.Autopilot.TrollChallengeMechanics",
            "ApplyEntryCompletionOrFailure", run);
        Equal(0, Convert.ToInt32(Field(terminal, "Counter")),
            "Troll entry/completion/failure resets its counter");
        True(!(bool)Call("NGUInjector.Autopilot.TrollChallengeMechanics",
                "BigTrollOutcomeReachable", 0), "big Troll switch zero is unreachable");
        for (var i = 1; i <= 6; i++) True((bool)Call(
                "NGUInjector.Autopilot.TrollChallengeMechanics", "BigTrollOutcomeReachable", i),
            "big Troll switch " + i + " is reachable");
        var planned = Call("NGUInjector.Autopilot.TrollChallengeMechanics",
            "EvaluatePlannedReset", 480, 0, 1, 120, false);
        True(!(bool)Field(planned, "Allowed"),
            "reset one event before the fifth Troll is rejected when recovery strands it");
        True((bool)Call("NGUInjector.Autopilot.TrollChallengeMechanics",
                "PopupChoosesNo", 1, 1), "popup switch at one chooses No");
        True((bool)Call("NGUInjector.Autopilot.TrollChallengeMechanics",
                "PopupChoosesNo", 48, 48), "popup switch at 48 chooses No");
        True((bool)Call("NGUInjector.Autopilot.TrollChallengeMechanics",
                "PopupComplete", 50), "popup chain completes at 50");
    }

    private static void LaserPhases()
    {
        var build = LaserInput(0, 0, 0.0, 0.0, 100.0, 120.0, 40.0, true);
        var buildDecision = Call("NGUInjector.Autopilot.LaserChallengeMechanics",
            "Evaluate", build);
        Equal(1, EnumIndex(Field(buildDecision, "Phase")),
            "reset-and-rebuild win selects Number-building phase");
        True(!(bool)Field(buildDecision, "ForbidRebirth"),
            "Number-building phase permits an exact planned reset");
        var commit = LaserInput(1, 1, .5, .2, 10.0, 12.0, 30.0, true);
        var commitDecision = Call("NGUInjector.Autopilot.LaserChallengeMechanics",
            "Evaluate", commit);
        Equal(2, EnumIndex(Field(commitDecision, "Phase")),
            "direct paired finish win selects commit phase");
        True((bool)Field(commitDecision, "ForbidRebirth"),
            "commit phase freezes pair-destroying rebirth");
        Near(12.0, Number(commitDecision, "DirectFinishSeconds"),
            "Laser direct finish is the slower paired track");
        var unknownReset = LaserInput(1, 0, .1, 0.0, 10.0, 12.0, -1.0, true);
        var failClosed = Call("NGUInjector.Autopilot.LaserChallengeMechanics",
            "Evaluate", unknownReset);
        Equal(2, EnumIndex(Field(failClosed, "Phase")),
            "material pair progress commits when reset successor is unknown");
    }

    private static void DeadlineAndRace()
    {
        var safe = Call("NGUInjector.Autopilot.ChallengeMechanics",
            "EvaluateTwentyFourHourDeadline", 1000.0, 2000.0, 60.0);
        Near(83400.0, Number(safe, "DeadlineSlackSeconds"),
            "24-Hour reports positive active-time slack");
        True(!(bool)Field(safe, "AtRisk"), "large positive deadline slack is safe");
        var missed = Call("NGUInjector.Autopilot.ChallengeMechanics",
            "EvaluateTwentyFourHourDeadline", 86000.0, 500.0, 60.0);
        Near(-100.0, Number(missed, "DeadlineSlackSeconds"),
            "24-Hour negative slack is never clamped");
        True((bool)Field(missed, "Missed"), "negative deadline slack is marked missed");
        var race = Call("NGUInjector.Autopilot.ChallengeMechanics",
            "EvaluateTwentyFourHourFrame", 86400.0, 58, 57);
        True((bool)Field(race, "FailureDispatched"),
            "deadline failure dispatches at exactly 86400");
        True((bool)Field(race, "CompletionDispatched"),
            "completion check independently dispatches in the same frame");
        True((bool)Field(race, "NativeSameFrameRace"),
            "same-frame native race is exposed, not recommended");
    }

    private static void TitanAndBatch()
    {
        var elapsed = new double[14];
        elapsed[0] = 3299.0; // T1 is 301 seconds from its 3600-second maturity.
        var valued = new bool[14];
        valued[0] = true;
        var kills = new int[14];
        kills[0] = 2;
        var clocks = NewArgs("NGUInjector.Autopilot.TitanClockSnapshot", elapsed);
        var vector = Call("NGUInjector.Autopilot.ChallengeMechanics",
            "EvaluateTitanClockLoss", clocks, valued, kills, 0, 0, 0);
        var item = ((Array)Field(vector, "Items")).Cast<object>().Single();
        Equal(301, Convert.ToInt32(Field(item, "RemainingBeforeSeconds")),
            "Titan at 301 seconds is represented, not hidden by a five-minute guard");
        Equal(3299, Convert.ToInt32(Field(item, "LostMaturitySeconds")),
            "Titan vector prices exact destroyed maturity");
        Equal(6598L, Convert.ToInt64(Field(vector, "TotalCycleDelaySeconds")),
            "Titan vector applies the requested future-kill horizon");

        var step1 = BatchStep(10.0, 3.0, 2.0);
        var step2 = BatchStep(20.0, 4.0, 1.0);
        var batch = Call("NGUInjector.Autopilot.ChallengeMechanics", "EvaluateBatch",
            ArrayOf("NGUInjector.Autopilot.ChallengeBatchStep", step1, step2), 100.0);
        Equal(1, Convert.ToInt32(Field(batch, "RecoveryCharges")),
            "two consecutive challenges charge final recovery once");
        Near(134.0, Number(batch, "TotalSeconds"),
            "batch cost is clears plus Titan vector plus one recovery minus downstream savings");
    }

    private static int ExpectedTarget(int type, int c)
    {
        switch (type)
        {
            case 0: return 57;
            case 1: return 58;
            case 2: return Math.Min(299, 57 + 26 * c);
            case 3: return 57;
            case 4: return 65;
            case 5: return 68 + 15 * c;
            case 6: return 39 + 5 * c;
            case 7: return 2 + c;
            case 8:
            case 9: return 57 + 10 * c;
            case 10: return 57 + 15 * c;
            default: throw new ArgumentOutOfRangeException("type");
        }
    }

    private static object TransitionState(object type, int difficulty)
    {
        var state = New("NGUInjector.Autopilot.ChallengeTransitionState");
        Set(state, "Type", type);
        Set(state, "Difficulty", Enum.ToObject(
            Type("NGUInjector.Autopilot.ChallengeDifficultyBand"), difficulty));
        return state;
    }

    private static object ChallengeType(int index)
    {
        return Enum.ToObject(Type(
            "NGUInjector.AllocationProfiles.RebirthStuff.ChallengeType"), index);
    }

    private static object TimingKey(string build, int type, int difficulty, int target,
        string policy)
    {
        var key = New("NGUInjector.Autopilot.ChallengeTimingKey");
        Set(key, "AssemblySha256", build);
        Set(key, "Type", ChallengeType(type));
        Set(key, "Difficulty", Enum.ToObject(
            Type("NGUInjector.Autopilot.ChallengeDifficultyBand"), difficulty));
        Set(key, "CompletedBefore", 0);
        Set(key, "ExactTarget", target);
        Set(key, "ResetPolicySignature", policy);
        return key;
    }

    private static void Record(object ledger, object key, string evidence,
        double online, double offline, double recovery, double upper)
    {
        var sample = New("NGUInjector.Autopilot.ChallengeTimingSample");
        Set(sample, "Key", key);
        Set(sample, "EvidenceKind", EnumValue(
            "NGUInjector.Autopilot.ChallengeTimingEvidenceKind", evidence));
        Set(sample, "ObservedOnlineSeconds", online);
        Set(sample, "ObservedOfflineSeconds", offline);
        Set(sample, "RecoverySeconds", recovery);
        Set(sample, "PredictedUpperSeconds", upper);
        CallInstance(ledger, "Record", sample);
    }

    private static object Estimate(object ledger, object key)
    {
        var method = ledger.GetType().GetMethod("TryEstimate",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var args = new[] {key, null};
        True((bool)method.Invoke(ledger, args), "timing estimate exists");
        return args[1];
    }

    private static object Intent(int type, int completion, string code, string epoch,
        object key, double total)
    {
        var intent = New("NGUInjector.Autopilot.ChallengeIntent");
        Set(intent, "Type", ChallengeType(type));
        Set(intent, "Completion", completion);
        Set(intent, "ProfileCode", code);
        Set(intent, "ExpectedStateVersion", epoch);
        Set(intent, "TimingKey", key);
        Set(intent, "TotalRouteSeconds", total);
        return intent;
    }

    private static object BudgetRequest(int track, int requested, int quota, int priority)
    {
        var request = New("NGUInjector.Autopilot.HundredLevelBudgetRequest");
        Set(request, "Track", Enum.ToObject(
            Type("NGUInjector.Autopilot.HundredLevelTrack"), track));
        Set(request, "RequestedLevels", requested);
        Set(request, "AuthorizedQuota", quota);
        Set(request, "Priority", priority);
        return request;
    }

    private static Dictionary<int, int> BudgetGrants(long spent, params object[] requests)
    {
        var decision = Call("NGUInjector.Autopilot.HundredLevelBudget", "Allocate", spent,
            ArrayOf("NGUInjector.Autopilot.HundredLevelBudgetRequest", requests));
        return ((Array)Field(decision, "Grants")).Cast<object>().ToDictionary(
            x => EnumIndex(Field(x, "Track")),
            x => Convert.ToInt32(Field(x, "GrantedLevels")));
    }

    private static object LaserInput(long aug, long upgrade, double augProgress,
        double upgradeProgress, double augSeconds, double upgradeSeconds,
        double resetSeconds, bool gold)
    {
        var input = New("NGUInjector.Autopilot.LaserPhaseInput");
        Set(input, "AugmentLevel", aug);
        Set(input, "UpgradeLevel", upgrade);
        Set(input, "AugmentProgress", augProgress);
        Set(input, "UpgradeProgress", upgradeProgress);
        Set(input, "AugmentFinishSeconds", augSeconds);
        Set(input, "UpgradeFinishSeconds", upgradeSeconds);
        Set(input, "ResetAndRebuildSeconds", resetSeconds);
        Set(input, "DirectGoldLedgerFeasible", gold);
        return input;
    }

    private static object BatchStep(double clear, double titan, double saved)
    {
        var step = New("NGUInjector.Autopilot.ChallengeBatchStep");
        Set(step, "ClearSeconds", clear);
        Set(step, "TitanClockResetCostSeconds", titan);
        Set(step, "DownstreamTimeSavedSeconds", saved);
        return step;
    }

    private static Type Type(string name)
    {
        var type = _assembly.GetType(name, false);
        if (type == null) throw new Exception("missing type " + name);
        return type;
    }

    private static object EnumValue(string typeName, string value)
    {
        return Enum.Parse(Type(typeName), value);
    }

    private static int EnumIndex(object value)
    {
        return Convert.ToInt32(value);
    }

    private static object New(string name)
    {
        return Activator.CreateInstance(Type(name), true);
    }

    private static object NewArgs(string name, params object[] args)
    {
        return Activator.CreateInstance(Type(name),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, args, null);
    }

    private static object Call(string typeName, string methodName, params object[] args)
    {
        var methods = Type(typeName).GetMethods(BindingFlags.Static | BindingFlags.Public
                                                 | BindingFlags.NonPublic)
            .Where(x => x.Name == methodName && x.GetParameters().Length == args.Length).ToArray();
        if (methods.Length != 1)
            throw new Exception(typeName + "." + methodName + " overload is ambiguous/missing");
        return methods[0].Invoke(null, args);
    }

    private static object CallInstance(object target, string methodName, params object[] args)
    {
        var methods = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public
                                                   | BindingFlags.NonPublic)
            .Where(x => x.Name == methodName && x.GetParameters().Length == args.Length).ToArray();
        if (methods.Length != 1)
            throw new Exception(target.GetType().Name + "." + methodName
                                + " overload is ambiguous/missing");
        return methods[0].Invoke(target, args);
    }

    private static void Set(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public
                                                    | BindingFlags.NonPublic);
        if (field == null) throw new Exception("missing field " + target.GetType().Name + "." + name);
        field.SetValue(target, value);
    }

    private static object Field(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public
                                                    | BindingFlags.NonPublic);
        if (field == null) throw new Exception("missing field " + target.GetType().Name + "." + name);
        return field.GetValue(target);
    }

    private static double Number(object target, string name)
    {
        return Convert.ToDouble(Field(target, name));
    }

    private static Array ArrayOf(string elementType, params object[] values)
    {
        var array = Array.CreateInstance(Type(elementType), values.Length);
        for (var i = 0; i < values.Length; i++) array.SetValue(values[i], i);
        return array;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception error)
        {
            _failures++;
            var current = error is TargetInvocationException && error.InnerException != null
                ? error.InnerException : error;
            Console.WriteLine("FAIL " + name + ": " + current.Message);
        }
    }

    private static void True(bool value, string message)
    {
        _assertions++;
        if (!value) throw new Exception(message);
    }

    private static void Equal(long expected, long actual, string message)
    {
        _assertions++;
        if (expected != actual)
            throw new Exception(message + ": expected " + expected + ", actual " + actual);
    }

    private static void Equal(string expected, string actual, string message)
    {
        _assertions++;
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Exception(message + ": expected " + expected + ", actual " + actual);
    }

    private static void Near(double expected, double actual, string message)
    {
        _assertions++;
        var tolerance = Math.Max(1e-11, Math.Abs(expected) * 1e-10);
        if (Math.Abs(expected - actual) > tolerance)
            throw new Exception(message + ": expected " + expected + ", actual " + actual);
    }
}
