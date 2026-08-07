namespace Anthill.Core.Memory;

/// <summary>
/// The four questions colony memory is supposed to answer. v3.8.19 — post-refactor stage 3.
///
/// WHAT WAS ACTUALLY MISSING. The colony has 32 tables and stores a great deal: objectives, missions,
/// failures with classes, task attempts, skills, pheromones, a repository index. What it did not have
/// was retrieval. Exactly TWO methods answered anything resembling "what happened before" —
/// `GetRecentMissions` and `GetTopPheromoneTrails` — and neither answers a question anyone asks.
///
/// The post-refactor plan states them plainly:
///
///     "What has worked before?"  ·  "What usually fails?"
///     "Who solved this previously?"  ·  "What knowledge already exists?"
///
/// This file answers them from what is already stored. Nothing new is persisted, and no judgement is
/// added — these are aggregations over recorded outcomes.
///
/// WHAT THESE DELIBERATELY DO NOT DO. They do not rank by anything a model said. Every figure here
/// comes from a recorded status, a failure class, or a reinforcement count. Ranking on prose quality
/// is what the artifact and evidence store exists to make unnecessary, and doing it before that store
/// has producers is the mistake ADR-004 and the peer review both warn against.
/// </summary>
public sealed partial class SqliteMemory
{
    /// <summary>
    /// "What has worked before?" — the trails with a positive record, strongest first.
    ///
    /// Learning-bearing signals only, and that filter is the point: operational telemetry, tool
    /// reliability and source-quality heuristics are recorded but were never allowed to steer
    /// strategy (v2.26.0). A recall method that ignored the distinction would quietly re-admit them.
    ///
    /// The predicate is copied from <c>GetTopPheromoneTrails</c> deliberately rather than invented.
    /// The first draft of this method filtered on <c>signal_category = 'learning'</c> — a category
    /// that does not exist — so it would have returned an empty list forever, with no error and no
    /// failing test. Two methods answering one question must ask it the same way.
    /// </summary>
    public List<Dictionary<string, object?>> WhatHasWorked(int limit = 10) =>
        Query(@"SELECT trail_key, trail_type, strength, success_count, failure_count, last_updated
                FROM pheromone_trails
                WHERE legacy = 0
                  AND success_count > failure_count
                  AND (signal_category IN ('procedural_learning', 'routing_preference')
                       OR (signal_category = '' AND success_count > 0))
                ORDER BY strength DESC, success_count DESC
                LIMIT @lim", ("@lim", Math.Clamp(limit, 1, 200)));

    /// <summary>
    /// "What usually fails?" — failure classes by frequency, with how often that class was retried
    /// and still failed.
    ///
    /// The retry column is the useful half. A class that fails once and succeeds on retry is a flake;
    /// one that fails every attempt is a wall, and the colony should stop spending budget on it. That
    /// distinction is already in <c>task_attempts</c> and has never been read.
    /// </summary>
    public List<Dictionary<string, object?>> WhatUsuallyFails(int limit = 10) =>
        Query(@"SELECT failure_class,
                       COUNT(*)                                   AS occurrences,
                       COUNT(DISTINCT task_id)                    AS distinct_tasks,
                       SUM(CASE WHEN number > 1 THEN 1 ELSE 0 END) AS failed_again_on_retry
                FROM task_attempts
                WHERE failure_class IS NOT NULL AND failure_class <> ''
                GROUP BY failure_class
                ORDER BY occurrences DESC
                LIMIT @lim", ("@lim", Math.Clamp(limit, 1, 200)));

    /// <summary>
    /// "Who solved this previously?" — which roles have a record of completing work of this shape.
    ///
    /// Matched on the task TITLE rather than on a model's summary, because the title is what the
    /// planner wrote and is stable; a summary is generated prose and would make this method rank by
    /// fluency. Crude, and honestly so: it is a substring match, not a semantic one, and it will miss
    /// synonyms. Naming that limit is better than hiding it behind an embedding nobody can inspect.
    /// </summary>
    public List<Dictionary<string, object?>> WhoSolvedThis(string topic, int limit = 10)
    {
        var needle = (topic ?? "").Trim();
        if (needle.Length < 3) return new List<Dictionary<string, object?>>();

        return Query(@"SELECT r.ant_name,
                              COUNT(*)                                        AS completions,
                              SUM(CASE WHEN r.success = 1 THEN 1 ELSE 0 END)   AS successes,
                              MAX(t.title)                                    AS example
                       FROM task_results r
                       JOIN tasks t ON t.id = r.task_id
                       WHERE t.title LIKE @needle AND r.success = 1
                       GROUP BY r.ant_name
                       ORDER BY successes DESC, completions DESC
                       LIMIT @lim",
                     ("@needle", $"%{needle}%"), ("@lim", Math.Clamp(limit, 1, 200)));
    }

    /// <summary>
    /// "What knowledge already exists?" — the certified procedures, plus how much evidence each has.
    ///
    /// Candidates are excluded. A candidate is a route observed once and never re-verified; offering
    /// it as existing knowledge is how observation gets mistaken for proof, which is the exact line
    /// v2.23.0 drew when it made registration record no outcome.
    /// </summary>
    public List<Dictionary<string, object?>> WhatKnowledgeExists(int limit = 25) =>
        Query(@"SELECT id, purpose, status, success_count, failure_count, consecutive_failures,
                       verification_policy, last_validated
                FROM skills
                WHERE status <> 'Candidate'
                ORDER BY success_count DESC, last_validated DESC
                LIMIT @lim", ("@lim", Math.Clamp(limit, 1, 200)));
}
