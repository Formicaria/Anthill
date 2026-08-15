# ANTHILL — THE PLAN

**The single forward document.** What is done, what is left, and the order to do it in.
`AUTONOMY-10.md` folded into this file; role mechanics live in
[`ANT_EXECUTION.md`](ANT_EXECUTION.md); the qualification protocol lives in
[`QUALIFICATION.md`](QUALIFICATION.md).

Shipping release: **v0.3.8.57**.

---

## 1. Where the colony measurably is

**Structurally complete, deterministically qualified across most of its declared scenarios, and
never once run against a real model.** That last clause is the whole shape of what remains.

Done and load-bearing:

- Twelve roles, contracted, gated, each with a real production trigger.
- Patch integrity: `add` means create, destructive applies require a base hash, a patch set applies
  as a unit or not at all, and one decision function (`PatchApply.Compute`) answers for every applier.
- The typed artifact channel: declared task inputs, a consumption ledger recording what each role
  actually read and at which hash, schema validation at the write and read boundaries, provenance
  carrying the provider and model that served each call.
- Structural enforcement: a UI change cannot dispatch without a valid `ui_map`; verification is
  policy-inserted and fails closed; the repair bound reads typed signatures; the scribe cannot
  certify unverified work; `MissionReconstruction` replays a mission from artifact IDs.
- Chat talks to the colony. An autonomous coding agent may not serve the conversation route; the
  colony dispatches one as a tool, inside a mission that reviews and verifies its work.

Not done, and the reason each matters, is the ordered plan below.

---

## 2. The plan, in order

### 1 — Reconcile the documentation

**This change.** Three documents disagreed with the code they describe, in ways that would have
misled anyone planning the work below: the verifier was recorded as planner-selectable in three
places after it became policy-inserted; the tester was recorded as running on the unpatched tree
after `MissionRevisionRegistry` began keeping the revision alive; `delete`/`rename` were recorded as
unimplemented after `DestinationPath` and `ComputeDelete` shipped; and `add`-over-existing was still
described as overwriting after it became a typed refusal.

A plan built on a wrong map produces work aimed at the wrong place. Everything after this depends on
this being right first.

### 2 — Make evidence identity mandatory for promotion

Auto-apply already refuses a patch set whose evidence judges a different revision. The **canonical
evaluator does not** — `HasDeterministicPass` was deliberately left unchanged, so correct test
results from the wrong tree can still reach `completed_verified` outside the auto-apply path.

- The canonical evaluator consumes `Evidence.Judges()`.
- Any mission with a materialized patch requires revision-bound evidence.
- Final patch-set and tree hashes must match; earlier repair generations and unpatched-workspace
  evidence are refused.
- Legacy unbound evidence stays readable for history and cannot promote new work.

Small, and it closes the last path by which a true statement about the wrong bytes becomes a verified
mission.

### 3 — Close the remaining deterministic qualification scenarios

`QualificationMatrixTests` is the ledger; four entries are short.

- **Qualification scenario 3** — a documentation patch driven through the Queen: goal → planner →
  docs proposal → typed `docs_patch_set` → materialization → verification → apply → evaluation.
- **Qualification scenario 15** — one composed mission reaching all twelve roles through their
  **production triggers**. No role invoked to satisfy a count.
- **Scenario 7** — the soldier block inside a full lifecycle: coder proposes, policy inserts the
  soldier, the block prevents verification, application and positive learning, and model text cannot
  argue it away.
- **Scenario 17** — kill the process mid-apply and restart: the incomplete transaction is detected,
  the tree is restored or resumed, and nothing is applied, approved or finalized twice.
- Plus the composed **UI-patch lifecycle**, which scenario 5 covers only in halves.

The `ScriptedColony` harness exists. These are script books, not new machinery.

### 4 — Cancellation and timeout proof for every role

The graduation record's cancellation column is empty for all twelve roles. System-level tests prove
the model call observes cancellation and that process-launching sites kill their trees; neither says
anything about a role.

Per role: cancellation before dispatch, during generation, during a tool call, and while waiting on a
dependency. Correct terminal state, no retry or handoff after operator cancellation, no orphan
process, no positive memory or reputation, clean restart.

Highest risk first — tester, file, researcher, web, ui_cartographer, scribe, and the coder on an
agent CLI. Those hold tools, sockets and subprocesses open.

### 5 — The first live qualification run

Everything above is proved against a scripted model whose answers were written to fit the runtime.
`QUALIFICATION.md` §3 holds the protocol and the fields a run must record.

