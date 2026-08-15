using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// PLAN.md acceptance gate 7 — a UI change cannot reach the coder without a valid `ui_map`.
///
/// `Planner.InjectSpecialistRouting` has inserted a cartographer ahead of the coder since Stage E and
/// was never the gate, for three reasons that each look small and are not:
///
///   1. GOAL TEXT ONLY. "Fix the broken button handler" with a task pointing at
///      `src/Anthill.UI/app.js` matched no keyword and was mapped by nobody.
///   2. A DEPENDENCY, not a requirement. The coder waited for the cartographer's task to FINISH —
///      including finishing by failing, or by producing no artifact. "Waited for a role" and "has a
///      map" are different claims and only the second is useful.
///   3. PLANNING TIME. A structural guarantee cannot live where a model has a say.
///
/// So the gate now decides at dispatch, from the store. The planner injection stays, because it is
/// what makes the map exist — a gate with no producer would just block UI work.
/// </summary>
public class UiChangeGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_uigate_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        var memory = new SqliteMemory(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
        memory.SaveMission(new Mission { Id = "m1", Goal = "redesign the dashboard page" });
        return memory;
    }

    private static Task Coder(string title = "Change the layout", string description = "update it") =>
        new() { Id = "t1", Title = title, Description = description, AssignedAnt = "coder", TaskType = "code_change" };

    private static Mission UiMission => new() { Id = "m1", Goal = "redesign the dashboard page" };
    private static Mission BackendMission => new() { Id = "m1", Goal = "speed up the sqlite writes" };

    private const string ValidMap = """{"routes":["/"],"api_calls":[],"files_examined":["app.js"]}""";

    // -------------------------------------------------------------------------------------------
    // Detection
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("redesign the dashboard page", null)]
    [InlineData("fix the frontend", null)]
    [InlineData("update the css", null)]
    // THE GAP THAT EXISTED: a goal with no UI word, and a task that names a UI file.
    [InlineData("fix the broken button handler", "edit src/Anthill.UI/app.js")]
    [InlineData("make it work again", "the styles in public/site.css are wrong")]
    [InlineData("repair the component", "components/Header.tsx renders twice")]
    public void UiWork_IsRecognisedFromTheGoalOrTheTasksPaths(string goal, string? taskText) =>
        Assert.True(UiChangeGate.LooksLikeUiWork(goal, taskText));

    [Theory]
    [InlineData("speed up the sqlite writes", "src/Anthill.Core/Memory/SqliteMemory.cs")]
    [InlineData("add a failure taxonomy", "src/Anthill.SDK/Contracts/FailureClass.cs")]
    public void BackendWork_IsNotTreatedAsUiWork(string goal, string taskText) =>
        Assert.False(UiChangeGate.LooksLikeUiWork(goal, taskText));

    /// <summary>
    /// The planner and the gate ask the same question. Two keyword lists would drift, and the drift
    /// is silent in the worst direction: the planner routes a set the gate does not guard, so a UI
    /// change gets a map it is not required to have while another gets neither.
    /// </summary>
    [Fact]
    public void ThePlannerAndTheGate_ShareOneDetector()
    {
        var planner = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Planning", "Planner.cs")));

        Assert.Contains("UiChangeGate.LooksLikeUiWork", planner);
        // And the old inline list is gone, rather than kept beside the shared call.
        Assert.DoesNotContain("\"frontend\", \"page\", \"css\"", planner);
    }

    // -------------------------------------------------------------------------------------------
    // The gate
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void ACoderOnUiWorkWithNoMap_IsRefused()
    {
        using var memory = Memory();

        var decision = UiChangeGate.Check(Coder(), UiMission, (IArtifactStore)memory, cartographerAvailable: true);

        Assert.False(decision.Allowed);
        Assert.Contains("no ui_map", decision.Reason);
    }

    [Fact]
    public void ACoderOnUiWorkWithAValidMap_ProceedsNormally()
    {
        using var memory = Memory();
        ((IArtifactStore)memory).Put(Artifact.Create(ArtifactSchemas.UiMap, "ui_cartographer", "m1", ValidMap));

        Assert.True(UiChangeGate.Check(Coder(), UiMission, (IArtifactStore)memory, true).Allowed);
    }

    /// <summary>
    /// PRESENT is not VALID, and this is the assertion that separates this gate from an existence
    /// check. A truncated payload under a `ui_map` label passes "the artifact exists" and the coder
    /// then plans against a map that is not one.
    /// </summary>
    [Fact]
    public void AMapThatDoesNotConformToItsSchema_IsNotAValidMap()
    {
        using var memory = Memory();
        ((IArtifactStore)memory).Put(Artifact.Create(
            ArtifactSchemas.UiMap, "ui_cartographer", "m1", "{\"routes\":[\"/\"],  <-- truncated"));

        var decision = UiChangeGate.Check(Coder(), UiMission, (IArtifactStore)memory, true);

        Assert.False(decision.Allowed);
        Assert.Contains("not usable", decision.Reason);
    }

    /// <summary>
    /// One good map among several bad ones is enough. The requirement is that a usable map EXISTS,
    /// not that every artifact ever labelled `ui_map` is pristine — a mission that mapped twice and
    /// got one bad read has still been mapped.
    /// </summary>
    [Fact]
    public void OneUsableMapAmongSeveral_IsEnough()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        store.Put(Artifact.Create(ArtifactSchemas.UiMap, "ui_cartographer", "m1", "not json at all"));
        store.Put(Artifact.Create(ArtifactSchemas.UiMap, "ui_cartographer", "m1", ValidMap));

        Assert.True(UiChangeGate.Check(Coder(), UiMission, store, true).Allowed);
    }

    /// <summary>
    /// A disabled cartographer is a REFUSAL WITH A NAME, not a silent pass. The planner's routing
    /// simply skipped insertion when the role was unavailable, so the coder proceeded unmapped and
    /// nothing anywhere said why.
    /// </summary>
    [Fact]
    public void AUiChangeWithNoCartographerAvailable_IsRefusedAndSaysWhy()
    {
        using var memory = Memory();

        var decision = UiChangeGate.Check(Coder(), UiMission, (IArtifactStore)memory, cartographerAvailable: false);

        Assert.False(decision.Allowed);
        Assert.Contains("ui_cartographer is not available", decision.Reason);
    }

    // -------------------------------------------------------------------------------------------
    // What the gate must NOT do
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void ANonUiCoderTask_IsUntouched()
    {
        using var memory = Memory();

        Assert.True(UiChangeGate.Check(
            Coder("Speed up writes", "src/Anthill.Core/Memory/SqliteMemory.cs"),
            BackendMission, (IArtifactStore)memory, true).Allowed);
    }

    /// <summary>
    /// Only the CODER. The gate reasons about proposing a change against a frontend; a tester or a
    /// researcher on a UI mission is doing something else, and blocking them would be the gate
    /// widening past the thing it thought about.
    /// </summary>
    [Theory]
    [InlineData("tester")]
    [InlineData("researcher")]
    [InlineData("ui_cartographer")]
    public void OtherRolesOnUiMissions_AreUntouched(string role)
    {
        using var memory = Memory();
        // Task is a class, not a record — no `with`. Built directly rather than mutating the shared
        // helper's return, so the roles in this theory cannot leak into each other.
        var task = new Task
        {
            Id = "t1", Title = "Change the layout", Description = "update it",
            AssignedAnt = role, TaskType = "code_change",
        };

        Assert.True(UiChangeGate.Check(task, UiMission, (IArtifactStore)memory, true).Allowed);
    }

    /// <summary>
    /// No store means the gate cannot verify anything, and it allows. A missing store is evidence
    /// about the WIRING, not about the mission — failing closed on it would block every caller that
    /// constructs a coder without one, which is most tests and the CLI, and would look exactly like
    /// a real finding.
    /// </summary>
    [Fact]
    public void WithNoArtifactStore_TheGateDoesNotInventAVerdict() =>
        Assert.True(UiChangeGate.Check(Coder(), UiMission, artifacts: null, cartographerAvailable: true).Allowed);

    // -------------------------------------------------------------------------------------------
    // Reachability
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The gate runs on the DISPATCH path. A decision function nothing calls is the defect this
    /// release has spent its length digging out — and it would be especially invisible here, because
    /// the planner's injection means most UI missions get a map anyway and the gate never firing
    /// would look like the gate working.
    /// </summary>
    [Fact]
    public void TheGate_IsEnforcedAtDispatch()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));

        Assert.Contains("UiChangeGate.Check(", source);
        Assert.Contains("ui_change_blocked_unmapped", source);
        // BLOCKED, not failed: the condition is curable by running the cartographer, and a failure
        // would spend a bounded repair budget on something no repair addresses.
        Assert.Contains("AntExecutionResult.Blocked(uiGate.Reason)", source);
    }

    /// <summary>
    /// And the plan records the gate as closed, on the same commit that closes it.
    /// </summary>
    [Fact]
    public void PlanAcceptanceGateSeven_IsRecordedAsClosed()
    {
        var gate = SourceText.PlanAcceptanceGate(7);

        Assert.Contains("✅", gate);
        Assert.Contains("v0.3.8.57", gate);
    }
}
