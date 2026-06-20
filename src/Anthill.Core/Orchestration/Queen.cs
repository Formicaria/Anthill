using System.Text.Json;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;

namespace Anthill.Core.Orchestration;

/// <summary>
/// Queen approval/patch-application flow plus the formatter surface that the CLI and the
/// secured API render. These read from <see cref="SqliteMemory"/> and stay metadata-first
/// by default; full result summaries are only included where a permission explicitly allows.
/// </summary>
public sealed partial class Queen
{
    private const string Divider = "\n--------------------------------------------------\n";

    private static string Str(Dictionary<string, object?> row, string key, string fallback = "") =>
        row.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? fallback : fallback;

    // ---- approvals --------------------------------------------------------

    public string ApproveRequest(string approvalId)
    {
        try { approvalId = Validation.ValidateApprovalId(approvalId); }
        catch (Exception e) { return $"Invalid approval id: {e.Message}"; }
        var approval = Memory.GetApprovalRequest(approvalId);
        if (approval is null) return $"No approval request found with id: {approvalId}";
        if (Str(approval, "status") != ApprovalStatus.Pending.Value())
            return $"Approval request is not pending.\nID: {approvalId}\nCurrent Status: {Str(approval, "status")}";
        var updated = Memory.UpdateApprovalStatus(approvalId, ApprovalStatus.Approved,
            "Approved by user. Patch can only be applied through /apply if write gates are enabled.");
        if (updated is not null)
            Memory.LogEvent(Str(updated, "mission_id"), "approval_request_approved", $"Approval request approved: {approvalId}",
                Str(updated, "task_id"), "queen",
                new() { ["approval_request_id"] = approvalId, ["action_type"] = Str(updated, "action_type"), ["target_id"] = Str(updated, "target_id"), ["patch_application_enabled"] = AnthillRuntime.EnablePatchApplication, ["file_writing_enabled"] = AnthillRuntime.EnableFileWriting });
        return $"Approval recorded.\nID: {approvalId}\nStatus: approved\n\nNext step: inspect the patch with /patch {Str(updated!, "target_id")}.\n" +
               $"To apply later: /apply {approvalId}\n\nPatch application requires both write gates enabled.";
    }

    public string RejectRequest(string approvalId, string? reason = null)
    {
        try { approvalId = Validation.ValidateApprovalId(approvalId); }
        catch (Exception e) { return $"Invalid approval id: {e.Message}"; }
        var approval = Memory.GetApprovalRequest(approvalId);
        if (approval is null) return $"No approval request found with id: {approvalId}";
        if (Str(approval, "status") != ApprovalStatus.Pending.Value())
            return $"Approval request is not pending.\nID: {approvalId}\nCurrent Status: {Str(approval, "status")}";
        var note = reason ?? "Rejected by user.";
        var updated = Memory.UpdateApprovalStatus(approvalId, ApprovalStatus.Rejected, note);
        if (updated is not null)
        {
            if (Str(updated, "action_type") == ApprovalActionType.PatchProposal.Value())
                Memory.UpdatePatchStatus(Str(updated, "target_id"), PatchStatus.Rejected, lastError: note);
            Memory.LogEvent(Str(updated, "mission_id"), "approval_request_rejected", $"Approval request rejected: {approvalId}",
                Str(updated, "task_id"), "queen",
                new() { ["approval_request_id"] = approvalId, ["action_type"] = Str(updated, "action_type"), ["target_id"] = Str(updated, "target_id"), ["reason"] = note });
        }
        return $"Approval request rejected.\nID: {approvalId}\nStatus: rejected\nReason: {note}";
    }

