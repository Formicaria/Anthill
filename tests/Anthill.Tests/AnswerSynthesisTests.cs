using Anthill.Core.Domain;
using Anthill.Core.Models;
using Anthill.Core.Orchestration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.16.0: the mission answer shown to the operator is a plain-English rewrite of the raw
/// best-task output. The rewrite is a presentation nicety, so the rules that matter are the
/// FALLBACKS — a mission must never end up answerless because a model was slow, down, or
/// disabled. Those rules are pure functions precisely so they can be proven without a provider.
/// </summary>
public class AnswerSynthesisTests
{
    private static Mission M(string goal = "do the thing", MissionStatus status = MissionStatus.Complete)
        => new() { Goal = goal, Status = status };

    /// <summary>A successful synthesis call carrying the given content.</summary>
    private static ModelCallResult Ok(string content) => new(ModelCallOutcome.Ok, content);

    [Fact]
    public void NullOrEmptySynthesis_FallsBackToTheRawAnswer()
    {
        Assert.Equal("raw", ResultAssembler.SelectFinalAnswer("raw", null));               // never called
        Assert.Equal("raw", ResultAssembler.SelectFinalAnswer("raw", Ok("")));
        Assert.Equal("raw", ResultAssembler.SelectFinalAnswer("raw", Ok("   \n  ")));
    }

    /// <summary>ModelRouter reports failure in-band as an "ERROR:" string rather than throwing.</summary>
    [Fact]
    public void ErrorResponse_FallsBackToTheRawAnswer()
    {
        // v3.2.0: a failed call is now expressed as a STATUS, not as prose that happens to start
        // with "ERROR:". Each of these previously relied on the prefix; they now rely on the
        // provider having said what went wrong.
        Assert.Equal("raw", ResultAssembler.SelectFinalAnswer("raw",
            new ModelCallResult(ModelCallOutcome.ConnectError, "ERROR: ollama temporarily unavailable")));
        Assert.Equal("raw", ResultAssembler.SelectFinalAnswer("raw",
            new ModelCallResult(ModelCallOutcome.Error, "ERROR: Model routing requested Ollama, but USE_OLLAMA is False.")));
        // And the case the prefix test could never catch: a provider that answered with nothing.
        Assert.Equal("raw", ResultAssembler.SelectFinalAnswer("raw",
            new ModelCallResult(ModelCallOutcome.Empty, "Ollama returned an empty response.")));
    }

    [Fact]
    public void GoodSynthesis_IsUsed_AndTrimmed()
    {
        Assert.Equal("The backup job finished.", ResultAssembler.SelectFinalAnswer("raw", Ok("  The backup job finished.\n ")));
    }

    /// <summary>A message that merely mentions an error is still a real answer.</summary>
    [Fact]
    public void AnswerDescribingAnError_IsNotMistakenForAFailedCall()
    {
        // This test is stronger than it was. Under the prefix rule it proved only that "ERROR:"
        // mid-string was tolerated. Now it proves content is not inspected AT ALL: a successful
        // call is used verbatim however much it talks about errors.
        const string answer = "The mission failed: ERROR: appears here and must not matter.";
        Assert.Equal(answer, ResultAssembler.SelectFinalAnswer("raw", Ok(answer)));
    }

    /// <summary>
    /// v3.1.0 (ADR-001): the gate is a PARAMETER, not a static read. These two tests used to
    /// save/mutate/restore AnthillRuntime.EnableAnswerSynthesis around the assertion — the exact
    /// global-state dance the phase exists to remove, and the reason two runtimes could not share
    /// a process. The rule under test is unchanged; it is simply now decidable from arguments.
    /// </summary>
    [Fact]
    public void SynthesisIsSkipped_WhenDisabled() =>
        Assert.False(ResultAssembler.ShouldSynthesizeAnswer(new string('x', 5000), synthesisEnabled: false));

    /// <summary>A short answer is already prose; paying a model call to rewrite it buys nothing.</summary>
    [Fact]
    public void SynthesisIsSkipped_ForShortOrEmptyAnswers()
    {
        Assert.False(ResultAssembler.ShouldSynthesizeAnswer("", synthesisEnabled: true));
        Assert.False(ResultAssembler.ShouldSynthesizeAnswer("   ", synthesisEnabled: true));
        Assert.False(ResultAssembler.ShouldSynthesizeAnswer(
            new string('x', ResultAssembler.AnswerSynthesisMinChars - 1), synthesisEnabled: true));
        Assert.True(ResultAssembler.ShouldSynthesizeAnswer(
            new string('x', ResultAssembler.AnswerSynthesisMinChars), synthesisEnabled: true));
    }

    /// <summary>
    /// Two decisions taken at once cannot disagree — the property the parameterisation buys. Under
    /// the old static read this was not expressible as a test at all.
    /// </summary>
    [Fact]
    public void TwoRunsWithDifferentSettings_DecideIndependently()
    {
        var answer = new string('x', ResultAssembler.AnswerSynthesisMinChars);
        Assert.True(ResultAssembler.ShouldSynthesizeAnswer(answer, synthesisEnabled: true));
        Assert.False(ResultAssembler.ShouldSynthesizeAnswer(answer, synthesisEnabled: false));
    }

    /// <summary>
    /// The prompt must not let a failed mission be narrated as a success, must carry the operator's
    /// original question, and must forbid inventing findings.
    /// </summary>
    [Fact]
    public void Prompt_CarriesOutcomeAndForbidsFabrication()
    {
        var failed = ResultAssembler.BuildAnswerSynthesisPrompt(M("check the backups", MissionStatus.Failed), "trace");
        Assert.Contains("FAILED", failed);
        Assert.Contains("Do not present it as a success", failed);
        Assert.Contains("check the backups", failed);
        Assert.Contains("Add nothing", failed);

        var partial = ResultAssembler.BuildAnswerSynthesisPrompt(M(status: MissionStatus.Partial), "trace");
        Assert.Contains("PARTIALLY", partial);

        var ok = ResultAssembler.BuildAnswerSynthesisPrompt(M(), "trace");
        Assert.Contains("completed successfully", ok);
    }

    /// <summary>Prompt size is bounded so a huge trace cannot blow up the call.</summary>
    [Fact]
    public void Prompt_TruncatesAnOversizedRawAnswer()
    {
        var huge = new string('x', ResultAssembler.AnswerSynthesisMaxInputChars * 3);
        var prompt = ResultAssembler.BuildAnswerSynthesisPrompt(M(), huge);
        Assert.True(prompt.Length < huge.Length, "an oversized raw answer must be truncated");
        Assert.Contains("[truncated]", prompt);
    }
}
