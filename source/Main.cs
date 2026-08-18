using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.CompilerServices;
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

Main is the Unity orchestration, lifecycle-epoch, save/load, and native-mutation dispatch host. It
publishes its instance before callbacks, discovers/rebinds live controllers, establishes the
active-game synchronization barrier, keys queued work and decision latches to GameEpoch, selects
exactly one allocation owner, and writes confirmed action/deployment telemetry. Inputs are Unity
state, legacy settings, autopilot config/plans, watched files, and manual snapshot keys; outputs are
leased manager/controller calls, durable last-good snapshot generations, and append-only runtime
evidence. No mutation may run before a synchronized epoch has a newly installed plan; reset/load/
unload synchronously cancel old work; dry-run and assist cannot inherit stronger legacy authority.
Focused managers own mechanics/native postconditions. Main owns lifecycle, cadence, and permission,
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
        private sealed class PendingDecisionPublication
        {
            internal bool TransactionComplete;
            internal string TransactionError;
        }
        private readonly EpochLatch<PendingDecisionPublication> _pendingDecision =
            new EpochLatch<PendingDecisionPublication>();
        private DateTime _lastAutoEnterAttempt = DateTime.MinValue;
        private volatile bool _zoneReloadRequested;
        private volatile bool _settingsReloadRequested;
        private volatile bool _allocationReloadRequested;
        private volatile bool _allocationListReloadRequested;
        private static bool _isUnloading;
        private static readonly EpochActionQueue MainThreadActions = new EpochActionQueue();
        private static readonly EpochActionQueue MainThreadLifecycleActions = new EpochActionQueue();
        private static long _rejectionEpoch;
        private static bool _allocationOwnerKnown;
        private static bool _autopilotOwnsAllocations;
        private string _lastRunSignature = string.Empty;

        internal static bool Test { get; set; }

        // WinForms events run on their own UI thread. Unity controllers may only be touched by the
        // game thread, so every form-triggered mutation captures its enqueue epoch and is drained
        // from MonoBehaviour.Update(). Save-bound work is discarded after reset/load; lifecycle
        // work survives those transitions but cannot cross host replacement/unload.
        internal static void RunOnMainThread(Action action)
        {
            MainThreadActions.Enqueue(GameEpochController.Shared.Current,
                EpochWorkScope.ExactGameState, action);
        }

        internal static void RunLifecycleOnMainThread(Action action)
        {
            MainThreadLifecycleActions.Enqueue(GameEpochController.Shared.Current,
                EpochWorkScope.HostSession, action);
        }

        private static void DrainMainThreadActions()
        {
            MainThreadActions.Drain(GameEpochController.Shared.Current, 32,
                reason => LogAction("HOLD", "Discarded queued settings action: " + reason),
                ex => LogAction("REJECTED", "Queued settings action failed: " + ex.Message));
        }

        private static void DrainLifecycleActions()
        {
            MainThreadLifecycleActions.Drain(GameEpochController.Shared.Current, 1,
                reason => LogAction("HOLD", "Discarded queued lifecycle action: " + reason),
                ex => LogAction("REJECTED", "Queued lifecycle action failed: " + ex.Message));
        }

        private static string _dir;
        private static string _profilesDir;
        private static string _sessionId = string.Empty;

        internal static string SessionId
        {
            get { return _sessionId; }
        }
        internal static string CurrentGameEpochFingerprint
        {
            get { return GameEpochController.Shared.Current.Fingerprint; }
        }

        // Multi-frame managers register unconditional cleanup (for example key-up) here. The
        // callback is bound to the current exact game epoch and is invoked synchronously before a
        // reset, load, quarantine, or unload publishes the successor epoch.
        internal static void RegisterEpochCancellation(string id, Action cancellation)
        {
            GameEpochController.Shared.RegisterCancellation(id, cancellation);
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

        internal static void LogDiagnostic(string msg)
        {
            var rebirth = Character == null || Character.rebirthTime == null
                ? 0 : Math.Floor(Character.rebirthTime.totalseconds);
            var line = $"{ DateTime.Now.ToShortDateString()}-{ DateTime.Now.ToShortTimeString()} ({rebirth}s): {msg}";
            if (OutputWriter != null) OutputWriter.WriteLine(line);
            else System.Diagnostics.Debug.WriteLine(line);
        }

        internal static void Log(string msg)
        {
            LogDiagnostic(msg);
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
                       && reference._lastGameplayReady
                       && GameEpochController.Shared.MutationOpen;
            }
        }

        private void GameplaySyncRoutine()
        {
            if (Character == null || Autopilot == null || Autopilot.Config == null)
                return;

            var ready = IsGameplayReady;
            string epochReason;
            if (ready)
                GameEpochController.Shared.ObserveSynchronizedFrame(
                    CaptureControllerIdentity(), out epochReason);
            else
                epochReason = "main menu is visible";
            var detail = ready
                ? "active gameplay verified by MainMenuController.doneInitialLoad and hidden menu transform; "
                  + (GameEpochController.Shared.MutationOpen
                      ? "game epoch and plan are active"
                      : GameEpochController.Shared.HoldReason)
                : "main menu is still visible; all game mutations are hard-paused";
            Autopilot.ReportSynchronization(ready, detail);

            if (!_syncStateInitialized || ready != _lastGameplayReady)
            {
                LogAction("SYNC", ready
                    ? (GameEpochController.Shared.MutationOpen
                        ? "Active gameplay and current-epoch plan verified; automation enabled"
                        : "Active gameplay verified; automation remains held by "
                          + GameEpochController.Shared.Phase + ": "
                          + GameEpochController.Shared.HoldReason)
                    : "Main menu detected; automation paused");
                ExecutionSafety.Invalidate(ready
                    ? "active gameplay synchronization acquired"
                    : "active gameplay synchronization lost");
                _syncStateInitialized = true;
                _lastGameplayReady = ready;
            }

            if (ready && !string.IsNullOrEmpty(epochReason)
                && GameEpochController.Shared.Phase == GameEpochPhase.Quarantined)
                LogAction("REJECTED", "Gameplay synchronization could not reopen automation: "
                                      + epochReason);

            if (ready || !Autopilot.Config.Enabled || !Autopilot.Config.IsFull || !Autopilot.Config.AutoEnterGame)
                return;
            if ((DateTime.Now - _lastAutoEnterAttempt).TotalSeconds < 5)
                return;
            if (!Character.mainMenu.getlocalSaveValidity())
                return;

            _lastAutoEnterAttempt = DateTime.Now;
            TryAutoEnterVerifiedLocalSave();
        }

        private void TryAutoEnterVerifiedLocalSave()
        {
            SaveData localSave;
            PlayerData expectedData;
            try
            {
                localSave = Character.mainMenu.getlocalSave();
                if (localSave == null || string.IsNullOrEmpty(localSave.playerData)
                    || string.IsNullOrEmpty(localSave.checksum))
                {
                    LogAction("HOLD", "Autosave entry held: the native local-save envelope is incomplete");
                    return;
                }

                var checksum = Character.importExport.getMD5Hash(localSave.playerData);
                if (!string.Equals(checksum, localSave.checksum, StringComparison.Ordinal))
                {
                    LogAction("REJECTED", "Autosave entry rejected before mutation: checksum mismatch");
                    return;
                }

                expectedData = BinaryFormatterExtensions.DeserializePlayerDataFromString(
                    new BinaryFormatter(), localSave.playerData);
                if (expectedData == null || expectedData.version < 361
                    || expectedData.version > Character.getVersion())
                {
                    LogAction("REJECTED", "Autosave entry rejected before mutation: invalid save graph or version");
                    return;
                }
            }
            catch (Exception validationError)
            {
                LogAction("REJECTED", "Autosave entry rejected during prevalidation: "
                                      + validationError.GetType().Name + ": "
                                      + validationError.Message);
                return;
            }

            var expected = CaptureImportedSaveFingerprint(expectedData);
            var before = TryCaptureLiveSaveFingerprint();
            var gameHash = GameAssemblySha256;
            if (string.IsNullOrEmpty(gameHash)
                && File.Exists(typeof(Character).Assembly.Location))
                gameHash = Sha256(typeof(Character).Assembly.Location);
            var registry = NativeBindingRegistry.Create(typeof(Character).Assembly, gameHash);
            if (!registry.IrreversibleActionsEnabled
                || !registry.HasBinding(NativeBindingKeys.LoadIntoGame))
            {
                LogAction("HOLD", "Autosave entry held before epoch transition: "
                                  + (registry.IsKnownBuild
                                      ? registry.FailureFor(NativeBindingKeys.LoadIntoGame)
                                      : registry.BuildFailureReason));
                return;
            }

            var native = registry.CreateMutationAdapters();
            var loadingEpoch = GameEpochController.Shared.BeginLoad(
                "verified native autosave load is in progress");
            _syncStateInitialized = true;
            _lastGameplayReady = false;
            _pendingDecision.Clear();
            Autopilot.ReportSynchronization(false,
                "verified native autosave load is in progress; waiting for exact postconditions");
            ExecutionSafety.Invalidate("verified autosave load epoch began");
            Autopilot.TryTitan7PuzzleStep();
            LoadoutManager.ReleaseLock();
            DiggerManager.ReleaseLock();

            try
            {
                var invocation = native.LoadSave(Character.saveLoad, localSave);
                var nativeTrue = invocation.ReturnedNormally
                                 && invocation.ReturnValue is bool
                                 && (bool)invocation.ReturnValue;
                if (!nativeTrue)
                {
                    var failureAfter = TryCaptureLiveSaveFingerprint();
                    var unchanged = before != null && failureAfter != null
                                    && string.Equals(before.ContentHash,
                                        failureAfter.ContentHash,
                                        StringComparison.Ordinal);
                    var failure = invocation.Status + ": " + invocation.Reason
                                  + (invocation.Exception == null ? string.Empty
                                      : "; " + invocation.Exception.GetType().Name + ": "
                                        + invocation.Exception.Message)
                                  + (unchanged ? "; exact before bytes retained"
                                      : "; live state changed or could not be recaptured");
                    GameEpochController.Shared.FailLoad(loadingEpoch, failure, failureAfter);
                    LogAction("REJECTED", "Autosave load quarantined: " + failure);
                    return;
                }

                string rebindError;
                if (!TryRebindGameControllers(out rebindError))
                {
                    GameEpochController.Shared.FailLoad(loadingEpoch,
                        "native load returned true but controller rebind failed: "
                        + rebindError, TryCaptureLiveSaveFingerprint());
                    LogAction("REJECTED", "Autosave load quarantined after native true: "
                                          + rebindError);
                    return;
                }

                var afterSerialized = Character.importExport.getBase64Data();
                var after = CaptureLiveSaveFingerprint(afterSerialized, Character);
                string commitError;
                if (!GameEpochController.Shared.CommitLoad(loadingEpoch, true,
                        expected, after, CaptureControllerIdentity(), out commitError))
                {
                    LogAction("REJECTED", "Autosave load returned true but its exact "
                                          + "postcondition failed; automation quarantined: "
                                          + commitError);
                    return;
                }

                _lastRunSignature = after.RunSignature;
                try
                {
                    RecreateEpochBoundManagers();
                    Character.mainMenu.finishMainMenu();
                }
                catch (Exception activationError)
                {
                    GameEpochController.Shared.Quarantine(
                        "autosave committed but activation failed: "
                        + activationError.GetType().Name + ": " + activationError.Message);
                    LogAction("REJECTED", "Autosave committed but activation was quarantined: "
                                          + activationError.Message);
                    return;
                }

                _syncStateInitialized = true;
                _lastGameplayReady = false;
                ExecutionSafety.Invalidate("verified autosave load committed a new save epoch");
                LogAction("SAVE", "Verified native autosave committed as "
                                  + GameEpochController.Shared.Current.Fingerprint
                                  + "; waiting for a later synchronized frame and plan");
            }
            catch (Exception loadError)
            {
                if (GameEpochController.Shared.Phase == GameEpochPhase.Loading)
                    GameEpochController.Shared.FailLoad(loadingEpoch,
                        "autosave load threw after its epoch closed: "
                        + loadError.GetType().Name + ": " + loadError.Message,
                        TryCaptureLiveSaveFingerprint());
                else
                    GameEpochController.Shared.Quarantine(
                        "autosave load threw after commit: "
                        + loadError.GetType().Name + ": " + loadError.Message);
                _syncStateInitialized = true;
                _lastGameplayReady = false;
                LogAction("REJECTED", "Autosave load failed: " + loadError.Message);
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
            ExecutionSafety.ReportHold("typed-intent:post-gear-allocation",
                "Post-gear allocation restoration is held until allocation exposes an exact typed child intent.");
        }

        internal void Unload()
        {
            if (_isUnloading) return;
            _isUnloading = true;
            _syncStateInitialized = true;
            _lastGameplayReady = false;
            GameEpochController.Shared.BeginUnload("assembly host is unloading");
            ExecutionSafety.Invalidate("assembly host is unloading");
            try
            {
                // The current Titan-7 implementation releases its pending key whenever automation
                // is paused. Invoke it while its native P/Invoke state still exists; newer
                // multi-frame managers use RegisterEpochCancellation and were already cancelled by
                // BeginUnload above.
                try
                {
                    if (Autopilot != null) Autopilot.TryTitan7PuzzleStep();
                }
                catch (Exception keyError)
                {
                    LogAction("REJECTED", "Pending key release failed during unload: "
                                          + keyError.Message);
                }
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
                MainThreadActions.Clear();
                MainThreadLifecycleActions.Clear();
                _pendingDecision.Clear();
            }
            catch (Exception e)
            {
                Log(e.Message);
            }
            OutputWriter?.Close();

            // Mono may keep this assembly/static graph alive after Unity destroys the host.
            // Explicitly release every object which could otherwise be mistaken for the next
            // injection's controller/session identity.
            if (reference == this) reference = null;
            MutationCoordinator.BindSharedEpochProvider(null);
            Character = null;
            Controller = null;
            PlayerController = null;
            Autopilot = null;
            WishManager = null;
            settingsForm = null;
            ConfigWatcher = null;
            AllocationWatcher = null;
            ZoneWatcher = null;
            OutputWriter = null;
            LootWriter = null;
            CombatWriter = null;
            AllocationWriter = null;
            PitSpinWriter = null;
            ActionWriter = null;
            _profile = null;
            _sessionId = string.Empty;
            _allocationOwnerKnown = false;
            _autopilotOwnsAllocations = false;
        }

        public void Awake()
        {
            if (reference != null && reference != this)
                throw new InvalidOperationException(
                    "A different NGU Autopilot Main instance is already published");
            // AddComponent invokes Awake synchronously. Publishing here guarantees that Start and
            // every later zero-delay InvokeRepeating callback see the actual owning instance.
            reference = this;
            MutationCoordinator.BindSharedEpochProvider(
                () => CurrentGameEpochFingerprint);
        }

        public void Start()
        {
            _isUnloading = false;
            _allocationOwnerKnown = false;
            string assemblyDir = null;
            string installDir = null;
            ExecutionSafety.Invalidate("assembly host started");
            try
            {
                assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                installDir = Directory.Exists(@"Z:\Users\jw\Desktop\bin\ngu-idle-bot")
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

                var initialSave = CaptureLiveSaveFingerprint(
                    Character.importExport.getBase64Data(), Character);
                _lastRunSignature = initialSave.RunSignature;
                GameEpochController.Shared.StartHost(_sessionId, initialSave,
                    CaptureControllerIdentity());
                ExecutionSafety.Invalidate("published initial game lifecycle epoch");
                // Deployment is not accepted merely because writers opened. Publish identity only
                // after the host/session/controller epoch exists for task-4's PID/session/MVID
                // handshake.
                PublishDeploymentIdentity(assemblyDir, installDir);

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
            ObserveGameEpochTransitions();
            TryInstallPlanForCurrentEpoch();
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

        /*
        RESET / CONTROLLER EPOCH OBSERVATION

        Native reset, challenge, and difficulty calls finish synchronously but can originate from
        a manager or the user's UI. Observe their authoritative run signature before any queued
        settings work is drained and once more at the end of the one-second transaction. The old
        epoch closes before rebuilding managers/profiles, so no callback can execute the prior
        run's breakpoint objects or pending decision latch.
        */
        private void ObserveGameEpochTransitions()
        {
            var phase = GameEpochController.Shared.Phase;
            if (phase == GameEpochPhase.Uninitialized || phase == GameEpochPhase.Loading
                || phase == GameEpochPhase.Unloading || phase == GameEpochPhase.Quarantined)
                return;
            if (Character == null)
            {
                GameEpochController.Shared.Quarantine("Character disappeared from the active epoch");
                ExecutionSafety.Invalidate("active Character disappeared");
                return;
            }

            var controllers = CaptureControllerIdentity();
            if (!GameEpochController.Shared.ControllersMatch(controllers))
            {
                GameEpochController.Shared.Quarantine(
                    "controller identity changed outside a committed load/rebind");
                ExecutionSafety.Invalidate("unexpected controller identity change");
                _lastGameplayReady = false;
                return;
            }

            var runSignature = CaptureRunSignature(Character);
            if (string.IsNullOrEmpty(_lastRunSignature))
            {
                _lastRunSignature = runSignature;
                return;
            }
            if (string.Equals(runSignature, _lastRunSignature, StringComparison.Ordinal)) return;

            var previous = _lastRunSignature;
            _lastRunSignature = runSignature;
            var successorAlreadyPublished = string.Equals(runSignature,
                GameEpochController.Shared.Current.RunSignature, StringComparison.Ordinal);
            if (!successorAlreadyPublished)
            {
                var fingerprint = CaptureLiveSaveFingerprint(string.Empty, Character);
                GameEpochController.Shared.AdvanceRun(fingerprint, controllers,
                    "authoritative run signature changed from " + previous + " to " + runSignature);
            }
            _lastGameplayReady = true;
            ExecutionSafety.Invalidate(successorAlreadyPublished
                ? "verified reset successor reconciled with live controllers"
                : "rebirth/challenge/difficulty run epoch advanced");

            // Pause is already visible through GameEpoch, so this call can only release an existing
            // pending Titan-7 key; it cannot start another one.
            if (Autopilot != null) Autopilot.TryTitan7PuzzleStep();
            try
            {
                RecreateEpochBoundManagers();
            }
            catch (Exception rebuildError)
            {
                GameEpochController.Shared.Quarantine(
                    "run changed but epoch-bound manager reset failed: "
                    + rebuildError.GetType().Name + ": " + rebuildError.Message);
                LogAction("REJECTED", "Run epoch quarantined while rebuilding managers: "
                                      + rebuildError.Message);
                return;
            }
            LogAction("EPOCH", successorAlreadyPublished
                ? "Verified reset epoch reconciled; managers rebuilt without a duplicate generation advance"
                : "Run epoch advanced; stale queues/latches discarded and a new plan is required");
        }

        private void RecreateEpochBoundManagers()
        {
            _pendingDecision.Clear();
            LoadoutManager.ReleaseLock();
            DiggerManager.ReleaseLock();
            _invManager = new InventoryManager();
            _yggManager = new YggdrasilManager();
            _questManager = new QuestManager();
            _combManager = new CombatManager();
            WishManager = new WishManager();
            Autopilot = new AutopilotManager(_dir, _profilesDir);
            _allocationOwnerKnown = false;
            LoadAllocation();
            ExecutionSafety.ObserveConfig(Autopilot.Config);
            RefreshAllocationOwnership();
        }

        private void TryInstallPlanForCurrentEpoch()
        {
            if (GameEpochController.Shared.Phase != GameEpochPhase.AwaitingPlan
                || !IsGameplayReady || !_syncStateInitialized || !_lastGameplayReady
                || Autopilot == null)
                return;

            var epoch = GameEpochController.Shared.Current;
            try
            {
                // Tick builds/installs policy before a root exists. MutationCoordinator's nonzero
                // root rule causes every purchase/action branch to hold, while pure planning and
                // allocation-profile compilation still complete.
                Autopilot.Tick();
                if (Autopilot.Config == null) return;
                if (Autopilot.Config.Enabled && Autopilot.Plan == null) return;
                if (Autopilot.Config.Enabled && Autopilot.Config.ManageAllocations
                    && Autopilot.Profile == null) return;

                var fingerprint = Autopilot.Plan == null
                    ? "disabled|" + Autopilot.Config.ExecutionFingerprint()
                    : Autopilot.Plan.Signature(Character);
                string reason;
                if (!GameEpochController.Shared.InstallPlan(epoch, fingerprint, out reason))
                {
                    LogAction("HOLD", "New-epoch plan installation held: " + reason);
                    return;
                }
                ExecutionSafety.Invalidate("new game-epoch plan installed");
                RefreshAllocationOwnership();
                LogAction("EPOCH", "Installed plan for "
                                   + GameEpochController.Shared.Current.Fingerprint);
            }
            catch (Exception error)
            {
                LogAction("REJECTED", "New-epoch plan build failed; automation remains held: "
                                      + error.GetType().Name + ": " + error.Message);
            }
        }

        private void QuickSave()
        {
            var phase = GameEpochController.Shared.Phase;
            if (phase == GameEpochPhase.Loading || phase == GameEpochPhase.Quarantined
                || phase == GameEpochPhase.Unloading || phase == GameEpochPhase.Uninitialized)
            {
                LogAction("HOLD", "Quicksave held because the game epoch is " + phase
                                  + ": " + GameEpochController.Shared.HoldReason);
                return;
            }
            using (ExecutionSafety.BeginCycle("manual quicksave", CurrentAutopilotConfig))
            {
                TryRunMutation("manual quicksave and Steam Cloud write",
                    MutationClass.SaveLoad, MutationOwner.User, () =>
                    {
                        Log("Writing timestamped quicksave and last-good generations");
                        // Native quicksave helpers establish this boundary before serializing.
                        // Direct cloud dispatch does not, so failing to do it here replays already
                        // played online time when F7 later computes offline progress.
                        var snapshotTime = Epoch.Current();
                        Character.lastTime = snapshotTime;
                        var data = Character.importExport.getBase64Data();
                        var parsed = Character.importExport.getDataFromString(data);
                        if (parsed == null || parsed.lastTime != snapshotTime)
                            throw new InvalidDataException(
                                "quicksave failed timestamp/read-back validation before publication");
                        var savePath = Path.Combine(_dir, "NGUSave.txt");
                        var saveResult = WriteTextAtomic(savePath, data + Environment.NewLine,
                            candidate =>
                            {
                                var candidateData = Character.importExport.getDataFromString(
                                    File.ReadAllText(candidate));
                                return candidateData != null
                                       && candidateData.lastTime == snapshotTime;
                            });

                        try
                        {
                            var json = JsonUtility.ToJson(Character.importExport.gameStateToData());
                            WriteTextAtomic(Path.Combine(_dir, "NGUSave.json"),
                                json + Environment.NewLine,
                                candidate => JsonUtility.FromJson<PlayerData>(
                                    File.ReadAllText(candidate)) != null);
                        }
                        catch (Exception jsonError)
                        {
                            // The validated base64 generation is the recovery artifact. A JSON
                            // diagnostic failure must not roll its timestamp back or destroy it.
                            LogAction("REJECTED", "Quicksave JSON diagnostic was not published: "
                                                  + jsonError.Message);
                        }

                        var cloudDispatched = Character.saveLoad.saveGamestateToSteamCloud();
                        LogAction(cloudDispatched ? "SAVE" : "REJECTED",
                            cloudDispatched
                                ? "Local quicksave committed at native timestamp " + snapshotTime
                                  + " (sha256 " + saveResult.PublishedSha256
                                  + "); Steam Cloud write dispatched, durability unconfirmed"
                                : "Local quicksave committed at native timestamp " + snapshotTime
                                  + " but Steam Cloud dispatch returned false");
                    });
            }
        }

        private static DurableGenerationResult WriteTextAtomic(string path, string contents,
            Func<string, bool> validator = null)
        {
            return DurableGenerationWriter.WriteText(path, contents, validator);
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
                var gameEpoch = GameEpochController.Shared.Current;
                var json = "{\n"
                           + "  \"schemaVersion\": 2,\n"
                           + "  \"observedAt\": \"" + DateTime.UtcNow.ToString("o") + "\",\n"
                           + "  \"producerPid\": " + process.Id + ",\n"
                           + "  \"producerProcessStartUtc\": \"" + process.StartTime.ToUniversalTime().ToString("o") + "\",\n"
                           + "  \"producerSessionId\": \"" + _sessionId + "\",\n"
                           + "  \"telemetryHandshake\": \"" + handshake + "\",\n"
                           + "  \"gameEpochFingerprint\": \""
                           + JsonEscape(gameEpoch.Fingerprint) + "\",\n"
                           + "  \"gameEpochPhase\": \""
                           + GameEpochController.Shared.Phase + "\",\n"
                           + "  \"gameEpochHostGeneration\": " + gameEpoch.HostGeneration + ",\n"
                           + "  \"gameEpochSaveGeneration\": " + gameEpoch.SaveGeneration + ",\n"
                           + "  \"gameEpochRunGeneration\": " + gameEpoch.RunGeneration + ",\n"
                           + "  \"gameEpochGeneration\": " + gameEpoch.Generation + ",\n"
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

                if (saveDataFromString == null || dataFromString == null)
                {
                    Log("Quicksave envelope/checksum did not produce a complete save graph");
                    return;
                }

                if (dataFromString.version < 361
                    && Application.platform != RuntimePlatform.WindowsEditor)
                {
                    Log("Bad save version");
                    return;
                }

                if (dataFromString.version > Character.getVersion())
                {
                    Log("Bad save version");
                    return;
                }

                var expected = CaptureImportedSaveFingerprint(base64Data, dataFromString);
                var beforeSerialized = Character.importExport.getBase64Data();
                var before = CaptureLiveSaveFingerprint(beforeSerialized, Character);
                var gameHash = GameAssemblySha256;
                if (string.IsNullOrEmpty(gameHash)
                    && File.Exists(typeof(Character).Assembly.Location))
                    gameHash = Sha256(typeof(Character).Assembly.Location);
                var registry = NativeBindingRegistry.Create(typeof(Character).Assembly, gameHash);
                if (!registry.IrreversibleActionsEnabled
                    || !registry.HasBinding(NativeBindingKeys.LoadIntoGame))
                {
                    LogAction("HOLD", "Quicksave load held before epoch transition: "
                                      + (registry.IsKnownBuild
                                          ? registry.FailureFor(NativeBindingKeys.LoadIntoGame)
                                          : registry.BuildFailureReason));
                    return;
                }
                var native = registry.CreateMutationAdapters();

                using (ExecutionSafety.BeginCycle("manual quickload", CurrentAutopilotConfig))
                {
                    TryRunMutation("manual quickload", MutationClass.SaveLoad, MutationOwner.User, () =>
                    {
                        // Close the old epoch and publish the pause before native validation can
                        // touch a controller. Every queue/latch/cancellation is now stale.
                        var loadingEpoch = GameEpochController.Shared.BeginLoad(
                            "quicksave load is in progress");
                        _syncStateInitialized = true;
                        _lastGameplayReady = false;
                        _pendingDecision.Clear();
                        if (Autopilot != null)
                            Autopilot.ReportSynchronization(false, "quicksave load is in progress; waiting for a verified gameplay state");
                        ExecutionSafety.Invalidate("manual quickload epoch began");

                        // Release the existing Titan-7 key after the epoch pause is visible. This
                        // branch cannot start a new key while IsAutomationReady is false.
                        if (Autopilot != null) Autopilot.TryTitan7PuzzleStep();
                        LoadoutManager.ReleaseLock();
                        DiggerManager.ReleaseLock();

                        var invocation = native.LoadSave(Character.saveLoad, saveDataFromString);
                        var nativeTrue = invocation.ReturnedNormally
                                         && invocation.ReturnValue is bool
                                         && (bool)invocation.ReturnValue;
                        if (!nativeTrue)
                        {
                            var failureAfter = TryCaptureLiveSaveFingerprint();
                            var unchanged = failureAfter != null
                                            && string.Equals(before.ContentHash,
                                                failureAfter.ContentHash,
                                                StringComparison.Ordinal);
                            var failure = invocation.Status + ": " + invocation.Reason
                                          + (invocation.Exception == null ? string.Empty
                                              : "; " + invocation.Exception.GetType().Name + ": "
                                                + invocation.Exception.Message)
                                          + (unchanged ? "; exact before bytes retained"
                                              : "; live state changed or could not be recaptured");
                            GameEpochController.Shared.FailLoad(loadingEpoch, failure,
                                failureAfter);
                            LogAction("REJECTED", "Quicksave load quarantined: " + failure);
                            return;
                        }

                        string rebindError;
                        if (!TryRebindGameControllers(out rebindError))
                        {
                            GameEpochController.Shared.FailLoad(loadingEpoch,
                                "native load returned true but controller rebind failed: "
                                + rebindError, TryCaptureLiveSaveFingerprint());
                            LogAction("REJECTED", "Quicksave load quarantined after native true: "
                                                  + rebindError);
                            return;
                        }

                        var afterSerialized = Character.importExport.getBase64Data();
                        var after = CaptureLiveSaveFingerprint(afterSerialized, Character);
                        string commitError;
                        if (!GameEpochController.Shared.CommitLoad(loadingEpoch, true,
                                expected, after, CaptureControllerIdentity(), out commitError))
                        {
                            LogAction("REJECTED", "Quicksave load returned true but its exact "
                                                  + "postcondition failed; automation quarantined: "
                                                  + commitError);
                            return;
                        }

                        _lastRunSignature = after.RunSignature;
                        try
                        {
                            RecreateEpochBoundManagers();
                        }
                        catch (Exception rebuildError)
                        {
                            GameEpochController.Shared.Quarantine(
                                "load committed but epoch-bound manager/plan reset failed: "
                                + rebuildError.GetType().Name + ": " + rebuildError.Message);
                            throw;
                        }
                        _syncStateInitialized = true;
                        _lastGameplayReady = false;
                        ExecutionSafety.Invalidate("manual quickload committed a new save epoch");
                        LogAction("SAVE", "Quicksave load committed as "
                                          + GameEpochController.Shared.Current.Fingerprint
                                          + "; waiting for a later synchronized frame and plan");
                    });
                }
            }
            catch (Exception e)
            {
                if (GameEpochController.Shared.Phase == GameEpochPhase.Loading)
                    GameEpochController.Shared.FailLoad(
                        GameEpochController.Shared.Current,
                        "quicksave load threw after its epoch closed: "
                        + e.GetType().Name + ": " + e.Message);
                _syncStateInitialized = true;
                _lastGameplayReady = false;
                Log($"Failed to load quicksave: {e.Message}");
            }
        }

        private static SaveStateFingerprint CaptureImportedSaveFingerprint(string serialized,
            PlayerData data)
        {
            if (data == null) return null;
            var difficulty = data.settings == null
                ? string.Empty : data.settings.rebirthDifficulty.ToString();
            var rebirth = data.stats == null ? -1L : data.stats.rebirthNumber;
            return new SaveStateFingerprint(
                EpochHash.Sha256((serialized ?? string.Empty).Trim()), data.version,
                data.lastTime, rebirth, difficulty, data.highestBoss,
                data.highestHardBoss, data.highestSadisticBoss,
                CaptureRunSignature(data));
        }

        private static SaveStateFingerprint CaptureImportedSaveFingerprint(PlayerData data)
        {
            if (data == null) return null;
            var difficulty = data.settings == null
                ? string.Empty : data.settings.rebirthDifficulty.ToString();
            var rebirth = data.stats == null ? -1L : data.stats.rebirthNumber;
            return new SaveStateFingerprint(string.Empty, data.version,
                data.lastTime, rebirth, difficulty, data.highestBoss,
                data.highestHardBoss, data.highestSadisticBoss,
                CaptureRunSignature(data));
        }

        private static SaveStateFingerprint CaptureLiveSaveFingerprint(string serialized,
            Character character)
        {
            if (character == null) return null;
            var difficulty = character.settings == null
                ? string.Empty : character.settings.rebirthDifficulty.ToString();
            var rebirth = character.stats == null ? -1L : character.stats.rebirthNumber;
            return new SaveStateFingerprint(
                string.IsNullOrEmpty(serialized) ? string.Empty
                    : EpochHash.Sha256(serialized.Trim()),
                character.version, character.lastTime, rebirth, difficulty,
                character.highestBoss, character.highestHardBoss,
                character.highestSadisticBoss, CaptureRunSignature(character));
        }

        private static SaveStateFingerprint TryCaptureLiveSaveFingerprint()
        {
            try
            {
                return Character == null || Character.importExport == null ? null
                    : CaptureLiveSaveFingerprint(Character.importExport.getBase64Data(), Character);
            }
            catch
            {
                return null;
            }
        }

        private static string CaptureRunSignature(Character character)
        {
            if (character == null) return "missing-character";
            var rebirth = character.stats == null ? -1L : character.stats.rebirthNumber;
            var difficulty = character.settings == null ? "missing-difficulty"
                : character.settings.rebirthDifficulty.ToString();
            var challenge = character.challenges == null ? "missing-challenges"
                : (character.challenges.inChallenge ? "active:" : "none:")
                  + character.challenges.curChallengeType;
            return rebirth + "|" + difficulty + "|next="
                   + character.nextRebirthDifficulty + "|" + challenge;
        }

        private static string CaptureRunSignature(PlayerData data)
        {
            if (data == null) return "missing-player-data";
            var rebirth = data.stats == null ? -1L : data.stats.rebirthNumber;
            var difficulty = data.settings == null ? "missing-difficulty"
                : data.settings.rebirthDifficulty.ToString();
            var challenge = data.challenges == null ? "missing-challenges"
                : (data.challenges.inChallenge ? "active:" : "none:")
                  + data.challenges.curChallengeType;
            return rebirth + "|" + difficulty + "|next="
                   + data.nextRebirthDifficulty + "|" + challenge;
        }

        private static ControllerIdentity CaptureControllerIdentity()
        {
            return new ControllerIdentity(ObjectIdentity(Character), ObjectIdentity(Controller),
                ObjectIdentity(PlayerController));
        }

        private static int ObjectIdentity(object value)
        {
            return value == null ? 0 : RuntimeHelpers.GetHashCode(value);
        }

        private static bool TryRebindGameControllers(out string error)
        {
            error = string.Empty;
            try
            {
                var character = FindObjectOfType<Character>();
                var playerController = FindObjectOfType<PlayerController>();
                if (character == null)
                {
                    error = "Character was not rediscovered";
                    return false;
                }
                if (character.inventoryController == null || playerController == null
                    || character.importExport == null || character.saveLoad == null
                    || character.mainMenu == null)
                {
                    error = "one or more required native controllers are null";
                    return false;
                }
                Character = character;
                Controller = character.inventoryController;
                PlayerController = playerController;
                return CaptureControllerIdentity().IsComplete;
            }
            catch (Exception rebindError)
            {
                error = rebindError.GetType().Name + ": " + rebindError.Message;
                return false;
            }
        }

        // Stuff on a very short timer
        void FastAllocationRoutine()
        {
            ObserveGameEpochTransitions();
            if (!IsAutomationReady)
                return;
            if (!Settings.GlobalEnabled && (Autopilot == null || !Autopilot.CanExecuteSafe))
                return;

            RefreshAllocationOwnership();
            ExecutionSafety.ReportHold("typed-intent:fast-allocation",
                "Fast loadout/allocation mutations are held until both expose typed child intents.");

            /*
            SETTLED-STATE PUBLICATION BARRIER

            This callback remains the publication cadence for a completed one-second root.  The
            fast allocation feature is deliberately outside the executable authority envelope, so
            its visible HOLD is a no-op and must not rewrite a successfully closed typed root as a
            failed transaction.  Once a typed fast-allocation child is integrated, its own exact
            result belongs in the root journal before publication.
            */
            PendingDecisionPublication publication;
            if (Autopilot != null && _pendingDecision.TryTake(
                    GameEpochController.Shared.Current, out publication))
            {
                try
                {
                    Autopilot.PublishDecisionAfterAutomation(publication.TransactionComplete,
                        publication.TransactionError);
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
            ObserveGameEpochTransitions();
            if (!IsAutomationReady)
                return;
            if (!Settings.GlobalEnabled && (Autopilot == null || !Autopilot.CanExecuteSafe))
                return;
            ExecutionSafety.ReportHold("typed-intent:quick-combat-rewards",
                "Quick combat, reward, Money Pit, spin, ITOPOD, and Blood mutations are held until their typed child intents are integrated.");
        }

        // Runs every second; tactical combat and allocations have separate faster loops.
        void AutomationRoutine()
        {
            var transactionComplete = false;
            var transactionError = string.Empty;
            var transactionErrors = new List<string>();
            GameEpochToken transactionEpoch = null;
            var rejectionEpochBefore = System.Threading.Interlocked.Read(ref _rejectionEpoch);
            RootTransaction mutationRoot = null;
            long executionStateVersion = -1;
            try
            {
                ObserveGameEpochTransitions();
                if (!IsAutomationReady)
                {
                    _timeLeft = 1f;
                    return;
                }
                transactionEpoch = GameEpochController.Shared.Current;
                // File watcher callbacks are deliberately drained only after the
                // gameplay synchronization barrier.  Allocation reload invokes its
                // allocation pass, so even a seemingly read-only profile change can
                // reach Unity controllers.
                ProcessPendingFileChanges();
                if (Autopilot != null)
                    Autopilot.Tick();
                RefreshAllocationOwnership();
                if (Autopilot == null)
                {
                    _timeLeft = 1f;
                    return;
                }
                var rootBegin = Autopilot.BeginAutomationRoot(
                    "one-second automation transaction");
                if (rootBegin.Status != RootBeginStatus.Begun || rootBegin.Transaction == null)
                {
                    ExecutionSafety.ReportHold("automation-root",
                        "One-second automation held: " + rootBegin.Reason);
                    _timeLeft = 1f;
                    return;
                }
                mutationRoot = rootBegin.Transaction;
                executionStateVersion = ExecutionSafety.StateVersion;
                Autopilot.ExecutePlannedMutations(mutationRoot);
                if (mutationRoot.IsClosed
                    || !string.Equals(mutationRoot.Token.EpochFingerprint,
                        CurrentGameEpochFingerprint, StringComparison.Ordinal))
                {
                    transactionErrors.Add("automation root closed during typed plan execution");
                    throw new InvalidOperationException(transactionErrors[0]);
                }

                if (!Settings.GlobalEnabled && (Autopilot == null || !Autopilot.CanExecuteSafe))
                {
                    _timeLeft = 1f;
                    return;
                }

                ExecutionSafety.ReportHold("typed-intent:itopod-routing",
                    "ITOPOD routing is held until its post-kill controller is integrated.");
                if (AutopilotWants(x => x.ManageBeards))
                    ExecutionSafety.ReportHold("typed-intent:beards",
                        "Beard mutation is held; the exact marginal oracle is shadow-only.");
                if (Settings.ManageInventory || AutopilotWants(x => x.ManageInventory))
                    ExecutionSafety.ReportHold("typed-intent:inventory",
                        "Bulk inventory mutation is held until its topology operations are coordinator child intents.");
                if (AutopilotWants(x => x.AllowEndSequence))
                    ExecutionSafety.ReportHold("typed-intent:end-sequence",
                        "END execution is disabled for this deployment.");
                if (AutopilotWants(x => x.ManageBloodMagic))
                    ExecutionSafety.ReportHold("typed-intent:blood",
                        "Blood spell execution is held until its typed delivery/cast intent is integrated.");
                if (AutopilotWants(x => x.ManageInventory) && !Controller.midDrag)
                    ExecutionSafety.ReportHold("typed-intent:daycare",
                        "Daycare mutation is held until its exact exchange is a coordinator child intent.");

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
                    ExecutionSafety.ReportHold("typed-intent:titan-execution",
                        "Titan loadout/digger staging is held until TitanExecutionManager is wired to the root.");
                }

                if ((Settings.ManageYggdrasil || AutopilotWants(x => x.ManageYggdrasil)) && Character.buttons.yggdrasil.interactable)
                {
                    if (mutationRoot == null || mutationRoot.IsClosed)
                    {
                        transactionErrors.Add("Yggdrasil: typed mutation root is unavailable");
                    }
                    else
                    {
                        _yggManager.ManageYggHarvest(mutationRoot);
                        if (!mutationRoot.IsClosed)
                            _yggManager.CheckFruits(mutationRoot);
                    }
                }

                if (Settings.AutoBuyEM && Character.highestBoss >= 17)
                {
                    ExecutionSafety.ReportHold("typed-intent:legacy-em-purchases",
                        "Legacy Energy/Magic/R3 purchases are held until the exact purchase catalog owns the transaction.");
                }

                if (!AutopilotWants(x => x.ManageAllocations) && Settings.GlobalEnabled
                    && ActiveProfile != null)
                    ExecutionSafety.ReportHold("typed-intent:legacy-allocations",
                        "Legacy allocation mutation is held until ExactResourceAllocator owns the child intent.");

                // Full autopilot already made one conservation-aware Blood decision
                // after MacGuffin selection.  Running the legacy threshold caster as
                // well can spend the remainder on a second spell in the same sweep.
                if (Settings.CastBloodSpells && !AutopilotWants(x => x.ManageBloodMagic))
                {
                    if (ActiveProfile != null)
                        ExecutionSafety.ReportHold("typed-intent:legacy-blood",
                            "Legacy Blood spell mutation is held until a typed cast intent is integrated.");
                }

                if ((Settings.AutoQuest || AutopilotWants(x => x.ManageQuests)) && Character.buttons.beast.interactable)
                {
                    if (mutationRoot == null || mutationRoot.IsClosed)
                    {
                        transactionErrors.Add("Quests: typed mutation root is unavailable");
                    }
                    else if (!Character.inventoryController.midDrag)
                    {
                        ExecutionSafety.ReportHold("quest-inventory-root-adapter",
                            "Quest-item merge/offer service is held until it exposes typed child intents.");
                        _questManager.CheckQuestTurnin(mutationRoot);
                        if (!mutationRoot.IsClosed)
                            _questManager.ManageQuests(mutationRoot);
                    }
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
                    ExecutionSafety.ReportHold("typed-intent:rebirth",
                        "Rebirth/challenge execution is held until the exact route intent is wired to this root.");
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
                if (mutationRoot != null)
                {
                    var epochChanged = !string.Equals(mutationRoot.Token.EpochFingerprint,
                        CurrentGameEpochFingerprint, StringComparison.Ordinal);
                    if (Autopilot != null)
                        Autopilot.RecordAutomationRoot(mutationRoot,
                            epochChanged ? "closed-by-epoch-transition" : "closed");
                    mutationRoot.Dispose();
                }
            }
            // A synchronous rebirth/challenge/difficulty may have happened at the commit boundary.
            // Observe it before publishing anything derived from the old run.
            ObserveGameEpochTransitions();
            var currentEpoch = GameEpochController.Shared.Current;
            if (Autopilot != null && transactionEpoch != null
                && transactionEpoch.Matches(currentEpoch, EpochWorkScope.ExactGameState)
                && GameEpochController.Shared.MutationOpen)
            {
                // Queue publication for the next fast-allocation sweep. Gear work above may have
                // reclaimed resource pools that are intentionally restored on that cadence.
                _pendingDecision.Set(currentEpoch, new PendingDecisionPublication
                {
                    TransactionComplete = transactionComplete,
                    TransactionError = transactionError
                });
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
            ObserveGameEpochTransitions();
            if (!IsAutomationReady)
                return;
            if (!Settings.GlobalEnabled && (Autopilot == null || !Autopilot.CanExecuteSafe))
                return;
            ExecutionSafety.ReportHold("typed-intent:adventure-routing",
                "Adventure routing/combat is held until CombatManager exposes coordinator child intents.");
        }

        private void MoveToITOPOD()
        {
            ExecutionSafety.ReportHold("typed-intent:itopod-routing",
                "Legacy ITOPOD movement is held until the typed post-kill/range controller is integrated.");
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
