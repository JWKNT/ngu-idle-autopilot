using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Security.Policy;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using NGUInjector.AllocationProfiles;
using NGUInjector.Autopilot;
using NGUInjector.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Application = UnityEngine.Application;

namespace NGUInjector
{
    internal class Main : MonoBehaviour
    {
        internal static InventoryController Controller;
        internal static Character Character;
        internal static PlayerController PlayerController;
        internal static StreamWriter OutputWriter;
        internal static StreamWriter LootWriter;
        internal static StreamWriter CombatWriter;
        internal static StreamWriter AllocationWriter;
        internal static StreamWriter PitSpinWriter;
        internal static StreamWriter ActionWriter;
        private static readonly object ActionLogLock = new object();
        private static readonly Dictionary<string, DateTime> LastRepeatedAction = new Dictionary<string, DateTime>();
        internal static Main reference;
        private YggdrasilManager _yggManager;
        private InventoryManager _invManager;
        private CombatManager _combManager;
        private QuestManager _questManager;
        private static CustomAllocation _profile;
        internal static AutopilotManager Autopilot;
        private float _timeLeft = 1.0f;
        internal static SettingsForm settingsForm;
        internal static WishManager WishManager;
        internal const string Version = "3.4.2";
        private static int _furthestZone;
        private bool _syncStateInitialized;
        private bool _lastGameplayReady;
        private DateTime _lastAutoEnterAttempt = DateTime.MinValue;
        private volatile bool _zoneReloadRequested;
        private volatile bool _settingsReloadRequested;
        private volatile bool _allocationReloadRequested;
        private volatile bool _allocationListReloadRequested;
        private static bool _isUnloading;
        private static readonly Queue<Action> MainThreadActions = new Queue<Action>();
        private static readonly object MainThreadActionsLock = new object();

        internal static bool Test { get; set; }

        // WinForms events run on their own UI thread.  Unity controllers may only
        // be touched by the game thread, so every form-triggered mutation is queued
        // and drained from MonoBehaviour.Update().
        internal static void RunOnMainThread(Action action)
        {
            if (action == null) return;
            lock (MainThreadActionsLock) MainThreadActions.Enqueue(action);
        }

        private static void DrainMainThreadActions()
        {
            for (var i = 0; i < 32; i++)
            {
                Action action;
                lock (MainThreadActionsLock)
                {
                    if (MainThreadActions.Count == 0) return;
                    action = MainThreadActions.Dequeue();
                }
                try { action(); }
                catch (Exception ex) { LogAction("REJECTED", "Queued settings action failed: " + ex.Message); }
            }
        }

        private static string _dir;
        private static string _profilesDir;

        private static bool _tempSwapped = false;

        internal static FileSystemWatcher ConfigWatcher;
        internal static FileSystemWatcher AllocationWatcher;
        internal static FileSystemWatcher ZoneWatcher;

        internal static bool IgnoreNextChange { get; set; }

        internal static SavedSettings Settings;

        internal static void Log(string msg)
        {
            var rebirth = Character == null || Character.rebirthTime == null
                ? 0 : Math.Floor(Character.rebirthTime.totalseconds);
            var line = $"{ DateTime.Now.ToShortDateString()}-{ DateTime.Now.ToShortTimeString()} ({rebirth}s): {msg}";
            if (OutputWriter != null) OutputWriter.WriteLine(line);
            else System.Diagnostics.Debug.WriteLine(line);
            if (!string.IsNullOrEmpty(msg) && msg.Length <= 300 && !msg.Contains("\n"))
                LogAction("SYSTEM", msg);
        }

        internal static void LogLoot(string msg)
        {
            LootWriter.WriteLine($"{ DateTime.Now.ToShortDateString()}-{ DateTime.Now.ToShortTimeString()} ({Math.Floor(Character.rebirthTime.totalseconds)}s): {msg}");
            LogAction("LOOT", msg);
        }

        internal static void LogCombat(string msg)
        {
            CombatWriter.WriteLine($"{DateTime.Now.ToShortDateString()}-{ DateTime.Now.ToShortTimeString()} ({Math.Floor(Character.rebirthTime.totalseconds)}s): {msg}");
            LogAction("COMBAT", msg);
        }

        internal static void LogPitSpin(string msg)
        {
            PitSpinWriter.WriteLine($"{DateTime.Now.ToShortDateString()}-{ DateTime.Now.ToShortTimeString()} ({Math.Floor(Character.rebirthTime.totalseconds)}s): {msg}");
            LogAction("REWARD", msg);
        }

        internal static void LogAction(string category, string msg)
        {
            if (ActionWriter == null || string.IsNullOrEmpty(msg)) return;
            lock (ActionLogLock)
            {
                // Identical 5 Hz allocation confirmations bury combat, purchases,
                // holds, and progression. Preserve every changed allocation but
                // reduce exact repeats to one heartbeat every two seconds.
                if (string.Equals(category, "ALLOC", StringComparison.OrdinalIgnoreCase))
                {
                    var key = category + "\n" + msg;
                    DateTime last;
                    var now = DateTime.UtcNow;
                    if (LastRepeatedAction.TryGetValue(key, out last)
                        && (now - last).TotalSeconds < 2.0)
                        return;
                    LastRepeatedAction[key] = now;
                    if (LastRepeatedAction.Count > 128)
                    {
                        var cutoff = now.AddSeconds(-30);
                        foreach (var stale in LastRepeatedAction.Where(x => x.Value < cutoff)
                                     .Select(x => x.Key).ToArray())
                            LastRepeatedAction.Remove(stale);
                    }
                }
                var rebirth = Character == null ? 0 : Math.Floor(Character.rebirthTime.totalseconds);
                ActionWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{category}] ({rebirth}s) {msg.Replace("\r", " ").Replace("\n", " ")}");
            }
        }

        internal static void LogAllocation(string msg)
        {
            if (!Settings.DebugAllocation) return;
            AllocationWriter.WriteLine($"{DateTime.Now.ToShortDateString()}-{ DateTime.Now.ToShortTimeString()} ({Math.Floor(Character.rebirthTime.totalseconds)}s): {msg}");
        }

        internal static string GetProfilesDir()
        {
            return _profilesDir;
        }

