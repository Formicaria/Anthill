# ANTHILL — THE PLAN

**Where the colony measurably IS.** Shipping release: **v0.3.8.48** — the 3.8 line is CLOSED.
The forward program lives in [`AUTONOMY-10.md`](AUTONOMY-10.md).

> v0.3.8.48: the project-centered restructure — conversations choose their project, a real
> restart-safe scheduler whose runs are conversations, approvals on the conversation itself
> with inline change cards, and seven navigation destinations with every old route aliased.

> v0.3.8.47: real projects (one per conversation, purpose-as-context, optional working
> directory), attachments with drag-and-drop, import and edit-resend, whitespace-tight chat
> bubbles, line-streamed agent stdout, and a desktop tray with a tell-only update check.

> v0.3.8.46: conversation search (server-side, over titles and transcript content), pinned
> conversations (stored, restart-safe, recency-beating), and markdown export with the decision
> log included — the chat items from the maturation directive, each backed by the store. The
> UI gap ledger emptied too: plan preview in chat's gate, the shadow judgment queue with its
> form, the Readiness page (snapshot, attestation, certification, report, introspection), and
> source quality on Memory & Signals. Plus turn timestamps and token accounting, home-grown
> escape-first syntax highlighting, and three escalation bugs found by driving the full
> chat→gate→preview→approve→mission→patch loop live, each fixed with a regression test.

> v0.3.8.45: Chat + Colony became a SPLIT page on the field's verdict — the desktop tester could
> not see the colony (it drew, centred, under the opaque floating conversation panel) and the
> operator ruled "should be a split page". Conversation left, colony right, in-flow, one
> canonical canvas; the frosted-overlay presentation is now forbidden by guard test.

> v0.3.8.44: chat answers ARRIVE as they are produced — a real streaming contract from SDK to
> provider to SSE to console, with abort reaching the model call — and the desktop app's first
> field failure was fixed at all three of its layers (loopback default, a log with the host's
> words, native-library self-extraction) with the Windows release archive now carrying
> `AnthillDesktop.exe` beside the server binary.

> v0.3.8.43 adds the two shapes the product had been promised: **AnthillDesktop**, the colony in a
> native Windows window (one WebView2 over the same in-process API — a window onto the colony,
> never a second one), and the **layered Chat + Colony mode** — the topology behind a frosted
> conversation panel, with Fit view and `prefers-reduced-motion` honored at the render loop.

> v0.3.8.42 is the *UI truthfulness and cohesion* release: the console claims only what the
> backend proves. Chat became the one mission entry (the four competing composers retired, each
> leaving a path behind) and chat turns are ANSWERED, through the same router the roles use; the
> topology opens beside the conversation through the one re-parented canvas; the Monitoring domain
> dissolved into the homes its concepts already had; and the found-by-driving-it defects closed
> (the fabricated role roster, cancelled-as-prose conversation state, double-submitting patch
> mutations, configured-reported-as-connected, fitness graded against a route no call would use).
> The governing audit is `docs/UI-CONTRACT-AUDIT.md`; §2 below still measures v0.3.8.40/41 and is
> superseded only where that document says so.

**v0.3.8.41 changed one thing in the table below.** `roster_profile` now defaults to `full`, so the
twelve mission roles are enabled on a new installation and on any existing one that never touched
the roster (`ConfigSchema` migrates only untouched legacy defaults; explicit choices and
`disabled_roles` survive). Finalization was reordered so the archivist writes its memory candidates
before learning consumes them, and made idempotent per evaluation; the verifier is now bound to the
tester and soldier evidence rather than to whatever the planner had produced at planning time.

**Still true, and the reason §6 stays open:** no live twelve-role mission has run against a real
model, and there is no deterministic Queen-driven acceptance test that reaches all twelve roles
through their production triggers. Enabling the roster by default makes that gap more visible, not
smaller. It is the next release's whole job.

Two programs ran in this line and both finished. The Core/Modules refactor (v3.8.3–v3.8.18) and the
twelve-role activation program (v3.8.19–v0.3.8.34). What follows is the state they left, measured.

