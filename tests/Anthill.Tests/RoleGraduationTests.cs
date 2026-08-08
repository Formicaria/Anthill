using System.Text.RegularExpressions;
using Anthill.Core.Agents;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Stage D role graduation. v3.8.28.
///
/// Three roles were declared complete and were quietly repository-specific or prose-driven: the
/// tester only knew .NET, the cartographer could only map ANTHILL, and the scribe never dispatched
/// the one tool its contract grants it. These are source-level guards, because the defects are about
/// what the code REACHES FOR rather than what it computes — and a unit test over a stub would have
/// passed against every one of them.
/// </summary>
public class RoleGraduationTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string SpecialistSource() => File.ReadAllText(
        Path.Combine(Root(), "src", "Anthill.Core", "Agents", "SpecialistAnts.cs"));

    /// <summary>
    /// The cartographer's probe list and its read budget must agree.
    ///
    /// Written because the first draft of the widening set the constant to twelve against a list of
    /// thirteen, which silently drops the last probe. An off-by-one here does not fail anything — it
    /// just means one conventional layout is never looked for, on every project, forever.
    /// </summary>
    [Fact]
    public void TheCartographersProbeBudget_MatchesItsProbeList()
    {
        var source = SpecialistSource();

        var listBlock = Regex.Match(source, @"foreach \(var known in new\[\]\s*\{(.*?)\}\)", RegexOptions.Singleline);
        Assert.True(listBlock.Success, "the cartographer's known-layout probe list could not be found");

        var probes = Regex.Matches(listBlock.Groups[1].Value, "\"([^\"]+)\"").Count;
        var declared = int.Parse(Regex.Match(source, @"MaxLayoutProbes = (\d+)").Groups[1].Value);

        Assert.Equal(probes, declared);
    }

    /// <summary>
    /// The cartographer must not be ANTHILL-only. It probed exactly two hard-coded `src/Anthill.UI/`
    /// paths, so pointed at any other project it added two files that do not exist and mapped
    /// whatever the top-level listing happened to catch.
    /// </summary>
    [Fact]
    public void TheCartographer_ProbesGenericLayoutsNotOnlyAnthill()
    {
        var listBlock = Regex.Match(SpecialistSource(),
            @"foreach \(var known in new\[\]\s*\{(.*?)\}\)", RegexOptions.Singleline).Groups[1].Value;

        var probes = Regex.Matches(listBlock, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        var anthillSpecific = probes.Where(p => p.Contains("Anthill", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.Contains("index.html", probes);
        Assert.Contains("src/main.ts", probes);        // a Node/TypeScript entry point
        Assert.True(probes.Count - anthillSpecific.Count >= 8,
            "the probe list is still mostly ANTHILL-specific: " + string.Join(", ", probes));
    }

    /// <summary>
    /// The tester must select from the WORKSPACE MANIFEST, not only the compiled catalog. Its
    /// default was `{ dotnet_version, dotnet_build }`, which on a Node or Python project runs the
    /// wrong toolchain and reports a failure that says more about the colony than about the code.
    /// </summary>
    [Fact]
    public void TheTester_SelectsChecksFromTheWorkspaceManifest()
    {
        var source = SpecialistSource();
        var tester = source[source.IndexOf("class TesterAnt", StringComparison.Ordinal)..];

        Assert.Contains("WorkspaceCapabilityManifest.ForCurrentMission()", tester);
    }

    /// <summary>
    /// The scribe must DISPATCH `read_changed_files_summary`. It is the only tool its contract
    /// grants, it was built for this role in v3.5.0, and the scribe inferred changed files by regex
    /// over other ants' prose instead — so a file merely discussed was reported as changed and a
    /// file changed but unmentioned was invisible.
    /// </summary>
    [Fact]
    public void TheScribe_DispatchesItsOnlyTool()
    {
        var source = SpecialistSource();
        var scribe = source[source.IndexOf("class ScribeAnt", StringComparison.Ordinal)..];

        Assert.Contains("RunTool(\"read_changed_files_summary\"", scribe);
    }

    /// <summary>
    /// And that tool is one the contract actually allows — a dispatch the authorization layer would
    /// deny is worse than no dispatch, because it looks like an attempt and fails at runtime.
    /// </summary>
    [Fact]
    public void TheScribesToolIsInItsContract() =>
        Assert.Contains("read_changed_files_summary",
            AntExecutionCatalog.ContractFor("scribe")!.AllowedTools);
}
