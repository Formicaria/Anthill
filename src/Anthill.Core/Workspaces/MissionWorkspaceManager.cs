using System.Diagnostics;
using System.Text;
using Anthill.Core.Common;
using Anthill.Core.Memory;

namespace Anthill.Core.Workspaces;

/// <summary>
/// v3.5.0 — creates, tracks and reclaims mission workspaces, and is the only thing that may.
///
/// What it adds over <see cref="Sandbox.SandboxWorkspace"/>, which already makes a perfectly good
/// git worktree: MEMORY. The sandbox is <c>IDisposable</c> and records nothing — no base revision,
/// no repository identity, no state, and nothing whatsoever after the process exits. That is fine
/// for a scratch directory and inadequate for the roadmap's exit gates, which require that every
/// change be attributable to one workspace and one base revision, and that recovery after restart be
/// possible at all.
///
/// Two rules are load-bearing and neither is obvious from the method list:
///
///   - EVERY state change is persisted before it is returned. A workspace whose row says Preparing
///     while the directory is already built is recoverable; one built without a row is not, because
///     nothing knows it exists to clean it up. So the row is written first and updated after.
///
///   - CLEANUP CANNOT DELETE A RETAINED WORKSPACE. An operator who retains a workspace is usually
///     mid-investigation of something that already went wrong, and a sweep that removes their
///     evidence is the worst possible moment to be efficient. The check is on the record itself
///     (<see cref="MissionWorkspace.Deletable"/>) so a second caller cannot miss it.
///
/// Deterministic C# throughout. No model participates in workspace lifecycle, for the same reason
/// no model picks its own tool authorization: the thing that decides where an agent may write must
/// not be the thing being contained.
/// </summary>
public sealed class MissionWorkspaceManager
{
    private readonly SqliteMemory _memory;
    private readonly string _sourceRoot;

    public MissionWorkspaceManager(SqliteMemory memory, string sourceRoot)
    {
        _memory = memory;
        _sourceRoot = ResolveRepositoryRoot(sourceRoot);
    }

    /// <summary>The checkout mission workspaces are taken from. Reported, so it is never a mystery.</summary>
    public string SourceRoot => _sourceRoot;

    /// <summary>
    /// Find the git checkout that <paramref name="start"/> belongs to, walking upward.
    ///
    /// This exists because of a wiring mistake worth recording. The manager was first handed
    /// <c>AllowedWorkspaceRoot</c> — which is the agent file-tool SANDBOX (<c>.anthill/workspace</c>
    /// in a real deployment), not the repository a code mission modifies. Those are two different
    /// concepts that happen to both be called "workspace", and the sandbox is never a git checkout,
    /// so every Prepare would have been rejected and the whole feature would have silently done
    /// nothing but log <c>workspace_unavailable</c>. Caught in the browser, against a live config.
    ///
    /// Walking up is the right default rather than a guess: the sandbox lives inside the repository
    /// it belongs to, so the first enclosing <c>.git</c> is that repository. Falling back to the
    /// original path when none is found keeps the failure honest — Prepare then rejects with "not a
    /// git checkout", which is true and says so.
    ///
    /// The live checkout is still never written to; it is the SOURCE a detached worktree is taken
    /// from, and <see cref="MissionWorkspaceScope"/> confines every write to that worktree.
    /// </summary>
    internal static string ResolveRepositoryRoot(string start)
    {
        var full = Path.GetFullPath(string.IsNullOrWhiteSpace(start) ? "." : start);

        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;

        return full;
    }

    /// <summary>
    /// Where workspaces live. Under the system temp root rather than inside the repository, so a
    /// stray build, an editor's file watcher, or a `git add .` in the live checkout can never pick
    /// up a mission's in-progress work.
    /// </summary>
    public static string Root => Path.Combine(Path.GetTempPath(), "anthill-workspaces");

