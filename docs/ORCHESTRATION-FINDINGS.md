# Mission orchestration — where each behaviour actually lives

Investigated 2026-09-03 against `main` at `fcf12a7` (v0.3.8.117), by reading the tree rather than
reasoning about it. Every claim carries a file:line citation. This is the map `.118`'s remaining
slices are built from; `docs/PLAN.md` §2e carries the work items, this carries the evidence.

Three findings changed what the work should be, and they are marked **▲** below. Two of them mean a
planned fix was aimed at the wrong thing.

---

## 1. A user's message becomes `Mission.Goal` verbatim — except on one path

Four human-facing paths, all preserving the text:

| Path | Where | Transformation |
|---|---|---|
| conversation turn | `ConversationRunner.cs:406-496` (`ComposeMissionGoal`) | **appends only** — project framing, ≤6 prior turns (4000 chars), attachments (8000 chars); every truncation announced inline (`:489`) |
| `POST /missions` | `ApiHost.Routes.cs:516-547` | `.Trim()` |
| `POST /agent/run` | `ApiHost.Routes.cs:566-644` | `.Trim()` — but this creates a `Mission` row for audit only and runs `ToolCallingLoop`, **never** the intake/planning pipeline |
| CLI | `Program.cs:131-145` | `String.Join(" ", args).Trim()` |

`MissionIntake.Resolve` classifies but never rewrites: `OriginalRequest` is a separate field on
`MissionSpecification` (`MissionIntake.cs:548-552`), and `Mission.Goal` keeps the composed string.

**▲ The exception, and it is a real one.** For standing objectives (`MaxRuns != 1`),
`Strategist.GenerateGoal` sends the operator's charter to a model and **the model's output becomes
`Mission.Goal`** (`Strategist.cs:74-76, 96-115`). The file's own comment names the failure mode:
*"Letting the LLM Strategist 'diversify' them is pure drift (it once rewrote a file-creation charter
into 'train a model')."* There is a fallback to the verbatim charter on parse failure, empty result
or near-duplicate — but **no check that the rewrite still says what the charter said**. One-shot
objectives (`MaxRuns == 1`) use the charter verbatim (`Strategist.cs:56-58`) and are unaffected.

---

## 2. Cross-mission context is prose with the ids thrown away; within-mission context is typed

This asymmetry is the context-loss finding, and it is sharper than "summaries are passed around".

**Across missions — prose, no provenance.** `PlanningService.CreatePlan` (`:85-101`) builds a
`memoryContext` string from `FormatRecentMemory` (3 missions) + `FormatRelevantMemory` (5 missions),
each result truncated to 400 chars, plus `FormatPheromoneContext(8)` — straight into the planner
prompt (`Planner.cs:157-168`). The formatted blocks carry `Goal / Status / Pheromone Score /
Result Summary` and **no mission id at all** (`SqliteMemory.Operations.cs:1299-1301, 1313-1315`).
`ResearcherAnt` independently re-fetches the same three blocks (`Ants.cs:172-174, 216-219`) and
truncates again to 7000 chars.

**Within a mission — typed, with ids.** `ArtifactContext.Compile` (`:94-230`) emits
`id: … schema: … producer: …` per artifact (`:192-195`), and `BuildContextPacketText` carries
`Task ID / Ant / Task Type / Status` per block (`DomainHelpers.cs:122-123`).

So a worker can cite what another task in the same mission produced, and cannot cite anything the
colony learned before this mission started. `ResearcherAnt.WithRecallRecord` (`Ants.cs:315-345`)
attaches a `recall_set` artifact with mission ids — but **after** the model call (`Ants.cs:285`), so
it is a citation record for a later integrity check, not something the model read. The planner's own
memory injection has no equivalent at all.

**Present-but-unused:** `WorkerCapabilities.RecallMissionHistory` is declared
(`MissionIntake.cs:610`) and one worker declares it (`AntRegistry.cs:386-387`), but no
`MissionSpecification` lists it in `RequiredCapabilities` and `EnsureClassCoverage` never sets it —
so the deterministic resolution basis can never select `mission_researcher` for that reason
(`WorkerResolution.cs:71-83`). The recall that does happen happens unconditionally in prompt-building
code, independent of the capability.

---

## 3. Task selection and the section-analysis trigger

