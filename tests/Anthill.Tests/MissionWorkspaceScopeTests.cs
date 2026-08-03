using Anthill.Core.Security;
using Anthill.Core.Workspaces;
using Xunit;

// `Task` in this codebase is Anthill.Core.Domain.Task (the mission task), reachable here through a
// global using. The threading one must be named — the same alias ApiHost uses.
using ThreadingTask = System.Threading.Tasks.Task;

namespace Anthill.Tests;

/// <summary>
/// v3.5.0 — the exit gate: a code mission cannot modify the active checkout through any agent path.
///
/// Before this, that gate was not merely unmet — it was INVERTED. Every write tool
/// (<c>write_text_file</c>, <c>apply_patch</c>) is a singleton constructed once at startup against
/// one <see cref="WorkspacePathGuard"/> rooted at the live checkout, so the operator's working tree
/// was the only place an agent could write.
///
/// The tests below are about containment, not about files: they assert what the guard RESOLVES and
/// what it REFUSES, because the guard is the single chokepoint every file tool passes through, and
/// a property proven there holds for all of them at once.
/// </summary>
public class MissionWorkspaceScopeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _live;
    private readonly string _workspace;

    public MissionWorkspaceScopeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-scope-" + Guid.NewGuid().ToString("N")[..10]);
        _live = Path.Combine(_dir, "live-checkout");
        _workspace = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(_live);
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private MissionWorkspace Workspace(WorkspaceState state = WorkspaceState.Active) => new()
    {
        Id = "ws1", MissionId = "m1", Root = _workspace, SourceRoot = _live, State = state,
    };

    // ---- the gate ---------------------------------------------------------------------------

    /// <summary>
    /// Inside a scope, a relative path an agent supplies lands in the MISSION WORKSPACE — not in the
    /// live checkout it would have landed in a moment earlier.
    /// </summary>
    [Fact]
    public void InsideAScope_ARelativePathResolvesIntoTheWorkspace()
    {
        var guard = new WorkspacePathGuard(_live);

        using (MissionWorkspaceScope.Enter(Workspace()))
        {
            var resolved = guard.ResolveSafePath("src/Program.cs");

            Assert.StartsWith(Path.GetFullPath(_workspace), resolved);
            Assert.DoesNotContain("live-checkout", resolved);
        }
    }

    /// <summary>
    /// The headline refusal. An agent that names the live checkout by ABSOLUTE path — the obvious
    /// way around a root that is merely a default — is denied, because containment is checked
    /// against the effective root rather than the configured one.
    /// </summary>
    [Fact]
    public void InsideAScope_TheLiveCheckoutIsUnreachable_EvenByAbsolutePath()
    {
        var guard = new WorkspacePathGuard(_live);
        var target = Path.Combine(_live, "Program.cs");

        using (MissionWorkspaceScope.Enter(Workspace()))
        {
            var error = Assert.Throws<UnauthorizedAccessException>(() => guard.ResolveSafePath(target));
            Assert.Contains(Path.GetFullPath(_workspace), error.Message);
        }
    }

    /// <summary>Traversal out of the workspace is refused for the same reason, by the same check.</summary>
    [Theory]
    [InlineData("../live-checkout/Program.cs")]
    [InlineData("../../etc/passwd")]
    [InlineData("subdir/../../live-checkout/x")]
    public void InsideAScope_TraversalOutOfTheWorkspaceIsRefused(string path)
    {
        var guard = new WorkspacePathGuard(_live);

        using (MissionWorkspaceScope.Enter(Workspace()))
            Assert.Throws<UnauthorizedAccessException>(() => guard.ResolveSafePath(path));
    }

    // ---- it only ever narrows ------------------------------------------------------------------

    /// <summary>
    /// Outside a scope, nothing changes. This is why the CLI, operator tooling and every existing
    /// test are unaffected: the mechanism narrows what a mission may reach and never widens it.
    /// </summary>
    [Fact]
    public void OutsideAScope_TheConfiguredRootStillApplies()
    {
        var guard = new WorkspacePathGuard(_live);

        var resolved = guard.ResolveSafePath("Program.cs");

        Assert.Equal(Path.GetFullPath(_live), guard.EffectiveRoot);
        Assert.StartsWith(Path.GetFullPath(_live), resolved);
    }

    /// <summary>Leaving a scope restores the previous root — an escape must not outlive the mission.</summary>
    [Fact]
    public void LeavingAScope_RestoresThePreviousRoot()
    {
        var guard = new WorkspacePathGuard(_live);

        using (MissionWorkspaceScope.Enter(Workspace()))
            Assert.Equal(Path.GetFullPath(_workspace), guard.EffectiveRoot);

        Assert.Equal(Path.GetFullPath(_live), guard.EffectiveRoot);
        Assert.Null(MissionWorkspaceScope.Current);
    }

    /// <summary>Scopes nest, so a nested mission cannot strand the outer one in the wrong workspace.</summary>
    [Fact]
    public void ScopesNest()
    {
        var inner = Path.Combine(_dir, "inner");
        Directory.CreateDirectory(inner);
        var guard = new WorkspacePathGuard(_live);

        using (MissionWorkspaceScope.Enter(Workspace()))
        {
            using (MissionWorkspaceScope.Enter(Workspace() with { Id = "ws2", Root = inner }))
                Assert.Equal(Path.GetFullPath(inner), guard.EffectiveRoot);

            Assert.Equal(Path.GetFullPath(_workspace), guard.EffectiveRoot);
        }
    }

    /// <summary>
    /// An UNUSABLE workspace does not become the root. A cleaned or orphaned workspace has no
    /// directory, and confining writes to a path that does not exist would turn every write into a
    /// confusing filesystem error instead of a clear refusal.
    /// </summary>
    [Theory]
    [InlineData(WorkspaceState.Cleaned)]
    [InlineData(WorkspaceState.Orphaned)]
    [InlineData(WorkspaceState.Rejected)]
    public void AnUnusableWorkspace_IsNotUsedAsARoot(WorkspaceState state)
    {
        var guard = new WorkspacePathGuard(_live);

        using (MissionWorkspaceScope.Enter(Workspace(state)))
            Assert.Equal(Path.GetFullPath(_live), guard.EffectiveRoot);
    }

    /// <summary>
    /// The ambient value flows across the Task continuations parallel task execution uses. Without
    /// this the scope would silently stop applying the moment an ant awaited anything — a
    /// containment boundary that holds only on the synchronous path is not a boundary.
    /// </summary>
    [Fact]
    public async ThreadingTask TheScopeFlowsAcrossAsyncContinuations()
    {
        var guard = new WorkspacePathGuard(_live);

        using (MissionWorkspaceScope.Enter(Workspace()))
        {
            var resolved = await ThreadingTask.Run(async () =>
            {
                await ThreadingTask.Yield();
                return guard.ResolveSafePath("nested/file.cs");
            });

            Assert.StartsWith(Path.GetFullPath(_workspace), resolved);
        }
    }

    /// <summary>
    /// And it stays ISOLATED between concurrent flows. Two missions running in parallel must not be
    /// able to see each other's workspace — the reason this is AsyncLocal rather than a static.
    /// </summary>
    [Fact]
    public async ThreadingTask TwoConcurrentMissions_DoNotSeeEachOthersWorkspace()
    {
        var other = Path.Combine(_dir, "other");
        Directory.CreateDirectory(other);
        var guard = new WorkspacePathGuard(_live);
        var gate = new SemaphoreSlim(0);

        var first = ThreadingTask.Run(async () =>
        {
            using (MissionWorkspaceScope.Enter(Workspace()))
            {
                gate.Release();                       // let the other mission enter its scope
                await ThreadingTask.Delay(50);
                return guard.EffectiveRoot;
            }
        });

        var second = ThreadingTask.Run(async () =>
        {
            await gate.WaitAsync();
            using (MissionWorkspaceScope.Enter(Workspace() with { Id = "ws2", Root = other }))
            {
                await ThreadingTask.Delay(10);
                return guard.EffectiveRoot;
            }
        });

        Assert.Equal(Path.GetFullPath(_workspace), await first);
        Assert.Equal(Path.GetFullPath(other), await second);
    }
}
