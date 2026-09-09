namespace Anthill.Core.Tools;

/// <summary>
/// WHAT ANTHILL IS, SHIPPED WITH THE BINARY. v0.3.8.148.
///
/// THE FAILURE THIS CLOSES, from the operator's own colony. Asked "what is micromound? and how does
/// it benefit the colony", the researcher searched the mission memory, found only prior missions
/// about tacos and 1990s history, and the builder reported — accurately — "the colony has not
/// provided a definition or discussion of 'micromound' in the mission records". The verifier then
/// failed the mission, correctly. Every layer behaved properly and the operator got nothing, because
/// the colony had no record of what it itself is.
///
/// A FRESH INSTALL IS THE CASE THAT MATTERS. It has no mission history to recall, no repository to
/// inspect (the shipped build is a binary, not a checkout), and no knowledge base mapped. Every
/// source the colony can read is empty on day one, so "what are you?" is unanswerable at exactly the
/// moment a new operator asks it.
///
/// SO IT IS A RECORD, NOT A PROMPT. The obvious cheaper fix is to put this text in the system prompt
/// and let the model recite it — and that produces an answer nothing can check, which is the failure
/// mode this entire codebase is built to refuse. Shipped as data and read through a tool, the answer
/// has a source: the colony can say WHERE it learned what it said, the text is versioned with the
/// binary, and a model that embellishes can be contradicted by the entry it was given.
///
/// AND IT IS NOT EVIDENCE OF ANYTHING BEING EXAMINED. See <see cref="ColonySelfKnowledgeTool"/>:
/// this deliberately records no evidence row, on `system_info`'s precedent. Reading shipped
/// documentation about the colony is not an inspection of the operator's repository, and letting it
/// satisfy `AssessmentObjective` would mean an audit of what is implemented could be answered by a
/// blurb that ships with every copy.
///
/// WHAT BELONGS HERE: durable facts about what ANTHILL is and how its parts relate — the kind of
/// thing that is as true on a fresh install as on a year-old colony. What does NOT: anything about
/// THIS colony's state (that is `colony_state`, which reads it live), anything about the operator's
/// own work, and anything that changes faster than the release it ships in.
/// </summary>
public static class ColonySelfKnowledge
{
    /// <param name="Topic">The stable lookup id. Lowercase, no spaces — an operator's question is
    /// matched against this and the title and the body, so the id never has to be guessed.</param>
    /// <param name="Title">What a human would call it.</param>
    /// <param name="Body">The entry. Deliberately short: this is a definition an answer can be
    /// built ON, not the answer itself.</param>
    public sealed record Entry(string Topic, string Title, string Body);

