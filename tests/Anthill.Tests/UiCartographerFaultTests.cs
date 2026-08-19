using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Security;
using Anthill.Core.Tools;
using Anthill.Modules.Tools;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// The cartographer's FAULT proof. v0.3.8.81 (PLAN.md §2 R3).
///
/// WHY THIS FILE EXISTS AT ALL. `RoleQualificationRecordTests` carried a null in exactly one
/// non-cancellation cell of the whole graduation record: `ui_cartographer/fault`. It was null
/// honestly — nothing proved what the cartographer does when the tool it depends on FAILS.
///
/// The adjacent test that did exist, and why it is not this one.
/// `UiCartographerAntTests.WorkspaceWithNoUiFiles_FailsAsDependency_NotSuccess` drives an EMPTY
/// workspace: the listing succeeds and returns nothing. That is a fault about the INPUT. The branch
/// nobody exercised is `SpecialistAnts.cs` — `if (!listing.Success) return Failed(DependencyFailure,
/// "workspace listing unavailable")` — a fault about the TOOL, where the ant is told nothing at all
/// rather than told there is nothing. Those two look alike in a summary and differ in the only place
/// it matters: what the ant does next.
///
/// WHAT IT WOULD COST TO BE WRONG. The cartographer's output is the `ui_map` that acceptance gate 7
/// requires before a UI change may reach the coder. If a broken listing tool produced an EMPTY map
/// rather than a refusal, the gate would be handed a well-formed map of nothing — and v0.3.8.64 had
/// to make `{}` stop conforming to the schema for precisely this reason. A map that says "this UI has
/// no routes" because the tool was down is not a weaker map; it is a false one, and the gate cannot
/// tell the difference.
/// </summary>
public class UiCartographerFaultTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "anthill_uicfault_" + Guid.NewGuid().ToString("N")[..10]);

    private SqliteMemory? _memory;

    public void Dispose()
    {
        _memory?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A tool that fails the way a tool fails: unsuccessfully, with a class, and no output.</summary>
    private sealed class FailingTool(string name, FailureClass failure) : ITool
    {
        public string Name => name;
        public string Description => "Test double: always fails.";

        public ToolResult Run(IReadOnlyDictionary<string, object?> args) =>
            new(Name, success: false, output: "", error: $"{name} is unavailable", failure: failure);
    }

    private (UiCartographerAnt Ant, DomainTask Task, Mission Mission) Harness(params ITool[] tools)
    {
        Directory.CreateDirectory(_dir);
        var workspace = Path.Combine(_dir, "ws");
        Directory.CreateDirectory(workspace);
        // A REAL UI file is present. If the ant ever maps anything here it is because it read the
        // disk despite its tool failing, which would be a different and worse defect than the one
        // under test — so the workspace is stocked rather than empty on purpose.
        File.WriteAllText(Path.Combine(workspace, "index.html"),
            "<div id=\"page-overview\"></div><script>function loadColony(){ api('/colony/registry'); }</script>");

        _memory = new SqliteMemory(Path.Combine(_dir, "fault.db"));
        var registry = new ToolRegistry(_memory);
        var guard = new WorkspacePathGuard(workspace);
        registry.Register(new DirectoryListTool(guard));
        registry.Register(new ReadTextFileTool(guard));
        // Last-write-wins: the doubles replace the working tools by name.
        foreach (var tool in tools) registry.Register(tool);

        var task = new DomainTask
        {
            Title = "Map the UI",
            Description = "map it",
            AssignedAnt = "ui_cartographer",
            TaskType = "ui_mapping",
        };
        var mission = new Mission { Goal = "map the ui", Tasks = { task } };
        _memory.SaveMission(mission);
        return (new UiCartographerAnt(registry), task, mission);
    }

    /// <summary>
    /// The listing tool fails: the cartographer REFUSES rather than mapping nothing.
    ///
    /// `failed_permanent` and not retryable, for the same reason the empty-workspace case chose it:
    /// a re-run against the same broken tool fails identically, and a retry would spend the
    /// scheduler's budget to learn that again.
    /// </summary>
    [Fact]
    public void WhenTheListingToolFails_TheCartographerRefusesInsteadOfMappingNothing()
    {
        var (ant, task, mission) = Harness(
            new FailingTool("list_directory", FailureClass.DependencyFailure));

        var outcome = ant.Execute(task, mission);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Failure);
        Assert.Equal(FailureClass.DependencyFailure, outcome.Failure!.Class);
        Assert.False(outcome.Failure.Retryable);
    }

    /// <summary>
    /// And it emits NO `ui_map`. This is the assertion the cell is actually worth having.
    ///
    /// A refusal that still produced a map would be admitted by `UiChangeGate` — which asks the
    /// artifact store whether a usable map EXISTS, not whether the task that produced it succeeded —
    /// and the coder would then edit a UI it had been told has no routes. The status and the artifact
    /// are two different claims, and only one of them is what the gate reads.
    /// </summary>
    [Fact]
    public void AFailedCartographer_EmitsNoUiMapForTheGateToFind()
    {
        var (ant, task, mission) = Harness(
            new FailingTool("list_directory", FailureClass.DependencyFailure));

        var outcome = ant.Execute(task, mission);

        Assert.DoesNotContain(outcome.Artifacts, a => a.Kind == "ui_map");
    }

    /// <summary>
    /// The control, and the half that keeps the two above from passing for the wrong reason: with
    /// the SAME workspace and working tools the ant does produce a map. Without this, a cartographer
    /// that had quietly stopped mapping anything at all would satisfy both assertions above.
    /// </summary>
    [Fact]
    public void TheSameWorkspaceWithWorkingTools_StillProducesAMap()
    {
        var (ant, task, mission) = Harness();

        var outcome = ant.Execute(task, mission);

        Assert.True(outcome.Success,
            "the fault tests above are only meaningful if this workspace is mappable when the tools "
          + "work. It is not, so they prove nothing about the failure path.");
        Assert.Contains(outcome.Artifacts, a => a.Kind == "ui_map");
    }

    /// <summary>Fails every read except the paths it is told to let through.</summary>
    private sealed class SelectiveReadTool(ITool real, params string[] readable) : ITool
    {
        public string Name => "read_text_file";
        public string Description => "Test double: reads only the named paths, fails the rest.";

        public ToolResult Run(IReadOnlyDictionary<string, object?> args)
        {
            var path = (args.GetValueOrDefault("path")?.ToString() ?? "").Replace('\\', '/');
            return readable.Any(r => path.EndsWith(r, StringComparison.OrdinalIgnoreCase))
                ? real.Run(args)
                : new ToolResult(Name, success: false, output: "", error: $"{path} is unavailable",
                    failure: FailureClass.DependencyFailure);
        }
    }

    /// <summary>
    /// A read failure DEGRADES the map; a listing failure REFUSES it. The asymmetry is deliberate,
    /// and it is written down here because nothing else states it in one place.
    ///
    /// The listing decides WHAT EXISTS; a read decides what one file contains. Losing the listing
    /// means the ant does not know what it is looking at, and a map built on that is a guess. Losing
    /// one read means the map is short by a file, and a partial map is still usable to the coder —
    /// the other side of which `UiCartographerAntTests.UnreadableKnownPaths_WarnButDoNotFailTheMap`
    /// already pins.
    /// </summary>
    [Fact]
    public void AFailedReadDegradesTheMap_ItDoesNotRefuseIt()
    {
        Directory.CreateDirectory(_dir);
        var workspace = Path.Combine(_dir, "ws-selective");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "index.html"),
            "<div id=\"page-overview\"></div><script>function loadColony(){ api('/colony/registry'); }</script>");
        File.WriteAllText(Path.Combine(workspace, "app.js"), "function unreadableOne(){}");

        _memory = new SqliteMemory(Path.Combine(_dir, "selective.db"));
        var registry = new ToolRegistry(_memory);
        var guard = new WorkspacePathGuard(workspace);
        registry.Register(new DirectoryListTool(guard));
        registry.Register(new SelectiveReadTool(new ReadTextFileTool(guard), "index.html"));

        var task = new DomainTask
        {
            Title = "Map the UI", Description = "map it",
            AssignedAnt = "ui_cartographer", TaskType = "ui_mapping",
        };
        var mission = new Mission { Goal = "map the ui", Tasks = { task } };
        _memory.SaveMission(mission);

        var outcome = new UiCartographerAnt(registry).Execute(task, mission);

        Assert.True(outcome.Success,
            "one readable file was enough to map and the ant refused anyway: " + outcome.Summary);
        Assert.Equal("succeeded_with_warnings", outcome.StatusCode);
        Assert.Contains(outcome.Warnings, w => w.StartsWith("unreadable:", StringComparison.Ordinal));
        Assert.Contains(outcome.Artifacts, a => a.Kind == "ui_map");
    }

    /// <summary>
    /// And when EVERY read fails the ant refuses after all — the second unexercised fault branch,
    /// pinned so the asymmetry above is not mistaken for "reads never matter".
    ///
    /// `examined.Count == 0` is a different refusal from the listing one and reaches the same place:
    /// no map exists, so the UI gate has nothing to admit. Worth its own assertion because the two
    /// branches are eighty lines apart and only one of them was ever named in a test.
    /// </summary>
    [Fact]
    public void WhenEveryReadFails_TheCartographerRefusesToo()
    {
        var (ant, task, mission) = Harness(
            new FailingTool("read_text_file", FailureClass.DependencyFailure));

        var outcome = ant.Execute(task, mission);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Failure);
        Assert.Equal(FailureClass.DependencyFailure, outcome.Failure!.Class);
        Assert.DoesNotContain(outcome.Artifacts, a => a.Kind == "ui_map");
    }
}
