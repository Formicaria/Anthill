using Anthill.Core.Configuration;
using Anthill.Core.Security;
// ToolRuntime.Live — the live capability gates the workspace guard re-reads on every call. In
// Anthill.Core.Tools rather than .Security, where WorkspacePathGuard itself lives.
using Anthill.Core.Tools;
using Anthill.Modules.Knowledge;
using Anthill.SDK.Knowledge;

namespace Anthill.Api;

/// <summary>
/// THE KNOWLEDGE SURFACE — v0.3.8.121.
///
/// What the console talks to when it asks what the organization knows. Every route here is a thin,
/// authenticated, SCOPED proxy onto <see cref="IKnowledgeProvider"/>, which is itself a thin proxy
/// onto FORAGER. Nothing in this file interprets knowledge; it authenticates the caller, resolves
/// the scope, and hands back what came out of the provider.
///
/// TWO BOUNDARY RULES, both load-bearing:
///
/// 1. THE CONSOLE NEVER TALKS TO FORAGER DIRECTLY. FORAGER has no authentication of its own — it
///    expects to own its loopback interface — so ANTHILL is the authenticated edge. Routing the
///    browser at FORAGER's port would put an unauthenticated knowledge base on the operator's
///    network with the colony's blessing.
///
/// 2. THE SCOPE IS RESOLVED HERE, from the caller's requested project through the operator's
///    configured map. A caller cannot name a FORAGER project id directly, so no request can reach a
///    knowledge base the operator has not mapped — which is Rule 12 at the HTTP edge, matching the
///    ambient-scope enforcement the tools get on the mission side.
///
/// Reads require <c>read_knowledge</c>; ingestion and review require <c>manage_knowledge</c>.
/// </summary>
public static partial class ApiHost
{
    /// <summary>
    /// The knowledge module, held so the routes can reach its provider. Constructed in
    /// <see cref="InitKnowledge"/> and handed to <c>Modules.LoadAll</c> in the same breath, so there
    /// is exactly one instance and exactly one HTTP client behind it.
    /// </summary>
    public static KnowledgeModule KnowledgeHost { get; private set; } = null!;

    private const int KnowledgeMaxLimit = 50;

    private sealed record KnowledgeRetrieveRequest(string? Query, string? Project, int? TopK, bool? IncludeHistorical);
    private sealed record KnowledgeIngestRequest(string? Project, string[]? Paths, bool? Force);

