using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>
/// A request for physical work. Carries its ORIGIN, because origin is an input to policy rather
/// than a choice of code path.
/// </summary>
/// <param name="MoundId">Which mound. A mission names exactly one.</param>
/// <param name="Steps">The work, as protocol steps. Validated against the charter before signing.</param>
/// <param name="Origin">Who asked — user, queen, workflow, automation, system.</param>
/// <param name="RequestedBy">The account, role or subsystem, for the audit trail.</param>
/// <param name="Reason">Why. Advisory, recorded, and never branched on.</param>
/// <param name="Worker">Which ant should run it, or empty for the Mound Major's choice.</param>
/// <param name="Duration">How long the mission stays valid.</param>
/// <param name="ApprovalGranted">
/// Set by the composition root once a real Anthill approval has been answered YES. The module never
/// creates approvals — §19 — so this is how an answer travels back in without the module reaching
/// into the core to look for one.
/// </param>
public sealed record PhysicalMissionRequest(
    string MoundId,
    IReadOnlyList<MissionStep> Steps,
    PhysicalOrigin Origin,
    string RequestedBy,
    string Reason = "",
    string Worker = "",
    TimeSpan? Duration = null,
    bool ApprovalGranted = false);

/// <summary>
/// The outcome of asking for physical work. Four states, and the third is the one that makes the
/// autonomy seam work without a second code path.
/// </summary>
/// <param name="Dispatched">Signed and queued for the mound.</param>
/// <param name="ApprovalRequired">Policy allows it, and a person must answer before it is issued.</param>
/// <param name="Refusals">Every reason, never just the first.</param>
public sealed record MissionDispatch(
    bool Dispatched,
    bool ApprovalRequired,
    IReadOnlyList<string> Refusals,
    Mission? Mission,
    Envelope? Envelope,
    string PolicyReason = "")
{
    public static MissionDispatch Refused(IReadOnlyList<string> reasons) =>
        new(false, false, reasons, null, null);

    public static MissionDispatch NeedsApproval(string reason) =>
        new(false, true, [], null, null, reason);
}

/// <summary>
/// PHYSICAL MISSION DISPATCH — PROTOCOL.md §9, and §15–§16 of the integration brief. v0.3.8.114.
///
/// ONE PIPELINE, EVERY ORIGIN. The brief names the failure it is guarding against: there must be no
/// `ManualMicromoundController` and no `AutonomousMicromoundController`. This is the reason there is
/// only one — origin enters as DATA on the request, policy reads it, and everything after policy is
/// identical whoever asked. A Queen-originated mission and a user-originated one differ in exactly
/// one place: whether an approval is owed first.
///
/// That property is asserted rather than asserted-about. `MissionDispatchTests` builds two requests
/// differing only in `Origin` and compares the signed envelopes' bodies.
///
/// THE ORDER OF THE GATES IS THE DESIGN, and each is here because something else cannot do it:
///
///   1. **Enrolled** — nothing can be signed FOR an identity that was never bound.
///   2. **Charter** — a mission carries no authority of its own (§9). No charter is not a weaker
///      mission, it is no mission: the mound would refuse it whole.
///   3. **Lease** — an expired lease means the mound has entered `safe_state` and is waiting for
///      fresh authority. Renewal is not resumption, so dispatching into it would be issuing work to
///      something that has correctly stopped listening.
///   4. **Policy** — who may spend the charter. Stop and hazardous are refused inside it, first.
///   5. **Approval** — if policy says one is owed and none has been granted, NOTHING IS QUEUED. The
///      mission is built and validated so the operator can see exactly what they are approving, and
///      then it is thrown away rather than held: a pending mission sitting in a queue is authority
///      nobody granted yet.
///   6. **`MissionValidator`** — the protocol's own, against the actual charter, before signing.
///
/// WHAT THIS DELIBERATELY DOES NOT DO. It does not check capabilities, limits, namespaces or step
/// ordering itself. `MissionValidator` does all of that, against the charter, and a second
/// implementation here would be defect class 5 in the layer where being wrong means moving
/// something physical. The mound then checks it AGAIN, and the mound wins.
/// </summary>
public sealed class MicromoundMissions(IMoundStore store, MicromoundIdentity identity, IEventBus events)
{
    private readonly IMoundStore _store = store;
    private readonly MicromoundIdentity _identity = identity;
    private readonly IEventBus _events = events;

