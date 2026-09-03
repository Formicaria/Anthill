using Anthill.Modules.Micromound;
using Micromound.Protocol;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// STRUCTURED PHYSICAL EVIDENCE — §21, PROTOCOL.md §6. v0.3.8.114.
///
/// One rule governs all of it: `unverified` is never turned into success. The brief says so
/// outright, SAFETY.md Layer 3 gates missions on `unverified` AS FAILURES, and UPSTREAM.md tells a
/// controller to treat it as failed-until-proven. So there is no path in `MicromoundEvidence` that
/// raises an outcome, and these facts are mostly about the ways it must go DOWN.
/// </summary>
[Collection(MicromoundCollection.Name)]
public class EvidenceIngestTests
{
    /// <summary>Ingest time — the moment the colony is reading the batch.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// When the action ran: BEFORE the colony sees it, because a mound uploads a backlog rather
    /// than narrating live. The first draft of this fixture had evidence captured five seconds
    /// after `Now`, and `EvidenceGate` correctly refused it as "captured in the future" — a fixture
    /// describing a physically impossible sequence, caught by the protocol's own rule.
    /// </summary>
    private static readonly DateTimeOffset Started = Now.AddSeconds(-60);

    private static (InMemoryMoundStore Store, MicromoundEvidence Evidence, RecordingEventBus Events) Colony()
    {
        var store = new InMemoryMoundStore();
        var events = new RecordingEventBus();

        store.UpsertMound(new MoundRecord { MoundId = "mm-greenhouse", Name = "Greenhouse" });
        store.PutCharter(new Charter
        {
            CharterId = "charter-1",
            MoundId = "mm-greenhouse",
            Evidence = new EvidencePolicy { RequiredFor = ["act.*", "routine.*"], MinIntervalSeconds = 60 },
        });

        return (store, new MicromoundEvidence(store, events), events);
    }

    private static ActionRecord Watering(string outcome, params string[] evidenceRefs) => new()
    {
        ActionId = "action-1",
        MissionId = "mission-1",
        CharterId = "charter-1",
        Capability = "act.water_valve",
        RequestedParameters = new Dictionary<string, double> { ["on_s"] = 60 },
        Parameters = new Dictionary<string, double> { ["on_s"] = 30 },
        StartedAt = Started.ToWire(),
        EndedAt = Started.AddSeconds(30).ToWire(),
        Outcome = outcome,
        EvidenceRequired = true,
        EvidenceRefs = [.. evidenceRefs],
    };

    private static EvidenceItem Reading(string id, DateTimeOffset captured) => new()
    {
        EvidenceId = id,
        Type = "reading",
        CapturedAt = captured.ToWire(),
        Source = MicromoundRoster.Witness,
        PayloadJson = """{"value":41.0,"unit":"percent","capability":"sense.soil_moisture"}""",
    };

    /// <summary>Proof that arrived and is fresh: the colony agrees with the mound.</summary>
    [Fact]
    public void AProvenAction_KeepsItsOutcome()
    {
        var (_, evidence, events) = Colony();

        var ingest = evidence.Ingest("mm-greenhouse",
            [Watering(ActionOutcomes.Succeeded, "ev-1")], [Reading("ev-1", Started.AddSeconds(5))], Now);

        var action = Assert.Single(ingest.Actions);
        Assert.Equal(ActionOutcomes.Succeeded, action.ColonyOutcome);
        Assert.True(action.Verified);
        Assert.False(action.Degraded);
        Assert.True(events.Saw(MicromoundEvents.EvidenceIngested));
    }

    /// <summary>
    /// A CLAIMED SUCCESS WITH NO PROOF IS `unverified`, AND STAYS THAT WAY. This is the whole point:
    /// the mound said the work happened, and the colony cannot say so, and the colony's record is
    /// what a mission gets to read.
    /// </summary>
    [Fact]
    public void AClaimedSuccessWithMissingEvidence_IsUnverified()
    {
        var (_, evidence, events) = Colony();

        var ingest = evidence.Ingest("mm-greenhouse",
            [Watering(ActionOutcomes.Succeeded, "ev-missing")], [], Now);

        var action = Assert.Single(ingest.Actions);
        Assert.Equal(ActionOutcomes.Unverified, action.ColonyOutcome);
        Assert.False(action.Verified);
        Assert.True(action.Degraded);
        Assert.True(events.Saw(MicromoundEvents.ActionDegraded));
    }

