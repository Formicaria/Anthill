using Anthill.Core.Verification;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A documentation patch is verified as documentation. v0.3.8.75.
///
/// THE DEFECT, and it is an escape hatch that was built and never reachable. `docs_patch` has
/// required `{diff, security_policy}` — deliberately no build — since the policy table was written.
/// Nothing ever selected it. The planner emits `patch_proposal` for every patch, docs and code
/// alike; the alias maps that to `code_patch`; `code_patch` requires `build`. So a README-only
/// change has always been compiled with `dotnet build -c Release`, on the Director thread, before it
/// could be called verified.
///
/// v3.8.21's note in that same table worries at length about precisely this cost — "up to half an
/// hour of wall clock per code-patch task, serially, on the Director thread" — and removed `test`
/// from the default to contain it. It did not notice that the `docs_patch` row sitting three lines
/// below was unreachable, which would have contained it further and for free.
///
/// THE TASK TYPE CANNOT TELL THEM APART. `coder.docs_coder` and `coder.ui_coder` both emit
/// `patch_proposal`. The patch's own paths can, and they are the honest source: what a change
/// touches is a fact about the change rather than a claim about it.
///
/// WHAT THIS DOES NOT DO, because it is the whole risk of the change: it does not weaken whether a
/// reproducible no is final. `diff` and `security_policy` still run on every docs patch, the soldier
/// is still policy-inserted on the patch set's existence, and a docs patch that trips either is
/// blocked exactly as before. This narrows WHICH deterministic build runs and nothing else.
/// </summary>
public class DocsPatchPolicyTests
{
    // -----------------------------------------------------------------------------------------------
    // The narrowing
    // -----------------------------------------------------------------------------------------------

    /// <summary>The defect, as the fact that motivates the fix: without paths, every patch is code.</summary>
    [Fact]
    public void WithoutPaths_APatchProposalIsStillACodePatch()
    {
        Assert.Equal("code_patch", VerificationPolicy.Canonical("patch_proposal"));
        Assert.Contains("build", VerificationPolicy.For("patch_proposal"));
    }

    [Theory]
    [InlineData("docs/COLONY-NOTE.md")]
    [InlineData("README.md")]
    [InlineData("CHANGELOG.md")]
    [InlineData("docs/adr/ADR-004.md")]
    public void APatchTouchingOnlyDocumentation_IsADocsPatch(string path)
    {
        Assert.Equal("docs_patch", VerificationPolicy.Canonical("patch_proposal", new[] { path }));
        Assert.DoesNotContain("build", VerificationPolicy.For(
            VerificationPolicy.Canonical("patch_proposal", new[] { path })));
    }

    [Fact]
    public void ADocsPatch_StillRunsDiffAndTheSecurityScan()
    {
        var required = VerificationPolicy.For(
            VerificationPolicy.Canonical("patch_proposal", new[] { "docs/NOTE.md" }));

        Assert.Contains("diff", required);
        Assert.Contains("security_policy", required);
    }

    // -----------------------------------------------------------------------------------------------
    // Conservative in the direction that matters
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// ONE code file makes the whole set a code patch. A patch set applies as a UNIT and is exactly
    /// as dangerous as its most dangerous member, so the classification is over the set — judging
    /// each proposal separately would let the `.md` files in a mixed set be verified under the
    /// lighter policy while the `.cs` file rode along.
    /// </summary>
    [Theory]
    [InlineData("docs/NOTE.md", "src/Anthill.Core/Queen.cs")]
    [InlineData("README.md", "src/Anthill.UI/app.js")]
    [InlineData("CHANGELOG.md", "Directory.Build.props")]
    [InlineData("docs/NOTE.md", "docs/notes.txt")]          // .txt is not a documentation path here
    [InlineData("docs/NOTE.md", ".github/workflows/ci.yml")]
    public void OneNonDocumentationPath_MakesTheWholeSetACodePatch(string docs, string other)
    {
        Assert.Equal("code_patch", VerificationPolicy.Canonical("patch_proposal", new[] { docs, other }));
        Assert.Contains("build", VerificationPolicy.For(
            VerificationPolicy.Canonical("patch_proposal", new[] { docs, other })));
    }