    public string ApplyApprovedPatch(string approvalId)
    {
        try { approvalId = Validation.ValidateApprovalId(approvalId); }
        catch (Exception e) { return $"Invalid approval id: {e.Message}"; }
        var approval = Memory.GetApprovalRequest(approvalId);
        if (approval is null) return $"No approval request found with id: {approvalId}";
        if (Str(approval, "status") != ApprovalStatus.Approved.Value())
            return $"Cannot apply patch. Approval request is not approved.\nID: {approvalId}\nCurrent Status: {Str(approval, "status")}";
        if (Str(approval, "action_type") != ApprovalActionType.PatchProposal.Value())
            return $"Cannot apply approval type: {Str(approval, "action_type")}\nOnly patch_proposal approvals can be applied.";
        var patchId = Str(approval, "target_id");
        var patch = Memory.GetPatchProposal(patchId);
        if (patch is null) return $"No patch proposal found for approval target id: {patchId}";
        if (Str(patch, "status") == PatchStatus.Applied.Value()) return $"Patch is already applied.\nPatch ID: {patchId}";
        if (Str(patch, "status") is var ps && (ps == PatchStatus.Rejected.Value() || ps == PatchStatus.Failed.Value()))
            return $"Patch cannot be applied because status is {ps}.\nPatch ID: {patchId}";

        var result = Tools.RunTool("apply_patch", Str(approval, "mission_id"), Str(approval, "task_id"), "queen",
            new() { ["patch"] = patch });
        if (!result.Success)
        {
            Memory.UpdatePatchStatus(patchId, PatchStatus.Failed, lastError: result.Error);
            Memory.LogEvent(Str(approval, "mission_id"), "patch_apply_failed", $"Patch application failed: {patchId}",
                Str(approval, "task_id"), "queen", new() { ["approval_request_id"] = approvalId, ["patch_id"] = patchId, ["error"] = result.Error });
            return $"Patch application failed.\nApproval ID: {approvalId}\nPatch ID: {patchId}\nError: {result.Error}";
        }
        string? backupPath = null;
        try { backupPath = JsonDocument.Parse(string.IsNullOrEmpty(result.Output) ? "{}" : result.Output).RootElement.TryGetProperty("backup_path", out var bp) ? bp.GetString() : null; }
        catch { /* tolerate */ }
        Memory.UpdatePatchStatus(patchId, PatchStatus.Applied, AnthillTime.NowUtc().ToIso(), backupPath, null);
        Memory.UpdateApprovalStatus(approvalId, ApprovalStatus.Consumed, "Approval consumed by successful patch application.");
        Memory.LogEvent(Str(approval, "mission_id"), "patch_applied", $"Patch applied successfully: {patchId}",
            Str(approval, "task_id"), "queen",
            new() { ["approval_request_id"] = approvalId, ["patch_id"] = patchId, ["file_path"] = Str(patch, "file_path"), ["change_type"] = Str(patch, "change_type"), ["backup_path"] = backupPath });
        Memory.UpdatePheromoneTrail("capability:controlled_file_writing", "capability", true, 0.03,
            new() { ["approval_request_id"] = approvalId, ["patch_id"] = patchId, ["file_path"] = Str(patch, "file_path") });
        return $"Patch applied successfully.\nApproval ID: {approvalId}\nPatch ID: {patchId}\nFile: {Str(patch, "file_path")}\nBackup: {backupPath ?? "n/a"}\nApproval Status: consumed\nPatch Status: applied";
    }

    public string FormatPendingApprovals(int limit = 20) => FormatApprovals(limit, ApprovalStatus.Pending);

    public string FormatApprovals(int limit = 20, ApprovalStatus? status = ApprovalStatus.Pending)
    {
        var rows = Memory.ListApprovalRequests(status, limit);
        var label = status?.Value() ?? "all";
        if (rows.Count == 0) return $"No approval requests found for status: {label}.";
        var blocks = rows.Select(a =>
            $"Approval ID: {Str(a, "id")}\nStatus: {Str(a, "status")}\nAction: {Str(a, "action_type")}\nTarget ID: {Str(a, "target_id")}\n" +
            $"Mission ID: {Str(a, "mission_id")}\nTask ID: {Str(a, "task_id", "n/a")}\nTitle: {Str(a, "title")}\nRequested By: {Str(a, "requested_by")}\n" +
            $"Created At: {Str(a, "created_at", "n/a")}\nDecided At: {Str(a, "decided_at", "n/a")}\nDecision Note: {Str(a, "decision_note", "n/a")}\n" +
            $"Description:\n{TextUtil.Truncate(Str(a, "description"), 260, "...[description truncated]")}");
        return $"Approval Requests | status={label} | count={rows.Count}\n\n" + string.Join(Divider, blocks);
    }