`Planner.CreateTasks` (`:113-260`). Three sources — spec-ingestion, model-planned, static fallback —
all funnelled through `EnforceConstraints` → `EnsureClassCoverage` → `AssignDefaultWorkers`
(`:128,130,253,258`). `EnsureClassCoverage` (`:334-596`) is a per-`MissionClass` switch that
**inserts fixed tasks regardless of what was proposed**.

The section-analysis gate is one line: `Planner.cs:128` calls `IsLongInput(goal)`, which is
`goal.Length > AnthillRuntime.LongInputThreshold` — 6000 characters (`AnthillRuntime.cs:363`) — and
nothing else. `CreateSpecIngestionTasks` (`:910-970`) then hardcodes
`researcher.mission_researcher` (`:930`), `builder.result_compiler` (`:949`),
`verifier.result_verifier` (`:963`).

**A detailed workflow request is by construction a long input.** Precision was punished.

---

## 4. Worker output acceptance fails closed — but two paths downgrade instead of failing

`TaskOutcomeMapper.Map` (`:57-104`) is the single chokepoint and is correct:

```csharp
private static readonly HashSet<string> Completing = new(StringComparer.Ordinal)
    { "succeeded", "succeeded_with_warnings" };
...
default:
    // Fail closed. An unrecognised code must never complete a task by default.
    return new(TaskOutcomeAction.Fail, false, "unknown_status", ...);
```

`Task.Result` is set at `ExecutionService.cs:749, 822`; the branch is at `:841-846`.

Worth knowing: **unparsed researcher and builder output becomes `succeeded_with_warnings`, not a
failure** (`Ants.cs:286-293, 1371-1381, 1413-1422`) — warning `unstructured_research_output`. And
**artifact schema conformance is a report, not a gate**: `ArtifactSchemaCheck` says so in its own
header — *"NOTHING HERE REFUSES A WRITE"* (`:21-25`) — and a non-conforming payload is stored with
only a best-effort `artifact_schema_violation` event (`SqliteMemory.Artifacts.cs:95-125`).

The coder is the one role that refuses to degrade: no model means `Failed(TransientProviderFailure)`
(`Ants.cs:843-845`), *"because a patch proposal invented without the model would be worse than none."*

---

## 5. An artifact id resolves to an EXCERPT, and a missing one is told only to the model

`ArtifactContext.Compile` (`:94-230`) is the resolver. `:211`:
`var excerpt = TextUtil.Truncate(artifact.Payload, maxItemChars, "...[artifact truncated]");` —
1200 chars by default, with the whole block capped at a quarter of the prose budget
(`DomainHelpers.cs:158`).

Unresolved ids **are** distinguished from absence (`:144-149` separates `missing` / `withheld` /
`ordered`) — but the report goes into the prompt text only (`:174`), and **there is no `LogEvent`
anywhere in that file**. A declared input that could not be resolved is disclosed to the model and to
nobody else. A store read failure returns `""` and writes to `Console.Error` (`:102-109`).

The soldier's separate resolver is stricter still: `SpecialistAnts.cs:625-626` filters unresolved ids
with `.Where(a => a is not null)` and reports nothing at all.

`BuilderAnt.ResolveInputs` is the counter-example that shows it can be done —
`unresolved_creation_inputs` rides on the task's own warnings and is therefore persisted
(`Ants.cs:1390-1402`).

---

## 6. ▲ `checks: 0` counts something other than what it looks like

**There are two unrelated evidence concepts, and the operator-facing number reads the weaker one.**

- `IEvidenceStore` (`IArtifactStore.cs:74-89`) — durable rows with `Kind / Deterministic / Passed /
  TaskId / RevisionId`. Written by the tool chokepoint (`Tools.cs:408-438`) and the patch
  verification bundle (`ExecutionService.cs:1390-1400`). **This is what the verification gates read.**
- `MissionReport.Checks` (`MissionReport.cs:38, 105-124`) — counts per-task `AntEvidence` rows of
  kind `"check"`, which exactly one place writes: `TesterAnt` (`SpecialistAnts.cs:220-228`).

So `checks: 0` means *no tester task dispatched `run_allowlisted_check`*. It does **not** mean the
evidence store is empty — that is `evidence_rows`, a separate and correctly-sourced number
(`MissionReport.cs:169, 278`). `MissionReport.Checks` is **display-only and gates nothing**: its only
consumer is `Render` itself.

