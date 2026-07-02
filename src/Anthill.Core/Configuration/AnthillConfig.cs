using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anthill.Core.Configuration;

/// <summary>
/// Runtime configuration envelope for ANTHILL's .NET build.
///
/// This is the direct successor to the Pydantic <c>AnthillConfig</c> from the
/// Python harness. It is a plain serialisable record-style class so it round-trips
/// through System.Text.Json the same way the original round-tripped through Pydantic.
/// Future versions can move implementation around it without changing callers.
/// </summary>
public sealed class AnthillConfig
{
    [JsonPropertyName("config_version")] public string ConfigVersion { get; set; } = "config-v1";
    [JsonPropertyName("safety_profile")] public string SafetyProfile { get; set; } = "SAFE_LOCAL";

    [JsonPropertyName("workspace_root")] public string WorkspaceRoot { get; set; } = AnthillRuntime.DefaultWorkspace;
    [JsonPropertyName("db_path")] public string DbPath { get; set; } = $"{AnthillRuntime.DefaultWorkspace}/anthill.db";
    [JsonPropertyName("backup_dir")] public string BackupDir { get; set; } = $"{AnthillRuntime.DefaultWorkspace}/backups";
    [JsonPropertyName("logs_dir")] public string LogsDir { get; set; } = $"{AnthillRuntime.DefaultWorkspace}/logs";
    [JsonPropertyName("exports_dir")] public string ExportsDir { get; set; } = $"{AnthillRuntime.DefaultWorkspace}/exports";
    [JsonPropertyName("agent_workspace_dir")] public string AgentWorkspaceDir { get; set; } = $"{AnthillRuntime.DefaultWorkspace}/workspace";

    // Defaults to all interfaces so a fresh container/LXC/Windows-Service deployment is reachable
    // out of the box, like a normal container — the operator login (not network isolation) is
    // what protects the API. Set to 127.0.0.1 (or ANTHILL_HOST=127.0.0.1) for localhost-only.
    [JsonPropertyName("api_host")] public string ApiHost { get; set; } = "0.0.0.0";
    [JsonPropertyName("api_port")] public int ApiPort { get; set; } = 8713;
    [JsonPropertyName("api_auth_enabled")] public bool ApiAuthEnabled { get; set; } = true;
    [JsonPropertyName("api_token_env")] public string ApiTokenEnv { get; set; } = "ANTHILL_API_TOKEN";
    [JsonPropertyName("cors_enabled")] public bool CorsEnabled { get; set; } = false;
    [JsonPropertyName("api_job_workers")] public int ApiJobWorkers { get; set; } = 1;

    [JsonPropertyName("use_ollama")] public bool UseOllama { get; set; } = true;
    [JsonPropertyName("ollama_model")] public string OllamaModel { get; set; } = "llama3.1:8b";
    [JsonPropertyName("ollama_host")] public string OllamaHost { get; set; } = "http://localhost:11434";
    [JsonPropertyName("model_routes")] public Dictionary<string, Dictionary<string, string>> ModelRoutes { get; set; } = new();

    [JsonPropertyName("web_search_enabled")] public bool WebSearchEnabled { get; set; } = false;
    [JsonPropertyName("patch_application_enabled")] public bool PatchApplicationEnabled { get; set; } = false;
    [JsonPropertyName("file_writing_enabled")] public bool FileWritingEnabled { get; set; } = false;
    [JsonPropertyName("shell_tool_enabled")] public bool ShellToolEnabled { get; set; } = false;
    [JsonPropertyName("file_tools_enabled")] public bool FileToolsEnabled { get; set; } = true;
    // Operator shell console (Configuration -> Shell): an interactive host terminal for admins
    // ONLY. Distinct from shell_tool_enabled (which gates the AI ants' allowlisted shell tool) —
    // this is arbitrary command execution by a logged-in human admin, every command audit-logged.
    // It is host remote-code-execution by design; keep it off on anything network-exposed you
    // don't fully trust. Default working directory for the console (blank = agent_workspace_dir).
    [JsonPropertyName("operator_shell_enabled")] public bool OperatorShellEnabled { get; set; } = true;
    [JsonPropertyName("operator_shell_dir")] public string OperatorShellDir { get; set; } = "";

