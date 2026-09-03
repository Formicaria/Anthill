using System.Text.Json;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The operator's configuration surface agrees with the runtime that reads it. v0.3.8.91.
///
/// WHAT THE SWEEP FOUND. `config.example.json` is the documented surface and nothing compared it to
/// anything: 25 parsed keys it never mentioned, three keys it documents that no code reads, and —
/// the one that mattered — seven specialist-ant flags shown as `false` which the roster migration
/// then forces to `true` at runtime. An operator reading the example would have believed those ants
/// were off. The only working controls, `roster_profile` and `disabled_roles`, appeared nowhere in it.
///
/// THAT GENERATED SCHEMA LANDED AT v0.3.8.114, and three of this class's tests went with it. A file
/// RENDERED from `ConfigCatalog` cannot document a key the runtime does not parse, and cannot omit
/// one without a declared reason, so the two directions this class used to assert are structural
/// now rather than checked — see `ConfigCatalogTests`. The deliberately-undocumented ledger moved
/// onto the properties as `[ConfigKey(UndocumentedBecause = …)]`.
///
/// What stays here is everything the catalog does not answer: that the roster controls appear in
/// the example at all, and the four runtime BEHAVIOURS v0.3.8.91 fixed — blank environment
/// variables treated as absent, the api-token fallback that was itself, an unreadable config
/// stopping the server, and the migration result reaching an operator.
/// </summary>
public class ConfigurationSurfaceTests
{
    private static Dictionary<string, JsonElement> Example()
    {
        var text = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "config.example.json"));
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text)!;
    }



    /// <summary>
    /// THE ROSTER CONTROLS ARE DOCUMENTED, because the flags beside them do not do what they look
    /// like they do.
    ///
    /// A file with no `config_schema_version` and present-but-false specialist flags is treated as
    /// unmigrated, adopts the `full` roster profile, and every one of those flags is forced true at
    /// runtime. `config.example.json` is exactly that file. So the seven flags it showed as `false`
    /// described a colony nobody was running, and the two settings that would actually have turned
    /// the ants off were not in the file at all.
    /// </summary>
    [Fact]
    public void TheRealRosterControls_AppearInTheExample()
    {
        var example = Example();

        Assert.True(example.ContainsKey("roster_profile"),
            "config.example.json still shows seven specialist flags as false without documenting "
          + "roster_profile, which is what actually decides whether those ants run.");
        Assert.True(example.ContainsKey("disabled_roles"),
            "disabled_roles is the per-role off switch and the example does not mention it.");
    }

    /// <summary>
    /// AN ENV VAR SET TO THE EMPTY STRING DOES NOT WIN.
    ///
    /// `??` tests for null, and `ANTHILL_OLLAMA_MODEL=` in a compose file is not null. The empty
    /// string used to override a configured value with nothing — while the documentation promised
    /// "highest precedence", meaning precedence for a value the operator had actually set.
    /// </summary>
    [Fact]
    public void TheEnvironmentOverrides_TreatBlankAsAbsent()
    {
        var code = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Configuration", "AnthillRuntime.cs")));

        foreach (var variable in new[]
                 { "ANTHILL_HOST", "ANTHILL_OLLAMA_MODEL", "ANTHILL_OLLAMA_HOST", "ANTHILL_PORT" })
            Assert.False(
                code.Contains($"Environment.GetEnvironmentVariable(\"{variable}\") ??", StringComparison.Ordinal),
                $"{variable} is read with `??`, so setting it to the empty string overrides the "
              + "configured value with nothing. Read it through the blank-aware helper.");
    }

    /// <summary>
    /// THE API TOKEN COMES FROM THE VARIABLE THE OPERATOR NAMED, AND NOWHERE ELSE.
    ///
    /// The fallback used to be the static's own prior value — which is initialised from
    /// `ANTHILL_API_TOKEN`. So repointing `api_token_env` at a variable you had not set kept
    /// authenticating against the one you had just stopped using, and because `ProjectConfig` re-runs
    /// on every settings update the value was sticky: once set it could never be cleared.
    /// </summary>
    [Fact]
    public void TheApiToken_HasNoSelfReferentialFallback()
    {
        var code = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Configuration", "AnthillRuntime.cs")));

        Assert.DoesNotContain("?? ApiAuthToken", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CONFIG FILE THAT CANNOT BE READ IS RECORDED, AND THE SERVER REFUSES TO START ON IT.
    ///
    /// It used to warn and run on SAFE_LOCAL defaults — which bind 0.0.0.0 and enable a different
    /// capability set than the operator's file describes. An operator can fix a syntax error in
    /// seconds; they cannot notice a colony quietly running somebody else's configuration.
    /// </summary>
    [Fact]
    public void AnUnreadableConfig_StopsTheServer()
    {
        var runtime = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Configuration", "AnthillRuntime.cs")));
        var host = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Api", "ApiHost.cs")));

        Assert.Contains("ConfigLoadError = ", runtime, StringComparison.Ordinal);
        Assert.Contains("ConfigLoadError.Length > 0", host, StringComparison.Ordinal);
        Assert.Contains("REFUSING TO START", host, StringComparison.Ordinal);
        // And the escape hatch is explicit rather than implicit.
        Assert.Contains("ANTHILL_ALLOW_INVALID_CONFIG", host, StringComparison.Ordinal);
    }

    /// <summary>
    /// A setting with no reader is a setting that does nothing. Empty by construction is fine;
    /// declared and unread is not.
    /// </summary>
    [Fact]
    public void TheMigrationResult_ReachesAnOperator()
    {
        var health = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Api", "ApiHost.Autonomy.cs")));

        Assert.Contains("LastConfigMigration", health, StringComparison.Ordinal);
    }
}