    /// <summary>
    /// Build the module. Called from <c>Run()</c> before <c>builder.Build()</c>, and the result is
    /// passed to <c>Modules.LoadAll</c> — constructing it here rather than inline there is what lets
    /// these routes share the one instance instead of standing up a second client.
    ///
    /// Performs no I/O and does not probe FORAGER. An unreachable knowledge base must not be able to
    /// stop the colony booting.
    /// </summary>
    private static KnowledgeModule InitKnowledge()
    {
        KnowledgeHost = new KnowledgeModule(
            // Read live on every call, never captured — an operator disabling knowledge or moving
            // the endpoint takes effect on the next request rather than the next restart.
            () =>
            {
                var settings = AnthillRuntime.Knowledge;
                return new KnowledgeOptions
                {
                    Enabled = settings.Enabled,
                    Endpoint = settings.Endpoint,
                    Token = settings.Token,
                    AllowRemoteEndpoint = settings.AllowRemote,
                    ProbeTimeoutMs = settings.ProbeTimeoutMs,
                    RetrievalTimeoutMs = settings.RetrievalTimeoutMs,
                    IngestionTimeoutMs = settings.IngestionTimeoutMs,
                    DefaultTopK = settings.DefaultTopK,
                    MaxContextChars = settings.MaxContextChars,
                    CacheSeconds = settings.CacheSeconds,
                    ProjectMap = settings.ProjectMap,
                    DefaultProjectRef = settings.DefaultProject,
                };
            },
            // WHERE A PROPOSAL GOES — v0.3.8.122, and it now goes somewhere that survives.
            //
            // This was `Queen?.Events.Publish(...)` under `EventTypes.ModuleRegistered`, and both
            // halves were wrong in ways that compounded. `Publish` is BUS-ONLY: the proposal reached
            // whichever browsers happened to have the stream open at that instant and then ceased to
            // exist — while the tool told the model it was "queued for an operator to approve or
            // decline". A worker was being told its proposal had been filed, by a call that filed
            // nothing. And `module_registered` is a one-time boot event, so even the live copy was
            // shelved where nobody looking for proposals would think to look.
            //
            // `LogEvent` writes the row and THEN publishes, so the console's live stream is
            // unchanged and the proposal is now in the event log, replayable on reconnect and
            // auditable afterwards. This is not the approval pipeline — a typed proposal KIND is a
            // core surface and deserves its own release — but a durable record with every field an
            // operator needs is the difference between "not built yet" and "silently discarded".
            //
            // No colony composed means nowhere durable to put it, and that THROWS rather than
            // returning quietly: the tool has a failure branch that tells the worker the proposal
            // could not be recorded, and that answer is true. The previous null-conditional made the
            // same situation look like success.
            proposal =>
            {
                var queen = Queen ?? throw new InvalidOperationException(
                    "No colony is composed, so a knowledge review proposal has nowhere durable to go.");
                queen.Memory.LogEvent(
                    string.IsNullOrWhiteSpace(proposal.MissionId)
                        ? AnthillRuntime.SystemApiMissionId
                        : proposal.MissionId!,
                    SDK.Events.EventTypes.KnowledgeReviewProposed,
                    $"Knowledge review proposed: {proposal.Action} {proposal.KnowledgeId}",
                    metadata: new Dictionary<string, object?>
                    {
                        ["module"] = "knowledge",
                        ["knowledge_id"] = proposal.KnowledgeId,
                        ["action"] = proposal.Action,
                        ["rationale"] = proposal.Rationale,
                        ["scope"] = proposal.Scope.ToString(),
                    });
            });

        return KnowledgeHost;
    }

