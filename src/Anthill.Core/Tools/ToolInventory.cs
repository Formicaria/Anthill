namespace Anthill.Core.Tools;

/// <summary>
/// v3.4.0 (ADR-006) — the names of the tools that EXIST, and the names contracts reference that do
/// not exist yet.
///
/// Why this needs to be written down. Tool names were strings scattered across three unrelated
/// places: <c>Queen.BuildToolRegistry</c> (what is registered), <c>ToolAuthorization</c> (what each
/// role may dispatch), and the specialist <c>AntExecutionContract</c>s (what each role declares).
/// Nothing compared them, and they had drifted:
///
///   - every specialist contract forbids <c>"shell"</c> and <c>"write_file"</c>. Neither is a tool.
///     The real names are <c>shell_command</c> and <c>write_text_file</c>, so those forbid-lists
///     have never denied anything. They looked like a security boundary and were decoration —
///     harmless only because <see cref="ToolAuthorization.MissionAgentForbidden"/> happened to cover
///     the same tools under their correct names.
///   - five contracts allow tools nobody has built. <c>ToolAuthorization</c> SHORT-CIRCUITS on
///     contract presence: a role with a contract may use its <c>AllowedTools</c> and nothing else.
///     So <c>tester</c>, whose only allowed tool is real, works — while <c>soldier</c>, <c>medic</c>,
///     <c>archivist</c> and <c>scribe</c> are each allowed exactly one tool that does not exist, and
///     are therefore allowed nothing at all.
///
/// That second point is the long-standing "core-ant contracts are blocked on tool-inventory
/// evidence" note in the roadmap. This is the evidence, and it is now executable rather than a
/// recollection.
///
/// Deliberately NOT the registry itself. The registry holds what a given run registered, which
/// depends on config gates — <c>list_directory</c> is absent when file tools are off. This is the
/// build's vocabulary: every name a tool could have, gate or no gate. A contract must be checkable
/// without standing up a runtime.
/// </summary>
public static class ToolInventory
{
    /// <summary>
    /// Every tool this build can register. Config decides which of them a given run actually has;
    /// this is the complete set of names that mean something.
    /// </summary>
    public static readonly IReadOnlySet<string> Implemented = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "system_info",
        "run_allowlisted_check",
        "list_directory",
        "read_text_file",
        "write_text_file",
        "web_search",
        "shell_command",
        "apply_patch",
        // v3.5.0: the scoped workspace tools. Both were declared by a contract and unbuilt, which
        // left ui_cartographer and scribe authorized to dispatch NOTHING — they ran and produced
        // no work, which reads as a weak model rather than as a missing tool.
        "search_workspace",
        "read_changed_files_summary",
        // v3.6.0: structural repository questions, answered from the index without reading files.
        "repository_index",
        // v0.3.8.98: the colony's own state — roster, declared capabilities, registered tools,
        // verification policy, and what has already run. Distinct from `system_info`, which
        // answers about the machine.
        "colony_state",
    };

    /// <summary>
    /// Tool names contracts reference that NOTHING implements. EMPTY as of v3.8.23.
    ///
    /// It held three names from v2.19.0 to v3.8.22 — policy_scan, read_failure_context and
    /// write_memory_candidate — and all three are now gone from the contracts rather than built.
    /// That was the finding, and it is worth keeping because the instinct was the opposite:
    ///
    ///   policy_scan            The capability EXISTS. SoldierAnt calls PolicyScan in process, as a
    ///                          deterministic service. That is the right shape for a verdict no
    ///                          model may influence, and wrapping it in a dispatchable tool would
    ///                          have added a call path without adding a capability.
    ///   read_failure_context   Genuinely absent — but the reach the medic lacks is durable attempt
    ///                          history, which orchestration should ASSEMBLE into a typed artifact
    ///                          rather than hand the medic a tool to go fetch.
    ///   write_memory_candidate REDUNDANT. The archivist already writes candidates as artifacts and
    ///                          IngestMemoryCandidates already consumes them. Building it would have
    ///                          created a second channel writing the same fact.
    ///
    /// Implementing all three would have made this list empty and the inventory green while adding
    /// attack surface and one duplicate write path. Emptying it by deleting the declarations is the
    /// same green for the opposite reason, and the reason is the part that matters.
    ///
    /// The list STAYS, empty, because it is load-bearing: a guard fails when a contract names a tool
    /// that is in neither set, so a new phantom fails the build instead of joining a pile. An empty
    /// set is the strongest form of that guard.
    ///
    /// v3.5.0 moved search_workspace and read_changed_files_summary OUT of here — they were built.
    /// </summary>
    public static readonly IReadOnlySet<string> Planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the name refers to a tool that exists in this build.</summary>
    public static bool Exists(string? toolName) => toolName is not null && Implemented.Contains(toolName);

    /// <summary>
    /// Roles whose contract allows only tools that do not exist yet — so the role is authorized to
    /// dispatch nothing. Computed, never stored: the answer changes the moment a planned tool ships,
    /// and a stored list would keep reporting a role as blocked after it was unblocked.
    /// </summary>
    public static IReadOnlyList<string> RolesBlockedByMissingTools(
        IReadOnlyDictionary<string, Agents.AntExecutionContract> contracts) =>
        contracts
            .Where(kv => kv.Value.AllowedTools.Count > 0 && !kv.Value.AllowedTools.Any(Exists))
            .Select(kv => kv.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
}
