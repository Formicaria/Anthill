using System.Text.Json;
using System.Text.Json.Serialization;
using Anthill.Core.Configuration;
using Anthill.Modules.Homelab.Integrations;
using Anthill.Modules.Micromound;
using Micromound.Protocol;

namespace Anthill.Api;

/// <summary>
/// MICROMOUND M1 wiring — the composition the module's README promised would happen here, and
/// nowhere else. The module proved the authority logic database-free and network-free; this file
/// gives it a database, seven endpoints, and a card on the Integrations tab, and adds no
/// authority of its own.
///
/// The endpoint set is PROTOCOL.md §9's M1 slice exactly. `/micromound/missions` and
/// `/micromound/charters` are deliberately NOT here — they are the command path, and they arrive
/// with M2 and M4 behind the approval pipeline. The only downlink these endpoints can produce is
/// a stop order.
///
/// Two auth models, on purpose. Operator endpoints go through <c>RequireAuth</c> like everything
/// else. The two `/micromound/v0/*` device endpoints do NOT: a mound has no session — its
/// enrollment token (once) and its Ed25519 signature (every beat) are the authentication, checked
/// inside the module against the store. A session gate on those would not add security; it would
/// add a shared credential every device had to hold.
/// </summary>
public static partial class ApiHost
{
    public static IMoundStore Mounds { get; private set; } = null!;
    private static MicromoundEnrollment MicromoundEnroll = null!;
    private static MicromoundSync MicromoundSyncSvc = null!;

    /// <summary>
    /// After InitHomelab (the integration catalog convention lives there) and after module load
    /// (MicromoundRuntime must already be configured — the store resolves its database path and
    /// cipher from it).
    /// </summary>
    private static void InitMicromound()
    {
        Mounds = new SqliteMoundStore();
        MicromoundEnroll = new MicromoundEnrollment(Mounds, Queen.Events);
        MicromoundSyncSvc = new MicromoundSync(Mounds, Queen.Events);
        IntegrationCatalog.Register(new MicromoundIntegrationDefinition());
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
            [MicromoundWidgetKinds.MissionStatus] = MicromoundWidgets.BuildMissionStatus(mounds),
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

    private sealed record MicromoundEnrollBody(
        [property: JsonPropertyName("mound_id")] string? MoundId,
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("public_key")] string? PublicKeyHex,
        [property: JsonPropertyName("tier")] string? Tier,
        [property: JsonPropertyName("hardware_profile")] string? HardwareProfile,
        [property: JsonPropertyName("capabilities")] List<string>? Capabilities,
        [property: JsonPropertyName("protocol_version")] int ProtocolVersion);

    private sealed record MicromoundSyncBody(
        [property: JsonPropertyName("mound_id")] string? MoundId,
        [property: JsonPropertyName("envelopes")] List<JsonElement>? Envelopes);

    private sealed record MoundStopRequest(
        [property: JsonPropertyName("mound_id")] string? MoundId);

    private static void MapMicromoundEndpoints(WebApplication app)
    {
        // ---- Fleet: what the colony can see -------------------------------------------------
        app.MapGet("/micromound/mounds", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, MicromoundPermissions.Read); if (auth is not null) return auth;
            var options = MicromoundRuntime.Options;
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["global_stop"] = MicromoundStop.IsEngaged(options),
                ["stop_file"] = MicromoundStop.PathFor(options),
                ["phase"] = "M1",
                ["command_path"] = false,
                ["items"] = Mounds.ListMounds(),
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
        app.MapPost("/micromound/v0/enroll", async (HttpContext ctx) =>
        {
            MicromoundEnrollBody? body;
            try { body = await ctx.Request.ReadFromJsonAsync<MicromoundEnrollBody>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (body is null || string.IsNullOrWhiteSpace(body.MoundId))
                return ApiJson.Error("mound_id is required.", "bad_request");

            var result = MicromoundEnroll.Enroll(new EnrollmentRequest(
                body.MoundId.Trim(), body.Token ?? "", body.PublicKeyHex ?? "", body.Tier ?? "",
                body.HardwareProfile ?? "", body.Capabilities ?? [], body.ProtocolVersion),
                DateTimeOffset.UtcNow);

            if (result.Accepted) BuildMicromoundWidgets();
            // Refusal reasons go back to the device: the operator minted this token minutes ago
            // and is standing next to the hardware — a silent refusal helps only an attacker's
            // patience, and every refusal is already a loud colony event.
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["accepted"] = result.Accepted,
                ["reason"] = result.Reason,
                ["colony_version"] = MicromoundRuntime.Options.ColonyVersion,
                ["protocol_version"] = ProtocolVersion.Current,
                ["sync_interval_s"] = result.Mound?.SyncIntervalSeconds,
            });
        });

        // ---- Device: the sync beat (PROTOCOL.md §1). The signature is the auth. -------------
        app.MapPost("/micromound/v0/sync", async (HttpContext ctx) =>
        {
            MicromoundSyncBody? body;
            try { body = await ctx.Request.ReadFromJsonAsync<MicromoundSyncBody>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (body is null || string.IsNullOrWhiteSpace(body.MoundId))
                return ApiJson.Error("mound_id is required.", "bad_request");

            var envelopes = new List<Envelope>();
            foreach (var element in body.Envelopes ?? [])
            {
                var envelope = JsonSerializer.Deserialize<Envelope>(element.GetRawText());
                if (envelope is null)
                    return ApiJson.Error("An envelope in the batch does not parse.", "bad_request");
                envelopes.Add(envelope);
            }

            var outcome = MicromoundSyncSvc.AcceptUplink(body.MoundId.Trim(), envelopes,
                DateTimeOffset.UtcNow);
            BuildMicromoundWidgets(); // refusals move the fleet picture too

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["accepted"] = outcome.Accepted,
                ["refusals"] = outcome.Refusals,
                ["accepted_through_seq"] = outcome.AcceptedThroughSeq,
                ["anchor_digest"] = outcome.AnchorDigest,
                ["stop"] = outcome.StopInEffect,
                // M1's whole downlink vocabulary: a stop order, or nothing.
                ["downlink"] = MicromoundSyncSvc.DownlinkKindsFor(outcome),
            });
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
    }

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
            EventType = stopped ? MicromoundEvents.StopInEffect : "micromound_stop_cleared",
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
