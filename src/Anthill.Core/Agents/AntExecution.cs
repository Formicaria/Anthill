using Anthill.Core.Contracts;

namespace Anthill.Core.Agents;

/// <summary>
/// Ant Execution Framework — Stage A (classification + contracts + structured results).
/// ADDITIVE: nothing in this file activates a role. Specialist roles stay Executable:false in the
/// registry until their canary stage completes (contract + handler + enforcement + tests + docs).
/// Fail-closed principle: anything not explicitly classified/contracted is treated as the most
/// restricted case.
/// </summary>
public enum AntRuntimeKind
{
    /// <summary>Orchestration/planning/policy services (queen, director, planner, constraint). Never mission workers.</summary>
    ControlPlane,
    /// <summary>Deterministic C# service behavior (homelab collectors, quartermaster). Never LLM-directed.</summary>
    DeterministicService,
    /// <summary>A real mission executor with a runtime handler and execution contract.</summary>
    MissionAgent,
    /// <summary>Displayed with name/purpose but no runtime implementation yet.</summary>
    VisualScaffold,
}

public enum AntWorkState { Offline, Idle, Assigned, Running, Waiting, Blocked, Failed }

/// <summary>Versioned execution contract for mission agents (spec §4.2). The runtime rejects
/// tasks that do not match the assigned role's contract.</summary>
public sealed record AntExecutionContract(
    string RoleId,
    string Version,
    IReadOnlySet<string> SupportedTaskTypes,
    IReadOnlySet<string> RequiredCapabilities,
    IReadOnlySet<string> AllowedTools,
    IReadOnlySet<string> ForbiddenTools,
    IReadOnlySet<string> ProducedArtifactTypes,
    IReadOnlySet<string> AllowedHandoffRoles,
    bool AllowsModelCalls,
    bool AllowsSideEffects,
    bool ProducesPatchProposals,
    ModelRequirement? Model = null)
{
    public bool SupportsTaskType(string taskType) =>
        SupportedTaskTypes.Count == 0 || SupportedTaskTypes.Contains(taskType ?? "");

    /// <summary>
    /// What this role needs from whatever model it is routed to. Never null to a caller — a role
    /// making no model calls needs nothing, and saying that explicitly beats a null every reader has
    /// to remember to handle.
    /// </summary>
    public ModelRequirement ModelNeeds => Model ?? ModelRequirement.None;
}

/// <summary>
/// v3.4.2 (ADR-003) — what a role needs from its MODEL, as distinct from what it needs from the
/// colony.
///
/// <see cref="AntExecutionContract.RequiredCapabilities"/> already says the latter: repo read,
/// process execution, model invocation. It cannot say the former, and that gap has a specific
/// consequence — routing a tool-using role to a model that cannot call tools DOES NOT FAIL. The
/// model is simply never shown the tools, answers from priors, and produces a confident unsourced
/// answer that reads as a bad answer rather than as a misconfiguration. That exact failure has
/// already happened once on this project's own hardware.
///
/// This is the missing half of the pairing the capability model was built for: v3.3.0 learned what
/// each model CAN do; this says what each role NEEDS. Neither is useful alone, which is why the
/// roadmap held these contracts back until the capability model existed.
///
/// Requirements are CHECKED and reported, never enforced by substitution here. The router owns
/// routing; a contract that could silently redirect a role to another model would be a second
/// routing policy competing with the real one.
/// </summary>
public sealed record ModelRequirement(
    bool ToolCalling = false,
    bool StructuredOutput = false,
    bool Reasoning = false,
    int? MinContextTokens = null)
{
    /// <summary>A role that makes no model calls, or whose model needs nothing in particular.</summary>
    public static readonly ModelRequirement None = new();

    /// <summary>Nothing to check — lets callers skip the work rather than compare four falses.</summary>
    public bool IsEmpty => Equals(None);
}

public sealed record AntArtifact(string Kind, string Title, string Content, string? Path = null);
public sealed record AntEvidence(string Kind, string Value, string? Detail = null);
public sealed record AntFailure(FailureClass Class, string Reason, bool Retryable);
public sealed record AntHandoff(
    string SourceRole, string DestinationRole, string Reason, string RequiredTaskType,
    IReadOnlyList<string> ArtifactKinds, bool Required, int Depth, string DedupeKey);

