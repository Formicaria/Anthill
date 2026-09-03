# Anthill.Modules.Micromound — the controller half, OPTIONAL

MICROMOUND is an optional integration, twice over. Its projects live outside `Anthill.sln`: a
checkout without the sibling `micromound` repository builds a complete colony with **no micromound
in it** — not a disabled copy, none of it (the Api's wiring compiles under the `MICROMOUND` define,
which only lights up when the wire-contract checkout exists). And a colony built *with* it still
opts in explicitly: `micromound_enabled` defaults to off. CI checks the contract out (pinned to a
micromound tag) and runs `Anthill.Tests.Micromound` as its own lane, so the integration is always
exercised where it costs nothing to carry.

MICROMOUND extends the colony into physical devices. This module is the ANTHILL side of that link.

**v0.3.8.114 — the colony can now direct mounds, and the beat is two-way.** `.60` shipped the
uplink and said plainly what it had not built: "M1 has no command path, so the colony can see
mounds and cannot direct them." That half is now here — a signing identity, charters, configuration
authoring, physical missions, structured evidence, the capability resolver, and a sync beat that
acknowledges, renews the lease, and delivers the downlink queue. Physical missions that policy says
need a person's answer go into **ANTHILL's own approval queue**, not a second one.

The device side, the wire protocol, and the safety model live in the `micromound` repository.
`docs/PROTOCOL.md` there is normative for everything on the wire; `docs/SAFETY.md` wins over every
other document including this one.

## What this module contains

| Piece | File | Notes |
|---|---|---|
| Module registration | `MicromoundModule.cs` | Configuration only. No I/O — a mound may be a Pi in a shed with a flat battery, and a colony that dialled it at boot would refuse to start |
| Configuration | `MicromoundOptions.cs`, `MicromoundRuntime.cs` | Handed over, never read from the core |
| Mound registry | `Micromound/MicromoundModels.cs`, `MoundStore.cs` | `IMoundStore` + an in-memory reference implementation |
| Enrollment | `Micromound/MicromoundEnrollment.cs` | Operator mints a one-time token; device sends its public key; token burns |
| Sync beat | `Micromound/MicromoundSync.cs` | Verifies signatures and the hash chain, ingests what arrived, renews the lease, drains the downlink queue, and answers with a signed `ack` |
| Signing identity | `Micromound/MicromoundIdentity.cs` | The colony's Ed25519 key, minted once, never readable back |
| Charters | `Micromound/MicromoundCharters.cs` | Issuance and the single lease-renewal path |
| Configuration | `Micromound/MicromoundConfiguration.cs` | Manifest authoring — hardware bindings, workers, device limits |
| Autonomy policy | `Micromound/MicromoundAutonomy.cs` | Who may spend a charter, evaluated for the asking origin |
| Missions | `Micromound/MicromoundMissions.cs` | One dispatcher, every origin; approval-required missions queue nothing |
| Evidence | `Micromound/MicromoundEvidence.cs` | The colony re-runs the gate; a verdict only ever goes down |
| Resolver | `Micromound/MicromoundResolver.cs` | Which mounds can satisfy a capability — answers, issues nothing |
| Kill switch | `Micromound/MicromoundStop.cs` | `.anthill/MICROMOUND_STOP`, plus per-mound stop in the record |
| Widget payloads | `Micromound/MicromoundWidgets.cs` | `mound_fleet`, `mission_status`, `evidence_feed` |

## What this module deliberately does not contain

**No UI.** `.114`'s scope was set to the backend, and the console surfaces for charters, missions
and evidence are deferred to a later release. Everything here is reachable over the API and nothing
here renders.

**No second approval system.** A mission that policy says needs a person's answer becomes an
ordinary ANTHILL `ApprovalRequest` (`ActionType = physical_action`), decided through the existing
`/approve/{id}` and `/reject/{id}`, and carried out by the same dispatcher with `ApprovalGranted`
set. There is no `ManualMicromoundController` and no `AutonomousMicromoundController`: origin is
data on the request, and everything after the policy check is identical whoever asked.

**No way to raise a ceiling.** `hazardous` is never a legal charter ceiling; a manifest can only
narrow; and the mound re-checks everything and wins.

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

   `/micromound/missions` and `/micromound/charters` are the command path, and as of `.114` they
   exist, behind `approve_micromound_actions`. The device pair (`/v0/enroll`,
   `/v0/sync`) carries no session gate on purpose: the one-time token and the Ed25519 signature
   ARE the authentication. `/micromound/stop` is per-mound only — the global stop stays a file
   (`.anthill/MICROMOUND_STOP`) precisely so no API flow can clear it.
4. **Composition.** `MicromoundOptions` is built from the live runtime and handed the same
   `FieldCipher.CreateDefault()` the homelab gets; `MicromoundModule` loads alongside
   `HomelabModule`. Permissions: `read_micromound` and `manage_micromound` ship enabled;
   `approve_micromound_actions` also ships enabled because the only thing it could authorize in M1
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
