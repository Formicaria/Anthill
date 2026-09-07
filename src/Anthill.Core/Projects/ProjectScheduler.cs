using Anthill.Core.Common;
using Anthill.Core.Conversations;
using Anthill.Core.Memory;

namespace Anthill.Core.Projects;

/// <summary>
/// v0.3.8.48 — the schedule executor. A 30-second tick scans for due, enabled schedules,
/// claims each atomically (see <see cref="SqliteMemory.TryClaimSchedule"/>), and turns the
/// occurrence into a REAL conversation inside the schedule's project — the operator can open
/// it, read what happened, review changes, and keep talking to it. There is no cloud: this
/// runs while the Anthill host runs, and the UI says so in those words.
///
/// Missed occurrences resolve conservatively at startup and on every tick: a schedule whose
/// next_run_at fell during downtime fires ONCE (trigger "missed_catchup"), then recomputes
/// forward. A backlog is never replayed — ten missed dailies are one run, not ten.
///
/// Overlap: "skip" (default) records a skipped_overlap run when the previous one is still
/// going; "queue" lets the new occurrence start when claimed after the running one finishes.
/// One-time schedules disable themselves after a completed run.
/// </summary>
public sealed class ProjectScheduler : IDisposable
{
    private readonly SqliteMemory _memory;
    private readonly ConversationRunner _conversations;
    private readonly string _instanceId = "host-" + Guid.NewGuid().ToString("N")[..8];
    private Timer? _timer;

    public ProjectScheduler(SqliteMemory memory, ConversationRunner conversations)
    {
        _memory = memory;
        _conversations = conversations;
    }

    public void Start()
    {
        RecoverAfterRestart();
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Restart recovery: clear this-process claims that can no longer be running, and mark runs
    /// the previous process left "running" as failed with an honest reason — a run that died with
    /// the host must not read as in-flight forever.
    /// </summary>
    private void RecoverAfterRestart()
    {
        foreach (var s in _memory.LoadAllSchedules())
        {
            foreach (var run in _memory.LoadScheduleRuns(s.Id, 5).Where(r => r.Status == "running"))
                _memory.SaveScheduleRun(run with
                {
                    Status = "failed",
                    Summary = "the Anthill host stopped while this run was in flight",
                    FinishedAt = AnthillTime.NowUtc(),
                });
            if (s.ClaimedBy is not null)
                _memory.SaveSchedule(s with { ClaimedBy = null, ClaimedAt = null });
        }
    }

    internal void Tick()
    {
        var now = AnthillTime.NowUtc();
        foreach (var s in _memory.LoadAllSchedules())
        {
            if (!s.Enabled || s.NextRunAt is null || s.NextRunAt > now) continue;
            if (!_memory.TryClaimSchedule(s.Id, _instanceId, now)) continue;
            var missed = now - s.NextRunAt.Value > TimeSpan.FromMinutes(5);
            try { Execute(s, missed ? "missed_catchup" : "schedule"); }
            catch (Exception error)
            {
                _memory.SaveScheduleRun(new ScheduleRun(
                    Guid.NewGuid().ToString("N")[..12], s.Id, s.ProjectId, null, null,
                    "failed", "schedule", error.Message, now, AnthillTime.NowUtc()));
            }
            finally { Advance(s, now); }
        }
    }

    /// <summary>Run now, from the UI. Overlap policy still applies; the claim does not.</summary>
    public ScheduleRun RunNow(ProjectSchedule schedule, string requestedBy) =>
        Execute(schedule, "manual", requestedBy);

    private ScheduleRun Execute(ProjectSchedule s, string trigger, string? requestedBy = null)
    {
        var now = AnthillTime.NowUtc();
        var runId = Guid.NewGuid().ToString("N")[..12];

        // Overlap: a previous run of THIS schedule still marked running means skip (recorded, not
        // silent) under the default policy. Until v0.3.8.137 this check could never fire for real:
        // runs were stamped terminal the moment their mission ROW existed, so nothing was ever
        // "running" by the time the next occurrence looked. Now that a run stays running until its
        // mission settles, this line is the overlap policy actually working.
        if (s.OverlapPolicy == "skip"
            && _memory.LoadScheduleRuns(s.Id, 3).Any(r => r.Status == "running"))
        {
            var skipped = new ScheduleRun(runId, s.Id, s.ProjectId, null, null,
                "skipped_overlap", trigger, "previous run still in progress", now, now);
            _memory.SaveScheduleRun(skipped);
            return skipped;
        }

        // The run IS a conversation, in the schedule's project, under the schedule's approval
        // mode — attributed to whoever created the schedule (or pressed Run now), because that
        // person made the standing decision. Fail-closed rule intact: no author, no permission.
        var by = string.IsNullOrWhiteSpace(requestedBy) ? s.CreatedBy : requestedBy;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Title = $"{s.Name} — {TimeZoneInfo.ConvertTimeFromUtc(now, Tz(s.Timezone)):MMM d HH:mm}",
            ProjectId = s.ProjectId,
            Policy = s.ApprovalMode,
            PolicySetBy = s.ApprovalMode == EscalationPolicy.Ask ? null : by,
            PolicySetAt = s.ApprovalMode == EscalationPolicy.Ask ? null : now,
        };
        _memory.SaveConversation(conversation);

        var run = new ScheduleRun(runId, s.Id, s.ProjectId, conversation.Id, null,
            "running", trigger, null, now, null);
        _memory.SaveScheduleRun(run);

        var answers = s.ApprovalMode == EscalationPolicy.Ask
            ? null
            : new Dictionary<string, string> { [ConversationRunner.StartMissionAction] = "approve" };

        // v0.3.8.137, the review's item 3: the runner returns as soon as the mission ROW exists,
        // and this method used to stamp the run "complete" right then — every schedule's history
        // said finished-in-milliseconds about work that ran for minutes or failed outright, and
        // the overlap check above could never see a run that was actually still in flight. The
        // truth now arrives through the settle callback, which the runner fires on its background
        // thread when the pipeline call has actually returned (or thrown).
        var outcome = _conversations.Run(conversation, s.Prompt, ConversationMode.Mission, answers,
            onMissionSettled: missionId => SettleRun(runId, missionId));

        // A fast mission can settle before this line runs, so every write below re-reads the row
        // under the same lock the callback takes and defers to a terminal status already stamped.
        lock (_runWrites)
        {
            var current = _memory.LoadScheduleRun(runId) ?? run;
            if (current.Status != "running") return current;

            // Ask-mode runs that stopped at the gate WAIT, visibly — the pending decision
            // surfaces in the project conversation, and the run says so instead of converting
            // itself to auto. A refusal never started the background thread, so no callback is
            // coming and these stamps are final.
            current = !outcome.Started && outcome.Decision is { Allowed: false }
                ? current with { Status = "waiting_approval", Summary = outcome.Summary, FinishedAt = AnthillTime.NowUtc() }
                : !outcome.Started
                    ? current with { Status = "failed", Summary = outcome.Summary, FinishedAt = AnthillTime.NowUtc() }
                    // Started and still going: record WHICH mission, keep the run running. The
                    // settle callback owns the terminal stamp.
                    : current with { MissionId = outcome.MissionId, Summary = outcome.Summary };
            _memory.SaveScheduleRun(current);
            return current;
        }
    }

