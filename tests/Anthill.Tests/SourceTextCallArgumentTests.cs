using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE RESOLVER THE GUARD SWEEP IS BUILT ON. v0.3.8.112, PLAN.md §2b `.112`.
///
/// WHY THE HELPER GETS ITS OWN TESTS. Ten guards are about to read their call sites through
/// <see cref="SourceText.CallArgument"/>, and a resolver that quietly returned nothing would make
/// every one of them pass over everything — the vacuity failure this suite has now caught in three
/// separate forms. The guards themselves cannot detect it: they assert an ABSENCE of violations, and
/// a reader that sees no call sites finds no violations either.
///
/// So the cases below are the shapes this codebase actually contains, and each one is a shape that
/// broke a naive implementation while this was being written: a nested call inside an argument, a
/// collection initializer with commas in it, a named argument, an interpolated string, a verbatim
/// string holding a comma, and a constant reached through its declaring type.
/// </summary>
public class SourceTextCallArgumentTests
{
    private static readonly IReadOnlyDictionary<string, string> Constants =
        SourceText.DeclaredConstants(
            "public const string MissionStarted = \"mission_started\";\n"
          + "public const string TaskFailed = \"task_failed\";\n");

    [Fact]
    public void ALiteralArgument_IsRead()
    {
        var found = SourceText.CallArgument(@"LogEvent(id, ""task_created"", null);", "LogEvent", 1);

        Assert.Equal(new[] { "task_created" }, found);
    }

    /// <summary>
    /// THE WHOLE POINT. A call site that names a shared constant is exactly the call site a
    /// literal-only guard cannot see, and exactly the one the code is supposed to be moving toward.
    /// </summary>
    [Fact]
    public void ANamedConstant_IsResolvedToItsValue()
    {
        var found = SourceText.CallArgument(
            @"LogEvent(mission.Id, EventTypes.MissionStarted, ""started"");", "LogEvent", 1, Constants);

        Assert.Equal(new[] { "mission_started" }, found);
    }

    /// <summary>
    /// A NESTED CALL AND A COLLECTION INITIALIZER BOTH CONTAIN COMMAS, and splitting on every comma
    /// would report their fragments as arguments — shifting every later position by one and reading
    /// the wrong argument entirely. This is the shape `Memory.LogEvent(…, metadata: new() { … })`
    /// has at nearly every call site in the tree.
    /// </summary>
    [Fact]
    public void CommasInsideNestedCallsAndInitializers_DoNotSplitTheArgumentList()
    {
        const string code = """
            LogEvent(Str(row, "mission_id"), "task_failed", Truncate(x, 300),
                metadata: new() { ["a"] = 1, ["b"] = 2 });
            """;

        Assert.Equal(new[] { "task_failed" }, SourceText.CallArgument(code, "LogEvent", 1));
    }

    /// <summary>A named argument is presentation; the value is the thing being checked.</summary>
    [Fact]
    public void ANamedArgument_IsReadPast()
    {
        var found = SourceText.CallArgument(
            @"Publish(bus, eventType: EventTypes.TaskFailed);", "Publish", 1, Constants);

        Assert.Equal(new[] { "task_failed" }, found);
    }

    /// <summary>
    /// WHAT IT CANNOT READ, IT DOES NOT GUESS. An interpolated string, a concatenation and a local
    /// are all unresolved — and the callers treat unresolved as "not a name I can check" rather than
    /// as a violation. Returning something plausible here would be worse than returning nothing: a
    /// guard would then refuse a call site over a value the resolver invented.
    /// </summary>
    [Fact]
    public void WhatItCannotResolve_IsOmittedRatherThanGuessed()
    {
        Assert.Empty(SourceText.CallArgument(@"LogEvent(id, $""task_{n}"", null);", "LogEvent", 1));
        Assert.Empty(SourceText.CallArgument(@"LogEvent(id, ""task_"" + n, null);", "LogEvent", 1));
        Assert.Empty(SourceText.CallArgument(@"LogEvent(id, eventName, null);", "LogEvent", 1));
        Assert.Empty(SourceText.CallArgument(@"LogEvent(id, Unknown.Constant, null);", "LogEvent", 1, Constants));
    }

