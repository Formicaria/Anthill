# ANTHILL — THE PLAN

**The single forward document.** What is done, what is left, and the order to do it in.
`AUTONOMY-10.md` folded into this file; role mechanics live in
[`ANT_EXECUTION.md`](ANT_EXECUTION.md); the qualification protocol lives in
[`QUALIFICATION.md`](QUALIFICATION.md).

Shipping release: **v0.3.8.67**.

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
- Every operator message is a mission. There is no chat lane, no `conversation` route and no
  unconfined agent access anywhere; the colony dispatches a coding agent as a TOOL, inside a mission
  that plans, reviews, tests and verifies its work (v0.3.8.58).

Not done, and the reason each matters, is the ordered plan below.

---

## 1b. Security review — BLOCKING, ahead of everything below

An external source-level review found **four P0 and two P1 defects**. They take priority over every
item in §2, because §2 is about the colony doing MORE and these are about its existing autonomy being
trustworthy. Shipping more autonomy on top of a broken confinement boundary enlarges the blast
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
7. **S7** — runtime and fault-injection tests land BEFORE auto-apply is re-enabled

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

### 2 — Make evidence identity mandatory for promotion ✅ v0.3.8.66

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

**Closed v0.3.8.66.** `MissionVerification.IsSatisfied(tasks, evidence)` is the promotion
overload the canonical evaluator now calls: a mission with a materialized patch requires
deterministic, passing evidence whose identity (revision id, patch-set hash, tree hash — the
`Evidence.Judges()` triple) names the FINAL revision. Earlier repair generations, rows with no
identity (legacy and unpatched-workspace evidence — still readable for history), and
non-deterministic rows cannot promote; an unreadable store fails closed per §1b S3's direction.
The Queen hands the evaluator the store's rows at finalization; the evaluator version bumps to
`evaluator-v3` so a persisted row says which rules graded it — the constant's documented purpose,
exercised this time.

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
