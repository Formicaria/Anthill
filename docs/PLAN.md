# ANTHILL — THE PLAN

**The single forward document.** What is done, what is left, and the order to do it in.
`AUTONOMY-10.md` folded into this file; role mechanics live in
[`ANT_EXECUTION.md`](ANT_EXECUTION.md); the qualification protocol lives in
[`QUALIFICATION.md`](QUALIFICATION.md).

Shipping release: **v0.3.8.96**.

---

## 1. Where the colony measurably is

**Structurally complete, deterministically qualified across its declared scenarios, never run
against a real model under protocol, and carrying contract drift that makes a live run
unattributable.** That last clause is what reorders everything below.

Done and load-bearing:

- Twelve roles, contracted, gated, each with a real production trigger.
- Patch integrity: `add` means create, destructive applies require a base hash, a patch set applies
  as a unit or not at all, and one decision function (`PatchApply.Compute`) answers for every applier.
- The typed artifact channel: declared task inputs, a consumption ledger recording what each role
  actually read and at which hash, schema validation at both boundaries, provenance carrying the
  provider and model that served each call.
- Structural enforcement: a UI change cannot dispatch without a valid `ui_map`; verification is
  policy-inserted and fails closed; the repair bound reads typed signatures; the scribe cannot
  certify unverified work; `MissionReconstruction` replays a mission from artifact IDs.
- Every operator message is a mission — no chat lane, no `conversation` route, no unconfined agent
  access. The colony dispatches a coding agent as a TOOL inside a mission that plans, reviews, tests
  and verifies its work (v0.3.8.58).
- A goal reaches applied bytes on the operator's tree, through all nine gates (v0.3.8.75).
- The security review of §5 is closed: S1–S7, with residuals recorded there.

### Counted honestly

| | |
|---|---|
| Deterministic qualification scenarios | **20 of 20 closed by substance** *(v0.3.8.79)*. None open, none partial |
| Acceptance gates | **12 of 12** *(v0.3.8.80)*. Gates 1 and 2 closed with R3's cancellation matrix, which is what made the roster's declarations true enough to grade |
| Cancellation cells | **48 of 48 decided; 33 driven live, 0 cited, 15 not-applicable** *(v0.3.8.88 — R3's exit gate MET)*. Two cells moved from not-applicable to DRIVEN by looking at the role's own trigger instead of the planner's: the archivist's at v0.3.8.85 (nothing had ever stopped it at finalization) and the medic's at v0.3.8.88 (from the tester→medic handoff). The 15 that remain are points the runtime cannot produce, each with the reason recorded |
| Graduation record | **Complete** *(v0.3.8.81)*. The cancellation column and the `ui_cartographer` fault cell were the last nulls |
| Live qualification | **Never run under protocol.** One live mission happened at v0.3.8.73 and produced three real defects; that is not the recorded multi-provider run item R4 requires |
| Model-calling roles | **5 roles and 8 routes, all declared, both directions asserted** *(v0.3.8.76)*. Was "7 declared, 5 of which cannot call a model" — and separately, 3 routes that do call one were declared nowhere |
| Structured output | **Asked for on the wire by `coder`, `planner`, `strategist`** *(v0.3.8.76)*. The field existed, was plumbed and was gated since v3.4.0; no producer had ever set it |

**No scenario is now weaker than the ledger says**, which is a sentence this document has not
been able to write before. Scenario 5's note used to admit "the composed UI-patch lifecycle is not
[proved]" while the entry was not labelled partial, so the guard built for exactly that could not see
it; scenario 17 was labelled honestly and stayed labelled for eleven releases. Both closed on
substance rather than on wording — 5 at v0.3.8.78, 17 at v0.3.8.79.

---

## 2. The release plan

Ordered by dependency, not by size. Each entry names its **exit gate** — the thing that must be true
before the next begins — because version numbers have proved a poor unit of prediction here: the last
twelve releases closed roughly two plan items, and every one of them overran because reaching an
outcome nothing had needed before uncovered a defect that had been sitting there for releases.

**Estimable horizon: R1–R4, about 8–15 releases.** Beyond that, treat the gates as the plan and the
numbers as placeholders. A realistic total for everything below is **40–70 releases** if the current
one-patch-version-per-discovered-defect rhythm holds.

---

### R1 — Contract and adapter truth · *1–3 releases* · **in progress**

Nothing downstream can be attributed until the colony's declarations match its runtime. A live
failure today cannot be pinned on the model, the adapter, or a contract that was never true.

- ✅ **Five deterministic roles declared `AllowsModelCalls: true`** *(v0.3.8.76 — contracts state what
  their ants can do; `ContractDeclarationTests` asserts a declaration belongs to a role that can act
  on it, and that a contract agrees with itself about `model.invoke`)*.
- ✅ **Three routes called models with no declaration at all** *(v0.3.8.76 — found while fixing the
  above, and the more expensive half. `planner`, `strategist` and the answer-synthesis `scribe` route
  were never graded, because the fitness report enumerated contracts and they have none.
  `ModelRouteRequirements` now declares all eight routes that reach the router, in both directions)*.
- ✅ **`ResponseSchemaJson` was declared and reaching nobody** *(v0.3.8.76 — `GenerateTyped` takes a
  schema; `coder`, `planner` and `strategist` send one. Each schema written against the parser rather
  than the prompt, which caught three disagreements that would each have been an outage)*.
- ✅ **Verifier's structured-output requirement** *(v0.3.8.76 — checked, and it described a use of the
  model that v3.8.22 ended. The verdict is deterministic and the model's reading never promotes;
  removed, and the context requirement that is real kept)*.
- ✅ **A guard for tick-versus-body disagreement** *(v0.3.8.76 — `ChecklistIntegrityTests`)*.
- ✅ **Provider-adapter conformance suite** *(v0.3.8.77 — `AdapterConformanceTests`: four adapters ×
  eight capabilities, thirty-two cells, each either citing the test that proves it or naming why the
  transport cannot. Citations are checked to resolve, and a fifth provider added to the module fails
  the matrix rather than passing by being unknown to it.)*
  **It found a live defect on its first pass:** `ModelCapabilityCatalog` declares `anthropic` as
  `Standard` — structured output included — so `Negotiate` kept a response schema for it, and
  `AnthropicBody` never read the field. Fixed by binding the schema the only way Anthropic serves
  one, as a forced tool call, with `ReadAnthropic` unwrapping the reply back into content.
- ◻ **Ollama capability discovery.** Reads `/api/tags`, which `ApiHost.Providers.cs:112` documents as
  a deliberate choice — Ollama publishes a per-model `capabilities` array there, and against three
  real local models the hand-written table was wrong twice. `/api/show` may still be richer. **Treat
  as a contested design decision to extend, not an oversight to fix.** Carried to R4, where a live
  multi-provider run is what would settle it; it is not a gate on anything before that.

> **Exit gate — ✅ CLOSED at v0.3.8.77.** Every role's declared capabilities match what it can do,
> asserted by a test *(v0.3.8.76)*. Every adapter passes the conformance suite or is explicitly
> marked unsupported for a named capability *(v0.3.8.77)*.

---

### R2 — Finish deterministic qualification · ✅ **CLOSED at v0.3.8.79**

The two scenarios the ledger overstated, and the two defects closing them uncovered.

- ✅ **Scenario 5 — the composed UI-patch lifecycle** *(v0.3.8.78 — `ComposedUiPatchLifecycleTests`.
  The gate and the producer were each proved and the JOIN was not: a map with the right shape and the
  wrong mission, or a gate reading a store the cartographer never wrote to, satisfies both ends while
  the middle is broken. The composed run drives goal → cartographer reading REAL UI files → gate
  admits → coder → tester and soldier → verification → applied bytes. The map is not scripted,
  because `UiCartographerAnt` holds no router to script — if it reads nothing, the gate refuses and
  the test fails, which is the coupling the scenario claims.)*
- ✅ **The build verifier reads `CheckSource`** *(v0.3.8.78 — surfaced by the above and blocking it.
  `RunAllowlistedCheckTool` has resolved ids through `CheckSource` since v0.3.8.73; `BuildVerifier`
  still asked for the literal `dotnet_build`, so a code patch in any non-.NET workspace ran
  `dotnet build` against a directory with no project and could never be verified. Where an operator
  declares checks, those checks are now the build — all of them, any failure fails, the result stays
  deterministic, an empty selection fails closed, and the no-declaration fallback is unchanged.)*
- ✅ **Scenario 17 — process death mid-apply** *(v0.3.8.79 — `ProcessDeathMidApplyTests` and the
  `Anthill.CrashHelper` executable. Recovery was proved by ABANDONING a transaction object, which is
  still a healthy process with flushed buffers and run finally blocks; a killed one has neither. The
  test starts a real process, waits for it to signal that the journal and patched bytes are durable,
  `Kill()`s it, and recovers in the parent. The sentinel is what makes it deterministic — killing on
  start would sometimes find no journal, and "recovered cleanly from nothing" is a pass that means
  nothing happened.)*

- ✅ **`RunShell` mangled double quotes on Windows** *(found at v0.3.8.78, fixed at v0.3.8.79.)*
  `AutoApplyRunner.RunShell` passes the whole command through `ProcessStartInfo.ArgumentList.Add`,
  which escapes by C-RUNTIME rules — an inner `"` becomes `\"` — and `cmd.exe` does not follow those
  rules. A verify command written as `findstr /C:"aria-label" file` reaches findstr as
  `/C:\"aria-label\"`, matches nothing, exits 1, and a correctly applied patch is rolled back with
  "Verify FAILED" against a tree where the change is present. **Two instances:**
  `autonomy_autoapply_verify_cmd`, and the auto-commit
  `git -c user.name="ANTHILL Auto-Apply" … -m "{msg}"` at `AutoApplyRunner.cs:639`, which no test
  exercised. Every quoted verify command configured in the field was affected.
  **Fixed** by handing Windows the raw string (`psi.Arguments`) so cmd applies its OWN rules, while
  Unix keeps `ArgumentList` — there is no command-line re-parsing on that side, so a string would
  introduce the very re-quoting this removes. `ShellQuotingTests` proves a quoted phrase survives,
  that an absent phrase still fails, and that `&&` still composes.
  **The sweep for the same class found a THIRD instance**, and the worst-placed of the three:
  `OperatorShell.Execute`, the dashboard's shell box, where an admin typing
  `git commit -m "fix the thing"` had it delivered as `-m \"fix` plus two stray arguments — a human
  typing a correct command and watching it come back wrong, with nothing in the output explaining
  why. `ShellSpawnTests` now pins the rule repo-wide in both directions: no `/c` through
  `ArgumentList`, and sites invoking a real program (`git`, `docker`, an agent binary, a declared
  check) keep the list, because over-applying the fix is the same defect from the other side.

