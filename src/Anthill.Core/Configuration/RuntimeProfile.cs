using Anthill.Core.Agents;

namespace Anthill.Core.Configuration;

/// <summary>
/// What this run is permitted to WRITE. Resolved once; a mission cannot widen it mid-flight.
/// </summary>
/// <param name="Files">Agent file writes are permitted at all.</param>
/// <param name="Patches">Approved patch proposals may be applied to disk.</param>
/// <param name="Shell">The allowlisted shell tool is available to ants.</param>
/// <param name="Root">The only tree writes may target.</param>
public sealed record WritePermissions(bool Files, bool Patches, bool Shell, string Root)
{
    /// <summary>True when nothing this run does can modify the workspace.</summary>
    public bool IsReadOnly => !Files && !Patches && !Shell;
}

/// <summary>
/// The verification bar this run is held to.
/// </summary>
/// <param name="ObjectiveVerification">The canonical evaluation also checks that the goal's
/// deliverable was actually produced, not only that tasks finished.</param>
/// <param name="KeepWithoutVerify">Break-glass: changes may be retained without deterministic
/// evidence. While true the installation is NOT V3-qualifiable.</param>
public sealed record VerificationPolicy(bool ObjectiveVerification, bool KeepWithoutVerify)
{
    /// <summary>
    /// v3.0.1 safety rule, expressed once: only a run held to a real bar can report verified
    /// success. Break-glass disqualifies the run regardless of what else is enabled.
    /// </summary>
    public bool CanRecordVerifiedSuccess => !KeepWithoutVerify;
}

/// <summary>
/// v3.1.0 (ADR-001) — the per-run resolved capability set.
///
/// <see cref="RuntimeOptions"/> answers "what is configured". <c>RuntimeProfile</c> answers the
/// question the mission path actually asks: "given that configuration, what may this run DO" —
/// which roles are executable, which tools exist, what may be written, and what the verification
/// bar is. Those answers were previously recomputed at each point of use from a mix of statics,
/// registry properties and rollout gates, which is how two call sites came to disagree.
///
/// Resolved once per mission run and immutable thereafter, so the answer a mission starts with is
/// the answer it finishes with. Construction runs the v2.26.0 <see cref="RuntimeConfigValidator"/>
/// and CARRIES the findings rather than throwing: the validator's contract is to degrade loudly,
/// never to refuse boot, and a half-configured colony needs a running console that explains the
/// problem more than it needs a dead process.
/// </summary>
public sealed record RuntimeProfile
{
    public required RuntimeOptions Options { get; init; }

    /// <summary>Roles the planner may assign and the runtime may dispatch, resolved through the
    /// activation tier and specialist rollout gates at resolution time.</summary>
    public required IReadOnlySet<string> ExecutableRoles { get; init; }

    /// <summary>Tool names actually registered for this run. A grant that no tool backs is a
    /// permission to do nothing; a tool with no grant is unreachable — both are visible here.</summary>
    public required IReadOnlySet<string> ToolGrants { get; init; }

    public required WritePermissions Writes { get; init; }

    public required VerificationPolicy Verification { get; init; }

    /// <summary>Configuration-health findings observed when this profile was resolved.</summary>
    public required IReadOnlyList<ConfigFinding> Findings { get; init; }

    public bool HasCriticalFinding => Findings.Any(f => f.Severity == "critical");

    public bool CanExecute(string roleId) => ExecutableRoles.Contains(roleId);

    public bool HasTool(string toolName) => ToolGrants.Contains(toolName);

    /// <summary>
    /// Resolve a profile from a captured options snapshot and the tools this run registered.
    /// </summary>
    /// <param name="options">The immutable configuration snapshot for this run.</param>
    /// <param name="registeredTools">Tool names from the run's <c>ToolRegistry</c>. Passed in
    /// rather than re-derived from the gates so the profile reports what was actually built, not
    /// what the gates imply should have been.</param>
    public static RuntimeProfile Resolve(RuntimeOptions options, IEnumerable<string> registeredTools) => new()
    {
        Options = options,
        // A property, not a cached set: it folds in the specialist rollout gates. Snapshotted here
        // so the run sees one answer even if a gate is flipped underneath it.
        ExecutableRoles = new HashSet<string>(AntRegistry.ExecutableRoleIds, StringComparer.OrdinalIgnoreCase),
        ToolGrants = new HashSet<string>(registeredTools, StringComparer.OrdinalIgnoreCase),
        Writes = new WritePermissions(
            Files: options.FileWriting,
            Patches: options.PatchApplication,
            Shell: options.ShellTool,
            Root: options.AllowedWorkspaceRoot),
        Verification = new VerificationPolicy(
            ObjectiveVerification: options.ObjectiveVerification,
            KeepWithoutVerify: options.KeepWithoutVerify),
        Findings = RuntimeConfigValidator.Validate(),
    };

    /// <summary>Operator-visible projection. Secret-free by construction — it contains no
    /// credentials, tokens, or hostnames.</summary>
    public Dictionary<string, object?> Snapshot() => new()
    {
        ["executable_roles"] = ExecutableRoles.OrderBy(r => r, StringComparer.Ordinal).ToList(),
        ["tool_grants"] = ToolGrants.OrderBy(t => t, StringComparer.Ordinal).ToList(),
        ["writes"] = new Dictionary<string, object?>
        {
            ["files"] = Writes.Files, ["patches"] = Writes.Patches,
            ["shell"] = Writes.Shell, ["read_only"] = Writes.IsReadOnly,
        },
        ["verification"] = new Dictionary<string, object?>
        {
            ["objective_verification"] = Verification.ObjectiveVerification,
            ["keep_without_verify"] = Verification.KeepWithoutVerify,
            ["can_record_verified_success"] = Verification.CanRecordVerifiedSuccess,
        },
        ["parallel_execution"] = Options.ParallelExecution,
        ["max_parallel_workers"] = Options.MaxParallelWorkers,
        ["activation_tier"] = Options.ActivationTier.ToString(),
        ["environment"] = Options.EnvironmentFingerprint,
        ["config_findings"] = Findings
            .Select(f => new Dictionary<string, object?>
            {
                ["severity"] = f.Severity, ["combination"] = f.Combination, ["detail"] = f.Detail,
            }).ToList(),
    };
}
