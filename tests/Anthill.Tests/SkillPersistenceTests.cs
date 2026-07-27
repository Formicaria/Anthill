using Anthill.Core.Memory;
using Anthill.Core.Skills;
using Anthill.Core.Verification;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.21.0 Phase C prerequisite. The V2.12 skills line shipped a complete evaluation model —
/// candidate → experimental → certified with automatic symmetric demotion — held entirely in a
/// dictionary. It had no production instantiation and no table, so every promotion a skill earned
/// was forgotten on exit.
///
/// That is why "skill selection in planning" could not be built as written: it would have selected
/// from a registry that is empty at every process start. These tests prove the standing a skill
/// earns actually outlives the process.
/// </summary>
public class SkillPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_skills_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string _dbPath = "";

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        if (_dbPath.Length == 0) _dbPath = Path.Combine(_dir, "skills.db");
        return new SqliteMemory(_dbPath);
    }

    /// <summary>A registry with a skill promoted the honest way: through verified outcomes.</summary>
    private static SkillRegistry CertifiedRegistry(string id = "restart_service")
    {
        var registry = new SkillRegistry();
        registry.RegisterCandidate(id, "restart a failed service", new[] { "stop", "start", "verify" });
        for (var i = 0; i < 3; i++)
            registry.RecordOutcome(id, Promotable($"bundle-{i}"), environment: "proxmox-8");
        return registry;
    }

    /// <summary>
    /// A bundle that actually satisfies the promotion rule: every REQUIRED verifier ran and
    /// passed, nothing blocked. Built the real way rather than by setting a flag, so these tests
    /// exercise the same evidence gate production promotion depends on.
    /// </summary>
    private static VerificationBundle Promotable(string id)
    {
        var bundle = new VerificationBundle
        {
            Id = id,
            TaskType = "code_patch",
            Required = { "build" },
            Results =
            {
                new VerificationResult("build", Passed: true, Deterministic: true, "build succeeded",
                    Array.Empty<VerificationEvidence>()),
            },
        };
        Assert.True(bundle.Promotable, "the fixture must be genuinely promotable");
        return bundle;
    }

    // ---- the point: standing survives the process ------------------------------------------------

    [Fact]
    public void ACertifiedSkill_IsStillCertifiedAfterAReload()
    {
        var registry = CertifiedRegistry();
        var before = registry.Get("restart_service")!;
        Assert.Equal(SkillStatus.Certified, before.Status);

        Memory().SaveSkillRegistry(registry);

        // A different process, same database.
        var reloaded = Memory().LoadSkillRegistry().Get("restart_service");
        Assert.NotNull(reloaded);
        Assert.Equal(SkillStatus.Certified, reloaded!.Status);
        Assert.Equal(before.SuccessCount, reloaded.SuccessCount);
        Assert.Equal(before.Confidence, reloaded.Confidence, 6);
    }

    [Fact]
    public void EvidenceAndEnvironmentCoverage_SurviveTheRoundTrip()
    {
        var registry = CertifiedRegistry();
        Memory().SaveSkillRegistry(registry);

        var reloaded = Memory().LoadSkillRegistry().Get("restart_service")!;
        Assert.Equal(3, reloaded.EvidenceBundleIds.Count);
        Assert.Contains("proxmox-8", reloaded.Environments);
        Assert.Contains("stop", reloaded.Procedure);
        // Coverage is what stops a skill being used where it was never proven.
        Assert.True(reloaded.UsableIn("proxmox-8"));
    }

    [Fact]
    public void FailureHistorySurvives_SoDemotionIsNotUndoneByARestart()
    {
        var registry = new SkillRegistry();
        registry.RegisterCandidate("flaky", "does something unreliable");
        registry.RecordOutcome("flaky", null);   // no bundle = not a verified success
        registry.RecordOutcome("flaky", null);
        var degraded = registry.Get("flaky")!.Status;

        Memory().SaveSkillRegistry(registry);
        var reloaded = Memory().LoadSkillRegistry().Get("flaky")!;

        Assert.Equal(degraded, reloaded.Status);
        Assert.Equal(2, reloaded.FailureCount);
        Assert.Equal(2, reloaded.ConsecutiveFailures);
    }

    /// <summary>
    /// Status is restored as recorded, not recomputed. Recomputing on load would let a policy
    /// change silently re-grade history the evidence no longer backs.
    /// </summary>
    [Fact]
    public void StatusIsRestoredAsRecorded_NotReEvaluatedUnderCurrentPolicy()
    {
        var registry = CertifiedRegistry();
        Memory().SaveSkillRegistry(registry);

        // Reload under a policy that would NEVER certify this skill (needs 99 successes).
        var strict = new SkillPolicy(CertifiedAfterVerifiedSuccesses: 99);
        var reloaded = Memory().LoadSkillRegistry(strict).Get("restart_service")!;

        Assert.Equal(SkillStatus.Certified, reloaded.Status);
    }

    /// <summary>
    /// An unreadable status must not restore as Certified — that would grant standing the database
    /// cannot justify. Fail closed to Candidate, which is usable for nothing.
    /// </summary>
    [Fact]
    public void AnUnrecognisedStoredStatus_FailsClosedToCandidate()
    {
        var mem = Memory();
        var skill = new Skill { Id = "corrupt", Purpose = "p", Status = SkillStatus.Certified, SuccessCount = 5 };
        mem.SaveSkill(skill);

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE skills SET status = 'NotAStatus' WHERE id = 'corrupt'";
            cmd.ExecuteNonQuery();
        }

        Assert.Equal(SkillStatus.Candidate, Memory().LoadSkillRegistry().Get("corrupt")!.Status);
    }

    [Fact]
    public void MalformedListColumns_DegradeToEmpty_RatherThanBlockingStartup()
    {
        var mem = Memory();
        mem.SaveSkill(new Skill { Id = "broken_lists", Purpose = "p" });

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE skills SET procedure_json = '{not json', environments_json = '\"a string\"' WHERE id = 'broken_lists'";
            cmd.ExecuteNonQuery();
        }

        var reloaded = Memory().LoadSkillRegistry().Get("broken_lists")!;
        Assert.Empty(reloaded.Procedure);
        Assert.Empty(reloaded.Environments);
    }

    [Fact]
    public void SavingTwice_UpdatesRatherThanDuplicating()
    {
        var mem = Memory();
        var registry = CertifiedRegistry();
        mem.SaveSkillRegistry(registry);

        registry.RecordOutcome("restart_service", Promotable("bundle-extra"), environment: "proxmox-8");
        mem.SaveSkillRegistry(registry);

        Assert.Single(Memory().LoadSkills());
        Assert.Equal(4, Memory().LoadSkillRegistry().Get("restart_service")!.SuccessCount);
    }

    [Fact]
    public void AFreshDatabaseHasNoSkills_AndLoadingIsSafe()
    {
        Assert.Empty(Memory().LoadSkills());
        Assert.Empty(Memory().LoadSkillRegistry().All);
    }
}
