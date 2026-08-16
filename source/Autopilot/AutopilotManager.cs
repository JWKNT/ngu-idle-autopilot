using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using NGUInjector.AllocationProfiles;
using NGUInjector.Managers;
using UnityEngine.UI;

/*
FILE PURPOSE

AutopilotManager is the live policy coordinator: it reloads plans, routes bosses/Adventure,
executes verified purchases and spells, separates persistent from reset-local spending, and emits
decision.json for the read-only monitor. Irreversible actions require full mode plus a confirmed
post-state delta. New mechanics should expose focused managers instead of duplicating authority.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class AutopilotManager
    {
        private readonly string _configPath;
        private readonly string _decisionPath;
        private readonly string _profilePath;
        private readonly string _profilesDir;
        private DateTime _configWriteTime = DateTime.MinValue;
        private string _lastPlanSignature = string.Empty;
        private DateTime _lastDecision = DateTime.MinValue;
        private DateTime _lastAdventureDecision = DateTime.MinValue;
        private ZoneTarget _adventureTarget;
        private AdventureCollectionTarget _collectionTarget;
        private int _loggedAdventureZone = int.MinValue;
        private int _loggedAdventureFightType = int.MinValue;
        private bool _loggedTitanAutoKill;
        private string _adventureRecoveryReason = string.Empty;
        private float _adventureRecoveryTargetHP;
        private int _adventureRecoveryEtaSeconds;
        private DateTime _adventureSafeZoneSince = DateTime.MinValue;
        private DateTime _resourceRateSampleTime = DateTime.MinValue;
        private long _lastExp;
        private long _lastLifetimeAp;
        private double _lastGold;
        private double _expPerSecond;
        private double _apPerSecond;
        private double _goldPerSecond;
        private long _decisionSequence;
        private byte _pendingPuzzleKey;
        private int _pendingPuzzleSequence = -1;
        private DateTime _pendingPuzzleKeyTime = DateTime.MinValue;
        private bool? _lastSynchronized;
        private DateTime _lastSynchronizationReport = DateTime.MinValue;
        private int _lastObservedHighestBoss = -1;
        private int _lastObservedSelectedBoss = -1;
        private string _lastBossTransition = "No boss transition observed in this process yet";

        internal AutopilotConfig Config { get; private set; }
        internal AutopilotPlan Plan { get; private set; }
        internal CustomAllocation Profile { get; private set; }

        internal bool CanExecuteSafe
        {
            get { return Config != null && Config.Enabled && (Config.IsAssist || Config.IsFull); }
        }

        internal bool CanExecuteIrreversible
        {
            get { return Config != null && Config.Enabled && Config.IsFull; }
        }

        internal bool TryTitan7PuzzleStep()
        {
            var c = Main.Character;
            if (_pendingPuzzleKey != 0)
            {
                // A menu/load transition can pause automation between key-down and
                // key-up. Always release the native key first; never leave input
                // logically held merely because gameplay synchronization was lost.
                if (c == null || !CanExecuteIrreversible || !Main.IsAutomationReady)
                {
                    keybd_event(_pendingPuzzleKey, 0, KeyEventKeyUp, UIntPtr.Zero);
                    _pendingPuzzleKey = 0;
                    _pendingPuzzleSequence = -1;
                    Main.LogAction("REJECTED", "Released pending Titan 7 key after automation paused");
                    return false;
                }
                var pendingAdventure = c.adventure;
                if ((DateTime.UtcNow - _pendingPuzzleKeyTime).TotalMilliseconds < 80)
                    return true;
                keybd_event(_pendingPuzzleKey, 0, KeyEventKeyUp, UIntPtr.Zero);
                var before = _pendingPuzzleSequence;
                var key = (char)_pendingPuzzleKey;
                _pendingPuzzleKey = 0;
                _pendingPuzzleSequence = -1;
                var confirmed = pendingAdventure.titan7QuestSequence == before + 1;
                Main.LogAction(confirmed ? "PROGRESSION" : "REJECTED",
                    confirmed
                        ? "Titan 7 FARTS puzzle: sent native " + key + " key input [confirmed sequence "
                          + before + " -> " + pendingAdventure.titan7QuestSequence + "]"
                        : "Titan 7 native " + key + " key input produced no sequence transition");
                return true;
            }
            if (!CanExecuteIrreversible || !Main.IsAutomationReady || c == null)
                return false;
            var a = c.adventure;
            if (!a.titan7questStarted || a.titan7questComplete || a.titan7Unlocked)
                return false;
            var sequence = a.titan7QuestSequence;
            var bosses = new[] {24, 41, 62, 81, 120};
            var letters = new[] {'F', 'A', 'R', 'T', 'S'};
            if (sequence < 0 || sequence >= bosses.Length || c.bossID != bosses[sequence])
                return false;
            if (c.bossController.isFighting || c.bossController.nukeBoss)
                return true;

            c.menuSwapper.swapMenu(15);
            if (c.menuID != 15)
            {
                Main.LogAction("REJECTED", "Titan 7 puzzle menu transition was rejected at Boss " + c.bossID);
                return true;
            }
            var window = Process.GetCurrentProcess().MainWindowHandle;
            if (window == IntPtr.Zero || !SetForegroundWindow(window))
            {
                Main.LogAction("REJECTED", "Titan 7 native key input is API-blocked because the game window could not be focused");
                return true;
            }
            _pendingPuzzleKey = (byte)letters[sequence];
            _pendingPuzzleSequence = sequence;
            _pendingPuzzleKeyTime = DateTime.UtcNow;
            keybd_event(_pendingPuzzleKey, 0, 0, UIntPtr.Zero);
            return true;
        }

        private const uint KeyEventKeyUp = 0x0002;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        internal string Status
        {
            get
            {
                if (Config == null) return "loading";
                if (!Config.Enabled) return "off";
                return Config.Mode + (Plan == null ? string.Empty : " / " + Plan.Stage);
            }
        }

        internal AutopilotManager(string runtimeDir, string profilesDir)
        {
            _profilesDir = profilesDir;
            _configPath = Path.Combine(runtimeDir, "autopilot.json");
            _decisionPath = Path.Combine(runtimeDir, "decision.json");
            _profilePath = Path.Combine(profilesDir, "autopilot.generated.json");
            ReloadConfig(true);
        }

        internal void Tick()
        {
            ReloadConfig(false);
            if (Config == null)
                return;

            if ((DateTime.Now - _lastDecision).TotalSeconds < Math.Max(1, Config.DecisionIntervalSeconds))
                return;

            _lastDecision = DateTime.Now;
            if (!Config.Enabled)
            {
                WriteDisabledDecision();
                return;
            }
            Plan = AutopilotPlanner.Build(Main.Character, Config);
            var signature = Plan.Signature();
            ObserveBossTransitions(Main.Character);

            if (signature != _lastPlanSignature)
            {
                Main.Log("Autopilot plan: " + Plan.Stage + " — " + Plan.Objective);
                _lastPlanSignature = signature;
                if (CanExecuteSafe && Config.ManageAllocations)
                    LoadGeneratedProfile();
            }

            if (!CanExecuteSafe)
                return;

            if (Config.ManageDiggers)
                DiggerManager.RecapDiggers();
            if (Config.ManageBloodMagic)
                ManageBloodSpell();
            if (CanExecuteIrreversible && Config.AllowExpSpending)
            {
                OpenExpBoxes();
                if (!BuyAtomicExpUpgrade() && !BuyEarlyAdventureStatAtom()
                    && !BuyStrategicPermanentExpUpgrade() && !BuyDaycareUnlock()
                    && !BuyBestYggPermanent())
                    BuyBestExpPackage();
            }
            if (CanExecuteIrreversible && Config.AllowApSpending)
                SpendBestApUpgrade();
            if (Config.ManageCards)
                CardCookingManager.ManageCards(Main.Character, Config, CanExecuteIrreversible);
            if (Config.ManageCooking)
                CardCookingManager.ManageCooking(Main.Character, CanExecuteIrreversible);
            if (CanExecuteIrreversible && Config.AllowPerkSpending)
                SpendBestPerk();
            if (CanExecuteIrreversible && Config.AllowQuirkSpending)
                SpendBestQuirk();
        }

        internal void PublishDecisionAfterAutomation(bool transactionComplete, string transactionError)
        {
            /*
            POST-TRANSACTION TELEMETRY BARRIER

            Tick builds policy and may mutate purchases before Main continues through inventory,
            Daycare, quests, allocations, and rebirth. Publishing inside Tick described the state
            before those later actions and made correct automation look stale for a full cycle.
            Main queues this method after the one-second transaction, then its fast allocator calls
            it after the settling sweep. The snapshot is still observational—it never drives
            mutations—and uses the installed plan with the final native state from that cycle.
            */
            if (Config == null) return;
            if (!Config.Enabled)
            {
                WriteDisabledDecision();
                return;
            }
            // Keep the policy object that Tick actually installed. Rebuilding here only
            // for display can produce a different rebirth/allocation target that has not
            // yet been loaded into Profile, making telemetry predictive instead of true.
            // All native state fields below are still sampled after the completed sweep.
            if (Plan == null)
                Plan = AutopilotPlanner.Build(Main.Character, Config);
            WriteDecision(transactionComplete, transactionError);
        }

        private void ObserveBossTransitions(Character c)
        {
            var highest = c.settings.rebirthDifficulty == difficulty.normal ? c.highestBoss
                : c.settings.rebirthDifficulty == difficulty.evil ? c.highestHardBoss
                : c.highestSadisticBoss;
            var selected = c.bossID + 1;
            if (_lastObservedHighestBoss >= 0 && highest > _lastObservedHighestBoss)
            {
                _lastBossTransition = "Record Fight Boss " + _lastObservedHighestBoss + " -> " + highest
                                      + " confirmed by the game's persistent highest-boss field";
                Main.LogAction("BOSS", _lastBossTransition);
            }
            else if (_lastObservedSelectedBoss >= 0 && selected != _lastObservedSelectedBoss)
            {
                _lastBossTransition = "Selected Fight Boss " + _lastObservedSelectedBoss + " -> " + selected
                                      + (selected < _lastObservedSelectedBoss
                                          ? " after rebirth reset"
                                          : " after native controller victory");
                Main.LogAction("BOSS", _lastBossTransition);
            }
            _lastObservedHighestBoss = highest;
            _lastObservedSelectedBoss = selected;
        }

        private void ManageBloodSpell()
        {
            var c = Main.Character;
            if (c.highestBoss < 37 || c.bloodMagic == null || c.bloodSpells == null
                || c.bloodMagic.bloodPoints <= 0)
                return;
            var bloodBefore = c.bloodMagic.bloodPoints;
            var label = string.Empty;

            // Both MacGuffin spells create permanent equipped-item levels and dominate
            // run-local bonuses whenever their native cooldown and threshold are ready.
            if (c.settings.rebirthDifficulty >= difficulty.evil
                && c.adventure.itopod.perkLevel.Count > 73 && c.adventure.itopod.perkLevel[73] >= 1
                && c.bloodMagic.macguffin2Time.totalseconds >= c.bloodMagicController.spells.macguffin2Cooldown
                && bloodBefore >= c.bloodSpells.minMacguffin2Blood())
            {
                c.bloodSpells.castMacguffin2Spell();
                label = "MacGuffin B (all equipped MacGuffins)";
            }
            else if (c.adventure.itopod.perkLevel.Count > 72 && c.adventure.itopod.perkLevel[72] >= 1
                     && c.bloodMagic.macguffin1Time.totalseconds >= c.bloodMagicController.spells.macguffin1Cooldown
                     && bloodBefore >= c.bloodSpells.minMacguffin1Blood())
            {
                c.bloodSpells.castMacguffin1Spell();
                label = "MacGuffin A";
            }
            else
            {
                var remaining = Plan == null ? int.MaxValue
                    : Plan.RebirthSeconds - (int)c.rebirthTime.totalseconds;
                if (remaining <= 5)
                {
                    c.bloodSpells.castRebirthSpell(bloodBefore);
                    label = "Rebirth Number reserve at the selected checkpoint";
                }
                else if (c.bloodMagic.adventureSpellTime.totalseconds >= c.bloodSpells.adventureSpellCooldown
                         && bloodBefore >= c.bloodSpells.minAdventureBlood()
                         && c.settings.rebirthDifficulty == difficulty.normal)
                {
                    c.bloodSpells.castAdventurePowerupSpell();
                    label = "Iron Pill permanent Adventure stats";
                }
                else if (c.settings.pitUnlocked && bloodBefore >= c.bloodSpells.minGoldBlood())
                {
                    c.bloodSpells.castGoldSpell(bloodBefore);
                    label = "Blood Counterfeit gold multiplier";
                }
            }

            if (string.IsNullOrEmpty(label)) return;
            var confirmed = c.bloodMagic.bloodPoints < bloodBefore;
            Main.LogAction(confirmed ? "BLOOD" : "REJECTED", confirmed
                ? "Cast " + label + " using " + (bloodBefore - c.bloodMagic.bloodPoints)
                  + " Blood [confirmed by Blood delta]"
                : label + " cast produced no Blood delta");
        }

        internal bool ControlAdventure(CombatManager combat, QuestManager quests)
        {
            if (!CanExecuteSafe || !Config.ManageAdventure)
                return false;
            if (!Main.Character.settings.autoKillTitans)
            {
                Main.Character.settings.autoKillTitans = true;
                if (!_loggedTitanAutoKill)
                {
                    Main.LogAction("ADVENTURE", "Enabled NGU Idle's native Titan auto-kill controller [confirmed by settings state]");
                    _loggedTitanAutoKill = true;
                }
            }
            var questZone = quests.IsQuesting();
            if (questZone > 0)
                return false;

            if (InventoryManager.ExileAssemblyReady(Main.Character))
            {
                if (Main.Character.adventure.autoattacking)
                    Main.Character.adventureController.idleAttackMove.setToggle();
                combat.MoveToZone(1);
                if (_loggedAdventureZone != 1 || _loggedAdventureFightType != 3)
                {
                    Main.LogAction("PROGRESSION", "Routing to zone 1 for the exact Exile clue-slot assembly");
                    _loggedAdventureZone = 1;
                    _loggedAdventureFightType = 3;
                }
                _adventureTarget = new ZoneTarget {Zone = 1, FightType = 3};
                return true;
            }

            int deathNoteZone;
            string deathNoteTarget;
            if (TryGetDeathNoteTarget(Main.Character, out deathNoteZone, out deathNoteTarget))
            {
                _adventureTarget = new ZoneTarget {Zone = deathNoteZone, FightType = 2};
                if (_loggedAdventureZone != deathNoteZone || _loggedAdventureFightType != 2)
                {
                    Main.LogAction("PROGRESSION", "Titan 8 Death Note target: " + deathNoteTarget
                                                         + " in zone " + deathNoteZone);
                    _loggedAdventureZone = deathNoteZone;
                    _loggedAdventureFightType = 2;
                }
                var consigliere = deathNoteZone == 26;
                combat.ManualZone(deathNoteZone, consigliere, true, consigliere, true, true);
                CaptureRecovery(combat);
                return true;
            }

            var titanZone = ZoneHelpers.HighestAvailableTitan();
            if (titanZone >= 0)
            {
                if (_loggedAdventureZone != titanZone || _loggedAdventureFightType != 2)
                {
                    Main.LogAction("ADVENTURE", "Prioritizing active Titan window in zone " + titanZone);
                    _loggedAdventureZone = titanZone;
                    _loggedAdventureFightType = 2;
                }
                combat.ManualZone(titanZone, true, true, true, true, true);
                CaptureRecovery(combat);
                return true;
            }
            if (_adventureTarget == null || (DateTime.Now - _lastAdventureDecision).TotalSeconds >= 1)
            {
                _lastAdventureDecision = DateTime.Now;
                try
                {
                    var progressionFront = ZoneStatHelper.GetBestZone();
                    _collectionTarget = AdventureCollectionPlanner.Evaluate(Main.Character, progressionFront);
                    _adventureTarget = _collectionTarget.Target ?? progressionFront;
                }
                catch
                {
                    _adventureTarget = null;
                    _collectionTarget = null;
                }
            }

            var best = _adventureTarget;
            if (Main.Character.settings.itopodOn
                && (best == null || _collectionTarget != null && _collectionTarget.IncompleteZones == 0))
            {
                var optimal = Math.Max(0, Math.Min(Main.Character.calculateBestItopodLevel(),
                    Main.Character.adventure.highestItopodLevel - 1));
                var lazyOwnsRange = Main.Character.arbitrary.boughtLazyITOPOD
                                    && Main.Character.arbitrary.lazyITOPODOn;
                if (!lazyOwnsRange && (Main.Character.adventure.zone < 1000
                    || Main.Character.adventureController.itopodLevel != optimal))
                    Main.Character.adventureController.setOptimalFloor();
                if (_loggedAdventureZone != 1000 || _loggedAdventureFightType != 0)
                {
                    Main.LogAction("ADVENTURE", "Farming optimal ITOPOD floor " + optimal
                                                   + " after completing the current ordinary-zone set");
                    _loggedAdventureZone = 1000;
                    _loggedAdventureFightType = 0;
                }
                _adventureTarget = new ZoneTarget {Zone = 1000, FightType = 0};
                combat.IdleZone(1000, false, true, Main.Settings.ITOPODBeastMode);
                CaptureRecovery(combat);
                return true;
            }

            if (best == null)
                return false;

            if (best.Zone != _loggedAdventureZone || best.FightType != _loggedAdventureFightType)
            {
                var collectionDetail = _collectionTarget == null ? string.Empty
                    : "; collection: " + _collectionTarget.Reason + " ("
                      + _collectionTarget.MissingSummary + ")";
                Main.LogAction(_collectionTarget != null && _collectionTarget.IsBackfill ? "COLLECTION" : "ADVENTURE",
                    "Routing to " + (ZoneStatHelper.UserOverrides.ContainsKey(best.Zone)
                        ? ZoneStatHelper.UserOverrides[best.Zone].Name : "zone " + best.Zone)
                    + " using fight type " + best.FightType + collectionDetail);
                _loggedAdventureZone = best.Zone;
                _loggedAdventureFightType = best.FightType;
            }
            var bossOnlyForSet = _collectionTarget != null && _collectionTarget.Target != null
                                 && _collectionTarget.Target.Zone == best.Zone && _collectionTarget.BossOnly;
            if (best.FightType == 2)
                combat.ManualZone(best.Zone, bossOnlyForSet, true, false, true, true);
            else if (best.FightType == 1)
                combat.ManualZone(best.Zone, bossOnlyForSet, true, true, false, true);
            else
                combat.IdleZone(best.Zone, bossOnlyForSet, true);
            CaptureRecovery(combat);
            return true;
        }

        private static bool TryGetDeathNoteTarget(Character c, out int zone, out string target)
        {
            zone = -1;
            target = string.Empty;
            if (c.adventure.titan8Unlocked || ZoneHelpers.GetMaxReachableZone(true) < 26)
                return false;
            if (!c.adventure.titan8questStarted)
            {
                if (!c.adventure.titan7Unlocked) return false;
                zone = 26;
                target = "defeat The Consigliere to obtain the Death Note";
                return true;
            }
            if (!c.adventure.skeletonWhacked) { zone = 2; target = "Skeleton"; return true; }
            if (!c.adventure.icarusWhacked) { zone = 4; target = "Icarus Proudbottom"; return true; }
            if (!c.adventure.kingCircleWhacked) { zone = 9; target = "King Circle"; return true; }
            if (!c.adventure.emptyNameWhacked) { zone = 10; target = "the empty-name enemy"; return true; }
            if (!c.adventure.robBossWhacked) { zone = 15; target = "Rob Boss"; return true; }
            zone = 26;
            target = "defeat The Consigliere again to unlock Titan 8";
            return true;
        }

        private void CaptureRecovery(CombatManager combat)
        {
            _adventureRecoveryReason = combat.RecoveryReason ?? string.Empty;
            if (Main.Character.adventure.zone == -1)
            {
                if (_adventureSafeZoneSince == DateTime.MinValue)
                    _adventureSafeZoneSince = DateTime.UtcNow;
            }
            else
            {
                _adventureSafeZoneSince = DateTime.MinValue;
                _adventureRecoveryReason = string.Empty;
            }
            if (string.IsNullOrEmpty(_adventureRecoveryReason))
            {
                _adventureRecoveryTargetHP = 0;
                _adventureRecoveryEtaSeconds = 0;
            }
            else
            {
                _adventureRecoveryTargetHP = combat.RecoveryTargetHP;
                _adventureRecoveryEtaSeconds = combat.RecoveryEtaSeconds;
            }
        }

        private static bool OrdinaryZoneSetComplete(Character c, int zone)
        {
            return AdventureCollectionPlanner.CoreSetComplete(c, zone);
        }

        internal void ReportSynchronization(bool synchronized, string detail)
        {
            if (_lastSynchronized == synchronized
                && (DateTime.Now - _lastSynchronizationReport).TotalSeconds < 1)
                return;
            _lastSynchronized = synchronized;
            _lastSynchronizationReport = DateTime.Now;
            if (synchronized)
                return;

            var escapedDetail = (detail ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
            var mode = Config == null ? "loading" : Config.Mode;
            var enabled = Config != null && Config.Enabled;
            var json = "{\n"
                       + "  \"schemaVersion\": 2,\n"
                       + "  \"buildId\": \"" + typeof(AutopilotManager).Assembly.ManifestModule.ModuleVersionId + "\",\n"
                       + "  \"producerPid\": " + Process.GetCurrentProcess().Id + ",\n"
                       + "  \"decisionSequence\": " + (++_decisionSequence) + ",\n"
                       + "  \"time\": \"" + DateTime.UtcNow.ToString("o") + "\",\n"
                       + "  \"enabled\": " + enabled.ToString().ToLowerInvariant() + ",\n"
                       + "  \"mode\": \"" + mode + "\",\n"
                       + "  \"synced\": false,\n"
                       + "  \"syncState\": \"main-menu\",\n"
                       + "  \"syncDetail\": \"" + escapedDetail + "\",\n"
                       + "  \"stage\": \"PAUSED / NOT IN ACTIVE GAME\",\n"
                       + "  \"objective\": \"Load a verified save and enter gameplay before automation\",\n"
                       + "  \"rebirthSeconds\": 0,\n"
                       + "  \"rebirthElapsed\": 0\n"
                       + "}\n";
            var tempPath = _decisionPath + ".tmp";
            File.WriteAllText(tempPath, json);
            try
            {
                if (File.Exists(_decisionPath))
                    File.Replace(tempPath, _decisionPath, null);
                else
                    File.Move(tempPath, _decisionPath);
            }
            catch
            {
                if (File.Exists(_decisionPath)) File.Delete(_decisionPath);
                File.Move(tempPath, _decisionPath);
            }
        }

        private void ReloadConfig(bool initial)
        {
            try
            {
                var writeTime = File.Exists(_configPath) ? File.GetLastWriteTimeUtc(_configPath) : DateTime.MinValue;
                if (!initial && writeTime == _configWriteTime)
                    return;
                Config = AutopilotConfig.LoadOrCreate(_configPath);
                _lastPlanSignature = string.Empty;
                _configWriteTime = File.GetLastWriteTimeUtc(_configPath);
                Main.Log("Autopilot configuration loaded: enabled=" + Config.Enabled + ", mode=" + Config.Mode);
            }
            catch (Exception e)
            {
                Main.Log("Autopilot config error: " + e.Message);
                Config = new AutopilotConfig();
            }
        }

        private void LoadGeneratedProfile()
        {
            File.WriteAllText(_profilePath, Plan.ToProfileJson(CanExecuteIrreversible && Config.AllowRebirths,
                CanExecuteIrreversible && Config.AllowChallenges));
            Profile = new CustomAllocation(_profilesDir, "autopilot.generated");
            Profile.ReloadAllocation();
        }

        private void WriteDecision(bool transactionComplete, string transactionError)
        {
            var c = Main.Character;
            UpdateResourceRates(c);
            var expStatus = GetExpStatus(c);
            var apStatus = GetApStatus(c);
            var goldStatus = GetGoldStatus(c);
            CompleteResourceStatus(expStatus, c.realExp, _expPerSecond);
            CompleteResourceStatus(apStatus, c.arbitrary.curArbitraryPoints, _apPerSecond);
            CompleteResourceStatus(goldStatus, c.realGold, _goldPerSecond);
            var augmentStatus = GetAugmentStatus(c);
            var escapedObjective = Plan.Objective.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var escapedStage = Plan.Stage.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string trainingGoal;
            int trainingEtaSeconds;
            GetNextTrainingGoal(c, out trainingGoal, out trainingEtaSeconds);
            var activeHighestBoss = c.settings.rebirthDifficulty == difficulty.normal ? c.highestBoss
                : c.settings.rebirthDifficulty == difficulty.evil ? c.highestHardBoss
                : c.highestSadisticBoss;
            var elapsedSeconds = (int)Math.Floor(c.rebirthTime.totalseconds);
            var bossFitEta = NextBossViabilityEta(c, Plan.RebirthSeconds);
            // Preserve a raw selected-boss estimate even when it does not fit the
            // chosen reset.  The separate fit/slack fields prevent that estimate
            // from being mistaken for an action the current run will actually take.
            var bossViabilityEta = bossFitEta >= 0 ? bossFitEta
                : NextBossViabilityEta(c, elapsedSeconds + 3600);
            var bossSelectedId = c.bossID + 1;
            var bossRecordTargetId = activeHighestBoss + 1;
            var bossTargetMatchesSelected = c.bossID == activeHighestBoss;
            var bossHorizon = Math.Max(0, Plan.RebirthSeconds - elapsedSeconds);
            var activeGoalsJson = ProgressionGoalEngine.ToJson(ProgressionGoalEngine.ActiveGoals(c,
                trainingGoal, trainingEtaSeconds, bossFitEta, Plan.RebirthSeconds, Plan.RebirthReason));
            var escapedTrainingGoal = trainingGoal.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var bossReady = IsNextBossReady(c);
            var bossFighting = c.bossController != null && (c.bossController.isFighting || c.bossController.nukeBoss);
            var bossKillEta = CurrentBossKillEta(c);
            var bossViabilityReason = BossViabilityReason(c, bossReady, bossFighting, bossKillEta);
            var energyIncome = Math.Max(0.0, c.energyPerSecond());
            var energySweepBound = Math.Max(1L, (long)Math.Ceiling(energyIncome * 0.2) + 1L);
            var energyIdleReason = c.idleEnergy <= 0 ? "fully-allocated"
                : c.idleEnergy <= energySweepBound ? "between-allocation-sweeps"
                : "productive-targets-saturated-or-rebirth-horizon-blocked";
            var energyBreakdown = EnergyAllocationBreakdown(c);
            var basicTrainingEnergy = BasicTrainingEnergy(c);
            var nonBasicTrainingEnergy = Math.Max(0L,
                Math.Max(0L, c.curEnergy - c.idleEnergy) - basicTrainingEnergy);
            var projectedAttackMultiplier = c.attackMulti > 0 ? c.nextAttackMulti / c.attackMulti : c.nextAttackMulti;
            var projectedDefenseMultiplier = c.defenseMulti > 0 ? c.nextDefenseMulti / c.defenseMulti : c.nextDefenseMulti;
            var bossCatchupComplete = c.bossID == activeHighestBoss;
            var rebirthPreviewMonotonic = projectedAttackMultiplier > 1.0 && projectedDefenseMultiplier > 1.0;
            var rebirthSafetyBlockReason = !Config.AllowRebirths
                ? "rebirth execution is disabled while the monotonic safety repair is verified"
                : !bossCatchupComplete
                    ? "selected Fight Boss has not caught up to the persistent record"
                    : !rebirthPreviewMonotonic
                        ? "native next-Number preview would lower Attack or Defense multiplier"
                        : string.Empty;
            var projectedRebirthAp = Math.Max(0, (Plan.RebirthSeconds - 3600) / 500);
            var questEta = -1;
            if (c.beastQuest.inQuest && c.beastQuest.targetDrops > c.beastQuest.curDrops)
            {
                var perDrop = c.beastQuestController.expectedTimePerDrop();
                if (c.beastQuest.idleMode) perDrop *= c.beastQuestController.idleDropFactor();
                questEta = perDrop > 0 ? (int)Math.Ceiling((c.beastQuest.targetDrops - c.beastQuest.curDrops) * perDrop) : -1;
            }
            var adventureUnlocked = c.highestBoss >= 4;
            var adventureZone = c.adventure.zone;
            var adventureTargetZone = _adventureTarget == null ? -1 : _adventureTarget.Zone;
            var adventureFightType = _adventureTarget == null ? 0 : _adventureTarget.FightType;
            var adventureTargetName = adventureTargetZone == 1000 ? "ITOPOD"
                : adventureTargetZone >= 0 && ZoneStatHelper.UserOverrides != null
                                      && ZoneStatHelper.UserOverrides.ContainsKey(adventureTargetZone)
                ? ZoneStatHelper.UserOverrides[adventureTargetZone].Name
                : "Not yet selected";
            var adventureBossOnlyForSet = _collectionTarget != null && _collectionTarget.Target != null
                                          && _collectionTarget.Target.Zone == adventureTargetZone
                                          && _collectionTarget.BossOnly;
            var escapedAdventureTargetName = adventureTargetName.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var adventureSafeZoneSeconds = adventureZone == -1 && _adventureSafeZoneSince != DateTime.MinValue
                ? Math.Max(0, (int)Math.Floor((DateTime.UtcNow - _adventureSafeZoneSince).TotalSeconds)) : 0;
            var adventureControlReason = adventureZone != -1 ? "engaged selected Adventure target"
                : !string.IsNullOrEmpty(_adventureRecoveryReason) ? _adventureRecoveryReason
                : adventureTargetZone >= 0 ? "transiting from Safe Zone to " + adventureTargetName
                : "waiting for the Adventure planner to select a target";
            var collectionReason = _collectionTarget == null
                ? "Collection planner is waiting for a fightable Adventure target" : _collectionTarget.Reason;
            var collectionMissing = _collectionTarget == null ? "unknown" : _collectionTarget.MissingSummary;
            var inventoryTotalSlots = AdventureCollectionPlanner.TotalInventorySlots(c);
            var inventoryFreeSlots = AdventureCollectionPlanner.FreeInventorySlots(c);
            var inventoryPressure = AdventureCollectionPlanner.InventoryPressure(c, _collectionTarget);
            var nextTitanName = NextTitanName(c);
            var escapedNextTitanName = nextTitanName.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var json = "{\n"
                       + "  \"schemaVersion\": 2,\n"
                       + "  \"buildId\": \"" + typeof(AutopilotManager).Assembly.ManifestModule.ModuleVersionId + "\",\n"
                       + "  \"producerPid\": " + Process.GetCurrentProcess().Id + ",\n"
                       + "  \"decisionSequence\": " + (++_decisionSequence) + ",\n"
                       + "  \"time\": \"" + DateTime.UtcNow.ToString("o") + "\",\n"
                       + "  \"enabled\": " + Config.Enabled.ToString().ToLowerInvariant() + ",\n"
                       + "  \"mutationsEnabled\": " + CanExecuteSafe.ToString().ToLowerInvariant() + ",\n"
                       + "  \"mode\": \"" + Config.Mode + "\",\n"
                       + "  \"synced\": true,\n"
                       + "  \"syncState\": \"active-gameplay\",\n"
                       + "  \"decisionPhase\": \"post-automation-transaction\",\n"
                       + "  \"automationTransactionComplete\": " + transactionComplete.ToString().ToLowerInvariant() + ",\n"
                       + "  \"automationTransactionError\": \"" + EscapeJson(transactionError ?? string.Empty) + "\",\n"
                       + "  \"stage\": \"" + escapedStage + "\",\n"
                       + "  \"objective\": \"" + escapedObjective + "\",\n"
                       + "  \"rebirthSeconds\": " + Plan.RebirthSeconds + ",\n"
                       + "  \"rebirthReason\": \"" + EscapeJson(Plan.RebirthReason) + "\",\n"
                       + "  \"rebirthRunnerUpSeconds\": " + Plan.RebirthRunnerUpSeconds + ",\n"
                       + "  \"rebirthRunnerUpDeltaSeconds\": " + Plan.RebirthRunnerUpDeltaSeconds + ",\n"
                       + "  \"rebirthRunnerUpReason\": \"" + EscapeJson(Plan.RebirthRunnerUpReason) + "\",\n"
                       + "  \"rebirthOptimizerModel\": \"exact-time-multiplier-event-rate-v1\",\n"
                       + "  \"rebirthObjective\": \"maximize compounded log Attack/Defense multiplier growth per wall-clock hour; cap compression breaks near ties\",\n"
                       + "  \"rebirthSelectedScorePerHour\": " + Plan.RebirthSelectedScorePerHour.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthRunnerUpScorePerHour\": " + Plan.RebirthRunnerUpScorePerHour.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthOptimizerProjectedMultiplier\": " + Plan.RebirthProjectedMultiplier.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthOptimizerProjectedAp\": " + Plan.RebirthProjectedAP + ",\n"
                       + "  \"rebirthCandidateSummary\": \"" + EscapeJson(Plan.RebirthCandidateSummary) + "\",\n"
                       + "  \"rebirthCandidateCount\": " + Plan.RebirthCandidateCount + ",\n"
                       + "  \"rebirthSearchResolutionSeconds\": 1,\n"
                       + "  \"rebirthHysteresisPercent\": 0.05,\n"
                       + "  \"rebirthElapsed\": " + Math.Floor(c.rebirthTime.totalseconds) + ",\n"
                       + "  \"rebirthProjectedAttackMultiplier\": " + projectedAttackMultiplier.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthProjectedDefenseMultiplier\": " + projectedDefenseMultiplier.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthCurrentAttackMultiplier\": " + c.attackMulti.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthNextAttackMultiplierPreview\": " + c.nextAttackMulti.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthCurrentDefenseMultiplier\": " + c.defenseMulti.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthNextDefenseMultiplierPreview\": " + c.nextDefenseMulti.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthPreviewMonotonic\": " + rebirthPreviewMonotonic.ToString().ToLowerInvariant() + ",\n"
                       + "  \"rebirthBossCatchupComplete\": " + bossCatchupComplete.ToString().ToLowerInvariant() + ",\n"
                       + "  \"rebirthExecutionEnabled\": " + Config.AllowRebirths.ToString().ToLowerInvariant() + ",\n"
                       + "  \"rebirthSafetyBlockReason\": \"" + EscapeJson(rebirthSafetyBlockReason) + "\",\n"
                       + "  \"rebirthProjectedAp\": " + projectedRebirthAp + ",\n"
                       + "  \"highestBoss\": " + activeHighestBoss + ",\n"
                       + "  \"normalHighestBoss\": " + c.highestBoss + ",\n"
                       + "  \"difficulty\": " + (int)c.settings.rebirthDifficulty + ",\n"
                       + "  \"nextTitanName\": \"" + escapedNextTitanName + "\",\n"
                       + "  \"nguUnlocked\": " + (c.inventory.itemList.numberComplete || c.settings.nguOn).ToString().ToLowerInvariant() + ",\n"
                       + "  \"hacksUnlocked\": " + c.hacks.hacksOn.ToString().ToLowerInvariant() + ",\n"
                       + "  \"wishesUnlocked\": " + c.wishes.wishesOn.ToString().ToLowerInvariant() + ",\n"
                       + "  \"cardsUnlocked\": " + c.cards.cardsOn.ToString().ToLowerInvariant() + ",\n"
                       + "  \"questUnlocked\": " + c.beastQuest.questsUnlocked.ToString().ToLowerInvariant() + ",\n"
                       + "  \"questInProgress\": " + c.beastQuest.inQuest.ToString().ToLowerInvariant() + ",\n"
                       + "  \"questId\": " + c.beastQuest.questID + ",\n"
                       + "  \"questCurrentDrops\": " + c.beastQuest.curDrops + ",\n"
                       + "  \"questTargetDrops\": " + c.beastQuest.targetDrops + ",\n"
                       + "  \"questIdle\": " + c.beastQuest.idleMode.ToString().ToLowerInvariant() + ",\n"
                       + "  \"questMinor\": " + c.beastQuest.reducedRewards.ToString().ToLowerInvariant() + ",\n"
                       + "  \"questAllActive\": " + c.beastQuest.allActive.ToString().ToLowerInvariant() + ",\n"
                       + "  \"questEtaSeconds\": " + questEta + ",\n"
                       + "  \"questBanked\": " + c.beastQuest.curBankedQuests + ",\n"
                       + "  \"questBankCap\": " + c.beastQuestController.maxBankedQuests() + ",\n"
                       + "  \"questButter\": " + c.arbitrary.beastButterCount + ",\n"
                       + "  \"questQpPreview\": " + (c.beastQuest.inQuest ? c.beastQuestController.currentQuestQPValue() : 0) + ",\n"
                       + "  \"nextBoss\": " + (activeHighestBoss + 1) + ",\n"
                       + "  \"bossSelectedId\": " + bossSelectedId + ",\n"
                       + "  \"bossRecordTargetId\": " + bossRecordTargetId + ",\n"
                       + "  \"bossTargetMatchesSelected\": " + bossTargetMatchesSelected.ToString().ToLowerInvariant() + ",\n"
                       + "  \"lastBossTransition\": \"" + EscapeJson(_lastBossTransition) + "\",\n"
                       + "  \"bossReady\": " + bossReady.ToString().ToLowerInvariant() + ",\n"
                       + "  \"bossFighting\": " + bossFighting.ToString().ToLowerInvariant() + ",\n"
                       + "  \"bossKillEtaSeconds\": " + bossKillEta + ",\n"
                       + "  \"bossViabilityEtaSeconds\": " + bossViabilityEta + ",\n"
                       + "  \"bossDefeatEtaSeconds\": " + bossViabilityEta + ",\n"
                       + "  \"bossRebirthHorizonSeconds\": " + bossHorizon + ",\n"
                       + "  \"bossDefeatFitsRebirthHorizon\": " + (bossFitEta >= 0).ToString().ToLowerInvariant() + ",\n"
                       + "  \"bossRebirthSlackSeconds\": " + (bossViabilityEta < 0 ? -1 : bossHorizon - bossViabilityEta) + ",\n"
                       + "  \"bossEtaModelVersion\": \"discrete-training-augment-event-and-fixed-fight-v3\",\n"
                       + "  \"bossEtaConfidence\": \"projected-current-allocation\",\n"
                       + "  \"bossEtaIncludedEvents\": \"discrete Basic Training, first pending completion on each allocated Augment/Upgrade track, boss/player regeneration, current physical gear\",\n"
                       + "  \"bossEtaExcludedEvents\": \"future allocation changes, chained Augment levels after the first pending completion, future drops/purchases\",\n"
                       + "  \"bossViabilityReason\": \"" + EscapeJson(bossViabilityReason) + "\",\n"
                       + "  \"trainingGoal\": \"" + escapedTrainingGoal + "\",\n"
                       + "  \"trainingEtaSeconds\": " + trainingEtaSeconds + ",\n"
                       + "  \"adventureUnlocked\": " + adventureUnlocked.ToString().ToLowerInvariant() + ",\n"
                       + "  \"adventureZone\": " + adventureZone + ",\n"
                       + "  \"adventureTargetZone\": " + adventureTargetZone + ",\n"
                       + "  \"adventureTargetName\": \"" + escapedAdventureTargetName + "\",\n"
                       + "  \"adventureFightType\": " + adventureFightType + ",\n"
                       + "  \"adventureBossOnlyForSet\": " + adventureBossOnlyForSet.ToString().ToLowerInvariant() + ",\n"
                       + "  \"collectionTargetZone\": " + (_collectionTarget == null || _collectionTarget.Target == null ? -1 : _collectionTarget.Target.Zone) + ",\n"
                       + "  \"collectionIsBackfill\": " + (_collectionTarget != null && _collectionTarget.IsBackfill).ToString().ToLowerInvariant() + ",\n"
                       + "  \"collectionRemainingItems\": " + (_collectionTarget == null ? 0 : _collectionTarget.RemainingItems) + ",\n"
                       + "  \"collectionIncompleteZones\": " + (_collectionTarget == null ? 0 : _collectionTarget.IncompleteZones) + ",\n"
                       + "  \"collectionReason\": \"" + EscapeJson(collectionReason) + "\",\n"
                       + "  \"collectionMissingSummary\": \"" + EscapeJson(collectionMissing) + "\",\n"
                       + "  \"inventoryTotalSlots\": " + inventoryTotalSlots + ",\n"
                       + "  \"inventoryUsedSlots\": " + Math.Max(0, inventoryTotalSlots - inventoryFreeSlots) + ",\n"
                       + "  \"inventoryFreeSlots\": " + inventoryFreeSlots + ",\n"
                       + "  \"inventoryPressure\": \"" + inventoryPressure + "\",\n"
                       + "  \"adventureHP\": " + c.adventure.curHP.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"adventureMaxHP\": " + c.totalAdvHP().ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"adventureRecoveryReason\": \"" + EscapeJson(_adventureRecoveryReason) + "\",\n"
                       + "  \"adventureRecoveryTargetHP\": " + _adventureRecoveryTargetHP.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"adventureRecoveryEtaSeconds\": " + _adventureRecoveryEtaSeconds + ",\n"
                       + "  \"adventureControlReason\": \"" + EscapeJson(adventureControlReason) + "\",\n"
                       + "  \"adventureSafeZoneSeconds\": " + adventureSafeZoneSeconds + ",\n"
                       + "  \"adventurePower\": " + c.totalAdvAttack().ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"adventureToughness\": " + c.totalAdvDefense().ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"energyCurrent\": " + c.curEnergy + ",\n"
                       + "  \"energyIdle\": " + c.idleEnergy + ",\n"
                       + "  \"energyAllocated\": " + Math.Max(0L, c.curEnergy - c.idleEnergy) + ",\n"
                       + "  \"energyIncomePerSecond\": " + energyIncome.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"energySweepBound\": " + energySweepBound + ",\n"
                       + "  \"energyIdleReason\": \"" + energyIdleReason + "\",\n"
                       + "  \"basicTrainingLongHorizonPolicy\": \"reserve Energy first for reachable maximum cap-reduction frontiers with at most a two-future-run Energy-cap payback; then optimize immediate boss marginal value\",\n"
                       + "  \"timeMachineHorizonDecision\": \"" + EscapeJson(AllocationProfiles.BreakpointTypes.TimeMachineBP.LastHorizonDecision) + "\",\n"
                       + "  \"energyAllocationBreakdown\": " + energyBreakdown + ",\n"
                       + "  \"energyBasicTrainingAllocated\": " + basicTrainingEnergy + ",\n"
                       + "  \"energyNonBasicTrainingAllocated\": " + nonBasicTrainingEnergy + ",\n"
                       + "  \"loadoutDecision\": \"" + EscapeJson(ProgressionLoadoutOptimizer.LastDecision) + "\",\n"
                       + "  \"loadoutObjective\": \"" + EscapeJson(ProgressionLoadoutOptimizer.LastObjective) + "\",\n"
                       + "  \"loadoutSearchExact\": " + ProgressionLoadoutOptimizer.LastSearchExact.ToString().ToLowerInvariant() + ",\n"
                       + "  \"loadoutScoreGain\": " + ProgressionLoadoutOptimizer.LastScoreGain.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"trashDecision\": \"" + EscapeJson(InventoryManager.LastTrashDecision) + "\",\n"
                       + "  \"filterDecision\": \"" + EscapeJson(InventoryManager.LastFilterDecision) + "\",\n"
                       + "  \"yggSeedDecision\": \"" + EscapeJson(YggdrasilManager.LastSeedDecision) + "\",\n"
                       + "  \"yggFruitDecision\": \"" + EscapeJson(YggdrasilManager.LastFruitDecision) + "\",\n"
                       + "  \"energyUtilization\": "
                       + (c.curEnergy <= 0 ? 1.0 : (double)(c.curEnergy - c.idleEnergy) / c.curEnergy)
                           .ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"exp\": " + c.realExp + ",\n"
                       + "  \"expDecision\": \"" + EscapeJson(expStatus.Decision) + "\",\n"
                       + "  \"expState\": \"" + expStatus.State + "\",\n"
                       + "  \"expTarget\": " + expStatus.Target + ",\n"
                       + "  \"expTargetCost\": " + expStatus.TargetCost + ",\n"
                       + "  \"expShortfall\": " + expStatus.Shortfall + ",\n"
                       + "  \"expIncomePerSecond\": " + expStatus.IncomePerSecond.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"expEtaSeconds\": " + expStatus.EtaSeconds + ",\n"
                       + "  \"ap\": " + c.arbitrary.curArbitraryPoints + ",\n"
                       + "  \"apDecision\": \"" + EscapeJson(apStatus.Decision) + "\",\n"
                       + "  \"apState\": \"" + apStatus.State + "\",\n"
                       + "  \"apTarget\": " + apStatus.Target + ",\n"
                       + "  \"apTargetCost\": " + apStatus.TargetCost + ",\n"
                       + "  \"apShortfall\": " + apStatus.Shortfall + ",\n"
                       + "  \"apIncomePerSecond\": " + apStatus.IncomePerSecond.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"apEtaSeconds\": " + apStatus.EtaSeconds + ",\n"
                       + "  \"gold\": " + c.realGold.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"goldDecision\": \"" + EscapeJson(goldStatus.Decision) + "\",\n"
                       + "  \"goldState\": \"" + goldStatus.State + "\",\n"
                       + "  \"goldTarget\": " + goldStatus.Target.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"goldTargetCost\": " + goldStatus.TargetCost.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"goldShortfall\": " + goldStatus.Shortfall.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"goldIncomePerSecond\": " + goldStatus.IncomePerSecond.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"goldEtaSeconds\": " + goldStatus.EtaSeconds + "\n"
                       + ",  \"augmentDecision\": \"" + EscapeJson(augmentStatus.Decision) + "\",\n"
                       + "  \"augmentEnergy\": " + augmentStatus.Allocated + ",\n"
                       + "  \"augmentProgress\": " + augmentStatus.Progress.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"augmentEtaSeconds\": " + augmentStatus.EtaSeconds + "\n"
                       + ",  \"goalNodes\": " + activeGoalsJson + "\n"
                       + "}\n";
            WriteAtomic(_decisionPath, json);
        }

        private void WriteDisabledDecision()
        {
            var json = "{\n"
                       + "  \"schemaVersion\": 2,\n"
                       + "  \"buildId\": \"" + typeof(AutopilotManager).Assembly.ManifestModule.ModuleVersionId + "\",\n"
                       + "  \"producerPid\": " + Process.GetCurrentProcess().Id + ",\n"
                       + "  \"decisionSequence\": " + (++_decisionSequence) + ",\n"
                       + "  \"time\": \"" + DateTime.UtcNow.ToString("o") + "\",\n"
                       + "  \"enabled\": false,\n"
                       + "  \"mutationsEnabled\": false,\n"
                       + "  \"mode\": \"" + EscapeJson(Config.Mode) + "\",\n"
                       + "  \"synced\": true,\n"
                       + "  \"stage\": \"AUTOMATION DISABLED\",\n"
                       + "  \"objective\": \"No bot mutations will execute until automation is enabled\",\n"
                       + "  \"rebirthSeconds\": 0,\n"
                       + "  \"rebirthElapsed\": 0\n"
                       + "}\n";
            WriteAtomic(_decisionPath, json);
        }

        private static void WriteAtomic(string path, string contents)
        {
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, contents);
            try
            {
                if (File.Exists(path))
                    File.Replace(tempPath, path, null);
                else
                    File.Move(tempPath, path);
            }
            catch
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(tempPath, path);
            }
        }

        private sealed class ResourceStatus
        {
            internal string State = "saving";
            internal string Decision = string.Empty;
            internal double Target;
            internal double TargetCost;
            internal double Shortfall;
            internal double IncomePerSecond;
            internal int EtaSeconds = -1;
        }

        private sealed class AugmentStatus
        {
            internal string Decision = string.Empty;
            internal long Allocated;
            internal float Progress;
            internal int EtaSeconds = -1;
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", " ").Replace("\n", " ");
        }

        private void UpdateResourceRates(Character c)
        {
            var now = DateTime.UtcNow;
            if (_resourceRateSampleTime != DateTime.MinValue)
            {
                var seconds = (now - _resourceRateSampleTime).TotalSeconds;
                if (seconds > .1 && seconds < 30)
                {
                    UpdatePositiveRate(ref _expPerSecond, (c.realExp - _lastExp) / seconds);
                    // Lifetime AP is monotonic across purchases, unlike the spendable
                    // balance, so an AP buy cannot erase measured AP income.
                    UpdatePositiveRate(ref _apPerSecond,
                        (c.arbitrary.curLifetimePoints - _lastLifetimeAp) / seconds);
                    UpdatePositiveRate(ref _goldPerSecond, (c.realGold - _lastGold) / seconds);
                }
            }
            _resourceRateSampleTime = now;
            _lastExp = c.realExp;
            _lastLifetimeAp = c.arbitrary.curLifetimePoints;
            _lastGold = c.realGold;
        }

        private static void UpdatePositiveRate(ref double smoothed, double observed)
        {
            // Purchases create negative deltas; they are not negative income. A slow
            // decay prevents one old drop from claiming an unrealistically short ETA.
            if (observed > 0)
                smoothed = smoothed <= 0 ? observed : smoothed * .85 + observed * .15;
            else
                smoothed *= .985;
            if (smoothed < 1e-9) smoothed = 0;
        }

        private static int ResourceEta(double current, double target, double perSecond)
        {
            if (target <= current) return 0;
            if (perSecond <= 0) return -1;
            return (int)Math.Min(int.MaxValue, Math.Ceiling((target - current) / perSecond));
        }

        private static void CompleteResourceStatus(ResourceStatus status, double balance, double incomePerSecond)
        {
            status.TargetCost = status.Target;
            status.Shortfall = Math.Max(0, status.Target - balance);
            status.IncomePerSecond = Math.Max(0, incomePerSecond);
            // A reserve is itself a funding target. Never publish ETA 0 while a
            // positive shortfall remains merely because no purchase is allowed yet.
            if (status.Target > balance)
                status.EtaSeconds = ResourceEta(balance, status.Target, status.IncomePerSecond);
            if (!string.IsNullOrEmpty(status.State) && status.State != "saving")
                return;
            if (status.Decision.StartsWith("Buying", StringComparison.Ordinal))
                status.State = "spend-now";
            else if (status.Decision.IndexOf("feature-lock", StringComparison.OrdinalIgnoreCase) >= 0
                     || status.Decision.IndexOf("unlock", StringComparison.OrdinalIgnoreCase) >= 0 && status.Target <= 0)
                status.State = "feature-locked";
            else if (status.Decision.IndexOf("controller", StringComparison.OrdinalIgnoreCase) >= 0
                     || status.Decision.IndexOf("validation", StringComparison.OrdinalIgnoreCase) >= 0)
                status.State = "api-blocked";
            else if (balance <= 0 || status.Shortfall > 0)
                status.State = "below-atomic-cost";
            else if (status.Target > 0)
                status.State = "saving";
            else
                status.State = "working-capital";
        }

        private ResourceStatus GetExpStatus(Character c)
        {
            if (c.realExp <= Config.ExpReserve)
                return new ResourceStatus {Decision = "No spendable EXP above the configured reserve", Target = Config.ExpReserve, EtaSeconds = 0};
            if (c.energySpeed < 49.91f)
            {
                var speedCost = !c.settings.special1Bought ? 1
                    : !c.settings.special2Bought ? 2
                    : !c.settings.special3Bought ? 3
                    : c.energyPurchases.energySpeed10Cost();
                return new ResourceStatus
                {
                    Decision = speedCost <= c.realExp - Config.ExpReserve
                        ? "Buying the highest-return unowned Energy-speed step toward the effective 50 cap"
                        : "Saving EXP for the next highest-return Energy-speed step toward 50",
                    Target = speedCost + Config.ExpReserve,
                    EtaSeconds = ResourceEta(c.realExp, speedCost + Config.ExpReserve, _expPerSecond)
                };
            }
            if (c.highestBoss < 4)
                return new ResourceStatus
                {
                    State = "feature-locked",
                    Decision = "Adventure-stat atoms are feature-locked until Fight Boss 4; retaining EXP",
                    Target = 0,
                    EtaSeconds = -1
                };
            var permanent = GetStrategicPermanentExpTarget(c);
            if (c.highestBoss < 17)
            {
                var earlyAtom = EarlyAdventureAtomIndex(c);
                if (earlyAtom < 0 && permanent != null)
                    return new ResourceStatus
                    {
                        Decision = permanent.Cost <= c.realExp - Config.ExpReserve
                            ? "Buying " + permanent.Label + " on this decision cycle: " + permanent.Reason
                            : "Saving EXP for " + permanent.Label + ": " + permanent.Reason,
                        Target = permanent.Cost + Config.ExpReserve,
                        EtaSeconds = ResourceEta(c.realExp, permanent.Cost + Config.ExpReserve, _expPerSecond)
                    };
                return new ResourceStatus
                {
                    State = earlyAtom < 0 ? "saving" : string.Empty,
                    Decision = earlyAtom < 0
                        ? "Saving EXP for permanent Energy packages at Boss 17; an Adventure-stat atom does not immediately open a new zone"
                        : c.realExp - Config.ExpReserve >= 3
                            ? "Buying one Adventure " + (earlyAtom == 0 ? "Power" : "Toughness")
                              + " atom because it immediately crosses the next-zone threshold"
                            : "Saving for the exact 3-EXP Adventure atom that immediately opens the next zone",
                    Target = earlyAtom < 0 ? 0 : 3 + Config.ExpReserve,
                    EtaSeconds = earlyAtom < 0 ? -1
                        : ResourceEta(c.realExp, 3 + Config.ExpReserve, _expPerSecond)
                };
            }
            if (permanent != null)
                return new ResourceStatus
                {
                    Decision = permanent.Cost <= c.realExp - Config.ExpReserve
                        ? "Buying " + permanent.Label + " on this decision cycle: " + permanent.Reason
                        : "Saving EXP for " + permanent.Label + ": " + permanent.Reason,
                    Target = permanent.Cost + Config.ExpReserve,
                    EtaSeconds = ResourceEta(c.realExp, permanent.Cost + Config.ExpReserve, _expPerSecond)
                };
            if (!c.purchases.hasDaycare && 250 <= Math.Max(1.0, c.stats.totalExp) * .10)
                return new ResourceStatus
                {
                    Decision = c.realExp >= 250 ? "Buying Item Daycare on this decision cycle" : "Saving EXP for Item Daycare",
                    Target = 250,
                    EtaSeconds = ResourceEta(c.realExp, 250, _expPerSecond)
                };
            if (c.highestBoss < 17)
                return new ResourceStatus {Decision = "Held for the Boss 17 custom power/cap/bars unlock", Target = 0, EtaSeconds = -1};

            var candidates = BuildExpCandidates(c);
            var preferred = candidates.OrderBy(x => x.Score).FirstOrDefault();
            if (preferred == null)
                return new ResourceStatus {Decision = "Held because no unlocked EXP purchase passed game-state validation", Target = 0, EtaSeconds = -1};
            return new ResourceStatus
            {
                Decision = preferred.Cost <= c.realExp - Config.ExpReserve
                    ? "Buying the marginally best " + preferred.Name + " power/cap/bars package"
                    : "Saving EXP for the marginally best " + preferred.Name + " power/cap/bars package",
                Target = preferred.Cost + Config.ExpReserve,
                EtaSeconds = ResourceEta(c.realExp, preferred.Cost + Config.ExpReserve, _expPerSecond)
            };
        }

        private ResourceStatus GetApStatus(Character c)
        {
            if (c.arbitrary.curArbitraryPoints <= Config.ApReserve)
                return new ResourceStatus {Decision = "No spendable AP above the configured reserve", Target = Config.ApReserve, EtaSeconds = 0};
            var controller = GetArbitraryController(c);
            if (controller == null)
                return new ResourceStatus {Decision = "Held because the game's AP purchase controller is not available", Target = 0, EtaSeconds = -1};
            var id = !c.arbitrary.instaTrain ? 9
                : !c.arbitrary.hasStarterPack ? 16
                // The bot already performs filtering and merging.  The Heart is the
                // first post-starter AP purchase that creates new progression income
                // (+20% AP once MAXXED), whereas Loot Filter merely duplicates us.
                : !HasYellowHeartDropped(c) ? 14
                : !IsApOwned(c, 15) && AdventureCollectionPlanner.InventoryPressureHigh(c, _collectionTarget) ? 15
                : NextAvailableApPurchase(controller);
            if (id < 0 || !ApPurchaseMethods.ContainsKey(id))
                return new ResourceStatus {Decision = "Held because every supported permanent AP upgrade is already owned or locked", Target = 0, EtaSeconds = -1};
            if (id == 14 && !CanReceiveYellowHeart(c))
                return new ResourceStatus {State = "api-blocked",
                    Decision = "Yellow Heart is the current AP target, but the game requires a free, non-filtered accessory slot before purchase",
                    Target = GetApCost(controller, id), EtaSeconds = -1};
            var cost = GetApCost(controller, id);
            var label = ApPurchaseMethods[id].Substring(3).Replace("AP", string.Empty);
            if (cost <= 0)
                return new ResourceStatus {Decision = "Held because " + label + " is not currently purchasable", Target = 0, EtaSeconds = -1};
            return new ResourceStatus
            {
                Decision = cost <= c.arbitrary.curArbitraryPoints - Config.ApReserve
                    ? "Buying " + ApLongHorizonReason(id, label) + " on this decision cycle"
                    : "Saving AP for " + ApLongHorizonReason(id, label),
                Target = cost + Config.ApReserve,
                EtaSeconds = ResourceEta(c.arbitrary.curArbitraryPoints, cost + Config.ApReserve, _apPerSecond)
            };
        }

        private static string ApLongHorizonReason(int id, string label)
        {
            if (id == 9)
                return label + " (permanently removes repeated Basic Training ramp time)";
            if (id == 16)
                return label + " (the next permanent multi-run progression bundle; cheaper purchases would delay it)";
            if (id == 14)
                return label + " (permanent +20% AP after MAXX; nominal AP-cost breakeven is 750,000 future AP after MAXX)";
            if (id == 15)
                return label + " (collection reserve is below projected merge/drop pressure; a full inventory destroys future drops)";
            return label + " (highest-ranked unlocked permanent upgrade after opportunity cost)";
        }

        private ResourceStatus GetGoldStatus(Character c)
        {
            var augmentReserve = RequiredAugmentWorkingCapital(c);
            if (c.highestBoss < 30)
                return new ResourceStatus
                {
                    State = augmentReserve > 0 ? "working-capital" : "no-profitable-sink",
                    Decision = augmentReserve > 0 && c.realGold >= augmentReserve
                        ? "Gold is funding the next charged Augment level; Money Pit/Time Machine are feature-locked until Boss 30"
                        : augmentReserve <= 0
                            ? "No Augment can complete before the selected rebirth; Money Pit/Time Machine are feature-locked, so this gold has no profitable pre-reset sink"
                            : "Saving gold so the active Augment can start its next paid level without stalling",
                    Target = augmentReserve,
                    EtaSeconds = ResourceEta(c.realGold, augmentReserve, _goldPerSecond)
                };
            var pitReady = c.settings.pitUnlocked
                           && c.pit.pitTime.totalseconds >= c.pitController.currentPitTime()
                           && c.pitController.canToss();
            var pitReserve = Math.Max(100000.0, Config.MoneyPitReserve);
            double permanentPitTarget = 0;
            string permanentPitLabel = string.Empty;
            var hasPermanentPitTarget = pitReady
                                        && MoneyPitManager.TryGetPermanentTierTarget(
                                            out permanentPitTarget, out permanentPitLabel);
            var remaining = Math.Max(0.0, Plan.RebirthSeconds - c.rebirthTime.totalseconds);
            var permanentTierReachable = hasPermanentPitTarget && _goldPerSecond > 0
                                         && Math.Max(0.0, permanentPitTarget - c.realGold) / _goldPerSecond <= remaining;
            if (permanentTierReachable)
                pitReserve = Math.Max(pitReserve, permanentPitTarget);
            var reserve = pitReady ? Math.Max(pitReserve, augmentReserve) : augmentReserve;
            return new ResourceStatus
            {
                Decision = pitReady
                    ? (c.realGold < reserve
                        ? permanentTierReachable && c.realGold < permanentPitTarget
                            ? "Saving gold for " + permanentPitLabel
                              + "; a smaller toss would delay this permanent cumulative Pit breakpoint"
                            : "Saving gold for the ready Money Pit toss while protecting the next Augment charge"
                        : "Money Pit is ready and funded; toss will execute on the next 0.2-second control tick")
                    : (augmentReserve > 0
                        ? "Money Pit is cooling down; reserving only the next active Augment charge and releasing surplus to Time Machine/diggers"
                        : "Money Pit is cooling down; gold is available only to Time Machine/Blood actions that complete before rebirth or permanent Digger upgrades"),
                Target = reserve,
                EtaSeconds = ResourceEta(c.realGold, reserve, _goldPerSecond)
            };
        }

        internal static double RequiredAugmentWorkingCapital(Character c)
        {
            if (c.augments == null || c.augmentsController == null)
                return 0;
            var reserve = 0.0;
            for (var i = 0; i < c.augments.augs.Length && i < c.augmentsController.augments.Length; i++)
            {
                var state = c.augments.augs[i];
                var controller = c.augmentsController.augments[i];
                // Gold is charged on the first advancing tick. Non-zero progress
                // proves the current level has already been paid for.
                if (state.augEnergy > 0 && state.augProgress <= 0)
                    reserve += controller.getAugCost();
                if (state.upgradeEnergy > 0 && state.upgradeProgress <= 0)
                    reserve += controller.getUpgradeCost();
            }
            return reserve;
        }

        private static AugmentStatus GetAugmentStatus(Character c)
        {
            if (c.augments == null || c.augmentsController == null)
                return new AugmentStatus {Decision = "Augment controllers are not available"};
            if (c.highestBoss < 13)
                return new AugmentStatus {Decision = "The first Augment is feature-locked until Boss 13"};
            for (var i = 0; i < c.augments.augs.Length && i < c.augmentsController.augments.Length; i++)
            {
                var state = c.augments.augs[i];
                var controller = c.augmentsController.augments[i];
                var label = i >= 0 && i < AugmentNames.Length ? AugmentNames[i] : "pair " + (i + 1);
                if (state.augEnergy > 0)
                {
                    var eta = controller.getAugProgressPerTick(state.augEnergy) > 0
                        ? (int)Math.Ceiling(controller.AugTimeLeftEnergy(state.augEnergy))
                        : -1;
                    return new AugmentStatus
                    {
                        Decision = "Installing " + label + " augment level " + (state.augLevel + 1),
                        Allocated = state.augEnergy,
                        Progress = state.augProgress,
                        EtaSeconds = eta
                    };
                }
                if (state.upgradeEnergy > 0)
                {
                    var eta = controller.getUpgradeProgressPerTick(state.upgradeEnergy) > 0
                        ? (int)Math.Ceiling(controller.UpgradeTimeLeftEnergy(state.upgradeEnergy))
                        : -1;
                    return new AugmentStatus
                    {
                        Decision = "Installing " + label + " upgrade level " + (state.upgradeLevel + 1),
                        Allocated = state.upgradeEnergy,
                        Progress = state.upgradeProgress,
                        EtaSeconds = eta
                    };
                }
            }
            return new AugmentStatus
            {
                Decision = "No Augment is currently fundable inside the rebirth horizon; Energy remains on higher marginal-value work",
                Allocated = 0,
                Progress = 0,
                EtaSeconds = -1
            };
        }

        private static readonly string[] AugmentNames =
        {
            "Safety Scissors", "Milk Infusion", "Cannon Implant", "Shoulder Mounted",
            "Actual Ammunition", "The Final Stand", "Buster"
        };

        private static string NextTitanName(Character c)
        {
            var items = c.inventory.itemList;
            if (!items.GRBComplete) return "GRB / Titan 1";
            if (!items.seedComplete) return "Grand Corrupted Tree / Titan 2";
            if (!items.jakeComplete) return "Jake / Titan 3";
            if (!items.uugComplete) return "UUG / Titan 4";
            if (!items.waldoComplete) return "Walderp / Titan 5";
            if (!items.beast1complete) return "The Beast / Titan 6";
            if (!items.nerdComplete) return "Greasy Nerd / Titan 7";
            if (!items.godmotherComplete) return "Godmother / Titan 8";
            if (!items.exileComplete) return "Exile / Titan 9";
            if (!items.spaceComplete) return "IT HUNGERS / Titan 10";
            if (!items.rockLobsterComplete) return "Rock Lobster / Titan 11";
            if (!items.amalgamateComplete) return "AMALGAMATE / Titan 12";
            return "next Titan version and drop-set milestone";
        }

        private static bool IsNextBossReady(Character c)
        {
            var boss = c.bossController;
            if (boss == null || boss.isFighting || boss.nukeBoss)
                return false;
            double killSeconds;
            return CombatHelpers.CanNukeCurrentBoss(c) || CombatHelpers.CanWinCurrentBoss(c, out killSeconds);
        }

        private static int CurrentBossKillEta(Character c)
        {
            if (c == null || c.bossController == null || !c.bossController.isFighting || c.bossCurHP <= 0)
                return -1;
            double killSeconds;
            double survivalSeconds;
            var survives = CombatHelpers.EvaluateFixedBossFight(c, c.attack, c.defense, c.curHP, c.bossCurHP,
                out killSeconds, out survivalSeconds);
            return !survives || double.IsInfinity(killSeconds) ? -1 : (int)Math.Ceiling(killSeconds);
        }

        private static string BossViabilityReason(Character c, bool ready, bool fighting, int killEta)
        {
            if (c == null || c.bossController == null)
                return "Fight Boss controller is unavailable";
            if (fighting)
                return killEta >= 0 ? "fight in progress; projected remaining combat time is " + killEta + " seconds"
                    : "fight in progress; current damage is not yet producing a finite kill ETA";
            if (ready)
                return "exact attack, defense, regeneration, and survival checks pass now";
            var outgoingPerTick = 0.02 * Math.Max(0.0, c.attack - c.bossDefense) - c.bossRegen;
            if (outgoingPerTick <= 0)
                return "holding because outgoing damage does not yet exceed the boss's defense and regeneration";
            var incomingPerTick = 0.02 * Math.Max(0.0, c.bossAttack - c.defense)
                                  - (0.001 + 0.001 * c.defense);
            if (incomingPerTick <= 0)
                return "waiting for the next controller viability refresh; boss cannot currently damage the player";
            var killSeconds = c.bossCurHP / outgoingPerTick * 0.02;
            var survivalSeconds = c.curHP / incomingPerTick * 0.02;
            return killSeconds >= survivalSeconds
                ? "holding until Attack shortens the fight or Defense/HP extends survival (kill "
                  + Math.Ceiling(killSeconds) + "s vs survival " + Math.Ceiling(survivalSeconds) + "s)"
                : "controller cooldown or boss-state gate is blocking an otherwise survivable attempt";
        }

        private static int NextBossViabilityEta(Character c, int rebirthTarget)
        {
            var immediateHorizon = Math.Max(0,
                rebirthTarget - (int)Math.Floor(c.rebirthTime.totalseconds));
            if (CombatHelpers.CanNukeCurrentBoss(c))
                return immediateHorizon >= 1 ? 1 : -1;
            if (IsNextBossReady(c))
            {
                double readyKillSeconds;
                var ready = ProjectedBossWin(c, 0, out readyKillSeconds)
                            && readyKillSeconds <= 120.0;
                var readyHorizon = Math.Max(0, rebirthTarget - (int)Math.Floor(c.rebirthTime.totalseconds));
                return ready && readyKillSeconds <= readyHorizon ? (int)Math.Ceiling(readyKillSeconds) : -1;
            }
            var horizon = Math.Max(0, rebirthTarget - (int)Math.Floor(c.rebirthTime.totalseconds));
            if (horizon <= 0) return -1;
            // Viability is not globally monotone because the remaining fight window
            // shrinks as the checkpoint approaches. Scan the finite event horizon and
            // return time-to-defeat, not merely time-until-startable.
            for (var wait = 0; wait <= horizon; wait++)
            {
                double killSeconds;
                if (!ProjectedBossWin(c, wait, out killSeconds)) continue;
                if (killSeconds > 120.0) continue;
                if (wait + killSeconds > horizon) continue;
                return (int)Math.Ceiling(wait + killSeconds);
            }
            return -1;
        }

        // Rebirth execution uses this same projection as telemetry so a planner
        // refresh cannot reset the run a fraction of a second before a selected
        // catch-up/record boss becomes defeatable.  The result is time-to-defeat,
        // including the wait for projected training/augment growth.
        internal static int SelectedBossDefeatEta(Character c, int horizonSeconds)
        {
            if (c == null || c.bossController == null || c.bossID > 300 || horizonSeconds <= 0)
                return -1;
            var absoluteTarget = (int)Math.Floor(c.rebirthTime.totalseconds) + horizonSeconds;
            return NextBossViabilityEta(c, absoluteTarget);
        }

        private static bool ProjectedBossWin(Character c, int seconds, out double killSeconds)
        {
            killSeconds = double.PositiveInfinity;
            var attackBase = Math.Max(0.0, c.training.getTotalAttack());
            var defenseBase = Math.Max(0.0, c.training.getTotalDefense());
            var attackGain = 0.0;
            var defenseGain = 0.0;
            for (var i = 0; i < 6; i++)
            {
                var attackLevel = c.training.attackTraining[i];
                var defenseLevel = c.training.defenseTraining[i];
                var attackLevels = TrainingLevelsGained(c, true, i, seconds);
                var defenseLevels = TrainingLevelsGained(c, false, i, seconds);
                attackGain += c.training.trainFactor[i]
                              * (Math.Pow(attackLevel + attackLevels, 1.3) - Math.Pow(attackLevel, 1.3));
                defenseGain += c.training.trainFactor[i]
                               * (Math.Pow(defenseLevel + defenseLevels, 1.3) - Math.Pow(defenseLevel, 1.3));
            }
            var attackTrainingMultiplier = c.attackMulti * c.adventureController.itopod.totalStatBonus()
                                           * (1.0 + c.inventoryController.attackBonus() / 100.0) * c.attackBoost;
            var defenseTrainingMultiplier = c.defenseMulti * c.adventureController.itopod.totalStatBonus()
                                            * (1.0 + c.inventoryController.defenseBonus() / 100.0) * c.defenseBoost;
            var currentAttackCore = Math.Max(1.0, 100.0 + attackBase * attackTrainingMultiplier);
            var currentDefenseCore = Math.Max(1.0, 100.0 + defenseBase * defenseTrainingMultiplier);
            var projectedAttackCore = 100.0 + (attackBase + attackGain) * attackTrainingMultiplier;
            var projectedDefenseCore = 100.0 + (defenseBase + defenseGain) * defenseTrainingMultiplier;
            var augRatio = ProjectedAugmentMultiplierRatio(c, seconds);
            var projectedAttack = c.attack * projectedAttackCore / currentAttackCore * augRatio;
            var projectedDefense = c.defense * projectedDefenseCore / currentDefenseCore * augRatio;
            var projectedBossHp = Math.Min(c.bossMaxHP, c.bossCurHP + c.bossRegen * 50.0 * seconds);
            var projectedMaxHp = 10.0 + projectedAttack * 10.0;
            var averageDefense = (c.defense + projectedDefense) / 2.0;
            var projectedPlayerHp = Math.Min(projectedMaxHp,
                c.curHP + 0.05 * (1.0 + averageDefense) * seconds);
            double survivalSeconds;
            return CombatHelpers.EvaluateFixedBossFight(c, projectedAttack, projectedDefense,
                projectedPlayerHp, projectedBossHp, out killSeconds, out survivalSeconds);
        }

        // Augment and Upgrade levels reset at rebirth, so only completions inside
        // this finite run horizon have combat value.  Model the first already-
        // allocated completion on every track, then recompute the exact raw
        // AllAugs sum; this also handles an Aug and its Upgrade completing in the
        // same horizon without dropping their multiplicative cross-term.
        private static double ProjectedAugmentMultiplierRatio(Character c, double seconds)
        {
            if (seconds <= 0 || c.augments == null || c.augmentsController == null
                || c.augments.augs == null || c.augmentsController.augments == null)
                return 1.0;
            try
            {
                var currentRaw = 1.0;
                var futureRaw = 1.0;
                var availableGold = Math.Max(0.0, c.realGold);
                var count = Math.Min(c.augments.augs.Length, c.augmentsController.augments.Length);
                for (var i = 0; i < count; i++)
                {
                    var state = c.augments.augs[i];
                    var controller = c.augmentsController.augments[i];
                    currentRaw += controller.getTotalStatBoost();
                    var level = state.augLevel;
                    var upgrade = state.upgradeLevel;
                    if (state.augEnergy > 0)
                    {
                        var eta = controller.AugTimeLeftEnergy(state.augEnergy);
                        if (!double.IsNaN(eta) && !double.IsInfinity(eta) && eta <= seconds)
                        {
                            var cost = state.augProgress > 0 ? 0.0 : controller.getAugCost();
                            if (cost <= availableGold)
                            {
                                availableGold -= cost;
                                level++;
                            }
                        }
                    }
                    if (state.upgradeEnergy > 0)
                    {
                        // The game's hypothetical Upgrade overload ignores its
                        // amount; the extension reproduces the native tick formula.
                        var eta = controller.UpgradeTimeLeftEnergy(state.upgradeEnergy);
                        if (!double.IsNaN(eta) && !double.IsInfinity(eta) && eta <= seconds)
                        {
                            var cost = state.upgradeProgress > 0 ? 0.0 : controller.getUpgradeCost();
                            if (cost <= availableGold)
                            {
                                availableGold -= cost;
                                upgrade++;
                            }
                        }
                    }
                    futureRaw += controller.baseBoost * (Math.Pow(upgrade, 2.0) + 1.0)
                                 * Math.Pow(level, controller.augTierBonus());
                }
                var currentTotal = c.augmentsController.totalBonus();
                var external = currentTotal / Math.Max(1e-300, currentRaw);
                var futureTotal = Math.Max(1.0, futureRaw * external);
                return futureTotal / Math.Max(1e-300, currentTotal);
            }
            catch
            {
                // A partial/unlocked controller array should degrade to the current
                // multiplier, never invent a projected stat jump.
                return 1.0;
            }
        }

        private static double TrainingRate(Character c, bool attack, int index)
        {
            var energy = attack ? c.training.attackEnergy[index] : c.training.defenseEnergy[index];
            var cap = attack ? c.training.attackCaps[index] : c.training.defenseCaps[index];
            if (energy <= 0 || cap <= 0) return 0.0;
            var ticksPerLevel = energy >= cap ? 1L : (long)Math.Ceiling((double)cap / energy);
            return 50.0 / ticksPerLevel * TrainingLevelMultiplier(c);
        }

        private static double TrainingLevelsGained(Character c, bool attack, int index, double seconds)
        {
            var energy = attack ? c.training.attackEnergy[index] : c.training.defenseEnergy[index];
            var cap = attack ? c.training.attackCaps[index] : c.training.defenseCaps[index];
            if (seconds <= 0 || energy <= 0 || cap <= 0) return 0.0;
            var ticks = Math.Max(0L, (long)Math.Floor(seconds * 50.0));
            if (ticks <= 0) return 0.0;
            var increment = Math.Min(1.0, (double)energy / cap);
            var progress = attack ? c.training.attackBarProgress[index] : c.training.defenseBarProgress[index];
            var first = Math.Max(1L, (long)Math.Ceiling(Math.Max(0.0, 1.0 - progress) / increment));
            if (ticks < first) return 0.0;
            var cycle = Math.Max(1L, (long)Math.Ceiling(1.0 / increment));
            var completions = 1L + (ticks - first) / cycle;
            return completions * TrainingLevelMultiplier(c);
        }

        private static int TrainingLevelMultiplier(Character c)
        {
            var levels = 1;
            if (c.adventure.itopod.perkLevel.Count > 15 && c.adventure.itopod.perkLevel[15] >= 1) levels++;
            if (c.beastQuest.quirkLevel.Count > 17 && c.beastQuest.quirkLevel[17] >= 1) levels++;
            if (c.wishes.wishes.Count > 23 && c.wishes.wishes[23].level >= 1) levels++;
            return levels;
        }

        private static string EnergyAllocationBreakdown(Character c)
        {
            var rows = new List<string>();
            for (var i = 0; i < 6; i++)
            {
                var attackEnergy = c.training.attackEnergy[i];
                var defenseEnergy = c.training.defenseEnergy[i];
                var attackRate = TrainingRate(c, true, i);
                var defenseRate = TrainingRate(c, false, i);
                var attackUnlocked = i == 0 || c.training.attackTraining[i - 1] > 5000L * i;
                var defenseUnlocked = i == 0 || c.training.defenseTraining[i - 1] > 5000L * i;
                rows.Add("{\"pair\":\"" + EscapeJson(AttackTrainingNames[i] + " + " + DefenseTrainingNames[i])
                         + "\",\"syncTraining\":" + c.settings.syncTraining.ToString().ToLowerInvariant()
                         + ",\"attackUnlocked\":" + attackUnlocked.ToString().ToLowerInvariant()
                         + ",\"defenseUnlocked\":" + defenseUnlocked.ToString().ToLowerInvariant()
                         + ",\"attackLevel\":" + c.training.attackTraining[i]
                         + ",\"defenseLevel\":" + c.training.defenseTraining[i]
                         + ",\"attackCap\":" + c.training.attackCaps[i]
                         + ",\"defenseCap\":" + c.training.defenseCaps[i]
                         + ",\"attackBarProgress\":" + c.training.attackBarProgress[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                         + ",\"defenseBarProgress\":" + c.training.defenseBarProgress[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                         + ",\"attackEnergy\":" + attackEnergy + ",\"defenseEnergy\":" + defenseEnergy
                         + ",\"totalEnergy\":" + (attackEnergy + defenseEnergy)
                         + ",\"attackLevelsPerSecond\":" + attackRate.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                         + ",\"defenseLevelsPerSecond\":" + defenseRate.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "}");
            }
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static long BasicTrainingEnergy(Character c)
        {
            long total = 0;
            for (var i = 0; i < 6; i++)
                total += Math.Max(0L, c.training.attackEnergy[i])
                         + Math.Max(0L, c.training.defenseEnergy[i]);
            return total;
        }

        private static readonly string[] AttackTrainingNames =
        {
            "Basic Attack", "Strong Attack", "Parry", "Piercing Attack", "Ultimate Attack", "Mega Buff"
        };

        private static readonly string[] DefenseTrainingNames =
        {
            "Basic Defense", "Defensive Buff", "Heal", "Block", "Ultimate Buff", "Oh Shit"
        };

        private static void GetNextTrainingGoal(Character c, out string goal, out int etaSeconds)
        {
            goal = "Keep all unlocked Basic Trainings speed-capped";
            etaSeconds = 0;
            var fallbackGoal = goal;
            long smallestRemaining = long.MaxValue;
            var bestEta = int.MaxValue;

            for (var i = 1; i < 6; i++)
            {
                var attackTarget = 5000L * i + 1L;
                if (c.training.attackTraining[i - 1] < attackTarget)
                {
                    var remaining = attackTarget - c.training.attackTraining[i - 1];
                    var attackGoal = "Unlock " + AttackTrainingNames[i];
                    if (remaining < smallestRemaining)
                    {
                        smallestRemaining = remaining;
                        fallbackGoal = attackGoal;
                    }
                    ConsiderTrainingEta(c, ref goal, ref bestEta, attackGoal, remaining,
                        c.training.attackEnergy[i - 1], c.training.attackCaps[i - 1]);
                }

                var defenseTarget = 5000L * i + 1L;
                if (c.training.defenseTraining[i - 1] < defenseTarget)
                {
                    var remaining = defenseTarget - c.training.defenseTraining[i - 1];
                    var defenseGoal = "Unlock " + DefenseTrainingNames[i];
                    if (remaining < smallestRemaining)
                    {
                        smallestRemaining = remaining;
                        fallbackGoal = defenseGoal;
                    }
                    ConsiderTrainingEta(c, ref goal, ref bestEta, defenseGoal, remaining,
                        c.training.defenseEnergy[i - 1], c.training.defenseCaps[i - 1]);
                }
            }

            if (bestEta != int.MaxValue)
                etaSeconds = bestEta;
            else if (smallestRemaining != long.MaxValue)
            {
                goal = fallbackGoal;
                etaSeconds = -1;
            }
        }

        private static void ConsiderTrainingEta(Character c, ref string bestGoal, ref int bestEta, string candidateGoal,
            long remainingLevels, long allocatedEnergy, long capEnergy)
        {
            var eta = TrainingEta(c, remainingLevels, allocatedEnergy, capEnergy);
            if (eta < 0)
                return;
            if (eta >= bestEta)
                return;
            bestEta = eta;
            bestGoal = candidateGoal;
        }

        private static int TrainingEta(Character c, long remainingLevels, long allocatedEnergy, long capEnergy)
        {
            if (remainingLevels <= 0) return 0;
            if (allocatedEnergy <= 0 || capEnergy <= 0) return -1;
            // Native BT discards bar overshoot, so below cap the discrete rate is
            // one level every ceil(cap / energy) ticks—not the continuous E/cap
            // approximation.
            var ticksPerLevel = allocatedEnergy >= capEnergy ? 1L
                : (long)Math.Ceiling((double)capEnergy / allocatedEnergy);
            var levelsPerSecond = 50.0 / ticksPerLevel * TrainingLevelMultiplier(c);
            return levelsPerSecond <= 0 ? -1 : (int)Math.Ceiling(remainingLevels / levelsPerSecond);
        }

        private void BuyBestExpPackage()
        {
            var c = Main.Character;
            if (c.highestBoss < 17 || c.realExp <= Config.ExpReserve)
                return;

            var candidates = BuildExpCandidates(c);
            // Select before testing affordability.  Buying a locally affordable
            // runner-up can delay the higher-return permanent package and increase
            // total progression time; in that case saving is the actual action.
            var best = candidates.Where(x => x.Cost > 0).OrderBy(x => x.Score).FirstOrDefault();
            if (best == null || best.Cost > c.realExp - Config.ExpReserve)
                return;

            SetPurchaseRatio(best.Controller, best.Power, best.Cap, best.Bars);
            var method = best.Controller.GetType().GetMethod("buyCustomAll", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                return;
            var expBefore = c.realExp;
            method.Invoke(best.Controller, null);
            var spent = expBefore - c.realExp;
            Main.LogAction(spent > 0 ? "PURCHASE" : "REJECTED",
                spent > 0
                    ? "Bought " + best.Name + " EXP package (" + best.Power + "/" + best.Cap + "/" + best.Bars
                      + ") for " + spent + " EXP [confirmed by EXP delta]"
                    : "EXP purchase for " + best.Name + " produced no EXP delta");
        }

        private static List<PurchaseCandidate> BuildExpCandidates(Character c)
        {
            var candidates = new List<PurchaseCandidate>();
            var currentDifficulty = c.settings.rebirthDifficulty;
            var energyWeight = currentDifficulty == difficulty.normal ? 3.0 : 2.0;
            var magicWeight = 1.0;
            var r3Weight = currentDifficulty == difficulty.normal ? 0.0 : currentDifficulty == difficulty.evil ? 1.5 : 2.0;
            var packagePower = currentDifficulty == difficulty.normal ? 5 : 4;
            var packageCap = currentDifficulty == difficulty.normal ? 160000 : 150000;
            var packageBars = currentDifficulty == difficulty.normal ? 4 : 1;

            AddCandidate(candidates, c.energyPurchases, "Energy", c.energyPower / energyWeight,
                packagePower, packageCap, packageBars);
            if (c.highestBoss >= 37)
                AddCandidate(candidates, c.magicPurchases, "Magic", c.magic.magicPower / magicWeight,
                    packagePower, packageCap, packageBars);
            if (c.res3.res3On && r3Weight > 0)
                AddCandidate(candidates, c.res3Purchases, "Resource 3", c.res3.res3Power / r3Weight,
                    packagePower, packageCap, packageBars);
            return candidates;
        }

        private static void OpenExpBoxes()
        {
            var c = Main.Character;
            if (c.lootBoxes == null || c.lootBoxes.expBoxCount <= 0)
                return;
            var controller = UnityEngine.Resources.FindObjectsOfTypeAll<LootBoxController>()
                .FirstOrDefault(x => x != null && x.character == c);
            if (controller == null)
                return;
            var boxesBefore = c.lootBoxes.expBoxCount;
            var expBefore = c.realExp;
            var opened = 0;
            while (c.lootBoxes.expBoxCount > 0 && opened < 100)
            {
                var countBefore = c.lootBoxes.expBoxCount;
                controller.openExpBox();
                if (c.lootBoxes.expBoxCount >= countBefore)
                    break;
                opened++;
            }
            Main.LogAction(opened > 0 ? "REWARD" : "REJECTED",
                opened > 0
                    ? "Opened " + opened + " EXP boxes for " + (c.realExp - expBefore)
                      + " EXP [confirmed by box count]"
                    : "EXP-box request produced no box-count transition");
        }

        private bool BuyAtomicExpUpgrade()
        {
            var c = Main.Character;
            if (c.energyPurchases == null || c.realExp <= Config.ExpReserve || c.energySpeed >= 49.91f)
                return false;

            var expBefore = c.realExp;
            var speedBefore = c.energySpeed;
            var purchases = 0;
            var buyOne = c.energyPurchases.GetType().GetMethod("buyEnergySpeed10",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var buyTen = c.energyPurchases.GetType().GetMethod("buyEnergySpeed100",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var specialFlags = new[] {c.settings.special1Bought, c.settings.special2Bought, c.settings.special3Bought};
            var specialCosts = new[] {1, 2, 3};
            var specialMethods = new[] {"buyEnergySpeedSpecial1", "buyEnergySpeedSpecial2", "buyEnergySpeedSpecial3"};
            for (var i = 0; i < specialMethods.Length && c.energySpeed < 49.91f; i++)
            {
                if (specialFlags[i] || specialCosts[i] > c.realExp - Config.ExpReserve)
                    continue;
                var special = c.energyPurchases.GetType().GetMethod(specialMethods[i],
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (special == null) continue;
                var before = c.realExp;
                special.Invoke(c.energyPurchases, null);
                if (c.realExp >= before) continue;
                purchases++;
            }
            while (c.energySpeed < 49.01f && purchases < 1000
                   && c.energyPurchases.energySpeed100Cost() <= c.realExp - Config.ExpReserve
                   && buyTen != null)
            {
                var before = c.realExp;
                buyTen.Invoke(c.energyPurchases, null);
                if (c.realExp >= before) break;
                purchases++;
            }
            while (c.energySpeed < 49.91f && purchases < 1000
                   && c.energyPurchases.energySpeed10Cost() <= c.realExp - Config.ExpReserve
                   && buyOne != null)
            {
                var before = c.realExp;
                buyOne.Invoke(c.energyPurchases, null);
                if (c.realExp >= before) break;
                purchases++;
            }
            var confirmed = c.realExp < expBefore && c.energySpeed > speedBefore;
            if (purchases > 0)
                Main.LogAction(confirmed ? "PURCHASE" : "REJECTED",
                    confirmed
                        ? "Bought " + purchases + " Energy-speed purchases: "
                          + speedBefore.ToString("0.0") + " -> " + c.energySpeed.ToString("0.0")
                          + " for " + (expBefore - c.realExp) + " EXP [confirmed by both deltas]"
                        : "Energy-speed purchase produced no verified EXP/speed transition");
            return confirmed;
        }

        private bool BuyEarlyAdventureStatAtom()
        {
            var c = Main.Character;
            if (c.highestBoss < 4 || c.highestBoss >= 17 || c.adventurePurchases == null
                || c.realExp - Config.ExpReserve < 3)
                return false;

            var best = EarlyAdventureAtomIndex(c);
            if (best < 0) return false;
            var current = new[]
            {
                Math.Max(1.0, Convert.ToDouble(c.adventure.attack)),
                Math.Max(1.0, Convert.ToDouble(c.adventure.defense))
            };
            var methods = new[] {"buy1Attack", "buy1Defense"};
            var labels = new[] {"Adventure Power", "Adventure Toughness"};

            var method = c.adventurePurchases.GetType().GetMethod(methods[best],
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                return false;
            var expBefore = c.realExp;
            var statBefore = current[best];
            method.Invoke(c.adventurePurchases, null);
            var statAfter = best == 0 ? Convert.ToDouble(c.adventure.attack)
                : best == 1 ? Convert.ToDouble(c.adventure.defense)
                : Convert.ToDouble(c.adventure.maxHP);
            var confirmed = c.realExp < expBefore && statAfter > statBefore;
            Main.LogAction(confirmed ? "PURCHASE" : "REJECTED",
                confirmed
                    ? "Bought +1 " + labels[best] + " for "
                      + (expBefore - c.realExp) + " EXP [confirmed by EXP/stat deltas]"
                    : labels[best] + " atomic purchase produced no verified EXP/stat transition");
            return confirmed;
        }

        private static int EarlyAdventureAtomIndex(Character c)
        {
            if (c == null || ZoneStatHelper.UserOverrides == null || c.highestBoss < 4 || c.highestBoss >= 17)
                return -1;
            var power = c.totalAdvAttack();
            var toughness = c.totalAdvDefense();
            var maxZone = ZoneHelpers.GetMaxReachableZone(false);
            foreach (var zone in ZoneStatHelper.UserOverrides.Where(x => x.Key <= maxZone)
                         .OrderBy(x => x.Key))
            {
                if (zone.Value.FightType(power, toughness) > 0) continue;
                var powerGap = zone.Value.MPower - power;
                var toughnessGap = zone.Value.MToughness - toughness;
                if (powerGap > 0 && powerGap <= 1.0 && toughnessGap <= 0) return 0;
                if (toughnessGap > 0 && toughnessGap <= 1.0 && powerGap <= 0) return 1;
                return -1;
            }
            return -1;
        }

        private bool BuyDaycareUnlock()
        {
            var c = Main.Character;
            if (c.highestBoss < 17 || c.adventurePurchases == null)
                return false;
            var available = c.realExp - Config.ExpReserve;
            if (available <= 0)
                return false;

            var lifetime = Math.Max(1.0, c.stats.totalExp);
            if (!c.purchases.hasDaycare && available >= 250 && 250 <= lifetime * .10)
                return TryBuyDaycare("buyDaycare", "Item Daycare", c.purchases.hasDaycare);
            if (c.purchases.hasDaycare && !c.purchases.hasDaycareSlot2
                && available >= 25000 && 25000 <= lifetime * .10)
                return TryBuyDaycare("buyDaycareSlot2", "Daycare slot 2", c.purchases.hasDaycareSlot2);
            if (c.purchases.hasDaycare && !c.purchases.hasDaycareSlot3
                && available >= 500000 && 500000 <= lifetime * .10)
                return TryBuyDaycare("buyDaycareSlot3", "Daycare slot 3", c.purchases.hasDaycareSlot3);
            return false;
        }

        private bool BuyStrategicPermanentExpUpgrade()
        {
            var c = Main.Character;
            var target = GetStrategicPermanentExpTarget(c);
            if (target == null)
                return false;
            // Returning true while saving is deliberate: buying a smaller resource
            // package would push the permanent unlock farther away.
            if (target.Cost > c.realExp - Config.ExpReserve)
                return true;
            var expBefore = c.realExp;
            var stateBefore = target.State();
            var method = target.Controller.GetType().GetMethod(target.Method,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                Main.LogAction("REJECTED", target.Label + " purchase API was not found");
                return true;
            }
            method.Invoke(target.Controller, null);
            var confirmed = c.realExp < expBefore && target.State() != stateBefore;
            Main.LogAction(confirmed ? "PURCHASE" : "REJECTED", confirmed
                ? "Bought " + target.Label + " for " + (expBefore - c.realExp)
                  + " EXP [confirmed by EXP and ownership/stat deltas]"
                : target.Label + " purchase produced no verified ownership/stat transition");
            return true;
        }

        private static PermanentExpTarget GetStrategicPermanentExpTarget(Character c)
        {
            if (c == null || c.adventurePurchases == null || c.miscPurchases == null)
                return null;
            var lifetime = Math.Max(1.0, c.stats.totalExp);
            var targets = new List<PermanentExpTarget>();
            if (c.highestBoss >= 4 && c.purchases.boost < .999f)
                targets.Add(new PermanentExpTarget(c.adventurePurchases, "buyRecycleBoost",
                    "Boost Recycling", 100, () => c.purchases.boost,
                    "permanently recovers more boost value into gear and the Infinity Cube"));
            if (c.highestBoss >= 4 && !c.purchases.hasAcc3)
                targets.Add(new PermanentExpTarget(c.adventurePurchases, "buyAcc3",
                    "Accessory slot 3", 3000, () => c.purchases.hasAcc3 ? 1.0 : 0.0,
                    "an additional equipped special compounds every combat and resource loadout"));
            if (c.settings.diggersOn && !c.purchases.hasDiggerSlot1)
                targets.Add(new PermanentExpTarget(c.miscPurchases, "buydigger1",
                    "Digger slot", 25000, () => c.purchases.hasDiggerSlot1 ? 1.0 : 0.0,
                    "parallel permanent digger bonuses remove repeated gold/Adventure bottlenecks"));
            if (c.settings.beardsOn && !c.purchases.hasBeardSlot1)
                targets.Add(new PermanentExpTarget(c.miscPurchases, "buybeard1",
                    "Beard slot", 50000, () => c.purchases.hasBeardSlot1 ? 1.0 : 0.0,
                    "a second permanent beard conversion stream repays across every long rebirth"));
            if (c.highestBoss >= 4 && c.purchases.hasAcc3 && !c.purchases.hasAcc5)
                targets.Add(new PermanentExpTarget(c.adventurePurchases, "buyAcc5",
                    "Accessory slot 5", 30000, () => c.purchases.hasAcc5 ? 1.0 : 0.0,
                    "an additional equipped special compounds every contextual loadout"));
            if (c.inventory.macguffins != null && c.inventory.macguffins.Count > 0
                && !c.purchases.hasMacguffinSlot1)
                targets.Add(new PermanentExpTarget(c.miscPurchases, "buyMacguffin1",
                    "MacGuffin slot", 10000000, () => c.purchases.hasMacguffinSlot1 ? 1.0 : 0.0,
                    "banks another permanent MacGuffin bonus on every rebirth"));

            // The guide's 10%-of-lifetime rule is used only as an opportunity-cost
            // admission test.  Within admitted upgrades we still use a progression
            // order, and we save rather than buying an inferior affordable package.
            return targets.FirstOrDefault(x => x.Cost <= lifetime * .10);
        }

        private static bool TryBuyDaycare(string methodName, string label, bool flagBefore)
        {
            var c = Main.Character;
            var expBefore = c.realExp;
            var method = c.adventurePurchases.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
                return false;
            method.Invoke(c.adventurePurchases, null);
            var flagAfter = methodName == "buyDaycare" ? c.purchases.hasDaycare
                : methodName == "buyDaycareSlot2" ? c.purchases.hasDaycareSlot2
                : c.purchases.hasDaycareSlot3;
            var confirmed = !flagBefore && flagAfter && c.realExp < expBefore;
            Main.LogAction(confirmed ? "PURCHASE" : "REJECTED",
                confirmed
                    ? "Bought " + label + " for " + (expBefore - c.realExp)
                      + " EXP [confirmed by unlock and EXP delta]"
                    : label + " purchase produced no verified unlock/EXP transition");
            return confirmed;
        }

        private bool BuyBestYggPermanent()
        {
            var c = Main.Character;
            var controller = c.yggdrasilPurchases;
            if (!c.settings.yggdrasilOn || controller == null || controller.fruitCosts == null)
                return false;
            var best = -1;
            var bestScore = double.MinValue;
            var count = Math.Min(c.yggdrasil.fruits.Count, controller.fruitCosts.Length);
            for (var i = 0; i < count; i++)
            {
                var fruit = c.yggdrasil.fruits[i];
                var cost = controller.fruitCosts[i];
                if (fruit.maxTier <= 0 || fruit.permCostPaid || cost <= 0
                    || cost > c.realExp - Config.ExpReserve || !controller.canBuy(i))
                    continue;
                var activation = c.yggdrasilController.activationCost[i];
                var resourceWeight = c.yggdrasilController.usesEnergy[i] ? 1.0 : 1.35;
                var score = resourceWeight * Math.Log(1.0 + Math.Max(1L, activation)) / cost;
                if (score <= bestScore) continue;
                bestScore = score;
                best = i;
            }
            if (best < 0)
                return false;

            var targetField = controller.GetType().GetField("fruitToBuy",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var buyMethod = controller.GetType().GetMethod("buyFruit",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (targetField == null || buyMethod == null)
                return false;
            var expBefore = c.realExp;
            targetField.SetValue(controller, best);
            buyMethod.Invoke(controller, null);
            var confirmed = c.yggdrasil.fruits[best].permCostPaid && c.realExp < expBefore;
            Main.LogAction(confirmed ? "PURCHASE" : "REJECTED",
                confirmed
                    ? "Bought permanent auto-activation for Yggdrasil fruit " + best + " for "
                      + (expBefore - c.realExp) + " EXP [confirmed by fruit flag and EXP delta]"
                    : "Yggdrasil permanent purchase produced no verified flag/EXP transition");
            return confirmed;
        }

        private static readonly Dictionary<int, string> ApPurchaseMethods = new Dictionary<int, string>
        {
            {14, "buyYellowHeartAP"},
            {16, "buyStarterPackAP"},
            {12, "buyCustomPercent1AP"},
            {13, "buyCustomPercent2AP"},
            {9, "buyInstaTrainAP"},
            {7, "buyLootFilterAP"},
            {8, "buyAutoBoostMergeAP"},
            {56, "buyAutoNukeAP"},
            {17, "buyAcc4AP"},
            {34, "buyAcc5AP"},
            {54, "buyAcc6AP"},
            {62, "buyAcc7AP"},
            {74, "buyAcc8AP"},
            {81, "buyAcc9AP"},
            {21, "buyYggReminderAP"},
            {22, "buyExtendedSpinBankAP"},
            {25, "buyLoadoutSlotAP"},
            {28, "buyBeardAP"},
            {29, "buyCubeFilterAP"},
            {32, "buyDaycareSpeedAP"},
            {47, "buyQuestLightAP"},
            {48, "buyFasterQuests1AP"},
            {49, "buyExtendedQuestBankAP"},
            {39, "buyLazyITOPODAP"},
            {40, "buyDiggerSlotAP"},
            {41, "buyMacguffinSlotAP"},
            {55, "buyCustomIdlePercent1AP"},
            {57, "buyDaycareArtAP"},
            {58, "buyNGUCapModifierAP"},
            {64, "buyRes3Percent1AP"},
            {65, "buyRes3Percent2AP"},
            {66, "buyRes3IdlePercent1AP"},
            {67, "buyRes3NameGeneratorAP"},
            {68, "buyFasterWishAP"},
            {69, "buyInvMergeSlotAP"},
            {71, "buyAdvLightAP"},
            {72, "buyAdvAdvancerAP"},
            {73, "buyGoToQuestAP"},
            {75, "buyDeckSlotAP"},
            {76, "buyMayoGenAP"},
            {77, "buyTagSlotAP"},
            {15, "buyInventoryAP"}
        };

        private static readonly int[] ApPurchaseOrder =
        {
            14, 16, 12, 13, 9, 56, 17, 34, 54, 62, 74, 81,
            32, 21, 22, 25, 28, 29, 47, 48, 49, 39, 40, 41, 55, 57,
            58, 64, 65, 66, 67, 68, 69, 71, 72, 73, 75, 76, 77, 15,
            // Bot-managed filtering/merging duplicates these convenience upgrades;
            // buy them only after upgrades that create progression value.
            7, 8
        };

        private void SpendBestApUpgrade()
        {
            var c = Main.Character;
            var available = c.arbitrary.curArbitraryPoints - Config.ApReserve;
            if (available <= 0)
                return;

            var controller = GetArbitraryController(c);
            if (controller == null)
                return;

            // Preserve AP for the next high-impact permanent gate instead of draining it
            // into whatever cheap button happens to be affordable first.
            if (!c.arbitrary.instaTrain)
            {
                TryBuyApUpgrade(controller, 9, available, ApPurchaseMethods[9]);
                return;
            }
            if (!c.arbitrary.hasStarterPack)
            {
                TryBuyApUpgrade(controller, 16, available, ApPurchaseMethods[16]);
                return;
            }
            // The script respects the game's unlocks rather than writing locked filter
            // flags directly. Once bought, native filtering prevents maxed drops from
            // consuming inventory and blocking continuous Adventure farming.
            if (!HasYellowHeartDropped(c) && CanReceiveYellowHeart(c))
            {
                TryBuyYellowHeart(controller, available);
                return;
            }

            // MAXX collection deliberately retains merge candidates. When verified
            // free slots fall below that live debt, reserve AP for space instead of
            // draining it into a lower-ranked convenience purchase.
            if (!IsApOwned(c, 15)
                && AdventureCollectionPlanner.InventoryPressureHigh(c, _collectionTarget))
            {
                TryBuyApUpgrade(controller, 15, available, ApPurchaseMethods[15]);
                return;
            }

            foreach (var id in ApPurchaseOrder)
            {
                if (id == 9 || id == 14 || id == 16 || IsApOwned(c, id) || !IsApFeatureUnlocked(c, id))
                    continue;
                if (TryBuyApUpgrade(controller, id, available, ApPurchaseMethods[id]))
                    return;
            }
        }

        private static ArbitraryController GetArbitraryController(Character c)
        {
            var controller = c.allArbitrary == null ? null : c.allArbitrary.arbitraryPods
                .FirstOrDefault(x => x != null && x.character == c);
            if (controller == null && c.allArbitrary != null)
                controller = c.allArbitrary.randomArbitraryController;
            if (controller == null)
                controller = UnityEngine.Resources.FindObjectsOfTypeAll<ArbitraryController>()
                    .FirstOrDefault(x => x != null && x.character == c);
            return controller;
        }

        private static bool HasYellowHeartMaxxed(Character c)
        {
            return c.inventory.itemList.itemMaxxed != null
                   && c.inventory.itemList.itemMaxxed.Count > 129
                   && c.inventory.itemList.itemMaxxed[129];
        }

        private static bool HasYellowHeartDropped(Character c)
        {
            return c.inventory.itemList.itemDropped != null
                   && c.inventory.itemList.itemDropped.Count > 129
                   && c.inventory.itemList.itemDropped[129];
        }

        private static bool CanReceiveYellowHeart(Character c)
        {
            return c.inventoryController != null && c.inventoryController.freeSpace();
        }

        private static bool TryBuyYellowHeart(ArbitraryController controller, long available)
        {
            var c = controller.character;
            var accessoryFilter = c.settings.filterAccessory;
            var itemFilterExists = c.inventory.itemList.itemFiltered != null
                                   && c.inventory.itemList.itemFiltered.Count > 129;
            var itemFilter = itemFilterExists && c.inventory.itemList.itemFiltered[129];
            try
            {
                // Native addItem applies filters synchronously. Temporarily exempt the
                // target, verify the AP/item transition, then restore the user's broad
                // filtering policy so Heart maxing cannot deadlock.
                c.settings.filterAccessory = false;
                if (itemFilterExists) c.inventory.itemList.itemFiltered[129] = false;
                return TryBuyApUpgrade(controller, 14, available, ApPurchaseMethods[14]);
            }
            finally
            {
                c.settings.filterAccessory = accessoryFilter;
                if (itemFilterExists) c.inventory.itemList.itemFiltered[129] = itemFilter;
            }
        }

        private static int NextAvailableApPurchase(ArbitraryController controller)
        {
            var c = controller.character;
            foreach (var id in ApPurchaseOrder)
            {
                if (id == 9 || id == 14 || id == 16 || !ApPurchaseMethods.ContainsKey(id))
                    continue;
                if (!IsApOwned(c, id) && IsApFeatureUnlocked(c, id))
                    return id;
            }
            return -1;
        }

        private static bool IsApFeatureUnlocked(Character c, int id)
        {
            switch (id)
            {
                case 21: return c.settings.yggdrasilOn;
                case 28: return c.settings.beardsOn;
                case 32: return c.purchases.hasDaycare;
                case 39: return c.settings.itopodOn;
                case 40: return c.settings.diggersOn;
                case 41: return c.achievements.achievementComplete.Count > 145
                                && c.achievements.achievementComplete[145];
                case 47:
                case 48:
                case 49: return c.settings.beastOn;
                case 55: return c.highestBoss >= 37;
                case 57: return c.purchases.hasDaycare;
                case 58: return c.settings.nguOn;
                case 64:
                case 65:
                case 66:
                case 67: return c.res3.res3On;
                case 68: return c.wishes.wishesOn;
                case 71:
                case 72: return c.highestBoss >= 4;
                case 73: return c.beastQuest.questsUnlocked;
                case 75:
                case 76:
                case 77: return c.cards.cardsOn;
                case 74:
                case 81: return c.settings.rebirthDifficulty >= difficulty.evil;
                default: return true;
            }
        }

        private static bool IsApOwned(Character c, int id)
        {
            switch (id)
            {
                case 7: return c.arbitrary.lootFilter;
                case 8: return c.arbitrary.improvedAutoBoostMerge;
                case 9: return c.arbitrary.instaTrain;
                case 12: return c.purchases.hasCustomEnergyPercent1 && c.purchases.hasCustomMagicPercent1;
                case 13: return c.purchases.hasCustomEnergyPercent2 && c.purchases.hasCustomMagicPercent2;
                // Heart purchase methods remain callable after purchase; ownership
                // is the dropped-item flag, not the later level-100 AP bonus flag.
                case 14: return HasYellowHeartDropped(c);
                case 15: return c.arbitrary.inventorySpaces >= 166;
                case 16: return c.arbitrary.hasStarterPack;
                case 17: return c.arbitrary.hasAcc4;
                case 21: return c.arbitrary.hasYggdrasilReminder;
                case 22: return c.arbitrary.hasExtendedSpinBank;
                case 25: return c.arbitrary.curLoadoutSlots >= 7;
                case 28: return c.arbitrary.beardSlots >= 4;
                case 29: return c.arbitrary.hasCubeFilter;
                case 32: return c.arbitrary.hasDaycareSpeed;
                case 34: return c.arbitrary.hasAcc5;
                case 47: return c.arbitrary.hasQuestLight;
                case 48: return c.arbitrary.hasFasterQuests;
                case 49: return c.arbitrary.hasExtendedQuestBank;
                case 54: return c.arbitrary.hasAcc6;
                case 55: return c.purchases.hasCustomIdleEnergyPercent1
                                && c.purchases.hasCustomIdleMagicPercent1;
                case 56: return c.arbitrary.boughtAutoNuke;
                case 57: return c.arbitrary.boughtDaycareArt;
                case 58: return c.arbitrary.hasNGUCapModifier;
                case 62: return c.arbitrary.hasAcc7;
                case 64: return c.purchases.hasCustomRes3Percent1;
                case 65: return c.purchases.hasCustomRes3Percent2;
                case 66: return c.purchases.hasCustomIdleRes3Percent1;
                case 67: return c.arbitrary.res3NameGeneratorBought;
                case 68: return c.arbitrary.wishSpeedBoster;
                case 69: return c.arbitrary.invMergeSlots >= 4;
                case 71: return c.arbitrary.advLightBought;
                case 72: return c.arbitrary.advAdvancerBought;
                case 73: return c.arbitrary.goToQuestZoneBought;
                case 74: return c.arbitrary.hasAcc8;
                case 75: return c.arbitrary.deckSpaceBought >= 50;
                case 76: return c.arbitrary.mayoGenSlots >= 2;
                case 77: return c.arbitrary.gotTagslot1;
                case 81: return c.arbitrary.hasAcc9;
                case 39: return c.arbitrary.boughtLazyITOPOD;
                case 40: return c.arbitrary.diggerSlots >= 6;
                case 41: return c.arbitrary.macguffinSlots >= 11;
                default: return false;
            }
        }

        private static long GetApCost(ArbitraryController controller, int id)
        {
            var previousId = controller.id;
            try
            {
                controller.id = id;
                return controller.cost();
            }
            finally
            {
                controller.id = previousId;
            }
        }

        private static bool TryBuyApUpgrade(ArbitraryController controller, int id, long available, string methodName)
        {
            var previousId = controller.id;
            var previousName = controller.itemName;
            try
            {
                controller.id = id;
                controller.itemName = methodName.Substring(3).Replace("AP", string.Empty);
                var cost = controller.cost();
                if (cost <= 0 || cost > available)
                    return false;
                var method = controller.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null)
                    return false;
                var apBefore = controller.character.arbitrary.curArbitraryPoints;
                method.Invoke(controller, null);
                var confirmed = controller.character.arbitrary.curArbitraryPoints < apBefore;
                Main.LogAction(confirmed ? "PURCHASE" : "REJECTED",
                    confirmed
                        ? "Bought AP upgrade " + controller.itemName + " for "
                          + (apBefore - controller.character.arbitrary.curArbitraryPoints)
                          + " AP [confirmed by AP delta]"
                        : "AP purchase for " + controller.itemName + " produced no AP delta");
                return confirmed;
            }
            finally
            {
                controller.id = previousId;
                controller.itemName = previousName;
            }
        }

        private static readonly string[] UpgradeKeywords =
        {
            "adventure", "ngu", "ygg", "fruit", "quest", "hack", "wish",
            "pp", "qp", "card", "energy power", "magic power", "energy cap", "magic cap"
        };

        private void SpendBestPerk()
        {
            var c = Main.Character;
            var controller = c.adventureController.itopod;
            var points = c.adventure.itopod.perkPoints;
            var best = FindBestUpgrade(controller.perkName, c.adventure.itopod.perkLevel,
                controller.maxLevel, controller.effectPerLevel, id => controller.perkCost(id), points - Config.PPReserve,
                controller.perkDifficultyReq, c.settings.rebirthDifficulty, id =>
                {
                    if (id < 0 || id >= controller.perkType.Count) return false;
                    var type = controller.perkType[id];
                    if (type == itopodPerk.MacGuffin)
                        return c.achievements.achievementComplete.Count > 145
                               && c.achievements.achievementComplete[145];
                    if (type == itopodPerk.Wishes) return c.wishes.wishesOn;
                    if (type == itopodPerk.Hacks) return c.hacks.hacksOn;
                    if (type == itopodPerk.Cards) return c.cards.cardsOn;
                    if (type == itopodPerk.Res3) return c.res3.res3On;
                    return true;
                });
            if (best < 0) return;
            var pointsBefore = c.adventure.itopod.perkPoints;
            var levelBefore = c.adventure.itopod.perkLevel[best];
            controller.tryLevelUp(best);
            var confirmed = c.adventure.itopod.perkPoints < pointsBefore
                            || c.adventure.itopod.perkLevel[best] > levelBefore;
            Main.LogAction(confirmed ? "PURCHASE" : "REJECTED",
                confirmed
                    ? "Bought perk " + controller.perkName[best] + " [confirmed by PP/level delta]"
                    : "Perk purchase for " + controller.perkName[best] + " produced no state transition");
        }

        private void SpendBestQuirk()
        {
            var c = Main.Character;
            var controller = c.beastQuestPerkController;
            var points = c.beastQuest.quirkPoints;
            var best = FindBestUpgrade(controller.quirkName, c.beastQuest.quirkLevel,
                controller.maxLevel, controller.effectPerLevel, id => controller.quirkCost(id), points - Config.QPReserve,
                controller.quirkDifficultyReq, c.settings.rebirthDifficulty, id =>
                {
                    if (id < 0 || id >= controller.quirkType.Count) return false;
                    var type = controller.quirkType[id];
                    if (type == itopodPerk.Res3) return c.res3.res3On;
                    if (type == itopodPerk.Wishes) return c.wishes.wishesOn;
                    if (type == itopodPerk.Cards) return c.cards.cardsOn;
                    return true;
                });
            if (best < 0) return;
            var pointsBefore = c.beastQuest.quirkPoints;
            var levelBefore = c.beastQuest.quirkLevel[best];
            controller.tryLevelUp(best);
            var confirmed = c.beastQuest.quirkPoints < pointsBefore || c.beastQuest.quirkLevel[best] > levelBefore;
            Main.LogAction(confirmed ? "PURCHASE" : "REJECTED",
                confirmed
                    ? "Bought quirk " + controller.quirkName[best] + " [confirmed by QP/level delta]"
                    : "Quirk purchase for " + controller.quirkName[best] + " produced no state transition");
        }

        private static int FindBestUpgrade(IList<string> names, IList<long> levels, IList<long> caps, IList<float> effects,
            Func<int, long> cost, long budget, IList<difficulty> requirements, difficulty currentDifficulty,
            Func<int, bool> allowed)
        {
            var best = -1;
            var bestScore = double.MaxValue;
            for (var i = 0; i < names.Count && i < levels.Count && i < caps.Count && i < effects.Count
                            && i < requirements.Count; i++)
            {
                if (allowed != null && !allowed(i)) continue;
                // Native capLevel interprets serialized maxLevel=0 as unlimited.
                var cap = caps[i] == 0 ? long.MaxValue : caps[i];
                if (levels[i] >= cap || requirements[i] > currentDifficulty) continue;
                var price = cost(i);
                if (price <= 0 || price > budget) continue;
                var name = (names[i] ?? string.Empty).ToLowerInvariant();
                var rank = UpgradeKeywords.Length + 2;
                for (var keyword = 0; keyword < UpgradeKeywords.Length; keyword++)
                {
                    if (!name.Contains(UpgradeKeywords[keyword])) continue;
                    rank = keyword;
                    break;
                }
                // Within a progression family, buy the largest exact serialized
                // effect per point rather than merely the cheapest button.
                var marginal = Math.Max(1e-12, Math.Abs(effects[i]));
                var score = rank * 1e18 + price / marginal;
                if (score >= bestScore) continue;
                bestScore = score;
                best = i;
            }
            return best;
        }

        private static void AddCandidate(ICollection<PurchaseCandidate> list, object controller, string name, double score,
            int power, int cap, int bars)
        {
            var oldPower = GetInputText(controller, "powerInput");
            var oldCap = GetInputText(controller, "capInput");
            var oldBars = GetInputText(controller, "barInput");
            try
            {
                SetPurchaseRatio(controller, power, cap, bars);
                var costMethod = controller.GetType().GetMethod("customAllCost", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (costMethod == null) return;
                var raw = costMethod.Invoke(controller, null);
                var cost = Convert.ToInt64(raw);
                list.Add(new PurchaseCandidate
                {
                    Controller = controller, Name = name,
                    // currentPower/difficultyWeight is the inverse first-order
                    // fractional power gain. Multiplying by exact package cost makes
                    // this cost per weighted permanent marginal—not merely whichever
                    // resource is currently smallest.
                    Score = Math.Max(1L, cost) * score, Cost = cost,
                    Power = power, Cap = cap, Bars = bars
                });
            }
            finally
            {
                SetInputText(controller, "powerInput", oldPower);
                SetInputText(controller, "capInput", oldCap);
                SetInputText(controller, "barInput", oldBars);
                InvokePurchaseInputUpdate(controller, "updateCustomPowerInput");
                InvokePurchaseInputUpdate(controller, "updateCustomCapInput");
                InvokePurchaseInputUpdate(controller, "updateCustomBarInput");
            }
        }

        private static string GetInputText(object controller, string fieldName)
        {
            var field = controller.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var input = field == null ? null : field.GetValue(controller) as InputField;
            return input == null ? string.Empty : input.text;
        }

        private static void SetInputText(object controller, string fieldName, string value)
        {
            var field = controller.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var input = field == null ? null : field.GetValue(controller) as InputField;
            if (input != null) input.text = value ?? string.Empty;
        }

        private static void SetPurchaseRatio(object controller, int power, int cap, int bars)
        {
            SetInput(controller, "powerInput", power);
            SetInput(controller, "capInput", cap);
            SetInput(controller, "barInput", bars);
            InvokePurchaseInputUpdate(controller, "updateCustomPowerInput");
            InvokePurchaseInputUpdate(controller, "updateCustomCapInput");
            InvokePurchaseInputUpdate(controller, "updateCustomBarInput");
        }

        private static void InvokePurchaseInputUpdate(object controller, string methodName)
        {
            var method = controller.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method != null)
                method.Invoke(controller, null);
        }

        private static void SetInput(object controller, string fieldName, int value)
        {
            var field = controller.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var input = field == null ? null : field.GetValue(controller) as InputField;
            if (input != null) input.text = value.ToString();
        }

        private sealed class PurchaseCandidate
        {
            internal object Controller;
            internal string Name;
            internal double Score;
            internal long Cost;
            internal int Power;
            internal int Cap;
            internal int Bars;
        }

        private sealed class PermanentExpTarget
        {
            internal readonly object Controller;
            internal readonly string Method;
            internal readonly string Label;
            internal readonly long Cost;
            internal readonly Func<double> State;
            internal readonly string Reason;

            internal PermanentExpTarget(object controller, string method, string label,
                long cost, Func<double> state, string reason)
            {
                Controller = controller;
                Method = method;
                Label = label;
                Cost = cost;
                State = state;
                Reason = reason;
            }
        }
    }
}
