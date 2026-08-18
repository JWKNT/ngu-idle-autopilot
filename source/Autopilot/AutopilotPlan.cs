using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/*
FILE PURPOSE

This file defines plan records shared by the high-level planner, generated allocation profile,
rebirth controller, and telemetry. Plans describe intended timed priorities and optimizer
evidence; they are not proof of mutation. Live controller state does not belong in serialization.
*/
namespace NGUInjector.Autopilot
{
    internal sealed class PlanBreakpoint
    {
        internal int Time;
        internal string[] Priorities;
    }

    internal sealed class TimedValue
    {
        internal int Time;
        internal int Value;
    }

    internal sealed class AutopilotPlan
    {
        internal string Stage;
        internal string Objective;
        internal int RebirthSeconds = -1;
        internal string RebirthReason = string.Empty;
        internal int RebirthRunnerUpSeconds = -1;
        internal int RebirthRunnerUpDeltaSeconds = -1;
        internal string RebirthRunnerUpReason = string.Empty;
        internal double RebirthSelectedScorePerHour;
        internal double RebirthRunnerUpScorePerHour;
        internal double RebirthProjectedMultiplier;
        internal int RebirthProjectedAP;
        internal string RebirthCandidateSummary = string.Empty;
        internal int RebirthCandidateCount;
        internal bool RebirthRecoveryMode;
        internal int RebirthRecoveryEtaSeconds = -1;
        internal int RebirthRecoveryRemainingBosses;
        internal string RebirthRecoveryReason = string.Empty;
        internal double RebirthExpectedCatchupExp;
        internal double RebirthExpectedCatchupExpPerHour;
        internal double RebirthMinimumNumberRatio;
        internal bool RebirthExecutionHold;
        internal int RebirthNextPositiveEtaSeconds = -1;
        internal int RebirthNextEvaluationEtaSeconds = -1;
        internal string RebirthEtaReason = string.Empty;
        // Puzzle windows and challenge stipulations are legality constraints, not candidates for
        // the general event scorer. Builders set this bit whenever changing the target could make
        // an otherwise valid run impossible or irreversibly miss its native window.
        internal bool RebirthTargetLocked;
        internal readonly List<TimedValue> NGUDifficulties = new List<TimedValue>();
        internal readonly List<string> Challenges = new List<string>();
        internal string ChallengeEvidenceSummary = string.Empty;
        internal bool ChallengeActive;
        internal bool ChallengeAdmitted;
        internal string ChallengeName = string.Empty;
        internal int ChallengeClearEtaSeconds = -1;
        internal int ChallengeRecoveryEtaSeconds = -1;
        internal int ChallengeTargetBoss = -1;
        internal int ChallengeTargetLevel = -1;
        internal int ChallengeCompletedBefore = -1;
        internal int ChallengeMaxCompletions = -1;
        internal string ChallengeEtaReason = string.Empty;
        internal string EndgameObjective = string.Empty;
        internal string EndgameMissingSummary = string.Empty;
        internal int Titan12VersionTarget = -1;
        internal bool EndgameReadyToTrigger;
        internal int WandoosOS;
        internal int[] Diggers = new int[0];
        internal readonly List<PlanBreakpoint> Energy = new List<PlanBreakpoint>();
        internal readonly List<PlanBreakpoint> Magic = new List<PlanBreakpoint>();
        internal readonly List<PlanBreakpoint> R3 = new List<PlanBreakpoint>();

        internal string Signature(Character c)
        {
            // The optimizer's elapsed+60 diagnostic probe moves every second while
            // reset is blocked. It is not executable policy and must not regenerate
            // the allocation profile (which would reclaim/reapply every resource). Likewise,
            // once a positive checkpoint is due, the optimizer may return the current second
            // forever. Canonicalize that moving target so the generated TIME rebirth remains
            // installed long enough to reach its commit gate.
            var elapsed = c == null || c.rebirthTime == null ? -1.0 : c.rebirthTime.totalseconds;
            var rebirthSignature = RebirthSignatureFor(RebirthSeconds,
                RebirthExecutionHold, elapsed);
            return Stage + "|" + Objective + "|" + rebirthSignature + "|" + RebirthReason + "|"
                   + RebirthExecutionHold + "|"
                   + EndgameObjective + "|" + EndgameMissingSummary + "|"
                   + Titan12VersionTarget + "|" + EndgameReadyToTrigger + "|"
                   + ChallengeActive + "|" + ChallengeAdmitted + "|" + ChallengeName + "|"
                   + string.Join(";", NGUDifficulties.Select(x => x.Time + ":" + x.Value).ToArray()) + "|" + WandoosOS
                   + "|" + string.Join(",", Diggers.Select(x => x.ToString()).ToArray())
                   + "|" + string.Join(",", Challenges.ToArray()) + "|" + BreakpointSignature(Energy) + "|" + BreakpointSignature(Magic) + "|" + BreakpointSignature(R3);
        }

