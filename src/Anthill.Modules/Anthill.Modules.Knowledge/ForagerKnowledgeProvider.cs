using System.Diagnostics;
using Anthill.SDK.Knowledge;

namespace Anthill.Modules.Knowledge;

/// <summary>
/// The live FORAGER integration: retrieval, evidence expansion, conflict surfacing, and ingestion
/// control.
///
/// THE RETRIEVAL IS EVIDENCE-FIRST, and that phrase means something specific here. A similarity-first
/// pipeline ranks chunks and pastes the winners into a prompt; what the model receives is text with
/// no accountability, and the model's only way to judge it is plausibility. This one ranks
/// CANDIDATES, then fetches what supports each one, then attaches the disagreements, and presents
/// every statement with its support level and its provenance attached. The model is told what kind
/// of thing it is being given.
///
/// The ordering is the design:
///
///     query -> normalize -> rank candidates -> filter by scope and support
///           -> fetch evidence -> expand entities -> attach conflicts -> assemble -> budget
///
/// Evidence comes before assembly because an item whose evidence cannot be resolved must be LABELLED
/// rather than dropped; conflicts come before assembly because a contested fact must never be
/// rendered as settled. Both would be impossible to do honestly if assembly ran first.
/// </summary>
/// <remarks>
/// NOT <c>IDisposable</c>, deliberately. It is handed a <see cref="ForagerClient"/> it did not
/// create, and a type that disposes what it does not own produces exactly the bug where one holder
/// closes a shared <c>HttpClient</c> out from under another. <see cref="KnowledgeModule"/> owns the
/// client's lifetime because it constructed it.
/// </remarks>
internal sealed class ForagerKnowledgeProvider : IKnowledgeProvider, IKnowledgeIngestionProvider
{
    private readonly ForagerClient _client;
    private readonly KnowledgeOptionsSource _options;
    private readonly KnowledgeCache _cache;

    public ForagerKnowledgeProvider(KnowledgeOptionsSource options, ForagerClient client, KnowledgeCache cache)
    {
        _options = options;
        _client = client;
        _cache = cache;
    }

    public string Name => "forager-http";

    // ---------------------------------------------------------------- availability

    public async Task<KnowledgeAvailability> ProbeAsync(CancellationToken cancellationToken)
    {
        var options = _options();
        var unusable = options.Unusable();
        if (unusable is not null) return KnowledgeAvailability.Off(unusable);

        var ready = await _client.GetAsync<ForagerHealth>("ready", options.ProbeTimeoutMs, cancellationToken)
            .ConfigureAwait(false);

        if (!ready.Ok || ready.Value is null)
        {
            return new KnowledgeAvailability
            {
                Enabled = true,
                Reachable = false,
                Endpoint = options.Endpoint,
                Reason = ready.Reason ?? "the knowledge service did not report readiness",
            };
        }

        var health = ready.Value;
        return new KnowledgeAvailability
        {
            Enabled = true,

            // FORAGER's /ready returns 503 with a body when it is up but not usable — migrations
            // pending, data directory missing. The client already turned that into a failure, so
            // reaching here means status was 2xx; we still read the field rather than assuming,
            // because "reachable" and "ready" are different claims and we are making the second.
            Reachable = string.Equals(health.Status, "ok", StringComparison.OrdinalIgnoreCase),
            Version = health.Version,
            SchemaVersion = health.SchemaVersion,
            SearchBackend = health.SearchBackend,
            ModelProvider = health.ModelProvider,
            Endpoint = options.Endpoint,
            Reason = string.Equals(health.Status, "ok", StringComparison.OrdinalIgnoreCase)
                ? null
                : $"the knowledge service reported status '{health.Status}'",
        };
    }

    // ---------------------------------------------------------------- search

