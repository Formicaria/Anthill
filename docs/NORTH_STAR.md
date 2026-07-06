# OPERATION ANTHILL — MASTER NORTH STAR ROADMAP

**Document type:** Codex / Claude implementation handoff
**Purpose:** Replace scattered roadmap direction with one ordered roadmap that keeps ANTHILL aligned to the original goal.
**Status:** This is the canonical, top-level build order. Subsystem docs (`docs/ROADMAP.md`, `docs/UI_ROADMAP.md`, `docs/AUTONOMY.md`, `docs/HOMELAB.md`) are retained as history and must not contradict this file.
**Current repo baseline used for this consolidation:** v1.8.26

---

## 0. North Star

ANTHILL is a local-first swarm operating system for your own hardware.

The end goal is not just a chatbot, not just a dashboard, and not just a Proxmox wrapper. The end goal is a local colony command center that can:

1. understand goals,
2. break them into tasks,
3. route work through specialized ants,
4. store mission/task/event/source/patch/approval/objective/pheromone history,
5. learn from previous outcomes,
6. show everything in a browser command center,
7. safely manage a homelab/network,
8. propose fixes,
9. require approval for risky actions,
10. eventually automate low-risk operations under strict guardrails.

The permanent rule is:

```text
OBSERVE -> DIAGNOSE -> PROPOSE -> RISK SCORE -> APPROVAL -> EXECUTE -> VERIFY -> LOG -> LEARN
```

Do not skip directly from diagnose to execute.

---

## 1. Current Baseline To Preserve

The existing repo is already past the original prototype stage. Treat these as preserved foundation, not future work.

### Current active architecture

- .NET 9-first application.
- Optional C++20 native kernel.
- Local Ollama default runtime.
- Web UI at `/ui`.
- SQLite memory.
- Mission/task/event/source/patch/approval/objective/pheromone records.
- Role-based ant routing.
- Worker runtime and ant capability profiles.
- Objective/autonomy system.
- ResourceGovernor and concurrent mission handling.
- Patch approval/apply safety model.
- Auto-apply is gated, fail-closed, and configurable.
- `py.old/` is archive material only and must not be used as active code.

### Existing completed roadmap work to keep intact

These features are considered baseline:

- Objective lifecycle hardening.
- Visual Patch Review Center.
- Mission Composer + Plan Preview.
- Colony Live Canvas 2.0.
- Objective Command Board.
- Mission Timeline + Task DAG Viewer.
- Ant Inspector + Performance Observatory.
- Memory + Pheromone Explorer.
- Visual Patch Center 2.0.
- Full Command Center Polish.
- CI artifact packaging.
- Auto-apply git integration.

### Existing docs to consolidate

The following docs should be treated as source material, but this master roadmap is the top-level direction document:

- `docs/ROADMAP.md`
- `docs/UI_ROADMAP.md`
- `docs/AUTONOMY.md`
- future `docs/HOMELAB.md`

---

## 2. Roadmap Drift To Fix First

There is roadmap drift in the docs:

1. `docs/UI_ROADMAP.md` says all UI phases are shipped.
2. `README.md` shows the repo at v1.8.26 with v1.8.25 UI polish and v1.8.26 auto-apply git integration shipped.
3. Older roadmap language still says some phases are future/not built.
4. Autonomy docs are largely complete, but some target-version language is stale.

### Required fix

Create one master roadmap file (`docs/NORTH_STAR.md`) and update older docs so they point to it. Each older doc should carry:

```text
Status: This document is retained as subsystem history. For current ordered work, see docs/NORTH_STAR.md.
```

Do not delete the older docs. They are useful subsystem history. Just stop treating them as the master build order.

---

## 3. Non-Negotiable Development Rules

### 3.1 Safety rules

1. No destructive infrastructure action without explicit approval.
2. No VM/LXC/container delete action in early V2.
3. No firewall changes without preview and approval.
4. No secrets in logs, UI, memory, test output, event payloads, or error messages.
5. No SSH/shell execution unless allowlisted and approval-gated.
6. No automatic repeated restart loops.
7. No hidden background actions.
8. No bypassing Queen, ApprovalGuard, or action policy.
9. No applying patches directly from non-approved ants.
10. No pretending a service was fixed without verification.
11. No widening the general SSRF guard for homelab traffic.
12. No action executes while `.anthill/HOMELAB_STOP` exists.
13. No Python files written, modified, or treated as active code.

