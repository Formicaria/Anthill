using System.Text.Json;
using System.Text.RegularExpressions;
using Anthill.Api;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE KNOWLEDGE SWITCH IS A SWITCH. v0.3.8.124.
///
/// Tools &gt; Knowledge has explained the FORAGER integration since v0.3.8.121 and then told the
/// operator to go and edit `config.json` to use it, because `knowledge_enabled` was declared
/// FileOnly — not measured and found to need it, but inherited from a section rule written for the
/// endpoint and the scope map. This release gives the page a toggle, and these guards exist because
/// a settings toggle has one classic failure and this repository has shipped it twice:
///
///   THE BUTTON THAT WRITES NOTHING. `ApplySettingsUpdate` skips any key `ConfigCatalog` does not
///   call editable, silently and with a success response. v0.3.8.96 found it live on
///   `acting_coder_enabled`; v0.3.8.97 found it again, a release apart, on `workspace_checks` and
///   `objective_verification_enabled`. Three switches an operator was told to flip that the surface
///   declined to write. So the first test here does not assert an attribute — it drives the real
///   update path and reads the live runtime gate on the other side.
///
/// And the widening is pinned in BOTH directions. Exactly one key crossed; the four that decide who
/// the colony trusts and what a mission may read did not, and the negative test is what keeps a
/// later "while I'm here" from taking the endpoint or the remote permission with it.
/// </summary>
public class KnowledgeGateTests : IDisposable
{
    private readonly bool _saved;

    public KnowledgeGateTests()
    {
        AnthillRuntime.Initialize();
        _saved = AnthillRuntime.Knowledge.Enabled;
    }

    // Restored through the same path the console uses, so a test cannot leave the runtime gate and
    // the persisted config disagreeing with each other.
    public void Dispose() => Gate(_saved);

    private static List<string> Gate(bool on) =>
        AnthillRuntime.ApplySettingsUpdate(new Dictionary<string, JsonElement>
        {
            [ApiHost.KnowledgeGateKey] = JsonSerializer.SerializeToElement(on),
        });

    private static string ConsoleSource() => File.ReadAllText(
        Path.Combine(SourceText.RepoRoot(), "src", "Anthill.UI", "knowledge.js"));

    // ---- The whole chain, driven rather than asserted about ------------------------------------

    /// <summary>
    /// POSTING THE GATE ACTUALLY MOVES THE COLONY'S GATE, in both directions.
    ///
    /// The black-box form the guard hierarchy asks for: `ApplySettingsUpdate` is the method behind
    /// `POST /settings`, and `AnthillRuntime.Knowledge.Enabled` is the value `KnowledgeModule` reads
    /// on every call. A test that only checked `IsEditable` would pass on a key that was editable
    /// and then dropped by the projection, which is the same button doing nothing for a different
    /// reason one layer down.
    /// </summary>
    [Fact]
    public void TogglingTheGate_ReachesTheRuntimeThatDecidesWhetherKnowledgeRuns()
    {
        // The one environment that would make this test lie. Named rather than skipped: a build
        // machine exporting the variable pins the gate, and the right outcome is a failure that
        // says so in one line, not a green run that proved nothing.
        Assert.False(ApiHost.KnowledgeGateEnvPinned,
            $"{ApiHost.KnowledgeGateEnvVar} is set in this environment, which overrides the config "
          + "file — so no settings write can move the gate here and this guard cannot measure "
          + "anything. Unset it for the test run.");

        var applied = Gate(true);
        Assert.Contains(ApiHost.KnowledgeGateKey, applied);
        Assert.True(AnthillRuntime.Knowledge.Enabled,
            "the settings surface reported knowledge_enabled applied and the runtime gate is still "
          + "off. The key is being accepted and then lost between the merge and the projection.");

        applied = Gate(false);
        Assert.Contains(ApiHost.KnowledgeGateKey, applied);
        Assert.False(AnthillRuntime.Knowledge.Enabled,
            "knowledge could be switched on from the console but not off again.");
    }

    /// <summary>
    /// Turning the gate on CHANGES NOTHING ELSE about the integration. The endpoint, the token
    /// permission and the scope map are what they were, because the switch's entire blast radius is
    /// "start using what the file already says" — which is the argument that made it editable, so it
    /// is asserted rather than left as an argument.
    /// </summary>
    [Fact]
    public void TheGate_ChangesNothingButWhetherTheIntegrationRuns()
    {
        Gate(false);
        var before = AnthillRuntime.Knowledge;
        var (endpoint, allowRemote, scopes) =
            (before.Endpoint, before.AllowRemote, before.ProjectMap.Count);

        Gate(true);
        var after = AnthillRuntime.Knowledge;

        Assert.Equal(endpoint, after.Endpoint);
        Assert.Equal(allowRemote, after.AllowRemote);
        Assert.Equal(scopes, after.ProjectMap.Count);
    }

    // ---- What did NOT cross ---------------------------------------------------------------------

