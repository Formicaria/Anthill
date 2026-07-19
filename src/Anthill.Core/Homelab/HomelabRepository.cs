using System.Text.Json;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Homelab.Approvals;
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
        "health_check_schedules", "action_proposals", "node_metrics", "arr_apps",
        "automation_rules", "automation_runs", // v2.5.0 Phase 14
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
        @"CREATE TABLE IF NOT EXISTS automation_rules (
            id TEXT PRIMARY KEY, name TEXT NOT NULL, trigger_kind TEXT NOT NULL, target TEXT,
            threshold INTEGER NOT NULL DEFAULT 3, action_kind TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 0, cooldown_minutes INTEGER NOT NULL DEFAULT 60,
            max_runs_per_day INTEGER NOT NULL DEFAULT 3, updated_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS automation_runs (
            id TEXT PRIMARY KEY, rule_id TEXT NOT NULL, rule_name TEXT, trigger_detail TEXT,
            action_taken TEXT, outcome TEXT NOT NULL, fired_at TEXT NOT NULL)",
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
        @"CREATE TABLE IF NOT EXISTS health_check_schedules (
            id TEXT PRIMARY KEY, check_kind TEXT NOT NULL, target TEXT NOT NULL,
            service_id TEXT, node_id TEXT, enabled INTEGER NOT NULL DEFAULT 1,
            timeout_ms INTEGER DEFAULT 0, created_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS action_proposals (
            id TEXT PRIMARY KEY, title TEXT NOT NULL, summary TEXT, action_type TEXT NOT NULL,
            target_kind TEXT, target_id TEXT NOT NULL, state TEXT NOT NULL DEFAULT 'pending',
            risk_level TEXT NOT NULL DEFAULT 'high', dedupe_key TEXT NOT NULL,
            dependency_fanout INTEGER DEFAULT 0, service_criticality TEXT,
            backup_covered INTEGER NOT NULL DEFAULT 0, internet_exposed INTEGER NOT NULL DEFAULT 0,
            rollback_note TEXT, dry_run_available INTEGER NOT NULL DEFAULT 0, payload TEXT,
            blast_radius_score INTEGER DEFAULT 0, blast_radius_explanation TEXT,
            requested_by TEXT, created_at TEXT NOT NULL,
            decided_by TEXT, decided_at TEXT, executed_by TEXT, executed_at TEXT, execution_result TEXT)",
        @"CREATE INDEX IF NOT EXISTS idx_action_proposals_state ON action_proposals(state, created_at)",
        @"CREATE TABLE IF NOT EXISTS node_metrics (
            node_id TEXT PRIMARY KEY, node_name TEXT NOT NULL, source TEXT NOT NULL,
            cpu_percent REAL DEFAULT -1, cpu_cores INTEGER DEFAULT 0,
            mem_used_bytes INTEGER DEFAULT -1, mem_total_bytes INTEGER DEFAULT -1,
            disk_used_bytes INTEGER DEFAULT -1, disk_total_bytes INTEGER DEFAULT -1,
            uptime_seconds INTEGER DEFAULT 0, updated_at TEXT NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS arr_apps (
            id TEXT PRIMARY KEY, kind TEXT NOT NULL, name TEXT NOT NULL, url TEXT NOT NULL,
            credential_id TEXT NOT NULL, enabled INTEGER NOT NULL DEFAULT 1,
            status TEXT NOT NULL DEFAULT 'unknown', version TEXT, health_warnings INTEGER DEFAULT 0,
            queue_count INTEGER DEFAULT -1, last_message TEXT, last_checked TEXT)",
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

    // ---- Virtualization + storage inventory (v1.12.0, Proxmox read-only sync) --------------------

    public void UpsertVm(VmRecord vm)
    {
        vm.UpdatedAt = AnthillTime.NowUtc().ToIso();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO vm_inventory (id, vm_id, name, node_id, status, cpu_cores, memory_mb, uptime_seconds, updated_at)
                VALUES ($id, $vmid, $name, $node, $status, $cpu, $mem, $uptime, $updated)
                ON CONFLICT(id) DO UPDATE SET vm_id=$vmid, name=$name, node_id=$node, status=$status,
                    cpu_cores=$cpu, memory_mb=$mem, uptime_seconds=$uptime, updated_at=$updated";
            Bind(cmd, "$id", vm.Id); Bind(cmd, "$vmid", vm.VmId); Bind(cmd, "$name", vm.Name);
            Bind(cmd, "$node", vm.NodeId); Bind(cmd, "$status", vm.Status); Bind(cmd, "$cpu", vm.CpuCores);
            Bind(cmd, "$mem", vm.MemoryMb); Bind(cmd, "$uptime", vm.UptimeSeconds); Bind(cmd, "$updated", vm.UpdatedAt);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<VmRecord> ListVms()
    {
        var list = new List<VmRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, vm_id, name, node_id, status, cpu_cores, memory_mb, uptime_seconds, updated_at FROM vm_inventory ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new VmRecord
            {
                Id = r.GetString(0), VmId = r.IsDBNull(1) ? "" : r.GetString(1), Name = r.GetString(2),
                NodeId = r.IsDBNull(3) ? "" : r.GetString(3), Status = r.IsDBNull(4) ? "" : r.GetString(4),
                CpuCores = (int)r.GetInt64(5), MemoryMb = r.GetInt64(6), UptimeSeconds = r.GetInt64(7),
                UpdatedAt = r.GetString(8),
            });
        }
        return list;
    }

    public void UpsertContainer(ContainerRecord container)
    {
        container.UpdatedAt = AnthillTime.NowUtc().ToIso();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO container_inventory (id, container_id, kind, name, node_id, status, updated_at)
                VALUES ($id, $cid, $kind, $name, $node, $status, $updated)
                ON CONFLICT(id) DO UPDATE SET container_id=$cid, kind=$kind, name=$name, node_id=$node,
                    status=$status, updated_at=$updated";
            Bind(cmd, "$id", container.Id); Bind(cmd, "$cid", container.ContainerId); Bind(cmd, "$kind", container.Kind);
            Bind(cmd, "$name", container.Name); Bind(cmd, "$node", container.NodeId);
            Bind(cmd, "$status", container.Status); Bind(cmd, "$updated", container.UpdatedAt);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ContainerRecord> ListContainers()
    {
        var list = new List<ContainerRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, container_id, kind, name, node_id, status, updated_at FROM container_inventory ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ContainerRecord
            {
                Id = r.GetString(0), ContainerId = r.IsDBNull(1) ? "" : r.GetString(1), Kind = r.GetString(2),
                Name = r.GetString(3), NodeId = r.IsDBNull(4) ? "" : r.GetString(4),
                Status = r.IsDBNull(5) ? "" : r.GetString(5), UpdatedAt = r.GetString(6),
            });
        }
        return list;
    }

    // ---- Automation (v2.5.0 Phase 14) ----------------------------------------------------------

    public void UpsertAutomationRule(Automation.AutomationRule r)
    {
        r.UpdatedAt = AnthillTime.NowUtc().ToIso();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO automation_rules (id, name, trigger_kind, target, threshold, action_kind, enabled, cooldown_minutes, max_runs_per_day, updated_at)
                VALUES ($id,$name,$tk,$target,$th,$ak,$en,$cd,$cap,$updated)
                ON CONFLICT(id) DO UPDATE SET name=$name, trigger_kind=$tk, target=$target, threshold=$th,
                    action_kind=$ak, enabled=$en, cooldown_minutes=$cd, max_runs_per_day=$cap, updated_at=$updated";
            Bind(cmd, "$id", r.Id); Bind(cmd, "$name", r.Name); Bind(cmd, "$tk", r.TriggerKind);
            Bind(cmd, "$target", r.Target); Bind(cmd, "$th", r.Threshold); Bind(cmd, "$ak", r.ActionKind);
            Bind(cmd, "$en", r.Enabled ? 1 : 0); Bind(cmd, "$cd", r.CooldownMinutes);
            Bind(cmd, "$cap", r.MaxRunsPerDay); Bind(cmd, "$updated", r.UpdatedAt);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<Automation.AutomationRule> ListAutomationRules()
    {
        var list = new List<Automation.AutomationRule>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, trigger_kind, target, threshold, action_kind, enabled, cooldown_minutes, max_runs_per_day, updated_at FROM automation_rules ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Automation.AutomationRule
            {
                Id = r.GetString(0), Name = r.GetString(1), TriggerKind = r.GetString(2),
                Target = r.IsDBNull(3) ? "" : r.GetString(3), Threshold = (int)r.GetInt64(4),
                ActionKind = r.GetString(5), Enabled = r.GetInt64(6) == 1,
                CooldownMinutes = (int)r.GetInt64(7), MaxRunsPerDay = (int)r.GetInt64(8),
                UpdatedAt = r.GetString(9),
            });
        }
        return list;
    }

    public void RecordAutomationRun(Automation.AutomationRun run)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT OR IGNORE INTO automation_runs (id, rule_id, rule_name, trigger_detail, action_taken, outcome, fired_at)
                VALUES ($id,$rid,$rname,$detail,$action,$outcome,$fired)";
            Bind(cmd, "$id", run.Id); Bind(cmd, "$rid", run.RuleId); Bind(cmd, "$rname", run.RuleName);
            Bind(cmd, "$detail", run.TriggerDetail); Bind(cmd, "$action", run.ActionTaken);
            Bind(cmd, "$outcome", run.Outcome); Bind(cmd, "$fired", run.FiredAt);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<Automation.AutomationRun> ListAutomationRuns(int limit = 100)
    {
        var list = new List<Automation.AutomationRun>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, rule_id, rule_name, trigger_detail, action_taken, outcome, fired_at FROM automation_runs ORDER BY fired_at DESC, id DESC LIMIT $limit";
        Bind(cmd, "$limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Automation.AutomationRun
            {
                Id = r.GetString(0), RuleId = r.GetString(1), RuleName = r.IsDBNull(2) ? "" : r.GetString(2),
                TriggerDetail = r.IsDBNull(3) ? "" : r.GetString(3), ActionTaken = r.IsDBNull(4) ? "" : r.GetString(4),
                Outcome = r.GetString(5), FiredAt = r.GetString(6),
            });
        }
        return list;
    }

    // ---- Backups (v2.4.0 Phase 13: the backup_inventory table finally gets its accessors) ------

    public void UpsertBackup(BackupRecord b)
    {
        b.UpdatedAt = AnthillTime.NowUtc().ToIso();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO backup_inventory (id, target_kind, target_id, location, status, last_success, last_attempt, size_bytes, notes, updated_at)
                VALUES ($id, $tk, $tid, $loc, $status, $ls, $la, $size, $notes, $updated)
                ON CONFLICT(id) DO UPDATE SET target_kind=$tk, target_id=$tid, location=$loc, status=$status,
                    last_success=$ls, last_attempt=$la, size_bytes=$size, notes=$notes, updated_at=$updated";
            Bind(cmd, "$id", b.Id); Bind(cmd, "$tk", b.TargetKind); Bind(cmd, "$tid", b.TargetId);
            Bind(cmd, "$loc", b.Location); Bind(cmd, "$status", b.Status); Bind(cmd, "$ls", b.LastSuccess);
            Bind(cmd, "$la", b.LastAttempt); Bind(cmd, "$size", b.SizeBytes); Bind(cmd, "$notes", b.Notes);
            Bind(cmd, "$updated", b.UpdatedAt);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<BackupRecord> ListBackups()
    {
        var list = new List<BackupRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, target_kind, target_id, location, status, last_success, last_attempt, size_bytes, notes, updated_at FROM backup_inventory ORDER BY target_kind, target_id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new BackupRecord
            {
                Id = r.GetString(0), TargetKind = r.GetString(1), TargetId = r.GetString(2),
                Location = r.IsDBNull(3) ? "" : r.GetString(3), Status = r.GetString(4),
                LastSuccess = r.IsDBNull(5) ? "" : r.GetString(5), LastAttempt = r.IsDBNull(6) ? "" : r.GetString(6),
                SizeBytes = r.IsDBNull(7) ? 0 : r.GetInt64(7), Notes = r.IsDBNull(8) ? "" : r.GetString(8),
                UpdatedAt = r.GetString(9),
            });
        }
        return list;
    }

    public void UpsertStoragePool(StoragePoolRecord pool)
    {
        pool.UpdatedAt = AnthillTime.NowUtc().ToIso();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO storage_inventory (id, entry_kind, name, node_id, kind, total_bytes, used_bytes, updated_at)
                VALUES ($id, 'pool', $name, $node, $kind, $total, $used, $updated)
                ON CONFLICT(id) DO UPDATE SET name=$name, node_id=$node, kind=$kind,
                    total_bytes=$total, used_bytes=$used, updated_at=$updated";
            Bind(cmd, "$id", pool.Id); Bind(cmd, "$name", pool.Name); Bind(cmd, "$node", pool.NodeId);
            Bind(cmd, "$kind", pool.Kind); Bind(cmd, "$total", pool.TotalBytes); Bind(cmd, "$used", pool.UsedBytes);
            Bind(cmd, "$updated", pool.UpdatedAt);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<StoragePoolRecord> ListStoragePools()
    {
        var list = new List<StoragePoolRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, node_id, kind, total_bytes, used_bytes, updated_at FROM storage_inventory WHERE entry_kind = 'pool' ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new StoragePoolRecord
            {
                Id = r.GetString(0), Name = r.GetString(1), NodeId = r.IsDBNull(2) ? "" : r.GetString(2),
                Kind = r.IsDBNull(3) ? "" : r.GetString(3), TotalBytes = r.GetInt64(4), UsedBytes = r.GetInt64(5),
                UpdatedAt = r.GetString(6),
            });
        }
        return list;
    }

    // ---- Incidents (v1.14.0) -----------------------------------------------------------------------

    public void OpenIncident(IncidentRecord incident, string openedBy)
    {
        if (string.IsNullOrWhiteSpace(incident.OpenedAt)) incident.OpenedAt = AnthillTime.NowUtc().ToIso();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO incidents (id, title, status, severity, subject_kind, subject_id, root_cause, opened_at, resolved_at)
                VALUES ($id, $title, $status, $sev, $skind, $sid, $root, $opened, NULL)
                ON CONFLICT(id) DO NOTHING";
            Bind(cmd, "$id", incident.Id); Bind(cmd, "$title", incident.Title); Bind(cmd, "$status", incident.Status);
            Bind(cmd, "$sev", incident.Severity); Bind(cmd, "$skind", incident.SubjectKind); Bind(cmd, "$sid", incident.SubjectId);
            Bind(cmd, "$root", incident.RootCause); Bind(cmd, "$opened", incident.OpenedAt);
            cmd.ExecuteNonQuery();
        }
        RecordEvent(new HomelabEvent
        {
            EventType = "incident_opened", SubjectKind = "incident", SubjectId = incident.Id,
            Severity = incident.Severity, Message = $"{incident.Title} (subject: {incident.SubjectId}, by {openedBy})",
        });
        RecordChange(new ChangeRecord
        {
            SubjectKind = "incident", SubjectId = incident.Id, ChangeKind = "created",
            Summary = $"Incident opened: {incident.Title}", ChangedBy = openedBy,
        });
    }

    public IncidentRecord? GetIncident(string id) => ListIncidents().FirstOrDefault(i => i.Id == id);

    public void SetIncidentStatus(string id, string status, string rootCause, string changedBy)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE incidents SET status = $status,
                root_cause = CASE WHEN $root != '' THEN $root ELSE root_cause END,
                resolved_at = CASE WHEN $status = 'resolved' THEN $now ELSE NULL END
                WHERE id = $id";
            Bind(cmd, "$id", id); Bind(cmd, "$status", status); Bind(cmd, "$root", rootCause ?? "");
            Bind(cmd, "$now", AnthillTime.NowUtc().ToIso());
            cmd.ExecuteNonQuery();
        }
        RecordChange(new ChangeRecord
        {
            SubjectKind = "incident", SubjectId = id, ChangeKind = "updated",
            Summary = $"Incident marked {status}" + (string.IsNullOrWhiteSpace(rootCause) ? "" : $" — {rootCause}"),
            ChangedBy = changedBy,
        });
    }

    public IReadOnlyList<IncidentRecord> ListIncidents()
    {
        var list = new List<IncidentRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, status, severity, subject_kind, subject_id, root_cause, opened_at, resolved_at FROM incidents ORDER BY opened_at DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new IncidentRecord
            {
                Id = r.GetString(0), Title = r.GetString(1), Status = r.GetString(2), Severity = r.GetString(3),
                SubjectKind = r.IsDBNull(4) ? "" : r.GetString(4), SubjectId = r.IsDBNull(5) ? "" : r.GetString(5),
                RootCause = r.IsDBNull(6) ? "" : r.GetString(6),
                OpenedAt = r.GetString(7), ResolvedAt = r.IsDBNull(8) ? "" : r.GetString(8),
            });
        }
        return list;
    }

    // ---- Network devices + risk findings (v1.13.0) ------------------------------------------------

    public void UpsertNetworkDevice(NetworkDevice device, string changedBy)
    {
        var now = AnthillTime.NowUtc().ToIso();
        if (string.IsNullOrWhiteSpace(device.FirstSeen)) device.FirstSeen = now;
        device.LastSeen = now;
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO network_devices (id, name, kind, mac, ip, vlan, known, notes, first_seen, last_seen)
                VALUES ($id, $name, $kind, $mac, $ip, $vlan, $known, $notes, $first, $last)
                ON CONFLICT(id) DO UPDATE SET name=$name, kind=$kind, mac=$mac, ip=$ip, vlan=$vlan,
                    known=$known, notes=$notes, last_seen=$last";
            Bind(cmd, "$id", device.Id); Bind(cmd, "$name", device.Name); Bind(cmd, "$kind", device.Kind);
            Bind(cmd, "$mac", device.Mac); Bind(cmd, "$ip", device.Ip); Bind(cmd, "$vlan", device.Vlan);
            Bind(cmd, "$known", device.Known ? 1 : 0); Bind(cmd, "$notes", device.Notes);
            Bind(cmd, "$first", device.FirstSeen); Bind(cmd, "$last", device.LastSeen);
            cmd.ExecuteNonQuery();
        }
        RecordChange(new ChangeRecord
        {
            SubjectKind = "network_device", SubjectId = device.Id, ChangeKind = "updated",
            Summary = $"Device '{(device.Name.Length > 0 ? device.Name : device.Mac)}' saved (known={device.Known})",
            ChangedBy = changedBy,
        });
    }

    public void RemoveNetworkDevice(string id, string removedBy)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM network_devices WHERE id = $id";
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
        RecordChange(new ChangeRecord
        {
            SubjectKind = "network_device", SubjectId = id, ChangeKind = "removed",
            Summary = "Network device removed", ChangedBy = removedBy,
        });
    }

    public IReadOnlyList<NetworkDevice> ListNetworkDevices()
    {
        var list = new List<NetworkDevice>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, kind, mac, ip, vlan, known, notes, first_seen, last_seen FROM network_devices ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new NetworkDevice
            {
                Id = r.GetString(0), Name = r.IsDBNull(1) ? "" : r.GetString(1), Kind = r.GetString(2),
                Mac = r.IsDBNull(3) ? "" : r.GetString(3), Ip = r.IsDBNull(4) ? "" : r.GetString(4),
                Vlan = r.IsDBNull(5) ? "" : r.GetString(5), Known = r.GetInt64(6) != 0,
                Notes = r.IsDBNull(7) ? "" : r.GetString(7),
                FirstSeen = r.IsDBNull(8) ? "" : r.GetString(8), LastSeen = r.IsDBNull(9) ? "" : r.GetString(9),
            });
        }
        return list;
    }

    public void UpsertRiskRecord(RiskRecord record)
    {
        var now = AnthillTime.NowUtc().ToIso();
        if (string.IsNullOrWhiteSpace(record.CreatedAt)) record.CreatedAt = now;
        record.UpdatedAt = now;
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO risk_records (id, finding_kind, subject_kind, subject_id, severity, summary, status, created_at, updated_at)
                VALUES ($id, $kind, $skind, $sid, $sev, $summary, $status, $created, $updated)
                ON CONFLICT(id) DO UPDATE SET finding_kind=$kind, subject_kind=$skind, subject_id=$sid,
                    severity=$sev, summary=$summary, status=$status, updated_at=$updated";
            Bind(cmd, "$id", record.Id); Bind(cmd, "$kind", record.FindingKind); Bind(cmd, "$skind", record.SubjectKind);
            Bind(cmd, "$sid", record.SubjectId); Bind(cmd, "$sev", record.Severity); Bind(cmd, "$summary", record.Summary);
            Bind(cmd, "$status", record.Status); Bind(cmd, "$created", record.CreatedAt); Bind(cmd, "$updated", record.UpdatedAt);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetRiskStatus(string id, string status, string changedBy)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE risk_records SET status = $status, updated_at = $now WHERE id = $id";
            Bind(cmd, "$id", id); Bind(cmd, "$status", status); Bind(cmd, "$now", AnthillTime.NowUtc().ToIso());
            cmd.ExecuteNonQuery();
        }
        RecordChange(new ChangeRecord
        {
            SubjectKind = "risk", SubjectId = id, ChangeKind = "updated",
            Summary = $"Risk finding marked {status}", ChangedBy = changedBy,
        });
    }

    public IReadOnlyList<RiskRecord> ListRiskRecords()
    {
        var list = new List<RiskRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, finding_kind, subject_kind, subject_id, severity, summary, status, created_at, updated_at FROM risk_records ORDER BY severity, updated_at DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new RiskRecord
            {
                Id = r.GetString(0), FindingKind = r.GetString(1),
                SubjectKind = r.IsDBNull(2) ? "" : r.GetString(2), SubjectId = r.IsDBNull(3) ? "" : r.GetString(3),
                Severity = r.GetString(4), Summary = r.IsDBNull(5) ? "" : r.GetString(5),
                Status = r.GetString(6), CreatedAt = r.GetString(7), UpdatedAt = r.GetString(8),
            });
        }
        return list;
    }

    // ---- Action proposals (v2.3.0, NORTH_STAR Phase 12) ----------------------------------------

    private const string ActionProposalColumns =
        "id, title, summary, action_type, target_kind, target_id, state, risk_level, dedupe_key, " +
        "dependency_fanout, service_criticality, backup_covered, internet_exposed, rollback_note, " +
        "dry_run_available, payload, blast_radius_score, blast_radius_explanation, requested_by, " +
        "created_at, decided_by, decided_at, executed_by, executed_at, execution_result";

    public void SaveActionProposal(ActionProposal p)
    {
        if (string.IsNullOrWhiteSpace(p.CreatedAt)) p.CreatedAt = AnthillTime.NowUtc().ToIso();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"INSERT INTO action_proposals ({ActionProposalColumns})
                VALUES ($id,$title,$summary,$atype,$tkind,$tid,$state,$risk,$dedupe,$fanout,$crit,$backup,$exposed,$rollback,$dry,$payload,$score,$expl,$reqby,$created,$decby,$decat,$exby,$exat,$exres)";
            BindActionProposal(cmd, p);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateActionProposal(ActionProposal p)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE action_proposals SET title=$title, summary=$summary, action_type=$atype,
                target_kind=$tkind, target_id=$tid, state=$state, risk_level=$risk, dedupe_key=$dedupe,
                dependency_fanout=$fanout, service_criticality=$crit, backup_covered=$backup,
                internet_exposed=$exposed, rollback_note=$rollback, dry_run_available=$dry, payload=$payload,
                blast_radius_score=$score, blast_radius_explanation=$expl, requested_by=$reqby,
                created_at=$created, decided_by=$decby, decided_at=$decat, executed_by=$exby,
                executed_at=$exat, execution_result=$exres WHERE id=$id";
            BindActionProposal(cmd, p);
            cmd.ExecuteNonQuery();
        }
    }

    public ActionProposal? GetActionProposal(string id)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {ActionProposalColumns} FROM action_proposals WHERE id=$id";
        Bind(cmd, "$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadActionProposal(r) : null;
    }

    public IReadOnlyList<ActionProposal> ListActionProposals(int limit = 100)
    {
        var list = new List<ActionProposal>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        // v2.4.0: id tiebreaker — created_at is second-resolution ISO, so two proposals created in
        // the same tick had NONDETERMINISTIC order (source of the Windows-only supersede test flake).
        cmd.CommandText = $"SELECT {ActionProposalColumns} FROM action_proposals ORDER BY created_at DESC, id DESC LIMIT $limit";
        Bind(cmd, "$limit", Math.Clamp(limit, 1, 500));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadActionProposal(r));
        return list;
    }

    private static void BindActionProposal(SqliteCommand cmd, ActionProposal p)
    {
        Bind(cmd, "$id", p.ApprovableId); Bind(cmd, "$title", p.Title); Bind(cmd, "$summary", p.Summary);
        Bind(cmd, "$atype", p.ActionType); Bind(cmd, "$tkind", p.TargetKind); Bind(cmd, "$tid", p.TargetId);
        Bind(cmd, "$state", p.State); Bind(cmd, "$risk", p.RiskLevel); Bind(cmd, "$dedupe", p.DedupeKey);
        Bind(cmd, "$fanout", p.DependencyFanout); Bind(cmd, "$crit", p.ServiceCriticality);
        Bind(cmd, "$backup", p.BackupCovered ? 1 : 0); Bind(cmd, "$exposed", p.InternetExposed ? 1 : 0);
        Bind(cmd, "$rollback", p.RollbackNote); Bind(cmd, "$dry", p.DryRunAvailable ? 1 : 0);
        Bind(cmd, "$payload", p.Payload); Bind(cmd, "$score", p.BlastRadiusScore);
        Bind(cmd, "$expl", p.BlastRadiusExplanation); Bind(cmd, "$reqby", p.RequestedBy);
        Bind(cmd, "$created", p.CreatedAt); Bind(cmd, "$decby", p.DecidedBy); Bind(cmd, "$decat", p.DecidedAt);
        Bind(cmd, "$exby", p.ExecutedBy); Bind(cmd, "$exat", p.ExecutedAt); Bind(cmd, "$exres", p.ExecutionResult);
    }

    private static ActionProposal ReadActionProposal(SqliteDataReader r)
    {
        string S(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
        return new ActionProposal
        {
            ApprovableId = S(0), Title = S(1), Summary = S(2), ActionType = S(3), TargetKind = S(4),
            TargetId = S(5), State = S(6), RiskLevel = S(7), DedupeKey = S(8),
            DependencyFanout = r.IsDBNull(9) ? 0 : r.GetInt32(9), ServiceCriticality = S(10),
            BackupCovered = !r.IsDBNull(11) && r.GetInt32(11) == 1,
            InternetExposed = !r.IsDBNull(12) && r.GetInt32(12) == 1,
            RollbackNote = S(13), DryRunAvailable = !r.IsDBNull(14) && r.GetInt32(14) == 1,
            Payload = S(15), BlastRadiusScore = r.IsDBNull(16) ? 0 : r.GetInt32(16),
            BlastRadiusExplanation = S(17), RequestedBy = S(18), CreatedAt = S(19),
            DecidedBy = S(20), DecidedAt = S(21), ExecutedBy = S(22), ExecutedAt = S(23), ExecutionResult = S(24),
        };
    }

    // ---- Events / changes / health -------------------------------------------------------------

    public void RecordEvent(HomelabEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.CreatedAt)) evt.CreatedAt = AnthillTime.NowUtc().ToIso();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            // OR IGNORE: providers use stable ids (e.g. pve-task:<UPID>) so a re-sync can never
            // duplicate an already-recorded event — and never crashes the sync on a PK collision.
            cmd.CommandText = @"INSERT OR IGNORE INTO homelab_events (id, event_type, subject_kind, subject_id, severity, message, mission_id, created_at)
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

    // ---- Health-check schedules + per-target history (v1.11.0) ------------------------------------

    public IReadOnlyList<HealthCheckResult> RecentHealthResultsForTarget(string target, int limit = 10)
    {
        var list = new List<HealthCheckResult>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, check_kind, target, service_id, node_id, status, latency_ms, detail, checked_at
            FROM health_checks WHERE target = $target ORDER BY checked_at DESC LIMIT $limit";
        Bind(cmd, "$target", target); Bind(cmd, "$limit", Math.Clamp(limit, 1, 200));
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

    public void UpsertHealthSchedule(Anthill.Core.Health.HealthCheckSchedule schedule, string changedBy)
    {
        if (string.IsNullOrWhiteSpace(schedule.CreatedAt)) schedule.CreatedAt = AnthillTime.NowUtc().ToIso();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO health_check_schedules (id, check_kind, target, service_id, node_id, enabled, timeout_ms, created_at)
                VALUES ($id, $kind, $target, $service, $node, $enabled, $timeout, $created)
                ON CONFLICT(id) DO UPDATE SET check_kind=$kind, target=$target, service_id=$service,
                    node_id=$node, enabled=$enabled, timeout_ms=$timeout";
            Bind(cmd, "$id", schedule.Id); Bind(cmd, "$kind", schedule.CheckKind); Bind(cmd, "$target", schedule.Target);
            Bind(cmd, "$service", schedule.ServiceId); Bind(cmd, "$node", schedule.NodeId);
            Bind(cmd, "$enabled", schedule.Enabled ? 1 : 0); Bind(cmd, "$timeout", schedule.TimeoutMs);
            Bind(cmd, "$created", schedule.CreatedAt);
            cmd.ExecuteNonQuery();
        }
        RecordChange(new ChangeRecord
        {
            SubjectKind = "health_check", SubjectId = schedule.Id, ChangeKind = "updated",
            Summary = $"Health check '{schedule.CheckKind} {schedule.Target}' saved", ChangedBy = changedBy,
        });
    }

    public void RemoveHealthSchedule(string id, string removedBy)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM health_check_schedules WHERE id = $id";
            Bind(cmd, "$id", id);
            cmd.ExecuteNonQuery();
        }
        RecordChange(new ChangeRecord
        {
            SubjectKind = "health_check", SubjectId = id, ChangeKind = "removed",
            Summary = "Health check schedule removed", ChangedBy = removedBy,
        });
    }

    public IReadOnlyList<Anthill.Core.Health.HealthCheckSchedule> ListHealthSchedules()
    {
        var list = new List<Anthill.Core.Health.HealthCheckSchedule>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, check_kind, target, service_id, node_id, enabled, timeout_ms, created_at FROM health_check_schedules ORDER BY created_at";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Anthill.Core.Health.HealthCheckSchedule
            {
                Id = r.GetString(0), CheckKind = r.GetString(1), Target = r.GetString(2),
                ServiceId = r.IsDBNull(3) ? "" : r.GetString(3), NodeId = r.IsDBNull(4) ? "" : r.GetString(4),
                Enabled = r.GetInt64(5) != 0, TimeoutMs = (int)r.GetInt64(6), CreatedAt = r.GetString(7),
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
        Devices = ListNetworkDevices().ToList(),
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
        var devices = 0;
        foreach (var device in bundle.Devices.Where(d => !string.IsNullOrWhiteSpace(d.Name) || !string.IsNullOrWhiteSpace(d.Mac)))
        {
            UpsertNetworkDevice(device, importedBy); devices++;
        }
        RecordChange(new ChangeRecord
        {
            SubjectKind = "inventory", SubjectId = "import", ChangeKind = "imported",
            Summary = $"Inventory import: {nodes} node(s), {services} service(s), {deps} dependency(ies), {devices} device(s)",
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

    // ---- v2.3.3: node metrics + *arr apps -----------------------------------------------------

    public void UpsertNodeMetric(NodeMetricRecord m)
    {
        m.UpdatedAt = AnthillTime.NowUtc().ToIso();
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO node_metrics (node_id,node_name,source,cpu_percent,cpu_cores,mem_used_bytes,mem_total_bytes,disk_used_bytes,disk_total_bytes,uptime_seconds,updated_at)
                VALUES ($id,$name,$src,$cpu,$cores,$mu,$mt,$du,$dt,$up,$at)
                ON CONFLICT(node_id) DO UPDATE SET node_name=$name,source=$src,cpu_percent=$cpu,cpu_cores=$cores,
                mem_used_bytes=$mu,mem_total_bytes=$mt,disk_used_bytes=$du,disk_total_bytes=$dt,uptime_seconds=$up,updated_at=$at";
            Bind(cmd,"$id",m.NodeId); Bind(cmd,"$name",m.NodeName); Bind(cmd,"$src",m.Source);
            Bind(cmd,"$cpu",m.CpuPercent); Bind(cmd,"$cores",m.CpuCores); Bind(cmd,"$mu",m.MemUsedBytes);
            Bind(cmd,"$mt",m.MemTotalBytes); Bind(cmd,"$du",m.DiskUsedBytes); Bind(cmd,"$dt",m.DiskTotalBytes);
            Bind(cmd,"$up",m.UptimeSeconds); Bind(cmd,"$at",m.UpdatedAt);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<NodeMetricRecord> ListNodeMetrics()
    {
        var list = new List<NodeMetricRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT node_id,node_name,source,cpu_percent,cpu_cores,mem_used_bytes,mem_total_bytes,disk_used_bytes,disk_total_bytes,uptime_seconds,updated_at FROM node_metrics ORDER BY node_name";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new NodeMetricRecord
        {
            NodeId=r.GetString(0), NodeName=r.GetString(1), Source=r.GetString(2),
            CpuPercent=r.IsDBNull(3)?-1:r.GetDouble(3), CpuCores=r.IsDBNull(4)?0:r.GetInt32(4),
            MemUsedBytes=r.IsDBNull(5)?-1:r.GetInt64(5), MemTotalBytes=r.IsDBNull(6)?-1:r.GetInt64(6),
            DiskUsedBytes=r.IsDBNull(7)?-1:r.GetInt64(7), DiskTotalBytes=r.IsDBNull(8)?-1:r.GetInt64(8),
            UptimeSeconds=r.IsDBNull(9)?0:r.GetInt64(9), UpdatedAt=r.IsDBNull(10)?"":r.GetString(10),
        });
        return list;
    }

    public void UpsertArrApp(ArrAppRecord a)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO arr_apps (id,kind,name,url,credential_id,enabled,status,version,health_warnings,queue_count,last_message,last_checked)
                VALUES ($id,$kind,$name,$url,$cred,$en,$st,$ver,$hw,$q,$msg,$at)
                ON CONFLICT(id) DO UPDATE SET kind=$kind,name=$name,url=$url,credential_id=$cred,enabled=$en,
                status=$st,version=$ver,health_warnings=$hw,queue_count=$q,last_message=$msg,last_checked=$at";
            Bind(cmd,"$id",a.Id); Bind(cmd,"$kind",a.Kind); Bind(cmd,"$name",a.Name); Bind(cmd,"$url",a.Url);
            Bind(cmd,"$cred",a.CredentialId); Bind(cmd,"$en",a.Enabled?1:0); Bind(cmd,"$st",a.Status);
            Bind(cmd,"$ver",a.Version); Bind(cmd,"$hw",a.HealthWarnings); Bind(cmd,"$q",a.QueueCount);
            Bind(cmd,"$msg",a.LastMessage); Bind(cmd,"$at",a.LastChecked);
            cmd.ExecuteNonQuery();
        }
    }

    public void RemoveArrApp(string id, string removedBy)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM arr_apps WHERE id=$id";
            Bind(cmd,"$id",id);
            if (cmd.ExecuteNonQuery() > 0)
                RecordChange(new ChangeRecord { SubjectKind="arr_app", SubjectId=id, ChangeKind="removed", Summary="*arr app removed", ChangedBy=removedBy });
        }
    }

    public IReadOnlyList<ArrAppRecord> ListArrApps()
    {
        var list = new List<ArrAppRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,kind,name,url,credential_id,enabled,status,version,health_warnings,queue_count,last_message,last_checked FROM arr_apps ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new ArrAppRecord
        {
            Id=r.GetString(0), Kind=r.GetString(1), Name=r.GetString(2), Url=r.GetString(3),
            CredentialId=r.GetString(4), Enabled=!r.IsDBNull(5)&&r.GetInt32(5)==1,
            Status=r.IsDBNull(6)?"unknown":r.GetString(6), Version=r.IsDBNull(7)?"":r.GetString(7),
            HealthWarnings=r.IsDBNull(8)?0:r.GetInt32(8), QueueCount=r.IsDBNull(9)?-1:r.GetInt32(9),
            LastMessage=r.IsDBNull(10)?"":r.GetString(10), LastChecked=r.IsDBNull(11)?"":r.GetString(11),
        });
        return list;
    }
}
