using Anthill.Core.Autonomy;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Orchestration;

namespace Anthill.Api;

/// <summary>
/// The autonomous Colony Director (Phase 1 MVP). A long-lived supervisor that works the
/// objective backlog one mission at a time: budget + kill-switch check → pull the highest
/// priority ready objective → run a mission for it through the shared job worker → record the
/// outcome → idle backoff → repeat.
///
/// Phase 2 adds the LLM Strategist: instead of using the objective's charter verbatim every
/// cycle, it synthesises the next concrete goal from the charter + this objective's own recent
/// run history + colony pheromone memory, rejects near-duplicate goals, and — within a hard cap
/// — can enqueue follow-up objectives it discovers. It fails closed to the Phase 1 behaviour
/// (charter-as-goal) whenever routing is off or the model misbehaves, so the loop never blocks
/// or stalls on the LLM. Writes stay queue-for-review: the Director only launches missions; it
/// never approves or applies patches. The kill switch (<see cref="AutonomyControl"/>) halts it
/// before every mission.
/// </summary>
public sealed class ColonyDirector : IDisposable
{
    private const string SystemMissionId = AnthillRuntime.SystemApiMissionId;

    private readonly Queen _queen;
    private readonly ApiJobRegistry _jobs;
    private readonly BudgetGuard _budget;
    private readonly Strategist _strategist;
    private readonly object _lifecycleLock = new();
    private Thread? _thread;
    private volatile bool _running;

    public ColonyDirector(Queen queen, ApiJobRegistry jobs)
    {
        _queen = queen;
        _jobs = jobs;
        _budget = new BudgetGuard(queen.Memory);
        _strategist = new Strategist(queen.Router, queen.Memory);
        _queen.Memory.EnsureSystemMission(SystemMissionId, "System API events");
    }

    public bool IsRunning => _running;

