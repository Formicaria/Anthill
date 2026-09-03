using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Every route the API maps is either reachable from the console or deliberately not. v0.3.8.35.
///
/// `ConsoleRouteAgreementTests` (v0.3.8.34) checks one direction: the console must not call a route
/// that does not exist. That catches a broken button. It cannot catch the opposite and more common
/// failure — a backend capability the console never surfaces, which is invisible precisely because
/// nothing is broken.
///
/// The audit that produced this file found 176 mapped routes, 119 console call sites, and 25 routes
/// with no console reference at all. Among them was `/config/health`: `RuntimeConfigValidator` has
/// produced severity-tagged findings about setting combinations that cannot work since v2.x, and the
/// console had never asked for them. Same shape as `ollama_model_present` — computed, exposed, read
/// by nobody — and the same consequence: an operator with a broken configuration sees a healthy
/// dashboard.
///
/// This is a LEDGER, not a prohibition. Plenty of routes should not have a UI. The rule is that the
/// answer must be written down, because "no console reference" and "deliberately no console
/// reference" look identical from the outside and only one of them is a decision.
/// </summary>
public class ConsoleRouteCoverageTests
{
    private static string Root() => SourceText.RepoRoot();

    /// <summary>
    /// Every served console asset, concatenated.
    ///
    /// v0.3.8.52 replaced a hardcoded four-name list with an enumeration of the directory, because
    /// the list was wrong the moment app.js was split: ~1,600 lines of homelab moved to homelab.js
    /// and every route those lines call instantly looked unreferenced, failing this test for a
    /// change that touched no routes at all. A test whose input is a manually maintained file list
    /// reports on the list, not on the console — and the same shape of staleness had already let
    /// navigation.test.js sit unrun in CI.
    /// </summary>
    private static string ConsoleSource() =>
        string.Concat(Directory.GetFiles(Path.Combine(Root(), "src", "Anthill.UI"))
            .Where(f => f.EndsWith(".js", StringComparison.Ordinal) || f.EndsWith(".html", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(File.ReadAllText));

    /// <summary>
    /// Routes with no console surface, and why. Triaged individually at v0.3.8.35.
    ///
    /// "UI GAP" entries are real deficiencies, recorded rather than hidden, and owned by
    /// `docs/UI-ALIGNMENT-BRIEF.md`. Writing them here rather than leaving them undiscovered is the
    /// point: the list is short, honest and reviewable, and it shrinks as the console work lands.
    /// </summary>
    private static readonly Dictionary<string, string> NoConsoleSurface = new(StringComparer.Ordinal)
    {
        // --- programmatic / machine surfaces. Correctly absent from a human console. -------------
        ["/"] = "serves the console itself; it is the UI rather than a route the UI calls",
        ["/agent/run"] = "programmatic single-agent entry point for scripted callers, not an operator action",
        ["/schema"] = "plain-text schema dump for support and CI; the console shows what it means instead",
        ["/selftest"] = "CI and support diagnostic; a failing self-test surfaces through /config/health",
        ["/runtime/inventory"] = "the generated declaration/call-site audit CI gates on; not operator-facing",

        // --- superseded by a richer surface the console already uses -----------------------------
        ["/tasks"] = "plain-text task metrics; the console reads per-mission tasks from /missions/*",
        ["/messages"] = "superseded by /conversations, which the console does use",
        ["/communication"] = "superseded by /conversations",
        ["/sources"] = "per-mission sources arrive inside the mission payload the console already reads",
        ["/sources/*"] = "as /sources",
        ["/providers/capabilities"] = "the console reads /ollama/models and /providers for the same decision",
        ["/colony/workers/telemetry"] = "worker_telemetry is already embedded in /colony/registry",

        // --- homelab subsystem: its own console area, separately scoped ---------------------------
        ["/homelab/providers"] = "homelab virtualisation providers; surfaced through the homelab deck",
        ["/homelab/proxmox/status"] = "homelab; the deck reads the aggregate service view",
        ["/homelab/proxmox/test"] = "homelab connection test invoked from the virt settings block",
        ["/homelab/backups"] = "homelab backups; no console area yet",
        ["/homelab/backup/impact/*"] = "homelab backup impact; no console area yet",
        ["/homelab/graph/dependents/*"] = "homelab dependency graph; the deck renders the graph itself",

        // --- MICROMOUND M1: two of these are not console routes at all, and the rest await the
        // Integrations card that renders the three widget payloads (mound_fleet, mission_status,
        // evidence_feed) through the widget runtime. ------------------------------------------------
        ["/micromound/v0/enroll"] = "device endpoint: the one-time token is the auth; a mound is not an operator",
        ["/micromound/v0/sync"] = "device endpoint: the Ed25519 signature is the auth; mounds dial in, consoles do not",
        // "/micromound/mounds" LEFT this ledger at v0.3.8.115. Colony Live reads the fleet listing
        // once on enable to decide whether a Micromound descent is offered at all, so the route is
        // reached and the entry would now be excusing nothing (`TheLedger_ContainsNothingTheConsole
        // ActuallyReaches`). What that read does NOT surface is the POST on the same path — minting
        // an enrollment token — which normalises to the same key and so cannot be recorded here
        // separately. It is tracked in docs/UI-ALIGNMENT-BRIEF.md with the rest of the M1 UI.
        ["/micromound/evidence"] = "UI GAP — the evidence_feed widget payload is the intended reader (M1 UI)",
        ["/micromound/stop"] = "UI GAP — per-mound stop control belongs on the fleet card; the global stop is a file by design",
        ["/micromound/stop/resume"] = "UI GAP — as /micromound/stop; resume is an explicit operator act",

        // --- MICROMOUND v0.3.8.114: the command path shipped and its console did NOT. -------------
        //
        // This is the deferral recorded where it can be checked rather than only asserted in prose.
        // The integration brief's acceptance experience — an operator adding a mound, authoring its
        // configuration, issuing a charter and watching evidence arrive, all from the console — is
        // NOT delivered by `.114`, by an explicit scope decision. Every route below is reachable
        // over the API and nothing renders it.
        //
        // These entries are meant to LEAVE this ledger, the way the v0.3.8.46 block above did.
        ["/micromound/charters"] = "UI GAP — charter issuance has no console surface; the command path shipped ahead of its UI at .114",
        ["/micromound/config"] = "UI GAP — manifest authoring (hardware bindings, device limits, workers) awaits its console form",
        ["/micromound/missions"] = "UI GAP — physical mission dispatch; approvals DO surface, through the existing approvals queue",
        ["/micromound/missions/*"] = "UI GAP — per-mission evidence summary; the mission_status widget payload is the intended reader",
        ["/micromound/resolve"] = "UI GAP — the capability resolver answers a question and issues nothing; no console asks it yet",
        ["/micromound/unlink"] = "UI GAP — retiring a device belongs on the fleet card, beside the token mint",

        // The v0.3.8.48 schedule entries left this ledger the same release they joined it: the
        // project workspace's Schedules tab reaches all of them.

        // --- The UI GAPS section emptied at v0.3.8.46. Every entry left by gaining a surface: ----
        // "/readiness/json", "/readiness/certification" and "/readiness/qualification-report" —
        //   the Readiness page (Administration → Readiness): snapshot with attestation, the
        //   certification download, the report action.
        // "/colony/introspection" — rendered on the same Readiness page.
        // "/source-quality" — shown on Memory & Signals, beside the learning signals it belongs with.
        // "/shadow/judge" — the pending queue renders in the shadow panel with the judgment form
        //   attached; a recorded judgment feeds the scoreboard.
        // "/missions/plan" — the dry-run preview renders inside chat's escalation gate, at the
        //   moment of the yes/no it informs.
    };

    /// <summary>Every route literal the API maps, normalised so `{id}` segments compare.</summary>
    private static HashSet<string> MappedRoutes()
    {
        var routes = new HashSet<string>(StringComparer.Ordinal);
        var apiDir = Path.Combine(Root(), "src", "Anthill.Api");

        foreach (var file in Directory.GetFiles(apiDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            foreach (var line in File.ReadLines(file))
            {
                if (!Regex.IsMatch(line, @"\b(Map[A-Za-z]+|Protected[A-Za-z]*)\s*\(")) continue;
                foreach (Match m in Regex.Matches(line, "\"(/[^\"]*)\""))
                    routes.Add(Normalise(m.Groups[1].Value));
            }
        }
        return routes;
    }

    private static string Normalise(string route) =>
        Regex.Replace(route, @"\{[^}]+\}", "*").TrimEnd('/') is { Length: > 0 } r ? r : "/";

    /// <summary>
    /// Does the console reach this route? Prefix-based, because the console composes paths
    /// (`api('/missions/' + id)`) — so a reference to the stem counts as covering the family.
    ///
    /// v0.3.8.40 — the stem must BEGIN a quoted path literal, not merely appear somewhere.
    ///
    /// A bare Contains matched a route inside any longer path ending with it. When `/agents` was
    /// added the nav table already held the route string `/colony/agents`, so the new endpoint
    /// counted as reached before a line of UI existed — verified at the time: two new routes, zero
    /// console code, and this audit passed them both. The guard was answering a question about
    /// substrings while appearing to answer one about coverage, which is worse than not asking,
    /// because a whole backend surface can arrive with no operator access and nothing says so.
    ///
    /// An opening quote is the cheapest rule that means what the test claims. Every console
    /// reference is a string literal — api('/x'), route:'/x', fetch(`/x`) — so a real caller always
    /// has one and a coincidental suffix inside a longer path never does.
    ///
    /// Verdicts were compared across all 178 routes before and after this change: none differed. The
    /// rule is strictly tighter and cost nothing, which is what made it safe to tighten in one go
    /// rather than behind a migration.
    /// </summary>
    private static bool ReachedByConsole(string route, string console)
    {
        var stem = route.Split('*')[0].TrimEnd('/');
        if (stem.Length <= 1) return false;

        return console.Contains("'" + stem, StringComparison.Ordinal)
            || console.Contains("\"" + stem, StringComparison.Ordinal)
            || console.Contains("`" + stem, StringComparison.Ordinal);
    }

    /// <summary>The ledger. A route with no surface and no recorded reason is an oversight.</summary>
    [Fact]
    public void EveryMappedRoute_IsReachableFromTheConsoleOrRecordedAsNotBeing()
    {
        var console = ConsoleSource();

        var undecided = MappedRoutes()
            .Where(r => !NoConsoleSurface.ContainsKey(r))
            .Where(r => !ReachedByConsole(r, console))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        Assert.True(undecided.Count == 0,
            "These routes exist and nothing in the console reaches them, and no reason is recorded. "
            + "That is how /config/health came to compute configuration findings for several releases "
            + "with no reader. Either surface them, or add them to NoConsoleSurface with the reason "
            + "(\"UI GAP — ...\" is a perfectly good reason): " + string.Join(", ", undecided));
    }

    /// <summary>
    /// The ledger may not name routes that no longer exist. A stale entry silently excuses a route
    /// that was renamed, and the rename is exactly when a console reference goes stale.
    /// </summary>
    [Fact]
    public void TheLedger_NamesOnlyRoutesThatStillExist()
    {
        var mapped = MappedRoutes();
        var stale = NoConsoleSurface.Keys
            .Where(r => !mapped.Contains(r))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "These routes are recorded as having no console surface, but the API no longer maps them: "
            + string.Join(", ", stale));
    }

    /// <summary>
    /// An entry must be NECESSARY — the same rule `StatusFieldConsumerTests` learned the hard way
    /// after exempting a field with the reason "read by the console".
    /// </summary>
    [Fact]
    public void TheLedger_ContainsNothingTheConsoleActuallyReaches()
    {
        var console = ConsoleSource();

        var unnecessary = NoConsoleSurface.Keys
            .Where(r => ReachedByConsole(r, console))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        Assert.True(unnecessary.Count == 0,
            "These routes are recorded as having no console surface, but the console reaches them. "
            + "Remove the entry: " + string.Join(", ", unnecessary));
    }

    /// <summary>
    /// Configuration findings reach the operator. The specific gap this audit found, pinned by name
    /// — a validator whose output nobody reads cannot report a broken installation.
    /// </summary>
    [Fact]
    public void ConfigurationFindings_ReachTheOperator()
    {
        var console = ConsoleSource();

        Assert.Contains("/config/health", console, StringComparison.Ordinal);
        Assert.Contains("cfgFindings", console, StringComparison.Ordinal);   // consumed, not merely fetched
        Assert.Contains("severity", console, StringComparison.Ordinal);      // and severity respected
    }

    /// <summary>
    /// v0.3.8.46: the last recorded UI gap gained a surface, and the guard that kept the gaps
    /// visible retired with them — exactly as its own failure message instructed. What remains is
    /// its inverse: the console must now actually REACH every route the gaps named, so deleting a
    /// surface reopens a gap loudly instead of silently.
    /// </summary>
    [Fact]
    public void TheFormerGaps_AreActuallySurfaced()
    {
        var console = ConsoleSource();

        foreach (var route in new[] { "/readiness/json", "/readiness/certification",
            "/readiness/qualification-report", "/colony/introspection", "/source-quality",
            "/shadow/judge", "/missions/plan" })
        {
            Assert.True(ReachedByConsole(route, console),
                $"{route} was a recorded UI gap, closed at v0.3.8.46 — and the console no longer "
                + "reaches it. Its surface has been removed; restore it or put the gap back on the "
                + "ledger honestly.");
        }
    }
}
