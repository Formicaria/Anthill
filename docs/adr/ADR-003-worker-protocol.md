# ADR-003: Durable Worker and Attempt Protocol

**Status:** Accepted — implementation targeted at v3.8.0 (was v3.4.0)
**Date:** 2026-07-27
**Baseline:** v3.0.0
**Roadmap:** V3 ROADMAP § v3.8.0 · North Star §6.2 (Execution Plane)

---

## 1. Context

Task execution today is in-process and largely in-memory. `ApiJobRegistry` has durable job rows with
claims, leases, and heartbeats — good — but task-level execution beneath a mission does not: tasks
are `Task.Run` futures held in a dictionary, and their attempt history is a mutable `AttemptCount`
on the task row rather than a record of what each attempt actually did.

v2.26.0 added a bounded drain and a no-non-terminal-task-at-finalisation invariant, which closed the
"terminal mission containing running tasks" hole. It did not make execution *reclaimable*: a crash
mid-task still loses the attempt's inputs, model route, tool versions, and evidence.

## 2. Decision

Define a worker protocol and a durable attempt record, keeping the local implementation but removing
the assumption that execution is local.

```
worker         id, capabilities, lease expiry, heartbeat, availability
task_attempts  attempt_no, task_id, worker_id, inputs_hash, model_route,
               tool_versions, environment, started, finished, duration,
               outcome_code, failure, evidence_ids, cancellation_reason
```

Rules:

- **Claims are atomic and idempotent.** Two workers cannot claim the same non-parallel task; a
  repeated claim by the same worker is a no-op, not a second attempt.
- **Every retry is a distinct attempt row** with its own durable reason. `AttemptCount` becomes a
  projection of attempt rows, not the source of truth.
- **Startup reconciles.** Queued, claimed, running, waiting, and orphaned tasks are resolved against
  lease expiry at boot, not left to time out silently.
- **Redelivery is classified per side-effect.** A read-only task is freely redeliverable; a task that
  changed external state is not redelivered without an idempotency guarantee or explicit operator
  action.
- **The protocol is separable from the transport.** Local execution implements the same interface a
  future remote worker would. This ADR does NOT introduce distribution; it removes the design
  assumption that would block it.

## 3. Consequences

**Accepted costs.** A new table and a write on every attempt boundary. Mitigated by v3.8.0's
transactional batching of task transitions.

**Explicitly rejected: distributing now.** The North Star says a distributed colony is not V3, only
that the interface must not prevent one. Building remote workers before the local protocol is proven
would be exactly the "framework before intelligence" inversion the doctrine warns against.

**Explicitly rejected: reusing `ApiJobRegistry` for tasks.** Missions and tasks have different
lifetimes, different cancellation semantics, and different redelivery rules. One queue serving both
would force the stricter rules onto the looser case.

## 4. Verification

- No accepted task is silently lost after crash or restart.
- Expired work is reclaimed without duplicate retained side effects.
- Two workers cannot claim the same non-parallel task (concurrency test).
- Fault tests cover crash before execution, during model call, during tool call, after change,
  during verification, and during cleanup.
