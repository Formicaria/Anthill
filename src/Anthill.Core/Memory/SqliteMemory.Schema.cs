using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Events;
using Anthill.Core.Security;
using Anthill.SDK.Events;

namespace Anthill.Core.Memory;

/// <summary>
/// The colony's pheromone-and-history store: a hardened SQLite database accessed
/// through Microsoft.Data.Sqlite. This partial holds connection management, schema
/// creation, the migration ledger, and the cross-cutting concerns introduced by the
/// .NET migration — a read-through <see cref="IMemoryCache"/> speed layer and AES-GCM
/// field encryption for sensitive columns.
///
/// Every query in the operations partial is fully parameterised; no value is ever
/// concatenated into SQL, which closes the door on injection from agent/model output.
/// </summary>
public sealed partial class SqliteMemory : IDisposable
{
    public string DbPath { get; }
    public bool FtsAvailable { get; private set; }

    /// <summary>True when this instance is backed by a private in-memory database, not a file.</summary>
    public bool IsInMemory { get; }

    /// <summary>
    /// Holds an in-memory database in existence.
    ///
    /// SQLite destroys an in-memory database when its last connection closes, and Connect() opens
    /// and closes one per operation — so without a connection held open for the lifetime of this
    /// object, the schema would be gone before the first read and every call would silently rebuild
    /// an empty database. Null for file-backed instances, which need no such help.
    /// </summary>
    private readonly SqliteConnection? _keepAlive;

    /// <summary>
    /// Where <see cref="LogEvent"/> announces what it just recorded. v3.8.3.
    ///
    /// Settable rather than a constructor parameter, because of how the colony is actually built:
    /// <c>Queen</c> accepts an optional pre-constructed memory, so a bus passed to
    /// <see cref="SqliteMemory"/>'s constructor would only ever reach instances the Queen created
    /// herself — and every caller supplying their own memory (the CLI, the API in some
    /// configurations, several hundred tests) would get a silently unwired one. A property covers
    /// both construction paths with one mechanism.
    ///
    /// Never null. It defaults to <see cref="NullEventBus"/> so that an unwired memory behaves
    /// exactly as it did before there was a bus, and so no publication site needs a null check.
    /// </summary>
    public IEventBus EventBus { get; set; } = NullEventBus.Instance;

    private readonly FieldCipher _cipher;
    private readonly IMemoryCache _cache;
    private readonly object _writeLock = new();
    // Bumped on every write; cache keys fold it in so a single increment invalidates
    // all read-through entries without tracking individual keys.
    private long _cacheGeneration;

    public SqliteMemory(string? dbPath = null, FieldCipher? cipher = null, IMemoryCache? cache = null)
    {
        AnthillRuntime.Initialize();
        var raw = dbPath ?? AnthillRuntime.DbPath;

        IsInMemory = IsInMemoryRequest(raw);
        if (IsInMemory)
        {
            // ":memory:" used to fall through to the path branch, and the consequences were quiet
            // and platform-dependent. On Linux ':' is a legal filename character, so every caller
            // asking for an in-memory database got a FILE literally named ":memory:" in ScriptDir —
            // shared by all of them, on disk, surviving between runs. On Windows ':' is illegal and
            // the same code threw "unable to open database file", which is how it was finally found:
            // CI on windows-latest, not a local run.
            //
            // A GUID name rather than the literal, because shared-cache in-memory databases are
            // keyed BY NAME: two instances both called ":memory:" would be one database, and the
            // isolation every caller assumes would still not be there.
            DbPath = "anthill-mem-" + Guid.NewGuid().ToString("N");
        }
        else
        {
            DbPath = Path.IsPathRooted(raw) ? Path.GetFullPath(raw) : Path.GetFullPath(Path.Combine(AnthillRuntime.ScriptDir, raw));
            Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        }

        _cipher = cipher ?? FieldCipher.CreateDefault();
        _cache = cache ?? new MemoryCache(new MemoryCacheOptions());

        if (IsInMemory)
        {
            _keepAlive = new SqliteConnection(ConnString);
            _keepAlive.Open();
        }

        InitDb();
        // v2.20.0 Stage 7: one-time reset of learning state derived under the pre-v2.19 completion
        // rule. Idempotent (durable meta marker); backs up before mutating; fresh DBs just get the
        // marker. Runs here so every entry point — API, CLI, tests — passes the same boundary.
        ApplyLearningReset();
        if (!IsInMemory) HardenDbFiles();
    }

    /// <summary>
    /// The one spelling of "give me a throwaway database" this understands.
    ///
    /// Deliberately exact rather than a fuzzy match: a path that merely CONTAINS "memory" is a real
    /// path someone meant, and silently redirecting it to a database that vanishes on dispose would
    /// be far worse than the error they would otherwise get.
    /// </summary>
    public static bool IsInMemoryRequest(string? path) =>
        string.Equals(path?.Trim(), ":memory:", StringComparison.OrdinalIgnoreCase);

    // Shared cache + WAL gives readers concurrency with a single writer, which matches the
    // parallel-ant execution model. Busy timeout absorbs brief write contention. Built once here so
    // Connect() and Dispose() (pool clear) always agree on the exact connection string.
    //
    // Shared cache is what makes the in-memory mode work at all: it is how several connections
    // reach the SAME named in-memory database instead of each getting a private empty one.
    private string ConnString => new SqliteConnectionStringBuilder
    {
        DataSource = DbPath,
        Mode = IsInMemory ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true,
    }.ToString();