    /// <summary>
    /// Create a workspace for <paramref name="missionId"/> and record what it was based on.
    ///
    /// The row is written at <see cref="WorkspaceState.Requested"/> BEFORE anything touches the
    /// disk, so a crash during preparation leaves a recoverable record rather than an untracked
    /// directory. Preparation that fails lands on <see cref="WorkspaceState.Rejected"/> with the
    /// reason, never on an exception thrown into a mission that was only asking for somewhere to
    /// work.
    /// </summary>
    public MissionWorkspace Prepare(string missionId)
    {
        var workspace = new MissionWorkspace
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            MissionId = missionId ?? "",
            SourceRoot = _sourceRoot,
            State = WorkspaceState.Requested,
        };
        _memory.SaveWorkspace(workspace);

        workspace = Transition(workspace, WorkspaceState.Preparing);

        // Captured BEFORE the worktree exists, from the source checkout — that is the revision the
        // work will be based on, and reading it later from inside the workspace would answer a
        // different question once an agent has committed anything.
        var isGit = Directory.Exists(Path.Combine(_sourceRoot, ".git"));
        var baseRevision = isGit ? GitOut(_sourceRoot, "rev-parse HEAD") : "";
        var fingerprint = isGit ? RootCommit(_sourceRoot) : "";
        var branch = isGit ? GitOut(_sourceRoot, "rev-parse --abbrev-ref HEAD") : "";

        Directory.CreateDirectory(Root);
        var target = Path.Combine(Root, $"{workspace.MissionId}-{workspace.Id}");

        string mode;
        if (isGit)
        {
            // Detached deliberately: a worktree on a named branch would move the live checkout's
            // branch pointer, which is exactly the "cannot modify the active checkout" gate.
            var (ok, error) = Git(_sourceRoot, $"worktree add --detach \"{target}\" HEAD");
            if (!ok)
                return Transition(workspace, WorkspaceState.Rejected,
                    note: $"git worktree failed: {error.Trim()}");
            mode = "worktree";
        }
        else
        {
            // No copy fallback here, on purpose. SandboxWorkspace copies because patch VERIFICATION
            // must see uncommitted working-tree state; a mission workspace must be attributable to a
            // base revision, and a copy of an unversioned directory has none to record. Refusing is
            // more useful than issuing a workspace whose provenance is a fiction.
            return Transition(workspace, WorkspaceState.Rejected,
                note: $"source is not a git checkout, so no base revision can be recorded: {_sourceRoot}");
        }

