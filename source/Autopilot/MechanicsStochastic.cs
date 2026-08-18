/*
FILE PURPOSE

Purpose: Dependency-free numerical kernel for stochastic NGU Idle forecasts: first/repeated
successes, partial coupons, variable merge contributions, correlated batches, and discrete branches.

Mechanism: Stable elementary functions feed exact bounded probability kernels. Scalar contributions
use renewal DP and truncated-polynomial exponentiation. Correlated monotone batches use sparse DP up
to explicit caps, then return labelled conservative bounds. Branches retain all outcomes for mean,
VaR, and upper-tail CVaR.

Inputs and outputs: Pure probabilities, integer deficits/trials, and optional evidence/capacity proof
objects produce probabilities, minimal integer quantiles, or CompletionForecast objects.

Invariants and safety: Invalid/nonfinite probability inputs throw; PMFs must sum to one within 1e-9;
input arrays are cloned; impossible work is infinity rather than NaN; irreversible actions without
exact capacity support invalidate their forecasts. Loops have .NET 3.5-compatible explicit bounds.

Extension points and non-goals: Source-specific tables/cadence belong in adapters. This kernel never
asserts native independence, previews RNG, reads a save, or authorizes a mutation.
*/
using System;
using System.Collections.Generic;

namespace NGUInjector.Autopilot
{
    internal enum ForecastEvidenceGrade
    {
        Unknown, Heuristic, Sampled, DerivedExact, SourceExact, Bounded
    }

    internal sealed class ForecastEvidence
    {
        internal ForecastEvidenceGrade Grade;
        internal string ProbabilitySource;
        internal string CadenceSource;
        internal string SourceHash;
        internal double SampleAgeSeconds;
        internal int SampleCount;
        internal bool OnlineOnly;
        internal string Notes;

        internal ForecastEvidence()
        {
            Grade = ForecastEvidenceGrade.Unknown;
            ProbabilitySource = "unavailable";
            CadenceSource = "unavailable";
            SourceHash = "";
            SampleAgeSeconds = -1.0;
            Notes = "";
        }

        internal static ForecastEvidence Derived(string source)
        {
            return new ForecastEvidence
            {
                Grade = ForecastEvidenceGrade.DerivedExact,
                ProbabilitySource = source ?? "derived",
                CadenceSource = "trials",
                Notes = "Pure calculation from supplied outcomes."
            };
        }

        internal ForecastEvidence Clone()
        {
            return new ForecastEvidence
            {
                Grade = Grade,
                ProbabilitySource = ProbabilitySource,
                CadenceSource = CadenceSource,
                SourceHash = SourceHash,
                SampleAgeSeconds = SampleAgeSeconds,
                SampleCount = SampleCount,
                OnlineOnly = OnlineOnly,
                Notes = Notes
            };
        }
    }

    internal sealed class ForecastCapacityProof
    {
        internal bool Admitted;
        internal int RequiredTransientSlots;
        internal int AvailableTransientSlots;
        internal bool ContainsIrreversibleOutcome;
        internal bool ExactSupportProven;
        internal string Reason;

        internal static ForecastCapacityProof NotRequired()
        {
            return new ForecastCapacityProof
            {
                Admitted = true,
                ExactSupportProven = true,
                Reason = "No physical insertion represented."
            };
        }

        internal static ForecastCapacityProof Prove(int required, int available,
            bool irreversible, bool exactSupportProven, string reason)
        {
            if (required < 0) throw new ArgumentOutOfRangeException("required");
            if (available < 0) throw new ArgumentOutOfRangeException("available");
            return new ForecastCapacityProof
            {
                Admitted = available >= required && (!irreversible || exactSupportProven),
                RequiredTransientSlots = required,
                AvailableTransientSlots = available,
                ContainsIrreversibleOutcome = irreversible,
                ExactSupportProven = exactSupportProven,
                Reason = reason ?? ""
            };
        }

        internal ForecastCapacityProof Clone()
        {
            return new ForecastCapacityProof
            {
                Admitted = Admitted,
                RequiredTransientSlots = RequiredTransientSlots,
                AvailableTransientSlots = AvailableTransientSlots,
                ContainsIrreversibleOutcome = ContainsIrreversibleOutcome,
                ExactSupportProven = ExactSupportProven,
                Reason = Reason
            };
        }
    }

    internal sealed class CompletionForecast
    {
        internal double MeanTrials = double.PositiveInfinity;
        internal long P50Trials = long.MaxValue;
        internal long P90Trials = long.MaxValue;
        internal long P95Trials = long.MaxValue;
        internal long P99Trials = long.MaxValue;
        internal double LowerBoundMeanTrials = double.PositiveInfinity;
        internal double UpperBoundMeanTrials = double.PositiveInfinity;
        internal bool Exact;
        internal bool Bounded;
        internal bool Valid = true;
        internal string InvalidReason = "";
        internal ForecastEvidence Evidence = ForecastEvidence.Derived("stochastic-kernel");
        internal ForecastCapacityProof Capacity = ForecastCapacityProof.NotRequired();
    }

    internal sealed class ScalarProbability
    {
        internal readonly int Contribution;
        internal readonly double Probability;

        internal ScalarProbability(int contribution, double probability)
        {
            if (contribution < 0) throw new ArgumentOutOfRangeException("contribution");
            MechanicsStochastic.ValidateProbabilityValue(probability, "probability");
            Contribution = contribution;
            Probability = probability;
        }
    }

    internal sealed class VectorOutcome
    {
        private readonly int[] _contributions;
        internal readonly string Id;
        internal readonly double Probability;

        internal VectorOutcome(double probability, int[] contributions)
            : this("", probability, contributions) { }

