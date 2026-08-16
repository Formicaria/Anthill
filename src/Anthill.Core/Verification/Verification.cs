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

    /// <summary>
    /// True when this verifier's answer depends ONLY on the workspace, not on the individual change
    /// being verified. v3.8.22.
    ///
    /// <see cref="BuildVerifier"/> and <see cref="TestVerifier"/> shell out to the toolchain and read
    /// nothing from the request but <c>WorkspaceRoot</c> — so for a patch set of five proposals they
    /// would return the same answer five times, at up to 600 and 1200 seconds each. Declaring the
    /// scope lets <see cref="VerificationRunner.RunForEach"/> run them once and share the result,
    /// which is what makes per-proposal verification affordable at all.
    ///
    /// A DEFAULT member returning false: a verifier that has not thought about this is treated as
    /// change-dependent and runs per proposal. That is the slow answer, never the wrong one — the
    /// failure mode of guessing true is a verdict computed from a different change than the one it
    /// is recorded against.
    /// </summary>
    bool WorkspaceScoped => false;
}

/// <summary>Which verifiers a task type REQUIRES. A task cannot be declared verified without
/// every required verifier passing (see <see cref="VerificationBundle.Promotable"/>).</summary>
public static class VerificationPolicy
{
    private static readonly Dictionary<string, string[]> Required = new(StringComparer.OrdinalIgnoreCase)
    {
        // v3.8.21 — `test` REMOVED from the default, deliberately and with the reason recorded.
        //
        // TestVerifier runs `dotnet test -c Release` — the entire suite, with a 1200-second cap —
        // and BuildVerifier runs `dotnet build -c Release` at 600. Requiring both meant up to half
        // an hour of wall clock per code-patch task, serially, on the Director thread. Worse, it is
        // self-referential: a mission that runs while the suite is running would invoke the suite
        // from inside itself.
        //
        // This table sat unenforced from v2.12 to v3.8.21 because nothing called the runner, so the
        // cost was never paid and never noticed. Wiring it up is what surfaced the number. Build is
        // kept because it is the check that matters most and is bounded — a patch that does not
        // compile can no longer reach a verified outcome. `test` remains a registered verifier and
        // can be required per task type when someone wants that trade.
        ["code_patch"] = new[] { "diff", "build", "security_policy" },
        ["code_patch_full"] = new[] { "diff", "build", "test", "security_policy" },
        ["docs_patch"] = new[] { "diff", "security_policy" },
        ["config_change"] = new[] { "diff", "security_policy", "build" },
        ["artifact_production"] = new[] { "artifact" },
    };

