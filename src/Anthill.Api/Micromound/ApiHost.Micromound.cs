#if MICROMOUND
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthill.Core.Configuration;
using Anthill.Modules.Homelab.Integrations;
using Anthill.Modules.Micromound;
using Micromound.Protocol;
// TWO TYPES, NOT THE NAMESPACE. `using Anthill.Core.Domain;` drags in `Anthill.Core.Domain.Task`,
// which makes every `Task<T>` and `Task.FromResult` in this file ambiguous against
// `System.Threading.Tasks.Task` — including the integration definition's `SyncAsync`, twenty lines
// down. Aliases import exactly what the approval wiring needs and nothing that collides.
using ApprovalActionType = Anthill.Core.Domain.ApprovalActionType;
using ApprovalRequest = Anthill.Core.Domain.ApprovalRequest;

namespace Anthill.Api;

/// <summary>
/// MICROMOUND wiring — the composition the module's README promised would happen here, and
/// nowhere else. The module proves the authority logic database-free and network-free; this file
/// gives it a database, a set of endpoints, and a card on the Integrations tab, and adds no
/// authority of its own.
///
/// v0.3.8.114 — THE COMMAND PATH IS HERE NOW. `.60` said charters and missions were "deliberately
/// NOT here — they are the command path, and they arrive with M2 and M4 behind the approval
/// pipeline." They have arrived, behind exactly that pipeline: a mission that policy says needs an
/// operator's answer becomes a real ANTHILL <c>ApprovalRequest</c> in the colony's one approval
/// queue, because §19 of the integration brief is explicit that there must not be a second
/// "Micromound Approvals" framework. THIS FILE is where that wiring can live and the module cannot:
/// the module may reference the SDK and the wire contract and nothing else of ours, and the
/// approval store is in the core.
///
/// TWO AUTH MODELS, ON PURPOSE. Operator endpoints go through <c>RequireAuth</c> like everything
/// else. The two `/micromound/v0/*` device endpoints do NOT: a mound has no session — its
/// enrollment token (once) and its Ed25519 signature (every beat) are the authentication, checked
/// inside the module against the store. A session gate on those would not add security; it would
/// add a shared credential every device had to hold.
///
/// AND THOSE TWO SPEAK THE DEVICE'S SHAPE, NOT A CONVENIENT ONE. Their request and response bodies
/// are whatever `Micromound.Host`'s `HttpEnrollmentClient` and `HttpSyncTransport` actually write
/// and read, down to the field names — see each endpoint for what that changed and why nothing
/// caught it sooner.
/// </summary>
public static partial class ApiHost
{
    public static IMoundStore Mounds { get; private set; } = null!;
    private static MicromoundEnrollment MicromoundEnroll = null!;
    private static MicromoundSync MicromoundSyncSvc = null!;
    private static MicromoundIdentity MicromoundId = null!;
    private static MicromoundCharters MicromoundCharterSvc = null!;
    private static MicromoundConfiguration MicromoundConfigSvc = null!;
    private static MicromoundMissions MicromoundMissionSvc = null!;
    private static MicromoundEvidence MicromoundEvidenceSvc = null!;
    private static MicromoundResolver MicromoundResolve = null!;

    /// <summary>
    /// After InitHomelab (the integration catalog convention lives there) and after module load
    /// (MicromoundRuntime must already be configured — the store resolves its database path and
    /// cipher from it).
    ///
    /// v0.3.8.114 — the command path is composed here, in the order it depends: the identity first
    /// (everything downlink is signed), then the four services that sign, then the sync beat, which
    /// needs two of them because an acknowledged beat both renews a lease and ingests evidence.
    /// Nothing is constructed lazily: a colony that mints its signing key on the first charter
    /// rather than at startup would mint it inside a request, under whatever lock that request held.
    /// </summary>
    private static void InitMicromound()
    {
        Mounds = new SqliteMoundStore();
        MicromoundId = new MicromoundIdentity(Mounds);
        MicromoundEnroll = new MicromoundEnrollment(Mounds, Queen.Events);
        MicromoundCharterSvc = new MicromoundCharters(Mounds, MicromoundId, Queen.Events);
        MicromoundConfigSvc = new MicromoundConfiguration(Mounds, MicromoundId, Queen.Events);
        MicromoundMissionSvc = new MicromoundMissions(Mounds, MicromoundId, Queen.Events);
        MicromoundEvidenceSvc = new MicromoundEvidence(Mounds, Queen.Events);
        MicromoundResolve = new MicromoundResolver(Mounds);
        MicromoundSyncSvc = new MicromoundSync(
            Mounds, Queen.Events, MicromoundId, MicromoundCharterSvc, MicromoundEvidenceSvc);
        IntegrationCatalog.Register(new MicromoundIntegrationDefinition());

        // The approval seam. Set here and nowhere else: `Anthill.Core` must not learn about an
        // optional module, and a colony built without MICROMOUND leaves this null and never
        // produces a `PhysicalAction` approval for it to miss.
        Anthill.Core.Orchestration.Queen.PhysicalActionReplay = ReplayApprovedMission;
    }

    /// <summary>
    /// The Integrations-tab card. Category infra, auth mode token (the enrollment token is the
    /// only secret this kind ever mints). Sync here is deterministic and NETWORK-FREE — mounds
    /// dial in, the colony never reaches out, so "sync" means re-deriving the three widget
    /// payloads from what the store already knows. The context's base url and credentials are
    /// deliberately unused for the same reason.
    /// </summary>
    private sealed class MicromoundIntegrationDefinition : IIntegrationDefinition
    {
        public string Kind => MicromoundModule.ModuleName;
        public string Category => "infra";
        public string AuthMode => "token";
        public IReadOnlyList<string> WidgetKinds => MicromoundWidgetKinds.All;

        public Task<IReadOnlyDictionary<string, string>> SyncAsync(
            IntegrationContext context, CancellationToken ct) =>
            Task.FromResult(BuildMicromoundWidgets());
    }

    /// <summary>One builder for both consumers: the integration platform's sync (which persists
    /// through integration_state) and the module's own widget cache (which /micromound reads).</summary>
    private static IReadOnlyDictionary<string, string> BuildMicromoundWidgets()
    {
        var options = MicromoundRuntime.Options;
        var now = DateTimeOffset.UtcNow;
        var mounds = Mounds.ListMounds();
        var payloads = new Dictionary<string, string>
        {
            [MicromoundWidgetKinds.MoundFleet] = MicromoundWidgets.BuildFleet(mounds, options, now),
            [MicromoundWidgetKinds.MissionStatus] =
                MicromoundWidgets.BuildMissionStatus(Mounds, mounds, MicromoundEvidenceSvc, now),
            [MicromoundWidgetKinds.EvidenceFeed] = MicromoundWidgets.BuildEvidenceFeed(Mounds, mounds, 5),
        };
        var stamp = now.ToWire();
        foreach (var (kind, payload) in payloads) Mounds.PutWidgetPayload(kind, payload, stamp);
        return payloads;
    }

