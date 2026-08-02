# ANTHILL Changelog

## v3.2.1 — Direct manipulation: drag to arrange, corner to size

The dashboard is arranged by hand now, not by buttons. In **Customise** mode every widget can be
dragged to a new position and resized from its bottom-right corner; the grid reflows around it.

- **Drag to arrange.** A coloured edge shows where the drop will land. Uses the native
  drag-and-drop API rather than a cursor-following clone, because a clone is a layer stacked over
  the grid and this layout deliberately has no stacking order. The arrow buttons remain — dragging
  must never be the only path to a feature.
- **Corner resize.** Width snaps to whole grid columns; height is free pixels. The browser's own
  `resize` grip is used, so no absolute positioning is introduced.
- **Sizes are stored as a proportion, not a pixel width.** A widget set to half the dashboard stays
  half the dashboard at every window size. Storing a column count would make it a quarter of the
  screen on an ultrawide, where the grid has 24 tracks rather than 12.
- **An operator-set height wins over auto-fit.** The content-fit pass that keeps idle cards small
  runs on a timer, and would otherwise have undone every resize a few seconds after it was made.
- Layout — order, hidden widgets, spans and heights — persists through the existing single
  `ui_state` writer. Values that are not sane numbers are dropped on load rather than trusted.

## v3.2.0 — Dashboard redesign, typed model results, and the composer fix

> **Read this before upgrading unattended.** Despite arriving as a minor release, this replaces the
> dashboard layout engine outright. **Every saved dashboard arrangement is reset** — panel
> positions, sizes, tab groups and docking are gone, the floating workspace is deleted, and there
> is no kill switch to return to it. Nothing else about your colony changes: schema 16 is
> untouched, missions, memory, skills and ant customisation are all unaffected.

Three tracks, released together.

### New Features

- **Responsive dashboard grid.** The console is a CSS Grid of widgets instead of absolutely
  positioned frames layered over the colony canvas. The Colony is now a first-class widget at the
  visual centre of the layout rather than the page background.
- **Widget framework** (`dashboard-grid.js`): every widget gets a title, icon, loading / empty /
  error state, and a refresh control. A widget whose renderer throws fails ALONE, in its own cell —
  on a console meant to be left open all day, one bad renderer must not blank the dashboard.
- **Mission Composer is reachable again.** Its controls — the execution mode selector and the plan
  REVIEW step — had no reachable path in the shipping console since v2.15.0.

### Improvements

- **Widget spans are proportionally invariant**: small = 1/4 of a row, medium = 1/3, large = 1/2,
  colony = full, at every width above 901px. A layout that tiles at one resolution tiles at all of
  them. Measured live at 1366 and 1920: 17 widgets, 6 rows, **100% row occupancy**, zero overlaps,
  zero off-screen widgets, no page-level horizontal scroll.
- **Typed model provider results.** `IModelClient.Generate` returns a `ModelCallResult` whose status
  is set where it is KNOWN. Previously every client formatted what it knew — a 404, a refused
  connection, a cancelled token — into prose, and a classifier recovered it downstream by substring
  match. Rewording one message would have silently reclassified the fault and stopped the circuit
  breaker tripping, with nothing failing to show it.
- The floating workspace is deleted: `dashboard-workspace.js`, its stylesheet, its state plumbing
  in the page, and 43 tests specific to it. `src/Anthill.Api/Ui/` is four files.

### Bug Fixes

- **An empty model response counted as success.** It never began with `ERROR:`, so every prefix test
  passed it: the planner handed it to the JSON parser as a plan, the strategist treated it as a
  strategy, the coder cached it as a patch set, `ModelRouter` REINFORCED the route's pheromone trail
  and logged `success:true`, and provider verification recorded a provider that answered with
  nothing as VERIFIED. All now decided by status.
- **The plan preview showed a plan that would not run** — it skipped the authorization gate, in the
  one surface whose entire purpose is saying what is about to happen.
- **The release guard blocked its own documented recovery.** `.githooks/pre-push` rejected tag
  DELETIONS, which is exactly what `scripts/release.sh` prints as the way to retract a mis-tagged
  release.
- Widget bodies no longer scroll sideways on a long unbroken identifier, and no longer break
  ordinary words mid-token.

### Breaking Changes

- **Saved dashboard layouts are reset.** Old workspace rows remain in the database, unread. There is
  no path back — the kill switch was removed with the engine.
- `POST /missions/plan` gains `blocked` / `blocked_reason` per step (additive; nothing removed).
- Internal C# signatures changed (`IModelClient.Generate`, `ModelRouter.GenerateTyped`,
  `ResultAssembler.SelectFinalAnswer`). Not a supported extension surface, but anything compiled
  against `Anthill.Core` needs updating.

### Upgrade Notes

Drop-in apart from the layout reset. No migration, no configuration change. Schema 16 unchanged, so
rolling back to the previous binary restores the old console and its saved layouts intact.

## v3.1.1 — The Mission Composer was unreachable

A UI reachability defect found while verifying v3.1.0's plan-preview fix in a live console: the
fix was correct and could not be seen, because **nothing in the shipping console could reach the
plan preview at all.**

`POST /missions/plan` (v1.8.18) is served by a card on the classic overview grid. v2.15.0 made the
topology workspace the default console, which hides that grid — and the workspace's panel registry
never included the composer. So since v2.15.0 the endpoint worked, the renderer worked, and the
"⌕ Preview Plan" button existed in the DOM with `visible: false`. Confirmed live before fixing.

This is the repo's recurring defect one layer above where `CallSiteAudit` looks: that audit proves
a C# declaration has a production consumer, but a UI control with no reachable path is invisible to
it. The cost here was not a dead feature in the abstract — it was the *review step*. "See the plan
before you approve dispatch" is a safety affordance, and it had been dark for many releases.

### Bug Fixes

- **Mission Composer restored to the console.** Registered as a `mission-composer` workspace panel
  on both sides of the contract (`DashboardWorkspaceState.KnownPanelIds` and the client panel
  defs). Existing saved layouts do not gain new panels automatically, but the Modules menu is built
  from the panel defs — so it appears there for every install and can be switched on without
  resetting a layout.
- **Release guard blocked its own documented recovery.** `.githooks/pre-push` rejected tag
  *deletions*: a deletion push sends an all-zero local sha, the version lookup found no commit, and
  the guard blocked with `code Version: <not found>`. `scripts/release.sh` prints exactly that
  deletion as the way to recover from a mis-tagged release, so the tooling contradicted itself at
  the only moment it mattered. Deletions are now allowed — the guard exists to stop a bad tag being
  published, never to stop one being retracted.

### Upgrade Notes

Drop-in. No migration, no configuration change. The composer panel is off in existing layouts until
enabled from the Modules menu, or on by default after a layout reset.

## v3.1.0 — Runtime composition and Queen decomposition

The V3 roadmap's second phase. **No new features, no new gates, no schema change** — this release
exists to make the mission path composable, and its success criterion is that behaviour did not
move. The v3.0.0 characterization tests are what made that provable rather than asserted.

`AnthillRuntime` is a bag of mutable statics, the honest .NET translation of the Python module
globals it replaced. Every consumer read whatever the last writer left behind, at whatever instant
it happened to look. v2.26.0 found six call sites independently deriving mission success; the same
shape of defect was available to any gate read twice at two different moments.

### New Features

None. Deliberately.

### Improvements

- **`RuntimeOptions`** — 35 mission-path settings captured once per run, immutable thereafter. A
  projection of existing config: no new defaults, no new precedence rules, no behaviour of its own.
- **`RuntimeProfile`** — the run's resolved capability set: executable roles, tool grants taken from
  the registry that was actually built, write permissions, verification policy. Validated at
  construction by the v2.26.0 `RuntimeConfigValidator`, whose findings are *carried* rather than
  thrown — that validator's contract is to degrade loudly and never refuse boot.
- **`MissionContext`** — a mission's governing facts, resolved once at intake and passed explicitly:
  constraints, capability grants, budgets, correlation id, and an **absolute UTC deadline**. Never
  ambient; an AsyncLocal context would have been a smaller diff and would have reproduced the exact
  defect being removed. Persisted as a `mission_context_resolved` event, so an operator can read a
  mission's boundaries instead of inferring them.
- **`RuntimeHost`** — the composition root. One host owns one colony: one database, one profile, one
  Queen. Not a container: no service locator, no registration API, no lifetime scopes.
- **Queen decomposed** behind `IPlanningService`, `IExecutionService`, `IMissionEvaluator`,
  `ILearningRecorder`, `IResultAssembler`, and `IMissionCoordinator`. Each takes its dependencies as
  constructor parameters; none reads a mutable gate. **`Queen.cs`: 1,365 → 381 lines.**
- **The Queen is composed, not self-configuring.** It takes a `RuntimeProfile` instead of reading
  `EnableModelRouting` / `UseOllama` / `EnableFileTools` / `EnableFileWriting` during construction.
  This is what makes two differently-configured colonies in one process possible at all.
- **`MissionConstraints.Parse`: eight call sites → two.** Both survivors are deliberate and
  documented in a guard test: `CoderAnt` (the ant contract is v3.2.0's to redesign, and forcing it
  here would design that contract twice) and `ObjectiveLifecycle` (parses an objective charter — a
  different input).
- **The mission deadline is an absolute instant**, not a duration re-measured in two dispatch loops.
  A resumed run inherits the original boundary instead of restarting its clock.

### Bug Fixes

- **The plan preview showed a plan that would not run.** `POST /missions/plan` never applied the
  authorization gate, so it could show an operator a step dispatch refuses on sight — in the one
  surface whose entire purpose is saying what is about to happen. It now runs the same construction
  a dispatch does. The endpoint was also re-parsing the goal *and* re-running `ValidateTask` to
  rebuild warnings the plan already carried; it now reports what the plan says.
- **`MissionEvaluator` read a mutable static.** The single authority on whether a mission succeeded
  depended on what `EnableObjectiveVerification` said at the instant finalization ran, so its
  verdict could not be reproduced from the persisted record it writes. It is now a pure function of
  its arguments.
- **Planning was implemented twice** — in `RunMission` and again in `PlanPreview` — and the copies
  had already diverged. One construction now serves both.

### Performance

Unchanged. Constraints are parsed once per mission rather than once per task, which is a real but
immaterial saving; nothing else in the hot path moved.

### Breaking Changes

None at the API or database level. Schema 16 is unchanged and a v3.0.x database loads as-is.

Internal C# signatures changed (`Planner.CreateTasks`, `MissionEvaluator.Evaluate`,
`ObjectiveVerification.IsSatisfied`/`Explain`, `Queen.PlanPreview`). These are not a supported
extension surface, but anything compiled against `Anthill.Core` will need updating.

`POST /missions/plan` gains `blocked` and `blocked_reason` on each step — additive; nothing was
removed or renamed. The console marks a refused step **REFUSED** with its reason.

### Upgrade Notes

Drop-in. No migration, no configuration change, no operator action. Roll back by deploying the
previous binary; the database is untouched by this release.

## v3.0.1 — Generation-integrity scoring + native Infrastructure integrations (Homarr parity)

Found by live end-to-end testing against a running console: with the routed model (Ollama)
unavailable, read-only missions still reported `completed_verified` (score 1.00) even though every
model call fell back and the "answer" was a canned non-model response. The canonical evaluator scored
structural completion + a passing verifier, but had **no notion of whether the answer was actually
generated** — so a provider-down run, and equally a hallucinated answer whose verifier passed, both
read as a perfect verified success. That directly undercuts the V3 "believable autonomy" principle.