    [JsonPropertyName("parallel_execution_enabled")] public bool ParallelExecutionEnabled { get; set; } = true;
    [JsonPropertyName("max_parallel_workers")] public int MaxParallelWorkers { get; set; } = 3;
    [JsonPropertyName("max_web_searches_per_mission")] public int MaxWebSearchesPerMission { get; set; } = 3;
    [JsonPropertyName("max_sources_per_mission")] public int MaxSourcesPerMission { get; set; } = 15;
    [JsonPropertyName("max_context_packet_chars")] public int MaxContextPacketChars { get; set; } = 7000;
    [JsonPropertyName("max_agent_message_content_chars")] public int MaxAgentMessageContentChars { get; set; } = 2200;

    // ---- Long-input / specification-ingestion handling ----
    // When a mission goal is larger than long_input_threshold characters, the Queen stops
    // dumping the whole document into one task and instead splits it into bounded section
    // analysis tasks (run in parallel), then a synthesis task, then verification.
    [JsonPropertyName("spec_ingestion_enabled")] public bool SpecIngestionEnabled { get; set; } = true;
    [JsonPropertyName("long_input_threshold")] public int LongInputThreshold { get; set; } = 6000;
    [JsonPropertyName("max_section_chars")] public int MaxSectionChars { get; set; } = 3500;
    [JsonPropertyName("max_section_tasks")] public int MaxSectionTasks { get; set; } = 6;
    // Maintenance / disk hygiene. A full DB copy is written before every mission; keep only the
    // newest N to bound the backup directory (the main source of disk bloat). event_retention_days
    // > 0 lets Flush Cache delete events older than that many days (0 = keep all).
    [JsonPropertyName("max_db_backups")] public int MaxDbBackups { get; set; } = 10;
    [JsonPropertyName("event_retention_days")] public int EventRetentionDays { get; set; } = 0;

    // ---- 24/7 autonomy (Phase 0 rails) ----
    // The Director only runs when BOTH autonomy_enabled is true AND it is started explicitly
    // (CLI --autonomous / API). All values default to safe/off. See docs/AUTONOMY.md.
    [JsonPropertyName("autonomy_enabled")] public bool AutonomyEnabled { get; set; } = false;
    [JsonPropertyName("autonomy_poll_seconds")] public int AutonomyPollSeconds { get; set; } = 30;
    [JsonPropertyName("autonomy_max_missions_per_hour")] public int AutonomyMaxMissionsPerHour { get; set; } = 6;
    [JsonPropertyName("autonomy_max_missions_per_day")] public int AutonomyMaxMissionsPerDay { get; set; } = 60;
    [JsonPropertyName("autonomy_max_consecutive_failures")] public int AutonomyMaxConsecutiveFailures { get; set; } = 3;
    // ---- Phase 2: Strategist (self-generated missions) ----
    [JsonPropertyName("autonomy_dedupe_similarity")] public double AutonomyDedupeSimilarity { get; set; } = 0.8;
    [JsonPropertyName("autonomy_max_followups_per_run")] public int AutonomyMaxFollowupsPerRun { get; set; } = 1;
    [JsonPropertyName("autonomy_max_objective_depth")] public int AutonomyMaxObjectiveDepth { get; set; } = 3;
    // Hard cap on the open backlog (pending + active). The Strategist stops enqueuing self-generated
    // follow-up objectives once the backlog reaches this size, bounding sprawl. 0 = no cap.
    [JsonPropertyName("autonomy_max_backlog")] public int AutonomyMaxBacklog { get; set; } = 40;
    // ---- Phase 3: concurrency (ResourceGovernor) ----
    // Upper bound on missions the Director may run at once. The ResourceGovernor can only ever
    // lower the effective value below this cap (host load / model-backend pressure), never raise it.
    [JsonPropertyName("autonomy_concurrency")] public int AutonomyConcurrency { get; set; } = 1;
    // Anti-starvation aging: a ready objective gains +1 effective priority for every this-many
    // minutes it has waited since its last run (or creation). 0 disables aging (pure strict priority).
    [JsonPropertyName("autonomy_aging_minutes")] public int AutonomyAgingMinutes { get; set; } = 30;
    // ---- Phase 4: learning loop ----
    // Mission outcomes bias objective selection: each objective keeps a success-score EMA; at
    // selection time it contributes up to ±autonomy_priority_bias_max effective priority points
    // (read-time only — stored priorities never drift). Objectives that keep failing to produce
    // value (low EMA over enough runs) or loop on near-identical generated goals are auto-paused
    // with an objective_retired event for human review.
    [JsonPropertyName("autonomy_learning_enabled")] public bool AutonomyLearningEnabled { get; set; } = true;
    [JsonPropertyName("autonomy_priority_bias_max")] public int AutonomyPriorityBiasMax { get; set; } = 2;
    [JsonPropertyName("autonomy_score_ema_alpha")] public double AutonomyScoreEmaAlpha { get; set; } = 0.3;
    [JsonPropertyName("autonomy_retire_min_runs")] public int AutonomyRetireMinRuns { get; set; } = 5;
    [JsonPropertyName("autonomy_retire_score_threshold")] public double AutonomyRetireScoreThreshold { get; set; } = 0.25;
    // How many recent generated goals to compare for loop detection (0 = off). Uses
    // autonomy_dedupe_similarity as the overlap threshold, same metric as Strategist dedup.
    [JsonPropertyName("autonomy_loop_window")] public int AutonomyLoopWindow { get; set; } = 4;
    // ---- Phase 5: gated auto-apply ----
    // The Director may auto-approve + apply a coder patch WITHOUT human review, but only when the
    // patch clears a strict allowlist AND the workspace still builds + tests green afterward; a
    // red verify auto-rolls-back from the pre-apply backup. Fail-closed: OFF by default, and with
    // an EMPTY path allowlist nothing is ever eligible even when enabled. Requires the
    // patch_application_enabled + file_writing_enabled write gates to also be on.
    [JsonPropertyName("autonomy_autoapply_enabled")] public bool AutonomyAutoApplyEnabled { get; set; } = false;
    // Glob patterns (workspace-relative) a patch's file_path must match to be auto-appliable.
    // Empty = nothing is eligible. e.g. ["docs/**", "src/**/*.cs"].
    [JsonPropertyName("autonomy_autoapply_paths")] public List<string> AutonomyAutoApplyPaths { get; set; } = new();
    // Max changed lines (new_content line count) a single patch may have to auto-apply.
    [JsonPropertyName("autonomy_autoapply_max_lines")] public int AutonomyAutoApplyMaxLines { get; set; } = 40;
    // Verify command run in the workspace after apply; empty = built-in `dotnet build` + `dotnet test`.
    [JsonPropertyName("autonomy_autoapply_verify_cmd")] public string AutonomyAutoApplyVerifyCmd { get; set; } = "";
    // Hard timeout (seconds) for the verify step.
    [JsonPropertyName("autonomy_autoapply_verify_timeout")] public int AutonomyAutoApplyVerifyTimeout { get; set; } = 900;
    // After a green verify, also `git add` + `git commit` the change locally (never pushed). Off = leave on disk.
    [JsonPropertyName("autonomy_autoapply_git_commit")] public bool AutonomyAutoApplyGitCommit { get; set; } = false;

