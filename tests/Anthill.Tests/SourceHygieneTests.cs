using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Source files are text, so the tools that read them can read them. v0.3.8.76.
///
/// FOUND WHILE DOING SOMETHING ELSE, which is the only way this one could be found.
/// `src/Anthill.Core/Agents/AntModelFitness.cs` contained a raw NUL byte — inside a string literal,
/// as a cache-key separator, written as the byte rather than as the `\0` escape:
///
///     var key = provider + "&lt;NUL&gt;" + configured;
///
/// The compiler is perfectly happy with it and the runtime behaviour is correct. What it breaks is
/// everything else. `grep`, `ripgrep` and `git grep` classify a file containing a NUL as BINARY and
/// silently skip it — not an error, no output, just absence. So did `git diff`. For as long as that
/// byte was there, `AntModelFitness.cs` was invisible to every text sweep run over this repository:
/// every "does any source file still mention X" check, every audit of call sites, every review pass
/// that starts with a search. A file that answers "no match" to every question is worse than one
/// that is missing, because absence of a match reads as absence of the thing.
///
/// It was found only because a fitness-report change happened to grep for a symbol that lives in
/// that file and got nothing back. That is luck, and this file is what replaces the luck.
///
/// THE SAME DEFECT CLASS THIS REPOSITORY KEEPS NAMING: a diagnostic that breaks what it describes.
/// The suite's source-reading guards use `File.ReadAllText`, which handles NULs fine — so the C#
/// tests would have kept passing over this file forever while every human and agent sweep skipped
/// it. The tests and the tools disagreed about what the repository contains.
/// </summary>
public class SourceHygieneTests
{
    private static IEnumerable<string> SourceFiles() =>
        new[] { "src", "tests" }
            .Select(d => Path.Combine(SourceText.RepoRoot(), d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>
    /// No source file contains a byte that makes text tools treat it as binary.
    ///
    /// NUL is the one that matters — it is what `grep` and `git` actually test for. The other C0
    /// control characters are included because they are equally invisible in an editor and equally
    /// surprising inside a literal, and because the fix is always the same: write the escape.
    /// Tab, carriage return and newline are excluded, being ordinary source bytes.
    /// </summary>
    [Fact]
    public void NoSourceFile_ContainsAControlByteThatMakesItBinary()
    {
        var offenders = new List<string>();

        foreach (var path in SourceFiles())
        {
            var bytes = File.ReadAllBytes(path);

            for (var i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                if (b >= 0x20 || b is 0x09 or 0x0A or 0x0D) continue;

                var line = bytes.Take(i).Count(x => x == 0x0A) + 1;
                offenders.Add(
                    $"{Path.GetRelativePath(SourceText.RepoRoot(), path)}:{line} contains 0x{b:X2}");
                break;   // one report per file; the first occurrence is enough to go and look
            }
        }

        Assert.True(offenders.Count == 0,
            "these source files contain raw control bytes: " + string.Join("; ", offenders)
          + ". Write the escape instead (\"\\0\", \"\\u0001\"): the value is identical and the file "
          + "stays text. A file with a NUL in it is skipped in silence by grep, ripgrep and git "
          + "grep, so it answers \"no match\" to every search anyone ever runs over this repository "
          + "— including the searches used to decide whether a symbol is still referenced.");
    }
}
