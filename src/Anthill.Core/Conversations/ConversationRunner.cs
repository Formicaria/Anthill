using Anthill.Core.Memory;
using ThreadingTask = System.Threading.Tasks.Task;

namespace Anthill.Core.Conversations;

/// <summary>
/// What a turn was ASKED for. v0.3.8.58: both answers do the same thing.
///
/// The enum is kept because the API still sends a mode and the console still has a mission button,
/// and a caller that never updates must not break. What is gone is the second behaviour: `Chat` no
/// longer selects a lane in which a model answers directly and can write to the operator's files.
/// It is retained as the API's default and routed to the mission pipeline like everything else.
///
/// Deleting the member outright was the alternative and it is worse: an old client sending
/// `"mode": "chat"` would then fail to deserialize rather than get the behaviour it should have had
/// all along.
/// </summary>
public enum ConversationMode
{
    /// <summary>What an unspecified request means. Reaches the mission pipeline.</summary>
    Chat = 0,

    /// <summary>
    /// The full mission pipeline: a plan, multiple tasks, specialists, verification — gated by
    /// <see cref="ConversationRunner.StartMissionAction"/>. Every turn reaches this now.
    /// </summary>
    Mission,
}

/// <summary>What one turn did, and where it went.</summary>
public sealed record ConversationOutcome(
    ConversationMode Mode,
    bool Started,
    string? MissionId,
    string Summary,
    EscalationDecision? Decision = null);

/// <summary>
/// v3.7.0 — the escalation boundary: what turns a conversation into a mission.
///
/// The phase asks for "one conversational surface that starts as chat and ESCALATES into autonomous
/// execution, with the escalation itself explicit, bounded and approved". The word doing the work is
/// EXPLICIT. Three designs were available and only one of them is defensible:
///
///   - the model decides when to escalate. Rejected: the agent's judgement about what deserves
///     autonomous multi-task execution would become a security-relevant decision, and a model that
///     wants to be helpful escalates.
///   - escalate automatically on complexity. Rejected for the same reason with extra steps — the
///     heuristic becomes the security boundary, and nobody can say what it will do next week.
///   - the CALLER asks for a mission, and that request is gated. Chosen.
///
/// So starting a mission is itself a side-effecting action, named
/// <see cref="StartMissionAction"/>, and it goes through the SAME <see cref="EscalationGate"/> as
/// apply_patch. That is the entire point of reusing the gate rather than inventing a second one: an
/// operator who set a standing policy has already answered this question, and one who did not gets
/// asked exactly once, in the place they already expect to be asked.
/// </summary>
public sealed class ConversationRunner
{
    /// <summary>
    /// The action name for "turn this conversation into a mission".
    ///
    /// Registered in <see cref="EscalationGate.SideEffecting"/> rather than special-cased here,
    /// because a boundary enforced in two places is a boundary that eventually disagrees with
    /// itself. It is not a tool and never will be — no model may call it.
    /// </summary>
    public const string StartMissionAction = "start_mission";

    /// <summary>How long to wait for the mission ROW to exist before giving up on linking it.</summary>
    public const int MissionIdTimeoutSeconds = 15;

    /// <summary>Longest thing that can plausibly be a mission id. A GUID is 36 characters.</summary>
    public const int MaxMissionIdLength = 64;

    /// <summary>
    /// Is this actually an id, or is it a report that arrived where an id was expected?
    ///
    /// Found in the running system rather than in a test. Before missions moved to the background,
    /// the runner linked the pipeline's RETURN value — which is the mission REPORT, not its id — so
    /// a conversation's MissionIds held a multi-kilobyte narrative. It rendered as a wall of text in
    /// the console and, worse, made the conversation-to-mission join quietly useless: nothing could
    /// ever look a mission up by that "id".
    ///
    /// The callback contract is correct now. This guard is what makes a future violation of it LOUD
    /// instead of silently corrupting history — the same principle already applied just below, where
    /// an id we do not have is refused rather than invented. One that is not an id is no better.
    /// </summary>
    public static bool LooksLikeMissionId(string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate)
        && candidate!.Length <= MaxMissionIdLength
        && !candidate.Any(char.IsWhiteSpace);

