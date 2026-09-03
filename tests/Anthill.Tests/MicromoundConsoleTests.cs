using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE MICROMOUND CONSOLE SAYS WHAT THE COLONY KNOWS, AND NOTHING ELSE. v0.3.8.115.
///
/// `src/Anthill.UI/micromound.js` closed the seven UI GAPs `.114` recorded. These are the rules that
/// closure depends on, and every one of them is a rule this subsystem could plausibly break by being
/// helpful — a status the browser guessed, a token cached "for convenience", a charter reported as
/// delivered because the API returned 200.
///
/// The vocabulary agreement — that the console's dropdowns are the PROTOCOL's closed sets — is
/// checked one tier higher, in `Anthill.Tests.Micromound.ConsoleVocabularyTests`, because that is
/// where the protocol assembly exists and a typed registry beats a source scan (docs/GUARDS.md).
/// What is left here is what can only be seen in the console's own source.
/// </summary>
public class MicromoundConsoleTests
{
    private static string Path_ => System.IO.Path.Combine(
        SourceText.RepoRoot(), "src", "Anthill.UI", "micromound.js");

    private static string Raw() => File.ReadAllText(Path_);

    /// <summary>Comments and strings blanked. Same JavaScript-aware stripper as the Colony Live guards.</summary>
    private static string Code()
    {
        var src = Raw().Replace("\r\n", "\n");
        var sb = new System.Text.StringBuilder(src.Length);
        bool line = false, block = false;
        char quote = '\0';

        for (var i = 0; i < src.Length; i++)
        {
            var c = src[i];
            var next = i + 1 < src.Length ? src[i + 1] : '\0';

            if (line) { if (c == '\n') { line = false; sb.Append(c); } else sb.Append(' '); continue; }
            if (block) { if (c == '*' && next == '/') { block = false; sb.Append("  "); i++; } else sb.Append(c == '\n' ? '\n' : ' '); continue; }
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == '\\' && next != '\0') { sb.Append(next); i++; }
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c == '/' && next == '/') { line = true; sb.Append("  "); i++; continue; }
            if (c == '/' && next == '*') { block = true; sb.Append("  "); i++; continue; }
            if (c is '"' or '\'' or '`') { quote = c; sb.Append(c); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    [Fact]
    public void TheGuardsInThisFile_AreReadingTheRealConsole()
    {
        Assert.True(File.Exists(Path_), "src/Anthill.UI/micromound.js is missing.");
        Assert.True(Code().Length > 3_000,
            $"micromound.js is {Code().Length} characters of code. Every assertion below would pass "
          + "vacuously on an input this small.");

        // And the stripper works. The phrase must exist ONLY in a comment — an earlier draft of this
        // floor picked one that also appears inside a template literal, where a string-preserving
        // stripper correctly keeps it, and the guard failed for being wrong about its own input.
        Assert.Contains("both ends of it ours", Raw());
        Assert.DoesNotContain("both ends of it ours", Code());
    }

    /// <summary>
    /// THE STATUS VERDICT IS THE COLONY'S. `MicromoundWidgets.StatusOf` reads the sync interval and
    /// the configured missed-beat grace; a browser has neither. `.115` made that method public and
    /// the fleet listing carries its answer, so the console renders it. A console that recomputed it
    /// would agree today and disagree the first time the grace is reconfigured — and disagree
    /// silently, which is the whole problem.
    /// </summary>
    [Fact]
    public void TheConsole_RendersTheColonysStatusRatherThanComputingOne()
    {
        var code = Code();

        Assert.Contains("mmFleet.status", code);

        foreach (var verdict in new[] { "'online'", "'offline'", "'quiesced'", "'unenrolled'" })
            Assert.False(code.Contains(verdict, StringComparison.Ordinal),
                $"micromound.js decides a mound status ({verdict}). That verdict belongs to "
              + "MicromoundWidgets.StatusOf, which reads configuration the browser cannot see.");

        // The server half of the same rule: the listing really does carry it.
        var api = SourceText.CodeOnly(File.ReadAllText(System.IO.Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Api", "Micromound", "ApiHost.Micromound.cs")));
        Assert.Contains("MicromoundWidgets.StatusOf", api);
    }

    /// <summary>
    /// A MINTED TOKEN IS SHOWN ONCE AND STORED NOWHERE. The colony keeps a hash; there is no
    /// re-issue and no self-service re-key. A console that cached the plaintext "so the operator can
    /// come back to it" would put an enrollment secret in browser storage on every machine that ever
    /// opened the page, and would do it silently.
    /// </summary>
    [Fact]
    public void AMintedToken_IsNeverPersisted()
    {
        var code = Code();

        foreach (var store in new[] { "localStorage", "sessionStorage", "indexedDB", "document.cookie" })
            Assert.False(code.Contains(store, StringComparison.Ordinal),
                $"micromound.js writes to {store}. The enrollment token passes through this file and "
              + "must not outlive the page that showed it.");

        // And the operator is told, at the moment they can still act on it.
        Assert.Contains("Shown ONCE", Raw());
    }

    /// <summary>
    /// NOTHING IS REPORTED AS DELIVERED. PROTOCOL.md §1: the colony never dials a mound — a device
    /// behind NAT dials in. So a charter, manifest or mission that the API accepted is QUEUED, and
    /// the difference matters most for the case an operator most needs to understand: a device that
    /// is offline and has collected nothing.
    /// </summary>
    [Fact]
    public void IssuedWorkIsReportedAsAwaitingCollection_NeverAsDelivered()
    {
        var raw = Raw();

        Assert.Contains("awaiting collection", raw);

        foreach (var claim in new[] { "delivered to the mound", "sent to the device", "now in force" })
            Assert.False(raw.Contains(claim, StringComparison.OrdinalIgnoreCase),
                $"micromound.js says work was '{claim}'. Everything issued lands in a downlink queue "
              + "and is collected on the device's next beat; the colony cannot know it arrived until "
              + "the ack does.");

        // The manifest case says the stronger version out loud: accepted is not in force.
        Assert.Contains("NOT in force", raw);
    }

    /// <summary>
    /// THE GLOBAL STOP IS NOT A BUTTON, AND THE CONSOLE SAYS WHY. SAFETY.md keeps three genuinely
    /// different stop routes; the colony-wide one is a file on disk precisely so no API flow can
    /// clear it. A console control that appeared to clear it — even one that failed — would teach an
    /// operator that the global stop is something software can talk its way out of.
    /// </summary>
    [Fact]
    public void TheGlobalStop_IsShownAsAFileAndNotOfferedAsAControl()
    {
        var code = Code();
        var raw = Raw();

        Assert.Contains("mmFleet.global_stop", code);
        Assert.Contains("out of this API's reach", raw);

        // Per-mound stop and resume ARE offered; the global one has no endpoint to call.
        Assert.Contains("'/micromound/stop'", code);
        Assert.Contains("'/micromound/stop/resume'", code);
    }

    /// <summary>
    /// EVERY MUTATION GOES THROUGH THE CONSOLE'S AUTHENTICATED HELPER. `api()` carries the bearer
    /// token and the console's error contract; a bare `fetch` here would be a second request path
    /// with its own auth story, and the one it would most likely get wrong is the one that mints a
    /// credential.
    /// </summary>
    [Fact]
    public void EveryRequest_GoesThroughTheConsolesApiHelper()
    {
        var code = Code();

        foreach (var raw in new[] { "fetch(", "XMLHttpRequest", "navigator.sendBeacon" })
            Assert.False(code.Contains(raw, StringComparison.Ordinal),
                $"micromound.js performs I/O with {raw} instead of the console's api() helper.");

        Assert.Contains("api('/micromound/mounds')", code);
        Assert.Contains("mmPost(", code);
    }

    /// <summary>
    /// AND IT IS REACHABLE. The static `.nav-item` divs in index.html are legacy DOM — `buildNav`
    /// renders `#nav-scroll` from the `IA` table — so a page that is not in `IA` has a route, a
    /// container and a PAGE_ENTER handler and no way for an operator to arrive at it.
    ///
    /// This is the "declared and reaching nobody" defect class aimed at navigation, and it is worth
    /// its own fact because the page LOOKS wired from every other angle.
    /// </summary>
    [Fact]
    public void TheMicromoundPage_IsInTheNavigationTableAndHasSomewhereToRender()
    {
        var app = File.ReadAllText(System.IO.Path.Combine(SourceText.RepoRoot(), "src", "Anthill.UI", "app.js"));
        var html = File.ReadAllText(System.IO.Path.Combine(SourceText.RepoRoot(), "src", "Anthill.UI", "index.html"));

        Assert.Matches(new Regex(@"page\s*:\s*'micromound'"), app);
        Assert.Contains("id=\"page-micromound\"", html);
        Assert.Contains("PAGE_ENTER['micromound']", Raw());

        // The vacuity floor: this scan can see a page that IS wired, so a miss above means absence.
        Assert.Matches(new Regex(@"page\s*:\s*'pheromones'"), app);
    }
}
