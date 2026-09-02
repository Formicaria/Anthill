# ANTHILL Training Missions — Fresh-Install Bootstrap (v0.3.8.55)

Status: Active operator guide. Part of the master roadmap — the current plan is `docs/PLAN.md`;
the historical staging this pack originated from is in `docs/archive/v3/NORTH_STAR.md`.

A fresh ANTHILL install starts with an empty memory and no pheromone trails. This pack is a
repeatable, **read-only** curriculum the colony runs against its own repo and runtime so that later
real patch missions start with memory of the repo structure, role boundaries, validation rules, and
roadmap direction — instead of rediscovering them mid-mission.

**Hard rule: every training mission is read-only.** Each goal below deliberately contains the
phrases `read-only`, `do not modify files`, and (where applicable) `one-shot`. These are parsed by
`MissionConstraints`, which strips coder patch-proposal tasks at planning time and lets the mission
end cleanly instead of looping. Training missions must never create patch proposals. If a training
mission ever produces a patch proposal, reject it and file an issue — that is a
constraint-enforcement regression (`LifecycleAndConstraintTests` covers it).

---

## How to run the pack

1. Install and start ANTHILL (see `docs/DEPLOYMENT.md`), open the console at
   `http://localhost:8713/ui`.
2. Submit the missions below **in order**, one at a time, from **Chat** (each message becomes a
   mission under your approval policy), or enable the Mission Command widget on the Colony
   Overview (Widgets menu) and submit from there. Paste each goal exactly — the constraint
   phrases matter.
3. Where plan preview is available, confirm the plan contains **no coder patch tasks** (the
   constraint banner should show the mission as read-only/no-patch).
4. After each mission, open the mission report and skim the final result for obvious nonsense —
   a wrong lesson stored in memory is worse than no lesson.
5. When the pack is done, check Tools → Memory & Signals: each mission should have left a
   mission record, sources, and a positive pheromone trail. The Colony Overview's map shows the
   trails as drift once memory accumulates.
6. Re-run the pack after major version jumps, or whenever memory has been cleared
   (Settings → Maintenance — clearing missions wipes mission history; training restores it).

Time cost is model-dependent; each mission is a normal multi-task mission (research → build →
verify, with policy-inserted reviews where applicable). Run them during idle time.

---

## The twelve training missions

### 1. Repo Orientation

```text
One-shot, read-only training mission, do not modify files: map the ANTHILL repository for future
missions. Produce a concise project map covering: the solution layout (src/Anthill.Core,
src/Anthill.Api, src/Anthill.Cli, src/Anthill.SDK, src/Anthill.Modules, tests/Anthill.Tests,
native/anthill_kernel), the runtime flow from CLI/API entry to Queen to Planner to ants to memory,
where the HTTP API endpoints are registered, where the embedded web UI lives (src/Anthill.UI —
index.html plus the split console scripts app.js, themes.js, inspector-routing.js, homelab.js,
mission-thread.js, dashboard-grid.js, colony-topology.js, colony-live.js), where tests live, where configuration is read (config.json,
AnthillRuntime, AnthillConfig), the deploy options (Windows desktop, Docker, LXC), and where
version markers live (AnthillRuntime.Version, Directory.Build.props, README, CHANGELOG, PLAN).
Summarize as a structured reference document in the final result.
```

### 2. Ant Role Training — the full twelve

```text
One-shot, read-only training mission, do not modify files: document the colony's ant registry and
routing rules as they stand with the full roster enabled by default. List every contracted role —
researcher, web, file, ui_cartographer, coder, builder, verifier, tester, soldier, medic, scribe,
archivist — and its workers, what each is allowed and forbidden to do, each role's scheduling mode
(planner-selectable, policy-inserted, failure-triggered, post-finalization), which permission or
constraint checks can reject a task, and how per-role model routes are set in the Ant Inspector
(Colony → Ant Inspector — one box per role). Note explicitly: no ant can apply patches directly;
patches always go through approval; tester and soldier are inserted by policy on every
state-changing patch set, never trusted to a planner's memory.
```

