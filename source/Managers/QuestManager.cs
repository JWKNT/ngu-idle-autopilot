using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Autopilot;
using static NGUInjector.Main;

/*
FILE PURPOSE

Purpose: QuestManager owns the event policy and verified native transitions for Beast Quests.  It
separates reward completion from the free-minor Item List MAXX campaign, stages the progression-
critical Antlers hybrid condition, and treats the major bank as a dated stock rather than a count.

Mechanism: QuestEventController is a controller-free oracle over an immutable snapshot.  It uses
the task-24 negative-binomial kernel for manual-drop tails, task-23 collection state for physical
quest-item development, task-6 usable capacity for Antlers, and exact native timer/eligibility
facts.  The live manager translates one selected action into task-1 child intents on a caller-owned
nonzero RootTransaction.  Butter and completion are two verified children in the same root and
Butter is never spent before readyToHandIn is true.

Inputs and outputs: Inputs are live BeastQuest fields, Item List flags, ordinary inventory
topology, Adventure feasibility, settings, and the caller's root transaction.  Outputs are a typed
QuestPolicyPlan, LastDecision telemetry, an Adventure zone handoff, and verified QUEST/REJECTED
logs.  CurrentMode is the integration hook that must gate InventoryManager: CompleteQuest may offer
surplus copies, while MaxxAndSkipMinor must merge all matching copies and offer none.

Invariants and safety: A major is never skipped.  A Buttered quest is never skipped.  Antlers
completion requires item 337, the native truth-table state, and an exact usable-slot proof for item
338.  Idle staging never authorizes a tick crossing one.  MAXX rerolls are minor-only and bounded to
one replacement per manager pass.  No parameterless compatibility entry point invokes a native
mutation; task 29 must pass the active root explicitly.

Extension points and non-goals: The global scheduler may replace typed opportunity values and live
cadence estimates.  This file does not merge inventory objects, own Adventure combat, preview the
saved quest RNG, enable native autocycle, or mutate another manager's state.
*/
namespace NGUInjector.Managers
{
    internal enum QuestExecutionMode
    {
        CompleteQuest,
        MaxxAndSkipMinor,
        AntlersHybrid
    }

    internal enum QuestEventAction
    {
        Hold,
        StartMajor,
        StartMinor,
        RerollMinor,
        SkipMinor,
        EnableIdle,
        DisableIdle,
        RouteManual,
        Complete
    }

    internal sealed class QuestManualFeasibility
    {
        internal bool Online = true;
        internal bool ZoneUnlocked = true;
        internal bool ZoneSurvivable = true;
        internal bool AdventureAvailable = true;
        internal bool TitanPreempted;
        internal bool CapacityAdmitted = true;

        internal bool Feasible
        {
            get
            {
                return Online && ZoneUnlocked && ZoneSurvivable && AdventureAvailable
                       && !TitanPreempted && CapacityAdmitted;
            }
        }

        internal string Blocker
        {
            get
            {
                if (!Online) return "offline";
                if (!ZoneUnlocked) return "quest zone locked";
                if (!ZoneSurvivable) return "quest zone not survivable";
                if (!AdventureAvailable) return "Adventure unavailable";
                if (TitanPreempted) return "Titan preemption";
                if (!CapacityAdmitted) return "no usable quest-drop inventory slot";
                return string.Empty;
            }
        }
    }

    internal sealed class QuestPolicySnapshot
    {
        internal bool Active;
        internal bool Minor;
        internal int QuestId;
        internal int Target;
        internal int Current;
        internal bool IdleMode;
        internal bool AllActive = true;
        internal double IdleProgress;
        internal double IdleIncrementPerTick;
        internal bool UsedButter;
        internal bool Ready;
        internal int Banked;
        internal int BankCapacity;
        internal double BankTimerSeconds;
        internal int BankIntervalSeconds = 28200;
        internal bool AllowMajor = true;
        internal int MaxxTargetId = -1;
        internal bool MaxxTargetComplete;
        internal bool NeedAntlers;
        internal bool Item337Dropped;
        internal bool Item338PhysicallyOwned;
        internal bool AntlersCapacityAdmitted;
        internal bool PreferManualMinor;
        internal double CompletionMeanSeconds = double.PositiveInfinity;
        internal double MinorRewardValue;
        internal double LostMajorValue;
        internal QuestManualFeasibility Manual = new QuestManualFeasibility();
    }

    internal sealed class QuestPolicyPlan
    {
        internal QuestExecutionMode Mode;
        internal QuestEventAction Action;
        internal int QuestId;
        internal int TargetQuestId;
        internal double SecondsToNextBankArrival;
        internal double SecondsToBankOverflow;
        internal string Reason = string.Empty;
    }

    internal sealed class QuestManualForecast
    {
        internal CompletionForecast Trials;
        internal double MeanSeconds;
        internal double P90Seconds;
        internal double SuccessProbability;
        internal string Evidence;
    }

    internal sealed class QuestItemDevelopment
    {
        internal int ItemId;
        internal bool Eligible;
        internal bool Maxxed;
        internal int RemainingContribution;
    }