**Present-but-unused:** `IEvidenceStore.HasDeterministicPass` is declared and implemented
(`SqliteMemory.Artifacts.cs:315-317`) and called **only from tests** — no production gate uses it;
`EvidenceVerdict.For` and `MissionVerification.EvidenceIdentitySatisfied` re-derive the same fact
from `.ForMission`.

---

## 7. ▲ Verification is stronger than the brief assumed — the leak is upstream of it

This is the finding that redirects the work. `VerifierAnt` asks the **store first** and downgrades a
model-only PASS (`Ants.cs:1642-1725`):

```csharp
var verdict = read.StoreFailed ? VerificationVerdict.Unavailable
    : fromEvidence?.Verdict
      ?? (modelWroteIt && VerificationVerdict.IsPass(fromProse) ? VerificationVerdict.Unknown : fromProse);
```

`EvidenceVerdict.For` (`:51-81`) counts only `deterministic == true` rows; a store full of
`model_review` rows returns `Unknown`, and `IsPass(Unknown)` is false. `MissionEvaluator.Evaluate`
(`:136-139`) distinguishes **NotRun** (no verification task at all) from **Failed** (ran, did not
pass). `Resolve` (`:442-446`) then requires `Verification.Passed` AND deliverable satisfied AND not
generation-degraded AND no deterministic block.

**Prose alone cannot produce `completed_verified` in the wired configuration.** The one gap: a
store-absent CLI/offline build falls through to `fromProse`.

**So the defect is not that verification is weak — it is that `mission.Status` is computed without
ever consulting it.** `Queen.cs:1299-1302`:

```csharp
var criticalFailed = mission.Tasks.Any(t => t.Status == TaskStatus.Failed && t.Critical);
var degraded = mission.Tasks.Any(t => t.Status == TaskStatus.Skipped || (t.Status == TaskStatus.Failed && !t.Critical));
mission.Status = criticalFailed ? Failed : degraded ? Partial : Complete;
```

No reference to evidence, checks or the verifier. `MissionOutcome.IsPositiveSuccess`
(`MissionOutcome.cs:67-68`) already treats **only** `completed_verified` as success, and
`completed_unverified` is documented as *"Work finished but the required evidence is absent. Not a
success."* The semantics are right one layer down; the structural `Complete` computed without
evidence is what surfaces.

---

## 8. ▲ The planner's provider fallback is invisible to the mission record

`Planner.CreateTasks` substitutes a static keyword-matched plan on **four** conditions: no model
configured (`:130`), a non-Ok model call (`:230-234`), a rejected plan (`:241-248`), and any parse
exception (`:255-258`). Every one is recorded **only** by `Console.Error.WriteLine` — no `LogEvent`,
nothing in the durable event or evidence trail. The file's own comment (`:222-226`) says it best:

> *"The planner's silent failure is the worst of the four this release wired, because it does not
> look like a failure… An operator sees a colony that ignored their goal, with a green run behind it."*

Two ant-level fallbacks are equally silent, and these are **presented as ordinary successes**:
`ResearcherAnt` when configured offline returns a static local-context summary with `StatusCode
"succeeded"` and no warning (`Ants.cs:236-241`); `BuilderAnt` likewise (`:1275`);
`WebResearchAnt.SummarizeSource` falls back to a truncated snippet as the "summary" with no warning
at all (`:610-624`). Provider *failure* (as opposed to configured-offline) is handled better —
`SucceededWithWarnings` carrying `provider_failure[...]`.

`ResultAssembler.ComposeFinalAnswer` is the counter-example: it logs `answer_synthesis_failed`
before falling back (`:338-344`).

---

## What this map changes about the remaining work

1. **Verification does not need rebuilding** (finding 7). It needs to be *consulted* by the closure
   decision. The fix is at `Queen.cs:1299-1302`, not in `VerifierAnt`.
2. **`checks: 0` is a mis-sourced display number** (finding 6), not evidence of missing verification.
   Fixing the label without fixing `MissionReport.Checks`'s source would move the lie rather than
   remove it.
3. **Silent fallbacks are the highest-value unrecorded event** (finding 8). Three call sites present
   substitution as success; the planner's is invisible even to the event log.
4. **Artifact resolution failures need a record, not a redesign** (finding 5) — the distinction
   already exists in `Compile`, it simply never leaves the prompt.
5. **The Strategist rewrite is an unguarded lossy step** (finding 1) that nothing downstream can
   detect, because the rewritten goal is indistinguishable from an operator's own words.
