using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using NGUInjector.AllocationProfiles.RebirthStuff;

/*
FILE PURPOSE

Purpose: This file is the fail-closed reset execution contract for ordinary rebirths, challenge
entries, and the two legal forward difficulty switches in NGU Idle 1.260. It owns the exact finite
selector gates, common reset preflight, typed before/after proofs, failure classification, and the
synchronous run-epoch close required after a verified reset.

Mechanism: DifficultyTransitionGate evaluates copied scalars only. DifficultyTransitionExecutor
then invokes the build-pinned gated selector, rereads the selected target, reruns the complete
preflight, invokes the build-pinned target start, and verifies the poststate. ResetPostconditions is
also used by BaseRebirth so ordinary/challenge paths cannot weaken +1/timer/one-hot rules. A scripted
boundary supports isolated fault matrices without loading Unity or a save.

Inputs and outputs: Inputs are copied gate/reset snapshots, one intended transition or challenge,
native adapter observations, and an explicit feature-authority bit. Outputs are named gate blockers,
exact proof results, and Held/RejectedUnchanged/Committed/CommittedWithException/Quarantined states.

Invariants and safety: The authoritative selectors are Rebirth.setEvilNextRebirth and
Rebirth.setSadisticNextRebirth. Their public start wrappers are reachable only after both local gate
passes and exact selected-target verification. No field write or legacy switch path exists here.
Difficulty and challenge authority remains false in default configuration until disposable-save
fixtures pass. A verified reset closes the old run epoch before this executor returns; a partial,
wrong-target, or multiple-flag result quarantines it.

Extension points and non-goals: Task 29 supplies scheduler intents/leases and the independently
configured difficulty authority. The pure task-17 snapshots are suitable for copied-save differential
tests. This file does not plan when to switch, change configuration, mutate a save, or discover native
members outside NativeBindingRegistry.
*/
namespace NGUInjector.Autopilot
{
    internal enum DifficultyTransitionKind
    {
        NormalToEvil = 0,
        EvilToSadistic = 1
    }

    internal enum ResetExecutionKind
    {
        Held = 0,
        RejectedUnchanged = 1,
        Committed = 2,
        CommittedWithException = 3,
        Quarantined = 4
    }

    internal sealed class DifficultyGateSnapshot
    {
        internal ResetDifficulty CurrentDifficulty;
        internal bool InChallenge;
        internal bool Achievement151;
        internal bool Achievement152;
        internal int HighestBoss;
        internal int HighestHardBoss;
        internal double AttackBoost;
        internal double ItopodTotalStatBonus;
        internal bool ExileV4Defeated;
        internal int BossId;
        internal bool BossFightInProgress;
        internal bool BossNukeInProgress;
        internal bool NoRebirthChallengeActive;
        internal double RebirthSeconds;
        internal double MinimumRebirthSeconds;
        internal bool GameplaySynchronized;
        internal bool MutationLeaseCurrent;

        internal DifficultyGateSnapshot Clone()
        {
            return (DifficultyGateSnapshot)MemberwiseClone();
        }
    }

    internal sealed class DifficultyGateResult
    {
        internal bool Legal;
        internal ResetDifficulty Source;
        internal ResetDifficulty Target;
        internal double RichJerkProduct = double.NaN;
        internal string[] Blockers = new string[0];
        internal string Evidence = string.Empty;
    }

    internal static class DifficultyTransitionGate
    {
        internal const double EvilRichJerkProduct = 10000.0;

        internal static DifficultyGateResult EvaluateSelector(DifficultyTransitionKind transition,
            DifficultyGateSnapshot state)
        {
            return Evaluate(transition, state, false);
        }

        internal static DifficultyGateResult EvaluateFinalPreflight(
            DifficultyTransitionKind transition, DifficultyGateSnapshot state)
        {
            return Evaluate(transition, state, true);
        }

        internal static ResetDifficulty Source(DifficultyTransitionKind transition)
        {
            return transition == DifficultyTransitionKind.NormalToEvil
                ? ResetDifficulty.Normal
                : transition == DifficultyTransitionKind.EvilToSadistic
                    ? ResetDifficulty.Evil : ResetDifficulty.Unknown;
        }

        internal static ResetDifficulty Target(DifficultyTransitionKind transition)
        {
            return transition == DifficultyTransitionKind.NormalToEvil
                ? ResetDifficulty.Evil
                : transition == DifficultyTransitionKind.EvilToSadistic
                    ? ResetDifficulty.Sadistic : ResetDifficulty.Unknown;
        }

