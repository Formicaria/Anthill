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
    [JsonPropertyName("api_job_workers")] public int ApiJobWorkers { get; set; } = 1;

    [JsonPropertyName("use_ollama")] public bool UseOllama { get; set; } = true;
    /// <summary>
    /// The local model to run on. EMPTY BY DEFAULT, and deliberately so. v3.8.33.
    ///
    /// This used to default to `llama3.1:8b`, which is a guess about someone else's machine: Ollama
    /// has no default model, and what you can run is whatever you chose to pull. On a host without
    /// that exact tag every ant call failed with `model not found` while the console still reported
    /// Ollama reachable.
    ///
    /// Empty means "not chosen", which <see cref="Anthill.Core.Models.LocalModelResolver"/> resolves
    /// against what the host actually holds — and which it refuses rather than guesses when the host
    /// holds several.
    /// </summary>
    [JsonPropertyName("ollama_model")] public string OllamaModel { get; set; } = "";
    [JsonPropertyName("ollama_host")] public string OllamaHost { get; set; } = "http://localhost:11434";
    [JsonPropertyName("model_routes")] public Dictionary<string, Dictionary<string, string>> ModelRoutes { get; set; } = new();

    /// <summary>
    /// v3.8.1 — one model every ant tries FIRST, whatever its own route says.
    ///
    /// Two settings rather than one flag, because "which model" and "does it take precedence" are
    /// questions an operator answers at different moments: naming a model is a setup step, promoting
    /// it over every per-ant route is an operational decision made when a better model arrives or
    /// the routed one goes missing. Empty means no priority — per-ant routing behaves exactly as
    /// before, which is what makes this safe to leave unset forever.
    /// </summary>
    [JsonPropertyName("model_priority_provider")] public string ModelPriorityProvider { get; set; } = "";
    [JsonPropertyName("model_priority_model")] public string ModelPriorityModel { get; set; } = "";

    [JsonPropertyName("web_search_enabled")] public bool WebSearchEnabled { get; set; } = false;
    [JsonPropertyName("patch_application_enabled")] public bool PatchApplicationEnabled { get; set; } = false;
    [JsonPropertyName("file_writing_enabled")] public bool FileWritingEnabled { get; set; } = false;
    [JsonPropertyName("shell_tool_enabled")] public bool ShellToolEnabled { get; set; } = false;
    [JsonPropertyName("file_tools_enabled")] public bool FileToolsEnabled { get; set; } = true;

    // v3.4.1: operator-defined tools. OFF by default, like every capability that reaches outside the
    // process. The host allowlist is the real boundary, not the flag: an HTTP tool whose host is not
    // listed can reach nothing, so turning the feature on by itself grants no network access. An
    // empty allowlist is therefore a usable, deliberate state and NOT treated as "allow everything".
    [JsonPropertyName("user_tools_enabled")] public bool UserToolsEnabled { get; set; } = false;
    [JsonPropertyName("user_tool_allowed_hosts")] public List<string> UserToolAllowedHosts { get; set; } = new();

    /// <summary>
    /// v0.3.8.73 — THE OPERATOR HALF of "verification commands come from the manifest or operator
    /// configuration, never model invention." Only the manifest half was ever built.
    ///
    /// Absent or empty means unchanged: detection answers, exactly as before. A non-empty list
    /// REPLACES detection for this installation, because an operator who states what verifies their
    /// workspace is stating a fact about it, and appending the detected checks back on would make
    /// the setting advisory.
    ///
    /// This lives in ANTHILL's configuration and NOT in the workspace being modified — that
    /// direction is the whole security property, and it is why there is no `.anthill-checks.json`.
    /// A coding agent can edit the repository it is working in; it cannot edit this.
    /// </summary>
    [JsonPropertyName("workspace_checks")] public List<ConfiguredCheck> WorkspaceChecks { get; set; } = new();
    // Operator shell console (Configuration -> Shell): an interactive host terminal for admins
    // ONLY. Distinct from shell_tool_enabled (which gates the AI ants' allowlisted shell tool) —
    // this is arbitrary command execution by a logged-in human admin, every command audit-logged.
    // It is host remote-code-execution by design; keep it off on anything network-exposed you
    // don't fully trust. Default working directory for the console (blank = agent_workspace_dir).
    /// <summary>
    /// v0.3.8.40 — "desktop", "server", or "auto" (the default, which detects).
    ///
    /// Anthill on a laptop is a personal assistant; in an LXC or Docker host it is a shared control
    /// plane expected to manage infrastructure. Declared once here rather than inferred separately
    /// by each feature that cares, because two features inferring it independently will disagree on
    /// exactly the host where the answer matters.
    /// </summary>
    [JsonPropertyName("deployment_mode")] public string DeploymentMode { get; set; } = "auto";
    /// <summary>
    /// v0.3.8.40 — whether APPROVED container actions may actually run. Off by default.
    ///
    /// Dry run works regardless: an operator can see exactly what would happen before deciding to
    /// enable this. An execute path nobody has watched run is not something to switch on for them.
    /// </summary>
    [JsonPropertyName("docker_execute_enabled")] public bool DockerExecuteEnabled { get; set; } = false;
    [JsonPropertyName("operator_shell_enabled")] public bool OperatorShellEnabled { get; set; } = true;
    [JsonPropertyName("operator_shell_dir")] public string OperatorShellDir { get; set; } = "";

    // Homelab foundation (v1.9.0, NORTH_STAR Phase 4): read-only subsystem, everything off by default.
    [JsonPropertyName("homelab_enabled")] public bool HomelabEnabled { get; set; } = false;
    [JsonPropertyName("homelab_scheduler_enabled")] public bool HomelabSchedulerEnabled { get; set; } = false;
    [JsonPropertyName("homelab_mock_providers_enabled")] public bool HomelabMockProvidersEnabled { get; set; } = false;
    [JsonPropertyName("homelab_max_concurrent_checks")] public int HomelabMaxConcurrentChecks { get; set; } = 2;
    // Health checks + notifications (v1.11.0, NORTH_STAR Phase 7): awareness only, no auto-remediation.
    [JsonPropertyName("homelab_health_interval_seconds")] public int HomelabHealthIntervalSeconds { get; set; } = 60;
    [JsonPropertyName("homelab_health_timeout_ms")] public int HomelabHealthTimeoutMs { get; set; } = 5000;
    [JsonPropertyName("homelab_notifications_enabled")] public bool HomelabNotificationsEnabled { get; set; } = false;
    /// <summary>MICROMOUND optional integration — off unless an operator turns it on.</summary>
    [JsonPropertyName("micromound_enabled")] public bool MicromoundEnabled { get; set; } = false;
    [JsonPropertyName("homelab_automation_enabled")] public bool HomelabAutomationEnabled { get; set; } = false;
    /// <summary>
    /// v2.15.0: nullable on purpose. The default flipped from off to on, and a plain bool cannot
    /// tell "this config predates the setting" from "the operator turned it off". Null means
    /// unset and resolves to the current default (on); an explicit false is always respected, so
    /// nobody who deliberately disabled the workspace gets it switched back on by an upgrade.
    /// ProjectConfig writes the resolved value back, so it becomes explicit on the next save.
    /// </summary>
    [JsonPropertyName("dashboard_workspace_enabled")] public bool? DashboardWorkspaceEnabled { get; set; }
    /// <summary>
    /// v2.16.0: write a concise plain-English answer at mission completion instead of surfacing
    /// the best task's raw output verbatim. Costs one model call per mission; turning it off
    /// restores the previous behaviour exactly.
    /// </summary>
    [JsonPropertyName("answer_synthesis_enabled")] public bool AnswerSynthesisEnabled { get; set; } = true;
    [JsonPropertyName("sandbox_execution_enabled")] public bool SandboxExecutionEnabled { get; set; } = false;
    /// <summary>
    /// The roster profile: one switch instead of nine. v3.8.26.
    ///
    /// Turning the colony on required setting `specialist_ant_execution_enabled`, an activation
    /// tier, and six separate `*_ant_enabled` flags — nine unrelated keys an operator had to know
    /// about and get consistent. Getting one wrong produces a role that is silently absent, and
    /// nothing correlated the nine into "is the roster on".
    ///
    /// <c>"core"</c> runs the six original ants and nothing else. <c>"full"</c> enables all twelve
    /// mission roles, handoff ingestion and bounded adaptive control.
    ///
    /// v0.3.8.41 — THE DEFAULT IS NOW <c>full</c>, and the honest basis for that is worth stating
    /// because it is narrower than it sounds. Every one of the twelve roles is STRUCTURALLY
    /// qualified — handler, contract, declared tools registered, required capabilities grantable
    /// (<c>FullRosterQualificationTests</c>) — and every one now has a production trigger: policy
    /// insertion for tester, soldier and verifier, failure for medic, post-finalization for the
    /// archivist, the plan for the rest. A role that is qualified and switched off by default is a
    /// role nobody runs.
    ///
    /// What does NOT yet exist is a deterministic Queen-driven acceptance suite that reaches all
    /// twelve through those triggers in one real mission. That gap is recorded in
    /// <c>docs/PLAN.md</c> §6 and is the next release's work. Flipping the default makes the gap
    /// more visible rather than smaller, which is the argument for doing it now: the roster is where
    /// operators will find what the fixture cannot.
    ///
    /// Existing installations are NOT switched over blindly. <see cref="ConfigSchema"/> migrates only
    /// a configuration that still matches the untouched legacy defaults exactly; any explicit
    /// operator choice — including a deliberate <c>core</c> recorded at schema version 2 or later —
    /// is preserved, and <see cref="DisabledRoles"/> always survives.
    /// </summary>
    [JsonPropertyName("roster_profile")] public string RosterProfile { get; set; } = RosterProfiles.Full;

    /// <summary>
    /// The configuration schema version this document was written at. v0.3.8.41.
    ///
    /// It exists to make one distinction that is otherwise unrepresentable: whether
    /// <c>roster_profile: "core"</c> is a choice or a leftover default. Below
    /// <see cref="ConfigSchema.Current"/> it is a leftover; at or above it, it is a choice. See
    /// <see cref="ConfigSchema"/>.
    /// </summary>
    [JsonPropertyName("config_schema_version")] public int ConfigSchemaVersion { get; set; } = ConfigSchema.Current;

    /// <summary>
    /// Per-role kill switches that survive the profile. v3.8.26.
    ///
    /// `roster_profile: "full"` with `disabled_roles: ["scribe"]` means "everything except the
    /// scribe" — the rollback path when one role misbehaves, without abandoning the profile and
    /// hand-setting the other five. Named explicitly rather than inferred from an unset boolean,
    /// because JSON cannot distinguish "false" from "absent" and a rollback that depends on that
    /// distinction is a rollback that fails when someone tidies the config.
    /// </summary>
    [JsonPropertyName("disabled_roles")] public string[] DisabledRoles { get; set; } = Array.Empty<string>();

    [JsonPropertyName("specialist_ant_execution_enabled")] public bool SpecialistAntExecutionEnabled { get; set; } = false;
    [JsonPropertyName("tester_ant_enabled")] public bool TesterAntEnabled { get; set; } = false;
    [JsonPropertyName("soldier_ant_enabled")] public bool SoldierAntEnabled { get; set; } = false;
    [JsonPropertyName("medic_ant_enabled")] public bool MedicAntEnabled { get; set; } = false;
    [JsonPropertyName("archivist_ant_enabled")] public bool ArchivistAntEnabled { get; set; } = false;
    /// <summary>v2.21.0 Phase A: admit specialist handoffs as real follow-up tasks. Off by default
    /// — this is the first feature that lets a mission grow its own task list at runtime.</summary>
    [JsonPropertyName("handoff_ingestion_enabled")] public bool HandoffIngestionEnabled { get; set; } = false;
    /// <summary>v2.21.0 Phase B: the adaptive controller may add bounded repair/verification tasks
    /// and stop a stalled mission. Off by default — it changes when a mission ends.</summary>
    [JsonPropertyName("adaptive_mission_control_enabled")] public bool AdaptiveMissionControlEnabled { get; set; } = false;
    /// <summary>
    /// v2.22.0 Phase D: how much of the colony may run — "core" | "adaptive" | "full".
    ///
    /// A CEILING, not a switch: per-role rollout flags still apply on top, so raising the tier can
    /// never turn a role on by itself. Narrowing it CAN turn roles off, which is the point.
    ///
    /// Defaults to "full" — meaning "defer entirely to the per-role flags", i.e. exactly the
    /// behaviour before this setting existed. Defaulting to "core" would have silently stopped
    /// specialists in every deployment that had already enabled them, on upgrade, with nothing
    /// announcing it. Safety here comes from the per-role flags, which are all off by default;
    /// the tier exists so an operator can additionally say "never run anything beyond X",
    /// whatever those flags say.
    ///
    /// Unrecognised values resolve to "core" — a typo must narrow, never widen.
    /// </summary>
    [JsonPropertyName("activation_tier")] public string ActivationTier { get; set; } = "full";
    /// <summary>v2.24.0: also require the deliverable the goal asked for before calling a mission
    /// verified. Additive — it can only narrow. Off by default: a change to what counts as success
    /// must be switched on deliberately, not arrive with an upgrade.</summary>
    [JsonPropertyName("objective_verification_enabled")] public bool ObjectiveVerificationEnabled { get; set; } = false;
    /// <summary>v2.24.0 Phase E: shadow mode observes real incidents and records what it WOULD have
    /// done. It never executes. Off by default — an observer that silently starts writing
    /// recommendations about production incidents should not arrive with an upgrade.</summary>
    [JsonPropertyName("shadow_observation_enabled")] public bool ShadowObservationEnabled { get; set; } = false;

    // v2.25.0 Phase F: the operator-defined readiness thresholds (NORTH_STAR: "meet
    // operator-defined thresholds" — so they are config, not constants). Defaults are deliberately
    // conservative; loosening them is an explicit operator decision in config, on the record.
    [JsonPropertyName("readiness_min_shadow_sample")] public int ReadinessMinShadowSample { get; set; } = 10;
    [JsonPropertyName("readiness_min_diagnosis_precision")] public double ReadinessMinDiagnosisPrecision { get; set; } = 0.8;
    [JsonPropertyName("readiness_min_action_accuracy")] public double ReadinessMinActionAccuracy { get; set; } = 0.8;
    [JsonPropertyName("ui_cartographer_ant_enabled")] public bool UiCartographerAntEnabled { get; set; } = false;
    [JsonPropertyName("scribe_ant_enabled")] public bool ScribeAntEnabled { get; set; } = false;
    [JsonPropertyName("homelab_slack_webhook")] public string HomelabSlackWebhook { get; set; } = "";
    [JsonPropertyName("homelab_discord_webhook")] public string HomelabDiscordWebhook { get; set; } = "";
    [JsonPropertyName("homelab_generic_webhook")] public string HomelabGenericWebhook { get; set; } = "";
    // Proxmox read-only integration (v1.12.0, NORTH_STAR Phase 8). GET-only by construction; the
    // API token lives in the homelab credential store (never here), referenced by credential id.
    [JsonPropertyName("homelab_proxmox_enabled")] public bool HomelabProxmoxEnabled { get; set; } = false;
    [JsonPropertyName("homelab_proxmox_host")] public string HomelabProxmoxHost { get; set; } = "";
    [JsonPropertyName("homelab_proxmox_port")] public int HomelabProxmoxPort { get; set; } = 8006;
    [JsonPropertyName("homelab_proxmox_credential_id")] public string HomelabProxmoxCredentialId { get; set; } = "proxmox-main";
    [JsonPropertyName("homelab_proxmox_insecure_tls")] public bool HomelabProxmoxInsecureTls { get; set; } = false;
    // v2.2.0: protocol is separate from TLS verification. "https" (default) or "http" for a PVE
    // reachable only over plain http; auth headers are attached identically in every mode.
    [JsonPropertyName("homelab_proxmox_protocol")] public string HomelabProxmoxProtocol { get; set; } = "https";
    [JsonPropertyName("homelab_proxmox_write_actions_enabled")] public bool HomelabProxmoxWriteActionsEnabled { get; set; } = false;
    [JsonPropertyName("homelab_proxmox_sync_interval_seconds")] public int HomelabProxmoxSyncIntervalSeconds { get; set; } = 300;
    [JsonPropertyName("homelab_arr_sync_interval_seconds")] public int HomelabArrSyncIntervalSeconds { get; set; } = 300;
    // Read-only virtualization integrations (v2.1.0). Each mirrors Proxmox: no write path exists in the
    // client, the secret lives in the credential store (referenced by id, never here), and the host must
    // be on the target allowlist. ESXi/vCenter = vSphere REST; Docker = Engine API; Hyper-V = WinRM (WMI
    // read-only Enumerate). All disabled by default.
    [JsonPropertyName("homelab_esxi_enabled")] public bool HomelabEsxiEnabled { get; set; } = false;
    [JsonPropertyName("homelab_esxi_host")] public string HomelabEsxiHost { get; set; } = "";
    [JsonPropertyName("homelab_esxi_port")] public int HomelabEsxiPort { get; set; } = 443;
    [JsonPropertyName("homelab_esxi_credential_id")] public string HomelabEsxiCredentialId { get; set; } = "esxi-main";
    [JsonPropertyName("homelab_esxi_insecure_tls")] public bool HomelabEsxiInsecureTls { get; set; } = false;
    [JsonPropertyName("homelab_esxi_sync_interval_seconds")] public int HomelabEsxiSyncIntervalSeconds { get; set; } = 300;
    [JsonPropertyName("homelab_docker_enabled")] public bool HomelabDockerEnabled { get; set; } = false;
    [JsonPropertyName("homelab_docker_host")] public string HomelabDockerHost { get; set; } = "";
    [JsonPropertyName("homelab_docker_port")] public int HomelabDockerPort { get; set; } = 2376;
    [JsonPropertyName("homelab_docker_credential_id")] public string HomelabDockerCredentialId { get; set; } = "docker-main";
    [JsonPropertyName("homelab_docker_insecure_tls")] public bool HomelabDockerInsecureTls { get; set; } = false;
    [JsonPropertyName("homelab_docker_sync_interval_seconds")] public int HomelabDockerSyncIntervalSeconds { get; set; } = 300;
    [JsonPropertyName("homelab_hyperv_enabled")] public bool HomelabHypervEnabled { get; set; } = false;
    [JsonPropertyName("homelab_hyperv_host")] public string HomelabHypervHost { get; set; } = "";
    [JsonPropertyName("homelab_hyperv_port")] public int HomelabHypervPort { get; set; } = 5986;
    [JsonPropertyName("homelab_hyperv_credential_id")] public string HomelabHypervCredentialId { get; set; } = "hyperv-main";
    [JsonPropertyName("homelab_hyperv_insecure_tls")] public bool HomelabHypervInsecureTls { get; set; } = false;
    [JsonPropertyName("homelab_hyperv_sync_interval_seconds")] public int HomelabHypervSyncIntervalSeconds { get; set; } = 300;
    // Network + security awareness (v1.13.0, NORTH_STAR Phase 9): deterministic findings, no scanning.
    [JsonPropertyName("homelab_risk_interval_seconds")] public int HomelabRiskIntervalSeconds { get; set; } = 3600;
    // Incident + change memory (v1.14.0, NORTH_STAR Phase 10): tracking + recommendations, no auto-fixes.
    [JsonPropertyName("homelab_incident_sweep_seconds")] public int HomelabIncidentSweepSeconds { get; set; } = 300;

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
    // v1.8.16 lifecycle hardening: let a successful one-shot or verification-only objective end
    // cleanly as Completed/Stopped instead of looping until loop detection retires it. Loop
    // detection stays available for true repeated loops. On by default; disable to restore the
    // pre-v1.8.16 "run until a rail stops it" behaviour.
    [JsonPropertyName("autonomy_oneshot_completion")] public bool AutonomyOneShotCompletion { get; set; } = true;
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
    // After a green verify, also `git add` + `git commit` the change locally on the standalone branch. Off = leave on disk.
    [JsonPropertyName("autonomy_autoapply_git_commit")] public bool AutonomyAutoApplyGitCommit { get; set; } = false;
    // v1.8.26: git integration for auto-apply. Commits (and optionally pushes) verified changes to a
    // dedicated standalone branch — NEVER main. The SSH deploy key is referenced by PATH on the host,
    // never stored as key material. Branch name is derived as "<git_username>-anthill".
    [JsonPropertyName("autonomy_autoapply_git_push")] public bool AutonomyAutoApplyGitPush { get; set; } = false;
    [JsonPropertyName("autonomy_autoapply_git_remote")] public string AutonomyAutoApplyGitRemote { get; set; } = "origin";
    [JsonPropertyName("autonomy_autoapply_git_username")] public string AutonomyAutoApplyGitUsername { get; set; } = "";
    [JsonPropertyName("autonomy_autoapply_git_ssh_key_path")] public string AutonomyAutoApplyGitSshKeyPath { get; set; } = "";
    // v1.8.21 fix: on deployments with no build toolchain (e.g. a published-binary LXC), the built-in
    // `dotnet build && dotnet test` verify always fails, so every auto-applied patch is rolled back and
    // nothing ever persists. When this is true AND no verify command is configured, auto-apply KEEPS the
    // applied patches without a verify gate (the operator's explicit, riskier choice). Ignored when a
    // verify command IS set — that always runs and gates keep/rollback. Default false = safe (verify).
    [JsonPropertyName("autonomy_autoapply_keep_without_verify")] public bool AutonomyAutoApplyKeepWithoutVerify { get; set; } = false;

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
