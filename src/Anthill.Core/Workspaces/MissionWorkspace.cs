namespace Anthill.Core.Workspaces;

/// <summary>
/// v3.5.0 — where a mission workspace is in its life.
///
/// Ten states rather than a boolean, because the operator questions that matter cannot be answered
/// by "does the directory exist": is this workspace still being built, is an agent inside it right
/// now, did I ask to keep it, was it abandoned by a process that died? Each of those has a different
/// correct action, and a flag collapses them into one.
///
/// The ordering is deliberate but the transitions are NOT a straight line — <see cref="Retained"/>
/// is a terminal branch off <see cref="Active"/> that cleanup may never touch, and
/// <see cref="Orphaned"/> is reached only by recovery, never by a running process.
/// </summary>
public enum WorkspaceState
{
    /// <summary>A mission asked for a workspace; nothing exists on disk yet.</summary>
    Requested = 0,

    /// <summary>Being created. Crossing this state is where a crash leaves a half-built directory.</summary>
    Preparing,

    /// <summary>Created, base revision recorded, nothing has run in it.</summary>
    Ready,

    /// <summary>An agent is working in it now.</summary>
    Active,

    /// <summary>Work paused at a recorded point, resumable after a restart.</summary>
    Checkpointed,

    /// <summary>The OPERATOR asked to keep it. Cleanup may never delete this — see the manager.</summary>
    Retained,

    /// <summary>Refused before it could be used (preparation failed, or policy declined it).</summary>
    Rejected,

    /// <summary>Finished with, queued for removal. Still on disk.</summary>
    CleanupPending,

    /// <summary>Removed from disk. The ROW SURVIVES, because attribution outlives the directory.</summary>
    Cleaned,

    /// <summary>
    /// Recorded as live, but its directory is gone — the signature of a process that died mid-run.
    /// Reached only by recovery at startup, and kept distinct from <see cref="Cleaned"/> because
    /// "we removed it" and "it vanished under us" call for different operator responses.
    /// </summary>
    Orphaned,
}

/// <summary>
/// v3.5.0 — the persisted manifest for one mission's workspace.
///
/// The roadmap's exit gate is "every change is attributable to one workspace and one base revision",
/// and that is precisely what <see cref="SandboxWorkspace"/> could not do. It creates a git worktree
/// perfectly well and then forgets everything about it: no base revision, no repository identity, no
/// state, and nothing at all after the process exits. A change produced inside it could be shown to
/// an operator but never traced back to what it was based on.
///
/// So this record is not a wrapper around the sandbox — it is the FACTS the sandbox threw away,
/// written down at the moment they are still true. The base revision in particular is captured at
/// creation and never recomputed: the whole value of "what was this based on" is that it is fixed,
/// and re-reading HEAD later answers a different question.
/// </summary>
public sealed record MissionWorkspace
{
    public required string Id { get; init; }
    public required string MissionId { get; init; }

    /// <summary>Absolute path to the workspace. Empty until <see cref="WorkspaceState.Ready"/>.</summary>
    public string Root { get; init; } = "";

    /// <summary>worktree | copy | operator — how it was made, which decides how it is torn down.</summary>
    public string Mode { get; init; } = "worktree";

    /// <summary>The checkout it was derived from. Never modified through any agent path.</summary>
    public string SourceRoot { get; init; } = "";

    /// <summary>
    /// The commit this workspace started from, captured ONCE at creation. Empty when the source is
    /// not a git checkout, which is an honest "unknown" rather than a fabricated revision.
    /// </summary>
    public string BaseRevision { get; init; } = "";

    /// <summary>
    /// Which repository this is, independently of where it happens to be checked out.
    ///
    /// The ROOT COMMIT hash, not a remote URL or a path: remotes get renamed, forks share URLs, and
    /// a path says only where a directory is today. The root commit is the one identifier that
    /// cannot change without the repository becoming a different repository — so two workspaces with
    /// the same fingerprint provably came from the same history.
    /// </summary>
    public string RepositoryFingerprint { get; init; } = "";

    public string Branch { get; init; } = "";
    public WorkspaceState State { get; init; } = WorkspaceState.Requested;

    /// <summary>Who asked to keep it, and why. Both set together or neither — see the manager.</summary>
    public string? RetainedBy { get; init; }
    public string? RetainReason { get; init; }

    /// <summary>Why it was rejected or orphaned. Never used for control flow; read by operators.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// The patch set written into this tree, or null when the tree is unpatched. v0.3.8.41.
    ///
    /// The distinction a check result cannot otherwise make. <c>RunAllowlistedCheckTool</c> resolves
    /// its working directory from whatever workspace is ambient, and TWO different trees are ambient
    /// at different moments of the same mission: the mission workspace, which is the source as the
    /// coder left it, and the disposable tree <c>VerifyPatchSet</c> materialises a patch set into.
    /// A tester that runs in the first and reports PASS has said something true about a tree that
    /// does not contain the proposal — the same shape as v3.8.22's build verdicts, which were true
    /// statements about the wrong workspace.
    ///
    /// Recorded rather than inferred from <see cref="Id"/>. The verification scope happens to name
    /// itself `verify-{patchSetId}`, and parsing that string would make a naming convention
    /// load-bearing — the kind of coupling that survives exactly until someone renames it for
    /// readability.
    ///
    /// Null is the honest default and means UNPATCHED, not unknown: every workspace that carries a
    /// patch is built by code that knows it does.
    /// </summary>
    public string? MaterializedPatchSetId { get; init; }

    /// <summary>
    /// Structural repair §3 — the REVISION identity of a patched scope, so a check running here can
    /// bind its evidence to the exact tree it judged. Null on unpatched workspaces, like the patch
    /// set id above and for the same honest reason.
    /// </summary>
    public string? RevisionId { get; init; }
    /// <summary>The materialized tree's content hash (<c>AppliedTreeHash</c>). Null when unpatched.</summary>
    public string? TreeHash { get; init; }
    /// <summary>The patch set's content hash. Null when unpatched.</summary>
    public string? PatchSetHash { get; init; }

    public DateTime CreatedAt { get; init; } = AnthillTime.NowUtc();
    public DateTime UpdatedAt { get; init; } = AnthillTime.NowUtc();

    /// <summary>
    /// States in which the directory is expected to exist on disk. Recovery compares this against
    /// reality — a workspace that claims one of these and has no directory is orphaned.
    /// </summary>
    public static readonly IReadOnlySet<WorkspaceState> OnDisk = new HashSet<WorkspaceState>
    {
        WorkspaceState.Ready, WorkspaceState.Active,
        WorkspaceState.Checkpointed, WorkspaceState.Retained, WorkspaceState.CleanupPending,
    };

    /// <summary>
    /// Cleanup may never touch a retained workspace. Expressed here, on the record, rather than only
    /// inside the sweep: it is a property of the workspace, and a rule that lives in one method is a
    /// rule the second caller does not know about.
    /// </summary>
    public bool Deletable => State != WorkspaceState.Retained;

    /// <summary>Whether work can still happen here. A cleaned or orphaned workspace cannot host it.</summary>
    public bool Usable => State is WorkspaceState.Ready or WorkspaceState.Active
        or WorkspaceState.Checkpointed or WorkspaceState.Retained;
}
