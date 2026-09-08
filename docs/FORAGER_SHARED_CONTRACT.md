# FORAGER–ANTHILL shared integration contract

**Contract version: 1-proposed, received 2026-09-07.** Part I is the operator-supplied contract,
verbatim. Part II is ANTHILL's Phase-0 reconciliation of it against the audited producer and
consumer (`docs/FORAGER_A0_COMPATIBILITY.md`, shipped in the same release as this document): the wire-name
resolution, the decisions the contract asks Phase 0 to make, and the full producer-side
requirement list. The contract's own framing governs how to read Part I: names describe required
semantics, not claims that endpoints already exist.

---

# Part I — the contract (verbatim)

This is a proposed implementation contract. Resolve wire names against the current APIs during
Phase 0, then record the agreed version and fixtures in both repositories. Names below describe
required semantics and are not claims that endpoints already exist.

## 1. Product and code ownership

| Concern | Owner |
| --- | --- |
| Standalone Forager app, installer, API and release artifacts | Forager |
| Source acquisition, parsers, extraction, chunking and canonical schema | Forager |
| Evidence, entity resolution, conflicts, review history and canonical search indexes | Forager |
| Engine builds consumed by both products | Forager |
| Native Anthill controls, managed process lifecycle and project connection UI | Anthill |
| Permission to ingest, retrieve, create missions and execute actions from Anthill | Anthill |
| Mission planning, execution, budgets, verification and operational memory | Anthill |
| Pheromone and procedural learning from observed outcomes | Anthill |
| Revision/event/package schemas and compatibility fixtures | Agreed contract; Forager publishes producer definitions and Anthill validates consumption |

Keep Forager's engine implementation in Forager. Both products use that engine. Anthill must not
maintain copied parsers, a forked canonical schema, or a second implementation of Forager's search
and conflict rules.

The dependency points from Anthill toward the optional Forager service. Forager has no hard
dependency on Anthill assemblies, a running colony, an Anthill account, or its mission database.

## 2. Supported modes

1. **Standalone:** Forager owns its UI, lifecycle and data. All ordinary operations work with
   Anthill absent. Integration settings and delivery failures cannot prevent local processing,
   review, search or export.
2. **Managed:** Anthill starts and supervises a compatible Forager engine from its bundled
   distribution. Anthill owns this process lifecycle; Forager remains the sole owner of its
   canonical database. The user needs no separate terminal or developer runtime installation.
3. **Attached:** Anthill connects to an existing Forager instance. It does not terminate, upgrade,
   replace or silently copy that instance. Local attachment is required. Preserve existing remote
   capability and qualify authenticated remote delivery where enabled.

Managed and attached modes must identify the exact service, data store and mapped project. Two
processes must never open the same Forager database for competing writes. A shared OS user or
familiar port is not sufficient proof that a process is the intended instance.

## 3. Contract discovery and access

Expose a versioned capability response covering engine identity/version, protocol version,
canonical schema version, supported export/import schema versions, authentication requirements and
supported optional operations.

Use additive changes where possible. Refuse incompatible required versions with actionable status.
Never silently downgrade to another project, stale snapshot or unknown service.

Use explicit authenticated pairing for the new integration flow, with scoped credentials,
rotation/revocation and bounded receiver permissions. Existing standalone local behavior must be
accounted for in the migration. A remote connection requires deliberate configuration and real
authentication. Validate service identities and destination URLs; redirects and document-supplied
URLs must not bypass endpoint restrictions.

The browser talks to Anthill's authenticated API in Anthill mode. Agent-selected arguments cannot
choose arbitrary Forager project IDs, endpoints, filesystem roots or credentials.

Project mapping is an authorized server operation. Bind the Anthill project to the specific
Forager instance and project; enforce user/project membership on every read and mutation,
including direct-ID reads, job actions, export downloads, events and package imports.

## 4. Canonical publication

A successfully queued or completed job is not by itself proof that new knowledge is usable.
Publish a revision only after its accepted canonical records, evidence and retrieval index are
consistent and queryable.

Define an immutable revision identity and a deterministic logical content hash. The logical hash
excludes incidental export timestamps and delivery IDs. Separately hash package bytes for transfer
integrity.

A published revision identifies:

- Producer instance, origin project, revision ID and publication sequence.
- Canonical schema and extraction/index version information.
- Added, changed, superseded, withdrawn or deleted records, including tombstones where appropriate.
- Evidence and source versions needed to reproduce the published view.
- Review/conflict state, confidentiality and a manifest of accepted records.
- Any partial-processing warnings, failed-source counts and limits on completeness.

