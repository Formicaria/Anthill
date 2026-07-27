using Xunit;

// The Anthill.Tests suite exercises GLOBAL mutable runtime state — the static AnthillRuntime
// feature gates (EnableMedicAnt, EnableSpecialistAntExecution, sandbox/model routing gates, …) and
// AnthillRuntime config initialization, which resets those gates to their config defaults.
//
// xUnit runs separate collections on parallel threads by default. The suite already groups the
// gate-mutating specialist tests into the "specialist-gates" collection, but the config-initializing
// tests (planner/autonomy/director/etc.) live in OTHER collections — so a config re-init on one
// thread can flip a gate back to its default in the middle of a gate test on another thread. This
// surfaced as HandoffAndRoutingTests intermittently seeing 'medic' gate closed mid-test.
//
// Per-collection serialization cannot close a cross-collection race over shared static state, so the
// whole assembly is serialized. The suite runs in seconds, so the cost is negligible and shared-state
// races become structurally impossible.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
