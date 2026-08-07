namespace Anthill.SDK.Tools;

/// <summary>
/// The three facts about a BUILD that <see cref="ToolDefinition.Validate"/> cannot know for itself.
/// v3.8.15.
///
/// WHY THIS EXISTS. A definition is validated at registration, and two of those checks are the
/// load-bearing ones: a definition may not take a built-in's name, and may not take a name the
/// colony structurally forbids to mission agents. Both answers live in <c>Anthill.Core</c> — in
/// <c>ToolInventory.Implemented</c> and <c>ToolAuthorization.MissionAgentForbidden</c> — and both
/// must stay there, because they describe what the CORE registers and what the core refuses. The
/// record moved to the SDK; the tables did not, and should not.
///
/// So the record asks. This is the question it asks, and the core is what answers it.
///
/// WHY IT IS ONE CONTRACT AND NOT THREE. All three are the same kind of fact — "what does this
/// build reserve, refuse, and know how to construct" — answered by the same object at the same
/// moment. Three interfaces would be three things to install, three to forget to install, and
/// three chances for a process to be half-configured.
/// </summary>
public interface IToolDefinitionPolicy
{
    /// <summary>
    /// Tool names this build already implements. A definition may not shadow one: a definition that
    /// could take over <c>apply_patch</c> would turn tool registration into privilege escalation.
    /// </summary>
    IReadOnlySet<string> ReservedToolNames { get; }

    /// <summary>
    /// Names no mission agent may ever dispatch, whatever any allowlist says. A definition claiming
    /// one is refused at the door rather than denied later, so the operator learns immediately
    /// instead of watching a registered tool fail on every call.
    /// </summary>
    IReadOnlySet<string> ForbiddenToolNames { get; }

    /// <summary>
    /// The kinds this build can actually construct — one per registered <see cref="IToolKindExecutor"/>.
    ///
    /// The <see cref="ToolKind"/> enum is deliberately wider than this set. Naming the buildable
    /// ones means a definition asking for a declared-but-unbuilt kind is told "not built yet"
    /// rather than being rejected generically: the difference between "does not exist" and "not
    /// switched on" is the difference between an operator filing a bug and an operator flipping a
    /// setting.
    /// </summary>
    IReadOnlySet<ToolKind> BuildableKinds { get; }
}
