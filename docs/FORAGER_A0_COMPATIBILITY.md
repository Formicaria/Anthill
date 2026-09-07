# FORAGER integration — A0 audit, compatibility decision, and pinned matrix

Phase A0 of the Forager-first-party integration program. This document is the gate artifact: the
compatibility decision, the runnable producer version, and the feature/API matrix, each verified
against the actual trees rather than against any design document.

> **The shared contract arrived after this audit shipped.** The operator delivered
> `01-SHARED-CONTRACT.md` one release later; it is recorded verbatim, reconciled against this
> audit, and extended in `docs/FORAGER_SHARED_CONTRACT.md` — which now carries the AUTHORITATIVE
> producer-requirement list (P1–P10; this document's P1–P6 are its first six) and the Phase-0
> decisions. This audit remains the measured ground truth for FORAGER 0.1.4 @ `258baf8`.

## Provenance — what was actually inspected

| Fact | Value |
|---|---|
| ANTHILL commit audited | `8491fee` (v0.3.8.138, `main`, clean tree) |
| Planning audit's claimed commit | `8394f18dc5db7a83b0c43cc9b2732a819499f4e7` — **not in this repository's history**; every observation was re-verified against `8491fee` rather than trusted |
| FORAGER commit audited | `258baf8` (branch `fix/subject-less-attribute-key`, clean tree), sibling checkout `../forager` |
| FORAGER version | 0.1.4 (`package.json`, `FORAGER_VERSION`), `SCHEMA_VERSION = 1` |
| `01-SHARED-CONTRACT.md` | **Not delivered.** The execution prompt names it as attached; no such file exists in either repository or in the session's inputs. The reconciliation base is therefore `docs/FORAGER_INTEGRATION.md` (this repo, audited at v0.3.8.120 / FORAGER 0.1.0) plus FORAGER's own `docs/` — recorded here so a later session does not go hunting for a document that was never provided |
| ANTHILL baseline tests | Full suite green at `8491fee` (all 334 test classes, 0 failures — the v0.3.8.138 release run) |
| FORAGER baseline tests | **298/298 green, 17 files** (`vitest run`, Linux, Node 22.23.2, fresh `npm install`). FORAGER's own `verification.md` says 290 — the branch has since added eight |

## The compatibility decision

**Pin: FORAGER 0.1.4 / schema_version 1 / anthill package_version 1, over HTTP, with the
`anthill` export package as the only integrity-bearing delivery unit.**

| Pinned item | Value |
|---|---|
| Producer version window | `>= 0.1.4 && schema_version == 1 && anthill package_version == 1`. Version read from `GET /api/ready` (`version`, `schema_version`); package version validated from `anthill-package.json` (`package_version`) on every package consumed |
| Runnable producer artifact | `forager-0.1.4-win-x64-standalone.zip`, sha256 `63c345fe996b13ec190ac09c0b6a9c90a4bf59d1d5d7953525a051d8f1b07f7c` (digest file verified against the zip). **The only 0.1.4 artifact that exists** — no portable (no-runtime) 0.1.4 zip was built. Standalone bundles Node 22.20.0, so a managed engine needs no host Node |
| Node floor (source runs) | `>= 22.13.0`, hard — the DB layer is built-in `node:sqlite`. FTS5 availability is a property of the host Node's SQLite build (runtime-detected, reported as `search_backend`); never pin on a backend string |
| Readiness | `GET /api/ready` (200/503, touches the DB). `GET /api/health` is liveness only and never fails — a supervisor must not treat it as readiness |
| Capability discovery | `GET /api/settings` (limits, source types, export formats, roots, embeddings, search backend) + `/api/ready`. There is no dedicated capability/feature-flag endpoint — producer-side requirement P1 below |
| Client generation | **Hand-written C# client only.** `GET /api/openapi.json` covers 35 of the server's 42 real paths and mis-declares at least one status code (201 vs the real 202 on export create). Not safe as a codegen source |
| Error contract | `{"error":{code,message,request_id,fields?}}` + `x-request-id` header; codes `validation_error/forbidden/not_found/conflict/file_too_large/unsupported_media_type/too_many_files/internal_error`; lists are `{items,page,page_size,total}` |
| Delivery / "revision" identity | FORAGER has **no revision counter, no cursor, no event feed** anywhere. Two delivery units are defined for A3: **(a) package delivery** — a completed `anthill` export, identity = sha256 of its `anthill-package.json` manifest (which itself carries per-file sha256 for all data files); **(b) live delivery** — a completed processing job, identity = `job.id` + its terminal timestamp, used as a polling watermark. Both are consumer-side constructions and say so |
| Auth posture | FORAGER has **no authentication of any kind** (deliberate: local-first, binds `127.0.0.1`). ANTHILL already sends `Authorization: Bearer {token}` when configured — the producer ignores it today, a fronting proxy can enforce it, and a future producer-side key consumes it without a consumer change. Managed mode therefore binds loopback and treats the engine as ANTHILL-owned; attached-remote mode requires `knowledge_forager_allow_remote` plus an authenticating proxy, exactly as FORAGER's own docs instruct |

## Producer feature/API matrix

Verified against source (`src/server/api/routes/*.ts`), not against `openapi.json`.

| Capability | Producer state | Anthill consumer state at `8491fee` |
|---|---|---|
| Health/readiness | `GET /api/health` (liveness), `GET /api/ready` (readiness + version + backend) | `ProbeAsync` → `/api/ready` ✓ |
| Capability report | `GET /api/settings` (richest), no dedicated endpoint | Not consumed |
| Projects | full CRUD + archive/restore; `DELETE` archives, nothing is deleted; list unpaginated | Not consumed (map is config-only) |
| Source upload (bytes) | `POST /api/projects/:id/sources` — multipart, repeated `files` + index-matched `paths`, 50 MB/file, 200 files, zips expand | **Not consumed** — no upload route or UI in ANTHILL |
| Directory import (server-side paths) | `POST /api/projects/:id/sources/directory`, fenced by input roots on the resolved real path | Consumed by `StartIngestionAsync` ✓ (with ANTHILL-side path guard first) |
| Processing | `POST .../process` (202, 409 if running), `GET /api/jobs/:id` (11 stages), cancel (cooperative), retry (checkpointed) | All consumed ✓ |
| Knowledge query | faceted paged list, item + evidence, search (`backend` reported per call) | Search/retrieve/get/evidence consumed ✓ |
| Entities / relationships | paged lists, entity detail, merge-candidates, accept/reject merge, unmerge | Read paths consumed; merge operations not |
| Review | `POST /api/knowledge/:id/review` (`mark_reviewed/reject/restore/archive`), review-events audit trail | **Not consumed** — ANTHILL's `knowledge_review_proposed` event has one writer and zero readers; nothing ever calls the producer |
| Conflicts | list, detail, resolve (4 actions), reopen, bulk-resolve | List consumed ✓; resolution not |
| Manual knowledge authoring | `POST .../knowledge`, `PATCH /api/knowledge/:id` (manual items only) | Not consumed |
| Exports | `obsidian`/`jsonl`/`anthill` zips (202 + poll + download, 409 while running); folder export with plan/diff + destination manifest; history capped 50 | **Not consumed** |
| Anthill package | zip: manifest + 6–7 JSONL files; manifest has per-file sha256, scopes (tenant/general) with confidentiality, counts, consumption rules | **No package consumer exists.** `KnowledgeOptions.PackagePath` is a dead field — and a hazard: any non-empty value skips endpoint/loopback validation in `Unusable()` and still routes to HTTP. Neutralize in A1/A3 |
| Event feed / webhooks / cursors | **Absent entirely** (no SSE, WS, outbox, `since=`, ETag) | A3 builds polling reconciliation + receipts; push marked pending on P2 |
| Auth / pairing | **Absent entirely** | Bearer sent when configured (ignored today); loopback default enforced consumer-side |
| Multi-project isolation | every list/search/export is `project_id`-scoped; **record-by-id routes are global lookups** (`/api/knowledge/:id` etc.) | ANTHILL re-checks project on the response (`GetAsync`, `GetJobAsync`) ✓ — keep; it is the actual tenancy boundary on those routes |
| Idempotency | none on any POST (`/process` 409s while running; a second export POST makes a second export) | A3/A4 must carry consumer-side idempotency identity |

## Consumer reuse/gap map (ANTHILL at `8491fee`)

**Reusable as-is:** `ForagerKnowledgeProvider` (13 operations, scope-gated, response-side project
checks), `KnowledgeModule` gating, the 13 `/knowledge/*` API routes, `KnowledgeScopeContext` +
`Queen.ResolveKnowledgeScope` (entered at intake since v0.3.8.136, refusal-not-fallback),
`knowledge_enabled` gate + settings catalog, the durable job queue (`ApiJobRegistry`, project id
survives crash-requeue since v0.3.8.137), the Director (objective→project→queue), mission intake +
dispatch-plan materialization (v0.3.8.138), `LearningRecorder`/`MemoryCandidateIngest`/
`ProceduralCandidatePromotion` as the outcome path, and the Knowledge UI shell (status/search/
retrieve/item/conflicts/sources/jobs with cancel/retry).

**Gaps the phases must close:**

1. **No ingestion entry in the UI** — the server route exists; nothing calls it. No upload path at
   all (route or UI). (A2)
2. **`knowledge_review_proposed` is write-only** — no approval queue, no apply consumer, no call to
   the producer's review endpoint. A saved event currently masquerades as a workflow. (A2)
3. **Knowledge tools are reachable by no agent.** The researcher's contract grants five tools;
   `ResearcherAnt.Execute` hard-dispatches a fixed set and never projects tool schemas; the only
   `ToolCallingLoop` call site (`POST /agent/run`) never enters a knowledge scope. (A3)
4. **`IncludeRelationships` accepted and ignored** at every layer; `KnowledgeContext.Relationships`
   is always empty. (A3)
5. **No knowledge in planning or learning** — `FormatRecent/RelevantMemory` and the candidate path
   never touch FORAGER. (A4/A5)
6. **`PackagePath` dead + hazardous** (skips validation, no provider behind it). (A1/A3)
7. **Project→FORAGER-project map is file-only** (`knowledge_project_map` in config.json); no
   runtime pairing/linking surface. (A1)
8. **No engine lifecycle management** — ANTHILL assumes an already-running FORAGER. (A1)
9. **Docs/code contradiction (corrected in this release):** five docs, three in-code comments and
   the config catalog note all said knowledge tools are *not registered* while disabled; the module
   deliberately registers-and-refuses (pinned by
   `WithKnowledgeDisabled_TheToolsRegisterAndRefuseRatherThanBeingAbsent`). The behavior is the
   deliberate one — role readiness must not depend on a feature flag — so the documentation was
   corrected rather than the behavior. Also corrected: the documented-but-nonexistent
   `ForagerPackageKnowledgeProvider`/`ForagerHttpKnowledgeProvider` names, the `source_set`
   artifact claim, the `ExternalUnavailable` failure-class name, and the §4.2 route spellings.

## Producer-side requirements (handoff to FORAGER; live gates pending on them)

| # | Requirement | Why | Until then |
|---|---|---|---|
| P1 | A capability endpoint (or `/api/settings` extension) declaring API surface + `anthill_package_version` + feature flags | Consumers currently infer capability from an incomplete OpenAPI doc | Pin by version window; hand-written client |
| P2 | A durable, scoped change feed with cursors (or webhooks + replay) | No push/replay primitive exists; A3's push gate stays **pending** | Polling reconciliation against jobs/exports + package delivery |
| P3 | Producer-side authentication consuming the Bearer token ANTHILL already sends | No auth of any kind today | Loopback-managed engine; authenticating proxy for remote |
| P4 | A checksum for the export **zip itself** (manifest covers only inner files) + a portable (non-standalone) 0.1.4+ artifact | Package integrity ends at the zip boundary; only the standalone artifact exists | Verify inner-file sha256s after unzip; ship standalone |
| P5 | `openapi.json` completeness (7 missing paths, 1 wrong status, 2 missing query params) | Codegen and doc drift | Hand-written client remains |
| P6 | Idempotency keys on export/process POSTs | Consumer retries can double-export | Consumer-side dedupe via receipts (A3) |

## Version note

FORAGER's `FORAGER_VERSION` and `package.json` version are two unenforced literals, and
`build-info.json` inside the artifact carries a third statement of it. The consumer trusts
`/api/ready` at runtime and the artifact digest at install time, never the source literals.
