# ANTHILL — CONSOLE ALIGNMENT BRIEF

**For the agent or engineer taking on the console.** Amended from an external draft at v0.3.8.35.

The original brief's intent is right and most of its content is kept. Four things in it did not
match this repository and would have caused real damage; they are corrected here, with the reasons,
because a brief that is wrong about the codebase produces work that is confidently wrong about it too.

---

## 0. Corrections to the original draft

**The repository is `https://github.com/Formicaria/Anthill`** — not `theonexone`. The
draft's URL fetches nothing.

**There is no frontend toolchain, and you must not add one.** The draft asks for design primitives,
shared/domain/feature component layers, strong typing, lint and frontend tests. None of that exists
here:

```
src/Anthill.UI/
  app.js             8,600 lines, vanilla, no modules
  index.html         3,100 lines
  dashboard-grid.js  808      mission-thread.js  279
  dashboard-grid.css 433
```

No `package.json`, no `tsconfig.json`, no `.csproj`, and `Anthill.UI` is **not in `Anthill.sln`** —
the assets are served statically. Following the draft literally leaves two options: introduce a
framework and bundler (an enormous unrequested change that would also break the CSP-safe
`data-onclick` delegation), or claim checks ran that could not. The draft's own §31 forbids the
second while its success criteria make it unavoidable.

**So: no framework, no bundler, no type system, no npm dependency, unless separately approved.**
Improve the vanilla code. If you believe a toolchain is genuinely required, stop and make that case
as its own proposal.

**The nine-phase, do-not-stop-early structure produces one unreviewable change.** This repo ships
small increments with explicit exit gates (`docs/HANDOFF.md`, `docs/AUTONOMY-10.md`). The work is
split into four shippable pieces in §6 below.

**Already done, so ignore the draft's ordering advice here:**
`fix/console-contract-and-escalation` merged in v0.3.8.34. Its `ConsoleRouteAgreementTests` is
existing art you should extend rather than reinvent.

---

## 1. Read these first

| Document | Why |
|---|---|
| `docs/PLAN.md` | Where the colony measurably is. §6 is the defect record — read it |
| `docs/AUTONOMY-10.md` | The forward program and phase order |
| `docs/HANDOFF.md` | Build/test/release mechanics, and what is deliberately not done |
| `docs/ANT_EXECUTION.md` | The twelve roles and their contracts |
| `CHANGELOG.md` v3.8.29 → v0.3.8.35 | The recent backend changes the console has to catch up with |

Then run the app and click through it before changing anything.

---

## 2. The invariants you may not break

These are not style preferences. Each one is a defect that already happened here.

**Executable attributes.** `data-onclick` is parsed by a micro-interpreter at the bottom of
`app.js`: it splits on `;` and resolves names against `window`. `getAttribute` decodes HTML entities
*before* that parser runs, so `escapeHtml` does not protect a value sitting inside `fn('…')` — an
apostrophe ends the argument and `;` starts a second statement that runs with the operator's session.

- Any value interpolated into a `data-on*` attribute goes through **`jsArg`**.
- Untrusted values should prefer the **`data-action` map** (v3.8.13): the value travels in a plain
  `data-*` attribute that is never parsed as code.
- `UiActionDispatchTests` enforces both. Do not weaken it.

**Every `/status` field must have a consumer.** `ollama_model_present` was computed and sent on
every request from v2.4.3 and read by nothing, so the status chip stayed green through a completely
unusable colony. `StatusFieldConsumerTests` now requires each field to be read or exempted with a
reason. If your redesign stops reading a field, that is a decision to record, not a test to silence.

**Backend semantics are not yours to change for UI convenience.** If a contract genuinely blocks
correct UX, make the smallest sound correction and say so in the PR.

**Do not rename backend states to make them friendlier.** Build a presentation layer. `blocked`,
`failed_retryable` and `skipped` mean specific things the runtime acts on.

---

## 3. What the console is actually for

Anthill runs **missions**. An operator gives a goal; a Queen plans it into tasks; twelve specialised
roles execute them; evidence decides whether the result is verified.

The user's questions, in priority order:

1. Is it working right now, and if not, what do I do about it?
2. What happened in this mission, and can I trust the result?
3. What is waiting on me? (approvals, escalations, patch review)
4. How do I configure it — model, providers, which roles run?
5. Why did it do that? (diagnostics, evidence, trails)

