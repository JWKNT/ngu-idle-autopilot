using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

/*
FILE PURPOSE

GameEpoch owns the identity and cancellation boundary between one live NGU Idle state graph and
the next. A token binds queued work, deferred observations, latches, plans, and recovery callbacks
to the host session plus save/run/controller generations which produced them. Main advances this
state synchronously before load/reset/unload work can yield to another Unity callback. Inputs are
read-only save fingerprints, controller object identities, native load outcomes, synchronized-frame
observations, and explicit plan-install acknowledgements. Outputs are immutable tokens, lifecycle
phase/hold reasons, stale-work decisions, and cancellation dispatch.

The same file owns the small durable-generation writer used for manual snapshots and lifecycle
telemetry. It writes and flushes a unique sibling temporary file, validates it when requested, and
uses atomic replacement while retaining the replaced destination as a timestamped last-good
generation. A failed validation or replacement never deletes the currently published file.

No token is reusable after a generation transition. Load success requires the native Boolean,
stable imported-state agreement, valid rebound controller identities, a later synchronized frame,
and a newly installed plan before mutation authority reopens. False/partial loads quarantine the
epoch rather than blessing menu visibility. Cancellation errors are reported to the caller and do
not stop remaining compensations. This file is pure infrastructure: it does not discover Unity
objects, call native game methods, decide rebirth strategy, or inspect a production save.
*/
namespace NGUInjector.Autopilot
{
    internal enum GameEpochPhase
    {
        Uninitialized,
        AwaitingSynchronization,
        AwaitingPlan,
        Active,
        Loading,
        Quarantined,
        Unloading
    }

    internal enum EpochWorkScope
    {
        ExactGameState,
        HostSession
    }

    internal sealed class ControllerIdentity
    {
        internal readonly int Character;
        internal readonly int InventoryController;
        internal readonly int PlayerController;

        internal ControllerIdentity(int character, int inventoryController, int playerController)
        {
            Character = character;
            InventoryController = inventoryController;
            PlayerController = playerController;
        }

        internal bool IsComplete
        {
            get { return Character != 0 && InventoryController != 0 && PlayerController != 0; }
        }

        internal bool SameAs(ControllerIdentity other)
        {
            return other != null && Character == other.Character
                   && InventoryController == other.InventoryController
                   && PlayerController == other.PlayerController;
        }

        internal string Fingerprint
        {
            get { return Character + ":" + InventoryController + ":" + PlayerController; }
        }
    }

    /*
    SAVE IDENTITY

    ContentHash records the exact serialized bytes observed at capture time, while the stable
    fields are the subset that native offline reconciliation must not replace. A successful load
    may legitimately change current resources/timers and therefore need not reproduce ContentHash.
    The committed epoch token incorporates the post-load content hash and a new save generation,
    so even loading byte-identical state into the same Character produces a distinct identity.
    */
    internal sealed class SaveStateFingerprint
    {
        internal readonly string ContentHash;
        internal readonly int Version;
        internal readonly int LastTime;
        internal readonly long RebirthNumber;
        internal readonly string Difficulty;
        internal readonly int HighestBoss;
        internal readonly int HighestHardBoss;
        internal readonly int HighestSadisticBoss;
        internal readonly string RunSignature;

        internal SaveStateFingerprint(string contentHash, int version, int lastTime,
            long rebirthNumber, string difficulty, int highestBoss, int highestHardBoss,
            int highestSadisticBoss, string runSignature)
        {
            ContentHash = contentHash ?? string.Empty;
            Version = version;
            LastTime = lastTime;
            RebirthNumber = rebirthNumber;
            Difficulty = difficulty ?? string.Empty;
            HighestBoss = highestBoss;
            HighestHardBoss = highestHardBoss;
            HighestSadisticBoss = highestSadisticBoss;
            RunSignature = runSignature ?? string.Empty;
        }

