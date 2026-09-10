using Anthill.Api.Knowledge;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE UNATTENDED STUDY PASS ASKS PERMISSION FIRST, EVERY TIME. v0.3.8.156.
///
/// `.154` shipped Study as a BUTTON and wrote down why: "a button that silently enrolled a knowledge
/// base into continuous work would be an automation decision made by a click that did not look like
/// one." Turning it into a schedule is exactly that decision, so it is made in the config file and
/// the pass refuses until it has been.
/// </summary>
public class KnowledgeStudyStagerTests : IDisposable
{
    private readonly string _saved;

    public KnowledgeStudyStagerTests()
    {
        AnthillRuntime.Initialize();
        _saved = AnthillRuntime.KnowledgeAutoStudy;
    }

    public void Dispose() => AnthillRuntime.KnowledgeAutoStudy = _saved;

    /// <summary>OFF unless a colony has said otherwise, in the file.</summary>
    [Fact]
    public void TheDefaultIsOff()
    {
        AnthillRuntime.Initialize();

        Assert.Equal("off", AnthillRuntime.KnowledgeAutoStudy);
    }

    /// <summary>
    /// A TYPO READS AS OFF — the OPPOSITE fallback to `auto_update`, twenty lines away in the same
    /// method, and the difference is the whole point. A typo in `auto_update` must not stop a colony
    /// checking for its own fixes, so it falls to `notify`; a typo here must not enrol a colony into
    /// work it never asked for, so it falls to `off`. Both answer "which way is the mistake
    /// cheaper", and they answer differently because the mistakes are not the same mistake.
    ///
    /// A source guard, because the projection runs off a loaded config file and the property this
    /// guards is which way the `_` arm points.
    /// </summary>
    [Fact]
    public void AnUnrecognisedValue_ReadsAsOff()
    {
        var runtime = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Configuration", "AnthillRuntime.cs")));

        var at = runtime.IndexOf("KnowledgeAutoStudy = (", StringComparison.Ordinal);
        Assert.True(at > 0, "the projection is gone; nothing reads knowledge_auto_study from the file");

        // BOUNDED BY THE DELIMITERS, NOT BY A CHARACTER BUDGET. `GuardHierarchyTests` refuses the
        // budget outright and `SourceText.MemberBody` is the reason it can: a budget is a proxy for
        // "inside this thing" that means something different on a CRLF checkout and drifts every
        // time an explanatory line is added to the code it guards.
        var arm = SourceText.MemberBody(runtime, at);
        Assert.Contains("\"on\" => \"on\"", arm, StringComparison.Ordinal);
        Assert.Contains("_ => \"off\"", arm, StringComparison.Ordinal);
        // AND THE ENVIRONMENT IS READ IN THE SAME ARM. The key declares
        // `ANTHILL_KNOWLEDGE_AUTO_STUDY`, and `ConfigCatalogTests` holds that a declared override
        // the runtime never reads is a setting an operator can set and watch do nothing.
        Assert.Contains("ANTHILL_KNOWLEDGE_AUTO_STUDY", arm, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND A PASS WITH THE SETTING OFF DOES NOTHING AND SAYS SO — a typed refusal rather than a
    /// silent return, because the button, a test and the timer drive this same method and all three
    /// need the difference between "nothing to study" and "not allowed to".
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task WithTheSettingOff_NothingIsStudied()
    {
        AnthillRuntime.KnowledgeAutoStudy = "off";

        var pass = await KnowledgeStudyStager.StudyIfEnabled();

        Assert.False(pass.Ran);
        Assert.Equal(0, pass.Submitted);
        Assert.Contains("knowledge_auto_study", pass.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// TWO GATES, BOTH ASKED. `knowledge_auto_study: on` in a colony whose `knowledge_enabled` is
    /// false has said yes to a schedule for a feature it has not switched on — and reaching the
    /// network on the strength of the narrower flag would be the outer gate reaching nobody, which
    /// is the defect this repository names most often.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task WithKnowledgeDisabled_TheTimerStudiesNothing()
    {
        AnthillRuntime.KnowledgeAutoStudy = "on";

        // The suite's colonies run with knowledge off unless a test turns it on, so this is the
        // ordinary state rather than a contrived one.
        if (AnthillRuntime.Knowledge.Enabled) return;

        var pass = await KnowledgeStudyStager.StudyIfEnabled();

        Assert.False(pass.Ran);
        Assert.Equal(0, pass.Submitted);
    }

    /// <summary>
    /// ONE IMPLEMENTATION, TWO CALLERS. The timer and the operator's button run the same pass —
    /// `SeedOnce` returns a typed result instead of logging and returning void precisely so that can
    /// be true. A source guard, because reaching it at runtime needs a FORAGER and a mission queue.
    /// </summary>
    [Fact]
    public void TheTimerRunsTheSamePassTheButtonDoes()
    {
        var stager = Stager();

        Assert.Contains("KnowledgeSeeder", stager, StringComparison.Ordinal);
        Assert.Contains("SeedOnce", stager, StringComparison.Ordinal);

        // AND IT RESOLVES SCOPE THE WAY A REQUEST DOES. The map is the containment; a scheduled path
        // with its own resolver would be a second answer to "which knowledge may this project read".
        Assert.Contains("ResolveScopeForStudy", stager, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE THREAD CANNOT KILL THE PROCESS. `ColonyDirector` learned this expensively: an exception
    /// escaping a bare background thread takes the colony with it. The inner handler logs; the outer
    /// one leaves quietly, because if logging itself throws there is nothing useful left to do from
    /// a thread nobody is watching.
    /// </summary>
    [Fact]
    public void TheBackgroundThread_GuardsItsOwnLogging()
    {
        var stager = Stager();

        Assert.Contains("IsBackground = true", stager, StringComparison.Ordinal);
        Assert.Contains("catch { break; }", stager, StringComparison.Ordinal);

        // Sleeps through the cancellation handle, never `Thread.Sleep` — a stop must be prompt.
        Assert.Contains("cancel.WaitHandle.WaitOne(Interval)", stager, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep", stager, StringComparison.Ordinal);
    }

    private static string Stager() => File.ReadAllText(Path.Combine(
        SourceText.RepoRoot(), "src", "Anthill.Api", "Knowledge", "KnowledgeStudyStager.cs"));
}
