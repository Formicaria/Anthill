using Anthill.Core.Missions;
using Anthill.SDK.Artifacts;

namespace Anthill.Core.Outcomes;

/// <summary>
/// DID THE THING ACTUALLY LEAVE, AND DID IT GO WHERE THE HUMAN AGREED? v0.3.8.103.
///
/// THE FAILURE THIS EXISTS FOR is not the one `.102` guards. An infrastructure operation can be
/// judged by what it left behind on the operator's own machine, and if it was wrong the paired
/// action reverses it. A send cannot be judged that way and cannot be reversed at all: the moment
/// a message reaches a third party it is read by people the colony has no channel to. So this gate
/// asks the two questions that must be answered BEFORE that moment and can only be checked after —
/// was a concrete destination approved by a human, and is that where it went.
///
/// THREE TARGET FIELDS, THREE DIFFERENT QUESTIONS. A record naming only "the target" cannot
/// distinguish "the operator approved an alias" from "the operator approved this url" from "we sent
/// it somewhere else". The last one is the failure a signed approval makes invisible, and no
/// absence check would ever catch it — every field is populated, and the send is still wrong.
///
/// AN HONEST REFUSAL IS A COMPLETE RECORD AND A FAILED MISSION, and those are two judgments, not
/// one. `not_sent` with a reason is exactly what the record should say when nothing left; it is
/// also not the thing the operator asked for. Letting the honesty of the record satisfy the
/// deliverable would make "we told you we didn't do it" a passing grade, which is how a colony
/// learns that explaining is cheaper than doing.
///
/// WHAT IT DOES NOT CHECK, deliberately: whether the message was well written, whether the
/// recipient was the right one, or whether sending was wise. Semantic judgments, the standing line
/// since v2.19.0 — a model asserting them is exactly the evidence this repository stopped
/// accepting. Traceable delivery is checkable; appropriateness is not.
/// </summary>
public static class ExternalActionIntegrity
{
    /// <summary>The verdict, and every reason it failed — all of them, because fixing one at a time
    /// across four mission runs is how a gate becomes something an operator routes around.</summary>
    public sealed record Result(bool Satisfied, IReadOnlyList<string> Reasons)
    {
        public string Explanation => Satisfied
            ? "external action integrity: the send was approved, resolved and receipted"
            : "external action integrity NOT satisfied — " + string.Join("; ", Reasons);
    }

    public static bool Applies(MissionSpecification? specification) =>
        specification is { MissionClass: MissionSpecification.ExternalActionClass }
        && specification.IsActionable;

    /// <summary>
    /// Judge the mission's send.
    /// </summary>
    /// <param name="artifacts">The mission's artifacts, or null when the store could not be read.
    /// Null FAILS CLOSED, and the asymmetry with <see cref="CitationIntegrity"/> is deliberate
    /// rather than an inconsistency between two gates written by different releases. That one
    /// exists to catch a claim the record CONTRADICTS, and an unreadable store contradicts nothing.
    /// This one's entire question is whether something was DONE: a store that cannot be read cannot
    /// show a send, and "we cannot tell" must never resolve to "yes" for an irreversible action.</param>
    public static Result Evaluate(MissionSpecification specification, IReadOnlyList<Artifact>? artifacts)
    {
        if (artifacts is null)
            return new Result(false, new[]
            {
                "the artifact store could not be read, so no send can be shown — and an unshowable "
              + "send is not a send",
            });

        var action = artifacts
            .Where(a => string.Equals(a.Schema, ArtifactSchemas.ExternalAction, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => ExternalAction.FromJson(a.Payload))
            .FirstOrDefault(r => r is not null);

        if (action is null)
            return new Result(false, new[]
            {
                $"no external-action record exists for '{specification.OriginalRequest}'. The class "
              + "asks for something to leave the colony; nothing recorded that anything did",
            });

        // A truthful refusal, reported as itself. The mission still did not deliver.
        if (!action.WasSent)
            return new Result(false, new[]
            {
                string.IsNullOrWhiteSpace(action.RefusedBecause)
                    ? "nothing was sent, and the record does not say why"
                    : $"nothing was sent: {action.RefusedBecause}",
            });

        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(action.ResolvedTarget))
            reasons.Add("the record names no destination the request resolved to, so what a human "
                      + "approved cannot be stated");

        else if (!string.Equals(action.ResolvedTarget, action.ExecutedTarget, StringComparison.OrdinalIgnoreCase))
            // Not an absence — a DIFFERENCE, and the one this gate exists for.
            reasons.Add($"the send landed on '{action.ExecutedTarget}' and the approved target was "
                      + $"'{action.ResolvedTarget}' — an approval of one destination is not an "
                      + "approval of another");

        if (string.IsNullOrWhiteSpace(action.Receipt))
            reasons.Add("no receipt exists — 'the request was made' is not 'the destination accepted it'");

        if (string.IsNullOrWhiteSpace(action.ApprovedBy))
            reasons.Add("the record does not say who approved the send, and an irreversible action "
                      + "with no named approver has no one who decided it");

        return new Result(reasons.Count == 0, reasons);
    }

    /// <summary>
    /// The record this mission's answer must be rendered from, or null when it produced none.
    ///
    /// Read by the assembler ahead of every prose path, which is the mechanism the exit line's
    /// second half depends on: a builder whose send was refused writes about a send anyway, because
    /// it is writing about the task and has no way to know a tool said no several steps upstream.
    /// The channel that reports what happened has to be the channel that knows.
    /// </summary>
    public static ExternalAction? Record(IReadOnlyList<Artifact>? artifacts) =>
        artifacts?
            .Where(a => string.Equals(a.Schema, ArtifactSchemas.ExternalAction, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => ExternalAction.FromJson(a.Payload))
            .FirstOrDefault(r => r is not null);
}