    /// <summary>
    /// A STRING CONTAINING A COMMA OR A PAREN MUST NOT END THE ARGUMENT LIST. The tree is full of
    /// these — every refusal message in the escalation lane is a sentence with punctuation in it.
    /// </summary>
    [Fact]
    public void PunctuationInsideAStringLiteral_IsNotStructure()
    {
        const string code = @"LogEvent(id, ""task_failed"", ""stopped, and it said (why)"");";

        Assert.Equal(new[] { "task_failed" }, SourceText.CallArgument(code, "LogEvent", 1));
    }

    /// <summary>Several calls in one file all report, and the method name matches on a word
    /// boundary — `MyLogEvent(` is a different method and must not be read as this one.</summary>
    [Fact]
    public void EveryCallIsReported_AndTheMethodNameMatchesOnAWordBoundary()
    {
        const string code = """
            LogEvent(a, "one", null);
            MyLogEvent(a, "not_this", null);
            LogEvent(b, "two", null);
            """;

        Assert.Equal(new[] { "one", "two" }, SourceText.CallArgument(code, "LogEvent", 1));
    }

    /// <summary>
    /// A CALL IS BOUNDED BY ITS OWN PARENTHESES, NOT BY THE NEXT SEMICOLON.
    ///
    /// `RoleContractChannelTests` bounded with `[^;]*;` and that is wrong in the direction that
    /// reports a violation which is not there: a call containing a lambda, or a sentence with a
    /// semicolon in it, was truncated before the argument the guard was looking for. Both shapes
    /// appear in the real call sites this suite reads.
    /// </summary>
    [Fact]
    public void ACallIsBoundedByItsOwnParentheses()
    {
        const string code = """
            GenerateTyped("planner", Build(x, y), antName: "planner",
                system: Prompt("planner"; "goal"), schema: PlanSchema);
            """;

        var site = Assert.Single(SourceText.CallSites(code, "GenerateTyped"));
        Assert.Contains("system:", site.Text, StringComparison.Ordinal);
        Assert.Equal("planner", site.Resolve(0, null));
        Assert.Equal(5, site.Arguments.Count);
    }

    /// <summary>
    /// AN UNBALANCED CALL IS SKIPPED RATHER THAN TRUNCATED. Half a statement is not a call, and
    /// letting a guard draw a conclusion from a fragment is how a rule comes to be enforced against
    /// text the compiler never saw as one expression.
    /// </summary>
    [Fact]
    public void AnUnbalancedCall_IsSkipped() =>
        Assert.Empty(SourceText.CallSites(@"LogEvent(id, ""task_created""", "LogEvent"));

    /// <summary>
    /// AND THE REPO-WIDE SYMBOL TABLE IS NOT EMPTY. Every guard that resolves through it degrades
    /// SILENTLY to literal-only when it is — which is the exact defect this release removes, so an
    /// empty table would leave ten guards looking fixed and behaving as they did before.
    /// </summary>
    [Fact]
    public void TheRepoWideSymbolTable_SeesTheTree()
    {
        var constants = SourceText.ConstantsAcrossSource(SourceText.RepoRoot());

        Assert.True(constants.Count >= 200,
            $"only {constants.Count} `public const string` declarations were found across src/. "
          + "Every widened guard resolves through this table and degrades silently to literal-only "
          + "when it is thin, so a small number here means the sweep has quietly been undone.");
        Assert.Equal("mission_started", constants["MissionStarted"]);
    }

    /// <summary>
    /// AND THE DECLARATION READER SEES THE REAL VOCABULARY FILE. A resolver fed an empty constant
    /// table degrades silently to literal-only — which is the exact defect being removed — so the
    /// table's own source is checked against a file the repository actually has.
    /// </summary>
    [Fact]
    public void TheDeclaredConstants_AreReadFromTheRealVocabulary()
    {
        var path = Path.Combine(SourceText.RepoRoot(), "src", "Anthill.SDK", "Events", "EventTypes.cs");
        Assert.True(File.Exists(path), "EventTypes.cs has moved; the sweep reads nothing.");

        var declared = SourceText.DeclaredConstants(SourceText.CodeOnly(File.ReadAllText(path)));

        Assert.True(declared.Count >= 50,
            $"only {declared.Count} event constants were parsed from EventTypes.cs. The vocabulary "
          + "has far more than that, so the declaration reader has stopped seeing the shape they "
          + "are written in — and every guard resolving through it has quietly become literal-only "
          + "again.");
        Assert.Equal("mission_started", declared["MissionStarted"]);
    }
}