### 3.2 Architecture rules

1. Keep ANTHILL local-first.
2. Preserve the mission/task/source/patch/event/approval/objective/pheromone model.
3. Add new systems through clean C# services and interfaces.
4. Do not bolt homelab features randomly into existing mission code.
5. Deterministic polling must be plain C# service code, not LLM calls.
6. Use LLM-backed ants only for judgment, summarization, planning, explanation, or report generation.
7. Use additive APIs/DTOs. Avoid destructive endpoint changes.
8. Every new stateful feature needs: model, persistence, API if UI-facing, tests, version note, changelog entry.
9. Every risky feature needs: capability gate, permission check, audit event, tests proving disabled/default-safe behavior.
10. Every new integration must be searchable, visible in UI, linkable to missions/incidents, and usable by memory/pheromones.

---

## 4. Global Bug-Prevention Gates

Every version from here forward must include regression protection for the classes of bugs that have already appeared.

### Required recurring validations

```bash
dotnet test Anthill.sln -c Release
dotnet build Anthill.sln -c Release --no-restore
dotnet publish src/Anthill.Cli/Anthill.Cli.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
```

*(Centralized in `scripts/validate.sh` / `scripts/validate.ps1` — shipped v1.8.28.)*

### Required CI / test guards

1. **Duplicate route guard** — fail boot/tests if two endpoints register the same route/method (prevents ambiguous-route HTTP 500s). *(Shipped: `AssertNoDuplicateRoutes` at boot, v1.8.23.2.)*
2. **UI JS syntax check** — `node --check` on embedded UI JavaScript. *(Shipped: `ui-integrity` CI job, v1.8.25.2.)*
3. **UI glyph/encoding integrity check** — fail on `�`, fail if known icon glyphs collapse to `?`, preserve the legitimate help-shortcut `?`. *(Shipped: `ui-integrity` CI job, v1.8.25.2.)*
4. **Planner routing tests** — UI/frontend/canvas/CSS goals route to `coder.ui_coder`; verification-only/read-only goals never route to coder patch tasks; file/code goals reach file/coder when appropriate. *(Shipped: `AntRegistryTests` + `LifecycleAndConstraintTests`, confirmed covered in the v1.8.28 audit.)*
5. **Serialization error guard** — API errors return meaningful JSON, not empty HTTP 500s. *(Shipped: `ApiJson.Envelope` + exception middleware, v1.8.23.1.)*
6. **Migration idempotence tests** — fresh DB, existing DB, and re-run all pass. *(Shipped: `RegressionGuardTests`, v1.8.28.)*
7. **Permission boundary tests** — Mission Coordinator denied where appropriate; Homelab Operator allowed only where appropriate; Administrator allowed where appropriate; no role gains dangerous permissions accidentally.
8. **Secret redaction tests** — credentials never appear in logs, API responses, UI snapshots, events, or test output.
9. **Approval gate tests** — patch/action cannot execute without approval; rejected cannot execute; superseded/duplicate handled deterministically.
10. **Kill-switch tests** — `.anthill/STOP` halts autonomy; `.anthill/HOMELAB_STOP` halts homelab actions; each is scoped correctly.
11. **No-Python guard** — no changes under `py.old/`; no new active Python files unless explicitly approved as non-runtime tooling. *(Shipped: CI `repo-guards` job + `RegressionGuardTests`, v1.8.28.)*
12. **Release artifact guard** — artifact uploads only after publish + selftest pass; the release workflow must not publish failed builds. *(Shipped: v1.8.23.3.)*

---

## 5. Consolidated Build Order

