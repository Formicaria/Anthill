using System.Text.RegularExpressions;

namespace Anthill.Core.Missions;

/// <summary>
/// THE OPERATOR'S REQUEST, READ ONCE. v0.3.8.98.
///
/// Intake resolves the request into a <see cref="MissionSpecification"/> that every later layer
/// consumes instead of re-reading the goal for itself. It is deterministic and pure: no model, no
/// store, no clock. A model may later PROPOSE a specification — ADR-008 permits that explicitly —
/// but what is kept is decided here, because a classification a model can assert is a
/// classification a model can assert wrongly and nothing would catch it.
///
/// DIMENSIONS, NOT A LABEL. The class is DERIVED from independently resolved dimensions — intent,
/// targets, freshness, authority — rather than matched as a phrase. That is what makes "what is
/// implemented", "what is enabled right now" and "why is it broken" separable: the first two share
/// intent and differ only in target, and the third differs in intent and therefore in class. A
/// single keyword cannot express that, and every attempt to make it do so ends with an audit
/// answered from mission history because someone said the word "mission".
///
/// WHY LEXICAL SIGNALS ARE STILL HONEST HERE. Resolving a dimension from vocabulary is not the
/// same defect as picking a WORKER from vocabulary. The defect in `AntRegistry.ResolveWorker` is
/// that a substring decides a capability question — whether this worker can serve this task — which
/// the word cannot answer. Here the words are evidence about what the operator MEANT, which is the
/// only thing they can be evidence about, and several independent signals must agree before a class
/// is assigned. When they do not agree, this returns `general` and the colony behaves exactly as it
/// did before intake existed. Silence is the safe answer; a guess is not.
///
/// THE ASK, NOT THE TRANSCRIPT. Classification reads only the operator's own words — everything
/// above the first `--- ` section marker, where `ComposeMissionGoal` begins the standing context and
/// transcript. This is v0.3.8.96's hardest live lesson, and it is reused rather than reimplemented:
/// the UI gate's refusal prose entered a conversation's transcript and re-tripped the gate on every
/// later mission, a self-sustaining refusal seeded by the gate quoting itself. A classifier reading
/// colony narration would inherit exactly that.
/// </summary>
public static class MissionIntake
{
    // ---- dimension signals ---------------------------------------------------------------------
    //
    // Word-boundary matched, never bare substrings. v0.3.8.96 was the release where "ui" matched
    // inside "b·ui·ld" and refused a docs change; the same trap is waiting in "audit"/"auditing"
    // and in every short token here.