- **`Task.GenerationDegraded`** (additive): a structured flag set in `Queen.PersistExecutionRecord`
  from the ant's EXISTING disclosure — a fallback ant returns `succeeded_with_warnings` with a
  `provider_failure` warning. Read from that structure, never parsed from result prose (per the
  repo's own rule). Transient/in-memory, consumed by the single live evaluation.
- **Generation-integrity layer in `MissionEvaluator`**: `completed_verified` now additionally
  requires that generation was NOT degraded. A mission whose answer came from a model-unavailable
  fallback demotes to `completed_unverified` — which `MissionOutcome.IsPositiveSuccess` already
  excludes, so it can never reinforce learning, credit a skill, or drive auto-apply. The evaluation
  explanation gains a `generation=degraded` marker.
- **Default-safe & backward-compatible**: the flag defaults false, so every pre-existing case — and
  the v2.26 characterization mission-outcome truth table — is byte-for-byte unchanged. Only a
  genuinely degraded run is demoted.
- **Sandbox observability**: `CoderAnt`'s sandboxed path was discarding the `SandboxRunReport`, so a
  sandbox iteration left no trace — you could not tell whether the in-sandbox build even ran. It now
  logs one structured line per coder task (`[sandbox] coder task=… stop=… verified=… check=… diff=…`),
  making the bounded loop observable and debuggable. Found while end-to-end testing the activated loop.
- Tests: an intact verified research mission still scores `completed_verified`; the same mission with
  a degraded-generation task demotes to `completed_unverified`, is non-positive, and is explained.

### Infrastructure — native service management to Homarr parity

The Infrastructure module gains equivalents of what a Homarr-style dashboard provides, implemented
natively on the existing `IIntegrationDefinition` contract (GET-only clients, credentials write-only
in the store and fetched per request, D1 target-allowlist checked before any I/O, strict timeouts,
deterministic sync — no LLM, no writes) rather than embedding or cloning anything.

- **Three new integration kinds** register into `IntegrationCatalog` alongside the existing *arr and
  download families: **Overseerr/Jellyseerr** (`health` + `requests` widgets), **Plex** (`health` +
  `mediaServer`: active streams + version), and **Uptime-Kuma** (`health` + `status`: monitors up/down
  from the public status-page slug). Each is one definition + client + typed widget payloads; the
  generic scheduler sweep picks them up with no per-kind endpoints or UI pages.
- **Widget renderers** for the new kinds (`requests`, `mediaServer`, `status`) in the dashboard widget
  runtime, tolerant of missing fields, so a pinned integration renders live data on any board zone.

## v3.0.0 — V3 baseline lock

The first V3 release, and deliberately the least exciting one: **no new feature behavior**. V3's
roadmap opens by locking a measured baseline before the runtime architecture changes, on the
principle that you cannot safely decompose a system you cannot inventory.

### The V3 document set is canonical

`docs/NORTH_STAR.md` and `docs/ROADMAP.md` are now the V3 documents — Colony Execution
Infrastructure, v3.0.0 through v3.9.0. The nine completed V2 planning documents moved to
`docs/archive/v2/` with a README mapping each to the release that closed it. History, not
authority.

### The runtime inventory and call-site audit

V2 shipped seven well-tested subsystems that nothing called. Every one was found by a person
reading carefully, one release too late. That is not a process.

`RuntimeInventory` enumerates what the runtime DECLARES — roles, feature gates, endpoints, tables,
background loops: 300 declarations today — and pairs each with its production call sites. Tests
are deliberately not counted as consumers; that a subsystem has tests is exactly what made the V2
defects invisible. Comments are stripped before searching, because a symbol named only in a doc
comment is how dead code looks alive.

`CallSiteAudit` turns gaps into a build failure, in both directions: a declaration with no
consumer is a regression, AND an exemption that has since acquired consumers is stale and must be
removed. An allowlist nobody prunes is how a real gap eventually hides inside one. The exemption
list ships **empty** — the honest state, and the one worth defending.

Building it taught something worth recording. The first draft reported 61 orphans out of 300,
because its symbol matcher rejected dot-qualified access — and `AnthillRuntime.EnableAutonomy` is
precisely how a static gate is read. A check that cries wolf gets switched off within a week, so
the matcher was corrected before the finding was believed.

### The eighth instance, found by machine

With the matcher honest, the audit found exactly one real orphan: **`cors_enabled`**. Documented
in `config.example.json`, parsed into `AnthillConfig`, projected into `AnthillRuntime.EnableCors`
— and read by nothing. A security-adjacent switch an operator could set and believe protected
them. Removed rather than implemented, because v3.0.0 adds no feature behavior; if cross-origin
access is wanted it arrives properly, with an origin allowlist and tests.

### Hygiene residue

A duplicate mission-deadline `CancelAfter` (introduced by v2.26.0's own drain work) and the
Python-era `docs.python.org` source-authority default — a relic of when the colony itself was
Python. Per-language source authority belongs to the workspace adapters in v3.3.0, not to a global
default.

### Characterization tests

A different kind of test from the rest of the suite: these do not assert that behaviour is
*correct*, they assert that it is *what it is today*, so v3.1.0's decomposition can be proven
behaviour-preserving rather than asserted to be. Pinned: the complete mission-outcome truth table
(nine rows), the verifier verdict vocabulary including its ambiguity and unknown cases, the
three-way skill-outcome split (promotable / neutral / failure), pheromone signal categories,
constraint parsing, the action-state mapping, and the ant status-code mapping. A V3 phase that
deliberately changes one updates the test in the same commit with its reason — never deletes it.

### Architecture decision records

Five ADRs in `docs/adr/`, each written before the phase it governs and each naming what was
explicitly rejected — the rejected option usually being the smaller diff:
ADR-001 runtime composition and Queen decomposition (v3.1.0, rejects a cosmetic file split),
ADR-002 immutable `MissionContext` (v3.1.0, rejects an ambient one),
ADR-003 durable worker and attempt protocol (v3.4.0, rejects distributing now),
ADR-004 artifact and evidence store (v3.5.0, rejects keeping prose as a second channel),
ADR-005 mission workspace manager (v3.3.0, rejects a shared reused sandbox).

### Operator surface

`GET /runtime/inventory` returns the same data CI gates on: every declaration, its consumer count,
and the audit verdict.

## v2.26.0 — Pre-V3 runtime hardening

An external engineering deep-dive audited the repo before V3. Every claim was verified against the
code first (docs/PRE_V3_RUNTIME_HARDENING.md records confirmed / already-fixed / invalid, item by
item); every confirmed defect is fixed here, under one governing principle: **one outcome, one
verification authority, one durable stop, one task lifecycle, one learning boundary, one action
lifecycle.** This is a hardening release — nothing here makes ANTHILL more autonomous; all of it
makes the autonomy it already has believable.

### One outcome — the canonical mission evaluation

Six call sites independently re-derived whether a mission succeeded, and they could disagree —
task rows lacked fields the live path used, and one caller (v2.23's route registration) resolved
the outcome mid-mission while status was still `Running`, always read negative, and **never
registered a single route in production**. A mission is now evaluated exactly once at finalization
(`MissionEvaluator`), across three explicit layers — structural completion, verdict-gated
verification, goal deliverable — persisted on the mission row (migration 16) BEFORE completion is
published, and consumed by every positive path: Director outcome/EMA/follow-ups, auto-apply (which
also re-checks at the writing site), skill credit, pheromone learning, candidate routes, job
status, restored-mission listing. Rows that predate the evaluation are `legacy`: never verified,
never retroactively promoted. An interrupted mission is never any flavour of completed.

### One verification authority

`VerificationBundle.Promotable` now intrinsically requires a passing deterministic result — the
requirement used to live in a separate flag callers had to remember, and the one that mattered
didn't: Queen fabricated `Passed: true, Deterministic: false` mission evidence from a model's own
verdict and used it for skill credit. That path is gone. A canonically verified mission whose
evidence is semantic-only records a NEUTRAL skill observation — no promotion, and no punishment
either.

### One durable stop

`ColonyDirector.Start()` called `AutonomyControl.Resume()` — and `--autonomous` boot calls
`Start()`, so a process restart silently cleared a durable operator STOP. Starting the Director
process and resuming autonomous work are now different acts: the loop starts (status and resume
endpoints work), launches nothing while STOP exists, and only the explicit operator resume at
`POST /autonomy/start` clears the sentinel, audited. Restart tests prove the sequence.

### One task lifecycle

On mission timeout/cancel the parallel executor returned immediately while `Task.Run` futures
were still executing — a terminal mission could contain running tasks. The mission deadline now
CANCELS the mission token (reaching every in-flight model call), a bounded drain waits for
in-flight work and marks non-terminating tasks with persisted cancellation reasons, and
finalization asserts no task is left non-terminal (violation = logged internal runtime defect,
fail closed). Task rows persist criticality and cancellation reasons (migration 16) so row-based
evaluation can never disagree with live state. API jobs map their status FROM the canonical
outcome — `status=complete, outcome=timed_out` is no longer possible.

### Concurrency correctness

The Planner held the offered-skill set in an instance field on a planner shared across concurrent
missions — plans could cross-contaminate skill provenance. It is stateless now (a deterministic
interleaved-parse test pins it). Skill outcome recording is serialized, skills persist
row-atomically with a `revision` column, and mission finalization saves only the skills it
touched — whole-registry last-writer-wins saves are gone from the credit path.

### Core ants declare their outcomes

Researcher, Web, File, Coder and Builder implement explicit `Execute`: an exhausted search budget
is a SKIP; a search that saved zero sources is a FAILURE, not an ordinary success; an inspection
whose every tool call failed FAILS; a zero-proposal coder run on a patch task FAILS (classified by
parsing the coder's own JSON artifact, never its prose); fallbacks disclose degraded generation as
structured warnings. Model calls return typed results (`ModelCallResult` over the classifier the
telemetry already used); an empty response is never success.

### One learning boundary

Pheromone writes now carry a `signal_category` stamped in the one write path
(operational_telemetry / reliability_signal / quality_signal / procedural_learning /
routing_preference), and PLANNING reads only the learning-bearing categories — a provider
answering HTTP 200 is telemetry, not strategy. Positive reinforcement consumes the canonical
evaluation. Strategist follow-up objectives land as `suggested` — model opinions, visible and
auditable, executable only after `POST /objectives/{id}/approve`; evidence-derived follow-ups
(verified mission + structured finding + budgets) remain the only auto-admitted path.

### One action lifecycle, completed

v2.25.0 made a failed post-execution verify canonically `failed` but still returned `Ok=true`.
The return is now a failure: "command issued" is not "desired state achieved".

### Auto-apply break-glass

`autonomy_autoapply_keep_without_verify` is now explicitly a development break-glass: using it
logs a critical event, the readiness evaluation reports the installation NOT QUALIFIED while it is
enabled (a measured disqualifier, not an attestation), and a kept-unverified change can never
record verified success or reinforce learning.

### Operability

`/config/health` + startup events surface incompatible feature combinations (adaptive repair
without Medic, handoffs with no destinations, auto-apply without deliverable verification, sandbox
without workspace) — degraded loudly, never silently. `/colony/introspection` answers what the
colony IS from live registries and gates, never from memory search. `POST
/readiness/qualification-report` writes `data/reports/v3-qualification.{json,md}` from measured
results only; a critical config finding forces NOT QUALIFIED regardless of every other gate.

### Performance corrections

Token estimation no longer allocates megabyte throwaway strings to divide a length by four.
`journal_mode=WAL` is set once at initialization instead of on every connection. Pre-mission
full-database backups are interval-based (`BackupMinIntervalMinutes`, default 6h) instead of
unconditional; migration and auto-apply paths keep unconditional backups. Hard-coded freshness
years ("2025"/"2026") derive from the clock.

Schema 16. All additive; no data deleted or reset. Rollback to v2.25.0 reads the same tables.

## v2.25.0 — V2 closes

The last four items from every roadmap — NORTH_STAR, ROADMAP, REMAINING_WORK — in one closeout
release, plus one gap of our own making. After this, every phase V2 promised either shipped or is
explicitly recorded as trigger-based future work. V3 begins from here, gated by the readiness
evaluation this release ships.

### The Safe Action Engine executor migration

`ActionLifecycle` shipped in v2.14.0 as "the ONE lifecycle every state-changing system shares" —
and the homelab `ActionExecutor`, the only production system that actually changes external state,
never consulted it. Its transitions were guarded by string comparisons that happened to agree with
the lifecycle: agreement by coincidence, not by structure.

`ActionLifecycleBridge` maps the persisted string states onto the canonical machine, and the
executor's refusals now COME FROM it — deciding a decided proposal or executing anything but an
approved one is refused by `ActionLifecycle.Transition`, with the string comparison gone. Unknown
or corrupt states map to a terminal state, so nothing can transition out of them by accident. The
persisted strings themselves are unchanged: every existing route, approval flow and dashboard read
keeps working.

The substantive half: **verification is now the only door to completion.** An action whose
post-execution verify failed used to remain "executed", with the failure buried in the result text
— an unverified outcome counted as success, the exact defect the V3 thresholds forbid. It now
lands canonically `failed` (new additive `lifecycle_state` column; legacy rows read as unknown,
which the readiness gate refuses to count as verified), and produces a `RecoveryOrchestrator`
decision on the audit stream. The decision is a RECOMMENDATION — nothing executes recovery,
because recovery that runs itself is exactly the autonomy V3 has not yet earned. The recovery
context is built only from what the proposal establishes: a rollback NOTE is prose for a human,
not machinery, so the orchestrator can never recommend an "immediate rollback" nothing can perform.

### Automation as a conversation

The NORTH_STAR v2.16.0 "Next:" item, same inversion as Missions: lead with what happened and what
the colony did about it, in plain English, with the raw outcome token behind a hover. The
vocabulary is honest about restraint — a cooldown or cap skip is the engine WORKING, so skips read
as deliberate quiet, not as failures.

### Fault injection becomes a measured series

The V3 threshold reads "repeated fault-injection runs stable" — which is only measurable if runs
are repeated and RECORDED. `ShadowSimulation.RunAll` executed inside tests and nowhere else. It
now runs daily on the shared scheduler (no private timers), and every run persists with a
behaviour fingerprint hashing every scenario's full outcome tuple. Stability = 2+ runs, identical
fingerprints, all passing. Two runs that both pass 16/16 but flip WHICH recommendation a scenario
produced are NOT stable — the pass count would have hidden the drift; the fingerprint does not.
One run is never stable: stability is a property of repetition.

### The V3.0 readiness gate (Phase F)

Not a feature — an evaluation. All ten NORTH_STAR Phase 7 thresholds, evaluated at
`/readiness/json` from two sources that are never conflated: **measured** checks computed from
live data (shadow accuracy vs operator-defined config thresholds, fault-injection stability,
executed-action verification coverage, policy-violation counts) and **attested** checks recorded
by an explicit operator judgment (`POST /readiness/attest`) for the things ANTHILL cannot verify
about itself — that the recovery suites were run and watched, that the kill switch was actually
pulled and execution actually halted. A measured check can never be attested into passing; an
attested check can never be inferred into passing; unmeasured and unattested both read NOT ready.
An attestation may record *not satisfied* — an operator who found the kill switch wanting needs
that on the record more than one who found it working.

The tenth threshold is the certification report itself (`/readiness/certification`): computed as
the conjunction of the other nine, not attestable — letting an operator attest it would let the
report certify itself. An unready system gets a report that says so, never a certificate.

The readiness thresholds are config (`readiness_min_*`) but deliberately NOT editable from the
settings UI: a release gate should not be loosenable from the console it gates.

### The seventh call-site gap — ours, from last release

v2.24.0 shipped `RecordOperatorJudgment` tested and called by nothing: the storage could fill with
recommendations that could never become scoreable in production. `POST /shadow/judge` closes it.
Recorded here because the pattern is this codebase's signature defect and this instance was ours,
caught one release later by the same discipline that caught the other six: assert the call site,
not only the implementation.

## v2.24.0 — Was the goal met, and does shadow mode have a track record?

`MissionVerification` answers whether a verification step ran and returned a pass. That is
necessary and not sufficient. A mission whose goal is "add a CHANGELOG entry" can plan a researcher
and a builder, produce a careful description of the change, have the verifier honestly pass — every
task did exactly what it said — and deliver no file change at all. `completed_verified` then flows
to pheromones, objective EMA, skill credit, and the auto-apply precondition.

`ObjectiveVerification` adds a deliverable check: when a goal plainly asks for a file change, a
file change must have been PROPOSED. Proposed, not applied — ants propose and a human or gated
auto-apply applies, so requiring application would fail every correctly operating mission awaiting
approval.

### Additive by construction

The interim gate remains the floor and is never relaxed. This can only narrow: nothing that fails
today can newly pass because of it, and a test asserts that property across every combination of
goal, verification state, and proposal count.

### Deliberately modest

Deciding "was the goal met" in general is a judgment call, and a model asserting it is precisely
the evidence v2.19.0 stopped accepting. So the only claim made is one that can be checked
deterministically, from a narrow list of verbs that plainly ask for a file change. A goal whose
intent cannot be read falls back to the interim gate alone — an unreadable goal must not fail a
mission that otherwise verified, or work would be punished for the phrasing of its request.

A read-only or verification-only mission never requires a change, since it is forbidden from making
one; requiring it would make the two rules contradict, and this one would win by failing every such
mission.

### Follow-ups from findings, not from opinions

The verifier has always reported "Missing Steps:" — a concrete list of what a mission did NOT do —
and nothing read it. Follow-up objectives came instead from the Strategist's free-form proposal
about what might be worth doing next: a model's opinion, generated on the strength of a success.

`EvidenceFollowUps` reads the findings. Each one becomes an objective traceable to the sentence
that caused it, with **its own budget** (a follow-up must never draw on the parent's remaining
runs, or an objective could extend itself indefinitely by discovering more work) and a **depth
cap**, so findings cannot generate an unbounded objective tree. Only verified missions produce
them: an unverified mission's "missing steps" describe work that may not be missing at all, since
the thing that was supposed to check is what failed.

A bug worth recording, found by simulating the parser against the real verifier text: `StaticVerify`
writes the clean case inline — `Missing Steps: None identified by static verification.` — so the
findings block is empty and the next line is `Risk Notes: ...`. Stopping at "a line containing a
colon" did not fire with no steps collected yet, and the parser produced a follow-up objective
titled "Risk Notes: none" — work invented from a section header, on a verification that found
nothing wrong. It now stops at the verifier's known section headers.

### Shadow Operations gets a production surface

The Shadow line shipped across two releases — a non-executing recommendation engine (v2.17.0) and
a sixteen-scenario fault catalog with a simulation harness (v2.18.0) — with **no table, no
endpoint, and no production call site**. `ShadowOperator.Recommend` was invoked only by its own
tests and the simulator. The sixth instance of this codebase's signature defect, and the largest.

That made Phase E's "live-incident wiring" unbuildable as written. Shadow mode's purpose is to
accumulate a track record: recommend, wait, compare against what the operator actually did, score
the difference. A recommendation that vanishes when the process exits cannot be compared to
anything, so qualification could only ever run over replayed scenarios. Wiring live observation
without storage would have produced a system that appeared to be qualifying itself while measuring
nothing.

Recommendations and outcomes now persist (migration 14) in separate tables, because they arrive at
different times — the recommendation when an incident is observed, the outcome when a human later
says what really happened. Joining them produces a scoreable pair; an unjudged recommendation is
excluded, since it proves nothing and must not move the score in either direction.

`/shadow/json` exposes the scoreboard, timing metrics, and the backlog awaiting operator judgment.
Timing is reported as a **median**, not a mean: one incident left open over a weekend would drag an
average far enough to make the number meaningless. And an empty scoreboard reports "has not
qualified anything" rather than a passing rate — a qualification gate that reads as satisfied
because nothing was measured is the most dangerous failure this subsystem could have.

### Shadow mode observes real incidents

With storage in place, the other half: `LiveIncidentObserver` watches an incident open, records
what shadow mode WOULD have done, and stops.

It never executes — there is no action pathway, and the recorded event says `executed: false`
explicitly. Observation is best-effort and cannot throw: an incident is the worst possible moment
to add a second failure, so a shadow error is logged and the incident proceeds exactly as it would
have.

The proposed operation is derived from the incident's **subject kind**, not from its title. Reading
intent out of prose would make the recommendation a function of how the title happened to be
worded, and the qualification score would then be measuring wording. An unrecognised subject gets
`investigate` — the least invasive operation there is.

`IncidentManager` gained an optional opened-hook rather than a dependency on colony memory, so the
homelab subsystem stays decoupled and the composition root does the wiring. The hook fires only for
a genuinely new incident: `Open` deduplicates by subject, and observing a deduplicated re-open
would inflate the qualification sample with repeats of the same event.

Off by default (`shadow_observation_enabled`). An observer that silently starts writing
recommendations about production incidents should not arrive with an upgrade.

### The qualification scoreboard gets a production caller

`QualificationScoreboard.Compute` takes typed pairs; storage returns rows. So even with a table in
place, the scoreboard could only ever be handed pairs built in memory by the simulator or by its own
tests — the same defect one layer up. `LoadScoreableRecommendations` rehydrates stored history into
the records it scores, and `/shadow/json` computes the scoreboard from that.

Rehydrating exposed a real hole: the first cut of the table stored only the risk **label**.
`PolicyViolations` counts "would have recommended execution while approval was required" — with the
approval flag unpersisted, that count could only ever come back zero, and the safety invariant would
have reported itself permanently satisfied no matter what the recommender did. The risk score,
approval flag and reasons are now persisted, and a test pins the round trip.

Malformed rows are skipped rather than defaulted, because a fabricated pair would move a
qualification metric with no evidence behind it. An unreadable rollback plan resolves to `Escalate`;
there is no no-op recovery action, and a soft default would read as recoverable.

The dashboard panel (Homelab → Automation) shows the diagnosis / prediction / rollback bundle
alongside the scoreboard. Zero scored incidents renders as **"not qualified"**, never as a pass — a
gate that looks satisfied because nothing was measured is the most dangerous failure this subsystem
has. The wire shape is projected explicitly rather than serialised from the records, since no naming
policy is configured and a record would go out PascalCase beside snake_case joined rows.

### Why the colony pheromones looked dead

Not a break in the pheromone system — the v2.20.0 learning reset, showing through a surface that
never explained it.

`ApplyLearningReset` sets every pre-boundary trail to the neutral 0.5 with its success count
restarted. On a real database that means the colony HUD renders a wall of identical 50% bars,
sorting by strength is meaningless, and the canvas field is uniform. The pre-reset values are still
held in each trail's metadata; nothing was lost.

v2.20.0 was supposed to surface the reset wherever rates are read. It reached `/memory/explorer`
and the Strategist's context — and missed `/pheromones/json`, which is exactly what the colony
dashboard reads. That endpoint now carries the reset date and legacy counts beside its data (a new
`ApiJson.Ok(data, meta)` overload, so `data` keeps its shape and every existing client is
unaffected), and the HUD says how many trails are awaiting re-verification.

The planning read had a sharper version of the same problem: with every trail legacy and at zero
successes, `GetTopPheromoneTrails` returns nothing, so planning memory is genuinely empty until a
mission reaches `completed_verified`. That is the intended boundary, but it reported itself as "No
pheromone trail memory found" — indistinguishable from never having had any. It now says how many
trails are held and what releases them.

### The Modules menu actually closes now

Two previous releases "fixed" this and neither worked, because the bug was never in the JavaScript.

`hidden` carries `display:none` from the **user agent** stylesheet only. `.ws-modules` sets
`display:flex`, and an author rule outranks the UA sheet — so `menu.hidden = true` set the
attribute correctly and changed nothing on screen. The v2.19.0 collapsible work and the v2.22.0
focus-mode fix were both correct, and both invisible.

`.ws-modules[hidden] { display: none !important; }` restores it. The minimized-panel tray had the
identical defect — `.ws-tray` also sets `display:flex`, so an empty tray chrome has been sitting on
the canvas whenever the script hid it — and is fixed the same way.

A guard test now checks that **every** element the workspace hides from script has a matching
`[hidden]` rule, since every one of them also sets `display`. The previous tests asserted the
JavaScript said the right thing; none of them could see that the CSS ignored it.

### Activation state is visible

`/colony/registry` reports the activation tier, its explanation, and per-role `admitted_by_tier` /
`gate_open`. Without it the console could show a specialist as unavailable with no way to tell
whether its own rollout flag or the tier was responsible — two different fixes wearing the same
symptom.

### Off by default

`objective_verification_enabled` defaults to false. A change to what counts as success is switched
on deliberately, not delivered by an upgrade. Failures are recorded as
`objective_verification_failed` with the goal, the proposal count, and what was required — never a
silent downgrade.

## v2.23.0 — Observed routes become hypotheses

v2.20.0 gave the archivist's memory candidates a consumer: they became durable events with
provenance. The *procedural* ones went no further. The archivist would observe "this route worked
on a verified mission", write it down, and the V2.12 evaluation model would never hear about it —
both halves of learning present, and not connected.

A verified route now registers as a skill **Candidate**.

### A hypothesis, not evidence

A candidate is usable for nothing. It appears in no plan (`SkillPlanningContext` offers only
Certified and Experimental), confers no permission, and carries no success count. Registration
records **no outcome at all** — standing is earned only through `RecordOutcome` with a promotable
verification bundle, exactly as before. A route observed ten times is still a candidate; treating
an observation as proof is precisely the mistake v2.19.0 exists to correct.

Only `completed_verified` missions propose routes. The archivist already enforces that, and it is
re-checked at registration rather than assumed — a defence that lives in one place is a defence
that moves.

### One route, one skill

Route ids are derived from the route itself, so the same sequence observed across many missions
converges on a single skill accumulating evidence. Per-observation ids would have produced a pile
of single-observation skills that could never reach the success count certification requires:
learning that looks busy and proves nothing.

So the loop is now closed end to end: observe a verified route → register it as a hypothesis → it
earns standing only by being followed and verified again. No step is skipped.

## v2.22.0 — The skills loop closes

v2.21.0 made skills durable and let a certified procedure INFORM a plan. Nothing recorded whether
following one actually worked, so standing could only ever be earned in the shadow simulator — the
loop could read, but not write.

### Provenance, then credit

A task now records the procedure it was planned from (`tasks.skill_id`, migration 13). When the
mission finishes, `CreditSkills` reports the outcome back to the registry and persists the result,
so standing outlives the process that earned it.

The credit rule is the one everything else obeys: **only `completed_verified` counts**. An
unverified mission passes no evidence bundle, which `RecordOutcome` treats as a non-success — a
procedure that cannot be shown to have worked has not been shown to work. It does not reinforce,
but it does not pretend the attempt never happened either: the same asymmetry v2.19.0 established.

### A claimed skill must have been offered

A `skill_id` is honoured only if it names a procedure that appeared in the context the planner was
actually shown. A model cannot invent an id, or name one it was never offered, and have a mission's
success credited to it. The offered set is parsed from the rendered block rather than passed in
separately — two sources of truth with nothing checking they agree is how these drift apart
silently — and a test pins that the formatter and the parser still match.

### An objective that never succeeded is no longer "Completed"

`RecordObjectiveRunOutcome` moved an objective to `Done` the moment `RunCount >= MaxRuns`. An
objective that failed every single attempt therefore ended in exactly the same state as one that
succeeded on its first run: **Done meant the budget ran out, not that the goal was met**, and every
report reading that status turned failure into achievement.

`ObjectiveProgress` now derives achievement from the run history — a run counts only when its
recorded outcome is `completed_verified`, the same standard mission grading, pheromones and skill
credit use. Budget exhaustion with no verified run ends as the new `exhausted_without_success`
rather than `completed_successfully`.

It also fixes the converse, which the old rule got wrong in the other direction: an objective that
achieved its goal early but whose FINAL run failed was labelled a failure. Achievement is not
undone by a later failure, so that is a completion.

Runs recorded before v2.19.0 hold raw statuses and cannot be confirmed as verified, so they fail
closed — the same stance the v2.20.0 learning reset took toward pre-boundary evidence. No new
storage: the evidence was always in `autonomy_runs`.

### Specialist activation is one dial

Six independent booleans plus a master switch meant turning the colony up required knowing which
flags existed and setting the right combination, with nothing to read to answer "what is switched
on?". `activation_tier` is now `core` | `adaptive` | `full`.

It is a **ceiling, not a switch**. `SpecialistGateOpen` requires all three of the master switch, the
tier, and the role's own rollout flag — so raising the tier can never turn a role on by itself, and
every existing gate stays exactly as binding. Narrowing it *can* turn a flagged role off, which is
the point. Unrecognised values fail closed to `core`: a typo must narrow, never widen.

The adaptive set is tester, medic and ui_cartographer — detect, diagnose, and read-only mapping.
Soldier, scribe and archivist are excluded on purpose: they issue security verdicts, write
operator-facing documentation, and write durable memory, none of which the adaptive loop needs and
each of which deserves a separate decision.

**The default is `full`**, which means "defer entirely to the per-role flags" — precisely the
behaviour before this setting existed. Defaulting to `core` would have silently stopped specialists
in every deployment that had already enabled them, on upgrade, with nothing announcing it. The
safety continues to come from the per-role flags, all of which remain off by default.

### Dashboard: the Modules list stops being furniture

The list was already collapsible, but the toggle was built with `aria-expanded` hardcoded to
`'false'`. Every re-render while the menu was open told assistive technology it was collapsed, and
nothing on the control indicated it could be closed again — so it read as permanent. The toggle now
reports the state it is actually in, and shows it (▸ / ▾).

Focus mode now closes the list and keeps it closed. Focus hides every unpinned panel; leaving a
checklist open on top of that is the opposite of focus, and the list would be enumerating panels
that are all hidden anyway. The rule is enforced in `setModulesOpen` as well as at render time, so
no caller can reopen it behind focus mode's back.

### Also fixed

The planner prompt still carried a hardcoded rule line, `assigned_ant must be one of: researcher,
web, file, coder, builder, verifier`, directly contradicting the runtime-derived roster printed
immediately above it. An enabled specialist was listed as available and forbidden in the same
prompt.

## v2.21.0 — Adaptive mission control

Specialists have emitted structured handoffs since v2.19.0 — tester to medic, soldier to builder,
scribe to verifier. The Queen recorded them and acted on none. `HandoffGate.Evaluate` was fully
implemented and fully tested with **zero production call sites**, the same "tested code with no
call site" pattern as v2.14.12, `SanitizeInto`, `/missions/json`, and the archivist's memory
candidates.

A handoff now creates a real follow-up task.

### The bound that was not actually bounding

Every specialist hardcodes `Depth: 1` when it builds a handoff — nothing about a task's position in
a handoff chain ever reaches the ant. Had the orchestrator trusted that number, a handoff from a
dynamically-created task would also have arrived at depth 1, `MaxHandoffDepth` would never have
been reached, and unbounded recursive task creation would have been possible **while the gate
appeared to enforce a limit**.

Depth is therefore derived from the source task's lineage (`HandoffGate.NextDepthFrom`), never from
the handoff's self-report. It is written into the task description, so it survives persistence and
a restart — the tasks table has no depth column, and a bound that resets on restart is not a bound.

### Admission

Every runtime-added task passes the **same** gates as an initial-plan task: `HandoffGate` (depth,
mission task budget, runtime eligibility, contract task-type support, dedupe) and then
`AntRegistry.ValidateTask`, the identical authorization check the planner's own tasks go through.
There is no admission path that skips them. A handoff can only request a role that is *already*
runtime-eligible for a task type its contract *already* supports — it can never grant a capability.

Admitted tasks are persisted immediately and enter the scheduler through `AddDynamicTask`, which
refuses a duplicate id rather than overwriting: `TaskById` deliberately omits duplicated ids so
execution can never be ambiguous, and silently replacing an entry would resurrect that ambiguity.

Rejections are recorded as `handoff_rejected` events with their reason. Nothing is dropped silently.

### Off by default

`handoff_ingestion_enabled` defaults to false. This is the first feature that lets a mission grow
its own task list at runtime, and it ships behind a switch — one config write from off.

### The adaptive decision layer

v2.21.0 let a handoff create a follow-up task. That is one specific way a mission can adapt. This
release adds the component that decides whether a mission should adapt at all, and how.

`AdaptiveMissionController` assesses a mission after a wave of execution and returns a typed
decision: **continue**, **delta-plan**, **repair**, **escalate**, or **finish**. It is deliberately
pure — no database, no model call, no scheduler mutation — so the same mission state always yields
the same decision, and the rules can be tested without running a mission.

### Bounded, because "replanning" is where unbounded task creation hides

The ADR rejected letting the planner re-plan freely on each wave: that is recursive task creation
wearing a different word. So:

- **Replans and repairs have separate counters that do not lend to each other.** A mission out of
  replan generations can still repair a broken step, and one out of repair cycles can still plan a
  missing step. Exhausting one budget never borrows from the other.
- **A wave that changed nothing escalates instead of continuing.** Progress is measured by a
  fingerprint over every task's id and status, ordered so task sequence cannot make a stalled
  mission look like it moved. Two identical fingerprints mean nothing happened.
- **Order of assessment is deliberate.** Terminal state is checked first, so a finished mission is
  never diagnosed as stalled merely because two waves look alike. Then real failures, then unmet
  criteria, then the stall check.

### A failed step is repaired before the plan is rewritten

A failed critical task means one step broke, not that the plan was wrong — repair is focused,
delta planning is not. Only when every task has finished and a criterion is still unmet does the
controller call for a delta plan, and then only for what is missing.

Unmet criteria are computed against the same `MissionVerification` standard the gate applies. An
assessment using a weaker rule than the gate would keep proposing work the gate would never accept,
or stop short of work it requires — including the v2.19.0 case where a verifier ran to completion
and reported failure.

### The loop obeys it

Both execution loops — sequential and parallel — consult the controller after each wave. Sequential
assesses per task; parallel assesses once per completed batch, so simultaneous completions cannot
each trigger their own replan for the same unmet criterion.

A **repair** admits one focused medic task for the failed step, deliberately **non-critical**: a
critical repair task that failed would itself become a new critical failure requesting another
repair — the exact loop the bounds exist to prevent, arriving through the back door. A **delta
plan** admits only the missing verification step, and refuses to duplicate one that already exists,
because a verifier that already ran and reported failure will not pass by being run again.

Every runtime-created task — handoff, repair, or delta — goes through one shared admission helper
that always runs `AntRegistry.ValidateTask`, adds to both the mission and the scheduler, and
persists. A test asserts the scheduler's dynamic-admission API is called from exactly one place, so
"no admission path skips the gates" stays checkable rather than aspirational.

**Budgets are derived by counting the mission's own audit events**, not held in memory. A restart
therefore cannot hand a mission a fresh allowance, the durability requirement comes with no schema
change, and every replan and repair a mission spent is readable in its event log.

### Off by default

`adaptive_mission_control_enabled` defaults to false. It changes when a mission ends, which is not
a behaviour to switch on silently.

### Skills stop being amnesiac

Starting Phase C surfaced a prerequisite nobody had scoped. `SkillRegistry` — the whole V2.12
evaluation model, candidate to certified with automatic symmetric demotion — had **zero production
instantiations and no database table**. It lived in a dictionary and was discarded when the process
exited. Only the shadow simulator ever built one.

That made "skill selection in planning" unbuildable as written: selection would have read an empty
registry at every process start. Wiring it first would have been worse than leaving it alone —
planning decisions taken from state that vanishes.

So skills became durable first. A `skills` table (migration 12), with status restored **as
recorded** rather than recomputed, because recomputing under current policy would let a threshold
change silently re-grade history the evidence no longer backs. Unreadable data fails closed: an
unrecognised status restores as `Candidate`, never `Certified`, and malformed columns degrade to
empty rather than blocking startup.

Then selection: `SkillPlanningContext` renders proven procedures into the planner prompt, from a
registry hydrated out of the database. It offers only Certified and Experimental skills, only
within an environment they were actually proven in, ordered by the evidence behind them — because
"certified" alone cannot distinguish three verified successes from thirty. It does not certify, and
it does not execute. The prompt says outright that a skill is a route to consider rather than a
script, since a planner treating certification as authorisation would bypass the gates every
planned task is required to pass.

Recording outcomes back against the skill that was used needs a skill reference on the task, and
lands next release. The loop reads today; it does not yet write.


## v2.20.0 — Adaptive mission runtime, part 2: the learning reset

v2.19.0 fixed how outcomes are graded going forward. This release deals with what the old rule
left behind: learning state — objective EMAs, pheromone strengths, success counters — accumulated
while structural completion counted as success and nothing required a verifier PASS.

### The one-time reset

On first open of a pre-v2.20 database, `ApplyLearningReset` runs exactly once, at a durable,
backed-up, audited boundary:

- **objective `success_ema` → neutral/unset**, the old value snapshotted into objective metadata
  as `legacy_success_ema` for reporting
- **pheromone trail strength → the neutral 0.5 a fresh trail starts at**; the live success counter
  restarts at 0; pre-reset strength and counts are snapshotted into trail metadata; the trail is
  marked `legacy`
- **failure history preserved in place** — `failure_count` and `consecutive_failures` are evidence
  of what went wrong, not artifacts of the defective success rule
- **raw history untouched** — missions, tasks, events, autonomy runs, approvals, patches, sources,
  agent messages

Safety: an online SQLite backup is taken **before any mutation** and its path recorded; the reset
is idempotent behind a durable meta marker; a `learning_reset` audit event records before/after
counts. Fresh databases just get the marker — the reset is a boundary, not a recurring purge, and
state earned after v2.19 is never touched.

### Legacy semantics

`legacy_unverified` trails are retained for reporting (pruning can never delete them, whatever
thresholds the operator passes) and excluded from planning reads — until a trail records a success
under the corrected rule, at which point it re-enters planning on evidence it actually earned.

### The reset is visible

`/memory/explorer` carries a `learning_reset` block (date + note), trail rows expose the `legacy`
flag, and the Strategist's pheromone context is headed by the reset date — so a success rate
measured after the boundary is never silently compared against one measured before it.

### Memory candidates get their consumer

`ArchivistAnt` has emitted `memory_candidate` artifacts since Stage D-6 — and nothing ingested
them: built, serialised, dropped. The fourth instance of the "tested code with no call site"
pattern (v2.14.12, `SanitizeInto`, `/missions/json`, `HandoffGate.Evaluate`). The Queen now
ingests each well-formed candidate as a durable `memory_candidate` event with provenance.
Deliberately narrow: records are stored, never certified, never fed to planning — `auto_promote`
is recorded, not acted on — and a guard test pins the call site itself, not just the parser.

### Unchanged

Specialist rollout gates stay closed; no additional specialists are activated. `MissionVerification`
remains the interim gate ("did verification run and pass"), with objective-level verification a
later phase. Researcher, Web, File, Coder, Builder stay on the default structured wrapper by
design.

## v2.19.0 — Adaptive mission runtime, part 1: ants declare outcomes, missions require proof

An ant reported its outcome as prose. Nothing parsed that prose. The orchestrator inferred success
from the fact that the ant returned a string at all.

**The chain, end to end.** A specialist built a full structured result — status, handoffs, evidence —
then discarded it through a compatibility adapter that flattened it into text. `RunSingleTask`
marked the task **Complete** unless the ant threw, timed out, or was denied before execution, so a
returned `failed_retryable` was recorded as completed. Mission grading read completed tasks and
produced Complete or Partial. `ColonyDirector` read `partial` as success. Success satisfied the
auto-apply precondition.

**A failing agent could drive an automatic code change.** The same rule fed objective EMA, pheromone
reinforcement and skill confidence, so the learning system was being trained on outcomes nothing had
verified.

### What changed

**Ants declare outcomes; the orchestrator stops inferring them.** `AntExecutionResult` gains
`AntMetrics`, `SucceededWithWarnings` and `Skipped`. All six specialists — tester, medic, soldier,
scribe, archivist, ui_cartographer — return structured results, and the `Compat` adapter that
stringified them into `UI_MAP_JSON` is **deleted**. `TaskOutcomeMapper` completes a task only on
`succeeded` / `succeeded_with_warnings`; unknown or null status fails closed. The scheduler still
owns the retry budget — a retryable failure is *eligible* for retry, not guaranteed one.

**A mission is verified only if its verifier said so.** `VerifierAnt` now declares a verdict through
the new `VerificationVerdict` vocabulary, and `MissionVerification` requires a real pass rather than
a completed verification task. Previously a verifier that ran to completion and reported
"Verification Failed" satisfied the gate. Parsing fails closed: absent, unrecognised, or ambiguous
output is `Unknown`, which is not a pass — the verifier prompt lists all three verdicts on one line,
and a model echoing it must not be read as whichever the parser happened to check first.

**Partial missions reinforce nothing.** `UpdateMissionPheromones` applies a positive delta only for
`completed_verified`. `completed_unverified` and `partial` apply **0.0** — not punished, because
partial work is ambiguous evidence, but never reinforced.

**Workspace Modules checklist is collapsible.** It was persistent and in the way.

### Expect the apparent success rate to drop

This is a **metric correction, not a regression**. Missions that previously graded successful on
structural completion alone now grade `completed_unverified` or `partial`. The prior number measured
"the ant returned a string", not "the work was verified".

### Not in this release

Stage 7 — the migration that resets derived learning state accumulated under the old rule — ships in
**v2.20.0**. Until then, pre-v2.19.0 EMA, pheromone strengths and confidence counters remain active
and were computed under the defective rule. Scope, constraints and the full remaining-work list are
in `docs/ADAPTIVE_RUNTIME_STATUS.md`.

Researcher, Web, File, Coder and Builder were deliberately **not** migrated: the default
`BaseAnt.Execute` wrapper already declares their outcomes correctly, and Verifier was the only core
ant whose text carried a control decision.

### Operator-facing behaviour preserved

Every migrated ant keeps its full narrative as the recorded result — the security review, the
candidate ledger, the documentation, the UI map, the verifier's reasoning and risk notes. A failing
verification deliberately does **not** fail its task, because that path replaces the result with a
one-line reason and would have destroyed exactly the explanation the operator needs.

## v2.18.2 — Hotfix: the mission answer was never in the payload

The Missions conversation showed **"Working — no answer recorded yet"** on every exchange, forever,
including long-finished missions.

**Cause.** `/missions/json` projected six fields:

```csharp
["id"], ["goal"], ["status"], ["success_score"], ["created_at"], ["saved_at"]
```

`final_result` and `user_result` were never in the response. The client read
`m.final_result || m.user_result`, which was therefore always empty, and the thread correctly
concluded there was no answer to show.

This dates from **v2.16.0**, when the conversation view was introduced — the answer has never once
displayed there. It survived the v2.18.1 reconciliation rewrite because that rewrite faithfully
preserved the client's behaviour: both versions read fields the endpoint does not return. The
v2.18.1 tests all passed because their fixtures were built from `final_result`, matching the client
rather than the server.

**Fix.** `/missions/json` now returns `answer` (preferring the synthesized `final_result`, falling
back to the raw `user_result`) plus an `answer_truncated` flag. The value is capped at
`MissionAnswerPreviewChars` (4000) because this endpoint serves up to 100 rows and a raw result can
be an entire diff; the untruncated text is unchanged in `/missions/{id}/report`, which the activity
disclosure already loads. When clipped, the exchange says so and points at Show activity instead of
ending mid-sentence.

**Tests.** The JS fixtures were rebuilt to match the real endpoint shape rather than the client's
assumption — the flaw that let this hide. Three regression tests cover reading the field the server
returns, an arriving answer registering as a change, and the truncation flag; reverting `answerOf`
fails two of them. A C# guard asserts the endpoint contract directly, since no amount of client
testing catches a missing column.

## v2.18.1 — Missions conversation: the three-second poll was destroying the thread

The OpenWebUI-style Missions view shipped in v2.16.0 was unusable in practice. Every symptom traced
to one line.

### Root cause — the whole conversation DOM was rebuilt every three seconds

`pollJobs()` runs on a 3s interval. While Operations → Missions was open it called
`loadMissionThread()`, which ended in:

```js
thread.innerHTML = rows.map(...).join('');
```

That destroyed and recreated every exchange on every poll, **whether or not the data had changed**.
The `/missions/json` response is cached for 10s, so most rebuilds were driven by byte-identical
data — caching the request never prevented the destructive render.

Confirmed consequences:

- Open **Show activity** disclosures snapped shut.
- Already-loaded reports were discarded, along with their `data-loaded` markers.
- Keyboard focus and text selection were lost.
- The live region (`aria-live` on `#ms-thread`) re-announced **all forty exchanges** every poll.
- **Scroll position was lost entirely.** Replacing `innerHTML` clamps `scrollTop` to 0, so the
  `atBottom` check — measured before the replacement — could only ever restore the *bottom*.
  Anyone reading history was thrown to the **top** of the thread every three seconds. This was
  worse than "the scroll jumps": there was no path that preserved position.

### Root cause — a failed activity report could never be retried

```js
det.dataset.loaded = '1';
renderMissionReport(...);
```

The disclosure was latched *before* the request resolved, and `renderMissionReport` swallowed
failures into the panel body and returned `undefined`. A report that timed out was stuck forever:
closing and reopening saw the latch and never retried.

### Root cause — dispatch discarded the directive and hid the error

`dispatchMission` cleared the textarea *before* posting, and `submitMissionGoal` did
`if(!r.success){enableInput(true);return;}` — so a rejected mission lost what the operator had
typed and told them nothing.

### Root cause — overlapping refreshes had no ordering

Page entry and the poll could both have a `/missions/json` request in flight with no generation
token, so a slow earlier response could land after a newer one and overwrite current state.

### The fix

**Incremental, keyed updates.** The thread is now reconciled by mission id: new exchanges are
appended, changed exchanges are patched in place, and rows are removed only when the server stops
returning them. Unchanged data does **no DOM work at all**.

Comparison uses a fingerprint of the seven fields that actually affect rendering — deliberately not
`JSON.stringify(mission)`, which is sensitive to property order and to fields the thread never
shows, and would reintroduce the rebuild it exists to prevent.

**All decision logic is DOM-free** and lives in the new `src/Anthill.Api/Ui/mission-thread.js`:
reconciliation, the activity state machine (`idle → loading → loaded → error`), the stale-response
gate, scroll-follow, announcements, and the composer reducer. This repo has no browser test
harness, so the logic was isolated specifically to be provable — see below.

**Activity state moved out of the DOM** into a store keyed by mission id, so open/loaded state
survives updates. `renderMissionReport` now returns success/failure; the report is marked loaded
only on success, a failure shows the reason with a **Retry** button, and reopening a failed
disclosure retries. Duplicate concurrent report requests are refused. Both callers — the Missions
thread and the Results page — were updated.

**Scroll anchoring** is measured before the update and applied after. Because rows are patched
rather than replaced, position is naturally preserved; the thread follows new content only when
the viewer was already within 96px of the bottom.

**Announcements** moved off `#ms-thread` onto a dedicated visually-hidden `role="status"` region
that speaks one newly finished mission, instead of the entire thread on every poll.

**Dispatch** holds the typed directive until the colony accepts it, restores it and refocuses on
failure, shows the error in a `role="alert"` slot, guards double-submit (button *and* Enter), and
refreshes the thread from source so a new mission appears immediately rather than after the cache
expires. The shared Overview and Colony inputs use the same path and are unaffected.

### Tests

`tests/ui/mission-thread.test.js` — 18 behavioural tests on `node --test`, built into the Node 20
CI already installs. No framework, no `package.json`, no build pipeline. Covers: unchanged data
causing no rebuild, non-displayed fields not counting as changes, single-row updates, appends,
removals, queued→running→complete transitions, open/loaded activity surviving updates, duplicate
report suppression, retry after failure, stale-response rejection, scroll-follow in both
directions, one-result announcements, and dispatch failure restoring a usable composer.

Both are wired into `scripts/validate.ps1` and the CI `ui-integrity` job. Nine C# guards in
`DashboardWorkspaceShellTests` pin what C# can see — chiefly that `thread.innerHTML` and
`rows.map(` have not returned to the render path.

The suite was mutation-checked: reverting the fingerprint to `JSON.stringify` and marking reports
loaded before resolution each cause a specific test to fail.

### Not verified here
The reconciliation logic is proven deterministically, but **no browser walkthrough was performed**
in this environment. The manual scenarios are listed in the PR description and should be run
against a deployed build before this is considered closed.

## v2.18.0 — Shadow Operations Fault-Injection Harness (NORTH_STAR Phase 7, Stage 2)

Stage 2 adds the simulation side of the qualification phase: replayable fault scenarios and a
deterministic harness that scores the shadow recommender's safety. Still additive and offline —
nothing executes.

- **`FaultScenarioCatalog`** (`src/Anthill.Core/Shadow/`): the sixteen fault-injection scenarios the
  phase requires — service crash, health-check false positive, full disk, failed backup, stale DNS
  record, unreachable Proxmox node, VM stuck in transition, firewall rule regression, dependency
  outage, expired credential, rate-limited provider, interrupted mission, failed verification, failed
  rollback, duplicate mission delivery, and malicious prompt injection in logs — each encoded as a
  replayable `ShadowObservation` plus whether approval must be mandatory.
- **`ShadowSimulation.Run` / `RunAll`**: feeds every scenario through `ShadowOperator` and scores two
  invariants per scenario — (1) *Safe*: the recommendation either requires approval or does not
  recommend execution (shadow mode never blindly advises acting), and (2) *ApprovalExpectationMet*:
  a high-risk scenario must come back requiring approval. Returns a `SimulationReport` with per-scenario
  results and the failing set.
- **Proven guarantee**: tests show every scenario is safe with no skills, AND that every high-risk
  scenario STILL requires approval and is never recommended for execution even when a certified,
  high-confidence skill exists for the exact operation — skill confidence can lower a risk score but
  cannot buy a high-risk action out of the approval gate.
- Still ahead in Phase 7: wiring shadow mode to live incidents, timing metrics (MTTD/MTTDiagnose/MTTR),
  a Shadow panel on the dashboard, and the V3.0 release thresholds.

## v2.17.0 — Shadow Operations & Operator Qualification (NORTH_STAR Phase 7, Stage 1)

The qualification gate before V3.0 grants any real authority. Stage 1 ships the recommendation
engine and the scoreboard as an additive, deterministic library — shadow mode observes and advises
but, by construction, cannot execute.

- **`ShadowOperator.Recommend`** (`src/Anthill.Core/Shadow/`): given an observed incident it produces
  the full bundle the phase mandates — diagnosis, proposed action, chosen skill, risk score,
  predicted outcome, verification plan, and rollback plan — and returns it. There is no execution
  path. The bundle is assembled from the already-shipped subsystems rather than fresh judgment:
  `RiskEngine.Score` (v2.14) for the risk assessment, `VerificationPolicy.For` (v2.12) for the
  verification plan, the `SkillRegistry` (v2.13) for the chosen skill and its derived confidence, and
  `RecoveryOrchestrator.Decide` (v2.14) for the rollback plan — so a recommendation is reproducible
  and consults no model at decision time. Outcome prediction is deterministic: approval-required
  dominates; an operation with no proven skill predicts failure; otherwise skill confidence and the
  risk score set the expectation. A high-risk operation is always flagged for approval and never
  marked as recommend-to-execute.
- **`QualificationScoreboard.Compute`**: turns recommendation/operator-outcome pairs into the core
  reliability rates — diagnosis precision and recall, action-selection accuracy, unnecessary-action
  rate, and predicted-success accuracy — plus two safety counters (policy violations, unverified
  success claims) that must stay zero. Every rate is division-guarded, so an empty or partial sample
  yields zeros rather than throwing or fabricating a perfect score. Ground truth comes from the
  operator; ANTHILL never scores its own success.
- Tests: high-risk → needs-approval + never-recommend; proven skill on a low-risk op → predicted
  success + would-recommend; unproven op → predicted failure; scoreboard rates computed on a fixed
  sample; empty sample is all-zero.
- Still ahead in Phase 7: wiring shadow mode to live incidents, the fault-injection scenario harness
  (service crash, stale DNS, expired credential, prompt injection in logs, …), timing metrics
  (MTTD/MTTDiagnose/MTTR), and the V3.0 release thresholds.

## v2.16.0 — Missions read like a conversation

### Added — a plain-English answer, not the winning task's raw output

`ComposeUserResult` has always returned the single best task's output *verbatim*, so the "answer"
could be JSON, a diff, or a verbose dump depending on which ant happened to win. Mission completion
now writes a concise plain-English answer into `FinalResult`.

Nothing is replaced: `UserResult` keeps the raw best-task output and `DebugResult` keeps the full
trace, so the detail behind an answer is always still there. No schema change — the API already
carried all three.

Every failure path falls back to the previous behaviour, because a mission must never end up
answerless: no router, `answer_synthesis_enabled=false`, an answer already short enough to be prose
(under 320 characters), a provider that is down, an `ERROR:` response, an empty response, or a
thrown exception. Those rules are pure functions with eight tests, proven without a live model.

Synthesis routes under a **`scribe`** role, which resolves through the normal route table, so answer
writing can be pointed at a cheaper model in Settings → Model Routing without touching code. The
prompt constrains the model to rephrase what the colony produced — it may not add findings, and a
failed or partial mission must be reported as such rather than narrated as a success.

### Changed — Operations → Missions is a conversation

Directive in at the bottom, answers in a scrolling thread above, and everything the colony did —
per-task trace, events, changes, verification — behind **one disclosure per response**.

Activity loads on first expand only, so a forty-mission thread does not fetch forty reports, and the
detail view reuses `renderMissionReport` verbatim rather than growing a second implementation that
can drift from the Results page. The thread is a polite live region and only auto-scrolls when you
are already at the bottom, so reading history is never interrupted by an arriving answer.

### Changed — chamber view no longer muddies

Ants inside a chamber overlapped badly. Three things were compounding: roles sat on a ring capped at
46px, their workers were placed 72px out, and the worker bearing came from `colonyAngleFor()` —
which in chamber mode derives from the *chamber's* index and is therefore identical for every role
in it. Each role's workers landed on its neighbour.

Every role now owns an angular **sector** of its chamber, with its workers on an arc inside that
sector, so cross-role collision is geometrically impossible rather than merely unlikely. Both radii
are derived from the arc length actually required, so a five-role chamber or a four-worker role
grows instead of packing tighter. Measured across all seven chambers: zero overlapping node pairs,
15px tightest gap, largest chamber 136px against 342px of centre spacing.

An intermediate attempt that only enlarged the radii took overlapping pairs from 9 to **24** — it is
the shared bearing, not the spacing, that caused the smudge.

### Changed — default dashboard layout

Colony Health / Colony Vitals / Missions / Jobs down the left, Agent Inspector / Patch Activity /
Live Telemetry down the right, System Core and Objectives floating low, and the centre of the map
kept clear. The other six panels start hidden, one click away in Modules. Defaults apply on first
run only — an existing saved layout wins until **Reset layout**.

### Queued
Bringing the same conversational treatment to the Automation tab.

## v2.15.3 — Hotfix: the status bar and mission directive box were invisible

v2.15.2 shipped with two pieces of primary chrome hidden: the ANTHILL status bar and the mission
directive box — the thing you type a mission into.

**Cause.** The rule that takes the classic Overview out of flow was an id allow-list:

```css
#page-overview.ws-active > *:not(#ws-root):not(#ws-topology){display:none !important;}
```

v2.15.2 then added `#ws-topbar` and `#ws-bottombar` as direct children of the same element, so both
matched the rule and were set to `display:none`. The `#ws-topbar > #tb-overview{display:block}`
rule could not rescue it — a child of a hidden parent does not render.

Both halves read correctly in isolation. The defect existed only in the relationship between them,
which is why nothing caught it: an allow-list that must be updated every time a sibling is added is
a latch waiting to catch the next one.

**Fix.** The rule excludes by class instead. Any workspace layer opts out by carrying `.ws-layer`,
so adding another cannot reintroduce this. `#ws-root` stays matched by id because the workspace
module owns and rewrites its `className` on every render.

`EveryWorkspaceLayer_SurvivesTheClassicPageHideRule` now parses what `initDashboardWorkspace`
attaches to the page and asserts each one carries the class — checking the relationship, not the
two rules separately.

## v2.15.2 — Chrome positioning: one missing containing block

Three reported symptoms, one root cause.

### Fixed — the workspace layers had no containing block

`#ws-topology`, `#ws-topbar` and the panel chrome are `position: absolute`, but neither `#main-area`
nor `.page` carries a `position`. Absolute elements with no positioned ancestor resolve against the
**initial containing block** — the whole viewport — so the entire workspace was laid out against the
window rather than the content area. It therefore rendered *underneath the nav sidebar* and past the
bottom edge.

That single fact produced everything reported:

- the caste legend and learning-signals panel were cut off on the left (sitting behind the sidebar),
- the colony view bar was clipped so it began mid-row — Command / Expanded / Active were off-screen,
- the mission directive box was pushed below the fold, leaving only a sliver.

`#page-overview.ws-active` is now `position: relative`. Nothing else about the layout changed.

### Fixed — the mission directive box could be covered

It lived inside the canvas layer, beneath the floating panel layer, so a panel could sit on top of
it. Since it is how work is started, it now gets its own bar pinned above the panel layer — the
existing element re-parented, not duplicated. Bottom-anchored topology overlays and the Overlays
button offset to clear it.

### Fixed — the toolbar hid behind the status bar

With the status bar correctly positioned it occupies the top 52px, and the workspace toolbar sat at
`top: 8px` with a *lower* z-index — so it did not overlap visibly, it disappeared. The fixed chrome
now has an explicit vertical budget: status bar 0–52, toolbar 58–90, Modules menu from 94, topology
overlay slots from 96, mission bar at the bottom. The offset is applied to every top slot rather
than to the view bar alone, so an overlay you re-anchor cannot land under the chrome either.

### Changed — overlay control moved into the Modules menu

Topology overlays (view controls, caste legend, learning signals, interaction hints) are now shown,
hidden, and re-anchored from the same right-hand Modules menu that lists the panels, instead of a
separate "Overlays" button pinned to the canvas. Two surfaces controlling what is on screen is how
they drift apart.

The standalone button existed to guarantee that hiding every overlay stayed recoverable; the Modules
menu lives in the always-present workspace toolbar, so that property is preserved. A hidden
overlay's anchor control is disabled rather than silently inert, since there is nothing to anchor.

`app.js` keeps ownership of overlay state and exposes `window.AnthillTopologyOverlays`; the
workspace module reads and writes through it rather than holding a second copy.

### Fixed — a whole UI file was outside the guards

`UiSource()` — the helper every UI regression guard reads — covered `index.html` and `app.js` only.
`dashboard-workspace.js` was never scanned, simply because it did not exist when the helper was
written; by now it builds most of the console's chrome. Orphaned element lookups and duplicate ids
in it were invisible to CI. It is included now, with the workspace's runtime-created ids
(`ws-panel-layer`, `ws-guides`, `ws-snapzones`, `ws-modules`, `ws-topbar`, `ws-bottombar`)
allow-listed for what they are.

### Added — guards
`WorkspaceLayers_HaveAContainingBlock`, `FixedChromeBands_DoNotOverlap`, and
`MissionDirective_IsAboveThePanelLayer`, and `TopologyOverlays_AreControlledFromTheModulesMenu`.
The first is the one worth having: a missing `position: relative` is invisible when reading any
individual rule, and its symptoms look like four unrelated clipping bugs.

## v2.15.1 — The dashboard is the colony, and it behaves like one

Operator feedback on v2.15.0: the workspace was a good starting point but buggy as a dashboard —
the map only filled the top of the page, half the console was still a non-modular scrolling section
underneath it, the colony view bar was cut off, the status bar was stranded mid-page, and dropping a
panel on an edge stretched it "super long instead of into a confined space". All of that is fixed.

### Fixed — the topology now fills the whole page

`#page-overview` is no longer a scrolling document when the workspace is live. The map occupies the
entire viewport and panels float over it.

The root cause of the "second dashboard" below the map: v2.15.0 hid `#ov2-grid` and nothing else,
while the page also contained a telemetry bar and **six** further `hud-panel` cards in normal flow.
Those are now taken out of flow by a single rule — `#page-overview.ws-active > *:not(#ws-root)` —
rather than an enumerated list, because enumerating is exactly how six cards got missed.

### Added — the six remaining cards are panels

Colony Vitals, Recent Missions, Patch Activity, Objectives, Recent Jobs and Live Telemetry are now
full workspace panels: draggable, resizable, collapsible, and groupable into tabs like everything
else. Fifteen panels total, all re-parenting their existing body elements, so there is still exactly
one renderer per card.

They start hidden so the colony canvas is the whole page until you place what you want on it. Every
one is a click away in the Modules menu.

### Changed — docking replaced by snapping

v2.15.0's edge rails are removed entirely: no rails, no `Dock left/right/top/bottom` menu entries,
no rail resize handles. Dragging to an edge or corner now snaps the panel into a bounded region —
left/right take a half, top/bottom take a half, corners take a quadrant. Corners are tested before
edges, since a corner sits inside both edge bands and aiming at one is deliberate.

**Existing docked layouts migrate rather than break.** `SanitizePanel` converts any panel saved as
docked into a floating panel snapped to the same edge, then clears the legacy dock fields. The dock
properties stay in the schema purely so v2.15.0 documents keep deserializing.

Snap geometry lives in `DashboardWorkspaceState.SnapRegion` and is exercised by the migration, so it
is real code with a real call site — not another tested function nothing invokes. Halves cover an
odd viewport with no dead strip, the four quadrants tile exactly, and a viewport too small to halve
still yields a usable panel instead of a zero-sized one.

### Fixed — the colony view bar was clipped

It rendered starting at "Handoffs" with Command / Expanded / Active / Chambers cut off the left
edge, because the overlay anchor slot capped width at 260px. Width now belongs to the overlays that
actually need constraining — the legend and signals panels — and the view bar is explicitly
`nowrap`.

### Fixed — the status bar sat mid-page

The ANTHILL bar (colony online, tasks, success rate, active ants, approvals, health, search) is
pinned to the top of the dashboard, above the colony view controls, by re-parenting the existing
element rather than duplicating it. Top-anchored topology overlays now clear it instead of hiding
beneath it.

### Test note
The v2.15.0 docking tests were removed along with the feature they covered, and replaced by
equivalent snapping tests — migration, tiling, minimums, and unknown-zone handling. No test was
weakened to obtain a green build.

## v2.15.0 — The topology-first dashboard, complete and on by default

The console track that began at v2.14.2 is finished. The live colony topology is the persistent
canvas of the Dashboard, and the panels above it can be moved, resized, grouped into tabs, or docked
to an edge.

### Changed — `dashboard_workspace_enabled` now defaults to ON

This is the release where the workspace becomes the console. It is still a kill switch, not a
vestige: setting it false restores the classic Overview grid and the standalone Colony page
immediately, with no migration and no data loss — saved layouts simply go unread.

The config property is now `bool?` on purpose. A plain bool cannot distinguish "this config predates
the setting" from "the operator turned it off", so an upgrade would have silently re-enabled the
workspace for someone who had deliberately disabled it. Null resolves to the new default; an
explicit `false` is always respected, and the resolved value is written back so it becomes explicit
on the next save.

`DashboardWorkspaceShellTests.FeatureFlag_...` inverts to assert default-ON. That is a requested
behaviour change, not a test relaxed for a green build, so the guard was strengthened rather than
dropped: the flag must still be exposed to the client, still be settable, and still be a real
rollback path.

### Added — tab groups (Stage 4)

Drag a panel onto another panel's header to stack them into tabs. Reorder, detach, and switch tabs
with the keyboard; active tab persists.

Groups are addressed internally as `g:<id>`, which means they reuse the entire existing drag,
resize, snap-guide and z-order implementation instead of getting a parallel one. **Only the active
tab renders** — inactive tabs are not merely hidden, so grouping panels reduces polling instead of
multiplying it, and the `refreshPolicy:'visible'` contract keeps holding. The client mirrors the
server rule that a group below two members dissolves, so you never stare at a one-tab stack waiting
for a reload to repair it.

### Added — docking (previously deferred)

Panels dock to any of the four edges with drop-zone previews, drag back out to refloat, and rails
resize as a unit. Docking was deferred in the original plan because hand-rolled dock geometry is
where window managers accumulate bugs; it shipped because **the geometry that matters lives in
tested C#** and the client does almost none of it — rails lay out with flexbox and the only stored
number is `dock_size`.

Two invariants, both enforced server-side so a hand-edited `ui_state.json` cannot bypass them:

- **A dock rail may not exceed 60% of its axis** (`MaxDockFraction`). The premise of this dashboard
  is that the map is the persistent background; a rail reaching 100% would let it be buried with no
  obvious way back.
- **Opposing rails are clamped together, not just individually.** Per-edge clamping alone still
  allows left 60% + right 60% = 120%, overlapping the rails and erasing the map. Over-budget pairs
  scale down proportionally so relative sizing survives. Found during the accessibility audit —
  precisely the class of bug the deferral was worried about.

A panel can no longer be docked and tabbed simultaneously; it would render in two places.

### Fixed — `ui_state.json` had two racing writers (Stage 8)

`saveUiState` in app.js and `save()` in dashboard-workspace.js were independent debounced
read-modify-write cycles on the same document, on different timers. Each preserved the other's keys
as of v2.14.14, but a panel drag landing inside an ant rename's window read a stale document and the
later PUT discarded the earlier change. Both now register mutators with a single `UiStateWriter`:
one debounce, one read, one write, chained so flushes cannot interleave, plus a `pagehide` flush.

The lifecycle audit that accompanied it came back clean — `initDashboardWorkspace` is boot-guarded,
`W.register` dedupes by id, and the multiple delegated listeners are distinct handlers rather than
duplicates.

### Added — default layout, responsive and accessibility pass (Stage 10)

The first-run layout keeps the centre of the map clear: five panels on the left and right edges,
four secondary panels available but not shown, one click away in Modules. Below the 900px breakpoint
side rails become full-width strips, edge drop zones give way to the menu, and touch targets grow —
against a *separate* server-side placement profile, so a phone visit cannot overwrite the desktop
arrangement. Escape always exits focus mode, tab groups follow the WAI-ARIA tabs pattern with roving
tabindex, and every drag-only capability has a menu equivalent.

### Fixed — documentation claimed guarantees nothing enforced

NORTH_STAR §9 stated that automated tests verify "required canonical documents exist". No such test
existed, and **five of the nine documents it listed had never been created** — `TOOLS.md`,
`VERIFICATION.md`, `SKILLS.md`, `RECOVERY.md`, `QUALIFICATION.md`. The list now names only real
files, `DocsConsistencyTests` enforces both that and that the roadmap docs mention the shipping
version, and NORTH_STAR/ROADMAP are backfilled through v2.15.0.

Remaining documentation debt is recorded rather than papered over: procedural skills (v2.13.0) has
no dedicated document, and V3 qualification lives only in NORTH_STAR §6.

### Note on test maintenance
Three test failures during this release were fixed-length source slices (`Math.Min(js.Length, start
+ 600)`) that stopped covering their target function as code was added — the assertion then passed
or failed on where the window landed rather than on the behaviour it named. All of them are now
brace-matched via a `BodyOf` helper, and several got stricter in the process. No test was weakened
to obtain a green build.

## v2.14.15 — Persistent topology, nine panels, and readable chambers

### Fixed — standby chambers looked broken rather than idle

Network Watch appeared unlit next to every other chamber. Not a Network Watch bug: its three roles
(`network_scout`, `health`, `security_scout`) are all declared `Executable: false`, so
`chamberStats` classified the whole chamber as `dormant`, which drew at stroke alpha **0.12** and
fill **0.012** against **0.30 / 0.035** for everything else. Any all-non-executable chamber did the
same — Infrastructure Works and Memory Vault included.

Standby is now a **steady, clearly visible** state (stroke 0.26 / fill 0.026), dimmer and cooler
than a working chamber but unmistakably present, and still labelled `standby` in its summary line.

### Changed — chamber pulse means something

Active chambers now breathe noticeably harder (amplitude 0.06 → 0.15, and 0.20 under Motion=High,
with a slightly faster period and a thicker ring). Idle and standby chambers are deliberately
**steady**: a pulsing ring means "work is happening here", so pulsing everything would make it say
nothing. Motion=Off still stops all of it.

### Fixed — the caste legend silently hid eight ants

`renderColonyLegend` capped itself at `.slice(0,15)`, which dropped every homelab ant — inventory,
network_scout, health, proxmox, storage, backup, security_scout, change_archivist — from the legend
while they were still drawn on the canvas. The legend now lists the full registry and scrolls; as of
v2.14.14 it is a hideable overlay, so it can afford to be complete.

### Added — the Agent Inspector and Jobs list are workspace panels

Two more panels registered against the same re-parenting pattern as the other seven, so there is
still exactly one renderer per card. This is the prerequisite for the change below: until the
dashboard could host the inspector and the jobs list, "the topology lives on the dashboard" was
only half true, because inspecting an ant still meant leaving it.

### Added — the topology is genuinely persistent (Stage 9 groundwork)

With the workspace live, `/colony/topology` now resolves to the Dashboard, which holds the topology,
the inspector, the jobs list, and the mission bar. The canvas stays mounted in one place for the
whole session instead of being re-parented on every navigation.

The redirect is keyed off the topology layer **existing**, not off the config flag — so if the
workspace fails to initialise for any reason, the Colony route behaves exactly as it always has.
With `dashboard_workspace_enabled` off, none of this engages.

## v2.14.14 — Topology overlays, and the layout validator that was never called

### Fixed — `DashboardWorkspaceState` was dead code in the running system

Stage 1 (v2.14.2) shipped a server-side workspace validator with 20 unit tests and the explicit
decision that *"layout correctness lives in C#"*. It was never wired in. `GET /ui/state` returned
the raw file and `PUT /ui/state` persisted the request body verbatim — `SanitizeInto` was called
only from the test project. Validation, clamping, off-screen recovery, and desktop/compact profile
isolation were all inert while every test stayed green.

This is the same shape as the v2.14.12 defect: well-tested code with no call site. Both endpoints
now run the sanitizer, the canonical panel and overlay ids move into
`DashboardWorkspaceState.KnownPanelIds` / `KnownOverlayIds`, and a guard asserts the handlers keep
calling it.

The unit tests had also drifted: they validated against `mission-command` and `pending-approvals`,
which do not exist, while missing five ids that do. Those fixture ids are deliberately arbitrary —
they prove the repair logic is id-agnostic — so they stay, now documented as such, with a separate
guard proving the *production* list matches what the client registers.

### Fixed — every ant rename or drag silently deleted the panel layout

`ui_state.json` is a whole-document store. `dashboard-workspace.js` writes it correctly with
read-modify-write, but `saveUiState()` in app.js posted a literal containing only its own six keys.
Because `dashboard_workspace` was simply absent from that payload, **every ant rename, ant drag,
chamber drag, and inspector save wiped the operator's entire panel arrangement.**

`saveUiState` now does read-modify-write like the workspace module. Residual race — two debounced
writers, last PUT wins — is unchanged and is written into Stage 8's scope rather than papered over.

This is the second partial-write-to-a-whole-document bug in two releases, after `model_routes`.

### Added — topology overlays (Stage 7)

The canvas chrome is now independently hideable and re-anchorable: **view controls**, **caste
legend**, **learning signals**, and **interaction hints**. Each can be toggled and moved between six
anchors, with state persisted in `dashboard_workspace.topology_overlays` and validated server-side
(unknown ids dropped, unknown anchors reset).

Overlays are re-parented into six anchor **slots** rather than positioned individually, so two
overlays sharing an anchor stack in flex flow instead of drawing on top of each other — which is
exactly what the legend and signals panel do by default, preserving how they have always looked.

The **Overlays** button is deliberately not itself an overlay: if it could be hidden, hiding
everything would be unrecoverable without hand-editing `ui_state.json`. The menu is the non-drag
equivalent for every overlay capability, hidden overlays get `aria-hidden` so they leave the tab
order, and Escape closes the menu and returns focus to the button.

The **inspector is deferred** to Stage 9. On the Colony page it is a sidebar card, not canvas
chrome; anchoring it belongs with route consolidation, when that layout goes away.

### Added — regression guards
- `Workspace_SanitizerIsWiredIntoTheUiStateEndpoints`: both handlers must call the sanitizer,
  checked inside each handler body so a call elsewhere in the file cannot satisfy it.
- `Workspace_CanonicalIdsMatchTheClientRegistrations`: the C# panel and overlay id lists must equal
  what `app.js` registers, or `Sanitize()` deletes real panels as unknown and invents placements
  for panels with no renderer.

## v2.14.13 — Editable Ant Inspector, topology as the dashboard canvas, UI hardening

Three pieces of work in one release: a hardening pass over the console, the Ant Inspector becoming
editable, and the live topology becoming the Dashboard's background layer.

### Added — editable Ant Inspector (Stage 3e)

Clicking an ant now opens a **Configure** section inside the *existing* right-side Agent Inspector
card — no second panel. It edits exactly three things, each through a persistence path that already
existed:

| Field | Writes to |
|---|---|
| Display name | `uiState.castes[role].name` — the same key the double-click rename uses |
| Accent colour | `uiState.castes[role].color`, via `casteColor`/`applyUiState` |
| Model route | `POST /settings {model_routes}` with normal auth — no new write path |

It also surfaces information the inspector never had: chamber, runtime status and unavailability
reason, planner eligibility, live pheromone strength, and the workspace path allowlists that
`AntRoleDefinition` has always carried but nothing rendered.

The inspector never grants a capability and never edits permissions, tool allowlists, or path
allowlists — those are contract-owned and display-only. Ants with no model route (control plane,
or not executable) get a short explanation instead of a dead disabled control.

Two honest deviations from the queued spec: execution-contract detail (task types, required
capabilities, risk class, compensation) is **not** shown because `/colony/registry` does not expose
it, and name/colour are caste-level because workers derive both from their caste in `applyUiState`.

### Fixed — `POST /settings` silently reset model routes

`AnthillRuntime.ApplySettingsUpdate` does `dict[key] = value`, so posting `model_routes` **replaces
the entire route map** rather than merging into it. The Ant Config page only avoided data loss by
coincidence — it posted every caste it rendered — and still dropped any route it omitted, including
`strategist`, `fallback`, and any caste with no model selected, silently reverting them to the
profile default.

Both writers now merge into a shared `modelRoutes` cache and post the whole map. Found while wiring
the inspector's model control, which would have hit it on every single save.

### Fixed — operator- and model-controlled strings reached markup unescaped

`showInspector` interpolated `n.label`, `n.role`, `n.parent`, `n.colony`, and mission-graph task
fields (`title`, `status`, `task_type`, assignee) into `innerHTML` without escaping, and pasted
`n.color` straight into `style=""`. Ant names come from operator input and task titles come from
model output; `UiStateStore` round-trips both verbatim by design ("the UI owns the shape"), which
makes the client the only place they can be sanitised.

All of them now go through `escapeHtml`, and colours through a new `cssColor()` that accepts only a
hex literal, a `var(--token)`, or a bare colour keyword. The console's CSP (`script-src 'self'`,
no unsafe-inline) blocks the classic payload, but CSP is a second line of defence, not a substitute
for escaping — markup injection does not require script execution.

### Added — topology as the dashboard canvas (Stage 6)

With `dashboard_workspace_enabled` on, the Dashboard now renders the live colony topology full-bleed
behind its floating panels. This is done by **re-parenting the single `#colony-canvas-area`** between
the Colony page and a new `#ws-topology` layer — not by adding a second renderer. One canvas, one
render loop, one polling path, and every existing interaction (ant drag, chamber drag, pan, zoom,
inspector) keeps working because it is literally the same element with the same listeners.

The canvas takes its size from its container, so it is re-measured on the frame *after* each move;
measuring during the move yields 0×0 and collapses every ant onto the origin. The Colony page
reclaims the canvas whenever it is opened, so that route never goes blank — route consolidation
stays Stage 9's job.

`.ws-root` is now `pointer-events:none` with panels and toolbar opting back in. Without that the
workspace root is a full-page invisible shield over a map you can no longer interact with.

### Added — regression guards
- `UiIntegrity_TopologyHasOneRendererAndPassesPointersThrough`: exactly one `<canvas>`, exactly one
  render-loop bootstrap, and `.ws-root` must not capture pointer events.
- `UiIntegrity_OperatorControlledFieldsAreEscapedBeforeMarkup`: node fields may not be interpolated
  into markup without `escapeHtml`/`cssColor`.
- `UiIntegrity_ColonyAndChamberSymbolsAreDeclared` widened to cover `topology*` and `overlay*`.

### Audit note
A whole-file undeclared-identifier scan of `app.js` and `dashboard-workspace.js` produced 22
candidates; all 22 triaged as false positives (every one properly declared). Without a real JS
parser — which the no-build-system constraint rules out — a whole-file version of that scan is not
trustworthy enough to gate a build, so the shipped guard stays prefix-scoped, where it was verified
to catch exactly the six real v2.14.12 defects with zero false positives.

### Known gap
The render loop suppresses drawing when the tab is backgrounded or the canvas measures zero (its
page is `display:none`). Occlusion-based throttling — "mostly covered by panels" — is not
implemented and is not claimed.

## v2.14.12 — Hotfix: the live colony canvas rendered no ants

Fixes the Colony topology showing only faint edges radiating from an empty centre, with no ants
visible, no ants draggable, no chambers, and the Chambers view collapsed to a single line.

**Root cause — call sites shipped without their definitions.** Releases v2.14.5 through v2.14.10
added code that *reads* colony map state and chamber geometry, but the declarations were never
actually written into `app.js`:

| Referenced by | Symbol | Existed? |
|---|---|---|
| `loop()` | `colonyPheromones` | no |
| `drawChambers`, `drawNode` | `colonyLabels` | no |
| `drawChambers`, `maybeSpawn` | `colonyMotion` | no |
| `buildNodes()` | `chamberCentres` | no |
| `drawChambers()` | `chamberRadius` | no |
| `mousedown` / `mousemove` / `dblclick` | `chamberAt` | no |
| `mousemove` | `moveChamber` | no |
| `mouseup` | `persistChamber` | no |
| viewbar buttons | `colonyResetView`, `colonyResetLayout` | no |
| viewbar selects | `loadColonyPrefs`, `setColonyPref` | no |

The visible symptoms follow exactly from the evaluation order:

- `loop()` threw a `ReferenceError` on `colonyPheromones` **after** `drawBg()`, `drawChambers()`, and
  `edges.forEach(...)` but **before** `nodes.forEach(...)`. Structural edges drew; ants never did,
  activity never decayed, and particles never advanced.
- `buildNodes()` threw on `chamberCentres` in chamber mode after pushing only Queen and Director —
  one node pair, one edge, hence "a single white line" on the Chambers tab.
- The Motion / Labels / Pheromones selects and the reset View / Layout buttons were inert markup
  with no listener bound.

**Why CI did not catch it.** An undeclared identifier is a runtime `ReferenceError`, not a syntax
error, so `node --check` passed and every existing UI guard passed. Those guards checked element
ids, encoding, and duplicate ids — none checked that a symbol a script *uses* is a symbol the
script *defines*.

### Fixed
- Declared the three map preferences (`colonyMotion`, `colonyLabels`, `colonyPheromones`) with
  validated setters, and honoured `prefers-reduced-motion` as a floor that a stored preference
  cannot override upward.
- Implemented the chamber geometry: `chamberCentres` (Queen's Core holds the centre as the control
  plane, the rest ring around it), `chamberRadius`, `chamberAt` (tightest ring wins, so overlaps
  resolve predictably), `moveChamber` (carries member ants), and `persistChamber` (stores the drag
  as a `{dx,dy}` offset against the computed base, so a chamber survives resize and reordering).
- Implemented `colonyResetView` (eases the camera to its targets rather than snapping) and
  `colonyResetLayout` (drops dragged ant positions and chamber offsets only — never touches caste
  names, colours, or model routes).
- Wired the viewbar controls through the existing single `data-*` dispatch path, CSP-safe.
- `Pheromones = Active` now actually narrows the field to ants working right now. The third option
  had been accepted by the markup and then ignored, behaving identically to `All`.

### Added — guards for this bug class
- `UiIntegrity_ColonyAndChamberSymbolsAreDeclared`: tokenizes `app.js` (stripping strings, comments,
  and regex literals so prose, URLs, and the apostrophe in `"Queen's Core"` cannot fool it), then
  asserts every `colony*`/`chamber*`-prefixed reference has a declaration. Verified in both
  directions — zero findings on the fixed file, exactly the six real defects on the broken one.
- `UiIntegrity_ColonyCanvasControlsHaveHandlers`: every `data-colonyact` / `data-colonypref` value in
  `index.html` must be named by a handler in `app.js`. Under `script-src 'self'` a data-attribute
  dispatch is a control's only route to behaviour, so inert markup is always a bug.

### Process note
Two v2.14.x hotfixes in a row came from edits whose *effects* were never verified — a CSS sweep that
removed a live rule, and this: patches reported as applied that had silently matched nothing. The
lesson carried into the guards above is that "the file parses" and "the file works" are different
claims, and only the second one matters.

## v2.14.11 — Hotfix: colony topology layout

The colony page rendered as a black void with the canvas squeezed into a narrow strip and the ants
collapsed toward a single point.

Cause: the v2.14.9 CSS cleanup swept every selector mentioning `cmap-mode`/`#cmap2`, which caught
`#page-colony:not(.cmap-mode) #tb-colony{display:none;}` — a rule that styled a **live** element
while merely *mentioning* the dead class. `#page-colony` is `flex-direction:row`, so the unhidden
telemetry bar became a ~900px column, starving the canvas of width; `resize()` then computed a tiny
layout radius and every ant clustered at the centre.

- Restored as an unconditional `#page-colony #tb-colony{display:none;}` (`.cmap-mode` no longer
  exists, so the guard clause is obsolete). The colony page has never shown the telemetry bar — the
  canvas carries its own HUD.
- Audited the other seven `#page-colony` rules the sweep removed: all referenced `.cmap-mode` or
  `#cmap2` and were genuinely dead.

Process note: a line-based regex sweep is the wrong tool for CSS, because a selector can reference a
retired class while still styling a live element — and a brace-balance check (which I did run) only
catches syntax damage, not a valid stylesheet missing a needed rule. Remaining cleanup stages diff
removed selectors against live element ids before deleting.

## v2.14.10 — Chamber renaming

- **Double-click a chamber to rename it**, exactly like renaming an ant — same popover, same
  Enter/Escape behaviour. Ant hit-testing still wins, so double-clicking an ant inside a chamber
  renames the ant.
- The **canonical chamber name never changes**: renaming stores a label in a separate
  `chamberNames` map, so role membership, drag offsets, and per-chamber stats keep working off the
  built-in identity. Clearing the field (or typing the original) restores the default name.
- Persists with the rest of the console layout in `ui_state.json`.

Deferred with a written spec rather than rushed: the **editable Ant Inspector side panel** (click an
ant → permissions, contract, workers, activity, with inline name/colour/model editing). It is
specified in `docs/DASHBOARD_WORKSPACE.md` under "Queued: editable Ant Inspector side panel",
including which persistence path each editable field must use — notably that per-role **model**
selection belongs to model-routing config and must go through the existing settings endpoint with
its normal auth, not a new write path. Building it half-way would have meant either a control that
silently does nothing or a bypass of routing config; both are worse than waiting a release.

## v2.14.9 — Seven functional chambers

Live feedback on v2.14.5: chambers were derived from each role's `Colony` string, which produced
~14 near-singleton chambers (a chamber for the verifier, one for the soldier, one for the medic…).
Replaced with a fixed functional taxonomy of **seven** chambers:

| Chamber | Ants |
|---|---|
| Queen's Core | Queen, Director, Planner, Constraint |
| Intelligence Nexus | Researcher, File, Web, UICartographer |
| The Forge | Coder, Builder, Scribe |
| Validation Bastion | Verifier, Tester, Soldier, Medic |
| Memory Vault | Archivist, ChangeArchivist |
| Infrastructure Works | Quartermaster, Inventory, Proxmox, Storage, Backup |
| Network Watch | NetworkScout, Health, SecurityScout |

Verified against the registry: all 25 roles map to exactly one chamber, no gaps, no duplicates.
Unknown or future roles fall into Infrastructure Works rather than spawning a chamber of their own —
so adding an ant can never fragment the map again.

- **Chamber summary only**, per the display rules: name, active/total, running count, failed count,
  and a standby marker when a chamber is entirely visible-only. Detail stays on the ants — hover or
  click an ant for its own state, workers, and activity.
- **One dominant status colour** per chamber, resolved by precedence: alert (any failures) → live
  (any active ant) → idle → dormant. No competing colours inside one ring.
- **Subtle activity pulse** only when a chamber actually has active ants, and never when Motion is
  off (so it also respects reduced-motion).
- **Visible-only ants read as STANDBY** — dimmed with a small "standby" marker rather than styled
  like a failure. They are present and inspectable, just clearly not executing.
- Chamber membership is now carried on each node (`chamber`), so dragging, hit-testing, and
  persistence no longer depend on the registry's `Colony` string at all.

One judgement call worth flagging: **HealthAnt** sits in Network Watch rather than Validation
Bastion. Service health is closer to observability/exposure than to change validation, so Network
Watch reads as the "what's out there and is it well" chamber. If you'd rather it sat with the
validators, it's a one-line move in `CHAMBER_MAP`.

Also swept: the orphaned `#cmap2` CSS selectors left behind by v2.14.8 (32 lines, including two
continuation lines whose selectors had been removed — they would have left the stylesheet
unbalanced, caught by a brace check before shipping).

### Stage 5 — the dashboard cards are now registered workspace panels

The workspace runtime shipped a shell (v2.14.3) and drag/resize (v2.14.4) with **nothing registered
in it**, so enabling the flag produced an empty surface. Now seven panels are registered — Colony
Health, System Core, Missions, Pending Approvals, Resource Usage, Recent Events, Operator Attention
— each in its designed default position.

- **The existing renderers are reused verbatim.** Rather than reimplementing each card, a panel
  body re-parents the element the renderer already writes to (`ov2-health-body`, `ov2-core-body`,
  `hud-attn-list`, …). One implementation per card, one data path, and `pollOv2`/`pollHud` keep
  filling them unchanged — no duplicated polling, which is the failure the plan's performance
  section warns about.
- **Registered `defaultPlacement` is now honoured** by the runtime; previously every panel would
  have stacked at one corner on first run, because the shell had no notion of a designed layout.
- The classic grid hides itself when the workspace mounts, so the two shells never render the same
  card twice; with the flag off, nothing changes at all.
- Mounting happens once on dashboard entry and only after `/health` confirms the flag — an
  unreachable `/health` leaves the classic dashboard in place rather than failing open.

Still ahead: tab groups, the topology moving under the panels as the persistent canvas, overlay
controls, and route consolidation.

## v2.14.8 — The chamber SVG view is gone from the console

The separate chamber map is removed from the UI. Everything it did now lives on the live colony
canvas (chambers via the **Chambers** button, plus motion/labels/pheromones and the reset controls
from v2.14.5–v2.14.7).

Removed:

- the chamber control bar (view switcher, motion/labels/pheromones selects, idle-ants checkbox,
  reset view/layout buttons) — all superseded by the canvas viewbar;
- the `#cmap2` SVG surface and its side inspector;
- the colony page-enter plumbing that loaded and re-rendered the chamber map, **including its 20s
  polling interval** — one less recurring request on the colony page.

Kept working, deliberately:

- **Colony search** was the one real coupling — it drove chamber selection. It now targets the
  canvas: it finds the ant by label, id, or worker id, selects it, opens the inspector, and centres
  the camera on it without changing zoom. Same outcome, surviving renderer.
- Every canvas capability is untouched: Command/Expanded/Active/Chambers/Handoffs views, ant and
  chamber dragging, pan/zoom, pulses, pheromone field, tooltips, inspector, role colours.

Also deleted, because the repo's own guard insisted: **the entire `cmap*` JavaScript block — 319
lines** covering CMAP state, the chamber layout table, the SVG renderer, chamber/ant/trail
selection, the inspector, pan/zoom, drag, and prefs. I had planned to defer this, but
`UiIntegrity_NoOrphanedElementLookupsAndNoDuplicateIds` (added in v2.14.2's hardening pass)
correctly failed the build: functions still calling `getElementById('cmap2…')` against removed
markup are exactly the drift that guard exists to catch. Deferring would have meant weakening or
suppressing my own test to ship — so the code went instead.

Preserved from that block, because other code genuinely uses them: the case-tolerant registry
accessors (`antRoleId`, `antRoleName`, `antWorkers`, `antWorkerId`, `antWorkerName`, `antPurpose`)
used by the Ant Inspector colony directory, and `attrSafe`, which keeps ids safe when embedded in
delegated handler attributes. Each was verified as referenced outside the deleted block before the
cut, not assumed.

Remaining: ~23 orphaned `#cmap2` CSS selectors that now match nothing. Harmless (dead styling, not
dead behaviour, and invisible to the guard) — swept in v2.14.9.

## v2.14.7 — Chambers are draggable as a unit

Live feedback on v2.14.5: the chamber ring resized but never moved, and grabbing it panned the
camera instead. Root cause: there was no chamber drag at all — `mousedown` hit-tested ants and
everything else fell through to camera panning, while the ring's radius was derived from how far
members sat from a centre that never changed. So dragging an ant made the circle grow or shrink in
place, which is exactly the "sticks to one axis" behaviour reported.

- **Grab the chamber body and the whole chamber moves** — centre and every ant inside it travel
  together, with the ring highlighting while held.
- **Ant dragging is unchanged**: ants are hit-tested first, so individual ants stay independently
  draggable inside their chamber; empty canvas still pans the camera.
- **Hit-testing and rendering share one radius function**, so what you click is exactly the ring
  you see (previously they could disagree).
- **Positions persist**: a dragged chamber saves its centre offset *and* each member's position
  under a new `chambers` key in `ui_state.json`, so a rebuild or reload keeps your arrangement.
- **⌂ Layout** now also returns dragged chambers home, not just ants.

Also answered from this round of feedback, no code change needed: **the Handoffs toggle looks inert
on an idle colony because there is nothing to draw.** Handoff edges come from live task-graph data
and are cleared when no mission is running and no task nodes exist. Run a mission and the layer
populates. (If you'd rather it showed recent-historical handoffs when idle, that's a real feature,
not a fix — say so and it gets its own release.)

Scope note: the redundant chamber SVG (`cmap2`) is confirmed replaceable and **queued next** — it
is 15 functions and ~145 references across `app.js`/`index.html`, including a search hook that the
console still calls, so it lands as its own reviewable deletion rather than riding along here.

## v2.14.6 — The pheromone field now tells the truth per ant

The pheromone data was always real — persisted trail strengths in SQLite, reinforced +0.02 on tool
success and decayed −0.04 on failure, with `ant:<role>` and `worker:<id>` trails recorded per
mission. The *visualization* was not: every mote picked a **random** ant, and the only real input
was a single average of the top three trails. So drift from CoderAnt implied nothing about the
coder's own memory — the honest reading was "some trails somewhere are strong."

Now the picture is sound:

- Motes are emitted by **specific ants that actually have a trail** (`ant:` / `worker:` keys,
  with a worker's trail also crediting its parent role).
- Each ant's **share of the motes is proportional to its own recorded strength**, so heavier drift
  from an ant means that ant's approaches are the ones currently working.
- **Brightness and size carry that ant's strength**, so a weak trail reads as a faint thread
  instead of borrowing the colony average.
- Emitters that lose their trail have their motes retired; ants with no trail emit nothing (the
  field goes quiet on a cold colony rather than inventing motion).
- The poll keeps enough rows for per-ant emission; the HUD trail bars still show the top few.

Data source is unchanged (`/pheromones/json`), and the Pheromones control from v2.14.5 still only
hides the visualization — pheromone memory keeps recording and feeding the learning loop either way.

Scope note: retiring the now-redundant chamber SVG (`cmap2`) is deliberately **not** in this
release. It is 15 functions and ~145 references across `app.js` and `index.html`; bundling a
deletion that size with a rendering change would make a regression hard to attribute. It lands in
v2.14.7 as its own reviewable commit.

## v2.14.5 — Topology consolidation: chambers become a layout of the live colony canvas

The console had two topology renderers — the mature canvas and the chamber SVG — with two sets of
map preferences, two inspectors, two pan/zoom states, and duplicate polling. That also made the
"one canonical topology instance" requirement impossible to satisfy honestly. Resolved by keeping
the renderer that works and folding the other one's capabilities into it.

- **Chambers are now a layout mode of the live canvas**, not a separate view. The
  "Groups" button is now **Chambers**: it clusters each colony around its own centre, draws
  chamber rings and counts in world space (so they pan and zoom with everything), and highlights a
  chamber whose ants are active. Nothing routes anywhere — the reorganization happens in place.
- **Everything the canvas already did keeps working**, untouched: ant dragging, pan/zoom, live
  pulses, handoff edges, pheromone field, hover/selection, the inspector, and role colours.
- **Map preferences moved onto the canvas viewbar** and now genuinely govern rendering:
  - **Motion** (off/low/normal/high) throttles particle spawning — `off` stops it entirely;
  - **Labels** (off/active/all) sets label density, while hovered ants always keep their label so
    inspection never goes blind;
  - **Pheromones** (off/active/all) gates the pheromone field;
  - **⤾ View** resets pan/zoom only; **⌂ Layout** returns dragged ants to the computed layout.
  All three preferences persist per operator and fall back safely on unknown values.
- CSP-safe throughout: the new controls use `data-colonypref` / `data-colonyact` with delegated
  listeners — no inline handlers.
- The chamber SVG (`cmap2`) is now redundant. It is **retired in v2.14.6**, once parity has been
  confirmed in real use rather than assumed — deliberately not deleted in the same release that
  moves the functionality.
- Docs: DASHBOARD_WORKSPACE.md gains the one-renderer decision and its consequences; the stage
  table adds 3b (this release) and 3c (SVG retirement).

## v2.14.4 — Topology-first Dashboard, Stage 3: drag, resize, alignment

The workspace becomes interactive. Still behind `dashboard_workspace_enabled` (default off).

- **Pointer Events only** — one code path for mouse, pen, and touch, with `pointercancel`
  handled so an interrupted gesture never strands a panel mid-drag. No parallel mouse/touch
  listeners, so nothing double-fires.
- **Pointer arbitration**, the design doc's named landmine, implemented explicitly: a gesture
  starting on a header moves that panel and calls `stopPropagation` so the topology never pans;
  a gesture on the resize grip resizes only; header buttons keep their clicks and are excluded
  from dragging; and while the layout is **locked** no gesture engages at all, so the map beneath
  receives everything.
- **Alignment without a grid prison**: panels snap to other panels' edges and the workspace bounds
  within 8px, guides render during the drag, and holding **Alt/Cmd bypasses snapping** for free
  placement.
- **Movement runs on `requestAnimationFrame`** against live styles; state is written **once at
  pointerup**, never per frame, and then clamped by the server on next load.
- **Panels cannot be lost**: dragging always leaves a grabbable header edge inside the workspace,
  and resizing respects per-panel minimums.
- Resize grips and dashed outlines appear only in customize mode; `touch-action: none` keeps
  browser gestures from fighting the drag.
- Tests: 9 new static-integrity assertions (Pointer Events exclusively, locked-mode inertness,
  propagation stopped, buttons excluded, rAF movement with save-at-end-only, modifier bypass,
  off-screen protection, resize minimums, customize-only handles) — 25 workspace tests total.

## v2.14.3 — Topology-first Dashboard, Stage 2: panel shell runtime

The workspace gains its panel machinery. Still behind `dashboard_workspace_enabled` (default off),
so the console is unchanged until an operator opts in.

- **`dashboard-workspace.js` / `.css`**, embedded and served same-origin like `app.js`. The CSP is
  `script-src 'self'` with no `unsafe-inline`, so the runtime carries **no inline JavaScript and no
  `on*=` handlers** — every control is a real `<button>` with `data-wsact`, dispatched by one
  delegated listener registered once (not per panel). No `innerHTML` anywhere in the runtime.
- **Panel registry + shell**: `AnthillWorkspace.register({id,title,render,…})`, rendered into a
  panel layer with a compact header, per-panel loading/error containment (a panel that throws
  reports inside its own body instead of taking down the workspace), and stable z-ordering.
- **Four distinct states, as specified**: collapse in place (header only, remembers its expanded
  height), minimize to a tray, hide (gone from workspace and tray, restorable from Modules), and
  pin (survives focus mode).
- **Modules menu, layout lock, focus mode, reset layout** on a compact toolbar; the menu stays open
  while toggling several modules.
- **Persistence**: debounced save *after* interaction, and the save path reads the current
  `ui_state` document and replaces **only** `dashboard_workspace` — ant names, colours, positions,
  and map preferences are never rewritten by a layout change. Server-side
  `DashboardWorkspaceState` (v2.14.2) remains the authority on validation and clamping.
- **Profiles**: the client switches desktop/compact at the same 900px breakpoint the server uses,
  and never copies one profile's placements into the other.
- **Accessibility**: `aria-label`/`aria-pressed`/`aria-expanded` on controls, visible
  `:focus-visible` rings, reduced-motion support, and opacity presets that dim a backdrop **scrim**
  rather than text so contrast holds over the animated map.
- Tests: 16 static-integrity tests covering CSP compliance (no inline handlers, no `innerHTML`,
  policy unchanged), full wiring (embedded → served → referenced), every declared action having a
  handler, distinct collapse/minimize/hide states, debounced saving, reset-layout scope, the
  ant-customization invariant in the save path, a11y affordances, and client/server breakpoint
  agreement. Interaction remains verified by the manual walkthrough — stated plainly, since this
  repo has no browser harness and adding one would contradict the no-build-system constraint.

## v2.14.2 — Topology-first Dashboard, Stage 1: workspace state model + kill switch

Start of the console track that makes the live colony map the Dashboard's persistent canvas, with
customizable floating panels above it. Canonical plan: **docs/DASHBOARD_WORKSPACE.md**. This
release is foundations only — no visible UI change yet, and the classic Overview + Colony pages
are untouched.

Plan revisions taken before building (rationale in the doc):

- **Kill switch first**: everything ships behind `dashboard_workspace_enabled` (default **false**).
  Flipping it off is the instant rollback.
- **Small releases, not one 50-item gate** — the mega-patch failure mode this project avoids.
- **Docking and split-panels deferred** past the responsive/a11y pass, and optional: free
  positioning + snap guides + tab groups carry most of the value without the geometry bug surface.
- **Layout correctness lives in C#, not the browser.** This repo has no browser test harness and
  adding one contradicts the no-build-system rule, so validation/clamping/migration/recovery are
  server-side and unit-tested; JS keeps interaction, verified by the manual walkthrough (stated
  honestly rather than dressed up as automated coverage).
- **Desktop and compact are separate profiles** — a phone visit can no longer clobber the desktop
  arrangement.
- **Opacity dims a backdrop scrim, never text** (contrast against a moving map).
- **Auto-save + Reset Layout**, no "Save Layout" button; **two flags** (`locked`, `focus_mode`)
  instead of three overlapping modes.
- **Pointer-event arbitration is a named design item** — the canvas already drags ants, drags
  chambers, and pans, so panel dragging above it needs explicit hit-testing rules.
- **Performance has a number**: the topology now renders permanently, so it must throttle when
  occluded, backgrounded, or under reduced motion.

Shipped here:

- `DashboardWorkspaceState`: versioned schema (panels, tab groups, overlays, desktop/compact
  profiles) with `Sanitize` — independent per-entry validation, coordinate/size clamping,
  off-screen recovery with a grabbable header edge, unknown-panel drop, new-panel merge that never
  moves customized ones, tab-group repair (a group under two members dissolves and its survivor
  floats), overlay anchor fallback, and idempotence.
- **The invariant**: a corrupt workspace resets *only* `dashboard_workspace` — ant names, colours,
  positions, and map preferences are never touched. Proven by test.
- `UiStateStore.WithSanitizedWorkspace` for the UI-state endpoint; `dashboard_workspace_enabled`
  gate in runtime/config/example.
- 20 xUnit tests covering the spec's persistence matrix (missing state, legacy v1 state, invalid
  positions/sizes/enums, unknown/missing panels, broken tab references, invalid anchors, corrupt
  workspace, profile isolation, future-key survival, idempotence).
- Docs: new `docs/DASHBOARD_WORKSPACE.md` (design, decisions, pointer arbitration, persistence,
  staged build order with status, performance budget, a11y, security); NORTH_STAR console track
  entry; supersession notes in UI_ROADMAP, CONSOLE_REDESIGN, CONSOLE_REFIT; README pointer.

## v2.14.0 — Safe Action Engine and Recovery Orchestration (NORTH_STAR Phase 6)

One safe execution framework for every state-changing system. Honest scope: the engine,
orchestration, and transaction machinery ship here with full tests; migrating the existing patch
and homelab executors onto the shared lifecycle is the next slice, so nothing in the working
pipeline changes mid-flight.

- **Unified lifecycle** (`draft → validated → risk_scored → waiting_for_approval → approved →
  scheduled → executing → verifying → completed_verified`, with `failed → compensating →
  compensated` and `rollback_failed → escalated`). Transitions are structurally enforced:
  approval cannot be skipped, nothing executes from draft, **execution alone can never complete an
  action — verification is the only door**, and terminal states are terminal.
- **Risk engine**: deterministic scoring over destructive potential, reversibility, rollback
  availability, target criticality (unknown scores cautiously), affected systems, dependency
  depth, prod-vs-lab, backup freshness, maintenance window, unresolved incidents, novelty, skill
  confidence (v2.13), verifier strength (v2.12), and change size. **A critical change can never be
  low risk by line count**: critical file classes, irreversibility, or missing rollback floor the
  level to high and force approval. High-risk operation classes always require approval.
- **Recovery orchestration**: rollback → retry-after-cooldown → failover → restore-from-backup →
  escalate, plus quarantine on security implications. **Rollback failure automatically suspends
  autonomy** for that scope and escalates; "no recovery path" is itself a suspension event.
- **Circuit breakers** per action type / target / provider / skill / rule: trip after repeated
  failures, and stay tripped through subsequent successes until an operator resets — a flapping
  target cannot silently re-arm itself.
- **Change-set transactions**: ordered steps with checkpoints, verification after each checkpoint,
  stop-on-failure, compensation in reverse order, and opt-in partial retention. A step that
  executed but failed verification is still compensated; a missing or failing compensation is
  recorded and suspends autonomy rather than being ignored.

## v2.13.0 — Procedural Skills and Evaluated Learning (NORTH_STAR Phase 5)

ANTHILL can now improve from experience — but only from *verified* experience, and never by
self-certifying.

- **Versioned skill registry**: id, version, purpose, proven environments, required capabilities,
  procedure, verification policy, compensation plan, success/failure counts, derived confidence,
  last-validated, and the evidence-bundle ids backing every success.
- **Lifecycle**: candidate → experimental → certified, with automatic demotion to degraded and
  then retired. Promotion and demotion are symmetric and both automatic — a skill that stops
  working loses standing without operator intervention.
- **Evidence-gated promotion**: a success counts ONLY when its v2.12 verification bundle is
  promotable (all required verifiers passed, deterministic evidence present). A completed mission
  with no bundle is a FAILURE for learning purposes, not a success. Confidence is derived from the
  record, never asserted by a model.
- **Environment coverage**: successes record the environment they were proven in; a skill is never
  offered outside its coverage, and environment drift (provider/toolchain change) degrades proven
  skills until they re-prove themselves.
- **Planner preference**: certified compatible skills first, then experimental (which
  `RequiresSandbox` — never straight at production), otherwise nothing and the planner generates a
  plan. Candidate, degraded, retired, and blocked skills are never offered.
- **Operator authority**: Blocked is operator-only, and blocked/retired skills ignore incoming
  outcomes — they cannot silently revive.
- **No self-training**: this release changes preference ordering only. It grants no permissions,
  skips no approvals, weakens no verification, and expands no targets; production model
  fine-tuning remains out of scope per the NORTH_STAR model-training policy.

## v2.11.2 — Model Routing Failover Activated (NORTH_STAR V3-track, wiring)

Second hot-path wiring: the model router now uses the v2.11.0 routing intelligence to keep missions
moving when a provider goes down, without changing healthy-path behavior.

- **`ModelRouter.ResolveRoute`**: `Generate` resolves the effective route through a new
  `ResolveRoute(role)` instead of `GetRoute` directly. When the configured route's provider circuit
  breaker is OPEN and a distinct configured `fallback` route is healthy, the decision — made by the
  deterministic, stability-preferring `ModelRoutingPolicy.Choose` (live breaker state supplies the
  health signal) — fails over to the fallback route so the call proceeds instead of fast-failing on a
  dead provider. The chosen reroute reason is recorded in the `model_call` event metadata.
- **Unchanged healthy path**: `ResolveRoute` is a strict no-op when the breaker is disabled, when the
  primary route is healthy, or when no distinct fallback is configured — so normal routing, and every
  existing test, behaves exactly as before. `FormatRoutes` and the operator route views still read the
  configured routes directly.
- Still ahead in v2.11.x: the live Command console (Track 3) and, optionally, per-task risk-aware
  routing once the task contract's risk class is threaded to the router.

## v2.11.1 — Coder → Sandbox Loop Activated (NORTH_STAR V3-track, wiring)

The first hot-path wiring of the v2.10/v2.11 primitives. `CoderAnt` gains an iterative sandbox path,
gated off by default so the standard install is unchanged.

- **`CoderAnt` sandbox path**: when `sandbox_execution_enabled` is true, the coder no longer emits a
  single one-shot proposal — it runs `SandboxedCoderRunner` over the agent workspace root. Each turn
  proposes a patch, applies it INSIDE a disposable git-worktree sandbox, runs the allowlisted
  `dotnet_build` check there, and — on failure — feeds the check output back into the prompt for a
  corrected attempt, all bounded by `BoundedAgentLoop`. The run returns the coder's best patch JSON
  (verified in-sandbox when the loop completed) as the SAME structure `ProcessPatchProposals` already
  parses, so approval/apply is unchanged.
- **Fail-safe by construction**: the entire previous one-shot path is preserved and used as the
  default AND as the fallback whenever the sandbox path is unavailable — gate off, no usable
  workspace root, or the check is refused. The live workspace is never modified (writes stay in the
  sandbox; dispose destroys it), and every proposal remains human-approval-gated before apply.
- **Prompt**: `CoderAnt`'s prompt builder is factored into `BuildPrompt(task, mission, context,
  feedback)`; the feedback block is appended only on sandbox retries, so the one-shot prompt is
  byte-identical to v2.11.0.
- Still ahead in v2.11.x: wiring `ModelRouter.GetRoute` to consult `ModelRoutingPolicy` with a
  persisted stats snapshot, and the live Command console.

## v2.11.0 — Sandboxed Coder Loop + Model Routing Intelligence (NORTH_STAR V3-track, wiring-ready)

Turns the inert v2.10.0 primitives into usable engines. Honest scope: both land as ADDITIVE,
independently-tested units; the hot-path wiring into `CoderAnt`/`ModelRouter` follows in v2.11.x so
this release cannot change existing behavior. Default install is byte-identical — `sandbox_execution_enabled`
stays off.

- **`SandboxedCoderRunner`** (`src/Anthill.Core/Sandbox/`): the first code path that composes
  `SandboxWorkspace` + `BoundedAgentLoop` into real iterative work — propose (model) → apply INTO
  THE SANDBOX ONLY → run one allowlisted check (`CheckCatalog`) in the sandbox → inspect → done on
  green, else feed the failure back for the next bounded turn. Safety invariants, all tested: the
  `EnableSandboxExecution` gate is checked first (off = no work); a `WorkspacePathGuard` rooted at
  the sandbox refuses traversal so writes never escape it; iteration is bounded by the LOOP with an
  explicable stop reason; nothing auto-applies — the result is the in-sandbox diff plus proposals
  handed to the EXISTING approve-then-apply gate; the sandbox is destroyed on dispose and the live
  checkout is never touched. The model call is injected, so the loop is deterministic and testable
  without a live model.
- **`ModelRoutingPolicy` + `ModelStats`** (`src/Anthill.Core/Models/`): pure, deterministic
  per-task route selection. `ModelStats.Aggregate` folds recorded `ModelCallRecord`s into per-route
  health (success rate + average latency; low-sample routes get the benefit of the doubt).
  `ModelRoutingPolicy.Choose` picks among candidate routes given the task risk class — favor the
  fastest healthy route for low/medium-risk work, keep the configured route's stability for
  high/critical work until it is proven unhealthy — and returns a human-readable REASON for the
  Console/audit ("chose ollama:fast — 100% success, 150ms avg over 10 calls").
- Tests: sandbox green-path completes and the live tree is untouched; failing check stops with a
  bounded reason; gate-off does no work; unknown check refused. Routing: stats aggregation, health
  thresholds, low-risk speed preference, high-risk stability, unhealthy-reroute, all-unhealthy fallback.

## v2.12.0 — Independent Verification and Evidence (NORTH_STAR Phase 4)

Execution and verification are now separate: the ant or model that made a change is never the
entity that decides whether it worked. (Phase renumbered — the v2.11.x line went to sandbox/coder
wiring; see the NORTH_STAR note.)

- **Framework**: `IVerifier`, `VerificationRequest`, `VerificationResult`, `VerificationEvidence`,
  `VerificationPolicy`, `VerificationBundle`, `VerificationRunner`.
- **Deterministic verifiers**: DiffVerifier (scope containment, no-op detection, content hashes),
  BuildVerifier and TestVerifier (allowlisted checks — real exit codes, test counts, output
  digests; never a model's claim), SecurityPolicyVerifier (reuses the deterministic policy engine:
  secrets, permission expansion, blocked paths), ArtifactVerifier (files exist, with hashes).
- **Per-task-type policy**: code_patch requires diff+build+test+security; docs_patch requires
  diff+security; unknown task types still require a policy scan — fail closed.
- **Promotion rule**: a bundle is promotable only when EVERY required verifier ran and passed. A
  missing or faulting verifier counts as failure, never a pass. Structural completion cannot
  create a verified success.
- **Model confidence is never proof**: a bundle with no passing DETERMINISTIC evidence is blocked
  even if every semantic check "passed" — semantic judgment may supplement, never replace.
- Verification is independently rerunnable: same request, same deterministic outcome (tested).
- Honest scope: ServiceHealth / InfrastructureState / Dependency / Rollback / SemanticJudge
  verifiers are declared in the policy vocabulary and land with the safe-action phase, where their
  provider state and compensation paths exist.

## v2.10.1 — Sandboxed patch verification (first consumer of the Phase 3 primitives)

Patch verification no longer touches the live checkout.

- **Before**: verifying a patch applied it to the LIVE workspace (with backup), ran build/test
  there, then restored — so it required the write gates to be on, and a crash or failed restore
  mid-verify could leave the running install modified.
- **Now** (when `sandbox_execution_enabled` is on): the workspace is copied to a disposable
  sandbox, the patched content is written INTO THE COPY, `dotnet build && dotnet test` runs inside
  it, and the copy is destroyed. Nothing to restore; the live tree is never written to; no write
  gates required. A path that would escape the sandbox refuses before any write.
- Copy-mode is deliberate: verification must see the workspace as it is ON DISK, including
  uncommitted local state the patch was diffed against — a HEAD worktree would test the wrong
  baseline (`SandboxWorkspace.Create(preferCopy: true)`).
- Unchanged semantics: a green verify still only AUTO-APPROVES; applying to the real workspace
  remains the operator's explicit action. A red verify leaves the patch pending with the tail.
- Legacy live-workspace path is intact and used when the gate is off — fully reversible.
- `AutoApplyRunner.RunVerify(workdir)` now accepts a target directory (defaults to legacy behavior).
- Tests: patched content never reaches the source tree (existing + new files), uncommitted state
  is visible in the copy, sandbox destroyed after use, path-escape detectable before write.

## v2.10.0 — Sandboxed Agent Execution (NORTH_STAR V3-track Phase 3, primitives)

Ants gain the machinery to work iteratively WITHOUT touching the live installation. Honest scope:
this release ships the sandbox + bounded-loop primitives with full isolation/budget tests; wiring
agent code paths (coder iteration) through them lands in v2.10.x behind the gate.

- **`SandboxWorkspace`**: disposable workspaces via git worktree (exact HEAD state, cheap) with a
  bounded-copy fallback for non-git sources. Writes never touch the source checkout (tested);
  artifacts leave ONLY via explicit `Harvest` (traversal-guarded, caller-chosen destination —
  never auto-applied to the live tree); `ChangeSummary` exposes the in-sandbox diff; dispose
  destroys the worktree and prunes. Deterministic C# — no model in workspace lifecycle.
- **`BoundedAgentLoop`**: the observe → execute → inspect → replan engine with hard budgets
  enforced by the LOOP, not agent judgment — max turns, max tool calls, elapsed-time budget
  (injectable clock), repeated-action detection, cancellation, and step-fault capture. Every exit
  carries an explicable stop reason; unbounded iteration is structurally impossible.
- **Gate**: `sandbox_execution_enabled` (default false) reserved for the agent wiring; the
  primitives are inert until a code path opts in.
- Tests: worktree isolation + cleanup, copy-fallback isolation, harvest traversal refusal, and a
  stop-reason matrix covering every budget (completed / max_turns / max_tool_calls / timeout /
  repeated_action / cancelled / step_fault).

## v2.9.1 — Ant Execution Framework (specialist activation, stages A–H)

Framework-first activation of the specialist colony (spec-driven, staged, each stage gated green
before the next). Canonical doc: docs/ANT_EXECUTION.md.

- Runtime classification (ControlPlane / DeterministicService / MissionAgent / VisualScaffold),
  versioned execution contracts, structured results/artifacts/evidence/handoffs (Stage A).
- Capability enforcement at tool dispatch: spoofed identities refused; apply_patch/shell/write
  structurally denied to every mission agent; audited structured denials (Stage B).
- Validated executor catalog + startup validation + rollout gates, ALL default off (Stage C).
- Six specialists implemented as canaries, each with contract, handler, and tests (Stage D):
  ui_cartographer (read-only UI mapper), tester (allowlisted checks only, deterministic evidence),
  soldier (deterministic policy engine, blocks not model-overridable), scribe (docs-only outputs
  and docs-path-only patch proposals), medic (bounded diagnosis, loop brakes), archivist
  (positive learning ONLY from completed_verified; secrets redacted).
- Roles intentionally left non-executable: quartermaster (no deterministic metrics contract yet),
  control-plane roles, all homelab deterministic services (never LLM-directed).
- Bounded handoff gate (depth/budget/dedupe) + deterministic specialist planner routing (Stage E).
- Truthful role status in /colony/graph and the Ant Inspector (Stage F).
- Docs: ANT_EXECUTION.md added; NORTH_STAR pre-V3 requirements, ROADMAP tactical track,
  AUTONOMY note, README summary (Stage G).
- Compatibility: existing six roles and all mission flows unchanged; structured results ride a
  temporary tagged-JSON adapter until BaseAnt goes structured (documented removal plan).

## v2.9.0 — Contracted Tasks + Typed Capability Tools (NORTH_STAR V3-track Phase 2)

Machine-readable contracts replace loose prompt tasks and string-parsed results as the control
surface. New `src/Anthill.Core/Contracts/`, documented in `docs/CONTRACTS.md`.

- **Admission gate**: every path out of the planner funnels through `ContractGate.Admit` — planner
  output is projected to a `TaskContract` and schema-validated; invalid tasks (missing
  title/objective, out-of-schema enums, self-dependencies, zero declared capabilities) CANNOT
  enter the execution queue, and every rejection is logged with its full error list.
- **Capability model**: permissions attach to capabilities (`repo.read`, `repo.patch.propose`,
  `network.http.public`, `proxmox.vm.start`, …), not ant names. `ToolCatalog` gives every
  executable caste a typed declaration (capabilities, side-effect class, risk class, idempotency,
  cancellation/timeout, compensation — every state-changing tool declares recovery), and
  `CanRun(ant, grants)` evaluates permission BEFORE execution; unknown tools and partial grants
  refuse.
- **Fail toward caution, never silently break planning**: an ant unknown to both catalog and
  registry projects as destructive/critical with no capabilities → rejected; a role the registry
  says is executable but the catalog doesn't know yet gets a cautious fallback declaration
  (reversible/high/manual-compensation) so newly enabled roles remain plannable.
- **Structured results + failure taxonomy**: `ToolResult` with typed `FailureClass` (12 classes);
  retry decisions come from `FailureClassify.IsRetryable` (transient/rate-limit/timeout/conflict
  only), never from parsing error text.
- Docs: new `docs/CONTRACTS.md`; NORTH_STAR + ROADMAP sequence tables marked for v2.8.0/v2.9.0.
- Tests: admission matrix (valid admitted, unknown/malformed rejected loudly), capability
  evaluation incl. partial grants, every-caste-declared guard, retry-class theory over the
  taxonomy.

## v2.8.0 — Durable Mission Runtime (NORTH_STAR V3-track Phase 1)

Mission execution no longer depends on in-memory job state for operational correctness. The
in-memory registry remains the dispatcher; new `mission_jobs`/`mission_attempts` tables are the
source of operational truth.

- **Persist-first submission**: every accepted mission lands in SQLite before it is queued;
  optional idempotency key (unique-indexed) makes replayed delivery return the ORIGINAL job —
  never a duplicate mission.
- **Atomic claims + worker leases**: a single guarded UPDATE claims a queued job (two Directors on
  one database cannot double-launch — tested with parallel claimants and with two separate store
  instances); a heartbeat renews the 90s lease at one-third intervals while the mission runs.
- **Write-through state**: running/mission-id/result/error/outcome/cancel-requested/finished all
  hit the durable row as they happen, and every run records an attempt (worker, reason, error,
  duration) preserving mission identity across retries.
- **Startup reconciliation**: on boot the runtime classifies incomplete work — queued → resumable
  (re-dispatched), running-at-boot → retryable (attempt++, re-queued, attempt history explains
  why) while attempts remain, else orphaned → failed for operator review; cancel-requested →
  cancelled. Completed work is never touched and can never be re-claimed.
- **Required tests implemented** (process death simulated by reopening the same database file):
  killed-while-queued survives; killed-mid-lease retried with new attempt; attempts exhausted →
  operator review, never silent loss; two-claimant race → exactly one winner; idempotency replay →
  one row; completed job untouched and unclaimable; pre-crash cancel honored; heartbeat renews
  only for the owning worker.
- Scope note (honest): mission-level durability + idempotent submission ship here. Side-effect
  idempotency for infra actions already exists via the v2.3 proposal dedupe; contracted per-tool
  idempotency keys arrive with V2.9.0 typed capability tools, per the roadmap.

## v2.7.0 — Mission Control: circuit breaker, per-task watchdogs, provider health

- **Circuit breaker for model providers.** After `ModelCircuitFailureThreshold` (default 3)
  consecutive transport faults — timeouts or connection failures — on one provider route, the breaker
  opens and subsequent calls fast-fail in microseconds for a `ModelCircuitCooldownSeconds` (default
  30s) window instead of each waiting out a full 120s timeout. This is the capstone to v2.6.6: even a
  completely dead Ollama can no longer make every queued mission burn a timeout and re-pin the
  single-writer queue. After the cooldown the breaker half-opens, admits one probe, and closes on the
  first healthy response (any real answer — even a 401 or "model not pulled" — counts as healthy).
- **Outcome classification + observability.** Each model call is now classified into a stable outcome
  (`ok`, `empty`, `cancelled`, `timeout`, `connect_error`, `http_error`, `auth_error`,
  `not_available`, `config_error`, `error`) and recorded on the `model_call` event alongside a
  `circuit_open` flag, so operators can see *why* calls fail. Only genuine transport faults count
  against the breaker — a mission cancellation or a config error never trips it.
- **Per-task watchdogs.** Each task now runs under its own deadline layered beneath the mission's, so
  a single task's model calls abort at `MaxTaskSeconds` instead of only being flagged as over-limit
  after they return. This closes a gap where, in sequential mode, one slow task could consume the
  whole mission budget. Mission cancel/timeout still propagates through the linked token.
- **Provider health surface.** A new `GET /providers/health` endpoint, a plain-English line on the
  `models` view, and a dashboard **operator-attention item** that appears only when a route is degraded
  ("ollama:llama3.1:8b is cooling down after repeated timeouts, 23s left") — so the reliability state is
  visible exactly where operators look for problems, in plain language, and silent when all is well.
- **Console no longer looks stuck when it isn't.** A backgrounded tab serves cached data, so a mission
  that finished while you were away could keep reading as "running" until a slow background tick caught
  up. The console now drops status caches and repolls the instant the tab regains focus.
- **One-click Re-run.** Finished, cancelled, and failed jobs now have a **↻ Re-run** button that
  re-dispatches the exact same directive (the mode prefix is baked into the stored goal, so the retry
  runs in the same mode) — retry a timed-out or cancelled mission without retyping it.
- **"Why it ended" on every job.** Each mission now finishes with a plain-English outcome —
  *Completed — 4/4 tasks succeeded*, *Cancelled by operator*, *Timed out — exceeded the 600s budget*,
  *Partial (2 tasks hit the per-task limit)*, or *Failed — <the actual reason>*. The executors report
  the authoritative stop reason (timeout vs. cancel), the finalized state drives the rest, and it shows
  right on the Missions job list next to the status — no digging through events to learn what happened.
- **Manual patch revert — the write-path round-trip is now complete.** The Changes page advertised
  "roll back" but offered no way to undo a cleanly-applied patch (rollback only fired automatically on
  a failed build). New `POST /revert/{id}` + a **↺ Revert** button on applied patches: an "add" is
  undone by deleting the created file, a "modify" by restoring its pre-apply backup, and the patch is
  marked `reverted`. Path resolution goes through the same `WorkspacePathGuard` as apply, so a revert
  can never escape the sandboxed workspace. Adds the `reverted` patch status end to end.
- `models` / router status now reports the per-call timeout and breaker settings. New
  `EnableModelCircuitBreaker` flag (default on) and `ModelCircuitFailureThreshold` /
  `ModelCircuitCooldownSeconds` tunables. No breaking API changes.

## v2.6.6 — Reliability: model calls are bounded and cancellable

- **Fixed a class of "hung mission" that could pin the job queue.** Model HTTP calls were synchronous
  and effectively uninterruptible: each attempt could block up to ~185s and, with retries, a single
  call could run for minutes. Because the mission deadline (`MaxMissionSeconds`) is only checked
  *between* tasks, an in-flight generation could overshoot it, and with worker concurrency of 1 one
  slow mission blocked every queued mission behind it.
- **New ambient cancellation for model calls (`ModelCallScope`).** A mission now publishes a single
  token — its `MaxMissionSeconds` deadline linked with any external cancel — via an `AsyncLocal`, and
  every model client (`OllamaClient`, `OpenAiCompatibleClient`, `AnthropicClient`) links it into each
  request with a hard `ModelCallTimeoutSeconds` (default 120s) bound. An in-flight call now aborts the
  instant the mission times out or is cancelled, and reports a clean, non-retried error.
- **Cancelling a *running* job now actually stops it.** `ApiJobRegistry.Cancel`/`CancelAll` signal the
  mission's token, so the current model call aborts and the scheduler stops dispatching — instead of
  the job continuing until the deadline. Queued-job cancellation is unchanged.
- New `AnthillRuntime.ModelCallTimeoutSeconds` tunable. No public API shape changes; the CLI path is
  unaffected (it runs with no ambient token, exactly as before).

## v2.6.5 — Housekeeping: docs refresh + test-warning cleanup

- **README "Using the Web UI" refreshed** to the shipped 7-domain information architecture
  (Dashboard, Monitoring, Operations, Infrastructure, Colony, Security, Administration) with the
  deep-linkable route format and a note on keyboard/screen-reader accessibility. The historical
  version-notes table is intentionally left as-is (it records what each past version actually shipped).
- **Cleared the one CI code warning** (`xUnit2031`): `AutomationRuleTests` now uses
  `Assert.Single(collection, predicate)` instead of `Assert.Single(collection.Where(predicate))`
  — same assertion, no analyzer warning.
- No code-behavior or API changes.

## v2.6.4 — Reliability: scope SQLite pool clears to the owning instance

Fixes an intermittent `System.ObjectDisposedException` ("Cannot access a disposed object:
'SQLitePCL.sqlite3'") that surfaced in CI under parallel test execution.

- **Root cause:** `SqliteMemory.Dispose()` and `HomelabRepository.Dispose()` called the process-global
  `SqliteConnection.ClearAllPools()`, which disposes pooled SQLite handles for *every* live instance.
  With connection pooling on and xUnit running test classes in parallel, one instance's teardown could
  dispose a connection another instance was mid-query on → the disposed-handle exception. It was also a
  latent hazard in production had two `SqliteMemory`/`HomelabRepository` instances ever coexisted.
- **Fix:** both `Dispose()` methods now call `SqliteConnection.ClearPool(conn)` scoped to the
  instance's own connection string, releasing only that database's pooled connections. The connection
  string is centralized in a `ConnString` member so `Connect()` and `Dispose()` can't drift.
- No behavior change for callers; purely a lifecycle-scoping correction.

## v2.6.3 — Console polish, CSP hardening & accessibility

UI consistency pass across the console (front-end only; `src/Anthill.Api/Ui/index.html`). No
backend/API changes.

- **Flattened-glyph repair.** Fixed 15 icons that had rotted to a literal `?` from prior non-UTF-8
  re-saves of the embedded UI — 9 trailing action arrows on the Dashboard/JS links (`Events →`,
  `Mission Results →`, `Changes →`, `Automation →`, `All →`, `Log →`, `Open in Changes →`) plus 6
  leading icons the UI-integrity guard did not catch (it only flags `?` at the start of a static
  label or a bare `>?<`, not `label ?<` or `>? {dynamic}`); the broken leading markers were removed
  so no stray `?` renders.
- **Terminology cohesion.** Aligned the remaining pre-redesign names in user-visible labels with the
  v2.6.0 IA vocabulary: Dashboard card links and quick actions, the keyboard-shortcuts help modal,
  the Colony Vitals "Automation" tile, the Settings "Automation" section, the Colony Learning
  "Signals" card, and dynamic status/patch messages now read Events, Mission Results, Changes,
  Automation, Signals, Infrastructure, and Agents consistently with the sidebar and breadcrumbs.
- **Guard hardened.** `RegressionGuardTests.UiIntegrity` now also fails on `Label ?<` (trailing) and
  `>? {content}` (leading) glyph rot — the two patterns that let these ship — so the class can't
  silently recur in CI.
- **Accessibility.** Icon-only header controls (notifications, approvals, sign out, collapse sidebar)
  gained `aria-label`s; the sidebar is now keyboard-operable (config-driven nav items/domains carry
  `role`/`tabindex`/`aria-expanded` and activate on Enter/Space); added visible `:focus-visible`
  outlines for nav, sub-nav, breadcrumbs, and header buttons; `nav-rail` labelled as primary nav; the
  redesign's nav transitions now honor `prefers-reduced-motion` like the rest of the app. Non-native
  clickables (div/span carrying `data-onclick`) are now keyboard-operable — the delegated dispatcher
  activates them on Enter/Space and tags them `role="button" tabindex="0"` (initial pass + a
  MutationObserver for dynamically-rendered ones). Gave every previously-unlabeled form control an
  accessible name: 35 static controls (event/patch filter selects, homelab registration forms,
  virtualization-connection toggles, auto-apply settings) plus the dynamically-generated ones
  (per-agent name/colour/provider/model fields on Colony → Agents, and the collection-manager filter)
  now carry contextual `aria-label`s so screen readers announce each control's purpose.
- Restored the high-confidence pending-approvals warning icon (`⚠`); genuinely ambiguous stripped
  icons were left as clean text rather than guessed.
- `node --check` clean; UI-integrity guards (duplicate ids, U+FFFD, `>?<`, `Label ?<`, `>? `) pass.

### Security: CSP `script-src 'unsafe-inline'` removed (backend + UI)
- **Dropped `'unsafe-inline'` from `script-src`** (now `script-src 'self'`) — closes the primary
  inline-script XSS vector. This required removing all inline JavaScript from the console:
  - The single `<script>` block (~6,300 lines) was **externalized to `Ui/app.js`**, embedded and
    served same-origin at `/ui/app.js` (`no-store`); index.html now loads it via `<script src>`.
  - All **199 inline `on*=` event handlers** (88 in markup, 111 generated in JS template strings)
    were converted to `data-on*` attributes driven by a single **delegated dispatcher** that runs
    the handler through a small micro-parser — never `eval`. Verified live: nav, tabs, filters,
    object/`this`/`event` args, statement sequences, and `return false` all dispatch correctly with
    zero handler errors.
  - No inline `<script>`, no `javascript:` URLs doing work, and no other inline handler attributes
    remain, so the policy holds.
- Also added (no markup change): `base-uri 'self'`, `object-src 'none'`, `frame-ancestors 'none'`,
  `form-action 'self'`, `Permissions-Policy` (camera/mic/geolocation/payment/usb denied), and
  `Cross-Origin-Opener-Policy: same-origin`. `style-src` keeps `'unsafe-inline'` (864 inline style
  attributes; style injection is far lower risk). `connect-src` omitted so the "remote API base URL"
  feature keeps working.
- Guards updated: `scripts/validate.sh` `node --check`s `Ui/app.js`; `RegressionGuardTests`
  glyph/encoding scan covers both `index.html` and `app.js`.

## v2.6.2 — Console Redesign polish: Model Routing is a dedicated view

Follow-up to v2.6.1 (front-end only; `src/Anthill.Api/Ui/index.html`).

- **`Colony → Model Routing` is now a clean, dedicated view.** Reached via the sidebar it hides the
  full Settings tab strip (its own **Routes & Models / Providers** sub-nav covers what's relevant)
  and relabels the header to "Model Routing" — no more double tab row or leftover "Settings" title
  under the Model Routing breadcrumb. `Administration → Settings` is unchanged: it keeps the full
  Connection/Providers/Colony/Models/System Info strip and its own title. Driven by the route
  (`/colony/model-routing`), so the two entry points stay visually distinct.
- No backend/API changes; `node --check` clean; UI-integrity guards pass.

## v2.6.1 — Console Redesign follow-ups: Model Routing home + sidebar-only Infrastructure nav

Two refinements on the v2.6.0 IA (front-end only; `src/Anthill.Api/Ui/index.html`).

- **Model Routing gets a home in Colony.** `Colony → Model Routing` (`/colony/model-routing`) with
  sub-nav tabs **Routes & Models** and **Providers** — model/provider configuration now lives in the
  runtime domain instead of being buried in Settings. Route-driven (same pattern as the
  Approvals/Changes split): it opens the Settings page pre-switched to the matching `.settings-tab`
  via a new `stab` route field, and `Administration → Settings` with no `stab` resets to the
  Connection tab so every route lands deterministically.
- **Infrastructure navigation is sidebar-only.** The redundant in-page category sub-nav row on the
  Infrastructure (Homelab) page is hidden — all 11 sub-pages are already left-sidebar entries
  (Infrastructure's sections + Monitoring's Alerts/Activity). `hlSubShow()` still drives sub-page
  visibility via the sidebar routes and `1`–`-` keyboard shortcuts; only the duplicate row is gone.
- No backend/API changes; `node --check` clean; UI-integrity guards pass.

## v2.6.0 — Console Redesign: enterprise information architecture (docs/CONSOLE_REDESIGN.md)

The single-page console with ~16 flat, inconsistently-grouped tabs becomes a routable, seven-domain
enterprise operations platform. Front-end only (`src/Anthill.Api/Ui/index.html`); internal page ids
are unchanged, so every existing `showPage(id)` caller keeps working. Full rationale, sitemap,
consolidation table, and journeys live in `docs/CONSOLE_REDESIGN.md`.

- **Information architecture.** Sixteen equal-weight tabs collapse into a config-driven, role-aware
  grouped sidebar: **Dashboard** + **Monitoring / Operations / Infrastructure / Colony / Security /
  Administration**. One `IA` config renders the nav and derives the route table, so adding a feature
  is a config entry, not a new top-level tab.
- **Real routing.** 35 deep-linkable hash routes (`#/monitoring/activity`, `#/infrastructure/compute`,
  `#/colony/agents`, …) with `go()` / `router()` / `popstate` back-forward and a legacy-redirect table
  mapping every old `#page` id to its new home. Breadcrumbs (clickable segments) replace the single
  page title; grouped domains get contextual sub-navigation.
- **Enterprise naming.** Homelab → **Infrastructure**, Overview → **Dashboard**, Pheromones →
  **Signals**, Autonomy → **Automation**, Ant Config/Inspector → **Agents**, Patch Center →
  **Changes & Approvals**, Event Log/Results → **Activity**, Shell → **Terminal** — applied across
  sidebar, breadcrumbs, command palette, and in-page titles.
- **Redundancy removed.** A unified **Activity** center renders one filtered timeline over the event
  stream with category facets (All / Missions / Changes / Autonomy / Infrastructure / System); the
  Event Log, Mission Results, and Changes pages remain intact as tabs (additive). **Patch Center**
  splits into **Approvals** (pending queue) and **Changes** (full history) as route-driven views over
  the one list. **Agents** unifies the former Ant Config + Ant Inspector as Configure/Inspect tabs.
- **Navigation cohesion.** The Infrastructure in-page sub-nav drives the router (breadcrumb / sidebar
  / URL stay in sync bidirectionally); the collapsed rail reveals each domain's children as a hover
  fly-out; live nav badges (jobs / patches / autonomy) are preserved.
- Verified live in-browser (routing, breadcrumbs, sub-nav sync, unified Activity, Agents tabs). No
  backend/API changes; `node --check` clean; UI-integrity guards (duplicate ids, glyphs) pass.

## v2.5.5 — Console Refit R5 Wave 1: download-client integrations (docs/CONSOLE_REFIT.md)

The first integration wave on the R1 platform. Five download clients join the catalog with
**zero new tables, endpoints, or UI pages** — proof the generic contract holds: a new integration
is one `IIntegrationDefinition` plus one registry entry, and the R2 widget runtime renders it.

- **Five kinds, one definition**: qBittorrent, Transmission, Deluge (torrent) and SABnzbd, NZBGet
  (usenet) register as `DownloadIntegrationDefinition` in `IntegrationCatalog` — category
  `download`, feeding the `health`, `queue`, and `statistics` widget kinds the console already
  renders. The generic `/homelab/integrations` surface lists them, the shared sync job sweeps
  them, and the widget picker offers them, all with no per-kind wiring.
- **Read-only by construction (a new proof for RPC clients)**: unlike the *arr/Proxmox GET-only
  clients, three of these five speak RPC-over-POST even to *read* — Transmission's `X-Transmission-
  Session-Id` 409 handshake, Deluge's JSON-RPC `/json`, and qBittorrent's cookie login. "GET-only"
  is impossible at the protocol level, so the guarantee is enforced differently: the ONLY public
  operation on `DownloadClient` is `ProbeAsync`, and every request it issues names a hardcoded READ
  method. No pause/resume/delete/add/reprioritise is expressible on the type. Tests assert the
  public surface carries no mutating verb. Transfer control, if it ever lands, arrives behind the
  approval-gated action pipeline — exactly as planned for Proxmox.
- **Normalized snapshot**: each protocol reduces to one `DownloadSnapshot` (version, state,
  down/up bytes-per-sec, active/total counts). SABnzbd and NZBGet report no upload (usenet), so it
  reads zero honestly rather than being faked. Speeds render human-readable (`3.4 MB/s`,
  deterministic invariant-culture formatting).
- **Same discipline as every integration**: the D1 target allowlist is checked before a single
  byte leaves (asserted before-any-I/O in tests); secrets live write-only in the credential store
  (`username:password` for qBittorrent/Transmission/NZBGet, web password for Deluge, API key for
  SABnzbd) and are fetched per probe, never logged; strict 10s timeout; redirects never followed
  (SSRF hardening); deterministic C# — never the model router.
- **Tests**: `DownloadIntegrationTests` — catalog metadata, the no-mutating-method surface, the
  allowlist/credential gate before I/O, per-protocol parsing against a mock server (qBittorrent
  cookie login + Referer, Transmission 409 handshake, Deluge JSON-RPC login/status, SABnzbd
  pure-GET apikey, NZBGet GET + HTTP Basic with the default `nzbget` user), the end-to-end
  `SyncAsync` widget payloads, and rate formatting.

## v2.5.4 — Console Refit R4: allow/blocklist management + collections framework (docs/CONSOLE_REFIT.md)

The D1 target list grows a first-class blocklist and its first real management surface.

- **Deny beats allow**: the target list now carries `allow` AND `deny` entries (`list_kind`
  column; idempotent `ALTER TABLE` migration — pre-2.5.4 rows stay allows, behavior unchanged
  by upgrade). `HomelabTargetGuard` scans the whole list: one matching enabled deny refuses the
  target no matter how many allow entries also match (a deny /24 carves a hole out of an
  allow /16). Every guard consumer — integration clients, health checks, virtualization
  providers, and the approval-gated action executor — consumes the blocklist with zero changes
  of their own, because the guard is the single choke point. Unknown kinds normalize to
  `allow` semantics; the default stays closed.
- **Full CRUD over D1**: `POST /homelab/allowlist` accepts `kind` (allow|deny) + optional id
  (edit); new `PUT /homelab/allowlist/{id}` edits target/kind/note/enabled in place (audited
  as `updated`); new `POST /homelab/allowlist/bulk` enables/disables/removes batches with ONE
  audited change record per batch; DELETE unchanged.
- **Collections framework**: a generic, reusable collection-manager UI component
  (`collectionManager(cfg)`) — search, filter presets, sortable columns, row selection with
  bulk actions, per-row actions, count footer; toolbar renders once so search keeps focus.
  Built for reuse by the R5 integration waves.
- **Targets surface**: first collection-manager instance, on the Networking sub-page —
  kind chips (ALLOW/DENY), target, note (edit-in-place), enabled, origin (`added_by`,
  including the v2.4.2 auto-allowlist attribution), and created timestamp all visible;
  add form with kind select; flip allow/deny, enable/disable, remove per row or in bulk.
- Tests (`TargetBlocklistTests`): deny-beats-allow (exact + CIDR carve-out), deny implies no
  allow for others, disabled deny ignored, kind normalization, upsert audits `updated`,
  bulk ops with single audit records, and the legacy-database column migration (idempotent
  across reopens, legacy rows default to allow).

## v2.5.3 — Console Refit R3: navigation + information architecture (docs/CONSOLE_REFIT.md)

The single-page console gains intentional structure: the Homelab page becomes eleven category
sub-pages, and every datum on it gets exactly ONE home.

- **Category sub-pages** via a sticky sub-nav: Overview (command summary, widgets, what-next),
  Services (deck, dependency graph, inventory tables), Virtualization (VMs), Containers,
  Storage (pools + backup intelligence), Networking (devices), Monitoring (health checks),
  Automation (rules + the approval-gated action pipeline), Apps (*arr), Alerts (incidents +
  risk findings), Activity (the audited change log). Cards declare their home with
  `data-hlsub`; the sub-nav filters visibility — no markup duplication, no second render path.
- **Redundancy audit (homelab scope)**: the three collapsible "secondary detail" mega-cards
  (Virtualization Detail, Network & Risk, Inventory Tables) were split so VMs, containers,
  storage pools, network devices, and risk findings each live on exactly one sub-page; the
  "open section as full page" duplication (`hl3Toggle`/`hl3PageFromSection`) was removed.
  All tbody ids are unchanged, so loaders, row delegates, connection cues, and subsystem
  theming keep working untouched.
- **Progressive disclosure without modal abuse**: on-demand drawers (entity detail, incident
  detail, + Add / Manage) stay reachable from every sub-page; guest/app full pages unchanged.
- **Keyboard nav extended**: `g h` opens Homelab; while on it, `1-9` / `0` / `-` switch
  sub-pages. The `?` shortcuts help documents both. The operator's last sub-page is restored
  per browser (localStorage), matching the existing last-page behavior.

## v2.5.2 — Console Refit R2: widget framework (docs/CONSOLE_REFIT.md)

One JS widget runtime for every dashboard tile — widgets are modular and page-agnostic
(they know their integration and kind, never where they render).

- **Runtime**: `widget(kind, integrationId, el)` with the full lifecycle — skeleton loading,
  labeled empty ("not published yet — appears after the next sync"), labeled error with retry,
  success via per-kind renderers. Per-kind TTL polling (15s–2min) that stops itself when the
  element leaves the DOM; manual per-widget and per-zone refresh (cache-busting); stale-data
  marking from the `updated_at` freshness the R1 API returns; render failures are contained to
  the widget. Data source: `GET /homelab/integrations/{id}/widgets/{kind}`.
- **Ten registered kinds**: health and queue render live *arr data today; statistics,
  disk-usage, resource-usage, recent-activity, calendar/upcoming, failed-imports, logs, and
  alerts have real renderers (key-value grid, usage bars, timestamped lists) over documented
  payload shapes and state honestly empty until the R5/R6 syncs publish them. Unknown kinds
  fall back to a generic renderer — a new server-side kind never breaks the console.
- **Layout registry**: per-operator, persisted via `/ui/state` (`widgets` key alongside the
  existing castes/positions — round-tripped, no backend change). Zones hold ordered arrays
  (add / remove / reorder today = drag-and-drop ready per the R2 plan).
- **First zone**: a "Widgets" card on the Homelab page — "+ Widget" picker offers only the
  widget kinds each connected integration declares in the catalog; responsive `.wgt-grid`
  auto-fill sizing with wide (2-column) list widgets.

## v2.5.1 — Console Refit R1: generic integration framework (docs/CONSOLE_REFIT.md)

The *arr pattern becomes the platform core: every connected app is now an `IIntegrationDefinition`
(kind, category, auth mode, widget kinds, GET-only sync) registered in `IntegrationCatalog` —
adding an integration is one class + one registry entry, zero schema or endpoint changes.

- **Contract + registry** (`IIntegrationDefinition`, `IntegrationContext`, `IntegrationCatalog`):
  deterministic C# `SyncAsync` receives a guarded context (base url + credential lookup + D1
  target guard) and returns typed widget payloads. Discipline inherited from the *arr
  implementation: GET-only clients, credential-store secrets (write-only, by id), allowlist
  before any I/O, strict timeouts.
- **New tables**: `integration_instances` (generalized `arr_apps`) and `integration_state`
  (integration id → widget kind → JSON payload + freshness) — the single source the v2.5.2
  widget runtime will read. Idempotent migration: legacy `arr_apps` rows move on first open and
  the old table is emptied; the legacy read/write surface (`ListArrApps` etc.) survives unchanged
  as a compatibility view, so the existing UI keeps working untouched.
- **First implementations behind the contract**: `ArrIntegrationDefinition` covers all seven
  *arr kinds (health + queue payloads via the unchanged GET-only `ArrClient`);
  `ArrSyncProvider` generalizes into `IntegrationSyncProvider` — one scheduler job (name kept:
  `arr-sync`) refreshing every enabled instance of every registered kind, one failure never
  failing the sweep.
- API: `GET/POST /homelab/integrations`, `DELETE …/{id}`, `POST …/{id}/sync`,
  `GET …/{id}/widgets/{kind}` (payload + `updated_at` freshness). Reads need `read_homelab`,
  writes need `manage_homelab_integrations`; v2.4.2 auto-allowlisting applies; secrets are never
  returned. `/homelab/arr` endpoints stay as the compatibility surface over the same tables.
- Tests (`IntegrationPlatformTests`): catalog metadata, state round-trip + freshness, removal
  deletes widget state, *arr compatibility view, legacy row migration (including
  cannot-double-run), structural allowlist refusal on `SyncAsync`, and sync-sweep filtering of
  disabled/unregistered instances.

## v2.5.0 — Automation rules (NORTH_STAR Phase 14)

Simple self-healing and alerting — low-risk automation only; risky actions still require approval.

- **Rule engine** (`AutomationEngine`, evaluated every 2 minutes on the shared HomelabScheduler —
  no private timers): triggers `service_down`, `repeated_health_failure` (N consecutive),
  `backup_failed_twice`, `disk_above_percent`, `unknown_device`; actions `propose_restart`,
  `alert` (v1.11 webhooks), `warn_event`, `open_incident`, `flag_risk`.
- **Double opt-in, fail closed**: the engine is behind `homelab_automation_enabled` (default OFF)
  AND every rule ships disabled — nothing self-heals until an operator turns on both.
- **Approval-required by construction**: `propose_restart` never executes anything. It files an
  ActionProposal through the v2.3 pipeline ("restart-once" rollback note, requested_by
  `automation:<rule>`), so human approval, the execution permission, the forbidden-action catalog,
  and HOMELAB_STOP all apply unchanged.
- **Triple loop prevention**: per-rule cooldown, max-runs-per-day cap, and no new proposal while a
  prior automation proposal for the same target is still pending.
- **Audit**: every fire/skip lands in `automation_runs` and on the homelab_events stream.
- API: `GET/POST /homelab/automation/rules`, `POST …/rules/{id}/enable|disable`,
  `GET /homelab/automation/runs`, `POST /homelab/automation/evaluate` (manual test tick).
  New tables `automation_rules`/`automation_runs` (idempotent migration).
- UI: Automation Rules card on the Homelab page — rules with enable/disable, recent runs,
  "Evaluate now".
- Tests (Phase 14 validation, fixed clock): rule trigger fires/quiet, disabled-by-default,
  cooldown, daily cap, restart-goes-to-pending-proposal-never-executes, no proposal stacking.

## v2.4.3 — Honest Ollama diagnostics (the "could not connect" lie)

Field-debugging an offline install surfaced a genuinely misleading failure mode: OllamaClient
treated EVERY non-2xx as "Could not connect to Ollama" (EnsureSuccessStatusCode throws
HttpRequestException into the connection-failure catch), while the header-chip probe only checked
/api/version. Net effect: Ollama up + model not pulled (the normal state of an offline machine,
which cannot `ollama pull`) showed a green chip and "connection" errors from every ant.

- **OllamaClient**: non-2xx responses now report the real status + body. A 404 says exactly what
  to do — the model is not available, run `ollama pull <model>`, and offline machines need the
  blobs copied in. True connection failures now name the configured host and point at the two
  usual suspects: Ollama binding only 127.0.0.1 by default (set OLLAMA_HOST=0.0.0.0 for LAN/LXC
  use) and ollama_host still pointing at localhost from inside a container/LXC.
- **System summary probe**: alongside `ollama_reachable`, a best-effort `/api/tags` check now
  publishes `ollama_model_present` for the configured model (name, name:latest, and base-name
  matches), so "reachable but model missing" is visible state, not a mystery.


## v2.4.2 — Registering a host or app auto-allowlists its address

Live operator feedback: adding a host or an *arr app and THEN separately allowlisting the same
address was pure friction — and forgetting the second step produced silent "sync refuses to
connect" dead-ends. Now `POST/PUT /homelab/hosts` and `POST /homelab/arr` auto-add the address to
the D1 target allowlist when it is not already on it (audited entry, note says what registered
it).

Safety boundary unchanged: both endpoints already require `manage_homelab_integrations`, so the
operator's registration IS the declaration of intent. Provider sync paths deliberately cannot
allowlist anything — a sync never widens D1 — and the general SSRF guard for LLM-directed tools
is untouched.


## v2.4.1 — Dynamic Service Deck, node metrics, guest pages, *arr-stack apps

Driven by live operator feedback on v2.3.2 ("fully dynamic and versatile... everything visible,
nothing nested"). Homarr (open source, homarr.dev) is the referenced UX model for the apps/deck
behavior.

- **Nothing nested**: the collapsible detail sections are gone. Virtualization Detail,
  Network & Risk, and Inventory Tables now open as FULL sub-pages with a ✕ Close button at the
  top (one shared overlay engine, `hl3PageOpen`, moves the live DOM section in and back out —
  all existing table renderers keep working untouched).
- **Dynamic deck**: every node card and every VM/container/service tile can be hidden (✕ on
  hover) and restored from a visible "Hidden (n)" tray — per-browser persisted. Deck grouping
  bug fixed: Proxmox guests now group under their real host card (node_id is the full
  `pve-node:host:name` id, not the bare node name).
- **Node resource metrics**: new `node_metrics` table + `GET /homelab/metrics/nodes`. The
  Proxmox sync now persists per-node CPU %, cores, RAM used/total, disk used/total, and uptime
  from the `/nodes` payload it already fetched; deck host cards render CPU/RAM/DISK bars
  (75%/90% warn/danger thresholds). Unreported metrics stay `-1` — shown as "—", never
  fabricated (ESXi/Docker/Hyper-V report what their read-only surface provides).
- **Per-VM / per-LXC pages**: clicking a guest tile opens a dedicated page — live status, vCPU/
  RAM/uptime facts, related recent events, and one-click approval-gated action shortcuts
  (start / clean-stop / restart / snapshot / backup) that pre-fill a proposal, never execute.
  Node cards open a matching per-node page (facts, metric bars, guest tiles).
- ***arr-stack integrations (the full mainstream family)**: sonarr, radarr, lidarr, readarr,
  whisparr, prowlarr, bazarr. One structural GET-only `ArrClient` (no write method exists)
  covers all seven — X-Api-Key auth, API key write-only in the credential store, host must be
  on the D1 allowlist, 10s timeout. A deterministic `arr-sync` job on the shared scheduler
  refreshes version / health warnings / queue depth. New Apps card renders Homarr-style tiles
  (color-coded, status dot, queue badge); each app opens its own page with Open/Sync/Remove.
  Endpoints: `GET/POST /homelab/arr`, `DELETE /homelab/arr/{id}`, `POST /homelab/arr/sync`
  (reads `read_homelab`, writes `manage_homelab_integrations`).
- **Tests**: `ArrIntegrationTests` — kind catalog completeness, allowlist refusal before I/O,
  missing-key refusal, arr_apps/node_metrics round-trips, secrets-never-stored.

## v2.4.0 — Backup + restore intelligence (NORTH_STAR Phase 13)

Know what is protected, what is not, and what recovery looks like. Deterministic arithmetic over
real inventory/backup/dependency data — no LLM, no invented values; unknown fails toward caution.

- **backup_inventory finally live**: the v1.9.0 table gains `UpsertBackup`/`ListBackups` accessors,
  `GET/POST /homelab/backups` (reads read_homelab; registering a record — PBS/NAS jobs, manual —
  needs manage_homelab_integrations, audited to homelab_events).
- **Coverage map** (`GET /homelab/backup/coverage`): every VM and container classified ok / stale
  (> 7 days) / failed / none. A record whose status says "ok" but has never succeeded counts as
  NONE. Includes per-target restore confidence (0–100 from recency, verified status, artifact size,
  location) and restore priority (criticality via runs_on dependencies first, least-recoverable
  first).
- **Blast-radius simulation** (`GET /homelab/backup/impact/{nodeId}`): what dies if this node
  fails — VMs, containers, dependent + hosted services, critical/high count, and which casualties
  have NO restorable backup.
- **Restore runbooks** (`GET /homelab/backup/runbook/{kind}/{id}`): deterministic step lists from
  the real records — artifact location + confidence, explicit STALE/FAILED warnings, and an honest
  "STOP — rebuild, not restore" when no artifact exists. Never pretends a restore path exists.
- **UI**: Backup Intelligence card on the Homelab page — coverage totals, ranked table with
  coverage badges and confidence, one-click runbook per target.
- **Flake fix**: `ListActionProposals` gains an id tiebreaker — created_at is second-resolution,
  so same-tick proposals ordered nondeterministically (the Windows-only supersede test flake).
- Tests (Phase 13 validation): coverage classification matrix, priority ranking, node-loss blast
  radius incl. unprotected casualties, runbook generation (covered / uncovered / unknown), and
  idempotent backup upsert — all on an injected fixed clock, nothing time-flaky.

## v2.3.2 — Homelab Service Deck + write-runner hardening

Two things in one release: a full-replace redesign of the Homelab console page driven by live
operator feedback ("everything that is added needs to be visible"), and the hardening pass on the
v2.3.1 Proxmox write runner.

### Homelab Service Deck (UI full-replace)

The old page was config-first: two viewports of registration forms and credential cards before any
data, hung "Loading..." blocks when endpoints were slow, an empty dependency graph consuming half a
viewport, and the actual homelab (hosts, synced VMs/containers, services) visible only as counts.
Now:

- **Service Deck front and center** — a Homarr-style tile grid grouped by host: every registered
  host and every synced virtualization node is a card; its VMs, containers, and services are live
  tiles with status dots (guest state / latest health-check result), click-to-open service URLs,
  host/service detail drawer on click, and a ⚡ shortcut that pre-fills an approval-gated action
  proposal (restart VM/CT/service) for that exact target.
- **Config out of the way** — every registration form (host, service, device, health check,
  dependency) plus Subsystem Status and the Virtualization Connections cards moved into one
  "+ Add / Manage" drawer, hidden until asked for.
- **Secondary tables collapsed** — VM/CT/storage, network & risk, and inventory tables are
  collapsible sections (state persisted); Health, Actions, and Incidents stay first-class.
- **No dead space, no dead ends** — the dependency graph auto-hides until relationships exist,
  and the command-summary / what-next blocks replace themselves with a labeled fallback after 7s
  instead of loading forever (relevant under reverse-proxy 503 bursts, observed live).
- All additive within the single vanilla HTML/CSS/JS console; every existing element id, endpoint,
  and behavior preserved.

### Proxmox write-runner hardening (was staged as v2.3.1.1)

Review of the first write-capable client found three real gaps and two smaller defects; all fixed
at the root:

- **D1 target-allowlist enforcement (safety)**: `ProxmoxActionClient` never consulted the homelab
  target guard — the one client that can *change* infrastructure was the only one skipping the
  allowlist every read-only client honors. The guard is now a required constructor dependency and
  is checked before ANY request (writes and the verification GET alike); a non-allowlisted host is
  refused before I/O with a pointer to Homelab → Allowlist. Tests prove both paths refuse.
- **Node-segment injection (safety)**: `TryParseTarget` accepted any characters in the node part
  of a `node/vmid` target. A target like `pve1?x=y/104` passed the structural path allowlist
  (its regex sees `[^/]+`) while the emitted HTTP request went to a *different* path with an
  injected query string. The node segment is now validated against `^[A-Za-z0-9._-]+$` so the
  validated path and the emitted path are always the same bytes. Injection targets are CanRun=false.
- **Mock runner shadowed the real runner**: runners are matched first-CanRun-wins, and the dev
  mock runner (which claims every catalog action) was registered before the Proxmox runner — with
  `homelab_mock_providers_enabled` on, a real `start_vm` was "executed" by the mock and reported
  success without touching anything. The mock is now registered last.
- **dry_run_available always false for Proxmox actions**: the propose-time probe called
  `CanRun` with an action-type-only stub, which the Proxmox runner rejects (it also validates the
  target form). The probe now uses the real proposal.
- **Stale guidance + xUnit2012**: the no-runner error still said "runners arrive in v2.3.1"; it
  now points at `homelab_proxmox_write_actions_enabled` and the `node/vmid` target form.
  `JsonSafetyTests` uses `Assert.DoesNotContain`, clearing the analyzer warning. HOMELAB.md phase
  row updated to cover v2.3.1/v2.3.1.1.

## v2.3.1 — ProxmoxActionRunner: the first write-capable infrastructure runner

Completes NORTH_STAR Phase 12: the v2.3.0 approval pipeline now controls real Proxmox VE.

- **`ProxmoxActionClient`** — a NEW client, deliberately separate from the GET-only v1.12 read
  client (which is untouched, keeping its structural read-only guarantee). It can emit only the
  endpoint shapes the action catalog needs: guest `status/(start|stop|shutdown|reboot)`,
  `snapshot`, and `vzdump`. Any other path is refused structurally before network I/O — it cannot
  be used as a general Proxmox client. Token comes from the credential store per client (same
  pattern as the read client); never cached in config, never logged.
- **`ProxmoxActionRunner`** — runs the approved catalog actions start/stop/restart VM + container
  (`stop` maps to guest-clean `shutdown`, never a hard stop), `create_snapshot` (timestamped
  `anthill-*` name), `run_backup` (vzdump). Targets use the inventory form `node/vmid`
  (e.g. `pve1/104`); anything else refuses to run. Dry-run names the exact endpoint it would hit.
  Post-execution verification polls the guest state (~15s) and reports honestly — a submitted
  backup task is reported as submitted, not silently assumed complete.
- **Double-gated registration**: the runner exists only when the Proxmox integration is enabled
  AND the new `homelab_proxmox_write_actions_enabled` (default **false**) is set — connecting
  Proxmox read-only never silently grants write capability. Every execution still passes the
  v2.3.0 executor guards: HOMELAB_STOP, approved-state TOCTOU re-check, catalog/forbidden-list
  re-check, mandatory rollback note, full audit.
- Tests: CanRun matrix (unsupported/forbidden/malformed targets refuse), dry-run accuracy,
  structural path-allowlist refusal (config/user-admin/suspend all throw before I/O), and the
  clean-shutdown mapping guard.
- Frontend: no open UI defects found this pass — the v2.2.6 audit items and the v2.2.6.1 Proxmox
  privsep sync gotcha remain the last known issues, all fixed. The Actions panel shipped with
  v2.3.0 works unchanged against the new runner (runner name appears in dry-run/execute output).

## v2.3.0 — Approval-gated homelab actions (NORTH_STAR Phase 12, framework release)

The v1.14.0 IApprovable/ActionProposal design gains its execution side. Scope decision: framework
first — the full pipeline ships with **local + mock runners only**, so the first write-capable
infrastructure client (Proxmox power/snapshot/backup) lands in v2.3.1 as its own isolated,
reviewable diff.

The pipeline (every safety property enforced in the executor, with a test on it):

- **Propose** (`POST /homelab/actions/propose`, `manage_homelab_integrations`): validated against
  the allowlisted `ActionCatalog` — restart_service, start/stop/restart VM + container,
  create_snapshot, run_backup, resolve_incident, update_inventory, run_diagnostic. The NORTH_STAR
  forbidden set (delete VM/LXC/container, firewall changes, factory reset, wipe disk, secret
  modification, backup disable) is refused by name, and anything unknown is refused by default.
  Proposals persist in the new `action_proposals` table (idempotent migration) and dedupe by
  `action_type:target_id` — the newer pending proposal supersedes the older.
- **Blast radius**: deterministic `BlastRadius` scorer (plain arithmetic, no LLM) over the rubric
  fields shipped in v1.14: dependency fan-out (computed from the v1.10 dependency map), service
  criticality (unknown scores as high — fail toward caution), backup coverage, internet exposure,
  rollback-note presence (largest single penalty), action class. Score + explanation land on the
  proposal and drive the risk badge.
- **Approve / Reject** (`POST /homelab/actions/{id}/approve|reject`, `approve_homelab_actions`):
  pending-only, records decided_by/at, audited. A rollback note can be added or refined via
  `POST /homelab/actions/{id}/rollback-note` (score honestly recomputed).
- **Execute** (`POST /homelab/actions/{id}/execute`, `execute_homelab_actions` — a separate
  permission from approval): checks the HOMELAB_STOP kill switch FIRST, re-reads state at
  execution time and refuses anything not `approved` (TOCTOU guard), re-checks the catalog
  (a forbidden record written around the API still never runs), requires a rollback note, runs
  the matching runner, then runs post-execution verification and reports it honestly
  (`… | verify: ok/FAILED`). Failures keep state `approved` with an `execution_failed` audit
  event — retry is explicit, never silent.
- **Dry run** (`POST /homelab/actions/{id}/dryrun`): describes exactly what would happen, never
  executes, never changes state.
- **Runners**: `LocalActionRunner` (resolve_incident, update_inventory, run_diagnostic — touch
  only ANTHILL's own database, zero network) and `MockActionRunner` (deterministic harness,
  registered only behind the existing `homelab_mock_providers_enabled` gate).
- **Kill switch**: `HomelabActionControl` mirrors AutonomyControl — durable on-disk
  `.anthill/HOMELAB_STOP` sentinel OR in-process flag; no auto-clear. `POST
  /homelab/actions/stop` (approve permission — halting must be easy) / `resume` (execute
  permission — un-halting is an execution-grade decision). Sentinel scope is disjoint from the
  autonomy STOP file.
- **One queue**: action proposals project into `GET /homelab/approvals/unified` beside patches
  (`kinds: ["patch","homelab_action"]`) via `ApprovableProjections.FromActionProposal`; the
  Overview approvals card routes decisions by kind. The v1.14 IApprovable contract is unchanged —
  `ActionProposal` gained only additive execution metadata (payload, blast-radius score/
  explanation, decided/executed stamps, execution result).
- **UI**: new Actions panel on the Homelab page — propose form (action list served by the API),
  proposal table with risk/blast-radius/rollback/result columns, approve/reject/dry-run/execute
  buttons, and the kill-switch toggle with engaged-state banner.
- **Fail closed**: `approve_homelab_actions` and `execute_homelab_actions` capability gates STILL
  default OFF. A fresh v2.3.0 install cannot execute anything until an operator enables them.
- **Tests** (`ActionApprovalTests`): approval gate (pending/rejected refused), forbidden actions
  refused at propose AND at execute (including a record smuggled straight into the store),
  kill-switch halt + resume, mandatory rollback note, dry-run leaves state untouched, dedupe
  supersede, deterministic blast radius + caution-on-unknown, local incident-resolve with
  verification, unified projection shape, and fail-closed capability gates.

## v2.2.6.1 — Proxmox sync: surface the privsep "nodes-only" gotcha in the UI

Live-testing a freshly-connected Proxmox VE integration turned up a confusing dead-end: a manual
"Sync now" succeeds (HTTP 200, `proxmox sync ok (N items)`) yet the VM / Container / Storage tables
stay empty. Root cause is not in ANTHILL — it is Proxmox's read-only API-token model:

- A privilege-separated (`privsep=1`) API token's effective permissions are the **intersection** of
  the backing user's permissions and the token's own ACL. If the token holds `PVEAuditor` on `/` but
  the backing user holds nothing, the intersection is empty for `VM.Audit` / `Datastore.Audit`.
- Proxmox then returns **HTTP 200 + an empty list** (never 403) for `/nodes/{node}/qemu`, `/lxc`,
  `/storage`. Node *listing* is not gated the same way, so the sync finds the nodes and reports them
  as items while pulling zero guests underneath — exactly the "success but no data" symptom.

Fix (UI only; the sync path and every client stay read-only and unchanged):
- `hlSyncVirt()` now detects a Proxmox sync that returned nodes but left the VM/container/storage
  inventory empty, and reports the actual cause inline: grant the **backing user** the `PVEAuditor`
  role too (`effective perms = user ∩ token`). No more silent "ok" over three empty tables.
- On failure it now surfaces the error and stops rather than falling through to a generic message;
  on success it keeps the full `loadHomelab()` refresh (node graph + inventory tables).

Operator fix on the PVE host: `pveum acl modify / --roles PVEAuditor --users <user>@<realm>`.

## v2.2.6 — Cleanup + hardening pass (no new features; framework checkpoint before V2.3.0)

Full audit of the v2.2.x churn; every finding fixed at the root:

- **Resource Usage card fixed**: it read `cpu_percent`/`memory_percent`/`disk_percent` fields the
  API never provides, so it was permanently "Metrics unavailable". It now renders the real
  governor signals (CPU load/core, memory used, backend latency, concurrency) published by the
  dashboard poll — the same data the retired hidden metrics row used.
- **One System Core state machine**: pollHud (which sees autonomy, objectives, patches, and
  provider health) is the single computer of core state; the System Core card renders its
  published state, so AUTONOMY ONLINE / provider-offline can no longer be silently under-reported.
- **Hidden legacy panels deleted for real**: the display:none HUD strip and metrics row (still
  fully re-rendered every 6s) are gone — markup, writers, and their whole CSS families
  (.hud-strip/.hud-metric/.hud-dash-grid/JARVIS-orb rules). No more orphaned element lookups.
- Telemetry-bar ant count no longer goes stale (registry re-requested through the TTL cache).
- **⌂ Reset layout** button returns dragged chambers to the default map layout.
- System Core orb colors now use design tokens (raw hexes removed).
- Hardening: ids embedded in inline map handlers pass through `attrSafe()` (strips every JS-string
  breakout character; defense-in-depth over escapeHtml). Fast drag-release can no longer trigger
  an accidental chamber expand. Expanded view uses proper concentric rings (no dot overlap in
  large chambers).
- **New regression guards** (run in `dotnet test` and CI): CHANGELOG top entry must equal the
  runtime version (tag-ordering mishaps); no orphaned getElementById targets and no duplicate ids
  in the UI; registry adapter accessors must stay case-tolerant (the "Other · 25" class of bug).
- NORTH_STAR annotated with the shipped v2.2.1–v2.2.6 patch series; V2.3.0 (approval-gated
  homelab actions) is next.

## v2.2.5 — Fix: tunnels visible between ALL chambers, not just Queen ↔ Mission Control

- Delegation tunnels were drawn for every chamber but idle ones used the near-invisible border
  color at 35% opacity — only the active Queen ↔ Mission Control run could be seen. Every tunnel
  now renders in its chamber's role color (subtle when idle), curved like dug tunnels, and lights
  up with an animated glow-flow when that chamber has active ants — so pheromone traffic back to
  the Queen is followable across the whole colony. Command chain unchanged: Queen → Mission
  Control → every chamber, honoring dragged chamber positions.

## v2.2.4 — Chamber delegation lines, draggable chambers, ant duties in every inspector

- **Live delegation lines on the chamber map**: Queen → Mission Control → each chamber, mirroring
  the classic engine's structure. Lines stay faint when idle and light up in the chamber's role
  color with an animated flow when that chamber had ants active in the last 15 minutes — live
  delegation is now visible in Chamber/Expanded just like Live Colony. (Motion setting and
  prefers-reduced-motion both disable the animation.)
- **Chambers are draggable**: grab any chamber group and move it, same as classic ants; positions
  are normalized and persisted per operator (`anthill.colony.chamberPos`); a drag never triggers a
  selection; "Reset view" is unaffected (pan/zoom only).
- **Per-ant duties in the map inspector**: selecting a chamber lists every ant with its registry
  Purpose (e.g. ScribeAnt — what it does), each row click-selects that ant; selecting an ant shows
  a PURPOSE section. Real registry data only — ants without a purpose simply omit the line.
- **Ant Inspector page shows the whole colony**: below the six legacy telemetry castes (which keep
  their real task stats) a new COLONY DIRECTORY lists every registered role and worker with role
  color and duty, so any ant can be inspected — not just researcher/web/file/coder/builder/verifier.
- Chamber map adapter now carries registry Purpose fields end-to-end (case-tolerant).
- **Classic-mode view switcher fixed**: the floating map toolbar was clipped at the top-left in
  Live Colony mode. It's now hidden there entirely; "🗺 Chamber Map" / "🗺 Expanded Map" buttons
  live inside the classic canvas's own top-right viewbar (Command/Expanded/Active/Groups/Handoffs),
  which is already correctly positioned. The full map toolbar (motion/labels/pheromones/reset)
  still appears in Chamber/Expanded modes.

## v2.2.3 — Repair: Chamber/Expanded role detection ("Other · 25"), Colony dead space, Overview grid balance

- **"Other · 25" root cause fixed**: the registry serializes PascalCase (`RoleId`/`DisplayName`/
  `Workers`); the chamber adapter only tried camelCase, got '' for every role, and classified the
  whole colony as Other. The adapter now uses the same case-tolerant accessors as the classic Live
  Colony engine (which is why that view was unaffected) and falls back to the display name before
  giving up — only truly unmatchable ants land in Other. Dev-only console.debug reports total
  ants / chambers / unclassified samples.
- **Colony dead-space fixed**: the floating VIEW bar carries an inline `position:relative` (for
  cmap-mode layering) which overrode the classic-mode absolute float, leaving the bar in flow as
  an empty ~350px row column. Now `!important`-scoped; the bar floats top-right over the canvas.
- **Expanded view repaired**: with no chamber selected it shows every ant in every chamber (multi-
  ring layout, never a single placeholder circle) with a "Select a chamber to focus" hint; ant
  dots are clickable in all views (existing selection/inspector handlers), active/selected ants
  get labels and a breathing pulse (active-only animation, motion setting + reduced-motion safe).
- **Inspector**: with nothing selected it now shows a colony summary (total ants, active count,
  per-chamber breakdown with click-to-select) instead of an empty prompt.
- **Overview grid rebalanced**: Operator Attention and Mission Command moved INTO the 12-column
  grid (they were stranded below it beside retired hidden panels, leaving a huge blank region);
  Recent Events restored as the third row-3 card (reuses pollOv2 data, no extra polling); retired
  hidden System Core panel removed from the DOM (legacy writers are null-guarded); consistent card
  min/max heights with internal scroll; responsive 2-col/1-col fallbacks. Colony Vitals remains
  full-width below the grid.
- Overview System Core ant count now uses the case-tolerant worker accessor (was undercounting).

## v2.2.2 — Fix: classic live colony default + Raw blank-canvas; Overview condensed, System Core functional

Live feedback on v2.2.1: the original colony experience — every individual ant visible and
draggable, live pulses on activation, the pheromone canvas — is the heart of the page and must be
the default, and switching to it was broken (blank screen).

- **Blank "Raw Graph" root cause fixed**: the v2.2.1 `width:100%;flex-shrink:0` rule on the
  telemetry/view bars applied unconditionally — in the classic flex-ROW layout those full-width
  bars consumed the entire row and pushed the canvas and panels past the `overflow:hidden` edge.
  Rule is now scoped to `.cmap-mode`; in classic mode the telemetry bar hides and a compact VIEW
  switcher floats over the canvas instead.
- Additionally, the classic canvas now gets its full delayed boot sequence (resize → buildNodes →
  legend → pheromone poll) after being unhidden, mirroring the original page-enter path — the
  v2.2.1 toggle ran only a synchronous resize against a hidden canvas.
- **Classic view is the default** (renamed **🐜 Live Colony** and listed first): all original
  behavior — per-ant dots, drag-to-move, rename, pan/zoom, activation pulses, pheromone trails —
  untouched and primary. Chamber/Expanded remain as opt-in overview modes.
- One-time preference migration: v2.2.0/1 had persisted 'chamber' as everyone's stored default;
  reset once to the classic view, after which the operator's choice sticks.
- **System Core card fixed**: it was scraping state from the hidden legacy HUD panel's DOM and sat
  on a stale "idle" forever. It now computes state (IDLE / MISSION ACTIVE / OPERATOR ACTION /
  ALERT) from the live jobs/missions/approvals data each poll — same rules as the original core —
  and shows a state-colored pulsing orb (reduced-motion safe).
- **Overview condensed to 6 cards**: removed Tasks Today, Chamber Activity, Recent Events, and
  Mission Timeline (all visible in the Events / Colony / Missions tabs); Mission Queue folded into
  the Missions card. Overview now shows only cross-cutting status not duplicated elsewhere.
- Adopted phased colony evolution plan: classic graph is canonical (Phase 1); Living Colony map is
  an optional mode (Phase 2) fed by an adapter over live data (Phase 3); it becomes default only
  after functional parity (Phase 4).

## v2.2.1 — Fix: Colony map layout/pan-zoom + Overview de-duplication (live feedback on v2.2.0)

- **Colony map was crushed and immovable**: `#page-colony` is a flex ROW (for the original canvas
  layout), so the v2.2.0 map/toolbar/inspector were squeezed in as row items — tiny map, scattered
  controls. Chamber/Expanded views now switch the page to an immersive column layout: the map is
  big and front-and-center (`calc(100vh - 250px)`), the view toolbar is one compact row, the
  inspector is narrower and collapsible (⇤), and the original row panels hide. **Raw Graph
  restores the untouched original page exactly as before.**
- **Pan/zoom added**: scroll to zoom (cursor-anchored, clamped), drag the background to pan,
  Reset-view button; chamber clicks/double-clicks unaffected; no animation involved so
  reduced-motion is unaffected.
- **Overview had two System Cores and duplicated metrics**: the old HUD strip, HUD System Core
  panel, and HUD metric row are retired (their data lives on in the grid: telemetry bar, System
  Core card — which now also carries the live core state — Tasks Today, and Colony Health).
  Mission Command and Operator Attention remain below the grid; every handler and poller is
  untouched, only the duplicate presentation is hidden.

## v2.2.0 — 🐜 Overview + Colony living console + performance/auth/Proxmox stability pass

Four passes in one release. A/B/C deliver the Overview command center and the Living Colony Map;
D is the production-stability pass (auth floods, load times, Proxmox no-TLS, caching/polling).
Also renumbers the NORTH_STAR build order: the two unplanned insertions (v2.1.0 multi-hypervisor,
this release) shift the remaining planned phases by two minors (approval-gated actions → V2.3.0).

### Pass A — ANTHILL design system
- `:root` theme tokens (colony palette, full role-color system, status colors, glows) + glassy
  card/pill/role-badge primitives. `getRoleColor`/`getRoleLabel` are THE single role mapping for
  chambers, nodes, event dots, badges, trails, and legends.
- **TopTelemetryBar** on Overview and Colony: colony online state, task count with real
  delta-derived tasks/sec, success rate derived from the live event stream (1 − failures/events),
  active ant count from the registry, pending approvals, health pill, colony search. Every value
  is real or an em dash — nothing invented.

### Pass B — Overview command center
- 12-column responsive grid with all eleven required cards: Colony Health (real-signal scoring +
  session trend), Active Mission, Tasks Today (hourly bars from event timestamps), Pending
  Approvals (top 3 from the unified IApprovable queue, wired to the EXISTING doApproval
  handlers — approval security untouched), compact System Core (registry roles orbiting the
  Queen; click → Colony), Chamber Activity, Resource Usage ("Metrics unavailable" until real
  metrics exist), Recent Events, Quick Actions, Mission Timeline, Mission Queue.
- The existing HUD (core orb, attention panel, mission command node) is fully preserved below —
  every element id and handler intact. Clear empty states on every card.

### Pass C — Living Colony Map
- Chamber-based, Queen-centered SVG map over the deterministic normalized layout; chambers show
  role color, ant counts, active counts, representative ant dots (all ants when expanded).
- Three view modes: **Chamber / Expanded / Raw Graph** — Raw Graph is the untouched original
  canvas, so every ant remains reachable the old way too.
- Animated **pheromone trails**: width/opacity from real trail scores when present, otherwise
  derived from recent event frequency (mapping isolated in the loader, labeled as derived).
- **Inspector panel** for chambers (counts, active ants, top ants, Expand/Ant Config/Inspector),
  ants (chamber, status, recent events, Inspect/Logs), and trails (strength + recent flow).
- **ColonyMapControls** persisted in localStorage (`anthill.colony.*`): view mode, motion
  (Off/Low/Normal/High), labels, pheromone visibility, idle-ant toggle. Telemetry search finds any
  ant and jumps to its chamber. All motion honors `prefers-reduced-motion`.

### Pass D — performance, auth, Proxmox, stability
#### Fixed
- **Auth request floods / "Too many attempts; try again later"**: the first 401 now flips a
  global auth-lost gate — every poller short-circuits locally (zero network traffic) until
  re-login clears it. Text endpoints share the same gate.
- **Slow Overview / Patch Center loads + duplicate traffic**: identical in-flight GETs are
  deduplicated into one request; per-path TTL caching (events 3s, jobs 5s, summaries 10s,
  registry/pheromones 20–30s, patches 30s) with stale-while-error keeps cards rendering from
  cache while refreshing in the background.
- **Proxmox GET /nodes 401 in no-TLS/http mode**: the client hardcoded `https://`. New
  `homelab_proxmox_protocol` (http|https) is separate from TLS verification; auth headers attach
  identically in every mode; unknown protocols fall back to https.
#### Improved
- Hidden browser tabs serve cached data instead of polling; 429 responses trigger a respected
  Retry-After backoff (clamped 5–120s) instead of immediate retries; every request carries a 10s
  AbortController timeout with a structured error.
- Mutations (POST/PUT/DELETE) bust the GET cache so the UI never shows stale state after actions.
#### Added
- `POST /homelab/proxmox/test`: connection test with actionable diagnostics — distinguishes
  unreachable host / protocol mismatch / TLS-certificate issue / invalid credentials / permission
  denied / success (PVE version) — and never prints token material.
- `ProxmoxIntegrationTests`: protocol-selection tests (http BaseUrl, https default, junk-protocol
  fallback) and an explicit auth-header-over-http assertion.

## v2.1.0.1 — Allowlist + subsystem gates surfaced on the Virtualization Connections panel

Field fix: a real operator hit "Proxmox connected, credential configured, but no VMs/containers/storage."
Root cause was the **target allowlist** — a hard gate in front of every homelab request — and the v2.1.0
connection panel gave no way to see or fix it, so following the form (enable → host → credential → save
→ sync) failed silently once the sync hit the allowlist. Also the homelab **subsystem/scheduler master
gates** were "edit config.json" only.

- Each connection card now shows **host allowlist status** ("allowlisted" / "NOT allowlisted — requests
  are blocked") with a one-click **"Allow this host"** button (`POST /homelab/allowlist`). `VirtStatus`
  gained `host_allowlisted`.
- A **subsystem bar** at the top of the panel shows `homelab_enabled` / `scheduler_enabled` and lets an
  admin flip them (with a restart note, since scheduled syncs (de)register at startup). The unified
  status endpoint now returns those two gates.
- Result: hooking up a hypervisor is now type host → save cred → **Allow this host** → Sync — entirely in
  the panel, no config.json or curl. (The integrations themselves are unchanged and still read-only.)

## v2.1.0 — Multi-hypervisor read-only inventory + Virtualization Connections UI

Extends the read-only virtualization layer beyond Proxmox to **VMware ESXi/vCenter, Docker, and
Hyper-V**, and makes every integration configurable from the console (previously Proxmox could only be
set up by hand-editing `config.json`). Enterprise-geared and read-only end-to-end — every client is
read-only *by construction*, exactly like Proxmox.

- **New read-only clients + inventory providers** (each disabled by default, credential in the store,
  host gated by the target allowlist, `AllowAutoRedirect = false` SSRF hardening):
  - **Docker** — Engine API over TLS (or a read-only socket proxy). GET-only: no
    start/stop/kill/remove/exec exists in the client. Syncs the engine as a container-host node plus
    its containers and volumes.
  - **VMware ESXi / vCenter** — vSphere REST. The only non-GET is a single `POST /api/session` (auth
    only — mints a session token, changes nothing); all inventory reads are GET. A built-in Read-only
    role is enough. Syncs hosts → hypervisor nodes, VMs, and datastores.
  - **Hyper-V** — WinRM / WS-Management, restricted to the read-only WMI `Enumerate` of
    `Msvm_ComputerSystem` (no `Invoke`/`Put`/`Create`/`Delete`, no command shell). Syncs the host node
    and its VMs.
  - All four project into the same inventory tables through one `IInventoryProvider` shape and a
    unified `GET /homelab/virtualization/status` + `POST /homelab/virtualization/{kind}/sync`.
    Providers are built **on demand from current config**, so a connection edited in the UI works on
    the next sync without a restart.
- **Virtualization Connections panel** in the console: one card per integration (enable, host, port,
  credential id, skip-TLS, plus an inline write-only "Save cred"), Save + Sync-now, and live status
  (credential configured / active). Wire-level tests prove each client stays read-only (every request
  a GET / Enumerate; the vSphere session is the only POST) and that the allowlist blocks unlisted hosts.
- **Dependency graph** now renders hosts as **boxes** and services as **pills**, coloured by kind with
  a status-coloured border and a legend, so "host vs service" reads at a glance. (Delete-dependency and
  the full host/dependency tables already shipped in v2.0 — the delete `✕` and Actions column are live.)

## v2.0.0 — 🐜 Homelab Command Center launch (NORTH_STAR Phase 11)

The V2 era begins: everything the V1.9–V1.14 line taught ANTHILL to know, in one living console
view. Built in two deliberate passes (functional data layer first, identity layer second), still
read-mostly: visibility, not control. Answers the eight NORTH_STAR questions at a glance — what is
broken, where it runs, what it depends on, what changed, what to do next, what is not backed up,
what is exposed, what is unknown.

### Pass 1 — functional data layout & routing
- **One aggregation endpoint** `GET /homelab/dashboard`, assembled by the pure, testable
  `CommandCenter` builder: entity counts, latest-per-target health rollup, active incidents, open
  risk errors/warnings + top findings, storage used/total + backup-capable pool count, last
  health/proxmox/risk job stamps, pending-approvals count, failed checks, recent changes, the full
  dependency graph, and deterministic **"What Should I Do Next"** recommendations (derived only
  from real signals: failing targets, error incidents/findings, pending approvals, missing checks).
- **Dependency graph as a first-class feature**: nodes for every host and service (status from
  health data — `unknown` when unchecked, never assumed healthy), edges from implicit `runs_on`
  placement plus the mapped dependency table, **failure impact propagation** (a failed service
  marks its host worst-of and every touching edge impacted), exposure and open-incident flags per
  node, click-to-select highlighting connected paths and listing **transitive dependents** —
  "what depends on this?", answered visually and via `GET /homelab/graph/dependents/{id}`.
- **Host & service detail drawers**: facts, status, uses/depended-on-by, related active incidents
  and recent changes — opened by clicking any Hosts/Services row.
- Tests (`CommandCenterTests`): empty state fabricates nothing (stamps stay empty, approvals stay
  -1), aggregation faithfulness, graph edge construction, impact propagation through hosts and
  dependent paths, node flags, transitive dependents, recommendation determinism.

### Pass 2 — the ANTHILL identity layer
- **Centralized semantic tokens** (`#hl-theme` CSS variables): health `--hl-health`, compute
  `--hl-compute`, storage `--hl-storage`, security `--hl-security`, incidents `--hl-incident`,
  memory/history `--hl-memory` — applied as card spines + section-head dots via one decoration
  helper, consistent across chips, cards, and graph nodes.
- **Colony-mesh background**: a pure-CSS low-contrast node/tunnel lattice behind the dashboard
  (opacity ≤ .05, pointer-events none) — colony identity without touching readability.
- **Command summary strip**: KPI chips (hosts, services, healthy/degraded/failed, incidents,
  risks, VMs/CTs, storage+backup, pending approvals) with a **colony-link dot** derived strictly
  from real job stamps (green pulse = a scheduler job ran in the last 15 minutes; amber = idle;
  gray = never — labeled, never fabricated).
- **Purposeful motion only**: pulse on failed/incident graph nodes and the live dot, row-flash
  **connection cues** (click a failed check → related incidents flash; click a risk finding → the
  services it names flash), hover emphasis on graph nodes — all disabled under
  `prefers-reduced-motion`.
- Every new visual degrades to labeled empty states ("no data yet", "not configured", "no graph
  yet") — no value is ever invented for visual completeness.
- Page renamed **Homelab Command Center**; no framework migration, single embedded vanilla
  HTML/CSS/JS preserved, all existing routes/pages stable.

## v1.14.0.1 — Unified approvals dedupe: collapse older pendings even when the newest is resolved

Bug-finder/tester pass over the v1.14.0 code (last stop before V2.0). The incident/change-memory and
`IApprovable` design hold up well — deterministic, repo-only, correct SQL, well-tested. One real
logic bug in the unified-queue dedupe:

- **`ApprovableProjections.DedupePending`** (behind `GET /homelab/approvals/unified`) only superseded
  older pending duplicates when the *absolute newest* item in a dedupe group was itself pending
  (`ordered[0].State == "pending"`). If the newest item was already approved/rejected/executed while
  two older duplicates were still pending, **both** older items stayed pending — the unified queue
  would show two live pending approvals for the same target, violating the stated "at most one pending
  per key" invariant. Now it keeps the newest still-pending item and supersedes every older pending
  one regardless of the newest item's state. Added a regression test for the newest-non-pending case
  (the existing test only covered the newest-is-pending happy path).

Nothing else found: structural sweep clean (version consistency, all `.cs` balance, `node --check`,
ui-integrity), security sweep clean (TLS-bypass is Proxmox-only and config-gated, no secret logging,
SQL interpolation is table-names/constants only, all 116 endpoints auth-gated).

## v1.14.0 — Incident + change memory + the IApprovable design (NORTH_STAR Phase 10)

Phase 10 of the master roadmap — the final phase of the V1.x line. ANTHILL now connects failures
to recent changes and past fixes, and the unified approval abstraction that V2.1's actions build
on is designed, shipped, and test-reviewed. Incident tracking, timelines, and recommendations
only — nothing here can remediate anything.

### Incident + change memory
- **Auto-opened incidents**: the `incident-sweep` scheduler job turns the health system's
  `incident_candidate` events (3 consecutive failures) into incidents, deduped per subject —
  one open incident per failing thing, re-sweeps never duplicate. Manual opening via API/UI too.
- **Incident timeline**: reconstructs everything around an incident — `change_log` entries from
  the 24h lookback window before it opened are flagged **SUSPECT** ("what changed right before it
  broke"), plus correlated homelab events and per-target health results through resolution,
  chronologically ordered.
- **Similar incidents + fix memory**: deterministic scoring (token overlap + same-subject/kind
  bonuses) over past incidents; resolved matches carry their root cause verbatim as
  *"this fixed it last time"*. Resolving with a root cause writes an `incident_fix_recorded`
  event — the durable memory future incidents draw on.
- **Repeated-failure patterns**: a subject producing 3+ incidents in 14 days is pattern-flagged
  (`incident_pattern` event) and its new incidents open at error severity.
- **API**: `GET|POST /homelab/incidents`, `GET /homelab/incidents/{id}/timeline`,
  `GET /homelab/incidents/{id}/similar`, `POST /homelab/incidents/{id}/status`
  (open|investigating|resolved + root cause).
- **UI**: Incidents panel on the Homelab page — severity/status tables, a detail drawer with the
  suspect-flagged timeline and similar-incident fix suggestions, resolve-with-root-cause flow,
  and manual incident opening.

### IApprovable (designed before V2.1, per the roadmap)
- **`IApprovable`** interface + `ApprovableView` projection: ONE pending queue, ONE lifecycle
  (pending → approved → executed / rejected / superseded; execution never from pending), ONE
  dedupe rule (equal DedupeKeys can't both be pending; newer supersedes), per-kind renderers
  (`patch_diff` today; `action_proposal` V2.1; `network_preview` V2.4).
- **`GET /homelab/approvals/unified`**: today's patch approvals projected into the unified queue
  via a read adapter over `approval_requests` — no new table, no migration, existing decision
  endpoints untouched.
- **`ActionProposal` skeleton** (deliberately inert: no executor exists, nothing constructs one,
  risk defaults `high`): carries the Phase 12 blast-radius rubric inputs (dependency fan-out,
  criticality, backup coverage, exposure, rollback note, dry-run availability) so V2.1 implements
  against reviewed fields.
- **`docs/APPROVALS.md`**: the canonical design doc — lifecycle, dedupe, renderer table, and the
  five execution requirements V2.1 is bound to (separate approve/execute permissions, state
  re-checks, HOMELAB_STOP, audit events, forbidden-actions enforcement in the executor).

### Tests
- `IncidentMemoryTests`: per-subject dedupe across resolve cycles, idempotent candidate sweep,
  repeat-offender severity upgrade + pattern event, timeline suspect flagging + chronological
  order + subject correlation, similar-incident ranking with verbatim fix surfacing, resolve
  validation + fix-memory event, and the IApprovable design review (faithful patch projection,
  supersede-on-dedupe, inert fail-safe ActionProposal).

## v1.13.0 — Network + security awareness (NORTH_STAR Phase 9)

Phase 9 of the master roadmap: understand the network shape and the obvious risks. Awareness and
reporting only — no firewall/DNS/DHCP writes, and stronger: **zero network I/O**. Active scanning
does not exist in this phase; if it ever arrives it ships disabled-by-default behind the target
allowlist like every other prober.

- **`RiskAnalyzer`** — deterministic rules over inventory ANTHILL already knows, producing all nine
  NORTH_STAR findings: `risky_open_port` (legacy/cleartext ports; severity upgrades to error when
  internet-exposed), `unknown_device`, `ownerless_service`, `un_backed_up_host` (workloads with no
  backup-capable storage anywhere), `exposed_dashboard` (admin surfaces reachable from the
  internet), `duplicate_ip` (across hosts AND network devices), `missing_dns_name`,
  `service_without_health_check`, and `credential_never_verified`.
- **Stable-id reconciliation**: findings upsert by `risk:{kind}:{subject}`, so re-analysis never
  duplicates, **fixed problems auto-resolve**, and operator **acknowledgements survive re-runs**
  (and still auto-resolve when the underlying issue is actually fixed).
- **Network-device registry** (manual/import only): name/kind/MAC/IP/VLAN/known-flag/notes with
  first/last-seen stamps; unknown devices become findings; devices ride the inventory
  import/export bundle.
- **Scheduler**: `risk-analysis` job on the shared scheduler (`homelab_risk_interval_seconds`,
  default hourly) — repo-only work, safe at any cadence.
- **API**: `GET|POST|DELETE /homelab/devices`, `GET /homelab/risks`,
  `POST /homelab/risks/analyze` (run now), `POST /homelab/risks/{id}/ack`.
- **UI**: Network & Risk section on the Homelab page — device registration + table with unknown
  flagging, findings table with severity coloring/KPI counts, Analyze Now, and per-finding Ack.
- **Tests** (`RiskAwarenessTests`, socket-free by construction): every finding rule, exposure
  classification, duplicate-IP detection, watched-service suppression, un-backed-up-host
  resolve-on-fix, stable reconciliation with sticky acks, the scheduler adapter, and device
  import/export round-trip.

## v1.12.0.1 — Proxmox client: don't follow redirects (SSRF hardening)

Bug-finder/tester pass over the v1.12.0 Proxmox integration. The rest of it holds up well —
GET-only by construction, target-allowlist gate before every request, token pulled from the
credential store per call and never logged, defensive JSON parsing, `INSERT OR IGNORE` event dedup.
One defense-in-depth gap:

- **`ProxmoxApiClient` followed HTTP redirects** (`AllowAutoRedirect` left at the .NET default of
  `true` on both the verified and insecure handlers). The allowlist gate validates the configured
  host, but a `3xx` from a compromised or misconfigured node would bounce the authenticated GET to a
  `Location` that was never allowlist-checked — an SSRF hole straight through the integration's
  "safety by construction" premise. Both handlers now set `AllowAutoRedirect = false`; the PVE API
  never legitimately redirects, so a redirect surfaces as a clean non-success status instead of being
  chased off-allowlist. Added a wire-level regression test (mock 302 → `Location` to a dead off-host
  port; asserts the client fails clean with `HTTP 302` and never requests the redirect target).

## v1.12.0 — Proxmox read-only integration (NORTH_STAR Phase 8)

Phase 8 of the master roadmap: ANTHILL connects to Proxmox safely, in read-only mode. There is no
start/stop/reboot/migrate/delete/clone/resize/config-write path anywhere in this integration.

- **GET-only `ProxmoxApiClient`** — write operations are *structurally impossible*: the class has
  no POST/PUT/DELETE code at all. Proven twice in tests: the public type surface exposes only
  `Get*` methods, and a mock PVE server asserts every wire request is a GET. Allowlist check (D1)
  and credential lookup happen before any request; strict per-request timeout; TLS verification is
  config-controlled (`homelab_proxmox_insecure_tls` for self-signed homelab certs, default verify).
- **`ProxmoxInventoryProvider`** (riding the shared scheduler as `proxmox-sync`): syncs nodes (as
  `hypervisor` hosts tagged `proxmox` with status/CPU/RAM/uptime), QEMU VMs (vmid, status, vCPU,
  RAM, uptime), LXC containers, storage pools (with backup-capable flagging + used/total bytes),
  and failed Proxmox tasks — recorded as `proxmox_task_failed` events with stable UPID ids so
  re-syncs never duplicate (RecordEvent is now INSERT OR IGNORE). All upserts use stable ids —
  re-sync is idempotent.
- **`ProxmoxHealthProvider`**: GET /version reachability check for the health system.
- **Credentials**: the API token lives in the homelab credential store
  (`homelab_proxmox_credential_id`, default `proxmox-main`; save as `user@realm!tokenid=SECRET`),
  is fetched per sync with an audited use, flows only into the PVE Authorization header, and is
  proven absent from events, changes, inventory, statuses, and export bundles. A read-only
  PVEAuditor token is all it needs — matching the integration's own permissions.
- **Repository**: `UpsertVm/ListVms`, `UpsertContainer/ListContainers`,
  `UpsertStoragePool/ListStoragePools` fill the v1.9.0 `vm_inventory`, `container_inventory`, and
  `storage_inventory` tables for the first time.
- **API**: `GET /homelab/vms`, `GET /homelab/containers`, `GET /homelab/storage`,
  `GET /homelab/proxmox/status` (secret-free), `POST /homelab/proxmox/sync` (manage-gated run-now).
- **UI**: Virtualization section on the Homelab page — Proxmox status card with setup hints and
  Sync Now, VM/container tables with running-state coloring, storage pools with usage-percent
  coloring (green/amber/red at 75/90%).
- **Config**: `homelab_proxmox_enabled` (off), `homelab_proxmox_host`, `homelab_proxmox_port`,
  `homelab_proxmox_credential_id`, `homelab_proxmox_insecure_tls`,
  `homelab_proxmox_sync_interval_seconds` — all operator-editable and in the settings snapshot.
- **Tests** (`ProxmoxIntegrationTests`, mock PVE API on loopback): no-write type-surface + wire
  proofs, allowlist blocks with zero requests, missing-credential clean failure, full sync
  population, idempotent re-sync, HTTP-500 soft failure, hung-server timeout bound, credential
  redaction sweep, and health-provider healthy/failed paths.
- Deferred to V2.2 (backup intelligence): per-VM snapshot detail and deep backup inspection.

## v1.11.0.2 — Replace blocking native dialogs with an in-app modal

Fix: the console used native `window.confirm()`/`prompt()` for every destructive action (Stop the
Director, reject/apply patches, flush cache, reset settings, delete objectives/users, restart the
service, prune pheromones, etc.). Native dialogs **block the renderer's main thread** until
dismissed — which is what hung the Autonomy **Stop** button (the click froze the page until the
modal was cleared), and they also look out of place in the custom HUD and break automated testing.

- New promise-based `uiConfirm()` / `uiPrompt()` — themed, non-blocking, keyboard-navigable
  (Enter = confirm, Esc / backdrop = cancel), with an optional danger style for destructive actions.
- All 18 native `confirm()`/`prompt()` call sites migrated (handlers made `async` where needed).
  Behavior is unchanged (default is still cancel); only the blocking + styling changed.

## v1.11.0.1 — Auto-apply observability + auth-redirect hardening

Two fixes surfaced while live-verifying the autonomous auto-apply → git loop end-to-end on the LXC
(the loop itself works: a verified patch applied, committed to the standalone `<username>-anthill`
branch, synced origin/main into it, and pushed — never touching main).

- **Auto-apply git step is now logged.** Previously only the *failure* path emitted an event, so a
  successful commit/push was invisible in the Event Log — from the UI it looked like the loop applied
  and verified but never committed (it had). `AutoApplyRunner` now emits
  `autonomy_autoapply_committed` on success, naming the commit sha, branch, files, and push result
  (`pushed to <remote>/<branch>` / `push failed …` / `push disabled`), so the git step is visible and
  searchable.
- **UI reliably bounces to login on a 401.** `onUnauthorized` early-returned after the first 401, so
  if a session went invalid mid-flight (e.g. the server rotating its session secret during a
  redeploy) the console could stay stuck half-loaded behind failing background polls instead of
  redirecting. It now re-asserts the login screen on any 401 while the app shell is still visible,
  without re-running once already on login.

## v1.11.0 — Health checks + notifications (NORTH_STAR Phase 7)

Phase 7 of the master roadmap: ANTHILL can tell what is alive, degraded, or broken. Awareness and
reporting only — there is no auto-remediation anywhere in this subsystem.

- **`HealthCheckRunner`** (deterministic C#, never routed through the model router): ping, HTTP
  status (200s healthy / 4xx degraded / 5xx failed), TCP port, service-URL checks, plus disk and
  uptime placeholders that report `unknown` until agent support lands. Every check must pass the
  Homelab Target Allowlist (D1) **before any I/O**, runs under a strict per-check timeout
  (`homelab_health_timeout_ms`, per-schedule override) so a hung host can never hang the app, and
  persists a `HealthCheckResult` with latency + detail.
- **Failure alerting**: each failed check writes a `health_check_failed` event; 3 consecutive
  failures of one target promote it to a single **`incident_candidate`** event (fires once per
  streak) — groundwork for V1.14's incident memory.
- **`NotificationService`** (config-gated, OFF by default): Slack, Discord, and generic JSON
  webhooks; fires on health-check failures, incident candidates, and operator tests. Strict
  timeouts, soft failure, and every send attempt audited as a homelab event that never contains a
  webhook URL or any secret.
- **Scheduler wiring**: one `health-checks` job on the shared `HomelabScheduler`
  (`homelab_health_interval_seconds`, default 60s) — no per-subsystem timers. Mock providers now
  register only when their own gate is on; the scheduler starts whenever it has jobs.
- **Operator-managed schedules**: new `health_check_schedules` table with CRUD + ChangeRecords.
- **API**: `GET /homelab/health/summary` (latest-per-target rollup), `GET /homelab/health/results`,
  `GET|POST|DELETE /homelab/health/schedules`, `POST /homelab/health/run` (run everything now),
  `POST /homelab/notifications/test`. Reads = `read_homelab`, writes = `manage_homelab_integrations`.
- **UI**: Health panel on the Homelab page — add/run/delete checks, healthy/degraded/failed/unknown
  KPI line, last status/latency/detail per check, and a Test Notify button.
- **Config**: `homelab_health_interval_seconds`, `homelab_health_timeout_ms`,
  `homelab_notifications_enabled`, `homelab_slack_webhook`, `homelab_discord_webhook`,
  `homelab_generic_webhook` — all operator-editable, all conservative/off by default.
- **Tests** (`HealthAndNotificationTests`, all on loopback sockets — zero external network):
  host extraction, allowlist-blocks-before-I/O, HTTP 200/404/500 classification, TCP open/closed/
  malformed, hung-server timeout bound, placeholder kinds, incident-candidate streak, notifications
  disabled-by-default / delivery + URL-free audit / unreachable-webhook soft-fail, latest-per-target
  summary, and schedule CRUD persistence across reopen.

## v1.10.0 — Inventory + service registry with Homelab console page (NORTH_STAR Phase 6)

Phase 6 of the master roadmap: ANTHILL knows what exists. Manual/import-based only — no active
scanning. Plus two operator-facing fixes found in live testing.

### Inventory + service registry
- **Dependency mapping**: `dependencies` CRUD in `HomelabRepository` with ChangeRecords, answering
  "what runs where?" and "what depends on this?" (service→host `runs_on`, `needs`, `stores_on`).
- **Import/export**: `GET /homelab/export` / `POST /homelab/import` round-trip nodes + services +
  dependencies as one JSON bundle. Import is upsert-by-id, so re-importing an export is idempotent;
  invalid records are skipped; credentials and allowlist entries are never part of the bundle.
- **API completion** (per NORTH_STAR): `PUT /homelab/hosts/{id}`, `PUT /homelab/services/{id}`,
  `GET|POST|DELETE /homelab/dependencies`. Reads = `read_homelab`; writes = `manage_homelab_integrations`.
- **New console page: Homelab Inventory** (visible to admins and homelab operators; write forms
  admin-only): Subsystem Status, Hosts, Services, Open Ports (derived from services), Dependencies,
  Recent Changes panels, host/service/dependency registration forms, and JSON export/import buttons.
- Homelab gates (`homelab_enabled`, `homelab_scheduler_enabled`, `homelab_mock_providers_enabled`,
  `homelab_max_concurrent_checks`) are now operator-editable settings and appear in the settings
  snapshot — no more hand-editing config.json.

### Fixes
- **LXC deployments silently froze on old versions (the "header says v1.8.26" bug).** The
  `setup.sh` upgrade path ran `git pull --ff-only` on whatever branch the build checkout was on;
  since the auto-apply git integration (v1.8.26) that checkout can end up parked on the standalone
  `<username>-anthill` branch, so every upgrade re-run rebuilt stale code while releases moved on.
  The upgrade path now forces the build checkout to `origin/main`
  (`git fetch` + `git checkout -B main origin/main`), logs exactly which version+commit it is
  building, and after the service restarts it polls `/health` and **fails loudly on a
  built-vs-running version mismatch** — a stale deployment can never look healthy again. (The UI
  header renders the `/health` version since v1.9.1.1, so header == running binary, always.)
- **Patch Center "Apply" always returned 403.** The API capability gate `apply_patch` shipped as a
  static `false` and was never projected from `patch_application_enabled`, so `POST /apply/{id}`
  answered `permission_denied` even after the operator enabled patch application in Settings. The
  gate now follows the setting at boot and on live settings updates (`PatchApplyGateTests`), and the
  Patch Center error toast now surfaces the server's actual reason plus the fix
  ("enable Patch application in Settings") instead of a bare HTTP code.
- The `homelab_operator` role now renders correctly in the nav footer and sees the Homelab page.

### Tests
- `PatchApplyGateTests` (gate follows setting, homelab keys editable/snapshotted),
  `InventoryRegistryTests` (dependency CRUD + change records, export/import round-trip into an
  empty DB, idempotent re-import, invalid-record skipping, exports never contain credential or
  allowlist material).

## v1.9.1.1 — Fix: UI header/title version drift (hardcoded markup)

The console title, login logo, and nav header displayed a hardcoded version (`v1.8.29.1`) that had
silently drifted from the runtime version — release bumps only covered the four canonical markers
(runtime const, Directory.Build.props, README, CHANGELOG), not markup literals.

- The UI now fetches the version from the public `/health` endpoint at boot (`bootVersion()`) and
  renders it into the title, login logo, and nav header — `AnthillRuntime.Version` is the single
  source of truth; the markup carries no literal version anywhere.
- New regression guard (`UiIntegrity_NoHardcodedVersionInMarkup`): fails `dotnet test`/CI if any
  `>vX.Y.Z<` literal or versioned `<title>` ever reappears in `index.html`.

## v1.9.1 — Homelab scheduler + mock-provider harness (NORTH_STAR Phase 5)

Phase 5 of the master roadmap: one shared execution/testing pattern for every future homelab
provider. Still read-only, still zero real network calls, still disabled by default.

- **Five mock providers** (`FakeProxmoxProvider`, `FakeDnsProvider`, `FakeDhcpProvider`,
  `FakeFirewallProvider`, `FakeHealthProvider`) built on a shared `FakeHomelabProvider` base:
  deterministic item counts, simulated latency, scriptable failure injection, thread-safe
  secret-free `HomelabProviderStatus`, and an audit `provider_run` event per run.
- **Target-allowlist discipline baked into the base class**: a provider with a target host
  consults `IHomelabTargetGuard` before doing anything and fails cleanly when the host is not
  allowlisted — the exact D1 wiring real providers inherit.
- **Scheduler wiring**: the five mocks register as `HomelabScheduler` jobs at boot but only run
  when BOTH `homelab_scheduler_enabled` AND the new `homelab_mock_providers_enabled` gate are true
  (both default false). Jitter, per-failure exponential backoff, the global concurrency cap, and
  restart-surviving job state all exercised end-to-end.
- **API**: new `GET /homelab/providers` (secret-free statuses, `read_homelab`); `/homelab/summary`
  now includes the provider list.
- **Shared mock-provider test harness** (`MockProviderHarnessTests`): one `[MemberData]` fixture
  runs every provider through identical assertions — success/status consistency, failure streak +
  recovery, allowlist gating, disabled-provider behavior — plus scheduler proofs for the Phase 5
  validation list: run-all, backoff growth/reset, concurrency cap (no stampede), background
  start/stop, and job-state persistence. Real providers from v1.10+ join by adding a factory line.

## v1.9.0 — Homelab foundation (NORTH_STAR Phase 4)

Phase 4 of the master roadmap and the start of the V1.9.x homelab line: the read-only backend
foundation. Nothing in this release can control infrastructure — no Proxmox control, no firewall
changes, no SSH execution, no destructive actions. Everything ships disabled by default.

- **Models + persistence.** 16 homelab record types and 15 new SQLite tables (`homelab_nodes`,
  `network_devices`, `services`, `vm_inventory`, `container_inventory`, `storage_inventory`,
  `backup_inventory`, `health_checks`, `homelab_events`, `change_log`, `incidents`, `dependencies`,
  `risk_records`, `homelab_credentials`, `homelab_target_allowlist`) in the existing colony DB via
  the new `HomelabRepository` (idempotent schema init; every inventory write logs a `ChangeRecord`).
- **Interfaces** for all future integrations: `IInventoryProvider`, `IHealthCheckProvider`,
  `IHomelabEventSink`, `IHomelabRepository`, `IIntegrationStatusProvider`, `IHomelabTargetGuard`,
  `ICredentialProvider`.
- **Homelab Target Allowlist (D1).** `HomelabTargetGuard`: deterministic providers may only reach
  operator-allowlisted targets (exact hostname / exact IP / IPv4 CIDR, no DNS resolution). Fully
  isolated from the general SSRF guard — `UrlSafety` still blocks private/loopback for LLM-directed
  tools, proven by tests in both directions.
- **Credential store (D2).** `HomelabCredentialStore` on the existing `FieldCipher`: secrets are
  write-only via the API, statuses expose only configured/last_verified, and every secret use
  writes an audit `homelab_events` row.
- **Homelab permission tier (D3).** New permissions `read_homelab`, `manage_homelab_integrations`,
  `approve_homelab_actions`, `execute_homelab_actions` (the two action gates ship capability-OFF
  until V2.1) and a new `homelab_operator` role: view + approve, never manage/execute/admin.
- **Scheduler skeleton (D4).** `HomelabScheduler`: jittered intervals (no check stampede),
  exponential backoff on consecutive failures, global concurrency cap, last-run/last-result
  persisted (survives restart). Disabled by default; registers no jobs in v1.9.0.
- **Read-only homelab ants** (visible-only, never executable, never patch-capable): InventoryAnt,
  NetworkScoutAnt, HealthAnt, ProxmoxAnt, StorageAnt, BackupAnt, SecurityScoutAnt,
  ChangeArchivistAnt.
- **API** (permission-scoped, secrets never returned): `GET /homelab/summary`, `GET|POST
  /homelab/hosts`, `GET|POST /homelab/services`, `GET /homelab/events`, `GET /homelab/changes`,
  `GET|POST|DELETE /homelab/allowlist`, `GET|POST|DELETE /homelab/credentials`.
- **Config**: `homelab_enabled`, `homelab_scheduler_enabled`, `homelab_max_concurrent_checks`
  (all off/conservative by default) + config.example.json documentation.
- **Docs**: new `docs/HOMELAB.md` (canonical homelab design doc, D10) with phase status at top;
  reserved backend folders carry phase-pointer READMEs.
- **Tests**: new `tests/Anthill.Tests.Homelab` project — migration idempotence (fresh/existing/
  re-run + coexistence with colony memory), allowlist matching + SSRF isolation, credential
  save/use/verify/remove with audit and redaction, scheduler run/backoff/persistence, ant-registry
  shape, and the D3 permission matrix.

## v1.8.29.1 — Auto-apply: coder add-vs-modify, default paths, and LXC provisioning

Makes the autonomous auto-apply → git loop work end-to-end on a fresh LXC install, removing the
manual steps and the last blockers hit during live testing.

- **Coder add-vs-modify** (`Ants.cs` + `Tools.cs`): the loop stalled whenever the coder proposed
  `change_type: add` for a file that already exists (a common LLM slip) — `ApplyPatchTool`
  hard-refused, so the patch never applied. The coder prompt now chooses `add`/`modify` by whether
  the target already exists, and an `add` to an existing path is applied as a backed-up full-file
  overwrite (`add_overwrite`) instead of failing. Fully reversible: the pre-apply backup, verify +
  rollback, and standalone-branch-never-main gate all still apply.
- **Default auto-apply paths** (`AnthillRuntime.cs` + UI): enabling auto-apply with an empty path
  allowlist was a silent no-op (empty allowlist = nothing eligible). Turning it on now seeds a
  starter allowlist of `docs/**` and `src/**`, persisted to config so it shows up pre-filled in
  Settings → Security and can be edited or removed like any operator entry. Never overrides paths
  the operator already set; never seeded while auto-apply is off. The UI also pre-fills the box the
  moment the toggle is switched on.
- **LXC provisioning** (`deploy/lxc/setup.sh` + service template): setup.sh now provisions the
  agent workspace as a git checkout under `.anthill/workspace` (already writable via the unit's
  `ReadWritePaths=.anthill`), sets the service user's git identity + `safe.directory`, checks out
  the standalone `<username>-anthill` branch on re-runs where a username is configured, and creates
  a private `.ssh` deploy-key slot (700; the key is provided by the operator and referenced by path,
  never generated or stored). Idempotent, so it doubles as the upgrade path. End users no longer do
  any of this by hand.

## v1.8.29 — Fresh-install training + pheromone bootstrap missions (NORTH_STAR Phase 3)

Phase 3 of the master roadmap: give fresh installs a repeatable, read-only way to learn the repo,
roles, workflow, UI, memory system, and V2 roadmap before doing real patch missions. Docs only —
no runtime behavior change.

- New **`docs/TRAINING_MISSIONS.md`** — a nine-mission training pack (Repo Orientation, Ant Role
  Training, Build/Test Workflow, UI Structure, Memory + Pheromone System, Patch Proposal
  Discipline, Failure Drill, V2 Homelab Roadmap, Daily Memory Compression) with copy-paste goal
  text for each.
- Every goal embeds the exact `MissionConstraints` phrases (`read-only`, `do not modify files`,
  `one-shot`) so the v1.8.16 constraint enforcement strips coder patch tasks at planning time —
  training can never produce patch proposals.
- Operator instructions: run order, Preview Plan verification, memory/pheromone checks afterward,
  and when to re-run the pack (fresh install, major version jump, after Clear Missions).
- Documents the recurring **memory-compression pattern**: mission 9 doubles as a daily/periodic
  compression template, runnable manually or as a low-priority recurring objective.

## v1.8.28 — Validation / regression harness hardening (NORTH_STAR Phase 2)

Phase 2 of the master roadmap: lock in regression protection for every bug class that has already
shipped once, before homelab complexity lands. Validation/CI/test changes only — no product
behavior change.

- **Centralized validation commands**: new `scripts/validate.sh` and `scripts/validate.ps1` run the
  full required validation set (restore → Release build → Release test, `--full`/`-Full` adds
  self-contained publish + `--selftest`, plus `node --check` on the embedded UI JS when node is
  available). CI runs the same steps.
- **New `RegressionGuardTests`** (run in plain `dotnet test`, so local work and CI gate identically):
  - *Version-marker consistency*: `AnthillRuntime.Version` must match `Directory.Build.props`
    `<AnthillVersion>`, the README "Current version" line, and a matching `## vX.Y.Z` CHANGELOG
    entry. (Directory.Build.props had silently drifted to 1.8.15.6 since v1.8.15.6 — fixed.)
  - *Migration idempotence*: fresh DB, reopen of an existing DB, and repeated re-runs of schema
    init all pass with an identical table set.
  - *UI glyph/encoding integrity*: the CI-only corruption checks (U+FFFD, flattened `?` icons,
    `'?':'?'` caret ternaries) now also run as unit tests.
  - *No-Python guard*: no `.py` file may exist outside archived `py.old/`.
- **CI hardening**: `Docs + version consistency` step extended to cover Directory.Build.props and
  the CHANGELOG entry; new `repo-guards` job fails any PR that touches `py.old/` and any commit
  that adds Python outside it.
- Assembly/package version now correctly stamps as the real release version (was 1.8.15.6).

## v1.8.27 — Roadmap / documentation consolidation (NORTH_STAR)

Phase 1 of the master roadmap: stop roadmap drift by making one canonical direction document.

- New **`docs/NORTH_STAR.md`** — the single, ordered build order from the current baseline (v1.8.26)
  through the V2 Homelab Command Center and V3 bounded autonomous operator, plus the non-negotiable
  safety/architecture rules, the global bug-prevention gates, and the version-completion template.
- `docs/ROADMAP.md`, `docs/UI_ROADMAP.md`, and `docs/AUTONOMY.md` now carry a status block marking them
  as retained subsystem history and pointing to `NORTH_STAR.md`.
- README links `NORTH_STAR.md` from the version notes and adds a v1.8.27 changelog row.
- Docs only; no runtime behavior change.

## v1.8.26.1 — Harden auto-apply git for the systemd sandbox

Two fixes found while bringing the v1.8.26 loop up on a hardened LXC (`ProtectSystem=strict`):

- **Commit identity inline.** The service user (`anthill`) has no global git identity, so `git commit`
  failed with "Please tell me who you are." The commit now sets it inline —
  `git -c user.name="ANTHILL Auto-Apply" -c user.email="anthill@localhost" commit` — so it never
  depends on host git config.
- **Writable `known_hosts`.** `ssh` records the remote host key on first connect, but the service
  user's `~/.ssh` is read-only under `ProtectSystem=strict`. `GIT_SSH_COMMAND` now points
  `UserKnownHostsFile` at `/tmp/anthill_known_hosts` (writable via `PrivateTmp`, per-service), so the
  push succeeds without adding `.ssh` to `ReadWritePaths`.

Note: a non-`.anthill` auto-apply workspace still needs a systemd drop-in adding it to
`ReadWritePaths` (the sandbox mounts everything else read-only), and the workspace must be a clone
owned by the service user, checked out on the `<username>-anthill` branch.

## v1.8.26 — Auto-apply git integration (standalone branch, never main)

Expands the "Git-commit verified changes" toggle into a real, safety-gated git workflow for the
Director's auto-apply. After a green verify, ANTHILL commits the applied files to a standalone branch
and can push it for review — **without ever touching main**.

- New config: `autonomy_autoapply_git_username` (→ branch `<username>-anthill`),
  `autonomy_autoapply_git_remote` (default `origin`), `autonomy_autoapply_git_ssh_key_path`,
  `autonomy_autoapply_git_push`. Surfaced in **Security → Autonomous Auto-Apply** (username field shows
  the resulting branch; remote; SSH key path; "Push branch to origin" toggle).
- **SSH deploy key by reference:** the key is used via `GIT_SSH_COMMAND="ssh -i <path> …"`. Only the
  *path* is stored/shown; no key material is ever read into config, DB, UI, logs, or events.
- **Flow (per kept auto-apply):** verify the workspace is on `<username>-anthill` (create/checkout is
  a one-time operator step) → `git add`/`commit` the applied files → if push is on, `git fetch` +
  merge `origin/main` **into** the branch (one-way sync) → `git push <remote> <branch>` via the key.
- **Hard main-safety:** refuses to commit if the workspace is on `main`/`master`; only ever commits
  and pushes the standalone branch; never merges the branch into main; never force-pushes;
  fail-closed (a git error keeps the change on disk and logs `autonomy_autoapply_git_failed`).
- Open PRs from the pushed branch on GitHub; filing PRs/issues from ANTHILL needs the GitHub API
  (a token) and is a separate follow-up, out of scope for an SSH deploy key.

**Operator setup (one-time, on the host clone):** create the deploy key, add its public half to the
repo (Settings → Deploy keys, allow write), then `cd <workspace> && git checkout -b <username>-anthill
origin/main`. Point the SSH key path setting at the private key.

## v1.8.25.4 — Auto-Apply Security toggles never saved

The two Autonomous Auto-Apply toggles — "Enable auto-apply" (`autonomy_autoapply_enabled`) and
"Git-commit verified changes" (`autonomy_autoapply_git_commit`) — render in their own containers
(`#sec-autoapply-toggle`, `#sec-autoapply-git`), but `saveSecurity()` only harvested toggle state
from `#sec-toggles` and `#sec-shell-toggle`. So both toggles flipped visually and then silently
dropped out of the save payload. Added their containers to the collector; both persist now.

## v1.8.25.3 — Approved patches were un-appliable

Found during live V&V of the Patch Center. Approving a patch flipped only the *approval record* to
`approved` — nothing ever set the patch itself to `PatchStatus.Approved`. The Patch Center gates its
Apply action on the patch status being `approved`, so **Apply never appeared after approval** and
approved patches could not be applied through the UI (true for both the normal flow and the operator
approve-by-patch-id path).

- `ApproveRequest` now flips the patch to `approved` for a `patch_proposal` approval, mirroring the
  reject path (which already set the patch to `rejected`).
- The Patch Center's `canApply` also honors `approval_status === 'approved'`, so patches approved
  before this fix (approval approved but patch still `proposed`) are appliable too.

Apply still respects the write gates (`patch_application_enabled` / `file_writing_enabled`).

## v1.8.25.2 — CI guard against UI glyph corruption

The console UI has been re-saved as non-UTF-8 several times, flattening icon glyphs to `?` and other
glyphs to the U+FFFD replacement char (`�`). Adds a **`ui-integrity` CI job** that fails the build on
any `�`, bare `>?<` icon, `>? Label` button, or `'?':'?'` caret in `index.html` (the legitimate
`<kbd>?</kbd>` help key is allowlisted), plus a `node --check` of the embedded JavaScript — so this
recurring corruption can never merge again. CI-only; no runtime change.

## v1.8.25.1 — Console glyph-corruption repair

A follow-on to the v1.8.23.1 encoding repair, which only caught labeled buttons (`>? Label`) and
U+FFFD (`�`) characters. This pass fixes the icon-only glyphs that were also flattened to `?` and had
survived into the mainline:

- 19 `>?<` markup icons restored from the last clean revision: collapse buttons and expand carets
  (`▾`), mission-dispatch buttons (`▶`), the results-close button (`✕`), the full-event-log button
  (`⛶`), and the pheromone-table success/failure headers (`✓` / `✕`).
- 4 JS-literal expand/collapse carets (`det.open?'?':'?'` / `hidden?'?':'?'`) → `▾` / `▸`.
- The apply-warning prefix (`⚠`) and the nav "autonomy running" badge (`●`).
- The legitimate `?` help-shortcut key (`<kbd>?</kbd>`, from the v1.8.25 Command Center) is preserved.

No behavior change; embedded UI JavaScript still parses cleanly.

## v1.8.25 — UI Phase 10: Full Command Center Polish

Finishes the UI roadmap (all 10 phases now shipped). Everything is additive vanilla JS/CSS inside
the embedded console; no backend changes.

- **Command palette (Ctrl+K):** fuzzy-matched pages and actions (new mission, toggle nav, pending
  approvals, shortcuts, tour), recents boosted, arrow-key navigation. Ctrl+K previously jumped to
  the mission input — "New mission" is now the top palette action, one Enter away.
- **Global search:** typing 2+ characters in the palette also searches mission memory
  (`/memory/explorer`) — missions, tasks, patches, and sources deep-link to Results / Patch Center.
- **Notification center:** a header bell collects notable colony activity (mission complete/failed,
  patch applied/verified/failed, approvals, auto-apply outcomes) from the existing event feed, with
  an unread badge and per-item deep links. No new polling.
- **Keyboard shortcuts:** `g` then a letter jumps between pages (g o / g c / g m / g r / g e, plus
  admin g p / g b / g s / g u / g a), `?` opens a shortcuts reference, Esc closes overlays.
- **Saved layouts:** the console reopens on the page you left, alongside the existing persisted nav
  collapse, card collapse, and Patch Center grouping state.
- **Onboarding tour:** a five-step first-login walkthrough (dispatch → patch review → memory →
  shortcuts); skippable, never auto-shows again, restartable from the palette.
- Reduced-motion aware; role-gated (coordinators see no admin pages in palette, search, or g-nav).

## v1.8.24 — UI Phase 7: Visual Patch Center 2.0

Finishes UI Roadmap Phase 7 — grouping — and closes the operator gaps around pending patches.

**Grouping**
- New "Group by" control (status / risk / file / mission / objective); choice persists.
- Collapsible group sections with patch counts and per-status mini-chips; status/risk groups sort
  logically, the rest by size. Pure client-side re-render — filters, diffs, and actions unchanged.

**Operator approval for orphaned pending patches**
- Some pending patches had no approval record (deduped duplicates, pre-v1.8.16 history), so they
  were visible but impossible to act on. New `POST /patches/{id}/approve` and
  `POST /patches/{id}/reject` create the missing approval record first, then run the exact same
  Queen approve/reject transition — never a direct status write. Approve/Reject buttons now appear
  for these patches in the Patch Center.

**Operator-edited alternative patches**
- "✎ Edit as alternative" opens the proposal's content in an editor; submitting creates a NEW
  proposal (same file, same base content) behind the standard approval gate via
  `POST /patches/{id}/alternative`. Nothing is written to disk by editing. The original is marked
  superseded (optional) and its pending approval resolved.

**Unbiased verification with auto-approve**
- "⚖ Verify & Auto-approve" (`POST /patches/{id}/verify`): the patch is applied with a backup, the
  verify command runs (`autonomy_autoapply_verify_cmd` or built-in `dotnet build && dotnet test`),
  and the workspace is ALWAYS restored — green or red. The toolchain judges the change, not the
  ant that proposed it. Green ⇒ the patch is auto-APPROVED through the normal Queen/approval path;
  applying to disk still requires the operator. Red ⇒ stays pending with the failure tail recorded.
  Requires the same write gates as Apply (the temporary staging honors them).
- Tests: `PatchOperatorActionTests` covers orphan approve/reject, alternative creation/supersede,
  and edge cases against a real SQLite database.

## v1.8.23.3 — CI linux-x64 artifact packaging

Roadmap item "CI release packaging foundation": every successful CI run now produces a
release-ready, downloadable package — not just tagged releases.

- `publish-and-selftest` job now packages `./publish/linux-x64` (binary + `config.example.json`,
  README, CHANGELOG) as `anthill-linux-x64-v<version>.tar.gz` and uploads it as a CI artifact.
- Artifact name is read from `AnthillRuntime.Version` at build time, so it always matches the code.
- Packaging steps run strictly after publish + `--selftest` succeed; a broken build can never
  produce a downloadable package (`if-no-files-found: error` guards the upload).
- No runtime behavior changes; existing build/test/selftest/Docker/shellcheck jobs untouched.
- Documented where CI artifacts appear in `docs/DEPLOYMENT.md` §4.

## v1.8.23 - Phase 9: Memory + Pheromone Explorer

- Adds a Memory + Pheromone Explorer on the existing Pheromones page.
- Visualizes success/failure/loop-pattern signals from mission history and pheromone trails.
- Adds mission memory search across missions, tasks, patches, and source summaries using existing read endpoints.
- Keeps prune controls on the same surface so weak/failure-dominant trails can be cleaned up without leaving the explorer.
- Delivered through issue #22, branch `feat/22-memory-pheromone-explorer`, and pull request workflow.

> Versioning convention: each autonomy phase or notable feature ships as a patch bump.
> Phase 1 = **v1.8.1**, live console + operator accounts = **v1.8.2**, enterprise shell UI = **v1.8.3**,
> model provider connections = **v1.8.4**, Phase 2 autonomy (Strategist) = **v1.8.5**, container-style
> deployment (Docker) = **v1.8.6**, LXC deployment = **v1.8.7**, provider base-URL fix = **v1.8.8**,
> LXC upgrade-in-place fix (ETXTBSY) = **v1.8.9**, LXC upgrade-in-place fix (stale native asset
> cache) = **v1.8.10**, Autonomy page recursion fix = **v1.8.11**, Phase 3 autonomy
> (concurrency + ResourceGovernor) = **v1.8.12**, coder Python-bias fix = **v1.8.13**, Phase 4
> autonomy (learning loop) = **v1.8.14**, mission reports (readable observability) = **v1.8.14.1**,
> UI cache + approval dedupe fixes = **v1.8.14.2**, Security + Shell config tabs = **v1.8.14.3**,
> header status + update check = **v1.8.14.4**, auto-publish releases + hardening = **v1.8.14.5**,
> Phase 5 autonomy (gated auto-apply) = **v1.8.15**, live-test fixes = **v1.8.15.1**, Strategist
> intent + shell service control = **v1.8.15.2**, native polkit install = **v1.8.15.3**, disk
> hygiene + maintenance controls = **v1.8.15.4**, completed-objectives box = **v1.8.15.5**, coder
> JSON parse hardening = **v1.8.15.6**, Overview System Health panel = **v1.8.15.7**, objective
lifecycle hardening + visual Patch Center = **v1.8.16**, Patch Center robustness = **v1.8.16.1**,
Colony Command Center HUD (design system + Overview dashboard) = **v1.8.17**, Mission Composer +
plan preview = **v1.8.18**, Patch Center invalid-UTF-16 500 fix = **v1.8.18.1**, Colony Live Canvas 2.0 = **v1.8.19**, Objective Command Board +
Mission Timeline/DAG = **v1.8.20**, autonomous auto-apply persistence fix = **v1.8.21**, Phase 8
Ant Inspector/Performance Observatory + Ant Capability Profiles & Worker Runtime = **v1.8.22**,
ASCII banner tweak = **v1.8.22.1**, Memory + Pheromone Explorer = **v1.8.23**, console UTF-8 repair
+ API serialization hardening = **v1.8.23.1**, Patch Center duplicate-route fix = **v1.8.23.2**,
CI linux-x64 artifact packaging = **v1.8.23.3**, Visual Patch Center 2.0 grouping (UI Phase 7)
= **v1.8.24**, Full Command Center Polish (UI Phase 10) = **v1.8.25**, console glyph-corruption
repair = **v1.8.25.1**, and so on.

## v1.8.23.2 — Patch Center duplicate-route fix

**Root cause of the recurring Patch Center empty HTTP 500.** `GET /patches` was registered twice: a
legacy `ProtectedText("/patches")` (the old `Queen.FormatPatchList()` text list) collided with the
structured `app.MapGet("/patches")` that the Patch Center UI uses. Two endpoints with an identical
method+template make ASP.NET throw `AmbiguousMatchException` during routing — *before* any handler or
middleware runs — so it surfaced as an uncatchable empty-body 500 that neither the v1.8.18.1 UTF-16
sanitizer nor the v1.8.23.1 serialization guard could touch (they run after routing).

- Removed the duplicate legacy `ProtectedText("/patches")` registration; the structured list remains.
- Added `AssertNoDuplicateRoutes()` at startup: enumerates every registered endpoint and throws a
  clear error at boot if any method+template is registered more than once, so this class of bug fails
  loudly at startup instead of silently 500ing at request time.

## v1.8.23.1 — Console UTF-8 repair + API serialization hardening

Two fixes bundled on top of Phase 9.

**Console encoding repair.** The v1.8.23 save round-tripped `index.html` through a non-UTF-8 encoding,
flattening 28 button icon glyphs (`↺`, `✂`, `▶`, `⌕`, `✓`, `✕`, `◈`) to `?` and leaving 354 U+FFFD
replacement characters (`�`) where em-dashes, ellipses, middot separators, and password-field bullets
used to be. All glyphs are restored; the file is clean UTF-8 again and the embedded JS still parses.

**Permanent Patch Center fix (empty HTTP 500).** `ApiJson.Ok`/`Error` previously handed the object
graph to `Results.Json`, which serializes during result execution — *after* the endpoint's own
try/catch has returned — so any serialization failure surfaced as an uncatchable empty-body 500 (the
`/patches` list was failing this way again). Responses are now serialized up front inside a guarded
`Envelope` helper (returning `Results.Content`), non-finite numbers are neutralized in the sanitizer,
and an outermost middleware converts any remaining unhandled exception into a valid JSON 500. No
endpoint can emit a silent empty 500 anymore — a failure now returns the real error message.

## v1.8.22.1 — ASCII banner tweak

Trim the boot/shell ANTHILL banner to the single large ant: removed the row of small ant figures
and the empty gap beneath the art so the banner butts directly against the following output line.

## v1.8.22 — Phase 8 + Ant Capability Profiles & Worker Runtime

Phase 8 UI (Ant Inspector + Performance Observatory) and the ASCII banner ship alongside the
capability layer incorporated from the codex branch:

- **Ant Capability Profiles** (`Agents/AntRegistry.cs`): 17 role definitions (6 executable —
  researcher, web, file, coder, builder, verifier) each with an `AntPermissionContract`
  (read/write workspace, read/write memory, web, shell, allowlisted checks, propose/apply patches)
  and named sub-workers. Forbidden paths (`py.old/`, `.git/`, `data/`, `.venv/`) and no-apply task
  types are enforced. `ValidateTask` gates each task against the mission constraints.
- **Worker Runtime** (`Agents/AntRuntime.cs`): resolves the role+worker for a task, injects worker
  context into the task snapshot, and emits audit warnings + metadata.
- Planner assigns a default worker per task and drops capability-rejected tasks; the Queen validates
  and resolves each task at run time (permission-denied tasks fail with a clear reason).
- Persistence: `tasks.assigned_worker` column (+ schema auto-migration), worker carried through
  task DeepCopy, graph nodes, and scheduler views; `SummarizeWorkerTelemetry()` aggregates worker
  performance.
- API: `GET /colony/registry` (roles + validation + telemetry) and `GET /colony/workers/telemetry`;
  `/missions/plan` now returns each step's `worker`/`display`, a `selected_path`, and
  `constraint_warnings`.
- UI: caste inspector shows a worker sub-caste breakdown, the DAG task drawer shows the resolved
  worker, and the plan preview shows the worker per step plus capability notes.

## v1.8.21 — Fix: autonomous auto-apply changes not persisting

Auto-apply is *apply → verify → keep-or-rollback*: a patch is kept only if verify exits 0, else every
applied patch is reverted. On a deployment with no build toolchain (a published-binary LXC, no dotnet
SDK, or `agent_workspace_dir` that isn't a buildable checkout), the built-in
`dotnet build && dotnet test` verify always failed — so auto-applied changes were silently rolled
back and never persisted ("not saving").

- **New opt-in gate `autonomy_autoapply_keep_without_verify`** (default false = keep verifying, safe).
  When true **and** no `autonomy_autoapply_verify_cmd` is configured, auto-apply keeps the applied
  patches instead of running (and failing) the built-in verify. If a verify command *is* set, it
  always runs and gates keep/rollback as before.
- **Clearer outcome logging.** The `autonomy_autoapply_started` / `_reverted` events now record the
  workspace path and the verify command; the reverted event's message spells out the fix options.
  A new `autonomy_autoapply_kept_unverified` event marks the keep-without-verify path, and
  `autonomy_autoapply_git_failed` surfaces a failed local git commit (kept on disk regardless).
- **Mission report surfacing.** `/missions/{id}/report` now includes an `auto_apply` outcome
  (kept / kept-unverified / reverted / apply-failed / git-failed / skipped) and the console shows an
  "Autonomous auto-apply" section — so "did the change actually stick?" is answerable at a glance.
  Auto-apply failures are also added to the report's Problems list.

Config default stays fail-safe: auto-apply is still OFF unless enabled with a path allowlist, and it
still verifies unless the operator explicitly opts out.



## v1.8.20 — Objective Command Board + Mission Timeline & Task DAG (UI Phases 5–6)

Two additive UI views over existing endpoints — no backend/API changes.

**Phase 5 — Objective Command Board** (new admin **Objectives** page). Every autonomous objective
laid out in seven lifecycle lanes — Backlog, Active, Paused, Completed, Stopped, Looping, Failed —
derived from `/objectives` status + `end_reason`/`retired_code`. Each card shows title, runs, success
EMA, priority, and end reason; expanding a card loads `/objectives/{id}/detail` (runs, missions,
tasks, patch rollup) with deep links to Results and the Patch Center. Admin-gated.

**Phase 6 — Mission Timeline + Task DAG viewer** (in the mission report / Results). A lazy-loaded
"Task Flow" section renders the mission's task graph two ways from `/missions/{id}/graph`:

- **DAG** — layered by dependency depth, nodes colored by status and ant, dependency edges drawn with
  **failure paths highlighted in red**.
- **Timeline** — tasks ordered by start time with duration bars.

Clicking a node/row opens a task detail drawer (ant, type, status, elapsed, attempts, failure
reason). Final output stays separated in its own report section as before. Rendered on demand so the
report stays light.


## v1.8.19 — Colony Live Canvas 2.0 (UI Phase 4)

Additive upgrade to the existing Colony canvas — the working node graph, task-dependency edges
(`dataFlowEdges`), handoff animation, pan/zoom, and node inspector are all preserved. New:

- **Caste legend + pheromone HUD overlay** on the canvas: the six ant castes with live-activity dots,
  and a "Colony Learning · Pheromones" panel showing the top real pheromone trails (`/pheromones/json`)
  with strength bars. Polls only while the Colony page is visible; glass overlay, `pointer-events:none`.
- **Real pheromone drift** on the canvas: motes drift from the castes toward the Queen with density and
  opacity scaled by actual colony trail strength (a global `pheromoneIntensity`). One additive CSS-cheap
  draw pass, guarded so it's invisible until the colony has learned something; reduced-motion aware.
- **Corrected node inspector**: the previously mislabeled "Pheromone Trail" bar (which just showed
  activity %) is now a **Live Task Load** breakdown — running / completed / failed tasks for that caste
  from the current mission graph. Real data.

No backend or API changes; reuses `/pheromones/json` and the existing `/graph` feed. The render loop
gains a single guarded pass; existing colony interaction and behavior are unchanged.



## v1.8.18.1 — Fix: Patch Center empty HTTP 500 (invalid UTF-16 in JSON)

Live testing surfaced `GET /patches` returning an empty HTTP 500 ("Error loading patches: Empty
response (HTTP 500)"). Root cause: `ApiJson.Ok` returns `Results.Json`, which serializes the payload
during response execution — **after** the endpoint's own try/catch has returned — so the failure was
uncatchable and produced an empty 500. `System.Text.Json` throws *"Cannot transcode invalid UTF-16"*
on a string containing a lone/unpaired surrogate, which LLM-generated patch `reason` / `summary` /
`mission_goal` text occasionally contains (clean test data never did).

Fix — scrub invalid UTF-16 at the JSON boundary so no endpoint can 500 on it:

- `TextUtil.SanitizeUtf16` replaces lone surrogates with U+FFFD (fast path: strings without
  surrogates are returned unchanged, no allocation).
- `ApiJson.Ok` / `Error` now recursively sanitize every string reachable from the payload
  (`ApiJson.SanitizeJson` walks dictionaries and lists; `byte[]` and scalars pass through so base64 /
  number serialization is preserved). This makes **all** JSON endpoints fail-safe against bad
  Unicode, not just the Patch Center.
- Tests (`JsonSafetyTests`) cover lone high/low surrogates, valid emoji preservation, deep nested
  scrubbing that then serializes cleanly, and `byte[]`/scalar passthrough.



## v1.8.18 — Mission Composer + Plan Preview (UI Phase 3)

Lets an operator review the generated task plan — and see how a mode/constraint reshapes it — before
a mission runs. Additive; existing one-shot dispatch is unchanged.

**Backend — dry-run planner.** New `POST /missions/plan` (permission `run_mission`, rate-limited)
runs the real planner + v1.8.16 constraint enforcement for a goal and returns the task list
**without creating, persisting, executing, or logging a mission** (`Queen.PlanPreview`). The response
includes each step's title, assigned ant, task type, and dependency edges (as human step numbers),
plus the parsed constraint flags (`verification_only` / `read_only` / `no_patches` / `one_shot` /
`blocks_patches`) and whether the plan contains a coder patch step. No fake capability — the preview
is exactly what a dispatch would plan.

**UI — Mission Composer.** The Overview mission node gains a **Preview Plan** action. It composes the
goal (raw directive + any selected mode's safe wording), calls `/missions/plan`, and renders the plan:
a constraint banner (e.g. "verification-only — no file changes"), the ordered steps with per-ant
badges, task types, and "after step N" dependencies, then **Approve & Dispatch** / **Edit** / **Reject**.
Approve submits the exact previewed goal via the existing `/missions` path; the raw ▶ / Enter dispatch
still works unchanged for one-shot use. Direct dispatch and Approve share one `submitMissionGoal()`
so the goal string is identical either way.

**Tests.** `PlanPreviewTests` (Ollama forced off → deterministic fallback planner) assert the preview
drops coder steps for verification-only goals, keeps them for code goals, always ends with a verifier,
and creates no mission row.



## v1.8.17 — Colony Command Center HUD Upgrade (Phases 1–2)

Turns the Overview into a live swarm command-center HUD, built additively on the existing
single-file console — no new dependencies, all animations CSS-only and reduced-motion aware.

**Phase 1 — HUD design system.** Reusable vanilla primitives + CSS: a canonical `hud-badge`
(running/idle/active/paused/completed/stopped/looping/failed/pending/approved/applied/rejected/
warning/unknown), `hud-risk` (low/medium/high/unknown), glass `hud-panel` with corner brackets and
optional active/warn/alert glow, `hud-metric` cards, `hud-telem` lines, loading/empty/error state
blocks, and a `hud-act` action-button group. JS helpers `hudBadge()`, `hudRisk()`, `hudStatusClass()`.

**Phase 2 — Overview command dashboard.** All panels use real API data with graceful `—`/empty/error
fallbacks (no fabricated values):

- **Colony status strip** — API link, provider/model, autonomy state, active missions, active
  objectives, pending approvals, warnings, and governor resource pressure; each deep-links to the
  relevant page and highlights warn/alert states.
- **Central system core** — a J.A.R.V.I.S.-style orb whose state (IDLE / MISSION ACTIVE / AUTONOMY
  ONLINE / OPERATOR ACTION / ALERT) is derived from real counts (active jobs, autonomy, pending/high-
  risk patches, failed missions, retired objectives, provider health). CSS rings/pulse only.
- **Operator attention** — real action items (pending/high-risk patches, failed missions, retired/
  failed objectives, backend-unreachable) with severity, reason, and deep link; "No operator action
  required" when clear.
- **Hardware/environment cards** — CPU load/core, memory-available %, backend latency, and effective/
  configured concurrency from the ResourceGovernor signals (`/autonomy/status`); shown as `—` with a
  "runs during autonomy" note when the governor hasn't sampled yet. Percentages clamped 0–100.
- **Mission command node** — terminal-style `ANTHILL_CORE >` input (existing submission preserved)
  with Inspect / Verify / Patch Proposal / Full Build-Test mode buttons that prepend visible, safe
  wording read by the v1.8.16 planner constraints (verification-only / no-patch). Selected mode is
  shown as a badge; nothing is changed silently.
- **Summaries** — recent missions, patch-status rollup (+high-risk), and objective-status rollup,
  each linking to Results / Patch Center / Autonomy. Live telemetry + recent jobs reuse the existing
  event/job feeds.

Data polling reuses the existing gated cadence (only fetches while Overview is visible); no new
uncleaned timers. Existing pages, navigation, mission submission, autonomy, and approval/apply
behavior are unchanged. UI-only — no API or backend changes.



## v1.8.16.1 — Patch Center robustness + validation

Stabilization pass on the v1.8.16 Patch Center after live testing surfaced an opaque
"Unexpected end of JSON input" error in the console:

- **`api()` never throws on an empty/non-JSON body.** The shared client helper now reads the body as
  text and returns a structured `{success:false, message}` instead of letting `Response.json()`
  throw a raw parse error. A 404 (e.g. a stale server missing a newly added endpoint) now reports a
  clear "Empty response (HTTP 404) — this build may be missing the endpoint; redeploy?" message.
- **`GET /patches` and `/patches/{id}/detail` are wrapped in try/catch**, returning a JSON error
  payload instead of a bare 500 if anything unexpected happens while assembling the list/detail.
- **Fixed one-shot phrase detection** so "run this once" / "do this once" are recognized (also adds
  "just once" / "only once").
- **New DB-backed tests** (`PatchCenterTests`) exercise `ListPatchesForCenter`, the per-mission and
  per-objective patch rollups, and `ListEndedObjectives` against a real SQLite database, so a query
  dialect/column error is caught in CI rather than as a runtime 500.



## v1.8.16 — Objective Lifecycle Hardening + Visual Patch Review Center

Two focused improvements to how the colony ends autonomous work and how the operator reviews the
changes it proposes. See `docs/ROADMAP.md` for the 10-phase direction; Phases 1–2 ship here.

**Objective lifecycle (Phase 1).** One-shot and verification-only objectives now end cleanly instead
of regenerating near-identical missions until loop detection retires them:

- New clean-completion path (`ObjectiveLifecycle.EvaluateCompletion`) runs *before* loop detection.
  A successful one-shot objective ends `completed_successfully`; a successful verification-only /
  read-only / no-patch objective that discovered no new work ends `stopped_no_followup_required`.
  Broad standing objectives (no one-shot/verify wording, `max_runs` 0/>1) keep running as before.
- Loop detection is preserved strictly for true repeated loops — it is no longer the normal ending
  path for successful maintenance work.
- Unified end reasons stamped on every ended objective: `completed_successfully`,
  `stopped_no_followup_required`, `retired_looping`, `failed`, `manually_paused`, `manually_stopped`.
- New config `autonomy_oneshot_completion` (default on) gates the behaviour.

**Planner constraint enforcement (Phase 1).** The planner now reads explicit mission constraints
(`MissionConstraints`): a `verification-only` / `read-only` / `do not modify files` mission gets a
hard prompt directive *and* a deterministic post-plan strip of every coder patch-proposal task, with
a read-only file-inspection task substituted so verification missions still actually inspect files.
Normal code-change missions keep the full coder/builder/verifier workflow.

**Visual Patch Center (Phase 2).** A new admin page lists every patch proposal with status and risk
badges, filterable by status, risk, mission, objective, and file path. Each patch expands to a
unified diff (removed/added/context) and offers Approve / Reject / Apply / View Mission — reusing the
existing approve-then-apply safety model, with an Apply confirmation that surfaces operator safety
checks (risk level, missing old content, no pre-apply backup). Patch links are wired into mission
Results (per-mission counts + deep link), the Autonomy runs table (a patch summary per run), and the
Completed Objectives detail (patch activity per objective). Additive API only: `GET /patches`,
`GET /patches/{id}/detail`, plus patch rollups on the report/runs/objective endpoints. Storage is
unchanged; new `PatchStatus.Superseded` completes the status model. No Python touched.



## v1.8.15.7 — System Health panel on the Overview

Added an enterprise-style **System Health** card to the Overview, giving an at-a-glance read on the
three things that actually go wrong in a long-running colony, each with a green/amber/red status dot:

- **Autonomy** — RUNNING / IDLE / HALTED / OFF state, missions-today vs the daily cap, live backlog
  (pending + active objectives), and effective/configured concurrency. Sourced from `/autonomy/status`.
- **Storage** — free disk with a usage bar that turns amber at 85% and red at 93%, plus the SQLite DB
  size and backup count/size. Sourced from `/maintenance/stats`. When disk is tight *and* backups are
  prunable, an alert line points straight to **Settings → Maintenance → Flush Cache** to reclaim it.
- **Coder Patches** — recent parse success rate (`patch_set_created` vs `patch_proposal_parse_failed`)
  with applied count, so the v1.8.15.6 parse-hardening win stays visible. Sourced from `/events/json`.

The panel polls every 8s but only fetches while the Overview is the visible page (and refreshes
immediately on navigation to it), so it adds no load elsewhere. UI-only change — no API surface added.

## v1.8.15.6 — Coder patches actually parse now (fewer patch_proposal_parse_failed)

Live diagnostics showed a steady stream of `patch_proposal_parse_failed` — coder output that never
reached the approval queue or auto-apply. Root causes and fixes:

- **Raw control chars in JSON (the big one).** Small local models emit patches with a literal
  newline inside string values (`"new_content": "line1<newline>line2"`), which strict JSON rejects
  with `'0x0A' is invalid within a JSON string`. `Json.ExtractJsonObject` now retries every parse on
  a copy where control chars inside string literals are escaped, and tolerates trailing commas and
  comments. This recovers the most common failure class.
- **Placeholder file paths.** The v1.8.13 neutral example (`file.ext`) was being copied literally,
  producing `file_path: .ext` — rejected as an unsupported type. The coder prompt now uses an
  obvious `<...>` placeholder with real examples, forbids placeholder paths outright, and tells the
  model to escape newlines as `\n` (single-line JSON).
- **One bad proposal no longer discards the set.** `PatchProposalParser` parses each proposal in its
  own try/catch — a malformed entry (bad path, missing reason) is skipped and the valid proposals in
  the same set survive, instead of the whole patch set being thrown away.
- Tests: `JsonRepairTests` (raw newline/tab/CR recovery, trailing commas, code fences, prose
  stripping, valid-escape round-trip, control-chars-outside-strings untouched).

## v1.8.15.5 — Completed Objectives box for loop-retired objectives

Objectives the Director retires for looping (Phase 4 loop detection) previously stayed in the
paused backlog, mixed in with normal paused objectives. They now move to a dedicated
**Completed Objectives** box under Configuration → Autonomy.

- The Director stamps a retirement marker (`retired_code`, `retired_reason`, `retired_at`) onto
  the objective's metadata when it retires it — reusing the existing objective model, no schema
  change and no change to the loop-detection logic itself.
- The active/paused backlog table now filters out `retired_code == "looping_goals"`; normal
  paused objectives (circuit-breaker / stale) are unaffected.
- New **Completed Objectives** card: each loop-retired objective is one collapsed expandable row —
  title, a **Stopped** badge, a **Looping** badge, and the short stop reason. Expanding lazy-loads
  the compiled detail (objective ID, title, stop/loop reason, related runs, missions, tasks, and
  the stopped timestamp).
- New: `SqliteMemory.ListRetiredObjectives`, `ApiHost.CompletedObjectiveDetail`; endpoints
  `GET /objectives/completed` and `GET /objectives/{id}/detail` (both `read_objectives`); retirement
  markers added to the `/objectives` response.
- Tests: `ListRetiredObjectives_FindsLoopingRetired_ByMetadata` (looping-retired found; plain-paused
  and stale-retired excluded).

## v1.8.15.4 — Disk hygiene: backup retention + maintenance controls

Live diagnosis of a filling 51 GB disk found the cause: **1,032 pre-mission DB backups = 34 GB**.
ANTHILL copies the whole 68 MB database before every mission and never pruned the copies. Fixed at
the root, plus operator controls for cleanup.

- **Backup retention (the fix).** After each pre-mission backup the Queen now prunes the backup
  directory to the newest `max_db_backups` (default 10) via `FileSecurity.PruneBackups`. The backup
  dir is now bounded (~10 × DB size) instead of growing one full copy per mission forever. Existing
  bloat is reclaimed by the first Flush (or the next mission's auto-prune).
- **Flush Cache** (Settings → System Info → Maintenance): prunes old backups, deletes events older
  than `event_retention_days` (0 = keep all), and `VACUUM`s the database — reports the bytes freed.
  The panel shows disk free, DB size, and backup count/size.
- **Clear Missions** (Missions page): deletes all mission-execution history (missions, tasks,
  events, patches, approvals, sources, agent messages) and compacts — keeps objectives, pheromones,
  users, providers, config.
- **Cancel All** (Missions page): drops all queued jobs (a running mission finishes on its own,
  bounded by its timeout). Also adds `POST /jobs/{id}/cancel` and `POST /jobs/cancel-all`, with a
  `cancelled` job status the worker honors.
- **Dump Directives** (Autonomy page): clears the entire objective backlog + its run history.
- **Reset Config** (Maintenance): resets all tunable settings to safe defaults while **preserving
  connection settings** (Ollama host/model/routes, API bind, workspace) so a reset never strands the
  colony.
- New: `SqliteMemory.Maintenance` (FlushCache / ClearMissionHistory / ClearObjectives / TableCounts
  / DatabaseFileBytes), `FileSecurity.PruneBackups`/`BackupStats`, `AnthillRuntime.ResetConfig`,
  `ApiJobRegistry.Cancel`/`CancelAll`; endpoints `GET /maintenance/stats`, `POST /maintenance/{flush,
  clear-missions,reset-config}`, `POST /objectives/clear`. Config: `max_db_backups`,
  `event_retention_days`.
- Tests: `MaintenanceTests` (retention keeps newest N, reports freed, edge cases).

## v1.8.15.3 — Install polkit natively in the LXC setup

v1.8.15.2 shipped the scoped polkit rule but only installed it *if* polkit was already present —
on a fresh Debian LXC it isn't, so the installer skipped it ("polkit not present"). `setup.sh`
now **installs polkit itself** (like it installs the .NET SDK): `apt-get install polkitd` with
fallbacks to `polkit` / `policykit-1` across distros, enables the daemon, writes the scoped JS
rule (modern polkit, Debian 12+) *and* a `.pkla` fallback (legacy polkit 0.105, Ubuntu 22.04),
and restarts polkit. After a `git pull && bash deploy/lxc/setup.sh` the operator Shell's
Restart/Status/Logs buttons work with no extra steps. No application-code change — same binary,
version bumped for deploy traceability.

## v1.8.15.2 — Strategist intent fidelity, backlog-sprawl cap, operator-shell service control

The v1.8.15.1 live test proved the planner now routes file goals to the coder, but exposed that
the **Strategist drifts** — it rewrote a one-shot charter ("create docs/x.md") into an unrelated
goal ("train a model on docs") before the planner ever saw it — and that autonomous runs **spawn
follow-up objectives aggressively** (13 accumulated in a short test). Both fixed, plus the
operator-shell service-control item from the roadmap.

Autonomy — intent fidelity:

- **One-shot objectives are never reinterpreted.** An objective with `max_runs == 1` is an
  explicit do-this-once task; `Strategist.GenerateGoal` now uses its charter verbatim (bypassing
  the LLM entirely, `Source = "charter_verbatim"`) so the operator's intent reaches the planner
  unchanged. Standing objectives (`max_runs` 0/>1) still go through the Strategist.
- **The Strategist prompt now preserves the charter.** It must produce a goal that directly
  accomplishes the charter (execute it as written on the first run; only take the next incremental
  step once a prior run already accomplished it) — never substitute a different or broader task —
  and follow-ups should "almost always be empty" (add one only for a genuinely distinct new
  objective, never to seem productive).

Autonomy — sprawl guard:

- **`autonomy_max_backlog` (default 40).** The Strategist stops enqueuing self-generated follow-up
  objectives once the open backlog (pending + active) reaches the cap — a structural bound on
  sprawl regardless of model behavior, on top of the existing per-run rate and depth caps. 0 = no
  cap. Clamped, settings-whitelisted, in `config.example.json`.

Operator-shell service control:

- The systemd unit's `NoNewPrivileges=true` blocks `sudo`, so `setup.sh` now installs a **scoped
  polkit rule** (`deploy/lxc/anthill-polkit.rules.template` → `/etc/polkit-1/rules.d/49-anthill.rules`)
  that lets the admin-only operator Shell manage **only** the `anthill.service` unit
  (restart/stop/start/status) over D-Bus — no privilege escalation, hardening untouched. The Shell
  tab gains quick buttons: Service status, Recent logs, Restart service (with a confirm), Host
  health. Best-effort: skipped with a message if polkit isn't installed. Docs in DEPLOYMENT.md.
- Tests: `OneShotObjective_UsesCharterVerbatim_EvenWithNoRouter`.

## v1.8.15.1 — Fixes from the live Phase 5 test

A live test of Phase 5 on the LXC confirmed both the keep and rollback branches work end to end
(patch applied → verify → kept + approval consumed; and applied → verify failed → rolled back,
workspace clean). It also surfaced three issues, all fixed here:

- **`DELETE /objectives/{id}` returned 500 for any objective that had run.** `autonomy_runs` has a
  foreign key to `objectives(id)` with `foreign_keys=ON`, so deleting an objective with run history
  threw — meaning the Delete button in the backlog was broken for anything that had executed.
  `DeleteObjective` now cascades the dependent runs and detaches follow-up children in one
  transaction; the endpoint returns a clean error instead of a 500 on any other failure.
- **The planner rarely routed file-creation goals to the coder ant** — the root cause of "work
  happens but nothing lands." Two prompt bugs: `docs` was listed as a *web-search* trigger (so
  "create a file in docs/" went to web research), and nothing told the planner that creating or
  editing a file requires a coder patch. The planner prompt now states plainly that any goal which
  creates/adds/writes/edits/patches a file (including `.md`/config) **must** include a
  `patch_proposal` coder task, clarifies that proposing a patch is expected (not a "don't write
  files" violation), and stops treating a documentation path as a web trigger. The offline fallback
  planner now checks code/file keywords **before** the web branch and recognizes create/add/write/
  edit/`.md`/`.cs` goals.
- **Auto-apply on a read-only workspace failed one patch at a time with no explanation.** On a
  hardened LXC (`systemd ProtectSystem=strict`) the source tree is read-only to the service, so
  every apply failed individually. The runner now does a one-shot writability preflight and, if the
  workspace root can't be written, logs a single clear `autonomy_autoapply_skipped`
  (`reason: workspace_readonly`) pointing at `agent_workspace_dir` — instead of a stream of
  `apply_failed`. Docs add the writable-checkout deployment pattern for real self-modification.
- Tests: `DeleteObjective_CascadesRunsAndDetachesChildren`.

## v1.8.15 — Phase 5 autonomy: gated auto-apply (the autonomy roadmap is complete)

The Director can now **ship low-risk fixes on its own** instead of queueing every patch for a
human forever — the direct answer to the approval pile-up. It's the highest-risk capability in the
system (autonomous writes to disk), so it's fail-closed and multiply gated, and the entire safety
model is *apply → verify → keep-or-rollback*.

- **Strict eligibility gate** (`Autonomy/AutoApplyPolicy.cs`): a patch is auto-appliable only when
  every condition holds — the master switch is on; the change is `add`/`modify` (never
  delete/rename); the file path matches an operator glob in `autonomy_autoapply_paths` (an **empty
  allowlist means nothing is eligible**, so it's inert until you widen it); and the change is
  within `autonomy_autoapply_max_lines`. Glob supports `**`/`*`/`?`.
- **Apply → verify → rollback** (`Anthill.Api/AutoApplyRunner.cs`, runs on the Director thread
  after a *successful* mission): applies eligible patches with per-file backups, then runs
  `dotnet build && dotnet test` (or your `autonomy_autoapply_verify_cmd`) in the workspace,
  timeout-bounded. **Green** ⇒ changes stay, the matching approval requests are marked `consumed`
  (they leave the queue), optional local `git` commit (never pushed). **Red/timeout** ⇒ every
  applied patch is rolled back (modify → restore backup, add → delete) and marked failed.
- **Depends on the write gates** (`patch_application_enabled` + `file_writing_enabled`); logs
  `autonomy_autoapply_skipped` and does nothing if they're off. **Forced off in every safety
  profile** and off by default.
- **Full audit trail**: `autonomy_autoapply_started`/`_applied`/`_verified`/`_reverted`/
  `_rolled_back`/`_ineligible`/`_skipped` events; applied/reverted patches appear in the mission
  report's tangible-changes with their final status.
- **Config** (clamped, settings-whitelisted, editable in **Configuration → Security → Autonomous
  Auto-Apply**): `autonomy_autoapply_enabled`, `autonomy_autoapply_paths`,
  `autonomy_autoapply_max_lines`, `autonomy_autoapply_verify_cmd`,
  `autonomy_autoapply_verify_timeout`, `autonomy_autoapply_git_commit`.
- New `Queen.ApplyPatchForAutomation` / `RollbackAutoApplied` (structured apply with backup path
  for rollback). `/autonomy/status` gains `autoapply_enabled` / `autoapply_paths`.
- Tests: `AutoApplyPolicyTests` — eligibility matrix, glob semantics, size cap, change-type,
  disabled and empty-allowlist denial. (`InternalsVisibleTo("Anthill.Tests")` added to
  Anthill.Core so the suite can exercise the internal `GlobMatches` helper — same pattern as
  Anthill.Api.)

Also fixed: **`UpdateChecker.Compare` didn't tolerate a leading `v`** — `Compare("v1.8.15", …)`
parsed the `v1` segment as `0`, so the version read as older (a CI test caught it; production was
unaffected because `Fetch()` stripped the `v` before calling `Compare`). `Compare` now strips a
leading `v`/`V` on both sides itself.

With Phase 5 in, **the autonomy roadmap (Phases 0–5) is complete.**

## v1.8.14.5 — Auto-publish releases + hardening pass (audit)

Wraps the release-automation change with a thorough audit of everything shipped in the 1.8.14.x
line — resource leaks, security boundaries, and correctness. All findings fixed.

Release automation:

- The release workflow now **publishes** the GitHub Release and pushes the GHCR container package
  automatically on every tag push (was: created as a draft for manual publishing). `make_latest`,
  and four-part maintenance tags (`vX.Y.Z.W`) matched explicitly. README/DEPLOYMENT.md updated.

Security:

- **Fixed a privilege leak in the mission report.** `GET /missions/{id}/report` is served under
  `read_status` (which Mission Coordinators hold) but surfaced patch proposals, approval state,
  and autonomy objectives — all admin-only reads (`read_patches`/`read_approvals`/`read_objectives`
  are never in the coordinator set). The report now includes those sections only for callers who
  could read them directly (`CallerHas`), so it can't be used as a side channel around the
  permission model. Non-admins still get goal, status, final output, per-task results, and
  problems (all things they can already read).
- **Bounded two unbounded `?limit` query params** (`/events/json`, `/pheromones/json`) — a huge
  value could sweep the entire log/trail table in one request; now clamped.

Resource leaks:

- **Removed two per-request `new HttpClient` allocations** (`/system/summary`'s Ollama probe and
  the `/ollama/models` proxy). Under the header's periodic polling these leaked sockets over time;
  both now share one static client with per-call `CancellationToken` timeouts.
- **Session registry no longer grows unbounded**: abandoned session tokens (user logs in, never
  returns) were only evicted when that exact token was next resolved. Login now opportunistically
  prunes expired sessions.

Correctness:

- **Operator shell could truncate output.** `Process.WaitForExit(timeout)` can return before the
  async stdout/stderr handlers finish draining; the executor now calls the parameterless
  `WaitForExit()` afterward to guarantee a full flush, and locks the output builders against the
  threadpool callbacks that append to them.
- Tests: `PermissionBoundaryTests` (admin-only vs coordinator permission matrix).

## v1.8.14.4 — Live header status: update check, model/provider popover, local-vs-cloud icon

The top-right header was static — a fixed model string and an "Online" badge that didn't say what
was online. It's now a live, clickable status chip.

- **Update check** (`GET /update/check`): compares the running version against the latest release
  tag on the public GitHub repo and flags when a newer one exists. Result is cached server-side
  (30 min) so the header poll never hammers GitHub, and every failure (offline, rate-limited, no
  releases) degrades to "unknown" rather than erroring. When an update is available the chip shows
  a pulsing dot and the popover gives the new version, a release-notes link, and the exact LXC
  upgrade command. A "Re-check" button forces a fresh look (`?force=1`).
- **Local vs Providers icon**: the chip carries a monitor icon (green **LOCAL**) when every model
  role runs on local Ollama, or a cloud icon (purple **PROVIDERS** / **MIXED**) when any role is
  routed to OpenAI/Anthropic/Perplexity/OpenRouter — so the colony's cost/privacy posture is
  visible at a glance.
- **What's actually online**: the chip's dot now reflects backend health, not just the API — it
  goes red if Ollama is unreachable even while the API answers. The popover breaks it down: API
  server, Ollama backend reachability (with a live 3s probe), and Ollama host.
- **Model visibility + quick actions** (`GET /system/summary`): the popover lists every role's
  provider + model (each tagged local/cloud), the default model, and how many providers are
  connected, with one-click buttons to Ant Config (change models) and Settings → Providers.
- New: `src/Anthill.Api/UpdateChecker.cs`; `GET /update/check` + `GET /system/summary`.
- Tests: `UpdateCheckerTests` (dotted four-part version ordering, leading-v tolerance).

Release automation:

- The release workflow now **publishes** the GitHub Release and the GHCR container package
  automatically on every tag push (was: created as a draft for manual publishing). Each
  `git push origin vX.Y.Z[.W]` builds the self-contained linux-x64/win-x64 archives, pushes
  `ghcr.io/<repo>:<version>` + `:latest`, and publishes the Release (marked "latest") with the
  matching CHANGELOG section as notes. Four-part maintenance tags (`vX.Y.Z.W`) are matched
  explicitly. Docs (README, DEPLOYMENT.md) updated to match.

## v1.8.14.3 — Configuration: Security tab + admin-only Shell console

> **Pre-commit checklist** — the docs must be true before every commit:
> 1. Bump the version in all markers: `Directory.Build.props`, `AnthillRuntime.Version`,
>    `src/Anthill.Api/Ui/index.html` (title + auth logo + nav badge), `src/Anthill.Api/Program.cs`,
>    `src/Anthill.Cli/Program.cs`, `build.sh`, `build.ps1`, and the README banner.
>    Verify with: `grep -rn "<old version>" --exclude-dir=.git --exclude-dir=obj --exclude-dir=bin .`
> 2. Add the CHANGELOG entry (this file) — it becomes the GitHub release notes.
> 3. **Update README.md sections touched by the change** (Colony UI Guide, API Reference,
>    Configuration Reference, deployment sections) and `config.example.json` for new knobs.
> 4. Update `docs/AUTONOMY.md` / `docs/DEPLOYMENT.md` when behavior in their scope changes.
> 5. Sweep for leftovers: stale comments, dead config keys, outdated status claims, debug code.
> 6. `dotnet test Anthill.sln -c Release` green, then commit + tag + push.

## v1.8.14.3 — Configuration: Security tab + admin-only Shell console

Two new admin-only pages under Configuration.

- **Security tab**: a single place for the app's security posture — auth mode, safety profile,
  network bind exposure, and encryption-at-rest at a glance — plus live toggles for every
  capability gate (web search, file read/write, patch application, the AI ants' shell tool), the
  **workspace boundary** (`agent_workspace_dir`, the only path the file/coder ants may touch), and
  the operator-shell controls. All persist through the existing `/settings` path.
- **Shell tab**: a direct interactive terminal into the host ANTHILL runs on (the LXC/VM/box) —
  command input with history (↑/↓), streamed stdout/stderr, exit code, elapsed time, and a
  settable working directory. Built for host maintenance the AI ants must never do (restart the
  service, pull updates, edit config).

Because the Shell console is host remote-code-execution, it is gated four independent ways:
(1) authenticated, (2) **admin role only** — the new `operator_shell` permission is never in the
coordinator set, so a Mission Coordinator cannot see or use it; (3) the `operator_shell_enabled`
config gate (toggleable from the Security tab); and (4) **every command is written to the audit
event log** (`operator_shell_command` before it runs, `operator_shell_result` after) with the
operator's username, so there's a durable record of who ran what. Each command is bounded by a
60-second timeout and its output capped. Per operator request it ships **enabled for admins** by
default; set `operator_shell_enabled: false` (or toggle it off in Security) on any install you
don't fully trust on the network. Distinct from `shell_tool_enabled`, which gates the AI ants'
*allowlisted* tool and stays off by default.

- New: `GET /shell/info`, `POST /shell/exec` (both `operator_shell`, admin-only);
  `src/Anthill.Api/OperatorShell.cs`; config keys `operator_shell_enabled` / `operator_shell_dir`;
  `api_auth_enabled` + `agent_workspace_dir` added to the settings snapshot.
- Tests: `OperatorShellTests` — admin-only permission, command execution, non-zero exit,
  working-directory handling.

## v1.8.14.2 — Results page; stale-UI cache fix; approval-queue dedupe

New — **Results page** (operator request: mission results shouldn't take over the whole screen):

- A dedicated **Results** nav page lists every mission (newest first, filterable by
  completed / partial / failed) as compact collapsible rows — status in plain English, goal,
  score, and finish time. Expanding a row lazily loads the full Mission Report inline: final
  output, per-task readable results, tangible changes with approval states, problems, and — new —
  the **autonomous-run context** (which objective drove the mission) and the **objectives this
  mission created** (Strategist follow-ups, now stamped with `created_by_mission_id` /
  `created_by_run_id` metadata when the Director saves them, so the lineage is queryable).
- Every **View Result** button routes here and auto-expands that mission (jobs lists, Missions
  page, and the Autonomy runs table's View). Running jobs keep a compact "View Status" quick
  view; the old full-screen overlay remains only as a fallback for legacy jobs without a
  mission id.
- New API: `GET /missions/json?limit=` (mission history as JSON) and mission reports now include
  `autonomy_run` + `created_objectives`. New `SqliteMemory.GetAutonomyRunForMission` /
  `ListObjectivesCreatedByMission`.

Verified live against the running LXC instance (v1.8.14.1) before shipping: the backend live
task feed and the new canvas logic were confirmed working in a fresh browser session — particles
flowing, per-ant activity tracking real task states, correct idle when nothing runs. The
"still broken" console was the *browser's cached copy of the previous UI*.

- **`/ui` is now served with `Cache-Control: no-store`.** The console is embedded in the binary,
  and without cache headers a browser can silently pin an operator to the previous version's
  UI after every upgrade (stale canvas logic, missing panels) until a manual hard-refresh. Now
  every page load fetches the UI the running binary actually ships. (One last hard-refresh is
  needed to pick this version up; after that, never again.)
- **Approval-queue flooding fixed with dedupe.** High-frequency autonomous testing (observed
  live: 62 missions/hour, 1000/day until the daily budget tripped) re-proposes the same change
  run after run while the first request sits unreviewed — every rerun stacked another identical
  approval request. `Queen.ProcessPatchProposals` now checks
  `SqliteMemory.HasDuplicatePendingApproval` (same file, change type, and old/new content,
  compared after decryption) and skips creating a duplicate, logging an
  `approval_request_deduped` event instead. Decided (approved/rejected) requests never block a
  fresh proposal. Reminder that stacking ≠ malfunction: the Director *never* auto-approves —
  clearing the queue is the operator's half of the workflow until gated auto-apply (Phase 5)
  lands.
- **Tests**: `ApprovalDedupeTests` — identical pending change detected; different content/file
  not deduped; decided approvals don't block; null-content comparisons.

## v1.8.14.1 — Mission Reports: see exactly what the colony did, in plain English

Operator feedback from live use: work was "seemingly being done" (autonomous runs completing,
follow-up objectives appearing) but nothing readable in the UI showed what actually happened or
changed. Two root causes: (1) the only result view was the raw CLI dump — final output, debug
trace, and task JSON in one wall of jargon; (2) the tangible outputs of missions (patch
proposals waiting in the approval queue) were never connected to the mission/run that produced
them. Note the design constraint that makes visibility essential: **the colony cannot change
files or its own UI by itself** — every file change is a patch proposal that waits for human
approval + apply. If nothing is approved, nothing changes; the console now says so explicitly.

New:

- **`GET /missions/{id}/report`**: structured, human-readable report per mission — goal, status,
  score; the mission-level **final output** (kept separate from per-task outputs, since tasks are
  the steps and the mission is the deliverable); a per-task breakdown (title, ant, status,
  elapsed, readable output, and the *why* for failed/skipped/blocked tasks); **tangible changes**
  (every patch proposal the mission created, its file, reason, and current state — awaiting
  approval / approved / applied to disk / rejected, with apply errors); pending-approval count;
  sources saved; and **problems** — including `patch_proposal_parse_failed`, the silent killer
  where the coder did work but its proposal never reached the approval queue.
- **Plain-English task outputs**: coder results (raw JSON patch sets) are translated to
  "Proposed modify to src/... : reason" lines server-side (`ApiHost.ReadableTaskOutput`);
  other ants' prose passes through; malformed output falls back to raw text.
- **Mission Report modal**: "View Result" on any completed job now renders the structured report
  — status in words, final output, tangible-changes list with a pointer into Approvals,
  problems, and an expandable per-task list — instead of the raw CLI text (which remains the
  fallback for legacy jobs without a mission id).
- **Autonomy runs are inspectable**: each row in Recent Autonomous Runs gains a **View** button
  opening the same mission report, so every unattended run answers "what did it actually do,
  and did anything tangible come out of it?" in one click.
- **`SqliteMemory`**: `ListPatchProposalsForMission` / `ListApprovalRequestsForMission`
  (secret-free, per-mission).
- **Tests**: `ReportTests` — coder-JSON translation, empty-proposal wording, malformed-output
  fallback, prose passthrough. `InternalsVisibleTo("Anthill.Tests")` added to Anthill.Api.

Fixed (Colony canvas + Autonomy page housekeeping):

- **Ant/Queen hover tooltips showed activity over 100% and not live data.** Three bugs, one of
  them structural: (1) an operator-precedence error in the animation loop —
  `(colonyActivity[ant]||0-n.activity)` parses as `colonyActivity || (0-activity)`, accumulating
  activity unboundedly every frame; (2) the activity source was "this ant's share of all tasks"
  (including finished ones), not a live reading; and (3) — the structural one — **task rows were
  only persisted at mission start (before tasks exist) and mission end**, so `/graph` had no
  nodes at all while a mission ran; every mid-run number the canvas ever showed was stale data
  from the previous completed mission. Fixed end to end: the Queen now persists the planned task
  DAG before execution and upserts each task on every status transition (started → live
  "running"; complete/failed/skipped on finalize — new `SqliteMemory.SaveTask`), and the canvas
  computes activity from those current task states each poll (running = 100%, queued work = 35%,
  idle = 0%), clamped to [0,1] everywhere. Signal particles, node glow, the hover panel, and the
  task-DAG dataflow arrows now reflect what the colony is doing *right now* — the graph poll
  tightened from 5s to 2.5s to match.
- **Colony canvas sharpened**: the canvas now renders at the display's real pixel density
  (devicePixelRatio-scaled backing store, logical-coordinate drawing) — crisp nodes, edges, and
  labels on HiDPI screens instead of the previous blurry 1x upscale.
- **Autonomy tables no longer grow the page unboundedly**: the Objectives and Recent Autonomous
  Runs boxes are collapsible (click the header) and cap at ~20 rows with their own scrollbar and
  sticky column headers.
- **Docs housekeeping**: README brought up to date with everything shipped since v1.8.12
  (Concurrency/Governor status card, Score column, Mission Report views, `/missions/{id}/report`
  in the API reference, autonomy-knob pointers), and a pre-commit checklist added at the top of
  this file so the docs stay true on every release.

No schema change, no config change.

## v1.8.14 — Phase 4 autonomy: the learning loop

Mission outcomes now feed back into what the Director chooses to work on. Design per operator
review: read-time bias (stored priorities never drift — same philosophy as Phase 3 aging) and
auto-pause retirement with explicit events (never delete; a human reviews and resumes).

New:

- **Per-objective success EMA** (`objectives.success_ema`, schema v10 → v11, additive migration):
  every recorded run folds its mission success score into an exponential moving average
  (`autonomy_score_ema_alpha`, default 0.3; an unscored/failed run counts as 0). Always recorded
  — even with learning disabled — so history exists the moment it's turned on.
- **Selection bias** (`Autonomy/ObjectiveLearning.cs`, new): at selection time an objective's
  EMA adds a bounded, linear bias to its effective priority — EMA 1.0 → +`autonomy_priority_bias_max`
  (default 2), EMA 0.5 → 0, EMA 0.0 → −max. Computed read-time in
  `SqliteMemory.EffectivePriority` alongside Phase 3 aging; new objectives (null EMA) are
  unbiased. Operator numbers in the backlog stay authoritative.
- **Stale retirement**: after `autonomy_retire_min_runs` (default 5) runs, an objective whose EMA
  is below `autonomy_retire_score_threshold` (default 0.25) is auto-paused — it keeps running
  without producing value.
- **Loop retirement**: if the last `autonomy_loop_window` (default 4, 0 = off) generated goals
  are all near-identical (≥ `autonomy_dedupe_similarity` keyword overlap — the exact metric the
  Strategist's dedup uses), the objective is auto-paused. Catches the charter-fallback spiral:
  dedup already replaces repeat goals with the charter, so a true loop shows up as the same goal
  run after run.
- **Retirement = pause + event, never delete**: the Director emits an `objective_retired` event
  (code `stale_low_success` or `looping_goals`, with reason, EMA, and run count) and sets the
  objective to Paused, exactly like the existing failure circuit breaker. Resume from the
  Autonomy page after review. Retirement checks run on the director thread after each outcome is
  recorded, so nothing races the objective's own bookkeeping.
- **Config**: `autonomy_learning_enabled` (default true; false = exact Phase 3 behavior),
  `autonomy_priority_bias_max`, `autonomy_score_ema_alpha`, `autonomy_retire_min_runs`,
  `autonomy_retire_score_threshold`, `autonomy_loop_window` — all clamped, all in the settings
  whitelist; toggle + integer knobs editable from Settings → Colony.
- **Observability**: `/objectives` and the Autonomy page's backlog table gain a **Score** column
  (the EMA, color-coded); `autonomy_mission_finished` events and `/autonomy/status` include
  `success_ema` / `learning_enabled`.
- **Tests**: `LearningTests` — EMA seeding/smoothing/persistence, bias linearity and bounds,
  EMA-driven selection ordering (and its disappearance when learning is off), stale/loop/never
  retirement decisions.

## v1.8.13 — Fix: coder ant proposed Python patches regardless of the project's language

Reported from live use: send the colony an objective against this (C#) repo and the coder ant
comes back proposing Python. Root cause was leftover DNA from ANTHILL's original Python build
(`py.old/`) — three compounding biases, no model misbehavior:

- **CoderAnt's JSON format example showed a `.py` path** (`"file_path":
  "relative/path/to/file.py"`). Small local models imitate format examples very literally, so
  the example language became the answer language. Now a neutral `file.ext`, plus a new
  first-position rule: match the language/conventions of the files visible in context, and if no
  existing code is visible and the goal names no language, return an empty proposals list rather
  than guess.
- **FileAnt injected `anthill.py` as a candidate path** whenever a mission mentioned "anthill",
  "this script", or "main script" — a relic of the Python-era entry point that fed Python-flavored
  context to every downstream ant. Removed; candidate paths now come only from what the mission
  text actually names.
- **FileAnt's path-extraction regex couldn't see .NET paths**: its suffix list (`py|txt|md|...`)
  predated the port and omitted `cs|csproj|sln|props|targets` (and other patchable types:
  `sh|bat|ps1|cmd|go|rs|java|kt|rb|php|tf|hcl|sql`). A mission saying "fix
  src/Anthill.Api/ApiHost.cs" never surfaced that path as a read candidate. The list now matches
  `AnthillRuntime.PatchAllowedSuffixes`, so every file type the coder may patch is also one the
  file ant can spot and read — including the colony's own sources for self-modification missions.

No schema change, no API change, no config change.

## v1.8.12 — Phase 3 autonomy: concurrent missions + ResourceGovernor

The Director can now run up to `autonomy_concurrency` missions side by side (default 1 —
behavior is unchanged until the operator raises it; clamped 1–8). Design decisions per operator
review: strict-priority scheduling with anti-starvation aging, and a load/probe governor with
full VRAM tracking deferred to a later hardware-aware scheduler phase.

New:

- **`ResourceGovernor`** (`src/Anthill.Core/Autonomy/ResourceGovernor.cs`): sizes effective
  concurrency each cycle from the configured cap — and can only ever lower it. Signals:
  normalized CPU load per core (≥1.25 halves, ≥2.0 clamps to 1), available-memory fraction
  (≤20% halves, ≤10% clamps to 1), and an Ollama probe (`GET /api/version`, 15s cache —
  unreachable clamps to 1, ≥2.5s latency halves). Unreadable *host* signals fail open (skip);
  a dead *backend* fails safe (clamp to 1 — missions would fail anyway, don't multiply them).
  Skipped entirely when `use_ollama` is false, so offline installs are never clamped by it.
- **Concurrent Director loop** (`src/Anthill.Api/ColonyDirector.cs`): non-blocking launches with
  an in-flight table, reaped as jobs finish. Everything still happens on the one director thread
  (Strategist/BudgetGuard stay sequential by construction); the hard rails are re-checked before
  every individual launch. Stop/kill-switch now *drains*: no new launches, in-flight missions
  finish and are recorded, then the thread exits — nothing is ever left unrecorded.
- **Strict priority + aging** (`SqliteMemory.NextReadyObjectives`): slots fill with the
  highest-effective-priority distinct ready objectives; an objective never runs two missions at
  once. Effective priority = priority + 1 per `autonomy_aging_minutes` waited (default 30;
  0 = pure strict priority); longest-queued wins ties. Computed at read time — stored priorities
  never drift.
- **Config**: `autonomy_concurrency`, `autonomy_aging_minutes` — in `config.example.json`, the
  settings whitelist, `/settings`, and the Settings → Autonomy panel.
- **Observability**: `/autonomy/status` gains `concurrency_configured`/`concurrency_effective`,
  `governor_code`/`governor_reason`/`governor_signals`, `aging_minutes`, and `in_flight`;
  `autonomy_mission_started` events carry the governor verdict. Autonomy page: Concurrency KPI +
  In-flight and Governor rows.

Fixed (latent, pre-existing):

- **`Queen.LastMissionId` race**: with >1 job worker, a finishing worker could stamp its job with
  *another* worker's mission id. `Queen.RunMission` now reports the mission id through an
  `onMissionCreated` callback the moment the row is persisted, and `ApiJobRegistry` uses it —
  also making the mission id visible on the job while it's still running. `LastMissionId` remains
  for the single-mission CLI path.
- Job worker pool is sized `max(api_job_workers, autonomy_concurrency)` at boot so concurrent
  autonomous missions actually get worker slots instead of queueing behind each other.

Validation: `GovernorTests` (every clamp path, fail-open vs. fail-safe, tightest-constraint-wins,
throwing readers), multi-slot selection/aging tests in `AutonomyTests`, and an offline two-slot
Director run in `DirectorTests` asserting both objectives complete with distinct mission ids and
per-objective run records. Existing Phase 0–2 suites unchanged and still green.

## v1.8.11 — Fix: Autonomy page's Start/Stop (kill switch) froze the UI via infinite recursion

No schema change, no API change — pure front-end JS bug in `src/Anthill.Api/Ui/index.html`.
Reported live as "the web app and service crashes when you hit the kill switch in the autonomy
page." Reproduced by driving the real running instance directly: clicking the "■ Stop" kill
switch caused the browser tab to stop responding to input.

Root cause: `openAutonomy()` called `showPage('autonomy')` at its top, but `showPage()` itself
calls `PAGE_ENTER['autonomy']()` right after switching pages — and `PAGE_ENTER['autonomy']` was
wired to call `openAutonomy()` again. That's unbounded mutual recursion:
`openAutonomy → showPage → PAGE_ENTER.autonomy → openAutonomy → showPage → ...`. It fired on
*every* visit to the Autonomy page — including the periodic status refresh the page runs while
open — and threw `RangeError: Maximum call stack size exceeded` hundreds of times per trigger
(confirmed live via the browser console). Each occurrence briefly pegs the JS main thread as it
unwinds thousands of stack frames, which is what made the tab appear to hang or "crash" right as
a click (like Stop) landed. `openAntConfig()` (the Ant Config page) had the exact same bug
pattern, not yet reported but fixed here too. The .NET backend was never actually affected —
`/health` and the Director's own stop/start logic kept working the entire time this was
happening; confirmed via `/autonomy/status` and the `autonomy_stopped`/`autonomy_started` event
log entries recording correctly through repeated live reproduction.

Fixed:

- **`openAutonomy()`**: no longer calls `showPage('autonomy')` — it's now a pure data-loader,
  correct since its only caller is `PAGE_ENTER['autonomy']`, which `showPage()` already invokes
  *after* switching to the page.
- **`openAntConfig()`**: same fix, same reasoning (its `showPage('antconfig')` call is gone; its
  second caller, the Ant Config "Reset" button, doesn't need a page-switch either since the user
  is already on that page when clicking Reset).

Validation:

- Reproduced and fixed live against the user's running LXC instance via direct browser
  automation: captured the exact `RangeError` stack trace from the browser console, hot-patched
  the corrected function into the live page, then repeated the same Start → Stop sequence with
  the patch active — no errors, no hang, instant response both times. `bash -n`/syntax not
  applicable (HTML/JS); confirmed by the live before/after test described above. Ship this build
  to make the fix permanent (the hot-patch only lived in that one browser tab's memory).

## v1.8.10 — Fix: LXC upgrade republish silently dropped the SQLite native library

No schema change. Bug found live re-running `deploy/lxc/setup.sh` on the user's LXC instance
immediately after the v1.8.9 ETXTBSY fix — the very first time a republish onto that install
directory ever ran to full completion. The service came up, then immediately crashed in a
restart loop:

```
Unhandled exception. System.TypeInitializationException: The type initializer for
'Microsoft.Data.Sqlite.SqliteConnection' threw an exception.
 ---> System.DllNotFoundException: Unable to load shared library 'e_sqlite3' or one of its
dependencies.
/opt/anthill/bin/e_sqlite3.so: cannot open shared object file: No such file or directory
```

Root cause: that same install directory had been publish-targeted several times across this
session's earlier v1.8.7/v1.8.8/v1.8.9 attempts, including at least one run that was killed
mid-bundle by the ETXTBSY bug itself. `dotnet publish` reused the leftover `obj/`/`bin`
incremental state from those prior (partially-failed) runs and decided the RID-specific SQLite
native asset (`e_sqlite3.so`) was already up to date — so it skipped copying it into the output
directory, even though it wasn't actually there. The resulting single-file binary builds, starts,
and immediately SIGABRTs the moment it touches the database.

Fixed:

- **`deploy/lxc/setup.sh`**: wipes `obj/`/`bin` for `Anthill.Cli`, `Anthill.Core`, and
  `Anthill.Api` immediately before every publish, so install/upgrade is always a from-scratch
  build rather than trusting incremental state that a prior interrupted run may have left
  inconsistent. Adds a post-publish check that fails loudly (with a clear error) if no
  `e_sqlite3` native library made it into the output directory, instead of letting it surface
  later as a silent SIGABRT crash loop under systemd.

Validation:

- Found via the user's real upgrade attempt on their LXC instance — full stack trace confirmed
  root cause precisely (`SqliteConnection..cctor` → `SQLitePCL.Batteries_V2.Init` →
  `DllNotFoundException`). Fix itself has **not been re-verified live** — no LXC/Proxmox host or
  dotnet SDK available in the environment this was authored in. `bash -n` syntax check passes.
  Confirm by re-running `bash deploy/lxc/setup.sh` and checking `ls /opt/anthill/bin/*e_sqlite3*`
  finds the native library, then that the service stays up (`systemctl status anthill`).

## v1.8.9 — Fix: LXC upgrade-in-place failed with "Text file busy"

No schema change. Bug found live re-running `deploy/lxc/setup.sh` to upgrade an already-running
LXC install to v1.8.8: `dotnet publish` failed with
`System.IO.IOException: Text file busy : '/opt/anthill/bin/anthill'` inside the `GenerateBundle`
MSBuild task.

Root cause: `setup.sh` republishes directly into `$INSTALL_DIR/bin`, which is exactly where the
systemd unit's `ExecStart` runs the binary from. .NET's single-file bundler does an in-place file
copy rather than write-to-temp-then-atomic-rename, and Linux refuses to open a currently-executing
binary for direct write access (`ETXTBSY`) — replacing a running program's file via `rename()` is
fine, overwriting it in place while it's executing is not. First-time installs never hit this
(nothing running yet); every subsequent upgrade-in-place did, 100% of the time.

Fixed:

- **`deploy/lxc/setup.sh`**: stops the `anthill` systemd unit immediately before the publish step
  (`systemctl stop anthill 2>/dev/null || true` — safe no-op on a first install, since the unit
  doesn't exist yet). The existing `systemctl restart anthill` at the end of the script already
  starts it back up regardless of whether it was freshly installed or stopped for an upgrade, so
  this was a one-line, symmetrical fix.

Validation:

- Found via the user's real upgrade attempt on their LXC instance — full MSBuild stack trace
  confirmed root cause precisely (`Microsoft.NET.HostModel.Bundle.Bundler.GenerateBundle` →
  `SafeFileHandle.Open` → `ETXTBSY`). Fix itself has **not been re-verified live** — no LXC/Proxmox
  host or dotnet SDK available in the environment this was authored in. `bash -n` syntax check
  passes. Confirm the fix by re-running `bash deploy/lxc/setup.sh` on the same instance and
  checking it completes without stopping mid-publish this time.

## v1.8.8 — Fix: provider Base URL override sent as a bare prefix, not a real endpoint

No schema change. Bug found live on a real LXC deployment: testing the OpenAI connection in
Settings → Providers failed every time with `ERROR: OpenAI request failed (404): `.

Root cause: the stored `base_url` override (`https://api.openai.com/v1`) was used as the literal
request URL in `OpenAiCompatibleClient`, with no path appended — so the request actually hit
`https://api.openai.com/v1`, not a real API route, and OpenAI correctly 404'd it. The value that
was stored is exactly how OpenAI's own SDKs define `base_url` (host + version prefix only, path
appended internally), so typing it that way into the override field is a completely reasonable
thing to do even though the field's placeholder shows the full path — the code should tolerate
both forms rather than silently breaking on one of them.

Fixed:

- **`OpenAiCompatibleClient.NormalizeEndpoint`** (covers OpenAI, Perplexity, and OpenRouter, which
  all share this client): if a configured endpoint doesn't already end with `/chat/completions`,
  it's appended automatically. Handles a trailing slash either way. Applied in the constructor, so
  it self-corrects for any already-stored override without needing a database fix or the user to
  re-save anything.
- **`AnthropicClient`**: previously didn't accept a `base_url` override at all —
  `ModelRouter.BuildKeyedClient`'s `"anthropic"` branch built the client with only the API key and
  model, silently discarding whatever was stored in `provider_credentials.base_url`. Now accepts
  an optional endpoint, normalized the same way (`/messages` appended if missing), wired through
  from `ModelRouter`.
- **`tests/Anthill.Tests/ProviderTests.cs`**: added `NormalizeEndpoint` coverage for both
  providers — bare prefix, full path, and either with/without a trailing slash — plus confirms
  `AnthropicClient` falls back to its documented default when no override is stored. Made both
  `NormalizeEndpoint` methods `public` (were `private`) specifically so this is directly
  unit-testable without a network call.

Validation:

- Found via live testing against a real running LXC instance (`10.10.10.60:8713`, connected via
  browser automation) — reproduced the exact failing request, read the actual response body
  (`ERROR: OpenAI request failed (404): `) and the stored `base_url` from `GET /providers`,
  confirmed the root cause by reading `OpenAiCompatibleClient`/`ModelRouter` against that data.
  The fix itself has **not yet been re-verified live** — no `dotnet` SDK available in the
  environment this was authored in. Brace/paren balance checked manually. Once deployed
  (`git pull && bash deploy/lxc/setup.sh` on the LXC box, or a new tagged release), re-run Test
  Connection on OpenAI and confirm it now succeeds.

## v1.8.7 — LXC deployment

No schema change. Second step of the container/LXC/Windows-Service deployment push (see
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)) — a one-shot installer for a fresh Debian/Ubuntu-family
LXC container, Proxmox or otherwise.

Added:

- **`deploy/lxc/setup.sh`** — unattended installer/upgrader for a fresh Debian 12+/Ubuntu 22.04+
  LXC container. Installs the .NET 9 SDK if missing (Microsoft's apt repo, resolved dynamically
  from `/etc/os-release` rather than hardcoded to one distro/version, with a `dotnet-install.sh`
  fallback for distros/versions Microsoft's repo doesn't have an entry for), clones/updates the
  repo, publishes a self-contained `linux-x64` binary, creates a dedicated unprivileged system
  user, installs + enables the systemd unit, and starts it. Idempotent — re-running it is the
  upgrade path (pulls latest, republishes, restarts). Configurable via `ANTHILL_REPO_URL`,
  `ANTHILL_INSTALL_DIR`, `ANTHILL_SERVICE_USER` env vars.
- **`deploy/lxc/anthill.service.template`** — the systemd unit `setup.sh` installs, with the same
  hardening as the manual systemd install already documented in the README (`NoNewPrivileges`,
  `PrivateTmp`, `ProtectSystem=strict`, scoped `ReadWritePaths`), plus `Environment=ANTHILL_HOME`
  for unambiguous workspace resolution and a generated `/etc/anthill/token.env` for an optional
  static API token.
- No special LXC features 