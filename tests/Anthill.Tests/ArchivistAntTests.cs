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
/// </summary>
[Collection("specialist-gates")]
public class ArchivistAntTests
{
    private static string Archive(Mission m, string desc = "archive the mission")
    {
        var t = new DomainTask { Title = "Archive", Description = desc, AssignedAnt = "archivist", TaskType = "memory_consolidation" };
        m.Tasks.Add(t);
        return new ArchivistAnt().Run(t, m);
    }

    private static Mission Terminal(MissionStatus status, bool verifierPassed = false)
    {
        var m = new Mission { Goal = "improve the widget", Status = status };
        m.Tasks.Add(new DomainTask { Title = "research", AssignedAnt = "researcher", Status = DomainTaskStatus.Complete, Result = "found things" });
        if (verifierPassed)
            m.Tasks.Add(new DomainTask { Title = "verify", AssignedAnt = "verifier", Status = DomainTaskStatus.Complete, Result = "PASS: verified" });
        return m;
    }

    [Fact]
    public void CompletedVerified_ProducesPositiveProceduralCandidate()
    {
        var o = Archive(Terminal(MissionStatus.Complete, verifierPassed: true));
        Assert.Contains("completed_verified", o);
        Assert.Contains("procedural_candidate", o);
        Assert.Contains("auto_promote", o);
        Assert.Contains("false", o); // never auto-certified
    }

    [Fact]
    public void CompletedUnverified_IsNotPositive()
    {
        var o = Archive(Terminal(MissionStatus.Complete, verifierPassed: false));
        Assert.Contains("completed_unverified", o);
        Assert.DoesNotContain("procedural_candidate", o); // not yet successful ≠ lesson to repeat
    }

    [Fact]
    public void PartialMission_NeverReinforcesPositively_ProducesNegative()
    {
        var m = Terminal(MissionStatus.Partial);
        m.Tasks.Add(new DomainTask { Title = "broken", AssignedAnt = "coder", Status = DomainTaskStatus.Failed, FailureReason = "patch rejected" });
        var o = Archive(m);
        Assert.DoesNotContain("procedural_candidate", o);
        Assert.Contains("negative", o);
        Assert.Contains("Do not repeat", o);
    }

    [Fact]
    public void FailedMission_ProducesNegativeLesson_WithProvenance()
    {
        var m = Terminal(MissionStatus.Failed);
        m.Tasks.Add(new DomainTask { Title = "boom", AssignedAnt = "tester", Status = DomainTaskStatus.Failed, FailureReason = "dotnet_build exit_code=1" });
        var o = Archive(m);
        Assert.Contains("negative", o);
        Assert.Contains("source_mission", o);
        Assert.Contains(m.Id, o); // provenance
    }

    [Fact]
    public void Cancellation_IsNeutral_EpisodicOnly()
    {
        var o = Archive(Terminal(MissionStatus.Failed), desc: "archive. outcome: cancelled");
        Assert.Contains("cancelled", o);
        Assert.DoesNotContain("procedural_candidate", o);
        Assert.DoesNotContain("negative", o.Split("episodic")[1]); // nothing beyond the episodic record
    }

    [Fact]
    public void NonTerminalMission_RefusesToArchive()
    {
        var m = new Mission { Goal = "still running", Status = MissionStatus.Running };
        var o = Archive(m);
        Assert.Contains("\"status\":\"blocked\"", o.Replace(" ", ""));
    }

    [Fact]
    public void SecretLikeContent_IsRedacted()
    {
        var m = Terminal(MissionStatus.Failed);
        m.Tasks.Add(new DomainTask { Title = "leak", AssignedAnt = "coder", Status = DomainTaskStatus.Failed, FailureReason = "config had password = 'hunter2secret'" });
        var o = Archive(m);
        Assert.DoesNotContain("hunter2secret", o);
        Assert.Contains("[REDACTED]", o);
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
