using Anthill.Core.Agents;
using Anthill.Core.Contracts;
using Anthill.Core.Outcomes;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.19.0 Stage 4 — TesterAnt is the first specialist on the structured contract.
///
/// It always built a complete AntExecutionResult (checks, evidence, and the medic/verifier
/// handoffs) and then discarded it through Compat(), which stringified everything into prose the
/// executor never parsed. A deterministic check could FAIL and the task was recorded complete.
///
/// Tester goes first deliberately: it is the ant that detects failure, so until its result is
/// authoritative nothing downstream — medic diagnosis, repair, verification — can be trusted.
/// </summary>
public class TesterAntStructuredTests
{
    [Fact]
    public void Execute_IsTheOverriddenContract_NotTheTextFallback()
    {
        var method = typeof(TesterAnt).GetMethod(nameof(BaseAnt.Execute));
        Assert.NotNull(method);
        Assert.Equal(typeof(TesterAnt), method!.DeclaringType);   // overridden, not inherited
        Assert.Equal(typeof(AntExecutionResult), method.ReturnType);
    }

    /// <summary>
    /// A task type outside the tester contract is BLOCKED — permanent, and never retried, because
    /// retrying cannot change a contract answer.
    /// </summary>
    [Fact]
    public void AnUnsupportedTaskType_IsBlocked_AndMapsToAPermanentFailure()
    {
        var contract = AntExecutionCatalog.ContractFor("tester");
        Assert.NotNull(contract);
        Assert.False(contract!.SupportsTaskType("apply_patch"),
            "apply_patch must never be inside the tester contract");

        // The blocked result the ant returns for that case maps to a permanent, non-retryable failure.
        var blocked = AntExecutionResult.Blocked("task type 'apply_patch' is outside the tester execution contract");
        var decision = TaskOutcomeMapper.Map(blocked);

        Assert.Equal(TaskOutcomeAction.Fail, decision.Action);
        Assert.False(decision.Retryable);
        Assert.Equal("blocked", decision.FailureType);
    }

    /// <summary>
    /// The regression this stage exists to close: a failing deterministic check must not produce a
    /// completed task. Built in the shape TesterAnt emits.
    /// </summary>
    [Fact]
    public void AFailingCheck_DoesNotCompleteTheTask()
    {
        var failing = new AntExecutionResult
        {
            Success = false,
            StatusCode = "failed_retryable",
            Summary = "2 check(s): 1 passed, 1 failed.",
            Evidence = { new AntEvidence("check", "dotnet_build", "exit_code=1 success=False") },
            Failure = new AntFailure(FailureClass.VerificationFailure, "one or more checks failed", Retryable: true),
        };

        var decision = TaskOutcomeMapper.Map(failing);

        Assert.Equal(TaskOutcomeAction.Fail, decision.Action);
        Assert.True(decision.Retryable, "a failed check is worth another attempt before escalating");
        Assert.False(TaskOutcomeMapper.IsCompleting(failing));
    }

    [Fact]
    public void APassingCheck_CompletesTheTask()
    {
        var passing = new AntExecutionResult
        {
            Success = true,
            StatusCode = "succeeded",
            Summary = "2 check(s): 2 passed, 0 failed.",
            Evidence = { new AntEvidence("check", "dotnet_build", "exit_code=0 success=True") },
        };

        Assert.Equal(TaskOutcomeAction.Complete, TaskOutcomeMapper.Map(passing).Action);
        Assert.True(TaskOutcomeMapper.IsCompleting(passing));
    }

    /// <summary>
    /// The handoffs the tester proposes now survive to the executor, which records them. Ingestion
    /// through HandoffGate is stage 4b — this proves the proposals are no longer stringified away.
    /// </summary>
    [Fact]
    public void HandoffsSurviveAsStructure_NotProse()
    {
        var failing = new AntExecutionResult
        {
            Success = false,
            StatusCode = "failed_retryable",
            Summary = "1 failed",
            Handoffs =
            {
                new AntHandoff("tester", "medic", "check failure needs diagnosis", "failure_diagnosis",
                    new[] { "test_report" }, true, 1, "tester-fail:m1:t1"),
            },
        };

        var handoff = Assert.Single(failing.Handoffs);
        Assert.Equal("medic", handoff.DestinationRole);
        Assert.Equal("failure_diagnosis", handoff.RequiredTaskType);
        Assert.True(handoff.Required, "a failure diagnosis handoff is required, not advisory");
        Assert.Equal("tester-fail:m1:t1", handoff.DedupeKey);
    }

    /// <summary>
    /// TesterAnt no longer routes through the shared Compat adapter. The adapter still exists for
    /// the five unmigrated specialists and must be gone by the end of stage 5.
    /// </summary>
    [Fact]
    public void TesterNoLongerUsesTheCompatibilityAdapter()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Agents", "SpecialistAnts.cs"));
        var start = source.IndexOf("public sealed class TesterAnt", StringComparison.Ordinal);
        Assert.True(start > 0, "TesterAnt not found");
        var end = source.IndexOf("public sealed class SoldierAnt", start, StringComparison.Ordinal);
        Assert.True(end > start, "SoldierAnt not found after TesterAnt");

        var tester = source[start..end];

        // Assert against CODE, not prose. The migration comment legitimately NAMES the adapter it
        // removed, and a comment mentioning Compat is not the same as code calling it. (Same trap
        // as ResetLayout_TouchesOnlyTheWorkspaceKey, which matched "positions" inside its own
        // explanatory comment.)
        var code = string.Join("\n", tester.Split('\n')
            .Select(line =>
            {
                var i = line.IndexOf("//", StringComparison.Ordinal);
                return i >= 0 ? line[..i] : line;
            }));

        Assert.DoesNotContain("Compat(", code);
        Assert.Contains("public override AntExecutionResult Execute", code);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