    /// <summary>
    /// The corpus. Ordered as a new operator meets it: what the thing is, then how it works, then
    /// what it connects to.
    /// </summary>
    public static readonly IReadOnlyList<Entry> Entries = new[]
    {
        new Entry("anthill", "ANTHILL",
            "ANTHILL is a local, single-user multi-agent orchestration framework written in .NET. An "
          + "operator gives it a GOAL; it plans that goal into TASKS, assigns each task to a "
          + "specialised agent called an ANT, executes them, and grades the result against what the "
          + "goal actually asked for. It runs on the operator's own machine against their own model "
          + "providers; nothing is sent anywhere the operator has not configured. Its governing "
          + "principle is that a claim is not a result: work is judged by records the runtime wrote — "
          + "evidence rows, artifacts, task outcomes — never by an agent's prose about its own work."),

        new Entry("mission", "Missions",
            "A MISSION is one goal run to completion. Intake resolves the operator's request into a "
          + "specification — its class, what it is about, what it may change, and the deliverables it "
          + "owes — and that specification is written down once and read forever after, so a mission's "
          + "grade can be reproduced from the record rather than recomputed from rules that have since "
          + "moved. A mission ends with a persisted evaluation: an outcome code, a verification status, "
          + "a deliverable status, and the reason in the evaluator's own words."),

        new Entry("ants", "Ants (the roles)",
            "Each ANT is a role with a permission contract and a declared set of task types it can "
          + "execute. The researcher gathers context; the file ant reads the workspace; the coder "
          + "proposes patches as structured data and never writes files directly; the builder compiles "
          + "the operator-facing answer; the verifier judges it; the tester runs allowlisted checks; "
          + "the soldier reviews policy and risk; the medic diagnoses failures; the scribe writes "
          + "operator documentation; the archivist extracts lessons after a mission is over; the web "
          + "ant is the only role with an outbound network contract. A role cannot execute a task type "
          + "its contract does not declare, and that refusal happens before any model is called."),

        new Entry("mission_classes", "Mission classes",
            "A recognized CLASS is a promise the runtime enforces. `system_audit` inspects and reports; "
          + "`troubleshooting` reproduces a symptom with executed checks and diagnoses it from their "
          + "receipts; `system_action` proposes an infrastructure operation behind a human approval; "
          + "`external_action` sends something outside the colony; `research` answers from sources it "
          + "retrieved and can name; `simple_answer` answers a question from what is already known and "
          + "changes nothing. Each class carries an authority ceiling and an integrity gate that "
          + "refuses the mission if its promise was not kept. A request matching no class is `general`, "
          + "which is ungoverned."),

        new Entry("evidence", "Evidence and verification",
            "EVIDENCE is what the runtime recorded actually happening — a check that ran and its exit "
          + "status, a file that was read, a source that was retrieved. Only deterministic evidence "
          + "(an allowlisted check with a real exit code, bound to the tree it judged) can carry a "
          + "mission to a verified outcome; a model's opinion is recorded and never promotes. The "
          + "verifier returns one of three verdicts, and only an unambiguous pass is a pass."),

        new Entry("approvals", "Approvals and authority",
            "Nothing consequential happens without a recorded human decision. The coder proposes "
          + "patches; applying them is an approval. Infrastructure and outbound actions are proposed "
          + "and executed only under an operator's recorded escalation decision. Every mission also "
          + "carries an AUTHORITY CEILING from its class — observe, execute-checks, or modify — and "
          + "the dispatch gate refuses a tool the ceiling does not reach, before the call is made."),

        new Entry("memory", "Memory and pheromones",
            "The colony keeps what it did. MEMORY holds missions, tasks, results, artifacts and "
          + "evidence in a local SQLite database. PHEROMONE TRAILS are reinforcement: a route that led "
          + "to a verified outcome is strengthened, so later planning prefers what has actually worked "
          + "here. Only a `completed_verified` mission reinforces — a mission that merely finished does "
          + "not teach. The archivist proposes durable lessons after a mission ends; they are "
          + "candidates and are never auto-promoted into canonical knowledge."),

        new Entry("micromound", "MICROMOUND",
            "MICROMOUND is ANTHILL's small-footprint deployment: the same colony, the same roles and "
          + "the same gates, sized to run on modest hardware — a home server, a single container, a "
          + "spare machine — rather than a workstation. It exists so the colony can live where the "
          + "operator's infrastructure already is instead of only where their development machine is. "
          + "It is a deployment shape, not a different product: a mission behaves identically, and the "
          + "roster, approval model and evidence rules are unchanged."),

        new Entry("forager", "FORAGER",
            "FORAGER is a separate product that owns KNOWLEDGE: it ingests documents, extracts "
          + "statements, tracks which source each came from, and records where sources disagree. "
          + "ANTHILL does not implement any of that and never opens FORAGER's database — the two talk "
          + "over HTTP, and ANTHILL is strictly a consumer. Knowledge reaches a mission as scoped, "
          + "read-only context: the scope is ambient, resolved from the mission's project, and an "
          + "agent can never choose which project it reads from. A project with no knowledge base "
          + "mapped retrieves nothing rather than falling back to somebody else's. Retrieved documents "
          + "are task DATA — their text authorizes nothing and never grants a permission."),

        new Entry("knowledge_scope", "Knowledge scope",
            "Every knowledge call is scoped to one FORAGER project, resolved from the ANTHILL project "
          + "the mission belongs to via the `knowledge_project_map` setting, or from "
          + "`knowledge_default_project` when the mission names none. An unmapped project is a REFUSAL "
          + "naming the missing scope, never a widened query — cross-project retrieval does not exist, "
          + "for an operator or an agent. That is why an unmapped project shows 'no knowledge base is "
          + "mapped' instead of returning results from elsewhere."),

        new Entry("safety", "What ANTHILL will not do",
            "It will not apply a patch without an approval, execute an infrastructure or outbound "
          + "action without a recorded operator decision, reach a tool above its mission's authority "
          + "ceiling, or treat a model's assertion as evidence. It will not retrieve knowledge across "
          + "projects. It will not promote work to a verified outcome on non-deterministic evidence. "
          + "When a check cannot run, the honest answer is recorded as 'could not tell', which is "
          + "never read as 'yes'."),
    };