Define the lifecycle for partial runs explicitly. A failed or cancelled attempt cannot emit a
normal complete-publication event. If accepted partial knowledge is published, its partial status
must be explicit and project policy must decide whether automation may use it.

Persist publication and its outgoing notification atomically, or provide an equivalent recoverable
publication ledger that cannot lose the notification after a successful commit. A browser polling
job status is never the notification producer.

## 5. Change feed and completion events

Provide a durable, scoped change feed with replay from a cursor. Push notifications may reduce
latency; the feed is the reconciliation path after lost delivery or downtime. Define retention,
expired-cursor recovery and resynchronization. Restoring or cloning a database must not silently
reuse a delivery identity that hides new changes.

Illustrative notification envelope:

```json
{
  "protocol_version": 1,
  "event_id": "stable-event-id",
  "event_type": "knowledge.revision.published",
  "producer_instance_id": "persistent-instance-id",
  "producer_generation": "generation-or-epoch-id",
  "source_project_id": "forager-project-id",
  "sequence": 42,
  "revision_id": "immutable-revision-id",
  "canonical_schema_version": 1,
  "logical_content_hash": "sha256:canonical-content-digest",
  "publication_status": "complete",
  "job_id": "origin-job-id",
  "occurred_at": "2026-09-07T12:00:00Z",
  "origin_kind": "operator_sources",
  "causation_id": null,
  "export_id": null,
  "package_digest": null
}
```

Large record lists and source text travel through bounded authenticated APIs or a canonical
package, rather than being embedded in notifications. Never accept a destination project or
executable instruction merely because it appears in this envelope.

An export-completed event references the same canonical revision. Re-exporting that revision as
Obsidian, JSON and an Anthill package must not produce three independent analysis missions.

## 6. Export and live delivery

Support two routes into the same Anthill revision-handling workflow:

- **Live service:** verify the published revision is queryable through the mapped provider,
  invalidate relevant caches, and make that revision available to analysis and retrieval. No
  redundant whole-database import is needed.
- **Canonical package transfer:** build the package completely, validate its manifest and digests,
  transfer or stage it atomically, and accept it through a canonical import/snapshot path before
  announcing readiness.

Prefer the recipient Forager engine's canonical package import adapter so Anthill can use its
existing provider. If the current code already has a qualified immutable package provider, reuse
it only if it satisfies the same evidence, scope, revision and temporal semantics. Select one
primary package consumption path during Phase 0; do not implement competing import engines.

Preserve origin IDs and provenance through import, while namespacing separate producer instances
to avoid collisions. Re-importing a package is duplicate-safe and never blindly overwrites newer
local review decisions. Define conflicts between incoming state and recipient review state.

Forager's optional Anthill destination can publish automatically after processing or after
selected exports. If the operator requests an Obsidian or generic JSON output, generate an
accompanying canonical handoff when needed; Anthill must not reconstruct canonical knowledge by
parsing its rendered Markdown export.

Delivery states must distinguish local export complete, pending transfer, accepted by Anthill,
available for retrieval, analysis queued, and downstream outcome where supported. Forager may
report a downstream state only from an authenticated Anthill receipt.

## 7. Reliability and duplicate handling

Use at-least-once delivery with duplicate-safe effects. Do not claim exactly-once networking.

Anthill durably records an incoming event before acknowledging receipt. Scope and authenticate
that receipt. Separate receipt from successful knowledge availability and from completed analysis.

For scheduling, derive a stable key from the source instance, mapped project, knowledge revision,
action type and relevant automation-policy version. Persist submission intent and use the existing
mission queue's idempotency support, or transactionally link receipt and queue work. A crash
between these steps must neither drop the action nor create duplicates.

Handle out-of-order publication events without moving the active view backwards. Reconcile missed
revisions deliberately, using a durable watermark. Bounded retries, backoff, quotas, cancellation
and visible terminal failures must exist on both sides.

Carry origin and causation through generated artifacts. Anthill-created outputs re-ingested by
Forager must be identifiable, must not masquerade as independent evidence, and must not
recursively trigger the same workflow without an explicit policy.

## 8. Retrieval, scope and learning

Preserve evidence IDs, exact excerpts and their hashes, source versions, support levels, dates,
confidentiality, conflicts and lifecycle state. A direct-fact label means that a source states
something; it is not proof of real-world correctness.

