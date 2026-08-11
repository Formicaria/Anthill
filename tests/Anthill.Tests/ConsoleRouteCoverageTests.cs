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

    private static string ConsoleSource() =>
        string.Concat(new[] { "app.js", "index.html", "dashboard-grid.js", "mission-thread.js" }
            .Select(f => File.ReadAllText(Path.Combine(Root(), "src", "Anthill.UI", f))));

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
        ["/readiness/attest"] = "POST paired with the readiness report; no reader until that report has a UI",

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

        // --- UI GAPS. Real, recorded, owned by the alignment brief. --------------------------------
        ["/readiness/json"] = "UI GAP — the qualification snapshot has no console view. AUTONOMY-10 "
                            + "makes qualification the exit gate for every phase and an operator "
                            + "currently cannot see it. Deliverable A/B in UI-ALIGNMENT-BRIEF.md",
        ["/readiness/certification"] = "UI GAP — as /readiness/json",
        ["/readiness/qualification-report"] = "UI GAP — as /readiness/json",
        ["/colony/introspection"] = "UI GAP — the colony's own account of its wiring; Layer-3 "
                                  + "diagnostic with no console home yet",
        ["/source-quality"] = "UI GAP — research source-quality trails are recorded and never shown; "
                            + "relates to the pheromone surface, which also has no operator view",
        ["/shadow/judge"] = "UI GAP — shadow-mode judgments are recorded and not surfaced",
        ["/missions/plan"] = "UI GAP — the dry-run plan preview lost its only surface when the "
                           + "dashboard composer retired (v0.3.8.42 §3: chat is the one mission "
                           + "entry). The capability was not removed; Chat should grow a preview step",
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
    /// The UI gaps stay VISIBLE. If someone deletes the entries instead of fixing the gaps, the
    /// ledger silently becomes a list of excuses — so their presence is asserted until the console
    /// work lands and they are removed together with the gap.
    /// </summary>
    [Fact]
    public void TheRecordedUiGaps_AreStillDeclaredAsGaps()
    {
        var gaps = NoConsoleSurface.Where(kv => kv.Value.StartsWith("UI GAP", StringComparison.Ordinal))
            .Select(kv => kv.Key).ToList();

        Assert.True(gaps.Count > 0,
            "No route is recorded as a UI gap. If the console genuinely now surfaces readiness, "
            + "colony introspection, source quality and shadow judgments, delete this test with the "
            + "entries. If it does not, the gaps must stay recorded.");
    }
}
