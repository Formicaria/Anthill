using Anthill.Core.Tools;

namespace Anthill.Core.Workspaces;

/// <summary>
/// v3.5.0 — what a given workspace can actually be verified with.
///
/// The manifest is DETECTED, not declared by a mission and not proposed by a model. That direction
/// is the whole exit gate: "verification commands come from the manifest or operator configuration,
/// never model invention." A model may choose WHICH declared check to run; it can never contribute
/// the command, the arguments or the timeout.
///
/// Detection reads the project (is there a .sln, a package.json). Execution reads only
/// <see cref="WorkspaceAdapters"/>, which lives in this repository under review. Keeping those two
/// directions apart is what stops an agent that can edit a repository from editing the thing that
/// checks it.
/// </summary>
public sealed record WorkspaceCapabilityManifest(
    string Root,
    IReadOnlyList<string> ProjectTypes,
    IReadOnlyList<CheckDefinition> Checks,
    IReadOnlyDictionary<string, string> AdapterVersions)
{
    /// <summary>Nothing was recognised — an honest state, not an error.</summary>
    public bool IsEmpty => Checks.Count == 0;

    public static readonly WorkspaceCapabilityManifest None =
        new("", Array.Empty<string>(), Array.Empty<CheckDefinition>(),
            new Dictionary<string, string>());

    /// <summary>
    /// Build the manifest for <paramref name="root"/>.
    ///
    /// A workspace matching several adapters gets all of their checks — a repository with a .NET
    /// backend and a Node frontend genuinely has both, and picking one would silently leave half the
    /// change unverified.
    ///
    /// Ids collide across adapters only if someone writes them that way; the FIRST wins and the
    /// duplicate is dropped rather than overwriting, so adding an adapter can never silently change
    /// what an existing check id runs.
    /// </summary>
    public static WorkspaceCapabilityManifest Detect(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return None;

        var adapters = WorkspaceAdapters.DetectAll(root);
        var checks = new List<CheckDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var adapter in adapters)
            foreach (var check in adapter.Checks)
                if (seen.Add(check.Id)) checks.Add(check);

        return new WorkspaceCapabilityManifest(
            Root: Path.GetFullPath(root),
            ProjectTypes: adapters.Select(a => a.Id).ToList(),
            Checks: checks,
            AdapterVersions: adapters.ToDictionary(a => a.Id, a => a.Version, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The manifest for the mission currently in scope, or <see cref="None"/> outside one.
    ///
    /// Deliberately NOT falling back to the live checkout. Detecting against the operator's working
    /// tree would produce a manifest describing a directory the mission is forbidden to touch, and a
    /// check run there would verify the wrong files while reporting success.
    /// </summary>
    public static WorkspaceCapabilityManifest ForCurrentMission() =>
        Detect(MissionWorkspaceScope.CurrentRoot);

    public CheckDefinition? Find(string? id) =>
        Checks.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
}
