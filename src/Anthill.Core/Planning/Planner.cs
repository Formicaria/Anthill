using System.Text.Json.Nodes;
using Anthill.Core.Agents;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Models;

namespace Anthill.Core.Planning;

/// <summary>
/// One reason a proposed plan was not accepted. <c>TaskIndex</c> is the position in the model's
/// `tasks` array, or -1 when the problem is with the plan as a whole.
/// </summary>
public sealed record PlanRejection(int TaskIndex, string Field, string Reason)
{
    public string Describe() =>
        TaskIndex < 0 ? $"plan.{Field}: {Reason}" : $"task[{TaskIndex}].{Field}: {Reason}";
}

/// <summary>
/// The outcome of reading a model's proposed plan: the tasks, or the reasons there are none.
///
/// v3.2.0 (phase) — strict, all-or-nothing schema validation. The parser used to repair a plan
/// in place: an unknown ant dropped that task, an unresolvable dependency dropped that edge, and
/// whatever survived was executed. A five-task plan could become a two-task plan with its ordering
/// removed, and the mission would report success against a graph nobody proposed and nobody
/// reviewed — having spent real time and real model calls getting there.
///
/// Rejections are CARRIED rather than thrown, matching RuntimeProfile.Findings: the caller decides
/// what a bad plan means (here: fall back to the static plan, which someone did review) and the
/// operator gets every reason rather than a count.
/// </summary>
public sealed record PlanParse(IReadOnlyList<Task> Tasks, IReadOnlyList<PlanRejection> Rejections)
{
    /// <summary>A plan is usable only when nothing was rejected AND something was produced.</summary>
    public bool Accepted => Rejections.Count == 0 && Tasks.Count > 0;

    public static PlanParse Reject(params PlanRejection[] reasons) =>
        new(Array.Empty<Task>(), reasons);
}

/// <summary>
/// Turns a mission goal into a task plan. Asks the routed planner model for a strict JSON
/// plan, validates and repairs it (drops invalid ants, guarantees a verifier, clamps count),
/// and falls back to a deterministic static plan whenever the model is unavailable or unusable.
/// Faithful to the Python <c>Planner</c>, prompt and all.
/// </summary>
public sealed class Planner
{
    // Stage D: derived per access (never cached) so specialist rollout gates apply immediately —
    // one canonical role catalog, no duplicated executable lists (spec §7.1).
    private static HashSet<string> AllowedAnts => new(AntRegistry.ExecutableRoleIds, StringComparer.OrdinalIgnoreCase);

    // v2.26.0 pre-V3 hardening: the Planner holds NO per-plan mutable state. The offered skill
    // ids (the set a claimed skill_id is checked against — credit must attach to a procedure the
    // planner was actually shown) used to live in an instance field here. One Planner is shared
    // across concurrent missions, so a second plan starting while another's model call was in
    // flight overwrote the first plan's provenance set. Plan-local data flows through parameters.

    private readonly bool _useOllama;
    private readonly ModelRouter? _router;

    public Planner(bool useOllama, ModelRouter? router)
    {
        _useOllama = useOllama;
        _router = router;
    }