        internal static bool AutopilotWants(Func<AutopilotConfig, bool> selector)
        {
            return Autopilot != null && Autopilot.CanExecuteSafe && Autopilot.Config != null && selector(Autopilot.Config);
        }

        internal static bool IsGameplayReady
        {
            get
            {
                if (Character == null || Character.mainMenu == null || Character.mainMenu.mainMenu == null)
                    return false;
                var menuPosition = Character.mainMenu.mainMenu.transform.localPosition;
                return Character.mainMenu.doneInitialLoad && menuPosition.x < -1000f && menuPosition.y > 1000f;
            }
        }

        // Mutation routines wait for the synchronization routine to publish the verified
        // transition. This keeps the action stream causally ordered: PAUSED -> SYNCED -> actions.
        internal static bool IsAutomationReady
        {
            get
            {
                return IsGameplayReady && reference != null && reference._syncStateInitialized
                       && reference._lastGameplayReady;
            }
        }

        private void GameplaySyncRoutine()
        {
            if (Character == null || Autopilot == null || Autopilot.Config == null)
                return;

            var ready = IsGameplayReady;
            var detail = ready
                ? "active gameplay verified by MainMenuController.doneInitialLoad and hidden menu transform"
                : "main menu is still visible; all game mutations are hard-paused";
            Autopilot.ReportSynchronization(ready, detail);

            if (!_syncStateInitialized || ready != _lastGameplayReady)
            {
                LogAction("SYNC", ready ? "Active gameplay verified; automation enabled" : "Main menu detected; automation paused");
                _syncStateInitialized = true;
                _lastGameplayReady = ready;
            }

            if (ready || !Autopilot.Config.Enabled || !Autopilot.Config.IsFull || !Autopilot.Config.AutoEnterGame)
                return;
            if ((DateTime.Now - _lastAutoEnterAttempt).TotalSeconds < 5)
                return;
            if (!Character.mainMenu.getlocalSaveValidity())
                return;

            _lastAutoEnterAttempt = DateTime.Now;
            LogAction("SYNC", "Verified local autosave found; invoking the game's own Load Autosave controller");
            Character.mainMenu.loadAutosave();
        }

        private static CustomAllocation ActiveProfile
        {
            get { return Autopilot != null && Autopilot.CanExecuteSafe && Autopilot.Profile != null ? Autopilot.Profile : _profile; }
        }

        internal void Unload()
        {
            _isUnloading = true;
            try
            {
                CancelInvoke("AutomationRoutine");
                CancelInvoke("SnipeZone");
                CancelInvoke("MonitorLog");
                CancelInvoke("QuickStuff");
                CancelInvoke("FastAllocationRoutine");
                CancelInvoke("SetResnipe");
                CancelInvoke("ShowBoostProgress");
                CancelInvoke("GameplaySyncRoutine");


                LootWriter?.Close();
                CombatWriter?.Close();
                AllocationWriter?.Close();
                PitSpinWriter?.Close();
                ActionWriter?.Close();
                settingsForm?.Close();
                settingsForm?.Dispose();

                ConfigWatcher?.Dispose();
                AllocationWatcher?.Dispose();
                ZoneWatcher?.Dispose();
            }
            catch (Exception e)
            {
                Log(e.Message);
            }
            OutputWriter?.Close();
        }

