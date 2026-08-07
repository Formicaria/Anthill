namespace Anthill.Core.Tools;

/// <summary>
/// The core's answers to the three questions <see cref="ToolDefinition.Validate"/> asks. v3.8.15.
///
/// Phase 5c step 3 moved the definition record to <c>Anthill.SDK.Tools</c> and left these tables
/// where they belong. Each is read THROUGH on every access rather than copied, for the reason
/// <see cref="ToolRuntime"/> states at greater length: a snapshot of a security table is a table
/// that stops being the one enforced the moment anything edits the original.
///
/// Note what this class deliberately is NOT: a fourth place tool names are written down. It reads
/// <see cref="ToolInventory.Implemented"/> and <see cref="ToolAuthorization.MissionAgentForbidden"/>
/// and adds nothing of its own, because the defect <see cref="ToolInventory"/> exists to prevent is
/// exactly "tool names scattered across three unrelated places that nothing compared".
/// </summary>
public sealed class ToolDefinitionPolicy : IToolDefinitionPolicy
{
    /// <summary>The instance the core installs. Stateless, so one is enough.</summary>
    public static readonly ToolDefinitionPolicy Live = new();

    private ToolDefinitionPolicy() { }

    public IReadOnlySet<string> ReservedToolNames => ToolInventory.Implemented;

    public IReadOnlySet<string> ForbiddenToolNames => ToolAuthorization.MissionAgentForbidden;

    /// <summary>
    /// Derived from the executors <see cref="UserToolRegistrar.Default"/> actually constructs, not
    /// declared beside them.
    ///
    /// Before this it was a hand-maintained <c>ToolKinds.Buildable</c> sitting next to the enum, and
    /// a second kind would have had to be added in two places — with the failure mode being a kind
    /// whose executor exists but which every definition is told is "not built yet", or worse, a kind
    /// declared buildable that the registrar then rejects for having no executor. Deriving it means
    /// the two cannot disagree.
    /// </summary>
    public IReadOnlySet<ToolKind> BuildableKinds => UserToolRegistrar.BuildableKinds;
}
