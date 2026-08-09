using Microsoft.Data.Sqlite;
using Anthill.Core.Common;

namespace Anthill.Core.Memory;

/// <summary>
/// v2.8.0 — Durable Mission Runtime (NORTH_STAR V3-track Phase 1). The persistent mission queue:
/// every job the API accepts lands here BEFORE it is queued in memory, every state transition is
/// written through, and startup reconciliation classifies whatever a crash left behind. The
/// in-memory registry remains the dispatcher, but this table is the source of operational truth —
/// no accepted mission can disappear with the process.
/// </summary>
public sealed partial class SqliteMemory
{
    private bool _jobTablesReady;
    private void EnsureJobTables()
    {
        if (_jobTablesReady) return;
        lock (_writeLock)
        {
            if (_jobTablesReady) return;
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS mission_jobs (
                    id TEXT PRIMARY KEY, goal TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'queued',
                    attempt INTEGER NOT NULL DEFAULT 1, idempotency_key TEXT,
                    assigned_worker TEXT, claim_at TEXT, lease_expires_at TEXT, heartbeat_at TEXT,
                    cancel_requested INTEGER NOT NULL DEFAULT 0,
                    mission_id TEXT, result TEXT, error TEXT, outcome TEXT, reason TEXT,
                    created_at TEXT NOT NULL, started_at TEXT, finished_at TEXT);
                CREATE UNIQUE INDEX IF NOT EXISTS ix_mission_jobs_idem
                    ON mission_jobs(idempotency_key) WHERE idempotency_key IS NOT NULL;
                CREATE TABLE IF NOT EXISTS mission_attempts (
                    id TEXT PRIMARY KEY, job_id TEXT NOT NULL, attempt INTEGER NOT NULL,
                    worker TEXT, reason TEXT, error TEXT, duration_ms INTEGER,
                    started_at TEXT, finished_at TEXT);";
            cmd.ExecuteNonQuery();
            _jobTablesReady = true;
        }
    }

    public sealed class MissionJobRow
    {
        public string Id = ""; public string Goal = ""; public string Status = "queued";
        public int Attempt = 1; public string? IdempotencyKey; public string? AssignedWorker;
        public string? LeaseExpiresAt; public bool CancelRequested;
        public string? MissionId; public string? Result; public string? Error;
        public string? Outcome; public string? Reason;
        public string CreatedAt = ""; public string? StartedAt; public string? FinishedAt;
        /// <summary>
        /// v3.8.34: the canonical outcome code, read from the mission this job ran.
        ///
        /// `mission_jobs` has no column for it — `UpdateJobState` never persisted one — so a
        /// durable row could say THAT it ended but not why, and the stored `status` could
        /// therefore disagree with its own `outcome` with nothing available to re-derive it from.
        /// Joined rather than added as a column: the mission already owns the canonical
        /// evaluation, and a second copy is a second thing that has to stay true.
        ///
        /// Empty for queued and running jobs, which is the signal not to re-derive their status.
        /// </summary>
        public string? OutcomeCode;
    }

    /// <summary>Insert-or-replay: if the idempotency key already exists, the EXISTING job is
    /// returned and nothing new is created — repeated delivery must not duplicate work.</summary>
    public (MissionJobRow Job, bool Replayed) PersistNewJob(string id, string goal, string? idempotencyKey)
    {
        EnsureJobTables();
        lock (_writeLock)
        {
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var existing = FindJobByIdempotencyKey(idempotencyKey!);
                if (existing is not null) return (existing, true);
            }
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO mission_jobs (id, goal, status, attempt, idempotency_key, created_at)
                VALUES ($id, $goal, 'queued', 1, $key, $created)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$goal", goal);
            cmd.Parameters.AddWithValue("$key", (object?)idempotencyKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", AnthillTime.NowUtc().ToIso());
            cmd.ExecuteNonQuery();
            return (new MissionJobRow { Id = id, Goal = goal, IdempotencyKey = idempotencyKey, CreatedAt = AnthillTime.NowUtc().ToIso() }, false);
        }
    }

    public MissionJobRow? FindJobByIdempotencyKey(string key)
    {
        EnsureJobTables();
        // v3.8.34: qualified — QueryJobs now joins `missions`, and `id`/`status`/`goal`/`created_at`
        // exist in both tables. An unqualified name here is an ambiguous-column error at runtime,
        // not a compile error.
        return QueryJobs("j.idempotency_key = $p", key).FirstOrDefault();
    }

