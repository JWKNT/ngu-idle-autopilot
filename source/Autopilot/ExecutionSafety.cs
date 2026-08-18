using System;
using System.Collections.Generic;
using System.Threading;

/*
FILE PURPOSE

ExecutionSafety owns the immutable admission snapshot used by a nonzero root mutation transaction.
It freezes autopilot mode and feature ownership for one scheduler pass, issues class-specific child
leases, and invalidates those leases whenever synchronization, configuration, or profile ownership
changes. Inputs are AutopilotConfig plus explicit mutation class/owner; outputs are immutable leases
and throttled HOLD telemetry. A dry-run snapshot never grants a game mutation, assist never grants
finite-resource or irreversible automation, and legacy/autopilot writers may not simultaneously own
the same class. There is deliberately no unscoped/cycle-zero fallback: callers outside a root receive
NoActiveTransaction, and overlapping roots are rejected instead of replacing the active snapshot.
MutationCoordinator owns invocation, exact postconditions, compensation, quarantine, and journaling;
this file remains the policy/admission component so existing callers can migrate incrementally.
*/
namespace NGUInjector.Autopilot
{
    internal enum MutationClass
    {
        Synchronization,
        AutopilotPolicy,
        Allocation,
        Combat,
        Adventure,
        Loadout,
        TitanLoadout,
        YggdrasilLoadout,
        MoneyPitLoadout,
        GoldLoadout,
        Diggers,
        Beards,
        Inventory,
        Daycare,
        Yggdrasil,
        Quests,
        Wishes,
        Cards,
        Cooking,
        MoneyPit,
        DailySpin,
        BloodMagic,
        PermanentSpend,
        Rebirth,
        Challenge,
        EndSequence,
        SaveLoad,
        Difficulty
    }

    internal enum MutationOwner
    {
        Legacy,
        Autopilot,
        User,
        System
    }

    internal enum MutationRisk
    {
        Reversible,
        FiniteResource,
        Irreversible,
        MixedPolicy
    }

    internal sealed class MutationLease
    {
        internal readonly MutationClass Class;
        internal readonly MutationRisk Risk;
        internal readonly MutationOwner Owner;
        internal readonly long StateVersion;
        internal readonly long CycleId;
        internal readonly string CycleName;

        internal MutationLease(MutationClass mutationClass, MutationRisk risk, MutationOwner owner,
            long stateVersion, long cycleId, string cycleName)
        {
            Class = mutationClass;
            Risk = risk;
            Owner = owner;
            StateVersion = stateVersion;
            CycleId = cycleId;
            CycleName = cycleName ?? string.Empty;
        }

        internal bool IsCurrent
        {
            get { return ExecutionSafety.IsCurrent(this); }
        }

        internal long RootTransactionId
        {
            get { return CycleId; }
        }
    }

    internal sealed class ExecutionCycle : IDisposable
    {
        private readonly long _cycleId;
        private readonly long _stateVersion;
        private bool _disposed;

        internal ExecutionCycle(long cycleId, long stateVersion)
        {
            if (cycleId <= 0) throw new ArgumentOutOfRangeException("cycleId");
            _cycleId = cycleId;
            _stateVersion = stateVersion;
        }

