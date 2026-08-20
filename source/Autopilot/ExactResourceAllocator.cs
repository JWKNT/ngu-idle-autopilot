using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/*
FILE PURPOSE

ExactResourceAllocator is the pure numerical boundary between strategic allocation plans and
NGU Idle's Int64 resource controllers. It provides overflow-safe percentage ceilings, exact
decimal input text, conservation proofs, native 20 ms event timing, aggregate-group budgets,
joint three-resource Wish headroom, and an explicit mode-before-snapshot execution order.

The class does not read Character state and never calls a game controller. Callers snapshot live
state, use these functions to construct exact long-valued deltas, then commit through the root
mutation coordinator and verify the observed native deltas. A failed numeric/conservation/order
check must hold allocation rather than falling back to float. Global terminal-value weighting and
irreversible purchases are intentionally outside this file.
*/
namespace NGUInjector.Autopilot
{
    internal enum ExactResourceKind
    {
        Energy = 0,
        Magic = 1,
        Resource3 = 2
    }

    internal enum NoCurrencyFallbackKind
    {
        None = 0,
        Ngu = 1,
        Wandoos = 2
    }

    /*
    SEALED FULL-TARGET SETTLEMENT

    An idle-pool bound is not an allocation proof: Energy silently moved from Basic Training to an
    Augment still leaves the same capacity and idle values.  ExactAllocationVector therefore seals
    every installed native target by stable key.  A successful sweep conserves each resource and a
    later capture must equal the target vector observed synchronously when Apply returned.  Dynamic
    target counts are permitted, but the key set cannot change across one settlement boundary.

    The vector contains no Character/controller references, so partial-settlement recovery remains
    a live-adapter responsibility.  This layer only decides whether a captured restore or requested
    layout is mathematically exact; it never treats an aggregate idle delta as sufficient evidence.
    */
    internal sealed class ExactAllocationVector
    {
        private readonly SortedDictionary<string, long> _targets;

        internal readonly ExactResourceKind Resource;
        internal readonly long Capacity;
        internal readonly long Idle;

        internal ExactAllocationVector(ExactResourceKind resource, long capacity, long idle,
            IDictionary<string, long> targets)
        {
            Resource = resource;
            Capacity = capacity;
            Idle = idle;
            _targets = new SortedDictionary<string, long>(StringComparer.Ordinal);
            if (targets == null) return;
            foreach (var pair in targets)
            {
                if (string.IsNullOrEmpty(pair.Key))
                    throw new ArgumentException("Allocation target key is empty.", "targets");
                if (_targets.ContainsKey(pair.Key))
                    throw new ArgumentException("Duplicate allocation target key: " + pair.Key,
                        "targets");
                _targets.Add(pair.Key, pair.Value);
            }
        }

        internal int TargetCount { get { return _targets.Count; } }

        internal bool TryGet(string key, out long value)
        {
            return _targets.TryGetValue(key ?? string.Empty, out value);
        }

        internal string[] Keys()
        {
            var result = new string[_targets.Count];
            _targets.Keys.CopyTo(result, 0);
            return result;
        }

        internal IDictionary<string, long> TargetsCopy()
        {
            return new SortedDictionary<string, long>(_targets, StringComparer.Ordinal);
        }

        internal bool IsConserved()
        {
            return ExactResourceAllocator.Conserves(Capacity, Idle, _targets.Values);
        }

        internal bool HasSameSchema(ExactAllocationVector other)
        {
            if (other == null || Resource != other.Resource || _targets.Count != other._targets.Count)
                return false;
            using (var left = _targets.Keys.GetEnumerator())
            using (var right = other._targets.Keys.GetEnumerator())
            {
                while (left.MoveNext())
                {
                    if (!right.MoveNext() || !string.Equals(left.Current, right.Current,
                            StringComparison.Ordinal))
                        return false;
                }
                return !right.MoveNext();
            }
        }

        internal bool ExactEquals(ExactAllocationVector other)
        {
            if (other == null || Capacity != other.Capacity || Idle != other.Idle
                || !HasSameSchema(other)) return false;
            foreach (var pair in _targets)
            {
                long observed;
                if (!other._targets.TryGetValue(pair.Key, out observed) || observed != pair.Value)
                    return false;
            }
            return true;
        }

        internal long Allocated
        {
            get { return Capacity < Idle ? -1L : Capacity - Idle; }
        }

        internal string Fingerprint()
        {
            var text = Resource + ":" + Idle + "/" + Capacity;
            foreach (var pair in _targets) text += "|" + pair.Key + "=" + pair.Value;
            return text;
        }
    }

