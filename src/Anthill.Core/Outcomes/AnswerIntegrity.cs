using Anthill.SDK.Artifacts;

namespace Anthill.Core.Outcomes;

/// <summary>
/// DID THE ANSWER ANSWER, AND DID IT CHANGE NOTHING DOING IT? v0.3.8.134.
///
/// THE CLASS'S PROMISE, and therefore the whole of what this checks. A `simple_answer` mission is
/// admitted on the strength of a claim its siblings never make: that the request needs nothing done
/// to anything. Every other gate in this folder asks whether a promised thing HAPPENED — did the
/// audit inspect, did the diagnosis run checks, did the operation reverse, did the send land, did
/// the research retrieve. This one asks the mirror question, and it is the harder shape to check
/// because absence is cheap to fake and expensive to prove: did anything happen that should not
/// have.
///
/// WHY IT IS NOT REDUNDANT WITH THE AUTHORITY CEILING. <c>MissionAuthorityGate</c> refuses
/// `apply_patch`, `write_text_file` and `shell_command` at dispatch, and that refusal is the real
/// enforcement — this gate cannot stop anything, it runs after the mission is over. What it adds is
/// the RECORD-SIDE account. A ceiling refuses a tool call; it says nothing about a plan that typed a
/// step as `patch_proposal`, or an ant that filed a patch artifact by a path the ceiling does not
/// govern, or a future release that adds a change lane and forgets to name it in the requirements
/// table. Two independent accounts that must agree is the shape ADR-004 asks for, and it is exactly
/// how `ResearchIntegrity` treats its own class: the artifacts say one thing, the evidence says
/// another, and a mission whose two accounts of itself disagree is refused.
///
/// THE DEFECT IT CLOSES, stated plainly because a gate whose motivating failure is unrecorded gets
/// deleted by the next reader who cannot see what it is for. "How do you make tacos" was answered
/// with a proposed source patch. The dynamic planner's standing rule — a goal that creates, adds,
/// writes or edits ANY file must include a `patch_proposal` coder task — fired on a goal naming no
/// file, and nothing downstream could say the plan was wrong, because the mission had no class and
/// therefore no promise to contradict. The class is what makes the contradiction expressible; this
/// is what expresses it.
///
/// WHAT IT DOES NOT CHECK, in this layer's standing tradition: whether the answer is TRUE, or good,
/// or whether the model actually knew what it said. Those are semantic judgments, a model asserting
/// one is the evidence v2.19.0 stopped accepting, and this class more than any other invites the
/// reach — it is the class with no receipts, so grading its content is the only thing left to want.
/// Wanting it is not a reason to do it. An unverifiable answer is what the operator asked for when
/// they asked a question with nothing to inspect, and a gate that pretended otherwise would make
/// every gate beside it less trustworthy.
/// </summary>
public static class AnswerIntegrity
{
    /// <summary>
    /// Task types whose work changes something, spelled once. The planner may propose any of them;
    /// what this class says is that none of them belongs in a mission admitted as needing no change
    /// — and a type the planner can emit that this set does not watch is a lane around the gate,
    /// which is `CreationIntegrity.CreationTaskTypes`' own hard-won lesson kept.
    /// </summary>
    public static readonly IReadOnlySet<string> ChangeTaskTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "patch_proposal",
            "patch",
            "code_change",
            "file_write",
            "system_operation",
            "external_action",
        };

    /// <summary>
    /// Artifact schemas that RECORD a change having been proposed or performed. The second account:
    /// a plan can be clean and an ant still file one of these, and the disagreement is the finding.
    /// </summary>
    public static readonly IReadOnlySet<string> ChangeArtifactSchemas =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ArtifactSchemas.PatchSet,
            ArtifactSchemas.ChangePlan,
            "docs_patch_set",
            "system_operation",
            "external_action",
        };

    /// <summary>The verdict and the reasons, one per thing that was wrong.</summary>
    public sealed record Result(bool Satisfied, IReadOnlyList<string> Failures, int Answered, int Changes)
    {
        public string Explanation => Satisfied
            ? $"answer integrity: {Answered} request(s) answered, nothing changed"
            : "answer integrity NOT satisfied — " + string.Join("; ", Failures);
    }

    /// <summary>
    /// Keyed on the CLASS, like every sibling that guards one class's own promise. A `general`
    /// mission that happens to look like an answer is untouched and ungraded, exactly as it was
    /// before this class existed.
    /// </summary>
    public static bool Applies(Missions.MissionSpecification? specification) =>
        specification is not null
     && string.Equals(specification.MissionClass, Missions.MissionSpecification.SimpleAnswerClass,
            StringComparison.OrdinalIgnoreCase);

    /// <param name="specification">The mission's recorded specification.</param>
    /// <param name="taskTypes">The task types of the mission's plan. Read rather than the tasks
    /// themselves because the typing is what the planner DECIDED, and a decision is the thing this
    /// gate has standing to contradict.</param>
    /// <param name="artifacts">The mission's artifacts, or null when the store could not be read.
    /// Null FAILS, and the asymmetry against `.99`'s permissive null is the one
    /// <c>ResearchIntegrity</c> and <c>AssessmentObjective</c> already draw. It reads oddly for a
    /// gate that mostly checks for ABSENCE — surely an unreadable store is the same as an empty one
    /// — and that is precisely why it must not: "we could not see whether anything was changed" is
    /// not the same fact as "nothing was changed", and this class's entire promise is the second
    /// one.</param>
    /// <param name="answer">The assembled answer, so "an answer exists" is decided from the
    /// persisted join rather than by looking at rendered prose.</param>
    public static Result Evaluate(
        Missions.MissionSpecification specification,
        IEnumerable<string?> taskTypes,
        IReadOnlyList<Artifact>? artifacts,
        AssembledAnswer? answer)
    {
        ArgumentNullException.ThrowIfNull(specification);

        var failures = new List<string>();
        var changes = 0;

        // 1. THE PLAN DID NOT REACH FOR A CHANGE LANE. The taco defect, caught at its source: the
        //    planner typed a step as a patch for a question that asked for none.
        foreach (var typed in (taskTypes ?? Array.Empty<string?>())
                     .Where(t => ChangeTaskTypes.Contains(t ?? ""))
                     .Select(t => t!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            changes++;
            failures.Add($"the plan typed a step as '{typed}' for a request that named nothing to "
                       + "change — the mission was admitted as answerable from what is already "
                       + "known, and a change lane is not an answer");
        }

        // 2. AND NEITHER DID ANYTHING ELSE. The second account. See the type's remarks: the plan can
        //    be clean and an artifact still say a change was proposed, and the two disagreeing is
        //    the finding rather than a discrepancy to reconcile quietly.
        if (artifacts is null)
        {
            failures.Add("the artifact store could not be read, so nothing can show that the answer "
                       + "changed nothing");
        }
        else
        {
            foreach (var artifact in artifacts.Where(a => ChangeArtifactSchemas.Contains(a.Schema)))
            {
                changes++;
                failures.Add($"a '{artifact.Schema}' record was filed by a mission whose class "
                           + "promises it changes nothing — the plan and the artifact store do not "
                           + "agree about what this mission did");
            }
        }

        // 3. AND SOMETHING WAS ACTUALLY ANSWERED. A class whose whole content is the answer, which
        //    produced none, has not merely underdelivered — it has nothing at all to show, and the
        //    coverage gate above speaks only for a mission that produced sections to cover.
        var answered = (answer?.Sections ?? Array.Empty<AnswerSection>()).Count(s => s.Answered);
        if (answered == 0)
            failures.Add("no request was answered — a mission admitted to answer a question and "
                       + "answering none of it has produced nothing this class can stand behind");

        return new Result(failures.Count == 0, failures, answered, changes);
    }
}
