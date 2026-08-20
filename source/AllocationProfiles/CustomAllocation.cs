using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NGUInjector.AllocationProfiles.BreakpointTypes;
using NGUInjector.AllocationProfiles.RebirthStuff;
using NGUInjector.Autopilot;
using NGUInjector.Managers;
using UnityEngine;

/*
FILE PURPOSE

CustomAllocation parses generated/user breakpoint profiles and executes the fast resource sweep.
It reconciles combined budgets across Basic Training, Augments, Wandoos, Time Machine, NGUs,
rituals, hacks, and wishes. After every valued finite target declines, otherwise-idle Energy,
Magic, and Resource 3 are reclaimed into an unlocked no-currency persistent/reset-local sink;
Resource 3 prefers a live Hack and may fall back to a valid Wish. The accepted native delta or
exact topology blocker is reported. Allocation calls run on Unity's main thread; actual deltas,
sync-pair costs, caps, and rebirth horizon must remain authoritative. A fallback must never start a
paid Time Machine bar or debit Gold, and the outer allocation transaction proves the complete
accepted target vector rather than trusting this local idle delta.
The Wandoos selector/method pair crosses the build-pinned NativeBindingRegistry and is accepted
only after the native OS plus level-reset postcondition is observed; name-only reflection is never
an executable fallback.
*/
namespace NGUInjector.AllocationProfiles
{
    [Serializable]
    internal class CustomAllocation : AllocationProfile
    {
        private static long _installationVersionClock;
        private BreakpointWrapper _wrapper;
        private readonly AllocationPlanSlot _planSlot = new AllocationPlanSlot();
        private long _materializedPlanVersion;
        private AllocationBreakPoint _currentMagicBreakpoint;
        private AllocationBreakPoint _currentEnergyBreakpoint;
        private AllocationBreakPoint _currentR3Breakpoint;
        private GearBreakpoint _currentGearBreakpoint;
        private DiggerBreakpoint _currentDiggerBreakpoint;
        private WandoosBreakpoint _currentWandoosBreakpoint;
        private NGUDiffBreakpoint _currentNguBreakpoint;
        private bool _hasGearSwapped;
        private bool _hasDiggerSwapped;
        private bool _hasWandoosSwapped;
        private bool _hasNGUSwapped;
        private readonly string _allocationPath;
        private readonly string _profileName;
        private string _lastEnergyActionShape = string.Empty;
        private DateTime _lastEnergyActionLog = DateTime.MinValue;
        private string _lastMagicActionShape = string.Empty;
        private DateTime _lastMagicActionLog = DateTime.MinValue;
        private string _lastR3ActionShape = string.Empty;
        private DateTime _lastR3ActionLog = DateTime.MinValue;

        internal static string LastMagicAllocationDecision { get; private set; }
            = "Magic allocation has not completed a verified sweep yet";
        internal static string LastR3AllocationDecision { get; private set; }
            = "Resource 3 allocation has not completed a verified sweep yet";

        internal bool IsAllocationRunning;
        internal long InstalledPlanVersion => _planSlot.Current == null
            ? 0
            : _planSlot.Current.InstallationVersion;
        internal string InstalledPlanFingerprint => _planSlot.Current == null
            ? string.Empty
            : _planSlot.Current.Fingerprint;

        public CustomAllocation(string profilesDir, string profile)
        {
            _allocationPath = Path.Combine(profilesDir, profile + ".json");
            _profileName = profile;
        }

        internal void ReloadAllocation()
        {
            var emptyAllocation = @"{
    ""Breakpoints"": {
      ""Magic"": [{""Time"": 0, ""Priorities"": []}],
      ""Energy"": [{""Time"": 0, ""Priorities"": []}],
      ""R3"": [{""Time"": 0, ""Priorities"": []}],
      ""Gear"": [{""Time"": 0, ""ID"": []}],
      ""Wandoos"": [{""Time"": 0, ""OS"": 0}],
      ""Diggers"": [{""Time"": 0, ""List"": []}],
      ""NGUDiff"": [{""Time"": 0, ""Diff"": 0}],
      ""RebirthTime"": -1
    }
  }";

            try
            {
                if (!File.Exists(_allocationPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_allocationPath));
                    File.WriteAllText(_allocationPath, emptyAllocation);
                    Main.Log("Created empty allocation profile. Please update allocation.json");
                }

                string text;
                string readError;
                if (!TryReadStable(_allocationPath, out text, out readError))
                {
                    RejectReload(readError);
                    return;
                }

                string compileError;
                if (!_planSlot.TryInstall(text, out compileError))
                {
                    // A watcher can observe the truncate/write interval used by many editors. The
                    // currently installed plan and its durable shadow remain untouched on rejection.
                    if (_planSlot.Current == null && File.Exists(LastGoodPath))
                    {
                        string lastGood;
                        string shadowError;
                        if (TryReadStable(LastGoodPath, out lastGood, out shadowError)
                            && _planSlot.TryInstall(lastGood, out shadowError))
                        {
                            AssignGlobalInstallationVersion();
                            Main.Log("Allocation source was invalid; restored last-good plan v"
                                     + InstalledPlanVersion + " from durable shadow: " + compileError);
                            return;
                        }
                    }
                    RejectReload(compileError);
                    return;
                }