        internal VectorOutcome(string id, double probability, int[] contributions)
        {
            MechanicsStochastic.ValidateProbabilityValue(probability, "probability");
            if (contributions == null) throw new ArgumentNullException("contributions");
            _contributions = (int[])contributions.Clone();
            for (var i = 0; i < _contributions.Length; i++)
                if (_contributions[i] < 0) throw new ArgumentOutOfRangeException("contributions");
            Id = id ?? "";
            Probability = probability;
        }

        internal int Dimension { get { return _contributions.Length; } }
        internal int ContributionAt(int index) { return _contributions[index]; }
        internal int[] Contributions() { return (int[])_contributions.Clone(); }
    }

    internal sealed class ValuedOutcome
    {
        internal readonly string Id;
        internal readonly double Probability;
        internal readonly double Value;

        internal ValuedOutcome(double probability, double value) : this("", probability, value) { }
        internal ValuedOutcome(string id, double probability, double value)
        {
            MechanicsStochastic.ValidateProbabilityValue(probability, "probability");
            if (double.IsNaN(value) || double.IsNegativeInfinity(value))
                throw new ArgumentOutOfRangeException("value");
            Id = id ?? "";
            Probability = probability;
            Value = value;
        }
    }

    internal sealed class BranchRiskForecast
    {
        internal bool Valid;
        internal string InvalidReason;
        internal double ExpectedValue;
        internal double ValueAtRisk;
        internal double UpperTailCvar;
        internal double MinimumValue;
        internal double MaximumValue;
        internal double Confidence;
        internal ForecastEvidence Evidence;
        internal ForecastCapacityProof Capacity;
    }

    internal sealed class StochasticActionModel
    {
        private readonly VectorOutcome[] _outcomes;
        internal readonly string Id;
        internal readonly double Seconds;
        internal readonly int WorstCaseTransientSlots;
        internal readonly bool ContainsIrreversibleOutcome;
        internal readonly ForecastEvidence Evidence;
        internal readonly ForecastCapacityProof Capacity;

        internal StochasticActionModel(string id, double seconds, IList<VectorOutcome> outcomes,
            int worstCaseTransientSlots, bool irreversible, ForecastEvidence evidence,
            ForecastCapacityProof capacity)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Action id required.", "id");
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
                throw new ArgumentOutOfRangeException("seconds");
            if (worstCaseTransientSlots < 0)
                throw new ArgumentOutOfRangeException("worstCaseTransientSlots");
            _outcomes = MechanicsStochastic.CopyAndValidateVectorOutcomes(outcomes, -1);
            Id = id;
            Seconds = seconds;
            WorstCaseTransientSlots = worstCaseTransientSlots;
            ContainsIrreversibleOutcome = irreversible;
            Evidence = evidence == null ? new ForecastEvidence() : evidence.Clone();
            Capacity = capacity == null ? ForecastCapacityProof.NotRequired() : capacity.Clone();
        }