Deliberately **after** items 3 and 4: without a complete deterministic baseline, a live failure
cannot be attributed to the model rather than to a hole already present.

Cover Ollama, an OpenAI-compatible provider, and Anthropic or a supported agent CLI; ideally one
small local model and one strong cloud model. Record provider and model version, tokens, cost,
durations, failure classes, which trigger reached each role, artifacts produced and consumed, and
whether `MissionReconstruction` can replay the result.

### 6 — Authoritative task inputs everywhere

`Task.InputArtifactIds` is authoritative when populated, and exactly one producer fills it. Every
other task still falls back to the mission-wide block.

The scheduler should derive inputs from dependencies, required schema types, the current revision,
artifact generation and policy relationships — never because an artifact merely exists somewhere in
the same mission.

### 7 — Finish replacing prose as the primary channel

The typed channel is load-bearing but not sole. The builder still produces free prose and
`Task.Result` remains central.

Not by labelling arbitrary prose. Change the builder's prompt to request a stable structure, define
an honest `mission_summary` carrying the operator-facing text as a field alongside verified outcome,
deliverables, evidence references, limitations and unresolved items, and have the answer assembler
consume it. The user-facing answer then traces to the verified record instead of being an independent
last-minute narrative.

### 8 — Complete artifact provenance

Recorded as gaps rather than fields, because nothing produces them:

- **Assumptions** — statement, source, confidence, whether verified, what would invalidate it.
  Exposes reasoning built on an unverified premise.
- **Retention** — class, expiry or review date, holds, supersession links, and a pruner that obeys
  the classification. A retention label nothing reads is a compliance claim the system does not keep.
- **Validation status** — queryable rather than reinterpreted from warning text: valid, invalid,
  unfixed schema, hash mismatch, unsupported version, missing source, superseded.

### 9 — Research citation quality

Qualification scenario 1 proves a brief parses. It does not prove the citations are any good.

Every factual claim links to source IDs that resolve to real `source_set` entries; URLs and retrieval
times persist; coverage is scored; unsupported claims and contradictory sources are surfaced; source
confidence affects synthesis; a malformed citation prevents a "fully verified research" status.

### 10 — Complete the per-role graduation record

Once the columns are honestly filled, delete the test that asserts gaps must exist and replace it
with one requiring every cell. Do not graduate a role because a test file mentions its name. Prefer
structured qualification records over a hand-authored citation table.

Then "Ready" means both configured now and proven.

### 11 — Reputation-aware routing

The colony calculates reputation and does not consume it: `ModelRouter.GetRoute()` is
configuration-driven.

Task-specific reputation, provider quality history, tool reliability, cost and latency scoring,
minimum observation thresholds, recency and decay, safe exploration, a human-readable explanation of
why a route was chosen, and a policy ceiling reputation cannot override. Demonstrated on a controlled
benchmark.

Until this lands, the colony stores experience without becoming better from it.

### 12 — Semantic and procedural memory

Episodes, artifacts, trails and candidates exist; the promotion layer does not. Combine verified
episodes into stable facts and reusable procedures with prerequisites, environments and confidence.
Expire stale knowledge, supersede contradicted knowledge, invalidate procedures when their
dependencies change, keep synthetic and imported material out of verified memory, and let an operator
inspect, export, invalidate and purge.

### 13 — The autonomous coding and PR lifecycle

The colony produces and validates a safer patch set; it cannot yet carry a task from issue to
reviewable PR. Branch ownership, base-commit policy, coherent commits, push authorization, idempotent
push, PR creation, durable external-action receipts, CI status ingestion and log diagnosis, review
feedback ingestion, bounded follow-up patches, requalification, base movement, conflict refusal,
protected-branch enforcement, approval-controlled merge, and restart without duplicate commits or PRs.

### 14 — Connectors, self-improvement, production qualification

The long horizon.

- **Connectors** — SDK, credential handles, OAuth scopes, read/write separation, typed external
  actions, risk tiers, idempotency keys, receipts, post-action verification, compensating rollback,
  events, webhooks and schedules.
- **Safe self-improvement** — mine verified failures, generate replay cases, propose isolated
  improvements, evaluate on held-out benchmarks, canary, detect reward hacking, roll back
  automatically, require human approval for any change to the colony's own authority.
- **Production qualification** — thirty-day soak, fault injection across provider, network, database
  and disk, external security review, threat model, SBOM and signed builds, backup and restore
  drills, migration and rollback tests, SLOs, alerting and runbooks.