    /// <summary>
    /// THE ENDPOINT, THE TOKEN, THE REMOTE PERMISSION AND THE SCOPE MAP STAY A FILE EDIT.
    ///
    /// These are the keys the section was made FileOnly for. `knowledge_forager_endpoint` decides
    /// which service the colony trusts as the source of organizational fact; `knowledge_project_map`
    /// decides which knowledge a mission may read; `knowledge_forager_allow_remote` permits sending
    /// the colony's queries to a service that has no authentication of its own, across a network.
    /// Each is a security decision that a compromised console must not be able to make, and none of
    /// them is made easier to reach by the switch above being writable.
    /// </summary>
    [Theory]
    [InlineData("knowledge_forager_endpoint")]
    [InlineData("knowledge_forager_token")]
    [InlineData("knowledge_forager_allow_remote")]
    [InlineData("knowledge_project_map")]
    [InlineData("knowledge_default_project")]
    public void TheEndpointTokenScopeAndRemotePermission_StayInTheFile(string key)
    {
        Assert.NotNull(ConfigCatalog.Find(key));
        Assert.False(ConfigCatalog.IsEditable(key),
            $"{key} became live-writable from the console. v0.3.8.124 moved exactly one knowledge "
          + "key across that line — the on/off switch, because it only starts using what the file "
          + "already says. This key decides who the colony trusts or what a mission may read, which "
          + "is a different decision: if it is genuinely meant to move, say why on the property and "
          + "update TheEditableSurface_IsExactlyWhatItWasBeforeItBecameAProjection in the same "
          + "commit.");
    }

    // ---- The console and the catalog, held to the same key --------------------------------------

    /// <summary>
    /// EVERY KNOWLEDGE KEY THE CONSOLE POSTS IS ONE THE SURFACE WILL WRITE.
    ///
    /// The source scan `docs/GUARDS.md` permits as a last resort: the console is JavaScript, its
    /// settings keys are string literals, and no compiled inspection can reach them. It resolves a
    /// NAME rather than a shape — each key found is looked up in the catalog — so a renamed key
    /// fails here instead of becoming another button that reports success and writes nothing.
    ///
    /// The vacuity floor is explicit: the scan must find the gate key itself. A regex that stopped
    /// matching would otherwise iterate an empty set and pass.
    /// </summary>
    [Fact]
    public void EveryKnowledgeKeyTheConsoleWrites_IsOneTheSettingsSurfaceAccepts()
    {
        var console = ConsoleSource();

        // Keys as they appear in a posted object literal: `knowledge_enabled: !!on`. Deliberately
        // not every mention of the string — the page NAMES several file-only keys in its prose to
        // tell the operator where to go, and naming one is the opposite of writing it.
        var posted = Regex.Matches(console, @"\b(knowledge_[a-z0-9_]+)\s*:")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(ApiHost.KnowledgeGateKey, posted);

        foreach (var key in posted)
            Assert.True(ConfigCatalog.IsEditable(key),
                $"knowledge.js posts `{key}` to /settings, and the settings surface does not accept "
              + "it. ApplySettingsUpdate skips a non-editable key silently and still answers "
              + "success, so this ships as a control that appears to work.");
    }

    /// <summary>
    /// AND THE ONE IT CANNOT OFFER, IT NAMES.
    ///
    /// A non-loopback endpoint with `knowledge_forager_allow_remote` off fails at the client with
    /// "refusing a non-loopback knowledge request" — after the operator has enabled knowledge and is
    /// looking at an unreachable knowledge base. The page has to say which key, because the
    /// alternative is an operator debugging FORAGER for a decision ANTHILL made deliberately and
    /// declined to explain.
    /// </summary>
    [Fact]
    public void TheConsole_NamesTheRemotePermissionItDeliberatelyWillNotToggle()
    {
        var console = ConsoleSource();

        Assert.Contains("knowledge_forager_allow_remote", console, StringComparison.Ordinal);
        Assert.DoesNotContain("knowledge_forager_allow_remote:", console, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE STATUS ROUTE AND THE CATALOG AGREE ON THE ENVIRONMENT VARIABLE.
    ///
    /// `AnthillRuntime` projects this gate as env-over-file, so on a colony that exports the
    /// variable a settings write persists and then loses to it — a toggle that appears to do
    /// nothing, which is the same defect the tests above exist for, arriving by a different door.
    /// The console withholds the control and names the variable instead, and it can only name the
    /// right one because `ApiHost` reads it from the declaration rather than spelling it a second
    /// time. This is what pins that.
    /// </summary>
    [Fact]
    public void TheEnvironmentPinTheConsoleReports_IsTheOneTheRuntimeActuallyReads()
    {
        Assert.Equal("ANTHILL_KNOWLEDGE_ENABLED", ApiHost.KnowledgeGateEnvVar);
        Assert.Equal(
            ConfigCatalog.Find(ApiHost.KnowledgeGateKey)!.EnvOverride,
            ApiHost.KnowledgeGateEnvVar);

        // And the runtime really does read it, rather than the catalog documenting an override
        // nothing consults — the failure that would make the console's explanation false while
        // every string in it matched.
        var runtime = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Configuration", "AnthillRuntime.cs")));
        Assert.Contains($"Env(\"{ApiHost.KnowledgeGateEnvVar}\")", runtime, StringComparison.Ordinal);
    }
}
