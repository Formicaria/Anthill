using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The durable mission contract: cancel, idempotency, lookup and history. v0.3.8.38.
///
/// Four defects from an external audit of v0.3.8.36, each re-proved against the tree before being
/// fixed. Three share one shape this repository knows well — the capability existed, was tested, and
/// nothing reached it:
///
/// <list type="bullet">
/// <item><c>Submit(goal, idempotencyKey)</c> and insert-or-replay have worked since v2.8.0, and
///   <c>POST /missions</c> passed no key. The eleventh unreachable capability.</item>
/// <item><c>GetMissionJob(id)</c> existed in the store; <c>GetJob</c> read only memory, so a job
///   listed by <c>/jobs</c> returned not-found from <c>/jobs/{id}</c> after a restart. Twelfth.</item>
/// <item><c>CancelAll</c> persisted nothing while <c>Cancel</c> did, so a crash straight after
///   "cancel all" requeued the work — the one operation whose entire purpose is stopping it.</item>
/// </list>
///
/// The fourth is worse than unreachable: clearing history deleted mission rows with foreign keys off
/// while workers could still be writing, taking the audit trail of a running mission with it.
/// </summary>
public class DurableMissionContractTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_durable_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        return new SqliteMemory(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
    }

    // ---------------------------------------------------------------------------------------
    // Idempotency — reachable at last
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// THE property: one key, one durable job, however many times it arrives. Asserted against the
    /// STORE, because that is where the guarantee lives and where a duplicate would appear.
    /// </summary>
    [Fact]
    public void TheSameIdempotencyKey_CreatesExactlyOneDurableJob()
    {
        using var memory = Memory();

        var (first, firstReplayed) = memory.PersistNewJob(Guid.NewGuid().ToString(), "do the thing", "retry-key-1");
        var (second, secondReplayed) = memory.PersistNewJob(Guid.NewGuid().ToString(), "do the thing", "retry-key-1");

        Assert.False(firstReplayed);
        Assert.True(secondReplayed, "the second submission with the same key must be a replay");
        Assert.Equal(first.Id, second.Id);
        Assert.Single(memory.ListMissionJobs(50).Where(j => j.Goal == "do the thing"));
    }

    /// <summary>Different keys are different missions — or the guard would suppress real work.</summary>
    [Fact]
    public void DifferentKeys_CreateDifferentJobs()
    {
        using var memory = Memory();

        var (a, _) = memory.PersistNewJob(Guid.NewGuid().ToString(), "goal", "key-a");
        var (b, _) = memory.PersistNewJob(Guid.NewGuid().ToString(), "goal", "key-b");

        Assert.NotEqual(a.Id, b.Id);
    }

    /// <summary>No key means no deduplication. Two deliberate submissions of the same wording are
    /// two missions, and collapsing them would lose work the operator asked for twice.</summary>
    [Fact]
    public void WithNoKey_RepeatedSubmissionsAreSeparateMissions()
    {
        using var memory = Memory();

        var (a, _) = memory.PersistNewJob(Guid.NewGuid().ToString(), "same wording", null);
        var (b, replayed) = memory.PersistNewJob(Guid.NewGuid().ToString(), "same wording", null);

        Assert.False(replayed);
        Assert.NotEqual(a.Id, b.Id);
    }

    /// <summary>
    /// The route must actually pass the key. Without this the store's guarantee stays exactly as
    /// unreachable as it was for eleven releases, and every test above still passes.
    /// </summary>
    [Fact]
    public void TheMissionRoute_PassesTheKeyAndValidatesIt()
    {
        var routes = File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Api", "ApiHost.Routes.cs"));

        Assert.Contains("Idempotency-Key", routes, StringComparison.Ordinal);
        Assert.Contains("Jobs.Submit(goal,", routes, StringComparison.Ordinal);
        // Bounded and validated: an unbounded key is an unbounded index entry.
        Assert.Contains("200 characters or fewer", routes, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Cancel-all — durable
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A cancelled queued job is TERMINAL in the database before the call returns. The old
    /// implementation set it in memory only, so a crash here requeued it.
    /// </summary>
    [Fact]
    public void CancellingAQueuedJob_IsTerminalInTheStoreImmediately()
    {
        using var memory = Memory();
        var (row, _) = memory.PersistNewJob(Guid.NewGuid().ToString(), "cancel me", null);

        memory.UpdateJobState(row.Id, "cancelled", reason: "cancelled while queued", finished: true);

        var reloaded = memory.GetMissionJob(row.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("cancelled", reloaded!.Status);
        Assert.False(string.IsNullOrEmpty(reloaded.FinishedAt), "a cancelled job must be finished, not merely flagged");
    }

    /// <summary>
    /// CancelAll routes through the same durable transition as Cancel rather than repeating it.
    /// Two implementations of one rule is exactly how they came to differ.
    /// </summary>
    [Fact]
    public void CancelAll_DelegatesToTheSingleDurableCancel()
    {
        var registry = File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Api", "ApiJobRegistry.cs"));

        var start = registry.IndexOf("public int CancelAll()", StringComparison.Ordinal);
        Assert.True(start >= 0, "CancelAll is no longer recognisable");
        var body = registry[start..Math.Min(registry.Length, start + 700)];

        Assert.Contains("Cancel(id)", body, StringComparison.Ordinal);
        // The old body mutated status inline and never touched the store.
        Assert.DoesNotContain("job.Status = \"cancelled\"", body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Lookup parity — a listed job can be opened
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A durable job survives the process. This is the state after a restart: the row exists, no
    /// in-memory registry knows about it, and the detail lookup must still find it.
    /// </summary>
    [Fact]
    public void ADurableJob_IsFoundByIdWithNoLiveRegistry()
    {
        using var memory = Memory();
        var (row, _) = memory.PersistNewJob(Guid.NewGuid().ToString(), "survives restart", null);
        memory.UpdateJobState(row.Id, "complete", finished: true);

        var found = memory.GetMissionJob(row.Id);

        Assert.NotNull(found);
        Assert.Equal(row.Id, found!.Id);
        Assert.Equal("complete", found.Status);
        Assert.Contains(memory.ListMissionJobs(50), j => j.Id == row.Id);
    }

    /// <summary>
    /// List and detail use ONE projection, and the detail route reads the durable-aware one. The
    /// two shapes disagreed about `outcome_code`, so a job lost its canonical outcome on restart
    /// while every other field looked familiar.
    /// </summary>
    [Fact]
    public void ListAndDetail_ShareOneProjection()
    {
        var registry = File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Api", "ApiJobRegistry.cs"));
        var routes = File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Api", "ApiHost.Routes.cs"));

        Assert.Contains("ListMissionJobs(limit).Select(Project)", registry, StringComparison.Ordinal);
        Assert.Contains("GetJobProjection", registry, StringComparison.Ordinal);
        Assert.Contains("Jobs.GetJobProjection(id)", routes, StringComparison.Ordinal);
        // outcome_code is joined from the canonical evaluation, not copied into the job row.
        Assert.Contains("LoadMissionEvaluation", registry, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Clearing history
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The clear covers the durable job tables. Omitting them left job rows pointing at deleted
    /// missions — and as of this release the detail route projects those rows, so it would describe
    /// work whose record had been erased.
    /// </summary>
    [Fact]
    public void ClearingHistory_AlsoClearsTheDurableJobTables()
    {
        using var memory = Memory();
        var (row, _) = memory.PersistNewJob(Guid.NewGuid().ToString(), "will be cleared", null);
        Assert.NotNull(memory.GetMissionJob(row.Id));

        memory.ClearMissionHistory();

        Assert.Null(memory.GetMissionJob(row.Id));
        Assert.Empty(memory.ListMissionJobs(50));
    }

    /// <summary>
    /// Clearing a database whose lazily-created job tables were never touched must not throw.
    /// `mission_jobs` and `mission_attempts` are created on first use, so naming them in the delete
    /// list is only safe because the clear checks the table exists.
    /// </summary>
    [Fact]
    public void ClearingAFreshDatabase_DoesNotThrow()
    {
        using var memory = Memory();

        var (freed, missions) = memory.ClearMissionHistory();

        Assert.True(freed >= 0);
        Assert.Equal(0, missions);
    }

    /// <summary>
    /// The endpoint refuses while work is in flight, on the SERVER. The console disabled its button;
    /// the endpoint accepted the call from anywhere, and a disabled button is not a gate.
    /// </summary>
    [Fact]
    public void TheClearEndpoint_RefusesWhileWorkIsActive()
    {
        var dashboard = File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Api", "ApiHost.Dashboard.cs"));

        var start = dashboard.IndexOf("/maintenance/clear-missions", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var body = dashboard[start..Math.Min(dashboard.Length, start + 1400)];

        Assert.Contains("ActiveJobIds()", body, StringComparison.Ordinal);
        Assert.Contains("conflict", body, StringComparison.Ordinal);
        // The refusal must come BEFORE the delete.
        Assert.True(body.IndexOf("ActiveJobIds()", StringComparison.Ordinal)
                  < body.IndexOf("ClearMissionHistory()", StringComparison.Ordinal),
            "the active-work check must run before anything is deleted");
    }

    /// <summary>
    /// Active work is read from the DURABLE table too, not just memory. After a restart a lease can
    /// still be held while the in-process registry is empty, and an idle-looking machine is exactly
    /// when someone clears history.
    /// </summary>
    [Fact]
    public void ActiveWorkIsCountedFromTheStore_NotOnlyFromMemory()
    {
        var registry = File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Api", "ApiJobRegistry.cs"));

        var start = registry.IndexOf("public List<string> ActiveJobIds()", StringComparison.Ordinal);
        Assert.True(start >= 0, "ActiveJobIds is missing");
        var body = registry[start..Math.Min(registry.Length, start + 600)];

        Assert.Contains("_mem.ListMissionJobs", body, StringComparison.Ordinal);
        Assert.Contains("IsTerminalStatus", body, StringComparison.Ordinal);
    }
}