    public MissionJobRow? GetMissionJob(string id)
    {
        EnsureJobTables();
        return QueryJobs("j.id = $p", id).FirstOrDefault();
    }

    public List<MissionJobRow> ListMissionJobs(int limit = 50)
    {
        EnsureJobTables();
        return QueryJobs(null, null, limit);
    }

    /// <summary>Atomic claim: exactly one caller wins a given queued job even with concurrent
    /// Directors on the same database (single UPDATE with a rowid subselect, BEGIN IMMEDIATE).</summary>
    public string? TryClaimJob(string jobId, string worker, int leaseSeconds)
    {
        EnsureJobTables();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE mission_jobs
                SET status='running', assigned_worker=$w, claim_at=$now, heartbeat_at=$now,
                    lease_expires_at=$lease, started_at=COALESCE(started_at,$now)
                WHERE id=$id AND status='queued' AND cancel_requested=0";
            cmd.Parameters.AddWithValue("$w", worker);
            cmd.Parameters.AddWithValue("$now", AnthillTime.NowUtc().ToIso());
            cmd.Parameters.AddWithValue("$lease", AnthillTime.NowUtc().AddSeconds(leaseSeconds).ToIso());
            cmd.Parameters.AddWithValue("$id", jobId);
            var claimed = cmd.ExecuteNonQuery() == 1;
            tx.Commit();
            return claimed ? jobId : null;
        }
    }

    /// <summary>Lease renewal. Returns false if the job is no longer this worker's (reclaimed).</summary>
    public bool HeartbeatJob(string jobId, string worker, int leaseSeconds)
    {
        EnsureJobTables();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE mission_jobs SET heartbeat_at=$now, lease_expires_at=$lease
                WHERE id=$id AND assigned_worker=$w AND status='running'";
            cmd.Parameters.AddWithValue("$now", AnthillTime.NowUtc().ToIso());
            cmd.Parameters.AddWithValue("$lease", AnthillTime.NowUtc().AddSeconds(leaseSeconds).ToIso());
            cmd.Parameters.AddWithValue("$id", jobId);
            cmd.Parameters.AddWithValue("$w", worker);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public void UpdateJobState(string jobId, string status, string? missionId = null, string? result = null,
        string? error = null, string? outcome = null, string? reason = null, bool? cancelRequested = null, bool finished = false)
    {
        EnsureJobTables();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE mission_jobs SET status=$status,
                mission_id=COALESCE($mid, mission_id), result=COALESCE($res, result),
                error=COALESCE($err, error), outcome=COALESCE($out, outcome), reason=COALESCE($why, reason),
                cancel_requested=COALESCE($cxl, cancel_requested),
                finished_at=CASE WHEN $fin=1 THEN $now ELSE finished_at END
                WHERE id=$id";
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddWithValue("$mid", (object?)missionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$res", (object?)result ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$out", (object?)outcome ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$why", (object?)reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cxl", (object?)(cancelRequested is null ? null : (cancelRequested.Value ? 1 : 0)) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fin", finished ? 1 : 0);
            cmd.Parameters.AddWithValue("$now", AnthillTime.NowUtc().ToIso());
            cmd.Parameters.AddWithValue("$id", jobId);
            cmd.ExecuteNonQuery();
        }
    }

    public void RecordJobAttempt(string jobId, int attempt, string worker, string reason,
        string? error, long durationMs, string? startedAt, string? finishedAt)
    {
        EnsureJobTables();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT OR IGNORE INTO mission_attempts (id, job_id, attempt, worker, reason, error, duration_ms, started_at, finished_at)
                VALUES ($id,$job,$n,$w,$why,$err,$ms,$s,$f)";
            cmd.Parameters.AddWithValue("$id", jobId + ":" + attempt);
            cmd.Parameters.AddWithValue("$job", jobId);
            cmd.Parameters.AddWithValue("$n", attempt);
            cmd.Parameters.AddWithValue("$w", worker);
            cmd.Parameters.AddWithValue("$why", reason);
            cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ms", durationMs);
            cmd.Parameters.AddWithValue("$s", (object?)startedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$f", (object?)finishedAt ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Startup reconciliation: classify and repair whatever the last process left behind.
    /// queued → resumable (re-dispatched) · running + expired/any lease at boot → retryable
    /// (attempt++, re-queued) while attempts remain, else orphaned → failed for operator review ·
    /// cancel_requested → cancelled. Completed work is never touched, so it is never repeated.
    /// Returns (resumable, retried, orphaned, cancelled).
    /// </summary>
    public (int Resumable, int Retried, int Orphaned, int Cancelled) ReconcileJobsAtStartup(int maxAttempts = 3)
    {
        EnsureJobTables();
        int resumable = 0, retried = 0, orphaned = 0, cancelled = 0;
        lock (_writeLock)
        {
            var incomplete = QueryJobs("j.status IN ('queued','running')", null, 1000);
            foreach (var job in incomplete)
            {
                if (job.CancelRequested)
                {
                    UpdateJobStateUnlocked("cancelled", job.Id, "cancelled_before_recovery", finished: true);
                    cancelled++;
                }
                else if (job.Status == "queued") { resumable++; } // survives as-is; dispatcher re-queues it
                else if (job.Attempt < maxAttempts) // running at boot = the old process died mid-flight
                {
                    RecordJobAttempt(job.Id, job.Attempt, job.AssignedWorker ?? "?", "lease_lost_process_died",
                        job.Error, 0, job.StartedAt, AnthillTime.NowUtc().ToIso());
                    using var conn = Connect();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"UPDATE mission_jobs SET status='queued', attempt=attempt+1,
                        assigned_worker=NULL, lease_expires_at=NULL, reason='recovered: retrying after process loss' WHERE id=$id";
                    cmd.Parameters.AddWithValue("$id", job.Id);
                    cmd.ExecuteNonQuery();
                    retried++;
                }
                else
                {
                    UpdateJobStateUnlocked("failed", job.Id, "orphaned: attempts exhausted after repeated process loss — operator review", finished: true);
                    orphaned++;
                }
            }
        }
        return (resumable, retried, orphaned, cancelled);
    }

    private void UpdateJobStateUnlocked(string status, string id, string reason, bool finished)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE mission_jobs SET status=$s, reason=$r, outcome=COALESCE(outcome,$s),
            finished_at=CASE WHEN $fin=1 THEN $now ELSE finished_at END WHERE id=$id";
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$r", reason);
        cmd.Parameters.AddWithValue("$fin", finished ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", AnthillTime.NowUtc().ToIso());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private List<MissionJobRow> QueryJobs(string? where, string? param, int limit = 50)
    {
        var list = new List<MissionJobRow>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        // v3.8.34: LEFT JOIN so a job can report the canonical outcome code of the mission it ran.
        // Aliased because mission_jobs and missions share id/goal/status/created_at, and an
        // unqualified column here would silently bind to the wrong table.
        cmd.CommandText = "SELECT j.id, j.goal, j.status, j.attempt, j.idempotency_key, j.assigned_worker, "
            + "j.lease_expires_at, j.cancel_requested, j.mission_id, j.result, j.error, j.outcome, j.reason, "
            + "j.created_at, j.started_at, j.finished_at, m.outcome_code "
            + "FROM mission_jobs j LEFT JOIN missions m ON m.id = j.mission_id"
            + (where is null ? "" : " WHERE " + where)
            + " ORDER BY j.created_at DESC, j.id DESC LIMIT $limit";
        if (param is not null) cmd.Parameters.AddWithValue("$p", param);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new MissionJobRow
            {
                Id = r.GetString(0), Goal = r.GetString(1), Status = r.GetString(2), Attempt = (int)r.GetInt64(3),
                IdempotencyKey = r.IsDBNull(4) ? null : r.GetString(4),
                AssignedWorker = r.IsDBNull(5) ? null : r.GetString(5),
                LeaseExpiresAt = r.IsDBNull(6) ? null : r.GetString(6),
                CancelRequested = r.GetInt64(7) == 1,
                MissionId = r.IsDBNull(8) ? null : r.GetString(8), Result = r.IsDBNull(9) ? null : r.GetString(9),
                Error = r.IsDBNull(10) ? null : r.GetString(10), Outcome = r.IsDBNull(11) ? null : r.GetString(11),
                Reason = r.IsDBNull(12) ? null : r.GetString(12), CreatedAt = r.GetString(13),
                StartedAt = r.IsDBNull(14) ? null : r.GetString(14), FinishedAt = r.IsDBNull(15) ? null : r.GetString(15),
                OutcomeCode = r.IsDBNull(16) ? null : r.GetString(16),
            });
        }
        return list;
    }
}
