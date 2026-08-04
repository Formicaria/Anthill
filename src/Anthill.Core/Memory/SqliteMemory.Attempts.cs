using Anthill.Core.Common;
using Anthill.Core.Workers;

namespace Anthill.Core.Memory;

/// <summary>
/// v3.8.0 — workers, attempts and the atomic claim.
///
/// THE CLAIM IS THE POINT, and it is the one thing here that cannot be done in application code.
/// "Two workers cannot claim the same non-parallel task" is unachievable by reading a row, checking
/// it, and writing it back: between the read and the write, another worker does the same thing and
/// both see an unclaimed task. No amount of care fixes that, because the flaw is the gap, not the
/// carelessness.
///
/// So the claim is a single conditional UPDATE whose WHERE clause carries the precondition, and the
/// answer is the number of rows it changed. SQLite serialises writers, so exactly one caller can see
/// a row count of 1 — the database enforces the invariant rather than the code hoping to.
/// </summary>
public sealed partial class SqliteMemory
{
    // ---- workers ------------------------------------------------------------------------------

    public void SaveWorker(WorkerRegistration worker)
    {
        if (worker is null || string.IsNullOrWhiteSpace(worker.Id)) return;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO workers (id, roles_json, kind, max_concurrent, last_heartbeat, registered_at)
                  VALUES (@id, @roles, @kind, @max, @beat, @at)
                  ON CONFLICT(id) DO UPDATE SET
                    roles_json=@roles, kind=@kind, max_concurrent=@max, last_heartbeat=@beat,
                    -- Re-registration means a NEW process wearing the same identity, so this moves
                    -- with it. Left at the first value it described a process that no longer exists,
                    -- which is the opposite of useful when the question is 'how long has the worker
                    -- holding this lease been up' — the question asked while diagnosing a crash.
                    registered_at=@at",
                ("@id", worker.Id), ("@roles", Json.SafeDumps(worker.Roles)),
                ("@kind", worker.Kind), ("@max", worker.MaxConcurrent),
                ("@beat", (object?)worker.LastHeartbeat?.ToIso() ?? DBNull.Value),
                ("@at", worker.RegisteredAt.ToIso()));
        }
    }

    /// <summary>Record that a worker is still alive. The cheapest write in the system, so it is its own statement.</summary>
    public void Heartbeat(string workerId, DateTime at)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, "UPDATE workers SET last_heartbeat=@beat WHERE id=@id",
                ("@beat", at.ToIso()), ("@id", workerId ?? ""));
        }
    }

    public IReadOnlyList<WorkerRegistration> LoadWorkers() =>
        Query("SELECT * FROM workers ORDER BY id").Select(row => new WorkerRegistration
        {
            Id = row.GetValueOrDefault("id")?.ToString() ?? "",
            Roles = Json.SafeLoadList(row.GetValueOrDefault("roles_json")?.ToString()),
            Kind = row.GetValueOrDefault("kind")?.ToString() ?? "local",
            MaxConcurrent = Convert.ToInt32(row.GetValueOrDefault("max_concurrent") ?? 1),
            LastHeartbeat = AnthillTime.ParseIsoOrNull(row.GetValueOrDefault("last_heartbeat")?.ToString()),
            RegisteredAt = AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("registered_at")?.ToString()),
        }).ToList();

    // ---- attempts -----------------------------------------------------------------------------

    /// <summary>
    /// Claim a task, atomically, and record the attempt.
    ///
    /// Returns null when someone else already holds it. That is a NORMAL outcome under concurrency,
    /// not an error — a scheduler racing three workers at one task expects two of them to be told no,
    /// and treating that as a fault would make ordinary operation look like a problem.
    ///
    /// The precondition lives in the WHERE clause: no live attempt for this task. "Live" means
    /// running with an unexpired lease, so a worker that died does not hold a task forever.
    /// </summary>
    public TaskAttempt? TryClaimTask(string taskId, string missionId, string workerId, TimeSpan lease)
    {
        var now = AnthillTime.NowUtc();

        lock (_writeLock)
        {
            using var conn = Connect();
            using var tx = conn.BeginTransaction();

            // The guard and the insert are ONE transaction. Checking outside it would reintroduce
            // exactly the read-then-write gap this method exists to close.
            using (var guard = conn.CreateCommand())
            {
                guard.Transaction = tx;
                guard.CommandText =
                    @"SELECT COUNT(*) FROM task_attempts
                      WHERE task_id=@t AND state='Running' AND (lease_until IS NULL OR lease_until > @now)";
                guard.Parameters.AddWithValue("@t", taskId ?? "");
                guard.Parameters.AddWithValue("@now", now.ToIso());
                if (Convert.ToInt64(guard.ExecuteScalar() ?? 0L) > 0) return null;
            }

            int number;
            using (var count = conn.CreateCommand())
            {
                count.Transaction = tx;
                count.CommandText = "SELECT COALESCE(MAX(number), 0) + 1 FROM task_attempts WHERE task_id=@t";
                count.Parameters.AddWithValue("@t", taskId ?? "");
                number = Convert.ToInt32(count.ExecuteScalar() ?? 1);
            }

            var attempt = new TaskAttempt
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                TaskId = taskId ?? "", MissionId = missionId ?? "", WorkerId = workerId ?? "",
                Number = number, State = AttemptState.Running,
                LeaseUntil = now.Add(lease), StartedAt = now,
            };

            NonQuery(conn, tx,
                @"INSERT INTO task_attempts
                    (id, task_id, mission_id, number, worker_id, state, provider, model,
                     may_have_side_effects, failure_class, failure_reason, lease_until, started_at, finished_at)
                  VALUES (@id, @t, @m, @n, @w, 'Running', NULL, NULL, 0, NULL, NULL, @lease, @start, NULL)",
                ("@id", attempt.Id), ("@t", attempt.TaskId), ("@m", attempt.MissionId),
                ("@n", attempt.Number), ("@w", attempt.WorkerId),
                ("@lease", attempt.LeaseUntil!.Value.ToIso()), ("@start", attempt.StartedAt.ToIso()));

            tx.Commit();
            return attempt;
        }
    }

    /// <summary>Extend a live attempt's lease. Silently does nothing once the attempt is terminal.</summary>
    public void RenewLease(string attemptId, TimeSpan lease)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                "UPDATE task_attempts SET lease_until=@until WHERE id=@id AND state='Running'",
                ("@until", AnthillTime.NowUtc().Add(lease).ToIso()), ("@id", attemptId ?? ""));
        }
    }

    /// <summary>
    /// Mark that this attempt has begun something with effects outside the process.
    ///
    /// Called BEFORE the side effect, never after — an attempt that dies mid-write is the entire
    /// reason the flag exists, and it cannot record anything once it is dead.
    /// </summary>
    public void MarkAttemptSideEffecting(string attemptId)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                "UPDATE task_attempts SET may_have_side_effects=1 WHERE id=@id", ("@id", attemptId ?? ""));
        }
    }

    /// <summary>Finish an attempt, with the route that served it and why it ended.</summary>
    public void FinishAttempt(string attemptId, AttemptState state,
        string? provider = null, string? model = null,
        string? failureClass = null, string? failureReason = null)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"UPDATE task_attempts
                     SET state=@state, provider=@p, model=@m,
                         failure_class=@fc, failure_reason=@fr,
                         lease_until=NULL, finished_at=@at
                   WHERE id=@id AND state='Running'",
                ("@state", state.ToString()),
                ("@p", (object?)provider ?? DBNull.Value), ("@m", (object?)model ?? DBNull.Value),
                ("@fc", (object?)failureClass ?? DBNull.Value),
                ("@fr", (object?)failureReason ?? DBNull.Value),
                ("@at", AnthillTime.NowUtc().ToIso()), ("@id", attemptId ?? ""));
        }
    }

    /// <summary>
    /// Mark every attempt whose lease has lapsed as abandoned, and report them.
    ///
    /// ABANDONED, not failed. Nobody observed a failure — the attempt may have succeeded and died
    /// before saying so, which is exactly why its side effects cannot be assumed absent. Calling it
    /// "failed" would invite a retry that duplicates work already done.
    /// </summary>
    public IReadOnlyList<TaskAttempt> ReclaimExpiredAttempts()
    {
        var now = AnthillTime.NowUtc();
        var expired = Query(
            @"SELECT * FROM task_attempts
              WHERE state='Running' AND lease_until IS NOT NULL AND lease_until <= @now",
            ("@now", now.ToIso())).Select(ReadAttempt).ToList();

        if (expired.Count == 0) return expired;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"UPDATE task_attempts SET state='Abandoned', finished_at=@at, lease_until=NULL
                  WHERE state='Running' AND lease_until IS NOT NULL AND lease_until <= @now",
                ("@at", now.ToIso()), ("@now", now.ToIso()));
        }

        return expired.Select(a => a with { State = AttemptState.Abandoned, FinishedAt = now }).ToList();
    }

    /// <summary>
    /// Reclaim attempts still held by THIS worker id, regardless of lease.
    ///
    /// Called at startup, and it closes a gap the expiry sweep structurally cannot. A process that
    /// crashes leaves its attempts Running with most of the lease still on the clock — thirty
    /// minutes, in this build — so <see cref="ReclaimExpiredAttempts"/> finds nothing at restart and
    /// the task stays stranded for the remainder of a lease held by a process that no longer exists.
    /// The gate says "no accepted task is silently lost after crash or restart"; waiting out a lease
    /// is losing it temporarily, which for an operator watching a stalled mission is losing it.
    ///
    /// Unconditional is SOUND here, and only here: if this process is starting up wearing this id,
    /// then any attempt still marked Running under that id belongs to a previous incarnation that is
    /// definitively gone. No other worker may make that inference about anyone else, which is why
    /// this takes the id rather than sweeping everything Running.
    ///
    /// Abandoned rather than Failed, as always — and side effects are still not assumed absent, so a
    /// reclaimed attempt that had touched something still waits for a person.
    /// </summary>
    public IReadOnlyList<TaskAttempt> ReclaimOwnAttempts(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId)) return Array.Empty<TaskAttempt>();

        var now = AnthillTime.NowUtc();
        var orphaned = Query(
            "SELECT * FROM task_attempts WHERE state='Running' AND worker_id=@w",
            ("@w", workerId)).Select(ReadAttempt).ToList();

        if (orphaned.Count == 0) return orphaned;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"UPDATE task_attempts SET state='Abandoned', finished_at=@at, lease_until=NULL
                  WHERE state='Running' AND worker_id=@w",
                ("@at", now.ToIso()), ("@w", workerId));
        }

        return orphaned.Select(a => a with { State = AttemptState.Abandoned, FinishedAt = now }).ToList();
    }

    public IReadOnlyList<TaskAttempt> LoadAttempts(string taskId) =>
        Query("SELECT * FROM task_attempts WHERE task_id=@t ORDER BY number", ("@t", taskId ?? ""))
            .Select(ReadAttempt).ToList();

    /// <summary>
    /// The attempts an operator has to look at personally.
    ///
    /// Abandoned AND possibly side-effecting: work whose ending nobody observed, which may already
    /// have changed something outside the process. No automatic policy can resolve these — that is
    /// the whole reason they are a category rather than a retry queue — so they are surfaced as a
    /// standing question instead of being quietly retried or quietly dropped.
    ///
    /// Ordered oldest first, deliberately. The longest-unanswered one is the one most likely to have
    /// been forgotten, and a newest-first list buries it exactly as it becomes most important.
    /// </summary>
    public IReadOnlyList<TaskAttempt> LoadAttemptsNeedingReview(int limit = 50) =>
        Query(@"SELECT * FROM task_attempts
                 WHERE state='Abandoned' AND may_have_side_effects=1
                 ORDER BY started_at ASC LIMIT @n", ("@n", Math.Max(1, limit)))
            .Select(ReadAttempt).ToList();

    /// <summary>Most recent attempts across every mission — the console's "what has been tried lately".</summary>
    public IReadOnlyList<TaskAttempt> LoadRecentAttempts(int limit = 30) =>
        Query("SELECT * FROM task_attempts ORDER BY started_at DESC LIMIT @n", ("@n", Math.Max(1, limit)))
            .Select(ReadAttempt).ToList();

    public IReadOnlyList<TaskAttempt> LoadMissionAttempts(string missionId) =>
        Query("SELECT * FROM task_attempts WHERE mission_id=@m ORDER BY started_at", ("@m", missionId ?? ""))
            .Select(ReadAttempt).ToList();

    private static TaskAttempt ReadAttempt(Dictionary<string, object?> row) => new()
    {
        Id = row.GetValueOrDefault("id")?.ToString() ?? "",
        TaskId = row.GetValueOrDefault("task_id")?.ToString() ?? "",
        MissionId = row.GetValueOrDefault("mission_id")?.ToString() ?? "",
        Number = Convert.ToInt32(row.GetValueOrDefault("number") ?? 1),
        WorkerId = row.GetValueOrDefault("worker_id")?.ToString() ?? "",
        // An unreadable state reads as Abandoned rather than Succeeded or Running. Fail closed:
        // "we do not know how this ended" is much closer to abandoned than to done.
        State = Enum.TryParse<AttemptState>(row.GetValueOrDefault("state")?.ToString(), out var s)
            ? s : AttemptState.Abandoned,
        Provider = row.GetValueOrDefault("provider")?.ToString(),
        Model = row.GetValueOrDefault("model")?.ToString(),
        MayHaveSideEffects = Convert.ToInt64(row.GetValueOrDefault("may_have_side_effects") ?? 0L) != 0,
        FailureClass = row.GetValueOrDefault("failure_class")?.ToString(),
        FailureReason = row.GetValueOrDefault("failure_reason")?.ToString(),
        LeaseUntil = AnthillTime.ParseIsoOrNull(row.GetValueOrDefault("lease_until")?.ToString()),
        StartedAt = AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("started_at")?.ToString()),
        FinishedAt = AnthillTime.ParseIsoOrNull(row.GetValueOrDefault("finished_at")?.ToString()),
    };
}
