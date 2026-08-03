using Anthill.Core.Models;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.3.0 (ADR-003) — capability awareness, fail-closed.
///
/// Nothing in the codebase could express a model capability before this, so the orchestration layer
/// had to assume every backend behaved identically. That assumption is invisible until it is wrong:
/// offering tools to a model that ignores them yields a confident answer which silently skipped the
/// tool call — indistinguishable from a bad answer.
/// </summary>
public class ModelCapabilityTests
{
    [Fact]
    public void AnUnknownProviderAndModel_GetsTheLeastCapableProfile()
    {
        var caps = ModelCapabilityCatalog.For("some-new-vendor", "mystery-model");

        Assert.False(caps.ToolCalling);
        Assert.False(caps.StructuredOutput);
        Assert.False(caps.Vision);
        Assert.Null(caps.ContextWindowTokens);   // unknown is not "small"
    }

    /// <summary>
    /// Ollama's provider default must NOT claim tools: support depends on the model pulled, not on
    /// Ollama. Claiming otherwise offers tools to whatever the operator happens to have locally.
    /// </summary>
    [Fact]
    public void OllamaItself_DoesNotClaimToolCalling()
    {
        Assert.False(ModelCapabilityCatalog.For("ollama", "some-random-local-model").ToolCalling);
    }

    /// <summary>
    /// But a tool-capable model served BY Ollama is tool-capable. The model is the authority; the
    /// company serving it is not.
    /// </summary>
    [Theory]
    [InlineData("llama3.1:8b")]
    [InlineData("qwen2.5-coder:7b-instruct-q4_K_M")]
    public void AToolCapableModelOnOllama_IsToolCapable(string model)
    {
        Assert.True(ModelCapabilityCatalog.For("ollama", model).ToolCalling);
    }

    /// <summary>Model ids carry tags and vendor prefixes, so matching is on substring.</summary>
    [Fact]
    public void ModelMatching_ToleratesTagsAndPrefixes()
    {
        Assert.True(ModelCapabilityCatalog.For("openrouter", "meta-llama/llama3.2-90b-vision").Vision);
    }

    [Fact]
    public void UnknownCapabilityNames_AreNotSilentlyGranted()
    {
        Assert.False(ModelCapabilities.Standard.Supports("telepathy"));
    }

    // ---- discovered capabilities: Ollama reports the truth, the name table only guesses ---------

    /// <summary>
    /// Three models actually installed on the operator's machine, with the capability arrays Ollama
    /// itself returns. The declared fragment table was wrong on two of them — it called gemma4
    /// text-only when Ollama reports tools AND thinking, and granted qwen3-coder a reasoning
    /// capability Ollama does not claim. Guessing from a model NAME is guessing; the runtime
    /// holding the weights knows.
    /// </summary>
    [Fact]
    public void OllamaReportedCapabilities_BeatTheNameTable()
    {
        var gemma = ModelCapabilities.FromOllama(new[] { "completion", "tools", "thinking" });
        Assert.True(gemma.ToolCalling);
        Assert.True(gemma.Reasoning);
        Assert.False(ModelCapabilityCatalog.For("ollama", "gemma4:31b").ToolCalling,
            "the name table cannot know this — which is exactly why discovery wins");

        var coder = ModelCapabilities.FromOllama(new[] { "completion", "tools" });
        Assert.True(coder.ToolCalling);
        Assert.False(coder.Reasoning);      // NOT claimed by Ollama, so not claimed here

        var plain = ModelCapabilities.FromOllama(new[] { "completion" });
        Assert.False(plain.ToolCalling);
        Assert.False(plain.Reasoning);
    }

    /// <summary>Streaming is a property of the server, and Ollama does not list it per model.</summary>
    [Fact]
    public void OllamaAlwaysStreams_EvenForACompletionOnlyModel()
    {
        Assert.True(ModelCapabilities.FromOllama(new[] { "completion" }).Streaming);
    }

    /// <summary>
    /// Fail closed on the way in too: a capability word a future Ollama adds must not silently
    /// enable a path we have never tested.
    /// </summary>
    [Fact]
    public void AnUnrecognisedReportedCapability_GrantsNothing()
    {
        var caps = ModelCapabilities.FromOllama(new[] { "completion", "telekinesis" });
        Assert.False(caps.ToolCalling);
        Assert.False(caps.Vision);
        Assert.False(caps.Embeddings);
    }

