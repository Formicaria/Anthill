# ANTHILL — Configuration reference

<!-- GENERATED FROM ConfigCatalog. Do not edit by hand: `ConfigCatalogTests`
     regenerates this file and fails on any difference. Change the property's
     `[ConfigKey]` attribute in `AnthillConfig.cs` instead. -->

Settings live in `.anthill/config.json`, resolved relative to the working
directory. `anthill --config` prints the active path.

**Editable** keys can be changed live through the settings surface under
`manage_settings`. **File-only** keys need a file edit and a restart.

| Key | Type | Default | Editable | Env override | Notes |
|---|---|---|---|---|---|
| `config_version` | string | `"config-v1"` | no | — |  |
| `safety_profile` | string | `"SAFE_LOCAL"` | no | — | **changes what the colony may do** |
| `workspace_root` | string | `".anthill"` | no | `ANTHILL_HOME` |  |
| `db_path` | string | `".anthill/anthill.db"` | no | — |  |
| `backup_dir` | string | `".anthill/backups"` | no | — |  |
| `logs_dir` | string | `".anthill/logs"` | no | — |  |
| `exports_dir` | string | `".anthill/exports"` | no | — |  |
| `agent_workspace_dir` | string | `".anthill/workspace"` | yes | — |  |
| `api_host` | string | `"0.0.0.0"` | no | `ANTHILL_HOST` |  |
| `api_port` | int | `8713` | no | `ANTHILL_PORT` | range 1–65535 |
| `api_auth_enabled` | bool | `true` | no | — | **changes what the colony may do** |
| `api_token_env` | string | `"ANTHILL_API_TOKEN"` | no | — |  |
| `api_job_workers` | int | `1` | no | — |  |
| `colony_name` | string | `"anthill"` | yes | — |  |
| `conversation_max_missions` | int | `25` | yes | — | **changes what the colony may do** |
| `conversation_max_turns` | int | `96` | yes | — | **changes what the colony may do** |
| `conversation_max_tool_calls` | int | `240` | yes | — | **changes what the colony may do** |
| `conversation_max_seconds` | int | `3600` | yes | — | **changes what the colony may do** |
| `use_ollama` | bool | `true` | yes | — |  |
| `ollama_model` | string | `""` | yes | `ANTHILL_OLLAMA_MODEL` |  |
| `ollama_host` | string | `"http://localhost:11434"` | yes | `ANTHILL_OLLAMA_HOST` |  |
| `model_routes` | object | `{}` | yes | — |  |
| `model_pricing` | object | `{}` | no | — |  |
| `model_pricing_currency` | string | `"USD"` | no | — |  |
| `web_search_enabled` | bool | `false` | yes | — |  |
| `patch_application_enabled` | bool | `false` | yes | — | **changes what the colony may do** |
| `file_writing_enabled` | bool | `false` | yes | — | **changes what the colony may do** |
| `shell_tool_enabled` | bool | `false` | yes | — | **changes what the colony may do** |
| `file_tools_enabled` | bool | `true` | yes | — | **changes what the colony may do** |
| `operator_shell_enabled` | bool | `false` | yes | — | **changes what the colony may do** |
| `setup_token_required` | bool | `false` | no | `ANTHILL_REQUIRE_SETUP_TOKEN` | **changes what the colony may do** |
| `operator_shell_dir` | string | `""` | yes | — |  |
| `infrastructure_enabled` | bool | `false` | yes | — | was: homelab_enabled |
| `infrastructure_scheduler_enabled` | bool | `false` | yes | — | was: homelab_scheduler_enabled |
| `infrastructure_mock_providers_enabled` | bool | `false` | yes | — | was: homelab_mock_providers_enabled |
| `infrastructure_max_concurrent_checks` | int | `2` | yes | — | was: homelab_max_concurrent_checks |
| `infrastructure_health_interval_seconds` | int | `60` | yes | — | was: homelab_health_interval_seconds |
| `infrastructure_health_timeout_ms` | int | `5000` | yes | — | was: homelab_health_timeout_ms |
| `infrastructure_notifications_enabled` | bool | `false` | yes | — | was: homelab_notifications_enabled |
| `infrastructure_automation_enabled` | bool | `false` | no | — | was: homelab_automation_enabled |
| `dashboard_workspace_enabled` | bool | `null` | no | — |  |
| `answer_synthesis_enabled` | bool | `true` | yes | — |  |
| `sandbox_execution_enabled` | bool | `false` | no | — | **changes what the colony may do** |
| `acting_coder_enabled` | bool | `false` | yes | — | **changes what the colony may do** |
| `roster_profile` | string | `"full"` | no | — | **changes what the colony may do** |
| `disabled_roles` | string[] | `[]` | no | — | **changes what the colony may do** |
| `specialist_ant_execution_enabled` | bool | `false` | no | — | **changes what the colony may do** |
| `tester_ant_enabled` | bool | `false` | no | — | **changes what the colony may do** |
| `soldier_ant_enabled` | bool | `false` | no | — | **changes what the colony may do** |
| `medic_ant_enabled` | bool | `false` | no | — | **changes what the colony may do** |
| `archivist_ant_enabled` | bool | `false` | no | — | **changes what the colony may do** |
| `external_destinations` | object | `{}` | no | — | **changes what the colony may do** |
| `ui_cartographer_ant_enabled` | bool | `false` | no | — | **changes what the colony may do** |
| `scribe_ant_enabled` | bool | `false` | no | — | **changes what the colony may do** |
| `infrastructure_slack_webhook` | string | _(secret)_ | yes | — | was: homelab_slack_webhook |
| `infrastructure_discord_webhook` | string | _(secret)_ | yes | — | was: homelab_discord_webhook |
| `infrastructure_generic_webhook` | string | _(secret)_ | yes | — | was: homelab_generic_webhook |
| `infrastructure_proxmox_enabled` | bool | `false` | yes | — | was: homelab_proxmox_enabled |
| `infrastructure_proxmox_host` | string | `""` | yes | — | was: homelab_proxmox_host |
| `infrastructure_proxmox_port` | int | `8006` | yes | — | was: homelab_proxmox_port |
| `infrastructure_proxmox_credential_id` | string | `"proxmox-main"` | yes | — | was: homelab_proxmox_credential_id |
| `infrastructure_proxmox_insecure_tls` | bool | `false` | yes | — | **changes what the colony may do**; was: homelab_proxmox_insecure_tls |
| `infrastructure_proxmox_protocol` | string | `"https"` | yes | — | was: homelab_proxmox_protocol |
| `infrastructure_proxmox_write_actions_enabled` | bool | `false` | no | — | **changes what the colony may do**; was: homelab_proxmox_write_actions_enabled |
| `infrastructure_proxmox_sync_interval_seconds` | int | `300` | yes | — | was: homelab_proxmox_sync_interval_seconds |
| `infrastructure_arr_sync_interval_seconds` | int | `300` | no | — | was: homelab_arr_sync_interval_seconds |
| `infrastructure_esxi_enabled` | bool | `false` | yes | — | was: homelab_esxi_enabled |
| `infrastructure_esxi_host` | string | `""` | yes | — | was: homelab_esxi_host |
| `infrastructure_esxi_port` | int | `443` | yes | — | was: homelab_esxi_port |
| `infrastructure_esxi_credential_id` | string | `"esxi-main"` | yes | — | was: homelab_esxi_credential_id |
| `infrastructure_esxi_insecure_tls` | bool | `false` | yes | — | **changes what the colony may do**; was: homelab_esxi_insecure_tls |
| `infrastructure_esxi_sync_interval_seconds` | int | `300` | yes | — | was: homelab_esxi_sync_interval_seconds |
| `infrastructure_docker_enabled` | bool | `false` | yes | — | was: homelab_docker_enabled |
| `infrastructure_docker_host` | string | `""` | yes | — | was: homelab_docker_host |
| `infrastructure_docker_port` | int | `2376` | yes | — | was: homelab_docker_port |
| `infrastructure_docker_credential_id` | string | `"docker-main"` | yes | — | was: homelab_docker_credential_id |
| `infrastructure_docker_insecure_tls` | bool | `false` | yes | — | **changes what the colony may do**; was: homelab_docker_insecure_tls |
| `infrastructure_docker_sync_interval_seconds` | int | `300` | yes | — | was: homelab_docker_sync_interval_seconds |
| `infrastructure_hyperv_enabled` | bool | `false` | yes | — | was: homelab_hyperv_enabled |
| `infrastructure_hyperv_host` | string | `""` | yes | — | was: homelab_hyperv_host |
| `infrastructure_hyperv_port` | int | `5986` | yes | — | was: homelab_hyperv_port |
| `infrastructure_hyperv_credential_id` | string | `"hyperv-main"` | yes | — | was: homelab_hyperv_credential_id |
| `infrastructure_hyperv_insecure_tls` | bool | `false` | yes | — | **changes what the colony may do**; was: homelab_hyperv_insecure_tls |
| `infrastructure_hyperv_sync_interval_seconds` | int | `300` | yes | — | was: homelab_hyperv_sync_interval_seconds |
| `infrastructure_risk_interval_seconds` | int | `3600` | yes | — | was: homelab_risk_interval_seconds |
| `infrastructure_incident_sweep_seconds` | int | `300` | yes | — | was: homelab_incident_sweep_seconds |
| `parallel_execution_enabled` | bool | `true` | yes | — |  |
| `max_parallel_workers` | int | `3` | yes | — |  |
| `max_web_searches_per_mission` | int | `3` | yes | — |  |
| `max_sources_per_mission` | int | `15` | yes | — |  |
| `max_context_packet_chars` | int | `7000` | yes | — |  |
| `max_agent_message_content_chars` | int | `2200` | yes | — |  |
| `spec_ingestion_enabled` | bool | `true` | yes | — |  |
| `long_input_threshold` | int | `6000` | yes | — |  |
| `max_section_chars` | int | `3500` | yes | — |  |
| `max_section_tasks` | int | `6` | yes | — |  |
| `max_db_backups` | int | `10` | yes | — |  |
| `event_retention_days` | int | `0` | yes | — |  |
| `autonomy_enabled` | bool | `false` | yes | — | **changes what the colony may do** |
| `autonomy_poll_seconds` | int | `30` | yes | — | **changes what the colony may do** |
| `autonomy_max_missions_per_hour` | int | `6` | yes | — | **changes what the colony may do** |
| `autonomy_max_missions_per_day` | int | `60` | yes | — | **changes what the colony may do** |
| `autonomy_max_consecutive_failures` | int | `3` | yes | — | **changes what the colony may do** |
| `autonomy_dedupe_similarity` | number | `0.8` | yes | — | **changes what the colony may do** |
| `autonomy_max_followups_per_run` | int | `1` | yes | — | **changes what the colony may do** |
| `autonomy_max_objective_depth` | int | `3` | yes | — | **changes what the colony may do** |
| `autonomy_max_backlog` | int | `40` | yes | — | **changes what the colony may do** |
| `autonomy_concurrency` | int | `1` | yes | — | **changes what the colony may do** |
| `autonomy_aging_minutes` | int | `30` | yes | — | **changes what the colony may do** |
| `autonomy_learning_enabled` | bool | `true` | yes | — | **changes what the colony may do** |
| `autonomy_priority_bias_max` | int | `2` | yes | — | **changes what the colony may do** |
| `autonomy_score_ema_alpha` | number | `0.3` | yes | — | **changes what the colony may do** |
| `autonomy_retire_min_runs` | int | `5` | yes | — | **changes what the colony may do** |
| `autonomy_retire_score_threshold` | number | `0.25` | yes | — | **changes what the colony may do** |
| `autonomy_loop_window` | int | `4` | yes | — | **changes what the colony may do** |
| `autonomy_escalation_policy` | string | `"ask"` | yes | — | Escalation policy for missions with no conversation (scheduled, CLI, Director): ask | auto_approve | bypass.; **changes what the colony may do** |
| `auto_update` | string | `"silent"` | yes | — | What to do when a newer release exists: silent | notify | off.; **changes what the colony may do** |
| `autonomy_autoapply_enabled` | bool | `false` | yes | — | **changes what the colony may do** |
| `autonomy_autoapply_paths` | string[] | `[]` | yes | — | **changes what the colony may do** |
| `autonomy_autoapply_max_lines` | int | `40` | yes | — | **changes what the colony may do** |
| `autonomy_autoapply_verify_cmd` | string | `""` | yes | — | **changes what the colony may do** |
| `autonomy_autoapply_verify_timeout` | int | `900` | yes | — | **changes what the colony may do** |
| `autonomy_autoapply_git_commit` | bool | `false` | yes | — | **changes what the colony may do** |
| `mission_replay_enabled` | bool | `false` | no | `ANTHILL_MISSION_REPLAY_ENABLED` | **changes what the colony may do** |
| `mission_replay_vault_path` | string | `""` | no | `ANTHILL_MISSION_REPLAY_VAULT_PATH` |  |
| `mission_replay_tag` | string | `"anthill/replay"` | no | `ANTHILL_MISSION_REPLAY_TAG` |  |
| `mission_replay_learning_enabled` | bool | `false` | no | `ANTHILL_MISSION_REPLAY_LEARNING_ENABLED` | **changes what the colony may do** |
| `knowledge_enabled` | bool | `false` | yes | `ANTHILL_KNOWLEDGE_ENABLED` | **changes what the colony may do** |
| `knowledge_forager_endpoint` | string | `"http://127.0.0.1:8790"` | no | `ANTHILL_KNOWLEDGE_FORAGER_ENDPOINT` |  |
| `knowledge_forager_token` | string | _(secret)_ | no | `ANTHILL_KNOWLEDGE_FORAGER_TOKEN` |  |
| `knowledge_forager_allow_remote` | bool | `false` | no | `ANTHILL_KNOWLEDGE_ALLOW_REMOTE` | **changes what the colony may do** |
| `knowledge_probe_timeout_ms` | int | `2000` | no | `ANTHILL_KNOWLEDGE_PROBE_TIMEOUT_MS` | range 250–60000 |
| `knowledge_retrieval_timeout_ms` | int | `5000` | no | `ANTHILL_KNOWLEDGE_RETRIEVAL_TIMEOUT_MS` | range 500–120000 |
| `knowledge_ingestion_timeout_ms` | int | `10000` | no | `ANTHILL_KNOWLEDGE_INGESTION_TIMEOUT_MS` | range 500–300000 |
| `knowledge_default_top_k` | int | `8` | no | `ANTHILL_KNOWLEDGE_TOP_K` | range 1–50 |
| `knowledge_max_context_chars` | int | `12000` | no | `ANTHILL_KNOWLEDGE_MAX_CONTEXT_CHARS` | range 1000–200000 |
| `knowledge_cache_seconds` | int | `30` | no | `ANTHILL_KNOWLEDGE_CACHE_SECONDS` | range 0–3600 |
| `knowledge_project_map` | object | `{}` | no | — | **changes what the colony may do** |
| `knowledge_default_project` | string | `""` | no | `ANTHILL_KNOWLEDGE_DEFAULT_PROJECT` |  |

