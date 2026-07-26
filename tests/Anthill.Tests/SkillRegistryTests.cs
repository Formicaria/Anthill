using Anthill.Core.Skills;
using Anthill.Core.Verification;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Phase 5 success criteria under test: only VERIFIED outcomes advance a skill; nothing
/// self-certifies; repeated failure degrades then retires; environment drift invalidates coverage;
/// the planner prefers certified over experimental and is offered nothing when neither exists.
/// </summary>
public class SkillRegistryTests
{
    private sealed class FakeVerifier : IVerifier
    {
        private readonly bool _pass;
        public FakeVerifier(string name, bool pass) { Name = name; _pass = pass; }
        public string Name { get; }
        public bool Deterministic => true;
        public VerificationResult Verify(VerificationRequest r) =>
            new(Name, _pass, true, _pass ? "ok" : "no", new List<VerificationEvidence> { new("k", "v") });
    }

    private static VerificationBundle Verified() =>
        new VerificationRunner(new IVerifier[] { new FakeVerifier("diff", true), new FakeVerifier("security_policy", true) })
            .Run(new VerificationRequest("docs_patch", Path.GetTempPath(), ChangedPath: "docs/a.md"));

    private static VerificationBundle Unverified() =>
        new VerificationRunner(new IVerifier[] { new FakeVerifier("diff", true), new FakeVerifier("security_policy", false) })
            .Run(new VerificationRequest("docs_patch", Path.GetTempPath(), ChangedPath: "docs/a.md"));

    [Fact]
    public void Sanity_BundleHelpersBehaveAsExpected()
    {
        Assert.True(Verified().Promotable);
        Assert.False(Unverified().Promotable);
    }

    // ---- Promotion requires verified evidence ---------------------------------------------------

    [Fact]
    public void NewSkill_IsCandidate_AndNotUsable()
    {
        var reg = new SkillRegistry();
        var s = reg.RegisterCandidate("restart-stuck-lxc", "restart a stuck container");
        Assert.Equal(SkillStatus.Candidate, s.Status);
        Assert.False(s.UsableIn("proxmox-8"));
        Assert.Null(reg.PreferredFor("restart", "proxmox-8"));
    }

    [Fact]
    public void VerifiedSuccess_PromotesToExperimental_ThenCertified()
    {
        var reg = new SkillRegistry();
        reg.RegisterCandidate("fix-docs", "update documentation");
        Assert.Equal(SkillStatus.Experimental, reg.RecordOutcome("fix-docs", Verified(), "dotnet-9"));
        Assert.Equal(SkillStatus.Experimental, reg.RecordOutcome("fix-docs", Verified(), "dotnet-9"));
        Assert.Equal(SkillStatus.Certified, reg.RecordOutcome("fix-docs", Verified(), "dotnet-9"));
        var s = reg.Get("fix-docs")!;
        Assert.Equal(3, s.SuccessCount);
        Assert.Equal(3, s.EvidenceBundleIds.Count);  // every success carries its proof
        Assert.Contains("dotnet-9", s.Environments);
    }

    [Fact]
    public void UnverifiedOutcome_NeverCounts_AsSuccess()
    {
        var reg = new SkillRegistry();
        reg.RegisterCandidate("sketchy", "do a thing");
        for (var i = 0; i < 5; i++) reg.RecordOutcome("sketchy", Unverified());
        var s = reg.Get("sketchy")!;
        Assert.Equal(0, s.SuccessCount);
        Assert.NotEqual(SkillStatus.Certified, s.Status);
        Assert.Equal(SkillStatus.Retired, s.Status); // repeated unverified outcomes retire it
    }

    [Fact]
    public void MissingBundle_IsFailure_NotSuccess()
    {
        var reg = new SkillRegistry();
        reg.RegisterCandidate("no-proof", "claims success");
        reg.RecordOutcome("no-proof", bundle: null);
        var s = reg.Get("no-proof")!;
        Assert.Equal(0, s.SuccessCount);
        Assert.Equal(1, s.FailureCount);
        Assert.Contains(s.Notes, n => n.Contains("no evidence bundle"));
    }

    [Fact]
    public void Confidence_IsDerived_NotAsserted()
    {
        var reg = new SkillRegistry();
        reg.RegisterCandidate("mixed", "sometimes works");
        reg.RecordOutcome("mixed", Verified());
        reg.RecordOutcome("mixed", Unverified());
        Assert.Equal(0.5, reg.Get("mixed")!.Confidence);
    }

