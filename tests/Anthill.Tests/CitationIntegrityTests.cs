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
