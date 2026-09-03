using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// THE SHAPE THE DEVICE ACTUALLY SPEAKS — v0.3.8.114, and this guard exists because its absence
/// cost this integration its front door.
///
/// M1's two device endpoints were written from `PROTOCOL.md` rather than read off the client that
/// has to call them, and they were wrong in four places at once. `/v0/enroll` required a `mound_id`
/// the device does not send, read the key from `public_key` where `HttpEnrollmentClient` writes
/// `device_public_key`, and returned no `controller_public_key` — so a device that somehow got past
/// the first two could never verify a downlink envelope. `/v0/sync` expected `{mound_id,
/// envelopes[]}` and answered an object, where `HttpSyncTransport` POSTs one raw envelope and
/// parses the whole response body as `List&lt;Envelope&gt;`.
///
/// Every one of those was invisible to every test, because both ends of every test were ours.
///
/// SO THIS READS THE OTHER REPOSITORY'S SOURCE. That is the weakest tier in `docs/GUARDS.md` and it
/// is deliberately chosen here, because the stronger tiers are not available across this boundary:
/// the field names live in `private sealed record`s inside an executable's HTTP client, so there is
/// no type to reflect over and no runtime to observe from this side. What IS available is the
/// pinned checkout — the same one this test project already compiles against — so the source is at
/// a known path, at a known tag, and reading it is a real check rather than a restatement of our
/// own beliefs.
///
/// EVERY ASSERTION HAS A VACUITY FLOOR. A guard that reads a file it cannot find, or a regex that
/// matches nothing, passes silently and is worse than no guard at all — it is a check that answers
/// "did I find any problems in nothing".
/// </summary>
[Collection(MicromoundCollection.Name)]
public class DeviceWireContractTests
{
    /// <summary>
    /// The pinned micromound checkout — the path MSBuild resolved, baked into this assembly by the
    /// `.csproj`.
    ///
    /// NOT derived from `typeof(SimMound).Assembly.Location`, which is the obvious move and is
    /// wrong: a project reference is COPIED into this project's output directory, so walking up
    /// from it lands inside the ANTHILL repo and finds no checkout at all. That mistake fails
    /// loudly here, which is the only reason it is worth writing down — the version of it that
    /// matters is a guard that walks up, finds nothing, and is written to shrug.
    /// </summary>
    private static string RepoRoot()
    {
        var recorded = typeof(DeviceWireContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "MicromoundRepoPath")?.Value;

        Assert.False(string.IsNullOrWhiteSpace(recorded),
            "this assembly does not record the micromound checkout it was built against. The "
          + "AssemblyMetadata item in Anthill.Tests.Micromound.csproj is what puts it there, and "
          + "without it this guard would silently check nothing.");

        Assert.True(Directory.Exists(Path.Combine(recorded!, "src", "Micromound.Host")),
            $"'{recorded}' does not look like a micromound checkout; the wire contract cannot be read");

        return recorded!;
    }

    private static string HostSource(string fileName)
    {
        var path = Path.Combine(RepoRoot(), "src", "Micromound.Host", fileName);

        Assert.True(File.Exists(path),
            $"{fileName} is not where this guard expects it. The device's HTTP client moved, which "
          + "is exactly the change that must not go unnoticed on this side of the link.");

        var text = File.ReadAllText(path);

        Assert.True(text.Length > 500, $"{fileName} read back nearly empty; the guard is vacuous");
        return text;
    }