    private readonly SqliteMemory _memory;
    private readonly Func<string, Action<string>, CancellationToken, string> _startMission;

    /// <summary>
    /// Live work, by conversation. The exit gate says "cancelling a conversation cancels the work it
    /// started" — and marking a row cancelled does not stop a mission that is already running. This
    /// is the half that actually stops it.
    ///
    /// Keyed by conversation rather than by mission because that is what the operator cancels; a
    /// conversation that escalated three times has three things to stop, and the operator should not
    /// have to know that.
    /// </summary>
    private readonly Dictionary<string, List<CancellationTokenSource>> _running = new();

    /// <summary>
    /// <paramref name="startMission"/> is the mission pipeline, injected. It reports the new mission
    /// id through its callback AS SOON AS THE ROW EXISTS, then keeps running — the runner needs the
    /// id to record history, and must not wait for the work to finish to get it.
    /// </summary>
    public ConversationRunner(SqliteMemory memory,
        Func<string, Action<string>, CancellationToken, string> startMission)
    {
        _memory = memory;
        _startMission = startMission;
    }

    /// <summary>
    /// Record a turn and, if it asks for one, escalate into a mission.
    ///
    /// <paramref name="answers"/> carries the operator's replies for this turn — the same shape the
    /// tool gate uses, so an operator answering "approve" for <see cref="StartMissionAction"/> is
    /// doing exactly what they do for any other side effect.
    /// </summary>
    public ConversationOutcome Run(
        Conversation conversation,
        string message,
        ConversationMode requested = ConversationMode.Chat,
        IReadOnlyDictionary<string, string>? answers = null,
        CancellationToken cancel = default,
        IReadOnlyList<(string Filename, string Content)>? attachments = null)
    {
        if (conversation is null)
            return new ConversationOutcome(requested, false, null, "no conversation");

        // A cancelled conversation starts nothing. The exit gate says cancelling a conversation
        // cancels the work it started; refusing to start MORE work is the other half of that, and
        // the cheaper half — stopping something is harder than not beginning it.
        if (conversation.Cancelled)
            return new ConversationOutcome(requested, false, null,
                "this conversation is cancelled and cannot start new work");

        var ordinal = _memory.LoadConversationTurns(conversation.Id).Count + 1;

        // v0.3.8.58 — THERE IS NO SECOND LANE. Every message the operator sends is a mission.
        //
        // `requested` still arrives from the API because the console has a mission button, but it no
        // longer selects between two behaviours: Chat and Mission both mean "give this to the
        // colony". The chat lane is not narrowed here, it is DELETED, and the deletion is the point.
        //
        // WHAT THE LANE ACTUALLY WAS. Not a chat model answering questions. It entered
        // AgentAccessScope with confinedWorkspace:false — standing in the operator's LIVE project
        // tree — and handed the conversation's approval policy to the provider, which is what
        // materialised the agent CLI's own edit flags and its settings file. Then BeginDirectEditSweep
        // and CommitDirectEdits existed to notice which files that turn had written and commit them.
        // A hundred lines of machinery for capturing work done outside the colony is not something
        // anyone builds for a lane that answers questions; it is the receipt for a lane that worked.
        //
        // v0.3.8.57 blocked a coding agent from serving this route and rewrote the prompt to say it
        // had no tools. Both were true and neither was the grant: the authorisation lived in the
        // access scope, not the sentence. That is this repository's own named defect — prose as a
        // control channel — committed by the change that was supposed to close it.
        //
        // WHAT REPLACES IT. The planner decides the shape. A trivial message yields a trivial plan;
        // a real request yields the full one. The colony deciding that a message is small is a
        // different thing from a lane in front of the colony deciding it never had to ask, and only
        // the first one keeps the roles load-bearing. There is no marker, no escalation and no
        // proposal, because there is nothing left to escalate FROM.

        // The shared budget, checked BEFORE the gate. A conversation that has spent its mission
        // allowance is not asking for permission — it is out of budget, and asking the operator to
        // approve something that will be refused anyway trains them to approve without reading.
        if (!conversation.Budget.AllowsAnotherMission(conversation.MissionIds.Count))
        {
            RecordTurn(conversation, ordinal, message, null, attachments, reuseIdenticalPending: true);
            return new ConversationOutcome(ConversationMode.Mission, false, null,
                $"conversation budget exhausted: {conversation.MissionIds.Count} of "
              + $"{conversation.Budget.MaxMissions} missions already started");
        }

        var decision = EscalationGate.Evaluate(conversation, StartMissionAction,
            answers?.GetValueOrDefault(StartMissionAction));
        try { _memory.SaveEscalationDecision(decision); } catch { }

        // v0.3.8.46, found live: every OTHER answer the operator gave is recorded NOW, not only
        // if some tool happens to consult it. The old shape left a trap — an operator approved a
        // refused action, the re-run mission planned differently and never asked again, the
        // approval evaporated unrecorded, and the stale refusal kept the conversation in
        // "waiting on you" forever. An answer given IS an operator decision; the record must say
        // so whether or not the work ends up needing it.
        foreach (var (action, answer) in answers ?? new Dictionary<string, string>())
        {
            if (action == StartMissionAction || string.IsNullOrWhiteSpace(action)) continue;
            try { _memory.SaveEscalationDecision(EscalationGate.Evaluate(conversation, action, answer)); }
            catch { }
        }

        if (!decision.Allowed)
        {
            // The turn is recorded even though nothing ran. An attempt to escalate that was refused
            // is part of the conversation's history — arguably the most interesting part, since it
            // is the moment the colony wanted more authority than it had.
            RecordTurn(conversation, ordinal, message, null, attachments, reuseIdenticalPending: true);
            return new ConversationOutcome(ConversationMode.Mission, false, null,
                $"escalation refused: {decision.Reason}", decision);
        }

        // Linked, not replaced: the caller's own cancellation still applies, and the conversation
        // gains a second way to stop the same work. Whichever fires first wins.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        lock (_running)
        {
            if (!_running.TryGetValue(conversation.Id, out var live))
                _running[conversation.Id] = live = new List<CancellationTokenSource>();
            live.Add(cts);
        }

        // The mission runs in the BACKGROUND and this returns as soon as the mission row exists.
        //
        // Found by running it: the first version called the pipeline synchronously and recorded the
        // turn afterwards, which meant an HTTP request blocked for the whole mission AND — much
        // worse — a mission that was slow, cancelled or crashed never got its turn or its link
        // recorded at all. The "conversation and mission are one history" gate failed in exactly
        // the cases where the history matters most.
        //
        // The id arrives through onMissionCreated, which the Queen already fires the moment the row
        // is persisted. Waiting for THAT is bounded and quick; waiting for the work is neither.
        var idReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // v0.3.8.51, found live in mission 46f1acb7: the goal was the operator's bare words —
        // "Make all of these changes" — and the coder honestly refused, because "these changes"
        // referred to a plan that lived in the CONVERSATION and the mission never saw it. The
        // goal now carries the operator's message plus the bounded recent transcript, so what
        // "this" and "these" point at travels with the work.
        var missionGoal = ComposeMissionGoal(conversation, message, attachments);

        _ = ThreadingTask.Run(() =>
        {
            try
            {
                _startMission(missionGoal, id => idReady.TrySetResult(id), cts.Token);
                // v0.3.8.48, found live: the mission finished, its answer sat in mission history,
                // and the conversation that started it showed nothing — an operator watching the
                // chat never saw the result of the work they approved. The pipeline call above is
                // synchronous, so this line runs when the mission has settled: put its answer in
                // the conversation, where the question was asked.
                RecordMissionAnswer(conversation.Id, idReady);
            }
            catch (Exception error) { idReady.TrySetException(error); }
            finally
            {
                // The lease on this conversation's cancellation source ends when the work does.
                lock (_running)
                {
                    if (_running.TryGetValue(conversation.Id, out var live)) live.Remove(cts);
                }
                try { cts.Dispose(); } catch { }
            }
        });

        string missionId;
        try
        {
            missionId = idReady.Task.Wait(TimeSpan.FromSeconds(MissionIdTimeoutSeconds))
                ? idReady.Task.Result
                : "";
        }
        catch (AggregateException error)
        {
            // The pipeline threw before creating a row. Recorded as a turn that started nothing,
            // because it did — and a silent drop here would lose the attempt entirely.
            RecordTurn(conversation, ordinal, message, null, attachments, reuseIdenticalPending: true);
            return new ConversationOutcome(ConversationMode.Mission, false, null,
                $"mission failed to start: {error.InnerException?.Message ?? error.Message}", decision);
        }

        if (missionId.Length == 0)
        {
            // The row did not appear in time. The work may still be starting, so this is reported
            // rather than treated as a failure — but it is NOT linked, because linking an id we do
            // not have would be a fabricated history.
            RecordTurn(conversation, ordinal, message, null, attachments, reuseIdenticalPending: true);
            return new ConversationOutcome(ConversationMode.Mission, true, null,
                "mission started, but its id did not arrive in time to link — check the mission list",
                decision);
        }

        if (!LooksLikeMissionId(missionId))
        {
            // Something that is not an id arrived where an id was expected. Recorded and reported
            // rather than stored: a bad link is worse than a missing one, because a missing link
            // shows up as a gap an operator can investigate and a bad one silently answers every
            // future join with nothing while looking perfectly healthy.
            RecordTurn(conversation, ordinal, message, null, attachments, reuseIdenticalPending: true);
            return new ConversationOutcome(ConversationMode.Mission, true, null,
                "mission started, but the pipeline reported something that is not a mission id — "
              + "not linking it; check the mission list", decision);
        }

        RecordTurn(conversation, ordinal, message, missionId, attachments, reuseIdenticalPending: true);
        _memory.SaveConversation(conversation with
        {
            // The link that makes the conversation and the mission ONE history, which is the exit
            // gate. Recorded on both sides: the turn says which mission it started, and the
            // conversation lists what it has started, so the join works from either direction.
            MissionIds = conversation.MissionIds.Append(missionId).Distinct().ToList(),
            UpdatedAt = AnthillTime.NowUtc(),
        });

        return new ConversationOutcome(ConversationMode.Mission, true, missionId,
            $"escalated into mission {missionId}", decision);
    }

