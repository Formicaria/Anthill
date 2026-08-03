using Anthill.Core.Models;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.3.0 (ADR-006) — the wire contract, tested without a provider.
///
/// Every mistake this file can make is a SILENT one: a tools array nested wrongly is ignored by the
/// provider and the model simply answers without calling anything; usage read from a field that
/// does not exist reports zero cost forever. Neither throws, neither fails a health check, and both
/// look exactly like a model choosing not to use a tool. That is why the projection is pure and
/// tested here rather than discovered against a live endpoint.
/// </summary>
public class ProviderWireFormatTests
{
    private static ModelRequest WithTools() => ModelRequest.FromPrompt("list the repo") with
    {
        Tools = new[] { new ModelToolSpec("list_directory", "lists a directory",
            "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}") },
    };

    [Fact]
    public void OpenAi_ToolsAreNestedUnderFunction_WithTheSchemaIntact()
    {
        var body = ProviderWireFormat.OpenAiBody(WithTools(), "gpt-4o");
        var fn = body["tools"]!.AsArray()[0]!["function"]!;

        Assert.Equal("function", body["tools"]!.AsArray()[0]!["type"]!.GetValue<string>());
        Assert.Equal("list_directory", fn["name"]!.GetValue<string>());
        // the schema must arrive as an OBJECT, not as a string containing JSON
        Assert.Equal("string", fn["parameters"]!["properties"]!["path"]!["type"]!.GetValue<string>());
    }

    /// <summary>
    /// Absence must be expressed by absence. Several backends treat `tools: []` differently from no
    /// tools at all — some reject it, some switch to a tool-forcing mode.
    /// </summary>
    [Fact]
    public void OpenAi_NoTools_OmitsTheKeyEntirely()
    {
        var body = ProviderWireFormat.OpenAiBody(ModelRequest.FromPrompt("hello"), "gpt-4o");
        Assert.False(body.ContainsKey("tools"));
        Assert.False(body.ContainsKey("stream"));          // and streaming is opt-in
        Assert.False(body.ContainsKey("response_format"));
    }

    /// <summary>
    /// An assistant turn must replay the tool calls it made.
    ///
    /// The protocol pairs each `tool` message with the assistant message that requested it, by id.
    /// Sending the assistant turn as empty content with its tool_calls dropped produces a
    /// conversation where results answer requests that are not in it — and a model reading that
    /// back has no evidence it already called the tool, so it calls again.
    ///
    /// Measured against a live local model before this was fixed: system_info called three times
    /// with identical arguments, answered correctly every time, and the run ended on the repeat
    /// guard having produced no answer. The failure presents as a loop, never as an error, which is
    /// why it needs a test rather than an eyeball.
    /// </summary>
    [Fact]
    public void AnAssistantTurn_ReplaysItsToolCalls_SoResultsHaveARequest()
    {
        var request = new ModelRequest
        {
            Messages = new[]
            {
                new ModelMessage(ModelMessage.User, "what OS is this?"),
                new ModelMessage(ModelMessage.Assistant, "")
                {
                    ToolCalls = new[] { new ModelToolCall("call_1", "system_info", "{}") },
                },
                new ModelMessage(ModelMessage.Tool, """{"os":"Pop!_OS"}""") { ToolCallId = "call_1" },
            },
        };

        var body = ProviderWireFormat.OpenAiBody(request, "gemma4:31b");
        var assistant = body["messages"]!.AsArray()[1]!;
        var call = assistant["tool_calls"]!.AsArray()[0]!;

        Assert.Equal("call_1", call["id"]!.GetValue<string>());
        Assert.Equal("function", call["type"]!.GetValue<string>());
        Assert.Equal("system_info", call["function"]!["name"]!.GetValue<string>());

        // and the ids must MATCH, or the pairing the protocol relies on is broken
        Assert.Equal(call["id"]!.GetValue<string>(),
            body["messages"]!.AsArray()[2]!["tool_call_id"]!.GetValue<string>());
    }

    /// <summary>An assistant turn with no tool calls must not carry an empty array.</summary>
    [Fact]
    public void AnOrdinaryAssistantTurn_HasNoToolCallsKey()
    {
        var request = new ModelRequest
        {
            Messages = new[] { new ModelMessage(ModelMessage.Assistant, "just talking") },
        };
        var assistant = ProviderWireFormat.OpenAiBody(request, "m")["messages"]!.AsArray()[0]!.AsObject();
        Assert.False(assistant.ContainsKey("tool_calls"));
    }