/// <summary>Structured execution result (spec §4.3). Mission control flow reads these fields;
/// the prose Narrative remains only for operators and backward compatibility.</summary>
/// <summary>
/// v2.19.0: what an execution cost. Recorded for observability and budget accounting; deliberately
/// separate from <see cref="AntExecutionResult.StatusCode"/> so cost can never imply an outcome.
/// </summary>
public sealed record AntMetrics
{
    public int ModelCalls { get; init; }
    public int ToolCalls { get; init; }
    public double ElapsedSeconds { get; init; }
    public int InputChars { get; init; }
    public int OutputChars { get; init; }
    public int RetryCount { get; init; }
    /// <summary>Stable description of where this ran, for reproducing a result later.</summary>
    public string? EnvironmentFingerprint { get; init; }
}

public sealed record AntExecutionResult
{
    public required bool Success { get; init; }
    /// <summary>succeeded | succeeded_with_warnings | failed_retryable | failed_permanent |
    /// blocked | skipped | cancelled | timed_out</summary>
    public required string StatusCode { get; init; }
    public required string Summary { get; init; }
    public string? Narrative { get; init; }
    public List<AntArtifact> Artifacts { get; init; } = new();
    public List<AntEvidence> Evidence { get; init; } = new();
    public List<AntHandoff> Handoffs { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public AntFailure? Failure { get; init; }
    /// <summary>
    /// v2.19.0: typed execution metrics. Observability only — these never influence task status,
    /// which is decided solely by <see cref="StatusCode"/>.
    /// </summary>
    public AntMetrics Metrics { get; init; } = new();

    public static AntExecutionResult Succeeded(string summary, string? narrative = null) =>
        new() { Success = true, StatusCode = "succeeded", Summary = summary, Narrative = narrative };
    public static AntExecutionResult Blocked(string reason) =>
        new() { Success = false, StatusCode = "blocked", Summary = reason,
                Failure = new AntFailure(FailureClass.AuthorizationFailure, reason, Retryable: false) };
    /// <summary>Completed, but with caveats the operator should see. Still a success.</summary>
    public static AntExecutionResult SucceededWithWarnings(string summary, IEnumerable<string> warnings, string? narrative = null) =>
        new() { Success = true, StatusCode = "succeeded_with_warnings", Summary = summary, Narrative = narrative,
                Warnings = warnings?.Where(w => !string.IsNullOrWhiteSpace(w)).ToList() ?? new() };

    /// <summary>Nothing to do — not a failure, and never reinforced as a success.</summary>
    public static AntExecutionResult Skipped(string reason) =>
        new() { Success = false, StatusCode = "skipped", Summary = reason };

    public static AntExecutionResult Failed(FailureClass cls, string reason) =>
        new() { Success = false, StatusCode = FailureClassify.IsRetryable(cls) ? "failed_retryable" : "failed_permanent",
                Summary = reason, Failure = new AntFailure(cls, reason, FailureClassify.IsRetryable(cls)) };
}

/// <summary>
/// Stage A classification of every registry role (spec §4.1) plus the versioned contracts for the
/// six specialists this framework will activate in Stage D. Roles absent from BOTH maps are
/// VisualScaffold — fail closed.
/// </summary>
public static class AntExecutionCatalog
{
    private static readonly Dictionary<string, AntRuntimeKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["queen"] = AntRuntimeKind.ControlPlane,
        ["director"] = AntRuntimeKind.ControlPlane,
        ["planner"] = AntRuntimeKind.ControlPlane,
        ["constraint"] = AntRuntimeKind.ControlPlane,
        ["inventory"] = AntRuntimeKind.DeterministicService,
        ["network_scout"] = AntRuntimeKind.DeterministicService,
        ["health"] = AntRuntimeKind.DeterministicService,
        ["proxmox"] = AntRuntimeKind.DeterministicService,
        ["storage"] = AntRuntimeKind.DeterministicService,
        ["backup"] = AntRuntimeKind.DeterministicService,
        ["security_scout"] = AntRuntimeKind.DeterministicService,
        ["change_archivist"] = AntRuntimeKind.DeterministicService,
        ["quartermaster"] = AntRuntimeKind.DeterministicService, // advisory service; NOT a free-form LLM worker
        ["researcher"] = AntRuntimeKind.MissionAgent,
        ["web"] = AntRuntimeKind.MissionAgent,
        ["file"] = AntRuntimeKind.MissionAgent,
        ["coder"] = AntRuntimeKind.MissionAgent,
        ["builder"] = AntRuntimeKind.MissionAgent,
        ["verifier"] = AntRuntimeKind.MissionAgent,
        // Specialists: MissionAgent by DESIGN — but implemented/planner-eligible only after Stage D.
        ["tester"] = AntRuntimeKind.MissionAgent,
        ["soldier"] = AntRuntimeKind.MissionAgent,
        ["medic"] = AntRuntimeKind.MissionAgent,
        ["archivist"] = AntRuntimeKind.MissionAgent,
        ["ui_cartographer"] = AntRuntimeKind.MissionAgent,
        ["scribe"] = AntRuntimeKind.MissionAgent,
    };