    /// <summary>
    /// Cancel a conversation AND the work it started.
    ///
    /// Both halves, in that order. Marking the row first means that even if cancelling in-flight work
    /// fails — a mission that ignores its token, a token source already disposed — no NEW work can
    /// start, which is the guarantee that does not depend on anyone else's cooperation.
    ///
    /// Returns how many live pieces of work were signalled, so an operator can tell "stopped two
    /// missions" from "there was nothing running". Silence on that distinction is what makes people
    /// press cancel twice.
    /// </summary>
    public int Cancel(string conversationId)
    {
        var conversation = _memory.LoadConversation(conversationId);
        if (conversation is not null && !conversation.Cancelled)
            _memory.SaveConversation(conversation with
            {
                Cancelled = true,
                UpdatedAt = AnthillTime.NowUtc(),
            });

        List<CancellationTokenSource> live;
        lock (_running)
        {
            if (!_running.TryGetValue(conversationId ?? "", out var found)) return 0;
            live = found;
            _running.Remove(conversationId ?? "");
        }

        var signalled = 0;
        foreach (var cts in live)
        {
            // Best-effort per source: one already-disposed token must not prevent cancelling the
            // rest. A cancel that stops two of three things and throws is worse than one that stops
            // what it can and says so.
            try { if (!cts.IsCancellationRequested) { cts.Cancel(); signalled++; } }
            catch (ObjectDisposedException) { }
            finally { try { cts.Dispose(); } catch { } }
        }

        return signalled;
    }