        public void Start()
        {
            try
            {
                var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var installDir = Directory.Exists(@"Z:\Users\jw\Desktop\bin\ngu-idle-bot")
                    ? @"Z:\Users\jw\Desktop\bin\ngu-idle-bot"
                    : assemblyDir;
                if (string.IsNullOrEmpty(installDir))
                {
                    var hostHome = Environment.GetEnvironmentVariable("HOME");
                    if (!string.IsNullOrEmpty(hostHome) && hostHome.StartsWith("/"))
                    {
                        var candidate = "Z:" + hostHome.Replace('/', '\\') + "\\Desktop\\bin\\ngu-idle-bot";
                        if (Directory.Exists(candidate)) installDir = candidate;
                    }
                }
                if (string.IsNullOrEmpty(installDir))
                    installDir = Environment.ExpandEnvironmentVariables("%userprofile%/Desktop/ngu-idle-bot");
                _dir = Path.Combine(installDir, "runtime");
                if (!Directory.Exists(_dir))
                {
                    Directory.CreateDirectory(_dir);
                }

                var logDir = Path.Combine(_dir, "logs");
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                OutputWriter = new StreamWriter(Path.Combine(logDir, "inject.log")) {AutoFlush = true};
                LootWriter = new StreamWriter(Path.Combine(logDir, "loot.log")) {AutoFlush = true};
                CombatWriter = new StreamWriter(Path.Combine(logDir, "combat.log")) {AutoFlush = true};
                AllocationWriter = new StreamWriter(Path.Combine(logDir, "allocation.log")) {AutoFlush = true};
                PitSpinWriter = new StreamWriter(Path.Combine(logDir, "pitspin.log"), true) {AutoFlush = true};
                ActionWriter = new StreamWriter(Path.Combine(logDir, "actions.log")) {AutoFlush = true};

                _profilesDir = Path.Combine(_dir, "profiles");
                if (!Directory.Exists(_profilesDir))
                {
                    Directory.CreateDirectory(_profilesDir);
                }

                var oldPath = Path.Combine(_dir, "allocation.json");
                var newPath = Path.Combine(_profilesDir, "default.json");

                if (File.Exists(oldPath) && !File.Exists(newPath))
                {
                    File.Move(oldPath, newPath);
                }
            }
            catch (Exception e)
            {
                Log(e.Message);
                Log(e.StackTrace);
                Loader.Unload();
                return;
            }
            
            try
            {
                Character = FindObjectOfType<Character>();
                if (Character == null)
                    throw new InvalidOperationException("NGU Idle Character controller was not found; injection occurred before gameplay initialized");

                Log("Injected");
                LogLoot("Starting Loot Writer");
                LogCombat("Starting Combat Writer");
                Controller = Character.inventoryController;
                PlayerController = FindObjectOfType<PlayerController>();
                _invManager = new InventoryManager();
                _yggManager = new YggdrasilManager();
                _questManager = new QuestManager();
                _combManager = new CombatManager();
                LoadoutManager.ReleaseLock();
                DiggerManager.ReleaseLock();

                Settings = new SavedSettings(_dir);

                if (!Settings.LoadSettings())
                {
                    var temp = new SavedSettings(null)
                    {
                        PriorityBoosts = new int[] { },
                        YggdrasilLoadout = new int[] { },
                        SwapYggdrasilLoadouts = false,
                        SwapTitanLoadouts = false,
                        TitanLoadout = new int[] { },
                        ManageDiggers = true,
                        ManageYggdrasil = false,
                        ManageEnergy = true,
                        ManageMagic = true,
                        ManageInventory = true,
                        ManageGear = true,
                        AutoConvertBoosts = true,
                        SnipeZone = 0,
                        FastCombat = false,
                        PrecastBuffs = true,
                        AutoFight = false,
                        AutoQuest = false,
                        AutoQuestITOPOD = false,
                        AllowMajorQuests = false,
                        GoldDropLoadout = new int[] {},
                        AutoMoneyPit = false,
                        AutoSpin = false,
                        MoneyPitLoadout = new int[] {},
                        AutoRebirth = false,
                        ManageWandoos = false,
                        MoneyPitThreshold = 1e5,
                        DoGoldSwap = false,
                        BoostBlacklist = new int[] {},
                        CombatMode = 0,
                        RecoverHealth = false,
                        SnipeBossOnly = true,
                        AllowZoneFallback = false,
                        QuestFastCombat = true,
                        AbandonMinors = false,
                        MinorAbandonThreshold = 30,
                        QuestCombatMode = 0,
                        AutoBuyEM = false,
                        AutoSpellSwap = false,
                        CounterfeitThreshold = 400,
                        SpaghettiThreshold = 30,
                        BloodNumberThreshold = 1e10,
                        CastBloodSpells = false,
                        IronPillThreshold = 10000,
                        BloodMacGuffinAThreshold = 6,
                        BloodMacGuffinBThreshold = 6,
                        CubePriority = 0,
                        CombatEnabled = false,
                        GlobalEnabled = false,
                        QuickDiggers = new int[] {},
                        QuickLoadout = new int[] {},
                        UseButterMajor = false,
                        ManualMinors =  false,
                        UseButterMinor = false,
                        ActivateFruits = false,
                        ManageR3 = true,
                        WishPriorities = new int[] {},
                        BeastMode = true,
                        ManageNGUDiff = true,
                        AllocationFile = "default",
                        TitanGoldTargets = new bool[ZoneHelpers.TitanZones.Length],
                        ManageGoldLoadouts = false,
                        ResnipeTime = 3600,
                        TitanMoneyDone = new bool[ZoneHelpers.TitanZones.Length],
                        TitanSwapTargets = new bool[ZoneHelpers.TitanZones.Length],
                        GoldCBlockMode = false,
                        DebugAllocation = false,
                        AdventureTargetITOPOD = false,
                        ITOPODRecoverHP = false,
                        ITOPODCombatMode = 0,
                        ITOPODBeastMode = true,
                        ITOPODFastCombat = true,
                        ITOPODPrecastBuffs = false,
                        DisableOverlay = false,
                        OptimizeITOPODFloor = false,
                        YggSwapThreshold = 1,
                        UpgradeDiggers = true,
                        BlacklistedBosses = new int[0],
                        SpecialBoostBlacklist = new int[0],
                        MoreBlockParry = false,
                        WishSortOrder = false,
                        WishSortPriorities = false,
                        HackAdvance = false
                    };

                    Settings.MassUpdate(temp);

                    Log($"Created default settings");
                }

                settingsForm = new SettingsForm();

                if (string.IsNullOrEmpty(Settings.AllocationFile))
                {
                    Settings.SetSaveDisabled(true);
                    Settings.AllocationFile = "default";
                    Settings.SetSaveDisabled(false);
                }

                if (Settings.TitanGoldTargets == null || Settings.TitanGoldTargets.Length != ZoneHelpers.TitanZones.Length)
                {
                    Settings.SetSaveDisabled(true);
                    var normalized = new bool[ZoneHelpers.TitanZones.Length];
                    if (Settings.TitanGoldTargets != null)
                        Array.Copy(Settings.TitanGoldTargets, normalized,
                            Math.Min(Settings.TitanGoldTargets.Length, normalized.Length));
                    Settings.TitanGoldTargets = normalized;
                    Settings.SetSaveDisabled(false);
                }

                if (Settings.TitanMoneyDone == null || Settings.TitanMoneyDone.Length != ZoneHelpers.TitanZones.Length)
                {
                    Settings.SetSaveDisabled(true);
                    var normalized = new bool[ZoneHelpers.TitanZones.Length];
                    if (Settings.TitanMoneyDone != null)
                        Array.Copy(Settings.TitanMoneyDone, normalized,
                            Math.Min(Settings.TitanMoneyDone.Length, normalized.Length));
                    Settings.TitanMoneyDone = normalized;
                    Settings.SetSaveDisabled(false);
                }

                if (Settings.TitanSwapTargets == null || Settings.TitanSwapTargets.Length != ZoneHelpers.TitanZones.Length)
                {
                    Settings.SetSaveDisabled(true);
                    var normalized = new bool[ZoneHelpers.TitanZones.Length];
                    if (Settings.TitanSwapTargets != null)
                        Array.Copy(Settings.TitanSwapTargets, normalized,
                            Math.Min(Settings.TitanSwapTargets.Length, normalized.Length));
                    Settings.TitanSwapTargets = normalized;
                    Settings.SetSaveDisabled(false);
                }

                if (Settings.SpecialBoostBlacklist == null)
                {
                    Settings.SetSaveDisabled(true);
                    Settings.SpecialBoostBlacklist = new int[0];
                    Settings.SetSaveDisabled(false);
                }

                if (Settings.BlacklistedBosses == null)
                {
                    Settings.SetSaveDisabled(true);
                    Settings.BlacklistedBosses = new int[0];
                    Settings.SetSaveDisabled(false);
                }

                WishManager = new WishManager();

                Autopilot = new AutopilotManager(_dir, _profilesDir);

                LoadAllocation();
                LoadAllocationProfiles();

                ZoneWatcher = new FileSystemWatcher
                {
                    Path = _dir,
                    Filter = "zoneOverride.json",
                    NotifyFilter = NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };

                ZoneWatcher.Changed += (sender, args) =>
                {
                    _zoneReloadRequested = true;
                };

                ConfigWatcher = new FileSystemWatcher
                {
                    Path = _dir,
                    Filter = "settings.json",
                    NotifyFilter = NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };

                ConfigWatcher.Changed += (sender, args) =>
                {
                    _settingsReloadRequested = true;
                };

                AllocationWatcher = new FileSystemWatcher
                {
                    Path = _profilesDir,
                    Filter = "*.json",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };

                AllocationWatcher.Changed += (sender, args) =>
                {
                    // The autopilot loads its generated profile synchronously. Do not
                    // let this watcher race in a load of the unrelated legacy default.
                    if (!string.Equals(args.Name, "autopilot.generated.json", StringComparison.OrdinalIgnoreCase))
                        _allocationReloadRequested = true;
                };
                AllocationWatcher.Created += (sender, args) => { _allocationListReloadRequested = true; };
                AllocationWatcher.Deleted += (sender, args) => { _allocationListReloadRequested = true; };
                AllocationWatcher.Renamed += (sender, args) => { _allocationListReloadRequested = true; };

                Settings.SaveSettings();
                Settings.LoadSettings();

                LogAllocation("Started Allocation Writer");

                ZoneStatHelper.CreateOverrides(_dir);

                settingsForm.UpdateFromSettings(Settings);
                settingsForm.Show();

                InvokeRepeating("GameplaySyncRoutine", 0.0f, .2f);
                InvokeRepeating("AutomationRoutine", 0.05f, 1.0f);
                InvokeRepeating("SnipeZone", 0.0f, .1f);
                InvokeRepeating("MonitorLog", 0.0f, 1f);
                InvokeRepeating("QuickStuff", 0.0f, .2f);
                // Resource plans are held for one second to avoid repeatedly removing and
                // restoring identical allocations. Combat/boss decisions retain their
                // 0.1/0.2-second cadence.
                // Generated Energy is free to reclaim and reassign. Sweep it five
                // times per second so the visible idle pool remains near zero instead
                // of waiting up to a full second between productive allocations.
                InvokeRepeating("FastAllocationRoutine", 0.0f, .2f);
                InvokeRepeating("ShowBoostProgress", 0.0f, 60.0f);
                InvokeRepeating("SetResnipe", 0f,1f);


                reference = this;
            }
            catch (Exception e)
            {
                Log(e.ToString());
                Log(e.StackTrace);
                if (e.InnerException != null) Log(e.InnerException.ToString());
            }
        }

