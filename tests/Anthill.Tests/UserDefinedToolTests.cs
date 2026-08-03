using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Tools;
using Xunit;

using FailureClass = Anthill.Core.Contracts.FailureClass;

namespace Anthill.Tests;

/// <summary>
/// v3.4.1 (ADR-006) — tools an operator defines, without a rebuild.
///
/// The exit gate is "a user-registered tool is subject to the same authorization and projection
/// rules as a built-in", and the way that is achieved is by user tools not being special anywhere:
/// a validated definition becomes an ordinary <see cref="ITool"/> in the ordinary registry. So the
/// tests that matter are the ones proving the boundaries did NOT get a hole cut in them for it.
///
/// The URL-substitution tests are the sharpest ones here. Everything else in this feature is
/// declarative and reviewable by reading it; the substitution is the single place where MODEL OUTPUT
/// becomes part of a request, and every classic way that goes wrong — path traversal, query
/// injection, a userinfo '@' relocating the host — is a silent success rather than an error.
/// </summary>
public class UserDefinedToolTests : IDisposable
{
    private static readonly string[] Allowed = { "api.internal.test" };

    private readonly bool _toolsWere = AnthillRuntime.EnableUserTools;
    private readonly IReadOnlyList<string> _hostsWere = AnthillRuntime.UserToolAllowedHosts;

    public UserDefinedToolTests()
    {
        AnthillRuntime.EnableUserTools = true;
        AnthillRuntime.UserToolAllowedHosts = Allowed;
        UserToolGrants.Clear();
    }

    public void Dispose()
    {
        AnthillRuntime.EnableUserTools = _toolsWere;
        AnthillRuntime.UserToolAllowedHosts = _hostsWere;
        UserToolGrants.Clear();
    }

