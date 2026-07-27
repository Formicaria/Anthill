using Anthill.Core.Memory;
using Anthill.Core.Readiness;
using Anthill.Core.Shadow;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.25.0 Phase F — the V3.0 readiness gate.
///
/// Not a feature: an evaluation. These tests pin its one governing rule — nothing can be satisfied
/// by silence. No data reads as NOT ready. No attestation reads as NOT ready. A measured check
/// cannot be attested into passing, and an attested check cannot be inferred into passing.
/// </summary>
public class V3ReadinessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_ready_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        return new SqliteMemory(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
    }

    private static readonly QualificationMetrics CleanShadow =
        new(Sample: 20, DiagnosisPrecision: 0.95, DiagnosisRecall: 0.9, ActionSelectionAccuracy: 0.9,
            UnnecessaryActionRate: 0.05, PredictedSuccessAccuracy: 0.9,
            PolicyViolations: 0, UnverifiedSuccessClaims: 0);

    private static V3Readiness.Inputs Inputs(
        QualificationMetrics? shadow = null, int sample = 20, bool faultStable = true, int faultRuns = 5,
        int executed = 4, int unknownLifecycle = 0,
        IReadOnlyDictionary<string, (bool, string)>? attestations = null) =>
        new(shadow ?? CleanShadow, sample, UnresolvedShadowBacklog: 0,
            faultRuns, FaultInjectionStableStreak: faultStable ? faultRuns : 1, faultStable,
            executed, unknownLifecycle,
            MinShadowSample: 10, MinDiagnosisPrecision: 0.8, MinActionAccuracy: 0.8,
            attestations ?? new Dictionary<string, (bool, string)>());

    private static Dictionary<string, (bool, string)> AllAttested() =>
        V3Readiness.AttestableIds.ToDictionary(id => id, _ => (true, "operator verified"));

    // ---- nothing is satisfied by silence -------------------------------------------------------

    [Fact]
    public void WithNoDataAndNoAttestations_NothingIsReady()
    {
        var report = V3Readiness.Evaluate(Inputs(
            shadow: new QualificationMetrics(0, 0, 0, 0, 0, 0, 0, 0), sample: 0,
            faultStable: false, faultRuns: 0, executed: 0));
        Assert.False(report.Ready);
        Assert.Contains("NOT READY", report.Statement);
        Assert.All(report.Checks, c => Assert.False(c.Satisfied));
    }

    [Fact]
    public void EverythingMeasuredAndAttested_IsReady()
    {
        var report = V3Readiness.Evaluate(Inputs(attestations: AllAttested()));
        Assert.True(report.Ready);
        Assert.Equal(report.Total, report.SatisfiedCount);
        Assert.Contains("READY", report.Statement);
    }

    /// <summary>A measured check cannot be attested into passing: perfect attestations with a
    /// failing shadow sample still fail the accuracy threshold.</summary>
    [Fact]
    public void AnAttestation_CannotSatisfyAMeasuredCheck()
    {
        var report = V3Readiness.Evaluate(Inputs(sample: 2, attestations: AllAttested()));
        var accuracy = report.Checks.Single(c => c.Id == V3Readiness.Ids.ShadowAccuracy);
        Assert.False(accuracy.Satisfied);
        Assert.False(report.Ready);
    }

    /// <summary>And the reverse: clean data cannot infer an attested check into passing.</summary>
    [Fact]
    public void CleanData_CannotInferAnAttestedCheck()
    {
        var report = V3Readiness.Evaluate(Inputs());   // perfect data, zero attestations
        Assert.False(report.Checks.Single(c => c.Id == V3Readiness.Ids.KillSwitch).Satisfied);
        Assert.False(report.Ready);
    }

    /// <summary>MeasuredAndAttested needs BOTH halves.</summary>
    [Fact]
    public void AHybridCheck_NeedsBothHalves()
    {
        // Attested but measured fails (a policy violation in the shadow record).
        var dirty = CleanShadow with { PolicyViolations = 1 };
        var r1 = V3Readiness.Evaluate(Inputs(shadow: dirty, attestations: AllAttested()));
        Assert.False(r1.Checks.Single(c => c.Id == V3Readiness.Ids.PolicyAndCredentials).Satisfied);

        // Measured holds but not attested.
        var r2 = V3Readiness.Evaluate(Inputs());
        Assert.False(r2.Checks.Single(c => c.Id == V3Readiness.Ids.PolicyAndCredentials).Satisfied);
    }

    /// <summary>Zero executed actions is "nothing to measure", which is NOT a pass — the same
    /// stance as the empty shadow scoreboard.</summary>
    [Fact]
    public void ZeroExecutedActions_IsNotAPass()
    {
        var report = V3Readiness.Evaluate(Inputs(executed: 0, attestations: AllAttested()));
        var check = report.Checks.Single(c => c.Id == V3Readiness.Ids.Level3Verification);
        Assert.False(check.Satisfied);
        Assert.Contains("NOT a pass", check.Detail);
    }

    [Fact]
    public void AnActionPredatingTheLifecycleColumn_BlocksTheLevel3Threshold()
    {
        var report = V3Readiness.Evaluate(Inputs(unknownLifecycle: 1, attestations: AllAttested()));
        Assert.False(report.Checks.Single(c => c.Id == V3Readiness.Ids.Level3Verification).Satisfied);
    }

    /// <summary>The tenth threshold is the certification itself: exactly the conjunction of the
    /// other nine, so a report can never certify an unready system.</summary>
    [Fact]
    public void TheCertificationThreshold_IsTheConjunctionOfTheOtherNine()
    {
        var ready = V3Readiness.Evaluate(Inputs(attestations: AllAttested()));
        Assert.True(ready.Checks.Single(c => c.Id == V3Readiness.Ids.CertificationReport).Satisfied);

        var notReady = V3Readiness.Evaluate(Inputs(faultStable: false, attestations: AllAttested()));
        var cert = notReady.Checks.Single(c => c.Id == V3Readiness.Ids.CertificationReport);
        Assert.False(cert.Satisfied);
        Assert.Contains("fabrication", cert.Detail);
    }

    // ---- fault-injection stability is a property of recorded history ---------------------------

    private static SimulationReport Report(bool allPass = true, string flavor = "a")
    {
        var results = new List<ScenarioResult>
        {
            new("scenario-1", true, false, "predicted_success:" + flavor, true, true, true),
            new("scenario-2", true, false, "predicted_failure", true, true, allPass),
        };
        var passed = results.Count(r => r.Passed);
        return new SimulationReport(results.Count, passed, passed == results.Count, results);
    }

    [Fact]
    public void OneRun_IsNeverStable()
    {
        var mem = Memory();
        mem.SaveFaultInjectionRun(Report());
        var (runs, streak, stable, _) = mem.FaultInjectionStability();
        Assert.Equal(1, runs);
        Assert.Equal(1, streak);
        Assert.False(stable);   // stability is a property of repetition
    }

    [Fact]
    public void IdenticalPassingRuns_AreStable()
    {
        var mem = Memory();
        var t = DateTime.UtcNow.AddHours(-3);
        for (var i = 0; i < 3; i++) mem.SaveFaultInjectionRun(Report(), t.AddHours(i));
        var (runs, streak, stable, _) = mem.FaultInjectionStability();
        Assert.Equal(3, runs);
        Assert.Equal(3, streak);
        Assert.True(stable);
    }

    /// <summary>Two runs that both PASS but differ in behaviour are not stable — the pass count
    /// would have hidden the drift; the fingerprint does not.</summary>
    [Fact]
    public void PassPreservingBehaviourDrift_BreaksStability()
    {
        var mem = Memory();
        var t = DateTime.UtcNow.AddHours(-2);
        mem.SaveFaultInjectionRun(Report(flavor: "a"), t);
        mem.SaveFaultInjectionRun(Report(flavor: "b"), t.AddHours(1));
        Assert.False(mem.FaultInjectionStability().Stable);
        Assert.NotEqual(
            SqliteMemory.FaultInjectionFingerprint(Report(flavor: "a")),
            SqliteMemory.FaultInjectionFingerprint(Report(flavor: "b")));
    }

    [Fact]
    public void AFailingRun_BreaksStability_EvenWithIdenticalFingerprints()
    {
        var mem = Memory();
        var t = DateTime.UtcNow.AddHours(-2);
        mem.SaveFaultInjectionRun(Report(allPass: false), t);
        mem.SaveFaultInjectionRun(Report(allPass: false), t.AddHours(1));
        Assert.False(mem.FaultInjectionStability().Stable);
    }

    // ---- attestations are explicit operator records --------------------------------------------

    [Fact]
    public void AnAttestation_RoundTrips_AndIsAudited()
    {
        var mem = Memory();
        Assert.True(mem.SaveReadinessAttestation(V3Readiness.Ids.KillSwitch, true, "pulled it, everything halted", "op"));

        var loaded = mem.LoadReadinessAttestations();
        Assert.True(loaded[V3Readiness.Ids.KillSwitch].Satisfied);
        Assert.Contains("op", loaded[V3Readiness.Ids.KillSwitch].Note);
        Assert.Single(mem.GetRecentEvents(50, eventType: "readiness_attested"));
    }

    /// <summary>An attestation can record NOT satisfied — an operator who found the kill switch
    /// wanting needs that on the record more than one who found it working.</summary>
    [Fact]
    public void ANegativeAttestation_IsRecorded_AndDoesNotSatisfy()
    {
        var mem = Memory();
        mem.SaveReadinessAttestation(V3Readiness.Ids.KillSwitch, false, "homelab stop worked, autonomy stop lagged", "op");
        Assert.False(mem.LoadReadinessAttestations()[V3Readiness.Ids.KillSwitch].Satisfied);
    }

    /// <summary>Unknown threshold ids are refused — a typo cannot create a phantom threshold.</summary>
    [Fact]
    public void AnUnknownThresholdId_IsRefused()
    {
        var mem = Memory();
        Assert.False(mem.SaveReadinessAttestation("kill_swich", true, "", "op"));
        Assert.False(mem.SaveReadinessAttestation("", true, "", "op"));
        Assert.Empty(mem.LoadReadinessAttestations());
    }

    /// <summary>The certification threshold is not attestable — it is computed, and letting an
    /// operator attest it would let the report certify itself.</summary>
    [Fact]
    public void TheCertificationThreshold_CannotBeAttested()
    {
        Assert.DoesNotContain(V3Readiness.Ids.CertificationReport, V3Readiness.AttestableIds);
        Assert.DoesNotContain(V3Readiness.Ids.ShadowAccuracy, V3Readiness.AttestableIds);
        Assert.DoesNotContain(V3Readiness.Ids.FaultInjectionStable, V3Readiness.AttestableIds);
        Assert.False(Memory().SaveReadinessAttestation(V3Readiness.Ids.CertificationReport, true, "", "op"));
    }

    // ---- the call sites ------------------------------------------------------------------------

    /// <summary>The evaluation, the attestation write, the operator's shadow judgment, and the
    /// certification report all have production surfaces — asserted here because this codebase has
    /// shipped seven well-tested subsystems that nothing called.</summary>
    [Fact]
    public void TheReadinessAndJudgmentEndpointsExist()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "ApiHost.cs"));
        Assert.Contains("\"/readiness/json\"", source);
        Assert.Contains("\"/readiness/attest\"", source);
        Assert.Contains("\"/readiness/certification\"", source);
        Assert.Contains("V3Readiness.Evaluate", source);
        Assert.Contains("SaveReadinessAttestation", source);
        // The operator's half of the shadow loop — RecordOperatorJudgment finally has a caller.
        Assert.Contains("\"/shadow/judge\"", source);
        Assert.Contains("RecordOperatorJudgment", source);
        // And the certification is always truthful about an unready system.
        Assert.Contains("does NOT certify", source);
    }

    /// <summary>Fault injection runs on the shared scheduler and records every run.</summary>
    [Fact]
    public void FaultInjectionRidesTheSharedScheduler()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "Homelab", "ApiHost.Homelab.cs"));
        Assert.Contains("\"fault-injection\"", source);
        Assert.Contains("SaveFaultInjectionRun", source);
        Assert.Contains("ShadowSimulation.RunAll", source);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
