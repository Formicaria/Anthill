using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Anthill.Core.Agents;
using Anthill.Core.Tools;

namespace Anthill.Core.Verification;

/// <summary>
/// NORTH_STAR Phase 4 — Independent Verification and Evidence. Execution and verification are
/// separated: the ant (or model) that performed a change is never the entity that decides whether
/// it worked. Every verifier is deterministic unless explicitly marked semantic, and a semantic
/// judge may SUPPLEMENT but never REPLACE deterministic evidence for state-changing operations.
/// Model confidence is never stored as proof.
/// </summary>
public sealed record VerificationRequest(
    string TaskType,
    string WorkspaceRoot,
    string? ChangedPath = null,
    string? NewContent = null,
    string? OldContent = null,
    IReadOnlyList<string>? ApprovedScope = null,
    IReadOnlyList<string>? RequiredArtifacts = null);

public sealed record VerificationEvidence(string Kind, string Value, string? Detail = null);

public sealed record VerificationResult(
    string Verifier,
    bool Passed,
    bool Deterministic,
    string Summary,
    IReadOnlyList<VerificationEvidence> Evidence);

public interface IVerifier
{
    string Name { get; }
    bool Deterministic { get; }
    VerificationResult Verify(VerificationRequest request);
}

/// <summary>Which verifiers a task type REQUIRES. A task cannot be declared verified without
/// every required verifier passing (see <see cref="VerificationBundle.Promotable"/>).</summary>
public static class VerificationPolicy
{
    private static readonly Dictionary<string, string[]> Required = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code_patch"] = new[] { "diff", "build", "test", "security_policy" },
        ["docs_patch"] = new[] { "diff", "security_policy" },
        ["config_change"] = new[] { "diff", "security_policy", "build" },
        ["artifact_production"] = new[] { "artifact" },
    };

    public static IReadOnlyList<string> For(string taskType) =>
        Required.TryGetValue(taskType ?? "", out var v) ? v : new[] { "security_policy" }; // unknown → at minimum policy-scan

    public static bool IsKnown(string taskType) => Required.ContainsKey(taskType ?? "");
}

/// <summary>The persisted proof for one verification run. Structural completion can never create
/// a verified success — only a bundle whose required verifiers all passed is promotable.</summary>
public sealed class VerificationBundle
{
    [JsonPropertyName("id")] public string Id { get; init; } = Guid.NewGuid().ToString();
    [JsonPropertyName("task_type")] public string TaskType { get; init; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; init; } = DateTime.UtcNow.ToString("O");
    [JsonPropertyName("results")] public List<VerificationResult> Results { get; init; } = new();
    [JsonPropertyName("required")] public List<string> Required { get; init; } = new();
    [JsonPropertyName("blocked_reasons")] public List<string> BlockedReasons { get; init; } = new();

    /// <summary>
    /// Promotion rule: every REQUIRED verifier ran and passed, nothing blocked, AND at least one
    /// passing result is deterministic.
    ///
    /// v2.26.0 pre-V3 hardening: the deterministic requirement is now INTRINSIC to this property.
    /// It used to live in the separate <see cref="HasDeterministicEvidence"/> flag that callers
    /// had to remember to consult — and one caller (mission-level skill credit) didn't, so a
    /// semantic-only bundle could promote a skill. An invariant a caller must remember is not an
    /// invariant. A missing verifier is a failure, not a pass — fail closed.
    /// </summary>
    [JsonPropertyName("promotable")]
    public bool Promotable => BlockedReasons.Count == 0 && Required.Count > 0
        && Required.All(r => Results.Any(x => x.Verifier == r && x.Passed))
        && HasDeterministicEvidence;

    /// <summary>Deterministic evidence must exist; semantic-only proof is never sufficient.</summary>
    [JsonPropertyName("has_deterministic_evidence")]
    public bool HasDeterministicEvidence => Results.Any(r => r.Deterministic && r.Passed);

