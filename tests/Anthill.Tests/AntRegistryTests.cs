using Anthill.Core.Agents;
using Anthill.Core.Common;
using Anthill.Core.Domain;
using Anthill.Core.Planning;
using Xunit;

namespace Anthill.Tests;

// v0.3.8.41 — joins the serialised collection because two tests here now SET the rollout gates.
// They read them before and got away with it while the default was `core` and the statics never
// moved; with `full` the state is real, and a test that mutates process-global gates in parallel
// with another that reads them is a flake waiting for a slow machine.
[Collection("specialist-gates")]
public class AntRegistryTests
{
    [Fact]
    public void Registry_HasExpectedVisibleColonyShape()
    {
        Assert.Equal(25, AntRegistry.Roles.Count); // Queen + Director + 15 main hubs + 8 homelab ants (v1.9.0)
        // 32 at v0.3.8.98: `researcher.runtime_researcher` joined, declaring `inspect_runtime_state`
        // — the capability an audit needs to answer "what is enabled right now" rather than reading
        // the source and calling that the state of the colony.
        // 33 at v0.3.8.102: `tester.action_proposer` — the operation lane's worker, ON the tester
        // rather than a thirteenth role. The role count above did NOT move, which is this pin
        // doing its job in the other direction: a lane landed without a roster change.
        Assert.Equal(33, AntRegistry.Roles.SelectMany(r => r.Workers).Count());
        Assert.Empty(AntRegistry.ValidateRegistry());
        Assert.Contains(AntRegistry.Roles, r => r.RoleId == "queen");
        Assert.Contains(AntRegistry.Roles, r => r.RoleId == "director");
        Assert.Contains(AntRegistry.Roles, r => r.RoleId == "ui_cartographer");
        // v1.9.0 homelab ants: present, visible-only, never executable, never patch-capable.
        var homelabRoles = new[] { "inventory", "network_scout", "health", "proxmox", "storage", "backup", "security_scout", "change_archivist" };
        foreach (var roleId in homelabRoles)
        {
            var role = Assert.Single(AntRegistry.Roles, r => r.RoleId == roleId);
            Assert.False(role.Executable, $"Homelab ant '{roleId}' must not be executable in v1.9.0.");
            Assert.False(role.Permissions.ProposePatches, $"Homelab ant '{roleId}' must not propose patches.");
            Assert.Equal("Homelab", role.Colony);
        }
        Assert.DoesNotContain(AntRegistry.ExecutableRoleIds, id => homelabRoles.Contains(id));
    }

