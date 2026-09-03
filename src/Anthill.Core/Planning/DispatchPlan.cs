namespace Anthill.Core.Planning;

/// <summary>
/// THE DECISION, WRITTEN DOWN BEFORE ANY WORKER RUNS. v0.3.8.118.
///
/// WHY THIS TYPE EXISTS. Every failure the live tests found shares one cause: nothing recorded what
/// the runtime decided to do, so nothing downstream could check whether it had been done. A
/// verifier reviewed a compiler's narrative because the narrative was the only account of the run
/// that existed. A role appeared to have participated because it was in the registry, not because
/// a record proved it ran. `status: complete` sat beside `checks: 0` because completion was
/// computed from task rows that never mentioned checks.
///
/// A dispatch plan is the missing referent. It is produced deterministically before dispatch,
/// persisted, and is the thing later stages are measured AGAINST rather than a story assembled
/// afterwards from whatever survived truncation.
///
/// IT IS A RECORD, NOT A PROMISE. The plan says what was selected and why; it does not assert that
/// any of it happened. Execution records say that, and they are a separate thing on purpose —
/// conflating "planned" with "ran" is precisely how a registered-but-never-dispatched role came to
/// be presented as having participated.
///
/// SKIPS ARE FIRST-CLASS. `RoleDisposition` carries a reason for every role the colony knows about,
/// including the ones that will not run. The brief's list of distinguishable states — missing task,
/// omitted artifact, unresolved artifact, unavailable tool, skipped role, failed execution — cannot
/// be reported by a runtime that only records what it did.
/// </summary>
public sealed record DispatchPlan(
    string MissionId,
    string RequestedObjective,
    IReadOnlyList<PlannedTask> Tasks,
    IReadOnlyList<RoleDisposition> Roles,
    string? OutputSchema,
    string? PermissionMode,
    string Strategy,
    string StrategyReason,
    IReadOnlyList<string> RequiredChecks,
    IReadOnlyList<string> ClosureRequirements,
    string PlannedAt,
    string PlannerVersion = DispatchPlan.Version)
{
    public const string Version = "dispatch-plan-v1";

    /// <summary>Roles the plan actually intends to dispatch, in plan order.</summary>
    public IReadOnlyList<string> DispatchedRoles =>
        Tasks.Select(t => t.Role).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Roles the operator required that this plan does NOT dispatch AND that nothing else
    /// guarantees. Non-empty means the plan must not proceed as though the request were satisfied —
    /// the brief's rule that a required role which is disabled, unroutable or unsupported cannot be
    /// quietly dropped.
    ///
    /// A policy-inserted role is excluded, and that exclusion is the point rather than a loophole:
    /// the runtime inserts the verifier whenever its inputs exist, so "required but not in the plan"
    /// describes it accurately and "unmet" does not.
    /// </summary>
    public IReadOnlyList<RoleDisposition> UnmetRequiredRoles =>
        Roles.Where(r => r.Required && !r.Dispatched && !r.SatisfiedByPolicy).ToList();
}

/// <summary>
/// One step, after resolution. `Label` is what the operator called it and survives verbatim;
/// `TaskType` is what it resolved TO and is the only field the runtime dispatches on.
///
/// Both are kept because losing either causes a real failure this release is fixing: dropping the
/// label leaves an operator unable to find their own step in the record, and treating the label as
/// the type is what let an arbitrary name be handed to a worker as though it were executable.
/// </summary>
public sealed record PlannedTask(
    string TaskId,
    string Label,
    string TaskType,
    string Role,
    IReadOnlyList<string> DependsOnTaskIds,
    IReadOnlyList<string> ExpectedInputArtifactIds,
    string? ExpectedOutputSchema,
    string Source)
{
    /// <summary>How this task came to be in the plan. Consumers branch on these, never on prose.</summary>
    public static class Sources
    {
        /// <summary>The operator asked for it, by label or by type.</summary>
        public const string Requested = "requested";
        /// <summary>The planner selected it deterministically and recorded why.</summary>
        public const string PlannerSelected = "planner_selected";
        /// <summary>A class-coverage rule supplied it because the class requires it.</summary>
        public const string CoverageSupplied = "coverage_supplied";
    }
}