> **v3.8.31 closed this line and was wrong to.** An external review of v3.8.29 found five defects
> that were still present, all with passing tests over them. v3.8.32 fixed them and built the guards
> that would have caught them; §6 records what the pattern was. The lesson is in the cleanup that
> missed them: it swept for ABSENCE — TODOs, broken links, unused declarations — and every one of
> those defects was a thing PRESENT and wired wrong. An absence-sweep cannot find them.

This replaces `NORTH_STAR.md`, `ROADMAP.md`, `REFACTOR-PLAN.md` and `POST_REFACTOR-PLAN.md`, which
are archived under `docs/archive/v3/`. There were 2,746 lines across those four, they overlapped
heavily, two of them were closed or superseded, and every release had to edit three of them to stay
consistent — which is how three documents come to disagree about the same release.

`CHANGELOG.md` remains the complete record of what shipped. This document answers ONE question:
**where the colony actually is**, measured.

**What is LEFT now lives in [`AUTONOMY-10.md`](AUTONOMY-10.md)** — the ten-phase program from here to
a production-qualified autonomous assistant, each phase with an exit gate that must pass through the
real composed runtime. Adopted v3.8.32.

The split is deliberate and is the lesson of the four documents this file replaced: a document that
describes both the present and the future ends up disagreeing with itself about the release in
between. PLAN.md is measured against the tree; AUTONOMY-10.md is ordered by dependency. Where a phase
there is already partly delivered, its status table says so rather than re-planning work that
shipped.

Rules for this file:

- Everything here is MEASURED against the tree, not estimated. Where something is unmeasured it says
  so rather than guessing.
- An item is DONE only when it has a production call site and a test keyed to what the producer
  actually emits. "Implemented" is not done — that distinction has cost this project six releases.
- When an item completes, it moves to the record below with its version, and the reasoning that made
  it hard stays attached. The reasoning is usually worth more than the fix.

---

## 1. What ANTHILL is for

A local, bounded, auditable colony that takes a goal, plans it, executes it through specialised
roles, and produces work an operator can verify and accept — on the operator's own hardware, with no
step that requires trusting a model's word about whether it succeeded.

The load-bearing commitment, from which most of the design follows:

> **Only reproducible evidence may carry a mission to a verified outcome.** A model's review is worth
> recording and can never promote. A compiler, a test runner, a hash comparison and a policy engine
> can.

The V4 target is a Codex/Claude-Code-style autonomous software workflow running on that framework.
It is not close, and the gap is not model quality — it is that roles still hand each other prose.

---

## 2. Where the colony actually is (measured at v0.3.8.40)

### Working end to end

| Capability | State |
|---|---|
| Mission planning and dispatch | Working. 12 roles registered, 12 handlers, 12 execution contracts |
| Durable worker/attempt runtime | Working. Leases, heartbeats, crash reclamation (v3.8.0) |
| Core/Modules boundary | Done and enforced by assembly-reference tests (v3.8.3–v3.8.18, ADR-007) |
| Artifact + evidence stores | Stores, producers, provenance, hashing (ADR-004, v3.8.19–v3.8.21) |
| Patch verification | Per-proposal, in a materialised sandbox containing the patch (v3.8.22–v3.8.23) |
| Deterministic blocks | A failed bundle or a soldier policy block demotes the mission outcome (v3.8.22) |
| Verification from evidence | The verifier reads stored evidence; model prose is recorded, never decisive (v3.8.27) |
| Pheromone decay | Trails fade toward neutral (v3.8.19) — they never had before |
| Colony recall | What has worked / usually fails / who solved this / what knowledge exists (v3.8.19) |
| Mission workspaces | Detached git worktrees, attributable to a base revision (v3.5.0) |
| Twelve roles run together | All twelve execute against a real database and registry, returning structured outcomes (v3.8.30) |

### Known gaps, stated plainly

