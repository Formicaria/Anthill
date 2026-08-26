using System.Diagnostics;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Verification;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE CRASH-INJECTION MATRIX FOR THE APPLY INTENT JOURNAL. v0.3.8.94 — the R0 residual PLAN.md
/// carried from v0.3.8.91.
///
/// v0.3.8.91 built the journal (Prepared → Mutating → Applied → Recorded) and the reconciler, and
/// proved them with in-process tests — which share scenario 17's original weakness: an abandoned
/// object is still a healthy process with flushed buffers and running finally blocks. This drives
/// each crash window with a REAL kill: `Anthill.CrashHelper`'s intent mode advances the journal to
/// a chosen phase using the live apply sequence verbatim, signals durability, and blocks; this
/// process kills it and then runs `PatchApplyReconciler.Reconcile` — which is exactly the restart
/// the journal exists for, in exactly the process arrangement it will face.
///
/// One row per crash window, and the EXPECTED OUTCOME is the reconciler's contract:
///   prepared            → discarded. Nothing was touched; the patch stays proposed.
///   mutating-unwritten  → discarded. The file still hashes to its pre-apply bytes.
///   mutating-written    → NEEDS OPERATOR, intent left OPEN. Bytes moved with no post-hash on
///                         record; deciding whose they are is the guess the reconciler refuses.
///   applied             → COMPLETED. Bytes and post-hash agree; the database catches up and the
///                         patch is Applied — the case that used to become an unrevertable phantom.
/// </summary>
[Collection("specialist-gates")]   // starts a child process, owns a temp tree, sets a runtime static
public class PatchApplyCrashMatrixTests : IDisposable
{
    private readonly string _root;
    private readonly string _workspace;
    private readonly string _dbPath;
    private readonly string _workspaceRootWas;

    public PatchApplyCrashMatrixTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "anthill-imx-" + Guid.NewGuid().ToString("N")[..10]);
        _workspace = Path.Combine(_root, "ws");
        _dbPath = Path.Combine(_root, "memory.db");
        Directory.CreateDirectory(_workspace);

        // The reconciler resolves intent targets through WorkspacePathGuard against this static —
        // the same way the live startup sweep does. Restored in Dispose.
        _workspaceRootWas = AnthillRuntime.AllowedWorkspaceRoot;
        AnthillRuntime.AllowedWorkspaceRoot = _workspace;
    }

    public void Dispose()
    {
        AnthillRuntime.AllowedWorkspaceRoot = _workspaceRootWas;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string HelperPath()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Anthill.CrashHelper.dll");
        Assert.True(File.Exists(dll), $"the crash helper is not beside the tests ({dll}).");
        return dll;
    }

    private const string Target = "target.txt";
    private const string OldContent = "pre-apply bytes\n";
    private const string NewContent = "post-apply bytes\n";
    private string _missionId = "";

    /// <summary>Kill the helper at <paramref name="phase"/> and reconcile in this process.</summary>
    private (PatchApplyReconciler.Outcome Outcome, SqliteMemory Memory) KillAtPhaseAndReconcile(string phase)
    {
        // Parent creates the schema and the records FIRST, so reconciliation has rows to finish —
        // the live case it mirrors is a patch that existed long before the apply crashed. The
        // mission and task rows are real because patch_sets carries foreign keys to both, exactly
        // as production rows do.
        var memory = new SqliteMemory(_dbPath);
        var mission = new Mission { Goal = "crash matrix seed", Status = MissionStatus.Complete };
        memory.SaveMission(mission);
        var task = new Task { AssignedAnt = "coder", TaskType = "patch_proposal", Title = "seed" };
        memory.SaveTask(mission.Id, task);
        memory.SavePatchSet(new PatchSet
        {
            Id = "crash-set",
            MissionId = mission.Id,
            TaskId = task.Id,
            Summary = "crash matrix seed",
            Proposals =
            {
                new PatchProposal
                {
                    Id = "crash-patch", FilePath = Target,
                    ChangeType = PatchChangeType.Modify,
                    OldContent = OldContent, NewContent = NewContent,
                },
            },
        });
        File.WriteAllText(Path.Combine(_workspace, Target), OldContent);
        _missionId = mission.Id;

        var sentinel = Path.Combine(_root, $"intent-{phase}.marker");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        start.ArgumentList.Add("exec");
        // v0.3.8.94 — the helper resolves its assemblies from the TEST SUITE's dependency closure.
        // The helper now references Anthill.Core (the intent-journal crash mode drives the real
        // SqliteMemory API), and `dotnet exec` against the helper's own deps.json failed to load
        // Core's transitive packages from the test output directory — on every OS, killing both
        // this matrix and scenario 17 in the same run. The tests' deps.json describes exactly the
        // closure sitting in this directory, so the helper runs under it.
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Anthill.Tests.deps.json"));
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Anthill.Tests.runtimeconfig.json"));
        start.ArgumentList.Add(HelperPath());
        start.ArgumentList.Add("intent");
        start.ArgumentList.Add(_dbPath);
        start.ArgumentList.Add(_workspace);
        start.ArgumentList.Add(sentinel);
        start.ArgumentList.Add(phase);
        start.ArgumentList.Add(Target);
        start.ArgumentList.Add(NewContent);
        start.ArgumentList.Add(_missionId);

        using var child = Process.Start(start)!;
        try
        {
            // Wait for DURABILITY, not time — and never read the streams of a live child designed
            // to block forever (the hang ProcessDeathMidApplyTests documents at length).
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(sentinel) && DateTime.UtcNow < deadline)
            {
                if (child.HasExited)
                    Assert.Fail("the crash helper exited instead of blocking at the crash window."
                              + $"\n  exit code: {child.ExitCode}"
                              + "\n  stdout: " + child.StandardOutput.ReadToEnd()
                              + "\n  stderr: " + child.StandardError.ReadToEnd());
                Thread.Sleep(50);
            }
            Assert.True(File.Exists(sentinel),
                $"the crash helper never reached a durable '{phase}' state within 30s.");

            // The journal really holds an open intent at the phase under test — asserted BEFORE
            // the kill, so the reconciliation below cannot be an artefact of nothing existing.
            var open = memory.OpenApplyIntents();
            Assert.Single(open);
            Assert.Equal("crash-patch", open[0].PatchId);

            child.Kill(entireProcessTree: true);
            child.WaitForExit(30_000);
        }
        catch
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        // ---- restart: this process is the one that comes back up ----
        return (PatchApplyReconciler.Reconcile(memory), memory);
    }

    private string PatchStatus(SqliteMemory memory) =>
        memory.GetPatchProposal("crash-patch")?.GetValueOrDefault("status")?.ToString() ?? "(missing)";

    [Fact]
    public void KilledAtPrepared_TheIntentIsDiscarded_AndNothingChanged()
    {
        var (outcome, memory) = KillAtPhaseAndReconcile("prepared");

        Assert.Equal(1, outcome.Discarded);
        Assert.Equal(0, outcome.Completed);
        Assert.Equal(0, outcome.NeedsOperator);
        Assert.Empty(memory.OpenApplyIntents());
        Assert.Equal(OldContent, File.ReadAllText(Path.Combine(_workspace, Target)));
        Assert.Equal("proposed", PatchStatus(memory));
    }

    [Fact]
    public void KilledMutating_BeforeTheWrite_TheHashDecidesDiscard()
    {
        var (outcome, memory) = KillAtPhaseAndReconcile("mutating-unwritten");

        Assert.Equal(1, outcome.Discarded);
        Assert.Equal(0, outcome.NeedsOperator);
        Assert.Empty(memory.OpenApplyIntents());
        Assert.Equal(OldContent, File.ReadAllText(Path.Combine(_workspace, Target)));
        Assert.Equal("proposed", PatchStatus(memory));
    }

    [Fact]
    public void KilledMutating_AfterBytesMoved_IsLeftForAnOperator_IntentOpen()
    {
        var (outcome, memory) = KillAtPhaseAndReconcile("mutating-written");

        Assert.Equal(1, outcome.NeedsOperator);
        Assert.Equal(0, outcome.Completed);
        Assert.Equal(0, outcome.Discarded);
        // The intent stays OPEN — the next restart re-presents it until a person decides. Closing
        // it would convert "this process will not guess" into "nobody will ever be asked".
        Assert.Single(memory.OpenApplyIntents());
        // The moved bytes are LEFT ALONE: the reconciler never re-runs an apply and never rolls
        // one back.
        Assert.Equal(NewContent, File.ReadAllText(Path.Combine(_workspace, Target)));
        Assert.Equal("proposed", PatchStatus(memory));
    }

    [Fact]
    public void KilledAtApplied_TheDatabaseCatchesUpWithTheDisk()
    {
        var (outcome, memory) = KillAtPhaseAndReconcile("applied");

        Assert.Equal(1, outcome.Completed);
        Assert.Equal(0, outcome.NeedsOperator);
        Assert.Empty(memory.OpenApplyIntents());
        Assert.Equal(NewContent, File.ReadAllText(Path.Combine(_workspace, Target)));
        // The phantom this journal was built for: the write landed, and before v0.3.8.91 the
        // record said it never happened — leaving the patch re-appliable and unrevertable at once.
        Assert.Equal("applied", PatchStatus(memory));
    }
}
