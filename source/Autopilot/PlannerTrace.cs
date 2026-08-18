using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/*
FILE PURPOSE

Purpose: PlannerTrace is the immutable telemetry and archived-shadow-replay surface for the bounded
global scheduler. It records what task 28 predicted, what typed event/delta was later observed, and
whether archived snapshots improve on the incumbent policy without suggesting execution authority.

Mechanism: A trace is captured from one ScheduleDecision and bound to its snapshot/model/objective
hashes. Observation produces a new record with typed event equality, timing residual, and delta
distance. PlannerBacktestRunner re-evaluates in-memory archived PlannerSearchState fixtures through
a caller-supplied pure shadow evaluator and compares terminal mean, branch switches, regressions,
and binding failures. JSON serialization is presentation-only and never parsed back into strategy.

Inputs and outputs: Inputs are shadow decisions, typed observations, or archived immutable states.
Outputs are immutable trace/backtest records and deterministic JSON suitable for task-29 telemetry.
No file, runtime, controller, save, or process access occurs.

Invariants and safety: Every nonterminal plan names a finite typed event or blocker; observations
from another snapshot are stale rather than matched; branch identity uses PlannerActionKey, not
labels; unavailable terminal estimates stay unavailable; and replay accepts only shadow decisions
whose state hash matches the archive.

Extension points and non-goals: Task 29 owns persistence and live residual capture; task 30 owns UI.
This file does not schedule, calibrate, execute, mutate, authorize, write archives, or infer commands
from JSON, strings, labels, or historical winner names.
*/
namespace NGUInjector.Autopilot
{
    internal enum PlannerObservationStatus
    {
        Pending,
        Matched,
        UnexpectedEvent,
        StaleSnapshot,
        NoExpectedEvent
    }

    internal sealed class PlannerObservation
    {
        internal readonly string SnapshotHash;
        internal readonly PlannerEventKey Event;
        internal readonly double ObservedSeconds;
        internal readonly PlannerDelta Delta;

        internal PlannerObservation(string snapshotHash, PlannerEventKey plannerEvent,
            double observedSeconds, PlannerDelta delta)
        {
            if (string.IsNullOrEmpty(snapshotHash))
                throw new ArgumentException("snapshot hash is required", "snapshotHash");
            if (double.IsNaN(observedSeconds) || double.IsInfinity(observedSeconds)
                || observedSeconds < 0.0)
                throw new ArgumentOutOfRangeException("observedSeconds");
            SnapshotHash = snapshotHash;
            Event = plannerEvent;
            ObservedSeconds = observedSeconds;
            Delta = delta ?? new PlannerDelta(null);
        }
    }

    internal sealed class PlannerTraceRecord
    {
        internal readonly string SnapshotHash;
        internal readonly string ModelHash;
        internal readonly string ObjectiveHash;
        internal readonly ScheduleDecisionStatus DecisionStatus;
        internal readonly PlannerAuthority Authority;
        internal readonly bool HasAction;
        internal readonly PlannerActionKey Action;
        internal readonly string ActionStableId;
        internal readonly bool HasExpectedEvent;
        internal readonly PlannerEventKey ExpectedEvent;
        internal readonly string EventStableId;
        internal readonly PlannerRouteEstimate TerminalEta;
        internal readonly double LowerBoundSeconds;
        internal readonly double GapSeconds;
        internal readonly bool HasRunnerUp;
        internal readonly PlannerActionKey RunnerUp;
        internal readonly double RegretSeconds;
        internal readonly PlannerBlockerKind Blocker;
        internal readonly bool UsedRolloutFallback;
        internal readonly int ExpandedNodes;
        internal readonly int GeneratedTransitions;
        internal readonly int DominancePruned;
        internal readonly PlannerObservationStatus ObservationStatus;
        internal readonly double ObservedSeconds;
        internal readonly double TimingResidualSeconds;
        internal readonly double DeltaResidual;

        private PlannerTraceRecord(ScheduleDecision decision,
            PlannerObservationStatus observationStatus, double observedSeconds,
            double timingResidualSeconds, double deltaResidual)
        {
            if (decision == null) throw new ArgumentNullException("decision");
            SnapshotHash = decision.PlanStateHash;
            ModelHash = decision.ModelHash;
            ObjectiveHash = decision.ObjectiveHash;
            DecisionStatus = decision.Status;
            Authority = decision.Authority;
            HasAction = decision.Selected != null;
            Action = HasAction ? decision.Selected.Key : default(PlannerActionKey);
            ActionStableId = HasAction ? decision.Selected.StableId : string.Empty;
            HasExpectedEvent = decision.ExpectedNextEvent != null;
            ExpectedEvent = HasExpectedEvent ? decision.ExpectedNextEvent.Key
                : default(PlannerEventKey);
            EventStableId = HasExpectedEvent
                ? decision.ExpectedNextEvent.StableId : string.Empty;
            TerminalEta = decision.TerminalEta;
            LowerBoundSeconds = decision.LowerBoundSeconds;
            GapSeconds = decision.OptimalityGapSeconds;
            HasRunnerUp = decision.HasRunnerUp;
            RunnerUp = decision.RunnerUp;
            RegretSeconds = decision.RegretSeconds;
            Blocker = decision.Blocker.Kind;
            UsedRolloutFallback = decision.UsedRolloutFallback;
            ExpandedNodes = decision.ExpandedNodes;
            GeneratedTransitions = decision.GeneratedTransitions;
            DominancePruned = decision.DominancePruned;
            ObservationStatus = observationStatus;
            ObservedSeconds = observedSeconds;
            TimingResidualSeconds = timingResidualSeconds;
            DeltaResidual = deltaResidual;
        }

