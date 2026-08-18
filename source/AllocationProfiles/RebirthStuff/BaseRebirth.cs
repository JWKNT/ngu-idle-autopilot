using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using NGUInjector.Autopilot;
using NGUInjector.Managers;

/*
FILE PURPOSE

BaseRebirth centralizes the safety gates and native controller path shared by rebirth strategies,
including fights, challenges, harvest/spell preparation, and progression locks. A recommendation
is not sufficient: the final preflight must still prove the live state safe. Derived classes only
decide availability; this base owns the irreversible transaction contract.
*/
namespace NGUInjector.AllocationProfiles.RebirthStuff
{
    internal enum ChallengeType
    {
        Basic,
        NoAug,
        TwentyFourHour,
        OneHundredLC,
        NoEquip,
        Troll,
        NoRebirth,
        LaserSword,
        Blind,
        NoNGU,
        NoTimeMachine
    }

    internal class RCTarget
    {
        internal ChallengeType Challenge { get; set; }
        internal int Index { get; set; }

        public override string ToString()
        {
            return $"{Challenge}-{Index}";
        }
    }

    internal abstract class BaseRebirth
    {
        internal static BaseRebirth CreateRebirth(double target, string type, string[] challenges)
        {
            type = type.ToUpper();
            BaseRebirth created;
            if (type == "TIME")
            {
                created = new TimeRebirth
                {
                    CharObj = Main.Character,
                    ChallengeTargets = ParseChallenges(challenges),
                    RebirthController = Main.Character.rebirth,
                    RebirthTime = target
                };
            }
            else if (type == "NUMBER")
            {
                created = new NumberRebirth
                {
                    CharObj = Main.Character,
                    ChallengeTargets = ParseChallenges(challenges),
                    RebirthController = Main.Character.rebirth,
                    MultTarget = target
                };
            }
            else if (type == "BOSSES")
            {
                created = new BossNumRebirth
                {
                    CharObj = Main.Character,
                    ChallengeTargets = ParseChallenges(challenges),
                    RebirthController = Main.Character.rebirth,
                    NumBosses = target
                };
            }
            else
                created = new NoRebirth();
            created.BindChallengeIntent(challenges);
            return created;
        }

        private static Dictionary<string, ChallengeType> CMap = new Dictionary<string, ChallengeType>
        {
            {"BASIC", ChallengeType.Basic},
            {"NOAUG", ChallengeType.NoAug},
            {"24HR", ChallengeType.TwentyFourHour},
            {"100LC", ChallengeType.OneHundredLC},
            {"NOEC", ChallengeType.NoEquip},
            {"TC", ChallengeType.Troll},
            {"NORB", ChallengeType.NoRebirth},
            {"LSC", ChallengeType.LaserSword},
            {"BLIND", ChallengeType.Blind},
            {"NONGU", ChallengeType.NoNGU},
            {"NOTM", ChallengeType.NoTimeMachine},
        };

        internal RCTarget[] ChallengeTargets { get; set; }
        private bool _challengeIntentRequested;
        private ChallengeIntent _selectedChallengeIntent;
        protected Rebirth RebirthController;
        internal abstract bool RebirthAvailable();
        protected Character CharObj;
        protected BaseRebirth()
        {
            CharObj = Main.Character;
            RebirthController = CharObj.rebirth;
        }

        private void BindChallengeIntent(string[] rawTargets)
        {
            _challengeIntentRequested = rawTargets != null && rawTargets.Length > 0;
            _selectedChallengeIntent = null;
            if (!_challengeIntentRequested || ChallengeTargets == null
                || ChallengeTargets.Length != 1 || CharObj == null) return;
            try
            {
                string evidence;
                var admissions = ChallengeStrategyPlanner.Recommend(CharObj, out evidence);
                if (admissions == null || admissions.Count != 1) return;
                var target = ChallengeTargets[0];
                var admission = admissions[0];
                if (admission != null && admission.Intent != null
                    && admission.Type == target.Challenge
                    && admission.Completion == target.Index
                    && string.Equals(admission.ProfileCode,
                        ChallengeMechanics.Code(target.Challenge) + "-" + target.Index,
                        StringComparison.OrdinalIgnoreCase))
                    _selectedChallengeIntent = admission.Intent;
            }
            catch
            {
                // Missing/unstable task-16 evidence is a hold, never a legacy fallback entry.
            }
        }

