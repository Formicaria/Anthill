using Anthill.Core.Domain;

namespace Anthill.Core.Outcomes;

/// <summary>
/// v2.24.0 Phase C5: "was the goal met", on top of "did a verifier pass".
///
/// <see cref="MissionVerification"/> answers whether the mission ran a verification step that
/// returned a pass. That is necessary and not sufficient: a mission whose goal was "add a
/// CHANGELOG entry" can plan a researcher and a builder, produce a description of the change,
/// have the verifier legitimately pass — the tasks all did what they said — and deliver no file
/// change at all. Every gate downstream then reads `completed_verified`.
///
/// This adds a deliverable check. It is deliberately **additive**: the interim gate remains the
/// floor, and this can only ever narrow what counts as verified. Nothing that fails today can newly
/// pass because of this type.
///
/// It is also deliberately **modest**. Deciding "was the goal met" in general is a judgment call,
/// and a model asserting it is exactly the kind of evidence v2.19.0 stopped accepting. So the only
/// claim made here is one that can be checked deterministically: **when a goal plainly asks for a
/// file change, a file change must have been proposed.** Goals whose intent cannot be read fall
/// back to the interim gate alone — an unreadable goal must not be able to fail a mission that
/// otherwise verified, because that would punish work for the phrasing of its request.
/// </summary>
public static class ObjectiveVerification
{
    /// <summary>What a goal plainly asks the mission to produce.</summary>
    public enum Deliverable
    {
        /// <summary>Intent could not be read. The interim gate alone applies.</summary>
        Unknown,
        /// <summary>The goal asks for a file to be created, changed, or removed.</summary>
        FileChange,
    }

    /// <summary>
    /// Verbs that plainly ask for a file change. Matched on a lowercased goal.
    ///
    /// Kept narrow on purpose. A verb that only *might* imply a change ("improve", "handle",
    /// "support") would make this fire on missions that legitimately deliver an answer, and a
    /// deliverable check that misfires is worse than none: it would mark genuinely complete work
    /// unverified and suppress the learning that work earned.
    /// </summary>
    private static readonly string[] FileChangeVerbs =
    {
        "create a file", "add a file", "write a file", "edit the file", "modify the file",
        "patch the", "add a changelog", "update the changelog", "update the readme",
        "add documentation", "write documentation", "create a script", "add a test",
        "fix the bug", "refactor",
    };

    /// <summary>
    /// Read the goal's deliverable. Returns <see cref="Deliverable.Unknown"/> unless the intent is
    /// explicit — and always for a goal the operator constrained to produce no changes, where
    /// demanding one would contradict the instruction.
    /// </summary>
    public static Deliverable Required(string? goal, Common.MissionConstraints? constraints = null)
    {
        if (string.IsNullOrWhiteSpace(goal)) return Deliverable.Unknown;

        // A no-patch / read-only / verification-only mission is FORBIDDEN from changing files.
        // Requiring a change would make the two rules contradict each other, and this one would win
        // by failing every such mission.
        if (constraints?.BlocksPatches == true) return Deliverable.Unknown;

        var lowered = goal.ToLowerInvariant();
        return FileChangeVerbs.Any(v => lowered.Contains(v, StringComparison.Ordinal))
            ? Deliverable.FileChange
            : Deliverable.Unknown;
    }

    /// <summary>
    /// Whether the mission produced what its goal asked for.
    ///
    /// <paramref name="proposedPatchCount"/> is how many patch proposals the mission produced.
    /// Proposals, not applications: ANTHILL's contract is that ants propose and a human (or a
    /// gated auto-apply) applies, so requiring an *applied* change would fail every correctly
    /// operating mission awaiting approval.
    /// </summary>
    public static bool DeliverablePresent(Deliverable required, int proposedPatchCount) => required switch
    {
        Deliverable.FileChange => proposedPatchCount > 0,
        _ => true,   // nothing specific was asked for; nothing specific is required
    };

    /// <summary>
    /// The full gate: the interim verification floor AND the deliverable the goal asked for.
    /// </summary>
    /// <param name="constraints">v3.1.0 (ADR-002): the mission's constraints, resolved at intake.
    /// This used to re-parse the goal here, which meant the deliverable check could in principle
    /// read a mission's own instructions differently from the gate that admitted its tasks.</param>
    /// <param name="request">v0.3.8.110 — the OPERATOR'S ASK, from the mission's recorded contract,
    /// rather than <c>mission.Goal</c>.
    ///
    /// WHAT WAS WRONG. `mission.Goal` is the COMPOSED goal: `ComposeMissionGoal` appends the
    /// standing context and the conversation transcript below a `--- ` marker, and this method
    /// substring-matches verbs like "refactor" and "update the readme" against the whole of it. So a
    /// mission whose transcript happened to contain someone typing "refactor" acquired a file-change
    /// requirement it was never given, and was graded not_satisfied for producing no patch. Intake
    /// has read only the operator's own words since `.98` — `MissionIntake.OperatorAskOnly` exists
    /// for exactly this, and `.96` paid for the lesson live when the UI gate's own refusal prose
    /// entered a transcript and re-tripped the gate on every later mission.
    ///
    /// Null keeps the previous behaviour exactly, for callers outside the mission engine.</param>
    public static bool IsSatisfied(Mission? mission, Common.MissionConstraints constraints, int proposedPatchCount,
        string? request = null)
    {
        if (mission is null) return false;
        if (!MissionVerification.IsSatisfied(mission.Tasks)) return false;   // the floor, unchanged

        return DeliverablePresent(Required(request ?? mission.Goal, constraints), proposedPatchCount);
    }

    /// <summary>Why the objective check said no — operator-facing, never a silent downgrade.</summary>
    /// <param name="request">v0.3.8.110 — the operator's ask, for the reason
    /// <see cref="IsSatisfied"/> gives. This one matters twice over: it is the sentence an operator
    /// READS, so an explanation derived from a different string than the grade would tell them the
    /// mission was demoted for something other than what demoted it.</param>
    public static string Explain(Mission? mission, Common.MissionConstraints constraints, int proposedPatchCount,
        string? request = null)
    {
        if (mission is null) return "no mission";
        if (!MissionVerification.IsSatisfied(mission.Tasks)) return MissionVerification.Explain(mission.Tasks);

        var required = Required(request ?? mission.Goal, constraints);
        if (DeliverablePresent(required, proposedPatchCount)) return "objective satisfied";

        return "the goal asks for a file change and the mission proposed none — "
             + "the tasks completed and verified, but the thing that was asked for was not produced";
    }
}
