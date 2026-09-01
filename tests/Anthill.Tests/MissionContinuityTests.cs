using Anthill.Core.Memory;
using Anthill.Core.Outcomes;
using Anthill.Core.Tools;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// WORK BUILDS ON VERIFIED WORK, AND THE RECORD SAYS WHOSE. v0.3.8.106, PLAN.md §2b `.106`.
///
/// THE EXIT GATE'S SECOND CLAUSE: "a second mission consumes the first's verified artifact by id."
///
/// WHAT WAS MISSING. The artifact store has had a cross-mission reach since it existed —
/// `IArtifactStore.Get(id)` — and nothing a mission could DISPATCH reached it. So every mission
/// started from nothing: a question about last week's audit was answered by running the audit
/// again and hoping the two agreed.
///
/// AND THE LEDGER COULD NOT HAVE RECORDED IT. `ArtifactConsumption.MissionId` has always been
/// written as the artifact's PRODUCING mission, and the only caller read within a single mission —
/// so "who produced it" and "who read it" were the same value, and the column meant both by
/// coincidence rather than by design. Cross-mission consumption is exactly where that coincidence
/// ends: without `ConsumerMissionId` the second mission's ledger would show nothing, and the claim
/// this release makes would be unprovable from the record it is supposed to be provable from.
///
/// THE GATE IS LINEAGE, NOT PERMISSION. An artifact from a mission that failed, stopped, or is
/// still waiting on an operator is a record of work whose own colony declined to stand behind it.
/// Reading it would launder an ungraded result into an input and give the second mission's answer a
/// confidence the first never earned.
/// </summary>
public class MissionContinuityTests : IDisposable
{
    private readonly string _dir;

    public MissionContinuityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-cont-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private SqliteMemory Memory() => new(Path.Combine(_dir, $"c-{Guid.NewGuid():N}.db"));

