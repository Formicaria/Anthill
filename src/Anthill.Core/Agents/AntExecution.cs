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

/// <summary>
/// HOW a role gets scheduled. v3.8.23.
///
/// Declared on the contract rather than inferred, because the four modes have genuinely different
/// safety properties and the difference was previously invisible: every role was, in principle,
/// something the planner could put in a plan. That is wrong for four of the twelve in ways that
/// cause real defects.
///
/// The medic is the clearest case. Planning a diagnosis before anything has failed gives it nothing
/// to diagnose — <c>MedicAnt.Execute</c> opens by returning Blocked when no failed task exists, which
/// is a handler defending itself against a scheduler that should never have called it. The archivist
/// is the mirror image: it needs a TERMINAL mission and the planner schedules it while tasks are
/// still running.
///
/// And the three safety roles must not depend on a model remembering to include them. A plan that
/// omits the tester is not a plan that skipped a step; it is a plan whose patches are unverified,
/// produced by exactly the component least able to be relied on for that.
/// </summary>
public enum SchedulingMode
{
    /// <summary>The planner may include this role in a plan. The default, and correct for the
    /// roles that do the mission's actual work.</summary>
    PlannerSelectable,

    /// <summary>Inserted by POLICY whenever its inputs exist, whatever the plan says. Tester,
    /// soldier and verifier — the steps a plan must not be able to omit.</summary>
    PolicyInserted,

    /// <summary>Runs only in response to a typed retryable failure. The medic; never scheduled
    /// speculatively, and bounded by a repair budget.</summary>
    FailureTriggered,

