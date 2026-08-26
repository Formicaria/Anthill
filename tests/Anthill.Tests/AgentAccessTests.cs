using Anthill.Core.Conversations;
using Anthill.Core.Memory;
using Anthill.Modules.Reasoning;
using Anthill.SDK.Reasoning;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.51 (field report) — "Write and actually manipulating files doesn't work for self
/// improvement." The transcript showed the colony's own Claude Code worker with every mutating
/// call dying at permission prompts a headless run can never answer. These tests pin the repair:
/// the operator's approval gate translates into the agent's own flags, directory gates become
/// exactly the granted reach, and the chat lane proposes missions itself instead of demanding the
/// operator say a magic word.
/// </summary>
public class AgentAccessTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _memory;

    public AgentAccessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-access-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "memory.db"));
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static AgentCli ClaudeCode() =>
        AgentCliCatalog.All.Single(a => a.Id == "agent:claude-code");

    // ---- the gate reaches the agent -------------------------------------------------------------

    [Fact]
    public void Ask_GrantsEditsInTheConfinedWorkspace_AndNothingElse()
    {
        var args = AgentCliCatalog.BuildAccessArgs(ClaudeCode(),
            new AgentAccessScope.Context("ask", Array.Empty<string>(), ConfinedWorkspace: true));
        Assert.Equal(new[] { "--permission-mode", "acceptEdits" }, args);
    }

    /// <summary>Second field round: the CHAT lane stands in LIVE files. Manual approval grants it
    /// nothing — per-edit prompts are unanswerable headless, and un-asked edits to a real tree are
    /// exactly what the policy refuses. The agent proposes a mission instead.</summary>
    [Fact]
    public void Ask_InALiveDirectory_GrantsNothing()
    {
        Assert.Empty(AgentCliCatalog.BuildAccessArgs(ClaudeCode(),
            new AgentAccessScope.Context("ask", Array.Empty<string>(), ConfinedWorkspace: false)));
        Assert.Null(AgentCliCatalog.BuildLocalSettingsJson(
            new AgentAccessScope.Context("ask", Array.Empty<string>(), ConfinedWorkspace: false)));
    }

    /// <summary>
    /// The colony's own probe named the real wall: no project-level settings file, so headless
    /// runs fall to harness defaults flags don't fully override. The materialized settings are
    /// the second channel of the SAME policy — marker present, allow list matching the flags,
    /// grants as additionalDirectories, and nothing at all when nothing is granted.
    /// </summary>
    [Fact]
    public void MaterializedSettings_MirrorThePolicy_BothChannelsOneAnswer()
    {
        var auto = AgentCliCatalog.BuildLocalSettingsJson(
            new AgentAccessScope.Context("autoapprove", new[] { "/srv/data" }));
        Assert.NotNull(auto);
        Assert.Contains(AgentCliCatalog.SettingsMarkerKey, auto);
        Assert.Contains("\"Edit\"", auto);
        Assert.Contains("Bash(dotnet:*)", auto);
        Assert.DoesNotContain("Bash(*)", auto);
        Assert.Contains("/srv/data", auto);

        var askConfined = AgentCliCatalog.BuildLocalSettingsJson(
            new AgentAccessScope.Context("ask", Array.Empty<string>(), ConfinedWorkspace: true));
        Assert.NotNull(askConfined);
        Assert.Contains("\"Write\"", askConfined);
        Assert.DoesNotContain("Bash(dotnet:*)", askConfined);

        Assert.Null(AgentCliCatalog.BuildLocalSettingsJson(null));
    }

    /// <summary>Materialization respects ownership and downgrades: it never touches an operator's
    /// own file, and a policy granting nothing DELETES the file Anthill previously wrote.</summary>
    [Fact]
    public void MaterializeLocalSettings_RespectsOwnership_AndClosesGatesOnDowngrade()
    {
        var work = Path.Combine(_dir, "ws");
        Directory.CreateDirectory(work);
        var agent = ClaudeCode();
        var settingsPath = Path.Combine(work, agent.LocalSettingsRelativePath!);

        // Grant → the file exists, marked as Anthill's.
        AgentCliCatalog.MaterializeLocalSettings(agent, work,
            new AgentAccessScope.Context("autoapprove", Array.Empty<string>()));
        Assert.True(File.Exists(settingsPath));
        Assert.Contains(AgentCliCatalog.SettingsMarkerKey, File.ReadAllText(settingsPath));

        // Downgrade to ask-in-live-tree → the gate closes: the file is REMOVED.
        AgentCliCatalog.MaterializeLocalSettings(agent, work,
            new AgentAccessScope.Context("ask", Array.Empty<string>(), ConfinedWorkspace: false));
        Assert.False(File.Exists(settingsPath));

        // An operator's own settings file (no marker) is never touched, in either direction.
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "{\"permissions\":{\"allow\":[\"WebFetch\"]}}");
        AgentCliCatalog.MaterializeLocalSettings(agent, work,
            new AgentAccessScope.Context("autoapprove", Array.Empty<string>()));
        Assert.Contains("WebFetch", File.ReadAllText(settingsPath));
        Assert.DoesNotContain(AgentCliCatalog.SettingsMarkerKey, File.ReadAllText(settingsPath));
    }

    [Fact]
    public void AutoApprove_AddsTheBoundedToolSet()
    {
        var args = AgentCliCatalog.BuildAccessArgs(ClaudeCode(),
            new AgentAccessScope.Context("autoapprove", Array.Empty<string>()));
        Assert.Contains("--permission-mode", args);
        Assert.Contains("--allowedTools", args);
        // Bounded: build/test tools, nothing network-shaped, no blanket bash.
        var list = args.ToList();
        var tools = list[list.IndexOf("--allowedTools") + 1];
        Assert.Contains("Bash(dotnet:*)", tools);
        Assert.DoesNotContain("Bash(*)", tools);
        Assert.DoesNotContain("curl", tools);
    }

    [Fact]
    public void Bypass_MapsToTheAgentsOwnSkipFlag_AndOnlyThat()
    {
        var args = AgentCliCatalog.BuildAccessArgs(ClaudeCode(),
            new AgentAccessScope.Context("bypass", Array.Empty<string>()));
        Assert.Equal(new[] { "--dangerously-skip-permissions" }, args);
    }

    [Fact]
    public void DirectoryGates_BecomeAddDirReach_OnePerGrant()
    {
        var args = AgentCliCatalog.BuildAccessArgs(ClaudeCode(),
            new AgentAccessScope.Context("ask", new[] { "/repos/anthill", "/srv/data" }));
        Assert.Equal(2, args.Count(a => a == "--add-dir"));
        Assert.Contains("/repos/anthill", args);
        Assert.Contains("/srv/data", args);
    }

    /// <summary>Absence is not consent, in both directions: no scope grants nothing, and an agent
    /// with no mapped flags gets nothing even under bypass.</summary>
    [Fact]
    public void NoScope_AndUnmappedAgents_GetNothing()
    {
        Assert.Empty(AgentCliCatalog.BuildAccessArgs(ClaudeCode(), null));

        var unmapped = AgentCliCatalog.All.First(a => a.BypassArgs is null or { Count: 0 });
        Assert.Empty(AgentCliCatalog.BuildAccessArgs(unmapped,
            new AgentAccessScope.Context("bypass", new[] { "/anywhere" })));
    }

    // ---- v0.3.8.93: the role's contract clamps before the policy grants -------------------------

    /// <summary>
    /// SKIP ALL APPROVALS SKIPS THE OPERATOR'S PROMPTS, NOT THE ROLE'S CONTRACT. Until v0.3.8.93
    /// the scope carried only the conversation policy, so a read-only researcher routed to Claude
    /// Code under Bypass was handed <c>--dangerously-skip-permissions</c> — full vendor write
    /// authority for a role whose registry contract can neither propose patches nor touch the
    /// workspace. The clamp: a non-writing role gets NO permission flags under any policy, and its
    /// materialized settings carry no Edit/Write/Bash. Directory gates survive as reach — a reader
    /// with reach is what the operator opened the gate for.
    /// </summary>
    [Theory]
    [InlineData("bypass")]
    [InlineData("autoapprove")]
    [InlineData("ask")]
    public void AReadOnlyRole_GetsNoPermissionFlags_UnderAnyPolicy(string policy)
    {
        var scope = new AgentAccessScope.Context(policy, Array.Empty<string>(),
            ConfinedWorkspace: true, RoleMayWrite: false);

        Assert.Empty(AgentCliCatalog.BuildAccessArgs(ClaudeCode(), scope));

        var settings = AgentCliCatalog.BuildLocalSettingsJson(scope);
        // No grants at all → null, which DELETES a previously materialized file: a downgrade that
        // closes a gate an earlier writing role legitimately opened in the same workspace.
        Assert.Null(settings);
    }

    [Fact]
    public void AReadOnlyRole_KeepsDirectoryReach_AndNothingElse()
    {
        var scope = new AgentAccessScope.Context("bypass", new[] { "/srv/data" },
            ConfinedWorkspace: true, RoleMayWrite: false);

        var args = AgentCliCatalog.BuildAccessArgs(ClaudeCode(), scope);
        Assert.Equal(new[] { "--add-dir", "/srv/data" }, args);
        Assert.DoesNotContain("--dangerously-skip-permissions", args);

        var settings = AgentCliCatalog.BuildLocalSettingsJson(scope);
        Assert.NotNull(settings);
        Assert.Contains("/srv/data", settings);
        Assert.DoesNotContain("\"Edit\"", settings);
        Assert.DoesNotContain("\"Write\"", settings);
        Assert.DoesNotContain("Bash(", settings);
    }

    /// <summary>A writing role under the same scope keeps exactly the pre-.93 translation — the
    /// clamp narrows, it never widens, and the default (no flag passed) is the writing shape so
    /// the operator's own direct agent lane is untouched.</summary>
    [Fact]
    public void AWritingRole_IsUnchangedByTheClamp()
    {
        var clamped = AgentCliCatalog.BuildAccessArgs(ClaudeCode(),
            new AgentAccessScope.Context("bypass", Array.Empty<string>(), RoleMayWrite: true));
        var defaulted = AgentCliCatalog.BuildAccessArgs(ClaudeCode(),
            new AgentAccessScope.Context("bypass", Array.Empty<string>()));

        Assert.Equal(new[] { "--dangerously-skip-permissions" }, clamped);
        Assert.Equal(clamped, defaulted);
    }

    /// <summary>
    /// The registry is the authority on which roles write: every role whose contract has neither
    /// ProposePatches nor WriteWorkspace is a reader at the CLI boundary. Derived from the
    /// registry rather than listed, so a contract change moves this test with it.
    /// </summary>
    [Fact]
    public void TheRegistrysReadOnlyRoles_AreExactlyTheClampedOnes()
    {
        var readers = Anthill.Core.Agents.AntRegistry.Roles
            .Where(r => !r.Permissions.ProposePatches && !r.Permissions.WriteWorkspace)
            .Select(r => r.RoleId).ToList();

        // The claim this release depends on: the researcher/builder/verifier trio are readers,
        // and the coder is not. If a registry change moves one of these, the CLI clamp moves too —
        // this test is where that becomes a conscious decision instead of a silent widening.
        Assert.Contains("researcher", readers);
        Assert.Contains("builder", readers);
        Assert.Contains("verifier", readers);
        Assert.DoesNotContain("coder", readers);
    }

    [Fact]
    public void TheScope_IsAmbient_AndRestoresOnDispose()
    {
        Assert.Null(AgentAccessScope.Current);
        using (AgentAccessScope.Enter("autoapprove", new[] { "/x" }))
        {
            Assert.Equal("autoapprove", AgentAccessScope.Current!.PolicyWire);
            Assert.Equal(new[] { "/x" }, AgentAccessScope.Current.GrantedDirectories);
        }
        Assert.Null(AgentAccessScope.Current);
    }

    // ---- directory gates persist, attributed and revocable --------------------------------------

    [Fact]
    public void Grants_RoundTrip_AttributedAndRevocable()
    {
        _memory.SaveProjectGrant(new ProjectGrant("g1", "p1", "/repos/anthill") { GrantedBy = "zwright" });
        _memory.SaveProjectGrant(new ProjectGrant("g2", "p1", "/srv/data") { GrantedBy = "zwright" });
        _memory.SaveProjectGrant(new ProjectGrant("g3", "OTHER", "/elsewhere") { GrantedBy = "someone" });

        var grants = _memory.LoadProjectGrants("p1");
        Assert.Equal(2, grants.Count);
        Assert.All(grants, g => Assert.Equal("zwright", g.GrantedBy));
        Assert.DoesNotContain(grants, g => g.Path == "/elsewhere");

        _memory.DeleteProjectGrant("g1");
        Assert.Single(_memory.LoadProjectGrants("p1"));
    }

    [Fact]
    public void TheMissionsConversation_IsFoundThroughItsTurn()
    {
        var conversation = new Conversation { Id = "c-find", ProjectId = "p1", Role = "queen" };
        _memory.SaveConversation(conversation);
        _memory.SaveConversationTurn(new ConversationTurn("t1", "c-find", 1, "user", "go")
        { MissionId = "m-123" });

        Assert.Equal("c-find", _memory.FindConversationForMission("m-123")!.Id);
        Assert.Null(_memory.FindConversationForMission("m-unknown"));
    }

    // ---- every message is a mission --------------------------------------------------------

    /// <summary>
    /// v0.3.8.58 — no marker, no proposal, no chat reply. The operator's message IS the mission,
    /// and under Ask it waits on the operator, visibly.
    ///
    /// This replaces `AChatReplyWithTheMarker_BecomesAGatedMission_UnderAsk`. That test was a real
    /// improvement in its release — before it, the colony answered a work request by telling the
    /// operator to "ask for it as a mission explicitly", a magic word. But it still routed the
    /// decision through a MODEL: the chat provider chose whether to emit the marker, so whether the
    /// colony's pipeline ran at all was a matter of the model's judgement about its own necessity.
    /// A model deciding it does not need the review is the one decision it must never make.
    /// </summary>
    [Fact]
    public void AnyMessage_BecomesAGatedMission_UnderAsk()
    {
        var conversation = new Conversation { Id = "c-esc", Role = "queen", Policy = EscalationPolicy.Ask };
        _memory.SaveConversation(conversation);
        var runner = new ConversationRunner(_memory,
            (_, _, onCreated, _) => { onCreated("m-should-not-run"); return "m-should-not-run"; });

        var outcome = runner.Run(conversation, "run the self-check and fix what you find");

        Assert.Equal(Anthill.Core.Conversations.ConversationMode.Mission, outcome.Mode);
        Assert.False(outcome.Started);
        Assert.Contains("escalation refused", outcome.Summary);

        // One operator turn, and NO assistant turn — nothing answered, because nothing ran.
        var turns = _memory.LoadConversationTurns("c-esc");
        Assert.Single(turns, t => t.Role == "user");
        Assert.DoesNotContain(turns, t => t.Role == "assistant");
    }

    [Fact]
    public void AnyMessage_JustRuns_UnderAutoApprove()
    {
        var conversation = new Conversation
        {
            Id = "c-auto", Role = "queen", Policy = EscalationPolicy.AutoApprove,
            PolicySetBy = "zwright", PolicySetAt = DateTime.UtcNow,
        };
        _memory.SaveConversation(conversation);
        var missionId = Guid.NewGuid().ToString();
        var runner = new ConversationRunner(_memory, (_, _, onCreated, _) => { onCreated(missionId); return missionId; });

        var outcome = runner.Run(conversation, "fix the failing build");

        Assert.True(outcome.Started);
        Assert.Equal(missionId, outcome.MissionId);
        var userTurn = Assert.Single(_memory.LoadConversationTurns("c-auto"), t => t.Role == "user");
        Assert.Equal(missionId, userTurn.MissionId);
    }

    /// <summary>
    /// A QUESTION is a mission too, and this is the assertion the operator asked for in so many
    /// words: "everytime something is sent in the chat box, it should be a mission for the colony.
    /// EVERYTIME."
    ///
    /// The inverted test it replaces — `AChatReplyWithoutTheMarker_StaysAChatAnswer` — encoded the
    /// opposite rule, that a question stays outside the colony. That is the seam the whole lane grew
    /// from: something has to decide "this one is only a question", and wherever that decision lives
    /// is a place the pipeline can be skipped. The planner may still answer a question with a small
    /// plan; what it may not do is not be asked.
    /// </summary>
    [Fact]
    public void AQuestion_IsAMissionToo()
    {
        var conversation = new Conversation
        {
            Id = "c-plain", Role = "queen", Policy = EscalationPolicy.Bypass,
            PolicySetBy = "zwright", PolicySetAt = DateTime.UtcNow,
        };
        _memory.SaveConversation(conversation);
        var runner = new ConversationRunner(_memory, (_, _, onCreated, _) => { onCreated("m-q"); return "m-q"; });

        var outcome = runner.Run(conversation, "what is the capital of France?");

        Assert.Equal(Anthill.Core.Conversations.ConversationMode.Mission, outcome.Mode);
        Assert.Equal("m-q", outcome.MissionId);
    }

    /// <summary>
    /// Mission 46f1acb7's defect verbatim: the operator said "Make all of these changes", the
    /// list of changes lived in the colony's own prior reply, and the mission goal carried five
    /// words. The goal carries the bounded transcript, so the coder can see what "these" is.
    /// </summary>
    [Fact]
    public void TheMissionGoal_CarriesTheConversation_SoPronounsResolve()
    {
        var conversation = new Conversation { Id = "c-goal", Role = "queen" };
        _memory.SaveConversation(conversation);
        _memory.SaveConversationTurn(new ConversationTurn("t1", "c-goal", 1, "user", "self check please"));
        _memory.SaveConversationTurn(new ConversationTurn("t2", "c-goal", 2, "assistant",
            "Two improvements: implement patch delete in PatchApply.cs, and add escaping tests."));

        var runner = new ConversationRunner(_memory, (_, _, _, _) => "unused");
        var goal = runner.ComposeMissionGoal(conversation, "Make all of these changes");

        Assert.StartsWith("Make all of these changes", goal);
        Assert.Contains("patch delete in PatchApply.cs", goal);      // the referent travels
        Assert.Contains("conversation context", goal);

        // And a conversation with no prior turns, no project and no attachments escalates with the
        // plain message, unchanged.
        var fresh = new Conversation { Id = "c-fresh", Role = "queen" };
        _memory.SaveConversation(fresh);
        Assert.Equal("do the thing", runner.ComposeMissionGoal(fresh, "do the thing"));
    }

    // ---- the direct-edit lane is gone ---------------------------------------------------------

    private static bool GitAvailable =>
        Anthill.Core.Projects.RepoOps.Git(Path.GetTempPath(), "--version").Ok;

    private string NewRepo()
    {
        var root = Path.Combine(_dir, "repo-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        Assert.True(Anthill.Core.Projects.RepoOps.Git(root, "init").Ok);
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        Assert.True(Anthill.Core.Projects.RepoOps.Commit(root, new[] { "seed.txt" }, "seed", "t").Ok);
        return root;
    }

    /// <summary>
    /// A CONVERSATION TOUCHES NOTHING, under the most permissive policy there is.
    ///
    /// This replaces the two direct-edit sweep tests, and the replacement is an inversion rather
    /// than a deletion, because the sweep is the evidence of what the old lane was. It existed to
    /// notice which files a chat turn had written to the operator's live tree and commit them —
    /// v0.3.8.52's field report was "did not auto commit", meaning the lane wrote files and the
    /// colony wanted the commits. Nobody builds that for a lane that answers questions.
    ///
    /// v0.3.8.57 refused a coding agent for the conversation route and rewrote the prompt to say it
    /// had no tools, and left the sweep and the unconfined access scope in place — so the sentence
    /// said one thing and the wiring still did another. Under Skip-all, this test would have failed.
    /// </summary>
    [Fact]
    public void UnderSkipAll_AConversationWritesNothingToTheTree()
    {
        if (!GitAvailable) return;
        var root = NewRepo();
        var before = Anthill.Core.Projects.RepoOps.Describe(root);

        _memory.SaveProject(new Anthill.Core.Projects.Project { Id = "p-sweep", Name = "sweep", Path = root });
        var conversation = new Conversation
        {
            Id = "c-sweep", Role = "queen", ProjectId = "p-sweep",
            Policy = EscalationPolicy.Bypass, PolicySetBy = "zwright", PolicySetAt = DateTime.UtcNow,
        };
        _memory.SaveConversation(conversation);

        // The mission pipeline is faked, so nothing downstream runs either: the only thing that
        // could write here is the conversation lane itself, which is the thing being tested.
        var runner = new ConversationRunner(_memory, (_, _, onCreated, _) => { onCreated("m-1"); return "m-1"; });
        Assert.True(runner.Run(conversation, "add colony.txt with a note").Started);

        var after = Anthill.Core.Projects.RepoOps.Describe(root);
        Assert.Equal(before.DirtyCount, after.DirtyCount);
        Assert.Equal(before.LastCommit, after.LastCommit);
        Assert.False(File.Exists(Path.Combine(root, "colony.txt")));
    }
}