Structure the console around those. Not around `/jobs`, `/workers`, `/events`.

**Terminology worth fixing.** The backend says ant, caste, worker, role, pheromone, trail, mission,
objective, task, artifact, evidence. The console currently uses several of these interchangeably.
Pick one user-facing word per concept and apply it everywhere; keep the internal term visible in the
technical layer so an operator reading logs can connect the two.

---

## 4. Progressive disclosure, concretely

- **Layer 1** — is it healthy, what needs me, what happened, primary actions.
- **Layer 2** — mission timeline, per-task outcomes, artifacts, evidence, retries, cost.
- **Layer 3** — raw ids, `/status` internals, trail kinds, failure classes, model routes, raw JSON.

The colony has genuine depth. Hiding it is as wrong as leading with it.

---

## 5. The state that matters most

The console's hardest job is telling an operator **why nothing is happening**. Cover all of these
explicitly — several have historically shown as "fine":

- No model chosen, or the configured one is not installed (`model_choice`, `model_resolved`).
- Ollama unreachable versus reachable-but-unusable — different states, different fixes.
- A role blocked by a gate versus a role that has no gate (`RoleReadiness`, `RoleGateStatus`).
- A mission blocked on a human decision (escalation, approval).
- A patch proposed but unverified; a patch verified but unapplied.
- Degraded mode: the colony ran without a model and the result is not verified.

Empty states say what is empty, whether that is expected, and the next step. Errors lead with the
human problem and put the raw response behind "technical details".

---

## 6. The four deliverables

Ship each one. Each gets its own PR, version bump and exit gate.

### A. Contract audit and guards
No visual change. Inventory every `api(...)` call in the console against the routes `ApiHost` maps;
every `/status` field against its consumer; every documented backend capability against whether the
UI represents it at all. Extend `ConsoleRouteAgreementTests` and `StatusFieldConsumerTests`.
**Exit gate:** a written list of UI/backend mismatches, and a guard for each class found.

### B. Navigation and information architecture
Restructure around §3. No dead ends, no orphaned pages, obvious current location.
**Exit gate:** a first-time walkthrough reaching every primary workflow without documentation.

### C. States
Empty, loading, error, degraded, blocked — everywhere, per §5.
**Exit gate:** every state in §5 reachable in a test or a scripted scenario, and correct.

### D. Consistency and visual hierarchy
One button pattern, one table pattern, one status vocabulary, one set of spacing rules.
**Exit gate:** no competing patterns for the same job; accessibility pass on keyboard and contrast.

---

## 7. Testing — what actually exists

```powershell
dotnet build Anthill.sln -c Release
dotnet test tests\Anthill.Tests\Anthill.Tests.csproj -c Release
node --check src\Anthill.UI\app.js
```

There is **no JS test runner and no linter**. The console is tested by C# tests that read
`app.js`/`index.html` as text: `UiActionDispatchTests`, `UiShellTests`, `UiAbsenceTests`,
`ConsoleRouteAgreementTests`, `StatusFieldConsumerTests`, and `RegressionGuardTests`' UI integrity
checks. That is the convention — extend it.

`node --check` is the only syntax check available. Use it; it has already caught real breakage.

**Do not report a test as run if you did not run it.** If something cannot be checked in your
environment, say which and why.

---

## 8. Git and release

- Never push to `main`. Branch, PR, squash-merge, then tag.
- **Five version markers** must agree: `Directory.Build.props`, `AnthillRuntime.Version`, README's
  `**Current version:**`, the top `## vX` entry in `CHANGELOG.md`, and a mention in `docs/PLAN.md`.
- Versions are now `v0.<major>.<minor>.<patch>` — see the v0.3.8.34 entry for why.
- Before tagging, confirm the markers already read the new version. A branch with no bump passes
  every guard, because they check that the markers agree with each other, not that they moved.
- Do not rewrite historical changelog entries. Ever.

---

## 9. Completion

Not "the build succeeds". Completion is:

- Backend contracts verified against what the API really returns.
- Every state in §5 reachable and correct.
- Guards added for each class of mismatch found.
- Tests run, with real output quoted.
- A first-time walkthrough performed and its findings fixed.
- Known limitations listed — the real ones, not a disclaimer.

If you find a defect in this brief, fix the brief. The last three releases were spent discovering
that a confident document is the most expensive kind of wrong.

---

## 10. Backend defects already fixed — do not re-implement

