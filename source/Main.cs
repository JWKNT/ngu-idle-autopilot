using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Security.Policy;
using System.Security.Cryptography;
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

/*
FILE PURPOSE

Main is the Unity orchestration and native-mutation dispatch host. It discovers live controllers,
establishes the active-game synchronization barrier, snapshots a sticky ExecutionSafety lease for
each scheduled pass, selects exactly one allocation owner, and writes confirmed action/deployment
telemetry. Inputs are Unity state, legacy settings, autopilot config/plans, and watched files;
outputs are leased manager/controller calls plus append-only runtime evidence. No mutation may run
before IsAutomationReady, dry-run can never inherit authority from GlobalEnabled, assist cannot
spend finite resources through legacy fallbacks, and a state-version change invalidates the whole
pass. Focused managers own mechanics and native postconditions; Main owns cadence and permission,
not progression strategy.
*/
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
        private bool _decisionPublishPending;
        private bool _pendingTransactionComplete;
        private string _pendingTransactionError = string.Empty;
        private DateTime _lastAutoEnterAttempt = DateTime.MinValue;
        private volatile bool _zoneReloadRequested;
        private volatile bool _settingsReloadRequested;
        private volatile bool _allocationReloadRequested;
        private volatile bool _allocationListReloadRequested;
        private static bool _isUnloading;
        private static readonly Queue<Action> MainThreadActions = new Queue<Action>();
        private static readonly Queue<Action> MainThreadLifecycleActions = new Queue<Action>();
        private static readonly object MainThreadActionsLock = new object();
        private static long _rejectionEpoch;
        private static bool _allocationOwnerKnown;
        private static bool _autopilotOwnsAllocations;

        internal static bool Test { get; set; }

        // WinForms events run on their own UI thread.  Unity controllers may only
        // be touched by the game thread, so every form-triggered mutation is queued
        // and drained from MonoBehaviour.Update().
        internal static void RunOnMainThread(Action action)
        {
            if (action == null) return;
            lock (MainThreadActionsLock) MainThreadActions.Enqueue(action);
        }

        internal static void RunLifecycleOnMainThread(Action action)
        {
            if (action == null) return;
            lock (MainThreadActionsLock) MainThreadLifecycleActions.Enqueue(action);
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

        private static void DrainLifecycleActions()
        {
            Action action;
            lock (MainThreadActionsLock)
            {
                if (MainThreadLifecycleActions.Count == 0) return;
                action = MainThreadLifecycleActions.Dequeue();
            }
            try { action(); }
            catch (Exception ex) { LogAction("REJECTED", "Queued lifecycle action failed: " + ex.Message); }
        }

        private static string _dir;
        private static string _profilesDir;
        private static string _sessionId = string.Empty;

        internal static string SessionId
        {
            get { return _sessionId; }
        }
        internal static string ActiveLocationSha256AtObservation { get; private set; } = string.Empty;
        internal static string DiskArtifactSha256 { get; private set; } = string.Empty;
        internal static string GameAssemblySha256 { get; private set; } = string.Empty;

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
            if (string.Equals(category, "REJECTED", StringComparison.OrdinalIgnoreCase))
                System.Threading.Interlocked.Increment(ref _rejectionEpoch);
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

        internal static AutopilotConfig CurrentAutopilotConfig
        {
            get { return Autopilot == null ? null : Autopilot.Config; }
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
                ExecutionSafety.Invalidate(ready
                    ? "active gameplay synchronization acquired"
                    : "active gameplay synchronization lost");
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
            using (ExecutionSafety.BeginCycle("gameplay synchronization", CurrentAutopilotConfig))
            {
                TryRunMutation("automatic Load Autosave", MutationClass.Synchronization,
                    MutationOwner.Autopilot, () =>
                    {
                        LogAction("SYNC", "Verified local autosave found; invoking the game's own Load Autosave controller");
                        Character.mainMenu.loadAutosave();
                    });
            }
        }

        private static CustomAllocation ActiveProfile
        {
            get
            {
                return Autopilot != null && Autopilot.CanExecuteSafe && Autopilot.Config != null
                       && Autopilot.Config.ManageAllocations && Autopilot.Profile != null
                    ? Autopilot.Profile : _profile;
            }
        }

        /*
        ALLOCATION WRITER OWNERSHIP

        The generated profile is a concrete mutation program, not passive planner telemetry. A
        stale generated instance must become unreachable the moment ManageAllocations is disabled;
        conversely the legacy profile must not race the generated writer. Refresh ownership after
        Autopilot.Tick has observed config changes and before opening the scheduler lease. The
        state-version bump invalidates any older pass that retained the previous profile reference.
        */
        private static void RefreshAllocationOwnership()
        {
            var owns = Autopilot != null && Autopilot.CanExecuteSafe && Autopilot.Config != null
                       && Autopilot.Config.ManageAllocations && Autopilot.Profile != null;
            if (!_allocationOwnerKnown)
            {
                _allocationOwnerKnown = true;
                _autopilotOwnsAllocations = owns;
                return;
            }
            if (_autopilotOwnsAllocations == owns) return;
            _autopilotOwnsAllocations = owns;
            ExecutionSafety.Invalidate("allocation profile ownership changed");
            LogAction("OWNERSHIP", owns
                ? "Autopilot generated profile acquired exclusive allocation ownership"
                : "Legacy selected profile acquired allocation ownership; generated profile invalidated");
        }

        private static MutationOwner AllocationOwner
        {
            get { return _autopilotOwnsAllocations ? MutationOwner.Autopilot : MutationOwner.Legacy; }
        }

        internal static bool HasExecutableAllocationOwner
        {
            get
            {
                return ActiveProfile != null && (_autopilotOwnsAllocations
                    || Settings != null && Settings.GlobalEnabled);
            }
        }

        private static CustomAllocation RebirthProfile(MutationOwner owner)
        {
            return owner == MutationOwner.Autopilot && Autopilot != null
                   && Autopilot.Config != null && Autopilot.Config.ManageAllocations
                   && Autopilot.Profile != null
                ? Autopilot.Profile : _profile;
        }

        private static bool TryRunMutation(string name, MutationClass mutationClass,
            MutationOwner owner, Action action)
        {
            MutationLease lease;
            string reason;
            if (!ExecutionSafety.TryAcquire(mutationClass, owner, out lease, out reason))
            {
                ExecutionSafety.ReportHold("lease:" + name + ":" + owner,
                    name + " held: " + reason);
                return false;
            }
            if (!lease.IsCurrent)
            {
                ExecutionSafety.ReportHold("stale-lease:" + name,
                    name + " held because its execution lease became stale");
                return false;
            }
            action();
            return true;
        }

        /*
        POST-GEAR ALLOCATION RESTORATION

        Native equipment swaps can lower resource caps, so the loadout transaction must reclaim
        Energy, Magic, and Resource 3 before swapping. Waiting for the next 0.2-second scheduler
        tick exposes a visibly empty allocation and can lose productive ticks. This entry point
        restores the currently authoritative profile synchronously after a verified swap/rollback;
        it never selects a profile or grants resources, and it remains gated by gameplay sync.
        */
        internal static void RestoreAllocationsAfterGearSwap()
        {
            if (!IsAutomationReady || ActiveProfile == null)
                return;
            TryRunMutation("post-gear allocation restoration", MutationClass.Allocation,
                AllocationOwner, () => ActiveProfile.DoAllocations());
        }

        internal void Unload()
        {
            _isUnloading = true;
            ExecutionSafety.Invalidate("assembly host is unloading");
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
                lock (MainThreadActionsLock)
                {
                    MainThreadActions.Clear();
                    MainThreadLifecycleActions.Clear();
                }
            }
            catch (Exception e)
            {
                Log(e.Message);
            }
            OutputWriter?.Close();
        }

        public void Start()
        {
            _isUnloading = false;
            _allocationOwnerKnown = false;
            ExecutionSafety.Invalidate("assembly host started");
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

                // Append-only session evidence survives reinjection, crash recovery, and rejected
                // duplicate-injection attempts. Truncating here erased the only audit trail for
                // irreversible actions. Every stream receives the same build/session boundary.
                OutputWriter = new StreamWriter(Path.Combine(logDir, "inject.log"), true) {AutoFlush = true};
                LootWriter = new StreamWriter(Path.Combine(logDir, "loot.log"), true) {AutoFlush = true};
                CombatWriter = new StreamWriter(Path.Combine(logDir, "combat.log"), true) {AutoFlush = true};
                AllocationWriter = new StreamWriter(Path.Combine(logDir, "allocation.log"), true) {AutoFlush = true};
                PitSpinWriter = new StreamWriter(Path.Combine(logDir, "pitspin.log"), true) {AutoFlush = true};
                ActionWriter = new StreamWriter(Path.Combine(logDir, "actions.log"), true) {AutoFlush = true};
                _sessionId = Guid.NewGuid().ToString("N");
                var sessionMarker = "=== SESSION " + DateTime.UtcNow.ToString("o") + " id "
                                    + _sessionId + " build "
                                    + typeof(Main).Assembly.ManifestModule.ModuleVersionId + " pid "
                                    + Process.GetCurrentProcess().Id + " ===";
                OutputWriter.WriteLine(sessionMarker);
                LootWriter.WriteLine(sessionMarker);
                CombatWriter.WriteLine(sessionMarker);
                AllocationWriter.WriteLine(sessionMarker);
                PitSpinWriter.WriteLine(sessionMarker);
                ActionWriter.WriteLine(sessionMarker);
                PublishDeploymentIdentity(assemblyDir, installDir);

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
                ExecutionSafety.ObserveConfig(Autopilot.Config);
                RefreshAllocationOwnership();

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
                    // Mono can report a relative path in Name, not only the leaf name.
                    if (!AutopilotPlan.IsGeneratedAllocationPath(args.Name)
                        && !AutopilotPlan.IsGeneratedAllocationPath(args.FullPath))
                        _allocationReloadRequested = true;
                };
                AllocationWatcher.Created += (sender, args) =>
                {
                    if (!AutopilotPlan.IsGeneratedAllocationPath(args.Name)
                        && !AutopilotPlan.IsGeneratedAllocationPath(args.FullPath))
                        _allocationListReloadRequested = true;
                };
                AllocationWatcher.Deleted += (sender, args) =>
                {
                    if (!AutopilotPlan.IsGeneratedAllocationPath(args.Name)
                        && !AutopilotPlan.IsGeneratedAllocationPath(args.FullPath))
                        _allocationListReloadRequested = true;
                };
                AllocationWatcher.Renamed += (sender, args) =>
                {
                    if (!AutopilotPlan.IsGeneratedAllocationPath(args.Name)
                        && !AutopilotPlan.IsGeneratedAllocationPath(args.FullPath))
                        _allocationListReloadRequested = true;
                };

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
                // Dispose every partially-created watcher/form/writer before the
                // unified loader destroys the named host. Otherwise a failed Start
                // permanently blocks reinjection while background callbacks survive.
                Unload();
                Loader.Unload();
            }
        }

        internal static void UpdateForm(SavedSettings newSettings)
        {
            settingsForm.UpdateFromSettings(newSettings);
        }

        public void Update()
        {
            if (_isUnloading) return;
            // Ejection must remain available when gameplay synchronization is lost;
            // ordinary settings/game mutations stay behind IsAutomationReady.
            DrainLifecycleActions();
            if (_isUnloading) return;
            if (IsAutomationReady)
                DrainMainThreadActions();
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
                using (ExecutionSafety.BeginCycle("manual quick equipment", CurrentAutopilotConfig))
                {
                var quickChanged = false;
                if (Settings.QuickLoadout.Length > 0)
                {
                    if (_tempSwapped)
                    {
                        Log("Restoring Previous Loadout");
                        quickChanged = TryRunMutation("manual loadout restoration",
                            MutationClass.Loadout, MutationOwner.User,
                            () => LoadoutManager.RestoreTempLoadout()) || quickChanged;
                    }
                    else
                    {
                        Log("Equipping Quick Loadout");
                        quickChanged = TryRunMutation("manual quick loadout",
                            MutationClass.Loadout, MutationOwner.User, () =>
                            {
                                LoadoutManager.SaveTempLoadout();
                                LoadoutManager.ChangeGear(Settings.QuickLoadout, false,
                                    MutationOwner.User);
                            }) || quickChanged;
                    }
                }

                if (Settings.QuickDiggers.Length > 0)
                {
                    if (_tempSwapped)
                    {
                        Log("Equipping Previous Diggers");
                        quickChanged = TryRunMutation("manual digger restoration", MutationClass.Diggers,
                            MutationOwner.User, () => DiggerManager.RestoreTempDiggers()) || quickChanged;
                    }
                    else
                    {
                        Log("Equipping Quick Diggers");
                        quickChanged = TryRunMutation("manual quick diggers", MutationClass.Diggers,
                            MutationOwner.User, () =>
                            {
                                DiggerManager.SaveTempDiggers();
                                DiggerManager.EquipDiggers(Settings.QuickDiggers);
                            }) || quickChanged;
                    }
                }

                if (quickChanged) _tempSwapped = !_tempSwapped;
                }
            }

            // F11 reserved for testing
            //if (Input.GetKeyDown(KeyCode.F11))
            //{
            //    Character.realExp += 10000;
            //}
        }

        private void QuickSave()
        {
            using (ExecutionSafety.BeginCycle("manual quicksave", CurrentAutopilotConfig))
            {
                TryRunMutation("manual quicksave and Steam Cloud write",
                    MutationClass.SaveLoad, MutationOwner.User, () =>
                    {
                        Log("Writing quicksave and json");
                        var data = Character.importExport.getBase64Data();
                        WriteTextAtomic(Path.Combine(_dir, "NGUSave.txt"), data + Environment.NewLine);

                        data = JsonUtility.ToJson(Character.importExport.gameStateToData());
                        WriteTextAtomic(Path.Combine(_dir, "NGUSave.json"), data + Environment.NewLine);

                        Character.saveLoad.saveGamestateToSteamCloud();
                    });
            }
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

        /*
        ACTIVE-VERSUS-DISK DEPLOYMENT IDENTITY

        Unity keeps the injected assembly in memory even when a newer DLL replaces the install
        artifact. Publish the active module MVID and hash separately from the expected disk DLL so
        operators never infer deployment from a timestamp alone. This is observational filesystem
        telemetry: it does not load the disk assembly, inject, restart, or touch game/save state.
        */
        private static void PublishDeploymentIdentity(string assemblyDir, string installDir)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var activePath = assembly.Location ?? string.Empty;
                var diskPath = Path.Combine(installDir ?? assemblyDir ?? string.Empty,
                    "NGUIdleAutopilot.dll");
                // Assembly.Location can point at a DLL that was replaced after injection;
                // its current bytes are disk evidence, never the already-loaded image.
                var activeLocationHash = File.Exists(activePath) ? Sha256(activePath) : string.Empty;
                var diskHash = File.Exists(diskPath) ? Sha256(diskPath) : string.Empty;
                var activeInfo = File.Exists(activePath) ? new FileInfo(activePath) : null;
                var diskInfo = File.Exists(diskPath) ? new FileInfo(diskPath) : null;
                var process = Process.GetCurrentProcess();
                var gameAssembly = typeof(Character).Assembly;
                var gamePath = gameAssembly.Location ?? string.Empty;
                var gameHash = File.Exists(gamePath) ? Sha256(gamePath) : string.Empty;
                ActiveLocationSha256AtObservation = activeLocationHash;
                DiskArtifactSha256 = diskHash;
                GameAssemblySha256 = gameHash;
                var activeBuild = assembly.ManifestModule.ModuleVersionId.ToString();
                var handshake = process.Id + ":" + _sessionId + ":" + activeBuild;
                var json = "{\n"
                           + "  \"schemaVersion\": 1,\n"
                           + "  \"observedAt\": \"" + DateTime.UtcNow.ToString("o") + "\",\n"
                           + "  \"producerPid\": " + process.Id + ",\n"
                           + "  \"producerProcessStartUtc\": \"" + process.StartTime.ToUniversalTime().ToString("o") + "\",\n"
                           + "  \"producerSessionId\": \"" + _sessionId + "\",\n"
                           + "  \"telemetryHandshake\": \"" + handshake + "\",\n"
                           + "  \"activeBuildId\": \"" + activeBuild + "\",\n"
                           + "  \"activeAssemblyPath\": \"" + JsonEscape(activePath) + "\",\n"
                           + "  \"activeLocationSha256AtObservation\": \"" + activeLocationHash + "\",\n"
                           + "  \"activeAssemblyBytes\": " + (activeInfo == null ? -1 : activeInfo.Length) + ",\n"
                           + "  \"diskArtifactPath\": \"" + JsonEscape(diskPath) + "\",\n"
                           + "  \"diskArtifactSha256\": \"" + diskHash + "\",\n"
                           + "  \"diskArtifactBytes\": " + (diskInfo == null ? -1 : diskInfo.Length) + ",\n"
                           + "  \"diskArtifactModifiedUtc\": \""
                           + (diskInfo == null ? string.Empty : diskInfo.LastWriteTimeUtc.ToString("o")) + "\",\n"
                           + "  \"gameAssemblyPath\": \"" + JsonEscape(gamePath) + "\",\n"
                           + "  \"gameAssemblySha256\": \"" + gameHash + "\",\n"
                           + "  \"activeImageHashAvailable\": false,\n"
                           + "  \"activeMatchesDisk\": \"unknown-until-reinjection-build-id-verification\"\n"
                           + "}\n";
                WriteTextAtomic(Path.Combine(_dir, "deployment.json"), json);
            }
            catch (Exception e)
            {
                LogAction("SYSTEM", "Deployment identity telemetry unavailable: " + e.Message);
            }
        }

        private static string Sha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string JsonEscape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n");
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

                using (ExecutionSafety.BeginCycle("manual quickload", CurrentAutopilotConfig))
                {
                    TryRunMutation("manual quickload", MutationClass.SaveLoad, MutationOwner.User, () =>
                    {
                        // Publish the pause before the game's load mutates controllers. All
                        // repeating bot routines remain behind this barrier until the normal
                        // gameplay synchronization probe verifies the new save is active.
                        _syncStateInitialized = true;
                        _lastGameplayReady = false;
                        if (Autopilot != null)
                            Autopilot.ReportSynchronization(false, "quicksave load is in progress; waiting for a verified gameplay state");
                        Character.saveLoad.loadintoGame(saveDataFromString);
                        ExecutionSafety.Invalidate("manual quickload began");
                    });
                }
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

            RefreshAllocationOwnership();
            using (ExecutionSafety.BeginCycle("fast allocation", CurrentAutopilotConfig))
            {
                try
                {
                    if ((Settings.ManageInventory || AutopilotWants(x => x.ManageInventory || x.ManageAdventure || x.ManageBosses))
                        && !Controller.midDrag)
                        TryRunMutation("fast progression loadout", MutationClass.Loadout,
                            ExecutionSafety.OwnerFor(MutationClass.Loadout),
                            () => ProgressionLoadoutOptimizer.Manage());
                    if (ActiveProfile != null && AutopilotWants(x => x.ManageAllocations))
                        TryRunMutation("fast allocations", MutationClass.Allocation,
                            AllocationOwner, () => ActiveProfile.DoAllocations());
                }
                catch (Exception e)
                {
                    Log("Fast allocation error: " + e.Message);
                    _pendingTransactionComplete = false;
                    _pendingTransactionError = string.IsNullOrEmpty(_pendingTransactionError)
                        ? "Fast allocation " + e.GetType().Name + ": " + e.Message
                        : _pendingTransactionError + "; fast allocation " + e.GetType().Name + ": " + e.Message;
                }
            }

            /*
            SETTLED-STATE PUBLICATION BARRIER

            Inventory/loadout work can reclaim Energy, Magic, and Resource 3 near the end of the
            one-second transaction. The fast allocator restores those pools on this 0.2-second
            cadence. Publishing between the two made a correct bot look completely idle. Emit the
            pending decision only after this sweep, when all cooperating mutation loops have
            reached their stable state. Any allocation failure marks the cycle partial.
            */
            if (_decisionPublishPending && Autopilot != null)
            {
                try
                {
                    Autopilot.PublishDecisionAfterAutomation(_pendingTransactionComplete,
                        _pendingTransactionError);
                    _decisionPublishPending = false;
                }
                catch (Exception e)
                {
                    Log("Settled decision publish failed: " + e.Message);
                }
            }
        }

        // Stuff on a very short timer
        void QuickStuff()
        {
            if (!IsAutomationReady)
                return;
            if (!Settings.GlobalEnabled && (Autopilot == null || !Autopilot.CanExecuteSafe))
                return;

            RefreshAllocationOwnership();
            using (ExecutionSafety.BeginCycle("quick combat and rewards", CurrentAutopilotConfig))
            {

            //Turn on autoattack if we're in ITOPOD and its not on
            if (Settings.AutoQuestITOPOD && Character.adventureController.zone >= 1000 && !Character.adventure.autoattacking && !Settings.CombatEnabled)
            {
                TryRunMutation("ITOPOD autoattack toggle", MutationClass.Combat,
                    MutationOwner.Legacy, () => Character.adventureController.idleAttackMove.setToggle());
            }

            if (Settings.AutoFight || AutopilotWants(x => x.ManageBosses))
            {
                var combatOwner = AutopilotWants(x => x.ManageBosses)
                    ? MutationOwner.Autopilot : MutationOwner.Legacy;
                MutationLease combatLease;
                string combatHold;
                if (!ExecutionSafety.TryAcquire(MutationClass.Combat, combatOwner,
                    out combatLease, out combatHold) || !combatLease.IsCurrent)
                {
                    ExecutionSafety.ReportHold("lease:quick-boss-combat",
                        "Boss combat held: " + (string.IsNullOrEmpty(combatHold)
                            ? "execution lease became stale" : combatHold));
                    return;
                }
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
                    if (ActiveProfile != null)
                        TryRunMutation("post-boss allocations", MutationClass.Allocation,
                            AllocationOwner, () => ActiveProfile.DoAllocations());
                }
            }

            if (Settings.AutoMoneyPit || AutopilotWants(x => x.ManageMoneyPit))
            {
                var autopilotPit = AutopilotWants(x => x.ManageMoneyPit);
                TryRunMutation("Money Pit", MutationClass.MoneyPit,
                    autopilotPit ? MutationOwner.Autopilot : MutationOwner.Legacy,
                    // Full autopilot's shared Gold ledger inside MoneyPitManager is the only
                    // reserve authority. 1e5 is the native minimum toss, not a second policy.
                    () => MoneyPitManager.CheckMoneyPit(autopilotPit
                        ? 1e5 : Settings.MoneyPitThreshold));
            }

            if (Settings.AutoSpin || AutopilotWants(x => x.ManageDailySpin))
            {
                TryRunMutation("daily spin", MutationClass.DailySpin,
                    AutopilotWants(x => x.ManageDailySpin)
                        ? MutationOwner.Autopilot : MutationOwner.Legacy,
                    () => MoneyPitManager.DoDailySpin());
            }

            if (Settings.AutoQuestITOPOD)
            {
                TryRunMutation("legacy ITOPOD routing", MutationClass.Adventure,
                    MutationOwner.Legacy, MoveToITOPOD);
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
                TryRunMutation("Blood spell automation toggles", MutationClass.BloodMagic,
                    autopilotBlood ? MutationOwner.Autopilot : MutationOwner.Legacy, () =>
                    {
                        Character.bloodMagic.rebirthAutoSpell = !autopilotBlood
                                                                 && Settings.BloodNumberThreshold > 0 && number < Settings.BloodNumberThreshold;
                        Character.bloodMagic.goldAutoSpell = !autopilotBlood
                                                              && Settings.CounterfeitThreshold > 0 && counterfeit < Settings.CounterfeitThreshold;
                        Character.bloodMagic.lootAutoSpell = !autopilotBlood
                                                              && Settings.SpaghettiThreshold > 0 && spaghetti < Settings.SpaghettiThreshold;
                        Character.bloodSpells.updateGoldToggleState();
                        Character.bloodSpells.updateLootToggleState();
                        Character.bloodSpells.updateRebirthToggleState();
                    });
            }
            }
        }

        // Runs every second; tactical combat and allocations have separate faster loops.
        void AutomationRoutine()
        {
            var transactionComplete = false;
            var transactionError = string.Empty;
            var transactionErrors = new List<string>();
            var rejectionEpochBefore = System.Threading.Interlocked.Read(ref _rejectionEpoch);
            ExecutionCycle executionCycle = null;
            long executionStateVersion = -1;
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
                RefreshAllocationOwnership();
                executionCycle = ExecutionSafety.BeginCycle("one-second automation transaction",
                    CurrentAutopilotConfig);
                executionStateVersion = ExecutionSafety.StateVersion;

                if (!Settings.GlobalEnabled && (Autopilot == null || !Autopilot.CanExecuteSafe))
                {
                    _timeLeft = 1f;
                    return;
                }

                RunAutomationStep("ITOPOD routing", MutationClass.Adventure,
                    MutationOwner.Legacy, () => ZoneHelpers.OptimizeITOPOD(), transactionErrors);

                if (AutopilotWants(x => x.ManageBeards))
                    RunAutomationStep("Beards", MutationClass.Beards,
                        MutationOwner.Autopilot, () => BeardManager.Manage(), transactionErrors);

                RunAutomationStep("Inventory", MutationClass.Inventory,
                    AutopilotWants(x => x.ManageInventory)
                        ? MutationOwner.Autopilot : MutationOwner.Legacy, () =>
                {
                    if ((Settings.ManageInventory || AutopilotWants(x => x.ManageInventory)) && !Controller.midDrag)
                    {
                        var converted = Character.inventory.GetConvertedInventory().ToArray();
                        _invManager.EnsureFiltered(converted);
                        // A transient event loadout keeps exact physical references
                        // for rollback. While that lock is held, any merge, boost,
                        // conversion, trash, or daycare move could consume one of
                        // those temporarily unequipped objects and make restoration
                        // impossible. Filters remain fail-closed, but topology waits.
                        if (LoadoutManager.CurrentLock != LockType.None)
                            return;
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
                        var immediateBoostSlots = _invManager.GetImmediateBoostSlots(converted);
                        _invManager.BoostInventory(immediateBoostSlots);
                        converted = Character.inventory.GetConvertedInventory().ToArray();
                        _invManager.BoostInfinityCubeToSoftcaps();
                        converted = Character.inventory.GetConvertedInventory().ToArray();
                        var boostSlots = _invManager.GetBoostSlots(converted);
                        _invManager.BoostInventory(boostSlots);
                        _invManager.BoostInfinityCube();
                        converted = Character.inventory.GetConvertedInventory().ToArray();
                        _invManager.ManageBoostConversion();

                        // Re-evaluate the whole equipped set after merges/boosts because
                        // those operations can change both candidate stats and legality.
                        ProgressionLoadoutOptimizer.Manage();
                    }
                }, transactionErrors);

                if (AutopilotWants(x => x.AllowEndSequence))
                    RunAutomationStep("terminal END placement and trigger", MutationClass.EndSequence,
                        MutationOwner.Autopilot, () => _invManager.TryExecuteEndSequence(),
                        transactionErrors);

                // MacGuffin selection occurs inside the inventory transaction. Cast permanent
                // MacGuffin Blood spells only afterward so their levels compound the chosen set.
                if (AutopilotWants(x => x.ManageBloodMagic))
                    RunAutomationStep("Blood spell policy", MutationClass.BloodMagic,
                        MutationOwner.Autopilot, () => Autopilot.ManageBloodSpell(), transactionErrors);

                // Daycare timers and completed-item rotation are independent of bulk
                // inventory manipulation, so a drag or disabled merge policy must not
                // stall this permanent progression system.
                if (AutopilotWants(x => x.ManageInventory) && !Controller.midDrag
                    && LoadoutManager.CurrentLock == LockType.None)
                    RunAutomationStep("Daycare", MutationClass.Daycare,
                        MutationOwner.Autopilot, () => DaycareManager.Manage(), transactionErrors);

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
                    || AutopilotWants(x => x.ManageAdventure)
                    || Settings.ManageGoldLoadouts && Settings.NeedsGoldSwap())
                {
                    RunAutomationStep("Titan loadout", MutationClass.TitanLoadout,
                        ExecutionSafety.OwnerFor(MutationClass.TitanLoadout),
                        () => LoadoutManager.TryTitanSwap(), transactionErrors);
                    // Do not let the clock alone stage/lock Titan diggers. Gear preflight
                    // either acquired the Titan lock or the currently equipped set already
                    // satisfies the authoritative combat gate.
                    var titanDiggerOwnerEnabled = AutopilotWants(x => x.ManageDiggers)
                                                  || Settings.GlobalEnabled && Settings.ManageDiggers;
                    if (titanDiggerOwnerEnabled && (LoadoutManager.CurrentLock == LockType.Titan
                        || ZoneHelpers.HighestAvailableTitan() >= 0))
                        RunAutomationStep("Titan diggers", MutationClass.Diggers,
                            ExecutionSafety.OwnerFor(MutationClass.Diggers),
                            () => DiggerManager.TryTitanSwap(), transactionErrors);
                }

                if ((Settings.ManageYggdrasil || AutopilotWants(x => x.ManageYggdrasil)) && Character.buttons.yggdrasil.interactable)
                {
                    RunAutomationStep("Yggdrasil", MutationClass.Yggdrasil,
                        AutopilotWants(x => x.ManageYggdrasil)
                            ? MutationOwner.Autopilot : MutationOwner.Legacy, () =>
                    {
                        _yggManager.ManageYggHarvest();
                        _yggManager.CheckFruits();
                    }, transactionErrors);
                }

                if (Settings.AutoBuyEM && Character.highestBoss >= 17)
                {
                    MutationLease purchaseLease;
                    string purchaseHold;
                    if (!ExecutionSafety.TryAcquire(MutationClass.PermanentSpend,
                        MutationOwner.Legacy, out purchaseLease, out purchaseHold)
                        || !purchaseLease.IsCurrent)
                    {
                        ExecutionSafety.ReportHold("lease:legacy-em-purchases",
                            "Legacy Energy/Magic/R3 purchases held: "
                            + (string.IsNullOrEmpty(purchaseHold)
                                ? "execution lease became stale" : purchaseHold));
                    }
                    else
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
                }

                if (!AutopilotWants(x => x.ManageAllocations) && Settings.GlobalEnabled
                    && ActiveProfile != null)
                    RunAutomationStep("Allocations", MutationClass.Allocation,
                        MutationOwner.Legacy, () => ActiveProfile.DoAllocations(), transactionErrors);

                // Full autopilot already made one conservation-aware Blood decision
                // after MacGuffin selection.  Running the legacy threshold caster as
                // well can spend the remainder on a second spell in the same sweep.
                if (Settings.CastBloodSpells && !AutopilotWants(x => x.ManageBloodMagic))
                {
                    if (ActiveProfile != null)
                    RunAutomationStep("Blood spells", MutationClass.BloodMagic,
                        MutationOwner.Legacy, () => ActiveProfile.CastBloodSpells(), transactionErrors);
                }

                if ((Settings.AutoQuest || AutopilotWants(x => x.ManageQuests)) && Character.buttons.beast.interactable)
                {
                    RunAutomationStep("Quests", MutationClass.Quests,
                        AutopilotWants(x => x.ManageQuests)
                            ? MutationOwner.Autopilot : MutationOwner.Legacy, () =>
                    {
                        if (!Character.inventoryController.midDrag)
                        {
                            var converted = Character.inventory.GetConvertedInventory().ToArray();
                            _invManager.ManageQuestItems(converted);
                            _questManager.CheckQuestTurnin();
                            _questManager.ManageQuests();
                        }
                    }, transactionErrors);
                }

                // Rebirth is the transaction commit boundary. Exceptions and normal-return
                // REJECTED mutations both leave the live snapshot uncertain, so preserve the
                // run and retry after a clean full sweep.
                if (System.Threading.Interlocked.Read(ref _rejectionEpoch) != rejectionEpochBefore)
                    transactionErrors.Add("one or more native mutations were rejected during this sweep");
                if (executionStateVersion != ExecutionSafety.StateVersion)
                    transactionErrors.Add("execution state changed during this sweep; commit lease invalidated");
                if (transactionErrors.Count == 0 && (Settings.AutoRebirth
                    || Autopilot != null && Autopilot.CanExecuteIrreversible && Autopilot.Config.AllowRebirths))
                {
                    var rebirthOwner = Autopilot != null && Autopilot.CanExecuteIrreversible
                                      && Autopilot.Config.AllowRebirths
                        ? MutationOwner.Autopilot : MutationOwner.Legacy;
                    var rebirthProfile = RebirthProfile(rebirthOwner);
                    if (rebirthProfile != null)
                        RunAutomationStep("Rebirth", MutationClass.Rebirth, rebirthOwner,
                            () => rebirthProfile.DoRebirth(), transactionErrors);
                }
                if (System.Threading.Interlocked.Read(ref _rejectionEpoch) != rejectionEpochBefore
                    && transactionErrors.Count == 0)
                    transactionErrors.Add("the rebirth/challenge mutation was rejected");
                transactionComplete = transactionErrors.Count == 0;
                transactionError = string.Join(" | ", transactionErrors.ToArray());
            }
            catch (Exception e)
            {
                transactionError = e.GetType().Name + ": " + e.Message;
                Log(e.Message);
                Log(e.StackTrace);
            }
            finally
            {
                if (executionCycle != null) executionCycle.Dispose();
            }
            // Queue publication for the next fast-allocation sweep. Gear work above may
            // have reclaimed resource pools that are intentionally restored on that cadence.
            _pendingTransactionComplete = transactionComplete;
            _pendingTransactionError = transactionError;
            _decisionPublishPending = Autopilot != null;
            _timeLeft = 1f;
        }

        private static void RunAutomationStep(string name, MutationClass mutationClass,
            MutationOwner owner, Action action, ICollection<string> errors)
        {
            try
            {
                TryRunMutation(name, mutationClass, owner, action);
            }
            catch (Exception e)
            {
                var detail = name + ": " + e.GetType().Name + ": " + e.Message;
                errors.Add(detail);
                Log(detail);
                Log(e.StackTrace);
                LogAction("REJECTED", name + " subsystem was quarantined for this sweep: "
                                      + e.GetType().Name + ": " + e.Message);
            }
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
            ExecutionSafety.Invalidate("legacy allocation profile reloaded");
            RefreshAllocationOwnership();
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
                    ExecutionSafety.Invalidate("legacy settings reloaded");
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

            RefreshAllocationOwnership();
            using (ExecutionSafety.BeginCycle("Adventure routing", CurrentAutopilotConfig))
            {
            var adventureOwner = AutopilotWants(x => x.ManageAdventure)
                ? MutationOwner.Autopilot : MutationOwner.Legacy;
            MutationLease adventureLease;
            string adventureHold;
            if (!ExecutionSafety.TryAcquire(MutationClass.Adventure, adventureOwner,
                out adventureLease, out adventureHold) || !adventureLease.IsCurrent)
            {
                ExecutionSafety.ReportHold("lease:adventure-routing",
                    "Adventure routing held: " + (string.IsNullOrEmpty(adventureHold)
                        ? "execution lease became stale" : adventureHold));
                return;
            }

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
            var dryRunVeto = Autopilot != null && Autopilot.Config != null
                             && Autopilot.Config.Enabled && Autopilot.Config.IsDryRun;
            var effectiveActive = !dryRunVeto
                                  && (Settings.GlobalEnabled || Autopilot != null && Autopilot.CanExecuteSafe);
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