    // ---- Demotion is automatic and symmetric ----------------------------------------------------

    [Fact]
    public void RepeatedFailure_DegradesThenRetires()
    {
        var reg = new SkillRegistry();
        for (var i = 0; i < 3; i++) reg.RecordOutcome("flaky", Verified(), "dotnet-9");
        Assert.Equal(SkillStatus.Certified, reg.Get("flaky")!.Status);
        reg.RecordOutcome("flaky", Unverified());
        reg.RecordOutcome("flaky", Unverified());
        Assert.Equal(SkillStatus.Degraded, reg.Get("flaky")!.Status);
        reg.RecordOutcome("flaky", Unverified());
        reg.RecordOutcome("flaky", Unverified());
        Assert.Equal(SkillStatus.Retired, reg.Get("flaky")!.Status);
    }

    [Fact]
    public void RetiredSkill_DoesNotSilentlyRevive()
    {
        var reg = new SkillRegistry();
        reg.RegisterCandidate("dead", "obsolete method");
        reg.SetOperatorStatus("dead", SkillStatus.Retired, "superseded");
        reg.RecordOutcome("dead", Verified(), "dotnet-9");
        Assert.Equal(SkillStatus.Retired, reg.Get("dead")!.Status);
        Assert.Equal(0, reg.Get("dead")!.SuccessCount);
    }

    [Fact]
    public void EnvironmentChange_DegradesProvenSkills()
    {
        var reg = new SkillRegistry();
        for (var i = 0; i < 3; i++) reg.RecordOutcome("pve-restart", Verified(), "proxmox-8");
        Assert.Equal(SkillStatus.Certified, reg.Get("pve-restart")!.Status);
        reg.OnEnvironmentChanged("proxmox-8", "upgraded to proxmox-9");
        Assert.Equal(SkillStatus.Degraded, reg.Get("pve-restart")!.Status);
        Assert.Null(reg.PreferredFor("pve", "proxmox-8")); // no longer offered until re-proven
    }

    // ---- Planner preference ---------------------------------------------------------------------

    [Fact]
    public void Planner_PrefersCertified_OverExperimental()
    {
        var reg = new SkillRegistry();
        for (var i = 0; i < 3; i++) reg.RecordOutcome("solid-fix", Verified(), "dotnet-9");
        reg.RecordOutcome("new-fix", Verified(), "dotnet-9");
        reg.Get("solid-fix")!.Purpose = "fix the build";
        reg.Get("new-fix")!.Purpose = "fix the build";
        var chosen = reg.PreferredFor("fix the build", "dotnet-9");
        Assert.Equal("solid-fix", chosen!.Id);
        Assert.Equal(SkillStatus.Certified, chosen.Status);
    }

    [Fact]
    public void Planner_OfferedNothing_WhenOnlyCandidatesOrWrongEnvironment()
    {
        var reg = new SkillRegistry();
        reg.RegisterCandidate("untried", "fix the build");
        Assert.Null(reg.PreferredFor("fix", "dotnet-9"));

        for (var i = 0; i < 3; i++) reg.RecordOutcome("linux-only", Verified(), "debian-12");
        reg.Get("linux-only")!.Purpose = "fix the build";
        Assert.Null(reg.PreferredFor("fix the build", "windows-11")); // environment coverage respected
    }

    [Fact]
    public void ExperimentalSkills_RequireSandbox()
    {
        var reg = new SkillRegistry();
        reg.RecordOutcome("new-thing", Verified(), "dotnet-9");
        var s = reg.Get("new-thing")!;
        Assert.Equal(SkillStatus.Experimental, s.Status);
        Assert.True(SkillRegistry.RequiresSandbox(s));

        for (var i = 0; i < 2; i++) reg.RecordOutcome("new-thing", Verified(), "dotnet-9");
        Assert.False(SkillRegistry.RequiresSandbox(reg.Get("new-thing")!)); // certified runs normally
    }

    [Fact]
    public void BlockedSkill_IsNeverOffered_AndIgnoresOutcomes()
    {
        var reg = new SkillRegistry();
        for (var i = 0; i < 3; i++) reg.RecordOutcome("risky", Verified(), "dotnet-9");
        reg.SetOperatorStatus("risky", SkillStatus.Blocked, "operator judgment");
        reg.RecordOutcome("risky", Verified(), "dotnet-9");
        Assert.Equal(SkillStatus.Blocked, reg.Get("risky")!.Status);
        Assert.Null(reg.PreferredFor("risky", "dotnet-9"));
    }
}
