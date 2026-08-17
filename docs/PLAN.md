# ANTHILL — THE PLAN

**The single forward document.** What is done, what is left, and the order to do it in.
`AUTONOMY-10.md` folded into this file; role mechanics live in
[`ANT_EXECUTION.md`](ANT_EXECUTION.md); the qualification protocol lives in
[`QUALIFICATION.md`](QUALIFICATION.md).

Shipping release: **v0.3.8.79**.

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
| Acceptance gates | **10 of 12**. Gates 1 and 2 (all roles Ready; every role with handler, contract, real trigger, typed output) close with items R3–R4 |
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

### R3 — Per-role cancellation and timeout · *3–5 releases*

The largest single item remaining, and the other large area where nothing has ever asserted the
outcome — which by the pattern of R2's neighbours means it will find defects rather than document
behaviour.

**All twelve roles**, not the high-risk subset. Order by risk — tester, file, researcher, web,
ui_cartographer, scribe, and the coder on an agent CLI hold tools, sockets and subprocesses — but
builder, verifier, soldier, medic and archivist need explicit proofs too.

Four cancellation points per role: before dispatch, during generation, during a tool call, and while
waiting on a dependency. Five properties each: correct terminal state, no retry or handoff after
operator cancellation, no orphan process, no positive memory or reputation, clean restart.

Build the shared harness first; twelve roles × four points is a fixture, not forty-eight tests.

> **Exit gate.** The graduation record's cancellation column is complete for all twelve roles,
> including the missing `ui_cartographer` fault cell. **Acceptance gates 1 and 2 close here.**

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

> **Exit gate.** A recorded run per provider with complete telemetry, and `QUALIFICATION.md` updated
> from "never happened" to the run's own evidence.

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

**Not a tail.** Fully async execution, ~166 runtime statics, VRAM scheduling, multi-platform QA,
event-loss accounting and deployment verification are independent workstreams. The statics in
particular have caused three defects in this session alone (a leaked `UseOllama`, two roster-gate
leaks), and they get harder to remove as more code depends on them. Take slices opportunistically;
some belong before R9 rather than after.

---

## 3. Acceptance gates

Non-negotiable. The colony is not a twelve-role colony until all of these pass. **10 of 12.**

1. ◻ All twelve roles report Ready under the full profile *(closes with R3)*
2. ◻ Every enabled role has a handler, contract, real production trigger and typed output *(R1 + R3)*
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
