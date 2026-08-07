using System.Reflection;
using Anthill.Core.Agents;
using Anthill.Core.Shadow;
using Anthill.Core.Autonomy;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Conversations;   // v3.7.0: conversations, escalation policy and run state
using Anthill.Core.Diagnostics;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Models;
using Anthill.Core.Orchestration;
using Anthill.Core.Planning;
using Anthill.Core.Readiness;
using Anthill.Core.Sandbox;   // LoopBudget — the agent loop's bounds
using Anthill.Core.Security;
using Anthill.Core.Tools;      // ToolInventory, ToolAuthorization — the /tools report
// `Task` here is Anthill.Core.Domain.Task (the mission task). The threading one must be named.
using ThreadingTask = System.Threading.Tasks.Task;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;


namespace Anthill.Api;

/// <summary>
/// Projections: what a mission, patch, objective or readiness check looks like on the wire.
///
/// v3.8.17 — split out of ApiHost.cs, which was 3,294 lines and 102 endpoints. Same class,
/// same behaviour: ApiHost has been `public static partial` with eight files since the homelab
/// moved, so this is where the file was always going to divide.
/// </summary>
public static partial class ApiHost
{
    /// <summary>
    /// Everything the top-right header needs in one call: version, a live Ollama reachability
    /// probe (so "online" means the model backend, not just the API), the active default model,
    /// and a local-vs-providers breakdown of the model routes.
    /// </summary>
    private static Dictionary<string, object?> SystemSummary()
    {
        // Per-role routing: how many roles run on local Ollama vs a cloud provider.
        var routes = AnthillRuntime.ModelRouting;
        var providerRoles = new List<string>();
        var localRoles = new List<string>();
        foreach (var (role, cfg) in routes)
        {
            var provider = cfg.GetValueOrDefault("provider") ?? AnthillRuntime.DefaultModelProvider;
            if (string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase)) localRoles.Add(role);
            else providerRoles.Add(role);
        }
        var routeList = routes.Select(kv => new Dictionary<string, object?>
        {
            ["role"] = kv.Key,
            ["provider"] = kv.Value.GetValueOrDefault("provider") ?? AnthillRuntime.DefaultModelProvider,
            ["model"] = kv.Value.GetValueOrDefault("model"),
        }).ToList();

