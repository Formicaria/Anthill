namespace Anthill.SDK.Reasoning;

/// <summary>
/// v3.3.0 (ADR-006) — what a provider/model pair can actually do.
///
/// Before this, nothing in the codebase could express a capability, so the orchestration layer had
/// no choice but to assume every backend behaved identically. That assumption is invisible until it
/// is wrong: offering tools to a model that ignores them produces a confident answer that silently
/// skipped the tool call, which reads as a bad answer rather than a missing capability.
///
/// FAIL CLOSED. An unknown model gets <see cref="TextOnly"/> — the intersection every provider
/// supports. The cost of under-claiming is a tool the operator could have used; the cost of
/// over-claiming is a request the provider rejects, or worse, silently ignores.
/// </summary>
public sealed record ModelCapabilities
{
    public bool ToolCalling { get; init; }
    public bool StructuredOutput { get; init; }
    public bool Streaming { get; init; }
    public bool Vision { get; init; }
    public bool Embeddings { get; init; }
    public bool Reasoning { get; init; }

    /// <summary>Null when unknown — which is NOT the same as small, and must not be treated as a limit.</summary>
    public int? ContextWindowTokens { get; init; }

    /// <summary>Everything a text completion endpoint can be relied on to do, and nothing more.</summary>
    public static readonly ModelCapabilities TextOnly = new();

    /// <summary>The common modern baseline: tools, JSON output and streaming.</summary>
    public static readonly ModelCapabilities Standard = new()
    {
        ToolCalling = true, StructuredOutput = true, Streaming = true,
    };

    /// <summary>
    /// Capabilities as OLLAMA ITSELF reports them (`/api/tags` → `capabilities: [...]`).
    ///
    /// v3.3.0: this is the "declared → discovered" upgrade the catalog was shaped for, and the
    /// operator's own machine proved why it matters. Against three real local models the hand-written
    /// fragment table was wrong twice: it called `gemma4:31b` text-only when Ollama reports
    /// `tools` AND `thinking`, and it granted `qwen3-coder` reasoning that Ollama does not claim.
    /// Guessing from a model's NAME is guessing; the runtime holding the weights knows.
    ///
    /// Still fail-closed on the way in: an unrecognised capability word grants nothing, so a future
    /// Ollama release adding a name we do not know cannot silently enable a path.
    /// </summary>
    public static ModelCapabilities FromOllama(IEnumerable<string>? reported)
    {
        // Streaming is a property of the SERVER here, not the model: Ollama streams anything it
        // serves, and it does not list that among per-model capabilities.
        var caps = TextOnly with { Streaming = true };
        if (reported is null) return caps;

        foreach (var raw in reported)
        {
            switch ((raw ?? "").Trim().ToLowerInvariant())
            {
                // "tools" is the only one that gates the agent loop, so it is the one worth being
                // exactly right about. Structured output rides with it: Ollama's OpenAI-compatible
                // endpoint accepts response_format wherever it accepts tools.
                case "tools": caps = caps with { ToolCalling = true, StructuredOutput = true }; break;
                case "thinking": caps = caps with { Reasoning = true }; break;
                case "vision": caps = caps with { Vision = true }; break;
                case "embedding":
                case "embeddings": caps = caps with { Embeddings = true }; break;
                case "completion": break;      // the baseline; already true of everything
                default: break;                // unknown word grants nothing
            }
        }
        return caps;
    }

    public bool Supports(string capability) => capability switch
    {
        "tool_calling" => ToolCalling,
        "structured_output" => StructuredOutput,
        "streaming" => Streaming,
        "vision" => Vision,
        "embeddings" => Embeddings,
        "reasoning" => Reasoning,
        _ => false,     // unknown capability names are not silently granted
    };
}

