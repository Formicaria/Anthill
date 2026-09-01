using System.Runtime.InteropServices;
using System.Text.Json;
using Anthill.Core.Agents;   // v3.8.25: AntExecutionCatalog, for the capability-aware dispatch check
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Security;

// v3.8.16 — this file used to be 536 lines: the registry plus seven tool implementations. Six of
// them left for Anthill.Modules.Tools. SystemInfoTool stayed, because it reports the native kernel,
// parallelism and FTS state — core introspection rather than a capability, and moving it would have
// meant an SDK contract whose only consumer is one tool's output dictionary.
//
// `using System.Diagnostics` and `using System.Text` went with the shell and patch tools.

namespace Anthill.Core.Tools;

/// <summary>
/// Tool dispatch + observability. Logs each call/result as events, hardens metadata,
/// and reinforces a per-tool pheromone trail by outcome. Mirrors the Python ToolRegistry,
/// including the success/failure strength deltas.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new();
    private readonly SqliteMemory _memory;

    public ToolRegistry(SqliteMemory memory) => _memory = memory;

    /// <summary>
    /// What this run can provide, resolved once by the composition root after every tool is
    /// registered. v3.8.25.
    ///
    /// Null until set, and that is deliberate rather than an oversight: a registry nobody has told
    /// what the run provides falls back to the ant-name path, which is exactly what every caller
    /// outside <c>Queen</c> — the CLI's direct registries, the API's projections, a hundred tests —
    /// has always used. Making the capability check conditional on being told is what lets this land
    /// without every one of those call sites having to answer a question they have no way to answer.
    /// </summary>
    public IReadOnlySet<string>? GrantedCapabilities { get; private set; }

    /// <summary>
    /// Called by the composition root once the registry is complete. AFTER registration, never
    /// during: the grant is derived from which tools actually arrived, and resolving it early would
    /// describe a colony with fewer capabilities than the one that runs.
    /// </summary>
    public void GrantCapabilities(IReadOnlySet<string> granted) => GrantedCapabilities = granted;

    /// <summary>
    /// Tool dispatches per task, counted where they actually happen. v3.8.26.
    ///
    /// <c>AntMetrics.ToolCalls</c> has existed since the execution framework and has been ZERO for
    /// every role in every mission, because the metric was something each ant was expected to
    /// self-report and two of twelve do — both of them only setting <c>OutputChars</c>. A counter
    /// nobody increments is not a measurement, and Stage F cannot qualify a role on evidence that
    /// does not exist.
    ///
    /// Counted HERE because this is the chokepoint every dispatch already passes through, which is
    /// the same reason authorization and evidence recording live here. Asking twelve handlers to
    /// remember is what produced the zeros.
    ///
    /// Bounded: entries are removed when read. A task that is never persisted leaks one small
    /// counter, and the alternative — an unbounded dictionary keyed by every task the process has
    /// ever seen — is worse.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _dispatchCounts = new();

    /// <summary>
    /// The tool NAMES a task dispatched, beside the count and for the same reason. v0.3.8.94.
    ///
    /// `FailureContext.Tool` and the persisted `TaskResult.Tool` have filtered ant evidence on
    /// kind "tool" since they were written, and no producer existed — both fields were null for
    /// every task of every mission, and an operator reading "Tool: —" on a failure that died
    /// inside a tool call had no way to know the blank was a missing producer rather than a
    /// toolless task. Recorded here because this is the chokepoint every dispatch passes; asking
    /// twelve handlers to remember is what produced the ToolCalls zeros.
    ///
    /// Ordered and DISTINCT — the record answers "which tools", the count answers "how many
    /// dispatches". Bounded exactly like the counts: removed when read.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> _dispatchedTools = new();

    /// <summary>Read and CLEAR the dispatch count for a task. Called once, when the execution
    /// record is persisted.</summary>
    public int TakeDispatchCount(string? taskId) =>
        taskId is not null && _dispatchCounts.TryRemove(taskId, out var n) ? n : 0;

    /// <summary>Read and CLEAR the distinct tool names a task dispatched, in first-use order.
    /// Called beside <see cref="TakeDispatchCount"/>, once, at the same measurement boundary.</summary>
    public IReadOnlyList<string> TakeDispatchedTools(string? taskId) =>
        taskId is not null && _dispatchedTools.TryRemove(taskId, out var names)
            ? names
            : Array.Empty<string>();

    public void Register(ITool tool) => _tools[tool.Name] = tool;

    /// <summary>
    /// v3.4.1: remove a tool from THIS run's registry. Exists for operator-defined tools, which are
    /// the only kind that can stop existing while the process is alive — deleting a definition has
    /// to take the tool out of the registry too, or a model keeps being offered a tool whose
    /// definition is gone and every call fails for a reason the transcript cannot show.
    ///
    /// Built-ins are refused. Registration composes the run's capabilities from config, and a
    /// runtime call able to strip <c>apply_patch</c> out of the registry would be a second, unaudited
    /// way to change what the colony can do.
    /// </summary>
    public bool Unregister(string name)
    {
        if (ToolInventory.Implemented.Contains(name ?? "")) return false;
        return _tools.Remove(name ?? "");
    }

    /// <summary>
    /// The tools actually registered for this run. v3.1.0: <see cref="Configuration.RuntimeProfile"/>
    /// reports the run's tool grants from THIS rather than re-deriving them from the capability
    /// gates — so the profile describes what was built, not what the gates imply should have been.
    /// </summary>
    public IReadOnlyCollection<string> Names => _tools.Keys.ToList();

    /// <summary>
    /// The registered tools themselves, for anything that needs more than their names —
    /// <see cref="ToolSchemaProjection"/> needs each one's description and argument schema to offer
    /// it to a model. Read-only: registration stays the single way a tool enters the registry.
    /// </summary>
    public IReadOnlyCollection<ITool> Tools => _tools.Values.ToList();

    public ToolResult RunTool(string name, string? missionId = null, string? taskId = null, string? antName = null,
        Dictionary<string, object?>? args = null)
    {
        args ??= new();

        // v3.8.26: counted BEFORE authorization, deliberately. A denied dispatch is still a
        // dispatch the role attempted, and a role that repeatedly tries tools it may not call is
        // exactly the behaviour a qualification review needs to see. Counting only successes would
        // make the metric agree with the role about how well it is doing.
        if (taskId is not null)
        {
            _dispatchCounts.AddOrUpdate(taskId, 1, (_, n) => n + 1);
            // The name too, distinct, first-use order — see _dispatchedTools. Same before-
            // authorization timing, same reasoning: a denied dispatch is still an attempt the
            // record should show.
            _dispatchedTools.AddOrUpdate(taskId,
                _ => new List<string> { name },
                (_, names) => { lock (names) { if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name); } return names; });
        }

        if (missionId is not null)
            _memory.LogEvent(missionId, "tool_called", $"Tool called: {name}", taskId, antName,
                new() { ["tool_name"] = name, ["arguments"] = SafeMetadata(args) });

        if (!_tools.TryGetValue(name, out var tool))
        {
            // ValidationFailure, not a defect: the CALL named something that does not exist, and a
            // model can correct that by choosing from the tools it was actually offered.
            var missing = new ToolResult(name, false, "", $"Tool not found or not registered: {name}",
                FailureClass.ValidationFailure);
            if (missionId is not null) LogToolResult(missionId, taskId, antName, missing);
            return missing;
        }

        // Execution framework Stage B: enforce the caller's declared boundary BEFORE the tool runs.
        // A denial is a structured failure with an audit event and zero side effects; spoofing an
        // unknown ant name is refused outright.
        // v3.8.25 — ToolExecutionContext gets its first production call site.
        //
        // It has been in the tree since the execution framework was written, capability-aware and
        // tested, and NOTHING built one: GrantedCapabilities had no source, so there was no way to
        // construct a context truthfully. CapabilityGrant is that source, resolved by the Queen from
        // what it actually composed.
        //
        // The full context is used whenever the run has told this registry what it provides, and the
        // ant-name path remains for every registry that has not been told — the CLI, the API's
        // projections, tests. Both paths re-check the same structural prohibitions; the context path
        // additionally verifies that the role's REQUIRED capabilities are ones this colony can
        // actually supply, which is the half that has never run.
        // LAYERED, not substituted. The first draft of this replaced the name-based call with the
        // context one and would have broken operator-defined tools: Evaluate(antName, …) consults
        // UserToolGrants BEFORE the built-in tables, because a tool that did not exist at compile
        // time is absent from every closed list — and the context overload has no such step, so
        // every user-defined tool would have been denied as "not allowlisted for role". Running the
        // established check first and the capability check second adds the missing gate without
        // moving any existing one.
        var decision = ToolAuthorization.Evaluate(antName, name);

        // Operator-defined tools are excluded from the context layer, and this is the second half of
        // the same lesson. Their whole design is that a definition WIDENS what a role may call
        // beyond the compiled allowlist — so evaluating one against `contract.AllowedTools` would
        // deny every user tool that the name path had just correctly permitted. The structural
        // prohibitions still applied to them above: a definition can never name apply_patch, and can
        // never claim a tool its role's contract forbids.
        var isUserDefined = UserToolGrants.TryGet(name, out _);

        if (decision.Allowed
            && !isUserDefined
            && GrantedCapabilities is { } granted
            && !string.IsNullOrWhiteSpace(antName)
            && AntExecutionCatalog.ContractFor(antName.Trim()) is { } contract)
        {
            decision = ToolAuthorization.Evaluate(
                new ToolExecutionContext(
                    MissionId: missionId ?? "",
                    TaskId: taskId ?? "",
                    RoleId: antName.Trim(),
                    // The worker that actually ran it. Today a role dispatches under its own name;
                    // when workers become distinct identities this is the field that carries them,
                    // and recording the role twice is more honest than inventing an id.
                    WorkerId: antName.Trim(),
                    GrantedCapabilities: granted,
                    AllowedTools: contract.AllowedTools,
                    ForbiddenTools: contract.ForbiddenTools),
                name);
        }

        if (!decision.Allowed)
        {
            // The class carries the denial now; the `authorization_denied:` prefix stays only as
            // human-readable text. Nothing may recover the status by matching that prefix — the
            // typed field is the one callers branch on.
            var denied = new ToolResult(name, false, "", $"authorization_denied: {decision.Reason}",
                FailureClass.AuthorizationFailure);
            if (missionId is not null)
                _memory.LogEvent(missionId, "tool_denied", $"Tool DENIED: {name}", taskId, antName,
                    new() { ["tool_name"] = name, ["ant_name"] = antName, ["reason"] = decision.Reason });
            return denied;
        }

        // v3.7.0 — the escalation gate, at the SAME chokepoint as authorization.
        //
        // Deliberately after ToolAuthorization and before execution. Authorization asks "may this
        // ROLE ever do this"; escalation asks "has the OPERATOR agreed to this happening now". They
        // are different questions with different answers, and a tool must pass both — but there is
        // no point asking the operator about something the role could never do anyway.
        //
        // Outside a conversation this returns null and nothing changes, which is why missions run
        // exactly as they did.
        // v0.3.8.104 — THE AUTHORITY CEILING, AT THE CHOKEPOINT. The `.103` divergence, closed.
        //
        // `.103` built `MissionAuthorityGate`, proved it directly, and consulted it nowhere. The
        // release recorded that honestly: nothing routed a mission to a tool its ceiling forbade,
        // but that was a property of the PLANNER rather than a gate, and a guarantee that depends
        // on nobody making a mistake upstream is not a guarantee. It is read here.
        //
        // THREE QUESTIONS, THREE ANSWERS, ALL REQUIRED. Authorization above asks "may this ROLE
        // ever do this". This asks "is this the KIND of mission that may do this at all" — settled
        // once at intake, from the operator's own request. Escalation below asks "has the OPERATOR
        // agreed to it happening now". None substitutes for another: an audit mission that reached
        // an execute tool is not made acceptable by a human clicking approve, because what the
        // human approved was an audit.
        //
        // Read from the mission's recorded CONTRACT, so a mission is held to the ceiling it was
        // admitted under rather than to whatever today's intake rules would give it — the whole
        // point of `.104`. No mission id means no mission-scoped ceiling to apply, which is the
        // case for tooling and the CLI and leaves them exactly as they were.
        // ONLY FOR A RECOGNIZED CLASS, and this condition is load-bearing rather than cautious.
        // `MissionSpecification.General` defaults its authority to Observe — which means "intake
        // did not classify this", NOT "this mission is read-only". Applying that as a ceiling would
        // refuse `apply_patch` on every coding mission in the colony, because the coding lane is
        // unclassified by design. A ceiling is only a fact where intake actually decided one.
        if (missionId is not null)
        {
            var missionContract = _memory.LoadMissionContract(missionId);
            var ceiling = missionContract is not null
                       && Missions.MissionContracts.RecognizedClasses.Contains(
                              missionContract.Specification.MissionClass)
                ? missionContract.Specification.Authority
                : (Missions.MissionAuthority?)null;
            if (ceiling is not null)
            {
                var authority = Missions.MissionAuthorityGate.Evaluate(ceiling.Value, name);

                if (!authority.Allowed)
                {
                    var refusedByCeiling = new ToolResult(name, false, "",
                        $"authority_ceiling: {authority.Reason}", FailureClass.AuthorizationFailure);
                    _memory.LogEvent(missionId, "authority_ceiling_refused",
                        $"Tool REFUSED by the mission's authority ceiling: {name}", taskId, antName,
                        new() { ["tool_name"] = name, ["ceiling"] = ceiling.Value.ToString(),
                                ["reason"] = authority.Reason });
                    return refusedByCeiling;
                }
            }
        }

        var escalation = Conversations.ConversationScope.Evaluate(name);
        if (escalation is { Allowed: false })
        {
            var refused = new ToolResult(name, false, "",
                $"escalation_refused: {escalation.Reason}", FailureClass.AuthorizationFailure);
            if (missionId is not null)
            {
                _memory.LogEvent(missionId, "escalation_refused",
                    $"Tool REFUSED pending operator decision: {name}", taskId, antName,
                    new() { ["tool_name"] = name, ["decision_id"] = escalation.Id,
                            ["policy"] = escalation.Policy.ToString(), ["reason"] = escalation.Reason,
                            ["awaiting_decision"] = escalation.AwaitingDecision });

                // v0.3.8.105 — A REFUSAL NOBODY MADE IS A QUESTION, AND A QUESTION NEEDS SOMETHING
                // TO ANSWER. PLAN.md §2b `.105`.
                //
                // Until now this branch produced a refusal and stopped. Under `Ask`, absence of an
                // answer is not consent — that rule is right and stays exactly as it is — but the
                // refusal was the WHOLE response: nothing anywhere recorded that an operator had
                // been left with a decision to make, so the mission failed and the operator was
                // never shown the thing they were failing for want of.
                //
                // `EscalationDecision.AwaitingDecision` separates the two cases the old code
                // collapsed: a REJECTION is an answer and the mission is finished with it; an
                // ABSENT decision is a question. Only the second raises a request.
                //
                // THE FILING ITSELF LIVES IN `OperatorDecisions.Request` — see it for which ledger
                // and why. Shared rather than written twice, because the MISSION lane discovers the
                // same absence somewhere else entirely: a mission does not run inside the ambient
                // `ConversationScope`, so this branch is unreachable from one, and a pause wired
                // only here would have been invisible to every mission the colony has ever run.
                // That is `.102`'s finding, and it is the trap this release walked up to.
                if (escalation.AwaitingDecision)
                    Conversations.OperatorDecisions.Request(_memory, missionId, name, antName ?? "queen");
            }
            return refused;
        }

        ToolResult result;
        try
        {
            result = tool.Run(args);
        }
        catch (Exception error)
        {
            result = new ToolResult(name, false, "", $"Tool execution failed: {error.Message}",
                ClassifyThrown(error));
        }

        if (missionId is not null)
        {
            LogToolResult(missionId, taskId, antName, result);
            _memory.UpdatePheromoneTrail($"tool:{name}", "tool", result.Success, result.Success ? 0.02 : -0.04,
                new() { ["mission_id"] = missionId, ["task_id"] = taskId, ["ant_name"] = antName });
            RecordEvidence(name, result, missionId, taskId, args);
            RecordCrossMissionRead(name, result, missionId, taskId, antName, args);
        }
        return result;
    }

    /// <summary>
    /// NOTE THAT THIS MISSION READ ANOTHER MISSION'S ARTIFACT. v0.3.8.106.
    ///
    /// HERE RATHER THAN IN THE TOOL, and the reason is identity. `ReadArtifactTool` would have to
    /// take the consuming mission from an argument, which means taking it from the MODEL — and a
    /// model that can name the mission it read on behalf of can attribute its reads to a mission
    /// that never made them, which is the one thing a lineage ledger must not permit. This frame
    /// has the mission, the task and the role as facts, the same reason evidence and pheromone
    /// reinforcement are recorded here.
    ///
    /// Only on SUCCESS: a refused read consumed nothing, and a ledger row for it would say the
    /// second mission built on a record it was never shown.
    ///
    /// Never throws — a ledger entry is a diagnostic, and the read has already returned.
    /// </summary>
    private void RecordCrossMissionRead(string name, ToolResult result, string missionId,
        string? taskId, string? antName, IReadOnlyDictionary<string, object?> args)
    {
        if (!string.Equals(name, ReadArtifactTool.ToolName, StringComparison.Ordinal)) return;
        if (!result.Success) return;

        try
        {
            var artifactId = (args.GetValueOrDefault("artifact_id")?.ToString() ?? "").Trim();
            if (artifactId.Length == 0) return;

            var store = (Anthill.SDK.Artifacts.IArtifactStore)_memory;
            var artifact = store.Get(artifactId);
            if (artifact is null) return;

            store.RecordConsumption(new Anthill.SDK.Artifacts.ArtifactConsumption
            {
                ArtifactId = artifact.Id,
                ContentHash = artifact.ContentHash,
                Schema = artifact.Schema,
                // The PRODUCING mission, which is what this column has always held.
                MissionId = artifact.MissionId,
                // And the one that read it — the distinction `.106` had to add, because until now
                // the only consumer read within a single mission and the two were the same value.
                ConsumerMissionId = missionId,
                ConsumerRole = antName ?? "unknown",
                ConsumerTaskId = taskId,
            });
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                $"[tools] could not record {missionId} reading artifact via {name}: {error.Message}");
        }
    }

    /// <summary>
    /// Record a deterministic tool outcome as ADR-004 evidence. v3.8.20.
    ///
    /// This is the dispatch chokepoint — it already knows the mission, the task, the ant and the
    /// result, which is why the event log and the pheromone reinforcement live here too. Evidence
    /// belongs beside them for the same reason.
    ///
    /// Only <see cref="ToolEvidence"/>'s short closed list produces anything; everything else
    /// returns null and nothing is written. And like the pheromone write above, a failure here must
    /// never fail the tool call: the result has already been produced and returned to the caller, so
    /// losing the record is strictly smaller than losing the work.
    /// </summary>
    private void RecordEvidence(string toolName, ToolResult result, string missionId, string? taskId,
        IReadOnlyDictionary<string, object?>? args = null)
    {
        // v0.3.8.98 — EITHER lane: a reproducible verdict, or a read-only observation. Which one
        // this is remains ToolEvidence's decision, and an observation is stamped non-deterministic
        // there, so nothing that reads this store for promotion can mistake one for the other.
        if (!ToolEvidence.Records(toolName)) return;

        try
        {
            // v0.3.8.97 — A FAILURE KEEPS ITS OUTPUT. This recorded `result.Output` on success and
            // `result.Error` on failure — and Error for a check is the one-line classification
            // ("check 'dotnet_test' exited 1") while Output holds the run's actual text on BOTH
            // branches. So the evidence carried the full transcript exactly when nobody needed it
            // and threw it away exactly when it was the diagnosis: three live revision-check
            // failures left 28 readable characters each. A failed check now records its headline
            // AND the tail of its output, where a build's or test run's verdict lines live.
            var detail = result.Success
                ? result.Output
                : (result.Error ?? "") + "\n" + Tail(result.Output ?? "", 1800);

            // v0.3.8.101 — THE RECEIPT NAMES ITS CHECK. A command_check row whose detail carries
            // only the run's output is a receipt for "something" — a passing run's transcript need
            // not mention which check produced it, and a diagnosis cannot rest on a receipt that
            // cannot be told from its siblings. The identity comes from the DISPATCH's own
            // arguments at this chokepoint, never parsed back out of output text.
            var checkId = args?.GetValueOrDefault("check_id")?.ToString();
            if (!string.IsNullOrWhiteSpace(checkId))
                detail = $"check '{checkId}': {detail}";
            var evidence = ToolEvidence.For(toolName, result.Success, missionId, taskId, detail);
            if (evidence is not null) ((Anthill.SDK.Artifacts.IEvidenceStore)_memory).Put(evidence);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Could not record tool evidence for {toolName}: {error.Message}");
        }
    }

    /// <summary>The last <paramref name="chars"/> of <paramref name="text"/> — the end is where a
    /// failed run's verdict lives.</summary>
    private static string Tail(string text, int chars) =>
        text.Length <= chars ? text : "…" + text[^chars..];

    /// <summary>
    /// Classify an exception that escaped a tool.
    ///
    /// v3.8.16 — the logic moved to <see cref="ToolFailure.Classify"/> in the SDK, because the tool
    /// implementations that call it left for <c>Anthill.Modules.Tools</c> and a module cannot see
    /// this class. This stays as a delegating alias: eleven in-core call sites across
    /// <c>CheckRunner</c>, <c>WorkspaceTools</c>, <c>HttpToolKind</c> and the tools that remain, plus
    /// <c>ToolFailureClassTests</c>, all name it, and there is nothing to gain from rewriting them
    /// to say the same thing differently.
    /// </summary>
    internal static FailureClass ClassifyThrown(Exception error) => ToolFailure.Classify(error);

    private void LogToolResult(string missionId, string? taskId, string? antName, ToolResult result) =>
        _memory.LogEvent(missionId, result.Success ? "tool_completed" : "tool_failed",
            $"Tool {(result.Success ? "completed" : "failed")}: {result.ToolName}", taskId, antName,
            new()
            {
                ["tool_name"] = result.ToolName, ["success"] = result.Success, ["error"] = result.Error,
                ["output_preview"] = TextUtil.Truncate(result.Output, 500),
                // The class is on the EVENT too, so "which tools fail, and how" is a query rather
                // than an exercise in grepping error prose out of a metadata blob.
                // v3.8.32: wire form via the shared converter — see FailureClassNames.
                ["failure_class"] = FailureClassNames.Wire(result.Failure), ["retryable"] = result.Retryable,
            });

    private static Dictionary<string, object?> SafeMetadata(IReadOnlyDictionary<string, object?> metadata)
    {
        var safe = new Dictionary<string, object?>();
        foreach (var (key, value) in metadata)
            safe[key] = value is string or int or long or double or bool or null ? value : value.ToString();
        return safe;
    }

    public string DescribeTools() => _tools.Count == 0
        ? "No tools registered."
        : string.Join("\n", _tools.Select(kv => $"- {kv.Key}: {kv.Value.Description}"));
}

