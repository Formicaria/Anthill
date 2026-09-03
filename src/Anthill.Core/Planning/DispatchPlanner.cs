using Anthill.Core.Agents;
using Anthill.Core.Missions;

namespace Anthill.Core.Planning;

/// <summary>
/// THE PRE-DISPATCH PLANNING STAGE. v0.3.8.118.
///
/// A pure function. Given what the operator asked for and what the colony can do, it returns either
/// a plan or the reasons there isn't one. It calls no model, touches no database, dispatches
/// nothing, and reads the clock exactly once so a plan can say when it was made. That purity is not
/// tidiness — it is what makes the deterministic end-to-end test the brief asks for possible at
/// all. A planning stage that needed a live mission to exercise could only ever be checked by
/// running one, which is the position this release is trying to get out of.
///
/// WHAT IT REFUSES, AND WHY REFUSING IS THE FEATURE. The live tests found that an unexecutable
/// request was silently converted into researcher section-analysis tasks. Silent substitution is
/// worse than failure: a refusal costs an operator a minute, while a substitution produces
/// plausible output against a question nobody asked. So every unresolvable thing here becomes a
/// <see cref="PlanBlocker"/> and stops the mission before a worker is invoked.
///
/// THE AUTHORITY IS THE TYPED REGISTRY, NOT A LIST KEPT HERE. Task types come from
/// `AntExecutionCatalog.Contracts[role].SupportedTaskTypes`; output schemas from `ProducedArtifactTypes`;
/// dispatchability from `Scheduling`. `docs/GUARDS.md` puts a typed registry above a source scan for
/// exactly this reason, and a second hand-maintained list of task types in this file would be the
/// `.115` role→sector table defect again with different nouns.
///
/// WHAT IT DELIBERATELY DOES NOT DO. It does not judge whether the requested plan is a GOOD way to
/// answer the request, does not reorder steps it was given, and does not add work the operator did
/// not ask for. Model judgment stays outside this stage, as it does in `MissionPreflight`.
/// </summary>
public static class DispatchPlanner
{
    /// <summary>How a plan's shape was arrived at. Persisted, so "why did it do that" has an answer.</summary>
    public static class Strategies
    {
        /// <summary>The operator supplied a workflow and every step of it resolved.</summary>
        public const string OperatorRequested = "operator_requested";
        /// <summary>No workflow was supplied; the existing planner path decides, as before.</summary>
        public const string PlannerChosen = "planner_chosen";
        /// <summary>Section-by-section ingestion — now only ever a recorded decision.</summary>
        public const string SectionAnalysis = "section_analysis";
    }

