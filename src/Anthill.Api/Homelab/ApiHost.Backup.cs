using Anthill.Core.Common;
using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Backup;

namespace Anthill.Api;

/// <summary>
/// v2.4.0 backup + restore intelligence endpoints (NORTH_STAR Phase 13). All reads are
/// read_homelab; registering/updating a backup record needs manage_homelab_integrations.
/// Everything served here is deterministic arithmetic over real repository data — no LLM output,
/// no invented values; unknown targets fail toward "uncovered".
/// </summary>
public static partial class ApiHost
{
    private sealed record BackupUpsertRequest(
        string? Id, string? TargetKind, string? TargetId, string? Location,
        string? Status, string? LastSuccess, string? LastAttempt, long? SizeBytes, string? Notes);

    private static void MapHomelabBackupEndpoints(WebApplication app)
    {
        // ---- Coverage map: every VM/container with coverage class, confidence, priority --------
        app.MapGet("/homelab/backup/coverage", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_homelab"); if (auth is not null) return auth;
            var entries = BackupIntelligence.CoverageMap(Homelab, AnthillTime.NowUtc());
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["stale_after_days"] = BackupIntelligence.StaleAfterDays,
                ["totals"] = entries.GroupBy(e => e.Coverage).ToDictionary(g => g.Key, g => (object?)g.Count()),
                ["entries"] = entries,
            });
        });

        // ---- Blast radius: what dies if this node fails ----------------------------------------
        app.MapGet("/homelab/backup/impact/{nodeId}", (HttpContext ctx, string nodeId) =>
        {
            var auth = RequireAuth(ctx, "read_homelab"); if (auth is not null) return auth;
            return ApiJson.Ok(BackupIntelligence.SimulateNodeLoss(Homelab, nodeId, AnthillTime.NowUtc()));
        });

        // ---- Restore runbook for one target ----------------------------------------------------
        app.MapGet("/homelab/backup/runbook/{kind}/{id}", (HttpContext ctx, string kind, string id) =>
        {
            var auth = RequireAuth(ctx, "read_homelab"); if (auth is not null) return auth;
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["target_kind"] = kind, ["target_id"] = id,
                ["steps"] = BackupIntelligence.Runbook(Homelab, kind, id, AnthillTime.NowUtc()),
            });
        });

        // ---- Register / update a backup record (PBS, NAS jobs, manual) -------------------------
        app.MapPost("/homelab/backups", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            BackupUpsertRequest? req = null;
            try { req = await ctx.Request.ReadFromJsonAsync<BackupUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid JSON body.", "invalid_request"); }
            if (req is null || string.IsNullOrWhiteSpace(req.TargetKind) || string.IsNullOrWhiteSpace(req.TargetId))
                return ApiJson.Error("target_kind and target_id are required.", "invalid_request");
            var rec = new BackupRecord
            {
                Id = string.IsNullOrWhiteSpace(req.Id) ? $"{req.TargetKind}:{req.TargetId}" : req.Id!,
                TargetKind = req.TargetKind!, TargetId = req.TargetId!,
                Location = req.Location ?? "", Status = string.IsNullOrWhiteSpace(req.Status) ? "unknown" : req.Status!,
                LastSuccess = req.LastSuccess ?? "", LastAttempt = req.LastAttempt ?? "",
                SizeBytes = req.SizeBytes ?? 0, Notes = req.Notes ?? "",
            };
            Homelab.UpsertBackup(rec);
            Homelab.RecordEvent(new HomelabEvent
            {
                EventType = "backup_record_upserted",
                SubjectKind = rec.TargetKind, SubjectId = rec.TargetId,
                Severity = rec.Status == "failed" ? "warning" : "info",
                Message = $"Backup record for {rec.TargetKind} {rec.TargetId} -> {rec.Status}",
            });
            return ApiJson.Ok(rec);
        });

        app.MapGet("/homelab/backups", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_homelab"); if (auth is not null) return auth;
            return ApiJson.Ok(Homelab.ListBackups());
        });
    }
}
