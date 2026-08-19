/*
FILE PURPOSE

Purpose: WandoosRunManager owns the one-at-a-time, reset-local operating-system transition selected
by AutopilotPlan. It does not own installation disks, which are permanent Inventory consumables.

Mechanism: A caller-owned root transaction captures the exact OS, Energy/Magic levels and progress,
and allocated resources; invokes the build-pinned native selector/method adapter; and accepts the
irreversible switch only when the requested OS is installed and native changeOS cleared exactly the
four documented run-progress fields without changing either allocation.

Inputs and outputs: Inputs are synchronized Wandoos state and AutopilotPlan.WandoosOS. Output is one
typed Allocation mutation result plus Wandoos telemetry. No save, profile, or configuration is
written.

Invariants and safety: OS IDs are 0..2; MEH requires the Jake item set and XL requires XLLevels>0.
The adapter restores its ambient nextOS selector. A same-OS request is a no-op. Because switching
destroys accumulated run progress, it has no compensation and never runs outside a nonzero root.

Extension points and non-goals: The planner prices whether the future multiplier repays destroyed
progress before rebirth. This manager proves execution only; it does not allocate Energy/Magic or
override a user profile outside Autopilot ownership.
*/
using System;
using NGUInjector.Autopilot;

namespace NGUInjector.Managers
{
    internal static class WandoosRunManager
    {
        private sealed class WandoosRunState
        {
            internal int Os;
            internal long EnergyLevel;
            internal long MagicLevel;
            internal float EnergyProgress;
            internal float MagicProgress;
            internal long EnergyAllocated;
            internal long MagicAllocated;
        }

        internal static bool IsTargetUnlocked(int target, bool jakeComplete, long xlLevels)
        {
            return target >= 0 && target <= 2
                   && (target != 1 || jakeComplete)
                   && (target != 2 || xlLevels > 0L);
        }

        internal static void Manage(AutopilotPlan plan)
        {
            ExecutionSafety.ReportHold("wandoos-root-required",
                "Wandoos OS changes require the caller-owned nonzero root transaction.");
        }

        internal static MutationResult Manage(RootTransaction root, AutopilotPlan plan)
        {
            var c = Main.Character;
            if (root == null || root.IsClosed || plan == null || c == null
                || c.settings == null || !c.settings.wandoos98On
                || c.wandoos98 == null || c.wandoos98Controller == null
                || c.wandoos98.disabled)
                return null;
            var target = plan.WandoosOS;
            if (target == (int)c.wandoos98.os) return null;
            if (!IsTargetUnlocked(target, c.inventory.itemList.jakeComplete,
                    c.wandoos98.XLLevels))
            {
                ExecutionSafety.ReportHold("wandoos-target-locked",
                    "Planned Wandoos OS " + target + " is not unlocked in synchronized state.");
                return null;
            }
            return root.ExecuteChild(new WandoosOsIntent(c, target));
        }

