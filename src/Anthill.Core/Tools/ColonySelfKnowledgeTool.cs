using Anthill.SDK.Tools;

namespace Anthill.Core.Tools;

/// <summary>
/// THE COLONY ANSWERS "WHAT ARE YOU?" FROM SOMETHING SHIPPED. v0.3.8.148.
///
/// See <see cref="ColonySelfKnowledge"/> for the failure this closes: asked what MICROMOUND is, a
/// real colony searched its mission memory, found tacos and 1990s history, and reported that it had
/// no record of the term. Every layer was correct and the operator got nothing.
///
/// WHY IT IS NOT `colony_state`. That tool reports what this colony IS RIGHT NOW — which roles are
/// executable, which tools registered, what has already run — and it reads live state to do it.
/// This reports what ANTHILL IS AT ALL, which is the same on every install and cannot be read from
/// anywhere because a fresh binary has no history, no checkout and no knowledge base. They are
/// different questions with different sources, and one tool answering both would mean every caller
/// of either got both.
///
/// IT RECORDS NO EVIDENCE, AND THAT IS THE LOAD-BEARING DECISION HERE.
///
/// The tempting move is to put it in `ToolEvidence.ObservationTools` so a mission that consults it
/// leaves an `inspection` row. That would be wrong in a way this repository has already paid for
/// once: `AssessmentObjective` requires `inspection` rows before an audit's conclusions can be
/// believed, and the whole point of that requirement is that the colony LOOKED AT THE OPERATOR'S
/// TREE. Admitting this tool to that lane would let an audit of what is implemented be satisfied by
/// a paragraph that ships with every copy — the same error `ToolEvidence` names for `web_search`,
/// wearing a different disguise.
///
/// `system_info` is the exact precedent and its exclusion is written in the same words: "reporting
/// the OS and the process is not evidence that anything about the colony was examined." Reading
/// shipped documentation is not evidence that anything was examined either. The call is still fully
/// recorded — `tool_called` and `tool_completed` have carried that since v1 — it is just not
/// evidence.
/// </summary>
public sealed class ColonySelfKnowledgeTool : ITool
{
    public const string ToolName = "colony_self_knowledge";

    public string Name => ToolName;

    public string Description =>
        "Read-only tool that returns ANTHILL's own shipped description of itself: what ANTHILL is, "
      + "how missions and ants work, what evidence and approvals mean, and what MICROMOUND and "
      + "FORAGER are. Use it to answer questions ABOUT ANTHILL ITSELF. Optional 'topic' argument "
      + "narrows the result; with no topic it returns every entry.";

    public string ParametersJson => """
        {"type":"object","properties":{"topic":{"type":"string","description":"Optional. A term to look up, e.g. 'micromound', 'forager', 'evidence'. Omit for everything."}}}
        """;

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        var topic = args?.GetValueOrDefault("topic")?.ToString();
        var found = ColonySelfKnowledge.Find(topic);

        // NO MATCH IS AN INDEX, NOT A FAILURE — and that is what lets the handler dispatch this
        // unconditionally instead of guessing from words in the goal whether the mission is about
        // ANTHILL.
        //
        // The alternative was a keyword trigger on the mission goal, and this session has watched
        // keyword triggers on the COMPOSED goal cause three separate defects: a recipe planned as a
        // patch, a briefing planned as fourteen tasks, a question routed to the web ant. A tool that
        // costs one in-memory lookup and returns two lines when it has nothing to say does not need
        // a trigger at all.
        //
        // The index also refuses the failure this tool exists to prevent: "no entry for X" alone
        // invites the model to supply one from its own weights, while naming what IS documented
        // turns a miss into a next step.
        if (found.Count == 0)
            return new ToolResult
            {
                ToolName = Name,
                Success = true,
                Output = "ANTHILL self-description: nothing here matches that request. This build "
                       + "documents itself on: "
                       + string.Join(", ", ColonySelfKnowledge.Entries.Select(e => e.Topic))
                       + ". Ask with one of those terms. Do NOT invent a definition for anything "
                       + "ANTHILL does not document — say plainly that it is not documented.",
            };

        var body = string.Join("\n\n", found.Select(e => $"## {e.Title} ({e.Topic})\n{e.Body}"));

        return new ToolResult
        {
            ToolName = Name,
            Success = true,
            // The source is stated IN the output, so an answer built on it can say where it came
            // from and a reader can tell a shipped definition from a model's recollection.
            Output = $"ANTHILL self-description (shipped with this build, {found.Count} entry(ies)):\n\n{body}",
        };
    }
}