    public string FormatApprovalDetail(string approvalId)
    {
        try { approvalId = Validation.ValidateApprovalId(approvalId); }
        catch (Exception e) { return $"Invalid approval id: {e.Message}"; }
        var approval = Memory.GetApprovalRequest(approvalId);
        if (approval is null) return $"No approval request found with id: {approvalId}";
        var metadata = ParseMetadata(approval);
        var targetId = Str(approval, "target_id");
        var relatedPatch = "";
        var applyLine = "";
        if (Str(approval, "action_type") == ApprovalActionType.PatchProposal.Value())
        {
            relatedPatch = $"\nInspect Related Patch: /patch {targetId}";
            applyLine = $"\nApply If Approved: /apply {approvalId}";
        }
        return $"Approval ID: {Str(approval, "id")}\nStatus: {Str(approval, "status")}\nAction Type: {Str(approval, "action_type")}\nTarget ID: {targetId}\n" +
               $"Mission ID: {Str(approval, "mission_id")}\nTask ID: {Str(approval, "task_id")}\nRequested By: {Str(approval, "requested_by")}\nTitle: {Str(approval, "title")}\n\n" +
               $"Description:\n{Str(approval, "description")}\n\nDecision Note:\n{Str(approval, "decision_note", "n/a")}\n\n" +
               $"Created At: {Str(approval, "created_at")}\nDecided At: {Str(approval, "decided_at", "n/a")}\n\n" +
               $"Metadata:\n{Json.Dumps(metadata, indented: true)}{relatedPatch}{applyLine}\n\n" +
               "Safety Note: /apply only works when approval is approved and both write gates are enabled.";
    }

    // ---- missions / patches / sources views -------------------------------

    public string FormatMissionDetail(string missionId)
    {
        var mission = Memory.GetMission(missionId);
        if (mission is null) return $"No mission found with id: {missionId}";
        var tasks = Memory.GetTasksForMission(missionId);
        var goal = TextUtil.Truncate(Str(mission, "goal"), 600, "...[goal truncated]");
        var userResult = TextUtil.Truncate(Str(mission, "user_result", Str(mission, "final_result")), 1200, "...[result truncated]");
        var taskLines = tasks.Select(t =>
            $"- {Str(t, "id")} | {Str(t, "assigned_ant")} | {Str(t, "task_type")} | {Str(t, "status")} | {Str(t, "elapsed_seconds", "0")}s | {Str(t, "title")}\n" +
            $"  Summary: {TextUtil.Truncate(Str(t, "result_summary", Str(t, "result")), 260, "...[summary truncated]")}");
        var tasksBlock = tasks.Count > 0 ? string.Join("\n", taskLines) : "No tasks saved for this mission.";
        return $"Mission ID: {Str(mission, "id")}\nStatus: {Str(mission, "status")}\nPheromone Score: {Str(mission, "success_score")}\n" +
               $"Best Output Task ID: {Str(mission, "best_output_task_id", "n/a")}\nCreated At: {Str(mission, "created_at")}\nSaved At: {Str(mission, "saved_at")}\n\n" +
               $"Goal:\n{goal}\n\nUser Result Preview:\n{userResult}\n\nTasks:\n{tasksBlock}";
    }

    public string FormatMissionHistory(int limit = 10)
    {
        var rows = Memory.GetRecentMissions(limit);
        if (rows.Count == 0) return "No missions in ANTHILL memory yet.";
        var blocks = rows.Select(m =>
            $"Mission ID: {Str(m, "id")} | Status: {Str(m, "status")} | Score: {Str(m, "success_score")}\n" +
            $"Goal: {TextUtil.Truncate(Str(m, "goal"), 200, "...[goal truncated]")}\nSaved At: {Str(m, "saved_at")}");
        return $"ANTHILL Mission History | count={rows.Count}\n\n" + string.Join(Divider, blocks);
    }

    public string FormatPatchList(int limit = 20)
    {
        var patches = Memory.ListPatchProposals(limit: limit);
        if (patches.Count == 0) return "No patch proposals recorded.";
        var blocks = patches.Select(p =>
            $"Patch ID: {Str(p, "id")} | Status: {Str(p, "status")} | Change: {Str(p, "change_type")}\n" +
            $"File: {Str(p, "file_path")}\nReason: {TextUtil.Truncate(Str(p, "reason"), 200, "...[reason truncated]")}\nMission ID: {Str(p, "mission_id")}");
        return $"Patch Proposals | count={patches.Count}\n\n" + string.Join(Divider, blocks);
    }

