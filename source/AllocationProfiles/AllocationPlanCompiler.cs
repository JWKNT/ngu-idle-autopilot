using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimpleJSON;

/*
FILE PURPOSE

AllocationPlanCompiler is the controller-free boundary between allocation JSON and live gameplay.
It proves that a complete document is structurally closed, validates every required section, and
produces a game-agnostic plan containing only values.  No Character, Unity, Main, or controller is
referenced here, so file-watcher/startup reloads can compile and version a candidate without causing
gameplay.  AllocationPlanSlot retains the last successfully compiled plan when an editor exposes a
torn or invalid intermediate file.
*/
namespace NGUInjector.AllocationProfiles
{
    internal sealed class AllocationResourcePlan
    {
        internal double Time;
        internal string[] Priorities;
    }

    internal sealed class AllocationGearPlan
    {
        internal double Time;
        internal int[] Gear;
    }

    internal sealed class AllocationDiggerPlan
    {
        internal double Time;
        internal int[] Diggers;
    }

    internal sealed class AllocationWandoosPlan
    {
        internal double Time;
        internal int OS;
    }

    internal sealed class AllocationNguDifficultyPlan
    {
        internal double Time;
        internal int Difficulty;
    }

    internal sealed class AllocationRebirthPlan
    {
        internal string Type;
        internal double Target;
        internal string[] Challenges;
        internal bool UsesLegacyTime;
    }

    internal sealed class CompiledAllocationPlan
    {
        internal AllocationResourcePlan[] Energy;
        internal AllocationResourcePlan[] Magic;
        internal AllocationResourcePlan[] R3;
        internal AllocationGearPlan[] Gear;
        internal AllocationDiggerPlan[] Diggers;
        internal AllocationWandoosPlan[] Wandoos;
        internal AllocationNguDifficultyPlan[] NguDifficulties;
        internal AllocationRebirthPlan Rebirth;
        internal string Fingerprint;
        internal long InstallationVersion;
    }

    internal sealed class AllocationPlanCompilation
    {
        internal bool Success;
        internal CompiledAllocationPlan Plan;
        internal string Error;
    }

    internal sealed class AllocationPlanSlot
    {
        internal CompiledAllocationPlan Current { get; private set; }
        internal string LastGoodSource { get; private set; }
        internal long Version { get; private set; }

        internal bool TryInstall(string source, out string error)
        {
            var compilation = AllocationPlanCompiler.Compile(source);
            if (!compilation.Success)
            {
                error = compilation.Error;
                return false;
            }

            Version++;
            compilation.Plan.InstallationVersion = Version;
            Current = compilation.Plan;
            LastGoodSource = source;
            error = string.Empty;
            return true;
        }
    }

    internal static class AllocationPlanCompiler
    {
        private static readonly HashSet<string> PriorityKinds = new HashSet<string>(
            new[]
            {
                "NGU", "CAPNGU", "ALLNGU", "CAPALLNGU",
                "AT", "CAPAT", "ALLAT", "CAPALLAT",
                "AUG", "CAPAUG", "BESTAUG", "CAPBESTAUG",
                "BT", "CAPBT", "ALLBT", "CAPALLBT",
                "HACK", "CAPHACK", "ALLHACK", "CAPALLHACK",
                "BESTHACK", "CAPBESTHACK", "WISH", "CAPWISH",
                "WAN", "CAPWAN", "BR", "TM", "CAPTM", "RIT", "CAPRIT"
            }, StringComparer.Ordinal);

        private static readonly HashSet<string> ChallengeKinds = new HashSet<string>(
            new[] {"BASIC", "NOAUG", "24HR", "100LC", "NOEC", "TC", "NORB", "LSC", "BLIND", "NONGU", "NOTM"},
            StringComparer.Ordinal);

        internal static AllocationPlanCompilation Compile(string source)
        {
            try
            {
                string structureError;
                if (!HasOneCompleteJsonRoot(source, out structureError))
                    return Failure(structureError);

                var root = JSON.Parse(source);
                if (root == null || !root.IsObject)
                    return Failure("allocation root must be an object");
                var breakpoints = Member(root, "Breakpoints");
                if (breakpoints == null || !breakpoints.IsObject)
                    return Failure("Breakpoints must be an object");

                var plan = new CompiledAllocationPlan
                {
                    Energy = ParseResources(breakpoints, "Energy"),
                    Magic = ParseResources(breakpoints, "Magic"),
                    R3 = ParseResources(breakpoints, "R3"),
                    Gear = ParseGear(breakpoints),
                    Diggers = ParseDiggers(breakpoints),
                    Wandoos = ParseWandoos(breakpoints),
                    NguDifficulties = ParseNguDifficulties(breakpoints),
                    Rebirth = ParseRebirth(breakpoints),
                    Fingerprint = Fingerprint(source)
                };
                return new AllocationPlanCompilation {Success = true, Plan = plan, Error = string.Empty};
            }
            catch (Exception error)
            {
                return Failure(error.Message);
            }
        }