    internal sealed class ExactAllocationSettlement
    {
        internal readonly ExactAllocationVector Before;
        internal readonly ExactAllocationVector RequestedAfter;

        internal ExactAllocationSettlement(ExactAllocationVector before,
            ExactAllocationVector requestedAfter)
        {
            Before = before;
            RequestedAfter = requestedAfter;
        }

        internal bool IsAdmissible(out string reason)
        {
            if (Before == null || RequestedAfter == null)
            {
                reason = "before/requested-after allocation vectors are required";
                return false;
            }
            if (Before.Resource != RequestedAfter.Resource)
            {
                reason = "resource identity changed across the allocation sweep";
                return false;
            }
            if (Before.Capacity != RequestedAfter.Capacity)
            {
                reason = "permanent resource capacity changed across the allocation sweep";
                return false;
            }
            if (!Before.HasSameSchema(RequestedAfter))
            {
                reason = "native allocation target schema changed during the sweep";
                return false;
            }
            if (!Before.IsConserved())
            {
                reason = "before-state omits or double-counts a native allocation target";
                return false;
            }
            if (!RequestedAfter.IsConserved())
            {
                reason = "requested-after state omits, overdraws, or double-counts native allocation";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        internal bool VerifyAcceptedNativeState(ExactAllocationVector observedAfter,
            out string reason)
        {
            if (!IsAdmissible(out reason)) return false;
            if (observedAfter == null || !RequestedAfter.ExactEquals(observedAfter))
            {
                reason = "accepted native target vector differs from the sealed requested-after vector";
                return false;
            }
            if (!observedAfter.IsConserved())
            {
                reason = "accepted native target vector does not conserve the resource";
                return false;
            }
            reason = string.Empty;
            return true;
        }
    }

    internal enum ExactAllocationPhase
    {
        ModeChanges = 0,
        Snapshot = 1,
        ReclaimChangedTargets = 2,
        ApplyExactDeltas = 3,
        VerifyConservation = 4
    }

    internal sealed class WishAllocationTarget
    {
        internal int WishId { get; set; }
        internal long ExistingEnergy { get; set; }
        internal long ExistingMagic { get; set; }
        internal long ExistingRes3 { get; set; }
        internal long DesiredEnergy { get; set; }
        internal long DesiredMagic { get; set; }
        internal long DesiredRes3 { get; set; }
    }

    internal sealed class WishAllocationDelta
    {
        internal int WishId { get; set; }
        internal long Energy { get; set; }
        internal long Magic { get; set; }
        internal long Res3 { get; set; }
    }

    internal static class ExactResourceAllocator
    {
        internal const double NativeTickSeconds = 0.02;

        /*
        RECLAIMABLE IDLE FALLBACK

        A finite strategic cap may leave a remainder after every admitted target is funded.  Idle
        Energy or Magic has zero return, so an already-unlocked persistent NGU is the preferred
        reclaimable sink; an already-active Wandoos bar is the reset-local alternative.  Neither
        path spends Gold or another finite currency.  Live adapters still prove controller
        acceptance and may try Wandoos if the preferred NGU controller accepts no resource.
        */
        internal static NoCurrencyFallbackKind SelectNoCurrencyFallback(bool nguUnlocked,
            bool nguControllerAvailable, bool wandoosUnlocked, bool wandoosInstalled,
            bool wandoosEnabled, bool wandoosDisabled)
        {
            if (nguUnlocked && nguControllerAvailable)
                return NoCurrencyFallbackKind.Ngu;
            if (wandoosUnlocked && wandoosInstalled && wandoosEnabled && !wandoosDisabled)
                return NoCurrencyFallbackKind.Wandoos;
            return NoCurrencyFallbackKind.None;
        }

        internal static long CeilingShare(long total, long numerator, long denominator)
        {
            if (total <= 0 || numerator <= 0 || denominator <= 0)
                return 0L;
            if (numerator >= denominator)
                return total;

            // Decimal represents every Int64 exactly and has ample range for an Int64 times
            // an ordinary percentage denominator. This avoids the 1,024-unit double ULP near
            // 9e18 and the much larger float loss in the former breakpoint path.
            var exact = decimal.Multiply((decimal)total, (decimal)numerator)
                        / (decimal)denominator;
            return checked((long)decimal.Ceiling(exact));
        }

        internal static long AggregatePercentBudget(long total, int percent)
        {
            if (percent <= 0) return 0L;
            return CeilingShare(total, Math.Min(100, percent), 100L);
        }

        internal static string FormatExactInput(long value)
        {
            return Math.Max(0L, value).ToString(CultureInfo.InvariantCulture);
        }

        internal static bool TryParseExactInput(string text, out long value)
        {
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
                   && value >= 0;
        }

        internal static long Headroom(long desiredTotal, long currentAllocation, long idle)
        {
            if (desiredTotal <= currentAllocation || idle <= 0)
                return 0L;
            return Math.Min(idle, desiredTotal - currentAllocation);
        }

        internal static long ProductiveSpeedCapHeadroom(long nativeCap,
            long currentAllocation, long idle)
        {
            // Native Basic Training awards at most one completion per 20 ms tick.
            // Allocation above its cap only changes the displayed utilization; it
            // cannot improve the rate and must remain idle for honest telemetry.
            return Headroom(Math.Max(0L, nativeCap), Math.Max(0L, currentAllocation), idle);
        }

        internal static long CapAtTickBoundary(double fullCap, long maximum, long idle)
        {
            if (maximum <= 0 || idle <= 0 || double.IsNaN(fullCap)
                || double.IsInfinity(fullCap) || fullCap <= 0.0)
                return 0L;
            var boundedCap = Math.Min((double)long.MaxValue, Math.Max(1.0, fullCap));
            var ticks = Math.Max(1.0, Math.Ceiling(boundedCap / maximum));
            var request = Math.Ceiling(boundedCap / ticks * 1.00000202655792);
            if (double.IsNaN(request) || request <= 0.0)
                return 0L;
            var exact = request >= long.MaxValue ? long.MaxValue : (long)request;
            return Math.Min(Math.Min(exact, maximum), idle);
        }

        internal static long NextCompletionAllocationChunk(double fullCap, long maximum,
            long idle)
        {
            return CapAtTickBoundary(fullCap, maximum, idle);
        }

        internal static long CompletionHeadroomForTicks(long cap, long existingAllocation,
            double progress, int ticks)
        {
            if (cap <= 0 || ticks <= 0 || existingAllocation < 0
                || double.IsNaN(progress) || double.IsInfinity(progress))
                return 0L;
            var boundedProgress = Math.Max(0.0, Math.Min(1.0, progress));
            if (boundedProgress >= 1.0)
                return 0L;
            var remaining = 1m - (decimal)boundedProgress;
            var desired = checked((long)decimal.Ceiling(
                remaining * (decimal)cap / (decimal)ticks));
            desired = Math.Max(1L, Math.Min(cap, desired));
            return Headroom(desired, existingAllocation, long.MaxValue);
        }

        internal static bool IsSupportedHackId(int id, int installedCount)
        {
            return id >= 0 && id <= 15 && id < installedCount;
        }

        internal static long NativeCompletionTicks(double progress, double progressPerTick)
        {
            if (double.IsNaN(progressPerTick) || double.IsInfinity(progressPerTick)
                || progressPerTick <= 0.0)
                return long.MaxValue;
            var remaining = Math.Max(0.0, 1.0 - Math.Max(0.0, Math.Min(1.0, progress)));
            if (remaining <= 0.0)
                return 1L;
            var ticks = Math.Ceiling(remaining / progressPerTick);
            if (double.IsNaN(ticks) || double.IsInfinity(ticks) || ticks >= long.MaxValue)
                return long.MaxValue;
            // Native online update loops award at most one level and reset progress to zero.
            return Math.Max(1L, (long)ticks);
        }

        internal static double NativeCompletionSeconds(double progress, double progressPerTick)
        {
            var ticks = NativeCompletionTicks(progress, progressPerTick);
            return ticks == long.MaxValue ? double.PositiveInfinity : ticks * NativeTickSeconds;
        }

        /*
        RESET-LOCAL COMPLETION ADMISSION

        A partially filled reset-local bar has no terminal value.  Wandoos is therefore useful
        only if the proposed allocation completes the next native level early enough to use the
        resulting multiplier.  Requiring the completed level to remain active for at least its
        own build time is the conservative break-even boundary when no stronger downstream quote
        exists; the absolute minute floor rejects last-second completions dominated by scheduler
        and allocation uncertainty.
        */
        internal static bool ResetLocalLevelHasUseWindow(double progress, long allocation,
            double speed, double baseTime, double remainingSeconds,
            out double completionSeconds)
        {
            completionSeconds = double.PositiveInfinity;
            if (allocation <= 0L || speed <= 0.0 || baseTime <= 0.0
                || remainingSeconds <= 0.0 || double.IsNaN(speed)
                || double.IsInfinity(speed) || double.IsNaN(baseTime)
                || double.IsInfinity(baseTime) || double.IsNaN(remainingSeconds)
                || double.IsInfinity(remainingSeconds))
                return false;
            var progressPerTick = Math.Min(1.0, allocation * speed / baseTime);
            completionSeconds = NativeCompletionSeconds(progress, progressPerTick);
            if (double.IsInfinity(completionSeconds) || completionSeconds > remainingSeconds)
                return false;
            var usefulSeconds = remainingSeconds - completionSeconds;
            return usefulSeconds >= Math.Max(60.0, completionSeconds);
        }

        internal static double HackMilestoneSeconds(long currentLevel, long levels,
            double currentProgress, double progressScalePerTick)
        {
            if (levels <= 0) return 0.0;
            if (progressScalePerTick <= 0.0 || double.IsNaN(progressScalePerTick)
                || double.IsInfinity(progressScalePerTick))
                return double.PositiveInfinity;
            var seconds = 0.0;
            for (var offset = 0L; offset < levels; offset++)
            {
                if (currentLevel > long.MaxValue - offset)
                    return double.PositiveInfinity;
                var level = currentLevel + offset;
                var divider = Math.Pow(1.0078, level) * (level + 1.0);
                var perTick = progressScalePerTick / divider;
                var progress = offset == 0L ? currentProgress : 0.0;
                var ticks = NativeCompletionTicks(progress, perTick);
                if (ticks == long.MaxValue) return double.PositiveInfinity;
                seconds += ticks * NativeTickSeconds;
            }
            return seconds;
        }

        internal static bool IsValidPhaseTransition(ExactAllocationPhase previous,
            ExactAllocationPhase next)
        {
            return (int)next == (int)previous + 1;
        }

        internal static bool CanSnapshot(bool osChangeDue, bool osChangeConfirmed)
        {
            return !osChangeDue || osChangeConfirmed;
        }

        internal static ExactAllocationPhase[] FrameOrder()
        {
            return new[]
            {
                ExactAllocationPhase.ModeChanges,
                ExactAllocationPhase.Snapshot,
                ExactAllocationPhase.ReclaimChangedTargets,
                ExactAllocationPhase.ApplyExactDeltas,
                ExactAllocationPhase.VerifyConservation
            };
        }

        internal static bool Conserves(long total, long idle, IEnumerable<long> allocations)
        {
            if (total < 0 || idle < 0 || idle > total || allocations == null)
                return false;
            decimal observed = idle;
            foreach (var allocation in allocations)
            {
                if (allocation < 0) return false;
                observed += allocation;
                if (observed > total) return false;
            }
            return observed == total;
        }

        internal static bool TryObservedAcceptance(long idleBefore, long idleAfter,
            long requested, out long accepted)
        {
            accepted = 0L;
            if (idleBefore < 0 || idleAfter < 0 || requested < 0 || idleAfter > idleBefore)
                return false;
            accepted = idleBefore - idleAfter;
            return accepted <= requested;
        }

        internal static IList<WishAllocationDelta> PlanWishHeadroom(
            IEnumerable<WishAllocationTarget> targets, long idleEnergy, long idleMagic,
            long idleRes3)
        {
            var result = new List<WishAllocationDelta>();
            if (targets == null || idleEnergy < 0 || idleMagic < 0 || idleRes3 < 0)
                return result;

            // A repeated logical slot is one native Wish. Merge by maximum desired total,
            // never by sum, so duplicate gate emphasis cannot over-add the same record.
            var merged = targets.GroupBy(x => x.WishId).OrderBy(x => x.Key);
            foreach (var group in merged)
            {
                var existingEnergy = group.Max(x => Math.Max(0L, x.ExistingEnergy));
                var existingMagic = group.Max(x => Math.Max(0L, x.ExistingMagic));
                var existingRes3 = group.Max(x => Math.Max(0L, x.ExistingRes3));
                var desiredEnergy = group.Max(x => Math.Max(0L, x.DesiredEnergy));
                var desiredMagic = group.Max(x => Math.Max(0L, x.DesiredMagic));
                var desiredRes3 = group.Max(x => Math.Max(0L, x.DesiredRes3));
                var energy = Headroom(desiredEnergy, existingEnergy, idleEnergy);
                var magic = Headroom(desiredMagic, existingMagic, idleMagic);
                var res3 = Headroom(desiredRes3, existingRes3, idleRes3);

                // A Wish with a zero final factor makes no progress. Commit its resource
                // triple atomically, or skip it and leave all three pools available.
                if (existingEnergy + energy <= 0 || existingMagic + magic <= 0
                    || existingRes3 + res3 <= 0)
                    continue;
                result.Add(new WishAllocationDelta
                {
                    WishId = group.Key,
                    Energy = energy,
                    Magic = magic,
                    Res3 = res3
                });
                idleEnergy -= energy;
                idleMagic -= magic;
                idleRes3 -= res3;
            }
            return result;
        }
    }
}
