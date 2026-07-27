using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Anthill.Core.Outcomes;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;
using DomainTaskStatus = Anthill.Core.Domain.TaskStatus;

namespace Anthill.Tests;

/// <summary>
/// v2.20.0: the archivist's memory candidates finally have a consumer. Since Stage D-6 the ant has
/// emitted `memory_candidate` artifacts — declared in its execution contract — and nothing ingested
/// them: built, serialised, dropped. The fourth instance of the "tested code with no call site"
/// pattern (v2.14.12, SanitizeInto, /missions/json, HandoffGate.Evaluate). These tests pin the
/// extraction semantics AND the call site itself.
/// </summary>
public class MemoryCandidateIngestTests
{
    // ---- extraction, fed by the REAL archivist output ------------------------------------------

    private static AntExecutionResult RealArchivalResult()
    {
        var t = new DomainTask { Title = "Archive", Description = "archive the mission", AssignedAnt = "archivist", TaskType = "memory_consolidation" };
        var m = new Mission { Goal = "improve the widget", Status = MissionStatus.Complete };
        m.Tasks.Add(new DomainTask { Title = "build", AssignedAnt = "builder", Status = DomainTaskStatus.Complete, Result = "built" });
        m.Tasks.Add(new DomainTask { Title = "verify", AssignedAnt = "verifier", Status = DomainTaskStatus.Complete, Result = "PASS: verified" });
        m.Tasks.Add(t);
        return new ArchivistAnt().Execute(t, m);
    }

    [Fact]
    public void ExtractsEveryCandidate_FromTheRealArchivistOutput()
    {
        var candidates = MemoryCandidateIngest.Extract(RealArchivalResult());
        Assert.True(candidates.Count >= 2); // episodic + positive procedural (verified mission)
        Assert.Contains(candidates, c => c.MemoryClass == "episodic");
        Assert.Contains(candidates, c => c.MemoryClass == "procedural_candidate");
        Assert.All(candidates, c => Assert.False(string.IsNullOrWhiteSpace(c.SourceMission)));
    }

    [Fact]
    public void NothingExtracted_ClaimsPromotability()
    {
        // auto_promote=false end to end: the archivist writes it, extraction preserves it, and the
        // stored event records it. Certification belongs to the evaluation pipeline alone.
        Assert.All(MemoryCandidateIngest.Extract(RealArchivalResult()), c => Assert.False(c.AutoPromote));
    }

    [Fact]
    public void MalformedArtifacts_YieldZeroCandidates_NotAnException()
    {
        var broken = AntExecutionResult.Succeeded("archived") with
        {
            Artifacts = new List<AntArtifact>
            {
                new(MemoryCandidateIngest.ArtifactKind, "bad json", "{not json"),
                new(MemoryCandidateIngest.ArtifactKind, "wrong shape", "{\"a\":1}"),
                new(MemoryCandidateIngest.ArtifactKind, "missing fields", "[{\"confidence\":\"high\"}]"),
            },
        };
        Assert.Empty(MemoryCandidateIngest.Extract(broken));
    }

    [Fact]
    public void ResultsWithoutTheArtifact_YieldNothing()
    {
        Assert.Empty(MemoryCandidateIngest.Extract(AntExecutionResult.Succeeded("plain text result")));
        Assert.Empty(MemoryCandidateIngest.Extract(null));
    }

    [Fact]
    public void EventMetadata_CarriesProvenanceAndTheUnactionedPromotabilityFlag()
    {
        var c = MemoryCandidateIngest.Extract(RealArchivalResult()).First();
        var meta = MemoryCandidateIngest.EventMetadata(c);
        Assert.Equal(c.MemoryClass, meta["memory_class"]);
        Assert.Equal(c.SourceMission, meta["source_mission"]);
        Assert.False((bool)meta["auto_promote"]!);
    }

    // ---- the call site -------------------------------------------------------------------------

    /// <summary>
    /// The lesson this codebase keeps re-teaching: a test that exercises Extract proves Extract
    /// works — it does not prove anything CALLS it. This asserts, against comment-stripped Queen
    /// source, that the archivist completion path feeds the ingester.
    /// </summary>
    [Fact]
    public void TheQueen_ActuallyIngests_OnArchivistCompletion()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs"));
        var code = string.Join("\n", source.Split('\n')
            .Select(line =>
            {
                var i = line.IndexOf("//", StringComparison.Ordinal);
                return i >= 0 ? line[..i] : line;
            }));
        Assert.Contains("IngestMemoryCandidates(mission, task, execution)", code);
        Assert.Contains("MemoryCandidateIngest.Extract", code);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
