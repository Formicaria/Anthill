using Anthill.Core.Missions;
using Anthill.SDK.Artifacts;

namespace Anthill.Core.Outcomes;

/// <summary>
/// WAS THE ASSESSMENT ACTUALLY DELIVERED? v0.3.8.98.
///
/// <see cref="MissionVerification"/> asks whether a verification step passed, and
/// <see cref="ObjectiveVerification"/> asks whether a goal that plainly demanded a FILE CHANGE
/// produced one. Neither can grade an audit: an audit changes nothing by construction, so the
/// deliverable layer reads `not_applicable` and the whole judgment collapses onto a verifier model
/// saying the words "Verification Passed". Mission `7afd85b2` is what that permits — two tasks
/// completed, nothing inspected, the requested assessment absent, and a positive grade available
/// for the asking.
///
/// So this asks the three questions an assessment can be held to WITHOUT a model's opinion, each
/// answered from a record the mission left behind rather than from prose about it:
///
///   1. DID ANYTHING GET INSPECTED? An assessment built on no observation is an assertion. The
///      evidence store answers, and `EvidenceKinds.Inspection` is the row an inspection leaves.
///   2. DID THE VERIFIER READ WHAT IT GRADED? The consumption ledger answers. A verifier that
///      consumed nothing graded prose it was handed, which is the "two channels and the prose one
///      wins" failure ADR-004 exists to prevent, arriving at the last gate instead of the first.
///   3. IS THERE AN ANSWER AT ALL? An assessment mission whose deliverable is absent is the
///      recorded failure in its purest form, and it costs nothing to refuse.
///
/// DELIBERATELY MODEST, for the same reason <see cref="ObjectiveVerification"/> is. It does NOT yet
/// check that each requested question was answered — see the note on question 3 in the body for why
/// the obvious implementation grades vocabulary rather than content, and what has to exist first.
/// A floor that catches the recorded failure beats a ceiling that pretends to measure quality.
///
/// AND DELIBERATELY ADDITIVE. It can only narrow what counts as verified: it applies to one mission
/// class, and nothing that fails today can newly pass because of it.
/// </summary>
public static class AssessmentObjective
{
    /// <summary>The verdict, with the reasons it can be explained by. Empty reasons ⇒ satisfied.</summary>
    public sealed record Result(bool Satisfied, IReadOnlyList<string> Reasons)
    {
        /// <summary>Operator-facing, and it names the gate — a refusal nobody can locate is a
        /// refusal nobody can fix.</summary>
        public string Explanation => Satisfied
            ? "assessment objective: satisfied"
            : "assessment objective NOT satisfied — " + string.Join("; ", Reasons);
    }

    private static readonly Result Ok = new(true, Array.Empty<string>());

    /// <summary>
    /// True when this layer has anything to say. An unclassified request, or one of a class this
    /// release does not serve, is left entirely to the existing gates — which is what makes the
    /// change safe for every mission that ran before it.
    /// </summary>
    public static bool Applies(MissionSpecification? specification) =>
        specification is { MissionClass: MissionSpecification.SystemAuditClass } && specification.IsActionable;

    /// <summary>
    /// Grade the assessment. Every input is a RECORD the mission left — never a task's self-report.
    /// </summary>
    /// <param name="evidence">The mission's evidence rows, or null when the store could not be
    /// read. Null fails CLOSED: an unreadable store is not proof that an inspection happened, and
    /// "an outage is never permission" is this repository's standing rule (PLAN §1b S3).</param>
    /// <param name="consumptions">The artifact consumption ledger for this mission, same rule.</param>
    /// <param name="answer">The operator-facing answer, as assembled — the text the operator will
    /// actually read, not an intermediate task result.</param>
    public static Result Evaluate(MissionSpecification specification,
        IReadOnlyList<Evidence>? evidence,
        IReadOnlyList<ArtifactConsumption>? consumptions,
        string? answer)
    {
        if (!Applies(specification)) return Ok;

        var reasons = new List<string>();

        // 1. An inspection happened — of every kind the specification said this class requires.
        //
        // Read from `RequiredEvidence` rather than hard-coded here, so the requirement is stated
        // once, where the class is defined, and a class that later needs a second kind gets it by
        // saying so instead of by editing this gate.
        if (evidence is null)
            reasons.Add("the evidence store could not be read, so no inspection can be shown");
        else
            foreach (var kind in specification.RequiredEvidence)
                if (!evidence.Any(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase)))
                    reasons.Add($"no '{kind}' evidence was recorded — the assessment rests on nothing that was read");

        // 2. The verifier read what it graded.
        if (consumptions is null)
            reasons.Add("the consumption ledger could not be read, so the verifier's inputs are unknown");
        else if (!consumptions.Any(c => string.Equals(c.ConsumerRole, "verifier", StringComparison.OrdinalIgnoreCase)))
            reasons.Add("the verifier consumed no artifact — it graded prose rather than the record");

        // 3. There is an answer at all.
        //
        // PER-DELIVERABLE COVERAGE IS NOT CHECKED HERE, AND THAT IS DELIBERATE. The obvious
        // implementation — does the answer contain this deliverable's subject words — grades on
        // VOCABULARY: an answer that says "Strengths: … Weaknesses: …" addresses "what is good and
        // bad about it" completely and contains neither word. A gate that demoted that mission
        // would be measuring spelling, which is the misfire `ObjectiveVerification`'s own doc warns
        // against, and the wrong kind of strictness makes a real gate untrustworthy faster than a
        // missing one does. Coverage becomes checkable when a deliverable can be CLAIMED — a task
        // asserting it serves `d2`, the assembler recording that the section was composed from that
        // task's output — which is the deliverable ledger, and it lands with the assembler rather
        // than being faked here from a word search. Until then this layer catches an assessment
        // that inspected nothing, a verifier that read nothing, and a mission with no answer; it
        // does not claim to catch a question quietly dropped.
        if (string.IsNullOrWhiteSpace(answer))
            reasons.Add("the mission produced no operator-facing answer");

        return reasons.Count == 0 ? Ok : new Result(false, reasons);
    }
}
