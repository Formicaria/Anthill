using System.Diagnostics;
using Anthill.SDK.Common;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Qualification scenario 17: a REAL process is killed mid-apply, and the tree comes back.
/// v0.3.8.79 (PLAN.md §2 R2). The last of the twenty.
///
/// WHAT WAS PARTIAL, and why the word was honest. `ApplyTransactionTests` has covered recovery since
/// v0.3.8.62, and its crash case works by abandoning the transaction object without committing. That
/// proves recovery reads an incomplete journal and restores from it — genuinely most of the value,
/// and the ledger said so. What it cannot prove is the thing the scenario names: a process that
/// DIES. An abandoned object is still a healthy process with flushed buffers, run finally blocks and
/// a filesystem that got everything it was told. A killed one has none of that.
///
/// So this starts `Anthill.CrashHelper` — a separate executable — waits for it to signal that the
/// journal and the patched bytes are durable, `Kill()`s it, and then runs recovery in this process.
/// Nothing about that is simulated: it is a real OS kill of a real process holding a real open
/// transaction.
///
/// WHY THE SENTINEL. Killing on start would race the writes: sometimes the journal would not exist,
/// and "recovered cleanly from nothing" is a pass that means nothing happened — the shape of a
/// vacuous test, which this repository has now caught in four separate guards. The helper writes the
/// sentinel LAST, after its mutations, so the kill lands on a state that is durable rather than
/// merely intended.
///
/// WHAT THIS STILL DOES NOT COVER, stated because scenario 17's title is broader than this test.
/// "Restart during approval/apply/finalization" has three phases; approval and finalization
/// idempotency are covered by `ApprovalDedupeTests` and `FinalizationOrderTests`, which the ledger
/// already cites. This closes the apply phase — the one that touches the operator's bytes, and the
/// one no test could reach without killing something.
/// </summary>
[Collection("specialist-gates")]   // starts a child process and owns a temp tree
public class ProcessDeathMidApplyTests : IDisposable
{
    private readonly string _root;

    public ProcessDeathMidApplyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "anthill-kill-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string HelperPath()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Anthill.CrashHelper.dll");
        Assert.True(File.Exists(dll),
            $"the crash helper is not beside the tests ({dll}). It is a ProjectReference from "
          + "Anthill.Tests, so this means the reference was dropped or its output stopped being "
          + "copied — and a scenario whose helper is missing does not fail loudly, it stops being "
          + "run, which is the state scenario 17 was already in.");
        return dll;
    }

    private string Seed(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// THE SCENARIO. A process dies holding an open transaction; recovery restores the tree
    /// byte-identically and leaves no durable failure behind.
    /// </summary>
    [Fact]
    public void AProcessKilledMidApply_LeavesATreeThatRecoversByteIdentically()
    {
        var a = Seed("a.txt", "original-a");
        var b = Seed("b.txt", "original-b");
        var sentinel = Path.Combine(_root, "reached-mid-apply.marker");

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
        start.ArgumentList.Add(_root);
        start.ArgumentList.Add(sentinel);
        start.ArgumentList.Add("a.txt=patched-a");
        start.ArgumentList.Add("b.txt=patched-b");

        using var child = Process.Start(start)!;
        try
        {
            // Wait for DURABILITY, not for time. A fixed sleep would be flaky on a loaded machine
            // and, worse, would sometimes pass by killing before anything was written.
            // THE STREAMS ARE READ ONLY AFTER THE CHILD IS GONE, and the first draft of this loop
            // hung the whole suite by getting that wrong.
            //
            // It called `Assert.False(child.HasExited, "…" + child.StandardOutput.ReadToEnd())`.
            // C# evaluates the message argument BEFORE calling the assert, and `ReadToEnd` blocks
            // until the stream closes — which, for a child deliberately blocked forever, is never.
            // So the first iteration waited for output from a process designed not to produce any,
            // xUnit applies no per-test timeout, and the run simply never finished. A diagnostic
            // that hangs the thing it is diagnosing, which is this repository's own defect class
            // arriving in the test written to close its last scenario.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(sentinel) && DateTime.UtcNow < deadline)
            {
                if (child.HasExited)
                    Assert.Fail(
                        "the crash helper exited instead of blocking mid-apply. It must never "
                      + "return, commit or roll back — a tidy end is the one thing this test cannot "
                      + $"use.\n  exit code: {child.ExitCode}"
                      + "\n  stdout: " + child.StandardOutput.ReadToEnd()
                      + "\n  stderr: " + child.StandardError.ReadToEnd());

                Thread.Sleep(50);
            }

            Assert.True(File.Exists(sentinel),
                "the crash helper never reached a durable mid-apply state within 30s.");

            // The patched bytes ARE on disk and the transaction is open — asserted before the kill,
            // so a later restoration cannot be an artefact of nothing having happened.
            Assert.Equal("patched-a", File.ReadAllText(a));
            Assert.Equal("patched-b", File.ReadAllText(b));
            Assert.True(Directory.Exists(Path.Combine(_root, ApplyTransaction.JournalDirectoryName)),
                "no journal on disk, so the kill would be testing recovery against nothing.");

            child.Kill(entireProcessTree: true);
            child.WaitForExit(30_000);
        }
        catch
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        // ---- restart: this process is the one that comes back up ----
        var results = ApplyTransaction.Recover(_root);

        Assert.Single(results);
        Assert.Contains("rolled back cleanly", results[0]);
        Assert.Equal("original-a", File.ReadAllText(a));
        Assert.Equal("original-b", File.ReadAllText(b));
        Assert.False(ApplyTransaction.HasRollbackFailure(_root),
            "recovery left a durable rollback failure, so the tree is not safe to work in.");
    }

    /// <summary>
    /// AND RECOVERY IS IDEMPOTENT. A second restart finds nothing to do rather than rolling the
    /// same journal back twice — "nothing applied, approved or finalized twice" is scenario 17's
    /// own wording, and a recovery that re-ran would restore stale content over newer work.
    /// </summary>
    [Fact]
    public void RecoveringTwice_IsNotRecoveringTwice()
    {
        var a = Seed("a.txt", "original-a");
        var sentinel = Path.Combine(_root, "reached.marker");

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
        start.ArgumentList.Add(_root);
        start.ArgumentList.Add(sentinel);
        start.ArgumentList.Add("a.txt=patched-a");

        using var child = Process.Start(start)!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(sentinel) && DateTime.UtcNow < deadline && !child.HasExited) Thread.Sleep(50);
        Assert.True(File.Exists(sentinel), "the crash helper never reached a durable mid-apply state.");

        child.Kill(entireProcessTree: true);
        child.WaitForExit(30_000);

        Assert.Single(ApplyTransaction.Recover(_root));
        Assert.Equal("original-a", File.ReadAllText(a));

        // The operator then does real work in the restored tree…
        File.WriteAllText(a, "work-done-after-recovery");

        // …and a second restart must not undo it.
        Assert.Empty(ApplyTransaction.Recover(_root));
        Assert.Equal("work-done-after-recovery", File.ReadAllText(a));
    }
}
