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
}