```text
V1.8.27  Roadmap/documentation consolidation                 [SHIPPED v1.8.27]
V1.8.28  Validation and regression harness hardening         [SHIPPED v1.8.28]
V1.8.29  Fresh-install training and pheromone bootstrap missions  [SHIPPED v1.8.29]
V1.9.0   Homelab foundation: models, folders, tables, target guard, credentials, permissions  [SHIPPED v1.9.0]
V1.9.1   Homelab scheduler skeleton and mock-provider harness  [SHIPPED v1.9.1]
V1.10.0  Inventory and service registry                       [SHIPPED v1.10.0]
V1.11.0  Health checks and notifications                      [SHIPPED v1.11.0]
V1.12.0  Proxmox read-only integration                        [SHIPPED v1.12.0]
V1.13.0  Network and security awareness                       [SHIPPED v1.13.0]
V1.14.0  Incident and change memory + IApprovable design      [SHIPPED v1.14.0 — V1.x line complete]
V2.0.0   Homelab Command Center launch
V2.1.0   Approval-gated homelab actions
V2.2.0   Backup and restore intelligence
V2.3.0   Automation rules
V2.4.0   DNS/DHCP/firewall control layer
V2.5.0   Full homelab operations layer
V3.0.0   Bounded autonomous homelab operator
```

---

# PHASE 1 — V1.8.27 ROADMAP / DOC CONSOLIDATION

**Status: SHIPPED in v1.8.27.**

## Goal
Create one canonical roadmap/north-star document and stop roadmap drift.

## Strict scope
Documentation and tests only. No product behavior changes.

## Required changes
1. Add `docs/NORTH_STAR.md`.
2. Update `docs/ROADMAP.md`, `docs/UI_ROADMAP.md`, `docs/AUTONOMY.md`, `README.md` to reference it.
3. Add a short "roadmap status" block near the top of each older doc:
   ```text
   Status: This document is retained as subsystem history. For current ordered work, see docs/NORTH_STAR.md.
   ```
4. Add a roadmap consistency test if possible: README version matches runtime version; no doc claims a shipped phase is still future; `docs/NORTH_STAR.md` exists.

## Success criteria
- One document clearly defines the future build order.
- Older roadmaps no longer conflict with the current version.
- Codex/Claude has one source to follow.
- No runtime behavior changes.

---

# PHASE 2 — V1.8.28 VALIDATION / REGRESSION HARNESS HARDENING

**Status: SHIPPED in v1.8.28** — `scripts/validate.sh` / `scripts/validate.ps1` centralize the
validation commands; `RegressionGuardTests` add version-marker consistency, migration idempotence,
UI glyph integrity, and no-Python guards to `dotnet test`; CI gains the `repo-guards` job
(py.old immutability + stray-Python scan) and an extended docs/version consistency check.

## Goal
Prevent repeated breakages before adding homelab complexity.

## Strict scope
Validation, CI, and test hardening only.

## Required changes
1. Centralize validation commands.
2. Add/strengthen tests for: route uniqueness, UI syntax, UI glyph integrity, planner routing, API serialization, migration idempotence, no `py.old` changes, version marker consistency.
3. Make CI fail loudly for these classes of errors.

## Success criteria
- Known bug classes cannot merge again.
- CI reports actionable failures.
- No product behavior changes.

---

# PHASE 3 — V1.8.29 FRESH-INSTALL TRAINING + PHEROMONE BOOTSTRAP

**Status: SHIPPED in v1.8.29** — `docs/TRAINING_MISSIONS.md` documents the nine-mission read-only
training pack with copy-paste goals wired to the `MissionConstraints` phrases, operator run
instructions, and the recurring memory-compression pattern.

## Goal
Give fresh ANTHILL installs a repeatable way to learn the repo, roles, workflow, UI, memory, and V2 roadmap before doing real patch missions.

## Strict scope
Read-only training missions and memory quality. No file changes by training missions.

## Required features
1. A documented training mission pack (`docs/TRAINING_MISSIONS.md`).
2. Operator instructions for running the missions after a fresh install.
3. Each training mission produces useful memory/pheromone trails.
4. A memory-compression mission pattern.

