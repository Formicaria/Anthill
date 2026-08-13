using Anthill.Core.Configuration;
using Anthill.Core.Conversations;
using Anthill.Core.Memory;
using Anthill.Core.Projects;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.53 — audit Phase 7: the direct coding-agent lane may not be a second, untracked source
/// of "verified" work. The sanctioned fail-closed shape: every writing run's fresh changes are
/// captured as ONE canonical <c>direct_change</c> artifact — base revision, files, bounded diffs,
/// commit state — explicitly marked unverified, never fed to positive memory. These tests run the
/// REAL ConversationRunner sweep over a REAL temporary git repository; only the reasoning reply
/// is faked, at its documented boundary (the ask function), which is what edits the files exactly
/// as a real agent CLI would — by writing them.
/// </summary>
public class DirectAgentLaneTests : IDisposable
{
    private readonly string _dir;
    private readonly string _repo;
    private readonly SqliteMemory _memory;

    public DirectAgentLaneTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-directlane-" + Guid.NewGuid().ToString("N")[..10]);
        _repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(_repo);
        _memory = new SqliteMemory(Path.Combine(_dir, "memory.db"));
        _memory.EnsureSystemMission(AnthillRuntime.SystemApiMissionId, "System API events");
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static bool GitAvailable => RepoOps.Git(Path.GetTempPath(), "--version").Ok;

    /// <summary>A project-linked conversation whose working directory is the temp repo.</summary>
    private Conversation Chat(EscalationPolicy policy)
    {
        var project = new Project { Id = "p1", Name = "Lane", Path = _repo };
        _memory.SaveProject(project);
        var conversation = new Conversation
        {
            Id = "c1", Role = "queen", Policy = policy, ProjectId = "p1",
            PolicySetBy = policy == EscalationPolicy.Ask ? null : "tester",
            PolicySetAt = policy == EscalationPolicy.Ask ? null : DateTime.UtcNow,
        };
        _memory.SaveConversation(conversation);
        return conversation;
    }

    /// <summary>A runner whose "agent" edits a file directly — the direct lane's defining act.</summary>
    private ConversationRunner EditingRunner(string fileName, string content) =>
        new(_memory, (_, onCreated, _) => { onCreated("m-x"); return "m-x"; },
            (_, _) =>
            {
                File.WriteAllText(Path.Combine(_repo, fileName), content);
                return new ConversationReply(true, "edited it directly", "agent:claude-code", "Claude Code", null);
            });

    private void SeedRepo()
    {
        Assert.True(RepoOps.Init(_repo).Ok);
        File.WriteAllText(Path.Combine(_repo, "seed.txt"), "seed\n");
        Assert.True(RepoOps.Commit(_repo, new[] { "seed.txt" }, "seed", "tester").Ok);
    }

    private Artifact? DirectChange() =>
        ((IArtifactStore)_memory).ForMission(AnthillRuntime.SystemApiMissionId, "direct_change")
            .FirstOrDefault();

    [Fact]
    public void Bypass_DirectEdit_IsCommitted_AndCapturedAsUnverifiedArtifact()
    {
        if (!GitAvailable) return;
        SeedRepo();
        var baseHead = RepoOps.Head(_repo);
        Assert.NotNull(baseHead);

        EditingRunner("colony.txt", "the colony was here\n")
            .Run(Chat(EscalationPolicy.Bypass), "leave a note");

        // Committed: the tree is clean again and HEAD moved.
        var state = RepoOps.Describe(_repo);
        Assert.Equal(0, state.DirtyCount);
        Assert.NotEqual(baseHead, RepoOps.Head(_repo));

        // And the canonical artifact names everything the audit requires.
        var artifact = DirectChange();
        Assert.NotNull(artifact);
        Assert.Equal("operator-agent", artifact!.ProducerRole);
        Assert.Contains(baseHead!, artifact.Payload);                       // base revision
        Assert.Contains("colony.txt", artifact.Payload);                    // the changed file
        Assert.Contains("\"committed\":true", artifact.Payload);
        Assert.Contains("not colony-verified work", artifact.Payload);      // the load-bearing sentence
        Assert.False(string.IsNullOrWhiteSpace(artifact.ContentHash));
    }

    [Fact]
    public void AutoApprove_DirectEdit_IsCaptured_ButNeverCommitted()
    {
        if (!GitAvailable) return;
        SeedRepo();
        var baseHead = RepoOps.Head(_repo);

        EditingRunner("notes.txt", "auto-approve edit\n")
            .Run(Chat(EscalationPolicy.AutoApprove), "take a note");

        // NOT committed — under Automatically approve the dirty tree stays the operator's.
        var state = RepoOps.Describe(_repo);
        Assert.Equal(1, state.DirtyCount);
        Assert.Equal(baseHead, RepoOps.Head(_repo));

        var artifact = DirectChange();
        Assert.NotNull(artifact);
        Assert.Contains("\"committed\":false", artifact!.Payload);
        Assert.Contains("\"final_revision\":null", artifact.Payload);
        Assert.Contains("not colony-verified work", artifact.Payload);
    }

    [Fact]
    public void ManualApproval_NoSweep_NoArtifact_NoCommit()
    {
        if (!GitAvailable) return;
        SeedRepo();
        var baseHead = RepoOps.Head(_repo);

        EditingRunner("sneaky.txt", "should not be swept\n")
            .Run(Chat(EscalationPolicy.Ask), "hello");

        Assert.Equal(baseHead, RepoOps.Head(_repo));   // nothing committed
        Assert.Null(DirectChange());                   // and nothing captured: this lane is read-only by policy
    }

    [Fact]
    public void OperatorsPreexistingDirt_IsNeverSweptIntoTheColonyCommit()
    {
        if (!GitAvailable) return;
        SeedRepo();
        // The operator's own work-in-progress, dirty BEFORE the agent runs.
        File.WriteAllText(Path.Combine(_repo, "wip.txt"), "operator's own\n");

        EditingRunner("agentwork.txt", "the agent's file\n")
            .Run(Chat(EscalationPolicy.Bypass), "do the thing");

        var state = RepoOps.Describe(_repo);
        Assert.Equal(1, state.DirtyCount);                                   // wip.txt survives, uncommitted
        Assert.Contains(state.Dirty, d => d.Path.Contains("wip.txt"));
        var artifact = DirectChange();
        Assert.NotNull(artifact);
        Assert.DoesNotContain("wip.txt", artifact!.Payload);                 // and is not claimed by the capture
    }

    // ---- the structural half: this lane can never reach positive memory -------------------------

    /// <summary>
    /// Learning consumes canonical mission evaluations; a conversation is not a mission. That is
    /// the fail-closed guarantee — and it must be STRUCTURAL, so these are source-level detectors
    /// in the CrossBoundaryAgreementTests tradition: the conversation lane must never grow a
    /// pheromone or learning call, and the learning lane must never grow a direct_change consumer
    /// that could turn unverified direct-agent output into positive reinforcement.
    /// </summary>
    [Fact]
    public void TheDirectLane_HasNoPathIntoLearning_AndLearningHasNoPathIntoDirectChanges()
    {
        var runner = File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Conversations", "ConversationRunner.cs"));
        Assert.DoesNotContain("LearningRecorder", runner.Replace(
            "never feeds positive memory: learning consumes canonical mission", ""));  // prose mention only
        Assert.DoesNotContain("Pheromone", runner);
        Assert.DoesNotContain("ReinforceTrail", runner);

        var learning = File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "LearningRecorder.cs"));
        Assert.DoesNotContain("direct_change", learning);
    }
}