    /// <summary>
    /// What the PLANNER emits, mapped to what this table is keyed by. v3.8.22 — the fix for a
    /// defect v3.8.21 shipped.
    ///
    /// The table above was written against the vocabulary of the verification spec; the planner
    /// emits <c>patch_proposal</c> (see Planner's plan prompt and its deterministic fallback plan).
    /// Neither name is wrong, and nothing connected them. When v3.8.21 gave the runner its first
    /// production call site it passed <c>task.TaskType</c> straight through, every real patch missed
    /// every key here, and <see cref="For"/> returned the unknown-type fallback — <c>security_policy</c>
    /// alone. The two DETERMINISTIC verifiers, diff and build, never ran on a single patch.
    ///
    /// The effect was worse than no wiring at all: patch verification appeared to be running, the
    /// event row said so, and a proposal containing code that does not compile could reach
    /// <c>completed_verified</c>. Tests did not catch it because they passed <c>"code_patch"</c>
    /// literally — a task type production never produces. <c>TheTaskTypeThePlannerActuallyEmits_*</c>
    /// in the suite now pins the real one.
    ///
    /// Aliases rather than extra table keys, so there stays exactly ONE row per verification policy.
    /// Duplicating <c>code_patch</c>'s verifier list under three names is how the two copies drift.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["patch_proposal"] = "code_patch",
        ["patch"] = "code_patch",
        ["code_change"] = "code_patch",
        ["docs_update"] = "docs_patch",
        ["documentation"] = "docs_patch",
    };

    /// <summary>
    /// A path this repository treats as documentation. v0.3.8.75.
    ///
    /// The SAME definition <c>ScribeAnt</c> enforces for its documentation-only restriction, moved
    /// here and referenced there rather than written twice — two copies of "what counts as docs" is
    /// two answers to a question the security boundary asks.
    /// </summary>
    public static readonly System.Text.RegularExpressions.Regex DocsPath =
        new(@"^(?:docs/[\w./\-]+\.md|README\.md|CHANGELOG\.md)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The policy key for a task type: itself when the table knows it, its alias when one exists,
    /// otherwise the type unchanged (so an unknown type still reaches the fallback in <see cref="For"/>).
    /// An explicit table key always wins over an alias — a policy written for a name is never
    /// redirected away from it.
    /// </summary>
    public static string Canonical(string? taskType)
    {
        var t = taskType ?? "";
        if (Required.ContainsKey(t)) return t;
        return Aliases.TryGetValue(t, out var canonical) ? canonical : t;
    }

    /// <summary>
    /// The policy key for a patch, read from WHAT IT TOUCHES as well as what the task calls itself.
    /// v0.3.8.75.
    ///
    /// THE DEFECT. `docs_patch` has required `{diff, security_policy}` — no build — since the policy
    /// table was written, and nothing has ever selected it. The planner emits `patch_proposal` for
    /// every patch, docs and code alike; the alias maps that to `code_patch`; and `code_patch`
    /// requires `build`. So a README-only change has always been compiled with
    /// `dotnet build -c Release`, on the Director thread, before it could be called verified. The
    /// v3.8.21 note above worries in detail about exactly that cost and did not notice that the
    /// escape hatch beside it was unreachable.
    ///
    /// The task type CANNOT distinguish them: `coder.docs_coder` and `coder.ui_coder` both emit
    /// `patch_proposal`. The patch's own paths can, and they are the honest source — what a change
    /// touches is a fact about the change, not a claim about it.
    ///
    /// CONSERVATIVE BY CONSTRUCTION, in the direction that matters:
    ///   * EVERY proposal must be a documentation path. One `.cs` file among ten `.md` files makes
    ///     the whole set a code patch, because a set is applied as a unit and is exactly as
    ///     dangerous as its most dangerous member.
    ///   * an empty set is NOT docs — "nothing to look at" must never select the lighter policy.
    ///   * `security_policy` and `diff` still run. The soldier is still policy-inserted. This
    ///     narrows which DETERMINISTIC BUILD runs, and nothing about whether a reproducible no is
    ///     final: a docs patch that fails `diff` or trips `PolicyScan` is blocked exactly as before.
    ///   * an explicit table key still wins, so a task that names `code_patch` is never softened.
    /// </summary>
    public static string Canonical(string? taskType, IReadOnlyList<string>? changedPaths)
    {
        var canonical = Canonical(taskType);

        // An explicit policy for this task type is a decision someone made; paths do not override it.
        if (Required.ContainsKey(taskType ?? "")) return canonical;

        // Only ever narrows code_patch → docs_patch. No other transition is inferred from paths.
        if (!string.Equals(canonical, "code_patch", StringComparison.OrdinalIgnoreCase)) return canonical;

        if (changedPaths is null || changedPaths.Count == 0) return canonical;

        var allDocs = changedPaths.All(p =>
            DocsPath.IsMatch((p ?? "").Replace('\\', '/').TrimStart('/')));

        return allDocs ? "docs_patch" : canonical;
    }

    public static IReadOnlyList<string> For(string taskType) =>
        Required.TryGetValue(Canonical(taskType), out var v) ? v : new[] { "security_policy" }; // unknown → at minimum policy-scan

    public static bool IsKnown(string taskType) => Required.ContainsKey(Canonical(taskType));
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
    public bool WorkspaceScoped => true;   // reads WorkspaceRoot and nothing else about the change

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
    public bool WorkspaceScoped => true;   // as BuildVerifier: the suite is per workspace, not per proposal

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
            // v0.3.8.59 (PLAN.md §1b S1): the fourth copy of the missing-separator comparison, and
            // the one with the worst consequence — this hashes a required artifact as EVIDENCE. A
            // link or a sibling-prefixed path meant the hash recorded as proof of a file inside the
            // workspace could be the hash of a file outside it, and a true statement about the wrong
            // bytes is the failure this repository has spent releases removing.
            var containment = Security.PathContainment.Resolve(r.WorkspaceRoot, rel);
            if (!containment.Allowed || !File.Exists(containment.Path))
            { missing.Add(rel); continue; }
            evidence.Add(new("file_hash", DiffVerifier.Sha(File.ReadAllText(containment.Path)), rel));
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

    /// <summary>
    /// Verify EVERY change in a set, sharing the workspace-scoped verifiers across them. v3.8.22.
    ///
    /// A patch set is not one change. Verifying it with a single request meant at most one proposal
    /// was examined and the rest were verified by implication — and in v3.8.21 the request carried no
    /// proposal at all, so <see cref="DiffVerifier"/> answered "no changed path supplied — nothing to
    /// verify" and failed. Every proposal now gets its own request and its own verdict.
    ///
    /// The verifiers that declare <see cref="IVerifier.WorkspaceScoped"/> run ONCE for the whole set
    /// and their single result is recorded against each proposal. That is not an approximation: their
    /// answer is by declaration independent of which change is being verified, so running them per
    /// proposal would produce identical verdicts at N times the cost. Everything else runs per change.
    ///
    /// One bundle per request comes back, in the order given. The CALLER decides what a set of bundles
    /// means — this returns evidence and judges nothing, which is why it cannot quietly promote a set
    /// where one proposal failed.
    /// </summary>
    public IReadOnlyList<VerificationBundle> RunForEach(IReadOnlyList<VerificationRequest> requests)
    {
        if (requests.Count == 0) return Array.Empty<VerificationBundle>();

        // Keyed by verifier name: the one result a workspace-scoped verifier produces for this set.
        var shared = new Dictionary<string, VerificationResult>(StringComparer.OrdinalIgnoreCase);
        var bundles = new List<VerificationBundle>(requests.Count);

        foreach (var request in requests)
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
                if (verifier.WorkspaceScoped && shared.TryGetValue(name, out var already))
                {
                    bundle.Results.Add(already);
                    continue;
                }
                VerificationResult result;
                try { result = verifier.Verify(request); }
                catch (Exception e)
                {
                    result = new(name, false, verifier.Deterministic, $"verifier faulted: {e.Message}",
                        new List<VerificationEvidence> { new("error", e.GetType().Name) });
                }
                // Cache the fault too. A build that faulted once will fault the same way for every
                // other proposal in this set, and re-running it to rediscover that costs the full
                // timeout each time.
                if (verifier.WorkspaceScoped) shared[name] = result;
                bundle.Results.Add(result);
            }
            if (!bundle.HasDeterministicEvidence)
                bundle.BlockedReasons.Add("no passing deterministic evidence — semantic judgment alone cannot verify");
            bundles.Add(bundle);
        }
        return bundles;
    }
}