        internal static void UpdateForm(SavedSettings newSettings)
        {
            settingsForm.UpdateFromSettings(newSettings);
        }

        public void Update()
        {
            DrainMainThreadActions();
            if (_isUnloading) return;
            _timeLeft -= Time.deltaTime;
            if (IsAutomationReady)
                _combManager.UpdateFightTimer(Time.deltaTime);

            settingsForm.UpdateProgressBar((int)Math.Floor(_timeLeft * 100));

            if (Input.GetKeyDown(KeyCode.F1))
            {
                if (!settingsForm.Visible)
                {
                    settingsForm.Show();
                }

                settingsForm.BringToFront();
            }

            if (Input.GetKeyDown(KeyCode.F2))
            {
                Settings.GlobalEnabled = !Settings.GlobalEnabled;
            }

            if (Input.GetKeyDown(KeyCode.F3))
            {
                QuickSave();
            }

            if (Input.GetKeyDown(KeyCode.F7))
            {
                QuickLoad();
            }

            if (Input.GetKeyDown(KeyCode.F4))
            {
                Settings.AutoQuestITOPOD = !Settings.AutoQuestITOPOD;
            }

            if (Input.GetKeyDown(KeyCode.F5))
            {
                DumpEquipped();
            }

            if (Input.GetKeyDown(KeyCode.F8))
            {
                if (Settings.QuickLoadout.Length > 0)
                {
                    if (_tempSwapped)
                    {
                        Log("Restoring Previous Loadout");
                        LoadoutManager.RestoreTempLoadout();
                    }
                    else
                    {
                        Log("Equipping Quick Loadout");
                        LoadoutManager.SaveTempLoadout();
                        LoadoutManager.ChangeGear(Settings.QuickLoadout);
                    }
                }

                if (Settings.QuickDiggers.Length > 0)
                {
                    if (_tempSwapped)
                    {
                        Log("Equipping Previous Diggers");
                        DiggerManager.RestoreTempDiggers();
                    }
                    else
                    {
                        Log("Equipping Quick Diggers");
                        DiggerManager.SaveTempDiggers();
                        DiggerManager.EquipDiggers(Settings.QuickDiggers);
                    }
                }

                _tempSwapped = !_tempSwapped;
            }

            // F11 reserved for testing
            //if (Input.GetKeyDown(KeyCode.F11))
            //{
            //    Character.realExp += 10000;
            //}
        }

        private void QuickSave()
        {
            Log("Writing quicksave and json");
            var data = Character.importExport.getBase64Data();
            WriteTextAtomic(Path.Combine(_dir, "NGUSave.txt"), data + Environment.NewLine);

            data = JsonUtility.ToJson(Character.importExport.gameStateToData());
            WriteTextAtomic(Path.Combine(_dir, "NGUSave.json"), data + Environment.NewLine);

            Character.saveLoad.saveGamestateToSteamCloud();
        }

