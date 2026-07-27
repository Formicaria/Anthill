# Adaptive Mission Runtime — Staged Status

**Shipping releases:** v2.19.0 (part 1) + v2.20.0 (part 2 — complete)
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
| 7 | Derived-learning migration at the v2.19.0 boundary | **Shipped in v2.20.0** |

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

## 5. Part 2 — delivered in v2.20.0

### 5.1 Stage 7: the derived-learning migration — SHIPPED

`SqliteMemory.ApplyLearningReset` (`SqliteMemory.LearningReset.cs`), run automatically at
construction on every entry point. Delivered against every constraint:

- **Reset:** objective `success_ema` → NULL (snapshotted to metadata as `legacy_success_ema`);
  trail strength → neutral 0.5 with success counters restarted; no consecutive-success counter
  existed to reset (`objectives` carries only `consecutive_failures`, which is failure history).
- **Untouched, proven by test:** missions, tasks, events, autonomy runs, approvals, patches,
  sources, agent messages; `failure_count` and `consecutive_failures` in place. Persisted skill
  demotion was moot: `SkillRegistry` is in-memory only — nothing persisted to demote.
- **Marked, not deleted:** pre-boundary trails carry `legacy = 1` plus a metadata snapshot of
  their pre-reset strength and counts. Retained for reporting, protected from `PrunePheromones`
  at any threshold, excluded from `GetTopPheromoneTrails` (the Strategist planning read) until
  they record a post-reset success — then re-admitted on evidence earned under the corrected rule.
- **Machinery:** durable meta marker (`learning_reset_v2_19`) for exactly-once; WAL-safe online
  SQLite backup **before** any mutation, path recorded; counts in meta + a `learning_reset` audit
  event; `LearningResetTests` proves idempotency, backup-before-mutation (by reading the pre-reset
  values back out of the backup), raw-history preservation, and the legacy semantics.
- **The boundary property:** fresh databases get the marker with nothing mutated and no backup,
  so post-v2.19 learning is never reset — it is a boundary, not a recurring purge.

**Known imprecision, accepted:** databases that ran v2.19.0 before upgrading accumulate up to a
release-cycle of correctly-graded trail updates mixed into pre-boundary rows; the reset marks
those rows legacy anyway. Over-marking is the fail-closed direction — verified work re-earns its
standing with one post-reset success.

### 5.2 Surface the reset — SHIPPED

`/memory/explorer` carries a `learning_reset` block (date + note); trail rows expose the `legacy`
flag; `FormatPheromoneContext` (the Strategist's own context) is headed by the reset date.

### 5.3 Carried-forward items — resolved or explicitly kept

- **`memory_candidate` consumer: wired.** `MemoryCandidateIngest.Extract` parses candidates from
  the archivist's structured result; the Queen ingests each as a durable `memory_candidate` event
  with provenance on the archivist completion path. Stored, never certified, never fed to
  planning — `auto_promote` is recorded, not acted on. A guard test pins the call site itself.
- **`MissionVerification` remains the interim gate** — by design. It answers "did verification run
  and pass", not "was the mission objective met". Objective-level verification is a later phase.
- **Specialist rollout gates stay closed.** Unchanged, per the standing constraint.
- **Still open for a future release:** `HandoffGate.Evaluate` (zero production call sites) and
  `VerificationPolicy` (used only by the shadow simulator) remain unwired — same pattern, not in
  the adaptive-runtime scope.

---

## 6. Recurring failure mode to keep watching

Four separate defects in this codebase have shared one shape: **tested code with no call site.**

- v2.14.12 — functions referenced every frame but never defined
- v2.14.14 — `SanitizeInto` implemented and tested, never called
- v2.18.2 — an endpoint that never returned the field its own client read
- v2.19.0 audit — `HandoffGate.Evaluate` with zero production call sites

A test that exercises a function proves the function works. It does not prove anything calls it.
When adding a capability, assert the **call site**, not only the implementation.
