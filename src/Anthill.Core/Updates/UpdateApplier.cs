using System.IO.Compression;

namespace Anthill.Core.Updates;

/// <summary>
/// APPLYING A STAGED UPDATE, AND THE ONE RULE THAT OUTRANKS SHIPPING IT. v0.3.8.146.
///
/// The rule: <b>never remove a path this release does not itself replace.</b> Not "try not to",
/// not "unless it looks like a leftover" — never. It is written as a filter over the archive's own
/// entries rather than as a delete-then-extract, because those two are the same operation only
/// when nothing else lives in the directory, and on the portable shape something else always does:
/// the colony's entire memory, in `.anthill`, beside the binary that is being replaced.
///
/// README has promised since long before this feature that a colony's data "survives every update,
/// reinstall, and uninstall". An updater that ran `rm -rf` on its own directory would break that
/// promise silently, on someone else's machine, at a moment nobody was watching — which is exactly
/// the combination that makes unattended software frightening. So the portable path enumerates
/// what the new archive contains and overwrites precisely that set, and refuses outright if the
/// data directory ever appears inside it.
///
/// WHY APPLYING HAPPENS AT LAUNCH rather than at download. A running process cannot replace its
/// own files on Windows at all, and on Linux the systemd unit makes `bin/` read-only to the
/// service by design. Both constraints point the same way: download and verify while running,
/// swap while not. It also means an update never interrupts a mission — the colony finishes what
/// it is doing, and the new version is what starts next time.
/// </summary>
public static class UpdateApplier
{
    /// <summary>What an apply attempt did. `Applied` false with `Fatal` false means "left as it was".</summary>
    public sealed record ApplyResult(bool Applied, string Message, bool Fatal = false);

    /// <summary>
    /// Replaces the program files with the contents of a verified archive, entry by entry.
    ///
    /// Used by the portable Windows shape and by the Linux service's pre-start swap. The Windows
    /// INSTALLED shape does not come through here — it runs the installer, which owns shortcuts,
    /// uninstall registration and its own file list.
    /// </summary>
    public static ApplyResult ApplyArchive(InstallSite site, string archivePath)
    {
        if (!File.Exists(archivePath))
            return new ApplyResult(false, $"the staged update is gone from {archivePath}; nothing was changed.");

        var program = Path.GetFullPath(site.ProgramDirectory);
        var data = Path.GetFullPath(site.DataDirectory);

        try
        {
            var entries = ReadEntries(archivePath);
            if (entries.Count == 0)
                return new ApplyResult(false, "the staged update contained no files; nothing was changed.");

            // THE REFUSAL THAT PROTECTS THE COLONY. If any entry would land inside the data
            // directory, this is not an update we understand, and the safe answer is to do nothing
            // at all rather than to skip the offending entry and apply the rest.
            foreach (var entry in entries)
            {
                var target = Path.GetFullPath(Path.Combine(program, entry));
                if (!target.StartsWith(program, StringComparison.Ordinal))
                    return new ApplyResult(false,
                        $"the staged update tried to write outside the program directory ('{entry}'); "
                      + "nothing was changed.", Fatal: true);
                if (IsInside(target, data))
                    return new ApplyResult(false,
                        $"the staged update tried to write into the colony's data directory ('{entry}'); "
                      + "nothing was changed.", Fatal: true);
            }

            Extract(archivePath, program);
            return new ApplyResult(true, $"replaced {entries.Count} program file(s) from {Path.GetFileName(archivePath)}.");
        }
        catch (Exception error)
        {
            return new ApplyResult(false, $"the staged update could not be applied ({error.Message}).", Fatal: true);
        }
    }

    /// <summary>Is <paramref name="candidate"/> the directory <paramref name="parent"/>, or inside it?</summary>
    public static bool IsInside(string candidate, string parent)
    {
        var c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return c.Equals(p, comparison)
            || c.StartsWith(p + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>The archive's file entries, normalised to relative paths. Directories are not entries.</summary>
    public static List<string> ReadEntries(string archivePath)
    {
        var names = new List<string>();
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archivePath);
            foreach (var entry in zip.Entries)
                if (!string.IsNullOrEmpty(entry.Name)) names.Add(entry.FullName);
            return names;
        }
        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            foreach (var name in TarNames(gzip)) names.Add(name);
            return names;
        }
        throw new NotSupportedException($"'{Path.GetFileName(archivePath)}' is not an archive this updater understands.");
    }

    private static void Extract(string archivePath, string destination)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archivePath);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;   // a directory entry
                var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
            return;
        }
        System.Formats.Tar.TarFile.ExtractToDirectory(
            DecompressToTemp(archivePath), destination, overwriteFiles: true);
    }

    private static string DecompressToTemp(string archivePath)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"anthill-update-{Guid.NewGuid():N}.tar");
        using (var file = File.OpenRead(archivePath))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var output = File.Create(temp))
            gzip.CopyTo(output);
        return temp;
    }

    private static IEnumerable<string> TarNames(Stream tar)
    {
        using var reader = new System.Formats.Tar.TarReader(tar, leaveOpen: true);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is System.Formats.Tar.TarEntryType.Directory) continue;
            var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
            if (!string.IsNullOrWhiteSpace(name)) yield return name;
        }
    }
}
