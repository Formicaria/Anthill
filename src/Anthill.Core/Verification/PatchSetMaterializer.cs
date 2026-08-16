using System.Security.Cryptography;
using System.Text;
using Anthill.Core.Domain;
using Anthill.Core.Sandbox;

namespace Anthill.Core.Verification;

/// <summary>
/// A patch set written into a disposable copy of the workspace, so it can be verified where it
/// actually lives. v3.8.23.
///
/// This is the piece that makes v3.8.22's build gate mean what its changelog claimed. That release
/// made <c>BuildVerifier</c> run on every patch — against <c>AnthillRuntime.AllowedWorkspaceRoot</c>,
/// the PRIMARY tree, which does not contain the patch. It proved that the repository builds. It
/// could never have proved that the proposal builds, and a proposal full of code that does not
/// compile passed it every time.
///
/// The capability to do this correctly already existed and had no mission-path caller — the same
/// shape as the <c>VerificationRunner</c> finding, twice over. <c>SandboxWorkspace</c> (v1.8.x) makes
/// isolated copies, and <c>PatchVerifyRunner</c> in <c>Anthill.Api</c> has materialised a patch into
/// one and built it since v1.8.24. But it is operator-triggered through
/// <c>POST /patches/{id}/verify</c>, it handles ONE patch rather than a set, and nothing in
/// <c>ProcessPatchProposals</c> has ever called it. This brings that capability into the core at
/// patch-SET granularity and puts it on the mission path.
/// </summary>
public sealed class MaterializedPatchSet : IDisposable
{
    private readonly SandboxWorkspace _sandbox;
    private bool _disposed;

    internal MaterializedPatchSet(SandboxWorkspace sandbox, string patchSetId, string baseRevision,
        IReadOnlyList<string> appliedPaths, string patchSetHash, string appliedTreeHash)
    {
        _sandbox = sandbox;
        PatchSetId = patchSetId;
        BaseRevision = baseRevision;
        AppliedPaths = appliedPaths;
        PatchSetHash = patchSetHash;
        AppliedTreeHash = appliedTreeHash;
    }

    /// <summary>Where the patched tree is. This is what verification must be pointed at.</summary>
    public string Root => _sandbox.Root;

    /// <summary>`worktree` or `copy` — recorded because they answer different questions about what
    /// the tree contained before the patch landed.</summary>
    public string Mode => _sandbox.Mode;

    public string PatchSetId { get; }

    /// <summary>The source revision the patch was applied ON TOP OF. Empty when the source is not a
    /// git checkout, which is recorded rather than guessed at.</summary>
    public string BaseRevision { get; }

    public IReadOnlyList<string> AppliedPaths { get; }

    /// <summary>
    /// Hash of the patch set's CONTENT — ordered path, change type and new content. Two runs of the
    /// same proposals produce the same hash, so a verdict can be bound to the exact change it
    /// judged rather than to a patch-set id that a later edit could reuse.
    /// </summary>
    public string PatchSetHash { get; }

    /// <summary>
    /// Hash of the patched files AS THEY LANDED, read back from disk. Distinct from
    /// <see cref="PatchSetHash"/> on purpose: that one says what was asked for, this one says what
    /// is actually in the tree the verifier is about to read. They differ if materialisation is
    /// partial, and a bundle bound to only the first would not be able to tell.
    /// </summary>
    public string AppliedTreeHash { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sandbox.Dispose();
    }
}

public static class PatchSetMaterializer
{
    /// <summary>The outcome of trying to write a patch set into a sandbox.</summary>
    /// <param name="Materialized">Null when nothing was verified — see <paramref name="Problem"/>.</param>
    public sealed record Result(MaterializedPatchSet? Materialized, string? Problem)
    {
        public bool Ok => Materialized is not null;
    }