### 3. Build/Test Workflow Training

```text
One-shot, read-only training mission, do not modify files: document the ANTHILL build and
validation workflow. Cover: the required validation commands and the scripts/validate.sh and
scripts/validate.ps1 entry points, the test projects and the regression guard tests (version-marker
consistency, migration idempotence, UI glyph integrity, console asset split guards, UTF-8 pipe
decoding), what CI runs on every pull request (build matrix, publish + selftest, Docker smoke
test, ui-integrity, repo-guards), the release flow (tag push triggers the Release workflow, tag
must match AnthillRuntime.Version on the tagged commit), and the anthill --qualification command
that verifies a deployment can actually run missions.
```

### 4. UI Structure Training

```text
One-shot, read-only training mission, do not modify files: document the embedded console UI so
future UI missions modify it safely. Cover: the vanilla HTML/CSS/JS architecture of
src/Anthill.UI (no React, no Tailwind, no build step; index.html plus split console scripts, each
pinned as an embedded resource and served same-origin under CSP script-src 'self' with no inline
scripts), the CSS-variable theme system (default palette is the formicaria.us website's; themes
re-state variables on html[data-theme] and are chosen in Settings), the five-destination IA
(Colony, Projects, Chat, Tools, Settings) and how routes alias when pages move, which API
endpoints the UI calls, the known encoding hazard (icon glyphs flattened to '?' when a file is
saved as non-UTF-8, guarded by CI ui-integrity), and a safe-modification checklist: additive
changes only, preserve existing pages and routes, keep UTF-8, keep vanilla JS, no inline scripts,
clean up timers, respect reduced-motion.
```

### 5. Memory + Pheromone System Training

```text
One-shot, read-only training mission, do not modify files: document ANTHILL's memory and pheromone
system. Cover: the SQLite schema families (missions, tasks, events, sources, patches, approvals,
objectives, pheromones, users, providers), how mission history is written and searched, how
pheromone trails strengthen only from verified outcomes and decay toward neutral when unused, how
the archivist turns the canonical mission evaluation into memory candidates exactly once per
evaluation (the finalization ledger refuses double claims), how Tools → Memory & Signals reads
them, the maintenance operations (backup retention, flush cache, clear missions) and what each
deletes or preserves, and how memory context is injected into future mission planning.
```

### 6. Patch Proposal Discipline

```text
One-shot, read-only training mission, do not modify files: document the patch lifecycle and its
safety rules as operating discipline for future code missions. Cover: patch proposal creation by
coder ants, risk scoring, the approve-then-apply model, materialization into an isolated mission
workspace (the live tree is never written during verification), policy insertion of tester and
soldier on every state-changing patch set, the verifier's binding to their stored evidence rather
than to model prose, the audit trail, auto-apply gating (fail-closed, allowlisted, configurable)
and its git integration (standalone branch, never main), duplicate/superseded patch handling, and
the unsafe patterns to avoid: applying without approval, deleting instead of editing, bypassing
the Queen or ApprovalGuard, and proposing patches on verification-only missions.
```

### 7. Failure Drill

```text
One-shot, read-only training mission, do not modify files: walk through a simulated CI-failure
incident to learn the diagnosis workflow. Scenario: the CI build fails on main after a merge.
Document, step by step, which information to collect first (failing job, failing step, error text,
recent commits), which ant roles participate in diagnosis, how to reproduce locally with
scripts/validate, how to distinguish product bugs from CI/environment flakes, what a minimal fix
proposal looks like versus overpatching, and how verification confirms the fix before the incident
is closed. Do not take action - this is a tabletop exercise producing a written runbook.
```

### 8. The Repair Loop Drill