### 15 — Technical cleanup

Carried, not forgotten: event-stream dropped-event accounting and the nine `/events/json` pollers
that depend on it; the ~166 `AnthillRuntime` statics; the no-UI build-and-boot CI gate; unique TRX
files per test project; fully async provider and ant execution; a genuinely new module with zero core
changes; narrow store interfaces only where a module needs one; VRAM-aware local scheduling; the
fixed ten-mission dedupe window; dashboard legacy-workspace deletion; the full
Windows/Linux/Docker/LXC QA checklist; `AntMetrics.InputChars`; and a re-audit of executable UI
interpolation attributes.

---

## 3. What "done" would mean

After items 1–5: *the mission runner is structurally complete, deterministically qualified across its
declared scenarios, and demonstrated against at least one real model.*

That is not a finished autonomous coding agent. Reputation routing, mature memory and the end-to-end
PR lifecycle are what stand between that claim and this one.

---

## 5. Acceptance gates

Non-negotiable. The colony is not a twelve-role colony until all of these pass.

1. ◻ All twelve roles report Ready under the full profile
2. ◻ Every enabled role has a handler, contract, real production trigger and typed output
3. ✅ A compile-breaking proposed change fails when built in the patched mission workspace *(v3.8.23)*
4. ✅ That failed patch cannot become `completed_verified` *(v3.8.22)* — retention and learning still to close
5. ✅ A Soldier block cannot be overridden by model text *(v3.8.22)*
6. ✅ Tester failure triggers exactly one bounded Medic repair and a mandatory retest *(v0.3.8.57 — the bound reads typed `failure_context` signatures, counted by distinct task; the narrative scan survives only where there is no artifact store)*
7. ✅ A UI change cannot reach Coder without a valid `ui_map` *(v0.3.8.57 — `UiChangeGate`, enforced at dispatch on a detector shared with the planner; valid means hash-intact and schema-conforming)*
8. ✅ Scribe and Archivist cannot act positively on unverified work *(v0.3.8.57 — the scribe refuses a `verified_change_summary` when verification is not satisfied; positive procedural memory comes only from `completed_verified`)*
9. ✅ Archivist runs only after the persisted canonical evaluation exists *(v3.8.26 / v0.3.8.41, pinned at v0.3.8.57 — outside the task graph, ledger-claimed so a replayed finalization cannot archive twice)*
10. ✅ Replaying artifact IDs reconstructs every role's inputs and evidence *(v0.3.8.57 — `MissionReconstruction`; inputs come from the consumption ledger, and the gaps are the gate)*
11. ✅ No mission ant can dispatch shell, direct file-write, or primary-workspace patch tools *(pinned by `RosterContractTests`)*
12. ✅ Disabled or unavailable roles never receive negative reputation for not running *(v3.8.26)*

Gates 1 and 2 close with plan items 3, 4 and 5 — a role is not Ready in the sense this list means
until its qualification scenario and its graduation record are real.

---

## 6. The record — the shape of the mistakes

Kept because the shape recurs and recognising it is worth more than any individual fix.

**A check that answers a question ADJACENT to the one asked, and passes.** Found fifteen times. The
newest: a graduation record cited two real cancellation test files that prove real things and name no
role, and a qualification index lived in a doc comment where a citation could rot into a deleted file
without anything noticing.

**Declared, and reaching nobody.** `RequiredInputArtifactTypes`, `EvidenceKinds.SchemaValid` and
`Task.InputArtifactIds` were each declared before anything populated them, and each looked exactly
like a working feature for releases. `Evidence.Judges` joined them inside a single release — added
and read by nothing until the same release closed it.

**A declaration that disagrees with the runtime.** The verifier's contract said planner-selectable
for six releases after the runtime guaranteed insertion. `scheduling_mode` is reported by the API and
read by operators, so this was the system stating a guarantee it did not keep.

**Prose as a control channel.** The bound on repair looping was a substring search of a previous
medic's narrative, and task results are truncated — so the bound was weakest exactly where the loop
was longest.

**A diagnostic that breaks what it describes.** The artifact schema check logged a violation through
an event table with a foreign key, turning "this payload is the wrong shape" into "the artifact was
never stored".

**Timeouts that abandon the work.** Five sites called `WaitForExit(ms)`, carried on when it returned
false, and read `ExitCode` — which throws on a live process — so a timeout surfaced as an
ordinary-looking exception while the process kept running.
