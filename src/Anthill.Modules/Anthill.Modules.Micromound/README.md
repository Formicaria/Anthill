# Anthill.Modules.Micromound — M1, read-only

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

**2. `IMoundStore` is in-memory here.**

The SQLite implementation belongs next to `HomelabRepository`'s `integration_instances` /
`integration_state` tables, and lands with the Api wiring below. Keeping the interface first is
what lets M1's authority logic be proven network-free and database-free, the same way the homelab's
mock-provider harness lets its 240 tests run without touching hardware.

## Still to wire (composition root, `Anthill.Api`)

None of this is in the module by design — composition happens in `Anthill.Api`.

1. **Integration kind.** Register a `micromound` `IIntegrationDefinition` (category `infra`,
   auth mode `token`) in `IntegrationCatalog`, publishing the three widget kinds through the
   existing `integration_state` mechanism so the widget runtime renders it without special-casing.
2. **Persistence.** A `SqliteMoundStore : IMoundStore` over three tables — `micromound_mounds`,
   `micromound_enrollment_tokens`, `micromound_beats` — following `HomelabRepository`'s write-lock
   and `Bind` conventions. Enrollment token hashes go through the field cipher.
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
   command path, and they arrive with M2 and M4 respectively.
4. **Composition.** Build `MicromoundOptions` from the live runtime and pass the existing
   `FieldCipher`; add `MicromoundModule` alongside `HomelabModule` where modules are registered.
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