    private static ToolDefinition Http(string name = "fetch_widget",
        string url = "https://api.internal.test/widgets/{id}", string[]? roles = null) => new()
    {
        Name = name,
        Description = "fetches a widget",
        Kind = ToolKind.Http,
        ParametersJson = """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""",
        Config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["url"] = url },
        AllowedRoles = roles ?? Array.Empty<string>(),
    };

    // ---- a definition cannot become an escalation ------------------------------------------------

    /// <summary>
    /// The load-bearing rejection. If a definition could take the name of a built-in, then defining
    /// a tool would be a way to REPLACE apply_patch with something of the operator's — or a model's
    /// — choosing, which turns tool registration into privilege escalation.
    /// </summary>
    [Theory]
    [InlineData("apply_patch")]
    [InlineData("shell_command")]
    [InlineData("write_text_file")]
    [InlineData("system_info")]
    public void ADefinition_MayNotShadowABuiltIn(string name)
    {
        var problems = Http(name).Validate();
        Assert.NotEmpty(problems);
    }

    /// <summary>
    /// Providers reject tool names outside a narrow shape, and a name that a provider silently drops
    /// produces a tool the model is never offered — indistinguishable from a model choosing not to
    /// call it.
    /// </summary>
    [Theory]
    [InlineData("has spaces")]
    [InlineData("Uppercase")]
    [InlineData("ab")]
    [InlineData("9leading_digit")]
    [InlineData("has-a-dash")]
    public void ADefinition_RejectsANameNoProviderWillAccept(string name) =>
        Assert.NotEmpty(Http(name).Validate());

    /// <summary>
    /// A schema that does not parse reaches the provider as a malformed tools array, which most
    /// backends answer by ignoring the tool — silently. Caught at the door instead.
    /// </summary>
    [Fact]
    public void ADefinition_WithAnUnparseableSchema_IsRejected()
    {
        var broken = Http() with { ParametersJson = "{not json" };
        Assert.Contains(broken.Validate(), p => p.Contains("valid JSON"));
    }

    /// <summary>A kind that exists in the enum but is not built says so, rather than failing vaguely.</summary>
    [Fact]
    public void ADeclaredButUnbuiltKind_SaysSo()
    {
        var mcp = Http() with { Kind = ToolKind.Mcp };
        Assert.Contains(mcp.Validate(), p => p.Contains("not built"));
    }

    // ---- the host allowlist is the actual boundary -----------------------------------------------

    /// <summary>
    /// A tool cannot be registered against a host the operator has not allowlisted. Registering a
    /// tool must not be a way to widen what the colony can reach — that decision stays in config,
    /// where a human makes it.
    /// </summary>
    [Fact]
    public void ADefinition_CannotRegisterAgainstANonAllowlistedHost()
    {
        var problems = UserToolRegistrar.Default().Validate(Http(url: "https://evil.example.com/x"));
        Assert.Contains(problems, p => p.Contains("not in user_tool_allowed_hosts"));
    }

    /// <summary>
    /// EXACT host matching. Suffix matching is the classic way an allowlist is defeated:
    /// "example.com" must not match "evil-example.com", and above all must not match
    /// "example.com.attacker.net".
    /// </summary>
    [Theory]
    [InlineData("api.internal.test", true)]
    [InlineData("API.Internal.Test", true)]          // case is not a security boundary
    [InlineData("evil-api.internal.test", false)]
    [InlineData("api.internal.test.attacker.net", false)]
    [InlineData("internal.test", false)]
    public void HostMatching_IsExact(string host, bool allowed) =>
        Assert.Equal(allowed, HttpToolKind.HostIsAllowed(host));

    /// <summary>An empty allowlist reaches nothing. Never "anything" — that is how a gate becomes decoration.</summary>
    [Fact]
    public void AnEmptyAllowlist_AllowsNothing()
    {
        AnthillRuntime.UserToolAllowedHosts = Array.Empty<string>();
        Assert.False(HttpToolKind.HostIsAllowed("api.internal.test"));
    }

    // ---- model output cannot rewrite the request -------------------------------------------------

    /// <summary>
    /// The one place model output enters a URL. Each of these arguments, substituted raw, changes
    /// the request into a different one — a different path, a different query, or with the userinfo
    /// '@' a DIFFERENT HOST that the post-substitution allowlist check would then have to catch.
    /// Encoding means none of them ever gets that far.
    /// </summary>
    [Theory]
    [InlineData("../../admin")]
    [InlineData("1?admin=true")]
    [InlineData("1#fragment")]
    [InlineData("evil.example.com/x")]
    [InlineData("@evil.example.com")]
    [InlineData("1&role=root")]
    public void ASubstitutedArgument_CannotRestructureTheUrl(string hostile)
    {
        var url = HttpToolKind.Substitute("https://api.internal.test/widgets/{id}",
            new Dictionary<string, object?> { ["id"] = hostile });

        var uri = new Uri(url);

        // the host is the definition's, whatever the argument tried to do
        Assert.Equal("api.internal.test", uri.Host);
        // exactly ONE path segment beyond /widgets/ — no traversal, no extra segments
        Assert.Equal(new[] { "widgets" },
            uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[..1]);
        Assert.Equal(2, uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length);
        // and nothing leaked into the query or the fragment
        Assert.Equal("", uri.Query);
        Assert.Equal("", uri.Fragment);
    }

    /// <summary>
    /// A missing argument THROWS rather than resolving to empty. Silently producing
    /// "/widgets/" yields a 404 far from the actual mistake, and a model told "404" learns the wrong
    /// lesson about a call it very nearly made correctly.
    /// </summary>
    [Fact]
    public void AMissingArgument_IsAnError_NotAnEmptySlot()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            HttpToolKind.Substitute("https://api.internal.test/widgets/{id}",
                new Dictionary<string, object?>()));

        Assert.Contains("id", error.Message);
    }

    // ---- registration and authorization ----------------------------------------------------------

    private static ToolRegistry Registry() => new(new SqliteMemory(":memory:"));

    /// <summary>
    /// The gate itself: a registered user tool is dispatchable through the ordinary path, under the
    /// ordinary enforcer, with no special case anywhere.
    /// </summary>
    [Fact]
    public void ARegisteredUserTool_IsDispatchableByAnyRole_WhenItGrantsNone()
    {
        var registry = Registry();
        var results = UserToolRegistrar.Default().RegisterAll(registry, new[] { Http() });

        Assert.True(Assert.Single(results).Registered);
        Assert.Contains("fetch_widget", registry.Names);
        // "any role that can use the tool may use it" — the operator's stated intent
        Assert.True(ToolAuthorization.Evaluate("researcher", "fetch_widget").Allowed);
        Assert.True(ToolAuthorization.Evaluate("coder", "fetch_widget").Allowed);
    }

    /// <summary>A named grant NARROWS: roles outside the list are refused.</summary>
    [Fact]
    public void ANamedGrant_ExcludesEveryOtherRole()
    {
        UserToolRegistrar.Default().RegisterAll(Registry(), new[] { Http(roles: new[] { "researcher" }) });

        Assert.True(ToolAuthorization.Evaluate("researcher", "fetch_widget").Allowed);
        Assert.False(ToolAuthorization.Evaluate("coder", "fetch_widget").Allowed);
    }

    /// <summary>
    /// Grants only ever widen the TOOL SET, never what a role may do. An unknown identity is still
    /// refused everything else, so a definition cannot be used to make spoofing a name worthwhile.
    /// </summary>
    [Fact]
    public void AGrant_DoesNotWidenAnythingElse()
    {
        UserToolRegistrar.Default().RegisterAll(Registry(), new[] { Http() });

        Assert.False(ToolAuthorization.Evaluate("researcher", "apply_patch").Allowed);
        Assert.False(ToolAuthorization.Evaluate("not-a-real-ant", "shell_command").Allowed);
    }

    /// <summary>
    /// With the feature off, definitions are INERT rather than registered-and-refusing. A tool that
    /// is offered to a model and always denies wastes a turn to teach it nothing.
    /// </summary>
    [Fact]
    public void WithTheFeatureOff_NothingIsRegisteredOrGranted()
    {
        AnthillRuntime.EnableUserTools = false;
        var registry = Registry();

        var results = UserToolRegistrar.Default().RegisterAll(registry, new[] { Http() });

        Assert.False(Assert.Single(results).Registered);
        Assert.DoesNotContain("fetch_widget", registry.Names);
        Assert.False(ToolAuthorization.Evaluate("researcher", "fetch_widget").Allowed);
    }

    /// <summary>
    /// The wholesale-replacement property. A definition removed since the last load must stop being
    /// granted — merging is how a revoked tool keeps working forever.
    /// </summary>
    [Fact]
    public void ARemovedDefinition_StopsBeingGranted()
    {
        var registry = Registry();
        var registrar = UserToolRegistrar.Default();

        registrar.RegisterAll(registry, new[] { Http() });
        Assert.True(ToolAuthorization.Evaluate("researcher", "fetch_widget").Allowed);

        registrar.RegisterAll(registry, Array.Empty<ToolDefinition>());
        Assert.False(ToolAuthorization.Evaluate("researcher", "fetch_widget").Allowed);
    }

    /// <summary>
    /// One bad definition must not stop the good ones loading. A colony that refuses to start
    /// because a stored tool has a bad URL cannot be fixed without hand-editing the database.
    /// </summary>
    [Fact]
    public void OneBadDefinition_DoesNotBlockTheRest()
    {
        var registry = Registry();
        var results = UserToolRegistrar.Default().RegisterAll(registry, new[]
        {
            Http("broken_tool", "https://evil.example.com/x"),
            Http("good_tool"),
        });

        Assert.False(results.Single(r => r.Name == "broken_tool").Registered);
        Assert.True(results.Single(r => r.Name == "good_tool").Registered);
        Assert.Contains("good_tool", registry.Names);
    }

    /// <summary>
    /// Registration and enablement are separate lifetimes: an operator revoking a tool mid-mission
    /// expects the next call to fail, and to be able to tell it was revoked rather than broken.
    /// </summary>
    [Fact]
    public void ADisabledDefinition_RefusesRatherThanRuns()
    {
        var tool = new UserDefinedTool(Http() with { Enabled = false }, new HttpToolKind());
        var result = tool.Run(new Dictionary<string, object?> { ["id"] = "1" });

        Assert.False(result.Success);
        Assert.Equal(FailureClass.AuthorizationFailure, result.Failure);
    }

    /// <summary>
    /// A user tool's failures are classified like any other tool's — the contract shipped one
    /// increment earlier applies here without a special case, which is the point.
    /// </summary>
    [Fact]
    public void AUserToolFailure_IsClassified()
    {
        var result = new HttpToolKind().Execute(Http(), new Dictionary<string, object?>());

        Assert.False(result.Success);
        Assert.Equal(FailureClass.ValidationFailure, result.Failure);   // missing {id}
        Assert.False(result.Retryable);
    }

    // ---- projection -------------------------------------------------------------------------------

    /// <summary>
    /// The other half of the gate: a granted user tool is OFFERED to the model by the same
    /// projection that offers built-ins, carrying its own schema.
    /// </summary>
    [Fact]
    public void AGrantedUserTool_IsOfferedToTheModel_WithItsSchema()
    {
        var registry = Registry();
        UserToolRegistrar.Default().RegisterAll(registry, new[] { Http() });

        var offered = ToolSchemaProjection.For(registry, "researcher");
        var spec = Assert.Single(offered.Where(t => t.Name == "fetch_widget"));

        Assert.Equal("fetches a widget", spec.Description);
        Assert.Contains("\"id\"", spec.ParametersJson);
    }

    /// <summary>And a tool whose grant excludes the role is never offered to it.</summary>
    [Fact]
    public void AUserToolIsNotOffered_ToARoleItDoesNotGrant()
    {
        var registry = Registry();
        UserToolRegistrar.Default().RegisterAll(registry, new[] { Http(roles: new[] { "researcher" }) });

        Assert.DoesNotContain(ToolSchemaProjection.For(registry, "coder"), t => t.Name == "fetch_widget");
    }

    // ---- persistence -------------------------------------------------------------------------------

    /// <summary>
    /// Definitions survive a restart, or the feature is a demo: a tool used in a mission would
    /// vanish on the next process start and its transcript would become unexplainable.
    /// </summary>
    [Fact]
    public void ADefinition_SurvivesARestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anthill-usertools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "memory.db");

        try
        {
            using (var memory = new SqliteMemory(db))
                memory.SaveToolDefinition(Http(roles: new[] { "researcher" }));

            using var reopened = new SqliteMemory(db);
            var loaded = Assert.Single(reopened.LoadToolDefinitions());

            Assert.Equal("fetch_widget", loaded.Name);
            Assert.Equal(ToolKind.Http, loaded.Kind);
            Assert.Equal("https://api.internal.test/widgets/{id}", loaded.Config["url"]);
            Assert.Equal(new[] { "researcher" }, loaded.AllowedRoles);
            Assert.True(loaded.Enabled);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// Revoking KEEPS the row. Revoked and never-defined are different facts, and only the stored
    /// row can tell an audit which one explains a transcript that called the tool.
    /// </summary>
    [Fact]
    public void DisablingKeepsTheRow_SoTheAuditStillExplainsTheTranscript()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anthill-usertools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            using var memory = new SqliteMemory(Path.Combine(dir, "memory.db"));
            memory.SaveToolDefinition(Http());

            Assert.True(memory.SetToolDefinitionEnabled("fetch_widget", false));
            Assert.False(Assert.Single(memory.LoadToolDefinitions()).Enabled);

            Assert.True(memory.DeleteToolDefinition("fetch_widget"));
            Assert.Empty(memory.LoadToolDefinitions());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The registry will not let a runtime call strip a built-in. Registration composes the run's
    /// capabilities from config; a second, unaudited way to change them would defeat that.
    /// </summary>
    [Fact]
    public void Unregister_RefusesBuiltIns()
    {
        var registry = Registry();
        registry.Register(new SystemInfoTool());
        UserToolRegistrar.Default().RegisterAll(registry, new[] { Http() });

        Assert.False(registry.Unregister("system_info"));
        Assert.Contains("system_info", registry.Names);

        Assert.True(registry.Unregister("fetch_widget"));
        Assert.DoesNotContain("fetch_widget", registry.Names);
    }
}