    /// <summary>
    /// An empty set is NOT documentation. "Nothing to look at" must never select the lighter policy —
    /// that is the shape of every fail-open defect this repository has found.
    /// </summary>
    [Fact]
    public void AnEmptySet_IsNotADocsPatch()
    {
        Assert.Equal("code_patch", VerificationPolicy.Canonical("patch_proposal", System.Array.Empty<string>()));
        Assert.Equal("code_patch", VerificationPolicy.Canonical("patch_proposal", null));
    }

    /// <summary>
    /// A path that only LOOKS like documentation does not qualify. Traversal, a nested `.md` outside
    /// `docs/`, and a lookalike directory are all code paths — the predicate is anchored at both
    /// ends for this reason.
    /// </summary>
    [Theory]
    [InlineData("../secrets/notes.md")]
    [InlineData("docs-secret/notes.md")]
    [InlineData("src/docs/notes.md")]
    [InlineData("notes.md")]
    [InlineData("docs/notes.md.cs")]
    public void APathThatMerelyResemblesDocumentation_DoesNot(string path) =>
        Assert.Equal("code_patch", VerificationPolicy.Canonical("patch_proposal", new[] { path }));

    /// <summary>
    /// An EXPLICIT policy key is never softened by paths. A task that says `code_patch` means it,
    /// and someone naming a policy deliberately outranks an inference from file names.
    /// </summary>
    [Fact]
    public void AnExplicitPolicyKey_IsNeverNarrowedByPaths()
    {
        Assert.Equal("code_patch", VerificationPolicy.Canonical("code_patch", new[] { "docs/NOTE.md" }));
        Assert.Contains("build", VerificationPolicy.For(
            VerificationPolicy.Canonical("code_patch", new[] { "docs/NOTE.md" })));
    }

    /// <summary>
    /// And the narrowing only ever goes `code_patch` → `docs_patch`. No other policy is inferred
    /// from paths, so a future table entry cannot be reached by accident.
    /// </summary>
    [Theory]
    [InlineData("artifact_production")]
    [InlineData("config_change")]
    [InlineData("some_unknown_type")]
    public void NoOtherPolicy_IsInferredFromPaths(string taskType) =>
        Assert.Equal(VerificationPolicy.Canonical(taskType),
                     VerificationPolicy.Canonical(taskType, new[] { "docs/NOTE.md" }));

    // -----------------------------------------------------------------------------------------------
    // One definition of "documentation"
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The scribe's documentation-only restriction and this policy read the SAME predicate. Two
    /// copies of "what counts as docs" would be two answers to a question the security boundary
    /// asks — and they would drift in the direction where one of them is more permissive.
    /// </summary>
    [Fact]
    public void TheScribeAndThePolicy_ShareOneDefinitionOfDocumentation()
    {
        var scribe = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Agents", "SpecialistAnts.cs")));

        Assert.Contains("VerificationPolicy.DocsPath", scribe);
        // …and does not keep its own copy of the pattern.
        Assert.DoesNotContain("docs/[\\w./\\-]+\\.md|README", scribe);
    }

    /// <summary>
    /// The call site classifies the WHOLE SET. Asserted on the source because the property is about
    /// which collection is passed, and a per-proposal call would still compile and still pass every
    /// test above while reintroducing the mixed-set hole.
    /// </summary>
    [Fact]
    public void TheCallSite_ClassifiesTheSet_NotEachProposal()
    {
        var execution = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));

        Assert.Contains("var setPaths = patchSet.Proposals.Select(p => p.FilePath).ToList();", execution);
        Assert.Contains("VerificationPolicy.Canonical(task.TaskType, setPaths)", execution);
    }
}
