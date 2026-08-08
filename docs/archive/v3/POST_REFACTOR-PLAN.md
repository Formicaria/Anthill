> **ARCHIVED — not current.** Superseded by `docs/PLAN.md` at v3.8.24. Its EXECUTION STATUS measurements, the v3.8.21 verification-defect post-mortem and the roster survey were folded into PLAN.md sections 2, 4 and 6.
>
> The current plan is [`docs/PLAN.md`](../../PLAN.md). This file is kept as the historical
> record and is no longer maintained; nothing here should be treated as describing the
> shipping colony.

---

===========================================================
OPERATION ANTHILL
POST REFACTOR STAGING
===========================================================

-----------------------------------------------------------
EXECUTION STATUS  (added v3.8.19 — measured, not estimated)
-----------------------------------------------------------

This document is a DIRECTION. It has no measurements, no file names and no acceptance criteria, so
it cannot be executed against directly — the refactor plan's habit of surveying first is what this
section adds. Measured against the tree at v3.8.19:

  S1  Foundation            LARGELY DONE by the refactor (v3.8.3-v3.8.18). Dependency inversion,
                            standardized interfaces, the event bus and the duplicate-logic sweep all
                            shipped. Open: 9 /events/json pollers in app.js, config standardization
                            (166 public statics on AnthillRuntime), a per-subsystem test audit.

  S2  Persistent workers    PARTIAL. workers + task_attempts persist with leases and heartbeats;
                            skills persist separately. MISSING: reputation, confidence, efficiency,
                            preferred task types — the workers table has six columns and none is a
                            score. BLOCKED BY S5: a score learned before artifacts is a score
                            learned from prose.

  S3  Colony memory         DONE for storage, DONE for retrieval as of v3.8.19. 32 tables held the
                            data and exactly two methods read it back; SqliteMemory.Recall.cs now
                            answers the four questions the stage names.

  S4  Pheromone layer       HALF DONE (v3.8.19). Time decay ships — trails had never faded in the
                            project's history. MISSING: the typed vocabulary (SUCCESS, FAST_PATH,
                            UNSTABLE...); trail_type is a free string. BLOCKED BY S5 for the same
                            reason as S2.

  S5  Knowledge graph       PRODUCERS SHIPPED (v3.8.20). The stores are no longer empty: an ant's
                            declared AntArtifacts become first-class rows at SaveTaskResult, and
                            deterministic evidence is recorded at the tool chokepoint. Five of the
                            seven kinds ants emit mapped onto schemas declared before the bridge.
                            STILL OPEN, and it is the real work: the six CORE ants emit
                            AntArtifact("text") — prose with a label. Typing them means giving their
                            output STRUCTURE, per ant, not mapping a name; relabelling prose as
                            `change_plan` is the "two channels, one wins" failure ADR-004 rejects.
                            And artifacts are not yet the INTERCHANGE — tasks still read the previous
                            task's prose. That last step is roadmap v3.9.0 proper.

                            Finding worth carrying: VerificationRunner, which owns BuildVerifier and
                            TestVerifier, HAS NO PRODUCTION CALL SITE — tests construct it and
                            nothing else does. The verification framework is a well-tested subsystem
                            that never runs. Activating it is its own piece of work.

                            v3.8.21 gave it that call site. v3.8.22 had to fix it — see below.

  S6-S15                    NOT STARTED. S10 (distributed) and S15 (self-sustaining) are end states
                            rather than work items and should not be scheduled as phases.

-----------------------------------------------------------
THE v3.8.21 VERIFICATION DEFECT  (added v3.8.22)
-----------------------------------------------------------

Recorded at length because the SHAPE of this mistake is more valuable than the fix, and because it
is the second time in three releases the same shape appeared.

v3.8.21 wired VerificationRunner into ProcessPatchProposals and reported patch verification as
working. An external review found it was not, on three counts:

  1. The planner emits task type `patch_proposal`. VerificationPolicy is keyed `code_patch`. Nothing
     mapped them, so For() returned its unknown-type fallback — `security_policy` alone. The two
     DETERMINISTIC verifiers never ran on a single patch.
  2. The request was built with neither ChangedPath nor content. DiffVerifier's first branch answers
     that with "no changed path supplied — nothing to verify" and a FAIL, so fixing (1) alone would
     have turned a silent pass into a universal failure.
  3. bundle.Promotable was written to an event row and read by NOTHING.

Each alone made the wiring decorative. Together they produced something worse than no wiring: an
event row asserting that verification had run, over a patch that had not been examined, on a mission
that could still reach completed_verified with code that does not compile.