> **And the guard that predicted its own expiry.** `PartialCoverage_IsDeclaredRatherThanImplied`
> asserted `NotEmpty(partial)`, which would have failed for the single outcome the ledger exists to
> reach. v0.3.8.78 recorded that here and left it standing, because it was still true then; v0.3.8.79
> removed the assertion in the release that made it false. Same correction v0.3.8.74 made to its
> sibling — *a guard that cannot express success is not a guard, it is a deadline* — and the second
> time this file has needed it.

> **Exit gate — ✅ CLOSED at v0.3.8.79.** 20 of 20 closed by substance, with no note admitting an
> unproved claim.

---

### R3 — Per-role cancellation and timeout · ✅ **CLOSED at v0.3.8.88**

The largest single item remaining, and the other large area where nothing had ever asserted the
outcome.

- ✅ **The cancellation matrix and its harness** *(v0.3.8.80 — `RoleCancellationTests`. Twelve roles ×
  four points = 48 cells, every one decided: 24 driven live by the harness, 11 cited to the tests
  that already prove them, 13 marked not-applicable — and the not-applicable claims are CHECKED
  against the contracts, so a role that acquires a tool or a router stops being exempt and the suite
  says so.)*
  **What it closed that nothing had:** every prior cancellation test was about a MECHANISM — the
  ambient model-call scope, the process tree, a hanging subprocess. None was about a ROLE. "Does
  cancelling stop the archivist without writing a lesson to durable memory" had no answer anywhere,
  and the damage differs per role: a cancelled tester leaves a process, a cancelled archivist leaves
  a memory, a cancelled coder leaves a patch set.
- ✅ **Acceptance gates 1 and 2** *(v0.3.8.80 — `AcceptanceGatesOneAndTwoTests`. Gate 1 asks for
  READY, which is stronger than the gate-only check that existed: `Ready` is a conjunction of five
  conditions and "no gate blocks it" reads exactly like "it is ready" in a summary. Gate 2 reads its
  four clauses from four different sources on purpose, because the gate is about them agreeing.)*
- ✅ **`during_generation` and `during_tool_call` driven live** for six of the eleven applicable
  cells *(v0.3.8.81 — researcher, web and coder mid-generation; file, researcher and web
  mid-tool-call)*.
  **It found the defect this item predicted, and a second one behind it.** Every model-calling role
  reads a non-Ok call as "the routed model is unavailable" and DEGRADES — which is right for the case
  it was written for. A cancelled call is non-Ok, so cancellation came through that same door: the
  researcher and the builder returned `SucceededWithWarnings`, the task COMPLETED, and a completed
  task ingests handoffs, inserts a verification task after a deliverable, hands the archivist
  something to remember and processes the coder's proposals. The operator pressed stop and the colony
  answered with a fabricated fallback deliverable and more scheduled work. `DrainRunningTasks` has
  recorded this state since v2.26.0 — for tasks still RUNNING at the grace deadline; one that
  finished INSIDE it by degrading was never its business, so **the faster a role gave up, the more
  likely its cancelled work was recorded as a completion.** Fixed once in `ExecutionService`, where
  what a stopped mission may record is decided, rather than at the eight ant call sites, where how to
  handle a bad model call is decided.
- ✅ **A cancelled call is no longer reputation** *(v0.3.8.81 — the second defect, and the one that
  outlived its mission)*. `ModelRouter.SendCore` held two implementations of one rule four lines
  apart: the breaker read `Cancelled` as Neutral — "we stopped the call ourselves" — while the
  pheromone trail derived everything from `Ok` and wrote a FAILURE against
  `model:{provider}:{model}:{role}`. The transient copy was right and the durable copy was wrong, so
  every operator stop taught the colony a little more firmly that the model its cancelled role was
  using is unsuited to that role. **This is a wrong memory that R8's exit gate could not have traced,
  because the mission that wrote it looked fine.** One authority now, `IsColonyStopped`, asked by
  both readers.
- ✅ **The graduation record's cancellation column, and the `ui_cartographer` fault cell**
  *(v0.3.8.81 — the last nulls in the record. `UiCartographerFaultTests` covers the branch nobody
  exercised: a failed listing TOOL, as against the empty WORKSPACE the unit tests already drive. A
  broken tool producing an empty map would be admitted by `UiChangeGate`, which asks whether a usable
  map exists rather than whether the task succeeded.)*
  Both gap-asserting tests were **rewritten rather than relaxed**, on their own instructions — the
  third time this file has recorded that correction after v0.3.8.74 and v0.3.8.79. *A guard that
  cannot express success is not a guard, it is a deadline.*
- ✅ **The harness ran the plans it wrote** *(v0.3.8.82 — and this is the release, because until it
  none of the above was about the roles it named)*. `Planner.TasksFromJson` rejects any plan with
  fewer than `MinDynamicTasks` (3) usable tasks and the mission silently runs `FallbackTasks`
  instead — a static researcher/file/coder/builder/verifier graph. **Every plan this harness ever
  scripted was below that minimum**: one task in the live fixture, two in the pre-dispatch one. So
  each cell passed because a fallback branch happened to contain the role the assertion was looking
  for, which is this repository's oldest defect shape pointed at its own test fixtures.
  `AssertTheMissionRanTheScriptedPlan` now compares the planned roles against the scripted ones on
  every cell, and `ScriptedPlan` builds a three-task graph with the role under test first.
- ✅ **`during_generation` and `during_tool_call` driven live, for real this time** *(v0.3.8.82 —
  researcher, web, coder and builder mid-generation; file, researcher, web, ui_cartographer and
  scribe mid-tool-call)*. The three cells v0.3.8.81 recorded as "attempted and did not reach the
  point" all had the same cause, and none of them was what that release guessed at: the builder was
  never planned, the scribe was never planned, and the cartographer's gate was tripped by the
  FALLBACK plan's researcher dispatching a tool inside the cartographer's grant. The v0.3.8.81 note
  worrying about "a dispatch outside per-role authorization attribution" described a fixture
  artefact, and is withdrawn.
- ✅ **The medic and the archivist cannot be driven by planning a task for them** *(reasoned at
  v0.3.8.82, LANDED at v0.3.8.83 — that release documented the reclassification and shipped a matrix
  that still drove all twelve, because the edit was lost and nothing compares a stated count against
  the matrix that produces it. A
  contract fact, found only once the plans became real)*. `AntRegistry.ValidateTask` refuses a
  planner-produced task for a `FailureTriggered` or `PostFinalization` role, so their
  `before_dispatch` and `awaiting_dependency` cells are now NOT-APPLICABLE with that reason, checked
  against the contract rather than asserted. They looked driven for two releases for the same reason
  everything else did.
- ✅ **The sweep for the same defect elsewhere in the suite** *(v0.3.8.83 — and it found a second
  instance)*. `ScriptedPlanConformanceTests` reads every `.Role("planner", …)` in the test suite and
  requires each scripted plan to be one the Planner would ACCEPT: at least `MinDynamicTasks` tasks,
  and only planner-eligible roles. `EarnedRepairLifecycleTests` scripted two — researcher and coder —
  for a goal containing both "document" and "add", which selects the fallback's CODE branch. That
  branch has a coder, so the patch → failing check → medic → repair → passing check loop the whole
  scenario asserts still happened, and **qualification scenario 15's last edge was being proved
  about a plan nobody wrote.** A plan may satisfy the guard statically, or its fixture may verify at
  runtime the way `RoleCancellationTests` now does; the guard also asserts it found at least five
  plans, because a sweep that silently stops sweeping is the failure it exists to prevent.

- ✅ **The last two cited cells, driven** *(v0.3.8.84 — and the citation was wrong, not merely
  unfinished)*. Both `verifier/during_generation` and `tester/during_tool_call` were recorded as
  unreachable because `SchedulingMode.PolicyInserted` meant "no plan may assign it".
  `AntRegistry.ValidateTask` refuses only `FailureTriggered` and `PostFinalization` from planner
  output, and v0.3.8.51 narrowed it to those two on a field report: *a planned tester or soldier step
  is a plan asking for MORE safety, not less; PolicyInserted is a floor, not a ceiling.* **The
  soldier — also PolicyInserted — was being driven by this same harness at both universal points the
  whole time**, which is the contradiction that should have been visible from inside the file. A
  declaration disagreeing with the runtime, written into the matrix whose job is catching those.
  The tester's cell is SPLIT rather than claimed whole: the harness proves what the role leaves
  behind, and the orphan-process half stays cited to the two tests that prove it, in the same cell,
  where all three citations are checked to resolve.
- ✅ **`archivist/before_dispatch`, and the defect it was hiding** *(v0.3.8.85)*. The cell was
  recorded not-applicable because `AntRegistry.ValidateTask` refuses a planned archivist — true of
  the PLANNER and false of the role. `Queen.RunArchivistAfterFinalization` is its real dispatch
  site, invoked directly once the canonical evaluation is persisted, and "cancel before this role
  acts" is answerable there. **The answer was that nothing stopped it**: that path does not go
  through `ExecutionService.RunSingleTask`, so it could not inherit v0.3.8.81's stop check and had
  none of its own — a cancelled mission still ran the archivist over its partial work and ingested
  the candidates it proposed. Fixed by reading the persisted outcome, the authority this method's
  own documentation already insists on, and skipping with the existing `archivist_skipped` event
  under a distinct reason.
  **And it shows how this harness can pass for the wrong reason even now.** The no-positive-memory
  property watches `memory_candidate_archived`, and a stopped mission usually gives the archivist
  nothing worth proposing — so the assertion passed because the archivist found nothing, not because
  it was prevented from looking.
