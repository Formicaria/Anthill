using Anthill.Core.Agents;
using Anthill.Core.Domain;   // ToolResult — the dispatch-level one
using Anthill.Core.Memory;
using Anthill.Core.Tools;
using Xunit;

// Anthill.Core.Contracts ALSO declares a ToolResult, so importing both namespaces makes the name
// ambiguous. Aliasing the two types actually used keeps this file unambiguous and makes the
// still-open design question visible: there are two same-named result types, and the dispatch one
// tested here is the one every ITool returns.
using FailureClass = Anthill.SDK.Contracts.FailureClass;
using FailureClassify = Anthill.SDK.Contracts.FailureClassify;

namespace Anthill.Tests;

/// <summary>
/// v3.4.0 (ADR-006) — the tool result says WHY it failed, typed.
///
/// The gap this closes: the registry already distinguished "no such tool", "you may not run that"
/// and "it threw", then flattened all three into one sentence and a false boolean, with
/// `authorization_denied:` left as a prose marker for callers to match on. That is the same
/// recover-the-status-by-reading-the-text pattern v3.2.0 removed from the ant contract, one layer
/// down — and it mattered more here, because the agent loop's whole job is deciding what to do next.
///
/// The behaviour under test is therefore not "the field is populated" but "the three failures a
/// model can respond to differently are told apart, and produce different instructions".
/// </summary>
public class ToolFailureClassTests
{
    private sealed class ThrowingTool : ITool
    {
        private readonly Exception _error;
        public ThrowingTool(Exception error) => _error = error;
        public string Name => "explodes";
        public string Description => "throws on purpose";
        public ToolResult Run(IReadOnlyDictionary<string, object?> args) => throw _error;
    }

    /// <summary>Registered under a real tool name so the denial path is reached rather than not-found.</summary>
    private sealed class NoOpTool : ITool
    {
        public string Name { get; init; } = "";
        public string Description => "does nothing";
        public ToolResult Run(IReadOnlyDictionary<string, object?> args) => new(Name, true, "ok");
    }

    private sealed class FailingTool : ITool
    {
        public string Name => "fails";
        public string Description => "returns a classified failure";
        public ToolResult Run(IReadOnlyDictionary<string, object?> args) =>
            new(Name, false, "", "upstream is down", FailureClass.TransientProviderFailure);
    }

    private static ToolRegistry Registry(params ITool[] tools)
    {
        var registry = new ToolRegistry(new SqliteMemory(":memory:"));
        foreach (var tool in tools) registry.Register(tool);
        return registry;
    }

    // ---- the registry classifies what it already knew ------------------------------------------

    /// <summary>
    /// A tool that does not exist is the CALLER's mistake, not a defect — the model named something
    /// it was not offered, and it can fix that by choosing from the list it was given.
    /// </summary>
    [Fact]
    public void AnUnknownTool_IsAValidationFailure()
    {
        var result = Registry().RunTool("no-such-tool");

        Assert.False(result.Success);
        Assert.Equal(FailureClass.ValidationFailure, result.Failure);
    }

    /// <summary>
    /// The denial that used to be discoverable only by reading the error text. Both are asserted:
    /// the class because that is what callers must branch on, and the absence of a status-bearing
    /// prefix requirement because nothing may go back to matching on prose.
    /// </summary>
    [Fact]
    public void ADeniedTool_IsAnAuthorizationFailure_AndIsNotRetryable()
    {
        // apply_patch is structurally forbidden to every mission agent — a real denial, not a
        // fabricated one. It must be REGISTERED, or the lookup fails first and this would silently
        // become a not-found test that happens to pass for the wrong reason.
        var result = Registry(new NoOpTool { Name = "apply_patch" })
            .RunTool("apply_patch", antName: "researcher");

        Assert.False(result.Success);
        Assert.Equal(FailureClass.AuthorizationFailure, result.Failure);
        Assert.False(result.Retryable);
    }

    /// <summary>
    /// A tool that throws without ever considering failure still gets a class, from the only honest
    /// evidence available: the exception type.
    /// </summary>
    [Theory]
    [InlineData(typeof(TimeoutException), FailureClass.Timeout)]
    [InlineData(typeof(HttpRequestException), FailureClass.TransientProviderFailure)]
    [InlineData(typeof(UnauthorizedAccessException), FailureClass.AuthorizationFailure)]
    [InlineData(typeof(ArgumentException), FailureClass.ValidationFailure)]
    public void AThrownException_IsClassifiedByType(Type exception, FailureClass expected) =>
        Assert.Equal(expected, ToolRegistry.ClassifyThrown((Exception)Activator.CreateInstance(exception)!));

    /// <summary>
    /// The load-bearing default. An unrecognised fault must NOT be guessed as transient: retrying a
    /// deterministic crash is how one broken tool consumes an entire mission's budget.
    /// </summary>
    [Fact]
    public void AnUnrecognisedException_IsAnInternalDefect_AndIsNeverRetryable()
    {
        var result = Registry(new ThrowingTool(new InvalidOperationException("bang"))).RunTool("explodes");

        Assert.Equal(FailureClass.InternalDefect, result.Failure);
        Assert.False(result.Retryable);
    }

