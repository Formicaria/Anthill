using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Anthill.Modules.Knowledge;
using Anthill.SDK.Knowledge;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.143 (A1) — the consumer's half of the producer's F0: `GET /api/capabilities` consumed
/// (identity, protocol window, honest flags), the ready-only engine tolerated, an incompatible
/// declaration refused with both numbers, and `X-Forager-Project` sent on every direct-id call.
///
/// The capability fixture below is a REAL capture from a running engine (producer tip, sandbox,
/// 2026-09-08) — the ForagerWire habit: field names read off a running instance, never invented.
/// </summary>
public class ForagerCapabilitiesTests
{
    private const string ProjectA = "proj_ef42d498ae1e";

    /// <summary>Captured verbatim from `curl /api/capabilities` against a live engine.</summary>
    private const string CapabilitiesBody = """
    {
        "protocol_version": 1,
        "canonical_schema_version": 1,
        "engine": { "name": "forager", "version": "0.1.4" },
        "instance": {
            "instance_id": "fgi_485c86c74660c46cf135ac70f07046e8",
            "generation": "gen_1bade2bbafd405e5",
            "generation_reason": "created",
            "created_at": "2026-09-08T03:03:35.913Z",
            "data_dir": "/sessions/sandbox/forager-data",
            "mode": "standalone"
        },
        "capabilities": {
            "retrieval": true,
            "ingestion": true,
            "exports": {
                "anthill":  { "package_version": 1,    "canonical": true,  "manifest": "anthill-package.json", "checksums": "sha256-per-file" },
                "jsonl":    { "package_version": null, "canonical": true,  "manifest": "manifest.json",        "checksums": null },
                "obsidian": { "package_version": null, "canonical": false, "manifest": null,                   "checksums": null },
                "folder":   { "package_version": 1,    "canonical": false, "manifest": ".forager-export.json", "checksums": "sha256-per-file" }
            },
            "imports": [],
            "publication": false,
            "change_feed": false,
            "push_delivery": false,
            "canonical_import": false,
            "authentication": false
        },
        "authentication": {
            "required": false,
            "schemes": [],
            "note": "This build has no authentication. It binds loopback by default; a non-loopback bind exposes every operation to the network."
        },
        "time": "2026-09-08T03:03:36.856Z"
    }
    """;

    private const string ReadyBody = """
    {"status":"ok","version":"0.1.4","schema_version":1,"time":"2026-09-08T03:03:36.847Z",
     "database":"ok","data_dir":"/sessions/sandbox/forager-data",
     "model_provider":"none (deterministic mode)","migrations_applied":7,"search_backend":"sqlite-fts5"}
    """;

    private const string ItemBody = """
    {"id":"ki_68c6b4a77fc81cb5","project_id":"proj_ef42d498ae1e","type":"fact","subject":"Project Falcon",
     "title":"Project Falcon launch date: March 3, 2026",
     "statement":"The launch date for Project Falcon is March 3, 2026.",
     "attribute_key":"falcon|launch_date","attribute_value":"2026-03-03","support":"direct_fact",
     "confidence":0.9,"status":"active","scope":"tenant","entity_ids":[],
     "effective_date":"2026-03-03","extractor_name":"forager-deterministic","extractor_version":"1.0.0",
     "review_status":"unreviewed","superseded_by":null,"evidence_count":1,
     "conflict_ids":[],"entities":[],"evidence":[],"source_ids":[]}
    """;

    /// <summary>A stub that records the X-Forager-Project header per request, path-keyed.</summary>
    private sealed class RecordingForager : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new(StringComparer.Ordinal);
        public List<(string Path, string? ProjectHeader)> Requests { get; } = new();

        public RecordingForager Route(string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes[pathAndQuery] = (status, body);
            return this;
        }

        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            request.Headers.TryGetValues("X-Forager-Project", out var values);
            Requests.Add((path, values?.FirstOrDefault()));

