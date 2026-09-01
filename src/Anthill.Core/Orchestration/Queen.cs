using System.Diagnostics;
using Anthill.Core.Agents;
using Anthill.Core.Outcomes;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Events;
using Anthill.Core.Memory;
using Anthill.Core.Models;
using Anthill.Core.Pheromones;
using Anthill.Core.Planning;
using Anthill.Core.Scheduling;
using Anthill.Core.Skills;
using Anthill.Core.Security;
using Anthill.Core.Tools;
using Anthill.SDK.Events;

namespace Anthill.Core.Orchestration;

/// <summary>
/// The Queen is the central coordinator: plan, dispatch, verify, remember, and score.
/// She stays thin enough to orchestrate while the ants and tools carry specialised
/// behaviour and <see cref="TaskScheduler"/> owns all dependency/lifecycle decisions.
/// This partial holds construction and the mission-execution engine; approvals, patch
/// application, and the formatter/view surface live in <c>Queen.Views.cs</c>.
/// </summary>
public sealed partial class Queen : IMissionCoordinator, IDisposable
{
    /// <summary>
    /// v3.8.0 — the worker's proof of life, for as long as this process is alive.
    ///
    /// Registration reports alive ONCE. Without this the worker goes stale within minutes and reads
    /// as crashed while it is sitting there working, which inverts the meaning of the whole
    /// availability rule: a healthy colony would look dead, and a genuinely dead one would look
    /// exactly the same.
    /// </summary>
    private readonly Timer? _workerHeartbeat;

    public void Dispose()
    {
        // Stopped BEFORE the database closes. A timer that fires into a disposed SqliteMemory throws
        // on a background thread, which is an ugly way to end an otherwise clean shutdown.
        _workerHeartbeat?.Dispose();
        // The bus goes down BEFORE the memory too, and for the same reason: its pump is a
        // background thread, and a subscriber woken after the database closed would fault there.
        //
        // Disposed even when adopted from a composition root. The Queen outlives the module host in
        // every arrangement the colony actually has, so "whoever created it disposes it" would mean
        // the bus outliving the database it reports on — which is the fault this line prevents.
        (Events as IDisposable)?.Dispose();
        Memory.Dispose();
    }

    public SqliteMemory Memory { get; }

    /// <summary>
    /// The colony's live event stream. v3.8.3.
    ///
    /// Owned by the Queen because its lifetime is the colony's lifetime, and because she is the one
    /// component guaranteed to exist. Publication does not go through her — <c>SqliteMemory.LogEvent</c>
    /// publishes at the point of record, so every existing emitter is already on the bus without
    /// having been touched. What this property provides is the SUBSCRIBE side: the API's event
    /// stream, and in later phases the modules, attach here.
    /// </summary>
    public IEventBus Events { get; }
    public ModelRouter? Router { get; }
    public ToolRegistry Tools { get; }
    /// <summary>The capability set this Queen was composed from. Missions resolve their context
    /// against it, so a mission cannot be governed by configuration the Queen never saw.
    ///
    /// v3.8.16: re-resolved by <see cref="AdoptModuleTools"/>, because six of the eleven tools now
    /// arrive after construction.</summary>
    public RuntimeProfile Profile { get; private set; }
    private readonly Planner _planner;
    /// <summary>v3.1.0 (ADR-001): planning behind an interface. The Queen decides WHEN a plan is
    /// made and owns the mission it belongs to; it no longer also implements how one is built.</summary>
    private readonly IPlanningService _planning;
    /// <summary>v3.1.0 (ADR-001): what a finished mission teaches the colony — scoring, pheromone
    /// reinforcement, skill credit, route registration — behind an interface. The Queen decides
    /// WHEN learning happens; this owns what gets recorded.</summary>
    private readonly ILearningRecorder _learning;
    /// <summary>v3.1.0 (ADR-001): the operator-facing accounts of a finished mission — raw output,
    /// full trace, and the synthesised answer — behind an interface.</summary>
    private readonly IResultAssembler _results;
    private readonly PheromoneEngine _pheromones = new();
    /// <summary>v3.1.0 (ADR-001): driving the task graph — dispatch, task lifecycle, the
    /// concurrency boundary, mid-run task admission — behind an interface. Internal rather than
    /// private so the admission path itself stays testable: a source guard proving a call site
    /// exists is not the same as proving the gates actually run.</summary>
    internal IExecutionService Execution { get; }
    /// <summary>v3.1.0 (ADR-001): the ONE grader, injected. A pass-through to the canonical
    /// evaluator — the interface exists so the composition root can see there is exactly one.</summary>
    private readonly IMissionEvaluator _evaluator = new CanonicalMissionEvaluator();

    /// <summary>
    /// v2.21.0 Phase C: the skills registry, hydrated from the database rather than constructed
    /// empty. Before this the V2.12 evaluation model had no production instantiation at all — a
    /// skill could earn Certified and nothing anywhere would ever see it.
    /// </summary>
    private SkillRegistry Skills => _skills ??= Memory.LoadSkillRegistry();
    private SkillRegistry? _skills;
    private readonly Dictionary<string, BaseAnt> _ants;
    public string? LastMissionId { get; private set; }

