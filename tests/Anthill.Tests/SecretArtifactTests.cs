using Anthill.Core.Domain;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// SECRET MEANS SECRET. v0.3.8.63, PLAN.md §1b S5 — the last P0.
///
/// `Artifact.cs` promised "never rendered, never sent to a model" since the visibility field
/// existed, and nothing enforced it: the context compiler emitted Secret payloads into prompts,
/// declared Secret inputs included, and the soldier read payloads with no check at all. Worse,
/// malformed visibility is deliberately coerced TO Secret, so the unenforced value was exactly
/// where a corrupt or hostile row landed. These tests pin the enforcement at both boundaries and
/// the one rule that makes the coercion safe: readability is an ALLOWLIST, so an out-of-range
/// enum value fails closed.
/// </summary>
public class SecretArtifactTests
{
    private const string SecretPayload = "api_key=sk-EXTREMELY-SECRET-VALUE-12345";

    private sealed class StubArtifactStore(IReadOnlyList<Artifact> rows) : IArtifactStore
    {
        public string Put(Artifact artifact) => artifact.Id;
        public Artifact? Get(string artifactId) => rows.FirstOrDefault(a => a.Id == artifactId);
        public IReadOnlyList<Artifact> ForMission(string missionId, int limit = 200) => rows;
        public IReadOnlyList<Artifact> ForMission(string missionId, string schema, int limit = 200) =>
            rows.Where(a => a.Schema == schema).ToList();
        public IReadOnlyList<Artifact> SourcesOf(string artifactId) => [];
        public IReadOnlyList<Artifact> ConsumersOf(string artifactId) => [];
        public void RecordConsumption(ArtifactConsumption consumption) { }
        public IReadOnlyList<ArtifactConsumption> ConsumptionsOf(string artifactId) => [];
        public IReadOnlyList<ArtifactConsumption> ConsumptionsForMission(string missionId, int limit = 500) => [];
    }

    private static Artifact Make(string id, ArtifactVisibility visibility,
        string schema = "research_brief", string? payload = null) => new()
    {
        Id = id,
        MissionId = "m1",
        Schema = schema,
        ProducerRole = "researcher",
        ContentHash = "sha256:x",
        Visibility = visibility,
        Payload = payload ?? SecretPayload,
    };

    // -------------------------------------------------------------------------------------------
    // The allowlist rule
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void SecretIsNotModelReadable() =>
        Assert.False(Make("a1", ArtifactVisibility.Secret).IsModelReadable);

    [Fact]
    public void ColonyAndOperatorAre()
    {
        Assert.True(Make("a1", ArtifactVisibility.Colony).IsModelReadable);
        Assert.True(Make("a2", ArtifactVisibility.Operator).IsModelReadable);
    }

    /// <summary>
    /// The corrupt-visibility case, and why the check is an allowlist. A row whose visibility
    /// column held garbage coerces to Secret at the store's read boundary; an in-memory value can
    /// hold an out-of-range enum too, and `!= Secret` would have read it as readable.
    /// </summary>
    [Fact]
    public void AnOutOfRangeVisibility_FailsClosed() =>
        Assert.False(Make("a1", (ArtifactVisibility)999).IsModelReadable);

    // -------------------------------------------------------------------------------------------
    // The render boundary: mission-wide (prioritized) blocks
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void APrioritizedSecretArtifact_NeverEntersTheBlock()
    {
        var store = new StubArtifactStore(
        [
            Make("open1", ArtifactVisibility.Colony, payload: "harmless notes"),
            Make("sec1", ArtifactVisibility.Secret),
        ]);

        var block = ArtifactContext.Compile(store, "m1", maxTotalChars: 8000);

        Assert.Contains("harmless notes", block);
        Assert.DoesNotContain(SecretPayload, block);
        Assert.DoesNotContain("sec1", block);   // undeclared secrets are not advertised either
    }

    // -------------------------------------------------------------------------------------------
    // Declared inputs: withheld, never silently dropped, never shown
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void ADeclaredSecretInput_IsWithheldByName_AndItsPayloadNeverAppears()
    {
        var store = new StubArtifactStore(
        [
            Make("open1", ArtifactVisibility.Colony, payload: "harmless notes"),
            Make("sec1", ArtifactVisibility.Secret),
        ]);

        var block = ArtifactContext.Compile(store, "m1", maxTotalChars: 8000,
            declaredInputIds: ["open1", "sec1"]);

        Assert.Contains("harmless notes", block);
        Assert.DoesNotContain(SecretPayload, block);
        // Reported, not silently dropped: the consumer must know a promised premise was withheld.
        Assert.Contains("WITHHELD", block);
        Assert.Contains("sec1", block);
    }

    /// <summary>Even when EVERY declared input is secret, the block says so rather than vanishing.</summary>
    [Fact]
    public void AllInputsSecret_StillProducesTheWithheldReport()
    {
        var store = new StubArtifactStore([Make("sec1", ArtifactVisibility.Secret)]);

        var block = ArtifactContext.Compile(store, "m1", maxTotalChars: 8000,
            declaredInputIds: ["sec1"]);

        Assert.Contains("WITHHELD", block);
        Assert.DoesNotContain(SecretPayload, block);
    }

    // -------------------------------------------------------------------------------------------
    // The soldier's direct read — the "check again at every direct consumer" rule
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void TheSoldiersDirectRead_AppliesTheSameCheck()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Agents", "SpecialistAnts.cs")));

        var read = source.IndexOf("ReadPatchSetArtifacts", StringComparison.Ordinal);
        Assert.True(read >= 0, "the soldier's artifact read is no longer recognisable");
        var body = source[read..Math.Min(source.Length, read + 2500)];

        Assert.Contains("IsModelReadable", body);
        Assert.Contains("WITHHELD", body);
    }

    /// <summary>
    /// And the contract's own words are enforced where they are written: the enum doc says
    /// "never sent to a model", and the compiler is the place every model context is assembled —
    /// so the filter must live there, not in a caller that could be bypassed.
    /// </summary>
    [Fact]
    public void TheCompilerItself_FiltersOnReadability()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Domain", "ArtifactContext.cs")));

        var compile = source.IndexOf("public static string Compile", StringComparison.Ordinal);
        Assert.True(compile >= 0);
        var body = source[compile..Math.Min(source.Length, compile + 4000)];
        Assert.Contains("IsModelReadable", body);
    }
}