    /// <summary>
    /// The project's open directory gates, as paths, plus the colony's own source tree.
    ///
    /// v0.3.8.58: this was the CHAT lane's grant set and chat no longer exists. It is kept and made
    /// shared rather than deleted, because the execution path had its own thinner copy — grants
    /// only, no colony-source reach — and deleting this one would have quietly narrowed what a
    /// mission may touch to less than the operator granted. Two resolutions of one question is how
    /// they disagree; there is now one.
    /// </summary>
    internal static IReadOnlyList<string> ProjectGrantPaths(SqliteMemory _memory, Conversation conversation)
    {
        var paths = new List<string>();
        try
        {
            if (!string.IsNullOrWhiteSpace(conversation.ProjectId))
                paths.AddRange(_memory.LoadProjectGrants(conversation.ProjectId!).Select(g => g.Path));
        }
        catch { /* no grants is a normal project, not an error */ }

        // v0.3.8.52 (third field round, operator's rule): "ANTHILL's working directory lives
        // alongside the project directory no matter what" — the colony's own source tree rides
        // as reach (--add-dir) on every conversation, so self-improvement stays possible
        // before, during and after any project's work. Reach only: the project's git badge and
        // the direct-edit sweep track the PROJECT's tree, never this one.
        try
        {
            if (Projects.ProjectRoots.ColonySource() is { } self
                && !paths.Contains(self, StringComparer.Ordinal))
                paths.Add(self);
        }
        catch { /* a colony without a source checkout simply grants nothing extra */ }
        return paths;
    }

