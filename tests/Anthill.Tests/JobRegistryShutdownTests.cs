using Anthill.Api;
using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// SHUTTING DOWN IS NOT AN EXCEPTION PATH. v0.3.8.120.
///
/// `ApiJobRegistry.Dispose()` completed the queue and disposed it in consecutive statements, while
/// its worker threads sat blocked inside `GetConsumingEnumerable()`. Disposing a
/// `BlockingCollection` under a blocked consumer throws `ObjectDisposedException` ON THAT THREAD —
/// and an unhandled exception on a background thread does not get logged, it ends the process.
///
/// CI found it in the least ambiguous way available: every one of 1,652 tests PASSED and the run
/// still failed, because the test host crashed during teardown. The same shape would take down the
/// API host on shutdown, which is what this guard is actually about — the CI symptom was only the
/// messenger.
///
/// What is asserted is the state the fix must reach, not the absence of a crash (a crash cannot be
/// asserted from inside the process it kills): after Dispose returns, no worker is still in the
/// queue. The guarded take in `WorkerLoop` is what makes that true even for a worker that was
/// blocked at the moment the registry went down.
/// </summary>
public class JobRegistryShutdownTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _mem;
    private readonly Queen _queen;

    public JobRegistryShutdownTests()
    {
        AnthillRuntime.Initialize();
        _dir = Path.Combine(Path.GetTempPath(), "anthill_jobshutdown_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _mem = new SqliteMemory(Path.Combine(_dir, "test.db"));
        _queen = new Queen(_mem);
    }

    [Fact]
    public void DisposingTheRegistry_LetsEveryWorkerLeaveTheQueueFirst()
    {
        var jobs = new ApiJobRegistry(_queen, 2);

        // The workers are blocked on an empty queue — the exact state that used to be fatal.
        Assert.False(jobs.WorkersHaveStopped(), "the workers never started; this test would pass vacuously.");

        jobs.Dispose();

        Assert.True(jobs.WorkersHaveStopped(),
            "a job worker was still inside the queue when Dispose() returned. Disposing the "
          + "collection under a blocked consumer throws on the worker thread, where nothing catches "
          + "it — which is a process kill, not an error.");
    }

    public void Dispose()
    {
        _queen.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }
}