    /*
    PURE QUEST EVENT ORACLE

    Policy precedence is progression constraint, MAXX campaign, major preservation, then routine
    reward completion.  Antlers is a constrained hybrid state rather than an average manual/idle
    preference.  The bank deadline is the first arrival that would be clamped at capacity, so a
    cap-1 bank has two intervals rather than the old count-only near-cap shortcut.
    */
    internal static class QuestEventController
    {
        internal const int BaseBankIntervalSeconds = 28200;
        internal const double AntlersMinimumFraction = .90;
        internal const double AntlersStagingCeiling = .98;

        internal static int BankIntervalSeconds(bool fasterQuests, bool fadComplete)
        {
            var result = BaseBankIntervalSeconds;
            if (fasterQuests) result = (int)(result * .8f);
            if (fadComplete) result = (int)(result * .9f);
            return result;
        }

        internal static double SecondsToNextBankArrival(double timerSeconds, int intervalSeconds)
        {
            if (double.IsNaN(timerSeconds) || double.IsInfinity(timerSeconds) || timerSeconds < 0.0)
                throw new ArgumentOutOfRangeException("timerSeconds");
            if (intervalSeconds <= 0) throw new ArgumentOutOfRangeException("intervalSeconds");
            if (timerSeconds >= intervalSeconds) return 0.0;
            return intervalSeconds - timerSeconds;
        }

        internal static double SecondsToBankOverflow(int banked, int capacity,
            double timerSeconds, int intervalSeconds)
        {
            if (banked < 0 || capacity < 0 || banked > capacity)
                throw new ArgumentOutOfRangeException("banked");
            var next = SecondsToNextBankArrival(timerSeconds, intervalSeconds);
            var arrivalsBeforeLoss = capacity - banked;
            return next + arrivalsBeforeLoss * (double)intervalSeconds;
        }

        internal static bool AntlersCompletionEligible(bool item337Dropped, bool idleMode,
            bool allActive, double idleProgress)
        {
            return item337Dropped && !idleMode && allActive
                   && idleProgress >= AntlersMinimumFraction && idleProgress <= 1.0;
        }

        internal static bool CanSafelyAdvanceAntlersIdle(double idleProgress,
            double incrementPerTick)
        {
            if (double.IsNaN(idleProgress) || double.IsInfinity(idleProgress)
                || double.IsNaN(incrementPerTick) || double.IsInfinity(incrementPerTick)
                || idleProgress < 0.0 || incrementPerTick <= 0.0) return false;
            return idleProgress < AntlersMinimumFraction
                   && idleProgress + incrementPerTick <= AntlersStagingCeiling
                   && idleProgress + incrementPerTick < 1.0;
        }

        internal static int HandInCredit(int level, int ratio)
        {
            if (level < 0) throw new ArgumentOutOfRangeException("level");
            if (ratio < 2) throw new ArgumentOutOfRangeException("ratio");
            return level > 100 ? 1 : 1 + level / ratio;
        }

        internal static bool ShouldApplyButter(bool readyToHandIn, bool alreadyUsed,
            int butterStock, bool willSkip, double incrementalQpValue)
        {
            return readyToHandIn && !alreadyUsed && butterStock > 0 && !willSkip
                   && incrementalQpValue > 0.0;
        }

        internal static QuestManualForecast ForecastManual(int remaining, double dropChance,
            double secondsPerEligibleKill, ForecastCapacityProof capacity)
        {
            if (remaining < 0) throw new ArgumentOutOfRangeException("remaining");
            if (double.IsNaN(secondsPerEligibleKill) || double.IsInfinity(secondsPerEligibleKill)
                || secondsPerEligibleKill < 0.0)
                throw new ArgumentOutOfRangeException("secondsPerEligibleKill");
            var p = Math.Min(1.0, Math.Max(0.0, dropChance));
            var result = new QuestManualForecast
            {
                SuccessProbability = p,
                Evidence = "source quest chance + action cadence; exact negative-binomial trials"
            };
            if (capacity != null && !capacity.Admitted)
            {
                result.Trials = new CompletionForecast
                {
                    Valid = false,
                    Exact = false,
                    InvalidReason = capacity.Reason,
                    Capacity = capacity.Clone()
                };
                result.MeanSeconds = double.PositiveInfinity;
                result.P90Seconds = double.PositiveInfinity;
                return result;
            }
            result.Trials = MechanicsStochastic.NegativeBinomialForecast(remaining, p);
            if (capacity != null) result.Trials.Capacity = capacity.Clone();
            result.MeanSeconds = ScaleTrials(result.Trials.MeanTrials, secondsPerEligibleKill);
            result.P90Seconds = ScaleTrials(result.Trials.P90Trials, secondsPerEligibleKill);
            return result;
        }

        internal static int SelectMaxxTarget(IEnumerable<QuestItemDevelopment> items)
        {
            if (items == null) return -1;
            var selected = items.Where(x => x != null && x.Eligible && !x.Maxxed)
                .OrderBy(x => x.RemainingContribution).ThenBy(x => x.ItemId).FirstOrDefault();
            return selected == null ? -1 : selected.ItemId;
        }

