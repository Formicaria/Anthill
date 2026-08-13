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

    // ---- the colony proposes the mission itself --------------------------------------------------

    /// <summary>
    /// The transcript's defect verbatim: the operator asked for work and was told to "ask for it
    /// as a mission explicitly". Now the chat model ends its reply with the escalation marker, the
    /// marker is stripped from the record, and the SAME gate the button used takes over — under
    /// Ask, the mission waits on the operator, visibly.
    /// </summary>
    [Fact]
    public void AChatReplyWithTheMarker_BecomesAGatedMission_UnderAsk()
    {
        var conversation = new Conversation { Id = "c-esc", Role = "queen", Policy = EscalationPolicy.Ask };
        _memory.SaveConversation(conversation);
        var runner = new ConversationRunner(_memory, (_, onCreated, _) => { onCreated("m-should-not-run"); return "m-should-not-run"; },
            ask: (_, _) => new ConversationReply(true,
                "This needs a real mission — building and testing.\n" + ConversationRunner.EscalateMarker,
                "local", "llama", null));

        var outcome = runner.Run(conversation, "run the self-check and fix what you find");

        // The proposal was converted into the mission path and REFUSED by the Ask gate — the
        // colony now waits on the operator instead of the operator waiting on a magic word.
        Assert.Equal(Anthill.Core.Conversations.ConversationMode.Mission, outcome.Mode);
        Assert.False(outcome.Started);
        Assert.Contains("escalation refused", outcome.Summary);

        var turns = _memory.LoadConversationTurns("c-esc");
        // One operator turn (never duplicated by the re-entry), one assistant turn, marker gone.
        Assert.Single(turns, t => t.Role == "user");
        var assistant = Assert.Single(turns, t => t.Role == "assistant");
        Assert.DoesNotContain(ConversationRunner.EscalateMarker, assistant.Content);
        Assert.Contains("real mission", assistant.Content);
    }

    [Fact]
    public void AChatReplyWithTheMarker_JustRuns_UnderAutoApprove()
    {
        var conversation = new Conversation
        {
            Id = "c-auto", Role = "queen", Policy = EscalationPolicy.AutoApprove,
            PolicySetBy = "zwright", PolicySetAt = DateTime.UtcNow,
        };
        _memory.SaveConversation(conversation);
        var missionId = Guid.NewGuid().ToString();
        var runner = new ConversationRunner(_memory, (_, onCreated, _) => { onCreated(missionId); return missionId; },
            ask: (_, _) => new ConversationReply(true, "On it.\n" + ConversationRunner.EscalateMarker, "local", "llama", null));

        var outcome = runner.Run(conversation, "fix the failing build");

        Assert.True(outcome.Started);
        Assert.Equal(missionId, outcome.MissionId);
        // The operator's single turn carries the mission link — proposal, gate and work one history.
        var userTurn = Assert.Single(_memory.LoadConversationTurns("c-auto"), t => t.Role == "user");
        Assert.Equal(missionId, userTurn.MissionId);
    }

    /// <summary>
    /// Mission 46f1acb7's defect verbatim: the operator said "Make all of these changes", the
    /// list of changes lived in the colony's own prior reply, and the mission goal carried five
    /// words. The goal now carries the bounded transcript, so the coder can see what "these" is.
    /// </summary>
    [Fact]
    public void TheMissionGoal_CarriesTheConversation_SoPronounsResolve()
    {
        var conversation = new Conversation { Id = "c-goal", Role = "queen" };
        _memory.SaveConversation(conversation);
        _memory.SaveConversationTurn(new ConversationTurn("t1", "c-goal", 1, "user", "self check please"));
        _memory.SaveConversationTurn(new ConversationTurn("t2", "c-goal", 2, "assistant",
            "Two improvements: implement patch delete in PatchApply.cs, and add escaping tests."));

        var runner = new ConversationRunner(_memory, (_, _, _) => "unused");
        var goal = runner.ComposeMissionGoal(conversation, "Make all of these changes");

        Assert.StartsWith("Make all of these changes", goal);
        Assert.Contains("patch delete in PatchApply.cs", goal);      // the referent travels
        Assert.Contains("conversation context", goal);

        // And a conversation with no prior turns escalates with the plain message, unchanged.
        var fresh = new Conversation { Id = "c-fresh", Role = "queen" };
        _memory.SaveConversation(fresh);
        Assert.Equal("do the thing", runner.ComposeMissionGoal(fresh, "do the thing"));
    }

    [Fact]
    public void AChatReplyWithoutTheMarker_StaysAChatAnswer()
    {
        var conversation = new Conversation { Id = "c-plain", Role = "queen" };
        _memory.SaveConversation(conversation);
        var runner = new ConversationRunner(_memory, (_, _, _) => "unused",
            ask: (_, _) => new ConversationReply(true, "The capital of France is Paris.", "local", "llama", null));

        var outcome = runner.Run(conversation, "what is the capital of France?");

        Assert.Equal(Anthill.Core.Conversations.ConversationMode.Chat, outcome.Mode);
        Assert.True(outcome.Started);
    }

    // ---- the direct-edit sweep (v0.3.8.52) --------------------------------------------------

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
    /// The v0.3.8.52 field defect verbatim: "Did not auto commit" — because under Skip-all the
    /// chat lane edits files DIRECTLY with its own tools, no patch exists, and the patch-apply
    /// commit hook has nothing to fire on. The sweep commits what the run made newly dirty —
    /// and ONLY that: the operator's own work-in-progress, dirty before the run, is untouchable.
    /// </summary>
    [Fact]
    public void DirectEditsUnderBypass_AreCommitted_TheOperatorsOwnDirtIsNot()
    {
        if (!GitAvailable) return;
        var root = NewRepo();
        File.WriteAllText(Path.Combine(root, "operator-wip.txt"), "the operator's half-done thought");

        _memory.SaveProject(new Anthill.Core.Projects.Project { Id = "p-sweep", Name = "sweep", Path = root });
        var conversation = new Conversation
        {
            Id = "c-sweep", Role = "queen", ProjectId = "p-sweep",
            Policy = EscalationPolicy.Bypass, PolicySetBy = "zwright", PolicySetAt = DateTime.UtcNow,
        };
        _memory.SaveConversation(conversation);

        var runner = new ConversationRunner(_memory, (_, _, _) => "unused",
            ask: (_, _) =>
            {
                File.WriteAllText(Path.Combine(root, "colony.txt"), "written directly by the agent");
                return new ConversationReply(true, "Done — colony.txt written.", "local", "llama", null);
            });
        var outcome = runner.Run(conversation, "add colony.txt with a note");
        Assert.True(outcome.Started);

        var state = Anthill.Core.Projects.RepoOps.Describe(root);
        Assert.Equal(1, state.DirtyCount);                                    // only the wip remains
        Assert.Contains(state.Dirty, d => d.Path.Contains("operator-wip"));
        Assert.NotNull(state.LastCommit);
        Assert.Contains("add colony.txt", state.LastCommit);                  // subject = the ask
    }

    /// <summary>Bypass only. Under Automatically approve a dirty tree is the operator's to
    /// commit (the pane's Commit button) — the sweep must not fire.</summary>
    [Fact]
    public void TheSweep_DoesNotFire_UnderAutoApprove()
    {
        if (!GitAvailable) return;
        var root = NewRepo();
        _memory.SaveProject(new Anthill.Core.Projects.Project { Id = "p-noswp", Name = "noswp", Path = root });
        var conversation = new Conversation
        {
            Id = "c-noswp", Role = "queen", ProjectId = "p-noswp",
            Policy = EscalationPolicy.AutoApprove, PolicySetBy = "zwright", PolicySetAt = DateTime.UtcNow,
        };
        _memory.SaveConversation(conversation);

        var runner = new ConversationRunner(_memory, (_, _, _) => "unused",
            ask: (_, _) =>
            {
                File.WriteAllText(Path.Combine(root, "colony.txt"), "auto-approve direct edit");
                return new ConversationReply(true, "Edited.", "local", "llama", null);
            });
        Assert.True(runner.Run(conversation, "tweak something small").Started);

        var state = Anthill.Core.Projects.RepoOps.Describe(root);
        Assert.Contains(state.Dirty, d => d.Path.Contains("colony.txt"));    // left for the operator
        Assert.Contains("seed", state.LastCommit ?? "");                     // no new commit landed
    }
}
