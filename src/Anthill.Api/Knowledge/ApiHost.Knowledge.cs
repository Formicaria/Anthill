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

    /// <summary>
    /// The settings key behind the console's knowledge toggle. Named once, here, and used by both
    /// the status route and <see cref="KnowledgeGateEnvVar"/> so the console cannot be reporting on
    /// one key while its button writes another.
    /// </summary>
    public const string KnowledgeGateKey = "knowledge_enabled";

    /// <summary>
    /// The environment variable that overrides <see cref="KnowledgeGateKey"/>, read from the config
    /// catalog rather than spelled again. Empty if the declaration ever stops declaring one — in
    /// which case nothing can be pinned and <see cref="KnowledgeGateEnvPinned"/> is false, which is
    /// the correct answer rather than a guess.
    /// </summary>
    public static string KnowledgeGateEnvVar =>
        ConfigCatalog.Find(KnowledgeGateKey)?.EnvOverride ?? "";

    /// <summary>
    /// True when that variable is set, so the file value cannot win. Read live: an operator's
    /// process environment does not change under it, but reading it live costs nothing and removes
    /// a cached-at-startup answer that would be wrong after a restart under a different unit file.
    /// </summary>
    public static bool KnowledgeGateEnvPinned =>
        KnowledgeGateEnvVar.Length > 0
     && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KnowledgeGateEnvVar));

    private sealed record KnowledgeRetrieveRequest(string? Query, string? Project, int? TopK, bool? IncludeHistorical);
    private sealed record KnowledgeIngestRequest(string? Project, string[]? Paths, bool? Force);

    /// <param name="Project">The ANTHILL project id to bind. Empty binds the DEFAULT instead, which
    /// is the scope a mission that names no project resolves to.</param>
    /// <param name="KnowledgeBase">The FORAGER project ref to bind it to. Empty UNBINDS.</param>
    private sealed record KnowledgeMapRequest(string? Project, string? KnowledgeBase);

    /// <param name="Project">The ANTHILL project whose bound knowledge base to study. Empty means
    /// the default binding, exactly as every other knowledge route reads it.</param>
    private sealed record KnowledgeSeedRequest(string? Project);

    /// <param name="Accept">True records agreement with the proposal; false records refusal.</param>
    /// <param name="Note">Optional, and worth writing: the next reader of this row is somebody
    /// deciding whether the colony's objections are usually right.</param>
    private sealed record KnowledgeReviewDecision(bool? Accept, string? Note);

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

                // v0.3.8.155 — AND IT IS RECORDED AS A PROPOSAL, not only as an event.
                //
                // An event is a thing that HAPPENED; a proposal is a thing that is WAITING. `.122`
                // put this in the event log and said so in as many words — "this is not the approval
                // pipeline… a typed proposal KIND is a core surface and deserves its own release" —
                // and until that release nothing could list what was outstanding or answer it. The
                // event stays: it is what the live stream shows and what makes the moment auditable.
                // The row is what an operator decides.
                queen.Memory.SaveKnowledgeReview(new Anthill.Core.Memory.SqliteMemory.KnowledgeReview
                {
                    Id = Guid.NewGuid().ToString(),
                    KnowledgeId = proposal.KnowledgeId,
                    ProjectRef = proposal.Scope.ProjectRef ?? "",
                    AnthillProjectId = proposal.Scope.AnthillProjectId,
                    Action = proposal.Action,
                    Rationale = proposal.Rationale,
                    MissionId = proposal.MissionId,
                });

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

                // v0.3.8.143 (A1) — who the producer says it is, from GET /api/capabilities.
                // Null on an engine that predates the capability response, which is tolerated;
                // `compatible` goes false only when a DECLARED version sits outside this build's
                // window, and `usable` above already folds that in.
                ["protocol_version"] = availability.ProtocolVersion,
                ["instance_id"] = availability.InstanceId,
                ["instance_generation"] = availability.InstanceGeneration,
                ["instance_mode"] = availability.InstanceMode,
                ["compatible"] = availability.Compatible,
                ["projects"] = AnthillRuntime.Knowledge.ProjectMap.Keys.ToList(),

                // ---- WHAT IS BOUND TO WHAT. v0.3.8.153 --------------------------------------
                //
                // `projects` above is the map's KEYS, which is all the scope selector ever needed:
                // pick a project, read its knowledge. It is not enough to ADMINISTER the map, and
                // that is why `.148` shipped `POST /knowledge/project-map` with no console control —
                // a panel cannot offer to rebind or unbind a binding it cannot display.
                //
                // The VALUES are FORAGER project refs, and they are the operator's own configuration
                // rather than anything FORAGER told us. This payload is already `read_knowledge`
                // gated (admin-only in the shipped role set) and already carries the endpoint, so it
                // reveals nothing the reader could not read from the config file beside it.
                ["project_map"] = AnthillRuntime.Knowledge.ProjectMap
                    .ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal),
                ["default_project"] = AnthillRuntime.Knowledge.DefaultProject,

                // v0.3.8.154 — how much of each bound base the colony has already studied, so the
                // console's button can say "12 already seeded" instead of offering an action whose
                // effect the operator has to guess at. Counted from the receipts, which is the same
                // record the seeding pass reads to decide what to skip: one source of truth, two
                // readers.
                ["seeded_counts"] = AnthillRuntime.Knowledge.ProjectMap
                    .ToDictionary(kv => kv.Key,
                                  kv => (object?)Queen.Memory.SeededSourceCount(kv.Value),
                                  StringComparer.Ordinal),

                // ---- What the console's on/off toggle needs to tell the truth. v0.3.8.124 -------
                //
                // `endpoint` above is the endpoint that was PROBED, and a disabled provider probes
                // nothing — `KnowledgeAvailability.Off` carries no endpoint at all. So with
                // knowledge off the console could describe the feature but could not say what it
                // was about to point at. This is the CONFIGURED value, reported whether or not a
                // request was made with it.
                ["configured_endpoint"] = AnthillRuntime.Knowledge.Endpoint,

                // Whether the file permits a non-loopback FORAGER. Reported because turning
                // knowledge on against a remote endpoint with this false produces a refusal at the
                // client — "refusing a non-loopback knowledge request" — and an operator who was
                // shown an Enable button deserves to know that before pressing it, not after.
                ["allow_remote"] = AnthillRuntime.Knowledge.AllowRemote,

                // v0.3.8.157 — the study schedule, so the console can draw the switch in the state
                // the colony is actually in. Projected the same way the runtime reads it: anything
                // unrecognised is `off`, so the page cannot show a schedule that is not running.
                ["auto_study"] = AnthillRuntime.KnowledgeAutoStudy,

                // AND WHETHER THE SWITCH IS PINNED BY THE ENVIRONMENT.
                //
                // `AnthillRuntime` projects `Enabled` as env-over-file, so on a colony that exports
                // ANTHILL_KNOWLEDGE_ENABLED a settings write would persist to config.json, be
                // re-projected, and lose to the variable — a toggle that appears to do nothing. The
                // console withholds the control in that case and names the variable instead.
                //
                // The variable's NAME is read out of the declaration rather than written here a
                // second time: two spellings of one environment variable is how the console ends up
                // reporting a pin that does not exist, or missing one that does.
                ["gate_env_var"] = KnowledgeGateEnvVar,
                ["gate_env_pinned"] = KnowledgeGateEnvPinned,
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

        // v0.3.8.148 — BIND AN ANTHILL PROJECT TO A FORAGER ONE, WITHOUT EDITING A FILE.
        //
        // The operator's colony showed the gap as a dead end: every Knowledge panel read
        // "No knowledge base is mapped for this project. Map it in knowledge_project_map, or set
        // knowledge_default_project" — a refusal naming a config key, from a UI with no way to set
        // it. Correct, and unactionable without a text editor and a restart.
        //
        // THE CONTRACT ALREADY REQUIRED THIS. `FORAGER_SHARED_CONTRACT.md` §3: "Project mapping is an
        // authorized server operation. Bind the Anthill project to the specific Forager instance and
        // project; enforce user/project membership on every read and mutation." An operation the
        // contract calls authorized and server-side was living in a hand-edited file, which is not a
        // weaker version of that — it is a different thing wearing its name.
        //
        // MANAGE, NOT READ, AND NEVER AN AGENT. This decides SCOPE, and scope is the one thing the
        // contract insists an agent may never choose: "Agent-selected arguments cannot choose
        // arbitrary Forager project IDs". It is an operator action reached through the authenticated
        // API and it is deliberately not a tool — no role's `AllowedTools` names it and none should.
        //
        // IT WIDENS NOTHING BY ITSELF. Binding a project only makes retrieval RESOLVABLE; every read
        // still goes through `ResolveKnowledgeScope` and the provider's own `RequireScope`, and an
        // unmapped project still refuses rather than falling back. What changes is that an operator
        // can answer the refusal the UI has been showing them.
        app.MapPost("/knowledge/project-map", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Manage); if (auth is not null) return auth;

            KnowledgeMapRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<KnowledgeMapRequest>().ConfigureAwait(false); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }

            var project = (body?.Project ?? "").Trim();
            var knowledgeBase = (body?.KnowledgeBase ?? "").Trim();

            // AN EMPTY KNOWLEDGE BASE UNBINDS, and unbinding is a real operation rather than an
            // error: an operator who mapped the wrong project must be able to say so, and the
            // honest end state of that is an unmapped project that refuses — never a silent
            // fallback to whatever was there before.
            if (project.Length == 0)
            {
                AnthillRuntime.Config.KnowledgeDefaultProject = knowledgeBase;
            }
            else if (knowledgeBase.Length == 0)
            {
                AnthillRuntime.Config.KnowledgeProjectMap.Remove(project);
            }
            else
            {
                AnthillRuntime.Config.KnowledgeProjectMap[project] = knowledgeBase;
            }

            // PERSISTED IMMEDIATELY. `KnowledgeOptions` re-reads the runtime per call, so the next
            // retrieval sees this without a restart — and writing the file in the same breath means
            // a mapping an operator made cannot survive only until the process ends, which is how a
            // setting comes to disagree with the file that is supposed to define it.
            AnthillRuntime.SaveConfig();

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["project"] = project,
                ["knowledge_base"] = knowledgeBase,
                ["bound"] = knowledgeBase.Length > 0,
                ["project_map"] = AnthillRuntime.Config.KnowledgeProjectMap,
                ["default_project"] = AnthillRuntime.Config.KnowledgeDefaultProject,
            }, knowledgeBase.Length > 0
                ? (project.Length == 0
                    ? $"Default knowledge base set to '{knowledgeBase}'."
                    : $"Project '{project}' is now mapped to knowledge base '{knowledgeBase}'.")
                : (project.Length == 0
                    ? "Default knowledge base cleared."
                    : $"Project '{project}' is no longer mapped to a knowledge base."));
        });

        // v0.3.8.155 — THE PROPOSALS AN OPERATOR HAS TO ANSWER.
        //
        // `knowledge_review` has existed since `.121`, been described, argued for, and reachable by
        // nobody: no role's contract named it, and the proposal it raises reached the event log and
        // stopped. These two routes and the researcher's grant are the missing first and last layers.
        app.MapGet("/knowledge/reviews", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Read); if (auth is not null) return auth;

            var status = ctx.Request.Query["status"].ToString();
            var reviews = Queen.Memory.KnowledgeReviews(
                string.IsNullOrWhiteSpace(status) ? null : status, 100);

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["reviews"] = reviews.Select(r => new Dictionary<string, object?>
                {
                    ["id"] = r.Id,
                    ["knowledge_id"] = r.KnowledgeId,
                    ["project_ref"] = r.ProjectRef,
                    ["action"] = r.Action,
                    ["rationale"] = r.Rationale,
                    ["mission_id"] = r.MissionId,
                    ["proposed_by"] = r.ProposedBy,
                    ["status"] = r.Status,
                    ["decided_by"] = r.DecidedBy,
                    ["decision_note"] = r.DecisionNote,
                    ["proposed_at"] = r.ProposedAt,
                    ["decided_at"] = r.DecidedAt,
                }).ToList(),
                ["pending"] = reviews.Count(r => r.Status == "pending"),
            });
        });

        // ACCEPTING RECORDS AGREEMENT; IT DOES NOT CHANGE A KNOWLEDGE BASE, and the response says so
        // rather than letting the word "accepted" imply an edit. §1 gives FORAGER the classification
        // and the ranking, and there is no producer surface for applying a review — that is P13.
        // A status this build could never reach would be a promise in an enum.
        app.MapPost("/knowledge/reviews/{id}/decide", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Manage); if (auth is not null) return auth;

            KnowledgeReviewDecision? body;
            try { body = await ctx.Request.ReadFromJsonAsync<KnowledgeReviewDecision>().ConfigureAwait(false); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }

            var accept = body?.Accept ?? false;
            // `CurrentUsername` is nullable — an authenticated caller without a resolvable name is
            // possible, and "who decided" must still say something rather than nothing. The fallback
            // is a WORD, not an empty string: a blank decider reads as a record nobody made.
            var decided = Queen.Memory.DecideKnowledgeReview(
                id, accept, CurrentUsername(ctx) ?? "operator", body?.Note);
            if (decided is null)
                return ApiJson.Error(
                    "That proposal is unknown, or it has already been decided. A second decision on "
                  + "one proposal is not an update — the record already says what happened.", "not_found");

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["id"] = decided.Id,
                ["status"] = decided.Status,
                ["decided_by"] = decided.DecidedBy,
            }, accept
                ? "Recorded as accepted. This does not change the knowledge base — FORAGER publishes "
                + "no surface for applying a review yet (P13); the record is ANTHILL's, and says an "
                + "operator agreed with the objection."
                : "Recorded as declined. The knowledge item is unchanged.");
        });

        // v0.3.8.154 — RUN THE COLONY OVER A KNOWLEDGE BASE.
        //
        // The operator's own request: "I should be able to click on one knowledge base and have it
        // then run through the anthill automated missions to build its memory and pheromones."
        //
        // MANAGE, not Read: this queues real missions that spend real model calls. It is the same
        // permission that binds the project, and for the same reason — deciding that the colony
        // should go and work on a knowledge base is an operator action, never an agent's.
        //
        // What it builds, and the honest limits of that, are stated on `KnowledgeSeeder` and
        // repeated to the operator in the console rather than left as a word that sounds bigger
        // than the runtime.
        app.MapPost("/knowledge/seed", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Manage); if (auth is not null) return auth;

            KnowledgeSeedRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<KnowledgeSeedRequest>().ConfigureAwait(false); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }

            var project = (body?.Project ?? "").Trim();
            var scope = ResolveKnowledgeScope(project);
            if (!scope.IsQueryable) return KnowledgeScopeRefusal();

            var result = await Anthill.Api.Knowledge.KnowledgeSeeder.SeedOnce(
                Queen.Memory, Jobs, scope, project.Length == 0 ? null : project,
                ctx.RequestAborted).ConfigureAwait(false);

            if (!result.Ok) return ApiJson.Error(result.Message, "bad_request");

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["submitted"] = result.Submitted,
                ["already_seeded"] = result.AlreadySeeded,
                ["available"] = result.Available,
                ["job_ids"] = result.JobIds,
            }, result.Message);
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
    /// <summary>
    /// v0.3.8.156 — the same scope resolution, for the background study pass.
    ///
    /// A WRAPPER RATHER THAN A SECOND RESOLVER, and rather than widening the real one to public. The
    /// timer must resolve a project exactly as a request does — the map is the containment, and a
    /// scheduled path that resolved scope its own way would be the second implementation this file
    /// spends most of its comments refusing.
    /// </summary>
    internal static KnowledgeScope ResolveScopeForStudy(string? anthillProjectId) =>
        ResolveKnowledgeScope(anthillProjectId);

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
            // v0.3.8.153 — IT NAMES THE PLACE, NOT ONLY THE KEY.
            //
            // This sentence was correct and unactionable for five releases: it named two config
            // keys to an operator looking at a browser, and the panel showing it had no control to
            // set either. `.148` built the route; this release built the control, so the refusal
            // finally points somewhere a reader can go. The keys stay named — a scripted caller
            // reading this over HTTP has no Knowledge tab — but they are no longer the only answer.
            "No knowledge base is mapped for this project. Map it on the Knowledge page, under "
          + "\"Knowledge bases\" — or set knowledge_project_map / knowledge_default_project in the "
          + "config file. Knowledge cannot be retrieved until it is bound.", "not_found");

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
