# Approvals — the one approval system (IApprovable)

v1.14.0 ships the design here: `IApprovable.cs` — the shared interface, the `ApprovableView`
projection behind `GET /homelab/approvals/unified` (today's patch approvals mapped by
`ApprovableProjections`), the single dedupe rule, and the deliberately inert V2.1
`ActionProposal` skeleton carrying the blast-radius rubric fields. Canonical design doc:
docs/APPROVALS.md. V2.1 adds the ActionExecutor against this contract without changing it.
