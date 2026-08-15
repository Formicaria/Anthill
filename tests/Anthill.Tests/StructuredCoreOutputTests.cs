using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The core ants' output stops being a string — where it genuinely has a shape. v0.3.8.57.
///
/// v3.8.21 refused to type the researcher, builder and verifier, and its reasoning was sound:
/// "giving prose a schema name would be relabelling, which is the 'two channels and the prose one
/// wins' failure ADR-004 exists to prevent." What it missed is that the RESEARCHER'S PROMPT already
/// demands a shape and has since the ant was written — four named sections, produced by the model and
/// then flattened into a string that nothing parsed. Recovering a shape the producer was already
/// asked for is a different act from inventing one.
///
/// THE BUILDER IS DELIBERATELY NOT TYPED, and that is the load-bearing half of this change. Its
/// prompt asks for "a practical final response" in 200-400 words with no sections at all. Structuring
/// it honestly means first changing what it is asked to produce — a behaviour change to the
/// operator-facing answer — and doing it by parsing would be exactly the relabelling above. The
/// ledger below records that as a decision rather than leaving it as an omission someone later reads
/// as an oversight.
/// </summary>
public class StructuredCoreOutputTests
{
    private const string WellFormed = """
        - Relevant Memory:
        Two prior missions touched the patch applier.

        - Useful Tool Context:
        repository_index reported 412 files.

        - Pheromone Guidance:
        The coder trail is strong for this task type.

        - Research Need:
        Whether PatchApply refuses an add over an existing file.
        """;

    // -------------------------------------------------------------------------------------------
    // Parsing
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void AWellFormedBrief_ParsesIntoItsSections()
    {
        var brief = ResearchBrief.TryParse(WellFormed);

        Assert.NotNull(brief);
        Assert.Contains("patch applier", brief!.RelevantMemory);
        Assert.Contains("412 files", brief.ToolContext);
        Assert.Contains("coder trail", brief.PheromoneGuidance);
        Assert.Contains("refuses an add", brief.ResearchNeed);
    }

    /// <summary>
    /// Models drop the bullet, bold the label, or change the case. Matching the LABEL rather than the
    /// exact line keeps this a format check rather than a formatting check — a parser that fails on
    /// `**Relevant Memory:**` would report almost every real response as unstructured and the feature
    /// would quietly never fire.
    /// </summary>
    [Theory]
    [InlineData("**Relevant Memory:** a\n**Useful Tool Context:** b\n**Pheromone Guidance:** c\n**Research Need:** d")]
    [InlineData("Relevant Memory: a\nUseful Tool Context: b\nPheromone Guidance: c\nResearch Need: d")]
    [InlineData("* relevant memory: a\n* useful tool context: b\n* pheromone guidance: c\n* research need: d")]
    public void CommonFormattingVariants_StillParse(string text) =>
        Assert.NotNull(ResearchBrief.TryParse(text));

    /// <summary>
    /// A missing section means the format was not followed, and that is reported as NOT PARSED rather
    /// than as an empty section. Accepting a partial would collapse "the researcher found no
    /// pheromone guidance" into "the model ignored the format", and every consumer downstream would
    /// read the second as the first.
    /// </summary>
    [Fact]
    public void ABriefMissingASection_DoesNotParse()
    {
        var missingOne = """
            - Relevant Memory:
            something

            - Useful Tool Context:
            something else

            - Research Need:
            the question
            """;

        Assert.Null(ResearchBrief.TryParse(missingOne));
    }

    /// <summary>
    /// But a section that is PRESENT AND EMPTY is fine. A researcher with nothing to say under a
    /// heading says so, and that is a real answer.
    /// </summary>
    [Fact]
    public void AnEmptySectionIsAnAnswer_AndStillParses()
    {
        var brief = ResearchBrief.TryParse("""
            - Relevant Memory:
            - Useful Tool Context:
            - Pheromone Guidance:
            - Research Need:
            whether the applier refuses
            """);

        Assert.NotNull(brief);
        Assert.Equal("", brief!.RelevantMemory);
        Assert.Contains("refuses", brief.ResearchNeed);
    }

    [Fact]
    public void PlainProse_DoesNotParse() =>
        Assert.Null(ResearchBrief.TryParse("The codebase is large and the applier looks correct to me."));

