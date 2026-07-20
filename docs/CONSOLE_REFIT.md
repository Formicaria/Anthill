# Console Refit — Integration Platform + UX Architecture Program (v2.5.x → v2.6)

Status: Canonical plan for the UX/architecture refinement + integration expansion mission
(operator brief, 2026-07). Ordered like NORTH_STAR: each phase is one reviewable release.
Read-only before action-gated; one credential store; one scheduler; one allowlist (D1);
widgets are modular and page-agnostic. Homarr/Homepage/Grafana/Portainer are UX references,
never copied.

## Phase R1 — v2.5.1: Generic integration framework (backend)

Generalize the *arr pattern into the platform core. `IIntegration` contract:
kind, category (media|download|infra|network|monitoring|automation|dev|storage|auth|notify),
auth mode (api_key|token|basic|none), capability list (widget kinds it can feed), a GET-only
client factory (credential store + D1 allowlist + strict timeout, structurally no writes),
and a deterministic `SyncAsync` publishing typed widget payloads into one `integration_state`
table (integration id → widget kind → JSON payload + freshness). `ArrClient`/`ArrSyncProvider`
become the first implementations behind the contract — endpoints and DB rows migrate, UI keeps
working. Registry-driven: adding an integration = one class + one registry entry, zero schema
or endpoint changes. API: `GET /homelab/integrations`, `POST /homelab/integrations`,
`DELETE .../{id}`, `POST .../{id}/sync`, `GET .../{id}/widgets/{kind}`.

## Phase R2 — v2.5.2: Widget framework (frontend)

One JS widget runtime: `widget(kind, integrationId, el)` with the full lifecycle —
loading / empty / error / success, TTL polling + manual refresh, responsive sizing, and a
layout registry (persisted per operator via /ui/state) prepared for drag-and-drop. First
widget kinds: queue, recent-activity, health, statistics, disk-usage, calendar/upcoming,
failed-imports, logs, alerts, resource-usage. Widgets never know their page.

## Phase R3 — v2.5.3: Navigation + information architecture

The single-page console gains intentional structure: grouped sidebar (Colony / Operations /
Homelab / Configuration stays, but Homelab expands into category sub-pages via the existing
hl3 sub-page engine: Overview, Services, Virtualization, Containers, Storage, Networking,
Monitoring, Automation, Apps, Alerts, Activity). Full redundancy audit: every datum gets ONE
home (approvals, health, events currently render in 3+ places); duplicated card layouts become
shared render helpers. Progressive disclosure everywhere; no modal abuse; keyboard nav (g-keys
already exist — extend to new sub-pages).

## Phase R4 — v2.5.4: Allowlist/blocklist management + collections framework

Full CRUD surface over D1 (+ a generic "collection manager" component other integrations
reuse): search, filter, sort, create/edit/delete, bulk enable/disable/remove, origin +
timestamps + notes visible. Blocklist support lands as first-class (deny beats allow;
executor + guards consume both).

## Phase R5 — v2.5.5+: Integration waves (one PR per wave, read-only first)

W1 download clients: qBittorrent, Transmission, Deluge, SABnzbd, NZBGet. — SHIPPED v2.5.5
   (DownloadIntegrationDefinition; read-only by construction — ProbeAsync is the only op even for
   the RPC-over-POST clients; health/queue/statistics widgets; no new tables/endpoints/pages).
W2 media servers/requests: Plex, Jellyfin, Emby, Tautulli, Overseerr, Jellyseerr.
W3 monitoring: Uptime Kuma, Netdata, Prometheus, Grafana annotations/health.
W4 infra: Portainer, TrueNAS, Unraid (Proxmox/Docker already exist — refit onto contract).
W5 networking: UniFi, OPNsense/pfSense, Traefik, NPM.
W6 notify/auth/dev/home: ntfy, Gotify, Discord/Slack webhooks (reuse NotificationService),
Authentik, Keycloak, GitHub/Gitea, Home Assistant, MinIO.

## Phase R6 — v2.6.0: Arr depth + actionable dashboards

Rich *arr operational data over the widget framework: full queue, grabbed releases, failed
imports, calendar, missing/wanted, root folders, indexer + download-client health, statistics,
history. Category dashboards become actionable (widget quick-actions route through the
approval-gated action pipeline — never direct writes).

## Invariants (every phase)

No hardcoded app assumptions; secrets write-only in the credential store; D1 allowlist before
any I/O; GET-only clients unless the action pipeline gates a write; every loader has
empty/error/timeout states; validate.sh green + version markers + CHANGELOG per release;
PRs merge only on green CI (`gh pr merge --auto`).