        internal static RCTarget[] ParseChallenges(string[] challenges)
        {
            if (challenges == null)
                return new RCTarget[0];

            var parsed = new List<RCTarget>();
            foreach (var c in challenges.Select(x => x.ToUpper()))
            {
                if (!c.Contains("-"))
                    continue;

                var split = c.Split(new[] {'-'}, StringSplitOptions.None);
                if (split.Length < 2)
                    continue;
                var challenge = split[0].ToUpper();
                if (!CMap.ContainsKey(challenge))
                    continue;

                if (!int.TryParse(split[1], out var index))
                    continue;

                if (parsed.Any(x => x.Challenge == CMap[challenge] && x.Index == index))
                    continue;
                parsed.Add(new RCTarget
                {
                    Index = index,
                    Challenge = CMap[challenge]
                });
            }

            return parsed.ToArray();
        }

        /*
        VERIFIED REBIRTH/CHALLENGE BOUNDARY

        Native adapter return is never accepted as commit evidence. ResetPostconditions requires the exact
        counter/timer transition, challenge one-hot/type/timer proof, hard-versus-Laser Number transform,
        Boss/Titan reset, and no unexpected challenge. Every verified reset closes the old run epoch before
        returning; an exact no-op is rejected, while a partial/wrong poststate quarantines automation.
        */
        protected bool EngageChalRebirth(ChallengeType expectedChallenge)
        {
            var beforeAudit = RebirthAuditSnapshot.Capture(CharObj);
            var before = LiveResetSnapshot.Capture(CharObj);
            var nativeType = LiveResetSnapshot.NativeChallengeTypeToken(CharObj,
                expectedChallenge);
            ResetNativeObservation invocation;
            try
            {
                var registry = NativeBindingRegistry.Create(typeof(Character).Assembly,
                    Main.GameAssemblySha256);
                var native = registry.CreateMutationAdapters();
                invocation = ResetNativeObservation.From(native.EnterChallenge(
                    RebirthController, NativeChallenge(expectedChallenge)));
            }
            catch (Exception error)
            {
                invocation = new ResetNativeObservation
                {
                    InvocationAttempted = true, ReturnedNormally = false,
                    Reason = error.GetType().Name + ": " + error.Message, Exception = error
                };
            }
            var after = LiveResetSnapshot.Capture(CharObj);
            var afterAudit = RebirthAuditSnapshot.Capture(CharObj);
            var proof = ResetPostconditions.VerifyChallenge(before, after,
                expectedChallenge, nativeType);
            Main.LogDiagnostic("Challenge reset audit (" + expectedChallenge + "): pre{"
                               + beforeAudit + "} post{" + afterAudit + "}; " + proof.Reason
                               + "; native=" + invocation.Reason);
            return PublishResetResult(before, after, proof, invocation,
                "Entered " + expectedChallenge + " challenge");
        }

        protected bool BaseRebirthChecks()
        {
            return CharObj.rebirthTime.totalseconds >= RebirthController.minRebirthTime()
                   && CharObj.bossID > 0
                   && !CharObj.bossController.isFighting
                   && !CharObj.bossController.nukeBoss
                   && !CharObj.challenges.noRebirthChallenge.inChallenge;
        }

