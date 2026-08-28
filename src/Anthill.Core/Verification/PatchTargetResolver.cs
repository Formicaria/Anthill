using Anthill.Core.Configuration;
using Anthill.Core.Memory;

namespace Anthill.Core.Verification;

/// <summary>
/// Where a patch set's bytes are meant to LAND. v0.3.8.97.
///
/// <paramref name="Ok"/> false means the target could not be established and NOTHING may be applied,
/// fingerprint-compared, or journal-checked against a guessed tree. <paramref name="Root"/> is the
/// absolute path of the target checkout when Ok. <paramref name="IsLiveTree"/> says whether that
/// target is the configured live root — the pre-project behaviour — so callers that log can name
/// which kind of tree they wrote to.
/// </summary>
public sealed record PatchTarget(bool Ok, string Root, bool IsLiveTree, string? WorkspaceId, string? Problem)
{
    public static PatchTarget Live() => new(true,
        LiveRoot(), true, null, null);

    public static PatchTarget Unresolvable(string? workspaceId, string problem) =>
        new(false, "", false, workspaceId, problem);

    /// <summary>
    /// The configured live root, resolved EXACTLY as <c>WorkspacePathGuard</c>'s constructor
    /// resolves it — a relative configured root anchors at <c>AnthillRuntime.ScriptDir</c>, not at
    /// whatever the process's current directory happens to be. Two resolutions of one setting
    /// would make the resolver and the guard disagree about where "the live tree" is.
    /// </summary>
    internal static string LiveRoot()
    {
        var raw = AnthillRuntime.AllowedWorkspaceRoot;
        return Path.IsPathRooted(raw)
            ? SafeFull(raw)
            : SafeFull(Path.Combine(AnthillRuntime.ScriptDir, raw));
    }

    internal static string SafeFull(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }
}

/// <summary>
/// THE ONE ANSWER TO "WHICH TREE IS THIS PATCH SET FOR". v0.3.8.97.
///
/// THE DEFECT THIS CLOSES. Since v0.3.8.95 a mission can select a PROJECT: its worktree derives from
/// the project's checkout, its verification materializes against that checkout, and its patch set
/// records the workspace it was diffed from (<c>PatchSet.WorkspaceId</c>). But every promotion-time
/// consumer — the gate's freshness compare and rollback-marker check, the set applier's preflight and
/// transaction, the intent hashes — consulted <c>AnthillRuntime.AllowedWorkspaceRoot</c>, the
/// configured LIVE root. So a project set was fingerprint-compared against the wrong tree, preflighted
/// against the wrong tree, and on approval written INTO the wrong tree: the operator selected
/// repository B and the apply landed in repository A. The identity survived exactly as far as
/// verification and was dropped at the apply boundary — the most consequential step in the system.
///
/// HOW IT RESOLVES, and each arm is deliberate:
///
///   - No set id, or a set with NO recorded workspace: the LIVE root. Model-emitted proposals and
///     every set from before v0.3.8.95 carry no workspace, were produced against the live tree, and
///     refusing them all would turn a schema addition into a retroactive freeze — the same
///     non-retroactive rule the fingerprint and evidence checks already follow.
///   - A recorded workspace whose row exists and whose SourceRoot is a present directory: THAT root.
///     The workspace row is the persisted identity chain's last link — project → mission workspace →
///     PatchSet.WorkspaceId → here.
///   - A recorded workspace the store cannot produce, or one with no SourceRoot, or a SourceRoot
///     that is gone: FAIL CLOSED. The set says where it belongs and that place cannot be
///     established, so "apply it to the live root instead" would be precisely the silent
///     wrong-tree write this resolver exists to end.
/// </summary>
public static class PatchTargetResolver
{
    public static PatchTarget Resolve(SqliteMemory memory, string? patchSetId)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (string.IsNullOrWhiteSpace(patchSetId)) return PatchTarget.Live();

        Dictionary<string, object?>? row;
        try { row = memory.GetPatchSetRow(patchSetId); }
        catch (Exception error)
        {
            return PatchTarget.Unresolvable(null,
                $"the patch set row for {patchSetId} could not be read ({error.Message}), so which "
              + "tree it targets is unknown — refused by the target resolver rather than guessed");
        }

        // No row at all: a proposal minted before patch_sets existed, or a synthetic set a test
        // never saved. Nothing recorded a workspace, so the live root is the honest pre-project
        // answer, exactly as it is for a saved set whose workspace_id is null.
        var workspaceId = row?.GetValueOrDefault("workspace_id")?.ToString();
        if (string.IsNullOrWhiteSpace(workspaceId)) return PatchTarget.Live();

        Workspaces.MissionWorkspace? workspace;
        try { workspace = memory.LoadWorkspace(workspaceId); }
        catch (Exception error)
        {
            return PatchTarget.Unresolvable(workspaceId,
                $"patch set {patchSetId} names workspace {workspaceId} and the workspace store could "
              + $"not be read ({error.Message}) — refused by the target resolver");
        }

        if (workspace is null)
            return PatchTarget.Unresolvable(workspaceId,
                $"patch set {patchSetId} names workspace {workspaceId} and no such workspace row "
              + "exists. The set states where it belongs and that place cannot be established — "
              + "refused by the target resolver rather than redirected to the live root");

        if (string.IsNullOrWhiteSpace(workspace.SourceRoot))
            return PatchTarget.Unresolvable(workspaceId,
                $"patch set {patchSetId}'s workspace {workspaceId} records no SourceRoot, so the "
              + "target checkout is unknown — refused by the target resolver");

        var root = PatchTarget.SafeFull(workspace.SourceRoot);
        if (!Directory.Exists(root))
            return PatchTarget.Unresolvable(workspaceId,
                $"patch set {patchSetId} targets {root} (workspace {workspaceId}) and that directory "
              + "does not exist — refused by the target resolver rather than applied elsewhere");

        var live = string.Equals(root, PatchTarget.LiveRoot(), StringComparison.OrdinalIgnoreCase);
        return new PatchTarget(true, root, live, workspaceId, null);
    }
}
