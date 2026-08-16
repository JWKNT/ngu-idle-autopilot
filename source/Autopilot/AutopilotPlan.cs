using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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
        internal readonly List<TimedValue> NGUDifficulties = new List<TimedValue>();
        internal readonly List<string> Challenges = new List<string>();
        internal int WandoosOS;
        internal int[] Diggers = new int[0];
        internal readonly List<PlanBreakpoint> Energy = new List<PlanBreakpoint>();
        internal readonly List<PlanBreakpoint> Magic = new List<PlanBreakpoint>();
        internal readonly List<PlanBreakpoint> R3 = new List<PlanBreakpoint>();

        internal string Signature()
        {
            return Stage + "|" + Objective + "|" + RebirthSeconds + "|" + RebirthReason + "|"
                   + string.Join(";", NGUDifficulties.Select(x => x.Time + ":" + x.Value).ToArray()) + "|" + WandoosOS
                   + "|" + string.Join(",", Diggers.Select(x => x.ToString()).ToArray())
                   + "|" + string.Join(",", Challenges.ToArray()) + "|" + BreakpointSignature(Energy) + "|" + BreakpointSignature(Magic) + "|" + BreakpointSignature(R3);
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
