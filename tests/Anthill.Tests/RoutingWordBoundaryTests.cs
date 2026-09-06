using Anthill.Core.Agents;
using Anthill.SDK.Common;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// ROUTING MATCHES WORDS, NOT LETTERS. v0.3.8.126.
///
/// WHAT WAS REPORTED. A mission whose text contained "requiring the user" was routed to
/// `coder.ui_coder`. `AntRegistry.ResolveWorker` decided the UI lane with
/// <c>text.Contains("ui")</c>, and "ui" sits inside "req·UI·ring". It also sits inside "b·ui·ld",
/// "g·ui·de", "q·ui·te", "s·ui·te" and "fl·ui·d" — all ordinary in mission prose, and "build"
/// appears in the plan of *every* mission this colony writes.
///
/// The wrong choice was then unappealable. `Pick(true, …)` marks the decision
/// `WorkerDecisionBasis.Keyword`, and `PlanningService` treats a keyword basis as final, so no
/// amount of pheromone evidence could route the task back to the worker that should have had it.
///
/// AND THIS REPOSITORY HAD ALREADY FIXED IT, ONCE. `UiChangeGate` hit the identical defect at
/// v0.3.8.96 and its comment records the diagnosis in full — "found live, twice, with two different
/// planners, before the substring was suspected". That fix never reached the worker resolver. One
/// rule, two implementations, one of them corrected: defect class #5. The two now share a matcher,
/// and the test at the bottom is what keeps them sharing it.
/// </summary>
public class RoutingWordBoundaryTests
{
    // ---- The reported failure, first ------------------------------------------------------------

    /// <summary>
    /// THE OPERATOR'S OWN PHRASE. Named as a test of its own rather than folded into a theory,
    /// because it is the sentence that was actually mis-routed and it should be the first thing
    /// anyone reading this file sees.
    /// </summary>
    [Fact]
    public void APhraseContainingRequiring_DoesNotRouteToTheUiCoder()
    {
        var (worker, decided) = AntRegistry.ResolveWorker(
            "coder", "patch_proposal", "fix the parser bug requiring the user to escape quotes");

        Assert.False(decided, "nothing in that sentence is a keyword; the choice must not be final");
        Assert.Equal("coder.backend_coder", worker?.WorkerId);
    }

    /// <summary>
    /// The whole family, because "requiring" is not special — it is one of many English words with
    /// `ui` inside it, and `build` is in the title of a task every plan contains.
    /// </summary>
    [Theory]
    [InlineData("requiring the user to confirm")]
    [InlineData("build the release pipeline")]
    [InlineData("write a guide for operators")]
    [InlineData("this is quite involved")]
    [InlineData("run the whole suite")]
    [InlineData("model the fluid dynamics")]
    [InlineData("Build final response")]          // the literal title in every generated plan
    public void WordsThatMerelyContainUi_DoNotRouteToTheUiCoder(string goal)
    {
        var (worker, _) = AntRegistry.ResolveWorker("coder", "", goal);
        Assert.Equal("coder.backend_coder", worker?.WorkerId);
    }

    /// <summary>
    /// AND REAL UI WORK STILL REACHES THE UI CODER. The half that makes the fix a fix rather than a
    /// deletion: a boundary that also stopped matching the thing it was for would be worse than the
    /// bug, and would pass a test that only checked the negatives.
    /// </summary>
    [Theory]
    [InlineData("update the ui canvas layout")]
    [InlineData("the UI needs a new panel")]
    [InlineData("restyle the frontend")]
    [InlineData("fix the css on the dashboard")]
    [InlineData("the html template is wrong")]
    public void RealUiWork_StillReachesTheUiCoder(string goal)
    {
        var (worker, decided) = AntRegistry.ResolveWorker("coder", "", goal);
        Assert.True(decided, "a real UI signal is a keyword decision");
        Assert.Equal("coder.ui_coder", worker?.WorkerId);
    }

    // ---- The sibling branches, which had the same defect ----------------------------------------

    /// <summary>
    /// "read" was a bare substring, so `al·read·y`, `th·read` and `sp·read` all sent a file task to
    /// the reader rather than the scout. Prefix matching keeps "readme" and "reading" — the
    /// inflections that are genuinely the same signal — and drops the accidents.
    /// </summary>
    [Theory]
    [InlineData("the file is already present", "file.file_scout")]
    [InlineData("check the thread count", "file.file_scout")]
    [InlineData("read the readme", "file.file_reader")]
    [InlineData("reading the config", "file.file_reader")]
    public void TheFileLane_MatchesReadAsAWordStart(string goal, string expected)
    {
        var (worker, _) = AntRegistry.ResolveWorker("file", "", goal);
        Assert.Equal(expected, worker?.WorkerId);
    }

    /// <summary>
    /// "data" caught "meta·data", which appears in the goal of anything touching artifacts, so the
    /// builder lane was decided by a word nobody typed.
    /// </summary>
    [Theory]
    [InlineData("attach the metadata to the record", "builder.response_builder")]
    [InlineData("load the dataset", "builder.result_compiler")]
    [InlineData("query the database", "builder.result_compiler")]
    public void TheBuilderLane_MatchesDataAsAWordStart(string goal, string expected)
    {
        var (worker, _) = AntRegistry.ResolveWorker("builder", "", goal);
        Assert.Equal(expected, worker?.WorkerId);
    }

