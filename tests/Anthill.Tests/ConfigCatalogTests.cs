using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE CONFIGURATION SURFACE HAS ONE AUTHORITY, AND THIS IS WHAT MAKES THAT TRUE. v0.3.8.114 —
/// R0's fourth exit-gate clause.
///
/// `ConfigurationSurfaceTests` (v0.3.8.91) pinned `config.example.json` against the runtime in both
/// directions and said what it was not: "a generated schema ... is the real end state and is named
/// in PLAN.md as its own piece of work. This is the guard that stops the drift getting worse in the
/// meantime." Three of its tests are retired here, because a file GENERATED from the catalog cannot
/// document a key the runtime does not parse, and cannot omit one without a declared reason — the
/// properties those tests defended are now structural rather than checked.
///
/// WHAT THE HAND-KEPT VERSION MISSED, found while replacing it: `dashboard_workspace_enabled` was in
/// `config.example.json` AND on the deliberately-undocumented ledger. Neither guard could see it —
/// one asserted example ⊆ parsed, the other parsed ⊆ example ∪ ledger, and a key in both satisfies
/// both directions. Two lists, one fact, and the contradiction sat in the checks written to prevent
/// exactly that.
/// </summary>
public class ConfigCatalogTests
{
    private static string RepoFile(params string[] parts) =>
        Path.Combine(new[] { SourceText.RepoRoot() }.Concat(parts).ToArray());

    /// <summary>
    /// THE EXAMPLE FILE IS THE CATALOG'S OUTPUT, BYTE FOR BYTE.
    ///
    /// Not "contains the same keys" — that is the adjacent question, and it would pass while an
    /// operator read a stale default. Regenerating and comparing is the only version that cannot be
    /// satisfied by a file that merely looks right.
    /// </summary>
    [Fact]
    public void TheExampleFile_IsWhatTheCatalogRenders()
    {
        var path = RepoFile("config.example.json");
        var onDisk = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var rendered = ConfigCatalog.RenderExampleJson().Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.True(onDisk == rendered,
            "config.example.json is no longer what ConfigCatalog renders. It is a GENERATED file: "
          + "change the property's [ConfigKey] attribute in AnthillConfig.cs, then regenerate with\n"
          + "    dotnet run --project src/Anthill.Cli -- --emit-config\n"
          + "Do not edit it by hand — the edit is what this test exists to catch.\n"
          + FirstDifference(onDisk, rendered));
    }

    /// <summary>The operator's configuration reference, likewise generated and likewise compared.</summary>
    [Fact]
    public void TheConfigurationReference_IsWhatTheCatalogRenders()
    {
        var path = RepoFile("docs", "CONFIGURATION.md");
        Assert.True(File.Exists(path),
            "docs/CONFIGURATION.md is missing. Generate it with "
          + "`dotnet run --project src/Anthill.Cli -- --emit-config`.");

        var onDisk = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var rendered = ConfigCatalog.RenderMarkdown().Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.True(onDisk == rendered,
            "docs/CONFIGURATION.md is no longer what ConfigCatalog renders. Regenerate with "
          + "`dotnet run --project src/Anthill.Cli -- --emit-config`.\n"
          + FirstDifference(onDisk, rendered));
    }

    /// <summary>
    /// THE CATALOG SEES EVERY PARSED KEY. The vacuity floor for everything above: a reflection
    /// filter that stopped matching would render an empty file, and an empty file compares equal to
    /// an empty file. This suite has caught that shape in six separate forms now.
    /// </summary>
    [Fact]
    public void TheCatalog_CoversTheWholeParsedSurface()
    {
        var parsed = typeof(AnthillConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(parsed.Count >= 100,
            $"only {parsed.Count} settings were found on AnthillConfig. The reflection filter has "
          + "stopped seeing the surface it measures.");

        var declared = ConfigCatalog.Declarations.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);

        Assert.True(parsed.SetEquals(declared),
            "the catalog and the parsed surface disagree.\n  catalog only: "
          + string.Join(", ", declared.Except(parsed).OrderBy(k => k, StringComparer.Ordinal))
          + "\n  parsed only: "
          + string.Join(", ", parsed.Except(declared).OrderBy(k => k, StringComparer.Ordinal)));
    }

