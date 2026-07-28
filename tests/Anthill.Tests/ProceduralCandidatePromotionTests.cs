using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Anthill.Core.Outcomes;
using Anthill.Core.Skills;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;
using DomainTaskStatus = Anthill.Core.Domain.TaskStatus;

namespace Anthill.Tests;

/// <summary>
/// v2.23.0 Phase C4: the archivist's procedural candidates reach the skill evaluation pipeline.
///
/// v2.20.0 gave memory candidates a consumer, but the procedural ones went no further — the
/// archivist would observe "this route worked on a verified mission", write it down, and the V2.12
/// evaluation model would never hear about it. Both halves of learning were present and not
/// connected.
///
/// The rule these tests exist to hold: an observation is a HYPOTHESIS, never evidence.
/// </summary>
public class ProceduralCandidatePromotionTests
{
    private static MemoryCandidateIngest.Candidate Procedural(string summary) =>
        new(ProceduralCandidatePromotion.ProceduralClass, summary, "m1", MissionOutcome.CompletedVerified, "medium", false);

    private static MemoryCandidateIngest.Candidate Episodic(string summary) =>
        new("episodic", summary, "m1", MissionOutcome.CompletedVerified, "high", false);

    // ---- an observation is never evidence ---------------------------------------------------------

    /// <summary>
    /// The core guarantee. A registered route is a Candidate: usable for nothing, in no plan, with
    /// no permission. Treating one observation as proof is precisely the mistake v2.19.0 corrected.
    /// </summary>
    [Fact]
    public void ARegisteredRouteIsACandidate_UsableForNothing()
    {
        var registry = new SkillRegistry();
        var ids = ProceduralCandidatePromotion.Register(
            registry, new[] { Procedural("Verified route for similar goals: researcher -> coder -> verifier") },
            MissionOutcome.CompletedVerified);

        var id = Assert.Single(ids);
        var skill = registry.Get(id)!;
        Assert.Equal(SkillStatus.Candidate, skill.Status);
        Assert.Equal(0, skill.SuccessCount);
        Assert.False(skill.UsableIn(""));
        // And therefore invisible to planning.
        Assert.Empty(SkillPlanningContext.Usable(registry));
    }

    [Fact]
    public void RegistrationRecordsNoOutcome_SoNothingCanBePromotedByObservation()
    {
        var registry = new SkillRegistry();
        for (var i = 0; i < 10; i++)
            ProceduralCandidatePromotion.Register(
                registry, new[] { Procedural("Verified route for similar goals: a -> b") },
                MissionOutcome.CompletedVerified);

        var skill = registry.Get(ProceduralCandidatePromotion.IdFor("x: a -> b"))!;
        Assert.Equal(SkillStatus.Candidate, skill.Status);   // ten observations, still unproven
        Assert.Equal(0, skill.SuccessCount);
        Assert.Equal(0, skill.FailureCount);
    }

    // ---- only verified missions propose routes -----------------------------------------------------

    [Theory]
    [InlineData(MissionOutcome.CompletedUnverified)]
    [InlineData(MissionOutcome.Partial)]
    [InlineData(MissionOutcome.FailedPermanent)]
    [InlineData("")]
    [InlineData("something_unknown")]
    public void AnUnverifiedMissionProposesNothing(string outcome)
    {
        var registry = new SkillRegistry();
        var ids = ProceduralCandidatePromotion.Register(
            registry, new[] { Procedural("Verified route for similar goals: a -> b") }, outcome);

        Assert.Empty(ids);
        Assert.Empty(registry.All);
    }

    [Fact]
    public void NonProceduralCandidatesAreIgnored()
    {
        var registry = new SkillRegistry();
        var ids = ProceduralCandidatePromotion.Register(
            registry, new[] { Episodic("Mission 'x' ended completed_verified.") }, MissionOutcome.CompletedVerified);

        Assert.Empty(ids);
        Assert.Empty(registry.All);
    }

    // ---- route identity ------------------------------------------------------------------------------

