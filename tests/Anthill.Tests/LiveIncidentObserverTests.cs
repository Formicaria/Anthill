using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Modules.Homelab;
using Anthill.Core.Memory;
using Anthill.Api;                      // v3.8.7: LiveIncidentObserver moved to the composition root
using Anthill.Core.Shadow;
using Anthill.Core.Skills;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.24.0 Phase E: shadow mode observes REAL incidents.
///
/// Two releases built a recommendation engine and a fault harness that only ever ran against
/// replayed scenarios, because nothing called them and nothing stored the result. Storage came
/// first; this is the caller.
///
/// The property that makes it shippable: shadow mode watches an incident open, records what it
/// WOULD have done, and stops. These tests hold that line.
/// </summary>
[Collection("specialist-gates")]
public class LiveIncidentObserverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_obs_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        return new SqliteMemory(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
    }

    private static IncidentRecord Incident(string subjectKind = "service", string id = "inc-live") => new()
    {
        Id = id, Title = "jellyfin is down", SubjectKind = subjectKind, SubjectId = "jellyfin",
        Severity = "error", Status = "open", OpenedAt = AnthillTime.NowUtc().ToIso(),
    };

    private static T WithObservation<T>(Func<T> body)
    {
        try
        {
            AnthillRuntime.EnableShadowObservation = true;
            return body();
        }
        finally { AnthillRuntime.EnableShadowObservation = false; }
    }

    // ---- gated, and silent when off ----------------------------------------------------------------

    [Fact]
    public void ShadowObservation_IsOffByDefault()
    {
        Assert.False(AnthillRuntime.EnableShadowObservation);
        Assert.False(new AnthillConfig().ShadowObservationEnabled);
    }

    /// <summary>With the gate closed, observing an incident writes nothing at all.</summary>
    [Fact]
    public void WithTheGateClosed_NothingIsRecorded()
    {
        var mem = Memory();
        Assert.Null(LiveIncidentObserver.Observe(mem, new SkillRegistry(), Incident()));
        Assert.Equal(0, mem.CountUnresolvedShadowRecommendations());
    }

    // ---- observation records, and never executes ----------------------------------------------------

    [Fact]
    public void ObservingAnIncident_RecordsARecommendation()
    {
        var mem = Memory();
        var rec = WithObservation(() => LiveIncidentObserver.Observe(mem, new SkillRegistry(), Incident()));

        Assert.NotNull(rec);
        Assert.Equal("inc-live", rec!.IncidentId);
        Assert.Equal(1, mem.CountUnresolvedShadowRecommendations());   // awaiting operator judgment
        Assert.Empty(mem.LoadScoreablePairs());                        // and not scoreable until then
    }

    /// <summary>
    /// The safety property, stated in the audit trail as well as the code: the recorded event says
    /// the recommendation was not executed. Shadow mode has no action pathway at all.
    /// </summary>
    [Fact]
    public void TheRecordedEventStatesThatNothingWasExecuted()
    {
        var mem = Memory();
        WithObservation(() => LiveIncidentObserver.Observe(mem, new SkillRegistry(), Incident()));

        var ev = Assert.Single(mem.GetRecentEvents(50, eventType: "shadow_recommendation_recorded"));
        Assert.Contains("executed", ev["metadata_json"]?.ToString() ?? "");
        Assert.Contains("false", (ev["metadata_json"]?.ToString() ?? "").ToLowerInvariant());
    }

    /// <summary>
    /// An incident is the worst possible moment to add a second failure. Observation is
    /// best-effort: a null registry, a malformed incident, anything — the caller proceeds.
    /// </summary>
    [Fact]
    public void ObservationNeverThrows()
    {
        var mem = Memory();
        WithObservation(() =>
        {
            Assert.Null(LiveIncidentObserver.Observe(null, null, Incident()));
            Assert.Null(LiveIncidentObserver.Observe(mem, null, null));
            Assert.Null(LiveIncidentObserver.Observe(mem, null, new IncidentRecord { Id = "" }));
            // A registry-less observation still works — skills are optional context, not a requirement.
            Assert.NotNull(LiveIncidentObserver.Observe(mem, null, Incident(id: "inc-noskills")));
            return 0;
        });
    }

    // ---- the operation is derived from the subject, not from prose ------------------------------------

    /// <summary>
    /// Reading intent out of the incident TITLE would make the recommendation a function of how the
    /// title happened to be worded — and the qualification score would then be measuring wording.
    /// The subject kind is structured data; the title is not.
    /// </summary>
    [Theory]
    [InlineData("service", "restart_service")]
    [InlineData("vm", "restart_guest")]
    [InlineData("container", "restart_guest")]
    [InlineData("storage", "restore")]
    [InlineData("host", "investigate_host")]
    public void TheProposedOperationComesFromTheSubjectKind(string kind, string expected) =>
        Assert.Equal(expected, LiveIncidentObserver.ToObservation(Incident(kind)).ProposedOperation);

    /// <summary>An unrecognised subject gets the least invasive operation there is.</summary>
    [Fact]
    public void AnUnknownSubjectInvestigates_RatherThanActing() =>
        Assert.Equal("investigate", LiveIncidentObserver.ToObservation(Incident("something_new")).ProposedOperation);

    [Fact]
    public void TheRootCauseIsPreferredOverTheTitle_WhenKnown()
    {
        var withCause = Incident();
        withCause.RootCause = "disk full on host";
        Assert.Equal("disk full on host", LiveIncidentObserver.ToObservation(withCause).Diagnosis);
        Assert.Equal("jellyfin is down", LiveIncidentObserver.ToObservation(Incident()).Diagnosis);
    }

    // ---- the operator's judgment closes the loop --------------------------------------------------------

    [Fact]
    public void RecordingTheOperatorJudgment_MakesThePairScoreable()
    {
        var mem = Memory();
        WithObservation(() => LiveIncidentObserver.Observe(mem, new SkillRegistry(), Incident()));

        LiveIncidentObserver.RecordOperatorJudgment(mem, "inc-live",
            diagnosisCorrect: true, actionWasNeeded: true, actionMatched: true, wouldHaveSucceeded: true,
            note: "restarted it myself");

        Assert.Single(mem.LoadScoreablePairs());
        Assert.Equal(0, mem.CountUnresolvedShadowRecommendations());
        Assert.Single(mem.GetRecentEvents(50, eventType: "shadow_outcome_recorded"));
    }

    [Fact]
    public void JudgmentWithoutAnIncidentId_IsIgnored()
    {
        var mem = Memory();
        LiveIncidentObserver.RecordOperatorJudgment(mem, "", true, true, true, true);
        LiveIncidentObserver.RecordOperatorJudgment(null, "inc-x", true, true, true, true);
        Assert.Empty(mem.LoadScoreablePairs());
    }

    // ---- the call site ------------------------------------------------------------------------------------

    /// <summary>
    /// The hook fires only for a genuinely NEW incident. `IncidentManager.Open` deduplicates by
    /// subject, so observing a deduplicated re-open would inflate the qualification sample with
    /// repeats of the same event.
    /// </summary>
    [Fact]
    public void TheIncidentManagerInvokesTheHook_AfterTheRepositoryAcceptsANewIncident()
    {
        // v3.8.7: the incident layer left the core with the rest of the homelab. This guard reads
        // source by PATH, so it is one of the few things a file move genuinely breaks — and it is
        // supposed to: a path-based assertion that silently stopped finding its file would pass by
        // vacuity, which is worse than failing.
        var source = File.ReadAllText(Path.Combine(RepoRoot(),
            "src", "Anthill.Modules", "Anthill.Modules.Homelab", "Incidents", "IncidentManager.cs"));
        var code = string.Join("\n", source.Split('\n')
            .Select(l => { var i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l[..i] : l; }));

        // Invoked after the repository call, and inside a try so a shadow failure cannot become an incident.
        var openBody = code[code.IndexOf("public IncidentRecord Open(", StringComparison.Ordinal)..];
        openBody = openBody[..openBody.IndexOf("internal bool IsRepeatOffender", StringComparison.Ordinal)];
        Assert.Contains("_repository.OpenIncident(incident, openedBy)", openBody);
        Assert.Contains("_onOpened?.Invoke(incident)", openBody);
        Assert.Contains("try {", openBody);

        // ...and the early return for a deduplicated incident happens BEFORE the hook.
        Assert.True(openBody.IndexOf("return existing", StringComparison.Ordinal)
                  < openBody.IndexOf("_onOpened?.Invoke", StringComparison.Ordinal),
            "a deduplicated re-open must not be observed as a new incident");
    }

    [Fact]
    public void TheCompositionRootWiresTheObserver()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "Homelab", "ApiHost.Homelab.cs"));
        Assert.Contains("LiveIncidentObserver.Observe(Queen.Memory", source);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