    public string FormatPatchDetail(string patchId)
    {
        try { patchId = Validation.ValidatePatchId(patchId); }
        catch (Exception e) { return $"Invalid patch id: {e.Message}"; }
        var patch = Memory.GetPatchProposal(patchId);
        if (patch is null) return $"No patch proposal found with id: {patchId}";
        return $"Patch ID: {Str(patch, "id")}\nStatus: {Str(patch, "status")}\nChange Type: {Str(patch, "change_type")}\nFile: {Str(patch, "file_path")}\n" +
               $"Mission ID: {Str(patch, "mission_id")}\nTask ID: {Str(patch, "task_id")}\n\nReason:\n{Str(patch, "reason")}\n\nRisk:\n{Str(patch, "risk")}\n\n" +
               $"Old Content:\n{TextUtil.Truncate(Str(patch, "old_content"), AnthillRuntime.MaxPatchContentChars, "...[old_content truncated]")}\n\n" +
               $"New Content:\n{TextUtil.Truncate(Str(patch, "new_content"), AnthillRuntime.MaxPatchContentChars, "...[new_content truncated]")}\n\n" +
               $"Applied At: {Str(patch, "applied_at", "n/a")}\nBackup Path: {Str(patch, "backup_path", "n/a")}\nLast Error: {Str(patch, "last_error", "n/a")}";
    }

    public string FormatSources(int limit = 20)
    {
        var rows = Memory.GetRecentSources(limit);
        if (rows.Count == 0) return "No source records saved.";
        var blocks = rows.Select(s =>
            $"Source ID: {Str(s, "id")} | Confidence: {Str(s, "confidence_label")} ({Str(s, "confidence_score")})\n" +
            $"Title: {Str(s, "title")}\nDomain: {Str(s, "domain")}\nURL: {Str(s, "url")}\nSummary: {TextUtil.Truncate(Str(s, "summary"), 240, "...[summary truncated]")}");
        return $"Saved Sources | count={rows.Count}\n\n" + string.Join(Divider, blocks);
    }

    public string FormatSourceDetail(string sourceId)
    {
        try { sourceId = Validation.ValidateSourceId(sourceId); }
        catch (Exception e) { return $"Invalid source id: {e.Message}"; }
        var s = Memory.GetSourceRecord(sourceId);
        if (s is null) return $"No source record found with id: {sourceId}";
        return $"Source ID: {Str(s, "id")}\nMission ID: {Str(s, "mission_id")}\nTask ID: {Str(s, "task_id", "n/a")}\nTitle: {Str(s, "title")}\n" +
               $"Domain: {Str(s, "domain")}\nURL: {Str(s, "url")}\nProvider: {Str(s, "provider")}\n" +
               $"Scores: relevance={Str(s, "relevance_score")} freshness={Str(s, "freshness_score")} authority={Str(s, "authority_score")} confidence={Str(s, "confidence_score")} ({Str(s, "confidence_label")})\n" +
               $"Quality Notes: {Str(s, "quality_notes")}\n\nSnippet:\n{Str(s, "snippet")}\n\nSummary:\n{Str(s, "summary")}";
    }

    public string FormatSourceQuality(int limit = 20)
    {
        var rows = Memory.GetSourceQualitySummary(limit);
        if (rows.Count == 0) return "No source quality data yet.";
        var blocks = rows.Select(r =>
            $"{Str(r, "domain")} | sources={Str(r, "source_count")} | avg_confidence={Str(r, "avg_confidence")} | " +
            $"avg_relevance={Str(r, "avg_relevance")} | avg_authority={Str(r, "avg_authority")} | last_seen={Str(r, "last_seen")}");
        return $"Source Quality by Domain | count={rows.Count}\n\n" + string.Join("\n", blocks);
    }

    // ---- diagnostics / status views --------------------------------------

    public string FormatMemoryView(int limit = 10) =>
        $"ANTHILL Recent Memory (limit={limit})\n\n{Memory.FormatRecentMemory(limit, AnthillRuntime.MemoryResultChars)}";

    public string FormatPheromoneView(int limit = 15) =>
        $"ANTHILL Pheromone Trails (top {limit})\n\n{Memory.FormatPheromoneContext(limit)}";

    public string FormatConfigStatus()
    {
        var c = AnthillRuntime.Config;
        return $"ANTHILL v{AnthillRuntime.Version} Configuration\nSafety Profile: {c.SafetyProfile}\nConfig Path: {AnthillRuntime.ConfigPath}\n" +
               $"Workspace Root: {AnthillRuntime.WorkspaceRootPath}\nDB Path: {AnthillRuntime.DbPath}\n" +
               $"API Auth Enabled: {AnthillRuntime.EnableApiAuth}\nAPI Host: {AnthillRuntime.ApiHost}\nAPI Port: {AnthillRuntime.ApiPort}\n" +
               $"Web Search: {AnthillRuntime.EnableWebSearch}\nFile Tools: {AnthillRuntime.EnableFileTools}\nShell Tool: {AnthillRuntime.EnableShellTool}\n" +
               $"Patch Application: {AnthillRuntime.EnablePatchApplication}\nFile Writing: {AnthillRuntime.EnableFileWriting}\n" +
               $"Parallel Execution: {AnthillRuntime.EnableParallelExecution} (workers={AnthillRuntime.MaxParallelWorkers})\n" +
               $"Native Kernel: {(Native.NativeKernel.UsingNative ? "active" : "managed-fallback")}";
    }

