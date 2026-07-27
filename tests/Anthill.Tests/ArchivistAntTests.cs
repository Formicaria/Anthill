using System.Text.Json;
using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;
using DomainTaskStatus = Anthill.Core.Domain.TaskStatus;

namespace Anthill.Tests;

/// <summary>
/// Stage D-6 validation gate (spec §15 ARCHIVISTANT): positive procedural memory ONLY from
/// completed_verified; completed-but-unverified and partial NEVER reinforce positively; failures
/// produce negative lessons; cancellation is neutral; secrets are redacted; provenance preserved;
/// nothing auto-promotes; gates control executability.
///
/// v2.19.0: ArchivistAnt returns a structured AntExecutionResult. These assertions previously
/// substring-matched the Compat() blob, which concatenated the summary and the serialised
/// candidates — so a match could come from either and the test could not say which. They now read
/// the memory_candidate artifact (the machine record) or the narrative (the operator record)
/// deliberately, and the learning rules are asserted against parsed candidates rather than
/// substrings.
/// </summary>
[Collection("specialist-gates")]
public class ArchivistAntTests
{
    private static AntExecutionResult Archive(Mission m, string desc = "archive the mission")
    {
        var t = new DomainTask { Title = "Archive", Description = desc, AssignedAnt = "archivist", TaskType = "memory_consolidation" };
        m.Tasks.Add(t);
        return new ArchivistAnt().Execute(t, m);
    }

    private static Mission Terminal(MissionStatus status, bool verifierPassed = false)
    {
        var m = new Mission { Goal = "improve the widget", Status = status };
        m.Tasks.Add(new DomainTask { Title = "research", AssignedAnt = "researcher", Status = DomainTaskStatus.Complete, Result = "found things" });
        if (verifierPassed)
            m.Tasks.Add(new DomainTask { Title = "verify", AssignedAnt = "verifier", Status = DomainTaskStatus.Complete, Result = "PASS: verified" });
        return m;
    }

    /// <summary>The serialised memory candidates — what the memory pipeline would ingest.</summary>
    private static string Candidates(AntExecutionResult r) =>
        Assert.Single(r.Artifacts, a => a.Kind == "memory_candidate").Content;

    /// <summary>The recorded archival text — Queen stores Narrative ?? Summary.</summary>
    private static string Recorded(AntExecutionResult r) => r.Narrative ?? r.Summary;

    private static JsonElement[] Parsed(AntExecutionResult r) =>
        JsonDocument.Parse(Candidates(r)).RootElement.EnumerateArray().ToArray();

    private static string[] Classes(AntExecutionResult r) =>
        Parsed(r).Select(c => c.GetProperty("memory_class").GetString()!).ToArray();

    [Fact]
    public void CompletedVerified_ProducesPositiveProceduralCandidate()
    {
        var o = Archive(Terminal(MissionStatus.Complete, verifierPassed: true));
        Assert.Contains("completed_verified", Recorded(o));
        Assert.Contains("procedural_candidate", Classes(o));
    }

    [Fact]
    public void NothingAutoPromotes_RegardlessOfOutcome()
    {
        // Previously "Assert.Contains(\"false\", o)", which any JSON false anywhere satisfied.
        // Certification is the V2.12 evaluation pipeline's decision, never archival's.
        var o = Archive(Terminal(MissionStatus.Complete, verifierPassed: true));
        Assert.All(Parsed(o), c => Assert.False(c.GetProperty("auto_promote").GetBoolean()));
    }

    [Fact]
    public void CompletedUnverified_IsNotPositive()
    {
        var o = Archive(Terminal(MissionStatus.Complete, verifierPassed: false));
        Assert.Contains("completed_unverified", Recorded(o));
        Assert.DoesNotContain("procedural_candidate", Classes(o)); // not yet successful != lesson to repeat
    }

    [Fact]
    public void PartialMission_NeverReinforcesPositively_ProducesNegative()
    {
        var m = Terminal(MissionStatus.Partial);
        m.Tasks.Add(new DomainTask { Title = "broken", AssignedAnt = "coder", Status = DomainTaskStatus.Failed, FailureReason = "patch rejected" });
        var o = Archive(m);
        Assert.DoesNotContain("procedural_candidate", Classes(o));
        Assert.Contains("negative", Classes(o));
        Assert.Contains("Do not repeat", Candidates(o));
    }

    [Fact]
    public void FailedMission_ProducesNegativeLesson_WithProvenance()
    {
        var m = Terminal(MissionStatus.Failed);
        m.Tasks.Add(new DomainTask { Title = "boom", AssignedAnt = "tester", Status = DomainTaskStatus.Failed, FailureReason = "dotnet_build exit_code=1" });
        var o = Archive(m);
        Assert.Contains("negative", Classes(o));
        // Provenance travels on every candidate, not merely somewhere in the blob.
        Assert.All(Parsed(o), c => Assert.Equal(m.Id, c.GetProperty("source_mission").GetString()));
        Assert.Contains(o.Evidence, e => e.Kind == "mission_id" && e.Value == m.Id);
    }

    [Fact]
    public void Cancellation_IsNeutral_EpisodicOnly()
    {
        var o = Archive(Terminal(MissionStatus.Failed), desc: "archive. outcome: cancelled");
        Assert.Contains("cancelled", Recorded(o));
        // Structural version of the old Split("episodic")[1] check: neutral means exactly one
        // episodic candidate and nothing else -- no procedural reinforcement, no negative lesson.
        Assert.Equal(new[] { "episodic" }, Classes(o));
    }

    [Fact]
    public void NonTerminalMission_RefusesToArchive()
    {
        var m = new Mission { Goal = "still running", Status = MissionStatus.Running };
        var o = Archive(m);
        Assert.Equal("blocked", o.StatusCode);
        Assert.False(o.Success);
    }

    [Fact]
    public void SecretLikeContent_IsRedacted()
    {
        var m = Terminal(MissionStatus.Failed);
        m.Tasks.Add(new DomainTask { Title = "leak", AssignedAnt = "coder", Status = DomainTaskStatus.Failed, FailureReason = "config had password = 'hunter2secret'" });
        var o = Archive(m);
        // Redaction must hold on BOTH surfaces: the ingested record and the operator-visible one.
        Assert.DoesNotContain("hunter2secret", Candidates(o));
        Assert.DoesNotContain("hunter2secret", Recorded(o));
        Assert.Contains("[REDACTED]", Candidates(o));
    }

    [Fact]
    public void GatesControlExecutability()
    {
        Assert.DoesNotContain("archivist", AntRegistry.ExecutableRoleIds);
        try
        {
            AnthillRuntime.EnableSpecialistAntExecution = true;
            AnthillRuntime.EnableArchivistAnt = true;
            Assert.Contains("archivist", AntRegistry.ExecutableRoleIds);
        }
        finally
        {
            AnthillRuntime.EnableSpecialistAntExecution = false;
            AnthillRuntime.EnableArchivistAnt = false;
        }
    }
}
