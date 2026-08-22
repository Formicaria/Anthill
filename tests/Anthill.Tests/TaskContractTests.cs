using Anthill.Core.Contracts;
// v0.3.8.87 — the capability claim in this file is now evaluated by the gate that decides a real
// dispatch (ToolAuthorization) against the grant a real colony resolves (CapabilityGrant), so both
// live here rather than being invented per-assertion.
using Anthill.Core.Tools;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;
// v3.8.10 — two types are named ToolResult: the CONTRACT one in Anthill.Core.Contracts and the
// DISPATCH one in Anthill.SDK.Tools. This file tests contracts and uses only the former — note
// ToolResult.Failed(FailureClass, ...), which exists solely on the contract type. The alias names
// which is meant, the same way ToolFailureClassTests.cs does for the other direction.
using ToolResult = Anthill.Core.Contracts.ToolResult;

namespace Anthill.Tests;

/// <summary>
/// v2.9.0 Phase 2 success criteria under test: planner output is schema validated; invalid tasks
/// cannot enter the execution queue; permissions are evaluable before execution against
/// capabilities (not ant names); retry decisions use typed failure classes; every state-changing
/// tool declares recovery behavior.
/// </summary>
public class TaskContractTests
{
    private static DomainTask T(string ant = "researcher", string title = "Investigate", string desc = "Look into it")
        => new() { Title = title, Description = desc, AssignedAnt = ant, TaskType = "research" };

    // ---- Schema validation + admission gate ----------------------------------------------------

    [Fact]
    public void ValidPlannerTask_ProjectsToValidContract_AndIsAdmitted()
    {
        var contract = TaskContract.FromTask(T());
        Assert.Empty(contract.Validate());
        Assert.Single(ContractGate.Admit(new List<DomainTask> { T() }));
    }

    [Fact]
    public void UnknownAnt_FailsTowardCaution_AndIsRejected()
    {
        var contract = TaskContract.FromTask(T(ant: "mystery"));
        Assert.Equal("destructive", contract.SideEffectClass); // unknown = worst case
        Assert.Equal("critical", contract.RiskClass);
        Assert.NotEmpty(contract.Validate()); // no capabilities declared → cannot be permission-checked
        var rejections = new List<string>();
        var admitted = ContractGate.Admit(new List<DomainTask> { T(ant: "mystery") }, rejections.Add);
        Assert.Empty(admitted);            // cannot enter the execution queue
        Assert.Single(rejections);         // and the rejection is loud
    }

    [Fact]
    public void MissingTitleOrObjective_IsRejected()
    {
        Assert.Contains("title is required", TaskContract.FromTask(T(title: "")).Validate());
        Assert.Contains("objective is required", TaskContract.FromTask(T(desc: "")).Validate());
    }

    [Fact]
    public void SelfDependency_IsRejected()
    {
        var task = T();
        task.DependsOn.Add(task.Id);
        Assert.Contains(TaskContract.FromTask(task).Validate(), e => e.Contains("depend on itself"));
    }

    // ---- Capability model ----------------------------------------------------------------------

    /// <summary>
    /// v0.3.8.87 — THE SAME CLAIM, RUN THROUGH THE GATE THAT ACTUALLY DECIDES IT.
    ///
    /// This test's title has always been the right one. Its body was not: it called
    /// <c>ToolCatalog.CanRun</c>, a permission check with no production caller in its entire life,
    /// and handed it grant sets the test itself invented. Both sides came from the test, so it could
    /// only ever confirm that `All(...Contains)` works.
    ///
    /// What decides a real dispatch is <c>ToolAuthorization.Evaluate</c>, reading the ROLE CONTRACT's
    /// capabilities against the grant <c>CapabilityGrant.Resolve</c> derived from what the
    /// composition root actually built. So both ends are now real producers: the contract on one
    /// side, the resolver on the other, and nothing in between authored here.
    /// </summary>
    [Fact]
    public void Permissions_EvaluableBeforeExecution_AgainstCapabilitiesNotAntNames()
    {
        // A colony with the file tools registered and no reasoning provider. `file` needs exactly
        // repo.read and repo.search; both are granted, so its dispatch is permitted.
        var withFiles = CapabilityGrant.Resolve(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "read_text_file", "list_directory", "search_workspace" },
            modelAvailable: false, webSearchEnabled: false);

        Assert.True(Ctx("file", withFiles, "read_text_file").Allowed);

