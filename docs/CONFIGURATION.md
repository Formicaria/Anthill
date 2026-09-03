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
| `homelab_enabled` | bool | `false` | yes | — |  |
| `homelab_scheduler_enabled` | bool | `false` | yes | — |  |
| `homelab_mock_providers_enabled` | bool | `false` | yes | — |  |
| `homelab_max_concurrent_checks` | int | `2` | yes | — |  |
| `homelab_health_interval_seconds` | int | `60` | yes | — |  |
| `homelab_health_timeout_ms` | int | `5000` | yes | — |  |
| `homelab_notifications_enabled` | bool | `false` | yes | — |  |
| `homelab_automation_enabled` | bool | `false` | no | — |  |
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
| `homelab_slack_webhook` | string | _(secret)_ | yes | — |  |
| `homelab_discord_webhook` | string | _(secret)_ | yes | — |  |
| `homelab_generic_webhook` | string | _(secret)_ | yes | — |  |
| `homelab_proxmox_enabled` | bool | `false` | yes | — |  |
| `homelab_proxmox_host` | string | `""` | yes | — |  |
| `homelab_proxmox_port` | int | `8006` | yes | — |  |
| `homelab_proxmox_credential_id` | string | `"proxmox-main"` | yes | — |  |
| `homelab_proxmox_insecure_tls` | bool | `false` | yes | — | **changes what the colony may do** |
| `homelab_proxmox_protocol` | string | `"https"` | yes | — |  |
| `homelab_proxmox_write_actions_enabled` | bool | `false` | no | — | **changes what the colony may do** |
| `homelab_proxmox_sync_interval_seconds` | int | `300` | yes | — |  |
| `homelab_arr_sync_interval_seconds` | int | `300` | no | — |  |
| `homelab_esxi_enabled` | bool | `false` | yes | — |  |
| `homelab_esxi_host` | string | `""` | yes | — |  |
| `homelab_esxi_port` | int | `443` | yes | — |  |
| `homelab_esxi_credential_id` | string | `"esxi-main"` | yes | — |  |
| `homelab_esxi_insecure_tls` | bool | `false` | yes | — | **changes what the colony may do** |
| `homelab_esxi_sync_interval_seconds` | int | `300` | yes | — |  |
| `homelab_docker_enabled` | bool | `false` | yes | — |  |
| `homelab_docker_host` | string | `""` | yes | — |  |
| `homelab_docker_port` | int | `2376` | yes | — |  |
| `homelab_docker_credential_id` | string | `"docker-main"` | yes | — |  |
| `homelab_docker_insecure_tls` | bool | `false` | yes | — | **changes what the colony may do** |
| `homelab_docker_sync_interval_seconds` | int | `300` | yes | — |  |
| `homelab_hyperv_enabled` | bool | `false` | yes | — |  |
| `homelab_hyperv_host` | string | `""` | yes | — |  |
| `homelab_hyperv_port` | int | `5986` | yes | — |  |
| `homelab_hyperv_credential_id` | string | `"hyperv-main"` | yes | — |  |
| `homelab_hyperv_insecure_tls` | bool | `false` | yes | — | **changes what the colony may do** |
| `homelab_hyperv_sync_interval_seconds` | int | `300` | yes | — |  |
| `homelab_risk_interval_seconds` | int | `3600` | yes | — |  |
| `homelab_incident_sweep_seconds` | int | `300` | yes | — |  |
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
| `autonomy_autoapply_enabled` | bool | `false` | yes | — | **changes what the colony may do** |
| `autonomy_autoapply_paths` | string[] | `[]` | yes | — | **changes what the colony may do** |
| `autonomy_autoapply_max_lines` | int | `40` | yes | — | **changes what the colony may do** |
| `autonomy_autoapply_verify_cmd` | string | `""` | yes | — | **changes what the colony may do** |
| `autonomy_autoapply_verify_timeout` | int | `900` | yes | — | **changes what the colony may do** |
| `autonomy_autoapply_git_commit` | bool | `false` | yes | — | **changes what the colony may do** |

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