    /// <summary>
    /// The mission goal a CONVERSATION escalates with: the operator's message, plus the bounded
    /// recent transcript so pronouns resolve. v0.3.8.51, from mission 46f1acb7 — the operator said
    /// "Make all of these changes", the colony's own reply held the list of changes, and the
    /// mission got five words and no list. The coder's refusal was correct; the goal was wrong.
    /// A conversation with no prior turns escalates with the plain message, unchanged.
    /// </summary>
    internal string ComposeMissionGoal(Conversation conversation, string message,
        IReadOnlyList<(string Filename, string Content)>? attachments = null)
    {
        const int MaxContextChars = 4000;
        const int MaxAttachmentChars = 8000;
        List<ConversationTurn> turns;
        try { turns = _memory.LoadConversationTurns(conversation.Id).ToList(); }
        catch { turns = new List<ConversationTurn>(); }

        var prior = turns
            .Where(t => !string.Equals(t.Content, message, StringComparison.Ordinal))
            .TakeLast(6).ToList();

        var sb = new System.Text.StringBuilder(message);

        // v0.3.8.58 — the PROJECT's standing context, rehomed from the deleted chat prompt.
        //
        // Its purpose is the point of writing one: it framed every chat turn, and a mission planned
        // without it would plan for a repository whose reason for existing nobody mentioned. Labelled
        // as the operator's own framing rather than the colony's conclusion, because a description is
        // an instruction and the plan should not later cite it as a finding.
        try
        {
            if (!string.IsNullOrWhiteSpace(conversation.ProjectId)
                && _memory.LoadProject(conversation.ProjectId!) is { } project)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine($"--- project \"{project.Name}\" ---");
                if (!string.IsNullOrWhiteSpace(project.DescriptionMd))
                {
                    sb.AppendLine("The operator describes its purpose as:");
                    sb.AppendLine(project.DescriptionMd.Trim());
                }
                // v0.3.8.55's rule survives the move: a pathless project stands in ANTHILL's own
                // checkout, and that is SAID rather than presented as a directory the operator chose.
                if (ProjectDirectory(_memory, conversation) is { } dir)
                    sb.AppendLine(string.IsNullOrWhiteSpace(project.Path)
                        ? $"No working directory is set, so the work stands in ANTHILL's own source checkout: {dir}"
                        : $"The project's working directory is: {dir}");
            }
        }
        catch { /* a project that will not load is missing context, never a failed mission */ }

        if (prior.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("--- conversation context (what the request above refers to) ---");
            var budget = MaxContextChars;
            foreach (var t in prior)
            {
                var who = string.Equals(t.Role, "user", StringComparison.OrdinalIgnoreCase) ? "Operator" : "Colony";
                var text = t.Content ?? "";
                if (text.Length > budget) text = text[..budget];
                sb.AppendLine($"{who}: {text}");
                budget -= text.Length;
                if (budget <= 0) break;
            }
        }