        var ready = workspace with
        {
            Root = target,
            Mode = mode,
            BaseRevision = baseRevision,
            RepositoryFingerprint = fingerprint,
            Branch = branch,
            State = WorkspaceState.Ready,
            UpdatedAt = AnthillTime.NowUtc(),
        };
        _memory.SaveWorkspace(ready);
        return ready;
    }

    public MissionWorkspace? Get(string id) => _memory.LoadWorkspace(id);
    public IReadOnlyList<MissionWorkspace> All() => _memory.LoadWorkspaces();

    /// <summary>Mark an agent as working here. Rejected when the workspace cannot host work.</summary>
    public MissionWorkspace? Activate(string id) => Move(id, WorkspaceState.Active);

    /// <summary>Pause at a resumable point, so a restart can pick the work back up.</summary>
    public MissionWorkspace? Checkpoint(string id) => Move(id, WorkspaceState.Checkpointed);

    /// <summary>
    /// Keep this workspace until the operator says otherwise. Both who and why are required: a
    /// retention with no reason becomes permanent clutter nobody dares delete, which is the failure
    /// mode of every "keep this" flag.
    /// </summary>
    public MissionWorkspace? Retain(string id, string retainedBy, string reason)
    {
        var workspace = _memory.LoadWorkspace(id);
        if (workspace is null || !workspace.Usable) return null;
        if (string.IsNullOrWhiteSpace(retainedBy) || string.IsNullOrWhiteSpace(reason)) return null;

        var retained = workspace with
        {
            State = WorkspaceState.Retained,
            RetainedBy = retainedBy.Trim(),
            RetainReason = reason.Trim(),
            UpdatedAt = AnthillTime.NowUtc(),
        };
        _memory.SaveWorkspace(retained);
        return retained;
    }

    /// <summary>
    /// Release a retention, putting the workspace back where cleanup can reach it. The only way out
    /// of <see cref="WorkspaceState.Retained"/>, and deliberately explicit — nothing should be able
    /// to un-retain a workspace as a side effect of doing something else.
    /// </summary>
    public MissionWorkspace? Release(string id)
    {
        var workspace = _memory.LoadWorkspace(id);
        if (workspace is null || workspace.State != WorkspaceState.Retained) return null;

        var released = workspace with
        {
            State = WorkspaceState.CleanupPending,
            RetainedBy = null,
            RetainReason = null,
            UpdatedAt = AnthillTime.NowUtc(),
        };
        _memory.SaveWorkspace(released);
        return released;
    }

    /// <summary>Queue for removal. A retained workspace refuses, rather than being silently queued.</summary>
    public MissionWorkspace? RequestCleanup(string id)
    {
        var workspace = _memory.LoadWorkspace(id);
        if (workspace is null || !workspace.Deletable) return null;
        return Move(id, WorkspaceState.CleanupPending);
    }

    /// <summary>
    /// Remove every workspace queued for cleanup, and report what was removed.
    ///
    /// Re-checks <see cref="MissionWorkspace.Deletable"/> per workspace even though only pending
    /// ones are selected: the query and the delete are separate moments, and an operator retaining a
    /// workspace between them must win. Cheap check, unrecoverable mistake.
    /// </summary>
    public IReadOnlyList<string> Cleanup()
    {
        var removed = new List<string>();

        foreach (var workspace in _memory.LoadWorkspaces()
                     .Where(w => w.State == WorkspaceState.CleanupPending))
        {
            if (!workspace.Deletable) continue;

            Remove(workspace);
            // The index goes with the tree. An index outliving the workspace it describes is a set
            // of answers about files nobody can read — and it would be reused by id if a later
            // workspace happened to land on the same revision.
            try { _memory.DeleteRepositoryIndexes(workspace.Id); } catch { }
            // The ROW SURVIVES the directory. Attribution outlives the files: an operator asking
            // six weeks later what a merged change was based on needs the base revision, and the
            // workspace it came from is long gone by then.
            _memory.SaveWorkspace(workspace with
            {
                State = WorkspaceState.Cleaned,
                UpdatedAt = AnthillTime.NowUtc(),
            });
            removed.Add(workspace.Id);
        }

        return removed;
    }

    /// <summary>
    /// Reconcile the recorded workspaces with what is actually on disk, at startup.
    ///
    /// This is the exit gate "workspace recovery after restart is tested", and the case it exists
    /// for is the ugly one: a process that died mid-mission leaves rows claiming Active or Preparing
    /// while the directory may or may not still be there. Both halves of that are handled, and they
    /// are handled DIFFERENTLY on purpose:
    ///
    ///   - row says live, directory gone      → Orphaned. Something removed it under us, and an
    ///                                          operator should know that rather than see it quietly
    ///                                          recorded as a clean deletion.
    ///   - row says Preparing, directory there → Rejected, and the directory removed. A half-built
    ///                                          workspace has no recorded base revision, so nothing
    ///                                          made in it could ever be attributed.
    ///   - row says Active, directory there   → Checkpointed. The work survived the process; leaving
    ///                                          it Active would claim an agent is running in it.
    ///
    /// Returns what it changed, so the caller can report it rather than have recovery be invisible.
    /// </summary>
    public IReadOnlyList<string> Recover()
    {
        var notes = new List<string>();

        foreach (var workspace in _memory.LoadWorkspaces())
        {
            var exists = workspace.Root.Length > 0 && Directory.Exists(workspace.Root);

            if (MissionWorkspace.OnDisk.Contains(workspace.State) && !exists)
            {
                _memory.SaveWorkspace(workspace with
                {
                    State = WorkspaceState.Orphaned,
                    Note = "recorded as live, but its directory is gone",
                    UpdatedAt = AnthillTime.NowUtc(),
                });
                notes.Add($"{workspace.Id}: orphaned (directory missing)");
            }
            else if (workspace.State == WorkspaceState.Preparing)
            {
                if (exists) Remove(workspace);
                _memory.SaveWorkspace(workspace with
                {
                    State = WorkspaceState.Rejected,
                    Note = "preparation did not complete; no base revision was ever recorded",
                    UpdatedAt = AnthillTime.NowUtc(),
                });
                notes.Add($"{workspace.Id}: rejected (preparation interrupted)");
            }
            else if (workspace.State == WorkspaceState.Active && exists)
            {
                _memory.SaveWorkspace(workspace with
                {
                    State = WorkspaceState.Checkpointed,
                    Note = "process restarted while this workspace was active",
                    UpdatedAt = AnthillTime.NowUtc(),
                });
                notes.Add($"{workspace.Id}: checkpointed (was active at restart)");
            }
        }

        return notes;
    }

    /// <summary>What an agent changed here, as porcelain status. Empty string when it is unreadable.</summary>
    public string ChangeSummary(MissionWorkspace workspace)
    {
        if (workspace.Mode != "worktree" || !Directory.Exists(workspace.Root)) return "";
        var (ok, output) = Git(workspace.Root, "status --porcelain");
        return ok ? output.Trim() : "";
    }

    // ---- internals -----------------------------------------------------------------------------

    private MissionWorkspace? Move(string id, WorkspaceState state)
    {
        var workspace = _memory.LoadWorkspace(id);
        if (workspace is null) return null;
        if (state != WorkspaceState.CleanupPending && !workspace.Usable) return null;
        return Transition(workspace, state);
    }

    private MissionWorkspace Transition(MissionWorkspace workspace, WorkspaceState state, string? note = null)
    {
        var moved = workspace with
        {
            State = state,
            Note = note ?? workspace.Note,
            UpdatedAt = AnthillTime.NowUtc(),
        };
        _memory.SaveWorkspace(moved);
        return moved;
    }

    /// <summary>
    /// Take a workspace off disk. Best-effort by design: a worktree the operator has already deleted
    /// by hand must not make cleanup throw, because the state change is the part that matters and a
    /// sweep that dies on the first oddity leaves everything after it queued forever.
    /// </summary>
    private void Remove(MissionWorkspace workspace)
    {
        try
        {
            if (workspace.Mode == "worktree" && Directory.Exists(workspace.SourceRoot))
            {
                Git(workspace.SourceRoot, $"worktree remove --force \"{workspace.Root}\"");
                Git(workspace.SourceRoot, "worktree prune");
            }
            if (Directory.Exists(workspace.Root)) Directory.Delete(workspace.Root, recursive: true);
        }
        catch { /* see the remarks above */ }
    }

    /// <summary>
    /// The repository's root commit — its identity, independent of where it is checked out.
    ///
    /// Chosen over a remote URL or a path because both of those change without the repository
    /// changing: remotes get renamed, forks share URLs, and a path says only where a directory sits
    /// today. Two workspaces with the same fingerprint provably share a history.
    /// </summary>
    private static string RootCommit(string root)
    {
        var output = GitOut(root, "rev-list --max-parents=0 HEAD");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
    }

    private static string GitOut(string workdir, string args)
    {
        var (ok, output) = Git(workdir, args);
        return ok ? output.Trim() : "";
    }

    private static (bool Ok, string Output) Git(string workdir, string args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workdir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,   // v0.3.8.55: children emit UTF-8, not the OS codepage
                StandardErrorEncoding = Encoding.UTF8,
            })!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);
            return (process.ExitCode == 0, process.ExitCode == 0 ? stdout : stderr);
        }
        catch (Exception error)
        {
            return (false, error.Message);
        }
    }
}
