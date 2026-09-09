using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Conversations;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE DANGER ZONE, AND ITS GATE IS ON THE SERVER. v0.3.8.145.
///
/// The Settings redesign puts the three irreversible actions — reset configuration, delete all
/// backups, wipe colony memory — on one page behind a typed confirmation: the operator types the
/// colony's name to unlock a button. This file holds the half of that promise the page cannot
/// keep by itself.
///
/// Three things are pinned. The confirmation is read by the ENDPOINT, from the body, and compared
/// against the runtime's colony name — a disabled button is not a gate, which this repository
/// wrote down at `.38` about clear-missions and then had to write down again here. The wipe takes
/// exactly what its row says it takes and keeps exactly what its row says it keeps, because a
/// destructive action whose description drifts from its effect is the worst kind of lie a settings
/// page can tell. And a wipe is refused while a mission is RUNNING by the mission table, not by the
/// API job queue: a chat-started mission never enters the queue, which is the same hole the header
/// had at `.144` — reporting "idle" — in the one place it would have destroyed data instead.
/// </summary>
public class DangerZoneTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _memory;

    public DangerZoneTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-danger-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "memory.db"));
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- the wipe takes what it says and keeps what it says ---------------------------------

    [Fact]
    public void WipeColonyMemory_DeletesMissionsConversationsAndTrails_KeepsProjectsAndUsers()
    {
        // A project the operator wrote down, a user, a mission with an event, a conversation with a
        // turn, and a pheromone trail the colony learned. After the wipe: the first two remain and
        // everything the colony REMEMBERED is gone.
        _memory.SaveProject(new Anthill.Core.Projects.Project { Id = "p-falcon", Name = "Falcon Research" });
        _memory.CreateUser("zwright", "a-long-enough-password-1", "admin");
        var mission = new Mission { Goal = "assess the colony", Status = MissionStatus.Complete };
        _memory.SaveMission(mission);
        _memory.LogEvent(mission.Id, "mission_started", "started");
        _memory.SaveConversation(new Conversation { Id = "c-wipe", Role = "queen", ProjectId = "p-falcon", Title = "hello" });
        _memory.SaveConversationTurn(new ConversationTurn("t1", "c-wipe", 1, "user", "hello"));
        _memory.UpdatePheromoneTrail("role:coder", "route_success", success: true, strengthDelta: 1.0);

        Assert.Single(_memory.GetRecentMissions(10), m => m["id"]?.ToString() == mission.Id);
        Assert.NotNull(_memory.LoadConversation("c-wipe"));
        Assert.NotEmpty(_memory.ListPheromoneTrails());

        var (freed, missions) = _memory.WipeColonyMemory();

        Assert.Equal(1, missions);
        Assert.True(freed >= 0);
        Assert.DoesNotContain(_memory.GetRecentMissions(10), m => m["id"]?.ToString() == mission.Id);
        Assert.Null(_memory.LoadConversation("c-wipe"));
        Assert.Empty(_memory.LoadConversationTurns("c-wipe"));
        Assert.Empty(_memory.ListPheromoneTrails());
        Assert.Empty(_memory.GetRecentEvents(50, missionId: mission.Id));

        // What the operator wrote down is untouched.
        Assert.NotNull(_memory.LoadProject("p-falcon"));
        Assert.NotNull(_memory.GetUser("zwright"));
    }

    /// <summary>
    /// The table list the wipe clears beyond mission history is named ONCE, in `MemoryWipeTables`,
    /// and every name in it is a table the schema actually creates — a name that matches nothing
    /// would be a promise the wipe silently failed to keep (the `TableExists` guard would skip it).
    /// </summary>
    [Fact]
    public void EveryMemoryWipeTable_ExistsInTheSchema()
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(_dir, "memory.db")))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) tables.Add(rd.GetString(0));
        }
        foreach (var table in SqliteMemory.MemoryWipeTables)
            Assert.True(tables.Contains(table), $"MemoryWipeTables names '{table}', which the schema does not create.");
    }

    // ---- running work is counted from the mission table ---------------------------------------

    [Fact]
    public void CountRunningMissions_SeesAMissionTheJobQueueNeverHeld()
    {
        Assert.Equal(0, _memory.CountRunningMissions());

        // Started the way a chat starts one: a mission row, no job row.
        var running = new Mission { Goal = "how do you make tacos?", Status = MissionStatus.Running };
        _memory.SaveMission(running);
        Assert.Equal(1, _memory.CountRunningMissions());

        running.Status = MissionStatus.Complete;
        _memory.SaveMission(running);
        Assert.Equal(0, _memory.CountRunningMissions());
    }

    // ---- the backup purge -----------------------------------------------------------------------

    [Fact]
    public void DeleteAllBackups_RemovesEveryBackup_AndNothingElse()
    {
        var backups = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(backups);
        File.WriteAllText(Path.Combine(backups, "anthill_20260901T010101.db"), "one");
        File.WriteAllText(Path.Combine(backups, "anthill_20260902T010101.db"), "two");
        File.WriteAllText(Path.Combine(backups, "notes.txt"), "keep me");

        var (deleted, freed) = FileSecurity.DeleteAllBackups(backups, p => p);

        Assert.Equal(2, deleted);
        Assert.Equal(6, freed);
        Assert.Empty(Directory.GetFiles(backups, "anthill_*.db"));
        Assert.True(File.Exists(Path.Combine(backups, "notes.txt")));
    }

    /// <summary>`PruneBackups(keep: 0)` still leaves everything: the purge is a different verb, not a zero.</summary>
    [Fact]
    public void PruneWithKeepZero_StillLeavesEverything()
    {
        var backups = Path.Combine(_dir, "backups2");
        Directory.CreateDirectory(backups);
        File.WriteAllText(Path.Combine(backups, "anthill_20260901T010101.db"), "one");

        var (deleted, _) = FileSecurity.PruneBackups(backups, 0, p => p);

        Assert.Equal(0, deleted);
        Assert.Single(Directory.GetFiles(backups, "anthill_*.db"));
    }

    // ---- the gate is in the handler, before anything is touched ------------------------------

    /// <summary>
    /// Every danger endpoint reads the confirmation FIRST. Read from the host's source, the way the
    /// clear-missions guard is, because the property is about ORDER inside a handler: refusal before
    /// effect, and the refusal names the code the console keys off.
    /// </summary>
    [Theory]
    [InlineData("/maintenance/reset-config", "ResetConfig()")]
    [InlineData("/maintenance/delete-backups", "DeleteAllBackups(")]
    [InlineData("/maintenance/wipe-memory", "WipeColonyMemory()")]
    public void EveryDangerEndpoint_ChecksTheColonyNameBeforeActing(string route, string effect)
    {
        var dashboard = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Api", "ApiHost.Dashboard.cs"));
        var start = dashboard.IndexOf($"\"{route}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{route} is not mapped");
        var body = dashboard[start..Math.Min(dashboard.Length, start + 1800)];

        var gate = body.IndexOf("RequireColonyNameConfirmation(ctx)", StringComparison.Ordinal);
        var act = body.IndexOf(effect, StringComparison.Ordinal);
        Assert.True(gate >= 0, $"{route} never asks for the colony name");
        Assert.True(act >= 0, $"{route} never reaches {effect}");
        Assert.True(gate < act, $"{route} must check the confirmation BEFORE {effect}");

        // The gate itself compares against the runtime's name and refuses with the code the console reads.
        var gateStart = dashboard.IndexOf("RequireColonyNameConfirmation(HttpContext ctx)", StringComparison.Ordinal);
        var gateBody = dashboard[gateStart..Math.Min(dashboard.Length, gateStart + 1200)];
        Assert.Contains("AnthillRuntime.ColonyName", gateBody, StringComparison.Ordinal);
        Assert.Contains("confirmation_mismatch", gateBody, StringComparison.Ordinal);
    }

    /// <summary>The wipe is refused on the MISSION TABLE, not only the job queue.</summary>
    [Fact]
    public void TheWipe_RefusesOnRunningMissions_NotOnlyQueuedJobs()
    {
        var dashboard = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Api", "ApiHost.Dashboard.cs"));
        var start = dashboard.IndexOf("\"/maintenance/wipe-memory\"", StringComparison.Ordinal);
        var body = dashboard[start..Math.Min(dashboard.Length, start + 1800)];

        Assert.Contains("CountRunningMissions()", body, StringComparison.Ordinal);
        Assert.Contains("\"conflict\"", body, StringComparison.Ordinal);
        Assert.True(body.IndexOf("CountRunningMissions()", StringComparison.Ordinal)
                  < body.IndexOf("WipeColonyMemory()", StringComparison.Ordinal),
            "the running-work check must run before anything is deleted");
    }

    // ---- the colony name --------------------------------------------------------------------------

    [Fact]
    public void TheColonyName_IsEditable_Defaulted_AndSurvivesAReset()
    {
        var decl = ConfigCatalog.Find("colony_name");
        Assert.NotNull(decl);
        Assert.Equal(ConfigExposure.Editable, decl!.Exposure);
        Assert.Equal("anthill", decl.Default);

        // The reset preserves it: a reset that renamed the colony would change the password to its
        // own confirmation. `ConfigResetTests` pins the initializer to the returned list; this pins
        // that the name is IN that list.
        var runtime = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Core", "Configuration", "AnthillRuntime.cs"));
        var start = runtime.IndexOf("public static List<string> ResetConfig()", StringComparison.Ordinal);
        var body = runtime[start..Math.Min(runtime.Length, start + 3000)];
        Assert.Contains("ColonyName = old.ColonyName", body, StringComparison.Ordinal);
        Assert.Contains("\"colony_name\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The defaults endpoint serves EDITABLE keys only and nothing else — a default for a key the
    /// page cannot write is a decoration, and a default for a secret would be a leak.
    /// </summary>
    [Fact]
    public void TheDefaultsEndpoint_ProjectsOnlyTheEditableSurface()
    {
        var dashboard = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Api", "ApiHost.Dashboard.cs"));
        var start = dashboard.IndexOf("\"/settings/defaults\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "/settings/defaults is not mapped");
        var body = dashboard[start..Math.Min(dashboard.Length, start + 1400)];

        Assert.Contains("RequireAuth(ctx, \"read_config\")", body, StringComparison.Ordinal);
        Assert.Contains("d.Exposure == ConfigExposure.Editable", body, StringComparison.Ordinal);
        Assert.Contains("ConfigCatalog.Declarations", body, StringComparison.Ordinal);

        // And every editable key has a declared default the endpoint would serve.
        foreach (var key in ConfigCatalog.EditableKeys)
            Assert.NotNull(ConfigCatalog.Find(key));
    }
}
