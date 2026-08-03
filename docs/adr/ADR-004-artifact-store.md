# ADR-004: Artifact and Evidence Store

**Status:** Accepted — implementation targeted at v3.9.0 (was v3.5.0)
**Date:** 2026-07-27
**Baseline:** v3.0.0
**Roadmap:** V3 ROADMAP § v3.9.0 · North Star §6.5 (Artifact and Evidence Plane)

---

## 1. Context

Ants collaborate today by passing prose. A task's output is a narrative string; the next ant
receives a "context packet" assembled by truncating prior narratives. Everything structured that an
ant produces — artifacts, evidence, metrics, handoffs — is serialised into an event's metadata JSON
and is, in practice, write-only.

The costs are concrete and already paid:

- v2.24.0's `EvidenceFollowUps` must parse the verifier's prose for "Missing Steps:" to find
  findings, and needed a known-section-header list to stop reading "Risk Notes: none" as a finding.
- v2.26.0's coder classification parses the coder's own JSON out of its narrative.
- Context assembly is bounded by character truncation, so what a downstream ant sees depends on how
  verbose an upstream ant was.

Prose is a lossy encoding of structure that was already known at the point of production.

## 2. Decision

Introduce immutable, content-addressed artifact and evidence stores. Substantive work moves as
artifact references; messages carry control-plane notices only.

```
artifact   id, schema, schema_version, producer_role, mission_id, task_id,
           workspace_id, source_artifact_ids[], content_hash, created_at,
           visibility, payload
evidence   id, kind, deterministic, passed, artifact_ids[], detail, created_at
```

Schemas defined in v3.9.0: repository map, file set, UI map, change plan, patch set, test report,
security review, failure diagnosis, verification bundle, operator summary, release notes, memory
candidate.

Rules:

- **Immutable and hashed.** An artifact is never edited; a revision is a new artifact citing the old
  one as a source. The hash detects stale or mutated input.
- **Provenance is mandatory.** Every artifact names its producer, its mission and task, and the
  artifacts it consumed. The dependency graph is derivable from that alone.
- **The Context Compiler selects; it does not concatenate.** Each task receives bounded excerpts and
  artifact references chosen for its declared inputs — reproducibly, so replaying an attempt
  reconstructs the same package.
- **Visibility is a first-class field.** Secret redaction happens at the store boundary, not at each
  render site.

## 3. Consequences

**Accepted costs.** Every ant's output path changes, and the store is a new persistence surface.
This is the largest behavioural change in V3 and is deliberately sequenced after the universal ant
protocol (v3.2.0), which is what makes "produce an artifact" expressible at all.

**Explicitly rejected: keeping prose as the interchange with artifacts alongside.** Two channels
means two truths, and the prose one wins whenever a model is more fluent than a schema. Narrative
remains — for the operator — but carries no control meaning, exactly as v2.19.0 established for task
outcomes.

**Explicitly rejected: mutable artifacts.** "Update the change plan in place" destroys the ability to
ask what a decision was based on at the time it was made.

## 4. Verification

- Every task input and output is traceable through artifact ids.
- Replaying an attempt reconstructs the context package exactly.
- No mission requires an unbounded transcript to continue.
- Artifact hashes detect mutation or stale input.
- The API can answer why an artifact exists, who produced it, and what consumed it.