    public async Task<KnowledgeOutcome<KnowledgeSearchResult>> SearchAsync(
        KnowledgeSearchRequest request, CancellationToken cancellationToken)
    {
        var scoped = RequireScope<KnowledgeSearchResult>(request.Scope);
        if (scoped is not null) return scoped;

        var query = Normalize(request.Query);
        if (query.Length == 0)
            return KnowledgeOutcome<KnowledgeSearchResult>.Failed(
                KnowledgeFailure.Invalid, "a knowledge search needs a non-empty query");

        var options = _options();
        var clock = Stopwatch.StartNew();

        var limit = Clamp(request.Limit, 1, 50);
        var path = $"projects/{ForagerClient.Segment(request.Scope.ProjectRef!)}/search"
                 + $"?q={ForagerClient.Query(query)}&limit={limit}&include_entities=true&include_chunks=false";

        var response = await _client.GetAsync<ForagerSearchResponse>(path, options.RetrievalTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if (!response.Ok || response.Value is null) return Propagate<ForagerSearchResponse, KnowledgeSearchResult>(response);

        var hits = new List<KnowledgeSearchHit>();
        foreach (var hit in response.Value.Knowledge ?? new List<ForagerSearchHit>())
        {
            var item = hit.Item;
            if (item?.Id is null) continue;
            if (!Admissible(item, request.Scope, request.MinimumSupport, request.IncludeHistorical)) continue;
            if (request.Types.Count > 0 &&
                !request.Types.Contains(item.Type ?? "", StringComparer.OrdinalIgnoreCase)) continue;

            hits.Add(new KnowledgeSearchHit
            {
                KnowledgeId = item.Id,
                Statement = item.Statement ?? item.Title ?? "",
                Title = item.Title,
                Type = item.Type ?? "fact",
                Support = ForagerMapping.Support(item.Support),
                Status = ForagerMapping.Status(item.Status),
                Confidence = item.Confidence,
                Score = hit.Score,
                Snippet = hit.Snippet,
                Why = hit.Why,
                EvidenceCount = item.EvidenceCount,
                IsContested = string.Equals(item.Status, "disputed", StringComparison.OrdinalIgnoreCase),
            });
        }

        return KnowledgeOutcome<KnowledgeSearchResult>.Success(new KnowledgeSearchResult
        {
            Hits = hits,
            Entities = (response.Value.Entities ?? new List<ForagerEntity>())
                .Select(ForagerMapping.ToEntity).ToList(),
            Metadata = new RetrievalMetadata
            {
                Query = query,
                Scope = request.Scope,
                Backend = response.Value.Backend,
                FactCount = hits.Count,
                CandidatesConsidered = response.Value.Knowledge?.Count ?? 0,
                ElapsedMs = clock.ElapsedMilliseconds,
            },
        });
    }

    // ---------------------------------------------------------------- retrieval

    public async Task<KnowledgeOutcome<KnowledgeContext>> RetrieveAsync(
        KnowledgeRetrievalRequest request, CancellationToken cancellationToken)
    {
        var scoped = RequireScope<KnowledgeContext>(request.Scope);
        if (scoped is not null) return scoped;

        var query = Normalize(request.Query);
        if (query.Length == 0)
            return KnowledgeOutcome<KnowledgeContext>.Failed(
                KnowledgeFailure.Invalid, "a knowledge retrieval needs a non-empty query");

        var options = _options();
        var cacheKey = $"retrieve|{request.Scope.CacheKey}|{query}|{request.TopK}|{request.IncludeHistorical}"
                     + $"|{request.MinimumSupport}|{request.IncludeEvidence}|{request.IncludeEntities}";
        if (_cache.TryGet<KnowledgeContext>(cacheKey, out var cached) && cached is not null)
            return KnowledgeOutcome<KnowledgeContext>.Success(cached);

        var clock = Stopwatch.StartNew();

        // Stage 1 — candidates. Over-fetch relative to TopK so that scope, support and status
        // filtering has something to discard without silently shrinking the answer below what was
        // asked for. Bounded, because this is FORAGER's work not ours.
        var searched = await SearchAsync(new KnowledgeSearchRequest
        {
            Query = query,
            Scope = request.Scope,
            Limit = Clamp(request.TopK * 3, 5, 50),
            MinimumSupport = request.MinimumSupport,
            IncludeHistorical = request.IncludeHistorical,
        }, cancellationToken).ConfigureAwait(false);

        if (!searched.Ok || searched.Value is null) return Propagate<KnowledgeSearchResult, KnowledgeContext>(searched);

        var candidates = searched.Value.Hits.Take(Clamp(request.TopK, 1, 50)).ToList();
        if (candidates.Count == 0)
        {
            var empty = KnowledgeContext.Empty(query, request.Scope) with
            {
                Metadata = new RetrievalMetadata
                {
                    Query = query,
                    Scope = request.Scope,
                    Backend = searched.Value.Metadata.Backend,
                    CandidatesConsidered = searched.Value.Metadata.CandidatesConsidered,
                    ElapsedMs = clock.ElapsedMilliseconds,
                },
            };
            _cache.Set(cacheKey, empty, options.CacheSeconds);
            return KnowledgeOutcome<KnowledgeContext>.Success(empty);
        }

        // Stage 2 — the item detail, which is where evidence, entities and conflict ids live.
        // One call per candidate. FORAGER has no batch endpoint; TopK is small and bounded, and
        // inventing a batch endpoint on its side for this would be a FORAGER change made for
        // ANTHILL's convenience rather than for FORAGER's own users.
        var facts = new List<KnowledgeFact>();
        var evidence = new List<KnowledgeEvidence>();
        var entities = new Dictionary<string, KnowledgeEntity>(StringComparer.Ordinal);
        var conflictIds = new HashSet<string>(StringComparer.Ordinal);
        var degradations = new List<string>();

        foreach (var candidate in candidates)
        {
            var detail = await _client.GetAsync<ForagerKnowledgeItem>(
                $"knowledge/{ForagerClient.Segment(candidate.KnowledgeId)}",
                options.RetrievalTimeoutMs, cancellationToken).ConfigureAwait(false);

            if (!detail.Ok || detail.Value is null)
            {
                // A candidate we cannot expand is RECORDED, not silently dropped. The hit is real;
                // our view of it is incomplete, and a context that quietly loses facts is one whose
                // emptiness cannot be trusted.
                degradations.Add($"could not expand {candidate.KnowledgeId}");
                continue;
            }

            var item = detail.Value;

            // Re-checked on the detail record, not merely on the search hit. The two come from
            // different endpoints, and the confidentiality band is the one field where trusting the
            // cheaper source would be a leak rather than an inconsistency.
            if (!Admissible(item, request.Scope, request.MinimumSupport, request.IncludeHistorical)) continue;

            var itemEvidence = new List<KnowledgeEvidence>();
            if (request.IncludeEvidence)
            {
                foreach (var wire in item.Evidence ?? new List<ForagerEvidence>())
                {
                    if (wire.Id is null) continue;
                    itemEvidence.Add(ForagerMapping.ToEvidence(wire));
                }
            }

            evidence.AddRange(itemEvidence);
            facts.Add(ForagerMapping.ToFact(item, itemEvidence.Select(e => e.EvidenceId).ToList()));

            foreach (var id in item.ConflictIds ?? new List<string>()) conflictIds.Add(id);

            if (request.IncludeEntities)
            {
                foreach (var reference in item.Entities ?? new List<ForagerEntityRef>())
                {
                    if (reference.Id is null || entities.ContainsKey(reference.Id)) continue;
                    entities[reference.Id] = new KnowledgeEntity
                    {
                        EntityId = reference.Id,
                        Name = reference.CanonicalName ?? reference.Id,
                        Type = reference.Type ?? "unknown",
                    };
                }
            }
        }

        // Stage 3 — conflicts. Rule 10: never hidden, and never resolved here. Fetched for the whole
        // scope and then narrowed to the facts in hand, which is one call instead of N and also
        // catches a conflict whose OTHER side did not rank into this query — precisely the case
        // where silence would be most misleading.
        var conflicts = new List<KnowledgeConflict>();
        if (request.IncludeConflicts && conflictIds.Count > 0)
        {
            var all = await GetConflictsAsync(request.Scope, cancellationToken).ConfigureAwait(false);
            if (all.Ok && all.Value is not null)
                conflicts.AddRange(all.Value.Where(c => conflictIds.Contains(c.ConflictId)));
            else
                degradations.Add("conflicts could not be fetched, so contested statements may be unmarked");
        }

        // Stage 4 — deterministic order. Best-supported first, then most confident, then id as the
        // tiebreak so the render is byte-stable across runs.
        facts = facts
            .OrderBy(f => (int)f.Support)
            .ThenByDescending(f => f.Confidence)
            .ThenBy(f => f.KnowledgeId, StringComparer.Ordinal)
            .ToList();

        // Stage 5 — budget. Whole facts are dropped from the TAIL, never truncated mid-statement:
        // a half-quoted excerpt is a misquotation, and this pipeline's whole claim is that quotes
        // are real. Truncation is then declared in the metadata and in the render.
        var truncated = false;
        var budget = Clamp(request.MaxContextChars, 1000, 200_000);
        while (facts.Count > 1 && EstimateSize(facts, evidence) > budget)
        {
            var dropped = facts[^1];
            facts.RemoveAt(facts.Count - 1);
            evidence.RemoveAll(e => e.KnowledgeId == dropped.KnowledgeId);
            truncated = true;
        }

        var keptIds = facts.Select(f => f.KnowledgeId).ToHashSet(StringComparer.Ordinal);
        evidence = evidence.Where(e => keptIds.Contains(e.KnowledgeId)).ToList();
        conflicts = conflicts.Where(c => c.KnowledgeIds.Any(keptIds.Contains)).ToList();

        var context = new KnowledgeContext
        {
            Facts = facts,
            Evidence = evidence,
            Entities = entities.Values.OrderBy(e => e.Name, StringComparer.Ordinal).ToList(),
            Conflicts = conflicts.OrderBy(c => c.ConflictId, StringComparer.Ordinal).ToList(),
            Metadata = new RetrievalMetadata
            {
                Query = query,
                Scope = request.Scope,
                Backend = searched.Value.Metadata.Backend,
                FactCount = facts.Count,
                EvidenceCount = evidence.Count,
                ConflictCount = conflicts.Count,
                OpenConflictCount = conflicts.Count(c => c.IsOpen),
                CandidatesConsidered = searched.Value.Metadata.CandidatesConsidered,
                ElapsedMs = clock.ElapsedMilliseconds,
                Truncated = truncated,
                Degradation = degradations.Count == 0 ? null : string.Join("; ", degradations),
            },
        };

        _cache.Set(cacheKey, context, options.CacheSeconds);
        return KnowledgeOutcome<KnowledgeContext>.Success(context);
    }

    // ---------------------------------------------------------------- item lookups

    public async Task<KnowledgeOutcome<KnowledgeFact>> GetAsync(
        string knowledgeId, KnowledgeScope scope, CancellationToken cancellationToken)
    {
        var scoped = RequireScope<KnowledgeFact>(scope);
        if (scoped is not null) return scoped;
        if (string.IsNullOrWhiteSpace(knowledgeId))
            return KnowledgeOutcome<KnowledgeFact>.Failed(KnowledgeFailure.Invalid, "a knowledge id is required");

        var result = await _client.GetAsync<ForagerKnowledgeItem>(
            $"knowledge/{ForagerClient.Segment(knowledgeId)}", _options().RetrievalTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Ok || result.Value is null) return Propagate<ForagerKnowledgeItem, KnowledgeFact>(result);

        // THE CROSS-PROJECT GUARD, and the reason it is here rather than trusted from the URL:
        // /api/knowledge/:id is NOT project-scoped on FORAGER's side. An id belonging to another
        // project resolves perfectly well. Rule 12 therefore has to be enforced on the response, and
        // the honest answer for an out-of-scope id is NotFound — telling a caller that an id exists
        // in a project it cannot see is itself a disclosure.
        if (!string.Equals(result.Value.ProjectId, scope.ProjectRef, StringComparison.Ordinal))
            return KnowledgeOutcome<KnowledgeFact>.Failed(KnowledgeFailure.NotFound,
                $"no knowledge item '{knowledgeId}' exists in {scope}");

        if (!ConfidentialityAllows(result.Value, scope))
            return KnowledgeOutcome<KnowledgeFact>.Failed(KnowledgeFailure.NotFound,
                $"no knowledge item '{knowledgeId}' exists in {scope}");

        var evidenceIds = (result.Value.Evidence ?? new List<ForagerEvidence>())
            .Select(e => e.Id ?? "").Where(id => id.Length > 0).ToList();
        return KnowledgeOutcome<KnowledgeFact>.Success(ForagerMapping.ToFact(result.Value, evidenceIds));
    }

    public async Task<KnowledgeOutcome<IReadOnlyList<KnowledgeEvidence>>> GetEvidenceAsync(
        string knowledgeId, KnowledgeScope scope, CancellationToken cancellationToken)
    {
        // Routed through GetAsync FIRST, for the scope check. Asking FORAGER for evidence directly
        // would answer for an item in any project, so the containment has to happen on the item.
        var owner = await GetAsync(knowledgeId, scope, cancellationToken).ConfigureAwait(false);
        if (!owner.Ok) return Propagate<KnowledgeFact, IReadOnlyList<KnowledgeEvidence>>(owner);

        var result = await _client.GetAsync<ForagerPage<ForagerEvidence>>(
            $"knowledge/{ForagerClient.Segment(knowledgeId)}/evidence",
            _options().RetrievalTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (!result.Ok || result.Value is null)
            return Propagate<ForagerPage<ForagerEvidence>, IReadOnlyList<KnowledgeEvidence>>(result);

        return KnowledgeOutcome<IReadOnlyList<KnowledgeEvidence>>.Success(
            (result.Value.Items ?? new List<ForagerEvidence>()).Select(ForagerMapping.ToEvidence).ToList());
    }

    public async Task<KnowledgeOutcome<IReadOnlyList<KnowledgeEntity>>> GetRelatedEntitiesAsync(
        string knowledgeId, KnowledgeScope scope, CancellationToken cancellationToken)
    {
        var owner = await GetAsync(knowledgeId, scope, cancellationToken).ConfigureAwait(false);
        if (!owner.Ok) return Propagate<KnowledgeFact, IReadOnlyList<KnowledgeEntity>>(owner);

        var entities = new List<KnowledgeEntity>();
        foreach (var id in owner.Value!.EntityIds)
        {
            var one = await _client.GetAsync<ForagerEntity>(
                $"entities/{ForagerClient.Segment(id)}", _options().RetrievalTimeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (one.Ok && one.Value is not null) entities.Add(ForagerMapping.ToEntity(one.Value));
        }
        return KnowledgeOutcome<IReadOnlyList<KnowledgeEntity>>.Success(entities);
    }

    public async Task<KnowledgeOutcome<IReadOnlyList<KnowledgeEntity>>> FindEntitiesAsync(
        string name, KnowledgeScope scope, CancellationToken cancellationToken)
    {
        var scoped = RequireScope<IReadOnlyList<KnowledgeEntity>>(scope);
        if (scoped is not null) return scoped;
        if (string.IsNullOrWhiteSpace(name))
            return KnowledgeOutcome<IReadOnlyList<KnowledgeEntity>>.Failed(
                KnowledgeFailure.Invalid, "an entity name is required");

        var result = await _client.GetAsync<ForagerPage<ForagerEntity>>(
            $"projects/{ForagerClient.Segment(scope.ProjectRef!)}/entities?q={ForagerClient.Query(Normalize(name))}&page_size=25",
            _options().RetrievalTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (!result.Ok || result.Value is null)
            return Propagate<ForagerPage<ForagerEntity>, IReadOnlyList<KnowledgeEntity>>(result);

        return KnowledgeOutcome<IReadOnlyList<KnowledgeEntity>>.Success(
            (result.Value.Items ?? new List<ForagerEntity>()).Select(ForagerMapping.ToEntity).ToList());
    }

    public async Task<KnowledgeOutcome<IReadOnlyList<KnowledgeConflict>>> GetConflictsAsync(
        KnowledgeScope scope, CancellationToken cancellationToken)
    {
        var scoped = RequireScope<IReadOnlyList<KnowledgeConflict>>(scope);
        if (scoped is not null) return scoped;

        var result = await _client.GetAsync<ForagerPage<ForagerConflict>>(
            $"projects/{ForagerClient.Segment(scope.ProjectRef!)}/conflicts?status=open&page_size=200",
            _options().RetrievalTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (!result.Ok || result.Value is null)
            return Propagate<ForagerPage<ForagerConflict>, IReadOnlyList<KnowledgeConflict>>(result);

        return KnowledgeOutcome<IReadOnlyList<KnowledgeConflict>>.Success(
            (result.Value.Items ?? new List<ForagerConflict>()).Select(ForagerMapping.ToConflict).ToList());
    }

    // ---------------------------------------------------------------- ingestion

    public async Task<KnowledgeOutcome<KnowledgeJob>> StartIngestionAsync(
        KnowledgeIngestionRequest request, CancellationToken cancellationToken)
    {
        var scoped = RequireScope<KnowledgeJob>(request.Scope);
        if (scoped is not null) return scoped;

        var options = _options();

        // Directory registration first, when paths were supplied. Every path here has ALREADY been
        // through the workspace guard on the ANTHILL side — this layer cannot check containment
        // because it does not know what a workspace is, and the contract says so out loud. FORAGER's
        // own FORAGER_ALLOWED_INPUT_ROOTS is the second, independent fence.
        foreach (var path in request.Paths)
        {
            var registered = await _client.PostAsync<ForagerJob>(
                $"projects/{ForagerClient.Segment(request.Scope.ProjectRef!)}/sources/directory",
                new { path, recursive = true }, options.IngestionTimeoutMs, cancellationToken).ConfigureAwait(false);

            // 403 here is FORAGER refusing a path outside its own allowed roots. Surfaced verbatim
            // rather than retried or rephrased: two fences disagreeing is exactly the condition an
            // operator needs to see.
            if (!registered.Ok && registered.Failure != KnowledgeFailure.NotFound)
                return Propagate<ForagerJob, KnowledgeJob>(registered);
        }

        var started = await _client.PostAsync<ForagerJob>(
            $"projects/{ForagerClient.Segment(request.Scope.ProjectRef!)}/process",
            new { force = request.Force }, options.IngestionTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (!started.Ok || started.Value is null) return Propagate<ForagerJob, KnowledgeJob>(started);

        // Anything the ingestion touched invalidates every cached read for this scope. Scoped
        // invalidation, not a global flush — another project's cache is not stale because this one
        // ingested.
        _cache.InvalidateScope(request.Scope);

        return KnowledgeOutcome<KnowledgeJob>.Success(ForagerMapping.ToJob(started.Value));
    }

    public async Task<KnowledgeOutcome<KnowledgeJob>> GetJobAsync(
        string jobId, KnowledgeScope scope, CancellationToken cancellationToken)
    {
        var scoped = RequireScope<KnowledgeJob>(scope);
        if (scoped is not null) return scoped;

        var result = await _client.GetAsync<ForagerJob>(
            $"jobs/{ForagerClient.Segment(jobId)}", _options().IngestionTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Ok || result.Value is null) return Propagate<ForagerJob, KnowledgeJob>(result);

        // Same containment argument as GetAsync: /api/jobs/:id is not project-scoped upstream.
        if (!string.Equals(result.Value.ProjectId, scope.ProjectRef, StringComparison.Ordinal))
            return KnowledgeOutcome<KnowledgeJob>.Failed(KnowledgeFailure.NotFound,
                $"no ingestion job '{jobId}' exists in {scope}");

        return KnowledgeOutcome<KnowledgeJob>.Success(ForagerMapping.ToJob(result.Value));
    }

    public async Task<KnowledgeOutcome<IReadOnlyList<KnowledgeJob>>> ListJobsAsync(
        KnowledgeScope scope, int limit, CancellationToken cancellationToken)
    {
        var scoped = RequireScope<IReadOnlyList<KnowledgeJob>>(scope);
        if (scoped is not null) return scoped;

        var result = await _client.GetAsync<ForagerPage<ForagerJob>>(
            $"projects/{ForagerClient.Segment(scope.ProjectRef!)}/jobs?page_size={Clamp(limit, 1, 100)}",
            _options().IngestionTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (!result.Ok || result.Value is null)
            return Propagate<ForagerPage<ForagerJob>, IReadOnlyList<KnowledgeJob>>(result);

        return KnowledgeOutcome<IReadOnlyList<KnowledgeJob>>.Success(
            (result.Value.Items ?? new List<ForagerJob>()).Select(ForagerMapping.ToJob).ToList());
    }

    public async Task<KnowledgeOutcome<KnowledgeJob>> CancelJobAsync(
        string jobId, KnowledgeScope scope, CancellationToken cancellationToken)
        => await JobActionAsync(jobId, scope, "cancel", cancellationToken).ConfigureAwait(false);

    public async Task<KnowledgeOutcome<KnowledgeJob>> RetryJobAsync(
        string jobId, KnowledgeScope scope, CancellationToken cancellationToken)
        => await JobActionAsync(jobId, scope, "retry", cancellationToken).ConfigureAwait(false);

    private async Task<KnowledgeOutcome<KnowledgeJob>> JobActionAsync(
        string jobId, KnowledgeScope scope, string action, CancellationToken cancellationToken)
    {
        // Ownership is established BEFORE the mutation, not after. Cancelling another project's job
        // and then discovering it was not ours is not a check, it is an apology.
        var owned = await GetJobAsync(jobId, scope, cancellationToken).ConfigureAwait(false);
        if (!owned.Ok) return owned;

        var result = await _client.PostAsync<ForagerJob>(
            $"jobs/{ForagerClient.Segment(jobId)}/{action}", null, _options().IngestionTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Ok || result.Value is null) return Propagate<ForagerJob, KnowledgeJob>(result);

        _cache.InvalidateScope(scope);
        return KnowledgeOutcome<KnowledgeJob>.Success(ForagerMapping.ToJob(result.Value));
    }

    public async Task<KnowledgeOutcome<IReadOnlyList<KnowledgeSource>>> ListSourcesAsync(
        KnowledgeScope scope, CancellationToken cancellationToken)
    {
        var scoped = RequireScope<IReadOnlyList<KnowledgeSource>>(scope);
        if (scoped is not null) return scoped;

        var result = await _client.GetAsync<ForagerPage<ForagerSource>>(
            $"projects/{ForagerClient.Segment(scope.ProjectRef!)}/sources?page_size=200",
            _options().RetrievalTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (!result.Ok || result.Value is null)
            return Propagate<ForagerPage<ForagerSource>, IReadOnlyList<KnowledgeSource>>(result);

        return KnowledgeOutcome<IReadOnlyList<KnowledgeSource>>.Success(
            (result.Value.Items ?? new List<ForagerSource>()).Select(ForagerMapping.ToSource).ToList());
    }

    // ---------------------------------------------------------------- shared

    /// <summary>
    /// The scope gate, applied at the top of every public method without exception.
    ///
    /// Returns null when the scope is usable and a typed refusal when it is not. An unresolvable
    /// scope NEVER widens to a default — that would be the cross-project leak Rule 12 exists to
    /// prevent, arriving as a convenience.
    /// </summary>
    private static KnowledgeOutcome<T>? RequireScope<T>(KnowledgeScope scope) where T : class =>
        scope.IsQueryable
            ? null
            : KnowledgeOutcome<T>.Failed(KnowledgeFailure.ScopeUnresolved,
                "no knowledge scope is resolved for this caller. A project must be mapped to a knowledge "
              + "base (knowledge_project_map) before its knowledge can be retrieved. Knowledge is never "
              + "read from an unmapped or default scope.");

    /// <summary>Carry a failure across a type change without inventing a new reason for it.</summary>
    private static KnowledgeOutcome<TOut> Propagate<TIn, TOut>(KnowledgeOutcome<TIn> from)
        where TIn : class where TOut : class =>
        new()
        {
            Failure = from.Failure == KnowledgeFailure.None ? KnowledgeFailure.Malformed : from.Failure,
            Reason = from.Reason ?? "the knowledge service returned no usable result",
            UpstreamRequestId = from.UpstreamRequestId,
        };

    /// <summary>Whether an item may be shown to this scope at all.</summary>
    private static bool Admissible(
        ForagerKnowledgeItem item, KnowledgeScope scope, KnowledgeSupport minimum, bool includeHistorical)
    {
        if (!ConfidentialityAllows(item, scope)) return false;

        var support = ForagerMapping.Support(item.Support);

        // Unknown support is admitted rather than filtered. It means FORAGER sent a level this build
        // does not recognise, and hiding those would make a version skew look like an empty
        // knowledge base — the least debuggable possible symptom. It renders as UNKNOWN SUPPORT.
        if (support != KnowledgeSupport.Unknown && (int)support > (int)minimum) return false;

        var status = ForagerMapping.Status(item.Status);
        if (!includeHistorical && status is KnowledgeStatus.Superseded or KnowledgeStatus.Archived) return false;

        return true;
    }

    /// <summary>
    /// The confidentiality fence, delegated to <see cref="KnowledgeScope.Allows"/> so the rule has
    /// exactly one definition. Unknown bands map to Tenant upstream, so an unrecognised value fails
    /// closed rather than escaping into a global scope.
    /// </summary>
    private static bool ConfidentialityAllows(ForagerKnowledgeItem item, KnowledgeScope scope) =>
        scope.Allows(ForagerMapping.Confidentiality(item.Scope));

    /// <summary>
    /// Query normalization: collapse whitespace, bound the length. Deliberately minimal — no
    /// stopword removal, no stemming, no rewriting. FORAGER's FTS5 backend already stems and ranks,
    /// and a second opinion applied before it would degrade recall while being invisible to anyone
    /// debugging why a search missed.
    /// </summary>
    private static string Normalize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";
        var collapsed = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 500 ? collapsed : collapsed[..500];
    }

    /// <summary>
    /// Approximate rendered size. An estimate on purpose: rendering the context to measure it, on
    /// every loop iteration, would be quadratic for no accuracy that matters — the budget is a
    /// guard rail, not an accounting boundary.
    /// </summary>
    private static int EstimateSize(IEnumerable<KnowledgeFact> facts, IEnumerable<KnowledgeEvidence> evidence)
    {
        var total = 0;
        foreach (var fact in facts) total += fact.Statement.Length + 160;
        foreach (var item in evidence) total += (item.Excerpt?.Length ?? 0) + (item.Location?.Length ?? 0) + 80;
        return total;
    }

    private static int Clamp(int value, int low, int high) => value < low ? low : value > high ? high : value;
}
