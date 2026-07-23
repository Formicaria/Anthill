using Anthill.Core.Configuration;
using Anthill.Core.Models;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.6.7 reliability capstone: the model-call outcome classifier and the per-provider circuit
/// breaker that fast-fails a dead/slow provider so it can't re-pin the single-writer queue (the
/// failure mode fixed in v2.6.6). Fully offline — the breaker uses an injected clock and the router
/// short-circuit test drives an already-open breaker, so nothing here makes a real network call.
/// </summary>
public class ModelReliabilityTests
{
    public ModelReliabilityTests() => AnthillRuntime.Initialize();

    // ---- Outcome classification (pins the client sentinel strings to outcomes) -----------------

    [Theory]
    [InlineData("Here is your answer.", ModelCallOutcome.Ok)]
    [InlineData("Ollama returned an empty response.", ModelCallOutcome.Empty)]
    [InlineData("OpenAI returned an empty response.", ModelCallOutcome.Empty)]
    [InlineData("ERROR: Ollama request cancelled because the mission was stopped.", ModelCallOutcome.Cancelled)]
    [InlineData("ERROR: Ollama request timed out after 120s (attempt 1/2).", ModelCallOutcome.Timeout)]
    [InlineData("ERROR: Could not connect to Ollama at http://x (refused).", ModelCallOutcome.ConnectError)]
    [InlineData("ERROR: Could not reach OpenAI: socket closed", ModelCallOutcome.ConnectError)]
    [InlineData("ERROR: OpenAI API key not configured. Add it in Settings → Providers.", ModelCallOutcome.ConfigError)]
    [InlineData("ERROR: OpenAI request failed (401): bad key", ModelCallOutcome.AuthError)]
    [InlineData("ERROR: Ollama at http://x is reachable but model 'llama3' is not available.", ModelCallOutcome.NotAvailable)]
    [InlineData("ERROR: Ollama at http://x answered HTTP 500.", ModelCallOutcome.HttpError)]
    [InlineData("ERROR: something else entirely", ModelCallOutcome.Error)]
    public void Classify_MapsClientSentinelsToOutcomes(string response, ModelCallOutcome expected)
    {
        Assert.Equal(expected, ModelCallOutcomeExtensions.Classify(response));
    }

    [Theory]
    [InlineData(ModelCallOutcome.Timeout, CircuitSignal.TransientFault)]
    [InlineData(ModelCallOutcome.ConnectError, CircuitSignal.TransientFault)]
    [InlineData(ModelCallOutcome.Cancelled, CircuitSignal.Neutral)]
    [InlineData(ModelCallOutcome.Error, CircuitSignal.Neutral)]
    [InlineData(ModelCallOutcome.Ok, CircuitSignal.Healthy)]
    [InlineData(ModelCallOutcome.AuthError, CircuitSignal.Healthy)]
    [InlineData(ModelCallOutcome.NotAvailable, CircuitSignal.Healthy)]
    public void ToCircuitSignal_OnlyTransportFaultsCountAgainstTheBreaker(ModelCallOutcome outcome, CircuitSignal expected)
    {
        Assert.Equal(expected, outcome.ToCircuitSignal());
    }

    // ---- Circuit breaker state machine ---------------------------------------------------------

    [Fact]
    public void Breaker_OpensAfterThreshold_FastFailsWhileOpen_ThenHalfOpenProbeCloses()
    {
        var now = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        var b = new ModelCircuitBreaker(threshold: 3, cooldownSeconds: 30, now: () => now);
        const string key = "ollama:llama3";

        Assert.Null(b.Blocked(key));                       // closed
        b.Record(key, CircuitSignal.TransientFault);       // 1
        b.Record(key, CircuitSignal.TransientFault);       // 2
        Assert.Null(b.Blocked(key));                       // still under threshold
        b.Record(key, CircuitSignal.TransientFault);       // 3 → trips
        Assert.True(b.IsOpen(key));
        Assert.NotNull(b.Blocked(key));                    // fast-fail while open

        now = now.AddSeconds(29);
        Assert.NotNull(b.Blocked(key));                    // cooldown not elapsed yet

        now = now.AddSeconds(2);                           // 31s total → half-open
        Assert.Null(b.Blocked(key));                       // exactly one probe admitted
        Assert.NotNull(b.Blocked(key));                    // concurrent callers still held back

        b.Record(key, CircuitSignal.Healthy);              // probe succeeds → close
        Assert.False(b.IsOpen(key));
        Assert.Null(b.Blocked(key));
    }

