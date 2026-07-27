using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Planning;
using Anthill.Core.Skills;
using Anthill.Core.Verification;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// v2.22.0 Phase C2: the skills loop closes. v2.21.0 made skills durable and let certified
/// procedures INFORM a plan, but nothing recorded whether following one worked — standing could
/// only ever be earned in the shadow simulator. Tasks now carry the procedure they followed, and a
/// finished mission credits it.
///
/// The rule is the one everything else obeys: only `completed_verified` is a positive outcome.
/// </summary>
public class SkillCreditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_credit_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---- a claimed skill id must have been offered ------------------------------------------------

    /// <summary>
    /// Credit must attach only to a procedure the planner was actually shown. A model that names an
    /// id from nowhere — or one belonging to a skill it was never offered — must not be able to
    /// direct credit at it.
    /// </summary>
    [Fact]
    public void OnlyIdsFromTheRenderedContext_CanBeClaimed()
    {
        var registry = new SkillRegistry();
        registry.RegisterCandidate("restart_service", "restart a failed service");
        for (var i = 0; i < 3; i++) registry.RecordOutcome("restart_service", Promotable($"b{i}"));

        var offered = Planner.SkillContextIds(SkillPlanningContext.Format(registry));

        Assert.Contains("restart_service", offered);
        Assert.DoesNotContain("invented_skill", offered);
        Assert.DoesNotContain("", offered);
    }

    [Fact]
    public void AnEmptyOrPlaceholderContext_OffersNothing()
    {
        Assert.Empty(Planner.SkillContextIds(SkillPlanningContext.Format(null)));
        Assert.Empty(Planner.SkillContextIds(""));
        Assert.Empty(Planner.SkillContextIds(null));
    }

    [Fact]
    public void TheOfferedSetSurvivesTheExactRenderedFormat()
    {
        // Format and parser must agree; if either drifts, every claim is silently rejected and the
        // loop goes quiet without failing anything.
        var registry = new SkillRegistry();
        foreach (var id in new[] { "alpha_skill", "beta_skill" })
        {
            registry.RegisterCandidate(id, $"purpose {id}");
            for (var i = 0; i < 3; i++) registry.RecordOutcome(id, Promotable($"{id}{i}"));
        }
        var offered = Planner.SkillContextIds(SkillPlanningContext.Format(registry));
        Assert.Equal(2, offered.Count);
        Assert.Contains("alpha_skill", offered);
        Assert.Contains("beta_skill", offered);
    }

    // ---- provenance is durable ---------------------------------------------------------------------

    [Fact]
    public void ATasksSkillReference_SurvivesPersistence()
    {
        Directory.CreateDirectory(_dir);
        var mem = new SqliteMemory(Path.Combine(_dir, "credit.db"));
        var mission = new Mission { Goal = "g" };
        mem.SaveMission(mission);

        var task = new DomainTask { Title = "do it", AssignedAnt = "builder", SkillId = "restart_service" };
        mem.SaveTask(mission.Id, task);

        var row = mem.GetTasksForMission(mission.Id).Single();
        Assert.Equal("restart_service", row["skill_id"]?.ToString());
    }

    [Fact]
    public void ATaskWithoutASkill_StoresNull_AndIsNeverCredited()
    {
        Directory.CreateDirectory(_dir);
        var mem = new SqliteMemory(Path.Combine(_dir, "credit2.db"));
        var mission = new Mission { Goal = "g" };
        mem.SaveMission(mission);
        mem.SaveTask(mission.Id, new DomainTask { Title = "plain", AssignedAnt = "builder" });

        Assert.Null(mem.GetTasksForMission(mission.Id).Single()["skill_id"]);
    }

    // ---- the credit rule ----------------------------------------------------------------------------

    /// <summary>
    /// The asymmetry v2.19.0 established, applied to skills: an unverified outcome must not promote.
    /// `RecordOutcome` treats a null bundle as a non-success, so a mission that merely finished
    /// cannot advance a procedure's standing.
    /// </summary>
    [Fact]
    public void AnUnverifiedMissionCannotPromoteASkill()
    {
        var registry = new SkillRegistry();
        registry.RegisterCandidate("hopeful", "unproven procedure");

        for (var i = 0; i < 5; i++) registry.RecordOutcome("hopeful", null);   // finished, never verified

        var skill = registry.Get("hopeful")!;
        Assert.Equal(0, skill.SuccessCount);
        Assert.NotEqual(SkillStatus.Certified, skill.Status);
        Assert.False(skill.UsableIn(""));
    }

    [Fact]
    public void OnlyAPromotableBundleAdvancesStanding()
    {
        var registry = new SkillRegistry();
        registry.RegisterCandidate("proven", "a real procedure");
        for (var i = 0; i < 3; i++) registry.RecordOutcome("proven", Promotable($"b{i}"));

        Assert.Equal(SkillStatus.Certified, registry.Get("proven")!.Status);
    }

    /// <summary>A bundle whose required verifier did not pass is not promotable — fail closed.</summary>
    [Fact]
    public void ABundleMissingItsRequiredVerifier_IsNotPromotable()
    {
        var missing = new VerificationBundle { TaskType = "mission_verification", Required = { "mission_verifier" } };
        Assert.False(missing.Promotable);

        var failed = new VerificationBundle
        {
            TaskType = "mission_verification", Required = { "mission_verifier" },
            Results = { new VerificationResult("mission_verifier", false, false, "nope", Array.Empty<VerificationEvidence>()) },
        };
        Assert.False(failed.Promotable);
    }

    // ---- the call site ------------------------------------------------------------------------------

    /// <summary>
    /// The lesson again: a credit function nothing calls changes no standing. This pins that the
    /// Queen credits at mission finalisation, gates on positive success, and persists the result.
    /// </summary>
    [Fact]
    public void TheQueenCreditsSkills_AtMissionFinalisation()
    {
        var code = CodeOnly(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs")));
        Assert.Contains("CreditSkills(mission)", code);

        var credit = Between(code, "private void CreditSkills", "private static Verification.VerificationBundle");
        Assert.Contains("MissionOutcome.IsPositiveSuccess", credit);   // only verified success counts
        Assert.Contains("Skills.RecordOutcome", credit);
        Assert.Contains("Memory.SaveSkillRegistry(Skills)", credit);   // standing outlives the process
    }

    /// <summary>The planner must record provenance, or nothing is ever creditable.</summary>
    [Fact]
    public void ThePlannerRecordsWhichSkillATaskFollowed()
    {
        var planner = CodeOnly(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Core", "Planning", "Planner.cs")));
        Assert.Contains("SkillId = skillId", planner);
        Assert.Contains("_offeredSkillIds.Contains(claimedSkill)", planner);
    }

    // ---- helpers -------------------------------------------------------------------------------------

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

    private static string Between(string text, string start, string end)
    {
        var a = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, $"anchor not found: {start}");
        var b = text.IndexOf(end, a, StringComparison.Ordinal);
        return b > a ? text[a..b] : text[a..];
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
