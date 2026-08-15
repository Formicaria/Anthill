# Anthill.Modules.Micromound — M1, read-only, OPTIONAL

MICROMOUND is an optional integration, twice over. Its projects live outside `Anthill.sln`: a
checkout without the sibling `micromound` repository builds a complete colony with **no micromound
in it** — not a disabled copy, none of it (the Api's wiring compiles under the `MICROMOUND` define,
which only lights up when the wire-contract checkout exists). And a colony built *with* it still
opts in explicitly: `micromound_enabled` defaults to off. CI checks the contract out (pinned to a
micromound tag) and runs `Anthill.Tests.Micromound` as its own lane, so the integration is always
exercised where it costs nothing to carry.

MICROMOUND extends the colony into physical devices. This module is the ANTHILL side of that
link, at its first phase: **the colony can see mounds and cannot direct them.**

The device side, the wire protocol, and the safety model live in the `micromound` repository.
`docs/PROTOCOL.md` there is normative for everything on the wire; `docs/SAFETY.md` wins over every
other document including this one.

## What M1 contains

| Piece | File | Notes |
|---|---|---|
| Module registration | `MicromoundModule.cs` | Configuration only. No I/O — a mound may be a Pi in a shed with a flat battery, and a colony that dialled it at boot would refuse to start |
| Configuration | `MicromoundOptions.cs`, `MicromoundRuntime.cs` | Handed over, never read from the core |
| Mound registry | `Micromound/MicromoundModels.cs`, `MoundStore.cs` | `IMoundStore` + an in-memory reference implementation |
| Enrollment | `Micromound/MicromoundEnrollment.cs` | Operator mints a one-time token; device sends its public key; token burns |
| Sync beat | `Micromound/MicromoundSync.cs` | Verifies signatures and the hash chain, records telemetry |
| Kill switch | `Micromound/MicromoundStop.cs` | `.anthill/MICROMOUND_STOP`, plus per-mound stop in the record |
| Widget payloads | `Micromound/MicromoundWidgets.cs` | `mound_fleet`, `mission_status`, `evidence_feed` |

## What M1 deliberately does not contain

No charter issuance, no mission assignment, no actuation, and no code path that can raise an
action ceiling. The only downlink the colony can produce is a stop order — and a stop is a command
to *stop* acting, which is why it is allowed to exist before the approval pipeline does.

`MicromoundPermissions.Approve` (`approve_micromound_actions`) is declared and unused. That is
intentional: the tiering is settled before there is anything to be tempted to skip it for.

## Two decisions worth reviewing

**1. This module references `Micromound.Protocol` and `Micromound.Crypto`.**

`Anthill.Modules/README.md` rule 1 says a module references `Anthill.SDK` only, and
`ModuleBoundaryTests` enforces it — including for references that are present but unused. Those
are *our* assemblies; `Micromound.Protocol` is not one. It is the shared wire contract, and the
alternative is a second hand-maintained copy of the envelope, charter, and evidence shapes on this
side of the link. MICROMOUND.md's "reuses, never duplicates" bites hardest on the contract itself:
two definitions of a wire format drift, and the drift surfaces in the field.

**This is now checked rather than assumed.** `ModuleBoundaryTests` filters on the `Anthill.`
prefix, so the Micromound references pass — but the theory's `[InlineData]` list is hand-written,
and Micromound was absent from it, which reads exactly like passing. It has been added, along with
a project reference so the assembly is loadable. A module missing from that list is not exempt
from the boundary; it is invisible to it.

The `.csproj` resolves the path via `$(MicromoundRepoPath)`, defaulting to a sibling checkout:

```bash
dotnet build -p:MicromoundRepoPath=D:\src\micromound
```

Once `Micromound.Protocol` is published as a package, that property goes away and this becomes an
ordinary `PackageReference`.

**2. `IMoundStore` has two implementations, and the tests use the in-memory one.**

`InMemoryMoundStore` is the reference the 33 tests prove the authority logic against —
network-free and database-free, the same way the homelab's mock-provider harness lets its 240
tests run without touching hardware. `SqliteMoundStore` (this directory) is what the composed
colony runs: same database file as colony memory and the homelab tables, own tables only,
`HomelabRepository`'s write-lock / WAL / `Bind` conventions, and enrollment token hashes through
the field cipher when one is configured.

## Wired (composition root, `Anthill.Api/Micromound/ApiHost.Micromound.cs`)

None of this is in the module by design — composition happens in `Anthill.Api`.

1. **Integration kind.** `micromound` is registered in `IntegrationCatalog` (category `infra`,
   auth mode `token`), publishing the three widget kinds through the existing `integration_state`
   mechanism. Its sync is deterministic and network-free: mounds dial in, so "sync" re-derives
   the widget payloads from what the store already knows.
2. **Persistence.** `SqliteMoundStore` over `micromound_mounds`, `micromound_enrollment_tokens`,
   `micromound_beats` (+ `micromound_widget_state` for payload freshness).
3. **Endpoints**, gated as PROTOCOL.md §9 specifies:

   | Endpoint | Method | Permission |
   |---|---|---|
   | `/micromound/mounds` | GET | `read_micromound` |
   | `/micromound/mounds` | POST | `manage_micromound` |
   | `/micromound/v0/enroll` | POST | token (device) |
   | `/micromound/v0/sync` | POST | device signature |
   | `/micromound/evidence` | GET | `read_micromound` |
   | `/micromound/stop` | POST | `approve_micromound_actions` |
   | `/micromound/stop/resume` | POST | `approve_micromound_actions` |

   `/micromound/missions` and `/micromound/charters` are **not** M1 endpoints — they are the
   command path, and they arrive with M2 and M4 respectively. The device pair (`/v0/enroll`,
   `/v0/sync`) carries no session gate on purpose: the one-time token and the Ed25519 signature
   ARE the authentication. `/micromound/stop` is per-mound only — the global stop stays a file
   (`.anthill/MICROMOUND_STOP`) precisely so no API flow can clear it.
4. **Composition.** `MicromoundOptions` is built from the live runtime and handed the same
   `FieldCipher.CreateDefault()` the homelab gets; `MicromoundModule` loads alongside
   `HomelabModule`. Permissions: `read_micromound` and `manage_micromound` ship enabled;
   `approve_micromound_actions` also ships enabled because the only thing it can authorize in M1
   is stopping hardware, and a stop an operator cannot reach is the unsafe default. The homelab
   operator role gains read + approve (view and halt), never manage — minting an enrollment
   token creates a device identity, which is an admin act like credential writes.
5. ~~**Tests.**~~ Shipped: `tests/Anthill.Tests.Micromound/` covers enrollment refusals,
   chain-anchor continuation, signature and impersonation refusal, reduced-profile enforcement,
   stop precedence, and widget shapes. It references `Micromound.Sim` deliberately — the envelopes
   under test are produced by the device implementation rather than hand-built to agree with the
   colony's expectations.

## A namespace footgun

This assembly's namespace is `Anthill.Modules.Micromound`; the protocol library's is
`Micromound.Protocol`. Inside this namespace, a *qualified* `Micromound.Protocol.Foo` resolves
`Micromound` to `Anthill.Modules.Micromound` and fails to compile. `using Micromound.Protocol;`
at the top of a file is fine (compilation-unit scope resolves from global), and where a qualified
name is genuinely needed, use `global::Micromound.Protocol.Foo` — `MicromoundModule.Register` does.