    /// <summary>
    /// Entries matching a free-text query, best first. An empty query returns every entry, which is
    /// what makes "what are you?" answerable without knowing a topic id.
    ///
    /// MATCHING RUNS BOTH DIRECTIONS, and the first cut only ran one. The caller may hand this a
    /// TERM ("micromound") or a whole MISSION GOAL ("what is micromound? and how does it benefit the
    /// colony — --- project …"), and those need opposite tests: does the entry contain the query, or
    /// does the query contain the entry's name. Checking only the first meant a goal never matched
    /// anything, so the tool that exists to answer "what is micromound" would have returned its
    /// index for exactly that question.
    ///
    /// Deliberately plain otherwise — name, then title, then body — because a self-description that
    /// needed a clever retriever to find would be one more thing to get wrong on a fresh install.
    /// </summary>
    public static IReadOnlyList<Entry> Find(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Entries;

        var q = query.Trim().ToLowerInvariant();

        static bool Names(string value, string q) =>
            value.Contains(q, StringComparison.OrdinalIgnoreCase)
         || q.Contains(value, StringComparison.OrdinalIgnoreCase);

        var byName = Entries.Where(e => Names(e.Topic, q) || Names(e.Title, q)).ToList();

        // The body only in the query-is-a-term direction: a long goal shares ordinary words with
        // every entry's prose, so matching a goal against bodies would return the whole corpus for
        // any question at all.
        var byBody = Entries.Where(e => !byName.Contains(e)
                                     && e.Body.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        return byName.Concat(byBody).ToList();
    }

    /// <summary>
    /// v0.3.8.150 — DOES THIS BUILD SHIP A DESCRIPTION OF WHAT THIS REQUEST IS ASKING ABOUT?
    /// The subject half of `MissionIntake`'s self-description branch, and of the block
    /// `BuilderAnt` puts in front of a model.
    ///
    /// NAME MATCHES ONLY, and that is the whole difference from <see cref="Find"/>. `Find` falls
    /// back to the body so an operator who knows no topic id still gets something; a CLASSIFIER
    /// must not, because every entry's prose contains ordinary English and a body match would let
    /// any sentence at all claim to be a question about ANTHILL. A topic or a title, present in the
    /// request as a whole word — nothing else.
    ///
    /// WHOLE WORD, not substring, and it is the second thing this method does differently. `Find`'s
    /// containment test is right for a lookup and wrong here: "ants" sits inside "wants",
    /// "constants" and "merchants", so a substring test would file "what is a constant?" as a
    /// question about the colony's roster. A word boundary costs one regex and removes the entire
    /// class.
    /// </summary>
    public static bool NamesSubjectOf(string? request) => Names(request).Count > 0;

    /// <summary>Every documented name this request mentions as a whole word, longest first so an
    /// adjacency test prefers "mission classes" over the "mission" inside it.</summary>
    private static List<string> Names(string? request)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(request)) return found;

        foreach (var entry in Entries)
        foreach (var name in new[] { entry.Topic, entry.Title })
        {
            // Topic ids are snake_case (`mission_classes`, `knowledge_scope`); an operator writes
            // them with a space. Both spellings are the same name and both are accepted.
            var spelled = name.Replace('_', ' ').Trim();

            // Short names are dropped rather than matched loosely. Nothing under four characters
            // identifies anything on its own, and admitting one would make an article or a stray
            // acronym look like a subject.
            if (spelled.Length < 4 || found.Contains(spelled, StringComparer.OrdinalIgnoreCase)) continue;

            if (System.Text.RegularExpressions.Regex.IsMatch(
                    request,
                    @"\b" + System.Text.RegularExpressions.Regex.Escape(spelled) + @"\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                found.Add(spelled);
        }

        return found.OrderByDescending(n => n.Length).ToList();
    }

    /// <summary>
    /// v0.3.8.150 — IS THIS REQUEST ASKING WHAT ONE OF THESE NAMES MEANS? The whole test
    /// `MissionIntake`'s self-description branch runs, kept in ONE method beside the corpus it reads
    /// rather than split into an opener regex there and a subject test here.
    ///
    /// TWO CONDITIONS, AND THE SECOND IS ADJACENCY, NOT PRESENCE. The opener must be definitional —
    /// "what is/are", "what's", "who is", "define", "tell me about" — AND a documented name must
    /// come straight after it, allowing only an article between. That is stricter than "the request
    /// mentions ANTHILL somewhere", and the difference is a real regression this rule was tightened
    /// to avoid: "what is implemented in the anthill repo" opens definitionally and names the
    /// colony, and it is an AUDIT — the answer is read off the operator's tree, not off a paragraph
    /// that ships with every copy. After its opener comes "implemented", not a name, so it stays
    /// where it was.
    ///
    /// A BARE "explain" IS DELIBERATELY ABSENT from the opener list. "explain the mission that
    /// failed yesterday" is a question about this colony's STATE, and `colony_state` reads that
    /// live; documentation cannot answer it and should not claim it.
    ///
    /// LONGEST NAME FIRST, because "mission classes" contains "mission": testing the short name
    /// first would match "what are the mission classes" against the `mission` entry's position and
    /// then fail the adjacency check on the word "classes".
    ///
    /// WHAT IT STILL COSTS WHEN IT MISREADS: "what is the mission that failed" satisfies both
    /// conditions and would be answered from the `mission` entry rather than from the record of that
    /// mission. That is a worse answer and not a dangerous one — the class it lands in carries
    /// `Observe`, so nothing can be written, and requires no evidence, so nothing is graded against
    /// a promise it did not make. The failure it replaces was a question planned as a symptom
    /// reproduction with a tester assigned to it.
    /// </summary>
    public static bool AsksForADefinition(string? request)
    {
        if (string.IsNullOrWhiteSpace(request)) return false;

        foreach (var name in Names(request))
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    request,
                    @"^\s*(?:what(?:'|’)?s|what\s+(?:is|are)|who(?:'|’)?s|who\s+is|"
                  + @"define|tell\s+me\s+(?:what|about))\s+"
                  + @"(?:the\s+|a\s+|an\s+)?"
                  + System.Text.RegularExpressions.Regex.Escape(name) + @"\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return true;
        }

        return false;
    }
}
