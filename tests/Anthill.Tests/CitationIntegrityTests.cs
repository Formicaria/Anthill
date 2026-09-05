using Anthill.Core.Outcomes;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.99 — A CITATION TO SOMETHING NEVER RETRIEVED.
///
/// The research class's characteristic failure, and the one an operator cannot catch by reading: a
/// fabricated citation looks exactly like a real one on the page. The model proposes which source
/// supports which claim; this decides whether the thing it cited was ever retrieved.
///
/// These tests hold both directions — a real citation resolves, an invented one fails the mission —
/// and the two boundaries that keep the gate honest: it says nothing about missions that retrieved
/// nothing, and it does not punish an answer for admitting what it could not attribute.
///
/// v0.3.8.123 adds the third direction, which is neither of the first two: a citation that is TRUE
/// and rests on nothing. `mission:&lt;id&gt;` resolved because the recall happened, so an unsupported
/// assertion became a sourced one by being remembered — and the next mission could cite that. The
/// tests for it come in a pair on purpose, because either alone is satisfiable by a gate that has
/// stopped working: one refuses the laundering chain (and its cycle), the other proves legitimate
/// recall still resolves.
/// </summary>
public class CitationIntegrityTests
{
    private const string Ollama = "https://ollama.com/";
    private const string LlamaCpp = "https://github.com/ggerganov/llama.cpp";

    /// <summary>
    /// A source set spelled THE WAY THE PRODUCER SPELLS IT.
    ///
    /// `WebResearchAnt` writes `new { src.Title, src.Url, … }` through `Json.Dumps`, which sets no
    /// naming policy — so the payload carries `"Title"` and `"Url"`, PascalCase. The first draft of
    /// this fixture wrote them lowercase, matching what the READER expected, and every test here
    /// passed while both readers silently resolved nothing against real payloads. A fixture that
    /// agrees with the code under test proves only that two things written together match.
    /// </summary>
    private static Artifact SourceSet(params string[] urls) => Artifact.Create(
        schema: ArtifactSchemas.SourceSet,
        producerRole: "web",
        missionId: "m",
        payload: Json.Dumps(new
        {
            query = "q",
            sources = urls.Select(u => new { Title = "t", Url = u, Domain = "d", confidence = 0.8 }),
        }));

    private static Artifact Answer(SourcedAnswer answer) => Artifact.Create(
        schema: ArtifactSchemas.SourcedAnswer,
        producerRole: "builder",
        missionId: "m",
        payload: answer.ToJson());

    private static SourcedAnswer Claims(params (string Text, string? Url)[] claims) =>
        new() { Claims = claims.Select(c => new SourcedClaim(c.Text, c.Url)).ToList() };

    /// <summary>
    /// A recall record spelled THE WAY ITS PRODUCER SPELLS IT, for the reason
    /// <see cref="SourceSet"/> is: `ResearcherAnt.WithRecallRecord` writes the same `sources` shape
    /// a source set uses, with `mission:&lt;id&gt;` in the url slot. One vocabulary for "what may be
    /// cited" is what lets one gate resolve both — and it is also exactly what made the laundering
    /// path possible, so a fixture that wrote it any other way would prove nothing about it.
    /// </summary>
    private static Artifact RecallSet(params string[] missionUrls) => Artifact.Create(
        schema: ArtifactSchemas.RecallSet,
        producerRole: "researcher",
        missionId: "m",
        payload: Json.Dumps(new
        {
            query = "q",
            sources = missionUrls.Select(u => new { Url = u, Title = "an earlier mission" }),
        }));