        protected bool EngageRebirth()
        {
            var beforeAudit = RebirthAuditSnapshot.Capture(CharObj);
            var before = LiveResetSnapshot.Capture(CharObj);
            if (before == null || before.CurrentDifficulty != before.NextDifficulty)
            {
                Main.LogAction("HOLD", "Ordinary rebirth cannot consume a pending difficulty selector");
                return false;
            }
            ResetNativeObservation invocation;
            try
            {
                var registry = NativeBindingRegistry.Create(typeof(Character).Assembly,
                    Main.GameAssemblySha256);
                invocation = ResetNativeObservation.From(registry.CreateMutationAdapters()
                    .InvokeOrdinaryRebirth(RebirthController));
            }
            catch (Exception error)
            {
                invocation = new ResetNativeObservation
                {
                    InvocationAttempted = true, ReturnedNormally = false,
                    Reason = error.GetType().Name + ": " + error.Message, Exception = error
                };
            }
            var after = LiveResetSnapshot.Capture(CharObj);
            var afterAudit = RebirthAuditSnapshot.Capture(CharObj);
            var proof = ResetPostconditions.VerifyOrdinary(before, after);
            Main.LogDiagnostic("Ordinary reset audit: pre{" + beforeAudit + "} post{"
                               + afterAudit + "}; " + proof.Reason + "; native="
                               + invocation.Reason);
            return PublishResetResult(before, after, proof, invocation,
                "Normal rebirth confirmed");
        }

        private bool PublishResetResult(ResetExecutionSnapshot before,
            ResetExecutionSnapshot after, ResetProof proof, ResetNativeObservation invocation,
            string committedSummary)
        {
            if (proof != null && proof.Satisfied)
            {
                ResetEpochTransition.Close(CharObj, after, committedSummary);
                Main.LogAction("REBIRTH", committedSummary
                    + (invocation != null && !invocation.ReturnedNormally
                        ? " [committed with native exception]" : " [exact postcondition]"));
                return true;
            }
            if (ResetPostconditions.ExactStateMatches(before, after))
            {
                Main.LogAction("REJECTED", "Reset produced an exact no-op: "
                                           + (proof == null ? "missing proof" : proof.Reason));
                return false;
            }
            var reason = "Reset produced a partial or wrong poststate: "
                         + (proof == null ? "missing proof" : proof.Reason);
            ResetEpochTransition.Quarantine(reason);
            Main.LogAction("REJECTED", reason);
            return false;
        }

        private static NativeChallengeCall NativeChallenge(ChallengeType challenge)
        {
            switch (challenge)
            {
                case ChallengeType.Basic: return NativeChallengeCall.Basic;
                case ChallengeType.NoAug: return NativeChallengeCall.NoAugs;
                case ChallengeType.TwentyFourHour: return NativeChallengeCall.TwentyFourHour;
                case ChallengeType.OneHundredLC: return NativeChallengeCall.OneHundredLevel;
                case ChallengeType.NoEquip: return NativeChallengeCall.NoEquipment;
                case ChallengeType.Troll: return NativeChallengeCall.Troll;
                case ChallengeType.NoRebirth: return NativeChallengeCall.NoRebirth;
                case ChallengeType.LaserSword: return NativeChallengeCall.LaserSword;
                case ChallengeType.Blind: return NativeChallengeCall.Blind;
                case ChallengeType.NoNGU: return NativeChallengeCall.NoNgu;
                case ChallengeType.NoTimeMachine: return NativeChallengeCall.NoTimeMachine;
                default: throw new ArgumentOutOfRangeException("challenge");
            }
        }

        private sealed class RebirthAuditSnapshot
        {
            private string Value;
            private double NumberAttack;
            private double ArbitraryPoints;
            private int Boss;
            private long Rebirths;
            private double RunSeconds;
            private string Challenge;