    /// <summary>
    /// THE DEVICE'S REPORT IS NEVER REWRITTEN. The colony's verdict lives beside it, because the
    /// DISAGREEMENT is how an operator learns that proof is missing rather than that a valve failed.
    /// </summary>
    [Fact]
    public void TheDeviceReport_SurvivesTheColonysDisagreement()
    {
        var (store, evidence, _) = Colony();

        evidence.Ingest("mm-greenhouse", [Watering(ActionOutcomes.Succeeded, "ev-missing")], [], Now);

        var stored = Assert.Single(store.ActionsForMission("mm-greenhouse", "mission-1"));

        Assert.Equal(ActionOutcomes.Succeeded, stored.Record.Outcome);     // what the mound said
        Assert.Equal(ActionOutcomes.Unverified, stored.ColonyOutcome);     // what the colony can say
        Assert.False(string.IsNullOrWhiteSpace(stored.Reason));
    }

    /// <summary>
    /// BOTH PARAMETER SETS SURVIVE, so a clamp cannot be hidden. §6 carries `requested_parameters`
    /// and `parameters` precisely because "reporting only the effective value would hide the clamp
    /// from the audit trail that exists to surface it."
    /// </summary>
    [Fact]
    public void AClamp_KeepsBothWhatWasAskedAndWhatRan()
    {
        var (store, evidence, _) = Colony();

        evidence.Ingest("mm-greenhouse",
            [Watering(ActionOutcomes.Clamped, "ev-1")], [Reading("ev-1", Started.AddSeconds(5))], Now);

        var stored = Assert.Single(store.ActionsForMission("mm-greenhouse", "mission-1"));

        Assert.Equal(60, stored.Record.RequestedParameters["on_s"]);
        Assert.Equal(30, stored.Record.Parameters["on_s"]);
        Assert.True(stored.Verified);   // clamped is still work that happened
    }

    /// <summary>
    /// A REFUSAL NEEDS NO PROOF. §6: "a `refused` or `stopped` record needs no proof: it is a
    /// definite outcome, not a claim about the physical world." Demanding evidence for a correctly
    /// reported no would invent a failure out of it.
    /// </summary>
    [Theory]
    [InlineData(ActionOutcomes.Refused)]
    [InlineData(ActionOutcomes.Stopped)]
    [InlineData(ActionOutcomes.Failed)]
    public void ADefiniteNonSuccess_IsNotDegradedForLackOfEvidence(string outcome)
    {
        var (_, evidence, _) = Colony();

        var ingest = evidence.Ingest("mm-greenhouse", [Watering(outcome)], [], Now);

        var action = Assert.Single(ingest.Actions);
        Assert.Equal(outcome, action.ColonyOutcome);
        Assert.False(action.Degraded);
        Assert.False(action.Verified);   // …and none of them is success either
    }

    /// <summary>
    /// EVIDENCE AND ITS ACTION ROUTINELY ARRIVE IN DIFFERENT BEATS. §6 drains a backlog
    /// oldest-first, so the gate reads across everything the colony holds rather than one batch.
    /// </summary>
    [Fact]
    public void EvidenceArrivingInAnEarlierBeat_StillProvesALaterAction()
    {
        var (_, evidence, _) = Colony();

        evidence.Ingest("mm-greenhouse", [], [Reading("ev-1", Started.AddSeconds(5))], Now);
        var ingest = evidence.Ingest("mm-greenhouse", [Watering(ActionOutcomes.Succeeded, "ev-1")], [], Now);

        Assert.True(Assert.Single(ingest.Actions).Verified);
    }

