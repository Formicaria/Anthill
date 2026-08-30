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
    public sealed record Result(bool Satisfied, IReadOnlyList<string> Unresolved, int Claims, int Unsourced)
    {
        public string Explanation => Satisfied
            ? $"citation integrity: {Claims} claim(s), {Unsourced} unsourced, every citation resolved"
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

    /// <summary>The claim record, from the LATEST sourced answer this mission produced.</summary>
    public static SourcedAnswer? Answer(IReadOnlyList<Artifact>? artifacts) =>
        artifacts?
            .Where(a => string.Equals(a.Schema, ArtifactSchemas.SourcedAnswer, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => SourcedAnswer.FromJson(a.Payload))
            .FirstOrDefault(a => a is not null);

    /// <summary>
    /// Every url this mission actually retrieved, from the `source_set` records the web ant writes.
    ///
    /// Case-insensitive, because a model that reproduces a url with different capitalisation has
    /// cited the same page — and refusing that would be grading transcription rather than honesty.
    /// </summary>
    public static IReadOnlySet<string> Retrieved(IReadOnlyList<Artifact>? artifacts)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (artifacts is null) return urls;

        foreach (var artifact in artifacts.Where(a =>
                     string.Equals(a.Schema, ArtifactSchemas.SourceSet, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(artifact.Payload);
                if (!document.RootElement.TryGetProperty("sources", out var sources)) continue;
                foreach (var source in sources.EnumerateArray())
                    if (source.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } value)
                        urls.Add(value);
            }
            catch (System.Text.Json.JsonException)
            {
                // A malformed source set records nothing here. It is already reported as a schema
                // non-conformance where the artifact is read; failing the mission a second time for
                // the same defect would say nothing new.
            }
        }
        return urls;
    }
}
