# ANTHILL Infrastructure — Canonical Design Doc

Status: Active subsystem design doc (NORTH_STAR D10). The master build order lives in
`docs/PLAN.md`; this file tracks infrastructure phase status and the design decisions that hold
across phases.

## Phase status

| Phase | Version | Status | Scope |
|---|---|---|---|
| Foundation | V1.9.0 | **SHIPPED** | Models, tables, interfaces, target allowlist, credential store, permission tier, scheduler skeleton, read-only ants, docs, tests |
| Scheduler + mock harness | V1.9.1 | **SHIPPED** | Five network-free mock providers, shared harness fixture, backoff/concurrency/persistence proofs, `/infrastructure/providers`, `infrastructure_mock_providers_enabled` gate |
| Inventory + service registry | V1.10.0 | **SHIPPED** | Manual registration, JSON import/export (idempotent upsert), dependency mapping, Infrastructure console page (hosts/services/ports/dependencies/changes) |
| Health checks + notifications | V1.11.0 | **SHIPPED** | Ping/HTTP/TCP/service-URL checks (allowlist-gated, strict timeouts), incident candidates at 3 consecutive failures, Slack/Discord/generic webhooks (off by default), Health panel on the Infrastructure page |
| Proxmox read-only | V1.12.0 | **SHIPPED** | GET-only client (no write path exists), nodes/VMs/LXCs/storage/failed-task sync on the shared scheduler, credential + allowlist discipline, Virtualization UI panels |
| Network + security awareness | V1.13.0 | **SHIPPED** | Nine deterministic risk findings with reconciliation (auto-resolve + sticky acks), network-device registry, exposure classification, Network & Risk UI — zero network I/O, no scanning |
| Incident + change memory | V1.14.0 | **SHIPPED** | Auto-opened deduped incidents, suspect-flagged timelines, similar-incident fix memory, repeat patterns, IApprovable design + unified approvals view (docs/APPROVALS.md) — V1.x line complete |
| Command Center launch | V2.0.0 | **SHIPPED** | One dashboard aggregation, impact-propagating dependency graph, entity drawers, next-checks, ANTHILL identity layer (semantic tokens, colony mesh, purposeful motion, reduced-motion aware) |
| Multi-hypervisor inventory | V2.1.0* | **SHIPPED** | Unplanned insertion: ESXi/Docker/Hyper-V read-only inventory (consumed the V2.1.0 number; the planned action phase shifted to V2.3.0) |
| Approval-gated actions | V2.3.0–V2.3.1 | **SHIPPED** | ActionProposal pipeline (propose → blast-radius score → approve → execute → verify → audit), allowlisted action catalog with structural forbidden set, Local + Mock runners, INFRASTRUCTURE_STOP kill switch + `/infrastructure/actions/stop`/`resume`, unified-queue projection, Actions console panel; v2.3.1 added the ProxmoxActionRunner (double-gated `infrastructure_proxmox_write_actions_enabled`, POST shapes structurally allowlisted, clean-shutdown-only, verified execution); v2.3.2 adds D1 target-guard enforcement + node-segment validation and the Service Deck console redesign. Action capability gates still ship OFF — fail closed |
| Backup + restore intelligence | V2.4.0 | Future | Coverage map, stale/failed detection, restore priority/confidence, blast-radius simulation |

## What v1.9.0 actually is

A read-only backend foundation. It cannot control anything:

- **Models + tables.** 16 record types and 15 SQLite tables (`infrastructure_nodes`, `network_devices`,
  `services`, `vm_inventory`, `container_inventory`, `storage_inventory`, `backup_inventory`,
  `health_checks`, `infrastructure_events`, `change_log`, `incidents`, `dependencies`, `risk_records`,
  `infrastructure_credentials`, `infrastructure_target_allowlist`, plus `infrastructure_meta` for scheduler state) in
  the existing colony DB (`InfrastructureRepository`, idempotent schema init).
- **Interfaces.** `IInventoryProvider`, `IHealthCheckProvider`, `IInfrastructureEventSink`,
  `IInfrastructureRepository`, `IIntegrationStatusProvider`, `IInfrastructureTargetGuard`, `ICredentialProvider`
  — every future integration implements these so one mock harness (v1.9.1) tests them all.
- **Target allowlist (D1).** `InfrastructureTargetGuard` lets deterministic infrastructure providers reach
  operator-allowlisted private hosts (exact hostname, exact IP, or IPv4 CIDR; no DNS resolution).
  It is a separate mechanism from the general SSRF guard (`UrlSafety`), which still blocks
  private/loopback targets for all LLM-directed tools. Tests prove the isolation both ways.
- **Credential store (D2).** `InfrastructureCredentialStore` on the existing `FieldCipher`: secrets are
  write-only through the API, statuses are secret-free (`configured` / `last_verified` only), and
  every secret use writes an audit `infrastructure_events` row. Secrets never reach LLM prompts.
- **Permission tier (D3).** New permissions `read_infrastructure`, `manage_infrastructure_integrations`,
  `approve_infrastructure_actions`, `execute_infrastructure_actions`; new role `infrastructure_operator` (view +
  approve — never manage, execute, or admin). The two action permissions ship capability-gated
  OFF until V2.1.
- **Scheduler skeleton (D4).** `InfrastructureScheduler`: single background runner with per-job jitter,
  exponential backoff on consecutive failures, a global concurrency cap, and last-run/last-result
  persisted through the repository. Disabled by default; v1.9.0 registers no jobs.
- **Read-only ants.** `InventoryAnt`, `NetworkScoutAnt`, `HealthAnt`, `ProxmoxAnt`, `StorageAnt`,
  `BackupAnt`, `SecurityScoutAnt`, `ChangeArchivistAnt` — visible in the colony registry,
  `Executable: false`, so the planner can never assign them tasks in v1.9.0.
- **API.** `/infrastructure/summary`, `/infrastructure/hosts` (GET/POST), `/infrastructure/services` (GET/POST),
  `/infrastructure/events`, `/infrastructure/changes`, `/infrastructure/allowlist` (GET/POST/DELETE),
  `/infrastructure/credentials` (GET status / POST save / DELETE). Reads need `read_infrastructure`; writes need
  `manage_infrastructure_integrations`; no endpoint returns a secret.

## Design rules that hold for every infrastructure phase

1. Read-only lands before action-gated; actions only ever arrive behind IApprovable + approval.
2. Deterministic polling is plain C# service code; LLM ants only summarize/explain/recommend.
3. One scheduler (`InfrastructureScheduler`), one credential store, one event stream
   (`infrastructure_events` + `change_log`), one approval system (IApprovable, from V1.14/V2.1).
4. The Infrastructure Target Allowlist never widens the general SSRF guard.
5. `.anthill/INFRASTRUCTURE_STOP` halts infrastructure actions; `.anthill/STOP` halts autonomy — separate scopes.
6. No secrets in logs, API responses, UI, events, or test output.
7. Every new stateful feature ships with: model, persistence, API (if UI-facing), tests,
   version note, changelog entry.