    /// <summary>
    /// Plan a mission before anything is dispatched.
    /// </summary>
    /// <param name="missionId">The mission this plan belongs to. A plan with no mission is a draft.</param>
    /// <param name="objective">The operator's objective, verbatim. Recorded, never re-interpreted here.</param>
    /// <param name="requested">
    /// What the operator asked for. <see cref="RequestedWorkflow.None"/> — the ordinary case, and
    /// every mission that ran before this type existed — yields a `planner_chosen` plan that
    /// constrains nothing, so existing behaviour is untouched.
    /// </param>
    /// <param name="contracts">
    /// The role contracts, injected rather than read from the static registry so the tests can plan
    /// against a known colony. Null means "ask the registry", which is what production does.
    /// </param>
    /// <param name="nowIso">The planning instant. Injected for determinism in tests.</param>
    public static DispatchPlanResult Plan(
        string missionId,
        string objective,
        RequestedWorkflow? requested,
        IReadOnlyDictionary<string, AntExecutionContract>? contracts = null,
        string? nowIso = null)
    {
        var reg = contracts ?? AntExecutionCatalog.Contracts;
        var when = nowIso ?? AnthillTime.NowUtc().ToIso();
        var req = requested ?? RequestedWorkflow.None;

        // NOTHING REQUESTED IS NOT AN ERROR. It is how nearly every mission arrives, and a plan
        // that refused it would break every caller that predates this type. The plan records that
        // the planner is choosing, which is itself the fact that was previously unrecorded.
        if (!req.IsSpecified)
            return new DispatchPlanResult(
                new DispatchPlan(
                    MissionId: missionId,
                    RequestedObjective: objective,
                    Tasks: [],
                    Roles: Dispositions(reg, req, dispatched: []),
                    OutputSchema: null,
                    PermissionMode: req.PermissionMode,
                    Strategy: Strategies.PlannerChosen,
                    StrategyReason: "no structured workflow was requested; the planner selects the shape",
                    RequiredChecks: [],
                    ClosureRequirements: ClosureRequirements.Baseline,
                    PlannedAt: when),
                []);

        var blockers = new List<PlanBlocker>();

        // ---- roles ---------------------------------------------------------------------------
        // Every role the operator NAMED is checked before any task is resolved, so a mission that
        // asks for a role the colony does not have fails on that, rather than on the downstream
        // confusion it causes.
        foreach (var role in req.AllNamedRoles())
        {
            if (!reg.TryGetValue(role, out var contract))
            {
                blockers.Add(new PlanBlocker(PlanBlocker.Codes.UnknownRole, role,
                    $"no role '{role}' is registered; registered roles are: {string.Join(", ", reg.Keys.OrderBy(k => k, StringComparer.Ordinal))}"));
                continue;
            }
            /* REQUIRING A POLICY-INSERTED ROLE IS SATISFIED, NOT REFUSED.
               The registry's own words for this mode are "inserted by POLICY whenever its inputs
               exist, whatever the plan says … the steps a plan must not be able to omit" — tester,
               soldier and verifier. Refusing a mission for requiring the one thing the runtime
               guarantees would be absurd, and it would refuse exactly the missions this release
               most wants to succeed: the ones that ask to be verified.

               The lifecycle modes are different and DO refuse. The medic runs only on a typed
               retryable failure and the archivist only after finalization; neither can be promised
               in advance, so "I require the medic" is a request nothing can honour. */
            if (req.RequiredRoles.Contains(role, StringComparer.OrdinalIgnoreCase)
                && !IsDispatchable(contract) && !IsPolicyInserted(contract))
                blockers.Add(new PlanBlocker(PlanBlocker.Codes.RoleUnavailable, role,
                    $"role '{role}' is registered but runs on the {contract.Scheduling} lifecycle, which "
                  + "nothing can promise in advance; it cannot be a required role"));
        }

        // ---- output schema -------------------------------------------------------------------
        // An unsupported schema fails planning. The brief is explicit that silently producing a
        // different one is not acceptable, and it is the same defect class as the task substitution.
        if (!string.IsNullOrWhiteSpace(req.OutputSchema) && !SchemaIsProducible(reg, req.OutputSchema!))
            blockers.Add(new PlanBlocker(PlanBlocker.Codes.UnsupportedOutputSchema, req.OutputSchema!,
                $"no registered role declares it produces artifact schema '{req.OutputSchema}'"));

        // ---- tasks ---------------------------------------------------------------------------
        var planned = new List<PlannedTask>();

        /* IDS ARE ASSIGNED BEFORE RESOLUTION, KEYED ON THE TRIMMED LABEL.
           Before, because a step may depend on one declared after it and an operator writing a
           request should not have to topologically sort it first. Trimmed, because the lookup
           trims and the two must agree: keying the map on the raw label while looking it up by the
           trimmed one silently loses every dependency whose label carried whitespace — the edge
           would simply not be found, and the task would plan as though it had no dependency at
           all. That is the same class of defect as the whole release: a thing quietly not
           happening rather than failing. */
        var idByLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in req.Tasks)
        {
            var key = (t.Label ?? "").Trim();
            if (key.Length == 0) continue;
            if (!idByLabel.ContainsKey(key)) idByLabel[key] = Guid.NewGuid().ToString();
        }