    /// <summary>Starts the loop. Refuses if autonomy is disabled in config. Clears the kill switch.</summary>
    public bool Start()
    {
        if (!AnthillRuntime.EnableAutonomy) return false;
        lock (_lifecycleLock)
        {
            if (_running) return true;
            AutonomyControl.Resume();
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "anthill-colony-director" };
            _thread.Start();
        }
        _queen.Memory.LogEvent(SystemMissionId, "autonomy_started", "Colony Director started.", antName: "director",
            metadata: new() { ["poll_seconds"] = AnthillRuntime.AutonomyPollSeconds });
        return true;
    }

    /// <summary>Stops the loop and engages the durable kill switch. A mission already running finishes first.</summary>
    public void Stop(string reason = "api stop")
    {
        lock (_lifecycleLock)
        {
            _running = false;
            AutonomyControl.Stop(reason);
        }
        _queen.Memory.LogEvent(SystemMissionId, "autonomy_stopped", $"Colony Director stopped: {reason}", antName: "director");
    }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                if (AutonomyControl.IsStopped) { _running = false; break; }

                var decision = _budget.Evaluate();
                if (!decision.Allowed)
                {
                    _queen.Memory.LogEvent(SystemMissionId, "autonomy_idle", decision.Reason, antName: "director",
                        metadata: new() { ["code"] = decision.Code });
                    if (decision.Code is "autonomy_disabled" or "kill_switch") { _running = false; break; }
                    Backoff();
                    continue;
                }

                var objective = _queen.Memory.NextReadyObjective();
                if (objective is null)
                {
                    _queen.Memory.LogEvent(SystemMissionId, "autonomy_idle", "No ready objective in the backlog.",
                        antName: "director", metadata: new() { ["code"] = "empty_backlog" });
                    Backoff();
                    continue;
                }

                RunObjectiveOnce(objective);
            }
            catch (Exception ex)
            {
                _queen.Memory.LogEvent(SystemMissionId, "autonomy_error", $"Director loop error: {ex.Message}", antName: "director",
                    metadata: new() { ["error"] = ex.Message });
            }
            if (_running) Backoff();
        }
    }

    private void RunObjectiveOnce(Objective objective)
    {
        var strategy = _strategist.GenerateGoal(objective);
        var goal = strategy.Goal;
        var run = new AutonomyRun { ObjectiveId = objective.Id, GeneratedGoal = goal };
        // Persist the run at launch so it immediately counts toward the rate budget.
        _queen.Memory.SaveAutonomyRun(run);
        _queen.Memory.LogEvent(SystemMissionId, "autonomy_mission_started",
            $"Director launched a mission for objective: {objective.Title}", antName: "director",
            metadata: new()
            {
                ["objective_id"] = objective.Id, ["autonomy_run_id"] = run.Id, ["goal"] = goal,
                ["goal_source"] = strategy.Source, ["strategist_notes"] = strategy.Notes,
                ["writes"] = "queued_for_review",
            });

        var job = _jobs.Submit(goal);
        WaitForJob(job);

        var (missionStatus, score, success) = ReadOutcome(job);
        run.MissionId = job.MissionId;
        run.MissionStatus = missionStatus;
        run.SuccessScore = score;
        run.FinishedAt = AnthillTime.NowUtc();
        run.Notes = job.Error;

        // Only a successful mission's discoveries are worth enqueuing — a failed run's follow-ups
        // are, by construction, follow-ups to work that didn't actually land.
        var enqueuedFollowUps = success ? SaveFollowUps(strategy.FollowUps) : 0;
        run.FollowUpsCreated = enqueuedFollowUps;
        _queen.Memory.SaveAutonomyRun(run);

        var updated = _queen.Memory.RecordObjectiveRunOutcome(objective.Id, success);
        _queen.Memory.LogEvent(SystemMissionId, "autonomy_mission_finished",
            $"Director mission finished for objective: {objective.Title} ({missionStatus})", antName: "director",
            metadata: new()
            {
                ["objective_id"] = objective.Id, ["autonomy_run_id"] = run.Id, ["mission_id"] = job.MissionId,
                ["mission_status"] = missionStatus, ["success"] = success, ["success_score"] = score,
                ["objective_status"] = updated?.Status.Value(), ["run_count"] = updated?.RunCount,
                ["consecutive_failures"] = updated?.ConsecutiveFailures, ["follow_ups_created"] = enqueuedFollowUps,
            });
    }

    /// <summary>Persists Strategist-discovered follow-up objectives and returns how many were saved.</summary>
    private int SaveFollowUps(List<Objective> followUps)
    {
        foreach (var fu in followUps) _queen.Memory.SaveObjective(fu);
        return followUps.Count;
    }

    private void WaitForJob(ApiMissionJob job)
    {
        // Missions are bounded by MaxMissionSeconds; cap the wait generously beyond that.
        var deadline = AnthillTime.NowUtc().AddSeconds(AnthillRuntime.MaxMissionSeconds + 120);
        while (job.Status is "queued" or "running")
        {
            if (AnthillTime.NowUtc() > deadline) break;
            Thread.Sleep(250);
        }
    }

    private (string Status, double? Score, bool Success) ReadOutcome(ApiMissionJob job)
    {
        if (job.Status == "failed" || job.MissionId is null)
            return ("failed", null, false);
        var mission = _queen.Memory.GetMission(job.MissionId);
        var status = mission?.GetValueOrDefault("status")?.ToString() ?? job.Status;
        double? score = null;
        var rawScore = mission?.GetValueOrDefault("success_score");
        if (rawScore is not null && double.TryParse(rawScore.ToString(), out var s)) score = s;
        var success = status is "complete" or "partial";
        return (status, score, success);
    }

    private static void Backoff() => Thread.Sleep(TimeSpan.FromSeconds(AnthillRuntime.AutonomyPollSeconds));

    /// <summary>Live snapshot for the /autonomy/status endpoint.</summary>
    public Dictionary<string, object?> StatusSnapshot()
    {
        var now = AnthillTime.NowUtc();
        var next = _queen.Memory.NextReadyObjective();
        return new Dictionary<string, object?>
        {
            ["enabled"] = AnthillRuntime.EnableAutonomy,
            ["running"] = _running,
            ["kill_switch_engaged"] = AutonomyControl.IsStopped,
            ["poll_seconds"] = AnthillRuntime.AutonomyPollSeconds,
            ["missions_last_hour"] = _queen.Memory.CountAutonomyRunsSince(now.AddHours(-1)),
            ["missions_last_day"] = _queen.Memory.CountAutonomyRunsSince(now.AddDays(-1)),
            ["max_missions_per_hour"] = AnthillRuntime.AutonomyMaxMissionsPerHour,
            ["max_missions_per_day"] = AnthillRuntime.AutonomyMaxMissionsPerDay,
            ["backlog_pending"] = _queen.Memory.ListObjectives(ObjectiveStatus.Pending).Count,
            ["backlog_active"] = _queen.Memory.ListObjectives(ObjectiveStatus.Active).Count,
            ["next_objective"] = next is null ? null : new Dictionary<string, object?>
            {
                ["id"] = next.Id, ["title"] = next.Title, ["priority"] = next.Priority,
            },
            ["budget_decision"] = _budget.Evaluate().Code,
        };
    }

    public void Dispose()
    {
        _running = false;
    }
}
