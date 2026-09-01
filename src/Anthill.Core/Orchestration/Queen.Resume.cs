using Anthill.Core.Domain;
using Anthill.SDK.Common;
using Anthill.SDK.Reasoning;

namespace Anthill.Core.Orchestration;

/// <summary>
/// What a resumption did, said in terms an operator can act on. v0.3.8.110.
/// </summary>
/// <param name="Resumed">True when tasks were actually re-run. False is the ordinary case for an
/// approval that unblocked nothing, and it is not an error.</param>
/// <param name="Reason">Why, in one sentence. Populated whether or not anything ran, because
/// "nothing was replayed" with no reason is the shape `.105`'s refusal had before it named what it
/// was waiting for.</param>
/// <param name="TasksReplayed">How many tasks were reset and dispatched again.</param>
/// <param name="Outcome">The mission's outcome code after re-evaluation, or null when nothing ran
/// and the mission was therefore not re-graded.</param>
public sealed record MissionResumption(
    bool Resumed, string Reason, string MissionId, int TasksReplayed, string? Outcome);

public sealed partial class Queen
{
    /// <summary>
    /// AN APPROVED DECISION REPLAYS THE REFUSED STEP. v0.3.8.110, PLAN.md §2b `.110`.
    ///
    /// WHAT WAS WRONG, in the words `.105` shipped and then had to keep repeating: "Approving does
    /// not replay the refused step; it settles the question." That sentence was honest and it was
    /// the whole defect. A mission that reached a side-effecting action, refused it because absence
    /// of an answer is not consent, and stopped — stayed stopped forever. The operator's approval
    /// changed a row and a grade; the work the approval was FOR never happened, and the only way to
    /// get it was to run the whole mission again from the goal.
    ///
    /// It was deferred three times, and not for want of will: nothing in this tree could read a
    /// finished mission back as an object graph. <see cref="MissionRehydration"/> is that piece, and
    /// this is what it was built for.
    ///
    /// THE THREE THINGS A REPLAY NEEDS, and each of them was missing:
    ///
    /// <list type="number">
    /// <item>A DECISION THE TOOL GATE WILL HONOUR. Approving wrote to `approval_requests` and
    ///   `OperatorDecisions.ForMission` read `escalation_decisions` — two disjoint tables, so a
    ///   replay would have refused identically and filed the same question again. `.110` teaches
    ///   that reader the approval ledger.</item>
    /// <item>THE GRAPH. See <see cref="MissionRehydration"/>.</item>
    /// <item>A TASK THE SCHEDULER WILL DISPATCH. A refused task is terminal, and every terminal
    ///   status is one the scheduler refuses to run. The reset below is narrow and explicit rather
    ///   than a general "un-finish this task", because widening it is how a replay comes to re-run
    ///   work that succeeded.</item>
    /// </list>
    ///
    /// ONLY THE TASKS THAT WERE REFUSED FOR THIS ACTION. Resolved from the mission's own
    /// `escalation_refused` events, which carry the task id and the tool name — not from "every
    /// failed task", which would replay a coder whose patch was rejected on its merits, and not from
    /// the whole mission, which is the re-run this exists to avoid. A completed task is never
    /// touched: its side effects already landed, and re-running it would duplicate them.
    ///
    /// THE DEADLINE IS ANCHORED AT THE RESUME, NOT AT THE ORIGINAL START, and this contradicts a
    /// comment `RunMission` has carried since v3.1.0 — "a resumed run compares against the same
    /// wall-clock boundary the original did instead of restarting its clock". That was written about
    /// a resumption that did not exist, and it is wrong now that one does: the mission was not
    /// running while it waited, it was waiting on a person. Charging human latency against the
    /// mission's budget would make every approval that took longer than `MaxMissionSeconds` resume
    /// straight into a timeout — the replay would be admitted, dispatch nothing, and grade the
    /// mission as having timed out on work it was never given a chance to do. The original comment
    /// is corrected in place rather than left to contradict this one.
    ///
    /// IT NEVER THROWS. Every caller is on an approval path whose safety-relevant half — the
    /// operator said yes — is already decided and already recorded. A resumption that fails must
    /// leave the approval standing and say so, not unwind it.
    /// </summary>
    public MissionResumption ResumeMission(string? missionId, string? action)
    {
        if (string.IsNullOrWhiteSpace(missionId) || string.IsNullOrWhiteSpace(action))
            return new MissionResumption(false, "no mission or action was named.", missionId ?? "", 0, null);

        try
        {
            var mission = MissionRehydration.Load(Memory, missionId);
            if (mission is null)
                return new MissionResumption(false,
                    $"mission {missionId} is not in the store, so there is nothing to replay.",
                    missionId!, 0, null);

            var refused = RefusedTaskIds(missionId!, action!);
            if (refused.Count == 0)
                return new MissionResumption(false,
                    $"no task in this mission was refused for '{action}', so the approval settles the "
                  + "question and there is nothing to replay.", missionId!, 0, null);

            var replayable = mission.Tasks.Where(t => refused.Contains(t.Id) && IsReplayable(t)).ToList();
            if (replayable.Count == 0)
                return new MissionResumption(false,
                    $"the task(s) refused for '{action}' are not in a replayable state — a completed "
                  + "task's effects have already landed and re-running it would duplicate them.",
                    missionId!, 0, null);

            foreach (var task in replayable) Reset(task);

            Memory.LogEvent(missionId!, "mission_resumed",
                $"Replaying {replayable.Count} task(s) refused for '{action}' under a recorded approval.",
                metadata: new()
                {
                    ["action"] = action,
                    ["task_ids"] = replayable.Select(t => t.Id).ToList(),
                    ["previous_status"] = mission.Status.Value(),
                });

            // From here the shape is RunMission's tail, deliberately: the same context builder, the
            // same executor, the same finalization, the same persistence ordering. A resumption that
            // ran its own private version of any of those would be a second answer to "how does a
            // mission finish", and the two would eventually disagree about a graded run.
            var profile = Profile;
            var resumedAt = AnthillTime.NowUtc();

            mission.Status = MissionStatus.Running;
            Memory.SaveMission(mission);

            var contract = Missions.MissionContracts.LoadOrCreate(Memory, mission);
            var context = MissionContext.Create(mission, profile, resumedAt, contract);

            using var missionCts = new CancellationTokenSource();
            missionCts.CancelAfter(context.Remaining(AnthillTime.NowUtc()));
            using var modelScope = ModelCallScope.Enter(missionCts.Token);

            var stopReason = Execution.Execute(mission, context, missionCts.Token);

            var evaluation = FinalizeMission(mission, context, stopReason);
            Anthill.Core.Workspaces.MissionRevisionRegistry.ReleaseMission(mission.Id);
            // SaveMission BEFORE SaveMissionEvaluation, for the reason RunMission states at length:
            // SaveMission is an INSERT OR REPLACE and would erase the evaluation columns.
            Memory.SaveMission(mission);
            Memory.SaveMissionEvaluation(evaluation);
            RecordMissionReport(mission.Id);

            return new MissionResumption(true,
                $"replayed {replayable.Count} task(s) refused for '{action}'.",
                missionId!, replayable.Count, evaluation.OutcomeCode);
        }
        catch (Exception error)
        {
            try
            {
                Memory.LogEvent(missionId!, "mission_resume_failed",
                    $"Could not replay the step refused for '{action}': {error.Message}",
                    metadata: new() { ["action"] = action, ["error"] = error.Message });
            }
            catch { /* the resumption already failed; losing its trace must not raise a second fault */ }

            return new MissionResumption(false,
                $"the approval is recorded and stands, but the replay could not run: {error.Message}",
                missionId ?? "", 0, null);
        }
    }

