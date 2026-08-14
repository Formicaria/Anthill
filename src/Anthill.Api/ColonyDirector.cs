using Anthill.Core.Autonomy;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Orchestration;
using Anthill.Core.Outcomes;

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

    /// <summary>
    /// Starts the loop. Refuses if autonomy is disabled in config.
    ///
    /// v2.26.0 pre-V3 hardening: starting the Director NEVER clears the STOP sentinel. STOP is
    /// documented as durable with no auto-clear — yet this method called AutonomyControl.Resume(),
    /// and the --autonomous boot path calls this method, so a process restart silently cleared an
    /// operator STOP. Starting the process and resuming autonomous work are different acts: the
    /// loop may start (it checks IsStopped before every mission and launches nothing while
    /// stopped), but only an explicit operator resume clears the sentinel.
    /// </summary>
    public bool Start()
    {
        if (!AnthillRuntime.EnableAutonomy) return false;
        lock (_lifecycleLock)
        {
            if (_running) return true;
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
                // The loop's last line of defence — and it runs on a plain background THREAD, so an
                // exception that escapes here does not fail a mission or a request: it kills the
                // ENTIRE process. CI proved it (v0.3.8.56): a test's teardown deleted the temp
                // colony out from under a director that woke mid-poll, the idle log threw 'unable
                // to open database file', and this handler's own LogEvent threw the same exception
                // again — unhandled on a bare thread, host dead, a fully green run aborted. The
                // same hole in production: a full disk or an unmounted volume would not degrade
                // the director, it would take the whole ship down. If even the error cannot be
                // logged, the colony this director writes to is already gone — leave quietly.
                try
                {
                    _queen.Memory.LogEvent(SystemMissionId, "autonomy_error", $"Director loop error: {ex.Message}", antName: "director",
                        metadata: new() { ["error"] = ex.Message });
                    if (_running) Backoff();
                    else if (DrainInFlight()) break;
                }
                catch
                {
                    break;
                }
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
        // v2.24.0 Phase C6: follow-ups derived from what verification FOUND, not only from what the
        // Strategist proposed. The verifier records "Missing Steps:" — a concrete list of what the
        // mission did not do — and until now nothing read it. Evidence-derived follow-ups carry
        // their own budget and a depth cap, so a finding can never extend an objective forever.
        var evidenceFollowUps = success && job.MissionId is { Length: > 0 } evidenceMissionId
            ? EvidenceFollowUps.From(_queen.Memory.GetTasksForMission(evidenceMissionId, 200),
                                     evidenceMissionId, objective, missionStatus)
            : (IReadOnlyList<Objective>)Array.Empty<Objective>();
        var enqueuedFollowUps = success
            // v2.26.0: the two follow-up sources are no longer merged into one admission path.
            // Evidence-derived follow-ups (verified mission + structured finding + budgets) remain
            // auto-admitted; Strategist proposals are model opinions and land as `suggested`,
            // requiring operator approval before they can execute.
            ? SaveFollowUps(strategy.FollowUps, job.MissionId, run.Id, suggested: true)
              + SaveFollowUps(evidenceFollowUps, job.MissionId, run.Id, suggested: false)
            : 0;
        if (evidenceFollowUps.Count > 0)
            _queen.Memory.LogEvent(SystemMissionId, "evidence_follow_ups_created",
                $"{evidenceFollowUps.Count} follow-up(s) created from verification findings.", antName: "director",
                metadata: new()
                {
                    ["objective_id"] = objective.Id, ["mission_id"] = job.MissionId,
                    ["titles"] = evidenceFollowUps.Select(f => f.Title).ToList(),
                });
        run.FollowUpsCreated = enqueuedFollowUps;
        _queen.Memory.SaveAutonomyRun(run);

        var updated = _queen.Memory.RecordObjectiveRunOutcome(objective.Id, success, score);
        _queen.Memory.LogEvent(SystemMissionId, "autonomy_mission_finished",
            $"Director mission finished for objective: {objective.Title} ({missionStatus})", antName: "director",
            metadata: new()
            {
                ["objective_id"] = objective.Id, ["autonomy_run_id"] = run.Id, ["mission_id"] = job.MissionId,
                ["mission_status"] = missionStatus, ["success"] = success, ["success_score"] = score,
                ["objective_status"] = updated?.Status.Value(), ["run_count"] = updated?.RunCount,
                ["consecutive_failures"] = updated?.ConsecutiveFailures, ["follow_ups_created"] = enqueuedFollowUps,
                ["success_ema"] = updated?.SuccessEma, ["in_flight"] = _inFlight.Count,
            });

        if (updated is not null) EvaluateObjectiveLifecycle(updated, success, enqueuedFollowUps);

        // Phase 5: on a successful mission, try to auto-apply any allowlisted patches it produced
        // (apply → build+test verify → keep or roll back). Fail-closed and no-op unless the
        // operator has enabled it AND configured a path allowlist. Runs on the director thread,
        // after bookkeeping, so it never races the loop.
        if (success && job.MissionId is { Length: > 0 } mid)
        {
            try { AutoApplyRunner.Run(_queen, mid); }
            catch (Exception ex)
            {
                _queen.Memory.LogEvent(SystemMissionId, "autonomy_autoapply_error",
                    $"Auto-apply run errored for mission {mid}: {ex.Message}", antName: "director",
                    metadata: new() { ["mission_id"] = mid, ["error"] = ex.Message });
            }
        }
    }

    /// <summary>
    /// v1.8.16 objective lifecycle: after an outcome lands, decide whether the objective should end,
    /// and why. The precedence — clean completion first (one-shot / verification-only / run-budget
    /// exhausted), then the circuit-breaker failure pause, then the Phase 4 loop/stale retirement —
    /// ensures loop detection is NOT the normal ending path for successful maintenance work, while
    /// still catching true autonomy loops. Runs on the director thread only, after the outcome is
    /// recorded, so it never races the objective's own bookkeeping.
    /// </summary>
    private void EvaluateObjectiveLifecycle(Objective objective, bool success, int followUpsCreated)
    {
        var alreadyDone = objective.Status == ObjectiveStatus.Done;       // run-budget rail fired this run
        var breakerPaused = objective.Status == ObjectiveStatus.Paused;   // consecutive-failure rail fired

        // 1) Clean completion — the normal ending path for one-shot / verification-only objectives
        //    (and for run-budget exhaustion). This deliberately runs BEFORE loop detection.
        //
        // v2.22.0: the objective's own run history decides whether budget exhaustion counts as
        // completion. Previously "Done" fired on RunCount >= MaxRuns regardless, so an objective
        // that failed every attempt ended in the same state as one that succeeded on the first —
        // reporting exhaustion as achievement.
        var progress = ObjectiveProgress.Assess(_queen.Memory.ListAutonomyRuns(objective.Id, limit: 200));
        var completion = ObjectiveLifecycle.EvaluateCompletion(objective, success, followUpsCreated, alreadyDone, progress);
        if (completion is not null) { StampObjectiveEnd(objective, completion, "objective_completed"); return; }

        // 2) Circuit breaker already paused it for repeated failures — record that as the end reason.
        if (breakerPaused)
        {
            StampObjectiveEnd(objective, new ObjectiveEndDecision(ObjectiveStatus.Paused, ObjectiveEndReason.Failed,
                $"Paused after {objective.ConsecutiveFailures} consecutive failures (circuit breaker)."), "objective_failed");
            return;
        }

        // 3) Phase 4 loop/stale retirement — preserved strictly for true repeated loops.
        CheckRetirement(objective);
    }

    /// <summary>
    /// Phase 4 learning loop: retire (auto-pause) an objective that is looping on near-identical
    /// goals or has gone stale (low success EMA). Retirement is a pause + <c>objective_retired</c>
    /// event — a human reviews and resumes. Preserved from v1.8.14; the v1.8.16 lifecycle only
    /// reaches here after clean-completion has been ruled out.
    /// </summary>
    private void CheckRetirement(Objective objective)
    {
        var recentGoals = _queen.Memory
            .ListAutonomyRuns(objective.Id, limit: Math.Max(AnthillRuntime.AutonomyLoopWindow, 1))
            .Select(r => r.GetValueOrDefault("generated_goal")?.ToString() ?? "")
            .ToList();
        var decision = ObjectiveLearning.EvaluateRetirement(objective, recentGoals);
        if (decision is null) return;

        // Stamp the retirement onto the objective's metadata so the UI can surface it (retired
        // objectives are shown in "Completed Objectives" instead of the paused backlog). Keep the
        // legacy retired_* markers for back-compat and add the unified v1.8.16 end_reason.
        objective.Status = ObjectiveStatus.Paused;
        objective.Metadata["retired_code"] = decision.Code;      // "looping_goals" | "stale_low_success"
        objective.Metadata["retired_reason"] = decision.Reason;
        objective.Metadata["retired_at"] = AnthillTime.NowUtc().ToIso();
        objective.Metadata["end_reason"] = ObjectiveEndReason.RetiredLooping;
        objective.Metadata["end_detail"] = decision.Reason;
        objective.Metadata["ended_at"] = AnthillTime.NowUtc().ToIso();
        _queen.Memory.SaveObjective(objective);
        _queen.Memory.LogEvent(SystemMissionId, "objective_retired",
            $"Director retired objective \"{objective.Title}\" ({decision.Code}): {decision.Reason}",
            antName: "director",
            metadata: new()
            {
                ["objective_id"] = objective.Id, ["objective_title"] = objective.Title,
                ["code"] = decision.Code, ["reason"] = decision.Reason, ["end_reason"] = ObjectiveEndReason.RetiredLooping,
                ["success_ema"] = objective.SuccessEma, ["run_count"] = objective.RunCount,
                ["action"] = "paused_for_review",
            });
    }

    /// <summary>Stamps a clean end-of-lifecycle decision onto the objective and logs it for the console.</summary>
    private void StampObjectiveEnd(Objective objective, ObjectiveEndDecision decision, string eventType)
    {
        objective.Status = decision.Status;
        objective.Metadata["end_reason"] = decision.EndReason;
        objective.Metadata["end_detail"] = decision.Detail;
        objective.Metadata["ended_at"] = AnthillTime.NowUtc().ToIso();
        _queen.Memory.SaveObjective(objective);
        _queen.Memory.LogEvent(SystemMissionId, eventType,
            $"Objective \"{objective.Title}\" ended: {ObjectiveEndReason.Label(decision.EndReason)} — {decision.Detail}",
            antName: "director",
            metadata: new()
            {
                ["objective_id"] = objective.Id, ["objective_title"] = objective.Title,
                ["end_reason"] = decision.EndReason, ["end_detail"] = decision.Detail,
                ["status"] = decision.Status.Value(), ["run_count"] = objective.RunCount,
                ["success_ema"] = objective.SuccessEma,
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

    /// <summary>
    /// Persists Strategist-discovered follow-up objectives, stamping which mission/run created
    /// them (so mission reports can show "objectives this mission created"). Returns how many were saved.
    /// </summary>
    private int SaveFollowUps(IReadOnlyList<Objective> followUps, string? missionId, string runId, bool suggested)
    {
        foreach (var fu in followUps)
        {
            if (missionId is not null) fu.Metadata["created_by_mission_id"] = missionId;
            fu.Metadata["created_by_run_id"] = runId;
            if (suggested)
            {
                // v2.26.0 pre-V3 hardening: a Strategist follow-up is a model OPINION, not a
                // proven discovery. It lands as `suggested` — visible, auditable, and NOT
                // executable until an operator approves it (POST /objectives/{id}/approve).
                // Evidence-derived follow-ups (verified mission + structured finding + budgets)
                // are the only auto-admitted path.
                fu.Status = ObjectiveStatus.Suggested;
                fu.Metadata["origin"] = "strategist_suggestion";
            }
            _queen.Memory.SaveObjective(fu);
        }
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
        // v2.19.0: `partial` is NOT success. v2.26.0: the answer is now READ from the one
        // persisted evaluation rather than re-derived from task rows — the row re-derivation
        // could disagree with the live path (rows lacked fields like criticality), which meant
        // objective EMA, follow-ups, closure and AUTO-APPLY keyed off a different truth than the
        // mission that just ran. A row without a persisted evaluation is legacy: never verified.
        var evaluation = job.MissionId is { Length: > 0 } mid ? _queen.Memory.LoadMissionEvaluation(mid) : null;
        if (evaluation is not null)
            return (evaluation.OutcomeCode, score, evaluation.IsPositive);

        // Legacy fallback (pre-v2.26 rows only): the old derivation, and it can never be positive —
        // a mission whose evidence predates the canonical evaluator is not retroactively promoted.
        var taskRows = job.MissionId is { Length: > 0 } legacyMid
            ? _queen.Memory.GetTasksForMission(legacyMid, 200)
            : new List<Dictionary<string, object?>>();
        var legacyOutcome = MissionOutcome.ResolveFromStatusText(status, MissionVerification.IsSatisfiedFromRows(taskRows));
        return (legacyOutcome, score, false);
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
            ["learning_enabled"] = AnthillRuntime.AutonomyLearningEnabled,
            ["autoapply_enabled"] = AnthillRuntime.AutonomyAutoApplyEnabled,
            ["autoapply_paths"] = AnthillRuntime.AutonomyAutoApplyPaths.Count,
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