    private SqliteConnection Connect()
    {
        var conn = new SqliteConnection(ConnString);
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            // v2.26.0: journal_mode=WAL is a PERSISTENT database property, set once at
            // initialization (EnsureSchema) — re-declaring it on every connection was a wasted
            // round-trip. busy_timeout and foreign_keys are genuinely per-connection and stay.
            pragma.CommandText = "PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    private void HardenDbFiles()
    {
        FileSecurity.HardenFilePermissions(DbPath);
        FileSecurity.HardenFilePermissions(DbPath + "-wal");
        FileSecurity.HardenFilePermissions(DbPath + "-shm");
    }

    // ---- Cache helpers ----------------------------------------------------

    private T CacheRead<T>(string key, Func<T> factory, int ttlSeconds = 3)
    {
        var versionedKey = $"{key}::gen{Interlocked.Read(ref _cacheGeneration)}";
        return _cache.GetOrCreate(versionedKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttlSeconds);
            return factory();
        })!;
    }

    private void InvalidateCache() => Interlocked.Increment(ref _cacheGeneration);

    public void Dispose()
    {
        // Clear ONLY this database's pooled connections. The process-global ClearAllPools() used to
        // live here — but it disposes pooled handles for EVERY live SqliteMemory instance, yanking
        // connections out from under any concurrent instance. That was the source of a parallel-test
        // "Cannot access a disposed object" flake, and a latent hazard if two instances ever coexist.
        // ClearPool is scoped to this connection string; the throwaway connection is only used to
        // identify the pool and is never opened.
        try { using var c = new SqliteConnection(ConnString); SqliteConnection.ClearPool(c); } catch { }
        // Released LAST. Closing this is what destroys an in-memory database, so it must outlive
        // the pool clear above — otherwise the pool would be clearing connections to a database
        // that no longer exists.
        try { _keepAlive?.Dispose(); } catch { }
    }

    private void InitDb()
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            // v2.26.0: WAL is a persistent database property — declared once here at
            // initialization instead of on every opened connection. (Cannot run inside the
            // transaction below: journal-mode changes are refused mid-transaction.)
            using (var wal = conn.CreateCommand()) { wal.CommandText = "PRAGMA journal_mode=WAL;"; wal.ExecuteNonQuery(); }

            using var tx = conn.BeginTransaction();
            foreach (var ddl in SchemaStatements) Exec(conn, tx, ddl);

            if (AnthillRuntime.EnableFtsMemory)
            {
                try
                {
                    Exec(conn, tx, "CREATE VIRTUAL TABLE IF NOT EXISTS missions_fts USING fts5(id UNINDEXED, goal, user_result, final_result)");
                    FtsAvailable = true;
                }
                catch (SqliteException)
                {
                    FtsAvailable = false;
                }
            }

            tx.Commit();
            EnsureColumns(conn);
            RunSchemaMigrations(conn);
            // Seed the system_api sentinel mission so system-level events satisfy the
            // events→missions foreign key on a fresh database.
            Exec(conn, null,
                "INSERT OR IGNORE INTO missions (id, goal, status, created_at, saved_at) " +
                $"VALUES ('{AnthillRuntime.SystemApiMissionId}', 'System API events', 'complete', " +
                $"'{AnthillTime.NowUtc().ToIso()}', '{AnthillTime.NowUtc().ToIso()}')");
        }
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static readonly string[] SchemaStatements =
    {
        @"CREATE TABLE IF NOT EXISTS anthill_meta (
            key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS schema_migrations (
            id INTEGER PRIMARY KEY, name TEXT NOT NULL, description TEXT,
            applied_at TEXT NOT NULL, anthill_version TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS missions (
            id TEXT PRIMARY KEY, goal TEXT NOT NULL, status TEXT NOT NULL,
            user_result TEXT, debug_result TEXT, final_result TEXT,
            best_output_task_id TEXT, success_score REAL,
            created_at TEXT NOT NULL, saved_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS tasks (
            id TEXT PRIMARY KEY, mission_id TEXT NOT NULL, title TEXT NOT NULL,
            description TEXT NOT NULL, assigned_ant TEXT NOT NULL, assigned_worker TEXT, task_type TEXT NOT NULL,
            parent_task_id TEXT, parent_task_ids_json TEXT, depends_on_json TEXT,
            status TEXT NOT NULL, result TEXT, result_summary TEXT,
            result_chars INTEGER DEFAULT 0, estimated_tokens INTEGER DEFAULT 0,
            created_at TEXT, started_at TEXT, finished_at TEXT, completed_at TEXT,
            failed_at TEXT, skipped_at TEXT, elapsed_seconds REAL,
            attempt_count INTEGER DEFAULT 0, max_attempts INTEGER DEFAULT 1,
            failure_reason TEXT, failure_type TEXT, skipped_reason TEXT, blocked_reason TEXT,
            FOREIGN KEY (mission_id) REFERENCES missions(id))",
        // v3.2.0 (phase): what the ANT reported, kept whole.
        //
        // Until now an AntExecutionResult decided the task's status and was then thrown away except
        // for its narrative: artifacts, evidence, handoffs, warnings, metrics and the failure class
        // all died at this boundary. Anything downstream that wanted them — an operator asking why
        // a task failed, learning, or a future replay — had to read the prose back, which is the
        // one thing this phase exists to stop.
        //
        // A separate table rather than columns on `tasks`: this records what the ant SAID, at the
        // moment it said it, and `tasks` records what the scheduler DID about it. They can
        // legitimately disagree — a late result is ignored, a timeout overwrites the text — and
        // collapsing them into one row would destroy the evidence of exactly those disagreements.
        @"CREATE TABLE IF NOT EXISTS task_results (
            task_id TEXT PRIMARY KEY, mission_id TEXT NOT NULL, ant_name TEXT NOT NULL,
            status_code TEXT NOT NULL, success INTEGER NOT NULL DEFAULT 0,
            contract_version TEXT,
            summary TEXT NOT NULL DEFAULT '',
            failure_class TEXT, failure_reason TEXT, failure_retryable INTEGER NOT NULL DEFAULT 0,
            artifacts_json TEXT, evidence_json TEXT, handoffs_json TEXT, warnings_json TEXT,
            metrics_json TEXT, recorded_at TEXT NOT NULL,
            FOREIGN KEY (mission_id) REFERENCES missions(id))",
        @"CREATE INDEX IF NOT EXISTS idx_task_results_mission ON task_results(mission_id)",
        // v3.4.1: operator-defined tools. Name is the primary key because a tool name has exactly
        // one live meaning — a model calling 'jira_issue' must not have to disambiguate between
        // three stored versions of it. Disabled rows are KEPT rather than deleted, so an audit can
        // still explain a transcript in which a since-revoked tool was called.
        // v3.5.0: mission workspaces. The row OUTLIVES the directory — a cleaned workspace keeps
        // its record, because "what was this change based on" is asked long after the files are
        // gone. No foreign key to missions: a workspace can be prepared for a mission id before the
        // mission row exists, and losing the workspace record to satisfy an ordering constraint
        // would defeat the attribution this table exists to provide.
        @"CREATE TABLE IF NOT EXISTS mission_workspaces (
            id TEXT PRIMARY KEY, mission_id TEXT NOT NULL, root TEXT NOT NULL DEFAULT '',
            mode TEXT NOT NULL DEFAULT 'worktree', source_root TEXT NOT NULL DEFAULT '',
            base_revision TEXT NOT NULL DEFAULT '', repository_fingerprint TEXT NOT NULL DEFAULT '',
            branch TEXT NOT NULL DEFAULT '', state TEXT NOT NULL,
            retained_by TEXT, retain_reason TEXT, note TEXT,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL)",
        @"CREATE INDEX IF NOT EXISTS idx_mission_workspaces_mission ON mission_workspaces(mission_id)",
        // v3.6.0: the repository index, keyed by workspace AND revision so a stored index can never
        // be applied to a tree it does not describe. Deleted with its workspace — an index outliving
        // the tree is a set of answers about files nobody can read.
        // v3.7.0: conversations as first-class runtime objects. The transcript survives a restart,
        // and an escalated run reads as ONE history across the conversation/mission boundary.
        // v3.8.0: workers and durable task attempts. Every retry is its own ROW, because a retry
        // counter tells you something was tried three times but not that the first timed out, the
        // second hit a provider fault, and the third produced a change nobody has looked at.
        //
        // These were written once before, ahead of the code that reads them, and rode into a patch
        // release on an unrelated commit — CallSiteAuditTests caught them as written-by-nothing and
        // read-by-nothing, which is exactly its job. They are back now because the claim, the lease
        // reclaim and their tests arrive with them.
        // v3.8.19 (ADR-004) — the artifact and evidence stores. Append-only: no UPDATE and no
        // DELETE path exists, because a revision is a new row citing the old one. source_ids_json
        // carries the provenance edges, which is what makes the dependency graph derivable rather
        // than something maintained beside the data.
        //
        // NO FOREIGN KEY on mission_id, unlike every neighbouring table, and that is deliberate.
        // These rows ARE the audit trail: a cascade would delete the evidence for a decision along
        // with the mission that made it, and a restrict would make mission maintenance fail because
        // something was once proven. An artifact outliving its mission is the point.
        @"CREATE TABLE IF NOT EXISTS artifacts (
            id TEXT PRIMARY KEY, schema TEXT NOT NULL, schema_version INTEGER NOT NULL DEFAULT 1,
            producer_role TEXT NOT NULL, mission_id TEXT NOT NULL, task_id TEXT, workspace_id TEXT,
            source_ids_json TEXT NOT NULL DEFAULT '[]', content_hash TEXT NOT NULL,
            visibility TEXT NOT NULL DEFAULT 'Colony', payload TEXT NOT NULL,
            created_at TEXT NOT NULL)",
        @"CREATE INDEX IF NOT EXISTS idx_artifacts_mission ON artifacts(mission_id, schema, created_at)",
        @"CREATE INDEX IF NOT EXISTS idx_artifacts_hash ON artifacts(content_hash)",
        @"CREATE TABLE IF NOT EXISTS evidence (
            id TEXT PRIMARY KEY, kind TEXT NOT NULL, deterministic INTEGER NOT NULL DEFAULT 0,
            passed INTEGER NOT NULL DEFAULT 0, artifact_ids_json TEXT NOT NULL DEFAULT '[]',
            detail TEXT NOT NULL DEFAULT '', mission_id TEXT NOT NULL, task_id TEXT,
            created_at TEXT NOT NULL)",
        @"CREATE INDEX IF NOT EXISTS idx_evidence_mission ON evidence(mission_id, deterministic, passed)",
        @"CREATE TABLE IF NOT EXISTS workers (
            id TEXT PRIMARY KEY, roles_json TEXT NOT NULL DEFAULT '[]',
            kind TEXT NOT NULL DEFAULT 'local', max_concurrent INTEGER NOT NULL DEFAULT 1,
            last_heartbeat TEXT, registered_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS task_attempts (
            id TEXT PRIMARY KEY, task_id TEXT NOT NULL, mission_id TEXT NOT NULL,
            number INTEGER NOT NULL DEFAULT 1, worker_id TEXT NOT NULL DEFAULT '',
            state TEXT NOT NULL DEFAULT 'Running', provider TEXT, model TEXT,
            may_have_side_effects INTEGER NOT NULL DEFAULT 0,
            failure_class TEXT, failure_reason TEXT,
            lease_until TEXT, started_at TEXT NOT NULL, finished_at TEXT)",
        // The claim reads this index on every attempt to take a task, so it is not optional.
        @"CREATE INDEX IF NOT EXISTS idx_task_attempts_live ON task_attempts(task_id, state, lease_until)",
        // v0.3.8.47: projects — the long-lived container a conversation lives in. One is created
        // with every new CONVERSATION (not per message), and an operator can make one by hand
        // with a name, a markdown purpose, and an optional working-directory path.
        @"CREATE TABLE IF NOT EXISTS projects (
            id TEXT PRIMARY KEY, name TEXT NOT NULL DEFAULT '',
            description_md TEXT NOT NULL DEFAULT '', path TEXT,
            archived INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL)",
        // v0.3.8.48: project schedules — real persistence for recurring and one-time work.
        // Times are stored UTC with the operator's IANA timezone kept beside them, because a
        // "daily 07:00" means 07:00 WHERE THE OPERATOR LIVES, across DST, forever.
        @"CREATE TABLE IF NOT EXISTS project_schedules (
            id TEXT PRIMARY KEY, project_id TEXT NOT NULL, name TEXT NOT NULL DEFAULT '',
            prompt TEXT NOT NULL DEFAULT '', trigger_type TEXT NOT NULL DEFAULT 'manual',
            cron TEXT, one_time_at TEXT, local_time TEXT, timezone TEXT NOT NULL DEFAULT 'UTC',
            approval_mode TEXT NOT NULL DEFAULT 'Ask', provider TEXT, model TEXT,
            enabled INTEGER NOT NULL DEFAULT 1, overlap_policy TEXT NOT NULL DEFAULT 'skip',
            next_run_at TEXT, last_run_at TEXT, claimed_by TEXT, claimed_at TEXT,
            created_by TEXT NOT NULL DEFAULT '', updated_by TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL)",
        @"CREATE INDEX IF NOT EXISTS idx_project_schedules_due ON project_schedules(enabled, next_run_at)",
        @"CREATE TABLE IF NOT EXISTS schedule_runs (
            id TEXT PRIMARY KEY, schedule_id TEXT NOT NULL, project_id TEXT NOT NULL,
            conversation_id TEXT, status TEXT NOT NULL DEFAULT 'running',
            ""trigger"" TEXT NOT NULL DEFAULT 'schedule', summary TEXT,
            started_at TEXT NOT NULL, finished_at TEXT)",
        @"CREATE INDEX IF NOT EXISTS idx_schedule_runs ON schedule_runs(schedule_id, started_at)",
        @"CREATE TABLE IF NOT EXISTS conversations (
            id TEXT PRIMARY KEY, title TEXT NOT NULL DEFAULT '', role TEXT NOT NULL DEFAULT 'researcher',
            policy TEXT NOT NULL DEFAULT 'Ask', policy_set_by TEXT, policy_set_at TEXT,
            mission_ids_json TEXT NOT NULL DEFAULT '[]', cancelled INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS conversation_turns (
            id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, ordinal INTEGER NOT NULL,
            role TEXT NOT NULL, content TEXT NOT NULL DEFAULT '',
            provider TEXT, model TEXT,
            tools_offered_json TEXT NOT NULL DEFAULT '[]', tools_called_json TEXT NOT NULL DEFAULT '[]',
            mission_id TEXT, created_at TEXT NOT NULL)",
        @"CREATE INDEX IF NOT EXISTS idx_conversation_turns ON conversation_turns(conversation_id, ordinal)",
        // v0.3.8.47: attachments — text files the operator handed a turn. TEXT content, capped at
        // the API; a binary the colony cannot actually read is refused there, not stored as a lie.
        @"CREATE TABLE IF NOT EXISTS conversation_attachments (
            id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, turn_id TEXT NOT NULL,
            filename TEXT NOT NULL, bytes INTEGER NOT NULL DEFAULT 0,
            content TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL)",
        @"CREATE INDEX IF NOT EXISTS idx_conversation_attachments ON conversation_attachments(turn_id)",
        // v0.3.8.50 (field report): the operator's names and colors for their ants. An override
        // table, not a rename of the registry — the role id stays the system's, the face is theirs.
        @"CREATE TABLE IF NOT EXISTS ant_profiles (
            ant_id TEXT PRIMARY KEY, display_name TEXT NOT NULL DEFAULT '',
            color TEXT NOT NULL DEFAULT '', updated_by TEXT NOT NULL DEFAULT '',
            updated_at TEXT NOT NULL)",
        // Decisions are their own table: an operator asking "why was this allowed" is asking about
        // ACTIONS, not turns, and one turn can take several. Refusals are stored too — a refused
        // attempt is the one nobody saw happen.
        @"CREATE TABLE IF NOT EXISTS escalation_decisions (
            id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, action TEXT NOT NULL,
            allowed INTEGER NOT NULL DEFAULT 0, policy TEXT NOT NULL DEFAULT 'Ask',
            decided_by TEXT NOT NULL DEFAULT '', decided_at TEXT NOT NULL, reason TEXT)",
        @"CREATE INDEX IF NOT EXISTS idx_escalation_decisions ON escalation_decisions(conversation_id)",
        @"CREATE TABLE IF NOT EXISTS repository_index (
            workspace_id TEXT NOT NULL, revision TEXT NOT NULL,
            fingerprint TEXT NOT NULL DEFAULT '', root TEXT NOT NULL DEFAULT '',
            truncated INTEGER NOT NULL DEFAULT 0, build_ms INTEGER NOT NULL DEFAULT 0,
            reused_files INTEGER NOT NULL DEFAULT 0, built_at TEXT NOT NULL,
            PRIMARY KEY (workspace_id, revision))",
        @"CREATE TABLE IF NOT EXISTS repository_index_files (
            workspace_id TEXT NOT NULL, revision TEXT NOT NULL, path TEXT NOT NULL,
            language TEXT NOT NULL DEFAULT 'other', bytes INTEGER NOT NULL DEFAULT 0,
            lines INTEGER NOT NULL DEFAULT 0, content_hash TEXT NOT NULL DEFAULT '',
            symbols_json TEXT,
            PRIMARY KEY (workspace_id, revision, path))",
        @"CREATE TABLE IF NOT EXISTS tool_definitions (
            name TEXT PRIMARY KEY, description TEXT NOT NULL DEFAULT '',
            kind TEXT NOT NULL, parameters_json TEXT NOT NULL DEFAULT '{}',
            config_json TEXT NOT NULL DEFAULT '{}', allowed_roles_json TEXT NOT NULL DEFAULT '[]',
            enabled INTEGER NOT NULL DEFAULT 1,
            created_by TEXT NOT NULL DEFAULT 'operator', created_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS events (
            id TEXT PRIMARY KEY, mission_id TEXT NOT NULL, task_id TEXT, ant_name TEXT,
            event_type TEXT NOT NULL, message TEXT NOT NULL, metadata_json TEXT,
            created_at TEXT NOT NULL, FOREIGN KEY (mission_id) REFERENCES missions(id))",
        // v2.24.0: the Shadow Operations line had no storage at all, so a recommendation could
        // never be compared with what the operator actually did — qualification could only ever be
        // computed over replayed scenarios. Recommendation and outcome are separate tables because
        // they arrive at different times; joining them is what produces a scoreable pair.
        @"CREATE TABLE IF NOT EXISTS shadow_recommendations (
            incident_id TEXT PRIMARY KEY, diagnosis TEXT NOT NULL DEFAULT '',
            proposed_action TEXT NOT NULL DEFAULT '', chosen_skill_id TEXT,
            chosen_skill_confidence REAL NOT NULL DEFAULT 0, risk_level TEXT,
            risk_score INTEGER NOT NULL DEFAULT 0, risk_requires_approval INTEGER NOT NULL DEFAULT 0,
            risk_reasons_json TEXT,
            predicted_outcome TEXT NOT NULL DEFAULT '', verification_plan_json TEXT,
            rollback_plan TEXT, rollback_reason TEXT,
            would_recommend_execution INTEGER NOT NULL DEFAULT 0, observed_at TEXT NOT NULL)",
        // v2.25.0: fault-injection runs recorded as a series — the V3 "repeated runs stable"
        // threshold is a property of history, and a harness that keeps no history cannot answer it.
        @"CREATE TABLE IF NOT EXISTS fault_injection_runs (
            id INTEGER PRIMARY KEY AUTOINCREMENT, ran_at TEXT NOT NULL,
            total INTEGER NOT NULL DEFAULT 0, passed INTEGER NOT NULL DEFAULT 0,
            all_passed INTEGER NOT NULL DEFAULT 0, fingerprint TEXT NOT NULL DEFAULT '',
            results_json TEXT)",
        // v2.25.0 Phase F: operator attestations for the V3 readiness gate. Absence of a row
        // means "not attested", which the evaluation reads as NOT satisfied.
        @"CREATE TABLE IF NOT EXISTS readiness_attestations (
            threshold_id TEXT PRIMARY KEY, satisfied INTEGER NOT NULL DEFAULT 0,
            note TEXT, attested_by TEXT, attested_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS shadow_outcomes (
            incident_id TEXT PRIMARY KEY, diagnosis_correct INTEGER NOT NULL DEFAULT 0,
            action_was_needed INTEGER NOT NULL DEFAULT 0, action_matched INTEGER NOT NULL DEFAULT 0,
            would_have_succeeded INTEGER NOT NULL DEFAULT 0, operator_note TEXT,
            resolved_at TEXT NOT NULL)",
        // v2.21.0: the V2.12 skills line was in-memory only — every promotion a skill earned was
        // lost on exit, which is why nothing could safely plan with skills or feed them candidates.
        @"CREATE TABLE IF NOT EXISTS skills (
            id TEXT PRIMARY KEY, version INTEGER NOT NULL DEFAULT 1, purpose TEXT NOT NULL DEFAULT '',
            environments_json TEXT, required_capabilities_json TEXT, procedure_json TEXT,
            verification_policy TEXT NOT NULL DEFAULT 'code_patch', compensation_plan TEXT,
            success_count INTEGER NOT NULL DEFAULT 0, failure_count INTEGER NOT NULL DEFAULT 0,
            consecutive_failures INTEGER NOT NULL DEFAULT 0,
            status TEXT NOT NULL DEFAULT 'Candidate', last_validated TEXT,
            evidence_bundles_json TEXT, notes_json TEXT, saved_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS pheromone_trails (
            id TEXT PRIMARY KEY, trail_key TEXT UNIQUE NOT NULL, trail_type TEXT NOT NULL,
            strength REAL NOT NULL, success_count INTEGER NOT NULL, failure_count INTEGER NOT NULL,
            last_updated TEXT NOT NULL, metadata_json TEXT)",
        @"CREATE TABLE IF NOT EXISTS patch_sets (
            id TEXT PRIMARY KEY, mission_id TEXT NOT NULL, task_id TEXT NOT NULL,
            summary TEXT NOT NULL, proposal_count INTEGER NOT NULL, created_at TEXT NOT NULL,
            FOREIGN KEY (mission_id) REFERENCES missions(id), FOREIGN KEY (task_id) REFERENCES tasks(id))",
        @"CREATE TABLE IF NOT EXISTS patch_proposals (
            id TEXT PRIMARY KEY, patch_set_id TEXT NOT NULL, mission_id TEXT NOT NULL,
            task_id TEXT NOT NULL, file_path TEXT NOT NULL, change_type TEXT NOT NULL,
            reason TEXT NOT NULL, risk TEXT NOT NULL, old_content TEXT, new_content TEXT,
            requires_approval INTEGER NOT NULL, status TEXT NOT NULL, created_at TEXT NOT NULL,
            applied_at TEXT, backup_path TEXT, last_error TEXT,
            FOREIGN KEY (mission_id) REFERENCES missions(id),
            FOREIGN KEY (task_id) REFERENCES tasks(id),
            FOREIGN KEY (patch_set_id) REFERENCES patch_sets(id))",
        @"CREATE TABLE IF NOT EXISTS approval_requests (
            id TEXT PRIMARY KEY, mission_id TEXT NOT NULL, task_id TEXT, action_type TEXT NOT NULL,
            target_id TEXT NOT NULL, title TEXT NOT NULL, description TEXT NOT NULL, status TEXT NOT NULL,
            requested_by TEXT NOT NULL, decision_note TEXT, metadata_json TEXT,
            created_at TEXT NOT NULL, decided_at TEXT, FOREIGN KEY (mission_id) REFERENCES missions(id))",
        @"CREATE TABLE IF NOT EXISTS task_result_summaries (
            task_id TEXT PRIMARY KEY, mission_id TEXT NOT NULL, ant_name TEXT NOT NULL,
            task_type TEXT NOT NULL, status TEXT NOT NULL, summary TEXT NOT NULL,
            result_chars INTEGER NOT NULL, estimated_tokens INTEGER NOT NULL, created_at TEXT NOT NULL,
            FOREIGN KEY (mission_id) REFERENCES missions(id))",
        @"CREATE TABLE IF NOT EXISTS message_metrics (
            id TEXT PRIMARY KEY, mission_id TEXT NOT NULL, task_id TEXT, ant_name TEXT,
            metric_type TEXT NOT NULL, input_chars INTEGER NOT NULL, output_chars INTEGER NOT NULL,
            input_tokens_est INTEGER NOT NULL, output_tokens_est INTEGER NOT NULL,
            metadata_json TEXT, created_at TEXT NOT NULL, FOREIGN KEY (mission_id) REFERENCES missions(id))",
        @"CREATE TABLE IF NOT EXISTS agent_messages (
            id TEXT PRIMARY KEY, mission_id TEXT NOT NULL, task_id TEXT, sender TEXT NOT NULL,
            recipient TEXT NOT NULL, message_type TEXT NOT NULL, content TEXT,
            content_chars INTEGER NOT NULL, estimated_tokens INTEGER NOT NULL, metadata_json TEXT,
            schema_version TEXT NOT NULL, created_at TEXT NOT NULL, FOREIGN KEY (mission_id) REFERENCES missions(id))",
        @"CREATE TABLE IF NOT EXISTS source_records (
            id TEXT PRIMARY KEY, mission_id TEXT NOT NULL, task_id TEXT, ant_name TEXT,
            title TEXT NOT NULL, url TEXT NOT NULL, domain TEXT NOT NULL, snippet TEXT, summary TEXT,
            provider TEXT NOT NULL, relevance_score REAL DEFAULT 0, freshness_score REAL DEFAULT 0,
            authority_score REAL DEFAULT 0, confidence_score REAL DEFAULT 0,
            confidence_label TEXT DEFAULT 'unknown', quality_notes TEXT, created_at TEXT NOT NULL,
            FOREIGN KEY (mission_id) REFERENCES missions(id))",
        // 24/7 autonomy (Phase 0): backlog of standing objectives + per-mission audit trail.
        @"CREATE TABLE IF NOT EXISTS objectives (
            id TEXT PRIMARY KEY, title TEXT NOT NULL, charter TEXT NOT NULL,
            priority INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL,
            max_runs INTEGER NOT NULL DEFAULT 0, run_count INTEGER NOT NULL DEFAULT 0,
            consecutive_failures INTEGER NOT NULL DEFAULT 0, parent_objective_id TEXT,
            metadata_json TEXT, created_at TEXT NOT NULL, last_run_at TEXT,
            success_ema REAL)",
        @"CREATE TABLE IF NOT EXISTS autonomy_runs (
            id TEXT PRIMARY KEY, objective_id TEXT NOT NULL, mission_id TEXT,
            generated_goal TEXT NOT NULL, mission_status TEXT NOT NULL, success_score REAL,
            follow_ups_created INTEGER NOT NULL DEFAULT 0, notes TEXT,
            started_at TEXT NOT NULL, finished_at TEXT,
            FOREIGN KEY (objective_id) REFERENCES objectives(id))",
        // Operator accounts: password-based login with roles (admin / coordinator).
        // The static API token is no longer the web credential; these accounts are.
        @"CREATE TABLE IF NOT EXISTS users (
            username TEXT PRIMARY KEY, password_hash TEXT NOT NULL, role TEXT NOT NULL,
            active INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL, last_login_at TEXT)",
        // Model provider connections: one row per external provider (openai/anthropic/perplexity/
        // openrouter/...). api_key is sealed at rest with FieldCipher (AES-256-GCM); it is never
        // returned to the console — only a "configured" boolean and metadata are. Ollama needs no
        // row here since it has no key (host/model live in config.json).
        @"CREATE TABLE IF NOT EXISTS provider_credentials (
            provider TEXT PRIMARY KEY, api_key TEXT, base_url TEXT, label TEXT,
            enabled INTEGER NOT NULL DEFAULT 1,
            last_verified_at TEXT, last_verify_ok INTEGER, last_verify_message TEXT,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL)",
        // Helpful indexes for the hot lookups the colony performs constantly.
        "CREATE INDEX IF NOT EXISTS idx_tasks_mission ON tasks(mission_id)",
        "CREATE INDEX IF NOT EXISTS idx_events_mission ON events(mission_id)",
        "CREATE INDEX IF NOT EXISTS idx_events_type ON events(event_type)",
        "CREATE INDEX IF NOT EXISTS idx_sources_mission ON source_records(mission_id)",
        "CREATE INDEX IF NOT EXISTS idx_objectives_status ON objectives(status)",
        "CREATE INDEX IF NOT EXISTS idx_autonomy_runs_objective ON autonomy_runs(objective_id)",
        "CREATE INDEX IF NOT EXISTS idx_autonomy_runs_started ON autonomy_runs(started_at)",
    };

    private void EnsureColumns(SqliteConnection conn)
    {
        HashSet<string> ColumnsFor(string table)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var reader = cmd.ExecuteReader();
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read()) cols.Add(reader.GetString(1));
            return cols;
        }

        void AddMissing(string table, Dictionary<string, string> wanted)
        {
            var existing = ColumnsFor(table);
            foreach (var (col, type) in wanted)
                if (!existing.Contains(col))
                    Exec(conn, null, $"ALTER TABLE {table} ADD COLUMN {col} {type}");
        }

        AddMissing("missions", new() { ["user_result"] = "TEXT", ["debug_result"] = "TEXT", ["best_output_task_id"] = "TEXT" });
        // v2.26.0 pre-V3 hardening (migration 16): the canonical evaluation is PERSISTED — the
        // authoritative outcome must not live only in transient events or per-caller re-derivation.
        // Legacy rows read as '' → "no evaluation", which is never treated as verified.
        AddMissing("missions", new()
        {
            ["outcome_code"] = "TEXT NOT NULL DEFAULT ''",
            ["stop_reason"] = "TEXT",
            ["verification_status"] = "TEXT NOT NULL DEFAULT ''",
            ["deliverable_status"] = "TEXT NOT NULL DEFAULT ''",
            ["evaluator_version"] = "TEXT NOT NULL DEFAULT ''",
            ["evaluated_at"] = "TEXT",
        });
        // v2.26.0: task rows must carry enough to reproduce live evaluation — criticality above
        // all, because row-based evaluation previously guessed it and could disagree with the
        // live mission object.
        AddMissing("tasks", new()
        {
            ["critical"] = "INTEGER NOT NULL DEFAULT 1",
            ["outcome_code"] = "TEXT NOT NULL DEFAULT ''",
            ["cancellation_reason"] = "TEXT",
        });
        // v2.26.0: optimistic-concurrency revision for row-level skill updates (whole-registry
        // saves were last-writer-wins across concurrent missions).
        AddMissing("skills", new() { ["revision"] = "INTEGER NOT NULL DEFAULT 0" });
        // v0.3.8.46: an operator's pin on a conversation. Legacy rows read 0 = unpinned.
        AddMissing("conversations", new() { ["pinned"] = "INTEGER NOT NULL DEFAULT 0" });
        // v0.3.8.46: per-turn token accounting. NULLABLE, no default — a provider that does not
        // report usage is unknown, and unknown recorded as 0 would understate every total.
        AddMissing("conversation_turns", new() { ["prompt_tokens"] = "INTEGER", ["completion_tokens"] = "INTEGER" });
        // v0.3.8.47: which project a conversation belongs to. Null for legacy conversations.
        AddMissing("conversations", new() { ["project_id"] = "TEXT" });
        // v0.3.8.48: project-level defaults. The policy is ATTRIBUTED like the conversation's —
        // a standing permission with no author reads as Ask, fail closed, same rule everywhere.
        AddMissing("projects", new()
        {
            ["default_policy"] = "TEXT NOT NULL DEFAULT 'Ask'",
            ["default_policy_by"] = "TEXT",
            ["default_policy_at"] = "TEXT",
            ["default_provider"] = "TEXT",
            ["default_model"] = "TEXT",
        });
        // v0.3.8.48: objectives belong to projects. Null = legacy/unassigned, surfaced as such —
        // never silently attached to an arbitrary project.
        AddMissing("objectives", new() { ["project_id"] = "TEXT" });
        // v2.26.0: what KIND of signal a trail carries. Planning may only read procedural /
        // routing categories; operational telemetry must not steer strategy.
        AddMissing("pheromone_trails", new() { ["signal_category"] = "TEXT NOT NULL DEFAULT ''" });
        // v2.24.0: the first cut of this table stored only the risk LABEL. `QualificationMetrics`
        // counts a policy violation as "would have recommended execution while approval was
        // required" — with the approval flag unpersisted, that count could only ever rehydrate as
        // zero, i.e. the safety invariant would have reported itself permanently satisfied.
        AddMissing("shadow_recommendations", new()
        {
            ["risk_score"] = "INTEGER NOT NULL DEFAULT 0",
            ["risk_requires_approval"] = "INTEGER NOT NULL DEFAULT 0",
            ["risk_reasons_json"] = "TEXT",
        });
        // v2.20.0 Stage 7: pre-reset trails are marked legacy — retained for reporting, excluded
        // from planning until they earn a post-reset success, protected from pruning.
        AddMissing("pheromone_trails", new() { ["legacy"] = "INTEGER NOT NULL DEFAULT 0" });
        AddMissing("tasks", new()
        {
            ["task_type"] = "TEXT DEFAULT 'general'", ["parent_task_id"] = "TEXT", ["parent_task_ids_json"] = "TEXT",
            ["assigned_worker"] = "TEXT",
            ["depends_on_json"] = "TEXT", ["result_summary"] = "TEXT", ["result_chars"] = "INTEGER DEFAULT 0",
            ["estimated_tokens"] = "INTEGER DEFAULT 0", ["created_at"] = "TEXT", ["started_at"] = "TEXT",
            ["finished_at"] = "TEXT", ["completed_at"] = "TEXT", ["failed_at"] = "TEXT", ["skipped_at"] = "TEXT",
            ["elapsed_seconds"] = "REAL", ["attempt_count"] = "INTEGER DEFAULT 0", ["max_attempts"] = "INTEGER DEFAULT 1",
            ["failure_reason"] = "TEXT", ["failure_type"] = "TEXT", ["skipped_reason"] = "TEXT", ["blocked_reason"] = "TEXT",
            // v2.22.0: provenance for skill credit — which proven procedure this task followed.
            ["skill_id"] = "TEXT",
        });
        // v0.3.8.37: base_hash — what the target hashed to when the patch was built. Additive and
        // nullable, so proposals written before this release keep applying; PatchApply only enforces
        // the check when the column holds a value.
        AddMissing("patch_proposals", new() { ["applied_at"] = "TEXT", ["backup_path"] = "TEXT", ["last_error"] = "TEXT", ["base_hash"] = "TEXT" });
        // Phase 4 learning loop: per-objective success EMA (nullable — null until the first recorded run).
        AddMissing("objectives", new() { ["success_ema"] = "REAL" });
        AddMissing("source_records", new()
        {
            ["relevance_score"] = "REAL DEFAULT 0", ["freshness_score"] = "REAL DEFAULT 0", ["authority_score"] = "REAL DEFAULT 0",
            ["confidence_score"] = "REAL DEFAULT 0", ["confidence_label"] = "TEXT DEFAULT 'unknown'", ["quality_notes"] = "TEXT",
        });
    }

    private void RunSchemaMigrations(SqliteConnection conn)
    {
        var migrations = new (int Id, string Name, string Description)[]
        {
            (1, "base_memory_schema", "Mission, task, event, pheromone, patch, approval, and metric tables verified."),
            (2, "external_research_schema", "Source records and source quality columns verified."),
            (3, "api_runtime_schema", "system_api mission/event compatibility verified."),
            (4, "agent_communication_schema", "Agent message ledger verified."),
            (5, "selftest_schema", "Self-test framework expectations verified."),
            (6, "config_and_migration_framework", "Configuration, workspace, schema metadata, and migration ledger enabled."),
            (7, "scheduler_task_lifecycle", "Task scheduler lifecycle, retry, failure, skip, and graph metadata columns verified."),
            (8, "autonomy_rails", "Autonomy backlog (objectives) and per-mission audit trail (autonomy_runs) tables verified."),
            (9, "user_accounts", "Operator accounts (users) with password login and roles verified."),
            (10, "model_provider_connections", "Encrypted API-key storage for external model providers (provider_credentials) verified."),
            (11, "autonomy_learning", "Phase 4 learning loop: per-objective success EMA column (objectives.success_ema) verified."),
            (12, "durable_skills", "V2.12 skills line persisted (skills table) — promotion state now survives restart."),
            (13, "skill_provenance", "tasks.skill_id records which proven procedure a task followed, closing the learning loop."),
            (14, "shadow_qualification", "Shadow recommendations and operator outcomes persisted — qualification can accumulate from live incidents, not only replayed scenarios."),
            (15, "user_defined_tools", "Operator-defined tools (tool_definitions) persisted — the tool ecosystem outlives the release cycle."),
            (16, "mission_workspaces", "Mission workspaces (mission_workspaces) persisted with base revision and lifecycle — changes become attributable and survive restart."),
            (17, "repository_index", "Repository index (repository_index, repository_index_files) persisted per workspace+revision — a restart reuses everything unchanged instead of re-walking the tree."),
            (18, "conversations", "Conversations, turns and escalation decisions persisted — a conversation survives restart and an escalated run reads as one history."),
            (19, "durable_attempts", "Workers and task attempts persisted with leases — a claim is atomic, a retry is a distinct attempt, and expired work is reclaimable."),
            (20, "artifact_evidence_store", "ADR-004 artifact and evidence stores (artifacts, evidence) — typed, hashed, append-only work products with provenance. Additive: nothing produces them yet."),
        };
        foreach (var (id, name, description) in migrations)
        {
            if (MigrationApplied(conn, id)) continue;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO schema_migrations (id, name, description, applied_at, anthill_version) VALUES (@id, @n, @d, @a, @v)";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@d", description);
            cmd.Parameters.AddWithValue("@a", AnthillTime.NowUtc().ToIso());
            cmd.Parameters.AddWithValue("@v", AnthillRuntime.Version);
            cmd.ExecuteNonQuery();
        }
        SetMeta(conn, "schema_version", AnthillRuntime.SchemaVersion);
        SetMeta(conn, "anthill_version", AnthillRuntime.Version);
        SetMeta(conn, "config_profile", AnthillRuntime.Config.SafetyProfile);
        SetMeta(conn, "config_path", AnthillRuntime.ConfigPath);
        SetMeta(conn, "workspace_root", AnthillRuntime.WorkspaceRootPath);
        SetMeta(conn, "db_path", DbPath);
    }

    private bool MigrationApplied(SqliteConnection conn, int id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM schema_migrations WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() is not null;
    }

    private void SetMeta(SqliteConnection conn, string key, object value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO anthill_meta (key, value, updated_at) VALUES (@k, @v, @u)";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", Json.SafeDumps(value));
        cmd.Parameters.AddWithValue("@u", AnthillTime.NowUtc().ToIso());
        cmd.ExecuteNonQuery();
    }
}
