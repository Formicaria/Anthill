using System.Runtime.CompilerServices;
using Anthill.Core.Configuration;
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


/// <summary>
/// Run the runtime bootstrap ONCE, before any test does. v0.3.8.88.
///
/// WHY THIS IS AT ASSEMBLY SCOPE. <c>AnthillRuntime.Initialize</c> is one-shot and it projects the
/// on-disk config over FIFTY-ONE process-global statics — every roster gate, `UseOllama`,
/// `EnableAutonomy`, `EnablePatchApplication`, the file and shell gates, the parallel-execution
/// switch. `Queen`'s constructor calls it. So the first test in a run that builds a Queen silently
/// overwrites whatever the tests before it had set, and every test after that one keeps its own
/// settings because the bootstrap short-circuits.
///
/// The consequence is not theoretical and it cost v0.3.8.87 a full release cycle. Four lifecycle
/// tests set roster flags, built a Queen, and had their settings discarded — so they passed or
/// failed on their POSITION in the run, and, because the values come from a file on the developer's
/// machine, on whose machine it ran. A sweep at v0.3.8.88 found twenty-one more test files saving one of
/// those fifty-one statics for restore without any guarantee the bootstrap had already happened.
///
/// Forcing it here makes every one of those saves meaningful: by the time any test reads a static it
/// already holds its configured value, and no later `Queen` can move it. That is a stronger
/// statement than fixing the four tests that happened to break — it removes the ordering hazard
/// rather than the instances of it.
///
/// It is NOT wrapped in a try/catch. If the runtime cannot bootstrap, every mission test is about to
/// fail anyway, and a swallowed exception here would turn "config is broken" into a scatter of
/// unrelated assertion failures — the diagnostic-that-hides-what-it-describes shape this repository
/// keeps finding.
/// </summary>
internal static class TestAssemblyBootstrap
{
    [ModuleInitializer]
    internal static void ForceRuntimeBootstrapBeforeAnyTestRuns() => AnthillRuntime.Initialize();
}
