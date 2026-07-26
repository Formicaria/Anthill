using Anthill.Core.Sandbox;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.10.1: the isolation guarantees sandboxed patch verification depends on. The verify path
/// itself needs a live Queen + toolchain (covered by manual/CI validation), so these tests pin the
/// property that matters: patch content written into a sandbox copy NEVER reaches the source tree,
/// including for new files and nested paths, and the copy disappears afterward.
/// </summary>
public class SandboxedVerifyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_sbxv_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Workspace()
    {
        var ws = Path.Combine(_dir, "live"); Directory.CreateDirectory(ws);
        Directory.CreateDirectory(Path.Combine(ws, "src"));
        File.WriteAllText(Path.Combine(ws, "src", "Program.cs"), "// original content");
        File.WriteAllText(Path.Combine(ws, "README.md"), "# original");
        return ws;
    }

    [Fact]
    public void PatchWrittenIntoSandbox_NeverReachesLiveWorkspace()
    {
        var live = Workspace();
        string sandboxRoot;
        using (var sbx = SandboxWorkspace.Create(live, preferCopy: true))
        {
            sandboxRoot = sbx.Root;
            // Simulate exactly what VerifyInSandbox does: overwrite the patched file in the copy.
            var target = Path.Combine(sbx.Root, "src", "Program.cs");
            Assert.Equal("// original content", File.ReadAllText(target)); // on-disk state copied
            File.WriteAllText(target, "// PATCHED for verification");
            Assert.Equal("// PATCHED for verification", File.ReadAllText(target));
        }
        Assert.Equal("// original content", File.ReadAllText(Path.Combine(live, "src", "Program.cs")));
        Assert.False(Directory.Exists(sandboxRoot));
    }

    [Fact]
    public void NewFilePatch_CreatesOnlyInsideSandbox()
    {
        var live = Workspace();
        using (var sbx = SandboxWorkspace.Create(live, preferCopy: true))
        {
            var target = Path.Combine(sbx.Root, "docs", "NEW_DOC.md");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "brand new");
            Assert.True(File.Exists(target));
        }
        Assert.False(File.Exists(Path.Combine(live, "docs", "NEW_DOC.md")));
        Assert.False(Directory.Exists(Path.Combine(live, "docs")));
    }

    [Fact]
    public void UncommittedLocalState_IsVisibleInCopySandbox()
    {
        // Why preferCopy matters: a HEAD worktree would miss uncommitted edits the patch was
        // diffed against, making verification test the wrong baseline.
        var live = Workspace();
        File.WriteAllText(Path.Combine(live, "README.md"), "# locally edited, uncommitted");
        using var sbx = SandboxWorkspace.Create(live, preferCopy: true);
        Assert.Equal("# locally edited, uncommitted", File.ReadAllText(Path.Combine(sbx.Root, "README.md")));
    }

    [Fact]
    public void PathEscapeAttempt_IsDetectableBeforeWrite()
    {
        var live = Workspace();
        using var sbx = SandboxWorkspace.Create(live, preferCopy: true);
        var escaped = Path.GetFullPath(Path.Combine(sbx.Root, "../../etc/evil.conf"));
        Assert.False(escaped.StartsWith(Path.GetFullPath(sbx.Root), StringComparison.OrdinalIgnoreCase));
    }
}