        internal bool StableStateMatches(SaveStateFingerprint actual, out string reason)
        {
            if (actual == null)
            {
                reason = "post-load save fingerprint is missing";
                return false;
            }
            if (Version != actual.Version)
            {
                reason = "save version differs from the prevalidated payload";
                return false;
            }
            if (LastTime != actual.LastTime)
            {
                reason = "lastTime differs from the prevalidated payload";
                return false;
            }
            if (RebirthNumber != actual.RebirthNumber)
            {
                reason = "rebirth number differs from the prevalidated payload";
                return false;
            }
            if (!string.Equals(Difficulty, actual.Difficulty, StringComparison.Ordinal))
            {
                reason = "difficulty differs from the prevalidated payload";
                return false;
            }
            if (HighestBoss != actual.HighestBoss || HighestHardBoss != actual.HighestHardBoss
                || HighestSadisticBoss != actual.HighestSadisticBoss)
            {
                reason = "highest-boss records differ from the prevalidated payload";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        internal string CanonicalStableState
        {
            get
            {
                return Version + "|" + LastTime + "|" + RebirthNumber + "|" + Difficulty
                       + "|" + HighestBoss + "|" + HighestHardBoss + "|"
                       + HighestSadisticBoss + "|" + RunSignature;
            }
        }

        internal string EffectiveContentHash
        {
            get
            {
                return string.IsNullOrEmpty(ContentHash)
                    ? EpochHash.Sha256(CanonicalStableState) : ContentHash;
            }
        }
    }

    internal static class EpochHash
    {
        internal static string Sha256(string value)
        {
            using (var hash = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }
    }

    internal sealed class GameEpochToken
    {
        internal readonly string SessionId;
        internal readonly long HostGeneration;
        internal readonly long SaveGeneration;
        internal readonly long RunGeneration;
        internal readonly long Generation;
        internal readonly string SaveContentHash;
        internal readonly string RunSignature;
        internal readonly string PlanFingerprint;
        internal readonly ControllerIdentity Controllers;

        internal GameEpochToken(string sessionId, long hostGeneration, long saveGeneration,
            long runGeneration, long generation, string saveContentHash, string runSignature,
            string planFingerprint, ControllerIdentity controllers)
        {
            SessionId = sessionId ?? string.Empty;
            HostGeneration = hostGeneration;
            SaveGeneration = saveGeneration;
            RunGeneration = runGeneration;
            Generation = generation;
            SaveContentHash = saveContentHash ?? string.Empty;
            RunSignature = runSignature ?? string.Empty;
            PlanFingerprint = planFingerprint ?? string.Empty;
            Controllers = controllers;
        }

        internal bool Matches(GameEpochToken current, EpochWorkScope scope)
        {
            if (current == null || HostGeneration <= 0 || current.HostGeneration <= 0
                || HostGeneration != current.HostGeneration
                || !string.Equals(SessionId, current.SessionId, StringComparison.Ordinal))
                return false;
            return scope == EpochWorkScope.HostSession || Generation == current.Generation;
        }

        internal string Fingerprint
        {
            get
            {
                return SessionId + ":h" + HostGeneration + ":s" + SaveGeneration + ":r"
                       + RunGeneration + ":g" + Generation + ":state=" + SaveContentHash
                       + ":run=" + RunSignature + ":plan=" + PlanFingerprint + ":controllers="
                       + (Controllers == null ? "missing" : Controllers.Fingerprint);
            }
        }
    }

    internal sealed class GameEpochController
    {
        private sealed class CancellationEntry
        {
            internal string Id;
            internal GameEpochToken Token;
            internal Action Cancel;
        }

        private readonly object _gate = new object();
        private readonly List<CancellationEntry> _cancellations =
            new List<CancellationEntry>();
        private long _hostSequence;
        private long _generation;
        private long _saveGeneration;
        private long _runGeneration;
        private string _sessionId = string.Empty;
        private string _saveContentHash = string.Empty;
        private string _runSignature = string.Empty;
        private string _planFingerprint = string.Empty;
        private ControllerIdentity _controllers;
        private GameEpochPhase _phase = GameEpochPhase.Uninitialized;
        private string _holdReason = "host has not published a game epoch";

        internal static readonly GameEpochController Shared = new GameEpochController();

        internal GameEpochPhase Phase
        {
            get { lock (_gate) return _phase; }
        }

        internal string HoldReason
        {
            get { lock (_gate) return _holdReason; }
        }

        internal bool MutationOpen
        {
            get { lock (_gate) return _phase == GameEpochPhase.Active; }
        }

        internal GameEpochToken Current
        {
            get { lock (_gate) return Snapshot(); }
        }

        internal GameEpochToken StartHost(string sessionId, SaveStateFingerprint save,
            ControllerIdentity controllers)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("A host session ID is required.", "sessionId");
            if (controllers == null || !controllers.IsComplete)
                throw new ArgumentException("Complete controller identities are required.",
                    "controllers");
            CancelAndClear("host replacement");
            lock (_gate)
            {
                _hostSequence++;
                if (_hostSequence <= 0) _hostSequence = 1;
                _generation++;
                if (_generation <= 0) _generation = 1;
                _saveGeneration = 1;
                _runGeneration = 1;
                _sessionId = sessionId;
                _controllers = controllers;
                _saveContentHash = save == null ? string.Empty : save.EffectiveContentHash;
                _runSignature = save == null ? string.Empty : save.RunSignature;
                _planFingerprint = string.Empty;
                _phase = GameEpochPhase.AwaitingSynchronization;
                _holdReason = "host published; waiting for a verified gameplay frame";
                return Snapshot();
            }
        }

        internal GameEpochToken BeginLoad(string reason)
        {
            TransitionBeforeCancellation(GameEpochPhase.Loading,
                string.IsNullOrEmpty(reason) ? "save load is in progress" : reason);
            return Current;
        }

        internal bool CommitLoad(GameEpochToken loadingToken, bool nativeReturnedTrue,
            SaveStateFingerprint expected, SaveStateFingerprint actual,
            ControllerIdentity reboundControllers, out string reason)
        {
            reason = string.Empty;
            lock (_gate)
            {
                if (_phase != GameEpochPhase.Loading || loadingToken == null
                    || !loadingToken.Matches(Snapshot(), EpochWorkScope.ExactGameState))
                {
                    reason = "load completion belongs to a stale game epoch";
                    QuarantineLocked(reason);
                    return false;
                }
                if (!nativeReturnedTrue)
                {
                    reason = "native loadintoGame returned false";
                    QuarantineObservedLocked(reason, actual);
                    return false;
                }
                if (reboundControllers == null || !reboundControllers.IsComplete)
                {
                    reason = "controller rebinding was incomplete";
                    QuarantineObservedLocked(reason, actual);
                    return false;
                }
                if (expected == null || !expected.StableStateMatches(actual, out reason))
                {
                    QuarantineObservedLocked(reason, actual);
                    return false;
                }

                _generation++;
                _saveGeneration++;
                _runGeneration++;
                _controllers = reboundControllers;
                _saveContentHash = actual.EffectiveContentHash;
                _runSignature = actual.RunSignature;
                _planFingerprint = string.Empty;
                _phase = GameEpochPhase.AwaitingSynchronization;
                _holdReason = "load committed; waiting for a later synchronized gameplay frame";
                return true;
            }
        }

        internal void FailLoad(GameEpochToken loadingToken, string reason,
            SaveStateFingerprint observed = null)
        {
            lock (_gate)
            {
                if (_phase != GameEpochPhase.Loading || loadingToken == null
                    || !loadingToken.Matches(Snapshot(), EpochWorkScope.ExactGameState))
                    return;
                QuarantineObservedLocked(string.IsNullOrEmpty(reason)
                    ? "load result is indeterminate" : reason, observed);
            }
        }

        internal GameEpochToken AdvanceRun(SaveStateFingerprint current,
            ControllerIdentity controllers, string reason)
        {
            CancelCurrent(string.IsNullOrEmpty(reason) ? "run transition" : reason);
            lock (_gate)
            {
                _generation++;
                _runGeneration++;
                _controllers = controllers;
                _saveContentHash = current == null ? _saveContentHash : current.EffectiveContentHash;
                _runSignature = current == null ? string.Empty : current.RunSignature;
                _planFingerprint = string.Empty;
                _phase = GameEpochPhase.AwaitingPlan;
                _holdReason = "run changed; waiting for a plan installed in the new epoch";
                return Snapshot();
            }
        }

        internal bool ObserveSynchronizedFrame(ControllerIdentity observed, out string reason)
        {
            reason = string.Empty;
            lock (_gate)
            {
                if (_phase == GameEpochPhase.Quarantined || _phase == GameEpochPhase.Unloading
                    || _phase == GameEpochPhase.Loading || _phase == GameEpochPhase.Uninitialized)
                {
                    reason = _holdReason;
                    return false;
                }
                if (observed == null || !observed.IsComplete)
                {
                    reason = "synchronized frame has incomplete controller identities";
                    QuarantineLocked(reason);
                    return false;
                }
                if (_controllers != null && !_controllers.SameAs(observed))
                {
                    reason = "controller identity changed outside a committed load/rebind";
                    QuarantineLocked(reason);
                    return false;
                }
                if (_phase == GameEpochPhase.AwaitingSynchronization)
                {
                    // A plan captured before this later-frame observation is stale even when the
                    // Character/controller objects were retained. Make the synchronization barrier
                    // part of exact token identity, not only a phase flag.
                    _generation++;
                    _controllers = observed;
                    _phase = GameEpochPhase.AwaitingPlan;
                    _holdReason = "gameplay synchronized; waiting for a new-epoch plan";
                }
                return _phase == GameEpochPhase.Active;
            }
        }

        internal bool InstallPlan(GameEpochToken expectedEpoch, string planFingerprint,
            out string reason)
        {
            lock (_gate)
            {
                if (_phase != GameEpochPhase.AwaitingPlan || expectedEpoch == null
                    || !expectedEpoch.Matches(Snapshot(), EpochWorkScope.ExactGameState))
                {
                    reason = "plan was produced for a stale or unsynchronized epoch";
                    return false;
                }
                if (string.IsNullOrEmpty(planFingerprint))
                {
                    reason = "a nonempty plan fingerprint is required";
                    return false;
                }
                // Plan installation is itself an identity boundary: UI work enqueued while policy
                // was unavailable must not inherit authority merely because a later plan opened.
                _generation++;
                _planFingerprint = planFingerprint;
                _phase = GameEpochPhase.Active;
                _holdReason = string.Empty;
                reason = string.Empty;
                return true;
            }
        }

        internal void Quarantine(string reason)
        {
            CancelCurrent(reason);
            lock (_gate) QuarantineLocked(reason);
        }

        internal void BeginUnload(string reason)
        {
            CancelCurrent(reason);
            lock (_gate)
            {
                _generation++;
                _phase = GameEpochPhase.Unloading;
                _holdReason = string.IsNullOrEmpty(reason) ? "assembly host is unloading" : reason;
                _planFingerprint = string.Empty;
            }
        }

        internal bool ControllersMatch(ControllerIdentity observed)
        {
            lock (_gate) return _controllers != null && _controllers.SameAs(observed);
        }

        internal void RegisterCancellation(string id, Action cancellation)
        {
            if (cancellation == null) return;
            lock (_gate)
            {
                _cancellations.Add(new CancellationEntry
                {
                    Id = id ?? string.Empty,
                    Token = Snapshot(),
                    Cancel = cancellation
                });
            }
        }

        internal string[] CancelCurrent(string reason)
        {
            List<CancellationEntry> callbacks;
            GameEpochToken token;
            lock (_gate)
            {
                token = Snapshot();
                callbacks = _cancellations.FindAll(x =>
                    x.Token != null && x.Token.Matches(token, EpochWorkScope.ExactGameState));
                _cancellations.RemoveAll(x => callbacks.Contains(x));
            }
            var errors = new List<string>();
            for (var i = 0; i < callbacks.Count; i++)
            {
                try { callbacks[i].Cancel(); }
                catch (Exception error)
                {
                    errors.Add((callbacks[i].Id ?? "cancellation") + ": "
                               + error.GetType().Name + ": " + error.Message);
                }
            }
            return errors.ToArray();
        }

        private void TransitionBeforeCancellation(GameEpochPhase phase, string reason)
        {
            CancelCurrent(reason);
            lock (_gate)
            {
                _generation++;
                _phase = phase;
                _holdReason = reason;
                _planFingerprint = string.Empty;
            }
        }

        private void CancelAndClear(string reason)
        {
            CancelCurrent(reason);
            lock (_gate) _cancellations.Clear();
        }

        private void QuarantineLocked(string reason)
        {
            _phase = GameEpochPhase.Quarantined;
            _holdReason = string.IsNullOrEmpty(reason) ? "game epoch is quarantined" : reason;
            _planFingerprint = string.Empty;
        }

        private void QuarantineObservedLocked(string reason, SaveStateFingerprint observed)
        {
            _generation++;
            if (observed != null)
            {
                _saveContentHash = observed.EffectiveContentHash;
                _runSignature = observed.RunSignature;
            }
            QuarantineLocked(reason);
        }

        private GameEpochToken Snapshot()
        {
            return new GameEpochToken(_sessionId, _hostSequence, _saveGeneration,
                _runGeneration, _generation, _saveContentHash, _runSignature,
                _planFingerprint, _controllers);
        }
    }

    internal sealed class EpochBoundAction
    {
        internal readonly GameEpochToken Token;
        internal readonly EpochWorkScope Scope;
        internal readonly Action Action;

        internal EpochBoundAction(GameEpochToken token, EpochWorkScope scope, Action action)
        {
            Token = token;
            Scope = scope;
            Action = action;
        }
    }

    internal sealed class EpochActionQueue
    {
        private readonly object _gate = new object();
        private readonly Queue<EpochBoundAction> _queue = new Queue<EpochBoundAction>();

        internal void Enqueue(GameEpochToken token, EpochWorkScope scope, Action action)
        {
            if (action == null) return;
            lock (_gate) _queue.Enqueue(new EpochBoundAction(token, scope, action));
        }

        internal int Drain(GameEpochToken current, int maximum, Action<string> onDiscard,
            Action<Exception> onError)
        {
            var executed = 0;
            for (var i = 0; i < Math.Max(0, maximum); i++)
            {
                EpochBoundAction work;
                lock (_gate)
                {
                    if (_queue.Count == 0) break;
                    work = _queue.Dequeue();
                }
                if (work.Token == null || !work.Token.Matches(current, work.Scope))
                {
                    if (onDiscard != null) onDiscard("queued work belongs to a stale game epoch");
                    continue;
                }
                try
                {
                    work.Action();
                    executed++;
                }
                catch (Exception error)
                {
                    if (onError != null) onError(error);
                }
            }
            return executed;
        }

        internal void Clear()
        {
            lock (_gate) _queue.Clear();
        }

        internal int Count
        {
            get { lock (_gate) return _queue.Count; }
        }
    }

    internal sealed class EpochLatch<T>
    {
        private readonly object _gate = new object();
        private GameEpochToken _token;
        private T _value;
        private bool _hasValue;

        internal void Set(GameEpochToken token, T value)
        {
            lock (_gate)
            {
                _token = token;
                _value = value;
                _hasValue = true;
            }
        }

        internal bool TryGet(GameEpochToken current, out T value)
        {
            lock (_gate)
            {
                if (_hasValue && _token != null
                    && _token.Matches(current, EpochWorkScope.ExactGameState))
                {
                    value = _value;
                    return true;
                }
                value = default(T);
                return false;
            }
        }

        internal bool TryTake(GameEpochToken current, out T value)
        {
            lock (_gate)
            {
                if (_hasValue && _token != null
                    && _token.Matches(current, EpochWorkScope.ExactGameState))
                {
                    value = _value;
                    _hasValue = false;
                    _token = null;
                    _value = default(T);
                    return true;
                }
                value = default(T);
                return false;
            }
        }

        internal void Clear()
        {
            lock (_gate)
            {
                _hasValue = false;
                _token = null;
                _value = default(T);
            }
        }
    }

    internal sealed class DurableGenerationResult
    {
        internal readonly string PublishedPath;
        internal readonly string PreviousGenerationPath;
        internal readonly string PublishedSha256;

        internal DurableGenerationResult(string publishedPath, string previousGenerationPath,
            string publishedSha256)
        {
            PublishedPath = publishedPath ?? string.Empty;
            PreviousGenerationPath = previousGenerationPath ?? string.Empty;
            PublishedSha256 = publishedSha256 ?? string.Empty;
        }
    }

    internal static class DurableGenerationWriter
    {
        internal static DurableGenerationResult WriteText(string path, string contents,
            Func<string, bool> validator = null)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is required", "path");
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("destination has no parent directory");
            Directory.CreateDirectory(directory);

            var temp = fullPath + ".pending." + Guid.NewGuid().ToString("N");
            var bytes = new UTF8Encoding(false).GetBytes(contents ?? string.Empty);
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (validator != null && !validator(temp))
                    throw new InvalidDataException("candidate generation failed validation");

                var previous = string.Empty;
                if (File.Exists(fullPath))
                {
                    previous = fullPath + ".last-good."
                               + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ") + "."
                               + Guid.NewGuid().ToString("N");
                    File.Replace(temp, fullPath, previous);
                }
                else
                {
                    File.Move(temp, fullPath);
                }
                return new DurableGenerationResult(fullPath, previous,
                    EpochHash.Sha256(File.ReadAllText(fullPath)));
            }
            finally
            {
                // Only a never-published temporary candidate is disposable. The destination and
                // every successful last-good generation are intentionally never removed here.
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { }
            }
        }
    }
}