## Deliberately absent from `config.example.json`

These are real settings. They are kept out of the example file for the
reason given, so that an operator finds out they exist here rather than
by reading the source.

| Key | Why |
|---|---|
| `model_priority_provider` | console-managed (Routing inspector) |
| `model_priority_model` | console-managed (Routing inspector) |
| `user_tools_enabled` | console-managed (operator-defined tools) |
| `user_tool_allowed_hosts` | console-managed |
| `workspace_checks` | file-only by design; see the v0.3.8.73 note on its declaration |
| `deployment_mode` | detected; the console shows it read-only |
| `docker_execute_enabled` | module surface, not a general operator setting |
| `micromound_enabled` | optional compile-time integration |
| `config_schema_version` | written by the migration, not by an operator |
| `handoff_ingestion_enabled` | internal wiring, no operator-facing behaviour on its own |
| `adaptive_mission_control_enabled` | internal wiring |
| `activation_tier` | console-managed |
| `objective_verification_enabled` | internal wiring |
| `shadow_observation_enabled` | console-managed (Readiness page) |
| `readiness_min_shadow_sample` | readiness thresholds, console-managed |
| `readiness_min_diagnosis_precision` | readiness thresholds, console-managed |
| `readiness_min_action_accuracy` | readiness thresholds, console-managed |
| `autonomy_oneshot_completion` | autonomy internals, console-managed |
| `autonomy_autoapply_git_push` | console-managed (auto-apply panel) |
| `autonomy_autoapply_git_remote` | console-managed |
| `autonomy_autoapply_git_username` | console-managed |
| `autonomy_autoapply_git_ssh_key_path` | console-managed |
| `autonomy_autoapply_keep_without_verify` | break-glass; documented in AUTONOMY.md,  |