        internal long CycleId { get { return _cycleId; } }
        internal long StateVersion { get { return _stateVersion; } }
        internal bool IsDisposed { get { return _disposed; } }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ExecutionSafety.EndCycle(_cycleId);
        }
    }

    internal static class ExecutionSafety
    {
        private sealed class Snapshot
        {
            internal long CycleId;
            internal long StateVersion;
            internal string Name;
            internal AutopilotConfig Config;
            internal bool ConfigEnabled;
            internal bool DryRun;
            internal bool Assist;
            internal bool Full;
            internal bool AllowLegacyFallback;
            internal Dictionary<MutationClass, bool> AutopilotOwnership;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, DateTime> LastHold =
            new Dictionary<string, DateTime>();
        private static Snapshot _active;
        private static long _cycleSequence;
        private static long _stateVersion = 1;
        private static string _configFingerprint = string.Empty;

        internal static long StateVersion
        {
            get { return Interlocked.Read(ref _stateVersion); }
        }

        internal static ExecutionCycle BeginCycle(string name, AutopilotConfig config)
        {
            ExecutionCycle cycle;
            string reason;
            if (!TryBeginCycle(name, config, out cycle, out reason))
                throw new InvalidOperationException(reason);
            return cycle;
        }

        /*
        ROOT ADMISSION

        A root is exclusive and always has an ID greater than zero.  In particular, a nested
        scheduler callback cannot silently replace the outer snapshot and then clear it when the
        inner scope exits. MutationCoordinator uses the non-throwing form so root rejection becomes
        a typed result; BeginCycle remains as a compatibility adapter for callers migrated later.
        */
        internal static bool TryBeginCycle(string name, AutopilotConfig config,
            out ExecutionCycle cycle, out string reason)
        {
            cycle = null;
            reason = string.Empty;
            ObserveConfig(config);
            lock (Gate)
            {
                if (_active != null)
                {
                    reason = "NestedRootTransaction: root " + _active.CycleId
                             + " (" + _active.Name + ") is already active";
                    return false;
                }

                var cycleId = ++_cycleSequence;
                if (cycleId <= 0)
                {
                    // Overflow is not realistic in a game process, but ID zero/negative is never
                    // allowed to regain its former unscoped meaning.
                    _cycleSequence = 1;
                    cycleId = 1;
                }
                _active = CreateSnapshot(cycleId, name, config);
                cycle = new ExecutionCycle(cycleId, _active.StateVersion);
                return true;
            }
        }

        internal static void ObserveConfig(AutopilotConfig config)
        {
            var fingerprint = config == null ? "<none>" : config.ExecutionFingerprint();
            lock (Gate)
            {
                if (string.Equals(fingerprint, _configFingerprint, StringComparison.Ordinal))
                    return;
                _configFingerprint = fingerprint;
                Interlocked.Increment(ref _stateVersion);
            }
        }

        internal static void Invalidate(string reason)
        {
            Interlocked.Increment(ref _stateVersion);
            ReportHold("state-version", "Execution state invalidated: " + (reason ?? "unspecified state change"), 1);
        }

        internal static bool TryAcquire(MutationClass mutationClass, MutationOwner owner,
            out MutationLease lease, out string reason)
        {
            return TryAcquire(mutationClass, RiskFor(mutationClass), owner, out lease, out reason);
        }

        internal static bool TryAcquire(MutationClass mutationClass, MutationRisk declaredRisk,
            MutationOwner owner, out MutationLease lease, out string reason)
        {
            lease = null;
            reason = string.Empty;
            Snapshot snapshot;
            lock (Gate)
            {
                snapshot = _active;
            }

            if (snapshot == null || snapshot.CycleId <= 0)
            {
                reason = "NoActiveTransaction: native mutations require a nonzero root transaction";
                return false;
            }

            if (snapshot.StateVersion != StateVersion)
            {
                reason = "execution state changed after this scheduler pass began";
                return false;
            }
            if (snapshot.DryRun)
            {
                reason = "dry-run is an unconditional native-mutation veto";
                return false;
            }
            if (owner == MutationOwner.Autopilot && !snapshot.ConfigEnabled)
            {
                reason = "autopilot is disabled";
                return false;
            }
            if (owner == MutationOwner.Legacy && snapshot.ConfigEnabled)
            {
                if (!snapshot.AllowLegacyFallback)
                {
                    reason = "legacy mutation fallback is disabled while autopilot owns execution";
                    return false;
                }
                if (SnapshotAutopilotOwns(snapshot, mutationClass))
                {
                    reason = "autopilot already owns this mutation class";
                    return false;
                }
            }
            if (owner == MutationOwner.Legacy && Main.Settings != null
                && !Main.Settings.GlobalEnabled)
            {
                reason = "legacy GlobalEnabled is off";
                return false;
            }
            if (owner == MutationOwner.Autopilot
                && !SnapshotAutopilotOwns(snapshot, mutationClass))
            {
                reason = "the corresponding autopilot feature does not own this mutation class";
                return false;
            }

            var risk = EffectiveRisk(RiskFor(mutationClass), declaredRisk);
            if (snapshot.Assist
                && (risk == MutationRisk.FiniteResource || risk == MutationRisk.Irreversible))
            {
                reason = "assist mode cannot spend finite resources or perform irreversible mutations";
                return false;
            }
            if (snapshot.ConfigEnabled && !snapshot.Assist && !snapshot.Full
                && owner != MutationOwner.User)
            {
                reason = "the configured execution mode is neither assist nor full";
                return false;
            }

            lease = new MutationLease(mutationClass, risk, owner, snapshot.StateVersion,
                snapshot.CycleId, snapshot.Name);
            return true;
        }

        internal static bool IsCurrent(MutationLease lease)
        {
            if (lease == null || lease.CycleId <= 0 || lease.StateVersion != StateVersion)
                return false;
            lock (Gate)
            {
                return _active != null && _active.CycleId == lease.CycleId;
            }
        }

        internal static bool IsRootCurrent(long rootTransactionId, long stateVersion)
        {
            if (rootTransactionId <= 0 || stateVersion != StateVersion) return false;
            lock (Gate)
            {
                return _active != null && _active.CycleId == rootTransactionId
                       && _active.StateVersion == stateVersion;
            }
        }

        internal static long ActiveRootTransactionId
        {
            get
            {
                lock (Gate) return _active == null ? 0 : _active.CycleId;
            }
        }

        internal static void ReportHold(string key, string detail, int minimumSeconds = 15)
        {
            var now = DateTime.UtcNow;
            lock (Gate)
            {
                DateTime last;
                if (LastHold.TryGetValue(key ?? string.Empty, out last)
                    && (now - last).TotalSeconds < Math.Max(1, minimumSeconds))
                    return;
                LastHold[key ?? string.Empty] = now;
                if (LastHold.Count > 128)
                {
                    var cutoff = now.AddMinutes(-10);
                    foreach (var stale in new List<string>(LastHold.Keys))
                        if (LastHold[stale] < cutoff) LastHold.Remove(stale);
                }
            }
            Main.LogAction("HOLD", detail);
        }

        internal static MutationOwner OwnerFor(MutationClass mutationClass)
        {
            lock (Gate)
            {
                if (_active != null)
                    return SnapshotAutopilotOwns(_active, mutationClass)
                        ? MutationOwner.Autopilot : MutationOwner.Legacy;
            }
            var config = CurrentConfig();
            return config != null && config.Enabled && AutopilotOwns(config, mutationClass)
                ? MutationOwner.Autopilot : MutationOwner.Legacy;
        }

        private static Snapshot CreateSnapshot(long cycleId, string name, AutopilotConfig config)
        {
            var ownership = new Dictionary<MutationClass, bool>();
            foreach (MutationClass mutationClass in Enum.GetValues(typeof(MutationClass)))
                ownership[mutationClass] = AutopilotOwns(config, mutationClass);
            return new Snapshot
            {
                CycleId = cycleId,
                StateVersion = StateVersion,
                Name = name ?? string.Empty,
                Config = config,
                ConfigEnabled = config != null && config.Enabled,
                DryRun = config != null && config.Enabled && config.IsDryRun,
                Assist = config != null && config.Enabled && config.IsAssist,
                Full = config != null && config.Enabled && config.IsFull,
                AllowLegacyFallback = config == null || config.AllowLegacyFallback,
                AutopilotOwnership = ownership
            };
        }

        private static bool SnapshotAutopilotOwns(Snapshot snapshot,
            MutationClass mutationClass)
        {
            bool owns;
            return snapshot != null && snapshot.AutopilotOwnership != null
                   && snapshot.AutopilotOwnership.TryGetValue(mutationClass, out owns) && owns;
        }

        private static AutopilotConfig CurrentConfig()
        {
            return Main.Autopilot == null ? null : Main.Autopilot.Config;
        }

        internal static MutationRisk RiskFor(MutationClass mutationClass)
        {
            switch (mutationClass)
            {
                case MutationClass.Inventory:
                case MutationClass.Daycare:
                case MutationClass.Yggdrasil:
                case MutationClass.Quests:
                case MutationClass.MoneyPit:
                case MutationClass.DailySpin:
                case MutationClass.BloodMagic:
                case MutationClass.Allocation:
                case MutationClass.Adventure:
                case MutationClass.Loadout:
                case MutationClass.TitanLoadout:
                case MutationClass.YggdrasilLoadout:
                case MutationClass.MoneyPitLoadout:
                case MutationClass.GoldLoadout:
                case MutationClass.Diggers:
                    return MutationRisk.FiniteResource;
                case MutationClass.PermanentSpend:
                case MutationClass.Rebirth:
                case MutationClass.Challenge:
                case MutationClass.Difficulty:
                case MutationClass.EndSequence:
                case MutationClass.SaveLoad:
                    return MutationRisk.Irreversible;
                case MutationClass.AutopilotPolicy:
                    return MutationRisk.MixedPolicy;
                default:
                    return MutationRisk.Reversible;
            }
        }

        private static MutationRisk EffectiveRisk(MutationRisk classRisk,
            MutationRisk declaredRisk)
        {
            if (classRisk == MutationRisk.Irreversible
                || declaredRisk == MutationRisk.Irreversible)
                return MutationRisk.Irreversible;
            if (classRisk == MutationRisk.FiniteResource
                || declaredRisk == MutationRisk.FiniteResource)
                return MutationRisk.FiniteResource;
            if (classRisk == MutationRisk.MixedPolicy
                || declaredRisk == MutationRisk.MixedPolicy)
                return MutationRisk.MixedPolicy;
            return MutationRisk.Reversible;
        }

        private static bool AutopilotOwns(AutopilotConfig config, MutationClass mutationClass)
        {
            if (config == null || !config.Enabled) return false;
            switch (mutationClass)
            {
                case MutationClass.Synchronization: return config.AutoEnterGame;
                case MutationClass.AutopilotPolicy: return true;
                case MutationClass.Allocation: return config.ManageAllocations;
                case MutationClass.Combat: return config.ManageBosses;
                case MutationClass.Adventure: return config.ManageAdventure;
                case MutationClass.Loadout:
                    return config.ManageAdventure || config.ManageBosses || config.ManageInventory
                           || config.ManageAllocations;
                case MutationClass.TitanLoadout: return config.ManageAdventure;
                case MutationClass.YggdrasilLoadout: return config.ManageYggdrasil;
                case MutationClass.MoneyPitLoadout: return config.ManageMoneyPit;
                case MutationClass.GoldLoadout: return false;
                case MutationClass.Diggers: return config.ManageDiggers;
                case MutationClass.Beards: return config.ManageBeards;
                case MutationClass.Inventory: return config.ManageInventory;
                case MutationClass.Daycare: return config.ManageInventory;
                case MutationClass.Yggdrasil: return config.ManageYggdrasil;
                case MutationClass.Quests: return config.ManageQuests;
                case MutationClass.Wishes: return config.ManageWishes;
                case MutationClass.Cards: return config.ManageCards;
                case MutationClass.Cooking: return config.ManageCooking;
                case MutationClass.MoneyPit: return config.ManageMoneyPit;
                case MutationClass.DailySpin: return config.ManageDailySpin;
                case MutationClass.BloodMagic: return config.ManageBloodMagic;
                case MutationClass.PermanentSpend:
                    return config.AllowExpSpending || config.AllowApSpending
                           || config.AllowPerkSpending || config.AllowQuirkSpending
                           || config.AllowCardYeeting;
                case MutationClass.Rebirth: return config.AllowRebirths;
                case MutationClass.Challenge: return config.AllowChallenges;
                // Difficulty authority is introduced by the dedicated transition executor. Until
                // its independently configured gate is integrated, new intents remain fail-closed.
                case MutationClass.Difficulty: return false;
                case MutationClass.EndSequence: return config.AllowEndSequence;
                case MutationClass.SaveLoad: return false;
                default: return false;
            }
        }

        internal static void EndCycle(long cycleId)
        {
            lock (Gate)
            {
                if (_active != null && _active.CycleId == cycleId)
                    _active = null;
            }
        }
    }
}
