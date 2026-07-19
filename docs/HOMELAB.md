# ANTHILL Homelab — Canonical Design Doc

Status: Active subsystem design doc (NORTH_STAR D10). The master build order lives in
`docs/NORTH_STAR.md`; this file tracks homelab phase status and the design decisions that hold
across phases.

## Phase status

| Phase | Version | Status | Scope |
|---|---|---|---|
| Foundation | V1.9.0 | **SHIPPED** | Models, tables, interfaces, target allowlist, credential store, permission tier, scheduler skeleton, read-only ants, docs, tests |
| Scheduler + mock harness | V1.9.1 | **SHIPPED** | Five network-free mock providers, shared harness fixture, backoff/concurrency/persistence proofs, `/homelab/providers`, `homelab_mock_providers_enabled` gate |
| Inventory + service registry | V1.10.0 | **SHIPPED** | Manual registration, JSON import/export (idempotent upsert), dependency mapping, Homelab console page (hosts/services/ports/dependencies/changes) |
| Health checks + notifications | V1.11.0 | **SHIPPED** | Ping/HTTP/TCP/service-URL checks (allowlist-gated, strict timeouts), incident candidates at 3 consecutive failures, Slack/Discord/generic webhooks (off by default), Health panel on the Homelab page |
| Proxmox read-only | V1.12.0 | **SHIPPED** | GET-only client (no write path exists), nodes/VMs/LXCs/storage/failed-task sync on the shared scheduler, credential + allowlist discipline, Virtualization UI panels |
| Network + security awareness | V1.13.0 | **SHIPPED** | Nine deterministic risk findings with reconciliation (auto-resolve + sticky acks), network-device registry, exposure classification, Network & Risk UI — zero network I/O, no scanning |
| Incident + change memory | V1.14.0 | **SHIPPED** | Auto-opened deduped incidents, suspect-flagged timelines, similar-incident fix memory, repeat patterns, IApprovable design + unified approvals view (docs/APPROVALS.md) — V1.x line complete |
| Command Center launch | V2.0.0 | **SHIPPED** | One dashboard aggregation, impact-propagating dependency graph, entity drawers, next-checks, ANTHILL identity layer (semantic tokens, colony mesh, purposeful motion, reduced-motion aware) |
| Multi-hypervisor inventory | V2.1.0* | **SHIPPED** | Unplanned insertion: ESXi/Docker/Hyper-V read-only inventory (consumed the V2.1.0 number; the planned action phase shifted to V2.3.0) |
| Approval-gated actions | V2.3.0 | **SHIPPED** | ActionProposal pipeline (propose → blast-radius score → approve → execute → verify → audit), allowlisted action catalog with structural forbidden set, Local + Mock runners (infrastructure runners arrive v2.3.1), HOMELAB_STOP kill switch + `/homelab/actions/stop`/`resume`, unified-queue projection, Actions console panel. Both action capability gates still ship OFF — fail closed |
| Backup + restore intelligence | V2.4.0 | Future | Coverage map, stale/failed detection, restore priority/confidence, blast-radius simulation |

## What v1.9.0 actually is

A read-only backend foundation. It cannot control anything:

- **Models + tables.** 16 record types and 15 SQLite tables (`homelab_nodes`, `network_devices`,
  `services`, `vm_inventory`, `container_inventory`, `storage_inventory`, `backup_inventory`,
  `health_checks`, `homelab_events`, `change_log`, `incidents`, `dependencies`, `risk_records`,
  `homelab_credentials`, `homelab_target_allowlist`, plus `homelab_meta` for scheduler state) in
  the existing colony DB (`HomelabRepository`, idempotent schema init).
- **Interfaces.** `IInventoryProvider`, `IHealthCheckProvider`, `IHomelabEventSink`,
  `IHomelabRepository`, `IIntegrationStatusProvider`, `IHomelabTargetGuard`, `ICredentialProvider`
  — every future integration implements these so one mock harness (v1.9.1) tests them all.
- **Target allowlist (D1).** `HomelabTargetGuard` lets deterministic homelab providers reach
  operator-allowlisted private hosts (exact hostname, exact IP, or IPv4 CIDR; no DNS resolution).
  It is a separate mechanism from the general SSRF guard (`UrlSafety`), which still blocks
  private/loopback targets for all LLM-directed tools. Tests prove the isolation both ways.
- **Credential store (D2).** `HomelabCredentialStore` on the existing `FieldCipher`: secrets are
  write-only through the API, statuses are secret-free (`configured` / `last_verified` only), and
  every secret use writes an audit `homelab_events` row. Secrets never reach LLM prompts.
- **Permission tier (D3).** New permissions `read_homelab`, `manage_homelab_integrations`,
  `approve_homelab_actions`, `execute_homelab_actions`; new role `homelab_operator` (view +
  approve — never manage, execute, or admin). The two action permissions ship capability-gated
  OFF until V2.1.
- **Scheduler skeleton (D4).** `HomelabScheduler`: single background runner with per-job jitter,
  exponential backoff on consecutive failures, a global concurrency cap, and last-run/last-result
  persisted through the repository. Disabled by default; v1.9.0 registers no jobs.
- **Read-only ants.** `InventoryAnt`, `NetworkScoutAnt`, `HealthAnt`, `ProxmoxAnt`, `StorageAnt`,
  `BackupAnt`, `SecurityScoutAnt`, `ChangeArchivistAnt` — visible in the colony registry,
  `Executable: false`, so the planner can never assign them tasks in v1.9.0.
- **API.** `/homelab/summary`, `/homelab/hosts` (GET/POST), `/homelab/services` (GET/POST),
  `/homelab/events`, `/homelab/changes`, `/homelab/allowlist` (GET/POST/DELETE),
  `/homelab/credentials` (GET status / POST save / DELETE). Reads need `read_homelab`; writes need
  `manage_homelab_integrations`; no endpoint returns a secret.

## Design rules that hold for every homelab phase

1. Read-only lands before action-gated; actions only ever arrive behind IApprovable + approval.
2. Deterministic polling is plain C# service code; LLM ants only summarize/explain/recommend.
3. One scheduler (`HomelabScheduler`), one credential store, one event stream
   (`homelab_events` + `change_log`), one approval system (IApprovable, from V1.14/V2.1).
4. The Homelab Target Allowlist never widens the general SSRF guard.
5. `.anthill/HOMELAB_STOP` halts homelab actions; `.anthill/STOP` halts autonomy — separate scopes.
6. No secrets in logs, API responses, UI, events, or test output.
7. Every new stateful feature ships with: model, persistence, API (if UI-facing), tests,
   version note, changelog entry.