    /// <summary>
    /// The tasks this mission refused for one action, from its own refusal events.
    ///
    /// `escalation_refused` is written by the dispatch chokepoint with the task id and the tool
    /// name, which is exactly the pair this needs — the alternative, scanning failure reasons for
    /// the word, would be a text search standing in for a record that already exists.
    /// </summary>
    private HashSet<string> RefusedTaskIds(string missionId, string action)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Memory.GetRecentEvents(500, "escalation_refused", missionId))
        {
            var metadata = Json.TryParseObject(row.GetValueOrDefault("metadata_json")?.ToString());
            var tool = metadata.GetValueOrDefault("tool_name")?.ToString() ?? "";
            if (!string.Equals(tool, action, StringComparison.OrdinalIgnoreCase)) continue;

            var taskId = row.GetValueOrDefault("task_id")?.ToString();
            if (!string.IsNullOrWhiteSpace(taskId)) ids.Add(taskId!);
        }
        return ids;
    }

    /// <summary>
    /// A task may be replayed when it did NOT complete. Complete is the one status this refuses, and
    /// the narrowness is the point: a refused tool call can leave a task Failed or Skipped, and both
    /// of those are honest inputs to a replay, while a task that completed produced whatever it
    /// produced and running it again would produce it twice.
    /// </summary>
    private static bool IsReplayable(Task task) => task.Status is not TaskStatus.Complete;

    /// <summary>
    /// Put one task back where the scheduler can reach it.
    ///
    /// EXPLICIT FIELD BY FIELD rather than a fresh task with the same id. A replayed task must carry
    /// its own history — the attempt count keeps counting, and the previous failure stays legible in
    /// the attempt ledger — while carrying none of the terminal MARKERS that make the scheduler
    /// refuse it. Building a new object would silently reset every field this list does not mention,
    /// which is the direction that loses information rather than the direction that keeps it.
    /// </summary>
    private static void Reset(Task task)
    {
        task.Status = TaskStatus.Pending;
        task.FinishedAt = null;
        task.CompletedAt = null;
        task.FailedAt = null;
        task.SkippedAt = null;
        task.ElapsedSeconds = null;
        task.FailureReason = null;
        task.FailureType = null;
        task.SkippedReason = null;
        task.BlockedReason = null;
        task.CancellationReason = null;
        // The deterministic block is cleared too, and only here. It is set by the reroute check for
        // a capability nothing can serve, and by the patch-scope and policy gates — none of which an
        // approval answers. But a stale one on a task the scheduler is about to dispatch would
        // refuse it before the replay began, reporting a block that the operator has just resolved.
        // `MaxAttempts` is raised to admit the replay: the bound exists to stop a task retrying
        // itself, and this retry was authorized by a person.
        task.DeterministicBlock = null;
        task.MaxAttempts = Math.Max(task.MaxAttempts, task.AttemptCount + 1);
    }
}