    [Fact]
    public void Breaker_FailedProbeReopens()
    {
        var now = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        var b = new ModelCircuitBreaker(threshold: 1, cooldownSeconds: 30, now: () => now);
        const string key = "ollama:llama3";

        b.Record(key, CircuitSignal.TransientFault);       // threshold 1 → open
        Assert.True(b.IsOpen(key));

        now = now.AddSeconds(31);
        Assert.Null(b.Blocked(key));                       // probe admitted
        b.Record(key, CircuitSignal.TransientFault);       // probe fails → reopen
        Assert.True(b.IsOpen(key));
        Assert.NotNull(b.Blocked(key));
    }

    [Fact]
    public void Breaker_NeutralIsIgnored_AndHealthyResetsTheFaultRun()
    {
        var now = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        var b = new ModelCircuitBreaker(threshold: 2, cooldownSeconds: 30, now: () => now);
        const string key = "ollama:llama3";

        b.Record(key, CircuitSignal.TransientFault);       // 1
        b.Record(key, CircuitSignal.Neutral);              // ignored — not a health signal
        Assert.Null(b.Blocked(key));                       // still 1 fault, under threshold
        b.Record(key, CircuitSignal.Healthy);              // resets the run
        b.Record(key, CircuitSignal.TransientFault);       // 1 again (not 2)
        Assert.Null(b.Blocked(key));                       // so the breaker stays closed
    }

    // ---- Router integration: an open breaker short-circuits before any network call ------------

    [Fact]
    public void ModelRouter_OpenBreaker_FastFailsWithoutANetworkCall()
    {
        AnthillRuntime.UseOllama = true;
        var now = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        var breaker = new ModelCircuitBreaker(threshold: 1, cooldownSeconds: 60, now: () => now);
        var router = new ModelRouter(memory: null, breaker: breaker);

        var (provider, model) = router.GetRoute("planner");
        breaker.Record($"{provider}:{model}", CircuitSignal.TransientFault); // open the route's breaker

        var result = router.Generate("planner", "say hi", retries: 1);       // must not touch the network

        Assert.StartsWith("ERROR:", result);
        Assert.Contains("temporarily unavailable", result);
        Assert.Contains("circuit open", result);
    }

    // ---- Provider health surface ---------------------------------------------------------------

    [Fact]
    public void Breaker_Snapshot_ReportsClosedOpenAndHalfOpen()
    {
        var now = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        var b = new ModelCircuitBreaker(threshold: 1, cooldownSeconds: 30, now: () => now);
        const string key = "ollama:llama3";

        Assert.Empty(b.Snapshot());                              // nothing exercised yet

        b.Record(key, CircuitSignal.TransientFault);             // → open
        var open = Assert.Single(b.Snapshot());
        Assert.Equal(key, open.Key);
        Assert.Equal("open", open.State);
        Assert.True(open.SecondsUntilClose > 0);

        now = now.AddSeconds(31);                                // cooldown elapsed → half-open
        Assert.Equal("half_open", Assert.Single(b.Snapshot()).State);

        b.Record(key, CircuitSignal.Healthy);                    // → closed
        Assert.Equal("closed", Assert.Single(b.Snapshot()).State);
    }

    [Fact]
    public void ModelRouter_ProviderHealth_ReflectsBreakerState()
    {
        var now = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        var breaker = new ModelCircuitBreaker(threshold: 1, cooldownSeconds: 60, now: () => now);
        var router = new ModelRouter(memory: null, breaker: breaker);

        Assert.Empty(router.ProviderHealth());

        var (provider, model) = router.GetRoute("planner");
        breaker.Record($"{provider}:{model}", CircuitSignal.TransientFault);

        var row = Assert.Single(router.ProviderHealth());
        Assert.Equal($"{provider}:{model}", row["route"]);
        Assert.Equal("open", row["state"]);
    }
}
