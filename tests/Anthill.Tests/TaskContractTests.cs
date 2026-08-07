using Anthill.Core.Contracts;
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

    [Fact]
    public void Permissions_EvaluableBeforeExecution_AgainstCapabilitiesNotAntNames()
    {
        Assert.True(ToolCatalog.CanRun("file", new[] { Capability.RepoRead, Capability.RepoSearch }));
        Assert.False(ToolCatalog.CanRun("file", new[] { Capability.RepoRead }));           // partial grant
        Assert.False(ToolCatalog.CanRun("coder", new[] { Capability.ModelInvoke }));       // no patch capability
        Assert.False(ToolCatalog.CanRun("unknown-tool", new[] { Capability.ModelInvoke })); // unknown → refuse
    }

    [Fact]
    public void EveryExecutableCaste_HasTypedDeclaration_AndChangersDeclareRecovery()
    {
        foreach (var ant in new[] { "researcher", "web", "file", "coder", "builder", "verifier" })
        {
            var d = ToolCatalog.Describe(ant);
            Assert.NotNull(d);
            Assert.NotEmpty(d!.RequiredCapabilities);
            if (d.SideEffectClass != "none")
                Assert.NotEqual("none", d.Compensation); // state-changing tools declare recovery
        }
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