- ✅ **`medic/before_dispatch` — DRIVEN LIVE** *(v0.3.8.88)*. The last gap, closed from the medic's
  real trigger rather than excused. v0.3.8.83 had already written down what it would take — "a
  critical task that fails under adaptive mission control" — and the fixture existed one file over:
  `CodePatchLifecycleTests` drives a patch mission whose policy-inserted tester runs a check against
  the materialized revision, fails, and hands off to the medic.

  The window is exact rather than approximate. Both admission paths — `IngestHandoffs` and
  `ApplyAdaptiveDecision`'s repair arm — admit the task FIRST and log afterwards with the
  destination role as the event's ant name, so that event means *scheduled, persisted, not yet
  dispatched*. The fixture stops the colony on it, through a synchronous test bus: the production
  `InProcessEventBus` dispatches off the publisher's thread by contract, which would make the
  stopping instant a race and the cell a coin toss.

- ◻ **The two cells that remain are FACTS, not gaps: `medic/awaiting_dependency` and
  `archivist/awaiting_dependency`.** Named individually because `RoleQualificationRecordTests`
  asserts this document keeps naming whatever is not driven. Neither can be produced by the runtime,
  and each says why in the matrix.
  - `medic/awaiting_dependency` — **not-applicable on a runtime fact, since v0.3.8.87.** Its old
    reason was "a role the planner may not assign has no planned task that can sit waiting", which
    is true, and answers a question adjacent to the one the cell asks: the medic DOES get a task,
    from two runtime paths. Neither gives it a dependency, and neither can. Its parent is a task
    that has already FAILED, so an edge onto that parent would never be satisfiable and the role
    would deadlock rather than wait — which is why `ApplyAdaptiveDecision`'s repair arm sets
    `ParentTaskIds` and leaves `DependsOn` empty while the delta-plan arm four lines below sets
    both. `HandoffGate` likewise constructs its task with no dependency.
    `RoleCancellationTests.ANoFailureTriggeredRole_IsEverGivenADependency` holds the creation sites
    to that, so the claim now fails when the code changes rather than when someone rereads it.
  - `archivist/awaiting_dependency` — **the strongest not-applicable in the matrix.** The role is
    never SCHEDULED at all; the Queen invokes it directly after finalization, so there is no queue
    entry for a dependency to hold up, now or ever. It does not depend on how a task happens to be
    constructed.

> **Exit gate — ✅ MET at v0.3.8.88.** All forty-eight cancellation cells decided; every driven cell
> proved to have run the plan its fixture wrote; **33 driven live, 0 cited, 15 not-applicable**. The
> medic's and archivist's universal cells were the gate's real substance and none of them was
> excused: `archivist/before_dispatch` was driven at v0.3.8.85 by looking at the role's own dispatch
> site instead of the planner's, `medic/before_dispatch` at v0.3.8.88 from the tester→medic handoff,
> and the two `awaiting_dependency` cells are recorded as facts the runtime cannot produce — with the
> medic's reason rewritten at v0.3.8.87 from a claim about the planner to a proof about the
> scheduler, pinned by a source guard. **The graduation record — ✅ complete at v0.3.8.81. Acceptance
> gates 1 and 2 — ✅ closed at v0.3.8.80.**

---

### R0 — Correctness before capability · *4–6 releases* · **in progress, gates everything**

Inserted at v0.3.8.91 after an external repository review, and placed FIRST because its findings are
not stylistic: several documents promise guarantees the runtime does not enforce, and two of them
were reachable. Every claim was verified against the code before being accepted; two were revised
(one worse than reported, one narrower). The reviewer's frame is the right one for this whole group:
*the foundations are sound, and the work is deleting the alternate paths around them.*

**Nothing below R0 proceeds until it closes.** No new ant, no memory feature, no broader autonomy, no
connectors. R4's live runs wait too — a qualification report is only as true as the instrumentation
under it.