| Gap | Why it matters |
|---|---|
| **Prose is still the PRIMARY channel** | v3.8.29 makes typed artifacts travel alongside it (coder, builder, verifier) with IDs for replay. `Task.Result` is still a string and the prose is still what the model reads first |
| **The verifier is still planner-selectable** | PARTLY CLOSED v0.3.8.41 — policy now binds it to the tester's and soldier's evidence, or inserts it when the plan omitted one, and a verification that cannot be inserted sets a `DeterministicBlock`. Its contract is still `PlannerSelectable`, because flipping it would refuse every planner-produced verifier until the adaptive delta-plan path also carries a parent |
| ~~Six specialists are gated off by default~~ | CLOSED v0.3.8.41 — `roster_profile` defaults to `full`. `ConfigSchema` migrates only configurations that never touched the roster; explicit choices and `disabled_roles` survive |
| **The tester does not run on the patched tree** | THE gap this release stopped short of from both directions. v0.3.8.41 makes the tester's report NAME the tree it judged and binds the verifier to that evidence, but the materialised patch still does not outlive `VerifyPatchSet`'s sandbox, so the tester resolves to the mission workspace — the same source WITHOUT the proposal in it |
| **No Queen-driven acceptance suite** | Twelve roles are enabled by default and every one has a production trigger; nothing yet drives all twelve through those triggers in one deterministic mission, and no live twelve-role mission has run against a real model |
| ~~Environmental failures charged to the ant~~ | CLOSED v3.8.32 — `FailureClassNames` is the one conversion; a test drives real results through the real mapper into the real attribution rule |
| ~~The verifier's sandbox held different bytes~~ | CLOSED v3.8.32 — `PatchApply` is the one applier; the materializer, the sandbox runner and `ApplyPatchTool` all call it |
| ~~The tester→medic handoff never fired~~ | CLOSED v3.8.32 — the gate reads the scheduler's terminal-failure return value instead of the ant's status code |
| ~~Readiness lied about the six core ants~~ | CLOSED v3.8.32 — `RoleGateStatus.NotGated` exists, and the ladder moved out of the route lambda into `RoleReadiness` where it is testable |
| ~~"Runs without an LLM" was untested~~ | CLOSED v3.8.32 — `OfflineMissionTests` runs whole missions with no provider |
| ~~The local model was hardcoded~~ | CLOSED v3.8.33 in source, v0.3.8.34 on disk — the retired default is recognised in an upgraded config.json rather than obeyed. `LocalModelResolver`; any model works, an unchosen one refuses with a remedy, and a source guard blocks a new default |
| ~~The console hid an unusable model~~ | CLOSED v3.8.33 — `/status` computed `ollama_model_present` from v2.4.3 and `app.js` never read it. Ninth instance of implemented-tested-unreachable, first in the UI |
| ~~Console executable attributes trusted escapeHtml~~ | CLOSED v0.3.8.34 — `jsArg` escapes for the interpreter before the attribute; 105 sites, verified against the real parser. v3.8.13 fixed one value and its own test documented why that was not enough |
| ~~A computed status field could have no reader~~ | CLOSED v0.3.8.34 — `StatusFieldConsumerTests`; `ollama_model_present` was probed on every request since v2.4.3 and read by nothing |
| ~~Backend capability with no console surface was invisible~~ | CLOSED v0.3.8.36 — `ConsoleRouteCoverageTests` audits the other direction. 25 of 176 routes had no surface; `/config/health` had computed configuration findings with no reader since v2.x. Six remain recorded as UI GAP |
| ~~A patch built on a stale read applied silently~~ | CLOSED v0.3.8.37 — `PatchProposal.BaseHash`; refused by all three appliers, checked before the fragment search, classified as TargetRejection |
| ~~A shipped changelog entry could be rewritten~~ | CLOSED v0.3.8.37 — `ShippedChangelogTests` compares each entry to its own tag. The mistake was made three times |
| ~~Mission submission could duplicate on retry~~ | CLOSED v0.3.8.38 — `POST /missions` passes an `Idempotency-Key` at last; the store has supported replay since v2.8.0 and nothing reached it |
| ~~A listed job could not be opened after restart~~ | CLOSED v0.3.8.38 — one projection for list and detail, live and durable; `outcome_code` joined from the canonical evaluation |
| ~~Cancel-all was not durable~~ | CLOSED v0.3.8.38 — delegates to the single durable cancel, so a crash cannot requeue cancelled work |
| ~~Clearing history could delete a running mission~~ | CLOSED v0.3.8.38 — refused server-side while work is active, and the durable job tables are no longer left dangling |
| **Reputation is derived, not consumed** | `ReputationOf` computes standing from trails as of v3.8.29. Nothing ROUTES on it yet — the router still picks by configuration |
| ~~Trail kinds unenforced~~ | CLOSED v3.8.31 — eleven kinds extracted from the call sites, validated on write, and a test pins the vocabulary against the code in both directions |
| **`AntMetrics`: InputChars still zero** | ToolCalls, ModelCalls, ElapsedSeconds, RetryCount and the environment fingerprint are all measured at chokepoints (v3.8.26, v3.8.31). InputChars would need each ant to report its own prompt size and no chokepoint sees it |
| **9 `/events/json` pollers remain in `app.js`** | The event stream exists and the console still polls. NOT a defect — polling is the documented fallback and works; moving to push is an optimisation for 3.9.0 |
| **166 public statics on `AnthillRuntime`** | Configuration is not standardised. The mission path is already forbidden from reading them (ADR-001, guarded); this is ergonomics rather than correctness |
| **No live twelve-role mission has ever run** | THE remaining gap. Everything above is verified by tests, and tests check what their author told them to check. A mission with `roster_profile: "full"` against a live model is the first thing 3.9.0 should do |

