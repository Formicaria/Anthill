using Anthill.SDK.Artifacts;

namespace Anthill.Core.Outcomes;

/// <summary>
/// DOES THE ANSWER CITE THINGS THAT WERE ACTUALLY RETRIEVED? v0.3.8.99.
///
/// THE FAILURE THIS EXISTS FOR is one the audit class cannot have. An audit's evidence is something
/// the colony DID, so "did it inspect" is answerable from its own records. A research answer's
/// evidence is something the WORLD said — and the model writing the answer is also the thing
/// proposing which source supports which sentence. That arrangement produces a fabricated citation:
/// a claim attributed to a url the mission never retrieved. It is fluent, it is confident, and it is
/// false in the one way an operator cannot catch by reading, because a real citation and an invented
/// one look identical on the page.
///
/// SO THE MODEL PROPOSES AND THIS DECIDES, which is ADR-008's division applied to attribution. The
/// builder is shown what was retrieved and asked to cite from it; this resolves every url it cited
/// against the mission's own `source_set` records. A citation that resolves to nothing is not a
/// weaker claim — it is a claim about the mission's own history that is untrue, and the mission
/// fails for it.
///
/// WHAT IT DOES NOT CHECK, deliberately: whether the source SUPPORTS the claim. That is a semantic
/// judgment and a model asserting it is exactly the evidence v2.19.0 stopped accepting. `.98`
/// recorded what happens when a gate reaches for semantics it cannot reach — see the answer-coverage
/// note in `PLAN.md` §2c — and this stays on the side of the line where a record can answer:
/// TRACEABILITY is checkable, support is not.
///
/// AN UNSOURCED CLAIM IS NOT A FAILURE. It is the honest outcome for something the mission could not
/// attribute, and refusing a mission for admitting one would teach exactly the wrong lesson: that
/// deleting the unsupported parts is how an answer passes.
///
/// CITATION LAUNDERING, CLOSED AT v0.3.8.123. `recall_set` rows are written by `ResearcherAnt` with
/// `Url = "mission:&lt;id&gt;"`, and until this release a claim citing one resolved because the recall
/// HAPPENED — the record proves the colony consulted mission `abc`, and nothing asked what mission
/// `abc` itself rested on. So an unsupported assertion made in one mission became a resolvable
/// citation in the next, and a third could cite the second: each hop looked like attribution and the
/// chain as a whole was attached to nothing. That is worse than a fabricated url, because it is
/// TRUE at every step — the recall really did occur — and the falsehood lives only in what the
/// operator concludes from it.
///
/// The fix is the walk in <see cref="Resolvable"/>: a `mission:` citation resolves only when the
/// recalled mission's own record reaches something the WORLD said. Depth-limited and cycle-safe,
/// because two missions that recalled each other would otherwise each vouch for the other forever.
/// </summary>
public static class CitationIntegrity
{
    /// <summary>The verdict, and the citations that could not be resolved.</summary>
    /// <param name="ContractFailure">v0.3.8.109 — set when the mission failed its CONTRACT rather
    /// than its citations: it was admitted as a class that requires retrieved sources and there is
    /// no retrieval, or no claim record, to check. A separate field rather than a sentinel in
    /// <paramref name="Unresolved"/>, because that list means "urls the answer cited" everywhere
    /// else and an operator reading "the answer cites '(no source was retrieved)'" would be told
    /// something untrue about their own answer.</param>
    public sealed record Result(bool Satisfied, IReadOnlyList<string> Unresolved, int Claims, int Unsourced,
        string? ContractFailure = null)
    {
        public string Explanation => Satisfied
            ? $"citation integrity: {Claims} claim(s), {Unsourced} unsourced, every citation resolved"
            : ContractFailure is not null
            ? $"citation integrity NOT satisfied — {ContractFailure}"
            : "citation integrity NOT satisfied — the answer cites "
            + string.Join("; ", Unresolved.Select(u => $"'{u}'"))
            + ", which this mission never retrieved";
    }

    private static readonly Result NothingToCheck = new(true, Array.Empty<string>(), 0, 0);

