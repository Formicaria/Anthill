using Anthill.Core.Configuration;
using Anthill.Core.Planning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Long-input / specification-ingestion planning: oversized documents are split into bounded,
/// non-critical section tasks that feed a single critical synthesis task and a verifier.
/// </summary>
public class PlannerTests
{
    [Fact]
    public void IsLongInput_TripsOnlyAboveThreshold()
    {
        AnthillRuntime.Initialize();
        var threshold = AnthillRuntime.LongInputThreshold;
        Assert.False(Planner.IsLongInput(new string('x', threshold)));
        Assert.True(Planner.IsLongInput(new string('x', threshold + 1)));
    }

    [Fact]
    public void SplitIntoSections_RespectsCharCapAndSectionCap()
    {
        var doc = string.Join("\n\n", Enumerable.Range(0, 40).Select(i => $"## Heading {i}\n" + new string('a', 800)));
        var sections = Planner.SplitIntoSections(doc, maxSectionChars: 1000, maxSections: 5);

        Assert.True(sections.Count <= 5);
        Assert.NotEmpty(sections);
        // Every section except the merged tail respects the cap; the tail is truncated to the cap too.
        Assert.All(sections, s => Assert.True(s.Length <= 1000, $"section length {s.Length} exceeded cap"));
    }

    [Fact]
    public void SplitIntoSections_HardSplitsAnOversizedSingleBlock()
    {
        var oneBlock = new string('z', 5000); // no boundaries, no blank lines
        var sections = Planner.SplitIntoSections(oneBlock, maxSectionChars: 1000, maxSections: 10);
        Assert.True(sections.Count >= 5);
        Assert.All(sections, s => Assert.True(s.Length <= 1000));
    }

    [Fact]
    public void CreateSpecIngestionTasks_BuildsSectionsThenSynthesisThenVerify()
    {
        var doc = string.Join("\n\n", Enumerable.Range(0, 6).Select(i => $"RULE_{i}:\n" + new string('q', 1200)));
        var tasks = Planner.CreateSpecIngestionTasks(doc);

        var sectionTasks = tasks.Where(t => t.TaskType == "section_analysis").ToList();
        var synthesis = Assert.Single(tasks.Where(t => t.TaskType == "synthesis"));
        var verify = Assert.Single(tasks.Where(t => t.TaskType == "verification"));

        Assert.NotEmpty(sectionTasks);
        Assert.All(sectionTasks, t =>
        {
            Assert.Equal("researcher", t.AssignedAnt);
            Assert.False(t.Critical); // sections are non-critical
        });

        // Synthesis depends on every section, is critical, and the verifier depends on synthesis.
        Assert.True(synthesis.Critical);
        Assert.Equal(sectionTasks.Select(t => t.Id).OrderBy(x => x), synthesis.DependsOn.OrderBy(x => x));
        Assert.Equal("builder", synthesis.AssignedAnt);
        Assert.Contains(synthesis.Id, verify.DependsOn);
    }
}
