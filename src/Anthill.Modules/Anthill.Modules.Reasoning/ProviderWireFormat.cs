using System.Text.Json;
using System.Text.Json.Nodes;
using Anthill.SDK.Reasoning;

namespace Anthill.Modules.Reasoning;

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

    /// <summary>
    /// v0.3.8.44 — read ONE OpenAI-compatible stream chunk (the JSON after an SSE <c>data:</c>
    /// prefix). Returns the content delta if the chunk carries one, and whether the stream is
    /// finished. Never throws: a malformed chunk is skipped, because one bad line in a stream of
    /// hundreds must not abort an answer that is otherwise arriving.
    ///
    /// The terminal <c>[DONE]</c> sentinel is handled by the CALLER before parsing — it is not
    /// JSON and pretending otherwise here would turn the protocol's end marker into a parse error
    /// swallowed on every single stream.
    /// </summary>
    public static (string? Delta, bool Finished, string? FinishReason) ReadOpenAiStreamChunk(string chunkJson)
    {
        try
        {
            var choice = JsonNode.Parse(chunkJson)?.AsObject()?["choices"]?.AsArray()
                ?.FirstOrDefault()?.AsObject();
            var delta = choice?["delta"]?.AsObject()?["content"]?.GetValue<string>();
            var finish = choice?["finish_reason"]?.GetValue<string>();
            return (string.IsNullOrEmpty(delta) ? null : delta, finish is { Length: > 0 }, finish);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return (null, false, null);
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
        else if (request.ResponseSchemaJson is { Length: > 0 } schema)
        {
            /*
             * STRUCTURED OUTPUT, in the only form Anthropic serves it. v0.3.8.77 (PLAN.md §2 R1).
             *
             * THE DEFECT THIS FIXES. `ModelCapabilityCatalog` declares anthropic as `Standard`,
             * which includes `StructuredOutput = true`. So `Negotiate` KEPT the schema — correctly,
             * by its own lights — and this method never read the field. The schema was dropped on
             * the floor, silently, while the capability report told the operator structured output
             * was supported.
             *
             * It was latent until v0.3.8.76, because until then no producer set `ResponseSchemaJson`
             * at all. Wiring the coder, planner and strategist made a three-year-old declaration
             * reachable for the first time, and the first thing it reached was an adapter that
             * ignores it. That is the same defect the previous release was about, one layer down,
             * and it is exactly what a conformance suite is for.
             *
             * WHY A TOOL AND NOT A `response_format`. Anthropic has no OpenAI-style
             * `response_format: json_schema`. Its documented way to bind a reply to a shape is a
             * tool the model is FORCED to call: the schema becomes the tool's `input_schema`, and
             * `tool_choice` names it, so the reply must be an instance of the schema.
             * `ReadAnthropic` unwraps that call's input back into `Content`, so a caller sees the
             * same JSON text it would get from OpenAI and no call site learns a provider name.
             *
             * AND WHY `else`. Schema-plus-tools is not representable here: forcing `tool_choice` at
             * the synthetic tool would make the model's real tools unreachable, and offering both
             * without forcing would leave the shape unbound again. The colony never sends both —
             * `GenerateTyped` carries a schema and no tools, `ToolCallingLoop` carries tools and no
             * schema — and `AdapterConformanceTests` pins that it stays unreachable rather than
             * leaving it to be discovered by whoever writes the first request that does both.
             */
            body["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = StructuredOutputToolName,
                    ["description"] = "Return the answer as an instance of this schema.",
                    ["input_schema"] = JsonNode.Parse(schema),
                },
            };
            body["tool_choice"] = new JsonObject
            {
                ["type"] = "tool", ["name"] = StructuredOutputToolName,
            };
        }

        return body;
    }

    /// <summary>
    /// The synthetic tool that carries a response schema to Anthropic.
    ///
    /// ONE definition, read by both the writer and the reader. Two spellings of this name would not
    /// fail loudly — the body would force a tool the reader does not recognise, and the reply would
    /// come back as a tool call nobody unwraps, which reads downstream as an empty response.
    /// </summary>
    public const string StructuredOutputToolName = "respond_with_schema";

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

            /*
             * Unwrap the structured-output tool back into content. v0.3.8.77.
             *
             * `AnthropicBody` sends a response schema as a forced tool call, because that is the
             * only JSON mode Anthropic has. Without this, the reply comes back as a ToolCall and
             * `Content` is empty — and an empty content string is read downstream as "the model
             * said nothing", which is the failure this whole path exists to prevent. The schema
             * would have been honoured perfectly and the answer thrown away.
             *
             * The synthetic call is REMOVED from `ToolCalls` rather than left beside the content: a
             * caller that asked for a schema did not ask for a tool call, and leaving it would make
             * `ToolCalls.Count > 0` true for every structured request, which is what a tool-calling
             * loop branches on.
             */
            var structured = calls.FirstOrDefault(c => c.Name == StructuredOutputToolName);
            if (structured is not null && content.Length == 0)
            {
                content = structured.ArgumentsJson;
                calls.Remove(structured);
            }

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
