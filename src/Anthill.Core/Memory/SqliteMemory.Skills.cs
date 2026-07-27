using Anthill.Core.Common;
using Anthill.Core.Skills;

namespace Anthill.Core.Memory;

/// <summary>
/// v2.21.0 Phase C prerequisite: skills become durable.
///
/// The V2.12 skills line shipped a complete evaluation model — candidate → experimental →
/// certified, with automatic symmetric demotion — held entirely in a `Dictionary` inside
/// `SkillRegistry`. It had **no production instantiation** (only the shadow simulator built one)
/// and no table. Every promotion a skill earned was forgotten when the process exited.
///
/// That made two Phase C items unbuildable as written. "Skill selection in normal planning" would
/// have selected from a registry that is empty at every process start; "connect memory candidates
/// to the evaluation pipeline" would have fed a pipeline whose state cannot outlive a restart.
/// Wiring either without this would be worse than leaving them unwired: planning decisions taken
/// from state that vanishes, and a learning system that forgets everything while appearing to work.
///
/// Persistence is deliberately a plain projection of the model — list fields as JSON, matching the
/// metadata_json convention already used for objectives and trails — so promotion logic stays in
/// <see cref="SkillRegistry"/> and this layer only stores what that logic decided.
/// </summary>
public sealed partial class SqliteMemory
{
    /// <summary>Write (or replace) one skill's durable record.</summary>
    public void SaveSkill(Skill skill)
    {
        if (skill is null || string.IsNullOrWhiteSpace(skill.Id)) return;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO skills (id, version, purpose, environments_json, required_capabilities_json,
                      procedure_json, verification_policy, compensation_plan, success_count, failure_count,
                      consecutive_failures, status, last_validated, evidence_bundles_json, notes_json, saved_at)
                  VALUES (@id, @ver, @purpose, @envs, @caps, @proc, @vpolicy, @comp, @sc, @fc, @cf, @status,
                      @validated, @bundles, @notes, @saved)
                  ON CONFLICT(id) DO UPDATE SET
                      version=@ver, purpose=@purpose, environments_json=@envs,
                      required_capabilities_json=@caps, procedure_json=@proc, verification_policy=@vpolicy,
                      compensation_plan=@comp, success_count=@sc, failure_count=@fc,
                      consecutive_failures=@cf, status=@status, last_validated=@validated,
                      evidence_bundles_json=@bundles, notes_json=@notes, saved_at=@saved",
                ("@id", skill.Id), ("@ver", skill.Version), ("@purpose", skill.Purpose),
                ("@envs", Json.SafeDumps(skill.Environments)),
                ("@caps", Json.SafeDumps(skill.RequiredCapabilities)),
                ("@proc", Json.SafeDumps(skill.Procedure)),
                ("@vpolicy", skill.VerificationPolicy), ("@comp", skill.CompensationPlan),
                ("@sc", skill.SuccessCount), ("@fc", skill.FailureCount),
                ("@cf", skill.ConsecutiveFailures), ("@status", skill.Status.ToString()),
                ("@validated", skill.LastValidated),
                ("@bundles", Json.SafeDumps(skill.EvidenceBundleIds)),
                ("@notes", Json.SafeDumps(skill.Notes)),
                ("@saved", AnthillTime.NowUtc().ToIso()));
        }
        InvalidateCache();
    }

    /// <summary>Every persisted skill, as domain objects.</summary>
    public List<Skill> LoadSkills() =>
        Query(@"SELECT id, version, purpose, environments_json, required_capabilities_json, procedure_json,
                    verification_policy, compensation_plan, success_count, failure_count,
                    consecutive_failures, status, last_validated, evidence_bundles_json, notes_json
                FROM skills ORDER BY id")
            .Select(ToSkill).ToList();

    /// <summary>
    /// A registry hydrated from the database. This is how production gets a registry with history —
    /// the parameterless constructor only ever yields an empty one.
    /// </summary>
    public SkillRegistry LoadSkillRegistry(SkillPolicy? policy = null)
    {
        var registry = new SkillRegistry(policy);
        foreach (var skill in LoadSkills()) registry.Restore(skill);
        return registry;
    }

    /// <summary>Persist every skill in a registry — call after any batch of recorded outcomes.</summary>
    public void SaveSkillRegistry(SkillRegistry registry)
    {
        if (registry is null) return;
        foreach (var skill in registry.All) SaveSkill(skill);
    }

    private static Skill ToSkill(Dictionary<string, object?> row) => new()
    {
        Id = row.GetValueOrDefault("id")?.ToString() ?? "",
        Version = (int)AsLong(row.GetValueOrDefault("version")),
        Purpose = row.GetValueOrDefault("purpose")?.ToString() ?? "",
        Environments = Json.TryParseStringList(row.GetValueOrDefault("environments_json") as string),
        RequiredCapabilities = Json.TryParseStringList(row.GetValueOrDefault("required_capabilities_json") as string),
        Procedure = Json.TryParseStringList(row.GetValueOrDefault("procedure_json") as string),
        VerificationPolicy = row.GetValueOrDefault("verification_policy")?.ToString() ?? "code_patch",
        CompensationPlan = row.GetValueOrDefault("compensation_plan")?.ToString() ?? "",
        SuccessCount = (int)AsLong(row.GetValueOrDefault("success_count")),
        FailureCount = (int)AsLong(row.GetValueOrDefault("failure_count")),
        ConsecutiveFailures = (int)AsLong(row.GetValueOrDefault("consecutive_failures")),
        // Fail CLOSED on an unrecognised status: an unreadable record must never restore as
        // Certified, which would grant a skill standing the database cannot actually justify.
        Status = Enum.TryParse<SkillStatus>(row.GetValueOrDefault("status")?.ToString(), out var s)
            ? s : SkillStatus.Candidate,
        LastValidated = row.GetValueOrDefault("last_validated")?.ToString() ?? "",
        EvidenceBundleIds = Json.TryParseStringList(row.GetValueOrDefault("evidence_bundles_json") as string),
        Notes = Json.TryParseStringList(row.GetValueOrDefault("notes_json") as string),
    };
}