        foreach (var t in req.Tasks)
        {
            var label = (t.Label ?? "").Trim();
            if (label.Length == 0) continue;

            // A DECLARED TYPE IS A CLAIM AND IS CHECKED. A label alone is descriptive and is
            // resolved. The two paths are separate because conflating them is the original defect.
            string? type = null;
            if (!string.IsNullOrWhiteSpace(t.TaskType))
            {
                if (!TypeIsRegistered(reg, t.TaskType!))
                {
                    blockers.Add(new PlanBlocker(PlanBlocker.Codes.UnsupportedTaskType, t.TaskType!,
                        $"task '{label}' declares type '{t.TaskType}', which no registered worker contract supports"));
                    continue;
                }
                type = t.TaskType!.Trim();
            }
            else
            {
                type = ResolveLabel(reg, label, t.Role);
                if (type is null)
                {
                    blockers.Add(new PlanBlocker(PlanBlocker.Codes.UnresolvableTaskLabel, label,
                        $"'{label}' is a description, not a registered task type, and no registered type matches it; "
                      + "declare an explicit task_type or rename the step"));
                    continue;
                }
            }

            var role = (t.Role ?? "").Trim();
            if (role.Length == 0)
            {
                role = RoleFor(reg, type) ?? "";
                if (role.Length == 0)
                {
                    blockers.Add(new PlanBlocker(PlanBlocker.Codes.UnsupportedTaskType, type,
                        $"task type '{type}' has no dispatchable role"));
                    continue;
                }
            }
            else if (reg.TryGetValue(role, out var rc))
            {
                /* A STEP FOR A POLICY-INSERTED ROLE IS REFUSED EVEN THOUGH REQUIRING ONE IS FINE.
                   The role is guaranteed; an operator-authored task for it is not the same thing
                   and would duplicate what policy already adds. Two verifier tasks that disagree
                   are worse than either alone, and the operator has no way to tell which verdict
                   closed the mission. Require the role; do not author its step. */
                if (IsPolicyInserted(rc))
                {
                    blockers.Add(new PlanBlocker(PlanBlocker.Codes.RoleIsPolicyInserted, role,
                        $"the runtime inserts '{role}' tasks itself whenever their inputs exist, so a "
                      + $"requested '{role}' step would duplicate one; list it in required_roles instead "
                      + "of authoring a task for it"));
                    continue;
                }
                if (!rc.SupportsTaskType(type))
                {
                    blockers.Add(new PlanBlocker(PlanBlocker.Codes.RoleTaskTypeMismatch, $"{role}/{type}",
                        $"role '{role}' does not declare support for task type '{type}'"));
                    continue;
                }
            }

            var deps = new List<string>();
            foreach (var raw in t.Dependencies)
            {
                var d = (raw ?? "").Trim();
                if (d.Length > 0 && idByLabel.TryGetValue(d, out var depId)) deps.Add(depId);
                else blockers.Add(new PlanBlocker(PlanBlocker.Codes.UnknownDependency, d,
                    $"task '{label}' depends on '{d}', which is not a step in this request"));
            }

            planned.Add(new PlannedTask(
                TaskId: idByLabel[label],
                Label: label,
                TaskType: type,
                Role: role,
                DependsOnTaskIds: deps,
                ExpectedInputArtifactIds: [],
                ExpectedOutputSchema: t.OutputSchema ?? req.OutputSchema,
                Source: PlannedTask.Sources.Requested));
        }

        if (blockers.Count > 0) return DispatchPlanResult.Refused([.. blockers]);