---

## 3. The twelve-role program

The target workflow. Every role has a real production trigger, a bounded surface, typed evidence,
and a proven end-to-end outcome — which does **not** mean every role dispatches a tool or runs in
every mission.

```
Context (Researcher · Web · File · UI Cartographer)
        │
        ▼
     Coder ──► PatchSet
        │
        ▼
     Queen ──► apply to isolated mission workspace
        │
        ▼
Tester + Soldier ──(retryable failure)──► Medic ──► back to Coder
        │
        ▼
   Verifier ──► VerificationBundle
        │
        ▼
Builder + Scribe ──► operator output
        │
        ▼
Canonical MissionEvaluation ──► Archivist ──► MemoryCandidate + pheromones
```

### 3.1 Scheduling modes

Declared on the contract (v3.8.23) and ENFORCED for all three non-planner modes (v3.8.25–v3.8.26).

| Mode | Roles |
|---|---|
| Planner-selectable | researcher, web, file, ui_cartographer, coder, builder, scribe |
| Inserted by policy | tester, soldier — inserted when a patch set exists (v3.8.26). The verifier is still planner-selectable |
| Triggered by retryable failure | medic |
| Triggered after finalization | archivist |

This is the mechanism that stops safety-critical steps depending on a model remembering to plan
them. A plan that omits the tester is not a plan that skipped a step — it is a plan whose patches
are unverified, produced by the component least able to be relied on for that.

### 3.2 Role specification

Where the current state differs from the target, both are stated. The gap is the plan.

