using System.Text.RegularExpressions;
using Anthill.Core.Agents;
using Anthill.Core.Memory;
using Anthill.Core.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// ANT EVIDENCE HAS ONE VOCABULARY, AND EVERY SITE USES IT. v0.3.8.94.
///
/// What this closes, and both halves are defect class 9 (a filter that could not match):
/// `FailureContext.Tool` and the persisted `TaskResult.Tool` filtered ant evidence on kind "tool"
/// for six releases while nothing emitted it — both fields null for every task of every mission —
/// and `deterministic_work_completed` tested ant evidence against the VERIFICATION STORE's
/// vocabulary (build / test_run / hash_match), kinds ant evidence has never carried, so half of
/// that expression was dead the day it was written. Bare string kinds at emission and consumption
/// sites are how a promise and its producer drift apart without either failing.
/// </summary>
public class AntEvidenceVocabularyTests
{
    private static IEnumerable<string> SourceFiles() =>
        Directory.GetFiles(Path.Combine(SourceText.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>
    /// NO EMISSION SITE USES A BARE LITERAL. Every `new AntEvidence(...)` names its kind through
    /// the vocabulary, so a typo'd or invented kind is a compile error rather than a row nothing
    /// will ever match.
    ///
    /// Known limitation, stated: a target-typed `new("kind", ...)` inside an evidence list is
    /// invisible to this regex. The tree's only such sites were converted to constants in the same
    /// release; this holds the explicit form, which is every site the sweep could see.
    /// </summary>
    [Fact]
    public void NoEvidenceEmission_UsesABareKindLiteral()
    {
        var offenders = new List<string>();
        var bare = new Regex(@"new AntEvidence\(\s*""");

        foreach (var file in SourceFiles())
            if (bare.IsMatch(SourceText.CodeOnly(File.ReadAllText(file))))
                offenders.Add(Path.GetFileName(file));

        Assert.True(offenders.Count == 0,
            "these files construct AntEvidence with a bare kind literal: "
          + string.Join(", ", offenders)
          + ". Use AntEvidenceKinds — a literal is how \"tool\" got promised to two consumers and "
          + "produced by nobody for six releases.");
    }

    /// <summary>Every declared kind is used somewhere in the tree — a constant nothing references
    /// is the same drift in the other direction.</summary>
    [Fact]
    public void EveryDeclaredKind_IsReferencedInTheTree()
    {
        var corpus = string.Join("\n", SourceFiles().Select(File.ReadAllText));

        foreach (var name in typeof(AntEvidenceKinds)
                     .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                     .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                     .Select(f => f.Name))
            Assert.True(Regex.IsMatch(corpus, $@"AntEvidenceKinds\.{Regex.Escape(name)}\b"),
                $"AntEvidenceKinds.{name} is declared and nothing in src references it. Declare "
              + "only what the code produces or consumes.");
    }

    /// <summary>The two vocabularies stay disjoint — a kind in both would re-open the exact
    /// wrong-witness confusion this file exists to end.</summary>
    [Fact]
    public void TheAntAndStoreVocabularies_AreDisjoint()
    {
        var overlap = AntEvidenceKinds.All
            .Intersect(Anthill.SDK.Artifacts.EvidenceKinds.Reproducible, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.True(overlap.Count == 0,
            "these kinds exist in BOTH the ant and the verification-store vocabularies: "
          + string.Join(", ", overlap)
          + ". A shared name is how a consumer comes to read one witness with the other's meaning.");
    }

    // ---- the producer the consumers waited for --------------------------------------------------

    /// <summary>
    /// THE REGISTRY RECORDS WHICH TOOLS A TASK DISPATCHED — names, distinct, first-use order,
    /// cleared on read, denied dispatches included. This is the record the measurement boundary
    /// turns into kind-"tool" evidence, which is what finally gives `FailureContext.Tool` and
    /// `TaskResult.Tool` something to match.
    /// </summary>
    [Fact]
    public void TheRegistry_RecordsDispatchedToolNames_DistinctAndCleared()
    {
        using var memory = new SqliteMemory(":memory:");
        var registry = new ToolRegistry(memory);

        // Unregistered names still count: the dispatch record is about what the role ATTEMPTED,
        // and it is taken before the lookup for the same reason the count is.
        registry.RunTool("read_text_file", taskId: "t1");
        registry.RunTool("read_text_file", taskId: "t1");
        registry.RunTool("web_search", taskId: "t1");
        registry.RunTool("unrelated_tool", taskId: "t2");

        Assert.Equal(new[] { "read_text_file", "web_search" }, registry.TakeDispatchedTools("t1"));
        Assert.Empty(registry.TakeDispatchedTools("t1"));   // cleared on read, like the count
        Assert.Equal(new[] { "unrelated_tool" }, registry.TakeDispatchedTools("t2"));
        Assert.Empty(registry.TakeDispatchedTools(null));
    }

    /// <summary>The consumers read the same kind the producer writes — pinned at the source level
    /// so the pair cannot drift apart again without failing here.</summary>
    [Fact]
    public void TheToolFilter_AndItsProducer_NameTheSameKind()
    {
        var execution = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));
        var taskResults = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Memory", "SqliteMemory.TaskResults.cs")));

        // The producer, at the measurement boundary…
        Assert.Contains("TakeDispatchedTools", execution);
        Assert.Contains("new AntEvidence(AntEvidenceKinds.Tool", execution);
        // …and both consumers, reading the constant rather than a literal.
        Assert.Contains("e.Kind == AntEvidenceKinds.Tool", execution);
        Assert.Contains("e.Kind == Agents.AntEvidenceKinds.Tool", taskResults);
        Assert.DoesNotContain("e.Kind == \"tool\"", execution);
        Assert.DoesNotContain("e.Kind == \"tool\"", taskResults);
    }

    // ---- the deliberate bridge skips are declared -----------------------------------------------

    /// <summary>
    /// EVERY ANT ARTIFACT KIND IS MAPPED OR DECLARED TRANSPORT-ONLY. `patch_json` fell into the
    /// bridge's null arm beside typos for six releases — deliberately unbridged (the parsed set is
    /// stored by RecordPatchArtifact; bridging the raw JSON would double-store the change), but
    /// indistinguishable from a gap. The decision now has a name, and a kind in neither place
    /// fails here instead of vanishing silently.
    /// </summary>
    [Theory]
    [InlineData("text")]
    [InlineData("patch_json")]
    public void TransportOnlyKinds_AreDeclared_AndDeliberatelyUnbridged(string kind)
    {
        Assert.Contains(kind, Anthill.SDK.Artifacts.ArtifactSchemas.TransportOnly);
        Assert.Null(Anthill.SDK.Artifacts.ArtifactSchemas.ForAntKind(kind));
    }

    /// <summary>The two sets cannot overlap: a kind both mapped and transport-only would give the
    /// bridge two contradictory instructions.</summary>
    [Fact]
    public void NoKind_IsBothMappedAndTransportOnly()
    {
        foreach (var kind in Anthill.SDK.Artifacts.ArtifactSchemas.TransportOnly)
            Assert.Null(Anthill.SDK.Artifacts.ArtifactSchemas.ForAntKind(kind));
    }
}