        private sealed class WandoosOsIntent :
            IMutationIntent<WandoosRunState, NativeInvocationResult, WandoosRunState>
        {
            private readonly Character _character;
            private readonly int _target;

            internal WandoosOsIntent(Character character, int target)
            {
                _character = character;
                _target = target;
            }

            public string Id { get { return "wandoos.os." + _target; } }
            public MutationClass Class { get { return MutationClass.Allocation; } }
            public MutationRisk Risk { get { return MutationRisk.FiniteResource; } }
            public MutationOwner Owner { get { return MutationOwner.Autopilot; } }
            public string BindingId { get { return NativeBindingKeys.WandoosSetOs; } }
            public bool Required { get { return false; } }
            public bool CanCompensate { get { return false; } }
            public bool CreatesNewEpoch { get { return false; } }
            public SettlePolicy Settle { get { return SettlePolicy.Immediate(); } }

            public WandoosRunState CaptureBefore(MutationContext context) { return Capture(); }

            public PreconditionResult CheckPreconditions(MutationContext context,
                WandoosRunState before)
            {
                if (!Main.IsAutomationReady)
                    return PreconditionResult.Hold("gameplay synchronization is not current");
                if (before == null || before.Os == _target)
                    return before == null
                        ? PreconditionResult.Hold("Wandoos state is unavailable")
                        : PreconditionResult.AlreadySatisfied("requested Wandoos OS is active");
                var itemList = _character.inventory == null ? null : _character.inventory.itemList;
                if (itemList == null || !IsTargetUnlocked(_target, itemList.jakeComplete,
                        _character.wandoos98.XLLevels))
                    return PreconditionResult.Hold("requested Wandoos OS is not unlocked");
                return PreconditionResult.Ready();
            }

            public NativeInvocationResult Apply(MutationContext context,
                RootTransactionToken token, WandoosRunState before)
            {
                var native = NativeBindingRegistry.Create(typeof(Character).Assembly,
                    Main.GameAssemblySha256).CreateMutationAdapters();
                return native.SwitchWandoosOperatingSystem(
                    _character.wandoos98Controller, _target);
            }

            public VerificationResult<WandoosRunState> Verify(MutationContext context,
                WandoosRunState before,
                MutationApplyObservation<NativeInvocationResult> apply)
            {
                var after = Capture();
                var invoked = apply.ReturnedNormally && apply.Value != null
                              && apply.Value.ReturnedNormally;
                var valid = invoked && after != null && after.Os == _target
                            && after.EnergyLevel == 0L && after.MagicLevel == 0L
                            && after.EnergyProgress == 0f && after.MagicProgress == 0f
                            && after.EnergyAllocated == before.EnergyAllocated
                            && after.MagicAllocated == before.MagicAllocated;
                if (!valid)
                    return VerificationResult<WandoosRunState>.Failed(
                        "Wandoos OS switch lacked exact target/reset/allocation postconditions");
                Main.LogAction("WANDOOS", "Changed OS " + before.Os + " -> " + _target
                    + " [confirmed zero Energy/Magic levels and progress; allocations preserved]");
                return VerificationResult<WandoosRunState>.Satisfied(after,
                    "exact Wandoos OS/reset transition confirmed");
            }

            public CompensationResult Compensate(MutationContext context, RecoveryToken token,
                WandoosRunState before, MutationApplyObservation<NativeInvocationResult> apply)
            {
                return CompensationResult.NotSupported(
                    "a second OS switch cannot restore destroyed run progress");
            }

            public bool BeforeStateMatches(WandoosRunState expected, WandoosRunState observed)
            {
                return Same(expected, observed);
            }

            public string FingerprintBefore(WandoosRunState state) { return Fingerprint(state); }
            public string FingerprintAfter(WandoosRunState state) { return Fingerprint(state); }

            private WandoosRunState Capture()
            {
                var w = _character == null ? null : _character.wandoos98;
                if (w == null) return null;
                return new WandoosRunState
                {
                    Os = (int)w.os,
                    EnergyLevel = w.energyLevel,
                    MagicLevel = w.magicLevel,
                    EnergyProgress = w.energyProgress,
                    MagicProgress = w.magicProgress,
                    EnergyAllocated = w.wandoosEnergy,
                    MagicAllocated = w.wandoosMagic
                };
            }

            private static bool Same(WandoosRunState a, WandoosRunState b)
            {
                return a != null && b != null && a.Os == b.Os
                       && a.EnergyLevel == b.EnergyLevel && a.MagicLevel == b.MagicLevel
                       && a.EnergyProgress == b.EnergyProgress
                       && a.MagicProgress == b.MagicProgress
                       && a.EnergyAllocated == b.EnergyAllocated
                       && a.MagicAllocated == b.MagicAllocated;
            }

            private static string Fingerprint(WandoosRunState state)
            {
                return state == null ? "missing" : state.Os + ":" + state.EnergyLevel + ":"
                    + state.MagicLevel + ":" + state.EnergyProgress.ToString("R") + ":"
                    + state.MagicProgress.ToString("R") + ":" + state.EnergyAllocated + ":"
                    + state.MagicAllocated;
            }
        }
    }
}
