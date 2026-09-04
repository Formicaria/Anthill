using Anthill.SDK.Common;
using Anthill.SDK.Contracts;
using Anthill.SDK.Knowledge;
using Anthill.SDK.Tools;

namespace Anthill.Modules.Knowledge;

// The knowledge tools: how an ant asks what the organization knows.
//
// TWO THINGS ARE TRUE OF EVERY TOOL IN THIS FILE and are worth stating once rather than six times.
//
// 1. THE SCOPE IS NOT AN ARGUMENT. Not one of these tools takes a project id. The scope comes from
//    KnowledgeScopeContext, which the core enters at mission intake, so a model cannot widen its own
//    reach by asking. See KnowledgeScopeContext's own comment for why an argument would have been a
//    Rule 12 hole rather than a convenience.
//
// 2. UNAVAILABLE IS NOT EMPTY. When knowledge cannot be reached, these tools say so explicitly and
//    tell the model not to substitute anything for what it did not get. An empty success would
//    invite a confident answer from priors, which is the specific failure the whole subsystem exists
//    to prevent.
//
// On sync-over-async: ITool.Run is synchronous and the provider is not, so each tool blocks on its
// call. This is safe here and deliberately bounded — there is no synchronization context in .NET to
// deadlock against, and every call carries a hard timeout from configuration, so a stalled FORAGER
// occupies a thread-pool thread for at most RetrievalTimeoutMs rather than indefinitely. Making
// ITool async would be the better fix and is a change to the tool contract that every tool in the
// colony would have to follow; it is not this integration's to make.

/// <summary>
/// Shared behaviour for the knowledge tools: scope resolution, timeout, failure translation.
/// </summary>
internal abstract class KnowledgeToolBase : ITool
{
    protected readonly IKnowledgeProvider Provider;
    protected readonly KnowledgeOptionsSource Options;

    protected KnowledgeToolBase(IKnowledgeProvider provider, KnowledgeOptionsSource options)
    {
        Provider = provider;
        Options = options;
    }

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract ToolResult Run(IReadOnlyDictionary<string, object?> args);
    public virtual string ParametersJson => """{"type":"object","properties":{}}""";

    /// <summary>
    /// The scope this call may read. Ambient, never from arguments.
    /// </summary>
    protected KnowledgeScope Scope => KnowledgeScopeContext.Current;

    /// <summary>
    /// Run a provider call to completion under a hard deadline, translating every outcome — success,
    /// typed failure, or an escaped exception — into a <see cref="ToolResult"/>.
    ///
    /// Nothing throws out of here. A tool that throws loses its failure classification at the
    /// registry boundary, and the registry's fallback classification is strictly less informed than
    /// what the provider already told us.
    /// </summary>
    protected ToolResult Execute<T>(
        Func<CancellationToken, Task<KnowledgeOutcome<T>>> call,
        Func<T, string> render,
        int timeoutMs) where T : class
    {
        if (!Scope.IsQueryable)
            return new ToolResult(Name, false, "",
                "No knowledge scope is in force for this mission, so there is nothing this tool may read. "
              + "A project must be mapped to a knowledge base before its knowledge is retrievable. "
              + "Do not substitute recalled or assumed facts for organizational knowledge.",
                FailureClass.AuthorizationFailure);

        try
        {
            using var deadline = new CancellationTokenSource(Math.Max(500, timeoutMs) + 500);
            var outcome = call(deadline.Token).GetAwaiter().GetResult();

            if (outcome.Ok && outcome.Value is not null)
                return new ToolResult(Name, true, render(outcome.Value));

            var reason = outcome.Reason ?? "the knowledge service returned no usable result";
            if (outcome.UpstreamRequestId is { Length: > 0 } id) reason += $" (request {id})";

            return new ToolResult(Name, false, "", Unavailable(reason, outcome.Failure), Classify(outcome.Failure));
        }
        catch (Exception error)
        {
            return new ToolResult(Name, false, "",
                $"The knowledge tool failed unexpectedly: {error.Message}", ToolFailure.Classify(error));
        }
    }

