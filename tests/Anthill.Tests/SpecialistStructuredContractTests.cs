using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.19.0 Stage 4 completion guard. Every specialist now returns a structured
/// AntExecutionResult; the UI_MAP_JSON compatibility adapter that stringified results into prose
/// has been deleted. That adapter was the mechanism behind the most severe defect in the audit:
/// a specialist could report failed_retryable, have it flattened into text nobody parsed, and be
/// recorded as a completed task — which graded the mission a success and let patches auto-apply.
///
/// This asserts against CODE, not prose: migration comments legitimately name the adapter they
/// removed, and a comment mentioning Compat is not the same as code calling it.
/// </summary>
public class SpecialistStructuredContractTests
{
    private static string SourceFile() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Agents", "SpecialistAnts.cs"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string CodeOnly(string src) => string.Join("\n", src.Split('\n')
        .Select(line =>
        {
            var i = line.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? line[..i] : line;
        }));

    [Fact]
    public void CompatibilityAdapter_IsGone_AndCannotComeBack()
    {
        var code = CodeOnly(SourceFile());
        Assert.DoesNotContain("Compat(", code);
        Assert.DoesNotContain("UI_MAP_JSON", code);
    }

    [Fact]
    public void EverySpecialist_ReturnsStructuredResults()
    {
        var src = SourceFile();
        var classes = System.Text.RegularExpressions.Regex
            .Matches(src, @"public sealed class (\w+Ant) : BaseAnt")
            .Select(m => m.Groups[1].Value).ToList();

        // Guards the guard: if the file is ever restructured so this finds nothing, the test must
        // fail loudly rather than vacuously pass over an empty list.
        Assert.True(classes.Count >= 6, $"expected the six specialists, found {classes.Count}");

        foreach (var name in classes)
        {
            var start = src.IndexOf($"public sealed class {name} : BaseAnt", StringComparison.Ordinal);
            var next = src.IndexOf("public sealed class ", start + 20, StringComparison.Ordinal);
            var body = CodeOnly(next > start ? src[start..next] : src[start..]);
            Assert.True(body.Contains("public override AntExecutionResult Execute"),
                $"{name} still relies on the string-only path; mission control cannot read its outcome.");
        }
    }
}
