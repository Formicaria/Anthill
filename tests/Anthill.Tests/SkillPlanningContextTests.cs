using Anthill.Core.Skills;
using Anthill.Core.Verification;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.21.0 Phase C1: certified procedures reach the planner — selection only.
///
/// The V2.12 line could promote a procedure to Certified on verified evidence and nothing ever
/// consulted the result: a skill earned standing that changed no decision anywhere. These tests
/// pin what the planner is told, and — more importantly — what it is NOT told.
/// </summary>
public class SkillPlanningContextTests
{
    private static VerificationBundle Promotable(string id)
    {
        var bundle = new VerificationBundle
        {
            Id = id, TaskType = "code_patch", Required = { "build" },
            Results = { new VerificationResult("build", true, true, "ok", Array.Empty<VerificationEvidence>()) },
        };
        Assert.True(bundle.Promotable);
        return bundle;
    }

    private static SkillRegistry With(params (string Id, int Successes, string Env)[] skills)
    {
        var registry = new SkillRegistry();
        foreach (var (id, successes, env) in skills)
        {
            registry.RegisterCandidate(id, $"purpose of {id}");
            for (var i = 0; i < successes; i++)
                registry.RecordOutcome(id, Promotable($"{id}-{i}"), environment: env);
        }
        return registry;
    }

    // ---- what the planner is NOT told --------------------------------------------------------

    /// <summary>
    /// A candidate has earned nothing. Offering it as a "proven procedure" would be the exact
    /// failure the whole evaluation model exists to prevent.
    /// </summary>
    [Fact]
    public void UnprovenSkills_AreNeverOffered()
    {
        var registry = new SkillRegistry();
        registry.RegisterCandidate("untested", "never run");
        Assert.Empty(SkillPlanningContext.Usable(registry));
        Assert.Contains("no proven procedures", SkillPlanningContext.Format(registry));
    }

    [Fact]
    public void FailingSkills_LoseTheirPlaceAutomatically()
    {
        var registry = With(("good", 3, ""));
        Assert.Single(SkillPlanningContext.Usable(registry));

        // Demotion is symmetric and automatic — enough consecutive failures and it is gone.
        for (var i = 0; i < 4; i++) registry.RecordOutcome("good", null);
        Assert.Empty(SkillPlanningContext.Usable(registry));
    }

    /// <summary>
    /// Coverage is the rule the rest of the system uses; planning must not get a looser one. A
    /// skill proven only on proxmox-8 is not a proven route on dotnet-9.
    /// </summary>
    [Fact]
    public void ASkillIsNotOfferedOutsideTheEnvironmentItWasProvenIn()
    {
        var registry = With(("proxmox_restart", 3, "proxmox-8"));
        Assert.Single(SkillPlanningContext.Usable(registry, "proxmox-8"));
        Assert.Empty(SkillPlanningContext.Usable(registry, "dotnet-9"));
    }

    [Fact]
    public void ANullRegistry_IsHandled_NotThrown()
    {
        Assert.Empty(SkillPlanningContext.Usable(null));
        Assert.Contains("no proven procedures", SkillPlanningContext.Format(null));
    }

    // ---- what it IS told ----------------------------------------------------------------------

    [Fact]
    public void ProvenSkills_AppearWithTheEvidenceBehindThem()
    {
        var block = SkillPlanningContext.Format(With(("restart_service", 3, "")));
        Assert.Contains("restart_service", block);
        Assert.Contains("Certified", block);
        // The count matters: "certified" alone cannot distinguish three successes from thirty.
        Assert.Contains("3 verified success(es)", block);
        Assert.Contains("confidence 1.00", block);
    }

    [Fact]
    public void StrongerEvidenceIsOfferedFirst()
    {
        var registry = With(("weak", 1, ""), ("strong", 5, ""));
        registry.RecordOutcome("weak", null);   // drags confidence down without retiring it

        var ordered = SkillPlanningContext.Usable(registry).Select(s => s.Id).ToList();
        Assert.Equal("strong", ordered.First());
    }

    [Fact]
    public void TheContextIsDeterministicAndBounded()
    {
        var registry = With(("a", 3, ""), ("b", 3, ""), ("c", 3, ""));
        Assert.Equal(SkillPlanningContext.Format(registry), SkillPlanningContext.Format(registry));
        Assert.Equal(2, SkillPlanningContext.Usable(registry, limit: 2).Count);
    }

    // ---- the call site ------------------------------------------------------------------------

    /// <summary>
    /// The recurring lesson: a formatter with no caller changes nothing. This pins that the Queen
    /// builds the context from a PERSISTED registry and hands it to the planner, and that the
    /// planner's prompt actually renders it.
    /// </summary>
    [Fact]
    public void TheQueenFeedsPersistedSkills_IntoThePlanner()
    {
        var queen = CodeOnly(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs")));
        Assert.Contains("SkillPlanningContext.Format(Skills)", queen);
        Assert.Contains("Memory.LoadSkillRegistry()", queen);   // hydrated, not constructed empty

        var planner = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Planning", "Planner.cs"));
        Assert.Contains("{skillContext}", planner);
    }

    /// <summary>
    /// A skill is a route to consider, not a script to run. The prompt must say so, because a
    /// planner that treats a certified procedure as authorisation would bypass the gates every
    /// planned task is supposed to pass.
    /// </summary>
    [Fact]
    public void ThePromptSaysSkillsAreRoutes_NotScripts()
    {
        var planner = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Planning", "Planner.cs"));
        Assert.Contains("they are not scripts", planner);
        Assert.Contains("permission and contract check", planner);
    }

    private static string CodeOnly(string src) => string.Join("\n", src.Split('\n')
        .Select(line =>
        {
            var i = line.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? line[..i] : line;
        }));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