        private static AllocationResourcePlan[] ParseResources(JSONNode breakpoints, string name)
        {
            var section = RequiredArray(breakpoints, name);
            var plans = new List<AllocationResourcePlan>();
            var index = 0;
            foreach (var node in section.Children)
            {
                RequireObject(node, name + "[" + index + "]");
                var time = ParseTime(Required(node, "Time", name + "[" + index + "]"),
                    name + "[" + index + "].Time", false);
                var prioritiesNode = Required(node, "Priorities", name + "[" + index + "]");
                if (!prioritiesNode.IsArray)
                    throw new FormatException(name + "[" + index + "].Priorities must be an array");
                var priorities = new List<string>();
                var priorityIndex = 0;
                foreach (var priorityNode in prioritiesNode.Children)
                {
                    if (!priorityNode.IsString)
                        throw new FormatException(name + "[" + index + "].Priorities[" + priorityIndex + "] must be a string");
                    var priority = priorityNode.Value.ToUpperInvariant();
                    ValidatePriority(priority, name + "[" + index + "].Priorities[" + priorityIndex + "]");
                    priorities.Add(priority);
                    priorityIndex++;
                }
                plans.Add(new AllocationResourcePlan {Time = time, Priorities = priorities.ToArray()});
                index++;
            }
            return plans.OrderByDescending(x => x.Time).ToArray();
        }

        private static AllocationGearPlan[] ParseGear(JSONNode breakpoints)
        {
            var section = RequiredArray(breakpoints, "Gear");
            var plans = new List<AllocationGearPlan>();
            var index = 0;
            foreach (var node in section.Children)
            {
                RequireObject(node, "Gear[" + index + "]");
                plans.Add(new AllocationGearPlan
                {
                    Time = ParseTime(Required(node, "Time", "Gear[" + index + "]"), "Gear[" + index + "].Time", false),
                    Gear = ParseIntegerArray(Required(node, "ID", "Gear[" + index + "]"), "Gear[" + index + "].ID", 0, int.MaxValue)
                });
                index++;
            }
            return plans.OrderByDescending(x => x.Time).ToArray();
        }

        private static AllocationDiggerPlan[] ParseDiggers(JSONNode breakpoints)
        {
            var section = RequiredArray(breakpoints, "Diggers");
            var plans = new List<AllocationDiggerPlan>();
            var index = 0;
            foreach (var node in section.Children)
            {
                RequireObject(node, "Diggers[" + index + "]");
                plans.Add(new AllocationDiggerPlan
                {
                    Time = ParseTime(Required(node, "Time", "Diggers[" + index + "]"), "Diggers[" + index + "].Time", false),
                    Diggers = ParseIntegerArray(Required(node, "List", "Diggers[" + index + "]"), "Diggers[" + index + "].List", 0, int.MaxValue)
                });
                index++;
            }
            return plans.OrderByDescending(x => x.Time).ToArray();
        }

        private static AllocationWandoosPlan[] ParseWandoos(JSONNode breakpoints)
        {
            var section = RequiredArray(breakpoints, "Wandoos");
            var plans = new List<AllocationWandoosPlan>();
            var index = 0;
            foreach (var node in section.Children)
            {
                RequireObject(node, "Wandoos[" + index + "]");
                plans.Add(new AllocationWandoosPlan
                {
                    Time = ParseTime(Required(node, "Time", "Wandoos[" + index + "]"), "Wandoos[" + index + "].Time", false),
                    OS = ParseInteger(Required(node, "OS", "Wandoos[" + index + "]"), "Wandoos[" + index + "].OS", 0, 2)
                });
                index++;
            }
            return plans.OrderByDescending(x => x.Time).ToArray();
        }

        private static AllocationNguDifficultyPlan[] ParseNguDifficulties(JSONNode breakpoints)
        {
            var section = RequiredArray(breakpoints, "NGUDiff");
            var plans = new List<AllocationNguDifficultyPlan>();
            var index = 0;
            foreach (var node in section.Children)
            {
                RequireObject(node, "NGUDiff[" + index + "]");
                plans.Add(new AllocationNguDifficultyPlan
                {
                    Time = ParseTime(Required(node, "Time", "NGUDiff[" + index + "]"), "NGUDiff[" + index + "].Time", false),
                    Difficulty = ParseInteger(Required(node, "Diff", "NGUDiff[" + index + "]"), "NGUDiff[" + index + "].Diff", 0, 2)
                });
                index++;
            }
            return plans.OrderByDescending(x => x.Time).ToArray();
        }