    public string FormatSchemaStatus()
    {
        var status = Memory.GetSchemaStatus();
        return $"ANTHILL Schema Status\nAnthill Version: {status.GetValueOrDefault("anthill_version")}\n" +
               $"Expected Schema Version: {status.GetValueOrDefault("expected_schema_version")}\n" +
               $"Recorded Schema Version: {status.GetValueOrDefault("schema_version")}\n" +
               $"Migration Count: {status.GetValueOrDefault("migration_count")}";
    }

    public string FormatSystemStatus()
    {
        var events = Memory.SummarizeEvents();
        var tasks = Memory.SummarizeTaskMetrics();
        var pendingApprovals = Memory.CountPendingApprovals();
        return $"ANTHILL v{AnthillRuntime.Version} System Status\nSafety Profile: {AnthillRuntime.Config.SafetyProfile}\n" +
               $"Native Kernel: {(Native.NativeKernel.UsingNative ? "active" : "managed-fallback")}\n" +
               $"Events Logged: {events.GetValueOrDefault("event_count")}\nFailure Events: {events.GetValueOrDefault("failure_event_count")}\n" +
               $"Tasks Recorded: {tasks.GetValueOrDefault("task_count")}\nFailed Tasks: {tasks.GetValueOrDefault("failed_count")}\nSkipped Tasks: {tasks.GetValueOrDefault("skipped_count")}\n" +
               $"Pending Approvals: {pendingApprovals}\nModel Calls This Session: {Router?.CallCount ?? 0}";
    }

    public string FormatRuntimeDiagnostics()
    {
        var failures = Memory.GetRecentFailureEvents(AnthillRuntime.DiagnosticEventLimit);
        var header = $"ANTHILL v{AnthillRuntime.Version} Runtime Diagnostics\nRecent Failure Events: {failures.Count}\n";
        if (failures.Count == 0) return header + "\nNo recent failure events. The colony is healthy.";
        var blocks = failures.Select(f =>
            $"[{Str(f, "event_type")}] {Str(f, "created_at")}\nMission: {Str(f, "mission_id")} | Task: {Str(f, "task_id", "n/a")} | Ant: {Str(f, "ant_name", "n/a")}\n" +
            $"Message: {TextUtil.Truncate(Str(f, "message"), 300, "...[message truncated]")}");
        return header + "\n" + string.Join(Divider, blocks);
    }

    public string FormatEventLog(int limit = 30, string? eventType = null, string? missionId = null)
    {
        var rows = Memory.GetRecentEvents(limit, eventType, missionId);
        if (rows.Count == 0) return "No events recorded.";
        var blocks = rows.Select(e =>
            $"[{Str(e, "event_type")}] {Str(e, "created_at")}\nMission: {Str(e, "mission_id")} | Task: {Str(e, "task_id", "n/a")} | Ant: {Str(e, "ant_name", "n/a")}\n" +
            $"{TextUtil.Truncate(Str(e, "message"), 300, "...[message truncated]")}");
        return $"ANTHILL Event Log | count={rows.Count}\n\n" + string.Join("\n\n", blocks);
    }

    public string FormatTaskMetrics()
    {
        var m = Memory.SummarizeTaskMetrics();
        return $"ANTHILL Task Metrics\nTasks: {m.GetValueOrDefault("task_count")}\nAvg Elapsed: {m.GetValueOrDefault("avg_elapsed_seconds")}s\n" +
               $"Max Elapsed: {m.GetValueOrDefault("max_elapsed_seconds")}s\nFailed: {m.GetValueOrDefault("failed_count")}\nSkipped: {m.GetValueOrDefault("skipped_count")}";
    }

    public string FormatMessageMetrics()
    {
        var m = Memory.SummarizeMessageMetrics();
        return $"ANTHILL Message Metrics\nMetrics: {m.GetValueOrDefault("metric_count")}\nInput Chars: {m.GetValueOrDefault("input_chars")}\n" +
               $"Output Chars: {m.GetValueOrDefault("output_chars")}\nInput Tokens (est): {m.GetValueOrDefault("input_tokens_est")}\nOutput Tokens (est): {m.GetValueOrDefault("output_tokens_est")}";
    }

