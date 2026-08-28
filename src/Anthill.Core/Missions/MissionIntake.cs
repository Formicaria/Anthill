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
      + @"deploy|install|write)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RepositoryTargets = new(
        @"\b(repo|repository|codebase|code base|source|implementation|implemented|workflow|"
      + @"orchestration|architecture|colony|anthill)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RuntimeTargets = new(
        @"\b(runtime|running|enabled|configured|configuration|current(?:ly)?|now|today|state|"
      + @"health|healthy|live|active|actually (?:ran|run|used)|missions?|workers?|ants?|roles?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CurrentFreshness = new(
        @"\b(now|current(?:ly)?|today|at the moment|right now|present(?:ly)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Capability ids a system audit requires. Workers declare against these; the resolver matches
    /// them. Named for what must be POSSIBLE, not for who does it — naming a worker here would put
    /// the selection back in the specification and make the resolver ceremonial.
    /// </summary>
    /// <remarks>
    /// <see cref="WorkerCapabilities.InspectRuntimeState"/> is deliberately ABSENT at v0.3.8.98. No
    /// worker serves it: the mission researcher reads history, not live state, and claiming
    /// otherwise is how an audit gets answered from what previous missions did. Requiring a
    /// capability nothing can serve would be a declaration reaching nobody — this repository's
    /// recurring defect — so the id stays defined for the release that builds the worker, and the
    /// requirement lands with it.
    /// </remarks>
    public static readonly IReadOnlyList<string> SystemAuditCapabilities = new[]
    {
        WorkerCapabilities.InspectRepository,
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

        // THE CLASS IS DERIVED, and only when the dimensions agree on something this release can
        // actually serve. Assessment of the repository and/or the runtime is a system audit. Change
        // and diagnose intents are real classes with no machinery yet (ADR-008: .101 and later), so
        // they resolve to `general` and behave as before rather than claiming a lane that is empty.
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
        if (AssessVerbs.IsMatch(request) || CapabilityQuestions.IsMatch(request)) return MissionIntent.Assess;
        return MissionIntent.Explain;
    }

    private static MissionTargets ResolveTargets(string request)
    {
        var targets = MissionTargets.None;
        if (RepositoryTargets.IsMatch(request)) targets |= MissionTargets.Repository;
        if (RuntimeTargets.IsMatch(request)) targets |= MissionTargets.Runtime;
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

    /// <summary>Read prior mission and objective memory.</summary>
    public const string RecallMissionHistory = "recall_mission_history";
}
