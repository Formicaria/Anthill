using Anthill.Core.Models;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.4.0 — the capability cache the MODEL CALL PATH reads.
///
/// This exists because discovery was originally wired into the /providers/capabilities endpoint and
/// nowhere else: the page reported that gemma4:31b supports tools while the client negotiated
/// against a name table that had never heard of it, stripped the tools, and the model — never shown
/// one — answered from priors and claimed a tool had told it so.
///
/// Two properties are load-bearing and neither is obvious from the type:
///   - For() must do NO I/O. The first version fetched inside it, on the call path, and hung the
///     suite: the stub server answered the capability request instead of the chat request, and the
///     real call then blocked to the 120s provider timeout.
///   - An unknown model must fall back, never be granted anything.
/// </summary>
public class OllamaCapabilityCacheTests : IDisposable
{
    private const string Host = "http://127.0.0.1:65530";   // deliberately dead: nothing may dial it

    public OllamaCapabilityCacheTests() => OllamaCapabilityCache.Invalidate();
    public void Dispose() => OllamaCapabilityCache.Invalidate();

    /// <summary>
    /// The headline guarantee: reading capabilities never touches the network. The host here is a
    /// closed port, so any I/O would cost a connection failure and — in the version that regressed —
    /// a multi-second stall on every model call.
    /// </summary>
    [Fact]
    public void Reading_DoesNoIO_EvenAgainstADeadHost()
    {
        var started = DateTime.UtcNow;
        for (var i = 0; i < 50; i++) OllamaCapabilityCache.For(Host, "anything:latest");
        var elapsed = DateTime.UtcNow - started;

        Assert.True(elapsed < TimeSpan.FromSeconds(1),
            $"50 capability reads took {elapsed.TotalSeconds:F1}s — something is doing I/O on the call path");
    }

    [Fact]
    public void AReportedModel_UsesWhatTheRuntimeSaid()
    {
        OllamaCapabilityCache.Seed(Host, new Dictionary<string, ModelCapabilities>
        {
            ["gemma4:31b"] = ModelCapabilities.FromOllama(new[] { "completion", "tools", "thinking" }),
            ["llama2-uncensored:70b"] = ModelCapabilities.FromOllama(new[] { "completion" }),
        });

        Assert.True(OllamaCapabilityCache.For(Host, "gemma4:31b").ToolCalling);
        Assert.True(OllamaCapabilityCache.For(Host, "gemma4:31b").Reasoning);
        Assert.False(OllamaCapabilityCache.For(Host, "llama2-uncensored:70b").ToolCalling);
    }

    /// <summary>
    /// An unknown model falls back to the declared table — and, failing that, to text-only. A
    /// runtime we have not asked can fail to CONFIRM a capability, never grant one.
    /// </summary>
    [Fact]
    public void AnUnknownModel_FallsBack_AndIsNeverGrantedTools()
    {
        OllamaCapabilityCache.Seed(Host, new Dictionary<string, ModelCapabilities>
        {
            ["gemma4:31b"] = ModelCapabilities.FromOllama(new[] { "completion", "tools" }),
        });

        Assert.False(OllamaCapabilityCache.For(Host, "something-nobody-has-described").ToolCalling);
    }

    /// <summary>
    /// The fallback is the name table, so a model the table DOES know keeps its capability even
    /// when the runtime has not described it — discovery adds knowledge, it does not remove it.
    /// </summary>
    [Fact]
    public void AModelTheTableKnows_KeepsItsCapability_WhenUndiscovered()
    {
        OllamaCapabilityCache.Seed(Host, new Dictionary<string, ModelCapabilities>());
        Assert.True(OllamaCapabilityCache.For(Host, "llama3.1:8b").ToolCalling);
    }