    public static AntRuntimeKind KindOf(string roleId) =>
        Kinds.TryGetValue(roleId ?? "", out var k) ? k : AntRuntimeKind.VisualScaffold;

    /// <summary>Planner eligibility is COMPUTED, never a stored boolean: a role may plan only if it
    /// is a MissionAgent, registry-executable+enabled, and has a runtime handler (Stage C wires the
    /// handler check; until then the registry's Executable flag is the binding constraint).</summary>
    public static bool PlannerEligible(string roleId) =>
        KindOf(roleId) == AntRuntimeKind.MissionAgent && AntRegistry.ExecutableRoleIds.Contains(roleId ?? "");

    private static IReadOnlySet<string> S(params string[] xs) => xs.ToHashSet(StringComparer.OrdinalIgnoreCase);
    private const string V = "1"; // contract version for every Stage A declaration

    /// <summary>Versioned contracts for the specialist roles (spec §6). Declared now, enforced in
    /// Stage B (dispatch) and honored by handlers in Stage D. NO role here has apply_patch, ever.</summary>
    public static readonly IReadOnlyDictionary<string, AntExecutionContract> Contracts =
        new Dictionary<string, AntExecutionContract>(StringComparer.OrdinalIgnoreCase)
    {
        ["tester"] = new("tester", V,
            SupportedTaskTypes: S("build_check", "test_execution", "frontend_check", "validation_check", "regression_check", "verification_check"),
            RequiredCapabilities: S(Capability.ProcessExecuteReadonly, Capability.RepoRead),
            AllowedTools: S("run_allowlisted_check"),
            ForbiddenTools: S("apply_patch", "shell_command", "write_text_file"),
            ProducedArtifactTypes: S("test_report"),
            AllowedHandoffRoles: S("verifier", "soldier", "medic"),
            AllowsModelCalls: false, AllowsSideEffects: false, ProducesPatchProposals: false,
            // Deterministic: it runs allowlisted checks and reports exit codes. No model, so no
            // model requirement — stating None explicitly rather than leaving it null.
            Model: ModelRequirement.None),
        ["soldier"] = new("soldier", V,
            SupportedTaskTypes: S("security_review", "patch_risk_review", "permission_review", "policy_review", "scope_review", "dependency_risk_review"),
            RequiredCapabilities: S(Capability.RepoRead),
            AllowedTools: S("policy_scan"),
            ForbiddenTools: S("apply_patch", "shell_command", "write_text_file"),
            ProducedArtifactTypes: S("security_review"),
            AllowedHandoffRoles: S("verifier", "medic", "builder"),
            AllowsModelCalls: true, AllowsSideEffects: false, ProducesPatchProposals: false,
            // A review is a VERDICT the colony branches on, so it must come back as a schema rather
            // than as prose to be parsed — the whole reason v3.2.0 removed prose-derived control
            // flow. Tool calling is not required: policy_scan hands it the evidence.
            Model: new ModelRequirement(StructuredOutput: true)),
        ["medic"] = new("medic", V,
            SupportedTaskTypes: S("failure_diagnosis", "repair_triage", "retry_classification", "root_cause_analysis", "recovery_recommendation"),
            RequiredCapabilities: S(Capability.ModelInvoke, Capability.RepoRead),
            AllowedTools: S("read_failure_context"),
            ForbiddenTools: S("apply_patch", "shell_command", "write_text_file"),
            ProducedArtifactTypes: S("failure_diagnosis", "repair_recommendation"),
            AllowedHandoffRoles: S("coder", "ui_cartographer", "tester", "builder"),
            AllowsModelCalls: true, AllowsSideEffects: false, ProducesPatchProposals: false,
            // The medic emits a FailureClass and a repair route that the scheduler acts on, so the
            // output is structured by necessity. Reasoning is required rather than preferred: it
            // infers a cause from evidence that does not state one, and a model that cannot hold a
            // chain of inference produces a plausible diagnosis of the wrong thing.
            Model: new ModelRequirement(StructuredOutput: true, Reasoning: true)),
        ["archivist"] = new("archivist", V,
            SupportedTaskTypes: S("memory_consolidation", "lesson_extraction", "negative_memory", "rule_archival", "mission_summary", "skill_candidate_extraction"),
            RequiredCapabilities: S(Capability.ModelInvoke),
            AllowedTools: S("write_memory_candidate"),
            ForbiddenTools: S("apply_patch", "shell_command", "write_text_file"),
            ProducedArtifactTypes: S("memory_candidate"),
            AllowedHandoffRoles: S(),
            AllowsModelCalls: true, AllowsSideEffects: false, ProducesPatchProposals: false,
            // The only role whose binding constraint is CONTEXT: it reads a terminal mission's whole
            // history to extract lessons. A short window does not fail here — it silently truncates
            // the history and produces confident lessons drawn from part of the evidence, which is
            // worse than no lesson because it is written to durable memory.
            Model: new ModelRequirement(StructuredOutput: true, MinContextTokens: 32_000)),
        ["ui_cartographer"] = new("ui_cartographer", V,
            SupportedTaskTypes: S("ui_mapping", "route_mapping", "component_mapping", "style_mapping", "frontend_dependency_mapping", "ui_change_impact"),
            RequiredCapabilities: S(Capability.RepoRead, Capability.RepoSearch),
            AllowedTools: S("list_directory", "read_text_file", "search_workspace"),
            ForbiddenTools: S("apply_patch", "shell_command", "write_text_file"),
            ProducedArtifactTypes: S("ui_map"),
            AllowedHandoffRoles: S("coder", "soldier"),
            AllowsModelCalls: true, AllowsSideEffects: false, ProducesPatchProposals: false,
            // The clearest case for the whole mechanism. This role EXISTS to walk a repository with
            // list_directory/read_text_file/search_workspace, and a model that cannot call tools
            // does not fail — it is never shown them, and maps the UI from priors. A confident
            // fabricated route map is far more damaging than an error.
            Model: new ModelRequirement(ToolCalling: true, StructuredOutput: true)),
        ["scribe"] = new("scribe", V,
            SupportedTaskTypes: S("release_notes", "changelog_update", "operator_documentation", "incident_summary", "verified_change_summary", "docs_patch_proposal"),
            RequiredCapabilities: S(Capability.ModelInvoke, Capability.RepoRead),
            AllowedTools: S("read_changed_files_summary"),
            ForbiddenTools: S("apply_patch", "shell_command", "write_text_file"),
            ProducedArtifactTypes: S("release_notes", "docs_patch_set"),
            AllowedHandoffRoles: S("verifier", "soldier"),
            AllowsModelCalls: true, AllowsSideEffects: false, ProducesPatchProposals: true, // docs paths ONLY (enforced at proposal time)
            // Prose is the deliverable, so no structured-output requirement — but the patch set it
            // proposes has to be machine-applicable, and it summarises a whole change set, so the
            // window is the constraint worth declaring.
            Model: new ModelRequirement(StructuredOutput: true, MinContextTokens: 16_000)),
    };

    public static AntExecutionContract? ContractFor(string roleId) =>
        Contracts.TryGetValue(roleId ?? "", out var c) ? c : null;
}
