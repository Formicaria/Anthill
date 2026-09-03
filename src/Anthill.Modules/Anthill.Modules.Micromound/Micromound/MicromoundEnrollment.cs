using System.Security.Cryptography;
using System.Text;
using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>What an operator gets back when they add a mound. The token is shown exactly once.</summary>
public sealed record MintedEnrollment(string MoundId, string Token, string ExpiresAt);

/// <summary>
/// What a device sends to <c>/micromound/v0/enroll</c> — PROTOCOL.md §3 step 2.
/// </summary>
/// <param name="MoundId">
/// USUALLY EMPTY, and that is not an omission. `HttpEnrollmentClient` — the real device client —
/// posts a token, a public key, a hardware profile and a tier, and no mound id: which mound this is
/// was settled by the OPERATOR when they minted the token, and is not a claim a device gets to
/// make. When it IS supplied (this colony's own tests, a console-driven enrolment) it is treated as
/// a cross-check that must agree with the token, never as the lookup.
/// </param>
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

    /// <summary>
    /// Device action: bind a public key to the record, burn the token, or refuse loudly.
    ///
    /// v0.3.8.114 — THE TOKEN IS THE LOOKUP. It always was the authority (it is what an operator
    /// minted for one mound), and M1 nonetheless found the mound by an id the device supplied and
    /// then checked the token against it. That is backwards, and it is also unusable: the actual
    /// device client sends no mound id at all, so every real enrolment would have been refused as
    /// "no such mound" — an integration whose front door nothing could walk through.
    /// </summary>
    public EnrollmentResult Enroll(EnrollmentRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        var presented = HashToken(request.Token);

        // Constant-time against every outstanding token, and the scan does not stop at the first
        // match: an early return would make "how long did the refusal take" a search for the token.
        EnrollmentToken? token = null;
        foreach (var candidate in _store.AllEnrollmentTokens())
            if (FixedTimeEquals(candidate.TokenHash, presented)) token = candidate;

        if (token is null)
            return Refuse(request.MoundId, "enrollment token is not one this colony is waiting for");

        // A supplied mound id is a CROSS-CHECK, never the lookup. A device configured for one mound
        // presenting another's token is a misconfiguration that would otherwise bind the wrong key
        // and then fail every subsequent beat with a signature refusal nobody could explain.
        if (!string.IsNullOrWhiteSpace(request.MoundId)
            && !string.Equals(request.MoundId, token.MoundId, StringComparison.Ordinal))
            return Refuse(request.MoundId,
                $"this token was issued for '{token.MoundId}', and the device says it is '{request.MoundId}'");

        if (token.IsBurned) return Refuse(token.MoundId, "enrollment token already used");

        var mound = _store.GetMound(token.MoundId);
        if (mound is null) return Refuse(token.MoundId, "no such mound; an operator must create it first");

        if (!ProtocolTime.TryParse(token.ExpiresAt, out var expires) || now >= expires)
            return Refuse(token.MoundId, "enrollment token expired");

        if (!MoundTiers.IsKnown(request.Tier))
            return Refuse(token.MoundId, $"unknown tier '{request.Tier}'");

        if (!TryDecodeKey(request.PublicKeyHex, out var publicKey))
            return Refuse(token.MoundId, "public key is not 32 bytes of hex");

        if (request.ProtocolVersion != ProtocolVersion.Current)
            return Refuse(token.MoundId,
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
