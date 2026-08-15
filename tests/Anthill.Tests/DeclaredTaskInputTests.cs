using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A task receives the artifacts it was GIVEN, not everything the mission happens to hold. v0.3.8.57.
///
/// `ArtifactContext.Compile` has shipped since v3.8.29 and it is bounded, ordered and honest about
/// truncation — but it answers one question for every caller: "what typed artifacts does this
/// MISSION have?" So a tester received the `ui_map` a cartographer wrote for an unrelated step, and
/// a researcher received the `patch_set`. Meanwhile `AntExecutionContract.RequiredInputArtifactTypes`
/// declared what each role needs and NOTHING populated it — declared and unreachable, the defect
/// class this program keeps finding.
///
/// The change is narrow on purpose. `Task.InputArtifactIds` is the authoritative list when it is
/// non-empty; when it is empty every worker gets exactly the block it got before. Narrowing by
/// guesswork would remove context a worker legitimately used, which is a worse failure than sending
/// too much, so only the ONE insertion point with an unambiguous producer populates it: the policy
/// review inserted the statement after its patch set was written.
///
/// WHAT THESE TESTS ARE FOR. Adding a `List&lt;string&gt;` to a record proves nothing. Three
/// boundaries can silently drop it and each has been the actual bug in an earlier release:
/// the database (no column — written to memory, gone on read), `DeepCopy` (ants receive a copy, so
/// a field set on the original and absent from the copy is a silent fallback), and the dispatch call
/// site (the parameter is optional, so a caller that never passes it compiles and does nothing).
/// Every test below crosses one of those.
/// </summary>
public class DeclaredTaskInputTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_inputs_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteMemory Memory()
    {
        Directory.CreateDirectory(_dir);
        return new SqliteMemory(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
    }

    private static string Put(IArtifactStore store, string missionId, string schema, string producer, string payload) =>
        store.Put(Artifact.Create(schema: schema, producerRole: producer, missionId: missionId, payload: payload));

    // -------------------------------------------------------------------------------------------
    // Compilation
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Declared inputs are the WHOLE block. The mission has three artifacts; the task named one.
    /// </summary>
    [Fact]
    public void DeclaredInputs_AreTheOnlyArtifactsCompiled()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "declared inputs" });

        var patch = Put(store, "m1", ArtifactSchemas.PatchSet, "coder", "THE-PATCH-UNDER-REVIEW");
        Put(store, "m1", ArtifactSchemas.UiMap, "ui_cartographer", "AN-UNRELATED-UI-MAP");
        Put(store, "m1", ArtifactSchemas.SourceSet, "researcher", "SOME-OTHER-SOURCES");

        var block = ArtifactContext.Compile(store, "m1", 20_000, declaredInputIds: new[] { patch });

        Assert.Contains("THE-PATCH-UNDER-REVIEW", block);
        Assert.DoesNotContain("AN-UNRELATED-UI-MAP", block);
        Assert.DoesNotContain("SOME-OTHER-SOURCES", block);
        // And the worker is told which kind of block it is holding: "these are yours" reads
        // differently from "here is everything the mission has".
        Assert.Contains("DECLARED INPUTS", block);
    }

    /// <summary>
    /// The schema priority list RANKS a mission-wide block. Applying it to a declared input would
    /// silently drop something the runtime deliberately supplied — so it is not applied.
    /// </summary>
    [Fact]
    public void ADeclaredInput_IsCompiledEvenWhenItsSchemaIsNotOnThePriorityList()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "off-list schema" });

        // Deliberately a schema the mission-wide path filters out.
        var odd = Put(store, "m1", "some_unlisted_schema", "scribe", "OFF-LIST-BUT-DECLARED");

        Assert.DoesNotContain("OFF-LIST-BUT-DECLARED", ArtifactContext.Compile(store, "m1", 20_000));
        Assert.Contains("OFF-LIST-BUT-DECLARED",
            ArtifactContext.Compile(store, "m1", 20_000, declaredInputIds: new[] { odd }));
    }

    /// <summary>
    /// Order is the order declared, not the store's ranking. The caller said which matters most.
    /// </summary>
    [Fact]
    public void DeclaredInputs_AppearInTheOrderDeclared()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "ordering" });

        // `patch_set` outranks `source_set` on the mission-wide priority list, so if the list were
        // still being applied the two would come back the other way round.
        var patch = Put(store, "m1", ArtifactSchemas.PatchSet, "coder", "PATCH-PAYLOAD");
        var sources = Put(store, "m1", ArtifactSchemas.SourceSet, "researcher", "SOURCES-PAYLOAD");

        var block = ArtifactContext.Compile(store, "m1", 20_000, declaredInputIds: new[] { sources, patch });

        Assert.True(block.IndexOf("SOURCES-PAYLOAD", StringComparison.Ordinal)
                  < block.IndexOf("PATCH-PAYLOAD", StringComparison.Ordinal),
            "declared order is the caller's instruction; re-ranking it discards that instruction");
    }

    /// <summary>
    /// A declared input that is not in the store is REPORTED, not skipped.
    ///
    /// "I was given nothing" and "I was given two things and one could not be found" lead to
    /// different work, and a worker that cannot tell them apart will confidently review half a
    /// change. The notice is emitted BEFORE the artifacts so a worker that stops reading early
    /// still sees it.
    /// </summary>
    [Fact]
    public void AMissingDeclaredInput_IsReportedRatherThanSilentlyDropped()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "missing input" });

        var patch = Put(store, "m1", ArtifactSchemas.PatchSet, "coder", "REAL-PAYLOAD");

        var block = ArtifactContext.Compile(store, "m1", 20_000,
            declaredInputIds: new[] { patch, "art-does-not-exist" });

        Assert.Contains("REAL-PAYLOAD", block);
        Assert.Contains("NOT FOUND", block);
        Assert.Contains("art-does-not-exist", block);
        Assert.True(block.IndexOf("NOT FOUND", StringComparison.Ordinal)
                  < block.IndexOf("REAL-PAYLOAD", StringComparison.Ordinal),
            "the absence notice must precede the artifacts, or a truncated read misses it");
    }

    /// <summary>
    /// Every declared id missing still produces a block. Returning "" here would be the silent
    /// failure this whole field exists to remove: the worker would receive nothing and have no way
    /// to know it had been promised anything.
    /// </summary>
    [Fact]
    public void AllDeclaredInputsMissing_StillSaysSo()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "all missing" });
        Put(store, "m1", ArtifactSchemas.PatchSet, "coder", "NOT-DECLARED");

        var block = ArtifactContext.Compile(store, "m1", 20_000, declaredInputIds: new[] { "nope-1", "nope-2" });

        Assert.Contains("NOT FOUND", block);
        Assert.Contains("nope-1", block);
        Assert.Contains("nope-2", block);
        Assert.DoesNotContain("NOT-DECLARED", block);
    }

    /// <summary>
    /// A declared input is reported missing even when the store holds NOTHING.
    ///
    /// The regression this pins: the empty-store shortcut ran before the declared-input handling, so
    /// `Compile` returned "" and the worker was told nothing. Every other test here seeded at least
    /// one artifact and sailed past it — the case it broke is the one where a declared input is most
    /// likely to be absent, which is a mission whose store is empty.
    /// </summary>
    [Fact]
    public void AMissingDeclaredInput_IsReportedEvenWhenTheMissionHasNoArtifactsAtAll()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "empty store" });

        var block = ArtifactContext.Compile(store, "m1", 20_000, declaredInputIds: new[] { "art-gone" });

        Assert.Contains("NOT FOUND", block);
        Assert.Contains("art-gone", block);
    }

    /// <summary>
    /// And an empty store with NOTHING declared still produces nothing. The shortcut is narrowed,
    /// not removed — a mission with no artifacts should not gain a header announcing that.
    /// </summary>
    [Fact]
    public void AnEmptyStoreWithNothingDeclared_StillCompilesToNothing()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "empty store" });

        Assert.Equal("", ArtifactContext.Compile(store, "m1", 20_000));
    }

    /// <summary>
    /// No declared inputs means the behaviour every existing task had. This is the compatibility
    /// claim the whole design rests on, so it is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void NoDeclaredInputs_CompilesTheMissionWideBlockUnchanged()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "fallback" });
        Put(store, "m1", ArtifactSchemas.PatchSet, "coder", "PATCH-PAYLOAD");
        Put(store, "m1", ArtifactSchemas.UiMap, "ui_cartographer", "UI-PAYLOAD");

        foreach (var block in new[]
                 {
                     ArtifactContext.Compile(store, "m1", 20_000),
                     ArtifactContext.Compile(store, "m1", 20_000, declaredInputIds: Array.Empty<string>()),
                 })
        {
            Assert.Contains("PATCH-PAYLOAD", block);
            Assert.Contains("UI-PAYLOAD", block);
            Assert.Contains("TYPED ARTIFACTS", block);
            Assert.DoesNotContain("DECLARED INPUTS", block);
        }
    }

    /// <summary>
    /// The budget still binds. A declared input is not permission to exceed what the caller allowed
    /// — a context packet that quietly doubles is how a dispatch starts failing on token limits.
    /// </summary>
    [Fact]
    public void DeclaredInputs_StillRespectTheBudget()
    {
        using var memory = Memory();
        var store = (IArtifactStore)memory;
        memory.SaveMission(new Mission { Id = "m1", Goal = "budget" });

        var a = Put(store, "m1", ArtifactSchemas.PatchSet, "coder", new string('a', 4_000));
        var b = Put(store, "m1", ArtifactSchemas.PatchSet, "coder", new string('b', 4_000));

        var block = ArtifactContext.Compile(store, "m1", 900, declaredInputIds: new[] { a, b });

        Assert.True(block.Length <= 900 + 200, $"block was {block.Length} characters against a 900 budget");
        Assert.Contains("omitted for space", block);
    }

    // -------------------------------------------------------------------------------------------
    // The boundaries that silently drop fields
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The database round trip. Without a column the field is set in memory and gone on read, and
    /// a replay could not reconstruct what a worker consumed — which is the entire claim.
    /// </summary>
    [Fact]
    public void InputArtifactIds_SurviveTheDatabase()
    {
        using var memory = Memory();
        memory.SaveMission(new Mission { Id = "m1", Goal = "round trip" });
        memory.SaveTask("m1", new Task
        {
            Id = "t1", Title = "review", Description = "d", AssignedAnt = "soldier",
            TaskType = "security_review", InputArtifactIds = new List<string> { "art-1", "art-2" },
        });

        var row = memory.GetTasksForMission("m1").Single(r => (string?)r["id"] == "t1");
        var stored = row["input_artifact_ids_json"]?.ToString() ?? "";

        Assert.Contains("art-1", stored);
        Assert.Contains("art-2", stored);
    }

    /// <summary>
    /// And the stored record is REACHABLE. Stored-but-unqueryable is the same defect wearing a
    /// different hat: the task graph is where an operator answers "what was this review looking at".
    /// </summary>
    [Fact]
    public void InputArtifactIds_AppearOnTheTaskGraphNode()
    {
        var source = File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "Queen.Views.cs"));

        // The NODE KEY, not merely the column name: reading the column and not publishing it is
        // exactly the failure this asserts against.
        Assert.Contains("[\"input_artifact_ids\"] =", SourceText.CodeOnly(source));
    }

    /// <summary>
    /// `DeepCopy` is what an ant actually receives. A field set on the original and absent from the
    /// copy is not a compile error and not a test failure anywhere else — it is a silent, permanent
    /// fallback to mission-wide context.
    /// </summary>
    [Fact]
    public void InputArtifactIds_SurviveDeepCopy()
    {
        var original = new Task
        {
            Id = "t1", Title = "review", Description = "d", AssignedAnt = "soldier",
            InputArtifactIds = new List<string> { "art-1" },
        };

        var copy = original.DeepCopy();

        Assert.Equal(new[] { "art-1" }, copy.InputArtifactIds);
        // A copy, not the same list: mutating one task's inputs must not reach through the snapshot.
        copy.InputArtifactIds.Add("art-2");
        Assert.Single(original.InputArtifactIds);
    }

    /// <summary>
    /// The dispatch call sites. `declaredInputIds` is an OPTIONAL parameter, so every one of the
    /// three context builders compiles unchanged while passing nothing — the field would be
    /// persisted, visible in the graph, and read by no worker.
    /// </summary>
    [Fact]
    public void EveryContextPacketCallSite_PassesTheTasksDeclaredInputs()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Agents", "Ants.cs")));

        var calls = System.Text.RegularExpressions.Regex.Matches(source, @"BuildContextPacketText\(");
        var wired = System.Text.RegularExpressions.Regex.Matches(source, @"declaredInputIds:\s*task\.InputArtifactIds");

        Assert.True(calls.Count > 0, "no context packet call sites found — this guard has stopped guarding anything");
        Assert.Equal(calls.Count, wired.Count);
    }

    // -------------------------------------------------------------------------------------------
    // The one populated producer, and its consumer
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The policy-inserted review names the patch set it exists to review.
    ///
    /// This is the only place in the colony where the producer is unambiguous — the patch artifact
    /// was written one statement earlier — and until this release its id was discarded, which is
    /// why the soldier had to go looking for "the mission's patch sets" instead.
    /// </summary>
    [Fact]
    public void ThePolicyInsertedReview_DeclaresThePatchArtifactItWasCreatedFor()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));

        // The PROPERTY, not the argument list. The first cut of this pinned both calls verbatim and
        // broke the moment RecordPatchArtifact gained an environment-fingerprint parameter for an
        // unrelated reason — a guard that fails on a spelling change is a guard that will be edited
        // to match rather than read, and next time the thing it protects may really have broken.
        //
        // What actually matters is the chain: the id is captured, it reaches the insertion, and it
        // lands on the task.
        Assert.Matches(@"=\s*RecordPatchArtifact\(", source);
        Assert.Matches(@"InsertPolicyReviewTasks\([^)]*patchArtifactId\)", source);
        Assert.Contains("InputArtifactIds = patchArtifactId", source);
    }

    /// <summary>
    /// The soldier reads what it was given. Reviewing every patch set a long mission accumulated
    /// makes "the review passed" a claim about material nobody asked about — and the count it
    /// reports would say "3 read" without saying which three.
    /// </summary>
    [Fact]
    public void TheSoldier_ReviewsItsDeclaredInputsRatherThanEveryPatchSetInTheMission()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Agents", "SpecialistAnts.cs")));

        Assert.Contains("task.InputArtifactIds.Count > 0", source);
        Assert.Contains("_artifacts.Get(id)", source);
        // The mission-wide read stays for planner-written reviews, which have no way to name a set.
        Assert.Contains("_artifacts.ForMission(mission.Id, Anthill.SDK.Artifacts.ArtifactSchemas.PatchSet)", source);
    }
}
