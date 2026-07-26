using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// Stage D-5 validation gate (spec §15 MEDICANT): classifies retryable vs permanent failures
/// deterministically, produces exactly one bounded repair route, refuses to run without a real
/// failure, stops duplicate repair loops, escalates when the budget is exhausted, and can never
/// apply changes.
/// </summary>
[Collection("specialist-gates")]
public class MedicAntTests
{
    private static Mission MissionWithFailure(string failureReason, params DomainTask[] extra)
    {
        var failedTask = new DomainTask
        {
            Title = "Broken step", AssignedAnt = "tester", Status = Anthill.Core.Domain.TaskStatus.Failed,
            FailureReason = failureReason, FailedAt = DateTime.UtcNow,
        };
        var m = new Mission { Goal = "fix things", Tasks = { failedTask } };
        foreach (var t in extra) m.Tasks.Add(t);
        return m;
    }

    private static string Diagnose(Mission m)
    {
        var t = new DomainTask { Title = "Diagnose", Description = "diagnose the failure", AssignedAnt = "medic", TaskType = "failure_diagnosis" };
        m.Tasks.Add(t);
        return new MedicAnt().Run(t, m);
    }

    [Fact]
    public void Timeout_ClassifiedRetryable_RoutedToTester()
    {
        var o = Diagnose(MissionWithFailure("check timed out after 600s"));
        Assert.Contains("Timeout", o);
        Assert.Contains("retryable: True", o);
        Assert.Contains("\"DestinationRole\":\"tester\"", o.Replace(" ", ""));
    }

    [Fact]
    public void ValidationFailure_ClassifiedPermanent_RoutedToCoder()
    {
        var o = Diagnose(MissionWithFailure("input failed validation: missing objective"));
        Assert.Contains("ValidationFailure", o);
        Assert.Contains("retryable: False", o);
        Assert.Contains("\"DestinationRole\":\"coder\"", o.Replace(" ", ""));
    }

    [Fact]
    public void UiFailure_RoutesThroughUiCartographer_BeforeRepair()
    {
        var o = Diagnose(MissionWithFailure("frontend check failed: app.js rendering broken"));
        Assert.Contains("\"DestinationRole\":\"ui_cartographer\"", o.Replace(" ", ""));
    }

    [Fact]
    public void NoFailureInMission_RefusesToDiagnose()
    {
        var m = new Mission { Goal = "all good", Tasks = { new DomainTask { Title = "ok", AssignedAnt = "researcher", Status = Anthill.Core.Domain.TaskStatus.Complete } } };
        var o = Diagnose(m);
        Assert.Contains("\"status\":\"blocked\"", o.Replace(" ", ""));
        Assert.Contains("nothing to diagnose", o);
    }

    [Fact]
    public void DiagnosisBudgetExhausted_EscalatesToBuilder_NoRepairLoop()
    {
        var prior1 = new DomainTask { Title = "diag1", AssignedAnt = "medic", Result = "diagnosed once" };
        var prior2 = new DomainTask { Title = "diag2", AssignedAnt = "medic", Result = "diagnosed twice" };
        var o = Diagnose(MissionWithFailure("build failed exit_code=1", prior1, prior2));
        Assert.Contains("budget exhausted", o);
        Assert.Contains("\"DestinationRole\":\"builder\"", o.Replace(" ", ""));
        Assert.DoesNotContain("\"DestinationRole\":\"coder\"", o.Replace(" ", ""));
    }

    [Fact]
    public void RepeatedIdenticalDiagnosis_Escalates_InsteadOfRepeatingRoute()
    {
        var m = MissionWithFailure("build failed exit_code=1");
        var failedId = m.Tasks[0].Id;
        m.Tasks.Add(new DomainTask
        {
            Title = "diag1", AssignedAnt = "medic",
            Result = $"dedupe: {failedId}:VerificationFailure — routed to coder already",
        });
        var o = Diagnose(m);
        Assert.Contains("escalat", o, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"DestinationRole\":\"builder\"", o.Replace(" ", ""));
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