THE SHAPE: a check that answers a question ADJACENT to the one asked, and passes. v3.8.18's sign-off
review found five instances of it. This is the sixth. The tell each time is a test that constructs
its own inputs from the vocabulary of the thing being tested rather than from the vocabulary of the
thing that CALLS it — here, tests that passed the literal string "code_patch", which is a task type
production never produces. A test written from the callee's dictionary can only ever prove the callee
is self-consistent.

THE RULE ADDED: a test for a production wiring must be keyed to a value the PRODUCER actually emits.
DeterministicBlockTests is written that way throughout, and says so.

Also closed here: the soldier's deterministic policy block. It has computed a verdict from PolicyScan
before any model text exists since v2.19.0, summarised it in its own output as "not overridable", and
emitted it as bare rule-id strings that no downstream gate recognised. It was overridable by anything.
Both signals now set Task.DeterministicBlock, which the canonical evaluator reads as a demoting layer
beside GenerationDegraded.

-----------------------------------------------------------
ROSTER ACTIVATION — SURVEYED, DEFERRED  (added v3.8.22)
-----------------------------------------------------------

Twelve ants are registered; six specialists sit behind rollout gates. The survey that preceded the
v3.8.22 scope change found the roster is NOT six config changes away from working, and the three
"phantom" tools should not simply be added to make the inventory green:

  soldier / policy_scan            The CAPABILITY exists — PolicyScan.Scan is implemented and
                                   SoldierAnt calls it directly, in-process, bypassing the tool
                                   chokepoint. Only the wrapper is missing. Wrapping it buys real
                                   things (authorization, events, evidence at one place).

  medic / read_failure_context     Genuinely absent. MedicAnt reads mission.Tasks in memory, so it
                                   sees THIS mission's task list and not the durable attempt history
                                   (LoadMissionAttempts) or the colony's recurring failure classes
                                   (WhatUsuallyFails). A tool would add reach it does not have.

  archivist / write_memory_candidate
                                   REDUNDANT, not missing. The archivist already emits
                                   memory_candidate artifacts; ExecutionService.IngestMemoryCandidates
                                   turns them into durable events; LearningRecorder rebuilds
                                   candidates from those events. A tool would be a SECOND write
                                   channel for the same fact — the "two channels and one wins"
                                   failure ADR-004 rejects. The honest fix is to remove it from the
                                   contract and record why, not to build it.

Deferred deliberately: activating specialists sits BEHIND the verification gate holding. There is no
value in switching on more of the things a gate is supposed to gate while the gate does not hold.

The standing external review's fuller program (roughly 7-10 focused PRs) is compatible with this and
should be read alongside it: truthful runtime declarations (all twelve workers need versioned
contracts; the core six have none), ToolExecutionContext having only test call sites, five specialist
contracts claiming model usage whose handlers make no model calls, invocation MODES (medic must not
be plannable without a failure; archivist must not be plannable while the mission runs), artifacts as
the interchange rather than prose, and per-role graduation criteria. The end-to-end gates it proposes
are the right acceptance tests, and the first of them is the one v3.8.22 makes true: a broken patch
cannot become verified.

THE ORDERING IN THIS DOCUMENT IS NARRATIVE, NOT DEPENDENCY. Stage 2 (reputation) is numbered three
stages before Stage 5 (knowledge graph), but ADR-004 is Accepted and the standing peer-review
recommendation is explicit: build the evidence graph BEFORE any reputation or learning work, because
reputation learned before reproducible evidence rewards persuasive prose rather than demonstrated
work. Read the dependency order, not the numbering.


Current Architecture Goal

The colony itself is the intelligence.

Workers are specialized long-lived entities.
The Queen coordinates.
Memory belongs to the colony.
Knowledge persists across generations.
Experience changes future behavior.

The objective is no longer "run AI agents."

The objective is:

Build a self-improving autonomous engineering colony.

===========================================================
STAGE 1
Foundation Stabilization
===========================================================

Goal:
Ensure every subsystem is deterministic and modular.

Tasks

• Remove remaining legacy implementations
• Finish dependency inversion
• Standardize interfaces
• Complete event bus integration
• Eliminate duplicate logic
• Standardize configuration
• Improve logging
• Unit tests for every subsystem
• Integration testing

Success Criteria

Every subsystem can be restarted independently.

No component owns another component directly.

Everything communicates through events or interfaces.

===========================================================
STAGE 2
Persistent Worker Model
===========================================================

Goal

Workers become permanent colony members instead of temporary task runners.

Each worker contains

• Identity
• Skills
• Experience
• Reputation
• Confidence
• Efficiency
• Success history
• Failure history
• Preferred task types

Workers improve over time.

Success Criteria

Worker state survives restarts.

Workers evolve independently.

===========================================================
STAGE 3
Colony Memory
===========================================================

Replace traditional chat history with recursive colony memory.

Memory stores

Objectives

Failures

Successes

Code knowledge

Repository understanding

Infrastructure knowledge

Mission outcomes

Environmental observations