    /// <summary>Runs after the canonical mission evaluation is persisted — a lifecycle worker rather
    /// than a planner task. The archivist.</summary>
    PostFinalization,
}

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
    ModelRequirement? Model = null,
    // v3.8.23. Defaulted to PlannerSelectable so the six contracts that predate this parameter keep
    // their existing meaning without being rewritten — and then four of them immediately override
    // it, because tester/soldier/medic/archivist were never really planner-selectable and the
    // declaration is what makes that checkable.
    SchedulingMode Scheduling = SchedulingMode.PlannerSelectable,
    // Artifact schemas this role needs as INPUT before it can run. v3.8.23.
    //
    // Empty means "no typed input required", which is the honest state for every role today: tasks
    // still hand each other prose, and until that changes a declared input requirement would be a
    // promise the runtime cannot keep. Declared now so the vocabulary exists before the first
    // consumer needs it — the ui_cartographer -> coder dependency is the case that will use it.
    IReadOnlySet<string>? RequiredInputArtifactTypes = null)
{
    private static readonly IReadOnlySet<string> NoInputs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Never null to a caller, for the same reason <see cref="ModelNeeds"/> is not.</summary>
    public IReadOnlySet<string> RequiredInputs => RequiredInputArtifactTypes ?? NoInputs;

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

    /// <summary>
    /// The provider and model that served this ant's call, when one did. v0.3.8.57.
    ///
    /// <see cref="ModelCalls"/> has recorded HOW MANY calls were made since the metrics existed and
    /// never which model made them, so "was this artifact written by the 7B or the 70B" had no
    /// answer. Null for a deterministic ant, and that is itself provenance worth recording: it says
    /// no model was involved rather than that the record is incomplete.
    /// </summary>
    public string? Provider { get; init; }
    public string? Model { get; init; }
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

    /// <summary>
    /// Versioned contracts for EVERY mission role (spec §4.2/§6). Declared in Stage A, enforced in
    /// Stage B (dispatch) and honored by handlers in Stage D. NO role here has apply_patch, ever.
    ///
    /// v3.8.23 — the six CORE ants join. Until this release this table held only the six specialists,
    /// which meant the roles that do almost all of the colony's actual work — researcher, web, file,
    /// coder, builder, verifier — ran with no declared surface at all: no supported task types, no
    /// tool allowlist, no forbidden list, no model requirement. Worse, <c>AntExecutorCatalog</c>
    /// only REQUIRED a contract for specialists, so their absence was not a gap the runtime could
    /// see. The colony's most privileged role (the coder, which produces patches) was its least
    /// specified.
    ///
    /// These six are written from what the handlers MEASURABLY do — the tools they dispatch, the
    /// artifacts they emit, the task types the planner assigns them — not from what the roadmap
    /// wants them to do. Where reality is thinner than the spec, the contract states reality and the
    /// gap is left visible; a contract that describes an aspiration is a contract the runtime will
    /// enforce against work nobody is doing.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, AntExecutionContract> Contracts =
        new Dictionary<string, AntExecutionContract>(StringComparer.OrdinalIgnoreCase)
    {
        // ---- core mission ants (v3.8.23) -------------------------------------------------------

        ["researcher"] = new("researcher", V,
            SupportedTaskTypes: S("research", "section_analysis", "synthesis"),
            RequiredCapabilities: S(Capability.ModelInvoke, Capability.RepoRead, Capability.RepoSearch),
            // v3.8.30: search joins the list, and the HANDLER dispatches it in the same release.
            //
            // The comment here used to explain why `search_workspace` and `repository_index` were
            // deliberately absent — adding a tool to an allowlist does not make a handler call it,
            // and a grant of unused reach is how a role's declared surface stops matching its real
            // one. That reasoning was right and the fix was to do BOTH, not to keep withholding: a
            // researcher that can list a directory but cannot search it is answering "what is in
            // this codebase" by reading folder names.
            AllowedTools: S("system_info", "list_directory", "search_workspace", "repository_index"),
            ForbiddenTools: S("apply_patch", "shell_command", "write_text_file"),
            ProducedArtifactTypes: S("text"),
            AllowedHandoffRoles: S("web", "file", "ui_cartographer", "coder", "builder"),
            AllowsModelCalls: true, AllowsSideEffects: false, ProducesPatchProposals: false,
            // Context, and nothing else. It assembles a brief from the goal, recalled memory and a
            // directory listing, and emits prose — so it needs neither structured output nor tool
            // calling, and claiming either would be a requirement nothing checks against reality.
            // The FIRST draft of this contract said `StructuredOutput: false`, which is identical to
            // ModelRequirement.None and declares nothing at all; AntModelFitnessTests caught it.
            // A declaration that looks like one and isn't is the exact failure this release is about.
            Model: new ModelRequirement(MinContextTokens: 8_000)),

        ["web"] = new("web", V,
            SupportedTaskTypes: S("external_research"),
            RequiredCapabilities: S(Capability.ModelInvoke, Capability.NetworkHttpPublic),
            // system_info is not dispatched by WebResearchAnt today, and is here anyway: the legacy
            // RoleAllowedTools table granted it, and a contract SHORT-CIRCUITS that table. Dropping
            // it would silently narrow the role in the same release that moves where its
            // authorization is declared, and then a break would have two candidate causes. The
            // narrowing can be its own decision, with its own evidence.
            AllowedTools: S("web_search", "system_info"),
            ForbiddenTools: S("apply_patch", "shell_command", "write_text_file"),
            // Genuinely typed since v3.8.21: WebResearchAnt persists a List<SourceRecord> and now
            // emits it as source_set alongside the narrative.
            ProducedArtifactTypes: S("source_set", "text"),
            AllowedHandoffRoles: S("researcher", "builder", "verifier"),
            AllowsModelCalls: true, AllowsSideEffects: false, ProducesPatchProposals: false,
            // Its SourceRecords come from the web_search TOOL, not from parsing model output, so
            // structured output is not required. What it does need is room for several sources and
            // their snippets at once.
            Model: new ModelRequirement(MinContextTokens: 8_000)),

        ["file"] = new("file", V,
            SupportedTaskTypes: S("file_inspection"),
            RequiredCapabilities: S(Capability.RepoRead, Capability.RepoSearch),
            // system_info: legacy-parity, as for web above.
            // v3.8.30: the file ant could READ files it already knew about and could not FIND one.
            // Search and the repository index are what turn "collect the relevant files" from a
            // guess into a query. Dispatched by the handler in this same release.
            AllowedTools: S("list_directory", "read_text_file", "system_info",
                            "search_workspace", "repository_index"),
            ForbiddenTools: S("apply_patch", "shell_command", "write_text_file"),
            ProducedArtifactTypes: S("file_set", "text"),
            AllowedHandoffRoles: S("researcher", "ui_cartographer", "coder", "tester"),
            // The one core ant that needs no model: it reads what is there and reports it. Stating
            // ModelRequirement.None explicitly rather than leaving it null, as tester does.
            AllowsModelCalls: false, AllowsSideEffects: false, ProducesPatchProposals: false,
            Model: ModelRequirement.None),

        ["coder"] = new("coder", V,
            SupportedTaskTypes: S("patch_proposal", "patch", "code_change"),
            // PatchPropose, never PatchApply. The two exist as separate capabilities precisely so
            // this line can grant one and withhold the other.
            RequiredCapabilities: S(Capability.ModelInvoke, Capability.RepoPatchPropose),
            // NO tools, deliberately. The coder proposes; it does not act. Its patch set reaches the
            // world only through the Queen's materialisation and the operator's approval, and
            // giving it apply_patch would collapse propose-then-approve into one step performed by
            // the least accountable component in the system.
            AllowedTools: S(),
            ForbiddenTools: S("apply_patch", "shell_command", "write_text_file"),
            ProducedArtifactTypes: S("patch_set"),
            AllowedHandoffRoles: S("tester", "soldier", "verifier"),
            AllowsModelCalls: true, AllowsSideEffects: false,
            // The only role for which this is true, and the reason its contract matters most.
            ProducesPatchProposals: true,
            // Strict structure by necessity: the output is parsed into a PatchSet and materialised
            // into a real tree. Prose that looks like a patch is not a patch. Reasoning is required
            // because it must hold a change across several files consistently.
            Model: new ModelRequirement(StructuredOutput: true, Reasoning: true)),

        ["builder"] = new("builder", V,
            SupportedTaskTypes: S("build_answer", "synthesis"),
            RequiredCapabilities: S(Capability.ModelInvoke),
            AllowedTools: S(),
            ForbiddenTools: S("apply_patch", "shell_command", "write_text_file"),
            ProducedArtifactTypes: S("text"),
            AllowedHandoffRoles: S("verifier", "scribe"),
            AllowsModelCalls: true, AllowsSideEffects: false, ProducesPatchProposals: false,
            // The largest input of the three prose roles: it reads EVERY prior task result to write
            // the operator's answer. Same shape as the archivist's context argument, one step down —
            // it reads the mission's results rather than its whole history, so 16k rather than 32k.
            // A short window here does not fail; it silently truncates the mission and produces a
            // confident summary of part of it.
            Model: new ModelRequirement(MinContextTokens: 16_000)),

        ["verifier"] = new("verifier", V,
            SupportedTaskTypes: S("verification"),
            RequiredCapabilities: S(Capability.ModelInvoke),
            AllowedTools: S(),
            ForbiddenTools: S("apply_patch", "shell_command", "write_text_file"),
            ProducedArtifactTypes: S("text"),
            AllowedHandoffRoles: S("builder", "scribe", "medic"),
            // AllowsModelCalls stays TRUE and this is the honest, uncomfortable entry in the table.
            // The spec wants the verifier to be a deterministic reader of the evidence store that
            // never treats prose as proof. Today VerifierAnt asks a model and emits a verdict string
            // that MissionVerification parses. That is a real gap, and writing the contract as if it
            // were already closed would hide the one place where a model's opinion still reaches a
            // verification decision. It is bounded by v3.8.22's rule — only deterministic evidence
            // promotes, and a DeterministicBlock cannot be argued away — but it is not yet what the
            // spec describes.
            AllowsModelCalls: true, AllowsSideEffects: false, ProducesPatchProposals: false,
            Model: new ModelRequirement(StructuredOutput: true),
            // v0.3.8.57 — PolicyInserted, which is what the runtime has GUARANTEED since v0.3.8.41.
            //
            // `EnsureVerificationWaitsFor` inserts a verifier when a patch set produces its review
            // evidence, and `EnsureVerificationAfterDeliverable` inserts one when a builder produces
            // a deliverable; both fail CLOSED, setting a DeterministicBlock when insertion is refused
            // so an unverifiable deliverable cannot read as a verified one. The contract meanwhile
            // still said PlannerSelectable — the default, inherited by never being written down.
            //
            // That gap is not cosmetic. The declared mode is what `RoleReadiness` reports, what the
            // API exposes as `scheduling_mode`, and what a reader consults to answer "can verification
            // be skipped by a plan that forgets it". The honest answer became NO in v0.3.8.41 and the
            // table kept saying YES for six releases.
            //
            // A FLOOR, NOT A CEILING (v0.3.8.51's rule): PolicyInserted does not forbid a planned
            // verifier. A plan that asks for verification is asking for more checking, not less, and
            // the gate in AntRegistry deliberately binds only the two modes whose handlers can do
            // nothing but refuse a planned invocation.
            Scheduling: SchedulingMode.PolicyInserted),

        // ---- specialist ants ------------------------------------------------------------------

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
            Model: ModelRequirement.None,
            // v3.8.23: inserted by policy, not chosen by a plan. A plan that omits the tester is not
            // a plan that skipped a step — it is a plan whose patches are unverified.
            Scheduling: SchedulingMode.PolicyInserted),
        ["soldier"] = new("soldier", V,
            SupportedTaskTypes: S("security_review", "patch_risk_review", "permission_review", "policy_review", "scope_review", "dependency_risk_review"),
            RequiredCapabilities: S(Capability.RepoRead),
            // v3.8.23: no tools. SoldierAnt calls PolicyScan as an in-process DETERMINISTIC
            // SERVICE, which is the right shape for it — a policy verdict that must not be
            // influenced by anything a model can reach. `policy_scan` was a contract-only name with
            // no implementation, no registration and no dispatch; wrapping the service in a tool
            // purely to make an inventory look complete would add a call path and no capability.
            AllowedTools: S(),
            ForbiddenTools: S("apply_patch", "shell_command", "write_text_file"),
            ProducedArtifactTypes: S("security_review"),
            AllowedHandoffRoles: S("verifier", "medic", "builder"),
            // v0.3.8.76 — FALSE, which is what has always been true of the code.
            //
            // `SoldierAnt` takes no `ModelRouter` and its whole verdict comes from `PolicyScan`, an
            // in-process deterministic service. It has never been able to make a model call. The
            // contract said `true` with a structured-output requirement, and the comment below it
            // argued — correctly — that a verdict the colony branches on must come back as a schema.
            // Both were reasoning about a model call that does not happen.
            //
            // This is the shape the security review kept finding one layer down: a declaration that
            // reads as a constraint when nothing consults it. The cost was not theoretical. Model
            // fitness grades a role by its declaration, so the soldier was reported UNFIT on any
            // model without structured output — an operator sent to change a model to fix a role
            // that never asks one anything. Five roles did this at once, which is the "seven roles
            // need a capable model" warning the colony has been showing for a capable model that
            // five of them will never call.
            //
            // What is NOT being said: not that the soldier should never use a model. If one is ever
            // wired in, `ModelCallingRoles` in `ModelRouteRequirements` is where the requirement
            // goes, and `ContractDeclarationTests` fails until the two agree.
            AllowsModelCalls: false, AllowsSideEffects: false, ProducesPatchProposals: false,
            Model: ModelRequirement.None,
            // v3.8.23: the review of a state-changing patch set is not optional and must not depend
            // on a model remembering to plan it.
            Scheduling: SchedulingMode.PolicyInserted),
        ["medic"] = new("medic", V,
            SupportedTaskTypes: S("failure_diagnosis", "repair_triage", "retry_classification", "root_cause_analysis", "recovery_recommendation"),
            // v0.3.8.76: `ModelInvoke` drops with the model call it was granting. A capability
            // required for something the role cannot do is reach nothing asked for.
            RequiredCapabilities: S(Capability.RepoRead),
            // v3.8.23: no tools. `read_failure_context` was a contract-only name. The medic reads
            // mission state in process; the reach it actually LACKS is the durable attempt history
            // and the colony's recurring failure classes, and the spec's answer to that is a typed
            // failure_context artifact assembled by orchestration, not a tool the medic dispatches.
            // Removed rather than built, so the contract stops naming something that does not exist.
            AllowedTools: S(),
            ForbiddenTools: S("apply_patch", "shell_command", "write_text_file"),
            ProducedArtifactTypes: S("failure_diagnosis", "repair_recommendation"),
            AllowedHandoffRoles: S("coder", "ui_cartographer", "tester", "builder"),
            // v0.3.8.76 — FALSE. `MedicAnt` takes no `ModelRouter`; it diagnoses from the typed
            // failure signature deterministically, and has since the bounded-repair work.
            //
            // The requirement removed here is the one it hurts most to remove, and it is worth
            // saying why rather than quietly deleting it. It declared Reasoning, and v0.3.8.49 built
            // reasoning-aware rerouting on top of that declaration: `RoleRequiresReasoning` reads it,
            // and reroutes a role to a reasoning-capable model when the configured one cannot infer.
            // For the medic that reroute has never fired on anything, because no medic call reaches
            // the router. The protection was real for the coder and imaginary for the medic, and one
            // declaration served both — so the suite's own test asserted the medic was protected and
            // passed, which is this repository's oldest defect class in the guard that was supposed
            // to be watching for it.
            AllowsModelCalls: false, AllowsSideEffects: false, ProducesPatchProposals: false,
            Model: ModelRequirement.None,
            // v3.8.23: only ever in response to a real failure. MedicAnt.Execute already opens by
            // returning Blocked when no failed task exists — a handler defending itself against a
            // scheduler that should never have called it. This is that rule, declared.
            Scheduling: SchedulingMode.FailureTriggered),
        ["archivist"] = new("archivist", V,
            SupportedTaskTypes: S("memory_consolidation", "lesson_extraction", "negative_memory", "rule_archival", "mission_summary", "skill_candidate_extraction"),
            // v0.3.8.76: drops with the model call. This left the archivist requiring NO capability
            // at all, which is the honest description of a role that reads mission state in process.
            RequiredCapabilities: S(),
            // v3.8.23: no tools, and this one is REDUNDANT rather than missing. The archivist
            // already emits memory_candidate artifacts; ExecutionService.IngestMemoryCandidates
            // turns them into durable events; LearningRecorder rebuilds candidates from those
            // events. A write tool would be a SECOND channel writing the same fact — the "two
            // channels and the prose one wins" failure ADR-004 exists to prevent. Memory persistence
            // stays under the Queen's post-finalization pipeline.
            AllowedTools: S(),
            ForbiddenTools: S("apply_patch", "shell_command", "write_text_file"),
            ProducedArtifactTypes: S("memory_candidate"),
            AllowedHandoffRoles: S(),
            // v0.3.8.76 — FALSE. `ArchivistAnt` has a parameterless constructor: no router, no
            // store, nothing to call. The 32k context requirement removed here was the largest
            // number in the table and the reason the fitness report told operators their model's
            // window was too small for the colony. It was describing a read of mission state that
            // happens in process, over objects, with no window at all.
            AllowsModelCalls: false, AllowsSideEffects: false, ProducesPatchProposals: false,
            Model: ModelRequirement.None,
            // v3.8.23: a lifecycle worker, not a planner task. It reads a TERMINAL mission, and the
            // planner schedules tasks while the mission is still running — so planning it has always
            // meant running it against a mission that cannot yet be summarised.
            Scheduling: SchedulingMode.PostFinalization),
        ["ui_cartographer"] = new("ui_cartographer", V,
            SupportedTaskTypes: S("ui_mapping", "route_mapping", "component_mapping", "style_mapping", "frontend_dependency_mapping", "ui_change_impact"),
            RequiredCapabilities: S(Capability.RepoRead, Capability.RepoSearch),
            AllowedTools: S("list_directory", "read_text_file", "search_workspace", "repository_index"),
            ForbiddenTools: S("apply_patch", "shell_command", "write_text_file"),
            ProducedArtifactTypes: S("ui_map"),
            AllowedHandoffRoles: S("coder", "soldier"),
            // v0.3.8.76 — FALSE, and this is the entry that was hardest to write down.
            //
            // The comment that stood here called this "the clearest case for the whole mechanism":
            // the role exists to walk a repository with tools, and a model that cannot call them
            // does not fail — it is never shown them and maps the UI from priors. Every word of that
            // is true, and none of it was about `UiCartographerAnt`, which takes a `ToolRegistry`,
            // walks the tree itself, and asks no model anything. The argument was so good that it
            // survived three releases of contract review without anyone checking whether the role it
            // described could make the call it was being protected from.
            //
            // Where the concern actually lives, checked rather than assumed. `/agent/run` hands any
            // of the twelve executable roles to `ToolCallingLoop`, so a run under this role name CAN
            // reach a model — and there the protection is already runtime rather than declarative:
            // `ModelRouter.SendCore` reroutes to a tool-capable model whenever a request carries
            // tools and the routed model cannot use them. That reroute reads the REQUEST, not this
            // contract, so it protected the agent-run path the whole time this declaration was
            // protecting nothing. Removing the declaration removes a duplicate of a rule that is
            // enforced where it can actually be true.
            AllowsModelCalls: false, AllowsSideEffects: false, ProducesPatchProposals: false,
            Model: ModelRequirement.None),
        ["scribe"] = new("scribe", V,
            SupportedTaskTypes: S("release_notes", "changelog_update", "operator_documentation", "incident_summary", "verified_change_summary", "docs_patch_proposal"),
            // v0.3.8.76: drops from the ANT. The scribe ROUTE keeps it — see below.
            RequiredCapabilities: S(Capability.RepoRead),
            AllowedTools: S("read_changed_files_summary"),
            ForbiddenTools: S("apply_patch", "shell_command", "write_text_file"),
            ProducedArtifactTypes: S("release_notes", "docs_patch_set"),
            AllowedHandoffRoles: S("verifier", "soldier"),
            // v0.3.8.76 — FALSE for the ANT, and the one place where the distinction this release
            // draws is load-bearing rather than tidy.
            //
            // `ScribeAnt` takes a `ToolRegistry` and no router. But `"scribe"` is ALSO a route name,
            // and `ResultAssembler.ComposeFinalAnswer` calls the router under it to synthesise the
            // operator's final answer — a real model call, on the path that produces the text the
            // operator actually reads. Two different things wear this name: an ant that cannot call
            // a model, and a route that does nothing else.
            //
            // Setting this to `false` and stopping would therefore have REMOVED coverage of the one
            // scribe model call that exists, which is the failure mode this release is about,
            // arriving from the opposite direction. The requirement moves to
            // `ModelRouteRequirements`, where it grades the route that makes the call, and where
            // `planner` and `strategist` — which have always called models and never had a contract
            // to be graded by — join it.
            AllowsModelCalls: false, AllowsSideEffects: false, ProducesPatchProposals: true, // docs paths ONLY (enforced at proposal time)
            Model: ModelRequirement.None),
    };

    public static AntExecutionContract? ContractFor(string roleId) =>
        Contracts.TryGetValue(roleId ?? "", out var c) ? c : null;
}