        internal static PlannerTraceRecord Capture(ScheduleDecision decision)
        {
            return new PlannerTraceRecord(decision, PlannerObservationStatus.Pending,
                -1.0, double.NaN, double.NaN);
        }

        internal static PlannerTraceRecord Observe(ScheduleDecision decision,
            PlannerObservation observation)
        {
            if (decision == null) throw new ArgumentNullException("decision");
            if (observation == null) throw new ArgumentNullException("observation");
            var status = !string.Equals(decision.PlanStateHash,
                    observation.SnapshotHash, StringComparison.Ordinal)
                ? PlannerObservationStatus.StaleSnapshot
                : decision.ExpectedNextEvent == null
                    ? PlannerObservationStatus.NoExpectedEvent
                    : decision.ExpectedNextEvent.Key.Equals(observation.Event)
                        ? PlannerObservationStatus.Matched
                        : PlannerObservationStatus.UnexpectedEvent;
            var timing = status != PlannerObservationStatus.Matched ? double.NaN
                : observation.ObservedSeconds
                  - decision.ExpectedNextEvent.Duration.MeanSeconds;
            var delta = status == PlannerObservationStatus.Matched
                ? decision.ExpectedDelta.DistanceFrom(observation.Delta) : double.NaN;
            return new PlannerTraceRecord(decision, status,
                observation.ObservedSeconds, timing, delta);
        }

        internal string ToJson()
        {
            return "{\"snapshotHash\":\"" + Escape(SnapshotHash)
                   + "\",\"modelHash\":\"" + Escape(ModelHash)
                   + "\",\"objectiveHash\":\"" + Escape(ObjectiveHash)
                   + "\",\"authority\":\"" + Authority
                   + "\",\"canExecute\":false,\"status\":\"" + DecisionStatus
                   + "\",\"action\":\"" + (HasAction ? Action.ToString() : string.Empty)
                   + "\",\"actionId\":\"" + Escape(ActionStableId)
                   + "\",\"nextEvent\":\""
                   + (HasExpectedEvent ? ExpectedEvent.ToString() : string.Empty)
                   + "\",\"eventId\":\"" + Escape(EventStableId)
                   + "\",\"meanSeconds\":" + Statistic(TerminalEta.MeanSeconds)
                   + ",\"p50Seconds\":" + Statistic(TerminalEta.P50Seconds)
                   + ",\"p90Seconds\":" + Statistic(TerminalEta.P90Seconds)
                   + ",\"lowerBoundSeconds\":" + Statistic(LowerBoundSeconds)
                   + ",\"upperBoundSeconds\":" + Statistic(TerminalEta.UpperBoundSeconds)
                   + ",\"gapSeconds\":" + Statistic(GapSeconds)
                   + ",\"regretSeconds\":" + Statistic(RegretSeconds)
                   + ",\"runnerUp\":\"" + (HasRunnerUp ? RunnerUp.ToString() : string.Empty)
                   + "\",\"blocker\":\"" + Blocker
                   + "\",\"rolloutFallback\":"
                   + (UsedRolloutFallback ? "true" : "false")
                   + ",\"expandedNodes\":" + ExpandedNodes
                   + ",\"generatedTransitions\":" + GeneratedTransitions
                   + ",\"dominancePruned\":" + DominancePruned
                   + ",\"observationStatus\":\"" + ObservationStatus
                   + "\",\"observedSeconds\":" + Statistic(ObservedSeconds)
                   + ",\"timingResidualSeconds\":"
                   + SignedStatistic(TimingResidualSeconds)
                   + ",\"deltaResidual\":" + Statistic(DeltaResidual) + "}";
        }

        private static string Statistic(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) || value < 0.0
                ? "null" : value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string SignedStatistic(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value)
                ? "null" : value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    internal sealed class PlannerArchivedSnapshot
    {
        internal readonly string ArchiveId;
        internal readonly string SnapshotHash;
        internal readonly PlannerSearchState State;
        internal readonly bool HasBaselineAction;
        internal readonly PlannerActionKey BaselineAction;
        internal readonly double BaselineMeanSeconds;

        internal PlannerArchivedSnapshot(string archiveId, PlannerSearchState state,
            bool hasBaselineAction, PlannerActionKey baselineAction,
            double baselineMeanSeconds)
        {
            if (string.IsNullOrEmpty(archiveId))
                throw new ArgumentException("archive ID is required", "archiveId");
            if (state == null || state.Projection == null)
                throw new ArgumentException(
                    "archived state requires a task-27 projection", "state");
            if (double.IsNaN(baselineMeanSeconds)
                || double.IsInfinity(baselineMeanSeconds) || baselineMeanSeconds < 0.0)
                throw new ArgumentOutOfRangeException("baselineMeanSeconds");
            ArchiveId = archiveId;
            State = state;
            SnapshotHash = state.Projection.SnapshotHash;
            HasBaselineAction = hasBaselineAction;
            BaselineAction = baselineAction;
            BaselineMeanSeconds = baselineMeanSeconds;
        }
    }

