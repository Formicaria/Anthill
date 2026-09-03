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
    [ConfigKey(Security = ConfigSecurity.Safety)]
    [JsonPropertyName("safety_profile")] public string SafetyProfile { get; set; } = "SAFE_LOCAL";

    [ConfigKey(Security = ConfigSecurity.Environment, EnvOverride = "ANTHILL_HOME")]
    [JsonPropertyName("workspace_root")] public string WorkspaceRoot { get; set; } = AnthillRuntime.DefaultWorkspace;
    [ConfigKey(Security = ConfigSecurity.Environment)]
    [JsonPropertyName("db_path")] public string DbPath { get; set; } = $"{AnthillRuntime.DefaultWorkspace}/anthill.db";
    [ConfigKey(Security = ConfigSecurity.Environment)]
    [JsonPropertyName("backup_dir")] public string BackupDir { get; set; } = $"{AnthillRuntime.DefaultWorkspace}/backups";
    [ConfigKey(Security = ConfigSecurity.Environment)]
    [JsonPropertyName("logs_dir")] public string LogsDir { get; set; } = $"{AnthillRuntime.DefaultWorkspace}/logs";
    [ConfigKey(Security = ConfigSecurity.Environment)]
    [JsonPropertyName("exports_dir")] public string ExportsDir { get; set; } = $"{AnthillRuntime.DefaultWorkspace}/exports";
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Environment,
        Section = "workspace", SectionNote = """Path the file ant is allowed to READ from. Set to your project source root.""",
        ExampleJson = "\"/path/to/your/project\"")]
    [JsonPropertyName("agent_workspace_dir")] public string AgentWorkspaceDir { get; set; } = $"{AnthillRuntime.DefaultWorkspace}/workspace";

    // Defaults to all interfaces so a fresh container/LXC/Windows-Service deployment is reachable
    // out of the box, like a normal container — the operator login (not network isolation) is
    // what protects the API. Set to 127.0.0.1 (or ANTHILL_HOST=127.0.0.1) for localhost-only.
    [ConfigKey(Security = ConfigSecurity.Environment, EnvOverride = "ANTHILL_HOST",
        Section = "api", SectionNote = """api_host: 0.0.0.0 (default) = accept from all interfaces, like a normal container/service — reachable at this machine's LAN IP with zero config. Set to 127.0.0.1 for localhost-only. Either can also be set via the ANTHILL_HOST / ANTHILL_PORT env vars (highest precedence, handy for Docker/LXC/Windows Service). On first launch the UI guides you through creating the admin account; on a non-loopback bind that step also requires the one-time setup token this process prints at startup (see _comment_setup_token). ANTHILL_API_TOKEN (optional, >= 32 chars) provides a programmatic bearer token for scripts/CI alongside normal operator accounts.""")]
    [JsonPropertyName("api_host")] public string ApiHost { get; set; } = "0.0.0.0";
    [ConfigKey(Security = ConfigSecurity.Environment, EnvOverride = "ANTHILL_PORT", Min = 1, Max = 65535)]
    [JsonPropertyName("api_port")] public int ApiPort { get; set; } = 8713;
    [ConfigKey(Security = ConfigSecurity.Safety)]
    [JsonPropertyName("api_auth_enabled")] public bool ApiAuthEnabled { get; set; } = true;
    [ConfigKey(Security = ConfigSecurity.Environment,
        Section = "token_env", SectionNote = """v0.3.8.91: api_token_env names the environment variable the optional programmatic bearer token is read from. It is now the ONLY source - it used to fall back to whatever ANTHILL_API_TOKEN held, so repointing this at a variable you had not set kept authenticating against the one you had just stopped using. Unset means no static token, which is a safe state: operator accounts are the real boundary.""",
        ExampleJson = "\"ANTHILL_API_TOKEN\"")]
    [JsonPropertyName("api_token_env")] public string ApiTokenEnv { get; set; } = "ANTHILL_API_TOKEN";
    [JsonPropertyName("api_job_workers")] public int ApiJobWorkers { get; set; } = 1;

    [ConfigKey(Exposure = ConfigExposure.Editable,
        Section = "ollama", SectionNote = """Set ollama_host to the IP:port of the machine running Ollama. If Ollama runs on the same machine as ANTHILL, use http://localhost:11434. If Ollama runs on a different machine (or ANTHILL runs in a container in bridge-network mode), use http://OLLAMA_MACHINE_IP:11434 (and make sure Ollama is bound to 0.0.0.0, not just 127.0.0.1). Can also be set via ANTHILL_OLLAMA_HOST / ANTHILL_OLLAMA_MODEL env vars, which take precedence over this file.""")]
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
    [ConfigKey(Exposure = ConfigExposure.Editable, EnvOverride = "ANTHILL_OLLAMA_MODEL",
        ExampleJson = "\"llama3.1:8b\"")]
    [JsonPropertyName("ollama_model")] public string OllamaModel { get; set; } = "";
    [ConfigKey(
        Exposure = ConfigExposure.Editable,
        Security = ConfigSecurity.Environment,
        EnvOverride = "ANTHILL_OLLAMA_HOST",
        ExampleJson = "\"http://YOUR_OLLAMA_HOST_IP:11434\"")]
    [JsonPropertyName("ollama_host")] public string OllamaHost { get; set; } = "http://localhost:11434";
    [ConfigKey(Exposure = ConfigExposure.Editable,
        Section = "routes", SectionNote = """Each ant can use a different model. All models must be pulled on the Ollama machine (ollama pull <model>). Smaller models are fine for web/fallback.""",
        ExampleJson = "{\"planner\": {\"provider\": \"ollama\",\"model\": \"llama3.3:70b\"},\"researcher\": {\"provider\": \"ollama\",\"model\": \"llama3.3:70b\"},\"coder\": {\"provider\": \"ollama\",\"model\": \"qwen2.5-coder:32b\"},\"builder\": {\"provider\": \"ollama\",\"model\": \"qwen2.5-coder:32b\"},\"verifier\": {\"provider\": \"ollama\",\"model\": \"llama3.3:70b\"},\"web\": {\"provider\": \"ollama\",\"model\": \"llama3.1:8b\"},\"fallback\": {\"provider\": \"ollama\",\"model\": \"llama3.1:8b\"}}")]
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
    [ConfigKey(Exposure = ConfigExposure.Editable, UndocumentedBecause = "console-managed (Routing inspector)")]
    [JsonPropertyName("model_priority_provider")] public string ModelPriorityProvider { get; set; } = "";
    [ConfigKey(Exposure = ConfigExposure.Editable, UndocumentedBecause = "console-managed (Routing inspector)")]
    [JsonPropertyName("model_priority_model")] public string ModelPriorityModel { get; set; } = "";

    /// <summary>
    /// v0.3.8.90 — what each provider's model costs, per million tokens. EMPTY BY DEFAULT.
    ///
    /// The colony ships with no prices on purpose: a rate compiled into the source is wrong for
    /// somebody the day it ships, and R4's exit gate asks for cost in the OPERATOR's currency. Keys
    /// are `provider/model`, and `provider/*` prices a whole provider — which is how a local Ollama
    /// run becomes a measured zero rather than an assumed one. See <see cref="ModelPricing"/>.
    /// </summary>
    [JsonPropertyName("model_pricing")] public Dictionary<string, ModelPrice> ModelPricing { get; set; } = new();
    [ConfigKey(Section = "pricing", SectionNote = """v0.3.8.90 — what your provider charges, per MILLION tokens, in your currency. EMPTY BY DEFAULT and deliberately so: the colony measures tokens, and only you know what they cost. Keys are 'provider/model'; 'provider/*' prices a whole provider, which is how a local Ollama run reports a MEASURED zero instead of an unknown (the colony will not assume local is free on your behalf). A run is priced only when EVERY model it used has an entry and every call reported usage — a partial total would understate what the run cost, and an understated figure in an operator report is worse than an absent one. Read by the live qualification record; see docs/QUALIFICATION.md.""")]
    [JsonPropertyName("model_pricing_currency")] public string ModelPricingCurrency { get; set; } = "USD";

    [ConfigKey(Exposure = ConfigExposure.Editable,
        Section = "features", SectionNote = """All feature gates default safe. Enable what you need.""")]
    [JsonPropertyName("web_search_enabled")] public bool WebSearchEnabled { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("patch_application_enabled")] public bool PatchApplicationEnabled { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("file_writing_enabled")] public bool FileWritingEnabled { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("shell_tool_enabled")] public bool ShellToolEnabled { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("file_tools_enabled")] public bool FileToolsEnabled { get; set; } = true;

    // v3.4.1: operator-defined tools. OFF by default, like every capability that reaches outside the
    // process. The host allowlist is the real boundary, not the flag: an HTTP tool whose host is not
    // listed can reach nothing, so turning the feature on by itself grants no network access. An
    // empty allowlist is therefore a usable, deliberate state and NOT treated as "allow everything".
    [ConfigKey(
        Exposure = ConfigExposure.Editable,
        Security = ConfigSecurity.Safety,
        UndocumentedBecause = "console-managed (operator-defined tools)")]
    [JsonPropertyName("user_tools_enabled")] public bool UserToolsEnabled { get; set; } = false;
    [ConfigKey(
        Exposure = ConfigExposure.Editable,
        Security = ConfigSecurity.Safety,
        UndocumentedBecause = "console-managed")]
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
    [ConfigKey(
        Exposure = ConfigExposure.Editable,
        Security = ConfigSecurity.Safety,
        UndocumentedBecause = "file-only by design; see the v0.3.8.73 note on its declaration")]
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
    [ConfigKey(UndocumentedBecause = "detected; the console shows it read-only")]
    [JsonPropertyName("deployment_mode")] public string DeploymentMode { get; set; } = "auto";
    /// <summary>
    /// v0.3.8.40 — whether APPROVED container actions may actually run. Off by default.
    ///
    /// Dry run works regardless: an operator can see exactly what would happen before deciding to
    /// enable this. An execute path nobody has watched run is not something to switch on for them.
    /// </summary>
    [ConfigKey(
        Security = ConfigSecurity.Safety,
        UndocumentedBecause = "module surface, not a general operator setting")]
    [JsonPropertyName("docker_execute_enabled")] public bool DockerExecuteEnabled { get; set; } = false;
    // v0.3.8.91: DEFAULT FLIPPED TO FALSE. This grants an authenticated administrator arbitrary
    // command execution on the host, which the comment above already said. Shipping it on meant a
    // fresh, network-reachable install was one account-creation away from a host shell — and until
    // this release that account could be created by anyone who reached the port. An operator who
    // wants the terminal turns it on deliberately, like every other capability that reaches outside
    // the colony. An existing config.json that already carries `true` keeps it: the raw overlay wins
    // over the profile, so this changes new installations rather than revoking a live feature.
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety,
        Section = "operator_shell", SectionNote = """Configuration -> Shell: admin-only interactive terminal into THIS host. Distinct from shell_tool_enabled (the AI ants' allowlisted tool) -- this is arbitrary command execution by a logged-in human admin, host RCE, every command audit-logged, never granted to coordinators. Turn off on untrusted networks. operator_shell_dir = default working directory (blank = agent_workspace_dir).""")]
    [JsonPropertyName("operator_shell_enabled")] public bool OperatorShellEnabled { get; set; } = false;

    /// <summary>
    /// v0.3.8.91 — force the first-run setup token even on a loopback bind.
    ///
    /// The default (`false`) means "decide from the bind": loopback needs no token, anything else
    /// does. Set it true for the one shape this process cannot see — a reverse proxy in front of a
    /// loopback bind, where every request arrives from localhost and the real boundary is the proxy.
    /// Also settable as ANTHILL_REQUIRE_SETUP_TOKEN=1.
    /// </summary>
    [ConfigKey(Security = ConfigSecurity.Safety, EnvOverride = "ANTHILL_REQUIRE_SETUP_TOKEN",
        Section = "setup_token", SectionNote = """v0.3.8.91 first-run protection. Before the first administrator exists, /auth/setup has to be reachable without a login - so on any NON-LOOPBACK bind the process mints a single-use setup token at startup, prints it to the service log, writes it to SETUP-TOKEN.txt in the workspace directory, and requires it. Setup spends it permanently. The rule reads the BIND and not the caller's address: behind a reverse proxy every request arrives from localhost, so an address rule would authorise the internet through one hop. Set this true for the one shape the bind cannot describe - a reverse proxy in front of a 127.0.0.1 bind. Also ANTHILL_REQUIRE_SETUP_TOKEN=1.""")]
    [JsonPropertyName("setup_token_required")] public bool SetupTokenRequired { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Environment)]
    [JsonPropertyName("operator_shell_dir")] public string OperatorShellDir { get; set; } = "";

    // Homelab foundation (v1.9.0, NORTH_STAR Phase 4): read-only subsystem, everything off by default.
    [ConfigKey(Exposure = ConfigExposure.Editable,
        Section = "homelab", SectionNote = """Homelab foundation (v1.9.0, NORTH_STAR Phase 4). Read-only in the V1.9.x line and everything ships OFF: homelab_enabled gates the subsystem, homelab_scheduler_enabled gates the background runner (no jobs are registered in v1.9.0), and a .anthill/HOMELAB_STOP file halts all homelab actions once actions exist (V2.1). Deterministic providers may only reach hosts on the operator-maintained target allowlist (POST /homelab/allowlist) - the general SSRF guard for AI tools is unaffected. See docs/HOMELAB.md.""")]
    [JsonPropertyName("homelab_enabled")] public bool HomelabEnabled { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("homelab_scheduler_enabled")] public bool HomelabSchedulerEnabled { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("homelab_mock_providers_enabled")] public bool HomelabMockProvidersEnabled { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("homelab_max_concurrent_checks")] public int HomelabMaxConcurrentChecks { get; set; } = 2;
    // Health checks + notifications (v1.11.0, NORTH_STAR Phase 7): awareness only, no auto-remediation.
    [ConfigKey(Exposure = ConfigExposure.Editable,
        Section = "homelab_health", SectionNote = """Health checks + notifications (v1.11.0). Checks run on the shared homelab scheduler (homelab_enabled + homelab_scheduler_enabled both true), only against allowlisted targets, under strict timeouts, and never auto-remediate. Notifications are OFF by default; set homelab_notifications_enabled=true and one or more webhook URLs to get alerts on health-check failures and incident candidates (3 consecutive failures). Webhook URLs never appear in logs or events.""")]
    [JsonPropertyName("homelab_health_interval_seconds")] public int HomelabHealthIntervalSeconds { get; set; } = 60;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("homelab_health_timeout_ms")] public int HomelabHealthTimeoutMs { get; set; } = 5000;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("homelab_notifications_enabled")] public bool HomelabNotificationsEnabled { get; set; } = false;
    /// <summary>MICROMOUND optional integration — off unless an operator turns it on.</summary>
    [ConfigKey(Security = ConfigSecurity.Safety, UndocumentedBecause = "optional compile-time integration")]
    [JsonPropertyName("micromound_enabled")] public bool MicromoundEnabled { get; set; } = false;
    [JsonPropertyName("homelab_automation_enabled")] public bool HomelabAutomationEnabled { get; set; } = false;
    /// <summary>
    /// v2.15.0: nullable on purpose. The default flipped from off to on, and a plain bool cannot
    /// tell "this config predates the setting" from "the operator turned it off". Null means
    /// unset and resolves to the current default (on); an explicit false is always respected, so
    /// nobody who deliberately disabled the workspace gets it switched back on by an upgrade.
    /// ProjectConfig writes the resolved value back, so it becomes explicit on the next save.
    /// </summary>
    [ConfigKey(ExampleJson = "true")]
    [JsonPropertyName("dashboard_workspace_enabled")] public bool? DashboardWorkspaceEnabled { get; set; }
    /// <summary>
    /// v2.16.0: write a concise plain-English answer at mission completion instead of surfacing
    /// the best task's raw output verbatim. Costs one model call per mission; turning it off
    /// restores the previous behaviour exactly.
    /// </summary>
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("answer_synthesis_enabled")] public bool AnswerSynthesisEnabled { get; set; } = true;
    [ConfigKey(Security = ConfigSecurity.Safety)]
    [JsonPropertyName("sandbox_execution_enabled")] public bool SandboxExecutionEnabled { get; set; } = false;
    /// <summary>
    /// v0.3.8.95: the acting coder — a coder task routed to an agent CLI edits the mission's
    /// isolated worktree directly instead of emitting patch JSON. The diff is captured into the
    /// one patch pipeline while the task graph is open; the promotion gate still owns the live
    /// checkout. Default off — acting is the operator's decision.
    /// </summary>
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("acting_coder_enabled")] public bool ActingCoderEnabled { get; set; } = false;
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
    [ConfigKey(Security = ConfigSecurity.Safety,
        Section = "roster", SectionNote = """READ THIS BEFORE TRUSTING THE SEVEN FLAGS BELOW. v0.3.8.91 correction. On a config file with no config_schema_version - which this example is - the migration treats present-but-false specialist flags as an UNMIGRATED file and adopts roster_profile 'full', which forces every one of them to TRUE at runtime. They looked like off switches and were not. The real controls are roster_profile ('full' | 'core') and disabled_roles, both of which were undocumented here until now. Set roster_profile to 'core' for the minimal roster, or list individual roles in disabled_roles. The seven flags remain for pre-migration files and are the reason the migration exists.""")]
    [JsonPropertyName("roster_profile")] public string RosterProfile { get; set; } = RosterProfiles.Full;

    /// <summary>
    /// The configuration schema version this document was written at. v0.3.8.41.
    ///
    /// It exists to make one distinction that is otherwise unrepresentable: whether
    /// <c>roster_profile: "core"</c> is a choice or a leftover default. Below
    /// <see cref="ConfigSchema.Current"/> it is a leftover; at or above it, it is a choice. See
    /// <see cref="ConfigSchema"/>.
    /// </summary>
    [ConfigKey(UndocumentedBecause = "written by the migration, not by an operator")]
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
    [ConfigKey(Security = ConfigSecurity.Safety)]
    [JsonPropertyName("disabled_roles")] public string[] DisabledRoles { get; set; } = Array.Empty<string>();

    [ConfigKey(Security = ConfigSecurity.Safety)]
    [JsonPropertyName("specialist_ant_execution_enabled")] public bool SpecialistAntExecutionEnabled { get; set; } = false;
    [ConfigKey(Security = ConfigSecurity.Safety)]
    [JsonPropertyName("tester_ant_enabled")] public bool TesterAntEnabled { get; set; } = false;
    [ConfigKey(Security = ConfigSecurity.Safety)]
    [JsonPropertyName("soldier_ant_enabled")] public bool SoldierAntEnabled { get; set; } = false;
    [ConfigKey(Security = ConfigSecurity.Safety)]
    [JsonPropertyName("medic_ant_enabled")] public bool MedicAntEnabled { get; set; } = false;
    [ConfigKey(Security = ConfigSecurity.Safety)]
    [JsonPropertyName("archivist_ant_enabled")] public bool ArchivistAntEnabled { get; set; } = false;
    /// <summary>v2.21.0 Phase A: admit specialist handoffs as real follow-up tasks. Off by default
    /// — this is the first feature that lets a mission grow its own task list at runtime.</summary>
    [ConfigKey(UndocumentedBecause = "internal wiring, no operator-facing behaviour on its own")]
    [JsonPropertyName("handoff_ingestion_enabled")] public bool HandoffIngestionEnabled { get; set; } = false;
    /// <summary>v2.21.0 Phase B: the adaptive controller may add bounded repair/verification tasks
    /// and stop a stalled mission. Off by default — it changes when a mission ends.</summary>
    [ConfigKey(UndocumentedBecause = "internal wiring")]
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
    [ConfigKey(Security = ConfigSecurity.Safety, UndocumentedBecause = "console-managed")]
    [JsonPropertyName("activation_tier")] public string ActivationTier { get; set; } = "full";
    /// <summary>v2.24.0: also require the deliverable the goal asked for before calling a mission
    /// verified. Additive — it can only narrow. Off by default: a change to what counts as success
    /// must be switched on deliberately, not arrive with an upgrade.</summary>
    [ConfigKey(
        Exposure = ConfigExposure.Editable,
        Security = ConfigSecurity.Safety,
        UndocumentedBecause = "internal wiring")]
    [JsonPropertyName("objective_verification_enabled")] public bool ObjectiveVerificationEnabled { get; set; } = false;

    /// <summary>
    /// v0.3.8.103 — the destinations an external-action mission may reach, as name → url. EMPTY by
    /// default, and the emptiness is the security posture: this map IS the allowlist, so a colony
    /// that has been told about no destinations can reach none. A send naming something absent from
    /// here refuses before an operator is ever asked to approve it, with the configured names in the
    /// message so the refusal is actionable.
    /// </summary>
    [ConfigKey(Security = ConfigSecurity.Safety,
        Section = "external_destinations", SectionNote = """v0.3.8.103: the destinations an external-action mission may reach, as name -> url. THIS MAP IS THE ALLOWLIST — there is no second one, so a colony told about no destinations can reach none, and that is the shipped default. A mission naming something absent from here refuses BEFORE an operator is asked to approve anything, with the configured names in the message. The name is matched case-insensitively inside the operator's request, so "incident webhook" resolves "post the summary to the incident webhook"; two names matching one request is a refusal rather than a guess. Every send is still gated by the escalation decision and the Modify authority ceiling on top of this.""")]
    [JsonPropertyName("external_destinations")] public Dictionary<string, string> ExternalDestinations { get; set; } = new();
    /// <summary>v2.24.0 Phase E: shadow mode observes real incidents and records what it WOULD have
    /// done. It never executes. Off by default — an observer that silently starts writing
    /// recommendations about production incidents should not arrive with an upgrade.</summary>
    [ConfigKey(UndocumentedBecause = "console-managed (Readiness page)")]
    [JsonPropertyName("shadow_observation_enabled")] public bool ShadowObservationEnabled { get; set; } = false;

    // v2.25.0 Phase F: the operator-defined readiness thresholds (NORTH_STAR: "meet
    // operator-defined thresholds" — so they are config, not constants). Defaults are deliberately
    // conservative; loosening them is an explicit operator decision in config, on the record.
    [ConfigKey(UndocumentedBecause = "readiness thresholds, console-managed")]
    [JsonPropertyName("readiness_min_shadow_sample")] public int ReadinessMinShadowSample { get; set; } = 10;
    [ConfigKey(UndocumentedBecause = "readiness thresholds, console-managed")]
    [JsonPropertyName("readiness_min_diagnosis_precision")] public double ReadinessMinDiagnosisPrecision { get; set; } = 0.8;
    [ConfigKey(UndocumentedBecause = "readiness thresholds, console-managed")]
    [JsonPropertyName("readiness_min_action_accuracy")] public double ReadinessMinActionAccuracy { get; set; } = 0.8;
    [ConfigKey(Security = ConfigSecurity.Safety)]
    [JsonPropertyName("ui_cartographer_ant_enabled")] public bool UiCartographerAntEnabled { get; set; } = false;
    [ConfigKey(Security = ConfigSecurity.Safety)]
    [JsonPropertyName("scribe_ant_enabled")] public bool ScribeAntEnabled { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Secret)]
    [JsonPropertyName("homelab_slack_webhook")] public string HomelabSlackWebhook { get; set; } = "";
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Secret)]
    [JsonPropertyName("homelab_discord_webhook")] public string HomelabDiscordWebhook { get; set; } = "";
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Secret)]
    [JsonPropertyName("homelab_generic_webhook")] public string HomelabGenericWebhook { get; set; } = "";
    // Proxmox read-only integration (v1.12.0, NORTH_STAR Phase 8). GET-only by construction; the
    // API token lives in the homelab credential store (never here), referenced by credential id.
    [ConfigKey(Exposure = ConfigExposure.Editable,
        Section = "homelab_proxmox", SectionNote = """Proxmox read-only integration (v1.12.0). GET-only by construction: no start/stop/reboot/migrate/delete/clone/resize/config writes exist anywhere in the client. Setup: (1) create an API token in Proxmox (Datacenter -> Permissions -> API Tokens, PVEAuditor role is enough - read-only on purpose), (2) save it as a credential with id matching homelab_proxmox_credential_id via POST /homelab/credentials with secret 'user@realm!tokenid=SECRET', (3) add the Proxmox host to the homelab allowlist, (4) set the host below and enable. Sync rides the shared homelab scheduler. Self-signed certs: set homelab_proxmox_insecure_tls=true to skip TLS verification (homelab default reality; keep false when you have real certs).""")]
    [JsonPropertyName("homelab_proxmox_enabled")] public bool HomelabProxmoxEnabled { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Environment)]
    [JsonPropertyName("homelab_proxmox_host")] public string HomelabProxmoxHost { get; set; } = "";
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Environment)]
    [JsonPropertyName("homelab_proxmox_port")] public int HomelabProxmoxPort { get; set; } = 8006;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Environment,
        ExampleJson = "\"proxmox-main\"")]
    [JsonPropertyName("homelab_proxmox_credential_id")] public string HomelabProxmoxCredentialId { get; set; } = "proxmox-main";
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("homelab_proxmox_insecure_tls")] public bool HomelabProxmoxInsecureTls { get; set; } = false;
    // v2.2.0: protocol is separate from TLS verification. "https" (default) or "http" for a PVE
    // reachable only over plain http; auth headers are attached identically in every mode.
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("homelab_proxmox_protocol")] public string HomelabProxmoxProtocol { get; set; } = "https";
    [ConfigKey(Security = ConfigSecurity.Safety)]
    [JsonPropertyName("homelab_proxmox_write_actions_enabled")] public bool HomelabProxmoxWriteActionsEnabled { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("homelab_proxmox_sync_interval_seconds")] public int HomelabProxmoxSyncIntervalSeconds { get; set; } = 300;
    [JsonPropertyName("homelab_arr_sync_interval_seconds")] public int HomelabArrSyncIntervalSeconds { get; set; } = 300;
    // Read-only virtualization integrations (v2.1.0). Each mirrors Proxmox: no write path exists in the
    // client, the secret lives in the credential store (referenced by id, never here), and the host must
    // be on the target allowlist. ESXi/vCenter = vSphere REST; Docker = Engine API; Hyper-V = WinRM (WMI
    // read-only Enumerate). All disabled by default.
    [ConfigKey(Exposure = ConfigExposure.Editable,
        Section = "homelab_virtualization", SectionNote = """Read-only virtualization integrations (v2.1.0). Same discipline as Proxmox: the client has no write methods (no start/stop/delete), the secret lives in the credential store (referenced by id, never here), and the host must be on the homelab allowlist. Configure these from the UI (Homelab -> Virtualization Connections) or here. ESXi/vCenter = vSphere REST (built-in Read-only role is enough); Docker = Engine API over TLS (or a read-only socket proxy); Hyper-V = WinRM WMI read-only Enumerate (HTTPS, read-only account).""")]
    [JsonPropertyName("homelab_esxi_enabled")] public bool HomelabEsxiEnabled { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Environment)]
    [JsonPropertyName("homelab_esxi_host")] public string HomelabEsxiHost { get; set; } = "";
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Environment)]
    [JsonPropertyName("homelab_esxi_port")] public int HomelabEsxiPort { get; set; } = 443;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Environment,
        ExampleJson = "\"esxi-main\"")]
    [JsonPropertyName("homelab_esxi_credential_id")] public string HomelabEsxiCredentialId { get; set; } = "esxi-main";
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("homelab_esxi_insecure_tls")] public bool HomelabEsxiInsecureTls { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("homelab_esxi_sync_interval_seconds")] public int HomelabEsxiSyncIntervalSeconds { get; set; } = 300;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("homelab_docker_enabled")] public bool HomelabDockerEnabled { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Environment)]
    [JsonPropertyName("homelab_docker_host")] public string HomelabDockerHost { get; set; } = "";
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Environment)]
    [JsonPropertyName("homelab_docker_port")] public int HomelabDockerPort { get; set; } = 2376;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Environment,
        ExampleJson = "\"docker-main\"")]
    [JsonPropertyName("homelab_docker_credential_id")] public string HomelabDockerCredentialId { get; set; } = "docker-main";
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("homelab_docker_insecure_tls")] public bool HomelabDockerInsecureTls { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("homelab_docker_sync_interval_seconds")] public int HomelabDockerSyncIntervalSeconds { get; set; } = 300;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("homelab_hyperv_enabled")] public bool HomelabHypervEnabled { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Environment)]
    [JsonPropertyName("homelab_hyperv_host")] public string HomelabHypervHost { get; set; } = "";
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Environment)]
    [JsonPropertyName("homelab_hyperv_port")] public int HomelabHypervPort { get; set; } = 5986;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Environment,
        ExampleJson = "\"hyperv-main\"")]
    [JsonPropertyName("homelab_hyperv_credential_id")] public string HomelabHypervCredentialId { get; set; } = "hyperv-main";
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("homelab_hyperv_insecure_tls")] public bool HomelabHypervInsecureTls { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("homelab_hyperv_sync_interval_seconds")] public int HomelabHypervSyncIntervalSeconds { get; set; } = 300;
    // Network + security awareness (v1.13.0, NORTH_STAR Phase 9): deterministic findings, no scanning.
    [ConfigKey(Exposure = ConfigExposure.Editable,
        Section = "homelab_risk", SectionNote = """Network + security awareness (v1.13.0). Deterministic risk findings computed from the inventory you have registered/synced - risky open ports, unknown devices, ownerless services, un-backed-up hosts, exposed dashboards, duplicate IPs, missing DNS names, unwatched services, unverified credentials. Zero network I/O: there is NO active scanning in this phase. Findings reconcile on every run (fixed problems auto-resolve; acknowledgements stick).""")]
    [JsonPropertyName("homelab_risk_interval_seconds")] public int HomelabRiskIntervalSeconds { get; set; } = 3600;
    // Incident + change memory (v1.14.0, NORTH_STAR Phase 10): tracking + recommendations, no auto-fixes.
    [ConfigKey(Exposure = ConfigExposure.Editable,
        Section = "homelab_incidents", SectionNote = """Incident + change memory (v1.14.0). Health-check failure streaks auto-open deduped incidents; each incident reconstructs a timeline (suspect changes in the 24h before it broke, health results and events during it), matches similar past incidents, and surfaces their recorded root causes as 'this fixed it last time'. Tracking and recommendations only - nothing auto-remediates.""")]
    [JsonPropertyName("homelab_incident_sweep_seconds")] public int HomelabIncidentSweepSeconds { get; set; } = 300;

    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("parallel_execution_enabled")] public bool ParallelExecutionEnabled { get; set; } = true;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("max_parallel_workers")] public int MaxParallelWorkers { get; set; } = 3;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("max_web_searches_per_mission")] public int MaxWebSearchesPerMission { get; set; } = 3;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("max_sources_per_mission")] public int MaxSourcesPerMission { get; set; } = 15;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("max_context_packet_chars")] public int MaxContextPacketChars { get; set; } = 7000;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("max_agent_message_content_chars")] public int MaxAgentMessageContentChars { get; set; } = 2200;

    // ---- Long-input / specification-ingestion handling ----
    // When a mission goal is larger than long_input_threshold characters, the Queen stops
    // dumping the whole document into one task and instead splits it into bounded section
    // analysis tasks (run in parallel), then a synthesis task, then verification.
    [ConfigKey(Exposure = ConfigExposure.Editable,
        Section = "spec_ingestion", SectionNote = """Long-input handling. When a mission goal exceeds long_input_threshold characters, the Queen classifies it as spec_ingestion and splits it into bounded section-analysis tasks (run in parallel, non-critical) feeding a single synthesis task, then verification. A failed section degrades the mission to Partial but never aborts it.""")]
    [JsonPropertyName("spec_ingestion_enabled")] public bool SpecIngestionEnabled { get; set; } = true;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("long_input_threshold")] public int LongInputThreshold { get; set; } = 6000;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("max_section_chars")] public int MaxSectionChars { get; set; } = 3500;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("max_section_tasks")] public int MaxSectionTasks { get; set; } = 6;
    // Maintenance / disk hygiene. A full DB copy is written before every mission; keep only the
    // newest N to bound the backup directory (the main source of disk bloat). event_retention_days
    // > 0 lets Flush Cache delete events older than that many days (0 = keep all).
    [ConfigKey(Exposure = ConfigExposure.Editable,
        Section = "maintenance", SectionNote = """Disk hygiene. A full DB copy is written before every mission; max_db_backups keeps only the newest N (older ones pruned each mission -- the main bloat control; 0 = keep all). Flush Cache (Settings -> System Info -> Maintenance) prunes backups + compacts the DB, and deletes events older than event_retention_days (0 = keep all).""")]
    [JsonPropertyName("max_db_backups")] public int MaxDbBackups { get; set; } = 10;
    [ConfigKey(Exposure = ConfigExposure.Editable)]
    [JsonPropertyName("event_retention_days")] public int EventRetentionDays { get; set; } = 0;

    // ---- 24/7 autonomy (Phase 0 rails) ----
    // The Director only runs when BOTH autonomy_enabled is true AND it is started explicitly
    // (CLI --autonomous / API). All values default to safe/off. See docs/AUTONOMY.md.
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety,
        Section = "autonomy", SectionNote = """24/7 autonomy rails (Phase 0/1). Off by default and fail-closed: the Director only runs when autonomy_enabled is true AND it is started explicitly. Budgets are hard caps; a .anthill/STOP file (or the API) is the kill switch. See docs/AUTONOMY.md.""")]
    [JsonPropertyName("autonomy_enabled")] public bool AutonomyEnabled { get; set; } = false;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_poll_seconds")] public int AutonomyPollSeconds { get; set; } = 30;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_max_missions_per_hour")] public int AutonomyMaxMissionsPerHour { get; set; } = 6;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_max_missions_per_day")] public int AutonomyMaxMissionsPerDay { get; set; } = 60;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_max_consecutive_failures")] public int AutonomyMaxConsecutiveFailures { get; set; } = 3;
    // ---- Phase 2: Strategist (self-generated missions) ----
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety,
        Section = "strategist", SectionNote = """Phase 2: LLM-generated mission goals (falls back to charter-as-goal if no route/provider is configured, or the call fails). Dedup rejects a goal too similar to a recent run for the same objective. Follow-up objectives the colony discovers are capped by rate and parent-chain depth so the backlog can't grow unbounded. See docs/AUTONOMY.md.""")]
    [JsonPropertyName("autonomy_dedupe_similarity")] public double AutonomyDedupeSimilarity { get; set; } = 0.8;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_max_followups_per_run")] public int AutonomyMaxFollowupsPerRun { get; set; } = 1;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_max_objective_depth")] public int AutonomyMaxObjectiveDepth { get; set; } = 3;
    // Hard cap on the open backlog (pending + active). The Strategist stops enqueuing self-generated
    // follow-up objectives once the backlog reaches this size, bounding sprawl. 0 = no cap.
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_max_backlog")] public int AutonomyMaxBacklog { get; set; } = 40;
    // ---- Phase 3: concurrency (ResourceGovernor) ----
    // Upper bound on missions the Director may run at once. The ResourceGovernor can only ever
    // lower the effective value below this cap (host load / model-backend pressure), never raise it.
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety,
        Section = "concurrency", SectionNote = """Phase 3: how many autonomous missions may run at once (1-8). The ResourceGovernor can only lower the effective value below this cap — high CPU load, low free memory, or a slow/unreachable Ollama backend halve it or clamp it to 1. Scheduling stays strict-priority, but a ready objective gains +1 effective priority per autonomy_aging_minutes waited so low-priority work can't starve (0 disables aging).""")]
    [JsonPropertyName("autonomy_concurrency")] public int AutonomyConcurrency { get; set; } = 1;
    // Anti-starvation aging: a ready objective gains +1 effective priority for every this-many
    // minutes it has waited since its last run (or creation). 0 disables aging (pure strict priority).
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_aging_minutes")] public int AutonomyAgingMinutes { get; set; } = 30;
    // ---- Phase 4: learning loop ----
    // Mission outcomes bias objective selection: each objective keeps a success-score EMA; at
    // selection time it contributes up to ±autonomy_priority_bias_max effective priority points
    // (read-time only — stored priorities never drift). Objectives that keep failing to produce
    // value (low EMA over enough runs) or loop on near-identical generated goals are auto-paused
    // with an objective_retired event for human review.
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety,
        Section = "learning", SectionNote = """Phase 4: mission outcomes bias objective selection. Each objective keeps a success-score EMA (weight of newest run = autonomy_score_ema_alpha); at selection time it adds up to ±autonomy_priority_bias_max effective priority points — read-time only, stored priorities never drift. Objectives with a low EMA after autonomy_retire_min_runs runs, or whose last autonomy_loop_window generated goals are near-identical (threshold = autonomy_dedupe_similarity), are auto-paused with an objective_retired event for human review. Set autonomy_learning_enabled false for pure Phase 3 behavior.""")]
    [JsonPropertyName("autonomy_learning_enabled")] public bool AutonomyLearningEnabled { get; set; } = true;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_priority_bias_max")] public int AutonomyPriorityBiasMax { get; set; } = 2;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_score_ema_alpha")] public double AutonomyScoreEmaAlpha { get; set; } = 0.3;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_retire_min_runs")] public int AutonomyRetireMinRuns { get; set; } = 5;
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_retire_score_threshold")] public double AutonomyRetireScoreThreshold { get; set; } = 0.25;
    // How many recent generated goals to compare for loop detection (0 = off). Uses
    // autonomy_dedupe_similarity as the overlap threshold, same metric as Strategist dedup.
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_loop_window")] public int AutonomyLoopWindow { get; set; } = 4;
    // v1.8.16 lifecycle hardening: let a successful one-shot or verification-only objective end
    // cleanly as Completed/Stopped instead of looping until loop detection retires it. Loop
    // detection stays available for true repeated loops. On by default; disable to restore the
    // pre-v1.8.16 "run until a rail stops it" behaviour.
    [ConfigKey(
        Exposure = ConfigExposure.Editable,
        Security = ConfigSecurity.Safety,
        UndocumentedBecause = "autonomy internals, console-managed")]
    [JsonPropertyName("autonomy_oneshot_completion")] public bool AutonomyOneShotCompletion { get; set; } = true;
    // ---- Phase 5: gated auto-apply ----
    // The Director may auto-approve + apply a coder patch WITHOUT human review, but only when the
    // patch clears a strict allowlist AND the workspace still builds + tests green afterward; a
    // red verify auto-rolls-back from the pre-apply backup. Fail-closed: OFF by default, and with
    // an EMPTY path allowlist nothing is ever eligible even when enabled. Requires the
    // patch_application_enabled + file_writing_enabled write gates to also be on.
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety,
        Section = "autoapply", SectionNote = """Phase 5: the Director may auto-approve+apply a coder patch WITHOUT human review, but ONLY when it clears the strict allowlist below AND the workspace still builds+tests green afterward (a red verify auto-rolls-back from the pre-apply backup). Highest-risk capability: OFF by default, forced off in every safety profile, and inert while autonomy_autoapply_paths is empty. Also requires patch_application_enabled + file_writing_enabled. autonomy_autoapply_paths = workspace-relative globs a patch must match (e.g. ["docs/**","src/**/*.cs"]); autonomy_autoapply_max_lines caps a single change; autonomy_autoapply_verify_cmd overrides the built-in 'dotnet build && dotnet test'; autonomy_autoapply_git_commit optionally commits verified changes locally (never pushed).""")]
    [JsonPropertyName("autonomy_autoapply_enabled")] public bool AutonomyAutoApplyEnabled { get; set; } = false;
    // Glob patterns (workspace-relative) a patch's file_path must match to be auto-appliable.
    // Empty = nothing is eligible. e.g. ["docs/**", "src/**/*.cs"].
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_autoapply_paths")] public List<string> AutonomyAutoApplyPaths { get; set; } = new();
    // Max changed lines (new_content line count) a single patch may have to auto-apply.
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_autoapply_max_lines")] public int AutonomyAutoApplyMaxLines { get; set; } = 40;
    // Verify command run in the workspace after apply; empty = built-in `dotnet build` + `dotnet test`.
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_autoapply_verify_cmd")] public string AutonomyAutoApplyVerifyCmd { get; set; } = "";
    // Hard timeout (seconds) for the verify step.
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_autoapply_verify_timeout")] public int AutonomyAutoApplyVerifyTimeout { get; set; } = 900;
    // After a green verify, also `git add` + `git commit` the change locally on the standalone branch. Off = leave on disk.
    [ConfigKey(Exposure = ConfigExposure.Editable, Security = ConfigSecurity.Safety)]
    [JsonPropertyName("autonomy_autoapply_git_commit")] public bool AutonomyAutoApplyGitCommit { get; set; } = false;
    // v1.8.26: git integration for auto-apply. Commits (and optionally pushes) verified changes to a
    // dedicated standalone branch — NEVER main. The SSH deploy key is referenced by PATH on the host,
    // never stored as key material. Branch name is derived as "<git_username>-anthill".
    [ConfigKey(
        Exposure = ConfigExposure.Editable,
        Security = ConfigSecurity.Safety,
        UndocumentedBecause = "console-managed (auto-apply panel)")]
    [JsonPropertyName("autonomy_autoapply_git_push")] public bool AutonomyAutoApplyGitPush { get; set; } = false;
    [ConfigKey(
        Exposure = ConfigExposure.Editable,
        Security = ConfigSecurity.Safety,
        UndocumentedBecause = "console-managed")]
    [JsonPropertyName("autonomy_autoapply_git_remote")] public string AutonomyAutoApplyGitRemote { get; set; } = "origin";
    [ConfigKey(
        Exposure = ConfigExposure.Editable,
        Security = ConfigSecurity.Safety,
        UndocumentedBecause = "console-managed")]
    [JsonPropertyName("autonomy_autoapply_git_username")] public string AutonomyAutoApplyGitUsername { get; set; } = "";
    [ConfigKey(
        Exposure = ConfigExposure.Editable,
        Security = ConfigSecurity.Environment,
        UndocumentedBecause = "console-managed")]
    [JsonPropertyName("autonomy_autoapply_git_ssh_key_path")] public string AutonomyAutoApplyGitSshKeyPath { get; set; } = "";
    // v1.8.21 fix: on deployments with no build toolchain (e.g. a published-binary LXC), the built-in
    // `dotnet build && dotnet test` verify always fails, so every auto-applied patch is rolled back and
    // nothing ever persists. When this is true AND no verify command is configured, auto-apply KEEPS the
    // applied patches without a verify gate (the operator's explicit, riskier choice). Ignored when a
    // verify command IS set — that always runs and gates keep/rollback. Default false = safe (verify).
    [ConfigKey(
        Exposure = ConfigExposure.Editable,
        Security = ConfigSecurity.Safety,
        UndocumentedBecause = "break-glass; documented in AUTONOMY.md, ")]
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
        // v0.3.8.91: the operator terminal is host RCE for administrators and now shares the fate of
        // every other write capability in a shipped profile — off unless the operator says otherwise
        // in their own config.json, which the raw overlay still honours.
        config.OperatorShellEnabled = false;
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