- ✅ **The window before the first administrator** *(v0.3.8.91)*. `/auth/setup` was unauthenticated
  while `CountUsers() == 0`, on a listener every profile forces to `0.0.0.0`, with
  `operator_shell_enabled` shipping true — reach the port, win the race, get a host shell. Closed by
  `SetupAuthority` (single-use bootstrap token, rule written on the BIND not the caller's address),
  a transactional `CreateInitialAdministrator`, and the operator terminal shipping off. The
  `DEPLOYMENT.md` paragraph that argued the bind was safe is corrected rather than deleted.
- ✅ **A verification fault fails closed** *(v0.3.8.91)*. The catch left `DeterministicBlock` null and
  `ApplyUnderBypass` gates on exactly that, so a crashed verifier let an unverified patch reach the
  operator's tree under a Bypass conversation.
- ✅ **No control decision is read out of prose** *(v0.3.8.91)*. A REJECTED patch satisfied
  `Contains("applied") && !Contains("not applied")`, returned HTTP 200 and fired a real git commit.
- ✅ **One promotion gate** *(v0.3.8.91)*. `PatchPromotionGate` is the single authority and the Apply
  button and bypass lane consult it. The actor changes exactly ONE condition — who satisfies the
  human; a test pins every other condition above the actor switch so no lane can be silently
  exempted. Found while building it: `Task.DeterministicBlock` had no database column, so the block
  that gates bypass application has never survived a restart. **Auto-apply still runs its own nine
  checks** — stricter than the gate on every axis, and folding it in is left as a named follow-up
  rather than done blind.
- ✅ **Patch sets apply as a unit on every path** *(v0.3.8.91)*. `PatchSetApply` preflights every
  target, journals before the first mutation, stages each file's pre-state, and rolls the whole set
  back on any failure. The bypass lane's `foreach (proposal) => apply` — which continued past a
  failure — is gone. `AutoApplyRunner`'s duplicate preflight now delegates to the one in Core.
- ✅ **The live tree must match the verified tree** *(v0.3.8.91)*. `WorkspaceFingerprint` — HEAD plus
  the full `git status --porcelain -uall` listing — captured when the sandbox is built and compared
  by the gate before any lane writes. HEAD alone was the first design and would have been wrong: it
  does not move on an uncommitted edit, which is the case the check exists for. Three states, and
  `NotCaptured` (non-git, or a set predating this) is deliberately not a refusal.
- ✅ **File mutation and database state, one recoverable transaction** *(v0.3.8.91)*. An apply intent
  journal — Prepared → Mutating → Applied → Recorded, on both lanes, written before the write — plus
  startup reconciliation that decides from hashes: prepared discards, applied completes the records,
  and a mid-write state matching neither hash is left for an operator rather than resolved in favour
  of success. It never re-applies and never rolls back.
  ✅ **The crash-injection matrix** *(v0.3.8.94)* — `Anthill.CrashHelper` gained an intent-journal
  mode that drives the live apply sequence to a chosen phase and blocks; `PatchApplyCrashMatrixTests`
  really kills it at each window (prepared / mutating-unwritten / mutating-written / applied) and
  reconciles in the parent process: discard, discard-by-hash, needs-operator-with-intent-open, and
  the database catching up with the disk — one deterministic recovered state per row.
- ✅ **A refused lease prevents execution** *(v0.3.8.91)*. The claim is taken before anything is
  committed and a refusal returns. Committing first was what forced the old code to ignore the
  refusal; there is now nothing to strand. A claim the scheduler then declines is released as
  `Abandoned` rather than held.
- ✅ **The configuration surface agrees with the runtime** *(v0.3.8.91)*. The `api_token_env`
  self-referential fallback (sticky, and kept using the variable the operator had abandoned); four
  `??` env overrides an empty string won; `ANTHILL_PORT` unclamped and silently falling back; an
  unreadable config running on defaults that bind `0.0.0.0`; `config.example.json` showing seven
  roster flags as `false` that the migration forces `true`, with the two real controls undocumented;
  and `LastConfigMigration`, which its own doc comment claimed two endpoints surfaced and neither did.
  `ConfigurationSurfaceTests` pins both directions against an explicit undocumented-on-purpose ledger.
  ◻ **The GENERATED schema is not built** — one declaration per key carrying type, default, env
  override, range, security class, aliases and UI exposure, with the example file and the docs
  generated from it. That is the end state; this release stopped the drift getting worse.
- ✅ **The evidence and artifact vocabulary mismatches** *(v0.3.8.94)*. `AntEvidenceKinds` is the
  closed vocabulary for what an ant reports (disjoint from the store's `EvidenceKinds`, on purpose);
  the kind-"tool" filter both `FailureContext.Tool` and `TaskResult.Tool` waited on for six releases
  finally has a producer (the registry records dispatched tool names; the measurement boundary turns
  them into evidence); `deterministic_work_completed` stopped consulting the wrong witness's
  vocabulary; `patch_json` and `text` are DECLARED transport-only at the artifact bridge instead of
  falling into the null arm beside typos; and `VerificationPolicyReachabilityTests` is the dormant-
  keys ledger (`code_patch_full`, `config_change`, `artifact_production` — real policies, recorded
  as dormant, failing the ledger when either fact changes).
- ✅ **Auto-apply consults the one gate** *(v0.3.8.94)*. The Director evaluates every eligible
  proposal as `PromotionActor.Automation` — the actor v0.3.8.91 declared and nothing used — and its
  private copies of the evaluation / write-gates / rollback-marker checks are deleted. The fold
  STRENGTHENS the lane (deterministic block, incomplete or blocking policy review, moved workspace —
  conditions the runner never checked); what stays runner-side is exactly the set-level part a
  per-proposal gate cannot own: the evidence content hash, mixed deterministic rows, patch-set
  identity, whole-set preflight, the durable transaction.
- ◻ **Enforcement.** Warnings as errors, analyzers, dependency and secret scanning, a complexity
  budget, module auto-discovery, typed database rows instead of `Dictionary<string, object?>`, and
  the agent rules written down rather than remembered.
  **Including the guard hierarchy**, which v0.3.8.92 paid for: a runtime black-box test first, then
  a typed registry, then compiled/Roslyn inspection, and a source scan last. When a source scan IS
  the right tool it may never depend on a character count — v0.3.8.91 shipped a guard reading a
  4,000-character window whose marker sat 27 characters inside it on Linux and outside it on a CRLF
  checkout, and main went red on a property that had not changed.

> **Exit gate.** Every write to the operator's tree passes one gate; every externally visible
> mutation is recoverable after a crash; no security decision reads prose or falls back to a broader
> source when its authoritative input is missing; and the configuration surface has one authority.

---

### R0-B — One real task, end-to-end, through the production path · *v0.3.8.93*

The corrections brief: make `operator request → plan → routed worker → mission worktree →
patch/evidence pipeline → real answer` true at every joint, not just at most of them. Closed by
substance at v0.3.8.93, with one honest residual:

- ✅ **The role's contract clamps the agent CLI** — `AgentAccessScope` carries `RoleMayWrite`,
  derived from the ant registry at dispatch; a read-only role gets no Edit/Write/Bash flags or
  settings under ANY policy. *Skip All Approvals skips the operator's prompts, not the role's
  contract* — the promotion gate's sentence, now true one layer earlier. Enforced in process argv
  and the materialized settings file, never in prompt prose.
- ✅ **Both coder modes end in one pipeline** — `ProcessPatchSet` is the single consumer; the
  harvested worktree diff now gets verification, a patch artifact, approval cards and the bypass
  gate exactly as a structured-JSON proposal does. The one divergence (review tasks cannot be
  inserted after the task graph closes) is evented, not silent.
- ✅ **The prompts tell the truth** — the operator request travels in an OPERATOR REQUEST fence and
  is the instruction; fetched/prior-model content keeps the UNTRUSTED fence; embedded fence markers
  are defanged in both, so a hostile string in a fetched document cannot close its fence or forge
  the operator's. Worker prompts name REAL dispatchable tools from the authorization table, or say
  "none" — `read_workspace_docs` and its phantom siblings no longer reach a prompt.
- ✅ **Mission size is proportional** — a one-task informational plan is a declared, accepted
  outcome; the three-task minimum and the guaranteed verifier now bind exactly the plans with
  consequential (patch-producing) work. The guard was split, both halves pinned.
- ✅ **One consequential pheromone decision** — verified worker trails break the
  declaration-order tie in worker selection; capability keywords outrank any trail; evented as
  `worker_selected_by_trail`; A/B-replayed in `PheromoneDecisionTests`.
- ✅ **The real result reaches the operator** — already true since v0.3.8.73 (`operator_summary`
  compiled from persisted rows, no model in the path); re-verified rather than rebuilt.
- ◻ **The live CLI gate has still not run.** `CliBoundaryCharacterizationTests` records the exact
  argv/settings per role×policy cell at the pure-function layer, and says in its own header that it
  is NOT the live gate: no vendor process starts in the suite. The live run remains R4's item, and
  nothing in this release claims it.

---

### R4 — Protocol-compliant live qualification · *2–5 releases*

Only meaningful after R1: without adapter conformance a live failure cannot be attributed.

Ollama, an OpenAI-compatible provider, and Claude Code or another agent CLI; ideally one small local
and one strong cloud model. Record provider and model version, tokens, cost, durations, failure
classes, which trigger reached each role, artifacts produced and consumed, and whether
`MissionReconstruction` replays the result.

Budget the run at one release and its findings at one to four. The single unstructured live mission
at v0.3.8.73 produced three real defects, one of which — the operator report having no compiler —
was architectural.

- ✅ **The recorder, built and proved before any live run** *(v0.3.8.89)*.
  `LiveQualificationRecord` assembles the exit gate's telemetry table out of records the colony
  already keeps — provenance for the model that actually served each call, `model_call` events for
  tokens and durations, typed failure classes, the consumption ledger for what each role really read,
  and `MissionReconstruction` for whether the run replays. `LiveQualificationRecordTests` holds it to
  `QUALIFICATION.md`'s table one-to-one and drives it against a scripted mission, so the live run is
  an operator pressing go rather than a live run plus an argument about whether its telemetry was
  complete.
- ✅ **Cost has a producer: the operator** *(v0.3.8.90)*. `ModelPricing` converts the tokens the
  runtime already measures against a `model_pricing` table in `config.json` — `provider/model`, or
  `provider/*` for a whole provider, which is how a local run reports a MEASURED zero instead of an
  unknown. A pure function over a table passed in, not a reader of statics, and deliberately not
  inside the recorder: this plan's own wording for the change.
  **The refusals are the safety property**, and there are three, distinguishable because they are
  three different things to do: no table configured, a provider that reported no usage, and a served
  model the table does not cover. A run is priced only when EVERY model has an entry and EVERY call
  reported usage — a partially priced run is not priced, because a total lower than the run's real
  cost wearing a currency symbol is worse than an absent figure. Nine tests in `ModelPricingTests`,
  most of them about the refusals.
- ◻ **The runs themselves.** Ollama, an OpenAI-compatible provider, and an agent CLI.

> **Exit gate.** A recorded run per provider with complete telemetry, and `QUALIFICATION.md` updated
> from "never happened" to the run's own evidence. The recorder is ✅ at v0.3.8.89 and cost ✅ at
> v0.3.8.90; **the gate is now open on nothing but the runs themselves.**

---

### R5 — Public-QA readiness · *2–4 releases*

Pulled forward from the old plan's tail, because outside testers are a stated goal and a stale
tutorial fails a new user the same way a stale handoff failed a new session.

Windows, Docker and LXC/Linux; fresh install; upgrade and migration; restart recovery; diagnostics
and redaction; tutorial accuracy; the complete `QA-CHECKLIST.md` walked end to end by someone who did
not write it.

> **Exit gate.** A person who has never seen the repository can install, run a mission, and file a
> report using only the shipped documents.

---

### R6 — Execution sandbox · *3–6 releases* · **gate for R9**

The plan still admits a filesystem TOCTOU window, `dotnet` on the shell allowlist as arbitrary
workspace code execution, and incomplete Windows junction testing. Containers or seccomp on Linux and
a job-object or equivalent on Windows.

**This is a gate before unattended coding, not a follow-up to it.** Autonomy on top of an escapable
sandbox enlarges the blast radius without improving the system — §5's own argument, applied to the
one boundary it left open.

> **Exit gate.** A hostile patch cannot escape the workspace on either platform, proved by test.

---

### R7 — Finish the typed channel · *4–7 releases*

- **Authoritative task inputs everywhere.** The scheduler derives inputs from dependencies, required
  schema types, the current revision, artifact generation and policy — never "an artifact exists
  somewhere in this mission".
- **The last of prose as the primary channel.** `Task.Result` is still central and the builder still
  produces free prose. `MissionReport` (v0.3.8.73) was the first half.
- **Complete artifact provenance.** Mostly filling fields that exist.
- **Research citation quality.** Scenario 1 has admitted since v0.3.8.57 that a brief citing its
  sources badly still parses.

> **Exit gate.** No downstream decision reads `Task.Result` prose where a typed artifact exists.

---

### R8 — Graduation, reputation routing, memory · *6–12 releases*

- **The per-role graduation record**, complete — only fillable after R3.
- **Reputation-aware routing.** The first item where behaviour changes based on the colony's own
  history: new failure modes rather than newly-discovered ones.
- **Semantic and procedural memory.** The widest range here. Memory that influences future missions
  is where a wrong answer compounds instead of failing.

> **Exit gate.** A role's history changes what it is asked to do, and a wrong memory can be traced
> to the mission that produced it.

---

### R9 — Sandboxed autonomy and the PR lifecycle · *4–8 releases*

Everything above, pointed at a real repository with a real pull request at the end. Requires R6.

> **Exit gate.** An unattended mission opens a PR a maintainer would accept, and every refusal on the
> way is attributable.

---

### R10 — Connectors, self-improvement, production hardening · *8–15 releases*

**Five programs, not one item.** The old plan's item 14 concealed this:

- a generic connector framework;
- safe self-improvement;
- production qualification — SLOs, alerting, runbooks;
- a thirty-day soak;
- external security review, SBOM and signing, backup/restore and migration drills.

> **Exit gate.** Thirty days unattended with no unexplained incident, and an external review with no
> open P0.

---

### Ongoing — technical cleanup

- ✅ **The event vocabulary is complete and consumed** *(v0.3.8.86 — 67 emitted names added, 2 phantoms
  removed, both directions enforced by `EventVocabularyTests`)*. Publishers still pass literals rather
  than the constants; that conversion is a separate, larger change and the guard makes the drift it
  would prevent impossible to widen in the meantime — a new literal must be declared to pass.
- ✅ **One catalog declares what a role may do** *(v0.3.8.87 — `ToolCatalog` removed,
  `CapabilityDeclarationTests` added)*. `AntExecutionCatalog` and `ToolCatalog` both declared each
  role's capabilities and side effects; only the first was enforced, and the second was read by the
  gate that decides whether a planned task may enter the queue. They disagreed about capabilities for
  four roles, about side effects for two, and about six roles the second did not list at all — which
  is where v0.3.8.76's deleted `model.invoke` lie survived. The pre-execution permission check that
  lived beside it, `ToolCatalog.CanRun`, had no production caller in its whole life; its one caller
  was a test that built both sides itself.
- ◻ **The `Capability` vocabulary is half-unwired, and now says so.** Seven of the fourteen names are
  granted by nothing and required by nobody. `repo.patch.apply` is withheld on purpose and always
  was; the Proxmox, homelab and credential names belong to a module surface that authorizes through
  `ActionExecutor` instead. Each is recorded in `CapabilityGrant.DeliberatelyUngranted` with its
  reason, and a new capability can no longer join the vocabulary and quietly reach nobody. Wiring or
  removing them is R6/R10 work, not cleanup.

- ◻ **The rest of the v0.3.8.90 sweep, with its sites.** Sweeping "a filter that could not match"
  across every vocabulary in the tree found ten instances. Six are closed in v0.3.8.90 (the four
  builder handoffs, the failure-event set, the console's mission notifications, the autonomy dedupe,
  the category switch, and the diagnostics counters). Four are recorded here rather than half-fixed,
  because each needs a decision and not an edit:
  - **`AntEvidence.Kind == "tool"` matches nothing** — `SqliteMemory.TaskResults.cs:127` and
    `ExecutionService.cs:1710`. No ant emits a `tool` citation (the kinds are `check`, `file_path`,
    `policy_rule`, `mission_id`, `failure_id`, `revision`, `workspace`, `failure_signature`), so
    `ArtifactProvenance.Tool` and `FailureContext.Tool` are null in every row ever written — reading
    as "no tool was involved" rather than "unknown". The decision is which side is wrong: producers
    should cite the tool, or the field should go.
  - **`ArtifactSchemas.ForAntKind` has no arm for `patch_json`** — `Evidence.cs:248`. The coder emits
    `patch_json`; the switch has an arm for `patch_set`, which nothing produces, and six other dead
    arms. The coder's artifact therefore maps to null and `BridgeArtifactsToStore` skips it. It is
    NOT simply a rename: `ExecutionService.cs:951` already `Put`s the patch set directly, so adding
    the arm may double-store. That has to be understood before it is touched. Related:
    `AntExecution.cs:358` declares the coder's `ProducedArtifactTypes` as `patch_set` — a name the
    coder does not produce.
  - **`EvidenceKinds.Reproducible.Contains(e.Kind)`** — `ExecutionService.cs:1479` — tests ADR-004
    verdict kinds against citation kinds. The disjunct is dead; only `e.Kind == "check"` ever fires.
    The file one directory over states the distinction being violated: an `AntEvidence` is a
    CITATION, ADR-004 evidence is a VERDICT.
  - **`VerificationPolicy` keys no task type can reach** — `Verification.cs:79-82, 110-111`:
    `code_patch_full`, `config_change`, `artifact_production`, and the aliases `docs_update` and
    `documentation`. Lower confidence than the others (they are table keys, and the type is public),
    but `DeterministicBlockTests` asserts against one of them directly — the same shape the file's
    own v3.8.21 note says caused a defect.
- ◻ **The emitter detector still has a blind spot, and it is wider than its comment admits.**
  v0.3.8.86's sweep reads event names handed to `LogEvent` as a literal first argument. A name passed
  through a wrapper is invisible — v0.3.8.89 named that — and so is one built by a ternary
  (`Queen.cs:1074` emits all three mission terminals that way) or one whose first argument contains a
  call, which the `[^,()]+` in the pattern rejects. v0.3.8.90 declared six of the affected names by
  hand because two consumers needed to reference them; roughly a dozen remain emitted and undeclared.
  Widening the detector is the fix, and it is a sweep of its own rather than a line in another
  release.
- ◻ **The operator's configuration surface disagrees with itself.** A second sweep, over config
  rather than vocabularies, found: 25 parsed keys `config.example.json` never documents — including
  `roster_profile` and `disabled_roles`, which are the ONLY working off-switches for the seven
  specialist ants the example file shows as `false` and the roster profile then forces to `true`;
  `config_version`, `logs_dir` and `exports_dir`, which are documented and read by nothing;
  `AnthillRuntime.LastConfigMigration`, whose own doc comment says two endpoints surface it and
  neither does; nine `RuntimeOptions` fields nobody reads, against that file's own stated rule; an
  `api_token_env` whose fallback is the static's prior value rather than a config value, so
  redirecting it to an unset variable silently keeps authenticating against `ANTHILL_API_TOKEN`; and
  three env overrides that use `??`, so an empty string set in a compose file wins over the file.
  **This is a release, not a cleanup line** — the token precedence is security-adjacent and the
  roster one is a safety claim the file gets wrong. v0.3.8.90 fixed only the piece it touched
  (`ResetConfig` silently discarding the operator's priority route, now also the price table).

**Not a tail.** Fully async execution, ~166 runtime statics, VRAM scheduling, multi-platform QA,
event-loss accounting and deployment verification are independent workstreams. The statics in
particular have caused three defects in this session alone (a leaked `UseOllama`, two roster-gate
leaks), and they get harder to remove as more code depends on them. Take slices opportunistically;
some belong before R9 rather than after.

---

## 3. Acceptance gates

Non-negotiable. The colony is not a twelve-role colony until all of these pass. **12 of 12** *(v0.3.8.80)*.

1. ✅ All twelve roles report Ready under the full profile *(v0.3.8.80)*
2. ✅ Every enabled role has a handler, contract, real production trigger and typed output *(v0.3.8.80)*
3. ✅ A compile-breaking proposed change fails when built in the patched mission workspace *(v3.8.23)*
4. ✅ That failed patch cannot become `completed_verified` *(v3.8.22)*
5. ✅ A Soldier block cannot be overridden by model text *(v3.8.22)*
6. ✅ Tester failure triggers exactly one bounded Medic repair and a mandatory retest *(v0.3.8.57)*
7. ✅ A UI change cannot reach Coder without a valid `ui_map` *(v0.3.8.57)*
8. ✅ Scribe and Archivist cannot act positively on unverified work *(v0.3.8.57)*
9. ✅ Archivist runs only after the persisted canonical evaluation exists *(v3.8.26 / v0.3.8.41, pinned at v0.3.8.57)*
10. ✅ Replaying artifact IDs reconstructs every role's inputs and evidence *(v0.3.8.57)*
11. ✅ No mission ant can dispatch shell, direct file-write, or primary-workspace patch tools
12. ✅ Disabled or unavailable roles never receive negative reputation for not running *(v3.8.26)*

---

## 4. What "done" would mean

A colony that plans, changes code, verifies with evidence, refuses on reproducible grounds, applies
what it has proved, remembers what it learned, and can be handed to someone who did not build it —
running unattended without an unexplained incident, on a sandbox a hostile patch cannot leave.

Every clause above is a gate in §2. None is a matter of opinion.

---

## 5. The security review — CLOSED, kept as the record

**S1–S7 all closed (v0.3.8.59–.65).** This section is history now, not a queue, and it sits below
the forward plan for that reason. It is kept in full because the findings and the sweeps they
prompted are the clearest worked examples of this repository's defect classes — the review named
two confinement sites and the sweep found six — and because the RESIDUALS recorded in each
subsection are real and feed R1 and R6 above.

An external source-level review found **four P0 and two P1 defects**. They took priority over
everything else, because they were about existing autonomy being trustworthy rather than about the
colony doing more. Shipping more autonomy on top of a broken confinement boundary enlarges the blast
radius; it does not improve the system.

> The biggest benefit is not additional autonomy — it is making existing autonomy trustworthy: no
> workspace escapes, no silent secret disclosure, no partial trees described as rolled back, and no
> database failure turning into permission to write.

### What was reviewed

| | |
|---|---|
| Release reviewed | **v0.3.8.57**, commit `c62a27a` |
| Main at review time | `527b4a7` — its only post-release changes are the PR #11 documentation reconciliation, so the runtime code is identical |
| CI | green on both the release and current main |
| Issue tracking | **none** — no open GitHub issues track any of these findings |
| Method | source-level review; the reviewing environment had no .NET SDK, so nothing was executed locally |

The last two rows matter. Green CI is not evidence against these findings — every one of them is a
path the suite does not exercise, and two of them (S2, S5) are places where a test asserts something
adjacent to the claim and passes. And with no issues open, this section is the only record.

### Immediate containment, until the P0s close

```json
{
  "autonomy_autoapply_enabled": false,
  "patch_application_enabled": false,
  "file_writing_enabled": false,
  "file_tools_enabled": false,
  "shell_tool_enabled": false
}
```

Also deny or restrict `/projects/{id}/file` and `/projects/{id}/files` at the proxy or API layer.
The Files-pane endpoints **do not consult the runtime write flags**, and their READ route is
escapable on its own — so the flags above do not contain S1 by themselves.

### The findings

| Priority | Finding | Worst consequence |
|---|---|---|
| **P0** | Files-pane and workspace confinement can be escaped | Read/write files outside the selected workspace |
| **P0** | Auto-apply is not actually atomic | Partial/truncated tree, with logs claiming rollback succeeded |
| **P0** | Verification and evidence fail open | Unverified patches reach live auto-apply |
| **P0** | Secret artifacts can be sent to models | Credential / private-data disclosure |
| **P1** | UI-map enforcement fails open | UI code dispatched without a trustworthy map |
| **P1** | Some subprocess timeouts are ineffective | Hung worker or director; unbounded output/memory |

### Repair order

This is the reviewer's order and it is **not** the severity order. Confinement comes first because
every later fix is verified by reading and writing files, and evidence comes before transactional
apply because a correct transaction around an unverified patch is a reliable way to ship the wrong
bytes.

1. ✅ **S1** — Files-pane traversal and symlink-safe confinement *(v0.3.8.59 — one resolver, `PathContainment`; TOCTOU remains, see below)*
2. ✅ **S2** — shell tool confinement: disable, or fix *(v0.3.8.59 — arguments contained; `dotnet` residual recorded)*
3. ✅ **S3** — evidence fail-CLOSED *(v0.3.8.61 — verifier: `verification_unavailable`; auto-apply: five refusal arms; see below)*
4. ✅ **S4** — transactional patch application and durable recovery *(v0.3.8.62 — `ApplyTransaction`; see below)*
5. ✅ **S5** — Secret-artifact filtering *(v0.3.8.63 — `IsModelReadable` allowlist; WITHHELD reporting; see below)*
6. ✅ **S6** — UI gate *(v0.3.8.64 — throwing store refuses; `{}` no longer conforms)*; remaining subprocess handling ✅ v0.3.8.59
7. ✅ **S7** — runtime and fault-injection tests before auto-apply is re-enabled *(v0.3.8.65 —
   `ApplyTransactionTests`, `EvidenceFailsClosedTests`, `SubprocessHangTests`; see S8 below. This
   line sat unticked for ten releases while its own body recorded the work as done — documentation
   drift that `DocumentCurrencyTests` cannot see, because it names no version.)*

---

#### S1 — Filesystem confinement (P0) ✅ v0.3.8.59

Broken in two independent ways, either of which is sufficient.

- The Files pane checks `full.StartsWith(root, StringComparison.Ordinal)` with **no separator
  requirement**. A project at `/srv/project` therefore serves `../project-secret/key.txt`, which
  resolves to `/srv/project-secret/key.txt` — a SIBLING whose name merely starts with the root
  string. The vulnerable helper feeds read, create and edit alike:
  `ApiHost.Providers.cs` L855–980.
- `WorkspacePathGuard` uses `Path.GetFullPath`, which removes `..` but does **not** resolve symlinks
  or Windows junctions. A link inside the workspace pointing outside it passes containment and is
  then followed by the file tools and the patch applier: `WorkspacePathGuard.cs` L63–78. Worse,
  `RepositoryIndex.cs` L232–249 CLAIMS symlinks are resolved while the guard does not — a
  declaration that disagrees with the runtime, in the security boundary.
- With `shell_tool_enabled`, confinement is weaker still: `cat`, `find` and `grep` accept
  unrestricted absolute paths, and setting a working directory does not sandbox a process:
  `ShellAndWebTools.cs` L31–56.

**Fixed in v0.3.8.59.** `Anthill.Core.Security.PathContainment` is the one resolver. It requires
exact-root equality or root-plus-separator, and it walks the path from the volume root resolving
EVERY component through its own chain of links, bounded at 40 hops so a cycle is a refusal rather
than a hang. Components that do not exist cannot be links and are appended literally, which is what
lets a file be created at a path whose parent is real. `WorkspacePathGuard.ResolveSafePath` delegates
to it, so all twenty call sites behind it are covered at once.

**The review named two sites; there were six.** The sweep the fix prompted found the identical
missing-separator comparison in `PatchVerifyRunner`, `SandboxWorkspace.Harvest` and
`Verification.Verify`, and a separator-correct but link-blind one in `PatchSetMaterializer`. The
`Verification` copy was the worst: it hashes a required artifact as EVIDENCE, so a link or a
sibling-prefixed path meant a hash recorded as proof of a file inside the workspace could be of a
file outside it. `PathContainmentTests` now carries a detector keyed on the ROOT side of the
comparison rather than the variable name — the first draft keyed on the variable and found only the
two already known, which is what a detector written around the examples in hand always does.

**`RepositoryIndex`'s comment is now true.** It claimed a symlink out of the workspace "resolves
outside the root and is refused here". It never was. Deferring to the guard was the right call for
the right reason; the guard just did not do the thing the comment credited it with.

**STILL OPEN — the TOCTOU race.** This is resolution-time containment. A component swapped for a
link BETWEEN the check and the caller's open is not closed, and cannot be with the APIs .NET exposes
portably — it needs handle-relative, no-follow syscalls (`openat` with `O_NOFOLLOW`). The window is
narrow and requires an attacker already able to write inside the workspace. Recorded here rather
than described in the code as handled.

**Tests.** `PathContainmentTests`: sibling-prefix traversal, absolute and relative link targets,
intermediate-component links, links pointing back inside, a root that is itself a link, link cycles,
non-existent leaves inside and outside, and the guard enforcing the same boundary. The link tests
probe for the privilege to create a link and skip without it — Windows needs Developer Mode or
elevation — so on such a machine the link half is unverified and the sibling half still runs. Linux
CI covers both. Windows junctions are covered by the same `LinkTarget` path .NET uses for symlinks
but are not separately exercised; that gap is real and small.

#### S2 — Shell tool confinement (P0) ✅ v0.3.8.59

Called out separately by the reviewer because it had its own remedy — the tool can be disabled
outright — and because the defect is a category error rather than a bug. `WorkingDirectory` decides
where RELATIVE paths resolve and confines nothing; `cat /etc/passwd`, `grep -r secret /` and
`find / -name '*.key'` ran exactly as written. The nine-command allowlist says WHICH PROGRAM may
run and nothing about what it is pointed at, and it was being asked to do a sandbox's job.

**Fixed in v0.3.8.59.** Every path-like argument resolves through `PathContainment` before a process
starts. An argument counts as a path if it is rooted, contains a separator, or contains `..`;
`--flag=value` is split so a path on the right of the equals is checked rather than skipped. Bare
tokens are left alone, so `grep -r secret .` still searches for the word. The command now runs in
`EffectiveRoot` rather than `Root` — inside a mission the workspace is a disposable tree, and the
old value pointed every shell command at the live checkout the mission exists to stay out of.

**Beyond the review:** `find -exec`, `-execdir`, `-ok`, `-okdir`, `-delete` and the `-fprintf`
family are refused. `find . -exec rm {} ;` passes every containment check because the path IS the
workspace — the flag is what runs the other program, which is the same question the review asked
about paths applied to arguments.

**STILL OPEN — `dotnet` is arbitrary code execution.** It is on the allowlist deliberately, for
build and test, and `dotnet run` executes whatever the workspace contains. Argument containment
cannot address that; it is governed by `shell_tool_enabled`, which is off by default. A real sandbox
(container, seccomp profile, job object) is the correct answer and is not reachable in-process across
the three platforms the colony supports.

**Tests.** `ShellConfinementTests` — absolute paths outside the root for each command the review
named, relative traversal, paths hidden in flag values, execute/delete flags, bare tokens that must
NOT be treated as paths, and paths inside the workspace that must still work. The gates fixture
opens the shell deliberately: with it closed every one of these would pass, refused by the enable
flag before containment was consulted, which is the adjacent-question defect in its purest form.

#### S3 — Verification and evidence fail OPEN (P0)

Two consecutive fail-open boundaries, and the direction is the wrong one — a store failure WIDENS
authority instead of stopping a live write.

- A verifier that cannot read evidence returns null and falls through to model or static prose:
  `Ants.cs` L1001–1051. The static fallback can emit `Verification Passed` from completed task
  counts alone: L1157–1165.
- Auto-apply that cannot read evidence returns **zero refusals** and continues. It also
  deliberately accepts missions with no revision-identified evidence, and skips proposals with a
  null patch-set id: `AutoApplyRunner.cs` L286–329.

**Fix.** An unavailable evidence store must produce `verification_unavailable` and never prose
fallback for production verification. Live auto-apply must require a non-empty patch-set id and
complete revision identity; the complete verification bundle for the exact revision and tree; at
least one deterministic pass; no deterministic failure; every policy-required check. Compare the
patch-set CONTENT hash as well as revision id and tree hash. Legacy unidentified evidence stays
readable for history and is manual-apply only.

**Tests.** Evidence-query exceptions, no evidence, legacy evidence, null patch-set id, mixed
revisions, mixed pass/fail rows, wrong tree hashes.

This subsumes §2 item 2, which asked only that the canonical evaluator consume `Evidence.Judges()`.

**Closed v0.3.8.61.** The verifier distinguishes a store that FAILED from a store that never
existed: failure produces `verification_unavailable` — a verdict `Parse` cannot emit and `IsPass`
never accepts — while the no-store CLI/test configuration keeps its static contract. Auto-apply's
gate (`RefuseEvidenceAboutAnotherRevision`, now taking `IEvidenceStore` so tests drive the real
function) refuses on: store read failure; no revision-identified evidence (legacy rows are
manual-apply only); missing patch-set id; evidence not deterministic-and-passing for the exact
revision and tree; any deterministic FAILURE for the revision (a pass cannot outvote it); and a
patch-set CONTENT hash mismatch — the evidence judged bytes, so the gate compares bytes, which
also makes a policy-filtered subset self-refusing. `EvidenceFailsClosedTests` covers the full
test list above behaviourally. Residual: "every policy-required check" is enforced upstream by
the canonical `completed_verified` evaluation auto-apply already requires; it is not re-derived
inside the gate, deliberately — one authority (`MissionVerification`) owns that rule.

#### S4 — Transactional patch application (P0)

The v0.3.8.57 "a patch set applies as a unit or not at all" guarantee does not survive a mid-write
failure.

- `ApplyPatchTool` backs up and then performs destructive I/O, but its outer handler returns only an
  error — the backup and path metadata are lost. A `WriteAllText` that truncates or partially
  creates a file before throwing is therefore unrecoverable: L75–95, L156–198.
- `AutoApplyRunner` rolls back only the EARLIER successful patches, never the operation that failed
  mid-write. It ignores every rollback return value and logs the whole batch as rolled back
  regardless: L176–204, L243–258.
- Rollback itself can destroy newer work: it deletes added files and overwrites modified or renamed
  ones without checking whether they changed after apply. Manual revert has the same behaviour:
  `Queen.Views.cs` L173–226, L268–318.

**Fix.** Stage writes into temporaries and atomically replace or move. Write a durable transaction
journal before the first mutation, and recover incomplete journals at startup. Return recovery
metadata even when the current operation fails. Record pre- and post-apply hashes, and roll back
only where the current bytes still match what was applied. Treat incomplete rollback as a critical,
durable `rollback_failed` state that halts auto-apply.

**Tests.** Injected disk-full, permission change, partial write, rename, rollback failure,
concurrent edit, process crash — asserting a **byte-identical** restored tree.
`AutoApplyAtomicityTests.cs` L141–155 currently asserts only that the SOURCE contains a rollback
call. That is a check answering a question adjacent to the one asked, in the file whose whole
purpose is to prove atomicity.

**Closed v0.3.8.62.** `ApplyTransaction` (SDK): journal durable before the first mutation; staged
atomic writes (a target is never half-written); hash-checked rollback that preserves and reports
newer work as conflicts; a durable `ROLLBACK_FAILED` marker that halts auto-apply until an
operator clears it; startup recovery replaying interrupted journals under the same rule. The tool
reports recovery metadata on failure and `applied_hash` on success; the runner journals the batch
and believes the rollback report; manual revert applies the same hash gate; the un-journaled
`RollbackAutoApplied` is deleted rather than left to drift. `ApplyTransactionTests` covers the
fault list behaviourally, byte-identical assertions included; the adjacent-question source scan is
replaced. Residual: disk-full and permission faults are injected at the transaction's write seam
rather than by filling a real volume — the seam fires between the temp write and the atomic swap,
which is the worst real moment; and legacy patches applied before `applied_hash` existed keep the
old unchecked revert behaviour, stated in the revert reply.

#### S5 — `ArtifactVisibility.Secret` does not prevent model disclosure (P0)

`Artifact.cs` L63–71 states that a Secret artifact is "never rendered, never sent to a model".
Nothing enforces it:

- mission queries return every visibility: `SqliteMemory.Artifacts.cs` L146–158;
- `ArtifactContext.Compile` does not filter Secret and emits their payloads, including declared
  inputs: `ArtifactContext.cs` L98–143, L168–205;
- those blocks are appended directly to model prompts: `DomainHelpers.cs` L153–162;
- the soldier reads payloads directly with no visibility check: `SpecialistAnts.cs` L325–354.

Built-in producers currently write mostly Colony or Operator, so exploitation needs a module, a
custom producer, or a corrupted/imported row. That is not much comfort: the public SDK exists to
support modules, and **malformed visibility is deliberately coerced TO Secret** — so the unsafe
value is precisely the one a malformed import lands on.

**Fix.** An audience-aware retrieval and render policy. Secret artifacts never enter any model
context or narrative renderer. A declared Secret INPUT is reported as WITHHELD rather than silently
omitted — a silent drop is how a role reasons confidently about a premise it never received. Apply
the check again at every direct consumer.

**Tests.** Prioritized, declared, and corrupt-visibility Secret artifacts.

**Closed v0.3.8.63.** `Artifact.IsModelReadable` is the one definition, and it is an ALLOWLIST
(Colony or Operator) so an out-of-range enum value fails closed — the coercion of malformed
visibility TO Secret finally means something. The context compiler removes Secret payloads from
mission-wide blocks (unadvertised) and reports declared Secret inputs as WITHHELD by id and
schema, never by content; the soldier's direct read applies the same check and names what it
withheld. `SecretArtifactTests` covers the review's three cases. Residual: the enum doc also says
"never in an API response" — the API surfaces return artifact METADATA through their own routes
and were not part of the four defect sites; a sweep of those routes belongs with S6's UI work.

#### S6 — UI-map gate fails open, and `{}` is a valid map (P1)

`UiChangeGate.Check` allows when the artifact store is absent or throws: L88–107. That was a
deliberate choice — a missing store is evidence about the WIRING rather than the mission, and
failing closed would block every CLI and test caller — but production dispatch always has a store,
and the two cases are distinguishable. Separately, the `ui_map` schema requires no keys, so `{}`
conforms: `ArtifactSchemaCheck.cs` L121–124. `UiChangeGateTests` proves a truncated map is refused
while an empty one passes.

**Fix.** Fail closed on production dispatch when the store is unavailable. Require an intact map
from `ui_cartographer` carrying `files_examined`, `routes` and `api_calls`.

**Closed v0.3.8.64.** The gate distinguishes the absent store (CLI/tests — permissive, evidence
about the wiring) from the throwing store (an incident — refuses, naming the outage), the same
two-state repair the verifier received in S3. The ui_map schema requires the three keys the
cartographer has always emitted unconditionally, so `{}` no longer conforms while an honest empty
map (`routes: []`) still does. S5's API residual was swept and closed by observation: no
Anthill.Api route serves artifact payloads at all, so "never in an API response" holds vacuously;
any future artifact-serving route must filter on `IsModelReadable` and this note is its warning.

#### S7 — Subprocess timeouts that cannot fire (P1) ✅ v0.3.8.59

`ShellCommandTool` and `RepoOps.Git` call synchronous `ReadToEnd()` **before**
`WaitForExit(timeout)`. A process that never exits therefore never reaches the timeout, and
sequential stdout-then-stderr reads deadlock when the other pipe fills: `ShellAndWebTools.cs`
L44–56, `RepoOps.cs` L27–51.

v0.3.8.57 fixed five git sites to kill their process trees on timeout and did not fix the read that
prevents the timeout being reached — the guard was added upstream of the thing that blocks it.

**Fix.** Concurrent asynchronous draining, bounded output, cancellation, process-tree termination.

**Half done in v0.3.8.59, unavoidably.** `ShellCommandTool`'s reads and its timeout are the same
method S2 had to change, so leaving the ordering broken there would have meant shipping a security
fix into a method that still hangs. Both pipes now drain concurrently, the wait bounds the whole
thing, the kill takes the process tree, and output is capped at 20,000 characters — `find` over a
large tree previously returned everything, into a ToolResult, into an artifact, into a prompt.

**`RepoOps.Git` closed too.** Same fix: both pipes drained concurrently, the wait bounds the whole
call, the process tree is killed. Worth naming what it looked like — v0.3.8.57 added
`Kill(entireProcessTree: true)` to the very line below the reads and did not touch them, so the
colony spent two releases with a correct kill on an unreachable path. A guard placed downstream of
the thing that hangs.

`git clone` on a large repository is the concrete deadlock: it writes progress to stderr
continuously while this side drains stdout, each waits for the other, and neither is timed out.

**STILL OPEN — the behavioural tests.** A child that writes heavily to BOTH streams and one that
never exits are not written. `ShellConfinementTests` pins the ORDER (no synchronous read between
start and wait), which is the defect's shape rather than a proof that the fix survives a real hang.

**Closed v0.3.8.65.** `SubprocessHangTests` runs the real things: a git that genuinely never exits
(a pre-commit hook that sleeps, against a shortened test-seam timeout) proves the timeout FIRES
and the call returns bounded; a hook that writes ~130KB to BOTH streams proves the sequential-read
deadlock is gone; and a `find` over four thousand files, through the production ShellCommandTool,
proves the flood drains concurrently and the output cap holds. POSIX-only by early return — the
children are shell scripts, and CI and both operator gates run them.

#### S9 — The colony asserts its roles through a channel that carries no authority (P0-adjacent) ✅ v0.3.8.59

**Found in the field, v0.3.8.59, and it is the release's own defect class one layer out.** With every
message now a mission, the colony's role prompts reach an agent CLI and the agent REFUSES them as a
prompt-injection attempt. It is right to.

`AgentCliProvider.Flatten` collapses every `ModelMessage` into one string, prefixing non-user roles
with a literal `[system]` text header, and hands the result to `-p "{prompt}"`. `-p` is a **USER
TURN**. `AgentCli` has `PromptArgs`, `StreamArgs`, `AcceptEditsArgs`, `AutoApproveToolArgs`,
`BypassArgs`, `AddDirArgs` and `LocalSettingsRelativePath` — and **no system-prompt flag at all**.

So what the agent receives is a user message that assigns it a persona, cites mission IDs, asserts
tool permissions the session does not have, demands a fixed output format, and carries a line of
prose that says `[system]`. That is the signature of an injection, not a resemblance to one. A model
that complied would be a model that does whatever any user turn claiming to be a system tells it.

The direct agent lane never hit this because the operator's words arrived as what they were: a person
asking a question. Nothing was impersonating anything. Deleting that lane was still right — but it
exposed that the colony's authority over its workers was never carried by anything except prose.

**Fix.** Use the real channel. Claude Code has `--append-system-prompt` and `--system-prompt` (and
`--system-prompt-file`), all valid alongside `-p`. Add `SystemPromptArgs` to `AgentCli`; route the
role contract there and ONLY the operator's actual task through `-p`; delete the `[system]` text
header, which exists solely because the system channel was missing. An agent with no such flag either
keeps the flattened form and its refusals, or is not routed roles that need a persona — recorded
per agent rather than assumed uniform.

**And stop laundering operator text as colony framing.** The field report showed an operator's
off-topic question surfacing as a project's "stated purpose". Operator text must be labelled as
operator text; presenting it under a heading that claims it is something else is the same defect
pointed inward, and it is what makes a legitimate prompt read as a fabricated one.

**Fixed in v0.3.8.59.** `AnthillRuntime.PromptInjectionPrefix` is deleted. `RoleSystemPrompt(role,
mission)` replaces it on the SYSTEM channel and says where it comes from — "this message is your
operating contract and comes from the harness itself, not from the person who wrote the request" —
which is a claim the transport now makes true. `UntrustedBlock(label, text)` fences the spans that
genuinely are untrusted, with paired delimiters and a subject.

`GenerateTyped` takes `system:`, composing a System + User pair; null keeps the old single-message
shape so an unconverted caller loses nothing. `AgentCli.SystemPromptArgs` carries the contract to an
agent's own flag — Claude Code's `--append-system-prompt`, appended rather than replacing so the
agent keeps its own tool guidance and safety instructions. `AgentCliProvider.Flatten` became `Split`;
the `[system]` literal is gone, and an agent with no such channel folds the contract into the prompt
plainly rather than impersonating a system header.

All eight model-calling roles now send a contract. The scribe is worth noting: it never carried the
old prefix, so it was the one role NOT sending an injection-shaped prompt — and also the one sending
no operating rules at all. Same gap, opposite symptom.

**Tests.** `RoleContractChannelTests` — no source anywhere asserts a system boundary from inside a
prompt; the contract names its own origin; the untrusted block fences only what it labels; every
`GenerateTyped` call passes `system:`; the catalog appends rather than replaces; an empty contract
sends no flag (a blank `--append-system-prompt ""` reads as an instruction to have no contract); and
the contract travels as discrete argv, never shell text.

**THE TALKING POINTS WERE CONDITIONAL FACTS ASSERTED UNCONDITIONALLY**, which is worse than either
"true" or "false" and is why a worker refusing to vouch for them was right. Both features exist:
`missions_fts USING fts5` is created in `SqliteMemory.Schema`, and `EnableParallelExecution` /
`MaxParallelWorkers` drive a `TaskScheduler` that reads `DependsOn`. But both are RUNTIME-CONDITIONAL.
`FtsAvailable` is a mutable flag the memory layer sets to false when SQLite throws
(`catch (SqliteException) { FtsAvailable = false; }`), so on an install without FTS5 the claim is
false — and `SelfTest` already reports exactly that: "FTS5 not available; keyword fallback in use".
`EnableParallelExecution` is an operator toggle, surfaced in `Queen.Views` as `Parallel Execution:
{flag}`.

So the colony KNEW the answer at runtime and asked the model to assert it from prose instead. That is
this repository's own recurring shape — a claim derived from a sentence rather than from the state
that knows — pointed at the operator reading the answer.

The strongest evidence it was a known hedge that got lost: the non-LLM `FallbackResponse` in the same
class says "uses FTS5 WHEN AVAILABLE". The deterministic path was more truthful than the instruction
given to the model.

**Related and NOT fixed:** that same `FallbackResponse` still opens with "1. Review patch proposals
using /patches…" unconditionally, on missions that produced no patches. Same untruth, deterministic
rather than generated, so no model will ever flag it.

**COMPLETED in the same release.** All six persona-bearing prompts converted — builder, coder,
verifier, planner and strategist by name; researcher and web carried only the banner. Operator text
(mission goal, prior task output, standing objective) is fenced with `UntrustedBlock`. The
strategist's objective matters most: it is text an operator wrote that the colony re-reads unattended
on every run, which makes it the highest-value place in the colony to plant an instruction — authored
once, obeyed forever, with nobody watching that turn.

The builder's `FallbackResponse` no longer opens with "Review patch proposals using /patches" on
missions that produced none. Same untruth as the deleted talking points, deterministic rather than
generated, so nothing downstream would ever have flagged it.

**STILL OPEN.** Only Claude Code has a verified system-prompt flag; Codex, Gemini, Aider and OpenCode
are declared as having none and fall back to folding. That is recorded per agent rather than assumed
uniform, but it means the fix is partial for four of five agents until each flag is confirmed.

#### S8 — Re-enable ✅ decision recorded v0.3.8.65

Fault-injection tests land before auto-apply is switched back on. §2 resumes after that.

**The precondition is met.** Every rung of this ladder is closed: confinement (S1/S2, .59),
evidence failing closed (S3, .61), transactional apply with durable recovery (S4, .62), secret
filtering (S5, .63), the UI gate (S6, .64), subprocess hangs proven survivable behaviourally
(S7, .59 + .65), and the authority channel (S9, .59). The fault-injection suites the reviewer
required exist and run on every gate: `ApplyTransactionTests` (mid-write faults, crash recovery,
concurrent edits, vanished backups — byte-identical restores or durable halts),
`EvidenceFailsClosedTests` (throwing stores), `SubprocessHangTests` (real hangs and floods).

**Re-enabling is an OPERATOR act, and this is its checklist.** `autonomy_autoapply_enabled` stays
off in the shipped defaults; an operator turning it on should verify, in order: (1) this section's
ladder shows every rung ✅ in the running version; (2) no `ROLLBACK_FAILED` marker exists under the
workspace's `.anthill/apply-journal/` — the runner refuses while one does; (3) both write gates
(`patch_application_enabled`, `file_writing_enabled`) are deliberate choices, not leftovers;
(4) `autonomy_autoapply_verify_cmd` names a check the deployment can actually run, or the
break-glass keep-without-verify is a knowing, logged choice; (5) the auto-apply path allowlist
names only trees whose partial states the operator could tolerate diagnosing. The colony enforces
(2) itself and logs the rest; the list exists so the decision is made once, with eyes open, rather
than discovered in fragments during an incident.

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
and read by nothing until the same release closed it. v0.3.8.86 found the vocabulary case: two event
constants nothing published, both NEAR-MISSES of real event names, so a subscriber filtering on
either compiled, ran, and matched nothing forever. v0.3.8.87 found the version with a passing test
attached — `ToolCatalog.CanRun`, a pre-execution permission check with no production caller in its
whole life, whose only caller was a test that built the descriptor AND the grant set itself. The
sharper reading is the one `FailureClassNames` had already written down about a different bug: *no
test anywhere ran a value from a real producer into a real consumer.*

**A filter that could not match, guarding the property that mattered most.** v0.3.8.89. Five
assertions queried `memory_candidate_archived`; the ingest emits `memory_candidate`. One of the five
was the cancellation harness's "no memory survives a stopped mission" — the property R3's header
singles out as the one that outlives the mission — and it was checking for an event no producer
writes. v0.3.8.85 wrote the sentence that names it ("held by luck rather than by design") and
attributed it to the archivist usually having nothing to propose; the real reason is that the filter
matched nothing. Found from the CONSUMER side: an event type queried by name is a position that can
only be an event type, and four of eighteen such names were declared nowhere — a blind spot in
v0.3.8.86's publication sweep, which by design reads only literals handed directly to `LogEvent`.

**A declaration that disagrees with the runtime.** The verifier's contract said planner-selectable
for six releases after the runtime guaranteed insertion. `scheduling_mode` is reported by the API and
read by operators, so this was the system stating a guarantee it did not keep.

**State captured before the bootstrap that sets it.** v0.3.8.88, and it cost a release cycle to
find. `AnthillRuntime.Initialize` is ONE-SHOT and projects the on-disk config over fifty-one
process-global statics; `Queen`'s constructor calls it. So a test that set a roster flag and then
built the first Queen in the process had its setting silently discarded, while the identical test
running second kept it — the outcome decided by position in the run, and, because the values come
from a file on the developer's machine, by whose machine ran it. Four lifecycle tests failed at
v0.3.8.87 with no production change behind them; the same four reproduced on the previous tag under
the same filter, which is what separated "my change broke this" from "this was never deterministic".
A sweep found twenty-one more test files saving one of those statics with no guarantee the bootstrap
had happened. Closed at the root — a `[ModuleInitializer]` runs it before the first test — rather than at
the four instances that happened to break.

**Prose as a control channel.** The bound on repair looping was a substring search of a previous
medic's narrative, and task results are truncated — so the bound was weakest exactly where the loop
was longest.

**A diagnostic that breaks what it describes.** The artifact schema check logged a violation through
an event table with a foreign key, turning "this payload is the wrong shape" into "the artifact was
never stored".

**Timeouts that abandon the work.** Five sites called `WaitForExit(ms)`, carried on when it returned
false, and read `ExitCode` — which throws on a live process — so a timeout surfaced as an
ordinary-looking exception while the process kept running.

**Two implementations of one rule, which eventually disagree.** Named in `HANDOFF.md` and earned its
place here at v0.3.8.81, with the shortest distance yet between the two copies: *four lines, in one
method.* `ModelRouter.SendCore` asked `ToCircuitSignal` whether a cancelled call says anything about
the provider (it said no) and then derived a pheromone delta from `result.Ok` (which said yes,
negatively). The transient reader was right and the durable one was wrong, so the disagreement was
invisible in the mission and permanent in the memory. **The lesson is not "look harder" — it is that
the second reader must ask the first**, which is what a shared predicate makes structural.

v0.3.8.87 found the widest distance instead of the shortest: two whole catalogs, in two assemblies,
declaring what each role may do. `AntExecutionCatalog` was enforced at DISPATCH; `ToolCatalog` was
read at ADMISSION and checked against nothing. They disagreed about capabilities for four roles,
about side effects for two, and about six roles the second did not list — where the projection's
fallback supplied `model.invoke`, the exact lie v0.3.8.76 had deleted from the archivist's contract
and which survived in the other book because nobody read both at once. The sharpest single
disagreement: `ToolCatalog` required `repo.write.sandbox` for the builder, a capability
`CapabilityGrant` is written never to grant, in a comment that names it. A requirement nothing could
satisfy, beside a check nothing ran. **The fix is the one `FailureClassNames` already established —
not "pick a book", but remove the choice.**

**A vocabulary that named half of what it described.** `EventTypes` declared 69 event constants
against 134 the runtime emits, said in its own header that it "was READ, out of the working tree,
from the LogEvent call sites" and that a subscriber written against it "is written against reality",
and instructed every future author to add the constant in the same change as the publisher. That
instruction was followed for roughly half the events across every release that had it. Two of the 69
were emitted by nobody and both were NEAR-MISSES of real names — the filter compiles, runs, and
matches nothing forever, which is the exact empty-panel failure the file was created to prevent.
Closed at v0.3.8.86 with `EventVocabularyTests`, and the general shape is the interesting part: *a
rule a document states and nothing checks describes the author's intention rather than the tree.*
This repository has now found that shape in a plan checklist, a graduation record, a qualification
ledger and an event vocabulary.

**A fixture that never ran the thing it declared.** Named at v0.3.8.82 and swept at v0.3.8.83, and
it belongs here rather than in a test file because the shape is general: a component that SUBSTITUTES
a safe default when its input is unusable — correctly, and loudly, to a log nobody in a test run
reads — turns every consumer that does not check into a consumer testing the default. Two fixtures
were affected and neither looked wrong; both asserted true things about a mission the fallback plan
produced. The fix is not "read the log": it is that a caller who supplies an input must be able to
assert the input was used.

**A filter that could not match, swept.** Named at v0.3.8.89 for `memory_candidate_archived` — an
event five assertions watched for and nothing has ever emitted. The v0.3.8.90 sweep looked for the
shape in every vocabulary in the tree and found ten more, and the distribution is the lesson: they
cluster wherever a producer and a consumer are far apart and a string is the only thing joining them.
The most expensive was not an empty panel but an ACTIVE harm — four handoffs to the builder asked for
task type `build` against a contract declaring `build_answer`, and because three were `Required`, the
gate's refusal set `DeterministicBlock` on the source task. The two routes whose purpose is to reach a
human marked the mission unverifiable and reached nobody, for as long as they have existed. Two
generalisations worth keeping: *a near-miss survives because the other arms of the same filter keep
working*, so the feature never looks broken; and the direction nothing was checking each time was
consumer→vocabulary, because the guards that existed all ran vocabulary→producer.

**A rule implemented twice, where the second copy is a list of strings.** `SummarizeEvents` spelled
the failure vocabulary as seven SQL literals while `GetRecentFailureEvents` built the same query from
the shared set; three literals had drifted onto names nothing emits. `ResetConfig` carries one list as
an object initializer and a second as the names it reports to the operator, and the priority route had
fallen out of both. Both now derive or are pinned to each other. The general form: *when a rule is
data, the second copy is invisible to the compiler and therefore drifts silently* — which is why the
fix is to make one derive from the other rather than to correct the copy.

**A degradation that outlives the reason for it.** Every model-calling role answers an unavailable
provider with a fallback, correctly. Cancellation entered through the same status, so the fallback
path became the cancellation path — and a role degrading gracefully is exactly a role that does not
look broken. Worth naming separately from the above because the code was right at every site: the
defect was in what ELSE arrives through a door built for one thing.
