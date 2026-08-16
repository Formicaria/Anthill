using Anthill.Core.Agents;
using Anthill.SDK.Common;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A scanner reads VALUES, and decoding is the caller's job. v0.3.8.72 — the class, after v0.3.8.71
/// closed the instance, and after the obvious fix turned out to be the wrong one.
///
/// WHAT v0.3.8.71 FOUND. `PolicyScan.secret_material` — critical, blocking, the rule the soldier's
/// whole blocking authority rests on — could not fire on a quoted credential in a proposed patch,
/// because the soldier scanned the patch artifact's JSON SERIALIZATION rather than its values. It
/// was fixed at the feed: `SoldierAnt.DecodeForScanning`.
///
/// THE WRONG FIX, RECORDED BECAUSE IT WAS NEARLY SHIPPED. The follow-up sweep's first move was to
/// widen the rule to tolerate `\"` — an escaped quote — on the reasoning that the feed is not the
/// only way encoded text arrives. That allowance would have done nothing. `Json.Dumps` leaves
/// `JsonSerializerOptions.Encoder` at `JavaScriptEncoder.Default`, which does not emit `\"`: it
/// emits a `"` unicode escape, and treats `&lt;`, `&gt;`, `&amp;`, `'` and `+` the same way. A
/// pattern taught to expect one escaping would have been just as blind, while looking fixed — and a
/// guard written by hand-typing the "escaped form" would have agreed with it, because both would
/// have been guessing at the same wrong thing.
///
/// SO NOTHING HERE HAND-WRITES AN ENCODING. Every encoded sample comes out of `Json.Dumps` — the
/// same call `RecordPatchArtifact` makes — and every decode goes through `DecodeForScanning`. If
/// .NET changes its default encoder tomorrow these tests still describe the truth, because they
/// never claimed to know what the escaping looks like. That is the property that was missing: not
/// "the rule handles escapes", but "the rule is never asked to".
///
/// The conclusion is a layering rule, and it is the one this repository already applies to
/// containment and to test collections: ONE place answers each question. Patterns match source.
/// Callers hand them source.
/// </summary>
public class SecretPatternEncodingTests
{
    private const string Secret = "sk-live-9f3a2b7c4d1e";

    /// <summary>The patch artifact as `RecordPatchArtifact` actually writes it — same serializer,
    /// same shape, so the encoding under test is the real one rather than a transcription of it.</summary>
    private static string PatchPayload() => Json.Dumps(new
    {
        patch_set_id = "ps-1",
        summary = "Add the deployment runbook.",
        proposals = new[]
        {
            new
            {
                FilePath = "docs/RUNBOOK.md",
                change_type = "add",
                new_content = "# Deployment runbook\n\napi_key = \"" + Secret + "\"\n",
            },
        },
    }, indented: true);

    // -----------------------------------------------------------------------------------------------
    // The defect, and the fix, both against the real serializer
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// THE DEFECT. Scanning the serialization finds nothing — and this is asserted rather than
    /// assumed, because it is the entire reason the decoder exists. If a future serializer change
    /// made this pass, the decoder would still be right and this test would say so by failing here
    /// with a message that explains it.
    /// </summary>
    [Fact]
    public void ScanningTheSerialization_FindsNoSecret()
    {
        var payload = PatchPayload();

        Assert.Contains(Secret, payload);   // the secret IS in there, verbatim
        Assert.DoesNotContain(PolicyScan.Scan(payload), f => f.RuleId == "secret_material");
    }

    /// <summary>
    /// THE FIX. Decoded first, the same rule finds the same secret and blocks. This is the pairing
    /// that matters: the two assertions differ only by the decode.
    /// </summary>
    [Fact]
    public void ScanningTheDecodedValues_FindsTheSecret_AndBlocks()
    {
        var finding = PolicyScan.Scan(SoldierAnt.DecodeForScanning(PatchPayload()))
            .FirstOrDefault(f => f.RuleId == "secret_material");

        Assert.True(finding is not null,
            "a quoted credential in a proposed patch produced no secret_material finding after "
          + "decoding. This is the rule the soldier's blocking authority rests on, and its miss is "
          + "silent — the review reports '0 blocking findings', never 'I could not read the "
          + "content', so an unreadable scan is indistinguishable from a clean one.");
        Assert.True(finding!.Blocking);
    }

