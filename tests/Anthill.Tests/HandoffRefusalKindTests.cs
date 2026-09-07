using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A BOUND THAT STOPS THE COLONY GROWING MUST NOT ALSO DECLARE THE MISSION BROKEN. v0.3.8.141.
///
/// THIS ONE CAME FROM THE OPERATOR'S OWN COLONY, NOT FROM THE PLAN. Sixty-six real missions: 37
/// escalated, and every one of the 39 `required_handoff_refused` events ended that way. The reasons
/// were not one kind of thing:
///
///   18  mission task budget exhausted (12/12)
///   15  near-duplicate handoff suppressed (dedupe 'medic:fsig:…')
///    5  destination does not support task type
///    1  handoff depth limit
///
/// Thirty-three of thirty-nine were the runtime's OWN growth bounds killing the mission they exist
/// to protect. The "how to make tacos" run died this way: `required handoff refused: medic ->
/// builder (build_answer) — near-duplicate handoff suppressed`.
///
/// `v3.8.25` PREDICTED IT AND EXEMPTED HALF OF IT. `RecordRequiredHandoffRefusal` says, verbatim:
/// "treating a declined suggestion as a block would make every capped or deduplicated handoff a
/// mission failure." It drew that exemption around OPTIONAL handoffs. For REQUIRED ones exactly what
/// it described is what happened.
///
/// THE CATEGORY ERROR is that one bool carried three different findings. "Nobody here can do this"
/// is a claim about the WORK. "You have grown as far as you may" is a fact about the RUNTIME. And
/// dedupe is neither — it fires precisely BECAUSE the destination task already exists, so refusing a
/// required handoff for it says a step did not happen when it demonstrably did.
/// </summary>
[Collection("specialist-gates")]
public class HandoffRefusalKindTests
{
    private static AntHandoff TesterToMedic(int depth = 1, string dedupe = "k1") =>
        new("tester", "medic", "check failed", "failure_diagnosis",
            Array.Empty<string>(), Required: true, Depth: depth, DedupeKey: dedupe);

    /// <summary>The medic is gated off by default; these tests are about the gate's verdict, not
    /// about which roles a colony has switched on.</summary>
    private static T WithMedicOpen<T>(Func<T> body)
    {
        var before = AnthillRuntime.EnableMedicAnt;
        var specialists = AnthillRuntime.EnableSpecialistAntExecution;
        try
        {
            AnthillRuntime.EnableMedicAnt = true;
            AnthillRuntime.EnableSpecialistAntExecution = true;
            return body();
        }
        finally
        {
            AnthillRuntime.EnableMedicAnt = before;
            AnthillRuntime.EnableSpecialistAntExecution = specialists;
        }
    }

    private static Mission MissionWithTasks(int count, string? descriptionCarrying = null)
    {
        var m = new Mission { Goal = "g" };
        for (var i = 0; i < count; i++)
            m.Tasks.Add(new Task
            {
                Id = $"t{i}", Title = $"t{i}", AssignedAnt = "researcher", TaskType = "research",
                Description = i == 0 && descriptionCarrying is not null ? descriptionCarrying : $"work {i}",
            });
        return m;
    }

    // ---- already satisfied -----------------------------------------------------------------------

    /// <summary>
    /// THE ONE THAT READS AS A REFUSAL AND IS THE OPPOSITE OF ONE, and the single largest cause of
    /// the operator's escalations after the task budget.
    ///
    /// Dedupe fires because a task carrying this handoff's key ALREADY EXISTS. The required step the
    /// handoff demanded is present. Reporting that as "mission cannot be verified" is the runtime
    /// contradicting its own record.
    /// </summary>
    [Fact]
    public void ADedupedHandoff_IsAlreadySatisfied_NotARefusalOfTheWork()
    {
        var mission = MissionWithTasks(2, descriptionCarrying: "diagnose it [handoff dedupe:k1 depth:1]");

        var admission = WithMedicOpen(() => HandoffGate.Evaluate(TesterToMedic(dedupe: "k1"), mission));

        // Still not admitted — no NEW task should be created, which is what dedupe is for and was
        // always right. What changes is what the refusal MEANS.
        Assert.False(admission.Accepted);
        Assert.Equal(HandoffGate.Refusal.AlreadySatisfied, admission.Why);
        Assert.False(admission.BlocksTheMission);
    }