    public string FormatAgentCommunication(int limit = 30, string? missionId = null)
    {
        var summary = Memory.SummarizeAgentMessages();
        var rows = Memory.GetRecentAgentMessages(limit, missionId);
        var header = $"ANTHILL v{AnthillRuntime.Version} Agent Communication Ledger\nSchema: {AnthillRuntime.AgentMessageVersion}\n" +
                     $"Enabled: {(AnthillRuntime.EnableAgentCommunicationLedger ? "ON" : "OFF")}\nLimit: {limit}\n" +
                     $"Message Count: {summary.GetValueOrDefault("message_count")}\n";
        if (rows.Count == 0) return header + "\nNo agent messages recorded yet.";
        var blocks = rows.Select(r =>
            $"Message ID: {Str(r, "id")}\nCreated At: {Str(r, "created_at")}\nMission ID: {Str(r, "mission_id")}\nTask ID: {Str(r, "task_id", "n/a")}\n" +
            $"Route: {Str(r, "sender")} -> {Str(r, "recipient")}\nType: {Str(r, "message_type")} | Schema: {Str(r, "schema_version")}\n" +
            $"Chars/Tokens: {Str(r, "content_chars")} / {Str(r, "estimated_tokens")}\nContent:\n{TextUtil.Truncate(Str(r, "content"), 700, "...[content truncated]")}");
        return header + "\n" + string.Join(Divider, blocks);
    }

    public string FormatModelRoutes() => Router is null ? "Model routing is disabled." : Router.FormatRoutes();
    public string FormatModelStatus() => Router is null ? "Model routing is disabled." : Router.FormatModels();

    // ---- task graph -------------------------------------------------------

