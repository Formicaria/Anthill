using Anthill.Modules.Micromound;
using Micromound.Protocol;
using Micromound.Sim;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// PROTOCOL.md §3. Every case here exists to defend one sentence: a mound's identity is something
/// an operator granted, not something a device asserted.
/// </summary>
[Collection(MicromoundCollection.Name)]
public class EnrollmentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-15T09:00:00Z");

    private static EnrollmentRequest RequestFor(string moundId, string token, SimMound device,
        string tier = MoundTiers.EdgeQueen) =>
        new(moundId, token, Convert.ToHexStringLower(device.PublicKey), tier,
            "raspberry-pi-5", ["sense.temp", "act.relay_1"], ProtocolVersion.Current);

    [Fact]
    public void An_operator_mints_a_token_and_the_device_binds_its_key()
    {
        using var workspace = new TempWorkspace();
        var store = new InMemoryMoundStore();
        var bus = new RecordingEventBus();
        var enrollment = new MicromoundEnrollment(store, bus);
        var device = new SimMound("mm-1");

        var minted = enrollment.MintToken("mm-1", "Shed Pi", MoundTiers.EdgeQueen, "tyler", Now);

        // Before enrolling, the record exists but trusts nothing.
        Assert.Equal("", store.GetMound("mm-1")!.PublicKey);

        var result = enrollment.Enroll(RequestFor("mm-1", minted.Token, device), Now);

        Assert.True(result.Accepted, result.Reason);
        var record = store.GetMound("mm-1")!;
        Assert.Equal(Convert.ToHexStringLower(device.PublicKey), record.PublicKey);
        Assert.Equal("raspberry-pi-5", record.HardwareProfile);
        Assert.Contains("act.relay_1", record.Capabilities);
        Assert.True(bus.Saw(MicromoundEvents.MoundEnrolled));
    }

    [Fact]
    public void The_plaintext_token_is_never_stored()
    {
        using var workspace = new TempWorkspace();
        var store = new InMemoryMoundStore();
        var enrollment = new MicromoundEnrollment(store, new RecordingEventBus());

        var minted = enrollment.MintToken("mm-1", "Shed Pi", MoundTiers.EdgeQueen, "tyler", Now);
        var stored = store.GetEnrollmentToken("mm-1")!;

        Assert.NotEqual(minted.Token, stored.TokenHash);
        Assert.Equal(MicromoundEnrollment.HashToken(minted.Token), stored.TokenHash);
    }

    [Fact]
    public void A_token_burns_on_use()
    {
        using var workspace = new TempWorkspace();
        var store = new InMemoryMoundStore();
        var enrollment = new MicromoundEnrollment(store, new RecordingEventBus());
        var device = new SimMound("mm-1");

        var minted = enrollment.MintToken("mm-1", "Shed Pi", MoundTiers.EdgeQueen, "tyler", Now);
        Assert.True(enrollment.Enroll(RequestFor("mm-1", minted.Token, device), Now).Accepted);

        var replay = enrollment.Enroll(RequestFor("mm-1", minted.Token, device), Now);

        Assert.False(replay.Accepted);
        Assert.Contains("already used", replay.Reason);
    }

    [Fact]
    public void A_wrong_token_is_refused_and_audited()
    {
        using var workspace = new TempWorkspace();
        var store = new InMemoryMoundStore();
        var bus = new RecordingEventBus();
        var enrollment = new MicromoundEnrollment(store, bus);
        var device = new SimMound("mm-1");

        enrollment.MintToken("mm-1", "Shed Pi", MoundTiers.EdgeQueen, "tyler", Now);

        var result = enrollment.Enroll(RequestFor("mm-1", "not-the-token", device), Now);

        Assert.False(result.Accepted);
        Assert.True(bus.Saw(MicromoundEvents.EnrollmentRefused));
        Assert.Equal("", store.GetMound("mm-1")!.PublicKey); // nothing was bound
    }

    [Fact]
    public void An_expired_token_is_refused()
    {
        using var workspace = new TempWorkspace();
        var store = new InMemoryMoundStore();
        var enrollment = new MicromoundEnrollment(store, new RecordingEventBus());
        var device = new SimMound("mm-1");

        var minted = enrollment.MintToken("mm-1", "Shed Pi", MoundTiers.EdgeQueen, "tyler", Now);
        var tooLate = Now.AddMinutes(MicromoundRuntime.Options.EnrollmentTokenTtlMinutes + 1);

        var result = enrollment.Enroll(RequestFor("mm-1", minted.Token, device), tooLate);

        Assert.False(result.Accepted);
        Assert.Contains("expired", result.Reason);
    }

    [Fact]
    public void A_device_the_operator_never_created_cannot_enroll_itself()
    {
        using var workspace = new TempWorkspace();
        var store = new InMemoryMoundStore();
        var enrollment = new MicromoundEnrollment(store, new RecordingEventBus());

        var result = enrollment.Enroll(RequestFor("mm-ghost", "anything", new SimMound("mm-ghost")), Now);

        Assert.False(result.Accepted);
        Assert.Null(store.GetMound("mm-ghost"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("00ff")]                 // right alphabet, wrong length
    [InlineData("0011223344556677889900112233445566778899001122334455667788990011223344")]
    public void A_public_key_that_is_not_32_bytes_of_hex_is_refused(string publicKeyHex)
    {
        using var workspace = new TempWorkspace();
        var store = new InMemoryMoundStore();
        var enrollment = new MicromoundEnrollment(store, new RecordingEventBus());

        var minted = enrollment.MintToken("mm-1", "Shed Pi", MoundTiers.EdgeQueen, "tyler", Now);

        var result = enrollment.Enroll(new EnrollmentRequest(
            "mm-1", minted.Token, publicKeyHex, MoundTiers.EdgeQueen, "pi", [], ProtocolVersion.Current), Now);

        Assert.False(result.Accepted);
        Assert.Contains("public key", result.Reason);
    }

    [Fact]
    public void A_protocol_version_mismatch_refuses_loudly_rather_than_guessing()
    {
        using var workspace = new TempWorkspace();
        var store = new InMemoryMoundStore();
        var enrollment = new MicromoundEnrollment(store, new RecordingEventBus());
        var device = new SimMound("mm-1");

        var minted = enrollment.MintToken("mm-1", "Shed Pi", MoundTiers.EdgeQueen, "tyler", Now);

        var result = enrollment.Enroll(new EnrollmentRequest(
            "mm-1", minted.Token, Convert.ToHexStringLower(device.PublicKey), MoundTiers.EdgeQueen,
            "pi", [], ProtocolVersion.Current + 1), Now);

        Assert.False(result.Accepted);
        Assert.Contains("protocol version", result.Reason);
    }

    [Fact]
    public void Re_minting_untrusts_the_key_that_was_bound_before()
    {
        using var workspace = new TempWorkspace();
        var store = new InMemoryMoundStore();
        var enrollment = new MicromoundEnrollment(store, new RecordingEventBus());
        var stolen = new SimMound("mm-1");

        var first = enrollment.MintToken("mm-1", "Shed Pi", MoundTiers.EdgeQueen, "tyler", Now);
        enrollment.Enroll(RequestFor("mm-1", first.Token, stolen), Now);
        Assert.NotEqual("", store.GetMound("mm-1")!.PublicKey);

        // The operator re-mints — deliberately un-trusting the device that holds the old key.
        enrollment.MintToken("mm-1", "Shed Pi", MoundTiers.EdgeQueen, "tyler", Now);

        Assert.Equal("", store.GetMound("mm-1")!.PublicKey);
    }

    [Fact]
    public void An_unknown_tier_is_refused_at_the_door()
    {
        using var workspace = new TempWorkspace();
        var store = new InMemoryMoundStore();
        var enrollment = new MicromoundEnrollment(store, new RecordingEventBus());

        Assert.Throws<ArgumentException>(() =>
            enrollment.MintToken("mm-1", "Shed Pi", "quantum_queen", "tyler", Now));
    }
}