    /// <summary>
    /// The refusal text a model reads. The last sentence is load-bearing safety design, not a
    /// pleasantry: without it, a model told only that retrieval failed will commonly proceed to
    /// answer from its own training as though it had retrieved something.
    /// </summary>
    private static string Unavailable(string reason, KnowledgeFailure failure)
    {
        var lead = failure switch
        {
            KnowledgeFailure.Disabled => "Organizational knowledge is not configured for this colony",
            KnowledgeFailure.ScopeUnresolved => "No knowledge scope is in force for this mission",
            KnowledgeFailure.NotFound => "That knowledge item does not exist in this scope",
            KnowledgeFailure.Unauthorized => "The knowledge service refused this colony's credentials",
            _ => "Knowledge retrieval is unavailable",
        };

        var text = $"{lead}: {reason}.";

        if (failure is KnowledgeFailure.NotFound or KnowledgeFailure.Invalid) return text;

        return text
            + "\nThe mission can continue without organizational knowledge, but evidence-backed context "
            + "could not be retrieved. Do NOT substitute recalled, assumed, or generally-known facts for "
            + "it, and do not present anything from this attempt as sourced.";
    }

    /// <summary>
    /// Knowledge failures into the colony's failure taxonomy. The mapping decides what the agent
    /// loop does next, so it is about RECOVERY rather than about severity: retryable transport
    /// faults become transient, gates become authorization, and anything the caller could fix
    /// becomes validation.
    /// </summary>
    private static FailureClass Classify(KnowledgeFailure failure) => failure switch
    {
        KnowledgeFailure.Disabled => FailureClass.AuthorizationFailure,
        KnowledgeFailure.ScopeUnresolved => FailureClass.AuthorizationFailure,
        KnowledgeFailure.Unauthorized => FailureClass.AuthorizationFailure,
        KnowledgeFailure.Unavailable => FailureClass.TransientProviderFailure,
        KnowledgeFailure.Upstream => FailureClass.TransientProviderFailure,
        KnowledgeFailure.Timeout => FailureClass.Timeout,
        KnowledgeFailure.NotFound => FailureClass.TargetRejection,
        KnowledgeFailure.Invalid => FailureClass.ValidationFailure,
        KnowledgeFailure.Malformed => FailureClass.InvalidArtifact,
        _ => FailureClass.ToolFailure,
    };

    protected static string? Text(IReadOnlyDictionary<string, object?> args, string key) =>
        args.GetValueOrDefault(key)?.ToString();

    protected static int Number(IReadOnlyDictionary<string, object?> args, string key, int fallback)
    {
        var raw = args.GetValueOrDefault(key)?.ToString();
        return int.TryParse(raw, out var value) ? value : fallback;
    }

    protected static bool Flag(IReadOnlyDictionary<string, object?> args, string key, bool fallback)
    {
        var raw = args.GetValueOrDefault(key)?.ToString();
        return bool.TryParse(raw, out var value) ? value : fallback;
    }
}

/// <summary>Ranked candidates. The cheap "is there anything about X" call.</summary>
internal sealed class KnowledgeSearchTool : KnowledgeToolBase
{
    public KnowledgeSearchTool(IKnowledgeProvider provider, KnowledgeOptionsSource options)
        : base(provider, options) { }

    public override string Name => KnowledgeToolNames.Search;

    public override string Description =>
        "Search the organization's canonical knowledge base for statements matching a query. "
      + "Returns ranked candidates with their support level, status and confidence, but NOT their "
      + "evidence — use knowledge_retrieve when you need evidence-backed context to reason from, or "
      + "knowledge_get for one item. Read-only. Scoped automatically to this mission's project.";

    public override string ParametersJson => """
        {"type":"object",
         "properties":{
           "query":{"type":"string","description":"What to look for, in natural language or keywords"},
           "limit":{"type":"integer","description":"Maximum results, 1-50 (default 10)"},
           "include_historical":{"type":"boolean","description":"Include superseded and archived statements. Use this to answer questions about what was true at an earlier time (default false)"}},
         "required":["query"]}
        """;

    public override ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        var query = Text(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return new ToolResult(Name, false, "", "Missing required argument: query", FailureClass.ValidationFailure);

        var options = Options();
        return Execute(
            token => Provider.SearchAsync(new KnowledgeSearchRequest
            {
                Query = query,
                Scope = Scope,
                Limit = Number(args, "limit", 10),
                IncludeHistorical = Flag(args, "include_historical", false),
            }, token),
            result => Render(result),
            options.RetrievalTimeoutMs);
    }