            internal static RebirthAuditSnapshot Capture(Character character)
            {
                if (character == null)
                    return new RebirthAuditSnapshot {Value = "Character=null"};
                return new RebirthAuditSnapshot
                {
                    Value = Build(character),
                    NumberAttack = character.attackMulti,
                    ArbitraryPoints = character.arbitrary.curArbitraryPoints,
                    Boss = character.bossID,
                    Rebirths = character.stats.rebirthNumber,
                    RunSeconds = character.rebirthTime == null ? 0.0 : character.rebirthTime.totalseconds,
                    Challenge = character.challenges == null || !character.challenges.inChallenge
                        ? "no challenge"
                        : "challenge " + character.challenges.curChallengeType
                };
            }

            public override string ToString()
            {
                return Value;
            }

            internal static string ConfirmedSummary(RebirthAuditSnapshot before,
                RebirthAuditSnapshot after, string action)
            {
                var inv = CultureInfo.InvariantCulture;
                var apDelta = after.ArbitraryPoints - before.ArbitraryPoints;
                var numberDelta = before.NumberAttack > 0.0
                    ? (after.NumberAttack / before.NumberAttack - 1.0) * 100.0
                    : 0.0;
                return action + " after " + FormatDuration(before.RunSeconds)
                       + " — Number " + FormatNumber(before.NumberAttack) + " → "
                       + FormatNumber(after.NumberAttack) + " ("
                       + numberDelta.ToString("+0.0;-0.0;0.0", inv) + "%)"
                       + "; AP " + apDelta.ToString("+0;-0;0", inv)
                       + "; Boss " + before.Boss + " → " + after.Boss
                       + "; rebirth #" + after.Rebirths
                       + "; " + after.Challenge;
            }

            private static string FormatNumber(double value)
            {
                if (double.IsNaN(value) || double.IsInfinity(value)) return "unknown";
                return value.ToString("0.###E+0", CultureInfo.InvariantCulture)
                    .Replace("E+", "e+").Replace("E-", "e-");
            }

            private static string FormatDuration(double seconds)
            {
                var total = Math.Max(0L, (long)Math.Floor(seconds));
                var days = total / 86400;
                var hours = total % 86400 / 3600;
                var minutes = total % 3600 / 60;
                var remainder = total % 60;
                if (days > 0) return days + "d " + hours + "h " + minutes + "m";
                if (hours > 0) return hours + "h " + minutes + "m";
                if (minutes > 0) return minutes + "m " + remainder + "s";
                return remainder + "s";
            }

            private static string Build(Character c)
            {
                if (c == null) return "Character=null";
                var inv = CultureInfo.InvariantCulture;
                return "numA=" + c.attackMulti.ToString("R", inv)
                       + ",numD=" + c.defenseMulti.ToString("R", inv)
                       + ",previewA=" + c.nextAttackMulti.ToString("R", inv)
                       + ",previewD=" + c.nextDefenseMulti.ToString("R", inv)
                       + ",AP=" + c.arbitrary.curArbitraryPoints
                       + ",bloodNumber=" + c.bloodMagic.rebirthPower.ToString("R", inv)
                       + ",difficulty=" + c.settings.rebirthDifficulty
                       + ",boss=" + c.bossID
                       + ",record=" + c.highestBoss + "/" + c.highestHardBoss
                       + "/" + c.highestSadisticBoss
                       + ",rebirths=" + c.stats.rebirthNumber
                       + ",time=" + c.rebirthTime.totalseconds.ToString("R", inv)
                       + ",challenge=" + ChallengeState(c);
            }