The external console brief (audited at v0.3.8.36) listed these as open. Re-proved and closed since;
verify with the named test before assuming any of it is still broken.

| Finding | State |
|---|---|
| Patch proposals carry no expected base hash | CLOSED v0.3.8.37 — `PatchBaseHashTests` |
| Empty auto-apply allowlist unproven | Was never open — `DeniedWhenAllowlistEmpty` |
| Mission submission idempotency unreachable | CLOSED v0.3.8.38 — `DurableMissionContractTests` |
| Durable job listed but not openable | CLOSED v0.3.8.38 — one projection, list and detail |
| Live/durable projections disagree on `outcome_code` | CLOSED v0.3.8.38 — joined from the canonical evaluation |
| Cancel-all not durable | CLOSED v0.3.8.38 — delegates to the single durable cancel |
| Clear-history unguarded during active work | CLOSED v0.3.8.38 — refused server-side |

Still open and owned by the console work: the auto-opening technical report on mission completion,
scattered terminal-state lists, canonical outcome versus answer framing, and settings that report
"saved" for a frozen runtime field.

---

## 11. The Micromound console — CLOSED at v0.3.8.115

`ConsoleRouteCoverageTests.NoConsoleSurface` records every mapped route with no console surface and
why. It is route-granular and normalises a path to one key regardless of method — so when Colony Live
began READING `/micromound/mounds` at v0.3.8.115 to decide whether to offer a device descent, that
entry had to leave the ledger even though the POST on the same path still had no surface. This
section was written to hold that one deferral. It held it for the length of one release.

`src/Anthill.UI/micromound.js` shipped in the same release and the whole Micromound UI-GAP block left
the ledger with it:

| Surface | Route | State at v0.3.8.115 |
|---|---|---|
| Fleet, with the colony's status verdict | `GET /micromound/mounds` | rendered — table with class, status, last beat, charter, lease, queued downlink |
| Mint an enrollment token | `POST /micromound/mounds` | rendered — shown once, stored nowhere, guarded by `AMintedToken_IsNeverPersisted` |
| Retire a device | `POST /micromound/unlink` | rendered — confirms, and repeats the API's note that the device is not told |
| Per-mound stop / resume | `POST /micromound/stop`, `/stop/resume` | rendered on each fleet row |
| Issue a charter | `POST /micromound/charters` | rendered — capabilities, routines, ceiling, duration, lease, safe state, evidence policy, per-capability limits |
| Author a manifest | `POST /micromound/config` | rendered — hardware bindings, capabilities, routines, workers, reasoning mode, safe state |
| Dispatch a physical mission | `POST /micromound/missions` | rendered — ordered steps with conditions; reports dispatch or the approval it parked |
| Per-mission evidence | `GET /micromound/missions/{id}` | rendered — device verdict and colony verdict, side by side, never merged |
| Capability resolution | `GET /micromound/resolve` | rendered — every candidate with its blockers, not only the eligible ones |
| Evidence feed | `GET /micromound/evidence` | rendered — fleet feed, and per-device recent beats |

Still absent, and correctly so: `POST /micromound/v0/enroll` and `/v0/sync` are DEVICE endpoints. A
mound is not an operator; it dials in, and its auth is a one-time token or an Ed25519 signature.

**The rule this section is really about.** Every form field was read off the type that declares it —
`Micromound.Protocol` for the wire shapes, `ApiHost.Micromound.cs` for the request bodies — because
v0.3.8.114 named the defect class for the opposite and paid a whole release for it at the front door
of the integration. `ConsoleVocabularyTests` keeps that true going forward at the typed tier: the
console's five closed vocabularies are compared against the protocol's own sets, so a protocol change
fails a test instead of leaving the console quietly a version behind.

**What the console still does NOT compute.** Nothing. Online/offline is the colony's verdict,
carried on the fleet listing since `MicromoundWidgets.StatusOf` became public at v0.3.8.115 — the
console renders it and does not recompute it, because the rule reads configuration a browser cannot
see.

**One thing worth retiring rather than finishing.** `mound_fleet`, `mission_status` and
`evidence_feed` widget payloads are still built on every Micromound mutation for a widget runtime
that was going to render them. The console now reads the routes directly. That is a second surface
with no reader — the same shape as `/config/health` before v0.3.8.35 — and it is carried in
`docs/PLAN.md` §2e.
