using Anthill.Core.Memory;
using ThreadingTask = System.Threading.Tasks.Task;

namespace Anthill.Core.Conversations;

/// <summary>How a turn is executed. The same conversation, two execution modes.</summary>
public enum ConversationMode
{
    /// <summary>
    /// Bounded work in the tool-calling loop: ask, run tools, feed results back, stop. Turn, tool
    /// call, wall-clock and repeat budgets all apply. The default, because most turns are questions.
    /// </summary>
    Chat = 0,

    /// <summary>
    /// The full mission pipeline: a plan, multiple tasks, specialists, verification. Reached only by
    /// ESCALATION — see <see cref="ConversationRunner.StartMissionAction"/>.
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
/// v0.3.8.42 — one conversational reply: the text, and which provider/model actually produced it.
/// The attribution is not decoration: capability-aware routing can substitute providers, and a
/// transcript that cannot say who answered cannot be audited.
/// </summary>
public sealed record ConversationReply(bool Ok, string Content, string Provider, string Model, string? Error,
    int? PromptTokens = null, int? CompletionTokens = null);

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

    /// <summary>
    /// v0.3.8.51 — the chat model's escalation PROPOSAL: a reply ending with this exact marker
    /// asks the colony to run the operator's request as a mission. Stripped from the record and
    /// converted into the same deterministic start_mission gate the old button used — the model
    /// can request, only the gate (and under Ask, only the operator) can allow. Double-bracketed
    /// so ordinary prose can never trip it.
    /// </summary>
    public const string EscalateMarker = "[[START_MISSION]]";

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
    /// <paramref name="startMission"/> is the mission pipeline, injected. The runner decides WHETHER
    /// a mission starts; the Queen decides what a mission does. Keeping those apart is what lets the
    /// escalation boundary be tested without standing up a colony.
    /// </summary>
    /// <summary>
    /// <paramref name="startMission"/> is the mission pipeline, injected. It reports the new mission
    /// id through its callback AS SOON AS THE ROW EXISTS, then keeps running — the runner needs the
    /// id to record history, and must not wait for the work to finish to get it.
    /// </summary>
    /// <summary>How many recent turns travel to the provider as context. Bounded, like everything.</summary>
    public const int ChatContextTurns = 12;

    /// <summary>
    /// v0.3.8.42 — the reasoning call behind chat turns, injected like the mission pipeline is.
    ///
    /// Until this existed, Chat mode recorded the operator's message and answered NOTHING: the
    /// "bounded conversational work" summary described a loop that had never been built, the
    /// console rendered the permanent "conversational work" state as an eternal spinner, and the
    /// natural misreading was "the model endpoint is down" when the truth was "no model is ever
    /// asked". The delegate resolves through the SAME router the roles use — the `conversation`
    /// route key, so Ollama, a keyed API or an installed agent CLI are equally valid answers and
    /// the operator chooses under Administration → Providers &amp; Model Routing. Null means the
    /// runtime was composed without reasoning, and the turn says so instead of pretending.
    /// </summary>
    /// <summary>v0.3.8.44: the second argument is the delta channel — null when the caller wants
    /// one reply, a sink when it wants the reply as it is produced. The delegate decides whether
    /// its provider can actually stream; the runner never fakes it.</summary>
    private readonly Func<string, Action<string>?, ConversationReply>? _ask;