    /// <summary>
    /// Sections answered out of order still parse, and each body belongs to its own heading. Bodies
    /// run to the next heading BY POSITION; reading to the next heading in prompt order would splice
    /// one section's text onto another's, which is a wrong answer that looks like a right one.
    /// </summary>
    [Fact]
    public void SectionsAnsweredOutOfOrder_KeepTheirOwnBodies()
    {
        var brief = ResearchBrief.TryParse("""
            - Research Need:
            NEED-TEXT

            - Relevant Memory:
            MEMORY-TEXT

            - Pheromone Guidance:
            PHEROMONE-TEXT

            - Useful Tool Context:
            TOOL-TEXT
            """);

        Assert.NotNull(brief);
        Assert.Equal("NEED-TEXT", brief!.ResearchNeed);
        Assert.Equal("MEMORY-TEXT", brief.RelevantMemory);
        Assert.Equal("PHEROMONE-TEXT", brief.PheromoneGuidance);
        Assert.Equal("TOOL-TEXT", brief.ToolContext);
    }

    [Fact]
    public void ABrief_SurvivesItsOwnJson()
    {
        var round = ResearchBrief.FromJson(ResearchBrief.TryParse(WellFormed)!.ToJson());

        Assert.NotNull(round);
        Assert.Contains("412 files", round!.ToolContext);
    }

    // -------------------------------------------------------------------------------------------
    // The parser and the prompt are one contract stated twice
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Every heading the parser looks for must still appear in the researcher's prompt.
    ///
    /// This is the failure mode that would otherwise be invisible: someone reworded the prompt, the
    /// parser stopped matching, and the researcher silently reported every response as unstructured
    /// forever. Nothing would break — there would just be no research_brief artifacts, and no reason
    /// for anyone to look.
    /// </summary>
    [Fact]
    public void EveryHeadingTheParserExpects_IsStillInTheResearchersPrompt()
    {
        var source = File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Agents", "Ants.cs"));

        foreach (var heading in ResearchBrief.Headings)
            Assert.True(source.Contains(heading, StringComparison.Ordinal),
                $"the parser looks for a '{heading}' section and the researcher's prompt no longer asks "
              + "for one. Either restore the heading or change ResearchBrief.Headings — a parser and a "
              + "prompt that disagree produce zero artifacts and no error.");
    }

    /// <summary>
    /// And the researcher actually calls it, refusing to emit an artifact when the parse fails. The
    /// parser is reachable code or it is decoration.
    /// </summary>
    [Fact]
    public void TheResearcher_EmitsTheBriefAndDisclosesWhenItCannot()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Agents", "Ants.cs")));

        Assert.Contains("ResearchBrief.TryParse(call.Content)", source);
        Assert.Contains("\"research_brief\"", source);
        Assert.Contains("unstructured_research_output", source);
    }

    // -------------------------------------------------------------------------------------------
    // What is NOT structured, and why
    // -------------------------------------------------------------------------------------------

    /// <param name="ArtifactKind">
    /// The AntArtifact kind this ant emits, or null when it emits none. Explicit, because the first
    /// draft inferred it with a regex built from the role name — which for "builder" searched for
    /// `WithArtifact(text, "bu` and would have passed no matter what the builder did.
    /// </param>
    private sealed record CoreAnt(string Role, bool Structured, string? ArtifactKind, string Why);

    private static readonly CoreAnt[] Ledger =
    {
        new("researcher", true, "research_brief",
            "Its prompt has demanded four named sections since the ant was written. The shape was "
          + "produced and discarded; ResearchBrief recovers it."),

        new("builder", false, null,
            "Its prompt asks for 'a practical final response' in 200-400 words with NO sections. "
          + "There is no shape to recover, so typing it would be the relabelling ADR-004 rejects. "
          + "Structuring the builder honestly means changing what it is asked to produce first — a "
          + "behaviour change to the operator-facing answer, not a parser."),

        new("verifier", false, null,
            "Produces a verdict whose STRUCTURE already lives elsewhere: Evidence rows and the "
          + "verification_bundle artifact carry the machine-readable result, and v0.3.8.57 gave "
          + "Evidence the revision identity. Its prose is commentary on a decision already recorded "
          + "in typed form."),
    };

    /// <summary>
    /// The ledger's claims about which ants are structured must match the code. A builder that grew a
    /// schema without this entry changing would mean someone typed prose and nobody argued about it.
    /// </summary>
    [Fact]
    public void TheLedgerMatchesWhichCoreAntsActuallyEmitTypedArtifacts()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Agents", "Ants.cs")));

        foreach (var ant in Ledger)
        {
            Assert.Equal(ant.Structured, ant.ArtifactKind is not null);

            var emits = ant.ArtifactKind is not null
                     && source.Contains($"\"{ant.ArtifactKind}\"", StringComparison.Ordinal);

            Assert.True(emits == ant.Structured,
                ant.Structured
                    ? $"the ledger says {ant.Role} emits a typed artifact and the code no longer does: {ant.Why}"
                    : $"{ant.Role} now emits a typed artifact, and the ledger says it should not: {ant.Why}. "
                    + "If that reasoning has changed, change the ledger — silently typing prose is the "
                    + "failure ADR-004 names.");
        }
    }
}