    /// <summary>
    /// The same route observed on many missions must converge on ONE skill accumulating evidence.
    /// Per-observation ids would produce a pile of single-observation skills that can never reach
    /// the success count certification requires — learning that looks busy and proves nothing.
    /// </summary>
    [Fact]
    public void TheSameRouteConvergesOnOneSkill()
    {
        var registry = new SkillRegistry();
        for (var i = 0; i < 3; i++)
            ProceduralCandidatePromotion.Register(
                registry, new[] { Procedural($"Verified route for similar goals: researcher -> coder -> verifier") },
                MissionOutcome.CompletedVerified);

        Assert.Single(registry.All);
    }

    [Fact]
    public void DifferentRoutesAreDifferentSkills()
    {
        var registry = new SkillRegistry();
        ProceduralCandidatePromotion.Register(registry,
            new[] { Procedural("Verified route: a -> b"), Procedural("Verified route: a -> b -> c") },
            MissionOutcome.CompletedVerified);

        Assert.Equal(2, registry.All.Count);
    }

    [Fact]
    public void ASummaryWithoutARoute_RegistersNothing()
    {
        var registry = new SkillRegistry();
        foreach (var junk in new[] { "no arrow here", "", "Verified route for similar goals: " })
            Assert.Empty(ProceduralCandidatePromotion.Register(
                registry, new[] { Procedural(junk) }, MissionOutcome.CompletedVerified));

        Assert.Empty(registry.All);
    }

    [Fact]
    public void PromotedIdsAreIdentifiableAsRoutes()
    {
        var id = ProceduralCandidatePromotion.IdFor("Verified route for similar goals: researcher -> coder");
        Assert.StartsWith(ProceduralCandidatePromotion.IdPrefix, id);
        Assert.Equal("route:researcher>coder", id);
    }

    [Fact]
    public void NullInputsAreHandled()
    {
        Assert.Empty(ProceduralCandidatePromotion.Register(null, null, MissionOutcome.CompletedVerified));
        Assert.Empty(ProceduralCandidatePromotion.Register(new SkillRegistry(), null, MissionOutcome.CompletedVerified));
    }

    // ---- fed by the REAL archivist output ------------------------------------------------------------

    /// <summary>
    /// The format contract. If the archivist's summary wording changes, this fails rather than the
    /// promotion quietly registering nothing — the failure mode that would make the whole loop go
    /// silent while every unit test still passed.
    /// </summary>
    [Fact]
    public void TheRealArchivistOutput_ProducesAUsableRoute()
    {
        var t = new DomainTask { Title = "Archive", Description = "archive", AssignedAnt = "archivist", TaskType = "memory_consolidation" };
        var m = new Mission { Goal = "ship it", Status = MissionStatus.Complete };
        m.Tasks.Add(new DomainTask { Title = "research", AssignedAnt = "researcher", Status = DomainTaskStatus.Complete, Result = "notes" });
        m.Tasks.Add(new DomainTask { Title = "verify", AssignedAnt = "verifier", Status = DomainTaskStatus.Complete, Result = "PASS: verified" });
        m.Tasks.Add(t);

        var candidates = MemoryCandidateIngest.Extract(new ArchivistAnt().Execute(t, m));
        var registry = new SkillRegistry();
        var ids = ProceduralCandidatePromotion.Register(registry, candidates, MissionOutcome.CompletedVerified);

        var id = Assert.Single(ids);
        Assert.StartsWith(ProceduralCandidatePromotion.IdPrefix, id);
        Assert.NotEmpty(registry.Get(id)!.Procedure);   // the route survived as steps
    }

    // ---- the call site -------------------------------------------------------------------------------

    /// <summary>
    /// v2.26.0: registration moved from archivist-task completion to mission FINALIZATION — the
    /// per-task call resolved the outcome while the mission was still Running, always read
    /// negative, and never registered a single route in production. The guard now pins the wiring
    /// that actually works: registration from the one canonical evaluation, per-skill persisted.
    /// </summary>
    [Fact]
    public void TheQueenRegistersRoutes_AtFinalization_FromTheCanonicalEvaluation()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs"));
        var code = string.Join("\n", source.Split('\n')
            .Select(l => { var i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l[..i] : l; }));

        Assert.Contains("RegisterProceduralRoutes(mission, evaluation)", code);
        Assert.Contains("ProceduralCandidatePromotion.Register(Skills, candidates, evaluation.OutcomeCode)", code);
        Assert.Contains("skill_candidate_registered", code);
        Assert.Contains("Memory.SaveSkill(registered)", code);   // row-atomic; survives the process
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