/// <summary>
/// Resolves capabilities for a provider/model pair.
///
/// Declared rather than probed, for now and deliberately: probing costs a live call per model and
/// cannot run at startup on an air-gapped colony. The table is the seam — a later release can fill
/// it from a provider's own metadata endpoint (OpenRouter and LM Studio both publish one) without
/// any caller changing, which is the property that matters.
/// </summary>
public static class ModelCapabilityCatalog
{
    /// <summary>
    /// Provider-level defaults. Ollama is the interesting case: tool support depends entirely on
    /// the MODEL pulled, not on Ollama itself, so the provider default stays text-only and models
    /// that support tools are named below. Claiming otherwise would offer tools to every local
    /// model an operator happens to have pulled.
    /// </summary>
    private static readonly Dictionary<string, ModelCapabilities> ByProvider = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] = ModelCapabilities.Standard with { Vision = true, Embeddings = true },
        ["anthropic"] = ModelCapabilities.Standard with { Vision = true },
        ["openrouter"] = ModelCapabilities.Standard,
        ["perplexity"] = ModelCapabilities.TextOnly with { Streaming = true },
        ["ollama"] = ModelCapabilities.TextOnly with { Streaming = true },

        // Local OpenAI-compatible servers. Same reasoning as Ollama and for the same reason: the
        // SERVER speaks the tool-calling protocol, but whether tools actually work depends on the
        // model the operator loaded into it. The provider default therefore claims only streaming,
        // and the model fragments below grant the rest.
        ["lmstudio"] = ModelCapabilities.TextOnly with { Streaming = true },
        ["vllm"] = ModelCapabilities.TextOnly with { Streaming = true },
        ["llamacpp"] = ModelCapabilities.TextOnly with { Streaming = true },
    };

    /// <summary>
    /// Model-name fragments that upgrade the provider default, matched case-insensitively on
    /// substring because model ids carry tags and vendor prefixes ("qwen2.5-coder:7b-instruct-q4").
    /// </summary>
    private static readonly (string Fragment, ModelCapabilities Caps)[] ByModelFragment =
    {
        // Hermes is the reference local function-calling family — trained specifically to emit
        // OpenAI-shaped tool calls, which is exactly what the OpenAI-compatible endpoint carries.
        ("hermes", ModelCapabilities.Standard with { Streaming = true }),
        ("nous-hermes", ModelCapabilities.Standard with { Streaming = true }),
        ("llama3.1", ModelCapabilities.Standard with { Streaming = true }),
        ("llama3.3", ModelCapabilities.Standard with { Streaming = true }),
        ("mistral-small", ModelCapabilities.Standard with { Streaming = true }),
        ("devstral", ModelCapabilities.Standard with { Streaming = true }),
        ("llama3.2", ModelCapabilities.Standard with { Streaming = true, Vision = true }),
        ("qwen2.5", ModelCapabilities.Standard with { Streaming = true }),
        ("qwen3", ModelCapabilities.Standard with { Streaming = true, Reasoning = true }),
        ("mistral-nemo", ModelCapabilities.Standard with { Streaming = true }),
        ("firefunction", ModelCapabilities.Standard with { Streaming = true }),
        ("command-r", ModelCapabilities.Standard with { Streaming = true }),
        ("llava", ModelCapabilities.TextOnly with { Streaming = true, Vision = true }),
        ("embed", ModelCapabilities.TextOnly with { Embeddings = true }),
        ("o1", ModelCapabilities.Standard with { Reasoning = true }),
        ("o3", ModelCapabilities.Standard with { Reasoning = true }),
    };

    /// <summary>
    /// Capabilities for a provider/model pair. The model fragment WINS over the provider default:
    /// a tool-capable model on Ollama is tool-capable, and a text-only model on OpenAI is not made
    /// tool-capable by the company that serves it.
    /// </summary>
    public static ModelCapabilities For(string? providerId, string? modelId)
    {
        var model = (modelId ?? "").Trim();
        foreach (var (fragment, caps) in ByModelFragment)
            if (model.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return caps;

        return ByProvider.TryGetValue((providerId ?? "").Trim(), out var byProvider)
            ? byProvider
            : ModelCapabilities.TextOnly;   // unknown provider: assume the least
    }

    /// <summary>
    /// Trim a request to what the pair can actually serve, so a caller may always ask for what it
    /// wants. Dropping tools here — once, at the seam — is what stops every call site growing its
    /// own "does this provider do tools?" branch, which is how provider names leak into
    /// orchestration logic.
    /// </summary>
    public static ModelRequest Negotiate(ModelRequest request, ModelCapabilities caps)
    {
        if (request is null) return request!;
        return request with
        {
            Tools = caps.ToolCalling ? request.Tools : Array.Empty<ModelToolSpec>(),
            ResponseSchemaJson = caps.StructuredOutput ? request.ResponseSchemaJson : null,
            Stream = request.Stream && caps.Streaming,
        };
    }
}