                AssignGlobalInstallationVersion();
                try
                {
                    PersistLastGood(text);
                }
                catch (Exception shadowError)
                {
                    // The in-memory candidate is already proven and remains usable. File.Replace
                    // leaves the prior durable plan intact, so a shadow I/O failure is observable
                    // but cannot destroy either last-good copy.
                    Main.Log("Allocation plan v" + InstalledPlanVersion
                             + " installed; durable last-good shadow retained its prior version: "
                             + shadowError.Message);
                }
                Main.Log(BuildAllocationString(_planSlot.Current));
                // Deliberately do not materialize breakpoints or execute DoAllocations here.
                // Full-mode execution lazily installs the version on its first coordinated sweep;
                // dry-run/assist startup and hot reload therefore invoke zero game controllers.
            }
            catch (Exception error)
            {
                RejectReload(error.GetType().Name + ": " + error.Message);
            }
        }

        private string LastGoodPath => _allocationPath + ".last-good";

        private void RejectReload(string error)
        {
            Main.Log("Rejected allocation reload; retaining last-good memory/disk plan v"
                     + InstalledPlanVersion + ": " + error);
        }

        private void AssignGlobalInstallationVersion()
        {
            _planSlot.Current.InstallationVersion = Interlocked.Increment(ref _installationVersionClock);
        }

        private static bool TryReadStable(string path, out string text, out string error)
        {
            text = string.Empty;
            error = string.Empty;
            try
            {
                var before = new FileInfo(path);
                before.Refresh();
                var beforeLength = before.Length;
                var beforeWrite = before.LastWriteTimeUtc;
                text = File.ReadAllText(path);
                var after = new FileInfo(path);
                after.Refresh();
                if (beforeLength != after.Length || beforeWrite != after.LastWriteTimeUtc)
                {
                    error = "allocation file changed while it was being read";
                    return false;
                }
                return true;
            }
            catch (Exception readError)
            {
                error = "allocation file read failed: " + readError.Message;
                return false;
            }
        }

        private void PersistLastGood(string text)
        {
            var temporary = LastGoodPath + ".tmp";
            File.WriteAllText(temporary, text);
            if (File.Exists(LastGoodPath))
                File.Replace(temporary, LastGoodPath, null);
            else
                File.Move(temporary, LastGoodPath);
        }

        private bool EnsureRuntimePlan()
        {
            var plan = _planSlot.Current;
            if (plan == null) return false;
            if (_materializedPlanVersion == plan.InstallationVersion && _wrapper != null) return true;

            try
            {
                var candidate = BuildRuntimeWrapper(plan);
                _wrapper = candidate;
                _materializedPlanVersion = plan.InstallationVersion;
                ResetRuntimeBreakpointState();
                Main.Log("Materialized allocation plan v" + _materializedPlanVersion
                         + " (" + plan.Fingerprint + ") for coordinated execution");
                return true;
            }
            catch (Exception error)
            {
                Main.Log("Rejected runtime materialization for allocation plan v"
                         + plan.InstallationVersion + ": " + error.Message);
                return _wrapper != null;
            }
        }

        private static BreakpointWrapper BuildRuntimeWrapper(CompiledAllocationPlan plan)
        {
            var allocationRebirthTime = plan.Rebirth.Type == "TIME" ? (int)plan.Rebirth.Target : -1;
            BaseRebirth rebirth;
            if (plan.Rebirth.UsesLegacyTime && allocationRebirthTime <= 0)
                rebirth = new NoRebirth();
            else
                rebirth = BaseRebirth.CreateRebirth(plan.Rebirth.Target, plan.Rebirth.Type,
                    plan.Rebirth.Challenges);

            return new BreakpointWrapper
            {
                Breakpoints = new Breakpoints
                {
                    Rebirth = rebirth,
                    Energy = BuildResourceBreakpoints(plan.Energy, ResourceType.Energy, allocationRebirthTime),
                    Magic = BuildResourceBreakpoints(plan.Magic, ResourceType.Magic, allocationRebirthTime),
                    R3 = BuildResourceBreakpoints(plan.R3, ResourceType.R3, allocationRebirthTime),
                    Gear = plan.Gear.Select(x => new GearBreakpoint {Time = x.Time, Gear = x.Gear}).ToArray(),
                    Diggers = plan.Diggers.Select(x => new DiggerBreakpoint {Time = x.Time, Diggers = x.Diggers}).ToArray(),
                    Wandoos = plan.Wandoos.Select(x => new WandoosBreakpoint {Time = x.Time, OS = x.OS}).ToArray(),
                    NGUBreakpoints = plan.NguDifficulties.Select(x => new NGUDiffBreakpoint
                        {Time = x.Time, Diff = x.Difficulty}).ToArray()
                }
            };
        }

        private static AllocationBreakPoint[] BuildResourceBreakpoints(
            IEnumerable<AllocationResourcePlan> plans, ResourceType type, int rebirthTime)
        {
            return plans.Select(x => new AllocationBreakPoint
            {
                Time = x.Time,
                Priorities = BaseBreakpoint.ParseBreakpointArray(x.Priorities, type, rebirthTime)
                    .Where(priority => priority != null).ToArray()
            }).ToArray();
        }

        private void ResetRuntimeBreakpointState()
        {
            _currentDiggerBreakpoint = null;
            _currentEnergyBreakpoint = null;
            _currentGearBreakpoint = null;
            _currentWandoosBreakpoint = null;
            _currentMagicBreakpoint = null;
            _currentR3Breakpoint = null;
            _currentNguBreakpoint = null;
            _hasGearSwapped = false;
            _hasDiggerSwapped = false;
            _hasWandoosSwapped = false;
            _hasNGUSwapped = false;
        }

        private string BuildAllocationString(CompiledAllocationPlan plan)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Loaded Custom Allocation from profile '" + _profileName + "' as plan v"
                               + plan.InstallationVersion + " (" + plan.Fingerprint + ")");
            builder.AppendLine(plan.Energy.Length + " Energy Breakpoints");
            builder.AppendLine(plan.Magic.Length + " Magic Breakpoints");
            builder.AppendLine(plan.R3.Length + " R3 Breakpoints");
            builder.AppendLine(plan.Gear.Length + " Gear Breakpoints");
            builder.AppendLine(plan.Diggers.Length + " Digger Breakpoints");
            builder.AppendLine(plan.Wandoos.Length + " Wandoos Breakpoints");
            builder.AppendLine(plan.NguDifficulties.Length + " NGU Difficulty Breakpoints");
            builder.AppendLine(plan.Rebirth.UsesLegacyTime && plan.Rebirth.Target <= 0
                ? "Rebirth Disabled."
                : "Rebirth " + plan.Rebirth.Type + " target " + plan.Rebirth.Target);
            if (plan.Rebirth.Challenges.Length > 0)
                builder.AppendLine("Challenge targets: " + string.Join(",", plan.Rebirth.Challenges));

            return builder.ToString();
        }

        internal void SwapNGUDiff()
        {
            if (!EnsureRuntimePlan())
                return;
            var bp = GetCurrentNGUDiffBreakpoint();
            if (bp == null)
                return;

            if (bp.Time != _currentNguBreakpoint.Time)
            {
                _hasNGUSwapped = false;
            }

            if (_hasNGUSwapped)
                return;

            if (bp.Diff == 0)
            {
                _character.settings.nguLevelTrack = difficulty.normal;
                if (_character.settings.nguLevelTrack == difficulty.normal)
                {
                    _hasNGUSwapped = true;
                }
            }
            else if (bp.Diff == 1 && (_character.settings.rebirthDifficulty == difficulty.evil ||
                                      _character.settings.rebirthDifficulty == difficulty.sadistic))
            {
                _character.settings.nguLevelTrack = difficulty.evil;
                if (_character.settings.nguLevelTrack == difficulty.evil)
                {
                    _hasNGUSwapped = true;
                }
            }
            else if (bp.Diff == 2 && _character.settings.rebirthDifficulty == difficulty.sadistic)
            {
                _character.settings.nguLevelTrack = difficulty.sadistic;
                if (_character.settings.nguLevelTrack == difficulty.sadistic)
                {
                    _hasNGUSwapped = true;
                }
            }

            _character.NGUController.refreshMenu();
        }

        internal void SwapOS()
        {
            if (!EnsureRuntimePlan())
                return;
            var bp = GetCurrentWandoosBreakpoint();
            if (bp == null)
                return;

            if (bp.Time != _currentWandoosBreakpoint.Time)
            {
                _hasWandoosSwapped = false;
            }

            if (_hasWandoosSwapped) return;

            var id = bp.OS;
            var target = id == 2 ? OSType.wandoosXL : id == 1 ? OSType.wandoosMEH : OSType.wandoos98;
            if (_character.wandoos98.os == target)
            {
                _hasWandoosSwapped = true;
                return;
            }
            if (id == 1 && !_character.inventory.itemList.jakeComplete
                || id == 2 && _character.wandoos98.XLLevels <= 0)
            {
                Main.LogAction("REJECTED", "Wandoos OS switch target is not unlocked");
                return;
            }

            var controller = Main.Character.wandoos98Controller;
            var energyBefore = _character.wandoos98.energyLevel;
            var magicBefore = _character.wandoos98.magicLevel;
            var native = NativeBindingRegistry.Create(typeof(Character).Assembly,
                Main.GameAssemblySha256).CreateMutationAdapters();
            var invocation = native.SwitchWandoosOperatingSystem(controller, id);
            if (!invocation.ReturnedNormally)
            {
                Main.LogAction("REJECTED", "Wandoos OS switch native transition held: "
                                           + invocation.Status + " — " + invocation.Reason);
                return;
            }
            var confirmed = _character.wandoos98.os == target
                            && _character.wandoos98.energyLevel == 0
                            && _character.wandoos98.magicLevel == 0;
            _hasWandoosSwapped = confirmed;
            Main.LogAction(confirmed ? "WANDOOS" : "REJECTED",
                confirmed
                    ? "Changed Wandoos OS to " + target + " [confirmed; reset levels "
                      + energyBefore + "/" + magicBefore + "]"
                    : "Wandoos OS switch produced no verified OS/reset delta");
            controller.refreshMenu();
        }

        public void DoRebirth()
        {
            if (!EnsureRuntimePlan())
                return;

            if (_wrapper.Breakpoints.Rebirth.RebirthAvailable())
            {
                if (_character.bossController.isFighting || _character.bossController.nukeBoss)
                {
                    Main.Log("Delaying rebirth while boss fight is in progress");
                    return;
                }
            }
            else
            {
                return;
            }

            if (_wrapper.Breakpoints.Rebirth.DoRebirth())
            {
                ResetRuntimeBreakpointState();
            }
        }

        public void CastBloodSpells()
        {
            CastBloodSpells(false);
        }

        public void CastBloodSpells(bool rebirth)
        {
            if (!EnsureRuntimePlan())
                return;
            if (!Main.Settings.CastBloodSpells && !Main.AutopilotWants(x => x.ManageBloodMagic))
                return;

            if (_wrapper.Breakpoints.Rebirth is TimeRebirth trb
                && (Main.Settings.AutoRebirth || Main.AutopilotWants(x => x.AllowRebirths)))
            {
                if (trb.RebirthTime - _character.rebirthTime.totalseconds < 30 * 60 && !rebirth)
                {
                    return;
                }
            }

            float iron = 0;
            long mcguffA = 0;
            long mcguffB = 0;
            if (Main.Settings.BloodMacGuffinBThreshold > 0)
            {
                if (_character.adventure.itopod.perkLevel[73] >= 1L &&
                    _character.settings.rebirthDifficulty >= difficulty.evil)
                {
                    if (_character.bloodMagic.macguffin2Time.totalseconds > _character.bloodMagicController.spells.macguffin2Cooldown)
                    {
                        if (_character.bloodMagic.bloodPoints >= _character.bloodSpells.minMacguffin2Blood())
                        {
                            var a = _character.bloodMagic.bloodPoints / _character.bloodSpells.minMacguffin2Blood();
                            mcguffB = (int) (Math.Log(a, 20.0) + 1.0);
                        }

                        if (Main.Settings.BloodMacGuffinBThreshold <= mcguffB)
                        {
                            _character.bloodSpells.castMacguffin2Spell();
                            Main.LogPitSpin("Casting Blood MacGuffin β power @ " + mcguffB);
                            return;
                        }
                        else
                        {
                            if (rebirth)
                            {
                                Main.Log("Casting Failed Blood MacGuffin β - Insufficient Power " + mcguffB +
                                         " of " + Main.Settings.BloodMacGuffinBThreshold);
                            }
                        }
                    }
                }
            }

            if (Main.Settings.BloodMacGuffinAThreshold > 0)
            {
                if (_character.adventure.itopod.perkLevel[72] >= 1L)
                {
                    if (_character.bloodMagic.macguffin1Time.totalseconds >= _character.bloodMagicController.spells.macguffin1Cooldown)
                    {
                        if (_character.bloodMagic.bloodPoints > _character.bloodSpells.minMacguffin1Blood())
                        {
                            var a = _character.bloodMagic.bloodPoints / _character.bloodSpells.minMacguffin1Blood();
                            mcguffA = (int) ((Math.Log(a, 10.0) + 1.0) *
                                             _character.wishesController.totalBloodGuffbonus());
                        }
                        if (Main.Settings.BloodMacGuffinAThreshold <= mcguffA)
                        {
                            _character.bloodSpells.castMacguffin1Spell();
                            Main.LogPitSpin("Casting Blood MacGuffin α power @ " + mcguffA);
                            return;
                        }
                        else
                        {
                            if (rebirth)
                            {
                                Main.Log("Casting Failed Blood MacGuffin α - Insufficient Power " + mcguffA +
                                         " of " + Main.Settings.BloodMacGuffinAThreshold);
                            }
                        }
                    }
                }
            }

            if (Main.Settings.IronPillThreshold > 100)
            {
                if (_character.bloodMagic.adventureSpellTime.totalseconds >
                    _character.bloodSpells.adventureSpellCooldown)
                {
                    if (_character.bloodMagic.bloodPoints > _character.bloodSpells.minAdventureBlood())
                    {
                        iron = (float) Math.Floor(Math.Pow(_character.bloodMagic.bloodPoints, 0.25));
                        if (_character.settings.rebirthDifficulty >= difficulty.evil)
                        {
                            iron *= _character.adventureController.itopod.ironPillBonus();
                        }
                    }

                    if (Main.Settings.IronPillThreshold <= iron)
                    {
                        _character.bloodSpells.castAdventurePowerupSpell();
                        Main.LogPitSpin("Casting Iron Blood Spell power @ " + iron);
                    }
                    else
                    {
                        if (rebirth)
                        {
                            Main.Log("Casting Failed Iron Blood Spell - Insufficient Power " + iron + " of " +
                                     Main.Settings.IronPillThreshold);
                        }
                    }
                }
            }
        }

        public override void AllocateEnergy()
        {
            if (!EnsureRuntimePlan())
                return;

            var bp = GetCurrentBreakpoint(true);
            var temp = new List<BaseBreakpoint>();
            if (bp != null)
            {
                if (_currentEnergyBreakpoint == null || bp.Time != _currentEnergyBreakpoint.Time)
                    _currentEnergyBreakpoint = bp;
                if (bp.Priorities != null)
                    temp = bp.Priorities.Where(x => x.IsValid()).ToList();
            }
            if (temp.Any(x => x is BasicTrainingBP))
            {
                var optimizedTraining = temp.OfType<BasicTrainingBP>()
                    .OrderByDescending(x => x.PriorityScore)
                    .Cast<BaseBreakpoint>();
                temp = optimizedTraining.Concat(temp.Where(x => !(x is BasicTrainingBP))).ToList();
            }
            var prioCount = temp.Count(x => !x.IsCapPrio());
            

            // This allocator owns the complete profile. removeMostEnergy leaves
            // Basic Training untouched, which can carry a stale prior profile into
            // one that intentionally contains no BT target.
            _character.removeAllEnergy();

            var cappedTraining = temp.OfType<BasicTrainingBP>().ToList();
            var optimizeCappedTraining = cappedTraining.Count > 0
                                         && cappedTraining.All(x => x.IsCapPrio());
            var optimizedTrainingSpent = 0L;
            var unlockFrontierSpent = 0L;
            var longHorizonTrainingSpent = 0L;
            var remainingTrainingBudget = 0L;
            if (optimizeCappedTraining)
            {
                // CAPALLBT is one portfolio ceiling.  The first claim on it is the currently
                // reachable ability frontier: speed-cap that row only when the exact native
                // completion leaves at least two minutes to use the new ability/AT unlock.
                var fraction = Math.Min(1.0, cappedTraining.Sum(x => x.ConfiguredFraction));
                remainingTrainingBudget = (long)Math.Ceiling(_character.curEnergy * fraction);
                foreach (var training in cappedTraining
                             .Where(x => x.UnlockFrontierReservation > 0L)
                             .OrderBy(x => x.UnlockFrontierSeconds))
                {
                    if (remainingTrainingBudget <= 0L || _character.idleEnergy <= 0L) break;
                    var reservation = training.UnlockFrontierReservation;
                    if (reservation > remainingTrainingBudget || reservation > _character.idleEnergy)
                        continue;
                    var spent = training.AllocateUnlockFrontier(remainingTrainingBudget);
                    optimizedTrainingSpent += spent;
                    unlockFrontierSpent += spent;
                    remainingTrainingBudget -= spent;
                }
            }

            /*
            FINITE ADVENTURE-GATE RESERVATION

            Once the current Basic Training ability frontier is funded, a valid
            AdvancedTrainingBP may reserve the exact Power/Toughness allocation that opens the
            next zone and still leaves a productive farm window before rebirth.
            */
            var cappedAdvancedTraining = temp.OfType<AdvancedTrainingBP>()
                .Where(x => x.IsCapPrio()).ToList();
            var advancedTrainingSpent = 0L;
            foreach (var training in cappedAdvancedTraining)
            {
                if (_character.idleEnergy <= 0) break;
                var before = _character.idleEnergy;
                training.Allocate();
                advancedTrainingSpent += Math.Max(0L, before - _character.idleEnergy);
            }

            if (optimizeCappedTraining)
            {
                // Preserve the planner's aggregate BT budget, but solve its internal
                // distribution by exact current marginal value after the ability frontier.

                // Fund persistent cap-compression investments before the immediate
                // boss derivative.  This prevents a high-cap newly unlocked row from
                // remaining at zero forever merely because its very first point has
                // weak local value.  Unreachable or >2-run-payback events reserve 0.
                foreach (var training in cappedTraining
                             .Where(x => x.LongHorizonReservation > 0)
                             .OrderBy(x => x.LongHorizonPaybackRuns))
                {
                    if (remainingTrainingBudget <= 0 || _character.idleEnergy <= 0) break;
                    var reservation = training.LongHorizonReservation;
                    if (reservation > remainingTrainingBudget || reservation > _character.idleEnergy)
                        continue;
                    var spent = training.AllocateLongHorizonReservation(remainingTrainingBudget);
                    optimizedTrainingSpent += spent;
                    longHorizonTrainingSpent += spent;
                    remainingTrainingBudget -= spent;
                }
                foreach (var training in cappedTraining.OrderByDescending(x => x.PriorityScore))
                {
                    if (remainingTrainingBudget <= 0 || _character.idleEnergy <= 0) break;
                    var spent = training.AllocateResidual(Math.Min(remainingTrainingBudget, _character.idleEnergy));
                    optimizedTrainingSpent += spent;
                    remainingTrainingBudget -= spent;
                }
            }

            var toAdd = prioCount > 0
                ? (long)Math.Ceiling((double)_character.idleEnergy / prioCount)
                : 0L;
            SetInput(toAdd);

            foreach (var prio in temp)
            {
                if (optimizeCappedTraining && prio is BasicTrainingBP)
                    continue;
                if (cappedAdvancedTraining.Contains(prio as AdvancedTrainingBP))
                    continue;
                if (!prio.IsCapPrio())
                {
                    prioCount--;
                }

                if (prio.Allocate())
                {
                    toAdd = prioCount > 0
                        ? (long)Math.Ceiling((double)_character.idleEnergy / prioCount)
                        : 0L;
                    SetInput(toAdd);
                }
            }

            // First fund the next exact BT event inside the configured portfolio
            // ceiling. This keeps the normal 5 Hz marginal reranking behavior.
            var swept = 0L;
            if (_character.idleEnergy > 0)
            {
                foreach (var training in temp.OfType<BasicTrainingBP>()
                             .OrderByDescending(x => x.PriorityScore))
                {
                    if (_character.idleEnergy <= 0) break;
                    swept += training.AllocateResidual(_character.idleEnergy);
                }
            }

            // Once every configured Energy sink has declined, idle Energy has no
            // opportunity cost. Fill all remaining productive Basic Training
            // headroom, ignoring the portfolio ceiling but never exceeding a native
            // one-level-per-tick speed cap.
            var idleFallback = 0L;
            if (_character.idleEnergy > 0)
            {
                foreach (var training in temp.OfType<BasicTrainingBP>()
                             .OrderByDescending(x => x.PriorityScore))
                {
                    if (_character.idleEnergy <= 0) break;
                    idleFallback += training.AllocateIdleFallback(_character.idleEnergy);
                }
            }

            /*
            UNIVERSAL NO-CURRENCY FALLBACK

            A capped profile is not permission to leave a productive resource idle.  NGU is
            persistent and therefore preferred; Wandoos is used only when its feature and current
            installation are live. Both allocations go through native add controllers and are
            measured from the idle-pool delta. Time Machine is deliberately absent because a new
            bar can spend Gold. The enclosing ResourceAllocationIntent independently seals and
            verifies the complete post-sweep target vector.
            */
            string universalFallbackDecision;
            var universalFallback = _character.idleEnergy > 0
                ? AllocateEnergyNoCurrencyFallback(out universalFallbackDecision)
                : NoFallbackNeeded(out universalFallbackDecision);

            _character.NGUController.refreshMenu();
            _character.wandoos98Controller.refreshMenu();
            _character.advancedTrainingController.refresh();
            _character.timeMachineController.updateMenu();
            _character.allOffenseController.refresh();
            _character.allDefenseController.refresh();
            _character.wishesController.updateMenu();
            _character.augmentsController.updateMenu();
            var priorityKinds = string.Join(", ", temp.Select(x => x is BasicTrainingBP
                ? ((BasicTrainingBP)x).Label
                : x.GetType().Name).Distinct().ToArray());
            var actionShape = temp.Count + ":" + priorityKinds
                              + ":no-currency=" + universalFallbackDecision;
            // Allocation still executes at 5 Hz. Coalesce identical telemetry so
            // synchronous AutoFlush I/O does not become part of the optimizer cost;
            // target-set changes are always logged immediately.
            if (actionShape != _lastEnergyActionShape
                || (DateTime.UtcNow - _lastEnergyActionLog).TotalSeconds >= 2.0)
            {
                Main.LogAction("ALLOC", "Rebalanced Energy across " + temp.Count + " targets: " + priorityKinds
                                        + "; idle=" + _character.idleEnergy
                                        + (optimizedTrainingSpent > 0 ? ", optimizedBT=" + optimizedTrainingSpent : string.Empty)
                                        + (unlockFrontierSpent > 0 ? ", ability-frontier=" + unlockFrontierSpent : string.Empty)
                                        + (longHorizonTrainingSpent > 0 ? ", persistent-cap-reserve=" + longHorizonTrainingSpent : string.Empty)
                                        + (advancedTrainingSpent > 0 ? ", next-zone-AT=" + advancedTrainingSpent : string.Empty)
                                        + (swept > 0 ? ", event-residual->BT=" + swept : string.Empty)
                                        + (idleFallback > 0 ? ", idle-fallback->BT=" + idleFallback : string.Empty)
                                        + (universalFallback > 0
                                            ? ", universal-no-currency->" + universalFallbackDecision
                                              + "=" + universalFallback
                                            : string.Empty)
                                        + (_character.idleEnergy > 0
                                            ? ", idle-topology-blocker=" + universalFallbackDecision
                                            : string.Empty));
                _lastEnergyActionShape = actionShape;
                _lastEnergyActionLog = DateTime.UtcNow;
            }
        }

        public override void AllocateMagic()
        {
            if (!EnsureRuntimePlan())
                return;

            var bp = GetCurrentBreakpoint(false);
            var temp = new List<BaseBreakpoint>();
            if (bp != null)
            {
                if (_currentMagicBreakpoint == null || bp.Time != _currentMagicBreakpoint.Time)
                    _currentMagicBreakpoint = bp;
                if (bp.Priorities != null)
                    temp = bp.Priorities.Where(x => x.IsValid()).ToList();
            }
            var prioCount = temp.Count(x => !x.IsCapPrio());

            if (temp.Count == 0)
                _character.removeAllMagic();
            else
                _character.removeMostMagic();
            var toAdd = prioCount > 0
                ? (long)Math.Ceiling((double)_character.magic.idleMagic / prioCount)
                : 0L;
            SetInput(toAdd);

            foreach (var prio in temp)
            {
                if (!prio.IsCapPrio())
                {
                    prioCount--;
                }

                if (prio.Allocate())
                {
                    toAdd = prioCount > 0
                        ? (long)Math.Ceiling((double)_character.magic.idleMagic / prioCount)
                        : 0L;
                    SetInput(toAdd);
                }
            }

            /*
            PAID TIME-MACHINE RESIDUAL

            Blood can be temporarily Gold-blocked between fast ritual completions. During an
            unscheduled rebirth hold, otherwise-idle Magic has no opportunity cost, but starting
            a fresh Time Machine level can consume Gold and may never repay before reset. Continue
            only an already-paid (progress > 0) gold-multiplier bar with the exact idle remainder.
            The next sweep reclaims it synchronously as soon as Blood accepts Magic again. Native
            addMagic performs the mutation; the observed allocation delta is the only reported spend.
            */
            var residualTimeMachine = 0L;
            var plan = Main.Autopilot == null ? null : Main.Autopilot.Plan;
            if (_character.magic.idleMagic > 0 && plan != null
                && (plan.RebirthExecutionHold || plan.RebirthBoundaryHold)
                && BR.LastGoldShortfall > 0.0 && _character.machine != null
                && _character.machine.goldMultiProgress > 0f
                && _character.buttons.brokenTimeMachine.interactable
                && !_character.challenges.timeMachineChallenge.inChallenge)
            {
                var beforeIdle = _character.magic.idleMagic;
                SetInput(beforeIdle);
                _character.timeMachineController.addMagic();
                residualTimeMachine = Math.Max(0L, beforeIdle - _character.magic.idleMagic);
            }

            string universalFallbackDecision;
            var universalFallback = _character.magic.idleMagic > 0
                ? AllocateMagicNoCurrencyFallback(out universalFallbackDecision)
                : NoFallbackNeeded(out universalFallbackDecision);

            var allocated = Math.Max(0L, _character.magic.curMagic - _character.magic.idleMagic);
            var timeMachine = _character.machine == null ? 0L : _character.machine.goldMultiMagic;
            var wandoos = _character.wandoos98 == null ? 0L : _character.wandoos98.wandoosMagic;
            var blood = _character.bloodMagic == null || _character.bloodMagic.ritual == null
                ? 0L
                : _character.bloodMagic.ritual.Sum(x => Math.Max(0L, x.magic));
            var targetKinds = string.Join(", ", temp.Select(x => x.GetType().Name).Distinct().ToArray());
            var idleReason = _character.magic.idleMagic <= 0 ? "fully allocated"
                + (residualTimeMachine > 0 ? "; paid Time Machine progress receives the Blood gold-wait remainder" : string.Empty)
                + (universalFallback > 0
                    ? "; universal no-currency fallback: " + universalFallbackDecision
                    : string.Empty)
                : "topology blocker: " + universalFallbackDecision;
            LastMagicAllocationDecision = "allocated " + allocated + "/" + _character.magic.curMagic
                                          + " (TM=" + timeMachine + ", Blood=" + blood
                                          + ", Wandoos=" + wandoos + "); idle=" + _character.magic.idleMagic
                                          + " — " + idleReason + "; Blood: " + BR.LastDecision
                                          + "; targets=" + targetKinds;

            _character.timeMachineController.updateMenu();
            _character.bloodMagicController.updateMenu();
            _character.NGUController.refreshMenu();
            _character.wandoos98Controller.refreshMenu();
            _character.wishesController.updateMenu();
            var actionShape = temp.Count + ":" + targetKinds + ":" + idleReason
                              + ":tm=" + (timeMachine > 0) + ":blood=" + (blood > 0)
                              + ":wan=" + (wandoos > 0)
                              + ":no-currency=" + universalFallback;
            // Magic is recalculated at 5 Hz just like Energy. Emit state changes
            // immediately, but coalesce identical verified layouts so a valid
            // ritual/TM allocation does not look like constant destructive churn.
            if (actionShape != _lastMagicActionShape
                || (DateTime.UtcNow - _lastMagicActionLog).TotalSeconds >= 2.0)
            {
                Main.LogAction("ALLOC", "Rebalanced Magic across " + temp.Count + " active priorities: "
                                        + LastMagicAllocationDecision);
                _lastMagicActionShape = actionShape;
                _lastMagicActionLog = DateTime.UtcNow;
            }
        }

        public override void AllocateR3()
        {
            if (!EnsureRuntimePlan())
                return;

            var bp = GetCurrentR3Breakpoint();
            if (bp != null && (_currentR3Breakpoint == null
                               || bp.Time != _currentR3Breakpoint.Time))
                _currentR3Breakpoint = bp;

            var temp = bp == null || bp.Priorities == null
                ? new List<BaseBreakpoint>()
                : bp.Priorities.Where(x => x.IsValid()).ToList();
            var prioCount = temp.Count(x => !x.IsCapPrio());
            _character.removeAllRes3();
            var toAdd = prioCount > 0
                ? (long)Math.Ceiling((double)_character.res3.idleRes3 / prioCount)
                : 0L;
            SetInput(toAdd);

            foreach (var prio in temp)
            {
                if (!prio.IsCapPrio())
                {
                    prioCount--;
                }

                if (prio.Allocate())
                {
                    toAdd = prioCount > 0
                        ? (long)Math.Ceiling((double)_character.res3.idleRes3 / prioCount)
                        : 0L;
                    SetInput(toAdd);
                }
            }

            /*
            RESOURCE 3 NO-CURRENCY FALLBACK

            Hack and Wish progress is permanent and neither native add controller debits Gold or a
            second finite currency. A strategic CAP therefore cannot justify leaving the remainder
            idle. Prefer Hacks because they progress from Resource 3 alone; use the planner's first
            valid Wish only when every installed Hack is unavailable/hard-capped or its controller
            accepts no Resource 3. A reached Hack target is advanced only through its native target
            controller and the forward target transition is checked before allocation. Every add is
            accepted solely from the observed idle-pool delta. The enclosing ResourceAllocationIntent
            separately seals the complete Hack/Wish vector and quarantines any later drift.
            */
            string universalFallbackDecision;
            var universalFallback = _character.res3.idleRes3 > 0
                ? AllocateR3NoCurrencyFallback(out universalFallbackDecision)
                : NoFallbackNeeded(out universalFallbackDecision);

            if (_character.hacksController != null)
                _character.hacksController.refreshMenu();
            if (_character.wishesController != null)
                _character.wishesController.updateMenu();
            LastR3AllocationDecision = "allocated "
                + Math.Max(0L, _character.res3.curRes3 - _character.res3.idleRes3) + "/"
                + _character.res3.curRes3 + " across " + temp.Count
                + " persistent Hack/Wish priorities; idle=" + _character.res3.idleRes3
                + "; no-currency=" + universalFallbackDecision
                + (_character.res3.idleRes3 > 0
                    ? "; idle-topology-blocker=" + universalFallbackDecision
                    : string.Empty);
            var actionShape = temp.Count + ":" + string.Join(", ", temp.Select(x =>
                                  x.GetType().Name).Distinct().ToArray())
                              + ":no-currency=" + universalFallbackDecision
                              + ":accepted=" + universalFallback
                              + ":idle=" + _character.res3.idleRes3;
            if (actionShape != _lastR3ActionShape
                || (DateTime.UtcNow - _lastR3ActionLog).TotalSeconds >= 2.0)
            {
                Main.LogAction("ALLOC", "Rebalanced Resource 3 across " + temp.Count
                                        + " active priorities: " + LastR3AllocationDecision);
                _lastR3ActionShape = actionShape;
                _lastR3ActionLog = DateTime.UtcNow;
            }
        }

        public override void EquipGear()
        {
            if (!EnsureRuntimePlan())
                return;
            var bp = GetCurrentGearBreakpoint();
            if (bp == null)
                return;

            if (bp.Time != _currentGearBreakpoint.Time)
            {
                _hasGearSwapped = false;
            }

            if (_hasGearSwapped) return;

            if (!LoadoutManager.CanSwap()) return;
            _hasGearSwapped = true;
            _currentGearBreakpoint = bp;
            LoadoutManager.ChangeGear(bp.Gear);
            Main.Controller.assignCurrentEquipToLoadout(0);
        }

        public override void EquipDiggers()
        {
            if (!EnsureRuntimePlan())
                return;
            var bp = GetCurrentDiggerBreakpoint();
            if (bp == null)
                return;

            if (bp.Time != _currentDiggerBreakpoint.Time)
            {
                _hasDiggerSwapped = false;
            }

            if (_hasDiggerSwapped) return;

            if (!DiggerManager.CanSwap()) return;
            _hasDiggerSwapped = true;
            _currentDiggerBreakpoint = bp;
            DiggerManager.EquipOptimizedDiggers(bp.Diggers);
            _character.allDiggers.refreshMenu();
        }

        private AllocationBreakPoint GetCurrentBreakpoint(bool energy)
        {
            var bps = energy ? _wrapper?.Breakpoints?.Energy : _wrapper?.Breakpoints?.Magic;
            if (bps == null)
                return null;

            foreach (var b in bps)
            {
                var rbTime = _character.rebirthTime.totalseconds;
                if (rbTime > b.Time)
                {
                    if (energy && _currentEnergyBreakpoint == null)
                    {
                        _currentEnergyBreakpoint = b;
                    }

                    if (!energy && _currentMagicBreakpoint == null)
                    {
                        _currentMagicBreakpoint = b;
                    }

                    return b;
                }
            }

            if (energy)
            {
                _currentEnergyBreakpoint = null;
            }
            else
            {
                _currentMagicBreakpoint = null;
            }

            return null;
        }

        private AllocationBreakPoint GetCurrentR3Breakpoint()
        {
            var bps = _wrapper?.Breakpoints?.R3;
            if (bps == null)
                return null;
            foreach (var b in bps)
            {
                var rbTime = _character.rebirthTime.totalseconds;
                if (rbTime > b.Time)
                {
                    if (_currentR3Breakpoint == null)
                    {
                        _currentR3Breakpoint = b;
                    }

                    return b;
                }
            }

            _currentR3Breakpoint = null;
            return null;
        }

        private GearBreakpoint GetCurrentGearBreakpoint()
        {
            var bps = _wrapper?.Breakpoints?.Gear;
            if (bps == null)
                return null;
            foreach (var b in bps)
            {
                if (_character.rebirthTime.totalseconds > b.Time)
                {
                    if (_currentGearBreakpoint == null)
                    {
                        _hasGearSwapped = false;
                        _currentGearBreakpoint = b;
                    }

                    return b;
                }
            }

            _currentGearBreakpoint = null;
            return null;
        }

        private DiggerBreakpoint GetCurrentDiggerBreakpoint()
        {
            var bps = _wrapper?.Breakpoints?.Diggers;
            if (bps == null)
                return null;

            if (_character.challenges.timeMachineChallenge.inChallenge)
                return null;

            foreach (var b in bps)
            {
                if (_character.rebirthTime.totalseconds > b.Time)
                {
                    if (_currentDiggerBreakpoint == null || _character.challenges.trollChallenge.inChallenge)
                    {
                        _hasDiggerSwapped = false;
                        _currentDiggerBreakpoint = b;
                    }

                    return b;
                }
            }

            _currentDiggerBreakpoint = null;
            return null;
        }

        private NGUDiffBreakpoint GetCurrentNGUDiffBreakpoint()
        {
            var bps = _wrapper?.Breakpoints?.NGUBreakpoints;
            if (bps == null)
                return null;
            foreach (var b in bps)
            {
                if (_character.rebirthTime.totalseconds > b.Time)
                {
                    if (_currentNguBreakpoint == null || _currentNguBreakpoint.Time != b.Time ||
                        _currentNguBreakpoint.Diff != b.Diff)
                    {
                        _hasNGUSwapped = false;
                        _currentNguBreakpoint = b;
                    }

                    return b;
                }
            }

            _currentNguBreakpoint = null;
            return null;
        }

        private WandoosBreakpoint GetCurrentWandoosBreakpoint()
        {
            var bps = _wrapper?.Breakpoints?.Wandoos;
            if (bps == null)
                return null;

            foreach (var b in bps)
            {
                if (_character.rebirthTime.totalseconds > b.Time)
                {
                    if (_currentWandoosBreakpoint == null)
                    {
                        _hasWandoosSwapped = false;
                        _currentWandoosBreakpoint = b;
                    }

                    return b;
                }
            }

            _currentWandoosBreakpoint = null;
            return null;
        }

        private static long NoFallbackNeeded(out string decision)
        {
            decision = "not needed; valued targets consumed the full pool";
            return 0L;
        }

        private long AllocateEnergyNoCurrencyFallback(out string decision)
        {
            var acceptedTotal = 0L;
            var acceptedSinks = new List<string>();
            var nguAvailable = _character.buttons != null
                               && _character.buttons.ngu.interactable
                               && _character.NGU != null && _character.NGU.skills != null
                               && _character.NGU.skills.Count > 0
                               && _character.NGUController != null
                               && _character.NGUController.NGU != null
                               && _character.NGUController.NGU.Length > 0;
            var wandoosAvailable = WandoosTopologyAvailable()
                                    && ProductiveWandoosEnergyWith(
                                        _character.wandoos98.wandoosEnergy
                                        + _character.idleEnergy);
            var selected = ExactResourceAllocator.SelectNoCurrencyFallback(nguAvailable,
                nguAvailable, wandoosAvailable, wandoosAvailable,
                wandoosAvailable, _character.wandoos98 != null && _character.wandoos98.disabled);

            if (selected == NoCurrencyFallbackKind.Ngu)
            {
                var accepted = TryAddIdleEnergyToNgu();
                acceptedTotal += accepted;
                if (accepted > 0L) acceptedSinks.Add("Energy NGU row 0");
            }
            if (_character.idleEnergy > 0 && wandoosAvailable)
            {
                var accepted = TryAddIdleEnergyToWandoos();
                acceptedTotal += accepted;
                if (accepted > 0L) acceptedSinks.Add("Wandoos Energy");
            }

            decision = DescribeNoCurrencyFallback("Energy", acceptedSinks,
                acceptedTotal, _character.idleEnergy, nguAvailable, wandoosAvailable);
            return acceptedTotal;
        }

        private long AllocateMagicNoCurrencyFallback(out string decision)
        {
            var acceptedTotal = 0L;
            var acceptedSinks = new List<string>();
            var nguAvailable = _character.buttons != null
                               && _character.buttons.ngu.interactable
                               && _character.NGU != null && _character.NGU.magicSkills != null
                               && _character.NGU.magicSkills.Count > 0
                               && _character.NGUController != null
                               && _character.NGUController.NGUMagic != null
                               && _character.NGUController.NGUMagic.Length > 0;
            var wandoosAvailable = WandoosTopologyAvailable()
                                    && ProductiveWandoosMagicWith(
                                        _character.wandoos98.wandoosMagic
                                        + _character.magic.idleMagic);
            var selected = ExactResourceAllocator.SelectNoCurrencyFallback(nguAvailable,
                nguAvailable, wandoosAvailable, wandoosAvailable,
                wandoosAvailable, _character.wandoos98 != null && _character.wandoos98.disabled);

            if (selected == NoCurrencyFallbackKind.Ngu)
            {
                var accepted = TryAddIdleMagicToNgu();
                acceptedTotal += accepted;
                if (accepted > 0L) acceptedSinks.Add("Magic NGU row 0");
            }
            if (_character.magic.idleMagic > 0 && wandoosAvailable)
            {
                var accepted = TryAddIdleMagicToWandoos();
                acceptedTotal += accepted;
                if (accepted > 0L) acceptedSinks.Add("Wandoos Magic");
            }

            decision = DescribeNoCurrencyFallback("Magic", acceptedSinks,
                acceptedTotal, _character.magic.idleMagic, nguAvailable, wandoosAvailable);
            return acceptedTotal;
        }

        private long AllocateR3NoCurrencyFallback(out string decision)
        {
            var acceptedTotal = 0L;
            var acceptedSinks = new List<string>();
            int hackId;
            string hackTopology;
            var hackAvailable = TrySelectR3FallbackHack(out hackId, out hackTopology);
            if (hackAvailable)
            {
                var accepted = TryAddIdleR3ToHack(hackId, out hackTopology);
                acceptedTotal += accepted;
                if (accepted > 0L) acceptedSinks.Add("Hack " + hackId);
            }

            var wishId = -1;
            var wishTopology = _character.res3.idleRes3 > 0L
                ? string.Empty : "Hack fallback consumed the full Resource 3 pool";
            var wishAvailable = _character.res3.idleRes3 > 0L
                                && TrySelectR3FallbackWish(out wishId, out wishTopology);
            if (wishAvailable)
            {
                var accepted = TryAddIdleR3ToWish(wishId);
                acceptedTotal += accepted;
                if (accepted > 0L) acceptedSinks.Add("Wish " + wishId);
                else wishTopology = "valid Wish " + wishId
                                    + " accepted zero through wishesController.addRes3";
            }

            decision = DescribeR3NoCurrencyFallback(acceptedSinks, acceptedTotal,
                _character.res3.idleRes3, hackTopology, wishTopology);
            return acceptedTotal;
        }

        private bool TrySelectR3FallbackHack(out int hackId, out string topology)
        {
            hackId = -1;
            topology = string.Empty;
            if (_character.buttons == null || !_character.buttons.hacks.interactable
                || _character.hacks == null || _character.hacks.hacks == null
                || _character.hacksController == null)
            {
                topology = "Hacks are locked or their native controller/state is unavailable";
                return false;
            }

            var bestMilestoneDistance = long.MaxValue;
            for (var id = 0; id < _character.hacks.hacks.Count; id++)
            {
                if (!ExactResourceAllocator.IsSupportedHackId(id,
                        _character.hacks.hacks.Count))
                    continue;
                try
                {
                    var hack = _character.hacks.hacks[id];
                    if (hack == null || hack.level >= _character.hacksController.hardCapLevel(id))
                        continue;
                    var distance = Math.Max(1L,
                        _character.hacksController.levelsToNextMilestone(id));
                    if (distance >= bestMilestoneDistance) continue;
                    bestMilestoneDistance = distance;
                    hackId = id;
                }
                catch
                {
                    // One malformed/unavailable row cannot hide a later installed Hack.
                }
            }
            if (hackId >= 0)
            {
                topology = "Hack " + hackId + " is unlocked below its native hard cap";
                return true;
            }
            topology = "every installed Hack is unavailable or at its native hard cap";
            return false;
        }

        private bool TrySelectR3FallbackWish(out int wishId, out string topology)
        {
            wishId = -1;
            topology = string.Empty;
            if (_character.buttons == null || !_character.buttons.wishes.interactable
                || _character.wishes == null || _character.wishes.wishes == null
                || _character.wishesController == null || Main.WishManager == null
                || _character.wishesController.curWishSlots() <= 0
                || Main.Autopilot != null && Main.Autopilot.Config != null
                && !Main.Autopilot.Config.ManageWishes)
            {
                topology = "Wishes are locked, disabled, slotless, or their native controller/state is unavailable";
                return false;
            }
            try
            {
                var candidates = Enumerable.Range(0, _character.wishes.wishes.Count)
                    .Where(Main.WishManager.isValidWish)
                    // Prefer a Wish already supplied by Energy/Magic so fallback R3 produces
                    // progress without creating a zero-factor third-resource-only bar.
                    .OrderByDescending(id => _character.wishes.wishes[id].energy > 0L
                                             && _character.wishes.wishes[id].magic > 0L)
                    .ThenBy(id => id);
                foreach (var candidate in candidates)
                {
                    wishId = candidate;
                    topology = "Wish " + wishId
                               + " is the first valid active-or-installed Wish";
                    return true;
                }
            }
            catch
            {
                topology = "the live Wish planner/controller topology could not be captured";
                return false;
            }
            topology = "no valid unlocked Wish exists for the available native Wish slots";
            return false;
        }

        private long TryAddIdleR3ToHack(int hackId, out string topology)
        {
            topology = "Hack " + hackId + " native allocation was not attempted";
            var before = _character.res3.idleRes3;
            if (before <= 0L) return 0L;
            try
            {
                if (_character.hacksController.hitTarget(hackId))
                {
                    var targetBefore = _character.hacks.hacks[hackId].target;
                    _character.hacksController.setToNextMilestone(hackId);
                    var targetAfter = _character.hacks.hacks[hackId].target;
                    if (targetAfter <= Math.Max(targetBefore,
                            _character.hacks.hacks[hackId].level))
                    {
                        topology = "Hack " + hackId
                                   + " reached its target and the native milestone controller did not advance it";
                        return 0L;
                    }
                }
                _character.hacksController.addR3(hackId, before);
                long accepted;
                if (!ExactResourceAllocator.TryObservedAcceptance(before,
                        _character.res3.idleRes3, before, out accepted))
                {
                    topology = "Hack " + hackId
                               + " produced an invalid observed Resource 3 idle delta";
                    return 0L;
                }
                topology = accepted > 0L ? "Hack " + hackId + " accepted " + accepted
                    : "Hack " + hackId + " accepted zero through hacksController.addR3";
                return accepted;
            }
            catch (Exception error)
            {
                topology = "Hack " + hackId + " native controller failed: "
                           + error.GetType().Name;
                return 0L;
            }
        }

        private long TryAddIdleR3ToWish(int wishId)
        {
            var before = _character.res3.idleRes3;
            if (before <= 0L || !SetInput(before)) return 0L;
            _character.wishesController.addRes3(wishId);
            long accepted;
            return ExactResourceAllocator.TryObservedAcceptance(before,
                _character.res3.idleRes3, before, out accepted) ? accepted : 0L;
        }

        private long TryAddIdleEnergyToNgu()
        {
            var before = _character.idleEnergy;
            if (before <= 0L || !SetInput(before)) return 0L;
            _character.NGUController.NGU[0].add();
            long accepted;
            return ExactResourceAllocator.TryObservedAcceptance(before, _character.idleEnergy,
                before, out accepted) ? accepted : 0L;
        }

        private long TryAddIdleEnergyToWandoos()
        {
            var before = _character.idleEnergy;
            if (before <= 0L || !SetInput(before)) return 0L;
            _character.wandoos98Controller.addEnergy();
            long accepted;
            return ExactResourceAllocator.TryObservedAcceptance(before, _character.idleEnergy,
                before, out accepted) ? accepted : 0L;
        }

        private long TryAddIdleMagicToNgu()
        {
            var before = _character.magic.idleMagic;
            if (before <= 0L || !SetInput(before)) return 0L;
            _character.NGUController.NGUMagic[0].add();
            long accepted;
            return ExactResourceAllocator.TryObservedAcceptance(before,
                _character.magic.idleMagic, before, out accepted) ? accepted : 0L;
        }

        private long TryAddIdleMagicToWandoos()
        {
            var before = _character.magic.idleMagic;
            if (before <= 0L || !SetInput(before)) return 0L;
            _character.wandoos98Controller.addMagic();
            long accepted;
            return ExactResourceAllocator.TryObservedAcceptance(before,
                _character.magic.idleMagic, before, out accepted) ? accepted : 0L;
        }

        private bool WandoosTopologyAvailable()
        {
            return _character.buttons != null && _character.buttons.wandoos.interactable
                   && _character.settings != null && _character.settings.wandoos98On
                   && _character.wandoos98 != null && _character.wandoos98.installed
                   && !_character.wandoos98.disabled
                   && _character.wandoos98Controller != null;
        }

        private bool ProductiveWandoosEnergyWith(long totalAllocation)
        {
            if (!WandoosTopologyAvailable()) return false;
            double completion;
            return ExactResourceAllocator.ResetLocalLevelHasUseWindow(
                _character.wandoos98.energyProgress, totalAllocation,
                _character.totalWandoosEnergySpeed(), CurrentWandoosBaseTime(),
                RemainingAllocationHorizon(), out completion);
        }

        private bool ProductiveWandoosMagicWith(long totalAllocation)
        {
            if (!WandoosTopologyAvailable()) return false;
            double completion;
            return ExactResourceAllocator.ResetLocalLevelHasUseWindow(
                _character.wandoos98.magicProgress, totalAllocation,
                _character.totalWandoosMagicSpeed(), CurrentWandoosBaseTime(),
                RemainingAllocationHorizon(), out completion);
        }

        private double RemainingAllocationHorizon()
        {
            if (Main.Autopilot == null || Main.Autopilot.Plan == null)
                return 0.0;
            var target = Main.Autopilot.Plan.EffectiveAllocationTarget(_character);
            return target > 0.0
                ? Math.Max(0.0, target - _character.rebirthTime.totalseconds)
                : 0.0;
        }

        private double CurrentWandoosBaseTime()
        {
            var os = (int)_character.wandoos98.os;
            if (_character.settings.rebirthDifficulty == difficulty.normal)
                return os == 2 ? 1e15 : os == 1 ? 1e12 : 1e9;
            return os == 2 ? 1e33 : os == 1 ? 1e27 : 1e21;
        }

        private static string DescribeNoCurrencyFallback(string resource,
            IList<string> acceptedSinks, long accepted, long remaining,
            bool nguAvailable, bool wandoosAvailable)
        {
            var sinks = acceptedSinks == null || acceptedSinks.Count == 0
                ? string.Empty
                : string.Join(" + ", acceptedSinks.ToArray());
            if (remaining <= 0L && accepted > 0L)
                return sinks;
            if (!nguAvailable && !wandoosAvailable)
                return "no unlocked no-currency " + resource
                       + " sink (NGU/Wandoos) exists in the live topology";
            if (accepted <= 0L)
                return "unlocked no-currency " + resource
                       + " sink topology accepted zero through its native controller";
            return sinks + "; native no-currency sinks rejected the remaining " + remaining;
        }

        private static string DescribeR3NoCurrencyFallback(IList<string> acceptedSinks,
            long accepted, long remaining, string hackTopology, string wishTopology)
        {
            var sinks = acceptedSinks == null || acceptedSinks.Count == 0
                ? string.Empty : string.Join(" + ", acceptedSinks.ToArray());
            if (remaining <= 0L && accepted > 0L) return sinks;
            var topology = "Hack: " + (hackTopology ?? "unavailable") + "; Wish: "
                           + (wishTopology ?? "unavailable");
            if (accepted <= 0L)
                return "no native no-currency Resource 3 sink accepted allocation ("
                       + topology + ")";
            return sinks + "; native Hack/Wish sinks rejected the remaining " + remaining
                   + " (" + topology + ")";
        }

        private bool SetInput(long val)
        {
            if (_character == null || _character.energyMagicPanel == null
                || _character.energyMagicPanel.energyRequested == null || val < 0L)
                return false;
            _character.energyMagicPanel.energyRequested.text =
                ExactResourceAllocator.FormatExactInput(val);
            _character.energyMagicPanel.validateInput();
            // This is the native controller request field, not an allocation target. The game's
            // text parser narrows above 2^53, so restore the already-bounded Int64 request before a
            // native add call; the enclosing full-vector transaction remains the settlement proof.
            if (_character.energyMagicPanel.energyMagicInput != val)
            {
                _character.energyMagicPanel.energyMagicInput = val;
                _character.energyMagicPanel.energyRequested.text =
                    ExactResourceAllocator.FormatExactInput(val);
            }
            return _character.energyMagicPanel.energyMagicInput == val;
        }
    }

    [Serializable]
    internal class BreakpointWrapper
    {
        [SerializeField] public Breakpoints Breakpoints;
    }

    [Serializable]
    internal class Breakpoints
    {
        [SerializeField] public AllocationBreakPoint[] Magic;
        [SerializeField] public AllocationBreakPoint[] Energy;
        [SerializeField] public AllocationBreakPoint[] R3;
        [SerializeField] public GearBreakpoint[] Gear;
        [SerializeField] public DiggerBreakpoint[] Diggers;
        [SerializeField] public WandoosBreakpoint[] Wandoos;
        [SerializeField] public BaseRebirth Rebirth;
        [SerializeField] public NGUDiffBreakpoint[] NGUBreakpoints;

    }

    [Serializable]
    internal class AllocationBreakPoint
    {
        [SerializeField] public double Time;
        [SerializeField] public BaseBreakpoint[] Priorities;
    }

    [Serializable]
    public class GearBreakpoint
    {
        public double Time;
        public int[] Gear;
    }

    [Serializable]
    public class DiggerBreakpoint
    {
        public double Time;
        public int[] Diggers;
    }

    [Serializable]
    public class WandoosBreakpoint
    {
        public double Time;
        public int OS;
    }

    [Serializable]
    public class NGUDiffBreakpoint
    {
        public double Time;
        public int Diff;
    }
}
