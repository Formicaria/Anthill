# ADR-008: The Universal Mission Lifecycle — One Spine for Every Mission Class

**Status:** Accepted — implementation begins at v0.3.8.98, sequenced by `PLAN.md`
**Date:** 2026-08-28
**Baseline:** v0.3.8.97 (`a828dfe`)
**Supersedes nothing.** ADR-002 (MissionContext), ADR-003 (worker protocol) and ADR-004 (artifact
store) are the load-bearing pieces this contract is assembled from; none of them changes.

---

## 1. Context

Anthill's stated purpose has always been broader than code:

> Accept an operator request, determine the requested deliverables and definition of done, identify
> the required capabilities, select the correct specialized ants and workers, execute the work
> through available tools, collect evidence, repair bounded failures, independently verify the
> original objective, return the requested result, and learn only from verified outcomes.

Measured against the code at v0.3.8.97, **only the coding mission class has a complete execution
and verification lifecycle.** This is not an impression; it is visible in three places:

- `ObjectiveVerification.Deliverable` declares exactly two values, `Unknown` and `FileChange`.
  Every non-code goal resolves to `Unknown` and falls through to the interim gate, so "was the
  operator's objective met" is a question the runtime can only answer about files.
- `AntRegistry.ResolveWorker` selects a worker by substring match over `taskType + goal`
  (`text.Contains("mission")` chooses the mission-memory researcher; `text.Contains("safety")`
  chooses the safety verifier). Specialization is decided by vocabulary, not by what the mission
  needs — so a repository audit is routed to the mission-history researcher because the word
  "mission" appears in its task text.
- `ResultAssembler.SelectBestOutputTaskId` returns the last completed builder's raw output as the
  operator's answer. Nothing compares that answer to what was asked, so a mission that answers one
  of three questions is indistinguishable from one that answers all three.

The consequence is recorded in mission `7afd85b2-e4a2-47ef-aa01-e5fa72ff00ca`: two tasks
completed, no checks, no evidence, the requested assessment absent — and the mission structurally
presented as finished. Task completion was measured. Objective completion was not.

Meanwhile the coding lane has genuinely strong machinery — per-project worktrees, revision-bound
evidence, patch custody, policy-inserted Tester and Soldier review, promotion gates, transactional
apply, cancellation. **The error would be to build a second orchestration system beside it.** The
correct move is to generalize the spine that already exists so coding becomes one class travelling
it rather than the only class with a road.

## 2. Decision

One production spine serves every mission class:

```
operator request
  → authoritative mission specification      (persisted once, at intake)
  → capability resolution                    (what is needed → who can do it)
  → admitted task graph                      (compiled, preflighted, covering the specification)
  → execution through tools and adapters     (real actions, real receipts)
  → typed artifacts and evidence             (produced, and recorded as consumed)
  → result assembly                          (the requested deliverables, assembled)
  → objective verification                   (judged against the specification)
  → finalization                             (a truthful outcome code)
  → verified learning                        (only from proven success)
```

### The permanent contract

1. **Coding is one mission class, not the workflow.** Any statement true of missions must be true
   of research, audit, document, data, diagnostic and operational missions as well, or it is a
   statement about the coding lane and must say so.

2. **Success means the operator's requested deliverables were produced and verified**, not that the
   scheduled tasks reached a terminal state. A mission whose graph completes while a requested
   deliverable is missing is an objective failure and must be recorded as one.

3. **Every explicit request becomes a tracked deliverable** with a stable identity, carried from
   intake to the final answer. A request that exists only as a sentence in the goal string is a
   request the runtime cannot be held to.

4. **Required capabilities determine compatible workers.** Worker selection resolves from what the
   mission needs against what each worker declares it can do — not from role-name resemblance and
   not from substring matches on the goal.

5. **Pheromones and learned routes may RANK compatible choices. They may never override
   compatibility, authority, tool availability or evidence requirements.** Prior success is not
   proof of present success. (This rule is already enforced for the tie-break introduced in
   v0.3.8.93; it is stated here as permanent.)

6. **Unsupported work blocks with the exact missing thing named** — capability, information,
   approval, adapter or authority. A model narrating an action it did not perform is the single
   failure this architecture exists to make impossible.

7. **Models may propose structure; deterministic gates enforce it.** A model may propose a plan, a
   classification or a specification. Coverage, compatibility, authority and verification are
   decided by code that fails closed.

8. **Model interpretation is not evidence.** The record must distinguish a model's statement, a
   sourced observation, a tool receipt, a deterministic check, and an operator confirmation. A
   structured model output is still a model output.

9. **Everything the coding lane already guarantees is preserved**: per-project mission worktrees,
   patch custody and revision identity, Tester/Soldier policy insertion, deterministic checks,
   approval and auto-apply gates, cancellation and timeout behaviour, typed artifacts and the
   consumption ledger, provider and model provenance, mission reconstruction, the persisted
   canonical evaluation, and the restriction of positive learning to verified success.

10. **One spine, not two.** No parallel framework, no second ledger, no separate non-code
    orchestrator. New capability extends the existing path or it does not ship.

### Mission classification is multi-dimensional

A single keyword label cannot express the difference between "what is implemented", "what is
running now", and "why is it broken". Classification therefore resolves independent dimensions:

| Dimension | Values |
|---|---|
| `intent` | explain · assess · diagnose · change |
| `targets` | repository · runtime · project · service |
| `freshness` | historical · current · live |
| `authority` | observe · execute checks · modify |
| `deliverables` | the requested outputs, each with a stable id |

The boundary this produces, stated once so later releases inherit it rather than re-litigate it:

- *assess, inventory, explain or report current state* → **system audit**
- *diagnose a symptom or determine root cause* → **troubleshooting**
- *modify or repair* → **consequential execution**, under the existing approval and patch gates

"Is the colony healthy right now?" is an audit when it can be answered from read-only health,
configuration, mission, route and evidence records. "Why is it unhealthy?" and "fix it" are not.

### Authority

Adapters, workers and missions each declare an authority level, and dispatch requires agreement
across the mission specification, operator policy, worker contract and adapter:

`observe → analyze → propose → create reversible → modify → external communication → deploy →
destructive/irreversible`

Confined code execution and every existing approval gate are preserved. Unconfined arbitrary shell
execution is not the universal executor and must not become one.

## 3. Consequences

**What this costs.** Intake becomes a real component with a persisted output rather than a string
passed forward. Worker contracts must declare what they accept and produce. Several current
behaviours that pass today will begin to fail honestly — a mission that answers two of three
questions, a verifier that grades an artifact it never read, an audit with no inspection behind it.
That is the intended effect, and it will make some previously "successful" missions report as
objective failures. Those missions were already failing; only the report changes.

**What this does not license.** It does not license a rewrite. It does not license adding roles to
appear capable. It does not license a release that ships schemas with no call site — the standing
rule is that a type is not complete until the production composition root constructs it, the real
operator path calls it, its output is consumed downstream, its decision changes observable
behaviour, and a composed test fails when the call site is removed.

**Sequencing lives in `PLAN.md`, not here.** This document defines the permanent contract; the plan
defines when each part of it ships and what its exit gate is. If the two disagree, `PLAN.md` is
wrong about status and this document is wrong about nothing — status is not architecture.

## 4. Status of the contract at the time of writing

Nothing in §2 is fully implemented at v0.3.8.97. Item 9's preservation list is real and shipped;
items 1–8 and 10 describe the target the `.98`–`.107` sequence is built to reach. This section
exists so that a reader who finds this ADR alone cannot mistake a decision for a description.
