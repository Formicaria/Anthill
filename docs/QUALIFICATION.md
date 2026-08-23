# ANTHILL — Qualification

Two things with the same name and different jobs. Keeping them apart is the point of this document.

| | Deterministic qualification | Live qualification |
|---|---|---|
| Runs | in CI, on every change | by an operator, deliberately |
| Blocks a merge | **yes** | **no** |
| Model | scripted (`ScriptedColony`) | real |
| Proves | the runtime does what it says | the runtime survives what models actually do |
| Status | **20 of 20 scenarios closed by substance** *(v0.3.8.79)* | **NEVER RUN** — see §3 |

A live run must never gate a merge. It costs money, needs credentials, depends on someone else's
uptime, and is non-deterministic by construction — a suite with those properties either becomes flaky
and gets ignored, or becomes a bill nobody approved. And a deterministic suite must never be mistaken
for evidence that the colony works with real models, because every answer in it was authored to fit
the runtime.

---

## 1. Deterministic qualification — merge-blocking

```
dotnet test tests\Anthill.Tests\Anthill.Tests.csproj -c Release
```

The twenty scenarios and what proves each are in `QualificationMatrixTests`, as an executable ledger
rather than a comment: every cited test file must exist, every open scenario must say OPEN, and every
open one must be named in `PLAN.md`. A citation cannot rot silently into a file that was deleted.

**Open at v0.3.8.57:** scenarios 3 (documentation patch) and 15 (all twelve roles through production
triggers). Both need a `ScriptedColony` script book; the harness exists and the scenarios do not.
Scenarios 7 and 17 are marked PARTIAL with the specific missing case named.

---

## 2. Per-role graduation

`RoleQualificationRecordTests` holds one row per executable role and nine columns — unit, integration,
production-call-site, fault, end-to-end, cancellation-and-timeout, activation, kill-switch, readiness
blocker.

The readiness surface (`RoleReadiness`) answers a **different question**: "can this role run now".
A role can be Ready and have no fault proof at all. Graduation is about what has been proved.

**The record is COMPLETE as of v0.3.8.81** — every row, every column, including the
cancellation-and-timeout column that was twelve-of-twelve empty from v0.3.8.57 until then, and the
`ui_cartographer` fault cell that was the last non-cancellation null.

The original finding is kept because the shape recurs. That column was empty by DISCOVERY, not by
omission: the first draft filled it with `ModelCallCancellationTests` and `ProcessTreeCancellationTests`
— real files proving real things (the model call observes cancellation; the process-launching sites
kill their trees) and saying nothing about any ROLE. The check that caught it was the weakest one in
the ledger: does the cited file so much as mention this role. A citation true about the system and
false about the row is how a record fills up while nothing gets proved.

It mattered more than the count suggested: v0.3.8.57 found five separate sites that abandoned a
running process on timeout, all in the area this column was emptiest about. Under-tested and
under-implemented turned out to be the same region.

**What the column now cites, and what it does not claim.** One file decides all forty-eight
role × cancellation-point cells: `RoleCancellationTests`. Twenty-nine are driven live, two are cited
to mechanism tests (`verifier/during_generation` and `tester/during_tool_call`, both
`SchedulingMode.PolicyInserted` and therefore unreachable by planning a task), and seventeen are
not-applicable with a reason checked against the contract rather than trusted.

**And a correction worth reading, from v0.3.8.82.** Between v0.3.8.80 and v0.3.8.82 that matrix
reported more coverage than it had. Every plan its fixtures scripted was below
`AnthillRuntime.MinDynamicTasks`, so `Planner` discarded it and ran the static fallback — and each
cell passed because a fallback branch happened to contain the role the assertion named. The
assertions were true; they were about the wrong mission. `AssertTheMissionRanTheScriptedPlan` now
compares the planned roles against the scripted ones on every cell, which is the guard whose absence
made a fixture-level substitution invisible for two releases.

---

## 3. Live qualification — NEVER RUN

**Status: not performed. No result exists for any provider.**

This is the honest state and it is worth being blunt about, because everything else in this repository
is scripted. The deterministic suite proves the runtime behaves as designed against answers written to
fit it. It cannot tell you what happens when a real model returns a patch with the fences mismatched,
ignores the four-section format, invents a file path, or takes ninety seconds to answer.

### What a run must cover

