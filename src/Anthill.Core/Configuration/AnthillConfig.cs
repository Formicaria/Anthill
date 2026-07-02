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
    // ---- Phase 3: concurrency (ResourceGovernor) ----
    // Upper bound on missions the Director may run at once. The ResourceGovernor can only ever
    // lower the effective value below this cap (host load / model-backend pressure), never raise it.
    [JsonPropertyName("autonomy_concurrency")] public int AutonomyConcurrency { get; set; } = 1;
    // Anti-starvation aging: a ready objective gains +1 effective priority for every this-many
    // minutes it has waited since its last run (or creation). 0 disables aging (pure strict priority).
    [JsonPropertyName("autonomy_aging_minutes")] public int AutonomyAgingMinutes { get; set; } = 30;

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
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
    };
}
