# ADR-005: Mission Workspace Manager

**Status:** Accepted — implementation targeted at v3.5.0 (was v3.3.0)
**Date:** 2026-07-27
**Baseline:** v3.0.0
**Roadmap:** V3 ROADMAP § v3.5.0 · North Star §7 Stage 4 · Doctrine rule 8

---

## 1. Context

ANTHILL's coder proposes patches; it does not apply them. Application happens later, through the
approval pipeline, against the **active checkout** — the same working tree the running installation
was built from. The v2.11.1 sandbox gate added a disposable sandbox for the coder's iterate-and-build
loop, but the live workspace remains the target for anything retained.

This blocks the V3 mission outright. A mission cannot inspect, edit, test, diagnose, repair, and
retest if every edit lands in the tree that is also the operator's working copy. It also makes
"reject the result" expensive: rejection must undo, rather than simply discard.

Doctrine rule 8 states it plainly: all writes occur in a mission workspace; the active checkout is
never an agent scratchpad.

## 2. Decision

Introduce `MissionWorkspaceManager` owning a per-mission, disposable, recoverable workspace.

```
workspace  id, mission_id, kind (worktree|clone|operator_dir), root_path,
           repository_fingerprint, base_revision, branch, adapter_versions,
           state, cleanup_policy, created_at, retained_at
states     requested -> preparing -> ready -> active -> checkpointed
                     -> (retained | rejected) -> cleanup_pending -> cleaned
                     -> orphaned
```

Paired with a `WorkspaceCapabilityManifest` that detects project type and declares the safe
build/test/format commands for it — .NET and Node adapters ship in v3.5.0.

Rules:

- **Git worktree by default.** Cheap, shares the object store, trivially disposable, and carries a
  real base revision.
- **Scoped tools only.** Read, search, edit, diff, change-set. No unrestricted shell or file write.
  Coder and Scribe write paths are confined to the workspace root, enforced, not conventional.
- **Verification commands come from the manifest.** Never from model invention. A model may not
  propose the command that decides whether its own work passed.
- **Checkpoint and resume.** A workspace survives process restart with enough state to continue or
  to be explained.
- **Cleanup cannot delete a retained workspace.** Operator retention is a hard stop.

## 3. Consequences

**Accepted costs.** Disk usage per concurrent mission, and a genuine lifecycle to get right —
orphaned-workspace reconciliation is a new failure mode. Accepted because the alternative is agents
writing to the live tree, which is not acceptable at any price.

**Explicitly rejected: keeping the proposal-only flow.** Patch proposals remain as the approval
artifact, but the coder must be able to iterate against real build output, and that requires a place
to write. Proposal-only is why the coder cannot currently fix its own compile error.

**Explicitly rejected: one shared sandbox reused across missions.** Cheaper, and it reintroduces
cross-mission contamination — the same class of defect as the shared `Planner` field found in
v2.26.0.

## 4. Verification

- A code mission cannot modify the active checkout through any agent path (enforced, tested).
- Every change is attributable to one workspace and one base revision.
- Workspace recovery after restart is tested.
- Cleanup cannot delete an operator-retained workspace.
- Rejecting retention leaves the active repository byte-identical.