    private static string Render(KnowledgeSearchResult result)
    {
        if (result.Hits.Count == 0)
            return "No organizational knowledge matched this query.\n"
                 + "The knowledge base was searched and had nothing. This is not evidence that the "
                 + "answer is unknown to the organization, and it is not permission to assume one.";

        var text = new System.Text.StringBuilder();
        text.Append(result.Hits.Count).Append(" result(s):\n");
        var index = 1;
        foreach (var hit in result.Hits)
        {
            text.Append('\n').Append(index++).Append(". ").Append(hit.Statement).Append('\n');
            text.Append("   id: ").Append(hit.KnowledgeId)
                .Append("   type: ").Append(hit.Type)
                .Append("   support: ").Append(hit.Support.ToString())
                .Append("   status: ").Append(hit.Status.ToString())
                .Append("   confidence: ").Append(hit.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                .Append('\n');
            if (hit.IsContested)
                text.Append("   CONTESTED: another source disagrees. Do not report this as settled.\n");
            if (hit.EvidenceCount == 0)
                text.Append("   NO EVIDENCE: this statement is unresolved.\n");
            if (!string.IsNullOrWhiteSpace(hit.Why))
                text.Append("   why: ").Append(hit.Why).Append('\n');
        }
        text.Append("\nUse knowledge_get or knowledge_evidence with an id to see the supporting sources.\n");
        return text.ToString();
    }
}

/// <summary>The main retrieval path: evidence-backed, conflict-aware context.</summary>
internal sealed class KnowledgeRetrieveTool : KnowledgeToolBase
{
    public KnowledgeRetrieveTool(IKnowledgeProvider provider, KnowledgeOptionsSource options)
        : base(provider, options) { }

    public override string Name => KnowledgeToolNames.Retrieve;

    public override string Description =>
        "Retrieve evidence-backed organizational knowledge for a question. Returns statements with "
      + "their support classification (direct fact, supported inference, uncertain inference, "
      + "unverified claim), the exact source excerpts behind each one, related entities, and any "
      + "conflicts where sources disagree. This is the tool to use before answering a question about "
      + "what the organization knows, decided, or documented. Read-only. You may call it more than "
      + "once as your understanding narrows.";

    public override string ParametersJson => """
        {"type":"object",
         "properties":{
           "query":{"type":"string","description":"The question or topic to retrieve knowledge about"},
           "top_k":{"type":"integer","description":"How many statements to assemble, 1-50 (default 8)"},
           "include_entities":{"type":"boolean","description":"Include related people, projects and organizations (default true)"},
           "include_relationships":{"type":"boolean","description":"Include typed relationships between entities (default false)"},
           "include_historical":{"type":"boolean","description":"Include superseded statements, for questions about an earlier point in time (default false)"}},
         "required":["query"]}
        """;

    public override ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        var query = Text(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return new ToolResult(Name, false, "", "Missing required argument: query", FailureClass.ValidationFailure);

        var options = Options();
        return Execute(
            token => Provider.RetrieveAsync(new KnowledgeRetrievalRequest
            {
                Query = query,
                Scope = Scope,
                TopK = Number(args, "top_k", options.DefaultTopK),
                IncludeEntities = Flag(args, "include_entities", true),
                IncludeRelationships = Flag(args, "include_relationships", false),
                IncludeHistorical = Flag(args, "include_historical", false),

                // Note what is absent: there is no include_conflicts parameter. Rule 10 says
                // conflicts are never hidden, and an option to hide them is a way to hide them.
                MaxContextChars = options.MaxContextChars,
            }, token),
            context => context.Render(),
            options.RetrievalTimeoutMs);
    }
}

/// <summary>One item, with everything known about it.</summary>
internal sealed class KnowledgeGetTool : KnowledgeToolBase
{
    public KnowledgeGetTool(IKnowledgeProvider provider, KnowledgeOptionsSource options)
        : base(provider, options) { }

    public override string Name => KnowledgeToolNames.Get;

    public override string Description =>
        "Fetch one knowledge item by its id, with its support classification, status, confidence and "
      + "evidence count. Ids come from knowledge_search or knowledge_retrieve. Read-only.";

