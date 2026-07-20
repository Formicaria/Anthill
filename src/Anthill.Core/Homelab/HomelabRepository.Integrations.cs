using Anthill.Core.Common;
using Anthill.Core.Integrations;
using Anthill.Core.Integrations.Arr;
using Microsoft.Data.Sqlite;

namespace Anthill.Core.Homelab;

/// <summary>
/// v2.5.1 Console Refit R1 (docs/CONSOLE_REFIT.md) — persistence for the generic integration
/// platform. `integration_instances` generalizes arr_apps (one row per configured integration of
/// any registered kind); `integration_state` holds one typed widget payload per
/// (integration id, widget kind) with freshness, the single source the R2 widget runtime reads.
/// The legacy ArrApp methods survive unchanged in signature but are now views over these tables,
/// so every existing caller (UI, endpoints, tests) keeps working while the rows migrate.
/// </summary>
public sealed partial class HomelabRepository
{
    // ---- Integration instances (generalized arr_apps) ----------------------------------------

    public void UpsertIntegrationInstance(IntegrationInstanceRecord i)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO integration_instances (id,kind,name,url,credential_id,enabled,status,last_message,last_checked)
                VALUES ($id,$kind,$name,$url,$cred,$en,$st,$msg,$at)
                ON CONFLICT(id) DO UPDATE SET kind=$kind,name=$name,url=$url,credential_id=$cred,enabled=$en,
                status=$st,last_message=$msg,last_checked=$at";
            Bind(cmd,"$id",i.Id); Bind(cmd,"$kind",i.Kind); Bind(cmd,"$name",i.Name); Bind(cmd,"$url",i.Url);
            Bind(cmd,"$cred",i.CredentialId); Bind(cmd,"$en",i.Enabled?1:0); Bind(cmd,"$st",i.Status);
            Bind(cmd,"$msg",i.LastMessage); Bind(cmd,"$at",i.LastChecked);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<IntegrationInstanceRecord> ListIntegrationInstances()
    {
        var list = new List<IntegrationInstanceRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,kind,name,url,credential_id,enabled,status,last_message,last_checked FROM integration_instances ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadInstance(r));
        return list;
    }

    public void RemoveIntegrationInstance(string id, string removedBy) =>
        RemoveInstanceCore(id, removedBy, subjectKind: "integration", summary: "integration removed");

    private void RemoveInstanceCore(string id, string removedBy, string subjectKind, string summary)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using (var state = conn.CreateCommand())
            {
                state.CommandText = "DELETE FROM integration_state WHERE integration_id=$id";
                Bind(state,"$id",id);
                state.ExecuteNonQuery();
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM integration_instances WHERE id=$id";
            Bind(cmd,"$id",id);
            if (cmd.ExecuteNonQuery() > 0)
                RecordChange(new ChangeRecord { SubjectKind=subjectKind, SubjectId=id, ChangeKind="removed", Summary=summary, ChangedBy=removedBy });
        }
    }

    private static IntegrationInstanceRecord ReadInstance(SqliteDataReader r) => new()
    {
        Id=r.GetString(0), Kind=r.GetString(1), Name=r.GetString(2), Url=r.GetString(3),
        CredentialId=r.GetString(4), Enabled=!r.IsDBNull(5)&&r.GetInt32(5)==1,
        Status=r.IsDBNull(6)?"unknown":r.GetString(6),
        LastMessage=r.IsDBNull(7)?"":r.GetString(7), LastChecked=r.IsDBNull(8)?"":r.GetString(8),
    };

    // ---- Integration state (widget payloads + freshness) -------------------------------------

    public void UpsertIntegrationState(string integrationId, string widgetKind, string payloadJson)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO integration_state (integration_id,widget_kind,payload_json,updated_at)
                VALUES ($id,$wk,$p,$at)
                ON CONFLICT(integration_id,widget_kind) DO UPDATE SET payload_json=$p, updated_at=$at";
            Bind(cmd,"$id",integrationId); Bind(cmd,"$wk",widgetKind);
            Bind(cmd,"$p",payloadJson); Bind(cmd,"$at",AnthillTime.NowUtc().ToIso());
            cmd.ExecuteNonQuery();
        }
    }

    public IntegrationStateRecord? GetIntegrationState(string integrationId, string widgetKind)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT integration_id,widget_kind,payload_json,updated_at FROM integration_state WHERE integration_id=$id AND widget_kind=$wk";
        Bind(cmd,"$id",integrationId); Bind(cmd,"$wk",widgetKind);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadState(r) : null;
    }

    public IReadOnlyList<IntegrationStateRecord> ListIntegrationStates(string integrationId)
    {
        var list = new List<IntegrationStateRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT integration_id,widget_kind,payload_json,updated_at FROM integration_state WHERE integration_id=$id ORDER BY widget_kind";
        Bind(cmd,"$id",integrationId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadState(r));
        return list;
    }

    private static IntegrationStateRecord ReadState(SqliteDataReader r) => new()
    {
        IntegrationId=r.GetString(0), WidgetKind=r.GetString(1),
        PayloadJson=r.GetString(2), UpdatedAt=r.IsDBNull(3)?"":r.GetString(3),
    };

    // ---- *arr compatibility view (legacy public surface, generalized storage) ----------------

    public void UpsertArrApp(ArrAppRecord a)
    {
        UpsertIntegrationInstance(new IntegrationInstanceRecord
        {
            Id=a.Id, Kind=a.Kind, Name=a.Name, Url=a.Url, CredentialId=a.CredentialId,
            Enabled=a.Enabled, Status=a.Status, LastMessage=a.LastMessage, LastChecked=a.LastChecked,
        });
        UpsertIntegrationState(a.Id, "health", ArrWidgetPayloads.Health(a.Status, a.Version, a.HealthWarnings, a.LastChecked));
        if (ArrClient.Kinds.TryGetValue(a.Kind, out var meta) && meta.HasQueue)
            UpsertIntegrationState(a.Id, "queue", ArrWidgetPayloads.Queue(a.QueueCount, a.LastChecked));
    }

    public void RemoveArrApp(string id, string removedBy) =>
        RemoveInstanceCore(id, removedBy, subjectKind: "arr_app", summary: "*arr app removed");

    public IReadOnlyList<ArrAppRecord> ListArrApps()
    {
        var list = new List<ArrAppRecord>();
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT i.id,i.kind,i.name,i.url,i.credential_id,i.enabled,i.status,i.last_message,i.last_checked,
                h.payload_json, q.payload_json
            FROM integration_instances i
            LEFT JOIN integration_state h ON h.integration_id=i.id AND h.widget_kind='health'
            LEFT JOIN integration_state q ON q.integration_id=i.id AND q.widget_kind='queue'
            ORDER BY i.name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var kind = r.GetString(1);
            if (!ArrClient.Kinds.ContainsKey(kind)) continue; // non-arr integrations have their own surfaces
            var health = ArrWidgetPayloads.ParseHealth(r.IsDBNull(9) ? null : r.GetString(9));
            list.Add(new ArrAppRecord
            {
                Id=r.GetString(0), Kind=kind, Name=r.GetString(2), Url=r.GetString(3),
                CredentialId=r.GetString(4), Enabled=!r.IsDBNull(5)&&r.GetInt32(5)==1,
                Status=r.IsDBNull(6)?"unknown":r.GetString(6),
                LastMessage=r.IsDBNull(7)?"":r.GetString(7), LastChecked=r.IsDBNull(8)?"":r.GetString(8),
                Version=health.Version, HealthWarnings=health.Warnings,
                QueueCount=ArrWidgetPayloads.ParseQueueTotal(r.IsDBNull(10) ? null : r.GetString(10)),
            });
        }
        return list;
    }

    // ---- One-time legacy migration (runs inside InitDb's transaction) ------------------------

    /// <summary>
    /// Moves any legacy arr_apps rows into integration_instances + integration_state, then empties
    /// arr_apps. Idempotent by emptiness: nothing writes arr_apps anymore, so rows found there are
    /// always pre-2.5.1 leftovers. Existing ids are never overwritten (INSERT OR IGNORE).
    /// </summary>
    private static void MigrateArrAppsToIntegrations(SqliteConnection conn, SqliteTransaction tx)
    {
        var legacy = new List<ArrAppRecord>();
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT id,kind,name,url,credential_id,enabled,status,version,health_warnings,queue_count,last_message,last_checked FROM arr_apps";
            using var r = read.ExecuteReader();
            while (r.Read()) legacy.Add(new ArrAppRecord
            {
                Id=r.GetString(0), Kind=r.GetString(1), Name=r.GetString(2), Url=r.GetString(3),
                CredentialId=r.GetString(4), Enabled=!r.IsDBNull(5)&&r.GetInt32(5)==1,
                Status=r.IsDBNull(6)?"unknown":r.GetString(6), Version=r.IsDBNull(7)?"":r.GetString(7),
                HealthWarnings=r.IsDBNull(8)?0:r.GetInt32(8), QueueCount=r.IsDBNull(9)?-1:r.GetInt32(9),
                LastMessage=r.IsDBNull(10)?"":r.GetString(10), LastChecked=r.IsDBNull(11)?"":r.GetString(11),
            });
        }
        if (legacy.Count == 0) return;

        foreach (var a in legacy)
        {
            using (var inst = conn.CreateCommand())
            {
                inst.Transaction = tx;
                inst.CommandText = @"INSERT OR IGNORE INTO integration_instances (id,kind,name,url,credential_id,enabled,status,last_message,last_checked)
                    VALUES ($id,$kind,$name,$url,$cred,$en,$st,$msg,$at)";
                Bind(inst,"$id",a.Id); Bind(inst,"$kind",a.Kind); Bind(inst,"$name",a.Name); Bind(inst,"$url",a.Url);
                Bind(inst,"$cred",a.CredentialId); Bind(inst,"$en",a.Enabled?1:0); Bind(inst,"$st",a.Status);
                Bind(inst,"$msg",a.LastMessage); Bind(inst,"$at",a.LastChecked);
                inst.ExecuteNonQuery();
            }
            var now = AnthillTime.NowUtc().ToIso();
            var payloads = new List<(string Kind, string Json)>
                { ("health", ArrWidgetPayloads.Health(a.Status, a.Version, a.HealthWarnings, a.LastChecked)) };
            if (ArrClient.Kinds.TryGetValue(a.Kind, out var meta) && meta.HasQueue)
                payloads.Add(("queue", ArrWidgetPayloads.Queue(a.QueueCount, a.LastChecked)));
            foreach (var (wk, json) in payloads)
            {
                using var state = conn.CreateCommand();
                state.Transaction = tx;
                state.CommandText = @"INSERT OR IGNORE INTO integration_state (integration_id,widget_kind,payload_json,updated_at)
                    VALUES ($id,$wk,$p,$at)";
                Bind(state,"$id",a.Id); Bind(state,"$wk",wk); Bind(state,"$p",json); Bind(state,"$at",now);
                state.ExecuteNonQuery();
            }
        }
        using var wipe = conn.CreateCommand();
        wipe.Transaction = tx;
        wipe.CommandText = "DELETE FROM arr_apps";
        wipe.ExecuteNonQuery();
    }
}
