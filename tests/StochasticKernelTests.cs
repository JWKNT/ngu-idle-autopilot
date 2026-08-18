using System;
using System.Collections.Generic;

/*
FILE PURPOSE

This isolated executable regression-tests the pure stochastic numerical kernel. It loads no Unity
or game assembly, reads no save/runtime data, uses no RNG, and performs no mutation. Golden vectors
cover extreme probabilities, minimal quantiles, partial coupons, renewal overshoot, correlated
batches, bounded fallback, capacity invalidation, branch CVaR, provenance, and defensive copying.
*/
internal static class StochasticKernelTests
{
    private static int _assertions;

    private static void Assert(bool value, string message)
    {
        _assertions++;
        if (!value) throw new Exception("FAIL: " + message);
    }

    private static void Equal(long expected, long actual, string message)
    {
        _assertions++;
        if (expected != actual)
            throw new Exception("FAIL: " + message + ": expected " + expected + ", got " + actual);
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        _assertions++;
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            throw new Exception("FAIL: " + message + ": expected " + expected + ", got " + actual);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        _assertions++;
        try { action(); }
        catch (T) { return; }
        throw new Exception("FAIL: " + message);
    }

    private static void TestStableGeometric()
    {
        Near(4.0, NGUInjector.Autopilot.MechanicsStochastic.GeometricMeanTrials(.25), 1e-12,
            "geometric mean");
        Equal(3L, NGUInjector.Autopilot.MechanicsStochastic.GeometricMedianTrials(.25),
            "geometric median");
        Near(.578125, NGUInjector.Autopilot.MechanicsStochastic.ProbabilityAtLeastOne(3, .25),
            1e-15, "geometric CDF");
        var rare = NGUInjector.Autopilot.MechanicsStochastic.ProbabilityAtLeastOne(100, 1e-14);
        Assert(rare > 9.99e-13 && rare < 1.001e-12,
            "small probability CDF does not cancel to zero");
        var rareQ = NGUInjector.Autopilot.MechanicsStochastic.GeometricQuantileTrials(1e-14, .90);
        Assert(rareQ > 0 && rareQ < long.MaxValue, "small probability quantile remains finite");
        Assert(NGUInjector.Autopilot.MechanicsStochastic.ProbabilityAtLeastOne(rareQ, 1e-14) >= .90,
            "geometric quantile reaches confidence");
        Assert(NGUInjector.Autopilot.MechanicsStochastic.ProbabilityAtLeastOne(rareQ - 1, 1e-14) < .90,
            "geometric quantile is minimal");
        Assert(double.IsPositiveInfinity(
            NGUInjector.Autopilot.MechanicsStochastic.GeometricMeanSeconds(0.0, 0.0)),
            "impossible zero-duration event is infinity, not NaN");
        Throws<ArgumentOutOfRangeException>(delegate
        {
            NGUInjector.Autopilot.MechanicsStochastic.GeometricMeanSeconds(.5, double.PositiveInfinity);
        }, "infinite cadence rejected consistently");
    }

    private static void TestBinomialAndNegativeBinomial()
    {
        var sum = 0.0;
        for (var k = 0; k <= 10; k++)
            sum += NGUInjector.Autopilot.MechanicsStochastic.BinomialProbability(k, 10, .25);
        Near(1.0, sum, 1e-12, "binomial PMF normalizes");
        Near(.578125, NGUInjector.Autopilot.MechanicsStochastic.BinomialAtLeast(1, 3, .25),
            1e-15, "binomial at-least-one matches geometric");
        Equal(997L, NGUInjector.Autopilot.MechanicsStochastic.NegativeBinomialQuantileTrials(100, .1, .50),
            "negative-binomial p50 golden");
        Equal(1123L, NGUInjector.Autopilot.MechanicsStochastic.NegativeBinomialQuantileTrials(100, .1, .90),
            "negative-binomial p90 golden");
        Equal(1161L, NGUInjector.Autopilot.MechanicsStochastic.NegativeBinomialQuantileTrials(100, .1, .95),
            "negative-binomial p95 golden");
        Equal(1235L, NGUInjector.Autopilot.MechanicsStochastic.NegativeBinomialQuantileTrials(100, .1, .99),
            "negative-binomial p99 golden");
        Equal(7L, NGUInjector.Autopilot.MechanicsStochastic.NegativeBinomialQuantileTrials(2, .25, .50),
            "two-success p50 golden");
        Equal(15L, NGUInjector.Autopilot.MechanicsStochastic.NegativeBinomialQuantileTrials(2, .25, .90),
            "two-success p90 golden");
        var q = NGUInjector.Autopilot.MechanicsStochastic.NegativeBinomialQuantileTrials(7, .17, .93);
        Assert(NGUInjector.Autopilot.MechanicsStochastic.NegativeBinomialCompletionProbability(7, q, .17) >= .93,
            "negative-binomial quantile reaches confidence");
        Assert(NGUInjector.Autopilot.MechanicsStochastic.NegativeBinomialCompletionProbability(7, q - 1, .17) < .93,
            "negative-binomial quantile is minimal");
        var forecast = NGUInjector.Autopilot.MechanicsStochastic.NegativeBinomialForecast(100, .1);
        Near(1000.0, forecast.MeanTrials, 1e-9, "negative-binomial forecast mean");
        Assert(forecast.Exact && forecast.Valid && !forecast.Bounded,
            "negative-binomial forecast exactness labelled");
        Throws<ArgumentOutOfRangeException>(delegate
        {
            NGUInjector.Autopilot.MechanicsStochastic.BinomialAtLeast(0, 1, double.NaN);
        }, "invalid binomial probability rejected even for vacuous tail");
    }