        private static DifficultyGateResult Evaluate(DifficultyTransitionKind transition,
            DifficultyGateSnapshot state, bool finalPreflight)
        {
            var blockers = new System.Collections.Generic.List<string>();
            var source = Source(transition);
            var target = Target(transition);
            var result = new DifficultyGateResult {Source = source, Target = target};
            if (source == ResetDifficulty.Unknown || target == ResetDifficulty.Unknown)
            {
                blockers.Add("transition kind is outside the exact forward catalog");
                return Finish(result, blockers, finalPreflight);
            }
            if (state == null)
            {
                blockers.Add("gate snapshot is missing");
                return Finish(result, blockers, finalPreflight);
            }
            if (state.CurrentDifficulty != source)
                blockers.Add("current difficulty is not " + source);
            // Active-challenge refusal is never bypassed by the Evil re-entry achievement.
            if (state.InChallenge)
                blockers.Add("a challenge is active");

            if (transition == DifficultyTransitionKind.NormalToEvil)
            {
                var product = state.AttackBoost * state.ItopodTotalStatBonus;
                result.RichJerkProduct = product;
                if (!state.Achievement152)
                {
                    if (state.HighestBoss < 300)
                        blockers.Add("Normal Boss record is below 300");
                    if (!state.Achievement151)
                        blockers.Add("Beast v4 achievement 151 is missing");
                    if (!FiniteNonNegative(state.AttackBoost)
                        || !FiniteNonNegative(state.ItopodTotalStatBonus)
                        || !FiniteNonNegative(product))
                        blockers.Add("Rich Jerk operands/product are not finite nonnegative values");
                    else if (product < EvilRichJerkProduct)
                        blockers.Add("Rich Jerk product is below 10000");
                }
            }
            else
            {
                if (state.HighestHardBoss < 300)
                    blockers.Add("Evil Boss record is below 300");
                if (!state.ExileV4Defeated)
                    blockers.Add("Exile v4 defeat flag is missing");
            }

            if (finalPreflight)
            {
                if (state.BossId <= 0) blockers.Add("current Boss is not beyond Boss 0");
                if (state.BossFightInProgress) blockers.Add("Fight Boss is active");
                if (state.BossNukeInProgress) blockers.Add("Fight Boss nuke is active");
                if (state.NoRebirthChallengeActive)
                    blockers.Add("No Rebirth Challenge forbids a reset");
                if (!FiniteNonNegative(state.RebirthSeconds)
                    || !FiniteNonNegative(state.MinimumRebirthSeconds))
                    blockers.Add("rebirth timer/minimum is not finite nonnegative state");
                else if (state.RebirthSeconds < state.MinimumRebirthSeconds)
                    blockers.Add("native minimum rebirth time is not met");
                if (!state.GameplaySynchronized)
                    blockers.Add("gameplay/controller synchronization is not current");
                if (!state.MutationLeaseCurrent)
                    blockers.Add("mutation lease is not current");
            }
            return Finish(result, blockers, finalPreflight);
        }

        private static DifficultyGateResult Finish(DifficultyGateResult result,
            System.Collections.Generic.List<string> blockers, bool finalPreflight)
        {
            result.Blockers = blockers.ToArray();
            result.Legal = result.Blockers.Length == 0;
            result.Evidence = (finalPreflight ? "final preflight " : "selector gate ")
                              + result.Source + "->" + result.Target + ": "
                              + (result.Legal ? "legal" : string.Join("; ", result.Blockers));
            return result;
        }