public sealed class SystemInfoTool : ITool
{
    // v3.8.11 — the runtime gates arrive through an interface, read LIVE on every call. This is
    // the colony's SECOND gate: RuntimeOptions already decided whether to register this tool,
    // and this re-check is what stops one that somehow reached the registry from acting.
    // Capturing the values would quietly collapse the two into one.
    private readonly IToolRuntimeOptions _options;

    public SystemInfoTool(IToolRuntimeOptions? options = null) => _options = options ?? ToolRuntime.Live;
    public string Name => "system_info";
    public string Description => "Read-only tool that returns basic OS, runtime, and workspace information.";

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        var info = new Dictionary<string, object?>
        {
            ["os"] = RuntimeInformation.OSDescription,
            ["os_architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["machine"] = Environment.MachineName,
            ["current_working_directory"] = Directory.GetCurrentDirectory(),
            ["script_directory"] = _options.ScriptDirectory,
            ["allowed_workspace_root"] = new WorkspacePathGuard().Root,
            ["file_tools_enabled"] = _options.FileToolsEnabled,
            ["shell_tool_enabled"] = _options.ShellToolEnabled,
            ["patch_application_enabled"] = _options.PatchApplicationEnabled,
            ["file_writing_enabled"] = _options.FileWritingEnabled,
            ["parallel_execution_enabled"] = AnthillRuntime.EnableParallelExecution,
            ["max_parallel_workers"] = AnthillRuntime.MaxParallelWorkers,
            ["fts_memory_enabled"] = AnthillRuntime.EnableFtsMemory,
            ["native_kernel"] = Native.NativeKernel.UsingNative ? "active" : "managed-fallback",
        };
        return new ToolResult(Name, true, Json.Dumps(info, indented: true));
    }
}