        // Live Ollama probe. v2.4.3: /api/version alone lied by omission — Ollama can be up while
        // the configured model is absent (typical on offline installs), and every ant call then
        // fails although the chip showed green. Now also checks /api/tags for the model.
        bool? ollamaReachable = null;
        bool? ollamaModelPresent = null;
        if (AnthillRuntime.UseOllama)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var baseHost = AnthillRuntime.OllamaHost.TrimEnd('/');
                using var resp = InternalHttp.GetAsync($"{baseHost}/api/version", cts.Token).GetAwaiter().GetResult();
                ollamaReachable = resp.IsSuccessStatusCode;
                if (ollamaReachable == true)
                {
                    try
                    {
                        using var tags = InternalHttp.GetAsync($"{baseHost}/api/tags", cts.Token).GetAwaiter().GetResult();
                        if (tags.IsSuccessStatusCode)
                        {
                            var tagsBody = tags.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
                            using var doc = System.Text.Json.JsonDocument.Parse(tagsBody);
                            var want = AnthillRuntime.OllamaModel;
                            ollamaModelPresent = doc.RootElement.TryGetProperty("models", out var models)
                                && models.ValueKind == System.Text.Json.JsonValueKind.Array
                                && models.EnumerateArray().Any(m =>
                                    m.TryGetProperty("name", out var n)
                                    && (string.Equals(n.GetString(), want, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(n.GetString(), want + ":latest", StringComparison.OrdinalIgnoreCase)
                                        || string.Equals((n.GetString() ?? "").Split(':')[0], want, StringComparison.OrdinalIgnoreCase)));
                        }
                    }
                    catch { /* model check is best-effort; reachability already established */ }
                }
            }
            catch { ollamaReachable = false; }
        }

        var providersConfigured = Queen.Memory.ListProviderConnections()
            .Count(p => p.GetValueOrDefault("configured") as bool? == true);

        return new Dictionary<string, object?>
        {
            ["version"] = AnthillRuntime.Version,
            ["native_kernel"] = Anthill.Core.Native.NativeKernel.UsingNative ? "active" : "managed-fallback",
            ["safety_profile"] = AnthillRuntime.Config.SafetyProfile,
            ["api_host"] = AnthillRuntime.ApiHost,
            ["use_ollama"] = AnthillRuntime.UseOllama,
            ["ollama_host"] = AnthillRuntime.OllamaHost,
            ["ollama_reachable"] = ollamaReachable,
            ["ollama_model_present"] = ollamaModelPresent, // v2.4.3: null = unknown/not checked
            ["default_model"] = AnthillRuntime.OllamaModel,
            ["routing_mode"] = providerRoles.Count == 0 ? "local" : (localRoles.Count == 0 ? "providers" : "mixed"),
            ["local_role_count"] = localRoles.Count,
            ["provider_role_count"] = providerRoles.Count,
            ["providers_configured"] = providersConfigured,
            ["routes"] = routeList,
        };
    }

    // ---- mission report -----------------------------------------------------

    private static IResult MissionReport(string id, bool includeSensitive)
    {
        var mission = Queen.Memory.GetMission(id);
        if (mission is null) return ApiJson.Error($"No mission found with id: {id}", "not_found");

        var tasks = Queen.Memory.GetTasksForMission(id);
        // Patches/approvals/objectives are admin-only surfaces — skip the queries entirely for
        // non-admin callers so nothing sensitive is even assembled.
        var patches = includeSensitive ? Queen.Memory.ListPatchProposalsForMission(id) : new List<Dictionary<string, object?>>();
        var approvals = includeSensitive ? Queen.Memory.ListApprovalRequestsForMission(id) : new List<Dictionary<string, object?>>();
        var approvalByTarget = approvals
            .GroupBy(a => a.GetValueOrDefault("target_id")?.ToString() ?? "")
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.GetValueOrDefault("created_at")?.ToString()).First());

        // Problem events for this mission, translated for humans. patch_proposal_parse_failed is
        // the big silent one: the coder did work, but its proposal never reached the approval
        // queue — from the outside it looks like "nothing happened".
        var problemTypes = new HashSet<string>
        {
            "task_failed", "task_blocked", "task_skipped_dependency", "mission_failed",
            "patch_proposal_parse_failed", "autonomy_error",
            // v1.8.21: auto-apply failures the operator needs to see ("why didn't it save?").
            "autonomy_autoapply_reverted", "autonomy_autoapply_apply_failed", "autonomy_autoapply_git_failed",
        };
        var missionEvents = Queen.Memory.GetRecentEvents(400, null, id);
        var problems = missionEvents
            .Where(e => problemTypes.Contains(e.GetValueOrDefault("event_type")?.ToString() ?? ""))
            .Select(e => new Dictionary<string, object?>
            {
                ["type"] = e.GetValueOrDefault("event_type"),
                ["message"] = e.GetValueOrDefault("message"),
                ["task_id"] = e.GetValueOrDefault("task_id"),
                ["at"] = e.GetValueOrDefault("created_at"),
            })
            .ToList();

        // v1.8.21: the latest gated auto-apply outcome for this mission, surfaced so the operator can
        // see whether auto-applied changes were kept, kept-unverified, reverted, or skipped — and why.
        Dictionary<string, object?>? autoApply = null;
        if (includeSensitive)
        {
            var aa = missionEvents.FirstOrDefault(e =>
                (e.GetValueOrDefault("event_type")?.ToString() ?? "").StartsWith("autonomy_autoapply_"));
            if (aa is not null)
            {
                var t = aa.GetValueOrDefault("event_type")?.ToString() ?? "";
                autoApply = new Dictionary<string, object?>
                {
                    ["type"] = t,
                    ["outcome"] = t.Replace("autonomy_autoapply_", ""),
                    ["kept"] = t is "autonomy_autoapply_verified" or "autonomy_autoapply_kept_unverified",
                    ["message"] = aa.GetValueOrDefault("message"),
                    ["at"] = aa.GetValueOrDefault("created_at"),
                };
            }
        }

        var taskReports = tasks.Select(t =>
        {
            var ant = t.GetValueOrDefault("assigned_ant")?.ToString() ?? "";
            var result = t.GetValueOrDefault("result")?.ToString() ?? "";
            return new Dictionary<string, object?>
            {
                ["id"] = t.GetValueOrDefault("id"),
                ["title"] = t.GetValueOrDefault("title"),
                ["ant"] = ant,
                ["task_type"] = t.GetValueOrDefault("task_type"),
                ["status"] = t.GetValueOrDefault("status"),
                ["elapsed_seconds"] = t.GetValueOrDefault("elapsed_seconds"),
                ["readable_output"] = ReadableTaskOutput(ant, result),
                ["failure_reason"] = t.GetValueOrDefault("failure_reason"),
                ["skipped_reason"] = t.GetValueOrDefault("skipped_reason"),
                ["blocked_reason"] = t.GetValueOrDefault("blocked_reason"),
            };
        }).ToList();

        var patchReports = patches.Select(p =>
        {
            var patchId = p.GetValueOrDefault("id")?.ToString() ?? "";
            var approval = approvalByTarget.GetValueOrDefault(patchId);
            return new Dictionary<string, object?>
            {
                ["id"] = patchId,
                ["file_path"] = p.GetValueOrDefault("file_path"),
                ["change_type"] = p.GetValueOrDefault("change_type"),
                ["reason"] = p.GetValueOrDefault("reason"),
                ["risk"] = p.GetValueOrDefault("risk"),
                ["status"] = p.GetValueOrDefault("status"),
                ["applied_at"] = p.GetValueOrDefault("applied_at"),
                ["last_error"] = p.GetValueOrDefault("last_error"),
                ["approval_id"] = approval?.GetValueOrDefault("id"),
                ["approval_status"] = approval?.GetValueOrDefault("status"),
            };
        }).ToList();

        // Autonomy linkage (admin-only surface): which objective drove this mission, and which it
        // created. Only assembled for callers who can read objectives directly.
        var run = includeSensitive ? Queen.Memory.GetAutonomyRunForMission(id) : null;
        var runObjective = run?.GetValueOrDefault("objective_id")?.ToString() is { Length: > 0 } oid
            ? Queen.Memory.GetObjective(oid) : null;
        var createdObjectives = includeSensitive
            ? Queen.Memory.ListObjectivesCreatedByMission(id)
                .Select(o => new Dictionary<string, object?>
                {
                    ["id"] = o.Id, ["title"] = o.Title, ["charter"] = o.Charter,
                    ["priority"] = o.Priority, ["status"] = o.Status.Value(),
                }).ToList()
            : new List<Dictionary<string, object?>>();

        var statuses = tasks.Select(t => t.GetValueOrDefault("status")?.ToString() ?? "").ToList();
        return ApiJson.Ok(new Dictionary<string, object?>
        {
            ["autonomy_run"] = run is null ? null : new Dictionary<string, object?>
            {
                ["run_id"] = run.GetValueOrDefault("id"),
                ["objective_id"] = run.GetValueOrDefault("objective_id"),
                ["objective_title"] = runObjective?.Title,
                ["generated_goal"] = run.GetValueOrDefault("generated_goal"),
                ["follow_ups_created"] = run.GetValueOrDefault("follow_ups_created"),
            },
            ["created_objectives"] = createdObjectives,
            ["id"] = id,
            ["goal"] = mission.GetValueOrDefault("goal"),
            ["status"] = mission.GetValueOrDefault("status"),
            ["success_score"] = mission.GetValueOrDefault("success_score"),
            ["created_at"] = mission.GetValueOrDefault("created_at"),
            ["completed_at"] = mission.GetValueOrDefault("completed_at"),
            // v2.16.0: the ANSWER the operator reads is final_result (a plain-English rewrite when
            // synthesis is on). user_result stays available as raw_output so the activity view can
            // show exactly what the winning task emitted, unedited.
            ["final_output"] = mission.GetValueOrDefault("final_result")
                               ?? mission.GetValueOrDefault("user_result"),
            ["raw_output"] = mission.GetValueOrDefault("user_result"),
            ["synthesized"] = mission.GetValueOrDefault("final_result") is string fr
                              && fr != (mission.GetValueOrDefault("user_result") as string ?? ""),
            ["task_counts"] = new Dictionary<string, object?>
            {
                ["total"] = statuses.Count,
                ["complete"] = statuses.Count(s => s == "complete"),
                ["failed"] = statuses.Count(s => s == "failed"),
                ["skipped"] = statuses.Count(s => s == "skipped"),
            },
            ["tasks"] = taskReports,
            ["patches"] = patchReports,
            // v1.8.16: rollup of patch activity for this mission (proposed/approved/applied/rejected/failed).
            ["patch_counts"] = includeSensitive ? Queen.Memory.PatchCountsForMission(id) : null,
            ["pending_approvals"] = approvals.Count(a => a.GetValueOrDefault("status")?.ToString() == "pending"),
            ["sources_saved"] = Queen.Memory.CountSourcesForMission(id),
            ["auto_apply"] = autoApply,
            ["problems"] = problems,
        });
    }

    /// <summary>
    /// Turns a task's raw result into readable English. Coder results are structured JSON patch
    /// sets — rendered as "Proposed change to <file>: <reason>" lines instead of raw JSON. Other
    /// ants already produce prose; it is passed through (bounded) as-is.
    /// </summary>
    internal static string ReadableTaskOutput(string ant, string result)
    {
        if (string.IsNullOrWhiteSpace(result)) return "";
        if (ant == "coder")
        {
            try
            {
                var parsed = Json.ExtractJsonObject(result);
                var summary = parsed["summary"]?.GetValue<string>()?.Trim() ?? "";
                var lines = new List<string>();
                if (summary.Length > 0) lines.Add(summary);
                if (parsed["proposals"] is System.Text.Json.Nodes.JsonArray proposals)
                {
                    if (proposals.Count == 0)
                        lines.Add("No file changes were proposed.");
                    foreach (var item in proposals)
                    {
                        if (item is not System.Text.Json.Nodes.JsonObject o) continue;
                        var file = o["file_path"]?.GetValue<string>() ?? "?";
                        var change = o["change_type"]?.GetValue<string>() ?? "modify";
                        var reason = o["reason"]?.GetValue<string>() ?? "";
                        lines.Add($"Proposed {change} to {file}: {reason}");
                    }
                }
                if (lines.Count > 0) return string.Join("\n", lines);
            }
            catch { /* not parseable as a patch set — fall through to raw text */ }
        }
        return TextUtil.Truncate(result, 4000, "\n...[output truncated — full text in the mission detail]");
    }

    // ---- Patch Center helpers (v1.8.16) ------------------------------------

    private static readonly Dictionary<string, string> PatchStatusLabels = new()
    {
        ["proposed"] = "Pending", ["approved"] = "Approved", ["applied"] = "Applied",
        ["rejected"] = "Rejected", ["failed"] = "Failed", ["superseded"] = "Superseded",
    };

    private static PatchStatus? ParsePatchStatusOrNull(string s) => s switch
    {
        "proposed" => PatchStatus.Proposed, "approved" => PatchStatus.Approved, "rejected" => PatchStatus.Rejected,
        "applied" => PatchStatus.Applied, "failed" => PatchStatus.Failed, "superseded" => PatchStatus.Superseded, _ => null,
    };

    /// <summary>Shapes one Patch Center list row: normalizes risk, adds a status label, no content body.</summary>
    private static Dictionary<string, object?> PatchCenterRow(Dictionary<string, object?> p)
    {
        var status = p.GetValueOrDefault("status")?.ToString() ?? "proposed";
        var riskRaw = p.GetValueOrDefault("risk")?.ToString() ?? "";
        return new Dictionary<string, object?>
        {
            ["id"] = p.GetValueOrDefault("id"),
            ["file_path"] = p.GetValueOrDefault("file_path"),
            ["change_type"] = p.GetValueOrDefault("change_type"),
            ["risk"] = RiskLevel.Normalize(riskRaw),
            ["risk_raw"] = riskRaw,
            ["reason"] = p.GetValueOrDefault("reason"),
            ["status"] = status,
            ["status_label"] = PatchStatusLabels.GetValueOrDefault(status, status),
            ["mission_id"] = p.GetValueOrDefault("mission_id"),
            ["mission_goal"] = p.GetValueOrDefault("mission_goal"),
            ["objective_id"] = p.GetValueOrDefault("objective_id"),
            ["run_id"] = p.GetValueOrDefault("run_id"),
            ["task_id"] = p.GetValueOrDefault("task_id"),
            ["patch_set_id"] = p.GetValueOrDefault("patch_set_id"),
            ["patch_set_summary"] = p.GetValueOrDefault("patch_set_summary"),
            ["created_at"] = p.GetValueOrDefault("created_at"),
            ["applied_at"] = p.GetValueOrDefault("applied_at"),
            ["last_error"] = p.GetValueOrDefault("last_error"),
            ["has_backup"] = !string.IsNullOrEmpty(p.GetValueOrDefault("backup_path")?.ToString()),
            ["approval_id"] = p.GetValueOrDefault("approval_id"),
            ["approval_status"] = p.GetValueOrDefault("approval_status"),
        };
    }

    /// <summary>Full JSON detail for one patch (Patch Center diff view): metadata + old/new content + approval.</summary>
    private static IResult PatchDetailJson(string patchId)
    {
        var p = Queen.Memory.GetPatchProposal(patchId);
        if (p is null) return ApiJson.Error($"No patch found with id: {patchId}", "not_found");
        var missionId = p.GetValueOrDefault("mission_id")?.ToString() ?? "";
        var approval = Queen.Memory.GetApprovalForTarget(patchId);
        var run = string.IsNullOrEmpty(missionId) ? null : Queen.Memory.GetAutonomyRunForMission(missionId);
        var objectiveId = run?.GetValueOrDefault("objective_id")?.ToString();
        var objective = string.IsNullOrEmpty(objectiveId) ? null : Queen.Memory.GetObjective(objectiveId!);
        var status = p.GetValueOrDefault("status")?.ToString() ?? "proposed";
        var riskRaw = p.GetValueOrDefault("risk")?.ToString() ?? "";
        return ApiJson.Ok(new Dictionary<string, object?>
        {
            ["id"] = patchId,
            ["file_path"] = p.GetValueOrDefault("file_path"),
            ["change_type"] = p.GetValueOrDefault("change_type"),
            ["risk"] = RiskLevel.Normalize(riskRaw),
            ["risk_raw"] = riskRaw,
            ["reason"] = p.GetValueOrDefault("reason"),
            ["status"] = status,
            ["status_label"] = PatchStatusLabels.GetValueOrDefault(status, status),
            ["old_content"] = p.GetValueOrDefault("old_content"),
            ["new_content"] = p.GetValueOrDefault("new_content"),
            ["mission_id"] = missionId,
            ["mission_goal"] = p.GetValueOrDefault("mission_goal"),
            ["task_id"] = p.GetValueOrDefault("task_id"),
            ["patch_set_summary"] = p.GetValueOrDefault("patch_set_summary"),
            ["objective_id"] = objectiveId,
            ["objective_title"] = objective?.Title,
            ["run_id"] = run?.GetValueOrDefault("id"),
            ["created_at"] = p.GetValueOrDefault("created_at"),
            ["applied_at"] = p.GetValueOrDefault("applied_at"),
            ["last_error"] = p.GetValueOrDefault("last_error"),
            ["has_backup"] = !string.IsNullOrEmpty(p.GetValueOrDefault("backup_path")?.ToString()),
            ["approval_id"] = approval?.GetValueOrDefault("id"),
            ["approval_status"] = approval?.GetValueOrDefault("status"),
        });
    }

    private static Dictionary<string, object?> ObjectiveDict(Objective o) => new()
    {
        ["id"] = o.Id, ["title"] = o.Title, ["charter"] = o.Charter, ["priority"] = o.Priority,
        ["status"] = o.Status.Value(), ["max_runs"] = o.MaxRuns, ["run_count"] = o.RunCount,
        ["consecutive_failures"] = o.ConsecutiveFailures, ["parent_objective_id"] = o.ParentObjectiveId,
        ["created_at"] = o.CreatedAt.ToIso(), ["last_run_at"] = o.LastRunAt.ToIsoOrNull(),
        ["success_ema"] = o.SuccessEma,
        // Retirement markers (stamped by the Director). Looping-retired objectives are shown in the
        // console's "Completed Objectives" box and filtered out of the active/paused backlog list.
        ["retired_code"] = o.Metadata.GetValueOrDefault("retired_code"),
        ["retired_reason"] = o.Metadata.GetValueOrDefault("retired_reason"),
        ["retired_at"] = o.Metadata.GetValueOrDefault("retired_at"),
        // v1.8.16 unified lifecycle end markers.
        ["end_reason"] = o.Metadata.GetValueOrDefault("end_reason"),
        ["end_detail"] = o.Metadata.GetValueOrDefault("end_detail"),
        ["ended_at"] = o.Metadata.GetValueOrDefault("ended_at"),
    };

    /// <summary>
    /// Compiles the "Completed Objectives" expanded view for one retired objective: the objective's
    /// own fields plus every autonomy run it produced, the missions those runs launched, and the
    /// tasks within those missions — all from existing models, no new storage.
    /// </summary>
    private static Dictionary<string, object?> CompletedObjectiveDetail(Objective o)
    {
        var runs = Queen.Memory.ListAutonomyRuns(o.Id, limit: 100);
        var missions = new List<Dictionary<string, object?>>();
        var tasks = new List<Dictionary<string, object?>>();
        foreach (var missionId in runs.Select(r => r.GetValueOrDefault("mission_id")?.ToString())
                     .Where(m => !string.IsNullOrEmpty(m)).Distinct())
        {
            var mission = Queen.Memory.GetMission(missionId!);
            if (mission is not null)
                missions.Add(new Dictionary<string, object?>
                {
                    ["id"] = mission.GetValueOrDefault("id"), ["goal"] = mission.GetValueOrDefault("goal"),
                    ["status"] = mission.GetValueOrDefault("status"), ["success_score"] = mission.GetValueOrDefault("success_score"),
                });
            foreach (var t in Queen.Memory.GetTasksForMission(missionId!, 200))
                tasks.Add(new Dictionary<string, object?>
                {
                    ["mission_id"] = missionId, ["title"] = t.GetValueOrDefault("title"),
                    ["ant"] = t.GetValueOrDefault("assigned_ant"), ["status"] = t.GetValueOrDefault("status"),
                    ["worker"] = t.GetValueOrDefault("assigned_worker"),
                    ["path_node"] = t.GetValueOrDefault("assigned_worker") ?? t.GetValueOrDefault("assigned_ant"),
                });
        }
        var endReason = o.Metadata.GetValueOrDefault("end_reason")?.ToString()
            ?? (o.Metadata.GetValueOrDefault("retired_code") is not null ? ObjectiveEndReason.RetiredLooping : null);
        return new Dictionary<string, object?>
        {
            ["id"] = o.Id, ["title"] = o.Title, ["charter"] = o.Charter,
            ["end_reason"] = endReason,
            ["end_reason_label"] = ObjectiveEndReason.Label(endReason),
            ["end_detail"] = o.Metadata.GetValueOrDefault("end_detail") ?? o.Metadata.GetValueOrDefault("retired_reason"),
            ["ended_at"] = o.Metadata.GetValueOrDefault("ended_at") ?? o.Metadata.GetValueOrDefault("retired_at"),
            ["patch_counts"] = Queen.Memory.PatchCountsForObjective(o.Id),
            ["retired_code"] = o.Metadata.GetValueOrDefault("retired_code"),
            ["retired_reason"] = o.Metadata.GetValueOrDefault("retired_reason"),
            ["retired_at"] = o.Metadata.GetValueOrDefault("retired_at"),
            ["run_count"] = o.RunCount, ["last_run_at"] = o.LastRunAt.ToIsoOrNull(),
            ["runs"] = runs.Select(r => new Dictionary<string, object?>
            {
                ["generated_goal"] = r.GetValueOrDefault("generated_goal"), ["mission_status"] = r.GetValueOrDefault("mission_status"),
                ["mission_id"] = r.GetValueOrDefault("mission_id"), ["started_at"] = r.GetValueOrDefault("started_at"),
                ["success_score"] = r.GetValueOrDefault("success_score"),
            }).ToList(),
            ["missions"] = missions,
            ["tasks"] = tasks,
        };
    }

    /// <summary>Human-readable byte size (e.g. "34.0 GB") for maintenance messages.</summary>
    private static string HumanBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }

    /// <summary>
    /// v2.25.0 Phase F: gather every readiness input from live storage and evaluate. The
    /// evaluation itself is pure (<see cref="V3Readiness.Evaluate"/>) so each threshold rule is
    /// testable without a database; this method is only the plumbing.
    /// </summary>
    private static ReadinessReport EvaluateReadiness()
    {
        var metrics = QualificationScoreboard.Compute(Queen.Memory.LoadScoreableRecommendations(500));
        var stability = Queen.Memory.FaultInjectionStability();
        var (executed, _, unknown) = Homelab.CountExecutedActionLifecycles();
        return V3Readiness.Evaluate(new V3Readiness.Inputs(
            Shadow: metrics,
            ShadowSample: metrics.Sample,
            UnresolvedShadowBacklog: Queen.Memory.CountUnresolvedShadowRecommendations(),
            FaultInjectionRuns: stability.Runs,
            FaultInjectionStableStreak: stability.StableStreak,
            FaultInjectionStable: stability.Stable,
            ExecutedActions: executed,
            ExecutedActionsUnknownLifecycle: unknown,
            MinShadowSample: AnthillRuntime.ReadinessMinShadowSample,
            MinDiagnosisPrecision: AnthillRuntime.ReadinessMinDiagnosisPrecision,
            MinActionAccuracy: AnthillRuntime.ReadinessMinActionAccuracy,
            Attestations: Queen.Memory.LoadReadinessAttestations(),
            BreakGlassKeepWithoutVerify: AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify));
    }

    private static Dictionary<string, object?> ReadinessSnapshot()
    {
        var report = EvaluateReadiness();
        return new Dictionary<string, object?>
        {
            ["ready"] = report.Ready,
            ["statement"] = report.Statement,
            ["satisfied"] = report.SatisfiedCount,
            ["total"] = report.Total,
            // Projected explicitly (same reason as /shadow/json): no serializer naming policy is
            // configured, and the wire shape must not change when a C# property is renamed.
            ["checks"] = report.Checks.Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Id, ["title"] = c.Title, ["kind"] = c.Kind.ToString().ToLowerInvariant(),
                ["satisfied"] = c.Satisfied, ["measured_holds"] = c.MeasuredHolds,
                ["attested"] = c.Attested, ["detail"] = c.Detail,
            }).ToList(),
            ["attestable_ids"] = V3Readiness.AttestableIds.OrderBy(x => x).ToList(),
        };
    }
}