            private static string ChallengeState(Character c)
            {
                var s = c.challenges;
                var all = c.allChallenges;
                if (s == null || all == null) return "unavailable";
                return (s.inChallenge ? "active" : "none") + ":" + s.curChallengeType
                       + ";flags=" + Flag(s.basicChallenge.inChallenge, "BASIC")
                       + Flag(s.noAugsChallenge.inChallenge, "NOAUG")
                       + Flag(s.hour24Challenge.inChallenge, "24HR")
                       + Flag(s.levelChallenge10k.inChallenge, "100LC")
                       + Flag(s.noEquipmentChallenge.inChallenge, "NOEC")
                       + Flag(s.trollChallenge.inChallenge, "TC")
                       + Flag(s.noRebirthChallenge.inChallenge, "NORB")
                       + Flag(s.laserSwordChallenge.inChallenge, "LSC")
                       + Flag(s.blindChallenge.inChallenge, "BLIND")
                       + Flag(s.nguChallenge.inChallenge, "NONGU")
                       + Flag(s.timeMachineChallenge.inChallenge, "NOTM")
                       + ";counts=" + Counts("B", s.basicChallenge, all.basicChallenge.maxCompletions)
                       + Counts("A", s.noAugsChallenge, all.noAugsChallenge.maxCompletions)
                       + Counts("24", s.hour24Challenge, all.hour24Challenge.maxCompletions)
                       + Counts("100", s.levelChallenge10k, all.level100Challenge.maxCompletions)
                       + Counts("E", s.noEquipmentChallenge, all.noEquipmentChallenge.maxCompletions)
                       + Counts("T", s.trollChallenge, all.trollChallenge.maxCompletions)
                       + Counts("R", s.noRebirthChallenge, all.noRebirthChallenge.maxCompletions)
                       + Counts("L", s.laserSwordChallenge, all.laserSwordChallenge.maxCompletions)
                       + Counts("D", s.blindChallenge, all.blindChallenge.maxCompletions)
                       + Counts("N", s.nguChallenge, all.NGUChallenge.maxCompletions)
                       + Counts("M", s.timeMachineChallenge, all.timeMachineChallenge.maxCompletions);
            }

            private static string Flag(bool active, string name)
            {
                return active ? name + "," : string.Empty;
            }

            private static string Counts(string name, Challenge state, int max)
            {
                return name + "=" + state.curCompletions + "/" + state.curEvilCompletions
                       + "/" + state.curSadisticCompletions + "/" + max + ",";
            }
        }

        protected bool AnyChallengesValid()
        {
            RCTarget selected;
            string reason;
            return TryValidateSelectedChallengeIntent(out selected, out reason);
        }

        protected bool TryStartChallenge()
        {
            RCTarget selected;
            string reason;
            if (!TryValidateSelectedChallengeIntent(out selected, out reason))
            {
                Main.LogAction("HOLD", "Selected challenge intent held: " + reason
                                           + "; no runner-up or ordinary reset was attempted");
                return false;
            }
            return EngageChalRebirth(selected.Challenge);
        }