```text
One-shot, read-only training mission, do not modify files: document the colony's bounded repair
loop as a tabletop exercise. Cover: what makes a task failure retryable versus permanent, how a
failed tester check hands off to the medic (failure-triggered scheduling), how the medic's
diagnosis can hand back to the coder for a fresh proposal generation, why every repair generation
materializes fresh evidence rather than reusing the last generation's, the bounds (repair cycles
and handoff depth) and why the adaptive stop message says the bound is spent rather than the
problem solved, and what an operator should check when a mission ends in adaptive_stop. Produce a
short runbook in the final result.
```

### 9. Roadmap Training

```text
One-shot, read-only training mission, do not modify files: internalize the master roadmap.
Summarize from docs/PLAN.md and docs/AUTONOMY-10.md: the measured current state of the twelve-role
program, the permanent OBSERVE-DIAGNOSE-PROPOSE-RISK-APPROVAL-EXECUTE-VERIFY-LOG-LEARN rule, the
non-negotiable safety rules (no destructive infrastructure actions, no secrets in logs,
kill-switch files per role, evidence over prose), the architecture rules (local-first,
deterministic C# for orchestration, LLM ants only for judgment, additive APIs), which acceptance
gaps remain open (the live twelve-role mission against a real model), and which phases are marked
shipped versus future. Store this as durable direction for future mission planning.
```

### 10. The One Lane

```text
One-shot, read-only training mission, do not modify files: document why the colony has exactly ONE
working lane. Every operator message becomes a mission: Queen-planned, policy-reviewed,
verifier-bound, memory-earning. Cover the lane that used to exist beside it and why it was removed
— chat turns under Bypass or Automatically-approve wrote to the operator's live tree, were captured
as direct_change artifacts marked not colony-verified, and were structurally excluded from learning.
Explain why "capture the unverified work and label it" was the wrong fix: a lane whose output must
be quarantined is a lane doing work outside the colony, and the label was the receipt. Cover what
the approval policy governs now (whether a MISSION needs a confirmation card, never whether the chat
box may edit files) and why the two were conflated. Produce the explanation as an operator-facing
reference.
```

### 11. Workspace & Project Discipline

```text
One-shot, read-only training mission, do not modify files: document how projects, working
directories, and mission workspaces relate. Cover: a project with no working directory stands in
ANTHILL's own source checkout by default (direct source access, labelled as a default) until the
operator sets one from the Files tab; every project owns its own tree under the shared projects
root once set; the colony's own source rides as reach on every conversation for self-improvement
work; each mission that changes files works in an isolated checkout named after its mission goal;
and nothing in a mission workspace touches the live tree until a change is approved. Summarize as
a reference the colony can recall when asked where work happens.
```

### 12. Daily Memory Compression

```text
One-shot, read-only training mission, do not modify files: compress recent mission history into
durable operating lessons. Review the most recent missions, tasks, failures, and pheromone trails;
extract at most ten short, generally applicable lessons (what worked, what failed and why, which
workflows or wordings produced clean results); discard noise and one-off details; and produce the
lessons as a concise numbered list in the final result so they persist in mission memory as
searchable guidance.
```

---

## The memory-compression pattern (recurring)

Mission 12 is also the template for ongoing memory hygiene. Two ways to run it on a cadence:

- **Manual:** re-submit mission 12 daily or after every 10–20 real missions.
- **Objective:** create an automation objective (Projects page, Automation panel) with the same
  charter text minus `one-shot` (keep `read-only` and `do not modify files`). The objective
  lifecycle runs it as a recurring verification-style objective that ends each run cleanly instead
  of looping. Keep its priority low so it never competes with real work.

Compression keeps memory searchable and keeps pheromone context small and high-signal — prune raw
history with the Maintenance controls once its lessons have been compressed.

---

## Success criteria

- A fresh install can run the whole pack without modifying a single file and without creating any
  patch proposal.
- Afterward, mission memory contains: a repo map, the full twelve-role boundaries, the validation
  workflow, UI safety rules, the memory model, patch discipline, an incident runbook, the repair
  loop, the two-lane rule, workspace discipline, and roadmap direction.
- Pheromone trails reward the safe read-only workflow patterns the pack exercised.
- Future patch missions plan faster and route more accurately because that context is retrievable.