        internal static double PermanentOpportunityCostPerSecond(
            IEnumerable<PermanentActionDescriptor> actions)
        {
            if (actions == null) return 0.0;
            var total = 0.0;
            foreach (var action in actions)
            {
                if (action == null || action.EtaSeconds <= 0.0
                    || double.IsNaN(action.EtaSeconds) || double.IsInfinity(action.EtaSeconds))
                    continue;
                total += Math.Max(0.0, action.DeltaLogEffect) / action.EtaSeconds;
                if (action.Dependency != PermanentDependencyKind.None)
                    total += Math.Max(0, action.TerminalDependencyDelta) / action.EtaSeconds;
            }
            return total;
        }

        internal static QuestPolicyPlan Evaluate(QuestPolicySnapshot state)
        {
            if (state == null) throw new ArgumentNullException("state");
            if (state.BankIntervalSeconds <= 0) throw new ArgumentOutOfRangeException("BankIntervalSeconds");
            var plan = new QuestPolicyPlan
            {
                QuestId = state.QuestId,
                TargetQuestId = state.MaxxTargetId,
                SecondsToNextBankArrival = SecondsToNextBankArrival(
                    state.BankTimerSeconds, state.BankIntervalSeconds),
                SecondsToBankOverflow = SecondsToBankOverflow(state.Banked,
                    state.BankCapacity, state.BankTimerSeconds, state.BankIntervalSeconds)
            };

            if (!state.Active)
            {
                if (state.AllowMajor && state.Banked > 0)
                {
                    plan.Mode = QuestExecutionMode.CompleteQuest;
                    plan.Action = QuestEventAction.StartMajor;
                    plan.Reason = "A banked major is consumed at start before its dated arrival can overflow.";
                    return plan;
                }
                plan.Mode = state.MaxxTargetId >= 0
                    ? QuestExecutionMode.MaxxAndSkipMinor : QuestExecutionMode.CompleteQuest;
                plan.Action = QuestEventAction.StartMinor;
                plan.Reason = state.MaxxTargetId >= 0
                    ? "Start a free minor and reroll toward the selected unMAXXed eligible item."
                    : "No major is banked; start a routine minor.";
                return plan;
            }

            var antlersNeeded = state.NeedAntlers && state.Item337Dropped
                                && !state.Item338PhysicallyOwned;
            if (antlersNeeded && state.AllActive)
            {
                plan.Mode = QuestExecutionMode.AntlersHybrid;
                if (!state.AntlersCapacityAdmitted)
                {
                    plan.Action = QuestEventAction.Hold;
                    plan.Reason = "Antlers staging held until one exact usable ordinary slot is proven.";
                    return plan;
                }
                if (state.Ready)
                {
                    plan.Action = AntlersCompletionEligible(true, state.IdleMode,
                            state.AllActive, state.IdleProgress)
                        ? QuestEventAction.Complete : QuestEventAction.Hold;
                    plan.Reason = plan.Action == QuestEventAction.Complete
                        ? "Native Antlers truth table and capacity are satisfied."
                        : "Ready quest retained because the native Antlers truth table is not satisfied.";
                    return plan;
                }
                if (state.IdleProgress >= AntlersMinimumFraction && state.IdleProgress <= 1.0)
                {
                    plan.Action = state.IdleMode
                        ? QuestEventAction.DisableIdle : QuestEventAction.RouteManual;
                    plan.Reason = "Fraction is inside the native Antlers window; finish with physical manual-mode drops.";
                    return plan;
                }
                if (CanSafelyAdvanceAntlersIdle(state.IdleProgress, state.IdleIncrementPerTick))
                {
                    plan.Action = state.IdleMode ? QuestEventAction.Hold : QuestEventAction.EnableIdle;
                    plan.Reason = "Advance the fresh all-active quest toward the tick-safe Antlers window.";
                    return plan;
                }
                plan.Action = state.IdleMode ? QuestEventAction.DisableIdle : QuestEventAction.Hold;
                plan.Reason = "The next idle tick is not safe for Antlers; do not cross one.";
                return plan;
            }

            var maxxCampaign = state.Minor && state.MaxxTargetId >= 0;
            if (maxxCampaign)
            {
                plan.Mode = QuestExecutionMode.MaxxAndSkipMinor;
                if (state.UsedButter)
                {
                    plan.Action = QuestEventAction.Hold;
                    plan.Reason = "A Buttered quest is never skipped.";
                }
                else if (state.QuestId != state.MaxxTargetId)
                {
                    plan.Action = QuestEventAction.RerollMinor;
                    plan.Reason = "Free minor reroll: active item is not the selected MAXX target.";
                }
                else if (state.MaxxTargetComplete)
                {
                    plan.Action = QuestEventAction.SkipMinor;
                    plan.Reason = "The development item is MAXXed; skip without completing or consuming it.";
                }
                else if (!state.Manual.Feasible)
                {
                    plan.Action = state.IdleMode ? QuestEventAction.DisableIdle : QuestEventAction.Hold;
                    plan.Reason = "MAXX needs physical manual drops; held by " + state.Manual.Blocker + ".";
                }
                else
                {
                    plan.Action = state.IdleMode
                        ? QuestEventAction.DisableIdle : QuestEventAction.RouteManual;
                    plan.Reason = "Farm physical copies and merge all contributions; offer none to the minor.";
                }
                return plan;
            }

            plan.Mode = QuestExecutionMode.CompleteQuest;
            if (state.Ready)
            {
                plan.Action = QuestEventAction.Complete;
                plan.Reason = "Quest is ready for the verified Butter/turn-in boundary.";
                return plan;
            }
            if (!state.Minor)
            {
                plan.Action = state.Manual.Feasible
                    ? (state.IdleMode ? QuestEventAction.DisableIdle : QuestEventAction.RouteManual)
                    : (state.IdleMode ? QuestEventAction.Hold : QuestEventAction.EnableIdle);
                plan.Reason = state.Manual.Feasible
                    ? "Major manual route preserves the active QP/AP multiplier."
                    : "Active major falls back to deterministic Idle progress during "
                      + state.Manual.Blocker + ".";
                return plan;
            }

            var losesArrival = state.Banked >= state.BankCapacity
                               && state.CompletionMeanSeconds > plan.SecondsToBankOverflow
                               && state.LostMajorValue > state.MinorRewardValue;
            if (losesArrival && !state.UsedButter)
            {
                plan.Action = QuestEventAction.SkipMinor;
                plan.Reason = "Minor completion misses the exact bank-overflow deadline; preserve the arriving major.";
                return plan;
            }
            if (state.PreferManualMinor && state.Manual.Feasible)
            {
                plan.Action = state.IdleMode
                    ? QuestEventAction.DisableIdle : QuestEventAction.RouteManual;
                plan.Reason = "Manual minor is explicitly preferred and feasible.";
            }
            else
            {
                plan.Action = state.IdleMode ? QuestEventAction.Hold : QuestEventAction.EnableIdle;
                plan.Reason = "Routine minor progresses concurrently in Idle mode.";
            }
            return plan;
        }