    // ---- Request shapes. Wire names are PROTOCOL.md's, not the web default's: a device is not a
    // browser, and case-insensitive camelCase matching does not bridge snake_case. --------------

    private sealed record MoundCreateRequest(
        [property: JsonPropertyName("mound_id")] string? MoundId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("tier")] string? Tier);

    /// <summary>
    /// EXACTLY WHAT `HttpEnrollmentClient` SENDS — v0.3.8.114, and this is a correction rather than
    /// an extension.
    ///
    /// M1's shape was invented from the protocol document rather than read off the client, and it
    /// differed in three ways that each made enrolment impossible: it required a `mound_id` the
    /// device does not send, it read the key from `public_key` where the device writes
    /// `device_public_key`, and it required a `protocol_version` the device omits. The front door
    /// of the integration could not be walked through, and nothing noticed because both ends of
    /// every test were ours.
    ///
    /// `mound_id`, `capabilities` and `protocol_version` stay ACCEPTED but optional: this colony's
    /// own console can supply them, and the module treats a supplied mound id as a cross-check
    /// against the token rather than as the lookup.
    ///
    /// `DeviceWireContractTests.TheEnrolmentFieldsTheDeviceSends_AreTheOnesThisColonyAccepts` reads
    /// the device's client out of the pinned checkout and fails if this set stops covering it. It
    /// cannot reflect over this record — it is private, and that test project does not reference
    /// `Anthill.Api` on purpose — so the four required names are written down in both places, and
    /// this sentence is the other half of that pairing.
    /// </summary>
    private sealed record MicromoundEnrollBody(
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("device_public_key")] string? DevicePublicKey,
        [property: JsonPropertyName("hardware_profile")] string? HardwareProfile,
        [property: JsonPropertyName("tier")] string? Tier,
        [property: JsonPropertyName("mound_id")] string? MoundId,
        [property: JsonPropertyName("capabilities")] List<string>? Capabilities,
        [property: JsonPropertyName("protocol_version")] int? ProtocolVersion);

    private sealed record MoundStopRequest(
        [property: JsonPropertyName("mound_id")] string? MoundId);

    // ---- The command path's request shapes. v0.3.8.114. ----------------------------------------

    private sealed record CharterBody(
        [property: JsonPropertyName("mound_id")] string? MoundId,
        [property: JsonPropertyName("capabilities")] List<string>? Capabilities,
        [property: JsonPropertyName("routines")] List<string>? Routines,
        [property: JsonPropertyName("action_ceiling")] string? ActionCeiling,
        [property: JsonPropertyName("duration_s")] int? DurationSeconds,
        [property: JsonPropertyName("lease_ttl_s")] int? LeaseTtlSeconds,
        [property: JsonPropertyName("mission_ref")] string? MissionRef,
        [property: JsonPropertyName("safe_state")] string? SafeState,
        [property: JsonPropertyName("evidence_required_for")] List<string>? EvidenceRequiredFor,
        [property: JsonPropertyName("evidence_min_interval_s")] int? EvidenceMinIntervalSeconds,
        [property: JsonPropertyName("limits")] Dictionary<string, CapabilityLimits>? Limits);

    private sealed record HardwareBody(
        [property: JsonPropertyName("device")] string? Device,
        [property: JsonPropertyName("driver")] string? Driver,
        [property: JsonPropertyName("settings")] Dictionary<string, string>? Settings);

    private sealed record ConfigBody(
        [property: JsonPropertyName("mound_id")] string? MoundId,
        [property: JsonPropertyName("hardware")] List<HardwareBody>? Hardware,
        [property: JsonPropertyName("capabilities")] List<string>? Capabilities,
        [property: JsonPropertyName("routines")] List<string>? Routines,
        [property: JsonPropertyName("workers")] List<WorkerDefinition>? Workers,
        [property: JsonPropertyName("device_limits")] Dictionary<string, CapabilityLimits>? DeviceLimits,
        [property: JsonPropertyName("reasoning_mode")] string? ReasoningMode,
        [property: JsonPropertyName("safe_state")] string? SafeState);

    /* ---- The friendly form's wire shape. v0.3.8.123 -------------------------------------------
       Deliberately NOT `FriendlyMoundConfiguration` itself. Every field here is nullable so the
       console can send a partial form as the operator fills it in and get a live answer back, and
       the defaults are applied in one place (`ReadAuthoringForm`) rather than being scattered
       between the wire contract and the model. `AdvancedCarry` IS sent whole, because the console
       never authors it — it round-trips exactly what the projection handed over, which is the
       property that stops a simple page deleting an advanced one's work. */
    private sealed record AuthoringDeviceBody(
        [property: JsonPropertyName("capability")] string? Capability,
        [property: JsonPropertyName("device")] string? Device,
        [property: JsonPropertyName("driver")] string? Driver,
        [property: JsonPropertyName("purpose")] string? Purpose,
        [property: JsonPropertyName("assigned_ant")] string? AssignedAnt,
        [property: JsonPropertyName("safe_min")] double? SafeMin,
        [property: JsonPropertyName("safe_max")] double? SafeMax,
        [property: JsonPropertyName("max_run_s")] double? MaxRunSeconds,
        [property: JsonPropertyName("min_rest_s")] double? MinRestSeconds,
        [property: JsonPropertyName("max_per_hour")] double? MaxTimesPerHour,
        [property: JsonPropertyName("verified_by")] string? VerifiedBy,
        [property: JsonPropertyName("settings")] Dictionary<string, string>? Settings);

    private sealed record AuthoringBody(
        [property: JsonPropertyName("mound_id")] string? MoundId,
        [property: JsonPropertyName("purpose")] string? Purpose,
        [property: JsonPropertyName("devices")] List<AuthoringDeviceBody>? Devices,
        [property: JsonPropertyName("routines")] List<string>? Routines,
        [property: JsonPropertyName("control_mode")] string? ControlMode,
        [property: JsonPropertyName("action_level")] string? ActionLevel,
        [property: JsonPropertyName("check_in_minutes")] int? CheckInMinutes,
        [property: JsonPropertyName("authority_days")] int? AuthorityDays,
        [property: JsonPropertyName("proof_interval_s")] int? ProofIntervalSeconds,
        [property: JsonPropertyName("safe_state")] string? SafeState,
        [property: JsonPropertyName("advanced")] AdvancedCarry? Advanced);

    private sealed record MissionBody(
        [property: JsonPropertyName("mound_id")] string? MoundId,
        [property: JsonPropertyName("steps")] List<MissionStep>? Steps,
        [property: JsonPropertyName("origin")] string? Origin,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("worker")] string? Worker,
        [property: JsonPropertyName("duration_s")] int? DurationSeconds);

    /// <summary>
    /// A mission request, parked in an approval's metadata so the decision can carry it out.
    ///
    /// It is stored rather than re-asked-for because an approval is answered minutes or hours
    /// later, by somebody who saw a summary and clicked yes — and rebuilding "what they approved"
    /// from that summary is how an operator ends up authorizing one thing and getting another.
    /// `PhysicalOrigin` rides along as its own field: the origin an approval REPLAYS under is the
    /// origin that asked, not `User` because a user answered.
    /// </summary>
    private sealed record ParkedMission(
        [property: JsonPropertyName("mound_id")] string MoundId,
        [property: JsonPropertyName("steps")] List<MissionStep> Steps,
        [property: JsonPropertyName("origin")] string Origin,
        [property: JsonPropertyName("requested_by")] string RequestedBy,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("worker")] string Worker,
        [property: JsonPropertyName("duration_s")] int DurationSeconds);

    /// <summary>
    /// One parked request becomes one dispatcher request. The ONLY difference between the
    /// approved and unapproved paths is the flag — §15: there is no `ManualMicromoundController`
    /// and no `AutonomousMicromoundController`, and a second construction site here would be the
    /// beginning of one.
    /// </summary>
    private static PhysicalMissionRequest Requested(ParkedMission parked, bool approvalGranted)
    {
        PhysicalOrigins.TryParse(parked.Origin, out var origin);

        return new PhysicalMissionRequest(
            parked.MoundId, parked.Steps, origin, parked.RequestedBy, parked.Reason, parked.Worker,
            TimeSpan.FromSeconds(parked.DurationSeconds), approvalGranted);
    }

    /// <summary>
    /// What the operator reads before answering. Names the mound, who asked, why, and every
    /// capability and routine the steps will reach for — an approval whose description does not say
    /// what will physically move is an approval nobody can give meaningfully.
    /// </summary>
    private static string DescribeMission(ParkedMission parked, string policyReason)
    {
        var capabilities = parked.Steps
            .Select(step => string.IsNullOrEmpty(step.RoutineId) ? step.Capability : step.RoutineId)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return $"Micromound '{parked.MoundId}' — {parked.Steps.Count} step(s): "
             + (capabilities.Count > 0 ? string.Join(", ", capabilities) : "(no capability named)")
             + $"\nRequested by {parked.RequestedBy} as '{parked.Origin}'."
             + (string.IsNullOrWhiteSpace(parked.Reason) ? "" : $"\nReason: {parked.Reason}")
             + $"\nPolicy: {policyReason}";
    }

    /// <summary>
    /// CARRYING OUT AN APPROVED PHYSICAL MISSION — the seam <see cref="Queen.PhysicalActionReplay"/>
    /// exists for, wired in <see cref="InitMicromound"/>.
    ///
    /// It re-runs the WHOLE dispatcher rather than skipping to the signing: every gate is checked
    /// again against the world as it is NOW. That matters because an approval is answered later —
    /// the lease may have lapsed, a stop may have been engaged, the charter may have expired since
    /// the operator was asked. An approval is permission to attempt the work, never a promise that
    /// the conditions still hold, and a replay that trusted the earlier check would act on a world
    /// that has moved.
    /// </summary>
    private static string ReplayApprovedMission(ApprovalRequest approval)
    {
        var json = approval.Metadata.TryGetValue("request_json", out var raw) ? raw?.ToString() : null;
        if (string.IsNullOrWhiteSpace(json))
            return "The approval carries no mission to replay, so nothing was issued.";

        ParkedMission? parked;
        try { parked = JsonSerializer.Deserialize<ParkedMission>(json); }
        catch (JsonException ex) { return $"The parked mission does not parse ({ex.Message}); nothing was issued."; }

        if (parked is null) return "The parked mission does not parse; nothing was issued.";

        var dispatch = MicromoundMissionSvc.Dispatch(
            Requested(parked, approvalGranted: true), DateTimeOffset.UtcNow);

        BuildMicromoundWidgets();

        if (dispatch.Dispatched)
            return $"Issued to Micromound '{parked.MoundId}' as mission {dispatch.Mission!.MissionId}. "
                 + "It travels on the mound's next beat.";

        // A refusal after approval is not a failure of the approval — it is the world having
        // changed, and saying which gate closed is the difference between an operator fixing it in
        // a minute and filing a bug.
        return $"Approved, and NOT issued: {string.Join("; ", dispatch.Refusals)}";
    }

    private static void MapMicromoundEndpoints(WebApplication app)
    {
        // ---- Fleet: what the colony can see -------------------------------------------------
        /* v0.3.8.123 — `/micromound/roster/defaults` WAS HERE AND IS GONE. It served the seven
           names a `+ Mound` chamber is drawn with, and being inside this region meant those seven
           presentation labels required the micromound repository to be checked out beside this one
           and the operator to hold `read_micromound`. Neither has anything to do with labelling a
           chamber in your own colony view, and when either was missing the chamber came up with no
           ants in it. The roster now lives in `Anthill.SDK.Modules.MoundRoster` and is served by
           `/colony/mound-roster`, which is always mapped; `MicromoundRoster` forwards to the same
           list, so there is still exactly one store and the device-runtime check still covers it.

           WHAT THE OPERATOR THEN DOES WITH THOSE NAMES STILL NEVER LEAVES THE COLONY. A renamed or
           recoloured ant is a label in the operator's saved layout, not a command: the mound is
           enrolled by one-time token and keeps taking orders under its own identity whatever the
           colony calls it. That separation is the entire point of the feature. */

        app.MapGet("/micromound/mounds", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, MicromoundPermissions.Read); if (auth is not null) return auth;
            var options = MicromoundRuntime.Options;

            // v0.3.8.115 — the STATUS VERDICT, keyed by mound id beside the records.
            //
            // Deliberately a sibling map rather than a field spliced into each item: `items` is the
            // serialization of `MoundRecord` itself, and projecting it by hand here would create a
            // second, hand-maintained list of its fields that goes stale the next time one is added.
            // A caller joins on `mound_id`, which costs one lookup and cannot drift.
            var mounds = Mounds.ListMounds();
            var globalStop = MicromoundStop.IsEngaged(options);
            var now = DateTimeOffset.UtcNow;

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["global_stop"] = globalStop,
                ["stop_file"] = MicromoundStop.PathFor(options),
                ["status"] = mounds.ToDictionary(
                    m => m.MoundId,
                    m => (object?)MicromoundWidgets.StatusOf(m, options, now, globalStop),
                    StringComparer.Ordinal),
                ["pending_downlink"] = mounds.ToDictionary(
                    m => m.MoundId,
                    m => (object?)Mounds.PendingDownlinkCount(m.MoundId),
                    StringComparer.Ordinal),
                // v0.3.8.114 — this said `M1` and `command_path: false` until the command path
                // existed, and then it kept saying it. The colony now issues charters,
                // configuration and missions, and this is the field an operator checks to find out.
                ["command_path"] = true,
                ["controller_public_key"] = MicromoundId.PublicKeyHex,
                ["items"] = mounds,
            });
        });

        // ---- Create a mound + mint its one-time enrollment token ----------------------------
        app.MapPost("/micromound/mounds", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, MicromoundPermissions.Manage); if (auth is not null) return auth;
            MoundCreateRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<MoundCreateRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (body is null || string.IsNullOrWhiteSpace(body.MoundId))
                return ApiJson.Error("mound_id is required.", "bad_request");
            var tier = string.IsNullOrWhiteSpace(body.Tier) ? MoundTiers.EdgeQueen : body.Tier.Trim();
            if (!MoundTiers.IsKnown(tier))
                return ApiJson.Error(
                    $"Unknown tier '{tier}'. Known: {MoundTiers.EdgeQueen}, {MoundTiers.DeterministicController}.",
                    "bad_request");

            MintedEnrollment minted;
            try
            {
                minted = MicromoundEnroll.MintToken(body.MoundId.Trim(), body.Name?.Trim() ?? "",
                    tier, CurrentUsername(ctx) ?? "operator", DateTimeOffset.UtcNow);
            }
            catch (ArgumentException ex) { return ApiJson.Error(ex.Message, "bad_request"); }

            BuildMicromoundWidgets();
            // The plaintext token appears in this response and nowhere else, ever — the store
            // holds a hash, encrypted at rest when a field cipher is configured.
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["mound_id"] = minted.MoundId,
                ["token"] = minted.Token,
                ["expires_at"] = minted.ExpiresAt,
                ["shown_once"] = true,
            });
        });

        // ---- Device: enrollment (PROTOCOL.md §3). The token is the auth. --------------------
        //
        // The RESPONSE is the other half of §3 step 3: "the controller binds the public key to the
        // mound record, burns the token, and RETURNS THE CONTROLLER PUBLIC KEY." M1 returned
        // everything except that, which left every enrolled device unable to verify a single
        // downlink envelope — the mound persists this key and checks `KeyIds.Controller` against it
        // forever after, so without it a charter is an unverifiable message it correctly drops.
        app.MapPost("/micromound/v0/enroll", async (HttpContext ctx) =>
        {
            MicromoundEnrollBody? body;
            try { body = await ctx.Request.ReadFromJsonAsync<MicromoundEnrollBody>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (body is null || string.IsNullOrWhiteSpace(body.Token))
                return ApiJson.Error("token is required.", "bad_request");

            var result = MicromoundEnroll.Enroll(new EnrollmentRequest(
                body.MoundId?.Trim() ?? "", body.Token, body.DevicePublicKey ?? "",
                string.IsNullOrWhiteSpace(body.Tier) ? MoundTiers.EdgeQueen : body.Tier,
                body.HardwareProfile ?? "", body.Capabilities ?? [],
                body.ProtocolVersion ?? ProtocolVersion.Current),
                DateTimeOffset.UtcNow);

            // A REFUSAL IS AN HTTP 4xx, not a 200 carrying `accepted: false`. The device reads the
            // status code first — `HttpEnrollmentClient` treats 4xx as "definite refusal, a retry
            // will not fix this" and anything else as "not yet enrolled, keep trying" — so
            // answering 200 to a burned token would put the device in a retry loop against a
            // decision that is final.
            if (!result.Accepted)
                return Results.Json(new Dictionary<string, object?>
                {
                    ["accepted"] = false,
                    // Refusal reasons go back to the device: the operator minted this token minutes
                    // ago and is standing next to the hardware — a silent refusal helps only an
                    // attacker's patience, and every refusal is already a loud colony event.
                    ["reason"] = result.Reason,
                }, statusCode: 400);

            BuildMicromoundWidgets();

            return Results.Json(new Dictionary<string, object?>
            {
                ["accepted"] = true,
                ["controller_public_key"] = MicromoundId.PublicKeyHex,
                ["mound_id"] = result.Mound?.MoundId,
                ["colony_version"] = MicromoundRuntime.Options.ColonyVersion,
                ["protocol_version"] = ProtocolVersion.Current,
                ["sync_interval_s"] = result.Mound?.SyncIntervalSeconds,
            });
        });

        // ---- Device: the sync beat (PROTOCOL.md §1). The signature is the auth. -------------
        //
        // ONE ENVELOPE IN, AN ARRAY OF ENVELOPES OUT. That is what `HttpSyncTransport` speaks —
        // it serializes a single `Envelope` as the whole request body and deserializes the whole
        // response body as `List<Envelope>`. M1 expected `{mound_id, envelopes[]}` and answered
        // `{accepted, refusals, downlink[...kind strings]}`, so a real mound's first beat would
        // have failed to parse in both directions.
        //
        // There is no `mound_id` field to read, and none is needed: the envelope carries its own,
        // and the signature is over bytes that include it. Trusting a separate field would let a
        // caller name one mound and sign as another.
        //
        // Pinned from the device's side by
        // `DeviceWireContractTests.TheSyncExchange_IsOneRawEnvelopeInAndAnArrayOfEnvelopesOut`.
        app.MapPost("/micromound/v0/sync", async (HttpContext ctx) =>
        {
            Envelope? uplink;
            try { uplink = await ctx.Request.ReadFromJsonAsync<Envelope>(ProtocolJson.Options); }
            catch (JsonException) { return ApiJson.Error("Envelope does not parse.", "bad_request"); }

            if (uplink is null || string.IsNullOrWhiteSpace(uplink.MoundId))
                return ApiJson.Error("An envelope with no mound_id cannot be attributed.", "bad_request");

            var outcome = MicromoundSyncSvc.AcceptUplink(uplink.MoundId, [uplink], DateTimeOffset.UtcNow);
            BuildMicromoundWidgets(); // refusals move the fleet picture too

            // A REFUSED BEAT GETS AN EMPTY ARRAY, NOT AN ERROR STATUS. The device's own rule is
            // that a non-2xx is a failed exchange it retries; a refusal is a decision it should
            // not retry into. So the exchange succeeded and carried nothing back — no ack, which
            // is precisely how the device learns its records are still its own to keep.
            return Results.Json(outcome.Downlink, ProtocolJson.Options);
        });

        // ---- Evidence: recent beats, fleet-wide or per mound --------------------------------
        app.MapGet("/micromound/evidence", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, MicromoundPermissions.Read); if (auth is not null) return auth;
            var moundId = ctx.Request.Query["mound_id"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(moundId))
                return ApiJson.Ok(new Dictionary<string, object?>
                {
                    ["mound_id"] = moundId,
                    ["items"] = Mounds.RecentBeats(moundId!, 50),
                });
            var cached = Mounds.GetWidgetPayload(MicromoundWidgetKinds.EvidenceFeed);
            var payload = cached?.PayloadJson
                ?? MicromoundWidgets.BuildEvidenceFeed(Mounds, Mounds.ListMounds(), 5);
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["feed"] = JsonSerializer.Deserialize<JsonElement>(payload),
                ["updated_at"] = cached?.UpdatedAt ?? DateTimeOffset.UtcNow.ToWire(),
            });
        });

        // ---- The command path — §26. Everything below issues a signed downlink envelope, and
        // ---- every one of them lands in the queue rather than on a wire: the colony never dials
        // ---- a mound (PROTOCOL.md §1), so "issued" means "waiting for the device's next beat".

        // Charters carry AUTHORITY, so they need the Approve permission rather than Manage. The
        // tiering was declared in `.60` with nothing using it "so the tiering is settled before
        // anything can be tempted to skip it"; this is the first thing it governs.
        app.MapPost("/micromound/charters", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, MicromoundPermissions.Approve); if (auth is not null) return auth;
            CharterBody? body;
            try { body = await ctx.Request.ReadFromJsonAsync<CharterBody>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (body is null || string.IsNullOrWhiteSpace(body.MoundId))
                return ApiJson.Error("mound_id is required.", "bad_request");

            var issue = MicromoundCharterSvc.Issue(new CharterRequest(
                body.MoundId.Trim(),
                body.Capabilities ?? [],
                body.Routines ?? [],
                string.IsNullOrWhiteSpace(body.ActionCeiling) ? "observe" : body.ActionCeiling.Trim(),
                Duration: TimeSpan.FromSeconds(body.DurationSeconds ?? 3600),
                LeaseTtl: TimeSpan.FromSeconds(body.LeaseTtlSeconds ?? 900),
                Limits: body.Limits,
                MissionRef: body.MissionRef ?? "",
                SafeState: string.IsNullOrWhiteSpace(body.SafeState) ? "all_actuators_off" : body.SafeState,
                EvidenceRequiredFor: body.EvidenceRequiredFor,
                EvidenceMinIntervalSeconds: body.EvidenceMinIntervalSeconds ?? 60),
                CurrentUsername(ctx) ?? "operator", DateTimeOffset.UtcNow);

            if (!issue.Issued)
                return ApiJson.Error(string.Join("; ", issue.Refusals), "refused");

            BuildMicromoundWidgets();
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["charter_id"] = issue.Charter!.CharterId,
                ["mound_id"] = issue.Charter.MoundId,
                ["action_ceiling"] = issue.Charter.ActionCeiling,
                ["expires_at"] = issue.Charter.ExpiresAt,
                // Not "delivered". The mound collects it, and the difference is a fact an operator
                // watching an offline device needs.
                ["awaiting_collection"] = Mounds.PendingDownlinkCount(issue.Charter.MoundId),
            });
        });

        // Configuration is Manage, not Approve: it is the hardware map an operator authors, and it
        // grants nothing. A manifest can only NARROW what a charter may later spend, because
        // `device_limits` is the middle tier of SAFETY.md Layer 1's intersection.
        app.MapPost("/micromound/config", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, MicromoundPermissions.Manage); if (auth is not null) return auth;
            ConfigBody? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ConfigBody>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (body is null || string.IsNullOrWhiteSpace(body.MoundId))
                return ApiJson.Error("mound_id is required.", "bad_request");

            var issue = MicromoundConfigSvc.Issue(new ConfigurationRequest(
                body.MoundId.Trim(),
                [.. (body.Hardware ?? []).Select(h => new HardwareAssignment(
                    h.Device ?? "", h.Driver ?? "",
                    h.Settings ?? new Dictionary<string, string>(StringComparer.Ordinal)))],
                body.Capabilities ?? [],
                body.Routines ?? [],
                body.Workers,
                body.DeviceLimits,
                string.IsNullOrWhiteSpace(body.ReasoningMode) ? ReasoningModes.None : body.ReasoningMode,
                string.IsNullOrWhiteSpace(body.SafeState) ? "all_actuators_off" : body.SafeState),
                CurrentUsername(ctx) ?? "operator", DateTimeOffset.UtcNow);

            if (!issue.Issued)
                return ApiJson.Error(string.Join("; ", issue.Refusals), "refused");

            BuildMicromoundWidgets();
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["manifest_id"] = issue.Manifest!.ManifestId,
                ["mound_id"] = issue.Manifest.MoundId,
                ["devices"] = issue.Manifest.Hardware.Count,
                ["awaiting_collection"] = Mounds.PendingDownlinkCount(issue.Manifest.MoundId),
                // The colony authored this; the mound validates it against its own drivers and may
                // still refuse. That refusal arrives as an uplink ack and is published, never
                // inferred from the absence of one.
                ["in_force"] = false,
            });
        });

        // ---- Physical work, and the one place an approval is owed ---------------------------
        app.MapPost("/micromound/missions", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, MicromoundPermissions.Approve); if (auth is not null) return auth;
            MissionBody? body;
            try { body = await ctx.Request.ReadFromJsonAsync<MissionBody>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (body is null || string.IsNullOrWhiteSpace(body.MoundId))
                return ApiJson.Error("mound_id is required.", "bad_request");

            if (!PhysicalOrigins.TryParse(body.Origin, out var origin))
                return ApiJson.Error(
                    $"Unknown origin '{body.Origin}'. Known: {string.Join(", ", PhysicalOrigins.All)}.",
                    "bad_request");

            var parked = new ParkedMission(
                body.MoundId.Trim(), body.Steps ?? [], PhysicalOrigins.Wire(origin),
                CurrentUsername(ctx) ?? "operator", body.Reason ?? "", body.Worker ?? "",
                body.DurationSeconds ?? 900);

            var dispatch = MicromoundMissionSvc.Dispatch(Requested(parked, approvalGranted: false),
                DateTimeOffset.UtcNow);

            if (dispatch.Dispatched)
            {
                BuildMicromoundWidgets();
                return ApiJson.Ok(new Dictionary<string, object?>
                {
                    ["dispatched"] = true,
                    ["mission_id"] = dispatch.Mission!.MissionId,
                    ["charter_id"] = dispatch.Mission.CharterId,
                    ["awaiting_collection"] = Mounds.PendingDownlinkCount(parked.MoundId),
                });
            }

            if (!dispatch.ApprovalRequired)
                return ApiJson.Error(string.Join("; ", dispatch.Refusals), "refused");

            // POLICY SAYS A PERSON MUST ANSWER. The module queued nothing — a mission parked in a
            // downlink queue is authority nobody granted — so the request is parked HERE, in
            // ANTHILL's one approval queue, and carried out by the ordinary dispatcher when the
            // answer arrives. §19: no second approval framework, and this is what that costs: one
            // record, one queue, one audit trail.
            var approval = new ApprovalRequest
            {
                ActionType = ApprovalActionType.PhysicalAction,
                TargetId = parked.MoundId,
                Title = $"Physical work on Micromound '{parked.MoundId}'",
                Description = DescribeMission(parked, dispatch.PolicyReason),
                RequestedBy = parked.RequestedBy,
                Metadata = new Dictionary<string, object?>
                {
                    ["module"] = MicromoundModule.ModuleName,
                    ["mound_id"] = parked.MoundId,
                    ["origin"] = parked.Origin,
                    ["policy_reason"] = dispatch.PolicyReason,
                    ["request_json"] = JsonSerializer.Serialize(parked),
                },
            };

            Queen.Memory.SaveApprovalRequest(approval);

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["dispatched"] = false,
                ["approval_required"] = true,
                ["approval_id"] = approval.Id,
                ["reason"] = dispatch.PolicyReason,
                ["decide_with"] = $"POST /approve/{approval.Id} or POST /reject/{approval.Id}",
            });
        });

        // What the colony can PROVE about one mission, beside what the mound claimed.
        app.MapGet("/micromound/missions/{missionId}", (HttpContext ctx, string missionId) =>
        {
            var auth = RequireAuth(ctx, MicromoundPermissions.Read); if (auth is not null) return auth;

            var mission = Mounds.GetMission(missionId);
            if (mission is null) return ApiJson.Error($"No such mission '{missionId}'.", "not_found");

            var summary = MicromoundEvidenceSvc.SummarizeMission(mission.MoundId, missionId);
            var report = Mounds.GetMissionReport(mission.MoundId, missionId);

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["mission_id"] = missionId,
                ["mound_id"] = mission.MoundId,
                ["charter_id"] = mission.CharterId,
                ["expires_at"] = mission.ExpiresAt,
                // Both verdicts, never merged: the disagreement is how an operator tells missing
                // proof from a valve that failed.
                ["device_state"] = report?.State ?? "",
                ["device_detail"] = report?.Detail ?? "",
                ["colony_verified"] = summary.AllVerified,
                ["actions"] = summary.Actions,
                ["verified_actions"] = summary.Verified,
                ["detail"] = summary.Detail,
                ["records"] = Mounds.ActionsForMission(mission.MoundId, missionId),
            });
        });

        // ---- The resolver — §18. Answers a question and issues nothing. ---------------------
        //
        // Read permission, deliberately: asking whether physical work is possible changes nothing
        // about any mound, and gating the question behind the authority to act would mean an
        // operator could not find out what they would be authorizing.
        app.MapGet("/micromound/resolve", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, MicromoundPermissions.Read); if (auth is not null) return auth;

            var wanted = ctx.Request.Query["capability"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(wanted))
                return ApiJson.Error("capability is required (a capability id or a routine id).", "bad_request");

            // ORIGIN IS A PARAMETER because the answer depends on who is asking, and defaulting it
            // to `user` for a Queen-side caller would promise capacity the dispatcher then refuses.
            if (!PhysicalOrigins.TryParse(ctx.Request.Query["origin"].FirstOrDefault(), out var origin))
                return ApiJson.Error(
                    $"Unknown origin. Known: {string.Join(", ", PhysicalOrigins.All)}.", "bad_request");

            var candidates = MicromoundResolve.Resolve(wanted, origin, DateTimeOffset.UtcNow);

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["capability"] = wanted,
                ["origin"] = PhysicalOrigins.Wire(origin),
                ["eligible"] = candidates.Count(c => c.Eligible),
                // EVERY mound, not only the eligible ones: "nothing can do this" and "one could,
                // but its lease lapsed" are different answers and a filter collapses them.
                ["items"] = candidates,
            });
        });

        // ---- Unlink: the device stops being this colony's ------------------------------------
        //
        // Manage, and it takes everything with it — charters, queued downlink, evidence, actions,
        // reports and the token. A mound id can be re-minted, so anything left behind is authority
        // and proof addressed to whatever claims that id next.
        app.MapPost("/micromound/unlink", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, MicromoundPermissions.Manage); if (auth is not null) return auth;
            MoundStopRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<MoundStopRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (body is null || string.IsNullOrWhiteSpace(body.MoundId))
                return ApiJson.Error("mound_id is required.", "bad_request");

            var moundId = body.MoundId.Trim();
            if (Mounds.GetMound(moundId) is null)
                return ApiJson.Error($"No such mound '{moundId}'.", "not_found");

            var removed = Mounds.RemoveMound(moundId);
            BuildMicromoundWidgets();

            Queen.Events.Publish(new Anthill.SDK.Events.ColonyEvent
            {
                EventType = MicromoundEvents.MoundUnlinked,
                Message = $"Micromound '{moundId}' unlinked. Its charters, queued downlink, evidence "
                        + "and action records were removed with it.",
                Metadata = new Dictionary<string, object?>
                {
                    ["module"] = MicromoundModule.ModuleName,
                    ["mound_id"] = moundId,
                    ["by"] = CurrentUsername(ctx) ?? "operator",
                },
            });

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["mound_id"] = moundId,
                ["removed"] = removed,
                // The device keeps its own keypair and will keep dialling in. It gets refused as an
                // unknown mound, loudly, which is the correct answer and worth saying out loud here
                // so nobody reads the next refusal as a fault.
                ["note"] = "The device is not told. Its next beat is refused as an unknown mound; "
                         + "re-adopting it needs a freshly minted enrollment token.",
            });
        });

        // ---- Stop and resume: the one command that exists before the command path does ------
        //
        // Per-mound only, by design. The GLOBAL stop is .anthill/MICROMOUND_STOP — a file on
        // disk precisely so that no API flow, approval or otherwise, can clear it. An endpoint
        // that could engage the global stop could also be argued into clearing it; the file
        // cannot. SAFETY.md's three stop routes stay three genuinely different routes.
        app.MapPost("/micromound/stop", async (HttpContext ctx) =>
            await SetMoundStop(ctx, stopped: true));

        app.MapPost("/micromound/stop/resume", async (HttpContext ctx) =>
            await SetMoundStop(ctx, stopped: false));

        /* ---- THE AUTHORING LAYER. v0.3.8.123 --------------------------------------------------
           Three routes over `MicromoundAuthoring`, which is a translation and not a second set of
           rules — the charter and configuration services below are still the only issuers and the
           mound is still the authority that can refuse them both.

           THE PERMISSIONS SPLIT ON WHAT EACH ROUTE ACTUALLY DOES, not on which page calls it.
           Reading the form is `Read`. COMPILING it is also `Read`, because a compile issues nothing
           and reaches no device — it is the page asking "would this be accepted, and what would you
           warn me about", and gating it behind `Approve` would mean an operator could not see a
           refusal until they were entitled to act on it. SAVING is `Approve`, because a save issues
           a charter, and a charter is authority. `Manage` is not enough for the save even though
           the manifest half of it would be: the two documents go out together, and the higher of
           the two permissions governs. */
        app.MapGet("/micromound/authoring/{moundId}", (HttpContext ctx, string moundId) =>
        {
            var auth = RequireAuth(ctx, MicromoundPermissions.Read); if (auth is not null) return auth;
            var mound = Mounds.GetMound(moundId);
            if (mound is null) return ApiJson.Error($"No such mound '{moundId}'.", "not_found");

            var manifest = string.IsNullOrEmpty(mound.ManifestId) ? null : Mounds.GetManifest(mound.ManifestId);
            var charter = string.IsNullOrEmpty(mound.CharterId) ? null : Mounds.GetCharter(mound.CharterId);
            var projection = MicromoundAuthoring.Project(mound, manifest, charter);

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["form"] = AuthoringFormPayload(projection.Configuration),
                // Named out loud rather than carried silently: everything here IS preserved across
                // a save, and an operator who cannot see it on this page needs to know the advanced
                // page still holds something.
                ["unrepresented"] = projection.Unrepresented,
                // What the DEVICE reported it physically has. A fact, never a grant — the page
                // offers these and nothing else, so a form cannot be built that the mound refuses.
                ["reported"] = mound.Capabilities,
                // How to read a capability id as a sentence, and which of the seven holds it.
                ["catalog"] = MicromoundCapabilityCatalog.All.Select(c => new Dictionary<string, object?>
                {
                    ["id"] = c.Id,
                    ["label"] = c.Label,
                    ["kind"] = c.Kind,
                    ["unit"] = c.Unit,
                    ["verifiable"] = c.Verifiable,
                    // Sent rather than derived in the browser. Which of the seven holds a
                    // capability is ANTS.md's own division — sensing to the Scout, action to the
                    // Forager — and a console that hard-coded two of those names would be the
                    // second store of a roster that has exactly one.
                    ["default_ant"] = MicromoundCapabilityCatalog.DefaultAnt(c.Id),
                }).ToList(),
                ["ants"] = MicromoundRoster.Names,
                ["control_modes"] = ControlModes.All,
                ["action_levels"] = ActionLevels.All,
                ["enrolled"] = !string.IsNullOrEmpty(mound.PublicKey),
                ["stopped"] = MicromoundStop.AppliesTo(mound, MicromoundRuntime.Options),
            });
        });

        // Compile without issuing. The page calls this as the operator types, so a refusal reads as
        // a sentence beside the field that caused it rather than as a failed save.
        app.MapPost("/micromound/authoring/preview", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, MicromoundPermissions.Read); if (auth is not null) return auth;
            var read = await ReadAuthoringForm(ctx);
            if (read.Error is not null) return read.Error;

            var plan = MicromoundAuthoring.Compile(read.Form!, read.Mound!);
            return ApiJson.Ok(AuthoringPlanPayload(plan, issued: false));
        });

        app.MapPost("/micromound/authoring", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, MicromoundPermissions.Approve); if (auth is not null) return auth;
            var read = await ReadAuthoringForm(ctx);
            if (read.Error is not null) return read.Error;

            var mound = read.Mound!;
            var plan = MicromoundAuthoring.Compile(read.Form!, mound);
            if (!plan.Ok) return ApiJson.Error(string.Join("; ", plan.Refusals), "refused");

            var by = CurrentUsername(ctx) ?? "operator";
            var now = DateTimeOffset.UtcNow;

            /* THE MANIFEST GOES FIRST, AND THAT ORDER IS THE SAFE ONE. `device_limits` is the
               middle tier of SAFETY.md Layer 1's intersection, so it can only ever NARROW what a
               charter may later spend. Issuing the bound before the authority means that if the
               second call refuses, the mound is left holding tighter limits and its previous
               authority — which is the failure everyone would choose. The reverse order would open
               a window where new authority was in force against the old bounds. */
            var config = MicromoundConfigSvc.Issue(plan.Configuration!, by, now);
            if (!config.Issued) return ApiJson.Error(string.Join("; ", config.Refusals), "refused");

            var issue = MicromoundCharterSvc.Issue(plan.Charter!, by, now);
            if (!issue.Issued)
                return ApiJson.Error(
                    "The limits were saved; the authority was refused: " + string.Join("; ", issue.Refusals),
                    "refused");

            // The one field neither document carries. `AutonomyPolicy` has been on the record since
            // .114 with no command path to set it — v0.3.8.123 is when the console started deciding
            // it, and re-reading the record here rather than reusing `mound` keeps the charter
            // service's own writes (charter id, lease, quiesce) rather than overwriting them.
            var after = Mounds.GetMound(mound.MoundId) ?? mound;
            after.AutonomyPolicy = plan.Autonomy;
            Mounds.UpsertMound(after);

            BuildMicromoundWidgets();
            var payload = AuthoringPlanPayload(plan, issued: true);
            payload["manifest_id"] = config.Manifest!.ManifestId;
            payload["charter_id"] = issue.Charter!.CharterId;
            payload["expires_at"] = issue.Charter.ExpiresAt;
            // Not "delivered", and not "in force". The mound collects both documents on its next
            // beat and validates them against its own drivers; it may still refuse.
            payload["awaiting_collection"] = Mounds.PendingDownlinkCount(mound.MoundId);
            return ApiJson.Ok(payload);
        });
    }

    /// <summary>What both authoring POSTs need before they can do anything: a form and a mound.</summary>
    private sealed record AuthoringRead(FriendlyMoundConfiguration? Form, MoundRecord? Mound, IResult? Error);

    private static async Task<AuthoringRead> ReadAuthoringForm(HttpContext ctx)
    {
        AuthoringBody? body;
        try { body = await ctx.Request.ReadFromJsonAsync<AuthoringBody>(); }
        catch { return new AuthoringRead(null, null, ApiJson.Error("Invalid request body.", "bad_request")); }

        if (body is null || string.IsNullOrWhiteSpace(body.MoundId))
            return new AuthoringRead(null, null, ApiJson.Error("mound_id is required.", "bad_request"));

        var mound = Mounds.GetMound(body.MoundId.Trim());
        if (mound is null)
            return new AuthoringRead(null, null, ApiJson.Error($"No such mound '{body.MoundId}'.", "not_found"));

        var form = new FriendlyMoundConfiguration(
            mound.MoundId,
            Purpose: body.Purpose ?? "",
            Devices: [.. (body.Devices ?? []).Select(d => new FriendlyDevice(
                Capability: d.Capability ?? "",
                Device: d.Device ?? "",
                Driver: d.Driver ?? "",
                Purpose: d.Purpose ?? "",
                AssignedAnt: d.AssignedAnt ?? "",
                SafeMin: d.SafeMin,
                SafeMax: d.SafeMax,
                MaxRunSeconds: d.MaxRunSeconds,
                MinRestSeconds: d.MinRestSeconds,
                MaxTimesPerHour: d.MaxTimesPerHour,
                VerifiedBy: d.VerifiedBy ?? "",
                Settings: d.Settings))],
            Routines: body.Routines,
            ControlMode: body.ControlMode ?? ControlModes.ManualOnly,
            ActionLevel: body.ActionLevel ?? ActionLevels.WatchOnly,
            CheckInMinutes: body.CheckInMinutes ?? 15,
            AuthorityDays: body.AuthorityDays ?? 7,
            ProofIntervalSeconds: body.ProofIntervalSeconds ?? 60,
            SafeState: string.IsNullOrWhiteSpace(body.SafeState) ? MicromoundAuthoring.DefaultSafeState : body.SafeState,
            Advanced: body.Advanced);

        return new AuthoringRead(form, mound, null);
    }

    /// <summary>
    /// The friendly form on the wire, in this file's own snake_case rather than C#'s property
    /// names. Written by hand for the same reason `/micromound/mounds` serializes `MoundRecord`
    /// directly and does NOT hand-project it: there, the record IS the payload and projecting it
    /// would create a second list of its fields to keep in step. Here the payload is a different
    /// shape from the model on purpose — the console sends partial forms and reads snake_case
    /// everywhere else — so the mapping is written once, in the one direction it travels.
    /// </summary>
    private static Dictionary<string, object?> AuthoringFormPayload(FriendlyMoundConfiguration form) => new()
    {
        ["mound_id"] = form.MoundId,
        ["purpose"] = form.Purpose,
        ["control_mode"] = form.ControlMode,
        ["action_level"] = form.ActionLevel,
        ["check_in_minutes"] = form.CheckInMinutes,
        ["authority_days"] = form.AuthorityDays,
        ["proof_interval_s"] = form.ProofIntervalSeconds,
        ["safe_state"] = form.SafeState,
        ["routines"] = form.Routines ?? [],
        ["devices"] = (form.Devices ?? []).Select(d => new Dictionary<string, object?>
        {
            ["capability"] = d.Capability,
            ["device"] = d.Device,
            ["driver"] = d.Driver,
            ["purpose"] = d.Purpose,
            ["assigned_ant"] = d.AssignedAnt,
            ["safe_min"] = d.SafeMin,
            ["safe_max"] = d.SafeMax,
            ["max_run_s"] = d.MaxRunSeconds,
            ["min_rest_s"] = d.MinRestSeconds,
            ["max_per_hour"] = d.MaxTimesPerHour,
            ["verified_by"] = d.VerifiedBy,
            ["settings"] = d.Settings,
        }).ToList(),
        // Sent back exactly as it will be received. The console does not read this — it posts it
        // straight back — and that is what makes a save from the simple page lossless.
        ["advanced"] = form.Advanced,
    };

    private static Dictionary<string, object?> AuthoringPlanPayload(MoundPlan plan, bool issued) => new()
    {
        ["ok"] = plan.Ok,
        ["issued"] = issued,
        // Both lists always, even when empty. A page that reads `refusals` only on a failure and
        // `warnings` only on a success ends up with two code paths for one answer.
        ["refusals"] = plan.Refusals,
        ["warnings"] = plan.Warnings,
        ["control_mode"] = ControlModes.FromPolicy(plan.Autonomy),
        // What the operator's answers actually became, so the advanced page and this one are
        // demonstrably describing the same mound rather than two views nobody has compared.
        ["compiled"] = plan.Charter is null ? null : new Dictionary<string, object?>
        {
            ["action_ceiling"] = plan.Charter.ActionCeiling,
            ["capabilities"] = plan.Charter.Capabilities,
            ["routines"] = plan.Charter.Routines,
            ["lease_ttl_s"] = (int)plan.Charter.LeaseTtl.TotalSeconds,
            ["duration_s"] = (int)plan.Charter.Duration.TotalSeconds,
            ["evidence_required_for"] = plan.Charter.EvidenceRequiredFor,
            ["evidence_min_interval_s"] = plan.Charter.EvidenceMinIntervalSeconds,
            ["safe_state"] = plan.Charter.SafeState,
            ["device_limits"] = plan.Configuration?.DeviceLimits,
            ["hardware"] = plan.Configuration?.Hardware.Select(h => new Dictionary<string, object?>
            {
                ["device"] = h.Device,
                ["driver"] = h.Driver,
            }).ToList(),
        },
    };

    private static async Task<IResult> SetMoundStop(HttpContext ctx, bool stopped)
    {
        var auth = RequireAuth(ctx, MicromoundPermissions.Approve); if (auth is not null) return auth;
        MoundStopRequest? body;
        try { body = await ctx.Request.ReadFromJsonAsync<MoundStopRequest>(); }
        catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
        if (body is null || string.IsNullOrWhiteSpace(body.MoundId))
            return ApiJson.Error(
                "mound_id is required. The global stop is the .anthill/MICROMOUND_STOP file, " +
                "deliberately out of this API's reach.", "bad_request");

        var mound = Mounds.GetMound(body.MoundId.Trim());
        if (mound is null) return ApiJson.Error($"No such mound '{body.MoundId}'.", "not_found");

        mound.Stopped = stopped;
        Mounds.UpsertMound(mound);
        Queen.Events.Publish(new Anthill.SDK.Events.ColonyEvent
        {
            EventType = stopped ? MicromoundEvents.StopInEffect : MicromoundEvents.StopCleared,
            Message = stopped
                ? $"Operator stop engaged for Micromound '{mound.MoundId}'. Its next sync carries the stop order."
                : $"Operator stop cleared for Micromound '{mound.MoundId}'. Resume is explicit, never automatic.",
            Metadata = new Dictionary<string, object?>
            {
                ["module"] = MicromoundModule.ModuleName,
                ["mound_id"] = mound.MoundId,
                ["stopped"] = stopped,
                ["by"] = CurrentUsername(ctx) ?? "operator",
                ["global_stop_engaged"] = MicromoundStop.IsEngaged(MicromoundRuntime.Options),
            },
        });
        BuildMicromoundWidgets();

        return ApiJson.Ok(new Dictionary<string, object?>
        {
            ["mound_id"] = mound.MoundId,
            ["stopped"] = mound.Stopped,
            ["global_stop"] = MicromoundStop.IsEngaged(MicromoundRuntime.Options),
        });
    }
}
#endif
