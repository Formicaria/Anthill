using Anthill.Core.Common;

namespace Anthill.Core.Projects;

/// <summary>
/// v0.3.8.47 — a project: the long-lived container a conversation lives in.
///
/// Named by the maturation directive and shaped by the operator's clarification: "a 'Project' or
/// Workspace should not be created with every chat message, but rather every conversation, and a
/// conversation will be directly linked to the workspace/mission." One project per conversation,
/// created automatically when the conversation starts — or by hand from the Projects tab, with a
/// name, a markdown statement of purpose, and an optional working-directory path.
///
/// The description is not decoration: it travels into the conversation prompt as standing
/// context, the same way Claude's project instructions do. The path is recorded and shown to the
/// colony as the project's working directory; deeper wiring (workspace-root override for the
/// project's missions) is future work and is NOT claimed by any surface.
/// </summary>
public sealed record Project
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";

    /// <summary>Markdown. What this project is FOR — injected into its conversations' prompts.</summary>
    public string DescriptionMd { get; init; } = "";

    /// <summary>Optional working directory the operator has pointed ANTHILL at. Null = none given.</summary>
    public string? Path { get; init; }

    public bool Archived { get; init; }
    public DateTime CreatedAt { get; init; } = AnthillTime.NowUtc();
    public DateTime UpdatedAt { get; init; } = AnthillTime.NowUtc();
}