        private bool TryValidateSelectedChallengeIntent(out RCTarget selected,
            out string reason)
        {
            selected = null;
            reason = string.Empty;
            if (!_challengeIntentRequested)
            {
                reason = "no challenge intent was requested";
                return false;
            }
            if (!Main.AutopilotWants(x => x.AllowChallenges))
            {
                reason = "challenge authority is feature-disabled";
                return false;
            }
            if (ChallengeTargets == null || ChallengeTargets.Length != 1)
            {
                reason = "the installed profile does not contain exactly one challenge target";
                return false;
            }
            selected = ChallengeTargets[0];
            if (_selectedChallengeIntent == null)
            {
                reason = "the task-16 epoch-bound intent is missing";
                return false;
            }
            if (CharObj == null || CharObj.challenges == null || CharObj.allChallenges == null
                || CharObj.settings == null || CharObj.rebirthTime == null)
            {
                reason = "live challenge state is incomplete";
                return false;
            }
            if (!Main.IsAutomationReady || CharObj.challenges.inChallenge
                || !CharObj.challenges.unlocked || !BaseRebirthChecks())
            {
                reason = "final synchronized native entry gates are not all satisfied";
                return false;
            }
            if (LiveResetSnapshot.Difficulty(CharObj.settings.rebirthDifficulty)
                != LiveResetSnapshot.Difficulty(CharObj.nextRebirthDifficulty))
            {
                reason = "a pending difficulty selector cannot be consumed by challenge entry";
                return false;
            }
            var completed = CurrentCompletions(CharObj.allChallenges, selected.Challenge);
            var maximum = MaximumCompletions(CharObj.allChallenges, selected.Challenge);
            if (!ChallengeUnlocked(CharObj.allChallenges, selected.Challenge)
                || selected.Index <= 0 || selected.Index > maximum
                || selected.Index != completed + 1)
            {
                reason = "unlock/count/maximum no longer matches the exact next completion";
                return false;
            }
            var exactTarget = ChallengeMechanics.ExactTarget(selected.Challenge, completed);
            if (_selectedChallengeIntent.TimingKey == null
                || _selectedChallengeIntent.TimingKey.CompletedBefore != completed
                || _selectedChallengeIntent.TimingKey.ExactTarget != exactTarget)
            {
                reason = "task-16 timing key is stale";
                return false;
            }
            var difficultyBand = CharObj.settings.rebirthDifficulty == difficulty.normal
                ? ChallengeDifficultyBand.Normal
                : CharObj.settings.rebirthDifficulty == difficulty.evil
                    ? ChallengeDifficultyBand.Evil : ChallengeDifficultyBand.Sadistic;
            var stateVersion = (Main.GameAssemblySha256 ?? string.Empty) + "|s="
                               + ExecutionSafety.StateVersion + "|d=" + difficultyBand
                               + "|t=" + selected.Challenge + "|c=" + completed
                               + "|target=" + exactTarget + "|boss=" + CharObj.bossID
                               + "|run=" + Math.Floor(CharObj.rebirthTime.totalseconds);
            if (!ChallengeIntentSelector.StillValid(_selectedChallengeIntent,
                    selected.Challenge, selected.Index, stateVersion))
            {
                reason = "task-16 intent state version no longer matches live state";
                return false;
            }
            return true;
        }

        private static int CurrentCompletions(AllChallengesController c, ChallengeType type)
        {
            switch (type)
            {
                case ChallengeType.Basic: return c.basicChallenge.currentCompletions();
                case ChallengeType.NoAug: return c.noAugsChallenge.currentCompletions();
                case ChallengeType.TwentyFourHour: return c.hour24Challenge.currentCompletions();
                case ChallengeType.OneHundredLC: return c.level100Challenge.currentCompletions();
                case ChallengeType.NoEquip: return c.noEquipmentChallenge.currentCompletions();
                case ChallengeType.Troll: return c.trollChallenge.currentCompletions();
                case ChallengeType.NoRebirth: return c.noRebirthChallenge.currentCompletions();
                case ChallengeType.LaserSword: return c.laserSwordChallenge.currentCompletions();
                case ChallengeType.Blind: return c.blindChallenge.currentCompletions();
                case ChallengeType.NoNGU: return c.NGUChallenge.currentCompletions();
                case ChallengeType.NoTimeMachine: return c.timeMachineChallenge.currentCompletions();
                default: throw new ArgumentOutOfRangeException("type");
            }
        }

        private static int MaximumCompletions(AllChallengesController c, ChallengeType type)
        {
            switch (type)
            {
                case ChallengeType.Basic: return c.basicChallenge.maxCompletions;
                case ChallengeType.NoAug: return c.noAugsChallenge.maxCompletions;
                case ChallengeType.TwentyFourHour: return c.hour24Challenge.maxCompletions;
                case ChallengeType.OneHundredLC: return c.level100Challenge.maxCompletions;
                case ChallengeType.NoEquip: return c.noEquipmentChallenge.maxCompletions;
                case ChallengeType.Troll: return c.trollChallenge.maxCompletions;
                case ChallengeType.NoRebirth: return c.noRebirthChallenge.maxCompletions;
                case ChallengeType.LaserSword: return c.laserSwordChallenge.maxCompletions;
                case ChallengeType.Blind: return c.blindChallenge.maxCompletions;
                case ChallengeType.NoNGU: return c.NGUChallenge.maxCompletions;
                case ChallengeType.NoTimeMachine: return c.timeMachineChallenge.maxCompletions;
                default: throw new ArgumentOutOfRangeException("type");
            }
        }