        // v0.3.8.58 — the operator's ATTACHMENTS travel with the goal.
        //
        // They used to reach the model through ChatPrompt, which no longer exists. Recording them
        // against the turn and stopping there would leave the console showing a file the mission
        // could not read — a turn that says "here is the spec" and work that never saw it. That is
        // this repository's "declared and reaching nobody", introduced by deleting their only
        // reader, so they get a reader here. Bounded like the transcript, for the same reason.
        var attached = attachments ?? Array.Empty<(string Filename, string Content)>();
        if (attached.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"--- attachments ({attached.Count}) ---");
            var budget = MaxAttachmentChars;
            foreach (var (filename, content) in attached)
            {
                var text = content ?? "";
                var truncated = text.Length > budget;
                if (truncated) text = budget <= 0 ? "" : text[..budget];
                sb.AppendLine($"# {filename}");
                sb.AppendLine(text);
                // Truncation is SAID. A silently clipped spec is worse than a missing one: the work
                // proceeds confidently against half a document and nothing anywhere records why.
                if (truncated) sb.AppendLine($"[truncated — {(content ?? "").Length} chars total]");
                budget -= text.Length;
                if (budget <= 0) break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// After an escalated mission SETTLES, its result becomes the conversation's next turn — the
    /// operator asked in chat, so chat is where the answer belongs. Runs on the background thread
    /// that just finished the pipeline. A cancelled conversation gets nothing (the operator moved
    /// on, same rule as chat replies), and a missing result is said plainly rather than invented.
    /// </summary>
    private void RecordMissionAnswer(string conversationId, TaskCompletionSource<string> idReady)
    {
        try
        {
            if (!idReady.Task.IsCompletedSuccessfully) return;
            var missionId = idReady.Task.Result;
            if (!LooksLikeMissionId(missionId)) return;

            var current = _memory.LoadConversation(conversationId);
            if (current is null || current.Cancelled) return;

            var mission = _memory.GetMission(missionId);
            if (mission is null) return;
            var status = mission.GetValueOrDefault("status")?.ToString() ?? "";
            var answer = mission.GetValueOrDefault("user_result")?.ToString();
            if (string.IsNullOrWhiteSpace(answer))
                answer = mission.GetValueOrDefault("final_result")?.ToString();

            // v0.3.8.51, found live: a FAILED mission's user_result was the medic's escalation
            // prose — "Semantic duplicate: failure signature fsig:…" landed in chat as the
            // colony's answer. An operator asked a question; machine bookkeeping is not the
            // reply. A non-complete mission now answers with what actually failed, in words,
            // built from the structured task rows.
            if (!string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase))
                answer = ComposeFailureAnswer(missionId, status) ?? answer;

            var content = !string.IsNullOrWhiteSpace(answer)
                ? answer!
                : $"The mission finished with status \"{status}\" and recorded no result text — "
                  + "its task trail is in the mission history.";

            var ordinal = _memory.LoadConversationTurns(conversationId).Count + 1;
            _memory.SaveConversationTurn(new ConversationTurn(
                Guid.NewGuid().ToString("N")[..12], conversationId, ordinal, "assistant", content)
            {
                MissionId = missionId,
            });
            _memory.SaveConversation(current with { UpdatedAt = AnthillTime.NowUtc() });
        }
        catch
        {
            // A failure to ANNOUNCE the answer must not fail the mission that produced it; the
            // result still exists in mission history either way.
        }
    }

