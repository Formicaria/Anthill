using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Missions;
using Anthill.Core.Outcomes;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A RECOGNIZED CLASS IS VERIFIED WITHOUT ANYONE TURNING IT ON. v0.3.8.104.
///
/// THE GAP THIS CLOSES, and it is six releases wide. `objective_verification_enabled` ships false.
/// `AssessmentObjective` (`.98`), `CitationIntegrity` (`.99`), `CreationIntegrity` (`.100`),
/// `DiagnosisIntegrity` (`.101`), `OperationIntegrity` (`.102`) and `ExternalActionIntegrity`
/// (`.103`) all sat behind it. Every one of those releases said "deterministically qualified" and
/// meant it honestly — the suite turned the flag on. Nobody's install did, so on a default colony
/// the entire enforcement layer those six releases built was inert.
///
/// The flag is NOT removed. It still governs the general and coding lanes, where flipping it would
/// change how existing installs grade work they already do — a real behaviour change with blast
/// radius unrelated to this release. What changes is that a class the runtime RECOGNIZES stops
/// asking: its gate is the reason the class exists, and a class whose gate is optional is a class
/// whose guarantee is optional.
///
/// AND IT FAILS CLOSED, which is the half that makes the rest mean something. A recognized class
/// whose gate could not run grades NotSatisfied naming the reason, never NotChecked. "We could not
/// tell" must not read as "yes" for a mission whose entire class is a promise that a specific thing
/// happened.
/// </summary>
public class RecognizedClassVerificationTests
{
    private static Mission Complete(string goal) => new()
    {
        Goal = goal,
        Status = MissionStatus.Complete,
        FinalResult = "an answer",
    };

    private static MissionEvaluation Evaluate(
        Mission mission, MissionSpecification specification, bool flag,
        IReadOnlyList<Artifact>? artifacts = null,
        IReadOnlyList<Evidence>? evidence = null,
        IReadOnlyList<ArtifactConsumption>? consumptions = null) =>
        MissionEvaluator.Evaluate(mission, stopReason: null, patchProposalCount: 0,
            MissionConstraints.None, objectiveVerificationEnabled: flag,
            evidence: evidence, specification: specification,
            consumptions: consumptions, artifacts: artifacts);

    /// <summary>
    /// THE NAMED TEST. The flag is off — the shipped default — and the class is still graded.
    ///
    /// Nothing here sets a runtime switch, which is what makes the assertion mean what it says: a
    /// test that enabled verification would prove only that verification works when enabled, which
    /// was never in doubt and is exactly how the gap survived six releases.
    /// </summary>
    [Fact]
    public void RecognizedMission_ObjectiveVerificationRunsUnderDefaultConfiguration()
    {
        var specification = MissionIntake.Resolve(
            "Audit this repository and the running colony: what is implemented, and what is enabled now?");
        Assert.Equal(MissionSpecification.SystemAuditClass, specification.MissionClass);

        // An audit that inspected nothing, verified nothing, consumed nothing — the shape mission
        // 7afd85b2 had when it was reported complete.
        var evaluation = Evaluate(Complete(specification.OriginalRequest), specification, flag: false,
            artifacts: Array.Empty<Artifact>(), evidence: Array.Empty<Evidence>(),
            consumptions: Array.Empty<ArtifactConsumption>());

        Assert.NotEqual(MissionEvaluation.Deliverable.NotChecked, evaluation.DeliverableStatus);
        Assert.Equal(MissionEvaluation.Deliverable.NotSatisfied, evaluation.DeliverableStatus);
        Assert.False(evaluation.IsPositive,
            "an audit that inspected nothing graded positive with the objective-verification flag "
          + "off — which is the state every install ships in.");
    }

    /// <summary>
    /// FAIL CLOSED. A recognized class whose gate did not speak at all is refused, and the
    /// explanation names why rather than leaving `deliverable=not_satisfied` for an operator to
    /// interpret.
    /// </summary>
    [Fact]
    public void RecognizedClass_WithUnavailableVerification_FailsClosed()
    {
        var specification = MissionIntake.Resolve(
            "Post the release summary to the team's incident webhook.");
        Assert.Equal(MissionSpecification.ExternalActionClass, specification.MissionClass);

        // A null artifact store: the send gate cannot resolve anything at all.
        var evaluation = Evaluate(Complete(specification.OriginalRequest), specification, flag: false,
            artifacts: null);

        Assert.Equal(MissionEvaluation.Deliverable.NotSatisfied, evaluation.DeliverableStatus);
        Assert.False(evaluation.IsPositive);
        Assert.False(string.IsNullOrWhiteSpace(evaluation.Explanation));
    }

    /// <summary>
    /// AND AN UNRECOGNIZED MISSION IS UNCHANGED. The coding lane keeps the flag it has always had,
    /// because a release that quietly started grading work installs already do would be changing
    /// behaviour nobody asked it to change.
    /// </summary>
    [Fact]
    public void AnUnrecognizedMission_StillHonoursTheFlag()
    {
        var specification = MissionSpecification.General("fix the failing build in this repository");

        var evaluation = Evaluate(Complete(specification.OriginalRequest), specification, flag: false);

        Assert.Equal(MissionEvaluation.Deliverable.NotChecked, evaluation.DeliverableStatus);
    }

    /// <summary>
    /// THE RECOGNIZED SET IS NAMED ONCE. The evaluator and the dispatch ceiling both ask "is this a
    /// class the runtime enforces", and two spellings of that would eventually disagree about which
    /// missions are graded — which is the defect this whole release is about, at a smaller scale.
    /// </summary>
    [Fact]
    public void EveryRecognizedClass_IsARealClassWithAGate()
    {
        var known = new[]
        {
            MissionSpecification.SystemAuditClass,
            MissionSpecification.TroubleshootingClass,
            MissionSpecification.SystemActionClass,
            MissionSpecification.ExternalActionClass,
            MissionSpecification.ResearchClass,   // v0.3.8.109 — gate: ResearchIntegrity
        };

        Assert.Equal(known.OrderBy(c => c, StringComparer.Ordinal),
            MissionContracts.RecognizedClasses.OrderBy(c => c, StringComparer.Ordinal));

        Assert.DoesNotContain(MissionSpecification.GeneralClass, MissionContracts.RecognizedClasses);
    }
}