        internal static bool ChallengeUnlocked(AllChallengesController cc, ChallengeType challenge)
        {
            object controller;
            switch (challenge)
            {
                case ChallengeType.Basic: controller = cc.basicChallenge; break;
                case ChallengeType.NoAug: controller = cc.noAugsChallenge; break;
                case ChallengeType.TwentyFourHour: controller = cc.hour24Challenge; break;
                case ChallengeType.OneHundredLC: controller = cc.level100Challenge; break;
                case ChallengeType.NoEquip: controller = cc.noEquipmentChallenge; break;
                case ChallengeType.Troll: controller = cc.trollChallenge; break;
                case ChallengeType.NoRebirth: controller = cc.noRebirthChallenge; break;
                case ChallengeType.LaserSword: controller = cc.laserSwordChallenge; break;
                case ChallengeType.Blind: controller = cc.blindChallenge; break;
                case ChallengeType.NoNGU: controller = cc.NGUChallenge; break;
                case ChallengeType.NoTimeMachine: controller = cc.timeMachineChallenge; break;
                default: return false;
            }

            var unlocked = controller.GetType().GetMethod("unlocked",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (unlocked == null || unlocked.ReturnType != typeof(bool))
                return false;
            return (bool)unlocked.Invoke(controller, null);
        }

        internal bool DoRebirth()
        {
            if (_challengeIntentRequested)
            {
                RCTarget selected;
                string reason;
                if (!TryValidateSelectedChallengeIntent(out selected, out reason))
                {
                    Main.LogAction("HOLD", "Challenge entry held: " + reason
                                           + "; no runner-up or ordinary reset was attempted");
                    return false;
                }
                if (PreRebirth()) return false;
                // Preparation can change a timing/state-version input. Consume the same one intent
                // again at the native boundary; never recompute a runner-up.
                return TryStartChallenge();
            }

            if (PreRebirth()) return false;
            return EngageRebirth();
        }

        protected bool PreRebirth()
        {
            if ((Main.Settings.ManageYggdrasil || Main.AutopilotWants(x => x.ManageYggdrasil))
                && YggdrasilManager.AnyHarvestable())
            {
                if (Main.AutopilotWants(x => x.ManageYggdrasil)
                    || Main.Settings.SwapYggdrasilLoadouts && Main.Settings.YggdrasilLoadout.Length > 0)
                {
                    if (!LoadoutManager.TryYggdrasilSwap())
                    {
                        Main.Log("Delaying rebirth to wait for ygg loadout/diggers");
                        return true;
                    }
                    if (!DiggerManager.TryYggSwap())
                    {
                        if (LoadoutManager.RestoreGear())
                            LoadoutManager.ReleaseLock();
                        Main.Log("Delaying rebirth; ygg digger lock failed and gear was restored");
                        return true;
                    }

                    YggdrasilManager.HarvestAll();
                    Main.Log("Delaying rebirth 1 loop to allow fruit effects");
                    return true;
                }

                YggdrasilManager.HarvestAll();
                Main.Log("Delaying rebirth 1 loop to allow fruit effects");
                return true;
            }

            DiggerManager.UpgradeCheapestDigger();

            // Autopilot's single-spell policy ran earlier in the same automation
            // transaction with the selected checkpoint in view.  Do not invoke the
            // independent legacy thresholds again at the mutation boundary.
            if (!Main.AutopilotWants(x => x.ManageBloodMagic))
                CastBloodSpells(true);
            return false;
        }

        protected void CastBloodSpells(bool rebirth)
        {
            if (!Main.Settings.CastBloodSpells && !Main.AutopilotWants(x => x.ManageBloodMagic))
                return;

            float iron = 0;
            long mcguffA = 0;
            long mcguffB = 0;
            if (Main.Settings.BloodMacGuffinBThreshold > 0)
            {
                if (CharObj.adventure.itopod.perkLevel[73] >= 1L &&
                    CharObj.settings.rebirthDifficulty >= difficulty.evil)
                {
                    if (CharObj.bloodMagic.macguffin2Time.totalseconds > CharObj.bloodSpells.macguffin2Cooldown)
                    {
                        if (CharObj.bloodMagic.bloodPoints >= CharObj.bloodSpells.minMacguffin2Blood())
                        {
                            var a = CharObj.bloodMagic.bloodPoints / CharObj.bloodSpells.minMacguffin2Blood();
                            mcguffB = (int)(Math.Log(a, 20.0) + 1.0);
                        }

                        if (Main.Settings.BloodMacGuffinBThreshold <= mcguffB)
                        {
                            CharObj.bloodSpells.castMacguffin2Spell();
                            Main.LogPitSpin("Casting Blood MacGuffin β power @ " + mcguffB);
                            return;
                        }
                        else
                        {
                            if (rebirth)
                            {
                                Main.Log("Casting Failed Blood MacGuffin β - Insufficient Power " + mcguffB +
                                         " of " + Main.Settings.BloodMacGuffinBThreshold);
                            }
                        }
                    }
                }
            }

            if (Main.Settings.BloodMacGuffinAThreshold > 0)
            {
                if (CharObj.adventure.itopod.perkLevel[72] >= 1L)
                {
                    if (CharObj.bloodMagic.macguffin1Time.totalseconds > CharObj.bloodSpells.macguffin1Cooldown)
                    {
                        if (CharObj.bloodMagic.bloodPoints > CharObj.bloodSpells.minMacguffin1Blood())
                        {
                            var a = CharObj.bloodMagic.bloodPoints / CharObj.bloodSpells.minMacguffin1Blood();
                            mcguffA = (int)((Math.Log(a, 10.0) + 1.0) *
                                             CharObj.wishesController.totalBloodGuffbonus());
                        }

                        if (Main.Settings.BloodMacGuffinAThreshold <= mcguffA)
                        {
                            CharObj.bloodSpells.castMacguffin1Spell();
                            Main.LogPitSpin("Casting Blood MacGuffin α power @ " + mcguffA);
                            return;
                        }
                        else
                        {
                            if (rebirth)
                            {
                                Main.Log("Casting Failed Blood MacGuffin α - Insufficient Power " + mcguffA +
                                         " of " + Main.Settings.BloodMacGuffinAThreshold);
                            }
                        }
                    }
                }
            }

            if (Main.Settings.IronPillThreshold > 100)
            {
                if (CharObj.bloodMagic.adventureSpellTime.totalseconds >
                    CharObj.bloodSpells.adventureSpellCooldown)
                {
                    if (CharObj.bloodMagic.bloodPoints > CharObj.bloodSpells.minAdventureBlood())
                    {
                        iron = (float)Math.Floor(Math.Pow(CharObj.bloodMagic.bloodPoints, 0.25));
                        if (CharObj.settings.rebirthDifficulty >= difficulty.evil)
                        {
                            iron *= CharObj.adventureController.itopod.ironPillBonus();
                        }
                    }

                    if (Main.Settings.IronPillThreshold <= iron)
                    {
                        CharObj.bloodSpells.castAdventurePowerupSpell();
                        Main.LogPitSpin("Casting Iron Blood Spell power @ " + iron);
                    }
                    else
                    {
                        if (rebirth)
                        {
                            Main.Log("Casting Failed Iron Blood Spell - Insufficient Power " + iron + " of " +
                                     Main.Settings.IronPillThreshold);
                        }
                    }
                }
            }
        }
    }
}