        private static AllocationRebirthPlan ParseRebirth(JSONNode breakpoints)
        {
            var rebirth = Member(breakpoints, "Rebirth");
            var legacy = Member(breakpoints, "RebirthTime");
            if (rebirth != null && legacy != null)
                throw new FormatException("Specify Rebirth or RebirthTime, not both");
            if (rebirth == null && legacy == null)
                throw new FormatException("Breakpoints must contain Rebirth or RebirthTime");

            if (rebirth == null)
            {
                return new AllocationRebirthPlan
                {
                    Type = "TIME",
                    Target = ParseTime(legacy, "RebirthTime", true),
                    Challenges = new string[0],
                    UsesLegacyTime = true
                };
            }

            RequireObject(rebirth, "Rebirth");
            var typeNode = Required(rebirth, "Type", "Rebirth");
            if (!typeNode.IsString)
                throw new FormatException("Rebirth.Type must be a string");
            var type = typeNode.Value.ToUpperInvariant();
            if (type != "TIME" && type != "NUMBER" && type != "BOSSES")
                throw new FormatException("Rebirth.Type must be TIME, NUMBER, or BOSSES");
            var targetNode = Required(rebirth, "Target", "Rebirth");
            var target = type == "TIME"
                ? ParseTime(targetNode, "Rebirth.Target", false)
                : ParseFiniteNumber(targetNode, "Rebirth.Target");
            if (target <= 0.0)
                throw new FormatException("Rebirth.Target must be greater than zero");
            if (type == "TIME" && target > int.MaxValue)
                throw new FormatException("Rebirth.Target exceeds the runtime time horizon");

            var challengesNode = Member(rebirth, "Challenges");
            if (challengesNode == null || !challengesNode.IsArray)
                throw new FormatException("Rebirth.Challenges must be an array");
            var challenges = new List<string>();
            var challengeIndex = 0;
            foreach (var challengeNode in challengesNode.Children)
            {
                if (!challengeNode.IsString)
                    throw new FormatException("Rebirth.Challenges[" + challengeIndex + "] must be a string");
                var challenge = challengeNode.Value.ToUpperInvariant();
                ValidateChallenge(challenge, challengeIndex);
                if (!challenges.Contains(challenge)) challenges.Add(challenge);
                challengeIndex++;
            }
            return new AllocationRebirthPlan
            {
                Type = type,
                Target = target,
                Challenges = challenges.ToArray(),
                UsesLegacyTime = false
            };
        }

