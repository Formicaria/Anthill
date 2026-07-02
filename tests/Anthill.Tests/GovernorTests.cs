using Anthill.Core.Autonomy;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Phase 3 ResourceGovernor: sizes effective autonomy concurrency from injected host/backend
/// signals. The governor may only ever lower the configured cap — never raise it — halving on
/// soft pressure, clamping to 1 on hard pressure or an unreachable model backend, and failing
/// open (full cap) when a host signal simply can't be read.
/// </summary>
public class GovernorTests
{
    private static ResourceGovernor Governor(
        double? loadPerCore = null, double? memAvailableFraction = null, BackendProbe? probe = null) =>
        new(() => loadPerCore, () => memAvailableFraction, () => probe);

    [Fact]
    public void HealthySignals_GrantFullConfiguredConcurrency()
    {
        var decision = Governor(loadPerCore: 0.2, memAvailableFraction: 0.7, probe: new BackendProbe(true, 40)).Evaluate(4);
        Assert.Equal(4, decision.EffectiveConcurrency);
        Assert.Equal("ok", decision.Code);
    }

    [Fact]
    public void UnreadableSignals_FailOpenToConfiguredCap()
    {
        // No /proc, no probe (e.g. Ollama disabled): nothing to clamp on.
        var decision = Governor().Evaluate(3);
        Assert.Equal(3, decision.EffectiveConcurrency);
        Assert.Equal("ok", decision.Code);
    }

    [Fact]
    public void HighLoad_HalvesConcurrency()
    {
        var decision = Governor(loadPerCore: 1.5).Evaluate(4);
        Assert.Equal(2, decision.EffectiveConcurrency);
        Assert.Equal("load_high", decision.Code);
    }

    [Fact]
    public void CriticalLoad_ClampsToOne()
    {
        var decision = Governor(loadPerCore: 2.5).Evaluate(4);
        Assert.Equal(1, decision.EffectiveConcurrency);
        Assert.Equal("load_critical", decision.Code);
    }

    [Fact]
    public void LowMemory_HalvesConcurrency()
    {
        var decision = Governor(memAvailableFraction: 0.15).Evaluate(4);
        Assert.Equal(2, decision.EffectiveConcurrency);
        Assert.Equal("memory_low", decision.Code);
    }

    [Fact]
    public void CriticalMemory_ClampsToOne()
    {
        var decision = Governor(memAvailableFraction: 0.05).Evaluate(4);
        Assert.Equal(1, decision.EffectiveConcurrency);
        Assert.Equal("memory_critical", decision.Code);
    }

    [Fact]
    public void UnreachableBackend_ClampsToOne()
    {
        var decision = Governor(loadPerCore: 0.1, probe: new BackendProbe(false, 4000)).Evaluate(4);
        Assert.Equal(1, decision.EffectiveConcurrency);
        Assert.Equal("backend_unreachable", decision.Code);
    }

    [Fact]
    public void SlowBackend_HalvesConcurrency()
    {
        var decision = Governor(probe: new BackendProbe(true, 3000)).Evaluate(4);
        Assert.Equal(2, decision.EffectiveConcurrency);
        Assert.Equal("backend_slow", decision.Code);
    }

    [Fact]
    public void TightestConstraintWins()
    {
        // Soft load (→2) + unreachable backend (→1): the hardest clamp is the answer.
        var decision = Governor(loadPerCore: 1.5, probe: new BackendProbe(false, 100)).Evaluate(4);
        Assert.Equal(1, decision.EffectiveConcurrency);
        Assert.Equal("backend_unreachable", decision.Code);
    }

    [Fact]
    public void NeverExceedsRequested_AndNeverBelowOne()
    {
        Assert.Equal(1, Governor(loadPerCore: 0.1).Evaluate(1).EffectiveConcurrency);
        Assert.Equal(1, Governor(loadPerCore: 9.9).Evaluate(1).EffectiveConcurrency);
        Assert.Equal(1, Governor().Evaluate(0).EffectiveConcurrency); // degenerate input normalized
    }

    [Fact]
    public void ThrowingSignalReader_IsTreatedAsUnreadable()
    {
        var governor = new ResourceGovernor(
            () => throw new IOException("boom"),
            () => throw new IOException("boom"),
            () => null);
        var decision = governor.Evaluate(2);
        Assert.Equal(2, decision.EffectiveConcurrency);
        Assert.Equal("ok", decision.Code);
    }
}
