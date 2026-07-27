using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Anthill.Core.Outcomes;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// v2.19.0 Stage 5/6: the verifier declares a verdict, and the mission gate requires it to be a
/// pass. Before this, VerifierAnt returned prose through the default text wrapper, so
/// "Verification Failed" was recorded with StatusCode "succeeded" on a Complete task — and the
/// gate, which asked only whether a verification task had completed, graded the mission
/// completed_verified. Positive learning and the auto-apply precondition both followed from a
/// verdict that said the opposite.
/// </summary>
public class VerificationVerdictTests
{
    // ---- parsing -----------------------------------------------------------------------------

    [Theory]
    [InlineData("Verification Passed\nReasoning: fine.", VerificationVerdict.Passed)]
    [InlineData("Verification Failed\nReasoning: broken.", VerificationVerdict.Failed)]
    [InlineData("Needs Improvement\nReasoning: thin.", VerificationVerdict.NeedsImprovement)]
    [InlineData("verification passed", VerificationVerdict.Passed)]           // case-insensitive
    [InlineData("VERIFICATION FAILED", VerificationVerdict.Failed)]
    public void RecognisedVerdicts_ParseToTheirValue(string text, string expected) =>
        Assert.Equal(expected, VerificationVerdict.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("the mission went fine, I think")]
    public void AbsentOrUnrecognisedOutput_IsUnknown_NotAPass(string? text)
    {
        Assert.Equal(VerificationVerdict.Unknown, VerificationVerdict.Parse(text));
        Assert.False(VerificationVerdict.TextIsPass(text));
    }

    /// <summary>
    /// Not hypothetical: the verifier prompt lists every option on one line
    /// ("Verdict: Verification Passed / Needs Improvement / Verification Failed"). A model that
    /// echoes it must not be read as whichever verdict the parser checks first — that would make a
    /// coin flip decide whether generated code may auto-apply.
    /// </summary>
    [Fact]
    public void OutputContainingMultipleVerdicts_IsAmbiguous_AndFailsClosed()
    {
        const string echoed = "Verdict: Verification Passed / Needs Improvement / Verification Failed";
        Assert.Equal(VerificationVerdict.Unknown, VerificationVerdict.Parse(echoed));
        Assert.False(VerificationVerdict.TextIsPass(echoed));
    }

    [Fact]
    public void OnlyAnUnambiguousPass_CountsAsAPass()
    {
        Assert.True(VerificationVerdict.IsPass(VerificationVerdict.Passed));
        foreach (var v in new[] { VerificationVerdict.Failed, VerificationVerdict.NeedsImprovement, VerificationVerdict.Unknown, null, "" })
            Assert.False(VerificationVerdict.IsPass(v), $"'{v}' must not count as a pass");
    }

    // ---- the ant declares its own verdict ------------------------------------------------------

    private static VerifierAnt StaticVerifier() => new(useOllama: false, router: null);

    private static (DomainTask, Mission) MissionThatPasses()
    {
        var v = new DomainTask { Title = "verify", AssignedAnt = "verifier", TaskType = "verify" };
        var m = new Mission { Goal = "ship it", Tasks = { v } };
        m.Tasks.Insert(0, new DomainTask { Title = "research", AssignedAnt = "researcher", Status = TaskStatus.Complete, Result = "notes" });
        m.Tasks.Insert(0, new DomainTask { Title = "build", AssignedAnt = "builder", Status = TaskStatus.Complete, Result = "the answer" });
        return (v, m);
    }

    private static (DomainTask, Mission) MissionThatFails()
    {
        var v = new DomainTask { Title = "verify", AssignedAnt = "verifier", TaskType = "verify" };
        var m = new Mission { Goal = "ship it", Tasks = { v } };
        m.Tasks.Insert(0, new DomainTask { Title = "build", AssignedAnt = "builder", Status = TaskStatus.Failed, Critical = true, FailureReason = "boom" });
        return (v, m);
    }

    [Fact]
    public void APassingVerification_IsSucceeded_AndCarriesThePassVerdict()
    {
        var (t, m) = MissionThatPasses();
        var o = StaticVerifier().Execute(t, m);
        Assert.Equal("succeeded", o.StatusCode);
        Assert.Empty(o.Warnings);
        Assert.Contains(o.Evidence, e => e.Kind == "verification_verdict" && e.Value == VerificationVerdict.Passed);
    }

    /// <summary>
    /// A non-pass is NOT a task failure. The verification ran correctly and produced a finding, so
    /// failing the task would route through ApplyNonCompletingOutcome and replace the full verdict
    /// text with a one-line reason — destroying the explanation the operator needs. The verdict
    /// travels as evidence and warnings; the mission gate is what refuses to call it verified.
    /// </summary>
    [Fact]
    public void AFailingVerification_StillCompletes_ButCarriesTheFailedVerdict()
    {
        var (t, m) = MissionThatFails();
        var o = StaticVerifier().Execute(t, m);
        Assert.True(o.Success);
        Assert.Equal("succeeded_with_warnings", o.StatusCode);
        Assert.Contains(o.Evidence, e => e.Kind == "verification_verdict" && e.Value == VerificationVerdict.Failed);
        Assert.NotEmpty(o.Warnings);
        Assert.Null(o.Failure);
    }

    [Fact]
    public void TheFullVerdictText_SurvivesAsTheOperatorRecord()
    {
        var (t, m) = MissionThatFails();
        var o = StaticVerifier().Execute(t, m);
        var recorded = o.Narrative ?? o.Summary;
        // Queen stores Narrative ?? Summary. Reasoning and risk notes must not be summarised away.
        Assert.Contains("Verification Failed", recorded);
        Assert.Contains("Reasoning:", recorded);
        Assert.Contains("Risk Notes:", recorded);
    }

    /// <summary>
    /// Run() is still the compatibility surface and must be byte-identical to what it produced
    /// before the migration, because the mission thread and operator UI render it directly.
    /// </summary>
    [Fact]
    public void RunOutput_IsUnchangedByTheMigration_AndMatchesTheNarrative()
    {
        var (t, m) = MissionThatPasses();
        var ant = StaticVerifier();
        Assert.Equal(ant.Run(t, m), ant.Execute(t, m).Narrative);
    }

    // ---- end to end: the verdict reaches the mission gate --------------------------------------

    [Fact]
    public void TheAntsVerdict_IsWhatTheMissionGateReads()
    {
        var (failT, failM) = MissionThatFails();
        failT.Result = StaticVerifier().Execute(failT, failM).Narrative;
        failT.Status = TaskStatus.Complete;
        Assert.False(MissionVerification.IsSatisfied(failM.Tasks));

        var (passT, passM) = MissionThatPasses();
        passT.Result = StaticVerifier().Execute(passT, passM).Narrative;
        passT.Status = TaskStatus.Complete;
        Assert.True(MissionVerification.IsSatisfied(passM.Tasks));
    }
}
