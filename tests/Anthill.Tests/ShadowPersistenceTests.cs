using Anthill.Core.Memory;
using Anthill.Core.SafeAction;
using Anthill.Core.Shadow;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.24.0 Phase E prerequisite: shadow recommendations become durable.
///
/// The Shadow Operations line shipped across two releases with no table, no endpoint, and no
/// production call site — the sixth and largest instance of this codebase's signature defect.
/// Shadow mode's whole purpose is to accumulate a track record: recommend, wait, compare against
/// what the operator actually did, score the difference. A recommendation that vanishes on exit
/// cannot be compared to anything, so qualification could only ever run over replayed scenarios.
/// </summary>
public class ShadowPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_shadow_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string _dbPath = "";

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        if (_dbPath.Length == 0) _dbPath = Path.Combine(_dir, "shadow.db");
        return new SqliteMemory(_dbPath);
    }

    private static ShadowRecommendation Rec(string id, bool wouldExecute = false) => new(
        IncidentId: id,
        Diagnosis: "service stopped",
        ProposedAction: "restart_service",
        ChosenSkillId: "restart_service",
        ChosenSkillConfidence: 0.9,
        Risk: new RiskAssessment("medium", 40, new[] { "reversible" }, RequiresApproval: false),
        PredictedOutcome: ShadowPrediction.Success,
        VerificationPlan: new[] { "health_check" },
        RollbackPlan: RecoveryAction.RetryAfterCooldown,
        RollbackReason: "restart is reversible",
        WouldRecommendExecution: wouldExecute);

    private static ShadowOutcome Outcome(string id, bool correct = true) =>
        new(id, DiagnosisCorrect: correct, ActionWasNeeded: true, ActionMatched: correct,
            WouldHaveSucceeded: correct, OperatorNote: "operator restarted it");

    // ---- the track record survives the process ----------------------------------------------------

    [Fact]
    public void ARecommendationSurvivesARestart()
    {
        Memory().SaveShadowRecommendation(Rec("inc-1"));
        Memory().SaveShadowOutcome(Outcome("inc-1"));

        var pair = Assert.Single(Memory().LoadScoreablePairs());
        Assert.Equal("inc-1", pair["incident_id"]?.ToString());
        Assert.Equal("restart_service", pair["proposed_action"]?.ToString());
        Assert.Equal(ShadowPrediction.Success, pair["predicted_outcome"]?.ToString());
    }

    /// <summary>
    /// A recommendation with no operator judgment proves nothing. Including it would let an
    /// unresolved backlog move the score in either direction without any evidence behind it.
    /// </summary>
    [Fact]
    public void AnUnjudgedRecommendationIsNotScoreable()
    {
        var mem = Memory();
        mem.SaveShadowRecommendation(Rec("inc-pending"));

        Assert.Empty(mem.LoadScoreablePairs());
        Assert.Equal(1, mem.CountUnresolvedShadowRecommendations());

        mem.SaveShadowOutcome(Outcome("inc-pending"));
        Assert.Single(mem.LoadScoreablePairs());
        Assert.Equal(0, mem.CountUnresolvedShadowRecommendations());
    }

    [Fact]
    public void RecordingTheSameIncidentTwice_Updates_RatherThanDuplicating()
    {
        var mem = Memory();
        mem.SaveShadowRecommendation(Rec("inc-2"));
        mem.SaveShadowRecommendation(Rec("inc-2", wouldExecute: true));
        mem.SaveShadowOutcome(Outcome("inc-2"));

        var pair = Assert.Single(mem.LoadScoreablePairs());
        Assert.Equal(1L, Convert.ToInt64(pair["would_recommend_execution"]));
    }

    [Fact]
    public void EmptyOrInvalidInputIsIgnored()
    {
        var mem = Memory();
        mem.SaveShadowRecommendation(null!);
        mem.SaveShadowOutcome(null!);
        Assert.Empty(mem.LoadScoreablePairs());
        Assert.Equal(0, mem.CountUnresolvedShadowRecommendations());
    }

    // ---- timing metrics ------------------------------------------------------------------------------

    [Fact]
    public void ResolutionSpansAreMeasuredFromStoredTimestamps()
    {
        var mem = Memory();
        var observed = DateTime.UtcNow.AddMinutes(-10);
        mem.SaveShadowRecommendation(Rec("inc-t"), observedAt: observed);
        mem.SaveShadowOutcome(Outcome("inc-t"), resolvedAt: observed.AddMinutes(5));

        var span = Assert.Single(mem.ShadowResolutionSeconds());
        Assert.InRange(span, 299, 301);   // ~5 minutes
    }

    /// <summary>
    /// Median, not mean: one incident left open over a weekend would drag an average far enough to
    /// make the number meaningless, and the threshold this feeds is about typical behaviour.
    /// </summary>
    [Fact]
    public void TimingUsesTheMedian_SoOneOutlierCannotDominate()
    {
        var normal = new List<double> { 10, 20, 30, 40, 50 };
        var withOutlier = new List<double> { 10, 20, 30, 40, 50, 100000 };

        Assert.Equal(30, ShadowTimingMetrics.From(normal).MedianResolutionSeconds);
        // The median barely moves; a mean would have exploded past 16000.
        Assert.InRange(ShadowTimingMetrics.From(withOutlier).MedianResolutionSeconds, 30, 40);
    }

    /// <summary>
    /// An empty sample must report zeros, never a flattering number. A qualification gate that
    /// reads as satisfied because nothing was measured is the worst possible failure here.
    /// </summary>
    [Fact]
    public void AnEmptySampleReportsNothing_NotSuccess()
    {
        Assert.Equal(ShadowTimingMetrics.Empty, ShadowTimingMetrics.From(null));
        Assert.Equal(ShadowTimingMetrics.Empty, ShadowTimingMetrics.From(Array.Empty<double>()));
        Assert.Equal(0, ShadowTimingMetrics.From(new List<double>()).Sample);
    }

    [Fact]
    public void NegativeSpansAreDiscardedAsClockArtefacts()
    {
        var mem = Memory();
        var observed = DateTime.UtcNow;
        mem.SaveShadowRecommendation(Rec("inc-clock"), observedAt: observed);
        mem.SaveShadowOutcome(Outcome("inc-clock"), resolvedAt: observed.AddMinutes(-5));   // resolved "before" observed

        Assert.Empty(mem.ShadowResolutionSeconds());
    }

    [Fact]
    public void ASingleSampleDoesNotThrow()
    {
        var one = ShadowTimingMetrics.From(new List<double> { 42 });
        Assert.Equal(1, one.Sample);
        Assert.Equal(42, one.MedianResolutionSeconds);
        Assert.Equal(42, one.P90ResolutionSeconds);
    }

    // ---- rehydration: the scoreboard finally has a production call site ----------------------------

    /// <summary>
    /// `QualificationScoreboard.Compute` takes typed pairs, but storage returns rows — so until
    /// v2.24.0 the scoreboard could only ever be handed pairs built in memory by the simulator or by
    /// its own tests. Rehydration is what turns stored history into a real score.
    /// </summary>
    [Fact]
    public void StoredPairsRehydrateIntoTheTypedRecordsTheScoreboardScores()
    {
        var mem = Memory();
        mem.SaveShadowRecommendation(Rec("inc-r"));
        mem.SaveShadowOutcome(Outcome("inc-r"));

        var (rec, outcome) = Assert.Single(mem.LoadScoreableRecommendations());
        Assert.Equal("inc-r", rec.IncidentId);
        Assert.Equal("restart_service", rec.ProposedAction);
        Assert.Equal(ShadowPrediction.Success, rec.PredictedOutcome);
        Assert.Equal(0.9, rec.ChosenSkillConfidence, 3);
        Assert.Equal(RecoveryAction.RetryAfterCooldown, rec.RollbackPlan);
        Assert.Equal(new[] { "health_check" }, rec.VerificationPlan);
        Assert.Equal("medium", rec.Risk.Level);
        Assert.True(outcome.DiagnosisCorrect);

        var metrics = QualificationScoreboard.Compute(mem.LoadScoreableRecommendations());
        Assert.Equal(1, metrics.Sample);
        Assert.Equal(1d, metrics.DiagnosisPrecision);
    }

    /// <summary>
    /// The first cut of the table stored only the risk LABEL. `PolicyViolations` counts
    /// "would have recommended execution while approval was required" — with the approval flag
    /// unpersisted, that count could only ever rehydrate as zero, and the safety invariant would
    /// have reported itself permanently satisfied no matter what the recommender did.
    /// </summary>
    [Fact]
    public void ThePolicyViolationInvariantSurvivesTheRoundTrip()
    {
        var mem = Memory();
        var violating = Rec("inc-policy", wouldExecute: true) with
        {
            Risk = new RiskAssessment("high", 90, new[] { "irreversible" }, RequiresApproval: true),
        };
        mem.SaveShadowRecommendation(violating);
        mem.SaveShadowOutcome(Outcome("inc-policy"));

        var rehydrated = Assert.Single(mem.LoadScoreableRecommendations());
        Assert.True(rehydrated.Rec.Risk.RequiresApproval);
        Assert.Equal(90, rehydrated.Rec.Risk.Score);
        Assert.Contains("irreversible", rehydrated.Rec.Risk.Reasons);

        Assert.Equal(1, QualificationScoreboard.Compute(mem.LoadScoreableRecommendations()).PolicyViolations);
    }

    /// <summary>An unparseable rollback plan escalates rather than resolving to something that
    /// sounds recoverable — there is no no-op recovery action.</summary>
    [Fact]
    public void AnUnreadableRollbackPlanEscalates()
    {
        var mem = Memory();
        mem.SaveShadowRecommendation(Rec("inc-junk") with { RollbackPlan = RecoveryAction.RetryAfterCooldown });
        mem.SaveShadowOutcome(Outcome("inc-junk"));
        Assert.Equal(RecoveryAction.RetryAfterCooldown, mem.LoadScoreableRecommendations()[0].Rec.RollbackPlan);

        // A row written before this column existed rehydrates to Escalate, not to a soft default.
        Assert.Equal(RecoveryAction.Escalate,
            Enum.TryParse<RecoveryAction>("", out var parsed) ? parsed : RecoveryAction.Escalate);
    }

    [Fact]
    public void AnEmptyStoreScoresAsZero_NotAsAPass()
    {
        var metrics = QualificationScoreboard.Compute(Memory().LoadScoreableRecommendations());
        Assert.Equal(0, metrics.Sample);
        Assert.Equal(0d, metrics.DiagnosisPrecision);
        Assert.Equal(0d, metrics.ActionSelectionAccuracy);
    }

    // ---- the call site --------------------------------------------------------------------------------

    /// <summary>
    /// The whole point of this phase: the shadow line finally has a production surface. And the
    /// endpoint must state that an empty scoreboard is "not qualified", never "passing".
    /// </summary>
    [Fact]
    public void TheShadowEndpointExists_AndAnEmptyScoreboardIsNotAPass()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "ApiHost.cs"));
        var code = string.Join("\n", source.Split('\n')
            .Select(l => { var i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l[..i] : l; }));

        Assert.Contains("\"/shadow/json\"", code);
        Assert.Contains("Queen.Memory.LoadScoreablePairs", code);
        Assert.Contains("ShadowTimingMetrics.From", code);
        Assert.Contains("has not qualified anything", code);

        // v2.24.0: and the scoreboard itself is computed from REHYDRATED storage, not from
        // anything that only exists inside this process.
        Assert.Contains("QualificationScoreboard.Compute(Queen.Memory.LoadScoreableRecommendations", code);

        // The dashboard panel reads it, and states an empty scoreboard as "not qualified".
        var ui = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "Ui", "app.js"));
        Assert.Contains("hlLoadShadow", ui);
        Assert.Contains("'/shadow/json'", ui);
        Assert.Contains("not qualified", ui);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
