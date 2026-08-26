using Anthill.Core.Conversations;
using Anthill.Core.Memory;
using Anthill.Core.Projects;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.47 — projects: the long-lived container a conversation lives in. One per conversation,
/// created at conversation start or by hand with a name, a markdown purpose, and an optional
/// working-directory path. The purpose is standing context; these tests pin the storage and the
/// context injection, which are the two claims the Projects page makes.
/// </summary>
public class ProjectTests : IDisposable
{
    private readonly string _dir;

    public ProjectTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-proj-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SqliteMemory Memory() => new(Path.Combine(_dir, "memory.db"));

    [Fact]
    public void AProject_SurvivesReload_AndAMissingPathStaysMissing()
    {
        using var memory = Memory();
        memory.SaveProject(new Project { Id = "p1", Name = "anthill docs",
            DescriptionMd = "# Purpose\nKeep the docs honest.", Path = "/home/z/docs" });
        memory.SaveProject(new Project { Id = "p2", Name = "no path given" });

        var withPath = memory.LoadProject("p1")!;
        Assert.Equal("anthill docs", withPath.Name);
        Assert.Equal("# Purpose\nKeep the docs honest.", withPath.DescriptionMd);
        Assert.Equal("/home/z/docs", withPath.Path);

        // Null is "the operator gave none", and it must rehydrate as exactly that.
        Assert.Null(memory.LoadProject("p2")!.Path);
    }

    [Fact]
    public void ArchivedProjects_SortLast_ButAreKept()
    {
        using var memory = Memory();
        memory.SaveProject(new Project { Id = "old", Name = "done with", Archived = true,
            UpdatedAt = DateTime.UtcNow });
        memory.SaveProject(new Project { Id = "live", Name = "current",
            UpdatedAt = DateTime.UtcNow.AddDays(-5) });

        var list = memory.LoadProjects();

        // Archived loses to active regardless of recency — closed, never erased.
        Assert.Equal(new[] { "live", "old" }, list.Select(p => p.Id));
    }

    [Fact]
    public void AConversation_KeepsItsProjectLink()
    {
        using var memory = Memory();
        memory.SaveProject(new Project { Id = "p1", Name = "the project" });
        memory.SaveConversation(new Conversation { Id = "c1", Title = "inside", ProjectId = "p1" });
        memory.SaveConversation(new Conversation { Id = "c2", Title = "legacy, no project" });

        Assert.Equal("p1", memory.LoadConversation("c1")!.ProjectId);
        Assert.Null(memory.LoadConversation("c2")!.ProjectId);
        Assert.Equal("c1", Assert.Single(memory.LoadProjectConversations("p1")).Id);
    }

    /// <summary>
    /// The purpose is not decoration: it travels into the conversation prompt as standing
    /// context, clearly attributed to the operator. This is the page's one behavioural claim.
    /// </summary>
    [Fact]
    public void TheProjectsPurpose_TravelsIntoTheConversationPrompt()
    {
        using var memory = Memory();
        memory.SaveProject(new Project { Id = "p1", Name = "release engineering",
            DescriptionMd = "Ship small, ship honest.", Path = "/repos/anthill" });
        var conversation = new Conversation { Id = "c1", ProjectId = "p1" };
        memory.SaveConversation(conversation);

        // v0.3.8.58: the project's standing context used to frame the CHAT PROMPT. Chat is gone —
        // every message is a mission — so the assertion follows the context to the mission GOAL.
        // Dropping this test with the prompt would have quietly un-tested the project description,
        // and a description nothing reads is the reason for writing one, removed.
        string? seen = null;
        new ConversationRunner(memory, (goal, _, onCreated, _) => { seen = goal; onCreated("m1"); return "m1"; })
            .Run(conversation with { Policy = EscalationPolicy.Bypass, PolicySetBy = "zwright", PolicySetAt = DateTime.UtcNow }, "what are we doing here?");

        Assert.Contains("release engineering", seen);
        Assert.Contains("Ship small, ship honest.", seen);
        Assert.Contains("/repos/anthill", seen);
        Assert.Contains("operator describes its purpose", seen);
    }

