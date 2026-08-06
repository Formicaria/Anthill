# Health — checks, classification, and alerts

Filled in v1.11.0 (NORTH_STAR Phase 7): `HealthModels.cs` (HealthStatus, HealthCheckSchedule,
HealthSummary, AlertRecord) and `HealthCheckRunner.cs` (ping/HTTP/TCP/service-URL checks +
disk/uptime placeholders). Rules that hold here: allowlist before any I/O, strict timeouts,
deterministic C# only, no auto-remediation — remediation arrives approval-gated in V2.1+.
See docs/NORTH_STAR.md and docs/HOMELAB.md.
