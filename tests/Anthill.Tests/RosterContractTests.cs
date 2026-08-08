using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Anthill.Core.Verification;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Every mission role declares itself, and the declaration matches what the handler does. v3.8.23.
///
/// Until this release the contract table held six of twelve roles. The six it held were the
/// SPECIALISTS — the ones behind rollout gates, doing no work in a default install. The six it did
/// not hold were the core ants that do nearly everything, including the coder, which is the only
/// role in the colony that produces changes to source code.
///
/// The reason the gap survived is worth more than the gap: <c>AntExecutorCatalog.Initialize</c>
/// checked for a missing contract only when <c>isSpecialist</c> was true, so for the six roles that
/// had no contract the check that would have reported it did not apply. The absence was invisible to
/// the runtime that was supposed to fail closed on it.
/// </summary>
public class RosterContractTests
{
    private static readonly string[] TwelveRoles =
    {
        "researcher", "web", "file", "coder", "builder", "verifier",
        "ui_cartographer", "tester", "soldier", "scribe", "medic", "archivist",
    };

    [Fact]
    public void AllTwelveMissionRoles_HaveAnExecutionContract()
    {
        var missing = TwelveRoles.Where(r => AntExecutionCatalog.ContractFor(r) is null).ToList();

        Assert.True(missing.Count == 0,
            "These mission roles have no execution contract, so nothing constrains what they may "
            + "dispatch or what task types they accept: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The prohibition that matters most, asserted across the whole roster rather than per role.
    /// No mission ant may apply a patch, write a file, or run a shell — the Queen's approval
    /// pipeline is the only path to the operator's tree.
    /// </summary>
    [Theory]
    [InlineData("apply_patch")]
    [InlineData("write_text_file")]
    [InlineData("shell_command")]
    public void NoMissionRole_MayDispatchAWorldChangingTool(string forbidden)
    {
        var granted = TwelveRoles
            .Where(r => AntExecutionCatalog.ContractFor(r)?.AllowedTools.Contains(forbidden) == true)
            .ToList();

        Assert.True(granted.Count == 0,
            $"'{forbidden}' is allowed to: {string.Join(", ", granted)}");
    }

    /// <summary>
    /// A contract's allowlist is what the ROLE MAY dispatch, and after v3.8.23 it short-circuits the
    /// legacy role table — so an allowlist narrower than what the handler actually calls turns into
    /// a runtime denial rather than a compile error. These are the calls each core handler really
    /// makes, read out of the source when the contracts were written.
    /// </summary>
    [Theory]
    [InlineData("researcher", "system_info")]
    [InlineData("researcher", "list_directory")]
    [InlineData("web", "web_search")]
    [InlineData("file", "list_directory")]
    [InlineData("file", "read_text_file")]
    [InlineData("tester", "run_allowlisted_check")]
    public void EachHandlersRealToolCalls_AreInItsContract(string role, string tool) =>
        Assert.Contains(tool, AntExecutionCatalog.ContractFor(role)!.AllowedTools);

    /// <summary>
    /// The coder has NO tools, deliberately, and this is the assertion that keeps it that way. It
    /// proposes; the Queen materialises and the operator approves. Granting it a tool that touches
    /// the tree would collapse three steps into one performed by the least accountable component.
    /// </summary>
    [Fact]
    public void TheCoder_DispatchesNothing()
    {
        var coder = AntExecutionCatalog.ContractFor("coder")!;

        Assert.Empty(coder.AllowedTools);
        Assert.True(coder.ProducesPatchProposals);
        Assert.Contains("patch_set", coder.ProducedArtifactTypes);
    }

    /// <summary>
    /// The four roles that were never really planner-selectable now say so. Before v3.8.23 the
    /// planner could put a medic in a plan before anything had failed — and <c>MedicAnt.Execute</c>
    /// opens by returning Blocked in exactly that case, a handler defending itself against its own
    /// scheduler.
    /// </summary>
    [Theory]
    [InlineData("tester", SchedulingMode.PolicyInserted)]
    [InlineData("soldier", SchedulingMode.PolicyInserted)]
    [InlineData("medic", SchedulingMode.FailureTriggered)]
    [InlineData("archivist", SchedulingMode.PostFinalization)]
    public void TheNonPlannableRoles_DeclareHowTheyAreActuallyScheduled(string role, SchedulingMode expected) =>
        Assert.Equal(expected, AntExecutionCatalog.ContractFor(role)!.Scheduling);

    /// <summary>The roles that do the mission's work stay planner-selectable — the default is not
    /// vacuous, and a release that made everything policy-inserted would pass the test above.</summary>
    [Theory]
    [InlineData("researcher")]
    [InlineData("coder")]
    [InlineData("builder")]
    public void TheWorkingRoles_RemainPlannerSelectable(string role) =>
        Assert.Equal(SchedulingMode.PlannerSelectable, AntExecutionCatalog.ContractFor(role)!.Scheduling);

    /// <summary>
    /// Declared task types must cover what the planner actually emits, or the contract check in each
    /// handler rejects real work. These are the strings in <c>Planner</c>'s prompt and its
    /// deterministic fallback plan — the same "test against the producer's vocabulary" rule that
    /// v3.8.22 added after the verification defect.
    /// </summary>
    [Theory]
    [InlineData("researcher", "research")]
    [InlineData("web", "external_research")]
    [InlineData("file", "file_inspection")]
    [InlineData("coder", "patch_proposal")]
    [InlineData("builder", "build_answer")]
    [InlineData("verifier", "verification")]
    [InlineData("ui_cartographer", "ui_mapping")]
    public void TheTaskTypesThePlannerEmits_AreSupportedByTheAssignedRole(string role, string taskType) =>
        Assert.True(AntExecutionCatalog.ContractFor(role)!.SupportsTaskType(taskType),
            $"the planner assigns '{taskType}' to {role}, whose contract rejects it");
}

/// <summary>
/// A patch set is verified in a tree that CONTAINS it. v3.8.23.
///
/// v3.8.22 made BuildVerifier run on every patch and pointed it at the primary workspace, which does
/// not contain the patch. The build verdicts were true statements about the wrong tree, and nothing
/// in the store could tell them apart from true statements about the right one.
/// </summary>
public class PatchSetMaterializerTests : IDisposable
{
    private readonly string _source = Path.Combine(Path.GetTempPath(), "anthill-mat-" + Guid.NewGuid().ToString("N")[..8]);

    public PatchSetMaterializerTests()
    {
        Directory.CreateDirectory(Path.Combine(_source, "src"));
        File.WriteAllText(Path.Combine(_source, "src", "Existing.cs"), "original contents");
    }

    public void Dispose()
    {
        try { Directory.Delete(_source, recursive: true); } catch { }
    }

    private static PatchSet SetWith(params PatchProposal[] proposals)
    {
        var set = new PatchSet { Summary = "test" };
        set.Proposals.AddRange(proposals);
        return set;
    }

    /// <summary>The contents the fixture writes into every existing source file.</summary>
    private const string Original = "original contents";

    /// <summary>
    /// A modify proposal in the shape a real coder emits.
    ///
    /// v3.8.32 — this helper used to omit <c>OldContent</c> entirely, and the materializer accepted
    /// it because the materializer ignored <c>OldContent</c> too. Both halves were wrong together, so
    /// every test here passed while describing a patch the operator's applier would have refused:
    /// <c>ApplyPatchTool</c> has always required exact <c>old_content</c> for a modify, and
    /// <c>CoderAnt</c>'s own prompt instructs the model to supply it.
    ///
    /// A fixture that constructs input in a shape the producer never emits is the same defect the
    /// whole v3.8.32 release is about — it just happened to be in a test rather than in the code.
    /// </summary>
    private static PatchProposal Modify(string path, string content, string oldContent = Original) =>
        new()
        {
            FilePath = path, ChangeType = PatchChangeType.Modify,
            OldContent = oldContent, NewContent = content,
        };

    /// <summary>The patch is in the sandbox, and the primary tree is untouched. Both halves matter:
    /// the first is why verification means anything, the second is why it is safe to run.</summary>
    [Fact]
    public void TheSandboxContainsThePatch_AndTheSourceDoesNot()
    {
        var result = PatchSetMaterializer.Materialize(
            SetWith(Modify("src/Existing.cs", "patched contents")), _source);

        Assert.True(result.Ok, result.Problem);
        using var materialized = result.Materialized!;

        Assert.Equal("patched contents",
            File.ReadAllText(Path.Combine(materialized.Root, "src", "Existing.cs")));
        Assert.Equal("original contents",
            File.ReadAllText(Path.Combine(_source, "src", "Existing.cs")));
    }

    /// <summary>A new file the source has never seen is created in the sandbox.</summary>
    [Fact]
    public void AnAddedFile_AppearsInTheSandbox()
    {
        var result = PatchSetMaterializer.Materialize(SetWith(
            new PatchProposal { FilePath = "src/Added.cs", ChangeType = PatchChangeType.Add, NewContent = "new" }),
            _source);

        Assert.True(result.Ok, result.Problem);
        using var materialized = result.Materialized!;

        Assert.True(File.Exists(Path.Combine(materialized.Root, "src", "Added.cs")));
        Assert.False(File.Exists(Path.Combine(_source, "src", "Added.cs")));
    }

    /// <summary>
    /// The security assertion. A proposal whose path climbs out of the sandbox would write into the
    /// operator's real tree from inside what is supposed to be an isolated verification.
    /// </summary>
    [Fact]
    public void APathThatEscapesTheSandbox_IsRefused()
    {
        var result = PatchSetMaterializer.Materialize(
            SetWith(Modify("../../escaped.cs", "should never be written")), _source);

        Assert.False(result.Ok);
        Assert.Contains("escapes", result.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// FAILS AS A UNIT. A patch set is applied as a unit, so a set with one bad proposal must not be
    /// verified on the strength of the good ones — that would produce a verdict about a tree that
    /// will never exist.
    /// </summary>
    [Fact]
    public void OneBadProposal_AbandonsTheWholeMaterialization()
    {
        var result = PatchSetMaterializer.Materialize(SetWith(
            Modify("src/Existing.cs", "fine"),
            Modify("../../escaped.cs", "not fine")), _source);

        Assert.False(result.Ok);
        Assert.Null(result.Materialized);
    }

    /// <summary>
    /// The hash is over CONTENT, and proposal order is an artifact of how a model emitted them
    /// rather than a property of the change.
    /// </summary>
    [Fact]
    public void ThePatchSetHash_IsIndependentOfProposalOrder()
    {
        var a = SetWith(Modify("src/A.cs", "one"), Modify("src/B.cs", "two"));
        var b = SetWith(Modify("src/B.cs", "two"), Modify("src/A.cs", "one"));

        Assert.Equal(PatchSetMaterializer.HashPatchSet(a), PatchSetMaterializer.HashPatchSet(b));
    }

    /// <summary>...and it changes when the content does, or it would be proving nothing.</summary>
    [Fact]
    public void ThePatchSetHash_ChangesWithTheContent()
    {
        var a = SetWith(Modify("src/A.cs", "one"));
        var b = SetWith(Modify("src/A.cs", "ONE"));

        Assert.NotEqual(PatchSetMaterializer.HashPatchSet(a), PatchSetMaterializer.HashPatchSet(b));
    }

    /// <summary>
    /// The applied-tree hash reads back from DISK, so it describes what the verifier is about to
    /// read rather than what was requested. Two different patches must not share one.
    /// </summary>
    [Fact]
    public void TheAppliedTreeHash_DescribesWhatLanded()
    {
        var first = PatchSetMaterializer.Materialize(SetWith(Modify("src/Existing.cs", "aaa")), _source);
        var second = PatchSetMaterializer.Materialize(SetWith(Modify("src/Existing.cs", "bbb")), _source);

        Assert.True(first.Ok, first.Problem);
        Assert.True(second.Ok, second.Problem);
        using var a = first.Materialized!;
        using var b = second.Materialized!;

        Assert.NotEqual(a.AppliedTreeHash, b.AppliedTreeHash);
    }

    /// <summary>Disposing removes the sandbox — a verification that leaked a full workspace copy per
    /// patch set would fill the disk on a busy colony.</summary>
    [Fact]
    public void DisposingTheMaterialization_RemovesTheSandbox()
    {
        var result = PatchSetMaterializer.Materialize(SetWith(Modify("src/Existing.cs", "x")), _source);
        Assert.True(result.Ok, result.Problem);

        var root = result.Materialized!.Root;
        Assert.True(Directory.Exists(root));

        result.Materialized!.Dispose();

        Assert.False(Directory.Exists(root));
    }

    /// <summary>An empty set is refused rather than materialised into a pointless copy.</summary>
    [Fact]
    public void AnEmptyPatchSet_IsRefused() =>
        Assert.False(PatchSetMaterializer.Materialize(SetWith(), _source).Ok);
}
