using Anthill.Core.Common;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Backup retention (v1.8.15.4) — the fix for the runaway backup directory (a full DB copy is
/// written before every mission). PruneBackups must keep exactly the newest N and delete the
/// rest, reporting how many files and bytes it freed.
/// </summary>
public class MaintenanceTests : IDisposable
{
    private readonly string _dir;
    private static string Id(string p) => p; // identity path resolver for the test dir

    public MaintenanceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_backups_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private void MakeBackups(int n)
    {
        for (var i = 0; i < n; i++)
            // Timestamped names sort chronologically, matching the real BackupDb naming.
            File.WriteAllText(Path.Combine(_dir, $"anthill_202601{i:D2}_120000.db"), new string('x', 100));
    }

    [Fact]
    public void PruneBackups_KeepsNewestN_AndReportsFreed()
    {
        MakeBackups(10);
        var (deleted, freed) = FileSecurity.PruneBackups(_dir, keep: 3, Id);
        Assert.Equal(7, deleted);
        Assert.Equal(700, freed); // 7 files * 100 bytes
        var remaining = Directory.GetFiles(_dir, "anthill_*.db").Select(Path.GetFileName).OrderBy(x => x).ToArray();
        // The three newest (highest-sorting) names survive.
        Assert.Equal(new[] { "anthill_20260107_120000.db", "anthill_20260108_120000.db", "anthill_20260109_120000.db" }, remaining);
    }

    [Fact]
    public void PruneBackups_KeepZeroOrFewer_IsNoOp()
    {
        MakeBackups(5);
        Assert.Equal((0, 0L), FileSecurity.PruneBackups(_dir, keep: 0, Id));
        Assert.Equal(5, Directory.GetFiles(_dir, "anthill_*.db").Length);
    }

    [Fact]
    public void PruneBackups_FewerThanKeep_DeletesNothing()
    {
        MakeBackups(2);
        var (deleted, freed) = FileSecurity.PruneBackups(_dir, keep: 10, Id);
        Assert.Equal(0, deleted);
        Assert.Equal(0, freed);
    }

    [Fact]
    public void BackupStats_ReportsCountAndBytes()
    {
        MakeBackups(4);
        var (count, bytes) = FileSecurity.BackupStats(_dir, Id);
        Assert.Equal(4, count);
        Assert.Equal(400, bytes);
    }
}