    /// <summary>
    /// True when the mission has something for this layer to judge: it retrieved sources and
    /// produced a claim record. A mission that searched nothing, or whose answer was ordinary prose,
    /// is left entirely to the existing gates — which is what makes this safe for every mission that
    /// ran before it.
    /// </summary>
    public static bool Applies(IReadOnlyList<Artifact>? artifacts) =>
        Retrieved(artifacts).Count > 0 && Answer(artifacts) is not null;

    /// <summary>
    /// THE SECOND TRIGGER, BUILT. v0.3.8.109 — either is sufficient: the mission's CONTRACT requires
    /// retrieved sources, or the mission actually retrieved some and produced a claim record.
    ///
    /// The two catch different failures and neither subsumes the other. The retrieval trigger
    /// (`.99`) is the broader one — it covers a coding mission that happened to search — and it can
    /// only ever catch a citation that resolves to nothing. It is blind by construction to the
    /// research mission that retrieved NOTHING: an empty store leaves nothing to contradict, and the
    /// gate correctly reads "nothing to check". The contract trigger is what makes that case
    /// answerable, because a mission whose class promises sourced work and produced no source has
    /// not delivered a weaker answer — it has not done the thing it was admitted to do.
    ///
    /// KEYED ON REQUIRED EVIDENCE, not on the class name, and that is deliberate: a later class that
    /// also requires <c>source_retrieval</c> gets this gate without editing this line, and a class
    /// that merely happens to be about the outside world does not get it by accident.
    /// </summary>
    public static bool Applies(Missions.MissionSpecification? specification, IReadOnlyList<Artifact>? artifacts) =>
        RequiresSources(specification) || Applies(artifacts);