## Training missions to document
1. **Repo Orientation** — read-only project map (structure, runtime flow, APIs, UI files, tests, config, deploy, version markers).
2. **Ant Role Training** — ant registry + routing, permissions, allowed/forbidden tools, executable vs visible-only ants.
3. **Build/Test Workflow Training** — solution/test projects, CI, release/version files, validation order.
4. **UI Structure Training** — UI file map, colony graph rendering, theme/color system, API endpoints, safe-modification checklist (preserve vanilla HTML/CSS/JS).
5. **Memory + Pheromone System Training** — schema, scoring, event/source/patch/approval/objective tracking, search/prune flow.
6. **Patch Proposal Discipline** — patch proposal → approval → verifier → audit lifecycle; unsafe patterns to avoid.
7. **Failure Drill** — simulated CI-failure incident workflow (which ants, what to collect, how to verify, avoid overpatching).
8. **V2 Homelab Roadmap Training** — internalize `docs/NORTH_STAR.md` direction and safety rules.
9. **Daily Memory Compression** — compress recent missions into durable operating lessons.

All training missions are read-only, must not modify files, and must not create patch proposals.

## Success criteria
- A fresh install can train itself without modifying files.
- Future patch missions run faster because memory contains repo structure, validation rules, role boundaries, and roadmap direction.
- Pheromone memory rewards safe, successful workflows.

---

# PHASE 4 — V1.9.0 HOMELAB FOUNDATION

**Status: SHIPPED in v1.9.0** — `HomelabRepository` (15 tables in the colony DB), the seven
provider interfaces, `HomelabTargetGuard` (D1, SSRF-isolated), `HomelabCredentialStore` (D2,
write-only secrets + audit), the homelab permission tier + `homelab_operator` role (D3, action
gates OFF until V2.1), the `HomelabScheduler` skeleton (D4, disabled by default), eight
visible-only read-only homelab ants, permission-scoped `/homelab/*` endpoints, `docs/HOMELAB.md`
(D10), and the `Anthill.Tests.Homelab` suite.

## Goal
Introduce the homelab backend foundation without controlling infrastructure.

## Strict scope
Read-only schemas, interfaces, permissions, config, and safety scaffolding only. No Proxmox control. No firewall control. No SSH execution. No destructive actions.

## Required backend folders
```text
src/Anthill.Core/Homelab/           src/Anthill.Core/Homelab/Security/
src/Anthill.Core/Homelab/Scheduling/ src/Anthill.Core/Homelab/Approvals/
src/Anthill.Core/Homelab/Notifications/
src/Anthill.Core/Integrations/  src/Anthill.Core/Inventory/  src/Anthill.Core/Health/
src/Anthill.Core/Security/  src/Anthill.Core/Automation/  src/Anthill.Core/Incidents/
src/Anthill.Core/Power/  src/Anthill.Core/Backups/
src/Anthill.Api/Homelab/  src/Anthill.Api/Health/  src/Anthill.Api/Inventory/  src/Anthill.Api/Incidents/
tests/Anthill.Tests.Homelab/
```

## Required models
`HomelabNode, NetworkDevice, ServiceRecord, VmRecord, ContainerRecord, StoragePoolRecord, StorageDeviceRecord, BackupRecord, HealthCheckResult, HomelabEvent, ChangeRecord, IncidentRecord, DependencyRecord, RiskRecord, CredentialRecord, TargetAllowlistRecord`

## Required tables
`homelab_nodes, network_devices, services, vm_inventory, container_inventory, storage_inventory, backup_inventory, health_checks, homelab_events, change_log, incidents, dependencies, risk_records, homelab_credentials, homelab_target_allowlist`

## Required interfaces
`IInventoryProvider, IHealthCheckProvider, IHomelabEventSink, IHomelabRepository, IIntegrationStatusProvider, IHomelabTargetGuard, ICredentialProvider`

## Required cross-cutting dependencies
- **D1 — Homelab Target Allowlist.** Operator-maintained allowlist for deterministic homelab providers to reach trusted private hosts. Do NOT loosen the general SSRF guard (which keeps blocking private/loopback/link-local for LLM-directed tools). Tests prove isolation.
- **D2 — Credential Store Abstraction.** `ICredentialProvider` on existing encryption; typed credentials by ID; UI saves write-only; API returns only configured/last_verified status; audit event on every use; never log or return secret values.
- **D3 — Homelab Permission Tier.** Permissions `read_homelab`, `manage_homelab_integrations`, `approve_homelab_actions`, `execute_homelab_actions`; recommended role `Homelab Operator` (view + approve, no full admin/provider/shell).
- **D4 — Homelab Scheduler Skeleton.** Interval background runner owning health checks / Proxmox sync / network polls; jitter/backoff; concurrency cap; persists last-run/last-result; only mock/no-op providers in v1.9.0.
- **D10 — Canonical Homelab Design Doc.** Create `docs/HOMELAB.md` (phase-status-at-top style like `docs/AUTONOMY.md`).

