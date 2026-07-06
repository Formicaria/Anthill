using System.Text.Json;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Microsoft.Data.Sqlite;

namespace Anthill.Core.Homelab;

/// <summary>
/// SQLite persistence for the homelab foundation (v1.9.0, NORTH_STAR Phase 4). Lives in the same
/// database file as colony memory so homelab knowledge is linkable to missions and searchable, but
/// owns its own tables and never touches the mission schema. Schema creation is idempotent
/// (CREATE TABLE IF NOT EXISTS) — fresh DB, existing DB, and re-runs are all safe, mirroring
/// SqliteMemory. Read-only foundation: nothing here can control infrastructure.
/// </summary>
public sealed class HomelabRepository : IHomelabRepository, IDisposable
{
    public string DbPath { get; }
    private readonly object _writeLock = new();

    public HomelabRepository(string? dbPath = null)
    {
        AnthillRuntime.Initialize();
        var raw = dbPath ?? AnthillRuntime.DbPath;
        DbPath = Path.IsPathRooted(raw) ? Path.GetFullPath(raw) : Path.GetFullPath(Path.Combine(AnthillRuntime.ScriptDir, raw));
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        InitDb();
    }

    public void Dispose() => SqliteConnection.ClearAllPools();

    private SqliteConnection Connect()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };
        var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>The 15 NORTH_STAR Phase 4 tables plus homelab_meta (scheduler job state, D4).</summary>
    internal static readonly string[] TableNames =
    {
        "homelab_nodes", "network_devices", "services", "vm_inventory", "container_inventory",
        "storage_inventory", "backup_inventory", "health_checks", "homelab_events", "change_log",
        "incidents", "dependencies", "risk_records", "homelab_credentials", "homelab_target_allowlist",
    };

    private static readonly string[] SchemaStatements =
    {
        @"CREATE TABLE IF NOT EXISTS homelab_nodes (
            id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL, address TEXT,
            os TEXT, role_tags_json TEXT, notes TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS network_devices (
            id TEXT PRIMARY KEY, name TEXT, kind TEXT NOT NULL, mac TEXT, ip TEXT, vlan TEXT,
            known INTEGER NOT NULL DEFAULT 1, notes TEXT, first_seen TEXT, last_seen TEXT)",
        @"CREATE TABLE IF NOT EXISTS services (
            id TEXT PRIMARY KEY, name TEXT NOT NULL, node_id TEXT, url TEXT, ports_json TEXT,
            protocol TEXT, owner TEXT, criticality TEXT NOT NULL DEFAULT 'normal',
            internet_exposed INTEGER NOT NULL DEFAULT 0, notes TEXT,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS vm_inventory (
            id TEXT PRIMARY KEY, vm_id TEXT, name TEXT NOT NULL, node_id TEXT, status TEXT,
            cpu_cores INTEGER DEFAULT 0, memory_mb INTEGER DEFAULT 0,
            uptime_seconds INTEGER DEFAULT 0, updated_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS container_inventory (
            id TEXT PRIMARY KEY, container_id TEXT, kind TEXT NOT NULL DEFAULT 'lxc',
            name TEXT NOT NULL, node_id TEXT, status TEXT, updated_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS storage_inventory (
            id TEXT PRIMARY KEY, entry_kind TEXT NOT NULL DEFAULT 'pool', name TEXT NOT NULL,
            node_id TEXT, pool_id TEXT, kind TEXT, model TEXT, smart_status TEXT,
            total_bytes INTEGER DEFAULT 0, used_bytes INTEGER DEFAULT 0,
            size_bytes INTEGER DEFAULT 0, updated_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS backup_inventory (
            id TEXT PRIMARY KEY, target_kind TEXT NOT NULL, target_id TEXT NOT NULL, location TEXT,
            status TEXT NOT NULL DEFAULT 'unknown', last_success TEXT, last_attempt TEXT,
            size_bytes INTEGER DEFAULT 0, notes TEXT, updated_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS health_checks (
            id TEXT PRIMARY KEY, check_kind TEXT NOT NULL, target TEXT NOT NULL, service_id TEXT,
            node_id TEXT, status TEXT NOT NULL, latency_ms REAL DEFAULT 0, detail TEXT,
            checked_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS homelab_events (
            id TEXT PRIMARY KEY, event_type TEXT NOT NULL, subject_kind TEXT, subject_id TEXT,
            severity TEXT NOT NULL DEFAULT 'info', message TEXT, mission_id TEXT,
            created_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS change_log (
            id TEXT PRIMARY KEY, subject_kind TEXT NOT NULL, subject_id TEXT NOT NULL,
            change_kind TEXT NOT NULL, summary TEXT, changed_by TEXT, mission_id TEXT,
            created_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS incidents (
            id TEXT PRIMARY KEY, title TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'open',
            severity TEXT NOT NULL DEFAULT 'warning', subject_kind TEXT, subject_id TEXT,
            root_cause TEXT, opened_at TEXT NOT NULL, resolved_at TEXT)",
        @"CREATE TABLE IF NOT EXISTS dependencies (
            id TEXT PRIMARY KEY, from_kind TEXT NOT NULL, from_id TEXT NOT NULL,
            to_kind TEXT NOT NULL, to_id TEXT NOT NULL,
            dependency_kind TEXT NOT NULL DEFAULT 'runs_on', notes TEXT, created_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS risk_records (
            id TEXT PRIMARY KEY, finding_kind TEXT NOT NULL, subject_kind TEXT, subject_id TEXT,
            severity TEXT NOT NULL DEFAULT 'warning', summary TEXT,
            status TEXT NOT NULL DEFAULT 'open', created_at TEXT NOT NULL, updated_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS homelab_credentials (
            id TEXT PRIMARY KEY, kind TEXT NOT NULL, target_host TEXT, secret_protected TEXT,
            last_verified TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS homelab_target_allowlist (
            id TEXT PRIMARY KEY, target TEXT NOT NULL, note TEXT,
            enabled INTEGER NOT NULL DEFAULT 1, added_by TEXT, created_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS homelab_meta (
            key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL)",
        @"CREATE INDEX IF NOT EXISTS idx_homelab_events_created ON homelab_events(created_at)",
        @"CREATE INDEX IF NOT EXISTS idx_change_log_created ON change_log(created_at)",
        @"CREATE INDEX IF NOT EXISTS idx_health_checks_checked ON health_checks(checked_at)",
    };

    private void InitDb()
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var tx = conn.BeginTransaction();
            foreach (var ddl in SchemaStatements)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = ddl;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private static void Bind(SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    // ---- Nodes -------------------------------------------------------------------------------

    public void UpsertNode(HomelabNode node, string changedBy)
    {
        var now = AnthillTime.NowUtc().ToIso();
        if (string.IsNullOrWhiteSpace(node.CreatedAt)) node.CreatedAt = now;
        node.UpdatedAt = now;
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO homelab_nodes (id, name, kind, address, os, role_tags_json, notes, created_at, updated_at)
                VALUES ($id, $name, $kind, $address, $os, $tags, $notes, $created, $updated)
                ON CONFLICT(id) DO UPDATE SET name=$name, kind=$kind, address=$address, os=$os,
                    role_tags_json=$tags, notes=$notes, updated_at=$updated";
            Bind(cmd, "$id", node.Id); Bind(cmd, "$name", node.Name); Bind(cmd, "$kind", node.Kind);
            Bind(cmd, "$address", node.Address); Bind(cmd, "$os", node.Os);
            Bind(cmd, "$tags", JsonSerializer.Serialize(node.RoleTags)); Bind(cmd, "$notes", node.Notes);
            Bind(cmd, "$created", node.CreatedAt); Bind(cmd, "$updated", node.UpdatedAt);
            cmd.ExecuteNonQuery();
        }
        RecordChange(new ChangeRecord
        {
            SubjectKind = "host", SubjectId = node.Id, ChangeKind = "updated",
            Summary = $"Node '{node.Name}' upserted", ChangedBy = changedBy, CreatedAt = now,
        });
    }

    public IReadOnlyList<HomelabNode> ListNodes()
    {
        var list = new List<HomelabNode>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, kind, address, os, role_tags_json, notes, created_at, updated_at FROM homelab_nodes ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new HomelabNode
            {
                Id = r.GetString(0), Name = r.GetString(1), Kind = r.GetString(2),
                Address = r.IsDBNull(3) ? "" : r.GetString(3), Os = r.IsDBNull(4) ? "" : r.GetString(4),
                RoleTags = r.IsDBNull(5) ? new() : (JsonSerializer.Deserialize<List<string>>(r.GetString(5)) ?? new()),
                Notes = r.IsDBNull(6) ? "" : r.GetString(6),
                CreatedAt = r.GetString(7), UpdatedAt = r.GetString(8),
            });
        }
        return list;
    }

    // ---- Services ------------------------------------------------------------------------------

    public void UpsertService(ServiceRecord service, string changedBy)
    {
        var now = AnthillTime.NowUtc().ToIso();
        if (string.IsNullOrWhiteSpace(service.CreatedAt)) service.CreatedAt = now;
        service.UpdatedAt = now;
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO services (id, name, node_id, url, ports_json, protocol, owner, criticality, internet_exposed, notes, created_at, updated_at)
                VALUES ($id, $name, $node, $url, $ports, $proto, $owner, $crit, $exposed, $notes, $created, $updated)
                ON CONFLICT(id) DO UPDATE SET name=$name, node_id=$node, url=$url, ports_json=$ports,
                    protocol=$proto, owner=$owner, criticality=$crit, internet_exposed=$exposed,
                    notes=$notes, updated_at=$updated";
            Bind(cmd, "$id", service.Id); Bind(cmd, "$name", service.Name); Bind(cmd, "$node", service.NodeId);
            Bind(cmd, "$url", service.Url); Bind(cmd, "$ports", JsonSerializer.Serialize(service.Ports));
            Bind(cmd, "$proto", service.Protocol); Bind(cmd, "$owner", service.Owner);
            Bind(cmd, "$crit", service.Criticality); Bind(cmd, "$exposed", service.InternetExposed ? 1 : 0);
            Bind(cmd, "$notes", service.Notes); Bind(cmd, "$created", service.CreatedAt); Bind(cmd, "$updated", service.UpdatedAt);
            cmd.ExecuteNonQuery();
        }
        RecordChange(new ChangeRecord
        {
            SubjectKind = "service", SubjectId = service.Id, ChangeKind = "updated",
            Summary = $"Service '{service.Name}' upserted", ChangedBy = changedBy, CreatedAt = now,
        });
    }

    public IReadOnlyList<ServiceRecord> ListServices()
    {
        var list = new List<ServiceRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, node_id, url, ports_json, protocol, owner, criticality, internet_exposed, notes, created_at, updated_at FROM services ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ServiceRecord
            {
                Id = r.GetString(0), Name = r.GetString(1),
                NodeId = r.IsDBNull(2) ? "" : r.GetString(2), Url = r.IsDBNull(3) ? "" : r.GetString(3),
                Ports = r.IsDBNull(4) ? new() : (JsonSerializer.Deserialize<List<int>>(r.GetString(4)) ?? new()),
                Protocol = r.IsDBNull(5) ? "" : r.GetString(5), Owner = r.IsDBNull(6) ? "" : r.GetString(6),
                Criticality = r.GetString(7), InternetExposed = r.GetInt64(8) != 0,
                Notes = r.IsDBNull(9) ? "" : r.GetString(9), CreatedAt = r.GetString(10), UpdatedAt = r.GetString(11),
            });
        }
        return list;
    }

    // ---- Events / changes / health -------------------------------------------------------------

    public void RecordEvent(HomelabEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.CreatedAt)) evt.CreatedAt = AnthillTime.NowUtc().ToIso();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO homelab_events (id, event_type, subject_kind, subject_id, severity, message, mission_id, created_at)
                VALUES ($id, $type, $skind, $sid, $sev, $msg, $mission, $created)";
            Bind(cmd, "$id", evt.Id); Bind(cmd, "$type", evt.EventType); Bind(cmd, "$skind", evt.SubjectKind);
            Bind(cmd, "$sid", evt.SubjectId); Bind(cmd, "$sev", evt.Severity); Bind(cmd, "$msg", evt.Message);
            Bind(cmd, "$mission", evt.MissionId); Bind(cmd, "$created", evt.CreatedAt);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<HomelabEvent> RecentEvents(int limit = 50)
    {
        var list = new List<HomelabEvent>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, event_type, subject_kind, subject_id, severity, message, mission_id, created_at FROM homelab_events ORDER BY created_at DESC LIMIT $limit";
        Bind(cmd, "$limit", Math.Clamp(limit, 1, 500));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new HomelabEvent
            {
                Id = r.GetString(0), EventType = r.GetString(1),
                SubjectKind = r.IsDBNull(2) ? "" : r.GetString(2), SubjectId = r.IsDBNull(3) ? "" : r.GetString(3),
                Severity = r.GetString(4), Message = r.IsDBNull(5) ? "" : r.GetString(5),
                MissionId = r.IsDBNull(6) ? "" : r.GetString(6), CreatedAt = r.GetString(7),
            });
        }
        return list;
    }

    public void RecordChange(ChangeRecord change)
    {
        if (string.IsNullOrWhiteSpace(change.CreatedAt)) change.CreatedAt = AnthillTime.NowUtc().ToIso();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO change_log (id, subject_kind, subject_id, change_kind, summary, changed_by, mission_id, created_at)
                VALUES ($id, $skind, $sid, $ckind, $summary, $by, $mission, $created)";
            Bind(cmd, "$id", change.Id); Bind(cmd, "$skind", change.SubjectKind); Bind(cmd, "$sid", change.SubjectId);
            Bind(cmd, "$ckind", change.ChangeKind); Bind(cmd, "$summary", change.Summary);
            Bind(cmd, "$by", change.ChangedBy); Bind(cmd, "$mission", change.MissionId); Bind(cmd, "$created", change.CreatedAt);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ChangeRecord> RecentChanges(int limit = 50)
    {
        var list = new List<ChangeRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, subject_kind, subject_id, change_kind, summary, changed_by, mission_id, created_at FROM change_log ORDER BY created_at DESC LIMIT $limit";
        Bind(cmd, "$limit", Math.Clamp(limit, 1, 500));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ChangeRecord
            {
                Id = r.GetString(0), SubjectKind = r.GetString(1), SubjectId = r.GetString(2),
                ChangeKind = r.GetString(3), Summary = r.IsDBNull(4) ? "" : r.GetString(4),
                ChangedBy = r.IsDBNull(5) ? "" : r.GetString(5), MissionId = r.IsDBNull(6) ? "" : r.GetString(6),
                CreatedAt = r.GetString(7),
            });
        }
        return list;
    }

    public void SaveHealthResult(HealthCheckResult result)
    {
        if (string.IsNullOrWhiteSpace(result.CheckedAt)) result.CheckedAt = AnthillTime.NowUtc().ToIso();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO health_checks (id, check_kind, target, service_id, node_id, status, latency_ms, detail, checked_at)
                VALUES ($id, $kind, $target, $service, $node, $status, $latency, $detail, $checked)";
            Bind(cmd, "$id", result.Id); Bind(cmd, "$kind", result.CheckKind); Bind(cmd, "$target", result.Target);
            Bind(cmd, "$service", result.ServiceId); Bind(cmd, "$node", result.NodeId); Bind(cmd, "$status", result.Status);
            Bind(cmd, "$latency", result.LatencyMs); Bind(cmd, "$detail", result.Detail); Bind(cmd, "$checked", result.CheckedAt);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<HealthCheckResult> RecentHealthResults(int limit = 50)
    {
        var list = new List<HealthCheckResult>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, check_kind, target, service_id, node_id, status, latency_ms, detail, checked_at FROM health_checks ORDER BY checked_at DESC LIMIT $limit";
        Bind(cmd, "$limit", Math.Clamp(limit, 1, 500));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new HealthCheckResult
            {
                Id = r.GetString(0), CheckKind = r.GetString(1), Target = r.GetString(2),
                ServiceId = r.IsDBNull(3) ? "" : r.GetString(3), NodeId = r.IsDBNull(4) ? "" : r.GetString(4),
                Status = r.GetString(5), LatencyMs = r.GetDouble(6),
                Detail = r.IsDBNull(7) ? "" : r.GetString(7), CheckedAt = r.GetString(8),
            });
        }
        return list;
    }

    // ---- Dependencies (v1.10.0) --------------------------------------------------------------------

    public void UpsertDependency(DependencyRecord dependency, string changedBy)
    {
        if (string.IsNullOrWhiteSpace(dependency.CreatedAt)) dependency.CreatedAt = AnthillTime.NowUtc().ToIso();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO dependencies (id, from_kind, from_id, to_kind, to_id, dependency_kind, notes, created_at)
                VALUES ($id, $fkind, $fid, $tkind, $tid, $dkind, $notes, $created)
                ON CONFLICT(id) DO UPDATE SET from_kind=$fkind, from_id=$fid, to_kind=$tkind,
                    to_id=$tid, dependency_kind=$dkind, notes=$notes";
            Bind(cmd, "$id", dependency.Id); Bind(cmd, "$fkind", dependency.FromKind); Bind(cmd, "$fid", dependency.FromId);
            Bind(cmd, "$tkind", dependency.ToKind); Bind(cmd, "$tid", dependency.ToId);
            Bind(cmd, "$dkind", dependency.DependencyKind); Bind(cmd, "$notes", dependency.Notes);
            Bind(cmd, "$created", dependency.CreatedAt);
            cmd.ExecuteNonQuery();
        }
        RecordChange(new ChangeRecord
        {
            SubjectKind = "dependency", SubjectId = dependency.Id, ChangeKind = "updated",
            Summary = $"Dependency {dependency.FromKind}:{dependency.FromId} -{dependency.DependencyKind}-> {dependency.ToKind}:{dependency.ToId}",
            ChangedBy = changedBy,
        });
    }

    public void RemoveDependency(string id, string removedBy)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM dependencies WHERE id = $id";
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
        RecordChange(new ChangeRecord
        {
            SubjectKind = "dependency", SubjectId = id, ChangeKind = "removed",
            Summary = "Dependency removed", ChangedBy = removedBy,
        });
    }

    public IReadOnlyList<DependencyRecord> ListDependencies()
    {
        var list = new List<DependencyRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, from_kind, from_id, to_kind, to_id, dependency_kind, notes, created_at FROM dependencies ORDER BY created_at";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new DependencyRecord
            {
                Id = r.GetString(0), FromKind = r.GetString(1), FromId = r.GetString(2),
                ToKind = r.GetString(3), ToId = r.GetString(4), DependencyKind = r.GetString(5),
                Notes = r.IsDBNull(6) ? "" : r.GetString(6), CreatedAt = r.GetString(7),
            });
        }
        return list;
    }

    // ---- Inventory import/export (v1.10.0) --------------------------------------------------------

    public HomelabInventoryExport ExportInventory() => new()
    {
        ExportedAt = AnthillTime.NowUtc().ToIso(),
        AnthillVersion = AnthillRuntime.Version,
        Nodes = ListNodes().ToList(),
        Services = ListServices().ToList(),
        Dependencies = ListDependencies().ToList(),
    };

    /// <summary>
    /// Imports an inventory bundle by upserting every record (same id = update, new id = insert),
    /// so re-importing an export is idempotent. Each upsert writes its own ChangeRecord; a summary
    /// "imported" ChangeRecord closes the batch. Never touches credentials or the allowlist.
    /// </summary>
    public (int Nodes, int Services, int Dependencies) ImportInventory(HomelabInventoryExport bundle, string importedBy)
    {
        var nodes = 0; var services = 0; var deps = 0;
        foreach (var node in bundle.Nodes.Where(n => !string.IsNullOrWhiteSpace(n.Name)))
        {
            UpsertNode(node, importedBy); nodes++;
        }
        foreach (var service in bundle.Services.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
        {
            UpsertService(service, importedBy); services++;
        }
        foreach (var dependency in bundle.Dependencies.Where(d => !string.IsNullOrWhiteSpace(d.FromId) && !string.IsNullOrWhiteSpace(d.ToId)))
        {
            UpsertDependency(dependency, importedBy); deps++;
        }
        RecordChange(new ChangeRecord
        {
            SubjectKind = "inventory", SubjectId = "import", ChangeKind = "imported",
            Summary = $"Inventory import: {nodes} node(s), {services} service(s), {deps} dependency(ies)",
            ChangedBy = importedBy,
        });
        return (nodes, services, deps);
    }

    // ---- Target allowlist (D1) -------------------------------------------------------------------

    public void AddAllowlistEntry(TargetAllowlistRecord entry)
    {
        if (string.IsNullOrWhiteSpace(entry.CreatedAt)) entry.CreatedAt = AnthillTime.NowUtc().ToIso();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO homelab_target_allowlist (id, target, note, enabled, added_by, created_at)
                VALUES ($id, $target, $note, $enabled, $by, $created)
                ON CONFLICT(id) DO UPDATE SET target=$target, note=$note, enabled=$enabled";
            Bind(cmd, "$id", entry.Id); Bind(cmd, "$target", entry.Target.Trim()); Bind(cmd, "$note", entry.Note);
            Bind(cmd, "$enabled", entry.Enabled ? 1 : 0); Bind(cmd, "$by", entry.AddedBy); Bind(cmd, "$created", entry.CreatedAt);
            cmd.ExecuteNonQuery();
        }
        RecordChange(new ChangeRecord
        {
            SubjectKind = "allowlist", SubjectId = entry.Id, ChangeKind = "created",
            Summary = $"Allowlist target '{entry.Target}' added", ChangedBy = entry.AddedBy,
        });
    }

    public void RemoveAllowlistEntry(string id, string removedBy)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM homelab_target_allowlist WHERE id = $id";
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
        RecordChange(new ChangeRecord
        {
            SubjectKind = "allowlist", SubjectId = id, ChangeKind = "removed",
            Summary = "Allowlist entry removed", ChangedBy = removedBy,
        });
    }

    public IReadOnlyList<TargetAllowlistRecord> ListAllowlist()
    {
        var list = new List<TargetAllowlistRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, target, note, enabled, added_by, created_at FROM homelab_target_allowlist ORDER BY created_at";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TargetAllowlistRecord
            {
                Id = r.GetString(0), Target = r.GetString(1), Note = r.IsDBNull(2) ? "" : r.GetString(2),
                Enabled = r.GetInt64(3) != 0, AddedBy = r.IsDBNull(4) ? "" : r.GetString(4), CreatedAt = r.GetString(5),
            });
        }
        return list;
    }

    // ---- Credentials (rows only — encryption/audit lives in HomelabCredentialStore) --------------

    internal void UpsertCredentialRow(string id, string kind, string targetHost, string secretProtected)
    {
        var now = AnthillTime.NowUtc().ToIso();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO homelab_credentials (id, kind, target_host, secret_protected, last_verified, created_at, updated_at)
                VALUES ($id, $kind, $host, $secret, NULL, $now, $now)
                ON CONFLICT(id) DO UPDATE SET kind=$kind, target_host=$host, secret_protected=$secret, updated_at=$now";
            Bind(cmd, "$id", id); Bind(cmd, "$kind", kind); Bind(cmd, "$host", targetHost);
            Bind(cmd, "$secret", secretProtected); Bind(cmd, "$now", now);
            cmd.ExecuteNonQuery();
        }
    }

    internal string? GetCredentialSecretProtected(string id)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT secret_protected FROM homelab_credentials WHERE id = $id";
        Bind(cmd, "$id", id);
        var value = cmd.ExecuteScalar();
        return value is string s && s.Length > 0 ? s : null;
    }

    internal void SetCredentialVerified(string id)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE homelab_credentials SET last_verified = $now, updated_at = $now WHERE id = $id";
            Bind(cmd, "$id", id); Bind(cmd, "$now", AnthillTime.NowUtc().ToIso());
            cmd.ExecuteNonQuery();
        }
    }

    internal void DeleteCredentialRow(string id)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM homelab_credentials WHERE id = $id";
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    internal IReadOnlyList<CredentialRecord> ListCredentialRows()
    {
        var list = new List<CredentialRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        // Deliberately never selects secret_protected — statuses are secret-free by construction.
        cmd.CommandText = "SELECT id, kind, target_host, secret_protected IS NOT NULL AND secret_protected != '', last_verified, created_at, updated_at FROM homelab_credentials ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new CredentialRecord
            {
                Id = r.GetString(0), Kind = r.GetString(1), TargetHost = r.IsDBNull(2) ? "" : r.GetString(2),
                Configured = r.GetInt64(3) != 0, LastVerified = r.IsDBNull(4) ? "" : r.GetString(4),
                CreatedAt = r.GetString(5), UpdatedAt = r.GetString(6),
            });
        }
        return list;
    }

    // ---- Scheduler job state (D4) -----------------------------------------------------------------

    public void RecordJobRun(string jobName, bool ok, string message)
    {
        var now = AnthillTime.NowUtc().ToIso();
        var value = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["last_run"] = now,
            ["last_result"] = (ok ? "ok" : "failed") + (string.IsNullOrWhiteSpace(message) ? "" : $": {message}"),
        });
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO homelab_meta (key, value, updated_at) VALUES ($key, $value, $now)
                ON CONFLICT(key) DO UPDATE SET value=$value, updated_at=$now";
            Bind(cmd, "$key", $"job:{jobName}"); Bind(cmd, "$value", value); Bind(cmd, "$now", now);
            cmd.ExecuteNonQuery();
        }
    }

    public (string LastRun, string LastResult)? GetJobState(string jobName)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM homelab_meta WHERE key = $key";
        Bind(cmd, "$key", $"job:{jobName}");
        if (cmd.ExecuteScalar() is not string raw || raw.Length == 0) return null;
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
        if (parsed is null) return null;
        return (parsed.GetValueOrDefault("last_run", ""), parsed.GetValueOrDefault("last_result", ""));
    }

    // ---- Summary -----------------------------------------------------------------------------------

    public Dictionary<string, long> TableCounts()
    {
        var counts = new Dictionary<string, long>();
        using var conn = Connect();
        foreach (var table in TableNames)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            counts[table] = (long)(cmd.ExecuteScalar() ?? 0L);
        }
        return counts;
    }
}
