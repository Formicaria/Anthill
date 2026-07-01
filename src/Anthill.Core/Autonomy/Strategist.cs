using System.Text.Json.Nodes;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Models;

namespace Anthill.Core.Autonomy;

/// <summary>
/// What the Strategist decided for one Director cycle: the concrete mission goal to run, any
/// new standing objectives discovered along the way, and where the goal came from (for the
/// audit trail / console).
/// </summary>
public sealed class StrategistResult
{
    public string Goal { get; set; } = "";
    public List<Objective> FollowUps { get; set; } = new();
    /// <summary>"strategist" when the LLM produced the goal; "fallback" when it degraded to the objective's charter verbatim (Phase 1 behaviour).</summary>
    public string Source { get; set; } = "fallback";
    /// <summary>Why it fell back, or any other diagnostic — surfaced in autonomy_run notes, never fatal.</summary>
    public string? Notes { get; set; }
}

/// <summary>
/// Phase 2 of the autonomy roadmap (see docs/AUTONOMY.md §4.2): turns one standing objective +
/// its own recent run history + colony pheromone memory into the next concrete mission goal,
/// instead of the Phase 1 Director just using the objective's charter verbatim every time.
///
/// Fails closed at every step: no router configured, a transport/auth error, unparseable JSON,
/// an empty goal, or a goal judged too similar to recent work for this objective all fall back
/// to the objective's charter exactly as Phase 1 did — the Director never blocks on the LLM and
/// never launches a wasted duplicate mission because the Strategist misbehaved.
/// </summary>
public sealed class Strategist
{
    private readonly ModelRouter? _router;
    private readonly SqliteMemory _memory;

    public Strategist(ModelRouter? router, SqliteMemory memory)
    {
        _router = router;
        _memory = memory;
    }

    public StrategistResult GenerateGoal(Objective objective)
    {
        var fallback = new StrategistResult { Goal = FallbackGoal(objective), Source = "fallback" };
        if (_router is null) return fallback;

        string response;
        try
        {
            response = _router.Generate("strategist", BuildPrompt(objective), antName: "strategist");
        }
        catch (Exception ex)
        {
            fallback.Notes = $"Strategist call threw: {ex.Message}";
            return fallback;
        }

        if (response.StartsWith("ERROR:", StringComparison.Ordinal))
        {
            fallback.Notes = response;
            return fallback;
        }

        try
        {
            var parsed = Json.ExtractJsonObject(response);
            var goal = (parsed["goal"]?.GetValue<string>() ?? "").Trim();
            if (goal.Length == 0)
            {
                fallback.Notes = "Strategist returned an empty goal.";
                return fallback;
            }

            if (IsNearDuplicate(objective.Id, goal, out var dupReason))
            {
                fallback.Notes = dupReason;
                return fallback;
            }

            return new StrategistResult
            {
                Goal = goal,
                FollowUps = ParseFollowUps(parsed, objective),
                Source = "strategist",
            };
        }
        catch (Exception ex)
        {
            fallback.Notes = $"Strategist response could not be parsed: {ex.Message}";
            return fallback;
        }
    }

    /// <summary>Phase 1 behaviour, kept as the fallback for every failure path above.</summary>
    private static string FallbackGoal(Objective objective) =>
        string.IsNullOrWhiteSpace(objective.Charter) ? objective.Title : objective.Charter;