## Required ants / services (read-only, visible)
`InventoryAnt, NetworkScoutAnt, HealthAnt, ProxmoxAnt, StorageAnt, BackupAnt, SecurityScoutAnt, ChangeArchivistAnt`

Polling/data-collection is plain C# services — never routed through the model router. LLM-backed ant behavior is used only for explanation, summarization, mission planning, or recommendations.

## Success criteria
Project builds; tests pass; tables migrate cleanly; endpoints are permission-scoped; credentials never returned; allowlist does not weaken general SSRF guard; no real infrastructure actions exist.

## Required tests
migration; ant-registry validation; homelab-summary API; allowlist isolation; credential save/use/redaction; permission matrix.

---

# PHASE 5 — V1.9.1 HOMELAB SCHEDULER + MOCK PROVIDER HARNESS

**Status: SHIPPED in v1.9.1** — `FakeHomelabProvider` base + five mocks (Proxmox/DNS/DHCP/
firewall/health) with `HomelabProviderStatus`, scheduler wiring behind the
`homelab_mock_providers_enabled` gate (off by default), `GET /homelab/providers`, and the shared
`MockProviderHarnessTests` fixture covering run/backoff/concurrency-cap/persistence/allowlist.

## Goal
One shared execution/testing pattern for future homelab providers.

## Required components
`HomelabScheduler, HomelabScheduledJob, HomelabProviderResult, HomelabProviderStatus, FakeProxmoxProvider, FakeDnsProvider, FakeDhcpProvider, FakeFirewallProvider, FakeHealthProvider`

## Requirements
Scheduler runs mock providers; shared mock-provider test harness; state survives restart; no check stampede (jitter/backoff + per-provider concurrency cap); consistent status/results; disabled-by-default config gate; no real network calls.

## Validation
scheduler run; backoff; concurrency cap; persistence; fake-provider fixture.

---

# PHASE 6 — V1.10.0 INVENTORY + SERVICE REGISTRY

**Status: SHIPPED in v1.10.0** — dependency mapping + import/export in `HomelabRepository`, the
full Phase 6 API surface (hosts/services PUT, dependencies GET/POST/DELETE, import/export), the
Homelab Inventory console page (hosts, services, ports, dependencies, recent changes, JSON
import/export), operator-editable homelab gates, and `InventoryRegistryTests`. Also fixed in this
release: the Patch Center apply 403 (apply_patch gate now follows patch_application_enabled).

## Goal
ANTHILL knows what exists (manual/import-based only; no active scanning required).

## Required features
Manual host/service registration; import/export inventory JSON; device role tags; service ownership; dependency mapping; ports/protocols/URLs/notes; "what runs where?" and "what depends on this?"; ChangeRecord on edits.

## Required API
```text
GET/POST /homelab/hosts    PUT /homelab/hosts/{id}
GET/POST /homelab/services PUT /homelab/services/{id}
GET /homelab/dependencies  POST /homelab/import  GET /homelab/export
```
Read = `read_homelab`; write = `manage_homelab_integrations`.

## UI panels
Hosts, Services, Ports, Dependencies, Recent changes.

## Validation
repository; API; import/export round-trip; permission; UI smoke.

---

# PHASE 7 — V1.11.0 HEALTH CHECKS + NOTIFICATIONS

**Status: SHIPPED in v1.11.0** — `HealthCheckRunner` (ping/HTTP/TCP/service-URL + disk/uptime
placeholders; allowlist-gated before any I/O; strict timeouts), `health_check_schedules` CRUD,
incident-candidate promotion at 3 consecutive failures, config-gated `NotificationService`
(Slack/Discord/generic, off by default, URL-free audits), the shared-scheduler `health-checks`
job, `/homelab/health/*` + `/homelab/notifications/test` endpoints, the Homelab-page Health
panel, and `HealthAndNotificationTests` on loopback sockets.

