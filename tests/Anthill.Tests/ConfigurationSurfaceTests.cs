using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// A generated schema — one declaration carrying type, default, env override, range, security class
/// and UI exposure, with the example file and the docs generated FROM it — is the real end state and
/// is named in `PLAN.md` as its own piece of work. This is the guard that stops the drift getting
/// worse in the meantime, and it is written the way this repository has learned to write these: an
/// explicit ledger of what is deliberately undocumented, so a new key cannot join that list by
/// accident.
/// </summary>
public class ConfigurationSurfaceTests
{
    private static Dictionary<string, JsonElement> Example()
    {
        var text = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "config.example.json"));
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text)!;
    }

    private static Dictionary<string, PropertyInfo> Parsed() =>
        typeof(AnthillConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null)
            .ToDictionary(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name, p => p,
                StringComparer.Ordinal);

    /// <summary>
    /// Settings the runtime parses and the example file deliberately does not show, each with a
    /// reason. The list is the point: an undocumented key has to be a decision.
    /// </summary>
    private static readonly Dictionary<string, string> DeliberatelyUndocumented = new(StringComparer.Ordinal)
    {
        ["config_schema_version"] = "written by the migration, not by an operator",
        ["handoff_ingestion_enabled"] = "internal wiring, no operator-facing behaviour on its own",
        ["adaptive_mission_control_enabled"] = "internal wiring",
        ["objective_verification_enabled"] = "internal wiring",
        ["shadow_observation_enabled"] = "console-managed (Readiness page)",
        ["activation_tier"] = "console-managed",
        ["readiness_min_shadow_sample"] = "readiness thresholds, console-managed",
        ["readiness_min_diagnosis_precision"] = "readiness thresholds, console-managed",
        ["readiness_min_action_accuracy"] = "readiness thresholds, console-managed",
        ["model_priority_provider"] = "console-managed (Routing inspector)",
        ["model_priority_model"] = "console-managed (Routing inspector)",
        ["user_tools_enabled"] = "console-managed (operator-defined tools)",
        ["user_tool_allowed_hosts"] = "console-managed",
        ["workspace_checks"] = "file-only by design; see the v0.3.8.73 note on its declaration",
        ["deployment_mode"] = "detected; the console shows it read-only",
        ["docker_execute_enabled"] = "module surface, not a general operator setting",
        ["micromound_enabled"] = "optional compile-time integration",
        ["dashboard_workspace_enabled"] = "console-managed",
        ["autonomy_oneshot_completion"] = "autonomy internals, console-managed",
        ["autonomy_autoapply_git_push"] = "console-managed (auto-apply panel)",
        ["autonomy_autoapply_git_remote"] = "console-managed",
        ["autonomy_autoapply_git_username"] = "console-managed",
        ["autonomy_autoapply_git_ssh_key_path"] = "console-managed",
        ["autonomy_autoapply_keep_without_verify"] = "break-glass; documented in AUTONOMY.md, "
                                                   + "deliberately not shown as an ordinary setting",
    };

    /// <summary>
    /// EVERY DOCUMENTED KEY IS ONE THE RUNTIME ACTUALLY PARSES.
    ///
    /// This is the direction that misleads an operator most directly: a key in the example that
    /// nothing reads is a setting they can spend an afternoon adjusting with no effect.
    /// </summary>
    [Fact]
    public void EveryKeyTheExampleDocuments_IsOneTheRuntimeParses()
    {
        var parsed = Parsed();

        var unread = Example().Keys
            .Where(k => !k.StartsWith("_comment", StringComparison.Ordinal))
            .Where(k => !parsed.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(unread.Count == 0,
            "config.example.json documents settings the runtime does not parse, so an operator can "
          + "set them and nothing happens: " + string.Join(", ", unread));
    }

    /// <summary>
    /// AND EVERY PARSED KEY IS EITHER DOCUMENTED OR DELIBERATELY NOT.
    ///
    /// The other direction, and the one that lets a real setting exist with no operator-facing
    /// surface at all. The ledger makes each omission a decision somebody made rather than a gap
    /// nobody noticed.
    /// </summary>
    [Fact]
    public void EveryParsedKey_IsDocumentedOrOnTheUndocumentedLedger()
    {
        var documented = Example().Keys.ToHashSet(StringComparer.Ordinal);

        var missing = Parsed().Keys
            .Where(k => !documented.Contains(k))
            .Where(k => !DeliberatelyUndocumented.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "these settings are parsed and appear neither in config.example.json nor on the "
          + "deliberately-undocumented ledger, so an operator has no way to learn they exist: "
          + string.Join(", ", missing)
          + ". Document them, or add them to the ledger with the reason.");
    }

    /// <summary>And the ledger names nothing that has since gone.</summary>
    [Fact]
    public void TheUndocumentedLedger_NamesOnlyRealSettings()
    {
        var parsed = Parsed();

        var stale = DeliberatelyUndocumented.Keys
            .Where(k => !parsed.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "the deliberately-undocumented ledger names settings that no longer exist: "
          + string.Join(", ", stale));
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