    public ConversationRunner(SqliteMemory memory,
        Func<string, Action<string>, CancellationToken, string> startMission,
        Func<string, Action<string>?, ConversationReply>? ask = null)
    {
        _memory = memory;
        _startMission = startMission;
        _ask = ask;
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
        Action<string>? onDelta = null,
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

        if (requested == ConversationMode.Chat)
        {
            // Chat is not gated HERE. The tools it may call are gated at dispatch, by the same gate,
            // which is the correct place: a conversation that only reads needs no permission, and
            // one that tries to write is stopped at the write rather than at the sentence before it.
            RecordTurn(conversation, ordinal, message, null, attachments);

            // v0.3.8.42: the turn is ANSWERED. Before this the message was recorded and nothing
            // was ever asked — see the _ask field for what that cost.
            if (_ask is null)
                return new ConversationOutcome(ConversationMode.Chat, false, null,
                    "no reasoning provider is composed — the message is recorded, and nothing can answer it");

            ConversationReply reply;
            try
            {
                // v0.3.8.51, second field round: the CHAT LANE is a working agent too — Claude
                // Code with a real directory — and it ran with nothing while only missions got
                // the operator's answer. The same policy + grants now ride this call, marked
                // UNCONFINED because this lane stands in live files: Manual approval grants no
                // writes here (the agent proposes a mission instead, where the sandbox is);
                // Automatically approve and Skip-all act as the operator chose.
                using var access = Anthill.SDK.Reasoning.AgentAccessScope.Enter(
                    conversation.EffectivePolicy.ToString().ToLowerInvariant(),
                    ProjectGrantPaths(conversation),
                    confinedWorkspace: false);
                reply = _ask(ChatPrompt(conversation), onDelta);
            }
            catch (Exception error) { reply = new ConversationReply(false, "", "", "", error.Message); }

            if (!reply.Ok)
                return new ConversationOutcome(ConversationMode.Chat, false, null,
                    $"no answer: {reply.Error ?? "the provider returned nothing"} — route "
                  + "'conversation' to a working provider under Administration → Providers & Model Routing");

            // Cancelled while the provider was thinking: the operator has already moved on, and a
            // reply landing in a cancelled conversation would look like it ignored the Stop.
            var current = _memory.LoadConversation(conversation.Id);
            if (current?.Cancelled == true)
                return new ConversationOutcome(ConversationMode.Chat, false, null,
                    "cancelled while answering — the reply was discarded");

            // v0.3.8.51 (field report): THE COLONY PROPOSES THE MISSION ITSELF. The transcript's
            // worst sentence was the colony telling its operator to "ask for it as a mission
            // explicitly" — a magic word. The chat prompt now invites the model to end its reply
            // with the escalation marker when the request is real work; the marker is stripped
            // from the record and the SAME deterministic gate the mission button used takes over:
            // Manual approval shows the in-chat card, Automatically approve and Skip-all just run.
            // The marker is a PROPOSAL, exactly as trusted as a handoff — the model can request,
            // only the gate can allow.
            var wantsMission = reply.Content.Contains(EscalateMarker, StringComparison.Ordinal);
            var content = wantsMission
                ? reply.Content.Replace(EscalateMarker, "", StringComparison.Ordinal).TrimEnd()
                : reply.Content;

            _memory.SaveConversationTurn(new ConversationTurn(
                Guid.NewGuid().ToString("N")[..12], conversation.Id, ordinal + 1, "assistant", content)
            {
                Provider = reply.Provider,
                Model = reply.Model,
                // v0.3.8.46: what the answer cost, when the provider says. Null is "not reported".
                PromptTokens = reply.PromptTokens,
                CompletionTokens = reply.CompletionTokens,
            });

            if (wantsMission)
                // Re-enter as a MISSION with the operator's own message as the goal. The recursion
                // is bounded — the mission path never re-enters chat — and the gate downstream is
                // the one authority: Ask records the refusal and waits visibly; Auto/Bypass run.
                // RecordTurn's identical-pending reuse links the mission to the operator's turn
                // instead of duplicating it.
                return Run(conversation, message, ConversationMode.Mission, answers,
                    cancel: cancel, onDelta: null, attachments: null);

            return new ConversationOutcome(ConversationMode.Chat, true, null,
                $"answered by {reply.Provider}/{reply.Model}");
        }

        // The shared budget, checked BEFORE the gate. A conversation that has spent its mission
        // allowance is not asking for permission — it is out of budget, and asking the operator to
        // approve something that will be refused anyway trains them to approve without reading.
        if (!conversation.Budget.AllowsAnotherMission(conversation.MissionIds.Count))
        {
            RecordTurn(conversation, ordinal, message, null, reuseIdenticalPending: true);
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
            RecordTurn(conversation, ordinal, message, null, reuseIdenticalPending: true);
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
        var missionGoal = ComposeMissionGoal(conversation, message);

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
            RecordTurn(conversation, ordinal, message, null, reuseIdenticalPending: true);
            return new ConversationOutcome(ConversationMode.Mission, false, null,
                $"mission failed to start: {error.InnerException?.Message ?? error.Message}", decision);
        }

        if (missionId.Length == 0)
        {
            // The row did not appear in time. The work may still be starting, so this is reported
            // rather than treated as a failure — but it is NOT linked, because linking an id we do
            // not have would be a fabricated history.
            RecordTurn(conversation, ordinal, message, null, reuseIdenticalPending: true);
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
            RecordTurn(conversation, ordinal, message, null, reuseIdenticalPending: true);
            return new ConversationOutcome(ConversationMode.Mission, true, null,
                "mission started, but the pipeline reported something that is not a mission id — "
              + "not linking it; check the mission list", decision);
        }

        RecordTurn(conversation, ordinal, message, missionId, reuseIdenticalPending: true);
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

    /// <summary>The project's open directory gates, as paths — the chat lane's grant set.</summary>
    private IReadOnlyList<string> ProjectGrantPaths(Conversation conversation)
    {
        if (string.IsNullOrWhiteSpace(conversation.ProjectId)) return Array.Empty<string>();
        try { return _memory.LoadProjectGrants(conversation.ProjectId!).Select(g => g.Path).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// The mission goal a CONVERSATION escalates with: the operator's message, plus the bounded
    /// recent transcript so pronouns resolve. v0.3.8.51, from mission 46f1acb7 — the operator said
    /// "Make all of these changes", the colony's own reply held the list of changes, and the
    /// mission got five words and no list. The coder's refusal was correct; the goal was wrong.
    /// A conversation with no prior turns escalates with the plain message, unchanged.
    /// </summary>
    internal string ComposeMissionGoal(Conversation conversation, string message)
    {
        const int MaxContextChars = 4000;
        List<ConversationTurn> turns;
        try { turns = _memory.LoadConversationTurns(conversation.Id).ToList(); }
        catch { return message; }

        var prior = turns
            .Where(t => !string.Equals(t.Content, message, StringComparison.Ordinal))
            .TakeLast(6).ToList();
        if (prior.Count == 0) return message;

        var sb = new System.Text.StringBuilder(message);
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
        // with it, and fed to the model with it through ChatPrompt.
        foreach (var (filename, content) in attachments ?? Array.Empty<(string, string)>())
            _memory.SaveAttachment(new ConversationAttachment(
                Guid.NewGuid().ToString("N")[..12], conversation.Id, id,
                filename, System.Text.Encoding.UTF8.GetByteCount(content ?? ""), content ?? ""));
        return id;
    }

    /// <summary>
    /// The bounded prompt: a short instruction and the last <see cref="ChatContextTurns"/> turns,
    /// the just-recorded message included. Provider-agnostic text, because the delegate may be
    /// backed by anything from a local model to an installed agent CLI, and the least capable
    /// transport (a prompt on argv) sets the contract for all of them.
    /// </summary>
    private string ChatPrompt(Conversation conversation)
    {
        var turns = _memory.LoadConversationTurns(conversation.Id);
        var recent = turns.Skip(Math.Max(0, turns.Count - ChatContextTurns));
        var sb = new System.Text.StringBuilder();
        // v0.3.8.51, second field round: the prompt used to say "you have no tools", which is a
        // LIE when the routed provider is a working agent — it then hit its own permission walls
        // and reported them to a confused operator. The prompt now states the access the operator
        // actually chose, so the agent acts within it or proposes a mission, never mystery-fails.
        var access = conversation.EffectivePolicy switch
        {
            EscalationPolicy.Bypass =>
                "The operator has set Skip all approvals for this conversation: you may read, edit "
                + "and run your available tools directly for small, contained work.",
            EscalationPolicy.AutoApprove =>
                "The operator has set Automatically approve for this conversation: you may edit "
                + "files and run bounded build/test commands directly for small, contained work.",
            _ =>
                "This conversation is under Manual approval: your access is READ-ONLY here. Do not "
                + "attempt writes or commands — they will be refused.",
        };
        sb.AppendLine("You are the ANTHILL colony's conversational assistant. Answer the operator's "
            + "last message concisely and truthfully, and never claim work you did not do. "
            + access + " For REAL multi-step work — builds, file changes, larger research — say "
            + "briefly what the mission will do and end your reply with the exact marker "
            + EscalateMarker + " on its own line. The colony then runs it as a mission under the "
            + "operator's approval policy; under Manual approval the operator is asked first. "
            + "Never use the marker for a question you can simply answer.");
        sb.AppendLine();
        // v0.3.8.47: the project's purpose is standing context — the point of writing one. Same
        // shape as Claude's project instructions: it travels with every turn, clearly labelled as
        // the operator's own framing, not the colony's conclusion.
        if (!string.IsNullOrWhiteSpace(conversation.ProjectId)
            && _memory.LoadProject(conversation.ProjectId!) is { } project
            && (!string.IsNullOrWhiteSpace(project.DescriptionMd) || !string.IsNullOrWhiteSpace(project.Path)))
        {
            sb.AppendLine($"This conversation belongs to the project \"{project.Name}\". "
                + "The operator describes its purpose as:");
            if (!string.IsNullOrWhiteSpace(project.DescriptionMd)) sb.AppendLine(project.DescriptionMd.Trim());
            if (!string.IsNullOrWhiteSpace(project.Path))
                sb.AppendLine($"The project's working directory is: {project.Path}");
            sb.AppendLine();
        }
        foreach (var t in recent)
        {
            sb.AppendLine((string.Equals(t.Role, "user", StringComparison.OrdinalIgnoreCase) ? "Operator: " : "Colony: ") + t.Content);
            // v0.3.8.47: a turn's attachments travel with it, clearly framed as operator-provided
            // files — the model sees the text the operator handed over, nothing more.
            foreach (var a in _memory.LoadTurnAttachments(t.Id))
                sb.AppendLine($"[Operator attached \"{a.Filename}\"]\n{a.Content}\n[end of \"{a.Filename}\"]");
        }
        sb.AppendLine("Colony:");
        return sb.ToString();
    }
}
