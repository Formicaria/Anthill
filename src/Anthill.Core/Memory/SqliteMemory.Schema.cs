using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Security;

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
        DbPath = Path.IsPathRooted(raw) ? Path.GetFullPath(raw) : Path.GetFullPath(Path.Combine(AnthillRuntime.ScriptDir, raw));
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        _cipher = cipher ?? FieldCipher.CreateDefault();
        _cache = cache ?? new MemoryCache(new MemoryCacheOptions());
        InitDb();
        HardenDbFiles();
    }

    private SqliteConnection Connect()
    {
        // Shared cache + WAL gives readers concurrency with a single writer, which matches
        // the parallel-ant execution model. Busy timeout absorbs brief write contention.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };
        var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;";
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
        SqliteConnection.ClearAllPools();
    }

    private void InitDb()
    {
        lock (_writeLock)
        {
            using var conn = Connect();
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
            description TEXT NOT NULL, assigned_ant TEXT NOT NULL, task_type TEXT NOT NULL,
            parent_task_id TEXT, parent_task_ids_json TEXT, depends_on_json TEXT,
            status TEXT NOT NULL, result TEXT, result_summary TEXT,
            result_chars INTEGER DEFAULT 0, estimated_tokens INTEGER DEFAULT 0,
            created_at TEXT, started_at TEXT, finished_at TEXT, completed_at TEXT,
            failed_at TEXT, skipped_at TEXT, elapsed_seconds REAL,
            attempt_count INTEGER DEFAULT 0, max_attempts INTEGER DEFAULT 1,
            failure_reason TEXT, failure_type TEXT, skipped_reason TEXT, blocked_reason TEXT,
            FOREIGN KEY (mission_id) REFERENCES missions(id))",
        @"CREATE TABLE IF NOT EXISTS events (
            id TEXT PRIMARY KEY, mission_id TEXT NOT NULL, task_id TEXT, ant_name TEXT,
            event_type TEXT NOT NULL, message TEXT NOT NULL, metadata_json TEXT,
            created_at TEXT NOT NULL, FOREIGN KEY (mission_id) REFERENCES missions(id))",
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
            metadata_json TEXT, created_at TEXT NOT NULL, last_run_at TEXT)",
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
        AddMissing("tasks", new()
        {
            ["task_type"] = "TEXT DEFAULT 'general'", ["parent_task_id"] = "TEXT", ["parent_task_ids_json"] = "TEXT",
            ["depends_on_json"] = "TEXT", ["result_summary"] = "TEXT", ["result_chars"] = "INTEGER DEFAULT 0",
            ["estimated_tokens"] = "INTEGER DEFAULT 0", ["created_at"] = "TEXT", ["started_at"] = "TEXT",
            ["finished_at"] = "TEXT", ["completed_at"] = "TEXT", ["failed_at"] = "TEXT", ["skipped_at"] = "TEXT",
            ["elapsed_seconds"] = "REAL", ["attempt_count"] = "INTEGER DEFAULT 0", ["max_attempts"] = "INTEGER DEFAULT 1",
            ["failure_reason"] = "TEXT", ["failure_type"] = "TEXT", ["skipped_reason"] = "TEXT", ["blocked_reason"] = "TEXT",
        });
        AddMissing("patch_proposals", new() { ["applied_at"] = "TEXT", ["backup_path"] = "TEXT", ["last_error"] = "TEXT" });
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
