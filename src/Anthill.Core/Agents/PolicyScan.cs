using System.Text.RegularExpressions;

namespace Anthill.Core.Agents;

/// <summary>
/// Execution framework Stage D-3 — SoldierAnt's deterministic policy engine. Pure functions over
/// the review input: no model call participates in ANY verdict here, so model output can explain a
/// result but can never override a block (spec §6.2). Every rule has a stable id that lands in
/// evidence.
/// </summary>
public sealed record PolicyFinding(string RuleId, string Risk, bool Blocking, string Detail);

public static class PolicyScan
{
    private sealed record Rule(string Id, string Risk, bool Blocking, Regex Pattern, string Why);

    private static readonly Rule[] Rules =
    {
        new("blocked_path_ci", "high", true, Rx(@"\.github[/\\]workflows[/\\]"), "CI/workflow files may not be modified by missions"),
        new("blocked_path_deploy", "high", true, Rx(@"\bdeploy[/\\]lxc[/\\]"), "deployment scripts are protected"),
        new("blocked_path_security", "critical", true, Rx(@"Anthill\.Core[/\\]Security[/\\]"), "security primitives are protected"),
        new("python_outside_archive", "high", true, Rx(@"(?<!py\.old[/\\])[\w\-]+\.py\b"), "Python outside py.old/ is forbidden (NORTH_STAR rule 13)"),
        new("secret_material", "critical", true, Rx(@"-----BEGIN [A-Z ]*PRIVATE KEY-----|(?:password|passwd|api[_-]?key|token|secret)\s*[:=]\s*['""][^'""\s]{6,}"), "secret-like content detected"),
        new("permission_expansion", "critical", true, Rx(@"ApplyPatches\s*=\s*true|apply_patch.*(?:allow|grant|enable)|(?:allow|grant|enable).*apply_patch"), "attempt to grant patch application"),
        new("allowlist_tampering", "critical", true, Rx(@"target_allowlist|homelab_target_allowlist|CheckCatalog\.Register|RoleAllowedTools"), "attempt to alter policy/allowlists"),
        new("destructive_operation", "critical", true, Rx(@"rm\s+-rf\s+/|DROP\s+TABLE|mkfs\.|wipe\s+disk|factory\s+reset", RegexOptions.IgnoreCase), "destructive operation"),
        new("auth_change", "high", false, Rx(@"RequireAuth|AuthLimiter|login|session[_-]?token", RegexOptions.IgnoreCase), "authentication surface touched — reviewer attention required"),
        new("db_migration", "medium", false, Rx(@"CREATE\s+TABLE|ALTER\s+TABLE|DROP\s+COLUMN", RegexOptions.IgnoreCase), "schema change — migration safety review required"),
        new("dependency_manifest", "medium", false, Rx(@"PackageReference|package\.json|\.csproj"), "dependency manifest change"),
        new("shell_or_script", "medium", false, Rx(@"\.(?:ps1|sh)\b"), "shell/script change"),
    };

    private static Regex Rx(string p, RegexOptions extra = RegexOptions.None) =>
        new(p, RegexOptions.Compiled | extra);

    /// <summary>Scan review input (changed paths, patch text, task description — whatever the
    /// mission supplies). Deterministic: same input, same findings, always.</summary>
    public static List<PolicyFinding> Scan(string input)
    {
        var findings = new List<PolicyFinding>();
        foreach (var rule in Rules)
        {
            var m = rule.Pattern.Match(input ?? "");
            if (m.Success)
                findings.Add(new PolicyFinding(rule.Id, rule.Risk, rule.Blocking,
                    $"{rule.Why} (matched: '{Truncate(m.Value)}')"));
        }
        return findings;
    }

    /// <summary>Scope check: when the input declares "approved_scope: a/, b/" every referenced
    /// repo path must fall inside it. Paths outside the approved scope are a blocking mismatch.</summary>
    public static PolicyFinding? ScopeMismatch(string input)
    {
        var scope = Regex.Match(input ?? "", @"approved_scope:\s*([^\n]+)");
        if (!scope.Success) return null;
        var allowed = scope.Groups[1].Value.Split(',').Select(s => s.Trim().Replace('\\', '/')).Where(s => s.Length > 0).ToList();
        var paths = Regex.Matches(input!, @"\b(?:src|docs|tests|deploy|scripts)/[\w./\-]+")
            .Select(m => m.Value).Distinct().ToList();
        var outside = paths.Where(p => !allowed.Any(a => p.StartsWith(a, StringComparison.OrdinalIgnoreCase))).ToList();
        return outside.Count > 0
            ? new PolicyFinding("scope_mismatch", "high", true,
                $"paths outside approved scope: {string.Join(", ", outside.Take(5))}")
            : null;
    }

    public static string OverallRisk(IReadOnlyList<PolicyFinding> findings) =>
        findings.Any(f => f.Risk == "critical") ? "critical"
        : findings.Any(f => f.Risk == "high") ? "high"
        : findings.Any(f => f.Risk == "medium") ? "medium" : "low";

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "…";
}
