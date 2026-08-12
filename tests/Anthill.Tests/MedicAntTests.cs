using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// Stage D-5 validation gate, rewritten for the structural repair (§1): the medic diagnoses THE
/// FAILURE THAT INVOKED IT (parent lineage, never the newest), consumes structure over prose,
/// keeps unknown unknown, selects specialists from task/artifact classification (words like "UI"
/// in error text route nothing), and detects semantic duplicates across different task UUIDs.
/// </summary>
[Collection("specialist-gates")]
public class MedicAntTests
{
    private static (Mission Mission, DomainTask Failed) MissionWithFailure(string failureReason,
        string failedTaskType = "test_execution", string failedRole = "tester", params DomainTask[] extra)
    {
        var failedTask = new DomainTask
        {
            Title = "Broken step", AssignedAnt = failedRole, TaskType = failedTaskType,
            Status = Anthill.Core.Domain.TaskStatus.Failed,
            FailureReason = failureReason, FailedAt = DateTime.UtcNow,
        };
        var m = new Mission { Goal = "fix things", Tasks = { failedTask } };
        foreach (var t in extra) m.Tasks.Add(t);
        return (m, failedTask);
    }

    /// <summary>The medic task carries its source failure as parent lineage — exactly what the
    /// handoff admission path records. §1A: the binding, not a convenience.</summary>
    private static AntExecutionResult Diagnose(Mission m, DomainTask boundTo)
    {
        var t = new DomainTask
        {
            Title = "Diagnose", Description = "diagnose the failure", AssignedAnt = "medic",
            TaskType = "failure_diagnosis", ParentTaskIds = new List<string> { boundTo.Id },
        };
        m.Tasks.Add(t);
        return new MedicAnt().Execute(t, m);
    }

    private static string Recorded(AntExecutionResult r) => r.Narrative ?? r.Summary;

    private static void AssertRoutesTo(AntExecutionResult r, string role) =>
        Assert.Contains(r.Handoffs, h => h.DestinationRole == role);

    [Fact]
    public void Timeout_ClassifiedRetryable_RoutedToTester()
    {
        var (m, failed) = MissionWithFailure("check timed out after 600s");
        var o = Diagnose(m, failed);
        Assert.Contains("timeout", Recorded(o));
        Assert.Contains("retryable: True", Recorded(o));
        AssertRoutesTo(o, "tester");
    }

    [Fact]
    public void ValidationFailure_ClassifiedPermanent_RoutedToCoder()
    {
        var (m, failed) = MissionWithFailure("input failed validation: missing objective");
        var o = Diagnose(m, failed);
        Assert.Contains("validation_failure", Recorded(o));
        Assert.Contains("retryable: False", Recorded(o));
        AssertRoutesTo(o, "coder");
    }

    /// <summary>
    /// §1D / scenario I — THE GUARD FOR THE ORIGINAL DEFECT. Error prose containing "UI",
    /// ".html" and "app.js" describes a deterministic check failure over ordinary code work.
    /// The words must route NOTHING; classification and task typing route the coder.
    /// </summary>
    [Fact]
    public void UiWordsInErrorProse_DoNotRouteToUiCartographer()
    {
        var (m, failed) = MissionWithFailure(
            "build failed exit_code=1 — configuration/UI mismatch noticed in app.js and index.html prose");
        var o = Diagnose(m, failed);
        Assert.DoesNotContain(o.Handoffs, h => h.DestinationRole == "ui_cartographer");
        AssertRoutesTo(o, "coder");
    }

    /// <summary>Scenario H's unit half — a genuinely ui-TYPED failed task selects the
    /// ui_cartographer from classification, no prose consulted.</summary>
    [Fact]
    public void UiTypedFailedTask_LegitimatelyRoutesToUiCartographer()
    {
        var (m, failed) = MissionWithFailure("frontend check failed exit_code=1",
            failedTaskType: "ui_mapping", failedRole: "ui_cartographer");
        // A classified deterministic failure over ui-TYPED work — the type decides, not the words.
        var o = Diagnose(m, failed);
        AssertRoutesTo(o, "ui_cartographer");
    }

    /// <summary>§1A — the medic diagnoses its PARENT, not the globally newest failure.</summary>
    [Fact]
    public void TwoFailures_MedicDiagnosesItsParent_NotTheNewest()
    {
        var (m, older) = MissionWithFailure("input failed validation: missing objective");
        var newer = new DomainTask
        {
            Title = "Unrelated newer failure", AssignedAnt = "web", TaskType = "external_research",
            Status = Anthill.Core.Domain.TaskStatus.Failed,
            FailureReason = "rate limit 429 from provider", FailedAt = DateTime.UtcNow.AddMinutes(5),
        };
        m.Tasks.Add(newer);

        var o = Diagnose(m, older);   // bound to the OLDER failure

        Assert.Contains(older.Id, Recorded(o));
        Assert.DoesNotContain(newer.Id, Recorded(o));
        Assert.Contains("validation_failure", Recorded(o));   // the parent's class, not the newer 429
    }

    /// <summary>§8's unit half — two medics bound to two parallel failures each diagnose their own.</summary>
    [Fact]
    public void ParallelFailures_EachMedicDiagnosesItsOwnParent()
    {
        var (m, failedA) = MissionWithFailure("check timed out after 600s");
        var failedB = new DomainTask
        {
            Title = "B fails differently", AssignedAnt = "tester", TaskType = "test_execution",
            Status = Anthill.Core.Domain.TaskStatus.Failed,
            FailureReason = "input failed validation: schema mismatch", FailedAt = DateTime.UtcNow.AddSeconds(1),
        };
        m.Tasks.Add(failedB);

        var oA = Diagnose(m, failedA);
        var oB = Diagnose(m, failedB);

        Assert.Contains(failedA.Id, Recorded(oA));
        Assert.Contains("timeout", Recorded(oA));
        Assert.Contains(failedB.Id, Recorded(oB));
        Assert.Contains("validation_failure", Recorded(oB));
    }

