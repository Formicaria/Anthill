using System.Net;
using System.Text;
using Anthill.Modules.Knowledge;
using Anthill.SDK.Contracts;
using Anthill.SDK.Knowledge;
using Anthill.SDK.Modules;
using Anthill.SDK.Tools;
using Xunit;

// GlobalUsings.cs binds the bare `Task` to Anthill.Core.Domain.Task — the MISSION task — so the
// threading one must be named. Same per-file alias EventBusTests and ConversationScopeTests use.
// The generic form has to be spelled out: a non-generic using alias cannot take type arguments.
using ThreadingTask = System.Threading.Tasks.Task;

namespace Anthill.Tests;

/// <summary>
/// THE FORAGER KNOWLEDGE INTEGRATION — v0.3.8.121.
///
/// These tests pin the properties the integration exists to guarantee, and they are written against
/// the SHAPES A REAL FORAGER RETURNS. Every JSON fixture below was captured from a running instance
/// (canonical schema v1, the bundled Falcon demo), not written from the documentation — the two
/// agreed, but a wire format that is asserted from a spec is a wire format nobody has checked.
///
/// The properties, in the order they matter:
///   · scope isolation — knowledge never crosses a project boundary (Rule 12)
///   · provenance      — every fact carries evidence or an explicit unresolved marker (Rule 9)
///   · conflicts       — never hidden, never resolved by the retrieval layer (Rule 10)
///   · temporal state  — superseded and historical stay distinguishable
///   · failure         — unavailable is typed, and never looks like "nothing known" (Rule 11)
///   · disabled        — a colony without FORAGER is unchanged (Rule 15)
/// </summary>
public class KnowledgeIntegrationTests
{
    private const string ProjectA = "proj_ef42d498ae1e";
    private const string ProjectB = "proj_67c30789f63a";

    private static KnowledgeOptions Options(string endpoint = "http://127.0.0.1:8790") => new()
    {
        Enabled = true,
        Endpoint = endpoint,
        RetrievalTimeoutMs = 2000,
        ProbeTimeoutMs = 1000,
        IngestionTimeoutMs = 2000,
        CacheSeconds = 0,
    };

    private static KnowledgeScope ScopeA => KnowledgeScope.ForProject(ProjectA, "anthill-project-a");

    /// <summary>
    /// A FORAGER that answers from a routing table instead of a socket. Every response body here is
    /// a real capture; the handler exists so the provider's behaviour can be pinned without a Node
    /// process, not so the wire format can be invented.
    /// </summary>
    private sealed class StubForager : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new(StringComparer.Ordinal);
        public List<string> Requested { get; } = new();

