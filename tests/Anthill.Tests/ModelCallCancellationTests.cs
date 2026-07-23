using System.Diagnostics;
using Anthill.Core.Configuration;
using Anthill.Core.Models;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.6.6 reliability fix: model HTTP calls are now bounded AND cancellable. A mission publishes a
/// deadline/cancel token through <see cref="ModelCallScope"/>; every <see cref="IModelClient"/> links
/// it into each request so a timed-out or cancelled mission aborts an in-flight generation instead of
/// pinning the single-writer job queue for minutes. These tests exercise the ambient-cancel path with
/// an already-cancelled token, so they short-circuit before any real network I/O and stay offline-safe.
/// </summary>
public class ModelCallCancellationTests
{
    public ModelCallCancellationTests() => AnthillRuntime.Initialize();

    [Fact]
    public void ModelCallScope_DefaultsToNoneOutsideAnyScope()
    {
        Assert.False(ModelCallScope.Current.CanBeCanceled);
    }

    [Fact]
    public void ModelCallScope_EntersNestsAndRestoresOnDispose()
    {
        using var outerCts = new CancellationTokenSource();
        using var innerCts = new CancellationTokenSource();

        Assert.False(ModelCallScope.Current.CanBeCanceled);
        using (ModelCallScope.Enter(outerCts.Token))
        {
            Assert.Equal(outerCts.Token, ModelCallScope.Current);
            using (ModelCallScope.Enter(innerCts.Token))
                Assert.Equal(innerCts.Token, ModelCallScope.Current); // nested scope overrides
            Assert.Equal(outerCts.Token, ModelCallScope.Current);     // inner dispose restores outer
        }
        Assert.False(ModelCallScope.Current.CanBeCanceled);           // outer dispose restores None
    }

    [Fact]
    public void OllamaClient_AbortsCleanly_WhenAmbientTokenAlreadyCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var client = new OllamaClient("test-model", "http://127.0.0.1:1"); // unroutable — must never be reached
        var sw = Stopwatch.StartNew();
        string result;
        using (ModelCallScope.Enter(cts.Token))
            result = client.Generate("say hi", retries: 3);
        sw.Stop();

        Assert.StartsWith("ERROR:", result);
        Assert.Contains("cancelled because the mission was stopped", result);
        // A cancelled mission must not retry or wait out any timeout — it returns effectively instantly.
        Assert.True(sw.Elapsed.TotalSeconds < 5, $"cancel should short-circuit, took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void OpenAiCompatibleClient_AbortsCleanly_WhenAmbientTokenAlreadyCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // A non-empty key is required to reach the request path (an empty key fails closed earlier).
        var client = new OpenAiCompatibleClient("OpenAI", "http://127.0.0.1:1/v1", "sk-test", "test-model");
        string result;
        using (ModelCallScope.Enter(cts.Token))
            result = client.Generate("say hi", retries: 3);

        Assert.StartsWith("ERROR:", result);
        Assert.Contains("cancelled because the mission was stopped", result);
    }

    [Fact]
    public void ModelCallTimeout_IsPositiveAndBoundedByMissionDeadline()
    {
        // The per-call bound must be tighter than the whole-mission deadline, otherwise a single call
        // could consume the entire mission budget and the deadline check could never fire between tasks.
        Assert.True(AnthillRuntime.ModelCallTimeoutSeconds > 0);
        Assert.True(AnthillRuntime.ModelCallTimeoutSeconds <= AnthillRuntime.MaxMissionSeconds);
    }
}