    private static void MapKnowledgeEndpoints(WebApplication app)
    {
        // Availability. The one route that answers usefully when knowledge is OFF — the console
        // needs to distinguish "not configured", "configured but unreachable" and "working", and a
        // 404 would collapse all three into the same blank panel.
        app.MapGet("/knowledge/status", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Read); if (auth is not null) return auth;

            var availability = await KnowledgeHost.Provider
                .ProbeAsync(ctx.RequestAborted).ConfigureAwait(false);

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["enabled"] = availability.Enabled,
                ["reachable"] = availability.Reachable,
                ["usable"] = availability.Usable,
                ["version"] = availability.Version,
                ["schema_version"] = availability.SchemaVersion,
                ["search_backend"] = availability.SearchBackend,
                ["model_provider"] = availability.ModelProvider,
                // The endpoint, never the token. This payload reaches the browser.
                ["endpoint"] = availability.Endpoint,
                ["reason"] = availability.Reason,
                ["projects"] = AnthillRuntime.Knowledge.ProjectMap.Keys.ToList(),
            });
        });

        app.MapGet("/knowledge/search", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Read); if (auth is not null) return auth;

            var query = ctx.Request.Query["q"].ToString();
            if (string.IsNullOrWhiteSpace(query))
                return ApiJson.Error("A search query is required.", "bad_request");

            var scope = ResolveKnowledgeScope(ctx.Request.Query["project"].ToString());
            if (!scope.IsQueryable) return KnowledgeScopeRefusal();

            var limit = int.TryParse(ctx.Request.Query["limit"], out var parsed) ? parsed : 20;
            var historical = ctx.Request.Query["include_historical"].ToString() == "true";

            var result = await KnowledgeHost.Provider.SearchAsync(new KnowledgeSearchRequest
            {
                Query = query,
                Scope = scope,
                Limit = Math.Clamp(limit, 1, KnowledgeMaxLimit),
                IncludeHistorical = historical,
            }, ctx.RequestAborted).ConfigureAwait(false);

            if (!result.Ok || result.Value is null) return KnowledgeFailureResult(result.Failure, result.Reason);

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["query"] = query,
                ["backend"] = result.Value.Metadata.Backend,
                ["took_ms"] = result.Value.Metadata.ElapsedMs,
                ["hits"] = result.Value.Hits.Select(h => new Dictionary<string, object?>
                {
                    ["knowledge_id"] = h.KnowledgeId,
                    ["statement"] = h.Statement,
                    ["title"] = h.Title,
                    ["type"] = h.Type,
                    ["support"] = h.Support.ToString(),
                    ["status"] = h.Status.ToString(),
                    ["confidence"] = h.Confidence,
                    ["score"] = h.Score,
                    ["snippet"] = h.Snippet,
                    ["why"] = h.Why,
                    ["evidence_count"] = h.EvidenceCount,
                    ["contested"] = h.IsContested,
                }).ToList(),
                ["entities"] = result.Value.Entities.Select(e => new Dictionary<string, object?>
                {
                    ["entity_id"] = e.EntityId, ["name"] = e.Name, ["type"] = e.Type, ["aliases"] = e.Aliases,
                }).ToList(),
            });
        });

        // Retrieval — a POST because the body carries options and because a retrieval is expensive
        // enough that it should not be something a browser repeats by re-issuing a cached GET.
        app.MapPost("/knowledge/retrieve", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Read); if (auth is not null) return auth;

            KnowledgeRetrieveRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<KnowledgeRetrieveRequest>().ConfigureAwait(false); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }

            if (string.IsNullOrWhiteSpace(body?.Query))
                return ApiJson.Error("A query is required.", "bad_request");

            var scope = ResolveKnowledgeScope(body.Project);
            if (!scope.IsQueryable) return KnowledgeScopeRefusal();

            var result = await KnowledgeHost.Provider.RetrieveAsync(new KnowledgeRetrievalRequest
            {
                Query = body.Query,
                Scope = scope,
                TopK = Math.Clamp(body.TopK ?? AnthillRuntime.Knowledge.DefaultTopK, 1, KnowledgeMaxLimit),
                IncludeHistorical = body.IncludeHistorical ?? false,
                MaxContextChars = AnthillRuntime.Knowledge.MaxContextChars,
            }, ctx.RequestAborted).ConfigureAwait(false);

            if (!result.Ok || result.Value is null) return KnowledgeFailureResult(result.Failure, result.Reason);
            return ApiJson.Ok(KnowledgeContextPayload(result.Value));
        });

        app.MapGet("/knowledge/items/{id}", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Read); if (auth is not null) return auth;
            var scope = ResolveKnowledgeScope(ctx.Request.Query["project"].ToString());
            if (!scope.IsQueryable) return KnowledgeScopeRefusal();

            var result = await KnowledgeHost.Provider.GetAsync(id, scope, ctx.RequestAborted).ConfigureAwait(false);
            if (!result.Ok || result.Value is null) return KnowledgeFailureResult(result.Failure, result.Reason);

            var fact = result.Value;
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["knowledge_id"] = fact.KnowledgeId,
                ["type"] = fact.Type,
                ["subject"] = fact.Subject,
                ["title"] = fact.Title,
                ["statement"] = fact.Statement,
                ["attribute_key"] = fact.AttributeKey,
                ["attribute_value"] = fact.AttributeValue,
                ["support"] = fact.Support.ToString(),
                ["status"] = fact.Status.ToString(),
                ["confidence"] = fact.Confidence,
                ["confidentiality"] = fact.Confidentiality.ToString(),
                ["effective_date"] = fact.EffectiveDate,
                ["superseded_by"] = fact.SupersededBy,
                ["evidence_ids"] = fact.EvidenceIds,
                ["entity_ids"] = fact.EntityIds,
                ["conflict_ids"] = fact.ConflictIds,
                ["extractor"] = fact.Extractor,
                ["has_provenance"] = fact.HasProvenance,
                ["contested"] = fact.IsContested,
            });
        });

        // "Why does the colony believe this?" — one click in the console, one route here.
        app.MapGet("/knowledge/items/{id}/evidence", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Read); if (auth is not null) return auth;
            var scope = ResolveKnowledgeScope(ctx.Request.Query["project"].ToString());
            if (!scope.IsQueryable) return KnowledgeScopeRefusal();

            var result = await KnowledgeHost.Provider.GetEvidenceAsync(id, scope, ctx.RequestAborted).ConfigureAwait(false);
            if (!result.Ok || result.Value is null) return KnowledgeFailureResult(result.Failure, result.Reason);

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["knowledge_id"] = id,
                ["evidence"] = result.Value.Select(KnowledgeEvidencePayload).ToList(),
            });
        });

        app.MapGet("/knowledge/entities", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Read); if (auth is not null) return auth;
            var scope = ResolveKnowledgeScope(ctx.Request.Query["project"].ToString());
            if (!scope.IsQueryable) return KnowledgeScopeRefusal();

            var name = ctx.Request.Query["name"].ToString();
            if (string.IsNullOrWhiteSpace(name))
                return ApiJson.Error("An entity name is required.", "bad_request");

            var result = await KnowledgeHost.Provider.FindEntitiesAsync(name, scope, ctx.RequestAborted).ConfigureAwait(false);
            if (!result.Ok || result.Value is null) return KnowledgeFailureResult(result.Failure, result.Reason);

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["entities"] = result.Value.Select(e => new Dictionary<string, object?>
                {
                    ["entity_id"] = e.EntityId,
                    ["name"] = e.Name,
                    ["type"] = e.Type,
                    ["aliases"] = e.Aliases,
                    ["mention_count"] = e.MentionCount,
                    ["confidence"] = e.Confidence,
                }).ToList(),
            });
        });

        app.MapGet("/knowledge/conflicts", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Read); if (auth is not null) return auth;
            var scope = ResolveKnowledgeScope(ctx.Request.Query["project"].ToString());
            if (!scope.IsQueryable) return KnowledgeScopeRefusal();

            var result = await KnowledgeHost.Provider.GetConflictsAsync(scope, ctx.RequestAborted).ConfigureAwait(false);
            if (!result.Ok || result.Value is null) return KnowledgeFailureResult(result.Failure, result.Reason);

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["conflicts"] = result.Value.Select(KnowledgeConflictPayload).ToList(),
            });
        });

        app.MapGet("/knowledge/sources", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Read); if (auth is not null) return auth;
            var scope = ResolveKnowledgeScope(ctx.Request.Query["project"].ToString());
            if (!scope.IsQueryable) return KnowledgeScopeRefusal();

            var ingestion = KnowledgeHost.Ingestion;
            if (ingestion is null) return KnowledgeFailureResult(KnowledgeFailure.Disabled, "knowledge is not configured");

            var result = await ingestion.ListSourcesAsync(scope, ctx.RequestAborted).ConfigureAwait(false);
            if (!result.Ok || result.Value is null) return KnowledgeFailureResult(result.Failure, result.Reason);

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["sources"] = result.Value.Select(s => new Dictionary<string, object?>
                {
                    ["source_id"] = s.SourceId,
                    ["name"] = s.Name,
                    ["type"] = s.Type,
                    ["content_hash"] = s.ContentHash,
                    ["size_bytes"] = s.SizeBytes,
                    ["processing_status"] = s.ProcessingStatus,
                    ["document_date"] = s.DocumentDate,
                    ["authoritative"] = s.Authoritative,
                    ["duplicate_of"] = s.DuplicateOf,
                    ["superseded_by"] = s.SupersededBy,
                    ["chunk_count"] = s.ChunkCount,
                }).ToList(),
            });
        });

        // ---- ingestion (manage_knowledge) ------------------------------------------------------

        app.MapGet("/knowledge/jobs", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Read); if (auth is not null) return auth;
            var scope = ResolveKnowledgeScope(ctx.Request.Query["project"].ToString());
            if (!scope.IsQueryable) return KnowledgeScopeRefusal();

            var ingestion = KnowledgeHost.Ingestion;
            if (ingestion is null) return KnowledgeFailureResult(KnowledgeFailure.Disabled, "knowledge is not configured");

            var result = await ingestion.ListJobsAsync(scope, 20, ctx.RequestAborted).ConfigureAwait(false);
            if (!result.Ok || result.Value is null) return KnowledgeFailureResult(result.Failure, result.Reason);

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["jobs"] = result.Value.Select(KnowledgeJobPayload).ToList(),
            });
        });

        app.MapGet("/knowledge/jobs/{id}", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Read); if (auth is not null) return auth;
            var scope = ResolveKnowledgeScope(ctx.Request.Query["project"].ToString());
            if (!scope.IsQueryable) return KnowledgeScopeRefusal();

            var ingestion = KnowledgeHost.Ingestion;
            if (ingestion is null) return KnowledgeFailureResult(KnowledgeFailure.Disabled, "knowledge is not configured");

            var result = await ingestion.GetJobAsync(id, scope, ctx.RequestAborted).ConfigureAwait(false);
            if (!result.Ok || result.Value is null) return KnowledgeFailureResult(result.Failure, result.Reason);
            return ApiJson.Ok(KnowledgeJobPayload(result.Value));
        });

        // Start ingestion. Returns as soon as FORAGER has QUEUED the work — this request never waits
        // for a document to be parsed, however large the archive.
        app.MapPost("/knowledge/jobs", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Manage); if (auth is not null) return auth;

            KnowledgeIngestRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<KnowledgeIngestRequest>().ConfigureAwait(false); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }

            var scope = ResolveKnowledgeScope(body?.Project);
            if (!scope.IsQueryable) return KnowledgeScopeRefusal();

            var ingestion = KnowledgeHost.Ingestion;
            if (ingestion is null) return KnowledgeFailureResult(KnowledgeFailure.Disabled, "knowledge is not configured");

            // THE WORKSPACE FENCE, and it runs BEFORE anything is sent. Every requested path is
            // resolved through the colony's own containment check, which follows symlinks and
            // refuses an escape by throwing. FORAGER has its own allowed-roots fence on the far
            // side; this is the near one, and neither is trusted to be the only one.
            var paths = new List<string>();
            foreach (var requested in body?.Paths ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(requested)) continue;
                try
                {
                    var guard = new WorkspacePathGuard(AnthillRuntime.AllowedWorkspaceRoot, ToolRuntime.Live);
                    var safe = guard.ResolveSafePath(requested);
                    if (guard.IsBlockedPath(safe))
                        return ApiJson.Error($"Refused: '{requested}' is inside a blocked path.", "permission_denied");
                    paths.Add(safe);
                }
                catch (UnauthorizedAccessException error)
                {
                    return ApiJson.Error(
                        $"Refused: '{requested}' is outside the colony workspace. {error.Message}", "permission_denied");
                }
            }

            var result = await ingestion.StartIngestionAsync(new KnowledgeIngestionRequest
            {
                Scope = scope,
                Paths = paths,
                Force = body?.Force ?? false,
                RequestedBy = CurrentUsername(ctx),
            }, ctx.RequestAborted).ConfigureAwait(false);

            if (!result.Ok || result.Value is null) return KnowledgeFailureResult(result.Failure, result.Reason);
            return ApiJson.Ok(KnowledgeJobPayload(result.Value), "Ingestion queued.");
        });

        app.MapPost("/knowledge/jobs/{id}/cancel", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Manage); if (auth is not null) return auth;
            var scope = ResolveKnowledgeScope(ctx.Request.Query["project"].ToString());
            if (!scope.IsQueryable) return KnowledgeScopeRefusal();

            var ingestion = KnowledgeHost.Ingestion;
            if (ingestion is null) return KnowledgeFailureResult(KnowledgeFailure.Disabled, "knowledge is not configured");

            var result = await ingestion.CancelJobAsync(id, scope, ctx.RequestAborted).ConfigureAwait(false);
            if (!result.Ok || result.Value is null) return KnowledgeFailureResult(result.Failure, result.Reason);
            return ApiJson.Ok(KnowledgeJobPayload(result.Value), "Cancellation requested; completed work is kept.");
        });

        app.MapPost("/knowledge/jobs/{id}/retry", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Manage); if (auth is not null) return auth;
            var scope = ResolveKnowledgeScope(ctx.Request.Query["project"].ToString());
            if (!scope.IsQueryable) return KnowledgeScopeRefusal();

            var ingestion = KnowledgeHost.Ingestion;
            if (ingestion is null) return KnowledgeFailureResult(KnowledgeFailure.Disabled, "knowledge is not configured");

            var result = await ingestion.RetryJobAsync(id, scope, ctx.RequestAborted).ConfigureAwait(false);
            if (!result.Ok || result.Value is null) return KnowledgeFailureResult(result.Failure, result.Reason);
            return ApiJson.Ok(KnowledgeJobPayload(result.Value), "Retrying from the last checkpoint.");
        });
    }

    /// <summary>
    /// Turn the caller's requested ANTHILL project into a knowledge scope.
    ///
    /// The caller names an ANTHILL project — never a FORAGER one — and the operator's configured map
    /// does the translation. That indirection IS the containment: a request cannot reach a knowledge
    /// base the operator has not deliberately mapped, whatever it puts in the query string.
    ///
    /// An empty project falls back to <c>knowledge_default_project</c>, which is correct HERE and
    /// would be wrong for a mission: a console operator asking a direct question has no project
    /// context to speak of, while a mission that fell back to a default would be reading a knowledge
    /// base that is not its own.
    /// </summary>
    private static KnowledgeScope ResolveKnowledgeScope(string? anthillProjectId)
    {
        var settings = AnthillRuntime.Knowledge;
        if (!settings.Enabled) return KnowledgeScope.Unresolved;

        if (!string.IsNullOrWhiteSpace(anthillProjectId))
        {
            var mapped = settings.ProjectRefFor(anthillProjectId);
            return mapped is null
                ? KnowledgeScope.Unresolved
                : KnowledgeScope.ForProject(mapped, anthillProjectId);
        }

        return settings.DefaultProject.Length > 0
            ? KnowledgeScope.ForProject(settings.DefaultProject)
            : KnowledgeScope.Unresolved;
    }

    /// <summary>
    /// The refusal for an unresolvable scope. Names the configuration key, because the only person
    /// who can fix this is an operator and "no scope" tells them nothing.
    /// </summary>
    private static IResult KnowledgeScopeRefusal() =>
        ApiJson.Error(
            "No knowledge base is mapped for this project. Map it in knowledge_project_map, or set "
          + "knowledge_default_project, before knowledge can be retrieved.", "not_found");

    /// <summary>
    /// A provider failure as an HTTP answer. The status codes matter to the console: unavailable and
    /// disabled render as an explanation rather than an error, and both are distinguishable from a
    /// genuine 404.
    /// </summary>
    private static IResult KnowledgeFailureResult(KnowledgeFailure failure, string? reason)
    {
        var message = reason ?? "The knowledge service did not answer.";
        return failure switch
        {
            KnowledgeFailure.NotFound => ApiJson.Error(message, "not_found"),
            KnowledgeFailure.Unauthorized => ApiJson.Error(message, "permission_denied"),
            KnowledgeFailure.Invalid => ApiJson.Error(message, "bad_request"),
            KnowledgeFailure.ScopeUnresolved => KnowledgeScopeRefusal(),
            _ => ApiJson.Error(message, "bad_request"),
        };
    }

    private static Dictionary<string, object?> KnowledgeContextPayload(KnowledgeContext context) => new()
    {
        ["query"] = context.Metadata.Query,
        ["scope"] = context.Metadata.Scope.ToString(),
        ["backend"] = context.Metadata.Backend,
        ["took_ms"] = context.Metadata.ElapsedMs,
        ["truncated"] = context.Metadata.Truncated,
        ["degradation"] = context.Metadata.Degradation,
        ["open_conflicts"] = context.Metadata.OpenConflictCount,
        ["facts"] = context.Facts.Select(f => new Dictionary<string, object?>
        {
            ["knowledge_id"] = f.KnowledgeId,
            ["statement"] = f.Statement,
            ["type"] = f.Type,
            ["support"] = f.Support.ToString(),
            ["status"] = f.Status.ToString(),
            ["confidence"] = f.Confidence,
            ["effective_date"] = f.EffectiveDate,
            ["evidence_ids"] = f.EvidenceIds,
            ["conflict_ids"] = f.ConflictIds,
            ["has_provenance"] = f.HasProvenance,
            ["contested"] = f.IsContested,
        }).ToList(),
        ["evidence"] = context.Evidence.Select(KnowledgeEvidencePayload).ToList(),
        ["entities"] = context.Entities.Select(e => new Dictionary<string, object?>
        {
            ["entity_id"] = e.EntityId, ["name"] = e.Name, ["type"] = e.Type, ["aliases"] = e.Aliases,
        }).ToList(),
        ["conflicts"] = context.Conflicts.Select(KnowledgeConflictPayload).ToList(),

        // The rendered form, verbatim — the same text a model is given. The console shows it so an
        // operator can see exactly what the colony was told, which is the difference between a
        // knowledge feature you can audit and one you have to trust.
        ["rendered"] = context.Render(),
    };

    private static Dictionary<string, object?> KnowledgeEvidencePayload(KnowledgeEvidence evidence) => new()
    {
        ["evidence_id"] = evidence.EvidenceId,
        ["knowledge_id"] = evidence.KnowledgeId,
        ["source_id"] = evidence.SourceId,
        ["source_name"] = evidence.SourceName,
        ["source_type"] = evidence.SourceType,
        ["location"] = evidence.Location,
        ["chunk_id"] = evidence.ChunkId,
        ["excerpt"] = evidence.Excerpt,
        ["excerpt_hash"] = evidence.ExcerptHash,
        ["extractor"] = evidence.Extractor,
        ["model"] = evidence.Model,
        ["confidence"] = evidence.Confidence,
        ["missing_excerpt"] = evidence.MissingExcerpt,
    };

    private static Dictionary<string, object?> KnowledgeConflictPayload(KnowledgeConflict conflict) => new()
    {
        ["conflict_id"] = conflict.ConflictId,
        ["type"] = conflict.Type,
        ["attribute_key"] = conflict.AttributeKey,
        ["status"] = conflict.Status,
        ["description"] = conflict.Description,
        ["knowledge_ids"] = conflict.KnowledgeIds,
        ["source_ids"] = conflict.SourceIds,
        ["suggested_resolution"] = conflict.SuggestedResolution,
        ["resolution"] = conflict.Resolution,
        ["open"] = conflict.IsOpen,
    };

    private static Dictionary<string, object?> KnowledgeJobPayload(KnowledgeJob job) => new()
    {
        ["job_id"] = job.JobId,
        ["status"] = job.Status,
        ["current_stage"] = job.CurrentStage,
        // Real persisted progress, derived by FORAGER from its own stage rows. Never interpolated,
        // never advanced by a timer on this side.
        ["progress"] = job.Progress,
        ["terminal"] = job.IsTerminal,
        ["started_at"] = job.StartedAtUtc,
        ["finished_at"] = job.FinishedAtUtc,
        ["error"] = job.Error,
        ["warnings"] = job.Warnings,
        ["stages"] = job.Stages.Select(s => new Dictionary<string, object?>
        {
            ["name"] = s.Name,
            ["status"] = s.Status,
            ["processed"] = s.Processed,
            ["skipped"] = s.Skipped,
            ["failed"] = s.Failed,
            ["warnings"] = s.Warnings,
        }).ToList(),
    };
}
