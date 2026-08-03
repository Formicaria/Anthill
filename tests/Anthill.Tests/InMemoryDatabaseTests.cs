using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.7.1 — ":memory:" means an in-memory database, on every platform.
///
/// Found by CI on windows-latest, not by any local run, and the local behaviour was the worse of
/// the two. SqliteMemory treated ":memory:" as an ordinary relative path. On Linux ':' is a legal
/// filename character, so every caller asking for a throwaway database silently got a FILE named
/// ":memory:" in the binary directory — on disk, shared by all of them, surviving between runs. The
/// tests passed while testing something other than what they claimed. On Windows ':' is illegal in
/// a filename and the same line threw "SQLite Error 14: unable to open database file", which is the
/// only reason anyone looked.
///
/// The lesson worth keeping is the one about green tests: four test classes believed they had
/// private in-memory storage and were in fact sharing one file. Nothing failed. Cross-platform CI
/// was the only thing standing between that and a much stranger bug later.
/// </summary>
public class InMemoryDatabaseTests
{
    [Theory]
    [InlineData(":memory:", true)]
    [InlineData(" :memory: ", true)]
    [InlineData(":MEMORY:", true)]
    [InlineData("memory", false)]
    [InlineData("/var/lib/anthill/memory.db", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheExactSpelling_MeansInMemory(string? candidate, bool expected) =>
        Assert.Equal(expected, SqliteMemory.IsInMemoryRequest(candidate));

    /// <summary>
    /// A path that merely CONTAINS "memory" is a real path someone meant. Redirecting it to storage
    /// that evaporates on dispose would be far worse than any error it might otherwise produce.
    /// </summary>
    [Fact]
    public void APathThatMerelyMentionsMemory_IsStillAFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anthill-mem-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            using var memory = new SqliteMemory(Path.Combine(dir, "memory.db"));

            Assert.False(memory.IsInMemory);
            Assert.True(File.Exists(memory.DbPath));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void AnInMemoryDatabase_WritesNothingToDisk()
    {
        using var memory = new SqliteMemory(":memory:");

        Assert.True(memory.IsInMemory);
        Assert.False(File.Exists(memory.DbPath));
        Assert.Equal(0, memory.DatabaseFileBytes());

        // The literal is never used as the database name. Asserting `!File.Exists(":memory:")`
        // instead was tempting and wrong: it passes or fails on whether an OLD build happened to
        // leave that file in the working directory, so it would have reported this defect as fixed
        // on any machine that had simply never run the broken version. A test whose result depends
        // on leftover state is not measuring the code.
        Assert.DoesNotContain(":memory:", memory.DbPath);
    }

    /// <summary>
    /// The schema must survive past the constructor. Connect() opens and closes a connection per
    /// operation, and SQLite destroys an in-memory database when its last connection closes — so
    /// without the retained connection this reads from a database that was rebuilt empty, which
    /// fails in a way that looks like a data bug rather than a lifetime bug.
    /// </summary>
    [Fact]
    public void AnInMemoryDatabase_SurvivesBetweenOperations()
    {
        using var memory = new SqliteMemory(":memory:");

        // The mission is saved first because events carry a foreign key to missions. Worth stating:
        // the FK firing here is proof the SCHEMA is present on the second connection, which is
        // exactly the property under test — a database rebuilt empty would have no constraint to
        // violate and would have accepted the orphan row silently.
        memory.SaveMission(new Anthill.Core.Domain.Mission { Id = "m1", Goal = "goal" });
        memory.LogEvent("m1", "test_event", "written to memory");

        Assert.NotEmpty(memory.GetRecentEvents(eventType: "test_event", missionId: "m1"));
    }

    /// <summary>
    /// Two in-memory instances must not see each other.
    ///
    /// This is the property the old code destroyed twice over: shared-cache in-memory databases are
    /// keyed BY NAME, so had the fix simply passed ":memory:" through, every instance would still
    /// have shared one database — the same coupling as the file, just harder to notice. The unique
    /// name per instance is what actually buys the isolation callers assume they already had.
    /// </summary>
    [Fact]
    public void TwoInMemoryDatabases_AreIsolatedFromEachOther()
    {
        using var first = new SqliteMemory(":memory:");
        using var second = new SqliteMemory(":memory:");

        first.SaveMission(new Anthill.Core.Domain.Mission { Id = "m1", Goal = "goal" });
        first.LogEvent("m1", "only_in_first", "");

        Assert.NotEqual(first.DbPath, second.DbPath);
        Assert.Empty(second.GetRecentEvents(eventType: "only_in_first", missionId: "m1"));
        // The mission itself must not be visible either — a shared database would show both.
        Assert.Null(second.GetMission("m1"));
    }
}
