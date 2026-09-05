using Anthill.SDK.Artifacts;

namespace Anthill.Core.Outcomes;

/// <summary>
/// DID THE ANSWER COME FROM SOMEWHERE, AND CAN IT SAY WHERE? v0.3.8.109.
///
/// THE CLASS'S PROMISE, and therefore the whole of what this checks. A research mission is admitted
/// on the strength of one claim: that the answer will rest on things the colony went and read. Every
/// other class's gate asks the same shape of question about its own promise — did the audit inspect,
/// did the diagnosis run checks, did the operation reverse, did the send land where it was approved.
/// This one asks whether anything was retrieved, whether the answer attributes itself to what was
/// retrieved, and whether each of the operator's requests traces back to a retrieval.
///
/// WHY IT IS NOT JUST <see cref="CitationIntegrity"/> UNDER A NEW NAME. That layer resolves cited
/// urls against retrieved ones, and it is reused here rather than reimplemented — a second resolver
/// would eventually disagree with the first about what counts as retrieved. What it cannot do alone
/// is speak for a mission's STRUCTURE: an answer with no citations at all passes it trivially, and a
/// research mission whose sections were written by a builder that consulted nothing is exactly that
/// answer. The two questions are "is what you cited real" and "did you look at anything", and only
/// the second is a question about the class.
///
/// GROUNDING DEGRADES HONESTLY, and this is the release's one genuinely awkward corner, so it is
/// stated rather than hidden behind a helper. A section's own evidence is meaningful only when the
/// plan DECLARED which task serves which deliverable. Under an inferred claim the compiling builder
/// is credited with every deliverable, and a builder leaves no evidence — it writes prose from other
/// tasks' output. Requiring per-section grounding there would fail every research mission whose
/// planner did not itemise its steps, which grades the planner's verbosity rather than the work. So
/// a declared section must carry its own retrieval, and an inferred one falls back to the mission's:
/// weaker, named as weaker, and never silently equated with the strong case.
///
/// WHAT IT DOES NOT CHECK, in this layer's standing tradition: whether the sources are any good,
/// whether they support what they are cited for, or whether the answer is true. Those are semantic
/// judgments, a model asserting one is the evidence v2.19.0 stopped accepting, and a gate that
/// reached for them would make every gate beside it less trustworthy.
/// </summary>
public static class ResearchIntegrity
{
    /// <summary>The verdict and the reasons, one per thing that was missing.</summary>
    public sealed record Result(bool Satisfied, IReadOnlyList<string> Failures, int Retrieved, int GroundedSections)
    {
        public string Explanation => Satisfied
            ? $"research integrity: {Retrieved} source(s) retrieved, "
            + $"{GroundedSections} section(s) traced to a retrieval"
            : "research integrity NOT satisfied — " + string.Join("; ", Failures);
    }

    /// <summary>
    /// Keyed on the CLASS, like every sibling that guards one class's own promise. A mission that
    /// merely searched the web is not a research mission and is left to <see cref="CitationIntegrity"/>
    /// alone, exactly as it was before this class existed.
    /// </summary>
    public static bool Applies(Missions.MissionSpecification? specification) =>
        specification is not null
     && string.Equals(specification.MissionClass, Missions.MissionSpecification.ResearchClass,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Evaluate.
    /// </summary>
    /// <param name="artifacts">The mission's artifacts, or null when the store could not be read.
    /// Null FAILS, and the asymmetry against `.99`'s permissive null is the same one
    /// <c>AssessmentObjective</c> draws: this layer's question is whether something happened, and an
    /// unreadable store cannot show that it did. "We could not tell" must not read as "yes" for a
    /// mission whose entire class is a promise that a specific thing happened.</param>
    /// <param name="evidence">The mission's evidence rows, or null. Null fails for the same
    /// reason.</param>
    /// <param name="answer">The assembled answer, so each request can be traced to a retrieval
    /// rather than to the mission as a whole.</param>
    /// <param name="recalledArtifacts">v0.3.8.123 — a prior mission's artifacts by id, handed
    /// straight to <see cref="CitationIntegrity"/>. Passed THROUGH rather than acted on here, and
    /// that is the point: this class delegates the "is what you cited real" question precisely so
    /// the two layers cannot disagree about what counts as retrieved, and a research mission whose
    /// answer cites another mission's narrative must therefore inherit the same refusal a coding
    /// mission would get for it. A parameter this class never reads for itself is the cheapest way
    /// to keep that inheritance true rather than merely intended.</param>
    public static Result Evaluate(
        Missions.MissionSpecification specification,
        IReadOnlyList<Artifact>? artifacts,
        IReadOnlyList<Evidence>? evidence,
        AssembledAnswer? answer,
        Func<string, IReadOnlyList<Artifact>?>? recalledArtifacts = null)
    {
        ArgumentNullException.ThrowIfNull(specification);

        var failures = new List<string>();

        // 1. SOMETHING WAS RETRIEVED. Delegated to the citation layer's contract trigger rather
        //    than re-derived, so the two can never disagree about what a retrieval is.
        var citations = CitationIntegrity.Evaluate(specification, artifacts, recalledArtifacts);
        if (!citations.Satisfied) failures.Add(citations.Explanation);

        // 2. AND THE STORE AGREES. The artifact record says what was cited; the evidence record
        //    says a retrieval TOOL RAN. Both, because they are written by different layers and a
        //    mission with one and not the other is a mission whose two accounts of itself disagree
        //    — which is the shape ADR-004 exists to refuse.
        if (evidence is null)
            failures.Add("the evidence store could not be read, so nothing can show that any "
                       + "retrieval actually ran");
        else if (!evidence.Any(e => string.Equals(e.Kind, EvidenceKinds.SourceRetrieval,
                     StringComparison.OrdinalIgnoreCase)))
            failures.Add("no retrieval was recorded in the evidence store — the answer names sources "
                       + "the colony has no record of having fetched");

        // 3. AND EACH REQUEST TRACES TO ONE. See the type's remarks: a DECLARED section must carry
        //    its own retrieval, an INFERRED one falls back to the mission's, and the difference is
        //    named in the failure rather than flattened.
        var grounded = 0;
        var missionGrounded = evidence?.Any(e => string.Equals(e.Kind, EvidenceKinds.SourceRetrieval,
            StringComparison.OrdinalIgnoreCase)) == true;

        foreach (var section in answer?.Sections ?? Array.Empty<AnswerSection>())
        {
            // An unanswered section is already the coverage gate's refusal. Failing it a second
            // time here would tell the operator the same thing twice in different words.
            if (!section.Answered) continue;

            var declared = string.Equals(section.Claim, Missions.DeliverableClaim.Declared,
                StringComparison.Ordinal);

            if (declared && !section.Grounded)
                failures.Add($"'{section.DeliverableId}' was answered by a step the plan named for it "
                           + "and that step recorded no retrieval — the section was written without "
                           + "consulting anything");
            else if (declared || missionGrounded)
                grounded++;
        }

        return new Result(failures.Count == 0, failures,
            CitationIntegrity.Retrieved(artifacts).Count, grounded);
    }
}