    // ---- bounded ---------------------------------------------------------------------------------

    /// <summary>
    /// THE BIGGEST SINGLE CAUSE: 18 of the operator's 39. A mission that did twelve tasks' worth of
    /// work is not a broken mission. The cap exists so "recursive unlimited task creation is
    /// structurally impossible" — a property of the runtime — and a limit doing its job is not
    /// evidence about the work it stopped.
    /// </summary>
    [Fact]
    public void AHandoffOverTheTaskBudget_IsBounded_NotBroken()
    {
        var admission = WithMedicOpen(() =>
            HandoffGate.Evaluate(TesterToMedic(), MissionWithTasks(HandoffGate.MaxMissionTasks)));

        Assert.False(admission.Accepted);
        Assert.Equal(HandoffGate.Refusal.Bounded, admission.Why);
        Assert.False(admission.BlocksTheMission);
    }

    /// <summary>The depth limit is the same shape as the budget: a bound, not a finding.</summary>
    [Fact]
    public void AHandoffOverTheDepthLimit_IsBounded_NotBroken()
    {
        var admission = WithMedicOpen(() =>
            HandoffGate.Evaluate(TesterToMedic(depth: HandoffGate.MaxHandoffDepth + 1), new Mission()));

        Assert.False(admission.Accepted);
        Assert.Equal(HandoffGate.Refusal.Bounded, admission.Why);
        Assert.False(admission.BlocksTheMission);
    }

    // ---- unservable ------------------------------------------------------------------------------

    /// <summary>
    /// AND THE HALF `v3.8.25` WAS ACTUALLY ABOUT STILL BLOCKS. A step no role in this colony can
    /// take is a claim about the WORK — "Required" that nothing enforces is a comment with a bool's
    /// type, and this release does not re-open that.
    /// </summary>
    [Fact]
    public void AnUnservableTaskType_IsStillARealRefusal()
    {
        var handoff = new AntHandoff("tester", "medic", "why", "interpretive_dance",
            Array.Empty<string>(), Required: true, Depth: 1, DedupeKey: "k9");

        var admission = WithMedicOpen(() => HandoffGate.Evaluate(handoff, new Mission()));

        Assert.False(admission.Accepted);
        Assert.Equal(HandoffGate.Refusal.Unservable, admission.Why);
        Assert.True(admission.BlocksTheMission);
    }

    /// <summary>A destination the runtime cannot execute is the same kind of no.</summary>
    [Fact]
    public void AnIneligibleDestination_IsStillARealRefusal()
    {
        // The medic gate is CLOSED here, deliberately — that is what makes the role ineligible.
        var admission = HandoffGate.Evaluate(TesterToMedic(), new Mission());

        Assert.False(admission.Accepted);
        Assert.Equal(HandoffGate.Refusal.Unservable, admission.Why);
        Assert.True(admission.BlocksTheMission);
    }

    // ---- the admitted case -----------------------------------------------------------------------

    /// <summary>An admitted handoff refuses nothing, and says so rather than leaving the field at
    /// whatever a caller last read.</summary>
    [Fact]
    public void AnAdmittedHandoff_CarriesNoRefusal()
    {
        var admission = WithMedicOpen(() => HandoffGate.Evaluate(TesterToMedic(), new Mission()));

        Assert.True(admission.Accepted, admission.Reason);
        Assert.Equal(HandoffGate.Refusal.None, admission.Why);
        Assert.False(admission.BlocksTheMission);
    }

    // ---- and exactly one kind blocks --------------------------------------------------------------

    /// <summary>
    /// THE RULE, STATED ONCE OVER THE WHOLE ENUM. A kind added later gets a decision rather than
    /// inheriting whichever branch it happens to fall into — which is how one bool came to carry
    /// three findings in the first place.
    /// </summary>
    [Theory]
    [InlineData(HandoffGate.Refusal.None, false)]
    [InlineData(HandoffGate.Refusal.AlreadySatisfied, false)]
    [InlineData(HandoffGate.Refusal.Bounded, false)]
    [InlineData(HandoffGate.Refusal.Unservable, true)]
    public void OnlyAnUnservableRefusal_BlocksTheMission(HandoffGate.Refusal why, bool blocks) =>
        Assert.Equal(blocks, new HandoffGate.Admission(false, "r", null, why).BlocksTheMission);
}