    /// <summary>
    /// A removed model must stop being credited. The refresh replaces the map wholesale rather than
    /// merging, because a merge keeps a deleted model alive forever.
    /// </summary>
    [Fact]
    public void SeedingReplaces_SoARemovedModelStopsBeingCredited()
    {
        OllamaCapabilityCache.Seed(Host, new Dictionary<string, ModelCapabilities>
        {
            ["temporary:latest"] = ModelCapabilities.Standard,
        });
        Assert.True(OllamaCapabilityCache.For(Host, "temporary:latest").ToolCalling);

        OllamaCapabilityCache.Seed(Host, new Dictionary<string, ModelCapabilities>
        {
            ["other:latest"] = ModelCapabilities.Standard,
        });
        Assert.False(OllamaCapabilityCache.For(Host, "temporary:latest").ToolCalling);
    }

    [Fact]
    public void TheSnapshot_ReportsWhatTheCallPathWillSee()
    {
        OllamaCapabilityCache.Seed(Host, new Dictionary<string, ModelCapabilities>
        {
            ["a:latest"] = ModelCapabilities.FromOllama(new[] { "completion", "tools" }),
            ["b:latest"] = ModelCapabilities.FromOllama(new[] { "completion" }),
        });

        var snapshot = OllamaCapabilityCache.Snapshot();
        Assert.Equal(2, snapshot.Count);
        // the report and the call path must agree — disagreeing is the bug this cache exists to fix
        Assert.Equal(snapshot["a:latest"].ToolCalling, OllamaCapabilityCache.For(Host, "a:latest").ToolCalling);
        Assert.Equal(snapshot["b:latest"].ToolCalling, OllamaCapabilityCache.For(Host, "b:latest").ToolCalling);
    }

    // ---- context windows -----------------------------------------------------------------------

    /// <summary>
    /// The key is architecture-prefixed, so it is found by SUFFIX rather than against a table of
    /// architecture names. A table would need editing every time a new one ships, and until someone
    /// noticed, every model of that architecture would silently report "unknown".
    /// </summary>
    [Theory]
    [InlineData("llama.context_length", 131072)]
    [InlineData("gemma3.context_length", 8192)]
    [InlineData("qwen3moe.context_length", 262144)]
    [InlineData("some.future.arch.context_length", 4096)]
    public void TheContextWindow_IsFoundWhateverTheArchitecture(string key, int tokens)
    {
        // Concatenated rather than interpolated: the JSON ends in '}}', which a $$"""...""" literal
        // reads as a closing interpolation brace.
        var json = "{\"model_info\":{\"general.architecture\":\"x\",\""
                 + key + "\":" + tokens + ",\"other\":1}}";
        Assert.Equal(tokens, OllamaCapabilityCache.ReadContextWindow(json));
    }

    /// <summary>
    /// Unknown, never guessed — and that is the safe direction, because AntModelFitness deliberately
    /// does not treat an unknown window as too small. A failed probe costs a check that does not
    /// fire, rather than a false warning an operator learns to ignore.
    /// </summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"model_info":{"general.architecture":"llama"}}""")]
    [InlineData("""{"model_info":{"llama.context_length":0}}""")]
    public void AnUnreadableOrAbsentContextWindow_IsNull(string json) =>
        Assert.Null(OllamaCapabilityCache.ReadContextWindow(json));

    /// <summary>
    /// The regression this closes — found in the browser, not by a test. ContextWindowTokens was
    /// declared on ModelCapabilities and assigned NOWHERE, so the archivist's 32k contract
    /// requirement reported FIT against every model regardless of its window. A field the code can
    /// carry but never populates makes every requirement built on it decorative.
    /// </summary>
    [Fact]
    public void ADiscoveredContextWindow_ReachesTheCallPath()
    {
        OllamaCapabilityCache.Seed(Host, new Dictionary<string, ModelCapabilities>
        {
            ["big:latest"] = ModelCapabilities.Standard with { ContextWindowTokens = 131_072 },
        });

        Assert.Equal(131_072, OllamaCapabilityCache.For(Host, "big:latest").ContextWindowTokens);
    }

    [Fact]
    public void Invalidate_ClearsEverything()
    {
        OllamaCapabilityCache.Seed(Host, new Dictionary<string, ModelCapabilities>
        {
            ["a:latest"] = ModelCapabilities.Standard,
        });
        OllamaCapabilityCache.Invalidate();
        Assert.Empty(OllamaCapabilityCache.Snapshot());
    }
}
