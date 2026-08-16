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
        new("python_outside_archive", "high", true, Rx(@"[\w\-]+\.py\b"), "Python is forbidden in this repository (NORTH_STAR rule 13)"),
        // v3.8.26 — CASE-INSENSITIVE, and widened to the shapes real source actually uses.
        //
        // This rule was case-SENSITIVE while `destructive_operation`, `auth_change` and
        // `db_migration` beside it were not, so the most severe rule in the table was the fussiest
        // about spelling. It matched `api_key = "…"` and missed `apiKey`, `apiToken`, `authToken`,
        // `accessToken`, `clientSecret` — the casings a C#, JS or TypeScript file is most likely to
        // contain. A secret in a proposed patch passed the security review because of a capital K.
        //
        // Found by a test fixture: v3.8.25 taught the soldier to read the real patch, and the first
        // fixture written for it used `var apiKey = "sk-…"` and did not trip. Chasing why exposed
        // the rule rather than the plumbing.
        //
        // The VALUE must still be QUOTED, and that restraint was measured rather than assumed.
        //
        // The first draft of this fix also accepted unquoted values of 12+ characters, to catch
        // `.env`-style assignments. Run against this repository it produced FIFTEEN false positives
        // — `token = AuthSessions.Issue(...)`, `token = _tokenProvider`,
        // `AuthToken = Environment.GetEnvironmentVariable(...)` — every one an assignment from a
        // function or field rather than a literal. A soldier that blocks those blocks ordinary
        // patches constantly, and a security rule that cries wolf is a security rule somebody
        // switches off. That is strictly worse than the narrow rule it replaced.
        //
        // Quoted-only, case-insensitive, with the noun list widened: one hit across all of `src/`,
        // and it is the archivist's own redaction pattern — a true positive by construction. The
        // actual defect was CASING, and casing is what this fixes.
        // v0.3.8.72 — THIS RULE READS SOURCE, AND IT IS THE CALLER'S JOB TO HAND IT SOURCE.
        //
        // v0.3.8.71 found the rule unable to fire on a quoted secret in a proposed patch, because the
        // soldier was scanning the patch artifact's JSON SERIALIZATION rather than its values. The
        // first instinct in v0.3.8.72 was to widen the pattern to tolerate an escaped quote — and
        // that was wrong, usefully. `Json.Dumps` leaves `JsonSerializerOptions.Encoder` at
        // `JavaScriptEncoder.Default`, which does not write `\"` at all: it writes `"`, and also
        // escapes `<`, `>`, `&`, `'` and `+` the same way. A `\\?['"]` allowance sees none of that.
        //
        // The general shape of the mistake is worth more than the instance: a scanner that tries to
        // recognise text through an encoding has to know every encoding, and it will be wrong the
        // first time one changes. DECODING BELONGS AT THE FEED. `SoldierAnt.DecodeForScanning` is
        // where it lives, `SecretPatternEncodingTests` pins that every artifact-scanning caller goes
        // through it, and this pattern stays written against source exactly as v3.8.26 tuned it.
        new("secret_material", "critical", true, Rx(
            @"-----BEGIN [A-Z ]*PRIVATE KEY-----"
            + @"|(?:password|passwd|api[_-]?key|api[_-]?token|auth[_-]?token|access[_-]?token|client[_-]?secret|bearer|credential|secret|token)"
            + @"\s*[:=]\s*['""][^'""\s]{6,}",
            RegexOptions.IgnoreCase), "secret-like content detected"),
        new("permission_expansion", "critical", true, Rx(@"ApplyPatches\s*=\s*true|apply_patch.*(?:allow|grant|enable)|(?:allow|grant|enable).*apply_patch"), "attempt to grant patch application"),
        // v0.3.8.73 — `workspace_checks` joins the list the day it is created, not the day someone
        // notices. Operator-declared checks REPLACE detection, so a patch that edits them is a patch
        // that edits what verifies the colony's own work — the exact shape this rule exists for, and
        // a strictly larger prize than the allowlists already named here. The setting lives in
        // ANTHILL's configuration rather than in the workspace precisely so a mission cannot reach
        // it; this rule is the second lock, for the case where a mission proposes a patch against
        // the colony's own tree.
        new("allowlist_tampering", "critical", true, Rx(@"target_allowlist|homelab_target_allowlist|workspace_checks|CheckCatalog\.Register|RoleAllowedTools"), "attempt to alter policy/allowlists"),
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
