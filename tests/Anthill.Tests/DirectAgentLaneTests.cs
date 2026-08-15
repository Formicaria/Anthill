using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.58 — THE DIRECT AGENT LANE IS GONE, and these are the detectors that keep it gone.
///
/// WHAT THIS FILE USED TO TEST, and why the change is an inversion rather than a deletion. In
/// v0.3.8.53 the conversation lane could write to the operator's live tree, so the audit's
/// fail-closed shape was to CAPTURE what it wrote: every writing run became one canonical
/// <c>direct_change</c> artifact — base revision, files, bounded diffs, commit state — marked
/// unverified and structurally barred from positive memory. Those tests ran the real sweep over a
/// real temporary repository and passed, and the guarantee they established was real.
///
/// It was also the wrong guarantee, and its own existence said so. A lane whose output has to be
/// quarantined as "unverified work of unknown provenance" is a lane doing work outside the colony.
/// The artifact was the receipt. v0.3.8.52's field report — "did not auto commit" — is the same
/// fact stated as a complaint: the lane wrote files, and the commit hook, which rode the patch
/// pipeline, had nothing to fire on because no patch had ever existed.
///
/// v0.3.8.57 addressed this twice and missed twice. It refused an autonomous coding agent for the
/// conversation route, which changed WHO could do the work; and it rewrote the chat prompt to say
/// "you have no tools in this conversation and you change nothing", which changed what the model
/// was TOLD. Neither touched the grant. That lived in
/// <c>AgentAccessScope.Enter(..., confinedWorkspace: false)</c> — the operator's approval policy,
/// handed to the provider, standing in a real directory — and it outlived both fixes. Prose as a
/// control channel, in the release whose stated purpose was removing prose as a control channel.
///
/// So the lane is deleted, not narrowed. Every operator message is a mission. What remains here is
/// the set of source-level detectors that make its return visible, in the
/// CrossBoundaryAgreementTests tradition: a structural guarantee has to be checked structurally,
/// because a lane like this comes back as a small helpful convenience and not as a decision.
/// </summary>
public class DirectAgentLaneTests
{
    private static string Runner() => File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
        "src", "Anthill.Core", "Conversations", "ConversationRunner.cs"));

    /// <summary>
    /// THE GRANT IS GONE. The conversation may not enter an agent access scope at all — confined or
    /// otherwise — because it dispatches no agent.
    ///
    /// This is the assertion that would have failed in v0.3.8.57, when the prompt said the lane had
    /// no tools and this call was still four lines away. Asserting on the prompt text is how that
    /// release convinced itself; asserting on the scope is what would have caught it.
    /// </summary>
    [Fact]
    public void TheConversationLane_EntersNoAgentAccessScope() =>
        Assert.DoesNotContain("AgentAccessScope", SourceText.CodeOnly(Runner()));

    /// <summary>
    /// UNCONFINED ACCESS EXISTS NOWHERE. The mission path enters a scope, deliberately, and it is
    /// always confined: mission work stands in a disposable sandbox or worktree, never the live
    /// checkout. `confinedWorkspace: false` was the whole of the direct lane's authority, so its
    /// absence across the entire source tree is the durable form of this guarantee — narrower than
    /// naming the one file that used to hold it, and it stays true if the lane returns elsewhere.
    /// </summary>
    [Fact]
    public void NoCallSiteAnywhere_RequestsUnconfinedAgentAccess()
    {
        var offenders = new List<string>();
        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(SourceText.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
             || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            // The scope's own DECLARATION carries the parameter and its default; only CALLS count.
            if (path.EndsWith("AgentAccessScope.cs", StringComparison.Ordinal)) continue;
            if (SourceText.CodeOnly(File.ReadAllText(path))
                .Contains("confinedWorkspace: false", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(path));
        }

        Assert.True(offenders.Count == 0,
            "these call sites grant an agent access to a NON-disposable tree: "
          + string.Join(", ", offenders)
          + ". That is the direct lane, whatever it is called now — work reaching the operator's "
          + "files without a plan, a review, a test or a verdict.");
    }

    /// <summary>
    /// NOTHING SWEEPS OR COMMITS FROM A CONVERSATION. The sweep existed only to catch what the lane
    /// wrote; a new one would be evidence the lane is back, and it would arrive looking helpful.
    /// </summary>
    [Fact]
    public void TheConversationLane_CommitsNothingAndCapturesNoDirectChange()
    {
        var runner = SourceText.CodeOnly(Runner());

        Assert.DoesNotContain("direct_change", runner);
        Assert.DoesNotContain("RepoOps.Commit", runner);
        Assert.DoesNotContain("DirectEditSweep", runner);
    }

    /// <summary>
    /// And the original structural guarantee is KEPT, because it costs nothing to keep and it
    /// guards the consequence rather than the mechanism: the conversation lane must never grow a
    /// pheromone or learning call, and the learning lane must never grow a `direct_change`
    /// consumer that could turn unverified output into positive reinforcement.
    ///
    /// The prose exemption the v0.3.8.53 version needed — a mention inside a comment — is gone with
    /// the comment, so this now reads the blanked source and needs no exemption at all. A guard
    /// with a carve-out is one refactor away from being a guard with a hole.
    /// </summary>
    [Fact]
    public void TheConversationLane_HasNoPathIntoLearning_AndLearningHasNoPathIntoDirectChanges()
    {
        var runner = SourceText.CodeOnly(Runner());
        Assert.DoesNotContain("LearningRecorder", runner);
        Assert.DoesNotContain("Pheromone", runner);
        Assert.DoesNotContain("ReinforceTrail", runner);

        var learning = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "LearningRecorder.cs")));
        Assert.DoesNotContain("direct_change", learning);
    }
}
