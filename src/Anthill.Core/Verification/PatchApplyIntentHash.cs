using Anthill.Core.Configuration;
using Anthill.Core.Security;
using Anthill.SDK.Common;

namespace Anthill.Core.Verification;

/// <summary>
/// The bytes at a workspace-relative path, right now, or null. v0.3.8.91.
///
/// One helper rather than the same six lines at each intent site, because the two hashes an intent
/// carries are only comparable if they were taken the same way — and a pre-hash computed by one
/// spelling against a post-hash computed by another would make every reconciliation ambiguous, which
/// is the one outcome the journal exists to avoid.
///
/// Null means absent or unreadable, and that is a real answer: a patch that CREATES a file has no
/// pre-hash, and reconciliation reads null-then-content as "the write landed" rather than as an
/// error. It resolves through the shared path guard like every other reader of the operator's tree.
/// </summary>
public static class PatchApplyIntentHash
{
    public static string? Of(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        try
        {
            var guard = new WorkspacePathGuard(AnthillRuntime.AllowedWorkspaceRoot);
            var resolved = guard.ResolveSafePath(relativePath);
            return File.Exists(resolved) ? ApplyTransaction.HashFile(resolved) : null;
        }
        catch { return null; }
    }
}
