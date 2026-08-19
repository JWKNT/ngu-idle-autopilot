using System;
using System.IO;
using UnityEngine;

/*
FILE PURPOSE

AutopilotConfig is the durable user boundary for execution mode, subsystem ownership, reserves,
and finite/irreversible permissions. It loads runtime/autopilot.json into conservative defaults
and supplies the permission fingerprint captured by ExecutionSafety for each scheduler pass.
Inputs are the JSON file and defaults; outputs are policy flags only—this class never touches a
native controller. Dry-run remains a hard mutation veto, assist never gains finite-resource
authority from these flags, and an enabled autopilot class preempts its legacy writer. New knobs
belong here only when live optimization cannot derive a safe choice; mechanics and strategy do not.
*/
namespace NGUInjector.Autopilot
{
    [Serializable]
    internal class AutopilotConfig
    {
        public bool Enabled = true;
        public string Mode = "dry-run";
        public string Goal = "progression";
        public bool AutoEnterGame = true;
        public bool AllowLegacyFallback = true;

        public bool ManageAllocations = true;
        public bool ManageBosses = true;
        public bool ManageAdventure = true;
        public bool ManageInventory = true;
        public bool ManageDiggers = true;
        public bool ManageYggdrasil = true;
        public bool ManageQuests = true;
        public bool ManageWishes = true;
        public bool ManageCards = true;
        public bool ManageCooking = true;
        public bool ManageMoneyPit = true;
        public bool ManageDailySpin = true;
        public bool ManageBloodMagic = true;
        public bool ManageBeards = true;

        public bool AllowExpSpending = false;
        public bool AllowApSpending = false;
        public bool AllowRebirths = false;
        public bool AllowChallenges = false;
        public bool AllowCardYeeting = false;
        public bool AllowPerkSpending = false;
        public bool AllowQuirkSpending = false;
        public bool AllowEndSequence = false;

        // Staged authority ceiling. These flags document the deployment boundary as data. Ordinary
        // rebirth and the narrow typed Normal-challenge wave are separately admitted by exact
        // preview/reset transactions; difficulty authority remains independent and fail-closed.
        public bool AllowVerifiedReversibleActions = true;
        public bool AllowGlobalSchedulerExecution = false;
        public bool AllowPermanentPurchaseExecution = false;
        public bool AllowMoneyPitExecution = false;
        public bool AllowDifficultyExecution = false;
        public bool AllowTitanOneThroughTwelveExecution = false;
        public bool AllowTitanThirteenFourteenExecution = false;
        public bool AllowMove69Execution = false;

        public long ExpReserve = 0;
        public long ApReserve = 0;
        public long PPReserve = 0;
        public long QPReserve = 0;
        public double MoneyPitReserve = 100000.0;
        public int DecisionIntervalSeconds = 1;
        public int TitanPreflightBackoffSeconds = 15;

        public static AutopilotConfig LoadOrCreate(string path)
        {
            if (File.Exists(path))
            {
                var loaded = JsonUtility.FromJson<AutopilotConfig>(File.ReadAllText(path));
                if (loaded != null)
                    return ApplyDeploymentAuthorityCeiling(loaded);
            }

            var config = ApplyDeploymentAuthorityCeiling(new AutopilotConfig());
            config.Save(path);
            return config;
        }

        internal static AutopilotConfig ApplyDeploymentAuthorityCeiling(
            AutopilotConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");
            // These routes remain modeled/read-only until their named copied-save fixtures,
            // build bindings, and backtests are explicitly completed in a later deployment.
            // EXP atoms represented by LivePermanentPurchaseRuntime now have build-pinned
            // invocation plus exact debit/effect settlement. Preserve the operator's explicit
            // AllowExpSpending choice and publish that same authority to the staged plan.
            config.AllowApSpending = false;
            // PerkPurchaseIntent now owns one native perk level per root and proves the exact
            // PP debit, level transition, and terminal item delivery. Preserve the operator's
            // explicit choice instead of silently narrowing it at deployment.
            config.AllowQuirkSpending = false;
            // ResetProgressionTransaction is the sole challenge-entry writer. Preserve the
            // operator's explicit choice; the planner still admits only the audited Normal subset,
            // and every entry repeats Titan/fruit/Blood/root/native postcondition gates.
            config.AllowEndSequence = false;
            config.AllowGlobalSchedulerExecution = false;
            config.AllowPermanentPurchaseExecution = config.AllowExpSpending;
            // Money Pit has a typed all-Gold transaction, chronological hard-spend ledger, exact
            // delivery preflight, and exact debit/tier postconditions. Preserve its separate
            // explicit irreversible flag instead of erasing both policy and manager ownership.
            config.AllowDifficultyExecution = false;
            // T1-T12 now execute one build-pinned, epoch-bound native frame at a time and prove
            // selected-version Bestiary plus full clock-reset deltas. Preserve explicit authority.
            config.AllowTitanThirteenFourteenExecution = false;
            config.AllowMove69Execution = false;
            return config;
        }

        internal bool GlobalSchedulerIsShadowOnly
        {
            get { return true; }
        }

        public void Save(string path)
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllText(path, JsonUtility.ToJson(this, true));
        }

        public bool IsDryRun
        {
            get { return !string.Equals(Mode, "assist", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(Mode, "full", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsAssist
        {
            get { return string.Equals(Mode, "assist", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsFull
        {
            get { return string.Equals(Mode, "full", StringComparison.OrdinalIgnoreCase); }
        }

        /*
        EXECUTION-PERMISSION FINGERPRINT

        A scheduler lease must remain sticky even if a file watcher or planner observes a new
        configuration halfway through a transaction. Include every field that can change mutation
        ownership or authority; numerical strategy reserves deliberately do not invalidate a lease
        because they are consumed by the next plan, never by this permission boundary.
        */
        internal string ExecutionFingerprint()
        {
            return Enabled + "|" + (Mode ?? string.Empty).ToLowerInvariant()
                   + "|" + AutoEnterGame + "|" + AllowLegacyFallback
                   + "|" + ManageAllocations + "|" + ManageBosses + "|" + ManageAdventure
                   + "|" + ManageInventory + "|" + ManageDiggers + "|" + ManageYggdrasil
                   + "|" + ManageQuests + "|" + ManageWishes + "|" + ManageCards
                   + "|" + ManageCooking + "|" + ManageMoneyPit + "|" + ManageDailySpin
                   + "|" + ManageBloodMagic + "|" + ManageBeards
                   + "|" + AllowExpSpending + "|" + AllowApSpending + "|" + AllowRebirths
                   + "|" + AllowChallenges + "|" + AllowCardYeeting + "|" + AllowPerkSpending
                   + "|" + AllowQuirkSpending + "|" + AllowEndSequence
                   + "|" + AllowVerifiedReversibleActions
                   + "|" + AllowGlobalSchedulerExecution
                   + "|" + AllowPermanentPurchaseExecution
                   + "|" + AllowMoneyPitExecution
                   + "|" + AllowDifficultyExecution
                   + "|" + AllowTitanOneThroughTwelveExecution
                   + "|" + AllowTitanThirteenFourteenExecution
                   + "|" + AllowMove69Execution;
        }
    }
}