Forager owns canonical ranking/indexing. Anthill owns query selection, context budgets,
provenance-aware assembly and which task receives which context. Any embeddings are derived
Forager indexes with versioned model/index metadata and a working non-vector path.

Documents are task data. Their text cannot grant permissions, alter project mapping, raise
autonomy levels, select new network destinations, or authorize its own execution.

Maintain three distinct record types:

1. Canonical sourced knowledge.
2. Project/mission memory and untested document-derived learning candidates.
3. Outcome-backed procedural/pheromone learning.

Reading or importing a successful-sounding procedure does not count as executing it successfully.
Project-specific memory and candidate retrieval remain project-scoped. Generalization requires an
explicit provenance-preserving policy and cannot merely strip a customer's name.

## 9. Compatibility and shared proof

Ship machine-readable request/response/event/package fixtures, including invalid and cross-project
cases. Pin the contract fixture version in both repositories.

Qualify standalone Forager, Anthill with knowledge disabled, managed Forager, attached Forager,
and supported remote/package delivery. Test current supported platform artifacts and at least the
declared upgrade/compatibility window.

At minimum, demonstrate: clean install; complete ingest/search/review/export; real source
citations; delivery while Anthill is down; restarts at receipt/submission boundaries; duplicate
and reordered events; package corruption; changed/withdrawn evidence; scope isolation; unsupported
versions; and one later mission consuming valid scoped memory.

Tests must cross the real API and durable-state boundaries. A mock response, rendered UI badge or
an emitted event without a consumer is insufficient evidence of completion.

---

# Part II — ANTHILL's Phase-0 reconciliation (against FORAGER 0.1.4 @ `258baf8`)

This section resolves the contract's names against the producer that actually exists, records the
decisions the contract instructs Phase 0 to make, and carries the authoritative producer-side
requirement list. The audited ground truth is `docs/FORAGER_A0_COMPATIBILITY.md`; the contract
arrived after that audit shipped, and this reconciliation supersedes the audit's interim P-list
(P1–P6 below are the audit's, renumbered where extended).

## The producer moved while Phase 0 was being recorded — F0 @ `566de69`

FORAGER's own Phase 0 landed on its branch as this document was being written (producer phases are
F-numbered; its gap list is G-numbered; its audit is `anthill-integration-audit.md` and it pins the
contract verbatim as `01-SHARED-CONTRACT.md` in that repository — the both-repositories requirement
is now met on both sides). What `566de69` actually implements, verified in source:

- **`GET /api/capabilities`** — `protocol_version: 1`, `canonical_schema_version`, engine
  identity/version, `instance {instance_id, generation, generation_reason, created_at, data_dir,
  mode}`, capability flags that are HONEST (`publication`, `change_feed`, `push_delivery`,
  `canonical_import`, `authentication` all `false` until each is real), and an explicit
  `authentication {required: false, schemes: [], note}` block. **P1 substantially landed**
  (remaining: supported export/import schema versions are not yet enumerated); **P7 landed** —
  identity travels with the DATABASE (copy the data dir, same instance, deliberately) and the
  generation exists to invalidate cursors after a restore/clone.
- **A store lock as a ROW, not a lockfile** (migration 006): heartbeat-based, acquired inside
  `BEGIN IMMEDIATE`, stale after 90s against a 20s beat; a second process over the same data
  directory refuses at startup with exit code 11 naming the holder. Contract §2's
  "two processes must never open the same database for competing writes" is enforced
  producer-side; `recoverInterrupted` is gated on holding the store.