        // The SAME colony, one role over. `researcher` requires model.invoke and no provider was
        // composed in, so the gate refuses — and the refusal names the capability, which is the half
        // an operator needs to know which switch to reach for.
        var refused = Ctx("researcher", withFiles, "read_text_file");
        Assert.False(refused.Allowed);
        Assert.Contains(Capability.ModelInvoke, refused.Reason);

        // A colony with nothing registered cannot even read. `file`'s requirement is unmet and the
        // refusal is the capability gate's, not "tool not found" three layers down.
        var bare = CapabilityGrant.Resolve(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), modelAvailable: false, webSearchEnabled: false);
        var bareRefusal = Ctx("file", bare, "read_text_file");
        Assert.False(bareRefusal.Allowed);
        Assert.Contains(Capability.RepoRead, bareRefusal.Reason);
    }

    /// <summary>Evaluate one role's dispatch under a resolved grant, with the tool allowlisted so
    /// the capability clause is the only thing that can refuse.</summary>
    private static ToolAuthorization.Decision Ctx(string role, IReadOnlySet<string> granted, string tool) =>
        ToolAuthorization.Evaluate(
            new ToolExecutionContext("m", "t", role, $"{role}-worker", granted,
                AllowedTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { tool },
                ForbiddenTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            tool);

    /// <summary>
    /// Every executable caste projects a schema-valid declaration, and none of them claims a side
    /// effect its contract denies.
    ///
    /// v0.3.8.87 rewrote this. It used to read <c>ToolCatalog</c>, assert `NotEmpty` on a capability
    /// list that catalog no longer owns, and range over the six roles it happened to list — the
    /// other six reached a "reversible / high / manual" fallback that was a fair default for a role
    /// nobody had described and wrong for six read-only ones.
    ///
    /// Now it ranges over all twelve contracts and checks the projection against the contract's own
    /// flags, which is the property that would have caught the old divergence: the catalog called the
    /// coder "reversible" while its contract declared <c>AllowsSideEffects: false</c>.
    /// </summary>
    [Fact]
    public void EveryExecutableCaste_ProjectsADeclarationItsContractAgreesWith()
    {
        // Read from the CONTRACT catalog, not AntRegistry.ExecutableRoleIds: the latter is computed
        // from runtime canary gates, so which roles it names depends on static configuration this
        // collection shares. A guard whose subject set moves with a flag passes for a reason nobody
        // chose.
        var wrong = new List<string>();

        foreach (var (role, contract) in Anthill.Core.Agents.AntExecutionCatalog.Contracts)
        {
            var projected = TaskContract.FromTask(
                new DomainTask { Title = $"Probe {role}", Description = "Projection probe.", AssignedAnt = role });

            if (!contract.AllowsSideEffects && projected.SideEffectClass != "none")
                wrong.Add($"{role}: contract declares no side effects and the projection says "
                        + $"'{projected.SideEffectClass}'");

            if (contract.AllowsSideEffects && projected.SideEffectClass == "none")
                wrong.Add($"{role}: contract allows side effects and the projection says 'none'");

            // Schema-valid, which is what admission actually turns on.
            var errors = projected.Validate();
            if (errors.Count > 0)
                wrong.Add($"{role}: projects a contract the schema rejects — {string.Join("; ", errors)}");
        }

        Assert.True(wrong.Count == 0,
            "the admission projection disagrees with the role contracts:\n  " + string.Join("\n  ", wrong));
    }

    // ---- Failure taxonomy drives retries -------------------------------------------------------

    [Theory]
    [InlineData(FailureClass.TransientProviderFailure, true)]
    [InlineData(FailureClass.RateLimit, true)]
    [InlineData(FailureClass.Timeout, true)]
    [InlineData(FailureClass.Conflict, true)]
    [InlineData(FailureClass.ValidationFailure, false)]
    [InlineData(FailureClass.AuthorizationFailure, false)]
    [InlineData(FailureClass.UnsafeState, false)]
    [InlineData(FailureClass.CompensationFailure, false)]
    [InlineData(FailureClass.InternalDefect, false)]
    public void RetryDecisions_ComeFromTypedClasses(FailureClass cls, bool retryable)
    {
        Assert.Equal(retryable, FailureClassify.IsRetryable(cls));
        var result = ToolResult.Failed(cls, "boom");
        Assert.Equal(retryable ? "failed_retryable" : "failed_permanent", result.Status);
    }
}
