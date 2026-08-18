using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
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
            if (type == "TIME")
            {
                return new TimeRebirth
                {
                    CharObj = Main.Character,
                    ChallengeTargets = ParseChallenges(challenges),
                    RebirthController = Main.Character.rebirth,
                    RebirthTime = target
                };
            }
            
            if (type == "NUMBER")
            {
                return new NumberRebirth
                {
                    CharObj = Main.Character,
                    ChallengeTargets = ParseChallenges(challenges),
                    RebirthController = Main.Character.rebirth,
                    MultTarget = target
                };
            }

            if (type == "BOSSES")
            {
                return new BossNumRebirth
                {
                    CharObj = Main.Character,
                    ChallengeTargets = ParseChallenges(challenges),
                    RebirthController = Main.Character.rebirth,
                    NumBosses = target
                };
            }

            return new NoRebirth();
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
        protected Rebirth RebirthController;
        internal abstract bool RebirthAvailable();
        protected Character CharObj;
        protected BaseRebirth()
        {
            CharObj = Main.Character;
            RebirthController = CharObj.rebirth;
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

        Reflection only proves that a method was requested, not that NGU Idle accepted the reset. Snapshot
        Number, AP, Blood rebirth power, Boss records, rebirth counter/timer, and every native challenge
        flag/count before and after the exact zero-argument method, then require an immediate native state
        delta. Challenge entry additionally requires the intended exact flag. The single transaction log
        contains both snapshots so a surprising Number loss or wrong challenge can be audited after restart.
        Callers propagate false so allocation cannot report a reset which never happened.
        */
        protected bool EngageChalRebirth(string rbType, ChallengeType expectedChallenge)
        {
            Main.Log($"Rebirthing into {rbType}");
            var method = RebirthController.GetType().GetPrivateMethod(rbType);
            if (method == null)
            {
                Main.LogAction("REJECTED", "Challenge entry method " + rbType
                                           + " was unavailable; no rebirth was attempted");
                return false;
            }
            var before = RebirthAuditSnapshot.Capture(CharObj);
            var rebirthsBefore = CharObj.stats.rebirthNumber;
            var timeBefore = CharObj.rebirthTime.totalseconds;
            var challengeBefore = CharObj.challenges.inChallenge;
            method.Invoke(RebirthController, null);
            var after = RebirthAuditSnapshot.Capture(CharObj);
            var resetConfirmed = CharObj.stats.rebirthNumber > rebirthsBefore
                                 || CharObj.rebirthTime.totalseconds + 1.0 < timeBefore;
            var exactChallenge = SpecificChallengeActive(expectedChallenge);
            var confirmed = resetConfirmed && !challengeBefore && CharObj.challenges.inChallenge
                            && exactChallenge;
            Main.LogAction(confirmed ? "REBIRTH" : "REJECTED", confirmed
                ? "Entered " + expectedChallenge + " through " + rbType
                  + " [confirmed transaction] pre{" + before + "} post{" + after + "}"
                : "Challenge request " + rbType
                  + " produced no verified reset plus exact " + expectedChallenge
                  + " transition; pre{" + before + "} post{" + after + "}");
            return confirmed;
        }

        private bool SpecificChallengeActive(ChallengeType challenge)
        {
            var challenges = CharObj.challenges;
            bool active;
            switch (challenge)
            {
                case ChallengeType.Basic: active = challenges.basicChallenge.inChallenge; break;
                case ChallengeType.NoAug: active = challenges.noAugsChallenge.inChallenge; break;
                case ChallengeType.TwentyFourHour: active = challenges.hour24Challenge.inChallenge; break;
                case ChallengeType.OneHundredLC: active = challenges.levelChallenge10k.inChallenge; break;
                case ChallengeType.NoEquip: active = challenges.noEquipmentChallenge.inChallenge; break;
                case ChallengeType.Troll: active = challenges.trollChallenge.inChallenge; break;
                case ChallengeType.NoRebirth: active = challenges.noRebirthChallenge.inChallenge; break;
                case ChallengeType.LaserSword: active = challenges.laserSwordChallenge.inChallenge; break;
                case ChallengeType.Blind: active = challenges.blindChallenge.inChallenge; break;
                case ChallengeType.NoNGU: active = challenges.nguChallenge.inChallenge; break;
                case ChallengeType.NoTimeMachine: active = challenges.timeMachineChallenge.inChallenge; break;
                default: return false;
            }
            if (!active) return false;
            var activeFlags = 0;
            if (challenges.basicChallenge.inChallenge) activeFlags++;
            if (challenges.noAugsChallenge.inChallenge) activeFlags++;
            if (challenges.hour24Challenge.inChallenge) activeFlags++;
            if (challenges.levelChallenge10k.inChallenge) activeFlags++;
            if (challenges.noEquipmentChallenge.inChallenge) activeFlags++;
            if (challenges.trollChallenge.inChallenge) activeFlags++;
            if (challenges.noRebirthChallenge.inChallenge) activeFlags++;
            if (challenges.laserSwordChallenge.inChallenge) activeFlags++;
            if (challenges.blindChallenge.inChallenge) activeFlags++;
            if (challenges.nguChallenge.inChallenge) activeFlags++;
            if (challenges.timeMachineChallenge.inChallenge) activeFlags++;
            return activeFlags == 1;
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
            Main.Log("Normal Rebirth Engaged");
            var method = RebirthController.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SingleOrDefault(x => x.Name == "engage" && x.GetParameters().Length == 0);
            if (method == null)
            {
                Main.LogAction("REJECTED", "Native normal-rebirth method was unavailable; no reset was attempted");
                return false;
            }
            var before = RebirthAuditSnapshot.Capture(CharObj);
            var rebirthsBefore = CharObj.stats.rebirthNumber;
            var timeBefore = CharObj.rebirthTime.totalseconds;
            method.Invoke(RebirthController, null);
            var after = RebirthAuditSnapshot.Capture(CharObj);
            var confirmed = CharObj.stats.rebirthNumber > rebirthsBefore
                            || CharObj.rebirthTime.totalseconds + 1.0 < timeBefore;
            Main.LogAction(confirmed ? "REBIRTH" : "REJECTED", confirmed
                ? "Completed normal rebirth [confirmed transaction] pre{" + before
                  + "} post{" + after + "}"
                : "Normal rebirth request produced no verified native reset transition; pre{"
                  + before + "} post{" + after + "}");
            return confirmed;
        }

        private sealed class RebirthAuditSnapshot
        {
            private string Value;

            internal static RebirthAuditSnapshot Capture(Character character)
            {
                return new RebirthAuditSnapshot {Value = Build(character)};
            }

            public override string ToString()
            {
                return Value;
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
            if (ChallengeTargets.Length == 0)
                return false;

            var cc = CharObj.allChallenges;
            foreach (var rc in ChallengeTargets)
            {
                if (!ChallengeUnlocked(cc, rc.Challenge))
                    continue;
                var i = rc.Index;
                switch (rc.Challenge)
                {
                    case ChallengeType.Basic:
                        if (i > cc.basicChallenge.maxCompletions || i != cc.basicChallenge.currentCompletions() + 1)
                            continue;
                        return true;
                    case ChallengeType.NoAug:
                        if (i > cc.noAugsChallenge.maxCompletions || i != cc.noAugsChallenge.currentCompletions() + 1)
                            continue;
                        return true;
                    case ChallengeType.TwentyFourHour:
                        if (i > cc.hour24Challenge.maxCompletions || i != cc.hour24Challenge.currentCompletions() + 1)
                            continue;
                        return true;
                    case ChallengeType.OneHundredLC:
                        if (i > cc.level100Challenge.maxCompletions || i != cc.level100Challenge.currentCompletions() + 1)
                            continue;
                        return true;
                    case ChallengeType.NoEquip:
                        if (i > cc.noEquipmentChallenge.maxCompletions || i != cc.noEquipmentChallenge.currentCompletions() + 1)
                            continue;
                        return true;
                    case ChallengeType.Troll:
                        if (i > cc.trollChallenge.maxCompletions || i != cc.trollChallenge.currentCompletions() + 1)
                            continue;
                        return true;
                    case ChallengeType.NoRebirth:
                        if (i > cc.noRebirthChallenge.maxCompletions || i != cc.noRebirthChallenge.currentCompletions() + 1)
                            continue;
                        return true;
                    case ChallengeType.LaserSword:
                        if (i > cc.laserSwordChallenge.maxCompletions || i != cc.laserSwordChallenge.currentCompletions() + 1)
                            continue;
                        return true;
                    case ChallengeType.Blind:
                        if (i > cc.blindChallenge.maxCompletions || i != cc.blindChallenge.currentCompletions() + 1)
                            continue;
                        return true;
                    case ChallengeType.NoNGU:
                        if (i > cc.NGUChallenge.maxCompletions || i != cc.NGUChallenge.currentCompletions() + 1)
                            continue;
                        return true;
                    case ChallengeType.NoTimeMachine:
                        if (i > cc.timeMachineChallenge.maxCompletions || i != cc.timeMachineChallenge.currentCompletions() + 1)
                            continue;
                        return true;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            return false;
        }

        protected bool TryStartChallenge()
        {
            if (ChallengeTargets.Length == 0)
                return false;

            var cc = CharObj.allChallenges;
            foreach (var rc in ChallengeTargets)
            {
                if (!ChallengeUnlocked(cc, rc.Challenge))
                    continue;
                var i = rc.Index;
                switch (rc.Challenge)
                {
                    case ChallengeType.Basic:
                        if (i > cc.basicChallenge.maxCompletions || i != cc.basicChallenge.currentCompletions() + 1)
                            continue;
                        return EngageChalRebirth("engageBasicChallenge", rc.Challenge);
                    case ChallengeType.NoAug:
                        if (i > cc.noAugsChallenge.maxCompletions || i != cc.noAugsChallenge.currentCompletions() + 1)
                            continue;
                        return EngageChalRebirth("engageNoAugsChallenge", rc.Challenge);
                    case ChallengeType.TwentyFourHour:
                        if (i > cc.hour24Challenge.maxCompletions || i != cc.hour24Challenge.currentCompletions() + 1)
                            continue;
                        return EngageChalRebirth("engage24HourChallenge", rc.Challenge);
                    case ChallengeType.OneHundredLC:
                        if (i > cc.level100Challenge.maxCompletions || i != cc.level100Challenge.currentCompletions() + 1)
                            continue;
                        return EngageChalRebirth("engagelevel100Challenge", rc.Challenge);
                    case ChallengeType.NoEquip:
                        if (i > cc.noEquipmentChallenge.maxCompletions || i != cc.noEquipmentChallenge.currentCompletions() + 1)
                            continue;
                        return EngageChalRebirth("engageNoEquipChallenge", rc.Challenge);
                    case ChallengeType.Troll:
                        if (i > cc.trollChallenge.maxCompletions || i != cc.trollChallenge.currentCompletions() + 1)
                            continue;
                        return EngageChalRebirth("engageTrollChallenge", rc.Challenge);
                    case ChallengeType.NoRebirth:
                        if (i > cc.noRebirthChallenge.maxCompletions || i != cc.noRebirthChallenge.currentCompletions() + 1)
                            continue;
                        return EngageChalRebirth("engageNoRebirthChallenge", rc.Challenge);
                    case ChallengeType.LaserSword:
                        if (i > cc.laserSwordChallenge.maxCompletions || i != cc.laserSwordChallenge.currentCompletions() + 1)
                            continue;
                        return EngageChalRebirth("engageLaserSwordChallenge", rc.Challenge);
                    case ChallengeType.Blind:
                        if (i > cc.blindChallenge.maxCompletions || i != cc.blindChallenge.currentCompletions() + 1)
                            continue;
                        return EngageChalRebirth("engageBlindChallenge", rc.Challenge);
                    case ChallengeType.NoNGU:
                        if (i > cc.NGUChallenge.maxCompletions || i != cc.NGUChallenge.currentCompletions() + 1)
                            continue;
                        return EngageChalRebirth("engageNGUChallenge", rc.Challenge);
                    case ChallengeType.NoTimeMachine:
                        if (i > cc.timeMachineChallenge.maxCompletions || i != cc.timeMachineChallenge.currentCompletions() + 1)
                            continue;
                        return EngageChalRebirth("engageTimeMachineChallenge", rc.Challenge);
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            return false;
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
            if (PreRebirth())
                return false;

            if (!CharObj.challenges.inChallenge && AnyChallengesValid())
            {
                // A rejected challenge entry is an uncertain irreversible boundary.
                // Never substitute an ordinary rebirth in the same transaction.
                return TryStartChallenge();
            }

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