    public override string ParametersJson => """
        {"type":"object",
         "properties":{
           "knowledge_id":{"type":"string","description":"The knowledge item id, e.g. ki_68c6b4a77fc81cb5"}},
         "required":["knowledge_id"]}
        """;

    public override ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        var id = Text(args, "knowledge_id");
        if (string.IsNullOrWhiteSpace(id))
            return new ToolResult(Name, false, "", "Missing required argument: knowledge_id", FailureClass.ValidationFailure);

        return Execute(
            token => Provider.GetAsync(id, Scope, token),
            fact => Json.Dumps(new
            {
                fact.KnowledgeId,
                fact.Type,
                fact.Subject,
                fact.Statement,
                Support = fact.Support.ToString(),
                Status = fact.Status.ToString(),
                fact.Confidence,
                fact.EffectiveDate,
                fact.SupersededBy,
                EvidenceCount = fact.EvidenceIds.Count,
                Contested = fact.IsContested,
                HasProvenance = fact.HasProvenance,
                fact.Extractor,
            }, indented: true),
            Options().RetrievalTimeoutMs);
    }
}

/// <summary>Why the colony believes something.</summary>
internal sealed class KnowledgeEvidenceTool : KnowledgeToolBase
{
    public KnowledgeEvidenceTool(IKnowledgeProvider provider, KnowledgeOptionsSource options)
        : base(provider, options) { }

    public override string Name => KnowledgeToolNames.Evidence;

    public override string Description =>
        "Show the evidence behind a knowledge item: which source each claim came from, where in that "
      + "source, and the exact quoted text. Use this to verify a statement before relying on it, and "
      + "to cite it accurately. Read-only.";

    public override string ParametersJson => """
        {"type":"object",
         "properties":{
           "knowledge_id":{"type":"string","description":"The knowledge item id to explain"}},
         "required":["knowledge_id"]}
        """;

    public override ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        var id = Text(args, "knowledge_id");
        if (string.IsNullOrWhiteSpace(id))
            return new ToolResult(Name, false, "", "Missing required argument: knowledge_id", FailureClass.ValidationFailure);

        return Execute(
            token => Provider.GetEvidenceAsync(id, Scope, token),
            evidence => Render(id, evidence),
            Options().RetrievalTimeoutMs);
    }

    private static string Render(string id, IReadOnlyList<KnowledgeEvidence> evidence)
    {
        if (evidence.Count == 0)
            return $"Knowledge item {id} has NO located evidence. It is unresolved: something asserted it, "
                 + "but the supporting text cannot be found. Do not rely on it without checking the source.";

        var text = new System.Text.StringBuilder();
        text.Append("Evidence for ").Append(id).Append(" (").Append(evidence.Count).Append(" link(s)):\n");
        foreach (var item in evidence)
        {
            text.Append("\n- source: ").Append(item.SourceName ?? item.SourceId).Append('\n');
            if (!string.IsNullOrWhiteSpace(item.Location)) text.Append("  location: ").Append(item.Location).Append('\n');
            if (!string.IsNullOrWhiteSpace(item.Excerpt)) text.Append("  excerpt: \"").Append(item.Excerpt).Append("\"\n");
            if (!string.IsNullOrWhiteSpace(item.Extractor)) text.Append("  extractor: ").Append(item.Extractor).Append('\n');
            if (item.MissingExcerpt)
                text.Append("  WARNING: the excerpt could not be located in the source any more.\n");
        }
        return text.ToString();
    }
}

/// <summary>Entity lookup, for pivoting from a name to what is known about it.</summary>
internal sealed class KnowledgeEntityTool : KnowledgeToolBase
{
    public KnowledgeEntityTool(IKnowledgeProvider provider, KnowledgeOptionsSource options)
        : base(provider, options) { }

    public override string Name => KnowledgeToolNames.Entity;

    public override string Description =>
        "Look up a person, project, organization, customer or product by name in the organization's "
      + "knowledge base. Returns the canonical entity with the other names it is known by — useful "
      + "when the same person appears as 'Bob Smith' in one document and 'Robert Smith' in another. "
      + "Read-only.";