        private static double ScaleTrials(double trials, double seconds)
        {
            if (double.IsPositiveInfinity(trials)) return double.PositiveInfinity;
            return trials * seconds;
        }
    }

    internal sealed class QuestManager
    {
        private readonly Character _character;
        internal static QuestExecutionMode CurrentMode { get; private set; }
        internal static bool MayOfferQuestItems
        {
            get { return CurrentMode == QuestExecutionMode.CompleteQuest; }
        }
        internal static bool MustMergeQuestItems
        {
            get { return CurrentMode == QuestExecutionMode.MaxxAndSkipMinor; }
        }
        internal static QuestPolicyPlan LastPlan { get; private set; }
        internal static string LastDecision { get; private set; } = "Quest policy is not yet evaluated";

        public QuestManager()
        {
            _character = Main.Character;
        }

        internal void CheckQuestTurnin()
        {
            ExecutionSafety.ReportHold("quest-root-required",
                "Quest turn-in requires the caller-owned nonzero root transaction.");
        }

        internal void ManageQuests()
        {
            ExecutionSafety.ReportHold("quest-root-required",
                "Quest event execution requires the caller-owned nonzero root transaction.");
        }

        internal void CheckQuestTurnin(RootTransaction root)
        {
            if (root == null || root.IsClosed) return;
            var state = CapturePolicySnapshot();
            var plan = Publish(QuestEventController.Evaluate(state));
            if (!state.Active || !state.Ready || plan.Action != QuestEventAction.Complete) return;

            var autopilot = AutopilotOwnsQuests();
            var configuredButter = state.Minor ? Settings.UseButterMinor : Settings.UseButterMajor;
            var incrementalQp = _character.beastQuestController.currentQuestQPValue()
                                * Math.Max(0.0, _character.allArbitrary.butterModifier() - 1.0);
            if ((configuredButter || autopilot && !state.Minor)
                && QuestEventController.ShouldApplyButter(state.Ready, state.UsedButter,
                    _character.arbitrary.beastButterCount, false, incrementalQp))
            {
                var butter = root.ExecuteChild(new QuestNativeIntent(_character,
                    QuestNativeAction.Butter, autopilot, plan.Mode));
                if (!butter.RequiredStepSatisfied) return;
            }

            root.ExecuteChild(new QuestNativeIntent(_character,
                QuestNativeAction.Complete, autopilot, plan.Mode));
        }

        internal void ManageQuests(RootTransaction root)
        {
            if (root == null || root.IsClosed) return;
            var state = CapturePolicySnapshot();
            var plan = Publish(QuestEventController.Evaluate(state));
            var autopilot = AutopilotOwnsQuests();
            switch (plan.Action)
            {
                case QuestEventAction.StartMajor:
                    root.ExecuteChild(new QuestNativeIntent(_character,
                        QuestNativeAction.StartMajor, autopilot, plan.Mode));
                    break;
                case QuestEventAction.StartMinor:
                    root.ExecuteChild(new QuestNativeIntent(_character,
                        QuestNativeAction.StartMinor, autopilot, plan.Mode));
                    break;
                case QuestEventAction.RerollMinor:
                {
                    var skipped = root.ExecuteChild(new QuestNativeIntent(_character,
                        QuestNativeAction.SkipMinor, autopilot, plan.Mode));
                    if (skipped.RequiredStepSatisfied)
                        root.ExecuteChild(new QuestNativeIntent(_character,
                            QuestNativeAction.StartMinor, autopilot, plan.Mode));
                    break;
                }
                case QuestEventAction.SkipMinor:
                    root.ExecuteChild(new QuestNativeIntent(_character,
                        QuestNativeAction.SkipMinor, autopilot, plan.Mode));
                    break;
                case QuestEventAction.EnableIdle:
                    root.ExecuteChild(new QuestNativeIntent(_character,
                        QuestNativeAction.EnableIdle, autopilot, plan.Mode));
                    break;
                case QuestEventAction.DisableIdle:
                    root.ExecuteChild(new QuestNativeIntent(_character,
                        QuestNativeAction.DisableIdle, autopilot, plan.Mode));
                    break;
            }
        }