    /// <summary>
    /// Serializes run-row writes between <see cref="Execute"/>'s thread and the runner's
    /// settle-callback thread, because both do load-modify-save on the same row.
    /// </summary>
    private readonly object _runWrites = new();

    /// <summary>
    /// The mission behind a schedule run has settled — the pipeline call returned or threw.
    /// Stamp the run with what ACTUALLY happened, read from the mission row itself rather than
    /// inferred from "it started". An empty mission id means the pipeline died before creating a
    /// row; that is a failed run, said in those words.
    /// </summary>
    private void SettleRun(string runId, string missionId)
    {
        lock (_runWrites)
        {
            var current = _memory.LoadScheduleRun(runId);
            if (current is null || current.Status != "running") return;

            string status;
            string summary;
            if (missionId.Length == 0)
            {
                status = "failed";
                summary = "the pipeline settled without ever reporting a mission id";
            }
            else
            {
                var mission = _memory.GetMission(missionId);
                var missionStatus = mission?["status"] as string ?? "";
                status = missionStatus switch
                {
                    "complete" => "complete",
                    "partial" => "partial",
                    "failed" => "failed",
                    // Settled but the mission row does not read terminal (cancelled mid-flight,
                    // or a row that cannot be read back): failed, with the observation recorded
                    // rather than a guess dressed as a status.
                    _ => "failed",
                };
                var result = (mission?["user_result"] as string)?.Trim();
                var firstLine = string.IsNullOrWhiteSpace(result)
                    ? $"mission {missionId} settled as '{(missionStatus.Length == 0 ? "unreadable" : missionStatus)}'"
                    : result!.Split('\n')[0].Trim();
                summary = firstLine.Length > 200 ? firstLine[..200] + "…" : firstLine;
            }

            _memory.SaveScheduleRun(current with
            {
                MissionId = current.MissionId ?? (missionId.Length == 0 ? null : missionId),
                Status = status,
                Summary = summary,
                FinishedAt = AnthillTime.NowUtc(),
            });
        }
    }

    private void Advance(ProjectSchedule s, DateTime now)
    {
        var current = _memory.LoadSchedule(s.Id) ?? s;
        var next = current.ComputeNextRun(now);
        _memory.SaveSchedule(current with
        {
            ClaimedBy = null,
            ClaimedAt = null,
            LastRunAt = now,
            NextRunAt = next,
            // One-time schedules retire themselves after their run; the record stays.
            Enabled = current.TriggerType == "once" ? false : current.Enabled,
            UpdatedAt = now,
        });
    }

    private static TimeZoneInfo Tz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }

    public void Dispose() => _timer?.Dispose();
}
