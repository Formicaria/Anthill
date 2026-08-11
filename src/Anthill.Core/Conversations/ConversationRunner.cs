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
            try { reply = _ask(ChatPrompt(conversation), onDelta); }
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

            _memory.SaveConversationTurn(new ConversationTurn(
                Guid.NewGuid().ToString("N")[..12], conversation.Id, ordinal + 1, "assistant", reply.Content)
            {
                Provider = reply.Provider,
                Model = reply.Model,
                // v0.3.8.46: what the answer cost, when the provider says. Null is "not reported".
                PromptTokens = reply.PromptTokens,
                CompletionTokens = reply.CompletionTokens,
            });
            return new ConversationOutcome(ConversationMode.Chat, true, null,
                $"answered by {reply.Provider}/{reply.Model}");
        }

        // The shared budget, checked BEFORE the gate. A conversation that has spent its mission
        // allowance is not asking for permission — it is out of budget, and asking the operator to
        // approve something that will be refused anyway trains them to approve without reading.
        if (!conversation.Budget.AllowsAnotherMission(conversation.MissionIds.Count))
        {
            RecordTurn(conversation, ordinal, message, null);
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
            RecordTurn(conversation, ordinal, message, null);
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

        _ = ThreadingTask.Run(() =>
        {
            try { _startMission(message, id => idReady.TrySetResult(id), cts.Token); }
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
            RecordTurn(conversation, ordinal, message, null);
            return new ConversationOutcome(ConversationMode.Mission, false, null,
                $"mission failed to start: {error.InnerException?.Message ?? error.Message}", decision);
        }

        if (missionId.Length == 0)
        {
            // The row did not appear in time. The work may still be starting, so this is reported
            // rather than treated as a failure — but it is NOT linked, because linking an id we do
            // not have would be a fabricated history.
            RecordTurn(conversation, ordinal, message, null);
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
            RecordTurn(conversation, ordinal, message, null);
            return new ConversationOutcome(ConversationMode.Mission, true, null,
                "mission started, but the pipeline reported something that is not a mission id — "
              + "not linking it; check the mission list", decision);
        }

        RecordTurn(conversation, ordinal, message, missionId);
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

    private string RecordTurn(Conversation conversation, int ordinal, string message, string? missionId,
        IReadOnlyList<(string Filename, string Content)>? attachments = null)
    {
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
        sb.AppendLine("You are the ANTHILL colony's conversational assistant. Answer the operator's "
            + "last message concisely and truthfully. You have no tools in this conversation: if the "
            + "operator is asking for real multi-step work, say that missions are started by asking "
            + "for the work explicitly — never claim work you did not do.");
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