## Goal
ANTHILL can tell what is alive, degraded, or broken. No auto-remediation.

## Required checks
Ping, HTTP status, TCP port, service URL, disk placeholder, uptime placeholder, failed history.

## Backend
`HealthCheckRunner, HealthStatus, HealthSummary, AlertRecord, HealthCheckSchedule, HealthCheckResult`

## Notifications (config-gated, disabled-by-default)
Slack webhook, Discord webhook, generic webhook. Fire on: health-check failure, new incident candidate, pending approval (once V2.1 exists).

## Requirements
Checks route through the Homelab Target Allowlist; wired into `HomelabScheduler`; strict timeouts so checks cannot hang the app.

## Validation
health classification; mock HTTP/TCP; timeout; notification; UI smoke.

---

# PHASE 8 — V1.12.0 PROXMOX READ-ONLY INTEGRATION

**Status: SHIPPED in v1.12.0** — GET-only `ProxmoxApiClient` (write operations structurally
impossible; proven by type-surface and wire-traffic tests), `ProxmoxInventoryProvider` (nodes,
QEMU VMs, LXCs, storage incl. backup-capable pools, failed tasks as stable-id events) +
`ProxmoxHealthProvider` on the shared scheduler, credential-store + target-allowlist discipline,
`/homelab/vms|containers|storage|proxmox/*` endpoints, Virtualization panels on the Homelab page,
and `ProxmoxIntegrationTests` against a mock PVE API. Per-VM snapshot detail and deep backup
inspection are deferred to V2.2 (backup intelligence).

## Goal
Connect ANTHILL to Proxmox safely in read-only mode. No start/stop/reboot/migrate/delete/clone/resize/config writes.

## Read-only features
connection profile, nodes, VMs, LXCs, status, CPU/RAM/storage, uptime, snapshots, backup status, failed Proxmox tasks.

## Backend
`ProxmoxInventoryProvider, ProxmoxHealthProvider, ProxmoxTaskRecord` — credential via CredentialStore; requests routed through the Target Allowlist; sync wired into `HomelabScheduler`; secrets never exposed.

## Validation
mock Proxmox API; config validation; no-write permission; credential redaction; UI smoke.

---

# PHASE 9 — V1.13.0 NETWORK + SECURITY AWARENESS

**Status: SHIPPED in v1.13.0** — `RiskAnalyzer` with all nine finding kinds and stable-id
reconciliation (auto-resolve on fix, sticky acknowledgements), the manual/import network-device
registry (in the export bundle), the `risk-analysis` shared-scheduler job, `/homelab/devices` +
`/homelab/risks` endpoints, Network & Risk console panels, and socket-free `RiskAwarenessTests`.
No active scanning exists; the phase is zero-network-I/O by construction.

## Goal
Understand the network shape and obvious risks. Awareness/reporting only; no firewall/DNS/DHCP writes.

## Features
Subnet/VLAN/DHCP-static-IP/DNS inventory; open-port registry; unknown-device placeholder; internet-exposed service tracking; risk-finding model. Any active scan is disabled by default and routed through the Target Allowlist.

## Findings
risky open ports; unknown device joined; ownerless service; un-backed-up host; internally/externally exposed dashboard; duplicate IP; missing DNS name; service without health check; credential configured but never verified.

## Validation
finding generation; duplicate IP; exposure classification; allowlist; UI smoke.

---

# PHASE 10 — V1.14.0 INCIDENT + CHANGE MEMORY

**Status: SHIPPED in v1.14.0 — the V1.x line is complete.** `IncidentManager` (candidate sweep
with per-subject dedupe, suspect-flagged timelines, similar-incident matching with verbatim fix
memory, repeat-offender patterns), `/homelab/incidents/*` endpoints, the Incidents console panel
with timeline drawer, and the full IApprovable design: interface + `ApprovableView`, the unified
`GET /homelab/approvals/unified` queue projecting today's patch approvals, the inert V2.1
`ActionProposal` skeleton with blast-radius rubric fields, and `docs/APPROVALS.md` binding V2.1's
executor to five safety requirements. Next: PHASE 11 — the V2.0.0 Homelab Command Center.

