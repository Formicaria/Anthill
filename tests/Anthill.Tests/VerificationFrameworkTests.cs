using Anthill.Core.Verification;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// NORTH_STAR Phase 4 success criteria under test: structural completion cannot create a verified
/// success; every promotable change has an evidence bundle; verification reruns identically;
/// failed verification blocks promotion; model confidence is never proof.
/// </summary>
public class VerificationFrameworkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_ver_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Ws()
    {
        Directory.CreateDirectory(_dir);
        return _dir;
    }

    // ---- Policy --------------------------------------------------------------------------------

    /// <summary>
    /// v3.8.21 — this test used to assert that <c>code_patch</c> requires test as well, and the
    /// policy was narrowed by operator decision when the framework got its first production call
    /// site. The reason is recorded in <c>VerificationPolicy</c>: <c>TestVerifier</c> runs the ENTIRE
    /// suite (<c>dotnet test -c Release</c>, 1200s cap) and <c>BuildVerifier</c> a full build at
    /// 600s, so requiring both meant up to half an hour per patch task — and a mission running
    /// during the suite would invoke the suite from inside itself.
    ///
    /// Asserted EXACTLY rather than with Contains, so the narrowing cannot drift further without
    /// this failing. Build stays because it is the check that matters: a patch that does not compile
    /// can no longer reach a verified outcome.
    /// </summary>
    [Fact]
    public void CodePatch_RequiresDiffBuildAndSecurity_ButNotTheFullSuite()
    {
        var req = VerificationPolicy.For("code_patch");

        Assert.Equal(new[] { "diff", "build", "security_policy" }, req);
        Assert.DoesNotContain("test", req);
    }

    /// <summary>
    /// And the stricter policy still exists for anyone who wants that trade. Pinned so the narrowing
    /// above is a CHOICE between two declared policies rather than a capability that quietly left.
    /// </summary>
    [Fact]
    public void TheFullPolicy_StillRequiresTheSuite() =>
        Assert.Equal(new[] { "diff", "build", "test", "security_policy" },
                     VerificationPolicy.For("code_patch_full"));

    [Fact]
    public void UnknownTaskType_StillRequiresPolicyScan_FailClosed()
        => Assert.Contains("security_policy", VerificationPolicy.For("something_new"));

    // ---- Diff verifier -------------------------------------------------------------------------

    [Fact]
    public void Diff_InScopeChange_Passes_WithHashes()
    {
        var r = new DiffVerifier().Verify(new VerificationRequest("code_patch", Ws(),
            ChangedPath: "docs/HOMELAB.md", NewContent: "new", OldContent: "old",
            ApprovedScope: new[] { "docs/" }));
        Assert.True(r.Passed);
        Assert.Contains(r.Evidence, e => e.Kind == "new_content_sha256");
        Assert.Contains(r.Evidence, e => e.Kind == "old_content_sha256");
    }

    [Fact]
    public void Diff_OutOfScopeChange_Fails()
    {
        var r = new DiffVerifier().Verify(new VerificationRequest("code_patch", Ws(),
            ChangedPath: "src/Anthill.Core/Queen.cs", NewContent: "x", ApprovedScope: new[] { "docs/" }));
        Assert.False(r.Passed);
        Assert.Contains("outside the approved scope", r.Summary);
    }

    [Fact]
    public void Diff_NoOpPatch_Fails()
    {
        var r = new DiffVerifier().Verify(new VerificationRequest("docs_patch", Ws(),
            ChangedPath: "README.md", NewContent: "same", OldContent: "same"));
        Assert.False(r.Passed);
        Assert.Contains("no-op", r.Summary);
    }

    // ---- Security verifier ---------------------------------------------------------------------

    [Fact]
    public void Security_SecretInContent_Fails()
    {
        var r = new SecurityPolicyVerifier().Verify(new VerificationRequest("code_patch", Ws(),
            ChangedPath: "src/app.cs", NewContent: "var k = \"x\"; password = 'hunter2secret'"));
        Assert.False(r.Passed);
        Assert.Contains(r.Evidence, e => e.Value == "secret_material");
    }

    [Fact]
    public void Security_CleanContent_Passes_WithRiskEvidence()
    {
        var r = new SecurityPolicyVerifier().Verify(new VerificationRequest("docs_patch", Ws(),
            ChangedPath: "docs/guide.md", NewContent: "# just documentation"));
        Assert.True(r.Passed);
        Assert.Contains(r.Evidence, e => e.Kind == "risk_level");
    }

    // ---- Artifact verifier ---------------------------------------------------------------------

    [Fact]
    public void Artifact_PresentFiles_PassWithHashes_MissingFail()
    {
        var ws = Ws();
        File.WriteAllText(Path.Combine(ws, "report.md"), "content");
        var ok = new ArtifactVerifier().Verify(new VerificationRequest("artifact_production", ws,
            RequiredArtifacts: new[] { "report.md" }));
        Assert.True(ok.Passed);
        Assert.Contains(ok.Evidence, e => e.Kind == "file_hash");

        var bad = new ArtifactVerifier().Verify(new VerificationRequest("artifact_production", ws,
            RequiredArtifacts: new[] { "report.md", "missing.md" }));
        Assert.False(bad.Passed);
        Assert.Contains("missing.md", bad.Summary);
    }

    [Fact]
    public void Artifact_NoneDeclared_CannotPass()
        => Assert.False(new ArtifactVerifier().Verify(new VerificationRequest("artifact_production", Ws())).Passed);

    // ---- Bundle + promotion rule -----------------------------------------------------------------

    private sealed record FakeVerifier(string Name, bool Passes, bool Deterministic = true) : IVerifier
    {
        public VerificationResult Verify(VerificationRequest r) =>
            new(Name, Passes, Deterministic, Passes ? "ok" : "nope", new List<VerificationEvidence> { new("k", "v") });
    }

    [Fact]
    public void AllRequiredPass_BundleIsPromotable_AndExplains()
    {
        var runner = new VerificationRunner(new IVerifier[]
        {
            new FakeVerifier("diff", true), new FakeVerifier("security_policy", true),
        });
        var bundle = runner.Run(new VerificationRequest("docs_patch", Ws(), ChangedPath: "docs/a.md"));
        Assert.True(bundle.Promotable);
        Assert.True(bundle.HasDeterministicEvidence);
        Assert.Contains("VERIFIED", bundle.Explain());
    }

    [Fact]
    public void OneRequiredFails_BundleNotPromotable()
    {
        var runner = new VerificationRunner(new IVerifier[]
        {
            new FakeVerifier("diff", true), new FakeVerifier("security_policy", false),
        });
        var bundle = runner.Run(new VerificationRequest("docs_patch", Ws()));
        Assert.False(bundle.Promotable);
        Assert.Contains("security_policy=FAIL", bundle.Explain());
    }

    [Fact]
    public void MissingVerifier_IsFailure_NotPass()
    {
        var runner = new VerificationRunner(new IVerifier[] { new FakeVerifier("diff", true) });
        var bundle = runner.Run(new VerificationRequest("docs_patch", Ws()));
        Assert.False(bundle.Promotable);
        Assert.Contains(bundle.BlockedReasons, b => b.Contains("security_policy"));
        Assert.Contains("security_policy=MISSING", bundle.Explain());
    }

    [Fact]
    public void SemanticOnlyEvidence_CannotVerify_ModelConfidenceIsNeverProof()
    {
        var runner = new VerificationRunner(new IVerifier[]
        {
            new FakeVerifier("diff", true, Deterministic: false),
            new FakeVerifier("security_policy", true, Deterministic: false),
        });
        var bundle = runner.Run(new VerificationRequest("docs_patch", Ws()));
        Assert.False(bundle.HasDeterministicEvidence);
        Assert.False(bundle.Promotable); // blocked despite every required verifier "passing"
        Assert.Contains(bundle.BlockedReasons, b => b.Contains("semantic judgment alone"));
    }

    [Fact]
    public void FaultingVerifier_CountsAsFailure_NeverCrashesTheRun()
    {
        var runner = new VerificationRunner(new IVerifier[]
        {
            new ThrowingVerifier(), new FakeVerifier("security_policy", true),
        });
        var bundle = runner.Run(new VerificationRequest("docs_patch", Ws()));
        Assert.False(bundle.Promotable);
        Assert.Contains(bundle.Results, r => r.Verifier == "diff" && !r.Passed && r.Summary.Contains("faulted"));
    }

    private sealed class ThrowingVerifier : IVerifier
    {
        public string Name => "diff";
        public bool Deterministic => true;
        public VerificationResult Verify(VerificationRequest r) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Verification_IsRerunnable_SameInputSameResult()
    {
        var req = new VerificationRequest("docs_patch", Ws(), ChangedPath: "docs/a.md", NewContent: "hello");
        var a = new VerificationRunner().Run(req);
        var b = new VerificationRunner().Run(req);
        Assert.Equal(a.Explain(), b.Explain());
    }
}