    public MissionDispatch Dispatch(PhysicalMissionRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mound = _store.GetMound(request.MoundId);
        if (mound is null) return Refuse(request, ["no such mound"]);

        if (string.IsNullOrEmpty(mound.PublicKey))
            return Refuse(request, ["mound is not enrolled, so nothing can be signed for it"]);

        if (string.IsNullOrEmpty(mound.CharterId))
            return Refuse(request,
                ["this mound holds no charter, and a mission carries no authority of its own"]);

        var charter = _store.GetCharter(mound.CharterId);
        if (charter is null)
            return Refuse(request, [$"charter '{mound.CharterId}' is not on file"]);

        if (MicromoundCharters.LeaseExpired(mound, now))
            return Refuse(request,
                ["the lease has expired; the mound is in its safe state and needs fresh authority, "
               + "because renewal is not resumption"]);

        // The ceiling this mission could reach is the charter's — the mound intersects it with each
        // worker's own, and with the device's, but the charter is the outermost bound the colony
        // knows. Policy is asked about that, not about a guess at what the steps will need.
        if (!ActionClasses.TryParse(charter.ActionCeiling, out var ceiling))
            return Refuse(request, [$"charter action_ceiling is unreadable: '{charter.ActionCeiling}'"]);

        var stopped = MicromoundStop.AppliesTo(mound, MicromoundRuntime.Options);
        var verdict = MicromoundAutonomy.Evaluate(mound.AutonomyPolicy, request.Origin, ceiling, stopped);

        if (!verdict.Allowed) return Refuse(request, [verdict.Reason]);

        var mission = new Mission
        {
            MissionId = Guid.NewGuid().ToString(),
            MoundId = request.MoundId,
            CharterId = charter.CharterId,
            Worker = request.Worker,
            RequiredCapabilities = [.. request.Steps
                .Where(s => !string.IsNullOrEmpty(s.Capability) && !CapabilityId.IsRoutine(s.Capability))
                .Select(s => s.Capability)
                .Distinct(StringComparer.Ordinal)],
            AllowedRoutines = [.. request.Steps
                .Where(s => !string.IsNullOrEmpty(s.RoutineId))
                .Select(s => s.RoutineId)
                .Distinct(StringComparer.Ordinal)],
            Steps = [.. request.Steps],
            RequiredEvidence = [.. request.Steps
                .Where(s => !string.IsNullOrEmpty(s.EvidenceTag))
                .Select(s => s.EvidenceTag)
                .Distinct(StringComparer.Ordinal)],
            // A mission's safe state may only RESTATE the charter's (§9). Copying it rather than
            // accepting one from the caller removes the only way to get that wrong: "two documents
            // disagreeing about where the hardware goes when the watchdog trips is a contradiction
            // nobody can resolve at the moment it matters."
            SafeState = charter.SafeState,
            ExpiresAt = now.Add(request.Duration ?? TimeSpan.FromMinutes(15)).ToWire(),
            Context = request.Reason,
        };

        // VALIDATED BEFORE THE APPROVAL IS ASKED FOR, not after. An operator approving a mission the
        // protocol would refuse has been asked to approve nothing — and would reasonably read the
        // later refusal as the colony ignoring their answer.
        var validation = MissionValidator.Validate(mission, charter, request.MoundId, now);
        if (!validation.IsValid) return Refuse(request, validation.Errors);

        if (verdict.RequiresApproval && !request.ApprovalGranted)
        {
            // NOTHING IS QUEUED. The mission was built so the operator can see what they are
            // approving, and it is discarded rather than parked: a mission waiting in a downlink
            // queue is authority nobody has granted yet, and the mound cannot tell the difference.
            Publish(MicromoundEvents.MissionApprovalRequired,
                $"Physical mission on Micromound '{request.MoundId}' needs an operator decision.",
                Provenance(request, mission, verdict));

            return MissionDispatch.NeedsApproval(verdict.Reason);
        }

        var envelope = _identity.Sign(new Envelope
        {
            MoundId = request.MoundId,
            Kind = EnvelopeKinds.Mission,
            SentAt = now.ToWire(),
            Body = System.Text.Json.JsonSerializer.SerializeToElement(mission, ProtocolJson.Options),
        });

        _store.PutMission(mission);
        _store.QueueDownlink(request.MoundId, envelope);

        Publish(MicromoundEvents.MissionDispatched,
            $"Physical mission dispatched to Micromound '{request.MoundId}'.",
            Provenance(request, mission, verdict));

        return new MissionDispatch(true, false, [], mission, envelope, verdict.Reason);
    }

    /// <summary>
    /// The audit record §16 asks for: who or what requested it, why, the mission, the mound, the
    /// capabilities and routines, the authority used, and the policy decision that let it through.
    ///
    /// The same shape on every outcome, deliberately. A refusal that recorded less than a dispatch
    /// would make "why did nothing happen" the harder question to answer, and it is already the one
    /// asked more often.
    /// </summary>
    private static Dictionary<string, object?> Provenance(
        PhysicalMissionRequest request, Mission? mission, PolicyVerdict verdict) => new()
    {
        ["mound_id"] = request.MoundId,
        ["origin"] = PhysicalOrigins.Wire(request.Origin),
        ["requested_by"] = request.RequestedBy,
        ["reason"] = request.Reason,
        ["mission_id"] = mission?.MissionId ?? "",
        ["charter_id"] = mission?.CharterId ?? "",
        ["capabilities"] = mission?.RequiredCapabilities ?? [],
        ["routines"] = mission?.AllowedRoutines ?? [],
        ["worker"] = request.Worker,
        ["policy_allowed"] = verdict.Allowed,
        ["policy_requires_approval"] = verdict.RequiresApproval,
        ["policy_reason"] = verdict.Reason,
    };

    private MissionDispatch Refuse(PhysicalMissionRequest request, IReadOnlyList<string> reasons)
    {
        var metadata = Provenance(request, null, new PolicyVerdict(false, false, string.Join("; ", reasons)));
        metadata["reasons"] = reasons;

        Publish(MicromoundEvents.MissionRefused,
            $"Physical mission refused for Micromound '{request.MoundId}': {string.Join("; ", reasons)}",
            metadata);

        return MissionDispatch.Refused(reasons);
    }

    private void Publish(string eventType, string message, Dictionary<string, object?> metadata)
    {
        metadata["module"] = MicromoundModule.ModuleName;
        _events.Publish(new ColonyEvent { EventType = eventType, Message = message, Metadata = metadata });
    }
}