        internal int IsQuesting()
        {
            var managed = Settings.AutoQuest || AutopilotOwnsQuests();
            if (!managed || !_character.beastQuest.inQuest) return -1;
            var plan = Publish(QuestEventController.Evaluate(CapturePolicySnapshot()));
            if (plan.Action != QuestEventAction.RouteManual
                && plan.Action != QuestEventAction.DisableIdle) return -1;
            var zone = _character.beastQuestController.curQuestZone();
            return CombatManager.IsZoneUnlocked(zone) ? zone : -1;
        }

        private QuestPolicyPlan Publish(QuestPolicyPlan plan)
        {
            LastPlan = plan;
            CurrentMode = plan.Mode;
            LastDecision = plan.Reason;
            return plan;
        }

        private QuestPolicySnapshot CapturePolicySnapshot()
        {
            var quest = _character.beastQuest;
            var controller = _character.beastQuestController;
            var topology = InventoryManager.CaptureOrdinaryTopology(_character);
            var questDropCapacity = topology == null ? null : LootCapacity.ProveOrdinary(topology,
                LootCapacityRequirement.ExactBatch("quest-physical-drop", 1, 0));
            var antlersCapacity = topology == null ? null : LootCapacity.ProveOrdinary(topology,
                LootCapacityRequirement.ExactUniqueDelivery("antlers-of-the-exile-338", 0, 1, 0));
            var target = SelectLiveMaxxTarget();
            var activeMaxxed = IsItemMaxxed(quest.questID);
            if (quest.inQuest && quest.reducedRewards && activeMaxxed
                && CurrentMode == QuestExecutionMode.MaxxAndSkipMinor)
                target = quest.questID;
            var zone = quest.inQuest ? controller.curQuestZone() : -1;
            var stats = zone >= 0 && ZoneStatHelper.UserOverrides != null
                        && ZoneStatHelper.UserOverrides.ContainsKey(zone)
                ? ZoneStatHelper.UserOverrides[zone] : null;
            var feasibility = new QuestManualFeasibility
            {
                Online = true,
                ZoneUnlocked = zone < 0 || CombatManager.IsZoneUnlocked(zone),
                ZoneSurvivable = zone < 0 || stats != null
                    && stats.FightType(_character.totalAdvAttack(), _character.totalAdvDefense()) > 0,
                AdventureAvailable = _character.buttons.adventure.interactable,
                TitanPreempted = ZoneHelpers.HighestAvailableTitan() >= 0,
                CapacityAdmitted = questDropCapacity != null && questDropCapacity.Admitted
                                   && IsDeliveryFilterSafe(quest.questID)
            };
            var chance = quest.inQuest ? Math.Min(1.0, controller.questDropChance()) : 0.0;
            var nativePerDrop = quest.inQuest ? controller.expectedTimePerDrop() : 0.0;
            var secondsPerKill = chance <= 0.0 ? 0.0 : nativePerDrop * chance;
            var remaining = Math.Max(0, quest.targetDrops - quest.curDrops);
            var forecastCapacity = questDropCapacity == null
                ? ForecastCapacityProof.Prove(1, 0, false, true, "No ordinary topology snapshot.")
                : ForecastCapacityProof.Prove(questDropCapacity.RequiredFreeSlots,
                    questDropCapacity.UsableFreeSlotCount, false, true, questDropCapacity.Reason);
            var forecast = QuestEventController.ForecastManual(remaining, chance,
                secondsPerKill, forecastCapacity);
            var interval = controller.timerThreshold();
            var item337 = IsItemDropped(337);
            var owns338 = topology != null && topology.HasOrdinaryItem(338);
            var idleIncrement = nativePerDrop <= 0.0 ? 0.0
                : 1.0 / nativePerDrop / LiveIdleDropFactor() / 50.0;
            return new QuestPolicySnapshot
            {
                Active = quest.inQuest,
                Minor = quest.reducedRewards,
                QuestId = quest.questID,
                Target = quest.targetDrops,
                Current = quest.curDrops,
                IdleMode = quest.idleMode,
                AllActive = quest.allActive,
                IdleProgress = quest.idleProgress,
                IdleIncrementPerTick = idleIncrement,
                UsedButter = quest.usedButter,
                Ready = quest.inQuest && controller.readyToHandIn(),
                Banked = quest.curBankedQuests,
                BankCapacity = controller.maxBankedQuests(),
                BankTimerSeconds = quest.dailyQuestTimer.totalseconds,
                BankIntervalSeconds = interval,
                AllowMajor = Settings.AllowMajorQuests || AutopilotOwnsQuests(),
                MaxxTargetId = target,
                MaxxTargetComplete = activeMaxxed,
                NeedAntlers = item337 && !owns338,
                Item337Dropped = item337,
                Item338PhysicallyOwned = owns338,
                AntlersCapacityAdmitted = antlersCapacity != null && antlersCapacity.Admitted
                                           && IsDeliveryFilterSafe(338),
                PreferManualMinor = Settings.ManualMinors,
                CompletionMeanSeconds = forecast.MeanSeconds,
                MinorRewardValue = quest.inQuest && quest.reducedRewards
                    ? controller.currentQuestQPValue() : 0.0,
                LostMajorValue = quest.inQuest ? Math.Max(50.0, controller.currentQuestQPValue()) : 50.0,
                Manual = feasibility
            };
        }

