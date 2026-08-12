using Anthill.Core.Common;
using Anthill.Core.Verification;

namespace Anthill.Core.Workspaces;

/// <summary>
/// The durable identity of one materialized patch revision — structural repair §3.
///
/// "Tester PASS" for a coding mission was invalid before this existed: VerifyPatchSet materialized
/// the patched tree, verified it, and DISPOSED it in the same call — so the policy-inserted tester,
/// running later as its own task, resolved the ambient MISSION workspace and judged the UNPATCHED
/// source. A broken patch passed testing whenever the original repository still built.
/// </summary>
public sealed record MissionRevision(
    string RevisionId,
    string MissionId,
    string PatchSetId,
    string PatchSetHash,
    string BaseRevision,
    string TreeHash,
    string Root,
    string Mode,
    string ProducingTaskId,
    DateTime CreatedAt);

/// <summary>
/// Keeps the CURRENT materialized revision per mission ALIVE until downstream deterministic
/// consumers (builder/tester/soldier/verifier) have run against it, and owns its disposal.
///
/// Ownership rules, stated because they are the whole point:
/// <list type="bullet">
/// <item>Registering a revision TRANSFERS ownership of the materialized tree here. The registrant
///   must not dispose it.</item>
/// <item>A NEW revision for the same mission replaces the old one and disposes its tree — §4's
///   fresh-retest invariant begins here: once PatchSet B is materialized, A's tree is gone and
///   nothing can accidentally re-run checks in it.</item>
/// <item><see cref="ReleaseMission"/> disposes at mission finalization. A crashed process leaks a
///   temp sandbox directory, which the OS temp cleaner owns — an honest, bounded cost.</item>
/// </list>
///
/// Process-lifetime by design: a restarted host has no materialized trees, and a reloaded mission's
/// checks must re-materialize rather than trust a path that no longer exists.
/// </summary>
public static class MissionRevisionRegistry
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, (MissionRevision Revision, MaterializedPatchSet Tree)> Live =
        new(StringComparer.Ordinal);

    /// <summary>Register the materialized tree as the mission's CURRENT revision, taking ownership.
    /// Any prior revision for the mission is disposed — its evidence is stale by construction.</summary>
    public static MissionRevision Register(string missionId, string producingTaskId, MaterializedPatchSet tree)
    {
        var revision = new MissionRevision(
            RevisionId: $"rev_{Guid.NewGuid():N}"[..20],
            MissionId: missionId,
            PatchSetId: tree.PatchSetId,
            PatchSetHash: tree.PatchSetHash,
            BaseRevision: tree.BaseRevision,
            TreeHash: tree.AppliedTreeHash,
            Root: tree.Root,
            Mode: tree.Mode,
            ProducingTaskId: producingTaskId,
            CreatedAt: AnthillTime.NowUtc());

        (MissionRevision, MaterializedPatchSet)? replaced = null;
        lock (Lock)
        {
            if (Live.TryGetValue(missionId, out var old)) replaced = old;
            Live[missionId] = (revision, tree);
        }
        if (replaced is { } r)
        {
            try { r.Item2.Dispose(); } catch { /* a temp dir that would not delete is the OS's problem */ }
        }
        return revision;
    }

    /// <summary>The mission's current revision, or null when no patch has been materialized (or the
    /// process restarted — in which case the tree is honestly gone).</summary>
    public static MissionRevision? CurrentFor(string missionId)
    {
        lock (Lock) return Live.TryGetValue(missionId, out var entry) ? entry.Revision : null;
    }

    /// <summary>Dispose and forget the mission's revision. Called at mission finalization.</summary>
    public static void ReleaseMission(string missionId)
    {
        (MissionRevision, MaterializedPatchSet)? entry = null;
        lock (Lock)
        {
            if (Live.TryGetValue(missionId, out var e)) { entry = e; Live.Remove(missionId); }
        }
        if (entry is { } r)
        {
            try { r.Item2.Dispose(); } catch { }
        }
    }

    /// <summary>Test hook: how many live revisions exist. Also honest telemetry.</summary>
    public static int LiveCount { get { lock (Lock) return Live.Count; } }
}
