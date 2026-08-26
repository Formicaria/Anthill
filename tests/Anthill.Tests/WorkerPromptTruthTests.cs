using Anthill.Core.Agents;
using Anthill.Core.Common;
using Anthill.Core.Domain;
using Anthill.Core.Tools;
using Xunit;
// `Task` here is Anthill.Core.Domain.Task via the suite's GlobalUsings alias — redeclaring the
// alias per-file is CS1537.

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.93 — the worker prompt names REAL tools or none. Until this release every dispatched
/// task's snapshot presented the registry's duty descriptors — `read_workspace_docs`,
/// `read_task_outputs`, names worded as tools and implemented by nothing — under the heading
/// "Allowed worker tools". A worker that asked for one was denied at dispatch and read as a weak
/// model: the phantom-tool defect (ADR-006), reproduced inside the colony's own prompts after the
/// contracts were cleaned of it. The snapshot now carries the role's actual dispatch allowlist,
/// from the same table the denial would come from, so prompt and gate cannot disagree.
/// </summary>
public class WorkerPromptTruthTests
{
    private static Task Snapshot(string ant, string worker, string type)
    {
        var task = new Task { AssignedAnt = ant, AssignedWorker = worker, TaskType = type, Description = "d" };
        return AntRuntime.PrepareWorkerTaskSnapshot(task, AntRuntime.Resolve(task, MissionConstraints.None));
    }

    /// <summary>Every tool name the snapshot offers exists in this build's inventory — for every
    /// executable role and worker the registry has, not a hand-picked sample.</summary>
    [Fact]
    public void EveryToolTheSnapshotNames_Exists()
    {
        foreach (var role in AntRegistry.Roles.Where(r => r.Executable && r.Enabled))
            foreach (var tool in ToolAuthorization.DispatchAllowlistFor(role.RoleId))
                Assert.True(ToolInventory.Exists(tool),
                    $"the {role.RoleId} snapshot would offer '{tool}', which no build implements — "
                  + "a worker that asks for it is denied at dispatch and reads as a weak model.");
    }

    /// <summary>The phantom names are gone from the prompt, and the real ones are in it.</summary>
    [Fact]
    public void TheResearcherSnapshot_NamesItsRealTools_NotItsDuties()
    {
        var snapshot = Snapshot("researcher", "researcher.repo_researcher", "research");

        Assert.DoesNotContain("read_workspace_docs", snapshot.Description);
        Assert.Contains("Dispatchable tools:", snapshot.Description);
        Assert.Contains("list_directory", snapshot.Description);
        Assert.Contains("system_info", snapshot.Description);
    }

    /// <summary>A role with an empty dispatch allowlist is told so in words — an honest "none"
    /// beats a plausible fiction, which is this repository's whole doctrine in one line.</summary>
    [Fact]
    public void ARoleWithNoTools_IsToldNone_NotHandedFictions()
    {
        var snapshot = Snapshot("builder", "builder.response_builder", "build_answer");

        Assert.DoesNotContain("read_task_outputs", snapshot.Description);
        Assert.Contains("none — this worker reasons over the context it is given", snapshot.Description);
    }
}
