using System.Security.Cryptography;
using System.Text;
using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>What an operator gets back when they add a mound. The token is shown exactly once.</summary>
public sealed record MintedEnrollment(string MoundId, string Token, string ExpiresAt);

/// <summary>What a device sends to <c>/micromound/v0/enroll</c> — PROTOCOL.md §3 step 2.</summary>
public sealed record EnrollmentRequest(
    string MoundId,
    string Token,
    string PublicKeyHex,
    string Tier,
    string HardwareProfile,
    IReadOnlyList<string> Capabilities,
    int ProtocolVersion);

public sealed record EnrollmentResult(bool Accepted, string Reason, MoundRecord? Mound)
{
    public static EnrollmentResult Refused(string reason) => new(false, reason, null);
}

/// <summary>
/// Enrollment — PROTOCOL.md §3. The whole flow exists to make one thing true: a mound's identity
/// is something an operator granted, not something a device asserted.
///
/// So the token is single-use and burned on success; the device generates its own keypair and
/// sends only the public half; and re-enrollment after a reflash or a key rotation needs a fresh
/// operator-minted token, because a device that can re-key itself can also re-key itself after
/// being stolen.
/// </summary>
public sealed class MicromoundEnrollment(IMoundStore store, IEventBus events)
{
    private readonly IMoundStore _store = store;
    private readonly IEventBus _events = events;

    /// <summary>
    /// Operator action (requires <see cref="MicromoundPermissions.Manage"/>): create the mound
    /// record and mint its one-time token. The plaintext token is returned to the caller and
    /// never stored — only a hash goes to the store.
    /// </summary>
    public MintedEnrollment MintToken(string moundId, string name, string tier, string issuedBy,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(moundId)) throw new ArgumentException("mound_id is required", nameof(moundId));
        if (!MoundTiers.IsKnown(tier)) throw new ArgumentException($"unknown tier '{tier}'", nameof(tier));

        var options = MicromoundRuntime.Options;
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var expiresAt = now.AddMinutes(options.EnrollmentTokenTtlMinutes);

        var existing = _store.GetMound(moundId);
        _store.UpsertMound(new MoundRecord
        {
            MoundId = moundId,
            Name = string.IsNullOrWhiteSpace(name) ? moundId : name,
            Tier = tier,
            // A re-mint clears the bound key: the operator is deliberately un-trusting the old one.
            PublicKey = "",
            HardwareProfile = existing?.HardwareProfile ?? "",
            Capabilities = existing?.Capabilities ?? [],
            EnrolledAt = "",
            LastSeen = existing?.LastSeen ?? "",
            LastSeq = -1,
            LastDigest = "",
            Stopped = existing?.Stopped ?? false
        });

        _store.PutEnrollmentToken(new EnrollmentToken
        {
            MoundId = moundId,
            TokenHash = HashToken(token),
            IssuedAt = now.ToWire(),
            ExpiresAt = expiresAt.ToWire(),
            IssuedBy = issuedBy
        });

        Publish(MicromoundEvents.EnrollmentTokenMinted, $"Micromound enrollment token minted for '{moundId}'.",
            new Dictionary<string, object?>
            {
                ["mound_id"] = moundId,
                ["tier"] = tier,
                ["issued_by"] = issuedBy,
                ["expires_at"] = expiresAt.ToWire()
            });

        return new MintedEnrollment(moundId, token, expiresAt.ToWire());
    }

    /// <summary>Device action: bind a public key to the record, burn the token, or refuse loudly.</summary>
    public EnrollmentResult Enroll(EnrollmentRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mound = _store.GetMound(request.MoundId);
        if (mound is null) return Refuse(request.MoundId, "no such mound; an operator must create it first");

        var token = _store.GetEnrollmentToken(request.MoundId);
        if (token is null) return Refuse(request.MoundId, "no enrollment token outstanding");
        if (token.IsBurned) return Refuse(request.MoundId, "enrollment token already used");

        if (!ProtocolTime.TryParse(token.ExpiresAt, out var expires) || now >= expires)
            return Refuse(request.MoundId, "enrollment token expired");

        if (!FixedTimeEquals(token.TokenHash, HashToken(request.Token)))
            return Refuse(request.MoundId, "enrollment token does not match");

        if (!MoundTiers.IsKnown(request.Tier))
            return Refuse(request.MoundId, $"unknown tier '{request.Tier}'");

        if (!TryDecodeKey(request.PublicKeyHex, out var publicKey))
            return Refuse(request.MoundId, "public key is not 32 bytes of hex");

        if (request.ProtocolVersion != ProtocolVersion.Current)
            return Refuse(request.MoundId,
                $"protocol version {request.ProtocolVersion} does not match colony version {ProtocolVersion.Current}");

        mound.PublicKey = Convert.ToHexStringLower(publicKey);
        mound.Tier = request.Tier;
        mound.HardwareProfile = request.HardwareProfile;
        mound.Capabilities = [.. request.Capabilities];
        mound.EnrolledAt = now.ToWire();
        mound.ProtocolVersion = request.ProtocolVersion;
        mound.LastSeq = -1;
        mound.LastDigest = "";
        _store.UpsertMound(mound);

        token.BurnedAt = now.ToWire();
        _store.PutEnrollmentToken(token);

        Publish(MicromoundEvents.MoundEnrolled, $"Micromound '{mound.MoundId}' enrolled.",
            new Dictionary<string, object?>
            {
                ["mound_id"] = mound.MoundId,
                ["tier"] = mound.Tier,
                ["capabilities"] = mound.Capabilities.Count
            });

        return new EnrollmentResult(true, "", mound);
    }

    private EnrollmentResult Refuse(string moundId, string reason)
    {
        Publish(MicromoundEvents.EnrollmentRefused, $"Micromound enrollment refused for '{moundId}': {reason}",
            new Dictionary<string, object?> { ["mound_id"] = moundId, ["reason"] = reason });

        return EnrollmentResult.Refused(reason);
    }

    private void Publish(string eventType, string message, Dictionary<string, object?> metadata)
    {
        metadata["module"] = MicromoundModule.ModuleName;
        _events.Publish(new ColonyEvent { EventType = eventType, Message = message, Metadata = metadata });
    }

    internal static string HashToken(string? token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? "")));

    internal static bool TryDecodeKey(string? hex, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(hex)) return false;

        try
        {
            var decoded = Convert.FromHexString(hex);
            if (decoded.Length != 32) return false;
            key = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool FixedTimeEquals(string? a, string? b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a ?? ""), Encoding.UTF8.GetBytes(b ?? ""));
}
