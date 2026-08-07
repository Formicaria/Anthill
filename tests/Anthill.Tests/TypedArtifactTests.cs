using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The ants that hold structure now emit it. v3.8.21.
///
/// v3.8.20 bridged an ant's DECLARED artifacts into the store and found that the six core ants
/// declare only <c>AntArtifact("text")</c> — prose with a label. Three of them turned out to be
/// holding real structure they simply had no way to express:
///
///   FileAnt          the paths it read            -> file_set
///   WebResearchAnt   the SourceRecords it saved   -> source_set
///   the coder path   the parsed PatchSet          -> patch_set (emitted where the parse happens)
///
/// The other three — researcher, builder, verifier — produce prose synthesis and are deliberately
/// left untyped. Giving prose a schema name is the "two channels and the prose one wins" failure
/// ADR-004 rejects, and doing it to satisfy a checklist would be worse than leaving the gap visible.
/// </summary>
public class TypedArtifactTests
{
    /// <summary>
    /// <c>WithArtifact</c> APPENDS. The narrative artifact stays — it is what an operator reads —
    /// and the typed one joins it. Replacing would trade a human record for a machine one, when the
    /// whole design is that both exist and only one carries control meaning.
    /// </summary>
    [Fact]
    public void ATypedArtifact_JoinsTheNarrativeOneRatherThanReplacingIt()
    {
        var artifacts = new List<AntArtifact> { new("text", "researcher output", "prose") };
        var result = new AntExecutionResult
        {
            Success = true, StatusCode = "succeeded", Summary = "s", Artifacts = artifacts,
        };

        var typed = result with
        {
            Artifacts = result.Artifacts.Concat(new[] { new AntArtifact("file_set", "Files", "[]") }).ToList(),
        };

        Assert.Equal(2, typed.Artifacts.Count);
        Assert.Contains(typed.Artifacts, a => a.Kind == "text");
        Assert.Contains(typed.Artifacts, a => a.Kind == "file_set");
    }

    /// <summary>Every kind the newly-typed ants emit has to resolve, or the bridge silently drops it.</summary>
    [Theory]
    [InlineData("file_set", ArtifactSchemas.FileSet)]
    [InlineData("source_set", ArtifactSchemas.SourceSet)]
    [InlineData("patch_set", ArtifactSchemas.PatchSet)]
    public void TheNewlyTypedKinds_AllResolve(string antKind, string expected) =>
        Assert.Equal(expected, ArtifactSchemas.ForAntKind(antKind));

    /// <summary>
    /// The three that stay prose stay prose. This is the assertion that stops a future release from
    /// quietly "finishing the job" by mapping `text` to something — the gap is the honest state, and
    /// closing it means giving those ants structure, not giving them a label.
    /// </summary>
    [Fact]
    public void NarrativeOutput_StillHasNoSchema() =>
        Assert.Null(ArtifactSchemas.ForAntKind("text"));
}

/// <summary>
/// The verification framework's first production call site. v3.8.21.
///
/// <c>VerificationRunner</c>, four verifiers and a policy table existed and were tested from v2.12
/// and NOTHING CALLED THEM. A code patch was never checked against the policy that declared what a
/// code patch requires. These tests pin the wiring decision and the narrowing that came with it.
/// </summary>
public class LiveVerificationTests
{
    /// <summary>
    /// The runner produces one result per REQUIRED verifier, and each carries its own
    /// reproducibility. That is what makes them ADR-004 evidence rather than opinions: the
    /// deterministic flag comes from the verifier, not from the caller.
    /// </summary>
    [Fact]
    public void EachRequiredVerifier_ProducesAResultCarryingItsOwnDeterminism()
    {
        var runner = new Anthill.Core.Verification.VerificationRunner(new Anthill.Core.Verification.IVerifier[]
        {
            new StubVerifier("diff", deterministic: true, passes: true),
            new StubVerifier("build", deterministic: true, passes: true),
            new StubVerifier("security_policy", deterministic: false, passes: true),
        });

        var bundle = runner.Run(new Anthill.Core.Verification.VerificationRequest("code_patch", Path.GetTempPath()));

        Assert.Equal(3, bundle.Results.Count);
        Assert.True(bundle.HasDeterministicEvidence);
        Assert.Equal(2, bundle.Results.Count(r => r.Deterministic));
    }

    /// <summary>
    /// A patch that does not build cannot be promotable, which is the entire reason for wiring this
    /// up. Before v3.8.21 the policy said so and nothing enforced it.
    /// </summary>
    [Fact]
    public void AFailingBuild_BlocksPromotion()
    {
        var runner = new Anthill.Core.Verification.VerificationRunner(new Anthill.Core.Verification.IVerifier[]
        {
            new StubVerifier("diff", deterministic: true, passes: true),
            new StubVerifier("build", deterministic: true, passes: false),
            new StubVerifier("security_policy", deterministic: false, passes: true),
        });

        var bundle = runner.Run(new Anthill.Core.Verification.VerificationRequest("code_patch", Path.GetTempPath()));

        Assert.False(bundle.Promotable);
    }

    /// <summary>
    /// Semantic judgment alone never promotes. The rule predates this release; wiring the runner is
    /// what makes it apply to real patches for the first time.
    /// </summary>
    [Fact]
    public void NonDeterministicPassesAlone_DoNotPromote()
    {
        var runner = new Anthill.Core.Verification.VerificationRunner(new Anthill.Core.Verification.IVerifier[]
        {
            new StubVerifier("diff", deterministic: false, passes: true),
            new StubVerifier("build", deterministic: false, passes: true),
            new StubVerifier("security_policy", deterministic: false, passes: true),
        });

        var bundle = runner.Run(new Anthill.Core.Verification.VerificationRequest("code_patch", Path.GetTempPath()));

        Assert.False(bundle.HasDeterministicEvidence);
        Assert.False(bundle.Promotable);
        Assert.Contains(bundle.BlockedReasons, r => r.Contains("deterministic"));
    }

    /// <summary>
    /// The default policy must NOT drag in the full suite. Asserted here as well as in
    /// <c>VerificationFrameworkTests</c> because this is the file about the live wiring, and the
    /// wall-clock cost of getting this wrong lands on the Director thread.
    /// </summary>
    [Fact]
    public void TheLivePolicy_DoesNotRunTheWholeTestSuitePerPatch() =>
        Assert.DoesNotContain("test", Anthill.Core.Verification.VerificationPolicy.For("code_patch"));

    private sealed class StubVerifier : Anthill.Core.Verification.IVerifier
    {
        private readonly bool _passes;
        public StubVerifier(string name, bool deterministic, bool passes)
        {
            Name = name; Deterministic = deterministic; _passes = passes;
        }
        public string Name { get; }
        public bool Deterministic { get; }
        public Anthill.Core.Verification.VerificationResult Verify(
            Anthill.Core.Verification.VerificationRequest request) =>
            new(Name, _passes, Deterministic, _passes ? "ok" : "failed",
                new List<Anthill.Core.Verification.VerificationEvidence>());
    }
}