    /// <summary>
    /// True when the request is large enough that planning it as a single broad task would
    /// overflow context. Such missions are handled as specification ingestion: split, analyze
    /// per section, then synthesize. Measured on the raw goal length in characters.
    /// </summary>
    /// <summary>
    /// Planning guidance that the registry's one-line Purpose cannot carry — operational emphasis
    /// ("this is the ONLY ant that changes files") rather than description. Roles absent here fall
    /// back to their registry purpose, so a newly enabled specialist is offered to the planner
    /// immediately with a sensible description instead of being invisible until someone edits a
    /// hardcoded prompt.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> PlannerEmphasis =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["researcher"] = "summarizes local memory, tool context, and mission-relevant internal context.",
            ["web"] = "performs read-only external research when the mission requires current/public information.",
            ["file"] = "inspects workspace files read-only. Use only for file/code/repo/folder missions.",
            ["coder"] = "proposes structured JSON patches to CREATE or MODIFY files (code, config, documentation, scripts). This is the ONLY ant that changes files — any goal that creates, adds, writes, edits, or patches a file needs a coder task.",
            ["builder"] = "creates the final response from prior ant outputs.",
            ["verifier"] = "verifies result quality and safety.",
        };

    public static bool IsLongInput(string goal) =>
        AnthillRuntime.EnableSpecIngestion && (goal ?? "").Length > AnthillRuntime.LongInputThreshold;

    /// <param name="skillContext">Certified/experimental procedures proven in this environment.
    /// Offered as known-good ROUTES to consider, never as scripts to run: every task planned from
    /// one still passes the ordinary authorization, contract and permission gates.</param>
    /// <param name="constraints">
    /// v3.1.0 (ADR-002): the mission's constraints, RESOLVED AT INTAKE and passed in. A
    /// verification-only / read-only / "do not modify files" mission must never have coder
    /// patch-proposal tasks planned for it — and the planner must reach that conclusion from the
    /// same constraint object the admission gate and the evaluator use, not from its own parse of
    /// the goal. v1.8.16 introduced the rule; the planner re-derived it until now.
    /// </param>
    /// <summary>
    /// v0.3.8.98 — <paramref name="specification"/> is the mission's authoritative account of what
    /// it needs (see <see cref="Anthill.Core.Missions.MissionSpecification"/>), and it travels here
    /// for the same reason <paramref name="constraints"/> already does: worker assignment happens
    /// inside this method, so a resolver that cannot see the specification cannot use it. It is
    /// optional because the offline/tooling callers have no mission; null means "requires nothing",
    /// which resolves exactly as this planner did before the parameter existed.
    /// </summary>
    public List<Task> CreateTasks(string goal, MissionConstraints constraints, string memoryContext = "",
        string toolContext = "", string pheromoneContext = "", string skillContext = "",
        Anthill.Core.Missions.MissionSpecification? specification = null)
    {

        // v2.22.0 (made concurrency-safe in v2.26.0): capture exactly which skills THIS plan was
        // shown, locally, so a claimed skill_id is checked against what this plan offered — never
        // against whatever a concurrently running plan happened to be offered.
        var offeredSkillIds = SkillContextIds(skillContext);

        // Long specification / architecture / framework documents are never sent into a single
        // "Analyze Mission Goal" task — they are chunked into bounded, parallel section reviews
        // followed by a synthesis pass. This runs regardless of model availability. (Spec-ingestion
        // plans are already research/synthesis/verify only — no coder tasks — so they honour the
        // no-patch constraint by construction.)
        if (IsLongInput(goal)) return AssignDefaultWorkers(EnsureClassCoverage(CreateSpecIngestionTasks(goal), goal, specification), goal, constraints, specification);

        if (!_useOllama || _router is null) return AssignDefaultWorkers(EnsureClassCoverage(EnforceConstraints(FallbackTasks(goal), goal, constraints), goal, specification), goal, constraints, specification);

        // v0.3.8.98 — THE REQUESTED DELIVERABLES, BY ID, so a task can say which one it serves.
        //
        // Offered rather than demanded: a claim is validated against these ids and an absent one
        // degrades to the ledger's `inferred` credit, so a model that ignores this block plans
        // exactly as it did before. What it buys when the model DOES answer is attribution — a
        // mission that asked three questions and lost one to a failed task can be refused for the
        // question, instead of passing because the other two finished.
        var deliverables = specification?.Deliverables ?? Array.Empty<Anthill.Core.Missions.MissionDeliverable>();
        var deliverableDirective = deliverables.Count == 0 ? "" :
            "- The operator asked for these deliverables. Set `deliverables` on a task to the ids it\n"
          + "  produces, using ONLY the ids listed here. Every id should be served by some task:\n"
          + string.Join("\n", deliverables.Select(d =>
                $"    {d.Id}: {TextUtil.Truncate(d.Request, 160, "...")}")) + "\n";

        var constraintDirective = constraints.BlocksPatches
            ? "\nHARD CONSTRAINT (operator requested verification / read-only / no file changes):\n" +
              "- Do NOT include any coder task or any task_type \"patch_proposal\". Propose NO file changes.\n" +
              "- Use only researcher, web, file (read-only), builder, and verifier ants.\n" +
              "- The mission's job is to inspect, verify, and report — not to modify anything.\n"
            : "";

        // v0.3.8.59 (PLAN.md §1b S9): the persona line is DELETED rather than moved, because
        // RoleSystemPrompt already says "You are the planner worker inside ANTHILL, a local
        // swarm-intelligence harness" — on the system channel, where it has standing. Restating it
        // in the request added nothing except a second, weaker copy of the same claim.
        var prompt = $@"
Available ants:
{RuntimeRoster.PromptBlock(PlannerEmphasis)}

Available tools:
{toolContext}

Memory:
{memoryContext}

Pheromone trail summary. Prefer high-strength matching patterns, but do not force them if the mission does not fit:
{pheromoneContext}

Proven procedures (verified history — consider these routes first; they are not scripts, and any
task you plan from one is still subject to every normal permission and contract check):
{skillContext}

{AnthillRuntime.OperatorRequestBlock("mission goal", goal)}
{constraintDirective}
Rules:
- Return ONLY valid JSON.
Do not wrap JSON in markdown code fences.
- Create between 1 and {AnthillRuntime.MaxDynamicTasks} tasks — as few as the request actually
  needs. A simple informational request is legitimately a SINGLE builder task; add researcher,
  file, or web tasks only when the answer genuinely needs them. Any plan that changes files
  (a coder task) must have at least {AnthillRuntime.MinDynamicTasks} tasks: context before the
  change, verification after it.
- assigned_ant must be one of the ants listed above.
- assigned_worker is optional but, when present, must be a registered worker under assigned_ant.
  Prefer these worker IDs: researcher.repo_researcher, researcher.mission_researcher,
  web.source_finder, web.source_verifier, file.file_scout, file.file_reader,
  coder.backend_coder, coder.ui_coder, coder.docs_coder,
  builder.response_builder, builder.result_compiler,
  verifier.result_verifier, verifier.safety_verifier.
- Keep each task description under 100 words.
- Skip the file ant unless file/code/repo/folder/path keywords appear in the goal.
- Use web only when the mission needs current, public, external, version, price, news, or online information from the internet. Do NOT use web merely because the goal mentions a documentation file or a path.
- If the goal creates, adds, writes, edits, modifies, or patches ANY file — including documentation (.md), config, or a new source file — you MUST include a coder task with task_type ""patch_proposal"" that proposes the change as a structured JSON patch. This is the only way ANTHILL produces file changes; a research/build answer that merely describes the change is NOT sufficient.
- Ants never write to disk directly — the coder only PROPOSES a patch, which a human (or gated auto-apply) applies later through /apply after approval. So proposing a patch via the coder is correct and expected, not a violation.
- Use file/coder for code, scripts, patches, folders, repos, bugs, refactors, and creating or editing any file.
- When the goal asks for a document, report, guide, or summary the operator will READ AS THE ANSWER (not a change to any file), give the builder task task_type ""document_creation"". When it asks for an analysis, comparison, or transformation OF DATA the mission gathers, use ""data_analysis"". These type the deliverable so it is produced as a checkable record; the file rule above still governs anything that touches files.
- Final task should usually be verifier.
- depends_on should usually be [] because ANTHILL auto-wires safe dependencies.
- skill_id is optional. Set it ONLY to the exact id of a proven procedure listed above that this
  task follows. It records which procedure was used so its track record can be updated; it grants
  no extra permission. Never invent an id.
{deliverableDirective}
Required JSON:
{{
  ""tasks"": [
    {{
      ""title"": ""Short title"",
      ""description"": ""Clear task description under 100 words"",
      ""assigned_ant"": ""researcher"",
      ""assigned_worker"": ""researcher.repo_researcher"",
      ""task_type"": ""research"",
      ""depends_on"": []
    }}
  ]
}}
";
        // v3.2.0: the provider's own status decides, not the shape of its prose. An EMPTY model
        // response now falls back too — it never started with "ERROR:", so it used to be handed
        // to the JSON parser as if it were a plan.
        //
        // v0.3.8.76: and the shape is now ALSO asked for on the wire, not only in the prose above.
        // The planner's silent failure is the worst of the four this release wired, because it does
        // not look like a failure: `TasksFromJson` rejects, `Plan` logs to stderr and returns
        // `FallbackTasks`, and the mission runs a generic static plan. An operator sees a colony
        // that ignored their goal, with a green run behind it.
        var result = _router.GenerateTyped("planner", prompt, antName: "planner",
            system: AnthillRuntime.RoleSystemPrompt("planner", goal), schema: PlanSchema);
        var response = result.Content;
        if (!result.Ok)
        {
            Console.Error.WriteLine($"Planner failed to use Ollama ({result.Status.Name()}): {response}");
            Console.Error.WriteLine("Using fallback static task plan.");
            return EnforceConstraints(FallbackTasks(goal), goal, constraints);
        }
        try
        {
            var parsed = Json.ExtractJsonObject(response);
            var plan = TasksFromJson(parsed, goal, offeredSkillIds,
                specification?.Deliverables.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (!plan.Accepted)
            {
                // Every reason, not a count. "Planner dropped 3 invalid task(s)" told an operator
                // that something was wrong and nothing about what, which is the same as telling
                // them nothing — and the plan ran anyway.
                Console.Error.WriteLine($"Dynamic plan REJECTED ({plan.Rejections.Count} problem(s)). Using fallback plan.");
                foreach (var r in plan.Rejections) Console.Error.WriteLine("  " + r.Describe());
                return EnforceConstraints(FallbackTasks(goal), goal, constraints);
            }
            var tasks = plan.Tasks.ToList();
            // Belt-and-suspenders: even with the prompt directive, a small model may still emit a
            // coder patch task on a verification-only mission. Strip them deterministically.
            return AssignDefaultWorkers(EnsureClassCoverage(EnforceConstraints(tasks, goal, constraints), goal, specification), goal, constraints, specification);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Dynamic planner parse failed: {error.Message}");
            return AssignDefaultWorkers(EnsureClassCoverage(EnforceConstraints(FallbackTasks(goal), goal, constraints), goal, specification), goal, constraints, specification);
        }
    }

    /// <summary>
    /// v1.8.16 planner constraint enforcement. When the mission is verification-only / read-only /
    /// no-patch, deterministically removes every patch-producing task (coder ant or
    /// <c>patch_proposal</c> task type) and drops now-orphaned dependencies on them. If removing the
    /// coder task would leave nothing to inspect the workspace, a read-only file-inspection task is
    /// substituted so verification missions still actually look at the files. A verifier is always
    /// guaranteed. Missions without a no-patch constraint pass through unchanged.
    /// </summary>
    /// <summary>
    /// v0.3.8.93 — whether a task is CONSEQUENTIAL: it proposes changes rather than answering a
    /// question. One definition, shared by the plan-size minimum (small consequential plans are
    /// rejected), the constraint stripper (no-patch missions remove exactly these), and the
    /// runtime verification policy (consequential plans always get a verifier). Three readers of
    /// one rule, so they cannot disagree about what "consequential" means.
    /// </summary>
    internal static bool IsConsequential(Task t) =>
        t.AssignedAnt == "coder" || t.TaskType is "patch_proposal" or "patch" or "code_change";

    internal static List<Task> EnforceConstraints(List<Task> tasks, string goal, MissionConstraints constraints)
    {
        if (!constraints.BlocksPatches || tasks.Count == 0) return tasks;

        var removedIds = tasks.Where(IsConsequential).Select(t => t.Id).ToHashSet();
        var kept = tasks.Where(t => !IsConsequential(t)).ToList();

        // Drop dependencies that pointed at removed tasks so the scheduler can't deadlock.
        foreach (var t in kept)
            t.DependsOn = t.DependsOn.Where(d => !removedIds.Contains(d)).ToList();

        // Guarantee the mission still inspects the workspace if it names files/code/paths.
        var mentionsFiles = new[] { "file", "code", "repo", "path", "folder", "directory", ".cs", ".md", ".json", "config" }
            .Any(k => goal.ToLowerInvariant().Contains(k));
        if (mentionsFiles && !kept.Any(t => t.AssignedAnt == "file"))
            kept.Insert(0, new Task
            {
                Title = "Inspect workspace files (read-only)",
                Description = $"List relevant workspace files and read safe text files to verify — do NOT modify anything: {goal}",
                AssignedAnt = "file", AssignedWorker = "file.file_reader", TaskType = "file_inspection",
            });

        if (kept.Count == 0)
            kept.Add(new Task
            {
                Title = "Research and report",
                Description = $"Investigate and report on the mission without changing any files: {goal}",
                AssignedAnt = "researcher", AssignedWorker = "researcher.mission_researcher", TaskType = "research",
            });

        if (!kept.Any(t => t.AssignedAnt == "verifier"))
            kept.Add(new Task
            {
                Title = "Verify findings",
                Description = $"Check the inspection/verification result for accuracy and completeness: {goal}",
                AssignedAnt = "verifier", AssignedWorker = "verifier.result_verifier", TaskType = "verification",
            });
        return kept;
    }

    /// <summary>
    /// THE COVERAGE A MISSION CLASS REQUIRES, guaranteed deterministically. v0.3.8.98.
    ///
    /// A system audit that plans no inspection step produces an assessment of what the model
    /// already believed — which is mission `7afd85b2`'s shape exactly: tasks completed, nothing
    /// read, findings asserted. The planner is a model and may omit the step; whether the mission
    /// class REQUIRES it is not a modelling question, so it is answered here.
    ///
    /// Same rule and same place as <see cref="EnforceConstraints"/>'s guaranteed verifier: only
    /// what is MISSING is added, an audit is the only class this acts on at v0.3.8.98, and every
    /// inserted task passes the ordinary authorization and permission gates below like any other.
    /// Workers are left unassigned on purpose — which worker serves an inspection is the
    /// specification's question, answered by <see cref="Agents.WorkerResolution"/> a few lines down.
    /// </summary>
    internal static List<Task> EnsureClassCoverage(List<Task> tasks, string goal,
        Anthill.Core.Missions.MissionSpecification? specification)
    {
        if (specification?.MissionClass != Anthill.Core.Missions.MissionSpecification.SystemAuditClass)
            return tasks;

        // READ-ONLY, stated in the description because that text reaches the worker. The file ant
        // holds no write permission at all, so this is a description of the work rather than a
        // restraint on it — but a task that reads as ambiguous invites a plan repair that is not.
        if (!tasks.Any(t => string.Equals(t.AssignedAnt, "file", StringComparison.OrdinalIgnoreCase)))
            tasks.Insert(0, new Task
            {
                Title = "Inspect the workspace (read-only)",
                Description = "List and read the repository files relevant to this assessment. "
                            + "Do not modify anything — this is an observation, and its findings "
                            + $"are the evidence the assessment must rest on: {goal}",
                AssignedAnt = "file",
                TaskType = "file_inspection",
                RequiredCapability = Anthill.Core.Missions.WorkerCapabilities.InspectRepository,
            });

        // THE RUNTIME HALF. "What is implemented" and "what is enabled right now" are different
        // questions against different sources, and an audit that answers only the first reads the
        // source code and calls it the state of the colony. The task names the CAPABILITY rather
        // than a worker: the registry still decides who serves it, so a better runtime worker later
        // is reached without editing this step.
        if (!tasks.Any(t => string.Equals(t.RequiredCapability, Anthill.Core.Missions.WorkerCapabilities.InspectRuntimeState,
                                          StringComparison.OrdinalIgnoreCase)))
            tasks.Insert(0, new Task
            {
                Title = "Inspect the live colony state (read-only)",
                Description = "Report the colony's current runtime state — which roles and workers "
                            + "are executable and what they declare, which tools this run registered, "
                            + "the verification policy in force, and what has already run. Read only; "
                            + $"change nothing: {goal}",
                AssignedAnt = "researcher",
                TaskType = "research",
                RequiredCapability = Anthill.Core.Missions.WorkerCapabilities.InspectRuntimeState,
            });

        if (!tasks.Any(t => string.Equals(t.AssignedAnt, "builder", StringComparison.OrdinalIgnoreCase)))
            tasks.Add(new Task
            {
                Title = "Compile the assessment",
                Description = $"Assemble the findings into the assessment the operator asked for: {goal}",
                AssignedAnt = "builder",
                TaskType = "synthesis",
            });

        if (!tasks.Any(t => string.Equals(t.AssignedAnt, "verifier", StringComparison.OrdinalIgnoreCase)))
            tasks.Add(new Task
            {
                Title = "Verify the assessment",
                Description = "Check that the assessment answers every question the operator asked "
                            + $"and is supported by what was actually inspected: {goal}",
                AssignedAnt = "verifier",
                TaskType = "verification",
            });

        return tasks;
    }

    private static List<Task> AssignDefaultWorkers(List<Task> tasks, string goal, MissionConstraints constraints,
        Anthill.Core.Missions.MissionSpecification? specification = null)
    {
        var valid = new List<Task>();
        foreach (var task in tasks)
        {
            task.AssignedAnt = (task.AssignedAnt ?? "").Trim().ToLowerInvariant();
            task.TaskType = string.IsNullOrWhiteSpace(task.TaskType)
                ? TextUtil.InferTaskType(task.AssignedAnt, task.Title, task.Description)
                : task.TaskType.Trim().ToLowerInvariant();
            // v0.3.8.98 — THIS is where a blank worker is filled, and therefore where the mission's
            // declared capabilities have to be consulted. The capability branch used to live in
            // `PlanningService`, downstream of this line, where it could never fire: this call had
            // already made the worker non-blank on every planner path. One resolver, at the first
            // place the question is asked, recording what decided it.
            if (string.IsNullOrWhiteSpace(task.AssignedWorker))
                WorkerResolution.Assign(task, goal, specification);
            var result = AntRegistry.ValidateTask(task, constraints);
            if (!result.Allowed)
            {
                Console.Error.WriteLine($"Planner rejected task '{task.Title}': {result.Reason}");
                continue;
            }
            valid.Add(task);
        }
        // Execution framework Stage E: deterministic specialist routing. For a UI-modification
        // goal, the UI cartographer maps the real frontend BEFORE the coder proposes changes —
        // but only when its rollout gates are open, and never for backend-only work (spec §7.3/§7.4).
        valid = InjectSpecialistRouting(valid, goal);

        // v2.9.0 contract gate (NORTH_STAR V3-track Phase 2): every path out of the planner funnels
        // through here — planner output is schema-validated against its TaskContract projection and
        // invalid tasks cannot enter the execution queue. Rejections are loud, never silent.
        return Contracts.ContractGate.Admit(valid, reason => Console.Error.WriteLine(reason));
    }

    internal static List<Task> InjectSpecialistRouting(List<Task> tasks, string goal)
    {
        // v0.3.8.57 — ONE detector, shared with UiChangeGate. This list used to live here and
        // nowhere else; the dispatch gate that now enforces the map would have needed its own copy,
        // and two lists mean the planner eventually routes a set the gate does not guard (or the
        // reverse, which blocks work nothing was planned to map).
        //
        // The planner reasons about the GOAL and the gate also reads task paths. That is not a
        // disagreement: at planning time the tasks the check would read do not exist yet, so the
        // planner asks the question with what it has and the gate asks it again with more.
        var uiGoal = Agents.UiChangeGate.LooksLikeUiWork(goal, null);
        var hasCoder = tasks.Any(t => t.AssignedAnt == "coder");
        var hasMapper = tasks.Any(t => t.AssignedAnt == "ui_cartographer");
        if (uiGoal && hasCoder && !hasMapper && AntRegistry.ExecutableRoleIds.Contains("ui_cartographer"))
        {
            var mapTask = new Task
            {
                Title = "Map the frontend (read-only)",
                Description = $"Produce a structured UI map (routes, functions, API calls, modification points) before any UI change: {goal}",
                AssignedAnt = "ui_cartographer", TaskType = "ui_mapping",
            };
            var coderIndex = tasks.FindIndex(t => t.AssignedAnt == "coder");
            tasks.Insert(coderIndex, mapTask);
            tasks[coderIndex + 1].DependsOn.Add(mapTask.Id); // coder waits for the real map
        }
        return tasks;
    }

    /// <summary>
    /// The ids inside a rendered skill-context block. Parsing the block the model actually saw
    /// keeps "what was offered" and "what may be claimed" from drifting apart — the alternative,
    /// passing the registry in separately, has two sources of truth and no check that they agree.
    /// </summary>
    internal static HashSet<string> SkillContextIds(string? skillContext)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (skillContext ?? "").Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal)) continue;
            var rest = trimmed[2..];
            var stop = rest.IndexOf(" [", StringComparison.Ordinal);
            if (stop > 0) ids.Add(rest[..stop].Trim());
        }
        return ids;
    }

    // Internal for the v2.26.0 concurrency test: the method is pure (no Planner state), and the
    // test proves two interleaved parses with different offered sets cannot cross-contaminate.
    /// <summary>
    /// The task graph's shape, on the wire. v0.3.8.76 (PLAN.md §2 R1).
    ///
    /// Mirrors the "Required JSON" block in the prompt and is pinned to it by
    /// `StructuredOutputTests`. `assigned_ant` is deliberately NOT an enum here even though
    /// `TasksFromJson` rejects unknown ants: the roster is configurable, an enum baked into a schema
    /// would be a second copy of it, and the two would disagree the first time a role was disabled.
    /// Rejection stays where it can read the live roster; the schema constrains the SHAPE, which is
    /// the part that is constant.
    ///
    /// TWO THINGS THIS SCHEMA MUST NOT DO, both found by checking it against the parser rather than
    /// against the prompt, and both of which would have turned a fix into an outage:
    ///
    ///   * `depends_on` is NOT an array of integers. `TasksFromJson` reads each element with
    ///     `ToString()` and the resolver immediately below it exists because "LLMs often emit
    ///     non-ID dependency references: integer indices or task titles" — both are accepted and
    ///     normalised. Typing it as `integer` would make the schema reject, at the provider, the
    ///     exact output the parser was written to tolerate.
    ///
    ///   * `skill_id` is optional in the prompt, absent from its example, and READ by the parser.
    ///     With `additionalProperties: false` and no entry here, a schema-honouring provider would
    ///     have been forbidden from emitting the one field that records which proven procedure a
    ///     task followed — silently ending skill attribution rather than breaking anything visible.
    /// </summary>
    internal const string PlanSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["tasks"],
          "properties": {
            "tasks": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["title", "description", "assigned_ant", "task_type"],
                "properties": {
                  "title": { "type": "string" },
                  "description": { "type": "string" },
                  "assigned_ant": { "type": "string" },
                  "assigned_worker": { "type": "string" },
                  "task_type": { "type": "string" },
                  "skill_id": { "type": "string" },
                  "deliverables": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "depends_on": {
                    "type": "array",
                    "items": { "type": ["string", "integer"] }
                  }
                }
              }
            }
          }
        }
        """;

    internal PlanParse TasksFromJson(JsonObject parsed, string goal, IReadOnlySet<string> offeredSkillIds,
        IReadOnlySet<string>? offeredDeliverableIds = null)
    {
        offeredDeliverableIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejections = new List<PlanRejection>();
        if (parsed["tasks"] is not JsonArray rawTasks)
            return PlanParse.Reject(new PlanRejection(-1, "tasks", "the plan has no `tasks` array"));

        var tasks = new List<Task>();
        var index = -1;
        foreach (var item in rawTasks.Take(AnthillRuntime.MaxDynamicTasks))
        {
            index++;
            if (item is not JsonObject obj)
            {
                rejections.Add(new PlanRejection(index, "task", "entry is not a JSON object"));
                continue;
            }
            // Normalisation, not repair: a missing title or description loses no structure, and the
            // graph the model proposed is unchanged. Contrast with an unknown ant or an
            // unresolvable edge below, which change WHICH WORK RUNS and are therefore rejections.
            var title = (obj["title"]?.GetValue<string>() ?? "").Trim();
            if (title.Length == 0) title = "Task";
            var description = (obj["description"]?.GetValue<string>() ?? "").Trim();
            if (description.Length == 0) description = $"Handle part of the mission: {goal}";
            var assignedAnt = (obj["assigned_ant"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
            if (!AllowedAnts.Contains(assignedAnt))
            {
                rejections.Add(new PlanRejection(index, "assigned_ant",
                    assignedAnt.Length == 0 ? "missing" : $"'{assignedAnt}' is not a planner-eligible role"));
                continue;
            }
            var assignedWorker = (obj["assigned_worker"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
            var taskType = (obj["task_type"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
            if (taskType.Length == 0) taskType = TextUtil.InferTaskType(assignedAnt, title, description);
            else
            {
                // A model may invent a task_type — "analysis" for a researcher, say. Now that the
                // executor blocks work outside a role's contract, an invented word would fail the
                // task mid-mission, having already paid for the plan. Replaced with the role's own
                // inferred type instead: NORMALISATION by this file's rule, since it changes
                // neither which ant runs nor the ordering, only the label the contract is checked
                // against. Roles without a contract keep whatever the model said, because there is
                // nothing to be outside of.
                var roleContract = AntExecutionCatalog.ContractFor(assignedAnt);
                if (roleContract is not null && !roleContract.SupportsTaskType(taskType))
                    taskType = TextUtil.InferTaskType(assignedAnt, title, description);
            }
            var dependsOn = (obj["depends_on"] as JsonArray)?.Select(n => n?.ToString() ?? "").Where(s => s.Length > 0).ToList() ?? new();
            // v2.22.0: skill provenance. Accepted ONLY when it names a procedure the registry
            // actually offered for this plan — a model must not be able to invent an id and have
            // the outcome credited to it, or to a skill it was never shown.
            var claimedSkill = (obj["skill_id"]?.GetValue<string>() ?? "").Trim();
            var skillId = offeredSkillIds.Contains(claimedSkill) ? claimedSkill : null;
            // v0.3.8.98 — WHICH REQUESTED DELIVERABLE THIS TASK SERVES, accepted only when it names
            // an id the specification actually holds. Same rule as `skill_id` above and for the same
            // reason: a model must not be able to invent an identifier and have the ledger credit
            // work to it. An unknown id is DROPPED rather than rejecting the plan — a mis-typed
            // claim degrades this task to the inferred credit every unclaiming task already gets,
            // which is a weaker record and not a broken mission.
            var claimedDeliverables = (obj["deliverables"] as JsonArray)?
                .Select(n => (n?.ToString() ?? "").Trim())
                .Where(id => id.Length > 0 && offeredDeliverableIds.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
            tasks.Add(new Task { Title = title, Description = description, AssignedAnt = assignedAnt, AssignedWorker = assignedWorker.Length == 0 ? null : assignedWorker, TaskType = taskType, DependsOn = dependsOn, SkillId = skillId, DeliverableIds = claimedDeliverables });
        }

        // LLMs often emit non-ID dependency references: integer indices ([0],[1]) or task titles.
        // Build lookup maps and resolve everything to real task IDs.
        var idByIndex = tasks.Select((t, i) => (t, i)).ToDictionary(x => x.i, x => x.t.Id);

        // Titles are NOT unique and nothing ever made them so. `ToDictionary` here threw
        // ArgumentException the moment a model emitted two tasks with the same title — which small
        // models do routinely, and which the untitled-task default ("Task") makes likelier still.
        // The throw was invisible in production because CreatePlan's catch-all turned it into
        // "parse failed → fallback", so a crash was being read as a bad plan for releases.
        //
        // A repeated title is not fatal by itself: it only matters if a dependency REFERS to it,
        // and then it is genuinely ambiguous — two tasks answer to that name and picking either is
        // a guess about ordering. Recorded here, rejected below only if something actually
        // references it.
        var idByTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguousTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tasks)
        {
            var key = t.Title.Trim();
            if (!idByTitle.TryAdd(key, t.Id)) ambiguousTitles.Add(key);
        }

        for (int i = 0; i < tasks.Count; i++)
        {
            var resolved = new List<string>();
            foreach (var dep in tasks[i].DependsOn)
            {
                // Integer index
                if (int.TryParse(dep, out var idx) && idx >= 0 && idx < tasks.Count && idx != i)
                { resolved.Add(idByIndex[idx]); continue; }
                // Exact task title — unless more than one task answers to it, in which case the
                // ordering the model asked for cannot be recovered and guessing is not allowed.
                if (ambiguousTitles.Contains(dep.Trim()))
                {
                    rejections.Add(new PlanRejection(i, "depends_on",
                        $"'{TextUtil.Truncate(dep, 60)}' is ambiguous — several tasks share that title"));
                    continue;
                }
                if (idByTitle.TryGetValue(dep.Trim(), out var titleId) && titleId != tasks[i].Id)
                { resolved.Add(titleId); continue; }
                // Already a valid task ID
                if (tasks.Any(t => t.Id == dep)) { resolved.Add(dep); continue; }

                // An edge that cannot be resolved used to be dropped here "so the scheduler doesn't
                // deadlock". That traded a deadlock for something worse and quieter: the task ran
                // anyway, out of order, against inputs its author said it needed. The ordering the
                // model expressed is part of the plan, so losing an edge rejects the plan.
                rejections.Add(new PlanRejection(i, "depends_on",
                    $"'{TextUtil.Truncate(dep, 60)}' matches no task in this plan"));
            }
            tasks[i].DependsOn = resolved.Distinct().ToList();
        }

        // Only when nothing else was rejected. A rejected task is not a usable one, so a plan that
        // already failed on a bad role would ALSO report "below the minimum" — a consequence of the
        // first problem presented as a second, co-equal one. The operator needs the cause, not the
        // count of symptoms it produced.
        //
        // v0.3.8.93 — THE MINIMUM APPLIES TO CONSEQUENTIAL PLANS, NOT TO EVERY PLAN. The guard was
        // split, not weakened. What it permanently protects: a plan that CHANGES something must
        // carry enough structure to be reviewed — a lone coder task with no research behind it and
        // no verification after it is exactly what three-task minimum was for, and that half stays
        // strict. What expired: forcing an informational request ("what does this constant do?")
        // to ship three tasks. Below the minimum, a small informational plan used to be rejected
        // here and silently replaced by FallbackTasks — the v0.3.8.82 defect shape: the operator's
        // request answered by a plan nobody wrote, at three model calls instead of one. A one-task
        // informational plan is now a legitimate DECLARED outcome; zero tasks is still a rejection,
        // because an empty plan is not a small plan.
        if (rejections.Count == 0 && tasks.Count == 0)
            rejections.Add(new PlanRejection(-1, "tasks", "the plan contains no usable tasks"));
        if (rejections.Count == 0 && tasks.Count < AnthillRuntime.MinDynamicTasks
            && tasks.Any(IsConsequential))
            rejections.Add(new PlanRejection(-1, "tasks",
                $"{tasks.Count} usable task(s), below the minimum of {AnthillRuntime.MinDynamicTasks} "
              + "for a plan containing consequential (patch-producing) work"));

        // ALL OR NOTHING. A plan is a graph, and a graph missing a node or an edge is not a smaller
        // version of the same plan — it is a different one that nothing reviewed. Executing it
        // spent real time and real model calls on work the operator never approved, and it looked
        // like success. Rejecting sends the mission to the static fallback, which is a plan someone
        // did review.
        if (rejections.Count > 0) return new PlanParse(Array.Empty<Task>(), rejections);
        // v0.3.8.93: the verifier is guaranteed for CONSEQUENTIAL plans only — same split as the
        // size minimum above, and as PlanningService.EnsurePlanVerification, which is the runtime
        // half of this same rule. An informational plan keeps the shape the model proposed.
        if (tasks.Any(IsConsequential) && !tasks.Any(t => t.AssignedAnt == "verifier"))
            tasks.Add(new Task
            {
                Title = "Verify mission output",
                Description = $"Check the final result for accuracy, completeness, and usefulness: {goal}",
                AssignedAnt = "verifier", AssignedWorker = "verifier.result_verifier", TaskType = "verification",
            });
        return new PlanParse(tasks.Take(AnthillRuntime.MaxDynamicTasks).ToList(), Array.Empty<PlanRejection>());
    }

    /// <summary>
    /// Specification-ingestion plan: one bounded analysis task per document section (non-critical,
    /// runnable in parallel), then a synthesis task that depends on all of them, then verification.
    /// Section tasks are non-critical so a single failed/timed-out section never skips synthesis —
    /// the synthesis still runs against whatever sections completed. Faithful to the long-input rule.
    /// </summary>
    public static List<Task> CreateSpecIngestionTasks(string goal)
    {
        var sections = SplitIntoSections(goal, AnthillRuntime.MaxSectionChars, AnthillRuntime.MaxSectionTasks);
        if (sections.Count == 0) sections.Add(TextUtil.Truncate(goal, AnthillRuntime.MaxSectionChars, "...[section truncated]"));

        var tasks = new List<Task>();
        var sectionIds = new List<string>();
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var task = new Task
            {
                Title = $"Analyze section {i + 1} of {sections.Count}",
                Description =
                    $"You are reviewing ONE section of a larger specification/architecture document. " +
                    $"Analyze ONLY this section. Extract: (1) concrete requirements and rules, (2) any " +
                    $"named components, tasks, or roles, (3) constraints, limits, and edge cases, " +
                    $"(4) open questions. Be concise and structured. Do not attempt to cover the whole document.\n\n" +
                    $"--- SECTION {i + 1}/{sections.Count} START ---\n{section}\n--- SECTION {i + 1}/{sections.Count} END ---",
                AssignedAnt = "researcher",
                AssignedWorker = "researcher.mission_researcher",
                TaskType = "section_analysis",
                Critical = false, // a failed section must not abort the mission
                MaxAttempts = 2,  // route timeouts back for one bounded retry with the same (already small) scope
            };
            tasks.Add(task);
            sectionIds.Add(task.Id);
        }

        var synthesis = new Task
        {
            Title = "Synthesize condensed implementation plan",
            Description =
                "Combine the per-section analyses above into ONE condensed implementation plan. " +
                "Produce: (1) a short overview of what the document asks for, (2) a deduplicated, ordered " +
                "list of concrete requirements/rules, (3) a proposed task breakdown (which work items, in what " +
                "order, with dependencies), and (4) risks and open questions. If some sections are missing " +
                "because their analysis failed, proceed with the sections that succeeded and note the gap.",
            AssignedAnt = "builder",
            AssignedWorker = "builder.result_compiler",
            TaskType = "synthesis",
            DependsOn = new List<string>(sectionIds),
            Critical = true,
            MaxAttempts = 1,
        };
        tasks.Add(synthesis);

        tasks.Add(new Task
        {
            Title = "Verify synthesized plan",
            Description = "Check the synthesized implementation plan for accuracy, completeness against the " +
                          "section analyses, internal consistency, and missing steps. Note any section gaps.",
            AssignedAnt = "verifier",
            AssignedWorker = "verifier.result_verifier",
            TaskType = "verification",
            DependsOn = new List<string> { synthesis.Id },
            Critical = true,
        });

        return tasks;
    }

    /// <summary>
    /// Splits a document into ordered chunks, each at most <paramref name="maxSectionChars"/> characters,
    /// preferring natural boundaries (markdown headings, ALL-CAPS label lines, then blank-line paragraphs).
    /// The number of chunks is capped at <paramref name="maxSections"/>; overflow is merged into the last chunk.
    /// </summary>
    public static List<string> SplitIntoSections(string text, int maxSectionChars, int maxSections)
    {
        var normalized = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
        if (normalized.Trim().Length == 0) return new List<string>();

        // 1) Prefer structural blocks: a heading/label line starts a new block.
        var lines = normalized.Split('\n');
        var blocks = new List<string>();
        var current = new System.Text.StringBuilder();
        bool IsBoundary(string line)
        {
            var t = line.Trim();
            if (t.Length == 0) return false;
            if (t.StartsWith("#")) return true;                                    // markdown heading
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Z][A-Z0-9 _\-]{3,}:?$")) return true; // ALL_CAPS label
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d+[\.\)]\s+\S")) return true;           // numbered heading
            return false;
        }
        foreach (var line in lines)
        {
            if (IsBoundary(line) && current.Length > 0)
            {
                blocks.Add(current.ToString());
                current.Clear();
            }
            current.Append(line).Append('\n');
        }
        if (current.Length > 0) blocks.Add(current.ToString());

        // 2) If structure was too coarse, fall back to blank-line paragraph blocks.
        if (blocks.Count < 2)
            blocks = System.Text.RegularExpressions.Regex.Split(normalized, @"\n\s*\n")
                .Where(b => b.Trim().Length > 0).ToList();
        if (blocks.Count == 0) blocks.Add(normalized);

        // 3) Hard-split any single block that on its own exceeds the cap.
        var sized = new List<string>();
        foreach (var block in blocks)
        {
            if (block.Length <= maxSectionChars) { sized.Add(block); continue; }
            for (var i = 0; i < block.Length; i += maxSectionChars)
                sized.Add(block.Substring(i, Math.Min(maxSectionChars, block.Length - i)));
        }

        // 4) Greedily pack consecutive blocks up to the cap.
        var chunks = new List<string>();
        var buffer = new System.Text.StringBuilder();
        foreach (var block in sized)
        {
            if (buffer.Length > 0 && buffer.Length + block.Length > maxSectionChars)
            {
                chunks.Add(buffer.ToString().Trim());
                buffer.Clear();
            }
            buffer.Append(block);
        }
        if (buffer.Length > 0) chunks.Add(buffer.ToString().Trim());

        // 5) Enforce the section-count cap by merging the overflow into the final chunk.
        if (chunks.Count > maxSections)
        {
            var head = chunks.Take(maxSections - 1).ToList();
            var tail = string.Join("\n\n", chunks.Skip(maxSections - 1));
            if (tail.Length > maxSectionChars) tail = tail.Substring(0, maxSectionChars);
            head.Add(tail);
            chunks = head;
        }

        return chunks.Where(c => c.Trim().Length > 0).ToList();
    }

    private static List<Task> FallbackTasks(string goal)
    {
        var lowered = goal.ToLowerInvariant();
        var codeKeywords = new[] { "code", "script", "python", "bug", "debug", "review", "refactor", "function", "class", "repo", "repository", "file", "folder", "directory", "patch", "modify", "change", "create", "add", "write", "edit", "document", "docs/", ".md", ".cs", ".json", "ui", "frontend", "canvas", "css", "html", "javascript", "visualization", "dashboard" };
        var isCodeGoal = codeKeywords.Any(lowered.Contains);

        // A goal that creates/edits a file must reach the coder — check it BEFORE the web branch,
        // so "create a docs file" produces a patch rather than a research answer that never lands.
        if (!isCodeGoal && AnthillRuntime.EnableWebSearch && TextUtil.ShouldUseWebSearch(goal))
            return new()
            {
                new() { Title = "Frame research need", Description = $"Identify what current/public information is needed for: {goal}", AssignedAnt = "researcher", AssignedWorker = "researcher.mission_researcher", TaskType = "research" },
                new() { Title = "External web research", Description = $"Run read-only web research and save source records for: {goal}", AssignedAnt = "web", AssignedWorker = "web.source_finder", TaskType = "external_research" },
                new() { Title = "Build sourced response", Description = $"Create a concise answer using internal context and saved source summaries: {goal}", AssignedAnt = "builder", AssignedWorker = "builder.response_builder", TaskType = "build_answer" },
                new() { Title = "Verify sourced result", Description = $"Check that the answer addresses the question and notes source limitations: {goal}", AssignedAnt = "verifier", AssignedWorker = "verifier.result_verifier", TaskType = "verification" },
            };

        if (isCodeGoal)
            return new()
            {
                new() { Title = "Research mission", Description = $"Understand the goal and frame the code/project inspection need: {goal}", AssignedAnt = "researcher", AssignedWorker = "researcher.repo_researcher", TaskType = "research" },
                new() { Title = "Inspect workspace files", Description = $"List relevant workspace files and read safe text files if useful: {goal}", AssignedAnt = "file", AssignedWorker = "file.file_scout", TaskType = "file_inspection" },
                new() { Title = "Create structured patch proposal", Description = $"Analyze available code/file context and propose structured patches as JSON only: {goal}", AssignedAnt = "coder", AssignedWorker = AntRegistry.DefaultWorkerFor("coder", "patch_proposal", goal)?.WorkerId, TaskType = "patch_proposal" },
                new() { Title = "Build final response", Description = $"Create a practical answer or implementation plan from the prior findings: {goal}", AssignedAnt = "builder", AssignedWorker = "builder.response_builder", TaskType = "build_answer" },
                new() { Title = "Verify result", Description = $"Check the result for accuracy, usefulness, missing steps, and risk: {goal}", AssignedAnt = "verifier", AssignedWorker = "verifier.result_verifier", TaskType = "verification" },
            };

        // v0.3.8.93 — a SHORT informational goal gets a single-task static plan. The three-task
        // generic fallback below predates proportional planning: for "what is X?" it spent a
        // research call and a verification call to dress up one answer, and the verifier was
        // grading prose, not protecting a change. Nothing consequential is planned here, so the
        // proportionality rule the dynamic planner now follows applies to the static plan too.
        // The bound is deliberately conservative: anything long enough to be a brief runs the
        // full research→build→verify shape exactly as before.
        if (goal.Trim().Length <= SimpleAnswerGoalChars)
            return new()
            {
                new() { Title = "Answer the request", Description = $"Answer directly and practically: {goal}", AssignedAnt = "builder", AssignedWorker = "builder.response_builder", TaskType = "build_answer" },
            };

        return new()
        {
            new() { Title = "Research mission", Description = $"Understand the goal and gather useful context: {goal}", AssignedAnt = "researcher", AssignedWorker = "researcher.mission_researcher", TaskType = "research" },
            new() { Title = "Build response", Description = $"Create a practical answer or action plan for: {goal}", AssignedAnt = "builder", AssignedWorker = "builder.response_builder", TaskType = "build_answer" },
            new() { Title = "Verify result", Description = $"Check the result for accuracy, usefulness, and missing steps: {goal}", AssignedAnt = "verifier", AssignedWorker = "verifier.result_verifier", TaskType = "verification" },
        };
    }

    /// <summary>
    /// The goal length below which the static fallback answers with a single builder task.
    /// A compile-time constant, like <see cref="AnthillRuntime.MinDynamicTasks"/> — proportionality
    /// is a planning rule, not an operator gate.
    /// </summary>
    internal const int SimpleAnswerGoalChars = 280;
}