    /// <summary>
    /// v0.3.8.47: an attachment rides its turn — stored against it, and fed to the model framed
    /// as an operator-provided file. Binary and size rules live at the API; storage just keeps
    /// what it is given faithfully.
    ///
    /// v0.3.8.58: "the prompt" is now the MISSION GOAL — the chat prompt that used to read
    /// attachments is deleted, and they were rehomed rather than dropped.
    /// </summary>
    [Fact]
    public void AnAttachment_RidesItsTurn_IntoStorageAndTheMissionGoal()
    {
        using var memory = Memory();
        var conversation = new Conversation { Id = "c1" };
        memory.SaveConversation(conversation);

        string? seen = null;
        new ConversationRunner(memory, (goal, _, onCreated, _) => { seen = goal; onCreated("m1"); return "m1"; })
            .Run(conversation with { Policy = EscalationPolicy.Bypass, PolicySetBy = "zwright", PolicySetAt = DateTime.UtcNow }, "summarize this file",
                attachments: new[] { ("notes.md", "# Notes\nthe colony hums") });

        var userTurn = memory.LoadConversationTurns("c1")[0];
        var stored = Assert.Single(memory.LoadTurnAttachments(userTurn.Id));
        Assert.Equal("notes.md", stored.Filename);
        Assert.Contains("the colony hums", stored.Content);

        Assert.Contains("notes.md", seen);
        Assert.Contains("the colony hums", seen);
        Assert.Contains("the colony hums", seen);
    }

    /// <summary>
    /// v0.3.8.48: a conversation NEVER invents its project. The endpoint refuses a missing
    /// project_id with its own error code, no auto-create branch survives, and the picker is the
    /// UI's answer — pinned at the source seams the behaviour lives in.
    /// </summary>
    [Fact]
    public void AConversation_RequiresAChosenProject()
    {
        var api = File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Api", "ApiHost.Providers.cs"));
        Assert.Contains("project_required", api);
        Assert.DoesNotContain("Name = string.IsNullOrWhiteSpace(body?.Title) ? \"New conversation\"", api);

        var js = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.UI", "app.js"));
        Assert.Contains("chatPickProject", js);
        // Cancel keeps the operator's message; nothing is created behind their back.
        Assert.Contains("if(!pid){ if(el) el.value=msg;", js);
        // No hardcoded policy on creation — the project's attributed default governs.
        Assert.DoesNotContain("policy:'ask'", js);
    }

    /// <summary>A project's unattributed non-Ask default reads as Ask. Same fail-closed rule.</summary>
    [Fact]
    public void AProjectsDefaultPolicy_FailsClosedWithoutAnAuthor()
    {
        var unattributed = new Project { Id = "p", DefaultPolicy = EscalationPolicy.Bypass };
        Assert.Equal(EscalationPolicy.Ask, unattributed.EffectiveDefaultPolicy);

        var attributed = unattributed with { DefaultPolicyBy = "zwright", DefaultPolicyAt = DateTime.UtcNow };
        Assert.Equal(EscalationPolicy.Bypass, attributed.EffectiveDefaultPolicy);
    }

    /// <summary>
    /// The console reaches every surface this feature shipped — a projects API with no caller is
    /// the "no call site, no feature" defect this suite exists to catch.
    /// </summary>
    [Fact]
    public void TheProjectSurfaces_AreWiredIntoTheConsole()
    {
        var js = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.UI", "app.js"));
        var html = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.UI", "index.html"));

        foreach (var stem in new[] { "'/projects'", "'/conversations/import'" })
            Assert.Contains(stem, js);
        // Attachments: staged client-side with the same limits the API enforces, said out loud.
        Assert.Contains("chatStageFiles", js);
        Assert.Contains("262144", js);
        Assert.Contains("id=\"chat-attach\"", html);
        // Import parses client-side so a bad file fails with words, not a 400.
        Assert.Contains("id=\"chat-import\"", html);
    }
}