    private static IReadOnlyList<string> JsonNamesIn(string source) =>
        [.. Regex.Matches(source, @"JsonPropertyName\(""(?<name>[a-z0-9_]+)""\)")
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// EVERY FIELD THE DEVICE SENDS AT ENROLMENT IS ONE THIS COLONY ACCEPTS, and the one field it
    /// READS BACK is one this colony returns.
    ///
    /// The accepted and returned sets are stated here as literals rather than reflected off
    /// `ApiHost`: the endpoint's request record is private to `Anthill.Api`, which this test project
    /// does not reference and should not — the module boundary is the point. So the pairing is
    /// pinned in both directions instead, and the endpoint carries the same names in a comment that
    /// names this test.
    /// </summary>
    [Fact]
    public void TheEnrolmentFieldsTheDeviceSends_AreTheOnesThisColonyAccepts()
    {
        var source = HostSource("HttpEnrollmentClient.cs");
        var names = JsonNamesIn(source);

        Assert.True(names.Count >= 5,
            "fewer JSON field names than the enrolment exchange has; the regex found "
          + $"{names.Count}: {string.Join(", ", names)}");

        // What `/micromound/v0/enroll` reads off the request. `mound_id`, `capabilities` and
        // `protocol_version` are accepted too and are deliberately NOT required — the device sends
        // none of them, and requiring them is precisely what made M1 unusable.
        var accepted = new[] { "token", "device_public_key", "hardware_profile", "tier" };

        // What it must put in the response. The mound persists this key and checks every downlink
        // envelope against it forever after.
        var returned = new[] { "controller_public_key" };

        var unhandled = names.Except(accepted, StringComparer.Ordinal)
                             .Except(returned, StringComparer.Ordinal)
                             .ToList();

        Assert.True(unhandled.Count == 0,
            "the device's enrolment client names fields this colony's endpoint does not handle: "
          + string.Join(", ", unhandled)
          + ". A field added on the device side is a contract change, and an endpoint that ignores "
          + "it fails in the field rather than here.");

        foreach (var required in accepted.Concat(returned))
            Assert.Contains(required, names, StringComparer.Ordinal);
    }

    /// <summary>
    /// THE SYNC EXCHANGE IS ONE ENVELOPE IN AND AN ARRAY OF ENVELOPES OUT — the shape M1 got wrong
    /// in both directions at once.
    ///
    /// Asserted against the client's own serialization calls, because that is the whole contract:
    /// there is no wrapper type on either side to compare against, which is exactly why an invented
    /// wrapper went unnoticed.
    /// </summary>
    [Fact]
    public void TheSyncExchange_IsOneRawEnvelopeInAndAnArrayOfEnvelopesOut()
    {
        var source = HostSource("HttpSyncTransport.cs");

        // The request body is the envelope itself, serialized with the shared protocol options —
        // not a field on an object, and not any other options.
        Assert.Matches(@"JsonSerializer\.Serialize\(\s*uplink\s*,\s*ProtocolJson\.Options\s*\)", source);

        // And the whole response body is a list of envelopes.
        Assert.Matches(@"JsonSerializer\.Deserialize<\s*List<\s*Envelope\s*>\s*>", source);

        // Positive control: the same file must NOT be readable as the wrapper M1 assumed, or the
        // two assertions above would pass on a client that also carried one.
        Assert.DoesNotMatch(@"JsonPropertyName\(""envelopes""\)", source);
        Assert.DoesNotMatch(@"JsonPropertyName\(""mound_id""\)", source);
    }

    /// <summary>
    /// THE PATHS THE DEVICE DIALS. Half a check, and said so plainly: the routes this colony serves
    /// are registered in `Anthill.Api`, which this test project deliberately does not reference, so
    /// this pins only the device's half of the pair. That is still worth having — a path change on
    /// the device side produces no error anywhere, just a controller that is never contacted — but
    /// it is not a proof that the two agree, and calling it one would be the shape this repository
    /// names as a check answering an adjacent question.
    /// </summary>
    [Fact]
    public void TheDevice_DialsThePathsThisColonyServes()
    {
        Assert.Contains("micromound/v0/sync", HostSource("HttpSyncTransport.cs"), StringComparison.Ordinal);
        Assert.Contains("micromound/v0/enroll", HostSource("HttpEnrollmentClient.cs"), StringComparison.Ordinal);
    }

}
