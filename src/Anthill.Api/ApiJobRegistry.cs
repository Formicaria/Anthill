using System.Collections.Concurrent;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Orchestration;

namespace Anthill.Api;

public sealed class ApiMissionJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Goal { get; init; } = "";
    public string Status { get; set; } = "queued"; // queued | running | complete | failed | cancelled
    /// <summary>Set by Cancel/CancelAll; a queued job is skipped by the worker instead of running.</summary>
    public volatile bool Cancelled;
    /// <summary>Cancels a *running* mission mid-flight — its token is handed to <see cref="Queen.RunMission"/>,
    /// which aborts any in-flight model call and stops the scheduler. No CancelAfter timer is attached
    /// here (the mission's own linked source owns the deadline), so this source never needs disposal.</summary>
    public CancellationTokenSource Cts { get; } = new();
    public string? MissionId { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    /// <summary>v2.7.0: plain-English "why it ended" (completed / timed_out / cancelled / partial / failed) + a short reason.</summary>
    public string? Outcome { get; set; }
    public string? Reason { get; set; }
    /// <summary>v2.26.0: the CANONICAL outcome code from the persisted mission evaluation — the
    /// job status is mapped from this, so status can never contradict the mission's outcome.</summary>
    public string? OutcomeCode { get; set; }
    public DateTime CreatedAt { get; init; } = AnthillTime.NowUtc();
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public Dictionary<string, object?> ToDict() => new()
    {
        ["id"] = Id, ["goal"] = Goal, ["status"] = Status, ["mission_id"] = MissionId,
        ["result"] = Result, ["error"] = Error, ["outcome"] = Outcome, ["reason"] = Reason,
        ["outcome_code"] = OutcomeCode,
        ["created_at"] = CreatedAt.ToIso(),
        ["started_at"] = StartedAt.ToIsoOrNull(), ["finished_at"] = FinishedAt.ToIsoOrNull(),
    };
}

/// <summary>
/// Bounded, in-process mission job runner. Missions submitted through the API are queued
/// and executed by a small worker pool (one worker by default, matching the Python build's
/// single-writer posture) so the HTTP request returns immediately while the colony works.
/// History is trimmed to a fixed cap to keep memory bounded.
/// </summary>
public sealed class ApiJobRegistry : IDisposable
{
    private readonly Queen _queen;
    private readonly BlockingCollection<ApiMissionJob> _queue = new();
    private readonly ConcurrentDictionary<string, ApiMissionJob> _jobs = new();
    private readonly ConcurrentQueue<string> _order = new();
    private readonly List<Thread> _workers = new();
    private readonly object _trimLock = new();

    private readonly Anthill.Core.Memory.SqliteMemory _mem;
    private const int LeaseSeconds = 90; // heartbeat renews at LeaseSeconds/3

    public ApiJobRegistry(Queen queen, int workers)
    {
        _queen = queen;
        _mem = queen.Memory;

        // v2.8.0 startup reconciliation: classify what the last process left behind, then re-queue
        // everything recoverable. The durable table is the source of truth — an accepted mission
        // cannot disappear because the process died.
        var (resumable, retried, orphaned, cancelled) = _mem.ReconcileJobsAtStartup();
        foreach (var row in _mem.ListMissionJobs(500).Where(r => r.Status == "queued").OrderBy(r => r.CreatedAt))
        {
            var job = new ApiMissionJob { Id = row.Id, Goal = row.Goal };
            _jobs[job.Id] = job;
            _order.Enqueue(job.Id);
            _queue.Add(job);
        }
        if (resumable + retried + orphaned + cancelled > 0)
            _mem.RecordJobAttempt("startup-reconcile", 0, Environment.MachineName,
                $"recovery: {resumable} resumable, {retried} retried, {orphaned} orphaned, {cancelled} cancelled", null, 0,
                AnthillTime.NowUtc().ToIso(), AnthillTime.NowUtc().ToIso());

        for (var i = 0; i < Math.Max(1, workers); i++)
        {
            var worker = new Thread(WorkerLoop) { IsBackground = true, Name = $"anthill-job-worker-{i}" };
            worker.Start();
            _workers.Add(worker);
        }
    }