        private static bool FiniteNonNegative(double value)
        {
            return value >= 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    internal sealed class ResetExecutionSnapshot
    {
        internal long RebirthNumber;
        internal double RebirthSeconds;
        internal ResetDifficulty CurrentDifficulty;
        internal ResetDifficulty NextDifficulty;
        internal ResetDifficulty NguLevelTrack;
        internal ResetNumberSnapshot Number = new ResetNumberSnapshot();
        internal int BossId;
        internal int CurrentHighestBoss;
        internal int HighestBoss;
        internal int HighestHardBoss;
        internal int HighestSadisticBoss;
        internal bool Achievement152;
        internal bool InChallenge;
        internal bool[] ChallengeFlags = new bool[11];
        internal string CurrentChallengeTypeToken = string.Empty;
        internal double[] ChallengeTimers = new double[11];
        internal double[] TitanClocks = new double[14];
        internal int[] TitanRunKillCounters = new int[0];
        internal string PersistentStateFingerprint = string.Empty;

        internal ResetExecutionSnapshot Clone()
        {
            var copy = (ResetExecutionSnapshot)MemberwiseClone();
            copy.Number = Number == null ? null : Number.Clone();
            copy.ChallengeFlags = Clone(ChallengeFlags);
            copy.ChallengeTimers = Clone(ChallengeTimers);
            copy.TitanClocks = Clone(TitanClocks);
            copy.TitanRunKillCounters = Clone(TitanRunKillCounters);
            return copy;
        }

        internal string ExactFingerprint
        {
            get
            {
                return RebirthNumber + "|" + RebirthSeconds.ToString("R", CultureInfo.InvariantCulture)
                       + "|" + CurrentDifficulty + "|" + NextDifficulty + "|" + NguLevelTrack
                       + "|" + NumberFingerprint(Number) + "|boss=" + BossId + "/" + CurrentHighestBoss
                       + "|records=" + HighestBoss + "/" + HighestHardBoss + "/" + HighestSadisticBoss
                       + "|a152=" + Achievement152 + "|challenge=" + InChallenge + ":"
                       + CurrentChallengeTypeToken + ":" + Join(ChallengeFlags) + ":"
                       + Join(ChallengeTimers) + "|titans=" + Join(TitanClocks) + ":"
                       + Join(TitanRunKillCounters) + "|persistent=" + PersistentStateFingerprint;
            }
        }

        private static string NumberFingerprint(ResetNumberSnapshot value)
        {
            if (value == null) return "missing";
            return string.Join(",", new[]
            {
                R(value.CurrentAttack), R(value.CurrentDefense), R(value.NextAttack),
                R(value.NextDefense), R(value.BossMultiplier), R(value.TimeMultiplier),
                R(value.OldBossMultiplier), R(value.OldTimeMultiplier)
            });
        }

        private static string R(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Join<T>(T[] value)
        {
            return value == null ? "missing" : string.Join(",", value);
        }

        private static T[] Clone<T>(T[] value)
        {
            return value == null ? new T[0] : (T[])value.Clone();
        }
    }

    internal sealed class ResetProof
    {
        internal bool Satisfied;
        internal bool ExactRebirthIncrement;
        internal bool TimerReset;
        internal bool ExactOneHotChallenge;
        internal bool ExactResetType;
        internal bool TargetDifficultyInstalled;
        internal string Reason = string.Empty;
    }

    internal static class ResetPostconditions
    {
        internal static ResetProof VerifyOrdinary(ResetExecutionSnapshot before,
            ResetExecutionSnapshot after)
        {
            var proof = Common(before, after);
            if (before == null || after == null) return proof;
            var challengePreserved = ChallengeStatePreserved(before, after);
            var recordsPreserved = before.HighestBoss == after.HighestBoss
                                   && before.HighestHardBoss == after.HighestHardBoss
                                   && before.HighestSadisticBoss == after.HighestSadisticBoss;
            proof.ExactResetType = SoftNumberTransition(before.Number, after.Number);
            proof.Satisfied = proof.ExactRebirthIncrement && proof.TimerReset && challengePreserved
                              && proof.ExactResetType && after.BossId == 0 && recordsPreserved
                              && AllZero(after.TitanClocks)
                              && AllZero(after.TitanRunKillCounters);
            proof.Reason = proof.Satisfied ? "exact ordinary rebirth postcondition"
                : "ordinary rebirth requires exact +1/zero timer, preserved challenge state, "
                  + "the synchronous soft Number bank, Boss 0, persistent records, and zero Titan state";
            return proof;
        }

        internal static ResetProof VerifyChallenge(ResetExecutionSnapshot before,
            ResetExecutionSnapshot after, ChallengeType intended, string intendedNativeTypeToken)
        {
            var proof = Common(before, after);
            if (before == null || after == null) return proof;
            var index = (int)intended;
            var flags = after.ChallengeFlags ?? new bool[0];
            var timers = after.ChallengeTimers ?? new double[0];
            proof.ExactOneHotChallenge = !before.InChallenge && after.InChallenge
                                        && flags.Length == 11 && index >= 0 && index < flags.Length
                                        && flags[index] && CountTrue(flags) == 1
                                        && string.Equals(after.CurrentChallengeTypeToken,
                                            intendedNativeTypeToken ?? string.Empty,
                                            StringComparison.Ordinal)
                                        && timers.Length == 11 && timers[index] == 0.0;
            proof.ExactResetType = ChallengeMechanics.EntryKind(intended)
                                   == ChallengeEntryTransformKind.HardReset
                ? after.Number != null && after.Number.AllExactlyOne
                : SoftNumberTransition(before.Number, after.Number);
            var recordsPreserved = before.HighestBoss == after.HighestBoss
                                   && before.HighestHardBoss == after.HighestHardBoss
                                   && before.HighestSadisticBoss == after.HighestSadisticBoss;
            var difficultyPreserved = after.CurrentDifficulty == before.CurrentDifficulty
                                      && after.NextDifficulty == before.CurrentDifficulty;
            proof.Satisfied = proof.ExactRebirthIncrement && proof.TimerReset
                              && proof.ExactOneHotChallenge && proof.ExactResetType
                              && after.BossId == 0 && recordsPreserved && difficultyPreserved
                              && AllZero(after.TitanClocks)
                              && AllZero(after.TitanRunKillCounters);
            proof.Reason = proof.Satisfied ? "exact challenge-entry postcondition"
                : "challenge entry requires +1/zero timer, exact one-hot/type/timer, reset-specific Number, Boss 0, preserved difficulty/records, and zero Titan state";
            return proof;
        }

        internal static ResetProof VerifyDifficulty(ResetExecutionSnapshot before,
            ResetExecutionSnapshot after, DifficultyTransitionKind transition)
        {
            var proof = Common(before, after);
            if (before == null || after == null) return proof;
            var target = DifficultyTransitionGate.Target(transition);
            proof.TargetDifficultyInstalled = after.CurrentDifficulty == target
                                              && after.NextDifficulty == target;
            proof.ExactResetType = after.Number != null && after.Number.AllExactlyOne;
            var recordsPreserved = before.HighestBoss == after.HighestBoss
                                   && before.HighestHardBoss == after.HighestHardBoss
                                   && before.HighestSadisticBoss == after.HighestSadisticBoss;
            var expectedNgu = transition == DifficultyTransitionKind.NormalToEvil
                && before.NguLevelTrack > ResetDifficulty.Evil
                    ? ResetDifficulty.Evil : before.NguLevelTrack;
            var achievement = transition == DifficultyTransitionKind.NormalToEvil
                ? after.Achievement152 : after.Achievement152 == before.Achievement152;
            proof.Satisfied = proof.ExactRebirthIncrement && proof.TimerReset
                              && proof.TargetDifficultyInstalled && proof.ExactResetType
                              && after.BossId == 0 && after.CurrentHighestBoss == 0
                              && recordsPreserved && after.NguLevelTrack == expectedNgu
                              && achievement && !after.InChallenge
                              && CountTrue(after.ChallengeFlags) == 0
                              && AllZero(after.TitanClocks)
                              && AllZero(after.TitanRunKillCounters);
            proof.Reason = proof.Satisfied ? "exact hard difficulty-transition postcondition"
                : "difficulty transition requires target current/next, exact +1/zero timer, Number=1, Boss/Titan clears, persistent records, NGU-track rule, and no challenge";
            return proof;
        }

        internal static bool ExactStateMatches(ResetExecutionSnapshot left,
            ResetExecutionSnapshot right)
        {
            return left != null && right != null && string.Equals(left.ExactFingerprint,
                right.ExactFingerprint, StringComparison.Ordinal);
        }

        private static ResetProof Common(ResetExecutionSnapshot before,
            ResetExecutionSnapshot after)
        {
            var proof = new ResetProof();
            if (before == null || after == null)
            {
                proof.Reason = "before/after reset snapshot is missing";
                return proof;
            }
            proof.ExactRebirthIncrement = before.RebirthNumber != long.MaxValue
                                          && after.RebirthNumber == before.RebirthNumber + 1L;
            proof.TimerReset = FiniteNonNegative(before.RebirthSeconds)
                               && after.RebirthSeconds == 0.0;
            return proof;
        }

        private static bool SoftNumberTransition(ResetNumberSnapshot before,
            ResetNumberSnapshot after)
        {
            return before != null && after != null
                   && FinitePositive(before.NextAttack) && FinitePositive(before.NextDefense)
                   && after.CurrentAttack == before.NextAttack
                   && after.CurrentDefense == before.NextDefense
                   && after.NextAttack == before.NextAttack
                   && after.NextDefense == before.NextDefense
                   // Native setNewMultis copies current Number and the two old factors, then
                   // resetBoss writes bossMulti=1 while resetTime clears only the timer fields.
                   // timeMulti therefore remains the just-finished value synchronously and is
                   // recalculated from the new timer by Rebirth.Update on a later Unity frame.
                   && after.BossMultiplier == 1.0
                   && after.TimeMultiplier == before.TimeMultiplier
                   && after.OldBossMultiplier == before.BossMultiplier
                   && after.OldTimeMultiplier == before.TimeMultiplier;
        }

        private static int CountTrue(bool[] flags)
        {
            if (flags == null) return 0;
            var count = 0;
            for (var i = 0; i < flags.Length; i++) if (flags[i]) count++;
            return count;
        }

        private static bool ChallengeStatePreserved(ResetExecutionSnapshot before,
            ResetExecutionSnapshot after)
        {
            var beforeFlags = before.ChallengeFlags ?? new bool[0];
            var afterFlags = after.ChallengeFlags ?? new bool[0];
            if (!before.InChallenge)
                return !after.InChallenge && CountTrue(afterFlags) == 0;
            if (!after.InChallenge || beforeFlags.Length != 11 || afterFlags.Length != 11
                || CountTrue(beforeFlags) != 1 || CountTrue(afterFlags) != 1
                || !string.Equals(before.CurrentChallengeTypeToken,
                    after.CurrentChallengeTypeToken, StringComparison.Ordinal)) return false;
            for (var i = 0; i < beforeFlags.Length; i++)
                if (beforeFlags[i] != afterFlags[i]) return false;
            return true;
        }

        private static bool AllZero(double[] values)
        {
            if (values == null || values.Length != 14) return false;
            for (var i = 0; i < values.Length; i++)
                if (values[i] != 0.0) return false;
            return true;
        }

        private static bool AllZero(int[] values)
        {
            if (values == null) return false;
            for (var i = 0; i < values.Length; i++) if (values[i] != 0) return false;
            return true;
        }

        private static bool FiniteNonNegative(double value)
        {
            return value >= 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool FinitePositive(double value)
        {
            return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    internal sealed class ResetNativeObservation
    {
        internal bool InvocationAttempted;
        internal bool ReturnedNormally;
        internal string Reason = string.Empty;
        internal Exception Exception;

        internal static ResetNativeObservation From(NativeInvocationResult result)
        {
            return result == null ? new ResetNativeObservation {Reason = "native result is missing"}
                : new ResetNativeObservation
                {
                    InvocationAttempted = result.InvocationAttempted,
                    ReturnedNormally = result.ReturnedNormally,
                    Reason = result.Reason,
                    Exception = result.Exception
                };
        }
    }

    internal interface IDifficultyTransitionBoundary
    {
        DifficultyGateSnapshot CaptureGate();
        ResetExecutionSnapshot CaptureState();
        ResetDifficulty ReadSelectedTarget();
        ResetNativeObservation Select(DifficultyTransitionKind transition);
        ResetNativeObservation Start(DifficultyTransitionKind transition);
    }

    internal interface IResetEpochBoundary
    {
        void CloseVerifiedRun(ResetExecutionSnapshot after, string reason);
        void Quarantine(string reason);
    }

    internal sealed class ResetExecutionResult
    {
        internal ResetExecutionKind Kind;
        internal string Reason = string.Empty;
        internal ResetProof Proof;
        internal bool SelectorAttempted;
        internal bool StartAttempted;
        internal bool EpochClosed;
    }

    internal sealed class DifficultyTransitionExecutor
    {
        internal const string EvilSelectorContract = "Rebirth.setEvilNextRebirth";
        internal const string SadisticSelectorContract = "Rebirth.setSadisticNextRebirth";
        internal const bool CreatesNewEpoch = true;

        private readonly IDifficultyTransitionBoundary _native;
        private readonly IResetEpochBoundary _epoch;

        internal DifficultyTransitionExecutor(IDifficultyTransitionBoundary native,
            IResetEpochBoundary epoch)
        {
            if (native == null) throw new ArgumentNullException("native");
            if (epoch == null) throw new ArgumentNullException("epoch");
            _native = native;
            _epoch = epoch;
        }

        internal ResetExecutionResult Execute(DifficultyTransitionKind transition,
            bool featureAuthority)
        {
            if (!featureAuthority)
                return Result(ResetExecutionKind.Held,
                    "difficulty authority is feature-disabled pending disposable-save integration");
            var before = _native.CaptureState();
            var firstGate = DifficultyTransitionGate.EvaluateFinalPreflight(transition,
                _native.CaptureGate());
            if (!firstGate.Legal)
                return Result(ResetExecutionKind.Held, firstGate.Evidence);

            var selector = SafeSelect(transition);
            var selected = Result(ResetExecutionKind.Held, selector.Reason);
            selected.SelectorAttempted = selector.InvocationAttempted;
            if (!selector.ReturnedNormally)
            {
                selected.Kind = ResetPostconditions.ExactStateMatches(before, _native.CaptureState())
                    ? ResetExecutionKind.RejectedUnchanged : ResetExecutionKind.Held;
                selected.Reason = "gated difficulty selector did not return normally: "
                                  + selector.Reason;
                return selected;
            }
            if (_native.ReadSelectedTarget() != DifficultyTransitionGate.Target(transition))
            {
                selected.Kind = ResetExecutionKind.RejectedUnchanged;
                selected.Reason = "gated selector did not publish the exact target difficulty";
                return selected;
            }

            var secondGate = DifficultyTransitionGate.EvaluateFinalPreflight(transition,
                _native.CaptureGate());
            if (!secondGate.Legal)
            {
                selected.Reason = "post-selector preflight became stale: " + secondGate.Evidence;
                return selected;
            }
            var selectedState = _native.CaptureState();
            var start = SafeStart(transition);
            var after = _native.CaptureState();
            var proof = ResetPostconditions.VerifyDifficulty(before, after, transition);
            var result = Result(ResetExecutionKind.Quarantined, proof.Reason);
            result.SelectorAttempted = selector.InvocationAttempted;
            result.StartAttempted = start.InvocationAttempted;
            result.Proof = proof;
            if (proof.Satisfied)
            {
                result.Kind = start.ReturnedNormally ? ResetExecutionKind.Committed
                    : ResetExecutionKind.CommittedWithException;
                _epoch.CloseVerifiedRun(after, "difficulty transition " + transition
                    + " committed with exact postcondition");
                result.EpochClosed = true;
                result.Reason = proof.Reason;
                return result;
            }
            if (ResetPostconditions.ExactStateMatches(selectedState, after))
            {
                result.Kind = ResetExecutionKind.RejectedUnchanged;
                result.Reason = "difficulty start produced an exact no-op";
                return result;
            }
            _epoch.Quarantine("partial/wrong difficulty transition: " + proof.Reason);
            result.Reason = "partial/wrong difficulty transition quarantined: " + proof.Reason;
            return result;
        }

        internal static ResetExecutionResult ExecuteLive(Character character,
            DifficultyTransitionKind transition, bool featureAuthority,
            bool gameplaySynchronized, bool mutationLeaseCurrent)
        {
            if (character == null)
                return Result(ResetExecutionKind.Held, "live Character is missing");
            try
            {
                var registry = NativeBindingRegistry.Create(typeof(Character).Assembly,
                    Main.GameAssemblySha256);
                var adapters = registry.CreateMutationAdapters();
                var boundary = new LiveDifficultyTransitionBoundary(character, adapters,
                    gameplaySynchronized, mutationLeaseCurrent);
                return new DifficultyTransitionExecutor(boundary,
                    new LiveResetEpochBoundary(character)).Execute(transition, featureAuthority);
            }
            catch (Exception error)
            {
                return Result(ResetExecutionKind.Held,
                    "difficulty native boundary unavailable: " + error.GetType().Name
                    + ": " + error.Message);
            }
        }

        private ResetNativeObservation SafeSelect(DifficultyTransitionKind transition)
        {
            try { return _native.Select(transition) ?? new ResetNativeObservation(); }
            catch (Exception error)
            {
                return new ResetNativeObservation
                {
                    InvocationAttempted = true, ReturnedNormally = false,
                    Reason = error.GetType().Name + ": " + error.Message, Exception = error
                };
            }
        }

        private ResetNativeObservation SafeStart(DifficultyTransitionKind transition)
        {
            try { return _native.Start(transition) ?? new ResetNativeObservation(); }
            catch (Exception error)
            {
                return new ResetNativeObservation
                {
                    InvocationAttempted = true, ReturnedNormally = false,
                    Reason = error.GetType().Name + ": " + error.Message, Exception = error
                };
            }
        }

        private static ResetExecutionResult Result(ResetExecutionKind kind, string reason)
        {
            return new ResetExecutionResult {Kind = kind, Reason = reason ?? string.Empty};
        }
    }

    /* A mutable pure boundary intentionally shipped for isolated task-17 fault matrices only. */
    internal sealed class ScriptedDifficultyBoundary : IDifficultyTransitionBoundary
    {
        internal DifficultyGateSnapshot FirstGate;
        internal DifficultyGateSnapshot SecondGate;
        internal ResetExecutionSnapshot Before;
        internal ResetExecutionSnapshot SelectedState;
        internal ResetExecutionSnapshot After;
        internal ResetDifficulty SelectedTarget;
        internal ResetNativeObservation SelectorResult = new ResetNativeObservation();
        internal ResetNativeObservation StartResult = new ResetNativeObservation();
        internal int GateCaptures;
        internal int StateCaptures;
        internal int SelectorCalls;
        internal int StartCalls;

        public DifficultyGateSnapshot CaptureGate()
        {
            return (++GateCaptures == 1 ? FirstGate : SecondGate).Clone();
        }

        public ResetExecutionSnapshot CaptureState()
        {
            StateCaptures++;
            if (StateCaptures == 1) return Before.Clone();
            if (StateCaptures == 2) return SelectedState.Clone();
            return After.Clone();
        }

        public ResetDifficulty ReadSelectedTarget() { return SelectedTarget; }

        public ResetNativeObservation Select(DifficultyTransitionKind transition)
        {
            SelectorCalls++;
            return SelectorResult;
        }

        public ResetNativeObservation Start(DifficultyTransitionKind transition)
        {
            StartCalls++;
            return StartResult;
        }
    }

    internal sealed class ScriptedResetEpochBoundary : IResetEpochBoundary
    {
        internal int CloseCalls;
        internal int QuarantineCalls;
        internal string LastReason = string.Empty;

        public void CloseVerifiedRun(ResetExecutionSnapshot after, string reason)
        {
            CloseCalls++;
            LastReason = reason ?? string.Empty;
        }

        public void Quarantine(string reason)
        {
            QuarantineCalls++;
            LastReason = reason ?? string.Empty;
        }
    }

    internal sealed class LiveDifficultyTransitionBoundary : IDifficultyTransitionBoundary
    {
        private readonly Character _character;
        private readonly NativeMutationAdapters _native;
        private readonly bool _synchronized;
        private readonly bool _leaseCurrent;

        internal LiveDifficultyTransitionBoundary(Character character,
            NativeMutationAdapters native, bool synchronized, bool leaseCurrent)
        {
            _character = character;
            _native = native;
            _synchronized = synchronized;
            _leaseCurrent = leaseCurrent;
        }

        public DifficultyGateSnapshot CaptureGate()
        {
            var c = _character;
            double totalStat = double.NaN;
            try { totalStat = c.adventureController.itopod.totalStatBonus(); }
            catch { }
            return new DifficultyGateSnapshot
            {
                CurrentDifficulty = LiveResetSnapshot.Difficulty(c.settings.rebirthDifficulty),
                InChallenge = c.challenges.inChallenge,
                Achievement151 = Achievement(c, 151),
                Achievement152 = Achievement(c, 152),
                HighestBoss = c.highestBoss,
                HighestHardBoss = c.highestHardBoss,
                AttackBoost = c.attackBoost,
                ItopodTotalStatBonus = totalStat,
                ExileV4Defeated = c.settings.exilev4Defeated,
                BossId = c.bossID,
                BossFightInProgress = c.bossController.isFighting,
                BossNukeInProgress = c.bossController.nukeBoss,
                NoRebirthChallengeActive = c.challenges.noRebirthChallenge.inChallenge,
                RebirthSeconds = c.rebirthTime.totalseconds,
                MinimumRebirthSeconds = c.rebirth.minRebirthTime(),
                GameplaySynchronized = _synchronized,
                MutationLeaseCurrent = _leaseCurrent
            };
        }

        public ResetExecutionSnapshot CaptureState()
        {
            return LiveResetSnapshot.Capture(_character);
        }

        public ResetDifficulty ReadSelectedTarget()
        {
            return LiveResetSnapshot.Difficulty(_character.nextRebirthDifficulty);
        }

        public ResetNativeObservation Select(DifficultyTransitionKind transition)
        {
            var target = transition == DifficultyTransitionKind.NormalToEvil
                ? NativeDifficultyCall.Evil : NativeDifficultyCall.Sadistic;
            return ResetNativeObservation.From(_native.SelectDifficulty(_character.rebirth, target));
        }

        public ResetNativeObservation Start(DifficultyTransitionKind transition)
        {
            var target = transition == DifficultyTransitionKind.NormalToEvil
                ? NativeDifficultyCall.Evil : NativeDifficultyCall.Sadistic;
            return ResetNativeObservation.From(_native.StartDifficulty(_character.rebirth, target));
        }

        private static bool Achievement(Character c, int index)
        {
            return c != null && c.achievements != null
                   && c.achievements.achievementComplete != null
                   && index >= 0 && index < c.achievements.achievementComplete.Count
                   && c.achievements.achievementComplete[index];
        }
    }

    internal sealed class LiveResetEpochBoundary : IResetEpochBoundary
    {
        private readonly Character _character;

        internal LiveResetEpochBoundary(Character character) { _character = character; }

        public void CloseVerifiedRun(ResetExecutionSnapshot after, string reason)
        {
            ResetEpochTransition.Close(_character, after, reason);
        }

        public void Quarantine(string reason)
        {
            ResetEpochTransition.Quarantine(reason);
        }
    }

    internal static class ResetEpochTransition
    {
        internal static void Close(Character c, ResetExecutionSnapshot after, string reason)
        {
            if (c == null || after == null)
            {
                Quarantine("verified reset could not publish a complete successor epoch");
                return;
            }
            // Keep this byte-for-byte aligned with Main.CaptureRunSignature so the observer
            // recognizes the synchronously published successor instead of advancing twice.
            var challenge = (after.InChallenge ? "active:" : "none:")
                            + after.CurrentChallengeTypeToken;
            var current = after.CurrentDifficulty.ToString();
            var next = after.NextDifficulty.ToString();
            var runSignature = after.RebirthNumber + "|" + current + "|next="
                               + next + "|" + challenge;
            var save = new SaveStateFingerprint(string.Empty, c.version, c.lastTime,
                after.RebirthNumber, current, after.HighestBoss,
                after.HighestHardBoss, after.HighestSadisticBoss, runSignature);
            var controllers = new ControllerIdentity(Identity(c), Identity(Main.Controller),
                Identity(Main.PlayerController));
            GameEpochController.Shared.AdvanceRun(save, controllers,
                string.IsNullOrEmpty(reason) ? "verified reset committed" : reason);
            ExecutionSafety.Invalidate("verified reset closed the prior run epoch");
        }

        internal static void Quarantine(string reason)
        {
            var detail = string.IsNullOrEmpty(reason) ? "reset result is indeterminate" : reason;
            GameEpochController.Shared.Quarantine(detail);
            ExecutionSafety.Invalidate(detail);
        }

        private static int Identity(object value)
        {
            return value == null ? 0 : RuntimeHelpers.GetHashCode(value);
        }
    }

    internal static class LiveResetSnapshot
    {
        internal static ResetExecutionSnapshot Capture(Character c)
        {
            if (c == null) return null;
            return new ResetExecutionSnapshot
            {
                RebirthNumber = c.stats == null ? -1L : c.stats.rebirthNumber,
                RebirthSeconds = c.rebirthTime == null ? double.NaN : c.rebirthTime.totalseconds,
                CurrentDifficulty = Difficulty(c.settings.rebirthDifficulty),
                NextDifficulty = Difficulty(c.nextRebirthDifficulty),
                NguLevelTrack = Difficulty(c.settings.nguLevelTrack),
                Number = new ResetNumberSnapshot
                {
                    CurrentAttack = c.attackMulti, CurrentDefense = c.defenseMulti,
                    NextAttack = c.nextAttackMulti, NextDefense = c.nextDefenseMulti,
                    BossMultiplier = c.bossMulti, TimeMultiplier = c.timeMulti,
                    OldBossMultiplier = c.oldBossMulti, OldTimeMultiplier = c.oldTimeMulti
                },
                BossId = c.bossID, CurrentHighestBoss = c.currentHighestBoss,
                HighestBoss = c.highestBoss, HighestHardBoss = c.highestHardBoss,
                HighestSadisticBoss = c.highestSadisticBoss,
                Achievement152 = Achievement(c, 152),
                InChallenge = c.challenges.inChallenge,
                ChallengeFlags = ChallengeFlags(c),
                CurrentChallengeTypeToken = c.challenges.curChallengeType.ToString(),
                ChallengeTimers = ChallengeTimers(c),
                TitanClocks = TitanClocks(c),
                TitanRunKillCounters = TitanRunKillCounters(c),
                PersistentStateFingerprint = PersistentFingerprint(c)
            };
        }

        internal static ResetDifficulty Difficulty(difficulty value)
        {
            return value == global::difficulty.normal ? ResetDifficulty.Normal
                : value == global::difficulty.evil ? ResetDifficulty.Evil
                : value == global::difficulty.sadistic ? ResetDifficulty.Sadistic
                : ResetDifficulty.Unknown;
        }

        internal static string NativeChallengeTypeToken(Character c, ChallengeType type)
        {
            switch (type)
            {
                case ChallengeType.Basic: return c.challenges.basicChallenge.challengeType.ToString();
                case ChallengeType.NoAug: return c.challenges.noAugsChallenge.challengeType.ToString();
                case ChallengeType.TwentyFourHour: return c.challenges.hour24Challenge.challengeType.ToString();
                case ChallengeType.OneHundredLC: return c.challenges.levelChallenge10k.challengeType.ToString();
                case ChallengeType.NoEquip: return c.challenges.noEquipmentChallenge.challengeType.ToString();
                case ChallengeType.Troll: return c.challenges.trollChallenge.challengeType.ToString();
                case ChallengeType.NoRebirth: return c.challenges.noRebirthChallenge.challengeType.ToString();
                case ChallengeType.LaserSword: return c.challenges.laserSwordChallenge.challengeType.ToString();
                case ChallengeType.Blind: return c.challenges.blindChallenge.challengeType.ToString();
                case ChallengeType.NoNGU: return c.challenges.nguChallenge.challengeType.ToString();
                case ChallengeType.NoTimeMachine: return c.challenges.timeMachineChallenge.challengeType.ToString();
                default: return string.Empty;
            }
        }

        private static bool Achievement(Character c, int index)
        {
            return c.achievements != null && c.achievements.achievementComplete != null
                   && index >= 0 && index < c.achievements.achievementComplete.Count
                   && c.achievements.achievementComplete[index];
        }

        private static bool[] ChallengeFlags(Character c)
        {
            return new[]
            {
                c.challenges.basicChallenge.inChallenge,
                c.challenges.noAugsChallenge.inChallenge,
                c.challenges.hour24Challenge.inChallenge,
                c.challenges.levelChallenge10k.inChallenge,
                c.challenges.noEquipmentChallenge.inChallenge,
                c.challenges.trollChallenge.inChallenge,
                c.challenges.noRebirthChallenge.inChallenge,
                c.challenges.laserSwordChallenge.inChallenge,
                c.challenges.blindChallenge.inChallenge,
                c.challenges.nguChallenge.inChallenge,
                c.challenges.timeMachineChallenge.inChallenge
            };
        }

        private static double[] ChallengeTimers(Character c)
        {
            return new[]
            {
                c.challenges.basicChallenge.challengeTime.totalseconds,
                c.challenges.noAugsChallenge.challengeTime.totalseconds,
                c.challenges.hour24Challenge.challengeTime.totalseconds,
                c.challenges.levelChallenge10k.challengeTime.totalseconds,
                c.challenges.noEquipmentChallenge.challengeTime.totalseconds,
                c.challenges.trollChallenge.challengeTime.totalseconds,
                c.challenges.noRebirthChallenge.challengeTime.totalseconds,
                c.challenges.laserSwordChallenge.challengeTime.totalseconds,
                c.challenges.blindChallenge.challengeTime.totalseconds,
                c.challenges.nguChallenge.challengeTime.totalseconds,
                c.challenges.timeMachineChallenge.challengeTime.totalseconds
            };
        }

        private static double[] TitanClocks(Character c)
        {
            return new[]
            {
                c.adventure.boss1Spawn.totalseconds, c.adventure.boss2Spawn.totalseconds,
                c.adventure.boss3Spawn.totalseconds, c.adventure.boss4Spawn.totalseconds,
                c.adventure.boss5Spawn.totalseconds, c.adventure.boss6Spawn.totalseconds,
                c.adventure.boss7Spawn.totalseconds, c.adventure.boss8Spawn.totalseconds,
                c.adventure.boss9Spawn.totalseconds, c.adventure.boss10Spawn.totalseconds,
                c.adventure.boss11Spawn.totalseconds, c.adventure.boss12Spawn.totalseconds,
                c.adventure.boss13Spawn.totalseconds, c.adventure.boss14Spawn.totalseconds
            };
        }

        private static int[] TitanRunKillCounters(Character c)
        {
            return new[]
            {
                c.adventure.boss5Kills, c.adventure.boss6Kills, c.adventure.boss7Kills,
                c.adventure.boss8Kills, c.adventure.boss9Kills, c.adventure.boss10Kills,
                c.adventure.boss11Kills, c.adventure.boss12Kills
            };
        }

        private static string PersistentFingerprint(Character c)
        {
            return "records=" + c.highestBoss + "/" + c.highestHardBoss + "/"
                   + c.highestSadisticBoss + "|exp=" + c.exp + "|pp=" + c.adventure.itopod.perkPoints
                   + "|qp=" + c.beastQuest.quirkPoints + "|ap=" + c.arbitrary.curArbitraryPoints;
        }
    }
}
