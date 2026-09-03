using Anthill.Core.Configuration;

namespace Anthill.Cli;

/// <summary>
/// REGENERATE THE CONFIGURATION SURFACE. v0.3.8.114 — the developer half of R0's fourth exit-gate
/// clause.
///
/// `ConfigCatalog` is the authority; `config.example.json` and `docs/CONFIGURATION.md` are its
/// output. This writes them. `ConfigCatalogTests` renders the same two artifacts and compares them
/// against what is on disk, so the only way to change either file is to change the declaration it
/// came from — an edit by hand fails the build, which is the property that makes "one authority"
/// true rather than merely intended.
///
/// IT WRITES INTO THE REPOSITORY, NOT INTO A COLONY. Every other command in this CLI operates on the
/// operator's own store; this one operates on the source tree, and confusing the two would let a
/// developer command overwrite a running colony's settings. So it takes the repository root
/// explicitly, verifies it looks like this repository before writing anything, and refuses rather
/// than guessing.
/// </summary>
public static class EmitConfigCommand
{
    public static int Run(string[] args)
    {
        var root = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
                   ?? FindRepositoryRoot();

        if (root is null)
        {
            Console.Error.WriteLine(
                "Could not find the repository root from the working directory.\n"
              + "Usage: anthill --emit-config [<repository-root>]");
            return 2;
        }

        // A directory that is not this repository is a refusal, not a best effort. The alternative
        // is writing two files into somebody's home directory and reporting success.
        var example = Path.Combine(root, "config.example.json");
        var marker = Path.Combine(root, "Anthill.sln");
        if (!File.Exists(marker))
        {
            Console.Error.WriteLine($"'{root}' does not look like the ANTHILL repository (no Anthill.sln).");
            return 2;
        }

        var reference = Path.Combine(root, "docs", "CONFIGURATION.md");
        Directory.CreateDirectory(Path.GetDirectoryName(reference)!);

        var wrote = 0;
        wrote += WriteIfChanged(example, ConfigCatalog.RenderExampleJson()) ? 1 : 0;
        wrote += WriteIfChanged(reference, ConfigCatalog.RenderMarkdown()) ? 1 : 0;

        Console.WriteLine(wrote == 0
            ? $"Both artifacts already match the catalog ({ConfigCatalog.Declarations.Count} settings)."
            : $"Regenerated {wrote} of 2 artifacts from {ConfigCatalog.Declarations.Count} declarations.");

        return 0;
    }

    /// <summary>
    /// Only touch a file whose content actually changed — an unchanged mtime keeps incremental
    /// builds from rebuilding the world every time somebody runs this out of habit.
    /// </summary>
    private static bool WriteIfChanged(string path, string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            if (string.Equals(existing, normalized, StringComparison.Ordinal))
            {
                Console.WriteLine($"  unchanged  {Path.GetFileName(path)}");
                return false;
            }
        }

        // LF deliberately, on every platform. These files are compared byte-for-byte by a test that
        // runs on Windows agents and Linux ones, and a CRLF checkout writing CRLF back would make
        // the comparison pass locally and fail in CI, or the reverse.
        File.WriteAllText(path, normalized);
        Console.WriteLine($"  wrote      {Path.GetFileName(path)}");
        return true;
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Anthill.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }
}
