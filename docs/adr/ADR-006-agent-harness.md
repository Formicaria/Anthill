# ADR-006 — Anthill as an agent harness: architecture assessment and roadmap refactor

**Status:** Accepted — v3.3.0 and v3.4.0 shipped against it
**Roadmap:** V3 ROADMAP § v3.3.0, § v3.4.0 · North Star §6 (Planes)
**Supersedes:** the ORDERING (not the content) of the v3.2.0 remainder and the phase formerly
numbered v3.3.0, which is now § v3.5.0. Numbered 006 rather than 003: `ADR-003-worker-protocol.md`
already held that number, and two ADR-003s is the same defect as two v3.5.0 roadmap phases.

## What changed

The goal moved from "a management dashboard for a swarm" to "a general-purpose agentic AI harness":
provider-agnostic, capability-aware, tool-centric, self-improving, with the dashboard and missions
as *capabilities* rather than the point.

This ADR records what already supports that, what blocks it, and the one sequencing decision that
follows — written before building anything, because the honest answer changed which item comes
first.

## What already fits (do not rebuild)

The survey found more foundation than the vision statement assumes:

| Vision requirement | What exists today |
|---|---|
| Provider abstraction | `IModelClient` with four implementations (Ollama, OpenAI-compatible, Anthropic, Placeholder) |
| Multiple providers/models, keys, endpoints | `ProviderCatalog` (Ollama, OpenAI, Anthropic, Perplexity, OpenRouter) + persisted provider config |
| Routing policy, failover, health | `ModelRoutingPolicy`, `ModelCircuitBreaker`, provider health/latency records |
| Cancellation | `ModelCallScope` — ambient async-local token linked into every HTTP request |
| Typed results, no prose control flow | `ModelCallResult` / `ModelCallOutcome`; v3.2.0 removed the last `"ERROR:"` prefix test |
| Self-describing tools | `ToolDescriptor`: name, description, required capabilities, side-effect class, risk class, idempotency, cancellation/timeout support |
| Per-role capability contracts | `AntExecutionContract` (six specialists today) |
| Agent runtime | Missions: queues, dependencies, retries, budgets, scheduling, persistence |

Adding OpenRouter/LM Studio/vLLM/llama.cpp is mostly `ProviderCatalog` entries plus an
OpenAI-compatible base URL. That is **not** the hard part and should not be mistaken for it.

## What actually blocks the vision

One thing, and everything else follows from it:

```csharp
public interface IModelClient
{
    ModelCallResult Generate(string prompt, int retries = 2);
}
```

A **string in, string out** contract. Every capability in the directive is unreachable through it:

- **Tool / function calling** — needs a message list, tool schemas, and tool-call results coming
  back as structure. There is nowhere to put any of them. `ToolDescriptor` already carries the
  metadata a schema needs; it is simply never projected to a provider.
- **Structured output** — no response-format field, no schema.
- **Streaming** — the method returns a completed string.
- **Vision / multimodal** — no content parts, only text.
- **Embeddings** — a different operation entirely.
- **Reasoning models** — no place for reasoning effort or for reasoning content in the response.
- **Usage accounting** — tokens are not returned, so per-mission cost cannot be attributed.
- **Per-agent model assignment** — the model is chosen inside the router by role, not passed per call.

There is also **no model capability model at all** (`SupportsTools`, `SupportsVision`, context
window: none of these exist anywhere in `src/Anthill.Core/Models`). The orchestration layer cannot
"adapt to available capabilities" because nothing can currently express one.

And the transport blocks a thread per call:

```csharp
using var response = Http.PostAsync(url, content, cts.Token).GetAwaiter().GetResult();
```

Sync-over-async in `ModelRouter` and `ProviderClients`. Under parallel task execution this consumes
a pool thread for the whole generation, and it makes streaming impossible by construction.

## Roadmap conflicts, and the reversal

**1. `IAntExecutor.ExecuteAsync` — deferred earlier, now required, for a different reason.**

It was deferred (correctly, at the time) because cancellation was already solved by
`ModelCallScope`, so the only thing `ExecuteAsync` would have added was a sync-to-async adapter over
twelve synchronous ants — the adapter pattern that phase had just finished deleting.

The harness vision changes the argument. Streaming, tool-call loops and per-call model selection all
need async for reasons that have nothing to do with cancellation. The deferral stands, but its
**prerequisite is promoted**: the provider layer goes async and typed FIRST, and the ant signature
follows it rather than wrapping it.

**2. Core-ant `AntExecutionContract`s — hold, with a second reason.**

Already blocked on evidence: `ToolAuthorization` short-circuits on contract presence, so an
incomplete `AllowedTools` denies tools mid-mission. The harness adds a second: contracts should
declare the **model capabilities** a role requires (tool calling, vision, context window), which
cannot be expressed until the capability model exists. Authoring them now would mean writing them
twice.

**3. v3.3.0 "Mission Workspace and Language Adapter Infrastructure" — unchanged and still wanted.**

It is the substrate for repository awareness. It moves *after* the provider substrate, not out.

**4. Nothing in the roadmap is obsolete.** The typed-protocol work of v3.2.0 is a precondition for
the harness rather than a detour: an agent runtime whose control flow reads prose cannot be made
provider-agnostic, because every provider phrases failure differently.

## Proposed sequencing

| Version | Content |
|---|---|
| v3.2.x | Finish current phase: strict planning ✅, structured results persisted ✅, contracts at dispatch ✅. Core-ant contracts deferred to v3.4.0. |
| **v3.3.0 (new)** | **Provider substrate.** Typed `ModelRequest`/`ModelResponse` (messages, tools, model, options, usage); async transport; `ModelCapabilities` per provider+model; capability-aware routing. |
| **v3.4.0 (new)** | **Tool framework projection.** `ToolDescriptor` → provider tool schemas; the tool-call loop; capability-gated tool exposure; core-ant contracts authored *with* required capabilities. |
| v3.5.0 | Mission workspace + language adapters (was v3.3.0) — repository awareness substrate. |
| v3.6.0 | Repository indexing, semantic search, dependency graphs. |
| v3.7.0 | Conversation orchestration: chat that escalates into missions. |

## Principles this locks in

1. **The provider interface is the seam.** Capabilities are negotiated there, never assumed by
   callers. No orchestration code branches on a provider's name.
2. **Capability-aware, not capability-assuming.** A missing capability degrades deliberately (no
   tools offered) rather than failing at the provider.
3. **One transport subsystem.** Retry, failover, circuit breaking, usage accounting and telemetry
   live in it, not scattered across callers — the same rule that put `MissionConstraints.Parse` in
   exactly one place.
4. **No adapters that outlive their migration.** The v3.2.0 lesson: a compatibility shim that
   flattens a typed value destroys the information the next layer needs, and it hides for releases.
5. **Structure over prose, everywhere.** Already true of ant results and model results; it must
   remain true of tool results and streamed content.

## First increment (small, reviewable, no behaviour change)

Introduce the typed request/response **alongside** `Generate(string)`, with the existing method
becoming a thin caller of it — one direction, one release, deleted when the last caller moves. That
ordering matters: building the typed path first and migrating callers into it is what stops the shim
becoming permanent, which is exactly how the string ant contract survived four releases.