    /// <summary>
    /// THE EDITABLE SET DID NOT CHANGE WHEN IT STOPPED BEING A LIST.
    ///
    /// The projection replaced a hand-kept HashSet of 98 names. A migration that quietly widened
    /// what the settings surface may write would be a security change wearing a refactor's clothes,
    /// so the count is pinned to what was measured at the moment of the move. Raising it is a
    /// deliberate act: change this number in the same commit and say why.
    ///
    /// 98 -> 99 AT v0.3.8.124: `knowledge_enabled`. Tools &gt; Knowledge described the FORAGER
    /// integration and then told the operator to go and edit JSON to use it. The switch decides
    /// whether the module talks to the endpoint the file ALREADY names, under the token the file
    /// already holds, within the scopes `knowledge_project_map` already grants — it can neither
    /// redirect what the colony trusts nor widen what a mission may read, which is what the rest of
    /// that section is FileOnly to prevent. Those four keys did not move and
    /// `KnowledgeGateTests.TheEndpointTokenScopeAndRemotePermission_StayInTheFile` holds them there.
    /// </summary>
    [Fact]
    public void TheEditableSurface_IsExactlyWhatItWasBeforeItBecameAProjection()
    {
        var editable = ConfigCatalog.EditableKeys;

        Assert.True(editable.Count == 99,
            $"the settings surface now exposes {editable.Count} writable keys; it exposed 98 when "
          + "the hand-kept set was replaced by a projection at v0.3.8.114, and 99 since "
          + "v0.3.8.124 added `knowledge_enabled`. Widening what an operator can change live "
          + "without a restart is a decision, not a side effect — say so here and in the "
          + "changelog.\n  " + string.Join("\n  ", editable.OrderBy(k => k, StringComparer.Ordinal)));

        // And it agrees with the runtime's own answer, which is what ApplySettingsUpdate consults.
        Assert.Equal(
            editable.OrderBy(k => k, StringComparer.Ordinal),
            AnthillRuntime.EditableSettingKeys.OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>
    /// A SECRET NEVER RENDERS A VALUE. An example file carrying a plausible-looking credential is
    /// how a placeholder reaches production, and how a real one reaches a public repository.
    /// </summary>
    [Fact]
    public void NoSecret_RendersItsValueIntoAGeneratedArtifact()
    {
        var secrets = ConfigCatalog.Declarations
            .Where(d => d.Security == ConfigSecurity.Secret && d.IsDocumented)
            .ToList();

        // THREE, and the number is small because the classification was tightened rather than
        // scattered. The first pass classed `api_token_env` and the four `homelab_*_credential_id`
        // keys as Secret; neither holds one. `api_token_env` holds the NAME of an environment
        // variable, and a credential id is a REFERENCE into the credential store —
        // `_comment_homelab_virtualization` says so in the file itself: "the secret lives in the
        // credential store (referenced by id, never here)". Classing a reference as a secret blanks
        // a useful example and teaches nobody anything.
        //
        // What is left is the three webhook URLs, which really do carry their token in the string.
        Assert.True(secrets.Count >= 3,
            $"only {secrets.Count} settings are classed Secret. The three webhook URLs are what "
          + "this rule exists for; if the classification stopped applying, this test is watching "
          + "nothing.");

        var json = ConfigCatalog.RenderExampleJson();
        foreach (var secret in secrets)
            Assert.Contains($"\"{secret.Key}\": \"\"", json, StringComparison.Ordinal);

        // AND AN ILLUSTRATION CANNOT REOPEN THE HOLE. `RenderedJson` blanks a Secret before it
        // consults `ExampleJson`, so declaring one on a secret key cannot put a value in the file.
        // Asserted rather than trusted, because the first draft got this the other way round.
        var withIllustration = ConfigCatalog.Declarations
            .Where(d => d.Security == ConfigSecurity.Secret && !string.IsNullOrEmpty(d.ExampleJson))
            .ToList();

        foreach (var declaration in withIllustration)
            Assert.Equal("\"\"", declaration.RenderedJson(new JsonSerializerOptions()));
    }

    /// <summary>
    /// EVERY DECLARED ENVIRONMENT OVERRIDE IS ONE THE RUNTIME ACTUALLY READS.
    ///
    /// A documented override nothing reads is worse than an undocumented one: an operator sets it,
    /// sees no effect, and concludes the setting does not work. Compiled inspection is not available
    /// here — the variable names are string literals inside `ProjectConfig` — so this is a source
    /// scan, which `docs/GUARDS.md` permits as the last resort provided it resolves a name rather
    /// than a shape, and does not slice by a character count. It does neither.
    /// </summary>
    [Fact]
    public void EveryDeclaredEnvironmentOverride_IsReadByTheRuntime()
    {
        var runtime = SourceText.CodeOnly(File.ReadAllText(
            RepoFile("src", "Anthill.Core", "Configuration", "AnthillRuntime.cs")));

        var declared = ConfigCatalog.Declarations
            .Where(d => !string.IsNullOrEmpty(d.EnvOverride))
            .ToList();

        Assert.True(declared.Count >= 4,
            $"only {declared.Count} environment overrides are declared; the catalog has stopped "
          + "carrying them.");

        var unread = declared
            .Where(d => !runtime.Contains($"\"{d.EnvOverride}\"", StringComparison.Ordinal))
            .Select(d => $"{d.Key} -> {d.EnvOverride}")
            .ToList();

        Assert.True(unread.Count == 0,
            "these settings declare an environment override the runtime never reads, so an operator "
          + "setting it sees nothing happen: " + string.Join(", ", unread));
    }

    /// <summary>
    /// A DECLARED RANGE IS ENFORCED SOMEWHERE. `api_port` is the one that has a range and the one
    /// that had the bug: v0.3.8.91 found `ANTHILL_PORT=0` and `70000` reaching Kestrel unclamped.
    /// A range in the catalog that nothing enforces is documentation of a guarantee that does not
    /// exist.
    /// </summary>
    [Fact]
    public void TheDeclaredPortRange_IsTheOneTheRuntimeEnforces()
    {
        var port = ConfigCatalog.Find("api_port");
        Assert.NotNull(port);
        Assert.True(port!.HasRange, "api_port no longer declares a range; the v0.3.8.91 clamp is unpinned.");
        Assert.Equal(1, port.Min);
        Assert.Equal(65535, port.Max);

        var runtime = SourceText.CodeOnly(File.ReadAllText(
            RepoFile("src", "Anthill.Core", "Configuration", "AnthillRuntime.cs")));

        Assert.Contains("envPort is >= 1 and <= 65535", runtime, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE UNDOCUMENTED LEDGER STILL EXISTS, AND IT IS STILL A DECISION — it just lives on the
    /// property now instead of in a test file a thousand lines away from it.
    /// </summary>
    [Fact]
    public void EveryUndocumentedKey_SaysWhy()
    {
        var hidden = ConfigCatalog.Declarations.Where(d => !d.IsDocumented).ToList();

        Assert.True(hidden.Count is >= 15 and <= 40,
            $"{hidden.Count} settings are kept out of the example file. 23 were at v0.3.8.114; a "
          + "number far from that means either the ledger stopped being read or a batch of settings "
          + "was hidden without anyone deciding to.");

        foreach (var declaration in hidden)
            Assert.False(string.IsNullOrWhiteSpace(declaration.UndocumentedBecause),
                $"{declaration.Key} is kept out of the example file with no reason given.");

        // And the reference page still tells an operator they exist. Hiding a setting from the file
        // an operator edits is a decision; hiding it from the documentation entirely is a gap.
        var markdown = ConfigCatalog.RenderMarkdown();
        foreach (var declaration in hidden)
            Assert.Contains(declaration.Key, markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE EXAMPLE FILE NEVER SHOWS A SAFETY GATE SWITCHED ON. v0.3.8.114, and it is a finding
    /// rather than a precaution.
    ///
    /// `config.example.json` is the file an operator copies to `config.json`. Before this release it
    /// showed `shell_tool_enabled`, `file_writing_enabled`, `patch_application_enabled` and
    /// `web_search_enabled` as `true` while the colony ships all four `false` — and its own
    /// `_comment_features` said, four lines above, "All feature gates default safe. Enable what you
    /// need." The prose promised one thing and the values below it did another, so copying the
    /// example turned on shell execution, file writing and patch application in one step, for
    /// somebody who believed they were accepting defaults.
    ///
    /// That is v0.3.8.91's roster finding pointed the other way: there, seven flags LOOKED off and
    /// ran on; here four look on while the colony ships them off. Both are the example file
    /// disagreeing with the runtime, and both mislead in the direction of more authority than the
    /// operator chose.
    ///
    /// So a Safety-class gate renders its shipped default, always. An illustration is for teaching
    /// somebody what a value looks like — a host, a model name, a route table — never for handing
    /// them a capability they did not ask for.
    /// </summary>
    [Fact]
    public void NoSafetyGate_IsIllustratedAsEnabled()
    {
        var gates = ConfigCatalog.Declarations
            .Where(d => d.Security == ConfigSecurity.Safety)
            .Where(d => d.ClrType == typeof(bool) || d.ClrType == typeof(bool?))
            .ToList();

        Assert.True(gates.Count >= 20,
            $"only {gates.Count} boolean safety gates are classified. The Security classification "
          + "has stopped being applied, and this guard is watching nothing.");

        var illustrated = gates
            .Where(d => !string.IsNullOrEmpty(d.ExampleJson))
            .Where(d => d.ExampleJson.Contains("true", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(illustrated.Count == 0,
            "config.example.json would show these safety gates ENABLED while the colony ships them "
          + "disabled: " + string.Join(", ", illustrated)
          + ". An operator copies this file. Showing a gate on is granting a capability they did "
          + "not choose — and the file's own _comment_features promises the opposite.");

        // And the rendered artifact agrees, which is the part an operator actually reads.
        var json = ConfigCatalog.RenderExampleJson();
        foreach (var gate in new[]
                 { "shell_tool_enabled", "file_writing_enabled", "patch_application_enabled" })
        {
            var declaration = ConfigCatalog.Find(gate);
            Assert.NotNull(declaration);
            Assert.Contains($"\"{gate}\": false", json, StringComparison.Ordinal);
        }
    }

    private static string FirstDifference(string onDisk, string rendered)
    {
        var a = onDisk.Split('\n');
        var b = rendered.Split('\n');

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var left = i < a.Length ? a[i] : "(end of file)";
            var right = i < b.Length ? b[i] : "(end of file)";
            if (!string.Equals(left, right, StringComparison.Ordinal))
                return $"\nfirst difference at line {i + 1}:\n  on disk : {Trim(left)}\n  rendered: {Trim(right)}";
        }

        return "";
    }

    private static string Trim(string line) => line.Length <= 160 ? line : line[..160] + "…";
}