| Role | Trigger | Tools | Typed output | Gap at v3.8.31 |
|---|---|---|---|---|
| **Researcher** | Planner, near intake | `system_info`, `list_directory`, `search_workspace`, `repository_index` | `context_brief` | Search granted AND dispatched at v3.8.30. Still emits prose (`text`) rather than a typed `context_brief` |
| **Web** | Planner, when external info needed | `web_search` | `source_set` | Done — genuinely typed since v3.8.21 |
| **File** | Planner | `list_directory`, `read_text_file`, `search_workspace`, `repository_index` | `file_set` | Done as of v3.8.30 — discovers paths rather than only reading ones the task named |
| **UI Cartographer** | Planner | 4 read tools | `ui_map` | Generalised at v3.8.28 — thirteen conventional layouts. Still not MANDATORY before Coder |
| **Coder** | Planner | **none, deliberately** | `patch_set` | Consumes prose, not typed context. PatchSet carries content but no workspace revision link |
| **Tester** | Policy, after every state-changing PatchSet | `run_allowlisted_check` | `test_report` + evidence | Done as of v3.8.28 — manifest-driven, multi-runtime. Cancellation and per-check timeouts still open |
| **Soldier** | Policy, on every state-changing PatchSet | none (PolicyScan is an in-process service) | `security_review` | Done as of v3.8.26 — reads the real PatchSet, inserted by policy |
| **Verifier** | Planner, after evidence exists | none | `verification_bundle` | Done as of v3.8.27 — reads the evidence store; a deterministic failure cannot be overridden by prose. Still planner-scheduled rather than policy-inserted |
| **Medic** | Retryable failure only | none (consumes `failure_context`) | `failure_diagnosis`, `repair_recommendation` | `failure_context` artifact does not exist; reads in-memory mission state |
| **Builder** | Planner, after verification | none | `operator_summary` | Emits prose |
| **Scribe** | Planner, after verification | `read_changed_files_summary` | `release_notes` / `docs_draft` | Done as of v3.8.28 — dispatches its tool; records whether the file list came from a diff or from prose |
| **Archivist** | After canonical evaluation persists | none | `memory_candidate` | Reachable as of v3.8.26 — runs post-finalization, outside the task graph. Had NEVER run before |

**A role with no tool calls is not inactive.** Coder, Soldier, Verifier, Medic, Builder and Archivist
can be fully functional through typed inputs, deterministic services, structured outputs and
consequential orchestration. Inventing tools to make an inventory look complete adds attack surface
without adding capability — which is why the three phantom tools were deleted rather than built
(v3.8.23; see §6).

---

## 4. What is left, in order

Each stage is a release. The order is a dependency order, not a preference.

### Stage A — the roster declares itself ✅ DONE (v3.8.23)

All twelve contracted; `SchedulingMode` declared; phantom tools removed; patches verified in a tree
that contains them.

### Stage B — the roster becomes consequential ✅ DONE (v3.8.25–v3.8.26)

Done: handoffs ingested on terminal failure; a refused REQUIRED handoff sets `DeterministicBlock`;
`ToolExecutionContext` has a production call site fed by `CapabilityGrant`; `SchedulingMode` enforced
for FailureTriggered and PostFinalization.

Also done: the soldier reviews the actual PatchSet — the patch-set artifact carries `new_content` at
Colony visibility and the review reads it, so a secret in proposed source is found rather than
scanned for in prose about it.

v3.8.26 closed it: `InsertPolicyReviewTasks` inserts tester and soldier whenever a patch set exists,
`PolicyInserted` is enforced now that the replacement path is real, and `/colony` reports per-role
readiness with the first binding blocked reason.

The verifier stays planner-selectable, deliberately — it is how verification happens at all today,
and moving it to policy insertion belongs with making it a deterministic evidence reader (Stage D).

1. **Honour `SchedulingMode`.** Policy inserts tester/soldier/verifier whenever their inputs exist.
   Medic fires from a typed retryable failure with a strict repair budget. Archivist runs after the
   canonical evaluation persists. The planner stops being able to schedule any of them.
2. **Ingest handoffs on the failure path.** Move `IngestHandoffs` so a failed task's handoffs are
   acted on — today Tester's failure→Medic handoff is unreachable.
3. **Make a rejected REQUIRED handoff consequential.** Block or fail the mission; do not just log.
4. **Route production dispatch through `ToolExecutionContext`.** Remove the ant-name authorization
   path. Every invocation carries mission/task/role/worker/attempt ids, granted capabilities, tool
   budget and a cancellation token.
5. **Soldier reviews the actual PatchSet**, not prior-task prose.

### Stage C — typed collaboration ◐ INTERCHANGE DONE (v3.8.29)

The one that unblocks everything downstream, and the largest.

1. Task contracts gain input artifact IDs and expected output schemas.
2. A context compiler passes bounded artifact excerpts to workers.
3. Researcher, Builder and Verifier get genuinely structured outputs — **not renamed prose**.
4. PatchSet stores diff content linked to a workspace revision.
5. `VerificationBundle` is persisted, bound to the patch and its evidence.
6. The canonical evaluator consumes that bundle.
7. Every artifact carries schema version, producer, environment fingerprint, content hash, non-null
   `sourceArtifactIds`, and a redaction class.