    /// <summary>
    /// v3.1.0 (ADR-001): the Queen's own construction is now composed from an immutable
    /// <see cref="RuntimeProfile"/> rather than read out of mutable statics.
    ///
    /// This is what makes the phase's exit gate reachable. Construction used to read
    /// <c>EnableModelRouting</c>, <c>UseOllama</c>, <c>EnableFileTools</c> and
    /// <c>EnableFileWriting</c> directly, which meant two Queens built at two different instants
    /// could disagree about their own shape — and it is why every gate-touching test had to
    /// serialise itself around the globals. A profile passed in makes the disagreement impossible
    /// and the serialisation unnecessary.
    ///
    /// <paramref name="profile"/> null captures the live runtime, preserving the existing
    /// single-instance behaviour for the CLI and the API host.
    /// </summary>
    public Queen(SqliteMemory? memory = null, RuntimeProfile? profile = null)
    {
        AnthillRuntime.Initialize();
        Memory = memory ?? new SqliteMemory();

        // v3.8.3 — wired FIRST, before any component below is constructed. Several of them log
        // events during construction (tool-registry validation, workspace reconciliation, config
        // health findings), and those are precisely the events an operator watching a cold start
        // wants to see. Wiring the bus after composition would persist them and announce none.
        //
        // Assigned onto Memory whether or not this Queen created it, so a caller-supplied memory is
        // wired identically — which is the reason EventBus is a property rather than a constructor
        // argument on SqliteMemory.
        //
        // v3.8.6 — ADOPTS an already-wired bus instead of replacing it. A composition root has to
        // build the memory and the bus BEFORE the Queen, because modules must be loaded before she
        // is composed (a Queen built first would report model fitness against a colony with no
        // providers). Overwriting the bus here would orphan every subscriber attached during module
        // loading — the module-registration events would be persisted and announced to nobody.
        Events = Memory.EventBus is NullEventBus ? new InProcessEventBus() : Memory.EventBus;
        Memory.EventBus = Events;

        // Captured BEFORE anything is built, so every component below sees one consistent answer.
        var options = (profile ?? RuntimeProfile.Resolve(RuntimeOptions.Capture(), Array.Empty<string>())).Options;
        Router = options.ModelRouting ? new ModelRouter(Memory) : null;
        Tools = BuildToolRegistry(options);
        // The profile is re-resolved against the tools this run actually registered, so its grants
        // describe what was built rather than what the gates implied.
        Profile = RuntimeProfile.Resolve(options, Tools.Names);
        _planner = new Planner(options.UseOllama, Router);
        // The registry factory, not the registry: Skills hydrates lazily from the database and is
        // shared with the credit/promotion paths, so there must remain exactly one instance.
        _planning = new PlanningService(_planner, Memory, Tools, () => Skills);
        _learning = new LearningRecorder(Memory, _pheromones, () => Skills);
        _results = new ResultAssembler(Memory, Router);
        _ants = new Dictionary<string, BaseAnt>
        {
            ["researcher"] = new ResearcherAnt(Memory, Tools, Router),
            ["web"] = new WebResearchAnt(Memory, Tools, Router),
            ["file"] = new FileAnt(Tools),
            ["coder"] = new CoderAnt(options.UseOllama, Router, (Anthill.SDK.Artifacts.IArtifactStore)Memory, Memory),
            ["builder"] = new BuilderAnt(options.UseOllama, Router, (Anthill.SDK.Artifacts.IArtifactStore)Memory),
            ["verifier"] = new VerifierAnt(options.UseOllama, Router, (Anthill.SDK.Artifacts.IEvidenceStore)Memory, (Anthill.SDK.Artifacts.IArtifactStore)Memory),
            // Stage D canary 1: handler registered unconditionally (implemented), but the role only
            // becomes executable/plannable when its rollout gates are open — the catalog and the
            // registry gate agree by construction.
            ["ui_cartographer"] = new UiCartographerAnt(Tools),
            ["tester"] = new TesterAnt(Tools),
            // v3.8.25: the store, so the review reads the PATCH rather than prose about it.
            ["soldier"] = new SoldierAnt((Anthill.SDK.Artifacts.IArtifactStore)Memory),
            ["scribe"] = new ScribeAnt(Tools),
            // Structural repair §1B: the store, so the diagnosis consumes the typed failure_context
            // recorded at the failure boundary instead of re-inferring a class from prose.
            // v0.3.8.101: and the memory, so the diagnosis stamps the failed check task's receipts.
            ["medic"] = new MedicAnt((Anthill.SDK.Artifacts.IArtifactStore)Memory, Memory),
            ["archivist"] = new ArchivistAnt(),
        };
        // Execution framework Stage C: validate the executor catalog at startup. Any problem keeps
        // the affected role unavailable (fail closed) and is loud, never silent.
        foreach (var problem in AntExecutorCatalog.Initialize(_ants.Keys.ToList()))
            Console.Error.WriteLine($"[startup-validation] {problem}");

        // v3.5.0: reconcile recorded workspaces with what is on disk, before anything can be
        // dispatched into one. A row left claiming Active by a process that died would otherwise be
        // handed to an agent as a live workspace, and something would wait forever for the agent
        // that row implies is already working in it.
        // v3.7.0: the conversation runtime gets its production call site here. Without this the
        // escalation gate is unreachable — ConversationScope.Evaluate returns null when nothing has
        // entered a scope, so every gate check would silently pass.
        // v0.3.8.58 — THE CONVERSATION HAS NO MODEL OF ITS OWN, because it is not a lane.
        //
        // Until now the runner took an `ask` delegate resolving the `conversation` route, and that
        // delegate is what made the chat box a place work could happen: it handed a prompt to
        // whatever provider was routed there, inside an access scope standing in the operator's live
        // tree. v0.3.8.57 refused a coding agent for that route, which narrowed WHO could do the work
        // without changing THAT the work was done outside the colony.
        //
        // The delegate is gone. Every operator message is a mission; the mission's own roles reason,
        // and the answer the operator reads is the scribe's, recorded back into the conversation by
        // RecordMissionAnswer. One voice, and it is downstream of verification rather than beside it.
        // v0.3.8.95: the conversation's PROJECT rides along into the mission, so the mission's
        // workspace can be a worktree of the project's own repository.
        Conversations = new Anthill.Core.Conversations.ConversationRunner(
            Memory, (goal, missionProjectId, onCreated, token) =>
                RunMission(goal, onMissionCreated: onCreated, cancel: token, projectId: missionProjectId));

        Scheduler = new Projects.ProjectScheduler(Memory, Conversations);

        Workspaces = new Anthill.Core.Workspaces.MissionWorkspaceManager(Memory, options.AllowedWorkspaceRoot);
        foreach (var note in Workspaces.Recover())
            Console.Error.WriteLine($"[workspace-recovery] {note}");
        // v3.8.26: Tools is passed so the execution path can read the per-task dispatch count and
        // fill in AntMetrics.ToolCalls — a counter that has been zero for every role since it was
        // declared, because it was self-reported and two of twelve ants report anything at all.
        // v0.3.8.51: the approve-and-apply transition rides in so "Skip all approvals" can apply a
        // verified patch without a prompt — through the same audited path the chat card uses.
        // v0.3.8.91: the bypass lane no longer applies a set one proposal at a time. It hands the
        // whole set to `ApplyPatchSetTransactionally`, which preflights every target, journals, and
        // rolls the set back on any failure — so "a patch set applies as a unit or not at all" is
        // true of this path too, and not only of auto-apply.
        Execution = new ExecutionService(Memory, _ants, Tools, Router,
            applyPatchSet: (patchSetId, who) => ApplyPatchSetTransactionally(patchSetId, who));

        // v3.8.0: this process registers as a worker, and startup reconciles what the last one left
        // behind. Both halves are needed for the phase's first gate — "no accepted task is silently
        // lost after crash or restart" — and neither works alone: an attempt with a lapsed lease is
        // only reclaimable if something sweeps for it, and a sweep only means anything if claims
        // carry a worker identity that can stop reporting.
        //
        // The id is derived from the database this colony serves, so a restart keeps one identity
        // while two colonies on one machine cannot appear to be the same worker.
        Anthill.Core.Workers.LocalWorker.Register(Memory,
            id: "local-" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(Memory.DbPath)))[..8].ToLowerInvariant(),
            roles: _ants.Keys.OrderBy(r => r, StringComparer.Ordinal).ToList(),
            // From the RESOLVED options, not the live static. RuntimeCompositionTests refuses any
            // read of a mutable gate in this file, and it is right to: the ceiling a worker
            // advertises must be the one this runtime was composed with, so the registered row and
            // the dispatcher cannot come to disagree when an operator edits the setting mid-run.
            maxConcurrent: options.MaxParallelWorkers);

        // Swallowed on purpose: a heartbeat that throws on a background thread would take the
        // process down over bookkeeping. A missed beat is self-correcting — the next one lands — and
        // if they all stop, that is exactly the signal this mechanism exists to send.
        _workerHeartbeat = new Timer(_ =>
            {
                try { Memory.Heartbeat(Anthill.Core.Workers.LocalWorker.Id, AnthillTime.NowUtc()); }
                catch { }
            },
            null, Anthill.Core.Workers.LocalWorker.HeartbeatEvery, Anthill.Core.Workers.LocalWorker.HeartbeatEvery);

        // Reported, never silent. An attempt abandoned by a dead process is exactly the evidence an
        // operator needs to explain a mission that stopped halfway — and one that MAY have left
        // effects outside the process is not automatically redeliverable, because an attempt that
        // died mid-write may well have completed the write.
        // Our OWN orphans first, and unconditionally. A process that crashed left its attempts
        // Running with most of a thirty-minute lease still on the clock, so the expiry sweep below
        // would find nothing at restart and the task would stay stranded for the rest of a lease
        // held by a process that is demonstrably gone — this one is starting up in its place.
        foreach (var abandoned in Memory.ReclaimOwnAttempts(Anthill.Core.Workers.LocalWorker.Id)
                     .Concat(Memory.ReclaimExpiredAttempts()))
            Console.Error.WriteLine(
                $"[attempt-recovery] task {abandoned.TaskId} (attempt {abandoned.Number}) was abandoned by "
              + $"worker {abandoned.WorkerId}; "
              + (abandoned.SafeToRedeliver
                    ? "read-only, safe to retry."
                    : "it may have left effects outside the process — review before retrying."));
    }

    /// <summary>
    /// v3.4.2 — does each role's route actually do what its contract needs?
    ///
    /// Worth reporting because EVERY mismatch here fails silently at runtime: a model that cannot
    /// call tools is never shown them and answers from priors; one without structured output returns
    /// prose where a schema was expected and parses to an empty result. Neither throws, neither opens
    /// a breaker, and in a transcript both read as a weak model rather than a misconfiguration.
    ///
    /// A warning, never a refusal — the operator's routing is theirs, and refusing to start over a
    /// fail-closed guess would be worse than running with something they can act on.
    ///
    /// v3.8.2 — MUST be called after the capability cache is warm, which is why it is a method
    /// rather than a few lines in the constructor.
    ///
    /// It used to run during construction, before anything had asked Ollama what its models can do.
    /// So it evaluated against the hand-written name table, which does not know gemma4:31b, and
    /// reported tool calling, structured output and reasoning missing on a model that reports all
    /// three. Five roles were named on every restart, wrongly.
    ///
    /// Two consequences, and the second is the reason this got fixed rather than tuned. The console
    /// log and the Tools &amp; Routing panel gave DIFFERENT answers about the same model, because
    /// /tools computes fitness on request when the cache is warm. And an alarm that is wrong on
    /// every restart is one an operator learns to scroll past — which costs nothing until the day it
    /// is right.
    /// </summary>
    public void ReportModelFitness(TextWriter? to = null)
    {
        if (Router is null) return;
        var output = to ?? Console.Error;

        var report = AntModelFitness.CheckAll(Router);

        // v0.3.8.41 — an UNRESOLVED route is reported once, not once per role.
        //
        // "No model is chosen" is a single fact about the colony with a single remedy. Printing it
        // per role produced seven near-identical paragraphs at every boot, each naming capabilities
        // the operator's model was never asked about — which is how a warning becomes wallpaper.
        var unresolved = report.Where(f => f.Unresolved is not null).ToList();
        if (unresolved.Count > 0)
            output.WriteLine(
                $"[model-fitness] {unresolved.Count} role(s) cannot run: {unresolved[0].Unresolved} "
              + $"Affected: {string.Join(", ", unresolved.Select(f => f.RoleId))}");

        foreach (var fitness in report.Where(f => !f.Fit && f.Unresolved is null))
            output.WriteLine(
                $"[model-fitness] role '{fitness.RoleId}' is routed to {fitness.Provider}:{fitness.Model}, "
              + $"which is missing: {string.Join("; ", fitness.Unmet)}");
    }

    /// <summary>
    /// Take the tools a module contributed, and re-state what this colony can do. v3.8.16.
    ///
    /// WHY THIS IS A METHOD AND NOT A LOOP AT THE CALL SITE. Both composition roots used to write
    /// <c>foreach (var tool in Modules.ContributedTools) Queen.Tools.Register(tool);</c>, which was
    /// correct while modules contributed nothing. It stopped being sufficient the moment six of the
    /// eleven tools started arriving this way, because <see cref="Profile"/> is resolved in the
    /// constructor from the registry as it stood THEN — so registering afterwards left
    /// <c>Profile.ToolGrants</c> naming five tools while eleven were dispatchable.
    ///
    /// That is a wrong answer, not a crash: <c>/status</c>, the runtime profile and every mission
    /// context would have described a colony less capable than the one running, and nothing would
    /// have failed. Making the registration and the re-resolve one call means a root cannot do the
    /// first and forget the second — the defect this repository's call-site audit exists to catch,
    /// closed structurally rather than by remembering.
    /// </summary>
    public void AdoptModuleTools(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        foreach (var tool in tools) Tools.Register(tool);
        Profile = RuntimeProfile.Resolve(Profile.Options, Tools.Names);

        // v3.8.25: the capability grant is re-resolved here for exactly the reason the profile is.
        //
        // v3.8.16 found that `Queen.Profile` was resolved at construction while module tools arrive
        // afterwards, so `/status` reported five tool grants for an eleven-tool colony — a wrong
        // answer that failed nothing. The capability grant has the same shape and a worse failure:
        // resolved before the module lands, `read_text_file` is absent, `repo.read` is not granted,
        // and every role requiring it is DENIED at dispatch. Registration and re-resolution stay one
        // call so the two cannot drift apart again.
        Tools.GrantCapabilities(CapabilityGrant.Resolve(
            Tools.Names.ToHashSet(StringComparer.OrdinalIgnoreCase),
            modelAvailable: Profile.Options.ModelRouting,
            webSearchEnabled: Profile.Options.WebSearch));
    }

    private ToolRegistry BuildToolRegistry(RuntimeOptions options)
    {
        var registry = new ToolRegistry(Memory);
        var guard = new WorkspacePathGuard(options.AllowedWorkspaceRoot);
        registry.Register(new SystemInfoTool());
        // Stage D-2: TesterAnt's ONLY execution surface — declared checks, never arbitrary commands.
        registry.Register(new RunAllowlistedCheckTool(options.AllowedWorkspaceRoot));
        if (options.FileTools)
            // v3.5.0: scoped workspace tool. The guard resolves to the MISSION workspace when one is
            // in scope, so it reads the tree the mission is actually changing.
            registry.Register(new SearchWorkspaceTool(guard));
        registry.Register(new ChangedFilesSummaryTool());
        // v3.6.0: repository questions answered from a revision-keyed index rather than by reading
        // the tree into a prompt. Cached per workspace — a tool that rebuilt on every call would
        // walk the repository once per question, which is the cost the index exists to avoid.
        registry.Register(new RepositoryIndexTool(IndexFor));
        // v0.3.8.98 — the colony's own state, for the audit class's runtime half. Registered in the
        // CORE rather than a module: a colony that cannot say what it is has no honest answer to
        // "what is enabled right now", and unlike file reads that is not an optional capability.
        // The tool list is read through a lambda because module tools arrive after this call.
        registry.Register(new ColonyStateTool(Memory, () => registry.Names.ToList()));
        // v0.3.8.106 — cross-mission continuity, registered in the CORE for `colony_state`'s reason:
        // the artifact store is core state, and work building on verified work is not an optional
        // capability. It gates on the producing mission's PERSISTED grade, so registering it
        // unconditionally cannot widen anything — a colony with no verified missions has a tool
        // that refuses every id it is given, which is "registered and refusing" rather than
        // "declared and absent".
        registry.Register(new ReadArtifactTool(Memory));

        // v3.8.16 — list_directory, read_text_file, write_text_file, web_search, shell_command and
        // apply_patch are NOT constructed here any more. They live in Anthill.Modules.Tools, and the
        // composition root drains them into this registry once the Queen exists.
        //
        // Both roots must load that module: Anthill.Api and Anthill.Cli. A colony built without it
        // plans and dispatches exactly as before, and every call to one of those six returns "Tool
        // not found or not registered" — a typed ValidationFailure rather than a crash, which is the
        // right shape for an absent capability and NOT what an operator expects from a default
        // install. ToolInventoryTests checks both sides of that pairing.

        // v3.4.1: operator-defined tools join the SAME registry, last, and by the same Register call
        // every built-in uses. That ordering is deliberate — a definition is validated against
        // ToolInventory and cannot take a built-in's name, so arriving last can never displace one.
        //
        // Registering them here rather than through a parallel path is the entire exit gate: from
        // this line onwards nothing in the harness — projection, authorization, dispatch, failure
        // classification, /tools — knows or asks whether a tool was compiled in or declared.
        UserTools = UserToolRegistrar.Default().RegisterAll(registry, Memory.LoadToolDefinitions());
        foreach (var rejected in UserTools.Where(r => !r.Registered))
            Console.Error.WriteLine(
                $"[user-tools] '{rejected.Name}' not registered: {string.Join("; ", rejected.Problems)}");

        // v3.8.25: resolve what this run can PROVIDE, last, from what actually got registered.
        //
        // This is what makes ToolExecutionContext constructible, and it has to happen here for the
        // same reason RuntimeProfile.Resolve does: module tools arrive after the built-ins, so a
        // grant computed any earlier would describe a colony with fewer capabilities than the one
        // about to run — and a capability check against an understated grant denies real work.
        //
        // Note the module dependency this makes visible. A colony built without Anthill.Modules.Tools
        // has no read_text_file, so it is not granted repo.read, so a role requiring it is refused
        // with that reason rather than discovering "Tool not found" one layer down.
        registry.GrantCapabilities(CapabilityGrant.Resolve(
            registry.Names.ToHashSet(StringComparer.OrdinalIgnoreCase),
            modelAvailable: options.ModelRouting,
            webSearchEnabled: options.WebSearch));

        return registry;
    }

    /// <summary>
    /// The outcome of loading operator-defined tools for THIS run, rejections included. Held so the
    /// API can answer "why is my tool not there" — the one question a rejected definition provokes,
    /// and one that is unanswerable if the rejection only ever reached stderr.
    /// </summary>
    public IReadOnlyList<ToolRegistration> UserTools { get; private set; } = Array.Empty<ToolRegistration>();

    /// <summary>
    /// v3.7.0: the conversational surface. Owned here because escalation starts missions, and the
    /// Queen is what runs a mission — the runner decides WHETHER, this decides what.
    ///
    /// Constructed at composition time rather than per request, so the live cancellation registry it
    /// holds survives between calls. A per-request runner would forget every mission it started the
    /// moment the request ended, which is the one thing it exists to remember.
    /// </summary>
    public Conversations.ConversationRunner Conversations { get; private set; } = null!;

    /// <summary>
    /// v0.3.8.48: the project schedule executor. Constructed with the runtime but NOT started —
    /// the API host starts it, so thousands of test-constructed Queens never spin a live timer.
    /// </summary>
    public Projects.ProjectScheduler Scheduler { get; private set; } = null!;

    /// <summary>
    /// v3.5.0: disposable, attributable workspaces for code missions. Owned by the Queen because
    /// workspace lifecycle is deterministic orchestration — no model participates in deciding where
    /// an agent may write, for the same reason none picks its own tool authorization.
    /// </summary>
    public Anthill.Core.Workspaces.MissionWorkspaceManager Workspaces { get; private set; } = null!;

    /// <summary>
    /// Re-read the stored definitions and re-register them into the live registry.
    ///
    /// Called after an operator adds, edits or revokes a tool, so the change takes effect for the
    /// next mission rather than the next restart. It re-registers the WHOLE set rather than one
    /// definition, because the grant table is replaced wholesale — that is what stops a definition
    /// removed since the last load from staying granted.
    /// </summary>
    public void ReloadUserTools() =>
        UserTools = UserToolRegistrar.Default().RegisterAll(Tools, Memory.LoadToolDefinitions());

    /// <summary>
    /// Prepare a workspace for a mission that may write, and record the outcome on the mission's
    /// own event stream.
    ///
    /// Returns null rather than throwing when preparation fails — which it legitimately does when
    /// the workspace root is not a git checkout. A mission that cannot get an isolated workspace
    /// still runs, under the configured root exactly as it did before v3.5.0; refusing to run at all
    /// would make an isolation improvement into a breaking change for every non-git deployment.
    /// The event says which happened, because "my changes went to the live checkout" must never be
    /// something an operator has to infer.
    /// </summary>
    /// <summary>
    /// Turn a finished mission's workspace changes into a patch set, and checkpoint the workspace.
    ///
    /// CHECKPOINTED, not cleaned. The change set references the workspace it came from, and an
    /// operator reviewing a proposal an hour later frequently wants to look at the tree that
    /// produced it. Reclaiming it the instant the mission ends would destroy the evidence at exactly
    /// the moment it becomes interesting — cleanup stays an explicit decision, and retention still
    /// beats it.
    ///
    /// Never throws into the mission. This runs after the outcome is decided, so a failure here can
    /// cost the operator a change set but must not be able to turn a completed mission into a failed
    /// one — the mission's work happened either way.
    /// </summary>
    private void HarvestWorkspaceChanges(Mission mission, Anthill.Core.Workspaces.MissionWorkspace? workspace)
    {
        if (workspace is null) return;

        try
        {
            // v0.3.8.95 — idempotent per workspace. The acting-coder path captures the workspace
            // diff the moment the coder task completes — while the task graph is still open, so
            // tester/soldier reviews CAN be inserted, which is the whole point of capturing early.
            // Running the same capture again here would file the identical changes as a second
            // patch set. One workspace, one change set; the skip is recorded so "already captured"
            // can never be misread as "found nothing".
            if (Memory.HasPatchSetForWorkspace(mission.Id, workspace.Id))
            {
                Memory.LogEvent(mission.Id, "workspace_already_captured",
                    $"Workspace {workspace.Id} diff was already captured during execution — not harvesting again.",
                    null, "queen", new() { ["workspace_id"] = workspace.Id });
                Workspaces.Checkpoint(workspace.Id);
                return;
            }

            // v0.3.8.93 — anchored to the task whose work produced the changes, resolved the same
            // way the result assembler picks the best output (this runs BEFORE Assemble, so
            // mission.BestOutputTaskId is still null here — which is why the old code recorded
            // every harvested set against task "", an id no approval card or verification fault
            // could ever be traced back from).
            var anchorId = ResultAssembler.SelectBestOutputTaskId(mission);
            var anchor = mission.Tasks.FirstOrDefault(t => t.Id == anchorId);

            var capture = Anthill.Core.Workspaces.WorkspaceChangeSet.Create(
                workspace, mission.Id, anchorId ?? "",
                $"Changes from mission workspace {workspace.Id}");

            // v0.3.8.97 — an unfaithful capture is refused whole, here exactly as in the acting
            // lane: harvesting the representable subset would file a set that silently omits
            // deletions, renames, or oversized work. The workspace stays on disk (Checkpoint below
            // never runs), so the operator can inspect what could not be captured.
            if (!capture.Faithful)
            {
                Memory.LogEvent(mission.Id, "workspace_capture_failed",
                    $"Workspace {workspace.Id} changes could NOT be faithfully captured at "
                  + $"finalization — {capture.Problems.Count} problem(s), so no patch set was "
                  + "harvested: " + string.Join(" | ", capture.Problems.Take(5)),
                    null, "queen",
                    new() { ["workspace_id"] = workspace.Id, ["problems"] = capture.Problems });
                return;
            }

            var changes = capture.Set;
            if (changes.Proposals.Count > 0)
            {
                Memory.LogEvent(mission.Id, "workspace_change_set",
                    $"{changes.Proposals.Count} file(s) proposed from workspace {workspace.Id}", null, "queen",
                    new()
                    {
                        ["workspace_id"] = workspace.Id,
                        ["base_revision"] = workspace.BaseRevision,
                        ["files"] = changes.Proposals.Select(p => p.FilePath).ToList(),
                    });

                // v0.3.8.93 — INTO THE ONE PIPELINE, not past it. This used to be a bare
                // SavePatchSet: the harvested set existed in the store with no verification
                // evidence, no artifact, no approval card and no bypass gate — reviewable in
                // principle, unreachable in practice. It now takes the same path a coder's
                // structured proposal takes (ProcessPatchSet saves it), with one honest
                // divergence: policy review tasks cannot be inserted at finalization, and the
                // pipeline records that instead of hiding it.
                if (anchor is not null)
                {
                    Execution.ProcessHarvestedPatchSet(mission, anchor, changes);
                }
                else
                {
                    // No completed task to anchor to — a mission whose every task failed but whose
                    // workspace still changed. The set is saved so the evidence survives; the
                    // pipeline needs a real task row for attribution and there is none to give it.
                    Memory.SavePatchSet(changes);
                    Memory.LogEvent(mission.Id, "workspace_change_set_unanchored",
                        $"Patch set {changes.Id} from workspace {workspace.Id} was saved without the "
                      + "review pipeline: no completed task exists to attribute it to.", null, "queen",
                        new() { ["patch_set_id"] = changes.Id, ["workspace_id"] = workspace.Id });
                }
            }
            else
            {
                // A real, reportable outcome. A mission that ran and changed nothing is not broken,
                // and silence here would leave an operator unable to tell it apart from a harvest
                // that failed.
                Memory.LogEvent(mission.Id, "workspace_no_changes",
                    $"Workspace {workspace.Id} finished with no file changes", null, "queen");
            }

            Workspaces.Checkpoint(workspace.Id);
        }
        catch (Exception error)
        {
            Memory.LogEvent(mission.Id, "workspace_harvest_failed",
                $"Could not build a change set from workspace {workspace.Id}: {error.Message}", null, "queen");
        }
    }

    private readonly Dictionary<string, Anthill.Core.Workspaces.RepositoryIndex> _indexes = new();

    /// <summary>
    /// The repository index for a workspace, built once and reused.
    ///
    /// Keyed by workspace AND revision, so an index cannot outlive the thing it describes: a
    /// workspace rebased onto a new base revision gets a new index rather than confidently
    /// answering from the old tree. Within one revision the mission's own edits do NOT invalidate it
    /// — RepositoryIndex.FileChanged answers staleness per file, so three edited files do not throw
    /// away what the index knows about twenty thousand others.
    /// </summary>
    private Anthill.Core.Workspaces.RepositoryIndex IndexFor(Anthill.Core.Workspaces.MissionWorkspace workspace)
    {
        var key = $"{workspace.Id}:{workspace.BaseRevision}";
        lock (_indexes)
        {
            if (_indexes.TryGetValue(key, out var cached)) return cached;

            // Stored index first, then refresh against it. On the first mission after a restart this
            // turns a full walk-and-parse into a walk that reuses every unchanged file — which on a
            // large repository is the entire reason the index exists.
            var stored = Memory.LoadRepositoryIndex(workspace.Id, workspace.BaseRevision);
            var built = Anthill.Core.Workspaces.RepositoryIndexBuilder.Build(workspace, stored);

            _indexes[key] = built;
            // Persisted best-effort: an index that fails to save costs the NEXT run some work, and
            // must never cost THIS one its answer.
            try { Memory.SaveRepositoryIndex(built); } catch { }
            return built;
        }
    }

    private Anthill.Core.Workspaces.MissionWorkspace? PrepareWorkspace(string missionId, string? projectSourceRoot = null)
    {
        try
        {
            var workspace = Workspaces.Prepare(missionId, projectSourceRoot);
            if (workspace.Usable)
            {
                Workspaces.Activate(workspace.Id);
                Memory.LogEvent(missionId, "workspace_ready",
                    $"Mission workspace {workspace.Id} prepared from {workspace.BaseRevision}", null, "queen",
                    new()
                    {
                        ["workspace_id"] = workspace.Id,
                        ["base_revision"] = workspace.BaseRevision,
                        ["root"] = workspace.Root,
                        // v0.3.8.95: which repository the worktree was taken from — per-project
                        // when the mission's project has a working directory, configured otherwise.
                        ["source_root"] = workspace.SourceRoot,
                    });
                return workspace;
            }

            Memory.LogEvent(missionId, "workspace_unavailable",
                $"No isolated workspace: {workspace.Note}. File operations use the configured root.",
                null, "queen", new() { ["reason"] = workspace.Note });
            return null;
        }
        catch (Exception error)
        {
            Memory.LogEvent(missionId, "workspace_unavailable",
                $"Workspace preparation failed: {error.Message}", null, "queen");
            return null;
        }
    }

    public string RunMission(string goal) => RunMission(goal, onMissionCreated: null);

    /// <summary>
    /// Runs a mission and reports the new mission's id to <paramref name="onMissionCreated"/> as
    /// soon as the row is persisted. Callers running missions concurrently (Phase 3) must use
    /// this callback instead of <see cref="LastMissionId"/>, which is a last-writer-wins
    /// convenience kept for the single-mission CLI path.
    ///
    /// <paramref name="cancel"/> lets the caller (e.g. the API job runner) stop a mission mid-flight:
    /// it is linked with a hard <see cref="AnthillRuntime.MaxMissionSeconds"/> deadline into a single
    /// token that is (a) published to every model call via <see cref="ModelCallScope"/> so an
    /// in-flight generation aborts promptly and (b) checked between tasks so the scheduler stops
    /// dispatching. Without it a hung/slow model call could pin the single-writer queue for minutes.
    /// </summary>
    public string RunMission(string goal, Action<string>? onMissionCreated, CancellationToken cancel = default,
        Action<MissionOutcome>? onMissionFinished = null, string? projectId = null)
    {
        Console.WriteLine($"Queen received mission: {goal}");
        var missionStartedAt = AnthillTime.NowUtc();

        // v3.1.0 (ADR-001): configuration is captured ONCE, here, and the run's capability set is
        // resolved from it. Everything below reads the snapshot; nothing on the mission path
        // reaches for a mutable static again. Two Queens in one process therefore cannot leak
        // configuration into each other's missions — each captured its own at its own intake.
        var profile = Profile;
        var options = profile.Options;

        // v0.3.8.95 — the project travels with the mission from intake. Whitespace is null: an
        // empty id recorded as "" would satisfy every is-not-null check while naming nothing.
        var mission = new Mission
        {
            Goal = goal,
            Status = MissionStatus.Running,
            ProjectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId!.Trim(),
        };
        LastMissionId = mission.Id;

        // v3.1.0 (ADR-002): the mission's governing facts, resolved once at intake and passed
        // explicitly from here on. Constraints are parsed exactly once; the deadline is an
        // ABSOLUTE instant anchored to the mission's own start, so a resumed run compares against
        // the same wall-clock boundary the original did instead of restarting its clock.
        // Persist the mission row before anything references it: `events`, and as of v0.3.8.104
        // `mission_contracts`, both carry FK constraints on `missions(id)`. This save used to sit
        // twelve lines below, which was fine while events were the only dependant and wrong the
        // moment the contract joined them — the whole suite failed on `FOREIGN KEY constraint
        // failed` from a table that did not exist a day earlier.
        Memory.SaveMission(mission);

        // v0.3.8.104 — the contract is RECORDED here, once, before anything reads it. A resumed or
        // replayed mission loads the one it was admitted under rather than acquiring a new one from
        // whatever intake says today.
        var contract = Missions.MissionContracts.LoadOrCreate(Memory, mission);
        var context = MissionContext.Create(mission, profile, missionStartedAt, contract);

        // One token governs the whole mission: external cancel OR the deadline, whichever comes first.
        using var missionCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        // v2.26.0 pre-V3 hardening: the mission DEADLINE cancels the token. Before this, timeout
        // was only a wall-clock check in the dispatch loop — in-flight model calls ran to their own
        // completion while the mission proceeded to finalization without them. MissionStopReason
        // checks the clock before the token, so a deadline cancellation still reports as timeout.
        // v3.1.0: armed from the context's absolute deadline rather than a fresh duration.
        missionCts.CancelAfter(context.Remaining(AnthillTime.NowUtc()));
        using var modelScope = ModelCallScope.Enter(missionCts.Token);

        onMissionCreated?.Invoke(mission.Id);

        // v3.5.0 — a mission permitted to WRITE gets its own workspace, and every file operation for
        // the rest of this mission is confined to it.
        //
        // Gated on the write capabilities rather than prepared for every mission: a read-only
        // research mission has nothing to isolate, and taking a git worktree for it would cost a
        // directory per question. The scope is entered even when preparation FAILS to produce a
        // usable workspace — in which case CurrentRoot is null and the guard keeps its configured
        // root, which is exactly the pre-v3.5.0 behaviour rather than a silent widening.
        // v0.3.8.95 — acting-coder missions also want one: the routed agent CLI edits files even
        // when ANTHILL's own write gates are off, and an edit with nowhere isolated to land is the
        // exact defect the workspace exists to prevent.
        var wantsWorkspace = AnthillRuntime.EnableFileWriting || AnthillRuntime.EnablePatchApplication
                             || AnthillRuntime.EnableActingCoder;
        // v0.3.8.95 — the workspace is a worktree of the PROJECT's repository when the mission has
        // a project with a working directory. Resolved here, once, from the persisted project row;
        // a project without a path (or a mission without a project) keeps the configured source.
        string? projectSourceRoot = null;
        if (mission.ProjectId is not null)
        {
            try
            {
                var missionProject = Memory.LoadProject(mission.ProjectId);
                if (missionProject is not null && !string.IsNullOrWhiteSpace(missionProject.Path))
                    projectSourceRoot = missionProject.Path;
            }
            catch { /* a project row that cannot be read falls back to the configured source */ }
        }
        var missionWorkspace = wantsWorkspace ? PrepareWorkspace(mission.Id, projectSourceRoot) : null;
        using var workspaceScope = Anthill.Core.Workspaces.MissionWorkspaceScope.Enter(missionWorkspace);
        Memory.LogEvent(mission.Id, "mission_context_resolved",
            "Mission constraints, capability grants, deadline and budgets resolved at intake.",
            metadata: context.Snapshot());

        // v2.26.0 backup policy: a full DB copy before EVERY mission does not scale — a read-only
        // question should not trigger a database-sized write once the colony has history. Backups
        // now run when the last one is older than BackupMinIntervalMinutes (schema migrations and
        // auto-apply runs take their own). Retention and permission hardening unchanged.
        var backupPath = FileSecurity.BackupDbIfDue(AnthillRuntime.DbPath, AnthillRuntime.BackupDir,
            AnthillRuntime.PathFromScript, TimeSpan.FromMinutes(AnthillRuntime.BackupMinIntervalMinutes));
        var (prunedBackups, freedBytes) = FileSecurity.PruneBackups(AnthillRuntime.BackupDir, AnthillRuntime.MaxDbBackups, AnthillRuntime.PathFromScript);
        Memory.LogEvent(mission.Id, backupPath is not null ? "db_backup_created" : "db_backup_skipped",
            backupPath is not null ? "Pre-mission DB backup created."
                : "Pre-mission DB backup skipped (a recent backup already exists, or no database file yet).",
            metadata: new() { ["backup_file"] = backupPath is not null ? Path.GetFileName(backupPath) : null,
                ["backups_pruned"] = prunedBackups, ["bytes_freed"] = freedBytes, ["keep"] = AnthillRuntime.MaxDbBackups });
        Memory.LogEvent(mission.Id, "mission_created", "Mission created.", metadata: new() { ["goal"] = goal });

        // Classify the request. Oversized specification/architecture documents are ingested
        // section-by-section instead of through a single broad analysis task.
        var isSpecIngestion = Planner.IsLongInput(goal);
        var missionType = isSpecIngestion ? "spec_ingestion" : "standard";
        Memory.LogEvent(mission.Id, "mission_classified", $"Mission classified as {missionType}.", metadata: new()
        {
            ["mission_type"] = missionType, ["goal_chars"] = goal.Length,
            ["long_input_threshold"] = AnthillRuntime.LongInputThreshold,
            ["spec_ingestion_enabled"] = AnthillRuntime.EnableSpecIngestion,
        });

        // v3.1.0 (ADR-001): planning is a service. The Queen says WHEN a plan is made and owns
        // everything that happens to it afterwards; it no longer also implements how one is built.
        mission.Tasks = _planning.CreatePlan(context);

        // v0.3.8.104 — PREFLIGHT, BEFORE ANYTHING RUNS.
        //
        // Every gate this program built before now answers "did the mission deliver", after the
        // model calls, the tool dispatches and the operator's wait are already spent. This asks the
        // half that is answerable in advance: is anything in this plan even POSITIONED to deliver.
        // It runs after class coverage has supplied what it supplies, so it refuses only what the
        // runtime cannot repair — the `.98` rule that a plan must not be rejected for a step the
        // runtime knows how to add.
        //
        // A capability blocker is not a failure. Nothing broke and nothing was attempted; the
        // colony cannot do a thing this mission requires, and it will not be able to on a retry.
        // `blocked_missing_capability` says that, where `failed_permanent` would invite the one
        // response guaranteed to be useless.
        var preflight = Planning.MissionPreflight.Check(mission.Tasks, context.Specification);
        if (!preflight.Ok)
        {
            Memory.LogEvent(mission.Id, "mission_preflight_refused", preflight.Explanation,
                metadata: new()
                {
                    ["blockers"] = preflight.Blockers.Select(b => new Dictionary<string, object?>
                    {
                        ["code"] = b.Code, ["subject"] = b.Subject, ["detail"] = b.Detail,
                    }).ToList(),
                    ["capability_blocked"] = preflight.IsCapabilityBlocked,
                });

            mission.Status = MissionStatus.Failed;
            mission.FinalResult = preflight.Explanation;
            mission.UserResult = preflight.Explanation;
            Memory.SaveMission(mission);
            Memory.SaveMissionEvaluation(new Outcomes.MissionEvaluation(
                MissionId: mission.Id,
                OutcomeCode: preflight.IsCapabilityBlocked
                    ? Outcomes.MissionOutcome.BlockedMissingCapability
                    : Outcomes.MissionOutcome.FailedPermanent,
                StructuralStatus: mission.Status.Value(),
                VerificationStatus: Outcomes.MissionEvaluation.Verification.NotRun,
                DeliverableStatus: Outcomes.MissionEvaluation.Deliverable.NotSatisfied,
                StopReason: "preflight_refused",
                EvaluatorVersion: "preflight-v1",
                EvaluatedAt: AnthillTime.NowUtc().ToIso(),
                Explanation: preflight.Explanation));
            // onMissionCreated already fired at admission — a refused plan is still a mission the
            // caller was told about, and firing twice would make one mission look like two.
            return mission.Id;
        }

        foreach (var task in mission.Tasks)
            Memory.LogEvent(mission.Id, "task_created", $"Task created for {task.AssignedAnt}: {task.Title}", task.Id, task.AssignedAnt,
                new() { ["task_type"] = task.TaskType, ["assigned_worker"] = task.AssignedWorker, ["depends_on"] = task.DependsOn, ["parent_task_ids"] = task.ParentTaskIds });

        Memory.LogEvent(mission.Id, "mission_started", "Mission execution started.", metadata: new()
        {
            ["mission_type"] = missionType,
            ["task_count"] = mission.Tasks.Count,
            ["planner_pattern"] = mission.Tasks.Select(t => t.AssignedAnt).ToList(),
            ["worker_path"] = mission.Tasks.Select(t => t.AssignedWorker ?? t.AssignedAnt).ToList(),
            ["task_type_pattern"] = mission.Tasks.Select(t => t.TaskType).ToList(),
            ["parallel_execution"] = options.ParallelExecution,
            ["max_parallel_workers"] = options.MaxParallelWorkers,
            ["auto_dependency_wiring"] = options.AutoDependencyWiring,
            ["correlation_id"] = context.CorrelationId,
            ["deadline"] = context.Deadline.ToIso(),
        });
        Console.WriteLine($"Mission ID: {mission.Id}");
        Console.WriteLine($"Created {mission.Tasks.Count} tasks. Parallel execution: {(options.ParallelExecution ? "ON" : "OFF")}\n");

        // Persist the planned DAG before execution so /graph (and the live colony canvas) can see
        // the mission's tasks while they run — not only after the mission finishes.
        Memory.SaveMission(mission);

        // The executors return WHY they stopped dispatching (mission_timeout / mission_cancelled), or
        // null if the plan ran to its natural end — the authoritative signal for the outcome below.
        // v3.1.0 (ADR-001): the executor returns WHY it stopped dispatching (mission_timeout /
        // mission_cancelled / adaptive_stop), or null if the plan ran to its natural end — the
        // authoritative signal the Queen grades against below.
        var stopReason = Execution.Execute(mission, context, missionCts.Token);

        var evaluation = FinalizeMission(mission, context, stopReason);
        // Structural repair §3: the mission is terminal — its materialized revision (if any) has
        // served every downstream consumer it will ever have. AFTER FinalizeMission, because the
        // evaluation reads the revision-freshness pairing off the tasks; the tree itself is not
        // needed for that, but releasing before evaluation would be the wrong order to teach.
        // Fully qualified: the Queen's own `Workspaces` property (the workspace manager) shadows
        // the namespace of the same name.
        Anthill.Core.Workspaces.MissionRevisionRegistry.ReleaseMission(mission.Id);
        Memory.SaveMission(mission);
        // The evaluation is persisted AFTER the final SaveMission on purpose: SaveMission is an
        // INSERT OR REPLACE, and a row replacement erases columns it does not carry — writing the
        // evaluation first would silently destroy it (the restart test caught exactly that). It is
        // still persisted BEFORE completion is published anywhere: the outcome event, the
        // job callback, and every Director/auto-apply read all come after this line.
        Memory.SaveMissionEvaluation(evaluation);

        // v0.3.8.73 — THE OPERATOR'S RECORD, COMPILED. The first live qualification run found the
        // final report carrying commands, exit codes, durations, test totals, a role census and a
        // `Dispatched` column, none of which came from the colony: `BuilderAnt` writes the answer
        // with a model, so a prompt asking for telemetry got telemetry-shaped prose. There was no
        // reporting code to have a bug in.
        //
        // AFTER SaveMissionEvaluation, because the report reads the persisted outcome — compiling it
        // earlier would report "none persisted" for a mission that had just been graded, which is
        // the ordering defect this file already carries two comments about.
        RecordMissionReport(mission.Id);
        Console.WriteLine("Mission saved to ANTHILL memory.");

        // v3.8.26 — the archivist finally has a trigger.
        //
        // It has NEVER RUN. Not once, in the project's history. The planner contains zero references
        // to it, no handoff targets it, and no policy created one — so the twelfth role has been
        // registered, contracted, handler-complete and gated for releases without a single path that
        // could reach it. v3.8.25 declaring it PostFinalization and enforcing the rule made that
        // visible rather than causing it; the enforcement removed a path that did not exist.
        //
        // This is the only place it can correctly run. The archivist reads a TERMINAL mission to
        // extract lessons, and every line above is what makes the mission terminal: execution has
        // stopped, the status is final, the canonical evaluation is computed AND PERSISTED. Running
        // it a line earlier would hand it a mission whose outcome is not yet decided — which is
        // exactly what a planner-scheduled archivist would have done, and why the contract forbids
        // that.
        RunArchivistAfterFinalization(mission, evaluation);

        // v0.3.8.41 — LEARNING RUNS HERE, AFTER THE ARCHIVIST. This is a real ordering fix.
        //
        // `LearningRecorder.RegisterProceduralRoutes` reads the mission's `memory_candidate` events
        // — the archivist's output — and turns qualifying ones into skill candidates. It ran inside
        // `FinalizeMission`, which is BEFORE the archivist has produced a single candidate, so that
        // query returned an empty list on every mission that has ever run. v2.26.0 moved route
        // registration to finalization to fix a different version of the same bug (it used to run
        // while the mission was still Running and always read a negative outcome); it landed one step
        // short, because the producer had not been given a trigger yet. v3.8.26 gave the archivist
        // its trigger and put it AFTER learning, which completed the loop in the wrong direction.
        //
        // So the order is now: evaluation persisted -> archivist writes candidates -> learning
        // consumes them. Each step's input exists before the step that needs it, which is the whole
        // property, and `FinalizationOrderTests.TheArchivistRunsBeforeLearning` asserts it by
        // position — because position is exactly what the defect was.
        //
        // IDEMPOTENT. Recovery may call finalization again for a mission that already finished, and
        // pheromone strength and skill observations are CUMULATIVE — learning twice does not produce
        // a wrong answer slowly, it produces a wrong answer immediately. The ledger below is checked
        // against the durable event log rather than an in-memory flag, because the process that ran
        // the mission is exactly the process a restart no longer has.
        if (MissionFinalizationLedger.TryClaimLearning(Memory, mission.Id, evaluation))
        {
            _learning.Record(mission, context, evaluation);
            // A NARROW update: SaveMission here would erase the evaluation columns written above.
            Memory.SaveMissionScore(mission.Id, mission.SuccessScore);
            Console.WriteLine($"Pheromone score: {mission.SuccessScore}");
        }

        // v2.7.0 (canonical since v2.26.0): the operator-facing "why it ended" derives from the
        // ONE persisted evaluation — the reason text is presentation; the code is authority.
        var outcome = ComputeOutcome(mission, stopReason) with { OutcomeCode = evaluation.OutcomeCode };
        Memory.LogEvent(mission.Id, "mission_outcome", outcome.Reason,
            metadata: new()
            {
                ["outcome"] = outcome.Outcome, ["reason"] = outcome.Reason,
                ["outcome_code"] = evaluation.OutcomeCode, ["mission_status"] = mission.Status.Value(),
                ["verification_status"] = evaluation.VerificationStatus,
                ["deliverable_status"] = evaluation.DeliverableStatus,
            });
        onMissionFinished?.Invoke(outcome);
        return _results.ComposeCliResult(mission);
    }

    /// <summary>Plain-English mission result the console surfaces on each job. Keyed status + a short reason.</summary>
    public sealed record MissionOutcome(string Outcome, string Reason, string OutcomeCode = "");

    /// <summary>
    /// Derives the operator-facing outcome from the executor's stop reason (authoritative for
    /// cancel/timeout) and the finalized mission/task state (for the completed/partial/failed split).
    /// </summary>
    internal static MissionOutcome ComputeOutcome(Mission mission, string? stopReason)
    {
        var total = mission.Tasks.Count;
        var done = mission.Tasks.Count(t => t.Status == TaskStatus.Complete);
        if (stopReason == Outcomes.MissionStopReasons.Cancelled)
            return new("cancelled", $"Cancelled by operator — {done}/{total} tasks finished before stopping.");
        if (stopReason == Outcomes.MissionStopReasons.Timeout)
            return new("timed_out", $"Timed out — exceeded the {AnthillRuntime.MaxMissionSeconds}s mission budget after {done}/{total} tasks.");

        var taskTimeouts = mission.Tasks.Count(t => t.FailureType == "timeout");
        var timeoutNote = taskTimeouts > 0 ? $" ({taskTimeouts} task{(taskTimeouts == 1 ? "" : "s")} hit the per-task limit)" : "";
        return mission.Status switch
        {
            MissionStatus.Complete => new("completed", $"Completed — {done}/{total} tasks succeeded{timeoutNote}."),
            MissionStatus.Partial => new("partial", $"Partial — {done}/{total} tasks succeeded; some were skipped or failed{timeoutNote}."),
            _ => new("failed",
                (mission.Tasks.FirstOrDefault(t => t.Status == TaskStatus.Failed)?.FailureReason is { Length: > 0 } fr
                    ? $"Failed — {fr}"
                    : $"Failed — a critical task did not succeed{timeoutNote}.")),
        };
    }

    /// <summary>
    /// v1.8.18 Mission Composer plan preview: builds the task plan for a goal exactly as
    /// <see cref="RunMission(string)"/> would (planner → task-type inference → auto-dependency
    /// wiring), but WITHOUT creating, persisting, executing, or logging a mission. Powers
    /// <c>POST /missions/plan</c> so an operator can review the plan (and see the effect of
    /// verification-only / no-patch constraints) before approving dispatch. Read-only: the only
    /// external effect is the planner's model call, exactly as a real dispatch would make.
    /// </summary>
    public MissionPlan PlanPreview(string goal)
    {
        // v3.1.0: the preview resolves a context exactly as a dispatch would, over a transient
        // mission that is never persisted, and then asks the SAME planning service — including the
        // authorization verdict, which the old preview skipped. It returns the plan together with
        // the constraints it was built under, so the endpoint rendering it does not have to
        // reconstruct either. An operator approving a preview is approving the plan that will run.
        var context = MissionContext.Create(new Mission { Goal = goal }, Profile, AnthillTime.NowUtc());
        return new MissionPlan(_planning.CreatePlan(context), context.Constraints, Planner.IsLongInput(goal));
    }

    /// <summary>
    /// Run the archivist as a LIFECYCLE step, after the canonical evaluation is persisted. v3.8.26.
    ///
    /// Not a task, deliberately. A planner task would have to be scheduled before the mission ends
    /// and would therefore read a mission that has not ended; a dynamically inserted task would have
    /// to be admitted into a scheduler that has already stopped. The archivist is the one role whose
    /// input is the FINISHED mission, so it runs outside the task graph entirely — which is what
    /// `SchedulingMode.PostFinalization` has meant since it was declared.
    ///
    /// A synthetic task carries the invocation because the handler's signature takes one and its
    /// contract check reads its TaskType. It is never persisted and never enters `mission.Tasks`:
    /// adding it would change the task graph the evaluation was just computed from, retroactively
    /// altering the record it is summarising.
    ///
    /// Failure here is CONTAINED. The mission has already succeeded or failed on its own terms and
    /// its outcome is already durable; an archivist that throws must not change either. The lesson
    /// is lost and said so, which is the correct trade — memory extraction is valuable and never
    /// authoritative.
    /// </summary>
    private void RunArchivistAfterFinalization(Mission mission, Outcomes.MissionEvaluation evaluation)
    {
        if (!AntExecutorCatalog.RuntimeAvailable("archivist"))
        {
            // Said out loud, like the policy-inserted reviews. "No lessons were extracted" and "the
            // archivist is switched off" are different facts and must not look the same.
            Memory.LogEvent(mission.Id, "archivist_skipped",
                "Archivist did not run: "
                + (AntExecutorCatalog.Snapshot.GetValueOrDefault("archivist")?.UnavailabilityReason ?? "unavailable"),
                metadata: new() { ["role"] = "archivist", ["outcome_code"] = evaluation.OutcomeCode });
            return;
        }

        // v0.3.8.85 (PLAN.md §2 R3) — A STOPPED MISSION IS NOT A LESSON.
        //
        // This dispatch had no cancellation check of any kind, and unlike every other role's it
        // could not have inherited one: it does not go through `ExecutionService.RunSingleTask`, so
        // there is no task row, no scheduler, and nothing for v0.3.8.81's stop check to run against.
        // A mission the operator stopped still reached here, still ran the archivist over whatever
        // partial work existed, and still ingested its memory candidates.
        //
        // R3 names this exact damage — "a cancelled tester leaves a process, a cancelled archivist
        // leaves a MEMORY" — and the memory is the one that outlives the mission and feeds R8. The
        // cancellation harness did not catch it because it asserts on `memory_candidate_archived`
        // events, and a stopped mission usually yields the archivist nothing to propose. The
        // property held by luck rather than by design, which is the weakest way for a property to
        // hold and indistinguishable from holding properly until the day it does not.
        //
        // Read from the PERSISTED canonical evaluation, not from an ambient token: this method's own
        // documentation says the outcome is handed over rather than re-derived precisely so nothing
        // downstream computes its own answer, and `MissionEvaluation` already maps the stop reason to
        // this vocabulary. Checked BEFORE `TryClaimArchivist` so a skipped run does not consume the
        // once-per-evaluation claim.
        // v0.3.8.105 — and a mission WAITING on an operator is stopped in exactly the sense this
        // check means. It did not do the thing it was asked to do; it asked a question and has not
        // been answered. Extracting lessons from the half of the work that ran would teach the
        // colony from a mission whose defining step never happened, and `.85`'s rule — a stopped
        // mission is not a lesson — is the same rule.
        if (evaluation.OutcomeCode is Outcomes.MissionOutcome.Cancelled
                                   or Outcomes.MissionOutcome.TimedOut
                                   or Outcomes.MissionOutcome.WaitingForApproval)
        {
            Memory.LogEvent(mission.Id, "archivist_skipped",
                $"Archivist did not run: the mission was {evaluation.OutcomeCode} and a stopped "
              + "mission's partial work is not a lesson to remember.",
                metadata: new()
                {
                    ["role"] = "archivist",
                    ["outcome_code"] = evaluation.OutcomeCode,
                    ["reason"] = "mission_stopped",
                });
            return;
        }

        if (!_ants.TryGetValue("archivist", out var archivist)) return;

        // v0.3.8.41 — once per evaluation. Memory candidates are the input to skill-candidate
        // registration, and registering the same observation twice is how a promotion threshold that
        // requires repeat evidence across missions gets satisfied by one mission finalised twice.
        if (!MissionFinalizationLedger.TryClaimArchivist(Memory, mission.Id, evaluation)) return;

        try
        {
            var synthetic = new Domain.Task
            {
                Title = "Archive mission lessons",
                // The CANONICAL outcome is handed over rather than re-derived. ArchivistAnt parses
                // an explicit `outcome: x` from its description, and the whole point of a persisted
                // evaluation is that nothing downstream computes its own answer.
                Description = $"Extract durable lessons from the finished mission. outcome: {evaluation.OutcomeCode}",
                AssignedAnt = "archivist",
                TaskType = "mission_summary",
                ParentTaskIds = mission.Tasks.Select(t => t.Id).ToList(),
            };

            var execution = archivist.Execute(synthetic, mission);

            Memory.LogEvent(mission.Id, "archivist_ran",
                $"Archivist ran after finalization: {execution.StatusCode} — {TextUtil.Truncate(execution.Summary, 300)}",
                metadata: new()
                {
                    ["role"] = "archivist", ["status_code"] = execution.StatusCode,
                    ["outcome_code"] = evaluation.OutcomeCode,
                    ["artifact_count"] = execution.Artifacts.Count,
                });

            // The same ingest the task path used, now reachable for the first time: candidates
            // become durable events, never auto-promoted.
            Execution.IngestMemoryCandidatesFor(mission, synthetic, execution);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[archivist] post-finalization run failed for {mission.Id}: {error.Message}");
            Memory.LogEvent(mission.Id, "archivist_failed",
                $"Archivist could not run after finalization: {error.Message}",
                metadata: new() { ["role"] = "archivist", ["outcome_code"] = evaluation.OutcomeCode });
        }
    }

    /// <summary>
    /// Compile the mission's record from persisted state and store it as the `operator_summary`
    /// artifact. v0.3.8.73.
    ///
    /// The artifact is the DURABLE answer to "what actually happened", and it is deliberately
    /// produced here rather than by a role: every role that could produce it is one a model speaks
    /// through. Failing to write it is logged and does not fail the mission — a mission that ran
    /// correctly must not be recorded as failed because its summary could not be filed — but the
    /// absence is visible rather than silent, because "no report" and "an invented report" are the
    /// two states this exists to keep apart.
    /// </summary>
    private void RecordMissionReport(string missionId)
    {
        try
        {
            var report = Outcomes.MissionReport.Compile(Memory, missionId);
            ((Anthill.SDK.Artifacts.IArtifactStore)Memory).Put(Anthill.SDK.Artifacts.Artifact.Create(
                schema: Anthill.SDK.Artifacts.ArtifactSchemas.OperatorSummary,
                producerRole: "queen",
                missionId: missionId,
                payload: Outcomes.MissionReport.Render(report),
                visibility: Anthill.SDK.Artifacts.ArtifactVisibility.Colony,
                provenance: new Anthill.SDK.Artifacts.ArtifactProvenance
                {
                    ColonyVersion = AnthillRuntime.Version,
                    RuntimeNode = "queen",
                    // The one field that matters here: NO model contributed to this payload, and
                    // saying so is what lets a reader tell it apart from the builder's narrative.
                    ModelInvolved = false,
                }));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[mission-report] could not record the operator summary for {missionId}: {error.Message}");
            Memory.LogEvent(missionId, "mission_report_unavailable",
                $"The compiled mission record could not be stored: {error.Message}", antName: "queen");
        }
    }

    private Outcomes.MissionEvaluation FinalizeMission(Mission mission, MissionContext context, string? stopReason)
    {
        // Only a CRITICAL task failure fails the whole mission. A non-critical failure/skip
        // (e.g. one spec-ingestion section) degrades the mission to Partial but never aborts it.
        // v2.26.0 invariant: no task may reach finalization non-terminal. If one does, that is an
        // internal runtime defect — reported as such, and the mission fails CLOSED rather than
        // evaluating half-finished state as if it were finished.
        var nonTerminal = mission.Tasks
            .Where(t => t.Status is TaskStatus.Pending or TaskStatus.Ready or TaskStatus.Blocked or TaskStatus.Running)
            .ToList();
        foreach (var stuck in nonTerminal)
        {
            stuck.Result = $"INTERNAL RUNTIME DEFECT: task was still '{stuck.Status.Value()}' at mission finalization.";
            stuck.CancellationReason = stuck.Result;
            stuck.Status = TaskStatus.Failed;
            stuck.FailureReason = stuck.Result;
            stuck.FailureType = "internal_runtime_defect";
            stuck.FinishedAt = AnthillTime.NowUtc();
            Memory.LogEvent(mission.Id, "internal_runtime_defect", stuck.Result, stuck.Id, stuck.AssignedAnt,
                new() { ["invariant"] = "no_non_terminal_task_at_finalization" });
        }

        var criticalFailed = mission.Tasks.Any(t => t.Status == TaskStatus.Failed && t.Critical);
        var degraded = mission.Tasks.Any(t => t.Status == TaskStatus.Skipped
                                              || (t.Status == TaskStatus.Failed && !t.Critical));
        mission.Status = criticalFailed ? MissionStatus.Failed : degraded ? MissionStatus.Partial : MissionStatus.Complete;

        // v3.1.0 (ADR-001): the three operator-facing accounts of a finished mission — raw best
        // output, full trace, and the plain-English answer — assembled behind one interface.
        //
        // v0.3.8.98 — ASSEMBLED BEFORE THE GRADE, because the answer is now part of what is graded.
        // This ran after the evaluation, which was harmless while the deliverable layer could only
        // ask whether a file changed and impossible once it asks whether the ANSWER addresses what
        // the operator requested: the evaluator read `FinalResult` before anything had written it,
        // and every assessment would have been demoted for producing no answer it had not yet been
        // given the chance to produce. Assembly is a pure function of the terminal tasks — it
        // selects, composes and synthesises, and decides nothing about the outcome — so moving it
        // ahead of the grade adds an input without adding an influence. The workspace harvest below
        // deliberately stays AFTER: a change set is a result of the mission, and producing one must
        // never alter the outcome that produced it.
        _results.Assemble(mission, context);
        Memory.LogEvent(mission.Id, "best_output_selected", $"Best output task selected: {mission.BestOutputTaskId}",
            metadata: new() { ["best_output_task_id"] = mission.BestOutputTaskId });

        // v2.26.0 pre-V3 hardening: the ONE evaluation. Computed exactly once, after every task is
        // terminal, PERSISTED before any learning/credit/completion consumer runs — so restored
        // state answers exactly what live state answered, and no consumer re-derives success.
        // v3.1.0: graded ONCE, through the one injected evaluator. Its inputs are the mission's
        // constraints and verification policy as resolved at intake, so the evaluation is
        // reproducible from the persisted record rather than dependent on what the statics happened
        // to say at the moment finalization ran.
        // v0.3.8.66 (§2 item 2): the evaluator receives the store's evidence rows so promotion
        // can require identity. A read failure passes NULL, which the verification layer treats as
        // fail-closed for any mission holding a materialized revision — §1b S3's direction, applied
        // at the last place a mission becomes completed_verified.
        IReadOnlyList<Anthill.SDK.Artifacts.Evidence>? missionEvidence = null;
        try { missionEvidence = ((Anthill.SDK.Artifacts.IEvidenceStore)Memory).ForMission(mission.Id); }
        catch (Exception evidenceError)
        {
            Console.Error.WriteLine($"[finalize] could not read evidence for {mission.Id}: {evidenceError.Message}");
        }
        // v0.3.8.98 — and the consumption ledger, read the same way and for the same reason: the
        // assessment objective asks whether the verifier read what it graded, and a read failure
        // must reach the evaluator as NULL (fail closed) rather than as an empty list, which would
        // be indistinguishable from "the verifier consumed nothing" and is a different fact.
        IReadOnlyList<Anthill.SDK.Artifacts.ArtifactConsumption>? missionConsumptions = null;
        try { missionConsumptions = ((Anthill.SDK.Artifacts.IArtifactStore)Memory).ConsumptionsForMission(mission.Id, 500); }
        catch (Exception consumptionError)
        {
            Console.Error.WriteLine($"[finalize] could not read consumptions for {mission.Id}: {consumptionError.Message}");
        }

        // v0.3.8.99 — and the mission's artifacts, so citation integrity can resolve what the
        // answer cited against what the mission actually retrieved. Read failure passes NULL, which
        // that layer treats as "nothing to contradict" rather than as a failure — see its remarks
        // for why it is the one gate that does NOT fail closed on an unreadable store.
        IReadOnlyList<Anthill.SDK.Artifacts.Artifact>? missionArtifacts = null;
        try { missionArtifacts = ((Anthill.SDK.Artifacts.IArtifactStore)Memory).ForMission(mission.Id); }
        catch (Exception artifactError)
        {
            Console.Error.WriteLine($"[finalize] could not read artifacts for {mission.Id}: {artifactError.Message}");
        }

        // v0.3.8.105 — and the questions this mission is still waiting on. Read here, beside the
        // other three store reads, and passed to the evaluator so that type stays a pure function
        // of its arguments. An unreadable store passes an EMPTY list rather than null, and the
        // asymmetry with the three reads above is deliberate: those fail CLOSED because a missing
        // record could be hiding a reason to withhold a pass, and this one would fail closed into
        // claiming a mission is waiting on a decision nobody ever asked for — inventing a pause is
        // the worse error, because a paused mission is one the operator is told to go answer.
        IReadOnlyList<string> pendingDecisions = Array.Empty<string>();
        try { pendingDecisions = Memory.PendingOperatorDecisions(mission.Id); }
        catch (Exception decisionError)
        {
            Console.Error.WriteLine(
                $"[finalize] could not read pending operator decisions for {mission.Id}: "
              + $"{decisionError.Message} — grading as though none were outstanding.");
        }

        var evaluation = _evaluator.Evaluate(
            mission, context, stopReason, Memory.CountPatchProposalsForMission(mission.Id), missionEvidence,
            missionConsumptions, missionArtifacts, pendingDecisions);
        // NB: persisted by RunMission AFTER the final SaveMission (INSERT OR REPLACE would erase
        // it here) and before anything publishes completion. In-process consumers below use this
        // same object, so they cannot disagree with what gets persisted.
        Memory.LogEvent(mission.Id, "mission_evaluated", evaluation.Explanation, metadata: new()
        {
            ["outcome_code"] = evaluation.OutcomeCode,
            ["verification_status"] = evaluation.VerificationStatus,
            ["deliverable_status"] = evaluation.DeliverableStatus,
            ["stop_reason"] = evaluation.StopReason,
            ["evaluator_version"] = evaluation.EvaluatorVersion,
        });
        if (evaluation.DeliverableStatus == Outcomes.MissionEvaluation.Deliverable.NotSatisfied)
            Memory.LogEvent(mission.Id, "objective_verification_failed",
                Outcomes.ObjectiveVerification.Explain(mission, context.Constraints,
                    Memory.CountPatchProposalsForMission(mission.Id)),
                metadata: new() { ["goal"] = TextUtil.Truncate(mission.Goal, 300) });

        // v0.3.8.41 — learning is NOT called here any more. It moved to `RunMission`, after the
        // canonical evaluation is persisted and after the archivist has written its memory
        // candidates, because learning is the thing that consumes those candidates. See the comment
        // at the call site; this note stays so the absence reads as a decision rather than a
        // deletion.

        // v3.5.0: whatever the mission changed in its workspace becomes a REVIEWABLE change set.
        //
        // This closes the loop the phase opened. Isolating an agent in a worktree it cannot escape
        // is safe and, on its own, useless — the work has to reach the operator, and the only
        // sanctioned route into the live checkout is the patch/approval pipeline that already
        // exists. So the diff becomes an ordinary PatchSet the Patch Center already reviews.
        //
        // After learning, before completion is published: a change set is a RESULT of the mission,
        // and producing one must never be able to alter the outcome that produced it.
        //
        // Read from the ambient scope rather than passed in. Finalization is a separate method and
        // threading a workspace through its signature would put a parameter on it that only one
        // caller could ever supply — while the scope already means exactly "the workspace this
        // mission is using". Outside a scope it is null and this is a no-op, which is correct for
        // the read-only missions that never get one.
        HarvestWorkspaceChanges(mission, Anthill.Core.Workspaces.MissionWorkspaceScope.Current);
        var eventType = mission.Status == MissionStatus.Complete ? "mission_completed" : mission.Status == MissionStatus.Partial ? "mission_partial" : "mission_failed";
        Memory.LogEvent(mission.Id, eventType, $"Mission finished with status: {mission.Status.Value()}", metadata: new()
        {
            ["success_score"] = mission.SuccessScore, ["task_count"] = mission.Tasks.Count,
            ["failed_tasks"] = mission.Tasks.Where(t => t.Status == TaskStatus.Failed).Select(t => t.Id).ToList(),
            ["skipped_tasks"] = mission.Tasks.Where(t => t.Status == TaskStatus.Skipped).Select(t => t.Id).ToList(),
            ["best_output_task_id"] = mission.BestOutputTaskId,
        });
        return evaluation;
    }


}