    public Dictionary<string, object?> BuildTaskGraphData(string? missionId = null, bool includeResults = false, bool includeResultPreview = true)
    {
        if (!AnthillRuntime.EnableTaskGraphExport)
            return new() { ["schema_version"] = AnthillRuntime.TaskGraphVersion, ["enabled"] = false, ["nodes"] = new List<object>(), ["edges"] = new List<object>() };
        missionId ??= LatestMissionId();
        if (missionId is null)
            return new() { ["schema_version"] = AnthillRuntime.TaskGraphVersion, ["enabled"] = true, ["mission_id"] = null, ["nodes"] = new List<object>(), ["edges"] = new List<object>() };
        var mission = Memory.GetMission(missionId);
        if (mission is null)
            return new() { ["schema_version"] = AnthillRuntime.TaskGraphVersion, ["enabled"] = true, ["mission_id"] = missionId, ["nodes"] = new List<object>(), ["edges"] = new List<object>(), ["error"] = "mission_not_found" };

        var tasks = Memory.GetTasksForMission(missionId, 300);
        var edges = new List<Dictionary<string, string>>();
        var childIds = new Dictionary<string, List<string>>();
        var parsed = new List<(Dictionary<string, object?> Row, List<string> Deps, List<string> Parents)>();

        foreach (var row in tasks)
        {
            var deps = ParseJsonStringList(Str(row, "depends_on_json", "[]"));
            var parents = ParseJsonStringList(Str(row, "parent_task_ids_json", "[]"));
            var pid = Str(row, "parent_task_id");
            if (!string.IsNullOrEmpty(pid) && !parents.Contains(pid)) parents.Add(pid);
            var id = Str(row, "id");
            childIds.TryAdd(id, new());
            foreach (var dep in deps) { childIds.TryAdd(dep, new()); childIds[dep].Add(id); edges.Add(new() { ["from"] = dep, ["to"] = id, ["type"] = "depends_on" }); }
            foreach (var parent in parents) { childIds.TryAdd(parent, new()); childIds[parent].Add(id); edges.Add(new() { ["from"] = parent, ["to"] = id, ["type"] = "parent_task" }); }
            parsed.Add((row, deps, parents));
        }

        var nodes = new List<Dictionary<string, object?>>();
        foreach (var (row, deps, parents) in parsed)
        {
            var status = Str(row, "status", "unknown");
            string? statusMessage = status switch
            {
                "failed" => Str(row, "failure_reason"),
                "skipped" => Str(row, "skipped_reason"),
                "blocked" => Str(row, "blocked_reason"),
                "ready" => "Dependencies satisfied; task is ready to run.",
                _ => null,
            };
            var id = Str(row, "id");
            var node = new Dictionary<string, object?>
            {
                ["task_id"] = id, ["mission_id"] = missionId, ["title"] = Str(row, "title"), ["name"] = Str(row, "title"),
                ["assigned_ant"] = Str(row, "assigned_ant", "unknown"), ["assigned_agent"] = Str(row, "assigned_ant", "unknown"),
                ["role"] = Str(row, "assigned_ant", "unknown"), ["task_type"] = Str(row, "task_type", "general"), ["status"] = status,
                ["dependency_ids"] = deps, ["depends_on"] = deps, ["parent_task_id"] = row.GetValueOrDefault("parent_task_id"),
                ["parent_task_ids"] = parents, ["child_task_ids"] = childIds.GetValueOrDefault(id, new()).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
                ["attempt_count"] = row.GetValueOrDefault("attempt_count"), ["max_attempts"] = row.GetValueOrDefault("max_attempts"),
                ["created_at"] = row.GetValueOrDefault("created_at"), ["started_at"] = row.GetValueOrDefault("started_at"),
                ["completed_at"] = row.GetValueOrDefault("completed_at"), ["failed_at"] = row.GetValueOrDefault("failed_at"),
                ["skipped_at"] = row.GetValueOrDefault("skipped_at"), ["failure_type"] = row.GetValueOrDefault("failure_type"),
                ["status_message"] = statusMessage, ["elapsed_seconds"] = row.GetValueOrDefault("elapsed_seconds"),
            };
            if (includeResultPreview && !string.IsNullOrEmpty(Str(row, "result_summary")))
            {
                var redacted = System.Text.RegularExpressions.Regex.Replace(Str(row, "result_summary"),
                    @"(?i)\b(token|api[_-]?key|password|passwd|secret|authorization)\b\s*[:=]\s*[^,\s;]+", "$1=[redacted]");
                node["result_summary_preview"] = TextUtil.Truncate(redacted, 240, "...[preview truncated]");
            }
            if (includeResults) node["result_summary"] = row.GetValueOrDefault("result_summary");
            nodes.Add(node);
        }

        var statusCounts = nodes.GroupBy(n => n.GetValueOrDefault("status")?.ToString() ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var antCounts = nodes.GroupBy(n => n.GetValueOrDefault("assigned_ant")?.ToString() ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var safeMission = includeResults ? mission : new Dictionary<string, object?>
        {
            ["id"] = mission.GetValueOrDefault("id"), ["goal"] = mission.GetValueOrDefault("goal"), ["status"] = mission.GetValueOrDefault("status"),
            ["best_output_task_id"] = mission.GetValueOrDefault("best_output_task_id"), ["success_score"] = mission.GetValueOrDefault("success_score"),
            ["created_at"] = mission.GetValueOrDefault("created_at"), ["saved_at"] = mission.GetValueOrDefault("saved_at"),
        };
        return new()
        {
            ["schema_version"] = AnthillRuntime.TaskGraphVersion, ["enabled"] = true, ["mission"] = safeMission, ["mission_id"] = missionId,
            ["nodes"] = nodes, ["edges"] = edges, ["status_counts"] = statusCounts, ["ant_counts"] = antCounts,
        };
    }

    public string FormatTaskGraph(string? missionId = null)
    {
        var graph = BuildTaskGraphData(missionId);
        if (graph.TryGetValue("error", out var err) && err is not null)
            return $"Task graph error: {err} for mission {graph.GetValueOrDefault("mission_id")}";
        var mission = graph.GetValueOrDefault("mission") as Dictionary<string, object?> ?? new();
        var nodes = graph.GetValueOrDefault("nodes") as List<Dictionary<string, object?>> ?? new();
        var edges = graph.GetValueOrDefault("edges") as List<Dictionary<string, string>> ?? new();
        var header = $"ANTHILL v{AnthillRuntime.Version} Task Graph\nSchema: {AnthillRuntime.TaskGraphVersion}\n" +
                     $"Mission ID: {graph.GetValueOrDefault("mission_id") ?? "n/a"}\nMission Status: {mission.GetValueOrDefault("status") ?? "n/a"}\n" +
                     $"Goal: {TextUtil.Truncate(mission.GetValueOrDefault("goal")?.ToString() ?? "", 250, "...[goal truncated]")}\n" +
                     $"Nodes: {nodes.Count} | Edges: {edges.Count}\n";
        if (nodes.Count == 0) return header + "\nNo task graph nodes found.";
        var lines = nodes.Select(n =>
        {
            var deps = string.Join(", ", (n.GetValueOrDefault("dependency_ids") as List<string>) ?? new());
            if (deps.Length == 0) deps = "none";
            var preview = n.GetValueOrDefault("result_summary_preview")?.ToString() ?? n.GetValueOrDefault("status_message")?.ToString() ?? "";
            return $"[{n.GetValueOrDefault("status")}] {n.GetValueOrDefault("assigned_agent")}::{n.GetValueOrDefault("task_type")}\n" +
                   $"Task ID: {n.GetValueOrDefault("task_id")}\nTitle: {n.GetValueOrDefault("title")}\nDepends On: {deps}\n" +
                   $"Attempts: {n.GetValueOrDefault("attempt_count")}/{n.GetValueOrDefault("max_attempts")}\nElapsed: {n.GetValueOrDefault("elapsed_seconds") ?? "n/a"}\n" +
                   $"Status Message: {TextUtil.Truncate(preview, 240, "...[message truncated]")}";
        });
        return header + "\n\n" + string.Join(Divider, lines);
    }

    // ---- result composition -----------------------------------------------

    public string? SelectBestOutputTaskId(Mission mission)
    {
        var builder = mission.Tasks.LastOrDefault(t => t.AssignedAnt == "builder" && t.Status == TaskStatus.Complete && !string.IsNullOrEmpty(t.Result));
        if (builder is not null) return builder.Id;
        var coder = mission.Tasks.LastOrDefault(t => t.AssignedAnt == "coder" && t.Status == TaskStatus.Complete && !string.IsNullOrEmpty(t.Result));
        if (coder is not null) return coder.Id;
        var completed = mission.Tasks.LastOrDefault(t => t.Status == TaskStatus.Complete && !string.IsNullOrEmpty(t.Result));
        return completed?.Id;
    }

    public string ComposeUserResult(Mission mission)
    {
        if (mission.BestOutputTaskId is not null)
        {
            var best = mission.Tasks.FirstOrDefault(t => t.Id == mission.BestOutputTaskId && !string.IsNullOrEmpty(t.Result));
            if (best is not null) return best.Result!;
        }
        var fallbackId = SelectBestOutputTaskId(mission);
        if (fallbackId is not null)
        {
            var task = mission.Tasks.FirstOrDefault(t => t.Id == fallbackId && !string.IsNullOrEmpty(t.Result));
            if (task is not null) return task.Result!;
        }
        return "Mission produced no completed user-facing output.";
    }

    public string ComposeDebugResult(Mission mission) => string.Join("\n", mission.Tasks.Select(t =>
        $"Task: {t.Title}\nTask ID: {t.Id}\nAnt: {t.AssignedAnt}\nTask Type: {t.TaskType}\nDepends On: [{string.Join(", ", t.DependsOn)}]\n" +
        $"Parent Task IDs: [{string.Join(", ", t.ParentTaskIds)}]\nStatus: {t.Status.Value()}\nResult Chars: {t.ResultChars}\n" +
        $"Estimated Tokens: {t.EstimatedTokens}\nResult Summary:\n{t.ResultSummary}\n\nFull Result:\n{t.Result}\n"));

    public string ComposeCliResult(Mission mission)
    {
        var header = mission.Status == MissionStatus.Complete ? "Mission Complete"
            : mission.Status == MissionStatus.Partial ? "Mission Partial" : "Mission Failed";
        var score = mission.SuccessScore?.ToString() ?? "Not scored yet";
        var debugTrace = TextUtil.Truncate(mission.DebugResult ?? "", 5000, "...[debug trace truncated for CLI; full trace saved in debug_result]");
        var pending = Memory.CountPendingApprovals();
        var approvalNote = pending > 0
            ? $"\n\nPending Approval Requests: {pending}\nUse /approvals to list them."
            : "\n\nPending Approval Requests: 0";
        return $"{header}\n\nGoal:\n{mission.Goal}\n\nMission Status:\n{mission.Status.Value()}\n\nPheromone Score:\n{score}\n\n" +
               $"Best Output Task ID:\n{mission.BestOutputTaskId ?? "n/a"}\n\nUser Result:\n{mission.UserResult}{approvalNote}\n\nDebug Trace:\n\n{debugTrace}";
    }

    public string? LatestMissionId()
    {
        if (LastMissionId is not null) return LastMissionId;
        var recent = Memory.GetRecentMissions(1);
        return recent.Count > 0 ? Str(recent[0], "id") : null;
    }

    private static Dictionary<string, object?> ParseMetadata(Dictionary<string, object?> row)
    {
        try
        {
            var json = row.GetValueOrDefault("metadata_json")?.ToString() ?? "{}";
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new();
        }
        catch { return new(); }
    }

    private static List<string> ParseJsonStringList(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }
}