    /// <summary>
    /// The quote really is encoded to something a pattern would not recognise, demonstrated rather
    /// than named. The assertion deliberately does NOT say `"` or `\"` — it says the raw text
    /// is not present in the payload while the secret is, which is what makes an encoding-aware
    /// pattern a losing bet whichever escaping .NET picks.
    /// </summary>
    [Fact]
    public void TheQuoteDoesNotSurviveSerializationAsItself()
    {
        var payload = PatchPayload();

        Assert.DoesNotContain("api_key = \"" + Secret, payload);
        Assert.Contains("api_key = ", payload);
        Assert.Contains(Secret, payload);
    }

    /// <summary>Paths survive the decode too, so the fix did not trade content for paths — the
    /// blocked-path rules read the same material.</summary>
    [Fact]
    public void DecodingKeepsThePaths()
    {
        var decoded = SoldierAnt.DecodeForScanning(PatchPayload());

        Assert.Contains("docs/RUNBOOK.md", decoded);
        Assert.Contains("patch_set_id", decoded);   // keys are kept: a rule may match a field name
    }

    /// <summary>
    /// A payload that will not parse is scanned RAW rather than dropped. A malformed patch artifact
    /// is when a review should get more suspicious, not quietly stop reading.
    /// </summary>
    [Fact]
    public void AMalformedPayload_IsStillScanned()
    {
        const string broken = "{ \"proposals\": [ { \"new_content\": \"rm -rf /\" ";

        Assert.Equal(broken, SoldierAnt.DecodeForScanning(broken));
        Assert.Contains(PolicyScan.Scan(SoldierAnt.DecodeForScanning(broken)),
            f => f.RuleId == "destructive_operation");
    }

    // -----------------------------------------------------------------------------------------------
    // The layering rule, enforced
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// EVERY ARTIFACT PAYLOAD HANDED TO A SCANNER GOES THROUGH THE DECODER. This is the check that
    /// catches the next site rather than the two this release looked at.
    ///
    /// The sweep behind it: `PolicyScan.Scan` has two callers. `SoldierAnt` reads artifact payloads —
    /// that is the defect, now decoded. `SecurityPolicyVerifier` reads `r.ChangedPath` and
    /// `r.NewContent`, which are raw strings and never serialized, which is exactly why the same rule
    /// works there and why the defect stayed invisible for two releases: one caller proved the rule
    /// fine while the other could not use it.
    ///
    /// WHAT THIS DOES NOT CLAIM. It checks that `.Payload` and `PolicyScan` do not meet without
    /// `DecodeForScanning` between them, in the file where they meet. It cannot see a payload that
    /// arrives through three helpers, and it says nothing about scanners outside `PolicyScan`. The
    /// honest scope is the adjacency that broke.
    /// </summary>
    [Fact]
    public void NoScannerIsHandedARawArtifactPayload()
    {
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(SourceText.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
             || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            // COMMENTS BLANKED FIRST — the fixed site explains the defect using both names, and a
            // guard that matches its own explanation is the trap this repository keeps re-finding.
            var code = SourceText.CodeOnly(File.ReadAllText(path));

            var scans = code.Contains("PolicyScan.Scan", StringComparison.Ordinal);
            var readsPayloads = code.Contains(".Payload", StringComparison.Ordinal);
            var decodes = code.Contains("DecodeForScanning", StringComparison.Ordinal);

            if (scans && readsPayloads && !decodes) offenders.Add(Path.GetFileName(path));
        }

        Assert.True(offenders.Count == 0,
            "these files read artifact payloads and run the policy scanner without decoding first: "
          + string.Join(", ", offenders)
          + ". An artifact payload is JSON; PolicyScan's rules are written against source. Widening "
          + "the rules is not the answer — the serializer's escaping is not `\\\"` and the next "
          + "encoding will differ again. Decode at the feed, with SoldierAnt.DecodeForScanning.");
    }
}