    private static void TestPartialCoupons()
    {
        Near(5.5, NGUInjector.Autopilot.MechanicsStochastic.CouponCollectorMeanUsefulDrops(3),
            1e-12, "fresh compatibility coupon mean");
        Near(12.0, NGUInjector.Autopilot.MechanicsStochastic.UniformCouponMeanEmissions(8, 2),
            1e-12, "partial coupon retains total universe");
        Near(24.0, NGUInjector.Autopilot.MechanicsStochastic.UniformCouponMeanTrials(8, 2, .5),
            1e-12, "partial coupon group chance");
        Equal(23L, NGUInjector.Autopilot.MechanicsStochastic.UniformCouponQuantileTrials(8, 2, 1.0, .90),
            "partial coupon p90");
        Equal(47L, NGUInjector.Autopilot.MechanicsStochastic.UniformCouponQuantileTrials(8, 2, .5, .90),
            "partial coupon thinned p90");
        Near(0.0, NGUInjector.Autopilot.MechanicsStochastic.UniformCouponCompletionProbability(8, 2, 1.0, 0),
            0.0, "coupon n=0 boundary");
        var q = NGUInjector.Autopilot.MechanicsStochastic.UniformCouponQuantileTrials(8, 2, .5, .95);
        Assert(NGUInjector.Autopilot.MechanicsStochastic.UniformCouponCompletionProbability(8, 2, .5, q) >= .95,
            "coupon quantile reaches confidence");
        Assert(NGUInjector.Autopilot.MechanicsStochastic.UniformCouponCompletionProbability(8, 2, .5, q - 1) < .95,
            "coupon quantile minimal");
    }

    private static List<NGUInjector.Autopilot.ScalarProbability> RenewalPmf()
    {
        return new List<NGUInjector.Autopilot.ScalarProbability>
        {
            new NGUInjector.Autopilot.ScalarProbability(0, .50),
            new NGUInjector.Autopilot.ScalarProbability(1, .25),
            new NGUInjector.Autopilot.ScalarProbability(3, .25)
        };
    }

    private static void TestRenewalContributions()
    {
        var forecast = NGUInjector.Autopilot.MechanicsStochastic.ContributionForecast(5, RenewalPmf());
        Near(5.875, forecast.MeanTrials, 1e-12, "renewal mean accounts for overshoot");
        Equal(5L, forecast.P50Trials, "renewal p50");
        Equal(10L, forecast.P90Trials, "renewal p90");
        Equal(11L, forecast.P95Trials, "renewal p95");
        Assert(NGUInjector.Autopilot.MechanicsStochastic.ContributionCompletionProbability(5, RenewalPmf(), 10) >= .90,
            "renewal CDF reaches p90 at golden trial");
        Assert(NGUInjector.Autopilot.MechanicsStochastic.ContributionCompletionProbability(5, RenewalPmf(), 9) < .90,
            "renewal p90 is minimal");
        var levelFifty = new List<NGUInjector.Autopilot.ScalarProbability>
        {
            new NGUInjector.Autopilot.ScalarProbability(51, 1.0)
        };
        Equal(2L, NGUInjector.Autopilot.MechanicsStochastic.ContributionQuantileTrials(100, levelFifty, .99),
            "two level-50 sources finish deficit 100");
        var ultraRare = new List<NGUInjector.Autopilot.ScalarProbability>
        {
            new NGUInjector.Autopilot.ScalarProbability(0, 1.0 - 1e-14),
            new NGUInjector.Autopilot.ScalarProbability(1, 1e-14)
        };
        var rareForecast = NGUInjector.Autopilot.MechanicsStochastic.ContributionForecast(1, ultraRare);
        Assert(!double.IsInfinity(rareForecast.MeanTrials) && rareForecast.MeanTrials > 9.9e13,
            "positive progress below PMF tolerance remains finite");
    }