    /// <summary>
    /// Anthropic takes the system prompt as a top-level field, NOT as a message. Sent as a message
    /// it is either rejected or silently treated as user text, which quietly changes the model's
    /// instructions.
    /// </summary>
    [Fact]
    public void Anthropic_SystemPromptIsLiftedOutOfTheMessages()
    {
        var request = new ModelRequest
        {
            Messages = new[]
            {
                new ModelMessage(ModelMessage.System, "you are terse"),
                new ModelMessage(ModelMessage.User, "hello"),
            },
        };

        var body = ProviderWireFormat.AnthropicBody(request, "claude-sonnet-4");
        Assert.Equal("you are terse", body["system"]!.GetValue<string>());
        Assert.Single(body["messages"]!.AsArray());
        Assert.Equal("user", body["messages"]!.AsArray()[0]!["role"]!.GetValue<string>());
    }

    /// <summary>max_tokens is required by Anthropic, so it always has a value.</summary>
    [Fact]
    public void Anthropic_AlwaysSendsMaxTokens()
    {
        var body = ProviderWireFormat.AnthropicBody(ModelRequest.FromPrompt("hi"), "claude-sonnet-4");
        Assert.True(body["max_tokens"]!.GetValue<int>() > 0);
    }

    [Fact]
    public void Anthropic_ToolsUseInputSchema_NotParameters()
    {
        var body = ProviderWireFormat.AnthropicBody(WithTools(), "claude-sonnet-4");
        var tool = body["tools"]!.AsArray()[0]!;
        Assert.NotNull(tool["input_schema"]);
        Assert.Null(tool["parameters"]);
    }

    // ---- reading replies -----------------------------------------------------------------------

    [Fact]
    public void OpenAi_ReadsContentUsageAndModel()
    {
        var response = ProviderWireFormat.ReadOpenAi(
            """
            {"model":"gpt-4o-2024","choices":[{"message":{"content":"hello"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":11,"completion_tokens":7}}
            """, "openai", "gpt-4o");

        Assert.True(response.Ok);
        Assert.Equal("hello", response.Content);
        Assert.Equal(18, response.Usage.TotalTokens);
        Assert.Equal("gpt-4o-2024", response.Model);       // what SERVED it, not what was asked for
        Assert.Equal("stop", response.FinishReason);
    }

    /// <summary>
    /// The important one: a reply that is only tool calls has no prose, and classifying it as an
    /// empty response would make the entire tool-calling path look like the oldest kind of failure.
    /// </summary>
    [Fact]
    public void AReplyOfOnlyToolCalls_IsASuccess_NotAnEmptyResponse()
    {
        var response = ProviderWireFormat.ReadOpenAi(
            """
            {"choices":[{"message":{"content":null,"tool_calls":[
              {"id":"call_1","function":{"name":"list_directory","arguments":"{\"path\":\".\"}"}}]},
              "finish_reason":"tool_calls"}]}
            """, "openai", "gpt-4o");

        Assert.True(response.Ok);
        Assert.Equal("", response.Content);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("list_directory", call.Name);
        Assert.Contains("path", call.ArgumentsJson);
    }

    [Fact]
    public void Anthropic_ReadsTextBlocksAndToolUse()
    {
        var response = ProviderWireFormat.ReadAnthropic(
            """
            {"model":"claude-sonnet-4","stop_reason":"tool_use","content":[
              {"type":"text","text":"checking"},
              {"type":"tool_use","id":"tu_1","name":"list_directory","input":{"path":"."}}],
             "usage":{"input_tokens":5,"output_tokens":9}}
            """, "claude-sonnet-4");

        Assert.True(response.Ok);
        Assert.Equal("checking", response.Content);
        Assert.Equal("list_directory", Assert.Single(response.ToolCalls).Name);
        Assert.Equal(14, response.Usage.TotalTokens);
    }

    /// <summary>A provider that reports no usage is UNKNOWN, never zero.</summary>
    [Fact]
    public void MissingUsage_ReadsAsUnknown_NotZero()
    {
        var response = ProviderWireFormat.ReadOpenAi(
            """{"choices":[{"message":{"content":"hi"}}]}""", "openai", "gpt-4o");

        Assert.Null(response.Usage.TotalTokens);
    }

    /// <summary>A malformed body is a status, not an exception thrown across the provider boundary.</summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"choices\":\"unexpected shape\"}")]
    public void AMalformedReply_IsAStatus_NotACrash(string body)
    {
        var response = ProviderWireFormat.ReadOpenAi(body, "openai", "gpt-4o");
        Assert.False(response.Ok);
        Assert.Equal("openai", response.Provider);
    }
}