1. `roster_profile: full` against a real model — all twelve roles enabled.
2. Ollama routing.
3. OpenAI-compatible routing.
4. Anthropic / agent CLI routing, where supported.
5. All twelve roles reached through **legitimate production triggers**, not by direct invocation.
6. The deterministic scenario set from §1, re-run live.

### What a run must record, per scenario and per role

| Field | Why |
|---|---|
| provider and model, with version | "it worked" is not portable across a model upgrade |
| prompt and completion tokens | the cost of the colony, per role, is otherwise a guess |
| cost | the same number in the operator's currency |
| wall time, per task and per mission | timeouts are only tunable against real latency |
| failure class per failure | `FailureClass`, not prose — so live results join the same taxonomy |
| which trigger reached each role | proves production triggers, not that the harness called it |
| artifact ids produced and consumed | `MissionReconstruction` should replay a live mission too |

Provenance already carries most of this per artifact as of v0.3.8.57 — provider, model, environment,
call counts. A live run should be reconstructable from the store afterwards rather than from notes.

### The recorder exists, and one field has no producer *(v0.3.8.89)*

`LiveQualificationRecord.For(memory, artifacts, evidence, missionId)` assembles that table from the
store. It was built and proved against a scripted mission BEFORE any live run, deliberately: every
field above is already persisted somewhere, so the recorder is an assembler rather than new
instrumentation, and proving it now means a live run is an operator pressing go instead of a live run
plus an argument about whether its telemetry was complete.

`LiveQualificationRecordTests` reads the table above and requires a one-to-one match with the fields
the recorder produces — a row nobody produces fails, and a field nobody asked for fails too.

### Cost has a producer now, and it is the operator *(v0.3.8.90)*

At v0.3.8.89 cost was the one field on this table with no producer at all: `ModelRouter` recorded
tokens and nothing converted them to currency. `ModelPricing` is that converter, and it is
configuration rather than code because a rate compiled into this repository would be wrong for
somebody on the day it shipped and wrong for everybody eventually.

Set it in `config.json`:

```json
"model_pricing_currency": "USD",
"model_pricing": {
  "ollama/*":            { "input_per_million": 0,    "output_per_million": 0 },
  "openai/gpt-4o-mini":  { "input_per_million": 0.15, "output_per_million": 0.60 }
}
```

Keys are `provider/model`; `provider/*` prices a whole provider and is how a LOCAL run becomes a
measured zero rather than an unknown. The colony does not assume that itself — "local models are
free" is a claim about somebody's hardware and electricity, not a fact this process can observe, so
the operator states it and the record then reports it as measured.

**A run is priced only when every model it used has an entry and every call reported usage.** The
three refusals are distinguishable on purpose, because they are three different things to do about
it:

| what the record says | what it means | what to do |
|---|---|---|
| no price table is configured | `model_pricing` is empty | write one |
| provider X reported no token usage | the provider is silent; unknown is not zero | nothing — a provider fact |
| the table does not cover: X | one model served calls and has no entry | add an entry, or `provider/*` |

The rule underneath all three: **a partially priced run is not priced.** Pricing the covered models
and omitting the rest produces a total lower than the run's real cost, wearing a decimal point and a
currency symbol, with nothing to tell the reader it is partial. An absent figure prompts a question;
an understated one does not.

**R4's exit gate can now be met on this field** — for a run whose provider reports usage and whose
models the operator has priced. It is not met by the recorder existing.

Two other fields stay unmeasured in a scripted run and are expected to be measured live: token counts
(the scripted provider reports no usage, which several real providers also do — absent stays absent,
never zero) and the provider's model VERSION, as distinct from its name.

### Recording the result

Add a dated section below. Do not edit this document to say a run happened without one.

<!-- LIVE-QUALIFICATION-RESULTS -->

_No live qualification has been recorded._

---

## 4. Why this document exists

The repository has had a `QA-CHECKLIST.md` (manual, operator-facing) and `TRAINING_MISSIONS.md`
(scenario material) for several releases, and `PLAN.md` Stage F and `AUTONOMY-10.md` Phase 5 both ask
for qualification without either of them owning the distinction between the two kinds. The result was
predictable: "qualification" reads as done because a suite passes, and the live half — the half that
would find what scripted answers cannot — has never been scheduled because nothing said it was
missing.

Saying it is missing is the deliverable here. A gap written down gets scheduled; a gap that is merely
absent gets mistaken for finished work.
