namespace Anthill.Tests;

/// <summary>
/// The source text of <c>ApiHost</c> — all of it. v3.8.17.
///
/// A dozen guards across seven test classes ask questions of the API host by reading its SOURCE:
/// does this endpoint exist, is the sanitizer wired into that handler, does the disabled-tool
/// projection say "disabled" rather than "rejected". They did it by reading
/// <c>src/Anthill.Api/ApiHost.cs</c>, which was correct while that file was the whole class.
///
/// Phase 6 split it into seven files and nine of those guards went red at once. That is the good
/// outcome — they were load-bearing and they said so immediately. The bad outcome was already
/// present and silent: <c>RuntimeCompositionTests</c> asserts ZERO occurrences of
/// <c>MissionConstraints.Parse</c> in the host, and after the split that passed by looking in the
/// wrong file. A guard that reads one file of a partial class is a guard that a future split turns
/// into decoration.
///
/// So this reads EVERY <c>ApiHost*.cs</c> under <c>src/Anthill.Api</c>, including the homelab
/// partials, sorted by path so the concatenation is deterministic and slice-based assertions behave
/// the same on every machine. Adding a partial cannot silently narrow what these guards see.
/// </summary>
internal static class ApiHostSource
{
    /// <summary>Walk up to the repo root, so the helper does not depend on the runner's cwd.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    /// <summary>
    /// Every partial of the class, concatenated in path order.
    ///
    /// Slice-based guards — "find <c>/autonomy/start</c>, then read to <c>/autonomy/stop</c>" —
    /// still work, because whole methods moved intact and an endpoint's neighbours moved with it.
    /// What would break such a guard is a partial that splits one method across two files, which is
    /// not a thing C# permits.
    /// </summary>
    internal static string All()
    {
        var apiDir = Path.Combine(RepoRoot(), "src", "Anthill.Api");
        var files = Directory.GetFiles(apiDir, "ApiHost*.cs", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0)
            throw new InvalidOperationException(
                $"No ApiHost*.cs found under {apiDir}. These guards read the host's source; finding "
              + "none means they would pass vacuously.");

        return string.Join("\n", files.Select(File.ReadAllText));
    }
}