### Stage D — per-role graduation ◻

Each role brought to the target in §3.2: UI Cartographer generalised and mandatory before Coder;
Tester manifest-driven with multi-runtime adapters and cancellation; Medic consuming a typed
`failure_context` with one bounded repair then mandatory retest; Scribe actually calling its tool and
drafting only from verified artifacts; Archivist running post-finalization.

### Stage E — outcome-gated learning ✅ DONE (v3.8.26, v3.8.29)

Pheromones recorded **only after** the canonical outcome persists.

| Situation | Effect |
|---|---|
| Output consumed downstream, required evidence passed, `completed_verified` | Positive role/worker trail |
| Typed failure attributable to a role's output | Negative role/task-type trail |
| Tester catches a real failure | Positive Tester; possibly negative patch/route |
| Soldier correctly blocks a dangerous patch | Positive Soldier; negative patch/route |
| Medic's repair passes mandatory retest | Positive Medic recovery trail |
| `completed_unverified` | Store the episode; no positive reinforcement |
| Disabled / skipped / cancelled / missing dependency | **Neutral.** A role is never punished for not running — `LearningAttribution`, v3.8.26 |
| Provider failure | Update provider reliability, not worker skill |
| Tool failure from the environment | Update tool/environment reliability, not worker skill |

No single global worker score. Trails are keyed by role, worker, task type, capability, environment
fingerprint, tool/source domain, and contract version. A trail influences routing only after a
configurable minimum number of observations. **Learning starts at full-roster activation** — legacy
unverified completions are never backfilled as positive.

### Stage F — qualification and activation ◐ FIXTURE DONE (v3.8.29)

CI requires all twelve to qualify. The default deliberately stays `core`; flipping it is the
operator's act, after a real run.

Per role: unit, integration, production-call-site, fault and end-to-end tests; cancellation, timeout,
retry and loop-bound tests; real model/tool-call metrics; shadow then supervised operation; an
operator-visible activation record and rollback.

A readiness surface reporting, per role: enabled state, scheduling mode, handler present, contract
present, tools implemented and registered, capabilities granted, model fitness, qualification status,
and the exact blocked reason. `RoleAvailability` already carries part of this.

Then a coherent profile rather than several unrelated flags:

Shipped in v3.8.26 as `roster_profile` + `disabled_roles`; the shape below was the target:

```json
{
  "ant_roster": {
    "profile": "full",
    "enabled_roles": ["researcher","web","file","coder","builder","verifier",
                      "ui_cartographer","tester","soldier","scribe","medic","archivist"],
    "handoff_ingestion": true,
    "adaptive_mission_control": true,
    "objective_verification": true,
    "max_medic_repairs_per_mission": 1
  }
}
```

Per-role kill switches stay, for rollback. `profile: full` resolves to all twelve. **CI requires all
twelve to report Ready under the full-roster qualification fixture before the default flips.**

### Stage G — remaining cleanup ◻

- 9 `/events/json` pollers in `app.js` move to the event stream
- Configuration standardisation (166 public statics on `AnthillRuntime`)
- The withdrawn no-UI boot gate (see §6)

---

## 5. Acceptance gates

Non-negotiable. The colony is not a twelve-role colony until all of these pass.

1. ◻ All twelve roles report Ready under the full profile
2. ◻ Every enabled role has a handler, contract, real production trigger and typed output
3. ✅ A compile-breaking proposed change fails when built in the patched mission workspace *(v3.8.23)*
4. ✅ That failed patch cannot become `completed_verified` *(v3.8.22)* — retention and learning still to close
5. ✅ A Soldier block cannot be overridden by model text *(v3.8.22)*
6. ◻ Tester failure triggers exactly one bounded Medic repair and a mandatory retest
7. ◻ A UI change cannot reach Coder without a valid `ui_map`
8. ◻ Scribe and Archivist cannot act positively on unverified work
9. ◻ Archivist runs only after the persisted canonical evaluation exists
10. ◻ Replaying artifact IDs reconstructs every role's inputs and evidence
11. ✅ No mission ant can dispatch shell, direct file-write, or primary-workspace patch tools *(pinned by `RosterContractTests`)*
12. ✅ Disabled or unavailable roles never receive negative reputation for not running *(v3.8.26)*