    private static readonly Regex AssessVerbs = new(
        @"\b(assess|audit|auditing|evaluate|evaluating|inspect|inspecting|review|reviewing|"
      + @"determine|determining|report|inventory|examine|examining|analy[sz]e|analy[sz]ing)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CapabilityQuestions = new(
        @"\b(capable|capabilit(?:y|ies)|abilit(?:y|ies)|what can it do|strengths?|weakness(?:es)?|"
      + @"limitations?|good and bad|shortcomings?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DiagnoseVerbs = new(
        @"\b(why|root cause|diagnose|diagnosing|troubleshoot|troubleshooting|debug|debugging|"
      + @"failing|broken|unhealthy|misbehav\w*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ChangeVerbs = new(
        @"\b(fix|repair|change|modify|add|remove|delete|refactor|implement|rename|update|migrate|"
        // v0.3.8.102: the unambiguous operational verbs — the system-action class's own
        // vocabulary. They join CHANGE because restarting a container changes the world exactly
        // as deploying does. Deliberately NOT `start`/`stop`: "start by assessing the colony"
        // must keep classifying as an audit, and a bare-verb ask like "stop the vm" resolves
        // `general` — behaving exactly as every change request did before this class existed —
        // which is recorded in §2c rather than bought with the audit lane's vocabulary. The
        // known cost of `restart` is recorded there too: a diagnostic question that mentions
        // restarting classifies as change and, with a service target, enters this lane — where
        // the worst outcome is a PROPOSAL an operator declines, never an action.
        // v0.3.8.103: the OUTBOUND verbs. They join CHANGE because posting to a third party
        // changes the world — irreversibly, and outside the operator's own machine, which is
        // more than a container restart can say. On their own they classify NOTHING: the
        // external class below also requires a named destination, so "send the report to the
        // team" stays `general` exactly as it did before this release. Deliberately NOT
        // `tell`/`share`/`report`, which are how operators ask for an ANSWER ("report on the
        // colony's health") and would drag the audit lane into a change intent.
      + @"deploy|install|write|restart|reboot|power[- ]cycle|post|publish|send|notify)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// v0.3.8.109 — the RESEARCH verbs: go and find out, from outside. Narrow, and narrower than the
    /// assessment list on purpose, because these words also appear in ordinary requests about the
    /// colony's own code — "look up the retry constant" is a file read, not a mission class.
    ///
    /// On their own they classify NOTHING. The research branch below also requires the World target
    /// AND the absence of every colony-side target, so a verb here reaching a repository question
    /// resolves exactly as it did before this class existed. That is the same discipline `.103` used
    /// for its outbound verbs, and for the same reason: a class admitted on a verb alone is a class
    /// that will eventually claim somebody else's mission.
    ///
    /// Deliberately NOT `investigate` or `look into`, which are how operators ask WHY something is
    /// broken — those belong to <see cref="DiagnoseVerbs"/>, and taking them here would let a
    /// diagnosis be answered by a web search.
    /// </summary>
    private static readonly Regex ResearchVerbs = new(
        @"\b(research|researching|look up|looking up|find out|finding out|survey|surveying|"
      + @"gather sources|cite|citing|what(?:'s| is| are) (?:the )?(?:latest|current|newest)|"
      + @"search (?:the )?(?:web|internet|online)|read up on)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RepositoryTargets = new(
        @"\b(repo|repository|codebase|code base|source|implementation|implemented|workflow|"
      + @"orchestration|architecture|colony|anthill)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RuntimeTargets = new(
        @"\b(runtime|running|enabled|configured|configuration|current(?:ly)?|now|today|state|"
      + @"health|healthy|live|active|actually (?:ran|run|used)|missions?|workers?|ants?|roles?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// v0.3.8.134 — IS THE OPERATOR ASKING A QUESTION? The `simple_answer` class's third condition,
    /// and it was added because the first two were not enough.
    ///
    /// The branch first read `Explain` intent AND no target, which is exactly the class's own
    /// description — "a question the colony can answer from what it already knows" — except for the
    /// word QUESTION, which was doing all the work and was nowhere in the code. `Explain` is the
    /// FALL-THROUGH intent: it means "no change verb, no diagnostic verb, no research verb, no
    /// assessment verb", which is a statement about what the request is NOT. Plenty of imperatives
    /// land there. "Document the deployment procedure in a runbook" is a creation request; "Exercise
    /// the coder and stop it while it works" is a colony instruction. Both were being admitted to a
    /// class whose gate forbids changing anything, so both would have been graded against a promise
    /// nobody made.
    ///
    /// SO THE SHAPE IS TESTED DIRECTLY, and narrowly: a question mark anywhere, or an interrogative
    /// or ask-me opener at the START of the request. The opener must be first because "the report
    /// on what can be done" contains `what` and asks nothing — the same word-position discipline
    /// `RoutingWords` exists for, applied to a sentence instead of a token.
    ///
    /// It is deliberately not a model call and not a cleverer parser. A request this misreads
    /// resolves `general` — ungraded, exactly as it did before this class existed — which is the
    /// cost this whole file is written to prefer over a confident misclassification.
    /// </summary>
    private static readonly Regex QuestionShape = new(
        @"\?|^\s*(?:who|what|when|where|why|how|which|whose|whom|can|could|should|would|will|"
      + @"do|does|did|is|are|was|were|am|explain|describe|define|tell me|teach me|"
      // v0.3.8.146 — THE ASK THAT IS NOT SHAPED LIKE A QUESTION, found in the operator's colony.
      //
      // "give me a briefing of 1990s historical events in the US" resolved `general`: Explain
      // intent, no target, and no interrogative opener. `general` is UNGOVERNED, so the request
      // reached the planner model — and whether it worked came down to whether that model's JSON
      // happened to parse. The same message, sent twice a minute apart, produced a clean two-task
      // answer once and a fourteen-task plan with a coder, patch proposals, testers and soldiers
      // the other time. That is the coin flip the operator experienced as "sometimes it works".
      //
      // THE OBJECT IS AN EXPLANATION, WHICH IS WHY THESE ARE SAFE AND A BARE "give me" IS NOT.
      // "give me a python script that parses CSV" names no target either, so a bare verb would
      // pull a genuine creation request into the class that refuses to create. Requiring the noun
      // keeps the boundary where `.134` put it: this class answers, it does not make things.
      + @"summari[sz]e|brief me|walk me through|help me understand|"
      + @"give me (?:an?\s+)?(?:brief|briefing|summary|overview|rundown|explanation|breakdown)|"
      + @"(?:an?\s+)?overview of)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// v0.3.8.145 — IS THE OPERATOR SAYING HELLO? The `simple_answer` class's fourth condition,
    /// found by driving the console as a first-time user: "hello" was not question-shaped, resolved
    /// `general`, and stopped the colony to ask permission to start a mission — which is the wall a
    /// new user hits before anything else. A greeting or an acknowledgement is answered from what is
    /// already known and changes nothing, which is exactly this class's promise.
    ///
    /// THE WHOLE MESSAGE MUST BE THE GREETING, anchored at both ends. `QuestionShape` may match a
    /// question mark anywhere because a question mark is a strong signal; a salutation is a weak
    /// one, and "hello, document the deployment procedure in a runbook" opens with it. Admitting that
    /// on the strength of its first word would put a creation request into a class whose ceiling
    /// forbids writing the runbook — the `.134` regression in a new coat. So a greeting followed by
    /// anything but a few closing words and punctuation is not a greeting here, and resolves
    /// `general` as it did before, which is the cost this file prefers over a confident misread.
    /// </summary>
    private static readonly Regex GreetingShape = new(
        @"^\s*(?:hi|hello|hey|yo|howdy|greetings|hiya|good\s+(?:morning|afternoon|evening|day)|"
      + @"thanks|thank\s+you|thx|ok(?:ay)?|cheers|ping|test(?:ing)?)"
      + @"(?:[\s,!.]+(?:there|all|everyone|team|again|back|a\s+lot|very\s+much|so\s+much))?"
      + @"[\s!.,]*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CurrentFreshness = new(
        @"\b(now|current(?:ly)?|today|at the moment|right now|present(?:ly)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// v0.3.8.102 — the SERVICE dimension, resolved at last: infrastructure nouns the infrastructure's
    /// own action catalog operates on. Deliberately narrow and concrete — a word here admits a
    /// CHANGE request into the class that proposes real operations, so "the build server is slow"
    /// must not enter on the strength of "server" alone unless the intent is also Change, and a
    /// repository-flavoured change ("fix the build in this repo") is excluded by the class
    /// derivation below rather than by this list guessing.
    /// </summary>
    private static readonly Regex ServiceTargets = new(
        @"\b(container|docker|compose|vm|virtual machine|proxmox|pve\w*|hyper-?v|vsphere|"
      + @"host|node|infrastructure|service|daemon|media[- ]server|plex|overseerr|uptime[- ]?kuma)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// v0.3.8.103 — the EXTERNAL dimension: a destination OUTSIDE the colony and outside the
    /// operator's own infrastructure. Narrow and concrete for the same reason
    /// <see cref="ServiceTargets"/> is, and for a sharper one: a word here admits a change request
    /// into the only lane that does something nothing in this repository can undo.
    ///
    /// A NAMED DESTINATION IS REQUIRED, which is what keeps the class honest. An operator saying
    /// "send the summary to the team" has named no endpoint, so there is nothing to resolve and
    /// nothing a human could approve — that request resolves `general`, exactly as it did before
    /// this class existed, rather than entering a lane whose whole premise is an approved target.
    /// </summary>
    private static readonly Regex ExternalTargets = new(
        @"\b(webhook|slack|discord|teams channel|pagerduty|opsgenie|endpoint|"
      + @"external api|third[- ]party)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// v0.3.8.154 — A URL IS A PLACE, NOT A DIRECTION, and it used to sit in
    /// <see cref="ExternalTargets"/> above on the reasoning that "a url is a destination".
    ///
    /// FROM THE OPERATOR'S COLONY. A long diagnostic mission — "Run an end-to-end diagnostic
    /// mission… demonstrate that EVERY currently enabled executable role performs meaningful work"
    /// — mentioned fetching `https://www.rfc-editor.org/rfc/rfc8259` to check JSON rules. The bare
    /// url set the External flag, the goal's ordinary construction verbs ("Build", "Create",
    /// "Produce") read as Change, and the mission was admitted to `external_action`: the class that
    /// promises something was SENT somewhere. `ExternalActionIntegrity` then refused it, correctly
    /// and unanswerably — "nothing was sent: no external destinations are configured, so 'JSON
    /// specification at https://…' names nothing this colony can reach". A test of the colony was
    /// graded against a promise to send a message nobody had asked it to send.
    ///
    /// `.109` WROTE THE RULE THAT SETTLES THIS: "World vs External is DIRECTION — External is where
    /// something GOES, World is where knowledge COMES FROM." A url on its own says only that a place
    /// exists; whether the colony is reading it or posting to it is said by the VERB. So a bare url
    /// is now a World target — somewhere outside the colony that can be read, which is exactly what
    /// `WorldTargets` is for — and it becomes a DESTINATION only when an outbound verb points at it.
    ///
    /// A NAMED destination still stands alone, above: writing "webhook" or "pagerduty" is naming
    /// somewhere to send, whatever the sentence around it does. That asymmetry is the whole content
    /// of this change — a proper noun for a delivery channel means direction; a scheme does not.
    /// </summary>
    private static readonly Regex Url = new(@"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The verbs that point AT a destination. A strict subset of <see cref="ChangeVerbs"/>'s
    /// outbound half, kept separate because Change is far broader: "build", "create" and "update"
    /// are Change and say nothing about sending anything anywhere.
    /// </summary>
    private static readonly Regex OutboundVerbs = new(
        @"\b(post|posting|publish|publishing|send|sending|notify|notifying|deliver|delivering|"
      + @"webhook|callback|push (?:to|it to))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// v0.3.8.109 — the WORLD dimension: somewhere outside the colony that can be READ.
    ///
    /// NO URL PATTERN IN THIS LIST, and v0.3.8.154 changed what that means rather than leaving a
    /// comment that had stopped being true. A bare url IS a World target now — it is added in
    /// `ResolveTargets` rather than here, because whether it is somewhere to read or somewhere to
    /// send depends on the verb beside it, which a vocabulary list cannot see. See `Url` above: the
    /// old reading admitted a diagnostic mission to `external_action` for mentioning an RFC.
    ///
    /// `sources` and `citations` are PLURAL on purpose. <see cref="RepositoryTargets"/> already
    /// claims the singular `source` — the source tree — and the two mean opposite things. The word
    /// boundary keeps them apart, which is subtle enough to be worth writing down rather than
    /// leaving for someone to rediscover by watching a repository question route to the web.
    /// </summary>
    private static readonly Regex WorldTargets = new(
        @"\b(web|internet|online|public(?:ly)? available|sources|citations|references|literature|"
      + @"papers|articles|publications|news|blogs?|upstream|vendors?|competitors?|market|"
      // Deliberately NOT "state of the art": `state` is a RuntimeTargets word and has been since
      // `.98`, so that phrase would set BOTH flags and the research branch would refuse the very
      // request it matched. A vocabulary entry that can never win is the declaration-reaching-nobody
      // defect at the smallest possible scale, and it is left out rather than left in to look
      // thorough.
      + @"industry|prior art)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Every target the colony can actually go and look at. v0.3.8.109.
    ///
    /// Named because the troubleshooting branch needs it and because naming it is what keeps that
    /// branch's narrowing behaviour-preserving: before <see cref="MissionTargets.World"/> existed
    /// this set WAS "any target", so every request that classified as troubleshooting before this
    /// release still does.
    /// </summary>
    private const MissionTargets InspectableTargets =
        MissionTargets.Repository | MissionTargets.Runtime | MissionTargets.Project
      | MissionTargets.Service | MissionTargets.External;

    /// <summary>
    /// Capability ids a system audit requires. Workers declare against these; the resolver matches
    /// them. Named for what must be POSSIBLE, not for who does it — naming a worker here would put
    /// the selection back in the specification and make the resolver ceremonial.
    /// </summary>
    /// <remarks>
    /// ORDERED, and the order is the tie-break `AntRegistry.ResolveByCapability` applies: the first
    /// capability a role can serve decides its worker. `inspect_repository` leads because a plan's
    /// unqualified research step is about what is implemented; the runtime half is reached by a task
    /// that names <see cref="WorkerCapabilities.InspectRuntimeState"/> as its own requirement, which
    /// is what `Planner.EnsureClassCoverage` gives the step it inserts for exactly that purpose.
    ///
    /// `inspect_runtime_state` was held OUT of this list at first, while nothing served it —
    /// requiring a capability no worker declares would be a declaration reaching nobody, this
    /// repository's recurring defect. `researcher.runtime_researcher` and the `colony_state` tool
    /// landed together, so the requirement lands with them.
    /// </remarks>
    public static readonly IReadOnlyList<string> SystemAuditCapabilities = new[]
    {
        WorkerCapabilities.InspectRepository,
        WorkerCapabilities.InspectRuntimeState,
        WorkerCapabilities.CompileResult,
        WorkerCapabilities.VerifyResultCompleteness,
    };

    /// <summary>
    /// Capability ids a troubleshooting mission requires. v0.3.8.101. `execute_diagnostic_checks`
    /// leads for the same reason `inspect_repository` leads the audit list: the class's defining
    /// step resolves first. Declared by the tester's workers IN THE SAME RELEASE — a capability
    /// nothing serves is a declaration reaching nobody, this repository's recurring defect, and the
    /// audit list's own remark is the precedent: the requirement lands with its worker.
    /// </summary>
    public static readonly IReadOnlyList<string> TroubleshootingCapabilities = new[]
    {
        WorkerCapabilities.ExecuteDiagnosticChecks,
        WorkerCapabilities.InspectRepository,
        WorkerCapabilities.CompileResult,
        WorkerCapabilities.VerifyResultCompleteness,
    };

    /// <summary>
    /// Capability ids a system-action mission requires. v0.3.8.102. `propose_system_action` leads
    /// for the standing reason: the class's defining step resolves first, and the capability is
    /// deliberately named for PROPOSING — what the worker may do alone — not for executing, which
    /// no capability grants because execution is the operator's recorded decision. Declared by the
    /// system operator's worker in the same release, per the standing precedent.
    /// </summary>
    public static readonly IReadOnlyList<string> SystemActionCapabilities = new[]
    {
        WorkerCapabilities.ProposeSystemAction,
        WorkerCapabilities.CompileResult,
        WorkerCapabilities.VerifyResultCompleteness,
    };

    /// <summary>
    /// Capability ids an external-action mission requires. v0.3.8.103. `propose_external_action`
    /// leads for the same reason `propose_system_action` leads its list: order is the tie-break
    /// `AntRegistry.ResolveByCapability` applies, and the class's DEFINING capability must be the
    /// one a researcher-shaped task cannot accidentally claim first.
    /// </summary>
    public static readonly IReadOnlyList<string> ExternalActionCapabilities = new[]
    {
        WorkerCapabilities.ProposeExternalAction,
        WorkerCapabilities.CompileResult,
        WorkerCapabilities.VerifyResultCompleteness,
    };

    /// <summary>
    /// Capability ids a research mission requires. v0.3.8.109. `retrieve_sources` leads for the
    /// standing reason — order is the tie-break <c>AntRegistry.ResolveByCapability</c> applies, and
    /// the class's DEFINING capability must be the one a researcher-shaped task cannot claim first.
    ///
    /// Declared by the web ant's own workers in this same release. Those two workers have carried no
    /// capability since `.98` gave workers capabilities at all — they were the outward read surface
    /// with nothing able to ask for them, which is why every research-flavoured request until now was
    /// served by whichever worker a keyword happened to match.
    /// </summary>
    public static readonly IReadOnlyList<string> ResearchCapabilities = new[]
    {
        WorkerCapabilities.RetrieveSources,
        WorkerCapabilities.CompileResult,
        WorkerCapabilities.VerifyResultCompleteness,
    };

    /// <summary>
    /// Capability ids a simple-answer mission requires. v0.3.8.134, and the SHORTEST list here by
    /// design rather than by omission: every sibling leads with the capability that defines its
    /// class — inspect, execute, propose, retrieve — and this class's defining property is that it
    /// needs none of them. What remains is what any specified mission owes: compile an answer, and
    /// check it answered what was asked.
    ///
    /// Naming a `answer_from_knowledge` capability here was considered and rejected: no worker
    /// declares it, and requiring a capability nothing serves is a declaration reaching nobody —
    /// this repository's recurring defect, named in the audit list's own remark. The constraint this
    /// class actually carries is its authority ceiling, which is enforced at dispatch, not a
    /// capability nobody can answer.
    /// </summary>
    public static readonly IReadOnlyList<string> SimpleAnswerCapabilities = new[]
    {
        WorkerCapabilities.CompileResult,
        WorkerCapabilities.VerifyResultCompleteness,
    };

    /// <summary>
    /// Resolve the specification. Never throws, and never returns null: a request it cannot
    /// classify becomes <see cref="MissionSpecification.General"/>, which constrains nothing.
    /// </summary>
    public static MissionSpecification Resolve(string? goal)
    {
        var request = OperatorAskOnly(goal ?? "").Trim();
        if (request.Length == 0) return MissionSpecification.General(request);

        var intent = ResolveIntent(request);
        var targets = ResolveTargets(request);
        var freshness = CurrentFreshness.IsMatch(request) ? MissionFreshness.Current : MissionFreshness.Historical;

        // ── v0.3.8.150 — "WHAT IS X" ABOUT THE COLONY ITSELF, AND WHY IT IS THE FIRST BRANCH ──────
        //
        // From the operator's colony, six consecutive chat messages, and the pattern is exact:
        //
        //     "what is micromound?"                                        → simple_answer
        //     "what is the anthill colony?"                                → general
        //     "what is forager? and how does it integrate into the …"      → general
        //     "what is micromound? and why does it integrate into Anthill" → TROUBLESHOOTING
        //
        // The one that worked is the one that named nothing. ASKING ABOUT ANTHILL GAVE THE QUESTION
        // A TARGET, and a target disqualifies `simple_answer` — which sits LAST, after every branch
        // that claims a request by something the colony can DO about it. So a question about the
        // colony was read as an instruction to go and look at the colony; "why does it integrate"
        // read as a symptom, and the mission planned a TESTER task titled "Reproduce the reported
        // symptom" for a question about a product feature. `DiagnosisIntegrity` then refused it for
        // having no `command_check` receipt — correctly, against a promise nobody had made.
        //
        // A DEFINITION IS NOT AN INSPECTION. When someone asks what a thing IS and this build ships
        // a written description of that thing, the answer is a RECORD: the same on every install,
        // readable with no checkout, no history and no knowledge base. There is nothing to inspect,
        // nothing to reproduce and nothing to retrieve, which is exactly `simple_answer`'s promise.
        // So this runs BEFORE the branches that would claim the request for a target — by the time a
        // target has been read, the question has already been turned into a job.
        //
        // THE WHOLE TEST LIVES BESIDE THE CORPUS IT READS (`ColonySelfKnowledge.AsksForADefinition`)
        // rather than as an opener regex here and a subject lookup there. Splitting it would be two
        // halves of one rule in two files, which is the drift this repository names most often — and
        // the adjacency requirement, which is the part that keeps "what is implemented in the
        // anthill repo" an AUDIT, only makes sense with both halves in view.
        // AND THREE THINGS TAKE IT STRAIGHT BACK OUT, every one of them found by the suite on this
        // release's first run — the fixture being "What is the Anthill colony capable of now? What
        // is good and bad about its workflow? Does it hit the proper ants it needs to?", which opens
        // definitionally, names the colony, and is an AUDIT in every other respect.
        //
        // The rule they express is one rule: DOCUMENTATION ANSWERS WHAT A THING IS, PERMANENTLY.
        // It cannot answer what this colony can do, how well it does it, or what is true of it right
        // now — those are read live, off the operator's own runtime and tree, and a paragraph that
        // ships with every copy would answer them identically on a colony where they are false.
        //
        //   CapabilityQuestions  "capable", "strengths", "weaknesses", "limitations", "good and bad"
        //                        — an assessment OF this colony, which is `system_audit`'s subject.
        //   AssessVerbs          "audit", "evaluate", "review", "analyse" — the request says outright
        //                        that it wants something examined.
        //   Freshness == Current "now", "currently", "right now", "today" — the class's own branch
        //                        below already records why a shipped answer may not claim freshness
        //                        it has no retrieval to back. Asking about NOW is asking about state.
        if (Tools.ColonySelfKnowledge.AsksForADefinition(request)
            && !CapabilityQuestions.IsMatch(request)
            && !AssessVerbs.IsMatch(request)
            && freshness != MissionFreshness.Current)
            return new MissionSpecification
            {
                OriginalRequest = request,
                MissionClass = MissionSpecification.SimpleAnswerClass,
                Intent = MissionIntent.Explain,
                // NONE, and it is FORCED rather than resolved — that is the entire content of this
                // branch. `ResolveTargets` read a target and was right to: the request does name
                // ANTHILL. What it cannot see is that the request asks what that name MEANS rather
                // than asking the colony to go and look at the thing, and a target is the colony's
                // word for somewhere to go and look.
                Targets = MissionTargets.None,
                Freshness = freshness,
                // OBSERVE, for the same reason the class's own branch below gives: not because the
                // answer inspects anything, but because it changes nothing — and the gate reads this
                // ceiling at dispatch, so a plan assembled any other way still cannot write.
                Authority = MissionAuthority.Observe,
                Deliverables = ResolveDeliverables(request),
                RequiredCapabilities = SimpleAnswerCapabilities,
                RequiredEvidence = Array.Empty<string>(),
            };

        // THE CLASS IS DERIVED, and only when the dimensions agree on something this release can
        // actually serve. Assessment of the repository and/or the runtime is a system audit;
        // diagnosis of a symptom about a nameable target is troubleshooting (v0.3.8.101); a CHANGE
        // to a SERVICE target is a system action (v0.3.8.102). Change intent aimed anywhere else —
        // including the repository, which is the coding lane every prior release protects —
        // resolves to `general` exactly as before.
        //
        // v0.3.8.102 — THE FIRST CHANGE CLASS, and the narrowest derivation in this method: it
        // requires the Service flag SPECIFICALLY, not merely "any target", because admitting a
        // change request here is admitting it to the lane that proposes real operations. A change
        // request that names both the repository and a service ("fix the deploy script and restart
        // the container") still enters — the class only PROPOSES, execution is human-gated, and
        // the repository half of such an ask correctly fails the class gate rather than silently
        // becoming a patch. Misclassification cost is a proposal card an operator declines, never
        // an action.
        // v0.3.8.103 — THE OUTBOUND CLASS, AND IT IS TESTED BEFORE THE SERVICE ONE. That order is
        // the release's sharpest classification decision, so it is stated rather than implied.
        //
        // A request may name both an external destination and a service: "notify the team's webhook
        // that the media-server container restarted". Nothing is being done to the container there —
        // it is the SUBJECT of a message, not the object of an action. The VERB says what is being
        // done and the destination says to whom, so letting the service noun win would silently turn
        // a notification into an infrastructure proposal. That is the worst direction for this to be
        // wrong in: `.102`'s lane proposes real operations, and an operator who asked to send a
        // message would be shown a restart to approve.
        //
        // The reverse misread costs less and is still guarded: a genuine infrastructure ask has no
        // external destination to match, so "restart the media-server container on pve1" cannot
        // reach this branch at all.
        if (intent == MissionIntent.Change && targets.HasFlag(MissionTargets.External))
            return new MissionSpecification
            {
                OriginalRequest = request,
                MissionClass = MissionSpecification.ExternalActionClass,
                Intent = MissionIntent.Change,
                Targets = targets,
                Freshness = MissionFreshness.Current,
                // MODIFY, and the second class to carry it — but what Modify grants here is not what
                // it grants `.102`. There, the executor's paired action reverses the operation. Here
                // nothing reverses anything: the ceiling admits the mission to a lane whose execute
                // tool is irreversible, which is why `MissionAuthorityGate` reads the ceiling at
                // dispatch for the first time in this release instead of trusting that it was set.
                Authority = MissionAuthority.Modify,
                Deliverables = ResolveDeliverables(request),
                RequiredCapabilities = ExternalActionCapabilities,
                // Record-keyed like `.102`, not evidence-keyed: the send's own pieces — resolved
                // target, executed target, receipt, approver — are the receipts, and
                // `ExternalActionIntegrity` refuses each absence, and each MISMATCH, by name.
                RequiredEvidence = Array.Empty<string>(),
            };

        if (intent == MissionIntent.Change && targets.HasFlag(MissionTargets.Service))
            return new MissionSpecification
            {
                OriginalRequest = request,
                MissionClass = MissionSpecification.SystemActionClass,
                Intent = MissionIntent.Change,
                Targets = targets,
                Freshness = MissionFreshness.Current,
                // MODIFY — the first class to carry it, and what it grants is exactly the infrastructure
                // action catalog behind its own approval gate: the model proposes, the operator's
                // recorded escalation decision executes, the executor's TOCTOU/rollback/kill-switch
                // gates stand underneath. Never a shell, never the patch lane.
                Authority = MissionAuthority.Modify,
                Deliverables = ResolveDeliverables(request),
                RequiredCapabilities = SystemActionCapabilities,
                // The gate is record-keyed on the `system_operation` artifact rather than on an
                // evidence kind: the operation's own pieces are the receipts, and
                // `OperationIntegrity` refuses each absence by name.
                RequiredEvidence = Array.Empty<string>(),
            };

        // v0.3.8.109 — THE RESEARCH CLASS, placed after the two Change branches and before
        // troubleshooting, which keeps this method's standing order: descending consequence. It
        // could sit anywhere among the read-only branches without changing a single answer, because
        // the three are disjoint by construction — this one requires the Research intent AND a World
        // target AND no colony-side target at all. That is stated rather than relied on silently:
        // an ordering that happens to be safe today is one a later verb can quietly break.
        //
        // THE THIRD CONDITION IS THE CLASS'S HONESTY. A request naming both worlds — "compare our
        // retry policy against what the upstream project does" — is NOT admitted here. Its answer
        // rests half on an inspection and half on a retrieval, and this class's gate can only speak
        // for the retrieval half; admitting it would let a mission pass a research gate while the
        // repository half of the question went unexamined. Such a request resolves exactly as it did
        // before this release, which is the outcome §2c records rather than the one this branch
        // guesses at.
        if (intent == MissionIntent.Research
            && targets.HasFlag(MissionTargets.World)
            && (targets & InspectableTargets) == MissionTargets.None)
            return new MissionSpecification
            {
                OriginalRequest = request,
                MissionClass = MissionSpecification.ResearchClass,
                Intent = MissionIntent.Research,
                Targets = MissionTargets.World,
                // A retrieval is a claim about what a page says NOW. A research answer assembled
                // from what the colony read last month is an archive, and it should say so rather
                // than be presented as the current state of anything.
                Freshness = MissionFreshness.Current,
                // OBSERVE. Reading pages changes nothing, and the outbound call is the web ant's own
                // long-standing permission contract rather than anything this ceiling grants.
                Authority = MissionAuthority.Observe,
                Deliverables = ResolveDeliverables(request),
                RequiredCapabilities = ResearchCapabilities,
                // EVIDENCE-KEYED, like the audit and the diagnosis and unlike the two action
                // classes. The class's promise is that the answer rests on something retrieved, and
                // `source_retrieval` is the row a retrieval leaves — spelled as the store spells it,
                // which is the audit class's own hard-won lesson kept.
                RequiredEvidence = new[] { Anthill.SDK.Artifacts.EvidenceKinds.SourceRetrieval },
            };

        // v0.3.8.109 — AND A PURELY OUTWARD "WHY" IS NOT A TROUBLESHOOTING MISSION. This condition
        // read `targets != MissionTargets.None` until the World target existed, and that was exactly
        // right while every target was something the colony could execute a check against. "Why is
        // the market moving" would now enter the class whose entire premise is a reproduction, and
        // the colony cannot re-run the world. Behaviour-preserving for every request that predates
        // this release: `InspectableTargets` is what "any target" meant when it was written.
        if (intent == MissionIntent.Diagnose && (targets & InspectableTargets) != MissionTargets.None)
            return new MissionSpecification
            {
                OriginalRequest = request,
                MissionClass = MissionSpecification.TroubleshootingClass,
                Intent = MissionIntent.Diagnose,
                Targets = targets | MissionTargets.Repository,
                // A symptom is a claim about NOW. "Why is it failing" answered from last month's
                // records is an archaeology report wearing a diagnosis's clothes.
                Freshness = MissionFreshness.Current,
                // EXECUTE CHECKS — the first class to carry this authority, and exactly this much:
                // allowlisted, read-only-in-effect check commands whose exit statuses become the
                // receipts a diagnosis rests on. Never Modify: a diagnosis that repairs has left
                // the class (ADR-008), and the repair lanes keep their own gates.
                Authority = MissionAuthority.ExecuteChecks,
                Deliverables = ResolveDeliverables(request),
                RequiredCapabilities = TroubleshootingCapabilities,
                // Spelled as the store spells it (the audit class's own lesson, kept): these are
                // the rows `ToolEvidence` writes when `run_allowlisted_check` dispatches, and the
                // rows `DiagnosisIntegrity` resolves receipts against.
                RequiredEvidence = new[] { Anthill.SDK.Artifacts.EvidenceKinds.CommandCheck },
            };

        // v0.3.8.134 — THE ANSWER CLASS, AND IT IS THE LAST BRANCH BEFORE THE EXIT, which is the
        // only place it could go. Every branch above claims a request by something the colony can
        // DO about it; this one claims what is left over when none of them wanted it and there is
        // still a question on the table.
        //
        // BOTH CONDITIONS ARE NARROW ON PURPOSE. `Explain` is the fall-through intent — no change
        // verb, no diagnostic verb, no research verb, no assessment verb — and `MissionTargets.None`
        // means the request named neither the repository, the runtime, a service, an external
        // destination nor the world. A request satisfying both is one the colony has nothing to
        // inspect for and nowhere to go and read: it is answered from what the model knows or not at
        // all, and saying so is more honest than pretending the answer was established.
        //
        // WHAT THIS TAKES OUT OF `general`, and it is the whole reason the branch exists. Before
        // this release such a request was ungoverned: no deliverable, no evidence, and no authority
        // ceiling, so `MissionAuthorityGate` had nothing to enforce and the dynamic planner's
        // file-change rule could turn "how do you make tacos" into a proposed patch with no layer
        // able to refuse it. The class does not make the planner smarter. It makes the refusal
        // possible, which is the difference between a prompt and a guarantee.
        //
        // `general` DOES NOT GO AWAY. A request that names a target but matches no class — an
        // Explain about the repository, a Change aimed at the tree — still lands there, unchanged
        // and ungraded, exactly as it did before.
        //
        // AND THE THIRD CONDITION IS THE ONE THE FIRST CUT OF THIS RELEASE LEFT OUT, so it is stated
        // rather than folded into the two above. `Explain` is the FALL-THROUGH intent — it says only
        // that no other verb list claimed the request — and the class's description says QUESTION.
        // Without `QuestionShape`, "Document the deployment procedure in a runbook" and "Exercise the
        // coder and stop it while it works" both entered a class whose gate forbids changing
        // anything, and were graded against a promise neither of them made. The suite caught it,
        // which is the argument for classifying against real fixtures rather than against the two
        // sentences the class was designed around.
        // v0.3.8.145 — or a whole-message greeting/acknowledgement (`GreetingShape`), which is
        // answered from what is already known just as a question is, and changes nothing.
        if (intent == MissionIntent.Explain
            && targets == MissionTargets.None
            && (QuestionShape.IsMatch(request) || GreetingShape.IsMatch(request)))
            return new MissionSpecification
            {
                OriginalRequest = request,
                MissionClass = MissionSpecification.SimpleAnswerClass,
                Intent = MissionIntent.Explain,
                Targets = MissionTargets.None,
                // HISTORICAL, and it is the resolved value rather than a forced one. Nothing here is
                // a claim about now: an answer from what is already known is as current as what is
                // known, and stamping it `Current` would be the class asserting a freshness it has
                // no retrieval to back. A request that explicitly asks for the latest something is
                // matched by `CurrentFreshness` and keeps that reading — but it is then a question
                // about the world, and the research branch above has already claimed it.
                Freshness = freshness,
                // OBSERVE. Not because the answer inspects something, but because it changes
                // nothing — and `MissionAuthorityGate` reads this ceiling at dispatch, so
                // `apply_patch`, `write_text_file` and `shell_command` are refused by the runtime
                // rather than merely absent from a plan.
                Authority = MissionAuthority.Observe,
                Deliverables = ResolveDeliverables(request),
                RequiredCapabilities = SimpleAnswerCapabilities,
                // NONE, and this class is the only one in the list that requires none. Its promise
                // is that the answer rests on nothing retrieved and nothing inspected; requiring an
                // evidence kind would be demanding a receipt for a thing the class exists to say
                // did not happen. `AnswerIntegrity` grades what it can grade instead: that an answer
                // exists, and that nothing was changed to produce it.
                RequiredEvidence = Array.Empty<string>(),
            };

        if (intent != MissionIntent.Assess || targets == MissionTargets.None)
            return MissionSpecification.General(request);

        return new MissionSpecification
        {
            OriginalRequest = request,
            MissionClass = MissionSpecification.SystemAuditClass,
            Intent = MissionIntent.Assess,
            // An audit of "the colony" is about both what is implemented and what is running; when
            // the request names only one, the other is not excluded — reading the repository to
            // answer a runtime question is cheap and reading neither is the failure being removed.
            Targets = targets | MissionTargets.Repository,
            Freshness = freshness,
            // READ-ONLY, always, at this class. Assessment that modifies is not assessment, and an
            // audit that discovers a fault reports it as a finding — never escalates itself into a
            // repair. That boundary is ADR-008's, restated where it is enforced.
            Authority = MissionAuthority.Observe,
            Deliverables = ResolveDeliverables(request),
            RequiredCapabilities = SystemAuditCapabilities,
            // The EVIDENCE KIND, spelled as the store spells it, because `AssessmentObjective`
            // looks for exactly these rows. The first draft said "repository_inspection", which
            // matches nothing the store ever writes — a requirement no producer could satisfy and
            // no consumer could check, which is a decoration, not a contract.
            RequiredEvidence = new[] { Anthill.SDK.Artifacts.EvidenceKinds.Inspection },
        };
    }

    /// <summary>
    /// Intent, resolved by precedence rather than by first match: change outranks diagnose outranks
    /// assess. A request that says "explain why it is broken and fix it" is a change mission that
    /// happens to contain assessment words, and treating it as an audit because "explain" appeared
    /// would authorize the wrong thing — the one direction this must never be wrong in.
    /// </summary>
    private static MissionIntent ResolveIntent(string request)
    {
        if (ChangeVerbs.IsMatch(request)) return MissionIntent.Change;
        if (DiagnoseVerbs.IsMatch(request)) return MissionIntent.Diagnose;
        // v0.3.8.109 — RESEARCH SITS BELOW DIAGNOSE AND ABOVE ASSESS, and both placements are
        // decisions rather than an ordering that fell out.
        //
        // Below diagnose: "research why the deploy keeps failing" is a diagnosis that used the word
        // research. Its answer must rest on checks the colony ran, and letting the research verb win
        // would route a reproducible question to a web search — the worst direction for this to be
        // wrong in, because the resulting answer would be fluent and unfalsifiable.
        //
        // Above assess: "research what the current best practice is" contains no assessment verb
        // today, but `analyse` and `review` are in that list and both appear in research asks. The
        // reverse order would let an outward question be classified as an audit of nothing, which is
        // the misread `.98` exists to prevent, pointed the other way.
        if (ResearchVerbs.IsMatch(request)) return MissionIntent.Research;
        if (AssessVerbs.IsMatch(request) || CapabilityQuestions.IsMatch(request)) return MissionIntent.Assess;
        return MissionIntent.Explain;
    }

    private static MissionTargets ResolveTargets(string request)
    {
        var targets = MissionTargets.None;
        if (RepositoryTargets.IsMatch(request)) targets |= MissionTargets.Repository;
        if (RuntimeTargets.IsMatch(request)) targets |= MissionTargets.Runtime;
        // v0.3.8.102: the Service flag existed since `.98` with nothing resolving it — a dimension
        // declared and reaching nobody, this repository's named defect, closed the release the
        // first class needs it.
        if (ServiceTargets.IsMatch(request)) targets |= MissionTargets.Service;
        // v0.3.8.103 — the External flag, declared at `.103` and resolved in the same release
        // that needs it. `.102` recorded what the other order costs: the Service flag existed
        // from `.98` with nothing resolving it, a dimension reaching nobody.
        if (ExternalTargets.IsMatch(request)) targets |= MissionTargets.External;

        // v0.3.8.154 — a url is a DESTINATION only when something is being sent to it, and a place
        // to READ otherwise. See `Url` above for the mission this was found in: a diagnostic test
        // that mentioned fetching an RFC was admitted to the class that promises a delivery, and
        // then refused for not having made one.
        if (Url.IsMatch(request))
            targets |= OutboundVerbs.IsMatch(request) ? MissionTargets.External : MissionTargets.World;
        // v0.3.8.109 — the World flag, declared and resolved in the release that needs it, per the
        // precedent `.102` set after the Service flag spent four releases reaching nobody.
        if (WorldTargets.IsMatch(request)) targets |= MissionTargets.World;
        return targets;
    }

    /// <summary>
    /// The deliverables, one per thing the operator actually asked for.
    ///
    /// Split on question marks first, because a multi-question request is the case that fails today
    /// — three questions asked, one answered, and the mission reported complete. A request with no
    /// question marks yields one deliverable: the whole ask. Ids are positional (`d1`, `d2`) so that
    /// rewording a question does not change what it is called.
    /// </summary>
    private static IReadOnlyList<MissionDeliverable> ResolveDeliverables(string request)
    {
        var parts = request.Split('?', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        // No question marks: the request is one deliverable, stated as the operator stated it.
        if (parts.Count <= 1)
            return new[] { new MissionDeliverable("d1", request, SubjectWords(request)) };

        return parts.Select((p, i) =>
            new MissionDeliverable($"d{i + 1}", p.EndsWith('?') ? p : p + "?", SubjectWords(p)))
            .ToList();
    }

    /// <summary>
    /// The content words of one deliverable — what a coverage check looks for in an answer.
    ///
    /// Stop words removed and nothing stemmed: this feeds a presence check, and a subject list that
    /// included "what" and "the" would be satisfied by any English sentence, which is precisely the
    /// plausible-prose failure the coverage gate exists to catch.
    /// </summary>
    private static IReadOnlyList<string> SubjectWords(string text)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "what","is","the","a","an","of","and","or","its","it","this","that","are","was","were",
            "do","does","did","to","for","in","on","at","by","with","about","now","you","your",
            "can","could","should","would","if","then","than","from","be","been","has","have",
            "we","our","us","i","me","my","there","their","them","they","he","she","not","no",
        };

        return Regex.Matches(text.ToLowerInvariant(), @"\b[a-z][a-z-]{2,}\b")
            .Select(m => m.Value)
            .Where(w => !stop.Contains(w))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToList();
    }

    /// <summary>
    /// The operator's own words: everything before the first `--- ` section marker.
    ///
    /// Duplicated from <c>UiChangeGate.OperatorAskOnly</c> only in spelling — the rule is one rule,
    /// and it is stated in both places because Agents and Missions cannot reference each other
    /// without a dependency neither wants. `IntakeAndUiGate_ReadTheSameOperatorAsk` holds the two
    /// implementations to identical behaviour, so a fix to one that misses the other fails.
    /// </summary>
    internal static string OperatorAskOnly(string goal)
    {
        var marker = goal.IndexOf("\n--- ", StringComparison.Ordinal);
        return marker >= 0 ? goal[..marker] : goal;
    }
}

/// <summary>
/// The capability vocabulary workers declare and specifications require. v0.3.8.98.
///
/// A closed set of ids, in one place, because the alternative is two spellings of the same
/// capability that never match — the failure `AntEvidenceKinds` was created to end for evidence
/// kinds, applied here to capabilities before it can happen.
/// </summary>
public static class WorkerCapabilities
{
    /// <summary>Read the source tree: files, docs, configuration, structure.</summary>
    public const string InspectRepository = "inspect_repository";

    /// <summary>Read persisted and live records: what is enabled, what ran, what evidence exists.</summary>
    public const string InspectRuntimeState = "inspect_runtime_state";

    /// <summary>Assemble findings into the answer the operator asked for.</summary>
    public const string CompileResult = "compile_result";

    /// <summary>Judge whether a result covers what was requested.</summary>
    public const string VerifyResultCompleteness = "verify_result_completeness";

    /// <summary>Judge safety, policy and risk — a different question from completeness.</summary>
    public const string VerifySafety = "verify_safety";

    /// <summary>Run allowlisted checks and record their exit statuses as receipts. v0.3.8.101 —
    /// the capability behind <see cref="MissionAuthority.ExecuteChecks"/>, and deliberately not
    /// "run commands": what it grants is the catalog, not a shell.</summary>
    public const string ExecuteDiagnosticChecks = "execute_diagnostic_checks";

    /// <summary>Propose an allowlisted infrastructure action into the infrastructure's approval-gated
    /// pipeline, with a rollback note and a captured before-state. v0.3.8.102 — deliberately
    /// PROPOSE: execution is the operator's recorded escalation decision, and no capability
    /// grants it.</summary>
    public const string ProposeSystemAction = "propose_system_action";

    /// <summary>
    /// Resolve an external destination, propose the send, and — under the operator's recorded
    /// decision — deliver it and record where it landed. v0.3.8.103. Declared in the same release
    /// as the worker that serves it: a capability nothing can satisfy is the
    /// declaration-reaching-nobody defect wearing a specification's clothes.
    /// </summary>
    public const string ProposeExternalAction = "propose_external_action";

    /// <summary>
    /// Search outside the colony, open what comes back, and record each source as something this
    /// mission can be held to having consulted. v0.3.8.109.
    ///
    /// Deliberately RETRIEVE rather than "answer": what the capability grants is fetching and
    /// recording. Whether the answer built on top honestly attributes its claims is
    /// <c>CitationIntegrity</c>'s question, and no capability can grant an answer the property of
    /// being true.
    /// </summary>
    public const string RetrieveSources = "retrieve_sources";

    /// <summary>Read prior mission and objective memory.</summary>
    public const string RecallMissionHistory = "recall_mission_history";
}
