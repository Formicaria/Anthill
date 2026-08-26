using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Orchestration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.53 — the keystone proof: the scripted provider is reached through the REAL plumbing.
/// A mission submitted through <see cref="Queen.RunMission(string)"/> — the operator's own public
/// path — has its ants' model calls answered by the script, which can only happen if the route
/// table, <see cref="Anthill.Core.Models.ReasoningProviders"/> factory resolution, and the
/// role-stamped prompt convention all hold end to end. No ant is invoked by hand; the ONLY fake
/// is the answer, at the audit's sanctioned boundary.
///
/// This is deliberately the SMALLEST composed scripted scenario — research-shaped, no patches.
/// The code-patch lifecycle scenarios build on this foundation and land with it proven.
/// </summary>
[Collection("specialist-gates")]   // route table + UseOllama are static; serialize with the togglers
public class ScriptedProviderTests : IDisposable
{
    private readonly string _dir;
    private readonly bool _useOllamaWas;

    public ScriptedProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-scripted-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        _useOllamaWas = AnthillRuntime.UseOllama;
    }

    public void Dispose()
    {
        AnthillRuntime.UseOllama = _useOllamaWas;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void AScriptedMission_RunsTheRealPath_AndTheScriptsAnswersLandInTheRecord()
    {
        const string researchMarker = "SCRIPTED-RESEARCH: the colony is a bounded auditable mission runner.";
        const string buildMarker = "SCRIPTED-BUILD: one sentence, as asked.";

        var book = new ScriptBook()
            // The planner's script is deliberately NOT a valid plan: the fallback static plan is
            // a legitimate production path and keeps this proof about PROVIDER plumbing, not
            // about plan-format archaeology. The lifecycle scenarios script real plans.
            .Role("planner", "not a plan")
            .Role("researcher", researchMarker)
            .Role("builder", buildMarker)
            .Role("verifier", "SCRIPTED-VERIFY: the response addresses the goal. VERDICT: pass");

        AnthillRuntime.UseOllama = true;   // ants consult the router; the router serves the script
        using var scripted = ScriptedColony.Begin(book,
            "planner", "researcher", "builder", "verifier", "fallback");

        var queen = new Queen(new SqliteMemory(Path.Combine(_dir, "scripted.db")));
        string? missionId = null;
        // v0.3.8.93 — brief-sized on purpose (above Planner.SimpleAnswerGoalChars): proportional
        // planning turned the old one-liner into a single-builder mission, and this proof needs the
        // researcher AND the builder to be asked, so the scripted markers can land in both records.
        queen.RunMission(
            "Summarize in one sentence what the ANTHILL framework does. Ground the sentence in "
          + "the colony's own context: how a mission travels from an operator's request through "
          + "planning, execution and verification to a final answer, which roles take part at "
          + "each stage and what each contributes, and whatever the colony's stored memory can "
          + "supply about its own history. Close with the single-sentence version an operator "
          + "would read first.",
            onMissionCreated: id => missionId = id);
        Assert.NotNull(missionId);

        // The mission is terminal, through the same public read the console uses.
        var mission = queen.Memory.GetMission(missionId!);
        Assert.NotNull(mission);
        Assert.Equal("complete", mission!.GetValueOrDefault("status")?.ToString());

        // The scripted answers are IN the persisted task record — the router resolved the
        // scripted factory for real, or these strings could not exist anywhere in this process.
        var tasks = queen.Memory.GetTasksForMission(missionId!);
        Assert.NotEmpty(tasks);
        var allResults = string.Join("\n", tasks.Select(t => t.GetValueOrDefault("result")?.ToString() ?? ""));
        Assert.Contains(researchMarker, allResults);
        Assert.Contains(buildMarker, allResults);

        // And the script saw the roles it served — the prompt convention held, both directions.
        Assert.Contains(book.Requests, r => r.Role == "researcher");
        Assert.Contains(book.Requests, r => r.Role == "builder");
    }

    /// <summary>
    /// An unscripted role fails LOUDLY, never silently: the provider answers Empty, the ant
    /// discloses a provider failure in the persisted record, and no invented content appears.
    /// (The ant's own disclosure sentence is the assertion target — the provider's refusal text
    /// is transport, and the builder rightly reports the OUTCOME, not the provider's prose.)
    /// </summary>
    [Fact]
    public void AnUnscriptedRole_IsADisclosedProviderFailure_NeverAnInventedAnswer()
    {
        var book = new ScriptBook().Role("planner", "not a plan")
            .Role("researcher", "only the researcher is scripted");
        AnthillRuntime.UseOllama = true;
        using var scripted = ScriptedColony.Begin(book, "planner", "researcher", "builder", "verifier", "fallback");

        var queen = new Queen(new SqliteMemory(Path.Combine(_dir, "unscripted.db")));
        string? missionId = null;
        // The goal's WORDING matters: the fallback planner is keyword-driven, and the first
        // draft of this test said "builder has no script" — whose 'script' keyword produced a
        // CODE plan with a file-scout task and no builder task at all, so nothing ever asked
        // the unscripted role. Summary-shaped wording yields the standard research plan whose
        // builder step is exactly the unscripted ant this test exists to watch fail.
        queen.RunMission("Summarize what happens when a role has no answer.",
            onMissionCreated: id => missionId = id);

        var tasks = queen.Memory.GetTasksForMission(missionId!);
        var allText = string.Join("\n", tasks.Select(t =>
            (t.GetValueOrDefault("result")?.ToString() ?? "") + "|" + (t.GetValueOrDefault("status")?.ToString() ?? "")));
        // The builder's own disclosure of the empty provider answer — a recorded failure,
        // not a fabricated build response.
        Assert.Contains("empty response", allText, StringComparison.OrdinalIgnoreCase);
    }
}
