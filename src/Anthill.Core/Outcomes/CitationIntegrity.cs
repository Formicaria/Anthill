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
    public static Result Evaluate(IReadOnlyList<Artifact>? artifacts)
    {
        var answer = Answer(artifacts);
        if (answer is null) return NothingToCheck;

        var retrieved = Retrieved(artifacts);
        if (retrieved.Count == 0) return NothingToCheck;

        var unresolved = answer.CitedUrls
            .Where(url => !retrieved.Contains(url))
            .ToList();

        return new Result(unresolved.Count == 0, unresolved, answer.Claims.Count, answer.UnsourcedCount);
    }

    /// <summary>
    /// Evaluate under the CONTRACT trigger as well as the retrieval one. v0.3.8.109.
    ///
    /// THE NULL ASYMMETRY IS INVERTED HERE, and that is the substance of the second trigger rather
    /// than a detail of it. <see cref="Evaluate(IReadOnlyList{Artifact})"/> treats an unreadable
    /// store as satisfied because its job is to catch a claim the record CONTRADICTS, and an empty
    /// record contradicts nothing. For a mission whose contract requires retrieved sources the
    /// question is the opposite one — absence IS the finding — so the same emptiness that means
    /// "nothing to check" over there means "the thing you were admitted to do did not happen" here.
    /// That is the identical reasoning <c>AssessmentObjective</c> applies to a missing inspection,
    /// and the reason the `.99` gate alone could never have caught a research mission that searched
    /// nothing: both cases leave exactly the same empty store.
    /// </summary>
    public static Result Evaluate(Missions.MissionSpecification? specification, IReadOnlyList<Artifact>? artifacts)
    {
        if (!RequiresSources(specification)) return Evaluate(artifacts);

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

        var unresolved = answer.CitedUrls.Where(url => !retrieved.Contains(url)).ToList();
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
    /// Everything this mission may honestly cite: what the world said (`source_set`) and what the
    /// colony already knew (`recall_set`). Both are records of something CONSULTED, which is the
    /// only property that makes a citation checkable — the difference between them is where the
    /// knowledge came from, not whether it can be traced.
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
}
