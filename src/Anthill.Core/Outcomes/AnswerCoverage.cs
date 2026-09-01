namespace Anthill.Core.Outcomes;

/// <summary>
/// DID THE ANSWER ANSWER WHAT WAS ASKED? v0.3.8.106, PLAN.md §2b.
///
/// THE SEVENTH GATE, and the first that is not keyed to a mission CLASS. Its six siblings each
/// guard one class's own promise — an audit inspected, a diagnosis cited receipts, an operation
/// executed reversibly, a send landed where a human agreed. This one guards something every
/// specified mission owes regardless of class: that the requests the operator made have answers.
///
/// WHAT MAKES IT CHECKABLE, and it is the whole reason this could not ship at `.98`. Coverage is
/// CLAIM-AND-SERVED: a deliverable is covered when a task that owns it completed and left content.
/// The rejected alternative is on the record in `.98`'s changelog — "does the answer contain this
/// question's words … grades on vocabulary … a gate that demoted it would make every real gate less
/// trustworthy" — and that judgement stands. This asks a question the ledger can answer.
///
/// IT DOES NOT JUDGE THE ANSWER. Not whether it is correct, complete, deep, or on topic; a section
/// with one dismissive sentence is covered. Those are semantic calls and stay outside, the standing
/// line every gate in this directory holds. What it catches is the structural failure `.98` named:
/// three questions asked, one answered, mission reported complete.
///
/// AND IT IS SILENT FOR A MISSION THAT ASKED FOR NOTHING IN PARTICULAR. An unspecified
/// specification declares no deliverables, so there is nothing it could have omitted — inventing a
/// requirement for it is the error `.104`'s preflight already recorded and refused to repeat. Every
/// coding mission in the colony is in that lane by design.
/// </summary>
public static class AnswerCoverage
{
    /// <summary>The verdict, and the requests that went unanswered.</summary>
    public sealed record Result(
        bool Satisfied, IReadOnlyList<string> Missing, int Requested, int Answered)
    {
        public string Explanation => Satisfied
            ? $"answer coverage: {Answered} of {Requested} requested item(s) answered"
            : $"answer coverage NOT satisfied — {Requested - Answered} of {Requested} requested "
            + "item(s) have no answer: " + string.Join("; ", Missing);
    }

    /// <summary>
    /// True when the mission SAID what it was asked for. A specification with no deliverables
    /// declares no sections, so this layer has nothing to be about and leaves the mission entirely
    /// to the gates that ran before it.
    /// </summary>
    public static bool Applies(AssembledAnswer? answer) =>
        answer is { Specified: true, Sections.Count: > 0 };

    /// <summary>
    /// Evaluate an assembled answer.
    ///
    /// A NULL ANSWER FAILS CLOSED, and the asymmetry with `CitationIntegrity` is deliberate. That
    /// layer catches a claim the record CONTRADICTS, so an unreadable store contradicts nothing and
    /// returns satisfied. This layer asks whether something is ABSENT, and absence is the entire
    /// question — the same reasoning `AssessmentObjective` uses for an unreadable evidence store.
    /// Null here means the answer could not be assembled at all, which is not evidence that the
    /// requests were met.
    /// </summary>
    public static Result Evaluate(AssembledAnswer? answer)
    {
        if (answer is null)
            return new Result(false, new[] { "the answer could not be assembled" }, 0, 0);

        var missing = answer.Missing
            .Select(s => $"'{Trim(s.Request)}' ({s.DeliverableId}, {s.State})")
            .ToList();

        return new Result(
            missing.Count == 0,
            missing,
            answer.Sections.Count,
            answer.Sections.Count(s => s.Answered));
    }

    private static string Trim(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "(no request recorded)" :
        text!.Length <= 70 ? text.Trim() : text[..70].Trim() + "…";
}
