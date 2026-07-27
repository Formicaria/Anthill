using Anthill.Core.Configuration;
using Anthill.Core.Domain;
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

    [Fact]
    public void NullOrEmptySynthesis_FallsBackToTheRawAnswer()
    {
        Assert.Equal("raw", Queen.SelectFinalAnswer("raw", null));
        Assert.Equal("raw", Queen.SelectFinalAnswer("raw", ""));
        Assert.Equal("raw", Queen.SelectFinalAnswer("raw", "   \n  "));
    }

    /// <summary>ModelRouter reports failure in-band as an "ERROR:" string rather than throwing.</summary>
    [Fact]
    public void ErrorResponse_FallsBackToTheRawAnswer()
    {
        Assert.Equal("raw", Queen.SelectFinalAnswer("raw", "ERROR: ollama temporarily unavailable"));
        Assert.Equal("raw", Queen.SelectFinalAnswer("raw", "ERROR: Model routing requested Ollama, but USE_OLLAMA is False."));
    }

    [Fact]
    public void GoodSynthesis_IsUsed_AndTrimmed()
    {
        Assert.Equal("The backup job finished.", Queen.SelectFinalAnswer("raw", "  The backup job finished.\n "));
    }

    /// <summary>A message that merely mentions an error is still a real answer.</summary>
    [Fact]
    public void AnswerDescribingAnError_IsNotMistakenForAFailedCall()
    {
        const string answer = "The mission failed: the ERROR: prefix only counts at the start.";
        Assert.Equal(answer, Queen.SelectFinalAnswer("raw", answer));
    }

    [Fact]
    public void SynthesisIsSkipped_WhenDisabled()
    {
        var prior = AnthillRuntime.EnableAnswerSynthesis;
        try
        {
            AnthillRuntime.EnableAnswerSynthesis = false;
            Assert.False(Queen.ShouldSynthesizeAnswer(new string('x', 5000)));
        }
        finally { AnthillRuntime.EnableAnswerSynthesis = prior; }
    }

    /// <summary>A short answer is already prose; paying a model call to rewrite it buys nothing.</summary>
    [Fact]
    public void SynthesisIsSkipped_ForShortOrEmptyAnswers()
    {
        var prior = AnthillRuntime.EnableAnswerSynthesis;
        try
        {
            AnthillRuntime.EnableAnswerSynthesis = true;
            Assert.False(Queen.ShouldSynthesizeAnswer(""));
            Assert.False(Queen.ShouldSynthesizeAnswer("   "));
            Assert.False(Queen.ShouldSynthesizeAnswer(new string('x', Queen.AnswerSynthesisMinChars - 1)));
            Assert.True(Queen.ShouldSynthesizeAnswer(new string('x', Queen.AnswerSynthesisMinChars)));
        }
        finally { AnthillRuntime.EnableAnswerSynthesis = prior; }
    }

    /// <summary>
    /// The prompt must not let a failed mission be narrated as a success, must carry the operator's
    /// original question, and must forbid inventing findings.
    /// </summary>
    [Fact]
    public void Prompt_CarriesOutcomeAndForbidsFabrication()
    {
        var failed = Queen.BuildAnswerSynthesisPrompt(M("check the backups", MissionStatus.Failed), "trace");
        Assert.Contains("FAILED", failed);
        Assert.Contains("Do not present it as a success", failed);
        Assert.Contains("check the backups", failed);
        Assert.Contains("Add nothing", failed);

        var partial = Queen.BuildAnswerSynthesisPrompt(M(status: MissionStatus.Partial), "trace");
        Assert.Contains("PARTIALLY", partial);

        var ok = Queen.BuildAnswerSynthesisPrompt(M(), "trace");
        Assert.Contains("completed successfully", ok);
    }

    /// <summary>Prompt size is bounded so a huge trace cannot blow up the call.</summary>
    [Fact]
    public void Prompt_TruncatesAnOversizedRawAnswer()
    {
        var huge = new string('x', Queen.AnswerSynthesisMaxInputChars * 3);
        var prompt = Queen.BuildAnswerSynthesisPrompt(M(), huge);
        Assert.True(prompt.Length < huge.Length, "an oversized raw answer must be truncated");
        Assert.Contains("[truncated]", prompt);
    }
}
