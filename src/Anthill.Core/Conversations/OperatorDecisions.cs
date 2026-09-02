using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.SDK.Common;

namespace Anthill.Core.Conversations;

/// <summary>
/// v0.3.8.102 — the first CONSUMER of the rule v0.3.8.46 wrote: "an answer given IS an operator
/// decision; the record must say so whether or not the work ends up needing it."
///
/// A mission does not run inside the conversation's ambient <see cref="ConversationScope"/> — it
/// runs in the background, deliberately, and the .101 composed missions prove it: their checks
/// dispatch without per-tool answers. So a tool that needs the operator's decision MID-MISSION
/// cannot ask the scope; it must read the DURABLE record the runner saved at mission start. The
/// permission is the record, and this is where the record is read.
///
/// Resolution is by the mission's own lineage: the conversation whose MissionIds carry this
/// mission, then the LATEST saved decision for the action. Latest, because an operator who
/// refused and then approved has decided twice and the second answer is the standing one — the
/// same last-write-wins the settings store applies. No conversation, or no decision, returns
/// null: absence is not consent, and the caller says so.
/// </summary>
public static class OperatorDecisions
{
    /// <summary>
    /// The mission's standing decision for <paramref name="action"/>, or null.
    ///
    /// v0.3.8.105 — AND, WHEN THERE IS NONE, THE QUESTION IS FILED. This method still returns
    /// exactly what it returned before: null when no decision exists, which every caller refuses
    /// on. What is new is that the absence stops being invisible.
    ///
    /// Why HERE and not in the callers. This is the one site in the mission lane that discovers "a
    /// side-effecting action needed the operator and the operator has not spoken" — the tools that
    /// call it each refuse in their own words, and asking each of them to also file a request would
    /// put one obligation in three places, which is how two of them end up disagreeing about
    /// whether it was met. The type's subject is the operator's decision for a mission action; an
    /// unanswered question is a state of that decision and not a different subject.
    ///
    /// It is NOT a second gate and it grants nothing. Filing fails silently rather than turning a
    /// correct refusal into an exception.
    /// </summary>
    public static EscalationDecision? ForMission(SqliteMemory memory, string? missionId, string action)
    {
        if (string.IsNullOrWhiteSpace(missionId) || string.IsNullOrWhiteSpace(action)) return null;
        try
        {
            var conversation = memory.LoadConversations()
                .FirstOrDefault(c => c.MissionIds.Contains(missionId, StringComparer.OrdinalIgnoreCase));

            var decision = conversation is null ? null
                : memory.LoadEscalationDecisions(conversation.Id)
                    .Where(d => string.Equals(d.Action, action, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(d => d.DecidedAt)
                    .LastOrDefault();

            // v0.3.8.110 — AND THE APPROVAL LEDGER IS AN ANSWER TOO.
            //
            // THE GAP THIS CLOSES, and it is why `.105`'s own description had to say "approving
            // does not replay the refused step". The question was filed into `approval_requests`
            // and this method read `escalation_decisions` — two disjoint tables. An operator who
            // approved through `/approve/{id}` changed a row that nothing on the mission path could
            // see, so the next dispatch of that action refused identically and filed the same
            // question again. The approval was real, recorded, visible in the UI, and inert.
            //
            // It is resolved by the SAME rule the conversation lane uses, extended rather than
            // duplicated: last answer wins. An operator who approved in the conversation and then
            // rejected the request has decided twice, and the second answer stands whichever ledger
            // it landed in.
            var filed = Decided(memory, missionId!, action);
            if (filed is not null && (decision is null || filed.DecidedAt >= decision.DecidedAt))
                decision = filed;

            // NOBODY WAS ASKED. A rejection is an answer and needs no request; only an absence
            // does. A missing CONVERSATION counts as an absence too — an autonomous or CLI mission
            // that reached a side-effecting action has no channel to have been asked through, and
            // that is more reason for the question to exist than less.
            if (decision is null) Request(memory, missionId!, action, "queen");

            return decision;
        }
        catch
        {
            // An unreadable store yields NO decision, which the caller refuses on — the S3 rule:
            // an outage is never permission.
            return null;
        }
    }

    /// <summary>
    /// FILE THE QUESTION THE MISSION IS STOPPING FOR. v0.3.8.105, PLAN.md §2b `.105`.
    ///
    /// One pending row in the ledger that already exists — `approval_requests`, with
    /// <see cref="ApprovalActionType.ToolUse"/>. No new table and no second vocabulary: that action
    /// type has been declared since the enum was written and had no producer anywhere in the tree,
    /// because every approval this colony has ever raised is a `PatchProposal`.
    ///
    /// It grants nothing and unblocks nothing. The refusal that brought us here stands. What this
    /// adds is the trace an operator can act on — `escalation_refused` says a thing did not happen,
    /// and a reader of that alone has no way to make it happen — and the record
    /// `MissionEvaluator` reads to grade the mission `waiting_for_approval` instead of failed.
    ///
    /// DEDUPED ON `&lt;mission&gt;:&lt;action&gt;`. A retried task asks the same question, and three
    /// identical pending rows would make an operator answer once per attempt to unblock one
    /// mission. Any existing row for that target — pending, approved or rejected — suppresses a new
    /// one: re-asking a question that was already answered no is worse than not asking.
    ///
    /// BEST-EFFORT BY CONSTRUCTION. Callers are on a refusal path whose safety-relevant half is
    /// already decided, so a storage failure must not become an exception. It prints, because a
    /// question that was never filed is a mission that stops with nothing to answer.
    ///
    /// `approval_requests` CARRIES A FOREIGN KEY TO `missions(id)`, which is the trap `.104` paid
    /// for when the contract table was written before the mission row existed. Every caller here is
    /// mid-execution and the mission was saved long before, so this holds — and if a future caller
    /// files earlier, the catch below turns the constraint into a printed line rather than a broken
    /// mission. That is the correct direction: losing the question is smaller than losing the run.
    /// </summary>
    /// <summary>
    /// The operator's answer as the APPROVAL LEDGER holds it, or null while the question stands.
    /// v0.3.8.110.
    ///
    /// A pending row is not an answer and returns null — which is the whole reason the row exists,
    /// and the reason this cannot simply test for the row's presence. Approved is consent, rejected
    /// is a refusal, and both are decisions; only "nobody has said anything" is an absence.
    ///
    /// `DecidedBy` is "operator" because that is what `/approve/{id}` means: a person acted on a
    /// request in the interface. It matters beyond bookkeeping — <see cref="EscalationDecision.AwaitingDecision"/>
    /// is true only while `DecidedBy` is <see cref="EscalationDecision.Undecided"/>, so a decision
    /// synthesised with the wrong attribution would keep re-filing the question it just answered.
    ///
    /// The policy is recorded as `Ask` because that is the policy under which the question was
    /// raised. Reading it back as anything else would say the colony was configured differently from
    /// how it actually ran.
    /// </summary>
    public static EscalationDecision? Decided(SqliteMemory memory, string missionId, string action)
    {
        if (string.IsNullOrWhiteSpace(missionId) || string.IsNullOrWhiteSpace(action)) return null;
        try
        {
            // v0.3.8.113 — TYPED. The status comparison was two string equalities against
            // `.Value()`, and the timestamp was parsed here with its own culture and style flags —
            // a third copy of a rule the store now applies once. What is left is the decision this
            // method actually makes.
            var approval = memory.ApprovalForTarget($"{missionId}:{action}", ApprovalActionType.ToolUse);
            if (approval is null) return null;

            var approved = approval.Status == ApprovalStatus.Approved;
            var rejected = approval.Status == ApprovalStatus.Rejected;
            if (!approved && !rejected) return null;

            var when = approval.DecidedAt ?? AnthillTime.NowUtc();

            return new EscalationDecision(
                Id: approval.Id.Length > 0 ? approval.Id : Guid.NewGuid().ToString("N")[..12],
                ConversationId: "",
                Action: action,
                Allowed: approved,
                Policy: EscalationPolicy.Ask,
                DecidedBy: "operator",
                DecidedAt: when,
                Reason: approved
                    ? "approved by the operator through the approval ledger"
                    : "rejected by the operator through the approval ledger");
        }
        catch
        {
            // The S3 rule, unchanged: an outage is never permission.
            return null;
        }
    }

    public static void Request(SqliteMemory memory, string missionId, string action, string requestedBy)
    {
        if (string.IsNullOrWhiteSpace(missionId) || string.IsNullOrWhiteSpace(action)) return;
        try
        {
            var target = $"{missionId}:{action}";
            if (memory.ApprovalForTarget(target, ApprovalActionType.ToolUse) is not null) return;

            memory.SaveApprovalRequest(new ApprovalRequest
            {
                MissionId = missionId,
                ActionType = ApprovalActionType.ToolUse,
                TargetId = target,
                Status = ApprovalStatus.Pending,
                RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? "queen" : requestedBy,
                Title = $"Approve '{action}' for this mission?",
                Description =
                    $"This mission reached the side-effecting action '{action}' and no operator "
                  + "decision is recorded for it, so the call was refused — absence of an answer is "
                  + "not consent. The mission is waiting on this, not failing on it. Approving "
                  + "records the decision AND replays the step that was refused (v0.3.8.110); "
                  + "rejecting records a refusal that stands and replays nothing.",
                Metadata = new() { ["action"] = action, ["requested_by"] = requestedBy },
            });

            memory.LogEvent(missionId, "operator_decision_requested",
                $"Waiting on an operator decision for '{action}'.", null, requestedBy,
                new() { ["action"] = action, ["target_id"] = target,
                        ["action_type"] = ApprovalActionType.ToolUse.Value() });
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                $"[escalation] could not file an operator decision request for '{action}' on "
              + $"{missionId}: {error.Message} — the refusal stands and nothing was approved, but "
              + "the operator has nothing to answer.");
        }
    }
}
