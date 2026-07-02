using Anthill.Core.Autonomy;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Orchestration;

namespace Anthill.Api;

/// <summary>
/// The autonomous Colony Director. A long-lived supervisor that works the objective backlog:
/// budget + kill-switch check → pull ready objectives → run missions for them through the shared
/// job worker pool → record the outcomes → idle backoff → repeat.
///
/// Phase 2 added the LLM Strategist: instead of using the objective's charter verbatim every
/// cycle, it synthesises the next concrete goal from the charter + this objective's own recent
/// run history + colony pheromone memory, rejects near-duplicate goals, and — within a hard cap
/// — can enqueue follow-up objectives it discovers. It fails closed to the Phase 1 behaviour
/// (charter-as-goal) whenever routing is off or the model misbehaves, so the loop never blocks
/// or stalls on the LLM.
///
/// Phase 3 adds concurrency: up to <see cref="AnthillRuntime.AutonomyConcurrency"/> missions run
/// side by side, sized down each cycle by the <see cref="ResourceGovernor"/> when the host or
/// model backend is under pressure. Scheduling is strict priority with anti-starvation aging
/// (see <c>SqliteMemory.NextReadyObjectives</c>), and an objective never has two missions in
/// flight at once — which also keeps its run-outcome bookkeeping serial. All launching, reaping,
/// and outcome recording still happens on the single director thread, so BudgetGuard and
/// Strategist calls stay sequential by construction. Writes stay queue-for-review: the Director
/// only launches missions; it never approves or applies patches. The kill switch
/// (<see cref="AutonomyControl"/>) drains the loop: no new launches, in-flight missions finish
/// and are recorded, then the thread exits.
/// </summary>
public sealed class ColonyDirector : IDisposable
{
    private const string SystemMissionId = AnthillRuntime.SystemApiMissionId;

    private readonly Queen _queen;
    private readonly ApiJobRegistry _jobs;
    private readonly BudgetGuard _budget;
    private readonly Strategist _strategist;
    private readonly ResourceGovernor _governor;
    private readonly object _lifecycleLock = new();
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>One launched-but-not-yet-recorded mission. Touched only by the director thread.</summary>
    private sealed record InFlight(Objective Objective, AutonomyRun Run, ApiMissionJob Job, StrategistResult Strategy, DateTime Deadline);

    private readonly List<InFlight> _inFlight = new();
    // Snapshot for StatusSnapshot(), which is called from API threads while the loop runs.
    private volatile IReadOnlyList<InFlight> _inFlightSnapshot = Array.Empty<InFlight>();
    private volatile GovernorDecision? _lastGovernorDecision;