        private static void ValidatePriority(string priority, string path)
        {
            if (string.IsNullOrEmpty(priority))
                throw new FormatException(path + " cannot be empty");
            var core = priority;
            var colon = core.IndexOf(':');
            if (colon >= 0)
            {
                if (colon != core.LastIndexOf(':'))
                    throw new FormatException(path + " has more than one cap separator");
                int percent;
                if (!int.TryParse(core.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out percent)
                    || percent < 0 || percent > 100)
                    throw new FormatException(path + " cap percent must be an integer from 0 to 100");
                core = core.Substring(0, colon);
            }
            var kind = core;
            var dash = core.IndexOf('-');
            if (dash >= 0)
            {
                if (dash != core.LastIndexOf('-'))
                    throw new FormatException(path + " has more than one index separator");
                int index;
                if (!int.TryParse(core.Substring(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
                    || index < 0)
                    throw new FormatException(path + " index must be a non-negative integer");
                kind = core.Substring(0, dash);
            }
            if (!PriorityKinds.Contains(kind))
                throw new FormatException(path + " has unsupported priority '" + kind + "'");
        }

        private static void ValidateChallenge(string challenge, int index)
        {
            var dash = challenge.IndexOf('-');
            if (dash <= 0 || dash != challenge.LastIndexOf('-'))
                throw new FormatException("Rebirth.Challenges[" + index + "] must use TYPE-DIFFICULTY");
            var kind = challenge.Substring(0, dash);
            int difficulty;
            if (!ChallengeKinds.Contains(kind)
                || !int.TryParse(challenge.Substring(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out difficulty)
                || difficulty < 0)
                throw new FormatException("Rebirth.Challenges[" + index + "] is not a supported non-negative target");
        }

        private static JSONNode RequiredArray(JSONNode parent, string name)
        {
            var node = Member(parent, name);
            if (node == null || !node.IsArray)
                throw new FormatException("Breakpoints." + name + " must be an array");
            return node;
        }

        private static JSONNode Required(JSONNode parent, string name, string path)
        {
            var node = Member(parent, name);
            if (node == null)
                throw new FormatException(path + "." + name + " is required");
            return node;
        }

        private static JSONNode Member(JSONNode parent, string name)
        {
            if (parent == null || !parent.IsObject) return null;
            foreach (var pair in parent)
                if (string.Equals(pair.Key, name, StringComparison.Ordinal)) return pair.Value;
            return null;
        }

        private static void RequireObject(JSONNode node, string path)
        {
            if (node == null || !node.IsObject)
                throw new FormatException(path + " must be an object");
        }

        private static int[] ParseIntegerArray(JSONNode node, string path, int minimum, int maximum)
        {
            if (node == null || !node.IsArray)
                throw new FormatException(path + " must be an array");
            var result = new List<int>();
            var index = 0;
            foreach (var child in node.Children)
            {
                result.Add(ParseInteger(child, path + "[" + index + "]", minimum, maximum));
                index++;
            }
            return result.ToArray();
        }

        private static int ParseInteger(JSONNode node, string path, int minimum, int maximum)
        {
            var value = ParseFiniteNumber(node, path);
            if (value != Math.Truncate(value) || value < minimum || value > maximum)
                throw new FormatException(path + " must be an integer from " + minimum + " to " + maximum);
            return (int)value;
        }

        private static double ParseFiniteNumber(JSONNode node, string path)
        {
            if (node == null || !node.IsNumber)
                throw new FormatException(path + " must be a number");
            var value = node.AsDouble;
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new FormatException(path + " must be finite");
            return value;
        }

        private static double ParseTime(JSONNode node, string path, bool allowNegative)
        {
            double seconds;
            if (node != null && node.IsNumber)
            {
                seconds = ParseFiniteNumber(node, path);
            }
            else if (node != null && node.IsObject)
            {
                seconds = 0.0;
                var count = 0;
                foreach (var pair in node)
                {
                    var unit = pair.Key.ToLowerInvariant();
                    if (unit != "h" && unit != "m" && unit != "s")
                        throw new FormatException(path + " contains unsupported unit '" + pair.Key + "'");
                    var value = ParseFiniteNumber(pair.Value, path + "." + pair.Key);
                    seconds += value * (unit == "h" ? 3600.0 : unit == "m" ? 60.0 : 1.0);
                    count++;
                }
                if (count == 0) throw new FormatException(path + " time object cannot be empty");
            }
            else
            {
                throw new FormatException(path + " must be a number or h/m/s object");
            }
            if (!allowNegative && seconds < 0.0)
                throw new FormatException(path + " cannot be negative");
            if (double.IsNaN(seconds) || double.IsInfinity(seconds))
                throw new FormatException(path + " must be finite");
            return Math.Truncate(seconds);
        }

        private static bool HasOneCompleteJsonRoot(string source, out string error)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                error = "allocation JSON is empty";
                return false;
            }
            var first = 0;
            while (first < source.Length && char.IsWhiteSpace(source[first])) first++;
            if (first >= source.Length || source[first] != '{')
            {
                error = "allocation JSON must begin with an object";
                return false;
            }
            return new StrictJsonReader(source).Validate(out error);
        }

        private sealed class StrictJsonReader
        {
            private readonly string _source;
            private int _position;

            internal StrictJsonReader(string source)
            {
                _source = source;
            }

            internal bool Validate(out string error)
            {
                try
                {
                    SkipWhitespace();
                    ReadValue();
                    SkipWhitespace();
                    if (_position != _source.Length)
                        throw Invalid("contains data after its root object");
                    error = string.Empty;
                    return true;
                }
                catch (FormatException invalid)
                {
                    error = invalid.Message;
                    return false;
                }
            }

            private void ReadValue()
            {
                SkipWhitespace();
                if (_position >= _source.Length) throw Incomplete();
                switch (_source[_position])
                {
                    case '{': ReadObject(); return;
                    case '[': ReadArray(); return;
                    case '"': ReadString(); return;
                    case 't': ReadLiteral("true"); return;
                    case 'f': ReadLiteral("false"); return;
                    case 'n': ReadLiteral("null"); return;
                    default:
                        if (_source[_position] == '-' || IsDigit(_source[_position]))
                        {
                            ReadNumber();
                            return;
                        }
                        throw Invalid("contains an invalid value");
                }
            }

            private void ReadObject()
            {
                _position++;
                SkipWhitespace();
                if (Take('}')) return;
                while (true)
                {
                    if (_position >= _source.Length) throw Incomplete();
                    if (_source[_position] != '"') throw Invalid("object member name must be a string");
                    ReadString();
                    SkipWhitespace();
                    if (!Take(':')) throw Invalid("object member must contain ':'");
                    ReadValue();
                    SkipWhitespace();
                    if (Take('}')) return;
                    if (!Take(',')) throw Invalid("object members must be separated by ','");
                    SkipWhitespace();
                }
            }

            private void ReadArray()
            {
                _position++;
                SkipWhitespace();
                if (Take(']')) return;
                while (true)
                {
                    ReadValue();
                    SkipWhitespace();
                    if (Take(']')) return;
                    if (!Take(',')) throw Invalid("array values must be separated by ','");
                    SkipWhitespace();
                }
            }

            private void ReadString()
            {
                _position++;
                while (_position < _source.Length)
                {
                    var current = _source[_position++];
                    if (current == '"') return;
                    if (current < 0x20) throw Invalid("contains an unescaped control character");
                    if (current != '\\') continue;
                    if (_position >= _source.Length) throw Incomplete();
                    var escape = _source[_position++];
                    if (escape == '"' || escape == '\\' || escape == '/' || escape == 'b'
                        || escape == 'f' || escape == 'n' || escape == 'r' || escape == 't') continue;
                    if (escape != 'u') throw Invalid("contains an invalid string escape");
                    for (var i = 0; i < 4; i++)
                    {
                        if (_position >= _source.Length) throw Incomplete();
                        if (!IsHex(_source[_position++])) throw Invalid("contains an invalid unicode escape");
                    }
                }
                throw Incomplete();
            }

            private void ReadNumber()
            {
                Take('-');
                if (_position >= _source.Length) throw Incomplete();
                if (Take('0'))
                {
                    if (_position < _source.Length && IsDigit(_source[_position]))
                        throw Invalid("number has a leading zero");
                }
                else
                {
                    if (!IsOneToNine(_source[_position])) throw Invalid("contains an invalid number");
                    while (_position < _source.Length && IsDigit(_source[_position])) _position++;
                }
                if (Take('.'))
                {
                    if (_position >= _source.Length) throw Incomplete();
                    if (!IsDigit(_source[_position])) throw Invalid("fraction requires digits");
                    while (_position < _source.Length && IsDigit(_source[_position])) _position++;
                }
                if (_position < _source.Length && (_source[_position] == 'e' || _source[_position] == 'E'))
                {
                    _position++;
                    if (_position < _source.Length && (_source[_position] == '+' || _source[_position] == '-')) _position++;
                    if (_position >= _source.Length) throw Incomplete();
                    if (!IsDigit(_source[_position])) throw Invalid("exponent requires digits");
                    while (_position < _source.Length && IsDigit(_source[_position])) _position++;
                }
            }

            private void ReadLiteral(string literal)
            {
                for (var i = 0; i < literal.Length; i++)
                {
                    if (_position >= _source.Length) throw Incomplete();
                    if (_source[_position++] != literal[i]) throw Invalid("contains an invalid literal");
                }
            }

            private void SkipWhitespace()
            {
                while (_position < _source.Length && (_source[_position] == ' '
                       || _source[_position] == '\t' || _source[_position] == '\r'
                       || _source[_position] == '\n')) _position++;
            }

            private bool Take(char expected)
            {
                if (_position >= _source.Length || _source[_position] != expected) return false;
                _position++;
                return true;
            }

            private FormatException Incomplete()
            {
                return Invalid("is torn or structurally incomplete");
            }

            private FormatException Invalid(string detail)
            {
                return new FormatException("allocation JSON " + detail + " at offset " + _position);
            }

            private static bool IsDigit(char value) { return value >= '0' && value <= '9'; }
            private static bool IsOneToNine(char value) { return value >= '1' && value <= '9'; }
            private static bool IsHex(char value)
            {
                return IsDigit(value) || value >= 'a' && value <= 'f' || value >= 'A' && value <= 'F';
            }
        }

        private static string Fingerprint(string source)
        {
            unchecked
            {
                uint hash = 2166136261;
                for (var i = 0; i < source.Length; i++)
                {
                    hash ^= source[i];
                    hash *= 16777619;
                }
                return hash.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        private static AllocationPlanCompilation Failure(string error)
        {
            return new AllocationPlanCompilation
            {
                Success = false,
                Plan = null,
                Error = string.IsNullOrEmpty(error) ? "allocation plan was rejected" : error
            };
        }
    }
}
