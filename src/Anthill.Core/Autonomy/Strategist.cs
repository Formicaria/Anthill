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

        // One-shot objectives (max_runs == 1) are explicit, do-this-exact-thing tasks — e.g.
        // "create docs/x.md". Letting the LLM Strategist "diversify" them is pure drift (it once
        // rewrote a file-creation charter into "train a model"). Use the charter verbatim so the
        // operator's intent reaches the planner unchanged. Broad standing objectives (max_runs 0
        // or >1) still go through the Strategist to make forward progress across runs.
        if (objective.MaxRuns == 1)
            return new StrategistResult { Goal = FallbackGoal(objective), Source = "charter_verbatim",
                Notes = "One-shot objective (max_runs=1): charter used verbatim to preserve intent." };

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

        // Structural sprawl guard: stop enqueuing self-generated objectives once the open backlog
        // (pending + active) is already at the cap. Without this, 1 follow-up/run across a busy
        // Director compounds into hundreds of objectives regardless of the per-run rate limit.
        if (AnthillRuntime.AutonomyMaxBacklog > 0)
        {
            var openBacklog = _memory.ListObjectives(ObjectiveStatus.Pending).Count
                            + _memory.ListObjectives(ObjectiveStatus.Active).Count;
            if (openBacklog >= AnthillRuntime.AutonomyMaxBacklog) return result;
        }

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
- ""goal"" MUST directly accomplish the standing objective's charter above. Do NOT substitute a
  different, broader, or tangential task. If the charter names a concrete deliverable (create/add/
  write/edit a specific file, fix a specific thing, produce a specific artifact), the goal must
  produce exactly that deliverable — restate it concretely, do not reinterpret it.
- If there are NO prior runs, execute the charter as written (concretized just enough for the
  Queen to plan). Only when a recent run already accomplished the charter should the goal be the
  next incremental step toward the same charter — never a new topic.
- Keep the goal specific enough that the Queen can decompose it into tasks.
- ""follow_ups"" should almost always be empty. Add one ONLY if this mission genuinely uncovered a
  distinct, necessary new standing objective that the charter does not already cover — this is
  rare. Never invent follow-ups to seem productive. Max {AnthillRuntime.AutonomyMaxFollowupsPerRun}.

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