    /// <summary>
    /// AN ACTION IS JUDGED UNDER THE AUTHORITY IT RAN UNDER. The evidence policy comes from the
    /// charter the RECORD cites, not the mound's current one — a later charter with a laxer policy
    /// must not retroactively verify work done before it existed.
    /// </summary>
    [Fact]
    public void AnAction_IsJudgedUnderTheCharterItCites()
    {
        var (store, evidence, _) = Colony();

        // A newer, laxer charter arrives and becomes the mound's current one.
        store.PutCharter(new Charter
        {
            CharterId = "charter-2",
            MoundId = "mm-greenhouse",
            Evidence = new EvidencePolicy { RequiredFor = [], MinIntervalSeconds = 3600 },
        });
        var mound = store.GetMound("mm-greenhouse")!;
        mound.CharterId = "charter-2";
        store.UpsertMound(mound);

        // The action still cites charter-1, and is judged by charter-1's policy.
        var ingest = evidence.Ingest("mm-greenhouse",
            [Watering(ActionOutcomes.Succeeded, "ev-missing")], [], Now);

        Assert.Equal(ActionOutcomes.Unverified, Assert.Single(ingest.Actions).ColonyOutcome);
    }

    /// <summary>
    /// A MISSION IS VERIFIED ONLY WHEN EVERY ACTION IS. Stricter than "the mound said completed",
    /// deliberately: one unproven actuation withholds the claim, and withholding costs an operator
    /// a question where granting it wrongly costs them a belief about the physical world.
    /// </summary>
    [Fact]
    public void AMissionWithOneUnprovenAction_IsNotVerified()
    {
        var (_, evidence, _) = Colony();

        var second = Watering(ActionOutcomes.Succeeded, "ev-missing");
        second.ActionId = "action-2";

        evidence.Ingest("mm-greenhouse",
            [Watering(ActionOutcomes.Succeeded, "ev-1"), second],
            [Reading("ev-1", Started.AddSeconds(5))], Now);

        var summary = evidence.SummarizeMission("mm-greenhouse", "mission-1");

        Assert.Equal(2, summary.Actions);
        Assert.Equal(1, summary.Verified);
        Assert.False(summary.AllVerified);
        Assert.Contains("unverified", summary.Detail, StringComparison.Ordinal);
    }

    /// <summary>And a mission nothing has reported on yet is not verified either.</summary>
    [Fact]
    public void AMissionWithNoActionsYet_IsNotVerified()
    {
        var (_, evidence, _) = Colony();

        var summary = evidence.SummarizeMission("mm-greenhouse", "mission-never-ran");

        Assert.False(summary.AllVerified);
        Assert.Equal(0, summary.Actions);
    }

    /// <summary>
    /// RE-INGESTING THE SAME RECORD DOES NOT DOUBLE IT. A backlog re-sends until acknowledged
    /// (§2), so the same action legitimately arrives more than once — and a mission that counted it
    /// twice would report progress that never happened.
    /// </summary>
    [Fact]
    public void ARepeatedRecord_IsStoredOnce()
    {
        var (_, evidence, _) = Colony();

        var proof = new[] { Reading("ev-1", Started.AddSeconds(5)) };
        evidence.Ingest("mm-greenhouse", [Watering(ActionOutcomes.Succeeded, "ev-1")], proof, Now);
        evidence.Ingest("mm-greenhouse", [Watering(ActionOutcomes.Succeeded, "ev-1")], proof, Now);

        Assert.Equal(1, evidence.SummarizeMission("mm-greenhouse", "mission-1").Actions);
    }

    /// <summary>An item or record with no id is refused loudly rather than stored unreachably.</summary>
    [Fact]
    public void AnUnidentifiedItem_IsRefusedRatherThanStored()
    {
        var (_, evidence, _) = Colony();

        var anonymous = Watering(ActionOutcomes.Succeeded);
        anonymous.ActionId = "";

        var ingest = evidence.Ingest("mm-greenhouse", [anonymous], [Reading("", Started)], Now);

        Assert.Empty(ingest.Actions);
        Assert.Equal(2, ingest.Refusals.Count);
    }
}