        private int SelectLiveMaxxTarget()
        {
            var states = new List<QuestItemDevelopment>();
            for (var id = 278; id <= 287; id++)
            {
                var maxxed = IsItemMaxxed(id);
                var copies = new List<CollectionPhysicalCopy>();
                for (var slot = 0; slot < _character.inventory.inventory.Count; slot++)
                {
                    var item = _character.inventory.inventory[slot];
                    if (item == null || item.id != id) continue;
                    var level = Math.Max(0, Math.Min(100, item.level));
                    copies.Add(new CollectionPhysicalCopy(id, level, level,
                        CollectionPhysicalLocation.OrdinaryInventory, item,
                        InventoryManager.IsNativeLoadoutReference(_character, slot)));
                }
                var observation = new CollectionItemObservation(id, maxxed,
                    IsItemDropped(id), 1, copies.ToArray());
                var collection = CollectionItemState.Build(observation,
                    new LootItemSourceMetadata[0]);
                states.Add(new QuestItemDevelopment
                {
                    ItemId = id,
                    Eligible = QuestItemEligible(id),
                    Maxxed = maxxed,
                    RemainingContribution = collection.RemainingContribution
                });
            }
            return QuestEventController.SelectMaxxTarget(states);
        }

        private bool QuestItemEligible(int id)
        {
            var list = _character.inventory.itemList;
            switch (id)
            {
                case 278: return true;
                case 279: return AdventureCollectionPlanner.CoreSetComplete(_character, 9);
                case 280: return AdventureCollectionPlanner.CoreSetComplete(_character, 20);
                case 281: return AdventureCollectionPlanner.CoreSetComplete(_character, 2);
                case 282: return AdventureCollectionPlanner.CoreSetComplete(_character, 12);
                case 283: return AdventureCollectionPlanner.CoreSetComplete(_character, 5);
                case 284: return _character.settings.rebirthDifficulty >= difficulty.evil
                                 && list.edgyComplete;
                case 285: return list.beardverseComplete;
                case 286: return _character.settings.rebirthDifficulty >= difficulty.evil
                                 && list.prettyComplete;
                case 287: return list.megaComplete;
                default: return false;
            }
        }

        private bool IsItemMaxxed(int id)
        {
            var values = _character.inventory.itemList.itemMaxxed;
            return id >= 0 && values != null && id < values.Count && values[id];
        }

        private bool IsItemDropped(int id)
        {
            var values = _character.inventory.itemList.itemDropped;
            return id >= 0 && values != null && id < values.Count && values[id];
        }

        private bool IsDeliveryFilterSafe(int id)
        {
            if (_character.settings.filterMisc) return false;
            var filtered = _character.inventory.itemList.itemFiltered;
            return !_character.arbitrary.lootFilter || filtered == null
                   || id < 0 || id >= filtered.Count || !filtered[id];
        }

        private double LiveIdleDropFactor()
        {
            var perks = _character.adventure.itopod.perkLevel;
            var factor = 8.0;
            if (perks != null && perks.Count > 105 && perks[105] >= 1) factor -= 2.0;
            if (perks != null && perks.Count > 106 && perks[106] >= 1) factor -= 1.0;
            if (perks != null && perks.Count > 91 && perks[91] >= 1) factor -= 1.0;
            if (perks != null && perks.Count > 92 && perks[92] >= 1) factor -= 1.0;
            return Math.Max(3.0, factor);
        }

        private static bool AutopilotOwnsQuests()
        {
            return Main.Autopilot != null && Main.Autopilot.CanExecuteSafe
                   && Main.Autopilot.Config.ManageQuests;
        }

        private enum QuestNativeAction
        {
            StartMajor,
            StartMinor,
            SkipMinor,
            EnableIdle,
            DisableIdle,
            Butter,
            Complete
        }

        private sealed class QuestMutationState
        {
            internal bool InQuest;
            internal int QuestId;
            internal int Target;
            internal int Current;
            internal int Banked;
            internal bool Minor;
            internal bool Idle;
            internal bool AllActive;
            internal float IdleProgress;
            internal bool Buttered;
            internal int ButterStock;
            internal long Qp;
            internal long Ap;
            internal bool UseMajorSetting;
            internal bool HasAntlers;
        }

