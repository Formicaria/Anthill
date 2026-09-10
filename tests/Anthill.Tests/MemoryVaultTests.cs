using System;
using System.IO;
using System.Linq;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE MEMORY VAULT: one shape over everything the colony remembers. v0.3.9.
///
/// These hold the two properties the feature rests on. The first is that the projection actually
/// crosses kinds — a browser whose search only reaches missions is a mission list with a search box.
/// The second is that the chamber a record lives in is decided ONCE: the 3D view seats a dot by it
/// and the sidebar counts a folder by it, and two spellings of that rule would put a record in a
/// chamber the counts say it is not in.
/// </summary>
public class MemoryVaultTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_vault_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        return new SqliteMemory(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
    }

    private static Mission Seed(SqliteMemory mem)
    {
        var mission = new Mission { Goal = "deploy the handbook to the wiki" };
        mem.SaveMission(mission);
        mem.SaveTask(mission.Id, new Task
        {
            Title = "Read what this project already knows",
            AssignedAnt = "researcher", TaskType = "research",
        });
        mem.SaveTask(mission.Id, new Task
        {
            Title = "Answer the request",
            AssignedAnt = "builder", TaskType = "build_answer",
        });
        mem.LogEvent(mission.Id, "mission_created", "Mission created.");
        return mission;
    }

    [Fact]
    public void TheVault_CrossesKinds_InOneQuery()
    {
        var mem = Memory();
        Seed(mem);

        var page = mem.VaultQuery(limit: 500);

        Assert.True(page.Total >= 4, $"the vault found {page.Total} records; the fixture wrote a mission, two tasks and an event");
        var kinds = page.Kinds.Select(k => k.Key).ToList();
        Assert.Contains("mission", kinds);
        Assert.Contains("task", kinds);
        Assert.Contains("event", kinds);
    }

    /// <summary>
    /// SEARCH REACHES ACROSS THEM. The word is in the mission's goal and in no task title, so a
    /// query that only searched titles-by-kind would answer this correctly by accident; the
    /// assertion is that the match arrives from the mission ROW inside a cross-kind query.
    /// </summary>
    [Fact]
    public void Search_FindsAcrossEveryKind()
    {
        var mem = Memory();
        Seed(mem);

        var hits = mem.VaultQuery(query: "handbook", limit: 100);
        Assert.Contains(hits.Records, r => r.Kind == "mission");

        var byTask = mem.VaultQuery(query: "Answer the request", limit: 100);
        Assert.Contains(byTask.Records, r => r.Kind == "task");

        Assert.Empty(mem.VaultQuery(query: "nothing in this colony says this", limit: 100).Records);
    }

    /// <summary>
    /// ONE HOME EACH, AND ONE RULE THAT DECIDES IT.
    ///
    /// `VaultChambers.ForRole` is the C# spelling and `VaultChambers.RoleCase` is the SQL one, and
    /// the projection uses the second. Two implementations of one rule is the defect this repository
    /// keeps paying for, so this drives EVERY role in the shipped roster through both and requires
    /// the same answer — which is what makes it safe for them to exist at all.
    /// </summary>
    [Fact]
    public void TheChamberRule_HasOneAnswer_InSqlAndInCode()
    {
        var mem = Memory();
        var mission = new Mission { Goal = "one task per role" };
        mem.SaveMission(mission);

        var roles = new[]
        {
            "queen", "planner", "constraint", "director", "researcher", "web", "file", "archivist",
            "coder", "ui_cartographer", "tester", "verifier", "soldier", "medic", "builder", "scribe",
            "a_role_nobody_declared",
        };
        foreach (var role in roles)
            mem.SaveTask(mission.Id, new Task { Title = role + " task", AssignedAnt = role });

        var tasks = mem.VaultQuery(kind: "task", limit: 500).Records;

        foreach (var role in roles)
        {
            var row = tasks.Single(r => r.Title == role + " task");
            Assert.Equal(VaultChambers.ForRole(role), row.Chamber);
        }
    }

    /// <summary>
    /// THE DOTS CARRY WHAT A DOT NEEDS AND NOT A BYTE MORE — with the exception of the trimmed title
    /// and the group, which are what make a dot clickable and seatable without a second call.
    /// </summary>
    [Fact]
    public void Dots_CarryTheirGroup_AndAreEveryRecord()
    {
        var mem = Memory();
        Seed(mem);

        var dots = mem.VaultDots("chamber");
        var page = mem.VaultQuery(limit: 1);

        Assert.Equal(page.Total, dots.Count);
        Assert.All(dots, d => Assert.False(string.IsNullOrWhiteSpace(d.Chamber)));
        // Grouped BY CHAMBER, so every dot's group is its chamber — the cheapest possible proof
        // that the record's group and the folder's group come from the same expression.
        Assert.All(dots, d => Assert.Equal(d.Chamber, d.Group));
    }

    /// <summary>
    /// THE LOCAL GRAPH IS THE COLONY'S OWN LINEAGE, not a similarity score: a mission's steps are
    /// its steps because the task rows say so.
    /// </summary>
    [Fact]
    public void Links_AreTheRecordedLineage()
    {
        var mem = Memory();
        var mission = Seed(mem);

        var links = mem.VaultLinks("mission:" + mission.Id);

        Assert.Equal(2, links.Count(l => l.Kind == "task"));
        Assert.All(links, l => Assert.False(l.Derived, "nothing in a mission's lineage is inferred"));
        Assert.Contains(links, l => l.Title.Contains("Answer the request", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIdIsSplitOnItsFirstColon()
    {
        Assert.Equal(("mission", "abc:def"), SqliteMemory.VaultSplit("mission:abc:def"));
        Assert.Equal(("", ""), SqliteMemory.VaultSplit("nocolon"));
        Assert.Equal(("", ""), SqliteMemory.VaultSplit(null));
    }

    /// <summary>
    /// FILTERS NARROW, AND THE COUNTS FOLLOW THEM. A tree whose folder counts described the whole
    /// colony while its rows described a filtered slice would be lying in the one place an operator
    /// uses to decide whether to look.
    /// </summary>
    [Fact]
    public void FolderCounts_DescribeTheFilteredSet()
    {
        var mem = Memory();
        Seed(mem);

        var all = mem.VaultQuery(limit: 500);
        var onlyTasks = mem.VaultQuery(kind: "task", limit: 500);

        Assert.True(all.Total > onlyTasks.Total);
        Assert.Single(onlyTasks.Kinds);
        Assert.Equal("task", onlyTasks.Kinds[0].Key);
        Assert.Equal(onlyTasks.Total, onlyTasks.Kinds[0].Count);
    }
}