---

## 6. The record — what was hard, and why

Kept because the *shape* of these mistakes recurs, and recognising the shape is worth more than any
individual fix.

**The recurring defect: a check that answers a question ADJACENT to the one asked, and passes.**
Found thirteen times.

| # | Release | The adjacent answer |
|---|---|---|
| 1–5 | v3.8.18 | `ApplyPatchTool` validated patch paths without its injected options; `WebSearchTool` the same on SSRF; `WorkspacePathGuard` read ambient state; `SafetyPolicy.Configure`/`Reset` were public so any SDK consumer could clear the blocklist; the no-UI gate asked for a fabricated resource name and watched a null check |
| 6 | v3.8.21 → fixed v3.8.22 | The planner emits `patch_proposal`; `VerificationPolicy` is keyed `code_patch`; nothing mapped them, so `diff` and `build` never ran on a single patch while the event row said verification had happened |
| 6b | v3.8.22 → fixed v3.8.23 | The build verifier then ran against the PRIMARY workspace, which does not contain the patch. True statements about the wrong tree |
| 7 | v3.8.26 → fixed v3.8.32 | `LearningAttribution` compared `task.FailureType` (`transient_provider_failure`) against the enum NAME (`TransientProviderFailure`) with `OrdinalIgnoreCase` — which bridges casing and not underscores. The test fed the enum name, a value production never writes there. Every environmental failure was charged to the ant for six releases |
| 8 | v3.8.23 → fixed v3.8.32 | `PatchSetMaterializer` overwrote whole files, ignoring `old_content`. Its tests checked that materialisation SUCCEEDED and hashed, never that the bytes matched what `ApplyPatchTool` would produce. Three appliers, no two alike |
| 9 | v3.8.25 → fixed v3.8.32 | The handoff gate read `!decision.Retryable` — the ant's status code — where it meant "the scheduler scheduled no retry". Tests covered "handoffs ingest on terminal failure" and "the tester emits a medic handoff" separately, never together |
| 10 | v3.8.26 → fixed v3.8.32 | The readiness ladder asked specialist-only questions of all twelve roles. Untestable inside a route lambda, so untested |
| 11 | v3.8.5 → fixed v3.8.32 | `CoreWithoutProviderTests` proved a typed refusal at one boundary and was allowed to stand for "a mission runs with no LLM" |
| 12 | v0.3.8.40 → fixed v0.3.8.41 | `AgentCliProvider` took a `workingDirectory` documented as the confinement for a writing agent. Neither production caller passed it, so a `Writes = true` agent inherited the API host's directory — the live checkout — and went around `SandboxWorkspace`, the path guard, PatchSet review and the approval gate in one step. `Writes` had one consumer: a JSON field the console displays. A sweep for "is confinement implemented?" finds a documented parameter and a flag and answers yes |
| 13 | v3.5.0 → named v0.3.8.41 | `RunAllowlistedCheckTool` resolves its workdir from whatever workspace is ambient. A tester ant runs as its own DAG task, AFTER `VerifyPatchSet` disposed the scope holding the materialised patch — so it checked the mission workspace, which has no patch in it, and "3 checks passed" was recorded as though it judged the proposal. 6b again, one layer out: the verifiers were fixed in v3.8.23 and the ant that runs the same checks was not |

**The rule that came out of it:** a test for a production wiring must be keyed to a value the
PRODUCER actually emits. The v3.8.21 tests passed the literal string `"code_patch"` — a task type
production never produces — so they could only ever prove the callee was self-consistent.

**The rule was written after #6 and applied only FORWARD.** That is the v3.8.32 lesson and it is the
expensive one. Defects 7–11 were all already in the tree when the rule was written down; it was
applied to new code as it was authored and never run backward over the suite that existed. Recording
a lesson as history is not the same as building a detector for it.