        /*
        VERIFIED QUEST NATIVE INTENT

        Every branch captures all persistent quest fields affected by native clear/start/complete.
        Apply's Boolean is deliberately ignored.  Skip admits only an unButtered minor; completion
        of an Antlers-mode quest additionally proves ordinary physical item 338 after reward credit.
        */
        private sealed class QuestNativeIntent :
            IMutationIntent<QuestMutationState, bool, QuestMutationState>
        {
            private readonly Character _character;
            private readonly QuestNativeAction _action;
            private readonly bool _autopilot;
            private readonly QuestExecutionMode _mode;

            internal QuestNativeIntent(Character character, QuestNativeAction action,
                bool autopilot, QuestExecutionMode mode)
            {
                _character = character;
                _action = action;
                _autopilot = autopilot;
                _mode = mode;
            }

            public string Id { get { return "quest/" + _action.ToString().ToLowerInvariant(); } }
            public MutationClass Class { get { return MutationClass.Quests; } }
            public MutationRisk Risk
            {
                get
                {
                    return _action == QuestNativeAction.EnableIdle
                           || _action == QuestNativeAction.DisableIdle
                        ? MutationRisk.Reversible : MutationRisk.FiniteResource;
                }
            }
            public MutationOwner Owner { get { return _autopilot ? MutationOwner.Autopilot : MutationOwner.Legacy; } }
            public string BindingId { get { return "BeastQuestController." + _action; } }
            public bool Required { get { return true; } }
            public bool CanCompensate
            {
                get
                {
                    return _action == QuestNativeAction.EnableIdle
                           || _action == QuestNativeAction.DisableIdle;
                }
            }
            public bool CreatesNewEpoch { get { return false; } }
            public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

            public QuestMutationState CaptureBefore(MutationContext context)
            {
                var quest = _character.beastQuest;
                var topology = InventoryManager.CaptureOrdinaryTopology(_character);
                return new QuestMutationState
                {
                    InQuest = quest.inQuest,
                    QuestId = quest.questID,
                    Target = quest.targetDrops,
                    Current = quest.curDrops,
                    Banked = quest.curBankedQuests,
                    Minor = quest.reducedRewards,
                    Idle = quest.idleMode,
                    AllActive = quest.allActive,
                    IdleProgress = quest.idleProgress,
                    Buttered = quest.usedButter,
                    ButterStock = _character.arbitrary.beastButterCount,
                    Qp = quest.quirkPoints,
                    Ap = _character.arbitrary.curArbitraryPoints,
                    UseMajorSetting = _character.settings.useMajorQuests,
                    HasAntlers = topology != null && topology.HasOrdinaryItem(338)
                };
            }

            public PreconditionResult CheckPreconditions(MutationContext context,
                QuestMutationState before)
            {
                switch (_action)
                {
                    case QuestNativeAction.StartMajor:
                        return !before.InQuest && before.Banked > 0
                            ? PreconditionResult.Ready()
                            : PreconditionResult.Hold("A major start needs no active quest and a positive bank.");
                    case QuestNativeAction.StartMinor:
                        return !before.InQuest ? PreconditionResult.Ready()
                            : PreconditionResult.Hold("A minor start needs no active quest.");
                    case QuestNativeAction.SkipMinor:
                        return before.InQuest && before.Minor && !before.Buttered
                            ? PreconditionResult.Ready()
                            : PreconditionResult.Hold("Only an unButtered minor may be skipped.");
                    case QuestNativeAction.EnableIdle:
                        return before.InQuest && !before.Idle ? PreconditionResult.Ready()
                            : PreconditionResult.AlreadySatisfied("Quest Idle mode already matches.");
                    case QuestNativeAction.DisableIdle:
                        return before.InQuest && before.Idle ? PreconditionResult.Ready()
                            : PreconditionResult.AlreadySatisfied("Quest manual mode already matches.");
                    case QuestNativeAction.Butter:
                        return before.InQuest && !before.Buttered && before.ButterStock > 0
                               && _character.beastQuestController.readyToHandIn()
                            ? PreconditionResult.Ready()
                            : PreconditionResult.Hold("Butter is allowed only at a ready turn-in boundary.");
                    case QuestNativeAction.Complete:
                        if (!before.InQuest || !_character.beastQuestController.readyToHandIn())
                            return PreconditionResult.Hold("Quest is not ready to hand in.");
                        if (_mode == QuestExecutionMode.AntlersHybrid)
                        {
                            var topology = InventoryManager.CaptureOrdinaryTopology(_character);
                            var proof = topology == null ? null : LootCapacity.ProveOrdinary(topology,
                                LootCapacityRequirement.ExactUniqueDelivery(
                                    "antlers-of-the-exile-338", 0, 1, 0));
                            if (proof == null || !proof.Admitted
                                || !DeliveryFilterSafe(_character, 338)
                                || !QuestEventController.AntlersCompletionEligible(
                                    IsDropped(_character, 337), before.Idle,
                                    before.AllActive, before.IdleProgress))
                                return PreconditionResult.Hold(
                                    "Antlers truth table or exact ordinary capacity is no longer valid.");
                        }
                        return PreconditionResult.Ready();
                    default: return PreconditionResult.Hold("Unknown quest action.");
                }
            }

            public bool Apply(MutationContext context, RootTransactionToken token,
                QuestMutationState before)
            {
                switch (_action)
                {
                    case QuestNativeAction.StartMajor:
                        _character.settings.useMajorQuests = true;
                        _character.beastQuestController.startQuest();
                        break;
                    case QuestNativeAction.StartMinor:
                        _character.settings.useMajorQuests = false;
                        _character.beastQuestController.startQuest();
                        break;
                    case QuestNativeAction.SkipMinor:
                        _character.beastQuestController.skipQuest();
                        break;
                    case QuestNativeAction.EnableIdle:
                    case QuestNativeAction.DisableIdle:
                        _character.beastQuest.idleMode = _action == QuestNativeAction.EnableIdle;
                        _character.beastQuestController.updateButtons();
                        _character.beastQuestController.updateButtonText();
                        break;
                    case QuestNativeAction.Butter:
                        _character.beastQuestController.tryUseButter();
                        break;
                    case QuestNativeAction.Complete:
                        _character.beastQuestController.completeQuest();
                        break;
                }
                return true;
            }

            public VerificationResult<QuestMutationState> Verify(MutationContext context,
                QuestMutationState before, MutationApplyObservation<bool> apply)
            {
                var after = CaptureBefore(context);
                var valid = false;
                switch (_action)
                {
                    case QuestNativeAction.StartMajor:
                        valid = after.InQuest && !after.Minor && after.Current == 0
                                && after.Target >= 50 && after.Target <= 59
                                && after.Banked == before.Banked - 1;
                        break;
                    case QuestNativeAction.StartMinor:
                        valid = after.InQuest && after.Minor && after.Current == 0
                                && after.Target >= 50 && after.Target <= 59
                                && after.Banked == before.Banked;
                        break;
                    case QuestNativeAction.SkipMinor:
                        valid = !after.InQuest && after.QuestId == 0
                                && after.Banked == before.Banked && !after.Buttered;
                        break;
                    case QuestNativeAction.EnableIdle:
                        valid = after.InQuest && after.QuestId == before.QuestId && after.Idle;
                        break;
                    case QuestNativeAction.DisableIdle:
                        valid = after.InQuest && after.QuestId == before.QuestId && !after.Idle
                                && after.IdleProgress == before.IdleProgress;
                        break;
                    case QuestNativeAction.Butter:
                        valid = after.InQuest && after.QuestId == before.QuestId
                                && after.Buttered && after.ButterStock == before.ButterStock - 1;
                        break;
                    case QuestNativeAction.Complete:
                        valid = !after.InQuest && after.QuestId == 0
                                && (after.Qp > before.Qp || after.Ap > before.Ap)
                                && (_mode != QuestExecutionMode.AntlersHybrid || after.HasAntlers);
                        break;
                }
                return valid ? VerificationResult<QuestMutationState>.Satisfied(after,
                        "Exact quest state transition verified.")
                    : VerificationResult<QuestMutationState>.Failed(
                        "Quest native call lacked its exact identity/resource postcondition.");
            }

            public CompensationResult Compensate(MutationContext context, RecoveryToken token,
                QuestMutationState before, MutationApplyObservation<bool> apply)
            {
                if (!CanCompensate)
                    return CompensationResult.NotSupported("Quest progress/resources cannot be reconstructed.");
                _character.beastQuest.idleMode = before.Idle;
                _character.beastQuestController.updateButtons();
                _character.beastQuestController.updateButtonText();
                var restored = CaptureBefore(context);
                return BeforeStateMatches(before, restored)
                    ? CompensationResult.Restored("Original quest mode restored.")
                    : CompensationResult.Failed("Original quest mode was not restored.");
            }

            public bool BeforeStateMatches(QuestMutationState expected, QuestMutationState observed)
            {
                return expected.InQuest == observed.InQuest && expected.QuestId == observed.QuestId
                       && expected.Target == observed.Target && expected.Current == observed.Current
                       && expected.Banked == observed.Banked && expected.Minor == observed.Minor
                       && expected.Idle == observed.Idle && expected.AllActive == observed.AllActive
                       && expected.IdleProgress == observed.IdleProgress
                       && expected.Buttered == observed.Buttered
                       && expected.ButterStock == observed.ButterStock
                       && expected.Qp == observed.Qp && expected.Ap == observed.Ap;
            }

            public string FingerprintBefore(QuestMutationState state) { return Fingerprint(state); }
            public string FingerprintAfter(QuestMutationState state) { return Fingerprint(state); }

            private static string Fingerprint(QuestMutationState state)
            {
                return state.InQuest + ":" + state.QuestId + ":" + state.Target + ":"
                       + state.Current + ":" + state.Banked + ":" + state.Minor + ":"
                       + state.Idle + ":" + state.AllActive + ":" + state.IdleProgress + ":"
                       + state.Buttered + ":" + state.ButterStock + ":" + state.Qp + ":" + state.Ap;
            }

            private static bool IsDropped(Character character, int id)
            {
                var values = character.inventory.itemList.itemDropped;
                return values != null && id >= 0 && id < values.Count && values[id];
            }


            private static bool DeliveryFilterSafe(Character character, int id)
            {
                if (character.settings.filterMisc) return false;
                var filtered = character.inventory.itemList.itemFiltered;
                return !character.arbitrary.lootFilter || filtered == null
                       || id < 0 || id >= filtered.Count || !filtered[id];
            }
        }
    }
}