    /// <summary>True when the mission's contract demands retrieved sources. v0.3.8.109.</summary>
    public static bool RequiresSources(Missions.MissionSpecification? specification) =>
        specification is not null
     && specification.RequiredEvidence.Contains(EvidenceKinds.SourceRetrieval, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// THE SECOND TRIGGER, BUILT AT v0.3.8.109 — see the <c>Applies(specification, artifacts)</c>
    /// overload. The account below is kept as written at `.104`, because what it describes is the
    /// reason the gap lasted five releases and the shape of what finally closed it: not a cleverer
    /// reading of the artifacts, but a class that declares what it requires.
    ///
    /// WHAT WAS MISSING, AND WHY IT COULD NOT BE FAKED. v0.3.8.104.
    ///
    /// `.104` was asked to key this gate on two triggers, either sufficient: the mission's CONTRACT
    /// requires sourced research, OR the mission actually retrieved sources. The second is what
    /// `.99` built and is the broader of the two — it covers every mission that searched, including
    /// coding missions that are never classified as research.
    ///
    /// The first CANNOT BE BUILT without something to read it from, and nothing declares it. There
    /// is no `research` mission class (the `.99` divergence, still open), no evidence kind meaning
    /// "a source was retrieved", and no worker capability a research mission could require. A
    /// trigger keyed on any of those would be a branch nothing reaches — the declaration-reaching-
    /// nobody defect this release exists to close, reintroduced by the release closing it.
    ///
    /// What it needs is the research class itself: a class that declares a required evidence kind
    /// for retrieved sources, so this reads `specification.RequiredEvidence` the way
    /// `DiagnosisIntegrity` reads `command_check` today. That is a classification change — new
    /// verbs, a new target, and an ordering decision against four existing branches — and doing it
    /// as an addendum is how a request gets silently rerouted into the wrong lane. It is named in
    /// `PLAN.md` §2c as the remaining half rather than approximated here.
    ///
    /// Recorded as a method so the next reader finds this where they will look for it, rather than
    /// concluding the second trigger was forgotten.
    ///
    /// v0.3.8.109 supplied all three of the missing pieces named above — the `research` class,
    /// <see cref="EvidenceKinds.SourceRetrieval"/>, and
    /// <c>WorkerCapabilities.RetrieveSources</c> — in the release that needs them, and this now
    /// reads true.
    /// </summary>
    public static bool ContractTriggerAvailable => true;

    /// <summary>
    /// Resolve every citation against what the mission actually retrieved.
    /// </summary>
    /// <param name="artifacts">The mission's artifacts, or null when the store could not be read.
    /// Null returns SATISFIED rather than failing closed, and the asymmetry is deliberate: this
    /// layer's job is to catch a claim the record CONTRADICTS, and an unreadable store contradicts
    /// nothing. Failing closed here would demote every mission whose store hiccuped for a fault
    /// none of them committed — unlike the assessment objective, where an unreadable store means a
    /// required inspection cannot be SHOWN and absence is the whole question.</param>
    /// <param name="recalledArtifacts">v0.3.8.123 — a prior mission's artifacts by mission id, so a
    /// `mission:` citation can be traced to what that mission itself rested on. A LOOKUP rather than
    /// a store reference, for the reason <c>TrailGuidedSelection.Prefer</c> takes one: the verdict
    /// stays a pure function of its arguments and is therefore replayable in a test from a
    /// hand-built history, which is the only way the cycle case can be exercised at all. Null means
    /// no prior mission can be traced, and a `mission:` citation then does not resolve — see
    /// <see cref="Resolvable"/> for why that direction is the safe one.</param>
    public static Result Evaluate(IReadOnlyList<Artifact>? artifacts,
        Func<string, IReadOnlyList<Artifact>?>? recalledArtifacts = null)
    {
        var answer = Answer(artifacts);
        if (answer is null) return NothingToCheck;

        var retrieved = Retrieved(artifacts);
        if (retrieved.Count == 0) return NothingToCheck;

        var resolvable = Resolvable(artifacts, recalledArtifacts);
        var unresolved = answer.CitedUrls
            .Where(url => !resolvable.Contains(url))
            .ToList();

        return new Result(unresolved.Count == 0, unresolved, answer.Claims.Count, answer.UnsourcedCount);
    }

    /// <summary>
    /// Evaluate under the CONTRACT trigger as well as the retrieval one. v0.3.8.109.
    ///
    /// THE NULL ASYMMETRY IS INVERTED HERE, and that is the substance of the second trigger rather
    /// than a detail of it. The retrieval-trigger overload treats an unreadable
    /// store as satisfied because its job is to catch a claim the record CONTRADICTS, and an empty
    /// record contradicts nothing. For a mission whose contract requires retrieved sources the
    /// question is the opposite one — absence IS the finding — so the same emptiness that means
    /// "nothing to check" over there means "the thing you were admitted to do did not happen" here.
    /// That is the identical reasoning <c>AssessmentObjective</c> applies to a missing inspection,
    /// and the reason the `.99` gate alone could never have caught a research mission that searched
    /// nothing: both cases leave exactly the same empty store.
    /// </summary>
    /// <param name="recalledArtifacts">See the other overload. The CONTRACT trigger's own two
    /// refusals below still read <see cref="Retrieved"/> rather than <see cref="Resolvable"/>, and
    /// that is deliberate: they ask whether this mission recorded consulting ANYTHING, which is a
    /// question about this mission's own conduct. Whether what it consulted can be traced further
    /// back is the citation question, and it is asked once, below, where the citations are.</param>
    public static Result Evaluate(Missions.MissionSpecification? specification, IReadOnlyList<Artifact>? artifacts,
        Func<string, IReadOnlyList<Artifact>?>? recalledArtifacts = null)
    {
        if (!RequiresSources(specification)) return Evaluate(artifacts, recalledArtifacts);

        if (artifacts is null)
            return new Result(false, Array.Empty<string>(), 0, 0,
                "this mission's contract requires retrieved sources and its artifact store could "
              + "not be read, so nothing can show that anything was retrieved. A gate that cannot "
              + "run is not a pass.");

        var retrieved = Retrieved(artifacts);
        if (retrieved.Count == 0)
            return new Result(false, Array.Empty<string>(), 0, 0,
                "this mission's contract requires retrieved sources and it recorded none. An answer "
              + "about the outside world assembled without consulting it is not a weaker answer — "
              + "it is the mission's own subject matter missing.");

        var answer = Answer(artifacts);
        if (answer is null)
            return new Result(false, Array.Empty<string>(), 0, 0,
                $"this mission retrieved {retrieved.Count} source(s) and produced no claim record, "
              + "so nothing attributes any part of the answer to any of them. What was retrieved "
              + "cannot be told apart from what was invented.");

        var resolvable = Resolvable(artifacts, recalledArtifacts);
        var unresolved = answer.CitedUrls.Where(url => !resolvable.Contains(url)).ToList();
        return new Result(unresolved.Count == 0, unresolved, answer.Claims.Count, answer.UnsourcedCount);
    }

    /// <summary>The claim record, from the LATEST sourced answer this mission produced.</summary>
    public static SourcedAnswer? Answer(IReadOnlyList<Artifact>? artifacts) =>
        artifacts?
            .Where(a => string.Equals(a.Schema, ArtifactSchemas.SourcedAnswer, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => SourcedAnswer.FromJson(a.Payload))
            .FirstOrDefault(a => a is not null);

    /// <summary>
    /// Everything this mission recorded CONSULTING: what the world said (`source_set`) and what the
    /// colony already knew (`recall_set`). Both are records of an act this mission performed, which
    /// is what the triggers above ask about — did this mission consult anything at all.
    ///
    /// NOT the set a citation resolves against; that is <see cref="Resolvable"/>, and v0.3.8.123
    /// split the two because they answer different questions. This one said "everything this
    /// mission may honestly cite" until that release, and the sentence was wrong in exactly the way
    /// the laundering path needed: a `recall_set` row records that the colony consulted its own
    /// history, which is a fact about this mission's conduct and no evidence about what the recalled
    /// mission itself rested on.
    ///
    /// Case-insensitive on the URL, because a model that reproduces one with different
    /// capitalisation has cited the same page and refusing that would grade transcription rather
    /// than honesty — and case-insensitive on the FIELD NAMES too, via the shared parser, because
    /// the first draft of this method read `"url"` from a payload the producer writes as `"Url"`
    /// and silently resolved nothing. See <see cref="SourceSetPayload"/>.
    /// </summary>
    public static IReadOnlySet<string> Retrieved(IReadOnlyList<Artifact>? artifacts) =>
        artifacts is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : SourceSetPayload.UrlsFrom(artifacts
                .Where(a => ArtifactSchemas.CitableRecords.Contains(a.Schema))
                .Select(a => a.Payload));

    /// <summary>The url scheme an internal source is recorded under — <c>ResearcherAnt</c>'s
    /// `mission:&lt;id&gt;`, named here so the writer and this reader cannot drift apart.</summary>
    public const string RecalledMissionPrefix = "mission:";

    /// <summary>
    /// How many recall hops the provenance walk will follow before it stops. Four, and the number
    /// is a BOUND rather than a judgment: the cycle guard already terminates every loop, so this
    /// exists for the chain that is merely long — A recalled B recalled C recalled D — where each
    /// additional hop makes "this claim traces to a source" a weaker statement about the claim and
    /// a more expensive one to compute. Somewhere the chain has to be treated as untraceable, and
    /// an unresolved citation is the honest answer at that point rather than a guess in either
    /// direction.
    /// </summary>
    public const int MaxRecallDepth = 4;

    /// <summary>True when this url names a prior mission rather than a page in the world.</summary>
    public static bool IsRecalledMission(string? url) =>
        url is not null && url.StartsWith(RecalledMissionPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// EVERYTHING A CITATION MAY ACTUALLY RESOLVE TO. v0.3.8.123, and the difference from
    /// <see cref="Retrieved"/> is the whole of this release's citation fix.
    ///
    /// <see cref="Retrieved"/> answers "what did this mission record consulting" — every url in
    /// every citable record, external and internal alike — and that is still the right question for
    /// the triggers, which ask whether this mission did any consulting at all. This answers the
    /// narrower one the citations themselves need: which of those a claim may honestly REST on.
    ///
    /// For a `source_set` url the two sets are identical, and deliberately so: the world said it,
    /// the mission wrote down that it read it, and there is nothing further back to walk. The
    /// divergence is `recall_set`. `mission:abc` records that the colony consulted its own history
    /// — a fact about THIS mission's conduct, and no evidence at all about what mission `abc`
    /// rested on. Resolving on the recall alone made "we concluded this before" a citation, which
    /// is how an unsupported assertion becomes a sourced one by being remembered: laundering, and
    /// it compounds, because the mission that cites the launderer launders in turn.
    ///
    /// SO THE WALK ASKS THE RECALLED MISSION THE SAME QUESTION. It resolves when that mission holds
    /// a `source_set` of its own — it went and read something — and otherwise follows ITS recalls
    /// one hop further, up to <see cref="MaxRecallDepth"/>. A visited set makes the cycle terminate:
    /// A citing B citing A is two missions vouching for each other and no source anywhere, so it
    /// must end, and it must end UNRESOLVED rather than at whichever of the two the walk entered
    /// from.
    ///
    /// AN UNTRACEABLE RECALL IS NOT A NEW CLAIM STATE. The url simply is not in this set, so the
    /// citing claim lands in <c>Result.Unresolved</c> exactly as an invented url does, and
    /// <c>SourcedAnswer</c> keeps deriving "unsourced" from a null url as it always has. A third
    /// state would have to be understood by every consumer downstream, and every one of them
    /// already knows what an unresolved citation means.
    ///
    /// A NULL LOOKUP RESOLVES NO RECALL, and that direction is chosen rather than inherited. This
    /// method's job is to say what a claim may rest on; "we have no way to find out" is not a
    /// reason to say yes. It is the opposite asymmetry from <c>Evaluate</c>'s
    /// permissive null store, and the two do not conflict: an unreadable store leaves nothing to
    /// contradict and the gate stays silent, while a caller that supplies no history has still
    /// produced a citation that nothing traces.
    /// </summary>
    public static IReadOnlySet<string> Resolvable(IReadOnlyList<Artifact>? artifacts,
        Func<string, IReadOnlyList<Artifact>?>? recalledArtifacts)
    {
        var resolvable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in Retrieved(artifacts))
        {
            if (!IsRecalledMission(url)) { resolvable.Add(url); continue; }
            if (TracesToRetrieval(url[RecalledMissionPrefix.Length..].Trim(), recalledArtifacts,
                    MaxRecallDepth, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                resolvable.Add(url);
        }
        return resolvable;
    }

    /// <summary>
    /// Did this prior mission's own record reach something the world said? The walk described in
    /// <see cref="Resolvable"/>.
    ///
    /// A `source_set` with at least one readable url ends it. An EMPTY one does not: a record of a
    /// search that returned nothing is a record of consulting nothing, and treating the artifact's
    /// existence as the answer would make the schema name the evidence — the silent-parse failure
    /// <see cref="SourceSetPayload"/> exists to have ended, reintroduced one layer up.
    ///
    /// Never throws. A history lookup that fails is a walk that cannot trace, which is already what
    /// this returns for a mission with no record — a diagnostic must not fail the grading it
    /// informs.
    /// </summary>
    private static bool TracesToRetrieval(string missionId,
        Func<string, IReadOnlyList<Artifact>?>? recalledArtifacts, int depth, HashSet<string> visited)
    {
        if (recalledArtifacts is null || missionId.Length == 0 || depth <= 0) return false;
        // The cycle, and the reason it must answer FALSE: A vouching for B while B vouches for A is
        // two missions and no source, and returning true for whichever the walk entered from would
        // make the verdict depend on where it started.
        if (!visited.Add(missionId)) return false;

        IReadOnlyList<Artifact>? history;
        try { history = recalledArtifacts(missionId); }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[citations] could not read the record of recalled mission {missionId}: {error.Message}");
            return false;
        }
        if (history is null || history.Count == 0) return false;

        if (history.Any(a => string.Equals(a.Schema, ArtifactSchemas.SourceSet, StringComparison.OrdinalIgnoreCase)
                          && SourceSetPayload.Read(a.Payload).Count > 0))
            return true;

        foreach (var url in SourceSetPayload.UrlsFrom(history
                     .Where(a => string.Equals(a.Schema, ArtifactSchemas.RecallSet, StringComparison.OrdinalIgnoreCase))
                     .Select(a => a.Payload)))
            if (IsRecalledMission(url)
                && TracesToRetrieval(url[RecalledMissionPrefix.Length..].Trim(), recalledArtifacts, depth - 1, visited))
                return true;

        return false;
    }
}