    [Fact]
    public void NoReportedCapabilities_IsTextOnly_NotEverything()
    {
        var caps = ModelCapabilities.FromOllama(null);
        Assert.False(caps.ToolCalling);
        Assert.False(caps.StructuredOutput);
    }

    // ---- endpoint normalization ----------------------------------------------------------------

    /// <summary>
    /// `ollama_host` has always meant the bare host, because the native API lived at /api/*. An
    /// operator who knows the OpenAI-compatible path will reasonably paste ".../v1" — the form every
    /// OpenAI client calls a base URL — and appending blindly would post to /v1/v1/chat/completions
    /// and 404.
    /// </summary>
    [Theory]
    [InlineData("http://10.10.10.57:11434")]
    [InlineData("http://10.10.10.57:11434/")]
    [InlineData("http://10.10.10.57:11434/v1")]
    [InlineData("http://10.10.10.57:11434/v1/")]
    [InlineData("http://10.10.10.57:11434/v1/chat/completions")]
    public void EveryFormAnOperatorMightType_ResolvesToOneEndpoint(string configured)
    {
        Assert.Equal("http://10.10.10.57:11434/v1/chat/completions",
            OllamaClient.ChatEndpoint(configured));
    }

    // ---- negotiation: ask for anything, receive only what can be served ------------------------

    private static ModelRequest FullyLoaded() => ModelRequest.FromPrompt("do the thing") with
    {
        Tools = new[] { new ModelToolSpec("read_file", "reads a file", "{}") },
        ResponseSchemaJson = "{\"type\":\"object\"}",
        Stream = true,
    };

    /// <summary>
    /// The point of negotiating at the seam: a caller may always ask for what it wants, and exactly
    /// one place decides what survives. Otherwise every call site grows its own "does this provider
    /// do tools?" branch, which is how provider names leak into orchestration logic.
    /// </summary>
    [Fact]
    public void AgainstATextOnlyModel_ToolsSchemaAndStreamingAreDropped()
    {
        var negotiated = ModelCapabilityCatalog.Negotiate(FullyLoaded(), ModelCapabilities.TextOnly);

        Assert.Empty(negotiated.Tools);
        Assert.Null(negotiated.ResponseSchemaJson);
        Assert.False(negotiated.Stream);
        Assert.Equal("do the thing", negotiated.Messages[0].Content);   // the request itself survives
    }

    [Fact]
    public void AgainstACapableModel_NothingIsDropped()
    {
        var negotiated = ModelCapabilityCatalog.Negotiate(FullyLoaded(), ModelCapabilities.Standard);

        Assert.Single(negotiated.Tools);
        Assert.NotNull(negotiated.ResponseSchemaJson);
        Assert.True(negotiated.Stream);
    }

    /// <summary>Not asking for streaming must never turn it on.</summary>
    [Fact]
    public void NegotiationOnlyEverRemoves()
    {
        var plain = ModelRequest.FromPrompt("hello");
        var negotiated = ModelCapabilityCatalog.Negotiate(plain, ModelCapabilities.Standard);

        Assert.False(negotiated.Stream);
        Assert.Empty(negotiated.Tools);
    }

    // ---- usage accounting ----------------------------------------------------------------------

    /// <summary>
    /// Unknown usage must not read as zero. A provider that reports nothing would otherwise
    /// contribute "0 tokens" to any total built from it, understating cost while looking precise.
    /// </summary>
    [Fact]
    public void UnknownUsage_IsNull_NotZero()
    {
        Assert.Null(ModelUsage.Unknown.TotalTokens);
        Assert.Equal(30, new ModelUsage(10, 20).TotalTokens);
        Assert.Equal(10, new ModelUsage(10, null).TotalTokens);   // partial knowledge is still knowledge
    }

    /// <summary>
    /// The typed response widens the existing result rather than inventing a second vocabulary for
    /// success — the colony already branches on ModelCallOutcome and must keep doing so.
    /// </summary>
    [Fact]
    public void TheTypedResponse_RoundTripsTheExistingResultType()
    {
        var original = new ModelCallResult(ModelCallOutcome.Ok, "hello");
        var widened = ModelResponse.FromCallResult(original, provider: "ollama", model: "llama3.1");

        Assert.True(widened.Ok);
        Assert.Equal("ollama", widened.Provider);

        var back = widened.ToCallResult();
        Assert.Equal(original.Status, back.Status);
        Assert.Equal(original.Content, back.Content);
    }
}