        internal VectorOutcome[] Outcomes() { return (VectorOutcome[])_outcomes.Clone(); }
    }

    internal static class MechanicsStochastic
    {
        private const double MassTolerance = 1e-9;
        private const int MaximumBinomialTerms = 100000;
        private const int MaximumCouponMissingTypes = 30;
        private const long MaximumSparseQuantileTrials = 1000000L;

        internal static double StableLogOneMinus(double x)
        {
            ValidateProbabilityValue(x, "x");
            if (x == 1.0) return double.NegativeInfinity;
            if (x == 0.0) return 0.0;
            if (x >= 1e-4) return Math.Log(1.0 - x);
            var power = x;
            var sum = 0.0;
            for (var k = 1; k <= 64; k++)
            {
                var term = power / k;
                sum += term;
                if (term <= Math.Abs(sum) * 2.2204460492503131e-16) break;
                power *= x;
            }
            return -sum;
        }

        internal static double StableExpm1(double x)
        {
            if (double.IsNaN(x)) throw new ArgumentOutOfRangeException("x");
            if (double.IsPositiveInfinity(x)) return double.PositiveInfinity;
            if (double.IsNegativeInfinity(x)) return -1.0;
            if (Math.Abs(x) >= 1e-5) return Math.Exp(x) - 1.0;
            var term = x;
            var sum = x;
            for (var k = 2; k <= 64; k++)
            {
                term *= x / k;
                sum += term;
                if (Math.Abs(term) <= Math.Max(1.0, Math.Abs(sum)) * 2.2204460492503131e-16)
                    break;
            }
            return sum;
        }

        internal static double GeometricMeanTrials(double p)
        {
            ValidateProbabilityValue(p, "successProbability");
            return p == 0.0 ? double.PositiveInfinity : 1.0 / p;
        }

        internal static long GeometricMedianTrials(double p)
        {
            return GeometricQuantileTrials(p, 0.5);
        }

        internal static long GeometricQuantileTrials(double p, double confidence)
        {
            ValidateProbabilityValue(p, "successProbability");
            ValidateConfidence(confidence);
            if (confidence == 0.0) return 0L;
            if (p == 0.0) return long.MaxValue;
            if (p == 1.0) return 1L;
            var raw = Math.Ceiling(StableLogOneMinus(confidence) / StableLogOneMinus(p));
            if (double.IsInfinity(raw) || raw >= long.MaxValue) return long.MaxValue;
            var result = Math.Max(1L, (long)raw);
            if (result < 9007199254740992L)
            {
                while (result > 1L && ProbabilityAtLeastOne(result - 1L, p) >= confidence) result--;
                while (ProbabilityAtLeastOne(result, p) < confidence) result++;
            }
            return result;
        }

        internal static double ProbabilityAtLeastOne(long trials, double p)
        {
            if (trials < 0L) throw new ArgumentOutOfRangeException("trials");
            ValidateProbabilityValue(p, "successProbability");
            if (trials == 0L || p == 0.0) return 0.0;
            if (p == 1.0) return 1.0;
            return Clamp(-StableExpm1(trials * StableLogOneMinus(p)));
        }

        internal static double GeometricMeanSeconds(double p, double secondsPerTrial)
        {
            if (double.IsNaN(secondsPerTrial) || double.IsInfinity(secondsPerTrial) || secondsPerTrial < 0.0)
                throw new ArgumentOutOfRangeException("secondsPerTrial");
            ValidateProbabilityValue(p, "successProbability");
            if (p == 0.0) return double.PositiveInfinity;
            return secondsPerTrial / p;
        }

        internal static double ExpectedTrialsForCopies(int copies, double p)
        {
            if (copies < 0) throw new ArgumentOutOfRangeException("requiredCopies");
            ValidateProbabilityValue(p, "copyProbability");
            if (copies == 0) return 0.0;
            return p == 0.0 ? double.PositiveInfinity : copies / p;
        }

        internal static double NegativeBinomialVarianceTrials(int successes, double p)
        {
            if (successes < 0) throw new ArgumentOutOfRangeException("requiredSuccesses");
            ValidateProbabilityValue(p, "probability");
            if (successes == 0) return 0.0;
            if (p == 0.0) return double.PositiveInfinity;
            return successes * (1.0 - p) / (p * p);
        }

        internal static double BinomialProbability(int successes, long trials, double p)
        {
            if (trials < 0L) throw new ArgumentOutOfRangeException("trials");
            ValidateProbabilityValue(p, "probability");
            if (successes < 0 || successes > trials) return 0.0;
            if (p == 0.0) return successes == 0 ? 1.0 : 0.0;
            if (p == 1.0) return successes == trials ? 1.0 : 0.0;
            var log = LogBinomialProbability(successes, trials, p);
            return log < -745.0 ? 0.0 : Clamp(Math.Exp(log));
        }

        internal static double BinomialAtLeast(int successes, long trials, double p)
        {
            if (trials < 0L) throw new ArgumentOutOfRangeException("trials");
            ValidateProbabilityValue(p, "probability");
            if (successes <= 0) return 1.0;
            if (successes > trials || p == 0.0) return 0.0;
            if (p == 1.0) return 1.0;
            if (successes == 1) return ProbabilityAtLeastOne(trials, p);
            if (successes > MaximumBinomialTerms)
                throw new ArgumentOutOfRangeException("successes", "Bounded term cap exceeded.");
            var logP = Math.Log(p);
            var logQ = StableLogOneMinus(p);
            var logTerm = trials * logQ;
            var logFailure = logTerm;
            for (var k = 1; k < successes; k++)
            {
                logTerm += Math.Log((double)(trials - k + 1L)) - Math.Log((double)k) + logP - logQ;
                logFailure = LogAdd(logFailure, logTerm);
            }
            return logFailure >= 0.0 ? 0.0 : Clamp(-StableExpm1(logFailure));
        }

        internal static double NegativeBinomialCompletionProbability(int successes, long trials, double p)
        {
            if (successes < 0) throw new ArgumentOutOfRangeException("requiredSuccesses");
            return BinomialAtLeast(successes, trials, p);
        }

        internal static long NegativeBinomialQuantileTrials(int successes, double p, double confidence)
        {
            if (successes < 0) throw new ArgumentOutOfRangeException("requiredSuccesses");
            ValidateProbabilityValue(p, "probability");
            ValidateConfidence(confidence);
            if (confidence == 0.0 || successes == 0) return 0L;
            if (p == 0.0) return long.MaxValue;
            if (p == 1.0) return successes;
            if (successes > MaximumBinomialTerms)
                throw new ArgumentOutOfRangeException("requiredSuccesses", "Bounded term cap exceeded.");
            var lower = (long)successes - 1L;
            var upper = Math.Max((long)successes, SaturatingCeiling(successes / p));
            while (NegativeBinomialCompletionProbability(successes, upper, p) < confidence)
            {
                lower = upper;
                if (upper >= long.MaxValue / 2L) return long.MaxValue;
                upper *= 2L;
            }
            while (lower + 1L < upper)
            {
                var middle = lower + (upper - lower) / 2L;
                if (NegativeBinomialCompletionProbability(successes, middle, p) >= confidence) upper = middle;
                else lower = middle;
            }
            return upper;
        }

        internal static CompletionForecast NegativeBinomialForecast(int successes, double p)
        {
            var mean = ExpectedTrialsForCopies(successes, p);
            return ExactForecast(mean,
                NegativeBinomialQuantileTrials(successes, p, 0.50),
                NegativeBinomialQuantileTrials(successes, p, 0.90),
                NegativeBinomialQuantileTrials(successes, p, 0.95),
                NegativeBinomialQuantileTrials(successes, p, 0.99),
                ForecastEvidence.Derived("negative-binomial"), ForecastCapacityProof.NotRequired());
        }

        internal static int AdditionalLevelZeroCopiesToMax(int level)
        {
            if (level < 0 || level > 100) throw new ArgumentOutOfRangeException("heldItemLevel");
            return 100 - level;
        }

        internal static int TotalLevelZeroCopiesForFreshMax() { return 101; }

        internal static double UniformCouponMeanEmissions(int totalTypes, int missingTypes)
        {
            ValidateCouponShape(totalTypes, missingTypes);
            var harmonic = 0.0;
            for (var i = 1; i <= missingTypes; i++) harmonic += 1.0 / i;
            return totalTypes * harmonic;
        }

        internal static double UniformCouponMeanTrials(int totalTypes, int missingTypes, double groupChance)
        {
            ValidateProbabilityValue(groupChance, "groupEmissionProbability");
            var emissions = UniformCouponMeanEmissions(totalTypes, missingTypes);
            if (emissions == 0.0) return 0.0;
            return groupChance == 0.0 ? double.PositiveInfinity : emissions / groupChance;
        }

        // Compatibility means a fresh group, m=r. Partial groups must use the explicit overload.
        internal static double CouponCollectorMeanUsefulDrops(int remainingUniformTypes)
        {
            return UniformCouponMeanEmissions(remainingUniformTypes, remainingUniformTypes);
        }

        internal static double CouponCollectorMeanUsefulDrops(int totalTypes, int missingTypes)
        {
            return UniformCouponMeanEmissions(totalTypes, missingTypes);
        }

        internal static double CouponCollectorMeanTrialsUniform(int remainingTypes, double groupChance)
        {
            return UniformCouponMeanTrials(remainingTypes, remainingTypes, groupChance);
        }

        internal static double CouponCollectorMeanTrialsUniform(int totalTypes, int missingTypes, double groupChance)
        {
            return UniformCouponMeanTrials(totalTypes, missingTypes, groupChance);
        }

        internal static double UniformCouponCompletionProbability(int totalTypes, int missingTypes,
            double groupChance, long trials)
        {
            ValidateCouponShape(totalTypes, missingTypes);
            ValidateProbabilityValue(groupChance, "groupEmissionProbability");
            if (trials < 0L) throw new ArgumentOutOfRangeException("trials");
            if (missingTypes == 0) return 1.0;
            if (trials < missingTypes || groupChance == 0.0) return 0.0;
            if (missingTypes > MaximumCouponMissingTypes)
                throw new ArgumentOutOfRangeException("missingTypes", "Bounded term cap exceeded.");
            var sum = 0.0;
            var compensation = 0.0;
            var choose = 1.0;
            for (var j = 0; j <= missingTypes; j++)
            {
                if (j > 0) choose *= (double)(missingTypes - j + 1) / j;
                var noHit = 1.0 - groupChance * j / totalTypes;
                var power = noHit <= 0.0 ? 0.0 : Math.Exp(trials * Math.Log(noHit));
                var signed = (j & 1) == 0 ? choose * power : -choose * power;
                var adjusted = signed - compensation;
                var next = sum + adjusted;
                compensation = (next - sum) - adjusted;
                sum = next;
            }
            return Clamp(sum);
        }

        internal static long UniformCouponQuantileTrials(int totalTypes, int missingTypes,
            double groupChance, double confidence)
        {
            ValidateCouponShape(totalTypes, missingTypes);
            ValidateProbabilityValue(groupChance, "groupEmissionProbability");
            ValidateConfidence(confidence);
            if (confidence == 0.0 || missingTypes == 0) return 0L;
            if (groupChance == 0.0) return long.MaxValue;
            var lower = (long)missingTypes - 1L;
            var upper = Math.Max((long)missingTypes,
                SaturatingCeiling(UniformCouponMeanTrials(totalTypes, missingTypes, groupChance)));
            while (UniformCouponCompletionProbability(totalTypes, missingTypes, groupChance, upper) < confidence)
            {
                lower = upper;
                if (upper >= long.MaxValue / 2L) return long.MaxValue;
                upper *= 2L;
            }
            while (lower + 1L < upper)
            {
                var middle = lower + (upper - lower) / 2L;
                if (UniformCouponCompletionProbability(totalTypes, missingTypes, groupChance, middle) >= confidence)
                    upper = middle;
                else lower = middle;
            }
            return upper;
        }

        internal static CompletionForecast ContributionForecast(int deficit,
            IList<ScalarProbability> contributionPmf)
        {
            return ContributionForecast(deficit, contributionPmf,
                ForecastEvidence.Derived("renewal-contribution"), ForecastCapacityProof.NotRequired());
        }

        internal static CompletionForecast ContributionForecast(int deficit,
            IList<ScalarProbability> contributionPmf, ForecastEvidence evidence,
            ForecastCapacityProof capacity)
        {
            if (deficit < 0) throw new ArgumentOutOfRangeException("remainingContribution");
            var proof = capacity == null ? ForecastCapacityProof.NotRequired() : capacity.Clone();
            if (!proof.Admitted)
                return InvalidForecast("Capacity proof rejected the contribution forecast.", evidence, proof);
            var pmf = NormalizeScalarPmf(contributionPmf);
            if (deficit == 0) return ExactForecast(0.0, 0L, 0L, 0L, 0L, evidence, proof);
            var progress = 0.0;
            for (var i = 0; i < pmf.Length; i++)
                if (pmf[i].Contribution > 0) progress += pmf[i].Probability;
            if (progress == 0.0)
                return ExactForecast(double.PositiveInfinity, long.MaxValue, long.MaxValue,
                    long.MaxValue, long.MaxValue, evidence, proof);

            var means = new double[deficit + 1];
            for (var remaining = 1; remaining <= deficit; remaining++)
            {
                var continuation = 0.0;
                for (var i = 0; i < pmf.Length; i++)
                    if (pmf[i].Contribution > 0)
                        continuation += pmf[i].Probability
                            * means[Math.Max(0, remaining - pmf[i].Contribution)];
                means[remaining] = (1.0 + continuation) / progress;
            }
            return ExactForecast(means[deficit],
                ContributionQuantileTrials(deficit, pmf, 0.50),
                ContributionQuantileTrials(deficit, pmf, 0.90),
                ContributionQuantileTrials(deficit, pmf, 0.95),
                ContributionQuantileTrials(deficit, pmf, 0.99), evidence, proof);
        }

        internal static double ContributionCompletionProbability(int deficit,
            IList<ScalarProbability> contributionPmf, long trials)
        {
            if (deficit < 0) throw new ArgumentOutOfRangeException("remainingContribution");
            if (trials < 0L) throw new ArgumentOutOfRangeException("trials");
            return ContributionCompletionProbability(deficit, NormalizeScalarPmf(contributionPmf), trials);
        }

        internal static long ContributionQuantileTrials(int deficit,
            IList<ScalarProbability> contributionPmf, double confidence)
        {
            if (deficit < 0) throw new ArgumentOutOfRangeException("remainingContribution");
            ValidateConfidence(confidence);
            return ContributionQuantileTrials(deficit, NormalizeScalarPmf(contributionPmf), confidence);
        }

        internal static CompletionForecast SparseMonotoneForecast(byte[] initialDeficits,
            IList<VectorOutcome> outcomes, int maxStates)
        {
            return SparseMonotoneForecast(initialDeficits, outcomes, maxStates,
                ForecastEvidence.Derived("correlated-sparse-monotone"),
                ForecastCapacityProof.NotRequired());
        }

        internal static CompletionForecast SparseMonotoneForecast(byte[] initialDeficits,
            IList<VectorOutcome> outcomes, int maxStates, ForecastEvidence evidence,
            ForecastCapacityProof capacity)
        {
            if (initialDeficits == null) throw new ArgumentNullException("initialDeficits");
            if (initialDeficits.Length == 0)
                throw new ArgumentException("At least one deficit required.", "initialDeficits");
            if (maxStates < 1) throw new ArgumentOutOfRangeException("maxStates");
            var proof = capacity == null ? ForecastCapacityProof.NotRequired() : capacity.Clone();
            if (!proof.Admitted)
                return InvalidForecast("Capacity proof rejected the correlated outcome forecast.", evidence, proof);
            var copied = CopyAndValidateVectorOutcomes(outcomes, initialDeficits.Length);
            var deficits = (byte[])initialDeficits.Clone();
            if (IsComplete(deficits)) return ExactForecast(0.0, 0L, 0L, 0L, 0L, evidence, proof);
            try
            {
                var mean = new SparseMeanContext(copied, maxStates).Mean(deficits);
                if (double.IsPositiveInfinity(mean))
                    return ExactForecast(mean, long.MaxValue, long.MaxValue,
                        long.MaxValue, long.MaxValue, evidence, proof);
                var quantiles = SparseQuantiles(deficits, copied, maxStates);
                if (quantiles == null) return BoundedSparseForecast(deficits, copied, evidence, proof);
                return ExactForecast(mean, quantiles[0], quantiles[1], quantiles[2], quantiles[3],
                    evidence, proof);
            }
            catch (StateCapExceededException)
            {
                return BoundedSparseForecast(deficits, copied, evidence, proof);
            }
        }

        internal static double ExpectedBranchValue(IList<ValuedOutcome> outcomes)
        {
            var copied = CopyAndValidateValuedOutcomes(outcomes);
            var total = 0.0;
            for (var i = 0; i < copied.Length; i++)
            {
                if (copied[i].Probability == 0.0) continue;
                if (double.IsPositiveInfinity(copied[i].Value)) return double.PositiveInfinity;
                total += copied[i].Probability * copied[i].Value;
            }
            return total;
        }

        internal static BranchRiskForecast EvaluateBranchRisk(IList<ValuedOutcome> outcomes,
            double confidence)
        {
            return EvaluateBranchRisk(outcomes, confidence,
                ForecastEvidence.Derived("discrete-branch"), ForecastCapacityProof.NotRequired());
        }

        internal static BranchRiskForecast EvaluateBranchRisk(IList<ValuedOutcome> outcomes,
            double confidence, ForecastEvidence evidence, ForecastCapacityProof capacity)
        {
            ValidateConfidence(confidence);
            var proof = capacity == null ? ForecastCapacityProof.NotRequired() : capacity.Clone();
            var result = new BranchRiskForecast
            {
                Valid = proof.Admitted,
                InvalidReason = proof.Admitted ? "" : "Capacity proof rejected the branch forecast.",
                Confidence = confidence,
                Evidence = evidence == null ? new ForecastEvidence() : evidence.Clone(),
                Capacity = proof
            };
            if (!proof.Admitted)
            {
                result.ExpectedValue = result.ValueAtRisk = result.UpperTailCvar =
                    result.MinimumValue = result.MaximumValue = double.PositiveInfinity;
                return result;
            }
            var copied = CopyAndValidateValuedOutcomes(outcomes);
            Array.Sort(copied, delegate(ValuedOutcome left, ValuedOutcome right)
            {
                return left.Value.CompareTo(right.Value);
            });
            result.ExpectedValue = ExpectedBranchValue(copied);
            result.MinimumValue = copied[0].Value;
            result.MaximumValue = copied[copied.Length - 1].Value;
            var cumulative = 0.0;
            result.ValueAtRisk = result.MaximumValue;
            for (var i = 0; i < copied.Length; i++)
            {
                cumulative += copied[i].Probability;
                if (cumulative + MassTolerance >= confidence)
                {
                    result.ValueAtRisk = copied[i].Value;
                    break;
                }
            }
            var tailMass = 1.0 - confidence;
            var remaining = tailMass;
            var weightedTail = 0.0;
            for (var i = copied.Length - 1; i >= 0 && remaining > 0.0; i--)
            {
                var take = Math.Min(remaining, copied[i].Probability);
                if (take <= 0.0) continue;
                if (double.IsPositiveInfinity(copied[i].Value))
                {
                    weightedTail = double.PositiveInfinity;
                    break;
                }
                weightedTail += take * copied[i].Value;
                remaining -= take;
            }
            result.UpperTailCvar = double.IsPositiveInfinity(weightedTail)
                ? double.PositiveInfinity : weightedTail / tailMass;
            return result;
        }

        internal static void ValidateProbabilityValue(double probability, string parameterName)
        {
            if (double.IsNaN(probability) || double.IsInfinity(probability)
                || probability < 0.0 || probability > 1.0)
                throw new ArgumentOutOfRangeException(parameterName);
        }

        internal static VectorOutcome[] CopyAndValidateVectorOutcomes(
            IList<VectorOutcome> outcomes, int requiredDimension)
        {
            if (outcomes == null) throw new ArgumentNullException("outcomes");
            if (outcomes.Count == 0)
                throw new ArgumentException("At least one outcome required.", "outcomes");
            var copied = new VectorOutcome[outcomes.Count];
            var dimension = requiredDimension;
            var total = 0.0;
            for (var i = 0; i < outcomes.Count; i++)
            {
                var outcome = outcomes[i];
                if (outcome == null) throw new ArgumentException("Null outcome.", "outcomes");
                if (dimension < 0) dimension = outcome.Dimension;
                if (outcome.Dimension != dimension)
                    throw new ArgumentException("Outcome dimensions must match.", "outcomes");
                copied[i] = new VectorOutcome(outcome.Id, outcome.Probability, outcome.Contributions());
                total += outcome.Probability;
            }
            ValidateMass(total, "outcomes");
            if (total != 1.0)
                for (var i = 0; i < copied.Length; i++)
                    copied[i] = new VectorOutcome(copied[i].Id, copied[i].Probability / total,
                        copied[i].Contributions());
            return copied;
        }

        private static CompletionForecast ExactForecast(double mean, long p50, long p90,
            long p95, long p99, ForecastEvidence evidence, ForecastCapacityProof capacity)
        {
            return new CompletionForecast
            {
                MeanTrials = mean,
                P50Trials = p50,
                P90Trials = p90,
                P95Trials = p95,
                P99Trials = p99,
                LowerBoundMeanTrials = mean,
                UpperBoundMeanTrials = mean,
                Exact = true,
                Bounded = false,
                Valid = true,
                Evidence = evidence == null ? ForecastEvidence.Derived("stochastic-kernel") : evidence.Clone(),
                Capacity = capacity == null ? ForecastCapacityProof.NotRequired() : capacity.Clone()
            };
        }

        private static CompletionForecast InvalidForecast(string reason,
            ForecastEvidence evidence, ForecastCapacityProof capacity)
        {
            return new CompletionForecast
            {
                Exact = false,
                Bounded = false,
                Valid = false,
                InvalidReason = reason,
                Evidence = evidence == null ? new ForecastEvidence() : evidence.Clone(),
                Capacity = capacity == null ? ForecastCapacityProof.NotRequired() : capacity.Clone()
            };
        }

        private static CompletionForecast BoundedSparseForecast(byte[] deficits,
            VectorOutcome[] outcomes, ForecastEvidence evidence, ForecastCapacityProof capacity)
        {
            var itemCount = 0;
            for (var i = 0; i < deficits.Length; i++) if (deficits[i] > 0) itemCount++;
            if (itemCount == 0) return ExactForecast(0.0, 0L, 0L, 0L, 0L, evidence, capacity);
            var lowerMean = 0.0;
            var upperMean = 0.0;
            var p50 = 0L;
            var p90 = 0L;
            var p95 = 0L;
            var p99 = 0L;
            for (var dimension = 0; dimension < deficits.Length; dimension++)
            {
                if (deficits[dimension] == 0) continue;
                var marginal = new ScalarProbability[outcomes.Length];
                for (var i = 0; i < outcomes.Length; i++)
                    marginal[i] = new ScalarProbability(outcomes[i].ContributionAt(dimension),
                        outcomes[i].Probability);
                var forecast = ContributionForecast(deficits[dimension], marginal);
                lowerMean = Math.Max(lowerMean, forecast.MeanTrials);
                upperMean = AddSaturating(upperMean, forecast.MeanTrials);
                p50 = AddSaturating(p50, ContributionQuantileTrials(deficits[dimension], marginal,
                    AdjustedUnionConfidence(0.50, itemCount)));
                p90 = AddSaturating(p90, ContributionQuantileTrials(deficits[dimension], marginal,
                    AdjustedUnionConfidence(0.90, itemCount)));
                p95 = AddSaturating(p95, ContributionQuantileTrials(deficits[dimension], marginal,
                    AdjustedUnionConfidence(0.95, itemCount)));
                p99 = AddSaturating(p99, ContributionQuantileTrials(deficits[dimension], marginal,
                    AdjustedUnionConfidence(0.99, itemCount)));
            }
            var boundedEvidence = evidence == null ? new ForecastEvidence() : evidence.Clone();
            boundedEvidence.Grade = ForecastEvidenceGrade.Bounded;
            boundedEvidence.Notes = "Cap reached; mean bounds are max marginal/sequential sum; quantiles are sequential union bounds.";
            return new CompletionForecast
            {
                MeanTrials = upperMean,
                P50Trials = p50,
                P90Trials = p90,
                P95Trials = p95,
                P99Trials = p99,
                LowerBoundMeanTrials = lowerMean,
                UpperBoundMeanTrials = upperMean,
                Exact = false,
                Bounded = true,
                Valid = true,
                Evidence = boundedEvidence,
                Capacity = capacity == null ? ForecastCapacityProof.NotRequired() : capacity.Clone()
            };
        }

        private static ScalarProbability[] NormalizeScalarPmf(IList<ScalarProbability> pmf)
        {
            if (pmf == null) throw new ArgumentNullException("contributionPmf");
            if (pmf.Count == 0)
                throw new ArgumentException("At least one PMF entry required.", "contributionPmf");
            var grouped = new SortedDictionary<int, double>();
            var total = 0.0;
            for (var i = 0; i < pmf.Count; i++)
            {
                if (pmf[i] == null) throw new ArgumentException("Null PMF entry.", "contributionPmf");
                double current;
                grouped.TryGetValue(pmf[i].Contribution, out current);
                grouped[pmf[i].Contribution] = current + pmf[i].Probability;
                total += pmf[i].Probability;
            }
            ValidateMass(total, "contributionPmf");
            var result = new ScalarProbability[grouped.Count];
            var index = 0;
            foreach (var pair in grouped)
                result[index++] = new ScalarProbability(pair.Key, pair.Value / total);
            return result;
        }

        private static double ContributionCompletionProbability(int deficit,
            ScalarProbability[] pmf, long trials)
        {
            if (deficit == 0) return 1.0;
            if (trials == 0L) return 0.0;
            var result = new double[deficit + 1];
            result[0] = 1.0;
            var power = new double[deficit + 1];
            for (var i = 0; i < pmf.Length; i++)
                power[Math.Min(deficit, pmf[i].Contribution)] += pmf[i].Probability;
            var exponent = trials;
            while (exponent > 0L)
            {
                if ((exponent & 1L) != 0L) result = Convolve(result, power, deficit);
                exponent >>= 1;
                if (exponent > 0L) power = Convolve(power, power, deficit);
            }
            return Clamp(result[deficit]);
        }

        private static long ContributionQuantileTrials(int deficit,
            ScalarProbability[] pmf, double confidence)
        {
            if (confidence == 0.0 || deficit == 0) return 0L;
            var progress = 0.0;
            for (var i = 0; i < pmf.Length; i++)
                if (pmf[i].Contribution > 0) progress += pmf[i].Probability;
            if (progress == 0.0) return long.MaxValue;
            var lower = 0L;
            var upper = 1L;
            while (ContributionCompletionProbability(deficit, pmf, upper) < confidence)
            {
                lower = upper;
                if (upper >= long.MaxValue / 2L) return long.MaxValue;
                upper *= 2L;
            }
            while (lower + 1L < upper)
            {
                var middle = lower + (upper - lower) / 2L;
                if (ContributionCompletionProbability(deficit, pmf, middle) >= confidence) upper = middle;
                else lower = middle;
            }
            return upper;
        }

        private static double[] Convolve(double[] left, double[] right, int cap)
        {
            var result = new double[cap + 1];
            for (var i = 0; i <= cap; i++)
            {
                if (left[i] == 0.0) continue;
                for (var j = 0; j <= cap; j++)
                {
                    if (right[j] == 0.0) continue;
                    result[Math.Min(cap, i + j)] += left[i] * right[j];
                }
            }
            return result;
        }

        private static long[] SparseQuantiles(byte[] initial, VectorOutcome[] outcomes, int maxStates)
        {
            var targets = new[] { 0.50, 0.90, 0.95, 0.99 };
            var quantiles = new[] { -1L, -1L, -1L, -1L };
            var states = new Dictionary<string, SparseMass>();
            states.Add(Encode(initial), new SparseMass(initial, 1.0));
            for (var trial = 1L; trial <= MaximumSparseQuantileTrials; trial++)
            {
                var next = new Dictionary<string, SparseMass>();
                foreach (var pair in states)
                {
                    if (IsComplete(pair.Value.State))
                    {
                        AddMass(next, pair.Value.State, pair.Value.Probability, maxStates);
                        continue;
                    }
                    for (var i = 0; i < outcomes.Length; i++)
                        if (outcomes[i].Probability > 0.0)
                            AddMass(next, Transition(pair.Value.State, outcomes[i]),
                                pair.Value.Probability * outcomes[i].Probability, maxStates);
                }
                states = next;
                SparseMass complete;
                var completed = states.TryGetValue(new string('\0', initial.Length), out complete)
                    ? Clamp(complete.Probability) : 0.0;
                for (var q = 0; q < targets.Length; q++)
                    if (quantiles[q] < 0L && completed + MassTolerance >= targets[q])
                        quantiles[q] = trial;
                if (quantiles[3] >= 0L) return quantiles;
            }
            return null;
        }

        private static void AddMass(Dictionary<string, SparseMass> states, byte[] state,
            double probability, int maxStates)
        {
            if (probability == 0.0) return;
            var key = Encode(state);
            SparseMass existing;
            if (states.TryGetValue(key, out existing))
            {
                existing.Probability += probability;
                return;
            }
            if (states.Count >= maxStates) throw new StateCapExceededException();
            states.Add(key, new SparseMass(state, probability));
        }

        private static byte[] Transition(byte[] state, VectorOutcome outcome)
        {
            var next = new byte[state.Length];
            for (var i = 0; i < state.Length; i++)
                next[i] = (byte)Math.Max(0, state[i] - outcome.ContributionAt(i));
            return next;
        }

        private static bool IsComplete(byte[] state)
        {
            for (var i = 0; i < state.Length; i++) if (state[i] != 0) return false;
            return true;
        }

        private static string Encode(byte[] state)
        {
            var chars = new char[state.Length];
            for (var i = 0; i < state.Length; i++) chars[i] = (char)state[i];
            return new string(chars);
        }

        private static ValuedOutcome[] CopyAndValidateValuedOutcomes(IList<ValuedOutcome> outcomes)
        {
            if (outcomes == null) throw new ArgumentNullException("outcomes");
            if (outcomes.Count == 0)
                throw new ArgumentException("At least one outcome required.", "outcomes");
            var copied = new ValuedOutcome[outcomes.Count];
            var total = 0.0;
            for (var i = 0; i < outcomes.Count; i++)
            {
                if (outcomes[i] == null) throw new ArgumentException("Null outcome.", "outcomes");
                copied[i] = new ValuedOutcome(outcomes[i].Id, outcomes[i].Probability, outcomes[i].Value);
                total += outcomes[i].Probability;
            }
            ValidateMass(total, "outcomes");
            if (total != 1.0)
                for (var i = 0; i < copied.Length; i++)
                    copied[i] = new ValuedOutcome(copied[i].Id, copied[i].Probability / total, copied[i].Value);
            return copied;
        }

        private static void ValidateCouponShape(int totalTypes, int missingTypes)
        {
            if (totalTypes < 0) throw new ArgumentOutOfRangeException("totalTypes");
            if (missingTypes < 0 || missingTypes > totalTypes)
                throw new ArgumentOutOfRangeException("missingTypes");
        }

        private static void ValidateConfidence(double confidence)
        {
            if (double.IsNaN(confidence) || double.IsInfinity(confidence)
                || confidence < 0.0 || confidence >= 1.0)
                throw new ArgumentOutOfRangeException("confidence");
        }

        private static void ValidateMass(double total, string parameterName)
        {
            if (double.IsNaN(total) || double.IsInfinity(total) || Math.Abs(total - 1.0) > MassTolerance)
                throw new ArgumentException("Probabilities must sum to one within 1e-9.", parameterName);
        }

        private static double LogBinomialProbability(int successes, long trials, double p)
        {
            var complement = trials - successes;
            var terms = Math.Min((long)successes, complement);
            if (terms > MaximumBinomialTerms)
                throw new ArgumentOutOfRangeException("successes", "Bounded term cap exceeded.");
            var logChoose = 0.0;
            for (var i = 1L; i <= terms; i++)
                logChoose += Math.Log((double)(trials - terms + i)) - Math.Log((double)i);
            return logChoose + successes * Math.Log(p) + complement * StableLogOneMinus(p);
        }

        private static double LogAdd(double left, double right)
        {
            var maximum = Math.Max(left, right);
            return maximum + Math.Log(Math.Exp(left - maximum) + Math.Exp(right - maximum));
        }

        private static double Clamp(double value)
        {
            if (value <= 0.0) return 0.0;
            if (value >= 1.0) return 1.0;
            return value;
        }

        private static long SaturatingCeiling(double value)
        {
            if (double.IsNaN(value) || value <= 0.0) return 0L;
            if (double.IsPositiveInfinity(value) || value >= long.MaxValue) return long.MaxValue;
            return (long)Math.Ceiling(value);
        }

        private static long AddSaturating(long left, long right)
        {
            return left == long.MaxValue || right == long.MaxValue || right > long.MaxValue - left
                ? long.MaxValue : left + right;
        }

        private static double AddSaturating(double left, double right)
        {
            return double.IsPositiveInfinity(left) || double.IsPositiveInfinity(right)
                ? double.PositiveInfinity : left + right;
        }

        private static double AdjustedUnionConfidence(double confidence, int itemCount)
        {
            return 1.0 - (1.0 - confidence) / itemCount;
        }

        private sealed class SparseMeanContext
        {
            private readonly VectorOutcome[] _outcomes;
            private readonly int _maxStates;
            private readonly Dictionary<string, double> _memo = new Dictionary<string, double>();
            private readonly Dictionary<string, bool> _active = new Dictionary<string, bool>();

            internal SparseMeanContext(VectorOutcome[] outcomes, int maxStates)
            {
                _outcomes = outcomes;
                _maxStates = maxStates;
            }

            internal double Mean(byte[] state)
            {
                if (IsComplete(state)) return 0.0;
                var key = Encode(state);
                double known;
                if (_memo.TryGetValue(key, out known)) return known;
                if (_memo.Count + _active.Count >= _maxStates) throw new StateCapExceededException();
                if (_active.ContainsKey(key)) throw new InvalidOperationException("Non-monotone cycle.");
                _active.Add(key, true);
                var progress = 0.0;
                var continuation = 0.0;
                for (var i = 0; i < _outcomes.Length; i++)
                {
                    if (_outcomes[i].Probability == 0.0) continue;
                    var next = Transition(state, _outcomes[i]);
                    if (Encode(next) == key) continue;
                    // Sum non-self mass directly. Computing 1-self would erase source-exact
                    // probabilities below machine epsilon when the self mass rounds to one.
                    progress += _outcomes[i].Probability;
                    var nextMean = Mean(next);
                    if (double.IsPositiveInfinity(nextMean)) continuation = double.PositiveInfinity;
                    else if (!double.IsPositiveInfinity(continuation))
                        continuation += _outcomes[i].Probability * nextMean;
                }
                _active.Remove(key);
                // A source-exact rare branch is progress even when its probability is below the
                // PMF sum-validation tolerance. Only a literal zero makes the state unreachable.
                var mean = progress <= 0.0 ? double.PositiveInfinity
                    : (1.0 + continuation) / progress;
                _memo.Add(key, mean);
                return mean;
            }
        }

        private sealed class SparseMass
        {
            internal readonly byte[] State;
            internal double Probability;
            internal SparseMass(byte[] state, double probability)
            {
                State = (byte[])state.Clone();
                Probability = probability;
            }
        }

        private sealed class StateCapExceededException : Exception { }
    }
}
