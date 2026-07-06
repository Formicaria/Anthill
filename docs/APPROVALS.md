# ANTHILL Approvals — The IApprovable Design (v1.14.0, NORTH_STAR Phase 10)

Status: Canonical design for the unified approval system. Reviewed and shipped as code in v1.14.0
(`src/Anthill.Core/Homelab/Approvals/IApprovable.cs`); V2.1 implements execution against this
contract without changing it. See `docs/NORTH_STAR.md` §6 rule 1: **one approval system**.

## Why one abstraction

Three things will need operator approval by V2.4: code **patches** (exist today), homelab
**action proposals** (V2.1: restart service, snapshot VM, run backup), and **network changes**
(V2.4: DNS/DHCP/firewall previews). Three separate queues means three audit trails, three dedupe
implementations, and three places for a safety bug to hide. `IApprovable` collapses them into:

- **One pending queue** — `GET /homelab/approvals/unified` returns every kind, newest first.
- **One lifecycle** — `pending → approved → executed`, `pending → rejected`,
  `pending → superseded`. Execution NEVER happens from `pending`; approval is a distinct human
  step, and every executor re-checks state at execution time (TOCTOU guard).
- **One dedupe rule** — two approvables with the same `DedupeKey` may not both be pending; the
  newer supersedes the older. Patches already do this per `action_type:target_id`; every future
  kind inherits it via `ApprovableProjections.DedupePending`.
- **One audit trail** — every state transition is an event on the owning record's stream
  (patch events today, `homelab_events` for actions), and the unified view exposes them uniformly.

What differs per kind is ONLY the renderer, selected by `RendererHint`:

| Kind | RendererHint | Detail view |
|---|---|---|
| `patch` (today) | `patch_diff` | unified diff, risk badge, verify state |
| `homelab_action` (V2.1) | `action_proposal` | blast-radius card, rollback note, dry-run button |
| `network_change` (V2.4) | `network_preview` | rule/record diff preview, export-config note |

## The contract

`IApprovable` (interface) → `ApprovableView` (serializable projection the API returns):
`ApprovableId` (kind-prefixed), `Kind`, `Title`, `Summary`, `RiskLevel` (low/medium/high/critical),
`State`, `DedupeKey`, `RendererHint`, `RequestedBy`, `CreatedAt`, `SourceId` (underlying record id).

Projections are **read adapters over existing stores** — no new table, no data migration. Today's
`ApprovableProjections.FromPatchApproval` maps `approval_requests` rows; V2.1 adds
`FromActionProposal`, V2.4 `FromNetworkChange`. Decisions keep flowing through each kind's
existing endpoints (`/approve/{id}` etc. for patches) so nothing breaks; a future unified decision
endpoint can delegate by `Kind` + `SourceId`.

## ActionProposal (V2.1 skeleton, shipped now for design review)

`ActionProposal : IApprovable` exists in code but is DELIBERATELY inert in v1.14: no
ActionExecutor exists, no endpoint constructs one, `RiskLevel` defaults to `high` (fail toward
caution). It already carries the NORTH_STAR Phase 12 blast-radius rubric inputs so the scoring
discussion happens against real fields: `DependencyFanout` (from the v1.10 dependency map),
`ServiceCriticality`, `BackupCovered` (v1.12 storage sync / V2.2 coverage map), `InternetExposed`
(v1.13 findings), `RollbackNote` (mandatory before execution), `DryRunAvailable`.

## V2.1 execution requirements (bound by this design)

1. Approval and execution are separate permissions (`approve_homelab_actions` vs
   `execute_homelab_actions` — both capability-gated OFF since v1.9.0).
2. Executors re-read state and refuse anything not `approved`.
3. `.anthill/HOMELAB_STOP` halts every executor regardless of state.
4. Every execution appends `executed`/`execution_failed` audit events + post-execution
   verification results to the same stream the approval lived on.
5. The forbidden-actions list (NORTH_STAR Phase 12) is enforced in the executor, not just the UI.
