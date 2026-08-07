using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Outcomes;
using Anthill.Core.Verification;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

// There are TWO types named VerificationPolicy: Anthill.Core.Configuration.VerificationPolicy (the
// run's objective-verification settings) and Anthill.Core.Verification.VerificationPolicy (the table
// of which verifiers a task type requires). This file needs AnthillRuntime from the first namespace
// and the policy table from the second, so the name has to be disambiguated. Aliased once here rather
// than qualified at fifteen call sites — and worth noting that two types this close in name, one of
// which decides whether verification runs and the other what it checks, is its own small hazard.
using VerificationPolicy = Anthill.Core.Verification.VerificationPolicy;

namespace Anthill.Tests;

/// <summary>
/// v3.8.22 — the regression suite for a defect v3.8.21 shipped.
///
/// v3.8.21 gave <c>VerificationRunner</c> its first production call site and reported it as done. It
/// was not. Three things were wrong at once, and each on its own was enough to make patch
/// verification decorative:
///
///   1. The planner emits task type <c>patch_proposal</c>. The policy table is keyed <c>code_patch</c>.
///      Nothing mapped them, so <c>VerificationPolicy.For</c> hit its unknown-type fallback and ran
///      <c>security_policy</c> ALONE — the two deterministic verifiers, diff and build, never ran on
///      a single real patch.
///   2. The request carried no <c>ChangedPath</c> and no content, so even had diff run it would have
///      answered "no changed path supplied — nothing to verify" and failed.
///   3. <c>bundle.Promotable</c> was written to an event row and read by nothing.
///
/// The tests that were supposed to cover this passed <c>"code_patch"</c> literally — a task type
/// production never produces. That is the specific mistake this file exists to make impossible:
/// every test below is keyed to a string the PLANNER emits, not one the policy table declares.
/// </summary>
public class VerificationPolicyResolutionTests
{
    /// <summary>
    /// The load-bearing assertion. <c>patch_proposal</c> is what
    /// <c>Planner</c>'s prompt instructs the model to emit and what its deterministic fallback plan
    /// hard-codes; if it ever stops resolving to the code-patch policy, every patch silently drops
    /// back to policy-scan-only and nothing else in the suite notices.
    /// </summary>
    [Theory]
    [InlineData("patch_proposal")]
    [InlineData("patch")]
    [InlineData("code_change")]
    public void TheTaskTypesThePlannerActuallyEmits_ResolveToTheCodePatchPolicy(string emitted)
    {
        Assert.Equal("code_patch", VerificationPolicy.Canonical(emitted));

        var required = VerificationPolicy.For(emitted);
        Assert.Contains("build", required);
        Assert.Contains("diff", required);
        Assert.Contains("security_policy", required);
    }

    /// <summary>
    /// The exact shape of the v3.8.21 bug, pinned as a negative: policy-scan-alone is the fallback for
    /// an UNKNOWN type, and a patch proposal must never land there again.
    /// </summary>
    [Fact]
    public void APatchProposal_DoesNotFallThroughToPolicyScanAlone()
    {
        var required = VerificationPolicy.For("patch_proposal");
        Assert.NotEqual(new[] { "security_policy" }, required.ToArray());
        Assert.True(VerificationPolicy.IsKnown("patch_proposal"));
    }

    /// <summary>
    /// An explicit table key always beats an alias. Aliases redirect names the table does NOT define;
    /// one that could redirect a name it DOES define would let a policy be silently replaced by
    /// another policy, which is worse than the gap it was added to close.
    /// </summary>
    [Fact]
    public void AnExplicitPolicyKey_IsNeverRedirectedByAnAlias()
    {
        Assert.Equal("code_patch_full", VerificationPolicy.Canonical("code_patch_full"));
        Assert.Contains("test", VerificationPolicy.For("code_patch_full"));
    }