    public ColonyDirector(Queen queen, ApiJobRegistry jobs, ResourceGovernor? governor = null)
    {
        _queen = queen;
        _jobs = jobs;
        _budget = new BudgetGuard(queen.Memory);
        _strategist = new Strategist(queen.Router, queen.Memory);
        _governor = governor ?? new ResourceGovernor();
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
            metadata: new()
            {
                ["poll_seconds"] = AnthillRuntime.AutonomyPollSeconds,
                ["concurrency_configured"] = AnthillRuntime.AutonomyConcurrency,
                ["aging_minutes"] = AnthillRuntime.AutonomyAgingMinutes,
            });
        return true;
    }

    /// <summary>Stops the loop and engages the durable kill switch. In-flight missions finish (and are recorded) first.</summary>
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
        while (true)
        {
            try
            {
                ReapFinished();

                if (!_running || AutonomyControl.IsStopped)
                {
                    _running = false;
                    if (DrainInFlight()) break;
                    continue; // still draining — keep reaping
                }

                var decision = _budget.Evaluate();
                if (!decision.Allowed)
                {
                    if (decision.Code is "autonomy_disabled" or "kill_switch")
                    {
                        _running = false;
                        continue; // next iteration enters the drain path
                    }
                    if (_inFlight.Count == 0)
                    {
                        _queen.Memory.LogEvent(SystemMissionId, "autonomy_idle", decision.Reason, antName: "director",
                            metadata: new() { ["code"] = decision.Code });
                        Backoff();
                    }
                    else ShortSleep();
                    continue;
                }

                var governor = _governor.Evaluate(AnthillRuntime.AutonomyConcurrency);
                _lastGovernorDecision = governor;
                var openSlots = governor.EffectiveConcurrency - _inFlight.Count;

                if (openSlots > 0)
                {
                    var inFlightIds = _inFlight.Select(f => f.Objective.Id).ToList();
                    var candidates = _queen.Memory.NextReadyObjectives(openSlots, inFlightIds);
                    if (candidates.Count == 0 && _inFlight.Count == 0)
                    {
                        _queen.Memory.LogEvent(SystemMissionId, "autonomy_idle", "No ready objective in the backlog.",
                            antName: "director", metadata: new() { ["code"] = "empty_backlog" });
                        Backoff();
                        continue;
                    }
                    foreach (var objective in candidates)
                    {
                        // Re-check the hard rails before every single launch: an earlier launch in
                        // this same cycle may have consumed the last budgeted slot.
                        var launchDecision = _budget.Evaluate();
                        if (!launchDecision.Allowed) break;
                        LaunchMission(objective, governor);
                    }
                }

                if (_inFlight.Count > 0) ShortSleep();
                else Backoff();
            }
            catch (Exception ex)
            {
                _queen.Memory.LogEvent(SystemMissionId, "autonomy_error", $"Director loop error: {ex.Message}", antName: "director",
                    metadata: new() { ["error"] = ex.Message });
                if (_running) Backoff();
                else if (DrainInFlight()) break;
            }
        }
    }

    /// <summary>Launches one mission for <paramref name="objective"/> without blocking the loop.</summary>
    private void LaunchMission(Objective objective, GovernorDecision governor)
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
                ["in_flight"] = _inFlight.Count + 1,
                ["concurrency_effective"] = governor.EffectiveConcurrency,
                ["concurrency_configured"] = AnthillRuntime.AutonomyConcurrency,
                ["governor_code"] = governor.Code,
            });

        var job = _jobs.Submit(goal);
        // Missions are bounded by MaxMissionSeconds; cap the wait generously beyond that.
        var deadline = AnthillTime.NowUtc().AddSeconds(AnthillRuntime.MaxMissionSeconds + 120);
        _inFlight.Add(new InFlight(objective, run, job, strategy, deadline));
        _inFlightSnapshot = _inFlight.ToList();
    }

    /// <summary>Records outcomes for every in-flight mission whose job has finished (or blown its deadline).</summary>
    private void ReapFinished()
    {
        if (_inFlight.Count == 0) return;
        var now = AnthillTime.NowUtc();
        var finished = _inFlight.Where(f => f.Job.Status is not ("queued" or "running") || now > f.Deadline).ToList();
        if (finished.Count == 0) return;
        foreach (var flight in finished)
        {
            _inFlight.Remove(flight);
            RecordOutcome(flight);
        }
        _inFlightSnapshot = _inFlight.ToList();
    }

    private void RecordOutcome(InFlight flight)
    {
        var (objective, run, job, strategy) = (flight.Objective, flight.Run, flight.Job, flight.Strategy);
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
                ["in_flight"] = _inFlight.Count,
            });
    }

    /// <summary>
    /// Drain step for shutdown: reaps finished missions and reports whether the drain is
    /// complete. In-flight jobs keep running on the worker pool (missions are bounded by
    /// MaxMissionSeconds and each flight carries a hard deadline), so this always terminates.
    /// </summary>
    private bool DrainInFlight()
    {
        ReapFinished();
        if (_inFlight.Count == 0) return true;
        ShortSleep();
        return false;
    }

    /// <summary>Persists Strategist-discovered follow-up objectives and returns how many were saved.</summary>
    private int SaveFollowUps(List<Objective> followUps)
    {
        foreach (var fu in followUps) _queen.Memory.SaveObjective(fu);
        return followUps.Count;
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

    /// <summary>Poll cadence while missions are in flight — snappy reaping without busy-waiting.</summary>
    private static void ShortSleep() => Thread.Sleep(500);

    /// <summary>Live snapshot for the /autonomy/status endpoint.</summary>
    public Dictionary<string, object?> StatusSnapshot()
    {
        var now = AnthillTime.NowUtc();
        var inFlight = _inFlightSnapshot;
        var governor = _lastGovernorDecision;
        var next = _queen.Memory.NextReadyObjectives(1, inFlight.Select(f => f.Objective.Id).ToList()).FirstOrDefault();
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
            ["concurrency_configured"] = AnthillRuntime.AutonomyConcurrency,
            ["concurrency_effective"] = governor?.EffectiveConcurrency,
            ["governor_code"] = governor?.Code,
            ["governor_reason"] = governor?.Reason,
            ["governor_signals"] = governor?.Signals,
            ["aging_minutes"] = AnthillRuntime.AutonomyAgingMinutes,
            ["in_flight"] = inFlight.Select(f => new Dictionary<string, object?>
            {
                ["objective_id"] = f.Objective.Id, ["objective_title"] = f.Objective.Title,
                ["autonomy_run_id"] = f.Run.Id, ["mission_id"] = f.Job.MissionId,
                ["job_status"] = f.Job.Status, ["started_at"] = f.Run.StartedAt.ToIso(),
            }).ToList(),
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