    /// <summary>A finished mission holding one artifact, graded as the caller asks.</summary>
    private static (string MissionId, string ArtifactId) PriorMission(
        SqliteMemory memory, string outcome, string payload = """{"finding":"twelve executable roles"}""")
    {
        var mission = new Anthill.Core.Domain.Mission { Goal = "Audit the roster." };
        memory.SaveMission(mission);

        var id = ((IArtifactStore)memory).Put(Artifact.Create(
            schema: ArtifactSchemas.DeliverableLedger, producerRole: "queen",
            missionId: mission.Id, payload: payload));

        memory.SaveMissionEvaluation(new MissionEvaluation(
            MissionId: mission.Id, OutcomeCode: outcome,
            StructuralStatus: "complete", VerificationStatus: "passed", DeliverableStatus: "satisfied",
            StopReason: null, EvaluatorVersion: MissionEvaluator.Version,
            EvaluatedAt: AnthillTime.NowUtc().ToIso(),
            Explanation: "test fixture"));

        return (mission.Id, id);
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>THE POSITIVE. A verified mission's artifact is readable by id, payload and all.</summary>
    [Fact]
    public void AVerifiedMissionsArtifact_IsReadableById()
    {
        using var memory = Memory();
        var (_, artifactId) = PriorMission(memory, MissionOutcome.CompletedVerified);

        var result = new ReadArtifactTool(memory).Run(
            new Dictionary<string, object?> { ["artifact_id"] = artifactId });

        Assert.True(result.Success, result.Error);
        Assert.Contains("twelve executable roles", result.Output, StringComparison.Ordinal);
        Assert.Contains(ArtifactSchemas.DeliverableLedger, result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE NEGATIVES THAT GIVE IT MEANING. Every outcome that is not a verified success is refused,
    /// including the two `.105` added — a mission still waiting on an operator has not finished,
    /// and one that stopped on a repeated failure produced its artifact on the way to giving up.
    /// </summary>
    [Theory]
    [InlineData(MissionOutcome.CompletedUnverified)]
    [InlineData(MissionOutcome.Partial)]
    [InlineData(MissionOutcome.FailedPermanent)]
    [InlineData(MissionOutcome.Escalated)]
    [InlineData(MissionOutcome.Cancelled)]
    [InlineData(MissionOutcome.WaitingForApproval)]
    [InlineData(MissionOutcome.BlockedMissingCapability)]
    public void AnUnverifiedMissionsArtifact_IsRefused(string outcome)
    {
        using var memory = Memory();
        var (_, artifactId) = PriorMission(memory, outcome);

        var result = new ReadArtifactTool(memory).Run(
            new Dictionary<string, object?> { ["artifact_id"] = artifactId });

        Assert.False(result.Success);
        Assert.Equal(FailureClass.PolicyDenial, result.Failure);
        Assert.Contains(outcome, result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("twelve executable roles", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND AN UNGRADED MISSION IS REFUSED, not assumed good. Every mission that predates canonical
    /// evaluation and every one still running is in this state — absence of a grade is not a pass.
    /// </summary>
    [Fact]
    public void AnUngradedMissionsArtifact_IsRefused()
    {
        using var memory = Memory();
        var mission = new Anthill.Core.Domain.Mission { Goal = "still running" };
        memory.SaveMission(mission);
        var artifactId = ((IArtifactStore)memory).Put(Artifact.Create(
            schema: ArtifactSchemas.DeliverableLedger, producerRole: "queen",
            missionId: mission.Id, payload: "{}"));

        var result = new ReadArtifactTool(memory).Run(
            new Dictionary<string, object?> { ["artifact_id"] = artifactId });

        Assert.False(result.Success);
        Assert.Equal(FailureClass.PolicyDenial, result.Failure);
    }

    [Fact]
    public void AnUnknownArtifactId_IsAValidationFailure()
    {
        using var memory = Memory();
        var result = new ReadArtifactTool(memory).Run(
            new Dictionary<string, object?> { ["artifact_id"] = "art_nope" });

        Assert.False(result.Success);
        Assert.Equal(FailureClass.ValidationFailure, result.Failure);
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE LEDGER DISTINGUISHES WHO PRODUCED FROM WHO READ, which is the defect underneath this
    /// slice and the reason the exit gate's claim is checkable at all.
    /// </summary>
    [Fact]
    public void TheLedger_RecordsWhoReadAsWellAsWhoProduced()
    {
        using var memory = Memory();
        var (producerId, artifactId) = PriorMission(memory, MissionOutcome.CompletedVerified);

        var second = new Anthill.Core.Domain.Mission { Goal = "Summarise last week's audit." };
        memory.SaveMission(second);

        var artifact = ((IArtifactStore)memory).Get(artifactId)!;
        ((IArtifactStore)memory).RecordConsumption(new ArtifactConsumption
        {
            ArtifactId = artifact.Id, ContentHash = artifact.ContentHash, Schema = artifact.Schema,
            MissionId = artifact.MissionId, ConsumerMissionId = second.Id,
            ConsumerRole = "researcher", ConsumerTaskId = "t-1",
        });

        var row = Assert.Single(memory.ConsumptionsByMission(second.Id));
        Assert.Equal(producerId, row.MissionId);
        Assert.Equal(second.Id, row.ConsumerMissionId);
        Assert.Equal(second.Id, row.ReadBy);
        Assert.True(row.IsCrossMission);

        // The PRODUCING mission's own ledger query is unchanged — `.98`'s assessment objective
        // reads it, and this release had no reason to alter that grading.
        Assert.Single(((IArtifactStore)memory).ConsumptionsForMission(producerId));
    }

    /// <summary>
    /// A SAME-MISSION READ IS NOT CROSS-MISSION, and a legacy row with no consumer recorded reads
    /// as the producing mission rather than as nothing. Without this, `ReadBy` would silently
    /// reclassify every row written before this release.
    /// </summary>
    [Fact]
    public void ASameMissionRead_IsNotCrossMission()
    {
        using var memory = Memory();
        var (missionId, artifactId) = PriorMission(memory, MissionOutcome.CompletedVerified);
        var artifact = ((IArtifactStore)memory).Get(artifactId)!;

        // Written the way `ArtifactContext` has always written one: no consumer mission at all.
        ((IArtifactStore)memory).RecordConsumption(new ArtifactConsumption
        {
            ArtifactId = artifact.Id, ContentHash = artifact.ContentHash, Schema = artifact.Schema,
            MissionId = artifact.MissionId, ConsumerRole = "verifier", ConsumerTaskId = "t-9",
        });

        var row = Assert.Single(memory.ConsumptionsByMission(missionId));
        Assert.Null(row.ConsumerMissionId);
        Assert.Equal(missionId, row.ReadBy);
        Assert.False(row.IsCrossMission);
    }

    /// <summary>
    /// THE CHOKEPOINT RECORDS THE READ, NOT THE TOOL.
    ///
    /// Source-shape, and the property is about IDENTITY rather than behaviour. The tool would have
    /// to take its consumer mission from an argument, which means from the model — and a model that
    /// can name the mission it read on behalf of can attribute its reads to a mission that never
    /// made them. The dispatch frame has the mission, the task and the role as facts. A behavioural
    /// test cannot see the difference; this can.
    /// </summary>
    [Fact]
    public void TheConsumerIdentity_ComesFromTheChokepointAndNotTheModel()
    {
        var tool = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Tools", "ReadArtifactTool.cs")));
        var registry = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Tools", "Tools.cs")));

        Assert.DoesNotContain("RecordConsumption", tool, StringComparison.Ordinal);
        Assert.DoesNotContain("mission_id", tool, StringComparison.Ordinal);
        Assert.Contains("ConsumerMissionId = missionId", registry, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE TOOL IS REGISTERED EVERYWHERE A TOOL HAS TO BE. `.103` shipped a class whose tools
    /// three separate guards refused for want of exactly this, and the guards were right — a tool
    /// missing from any one of these tables is authorized to dispatch nothing, which reads as a
    /// weak model rather than as a missing registration.
    /// </summary>
    [Fact]
    public void TheTool_IsRegisteredAcrossEveryTable()
    {
        Assert.Contains(ReadArtifactTool.ToolName, ToolInventory.Implemented);

        // The SDK's own mirror, read as source. `SafetyPolicy`'s built-in table is internal and is
        // replaced by the core's at composition — asserting through the live policy would compare
        // `ToolInventory.Implemented` with itself. What matters is the SDK's fallback list, which
        // is what answers in a process that never loaded the core, and it is a literal there
        // because the SDK may not name Core.
        Assert.Contains($"\"{ReadArtifactTool.ToolName}\"", File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.SDK", "Common", "SafetyPolicy.cs")),
            StringComparison.Ordinal);

        var contract = Anthill.Core.Agents.AntExecutionCatalog.ContractFor("researcher");
        Assert.NotNull(contract);
        Assert.Contains(ReadArtifactTool.ToolName, contract!.AllowedTools, StringComparer.OrdinalIgnoreCase);
    }
}