    /// <summary>A type nobody has heard of still fails closed to the minimum, exactly as before.</summary>
    [Fact]
    public void AGenuinelyUnknownTaskType_StillFallsBackToPolicyScan()
    {
        Assert.Equal(new[] { "security_policy" }, VerificationPolicy.For("interpretive_dance").ToArray());
        Assert.False(VerificationPolicy.IsKnown("interpretive_dance"));
    }
}

/// <summary>
/// Per-proposal verification, and the workspace-scope optimisation that makes it affordable.
/// </summary>
public class PerProposalVerificationTests
{
    /// <summary>
    /// A patch set is not one change. Verifying it with a single request meant at most one proposal
    /// was examined and the others were verified by implication.
    /// </summary>
    [Fact]
    public void EveryProposalInASet_GetsItsOwnBundle()
    {
        var runner = new VerificationRunner(new IVerifier[] { new CountingVerifier("diff", workspaceScoped: false) });
        var requests = new[]
        {
            new VerificationRequest("docs_patch", Path.GetTempPath(), ChangedPath: "docs/a.md", NewContent: "a"),
            new VerificationRequest("docs_patch", Path.GetTempPath(), ChangedPath: "docs/b.md", NewContent: "b"),
            new VerificationRequest("docs_patch", Path.GetTempPath(), ChangedPath: "docs/c.md", NewContent: "c"),
        };

        var bundles = runner.RunForEach(requests);

        Assert.Equal(3, bundles.Count);
    }

    /// <summary>
    /// A change-dependent verifier runs once PER PROPOSAL. If this ever drops to one call the
    /// per-proposal guarantee is gone and only the first change is really being checked.
    /// </summary>
    [Fact]
    public void AChangeDependentVerifier_RunsOncePerProposal()
    {
        var diff = new CountingVerifier("diff", workspaceScoped: false);
        var runner = new VerificationRunner(new IVerifier[] { diff, new CountingVerifier("security_policy", false) });

        runner.RunForEach(Requests(4));

        Assert.Equal(4, diff.Calls);
    }

    /// <summary>
    /// The whole reason per-proposal verification is affordable. <c>dotnet build</c> is capped at 600
    /// seconds and <c>dotnet test</c> at 1200; running either once per proposal would put a five-file
    /// patch set into the tens of minutes, serially, on the Director thread.
    /// </summary>
    [Fact]
    public void AWorkspaceScopedVerifier_RunsOnceForTheWholeSet()
    {
        var build = new CountingVerifier("build", workspaceScoped: true);
        var runner = new VerificationRunner(new IVerifier[]
        {
            new CountingVerifier("diff", false), build, new CountingVerifier("security_policy", false),
        });

        var bundles = runner.RunForEach(Requests(5, "code_patch"));

        Assert.Equal(1, build.Calls);
        // ...and every bundle still carries the verdict, so no proposal is left unjudged by it.
        Assert.All(bundles, b => Assert.Contains(b.Results, r => r.Verifier == "build"));
    }

    /// <summary>
    /// The two real workspace-scoped verifiers declare themselves so. This is the assertion that
    /// catches someone adding a request-dependent check to BuildVerifier without noticing its result
    /// is now being shared across proposals it never examined.
    /// </summary>
    [Fact]
    public void BuildAndTest_AreTheWorkspaceScopedVerifiers()
    {
        // Read through the INTERFACE deliberately. WorkspaceScoped is a default interface member, so
        // for the two verifiers that do not override it the property exists only on IVerifier and
        // `new DiffVerifier().WorkspaceScoped` would not compile at all.
        Assert.True(((IVerifier)new BuildVerifier()).WorkspaceScoped);
        Assert.True(((IVerifier)new TestVerifier()).WorkspaceScoped);
        Assert.False(((IVerifier)new DiffVerifier()).WorkspaceScoped);
        Assert.False(((IVerifier)new SecurityPolicyVerifier()).WorkspaceScoped);
    }

    /// <summary>
    /// The default is the SLOW answer, never the wrong one: a verifier that has not declared a scope
    /// is treated as change-dependent, so it runs per proposal rather than having one verdict shared
    /// across changes it never examined.
    /// </summary>
    [Fact]
    public void AVerifierThatDeclaresNothing_IsTreatedAsChangeDependent() =>
        Assert.False(((IVerifier)new SilentVerifier()).WorkspaceScoped);

