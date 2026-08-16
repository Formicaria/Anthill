# ANTHILL Ant Execution Framework

Canonical reference for how ants actually execute. Source of truth: `src/Anthill.Core/Agents/`
(`AntExecution.cs`, `AntExecutorCatalog.cs`, `SpecialistAnts.cs`, `PolicyScan.cs`, `HandoffGate.cs`)
and `src/Anthill.Core/Tools/ToolAuthorization.cs`. Tests: `AntExecutionFrameworkTests`,
`ToolAuthorizationTests`, `AntExecutorCatalogTests`, per-role `*AntTests`, `HandoffAndRoutingTests`.

## Runtime classification

Every role has exactly one `AntRuntimeKind`; planner eligibility is COMPUTED, never a stored flag:

- **ControlPlane** (queen, director, planner, constraint) — orchestration/planning/policy services.
  Never mission workers, never planner-eligible.
- **DeterministicService** (inventory, network_scout, health, proxmox, storage, backup,
  security_scout, change_archivist, quartermaster) — plain C# service behavior. Never LLM-directed,
  never planner-eligible. Mission agents may consume their structured data.
- **MissionAgent** — real executors with runtime handlers and execution contracts.
- **VisualScaffold** — displayed but unimplemented. Fail closed: anything unclassified is a scaffold.

## Execution contracts

Every specialist mission agent has a versioned `AntExecutionContract`: supported task types,
required capabilities, allowed/forbidden tools, produced artifacts, allowed handoff targets, model
permission, side-effect permission, patch-proposal permission. The runtime rejects tasks outside
the contract. **No role or worker has apply permission — patch application lives only in the
queen/director approval pipeline.**

## Capability enforcement at dispatch

`ToolRegistry.RunTool` authorizes BEFORE running: unknown ant names are refused (spoofing grants
nothing), mission agents run only their dispatch allowlist, `apply_patch`/`shell_command`/
`write_text_file` are structurally forbidden to every mission agent, and specialist contracts are
enforced even before activation. Denials return structured `authorization_denied` failures, run
nothing, and land on the audit stream as `tool_denied` events.

## Structured results

Specialists build `AntExecutionResult` (typed status codes, artifacts, evidence, handoffs,
failures per the v2.9 taxonomy) and return it through a TEMPORARY compatibility adapter (tagged
JSON block inside the legacy string result) until `BaseAnt` goes structured — see spec §16.

## Bounded handoffs

`HandoffGate.Evaluate` admits a handoff task only when: destination is runtime-eligible, its
contract supports the task type, depth ≤ 2, mission has < 12 tasks, and no duplicate dedupe key
exists. Rejections carry reasons. Recursive unlimited task creation is structurally impossible.

## Rollout gates

`specialist_ant_execution_enabled` (master) AND one per-role flag (`tester_ant_enabled`, …) must
BOTH be true for a specialist to become executable/plannable. Startup validation
(`AntExecutorCatalog.Initialize`) verifies handlers/contracts and publishes per-role availability
with explicit unavailability reasons, surfaced in `/colony/graph.runtime_status` and the Ant
Inspector.

**v0.3.8.41 — the shipped default sets all of them.** `roster_profile` defaults to `full`, and
`RosterProfiles.Resolve` turns the master switch, all six per-role flags, handoff ingestion and
adaptive mission control on. The individual flags still exist and still bind: `roster_profile:
"core"` leaves every one of them false, and `disabled_roles` subtracts from whatever the profile
resolved — applied last and absolutely, because a kill switch a profile could override would not be
a kill switch.

Existing installations are migrated by `ConfigSchema` only when the on-disk configuration still
matches the untouched legacy defaults. An explicit `core`, any hand-enabled specialist, or a
configuration already at schema version 2 is preserved exactly. See `ConfigMigrationTests`.

## Role matrix

