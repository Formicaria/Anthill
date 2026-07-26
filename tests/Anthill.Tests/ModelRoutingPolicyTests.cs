using System.Collections.Generic;
using System.Linq;
using Anthill.Core.Models;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.11.1 — model routing intelligence is deterministic and explainable: stats aggregate
/// correctly, low-sample routes get the benefit of the doubt, low-risk work favors the fastest
/// healthy route, and high-risk work favors the configured route's stability until it is proven
/// unhealthy. Every choice carries a reason.
/// </summary>
public class ModelRoutingPolicyTests
{
    private static ModelCallRecord Ok(string prov, string model, double ms) => new(prov, model, true, ms);
    private static ModelCallRecord Fail(string prov, string model, double ms) => new(prov, model, false, ms);

    [Fact]
    public void Aggregate_ComputesSuccessRateAndAvgLatency()
    {
        var records = new[]
        {
            Ok("ollama", "a", 100), Ok("ollama", "a", 300), Fail("ollama", "a", 200),
        };
        var stats = ModelStats.Aggregate(records);
        var h = stats["ollama:a"];
        Assert.Equal(3, h.Calls);
        Assert.Equal(2, h.Successes);
        Assert.Equal(2d / 3d, h.SuccessRate, 3);
        Assert.Equal(200d, h.AvgLatencyMs, 3);
    }

    [Fact]
    public void Health_LowSampleIsHealthy_ManyFailuresIsNot()
    {
        var lowSample = ModelStats.Aggregate(new[] { Fail("p", "m", 50) });
        Assert.True(lowSample["p:m"].Healthy); // one bad sample doesn't condemn a route

        var proven = ModelStats.Aggregate(Enumerable.Range(0, 10).Select(_ => Fail("p", "m", 50)));
        Assert.False(proven["p:m"].Healthy);
    }

    [Fact]
    public void Choose_NoStats_KeepsConfiguredRoute()
    {
        var choice = ModelRoutingPolicy.Choose("low", ("ollama", "base"),
            new[] { ("ollama", "alt") }, new Dictionary<string, RouteHealth>());
        Assert.Equal("ollama", choice.Provider);
        Assert.Equal("base", choice.Model);
    }

    [Fact]
    public void Choose_LowRisk_PicksFastestHealthyRoute()
    {
        var stats = ModelStats.Aggregate(
            Enumerable.Range(0, 10).SelectMany(_ => new[] { Ok("ollama", "slow", 900), Ok("ollama", "fast", 150) }));

        var choice = ModelRoutingPolicy.Choose("low", ("ollama", "slow"),
            new[] { ("ollama", "fast") }, stats);

        Assert.Equal("fast", choice.Model);
        Assert.Contains("fast", choice.Reason);
    }

    [Fact]
    public void Choose_HighRisk_KeepsConfiguredWhileHealthy_EvenIfAlternateIsFaster()
    {
        var stats = ModelStats.Aggregate(
            Enumerable.Range(0, 10).SelectMany(_ => new[] { Ok("ollama", "stable", 900), Ok("ollama", "fast", 150) }));

        var choice = ModelRoutingPolicy.Choose("high", ("ollama", "stable"),
            new[] { ("ollama", "fast") }, stats);

        Assert.Equal("stable", choice.Model); // stability wins for high-risk
        Assert.Contains("stability", choice.Reason);
    }

    [Fact]
    public void Choose_HighRisk_ReroutesWhenConfiguredIsUnhealthy()
    {
        var records = new List<ModelCallRecord>();
        records.AddRange(Enumerable.Range(0, 10).Select(_ => Fail("ollama", "broken", 50)));
        records.AddRange(Enumerable.Range(0, 10).Select(_ => Ok("ollama", "healthy", 300)));
        var stats = ModelStats.Aggregate(records);

        var choice = ModelRoutingPolicy.Choose("critical", ("ollama", "broken"),
            new[] { ("ollama", "healthy") }, stats);

        Assert.Equal("healthy", choice.Model);
        Assert.Contains("rerouted", choice.Reason);
    }

    [Fact]
    public void Choose_AllUnhealthy_KeepsConfiguredAndSaysSo()
    {
        var records = new List<ModelCallRecord>();
        records.AddRange(Enumerable.Range(0, 10).Select(_ => Fail("ollama", "base", 50)));
        records.AddRange(Enumerable.Range(0, 10).Select(_ => Fail("ollama", "alt", 50)));
        var stats = ModelStats.Aggregate(records);

        var choice = ModelRoutingPolicy.Choose("low", ("ollama", "base"),
            new[] { ("ollama", "alt") }, stats);

        Assert.Equal("base", choice.Model);
        Assert.Contains("unhealthy", choice.Reason);
    }
}