    public ApiMissionJob Submit(string goal, string? idempotencyKey = null)
    {
        // v2.8.0: persist FIRST (durability), with idempotent replay — the same key never creates
        // a duplicate mission, it returns the original job.
        var (row, replayed) = _mem.PersistNewJob(Guid.NewGuid().ToString(), goal, idempotencyKey);
        if (replayed)
        {
            if (_jobs.TryGetValue(row.Id, out var known)) return known;
            var ghost = new ApiMissionJob { Id = row.Id, Goal = row.Goal };
            ghost.Status = row.Status; ghost.MissionId = row.MissionId; ghost.Result = row.Result;
            return ghost; // terminal or owned elsewhere — never re-queued
        }
        var job = new ApiMissionJob { Id = row.Id, Goal = goal };
        _jobs[job.Id] = job;
        _order.Enqueue(job.Id);
        TrimLocked();
        _queue.Add(job);
        return job;
    }

    private void WorkerLoop()
    {
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            // Skip work cancelled while it sat in the queue. A running mission is now interruptible
            // too: its Cts token is handed to RunMission, which aborts any in-flight model call and
            // stops the scheduler — so a hung/slow mission no longer pins the single-writer queue.
            if (job.Cancelled)
            {
                job.Status = "cancelled";
                job.FinishedAt = AnthillTime.NowUtc();
                _mem.UpdateJobState(job.Id, "cancelled", reason: "cancelled while queued", finished: true);
                continue;
            }
            // v2.8.0 atomic claim + lease: only the claim winner runs (two Directors on one DB
            // cannot double-launch), and a heartbeat renews the lease while the mission works.
            var workerName = Thread.CurrentThread.Name ?? "worker";
            if (_mem.TryClaimJob(job.Id, workerName, LeaseSeconds) is null)
                continue; // claimed elsewhere, cancelled, or already terminal — never run it twice
            using var heartbeat = new Timer(_ => _mem.HeartbeatJob(job.Id, workerName, LeaseSeconds),
                null, TimeSpan.FromSeconds(LeaseSeconds / 3.0), TimeSpan.FromSeconds(LeaseSeconds / 3.0));
            job.Status = "running";
            job.StartedAt = AnthillTime.NowUtc();
            try
            {
                // The callback stamps the mission id the moment the row exists — both so the id is
                // visible while the mission is still running and so concurrent workers (Phase 3)
                // never read another mission's id off the shared Queen.LastMissionId.
                job.Result = _queen.RunMission(job.Goal,
                    missionId => { job.MissionId = missionId; _mem.UpdateJobState(job.Id, "running", missionId: missionId); },
                    job.Cts.Token,
                    outcome => { job.Outcome = outcome.Outcome; job.Reason = outcome.Reason; job.OutcomeCode = outcome.OutcomeCode; });
                // v2.26.0 pre-V3 hardening: the job status MAPS FROM the canonical mission
                // outcome — "RunMission returned" is not "complete". Before this, a timed-out
                // mission produced the contradiction status=complete / outcome=timed_out, and
                // failed missions wore "complete" too.
                job.Status = job.Cancelled ? "cancelled" : StatusFromOutcome(job.Outcome, job.OutcomeCode);
            }
            catch (OperationCanceledException)
            {
                job.Status = "cancelled";
            }
            catch (Exception error)
            {
                job.Error = error.Message;
                job.Status = "failed";
            }
            finally
            {
                job.FinishedAt = AnthillTime.NowUtc();
                // v2.8.0 write-through: the durable row always reflects the final state + attempt.
                var row = _mem.GetMissionJob(job.Id);
                _mem.UpdateJobState(job.Id, job.Status, missionId: job.MissionId, result: job.Result,
                    error: job.Error, outcome: job.Outcome, reason: job.Reason, finished: true);
                _mem.RecordJobAttempt(job.Id, row?.Attempt ?? 1, workerName, "run:" + job.Status, job.Error,
                    (long)((job.FinishedAt - job.StartedAt)?.TotalMilliseconds ?? 0),
                    job.StartedAt.ToIsoOrNull(), job.FinishedAt.ToIsoOrNull());
            }
        }
    }

    /// <summary>
    /// v2.26.0: typed job status from the canonical outcome. The keyed vocabulary is unchanged
    /// (queued | running | complete | failed | cancelled) plus timed_out — an addition, not a
    /// redefinition, so existing consumers keep working while the contradiction dies.
    /// A mission with no recorded outcome maps to failed: an unexplained end is not a success.
    /// </summary>
    internal static string StatusFromOutcome(string? outcome, string? outcomeCode)
    {
        // Prefer the canonical evaluation code when the callback delivered one.
        var key = string.IsNullOrWhiteSpace(outcomeCode) ? outcome : outcomeCode;
        return key switch
        {
            "completed" or "completed_verified" or "completed_unverified" => "complete",
            "partial" => "complete",   // structurally finished; the outcome field carries the nuance
            "timed_out" => "timed_out",
            "cancelled" => "cancelled",
            // v3.8.34: escalation is NOT failure, and collapsing it here contradicted the closed
            // vocabulary that defines it: "Distinct from failed — nothing broke; the runtime
            // declined to continue without judgment" (MissionOutcome.Escalated).
            //
            // The cost was visible on the dashboard. An adaptive stop produces
            // outcome="completed" + code="escalated", so the operator saw a FAILED badge directly
            // above this job's own sentence, "Completed — 5/5 tasks succeeded." Three of twenty
            // persisted rows in the live database are in exactly that state.
            //
            // The theory over this method asserted ("escalated", null) => "failed" — an input
            // production never produces, since the code always arrives alongside outcome
            // "completed". The real pairing was untested, which is why an explicit mapping could
            // disagree with the vocabulary for two releases.
            "escalated" => "escalated",
            "failed" or "failed_permanent" or "failed_retryable" => "failed",
            _ => "failed",
        };
    }

    // v2.8.0: reads come from the durable table (survives restart); live in-memory jobs overlay
    // their current status so nothing appears stale mid-run.
    public List<Dictionary<string, object?>> ListJobs(int limit = 50) =>
        _mem.ListMissionJobs(limit).Select(row =>
        {
            if (_jobs.TryGetValue(row.Id, out var live)) return live.ToDict();
            return new Dictionary<string, object?>
            {
                ["id"] = row.Id, ["goal"] = row.Goal, ["status"] = row.Status, ["mission_id"] = row.MissionId,
                ["result"] = row.Result, ["error"] = row.Error, ["outcome"] = row.Outcome, ["reason"] = row.Reason,
                ["created_at"] = row.CreatedAt, ["started_at"] = row.StartedAt, ["finished_at"] = row.FinishedAt,
                ["attempt"] = row.Attempt,
            };
        }).ToList();

    public ApiMissionJob? GetJob(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>Requests cancellation of one job. Queued work is dropped before it runs; a running
    /// mission is signalled to stop mid-flight (its next model call / task boundary aborts). Returns
    /// true if the job exists and wasn't already terminal.</summary>
    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return false;
        if (IsTerminalStatus(job.Status)) return false;
        job.Cancelled = true;
        _mem.UpdateJobState(id, job.Status, cancelRequested: true); // durable: survives restart mid-cancel
        SignalCancel(job);
        if (job.Status == "queued")
        {
            job.Status = "cancelled"; job.FinishedAt = AnthillTime.NowUtc();
            _mem.UpdateJobState(id, "cancelled", reason: "cancelled while queued", finished: true);
        }
        return true;
    }

    /// <summary>
    /// A job that has already ended. One definition, because there were two ad-hoc lists and both
    /// were wrong the same way.
    ///
    /// v3.8.34: the lists read "complete" or "failed" or "cancelled" and omitted <c>timed_out</c>,
    /// which <see cref="StatusFromOutcome"/> has returned since v2.26.0 — so cancelling a job that
    /// had already timed out reported success and signalled a token nobody was holding, and
    /// CancelAll counted it. Adding "escalated" to two separate lists would have repeated the
    /// mistake a third time; the set is the thing that was missing, so it is now named once and
    /// derived from the statuses this class actually assigns.
    /// </summary>
    internal static bool IsTerminalStatus(string? status) =>
        status is "complete" or "failed" or "cancelled" or "timed_out" or "escalated";

    /// <summary>Cancels every non-terminal job. Returns how many were affected.</summary>
    public int CancelAll()
    {
        var n = 0;
        foreach (var job in _jobs.Values)
        {
            if (IsTerminalStatus(job.Status)) continue;
            job.Cancelled = true;
            SignalCancel(job);
            if (job.Status == "queued") { job.Status = "cancelled"; job.FinishedAt = AnthillTime.NowUtc(); }
            n++;
        }
        return n;
    }

    /// <summary>Fires the job's cancellation token. Guarded against the benign race where the mission
    /// finished and the source was already disposed between the status check and here.</summary>
    private static void SignalCancel(ApiMissionJob job)
    {
        try { job.Cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void TrimLocked()
    {
        lock (_trimLock)
        {
            while (_jobs.Count > AnthillRuntime.ApiJobMaxHistory && _order.TryDequeue(out var oldest))
                _jobs.TryRemove(oldest, out _);
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _queue.Dispose();
    }
}