    /// <summary>The operator-readable account of a mission that did not complete: which task
    /// failed and its own stated reason — structured rows, no medic bookkeeping.</summary>
    private string? ComposeFailureAnswer(string missionId, string status)
    {
        try
        {
            var failed = _memory.GetTasksForMission(missionId)
                .FirstOrDefault(t => string.Equals(t.GetValueOrDefault("status")?.ToString(), "failed",
                    StringComparison.OrdinalIgnoreCase));
            var title = failed?.GetValueOrDefault("title")?.ToString();
            var reason = failed?.GetValueOrDefault("result")?.ToString();
            if (string.IsNullOrWhiteSpace(reason)) reason = failed?.GetValueOrDefault("result_summary")?.ToString();

            return failed is null
                ? $"The mission ended \"{status}\" without completing. The task trail is in the mission history."
                : $"The mission could not finish. \"{title}\" failed: "
                  + (string.IsNullOrWhiteSpace(reason) ? "no reason was recorded." : reason!.Trim())
                  + "\n\nThe full task trail is in the mission history.";
        }
        catch { return null; }
    }

    private string RecordTurn(Conversation conversation, int ordinal, string message, string? missionId,
        IReadOnlyList<(string Filename, string Content)>? attachments = null,
        bool reuseIdenticalPending = false)
    {
        // v0.3.8.48, found live: approving a refused start_mission re-sends the SAME message
        // (convApprove restates it to meet the gate), and recording that re-send as a new turn
        // fabricated a duplicate — the operator spoke once, the transcript said twice. If the last
        // turn is the operator's identical attempt that started nothing, this IS that attempt:
        // link the mission to it instead of inventing a second one. A deliberate repeat that
        // already started work keeps its mission link, so it is never collapsed.
        if (reuseIdenticalPending)
        {
            // The last USER turn, not the last turn: v0.3.8.51's escalation proposal records the
            // model's reply between the operator's message and the mission re-run, and that
            // intervening assistant turn must not turn one operator message into two.
            var last = _memory.LoadConversationTurns(conversation.Id)
                .LastOrDefault(t => string.Equals(t.Role, "user", StringComparison.OrdinalIgnoreCase));
            if (last is not null
                && string.Equals(last.Content, message ?? "", StringComparison.Ordinal)
                && last.MissionId is null)
            {
                _memory.SaveConversationTurn(last with { MissionId = missionId });
                return last.Id;
            }
        }

        var id = Guid.NewGuid().ToString("N")[..12];
        _memory.SaveConversationTurn(new ConversationTurn(
            id, conversation.Id, ordinal, "user", message ?? "")
        {
            MissionId = missionId,
        });
        // v0.3.8.47: attachments belong to the turn that brought them — recorded with it, shown
        // with it, and carried into the mission goal by ComposeMissionGoal.
        foreach (var (filename, content) in attachments ?? Array.Empty<(string, string)>())
            _memory.SaveAttachment(new ConversationAttachment(
                Guid.NewGuid().ToString("N")[..12], conversation.Id, id,
                filename, System.Text.Encoding.UTF8.GetByteCount(content ?? ""), content ?? ""));
        return id;
    }

    /// <summary>
    /// v0.3.8.52 — this conversation's working tree. v0.3.8.58 — and therefore its MISSION's.
    ///
    /// The field report this fixed was "every project's chat ran in the same tree". Only the chat
    /// lane ever consulted it, so removing that lane without moving this would have reproduced the
    /// same bug one door down, as "every project's mission runs in the same tree" — a regression
    /// introduced by a deletion, which is the kind that gets attributed to anything but the change
    /// that caused it.
    /// </summary>
    internal static string? ProjectDirectory(SqliteMemory _memory, Conversation conversation)
    {
        try
        {
            // v0.3.8.55 (fourth field round, REVERSING the third): a missing working directory
            // no longer blocks the chat. The default is ANTHILL's own source checkout — direct
            // source access, the colony standing in its own tree — and it stays PRIMARY until
            // the operator sets a directory, whose choice then takes over completely. The
            // source tree never leaves the conversation either way: it rides as reach on every
            // grant set (ProjectGrantPaths), so self-improvement stays possible even after the
            // operator points the project somewhere else.
            if (!string.IsNullOrWhiteSpace(conversation.ProjectId)
                && _memory.LoadProject(conversation.ProjectId!) is { } project
                && !string.IsNullOrWhiteSpace(project.Path))
                return project.Path;
            return Projects.ProjectRoots.ColonySource();
        }
        catch { return null; }
    }

}
