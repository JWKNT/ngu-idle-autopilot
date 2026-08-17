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
executes verified purchases and spells, separates persistent from reset-local spending, emits
decision.json, and records sparse verified progression events for the read-only monitor. Irreversible
actions require full mode plus a confirmed post-state delta. New mechanics should expose focused
managers instead of duplicating authority.
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
        private bool[] _lastObservedItemDropped;
        private bool[] _lastObservedItemMaxxed;
        private int[] _lastObservedTitanKills;
        private long[] _lastObservedTrainingMilestones;
        private bool[] _lastObservedCombatAbilityUnlocks;
        private long[] _lastObservedAugmentMilestones;

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
            ObserveKeyEvents(Main.Character);

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
                if (!BuyGateExpUpgrade() && !BuyAtomicExpUpgrade()
                    && !BuyStrategicPermanentExpUpgrade() && !BuyMagicSpeedBreakpoint()
                    && !BuyBestYggPermanent() && !BuyQolExpUpgrade())
                    BuyBestMarginalExpUpgrade();
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

        /*
        SPARSE KEY-EVENT OBSERVER

        The full action log intentionally records high-frequency control work. The monitor's Key
        Events tab instead needs transitions that remain meaningful hours later. Snapshot native
        persistent/counter fields here and log only confirmed deltas: Titan kill counters, first
        Item List discovery/MAXX flags, and the highest-place-value level boundary (for example
        200,000 rather than 123,456). The first observation is a silent baseline so injection never
        invents historical events. Counter/level decreases after rebirth reset the baseline without
        producing a false milestone.
        */
        private void ObserveKeyEvents(Character c)
        {
            if (c == null || c.adventure == null || c.inventory == null
                || c.inventory.itemList == null)
                return;

            ObserveTitanKills(c);
            ObserveItemListTransitions(c);
            ObserveLevelMilestones(c);
        }

        private void ObserveTitanKills(Character c)
        {
            var current = new[]
            {
                c.adventure.titan1Kills, c.adventure.titan2Kills, c.adventure.titan3Kills,
                c.adventure.titan4Kills, c.adventure.titan5Kills, c.adventure.titan6Kills,
                c.adventure.titan7Kills, c.adventure.titan8Kills, c.adventure.titan9Kills,
                c.adventure.titan10Kills, c.adventure.titan11Kills, c.adventure.titan12Kills
            };
            if (_lastObservedTitanKills == null)
            {
                _lastObservedTitanKills = current;
                return;
            }
            for (var i = 0; i < current.Length; i++)
            {
                if (current[i] > _lastObservedTitanKills[i])
                    Main.LogAction("TITAN", "Defeated " + GameNames.Titan(c, i) + " — native kill count "
                                            + current[i] + " [confirmed by Titan counter delta]");
            }
            _lastObservedTitanKills = current;
        }

        private void ObserveItemListTransitions(Character c)
        {
            var list = c.inventory.itemList;
            var dropped = list.itemDropped;
            var maxxed = list.itemMaxxed;
            if (dropped == null || maxxed == null)
                return;
            var count = Math.Min(dropped.Count, maxxed.Count);
            if (_lastObservedItemDropped == null || _lastObservedItemDropped.Length != count)
            {
                _lastObservedItemDropped = Enumerable.Range(0, count).Select(i => dropped[i]).ToArray();
                _lastObservedItemMaxxed = Enumerable.Range(0, count).Select(i => maxxed[i]).ToArray();
                return;
            }
            for (var id = 1; id < count; id++)
            {
                var becameMaxxed = maxxed[id] && !_lastObservedItemMaxxed[id];
                var firstDrop = dropped[id] && !_lastObservedItemDropped[id];
                if (becameMaxxed)
                    Main.LogAction("COLLECTION", "MAXXED " + SafeItemName(c, id)
                                                     + " (Item ID " + id + ") [confirmed by Item List flag]");
                else if (firstDrop)
                    Main.LogAction("DISCOVERY", "First obtained " + SafeItemName(c, id)
                                                    + " (Item ID " + id + ") [confirmed by Item List flag]");
                _lastObservedItemDropped[id] = dropped[id];
                _lastObservedItemMaxxed[id] = maxxed[id];
            }
        }

        private void ObserveLevelMilestones(Character c)
        {
            if (c.training != null && c.training.attackTraining != null
                && c.training.defenseTraining != null)
            {
                var current = new long[12];
                var abilityUnlocked = new bool[12];
                for (var i = 0; i < 6; i++)
                {
                    current[i] = GreatestPlaceMilestone(c.training.attackTraining[i]);
                    current[i + 6] = GreatestPlaceMilestone(c.training.defenseTraining[i]);
                    abilityUnlocked[i] = AdventureAbilityUnlocked(true, i, c.training.attackTraining[i]);
                    abilityUnlocked[i + 6] = AdventureAbilityUnlocked(false, i,
                        c.training.defenseTraining[i]);
                }

                /*
                TRAINING ROWS ARE NOT COMBAT-ABILITY UNLOCKS

                The native Basic Training entry is available before the Adventure move carrying
                the same label is available. For example, Parry training can advance while Parry
                itself remains locked until 15,000. Locked moves do not belong in the achievement
                ledger. Emit one compact unlock event at the actual native threshold, then use the
                game's native move name for later significant-place training milestones.
                */
                var newlyUnlocked = new bool[12];
                if (_lastObservedCombatAbilityUnlocks != null
                    && _lastObservedCombatAbilityUnlocks.Length == abilityUnlocked.Length)
                {
                    for (var i = 0; i < abilityUnlocked.Length; i++)
                    {
                        newlyUnlocked[i] = abilityUnlocked[i] && !_lastObservedCombatAbilityUnlocks[i];
                        if (!newlyUnlocked[i]) continue;
                        var attack = i < 6;
                        var row = attack ? i : i - 6;
                        var label = attack ? GameNames.AttackTraining(c, row)
                            : GameNames.DefenseTraining(c, row);
                        Main.LogAction("PROGRESSION", label + " unlocked");
                    }
                }
                if (_lastObservedTrainingMilestones != null)
                {
                    for (var i = 0; i < current.Length; i++)
                    {
                        if (current[i] <= _lastObservedTrainingMilestones[i] || current[i] <= 0) continue;
                        if (newlyUnlocked[i]) continue;
                        if (!abilityUnlocked[i]) continue;
                        var attack = i < 6;
                        var row = i < 6 ? i : i - 6;
                        var label = attack ? GameNames.AttackTraining(c, row)
                            : GameNames.DefenseTraining(c, row);
                        Main.LogAction("MILESTONE", label + " Lv " + current[i].ToString("N0"));
                    }
                }
                _lastObservedTrainingMilestones = current;
                _lastObservedCombatAbilityUnlocks = abilityUnlocked;
            }

            if (c.augments == null || c.augments.augs == null || c.augmentsController == null
                || c.augmentsController.augments == null)
                return;
            var tracks = Math.Min(c.augmentsController.augments.Length, c.augments.augs.Length);
            var augCurrent = new long[tracks * 2];
            for (var i = 0; i < tracks; i++)
            {
                augCurrent[2 * i] = GreatestPlaceMilestone(c.augments.augs[i].augLevel);
                augCurrent[2 * i + 1] = GreatestPlaceMilestone(c.augments.augs[i].upgradeLevel);
            }
            if (_lastObservedAugmentMilestones != null
                && _lastObservedAugmentMilestones.Length == augCurrent.Length)
            {
                for (var i = 0; i < augCurrent.Length; i++)
                {
                    if (augCurrent[i] <= _lastObservedAugmentMilestones[i] || augCurrent[i] <= 0) continue;
                    var pair = i / 2;
                    var upgrade = i % 2 != 0;
                    Main.LogAction("MILESTONE", GameNames.Augment(c, pair, upgrade) + " ("
                                                + (upgrade ? "Upgrade" : "Augment") + " row " + (pair + 1)
                                                + ") reached level "
                                                + augCurrent[i].ToString("N0")
                                                + " [confirmed at greatest-place-value boundary]");
                }
            }
            _lastObservedAugmentMilestones = augCurrent;
        }

        private static long GreatestPlaceMilestone(long level)
        {
            if (level <= 0) return 0;
            var place = 1L;
            while (place <= level / 10 && place <= long.MaxValue / 10)
                place *= 10;
            return level / place * place;
        }

        private static long AdventureAbilityUnlockLevel(bool attack, int row)
        {
            // Regular Attack, Idle Attack, and Block are available independently of their Basic
            // Training rows. The remaining native Adventure moves follow the row's exact
            // 5,000-level step: Strong Attack 10k, Parry 15k, Defensive Buff 5k, and so on.
            if ((attack && row == 0) || row == 5) return 0L;
            return 5000L * (row + 1L);
        }

        private static bool AdventureAbilityUnlocked(bool attack, int row, long level)
        {
            return level >= AdventureAbilityUnlockLevel(attack, row);
        }

        private static string SafeItemName(Character c, int id)
        {
            return GameNames.Item(c, id);
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
                label = "Blood MacGuffin β — all equipped MacGuffins";
            }
            else if (c.adventure.itopod.perkLevel.Count > 72 && c.adventure.itopod.perkLevel[72] >= 1
                     && c.bloodMagic.macguffin1Time.totalseconds >= c.bloodMagicController.spells.macguffin1Cooldown
                     && bloodBefore >= c.bloodSpells.minMacguffin1Blood())
            {
                c.bloodSpells.castMacguffin1Spell();
                label = "Blood MacGuffin α";
            }
            else
            {
                var remaining = Plan == null ? int.MaxValue
                    : Plan.RebirthSeconds - (int)c.rebirthTime.totalseconds;
                if (remaining <= 5)
                {
                    c.bloodSpells.castRebirthSpell(bloodBefore);
                    label = "Blood NUMBER Boost — reserved for the selected rebirth checkpoint";
                }
                else if (c.bloodMagic.adventureSpellTime.totalseconds >= c.bloodSpells.adventureSpellCooldown
                         && bloodBefore >= c.bloodSpells.minAdventureBlood()
                         && c.settings.rebirthDifficulty == difficulty.normal)
                {
                    c.bloodSpells.castAdventurePowerupSpell();
                    label = "Iron Pill — permanent Adventure stats";
                }
                else if (c.settings.pitUnlocked && bloodBefore >= c.bloodSpells.minGoldBlood())
                {
                    c.bloodSpells.castGoldSpell(bloodBefore);
                    label = "Counterfeit Gold";
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
                                                         + " in " + GameNames.Zone(Main.Character, deathNoteZone));
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
                    Main.LogAction("ADVENTURE", "Prioritizing active Titan window in "
                                                   + GameNames.Zone(Main.Character, titanZone));
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
                    "Routing to " + GameNames.Zone(Main.Character, best.Zone)
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
            const int bossRawProjectionHorizon = 604800;
            var bossFitEta = NextBossViabilityEta(c, Plan.RebirthSeconds);
            // Preserve a raw selected-boss estimate even when it does not fit the
            // chosen reset.  The separate fit/slack fields prevent that estimate
            // from being mistaken for an action the current run will actually take.
            var bossViabilityEta = bossFitEta >= 0 ? bossFitEta
                : RawSelectedBossDefeatEta(c, bossRawProjectionHorizon);
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
            var bossEtaState = c.bossController == null ? "controller-unavailable"
                : bossFighting && bossKillEta >= 0 ? "active-fight"
                : bossViabilityEta >= 0 ? "finite"
                : "outside-seven-day-current-allocation-model";
            var energyIncome = Math.Max(0.0, c.energyPerSecond());
            var magicIncome = Math.Max(0.0, c.magicPerSecond());
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
            var recoveryResetEta = -1;
            var recoveryContinueEta = -1;
            var recoveryRouteReason = string.Empty;
            var recoveryMode = c.settings.rebirthDifficulty == difficulty.normal && c.bossID < c.highestBoss;
            var recoveryResetEfficient = !recoveryMode
                || RebirthOptimizer.RecoveryResetEfficient(c, bossViabilityEta,
                    out recoveryResetEta, out recoveryContinueEta, out recoveryRouteReason);
            if (!recoveryMode)
            {
                recoveryResetEta = -1;
                recoveryContinueEta = -1;
                recoveryRouteReason = "boss record already caught up; normal checkpoint objective applies";
            }
            var rebirthSafetyBlockReason = !Config.AllowRebirths
                ? "rebirth execution is disabled in autopilot settings"
                : !rebirthPreviewMonotonic
                    ? "native next-Number preview would lower Attack or Defense multiplier"
                    : !recoveryResetEfficient
                        ? recoveryRouteReason
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
            var adventureTargetName = adventureTargetZone >= 0
                ? GameNames.Zone(c, adventureTargetZone) : "Not yet selected";
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
            var deferredExpPermanent = GetStrategicPermanentExpTarget(c);
            var expQolPolicy = Config.ManageInventory && Config.ManageAllocations
                ? "deferred: Basic Loot Filter, Auto Merge, Inventory Merge Slot, loadouts, custom buttons, and Auto Advance duplicate active bot controllers"
                : "eligible only for the disabled matching bot subsystem and only below 0.5% of lifetime EXP";
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
                       + "  \"rebirthOptimizerModel\": \"exact-time-and-boss-array-recovery-route-v2\",\n"
                       + "  \"rebirthObjective\": \"minimize modeled wall-clock time to the persistent boss record while preserving strict Number growth; maximize compounded log growth after catch-up\",\n"
                       + "  \"rebirthSelectedScorePerHour\": " + Plan.RebirthSelectedScorePerHour.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthRunnerUpScorePerHour\": " + Plan.RebirthRunnerUpScorePerHour.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthOptimizerProjectedMultiplier\": " + Plan.RebirthProjectedMultiplier.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthOptimizerProjectedAp\": " + Plan.RebirthProjectedAP + ",\n"
                       + "  \"rebirthOptimizerRecordRecoveryEtaSeconds\": " + Plan.RebirthRecoveryEtaSeconds + ",\n"
                       + "  \"rebirthOptimizerRecoveryRemainingBosses\": " + Plan.RebirthRecoveryRemainingBosses + ",\n"
                       + "  \"rebirthOptimizerRecoveryReason\": \"" + EscapeJson(Plan.RebirthRecoveryReason) + "\",\n"
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
                       + "  \"rebirthRecoveryMode\": " + recoveryMode.ToString().ToLowerInvariant() + ",\n"
                       + "  \"rebirthRecoveryResetEfficient\": " + recoveryResetEfficient.ToString().ToLowerInvariant() + ",\n"
                       + "  \"rebirthRecoveryResetRouteEtaSeconds\": " + recoveryResetEta + ",\n"
                       + "  \"rebirthRecoveryContinueRouteEtaSeconds\": " + recoveryContinueEta + ",\n"
                       + "  \"rebirthRecoveryRemainingBosses\": " + Math.Max(0, activeHighestBoss - c.bossID) + ",\n"
                       + "  \"rebirthRecoveryReason\": \"" + EscapeJson(recoveryRouteReason) + "\",\n"
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
                       + "  \"bossEtaState\": \"" + bossEtaState + "\",\n"
                       + "  \"bossEtaProjectionHorizonSeconds\": " + bossRawProjectionHorizon + ",\n"
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
                       + "  \"energyBasePower\": " + c.energyPower.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"energyBaseCap\": " + c.capEnergy + ",\n"
                       + "  \"energyBaseBars\": " + c.energyBars + ",\n"
                       + "  \"energyIncomePerSecond\": " + energyIncome.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"energySweepBound\": " + energySweepBound + ",\n"
                       + "  \"energyIdleReason\": \"" + energyIdleReason + "\",\n"
                       + "  \"basicTrainingLongHorizonPolicy\": \"reserve Energy first for reachable maximum cap-reduction frontiers with at most a two-future-run Energy-cap payback; then optimize immediate boss marginal value\",\n"
                       + "  \"timeMachineHorizonDecision\": \"" + EscapeJson(AllocationProfiles.BreakpointTypes.TimeMachineBP.LastHorizonDecision) + "\",\n"
                       + "  \"energyAllocationBreakdown\": " + energyBreakdown + ",\n"
                       + "  \"energyBasicTrainingAllocated\": " + basicTrainingEnergy + ",\n"
                       + "  \"energyNonBasicTrainingAllocated\": " + nonBasicTrainingEnergy + ",\n"
                       + "  \"magicCurrent\": " + c.magic.curMagic + ",\n"
                       + "  \"magicIdle\": " + c.magic.idleMagic + ",\n"
                       + "  \"magicAllocated\": " + Math.Max(0L, c.magic.curMagic - c.magic.idleMagic) + ",\n"
                       + "  \"magicIncomePerSecond\": " + magicIncome.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"magicBaseSpeed\": " + c.magic.magicBarSpeed.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"magicBasePower\": " + c.magic.magicPower.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"magicBaseCap\": " + c.magic.capMagic + ",\n"
                       + "  \"magicBaseBars\": " + c.magic.magicPerBar + ",\n"
                       + "  \"magicTimeMachineAllocated\": " + c.machine.goldMultiMagic + ",\n"
                       + "  \"magicBloodAllocated\": " + (c.bloodMagic == null || c.bloodMagic.ritual == null ? 0L : c.bloodMagic.ritual.Sum(x => Math.Max(0L, x.magic))) + ",\n"
                       + "  \"magicWandoosAllocated\": " + c.wandoos98.wandoosMagic + ",\n"
                       + "  \"magicAllocationDecision\": \"" + EscapeJson(CustomAllocation.LastMagicAllocationDecision) + "\",\n"
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
                       + "  \"expTargetName\": \"" + EscapeJson(expStatus.TargetName) + "\",\n"
                       + "  \"expState\": \"" + expStatus.State + "\",\n"
                       + "  \"expTarget\": " + expStatus.Target + ",\n"
                       + "  \"expTargetCost\": " + expStatus.TargetCost + ",\n"
                       + "  \"expShortfall\": " + expStatus.Shortfall + ",\n"
                       + "  \"expIncomePerSecond\": " + expStatus.IncomePerSecond.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"expEtaSeconds\": " + expStatus.EtaSeconds + ",\n"
                       + "  \"expPolicyModel\": \"exact progression gate; Energy speed; admitted permanent systems; discrete Magic speed; Ygg permanents; fallback QoL; stage-weighted P/C/B\",\n"
                       + "  \"expQolPolicy\": \"" + EscapeJson(expQolPolicy) + "\",\n"
                       + "  \"expDeferredPermanentTarget\": \"" + EscapeJson(deferredExpPermanent == null ? "none admitted" : deferredExpPermanent.Label) + "\",\n"
                       + "  \"expDeferredPermanentCost\": " + (deferredExpPermanent == null ? 0L : deferredExpPermanent.Cost) + ",\n"
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
            internal string TargetName = string.Empty;
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
            // Always price the next concrete purchase, even at zero spendable EXP.
            // Returning only the reserve here made telemetry lose the purchase name
            // and cost precisely while the player was waiting for the next reward.
            var gate = GetGateExpTarget(c);
            if (gate != null)
                return ExpTargetStatus(c, gate, "progression gate");
            if (c.energySpeed < 49.91f)
            {
                var speedCost = !c.settings.special1Bought ? 1
                    : !c.settings.special2Bought ? 2
                    : !c.settings.special3Bought ? 3
                    : c.energyPurchases.energySpeed10Cost();
                return new ResourceStatus
                {
                    TargetName = "Energy Speed",
                    Decision = speedCost <= c.realExp - Config.ExpReserve
                        ? "Buying the highest-return unowned Energy-speed step toward the effective 50 cap"
                        : "Saving EXP for the next highest-return Energy-speed step toward 50",
                    Target = speedCost + Config.ExpReserve,
                    EtaSeconds = ResourceEta(c.realExp, speedCost + Config.ExpReserve, _expPerSecond)
                };
            }
            var permanent = GetStrategicPermanentExpTarget(c);
            if (c.highestBoss < 17)
            {
                if (permanent != null && ShouldReserveForPermanentExpTarget(c, permanent))
                    return new ResourceStatus
                    {
                        TargetName = permanent.Label,
                        Decision = permanent.Cost <= c.realExp - Config.ExpReserve
                            ? "Buying " + permanent.Label + " on this decision cycle: " + permanent.Reason
                            : "Saving EXP for " + permanent.Label + ": " + permanent.Reason,
                        Target = permanent.Cost + Config.ExpReserve,
                        EtaSeconds = ResourceEta(c.realExp, permanent.Cost + Config.ExpReserve, _expPerSecond)
                    };
                // Fixed Energy Power/Bar atoms remain legal before the Boss 17
                // custom-input unlock, so fall through to the marginal selector.
            }
            if (permanent != null && ShouldReserveForPermanentExpTarget(c, permanent))
                return new ResourceStatus
                {
                    TargetName = permanent.Label,
                    Decision = permanent.Cost <= c.realExp - Config.ExpReserve
                        ? "Buying " + permanent.Label + " on this decision cycle: " + permanent.Reason
                        : "Saving EXP for " + permanent.Label + ": " + permanent.Reason,
                    Target = permanent.Cost + Config.ExpReserve,
                    EtaSeconds = ResourceEta(c.realExp, permanent.Cost + Config.ExpReserve, _expPerSecond)
                };
            int magicSpeedSteps;
            double magicRateAfter;
            if (TryGetMagicSpeedBreakpoint(c, out magicSpeedSteps, out magicRateAfter))
            {
                var cost = 3L * magicSpeedSteps;
                return new ResourceStatus
                {
                    TargetName = "Magic Speed breakpoint",
                    Decision = cost <= c.realExp - Config.ExpReserve
                        ? "Buying the next productive Magic Speed breakpoint now: "
                          + c.magicPerSecond().ToString("0.###") + " -> " + magicRateAfter.ToString("0.###") + "/s"
                        : "Saving briefly for the next productive Magic Speed breakpoint: "
                          + c.magicPerSecond().ToString("0.###") + " -> " + magicRateAfter.ToString("0.###") + "/s",
                    Target = cost + Config.ExpReserve,
                    EtaSeconds = ResourceEta(c.realExp, cost + Config.ExpReserve, _expPerSecond)
                };
            }
            var qol = GetQolExpTarget(c);
            if (qol != null && ShouldReserveForPermanentExpTarget(c, qol))
                return ExpTargetStatus(c, qol, "fallback QoL");
            var preferred = BestMarginalExpCandidate(c);
            if (preferred == null)
                return new ResourceStatus {TargetName = "next unlocked EXP purchase", Decision = "Held because no unlocked EXP purchase passed game-state validation", Target = 0, EtaSeconds = -1};
            return new ResourceStatus
            {
                TargetName = preferred.Label,
                Decision = preferred.Cost <= c.realExp - Config.ExpReserve
                    ? "Buying " + preferred.Label + " now: " + preferred.Reason
                    : "Saving briefly for " + preferred.Label + ": " + preferred.Reason,
                Target = preferred.Cost + Config.ExpReserve,
                EtaSeconds = ResourceEta(c.realExp, preferred.Cost + Config.ExpReserve, _expPerSecond)
            };
        }

        private ResourceStatus ExpTargetStatus(Character c, PermanentExpTarget target, string category)
        {
            var funded = target.Cost <= c.realExp - Config.ExpReserve;
            return new ResourceStatus
            {
                TargetName = target.Label,
                Decision = (funded ? "Buying " : "Saving EXP for ") + target.Label
                           + (funded ? " now" : string.Empty) + " [" + category + "]: " + target.Reason,
                Target = target.Cost + Config.ExpReserve,
                EtaSeconds = ResourceEta(c.realExp, target.Cost + Config.ExpReserve, _expPerSecond)
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
            var label = NativeApPurchaseName(c, id);
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
                if (state.augEnergy > 0)
                {
                    var eta = controller.getAugProgressPerTick(state.augEnergy) > 0
                        ? (int)Math.Ceiling(controller.AugTimeLeftEnergy(state.augEnergy))
                        : -1;
                    return new AugmentStatus
                    {
                        Decision = "Installing " + GameNames.Augment(c, i, false)
                                   + " level " + (state.augLevel + 1),
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
                        Decision = "Installing " + GameNames.Augment(c, i, true)
                                   + " level " + (state.upgradeLevel + 1),
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

        private static string NextTitanName(Character c)
        {
            var items = c.inventory.itemList;
            if (!items.GRBComplete) return GameNames.Titan(c, 0);
            if (!items.seedComplete) return GameNames.Titan(c, 1);
            if (!items.jakeComplete) return GameNames.Titan(c, 2);
            if (!items.uugComplete) return GameNames.Titan(c, 3);
            if (!items.waldoComplete) return GameNames.Titan(c, 4);
            if (!items.beast1complete) return GameNames.Titan(c, 5);
            if (!items.nerdComplete) return GameNames.Titan(c, 6);
            if (!items.godmotherComplete) return GameNames.Titan(c, 7);
            if (!items.exileComplete) return GameNames.Titan(c, 8);
            if (!items.spaceComplete) return GameNames.Titan(c, 9);
            if (!items.rockLobsterComplete) return GameNames.Titan(c, 10);
            if (!items.amalgamateComplete) return GameNames.Titan(c, 11);
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
            var activeKillEta = CurrentBossKillEta(c);
            if (c.bossController != null && c.bossController.isFighting)
                return activeKillEta >= 0 && activeKillEta <= immediateHorizon ? activeKillEta : -1;
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

        /*
        RAW BOSS ETA

        The rebirth-fit search is intentionally finite and may correctly reject a boss that cannot
        die before this run's checkpoint. The monitor still needs a bounded raw forecast, not an
        eternal "calculating" placeholder. Under the frozen-current-allocation model, projected
        training and Augment multipliers are non-decreasing, so an exponential bracket followed by
        integer binary search finds the first viable start in O(log horizon) projections. Seven days
        is a hard reporting horizon; failure is emitted explicitly as outside-model, never pending.
        */
        private static int RawSelectedBossDefeatEta(Character c, int horizonSeconds)
        {
            if (c == null || c.bossController == null || horizonSeconds <= 0)
                return -1;
            var activeKillEta = CurrentBossKillEta(c);
            if (c.bossController.isFighting)
                return activeKillEta >= 0 && activeKillEta <= horizonSeconds ? activeKillEta : -1;
            if (CombatHelpers.CanNukeCurrentBoss(c))
                return 1;

            double killSeconds;
            if (ProjectedBossWin(c, 0, out killSeconds) && killSeconds <= 120.0)
                return (int)Math.Ceiling(killSeconds);

            var previous = 0;
            var upper = -1;
            for (var wait = 1; wait < horizonSeconds; wait = wait > horizonSeconds / 2
                     ? horizonSeconds - 1 : wait * 2)
            {
                if (ProjectedBossWin(c, wait, out killSeconds) && killSeconds <= 120.0)
                {
                    upper = wait;
                    break;
                }
                previous = wait;
                if (wait == horizonSeconds - 1) break;
            }
            if (upper < 0)
                return -1;

            var lower = previous + 1;
            while (lower < upper)
            {
                var middle = lower + (upper - lower) / 2;
                if (ProjectedBossWin(c, middle, out killSeconds) && killSeconds <= 120.0)
                    upper = middle;
                else
                    lower = middle + 1;
            }
            if (!ProjectedBossWin(c, lower, out killSeconds) || killSeconds > 120.0
                || lower + killSeconds > horizonSeconds)
                return -1;
            return (int)Math.Ceiling(lower + killSeconds);
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
                rows.Add("{\"pair\":\"" + EscapeJson(GameNames.AttackTraining(c, i) + " + "
                         + GameNames.DefenseTraining(c, i))
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
                    var attackGoal = "Unlock " + GameNames.AttackTraining(c, i);
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
                    var defenseGoal = "Unlock " + GameNames.DefenseTraining(c, i);
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

        private void BuyBestMarginalExpUpgrade()
        {
            var c = Main.Character;
            if (c.realExp <= Config.ExpReserve)
                return;

            var best = BestMarginalExpCandidate(c);
            if (best == null || best.Cost > c.realExp - Config.ExpReserve)
                return;

            var oldPower = GetInputText(best.Controller, "powerInput");
            var oldCap = GetInputText(best.Controller, "capInput");
            var oldBars = GetInputText(best.Controller, "barInput");
            if (best.UsesCustomInput)
                SetPurchaseRatio(best.Controller, best.Power, best.Cap, best.Bars);
            var method = best.Controller.GetType().GetMethod(best.Method,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                return;
            var expBefore = c.realExp;
            var statBefore = best.ReadValue();
            try
            {
                method.Invoke(best.Controller, null);
            }
            finally
            {
                if (best.UsesCustomInput)
                {
                    SetInputText(best.Controller, "powerInput", oldPower);
                    SetInputText(best.Controller, "capInput", oldCap);
                    SetInputText(best.Controller, "barInput", oldBars);
                    InvokePurchaseInputUpdate(best.Controller, "updateCustomPowerInput");
                    InvokePurchaseInputUpdate(best.Controller, "updateCustomCapInput");
                    InvokePurchaseInputUpdate(best.Controller, "updateCustomBarInput");
                }
            }
            var spent = expBefore - c.realExp;
            var statAfter = best.ReadValue();
            var confirmed = spent == best.Cost && statAfter > statBefore;
            Main.LogAction(confirmed ? "PURCHASE" : "REJECTED",
                confirmed
                    ? "Bought " + best.Label + " for " + spent
                      + " EXP [confirmed by exact EXP and permanent-stat deltas]; " + best.Reason
                    : best.Label + " purchase failed validation: spent=" + spent
                      + ", stat " + statBefore.ToString("0.###") + " -> " + statAfter.ToString("0.###"));
        }

        private static MarginalExpCandidate BestMarginalExpCandidate(Character c)
        {
            if (c == null || c.energyPurchases == null)
                return null;

            /*
             * EXP RESOURCE POLICY
             *
             * Native customAllCost is exactly powerCost + capCost + barCost; there
             * is no bundle discount.  Consequently a partially funded ratio bundle
             * is weakly dominated by buying its useful atoms as soon as they are
             * affordable.  We keep P/C/B near the stage-appropriate long-horizon
             * ratio, but execute only the currently lagging dimension.  This gives
             * the player its permanent benefit immediately and re-evaluates after
             * every purchase instead of waiting for an arbitrary round package.
             */
            var earlyNormal = c.settings.rebirthDifficulty == difficulty.normal && c.highestBoss < 58;
            var ratioPower = earlyNormal ? 1.0 : c.settings.rebirthDifficulty == difficulty.normal ? 5.0 : 4.0;
            var ratioCap = earlyNormal ? 37500.0 : c.settings.rebirthDifficulty == difficulty.normal ? 160000.0 : 150000.0;
            var ratioBars = earlyNormal ? 1.0 : c.settings.rebirthDifficulty == difficulty.normal ? 4.0 : 1.0;

            // Early Normal is overwhelmingly Energy constrained.  Magic becomes a
            // candidate only after the first-Titan progression region; R3 only when
            // the game has actually enabled it.  Later resource shares retain the
            // existing 3:1-ish Energy preference through their normalized power.
            object controller = c.energyPurchases;
            var resource = "Energy";
            var basePower = (double)c.energyPower;
            var baseCap = (double)c.capEnergy;
            var baseBars = (double)c.energyBars;
            Func<double> readPower = () => c.energyPower;
            Func<double> readCap = () => c.capEnergy;
            Func<double> readBars = () => c.energyBars;
            var costScale = 1L;
            if (!earlyNormal && c.highestBoss >= 37 && c.magicPurchases != null
                && c.magic.magicPower < c.energyPower / 3.0f)
            {
                controller = c.magicPurchases;
                resource = "Magic";
                basePower = c.magic.magicPower;
                baseCap = c.magic.capMagic;
                baseBars = c.magic.magicPerBar;
                readPower = () => c.magic.magicPower;
                readCap = () => c.magic.capMagic;
                readBars = () => c.magic.magicPerBar;
                costScale = 3L;
            }
            if (c.res3.res3On && c.settings.rebirthDifficulty != difficulty.normal
                && c.res3Purchases != null && c.res3.res3Power < basePower / 2.0)
            {
                controller = c.res3Purchases;
                resource = "Resource 3";
                basePower = c.res3.res3Power;
                baseCap = c.res3.capRes3;
                baseBars = c.res3.res3PerBar;
                readPower = () => c.res3.res3Power;
                readCap = () => c.res3.capRes3;
                readBars = () => c.res3.res3PerBar;
                costScale = 100000L;
            }

            var candidates = new List<MarginalExpCandidate>();
            if (resource == "Energy")
            {
                candidates.Add(new MarginalExpCandidate(controller, resource + " Power +0.1",
                    "buyEnergyPower01", 15, readPower, basePower / ratioPower,
                    "Power is the lagging balanced-growth dimension and accelerates every power-sensitive Energy system",
                    false, 0, 0, 0, .1 / ratioPower));
            }
            else
            {
                candidates.Add(new MarginalExpCandidate(controller, resource + " Power +1",
                    "buyCustomPower", 150L * costScale, readPower, basePower / ratioPower,
                    "Power is the lagging balanced-growth dimension and accelerates this resource's power-sensitive systems",
                    true, 1, 0, 0, 1.0 / ratioPower));
            }

            // Native cost is linear at one Energy EXP per 250 cap, but the custom
            // input validator enforces a 10,000-cap minimum. Therefore 40 EXP is the
            // smallest executable Energy cap purchase (scaled for Magic/R3); a
            // theoretical +250 atom would be rejected by the controller.
            if (c.highestBoss >= 17 && baseCap >= 100000)
                candidates.Add(new MarginalExpCandidate(controller, resource + " Cap +10,000",
                    "buyCustomCap", 40L * costScale, readCap, baseCap / ratioCap,
                    c.idleEnergy <= 0
                        ? "all generated Energy is productive, so permanent allocation headroom is the current cap bottleneck"
                        : "cap is the lagging long-horizon P/C/B dimension",
                    true, 0, 10000, 0, 10000.0 / ratioCap));
            var barMethod = resource == "Energy" ? "buyEnergyBar1" : "buyCustomBar";
            var barUsesCustomInput = resource != "Energy";
            candidates.Add(new MarginalExpCandidate(controller, resource + " Bar +1",
                barMethod, 80L * costScale, readBars, baseBars / ratioBars,
                "bars are the lagging P/C/B dimension and permanently shorten resource refill time in this and future rebirths",
                barUsesCustomInput, 0, 0, 1, 1.0 / ratioBars));

            // First lift the smallest normalized P/C/B dimension.  At exact ties,
            // prefer the atom that advances one normalized ratio unit most cheaply.
            return candidates.Where(x => x.Cost > 0)
                .OrderBy(x => x.NormalizedLevel)
                .ThenBy(x => x.Cost / Math.Max(1e-12, x.NormalizedStep))
                .FirstOrDefault();
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

        private bool BuyGateExpUpgrade()
        {
            var target = GetGateExpTarget(Main.Character);
            if (target == null)
                return false;
            if (target.Cost > Main.Character.realExp - Config.ExpReserve)
                return true;
            return BuyExpTarget(target, "progression-gate");
        }

        private PermanentExpTarget GetGateExpTarget(Character c)
        {
            if (c == null || c.adventurePurchases == null)
                return null;

            // Inventory space is not cosmetic: with two or fewer free slots, one
            // multi-drop kill can lose an un-MAXXED item before the next merge/trash
            // sweep. Buy the native slot before any throughput stat in that state.
            var freeSlots = AdventureCollectionPlanner.FreeInventorySlots(c);
            if (freeSlots <= 2)
            {
                var costMethod = c.adventurePurchases.GetType().GetMethod("invSpaceCost",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var cost = costMethod == null ? 0L : Convert.ToInt64(costMethod.Invoke(c.adventurePurchases, null));
                if (cost > 0)
                    return new PermanentExpTarget(c.adventurePurchases, "buyInventorySpace",
                        "Inventory Space +1", cost, () => c.inventory.spaces,
                        "only " + freeSlots + " slots remain, so another slot prevents otherwise-valid loot from being dropped");
            }

            // An Adventure atom wins only when that exact atom crosses the next
            // configured zone threshold. Broad speculative Adventure-stat buying is
            // intentionally rejected in favor of compounding resource generation.
            var adventureAtom = EarlyAdventureAtomIndex(c);
            if (adventureAtom == 0)
                return new PermanentExpTarget(c.adventurePurchases, "buy1Attack",
                    "Adventure Power +1", 3, () => c.adventure.attack,
                    "this exact atom opens the next otherwise-unfightable Adventure zone");
            if (adventureAtom == 1)
                return new PermanentExpTarget(c.adventurePurchases, "buy1Defense",
                    "Adventure Toughness +1", 3, () => c.adventure.defense,
                    "this exact atom opens the next otherwise-unfightable Adventure zone");

            // Regen is a throughput stat only while recovery is actually delaying
            // Adventure. Compare the measured current recovery interval with the
            // modeled interval after +1 regen, and include the EXP acquisition wait.
            if (c.adventure.zone == -1 && _adventureRecoveryEtaSeconds >= 60)
            {
                var regen = Math.Max(.001, c.totalAdvHPRegen());
                var projectedEta = _adventureRecoveryEtaSeconds * regen / (regen + 1.0);
                var secondsSaved = _adventureRecoveryEtaSeconds - projectedEta;
                var available = Math.Max(0L, c.realExp - Config.ExpReserve);
                var fundingWait = available >= 50 ? 0.0
                    : _expPerSecond > 0 ? (50.0 - available) / _expPerSecond : double.PositiveInfinity;
                if (secondsSaved >= 15.0 && secondsSaved > fundingWait
                    && 50 <= Math.Max(1.0, c.stats.totalExp) * .02)
                    return new PermanentExpTarget(c.adventurePurchases, "buy1HPRegen",
                        "Adventure HP Regen +1", 50, () => c.adventure.regen,
                        "measured Safe-Zone recovery falls from about " + _adventureRecoveryEtaSeconds
                        + "s to " + Math.Ceiling(projectedEta) + "s, repaying faster than its EXP funding delay");
            }

            // Fight-Boss percentage stats are normally worse than resource growth.
            // Promote one only if the native discrete combat model changes from a
            // loss to a <=120-second win immediately, including death-before-hit.
            if (c.statBoostPurchases != null && c.bossController != null
                && !c.bossController.isFighting && !c.bossController.nukeBoss)
            {
                double currentKill;
                var currentlyViable = CombatHelpers.CanWinCurrentBoss(c, out currentKill);
                if (!currentlyViable)
                {
                    var attackRatio = (Math.Max(.0001, c.attackBoost) + .1) / Math.Max(.0001, c.attackBoost);
                    var defenseRatio = (Math.Max(.0001, c.defenseBoost) + .1) / Math.Max(.0001, c.defenseBoost);
                    double attackKill;
                    double attackSurvival;
                    var boostedAttack = c.attack * attackRatio;
                    var attackWins = CombatHelpers.EvaluateFixedBossFight(c, boostedAttack, c.defense,
                        Math.Max(c.curHP, 10.0 + boostedAttack * 10.0), c.bossCurHP,
                        out attackKill, out attackSurvival) && attackKill <= 120.0;
                    double defenseKill;
                    double defenseSurvival;
                    var boostedDefense = c.defense * defenseRatio;
                    var defenseWins = CombatHelpers.EvaluateFixedBossFight(c, c.attack, boostedDefense,
                        Math.Max(c.curHP, c.maxHP), c.bossCurHP,
                        out defenseKill, out defenseSurvival) && defenseKill <= 120.0;
                    if (attackWins && (!defenseWins || attackKill <= defenseKill))
                        return new PermanentExpTarget(c.statBoostPurchases, "buyAttack10",
                            "Fight Boss Attack +10%", 30, () => c.attackBoost,
                            "the discrete combat projection changes from a loss to a "
                            + Math.Ceiling(attackKill) + "s win against selected Boss " + (c.bossID + 1));
                    if (defenseWins)
                        return new PermanentExpTarget(c.statBoostPurchases, "buyDefense10",
                            "Fight Boss Defense +10%", 30, () => c.defenseBoost,
                            "the discrete combat projection changes from a loss to a "
                            + Math.Ceiling(defenseKill) + "s win against selected Boss " + (c.bossID + 1));
                }
            }
            return null;
        }

        private bool BuyQolExpUpgrade()
        {
            var target = GetQolExpTarget(Main.Character);
            if (target == null)
                return false;
            if (target.Cost > Main.Character.realExp - Config.ExpReserve)
                return ShouldReserveForPermanentExpTarget(Main.Character, target);
            return BuyExpTarget(target, "fallback-qol");
        }

        private bool BuyMagicSpeedBreakpoint()
        {
            var c = Main.Character;
            int steps;
            double projectedRate;
            if (!TryGetMagicSpeedBreakpoint(c, out steps, out projectedRate))
                return false;
            var cost = 3L * steps;
            if (cost > c.realExp - Config.ExpReserve)
                return true;
            var method = c.magicPurchases.GetType().GetMethod("buy10MagicSpeed",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                return false;
            var expBefore = c.realExp;
            var speedBefore = c.magic.magicBarSpeed;
            var rateBefore = c.magicPerSecond();
            for (var i = 0; i < steps; i++)
                method.Invoke(c.magicPurchases, null);
            var spent = expBefore - c.realExp;
            var confirmed = spent == cost && c.magic.magicBarSpeed > speedBefore
                            && c.magicPerSecond() > rateBefore;
            Main.LogAction(confirmed ? "PURCHASE" : "REJECTED", confirmed
                ? "Bought " + steps + " Magic Speed atoms for " + spent + " EXP: base speed "
                  + speedBefore.ToString("0.0") + " -> " + c.magic.magicBarSpeed.ToString("0.0")
                  + ", generation " + rateBefore.ToString("0.###") + " -> "
                  + c.magicPerSecond().ToString("0.###") + "/s [confirmed discrete rate breakpoint]"
                : "Magic Speed breakpoint purchase failed validation: spent=" + spent
                  + ", rate " + rateBefore.ToString("0.###") + " -> " + c.magicPerSecond().ToString("0.###"));
            return confirmed;
        }

        private static bool TryGetMagicSpeedBreakpoint(Character c, out int steps, out double projectedRate)
        {
            steps = 0;
            projectedRate = 0;
            if (c == null || c.magicPurchases == null || c.magic == null || c.magic.capMagic < 1000
                || c.magic.magicBarSpeed >= 49.91f)
                return false;
            var currentRate = Math.Max(0.0, c.magicPerSecond());
            var energyRate = Math.Max(0.0, c.energyPerSecond());
            // Before Titan 1, Magic supports Blood/TM but Energy still drives nearly
            // every immediate system. Later Normal raises the floor to one-third;
            // Evil/Sadistic allow Magic to approach parity through normal P/C/B.
            var desiredShare = c.settings.rebirthDifficulty == difficulty.normal
                ? c.highestBoss < 58 ? .10 : 1.0 / 3.0
                : .50;
            if (currentRate >= energyRate * desiredShare || c.magic.idleMagic > Math.Max(2L,
                    (long)Math.Ceiling(currentRate * .25)))
                return false;

            var baseSpeed = Math.Max(.1, c.magic.magicBarSpeed);
            var totalSpeed = Math.Max(.1, c.totalMagicSpeed());
            var speedMultiplier = totalSpeed / baseSpeed;
            var bars = Math.Max(1L, c.totalMagicBar());
            for (var n = 1; n <= 10 && baseSpeed + .1 * n <= 50.001; n++)
            {
                var futureSpeed = Math.Min(50.0, totalSpeed + .1 * n * speedMultiplier);
                var futureRate = 50.0 / Math.Ceiling(50.0 / futureSpeed) * bars;
                if (futureRate <= currentRate + 1e-6) continue;
                steps = n;
                projectedRate = futureRate;
                return true;
            }
            return false;
        }

        private PermanentExpTarget GetQolExpTarget(Character c)
        {
            if (c == null || c.adventurePurchases == null || c.miscPurchases == null)
                return null;
            var lifetime = Math.Max(1.0, c.stats.totalExp);

            // These buttons duplicate active bot subsystems and therefore remove no
            // progression time in full mode. They become valid only if the matching
            // subsystem was deliberately disabled, and even then must be trivial
            // relative to lifetime EXP so convenience cannot starve real growth.
            if (!Config.ManageInventory && !c.purchases.hasFilter && 20 <= lifetime * .005)
                return new PermanentExpTarget(c.adventurePurchases, "buyFilter",
                    "Basic Loot Filter", 20, () => c.purchases.hasFilter ? 1.0 : 0.0,
                    "inventory automation is disabled, so the native filter now prevents manual loot overflow");
            if (!Config.ManageInventory && !c.purchases.hasAutoMerge && 200 <= lifetime * .005)
                return new PermanentExpTarget(c.adventurePurchases, "buyAutoMerge",
                    "Auto Merge", 200, () => c.purchases.hasAutoMerge ? 1.0 : 0.0,
                    "inventory automation is disabled, so native merging now preserves collection throughput");
            if (!Config.ManageInventory && c.purchases.hasAutoMerge && !c.purchases.hasInvMerge
                && 1000 <= lifetime * .005)
                return new PermanentExpTarget(c.adventurePurchases, "buyInvMergeUnlock",
                    "Inventory Merge Slot", 1000, () => c.purchases.hasInvMerge ? 1.0 : 0.0,
                    "inventory automation is disabled and native inventory merging can replace repeated manual merges");
            if (!Config.ManageAllocations && !c.purchases.hasAutoAdvance && 300 <= lifetime * .005)
                return new PermanentExpTarget(c.miscPurchases, "buyAutoAdvance",
                    "Auto Advance", 300, () => c.purchases.hasAutoAdvance ? 1.0 : 0.0,
                    "allocation automation is disabled, so native excess transfer prevents capped Basic Training waste");
            return null;
        }

        private static int EarlyAdventureAtomIndex(Character c)
        {
            if (c == null || ZoneStatHelper.UserOverrides == null || c.highestBoss < 4)
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

        private bool BuyStrategicPermanentExpUpgrade()
        {
            var c = Main.Character;
            var target = GetStrategicPermanentExpTarget(c);
            if (target == null)
                return false;
            if (target.Cost > c.realExp - Config.ExpReserve)
                return ShouldReserveForPermanentExpTarget(c, target);
            BuyExpTarget(target, "permanent-system");
            return true;
        }

        private bool BuyExpTarget(PermanentExpTarget target, string category)
        {
            var c = Main.Character;
            if (c == null || target == null || target.Cost > c.realExp - Config.ExpReserve)
                return false;
            var expBefore = c.realExp;
            var stateBefore = target.State();
            var method = target.Controller.GetType().GetMethod(target.Method,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                Main.LogAction("REJECTED", target.Label + " purchase API was not found");
                return false;
            }
            method.Invoke(target.Controller, null);
            var confirmed = c.realExp < expBefore && target.State() != stateBefore;
            Main.LogAction(confirmed ? "PURCHASE" : "REJECTED", confirmed
                ? "Bought " + target.Label + " for " + (expBefore - c.realExp)
                  + " EXP [" + category + "; confirmed by EXP and ownership/stat deltas] — " + target.Reason
                : target.Label + " purchase produced no verified ownership/stat transition");
            return confirmed;
        }

        private bool ShouldReserveForPermanentExpTarget(Character c, PermanentExpTarget target)
        {
            if (c == null || target == null)
                return false;
            var available = Math.Max(0L, c.realExp - Config.ExpReserve);
            if (available >= target.Cost)
                return true;

            /*
             * A one-time unlock can justify a reserve because it cannot be bought
             * fractionally.  That does not justify freezing EXP for an entire long
             * accumulation, however.  Enter a short funding window only when the
             * admitted upgrade is close; until then permanent resource atoms earn
             * returns and are re-priced every second.  Accessory slots get the
             * longest window because they improve every contextual loadout.
             */
            var reserveWindow = target.Label.IndexOf("Accessory", StringComparison.OrdinalIgnoreCase) >= 0 ? 180.0
                : target.Label.IndexOf("Boost Recycling", StringComparison.OrdinalIgnoreCase) >= 0 ? 120.0
                : target.Label.IndexOf("Digger", StringComparison.OrdinalIgnoreCase) >= 0
                  || target.Label.IndexOf("Beard", StringComparison.OrdinalIgnoreCase) >= 0 ? 120.0
                : 60.0;
            var shortfall = target.Cost - available;
            if (_expPerSecond > 0 && shortfall / _expPerSecond <= reserveWindow)
                return true;
            // With no stable income estimate, reserve only the final 2%; this avoids
            // an infinite or multi-hour hold while still preventing a near-funded
            // discrete purchase from being delayed by one atom.
            return shortfall <= Math.Max(3L, (long)Math.Ceiling(target.Cost * .02));
        }

        private static PermanentExpTarget GetStrategicPermanentExpTarget(Character c)
        {
            if (c == null || c.adventurePurchases == null || c.miscPurchases == null)
                return null;
            var lifetime = Math.Max(1.0, c.stats.totalExp);
            var targets = new List<PermanentExpTarget>();
            // Native AdventurePurchases disables this button at purchases.boost
            // >= 0.5 and buyRecycleBoost clamps the field to exactly 0.5. Basic
            // Challenge completions are added only to the displayed percentage;
            // they do not raise the purchasable cap. Testing against 0.999 made a
            // MAX button look perpetually unowned to both the buyer and monitor.
            if (c.highestBoss >= 4 && c.purchases.boost < .5f)
                targets.Add(new PermanentExpTarget(c.adventurePurchases, "buyRecycleBoost",
                    "Boost Recycling", 100, () => c.purchases.boost,
                    "permanently recovers more boost value into gear and the Infinity Cube", 5.0));
            if (c.highestBoss >= 17 && !c.purchases.hasDaycare)
                targets.Add(new PermanentExpTarget(c.adventurePurchases, "buyDaycare",
                    "Item Daycare", 250, () => c.purchases.hasDaycare ? 1.0 : 0.0,
                    "creates a parallel permanent MAXX stream for slow, rare, and temporarily unfarmable equipment", 2.0));
            if (c.highestBoss >= 4 && !c.purchases.hasAcc3)
                targets.Add(new PermanentExpTarget(c.adventurePurchases, "buyAcc3",
                    "Accessory slot 3", 3000, () => c.purchases.hasAcc3 ? 1.0 : 0.0,
                    "an additional equipped special compounds every combat and resource loadout", 10.0));
            if (c.purchases.hasDaycare && !c.purchases.hasDaycareSlot2)
                targets.Add(new PermanentExpTarget(c.adventurePurchases, "buyDaycareSlot2",
                    "Daycare slot 2", 25000, () => c.purchases.hasDaycareSlot2 ? 1.0 : 0.0,
                    "doubles parallel item leveling when the collection planner still has un-MAXXED equipment debt", 2.0));
            if (c.purchases.hasDaycare && c.purchases.hasDaycareSlot2 && !c.purchases.hasDaycareSlot3)
                targets.Add(new PermanentExpTarget(c.adventurePurchases, "buyDaycareSlot3",
                    "Daycare slot 3", 500000, () => c.purchases.hasDaycareSlot3 ? 1.0 : 0.0,
                    "adds a third parallel item-leveling stream for late rare items, Hearts, and MacGuffins", 2.0));
            if (c.settings.diggersOn && !c.purchases.hasDiggerSlot1)
                targets.Add(new PermanentExpTarget(c.miscPurchases, "buydigger1",
                    "Digger slot", 25000, () => c.purchases.hasDiggerSlot1 ? 1.0 : 0.0,
                    "parallel permanent digger bonuses remove repeated gold/Adventure bottlenecks", 8.0));
            if (c.settings.beardsOn && !c.purchases.hasBeardSlot1)
                targets.Add(new PermanentExpTarget(c.miscPurchases, "buybeard1",
                    "Beard slot", 50000, () => c.purchases.hasBeardSlot1 ? 1.0 : 0.0,
                    "a second permanent beard conversion stream repays across every long rebirth", 8.0));
            if (c.highestBoss >= 4 && c.purchases.hasAcc3 && !c.purchases.hasAcc5)
                targets.Add(new PermanentExpTarget(c.adventurePurchases, "buyAcc5",
                    "Accessory slot 5", 30000, () => c.purchases.hasAcc5 ? 1.0 : 0.0,
                    "an additional equipped special compounds every contextual loadout", 10.0));
            if (c.inventory.macguffins != null && c.inventory.macguffins.Count > 0
                && !c.purchases.hasMacguffinSlot1)
                targets.Add(new PermanentExpTarget(c.miscPurchases, "buyMacguffin1",
                    "MacGuffin slot", 10000000, () => c.purchases.hasMacguffinSlot1 ? 1.0 : 0.0,
                    "banks another permanent MacGuffin bonus on every rebirth", 4.0));

            // The guide's 10%-of-lifetime rule is used only as an opportunity-cost
            // admission test.  Within admitted upgrades we still use a progression
            // order, and we save rather than buying an inferior affordable package.
            return targets.Where(x => x.Cost <= lifetime * .10)
                .OrderBy(x => x.Cost / Math.Max(.01, x.UtilityWeight))
                .FirstOrDefault();
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
                    ? "Bought permanent auto-activation for " + GameNames.Fruit(c, best) + " for "
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

        private static string NativeApPurchaseName(Character c, int id)
        {
            try
            {
                var native = UnityEngine.Resources.FindObjectsOfTypeAll<ArbitraryController>()
                    .Where(x => x != null && x.id == id && !string.IsNullOrEmpty(x.itemName))
                    .OrderByDescending(x => x.character == c)
                    .FirstOrDefault();
                if (native != null)
                    return native.itemName.Replace("\r", " ").Replace("\n", " ").Trim();
            }
            catch { }
            // These four are the early targets that can be selected while their shop page is
            // inactive (and therefore absent from Resources). Strings match the serialized AP
            // shop labels/internal controller name in the installed build.
            if (id == 9) return "Insta Training Caps";
            if (id == 14) return GameNames.Item(c, 129);
            if (id == 15) return "Additional Inventory Spaces";
            if (id == 16) return "Starter Pack";
            return "AP upgrade ID " + id;
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
                controller.itemName = NativeApPurchaseName(controller.character, id);
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

        private sealed class MarginalExpCandidate
        {
            internal readonly object Controller;
            internal readonly string Label;
            internal readonly string Method;
            internal readonly long Cost;
            internal readonly Func<double> ReadValue;
            internal readonly double NormalizedLevel;
            internal readonly string Reason;
            internal readonly bool UsesCustomInput;
            internal readonly int Power;
            internal readonly int Cap;
            internal readonly int Bars;
            internal readonly double NormalizedStep;

            internal MarginalExpCandidate(object controller, string label, string method, long cost,
                Func<double> readValue, double normalizedLevel, string reason, bool usesCustomInput,
                int power, int cap, int bars, double normalizedStep)
            {
                Controller = controller;
                Label = label;
                Method = method;
                Cost = cost;
                ReadValue = readValue;
                NormalizedLevel = normalizedLevel;
                Reason = reason;
                UsesCustomInput = usesCustomInput;
                Power = power;
                Cap = cap;
                Bars = bars;
                NormalizedStep = normalizedStep;
            }
        }

        private sealed class PermanentExpTarget
        {
            internal readonly object Controller;
            internal readonly string Method;
            internal readonly string Label;
            internal readonly long Cost;
            internal readonly Func<double> State;
            internal readonly string Reason;
            internal readonly double UtilityWeight;

            internal PermanentExpTarget(object controller, string method, string label,
                long cost, Func<double> state, string reason, double utilityWeight = 1.0)
            {
                Controller = controller;
                Method = method;
                Label = label;
                Cost = cost;
                State = state;
                Reason = reason;
                UtilityWeight = Math.Max(.01, utilityWeight);
            }
        }
    }
}
