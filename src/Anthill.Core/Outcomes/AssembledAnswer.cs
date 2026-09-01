using Anthill.Core.Domain;
using Anthill.Core.Missions;

namespace Anthill.Core.Outcomes;

/// <summary>What happened to one requested section. The three states are not degrees of the same
/// thing — they want different words to an operator and different treatment from the gate.</summary>
public static class AnswerSectionState
{
    /// <summary>A task that owns this deliverable completed and left content.</summary>
    public const string Answered = "answered";

    /// <summary>A task that owns it completed and left NOTHING. Distinct from unanswered: the step
    /// ran, so the plan was not the problem, and an operator chasing a missing step would be
    /// chasing the wrong thing.</summary>
    public const string Empty = "empty";

    /// <summary>Nothing owns it, or nothing that owns it finished. The request has no answer.</summary>
    public const string Unanswered = "unanswered";
}

/// <summary>
/// One requested deliverable and the text that answers it, with the lineage that put it there.
/// </summary>
/// <param name="Claim">From <see cref="DeliverableClaim"/> — whether the plan DECLARED this
/// section's owner or the runtime inferred it. Carried into the answer rather than left in the
/// ledger, because "the plan mapped your question to a step" and "one builder was assumed to cover
/// everything" are different assurances and the operator is the one who should get to tell.</param>
/// <param name="EvidenceIds">v0.3.8.109 — the evidence rows the tasks that served THIS section
/// actually left. The join existed at `.106` (a section knows its serving tasks, and evidence knows
/// its task) and had no consumer; §2c recorded that rendering a join is not the same as making it a
/// checkable property. <see cref="ResearchIntegrity"/> is its consumer.</param>
public sealed record AnswerSection(
    string DeliverableId,
    string Request,
    string Content,
    IReadOnlyList<string> ServingTaskIds,
    string Claim,
    string State,
    IReadOnlyList<string> EvidenceIds)
{
    public bool Answered => string.Equals(State, AnswerSectionState.Answered, StringComparison.Ordinal);

    /// <summary>
    /// This section rests on something the mission recorded doing. v0.3.8.109.
    ///
    /// MEANINGFUL ONLY WHERE THE CLAIM IS <see cref="DeliverableClaim.Declared"/>, and any consumer
    /// has to know that. Under an INFERRED claim the compiling builder is credited with every
    /// deliverable, and a builder leaves no evidence of its own — it writes prose from other tasks'
    /// output — so every section of such a mission is ungrounded by this measure however much work
    /// the mission did. A gate reading this without that distinction would be grading how explicitly
    /// the plan attributed its steps, which is a property of the planner's verbosity and not of the
    /// answer.
    /// </summary>
    public bool Grounded => EvidenceIds.Count > 0;
}

/// <summary>
/// THE ANSWER IS BUILT FROM WHAT WAS ASKED, SECTION BY SECTION. v0.3.8.106, PLAN.md §2b.
///
/// WHAT THIS REPLACES, in `.98`'s own words: "<c>ResultAssembler</c> never read it at all and
/// returned the last builder task's output as the answer." Three layers interpreted the operator's
/// request and the one that produced what the operator READS interpreted nothing — it picked a task
/// by role (last completed builder, else coder, else anything) and handed over its raw text. A
/// mission that was asked three questions and answered one produced an answer indistinguishable
/// from a mission that answered all three, because the answer was never about the questions.
///
/// SO THE SPECIFICATION IS THE OUTLINE. Each requested deliverable is a section; each section's
/// content is the recorded output of the tasks that SERVED it; a request nothing served says so in
/// the answer rather than only in a ledger the operator has to go and find.
///
/// COVERAGE IS CLAIM-AND-SERVED, NEVER A WORD SEARCH, and that is a rule this repository already
/// paid for. `.98` considered the obvious implementation and rejected it in writing: "does the
/// answer contain this question's words — grades on vocabulary: an answer reading 'Strengths: …
/// Weaknesses: …' addresses 'what is good and bad about it' completely and contains neither word,
/// and a gate that demoted it would make every real gate less trustworthy. Coverage becomes
/// checkable when a deliverable can be CLAIMED by the task that served it." That is
/// <see cref="DeliverableLedger"/>, and this is the assembler it said the check should land with.
///
/// <see cref="MissionDeliverable.Subject"/> IS THEREFORE STILL UNREAD, and deliberately. Intake has
/// populated it since `.98` and nothing consumes it; its own doc comment offers it as "the topic
/// keywords a coverage check can look for in an answer", which is precisely the check `.98`
/// forbade in the same release. Wiring it here would have been the most natural-looking mistake
/// available and is recorded as declined rather than left to be rediscovered.
///
/// NO MODEL TOUCHES THIS. Sections are cut from recorded task results. A synthesised section could
/// drop an unanswered one and read as complete — the "two channels and the prose one wins" failure
/// ADR-004 exists to end, arriving where it would be least visible.
/// </summary>
public sealed record AssembledAnswer(IReadOnlyList<AnswerSection> Sections, bool Specified)
{
    /// <summary>The requests this mission did not answer.</summary>
    public IReadOnlyList<AnswerSection> Missing =>
        Sections.Where(s => !s.Answered).ToList();

    /// <summary>Every requested section has content. Meaningless for an unspecified mission, which
    /// declared no sections — see <see cref="AnswerCoverage.Applies"/>.</summary>
    public bool Covered => Sections.Count > 0 && Missing.Count == 0;

