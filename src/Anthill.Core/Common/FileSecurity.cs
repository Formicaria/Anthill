using System.Runtime.InteropServices;

namespace Anthill.Core.Common;

/// <summary>
/// Best-effort file permission hardening and DB snapshotting.
///
/// On POSIX this chmod-600s files that hold local runtime state (the SQLite DB and
/// its WAL/SHM siblings, backups). On Windows the POSIX bits are meaningless, so we
/// fall back to clearing inherited ACLs is left to the OS default and the call is a
/// no-op — exactly the portable, never-fatal behaviour of the Python harden helper.
/// </summary>
public static class FileSecurity
{
    public static void HardenFilePermissions(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Owner read/write only (0o600).
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // Permission hardening is best-effort and must never abort startup.
        }
    }

    /// <summary>
    /// Copies the SQLite DB to the backup directory with a UTC timestamp suffix.
    /// Returns the backup path on success, or null when there is nothing to copy.
    /// Called automatically at mission start so every run has a pre-mission snapshot.
    /// </summary>
    public static string? BackupDb(string dbPath, string backupDir, Func<string, string> pathResolver)
    {
        try
        {
            var src = pathResolver(dbPath);
            var dstDir = pathResolver(backupDir);
            if (!File.Exists(src)) return null;
            Directory.CreateDirectory(dstDir);
            var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var dst = Path.Combine(dstDir, $"anthill_{ts}.db");
            File.Copy(src, dst, overwrite: true);
            HardenFilePermissions(dst);
            return dst;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps only the newest <paramref name="keep"/> DB backups in the backup directory, deleting
    /// the rest. A full DB copy is written before every mission (<see cref="BackupDb"/>), so without
    /// this the backup dir grows without bound — the primary cause of disk bloat. Returns how many
    /// files were deleted and how many bytes that freed. keep &lt;= 0 leaves everything untouched.
    /// </summary>
    public static (int Deleted, long BytesFreed) PruneBackups(string backupDir, int keep, Func<string, string> pathResolver)
    {
        if (keep <= 0) return (0, 0);
        try
        {
            var dir = pathResolver(backupDir);
            if (!Directory.Exists(dir)) return (0, 0);
            var backups = new DirectoryInfo(dir).GetFiles("anthill_*.db")
                .OrderByDescending(f => f.Name) // timestamped name sorts chronologically
                .ToList();
            var deleted = 0; long freed = 0;
            foreach (var f in backups.Skip(keep))
            {
                var size = f.Length;
                try { f.Delete(); deleted++; freed += size; } catch { /* skip locked/removed */ }
            }
            return (deleted, freed);
        }
        catch { return (0, 0); }
    }

    /// <summary>Total size (bytes) and file count of the DB backup directory — for maintenance stats.</summary>
    public static (int Count, long Bytes) BackupStats(string backupDir, Func<string, string> pathResolver)
    {
        try
        {
            var dir = pathResolver(backupDir);
            if (!Directory.Exists(dir)) return (0, 0);
            var files = new DirectoryInfo(dir).GetFiles("anthill_*.db");
            return (files.Length, files.Sum(f => f.Length));
        }
        catch { return (0, 0); }
    }
}