- 308/308 producer tests at that tip (ten new over the baseline this document's audit recorded).

Its §3 also proposes the wire names for the still-open rows, which ANTHILL ADOPTS now so both
sides build toward the same surface: `GET /api/feed?cursor=` with stable ordering and a typed
`cursor_expired` (P2); `knowledge_revisions` written in one transaction with counter-based
`rev_…` ids — deliberately NOT content-derived, because two runs producing identical content are
still two publications — and an explicit `completeness: complete|partial` (P8);
`pairing_credentials` bearer tokens, scoped and revocable, off by default but REQUIRED the moment
the bind is non-loopback (P3). A1 consumes `/api/capabilities` and retires the interim
endpoint+data_dir+version identity tuple.

And it kept moving: `fc3a44b` **completes P1** — export schema versions enumerated per format as
`capabilities.exports.<format>.package_version`, each with a `canonical` flag (Obsidian is
`false`, which is §6's "must not reconstruct canonical knowledge from rendered Markdown" as a
checkable field), and `capabilities.imports: []` so "cannot import" is distinguishable from "too
old to say" — and lands producer-side scope enforcement on all 18 direct-id routes via an
`X-Forager-Project` header (`enforceScope`; a foreign id is 404, not 403). Until P3 the header is
a declaration rather than a credential, but the moment pairing lands the same call sites become
authorization with no edit. **Consumer obligation adopted: from A1 on, ANTHILL sends
`X-Forager-Project` on every direct-id call** — it costs nothing today and is the enforcement
handshake tomorrow; the response-side project checks stay regardless, as the consumer's half.

## Wire-name resolution — contract semantics vs. producer 0.1.4

| Contract requirement | Producer 0.1.4 reality | Resolution |
| --- | --- | --- |
| §3 versioned capability response (engine identity, protocol, schema, import/export versions, auth requirements, optional ops) | `GET /api/ready` (version, schema_version, backend) + `GET /api/settings` (limits, formats, roots). No protocol version, no instance identity, no auth declaration, no import versions | Consume `/api/ready` + `/api/settings` as the interim capability pair; full response is **P1** |
| §2 "identify the exact service and data store"; §5 `producer_instance_id` / `producer_generation` | No persistent instance identity anywhere. `/api/settings` reports `data_dir`, which is the closest fact | Managed mode: ANTHILL owns the process and its `FORAGER_DATA_DIR`, so identity is held by ownership. Attached mode: interim identity = endpoint + `data_dir` + version tuple, verified on every (re)connect; persistent id + generation is **P7** |
| §3 authenticated pairing, scoped credentials, rotation/revocation | No authentication of any kind (deliberate; binds `127.0.0.1`) | ANTHILL already sends `Authorization: Bearer` when configured; producer auth consuming it is **P3**. Until then: managed = loopback + process ownership; attached-remote = deliberate config + authenticating proxy, per FORAGER's own docs |
| §4 immutable revision identity + logical content hash + publication ledger | No revision counter, no ledger, no publication event. Jobs publish nothing; the export manifest is the only content-addressed artifact | Producer revisions/ledger is **P8**. Interim delivery identities (consumer-side, and named as such): package delivery = sha256 of `anthill-package.json` (whose own per-file sha256 make the logical content reproducible); live delivery = completed `job_id` + terminal timestamp watermark |
| §5 durable scoped change feed with cursors; push optional | Absent entirely (no SSE/WS/outbox/`since=`/ETag) | **P2**. Until then A3's reconciliation path is polling `GET /api/projects/:id/jobs` + `GET /api/projects/:id/exports` against a durable ANTHILL-side watermark — explicitly the interim substitute for the feed, not a second delivery system |
| §5 envelope fields | N/A (no producer events) | ANTHILL's A3 receipt table adopts the envelope's field vocabulary now (`event_id`, `sequence`, `revision_id`, `producer_instance_id`, `producer_generation`, `origin_kind`, `causation_id`, `logical_content_hash`, `publication_status`) so producer events land in an already-shaped consumer when P2/P8 arrive; interim rows are synthesized from polling with `origin_kind` saying so |
| §6 export-completed references the same revision; three export formats ≠ three missions | Export IDs are random; no revision to reference | Interim: A3 deduplicates on the **logical package identity** (manifest hash), and A4's proposal key never includes export id — so re-exports of the same content collapse. Proper fix rides P8 |
| §7 idempotency keys / duplicate-safe POSTs | None on any POST | **P6**; consumer-side receipts + the A4 stable action key carry dedupe until then |
| §3 membership enforcement incl. direct-ID reads | Record-by-id routes are global lookups | Producer-side enforcement joins **P3**; ANTHILL keeps its response-side project checks (already present in `GetAsync`/`GetJobAsync`) as the consumer's half of the boundary — both halves, permanently |
| §9 machine-readable fixtures, pinned versions | Producer-side tests exist (298 at 0.1.4, per FORAGER's own `verification.md`); no shared fixture set | **P10**: producer publishes request/response/event/package fixtures; ANTHILL pins and validates them (A6). Fixture version starts at `contract-1` |

## Phase-0 decisions (the contract asks; ANTHILL answers)

1. **Primary package consumption path: the recipient Forager engine's canonical import adapter
   (producer import API = P9). The documented-but-never-built C# `ForagerPackageKnowledgeProvider`
   is REJECTED, permanently.** Contract §6 prefers the engine adapter, and §1 forbids a second
   implementation of Forager's search and conflict rules — which is exactly what a C# JSONL
   provider would have to become to satisfy the same evidence, scope, revision and temporal
   semantics. In managed mode the recipient engine is already running; package transfer becomes:
   validate manifest digests → import into the local engine (P9) → the existing HTTP provider
   serves it. `KnowledgeOptions.PackagePath` (dead, and hazardous — a non-empty value skipped
   endpoint validation) was removed at v0.3.8.143 rather than implemented.
2. **Contract version pinned: `1-proposed` (this document), fixture version `contract-1` reserved
   for the P10 fixture set.** Producer-version window unchanged from A0: `>= 0.1.4`,
   `schema_version == 1`, anthill `package_version == 1`.
3. **Interim delivery identities are consumer-side constructions and every stored row says so**
   (`origin_kind` distinguishes a synthesized polling receipt from a producer event), so P2/P8's
   arrival upgrades the pipeline without reinterpreting old rows.
4. **Both modes verify identity before first use and on every reconnect** — managed by process
   ownership + data-dir ownership, attached by the endpoint/data_dir/version tuple until P7 —
   and refuse on mismatch rather than proceeding against an unknown instance (§2's "familiar port
   is not sufficient proof").

## Producer-side requirements — the authoritative list (handoff to FORAGER)

| # | Requirement | Contract § | Until it lands |
| --- | --- | --- | --- |
| P1 | Versioned capability response: engine identity/version, protocol version, canonical schema version, supported export/import schema versions, auth requirements, optional operations | §3 | **COMPLETED at producer `fc3a44b`** (`GET /api/capabilities` incl. per-format export package versions with `canonical` flags and explicit `imports: []`). ANTHILL consumes it in A1; until then `/api/ready` + `/api/settings` |
| P2 | Durable, scoped change feed with cursor replay, retention and expired-cursor recovery; optional push — agreed wire shape `GET /api/feed?cursor=` with typed `cursor_expired` | §5 | Polling reconciliation against jobs/exports with a durable ANTHILL watermark |
| P3 | Authentication: `pairing_credentials` bearer tokens (scoped, revocable, off by default, REQUIRED on non-loopback bind), consuming the Bearer ANTHILL already sends; membership enforcement on direct-ID reads | §3 | Loopback-managed engine; authenticating proxy for remote; ANTHILL response-side project checks |
| P4 | Zip-level checksum for exports + a portable (non-standalone) artifact per release | §4 (transfer integrity) | Verify inner-file sha256s after unzip; ship standalone |
| P5 | `openapi.json` completeness (7 missing paths, 1 wrong status code, 2 missing query params at 0.1.4) | §9 | Hand-written C# client |
| P6 | Idempotency keys on process/export POSTs | §7 | Consumer-side receipt dedupe + stable action keys |
| P7 | Persistent `producer_instance_id` + `producer_generation` in the capability response | §2/§5 | **Landed at producer tip `566de69`** (identity travels with the database; generation invalidates cursors after restore/clone; store lock refuses a second writer with exit 11). ANTHILL consumes in A1 |
| P8 | Immutable revision identity, deterministic logical content hash, and an atomic publication ledger (a completed job is not a publication) — agreed shape: `knowledge_revisions` in one transaction, counter-based `rev_…` ids, explicit `completeness: complete\|partial` | §4 | Consumer-side interim identities: package-manifest hash / job-id watermark, labeled as synthesized |
| P9 | Canonical package IMPORT adapter (accept an `anthill`-format package into an engine, duplicate-safe, review-preserving, instance-namespaced) | §6 | Live-service delivery only; no package consumption path exists and none is faked |
| P10 | Machine-readable contract fixtures (request/response/event/package, including invalid and cross-project cases), versioned `contract-1` | §9 | A6 builds ANTHILL's own fixture harness against the live API |

## What the contract confirms about work already shipped

The consumer doctrine the contract mandates is already ANTHILL's: scope is ambient and never an
agent-chosen argument (v0.3.8.121/.136); an unmapped project retrieves nothing rather than falling
back (`ResolveKnowledgeScope`); documents are task data and their text authorizes nothing
(`UiChangeGate`'s operator-ask-only rule, the ingestion path guards); the three record types in §8
map onto canonical knowledge (FORAGER), mission/project memory + candidates
(`memory_candidate` / `procedural_candidate`, `auto_promote: false`), and outcome-backed learning
(`LearningRecorder`, reinforced only by `completed_verified`); and reading a procedure never
counts as executing it (`ColonyRecallTests.WhatKnowledgeExists_ExcludesUnprovenCandidates`).