    public string Explain() =>
        $"{(Promotable ? "VERIFIED" : "NOT VERIFIED")} [{TaskType}] " +
        string.Join(", ", Required.Select(r =>
        {
            var res = Results.FirstOrDefault(x => x.Verifier == r);
            return res is null ? $"{r}=MISSING" : $"{r}={(res.Passed ? "pass" : "FAIL")}";
        })) + (BlockedReasons.Count > 0 ? " | blocked: " + string.Join("; ", BlockedReasons) : "");
}

// ---- Deterministic verifiers -------------------------------------------------------------------

/// <summary>Confirms the change stays inside the approved scope and actually changes something.</summary>
public sealed class DiffVerifier : IVerifier
{
    public string Name => "diff";
    public bool Deterministic => true;

    public VerificationResult Verify(VerificationRequest r)
    {
        var evidence = new List<VerificationEvidence>();
        if (string.IsNullOrEmpty(r.ChangedPath))
            return new(Name, false, true, "no changed path supplied — nothing to verify", evidence);

        var path = r.ChangedPath.Replace('\\', '/');
        evidence.Add(new("changed_path", path));
        if (r.NewContent is not null)
            evidence.Add(new("new_content_sha256", Sha(r.NewContent)));
        if (r.OldContent is not null)
        {
            evidence.Add(new("old_content_sha256", Sha(r.OldContent)));
            if (r.OldContent == r.NewContent)
                return new(Name, false, true, "patch is a no-op (old == new)", evidence);
        }

        if (r.ApprovedScope is { Count: > 0 })
        {
            var inScope = r.ApprovedScope.Any(s => path.StartsWith(s.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            evidence.Add(new("approved_scope", string.Join(",", r.ApprovedScope)));
            if (!inScope)
                return new(Name, false, true, $"changed path '{path}' is outside the approved scope", evidence);
        }
        return new(Name, true, true, $"diff confined to '{path}'", evidence);
    }

    internal static string Sha(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
}

/// <summary>Runs the allowlisted build check inside the given workspace and records the exit code.</summary>
public sealed class BuildVerifier : IVerifier
{
    public string Name => "build";
    public bool Deterministic => true;

    public VerificationResult Verify(VerificationRequest r)
    {
        var tool = new RunAllowlistedCheckTool(r.WorkspaceRoot);
        var res = tool.Run(new Dictionary<string, object?> { ["check_id"] = "dotnet_build" });
        var exit = System.Text.RegularExpressions.Regex.Match(res.Output ?? "", @"exit_code=(-?\d+)").Groups[1].Value;
        return new(Name, res.Success, true,
            res.Success ? "build succeeded" : $"build failed ({res.Error})",
            new List<VerificationEvidence>
            {
                new("command", "dotnet_build"),
                new("exit_code", exit.Length > 0 ? exit : "n/a"),
                new("output_digest", DiffVerifier.Sha(res.Output ?? "")),
            });
    }
}

/// <summary>Runs the allowlisted test suite and records real results — never a model's claim.</summary>
public sealed class TestVerifier : IVerifier
{
    public string Name => "test";
    public bool Deterministic => true;

    public VerificationResult Verify(VerificationRequest r)
    {
        var tool = new RunAllowlistedCheckTool(r.WorkspaceRoot);
        var res = tool.Run(new Dictionary<string, object?> { ["check_id"] = "dotnet_test" });
        var output = res.Output ?? "";
        var exit = System.Text.RegularExpressions.Regex.Match(output, @"exit_code=(-?\d+)").Groups[1].Value;
        var totals = System.Text.RegularExpressions.Regex.Match(output, @"total:\s*(\d+).*?failed:\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var evidence = new List<VerificationEvidence>
        {
            new("command", "dotnet_test"),
            new("exit_code", exit.Length > 0 ? exit : "n/a"),
            new("output_digest", DiffVerifier.Sha(output)),
        };
        if (totals.Success)
            evidence.Add(new("test_counts", $"total={totals.Groups[1].Value} failed={totals.Groups[2].Value}"));
        return new(Name, res.Success, true, res.Success ? "tests passed" : $"tests failed ({res.Error})", evidence);
    }
}

/// <summary>Reuses the deterministic policy engine: secrets, permission expansion, blocked paths.</summary>
public sealed class SecurityPolicyVerifier : IVerifier
{
    public string Name => "security_policy";
    public bool Deterministic => true;

    public VerificationResult Verify(VerificationRequest r)
    {
        var subject = $"{r.ChangedPath}\n{r.NewContent}";
        var findings = PolicyScan.Scan(subject);
        var blocking = findings.Where(f => f.Blocking).ToList();
        var evidence = findings.Select(f => new VerificationEvidence("policy_rule", f.RuleId, f.Detail)).ToList();
        evidence.Add(new("risk_level", PolicyScan.OverallRisk(findings)));
        return new(Name, blocking.Count == 0, true,
            blocking.Count == 0 ? $"no blocking policy findings ({findings.Count} advisory)"
                                : $"{blocking.Count} BLOCKING policy finding(s)",
            evidence);
    }
}

/// <summary>Confirms required outputs actually exist, with hashes — not just claimed.</summary>
public sealed class ArtifactVerifier : IVerifier
{
    public string Name => "artifact";
    public bool Deterministic => true;

    public VerificationResult Verify(VerificationRequest r)
    {
        var required = r.RequiredArtifacts ?? Array.Empty<string>();
        if (required.Count == 0)
            return new(Name, false, true, "no required artifacts declared — cannot verify production", new List<VerificationEvidence>());
        var evidence = new List<VerificationEvidence>();
        var missing = new List<string>();
        foreach (var rel in required)
        {
            var full = Path.GetFullPath(Path.Combine(r.WorkspaceRoot, rel));
            if (!full.StartsWith(Path.GetFullPath(r.WorkspaceRoot), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            { missing.Add(rel); continue; }
            evidence.Add(new("file_hash", DiffVerifier.Sha(File.ReadAllText(full)), rel));
        }
        return new(Name, missing.Count == 0, true,
            missing.Count == 0 ? $"{required.Count} artifact(s) present" : $"missing: {string.Join(", ", missing)}",
            evidence);
    }
}

// ---- Runner --------------------------------------------------------------------------------------

/// <summary>
/// Runs a task type's REQUIRED verifiers and produces the evidence bundle. Verification is
/// independently rerunnable (same request → same deterministic results), and a verifier that
/// cannot run counts as a failure, never a pass.
/// </summary>
public sealed class VerificationRunner
{
    private readonly Dictionary<string, IVerifier> _verifiers;

    public VerificationRunner(IEnumerable<IVerifier>? verifiers = null)
    {
        var list = verifiers?.ToList() ?? new List<IVerifier>
        {
            new DiffVerifier(), new BuildVerifier(), new TestVerifier(),
            new SecurityPolicyVerifier(), new ArtifactVerifier(),
        };
        _verifiers = list.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
    }

    public VerificationBundle Run(VerificationRequest request)
    {
        var required = VerificationPolicy.For(request.TaskType).ToList();
        var bundle = new VerificationBundle { TaskType = request.TaskType, Required = required };
        foreach (var name in required)
        {
            if (!_verifiers.TryGetValue(name, out var verifier))
            {
                bundle.BlockedReasons.Add($"required verifier '{name}' is not registered");
                continue;
            }
            try { bundle.Results.Add(verifier.Verify(request)); }
            catch (Exception e)
            {
                bundle.Results.Add(new(name, false, verifier.Deterministic, $"verifier faulted: {e.Message}",
                    new List<VerificationEvidence> { new("error", e.GetType().Name) }));
            }
        }
        if (!bundle.HasDeterministicEvidence)
            bundle.BlockedReasons.Add("no passing deterministic evidence — semantic judgment alone cannot verify");
        return bundle;
    }
}