    internal enum PlannerBacktestCaseStatus
    {
        ImprovedOrEqual,
        Regressed,
        Incomplete,
        BindingMismatch,
        NonShadowDecision
    }

    internal sealed class PlannerBacktestCaseResult
    {
        internal readonly string ArchiveId;
        internal readonly PlannerBacktestCaseStatus Status;
        internal readonly bool BranchSwitched;
        internal readonly double MeanDeltaSeconds;

        internal PlannerBacktestCaseResult(string archiveId,
            PlannerBacktestCaseStatus status, bool branchSwitched,
            double meanDeltaSeconds)
        {
            ArchiveId = archiveId;
            Status = status;
            BranchSwitched = branchSwitched;
            MeanDeltaSeconds = meanDeltaSeconds;
        }
    }

    internal sealed class PlannerBacktestResult
    {
        private readonly PlannerBacktestCaseResult[] _cases;
        internal readonly int Total;
        internal readonly int ImprovedOrEqual;
        internal readonly int Regressed;
        internal readonly int Incomplete;
        internal readonly int BindingFailures;
        internal readonly int BranchSwitches;
        internal readonly double AggregateMeanDeltaSeconds;

        internal PlannerBacktestResult(IEnumerable<PlannerBacktestCaseResult> cases)
        {
            _cases = (cases ?? Enumerable.Empty<PlannerBacktestCaseResult>()).ToArray();
            Total = _cases.Length;
            ImprovedOrEqual = _cases.Count(x =>
                x.Status == PlannerBacktestCaseStatus.ImprovedOrEqual);
            Regressed = _cases.Count(x => x.Status == PlannerBacktestCaseStatus.Regressed);
            Incomplete = _cases.Count(x => x.Status == PlannerBacktestCaseStatus.Incomplete);
            BindingFailures = _cases.Count(x =>
                x.Status == PlannerBacktestCaseStatus.BindingMismatch
                || x.Status == PlannerBacktestCaseStatus.NonShadowDecision);
            BranchSwitches = _cases.Count(x => x.BranchSwitched);
            AggregateMeanDeltaSeconds = _cases
                .Where(x => x.Status == PlannerBacktestCaseStatus.ImprovedOrEqual
                            || x.Status == PlannerBacktestCaseStatus.Regressed)
                .Sum(x => x.MeanDeltaSeconds);
        }

        internal PlannerBacktestCaseResult[] Cases()
        {
            return (PlannerBacktestCaseResult[])_cases.Clone();
        }
    }

    internal static class PlannerBacktestRunner
    {
        internal static PlannerBacktestResult Replay(
            IEnumerable<PlannerArchivedSnapshot> source,
            Func<PlannerArchivedSnapshot, ScheduleDecision> shadowEvaluator)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (shadowEvaluator == null) throw new ArgumentNullException("shadowEvaluator");
            var result = new List<PlannerBacktestCaseResult>();
            foreach (var archive in source)
            {
                if (archive == null)
                    throw new ArgumentException("archive cannot contain null");
                var decision = shadowEvaluator(archive);
                if (decision == null || decision.Authority != PlannerAuthority.ShadowOnly
                    || decision.CanExecute)
                {
                    result.Add(new PlannerBacktestCaseResult(archive.ArchiveId,
                        PlannerBacktestCaseStatus.NonShadowDecision, false, 0.0));
                    continue;
                }
                if (!string.Equals(archive.SnapshotHash, decision.PlanStateHash,
                        StringComparison.Ordinal))
                {
                    result.Add(new PlannerBacktestCaseResult(archive.ArchiveId,
                        PlannerBacktestCaseStatus.BindingMismatch, false, 0.0));
                    continue;
                }
                var switched = archive.HasBaselineAction && decision.Selected != null
                               && !archive.BaselineAction.Equals(decision.Selected.Key);
                if (!decision.TerminalEta.ModelComplete)
                {
                    result.Add(new PlannerBacktestCaseResult(archive.ArchiveId,
                        PlannerBacktestCaseStatus.Incomplete, switched, 0.0));
                    continue;
                }
                var delta = decision.TerminalEta.MeanSeconds
                            - archive.BaselineMeanSeconds;
                result.Add(new PlannerBacktestCaseResult(archive.ArchiveId,
                    delta <= 1e-9 ? PlannerBacktestCaseStatus.ImprovedOrEqual
                        : PlannerBacktestCaseStatus.Regressed, switched, delta));
            }
            return new PlannerBacktestResult(result);
        }
    }
}
