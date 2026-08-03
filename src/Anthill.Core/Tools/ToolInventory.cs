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
    };

    /// <summary>
    /// Tool names the specialist contracts reference that NOTHING implements.
    ///
    /// Listed explicitly rather than tolerated silently, for two reasons. A contract naming a
    /// phantom tool is not a harmless placeholder — it is a role that can dispatch nothing, which
    /// presents at runtime as an ant that runs and produces no work rather than as an error. And
    /// enumerating them here means a NEW phantom fails the build instead of joining the pile.
    ///
    /// Each is a real intended capability, which is why the contracts were written against them:
    ///   policy_scan               — soldier's security/policy review surface
    ///   read_failure_context      — medic's read-only view of a failed task's evidence
    ///   write_memory_candidate    — archivist's only write path, into the memory pipeline
    ///
    /// v3.5.0 moved search_workspace and read_changed_files_summary OUT of this list — they are
    /// built, and both roles that were blocked on them can now dispatch.
    ///
    /// A name moving from here to <see cref="Implemented"/> is the whole point; a guard fails if one
    /// appears in both, because a contract that keeps treating a built tool as planned would keep
    /// its role idle for no visible reason.
    /// </summary>
    public static readonly IReadOnlySet<string> Planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "policy_scan",
        "read_failure_context",
        "write_memory_candidate",
    };

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