        private static void WriteTextAtomic(string path, string contents)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, contents);
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
        }

        private void QuickLoad()
        {
            var filename = Path.Combine(_dir, "NGUSave.txt");
            if (!File.Exists(filename))
            {
                Log("Quicksave doesn't exist");
                return;
            }

            var saveTime = File.GetLastWriteTime(filename);
            var s = DateTime.Now.Subtract(saveTime);
            var secDiff = (int)s.TotalSeconds;
            if (secDiff > 120)
            {
                var diff = saveTime.GetPrettyDate();

                var confirmResult = MessageBox.Show($"Last quicksave was {diff}. Are you sure you want to load?",
                    "Load Quicksave"
                    , MessageBoxButtons.YesNo);

                if (confirmResult == DialogResult.No)
                    return;
            }

            Log("Loading quicksave");
            string base64Data;
            try
            {
                base64Data = File.ReadAllText(filename);
            }
            catch (Exception e)
            {
                Log($"Failed to read quicksave: {e.Message}");
                return;
            }

            try
            {
                var saveDataFromString = Character.importExport.getSaveDataFromString(base64Data);
                var dataFromString = Character.importExport.getDataFromString(base64Data);

                if ((dataFromString == null || dataFromString.version < 361) &&
                    Application.platform != RuntimePlatform.WindowsEditor)
                {
                    Log("Bad save version");
                    return;
                }

                if (dataFromString.version > Character.getVersion())
                {
                    Log("Bad save version");
                    return;
                }

                // Publish the pause before the game's load mutates controllers.  All
                // repeating bot routines remain behind this barrier until the normal
                // gameplay synchronization probe verifies the new save is active.
                _syncStateInitialized = true;
                _lastGameplayReady = false;
                if (Autopilot != null)
                    Autopilot.ReportSynchronization(false, "quicksave load is in progress; waiting for a verified gameplay state");
                Character.saveLoad.loadintoGame(saveDataFromString);
            }
            catch (Exception e)
            {
                Log($"Failed to load quicksave: {e.Message}");
            }
        }

        // Stuff on a very short timer
        void FastAllocationRoutine()
        {
            if (!IsAutomationReady)
                return;
            if (!Settings.GlobalEnabled && (Autopilot == null || !Autopilot.CanExecuteSafe))
                return;

            try
            {
                if ((Settings.ManageInventory || AutopilotWants(x => x.ManageInventory || x.ManageAdventure || x.ManageBosses))
                    && !Controller.midDrag)
                    ProgressionLoadoutOptimizer.Manage();
                if (ActiveProfile != null && AutopilotWants(x => x.ManageAllocations))
                    ActiveProfile.DoAllocations();
            }
            catch (Exception e)
            {
                Log("Fast allocation error: " + e.Message);
            }
        }

        // Stuff on a very short timer
        void QuickStuff()
        {
            if (!IsAutomationReady)
                return;
            if (!Settings.GlobalEnabled && (Autopilot == null || !Autopilot.CanExecuteSafe))
                return;

            //Turn on autoattack if we're in ITOPOD and its not on
            if (Settings.AutoQuestITOPOD && Character.adventureController.zone >= 1000 && !Character.adventure.autoattacking && !Settings.CombatEnabled)
            {
                Character.adventureController.idleAttackMove.setToggle();
            }

            if (Settings.AutoFight || AutopilotWants(x => x.ManageBosses))
            {
                if (Autopilot != null && Autopilot.TryTitan7PuzzleStep())
                    return;
                var needsAllocation = false;
                var bc = Character.bossController;
                if (!bc.isFighting && !bc.nukeBoss)
                {
                    if (Character.bossID == 0)
                        needsAllocation = true;

                    if (CombatHelpers.CanNukeCurrentBoss(bc.character))
                    {
                        bc.startNuke();
                        LogAction(bc.isFighting && bc.nukeBoss ? "BOSS" : "REJECTED",
                            bc.isFighting && bc.nukeBoss
                                ? "Boss nuke started [confirmed by BossController state]"
                                : "Boss nuke request produced no state transition");
                    }
                    else
                    {
                        double expectedKillSeconds;
                        if (CombatHelpers.CanWinCurrentBoss(bc.character, out expectedKillSeconds))
                        {
                            bc.beginFight();
                            bc.stopButton.gameObject.SetActive(true);
                            LogAction(bc.isFighting ? "BOSS" : "REJECTED",
                                bc.isFighting
                                    ? "Exact-viability boss fight started; expected kill "
                                      + expectedKillSeconds.ToString("0.00")
                                      + "s [confirmed by BossController state]"
                                    : "Boss fight request produced no state transition");
                        }
                    }
                }

                if (needsAllocation)
                {
                    ActiveProfile.DoAllocations();
                }
            }

            if (Settings.AutoMoneyPit || AutopilotWants(x => x.ManageMoneyPit))
            {
                var autopilotPit = AutopilotWants(x => x.ManageMoneyPit);
                var workingCapital = autopilotPit
                    ? AutopilotManager.RequiredAugmentWorkingCapital(Character) : 0.0;
                // Native Pit tosses every gold piece, so a numeric threshold cannot
                // preserve an unpaid concurrent charge. Wait for that charge to start
                // (progress > 0 proves it was paid) before engaging the Pit.
                if (workingCapital <= 0)
                    MoneyPitManager.CheckMoneyPit(autopilotPit
                        ? Autopilot.Config.MoneyPitReserve
                        : Settings.MoneyPitThreshold);
            }

            if (Settings.AutoSpin || AutopilotWants(x => x.ManageDailySpin))
            {
                MoneyPitManager.DoDailySpin();
            }

            if (Settings.AutoQuestITOPOD)
            {
                MoveToITOPOD();
            }

            if (Settings.AutoSpellSwap || AutopilotWants(x => x.ManageBloodMagic))
            {
                var spaghetti = (Character.bloodMagicController.lootBonus() - 1) * 100;
                var counterfeit = ((Character.bloodMagicController.goldBonus() - 1)) * 100;
                var number = Character.bloodMagic.rebirthPower;
                var autopilotBlood = AutopilotWants(x => x.ManageBloodMagic);
                // Native automation divides Blood equally, then lets invalid low shares
                // fail while Rebirth still consumes its share. Full mode owns the cast
                // decision, so disable that lossy splitter and cast one best spell.
                Character.bloodMagic.rebirthAutoSpell = !autopilotBlood
                                                         && Settings.BloodNumberThreshold > 0 && number < Settings.BloodNumberThreshold;
                Character.bloodMagic.goldAutoSpell = !autopilotBlood
                                                      && Settings.CounterfeitThreshold > 0 && counterfeit < Settings.CounterfeitThreshold;
                Character.bloodMagic.lootAutoSpell = !autopilotBlood
                                                      && Settings.SpaghettiThreshold > 0 && spaghetti < Settings.SpaghettiThreshold;
                Character.bloodSpells.updateGoldToggleState();
                Character.bloodSpells.updateLootToggleState();
                Character.bloodSpells.updateRebirthToggleState();
            }
        }

        // Runs every second; tactical combat and allocations have separate faster loops.
        void AutomationRoutine()
        {
            try
            {
                if (!IsAutomationReady)
                {
                    _timeLeft = 1f;
                    return;
                }
                // File watcher callbacks are deliberately drained only after the
                // gameplay synchronization barrier.  Allocation reload invokes its
                // allocation pass, so even a seemingly read-only profile change can
                // reach Unity controllers.
                ProcessPendingFileChanges();
                if (Autopilot != null)
                    Autopilot.Tick();

                if (!Settings.GlobalEnabled && (Autopilot == null || !Autopilot.CanExecuteSafe))
                {
                    _timeLeft = 1f;
                    return;
                }

                ZoneHelpers.OptimizeITOPOD();

                if (AutopilotWants(x => x.ManageBeards))
                    BeardManager.Manage();

                if ((Settings.ManageInventory || AutopilotWants(x => x.ManageInventory)) && !Controller.midDrag)
                {
                    var converted = Character.inventory.GetConvertedInventory().ToArray();
                    _invManager.EnsureFiltered(converted);
                    _invManager.ManageConvertibles(converted);
                    converted = Character.inventory.GetConvertedInventory().ToArray();
                    _invManager.MergeEquipped(converted);
                    converted = Character.inventory.GetConvertedInventory().ToArray();
                    _invManager.MergeInventory(converted);
                    converted = Character.inventory.GetConvertedInventory().ToArray();
                    _invManager.MergeBoosts(converted);
                    // Native set-completion flags are awarded by the Item List
                    // controller, not by mergeAll itself.  Claim them immediately
                    // so an item that just reached level 100 cannot leave Adventure
                    // stuck in boss-snipe mode until the player opens that menu.
                    Character.allItemList.checkforBonuses();
                    _invManager.TrashProvenRedundantItem();
                    converted = Character.inventory.GetConvertedInventory().ToArray();
                    _invManager.MergeGuffs(converted);
                    converted = Character.inventory.GetConvertedInventory().ToArray();
                    var boostSlots = _invManager.GetBoostSlots(converted);
                    _invManager.BoostInventory(boostSlots);
                    _invManager.BoostInfinityCube();
                    converted = Character.inventory.GetConvertedInventory().ToArray();
                    boostSlots = _invManager.GetBoostSlots(converted);
                    _invManager.ManageBoostConversion(boostSlots);

                    // Re-evaluate the whole equipped set after merges/boosts because
                    // those operations can change both candidate stats and legality.
                    ProgressionLoadoutOptimizer.Manage();
                }

                // Daycare timers and completed-item rotation are independent of bulk
                // inventory manipulation, so a drag or disabled merge policy must not
                // stall this permanent progression system.
                if (Autopilot != null && Autopilot.CanExecuteSafe && !Controller.midDrag)
                    DaycareManager.Manage();

                //if (Settings.ManageInventory && !Controller.midDrag)
                //{
                //    var watch = Stopwatch.StartNew();
                //    var converted = Character.inventory.GetConvertedInventory().ToArray();
                //    Log($"Creating CI: {watch.ElapsedMilliseconds}");
                //    watch = Stopwatch.StartNew();
                //    var boostSlots = _invManager.GetBoostSlots(converted);
                //    Log($"Get Boost Slots: {watch.ElapsedMilliseconds}");
                //    watch = Stopwatch.StartNew();
                //    _invManager.EnsureFiltered(converted);
                //    Log($"Filtering: {watch.ElapsedMilliseconds}");
                //    watch = Stopwatch.StartNew();
                //    _invManager.ManageConvertibles(converted);
                //    Log($"Convertibles: {watch.ElapsedMilliseconds}");
                //    watch = Stopwatch.StartNew();
                //    _invManager.MergeEquipped(converted);
                //    Log($"Merge Equipped: {watch.ElapsedMilliseconds}");
                //    watch = Stopwatch.StartNew();
                //    _invManager.MergeInventory(converted);
                //    Log($"Merge Inventory: {watch.ElapsedMilliseconds}");
                //    watch = Stopwatch.StartNew();
                //    _invManager.MergeBoosts(converted);
                //    Log($"Merge Boosts: {watch.ElapsedMilliseconds}");
                //    watch = Stopwatch.StartNew();
                //    _invManager.MergeGuffs(converted);
                //    Log($"Merge Guffs: {watch.ElapsedMilliseconds}");
                //    watch = Stopwatch.StartNew();
                //    _invManager.BoostInventory(boostSlots);
                //    Log($"Boost Inventory: {watch.ElapsedMilliseconds}");
                //    watch = Stopwatch.StartNew();
                //    _invManager.BoostInfinityCube();
                //    Log($"Boost Cube: {watch.ElapsedMilliseconds}");
                //    watch = Stopwatch.StartNew();
                //    _invManager.ManageBoostConversion(boostSlots);
                //    Log($"Boost Conversion: {watch.ElapsedMilliseconds}");
                //    watch.Stop();
                //}

                if (LoadoutManager.CurrentLock == LockType.Titan || Settings.SwapTitanLoadouts
                    || Settings.ManageGoldLoadouts && Settings.NeedsGoldSwap())
                {
                    LoadoutManager.TryTitanSwap();
                    DiggerManager.TryTitanSwap();
                }

                if ((Settings.ManageYggdrasil || AutopilotWants(x => x.ManageYggdrasil)) && Character.buttons.yggdrasil.interactable)
                {
                    _yggManager.ManageYggHarvest();
                    _yggManager.CheckFruits();
                }

                if (Settings.AutoBuyEM && Character.highestBoss >= 17)
                {
                    var ePurchase = Character.energyPurchases;
                    var mPurchase = Character.magicPurchases;
                    var r3Purchase = Character.res3Purchases;

                    var energy = ePurchase.customAllCost() > 0;
                    var r3 = Character.res3.res3On && r3Purchase.customAllCost() > 0;
                    var magic = Character.highestBoss >= 37 && mPurchase.customAllCost() > 0;

                    long total = 0;

                    if (energy)
                    {
                        total += ePurchase.customAllCost();
                    }
                    
                    if (magic)
                    {
                        total += mPurchase.customAllCost();
                    }

                    if (r3)
                    {
                        total += r3Purchase.customAllCost();
                    }

                    if (total > 0)
                    {
                        var numPurchases = Math.Floor((double)(Character.realExp / total));

                        if (numPurchases > 0)
                        {
                            var t = string.Empty;
                            if (energy)
                            {
                                t += "/exp";
                            }

                            if (magic)
                            {
                                t += "/magic";
                            }

                            if (r3)
                            {
                                t += "/res3";
                            }

                            t = t.Substring(1);

                            Log($"Buying {numPurchases} {t} purchases");
                            for (var i = 0; i < numPurchases; i++)
                            {
                                if (energy)
                                {
                                    var ePurchaseMethod = ePurchase.GetType().GetMethod("buyCustomAll",
                                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    ePurchaseMethod?.Invoke(ePurchase, null);
                                }

                                if (magic)
                                {
                                    var mPurchaseMethod = mPurchase.GetType().GetMethod("buyCustomAll",
                                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    mPurchaseMethod?.Invoke(mPurchase, null);
                                }

                                if (r3)
                                {
                                    var r3PurchaseMethod = r3Purchase.GetType().GetMethod("buyCustomAll",
                                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    r3PurchaseMethod?.Invoke(r3Purchase, null);
                                }
                            }
                        }
                    }
                }

                if (!AutopilotWants(x => x.ManageAllocations))
                    ActiveProfile.DoAllocations();

                if (Settings.CastBloodSpells || AutopilotWants(x => x.ManageBloodMagic))
                    ActiveProfile.CastBloodSpells();

                if ((Settings.AutoQuest || AutopilotWants(x => x.ManageQuests)) && Character.buttons.beast.interactable)
                {
                    if (!Character.inventoryController.midDrag)
                    {
                        var converted = Character.inventory.GetConvertedInventory().ToArray();
                        _invManager.ManageQuestItems(converted);
                        _questManager.CheckQuestTurnin();
                        _questManager.ManageQuests();
                    }
                }

                if (Settings.AutoRebirth || Autopilot != null && Autopilot.CanExecuteIrreversible && Autopilot.Config.AllowRebirths)
                {
                    ActiveProfile.DoRebirth();
                }
            }
            catch (Exception e)
            {
                Log(e.Message);
                Log(e.StackTrace);
            }
            _timeLeft = 1f;
        }

        internal static void LoadAllocation()
        {
            _profile = new CustomAllocation(_profilesDir, Settings.AllocationFile);
            try
            {
                _profile.ReloadAllocation();
            }
            catch (Exception e)
            {
                Log(e.Message);
            }
        }

        private void ProcessPendingFileChanges()
        {
            // FileSystemWatcher callbacks run on worker threads. They may only set
            // flags; every Unity/controller/UI mutation is serialized here on the
            // main thread.
            if (_zoneReloadRequested)
            {
                _zoneReloadRequested = false;
                ZoneStatHelper.CreateOverrides(_dir);
                Log("Reloaded zone overrides on the main thread");
            }
            if (_settingsReloadRequested)
            {
                _settingsReloadRequested = false;
                if (IgnoreNextChange)
                    IgnoreNextChange = false;
                else
                {
                    Settings.LoadSettings();
                    settingsForm.UpdateFromSettings(Settings);
                    LoadAllocation();
                }
            }
            if (_allocationListReloadRequested)
            {
                _allocationListReloadRequested = false;
                LoadAllocationProfiles();
            }
            if (_allocationReloadRequested)
            {
                _allocationReloadRequested = false;
                LoadAllocation();
            }
        }

        private static void LoadAllocationProfiles() {
            var files = Directory.GetFiles(_profilesDir);
            settingsForm.UpdateProfileList(files.Select(Path.GetFileNameWithoutExtension).ToArray(), Settings.AllocationFile);
        }

        private void SnipeZone()
        {
            if (!IsAutomationReady)
                return;
            if (!Settings.GlobalEnabled && (Autopilot == null || !Autopilot.CanExecuteSafe))
                return;

            //If tm ever drops to 0, reset our gold loadout stuff
            if (Character.machine.realBaseGold == 0.0 && !Settings.DoGoldSwap)
            {
                Log("Time Machine Gold is 0. Lets reset gold snipe zone.");
                Settings.DoGoldSwap = true;
                Settings.TitanMoneyDone = new bool[ZoneHelpers.TitanZones.Length];
            }

            //This logic should trigger only if Time Machine is ready
            if (Character.buttons.brokenTimeMachine.interactable)
            {
                if (Character.machine.realBaseGold == 0.0)
                {
                    _combManager.ManualZone(0, false, false, false, true, false);
                    return;
                }
                //Go to our gold loadout zone next to get a high gold drop
                if (Settings.ManageGoldLoadouts && Settings.DoGoldSwap && Settings.GoldDropLoadout.Length > 0)
                {
                    if (LoadoutManager.TryGoldDropSwap())
                    {
                        var bestZone = ZoneStatHelper.GetBestZone();
                        _furthestZone = ZoneHelpers.GetMaxReachableZone(false);
                        
                        _combManager.ManualZone(bestZone.Zone, true, bestZone.FightType == 1, false, bestZone.FightType == 2, false);
                        return;
                    }
                }
            }

            var questZone = _questManager.IsQuesting();

            if (Autopilot != null && Autopilot.ControlAdventure(_combManager, _questManager))
                return;
            if (!Settings.CombatEnabled || Settings.AdventureTargetITOPOD || !ZoneHelpers.ZoneIsTitan(Settings.SnipeZone) ||
                ZoneHelpers.ZoneIsTitan(Settings.SnipeZone) &&
                !ZoneHelpers.TitanSpawningSoon(Array.IndexOf(ZoneHelpers.TitanZones, Settings.SnipeZone)))
            {
                if (questZone > 0)
                {
                    if (Settings.QuestCombatMode == 0)
                    {
                        _combManager.ManualZone(questZone, false, false, false, Settings.QuestFastCombat, Settings.BeastMode);
                    }
                    else
                    {
                        _combManager.IdleZone(questZone, false, false);
                    }

                    return;
                }
            }

            if (!Settings.CombatEnabled)
                return;

            var tempZone = Settings.AdventureTargetITOPOD ? 1000 : Settings.SnipeZone;
            if (tempZone < 1000)
            {
                if (!CombatManager.IsZoneUnlocked(Settings.SnipeZone))
                {
                    tempZone = Settings.AllowZoneFallback ? ZoneHelpers.GetMaxReachableZone(false) : 1000;
                }
                else
                {
                    if (ZoneHelpers.ZoneIsTitan(Settings.SnipeZone) && !ZoneHelpers.TitanSpawningSoon(Array.IndexOf(ZoneHelpers.TitanZones, Settings.SnipeZone)))
                    {
                        tempZone = 1000;
                    }
                }
            }

            if (tempZone >= 1000)
            {
                if (Settings.ITOPODCombatMode == 0)
                {
                    _combManager.ManualZone(tempZone, false, Settings.ITOPODRecoverHP, Settings.ITOPODPrecastBuffs, Settings.ITOPODFastCombat, Settings.ITOPODBeastMode);
                }
                else
                {
                    _combManager.IdleZone(tempZone, false, Settings.ITOPODRecoverHP);
                }

                return;
            }
            
            if (Settings.CombatMode == 0)
            {
                _combManager.ManualZone(tempZone, Settings.SnipeBossOnly, Settings.RecoverHealth, Settings.PrecastBuffs, Settings.FastCombat, Settings.BeastMode);
            }
            else
            {
                _combManager.IdleZone(tempZone, Settings.SnipeBossOnly, Settings.RecoverHealth);
            }
        }

        private void MoveToITOPOD()
        {
            if (!Settings.GlobalEnabled)
                return;

            if (_questManager.IsQuesting() >= 0)
                return;

            if (Settings.CombatEnabled)
                return;

            if (Settings.DoGoldSwap)
                return;

            //If we're not in ITOPOD, move there if its set
            if (Character.adventureController.zone >= 1000 || !Settings.AutoQuestITOPOD) return;
            Log($"Moving to ITOPOD to idle.");
            _combManager.MoveToZone(1000);
        }

        private void DumpEquipped()
        {
            var list = new List<int>
            {
                Character.inventory.head.id,
                Character.inventory.chest.id,
                Character.inventory.legs.id,
                Character.inventory.boots.id,
                Character.inventory.weapon.id
            };

            if (Character.inventoryController.weapon2Unlocked())
            {
                list.Add(Character.inventory.weapon2.id);
            }

            foreach (var acc in Character.inventory.accs)
            {
                list.Add(acc.id);
            }

            list.RemoveAll(x => x == 0);
            var items = $"[{string.Join(", ", list.Select(x => x.ToString()).ToArray())}]";

            Log($"Equipped Items: {items}");
            Clipboard.SetText(items);
        }

        public void OnGUI()
        {
            if (Settings.DisableOverlay) return;
            var effectiveActive = Settings.GlobalEnabled || Autopilot != null && Autopilot.CanExecuteSafe;
            GUI.Label(new Rect(10, 0, 240, 40), $"Automation - {(effectiveActive ? "Active" : "Inactive")}");
            GUI.Label(new Rect(10, 10, 200, 40), $"Next Loop - {_timeLeft:00.0}s");
            GUI.Label(new Rect(10, 20, 200, 40), $"Profile - {Settings.AllocationFile}");
            if (Autopilot != null)
                GUI.Label(new Rect(10, 30, 500, 40), $"Autopilot - {Autopilot.Status}");
        }

        public void MonitorLog()
        {
            if (!IsAutomationReady)
                return;
            var bLog = Character.adventureController.log;
            var type = bLog.GetType().GetField("Eventlog",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var val = type?.GetValue(bLog);
            if (val == null)
                return;

            var log = (List<string>) val;
            for (var i = log.Count - 1; i >= 0; i--)
            {
                var line = log[i];
                if (!line.Contains("dropped")) continue;
                if (line.Contains("gold")) continue;
                if (line.ToLower().Contains("special boost")) continue;
                if (line.ToLower().Contains("toughness boost")) continue;
                if (line.ToLower().Contains("power boost")) continue;
                if (line.Contains("EXP")) continue;
                if (line.EndsWith("<b></b>")) continue;
                var result = line;
                if (result.Contains("\n"))
                {
                    result = result.Split(new[] {'\n'}).Last();
                }

                var sb = new StringBuilder(result);
                sb.Replace("<color=blue>", "");
                sb.Replace("<b>", "");
                sb.Replace("</color>", "");
                sb.Replace("</b>", "");

                LogLoot(sb.ToString());
                log[i] = $"{line}<b></b>";
            }
        }

        public void SetResnipe()
        {
            if (!IsAutomationReady)
                return;
            if (Settings.ResnipeTime == 0 && !Settings.GoldCBlockMode) return;

            if (Settings.GoldCBlockMode)
            {
                var furthest = ZoneHelpers.GetMaxReachableZone(false);
                if (furthest > _furthestZone)
                {
                    Settings.DoGoldSwap = true;
                    _furthestZone = furthest;
                }

                return;
            }

            if (Math.Abs(Character.rebirthTime.totalseconds - Settings.ResnipeTime) <= 1)
            {
                Settings.DoGoldSwap = true;
            }
        }

        public void ShowBoostProgress()
        {
            if (!IsAutomationReady)
                return;
            var boostSlots = _invManager.GetBoostSlots(Character.inventory.GetConvertedInventory().ToArray());
            try
            {
                _invManager.ShowBoostProgress(boostSlots);
            }
            catch (Exception e)
            {
                Log(e.Message);
                Log(e.StackTrace);
            }
        }

        public void OnApplicationQuit()
        {
            Loader.Unload();
        }

        public void ResetBoostProgress()
        {

        }
    }
}
