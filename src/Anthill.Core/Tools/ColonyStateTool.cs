using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.SDK.Tools;

namespace Anthill.Core.Tools;

/// <summary>
/// WHAT THE COLONY IS RIGHT NOW. v0.3.8.98.
///
/// WHY IT IS NOT `system_info`. That tool answers a question about the MACHINE — OS, framework,
/// working directory, and the handful of feature switches a tool happens to consult. This answers a
/// question about the COLONY: which roles and workers are executable, what each one declares it can
/// do, which tools this run actually registered, what the verification policy is, and what the
/// missions already in memory did. Those are different subjects with different readers, and folding
/// them into one tool would mean every caller of either receives both.
///
/// WHY IT EXISTS AT ALL. v0.3.8.98's audit could read the REPOSITORY and nothing else, so
/// "what is implemented" was answerable and "what is enabled, and what has actually run" was not —
/// and an operator asking "is the colony healthy?" means the second. The specification names
/// `inspect_runtime_state` as a capability precisely to separate them; a capability with no tool
/// behind it would be the declaration-reaching-nobody defect this release exists to remove, so the
/// tool and the worker that dispatches it land together or neither does.
///
/// DETERMINISTIC AND READ-ONLY. It calls no model, changes nothing, and asks the same sources the
/// operator's own `/status` view asks. Repeating it reports the state at that moment — which is
/// exactly why its evidence is recorded as a non-deterministic INSPECTION rather than a verdict:
/// live state is not bound to any tree, and can differ between two honest reads.
///
/// SECRET-FREE BY CONSTRUCTION. It reports names, counts, flags and ids — never configuration
/// values that could carry a credential, and never a mission's content. `RuntimeProfile.Snapshot`
/// is already the operator-visible projection and is used as-is rather than re-derived here.
///
/// AND THE PROFILE IT REPORTS IS THE LIVE ONE, captured at the moment of the call rather than taken
/// from the mission's context. That is not a breach of ADR-001, which requires the MISSION PATH to
/// read the snapshot it captured at intake: "what is enabled right now" and "what was this mission
/// resolved under" are different questions, and an audit answering the first with the second would
/// be describing the past. The payload names it `live_runtime_profile` so no reader can confuse
/// them, and where the two differ that difference is itself worth reporting.
/// </summary>
public sealed class ColonyStateTool : ITool
{
    private readonly SqliteMemory _memory;
    private readonly Func<IReadOnlyList<string>> _registeredTools;

    /// <param name="registeredTools">Read LIVE, as a lambda, because module tools are drained into
    /// the registry after the Queen is built — a list captured at construction would describe a
    /// smaller colony than the one running, which is the ordering trap v3.8.16 documented for the
    /// capability grant and would reproduce here as an audit that under-reports its own reach.</param>
    public ColonyStateTool(SqliteMemory memory, Func<IReadOnlyList<string>> registeredTools)
    {
        _memory = memory;
        _registeredTools = registeredTools;
    }

    public string Name => "colony_state";

    public string Description =>
        "Read-only tool that reports the colony's own current state: executable roles and workers "
      + "with their declared capabilities, the tools this run registered, the verification policy, "
      + "and summary counts of what has already run.";

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        try
        {
            var roster = AntRegistry.Roles.Select(role => new Dictionary<string, object?>
            {
                ["role"] = role.RoleId,
                ["executable"] = AntRegistry.ExecutableRoleIds.Contains(role.RoleId),
                ["workers"] = role.Workers.Select(w => new Dictionary<string, object?>
                {
                    ["worker"] = w.WorkerId,
                    ["enabled"] = w.Enabled,
                    // The DECLARED capabilities, which is what worker resolution reads. An audit
                    // that reports the roster without them describes a colony whose routing
                    // decisions cannot be explained.
                    ["capabilities"] = w.Capabilities,
                }).ToList(),
            }).ToList();

            var state = new Dictionary<string, object?>
            {
                ["colony_version"] = AnthillRuntime.Version,
                ["roster"] = roster,
                ["registered_tools"] = Safely(() => _registeredTools().OrderBy(n => n, StringComparer.Ordinal).ToList()),
                // The LIVE profile, captured here rather than taken from the mission's context —
                // and the key says so. ADR-001 requires the MISSION path to read the snapshot it
                // captured at intake, and that rule is not being bent: this tool answers "what is
                // enabled right now", which is a different question from "what was this mission
                // resolved under", and an audit that reported the mission's own snapshot as the
                // colony's current state would be describing the past. Where the two differ, that
                // difference is itself a finding.
                ["live_runtime_profile"] = Safely(() =>
                    RuntimeProfile.Resolve(RuntimeOptions.Capture(), _registeredTools()).Snapshot()),
                ["events"] = Safely(() => _memory.SummarizeEvents()),
                ["tasks"] = Safely(() => _memory.SummarizeTaskMetrics()),
                ["recent_missions"] = Safely(() => _memory.GetRecentMissions(10)
                    .Select(m => new Dictionary<string, object?>
                    {
                        ["id"] = m.GetValueOrDefault("id"),
                        ["status"] = m.GetValueOrDefault("status"),
                        ["outcome_code"] = m.GetValueOrDefault("outcome_code"),
                    }).ToList()),
            };

            return new ToolResult(Name, true, Json.Dumps(state, indented: true));
        }
        catch (Exception error)
        {
            return new ToolResult(Name, false, "", $"colony_state failed: {error.Message}",
                FailureClass.ToolFailure);
        }
    }

    /// <summary>
    /// One unreadable source is a GAP IN THE REPORT, not a failed inspection.
    ///
    /// An audit that returns nothing because the event summary query threw has told the operator
    /// less than one that returns the roster and says the counts were unavailable — and "the store
    /// could not be read" is itself a finding worth reporting rather than swallowing into a blank
    /// result. Stated as a value in the payload so a reader sees it in place.
    /// </summary>
    private static object? Safely<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception error) { return $"unavailable: {error.Message}"; }
    }
}
