# ADR-001: Runtime Composition and Queen Decomposition

**Status:** Accepted — implementation targeted at v3.1.0
**Date:** 2026-07-27
**Baseline:** v3.0.0
**Roadmap:** V3 ROADMAP § v3.1.0 · North Star §6.1 (Control Plane)

---

## 1. Context

`Queen` is the colony's mission authority. It is also, today, the implementation of planning
invocation, parallel and sequential execution, task result finalisation, adaptive decision
application, patch processing, skill credit, procedural route registration, mission evaluation,
pheromone learning, and operator result composition. The class exceeds 1,300 lines and every one of
those responsibilities reaches directly for mutable static state on `AnthillRuntime`.

Two concrete costs, both observed rather than theorised:

1. **Tests cannot isolate.** Every test that touches a feature gate saves the static, mutates it,
   and restores it in `Dispose`. Ordering bugs are latent; two runtimes cannot coexist in one
   process. The `[Collection("Autonomy")]` and `[Collection("specialist-gates")]` attributes exist
   solely to serialise tests around global state.
2. **Authority diffuses.** v2.26.0 found six call sites independently deriving mission success.
   That was possible because there was no seam at which "who decides this" is answerable — the
   decision lived wherever a caller needed it.

## 2. Decision

Introduce explicit composition without introducing a second lifecycle owner.

- **`RuntimeOptions`** — immutable, constructed once from config. Replaces reads of mutable
  `AnthillRuntime` statics in new code.
- **`RuntimeProfile`** — a per-run resolved capability set (executable roles, tool grants, write
  permissions, verification policy), validated at construction by the v2.26.0
  `RuntimeConfigValidator`.
- **`MissionContext`** — immutable per-mission: constraints, workspace identity, capability grants,
  deadlines, budgets, environment fingerprint, correlation IDs. Passed explicitly; never ambient.
- **Composition root** — one place that builds the object graph. `ApiHost` stops owning runtime
  services as statics and exposes a host-scoped container.
- **Queen decomposition** behind interfaces: `IMissionCoordinator`, `IPlanningService`,
  `IExecutionService`, `IMissionEvaluator`, `ILearningRecorder`, `IResultAssembler`.

`Queen` remains the mission authority and the public facade. It delegates; it does not abdicate.

## 3. Consequences

**Accepted costs.** This is the largest mechanical refactor in the project's history and produces
zero user-visible behaviour. It touches the mission path, which is the highest-risk surface. The
v3.0.0 characterization tests exist precisely to make this safe — they pin current behaviour so the
refactor can be proven behaviour-preserving rather than asserted to be.

**Explicitly rejected: a cosmetic file split.** The review that prompted this work warned against
"a cosmetic file split that leaves hidden coupling unchanged." An extraction that leaves the
extracted service reading `AnthillRuntime` statics has moved lines, not coupling. Each interface
must take its dependencies as constructor parameters, and `MissionContext` must arrive as an
argument.

**Explicitly rejected: multiple lifecycle owners.** Decomposition must not produce two components
that both believe they finalise a mission. The v2.26.0 canonical `MissionEvaluation` is the model:
`IMissionEvaluator` computes it once, everything else consumes it.

## 4. Verification

- Two runtime instances execute tests in the same process without configuration leakage.
- `[Collection]` serialisation attributes become removable for the tests they were added for.
- Characterization tests pass unchanged across the refactor — that is the definition of done.
- Restart, cancellation, and STOP suites remain green.
- The call-site audit stays clean: extracted interfaces must have production consumers, not just
  declarations.