Instead of storing conversations...

Store experience.

Memory should answer:

"What has worked before?"

"What usually fails?"

"Who solved this previously?"

"What knowledge already exists?"

===========================================================
STAGE 4
Pheromone Intelligence Layer
===========================================================

Pheromones become the colony's distributed decision system.

Every completed action emits weighted pheromones.

Possible pheromone types

SUCCESS

FAILURE

FAST_PATH

HIGH_COST

SECURITY

TESTED

UNSTABLE

EXPERIMENTAL

DOCUMENTED

URGENT

Weights decay naturally over time.

Recent success becomes easier to discover.

Repeated failures become avoided.

The colony begins navigating experience rather than instructions.

===========================================================
STAGE 5
Knowledge Graph
===========================================================

Convert memory into relationships.

Example

Mission

↓

Files

↓

Functions

↓

Tests

↓

Documentation

↓

Previous Fixes

↓

Responsible Workers

Everything becomes connected.

Instead of searching text...

The colony traverses knowledge.

===========================================================
STAGE 6
Mission Planning Engine
===========================================================

Queen no longer creates simple task lists.

Instead she creates:

Objectives

↓

Strategies

↓

Mission Trees

↓

Worker Assignments

↓

Validation Gates

↓

Completion

Mission plans become dynamic.

Workers may split missions.

Merge missions.

Abort missions.

Retry independently.

===========================================================
STAGE 7
Autonomous Simulation
===========================================================

This becomes the primary learning mechanism.

Workers generate practice missions automatically.

Examples

Fix intentionally broken code

Refactor sample repositories

Deploy test infrastructure

Solve generated programming problems

Debug artificial failures

Review generated pull requests

Run thousands of simulations.

No human required.

Experience increases.

Skill ratings increase.

Failure becomes training.

===========================================================
STAGE 8
Recursive Colony Improvement
===========================================================

The colony begins improving itself.

Possible improvements

Worker specialization

Mission templates

Planning heuristics

Scheduling

Memory organization

Prompt optimization

Execution policies

Resource allocation

After every mission

Observe

Analyze

Learn

Adjust

Repeat

===========================================================
STAGE 9
Multi-Model Intelligence
===========================================================

Workers choose the best reasoning engine.

Possible models

Fast local

Deep reasoning

Code specialist

Vision

Planning

Embedding

Summarization

Selection becomes automatic.

Model routing depends on task history and pheromone confidence.

===========================================================
STAGE 10
Distributed Colony
===========================================================

Support multiple colonies.

Home Colony

Development Colony

Research Colony

Testing Colony

Production Colony

Colonies exchange

Knowledge

Pheromones

Successful strategies

Worker experience

Mission templates

Failures

Every colony contributes back.

===========================================================
STAGE 11
Environmental Awareness
===========================================================

The colony continuously observes its environment.

Repository changes

CI status

System resources

Network

Container health

Git activity

Issue trackers

Documentation

Infrastructure

The colony notices work before being asked.

===========================================================
STAGE 12
Engineering Automation
===========================================================

Complete autonomous engineering loops.

Detect issue

↓

Investigate

↓

Research

↓

Plan

↓

Implement

↓

Test

↓

Review

↓

Document

↓

Commit

↓

Open PR

↓

Monitor

Human approval remains optional depending on policy.

===========================================================
STAGE 13
Adaptive Specialization
===========================================================

Workers naturally drift into specialties.

Examples

Backend

Frontend

Infrastructure

Networking

Security

Testing

Documentation

Architecture

DevOps

AI

Confidence grows with repeated success.

Workers become experts rather than generalists.

===========================================================
STAGE 14
Collective Intelligence
===========================================================

No worker possesses complete knowledge.

The colony does.

Knowledge emerges from interaction.

Workers consult

Memory

Pheromones

Knowledge Graph

Other specialists

Mission history

Collective intelligence becomes greater than individual capability.

===========================================================
STAGE 15
Self-Sustaining Colony
===========================================================

End State

The colony continuously

Learns

Practices

Improves

Documents

Refactors

Organizes

Shares knowledge

Optimizes itself

The Queen becomes a strategic coordinator.

Workers become experienced specialists.

Memory becomes institutional knowledge.

Pheromones become intuition.

The Knowledge Graph becomes understanding.

The colony itself becomes the intelligent system.

===========================================================
LONG-TERM VISION
===========================================================

ANTHILL is not an AI agent framework.

It is an autonomous engineering organism.

Its intelligence does not come from any single model,
worker, or prompt.

Its intelligence emerges from:

Persistent specialization

Recursive learning

Shared colony memory

Pheromone-guided decision making

Knowledge graph reasoning

Autonomous simulation

Continuous self-improvement

The colony remembers.
The colony learns.
The colony adapts.
The colony evolves.
