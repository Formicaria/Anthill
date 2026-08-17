using System.Runtime.CompilerServices;

namespace Anthill.Tests;

/// <summary>
/// The colony's mission narration is silenced during tests, unless it is asked for. v0.3.8.78.
///
/// WHY THIS EXISTS, and it is a real finding rather than tidiness. A green run of this suite emits
/// roughly two hundred lines that READ as failures: `Adaptive stop: critical failure persists`,
/// `Task failed_retryable: one or more checks failed`, `[verifier] could not read evidence: store is
/// down`, `SQLite Error 19: FOREIGN KEY constraint failed`. Every one of them is a fixture
/// deliberately driving a failure path — `EvidenceFailsClosedTests` injects a store that throws on
/// every call precisely to prove evidence fails CLOSED — and the colony prints its operator console
/// while they run.
///
/// The cost is not noise. It is that a REAL failure arrives in the middle of two hundred lines of
/// simulated failure, and the reader has to already know which is which. That is the defect class
/// this repository keeps naming, pointed at its own test output: a diagnostic that degrades the
/// thing it describes. Two of this release line's failures were slower to spot for exactly this
/// reason, and the operator reading the run asked, reasonably, what all the errors were.
///
/// WHAT THIS IS NOT. It is not a production change. Nothing in `src/` is touched, no logging
/// abstraction is introduced, and the operator console is byte-identical outside a test run — the
/// colony's `Console.WriteLine` calls are its OPERATOR interface and are correct where they are. The
/// swap happens in the test assembly, at load, and only there.
///
/// AND IT IS REVERSIBLE PER RUN. `ANTHILL_TEST_CONSOLE=1` restores the full narration, because the
/// moment this is genuinely needed is when a mission-shaped test fails and the transcript is the
/// evidence. Silence that cannot be lifted is how a diagnostic gets deleted rather than quieted.
///
/// Assertion messages are unaffected: xUnit writes those itself, and this suite invests heavily in
/// making them name the layer that failed. Those are what a run should be read for.
/// </summary>
internal static class TestConsole
{
    /// <summary>
    /// Runs once, when the test assembly loads — before any collection starts, so no test can emit
    /// narration ahead of the swap.
    /// </summary>
    [ModuleInitializer]
    internal static void Silence()
    {
        if (Environment.GetEnvironmentVariable("ANTHILL_TEST_CONSOLE") is "1" or "true") return;

        // Discarded rather than buffered. A buffer would grow across ~3,000 tests, several of which
        // run whole missions, and nothing would ever read it — xUnit reports the assertion, not the
        // process's stdout.
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
    }
}
