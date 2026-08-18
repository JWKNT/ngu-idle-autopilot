using System;
using System.Collections.Generic;

/*
FILE PURPOSE

This isolated executable regression-tests ExecutionSafety without loading Unity, the installed
game assembly, a save, or runtime configuration. Minimal stubs supply only the policy fields the
lease boundary reads. Tests cover unconditional dry-run veto, assist finite/irreversible denial,
single-owner handoff, and state-version invalidation after a hot policy change.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class AutopilotConfig
    {
        internal bool Enabled = true;
        internal string Mode = "dry-run";
        internal bool AutoEnterGame = true;
        internal bool AllowLegacyFallback = true;
        internal bool ManageAllocations = true;
        internal bool ManageBosses = true;
        internal bool ManageAdventure = true;
        internal bool ManageInventory = true;
        internal bool ManageDiggers = true;
        internal bool ManageYggdrasil = true;
        internal bool ManageQuests = true;
        internal bool ManageWishes = true;
        internal bool ManageCards = true;
        internal bool ManageCooking = true;
        internal bool ManageMoneyPit = true;
        internal bool ManageDailySpin = true;
        internal bool ManageBloodMagic = true;
        internal bool ManageBeards = true;
        internal bool AllowExpSpending = true;
        internal bool AllowApSpending = true;
        internal bool AllowPerkSpending = true;
        internal bool AllowQuirkSpending = true;
        internal bool AllowCardYeeting = true;
        internal bool AllowRebirths = true;
        internal bool AllowChallenges = true;
        internal bool AllowEndSequence = true;
        internal bool AllowVerifiedReversibleActions = true;
        internal bool AllowGlobalSchedulerExecution;
        internal bool AllowPermanentPurchaseExecution;
        internal bool AllowMoneyPitExecution;
        internal bool AllowDifficultyExecution;
        internal bool AllowTitanOneThroughTwelveExecution;
        internal bool AllowTitanThirteenFourteenExecution;
        internal bool AllowMove69Execution;

        internal bool IsDryRun { get { return Mode != "assist" && Mode != "full"; } }
        internal bool IsAssist { get { return Mode == "assist"; } }
        internal bool IsFull { get { return Mode == "full"; } }

        internal string ExecutionFingerprint()
        {
            return Enabled + "|" + Mode + "|" + ManageAllocations + "|" + ManageDiggers
                   + "|" + ManageInventory + "|" + AllowRebirths + "|" + AllowEndSequence
                   + "|" + AllowVerifiedReversibleActions + "|"
                   + AllowGlobalSchedulerExecution + "|" + AllowPermanentPurchaseExecution
                   + "|" + AllowMoneyPitExecution + "|" + AllowDifficultyExecution + "|"
                   + AllowTitanOneThroughTwelveExecution + "|"
                   + AllowTitanThirteenFourteenExecution + "|" + AllowMove69Execution;
        }
    }

    internal sealed class AutopilotManager
    {
        internal AutopilotConfig Config;
    }
}

namespace NGUInjector
{
    using NGUInjector.Autopilot;

    internal sealed class SavedSettings
    {
        internal bool GlobalEnabled = true;
    }

    internal static class Main
    {
        internal static AutopilotManager Autopilot;
        internal static SavedSettings Settings = new SavedSettings();
        internal static readonly List<string> Holds = new List<string>();

        internal static void LogAction(string category, string detail)
        {
            Holds.Add(category + ":" + detail);
        }
    }

    internal static class ExecutionSafetyRegressionTests
    {
        private static int _assertions;

        private static void Assert(bool value, string message)
        {
            _assertions++;
            if (!value) throw new Exception("FAIL: " + message);
        }

        private static bool Acquire(MutationClass mutationClass, MutationOwner owner,
            out MutationLease lease)
        {
            string reason;
            return ExecutionSafety.TryAcquire(mutationClass, owner, out lease, out reason);
        }

        public static int Main()
        {
            TestDryRunVeto();
            TestAssistMatrix();
            TestExclusiveOwnerHandoff();
            TestStaleLeaseInvalidation();
            TestStagedAuthorityInvalidation();
            Console.WriteLine("PASS: " + _assertions + " execution-safety assertions");
            return 0;
        }

        private static void TestDryRunVeto()
        {
            var config = new AutopilotConfig {Mode = "dry-run"};
            NGUInjector.Main.Autopilot = new AutopilotManager {Config = config};
            using (ExecutionSafety.BeginCycle("dry-run", config))
            {
                MutationLease lease;
                Assert(!Acquire(MutationClass.Combat, MutationOwner.Autopilot, out lease),
                    "dry-run must deny Autopilot combat");
                Assert(!Acquire(MutationClass.Inventory, MutationOwner.Legacy, out lease),
                    "dry-run must deny legacy inventory");
                Assert(!Acquire(MutationClass.SaveLoad, MutationOwner.User, out lease),
                    "dry-run must deny user-routed native mutations through the bot");
            }
        }

        private static void TestAssistMatrix()
        {
            var config = new AutopilotConfig {Mode = "assist"};
            NGUInjector.Main.Autopilot.Config = config;
            using (ExecutionSafety.BeginCycle("assist", config))
            {
                MutationLease lease;
                Assert(Acquire(MutationClass.Combat, MutationOwner.Autopilot, out lease),
                    "assist should permit reversible combat routing");
                Assert(Acquire(MutationClass.Cards, MutationOwner.Autopilot, out lease),
                    "assist should permit non-consuming Card policy");
                Assert(!Acquire(MutationClass.Inventory, MutationOwner.Autopilot, out lease),
                    "assist must deny inventory consumption/topology");
                Assert(!Acquire(MutationClass.Diggers, MutationOwner.Autopilot, out lease),
                    "assist must deny Gold-consuming Diggers");
                Assert(!Acquire(MutationClass.BloodMagic, MutationOwner.Autopilot, out lease),
                    "assist must deny Blood spending");
                Assert(!Acquire(MutationClass.Rebirth, MutationOwner.Autopilot, out lease),
                    "assist must deny rebirth");
                Assert(!Acquire(MutationClass.EndSequence, MutationOwner.Autopilot, out lease),
                    "assist must deny the terminal sequence");
            }
        }

        private static void TestExclusiveOwnerHandoff()
        {
            var config = new AutopilotConfig {Mode = "full", ManageAllocations = true};
            NGUInjector.Main.Autopilot.Config = config;
            using (ExecutionSafety.BeginCycle("autopilot-owner", config))
            {
                MutationLease lease;
                Assert(Acquire(MutationClass.Allocation, MutationOwner.Autopilot, out lease),
                    "enabled Autopilot allocation owner should acquire");
                Assert(!Acquire(MutationClass.Allocation, MutationOwner.Legacy, out lease),
                    "legacy writer must not share an Autopilot-owned class");
            }

            config.ManageAllocations = false;
            using (ExecutionSafety.BeginCycle("legacy-owner", config))
            {
                MutationLease lease;
                Assert(!Acquire(MutationClass.Allocation, MutationOwner.Autopilot, out lease),
                    "disabled Autopilot feature must relinquish ownership");
                Assert(Acquire(MutationClass.Allocation, MutationOwner.Legacy, out lease),
                    "legacy writer should acquire after explicit handoff");
            }
        }

        private static void TestStaleLeaseInvalidation()
        {
            var config = new AutopilotConfig {Mode = "full"};
            NGUInjector.Main.Autopilot.Config = config;
            using (ExecutionSafety.BeginCycle("stale", config))
            {
                MutationLease lease;
                Assert(Acquire(MutationClass.Combat, MutationOwner.Autopilot, out lease),
                    "initial lease should acquire");
                ExecutionSafety.Invalidate("test state change");
                Assert(!lease.IsCurrent, "state invalidation must stale an issued lease");
                MutationLease second;
                Assert(!Acquire(MutationClass.Combat, MutationOwner.Autopilot, out second),
                    "the invalidated cycle must not issue another lease");
            }
        }

        private static void TestStagedAuthorityInvalidation()
        {
            var config = new AutopilotConfig {Mode = "full"};
            NGUInjector.Main.Autopilot.Config = config;
            using (ExecutionSafety.BeginCycle("staged-authority", config))
            {
                MutationLease lease;
                Assert(Acquire(MutationClass.Combat, MutationOwner.Autopilot, out lease),
                    "verified reversible stage initially acquires its lease");
                config.AllowDifficultyExecution = true;
                ExecutionSafety.ObserveConfig(config);
                Assert(!lease.IsCurrent,
                    "changing a staged authority bit invalidates all issued leases");
            }
        }
    }
}
