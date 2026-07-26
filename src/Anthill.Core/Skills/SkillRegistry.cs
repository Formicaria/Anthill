using System.Text.Json.Serialization;
using Anthill.Core.Verification;

namespace Anthill.Core.Skills;

/// <summary>
/// NORTH_STAR Phase 5 — Procedural Skills and Evaluated Learning. ANTHILL improves from VERIFIED
/// experience only: a reusable method earns promotion through repeated successes that each carry a
/// promotable evidence bundle (v2.12 Phase 4), and it is demoted the moment reality stops agreeing.
/// Hard rules enforced here: nothing self-certifies, unverified/partial outcomes never count as
/// success, and learning may reorder preferences but can never grant permissions, skip approvals,
/// weaken verification, or expand targets.
/// </summary>
public enum SkillStatus { Candidate, Experimental, Certified, Degraded, Retired, Blocked }

public sealed class Skill
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("purpose")] public string Purpose { get; set; } = "";
    /// <summary>Environment fingerprints this skill has been proven against (e.g. "proxmox-8",
    /// "dotnet-9"). Empty = unproven anywhere; a skill is never used outside its coverage.</summary>
    [JsonPropertyName("environments")] public List<string> Environments { get; set; } = new();
    [JsonPropertyName("required_capabilities")] public List<string> RequiredCapabilities { get; set; } = new();
    [JsonPropertyName("procedure")] public List<string> Procedure { get; set; } = new();
    [JsonPropertyName("verification_policy")] public string VerificationPolicy { get; set; } = "code_patch";
    [JsonPropertyName("compensation_plan")] public string CompensationPlan { get; set; } = "";
    [JsonPropertyName("success_count")] public int SuccessCount { get; set; }
    [JsonPropertyName("failure_count")] public int FailureCount { get; set; }
    [JsonPropertyName("consecutive_failures")] public int ConsecutiveFailures { get; set; }
    [JsonPropertyName("status")] public SkillStatus Status { get; set; } = SkillStatus.Candidate;
    [JsonPropertyName("last_validated")] public string LastValidated { get; set; } = "";
    [JsonPropertyName("evidence_bundles")] public List<string> EvidenceBundleIds { get; set; } = new();
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();

    /// <summary>Confidence is DERIVED from outcomes, never asserted by a model.</summary>
    [JsonPropertyName("confidence")]
    public double Confidence => SuccessCount + FailureCount == 0
        ? 0 : Math.Round((double)SuccessCount / (SuccessCount + FailureCount), 3);

    public bool UsableIn(string environment) =>
        Status is SkillStatus.Certified or SkillStatus.Experimental
        && (Environments.Count == 0 || Environments.Contains(environment, StringComparer.OrdinalIgnoreCase));
}

/// <summary>Operator-tunable promotion thresholds — the bar, not the judgment.</summary>
public sealed record SkillPolicy(
    int ExperimentalAfterVerifiedSuccesses = 1,
    int CertifiedAfterVerifiedSuccesses = 3,
    double CertifiedMinConfidence = 0.8,
    int DegradeAfterConsecutiveFailures = 2,
    int RetireAfterConsecutiveFailures = 4);

public sealed class SkillRegistry
{
    private readonly Dictionary<string, Skill> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly SkillPolicy _policy;

    public SkillRegistry(SkillPolicy? policy = null) => _policy = policy ?? new SkillPolicy();

    public IReadOnlyCollection<Skill> All => _skills.Values;
    public Skill? Get(string id) => _skills.TryGetValue(id ?? "", out var s) ? s : null;

    /// <summary>Register (or return) a CANDIDATE. Candidates are never usable — they must earn
    /// experimental status through a verified success first.</summary>
    public Skill RegisterCandidate(string id, string purpose, IEnumerable<string>? procedure = null)
    {
        if (_skills.TryGetValue(id, out var existing)) return existing;
        var skill = new Skill
        {
            Id = id, Purpose = purpose, Status = SkillStatus.Candidate,
            Procedure = procedure?.ToList() ?? new List<string>(),
        };
        _skills[id] = skill;
        return skill;
    }