/// <summary>
/// EVERYTHING KNOWN ABOUT ONE ROLE'S PARTICIPATION, INCLUDING THAT IT HAS NONE.
///
/// The brief asks for ten separate facts per role, and the separation is the point: `Registered`
/// and `Dispatched` were previously collapsed, so a role present in the registry read as a role
/// that had taken part. Every field here answers a different question, and `Reason` is mandatory
/// whenever the answer is negative — "skipped" without a reason is the failure message this
/// repository keeps paying to relearn.
///
/// `Completed` and `Failed` stay false at planning time and are filled from execution records.
/// A plan cannot know them, and a type that let it pretend to would reintroduce the defect.
/// </summary>
public sealed record RoleDisposition(
    string RoleId,
    bool Registered,
    bool Enabled,
    bool Routable,
    bool Dispatchable,
    bool Dispatched,
    bool Required,
    string Reason,
    bool Completed = false,
    bool Failed = false,
    bool Blocked = false,
    bool SatisfiedByPolicy = false)
{
    /// <summary>Stable reason codes. Prose belongs in the detail, not in the branch.</summary>
    public static class Reasons
    {
        public const string Dispatched = "dispatched";
        public const string NotRequested = "not_requested";
        public const string NotRegistered = "not_registered";
        public const string Disabled = "disabled";
        public const string NotRoutable = "not_routable";
        public const string NotPlannerSelectable = "not_planner_selectable";
        public const string NoSupportedTaskType = "no_supported_task_type";

        /// <summary>
        /// The runtime inserts this role whenever its inputs exist, whatever the plan says — the
        /// registry's own words for tester, soldier and verifier: "the steps a plan must not be able
        /// to omit". A required role in this mode is SATISFIED, not unavailable, and refusing a
        /// mission for requiring the very thing the runtime guarantees would be absurd.
        /// </summary>
        public const string PolicyInserted = "policy_inserted";

        /// <summary>Runs only on a typed retryable failure (the medic), or after finalization (the
        /// archivist). Neither can be promised in advance, so requiring one is a real error.</summary>
        public const string LifecycleOnly = "lifecycle_only";
    }
}

/// <summary>
/// Why a plan could not be produced. Same shape as <see cref="MissionPreflight.Blocker"/> on
/// purpose — an operator reading a refusal should not have to learn two vocabularies for it, and
/// the two gates answer adjacent questions at adjacent moments.
/// </summary>
public sealed record PlanBlocker(string Code, string Subject, string Detail)
{
    public override string ToString() => $"{Code} [{Subject}]: {Detail}";

    public static class Codes
    {
        /// <summary>A requested task type no worker contract declares support for.</summary>
        public const string UnsupportedTaskType = "unsupported_task_type";
        /// <summary>A requested output schema no role declares it can produce.</summary>
        public const string UnsupportedOutputSchema = "unsupported_output_schema";
        /// <summary>A requested role the registry does not contain.</summary>
        public const string UnknownRole = "unknown_role";
        /// <summary>A requested role that exists but cannot be dispatched or guaranteed.</summary>
        public const string RoleUnavailable = "role_unavailable";
        /// <summary>
        /// A STEP was requested for a role the runtime inserts by policy. The role itself is fine —
        /// requiring it is honoured — but authoring a task for it would duplicate the one policy
        /// already adds, and two verifier tasks disagreeing is worse than either alone.
        /// </summary>
        public const string RoleIsPolicyInserted = "role_is_policy_inserted";
        /// <summary>A label that resolved to nothing the runtime can execute.</summary>
        public const string UnresolvableTaskLabel = "unresolvable_task_label";
        /// <summary>A dependency naming a step that is not in the request.</summary>
        public const string UnknownDependency = "unknown_dependency";
        /// <summary>A requested role and task type that cannot be paired.</summary>
        public const string RoleTaskTypeMismatch = "role_task_type_mismatch";
    }
}

/// <summary>
/// The planning stage's answer: a plan, or the reasons there isn't one. Never both, and never
/// neither.
///
/// `Ok == false` must stop the mission BEFORE worker dispatch. That is the behaviour the live tests
/// found missing — an unexecutable request was silently replaced with section-analysis tasks, which
/// is worse than a refusal because it produces output an operator may believe.
/// </summary>
public sealed record DispatchPlanResult(DispatchPlan? Plan, IReadOnlyList<PlanBlocker> Blockers)
{
    public bool Ok => Blockers.Count == 0 && Plan is not null;

    public string Explanation => Ok
        ? $"dispatch plan {Plan!.PlannerVersion}: {Plan.Tasks.Count} task(s), strategy {Plan.Strategy} ({Plan.StrategyReason})"
        : "planning refused this request — " + string.Join("; ", Blockers.Select(b => b.ToString()));

    public static DispatchPlanResult Refused(params PlanBlocker[] blockers) => new(null, blockers);
}
