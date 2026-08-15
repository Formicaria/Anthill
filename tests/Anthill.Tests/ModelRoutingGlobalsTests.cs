using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A test that MUTATES a model-routing global must be serialized with the tests that RUN missions.
/// v0.3.8.60.
///
/// HOW THIS WAS FOUND, because the diagnosis matters more than the fix. `ColonyAcceptanceTests`
/// ScenarioA began failing — mission `failed` instead of `complete`, after almost exactly twenty
/// seconds, twice. I first attributed it to a performance regression in `PathContainment` and
/// optimised that resolver on the strength of the guess. The optimisation was worth having on its
/// own merits and it changed nothing here: the second run failed at 19.9s against the first at
/// 20.1s, which is the tell — a fixed cost, not a load-dependent one.
///
/// The fixed cost is a network timeout. `AnthillRuntime.UseOllama` is a MUTABLE STATIC that every
/// ant reads to decide between calling a model and taking its deterministic offline path. When
/// ScenarioA runs with it false, its three tasks finish in milliseconds and the mission completes.
/// When another test flips it true mid-run — `ModelReliabilityTests` does exactly that — the same
/// three tasks each try to reach a model that is not there, spend the connect timeout failing, fail
/// critically, and the mission is `failed`.
///
/// So this was never caused by the source changes around it. It is a pre-existing race that adding
/// three new test classes made land on the wrong side, because more classes changes how xUnit
/// schedules the parallel ones. A race that reproduces reliably is still a race; it just stopped
/// being lucky.
///
/// TWO COLLECTIONS EXISTED FOR THIS, WHICH IS WHY NEITHER WORKED. `ColonyAcceptanceTests` carried
/// <c>[Collection("specialist-gates")]</c> — "gate toggles are static; serialize with the other
/// togglers" — while `DirectorTests` defined <c>[CollectionDefinition("Autonomy")]</c> "so the
/// autonomy tests never race on global runtime flags". Same shared resource, same stated reason, two
/// names. xUnit runs different collections IN PARALLEL WITH EACH OTHER, so thirty-two classes were
/// serialized against each other, twelve were serialized against each other, and the two groups
/// raced freely.
///
/// The first attempt at this fix added the second collection attribute to four classes that already
/// had the first, which does not compile — a class belongs to one collection. That failure was
/// useful: it is the compiler saying the two names are the same resource.
///
/// Merged into one. This guard is the membership check neither collection had, plus the invariant
/// that made them ineffective: there is ONE name.
/// </summary>
public class ModelRoutingGlobalsTests
{
    /// <summary>The statics an in-flight mission reads to decide whether to call a model at all.</summary>
    private static readonly string[] RoutingGlobals =
    {
        "AnthillRuntime.UseOllama = ",
        "AnthillRuntime.EnableModelRouting = ",
    };

    private const string Collection = "Collection(\"specialist-gates\")";

    [Fact]
    public void EveryTestThatMutatesAModelRoutingGlobal_IsSerializedWithTheMissionTests()
    {
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(SourceText.RepoRoot(), "tests", "Anthill.Tests"), "*.cs"))
        {
            // COMMENTS BLANKED FIRST. The doc above quotes both the globals and the attribute in
            // order to explain them, and a guard that matches its own explanation is the trap this
            // repository keeps re-finding — it fired on the first run of the check below, against
            // the paragraph describing the merge.
            var source = SourceText.CodeOnly(File.ReadAllText(path));
            if (Path.GetFileName(path) == "ModelRoutingGlobalsTests.cs") continue;

            if (RoutingGlobals.Any(g => source.Contains(g, StringComparison.Ordinal))
                && !source.Contains(Collection, StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(path));
        }

        Assert.True(offenders.Count == 0,
            "these test classes mutate a model-routing global and are NOT in the "
          + "\"specialist-gates\" collection: " + string.Join(", ", offenders)
          + ". They therefore run in parallel with the tests that execute real missions, and a "
          + "mission reads UseOllama at every ant dispatch. Flipping it mid-mission sends ants that "
          + "should take the offline path to a model that is not there, where they spend the connect "
          + "timeout and fail critically. Add the collection attribute.");
    }

    /// <summary>
    /// ONE collection name, which is the invariant the two-collection version broke.
    ///
    /// Membership checks are worth nothing if a second collection exists: every class can be a
    /// correct member of its own group and still race the other group. This is the assertion that
    /// would have failed before the merge, and the one a future split would fail again.
    /// </summary>
    [Fact]
    public void ThereIsExactlyOneCollection_ForProcessGlobalState()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(SourceText.RepoRoot(), "tests", "Anthill.Tests"), "*.cs"))
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(
                         SourceText.CodeOnly(File.ReadAllText(path)),
                         @"Collection(?:Definition)?\(""(?<name>[^""]+)""\)"))
                names.Add(m.Groups["name"].Value);

        Assert.True(names.Count <= 1,
            "the suite has more than one test collection: " + string.Join(", ", names)
          + ". Different collections run in PARALLEL with each other, so two collections guarding the "
          + "same process-global state guard nothing — which is exactly how a mission-running test "
          + "came to race a test that flips UseOllama.");
    }

    /// <summary>
    /// And the mission-running tests are IN it, so the serialization has something to serialize
    /// against. Half a mechanism is the state this defect lived in.
    /// </summary>
    [Theory]
    [InlineData("ColonyAcceptanceTests.cs")]
    [InlineData("CodePatchLifecycleTests.cs")]
    public void TheMissionRunningTests_AreInTheCollection(string file) =>
        Assert.Contains(Collection, SourceText.CodeOnly(File.ReadAllText(
            Path.Combine(SourceText.RepoRoot(), "tests", "Anthill.Tests", file))));
}