            if (!_routes.TryGetValue(path, out var hit))
                return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        """{"error":{"code":"not_found","message":"no such route","request_id":"stub-404"}}""",
                        Encoding.UTF8, "application/json"),
                });

            return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(hit.Status)
            {
                Content = new StringContent(hit.Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (ForagerKnowledgeProvider Provider, RecordingForager Stub) Rig(
        RecordingForager stub)
    {
        var opts = new KnowledgeOptions
        {
            Enabled = true,
            Endpoint = "http://127.0.0.1:8790",
            ProbeTimeoutMs = 1000,
            RetrievalTimeoutMs = 2000,
            IngestionTimeoutMs = 2000,
            CacheSeconds = 0,
        };
        var client = new ForagerClient(() => opts, stub);
        return (new ForagerKnowledgeProvider(() => opts, client, new KnowledgeCache()), stub);
    }

    private static KnowledgeScope ScopeA => KnowledgeScope.ForProject(ProjectA, "anthill-project-a");

    // -------------------------------------------------------------------------------------------

    /// <summary>The wire type swallows the real response whole — every field lands where mapping
    /// will look for it, including the per-format export descriptors and the honest flags.</summary>
    [Fact]
    public void TheRealCapabilitiesCapture_ParsesWhole()
    {
        var caps = JsonSerializer.Deserialize<ForagerCapabilities>(CapabilitiesBody,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(1, caps.ProtocolVersion);
        Assert.Equal(1, caps.CanonicalSchemaVersion);
        Assert.Equal("forager", caps.Engine!.Name);
        Assert.Equal("fgi_485c86c74660c46cf135ac70f07046e8", caps.Instance!.InstanceId);
        Assert.Equal("gen_1bade2bbafd405e5", caps.Instance.Generation);
        Assert.Equal("standalone", caps.Instance.Mode);

        var flags = caps.Capabilities!;
        Assert.True(flags.Retrieval);
        Assert.False(flags.Publication);
        Assert.False(flags.ChangeFeed);
        Assert.False(flags.CanonicalImport);
        Assert.NotNull(flags.Imports);            // [] — "cannot import", not "too old to say"
        Assert.Empty(flags.Imports!);

        // The checkable form of contract §6: the anthill package is canonical WITH checksums;
        // Obsidian is rendered output and says so.
        Assert.True(flags.Exports!["anthill"].Canonical);
        Assert.Equal(1, flags.Exports["anthill"].PackageVersion);
        Assert.Equal("sha256-per-file", flags.Exports["anthill"].Checksums);
        Assert.False(flags.Exports["obsidian"].Canonical);

        Assert.False(caps.Authentication!.Required);
    }

    /// <summary>The probe carries the producer's identity, and both version numbers inside the
    /// window leave the availability compatible and usable.</summary>
    [Fact]
    public async System.Threading.Tasks.Task TheProbe_CarriesInstanceIdentity_AndStaysUsable()
    {
        var (provider, _) = Rig(new RecordingForager()
            .Route("/api/ready", ReadyBody)
            .Route("/api/capabilities", CapabilitiesBody));

        var availability = await provider.ProbeAsync(CancellationToken.None);

        Assert.True(availability.Usable);
        Assert.True(availability.Compatible);
        Assert.Equal(1, availability.ProtocolVersion);
        Assert.Equal("fgi_485c86c74660c46cf135ac70f07046e8", availability.InstanceId);
        Assert.Equal("gen_1bade2bbafd405e5", availability.InstanceGeneration);
        Assert.Equal("standalone", availability.InstanceMode);
        Assert.Equal("sqlite-fts5", availability.SearchBackend);   // /ready facts still present
    }

    /// <summary>An engine from before the capability response 404s the route. That declares
    /// nothing, and the additive rule tolerates silence: the probe stands on /ready alone.</summary>
    [Fact]
    public async System.Threading.Tasks.Task AnEngineWithoutCapabilities_IsToleratedOnReadyAlone()
    {
        var (provider, _) = Rig(new RecordingForager().Route("/api/ready", ReadyBody));

        var availability = await provider.ProbeAsync(CancellationToken.None);

        Assert.True(availability.Usable);
        Assert.True(availability.Compatible);
        Assert.Null(availability.ProtocolVersion);
        Assert.Null(availability.InstanceId);
        Assert.Equal("0.1.4", availability.Version);
    }

    /// <summary>
    /// A DECLARED protocol outside the window is refused with both numbers — reachable, honest,
    /// and not usable. Never a silent downgrade to the old routes (contract §3).
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task ADeclaredForeignProtocol_MakesTheAvailabilityIncompatible()
    {
        var foreign = CapabilitiesBody.Replace("\"protocol_version\": 1", "\"protocol_version\": 2");
        var (provider, _) = Rig(new RecordingForager()
            .Route("/api/ready", ReadyBody)
            .Route("/api/capabilities", foreign));

        var availability = await provider.ProbeAsync(CancellationToken.None);

        Assert.True(availability.Reachable);       // the service answered — that truth stands
        Assert.False(availability.Compatible);
        Assert.False(availability.Usable);
        Assert.Contains("protocol version 2", availability.Reason);
        Assert.Contains("speaks 1", availability.Reason);
    }

    /// <summary>
    /// Every direct-id call declares its project as `X-Forager-Project` — the header FORAGER
    /// enforces on those routes, and the handshake that becomes authorization when pairing lands.
    /// The project-rooted search path carries the project in the URL and sends none.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task DirectIdCalls_DeclareTheProjectHeader()
    {
        var (provider, stub) = Rig(new RecordingForager()
            .Route("/api/knowledge/ki_68c6b4a77fc81cb5", ItemBody)
            .Route("/api/knowledge/ki_68c6b4a77fc81cb5/evidence",
                """{"items":[],"page":1,"page_size":50,"total":0}"""));

        var fact = await provider.GetAsync("ki_68c6b4a77fc81cb5", ScopeA, CancellationToken.None);
        Assert.True(fact.Ok, fact.Reason);
        var evidence = await provider.GetEvidenceAsync("ki_68c6b4a77fc81cb5", ScopeA, CancellationToken.None);
        Assert.True(evidence.Ok, evidence.Reason);

        Assert.All(stub.Requests, r => Assert.Equal(ProjectA, r.ProjectHeader));
        Assert.Contains(stub.Requests, r => r.Path == "/api/knowledge/ki_68c6b4a77fc81cb5");
        Assert.Contains(stub.Requests, r => r.Path == "/api/knowledge/ki_68c6b4a77fc81cb5/evidence");
    }

    /// <summary>The job direct-id read and the mutation behind it both declare the project.</summary>
    [Fact]
    public async System.Threading.Tasks.Task JobCalls_DeclareTheProjectHeader()
    {
        const string jobBody = """
        {"id":"job_1","project_id":"proj_ef42d498ae1e","status":"running","stages":[],
         "created_at":"2026-09-08T00:00:00Z"}
        """;
        var (provider, stub) = Rig(new RecordingForager()
            .Route("/api/jobs/job_1", jobBody)
            .Route("/api/jobs/job_1/cancel", jobBody));

        var cancel = await provider.CancelJobAsync("job_1", ScopeA, CancellationToken.None);
        Assert.True(cancel.Ok, cancel.Reason);

        Assert.All(stub.Requests, r => Assert.Equal(ProjectA, r.ProjectHeader));
        Assert.Contains(stub.Requests, r => r.Path == "/api/jobs/job_1/cancel");
    }

    /// <summary>A project-rooted path does not need the declaration and does not send one — the
    /// header is a statement about direct-id routes, not noise on every request.</summary>
    [Fact]
    public async System.Threading.Tasks.Task ProjectRootedCalls_SendNoHeader()
    {
        var (provider, stub) = Rig(new RecordingForager()
            .Route($"/api/projects/{ProjectA}/search?q=falcon&limit=10&include_entities=true&include_chunks=false",
                """{"query":"falcon","backend":"sqlite-fts5","took_ms":1,"knowledge":[],"entities":[]}"""));

        var search = await provider.SearchAsync(
            new KnowledgeSearchRequest { Query = "falcon", Scope = ScopeA, Limit = 10 }, CancellationToken.None);
        Assert.True(search.Ok, search.Reason);

        Assert.All(stub.Requests, r => Assert.Null(r.ProjectHeader));
    }
}
