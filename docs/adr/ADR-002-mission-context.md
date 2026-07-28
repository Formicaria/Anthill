# ADR-002: MissionContext — Immutable Per-Mission State

**Status:** Accepted — implementation targeted at v3.1.0
**Date:** 2026-07-27
**Baseline:** v3.0.0
**Roadmap:** V3 ROADMAP § v3.1.0 · North Star §7 Stage 1 (Intake and Constraints)

---

## 1. Context

A mission's governing facts are currently scattered and re-derived at each point of use:

- Constraints are re-parsed from the goal string by `MissionConstraints.Parse(mission.Goal)` at
  eight separate sites. Each parse is an opportunity to disagree.
- Deadlines live in a `CancellationTokenSource` plus a wall-clock comparison in the dispatch loop.
- Capability grants are read from mutable statics at the moment of use, not resolved at admission.
- The environment fingerprint is recomputed on demand.
- There is no correlation identity spanning a mission's model calls, tool calls, and artifacts.

The v2.26.0 hardening showed what this costs: the deliverable-verification layer had to re-parse
constraints to answer a question the mission should already have known about itself.

## 2. Decision

Create an immutable `MissionContext`, constructed once at intake (North Star Stage 1) and passed
explicitly to everything that needs it.

```
MissionContext
  MissionId, CorrelationId
  Goal
  Constraints          resolved ONCE at intake, never re-parsed
  CapabilityGrants     what this mission may do, resolved from the RuntimeProfile
  Workspace            identity + base revision (populated at v3.3.0; null before)
  Deadline             absolute UTC instant, not a duration
  Budgets              model calls, tool calls, elapsed, repair attempts, context size
  EnvironmentFingerprint
  CreatedAt
```

Rules:

- **Resolved once.** `MissionConstraints.Parse` is called exactly once per mission, at intake. Every
  later reader consumes `context.Constraints`.
- **Immutable.** A record with init-only members. A mission's boundaries cannot widen mid-flight;
  the adaptive controller may narrow what it attempts, never what it is permitted.
- **Explicit.** Passed as a parameter. Never ambient, never a static, never thread-local. The
  `ModelCallScope` ambient token remains as the cancellation mechanism only.
- **Absolute deadline.** An instant, not a duration, so restart and resume compare against the same
  wall-clock boundary the original run did.

## 3. Consequences

**Accepted costs.** Wide signature churn: most mission-path methods gain a parameter. That churn is
the point — an explicit parameter is a reviewable dependency, and the compiler enforces it where a
static read cannot be enforced at all.

**Explicitly rejected: an ambient/AsyncLocal MissionContext.** It would be a smaller diff and would
reproduce the exact defect being removed. Ambient state is how six call sites came to answer the
same question differently.

**Explicitly rejected: a mutable context.** "Just update the budget on the context" is how a bound
stops bounding. Budget consumption is tracked in execution state; the context holds the ceiling.

## 4. Verification

- `MissionConstraints.Parse` appears exactly once on the mission path (guard test).
- A mission's constraints, deadline, and grants are identical at intake and at finalisation.
- Restart resumes against the same absolute deadline rather than restarting the clock.
- The canonical `MissionEvaluation` reads its deliverable requirement from the context, not from a
  re-parse.
