# Adaptive Mission Runtime — Staged Status

**Shipping release:** v2.19.0 (part 1)
**Next release:** v2.20.0 (part 2)
**Design record:** `docs/ADR-ADAPTIVE-MISSION-RUNTIME.md`

This document states exactly where the adaptive-runtime work stands: what shipped, what did not,
and what must be true before part 2 is done. It exists so the next session can resume without
re-deriving the plan.

---

## 1. The defect this work exists to close

An ant reported an outcome as prose. Nothing parsed that prose. The orchestrator inferred success
from the mere fact that the ant returned a string.

The chain, end to end:

1. A specialist built a full structured result — status, handoffs, evidence — and then discarded it
   through a compatibility adapter that flattened it into text (`UI_MAP_JSON`).
2. `RunSingleTask` marked the task **Complete** unless the ant threw, timed out, or was denied
   before execution. A returned `failed_retryable` was therefore recorded as a completed task.
3. Mission grading read completed tasks and produced **Complete** or **Partial**.
4. `ColonyDirector` treated `partial` as success.
5. Success satisfied the auto-apply precondition.

**A failing agent could drive an automatic code change.** Separately, the same rule fed objective
EMA, pheromone reinforcement, and skill confidence — so the learning system was being trained on
outcomes that were never verified.

---

## 2. What shipped in v2.19.0

| Stage | Scope | State |
|---|---|---|
| 1 | `AntExecutionResult` foundation — `AntMetrics`, `SucceededWithWarnings`, `Skipped`, `BaseAnt.Execute` | **Shipped** |
| 2 | `MissionOutcome.IsPositiveSuccess` — only `completed_verified` is a positive success | **Shipped** |
| 3 | `TaskOutcomeMapper` + `Queen.RunSingleTask` wiring — the ant's declared status decides the task's fate | **Shipped** |
| 4 | All six specialists migrated; the `Compat` adapter **deleted** | **Shipped** |
| 5a | `VerifierAnt` declares its verdict via `VerificationVerdict` | **Shipped** |
| 5b | Researcher, Web, File, Coder, Builder migrations | **Deliberately not done — see §4** |
| 6 | `MissionVerification` requires a real PASS verdict, not mere completion | **Shipped** |
| 7 | Derived-learning migration at the v2.19.0 boundary | **Not shipped — part 2** |

### The three behavioural changes that matter

**A specialist's declared status now decides the task outcome.** `TaskOutcomeMapper.Map` completes
only on `succeeded` / `succeeded_with_warnings`; unknown or null status fails closed. Non-completing
outcomes route through `ApplyNonCompletingOutcome`, and the scheduler — which owns the retry budget —
decides whether a retryable failure is retried.

**A mission is verified only if its verifier said so.** Previously the gate asked whether a
verification task had *completed*. A verifier that ran to completion and reported
"Verification Failed" satisfied it. `VerificationVerdict` now parses the verdict and
`MissionVerification` requires a pass on both the object and row paths.

**Partial missions reinforce nothing.** `UpdateMissionPheromones` applies a positive delta only for
`completed_verified`; `completed_unverified` and `partial` apply **0.0** — not punished, because
partial work is genuinely ambiguous evidence, but never reinforced.

### Expect the apparent success rate to drop

This is the intended metric correction, not a regression. Missions that previously graded
"successful" on structural completion alone will now grade `completed_unverified` or `partial`.
The prior number was measuring "the ant returned a string", not "the work was verified".

---

## 3. Design decisions worth not re-litigating

**A failed verification does not fail the verification task.** Marking it failed would have been
less code — the row gate already disqualifies on any failure. It was rejected because
`ApplyNonCompletingOutcome` sets `task.Result = decision.Reason`, which replaces the verifier's
full verdict, reasoning, missing steps and risk notes with a one-line string. That destroys the
operator's explanation. The verification completes and carries its verdict as evidence and
warnings; the **gate** refuses to call the mission verified.

**The verdict rule applies to the `verifier` role only.** `tester` and `soldier` are verification
steps but do not speak the verdict vocabulary. Requiring a verdict from them would parse `Unknown`
and fail every mission they touch.

**Ambiguous verdicts fail closed.** The verifier prompt lists all three options on one line. A model
echoing that line must not be read as whichever verdict the parser checks first — that would let a
coin flip decide whether generated code may auto-apply. Multiple distinct verdicts parse to
`Unknown`, which is not a pass.

**A blocking finding leaves `succeeded_with_warnings`.** For Soldier and Scribe, the review itself
succeeded even though it did not pass. The verdict lives in the artifact, evidence, and warnings.

---

## 4. What was deliberately not done

**Researcher, Web, File, Coder, Builder were not migrated to explicit `Execute` overrides.** They
produce prose or code as their artifact, and `BaseAnt.Execute`'s default wrapper already declares
their outcomes correctly — including recognising an in-band `ERROR:` from the model router as a
*retryable* provider failure. Verifier was the only core ant whose **text carried a control
decision**, which is why it was migrated and the others were not. Migrating the rest would be churn
with regression risk and no behaviour gained. Revisit only when one of them needs to emit structured
handoffs or artifacts.

---

## 5. Part 2 — what v2.20.0 must deliver

### 5.1 Stage 7: the derived-learning migration (the main body of work)

Learning state accumulated under the defective completion rule is still active. It must be reset —
narrowly, once, and reversibly.

**Reset only these, all derived under the old rule:**

- objective success EMA → neutral/unset
- active pheromone strengths derived from mission success
- consecutive-success and confidence counters that accepted partial or unverified outcomes

**Never touch:**

- historical missions, autonomy runs, events, evidence, operator audit records — **preserve all raw
  history**
- failure history, policy violations, negative memory, approval history, verified evidence bundles
- skills whose success records can be proven from promotable verification bundles — do not demote

**Mark, do not delete.** Pre-v2.19.0 learning data that cannot be reconstructed is marked
`legacy_unverified`: retained for reporting, excluded from active planning, routing, prioritisation,
certification, and autonomy decisions.

**The migration must have:**

- a durable version marker so it runs exactly once
- a backup taken *before* any mutation
- before/after counts recorded
- an audit event
- tests proving it is idempotent
- tests proving raw history is untouched

### 5.2 Surface the reset

The UI and reports must visibly identify the learning reset date, so a rate measured after the
boundary is never silently compared against one measured before it.

### 5.3 Open items carried forward

- **`memory_candidate` artifacts have no consumer.** `ArchivistAnt` emits memory candidates and
  declares the artifact type in its contract, but nothing ingests them. Same "tested code with no
  call site" pattern as `HandoffGate.Evaluate` (zero production call sites) and `VerificationPolicy`
  (used only by the shadow simulator). Decide in part 2 whether to wire or remove.
- **`MissionVerification` remains the interim gate.** It answers "did verification run and pass",
  not "was the mission's actual objective met".
- **Specialist rollout gates stay closed.** No additional specialists are activated globally; each
  keeps its existing per-role gate.

---

## 6. Recurring failure mode to keep watching

Four separate defects in this codebase have shared one shape: **tested code with no call site.**

- v2.14.12 — functions referenced every frame but never defined
- v2.14.14 — `SanitizeInto` implemented and tested, never called
- v2.18.2 — an endpoint that never returned the field its own client read
- v2.19.0 audit — `HandoffGate.Evaluate` with zero production call sites

A test that exercises a function proves the function works. It does not prove anything calls it.
When adding a capability, assert the **call site**, not only the implementation.