    /// <summary>§1A fail-closed: no parent lineage → refuse, even though a failure exists that the
    /// old code would happily have picked up.</summary>
    [Fact]
    public void NoParentLineage_RefusesToGuess()
    {
        var (m, _) = MissionWithFailure("something failed");
        var t = new DomainTask { Title = "Diagnose", AssignedAnt = "medic", TaskType = "failure_diagnosis" };
        m.Tasks.Add(t);
        var o = new MedicAnt().Execute(t, m);
        Assert.Equal("blocked", o.StatusCode);
        Assert.Contains("cannot be identified", o.Summary);
    }

    /// <summary>§1C — unknown stays unknown: no InternalDefect verdict, no repair route, escalation.</summary>
    [Fact]
    public void UnclassifiableFailure_EscalatesForEvidence_NeverInternalDefect()
    {
        var (m, failed) = MissionWithFailure("gremlins ate it");
        var o = Diagnose(m, failed);
        Assert.DoesNotContain("internal_defect", Recorded(o));
        Assert.DoesNotContain(o.Handoffs, h => h.DestinationRole == "coder");
        AssertRoutesTo(o, "builder");
        Assert.Contains("UNCLASSIFIED", Recorded(o));
    }

    [Fact]
    public void DiagnosisBudgetExhausted_EscalatesToBuilder_NoRepairLoop()
    {
        var prior1 = new DomainTask { Title = "diag1", AssignedAnt = "medic", Result = "diagnosed once" };
        var prior2 = new DomainTask { Title = "diag2", AssignedAnt = "medic", Result = "diagnosed twice" };
        var (m, failed) = MissionWithFailure("build failed exit_code=1", extra: new[] { prior1, prior2 });
        var o = Diagnose(m, failed);
        Assert.Contains("budget exhausted", o.Summary);
        AssertRoutesTo(o, "builder");
        Assert.DoesNotContain(o.Handoffs, h => h.DestinationRole == "coder");
    }

    /// <summary>
    /// §1E / scenario D — SEMANTIC duplicate detection across task UUIDs. The same defect is
    /// reproduced under a brand-new failed task (new UUID); the medic recognises the signature
    /// from its own prior diagnosis and escalates instead of opening another repair loop.
    /// </summary>
    [Fact]
    public void SameSemanticFailure_UnderANewTaskUuid_Escalates()
    {
        var (m, failedA) = MissionWithFailure("build failed exit_code=1 at Foo.cs(42,7)");
        var first = Diagnose(m, failedA);
        Assert.Equal("succeeded", first.StatusCode);
        // The first diagnosis is recorded exactly as the runtime records it.
        m.Tasks.Last().Result = first.Narrative;

        // The same defect comes back: new task, new UUID, same normalized error shape.
        var failedB = new DomainTask
        {
            Title = "Broken step again", AssignedAnt = "tester", TaskType = "test_execution",
            Status = Anthill.Core.Domain.TaskStatus.Failed,
            FailureReason = "build failed exit_code=1 at Foo.cs(42,7)", FailedAt = DateTime.UtcNow.AddMinutes(1),
        };
        m.Tasks.Add(failedB);

        var second = Diagnose(m, failedB);

        Assert.Contains("escalat", second.Summary, StringComparison.OrdinalIgnoreCase);
        AssertRoutesTo(second, "builder");
        Assert.DoesNotContain(second.Handoffs, h => h.DestinationRole == "coder");
    }

    /// <summary>And the counter-case: a MATERIALLY DIFFERENT failure is not a duplicate.</summary>
    [Fact]
    public void ADifferentFailure_IsNotASemanticDuplicate()
    {
        var (m, failedA) = MissionWithFailure("build failed exit_code=1 at Foo.cs(42,7)");
        var first = Diagnose(m, failedA);
        m.Tasks.Last().Result = first.Narrative;

        var failedB = new DomainTask
        {
            Title = "A genuinely different defect", AssignedAnt = "tester", TaskType = "test_execution",
            Status = Anthill.Core.Domain.TaskStatus.Failed,
            FailureReason = "input failed validation: schema mismatch on objectives payload",
            FailedAt = DateTime.UtcNow.AddMinutes(1),
        };
        m.Tasks.Add(failedB);

        var second = Diagnose(m, failedB);
        Assert.Equal("succeeded", second.StatusCode);   // a real diagnosis, not an escalation
    }

    [Fact]
    public void Medic_CannotApplyOrShell()
    {
        Assert.False(Anthill.Core.Tools.ToolAuthorization.Evaluate("medic", "apply_patch").Allowed);
        Assert.False(Anthill.Core.Tools.ToolAuthorization.Evaluate("medic", "shell_command").Allowed);
        Assert.False(Anthill.Core.Tools.ToolAuthorization.Evaluate("medic", "write_text_file").Allowed);
    }

    [Fact]
    public void GatesControlExecutability()
    {
        Assert.DoesNotContain("medic", AntRegistry.ExecutableRoleIds);
        try
        {
            AnthillRuntime.EnableSpecialistAntExecution = true;
            AnthillRuntime.EnableMedicAnt = true;
            Assert.Contains("medic", AntRegistry.ExecutableRoleIds);
        }
        finally
        {
            AnthillRuntime.EnableSpecialistAntExecution = false;
            AnthillRuntime.EnableMedicAnt = false;
        }
    }
}