    [Fact]
    public void Registry_HasNoDuplicateIdsAndNoWorkerExceedsParent()
    {
        Assert.Equal(AntRegistry.Roles.Count, AntRegistry.Roles.Select(r => r.RoleId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var workers = AntRegistry.Roles.SelectMany(r => r.Workers).ToList();
        Assert.Equal(workers.Count, workers.Select(w => w.WorkerId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Empty(AntRegistry.ValidateRegistry());
    }

    [Fact]
    public void Registry_NoAntCanApplyPatches()
    {
        Assert.DoesNotContain(AntRegistry.Roles, r => r.Permissions.ApplyPatches);
        Assert.DoesNotContain(AntRegistry.Roles.SelectMany(r => r.Workers), w => w.Permissions.ApplyPatches);
    }

    [Fact]
    public void Validation_RejectsUnknownRoleWorkerAndWorkerParentMismatch()
    {
        var constraints = MissionConstraints.None;
        Assert.False(AntRegistry.ValidateTask(new Task { AssignedAnt = "nope", TaskType = "research" }, constraints).Allowed);
        Assert.False(AntRegistry.ValidateTask(new Task { AssignedAnt = "file", AssignedWorker = "file.nope", TaskType = "file_inspection" }, constraints).Allowed);
        Assert.False(AntRegistry.ValidateTask(new Task { AssignedAnt = "file", AssignedWorker = "coder.backend_coder", TaskType = "file_inspection" }, constraints).Allowed);
    }

    [Fact]
    public void Validation_BlocksCoderWhenMissionBlocksPatches()
    {
        var constraints = MissionConstraints.Parse("verification only, do not modify files");
        var task = new Task { AssignedAnt = "coder", AssignedWorker = "coder.backend_coder", TaskType = "patch_proposal" };
        var result = AntRegistry.ValidateTask(task, constraints);
        Assert.False(result.Allowed);
        Assert.Contains("block", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Planner_VerificationOnlyDoesNotUseCoderAndGetsWorkers()
    {
        const string Goal = "verify the parser only; no patches and do not modify files";
        var planner = new Planner(useOllama: false, router: null);
        var tasks = planner.CreateTasks(Goal, MissionConstraints.Parse(Goal));
        Assert.DoesNotContain(tasks, t => t.AssignedAnt == "coder");
        Assert.All(tasks, t => Assert.False(string.IsNullOrWhiteSpace(t.AssignedWorker)));
    }

    [Fact]
    public void Planner_UiGoalRoutesCoderToUiWorker()
    {
        const string UiGoal = "update the Colony Live UI canvas and CSS";
        var planner = new Planner(useOllama: false, router: null);
        var tasks = planner.CreateTasks(UiGoal, MissionConstraints.Parse(UiGoal));
        Assert.Contains(tasks, t => t.AssignedAnt == "coder" && t.AssignedWorker == "coder.ui_coder");
    }

    [Fact]
    public void Runtime_ResolvesWorkerAndKeepsParentExecutor()
    {
        var task = new Task { AssignedAnt = "coder", AssignedWorker = "coder.ui_coder", TaskType = "patch_proposal", Description = "Update UI." };
        var runtime = AntRuntime.Resolve(task, MissionConstraints.None);
        Assert.Equal("coder", runtime.ExecutorRoleId);
        Assert.Equal("coder.ui_coder", runtime.RuntimeNodeId);
        Assert.Equal("coder.ui_coder", task.AssignedWorker);
        Assert.DoesNotContain(runtime.AuditWarnings, w => w.Contains("apply_patch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Runtime_AddsWorkerContextToTaskSnapshot()
    {
        var task = new Task { AssignedAnt = "file", AssignedWorker = "file.file_reader", TaskType = "file_inspection", Description = "Read the registry file." };
        var runtime = AntRuntime.Resolve(task, MissionConstraints.None);
        var snapshot = AntRuntime.PrepareWorkerTaskSnapshot(task, runtime);
        Assert.Contains("Worker Runtime Context", snapshot.Description);
        Assert.Contains("file.file_reader", snapshot.Description);
        Assert.Contains("apply_patch is forbidden", snapshot.Description);
        Assert.Equal("Read the registry file.", task.Description);
    }

    /// <summary>
    /// A role whose gate is shut is visible-only, and the runtime refuses to resolve a worker for it.
    ///
    /// v0.3.8.41 — the gate is now forced shut by the test instead of being assumed. It used to be
    /// assumed correctly for one reason only: the default roster was `core`, so the tester's rollout
    /// flag was off in every process, and this read like a test about visible-only roles while
    /// actually depending on the shipped default. With `full` as the default the tester's gate is
    /// OPEN, `AntRuntime.Resolve` gets past the visible-only check, and the refusal it produces is
    /// the scheduling-mode one — a different rule, correctly applied, failing a test that meant to
    /// ask about a different thing.
    ///
    /// Both refusals are worth having, so both are asserted, each against the state that provokes it.
    /// </summary>
    [Fact]
    public void Runtime_RejectsVisibleOnlyRoles() => RosterGates.With(() =>
    {
        var task = new Task { AssignedAnt = "tester", AssignedWorker = "tester.dotnet_tester", TaskType = "verification", Description = "Run checks." };
        var error = Assert.Throws<InvalidOperationException>(() => AntRuntime.Resolve(task, MissionConstraints.None));
        Assert.Contains("visible-only", error.Message);
    }, specialists: false, tester: false);

    /// <summary>
    /// v0.3.8.51 (field report): with the gate open, a PLANNED tester resolves — the operator's
    /// baseline-checks step is a plan asking for more safety, and the old refusal threw it away.
    /// The scheduling-mode wall now guards only medic and archivist, whose handlers can only
    /// refuse a planned invocation.
    /// </summary>
    [Fact]
    public void Runtime_AdmitsAPlannedTester_TheFloorIsNotACeiling() => RosterGates.With(() =>
    {
        var task = new Task { AssignedAnt = "tester", AssignedWorker = "tester.dotnet_tester", TaskType = "verification", Description = "Run checks." };
        var selection = AntRuntime.Resolve(task, MissionConstraints.None);
        Assert.Equal("tester", selection.ExecutorRoleId);
    }, specialists: true, tier: ActivationTier.Full, tester: true);
}