        public StubForager Route(string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes[pathAndQuery] = (status, body);
            return this;
        }

        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.PathAndQuery;
            Requested.Add(key);
            if (!_routes.TryGetValue(key, out var hit))
            {
                return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        """{"error":{"code":"not_found","message":"no such route","request_id":"stub-404"}}""",
                        Encoding.UTF8, "application/json"),
                });
            }
            return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(hit.Status)
            {
                Content = new StringContent(hit.Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    // Real captures. Trimmed to the fields the mapper reads, with the shapes preserved exactly —
    // note `suggested_resolution` is an OBJECT, which a hand-written fixture gets wrong.
    private const string SearchBody = """
    {"query":"launch date","backend":"sqlite-fts5","took_ms":3,
     "knowledge":[
       {"item":{"id":"ki_68c6b4a77fc81cb5","project_id":"proj_ef42d498ae1e","type":"fact",
                "subject":"Project Falcon","title":"Project Falcon launch date: March 3, 2026",
                "statement":"The launch date for Project Falcon is March 3, 2026.",
                "attribute_key":"falcon|launch_date","attribute_value":"2026-03-03",
                "support":"direct_fact","confidence":0.9,"status":"disputed","scope":"tenant",
                "entity_ids":["ent_dd158495e69ad90d"],"effective_date":"2026-03-03",
                "extractor_name":"forager-deterministic","extractor_version":"1.0.0","evidence_count":3},
        "score":11.649,"snippet":"The launch date for Project Falcon is March 3, 2026.",
        "why":"Full-text match in title and statement of this fact"}],
     "entities":[{"id":"ent_dd158495e69ad90d","type":"project","canonical_name":"Project Falcon",
                  "aliases":[{"alias":"Falcon","kind":"name","confidence":0.85}],"confidence":0.9,"mention_count":7}]}
    """;

    private const string ItemBody = """
    {"id":"ki_68c6b4a77fc81cb5","project_id":"proj_ef42d498ae1e","type":"fact","subject":"Project Falcon",
     "title":"Project Falcon launch date: March 3, 2026",
     "statement":"The launch date for Project Falcon is March 3, 2026.",
     "attribute_key":"falcon|launch_date","attribute_value":"2026-03-03","support":"direct_fact",
     "confidence":0.9,"status":"disputed","scope":"tenant","entity_ids":["ent_dd158495e69ad90d"],
     "effective_date":"2026-03-03","extractor_name":"forager-deterministic","extractor_version":"1.0.0",
     "review_status":"unreviewed","superseded_by":null,"evidence_count":3,
     "conflict_ids":["cf_ce584a168fa9df22"],
     "entities":[{"id":"ent_dd158495e69ad90d","canonical_name":"Project Falcon","type":"project"}],
     "evidence":[{"id":"ev_4fb9d9a734fcb956","target_id":"ki_68c6b4a77fc81cb5",
                  "source_id":"src_01db7535b7da111a","chunk_id":"src_01db7535b7da111a_c0003",
                  "location":"Section: Falcon Design Review Notes > Schedule",
                  "excerpt":"The launch date for Project Falcon is March 3, 2026.",
                  "content_hash":"5e8b0ec56a569da8","extractor_name":"forager-deterministic",
                  "extractor_version":"1.0.0","model_name":null,"confidence":0.9,
                  "missing_excerpt":false,"source_name":"08-design-review.docx","source_type":"docx"}]}
    """;

    private const string ConflictsBody = """
    {"items":[{"id":"cf_ce584a168fa9df22","type":"attribute_mismatch","attribute_key":"falcon|launch_date",
               "status":"open",
               "description":"2 different values were stated for \"launch_date\" of falcon: 2026-03-03 vs 2026-04-10",
               "item_ids":["ki_68c6b4a77fc81cb5","ki_f21cef030f6b3127"],
               "source_ids":["src_01db7535b7da111a","src_9c1"],
               "suggested_resolution":{"winner_id":"ki_f21cef030f6b3127",
                                       "reason":"Only this statement comes from a dated source (2026-02-18)"},
               "resolution":null}],"page":1,"page_size":200,"total":1}
    """;

    private static (ForagerKnowledgeProvider Provider, StubForager Stub) Build(
        StubForager stub, KnowledgeOptions? options = null)
    {
        var opts = options ?? Options();
        var client = new ForagerClient(() => opts, stub);
        return (new ForagerKnowledgeProvider(() => opts, client, new KnowledgeCache()), stub);
    }

    // ---- Rule 12: scope isolation ------------------------------------------------------------

    [Fact]
    public async ThreadingTask AnUnresolvedScope_RetrievesNothingRatherThanEverything()
    {
        var (provider, stub) = Build(new StubForager());

        var result = await provider.SearchAsync(new KnowledgeSearchRequest
        {
            Query = "anything",
            Scope = KnowledgeScope.Unresolved,
        }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(KnowledgeFailure.ScopeUnresolved, result.Failure);
        // The load-bearing assertion: it did not fall back to a default scope and go asking.
        Assert.Empty(stub.Requested);
    }

    [Fact]
    public async ThreadingTask AnItemBelongingToAnotherProject_IsNotFound_NotReturned()
    {
        // FORAGER's /api/knowledge/{id} is NOT project-scoped — verified against a live instance,
        // where a bare id resolved and returned another project's row with HTTP 200. That is why
        // the provider checks project_id on the RESPONSE. This test is that check.
        var stub = new StubForager()
            .Route("/api/knowledge/ki_68c6b4a77fc81cb5", ItemBody);
        var (provider, _) = Build(stub);

        var otherProject = KnowledgeScope.ForProject(ProjectB, "anthill-project-b");
        var result = await provider.GetAsync("ki_68c6b4a77fc81cb5", otherProject, CancellationToken.None);

        Assert.False(result.Ok);
        // NotFound, not a denial: telling a caller that an id exists in a project it cannot see is
        // itself a disclosure.
        Assert.Equal(KnowledgeFailure.NotFound, result.Failure);
    }

    [Fact]
    public async ThreadingTask TheSameItem_IsReturnedWithinItsOwnScope()
    {
        var stub = new StubForager().Route("/api/knowledge/ki_68c6b4a77fc81cb5", ItemBody);
        var (provider, _) = Build(stub);

        var result = await provider.GetAsync("ki_68c6b4a77fc81cb5", ScopeA, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("ki_68c6b4a77fc81cb5", result.Value!.KnowledgeId);
    }

    [Fact]
    public void AGlobalScope_MayNotSeeTenantMaterial()
    {
        // The rule this pins was written wrong first: an ordering comparison on the enum silently
        // admitted tenant material into a global scope, because Tenant is 1 and General is 2 and the
        // numbers mean nothing. See KnowledgeScope.Allows.
        var global = new KnowledgeScope { Kind = KnowledgeScopeKind.Global, ProjectRef = "shared" };

        Assert.False(global.Allows(KnowledgeConfidentiality.Tenant));
        Assert.False(global.Allows(KnowledgeConfidentiality.Unknown));
        Assert.True(global.Allows(KnowledgeConfidentiality.General));

        // Anything narrower sees both.
        Assert.True(ScopeA.Allows(KnowledgeConfidentiality.Tenant));
        Assert.True(ScopeA.Allows(KnowledgeConfidentiality.General));
    }

    [Fact]
    public void TwoScopes_NeverShareACacheEntry()
    {
        Assert.NotEqual(
            KnowledgeScope.ForProject(ProjectA).CacheKey,
            KnowledgeScope.ForProject(ProjectB).CacheKey);
    }

    // ---- Rule 9: provenance --------------------------------------------------------------------

    [Fact]
    public async ThreadingTask EveryRetrievedFact_CarriesEvidenceOrAnExplicitUnresolvedMarker()
    {
        var stub = new StubForager()
            .Route($"/api/projects/{ProjectA}/search?q=launch%20date&limit=24&include_entities=true&include_chunks=false", SearchBody)
            .Route("/api/knowledge/ki_68c6b4a77fc81cb5", ItemBody)
            .Route($"/api/projects/{ProjectA}/conflicts?status=open&page_size=200", ConflictsBody);
        var (provider, _) = Build(stub);

        var result = await provider.RetrieveAsync(new KnowledgeRetrievalRequest
        {
            Query = "launch date",
            Scope = ScopeA,
            TopK = 8,
        }, CancellationToken.None);

        // Coalesced rather than passed bare: xunit's userMessage parameter is non-nullable and
        // TreatWarningsAsErrors turns CS8604 into a build failure.
        Assert.True(result.Ok, result.Reason ?? "retrieval did not succeed");
        var context = result.Value!;
        Assert.NotEmpty(context.Facts);

        // The invariant, checked by the type itself rather than restated here.
        Assert.Empty(context.FactsWithoutProvenance());

        var fact = context.Facts.Single();
        Assert.Single(fact.EvidenceIds);
        var evidence = Assert.Single(context.Evidence);
        Assert.Equal("08-design-review.docx", evidence.SourceName);
        Assert.Equal("The launch date for Project Falcon is March 3, 2026.", evidence.Excerpt);
        Assert.False(string.IsNullOrEmpty(evidence.ExcerptHash));
    }

    [Fact]
    public async ThreadingTask ProvenanceSurvivesTheWholePipeline_FromResultToSource()
    {
        var stub = new StubForager()
            .Route($"/api/projects/{ProjectA}/search?q=launch%20date&limit=24&include_entities=true&include_chunks=false", SearchBody)
            .Route("/api/knowledge/ki_68c6b4a77fc81cb5", ItemBody)
            .Route($"/api/projects/{ProjectA}/conflicts?status=open&page_size=200", ConflictsBody);
        var (provider, _) = Build(stub);

        var context = (await provider.RetrieveAsync(new KnowledgeRetrievalRequest
        { Query = "launch date", Scope = ScopeA }, CancellationToken.None)).Value!;

        // result -> knowledge item -> evidence -> source, joinable end to end.
        var fact = context.Facts.Single();
        var evidenceId = Assert.Single(fact.EvidenceIds);
        var evidence = context.Evidence.Single(e => e.EvidenceId == evidenceId);
        Assert.Equal(fact.KnowledgeId, evidence.KnowledgeId);
        Assert.Equal("src_01db7535b7da111a", evidence.SourceId);
        Assert.Equal("forager-deterministic@1.0.0", evidence.Extractor);
    }

    [Fact]
    public void AFactWithNoEvidenceAndNoUnresolvedStatus_IsADefect()
    {
        var bad = new KnowledgeFact
        {
            KnowledgeId = "ki_bad", Statement = "asserted from nowhere",
            Support = KnowledgeSupport.DirectFact, Status = KnowledgeStatus.Active,
        };
        Assert.False(bad.HasProvenance);

        var honest = bad with { Status = KnowledgeStatus.Unresolved };
        Assert.True(honest.HasProvenance);
    }

    // ---- Rule 10: conflicts --------------------------------------------------------------------

    [Fact]
    public async ThreadingTask AContestedFact_ArrivesWithItsConflictAttachedAndUnresolved()
    {
        var stub = new StubForager()
            .Route($"/api/projects/{ProjectA}/search?q=launch%20date&limit=24&include_entities=true&include_chunks=false", SearchBody)
            .Route("/api/knowledge/ki_68c6b4a77fc81cb5", ItemBody)
            .Route($"/api/projects/{ProjectA}/conflicts?status=open&page_size=200", ConflictsBody);
        var (provider, _) = Build(stub);

        var context = (await provider.RetrieveAsync(new KnowledgeRetrievalRequest
        { Query = "launch date", Scope = ScopeA }, CancellationToken.None)).Value!;

        Assert.True(context.HasOpenConflicts);
        var conflict = Assert.Single(context.Conflicts);
        Assert.Equal("attribute_mismatch", conflict.Type);
        Assert.True(conflict.IsOpen);
        Assert.Null(conflict.Resolution);

        // The suggestion is CARRIED but not applied — the reasoning layer weighs it.
        Assert.Contains("dated source", conflict.SuggestedResolution!);
        Assert.Equal(KnowledgeStatus.Disputed, context.Facts.Single().Status);
    }

    [Fact]
    public async ThreadingTask TheRenderedContext_LeadsWithTheConflictAndNeverPresentsItAsSettled()
    {
        var stub = new StubForager()
            .Route($"/api/projects/{ProjectA}/search?q=launch%20date&limit=24&include_entities=true&include_chunks=false", SearchBody)
            .Route("/api/knowledge/ki_68c6b4a77fc81cb5", ItemBody)
            .Route($"/api/projects/{ProjectA}/conflicts?status=open&page_size=200", ConflictsBody);
        var (provider, _) = Build(stub);

        var rendered = (await provider.RetrieveAsync(new KnowledgeRetrievalRequest
        { Query = "launch date", Scope = ScopeA }, CancellationToken.None)).Value!.Render();

        Assert.Contains("CONFLICTS DETECTED", rendered, StringComparison.Ordinal);
        Assert.Contains("Do not report either side as settled", rendered, StringComparison.Ordinal);
        Assert.Contains("NOT APPLIED", rendered, StringComparison.Ordinal);

        // A model reads the disagreement BEFORE it reads the facts. One that meets the facts first
        // has already formed an answer.
        Assert.True(rendered.IndexOf("CONFLICTS DETECTED", StringComparison.Ordinal)
                  < rendered.IndexOf("\nFACTS\n", StringComparison.Ordinal));
    }

    // ---- support classification and temporal state ---------------------------------------------

    [Theory]
    [InlineData("direct_fact", KnowledgeSupport.DirectFact)]
    [InlineData("supported_inference", KnowledgeSupport.SupportedInference)]
    [InlineData("uncertain_inference", KnowledgeSupport.UncertainInference)]
    [InlineData("unverified_claim", KnowledgeSupport.UnverifiedClaim)]
    [InlineData("something_from_a_newer_forager", KnowledgeSupport.Unknown)]
    [InlineData(null, KnowledgeSupport.Unknown)]
    public void SupportIsMapped_AndAnUnrecognisedLevelIsNeverPromoted(string? wire, KnowledgeSupport expected)
        => Assert.Equal(expected, ForagerMapping.Support(wire));

    [Theory]
    [InlineData("tenant", KnowledgeConfidentiality.Tenant)]
    [InlineData("general", KnowledgeConfidentiality.General)]
    [InlineData("something_new", KnowledgeConfidentiality.Tenant)]
    [InlineData(null, KnowledgeConfidentiality.Tenant)]
    public void AnUnrecognisedConfidentialityBand_FailsClosedToTenant(string? wire, KnowledgeConfidentiality expected)
        => Assert.Equal(expected, ForagerMapping.Confidentiality(wire));

    [Fact]
    public void SupersededKnowledge_StaysDistinguishableFromCurrent()
    {
        Assert.Equal(KnowledgeStatus.Superseded, ForagerMapping.Status("superseded"));
        Assert.Equal(KnowledgeStatus.Stale, ForagerMapping.Status("stale"));
        Assert.Equal(KnowledgeStatus.Archived, ForagerMapping.Status("archived"));
        Assert.Equal(KnowledgeStatus.Unknown, ForagerMapping.Status("a_state_this_build_predates"));
    }

    [Fact]
    public void AnUnverifiedClaim_IsRenderedWithAnExplicitWarning()
    {
        var context = new KnowledgeContext
        {
            Facts = new[]
            {
                new KnowledgeFact
                {
                    KnowledgeId = "ki_1", Statement = "Somebody said the vendor agreed.",
                    Support = KnowledgeSupport.UnverifiedClaim, Status = KnowledgeStatus.Active,
                    EvidenceIds = new[] { "ev_1" },
                },
            },
            Evidence = new[]
            {
                new KnowledgeEvidence
                {
                    EvidenceId = "ev_1", KnowledgeId = "ki_1", SourceId = "src_1",
                    SourceName = "notes.md", Excerpt = "I think the vendor agreed.",
                },
            },
            Metadata = new RetrievalMetadata { Query = "vendor", Scope = ScopeA },
        };

        var rendered = context.Render();
        Assert.Contains("UNVERIFIED CLAIM", rendered, StringComparison.Ordinal);
        Assert.Contains("supported by nothing", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyContext_SaysItSearchedRatherThanImplyingTheAnswerIsUnknowable()
    {
        var rendered = KnowledgeContext.Empty("what did we decide", ScopeA).Render();

        Assert.Contains("No organizational knowledge matched", rendered, StringComparison.Ordinal);
        // The distinction that stops a model answering from priors.
        Assert.Contains("does NOT mean", rendered, StringComparison.Ordinal);
        Assert.Contains("Do not fill the gap by assumption", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRender_IsDeterministic()
    {
        // Same inputs, same bytes. Not aesthetics: a context that reorders itself between runs makes
        // a misbehaving mission unreproducible and prompt caching pointless.
        var context = new KnowledgeContext
        {
            Facts = new[]
            {
                new KnowledgeFact
                {
                    KnowledgeId = "ki_2", Statement = "Revision C was approved.",
                    Support = KnowledgeSupport.DirectFact, Status = KnowledgeStatus.Unresolved,
                    EvidenceIds = Array.Empty<string>(),
                },
            },
            Metadata = new RetrievalMetadata { Query = "q", Scope = ScopeA },
        };

        Assert.Equal(context.Render(), context.Render());
    }

    // ---- Rule 11 / 15: failure and absence -----------------------------------------------------

    [Fact]
    public async ThreadingTask AnUnreachableForager_IsATypedFailure_NotAnEmptyResult()
    {
        var stub = new StubForager(); // every route 404s
        var (provider, _) = Build(stub);

        var result = await provider.SearchAsync(new KnowledgeSearchRequest
        { Query = "anything", Scope = ScopeA }, CancellationToken.None);

        Assert.False(result.Ok);
        // NOT an empty success. "Nothing known" and "could not ask" must never be the same value.
        Assert.NotEqual(KnowledgeFailure.None, result.Failure);
    }

    [Fact]
    public async ThreadingTask AForagerErrorEnvelope_KeepsItsRequestIdForCorrelation()
    {
        var stub = new StubForager().Route(
            "/api/knowledge/ki_x",
            """{"error":{"code":"internal_error","message":"boom","request_id":"0d262cb0-7113"}}""",
            HttpStatusCode.InternalServerError);
        var (provider, _) = Build(stub);

        var result = await provider.GetAsync("ki_x", ScopeA, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(KnowledgeFailure.Upstream, result.Failure);
        Assert.Equal("0d262cb0-7113", result.UpstreamRequestId);
        Assert.True(result.Retryable);
    }

    [Fact]
    public async ThreadingTask WhenKnowledgeIsDisabled_NothingIsAskedAndTheReasonIsTyped()
    {
        var off = Options() with { Enabled = false };
        var stub = new StubForager();
        var (provider, _) = Build(stub, off);

        var result = await provider.SearchAsync(new KnowledgeSearchRequest
        { Query = "anything", Scope = ScopeA }, CancellationToken.None);

        Assert.Equal(KnowledgeFailure.Disabled, result.Failure);
        Assert.False(result.Retryable);
        Assert.Empty(stub.Requested);
    }

    [Fact]
    public async ThreadingTask ANullProvider_ReportsDisabledRatherThanEmpty()
    {
        var provider = new NullKnowledgeProvider("knowledge is disabled in configuration");
        var availability = await provider.ProbeAsync(CancellationToken.None);

        Assert.False(availability.Usable);
        Assert.False(availability.Enabled);
        Assert.Contains("disabled", availability.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- SSRF / endpoint safety ----------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1:8790", true)]
    [InlineData("http://localhost:8790", true)]
    [InlineData("http://[::1]:8790", true)]
    [InlineData("http://127.5.5.5:8790", true)]
    [InlineData("http://10.0.0.5:8790", false)]
    [InlineData("http://knowledge.internal:8790", false)]
    [InlineData("http://169.254.169.254", false)]
    public void ANonLoopbackEndpoint_IsRefusedUnlessTheOperatorOptedIn(string endpoint, bool loopback)
    {
        var uri = new Uri(endpoint);
        Assert.Equal(loopback, KnowledgeOptions.IsLoopback(uri));

        var options = Options(endpoint);
        // FORAGER has no authentication of its own, so reaching one across a network has to be a
        // decision somebody made rather than one a copied config made for them.
        Assert.Equal(loopback, options.Unusable() is null);

        var permitted = options with { AllowRemoteEndpoint = true };
        Assert.Null(permitted.Unusable());
    }

    [Fact]
    public async ThreadingTask ANonLoopbackEndpoint_IsRefusedBeforeAnyRequestLeaves()
    {
        var stub = new StubForager();
        var (provider, _) = Build(stub, Options("http://192.168.1.50:8790"));

        var result = await provider.SearchAsync(new KnowledgeSearchRequest
        { Query = "anything", Scope = ScopeA }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Empty(stub.Requested);
    }

    // ---- tools ---------------------------------------------------------------------------------

    [Fact]
    public void NoKnowledgeTool_TakesAProjectArgument()
    {
        // The security property behind KnowledgeScopeContext: a model chooses tool arguments, so a
        // project_id parameter would make the scope of a knowledge query something the model
        // selects. Rule 12 would then be enforced by the model's discretion, which is not
        // enforcement. This test is what stops one being added by helpfulness.
        var options = Options();
        var provider = new NullKnowledgeProvider("off");
        ITool[] tools =
        {
            new KnowledgeSearchTool(provider, () => options),
            new KnowledgeRetrieveTool(provider, () => options),
            new KnowledgeGetTool(provider, () => options),
            new KnowledgeEvidenceTool(provider, () => options),
            new KnowledgeEntityTool(provider, () => options),
        };

        foreach (var tool in tools)
        {
            // PARAMETER NAMES, not the whole schema blob. The first version of this assertion
            // searched the raw JSON and failed on knowledge_retrieve, whose `include_entities`
            // description reads "Include related people, projects and organizations" — a legitimate
            // sentence that says nothing about scope. The rule is about what a model can SET.
            var schema = System.Text.Json.Nodes.JsonNode.Parse(tool.ParametersJson)!.AsObject();
            var names = schema["properties"]?.AsObject().Select(p => p.Key).ToList() ?? new List<string>();

            foreach (var forbidden in new[] { "project", "scope", "tenant", "workspace" })
                Assert.DoesNotContain(names, n => n.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void EveryKnowledgeToolSchema_IsAValidJsonSchemaObject()
    {
        var options = Options();
        var provider = new NullKnowledgeProvider("off");
        ITool[] tools =
        {
            new KnowledgeSearchTool(provider, () => options),
            new KnowledgeRetrieveTool(provider, () => options),
            new KnowledgeGetTool(provider, () => options),
            new KnowledgeEvidenceTool(provider, () => options),
            new KnowledgeEntityTool(provider, () => options),
            new KnowledgeReviewTool(_ => { }),
        };

        foreach (var tool in tools)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(tool.ParametersJson);
            Assert.IsType<System.Text.Json.Nodes.JsonObject>(node);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        }
    }

    [Fact]
    public void AToolCalledWithNoAmbientScope_RefusesAndSaysNotToSubstitute()
    {
        var tool = new KnowledgeSearchTool(new NullKnowledgeProvider("off"), () => Options());

        // No KnowledgeScopeContext.Enter — the default is Unresolved, which retrieves nothing.
        var result = tool.Run(new Dictionary<string, object?> { ["query"] = "anything" });

        Assert.False(result.Success);
        Assert.Equal(FailureClass.AuthorizationFailure, result.Failure);
        Assert.Contains("Do not substitute", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheAmbientScope_NarrowsAndRestores()
    {
        Assert.False(KnowledgeScopeContext.HasScope);

        using (KnowledgeScopeContext.Enter(ScopeA))
        {
            Assert.True(KnowledgeScopeContext.HasScope);
            Assert.Equal(ProjectA, KnowledgeScopeContext.Current.ProjectRef);

            using (KnowledgeScopeContext.Enter(KnowledgeScope.ForProject(ProjectB)))
                Assert.Equal(ProjectB, KnowledgeScopeContext.Current.ProjectRef);

            Assert.Equal(ProjectA, KnowledgeScopeContext.Current.ProjectRef);
        }

        Assert.False(KnowledgeScopeContext.HasScope);
    }

    [Fact]
    public void AReviewProposal_RequiresARationaleAndAppliesNothing()
    {
        var recorded = new List<KnowledgeReviewProposal>();
        var tool = new KnowledgeReviewTool(recorded.Add);

        using var _ = KnowledgeScopeContext.Enter(ScopeA);

        var thin = tool.Run(new Dictionary<string, object?>
        { ["knowledge_id"] = "ki_1", ["action"] = "reject", ["rationale"] = "no" });
        Assert.False(thin.Success);
        Assert.Empty(recorded);

        var bogus = tool.Run(new Dictionary<string, object?>
        { ["knowledge_id"] = "ki_1", ["action"] = "delete_everything", ["rationale"] = "because I said so" });
        Assert.False(bogus.Success);
        Assert.Empty(recorded);

        var good = tool.Run(new Dictionary<string, object?>
        {
            ["knowledge_id"] = "ki_1", ["action"] = "reject",
            ["rationale"] = "The cited source was superseded by the March change log.",
        });
        Assert.True(good.Success);
        Assert.Single(recorded);
        // It PROPOSES. The output must not let a model believe the base changed.
        Assert.Contains("has NOT changed", good.Output, StringComparison.Ordinal);
        // v0.3.8.122 — and it must not describe a pipeline that does not exist. This said the
        // proposal was "queued for an operator to approve or decline"; there is no queue and no
        // approval surface consuming these, and a model told its change is pending will plan the
        // next step as though it were.
        Assert.DoesNotContain("queued", good.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A SINK THAT CANNOT RECORD MUST NOT READ AS SUCCESS — v0.3.8.122.
    ///
    /// The composition default was `_ => { }`: a module built without a proposal sink accepted every
    /// proposal, dropped it, and let the tool report that it had been recorded. Nothing downstream
    /// could tell that apart from a filing, which is the worst shape a failure can take — it is
    /// invisible at the only moment anyone could act on it. The default now throws, and this is the
    /// behaviour that makes throwing the right answer: the tool already had an honest failure branch
    /// and never had a reason to reach it.
    /// </summary>
    [Fact]
    public void AProposalThatCannotBeRecorded_FailsRatherThanReportingSuccess()
    {
        var tool = new KnowledgeReviewTool(
            _ => throw new InvalidOperationException("no proposal sink is composed"));

        using var _ = KnowledgeScopeContext.Enter(ScopeA);

        var result = tool.Run(new Dictionary<string, object?>
        {
            ["knowledge_id"] = "ki_1", ["action"] = "reject",
            ["rationale"] = "The cited source was superseded by the March change log.",
        });

        Assert.False(result.Success);
        Assert.Contains("could not be recorded", result.Error ?? "", StringComparison.Ordinal);
    }

    // ---- module registration ------------------------------------------------------------------

    private sealed class CapturingContext : IModuleContext
    {
        public List<ITool> Tools { get; } = new();
        public List<SDK.Events.ColonyEvent> Events2 { get; } = new();

        public SDK.Events.IEventBus Events { get; }
        public SDK.Memory.IPheromoneMemory Pheromones => null!;
        public SDK.Memory.IEventLog EventLog => null!;
        public IReadOnlyDictionary<string, object?> Configuration { get; } = new Dictionary<string, object?>();
        public Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory { get; } =
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

        public CapturingContext() => Events = new Bus(this);

        public void RegisterReasoningProvider(SDK.Reasoning.IReasoningProviderFactory factory) { }
        public void RegisterCapabilityProbe(SDK.Reasoning.IModelCapabilityProbe probe) { }
        public void RegisterTool(ITool tool) => Tools.Add(tool);

        private sealed class Bus : SDK.Events.IEventBus
        {
            private readonly CapturingContext _owner;
            public Bus(CapturingContext owner) => _owner = owner;
            public void Publish(SDK.Events.ColonyEvent colonyEvent) => _owner.Events2.Add(colonyEvent);

            // Both overloads. Registration publishes and never subscribes, so these are inert —
            // but an interface implemented by halves does not compile, and a test double that
            // silently dropped a subscription would be worse than one that never offered it.
            public IDisposable Subscribe(Action<SDK.Events.ColonyEvent> handler) => new Noop();
            public IDisposable Subscribe(string eventType, Action<SDK.Events.ColonyEvent> handler) => new Noop();

            private sealed class Noop : IDisposable { public void Dispose() { } }
        }
    }

    [Fact]
    public void WithKnowledgeDisabled_TheToolsRegisterAndRefuseRatherThanBeingAbsent()
    {
        // This test asserted the OPPOSITE first — that a disabled module registers nothing — and
        // three colony guards failed on it: a role contract naming an unregistered tool makes that
        // role unqualified, so shipping knowledge off (the default) shipped an unready researcher.
        //
        // "Registered and refusing" is a different fact from "declared and absent". The tools are
        // present so the roster qualifies; they refuse at call time so nothing can act on knowledge
        // this colony does not have.
        var module = new KnowledgeModule(() => Options() with { Enabled = false });
        var context = new CapturingContext();

        module.Register(context);

        Assert.Equal(
            KnowledgeToolNames.All.OrderBy(n => n, StringComparer.Ordinal).ToList(),
            context.Tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList());

        var published = Assert.Single(context.Events2);
        Assert.Contains("inactive", published.Message, StringComparison.OrdinalIgnoreCase);
        // Pattern match rather than Assert.Equal(false, ...) — a literal boolean expected value
        // trips xUnit2004, and TreatWarningsAsErrors makes that a build failure.
        Assert.True(published.Metadata["usable"] is false);

        // And it actually refuses. A registered tool that quietly answered would be worse than one
        // that was never offered.
        var search = context.Tools.Single(t => t.Name == KnowledgeToolNames.Search);
        using var _ = KnowledgeScopeContext.Enter(ScopeA);
        var result = search.Run(new Dictionary<string, object?> { ["query"] = "anything" });
        Assert.False(result.Success);
        Assert.Contains("Do not substitute", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithKnowledgeEnabled_TheModuleRegistersExactlyTheDeclaredVocabulary()
    {
        var module = new KnowledgeModule(() => Options());
        var context = new CapturingContext();

        module.Register(context);

        var registered = context.Tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var declared = KnowledgeToolNames.All.OrderBy(n => n, StringComparer.Ordinal).ToList();

        // No missing, no phantom — the same contract ToolInventory holds for the build's vocabulary.
        Assert.Equal(declared, registered);
    }

    [Fact]
    public void RegistrationPublishesNoSecret()
    {
        var module = new KnowledgeModule(() => Options() with { Token = "super-secret-token-value" });
        var context = new CapturingContext();

        module.Register(context);

        var published = Assert.Single(context.Events2);
        var serialized = System.Text.Json.JsonSerializer.Serialize(published.Metadata);
        Assert.DoesNotContain("super-secret-token-value", serialized, StringComparison.Ordinal);
        // It says WHETHER there is one, which is the operable fact.
        Assert.Contains("authenticated", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadToolsAreReadOnly_AndOnlyReviewIsNot()
    {
        foreach (var name in KnowledgeToolNames.All)
        {
            var expected = name != KnowledgeToolNames.Review;
            Assert.Equal(expected, KnowledgeToolNames.IsReadOnly(name));
        }
    }
}
