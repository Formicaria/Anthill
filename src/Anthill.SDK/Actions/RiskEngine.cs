namespace Anthill.SDK.Actions;

/// <summary>
/// NORTH_STAR Phase 6 risk engine. Deterministic arithmetic — no model input. Critical changes can
/// NEVER qualify as low risk by size alone: touching a critical file class, an irreversible
/// operation, a stale/absent backup, or a high-risk category floors the score at high/critical
/// regardless of how few lines changed.
/// </summary>
public sealed record RiskInputs(
    string Operation = "",
    bool Reversible = true,
    bool HasDeterministicRollback = true,
    string TargetCriticality = "unknown",   // low | normal | high | critical | unknown(→cautious)
    int AffectedSystems = 0,
    int DependencyDepth = 0,
    bool Production = true,
    int BackupAgeDays = -1,                 // -1 = unknown/none
    bool InMaintenanceWindow = false,
    bool UnresolvedIncidents = false,
    bool Novel = true,                      // never done before in this environment
    double SkillConfidence = 0,             // 0..1 from the v2.13 registry
    bool StrongVerifiers = false,           // v2.12 bundle would cover this action
    int ChangedLines = 0,
    IReadOnlyList<string>? TouchedPaths = null);

public sealed record RiskAssessment(string Level, int Score, IReadOnlyList<string> Reasons, bool RequiresApproval);

public static class RiskEngine
{
    /// <summary>Operations that always require explicit approval (spec: high-risk categories).</summary>
    public static readonly string[] HighRiskOperations =
    {
        "delete", "destroy_storage", "restore", "firewall", "credential", "permission",
        "authentication", "routing", "migration", "shutdown", "cluster", "git_force",
    };

    /// <summary>File classes whose modification is critical irrespective of diff size.</summary>
    public static readonly string[] CriticalPathMarkers =
    {
        ".github/workflows/", "deploy/", "Security/", "Directory.Build.props",
        "docker-compose", "Dockerfile", "config.json",
    };

    public static RiskAssessment Score(RiskInputs i)
    {
        var reasons = new List<string>();
        var score = 0;

        var op = (i.Operation ?? "").ToLowerInvariant();
        var highRiskOp = HighRiskOperations.FirstOrDefault(h => op.Contains(h));
        if (highRiskOp is not null) { score += 45; reasons.Add($"high-risk operation class '{highRiskOp}'"); }

        if (!i.Reversible) { score += 40; reasons.Add("operation is irreversible"); }
        if (!i.HasDeterministicRollback) { score += 30; reasons.Add("no deterministic rollback"); }

        score += i.TargetCriticality.ToLowerInvariant() switch
        {
            "critical" => 30, "high" => 20, "normal" => 8, "low" => 2,
            _ => 25, // unknown criticality fails toward caution
        };
        if (i.TargetCriticality.ToLowerInvariant() is not ("critical" or "high" or "normal" or "low"))
            reasons.Add("target criticality unknown — scored cautiously");

        if (i.AffectedSystems > 1) { score += Math.Min(20, i.AffectedSystems * 4); reasons.Add($"{i.AffectedSystems} affected systems"); }
        if (i.DependencyDepth > 1) { score += Math.Min(15, i.DependencyDepth * 3); reasons.Add($"dependency depth {i.DependencyDepth}"); }
        if (i.Production) { score += 10; reasons.Add("production target"); }
        else reasons.Add("lab target");

        if (i.BackupAgeDays < 0) { score += 20; reasons.Add("no known backup"); }
        else if (i.BackupAgeDays > 7) { score += 12; reasons.Add($"backup is {i.BackupAgeDays} days stale"); }

        if (i.UnresolvedIncidents) { score += 15; reasons.Add("unresolved incidents on this target"); }
        if (i.Novel) { score += 10; reasons.Add("novel action for this environment"); }
        if (i.SkillConfidence > 0) { score -= (int)Math.Round(i.SkillConfidence * 15); reasons.Add($"skill confidence {i.SkillConfidence:0.00}"); }
        if (i.StrongVerifiers) { score -= 10; reasons.Add("strong deterministic verifiers available"); }
        if (i.InMaintenanceWindow) { score -= 8; reasons.Add("inside maintenance window"); }

        // Cumulative change size contributes, but can never be the ONLY thing that matters.
        if (i.ChangedLines > 500) { score += 12; reasons.Add($"large change ({i.ChangedLines} lines)"); }
        else if (i.ChangedLines > 100) { score += 6; reasons.Add($"moderate change ({i.ChangedLines} lines)"); }

        var criticalPath = (i.TouchedPaths ?? Array.Empty<string>())
            .FirstOrDefault(p => CriticalPathMarkers.Any(m => p.Replace('\\', '/').Contains(m, StringComparison.OrdinalIgnoreCase)));
        var floorCritical = criticalPath is not null || !i.Reversible || !i.HasDeterministicRollback;
        if (criticalPath is not null) reasons.Add($"touches critical file class '{criticalPath}'");

        score = Math.Max(0, score);
        var level = score >= 70 ? "critical" : score >= 45 ? "high" : score >= 25 ? "medium" : "low";

        // The floor: a one-line change to a critical class is never "low".
        if (floorCritical && level is "low" or "medium")
        {
            level = "high";
            reasons.Add("risk floored to high: critical file class / irreversible / no rollback");
        }
        if (highRiskOp is not null && level == "low") level = "high";

        var requiresApproval = level is "high" or "critical" || highRiskOp is not null || floorCritical;
        return new RiskAssessment(level, score, reasons, requiresApproval);
    }
}