    /// <summary>
    /// Copy the workspace, write every proposal into the copy, and hand back the patched root.
    ///
    /// FAILS CLOSED AND AS A UNIT. If any proposal cannot be written — a path that escapes the
    /// sandbox, a delete of something absent, an IO error — the whole materialisation is abandoned
    /// and the sandbox is disposed. A patch set is applied as a unit and must be verified as one;
    /// verifying a partially-written set would produce a verdict about a tree that will never exist.
    ///
    /// <c>preferCopy: true</c> matches what patch verification has always needed and why
    /// <c>PatchVerifyRunner</c> chose it: the tree must be the workspace AS IT IS ON DISK, including
    /// uncommitted local state the patch was diffed against. A worktree of HEAD is a different tree,
    /// and a patch that applies cleanly to the working copy may not apply to HEAD at all.
    /// </summary>
    public static Result Materialize(PatchSet patchSet, string sourceRoot)
    {
        if (patchSet.Proposals.Count == 0) return new(null, "patch set has no proposals");
        if (!Directory.Exists(sourceRoot)) return new(null, $"source root does not exist: {sourceRoot}");

        SandboxWorkspace sandbox;
        try { sandbox = SandboxWorkspace.Create(sourceRoot, preferCopy: true); }
        catch (Exception e) { return new(null, $"sandbox could not be created: {e.Message}"); }

        try
        {
            var sandboxRoot = Path.GetFullPath(sandbox.Root);
            var applied = new List<string>();

            foreach (var proposal in patchSet.Proposals)
            {
                if (string.IsNullOrWhiteSpace(proposal.FilePath))
                    throw new InvalidOperationException($"proposal {proposal.Id} has no file path");

                // A relative path with enough `..` in it resolves outside the sandbox, and writing
                // there would modify the operator's real tree from inside what is supposed to be an
                // isolated verification.
                //
                // v0.3.8.59 (PLAN.md §1b S1): this one had the separator RIGHT and links wrong —
                // Path.GetFullPath is lexical, so a link inside the sandbox pointing at the live
                // checkout resolved to a path still textually under sandboxRoot and materialised
                // straight through it. Now the same resolver as everywhere else.
                var containment = Security.PathContainment.Resolve(sandboxRoot, proposal.FilePath);
                if (!containment.Allowed)
                    throw new InvalidOperationException(
                        $"proposal {proposal.Id} path escapes the sandbox: {proposal.FilePath}");
                var target = containment.Path;

                // v0.3.8.52 — the delete arm that used to sit here is gone.
                //
                // It was `if (File.Exists(target)) File.Delete(target);` — a silent no-op when the
                // file was absent, where the operator's applier refuses (RefusedTargetMissing). That
                // is the same class of divergence as the v3.8.32 defect below, just quieter: a patch
                // set containing a delete of something already gone materialised clean and verified
                // green, and then failed at apply time. Delete and rename now go through Compute
                // with everything else, and the sandbox refuses exactly where the tool would.
                //
                // The destination of a rename gets the sandbox-containment check the source got, for
                // the reason the source has one: a `..`-laden destination writes into the operator's
                // real tree from inside what is supposed to be isolated verification.
                string? destinationTarget = null;
                if (!string.IsNullOrWhiteSpace(proposal.DestinationPath))
                {
                    var destinationContainment =
                        Security.PathContainment.Resolve(sandboxRoot, proposal.DestinationPath);
                    if (!destinationContainment.Allowed)
                        throw new InvalidOperationException(
                            $"proposal {proposal.Id} destination escapes the sandbox: {proposal.DestinationPath}");
                    destinationTarget = destinationContainment.Path;
                }

                // v3.8.32 — THE defect this file shipped with.
                //
                // This wrote `proposal.NewContent` over the whole file for every change type,
                // ignoring `OldContent` entirely. ApplyPatchTool — the applier that will actually
                // touch the operator's tree — replaces one exact occurrence of `old_content` and
                // refuses if it is absent or ambiguous. So for any modify that was not a whole-file
                // rewrite, this materialised a file consisting only of the patch fragment, and the
                // verifier then compiled and attested to a tree the colony could never produce.
                //
                // v3.8.23's claim was "patches are verified in a sandbox that contains them". The
                // sandbox contained something else. Shared semantics now; a divergence requires
                // editing PatchApply, where the reason is written down.
                var current = File.Exists(target) ? File.ReadAllText(target) : null;
                var outcome = PatchApply.Compute(
                    ChangeTypeName(proposal.ChangeType), proposal.OldContent, proposal.NewContent, current,
                    // v0.3.8.37: verify against the base the patch was BUILT on. A sandbox that
                    // accepts a stale patch attests to a tree the operator's applier would refuse.
                    proposal.BaseHash,
                    proposal.DestinationPath,
                    destinationTarget is not null
                        && (File.Exists(destinationTarget) || Directory.Exists(destinationTarget)));
                if (!outcome.Ok)
                    throw new InvalidOperationException($"proposal {proposal.Id} ({proposal.FilePath}): {outcome.Reason}");

                switch (outcome.Status)
                {
                    case PatchApplyStatus.Deleted:
                        File.Delete(target);
                        break;

                    case PatchApplyStatus.Renamed:
                        Directory.CreateDirectory(Path.GetDirectoryName(destinationTarget!)!);
                        File.Move(target, destinationTarget!);
                        // Both ends are recorded as applied. The source is now absent and the
                        // destination present, and AppliedTreeHash reads each back from disk — a
                        // materialisation that moved the file only halfway has to be visible in the
                        // hash the verdict is bound to.
                        applied.Add(proposal.DestinationPath!);
                        break;

                    default:
                        if (outcome.Status == PatchApplyStatus.Created)
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.WriteAllText(target, outcome.Content!);
                        break;
                }
                applied.Add(proposal.FilePath);
            }

            return new(new MaterializedPatchSet(
                sandbox,
                patchSet.Id,
                BaseRevisionOf(sourceRoot),
                applied,
                HashPatchSet(patchSet),
                HashAppliedTree(sandboxRoot, applied)), null);
        }
        catch (Exception e)
        {
            sandbox.Dispose();
            return new(null, e.Message);
        }
    }

