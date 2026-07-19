using Anthill.Core.Homelab.Approvals;

namespace Anthill.Core.Homelab.Actions;

/// <summary>
/// Deterministic blast-radius scoring (v2.3.0) over the rubric inputs that shipped on
/// <see cref="ActionProposal"/> in v1.14.0: dependency fan-out, service criticality, backup
/// coverage, internet exposure, rollback availability, plus the action class. Plain arithmetic —
/// no LLM involvement (NORTH_STAR §3.2 rule 5) — so the same proposal always scores the same and
/// tests can assert exact values. Missing data fails toward caution: unknown criticality scores
/// as if high, and a missing rollback note is the single largest penalty.
/// </summary>
public static class BlastRadius
{
    public sealed record Result(int Score, string Level, string Explanation);

    public static Result Score(ActionProposal p)
    {
        var reasons = new List<string>();
        var score = 0;

        // Dependency fan-out (v1.10 dependency map): every dependent that would feel this action.
        if (p.DependencyFanout > 0)
        {
            var pts = Math.Min(p.DependencyFanout * 2, 8); // cap so fan-out alone can't dominate
            score += pts; reasons.Add($"{p.DependencyFanout} dependent(s) (+{pts})");
        }

        // Service criticality — unknown scores like high (fail toward caution).
        var crit = (p.ServiceCriticality ?? "").Trim().ToLowerInvariant();
        var critPts = crit switch { "critical" => 4, "high" => 3, "normal" => 1, "low" => 0, _ => 3 };
        score += critPts;
        reasons.Add(crit.Length > 0 ? $"criticality {crit} (+{critPts})" : $"criticality unknown (+{critPts}, treated as high)");

        if (!p.BackupCovered) { score += 3; reasons.Add("no backup coverage (+3)"); }
        if (p.InternetExposed) { score += 3; reasons.Add("internet-exposed (+3)"); }
        if (string.IsNullOrWhiteSpace(p.RollbackNote)) { score += 4; reasons.Add("no rollback note (+4)"); }

        if (ActionCatalog.PowerActions.Contains(p.ActionType)) { score += 2; reasons.Add("power-state action (+2)"); }
        else if (ActionCatalog.LocalActions.Contains(p.ActionType)) { reasons.Add("local-only action (+0)"); }
        else { score += 1; reasons.Add("infrastructure action (+1)"); }

        var level = score <= 3 ? "low" : score <= 7 ? "medium" : score <= 11 ? "high" : "critical";
        return new Result(score, level, string.Join("; ", reasons));
    }

    /// <summary>Scores the proposal and writes the result back onto it (risk level + explanation fields).</summary>
    public static void Apply(ActionProposal p)
    {
        var result = Score(p);
        p.BlastRadiusScore = result.Score;
        p.BlastRadiusExplanation = result.Explanation;
        p.RiskLevel = result.Level;
    }
}