    /// <summary>A tool that classifies its own failure keeps that class — the registry does not overwrite it.</summary>
    [Fact]
    public void AToolsOwnClassification_Survives()
    {
        var result = Registry(new FailingTool()).RunTool("fails");

        Assert.Equal(FailureClass.TransientProviderFailure, result.Failure);
        Assert.True(result.Retryable);
    }

    /// <summary>
    /// Failing without saying why is a defect, not a fourth state. Otherwise "succeeded" and "failed
    /// for unstated reasons" would both read as None and callers could not tell them apart.
    /// </summary>
    [Fact]
    public void AnUnexplainedFailure_IsADefect_NotNone()
    {
        Assert.Equal(FailureClass.InternalDefect, new ToolResult("t", false, "", "it just failed").Failure);
        Assert.Equal(FailureClass.None, new ToolResult("t", true, "fine").Failure);
    }

    /// <summary>
    /// Retryable is DERIVED, never stored, so it cannot contradict the class it describes — one
    /// definition of retryable in the codebase rather than two that drift apart.
    /// </summary>
    [Fact]
    public void Retryable_AgreesWithTheSharedDefinition()
    {
        foreach (var each in Enum.GetValues<FailureClass>())
            Assert.Equal(FailureClassify.IsRetryable(each),
                new ToolResult("t", false, "", "e", each).Retryable);
    }

    // ---- and the loop turns the class into a different instruction ------------------------------

    /// <summary>
    /// The reason the classes exist. Told only "the tool failed", a model has exactly one move —
    /// call it again — and it will until the budget is gone. These three failures each need a
    /// different next move, so they must produce different text.
    /// </summary>
    [Fact]
    public void EachFailureClass_TellsTheModelSomethingDifferent()
    {
        string For(FailureClass c) => ToolCallingLoop.Guidance(new ToolResult("t", false, "", "e", c));

        var denied = For(FailureClass.AuthorizationFailure);
        var invalid = For(FailureClass.ValidationFailure);
        var transient = For(FailureClass.TransientProviderFailure);
        var defect = For(FailureClass.InternalDefect);

        Assert.Equal(4, new HashSet<string> { denied, invalid, transient, defect }.Count);

        // a denial must steer AWAY from the tool; a transient failure must steer back TO it
        Assert.Contains("not call it again", denied);
        Assert.Contains("may succeed", transient);
        Assert.Contains("arguments", invalid);
        Assert.Contains("will repeat", defect);
    }

    // ---- and the classes cannot quietly rot ------------------------------------------------------

    /// <summary>
    /// Source guard: every failure a shipped tool returns must NAME its class.
    ///
    /// Without this the feature decays silently and in the worst direction. An unclassified failure
    /// still compiles, still returns false, and defaults to InternalDefect — so a new tool's
    /// "missing required argument" would tell the model "this will repeat, change approach" when the
    /// correct advice is "fix the arguments and call again". Nothing fails; the agent just gets
    /// worse at recovering, which is not a symptom anyone goes looking for.
    ///
    /// A source-text guard rather than a runtime one because the failures live on branches that need
    /// specific config gates and filesystem states to reach; the property being protected is a
    /// property of the CODE, so the code is what gets asserted.
    /// </summary>
    [Theory]
    [InlineData("src/Anthill.Core/Tools/Tools.cs")]
    [InlineData("src/Anthill.Core/Tools/CheckRunner.cs")]
    public void EveryToolFailureInTheSource_NamesItsFailureClass(string file)
    {
        var source = File.ReadAllText(RepoFile(file));
        var unclassified = new List<string>();

        // Whole STATEMENTS, not lines. The first version of this guard read line by line and fired
        // on a perfectly well-classified failure that simply wrapped across three lines — a guard
        // that constrains formatting rather than behaviour, which people fix by reformatting until
        // it stops complaining. Scanning to the statement's closing paren asks the question actually
        // worth asking: does this construction name a class anywhere in it?
        const string marker = "new ToolResult(Name, false";
        for (var at = source.IndexOf(marker, StringComparison.Ordinal); at >= 0;
             at = source.IndexOf(marker, at + marker.Length, StringComparison.Ordinal))
        {
            var end = source.IndexOf(");", at, StringComparison.Ordinal);
            if (end < 0) end = source.Length;
            var statement = source[at..end];

            if (statement.Contains("FailureClass.") || statement.Contains("ClassifyThrown")) continue;

            var line = source.Take(at).Count(c => c == '\n') + 1;
            unclassified.Add($"  {file}:{line}: {statement.Split('\n')[0].Trim()}");
        }

        Assert.True(unclassified.Count == 0,
            "These tool failures do not say why they failed, so they default to InternalDefect and "
          + "the model will be told to change approach when it may only need to fix its arguments:\n"
          + string.Join("\n", unclassified));
    }

    /// <summary>Walk up to the repo root so the guard does not depend on the test runner's cwd.</summary>
    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative))) dir = dir.Parent;
        Assert.True(dir is not null, $"could not locate {relative} from {AppContext.BaseDirectory}");
        return Path.Combine(dir!.FullName, relative);
    }
}
