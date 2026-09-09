using Anthill.Core.Agents;
using Anthill.Core.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE COLONY CAN SAY WHAT IT IS. v0.3.8.148.
///
/// FROM THE OPERATOR'S OWN COLONY. Asked "what is micromound? and how does it benefit the colony",
/// the researcher searched mission memory, found prior missions about tacos and 1990s history, and
/// the builder reported — accurately — that the colony had no record of the term. The verifier
/// failed the mission, correctly. Every layer behaved properly and the operator got nothing.
///
/// A FRESH INSTALL IS THE CASE THAT MATTERS: no mission history to recall, no checkout to inspect
/// (the shipped build is a binary), no knowledge base mapped. Every source the colony can read is
/// empty on day one, which is exactly when a new operator asks what it is.
/// </summary>
public class ColonySelfKnowledgeTests
{
    private static ToolResult Ask(string? topic) =>
        new ColonySelfKnowledgeTool().Run(
            topic is null ? new Dictionary<string, object?>()
                          : new Dictionary<string, object?> { ["topic"] = topic });

    // ---- the question that failed ---------------------------------------------------------------

    /// <summary>THE NAMED TEST, in the operator's own words.</summary>
    [Fact]
    public void TheQuestionThatFailed_IsAnswerable()
    {
        var result = Ask("what is micromound? and how does it benefit the colony");

        Assert.True(result.Success, result.Error);
        Assert.Contains("MICROMOUND", result.Output, StringComparison.Ordinal);
        Assert.Contains("deployment", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>And the other two the operator could not get explanations for.</summary>
    [Theory]
    [InlineData("forager", "knowledge")]
    [InlineData("micromound", "deployment")]
    [InlineData("what are the ants?", "verifier")]
    [InlineData("how does anthill handle approvals", "approval")]
    public void TheSubjectsAnOperatorAsksAbout_AreDocumented(string ask, string expected)
    {
        var result = Ask(ask);

        Assert.True(result.Success, result.Error);
        Assert.Contains(expected, result.Output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- matching runs both directions ------------------------------------------------------------

    /// <summary>
    /// THE BUG THE FIRST CUT HAD. The caller may hand this a TERM or a whole MISSION GOAL, and those
    /// need opposite tests: does the entry contain the query, or does the query contain the entry's
    /// name. The first cut checked only the first, so a goal matched nothing — and the tool written
    /// to answer "what is micromound" would have returned its index for exactly that question.
    /// </summary>
    [Fact]
    public void ATermAndAWholeGoal_BothFindTheSameEntry()
    {
        var byTerm = ColonySelfKnowledge.Find("micromound");
        var byGoal = ColonySelfKnowledge.Find(
            "what is micromound? and how does it benefit the colony\n\n--- project \"Questions\" ---");

        Assert.Contains(byTerm, e => e.Topic == "micromound");
        Assert.Contains(byGoal, e => e.Topic == "micromound");
    }

    // ---- a miss is cheap, which is what lets it run every time ------------------------------------

    /// <summary>
    /// AN UNRELATED GOAL PAYS ALMOST NOTHING. This is what makes unconditional dispatch defensible:
    /// the alternative was a keyword trigger on the composed goal, and keyword triggers on that
    /// string have caused three separate defects in recent releases — a recipe planned as a patch, a
    /// briefing planned as fourteen tasks, a question routed to the web ant.
    /// </summary>
    [Fact]
    public void AnUnrelatedGoal_GetsAShortIndex_NotTheWholeCorpus()
    {
        var result = Ask("give me a summary on history of the 1980s");

        Assert.True(result.Success, "a miss must not fail — the handler dispatches this every time");
        Assert.Contains("micromound", result.Output, StringComparison.OrdinalIgnoreCase);   // the index names it
        Assert.DoesNotContain("small-footprint deployment", result.Output, StringComparison.Ordinal);
        Assert.True(result.Output.Length < 600, $"the index is {result.Output.Length} chars; it rides on every researcher task");
    }

    /// <summary>
    /// AND THE MISS REFUSES THE FAILURE THIS TOOL EXISTS TO PREVENT. "No entry" alone invites the
    /// model to supply one from its own weights.
    /// </summary>
    [Fact]
    public void AMiss_TellsTheModelNotToInventADefinition()
    {
        // The query must share no word with the corpus. An earlier draft asked about "something
        // ANTHILL does not document" and PASSED the lookup — `Find` matches in both directions
        // (a caller may pass a bare term or a whole mission goal), so the word "anthill" inside the
        // question hit the `anthill` entry and the tool took the success path. The test was
        // measuring its own phrasing rather than the miss.
        Assert.Contains("Do NOT invent", Ask("quantum chromodynamics").Output,
            StringComparison.Ordinal);
    }

    // ---- it is shipped, and it is wired ------------------------------------------------------------

    /// <summary>
    /// EVERY ENTRY IS REAL. A corpus with an empty body is a definition that reads as present and
    /// answers nothing, which is worse than an absent one because the miss-index would not name it.
    /// </summary>
    [Fact]
    public void EveryEntry_HasAUniqueIdAndRealContent()
    {
        Assert.NotEmpty(ColonySelfKnowledge.Entries);

        Assert.Equal(ColonySelfKnowledge.Entries.Count,
            ColonySelfKnowledge.Entries.Select(e => e.Topic).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.All(ColonySelfKnowledge.Entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Topic));
            Assert.False(string.IsNullOrWhiteSpace(e.Title));
            Assert.True(e.Body.Length > 120, $"'{e.Topic}' is too short to be a definition");
        });
    }

    /// <summary>
    /// AND THE ROLE THAT READS THE COLONY CAN REACH IT. This file's siblings have found the same
    /// defect eight times: a tool registered and granted to nobody, or granted and dispatched by
    /// nobody. The contract and the handler landed in the same release and this pins both halves.
    /// </summary>
    [Fact]
    public void TheResearcher_IsAllowedTheTool_AndTheHandlerDispatchesIt()
    {
        var researcher = AntExecutionCatalog.ContractFor("researcher");

        Assert.NotNull(researcher);
        Assert.Contains(ColonySelfKnowledgeTool.ToolName, researcher!.AllowedTools);

        var handler = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Agents", "Ants.cs")));

        Assert.Contains("ColonySelfKnowledgeTool.ToolName", handler, StringComparison.Ordinal);
    }

    /// <summary>
    /// IT RECORDS NO EVIDENCE, AND THAT IS DELIBERATE. `AssessmentObjective` requires `inspection`
    /// rows before an audit's conclusions can be believed, and the point of that requirement is that
    /// the colony LOOKED AT THE OPERATOR'S TREE. Admitting this tool to that lane would let an audit
    /// of what is implemented be satisfied by a paragraph that ships with every copy — the error
    /// `ToolEvidence` names for `web_search`, in a different disguise. `system_info` is the exact
    /// precedent and is excluded for the same reason in the same words.
    /// </summary>
    [Fact]
    public void ReadingTheShippedDescription_IsNotEvidenceThatAnythingWasExamined()
    {
        Assert.False(Anthill.Core.Tools.ToolEvidence.Records(ColonySelfKnowledgeTool.ToolName));
        Assert.False(Anthill.Core.Tools.ToolEvidence.IsObservation(ColonySelfKnowledgeTool.ToolName));
        Assert.False(Anthill.Core.Tools.ToolEvidence.IsDeterministic(ColonySelfKnowledgeTool.ToolName));
    }
}