        internal static string RebirthSignatureFor(int targetSeconds, bool executionHold,
            double elapsedSeconds)
        {
            if (executionHold) return "UNSCHEDULED-HOLD";
            if (targetSeconds >= 0 && elapsedSeconds >= targetSeconds) return "DUE";
            return targetSeconds.ToString();
        }

        internal static bool IsGeneratedAllocationPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var normalized = path.Replace('\\', '/');
            var separator = normalized.LastIndexOf('/');
            var leaf = separator < 0 ? normalized : normalized.Substring(separator + 1);
            return string.Equals(leaf, "autopilot.generated.json",
                StringComparison.OrdinalIgnoreCase);
        }

        /*
        RESET-LOCAL ALLOCATION HORIZON

        A rolling rebirth safety hold is not an execution countdown. Treating its diagnostic
        elapsed+60 timestamp as a real reset made every Augment, Time Machine, Advanced Training,
        and Blood decision reject work that needed more than one minute. While reset is blocked,
        grant reset-local systems a conservative rolling hour; the planner still reevaluates every
        second and the irreversible rebirth controller remains governed by its separate safety gate.
        */
        internal double EffectiveAllocationTarget(Character c)
        {
            if (!RebirthExecutionHold || c == null) return RebirthSeconds;
            return Math.Max(RebirthSeconds, c.rebirthTime.totalseconds + 3600.0);
        }

        private static string BreakpointSignature(IEnumerable<PlanBreakpoint> points)
        {
            return string.Join(";", points.Select(x => x.Time + ":" + string.Join(",", x.Priorities)).ToArray());
        }

        internal string ToProfileJson(bool allowRebirth, bool allowChallenges)
        {
            var b = new StringBuilder();
            b.AppendLine("{");
            b.AppendLine("  \"_generatedBy\": \"NGU Autopilot - edit autopilot.json, not this file\",");
            b.AppendLine("  \"Breakpoints\": {");
            AppendResource(b, "Energy", Energy);
            b.AppendLine(",");
            AppendResource(b, "Magic", Magic);
            b.AppendLine(",");
            AppendResource(b, "R3", R3);
            b.AppendLine(",");
            b.AppendLine("    \"Gear\": [{\"Time\": 0, \"ID\": []}],");
            b.AppendLine("    \"Diggers\": [{\"Time\": 0, \"List\": [" + string.Join(",", Diggers.Select(x => x.ToString()).ToArray()) + "]}],");
            b.AppendLine("    \"Wandoos\": [{\"Time\": 0, \"OS\": " + WandoosOS + "}],");
            b.AppendLine("    \"NGUDiff\": [" + string.Join(",", NGUDifficulties.Select(x => "{\"Time\":" + x.Time + ",\"Diff\":" + x.Value + "}").ToArray()) + "],");
            if (allowRebirth)
            {
                var challenges = allowChallenges ? Challenges : new List<string>();
                b.AppendLine("    \"Rebirth\": {\"Type\": \"TIME\", \"Target\": " + RebirthSeconds
                             + ", \"Challenges\": [" + string.Join(",", challenges.Select(x => "\"" + x + "\"").ToArray()) + "]}");
            }
            else
            {
                b.AppendLine("    \"RebirthTime\": -1");
            }
            b.AppendLine("  }");
            b.AppendLine("}");
            return b.ToString();
        }

        private static void AppendResource(StringBuilder b, string name, IList<PlanBreakpoint> points)
        {
            b.AppendLine("    \"" + name + "\": [");
            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                var priorities = string.Join(",", point.Priorities.Select(x => "\"" + x + "\"").ToArray());
                b.Append("      {\"Time\": " + point.Time + ", \"Priorities\": [" + priorities + "]}");
                if (i + 1 < points.Count)
                    b.Append(",");
                b.AppendLine();
            }
            b.Append("    ]");
        }
    }
}