    /// <summary>
    /// THE WORST OF THEM, and the one a boundary alone does not fix.
    ///
    /// The researcher lane keyed on the bare word "mission" — and `WorkerResolution` concatenates
    /// the MISSION GOAL into every task's routing text, so the word is present in essentially every
    /// composed goal the colony builds. The branch was not routing on intent, it was routing on its
    /// own scaffolding, and it therefore always chose the same worker. The signal is the phrase an
    /// operator asking about past runs actually writes.
    /// </summary>
    [Theory]
    [InlineData("summarise the mission history", "researcher.mission_researcher")]
    [InlineData("what did past missions conclude", "researcher.mission_researcher")]
    [InlineData("research the repository layout for this mission", "researcher.repo_researcher")]
    [InlineData("check permissions on the submission endpoint", "researcher.repo_researcher")]
    public void TheResearcherLane_NeedsThePhrase_NotTheWordEveryGoalContains(string goal, string expected)
    {
        var (worker, _) = AntRegistry.ResolveWorker("researcher", "", goal);
        Assert.Equal(expected, worker?.WorkerId);
    }

    // ---- The matcher itself ---------------------------------------------------------------------

    /// <summary>
    /// The two modes, stated directly, because the choice between them is the whole design and a
    /// future keyword has to be classified into one of them deliberately.
    /// </summary>
    [Fact]
    public void WordMatchesOnlyTheWholeWord_PrefixMatchesOnlyTheStartOfOne()
    {
        Assert.True(RoutingWords.Word("update the ui", "ui"));
        Assert.False(RoutingWords.Word("requiring", "ui"));
        Assert.False(RoutingWords.Word("guide", "ui"));

        Assert.True(RoutingWords.Prefix("readme", "read"));
        Assert.True(RoutingWords.Prefix("reading it", "read"));
        Assert.False(RoutingWords.Prefix("already", "read"));
        Assert.False(RoutingWords.Prefix("metadata", "data"));

        // Case-insensitive, because a goal is prose and an operator capitalises as they please.
        Assert.True(RoutingWords.Word("The UI", "ui"));

        // Empty input is not a match and not an exception: an unfilled task title is ordinary.
        Assert.False(RoutingWords.Word("", "ui"));
        Assert.False(RoutingWords.Word(null, "ui"));
    }

    /// <summary>
    /// THE WEB LANE HAD IT TOO. "search" inside "re·search" sent any goal mentioning research to
    /// the web, whatever it asked for.
    /// </summary>
    [Fact]
    public void ResearchingSomething_IsNotAWebSearch()
    {
        Assert.False(TextUtil.ShouldUseWebSearch("research the repository structure"));
        Assert.False(TextUtil.ShouldUseWebSearch("the researcher ant reads the plan"));

        // ...and an actual web signal still is one, inflections included.
        Assert.True(TextUtil.ShouldUseWebSearch("search for the latest release notes"));
        Assert.True(TextUtil.ShouldUseWebSearch("searching online for prices"));
    }

    /// <summary>
    /// ONE RULE, ONE IMPLEMENTATION. `UiChangeGate` and `AntRegistry` both decide whether something
    /// is UI work, and for two hundred releases they decided it differently — which is how one of
    /// them could be fixed while the other stayed broken. They now agree by construction, and this
    /// asserts agreement on the cases that separated them rather than on the fact that a shared
    /// helper exists.
    /// </summary>
    [Theory]
    [InlineData("requiring the user to confirm", false)]
    [InlineData("build the release pipeline", false)]
    [InlineData("update the ui canvas layout", true)]
    [InlineData("restyle the frontend", true)]
    public void TheGateAndTheResolver_AgreeOnWhatUiWorkIs(string goal, bool isUi)
    {
        Assert.Equal(isUi, UiChangeGate.LooksLikeUiWork(goal, ""));

        var (worker, _) = AntRegistry.ResolveWorker("coder", "", goal);
        Assert.Equal(isUi ? "coder.ui_coder" : "coder.backend_coder", worker?.WorkerId);
    }

    /// <summary>
    /// AND NO ROUTING BRANCH GOES BACK TO A BARE SUBSTRING.
    ///
    /// The source guard, because the behavioural tests above only cover the collisions somebody
    /// thought of. A new keyword added with `text.Contains(...)` would pass every one of them and
    /// reintroduce the defect for whichever English word happens to contain it.
    /// </summary>
    [Fact]
    public void NoWorkerRoutingBranch_DecidesOnABareSubstring()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Agents", "AntRegistry.cs")));

        var resolver = SourceText.MemberBody(source,
            source.IndexOf("internal static (AntWorkerDefinition? Worker, bool KeywordDecided) ResolveWorker",
                StringComparison.Ordinal));

        Assert.False(string.IsNullOrWhiteSpace(resolver), "ResolveWorker was renamed; repoint this guard.");

        // `.md` is the one literal left, and deliberately: a file extension is not a word, and there
        // is no word boundary in front of a dot.
        var bare = resolver.Replace("text.Contains(\".md\")", "", StringComparison.Ordinal);
        Assert.DoesNotContain("text.Contains(", bare, StringComparison.Ordinal);

        // And the vacuity floor: the resolver still routes, through the shared matcher.
        Assert.Contains("RoutingWords.", resolver, StringComparison.Ordinal);
    }
}
