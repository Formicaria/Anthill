using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.8.2 — model fitness is judged against DISCOVERED capabilities, never the declared table.
///
/// The defect, exactly. The fitness report ran inside the Queen's constructor; the capability cache
/// was warmed by a background task started afterwards. So the report evaluated every route against
/// the hand-written name table — which, by this repository's own record, "called gemma4:31b
/// text-only when Ollama reports tools AND thinking". On a colony routed to that model it named five
/// roles as broken on every single restart, and every one of those warnings was wrong.
///
/// Two consequences, and the second is why this is a defect rather than a cosmetic slip. The startup
/// log and the Tools &amp; Routing panel gave DIFFERENT answers about the same model, because /tools
/// computes fitness on request, by which time the cache is warm — a colony contradicting itself. And
/// an alarm that is wrong on every restart is one an operator learns to scroll past, which costs
/// nothing at all until the day it is right.
///
/// These are source-order assertions because the bug WAS an ordering: the code was correct, the
/// data it read was not yet there. Nothing about the fitness calculation itself could have caught
/// it, and no unit test of AntModelFitness would have failed.
/// </summary>
public class ModelFitnessReportingOrderTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repository root");
        return dir!.FullName;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    /// <summary>
    /// The constructor must NOT report fitness. It runs before anything has asked Ollama what its
    /// models can do, so a report from there is a report about the declared table.
    /// </summary>
    [Fact]
    public void TheQueenConstructor_DoesNotReportFitness()
    {
        var queen = Read("src", "Anthill.Core", "Orchestration", "Queen.cs");

        var ctor = Regex.Match(queen, @"public Queen\(.*?\n    \}", RegexOptions.Singleline).Value;
        Assert.NotEqual("", ctor);

        Assert.DoesNotContain("AntModelFitness.CheckAll", ctor);
        Assert.DoesNotContain("model-fitness", ctor);
    }

    /// <summary>But it must still exist as a callable report — removing it is not the fix.</summary>
    [Fact]
    public void TheFitnessReport_IsStillAvailable() =>
        Assert.Contains("public void ReportModelFitness", Read("src", "Anthill.Core", "Orchestration", "Queen.cs"));

    /// <summary>
    /// And every caller must warm the cache FIRST. Asserted on relative position rather than mere
    /// presence: both calls being in the file is exactly the state that shipped the bug.
    /// </summary>
    [Theory]
    [InlineData("src/Anthill.Api/ApiHost.cs")]
    [InlineData("src/Anthill.Cli/Program.cs")]
    public void EveryCaller_WarmsTheCacheBeforeReporting(string relativePath)
    {
        var source = Read(relativePath.Split('/'));

        var warm = source.IndexOf("OllamaCapabilityCache.Warm", StringComparison.Ordinal);
        var report = source.IndexOf("ReportModelFitness", StringComparison.Ordinal);

        Assert.True(warm >= 0, $"{relativePath} never warms the capability cache");
        Assert.True(report >= 0, $"{relativePath} never reports model fitness");
        Assert.True(warm < report,
            $"{relativePath} reports fitness before warming the capability cache, so the report "
          + "describes the declared name table rather than what the provider actually serves — "
          + "which is the v3.8.2 defect, restored.");
    }
}