`CrossBoundaryAgreementTests` is that detector, and it exists in three forms — no `FailureClass`
stringified outside the shared converter, no second patch applier, no enum with a custom wire form
read by `Enum.TryParse`. All three were verified to FAIL against v3.8.31 before being kept. **A guard
nobody has watched fail is a guard nobody has tested**, and the vocabulary guard added in v3.8.31 is
the proof: the first thing it did on its first run was catch its own author.

**Why the v3.8.31 "full cleanup" missed all five.** It swept for ABSENCE — TODO comments, broken
links, untracked files, undeclared trail kinds, unsuppressed warnings. Every question it asked was
"is something missing". All five defects were things PRESENT and wired wrong, where nothing is
missing and everything compiles. An absence-sweep is structurally incapable of finding them, and
1,750 passing tests measured how much had been asserted rather than how much was true.

**The second recurring defect: a well-built subsystem with no production call site.** Found three
times, all in the same area. `VerificationRunner` (four verifiers, a policy table, tested since
v2.12, never called). `SandboxWorkspace` + `PatchVerifyRunner` (materialise a patch and build it
since v1.8.24 — operator-triggered only, never reached from a mission). `ToolExecutionContext`
(**still open**, Stage B item 4). The generated call-site audit exists because of this class; it
catches declarations with no consumer, and did not catch these because the consumers exist — they
are just not on the path that matters.

**Fail-closed logic blind to its own case (v3.8.23).** `AntExecutorCatalog.Initialize` checked for a
missing contract only `if (isSpecialist)`, so for the six roles that had no contract the check that
would have reported it did not apply. The most privileged role in the colony — the coder, the only
one producing source changes — was the least specified, and nothing could see it.

**Three phantom tools, deleted rather than built (v3.8.23).** `policy_scan`, `read_failure_context`
and `write_memory_candidate` sat in contracts from v2.19.0 with nothing implementing them. The
instinct was to build all three. On inspection: `policy_scan`'s capability already exists as an
in-process deterministic service and belongs out of a model's reach; `read_failure_context` should be
orchestration assembling a typed artifact, not a tool the medic fetches with; and
`write_memory_candidate` was **redundant** — the archivist already writes candidates as artifacts and
`IngestMemoryCandidates` already consumes them, so building it would have created a second channel
writing the same fact. Implementing all three would have produced a green inventory with more attack
surface and one duplicate write path.

**A withdrawn gate, on purpose (v3.8.18).** The no-UI boot CI job failed three times; rather than
mark it `continue-on-error` and keep a green tick, the job was removed and the success criterion
reverted to NOT PROVEN. A gate that cannot hold is worse than an absent one, because it is counted.
Still open, in Stage G.

**Ordering that had to be argued (v3.8.19).** Worker reputation is numbered three stages before the
knowledge graph in the old plan, and was deliberately built after it: reputation learned before
reproducible evidence rewards persuasive prose rather than demonstrated work. Read the dependency
order, not the numbering — which is part of why those documents were merged into this one.

---

## 7. Where everything else lives

| Document | Purpose |
|---|---|
| `CHANGELOG.md` | The complete record of what shipped |
| `docs/adr/ADR-001…007` | Decision records. Why a choice was made, and what it cost |
| `docs/ANT_EXECUTION.md` | The execution framework's role matrix and gates |
| `docs/AUTONOMY.md` | The autonomy model and its limits |
| `docs/APPROVALS.md`, `docs/CONTRACTS.md` | Approval pipeline; task contract vocabulary |
| `docs/DEPLOYMENT.md`, `docs/HOMELAB.md` | Running it |
| `docs/HANDOFF.md` | Release recipe — including the manual tag steps |
| `docs/TRAINING_MISSIONS.md` | Missions used to exercise the colony |
| `docs/archive/v3/` | `NORTH_STAR`, `ROADMAP`, `REFACTOR-PLAN`, `POST_REFACTOR-PLAN`, and superseded design docs |
| `docs/archive/v2/` | The Homelab Command Center era, closed at v2.26.0 |