    /// <summary>
    /// The colony's prior missions, as the lookup the evaluator is handed — `Queen` supplies the
    /// artifact store's `ForMission` in production, and this supplies a history written by hand.
    ///
    /// A LOOKUP RATHER THAN A DATABASE is what makes the cycle case expressible at all: two
    /// missions that recall each other would need mission rows, artifact rows and a working store
    /// before the walk under test ever ran, and what would then have been proven is that SQLite can
    /// hold a cycle. An unknown id returns null, which is what an unreadable or absent record
    /// returns in production, and the two must reach the same verdict.
    /// </summary>
    private static Func<string, IReadOnlyList<Artifact>?> History(
        params (string MissionId, Artifact[] Artifacts)[] missions)
    {
        var byId = new Dictionary<string, IReadOnlyList<Artifact>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (missionId, artifacts) in missions) byId[missionId] = artifacts;
        return id => byId.TryGetValue(id, out var found) ? found : null;
    }

    [Fact]
    public void TheBuildersClaimFormat_Parses_AndKeepsUnsourcedClaims()
    {
        var parsed = SourcedAnswer.TryParse($"""
            CLAIM: Ollama runs models locally. [SOURCE: {Ollama}]
            CLAIM: llama.cpp is written in C++. [SOURCE: {LlamaCpp}]
            CLAIM: Most runtimes ship quantization. [UNSOURCED]
            """);

        Assert.NotNull(parsed);
        Assert.Equal(3, parsed!.Claims.Count);
        Assert.Equal(2, parsed.CitedUrls.Count);
        Assert.Equal(1, parsed.UnsourcedCount);

        // The claim text survives without its marker — the marker is metadata, not prose.
        Assert.Equal("Most runtimes ship quantization.", parsed.Claims[2].Text);
        Assert.Null(parsed.Claims[2].SourceUrl);
    }

    /// <summary>
    /// Ordinary prose returns NULL, which is a result rather than a failure to handle later: an
    /// artifact of empty claims would make "the model wrote prose" indistinguishable from "the
    /// answer asserted nothing", and every consumer downstream would believe the second.
    /// </summary>
    [Fact]
    public void OrdinaryProse_IsNotAClaimRecord()
    {
        Assert.Null(SourcedAnswer.TryParse("Ollama and llama.cpp are both popular local runtimes."));
        Assert.Null(SourcedAnswer.TryParse(""));
        Assert.Null(SourcedAnswer.TryParse(null));
    }

    /// <summary>The marking is rendered from the RECORD, so it cannot depend on the model having
    /// remembered to write it — or survive a synthesis pass that paraphrased it away.</summary>
    [Fact]
    public void TheRendering_MarksEveryUnattributedClaim()
    {
        var rendered = Claims(("Runs locally.", Ollama), ("Ships quantization.", null)).Render();

        Assert.Contains(Ollama, rendered, StringComparison.Ordinal);
        Assert.Contains("UNSOURCED", rendered, StringComparison.Ordinal);
        Assert.Contains("Ships quantization.", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCitationResolving_IsSatisfied()
    {
        var artifacts = new[]
        {
            SourceSet(Ollama, LlamaCpp),
            Answer(Claims(("Runs locally.", Ollama), ("Written in C++.", LlamaCpp), ("Quantization.", null))),
        };

        var result = CitationIntegrity.Evaluate(artifacts);

        Assert.True(result.Satisfied, result.Explanation);
        Assert.Empty(result.Unresolved);
        Assert.Equal(3, result.Claims);
        Assert.Equal(1, result.Unsourced);
    }

    /// <summary>THE FAILURE. A url the mission never retrieved, cited as though it had been.</summary>
    [Fact]
    public void AFabricatedCitation_IsRefused_AndNamed()
    {
        var artifacts = new[]
        {
            SourceSet(Ollama),
            Answer(Claims(("Runs locally.", Ollama), ("Most deployed.", "https://example.invalid/never"))),
        };

        var result = CitationIntegrity.Evaluate(artifacts);

        Assert.False(result.Satisfied);
        Assert.Equal("https://example.invalid/never", Assert.Single(result.Unresolved));
        Assert.Contains("never retrieved", result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Capitalisation is transcription, not honesty. A model that reproduced a url with a different
    /// case cited the same page, and refusing that would grade typing.
    /// </summary>
    [Fact]
    public void ACitationDifferingOnlyInCase_Resolves()
    {
        var artifacts = new[]
        {
            SourceSet("https://Ollama.com/"),
            Answer(Claims(("Runs locally.", "https://ollama.com/"))),
        };

        Assert.True(CitationIntegrity.Evaluate(artifacts).Satisfied);
    }

    /// <summary>
    /// AN UNSOURCED CLAIM IS NOT A FAILURE. Refusing a mission for admitting one would teach exactly
    /// the wrong lesson: that deleting the unsupported parts is how an answer passes.
    /// </summary>
    [Fact]
    public void AnAnswerThatAdmitsWhatItCannotAttribute_IsSatisfied()
    {
        var artifacts = new[]
        {
            SourceSet(Ollama),
            Answer(Claims(("Runs locally.", Ollama), ("A.", null), ("B.", null))),
        };

        var result = CitationIntegrity.Evaluate(artifacts);
        Assert.True(result.Satisfied);
        Assert.Equal(2, result.Unsourced);
    }

    /// <summary>
    /// THE PARSER READS THE PRODUCER, WHATEVER CASE IT USES.
    ///
    /// Both spellings, asserted together, so this can never again pass by agreeing with one of them.
    /// The PascalCase case is what production actually writes; the camelCase case is what a future
    /// serializer policy would produce, and a reader that broke silently on that change would be the
    /// same defect with a different trigger.
    /// </summary>
    [Fact]
    public void TheSourceSetParser_ReadsEitherSpelling()
    {
        var pascal = Json.Dumps(new { sources = new[] { new { Title = "t", Url = Ollama } } });
        var camel = Json.Dumps(new { sources = new[] { new { title = "t", url = Ollama } } });

        Assert.Equal(Ollama, Assert.Single(SourceSetPayload.Read(pascal)).Url);
        Assert.Equal(Ollama, Assert.Single(SourceSetPayload.Read(camel)).Url);

        // And nothing usable is read from junk, rather than throwing into the mission's face.
        Assert.Empty(SourceSetPayload.Read("{ not json"));
        Assert.Empty(SourceSetPayload.Read("{}"));
        Assert.Empty(SourceSetPayload.Read(null));
    }

    /// <summary>
    /// AN INTERNAL SOURCE IS A SOURCE — WHEN THE MISSION BEHIND IT WENT AND READ SOMETHING.
    /// v0.3.8.99, NARROWED AT v0.3.8.123.
    ///
    /// The original claim stands and is still asserted here: work drawn from the colony's own prior
    /// missions is TRACEABLE, the recall leaves a record, and `mission:&lt;id&gt;` resolves against
    /// it. Before that record existed such a claim could only render `[UNSOURCED]`, which flattens
    /// "we could not attribute this" together with "this came from our own history" — different
    /// facts, leading an operator to different next steps. An unrecalled mission id still fails
    /// exactly as an invented url does.
    ///
    /// WHAT `.123` CHANGED, and why this test was rewritten rather than deleted. The recall record
    /// proves the recall HAPPENED. It says nothing whatever about what the recalled mission itself
    /// rested on — so an unsupported assertion made in mission A became a resolvable citation in
    /// mission B simply by being remembered, and C could then cite B. Every hop was true; the chain
    /// as a whole hung on nothing. The fixture below therefore gives the recalled mission a
    /// `source_set` of its own, which is the case the original test MEANT and the case the gate
    /// should always have been asserting: the recall is legitimate because there is a page at the
    /// end of it. <see cref="ARecalledMissionThatRestsOnNothing_DoesNotLaunderTheClaim"/> is the
    /// other half, and the two must be read together: this one alone would pass against a gate that
    /// never checked anything.
    /// </summary>
    [Fact]
    public void AClaimCitingARecalledMission_Resolves_AndAnUnrecalledOneDoesNot()
    {
        var recall = RecallSet("mission:abc123");

        // The recalled mission went and read something. That is what makes citing it honest.
        var history = History(("abc123", new[] { SourceSet(Ollama) }));

        var honest = new[] { recall, Answer(Claims(("We concluded this before.", "mission:abc123"))) };
        var invented = new[] { recall, Answer(Claims(("We concluded this before.", "mission:never-ran"))) };

        Assert.True(CitationIntegrity.Evaluate(honest, history).Satisfied);

        var refused = CitationIntegrity.Evaluate(invented, history);
        Assert.False(refused.Satisfied);
        Assert.Equal("mission:never-ran", Assert.Single(refused.Unresolved));
    }

    /// <summary>
    /// CITATION LAUNDERING, REFUSED. v0.3.8.123 — and it is the failure that a citation cannot show
    /// on the page, because every step of it is TRUE.
    ///
    /// Mission A answered without attributing anything. That is an honest outcome and this layer has
    /// always permitted it (see <see cref="AnAnswerThatAdmitsWhatItCannotAttribute_IsSatisfied"/>);
    /// nothing here punishes A. Mission B then recalls A and cites `mission:A` — and until this
    /// release that citation RESOLVED, because the recall record proves the recall happened and
    /// nothing asked what A itself rested on. B's answer therefore read as sourced, its source was
    /// A's narrative, and A's narrative was attached to nothing. Worse than a fabricated url,
    /// because a fabricated url is false and can be caught by looking; this is true at every hop and
    /// false only in what an operator concludes from the chain.
    ///
    /// AND THE CYCLE TERMINATES, UNRESOLVED. Two missions that recalled each other vouch for one
    /// another forever, and a walk that answered "resolved" for whichever it happened to enter from
    /// would make the verdict a fact about the walk instead of about the evidence. It must stop, and
    /// it must stop by saying no — a hang here would read as a slow suite rather than as a defect.
    /// </summary>
    [Fact]
    public void ARecalledMissionThatRestsOnNothing_DoesNotLaunderTheClaim()
    {
        var cited = new[] { RecallSet("mission:A"), Answer(Claims(("A concluded this, so it stands.", "mission:A"))) };

        // A retrieved nothing and attributed nothing: an honest unsourced answer, and not a source.
        var unsupported = History(("A", new[] { Answer(Claims(("It stands.", null))) }));

        var laundered = CitationIntegrity.Evaluate(cited, unsupported);
        Assert.False(laundered.Satisfied);
        Assert.Equal("mission:A", Assert.Single(laundered.Unresolved));

        // The same verdict from a history that has never heard of A, and from no history at all.
        // "We cannot find out" is not a reason to say yes about what a claim rests on.
        Assert.False(CitationIntegrity.Evaluate(cited, History()).Satisfied);
        Assert.False(CitationIntegrity.Evaluate(cited).Satisfied);

        // THE CYCLE. A recalled B, B recalled A, and neither ever retrieved anything.
        var cycle = History(
            ("A", new[] { RecallSet("mission:B") }),
            ("B", new[] { RecallSet("mission:A") }));

        var refused = CitationIntegrity.Evaluate(cited, cycle);
        Assert.False(refused.Satisfied);
        Assert.Equal("mission:A", Assert.Single(refused.Unresolved));
    }

    /// <summary>
    /// AND LEGITIMATE RECALL IS UNTOUCHED. v0.3.8.123.
    ///
    /// The narrowing above is worth nothing if it also refuses the case the recall record was built
    /// for: a claim drawn from a prior mission that DID go and read something. That mission's answer
    /// traces to a page in the world, and citing it is attribution rather than assertion — the whole
    /// distinction `.99` created the `recall_set` to make expressible.
    ///
    /// THE CHAIN RESOLVES TOO, and it is the same fact one hop further out. A recalled B, B recalled
    /// nothing but retrieved a source; the provenance reaches the world through two hops instead of
    /// one, and stopping at the first would refuse a mission for the shape of its history rather
    /// than for its evidence. What bounds it is depth, not distrust — see
    /// <see cref="CitationIntegrity.MaxRecallDepth"/>.
    /// </summary>
    [Fact]
    public void ARecalledMissionThatRetrievedItsOwnSources_StillResolves()
    {
        var cited = new[] { RecallSet("mission:A"), Answer(Claims(("A established this.", "mission:A"))) };

        Assert.True(CitationIntegrity.Evaluate(cited,
            History(("A", new[] { SourceSet(Ollama), Answer(Claims(("Runs locally.", Ollama))) }))).Satisfied);

        // A → B → the world.
        Assert.True(CitationIntegrity.Evaluate(cited, History(
            ("A", new[] { RecallSet("mission:B") }),
            ("B", new[] { SourceSet(LlamaCpp) }))).Satisfied);

        // A `source_set` that retrieved NOTHING is not the end of the walk. The artifact's existence
        // is not the evidence — its contents are, and a search that returned nothing is a record of
        // having consulted nothing.
        Assert.False(CitationIntegrity.Evaluate(cited, History(("A", new[] { SourceSet() }))).Satisfied);
    }

    /// <summary>
    /// THE VERIFIER'S EVALUATOR INHERITS THE SAME REFUSAL. v0.3.8.123.
    ///
    /// `VerifierAnt` reads the evidence store and never walks citations, which is correct and stays
    /// as it is — asking a verifier to trace provenance would put the model back inside the decision
    /// ADR-008 removed it from. The gap was that nothing asserted a prior mission's NARRATIVE cannot
    /// stand in for evidence at the layer that grades the answer.
    ///
    /// `ResearchIntegrity` delegates the "is what you cited real" question to `CitationIntegrity`
    /// precisely so the two cannot disagree about what counts as retrieved. That delegation is what
    /// this asserts: a research mission whose only source is another mission's unsupported answer
    /// fails, in the same words, through the class gate that grades it. Without this, the fix could
    /// be complete in the layer nobody's mission is actually graded by.
    /// </summary>
    [Fact]
    public void AResearchMissionCitingAnotherMissionsNarrative_FailsTheClassGate()
    {
        var specification = Anthill.Core.Missions.MissionIntake.Resolve(
            "Research what the papers and vendors say about local model quantization. "
          + "What do the papers recommend? Which vendors ship it?");
        Assert.Equal(Anthill.Core.Missions.MissionSpecification.ResearchClass, specification.MissionClass);

        var artifacts = new[]
        {
            RecallSet("mission:A"),
            Answer(Claims(("The vendors ship it, as we established before.", "mission:A"))),
        };
        // A retrieval RAN — so the second and third failures this class checks are satisfied, and
        // whatever comes back is the citation layer speaking and nothing else.
        var evidence = new[]
        {
            Evidence.Create(kind: EvidenceKinds.SourceRetrieval, deterministic: false, passed: true,
                missionId: "m", detail: "recall: q"),
        };

        var unsupported = History(("A", new[] { Answer(Claims(("It is so.", null))) }));

        var refused = ResearchIntegrity.Evaluate(specification, artifacts, evidence,
            answer: null, recalledArtifacts: unsupported);
        Assert.False(refused.Satisfied);
        Assert.Contains("mission:A", refused.Explanation, StringComparison.Ordinal);
        Assert.Contains("never retrieved", refused.Explanation, StringComparison.Ordinal);

        // And the same mission passes this gate the moment the recalled mission has a source of its
        // own — the fix refuses laundering, not recall.
        Assert.True(ResearchIntegrity.Evaluate(specification, artifacts, evidence, answer: null,
            recalledArtifacts: History(("A", new[] { SourceSet(Ollama) }))).Satisfied);
    }

    /// <summary>
    /// THE REASON SURVIVES THE PROCESS. v0.3.8.99.
    ///
    /// The evaluator composes a sentence naming the gate that refused and what it refused for, and
    /// until this release the mission row dropped it — every reader that came after the process
    /// exited saw the placeholder "loaded from persisted evaluation". The status columns say WHAT
    /// the verdict was; only this says WHY, and "failure messages must name the layer that said no"
    /// is not satisfied by a message that lives until the process ends.
    /// </summary>
    [Fact]
    public void ThePersistedEvaluation_KeepsTheReasonItWasGraded()
    {
        var db = Path.Combine(Path.GetTempPath(), $"anthill-eval-{Guid.NewGuid():N}.db");
        try
        {
            using var memory = new Anthill.Core.Memory.SqliteMemory(db);
            var mission = new Anthill.Core.Domain.Mission { Goal = "why was this refused?" };
            memory.SaveMission(mission);

            var reason = "outcome=completed_unverified (…) citation integrity NOT satisfied — "
                       + "the answer cites 'https://example.invalid/never', which this mission never retrieved";
            memory.SaveMissionEvaluation(new Anthill.Core.Outcomes.MissionEvaluation(
                MissionId: mission.Id,
                OutcomeCode: Anthill.Core.Outcomes.MissionOutcome.CompletedUnverified,
                StructuralStatus: "complete",
                VerificationStatus: Anthill.Core.Outcomes.MissionEvaluation.Verification.Passed,
                DeliverableStatus: Anthill.Core.Outcomes.MissionEvaluation.Deliverable.NotSatisfied,
                StopReason: null,
                EvaluatorVersion: "evaluator-v3",
                EvaluatedAt: "now",
                Explanation: reason));

            var loaded = memory.LoadMissionEvaluation(mission.Id);
            Assert.NotNull(loaded);
            Assert.Equal(reason, loaded!.Explanation);
        }
        finally { try { File.Delete(db); } catch { } }
    }

    /// <summary>
    /// THE BOUNDARIES. A mission that retrieved nothing, or whose builder wrote prose, has nothing
    /// for this layer to judge — which is every mission that ran before this release.
    /// </summary>
    [Fact]
    public void ItIsSilentWhereThereIsNothingToCheck()
    {
        Assert.False(CitationIntegrity.Applies(null));
        Assert.False(CitationIntegrity.Applies(Array.Empty<Artifact>()));

        // Sources retrieved, no claim record: ordinary prose, judged by the other gates.
        Assert.False(CitationIntegrity.Applies(new[] { SourceSet(Ollama) }));

        // A claim record with nothing retrieved: nothing to resolve against, so nothing is claimed.
        Assert.False(CitationIntegrity.Applies(new[] { Answer(Claims(("A.", Ollama))) }));

        // And an unreadable store contradicts nothing — see the remarks on Evaluate for why this
        // is the one gate that does NOT fail closed.
        Assert.True(CitationIntegrity.Evaluate(null).Satisfied);
    }
}