    /// <summary>
    /// Rejects a generated goal whose keyword overlap with a recently completed run's goal (for
    /// the same objective) meets or exceeds <see cref="AnthillRuntime.AutonomyDedupeSimilarity"/>.
    /// Overlap is measured as intersection size over the smaller keyword set — a "containment"
    /// ratio, so a short goal that is entirely covered by a longer prior goal still counts as a
    /// near-duplicate even though a symmetric (Jaccard) score would understate it.
    /// </summary>
    private bool IsNearDuplicate(string objectiveId, string goal, out string reason)
    {
        reason = "";
        var goalKeywords = TextUtil.ExtractKeywords(goal);
        if (goalKeywords.Count == 0) return false;

        var recent = _memory.ListAutonomyRuns(objectiveId, limit: 10)
            .Where(r => r.GetValueOrDefault("mission_status")?.ToString() is "complete" or "partial");
        foreach (var run in recent)
        {
            var priorGoal = run.GetValueOrDefault("generated_goal")?.ToString() ?? "";
            var priorKeywords = TextUtil.ExtractKeywords(priorGoal);
            if (priorKeywords.Count == 0) continue;

            var overlap = goalKeywords.Intersect(priorKeywords).Count();
            var similarity = overlap / (double)Math.Min(goalKeywords.Count, priorKeywords.Count);
            if (similarity >= AnthillRuntime.AutonomyDedupeSimilarity)
            {
                reason = $"Generated goal was ~{similarity:P0} similar to a recent completed run for this objective " +
                          "(\"" + TextUtil.Truncate(priorGoal, 120) + "\"); falling back to the objective's charter.";
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Pulls at most <see cref="AnthillRuntime.AutonomyMaxFollowupsPerRun"/> follow-up objectives
    /// out of the model's response, dropping anything once the parent chain would exceed
    /// <see cref="AnthillRuntime.AutonomyMaxObjectiveDepth"/> — the backlog-explosion guard the
    /// design doc calls out as an open question. New objectives are queued one priority tier
    /// below their parent so they never starve the standing backlog.
    /// </summary>
    private List<Objective> ParseFollowUps(JsonObject parsed, Objective objective)
    {
        var result = new List<Objective>();
        if (AnthillRuntime.AutonomyMaxFollowupsPerRun <= 0) return result;
        if (parsed["follow_ups"] is not JsonArray raw || raw.Count == 0) return result;

        var parentDepth = _memory.ObjectiveDepth(objective.Id);
        if (parentDepth + 1 > AnthillRuntime.AutonomyMaxObjectiveDepth) return result;

        foreach (var item in raw.Take(AnthillRuntime.AutonomyMaxFollowupsPerRun))
        {
            if (item is not JsonObject obj) continue;
            var title = (obj["title"]?.GetValue<string>() ?? "").Trim();
            var charter = (obj["charter"]?.GetValue<string>() ?? "").Trim();
            if (title.Length == 0 || charter.Length == 0) continue;
            result.Add(new Objective
            {
                Title = title,
                Charter = charter,
                Priority = Math.Max(0, objective.Priority - 1),
                ParentObjectiveId = objective.Id,
            });
        }
        return result;
    }

    private string BuildPrompt(Objective objective)
    {
        var recentRuns = _memory.ListAutonomyRuns(objective.Id, limit: 5);
        var runsSummary = recentRuns.Count == 0
            ? "(no prior runs for this objective)"
            : string.Join("\n", recentRuns.Select(r =>
                $"- goal: {TextUtil.Truncate(r.GetValueOrDefault("generated_goal")?.ToString() ?? "", 160)} " +
                $"| status: {r.GetValueOrDefault("mission_status")}"));

        var trails = _memory.GetTopPheromoneTrails(8);
        var trailsSummary = trails.Count == 0
            ? "(no pheromone memory yet)"
            : string.Join("\n", trails.Select(t =>
                $"- {t.GetValueOrDefault("trail_key")} (strength {t.GetValueOrDefault("strength")})"));

        return $@"{AnthillRuntime.PromptInjectionPrefix}
ANTHILL v{AnthillRuntime.Version} | role: strategist | timestamp: {AnthillTime.NowUtc().ToIso()}
You are concise. Do not explain your reasoning unless asked.

You are the Strategist inside ANTHILL, a local swarm-intelligence AI harness running
unattended (24/7 autonomy). Your job is to turn one standing objective into the single next
concrete mission for the colony to run right now.

Standing objective:
  Title: {objective.Title}
  Charter: {objective.Charter}

Recent runs for this objective (most recent first):
{runsSummary}

Colony pheromone memory (routes/strategies that have worked well elsewhere):
{trailsSummary}

Rules:
- Return ONLY valid JSON. Do not wrap it in markdown code fences.
- ""goal"" must be a single concrete, actionable mission goal — specific enough that the Queen
  can decompose it into tasks, and meaningfully different from the recent runs listed above.
- Do not restate a goal that is essentially the same as a recent run; make forward progress.
- ""follow_ups"" is optional: at most {AnthillRuntime.AutonomyMaxFollowupsPerRun} genuinely new
  standing objective(s) this work surfaced. Omit or leave empty when nothing new was discovered.

Required JSON:
{{
  ""goal"": ""Specific mission goal text"",
  ""follow_ups"": [
    {{ ""title"": ""Short title"", ""charter"": ""Standing goal text for the new objective"" }}
  ]
}}
";
    }
}