    /// <summary>
    /// Record one execution outcome. A success counts ONLY when its evidence bundle is promotable
    /// (all required verifiers passed, deterministic evidence present) — a "completed" mission with
    /// no proof is not a success and cannot advance a skill. Returns the resulting status.
    /// </summary>
    public SkillStatus RecordOutcome(string id, VerificationBundle? bundle, string environment = "", string? note = null)
    {
        var skill = Get(id) ?? RegisterCandidate(id, "(auto-registered from outcome)");
        if (skill.Status is SkillStatus.Blocked or SkillStatus.Retired)
        {
            skill.Notes.Add($"outcome ignored — skill is {skill.Status}");
            return skill.Status; // blocked/retired skills never silently revive
        }

        var verified = bundle is { Promotable: true };
        if (verified)
        {
            skill.SuccessCount++;
            skill.ConsecutiveFailures = 0;
            skill.LastValidated = DateTime.UtcNow.ToString("O");
            skill.EvidenceBundleIds.Add(bundle!.Id);
            if (environment.Length > 0 && !skill.Environments.Contains(environment, StringComparer.OrdinalIgnoreCase))
                skill.Environments.Add(environment);
        }
        else
        {
            skill.FailureCount++;
            skill.ConsecutiveFailures++;
            skill.Notes.Add(note ?? (bundle is null
                ? "no evidence bundle — not counted as success"
                : $"unverified outcome: {bundle.Explain()}"));
        }
        return Reevaluate(skill);
    }

    /// <summary>Status is recomputed from the record — promotion and demotion are both automatic
    /// and symmetric; a skill that stops working loses standing without operator intervention.</summary>
    private SkillStatus Reevaluate(Skill s)
    {
        if (s.ConsecutiveFailures >= _policy.RetireAfterConsecutiveFailures)
            s.Status = SkillStatus.Retired;
        else if (s.ConsecutiveFailures >= _policy.DegradeAfterConsecutiveFailures)
            s.Status = SkillStatus.Degraded;
        else if (s.SuccessCount >= _policy.CertifiedAfterVerifiedSuccesses
                 && s.Confidence >= _policy.CertifiedMinConfidence)
            s.Status = SkillStatus.Certified;
        else if (s.SuccessCount >= _policy.ExperimentalAfterVerifiedSuccesses)
            s.Status = SkillStatus.Experimental;
        else
            s.Status = SkillStatus.Candidate;
        return s.Status;
    }

    /// <summary>Environment drift (provider upgrade, toolchain change) invalidates proven coverage:
    /// certified skills fall back to degraded until they prove themselves again.</summary>
    public void OnEnvironmentChanged(string environment, string reason)
    {
        foreach (var s in _skills.Values.Where(x => x.Environments.Contains(environment, StringComparer.OrdinalIgnoreCase)))
        {
            if (s.Status is SkillStatus.Certified or SkillStatus.Experimental)
            {
                s.Status = SkillStatus.Degraded;
                s.Notes.Add($"degraded: environment '{environment}' changed ({reason}) — must re-prove");
            }
        }
    }

    /// <summary>Operator override — the only path to Blocked, and the only way back from Retired.</summary>
    public void SetOperatorStatus(string id, SkillStatus status, string reason)
    {
        var s = Get(id); if (s is null) return;
        s.Status = status;
        s.Notes.Add($"operator set {status}: {reason}");
    }

    /// <summary>
    /// Planner preference (spec §"Planner use"): certified compatible skills first, then
    /// experimental (sandbox/shadow only), then nothing — the caller generates a plan instead.
    /// Degraded, retired, blocked, and candidate skills are never offered.
    /// </summary>
    public Skill? PreferredFor(string purposeContains, string environment)
    {
        var usable = _skills.Values
            .Where(s => s.UsableIn(environment))
            .Where(s => purposeContains.Length == 0
                     || s.Purpose.Contains(purposeContains, StringComparison.OrdinalIgnoreCase)
                     || s.Id.Contains(purposeContains, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return usable.Where(s => s.Status == SkillStatus.Certified).OrderByDescending(s => s.Confidence).FirstOrDefault()
            ?? usable.Where(s => s.Status == SkillStatus.Experimental).OrderByDescending(s => s.Confidence).FirstOrDefault();
    }

    /// <summary>Experimental skills may only run in sandbox/shadow mode — never straight at prod.</summary>
    public static bool RequiresSandbox(Skill s) => s.Status == SkillStatus.Experimental;
}
