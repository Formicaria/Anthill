using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Outcomes;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The operator's report is a projection of rows, not a model's account of them. v0.3.8.73 — the
/// largest finding of the first live qualification run (PLAN.md §2 item 5).
///
/// WHAT THE RUN FOUND. A live mission's final report carried commands, exit codes, durations, test
/// totals, a role census, medic activity and a `Dispatched` column. None of it came from the colony.
/// `BuilderAnt` writes the operator answer by prompting a model with prior task context, so a prompt
/// asking for a report with telemetry received telemetry-shaped prose. There was no reporting code
/// to have a bug in — which is why six of the eight reported defects were one defect.
///
/// THE TELL, and it is the reason this file asserts what it asserts: `Dispatched` appears nowhere in
/// the source, and the statuses inside it read `In Progress` while the persisted vocabulary is the
/// lowercase `TaskStatus` enum. The column was invented. Two further reported defects dissolved on
/// the same evidence and are pinned below as the facts they actually are — the colony CANNOT
/// finalize with a task in progress, and the verifier does NOT return a pass without evidence.
///
/// Recording those two matters as much as the fix. A report that lists eight defects where there are
/// three sends three fixes somewhere useless, and "we checked, and the mechanism was already right"
/// is a finding this repository keeps having to re-derive because nobody wrote it down.
/// </summary>
[Collection("specialist-gates")]
public class MissionReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "anthill-report-" + Guid.NewGuid().ToString("N")[..10]);

    public MissionReportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory NewMemory() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N")[..8] + ".db"));

    // -----------------------------------------------------------------------------------------------
    // Nothing is invented
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// AN EMPTY MISSION REPORTS EMPTINESS. This is the assertion the whole class turns on: with no
    /// tasks, no checks and no evidence, every figure is zero or "not recorded" — never a plausible
    /// default. A report that fills gaps is the defect one layer in.
    /// </summary>
    [Fact]
    public void AMissionWithNothingRecorded_ReportsNothing_NotSomethingPlausible()
    {
        using var memory = NewMemory();
        var report = MissionReport.Compile(memory, "no-such-mission");
        var text = MissionReport.Render(report);

        Assert.Empty(report.Tasks);
        Assert.Empty(report.Checks);
        Assert.Equal(0, report.EvidenceCount);
        Assert.Null(report.ElapsedSeconds);
        Assert.Equal("none persisted", report.OutcomeCode);

        Assert.Contains("elapsed_seconds: not recorded", text);
        Assert.Contains("none — no check evidence was recorded", text);
        Assert.Contains("roles dispatched (0): none", text);
    }

    /// <summary>
    /// THE ROLE CENSUS COMES FROM THE REGISTRY. The live report's census was short because a model
    /// listed the roles it had seen mentioned; the registry is the only thing that knows what
    /// exists, and it is asked directly.
    /// </summary>
    [Fact]
    public void TheRoleCensus_IsTheRegistry_NotWhateverRan()
    {
        using var memory = NewMemory();
        var report = MissionReport.Compile(memory, "m1");

        Assert.Equal(
            AntRegistry.Roles.Select(r => r.RoleId).Distinct(StringComparer.Ordinal)
                .OrderBy(r => r, StringComparer.Ordinal),
            report.RolesRegistered);

        // …and REGISTERED and DISPATCHED are separate fields, which is the confusion the live
        // report's single `Dispatched` column embodied: it held statuses rather than a yes/no,
        // because it was answering two questions with one invented word.
        Assert.NotEqual(report.RolesRegistered, report.RolesDispatched);
        Assert.Empty(report.RolesDispatched);
    }

    /// <summary>
    /// THE COMPILER CANNOT BE HANDED PROSE, and the signature is the guarantee. `Compile` takes a
    /// store and an id; there is no parameter a model answer could travel through, so no later edit
    /// can quietly let one contribute. This is asserted on the source because it is a property of
    /// the shape rather than of any single run.
    /// </summary>
    [Fact]
    public void TheCompiler_TakesNoTextParameter_AndCallsNoModel()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Outcomes", "MissionReport.cs")));

        foreach (var forbidden in new[] { "ModelRouter", "GenerateTyped", "_router", "IReasoningProvider" })
            Assert.False(source.Contains(forbidden, StringComparison.Ordinal),
                $"MissionReport reaches a model ({forbidden}). Every value in the operator's record "
              + "must be a projection of a persisted row — that is the entire difference between "
              + "this and the report the first live run produced.");

        Assert.Contains("public static Report Compile(SqliteMemory memory, string missionId)", source);
    }

    /// <summary>
    /// AND THE BUILDER IS TOLD TO STOP COMPETING WITH IT. The structural fix is the compiled
    /// artifact; this is the smaller half, and it is checked because a rule that quietly disappears
    /// puts the narrative back in the telemetry business.
    /// </summary>
    [Fact]
    public void TheBuilder_IsForbiddenFromStatingFigures()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Agents", "Ants.cs")));

        Assert.Contains("NEVER state a command, exit code, duration, timestamp, test count", source);
    }

    // -----------------------------------------------------------------------------------------------
    // The two reported defects that were not defects — recorded so they are not re-fixed
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// "Anthill finalized while tasks were still In Progress" — it cannot. `Queen.FinalizeMission`
    /// carries a v2.26.0 invariant: any task still pending, ready, blocked or running at
    /// finalization is forced to `failed` with `internal_runtime_defect`, logged as an invariant
    /// breach, and the mission fails CLOSED rather than being evaluated half-finished.
    ///
    /// The `In Progress` in the live report was title-case prose. The persisted vocabulary is the
    /// lowercase `TaskStatus` enum, which is also why the report's status column could contain a
    /// value the database has no word for.
    /// </summary>
    [Fact]
    public void FinalizationRefusesNonTerminalTasks_WhichIsWhyTheReportedStatusWasProse()
    {
        var queen = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs")));

        Assert.Contains("no_non_terminal_task_at_finalization", queen);
        Assert.Contains("internal_runtime_defect", queen);

        // The vocabulary the runtime actually writes has no "In Progress" in it.
        Assert.DoesNotContain("In Progress", queen, StringComparison.Ordinal);
    }

    /// <summary>
    /// "The verifier fails open — returned PASS without evidence" — it does not. The Queen always
    /// hands the verifier the evidence store, and an empty evidence list resolves to `Unknown`, with
    /// the explanation naming the reason. A PASS an operator reads in the final report came from the
    /// builder's prose, not from a verdict.
    ///
    /// The report was one line from something true, and v0.3.8.73 closed it anyway: with NO store at
    /// all the verdict used to be parsed out of the model's text. Prose may still downgrade; it can
    /// no longer promote.
    /// </summary>
    [Fact]
    public void AnEmptyEvidenceList_IsUnknown_NeverAPass()
    {
        var verdict = EvidenceVerdict.For(Array.Empty<Anthill.SDK.Artifacts.Evidence>());

        Assert.Equal(VerificationVerdict.Unknown, verdict.Verdict);
        Assert.False(verdict.IsPass);
        Assert.Contains("nothing has been verified", verdict.Explanation);
    }

    [Fact]
    public void TheVerifier_IsAlwaysGivenTheEvidenceStore()
    {
        var queen = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs")));

        Assert.Contains("new VerifierAnt(", queen);
        Assert.Contains("IEvidenceStore)Memory", queen);
    }

    /// <summary>
    /// A MODEL'S CONFIDENCE CANNOT PROMOTE A VERDICT; ITS DOUBT IS STILL HEARD. And the scoping here
    /// is the correction of a wrong fix, which is why it is asserted from both sides.
    ///
    /// The first attempt at closing the no-store hole refused ANY verdict parsed out of the
    /// verifier's text. That broke `EvidenceFailsClosedTests.NoStoreAtAll_KeepsTheStaticPath` and
    /// `VerificationVerdictTests.APassingVerification_IsSucceeded` — two tests that were RIGHT.
    /// `text` is not always model prose: with `useOllama` false there is no model in this ant, and
    /// the text is the static verifier's own deterministic evaluation of task states. S3 preserved
    /// that path on purpose, calling its removal "rigour's costume on a regression". The refusal
    /// belongs on "a model wrote it", not on "it came from text" — an adjacent question, answered
    /// confidently, which is the failure this whole release is about.
    /// </summary>
    [Fact]
    public void TheNoStoreRefusal_IsScopedToModelWrittenVerdicts()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Agents", "Ants.cs")));

        Assert.Contains("var modelWroteIt = _useOllama && _router is not null;", source);
        Assert.Contains("modelWroteIt && Outcomes.VerificationVerdict.IsPass(fromProse)", source);

        // The deterministic path still promotes, which is what the two tests above defend.
        Assert.Equal(VerificationVerdict.Passed, VerificationVerdict.Parse("Verdict: Verification Passed"));
    }

    // -----------------------------------------------------------------------------------------------
    // Finding 7 — a named source is a constraint, not a hint
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The web ant queried an unrelated domain because it was never looking for a target: the query
    /// was `goal + description`, so a domain the operator named was just more words. One recognised
    /// site becomes a `site:` filter.
    /// </summary>
    [Theory]
    [InlineData("Check the ARIA guidance on w3.org for naming list regions", "w3.org")]
    [InlineData("Look this up with site: developer.mozilla.org please", "developer.mozilla.org")]
    [InlineData("Read https://learn.microsoft.com for the answer", "learn.microsoft.com")]
    public void ANamedSite_BecomesASiteFilter(string text, string expected) =>
        Assert.Equal(expected, WebResearchAnt.RequestedSite(text));

    /// <summary>
    /// And guessing is refused. No domain means no filter; TWO domains as often means a comparison
    /// as it means one of them is the target, and picking would be inventing intent — the failure
    /// mode this whole release is about, in miniature.
    /// </summary>
    [Theory]
    [InlineData("Summarize what the framework does")]
    [InlineData("Compare w3.org and developer.mozilla.org on this")]
    [InlineData("Update docs/COLONY-NOTE.md and README.md")]
    public void AnAmbiguousOrAbsentTarget_AddsNoFilter(string text) =>
        Assert.Null(WebResearchAnt.RequestedSite(text));
}
