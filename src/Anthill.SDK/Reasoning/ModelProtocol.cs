namespace Anthill.SDK.Reasoning;

/// <summary>
/// v3.3.0 (ADR-006) — the typed request/response the provider seam is built on.
///
/// What it replaces, and why the replacement is the whole point: <c>IModelClient.Generate(string)</c>
/// is string in, string out. Tool calling, structured output, streaming, vision, embeddings,
/// reasoning content, token accounting and per-call model selection are each unreachable through it
/// — not difficult, unreachable, because there is nowhere in the signature to put them.
///
/// These types are introduced BEFORE any caller moves, and callers migrate INTO them. The opposite
/// order is how the colony ended up with a string ant contract that survived four releases: a shim
/// written "for the migration" becomes permanent when the typed path it wraps never arrives.
/// </summary>
public sealed record ModelMessage(string Role, string Content)
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";

    /// <summary>Id of the tool call this message answers. Set only on <see cref="Tool"/> messages.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>
    /// The tool calls an ASSISTANT turn made. Required, not decorative.
    ///
    /// The OpenAI protocol pairs every `tool` message with the assistant message that requested it,
    /// by id. Recording the assistant's turn as empty content and dropping its tool_calls produces
    /// a conversation where results arrive for requests that were never made — and a model replayed
    /// that transcript cannot tell it already called the tool, so it calls again. Observed exactly
    /// that against a live model: three identical system_info calls, each answered correctly, the
    /// loop stopped by its own repeat guard with no answer produced.
    /// </summary>
    public IReadOnlyList<ModelToolCall> ToolCalls { get; init; } = Array.Empty<ModelToolCall>();

    /// <summary>
    /// Non-text parts (images, documents). Empty for the text-only calls the colony makes today;
    /// present so a vision-capable provider is a capability question rather than a redesign.
    /// </summary>
    public IReadOnlyList<ModelContentPart> Parts { get; init; } = Array.Empty<ModelContentPart>();
}

/// <summary>One non-text part of a message. <c>Kind</c> is "image" | "audio" | "document".</summary>
public sealed record ModelContentPart(string Kind, string MediaType, string Data);

/// <summary>
/// A tool offered to the model. <c>ParametersJson</c> is a JSON Schema object — the same shape every
/// provider wants, projected from <c>ToolDescriptor</c> rather than authored twice.
/// </summary>
public sealed record ModelToolSpec(string Name, string Description, string ParametersJson);

/// <summary>A tool invocation the model asked for. <c>ArgumentsJson</c> is whatever it produced.</summary>
public sealed record ModelToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// What a caller asks for. Everything optional has a defensible default so an ordinary text
/// generation stays a one-liner: the typed protocol must not make the simple case worse, or callers
/// will keep reaching for the string one.
/// </summary>
public sealed record ModelRequest
{
    public required IReadOnlyList<ModelMessage> Messages { get; init; }

    /// <summary>Explicit model id. Null means "whatever the router's policy selects for this role".</summary>
    public string? Model { get; init; }

    /// <summary>Tools offered THIS call. Never sent to a provider that cannot use them.</summary>
    public IReadOnlyList<ModelToolSpec> Tools { get; init; } = Array.Empty<ModelToolSpec>();

    /// <summary>JSON Schema the reply must satisfy, when the provider supports structured output.</summary>
    public string? ResponseSchemaJson { get; init; }

    public double? Temperature { get; init; }
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// Streaming is requested, not assumed. A provider without it returns the whole response at
    /// once and the caller sees one chunk — degradation, never an error.
    /// </summary>
    public bool Stream { get; init; }

    /// <summary>The colony's overwhelmingly common shape: one prompt, no tools.</summary>
    public static ModelRequest FromPrompt(string prompt, string? model = null) => new()
    {
        Messages = new[] { new ModelMessage(ModelMessage.User, prompt ?? "") },
        Model = model,
    };
}

/// <summary>
/// Token accounting. Nullable rather than zero-defaulted: a provider that does not report usage is
/// UNKNOWN, and recording that as "0 tokens" would quietly understate cost in any total built from
/// it. Absence and zero are different facts.
/// </summary>
public sealed record ModelUsage(int? PromptTokens, int? CompletionTokens)
{
    public int? TotalTokens => PromptTokens is null && CompletionTokens is null
        ? null
        : (PromptTokens ?? 0) + (CompletionTokens ?? 0);

    public static readonly ModelUsage Unknown = new(null, null);
}

/// <summary>
/// What came back. Carries the same <see cref="ModelCallOutcome"/> the colony already branches on,
/// so this is a widening of the existing typed result rather than a second vocabulary for success.
/// </summary>
public sealed record ModelResponse
{
    public required ModelCallOutcome Status { get; init; }
    public required string Content { get; init; }

    /// <summary>Tool calls the model requested. Empty when it answered directly.</summary>
    public IReadOnlyList<ModelToolCall> ToolCalls { get; init; } = Array.Empty<ModelToolCall>();

    /// <summary>Reasoning-model thinking, when the provider exposes it separately from the answer.</summary>
    public string? Reasoning { get; init; }

    public ModelUsage Usage { get; init; } = ModelUsage.Unknown;

    /// <summary>The model that actually served this — not the one requested, which may have been null.</summary>
    public string? Model { get; init; }
    public string? Provider { get; init; }

    /// <summary>Provider's own stop reason, verbatim, for diagnosis. Never used for control flow.</summary>
    public string? FinishReason { get; init; }

    public bool Ok => Status == ModelCallOutcome.Ok;

    /// <summary>Bridge from the current result type while callers migrate.</summary>
    public static ModelResponse FromCallResult(ModelCallResult result, string? provider = null, string? model = null) =>
        new() { Status = result.Status, Content = result.Content, Provider = provider, Model = model };

    /// <remarks>
    /// v0.3.8.57 — carries Provider and Model. This conversion USED to drop them, which is why an
    /// artifact could not say which model produced it: the router resolved a route, the response
    /// recorded it, and the narrowing to ModelCallResult threw it away one line before the ant.
    /// </remarks>
    public ModelCallResult ToCallResult() => new(Status, Content) { Provider = Provider, Model = Model };
}
