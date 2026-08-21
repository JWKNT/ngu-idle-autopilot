using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using NGUInjector.AllocationProfiles;
using NGUInjector.Managers;
using UnityEngine.UI;

/*
FILE PURPOSE

AutopilotManager is the task-29 integration surface: it reloads and installs pure plans, exposes the
single GameEpoch-bound MutationCoordinator root used by Main, invokes only typed child-intent
managers, emits exact shadow/transaction/native-binding telemetry, and records sparse verified
progression events. Its read-only dashboard envelope also publishes the complete grouped native
Energy/Magic/Resource 3 allocation vector and live Wandoos levels so the player can account for
every resource without weakening the exact transaction proof. Planning is separate from mutation execution. Build-pinned EXP atoms, PP perk
purchases, T1-T12 execution, and ordinary rebirth are live behind explicit authority and fresh
postconditions. PP execution buys only the audited early sequence or terminal item; later PP is
saved until typed downstream-time quotes replace name/effect heuristics. Exact Money Pit and the
audited Normal challenge subset are also live behind their
separate explicit flags; AP/QP purchases, difficulty, T13/T14, MOVE69, END, and the global scheduler
remain fail-closed for this deploy. Legacy direct mutation helpers are not called; staged authority
can expand only through typed postconditions and copied-save/backtest evidence.
ITOPOD telemetry and valuation report the active farm floor, repeatable one-hit fallback,
conservative combat frontier, diagnostic Regular-Attack reach, and empirical failure breaker
separately. Open pushes price and schedule the next record divisible by ten; repeated confirmed
floor deaths pause that push on a lower session-proven clear when one exists. Continuous
ITOPOD combat never leaves merely to pre-cast a recycled buff, because native re-entry resets the
ten-kill floor counter; floor-boundary healing happens in place without erasing partial progress.
The newest fightable incomplete core set yields to an already-partial ITOPOD decade or immediately
following 100-floor super-boundary only when the fresh award ETA plus its uncertainty margin fits
before the selected rebirth. This finite lease is recomputed each second; optional novelty drops
never extend the core-set lease.
After a rebirth, ordinary Adventure briefly preempts ITOPOD only when a source-proved Augment or
Blood purchase can finish inside the run but has no Gold. Before the native Time Machine record
gate this farms only the required liquid Gold; afterward one enemy drop seeds positive passive GPS.
Once the purchase is funded or GPS is positive, ITOPOD immediately regains priority.
Any ordinary-zone route selected while another zone has an active enemy first settles through one
native Safe-Zone frame. The following root enters the selected zone, so an ITOPOD-to-collection
probe cannot be rejected by the game or quarantine Adventure merely because combat was in progress.
Optional collection telemetry also exposes the compatible online kill cadence and source-backed
mean/P90 completion times so the dashboard can explain whether a one-kill probe ended in farming or
an immediate return to ITOPOD.
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
        private MajorUnlockTarget _majorUnlockTarget;
        private GoldBootstrapDecision _goldBootstrapDecision = GoldBootstrapDecision.Hold(
            "Gold bootstrap has not been evaluated");
        private AdventureRoutePlan _lastAdventureRoutePlan;
        private double _lastItopodCompletionHorizonSeconds = -1.0;
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
        private readonly MutationCoordinator _mutationCoordinator;
        private readonly PermanentPurchaseManager _permanentPurchaseManager;
        private long _permanentPurchasePass;

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
            ExecutionSafety.ReportHold("titan7-typed-input-required",
                "Titan 7 native key delivery is disabled until it is a typed, epoch-cancelled coordinator child intent.");
            return false;
        }

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
            _mutationCoordinator = MutationCoordinator.Shared;
            _permanentPurchaseManager = new PermanentPurchaseManager(true);
            _profilesDir = profilesDir;
            _configPath = Path.Combine(runtimeDir, "autopilot.json");
            _decisionPath = Path.Combine(runtimeDir, "decision.json");
            _profilePath = Path.Combine(profilesDir, "autopilot.generated.json");
            ReloadConfig(true);
        }

        internal RootBeginResult BeginAutomationRoot(string name)
        {
            if (Config == null)
                return new RootBeginResult(RootBeginStatus.Held, null,
                    "autopilot configuration is unavailable");
            if (!GameEpochController.Shared.MutationOpen)
                return new RootBeginResult(RootBeginStatus.Held, null,
                    "game epoch is not active: " + GameEpochController.Shared.HoldReason);
            var result = _mutationCoordinator.BeginRoot(name, Config);
            if (Plan != null)
            {
                Plan.RootTransactionState = result.Status == RootBeginStatus.Begun
                    ? "open" : "held";
                Plan.RootTransactionId = result.Transaction == null
                    ? 0L : result.Transaction.Id;
                Plan.RootEpochFingerprint = result.Transaction == null
                    ? Main.CurrentGameEpochFingerprint
                    : result.Transaction.Token.EpochFingerprint;
                if (result.Status == RootBeginStatus.Held)
                    Plan.GlobalScheduleBlocker = new PlannerBlocker(
                        PlannerBlockerKind.OutsideModel,
                        "mutation root held: " + result.Reason);
            }
            return result;
        }

        internal void ExecutePlannedMutations(RootTransaction root)
        {
            if (root == null || root.IsClosed || !CanExecuteSafe || Plan == null) return;
            RefreshRebirthBoundaryHold();
            if (Config.ManageCards)
                CardCookingManager.ManageCards(Main.Character, Config,
                    CanExecuteIrreversible, root);
            if (!root.IsClosed && Config.ManageCooking)
                CardCookingManager.ManageCooking(Main.Character,
                    CanExecuteIrreversible, root);
            if (!root.IsClosed && CanExecuteIrreversible && Config.AllowExpSpending)
                ExecuteOneExpPurchase(root);
            if (!root.IsClosed && CanExecuteIrreversible && Config.AllowPerkSpending)
                ExecuteOnePerkPurchase(root);
            // AP Hearts/late AP atoms, Move69, difficulty/challenge executors, terminal
            // transactions, and T13/T14 retain their separate fail-closed gates.
        }

        private void RefreshRebirthBoundaryHold()
        {
            if (Plan == null || Config == null || Main.Character == null
                || Main.Character.rebirthTime == null)
                return;
            var due = !Plan.RebirthExecutionHold && Plan.RebirthSeconds >= 0
                      && Main.Character.rebirthTime.totalseconds >= Plan.RebirthSeconds;
            if (!due)
            {
                Plan.SetRebirthBoundaryHold(false, string.Empty);
                return;
            }
            // This runs only after the epoch-bound root is open, so EvaluateLive observes the
            // same synchronization/Titan/fruit/Blood/preview/policy facts as the final reset child.
            // A blocked due checkpoint must keep reset-local allocations on a rolling horizon;
            // it must not be mislabeled as the optimizer preferring continuation.
            var gate = OrdinaryRebirthTransaction.EvaluateLive(Main.Character, Plan, Config, true);
            Plan.SetRebirthBoundaryHold(!gate.Ready, gate.Reason);
        }

        /*
        LIVE EXP PURCHASE BRIDGE

        GetExpStatus and this selector share the same early progression policy.  Only a descriptor
        whose complete integral state vector is supported by LivePermanentPurchaseRuntime may reach
        the irreversible manager; unsupported later-game atoms remain telemetry-only.  One exact
        purchase is attempted per root and every normal return must settle as the exact EXP debit
        plus declared permanent delta before a PURCHASE event is emitted.
        */
        private void ExecuteOneExpPurchase(RootTransaction root)
        {
            PurchaseDescriptor descriptor;
            object controller;
            PurchaseCostState costState;
            string policyReason;
            if (!TrySelectLiveExpPurchase(Main.Character, out descriptor, out controller,
                    out costState, out policyReason))
                return;
            var runtime = new LivePermanentPurchaseRuntime(Main.Character, controller, costState);
            var before = runtime.Capture(descriptor);
            var expected = LivePermanentPurchaseRuntime.ExpectedAfter(descriptor, before);
            if (before == null || expected == null) return;
            var planned = _permanentPurchaseManager.Plan(before, descriptor, expected,
                Math.Max(0L, Config.ExpReserve), null, 1.0);
            if (planned.Status != PurchasePlanStatus.Planned || planned.Plan == null) return;
            var result = _permanentPurchaseManager.ExecuteOne(
                new PurchasePlanningPass(++_permanentPurchasePass), root, planned.Plan, runtime);
            if (result.Mutation == null) return;
            if (result.Mutation.Kind == MutationResultKind.Committed)
            {
                Main.LogAction("PURCHASE", "Bought " + descriptor.DisplayName + " for "
                    + planned.Plan.ExactCost + " EXP [confirmed by exact debit and permanent-stat delta]; "
                    + policyReason);
            }
            else if (result.Mutation.Kind != MutationResultKind.Held)
            {
                Main.LogAction("REJECTED", descriptor.DisplayName + " purchase "
                    + result.Mutation.Kind + ": " + result.Mutation.Reason);
            }
        }

        private void ExecuteOnePerkPurchase(RootTransaction root)
        {
            var c = Main.Character;
            var spendable = c == null || c.adventure == null || c.adventure.itopod == null
                ? 0L : c.adventure.itopod.perkPoints - Math.Max(0L, Config.PPReserve);
            var id = SelectAffordablePerkTarget(c, spendable);
            if (id < 0) return;
            var result = root.ExecuteChild(new PerkPurchaseIntent(c, id,
                Math.Max(0L, Config.PPReserve)));
            if (result.Kind != MutationResultKind.Committed
                && result.Kind != MutationResultKind.NoOpVerified
                && result.Kind != MutationResultKind.Held)
                Main.LogAction("REJECTED", "Perk purchase intent " + result.Kind
                    + ": " + result.Reason);
        }

        private bool TrySelectLiveExpPurchase(Character c, out PurchaseDescriptor descriptor,
            out object controller, out PurchaseCostState costState, out string reason)
        {
            descriptor = null;
            controller = null;
            costState = null;
            reason = string.Empty;
            if (c == null || c.energyPurchases == null || c.realExp <= Config.ExpReserve)
                return false;

            // Fixed progression/QoL gates outrank marginal P/C/B growth when the existing policy
            // has admitted and funded them.
            var gate = GetGateExpTarget(c);
            if (gate != null)
                return gate.Cost <= c.realExp - Config.ExpReserve
                       && TryMapFixedExpTarget(gate, out descriptor, out controller,
                           out costState, out reason);

            if (c.energySpeed < 49.91f)
            {
                if (!TryGetEnergySpeedPurchase(c, out descriptor, out costState, out reason))
                    return false;
                controller = c.energyPurchases;
                long exactCost;
                try { exactCost = descriptor.Cost.Evaluate(costState); }
                catch { return false; }
                return exactCost <= c.realExp - Config.ExpReserve;
            }

            var permanent = GetStrategicPermanentExpTarget(c);
            if (permanent != null && ShouldReserveForPermanentExpTarget(c, permanent))
                return permanent.Cost <= c.realExp - Config.ExpReserve
                       && TryMapFixedExpTarget(permanent, out descriptor, out controller,
                           out costState, out reason);

            int magicSteps;
            double magicRate;
            string magicReason;
            if (MagicSpeedOutranksMarginalGrowth(c, out magicSteps, out magicRate,
                    out magicReason))
            {
                if (!TryReadPositiveIntField(c.magicPurchases, "magicSpeed10Cost",
                        out var magicAtomCost)
                    || magicSteps <= 0 || magicAtomCost > long.MaxValue / magicSteps
                    || magicAtomCost * magicSteps > c.realExp - Config.ExpReserve
                    || !PurchaseDescriptorCatalog.TryGet("exp.magic.speed10", out descriptor))
                    return false;
                controller = c.magicPurchases;
                costState = PurchaseCostState.Live(magicAtomCost);
                reason = "fully funded " + magicSteps + "-atom discrete Magic-rate breakpoint; "
                         + "buying one exact +0.1 atom this root; " + magicReason;
                return true;
            }

            var qol = GetQolExpTarget(c);
            if (qol != null && ShouldReserveForPermanentExpTarget(c, qol))
                return qol.Cost <= c.realExp - Config.ExpReserve
                       && TryMapFixedExpTarget(qol, out descriptor, out controller,
                           out costState, out reason);

            var marginal = BestMarginalExpCandidate(c);
            if (marginal == null || marginal.Cost > c.realExp - Config.ExpReserve)
                return false;
            var key = MarginalExpDescriptorKey(marginal.Controller.GetType().Name,
                marginal.Method);
            if (string.IsNullOrEmpty(key) || !PurchaseDescriptorCatalog.TryGet(key, out descriptor))
            {
                reason = "the selected " + marginal.Label
                         + " has no exact live descriptor mapping";
                return false;
            }
            controller = marginal.Controller;
            if (descriptor.Cost.Kind == PurchaseCostKind.Fixed)
                costState = PurchaseCostState.Fixed();
            else
            {
                var amount = MarginalExpAmount(marginal.Power, marginal.Cap, marginal.Bars);
                if (amount <= 0L)
                {
                    reason = "the selected " + marginal.Label
                             + " does not have one exact custom-input amount";
                    return false;
                }
                costState = PurchaseCostState.WithAmount(amount);
            }
            long sealedCost;
            try { sealedCost = descriptor.Cost.Evaluate(costState); }
            catch { return false; }
            if (sealedCost != marginal.Cost)
            {
                reason = "the selected " + marginal.Label + " cost changed from "
                         + marginal.Cost + " to " + sealedCost + " EXP before execution";
                return false;
            }
            reason = marginal.Reason;
            return true;
        }

        internal static string MarginalExpDescriptorKey(string controllerTypeName,
            string method)
        {
            if (string.Equals(controllerTypeName, "EnergyPurchases", StringComparison.Ordinal))
            {
                if (method == "buyEnergyPower01") return "exp.energy.power01";
                if (method == "buyEnergyBar1") return "exp.energy.bar1";
                if (method == "buyCustomPower") return "exp.energy.custom-power";
                if (method == "buyCustomCap") return "exp.energy.custom-cap";
                if (method == "buyCustomBar") return "exp.energy.custom-bar";
            }
            if (string.Equals(controllerTypeName, "MagicPurchases", StringComparison.Ordinal))
            {
                if (method == "buyCustomPower") return "exp.magic.custom-power";
                if (method == "buyCustomCap") return "exp.magic.custom-cap";
                if (method == "buyCustomBar") return "exp.magic.custom-bar";
            }
            if (string.Equals(controllerTypeName, "Resource3Purchases", StringComparison.Ordinal))
            {
                if (method == "buyCustomPower") return "exp.resource3.custom-power";
                if (method == "buyCustomCap") return "exp.resource3.custom-cap";
                if (method == "buyCustomBar") return "exp.resource3.custom-bar";
            }
            return string.Empty;
        }

        internal static long MarginalExpAmount(int power, int cap, int bars)
        {
            var positive = (power > 0 ? 1 : 0) + (cap > 0 ? 1 : 0) + (bars > 0 ? 1 : 0);
            if (positive != 1) return 0L;
            return power > 0 ? power : cap > 0 ? cap : bars;
        }

        private bool TryGetEnergySpeedPurchase(Character c, out PurchaseDescriptor descriptor,
            out PurchaseCostState costState, out string reason)
        {
            descriptor = null;
            costState = null;
            reason = string.Empty;
            if (c == null || c.energyPurchases == null) return false;
            long special1;
            long special2;
            long special3;
            if (!TryReadPositiveIntField(c.energyPurchases, "energySpecial1Cost", out special1)
                || !TryReadPositiveIntField(c.energyPurchases, "energySpecial2Cost", out special2)
                || !TryReadPositiveIntField(c.energyPurchases, "energySpecial3Cost", out special3))
                return false;
            var spendable = Math.Max(0L, c.realExp - Config.ExpReserve);
            var choice = ExpPurchasePolicy.ChoosePre50EnergySpeed(c.energySpeed,
                c.settings.special1Bought, c.settings.special2Bought,
                c.settings.special3Bought, special1, special2, special3,
                c.energyPurchases.energySpeed10Cost(),
                c.energyPurchases.energySpeed100Cost(), spendable);
            if (choice == null
                || !PurchaseDescriptorCatalog.TryGet(choice.DescriptorKey, out descriptor))
                return false;
            costState = descriptor.Cost.Kind == PurchaseCostKind.LiveSerialized
                ? PurchaseCostState.Live(choice.ExactCost)
                : PurchaseCostState.WithScalar(c.energySpeed);
            long sealedCost;
            try { sealedCost = descriptor.Cost.Evaluate(costState); }
            catch { return false; }
            if (sealedCost != choice.ExactCost) return false;
            reason = "pre-50 productive speed ROI: +"
                     + (choice.DeltaHundredths / 100.0).ToString("0.0")
                     + " base speed, productive gain " + choice.ProductiveGain.ToString("0.0")
                     + " for " + choice.ExactCost + " exact EXP";
            return true;
        }

        private static bool TryReadPositiveIntField(object controller, string fieldName,
            out long value)
        {
            value = 0L;
            if (controller == null || string.IsNullOrEmpty(fieldName)) return false;
            try
            {
                var field = controller.GetType().GetField(fieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null) return false;
                value = Convert.ToInt64(field.GetValue(controller));
                return value > 0L;
            }
            catch { return false; }
        }

        private static bool TryMapFixedExpTarget(PermanentExpTarget target,
            out PurchaseDescriptor descriptor, out object controller,
            out PurchaseCostState costState, out string reason)
        {
            descriptor = null;
            controller = target == null ? null : target.Controller;
            costState = null;
            reason = target == null ? string.Empty : target.Reason;
            if (target == null || controller == null) return false;
            var exactController = controller;
            descriptor = PurchaseDescriptorCatalog.AllExp().FirstOrDefault(x =>
                string.Equals(x.DeclaringTypeName, exactController.GetType().FullName,
                    StringComparison.Ordinal)
                && string.Equals(x.NativeMethodName, target.Method,
                    StringComparison.Ordinal));
            if (descriptor == null) return false;
            costState = descriptor.Cost.Kind == PurchaseCostKind.ExpInventorySpace
                ? PurchaseCostState.WithCounter(Main.Character.inventoryController.curSpaces())
                : PurchaseCostState.Fixed();
            try { return descriptor.Cost.Evaluate(costState) == target.Cost; }
            catch { return false; }
        }

        internal OrdinaryRebirthExecutionOutcome ExecuteOrdinaryRebirth(
            RootTransaction root)
        {
            if (root == null || root.IsClosed || !CanExecuteIrreversible || Plan == null
                || Config == null || !Config.AllowRebirths)
                return new OrdinaryRebirthExecutionOutcome
                {
                    Reason = "ordinary rebirth authority/root/plan is unavailable"
                };
            return OrdinaryRebirthTransaction.Execute(root, Main.Character, Plan, Config);
        }

        internal void RecordAutomationRoot(RootTransaction root, string state)
        {
            if (Plan == null) return;
            Plan.RootTransactionState = state ?? string.Empty;
            if (root == null) return;
            Plan.RootTransactionId = root.Id;
            Plan.RootEpochFingerprint = root.Token.EpochFingerprint;
            var results = root.Results.ToArray();
            Plan.RootCommittedSteps = results.Count(x =>
                x.Kind == MutationResultKind.Committed
                || x.Kind == MutationResultKind.NoOpVerified);
            Plan.RootHeldSteps = results.Count(x => x.Kind == MutationResultKind.Held);
            Plan.RootPendingSteps = results.Count(x => x.Kind == MutationResultKind.Pending);
            Plan.RootRejectedSteps = results.Count(x =>
                x.Kind == MutationResultKind.RejectedUnchanged
                || x.Kind == MutationResultKind.Compensated);
            Plan.RootQuarantinedSteps = results.Count(x =>
                x.Kind == MutationResultKind.Quarantined
                || x.Kind == MutationResultKind.Indeterminate
                || x.Kind == MutationResultKind.CommittedWithException);
            Plan.RootResultSummary = string.Join(" | ", results.Select(x =>
                x.IntentId + "=" + x.Kind + (string.IsNullOrEmpty(x.Reason)
                    ? string.Empty : ": " + x.Reason)).ToArray());
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
            if (Config.ManageDiggers)
                Plan.Diggers = DiggerManager.OptimizeForPlan(Plan);
            AutopilotPlanner.FinalizeResetLocalChoices(Main.Character, Plan);
            var signature = Plan.Signature(Main.Character);
            ObserveBossTransitions(Main.Character);
            ObserveKeyEvents(Main.Character);

            if (signature != _lastPlanSignature)
            {
                Main.Log("Autopilot plan: " + Plan.Stage + " — " + Plan.Objective);
                _lastPlanSignature = signature;
                if (CanExecuteSafe && Config.ManageAllocations)
                    LoadGeneratedProfile();
            }

        }

        /*
        TROLL CONFIRMATION SERVICE

        Native Troll challenges can install a chain of up to fifty-five modal ConfirmationBox
        callbacks. Leaving the chain open blocks useful automation, while choosing Yes on the
        native switcheroo step restarts it. Drive the installed Unity button listeners (not the
        hidden effect methods), verify every counter transition, and stop the entire planner tick
        if the popup fails to advance. Small/big troll messages have no chain counter and are closed
        only at their exact native cadence boundary.
        */
        private static bool ServiceTrollChallengeDialogs()
        {
            var c = Main.Character;
            var controller = c.allChallenges.trollChallenge;
            var box = controller.box;
            if (box == null || box.messageBox == null) return true;

            var timerBefore = c.challenges.trollCounter;
            var chainBefore = controller.boxCounter;
            var clicks = 0;
            while (controller.boxCounter > 0 && clicks < 55)
            {
                var before = controller.boxCounter;
                var button = before == controller.switcherooBox ? box.noButton : box.yesButton;
                if (button == null || button.onClick == null)
                {
                    Main.LogAction("REJECTED", "Troll dialog HOLD: native button unavailable at box "
                                                     + before + "/" + controller.switcherooBox);
                    return false;
                }
                button.onClick.Invoke();
                clicks++;
                if (controller.boxCounter != 0 && controller.boxCounter == before)
                {
                    Main.LogAction("REJECTED", "Troll dialog HOLD: native chain did not advance at box "
                                                     + before + "/" + controller.switcherooBox);
                    return false;
                }
            }
            if (controller.boxCounter > 0)
            {
                Main.LogAction("REJECTED", "Troll dialog HOLD: native chain exceeded 55 verified clicks");
                return false;
            }

            var visible = box.messageBox.transform.localPosition.x > -1900f
                          || box.messageBox.transform.localPosition.y > -1900f;
            var factor = Math.Max(1, controller.trollFactor());
            var cadenceMessage = c.challenges.trollCounter > 0
                                 && c.challenges.trollCounter % factor == 0;
            if (visible && (clicks > 0 || cadenceMessage))
            {
                if (box.yesButton == null || box.yesButton.onClick == null)
                {
                    Main.LogAction("REJECTED", "Troll dialog HOLD: final native close button unavailable");
                    return false;
                }
                box.yesButton.onClick.Invoke();
                clicks++;
                visible = box.messageBox.transform.localPosition.x > -1900f
                          || box.messageBox.transform.localPosition.y > -1900f;
                if (visible)
                {
                    Main.LogAction("REJECTED", "Troll dialog HOLD: native message remained visible after close");
                    return false;
                }
            }

            if (clicks > 0)
                Main.LogAction("CHALLENGE", "Serviced Troll native dialogs [confirmed] timer "
                                               + timerBefore + " -> " + c.challenges.trollCounter
                                               + ", boxes " + chainBefore + " -> " + controller.boxCounter
                                               + ", switcheroo " + controller.switcherooBox
                                               + ", clicks " + clicks);
            return true;
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
                    abilityUnlocked[i] = AdventureUnlockEarned(i, c.training.attackTraining[i]);
                    abilityUnlocked[i + 6] = AdventureUnlockEarned(i,
                        c.training.defenseTraining[i]);
                }

                /*
                TRAINING ROWS ARE NOT COMBAT-ABILITY UNLOCKS

                Each Basic Training slot is named for the move currently being trained, while the
                native nextAttackName/nextDefenseName widget names the following move earned at the
                slot's threshold. For example, Strong Attack training is index 2, and reaching 15K
                there unlocks Parry. Emit unlocks from the separate next-move mapping; milestones
                always use the current-slot mapping.
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
                        var label = attack ? GameNames.AttackUnlock(row)
                            : GameNames.DefenseUnlock(row);
                        Main.LogAction("PROGRESSION", label + " unlocked");
                    }
                }
                if (_lastObservedTrainingMilestones != null)
                {
                    for (var i = 0; i < current.Length; i++)
                    {
                        if (current[i] <= _lastObservedTrainingMilestones[i] || current[i] <= 0) continue;
                        if (newlyUnlocked[i]) continue;
                        var attack = i < 6;
                        var row = i < 6 ? i : i - 6;
                        var label = attack ? GameNames.AttackTraining(c, row)
                            : GameNames.DefenseTraining(c, row);
                        Main.LogAction("MILESTONE", label + " Training " + current[i].ToString("N0"));
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
                    Main.LogAction("MILESTONE", GameNames.Augment(c, pair, upgrade) + " "
                                                + augCurrent[i].ToString("N0"));
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

        private static long AdventureAbilityUnlockLevel(int row)
        {
            // Slots 0-4 each unlock the following Adventure move at a 5K step. Slot 5 is the
            // terminal Ultimate training and has no subsequent move to unlock.
            if (row < 0 || row >= 5) return -1L;
            return 5000L * (row + 1L);
        }

        private static bool AdventureUnlockEarned(int row, long level)
        {
            var requirement = AdventureAbilityUnlockLevel(row);
            return requirement > 0 && level >= requirement;
        }

        private static string SafeItemName(Character c, int id)
        {
            return GameNames.Item(c, id);
        }

        internal void ManageBloodSpell()
        {
            var c = Main.Character;
            if (c.highestBoss < 37 || c.bloodMagic == null || c.bloodSpells == null
                || c.bloodMagic.bloodPoints <= 0)
                return;
            var bloodBefore = c.bloodMagic.bloodPoints;
            var label = string.Empty;
            var effect = string.Empty;
            var guffLevelsBefore = TotalMacGuffinLevels(c);
            var adventureAttackBefore = c.adventure.attack;
            var adventureDefenseBefore = c.adventure.defense;
            var rebirthPowerBefore = c.bloodMagic.rebirthPower;
            var nextAttackBefore = c.nextAttackMulti;
            var nextDefenseBefore = c.nextDefenseMulti;
            var goldSpellBefore = c.bloodMagic.goldSpellBlood;
            var lootSpellBefore = c.bloodMagic.lootSpellBlood;
            var endItemBefore = EndgameDependencyModel.IsOwned(c, 494);
            var hasMacGuffin = guffLevelsBefore >= 0.0 && HasMacGuffin(c);
            var remaining = Plan == null || Plan.RebirthExecutionHold ? int.MaxValue
                : Plan.RebirthSeconds - (int)c.rebirthTime.totalseconds;

            // Item 494 is a terminal dependency. Native castEndSpell spends the entire Blood
            // pool, so reserve it ahead of every repeatable spell and require a physical loot
            // slot before crossing the irreversible boundary.
            if (c.settings.rebirthDifficulty == difficulty.sadistic && !endItemBefore)
            {
                if (c.inventory == null || c.inventory.inventory == null
                    || !c.inventory.inventory.Any(x => x == null || x.id <= 0))
                {
                    ExecutionSafety.ReportHold("end-blood-inventory",
                        "END Blood spell held until an ordinary inventory slot is empty");
                    return;
                }
                var cost = c.bloodSpells.endSpellBlood();
                if (bloodBefore < cost)
                {
                    ExecutionSafety.ReportHold("end-blood-reserve",
                        "Reserving Blood for terminal item 494: " + bloodBefore.ToString("0.###e+0")
                        + " / " + cost.ToString("0.###e+0"), 60);
                    return;
                }
                c.bloodSpells.castEndSpell();
                label = "END Blood spell — terminal item 494";
                effect = "end";
            }
            // Both MacGuffin spells create permanent equipped-item levels and dominate
            // run-local bonuses whenever their native cooldown and threshold are ready.
            else if (c.settings.rebirthDifficulty >= difficulty.evil
                && c.adventure.itopod.perkLevel.Count > 73 && c.adventure.itopod.perkLevel[73] >= 1
                && c.bloodMagic.macguffin2Time.totalseconds >= c.bloodMagicController.spells.macguffin2Cooldown
                && bloodBefore >= c.bloodSpells.minMacguffin2Blood()
                && hasMacGuffin
                && PermanentSpellWindowOpen(remaining, c.bloodMagicController.spells.macguffin2Cooldown))
            {
                c.bloodSpells.castMacguffin2Spell();
                label = "Blood MacGuffin β — all equipped MacGuffins";
                effect = "guff";
            }
            else if (c.adventure.itopod.perkLevel.Count > 72 && c.adventure.itopod.perkLevel[72] >= 1
                     && c.bloodMagic.macguffin1Time.totalseconds >= c.bloodMagicController.spells.macguffin1Cooldown
                     && bloodBefore >= c.bloodSpells.minMacguffin1Blood()
                     && CanCastAlpha(c)
                     && PermanentSpellWindowOpen(remaining, c.bloodMagicController.spells.macguffin1Cooldown))
            {
                c.bloodSpells.castMacguffin1Spell();
                label = "Blood MacGuffin α";
                effect = "guff";
            }
            else
            {
                if (c.bloodMagic.adventureSpellTime.totalseconds >= c.bloodSpells.adventureSpellCooldown
                         && bloodBefore >= c.bloodSpells.minAdventureBlood()
                         && PermanentSpellWindowOpen(remaining, c.bloodSpells.adventureSpellCooldown))
                {
                    c.bloodSpells.castAdventurePowerupSpell();
                    label = "Iron Pill — permanent Adventure stats";
                    effect = "adventure";
                }
                else if (remaining <= 5)
                {
                    c.bloodSpells.castRebirthSpell(bloodBefore);
                    label = "Blood NUMBER Boost — reserved for the selected rebirth checkpoint";
                    effect = "number";
                }
                else if (!PermanentSpellWillBecomeReady(c, remaining)
                         && _collectionTarget != null && _collectionTarget.Target != null
                         && bloodBefore >= c.bloodSpells.minLootBlood())
                {
                    c.bloodSpells.castLootSpell(bloodBefore);
                    label = "Blood Spaghetti — active Item List collection throughput";
                    effect = "loot";
                }
                else if (!PermanentSpellWillBecomeReady(c, remaining)
                         && c.settings.pitUnlocked && bloodBefore >= c.bloodSpells.minGoldBlood())
                {
                    c.bloodSpells.castGoldSpell(bloodBefore);
                    label = "Counterfeit Gold";
                    effect = "gold";
                }
            }

            if (string.IsNullOrEmpty(label)) return;
            var paymentConfirmed = c.bloodMagic.bloodPoints < bloodBefore;
            var effectConfirmed = effect == "guff" ? TotalMacGuffinLevels(c) > guffLevelsBefore
                : effect == "adventure" ? c.adventure.attack > adventureAttackBefore
                                          && c.adventure.defense > adventureDefenseBefore
                : effect == "number" ? c.bloodMagic.rebirthPower > rebirthPowerBefore
                : effect == "gold" ? c.bloodMagic.goldSpellBlood > goldSpellBefore
                : effect == "end" ? EndgameDependencyModel.IsOwned(c, 494)
                : effect == "loot" && c.bloodMagic.lootSpellBlood > lootSpellBefore;
            var confirmed = paymentConfirmed && effectConfirmed;
            Main.LogAction(confirmed ? "BLOOD" : "REJECTED", confirmed
                ? "Cast " + label + " using " + (bloodBefore - c.bloodMagic.bloodPoints)
                  + " Blood" + (effect == "number"
                      ? "; rebirth power " + rebirthPowerBefore.ToString("0.###e+0") + " -> "
                        + c.bloodMagic.rebirthPower.ToString("0.###e+0")
                        + "; Number preview A " + nextAttackBefore.ToString("0.###e+0") + " -> "
                        + c.nextAttackMulti.ToString("0.###e+0") + ", D "
                        + nextDefenseBefore.ToString("0.###e+0") + " -> "
                        + c.nextDefenseMulti.ToString("0.###e+0")
                      : string.Empty)
                  + " [confirmed by payment and spell-specific state delta]"
                : label + " cast lacked a verified payment plus spell-specific effect delta");
        }

        private static bool HasMacGuffin(Character c)
        {
            return c.inventory.macguffins != null
                   && c.inventory.macguffins.Any(x => x != null && x.id > 0);
        }

        private static bool CanCastAlpha(Character c)
        {
            if (!HasMacGuffin(c)) return false;
            return c.wishes.wishes.Count <= 24 || c.wishes.wishes[24].level <= 0
                   || c.inventory.macguffins.Count > 0
                   && c.inventory.macguffins[0] != null && c.inventory.macguffins[0].id > 0;
        }

        private static double TotalMacGuffinLevels(Character c)
        {
            var total = 0.0;
            if (c.inventory.macguffins == null) return total;
            foreach (var item in c.inventory.macguffins)
                if (item != null && item.id > 0) total += item.level;
            return total;
        }

        private static bool PermanentSpellWillBecomeReady(Character c, int remainingSeconds)
        {
            if (remainingSeconds <= 5) return false;
            var horizon = remainingSeconds == int.MaxValue ? double.PositiveInfinity : remainingSeconds;
            if (c.settings.rebirthDifficulty >= difficulty.evil && HasMacGuffin(c)
                && c.adventure.itopod.perkLevel.Count > 73 && c.adventure.itopod.perkLevel[73] >= 1
                && c.bloodMagicController.spells.macguffin2Cooldown
                   - c.bloodMagic.macguffin2Time.totalseconds <= horizon)
                return true;
            if (CanCastAlpha(c) && c.adventure.itopod.perkLevel.Count > 72
                && c.adventure.itopod.perkLevel[72] >= 1
                && c.bloodMagicController.spells.macguffin1Cooldown
                   - c.bloodMagic.macguffin1Time.totalseconds <= horizon)
                return true;
            return c.bloodSpells.adventureSpellCooldown
                   - c.bloodMagic.adventureSpellTime.totalseconds <= horizon;
        }

        private static bool PermanentSpellWindowOpen(int remainingSeconds, int cooldownSeconds)
        {
            // With only one cast left in the run, saving Blood until the mutation boundary weakly
            // dominates spending at the first minimum because permanent spell gains are sublinear
            // and integer-stepped. Cast immediately only when the cooldown can return before the
            // selected checkpoint; at <=5 seconds, consume the maximized end-of-run pool.
            return remainingSeconds == int.MaxValue || remainingSeconds <= 5
                   || remainingSeconds > cooldownSeconds + 5;
        }

        internal bool ControlAdventure(CombatManager combat, QuestManager quests)
        {
            if (!CanExecuteSafe || !Config.ManageAdventure)
                return false;
            _lastAdventureRoutePlan = null;
            _lastItopodCompletionHorizonSeconds = -1.0;
            // A major-unlock target owns Adventure only while its native unlock
            // condition remains unmet. Clear its cached route before reevaluating
            // so consuming a key cannot leave a stale Sky target for another cycle.
            if (_majorUnlockTarget != null)
            {
                _majorUnlockTarget = null;
                _adventureTarget = null;
                _lastAdventureDecision = DateTime.MinValue;
            }
            var typedTitanExecutionOwnsAdventure = Config.AllowTitanOneThroughTwelveExecution;
            if (!typedTitanExecutionOwnsAdventure && !Main.Character.settings.autoKillTitans)
            {
                Main.Character.settings.autoKillTitans = true;
                if (!_loggedTitanAutoKill)
                {
                    Main.LogAction("ADVENTURE", "Enabled NGU Idle's native Titan auto-kill controller [confirmed by settings state]");
                    _loggedTitanAutoKill = true;
                }
            }
            var questZone = quests.IsQuesting();

            if (!PrepareEndgameTitan12Version(combat))
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

            // When typed Titan execution is enabled its synchronous runtime owns the temporary
            // manageFight/zone commitment and requires native auto-kill to remain persistently off.
            // Ordinary Adventure must neither force the setting back on nor race the same Titan.
            var titanZone = typedTitanExecutionOwnsAdventure
                ? -1 : ZoneHelpers.HighestAvailableTitan();
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

            // A ready Titan window has now been exhausted or rejected. Resume the active
            // Beast Quest rather than letting ordinary collection/ITOPOD routing steal it.
            if (questZone > 0)
                return false;

            _majorUnlockTarget = MajorUnlockPlanner.Evaluate(Main.Character);
            if (_majorUnlockTarget != null)
            {
                _collectionTarget = null;
                _adventureTarget = _majorUnlockTarget.AsZoneTarget();
                if (_loggedAdventureZone != _majorUnlockTarget.Zone
                    || _loggedAdventureFightType != _majorUnlockTarget.FightType)
                {
                    Main.LogAction("PROGRESSION", _majorUnlockTarget.Reason);
                    _loggedAdventureZone = _majorUnlockTarget.Zone;
                    _loggedAdventureFightType = _majorUnlockTarget.FightType;
                }
                combat.ManualZone(_majorUnlockTarget.Zone, _majorUnlockTarget.BossOnly,
                    true, true, true, true);
                CaptureRecovery(combat);
                return true;
            }

            // ITOPOD enemies deliberately provide no Gold. A fresh run can therefore deadlock at
            // zero Gold: Augments/Blood cannot pay their first native bar charge, and the Time
            // Machine has no base-drop record from which to produce GPS. Admit a regular-zone
            // detour only for an exact finishable sink, and release it as soon as liquid funding or
            // one eligible record-setting kill restores the Gold feedback loop.
            var wasGoldBootstrap = _goldBootstrapDecision != null
                                   && _goldBootstrapDecision.ShouldRoute;
            _goldBootstrapDecision = EvaluateGoldBootstrap(Main.Character);
            if (_goldBootstrapDecision.ShouldRoute)
            {
                _collectionTarget = null;
                _adventureTarget = new ZoneTarget
                {
                    Zone = _goldBootstrapDecision.TargetZone,
                    FightType = _goldBootstrapDecision.TargetFightType
                };
                ProgressionLoadoutOptimizer.SetAdventureRouteObjective(
                    _goldBootstrapDecision.TargetZone, false, false, false, true);
                if (_loggedAdventureZone != _goldBootstrapDecision.TargetZone
                    || _loggedAdventureFightType != _goldBootstrapDecision.TargetFightType)
                {
                    Main.LogAction("GOLD", _goldBootstrapDecision.Reason + "; conservative drop "
                        + _goldBootstrapDecision.ConservativeDrop.ToString("0")
                        + ", ETA " + Math.Ceiling(_goldBootstrapDecision.EtaSeconds) + "s");
                    _loggedAdventureZone = _goldBootstrapDecision.TargetZone;
                    _loggedAdventureFightType = _goldBootstrapDecision.TargetFightType;
                }
                if (_goldBootstrapDecision.TargetFightType == 2)
                    combat.ManualZone(_goldBootstrapDecision.TargetZone, false,
                        true, false, true, true);
                else
                    combat.ManualZone(_goldBootstrapDecision.TargetZone, false,
                        true, true, false, true);
                CaptureRecovery(combat);
                return true;
            }
            if (wasGoldBootstrap)
            {
                // Do not let the one-second ordinary-route cache or its loadout lease retain
                // Adventure after the native Gold postcondition has become true.
                _adventureTarget = null;
                _lastAdventureDecision = DateTime.MinValue;
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
            var terminalItopodDropMissing = Main.Character.settings.rebirthDifficulty == difficulty.sadistic
                                            && !EndgameDependencyModel.IsOwned(Main.Character, 491);
            AdventureRoutePlan routePlan = null;
            if (Main.Character.settings.itopodOn)
            {
                var route = ZoneHelpers.ConfigureITOPOD();
                if (!route.Confirmed)
                    return false;
                long nextPerkCost;
                double nextPerkGain;
                NextStrategicPerkGate(Main.Character, out nextPerkCost, out nextPerkGain);
                // Optional Item List debt stays protected, but it owns Adventure only when its
                // set transition, boost route, or completed physical item has a proven progression
                // payoff. This prevents rare unequipped Sky accessories from blocking steady
                // ITOPOD PP/EXP/AP/boost income merely because their catalog level is below 100.
                var collectionDebt = _collectionTarget != null
                                     && _collectionTarget.StrategicDebt;
                var currentProgressionFront = ZoneStatHelper.GetBestZone();
                var progressionPush = _collectionTarget == null
                                      || _collectionTarget.Target == null
                                      || !_collectionTarget.IsBackfill
                                      && currentProgressionFront != null
                                      && _collectionTarget.Target.Zone == currentProgressionFront.Zone
                                      && _collectionTarget.Target.FightType != 2;
                var attackCadence = route.Climbing
                                    && Main.Settings.ITOPODCombatMode != 1
                                    && Main.Character.training.attackTraining[0] >= 5000
                    ? .8 : Math.Max(.02, Main.Character.adventure.attackSpeed);
                var hasModeledTargetTime = route.Climbing
                                           && !double.IsNaN(route.TargetKillSeconds)
                                           && !double.IsInfinity(route.TargetKillSeconds)
                                           && route.TargetKillSeconds > 0.0;
                var targetCombatSeconds = hasModeledTargetTime
                    ? route.TargetKillSeconds
                    : route.Climbing ? ItopodFightProgressWatch.NoProgressSeconds : attackCadence;
                var killCycleSeconds = targetCombatSeconds + Math.Max(0.0,
                    Main.Character.adventureController.respawnTime());
                var ordinaryPpPerSecond = 0.0;
                try
                {
                    ordinaryPpPerSecond = Math.Max(0.0,
                        Main.Character.adventureController.itopod.progressGained(
                            Math.Max(0, route.Climbing ? route.End - 1 : route.FarmFloor)))
                        / (double)Math.Max(1,
                            Main.Character.adventureController.itopod.pointThreshold())
                        / Math.Max(.02, killCycleSeconds);
                }
                catch { ordinaryPpPerSecond = 0.0; }
                var optionalOnly = _collectionTarget != null
                                   && _collectionTarget.Target != null
                                   && _collectionTarget.SetRewardNativeMagnitude <= 0.0
                                   && _collectionTarget.UsefulBoostGain <= 0.0
                                   && _collectionTarget.OptionalProgressionGain > 0.0;
                // While an open-ended climb is active, value the next decade's hardest fought
                // floor even when it lies beyond the diagnostic model. When the breaker is held,
                // retain only the conservative reach so the selector chooses steady farming.
                var valuedItopodReach = route.Climbing
                    ? Math.Max(route.FrontierFloor, Math.Max(0, route.End - 1))
                    : route.FrontierFloor;
                var frontlineCompletionHorizon = Plan == null
                                                  || Plan.RebirthExecutionHold
                                                  || Plan.RebirthSeconds < 0
                    ? -1.0
                    : Math.Max(0.0, Plan.RebirthSeconds
                        - Main.Character.rebirthTime.totalseconds);
                routePlan = ItopodPerkPlanner.ChooseAdventureRoute(
                    Math.Max(1, Main.Character.adventure.highestItopodLevel),
                    valuedItopodReach,
                    killCycleSeconds,
                    Main.Character.adventure.itopod.perkPoints,
                    Math.Max(0L, Config.PPReserve), nextPerkCost, nextPerkGain,
                    collectionDebt,
                    _collectionTarget != null && _collectionTarget.BossOnly,
                    _collectionTarget != null && _collectionTarget.IsBackfill,
                    _collectionTarget == null ? -1.0
                        : _collectionTarget.ExpectedTargetDropSeconds,
                    _collectionTarget == null ? 0.0
                        : Math.Max(_collectionTarget.SetRewardNativeMagnitude,
                            _collectionTarget.OptionalProgressionGain),
                    progressionPush, terminalItopodDropMissing,
                    Main.Character.adventure.zone >= 1000
                        ? Main.Character.adventureController.itopodLevel : -1,
                    Main.Character.adventureController.itopodKillCount,
                    Main.Character.adventure.itopodStart,
                    Main.Character.adventure.itopodEnd,
                    ordinaryPpPerSecond, optionalOnly,
                    optionalOnly && _collectionTarget != null
                    && _collectionTarget.NeedsCadenceProbe,
                    _collectionTarget != null
                    && _collectionTarget.CoreSetIncomplete
                    && !_collectionTarget.IsBackfill,
                    route.Climbing, frontlineCompletionHorizon);
                _lastAdventureRoutePlan = routePlan;
                _lastItopodCompletionHorizonSeconds = frontlineCompletionHorizon;
                var chooseItopod = routePlan.Choice == AdventureRouteChoice.ItopodFrontier
                                   || routePlan.Choice == AdventureRouteChoice.ItopodFarm;
                if (!chooseItopod)
                {
                    if (best != null)
                        ProgressionLoadoutOptimizer.SetAdventureRouteObjective(best.Zone,
                            routePlan.Choice == AdventureRouteChoice.ProgressionPush,
                            true, routePlan.Choice == AdventureRouteChoice.BossSnipe, false);
                }
                else
                {
                if (route.RequiresZoneReset && Main.Character.adventure.zone >= 1000)
                {
                    Main.LogAction("ITOPOD", "Resetting the live sentinel floor through Safe Zone before the next spawn");
                    combat.ManualZone(-1, false, false, false, false, false);
                    CaptureRecovery(combat);
                    return true;
                }
                // The native itopodOn unlock is authoritative. Requiring a T4 kill for a fixed
                // farm stranded early saves that legitimately unlocked ITOPOD through another
                // route: the selector chose permanent PP/EXP/AP/boost income, then execution
                // silently fell through to an irrelevant ordinary zone. A confirmed unlocked
                // route therefore executes whether it is climbing or farming.
                if (Main.Character.settings.itopodOn)
                {
                    var move69Pending = Main.Character.adventure.move69Unlocked
                                        && Main.Character.adventure.move69Used < 69
                                        && !EndgameDependencyModel.IsOwned(Main.Character, 481);
                    var attackTraining = Main.Character.training.attackTraining;
                    var manualItopod = Main.Settings.ITOPODCombatMode != 1
                                       && attackTraining != null && attackTraining.Length > 0
                                       && attackTraining[0] >= 5000;
                    var fightType = move69Pending || manualItopod ? 2 : 0;
                    // Loadout staging and a native zone change may require adjacent enemy-free
                    // frames. Publish the final ITOPOD target before staging so the typed outer
                    // Adventure intent can verify this Safe-Zone/preparation step instead of
                    // misclassifying useful progress as a missing route and quarantining the class.
                    _adventureTarget = new ZoneTarget {Zone = 1000, FightType = fightType};
                    // A reach proof may depend on an owned combat set that is not
                    // currently equipped. Deliberately leave an ordinary zone before
                    // staging it. Waiting for the brief natural gap between enemy
                    // spawns is not live: this controller runs once per second, so an
                    // idle fight can hide every such gap forever.
                    if (Main.Character.adventure.zone != 1000)
                    {
                        if (Main.Character.adventure.zone != -1)
                        {
                            combat.MoveToZone(-1);
                            CaptureRecovery(combat);
                            return true;
                        }
                        if (!ProgressionLoadoutOptimizer.PrepareItopodRoute())
                            return true;
                    }
                    if (_loggedAdventureZone != 1000 || _loggedAdventureFightType != fightType)
                    {
                        Main.LogAction("ADVENTURE", (route.Climbing
                            ? "Climbing ITOPOD range " + route.Start + "-" + route.End
                              + " for globally valued first-clear PP"
                            : "Farming ITOPOD floor " + route.FarmFloor
                              + (terminalItopodDropMissing ? " for END item 491" : string.Empty))
                            + "; " + routePlan.Reason);
                        _loggedAdventureZone = 1000;
                        _loggedAdventureFightType = fightType;
                    }
                    if (fightType == 2)
                    {
                        // Leaving ITOPOD and re-entering at the configured range start resets the
                        // native ten-kill floor counter. Charge/Parry recycling previously caused
                        // a Safe-Zone hop roughly every cooldown, so a floor needing ten kills
                        // could remain at its start forever despite two-second victories. The
                        // frontier oracle admits only a set that survives without a pre-cast;
                        // keep the stream continuous and let ordinary in-fight moves do the work.
                        combat.ManualZone(1000, false, true, false, true,
                            Main.Settings.ITOPODBeastMode);
                    }
                    else
                        combat.IdleZone(1000, false, true, Main.Settings.ITOPODBeastMode);
                    CaptureRecovery(combat);
                    return true;
                }
                }
                if (_collectionTarget != null && _collectionTarget.Target != null)
                {
                    _adventureTarget = _collectionTarget.Target;
                    best = _adventureTarget;
                }
            }

            if (best == null)
                return false;

            if (routePlan == null)
            {
                var progressionFront = ZoneStatHelper.GetBestZone();
                var push = progressionFront != null && best.Zone == progressionFront.Zone
                           && best.FightType != 2;
                ProgressionLoadoutOptimizer.SetAdventureRouteObjective(best.Zone, push,
                    true, _collectionTarget != null && _collectionTarget.BossOnly, false);
            }

            // Native Adventure can reject a direct zone replacement while the current enemy owns
            // combat. This is especially visible when the route selector asks for a one-kill
            // collection cadence probe while ITOPOD is streaming. Publish `best` as the durable
            // target, settle one root in Safe Zone, and enter it on the following root. The typed
            // outer intent deliberately accepts Zone=-1 as useful transition progress.
            if (best.Zone >= 0 && Main.Character.adventure.zone != best.Zone
                && Main.Character.adventure.zone != -1)
            {
                Main.LogAction("ADVENTURE", "Leaving "
                    + GameNames.Zone(Main.Character, Main.Character.adventure.zone)
                    + " through Safe Zone before routing to "
                    + GameNames.Zone(Main.Character, best.Zone));
                combat.MoveToZone(-1);
                CaptureRecovery(combat);
                return true;
            }
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

        internal int CurrentAdventureTargetZone
        {
            get { return _adventureTarget == null ? -1 : _adventureTarget.Zone; }
        }

        internal int CurrentAdventureFightType
        {
            get { return _adventureTarget == null ? -1 : _adventureTarget.FightType; }
        }

        private sealed class GoldBootstrapSink
        {
            internal string Name = string.Empty;
            internal double Cost;
            internal double Score;
        }

        private GoldBootstrapDecision EvaluateGoldBootstrap(Character c)
        {
            if (c == null || Plan == null || c.rebirthTime == null)
                return GoldBootstrapDecision.Hold("Gold bootstrap is waiting for a live plan");
            var remaining = Math.Max(0.0,
                Plan.EffectiveAllocationTarget(c) - c.rebirthTime.totalseconds);
            var sink = FindGoldBootstrapSink(c, remaining);
            var zone = ZoneStatHelper.GetBestZone();
            if (sink == null || zone == null)
                return GoldBootstrapDecision.Hold(sink == null
                    ? "No finishable Augment or valued Blood purchase needs Gold before rebirth"
                    : "No safe ordinary Adventure zone is currently available for Gold");
            var baseGold = GoldBootstrapPlanner.OrdinaryMobBaseGold(zone.Zone);
            // ZoneStatHelper is a routing threshold, not an exact enemy-time proof. Charge the
            // full bounded ordinary-fight horizon here; the loadout solver independently rejects
            // any complete candidate whose captured enemy takes longer than 120 seconds.
            var killCycle = 120.0 + Math.Max(0.0,
                c.adventureController.respawnTime());
            return GoldBootstrapPlanner.Evaluate(new GoldBootstrapSnapshot
            {
                CurrentGold = Math.Max(0.0, c.realGold),
                SinkCost = sink.Cost,
                SinkName = sink.Name,
                SelectedBoss = c.bossID,
                HighestBoss = c.highestBoss,
                TimeMachineChallenge = c.challenges.timeMachineChallenge.inChallenge,
                BaseGoldRecord = c.machine == null ? 0.0 : Math.Max(0.0, c.machine.realBaseGold),
                GrossGoldPerSecond = Math.Max(0.0, c.grossGoldPerSecond()),
                RemainingSeconds = remaining,
                TargetZone = zone.Zone,
                TargetFightType = zone.FightType,
                TargetMobBaseGold = baseGold,
                TotalGoldDropMultiplier = Math.Max(0.0, c.totalGoldbonus()),
                KillCycleSeconds = killCycle
            });
        }

        private GoldBootstrapSink FindGoldBootstrapSink(Character c, double remaining)
        {
            GoldBootstrapSink best = null;
            try
            {
                // Only create an Augment demand while the active generated breakpoint really
                // contains BestAug. Use the cap written by that breakpoint; an uncapped BestAug
                // receives a conservative 20% of the current Energy pool after earlier priorities.
                var active = Plan.Energy.Where(x => x.Time <= c.rebirthTime.totalseconds)
                    .OrderBy(x => x.Time).LastOrDefault();
                var augPercent = 0.0;
                var augEnabled = false;
                if (active != null && active.Priorities != null)
                {
                    foreach (var priority in active.Priorities)
                    {
                        if (string.Equals(priority, "BESTAUG", StringComparison.OrdinalIgnoreCase))
                        {
                            augEnabled = true;
                            augPercent = Math.Max(augPercent, 20.0);
                        }
                        else if (priority != null && priority.StartsWith("CAPBESTAUG:",
                                     StringComparison.OrdinalIgnoreCase))
                        {
                            double parsed;
                            if (double.TryParse(priority.Substring("CAPBESTAUG:".Length),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out parsed))
                            {
                                augEnabled = true;
                                augPercent = Math.Max(augPercent, parsed);
                            }
                        }
                    }
                }
                if (augEnabled && c.buttons.augmentation.interactable
                    && !c.challenges.noAugsChallenge.inChallenge
                    && c.augments != null && c.augmentsController != null)
                {
                    var energy = Math.Max(1L, (long)Math.Floor(
                        Math.Max(0L, c.curEnergy) * Math.Max(0.0, augPercent) / 100.0));
                    var count = Math.Min(c.augments.augs.Length,
                        c.augmentsController.augments.Length);
                    for (var i = 0; i < count; i++)
                    {
                        var state = c.augments.augs[i];
                        var controller = c.augmentsController.augments[i];
                        if (!controller.augLocked() && !controller.hitAugmentTarget()
                            && state.augProgress <= 0.0)
                        {
                            var seconds = controller.AugTimeLeftEnergy(energy);
                            if (GoldBootstrapPlanner.HasPayoffWindow(seconds, remaining))
                            {
                                var level = (double)state.augLevel;
                                var upgrade = (double)state.upgradeLevel;
                                var marginal = controller.baseBoost * (upgrade * upgrade + 1.0)
                                    * (Math.Pow(level + 1.0, controller.augTierBonus())
                                       - Math.Pow(level, controller.augTierBonus()));
                                ConsiderGoldSink(ref best,
                                    GameNames.Augment(c, i, false), controller.getAugCost(),
                                    marginal / Math.Max(.0001, seconds));
                            }
                        }
                        if (!controller.upgradeLocked() && !controller.hitUpgradeTarget()
                            && state.augLevel > 0 && state.upgradeProgress <= 0.0)
                        {
                            var seconds = controller.UpgradeTimeLeftEnergy(energy);
                            if (GoldBootstrapPlanner.HasPayoffWindow(seconds, remaining))
                            {
                                var level = (double)state.augLevel;
                                var upgrade = (double)state.upgradeLevel;
                                var marginal = controller.baseBoost * (2.0 * upgrade + 1.0)
                                               * Math.Pow(level, controller.augTierBonus());
                                ConsiderGoldSink(ref best,
                                    GameNames.Augment(c, i, true), controller.getUpgradeCost(),
                                    marginal / Math.Max(.0001, seconds));
                            }
                        }
                    }
                }

                // BR publishes this only after proving a valued Blood target, sufficient Magic,
                // native duration, and the same reset horizon. Prefer it when its exact charge is
                // cheaper than the selected Augment, otherwise retain the higher marginal Augment.
                var bloodShortfall = AllocationProfiles.BreakpointTypes.BR.LastGoldShortfall;
                if (bloodShortfall > 0.0)
                    ConsiderGoldSink(ref best, "valued Blood ritual",
                        Math.Max(0.0, c.realGold) + bloodShortfall,
                        best == null ? 1.0 : best.Score);
            }
            catch
            {
                return null;
            }
            return best;
        }

        private static void ConsiderGoldSink(ref GoldBootstrapSink best, string name,
            double cost, double score)
        {
            if (string.IsNullOrEmpty(name) || cost <= 0.0 || double.IsNaN(cost)
                || double.IsInfinity(cost) || score <= 0.0 || double.IsNaN(score)
                || double.IsInfinity(score))
                return;
            if (best == null || score > best.Score + 1e-12
                || Math.Abs(score - best.Score) <= 1e-12 && cost < best.Cost)
                best = new GoldBootstrapSink {Name = name, Cost = cost, Score = score};
        }

        private static bool PrepareEndgameTitan12Version(CombatManager combat)
        {
            var c = Main.Character;
            if (c == null || c.settings.rebirthDifficulty != difficulty.sadistic)
                return true;
            var desiredOneBased = EndgameDependencyModel.NextMissingTitan12Version(c);
            if (desiredOneBased < 1 || !ZoneHelpers.TitanSpawningSoon(11))
                return true;
            var desired = desiredOneBased - 1;
            if (c.adventure.titan12Version == desired)
                return true;
            if (!CombatManager.IsZoneUnlocked(ZoneHelpers.TitanZones[11]))
            {
                ExecutionSafety.ReportHold("end-t12-version-zone",
                    "T12 END-piece version " + desiredOneBased
                    + " is selected by the dependency graph but zone 42 is not reachable");
                return false;
            }

            combat.MoveToZone(ZoneHelpers.TitanZones[11]);
            if (c.adventure.zone != ZoneHelpers.TitanZones[11])
                return false;
            c.adventureController.changeTitanDifficulty(desired);
            if (c.adventure.titan12Version != desired)
            {
                Main.LogAction("REJECTED", "Native T12 version selector did not retain END target v"
                                           + desiredOneBased);
                return false;
            }
            Main.LogAction("ADVENTURE", "Selected T12 v" + desiredOneBased
                                               + " for missing END item "
                                               + EndgameDependencyModel.TitanVersionItem(desiredOneBased)
                                               + " [native version field confirmed]");
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
                       + "  \"producerSessionId\": \"" + EscapeJson(Main.SessionId) + "\",\n"
                       + "  \"activeLocationSha256AtObservation\": \"" + EscapeJson(Main.ActiveLocationSha256AtObservation) + "\",\n"
                       + "  \"diskArtifactSha256\": \"" + EscapeJson(Main.DiskArtifactSha256) + "\",\n"
                       + "  \"gameAssemblySha256\": \"" + EscapeJson(Main.GameAssemblySha256) + "\",\n"
                       + "  \"gameAssemblyMvid\": \"" + typeof(Character).Assembly.ManifestModule.ModuleVersionId + "\",\n"
                       + BindingHealthJson()
                       + "  \"gameEpochFingerprint\": \"" + EscapeJson(Main.CurrentGameEpochFingerprint) + "\",\n"
                       + "  \"gameEpochPhase\": \"" + GameEpochController.Shared.Phase + "\",\n"
                       + "  \"gameEpochMutationOpen\": " + GameEpochController.Shared.MutationOpen.ToString().ToLowerInvariant() + ",\n"
                       + "  \"activeImageHashAvailable\": false,\n"
                       + "  \"activeMatchesDisk\": \"unknown-until-reinjection-build-id-verification\",\n"
                       + "  \"decisionSequence\": " + (++_decisionSequence) + ",\n"
                       + "  \"time\": \"" + DateTime.UtcNow.ToString("o") + "\",\n"
                       + "  \"enabled\": " + enabled.ToString().ToLowerInvariant() + ",\n"
                       + "  \"mode\": \"" + mode + "\",\n"
                       + "  \"synced\": false,\n"
                       + "  \"syncState\": \"main-menu\",\n"
                       + "  \"syncDetail\": \"" + escapedDetail + "\",\n"
                       + "  \"authorityStage\": \"ObserveOnly\",\n"
                       + "  \"globalScheduler\": " + GlobalSchedulerJson() + ",\n"
                       + "  \"mutationRoot\": " + MutationRootJson() + ",\n"
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
            // An unscheduled optimizer hold is not a TIME rebirth sixty seconds in
            // the future. Keep the allocation profile active but omit its rebirth
            // breakpoint until a mathematically valid execution target exists.
            File.WriteAllText(_profilePath, Plan.ToProfileJson(CanExecuteIrreversible
                && Config.AllowRebirths && !Plan.RebirthExecutionHold,
                // Typed ResetProgressionTransaction is the sole challenge-entry writer. Never
                // emit BaseRebirth's direct legacy path into the generated allocation profile.
                false));
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
            var allocationTarget = Plan.EffectiveAllocationTarget(c);
            var goldHorizon = ResourceHorizonModel.EvaluateGold(c,
                Math.Max(0, (int)Math.Floor(allocationTarget - c.rebirthTime.totalseconds)));
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
            var bossFitTarget = Plan.RebirthExecutionHold
                ? elapsedSeconds + bossRawProjectionHorizon : Plan.RebirthSeconds;
            var bossFitEta = NextBossViabilityEta(c, bossFitTarget);
            // Preserve a raw selected-boss estimate even when it does not fit the
            // chosen reset.  The separate fit/slack fields prevent that estimate
            // from being mistaken for an action the current run will actually take.
            var bossViabilityEta = bossFitEta >= 0 ? bossFitEta
                : RawSelectedBossDefeatEta(c, bossRawProjectionHorizon);
            var bossSelectedId = c.bossID + 1;
            var bossRecordTargetId = activeHighestBoss + 1;
            var bossTargetMatchesSelected = c.bossID == activeHighestBoss;
            var bossHorizon = Plan.RebirthExecutionHold ? bossRawProjectionHorizon
                : Math.Max(0, Plan.RebirthSeconds - elapsedSeconds);
            var activeGoalsJson = ProgressionGoalEngine.ToJson(ProgressionGoalEngine.ActiveGoals(c,
                trainingGoal, trainingEtaSeconds, bossFitEta, bossFitTarget, Plan.RebirthReason));
            var escapedTrainingGoal = trainingGoal.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var bossReady = IsNextBossReady(c);
            var bossFighting = c.bossController != null && (c.bossController.isFighting || c.bossController.nukeBoss);
            var bossKillEta = CurrentBossKillEta(c);
            // This is deliberately a rolling estimate. Future training, allocation and drops will
            // move it, but hiding the ETA at every model-changing event makes the control loop
            // unreadable. Recompute from the fresh live state each second instead.
            var bossViabilityReason = BossViabilityReason(c, bossReady, bossFighting, bossKillEta);
            var bossEtaState = c.bossController == null ? "controller-unavailable"
                : bossFighting && bossKillEta >= 0 ? "active-fight"
                : bossViabilityEta >= 0 ? "finite"
                : "outside-seven-day-current-allocation-model";
            var energyIncome = Math.Max(0.0, c.energyPerSecond());
            var magicIncome = Math.Max(0.0, c.magicPerSecond());
            var energySweepBound = Math.Max(1L, (long)Math.Ceiling(energyIncome * 0.2) + 1L);
            var productiveTrainingHeadroom = ProductiveBasicTrainingHeadroom(c);
            var energyIdleReason = c.idleEnergy <= 0 ? "fully-allocated"
                : c.idleEnergy <= energySweepBound ? "between-allocation-sweeps"
                : productiveTrainingHeadroom > 0 ? "productive-basic-training-headroom-unallocated"
                : "all-unlocked-basic-training-speed-capped-no-other-admitted-target";
            var energyBreakdown = EnergyAllocationBreakdown(c);
            var resourceAllocationSummary = ResourceAllocationSummaryJson(c);
            var basicTrainingEnergy = BasicTrainingEnergy(c);
            var nonBasicTrainingEnergy = Math.Max(0L,
                Math.Max(0L, c.curEnergy - c.idleEnergy) - basicTrainingEnergy);
            var projectedAttackMultiplier = c.attackMulti > 0 ? c.nextAttackMulti / c.attackMulti : c.nextAttackMulti;
            var projectedDefenseMultiplier = c.defenseMulti > 0 ? c.nextDefenseMulti / c.defenseMulti : c.nextDefenseMulti;
            var bossCatchupComplete = c.bossID == activeHighestBoss;
            var rebirthPreviewMonotonic = projectedAttackMultiplier > 1.0 && projectedDefenseMultiplier > 1.0;
            var rebirthNumberSafe = projectedAttackMultiplier >= 1.0 - 1e-12
                                    && projectedDefenseMultiplier >= 1.0 - 1e-12
                                    && !double.IsNaN(projectedAttackMultiplier)
                                    && !double.IsNaN(projectedDefenseMultiplier)
                                    && !double.IsInfinity(projectedAttackMultiplier)
                                    && !double.IsInfinity(projectedDefenseMultiplier);
            var recoveryResetEta = Plan.RebirthRecoveryEtaSeconds;
            var recoveryContinueEta = -1;
            var recoveryMode = c.settings.rebirthDifficulty == difficulty.normal
                               && c.bossID < c.highestBoss;
            // Mirror the irreversible admission kernel in telemetry. Aggregate one-run score is
            // necessary but not sufficient below the Boss record; unknown exact replay ETA must be
            // published as a hold instead of claiming that reset is recovery-efficient.
            var liveRecoveryRatio = Math.Min(projectedAttackMultiplier,
                projectedDefenseMultiplier);
            var recoveryPolicyRatio = Plan.RebirthSeconds > elapsedSeconds
                ? Plan.RebirthMinimumNumberRatio : liveRecoveryRatio;
            var recoveryPolicy = RebirthOptimizer.EvaluateMutationPolicy(
                Plan.RebirthSelectedScorePerHour, true, recoveryPolicyRatio, recoveryMode,
                recoveryResetEta, recoveryContinueEta);
            var recoveryResetEfficient = recoveryPolicy.Authorized;
            var recoveryRouteReason = recoveryPolicy.Reason;
            if (!recoveryMode)
            {
                recoveryResetEta = -1;
                recoveryContinueEta = -1;
            }
            var rebirthSafetyBlockReason = !Config.AllowRebirths
                ? "rebirth execution is disabled in autopilot settings"
                : Plan.RebirthExecutionHold
                    ? "the event-driven planner has not admitted a valid finite mutation boundary"
                    : Plan.RebirthBoundaryHold
                        ? Plan.RebirthBoundaryReason
                    : Plan.RebirthSeconds <= elapsedSeconds
                      && recoveryMode && !recoveryPolicy.Authorized
                        ? recoveryPolicy.Reason
                    : string.Empty;
            var projectedRebirthAp = MechanicsProgression.TimeAp(Plan.RebirthSeconds);
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
            var itopodRoute = ZoneHelpers.LastItopodRoute;
            var itopodProgressPerKill = c.settings.itopodOn
                ? c.adventureController.itopod.progressGained(c.adventureController.itopodLevel) : 0;
            var itopodPointThreshold = c.settings.itopodOn
                ? c.adventureController.itopod.pointThreshold() : 0;
            var escapedAdventureTargetName = adventureTargetName.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var adventureSafeZoneSeconds = adventureZone == -1 && _adventureSafeZoneSince != DateTime.MinValue
                ? Math.Max(0, (int)Math.Floor((DateTime.UtcNow - _adventureSafeZoneSince).TotalSeconds)) : 0;
            var adventureControlReason = _goldBootstrapDecision != null
                                         && _goldBootstrapDecision.ShouldRoute
                ? _goldBootstrapDecision.Reason
                : _lastAdventureRoutePlan != null ? _lastAdventureRoutePlan.Reason
                : adventureZone != -1 ? "engaged selected Adventure target"
                : !string.IsNullOrEmpty(_adventureRecoveryReason) ? _adventureRecoveryReason
                : adventureTargetZone >= 0 ? "transiting from Safe Zone to " + adventureTargetName
                : "waiting for the Adventure planner to select a target";
            var collectionReason = _collectionTarget == null
                ? _majorUnlockTarget == null
                    ? "Collection planner is waiting for a fightable Adventure target"
                    : "Deferred while pursuing " + _majorUnlockTarget.Mechanic
                : _collectionTarget.Reason;
            var collectionMissing = _collectionTarget == null ? "unknown" : _collectionTarget.MissingSummary;
            var collectionSetReward = _collectionTarget == null ? "unresolved" : _collectionTarget.SetReward;
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
                       + "  \"producerSessionId\": \"" + EscapeJson(Main.SessionId) + "\",\n"
                       + "  \"activeLocationSha256AtObservation\": \"" + EscapeJson(Main.ActiveLocationSha256AtObservation) + "\",\n"
                       + "  \"diskArtifactSha256\": \"" + EscapeJson(Main.DiskArtifactSha256) + "\",\n"
                       + "  \"gameAssemblySha256\": \"" + EscapeJson(Main.GameAssemblySha256) + "\",\n"
                       + "  \"gameAssemblyMvid\": \"" + typeof(Character).Assembly.ManifestModule.ModuleVersionId + "\",\n"
                       + BindingHealthJson()
                       + "  \"gameEpochFingerprint\": \"" + EscapeJson(Main.CurrentGameEpochFingerprint) + "\",\n"
                       + "  \"gameEpochPhase\": \"" + GameEpochController.Shared.Phase + "\",\n"
                       + "  \"gameEpochMutationOpen\": " + GameEpochController.Shared.MutationOpen.ToString().ToLowerInvariant() + ",\n"
                       + "  \"activeImageHashAvailable\": false,\n"
                       + "  \"activeMatchesDisk\": \"unknown-until-reinjection-build-id-verification\",\n"
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
                       + "  \"authorityStage\": \"" + Plan.AuthorityStage + "\",\n"
                       + "  \"stagedAuthority\": {\"verifiedReversible\":" + (Plan.AuthorityStage == AutopilotAuthorityStage.VerifiedReversible).ToString().ToLowerInvariant()
                       + ",\"permanentPurchases\":" + Plan.PermanentPurchasesAuthorized.ToString().ToLowerInvariant()
                       + ",\"moneyPit\":" + Plan.MoneyPitAuthorized.ToString().ToLowerInvariant()
                       + ",\"challenges\":" + Plan.ChallengesAuthorized.ToString().ToLowerInvariant()
                       + ",\"difficulty\":" + Plan.DifficultyAuthorized.ToString().ToLowerInvariant()
                       + ",\"titan1Through12\":" + Plan.TitanOneThroughTwelveAuthorized.ToString().ToLowerInvariant()
                       + ",\"titan13Through14\":" + Plan.TitanThirteenFourteenAuthorized.ToString().ToLowerInvariant()
                       + ",\"move69\":" + Plan.Move69Authorized.ToString().ToLowerInvariant()
                       + ",\"endSequence\":" + Plan.EndSequenceAuthorized.ToString().ToLowerInvariant() + "},\n"
                       + "  \"globalScheduler\": " + GlobalSchedulerJson() + ",\n"
                       + "  \"mutationRoot\": " + MutationRootJson() + ",\n"
                       + "  \"stage\": \"" + escapedStage + "\",\n"
                       + "  \"objective\": \"" + escapedObjective + "\",\n"
                       + "  \"rebirthSeconds\": " + Plan.RebirthSeconds + ",\n"
                       + "  \"rebirthReason\": \"" + EscapeJson(Plan.RebirthReason) + "\",\n"
                       + "  \"rebirthExecutionHold\": " + Plan.RebirthExecutionHold.ToString().ToLowerInvariant() + ",\n"
                       + "  \"rebirthBoundaryHold\": " + Plan.RebirthBoundaryHold.ToString().ToLowerInvariant() + ",\n"
                       + "  \"rebirthBoundaryReason\": \"" + EscapeJson(Plan.RebirthBoundaryReason) + "\",\n"
                       + "  \"rebirthNextPositiveEtaSeconds\": " + Plan.RebirthNextPositiveEtaSeconds + ",\n"
                       + "  \"rebirthNextEvaluationEtaSeconds\": " + Plan.RebirthNextEvaluationEtaSeconds + ",\n"
                       + "  \"rebirthEtaReason\": \"" + EscapeJson(Plan.RebirthEtaReason) + "\",\n"
                       + "  \"rebirthRunnerUpSeconds\": " + Plan.RebirthRunnerUpSeconds + ",\n"
                       + "  \"rebirthRunnerUpDeltaSeconds\": " + Plan.RebirthRunnerUpDeltaSeconds + ",\n"
                       + "  \"rebirthRunnerUpReason\": \"" + EscapeJson(Plan.RebirthRunnerUpReason) + "\",\n"
                       + "  \"rebirthOptimizerModel\": \"rolling-finite-checkpoint-v9\",\n"
                       + "  \"rebirthObjective\": \"maintain a finite ordinary-rebirth countdown from total persistent cycle value; only the native No-Rebirth challenge can suppress execution\",\n"
                       + "  \"rebirthSelectedScorePerHour\": " + Plan.RebirthSelectedScorePerHour.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthRunnerUpScorePerHour\": " + Plan.RebirthRunnerUpScorePerHour.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthOptimizerProjectedMultiplier\": " + Plan.RebirthProjectedMultiplier.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthOptimizerProjectedAp\": " + Plan.RebirthProjectedAP + ",\n"
                       + "  \"rebirthOptimizerRecordRecoveryEtaSeconds\": " + Plan.RebirthRecoveryEtaSeconds + ",\n"
                       + "  \"rebirthOptimizerRecoveryRemainingBosses\": " + Plan.RebirthRecoveryRemainingBosses + ",\n"
                       + "  \"rebirthOptimizerRecoveryReason\": \"" + EscapeJson(Plan.RebirthRecoveryReason) + "\",\n"
                       + "  \"rebirthExpectedCatchupExp\": " + Plan.RebirthExpectedCatchupExp.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthExpectedCatchupExpPerHour\": " + Plan.RebirthExpectedCatchupExpPerHour.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthMinimumNumberRatio\": " + Plan.RebirthMinimumNumberRatio.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"rebirthCandidateSummary\": \"" + EscapeJson(Plan.RebirthCandidateSummary) + "\",\n"
                       + "  \"challengeEvidenceSummary\": \"" + EscapeJson(Plan.ChallengeEvidenceSummary) + "\",\n"
                       + "  \"challengeActive\": " + Plan.ChallengeActive.ToString().ToLowerInvariant() + ",\n"
                       + "  \"challengeAllowsRebirth\": " + Plan.ChallengeAllowsRebirth.ToString().ToLowerInvariant() + ",\n"
                       + "  \"challengeRulesSummary\": \"" + EscapeJson(Plan.ChallengeRulesSummary) + "\",\n"
                       + "  \"challengeRebirthPolicy\": \"" + EscapeJson(Plan.ChallengeRebirthPolicy) + "\",\n"
                       + "  \"nextChallengeAdmitted\": " + Plan.ChallengeAdmitted.ToString().ToLowerInvariant() + ",\n"
                       + "  \"nextChallengeName\": \"" + EscapeJson(Plan.ChallengeName) + "\",\n"
                       + "  \"nextChallengeEtaSeconds\": " + Plan.ChallengeClearEtaSeconds + ",\n"
                       + "  \"challengeRecoveryEtaSeconds\": " + Plan.ChallengeRecoveryEtaSeconds + ",\n"
                       + "  \"challengeTargetBoss\": " + Plan.ChallengeTargetBoss + ",\n"
                       + "  \"challengeTargetLevel\": " + Plan.ChallengeTargetLevel + ",\n"
                       + "  \"challengeCompletedBefore\": " + Plan.ChallengeCompletedBefore + ",\n"
                       + "  \"challengeMaxCompletions\": " + Plan.ChallengeMaxCompletions + ",\n"
                       + "  \"challengeEtaReason\": \"" + EscapeJson(Plan.ChallengeEtaReason) + "\",\n"
                       + "  \"endgameObjective\": \"" + EscapeJson(Plan.EndgameObjective) + "\",\n"
                       + "  \"endgameMissingSummary\": \"" + EscapeJson(Plan.EndgameMissingSummary) + "\",\n"
                       + "  \"endgameTitan12VersionTarget\": " + Plan.Titan12VersionTarget + ",\n"
                       + "  \"endgameReadyToTrigger\": " + Plan.EndgameReadyToTrigger.ToString().ToLowerInvariant() + ",\n"
                       + "  \"endgameExecutionAuthorized\": " + Config.AllowEndSequence.ToString().ToLowerInvariant() + ",\n"
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
                       + "  \"rebirthNumberNonRegression\": " + rebirthNumberSafe.ToString().ToLowerInvariant() + ",\n"
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
                       + "  \"questPoints\": " + c.beastQuest.quirkPoints + ",\n"
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
                       + "  \"adventureRouteChoice\": \"" + EscapeJson(_lastAdventureRoutePlan == null ? string.Empty : _lastAdventureRoutePlan.Choice.ToString()) + "\",\n"
                       + "  \"adventureRouteReason\": \"" + EscapeJson(_lastAdventureRoutePlan == null ? string.Empty : _lastAdventureRoutePlan.Reason) + "\",\n"
                       + "  \"itopodMode\": \"" + EscapeJson(itopodRoute.Mode) + "\",\n"
                       + "  \"itopodRouteConfirmed\": " + itopodRoute.Confirmed.ToString().ToLowerInvariant() + ",\n"
                       + "  \"itopodRouteReason\": \"" + EscapeJson(itopodRoute.Reason) + "\",\n"
                       + "  \"itopodCurrentFloor\": " + c.adventureController.itopodLevel + ",\n"
                       + "  \"itopodHighestFloor\": " + c.adventure.highestItopodLevel + ",\n"
                       + "  \"itopodReachableOneHitFloor\": " + itopodRoute.ReachableFloor + ",\n"
                       + "  \"itopodFrontierFloor\": " + itopodRoute.FrontierFloor + ",\n"
                       + "  \"itopodModeledPositiveDamageFloor\": "
                       + itopodRoute.ModeledPositiveDamageFloor + ",\n"
                       + "  \"itopodModelLimitsClimb\": false,\n"
                       + "  \"itopodRequiresFullHpOnEntry\": true,\n"
                       + "  \"itopodClimbAdmissionPolicy\": \"live-outcomes-no-formula-ceiling\",\n"
                       + "  \"itopodFailureLimit\": "
                       + ItopodClimbTrialController.FailureStreakLimit + ",\n"
                       + "  \"itopodNoProgressSeconds\": "
                       + ItopodFightProgressWatch.NoProgressSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"itopodRetryImprovementFraction\": "
                       + ItopodClimbTrialController.ReadmissionImprovement.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"itopodBlockedFloor\": " + itopodRoute.BlockedFloor + ",\n"
                       + "  \"itopodFailureFloor\": " + itopodRoute.FailureFloor + ",\n"
                       + "  \"itopodConsecutiveFailures\": " + itopodRoute.ConsecutiveFailures + ",\n"
                       + "  \"itopodEmpiricalTrial\": " + itopodRoute.EmpiricalTrial.ToString().ToLowerInvariant() + ",\n"
                       + "  \"itopodRangeStart\": " + c.adventure.itopodStart + ",\n"
                       + "  \"itopodRangeEnd\": " + c.adventure.itopodEnd + ",\n"
                       + "  \"itopodFarmFloor\": " + itopodRoute.FarmFloor + ",\n"
                       + "  \"itopodKillsOnFloor\": " + c.adventureController.itopodKillCount + ",\n"
                       + "  \"itopodNextAwardFloor\": " + (_lastAdventureRoutePlan == null ? 0 : _lastAdventureRoutePlan.AwardFloor) + ",\n"
                       + "  \"itopodNextAwardEtaSeconds\": " + (_lastAdventureRoutePlan == null || double.IsInfinity(_lastAdventureRoutePlan.SecondsToAward) ? -1.0 : _lastAdventureRoutePlan.SecondsToAward).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"itopodFrontlineCompletionHorizonSeconds\": " + _lastItopodCompletionHorizonSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"itopodPerkPoints\": " + c.adventure.itopod.perkPoints + ",\n"
                       + "  \"itopodPointProgress\": " + c.adventure.itopod.pointProgress + ",\n"
                       + "  \"itopodPointThreshold\": " + itopodPointThreshold + ",\n"
                       + "  \"itopodProgressPerKill\": " + itopodProgressPerKill + ",\n"
                       + "  \"majorUnlockActive\": " + (_majorUnlockTarget != null).ToString().ToLowerInvariant() + ",\n"
                       + "  \"majorUnlockName\": \"" + EscapeJson(_majorUnlockTarget == null ? string.Empty : _majorUnlockTarget.Mechanic) + "\",\n"
                       + "  \"majorUnlockGoal\": \"" + EscapeJson(_majorUnlockTarget == null ? string.Empty : _majorUnlockTarget.Goal) + "\",\n"
                       + "  \"majorUnlockReason\": \"" + EscapeJson(_majorUnlockTarget == null ? string.Empty : _majorUnlockTarget.Reason) + "\",\n"
                       + "  \"majorUnlockItemId\": " + (_majorUnlockTarget == null ? 0 : _majorUnlockTarget.ItemId) + ",\n"
                       + "  \"majorUnlockGuaranteedDrop\": " + (_majorUnlockTarget != null && _majorUnlockTarget.GuaranteedFirstDrop).ToString().ToLowerInvariant() + ",\n"
                       + "  \"majorUnlockDropChance\": " + (_majorUnlockTarget == null ? 0.0 : _majorUnlockTarget.DropChance).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"majorUnlockExpectedDropSeconds\": " + (_majorUnlockTarget == null ? -1.0 : _majorUnlockTarget.ExpectedDropSeconds).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"majorUnlockP90DropSeconds\": " + (_majorUnlockTarget == null ? -1.0 : _majorUnlockTarget.P90DropSeconds).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"majorUnlockConsecutiveFailures\": " + (_majorUnlockTarget == null ? 0 : _majorUnlockTarget.ConsecutiveFailures) + ",\n"
                       + "  \"majorUnlockRetryEtaSeconds\": " + (_majorUnlockTarget == null ? 0 : _majorUnlockTarget.RetryEtaSeconds) + ",\n"
                       + "  \"collectionTargetZone\": " + (_collectionTarget == null || _collectionTarget.Target == null ? -1 : _collectionTarget.Target.Zone) + ",\n"
                       + "  \"collectionIsBackfill\": " + (_collectionTarget != null && _collectionTarget.IsBackfill).ToString().ToLowerInvariant() + ",\n"
                       + "  \"collectionRemainingItems\": " + (_collectionTarget == null ? 0 : _collectionTarget.RemainingItems) + ",\n"
                       + "  \"collectionProjectedNewSlots\": " + (_collectionTarget == null ? 0 : _collectionTarget.ProjectedNewSlots) + ",\n"
                       + "  \"collectionRequiredFreeReserve\": " + (_collectionTarget == null ? 3 : _collectionTarget.RequiredFreeReserve) + ",\n"
                       + "  \"collectionIncompleteZones\": " + (_collectionTarget == null ? 0 : _collectionTarget.IncompleteZones) + ",\n"
                       + "  \"collectionUsefulBoostDebt\": " + (_collectionTarget == null ? 0.0 : _collectionTarget.UsefulBoostDebt).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"collectionUsefulBoostGain\": " + (_collectionTarget == null ? 0.0 : _collectionTarget.UsefulBoostGain).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"collectionUsefulBoostTarget\": \"" + EscapeJson(_collectionTarget == null ? string.Empty : _collectionTarget.UsefulBoostTarget) + "\",\n"
                       + "  \"collectionStrategicDebt\": " + (_collectionTarget != null && _collectionTarget.StrategicDebt).ToString().ToLowerInvariant() + ",\n"
                       + "  \"collectionCoreSetIncomplete\": " + (_collectionTarget != null && _collectionTarget.CoreSetIncomplete).ToString().ToLowerInvariant() + ",\n"
                       + "  \"collectionOptionalProgressionGain\": " + (_collectionTarget == null ? 0.0 : _collectionTarget.OptionalProgressionGain).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"collectionOptionalCombatGain\": " + (_collectionTarget == null ? 0.0 : _collectionTarget.OptionalCombatGain).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"collectionOptionalProductionGain\": " + (_collectionTarget == null ? 0.0 : _collectionTarget.OptionalProductionGain).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"collectionOptionalProgressionItemId\": " + (_collectionTarget == null ? 0 : _collectionTarget.OptionalProgressionItemId) + ",\n"
                       + "  \"collectionNeedsCadenceProbe\": " + (_collectionTarget != null && _collectionTarget.NeedsCadenceProbe).ToString().ToLowerInvariant() + ",\n"
                       + "  \"collectionObservedKillSeconds\": " + (_collectionTarget == null ? -1.0 : _collectionTarget.ObservedKillSeconds).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"collectionExpectedTargetDropSeconds\": " + (_collectionTarget == null || double.IsInfinity(_collectionTarget.ExpectedTargetDropSeconds) ? -1.0 : _collectionTarget.ExpectedTargetDropSeconds).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"collectionTargetDropConfidenceSeconds\": " + (_collectionTarget == null || double.IsInfinity(_collectionTarget.TargetDropConfidenceSeconds) ? -1.0 : _collectionTarget.TargetDropConfidenceSeconds).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"collectionStochasticEvidence\": \"" + EscapeJson(_collectionTarget == null ? string.Empty : _collectionTarget.StochasticEvidence) + "\",\n"
                       + "  \"collectionOptionalProgressionTarget\": \"" + EscapeJson(_collectionTarget == null ? string.Empty : _collectionTarget.OptionalProgressionTarget) + "\",\n"
                       + "  \"collectionOptionalProgressionKind\": \"" + EscapeJson(_collectionTarget == null ? string.Empty : _collectionTarget.OptionalProgressionKind) + "\",\n"
                       + "  \"collectionSetReward\": \"" + EscapeJson(collectionSetReward) + "\",\n"
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
                       + "  \"goldBootstrapActive\": " + (_goldBootstrapDecision != null && _goldBootstrapDecision.ShouldRoute).ToString().ToLowerInvariant() + ",\n"
                       + "  \"goldBootstrapMode\": \"" + EscapeJson(_goldBootstrapDecision == null ? "None" : _goldBootstrapDecision.Mode.ToString()) + "\",\n"
                       + "  \"goldBootstrapTargetZone\": " + (_goldBootstrapDecision == null ? -1 : _goldBootstrapDecision.TargetZone) + ",\n"
                       + "  \"goldBootstrapSink\": \"" + EscapeJson(_goldBootstrapDecision == null ? string.Empty : _goldBootstrapDecision.SinkName) + "\",\n"
                       + "  \"goldBootstrapSinkCost\": " + (_goldBootstrapDecision == null ? 0.0 : _goldBootstrapDecision.SinkCost).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"goldBootstrapConservativeDrop\": " + (_goldBootstrapDecision == null ? 0.0 : _goldBootstrapDecision.ConservativeDrop).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"goldBootstrapConservativeGps\": " + (_goldBootstrapDecision == null ? 0.0 : _goldBootstrapDecision.ConservativeGps).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"goldBootstrapEtaSeconds\": " + (_goldBootstrapDecision == null || double.IsInfinity(_goldBootstrapDecision.EtaSeconds) ? -1.0 : _goldBootstrapDecision.EtaSeconds).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"goldBootstrapReason\": \"" + EscapeJson(_goldBootstrapDecision == null ? string.Empty : _goldBootstrapDecision.Reason) + "\",\n"
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
                       + "  \"energyBasicTrainingSpeedCapHeadroom\": " + productiveTrainingHeadroom + ",\n"
                       + "  \"energyIdleReason\": \"" + energyIdleReason + "\",\n"
                       + "  \"energyPortfolioDecision\": \"" + EscapeJson(CustomAllocation.LastEnergyPortfolioDecision) + "\",\n"
                       + "  \"basicTrainingLongHorizonPolicy\": \"reserve Energy first for reachable maximum cap-reduction frontiers with at most a two-future-run Energy-cap payback; optimize immediate boss marginal value; then use otherwise-idle Energy to speed-cap every unlocked training\",\n"
                       + "  \"advancedTrainingHorizonDecision\": \"" + EscapeJson(AllocationProfiles.BreakpointTypes.AdvancedTrainingBP.CurrentDecision(c)) + "\",\n"
                       + "  \"advancedTrainingTargetZone\": " + AllocationProfiles.BreakpointTypes.AdvancedTrainingBP.LastTargetZone + ",\n"
                       + "  \"advancedTrainingAttackTarget\": " + AllocationProfiles.BreakpointTypes.AdvancedTrainingBP.LastAttackTarget + ",\n"
                       + "  \"advancedTrainingDefenseTarget\": " + AllocationProfiles.BreakpointTypes.AdvancedTrainingBP.LastDefenseTarget + ",\n"
                       + "  \"advancedTrainingCompletionEtaSeconds\": " + AllocationProfiles.BreakpointTypes.AdvancedTrainingBP.LastCompletionEtaSeconds + ",\n"
                       + "  \"advancedTrainingPolicy\": \"reset-local: allocate only for a finite ordinary-zone threshold, an AT-solvable due Titan, or a five-percent ITOPOD retry event after the empirical three-failure breaker, with time left to use the gain before rebirth\",\n"
                       + "  \"timeMachineHorizonDecision\": \"" + EscapeJson(AllocationProfiles.BreakpointTypes.TimeMachineBP.LastHorizonDecision) + "\",\n"
                       + "  \"energyAllocationBreakdown\": " + energyBreakdown + ",\n"
                       + "  \"resourceAllocationSummary\": " + resourceAllocationSummary + ",\n"
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
                       + "  \"timeMachineEnergyAllocated\": " + c.machine.speedEnergy + ",\n"
                       + "  \"timeMachineSpeedLevel\": " + c.machine.levelSpeed + ",\n"
                       + "  \"timeMachineSpeedProgress\": " + c.machine.speedProgress.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"timeMachineGoldLevel\": " + c.machine.levelGoldMulti + ",\n"
                       + "  \"timeMachineGoldProgress\": " + c.machine.goldMultiProgress.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"timeMachineNextGoldCost\": " + c.timeMachineController.machineGoldMultiCost().ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"timeMachineCurrentMagicProgressPerTick\": " + c.timeMachineController.goldMultiProgressPerTick(c.machine.goldMultiMagic).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"timeMachineFullMagicProgressPerTick\": " + c.timeMachineController.goldMultiProgressPerTick(c.magic.curMagic).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"grossGoldPerSecond\": " + c.grossGoldPerSecond().ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"timeMachineBaseGoldRecord\": " + (c.machine == null ? 0.0 : c.machine.realBaseGold).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"magicBloodAllocated\": " + (c.bloodMagic == null || c.bloodMagic.ritual == null ? 0L : c.bloodMagic.ritual.Sum(x => Math.Max(0L, x.magic))) + ",\n"
                       + "  \"magicWandoosAllocated\": " + c.wandoos98.wandoosMagic + ",\n"
                       + "  \"wandoosEnergyAllocated\": " + c.wandoos98.wandoosEnergy + ",\n"
                       + "  \"wandoosEnergyLevel\": " + c.wandoos98.energyLevel + ",\n"
                       + "  \"wandoosMagicLevel\": " + c.wandoos98.magicLevel + ",\n"
                       + "  \"wandoosEnergyProgress\": " + c.wandoos98.energyProgress.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"wandoosMagicProgress\": " + c.wandoos98.magicProgress.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"wandoosOsLevel\": " + c.wandoos98.OSlevel + ",\n"
                       + "  \"wandoosOsType\": " + (int)c.wandoos98.os + ",\n"
                       + "  \"magicAllocationDecision\": \"" + EscapeJson(CustomAllocation.LastMagicAllocationDecision) + "\",\n"
                       + "  \"res3AllocationDecision\": \"" + EscapeJson(CustomAllocation.LastR3AllocationDecision) + "\",\n"
                       + "  \"bloodMagicAllocationDecision\": \"" + EscapeJson(AllocationProfiles.BreakpointTypes.BR.LastDecision) + "\",\n"
                       + "  \"loadoutDecision\": \"" + EscapeJson(ProgressionLoadoutOptimizer.LastDecision) + "\",\n"
                       + "  \"loadoutObjective\": \"" + EscapeJson(ProgressionLoadoutOptimizer.LastObjective) + "\",\n"
                       + "  \"loadoutSearchExact\": " + ProgressionLoadoutOptimizer.LastSearchExact.ToString().ToLowerInvariant() + ",\n"
                       + "  \"loadoutScoreGain\": " + ProgressionLoadoutOptimizer.LastScoreGain.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"boostDecision\": \"" + EscapeJson(InventoryManager.LastBoostDecision) + "\",\n"
                       + "  \"transformDecision\": \"" + EscapeJson(InventoryManager.LastTransformDecision) + "\",\n"
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
                       + "  \"expPolicyModel\": \"exact progression gate; Energy speed; admitted permanent systems; amortized Magic refill versus the best permanent P/C/B marginal; contextual Ygg and non-duplicated QoL\",\n"
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
                       + "  \"goldEtaSeconds\": " + goldStatus.EtaSeconds + ",\n"
                       + "  \"goldProjectedBaselineAtRebirth\": " + goldHorizon.BaselineAtRebirth.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"goldCommittedBeforeRebirth\": " + goldHorizon.CommittedSpend.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"goldHorizonShortfall\": " + goldHorizon.Shortfall.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "\n"
                       + ",  \"augmentDecision\": \"" + EscapeJson(augmentStatus.Decision) + "\",\n"
                       + "  \"augmentEnergy\": " + augmentStatus.Allocated + ",\n"
                       + "  \"augmentProgress\": " + augmentStatus.Progress.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                       + "  \"augmentEtaSeconds\": " + augmentStatus.EtaSeconds + ",\n"
                       + "  \"characterStats\": " + SafeTelemetry(() => CharacterStatsJson(c), "{}") + ",\n"
                       + "  \"equippedGear\": " + SafeTelemetry(() => EquippedGearJson(c), "[]") + ",\n"
                       + "  \"inventoryItems\": " + SafeTelemetry(() => EquipmentListJson(c, c.inventory.inventory, "inventory"), "[]") + ",\n"
                       + "  \"daycareItems\": " + SafeTelemetry(() => EquipmentListJson(c, c.inventory.daycare, "daycare"), "[]") + ",\n"
                       + "  \"macguffins\": " + SafeTelemetry(() => EquipmentListJson(c, c.inventory.macguffins, "macguffin"), "[]") + ",\n"
                       + "  \"itemListEntries\": " + SafeTelemetry(() => ItemListJson(c), "[]") + ",\n"
                       + "  \"itemListCatalogueCount\": " + ItemCatalogueCount(c) + ",\n"
                       + "  \"itopodPerks\": " + SafeTelemetry(() => ItopodPerksJson(c), "[]") + ",\n"
                       + "  \"expPurchases\": " + SafeTelemetry(() => PublicPurchaseStateJson(c.purchases), "[]") + ",\n"
                       + "  \"apPurchases\": " + SafeTelemetry(() => ApPurchaseStateJson(c), "[]") + ",\n"
                       + "  \"mechanicUnlocks\": " + SafeTelemetry(() => MechanicUnlocksJson(c), "[]") + ",\n"
                       + "  \"nguProgress\": " + SafeTelemetry(() => NguProgressJson(c), "[]") + ",\n"
                       + "  \"hackProgress\": " + SafeTelemetry(() => HackProgressJson(c), "[]") + ",\n"
                       + "  \"wishProgress\": " + SafeTelemetry(() => WishProgressJson(c), "[]") + ",\n"
                       + "  \"fruitProgress\": " + SafeTelemetry(() => FruitProgressJson(c), "[]") + ",\n"
                       + "  \"diggerProgress\": " + SafeTelemetry(() => DiggerProgressJson(c), "[]") + ",\n"
                       + "  \"beardProgress\": " + SafeTelemetry(() => BeardProgressJson(c), "[]") + ",\n"
                       + "  \"goalNodes\": " + activeGoalsJson + "\n"
                       + "}\n";
            WriteAtomic(_decisionPath, json);
        }

        private void WriteDisabledDecision()
        {
            var json = "{\n"
                       + "  \"schemaVersion\": 2,\n"
                       + "  \"buildId\": \"" + typeof(AutopilotManager).Assembly.ManifestModule.ModuleVersionId + "\",\n"
                       + "  \"producerPid\": " + Process.GetCurrentProcess().Id + ",\n"
                       + "  \"producerSessionId\": \"" + EscapeJson(Main.SessionId) + "\",\n"
                       + "  \"activeLocationSha256AtObservation\": \"" + EscapeJson(Main.ActiveLocationSha256AtObservation) + "\",\n"
                       + "  \"diskArtifactSha256\": \"" + EscapeJson(Main.DiskArtifactSha256) + "\",\n"
                       + "  \"gameAssemblySha256\": \"" + EscapeJson(Main.GameAssemblySha256) + "\",\n"
                       + "  \"gameAssemblyMvid\": \"" + typeof(Character).Assembly.ManifestModule.ModuleVersionId + "\",\n"
                       + BindingHealthJson()
                       + "  \"gameEpochFingerprint\": \"" + EscapeJson(Main.CurrentGameEpochFingerprint) + "\",\n"
                       + "  \"gameEpochPhase\": \"" + GameEpochController.Shared.Phase + "\",\n"
                       + "  \"gameEpochMutationOpen\": " + GameEpochController.Shared.MutationOpen.ToString().ToLowerInvariant() + ",\n"
                       + "  \"activeImageHashAvailable\": false,\n"
                       + "  \"activeMatchesDisk\": \"unknown-until-reinjection-build-id-verification\",\n"
                       + "  \"decisionSequence\": " + (++_decisionSequence) + ",\n"
                       + "  \"time\": \"" + DateTime.UtcNow.ToString("o") + "\",\n"
                       + "  \"enabled\": false,\n"
                       + "  \"mutationsEnabled\": false,\n"
                       + "  \"mode\": \"" + EscapeJson(Config.Mode) + "\",\n"
                       + "  \"authorityStage\": \"ObserveOnly\",\n"
                       + "  \"globalScheduler\": " + GlobalSchedulerJson() + ",\n"
                       + "  \"mutationRoot\": " + MutationRootJson() + ",\n"
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

        private static string BindingHealthJson()
        {
            return "  \"nativeBindingKnownBuild\": " + Main.NativeBindingKnownBuild.ToString().ToLowerInvariant() + ",\n"
                   + "  \"nativeBindingsComplete\": " + Main.NativeBindingsComplete.ToString().ToLowerInvariant() + ",\n"
                   + "  \"nativeBindingDescriptorCount\": " + Main.NativeBindingDescriptorCount + ",\n"
                   + "  \"nativeBindingBoundCount\": " + Main.NativeBindingBoundCount + ",\n"
                   + "  \"nativeBindingFailureCount\": " + Main.NativeBindingFailureCount + ",\n"
                   + "  \"nativeBindingFailureSummary\": \"" + EscapeJson(Main.NativeBindingFailureSummary) + "\",\n";
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

        private static string JsonNumber(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value)
                ? "0"
                : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        private string GlobalSchedulerJson()
        {
            if (Plan != null && Plan.GlobalSchedule != null)
            {
                var trace = PlannerTraceRecord.Capture(Plan.GlobalSchedule).ToJson();
                var estimate = Plan.GlobalSchedule.TerminalEta;
                return trace.Substring(0, trace.Length - 1)
                       + ",\"provenance\":\"" + estimate.Provenance
                       + "\",\"sampleCount\":" + estimate.SampleCount
                       + ",\"confidence\":" + JsonNumber(estimate.Confidence) + "}";
            }
            var blocker = Plan == null || Plan.GlobalScheduleBlocker == null
                ? "planner snapshot unavailable" : Plan.GlobalScheduleBlocker.Detail;
            var blockerKind = Plan == null || Plan.GlobalScheduleBlocker == null
                ? PlannerBlockerKind.OutsideModel : Plan.GlobalScheduleBlocker.Kind;
            return "{\"snapshotHash\":\"\",\"modelHash\":\"\",\"objectiveHash\":\"\""
                   + ",\"authority\":\"ShadowOnly\",\"canExecute\":false"
                   + ",\"status\":\"Blocked\",\"nextEvent\":\"\""
                   + ",\"meanSeconds\":null,\"p50Seconds\":null,\"p90Seconds\":null"
                   + ",\"lowerBoundSeconds\":null,\"upperBoundSeconds\":null"
                   + ",\"gapSeconds\":null,\"regretSeconds\":null"
                   + ",\"provenance\":\"Unknown\",\"sampleCount\":0,\"confidence\":0"
                   + ",\"blocker\":\"" + blockerKind + "\",\"blockerDetail\":\""
                   + EscapeJson(blocker) + "\"}";
        }

        private string MutationRootJson()
        {
            return "{\"id\":" + (Plan == null ? 0L : Plan.RootTransactionId)
                   + ",\"state\":\"" + EscapeJson(Plan == null ? "not-planned" : Plan.RootTransactionState)
                   + "\",\"epochFingerprint\":\""
                   + EscapeJson(Plan == null ? Main.CurrentGameEpochFingerprint : Plan.RootEpochFingerprint)
                   + "\",\"committedSteps\":" + (Plan == null ? 0 : Plan.RootCommittedSteps)
                   + ",\"heldSteps\":" + (Plan == null ? 0 : Plan.RootHeldSteps)
                   + ",\"pendingSteps\":" + (Plan == null ? 0 : Plan.RootPendingSteps)
                   + ",\"rejectedSteps\":" + (Plan == null ? 0 : Plan.RootRejectedSteps)
                   + ",\"quarantinedSteps\":" + (Plan == null ? 0 : Plan.RootQuarantinedSteps)
                   + ",\"resultSummary\":\""
                   + EscapeJson(Plan == null ? string.Empty : Plan.RootResultSummary) + "\""
                   + "}";
        }

        private static string SafeTelemetry(Func<string> builder, string fallback)
        {
            try { return builder(); }
            catch { return fallback; }
        }

        private static string CharacterStatsJson(Character c)
        {
            return "{\"fightBossAttack\":" + JsonNumber(c.attack)
                   + ",\"fightBossDefense\":" + JsonNumber(c.defense)
                   + ",\"fightBossCurrentHP\":" + JsonNumber(c.curHP)
                   + ",\"fightBossMaxHP\":" + JsonNumber(c.maxHP)
                   + ",\"adventureAttack\":" + JsonNumber(c.totalAdvAttack())
                   + ",\"adventureDefense\":" + JsonNumber(c.totalAdvDefense())
                   + ",\"adventureCurrentHP\":" + JsonNumber(c.adventure.curHP)
                   + ",\"adventureMaxHP\":" + JsonNumber(c.totalAdvHP())
                   + ",\"energyPower\":" + JsonNumber(c.energyPower)
                   + ",\"energyCap\":" + c.capEnergy
                   + ",\"energyBars\":" + c.energyBars
                   + ",\"magicPower\":" + JsonNumber(c.magic.magicPower)
                   + ",\"magicCap\":" + c.magic.capMagic
                   + ",\"magicBars\":" + c.magic.magicPerBar
                   + ",\"res3Name\":\"" + EscapeJson(c.res3.res3Name) + "\""
                   + ",\"res3Unlocked\":" + c.res3.res3On.ToString().ToLowerInvariant()
                   + ",\"res3Power\":" + JsonNumber(c.res3.res3Power)
                   + ",\"res3Cap\":" + c.res3.capRes3
                   + ",\"res3Bars\":" + c.res3.res3PerBar
                   + ",\"res3Current\":" + c.res3.curRes3
                   + ",\"res3Idle\":" + c.res3.idleRes3
                   + ",\"lifetimeExp\":" + c.stats.totalExp
                   + ",\"lifetimeAp\":" + c.arbitrary.curLifetimePoints
                   + ",\"seeds\":" + c.yggdrasil.seeds
                   + ",\"blood\":" + JsonNumber(c.bloodMagic.bloodPoints)
                   + "}";
        }

        private static string EquippedGearJson(Character c)
        {
            var rows = new List<string>
            {
                EquipmentJson(c, c.inventory.head, "Head", "equipped", 0),
                EquipmentJson(c, c.inventory.chest, "Chest", "equipped", 1),
                EquipmentJson(c, c.inventory.legs, "Legs", "equipped", 2),
                EquipmentJson(c, c.inventory.boots, "Boots", "equipped", 3),
                EquipmentJson(c, c.inventory.weapon, "Weapon", "equipped", 4),
                EquipmentJson(c, c.inventory.weapon2, "Weapon 2", "equipped", 5)
            };
            for (var i = 0; i < c.inventory.accs.Count; i++)
                rows.Add(EquipmentJson(c, c.inventory.accs[i], "Accessory " + (i + 1), "equipped", i));
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static string EquipmentListJson(Character c, IList<Equipment> items, string location)
        {
            if (items == null) return "[]";
            var rows = new List<string>();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.id <= 0) continue;
                rows.Add(EquipmentJson(c, item, location + " " + (i + 1), location, i));
            }
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static string EquipmentJson(Character c, Equipment item, string slot,
            string location, int index)
        {
            if (item == null) item = new Equipment();
            var maxxed = item.id > 0 && c.inventory.itemList.itemMaxxed != null
                          && item.id < c.inventory.itemList.itemMaxxed.Count
                          && c.inventory.itemList.itemMaxxed[item.id];
            var specials = new List<string>();
            AddEquipmentSpecial(specials, item.spec1Type, item.spec1Cur, item.spec1Cap);
            AddEquipmentSpecial(specials, item.spec2Type, item.spec2Cur, item.spec2Cap);
            AddEquipmentSpecial(specials, item.spec3Type, item.spec3Cur, item.spec3Cap);
            return "{\"slot\":\"" + EscapeJson(slot) + "\",\"location\":\""
                   + EscapeJson(location) + "\",\"index\":" + index
                   + ",\"id\":" + item.id + ",\"name\":\""
                   + EscapeJson(item.id <= 0 ? "Empty" : GameNames.Item(c, item.id)) + "\""
                   + ",\"part\":\"" + EscapeJson(item.type.ToString()) + "\""
                   + ",\"level\":" + item.level
                   + ",\"maxxed\":" + maxxed.ToString().ToLowerInvariant()
                   + ",\"locked\":" + (!item.removable).ToString().ToLowerInvariant()
                   + ",\"bossRequired\":" + item.bossRequired
                   + ",\"attack\":" + JsonNumber(item.curAttack)
                   + ",\"attackCap\":" + JsonNumber(item.capAttack)
                   + ",\"defense\":" + JsonNumber(item.curDefense)
                   + ",\"defenseCap\":" + JsonNumber(item.capDefense)
                   + ",\"specials\":[" + string.Join(",", specials.ToArray()) + "]}";
        }

        private static void AddEquipmentSpecial(ICollection<string> rows, specType type,
            double current, double cap)
        {
            if (type == specType.None) return;
            rows.Add("{\"type\":\"" + EscapeJson(type.ToString()) + "\",\"current\":"
                     + JsonNumber(current) + ",\"cap\":" + JsonNumber(cap) + "}");
        }

        private static int ItemCatalogueCount(Character c)
        {
            return c.itemInfo == null || c.itemInfo.itemName == null ? 0 : c.itemInfo.itemName.Length;
        }

        private static string ItemListJson(Character c)
        {
            var dropped = c.inventory.itemList.itemDropped;
            var maxxed = c.inventory.itemList.itemMaxxed;
            var filtered = c.inventory.itemList.itemFiltered;
            var count = Math.Max(ItemCatalogueCount(c), Math.Max(dropped == null ? 0 : dropped.Count,
                maxxed == null ? 0 : maxxed.Count));
            var rows = new List<string>();
            for (var id = 1; id < count; id++)
            {
                var wasDropped = dropped != null && id < dropped.Count && dropped[id];
                var wasMaxxed = maxxed != null && id < maxxed.Count && maxxed[id];
                if (!wasDropped && !wasMaxxed) continue;
                var isFiltered = filtered != null && id < filtered.Count && filtered[id];
                rows.Add("{\"id\":" + id + ",\"name\":\"" + EscapeJson(GameNames.Item(c, id))
                         + "\",\"dropped\":" + wasDropped.ToString().ToLowerInvariant()
                         + ",\"maxxed\":" + wasMaxxed.ToString().ToLowerInvariant()
                         + ",\"filtered\":" + isFiltered.ToString().ToLowerInvariant() + "}");
            }
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static string ItopodPerksJson(Character c)
        {
            if (c.adventureController == null || c.adventureController.itopod == null
                || c.adventure.itopod == null) return "[]";
            var controller = c.adventureController.itopod;
            var levels = c.adventure.itopod.perkLevel;
            var count = Math.Min(levels.Count, Math.Min(controller.perkName.Count,
                Math.Min(controller.cost.Count, controller.maxLevel.Count)));
            var rows = new List<string>();
            for (var id = 0; id < count; id++)
            {
                var requirement = id < controller.perkDifficultyReq.Count
                    ? controller.perkDifficultyReq[id] : difficulty.normal;
                var type = id < controller.perkType.Count ? controller.perkType[id].ToString() : "Unknown";
                rows.Add("{\"id\":" + id + ",\"name\":\"" + EscapeJson(controller.perkName[id])
                         + "\",\"type\":\"" + EscapeJson(type) + "\",\"level\":" + levels[id]
                         + ",\"maxLevel\":" + controller.maxLevel[id] + ",\"baseCost\":"
                         + controller.cost[id] + ",\"difficulty\":" + (int)requirement
                         + ",\"unlocked\":"
                         + (c.settings.itopodOn && c.settings.rebirthDifficulty >= requirement)
                             .ToString().ToLowerInvariant() + "}");
            }
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static string PublicPurchaseStateJson(object purchases)
        {
            if (purchases == null) return "[]";
            var rows = new List<string>();
            foreach (var field in purchases.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)
                         .OrderBy(x => x.Name))
            {
                var value = field.GetValue(purchases);
                if (field.FieldType == typeof(bool))
                    rows.Add(PurchaseFieldJson(field.Name, (bool)value ? "1" : "0", (bool)value));
                else if (field.FieldType == typeof(int) || field.FieldType == typeof(long)
                         || field.FieldType == typeof(float) || field.FieldType == typeof(double))
                {
                    var number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                    rows.Add(PurchaseFieldJson(field.Name, JsonNumber(number), number > 0.0));
                }
            }
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static string PurchaseFieldJson(string field, string value, bool owned)
        {
            return "{\"key\":\"" + EscapeJson(field) + "\",\"name\":\""
                   + EscapeJson(HumanizeIdentifier(field)) + "\",\"value\":" + value
                   + ",\"owned\":" + owned.ToString().ToLowerInvariant() + "}";
        }

        private static string HumanizeIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var clean = value.Replace("buy", string.Empty).Replace("AP", string.Empty)
                .Replace("has", string.Empty).Replace("bought", string.Empty);
            var chars = new List<char>();
            for (var i = 0; i < clean.Length; i++)
            {
                if (i > 0 && char.IsUpper(clean[i]) && !char.IsWhiteSpace(clean[i - 1])) chars.Add(' ');
                chars.Add(clean[i]);
            }
            var result = new string(chars.ToArray()).Trim();
            return result.Length == 0 ? value : char.ToUpperInvariant(result[0]) + result.Substring(1);
        }

        private static string ApPurchaseStateJson(Character c)
        {
            var rows = new List<string>();
            foreach (var pair in ApPurchaseMethods.OrderBy(x => x.Key))
            {
                var unlocked = IsApFeatureUnlocked(c, pair.Key);
                rows.Add("{\"id\":" + pair.Key + ",\"name\":\""
                         + EscapeJson(HumanizeIdentifier(pair.Value)) + "\",\"owned\":"
                         + IsApOwned(c, pair.Key).ToString().ToLowerInvariant()
                         + ",\"unlocked\":" + unlocked.ToString().ToLowerInvariant() + "}");
            }
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static string MechanicUnlocksJson(Character c)
        {
            var rows = new List<string>();
            AddMechanic(rows, "fight-boss", "Fight Boss", true, "Available from the start");
            AddMechanic(rows, "inventory", "Inventory", c.settings.inventoryOn, "Progress the opening bosses");
            AddMechanic(rows, "adventure", "Adventure", c.highestBoss >= 4, "Defeat Boss 4");
            AddMechanic(rows, "augments", "Augments", c.buttons.augmentation.interactable, "Defeat Boss 13");
            AddMechanic(rows, "time-machine", "Time Machine", c.highestBoss >= 30, "Defeat Boss 30");
            AddMechanic(rows, "blood-magic", "Blood Magic", c.buttons.bloodMagic.interactable, "Advance the Fight Boss route");
            AddMechanic(rows, "money-pit", "Money Pit", c.settings.pitUnlocked, "Unlock the Time Machine and Money Pit");
            AddMechanic(rows, "wandoos", "Wandoos", c.settings.wandoos98On, "Consume the Wandoos 98 disk");
            AddMechanic(rows, "yggdrasil", "Yggdrasil", c.settings.yggdrasilOn, "Consume the Seed progression item");
            AddMechanic(rows, "itopod", "ITOPOD", c.settings.itopodOn, "Consume the Infinite Cube/ITOPOD progression item");
            AddMechanic(rows, "ngu", "NGUs", c.settings.nguOn || c.inventory.itemList.numberComplete, "Complete the Number set");
            AddMechanic(rows, "beards", "Beards", c.settings.beardsOn, "Reach the Beard unlock milestone");
            AddMechanic(rows, "diggers", "Gold Diggers", c.settings.diggersOn, "Reach the Digger unlock milestone");
            AddMechanic(rows, "quests", "Beast Quests", c.beastQuest.questsUnlocked, "Defeat the Beast and unlock quests");
            AddMechanic(rows, "hacks", "Hacks", c.hacks.hacksOn, "Enter Evil and unlock Resource 3/Hacks");
            AddMechanic(rows, "wishes", "Wishes", c.wishes.wishesOn, "Advance Evil progression");
            AddMechanic(rows, "cards", "Cards", c.cards.cardsOn, "Advance Sadistic progression");
            AddMechanic(rows, "resource-3", c.res3.res3Name, c.res3.res3On, "Enter Evil difficulty");
            AddMechanic(rows, "daycare", "Item Daycare", c.purchases.hasDaycare, "Buy the permanent Daycare upgrade");
            AddMechanic(rows, "macguffins", "MacGuffins", c.inventory.macguffins != null
                && c.inventory.macguffins.Count > 0, "Obtain the first MacGuffin");
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static void AddMechanic(ICollection<string> rows, string id, string name,
            bool unlocked, string hint)
        {
            rows.Add("{\"id\":\"" + EscapeJson(id) + "\",\"name\":\"" + EscapeJson(name)
                     + "\",\"unlocked\":" + unlocked.ToString().ToLowerInvariant()
                     + ",\"hint\":\"" + EscapeJson(hint) + "\"}");
        }

        private static string NguProgressJson(Character c)
        {
            var rows = new List<string>();
            AddNguRows(rows, c, c.NGU.skills, c.NGUController.NGU, "Energy");
            AddNguRows(rows, c, c.NGU.magicSkills, c.NGUController.NGUMagic, "Magic");
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static void AddNguRows<T>(ICollection<string> rows, Character c, IList<NGU> skills,
            IList<T> controllers, string resource)
        {
            if (skills == null || controllers == null) return;
            var count = Math.Min(skills.Count, controllers.Count);
            for (var id = 0; id < count; id++)
            {
                var nameField = controllers[id].GetType().GetField("NGUName",
                    BindingFlags.Instance | BindingFlags.Public);
                var name = nameField == null ? resource + " NGU " + (id + 1)
                    : Convert.ToString(nameField.GetValue(controllers[id]));
                var skill = skills[id];
                rows.Add("{\"resource\":\"" + resource + "\",\"id\":" + id
                         + ",\"name\":\"" + EscapeJson(name) + "\",\"unlocked\":"
                         + c.settings.nguOn.ToString().ToLowerInvariant() + ",\"normalLevel\":"
                         + skill.level + ",\"evilLevel\":" + skill.evilLevel
                         + ",\"sadisticLevel\":" + skill.sadisticLevel + ",\"allocated\":"
                         + (resource == "Energy" ? skill.energy : skill.magic) + "}");
            }
        }

        private static string HackProgressJson(Character c)
        {
            var rows = new List<string>();
            var count = Math.Min(c.hacks.hacks.Count, c.hacksController.properties.Count);
            for (var id = 0; id < count; id++)
            {
                var hack = c.hacks.hacks[id];
                rows.Add("{\"id\":" + id + ",\"name\":\""
                         + EscapeJson(c.hacksController.properties[id].hackName) + "\",\"unlocked\":"
                         + c.hacks.hacksOn.ToString().ToLowerInvariant() + ",\"level\":" + hack.level
                         + ",\"target\":" + hack.target + ",\"progress\":" + JsonNumber(hack.progress)
                         + ",\"allocated\":" + hack.res3 + "}");
            }
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static string WishProgressJson(Character c)
        {
            var rows = new List<string>();
            var count = Math.Min(c.wishes.wishes.Count, c.wishesController.properties.Count);
            for (var id = 0; id < count; id++)
            {
                var wish = c.wishes.wishes[id];
                var property = c.wishesController.properties[id];
                var unlocked = c.wishes.wishesOn
                               && c.settings.rebirthDifficulty >= property.difficultyRequirement;
                rows.Add("{\"id\":" + id + ",\"name\":\"" + EscapeJson(property.wishName)
                         + "\",\"unlocked\":" + unlocked.ToString().ToLowerInvariant()
                         + ",\"level\":" + wish.level + ",\"maxLevel\":" + property.maxLevel
                         + ",\"progress\":" + JsonNumber(wish.progress) + ",\"energy\":"
                         + wish.energy + ",\"magic\":" + wish.magic + ",\"res3\":" + wish.res3 + "}");
            }
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static string FruitProgressJson(Character c)
        {
            var rows = new List<string>();
            for (var id = 0; id < c.yggdrasil.fruits.Count; id++)
            {
                var fruit = c.yggdrasil.fruits[id];
                rows.Add("{\"id\":" + id + ",\"name\":\"" + EscapeJson(GameNames.Fruit(c, id))
                         + "\",\"unlocked\":" + (c.settings.yggdrasilOn && fruit.maxTier > 0)
                             .ToString().ToLowerInvariant() + ",\"maxTier\":" + fruit.maxTier
                         + ",\"totalLevels\":" + fruit.totalLevels + ",\"activated\":"
                         + fruit.activated.ToString().ToLowerInvariant() + ",\"permanentActivation\":"
                         + fruit.permCostPaid.ToString().ToLowerInvariant() + "}");
            }
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static string DiggerProgressJson(Character c)
        {
            var rows = new List<string>();
            for (var id = 0; id < c.diggers.diggers.Count; id++)
            {
                var digger = c.diggers.diggers[id];
                rows.Add("{\"id\":" + id + ",\"name\":\"" + EscapeJson(GameNames.Digger(c, id))
                         + "\",\"unlocked\":" + (c.settings.diggersOn && digger.maxLevel > 0)
                             .ToString().ToLowerInvariant() + ",\"level\":" + digger.curLevel
                         + ",\"maxLevel\":" + digger.maxLevel + ",\"active\":"
                         + digger.active.ToString().ToLowerInvariant() + "}");
            }
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static string BeardProgressJson(Character c)
        {
            var rows = new List<string>();
            for (var id = 0; id < c.beards.beards.Count; id++)
            {
                var beard = c.beards.beards[id];
                rows.Add("{\"id\":" + id + ",\"name\":\"" + EscapeJson(GameNames.Beard(c, id))
                         + "\",\"unlocked\":" + c.settings.beardsOn.ToString().ToLowerInvariant()
                         + ",\"level\":" + beard.beardLevel + ",\"permanentLevel\":"
                         + beard.permLevel + ",\"bankedLevel\":" + beard.bankedLevel
                         + ",\"active\":" + beard.active.ToString().ToLowerInvariant() + "}");
            }
            return "[" + string.Join(",", rows.ToArray()) + "]";
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
                PurchaseDescriptor speedDescriptor;
                PurchaseCostState speedState;
                string speedReason;
                if (!TryGetEnergySpeedPurchase(c, out speedDescriptor, out speedState,
                        out speedReason))
                    return new ResourceStatus
                    {
                        TargetName = "Energy Speed",
                        Decision = "Held because the build-pinned Energy Speed cost/effect state is unavailable",
                        Target = 0,
                        EtaSeconds = -1
                    };
                var speedCost = speedDescriptor.Cost.Evaluate(speedState);
                return new ResourceStatus
                {
                    TargetName = speedDescriptor.DisplayName,
                    Decision = speedCost <= c.realExp - Config.ExpReserve
                        ? "Buying one build-pinned exact Energy Speed atom now: " + speedReason
                        : "Saving EXP for the exact Energy Speed atom: " + speedReason,
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
            string magicRoiReason;
            if (MagicSpeedOutranksMarginalGrowth(c, out magicSpeedSteps, out magicRateAfter,
                    out magicRoiReason))
            {
                long atomCost;
                if (!TryReadPositiveIntField(c.magicPurchases, "magicSpeed10Cost", out atomCost)
                    || magicSpeedSteps <= 0 || atomCost > long.MaxValue / magicSpeedSteps)
                    return new ResourceStatus
                    {
                        TargetName = "Magic Speed discrete breakpoint",
                        Decision = "Held because the build-pinned Magic Speed cost is unavailable",
                        Target = 0,
                        EtaSeconds = -1
                    };
                var cost = atomCost * magicSpeedSteps;
                return new ResourceStatus
                {
                    TargetName = "Magic Speed " + c.magic.magicBarSpeed.ToString("0.0")
                                 + " -> " + (c.magic.magicBarSpeed + .1f * magicSpeedSteps).ToString("0.0"),
                    Decision = cost <= c.realExp - Config.ExpReserve
                        ? "Breakpoint bundle is fully funded; buying one exact +0.1 atom this root ("
                          + magicSpeedSteps + " atom(s) currently required): " + magicRoiReason
                        : "Saving the full " + magicSpeedSteps
                          + "-atom bundle before starting the discrete breakpoint: " + magicRoiReason,
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
            var spaceCritical = !IsApOwned(c, 15)
                                && AdventureCollectionPlanner.InventoryPressureCritical(c);
            var spaceNeeded = !IsApOwned(c, 15)
                              && AdventureCollectionPlanner.InventoryPressureHigh(c, _collectionTarget);
            var id = spaceCritical ? 15
                : !c.arbitrary.instaTrain ? 9
                : spaceNeeded ? 15
                : !c.arbitrary.hasStarterPack ? 16
                // The bot already performs filtering and merging.  The Heart is the
                // first post-starter AP purchase that creates new progression income
                // (+20% AP once MAXXED), whereas Loot Filter merely duplicates us.
                : !HasYellowHeartDropped(c) ? 14
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
                return label + " (permanent 500 EXP and 5 inventory spaces; consumables and weaker purchases would delay this multi-run bundle)";
            if (id == 14)
                return label + " (permanent +20% AP after MAXX; nominal AP-cost breakeven is 750,000 future AP after MAXX)";
            if (id == 15)
                return label + " (collection reserve is below projected merge/drop pressure; a full inventory destroys future drops)";
            return label + " (highest-ranked unlocked permanent upgrade after opportunity cost)";
        }

        private ResourceStatus GetGoldStatus(Character c)
        {
            var remaining = Math.Max(0.0,
                Plan.EffectiveAllocationTarget(c) - c.rebirthTime.totalseconds);
            var horizon = ResourceHorizonModel.EvaluateGold(c, (int)Math.Ceiling(remaining));
            var pitReady = c.settings.pitUnlocked
                           && c.pit.pitTime.totalseconds >= c.pitController.currentPitTime()
                           && c.pitController.canToss();
            var primary = horizon.Claims.Where(x => x.Amount > 0)
                .OrderBy(x => x.Kind).FirstOrDefault();
            var target = primary == null ? 0.0 : primary.Amount;
            var decision = horizon.Decision + "; projected baseline "
                           + horizon.BaselineAtRebirth.ToString("0") + " Gold versus "
                           + horizon.CommittedSpend.ToString("0") + " committed";
            if (pitReady)
            {
                var protectedSpend = horizon.ProtectedSpendBefore(GoldClaimKind.MoneyPitPermanentTier);
                var pitClaim = horizon.Claims.FirstOrDefault(x =>
                    x.Kind == GoldClaimKind.MoneyPitPermanentTier);
                decision = protectedSpend > 0
                    ? "Money Pit is ready but the joint Gold ledger protects "
                      + protectedSpend.ToString("0") + " for "
                      + string.Join(" + ", horizon.Claims.Where(x => x.Hard)
                          .Select(x => x.Label).ToArray())
                    : pitClaim == null
                        ? "Money Pit is ready, but no reachable permanent Pit tier owns this horizon"
                        : c.realGold < pitClaim.Amount
                            ? "Saving for " + pitClaim.Label + " at " + pitClaim.Amount.ToString("0")
                            : "Money Pit is ready and the shared Gold ledger admits the permanent tier toss";
                if (pitClaim != null && protectedSpend <= 0)
                {
                    target = pitClaim.Amount;
                    primary = pitClaim;
                }
            }
            return new ResourceStatus
            {
                State = horizon.TimeMachineUseful ? "working-capital"
                    : horizon.CommittedSpend > 0 ? "funded-horizon" : "no-profitable-sink",
                Decision = decision,
                TargetName = primary == null ? horizon.TargetName : primary.Label,
                Target = target,
                EtaSeconds = ResourceEta(c.realGold, target, _goldPerSecond)
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

        private static string ResourceAllocationSummaryJson(Character c)
        {
            // Reuse the same complete native target capture that settles Allocation mutations.
            // This is a read-only presentation projection: the dashboard receives grouped labels,
            // while the transaction layer continues to prove the full stable-key vector.
            var snapshot = LiveResourceAllocationProof.Capture(c, 1L, "dashboard-read-only");
            if (snapshot == null)
                return "{\"available\":false,\"energy\":null,\"magic\":null,\"resource3\":null}";
            return "{\"available\":true,\"energy\":"
                   + AllocationGroupsJson(snapshot.Energy)
                   + ",\"magic\":" + AllocationGroupsJson(snapshot.Magic)
                   + ",\"resource3\":" + AllocationGroupsJson(snapshot.Resource3) + "}";
        }

        private static string AllocationGroupsJson(ExactAllocationVector vector)
        {
            if (vector == null) return "null";
            var grouped = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var pair in vector.TargetsCopy())
            {
                if (pair.Value <= 0L) continue;
                var label = AllocationGroupLabel(pair.Key);
                long current;
                grouped.TryGetValue(label, out current);
                grouped[label] = current + pair.Value;
            }
            var rows = new List<string>();
            foreach (var pair in grouped.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
                rows.Add("{\"name\":\"" + EscapeJson(pair.Key) + "\",\"allocated\":"
                         + pair.Value + "}");
            return "{\"capacity\":" + vector.Capacity + ",\"idle\":" + vector.Idle
                   + ",\"conserved\":" + vector.IsConserved().ToString().ToLowerInvariant()
                   + ",\"groups\":[" + string.Join(",", rows.ToArray()) + "]}";
        }

        private static string AllocationGroupLabel(string key)
        {
            if (key.StartsWith("training.", StringComparison.Ordinal)) return "Basic Training";
            if (key.StartsWith("augment.", StringComparison.Ordinal)) return "Augments";
            if (key.StartsWith("advanced-training.", StringComparison.Ordinal)) return "Advanced Training";
            if (key.StartsWith("wandoos.", StringComparison.Ordinal)) return "Wandoos";
            if (key.StartsWith("time-machine.", StringComparison.Ordinal)) return "Time Machine";
            if (key.StartsWith("ngu.", StringComparison.Ordinal)) return "NGUs";
            if (key.StartsWith("blood.", StringComparison.Ordinal)) return "Blood Magic";
            if (key.StartsWith("hack.", StringComparison.Ordinal)) return "Hacks";
            if (key.StartsWith("wish.", StringComparison.Ordinal)) return "Wishes";
            return "Other systems";
        }

        private static long BasicTrainingEnergy(Character c)
        {
            long total = 0;
            for (var i = 0; i < 6; i++)
                total += Math.Max(0L, c.training.attackEnergy[i])
                         + Math.Max(0L, c.training.defenseEnergy[i]);
            return total;
        }

        private static long ProductiveBasicTrainingHeadroom(Character c)
        {
            decimal total = 0m;
            for (var i = 0; i < 6; i++)
            {
                var attackUnlocked = i == 0 || c.training.attackTraining[i - 1] > 5000L * i;
                var defenseUnlocked = i == 0 || c.training.defenseTraining[i - 1] > 5000L * i;
                if (attackUnlocked)
                    total += ExactResourceAllocator.ProductiveSpeedCapHeadroom(
                        c.training.attackCaps[i], c.training.attackEnergy[i], long.MaxValue);
                if (defenseUnlocked)
                    total += ExactResourceAllocator.ProductiveSpeedCapHeadroom(
                        c.training.defenseCaps[i], c.training.defenseEnergy[i], long.MaxValue);
                if (total >= long.MaxValue)
                    return long.MaxValue;
            }
            return (long)total;
        }

        internal static void GetNextTrainingGoal(Character c, out string goal, out int etaSeconds)
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
                // Spend the cheaper currency for the same permanent slot. AP space
                // costs at most 10,000 AP; if it is already funded, leave EXP for
                // compounding generation and let the AP transaction later in this
                // same automation cycle perform the native purchase.
                var apController = GetArbitraryController(c);
                var apSpaceCost = apController == null || IsApOwned(c, 15)
                    ? long.MaxValue : GetApCost(apController, 15);
                var apCanFundSpace = apSpaceCost > 0
                                     && apSpaceCost <= c.arbitrary.curArbitraryPoints - Config.ApReserve;
                if (apCanFundSpace)
                    return null;
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

            // Fight-Boss percentage stats are normally worse than resource growth. A one-atom
            // immediate win is still insufficient: it must be the next persistent record, have a
            // finite frozen-allocation rollout ETA, and beat the best permanent P/C/B atom on
            // per-EXP time value. This prevents repeatedly buying direct stats for an old Boss.
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
                    var chosenKill = -1.0;
                    string method = null;
                    string label = null;
                    Func<double> state = null;
                    if (attackWins && (!defenseWins || attackKill <= defenseKill))
                    {
                        chosenKill = attackKill;
                        method = "buyAttack10";
                        label = "Fight Boss Attack +10%";
                        state = () => c.attackBoost;
                    }
                    else if (defenseWins)
                    {
                        chosenKill = defenseKill;
                        method = "buyDefense10";
                        label = "Fight Boss Defense +10%";
                        state = () => c.defenseBoost;
                    }
                    if (method != null)
                    {
                        var activeHighest = c.settings.rebirthDifficulty == difficulty.normal
                            ? c.highestBoss : c.settings.rebirthDifficulty == difficulty.evil
                                ? c.highestHardBoss : c.highestSadisticBoss;
                        var rollout = RawSelectedBossDefeatEta(c, 604800);
                        var competing = BestMarginalExpCandidate(c);
                        double gateScore;
                        double permanentScore;
                        if (ExpPurchasePolicy.FightBossGateOutranksPermanent(
                                c.bossID == activeHighest, rollout, chosenKill, 30,
                                competing == null ? 0.0 : competing.NormalizedLevel,
                                competing == null ? 0.0 : competing.NormalizedStep,
                                competing == null ? 0L : competing.Cost,
                                out gateScore, out permanentScore))
                            return new PermanentExpTarget(c.statBoostPurchases, method, label, 30,
                                state, "one exact atom opens new-record Boss " + (c.bossID + 1)
                                       + " now instead of the source-modeled " + rollout
                                       + "s rollout; forward-gate ROI "
                                       + gateScore.ToString("0.000000") + "/EXP beats permanent growth "
                                       + permanentScore.ToString("0.000000") + "/EXP");
                    }
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
            string roiReason;
            if (!MagicSpeedOutranksMarginalGrowth(c, out steps, out projectedRate, out roiReason))
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
                  + c.magicPerSecond().ToString("0.###") + "/s [confirmed discrete rate breakpoint; "
                  + roiReason + "]"
                : "Magic Speed breakpoint purchase failed validation: spent=" + spent
                  + ", rate " + rateBefore.ToString("0.###") + " -> " + c.magicPerSecond().ToString("0.###"));
            return confirmed;
        }

        /*
        CROSS-RESOURCE EXP COMPARISON

        Magic Speed changes refill time; it does not increase progress after the Magic cap is full.
        Therefore its multi-run value is the discrete refill-rate gain multiplied by the fraction
        of a representative run spent filling Magic and by the share of progression currently
        supplied by persistent Magic systems. Compare that amortized logarithmic gain per EXP with
        the next balanced permanent P/C/B atom. This prevents an attractive-looking rate breakpoint
        from preempting an Energy cap/power gain that works for the entire current and future runs.
        */
        private static bool MagicSpeedOutranksMarginalGrowth(Character c, out int steps,
            out double projectedRate, out string reason)
        {
            reason = string.Empty;
            if (!TryGetMagicSpeedBreakpoint(c, out steps, out projectedRate))
                return false;
            var currentRate = Math.Max(1e-9, c.magicPerSecond());
            var horizon = 3600.0;
            if (Main.Autopilot != null && Main.Autopilot.Plan != null
                && Main.Autopilot.Plan.RebirthSeconds > 0)
                horizon = Math.Max(60.0, Main.Autopilot.Plan.EffectiveAllocationTarget(c)
                    - c.rebirthTime.totalseconds);
            var persistentMagicWeight = c.settings.rebirthDifficulty != difficulty.normal ? .50
                : c.settings.nguOn || c.settings.beardsOn ? 1.0 / 3.0
                : c.settings.yggdrasilOn ? .20
                : .10;
            var currentIntegral = IntegratedRefill(c.magic.curMagic, currentRate, horizon);
            var projectedIntegral = IntegratedRefill(c.magic.curMagic, projectedRate, horizon);
            var integratedGain = currentIntegral <= 0 ? 0.0
                : Math.Max(0.0, projectedIntegral / currentIntegral - 1.0);
            var magicScore = Math.Log(1.0 + integratedGain) * persistentMagicWeight
                             / Math.Max(1L, 3L * steps);

            var competing = BestMarginalExpCandidate(c);
            var competingScore = competing == null ? 0.0
                : Math.Log(1.0 + competing.NormalizedStep
                           / Math.Max(1e-9, competing.NormalizedLevel))
                  / Math.Max(1L, competing.Cost);
            reason = "Magic generation " + currentRate.ToString("0.###") + " -> "
                     + projectedRate.ToString("0.###") + "/s, integrated run throughput +"
                     + (integratedGain * 100.0).ToString("0.###") + "%, amortized ROI "
                     + magicScore.ToString("0.000000") + "/EXP versus "
                     + (competing == null ? "no P/C/B candidate" : competing.Label + " at "
                        + competingScore.ToString("0.000000") + "/EXP");
            return magicScore > competingScore;
        }

        private static double IntegratedRefill(double cap, double rate, double horizon)
        {
            if (cap <= 0 || rate <= 0 || horizon <= 0) return 0;
            var fillTime = cap / rate;
            if (horizon <= fillTime)
                return .5 * rate * horizon * horizon;
            return cap * horizon - .5 * cap * fillTime;
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
            var bars = Math.Max(1L, c.totalMagicBar());
            return ExpPurchasePolicy.TryMagicDiscreteBreakpoint(baseSpeed, totalSpeed,
                bars, currentRate, 10, out steps, out projectedRate);
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

            // Lost loot is irrecoverable. At critical capacity the cheapest native
            // AP slot preempts every other AP goal, even Insta Training. At normal
            // collection pressure, Insta Training keeps its permanent run-start
            // priority but capacity still beats the larger Starter/Heart reserves.
            var spaceCritical = !IsApOwned(c, 15)
                                && AdventureCollectionPlanner.InventoryPressureCritical(c);
            if (spaceCritical)
            {
                TryBuyApUpgrade(controller, 15, available, ApPurchaseMethods[15]);
                return;
            }

            // Preserve AP for the next high-impact permanent gate instead of draining it
            // into whatever cheap button happens to be affordable first.
            if (!c.arbitrary.instaTrain)
            {
                TryBuyApUpgrade(controller, 9, available, ApPurchaseMethods[9]);
                return;
            }
            if (!IsApOwned(c, 15)
                && AdventureCollectionPlanner.InventoryPressureHigh(c, _collectionTarget))
            {
                TryBuyApUpgrade(controller, 15, available, ApPurchaseMethods[15]);
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

        private static readonly string[] StrategicSavingsKeywords =
        {
            "fib", "spawn", "welcome", "bank", "slot"
        };

        private static int SelectAffordablePerkTarget(Character c, long spendable)
        {
            if (c == null || spendable <= 0L || c.adventureController == null
                || c.adventureController.itopod == null || c.adventure == null
                || c.adventure.itopod == null)
                return -1;
            var controller = c.adventureController.itopod;
            var levels = c.adventure.itopod.perkLevel;
            if (c.settings.rebirthDifficulty == difficulty.sadistic
                && !EndgameDependencyModel.IsOwned(c, 482)
                && levels.Count > 231 && levels[231] < 1)
            {
                if (!HasEmptyOrdinaryInventorySlot(c)) return -1;
                var terminal = controller.perkCost(231);
                return terminal > 0L && terminal <= spendable ? 231 : -1;
            }
            var early = EarlyNormalPerkTarget(c, controller, levels, spendable);
            if (early >= 0) return early;
            // After the audited early sequence, local name/effect weights are not a common
            // time-to-progression unit. Saving PP is safer than spending a permanent currency on
            // a plausible-looking tooltip. Later purchases remain held until ChoosePerk receives
            // admission-grade downstream-seconds candidates from the global quote producer.
            return -1;
        }

        private void SpendBestPerk()
        {
            var c = Main.Character;
            var controller = c.adventureController.itopod;
            var points = c.adventure.itopod.perkPoints;
            var spendable = points - Config.PPReserve;
            var best = -1;
            if (c.settings.rebirthDifficulty == difficulty.sadistic
                && !EndgameDependencyModel.IsOwned(c, 482)
                && c.adventure.itopod.perkLevel.Count > 231
                && c.adventure.itopod.perkLevel[231] < 1)
            {
                if (!HasEmptyOrdinaryInventorySlot(c))
                {
                    ExecutionSafety.ReportHold("end-perk-inventory",
                        "END perk 231 held until an ordinary inventory slot is empty");
                    return;
                }
                var terminalCost = controller.perkCost(231);
                if (terminalCost <= 0 || terminalCost > spendable) return;
                best = 231;
            }
            else
                best = EarlyNormalPerkTarget(c, controller, c.adventure.itopod.perkLevel, spendable);
            if (best == -1 && c.adventure.titan4Kills > 0)
                best = FindBestUpgrade(controller.perkName, c.adventure.itopod.perkLevel,
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
                }, id => PerkMarginalValue(c, controller, id));
            if (best < 0) return;
            var pointsBefore = c.adventure.itopod.perkPoints;
            var levelBefore = c.adventure.itopod.perkLevel[best];
            controller.tryLevelUp(best);
            var confirmed = c.adventure.itopod.perkPoints < pointsBefore
                            || c.adventure.itopod.perkLevel[best] > levelBefore;
            if (best == 231)
                confirmed = confirmed && EndgameDependencyModel.IsOwned(c, 482);
            Main.LogAction(confirmed ? "PURCHASE" : "REJECTED",
                confirmed
                    ? "Bought perk " + controller.perkName[best] + " [confirmed by PP/level delta]"
                    : "Perk purchase for " + controller.perkName[best] + " produced no state transition");
        }

        private static void NextStrategicPerkGate(Character c, out long cost,
            out double marginalLogGain)
        {
            cost = 0L;
            marginalLogGain = 0.0;
            if (c == null || c.adventureController == null
                || c.adventureController.itopod == null || c.adventure == null
                || c.adventure.itopod == null)
                return;
            var controller = c.adventureController.itopod;
            var levels = c.adventure.itopod.perkLevel;
            var id = -1;
            if (c.settings.rebirthDifficulty == difficulty.sadistic
                && !EndgameDependencyModel.IsOwned(c, 482)
                && levels.Count > 231 && levels[231] < 1)
                id = 231;
            else if (c.settings.rebirthDifficulty == difficulty.normal)
            {
                var ordered = new[]
                {
                    new[] {0, 1}, new[] {1, 1}, new[] {2, 1}, new[] {3, 1},
                    new[] {4, 1}, new[] {18, 2}
                };
                foreach (var target in ordered)
                    if (target[0] < levels.Count && levels[target[0]] < target[1])
                    {
                        id = target[0];
                        break;
                    }
                if (id < 0 && c.adventure.titan4Kills <= 0 && levels.Count > 8)
                    id = levels[6] <= levels[8] ? 6 : 8;
            }
            // Do not feed the ITOPOD frontier score a later perk value derived only from names or
            // serialized effect magnitudes. Ordinary ITOPOD PP/EXP/AP/boost rates remain live,
            // while the discrete next-perk bonus waits for a typed downstream-seconds quote.
            if (id < 0) return;
            cost = Math.Max(0L, controller.perkCost(id));
            marginalLogGain = Math.Max(0.0, PerkMarginalValue(c, controller, id));
        }

        private static int EarlyNormalPerkTarget(Character c, ItopodPerkController controller,
            IList<long> perkLevels, long budget)
        {
            if (c.settings.rebirthDifficulty != difficulty.normal)
                return -1;
            // Shipped perk IDs and Chapter 1 breakpoints: one level in each Newbie perk,
            // two Instant Advanced Training levels, then balance Generic Energy Power/Cap.
            // If the next ordered purchase is unaffordable, save PP instead of leaking it into a
            // cheaper low-value perk and delaying the progression breakpoint.
            var ordered = new[]
            {
                new[] {0, 1}, new[] {1, 1}, new[] {2, 1}, new[] {3, 1}, new[] {4, 1},
                new[] {18, 2}
            };
            foreach (var target in ordered)
            {
                var id = target[0];
                if (id >= perkLevels.Count || perkLevels[id] >= target[1])
                    continue;
                return controller.perkCost(id) > 0 && controller.perkCost(id) <= budget ? id : -2;
            }
            // T4 unlocks the full marginal-value policy, but it must not abandon a
            // half-finished early sequence and strand permanent breakpoint rewards.
            if (c.adventure.titan4Kills > 0) return -1;
            if (perkLevels.Count <= 8) return -1;
            var balanced = perkLevels[6] <= perkLevels[8] ? 6 : 8;
            return controller.perkCost(balanced) > 0 && controller.perkCost(balanced) <= budget
                ? balanced : -2;
        }

        private void SpendBestQuirk()
        {
            var c = Main.Character;
            var controller = c.beastQuestPerkController;
            var points = c.beastQuest.quirkPoints;
            var spendable = points - Config.QPReserve;
            var best = -1;
            if (c.settings.rebirthDifficulty == difficulty.sadistic
                && !EndgameDependencyModel.IsOwned(c, 486)
                && c.beastQuest.quirkLevel.Count > 176
                && c.beastQuest.quirkLevel[176] < 1)
            {
                if (!HasEmptyOrdinaryInventorySlot(c))
                {
                    ExecutionSafety.ReportHold("end-quirk-inventory",
                        "END quirk 176 held until an ordinary inventory slot is empty");
                    return;
                }
                var terminalCost = controller.quirkCost(176);
                if (terminalCost <= 0 || terminalCost > spendable) return;
                best = 176;
            }
            else best = FindBestUpgrade(controller.quirkName, c.beastQuest.quirkLevel,
                controller.maxLevel, controller.effectPerLevel, id => controller.quirkCost(id), points - Config.QPReserve,
                controller.quirkDifficultyReq, c.settings.rebirthDifficulty, id =>
                {
                    if (id < 0 || id >= controller.quirkType.Count) return false;
                    var type = controller.quirkType[id];
                    if (type == itopodPerk.Res3) return c.res3.res3On;
                    if (type == itopodPerk.Wishes) return c.wishes.wishesOn;
                    if (type == itopodPerk.Cards) return c.cards.cardsOn;
                    return true;
                }, id => QuirkMarginalValue(c, controller, id));
            if (best < 0) return;
            var pointsBefore = c.beastQuest.quirkPoints;
            var levelBefore = c.beastQuest.quirkLevel[best];
            controller.tryLevelUp(best);
            var confirmed = c.beastQuest.quirkPoints < pointsBefore || c.beastQuest.quirkLevel[best] > levelBefore;
            if (best == 176)
                confirmed = confirmed && EndgameDependencyModel.IsOwned(c, 486);
            Main.LogAction(confirmed ? "PURCHASE" : "REJECTED",
                confirmed
                    ? "Bought quirk " + controller.quirkName[best] + " [confirmed by QP/level delta]"
                    : "Quirk purchase for " + controller.quirkName[best] + " produced no state transition");
        }

        private static bool HasEmptyOrdinaryInventorySlot(Character c)
        {
            return c != null && c.inventory != null && c.inventory.inventory != null
                   && c.inventory.inventory.Any(x => x == null || x.id <= 0);
        }

        private static int FindBestUpgrade(IList<string> names, IList<long> levels, IList<long> caps, IList<float> effects,
            Func<int, long> cost, long budget, IList<difficulty> requirements, difficulty currentDifficulty,
            Func<int, bool> allowed, Func<int, double> nativeMarginal = null)
        {
            if (budget <= 0) return -1;
            // Do not leak points into a cheap marginal perk while already within
            // one additional current balance of a discrete unlock/slot breakpoint.
            for (var i = 0; i < names.Count && i < levels.Count && i < caps.Count
                            && i < requirements.Count; i++)
            {
                if (allowed != null && !allowed(i)) continue;
                var cap = caps[i] == 0 ? long.MaxValue : caps[i];
                if (levels[i] >= cap || requirements[i] > currentDifficulty) continue;
                var name = (names[i] ?? string.Empty).ToLowerInvariant();
                if (!StrategicSavingsKeywords.Any(name.Contains)) continue;
                var price = cost(i);
                if (price > budget && price - budget <= budget)
                    return -1;
            }
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
                var weight = UpgradeObjectiveWeight(name, currentDifficulty);
                var serializedEffect = Math.Abs((double)effects[i]);
                // Percentage-like native effects compound against the already-owned
                // levels, so value the next logarithmic multiplier rather than raw
                // effect/price. Zero-effect one-time unlocks receive explicit option
                // value instead of becoming permanently invisible to the optimizer.
                var marginal = nativeMarginal != null ? nativeMarginal(i)
                    : serializedEffect > 0.0
                    ? Math.Log(1.0 + serializedEffect * (levels[i] + 1.0))
                      - Math.Log(1.0 + serializedEffect * levels[i])
                    : StrategicSavingsKeywords.Any(name.Contains) ? 1.0 : 1e-6;
                var score = price / Math.Max(1e-12, weight * marginal);
                if (score >= bestScore) continue;
                bestScore = score;
                best = i;
            }
            return best;
        }

        private static double PerkMarginalValue(Character c, ItopodPerkController controller, int id)
        {
            if (id < 0 || id >= c.adventure.itopod.perkLevel.Count
                || id >= controller.effectPerLevel.Count || id >= controller.perkName.Count)
                return 0.0;
            var level = c.adventure.itopod.perkLevel[id];
            var effect = Math.Abs((double)controller.effectPerLevel[id]);
            var name = (controller.perkName[id] ?? string.Empty).ToLowerInvariant();
            if (StrategicSavingsKeywords.Any(name.Contains)) return 1.0;
            if (id == 109 || id == 110)
                return Math.Max(1e-6, WishManager.SecondsSavedByOneMinimumReducerLevel(
                    CountMinimumBoundWishLevels(c) > 0, CountMinimumBoundWishLevels(c)) / 3600.0);
            // Native respawn perk 93 subtracts level*effect from the cycle
            // multiplier. Its speed value increases toward the 80% cap, the
            // opposite of an ordinary diminishing positive multiplier.
            if (id == 93 && effect > 0.0)
            {
                var before = Math.Max(0.2, 1.0 - level * effect);
                var after = Math.Max(0.2, 1.0 - (level + 1.0) * effect);
                return after < before ? Math.Log(before / after) : 0.0;
            }
            return effect > 0.0
                ? Math.Log(1.0 + effect * (level + 1.0))
                  - Math.Log(1.0 + effect * level)
                : 1e-6;
        }

        private static double QuirkMarginalValue(Character c,
            BeastQuestPerkController controller, int id)
        {
            if (id < 0 || id >= c.beastQuest.quirkLevel.Count
                || id >= controller.effectPerLevel.Count || id >= controller.quirkName.Count)
                return 0.0;
            if (id == 54)
            {
                var affected = CountMinimumBoundWishLevels(c);
                return Math.Max(1e-6,
                    WishManager.SecondsSavedByOneMinimumReducerLevel(affected > 0, affected)
                    / 3600.0);
            }
            var level = c.beastQuest.quirkLevel[id];
            var effect = Math.Abs((double)controller.effectPerLevel[id]);
            var name = (controller.quirkName[id] ?? string.Empty).ToLowerInvariant();
            if (StrategicSavingsKeywords.Any(name.Contains)) return 1.0;
            return effect > 0.0
                ? Math.Log(1.0 + effect * (level + 1.0))
                  - Math.Log(1.0 + effect * level)
                : 1e-6;
        }

        private static int CountMinimumBoundWishLevels(Character c)
        {
            if (c == null || c.wishes == null || c.wishesController == null
                || c.wishes.wishes == null || c.wishesController.properties == null)
                return 0;
            var minimumRate = c.wishesController.minimumWishTime();
            var affected = 0;
            var count = Math.Min(c.wishes.wishes.Count, c.wishesController.properties.Count);
            for (var i = 0; i < count; i++)
            {
                if (c.wishesController.wishLocked(i)
                    || c.wishes.wishes[i].level >= c.wishesController.properties[i].maxLevel)
                    continue;
                var rate = c.wishesController.progressPerTickMax(i);
                if (rate > 0 && Math.Abs(rate - minimumRate) <= Math.Max(1e-12, minimumRate * 1e-5))
                    affected += (int)Math.Min(1000000L,
                        c.wishesController.properties[i].maxLevel - c.wishes.wishes[i].level);
            }
            return affected;
        }

        private static double UpgradeObjectiveWeight(string name, difficulty currentDifficulty)
        {
            if (StrategicSavingsKeywords.Any(name.Contains)) return 12.0;
            if (name.Contains("adventure")) return 9.0;
            if (name.Contains("hack") || name.Contains("wish"))
                return currentDifficulty >= difficulty.evil ? 9.0 : 5.0;
            if (name.Contains("ngu")) return 8.0;
            if (name.Contains("ygg") || name.Contains("fruit")) return 7.0;
            if (name.Contains("quest")) return 6.5;
            if (name.Contains("pp") || name.Contains("qp") || name.Contains("card")) return 6.0;
            if (UpgradeKeywords.Any(name.Contains)) return 5.0;
            return 1.0;
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