## Goal
Connect failures to recent changes and past fixes. Incident tracking, timelines, recommendations only. No auto-fixes.

## Features
`IncidentRecord` + timeline; recent-changes-before-failure; similar-incident matching; repeated-failure pattern detection; pheromone trails for fixes ("this fixed it last time"); root-cause notes; links to hosts/services/VMs/LXCs/health-checks/change-records/mission IDs/objectives/approvals.

## IApprovable design (design here, before V2.1)
Shared abstraction for future action proposals and existing patch approvals: one pending-approvals UI, one audit trail, one dedupe path, with different renderers (patch diff, action proposal, network change preview).

## Validation
incident timeline; similar incident; memory search integration; UI smoke; IApprovable design review.

---

# PHASE 11 — V2.0.0 HOMELAB COMMAND CENTER LAUNCH

## Goal
Turn V1 backend foundations into the main homelab dashboard. Mostly read-only — visibility, not full control.

## Questions ANTHILL must answer
What is broken? Where is it running? What does it depend on? What changed recently? What should I do next? What is not backed up? What is exposed? What is unknown on the network?

## UI rules
Preserve the single embedded vanilla HTML/CSS/JS UI (no React/Tailwind migration); CSS variables + reusable render helpers; additive only; real data first with labeled fallbacks (never fake operational values); timers cleaned up; reduced-motion aware; no broken glyphs.

## Cards
Health Summary, Critical Services, Failed Checks, Recent Changes, Active Incidents, Proxmox Nodes, Storage/Backups, Security Findings. Plus dependency graph; service/host/incident detail pages; mission memory linked to incidents; one aggregation endpoint.

## Validation
dashboard endpoint; health summary; dependency graph; UI syntax/glyph; CI release artifact.

---

# PHASE 12 — V2.1.0 APPROVAL-GATED HOMELAB ACTIONS

## Goal
Controlled low-risk actions only after explicit approval. No autonomous dangerous behavior.

## Allowed initial actions
restart service/container; start/stop/restart VM/LXC; create snapshot; run backup; mark incident resolved; update inventory; run approved diagnostic command.

## Forbidden in V2.1
delete VM/LXC/container; delete firewall rules; factory reset; wipe disks; auto-modify secrets; disable backups; open internet exposure without approval.

## Required safety
IApprovable-based `ActionProposal`; approval required; blast-radius score; rollback/recovery note; audit log; dry-run when possible; permission check; post-execution verification; secret redaction; `.anthill/HOMELAB_STOP`.

## Blast-radius rubric inputs (define before implementation)
dependency fan-out; service criticality; backup coverage; replica count; maintenance window; host/node role; internet exposure; interrupts current user-facing service; rollback availability.

## Permissions
`approve_homelab_actions` and `execute_homelab_actions` are separate checks. Endpoints: `POST /homelab/actions/stop`, `POST /homelab/actions/resume`.

## Validation
approval gate; audit; permission; dry-run; mock executor; kill-switch isolation; rollback note.

---

# PHASE 13 — V2.2.0 BACKUP + RESTORE INTELLIGENCE

## Goal
Know what is protected, what is not, and what recovery looks like.

## Features
backup coverage map; stale-backup warnings; failed-backup detection; restore priority; restore confidence score; "what dies if this disk/node fails?"; restore runbook generation; backup dependency view; blast-radius simulation for node/disk loss.

## Validation
backup coverage; stale backup; blast-radius; runbook generation.

---

# PHASE 14 — V2.3.0 AUTOMATION RULES

## Goal
Simple self-healing and alerting. Low-risk automation only; risky actions still require approval.

## Examples
down → restart once; backup fails twice → alert; disk > 90% → warn; UPS on battery → shut down low-priority VMs (policy/approval); unknown device joins → flag; repeated health failure → incident.

## Backend
`AutomationRule, AutomationTrigger, AutomationAction, AutomationRunRecord, AutomationPolicy, AutomationCooldown` — reuse `HomelabScheduler`; cooldowns + retry limits + loop prevention; automation audit log; disabled-by-default; risky automation routed through IApprovable/ActionExecutor/HOMELAB_STOP.

