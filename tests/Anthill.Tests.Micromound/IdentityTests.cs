using System.Text.Json;
using Anthill.Modules.Micromound;
using Micromound.Crypto;
using Micromound.Protocol;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// THE COLONY CAN SIGN, AND ITS SIGNATURE IS THE ONE A MOUND WILL ACCEPT. v0.3.8.114.
///
/// `.60` shipped the uplink and said what it had not built: "M1 has no command path, so the colony
/// can see mounds and cannot direct them." Everything a controller does in `UPSTREAM.md`'s
/// sense — charters, configuration, missions, a stop that is an order rather than a local
/// refusal — rests on a key. These are the facts about that key.
///
/// They are asserted against the PROTOCOL LIBRARY rather than against our own re-derivation of it:
/// the envelope's canonical bytes, its signer and its verifier all come from `Micromound.Protocol`
/// and `Micromound.Crypto`, so a test that agreed with Anthill and disagreed with a real mound
/// could not pass here.
/// </summary>
[Collection(MicromoundCollection.Name)]
public class IdentityTests
{
    /// <summary>
    /// A minimal well-formed envelope. `Body` is a real empty object rather than the default
    /// JsonElement: an unset one is `ValueKind.Undefined`, which does not serialize, so
    /// `CanonicalBytes` would throw before any of these tests reached their assertion.
    /// </summary>
    private static Envelope Downlink(string moundId, string kind) => new()
    {
        Id = Guid.NewGuid().ToString(),
        MoundId = moundId,
        Kind = kind,
        Seq = 1,
        SentAt = DateTimeOffset.UtcNow.ToWire(),
        Body = JsonSerializer.SerializeToElement(new { }),
    };