    /// <summary>The wire spelling <see cref="PatchApply"/> expects. v0.3.8.52: all four change types
    /// route through it now — nothing is handled ahead of it — and an unknown one is refused there
    /// rather than defaulting to a modify.</summary>
    private static string ChangeTypeName(PatchChangeType kind) => kind switch
    {
        PatchChangeType.Add => PatchApply.Add,
        PatchChangeType.Modify => PatchApply.Modify,
        PatchChangeType.Delete => PatchApply.Delete,
        PatchChangeType.Rename => PatchApply.Rename,
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Content hash of the set: every proposal's path, change type, OLD content and new content,
    /// ORDERED by path so two sets with the same changes in a different order hash the same. The
    /// order proposals arrive in is an artifact of how a model emitted them, not a property of the
    /// change.
    ///
    /// v3.8.32 added <c>OldContent</c>. It was excluded while materialization ignored it, which was
    /// self-consistent and wrong in the same direction: two patch sets that replace DIFFERENT text
    /// with the same new text are different changes and produce different trees, and hashing them
    /// identically would let a cached or compared verification stand in for one it never ran.
    /// </summary>
    public static string HashPatchSet(PatchSet patchSet)
    {
        var sb = new StringBuilder();
        foreach (var p in patchSet.Proposals
                     .OrderBy(p => p.FilePath, StringComparer.Ordinal)
                     .ThenBy(p => p.Id, StringComparer.Ordinal))
            sb.Append(p.FilePath).Append('\n')
              .Append(p.ChangeType).Append('\n')
              .Append(p.OldContent ?? "").Append('\n')
              // v0.3.8.52 added DestinationPath for the reason v3.8.32 added OldContent: two renames
              // of the same file to DIFFERENT destinations are different changes that produce
              // different trees, and hashing them identically would let a verdict for one stand in
              // for the other.
              .Append(p.DestinationPath ?? "").Append('\n')
              .Append(p.NewContent ?? "").Append('\n');
        return Sha(sb.ToString());
    }

    /// <summary>What the patched files actually contain on disk, read back after writing.</summary>
    private static string HashAppliedTree(string root, IReadOnlyList<string> relativePaths)
    {
        var sb = new StringBuilder();
        foreach (var rel in relativePaths.OrderBy(p => p, StringComparer.Ordinal))
        {
            var full = Path.Combine(root, rel);
            sb.Append(rel).Append('\n')
              .Append(File.Exists(full) ? Sha(File.ReadAllText(full)) : "absent")
              .Append('\n');
        }
        return Sha(sb.ToString());
    }

    /// <summary>
    /// HEAD of the SOURCE checkout, read before anything is written. Empty for a non-git source,
    /// which is honest — <c>MissionWorkspaceManager.Prepare</c> refuses outright in that case, but
    /// refusing here would mean an unversioned project could never verify a patch at all.
    /// </summary>
    private static string BaseRevisionOf(string sourceRoot)
    {
        if (!Directory.Exists(Path.Combine(sourceRoot, ".git"))) return "";
        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "git", "rev-parse HEAD")
            {
                WorkingDirectory = sourceRoot, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,   // v0.3.8.55: children emit UTF-8, not the OS codepage
                StandardErrorEncoding = Encoding.UTF8,
            });
            if (proc is null) return "";
            var output = proc.StandardOutput.ReadToEnd().Trim();
            // v0.3.8.57 — the short-circuit was correct about the RESULT and left the process
            // running. `&&` skips ExitCode on timeout (which would have thrown), so this site
            // returned "" honestly and abandoned git and its children.
            if (!proc.WaitForExit(10_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return "";
            }
            return proc.ExitCode == 0 ? output : "";
        }
        catch { return ""; }
    }

    private static string Sha(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
