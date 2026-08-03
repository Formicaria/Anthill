using System.Text.Json;
using System.Text.Json.Nodes;

namespace Anthill.Core.Models;

/// <summary>
/// v3.3.0 (ADR-006) — <see cref="ModelRequest"/> projected onto each provider's wire format, and
/// their replies read back into <see cref="ModelResponse"/>.
///
/// PURE FUNCTIONS, deliberately. The clients own HTTP — retries, timeouts, the circuit breaker —
/// and this file owns shape. Separating them is what makes the wire contract testable without a
/// provider: every mistake below is a silent one (a tools array the provider ignores because it
/// was nested wrongly, usage read from a field that does not exist) and silent mistakes are the
/// ones that need tests rather than integration luck.
///
/// The clients migrate onto these next; nothing calls them yet.
/// </summary>
public static class ProviderWireFormat
{
    // ---- OpenAI-compatible (also LM Studio, vLLM, llama.cpp server, OpenRouter) ----------------

    /// <summary>
    /// Body for POST /v1/chat/completions. This one shape serves every OpenAI-compatible backend,
    /// which is why "add LM Studio / vLLM / llama.cpp" is a catalog entry and a base URL rather
    /// than a new client.
    /// </summary>
    public static JsonObject OpenAiBody(ModelRequest request, string model)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            var msg = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
            if (m.Role == ModelMessage.Tool && m.ToolCallId is not null)
                msg["tool_call_id"] = m.ToolCallId;

            // An assistant turn REPLAYS the tool calls it made. Without this the conversation sent
            // back on the next turn contains tool results answering requests that are not in it, and
            // a model reading that has no evidence it already called the tool — so it calls again.
            // Measured against a live model: three identical calls, all answered, no answer produced.
            if (m.Role == ModelMessage.Assistant && m.ToolCalls.Count > 0)
            {
                var calls = new JsonArray();
                foreach (var call in m.ToolCalls)
                    calls.Add(new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = call.ArgumentsJson,
                        },
                    });
                msg["tool_calls"] = calls;
            }
            messages.Add(msg);
        }

        var body = new JsonObject { ["model"] = model, ["messages"] = messages };
        if (request.Temperature is { } t) body["temperature"] = t;
        if (request.MaxOutputTokens is { } max) body["max_tokens"] = max;
        if (request.Stream) body["stream"] = true;

        // Omitted entirely when empty. An empty `tools: []` is not the same as no tools to several
        // backends — some reject it, some switch to a tool-forcing mode — so absence must be
        // expressed by absence.
        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(string.IsNullOrWhiteSpace(tool.ParametersJson)
                            ? "{\"type\":\"object\",\"properties\":{}}" : tool.ParametersJson),
                    },
                });
            body["tools"] = tools;
        }

        if (request.ResponseSchemaJson is { Length: > 0 } schema)
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "response", ["schema"] = JsonNode.Parse(schema) },
            };

        return body;
    }

    /// <summary>Read an OpenAI-compatible reply. Never throws: a malformed body is a status, not a crash.</summary>
    public static ModelResponse ReadOpenAi(string json, string provider, string requestedModel)
    {
        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            var choice = root?["choices"]?.AsArray()?.FirstOrDefault()?.AsObject();
            var message = choice?["message"]?.AsObject();
            var content = message?["content"]?.GetValue<string>() ?? "";

            var calls = new List<ModelToolCall>();
            foreach (var c in message?["tool_calls"]?.AsArray() ?? new JsonArray())
            {
                var fn = c?["function"]?.AsObject();
                if (fn is null) continue;
                calls.Add(new ModelToolCall(
                    c?["id"]?.GetValue<string>() ?? "",
                    fn["name"]?.GetValue<string>() ?? "",
                    fn["arguments"]?.GetValue<string>() ?? "{}"));
            }

            var usage = root?["usage"]?.AsObject();
            return new ModelResponse
            {
                // A reply carrying tool calls and no prose is a SUCCESS, not an empty response.
                // Testing content alone would classify the tool-calling path as a failure — the
                // most important new path treated as the oldest kind of error.
                Status = content.Length == 0 && calls.Count == 0 ? ModelCallOutcome.Empty : ModelCallOutcome.Ok,
                Content = content,
                ToolCalls = calls,
                Usage = new ModelUsage(
                    (int?)usage?["prompt_tokens"]?.GetValue<int>(),
                    (int?)usage?["completion_tokens"]?.GetValue<int>()),
                Model = root?["model"]?.GetValue<string>() ?? requestedModel,
                Provider = provider,
                FinishReason = choice?["finish_reason"]?.GetValue<string>(),
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return new ModelResponse
            {
                Status = ModelCallOutcome.Error,
                Content = $"Could not read the provider reply: {error.Message}",
                Provider = provider,
                Model = requestedModel,
            };
        }
    }

    // ---- Anthropic messages --------------------------------------------------------------------

    /// <summary>
    /// Body for POST /v1/messages. Anthropic differs in two ways that matter: the system prompt is
    /// a TOP-LEVEL field rather than a message, and max_tokens is required rather than optional.
    /// Both are the kind of difference that belongs at this seam and nowhere else.
    /// </summary>
    public static JsonObject AnthropicBody(ModelRequest request, string model, int defaultMaxTokens = 4096)
    {
        var system = string.Join("\n\n", request.Messages
            .Where(m => m.Role == ModelMessage.System).Select(m => m.Content));

        var messages = new JsonArray();
        foreach (var m in request.Messages.Where(m => m.Role != ModelMessage.System))
            messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxOutputTokens ?? defaultMaxTokens,
        };
        if (system.Length > 0) body["system"] = system;
        if (request.Temperature is { } t) body["temperature"] = t;
        if (request.Stream) body["stream"] = true;

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
                tools.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = JsonNode.Parse(string.IsNullOrWhiteSpace(tool.ParametersJson)
                        ? "{\"type\":\"object\",\"properties\":{}}" : tool.ParametersJson),
                });
            body["tools"] = tools;
        }

        return body;
    }

    /// <summary>Read an Anthropic reply: content is a list of typed blocks, not a string.</summary>
    public static ModelResponse ReadAnthropic(string json, string requestedModel)
    {
        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            var text = new System.Text.StringBuilder();
            var calls = new List<ModelToolCall>();

            foreach (var block in root?["content"]?.AsArray() ?? new JsonArray())
            {
                var kind = block?["type"]?.GetValue<string>();
                if (kind == "text") text.Append(block?["text"]?.GetValue<string>() ?? "");
                else if (kind == "tool_use")
                    calls.Add(new ModelToolCall(
                        block?["id"]?.GetValue<string>() ?? "",
                        block?["name"]?.GetValue<string>() ?? "",
                        block?["input"]?.ToJsonString() ?? "{}"));
            }

            var usage = root?["usage"]?.AsObject();
            var content = text.ToString();
            return new ModelResponse
            {
                Status = content.Length == 0 && calls.Count == 0 ? ModelCallOutcome.Empty : ModelCallOutcome.Ok,
                Content = content,
                ToolCalls = calls,
                Usage = new ModelUsage(
                    (int?)usage?["input_tokens"]?.GetValue<int>(),
                    (int?)usage?["output_tokens"]?.GetValue<int>()),
                Model = root?["model"]?.GetValue<string>() ?? requestedModel,
                Provider = "anthropic",
                FinishReason = root?["stop_reason"]?.GetValue<string>(),
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return new ModelResponse
            {
                Status = ModelCallOutcome.Error,
                Content = $"Could not read the provider reply: {error.Message}",
                Provider = "anthropic",
                Model = requestedModel,
            };
        }
    }
}