    /// <summary>
    /// THE IDENTITY IS MINTED ONCE AND SURVIVES A RESTART. A colony that generated a new key each
    /// time it started would orphan its fleet on every restart — every mound holds the public key
    /// it was handed at enrollment and would refuse each later charter as `unknown_key`, correctly,
    /// while looking to an operator like the fleet had simply stopped obeying.
    /// </summary>
    [Fact]
    public void TheControllerIdentity_IsStableAcrossRestarts()
    {
        var store = new InMemoryMoundStore();

        var first = new MicromoundIdentity(store).PublicKeyHex;

        // A second identity over the same store is a restart: same store, new object graph.
        var second = new MicromoundIdentity(store).PublicKeyHex;

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);                       // 32 bytes, lowercase hex
        Assert.Equal(first, first.ToLowerInvariant());
    }

    /// <summary>
    /// AND IT IS NOT A CONSTANT. A key derived from something fixed — a build, a machine name, the
    /// empty seed — would be the same key in every colony, so any operator could sign another
    /// operator's fleet. Two independent stores must mint independent identities.
    /// </summary>
    [Fact]
    public void TwoColonies_DoNotShareAnIdentity()
    {
        var one = new MicromoundIdentity(new InMemoryMoundStore()).PublicKeyHex;
        var two = new MicromoundIdentity(new InMemoryMoundStore()).PublicKeyHex;

        Assert.NotEqual(one, two);
    }

    /// <summary>
    /// A SIGNED DOWNLINK VERIFIES UNDER THE KEY ID A MOUND LOOKS IT UP BY.
    ///
    /// `KeyIds.Controller` is not our choice — it is the name the protocol gives the upstream
    /// signer, and a mound resolves the colony's public key under exactly that string. Signing
    /// under any other id produces an envelope that is cryptographically perfect and refused as
    /// `unknown_key`.
    /// </summary>
    [Fact]
    public void ASignedDownlink_VerifiesAsTheController()
    {
        var identity = new MicromoundIdentity(new InMemoryMoundStore());

        var envelope = identity.Sign(Downlink("mm-test", EnvelopeKinds.Charter));

        Assert.StartsWith("ed25519:", envelope.Signature, StringComparison.Ordinal);

        var directory = new InMemoryPublicKeyDirectory();
        directory.Register(KeyIds.Controller, Convert.FromHexString(identity.PublicKeyHex));

        var check = new Ed25519EnvelopeVerifier(directory)
            .Verify(KeyIds.Controller, envelope.CanonicalBytes(), envelope.Signature);

        Assert.True(check.IsValid, check.Describe());
    }

    /// <summary>
    /// AND A TAMPERED ENVELOPE DOES NOT. The point of signing downlink at all: a charter whose
    /// capabilities were widened in transit must fail, not merely look different.
    /// </summary>
    [Fact]
    public void ATamperedDownlink_IsRefused()
    {
        var identity = new MicromoundIdentity(new InMemoryMoundStore());
        var envelope = identity.Sign(Downlink("mm-test", EnvelopeKinds.Charter));

        var directory = new InMemoryPublicKeyDirectory();
        directory.Register(KeyIds.Controller, Convert.FromHexString(identity.PublicKeyHex));
        var verifier = new Ed25519EnvelopeVerifier(directory);

        // Change one field the canonical bytes cover, leaving the signature alone.
        envelope.Kind = EnvelopeKinds.Stop;

        var check = verifier.Verify(KeyIds.Controller, envelope.CanonicalBytes(), envelope.Signature);

        Assert.False(check.IsValid);
        Assert.Equal(SignatureStatus.BadSignature, check.Status);
    }

    /// <summary>
    /// UPLINK VERIFIES AGAINST THE MOUND'S OWN KEY, RESOLVED FROM THE REGISTRY — and an unenrolled
    /// mound resolves to nothing.
    ///
    /// PROTOCOL.md §2: "a key the verifier's directory does not hold is a refusal, because
    /// enrollment is the only way a key becomes known." There is no trust-on-first-use, so a device
    /// that has not been GRANTED an identity cannot assert one by signing with it — the refusal
    /// must be `unknown_key` rather than `bad_signature`, because those are different facts and
    /// only one of them means "somebody tampered".
    /// </summary>
    [Fact]
    public void UplinkFromAnUnenrolledMound_IsUnknownKeyRatherThanBadSignature()
    {
        var store = new InMemoryMoundStore();
        var identity = new MicromoundIdentity(store);

        // The device has a real key and signs correctly with it. It simply was never enrolled.
        var device = Ed25519KeyPair.Generate();
        var envelope = EnvelopeSigning.Sign(Downlink("mm-stranger", EnvelopeKinds.MoundSync),
            new Ed25519EnvelopeSigner("mm-stranger", device));

        var check = identity.UplinkVerifier()
            .Verify("mm-stranger", envelope.CanonicalBytes(), envelope.Signature);

        Assert.False(check.IsValid);
        Assert.Equal(SignatureStatus.UnknownKey, check.Status);
    }

    /// <summary>And once enrolled, the same envelope verifies.</summary>
    [Fact]
    public void UplinkFromAnEnrolledMound_VerifiesAgainstItsBoundKey()
    {
        var store = new InMemoryMoundStore();
        var identity = new MicromoundIdentity(store);

        var device = Ed25519KeyPair.Generate();
        store.UpsertMound(new MoundRecord
        {
            MoundId = "mm-known",
            Name = "Workshop",
            PublicKey = Convert.ToHexStringLower(device.PublicKey),
        });

        var envelope = EnvelopeSigning.Sign(Downlink("mm-known", EnvelopeKinds.MoundSync),
            new Ed25519EnvelopeSigner("mm-known", device));

        var check = identity.UplinkVerifier()
            .Verify("mm-known", envelope.CanonicalBytes(), envelope.Signature);

        Assert.True(check.IsValid, check.Describe());
    }

    /// <summary>
    /// THE DIRECTORY IS READ LIVE, NOT CACHED. An operator who decommissions a mound has revoked
    /// it; a verifier holding a snapshot would keep accepting its traffic until something happened
    /// to rebuild it, and "until something happened" is not a security property.
    /// </summary>
    [Fact]
    public void ARevokedMound_StopsVerifyingImmediately()
    {
        var store = new InMemoryMoundStore();
        var identity = new MicromoundIdentity(store);

        var device = Ed25519KeyPair.Generate();
        store.UpsertMound(new MoundRecord
        {
            MoundId = "mm-gone",
            Name = "Rover",
            PublicKey = Convert.ToHexStringLower(device.PublicKey),
        });

        var envelope = EnvelopeSigning.Sign(Downlink("mm-gone", EnvelopeKinds.MoundSync),
            new Ed25519EnvelopeSigner("mm-gone", device));

        Assert.True(identity.UplinkVerifier()
            .Verify("mm-gone", envelope.CanonicalBytes(), envelope.Signature).IsValid);

        store.RemoveMound("mm-gone");

        var after = identity.UplinkVerifier()
            .Verify("mm-gone", envelope.CanonicalBytes(), envelope.Signature);

        Assert.False(after.IsValid);
        Assert.Equal(SignatureStatus.UnknownKey, after.Status);
    }

    /// <summary>
    /// THE SEED SURVIVES A REAL RESTART, THROUGH THE REAL STORE, AND THROUGH THE CIPHER.
    ///
    /// The in-memory store proves the logic; this proves the persistence, which is where an
    /// encrypted round trip can go wrong without anything else noticing — the failure mode is a
    /// colony that mints a fresh identity on every boot and orphans its fleet, and it would look
    /// exactly like a working colony until the first charter after a restart.
    /// </summary>
    [Fact]
    public void TheSeed_RoundTripsThroughTheSqliteStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anthill-mm-id-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "t.db");

            string first;
            using (var store = new SqliteMoundStore(path)) first = new MicromoundIdentity(store).PublicKeyHex;

            // A new store object over the same file is the restart.
            using (var store = new SqliteMoundStore(path))
                Assert.Equal(first, new MicromoundIdentity(store).PublicKeyHex);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// AND A SECOND WRITE CANNOT REPLACE IT. `PutControllerSeed` is INSERT OR IGNORE rather than an
    /// upsert, because silently rotating the colony's key orphans every enrolled mound — and the
    /// symptom, a fleet that refuses every charter as `unknown_key`, is indistinguishable from a
    /// fleet that has stopped working for some other reason.
    /// </summary>
    [Fact]
    public void TheSeed_IsNotSilentlyRotated()
    {
        var store = new InMemoryMoundStore();
        var identity = new MicromoundIdentity(store);
        var original = identity.PublicKeyHex;

        store.PutControllerSeed(Ed25519KeyPair.Generate().Seed);

        // The already-minted identity keeps its key…
        Assert.Equal(original, identity.PublicKeyHex);

        // …and the SQLite store refuses the overwrite outright, which is where it matters.
        var dir = Path.Combine(Path.GetTempPath(), "anthill-mm-rot-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            using var sqlite = new SqliteMoundStore(Path.Combine(dir, "t.db"));
            var minted = new MicromoundIdentity(sqlite).PublicKeyHex;

            sqlite.PutControllerSeed(Ed25519KeyPair.Generate().Seed);

            Assert.Equal(minted, new MicromoundIdentity(sqlite).PublicKeyHex);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
