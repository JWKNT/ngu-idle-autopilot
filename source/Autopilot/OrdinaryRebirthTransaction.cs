using System;
using System.Globalization;
using NGUInjector.AllocationProfiles.RebirthStuff;
using NGUInjector.Managers;

/*
FILE PURPOSE

Purpose: Execute an optimizer-authorized ordinary rebirth as the final typed child of the live
one-second mutation root. This file turns the existing exact rebirth model and reset postconditions
into an executable transaction without granting challenge or difficulty authority.

Mechanism: A pure admission gate first refuses work that is not due. A build-pinned preview child
then invokes calculateTimeMulti and calculateNextMultis in native order and proves every formula
input remained stable. The reset child recaptures the live state, repeats the complete policy gate,
invokes ordinary Rebirth.engage, verifies the exact +1/timer/Number/Boss/Titan transform, and closes
the old game epoch synchronously.

Inputs and outputs: Inputs are the current Character, immutable Autopilot plan/config, and the
caller-owned RootTransaction. Outputs are a typed OrdinaryRebirthExecutionOutcome and the two root
journal entries when a due reset is attempted.

Invariants and safety: A future/held/non-positive reset creates no child and cannot dirty the root.
Challenge selection, difficulty changes, harvests, Blood spending, Titan fights, and loadout changes
are never hidden inside this transaction. Unknown bindings, stale preview state, a native no-op, or
a partial reset fail closed; partial reset state quarantines the game epoch.

Extension points and non-goals: Typed pre-rebirth Blood/Ygg/Titan actions can become earlier sibling
intents later. Until then their live pending state is an explicit reset blocker. This file does not
plan reset times, enter challenges, or mutate allocation profiles.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class OrdinaryRebirthGateInput
    {
        internal bool Authority;
        internal bool FullMode;
        internal bool GameplaySynchronized;
        internal bool PlanPresent;
        internal bool PlanExecutionHold;
        internal double TargetSeconds;
        internal double ElapsedSeconds;
        internal double MinimumSeconds;
        internal int BossId;
        internal bool BossFight;
        internal bool BossNuke;
        internal bool NoRebirthChallenge;
        internal bool DifficultySelectorClear;
        internal bool TitanBoundaryClear;
        internal bool HarvestBoundaryClear;
        internal bool BloodBoundaryClear;
        internal bool GrbWindowClear;
        internal bool ImminentBossClear;
        internal bool RequirePreview;
        internal bool PreviewValid;
        internal bool PolicyAuthorized;
        internal string PolicyReason = string.Empty;
    }

    internal sealed class OrdinaryRebirthGateResult
    {
        internal bool Ready;
        internal string Reason = string.Empty;
    }

    internal static class OrdinaryRebirthGate
    {
        internal static OrdinaryRebirthGateResult Evaluate(OrdinaryRebirthGateInput input)
        {
            if (input == null) return Hold("ordinary rebirth gate input is missing");
            if (!input.Authority) return Hold("ordinary rebirth authority is disabled");
            if (!input.FullMode) return Hold("ordinary rebirth requires full mode");
            if (!input.GameplaySynchronized) return Hold("gameplay synchronization is not current");
            if (!input.PlanPresent) return Hold("the optimizer plan is missing");
            if (input.PlanExecutionHold) return Hold("the optimizer selected continuation/hold");
            if (!FiniteNonNegative(input.TargetSeconds)
                || !FiniteNonNegative(input.ElapsedSeconds)
                || !FiniteNonNegative(input.MinimumSeconds))
                return Hold("rebirth target/timer/minimum is not finite nonnegative state");
            if (input.ElapsedSeconds < input.MinimumSeconds)
                return Hold("native minimum rebirth time is not met");
            if (input.ElapsedSeconds < input.TargetSeconds)
                return Hold("the selected optimizer checkpoint is not due");
            // Native bossID is zero-based: 0 is the playable Boss 1, not an unselected sentinel.
            if (input.BossId < 0) return Hold("current Fight Boss selection is invalid");
            if (input.BossFight || input.BossNuke)
                return Hold("Fight Boss is active at the reset boundary");
            if (input.NoRebirthChallenge)
                return Hold("No Rebirth Challenge forbids an ordinary reset");
            if (!input.DifficultySelectorClear)
                return Hold("a pending difficulty selector cannot be consumed by ordinary rebirth");
            if (!input.TitanBoundaryClear)
                return Hold("a ready/active Titan boundary must be resolved before reset");
            if (!input.HarvestBoundaryClear)
                return Hold("a mature fruit must be handled by its typed transaction before reset");
            if (!input.BloodBoundaryClear)
                return Hold("valued Blood remains uncommitted at the reset boundary");
            if (!input.GrbWindowClear)
                return Hold("the first GRB/3,600-second window is not yet complete");
            if (!input.ImminentBossClear)
                return Hold("a projected Fight Boss kill is within two seconds");
            if (input.RequirePreview && !input.PreviewValid)
                return Hold("the build-pinned native rebirth preview is invalid or stale");
            if (input.RequirePreview && !input.PolicyAuthorized)
                return Hold(string.IsNullOrEmpty(input.PolicyReason)
                    ? "the final mutation policy rejected this reset" : input.PolicyReason);
            return new OrdinaryRebirthGateResult
            {
                Ready = true,
                Reason = input.RequirePreview
                    ? "ordinary rebirth has exact final admission"
                    : "ordinary rebirth is due for preview synchronization"
            };
        }

        private static OrdinaryRebirthGateResult Hold(string reason)
        {
            return new OrdinaryRebirthGateResult {Ready = false, Reason = reason};
        }

        private static bool FiniteNonNegative(double value)
        {
            return value >= 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    internal sealed class OrdinaryRebirthExecutionOutcome
    {
        internal bool Attempted;
        internal bool Committed;
        internal bool Failed;
        internal string Reason = string.Empty;
    }

    internal static class OrdinaryRebirthTransaction
    {
        internal static OrdinaryRebirthExecutionOutcome Execute(RootTransaction root,
            Character character, AutopilotPlan plan, AutopilotConfig config)
        {
            var initial = EvaluateLive(character, plan, config, false);
            if (!initial.Ready)
                return new OrdinaryRebirthExecutionOutcome {Reason = initial.Reason};
            if (root == null || root.IsClosed)
                return Failed("ordinary rebirth root is unavailable");

            var preview = root.ExecuteChild(new RebirthPreviewIntent(character));
            if (preview.Kind != MutationResultKind.Committed
                && preview.Kind != MutationResultKind.NoOpVerified)
                return Failed("rebirth preview transaction did not commit: " + preview.Reason);

            var final = EvaluateLive(character, plan, config, true);
            if (!final.Ready)
            {
                ExecutionSafety.ReportHold("ordinary-rebirth-final-preflight", final.Reason);
                return new OrdinaryRebirthExecutionOutcome
                {
                    Attempted = true,
                    Reason = final.Reason
                };
            }

            var reset = root.ExecuteChild(new OrdinaryResetIntent(character, plan));
            var committed = reset.Kind == MutationResultKind.Committed
                            || reset.Kind == MutationResultKind.CommittedWithException;
            return new OrdinaryRebirthExecutionOutcome
            {
                Attempted = true,
                Committed = committed,
                Failed = !committed,
                Reason = reset.Reason
            };
        }

        private static OrdinaryRebirthExecutionOutcome Failed(string reason)
        {
            return new OrdinaryRebirthExecutionOutcome
            {
                Attempted = true,
                Failed = true,
                Reason = reason ?? string.Empty
            };
        }

        internal static OrdinaryRebirthGateResult EvaluateLive(Character c,
            AutopilotPlan plan, AutopilotConfig config, bool requirePreview)
        {
            var input = new OrdinaryRebirthGateInput
            {
                Authority = config != null && config.AllowRebirths,
                FullMode = config != null && config.IsFull,
                GameplaySynchronized = Main.IsAutomationReady
                                         && GameEpochController.Shared.MutationOpen,
                PlanPresent = plan != null,
                PlanExecutionHold = plan == null || plan.RebirthExecutionHold,
                TargetSeconds = plan == null ? double.NaN : plan.RebirthSeconds,
                ElapsedSeconds = c == null || c.rebirthTime == null
                    ? double.NaN : c.rebirthTime.totalseconds,
                MinimumSeconds = MinimumSeconds(c),
                BossId = c == null ? -1 : c.bossID,
                BossFight = c == null || c.bossController == null
                            || c.bossController.isFighting,
                BossNuke = c == null || c.bossController == null
                           || c.bossController.nukeBoss,
                NoRebirthChallenge = c == null || c.challenges == null
                                     || c.challenges.noRebirthChallenge.inChallenge,
                DifficultySelectorClear = c != null && c.settings != null
                                          && c.settings.rebirthDifficulty
                                          == c.nextRebirthDifficulty,
                TitanBoundaryClear = TitanBoundaryClear(c),
                HarvestBoundaryClear = HarvestBoundaryClear(c, config),
                BloodBoundaryClear = BloodBoundaryClear(c, config),
                GrbWindowClear = GrbWindowClear(c, plan),
                ImminentBossClear = ImminentBossClear(c),
                RequirePreview = requirePreview,
                PreviewValid = !requirePreview,
                PolicyAuthorized = !requirePreview
            };
            if (requirePreview && c != null && plan != null)
                CompletePreviewPolicy(c, plan, input);
            return OrdinaryRebirthGate.Evaluate(input);
        }

        private static void CompletePreviewPolicy(Character c, AutopilotPlan plan,
            OrdinaryRebirthGateInput input)
        {
            var elapsed = (int)Math.Floor(c.rebirthTime.totalseconds);
            var expectedTime = RebirthTransitionKernel.ExactTimeMultiplier(
                c.rebirthTime.totalseconds);
            var finitePreview = FinitePositive(c.nextAttackMulti)
                                && FinitePositive(c.nextDefenseMulti)
                                && NearlyEqual(c.timeMulti, expectedTime);
            var score = plan.RebirthTargetLocked
                ? double.MaxValue : plan.RebirthSelectedScorePerHour;
            var recoveryMode = plan.RebirthRecoveryMode;
            var resetRouteEtaSeconds = plan.RebirthRecoveryEtaSeconds;
            var continueRouteEtaSeconds = -1;
            var liveDue = true;
            if (!plan.RebirthTargetLocked)
            {
                var earlyNormal = c.settings.rebirthDifficulty == difficulty.normal
                                  && !(c.inventory.itemList.numberComplete || c.settings.nguOn);
                if (earlyNormal)
                {
                    var live = RebirthOptimizer.EarlyNormal(c);
                    liveDue = !live.ExecutionHold && live.TargetSeconds <= elapsed;
                    score = live.SelectedScorePerHour;
                    recoveryMode = live.RecoveryMode;
                    resetRouteEtaSeconds = live.RecoveryEtaSeconds;
                }
                else
                {
                    var live = StrategyCheckpointPlanner.Select(c, plan.RebirthSeconds,
                        plan.RebirthReason);
                    liveDue = !live.ExecutionHold && live.TargetSeconds <= elapsed;
                    score = live.SelectedScorePerHour;
                }
            }
            var ratio = Math.Min(c.attackMulti > 0.0
                    ? c.nextAttackMulti / c.attackMulti : 0.0,
                c.defenseMulti > 0.0
                    ? c.nextDefenseMulti / c.defenseMulti : 0.0);
            var decision = RebirthOptimizer.EvaluateMutationPolicy(score,
                finitePreview && liveDue, ratio, recoveryMode, resetRouteEtaSeconds,
                continueRouteEtaSeconds);
            input.PreviewValid = finitePreview && liveDue;
            input.PolicyAuthorized = decision.Authorized;
            input.PolicyReason = decision.Reason;
        }

        private static double MinimumSeconds(Character c)
        {
            try
            {
                return c == null || c.rebirth == null
                    ? double.NaN : c.rebirth.minRebirthTime();
            }
            catch { return double.NaN; }
        }

        private static bool TitanBoundaryClear(Character c)
        {
            try
            {
                return c != null && ZoneHelpers.HighestAvailableTitan() < 0
                       && !(ZoneHelpers.ZoneIsTitan(c.adventure.zone)
                            && c.adventureController != null
                            && (c.adventureController.currentEnemy != null
                                || c.adventureController.fightInProgress));
            }
            catch { return false; }
        }

        private static bool HarvestBoundaryClear(Character c, AutopilotConfig config)
        {
            if (c == null || config == null) return false;
            if (!config.ManageYggdrasil) return true;
            try { return !YggdrasilManager.AnyHarvestable(); }
            catch { return false; }
        }

        private static bool BloodBoundaryClear(Character c, AutopilotConfig config)
        {
            return c != null && config != null
                   && (!config.ManageBloodMagic || c.bloodMagic != null
                       && c.bloodMagic.bloodPoints <= 0.0);
        }

        private static bool GrbWindowClear(Character c, AutopilotPlan plan)
        {
            return c != null && plan != null
                   && !(c.settings.rebirthDifficulty == difficulty.normal
                        && c.highestBoss >= 58 && plan.RebirthSeconds < 3600
                        && c.rebirthTime.totalseconds < 3600);
        }

        private static bool ImminentBossClear(Character c)
        {
            if (c == null) return false;
            if (c.settings.rebirthDifficulty != difficulty.normal) return true;
            var eta = AutopilotManager.SelectedBossDefeatEta(c, 5);
            return eta < 0 || eta > 2;
        }

        private static bool FinitePositive(double value)
        {
            return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool NearlyEqual(double left, double right)
        {
            if (double.IsNaN(left) || double.IsNaN(right)
                || double.IsInfinity(left) || double.IsInfinity(right)) return false;
            return Math.Abs(left - right) <= Math.Max(1e-12,
                Math.Max(Math.Abs(left), Math.Abs(right)) * 2e-7);
        }
    }

    internal sealed class RebirthPreviewState
    {
        internal long RebirthNumber;
        internal double ElapsedSeconds;
        internal double BloodPower;
        internal double BossMultiplier;
        internal double OldBossMultiplier;
        internal double OldTimeMultiplier;
        internal long AttackLevels;
        internal double TimeMultiplier;
        internal double NextAttack;
        internal double NextDefense;

        internal static RebirthPreviewState Capture(Character c)
        {
            if (c == null || c.rebirthTime == null || c.bloodMagic == null
                || c.training == null) throw new InvalidOperationException(
                    "rebirth preview state is incomplete");
            return new RebirthPreviewState
            {
                RebirthNumber = c.stats == null ? -1L : c.stats.rebirthNumber,
                ElapsedSeconds = c.rebirthTime.totalseconds,
                BloodPower = c.bloodMagic.rebirthPower,
                BossMultiplier = c.bossMulti,
                OldBossMultiplier = c.oldBossMulti,
                OldTimeMultiplier = c.oldTimeMulti,
                AttackLevels = c.training.totalAttackLevels,
                TimeMultiplier = c.timeMulti,
                NextAttack = c.nextAttackMulti,
                NextDefense = c.nextDefenseMulti
            };
        }

        internal string Fingerprint
        {
            get
            {
                var inv = CultureInfo.InvariantCulture;
                return RebirthNumber + "|" + ElapsedSeconds.ToString("R", inv) + "|"
                       + BloodPower.ToString("R", inv) + "|"
                       + BossMultiplier.ToString("R", inv) + "|"
                       + OldBossMultiplier.ToString("R", inv) + "|"
                       + OldTimeMultiplier.ToString("R", inv) + "|" + AttackLevels + "|"
                       + TimeMultiplier.ToString("R", inv) + "|"
                       + NextAttack.ToString("R", inv) + "|"
                       + NextDefense.ToString("R", inv);
            }
        }
    }

    internal sealed class RebirthPreviewApply
    {
        internal NativeInvocationResult Time;
        internal NativeInvocationResult Preview;
    }

    internal sealed class RebirthPreviewIntent :
        IMutationIntent<RebirthPreviewState, RebirthPreviewApply, RebirthPreviewState>
    {
        private readonly Character _character;

        internal RebirthPreviewIntent(Character character) { _character = character; }

        public string Id { get { return "ordinary-rebirth-preview"; } }
        public MutationClass Class { get { return MutationClass.Rebirth; } }
        public MutationRisk Risk { get { return MutationRisk.Irreversible; } }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId { get { return "Rebirth.calculateTimeMulti+calculateNextMultis"; } }
        public bool Required { get { return true; } }
        public bool CanCompensate { get { return false; } }
        public bool CreatesNewEpoch { get { return false; } }
        public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

        public RebirthPreviewState CaptureBefore(MutationContext context)
        {
            return RebirthPreviewState.Capture(_character);
        }

        public PreconditionResult CheckPreconditions(MutationContext context,
            RebirthPreviewState before)
        {
            return _character == null || _character.rebirth == null
                ? PreconditionResult.Hold("native Rebirth controller is unavailable")
                : PreconditionResult.Ready();
        }

        public RebirthPreviewApply Apply(MutationContext context, RootTransactionToken token,
            RebirthPreviewState before)
        {
            var native = NativeBindingRegistry.Create(typeof(Character).Assembly,
                Main.GameAssemblySha256).CreateMutationAdapters();
            var time = native.RefreshRebirthTimeMultiplier(_character.rebirth);
            var preview = time.ReturnedNormally
                ? native.RefreshRebirthPreview(_character.rebirth) : null;
            return new RebirthPreviewApply {Time = time, Preview = preview};
        }

        public VerificationResult<RebirthPreviewState> Verify(MutationContext context,
            RebirthPreviewState before, MutationApplyObservation<RebirthPreviewApply> apply)
        {
            var after = RebirthPreviewState.Capture(_character);
            var calls = apply.ReturnedNormally && apply.Value != null
                        && apply.Value.Time != null && apply.Value.Time.ReturnedNormally
                        && apply.Value.Preview != null && apply.Value.Preview.ReturnedNormally;
            var stable = before.RebirthNumber == after.RebirthNumber
                         && before.ElapsedSeconds == after.ElapsedSeconds
                         && before.BloodPower == after.BloodPower
                         && before.BossMultiplier == after.BossMultiplier
                         && before.OldBossMultiplier == after.OldBossMultiplier
                         && before.OldTimeMultiplier == after.OldTimeMultiplier
                         && before.AttackLevels == after.AttackLevels;
            var expected = RebirthTransitionKernel.ExactTimeMultiplier(before.ElapsedSeconds);
            var valid = calls && stable
                        && Math.Abs(after.TimeMultiplier - expected) <= Math.Max(1e-12,
                            Math.Max(Math.Abs(after.TimeMultiplier), Math.Abs(expected)) * 2e-7)
                        && after.NextAttack > 0.0 && after.NextDefense > 0.0
                        && !double.IsNaN(after.NextAttack) && !double.IsNaN(after.NextDefense)
                        && !double.IsInfinity(after.NextAttack)
                        && !double.IsInfinity(after.NextDefense);
            return valid
                ? VerificationResult<RebirthPreviewState>.Satisfied(after,
                    "build-pinned rebirth preview synchronized")
                : VerificationResult<RebirthPreviewState>.Failed(
                    "rebirth preview call order/input/postcondition proof failed");
        }

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            RebirthPreviewState before, MutationApplyObservation<RebirthPreviewApply> apply)
        {
            return CompensationResult.NotSupported("derived preview failure is fail-closed");
        }

        public bool BeforeStateMatches(RebirthPreviewState expected,
            RebirthPreviewState observed)
        {
            return expected != null && observed != null
                   && expected.Fingerprint == observed.Fingerprint;
        }

        public string FingerprintBefore(RebirthPreviewState before)
        {
            return before == null ? string.Empty : before.Fingerprint;
        }

        public string FingerprintAfter(RebirthPreviewState after)
        {
            return after == null ? string.Empty : after.Fingerprint;
        }
    }

    internal sealed class OrdinaryResetIntent :
        IMutationIntent<ResetExecutionSnapshot, NativeInvocationResult, ResetExecutionSnapshot>
    {
        private readonly Character _character;
        private readonly AutopilotPlan _plan;

        internal OrdinaryResetIntent(Character character, AutopilotPlan plan)
        {
            _character = character;
            _plan = plan;
        }

        public string Id { get { return "ordinary-rebirth-reset"; } }
        public MutationClass Class { get { return MutationClass.Rebirth; } }
        public MutationRisk Risk { get { return MutationRisk.Irreversible; } }
        public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
        public string BindingId { get { return "Rebirth.engage()"; } }
        public bool Required { get { return true; } }
        public bool CanCompensate { get { return false; } }
        public bool CreatesNewEpoch { get { return true; } }
        public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

        public ResetExecutionSnapshot CaptureBefore(MutationContext context)
        {
            return LiveResetSnapshot.Capture(_character);
        }

        public PreconditionResult CheckPreconditions(MutationContext context,
            ResetExecutionSnapshot before)
        {
            var gate = OrdinaryRebirthTransaction.EvaluateLive(_character, _plan,
                Main.Autopilot == null ? null : Main.Autopilot.Config, true);
            return gate.Ready ? PreconditionResult.Ready()
                : PreconditionResult.Hold(gate.Reason);
        }

        public NativeInvocationResult Apply(MutationContext context, RootTransactionToken token,
            ResetExecutionSnapshot before)
        {
            return NativeBindingRegistry.Create(typeof(Character).Assembly,
                Main.GameAssemblySha256).CreateMutationAdapters()
                .InvokeOrdinaryRebirth(_character.rebirth);
        }

        public VerificationResult<ResetExecutionSnapshot> Verify(MutationContext context,
            ResetExecutionSnapshot before,
            MutationApplyObservation<NativeInvocationResult> apply)
        {
            var after = LiveResetSnapshot.Capture(_character);
            var proof = ResetPostconditions.VerifyOrdinary(before, after);
            var nativeReason = apply.ReturnedNormally && apply.Value != null
                ? apply.Value.Reason : "native invocation threw";
            Main.LogDiagnostic("Typed ordinary reset audit: " + proof.Reason
                               + "; native=" + nativeReason);
            if (proof.Satisfied)
            {
                ResetEpochTransition.Close(_character, after, "Normal rebirth confirmed");
                Main.LogAction("REBIRTH", "Normal rebirth confirmed [typed exact postcondition]");
                return VerificationResult<ResetExecutionSnapshot>.Satisfied(after,
                    "exact ordinary rebirth postcondition");
            }
            if (!ResetPostconditions.ExactStateMatches(before, after))
                ResetEpochTransition.Quarantine("ordinary rebirth produced a partial/wrong poststate: "
                                                + proof.Reason);
            return VerificationResult<ResetExecutionSnapshot>.Failed(proof.Reason);
        }

        public CompensationResult Compensate(MutationContext context, RecoveryToken token,
            ResetExecutionSnapshot before,
            MutationApplyObservation<NativeInvocationResult> apply)
        {
            return CompensationResult.NotSupported("an ordinary rebirth is irreversible");
        }

        public bool BeforeStateMatches(ResetExecutionSnapshot expected,
            ResetExecutionSnapshot observed)
        {
            return ResetPostconditions.ExactStateMatches(expected, observed);
        }

        public string FingerprintBefore(ResetExecutionSnapshot before)
        {
            return before == null ? string.Empty : before.ExactFingerprint;
        }

        public string FingerprintAfter(ResetExecutionSnapshot after)
        {
            return after == null ? string.Empty : after.ExactFingerprint;
        }
    }
}
