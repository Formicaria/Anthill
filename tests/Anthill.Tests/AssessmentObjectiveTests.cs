using Anthill.Core.Missions;
using Anthill.Core.Outcomes;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.98 — THE GATE THAT MISSION 7afd85b2 WOULD HAVE FAILED.
///
/// That mission completed its tasks, inspected nothing, produced no assessment, and was gradeable
/// as complete — because the deliverable layer could read exactly one intent, `FileChange`, and an
/// audit changes no files. So it resolved to `not_applicable` and the entire judgment fell to a
/// verifier model saying "Verification Passed".
///
/// These tests hold the three things an assessment can be held to without a model's opinion, each
/// answered from a record the mission left behind: something was inspected, the verifier read what
/// it graded, and there is an answer. They also hold the boundary — this layer must be silent for
/// every mission class it does not serve, which is what makes it safe for work that ran before it.
/// </summary>
public class AssessmentObjectiveTests
{
    private const string Mission = "m_audit";

    private static MissionSpecification Audit() =>
        MissionIntake.Resolve("Assess what this colony can do today and whether its missions reach the right workers.");

    private static IReadOnlyList<Evidence> Inspected() => new[]
    {
        Evidence.Create(EvidenceKinds.Inspection, deterministic: false, passed: true, Mission,
            detail: "list_directory: ."),
    };

    private static IReadOnlyList<ArtifactConsumption> VerifierRead(string role = "verifier") => new[]
    {
        new ArtifactConsumption
        {
            ArtifactId = "a1", ContentHash = "h", Schema = ArtifactSchemas.FileSet,
            MissionId = Mission, ConsumerRole = role,
        },
    };

    private const string Answer = "Capabilities: … Strengths: … Weaknesses: … Roles used: …";

    [Fact]
    public void AnAuditThatInspected_AndWasVerifiedAgainstWhatItRead_IsSatisfied()
    {
        var result = AssessmentObjective.Evaluate(Audit(), Inspected(), VerifierRead(), Answer);

        Assert.True(result.Satisfied, result.Explanation);
        Assert.Empty(result.Reasons);
    }

    /// <summary>The recorded failure, stated as a unit: tasks ran, nothing was read.</summary>
    [Fact]
    public void AnAuditWithNoInspection_IsRefused_AndSaysSo()
    {
        var result = AssessmentObjective.Evaluate(Audit(),
            evidence: Array.Empty<Evidence>(), VerifierRead(), Answer);

        Assert.False(result.Satisfied);
        Assert.Contains($"no '{EvidenceKinds.Inspection}' evidence", result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// A verifier that consumed nothing graded the prose it was handed. The ledger is what makes
    /// that checkable, and a deterministic check kind is NOT what satisfies this — an inspection
    /// row is what an observe-authority mission can honestly produce.
    /// </summary>
    [Fact]
    public void AVerifierThatConsumedNothing_IsRefused()
    {
        var result = AssessmentObjective.Evaluate(Audit(), Inspected(),
            consumptions: Array.Empty<ArtifactConsumption>(), Answer);

        Assert.False(result.Satisfied);
        Assert.Contains("verifier consumed no artifact", result.Explanation, StringComparison.Ordinal);

        // Another role reading an artifact is not the verifier reading one.
        var builderOnly = AssessmentObjective.Evaluate(Audit(), Inspected(), VerifierRead("builder"), Answer);
        Assert.False(builderOnly.Satisfied);
    }

    [Fact]
    public void AnAuditWithNoAnswer_IsRefused()
    {
        var result = AssessmentObjective.Evaluate(Audit(), Inspected(), VerifierRead(), answer: "   ");

        Assert.False(result.Satisfied);
        Assert.Contains("no operator-facing answer", result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// AN OUTAGE IS NEVER PERMISSION. A store that could not be read is not testimony that the work
    /// happened — the same rule the evidence identity gate applies to a revision-bearing mission.
    /// </summary>
    [Fact]
    public void AnUnreadableStore_FailsClosed()
    {
        Assert.False(AssessmentObjective.Evaluate(Audit(), evidence: null, VerifierRead(), Answer).Satisfied);
        Assert.False(AssessmentObjective.Evaluate(Audit(), Inspected(), consumptions: null, Answer).Satisfied);
    }

    /// <summary>
    /// THE BOUNDARY. Every mission before this release, and every class intake cannot yet serve,
    /// resolves to `general` — and this layer must then decide nothing at all, or it would be a new
    /// gate applied retroactively to work that was never asked to satisfy it.
    /// </summary>
    [Fact]
    public void ItIsSilentForEveryOtherMissionClass()
    {
        var general = MissionIntake.Resolve("Add a changelog entry for the release.");
        Assert.Equal(MissionSpecification.GeneralClass, general.MissionClass);
        Assert.False(AssessmentObjective.Applies(general));
        Assert.False(AssessmentObjective.Applies(null));

        // Applied anyway, it still says yes — the guard is stated twice on purpose, because a
        // caller that forgets `Applies` must not thereby fail a coding mission.
        Assert.True(AssessmentObjective.Evaluate(general, evidence: null, consumptions: null, answer: null).Satisfied);
    }
}