| Role | Kind | Implemented | Planner-eligible | Default | Primary task types | Execution surface | Limits |
|---|---|---|---|---|---|---|---|
| researcher | MissionAgent | yes | yes | on | research | system_info, list_directory | read-only tools |
| web | MissionAgent | yes | yes | on | external_research | web_search | read-only |
| file | MissionAgent | yes | yes | on | file_inspection | list_directory, read_text_file | read-only |
| coder | MissionAgent | yes | yes | on | code_change | model only | proposals only, no apply; a UI change cannot dispatch without a valid `ui_map` (v0.3.8.57) |
| builder | MissionAgent | yes | yes | on | build_answer | model only | — |
| verifier | MissionAgent | yes | yes | on | verification | model only | `PolicyInserted` since v0.3.8.57 — the runtime guarantees one when its evidence exists, and a planned verifier is still admissible |
| ui_cartographer | MissionAgent | yes | gated | **on** (full profile) | ui_mapping … | list_directory, read_text_file | read-only; its `ui_map` is REQUIRED before a UI coder task dispatches (v0.3.8.57) |
| tester | MissionAgent | yes | gated | **on** (full profile) | build_check, test_execution … | run_allowlisted_check ONLY | no shell, no model, evidence required; runs inside the materialized revision (v0.3.8.70) and its checks come from `CheckSource` (v0.3.8.73) |
| soldier | MissionAgent | yes | gated | **on** (full profile) | security_review … | deterministic PolicyScan | blocks not model-overridable |
| scribe | MissionAgent | yes | gated | **on** (full profile) | release_notes, docs_patch_proposal … | read summaries | docs-path patches only; refuses `verified_change_summary` when nothing verified (v0.3.8.57) |
| medic | MissionAgent | yes | gated | **on** (full profile) | failure_diagnosis … | read failure context | 2 diagnoses/mission, repeat → escalate |
| archivist | MissionAgent | yes | gated | **on** (full profile) | memory_consolidation … | emits memory candidates | positive ONLY from completed_verified |
| quartermaster | DeterministicService | n/a | never | n/a | — | — | intentionally non-executable (no metrics contract yet) |
| queen/director/planner/constraint | ControlPlane | yes | never | n/a | — | — | — |
| 8 homelab roles | DeterministicService | yes | never | n/a | — | C# services/providers | never LLM-directed |

## Where a check comes from — v0.3.8.73

`CheckSource` is the single decision function both the tester's SELECTION and
`run_allowlisted_check`'s RESOLUTION read. Precedence:

1. **Operator configuration** — `workspace_checks` in ANTHILL's own config file. Non-empty
   **replaces** detection for the installation; absent or empty changes nothing.
2. **Workspace detection** — `WorkspaceAdapters`, as since v3.5.0.
3. **The compiled catalog** — only when neither of the above says anything.

Two properties are load-bearing and neither is negotiable.

**The declarations live in ANTHILL's configuration, never in the workspace being modified.** There is
deliberately no `.anthill-checks.json`: a check file inside the repository would hand every coding
agent the power to rewrite its own exam, which is the exact separation `WorkspaceAdapter` was built
to preserve. `PolicyScan.allowlist_tampering` also matches `workspace_checks`, so a patch proposing
to edit the setting is a blocking finding.

**One function, because there used to be two.** Selection read
`manifest.IsEmpty ? CheckCatalog.Ids : manifest.Checks` while resolution read
`manifest.Find(id) ?? CheckCatalog.Get(id)` — two spellings of one rule, and the runner's own comment
named the failure they invite: a tester selecting an id the runner then refuses.

A built-in id (`dotnet_build`, `dotnet_test`, `dotnet_version`) cannot be redefined by configuration.
Those names appear in the auto-apply verify path, the graduation record and the changelog; keeping
the name while changing the command is how a report describes a check that did not run.

## Outcome semantics

`completed_verified` is the only positive learning signal. Completed-unverified, partial,
timed-out, and failed never reinforce positively; cancellation is neutral. Enforced in
ArchivistAnt and its tests.

## Deferred (per NORTH_STAR phases) — all since shipped

Everything this section deferred has landed: sandboxed execution (v2.10.x), independent
verification/evidence (v2.12.0, hardened v2.26.0 — Promotable intrinsically requires
deterministic evidence), skill certification (v2.13.0, durable v2.21.0, row-atomic v2.26.0),
safe-action recovery orchestration (v2.14.0, executor migration v2.25.0), scheduler-side live
handoff ingestion (v2.21.0), and structured returns for every ant — specialists in v2.19.0
(adapter deleted) and the five core ants in v2.26.0, each declaring typed outcomes.