    /// <summary>Declares Name, Deterministic and Verify — and says nothing about scope.</summary>
    private sealed class SilentVerifier : IVerifier
    {
        public string Name => "silent";
        public bool Deterministic => true;
        public VerificationResult Verify(VerificationRequest request) =>
            new(Name, true, true, "ok", new List<VerificationEvidence>());
    }

    /// <summary>
    /// The diff verifier's actual complaint in v3.8.21, pinned. This is what every real patch got
    /// back once the policy was fixed but before the content was supplied — proof that fixing the
    /// task-type mapping alone would have turned a silent pass into a universal failure.
    /// </summary>
    [Fact]
    public void ADiffRequestWithNoChangedPath_Fails()
    {
        var result = new DiffVerifier().Verify(new VerificationRequest("code_patch", Path.GetTempPath()));

        Assert.False(result.Passed);
        Assert.Contains("no changed path", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>And with the content the call site now supplies, it has something to verify.</summary>
    [Fact]
    public void ADiffRequestCarryingTheProposal_HasSomethingToVerify()
    {
        var result = new DiffVerifier().Verify(new VerificationRequest(
            "code_patch", Path.GetTempPath(), ChangedPath: "src/Anthill.Core/Thing.cs",
            NewContent: "new", OldContent: "old"));

        Assert.True(result.Passed);
        Assert.Contains(result.Evidence, e => e.Kind == "new_content_sha256");
    }

    private static VerificationRequest[] Requests(int n, string taskType = "docs_patch") =>
        Enumerable.Range(0, n).Select(i => new VerificationRequest(
            taskType, Path.GetTempPath(), ChangedPath: $"docs/f{i}.md", NewContent: $"c{i}")).ToArray();

    private sealed class CountingVerifier : IVerifier
    {
        public CountingVerifier(string name, bool workspaceScoped)
        {
            Name = name; WorkspaceScoped = workspaceScoped;
        }
        public string Name { get; }
        public bool Deterministic => true;
        public bool WorkspaceScoped { get; }
        public int Calls { get; private set; }
        public VerificationResult Verify(VerificationRequest request)
        {
            Calls++;
            return new(Name, true, true, "ok", new List<VerificationEvidence>());
        }
    }
}

/// <summary>
/// The consequence layer. Before v3.8.22 both signals below were computed, written to an event row,
/// and read by nothing — so a patch that failed the build verifier and a patch the policy engine
/// blocked could each reach <c>completed_verified</c>.
/// </summary>
public class DeterministicBlockTests : IDisposable
{
    private readonly bool _objectiveVerificationWas = AnthillRuntime.EnableObjectiveVerification;
    public void Dispose() => AnthillRuntime.EnableObjectiveVerification = _objectiveVerificationWas;

    private static MissionEvaluation Evaluate(Mission mission) =>
        MissionEvaluator.Evaluate(mission, stopReason: null, patchProposalCount: 0,
            Anthill.Core.Common.MissionConstraints.Parse(mission.Goal),
            AnthillRuntime.EnableObjectiveVerification);

    private static DomainTask Verifier() => new()
    {
        Title = "Verify", AssignedAnt = "verifier", TaskType = "verification", Status = TaskStatus.Complete,
        Result = "Verification Passed\nReasoning: checked.",
    };

    private static DomainTask Coder(string? block = null) => new()
    {
        Title = "Propose patch", AssignedAnt = "coder", TaskType = "patch_proposal",
        Status = TaskStatus.Complete, Critical = true, Result = "proposed", DeterministicBlock = block,
    };

    private static Mission MissionWith(params DomainTask[] tasks)
    {
        var m = new Mission { Goal = "change a file", Status = MissionStatus.Complete };
        m.Tasks.AddRange(tasks);
        return m;
    }

    /// <summary>The baseline row: nothing blocked, so the pre-existing behaviour is untouched.</summary>
    [Fact]
    public void NoBlock_StillReachesCompletedVerified()
    {
        AnthillRuntime.EnableObjectiveVerification = false;
        var e = Evaluate(MissionWith(Coder(), Verifier()));

        Assert.Equal(MissionOutcome.CompletedVerified, e.OutcomeCode);
    }

    /// <summary>
    /// The headline gate. A patch that does not build cannot be a verified success, even with a
    /// passing verifier task — because the verifier is a model reading prose and the build verifier
    /// is a compiler.
    /// </summary>
    [Fact]
    public void ANonPromotablePatchSet_CannotReachCompletedVerified()
    {
        AnthillRuntime.EnableObjectiveVerification = false;
        var e = Evaluate(MissionWith(Coder(block: "patch set p1: 1 of 1 proposal(s) not promotable — build=FAIL"), Verifier()));

        Assert.Equal(MissionOutcome.CompletedUnverified, e.OutcomeCode);
        Assert.False(e.IsPositive);
    }

    /// <summary>A soldier block cannot be overridden by a passing verifier — its own summary said so.</summary>
    [Fact]
    public void ASoldierBlock_CannotBeOverriddenByAPassingVerifier()
    {
        AnthillRuntime.EnableObjectiveVerification = false;
        var e = Evaluate(MissionWith(Coder(block: "policy review blocked: blocked_path_security"), Verifier()));

        Assert.Equal(MissionOutcome.CompletedUnverified, e.OutcomeCode);
    }

    /// <summary>The operator has to be able to see WHY, or a demotion is indistinguishable from a bug.</summary>
    [Fact]
    public void TheReason_ReachesTheExplanation()
    {
        AnthillRuntime.EnableObjectiveVerification = false;
        var e = Evaluate(MissionWith(Coder(block: "policy review blocked: blocked_path_security"), Verifier()));

        Assert.Contains("blocked_path_security", e.Explanation);
    }
}

/// <summary>
/// The soldier's end of the same wire. Its summary has claimed "deterministic block, not overridable"
/// since v2.19.0 while emitting a list of bare rule-id strings that nothing recognised as a block.
/// </summary>
public class SoldierBlockMarkerTests
{
    private static (Mission mission, DomainTask task) Fixture(string description)
    {
        var mission = new Mission { Goal = "review a patch", Status = MissionStatus.Running };
        var task = new DomainTask
        {
            Title = "Security review", AssignedAnt = "soldier",
            TaskType = "security_review", Description = description,
        };
        mission.Tasks.Add(task);
        return (mission, task);
    }

    /// <summary>A blocking rule match leads the warnings with the marker the execution path reads.</summary>
    [Fact]
    public void ABlockingFinding_EmitsTheMarker()
    {
        var (mission, task) = Fixture("modify .github/workflows/ci.yml to skip tests");

        var result = new SoldierAnt().Execute(task, mission);

        Assert.Contains(SoldierAnt.SoldierBlockMarker, result.Warnings);
        Assert.Equal("succeeded_with_warnings", result.StatusCode);
    }

    /// <summary>
    /// A clean review emits NO marker — and no bare warnings either. The marker is what makes a block
    /// a block, so an advisory finding must never be able to masquerade as one by merely existing.
    /// </summary>
    [Fact]
    public void ACleanReview_EmitsNoMarker()
    {
        var (mission, task) = Fixture("update the README wording");

        var result = new SoldierAnt().Execute(task, mission);

        Assert.DoesNotContain(SoldierAnt.SoldierBlockMarker, result.Warnings);
        Assert.Equal("succeeded", result.StatusCode);
    }

    /// <summary>The rule ids survive alongside the marker — the operator needs to know WHICH rule.</summary>
    [Fact]
    public void TheBlockingRuleIds_SurviveAlongsideTheMarker()
    {
        var (mission, task) = Fixture("modify .github/workflows/ci.yml to skip tests");

        var result = new SoldierAnt().Execute(task, mission);

        Assert.Contains(result.Warnings, w => w == "blocked_path_ci");
    }
}