    public override string ParametersJson => """
        {"type":"object",
         "properties":{
           "name":{"type":"string","description":"The name to look up"}},
         "required":["name"]}
        """;

    public override ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        var name = Text(args, "name");
        if (string.IsNullOrWhiteSpace(name))
            return new ToolResult(Name, false, "", "Missing required argument: name", FailureClass.ValidationFailure);

        return Execute(
            token => Provider.FindEntitiesAsync(name, Scope, token),
            entities => entities.Count == 0
                ? $"No entity named '{name}' is known in this scope."
                : Json.Dumps(entities.Select(e => new
                {
                    e.EntityId,
                    e.Name,
                    e.Type,
                    e.Aliases,
                    e.MentionCount,
                }), indented: true),
            Options().RetrievalTimeoutMs);
    }
}

/// <summary>
/// Propose a review action. The one non-read-only tool here, and it is still not a mutation.
///
/// It records a PROPOSAL for an operator. Nothing in this tool reaches FORAGER, and nothing an agent
/// does through it changes canonical knowledge — that is Rule 8, and the reason the tool is written
/// this way rather than as a passthrough to FORAGER's review endpoint.
/// </summary>
internal sealed class KnowledgeReviewTool : ITool
{
    private readonly Action<KnowledgeReviewProposal> _propose;

    public KnowledgeReviewTool(Action<KnowledgeReviewProposal> propose) => _propose = propose;

    public string Name => KnowledgeToolNames.Review;

    public string Description =>
        "Propose a review decision about a knowledge item — that it looks correct, that it should be "
      + "rejected, or that it should be archived — for a human to approve. This does NOT change the "
      + "knowledge base. Use it when your investigation found that a stored statement is wrong, "
      + "outdated, or contradicted by better evidence. A rationale is required.";

    public string ParametersJson => """
        {"type":"object",
         "properties":{
           "knowledge_id":{"type":"string","description":"The knowledge item the proposal is about"},
           "action":{"type":"string","enum":["mark_reviewed","reject","restore","archive"],"description":"What you are proposing"},
           "rationale":{"type":"string","description":"Why, citing the evidence that led you here"}},
         "required":["knowledge_id","action","rationale"]}
        """;

    private static readonly IReadOnlySet<string> Actions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mark_reviewed", "reject", "restore", "archive" };

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        var scope = KnowledgeScopeContext.Current;
        if (!scope.IsQueryable)
            return new ToolResult(Name, false, "",
                "No knowledge scope is in force, so there is no knowledge base to propose against.",
                FailureClass.AuthorizationFailure);

        var id = args.GetValueOrDefault("knowledge_id")?.ToString();
        var action = args.GetValueOrDefault("action")?.ToString();
        var rationale = args.GetValueOrDefault("rationale")?.ToString();

        if (string.IsNullOrWhiteSpace(id))
            return new ToolResult(Name, false, "", "Missing required argument: knowledge_id", FailureClass.ValidationFailure);
        if (string.IsNullOrWhiteSpace(action) || !Actions.Contains(action))
            return new ToolResult(Name, false, "",
                $"action must be one of: {string.Join(", ", Actions)}", FailureClass.ValidationFailure);

        // Required, and enforced rather than encouraged. An unexplained proposal cannot be reviewed,
        // so accepting one would produce approval requests an operator has no basis to decide.
        if (string.IsNullOrWhiteSpace(rationale) || rationale.Trim().Length < 12)
            return new ToolResult(Name, false, "",
                "A rationale is required, and must say what evidence led to this proposal. "
              + "An unexplained proposal cannot be reviewed.", FailureClass.ValidationFailure);

        try
        {
            _propose(new KnowledgeReviewProposal
            {
                KnowledgeId = id,
                Scope = scope,
                Action = action,
                Rationale = rationale.Trim(),
                MissionId = scope.MissionId,
            });
        }
        catch (Exception error)
        {
            return new ToolResult(Name, false, "",
                $"The review proposal could not be recorded: {error.Message}", ToolFailure.Classify(error));
        }

        return new ToolResult(Name, true,
            $"Recorded a proposal to '{action}' knowledge item {id}. This has NOT changed the knowledge "
          + "base; it is queued for an operator to approve or decline. Continue without assuming the "
          + "change has been applied.");
    }
}
