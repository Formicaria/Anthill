# Incidents — failure memory, timelines, and fix recommendations

Filled in v1.14.0 (NORTH_STAR Phase 10): `IncidentManager.cs` — candidate sweep (auto-opened,
per-subject deduped incidents), suspect-flagged timelines (changes before the failure), similar-
incident matching with verbatim fix memory, and repeat-offender pattern detection. Tracking and
recommendations only; remediation arrives approval-gated in V2.1+ via IApprovable
(see ../Homelab/Approvals and docs/APPROVALS.md).