    private static void TestCorrelatedSparseBatchesAndCapacity()
    {
        var raw = new[] { 0, 0 };
        var noProgress = new NGUInjector.Autopilot.VectorOutcome("none", .5, raw);
        raw[0] = 99;
        var both = new NGUInjector.Autopilot.VectorOutcome("both", .5, new[] { 1, 1 });
        var outcomes = new List<NGUInjector.Autopilot.VectorOutcome> { noProgress, both };
        var exact = NGUInjector.Autopilot.MechanicsStochastic.SparseMonotoneForecast(
            new byte[] { 1, 1 }, outcomes, 8);
        Near(2.0, exact.MeanTrials, 1e-12, "correlated all-or-none batch mean");
        Equal(1L, exact.P50Trials, "correlated batch p50");
        Equal(4L, exact.P90Trials, "correlated batch p90");
        Equal(7L, exact.P99Trials, "correlated batch p99");
        Assert(exact.Exact && noProgress.ContributionAt(0) == 0,
            "outcome contributions defensively copied");

        var bounded = NGUInjector.Autopilot.MechanicsStochastic.SparseMonotoneForecast(
            new byte[] { 2, 2 }, outcomes, 1);
        Assert(bounded.Valid && bounded.Bounded && !bounded.Exact,
            "state-cap overflow is visibly bounded");
        Near(4.0, bounded.LowerBoundMeanTrials, 1e-12, "bounded lower mean is max marginal");
        Near(8.0, bounded.UpperBoundMeanTrials, 1e-12, "bounded upper mean is sequential sum");
        Assert(bounded.Evidence.Grade == NGUInjector.Autopilot.ForecastEvidenceGrade.Bounded,
            "bounded forecast provenance labelled");

        var rejected = NGUInjector.Autopilot.ForecastCapacityProof.Prove(2, 1, true, true,
            "unique batch has insufficient slots");
        var invalid = NGUInjector.Autopilot.MechanicsStochastic.SparseMonotoneForecast(
            new byte[] { 1, 1 }, outcomes, 8,
            NGUInjector.Autopilot.ForecastEvidence.Derived("source-table"), rejected);
        Assert(!invalid.Valid && !invalid.Exact && double.IsPositiveInfinity(invalid.MeanTrials),
            "capacity failure invalidates forecast instead of pricing item loss");
        var unsupported = NGUInjector.Autopilot.ForecastCapacityProof.Prove(1, 100, true, false,
            "expected-value capacity is not exact support");
        Assert(!unsupported.Admitted, "irreversible outcome requires exact support proof");
    }

    private static void TestBranchesAndValidation()
    {
        var branches = new List<NGUInjector.Autopilot.ValuedOutcome>
        {
            new NGUInjector.Autopilot.ValuedOutcome("ordinary", .8, 1.0),
            new NGUInjector.Autopilot.ValuedOutcome("tail", .2, 10.0)
        };
        Near(2.8, NGUInjector.Autopilot.MechanicsStochastic.ExpectedBranchValue(branches), 1e-12,
            "discrete expectation retains branches");
        var risk = NGUInjector.Autopilot.MechanicsStochastic.EvaluateBranchRisk(branches, .8);
        Near(1.0, risk.ValueAtRisk, 1e-12, "branch VaR");
        Near(10.0, risk.UpperTailCvar, 1e-12, "upper-tail CVaR");
        Assert(risk.Valid && risk.Evidence.Grade == NGUInjector.Autopilot.ForecastEvidenceGrade.DerivedExact,
            "branch provenance carried");
        Throws<ArgumentException>(delegate
        {
            NGUInjector.Autopilot.MechanicsStochastic.ExpectedBranchValue(
                new[] { new NGUInjector.Autopilot.ValuedOutcome(.9, 1.0) });
        }, "branch mass must sum to one");
        Throws<ArgumentException>(delegate
        {
            NGUInjector.Autopilot.MechanicsStochastic.ContributionForecast(2,
                new[] { new NGUInjector.Autopilot.ScalarProbability(1, .8) });
        }, "contribution mass must sum to one");
        Throws<ArgumentOutOfRangeException>(delegate
        {
            NGUInjector.Autopilot.MechanicsStochastic.UniformCouponMeanEmissions(2, 3);
        }, "missing coupon count cannot exceed universe");
        Throws<ArgumentOutOfRangeException>(delegate
        {
            NGUInjector.Autopilot.MechanicsStochastic.GeometricMeanTrials(double.NaN);
        }, "NaN probability rejected");
    }

    public static int Main()
    {
        TestStableGeometric();
        TestBinomialAndNegativeBinomial();
        TestPartialCoupons();
        TestRenewalContributions();
        TestCorrelatedSparseBatchesAndCapacity();
        TestBranchesAndValidation();
        Console.WriteLine("Stochastic kernel regression tests passed (" + _assertions + " assertions).");
        return 0;
    }
}