    /// <summary>
    /// The operator-facing answer.
    ///
    /// AN UNSPECIFIED MISSION RENDERS ITS CONTENT VERBATIM — byte for byte what the colony produced
    /// before this type existed. That is what makes one assembly path safe for every mission: the
    /// coding lane declares no deliverables, so it has exactly one section and no headings, and the
    /// answer it yields is the answer it always yielded.
    /// </summary>
    public string Render()
    {
        if (!Specified)
            return Sections.Count == 0 ? "" : Sections[0].Content;

        var parts = Sections.Select(s => s.State switch
        {
            AnswerSectionState.Answered => $"[{s.DeliverableId}] {s.Request}\n{s.Content}",

            AnswerSectionState.Empty =>
                $"[{s.DeliverableId}] {s.Request}\nNOT ANSWERED — the step that owned this request "
              + $"({string.Join(", ", s.ServingTaskIds)}) completed and produced no output.",

            _ => $"[{s.DeliverableId}] {s.Request}\nNOT ANSWERED — "
               + (s.ServingTaskIds.Count == 0
                   ? "no step in this mission was answerable for it."
                   : $"the step(s) that owned it ({string.Join(", ", s.ServingTaskIds)}) did not complete."),
        });

        var body = string.Join("\n\n", parts);
        var missing = Missing.Count;

        // THE SHORTFALL IS STATED IN THE ANSWER, not left to be counted. An answer whose gaps are
        // visible only by reading every section is one an operator skims past.
        return missing == 0
            ? body
            : body + $"\n\n{missing} of {Sections.Count} requested item(s) were not answered.";
    }

    /// <summary>
    /// Assemble.
    /// </summary>
    /// <param name="fallbackContent">What the mission produced when it declared no deliverables —
    /// the raw best-task output. Used ONLY for the unspecified single-section case; a specified
    /// mission never falls back to it, because falling back is the behaviour this type removes.</param>
    /// <param name="evidence">v0.3.8.109 — the mission's evidence rows, so each section can name the
    /// ones its own serving tasks left. Optional and null-safe: every caller that predates this
    /// parameter gets sections with an empty evidence list, which is exactly what they had.</param>
    public static AssembledAnswer Build(
        MissionSpecification? specification, IReadOnlyList<Task>? tasks, string? fallbackContent,
        IReadOnlyList<Anthill.SDK.Artifacts.Evidence>? evidence = null)
    {
        var all = tasks ?? Array.Empty<Task>();
        var ledger = DeliverableLedger.Build(specification, all);

        // Evidence indexed by the task that produced it. Rows with no task id are mission-level and
        // belong to no section — they are deliberately dropped here rather than credited to every
        // section, which would make "this section rests on something" true by default.
        var evidenceByTask = (evidence ?? Array.Empty<Anthill.SDK.Artifacts.Evidence>())
            .Where(e => !string.IsNullOrWhiteSpace(e.TaskId))
            .GroupBy(e => e.TaskId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Id).ToList(), StringComparer.Ordinal);

        // UNSPECIFIED: one section, no headings, content unchanged. Not a special case bolted on —
        // it is the honest reading of a mission that asked for one thing without saying so, and it
        // is what lets the assembler have a single path instead of a path and an escape hatch.
        if (ledger.Count == 0)
            return new AssembledAnswer(
                new[]
                {
                    new AnswerSection(
                        DeliverableId: "d1",
                        Request: specification?.OriginalRequest ?? "",
                        Content: fallbackContent ?? "",
                        ServingTaskIds: Array.Empty<string>(),
                        Claim: DeliverableClaim.Inferred,
                        State: string.IsNullOrWhiteSpace(fallbackContent)
                            ? AnswerSectionState.Unanswered
                            : AnswerSectionState.Answered,
                        // An unspecified mission has no serving tasks to join on, so it has no
                        // section-level evidence. Not "none was produced" — nothing declared a
                        // section for any of it to belong to.
                        EvidenceIds: Array.Empty<string>()),
                },
                Specified: false);

        var byId = all.ToDictionary(t => t.Id, t => t, StringComparer.Ordinal);

        var sections = ledger.Select(entry =>
        {
            // Only tasks that COMPLETED contribute text. A failed task's result is its failure
            // message, and pasting that under a heading reads as an answer to the question.
            var completed = entry.ServingTaskIds
                .Select(id => byId.GetValueOrDefault(id))
                .Where(t => t is not null && t.Status == TaskStatus.Complete)
                .Select(t => t!.Result ?? "")
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .ToList();

            var state = !entry.Served ? AnswerSectionState.Unanswered
                : completed.Count == 0 ? AnswerSectionState.Empty
                : AnswerSectionState.Answered;

            return new AnswerSection(
                entry.Id, entry.Request,
                string.Join("\n\n", completed),
                entry.ServingTaskIds,
                entry.Claim,
                state,
                entry.ServingTaskIds
                    .SelectMany(id => evidenceByTask.GetValueOrDefault(id) ?? new List<string>())
                    .Distinct(StringComparer.Ordinal)
                    .ToList());
        }).ToList();

        return new AssembledAnswer(sections, Specified: true);
    }

    /// <summary>Operator-visible projection, for the artifact and the record. Secret-free.</summary>
    public Dictionary<string, object?> Snapshot() => new()
    {
        ["specified"] = Specified,
        ["requested"] = Sections.Count,
        ["answered"] = Sections.Count(s => s.Answered),
        ["sections"] = Sections.Select(s => new Dictionary<string, object?>
        {
            ["id"] = s.DeliverableId,
            ["request"] = s.Request,
            ["claim"] = s.Claim,
            ["state"] = s.State,
            ["serving_task_ids"] = s.ServingTaskIds,
            ["evidence_ids"] = s.EvidenceIds,
            ["grounded"] = s.Grounded,
            ["content_chars"] = s.Content.Length,
        }).ToList(),
    };
}
