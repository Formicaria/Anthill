# Integrations — external providers (Proxmox, DNS, DHCP, firewall, health)

v1.9.1 ships the mock-provider harness here: `FakeProviders.cs` (shared `FakeHomelabProvider`
base + five deterministic, network-free mocks and `HomelabProviderStatus`). Real providers arrive
read-only from V1.12.0 (Proxmox) and must follow the same pattern: deterministic C# (never routed
through the model router), target-allowlist discipline, secret-free statuses, audit events, and
passing the shared `MockProviderHarnessTests` fixture. See docs/NORTH_STAR.md and docs/HOMELAB.md.
