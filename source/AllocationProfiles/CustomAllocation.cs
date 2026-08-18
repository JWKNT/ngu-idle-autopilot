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
rituals, hacks, and wishes, returning unused resources safely. Allocation calls run on Unity's
main thread; actual deltas, sync-pair costs, caps, and rebirth horizon must remain authoritative.
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
            if (bp == null)
            {
                _character.removeAllEnergy();
                return;
            }

            if (bp.Time != _currentEnergyBreakpoint.Time)
            {
                _currentEnergyBreakpoint = bp;
            }

            var temp = bp.Priorities.Where(x => x.IsValid()).ToList();
            if (temp.Count == 0)
            {
                _character.removeAllEnergy();
                return;
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

            /*
            FINITE ADVENTURE-GATE RESERVATION

            A valid AdvancedTrainingBP has already proven that its exact finite
            Power/Toughness target opens the next zone and repays before rebirth.
            Reserve that small, reset-local gate allocation before the broad BT
            water-fill; otherwise the aggregate BT budget can consume the Energy
            first and make an admitted two-stat gate impossible to complete.
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

            var cappedTraining = temp.OfType<BasicTrainingBP>().ToList();
            var optimizeCappedTraining = cappedTraining.Count > 0
                                         && cappedTraining.All(x => x.IsCapPrio());
            var optimizedTrainingSpent = 0L;
            var longHorizonTrainingSpent = 0L;
            if (optimizeCappedTraining)
            {
                // Preserve the planner's aggregate BT budget, but solve its internal
                // distribution by exact current marginal value. A 4x15% declaration
                // now means "60% to the best BT margins", not four equal 15% slices.
                var fraction = Math.Min(1.0, cappedTraining.Sum(x => x.ConfiguredFraction));
                var remainingTrainingBudget = (long)Math.Ceiling(_character.curEnergy * fraction);

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
            // one-level-per-tick speed cap. Any surplus after this pass is genuinely
            // unusable at the current unlock/horizon state and should remain idle.
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
            var actionShape = temp.Count + ":" + priorityKinds;
            // Allocation still executes at 5 Hz. Coalesce identical telemetry so
            // synchronous AutoFlush I/O does not become part of the optimizer cost;
            // target-set changes are always logged immediately.
            if (actionShape != _lastEnergyActionShape
                || (DateTime.UtcNow - _lastEnergyActionLog).TotalSeconds >= 2.0)
            {
                Main.LogAction("ALLOC", "Rebalanced Energy across " + temp.Count + " targets: " + priorityKinds
                                        + "; idle=" + _character.idleEnergy
                                        + (optimizedTrainingSpent > 0 ? ", optimizedBT=" + optimizedTrainingSpent : string.Empty)
                                        + (longHorizonTrainingSpent > 0 ? ", persistent-cap-reserve=" + longHorizonTrainingSpent : string.Empty)
                                        + (advancedTrainingSpent > 0 ? ", next-zone-AT=" + advancedTrainingSpent : string.Empty)
                                        + (swept > 0 ? ", event-residual->BT=" + swept : string.Empty)
                                        + (idleFallback > 0 ? ", idle-fallback->BT=" + idleFallback : string.Empty));
                _lastEnergyActionShape = actionShape;
                _lastEnergyActionLog = DateTime.UtcNow;
            }
        }

        public override void AllocateMagic()
        {
            if (!EnsureRuntimePlan())
                return;

            var bp = GetCurrentBreakpoint(false);
            if (bp == null)
            {
                _character.removeAllMagic();
                return;
            }

            if (bp.Time != _currentMagicBreakpoint.Time)
            {
                _currentMagicBreakpoint = bp;
            }

            var temp = bp.Priorities.Where(x => x.IsValid()).ToList();
            if (temp.Count == 0)
            {
                _character.removeAllMagic();
                return;
            }
            var prioCount = temp.Count(x => !x.IsCapPrio());

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
            if (_character.magic.idleMagic > 0 && plan != null && plan.RebirthExecutionHold
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

            var allocated = Math.Max(0L, _character.magic.curMagic - _character.magic.idleMagic);
            var timeMachine = _character.machine == null ? 0L : _character.machine.goldMultiMagic;
            var wandoos = _character.wandoos98 == null ? 0L : _character.wandoos98.wandoosMagic;
            var blood = _character.bloodMagic == null || _character.bloodMagic.ritual == null
                ? 0L
                : _character.bloodMagic.ritual.Sum(x => Math.Max(0L, x.magic));
            var targetKinds = string.Join(", ", temp.Select(x => x.GetType().Name).Distinct().ToArray());
            var idleReason = _character.magic.idleMagic <= 0 ? "fully allocated"
                + (residualTimeMachine > 0 ? "; paid Time Machine progress receives the Blood gold-wait remainder" : string.Empty)
                : allocated <= 0 ? "no active native target accepted Magic"
                : blood > 0 && temp.All(x => x is BR)
                    ? "active Blood ritual is at its productive cap; the small remainder has no other admitted Magic sink"
                : blood <= 0 && temp.Any(x => x is BR)
                    ? BR.LastDecision
                : "remaining Magic exceeds the active targets' productive caps or cannot finish before rebirth";
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
                              + ":wan=" + (wandoos > 0);
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
            if (bp == null)
            {
                _character.removeAllRes3();
                LastR3AllocationDecision = "No Resource 3 breakpoint is active";
                return;
            }

            if (bp.Time != _currentR3Breakpoint.Time)
            {
                _currentR3Breakpoint = bp;
            }

            var temp = bp.Priorities.Where(x => x.IsValid()).ToList();
            if (temp.Count == 0)
            {
                _character.removeAllRes3();
                LastR3AllocationDecision = "No unlocked persistent Resource 3 target is currently valid";
                return;
            }
            
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

            _character.hacksController.refreshMenu();
            _character.wishesController.updateMenu();
            LastR3AllocationDecision = "allocated "
                + Math.Max(0L, _character.res3.curRes3 - _character.res3.idleRes3) + "/"
                + _character.res3.curRes3 + " across " + temp.Count
                + " persistent Hack/Wish priorities; idle=" + _character.res3.idleRes3;
            Main.LogAction("ALLOC", "Rebalanced Resource 3 across " + temp.Count + " active priorities");
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

        private void SetInput(float val)
        {
            _character.energyMagicPanel.energyRequested.text = val.ToString();
            _character.energyMagicPanel.validateInput();
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
