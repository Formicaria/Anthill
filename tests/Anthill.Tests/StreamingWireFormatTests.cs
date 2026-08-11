using Anthill.SDK.Reasoning;
using Anthill.Modules.Reasoning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.44 — the streaming seam, tested where it is pure. The chunk reader is the piece every
/// OpenAI-compatible streaming provider shares, and the piece a malformed line would otherwise
/// break mid-answer; the SDK contract is pinned so streaming stays a CAPABILITY a caller asks
/// about, never a wrapper that fakes a trickle over a blocking call.
/// </summary>
public class StreamingWireFormatTests
{
    [Fact]
    public void AContentDelta_IsExtracted()
    {
        var (delta, finished, _) = ProviderWireFormat.ReadOpenAiStreamChunk(
            """{"choices":[{"delta":{"content":"Hel"},"finish_reason":null}]}""");
        Assert.Equal("Hel", delta);
        Assert.False(finished);
    }

    [Fact]
    public void TheFinishChunk_EndsTheStream_WithItsReason()
    {
        var (delta, finished, reason) = ProviderWireFormat.ReadOpenAiStreamChunk(
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");
        Assert.Null(delta);
        Assert.True(finished);
        Assert.Equal("stop", reason);
    }

    [Fact]
    public void AMalformedChunk_IsSkipped_NotFatal()
    {
        // One bad line in a stream of hundreds must not abort an answer that is arriving.
        var (delta, finished, _) = ProviderWireFormat.ReadOpenAiStreamChunk("{not json");
        Assert.Null(delta);
        Assert.False(finished);
    }

    [Fact]
    public void AnEmptyDelta_IsNotAnnounced()
    {
        // Keep-alive chunks with empty content produce no onDelta call downstream.
        var (delta, _, _) = ProviderWireFormat.ReadOpenAiStreamChunk(
            """{"choices":[{"delta":{"content":""},"finish_reason":null}]}""");
        Assert.Null(delta);
    }

    /// <summary>Streaming is additive: the interface extends the one contract every provider
    /// meets, and Ollama implements it — the local-first provider streams first.</summary>
    [Fact]
    public void TheContract_IsAdditive_AndOllamaMeetsIt()
    {
        Assert.True(typeof(IReasoningProvider).IsAssignableFrom(typeof(IStreamingReasoningProvider)));
        Assert.True(typeof(IStreamingReasoningProvider).IsAssignableFrom(typeof(OllamaClient)));
    }

    /// <summary>
    /// The streamed and unstreamed calls must be the SAME conversation on the wire, one flag
    /// apart — a second body builder is how the two paths drift into answering differently.
    /// SendStreaming builds through the same OpenAiBody and flips stream:true; this pins the
    /// source arrangement, since the network call itself needs a live provider.
    /// </summary>
    [Fact]
    public void SendStreaming_SharesTheBodyBuilder_AndFallsBackForTools()
    {
        var src = File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Modules", "Anthill.Modules.Reasoning", "OllamaProvider.cs"));
        var at = src.IndexOf("public ModelResponse SendStreaming", StringComparison.Ordinal);
        Assert.True(at >= 0, "SendStreaming not found");
        var body = src[at..];
        Assert.Contains("ProviderWireFormat.OpenAiBody(negotiated, model)", body);
        Assert.Contains("body[\"stream\"] = true", body);
        // Tools fall back to the tested non-streaming path rather than claiming an untested one.
        Assert.Contains("return Send(request, retries);", body);
        // No silent restart over text the operator has already read.
        Assert.DoesNotContain("for (var attempt", body[..body.IndexOf("private static ModelResponse Fail", StringComparison.Ordinal)]);
    }

    /// <summary>
    /// v0.3.8.47, found live after THREE releases of streaming work: the router hands out
    /// ReasoningProviderAdapter, and the adapter did not carry IStreamingReasoningProvider — so
    /// the Queen's capability test was false for every provider and not one delta ever flowed.
    /// The wrapper must forward the capability, and this pin is what keeps the next wrapper from
    /// silently hiding it again.
    /// </summary>
    [Fact]
    public void TheRoutersAdapter_ForwardsTheStreamingCapability()
    {
        var src = File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Models", "ReasoningProviders.cs"));

        Assert.Contains("class ReasoningProviderAdapter : IModelClient, IStreamingReasoningProvider", src);
        Assert.Contains("_inner is IStreamingReasoningProvider streaming", src);
        Assert.Contains("streaming.SendStreaming(request, onDelta, retries)", src);
        // The fallback is the whole answer through Send — never a fake trickle.
        Assert.Contains(": _inner.Send(request, retries);", src);
    }

    // ---- v0.3.8.47: the agent NDJSON stream (Claude Code's stream-json) --------------------------

    /// <summary>An assistant event's text blocks become the delta; nothing else leaks through.</summary>
    [Fact]
    public void AgentStreamEvent_AssistantText_BecomesTheDelta()
    {
        var (text, result) = Anthill.Modules.Reasoning.AgentCliProvider.ParseStreamEvent(
            """{"type":"assistant","message":{"content":[{"type":"text","text":"the colony "},{"type":"text","text":"hums"}]}}""");

        Assert.Equal("the colony hums", text);
        Assert.Null(result);
    }

    /// <summary>The result event carries the final answer — transport JSON never reaches the record.</summary>
    [Fact]
    public void AgentStreamEvent_Result_IsTheFinalAnswer()
    {
        var (text, result) = Anthill.Modules.Reasoning.AgentCliProvider.ParseStreamEvent(
            """{"type":"result","subtype":"success","result":"done and verified"}""");

        Assert.Null(text);
        Assert.Equal("done and verified", result);
    }

    /// <summary>--include-partial-messages: the wrapped content_block_delta is a token delta.</summary>
    [Fact]
    public void AgentStreamEvent_TokenDelta_IsText()
    {
        var (text, result) = Anthill.Modules.Reasoning.AgentCliProvider.ParseStreamEvent(
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"hum"}}}""");

        Assert.Equal("hum", text);
        Assert.Null(result);
    }

    /// <summary>Verbose noise, tool events, and non-JSON lines are silence, never junk deltas.</summary>
    [Theory]
    [InlineData("""{"type":"system","subtype":"init"}""")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"bash"}]}}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void AgentStreamEvent_NoiseIsSilence(string line)
    {
        var (text, result) = Anthill.Modules.Reasoning.AgentCliProvider.ParseStreamEvent(line);

        Assert.Null(text);
        Assert.Null(result);
    }
}