## Validation
rule trigger; cooldown; approval-required; loop prevention; HOMELAB_STOP.

---

# PHASE 15 — V2.4.0 NETWORK CONTROL LAYER

## Goal
Manage DNS/DHCP/firewall safely. All network changes require approval.

## Features
DNS record management; static IP tracking; DHCP lease awareness; firewall rule explanation; VLAN documentation; proposed firewall changes; exposed-service audit; change preview before apply.

## Implementation
IApprovable network-action proposals; `IDnsProvider` / `IDhcpProvider` / `IFirewallProvider`; diff/preview; approval-gated apply; rollback/export-config note; reuse the shared mock-provider harness.

## Validation
mock provider; approval; diff preview; rollback note; allowlist; secret redaction.

---

# PHASE 16 — V2.5.0 FULL HOMELAB OPERATIONS LAYER

## Goal
Make ANTHILL the main operational control plane for the homelab.

## Capabilities
dependency-aware maintenance; node drain planning; Proxmox migration recommendations; service recovery missions; automatic documentation; power/cost optimization; security hardening missions; recurring health summaries; maintenance windows; action sequencing; operator runbook generation.

## Validation
dependency planner; maintenance simulation; recovery mission; documentation generation; action sequencing.

---

# PHASE 17 — V3.0.0 BOUNDED AUTONOMOUS HOMELAB OPERATOR

## Goal
A bounded local autonomous homelab operator, bounded by risk, approvals, permissions, budgets, maintenance windows, and kill switches.

## Endgame behavior
Watches the network; detects issues; creates missions; proposes fixes; applies pre-approved low-risk fixes; requires approval for risky changes; remembers every result; improves with pheromone history; generates operator reports; keeps the homelab documented.

## Autonomy controls
risk tiers; approval thresholds; emergency stop; HOMELAB_STOP; maintenance windows; safe mode; max action frequency; loop detection; rollback memory; audit export.

## Success criteria
Detects common failures unprompted; resolves approved low-risk failures; escalates risky failures; never performs dangerous actions silently; uses incident/pheromone memory to improve.

---

## 6. Recommended Workflow Improvements

1. **One approval system** — IApprovable, not separate queues for patches/actions/network changes.
2. **One scheduler** — `HomelabScheduler`, not per-subsystem timers.
3. **One mock-provider harness** — one test fixture architecture for Proxmox/DNS/DHCP/firewall/health.
4. **One credential store** — `ICredentialProvider`, not per-provider storage.
5. **One event model** — all homelab systems write `homelab_events` + `change_log` so memory/pheromones can learn.
6. **One master roadmap** — this file. Subsystem docs must not contradict the master build order.
7. **Read-only before action-gated** — every integration lands read-only first.
8. **Deterministic code before LLM judgment** — polling/sync/health/scans/provider calls are deterministic C#; LLM ants summarize and recommend, they do not poll infrastructure.

---

## 7. Version Completion Template

Every version must end with:

```text
Version updated.
CHANGELOG updated.
README version note updated if applicable.
docs/NORTH_STAR.md updated if roadmap changed.
docs/HOMELAB.md phase marker updated if homelab-related.
Tests added/updated.
CI passing.
API documented if applicable.
UI panel added if applicable.
Safety boundary documented.
No secrets exposed.
Migration tested if database changed.
Release artifact generated if release workflow exists.
No py.old changes.
No conflict markers.
No old version markers.
```

---

## 8. Final Target

By **V2.0**, ANTHILL should show: every known host; every known service; where each runs; what each depends on; what is healthy/broken; what changed recently; what is not backed up; what is exposed/risky; what incident is active; what next action is recommended.

By **V2.5**, ANTHILL safely helps operate the homelab.

By **V3.0**, ANTHILL becomes a bounded autonomous homelab operator.

The original goal stays intact:

```text
Local-first swarm intelligence.
Visible mission execution.
Persistent memory.
Safe patch/action approvals.
Pheromone learning.
A command center that eventually runs the homelab like a local Jarvis, without giving up control.
```