        var dispatched = planned.Select(p => p.Role).ToList();
        return new DispatchPlanResult(
            new DispatchPlan(
                MissionId: missionId,
                RequestedObjective: objective,
                Tasks: planned,
                Roles: Dispositions(reg, req, dispatched),
                OutputSchema: req.OutputSchema,
                PermissionMode: req.PermissionMode,
                Strategy: Strategies.OperatorRequested,
                StrategyReason: $"the operator requested {planned.Count} step(s) and every one resolved to a registered task type",
                RequiredChecks: [],
                ClosureRequirements: ClosureRequirements.Baseline,
                PlannedAt: when),
            []);
    }

    /// <summary>
    /// What must be true before a mission may report `complete`. Recorded on the plan so the
    /// closure decision is measured against something written down in advance rather than
    /// reconstructed afterwards — the `checks: 0 / status: complete` defect in one line.
    /// </summary>
    public static class ClosureRequirements
    {
        public const string ChecksExist = "at_least_one_reproducible_check";
        public const string VerifierExecuted = "verifier_task_executed";
        public const string VerifierArtifactReachedCompiler = "verifier_artifact_reached_compiler";
        public const string NoUnresolvedRequiredArtifacts = "no_unresolved_required_artifacts";
        public const string NoUnsupportedMajorClaims = "no_unsupported_major_claims";
        public const string RequiredTasksExecuted = "required_tasks_executed_or_evidence_backed_skip";

        public static readonly IReadOnlyList<string> Baseline =
        [
            ChecksExist, VerifierExecuted, VerifierArtifactReachedCompiler,
            NoUnresolvedRequiredArtifacts, NoUnsupportedMajorClaims, RequiredTasksExecuted,
        ];
    }

    /// <summary>A role the planner may put in a plan. Non-planner-selectable roles run on their own
    /// lifecycle triggers and cannot be requested as workflow steps.</summary>
    private static bool IsDispatchable(AntExecutionContract c) =>
        c.Scheduling == SchedulingMode.PlannerSelectable;

    /// <summary>The runtime supplies this role itself whenever its inputs exist. Not dispatchable by
    /// the planner, and not missing either — the distinction the `.118` test run surfaced.</summary>
    private static bool IsPolicyInserted(AntExecutionContract c) =>
        c.Scheduling == SchedulingMode.PolicyInserted;

    private static bool TypeIsRegistered(IReadOnlyDictionary<string, AntExecutionContract> reg, string type)
    {
        var t = (type ?? "").Trim();
        return t.Length > 0 && reg.Values.Any(c => c.SupportedTaskTypes.Contains(t));
    }

    private static bool SchemaIsProducible(IReadOnlyDictionary<string, AntExecutionContract> reg, string schema)
    {
        var s = (schema ?? "").Trim();
        return s.Length > 0 && reg.Values.Any(c => c.ProducedArtifactTypes.Contains(s));
    }

    /// <summary>The first dispatchable role declaring this type, in a stable order so the same
    /// request plans the same way twice.</summary>
    private static string? RoleFor(IReadOnlyDictionary<string, AntExecutionContract> reg, string type) =>
        reg.Where(kv => IsDispatchable(kv.Value) && kv.Value.SupportedTaskTypes.Contains(type))
           .OrderBy(kv => kv.Key, StringComparer.Ordinal)
           .Select(kv => kv.Key)
           .FirstOrDefault();

    /// <summary>
    /// Resolve a descriptive label to a registered task type — EXACT MATCHES ONLY.
    ///
    /// Deliberately not fuzzy. A near-match is how "deep_competitive_scan" becomes "research" and
    /// the operator is never told, which is the substitution this stage exists to stop. If a label
    /// is not itself a registered type, the operator is asked to declare one.
    /// </summary>
    private static string? ResolveLabel(
        IReadOnlyDictionary<string, AntExecutionContract> reg, string label, string? role)
    {
        var l = (label ?? "").Trim();
        if (l.Length == 0) return null;

        if (!string.IsNullOrWhiteSpace(role) && reg.TryGetValue(role!, out var rc))
            return rc.SupportedTaskTypes.Contains(l) ? l : null;

        return TypeIsRegistered(reg, l) ? l : null;
    }

    /// <summary>
    /// One row per registered role, whether or not it takes part. A role the plan does not dispatch
    /// still gets a reason, because "registered" was previously being read as "participated".
    /// </summary>
    private static IReadOnlyList<RoleDisposition> Dispositions(
        IReadOnlyDictionary<string, AntExecutionContract> reg,
        RequestedWorkflow req,
        IReadOnlyList<string> dispatched)
    {
        var required = new HashSet<string>(req.RequiredRoles, StringComparer.OrdinalIgnoreCase);
        var runs = new HashSet<string>(dispatched, StringComparer.OrdinalIgnoreCase);

        return reg.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
        {
            var (id, c) = (kv.Key, kv.Value);
            var dispatchable = IsDispatchable(c);
            var policy = IsPolicyInserted(c);
            var isDispatched = runs.Contains(id);
            var reason =
                isDispatched ? RoleDisposition.Reasons.Dispatched
                : policy ? RoleDisposition.Reasons.PolicyInserted
                : c.Scheduling is SchedulingMode.FailureTriggered or SchedulingMode.PostFinalization
                    ? RoleDisposition.Reasons.LifecycleOnly
                : !dispatchable ? RoleDisposition.Reasons.NotPlannerSelectable
                : c.SupportedTaskTypes.Count == 0 ? RoleDisposition.Reasons.NoSupportedTaskType
                : RoleDisposition.Reasons.NotRequested;

            return new RoleDisposition(
                RoleId: id,
                Registered: true,
                Enabled: true,
                // Routable and Dispatchable are not the same question: a policy-inserted role IS
                // routed and run, just never by the planner. Collapsing them is how "the planner
                // cannot pick it" became "it does not participate".
                Routable: dispatchable || policy,
                Dispatchable: dispatchable,
                Dispatched: isDispatched,
                Required: required.Contains(id),
                Reason: reason,
                SatisfiedByPolicy: policy);
        }).ToList();
    }
}