    /// <summary>
    /// Safety-profile overrides applied before the user's on-disk config is merged on top.
    /// Mirrors <c>_safety_profile_overrides</c> in the Python runtime: every shipped profile
    /// keeps the system fail-closed (no shell, no writes, auth always on). Binding defaults to
    /// all interfaces (container/appliance-friendly) because <c>ApiAuthEnabled</c> is forced true
    /// here too — the operator login, not network isolation, is the security boundary. Set
    /// api_host to 127.0.0.1 (or ANTHILL_HOST=127.0.0.1) explicitly for a localhost-only install.
    /// </summary>
    public static void ApplySafetyProfile(AnthillConfig config, string profile)
    {
        var normalized = (profile ?? "SAFE_LOCAL").Trim().ToUpperInvariant();
        // All four shipped profiles are conservative; RESEARCH_LOCAL / POWER_USER merely
        // permit read-only web search. Writes and shell stay off everywhere by default.
        var webSearch = normalized is "RESEARCH_LOCAL" or "POWER_USER";
        config.WebSearchEnabled = webSearch;
        config.PatchApplicationEnabled = false;
        config.FileWritingEnabled = false;
        config.ShellToolEnabled = false;
        config.ApiAuthEnabled = true;
        config.ApiHost = "0.0.0.0";
        config.ApiJobWorkers = 1;
        // Autonomy is fail-closed across every shipped profile; the user must opt in explicitly.
        config.AutonomyEnabled = false;
        // Phase 5 auto-apply is the highest-risk capability (autonomous writes) — always off in
        // every shipped profile, re-enabled only by an explicit operator edit.
        config.AutonomyAutoApplyEnabled = false;
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
    };
}
