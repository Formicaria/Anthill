using System.Text.RegularExpressions;
using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v1.8.28 validation/regression harness (NORTH_STAR Phase 2). Repo-level guards for bug classes
/// that have already shipped once: version-marker drift, non-idempotent schema migration, UI glyph
/// corruption, and stray active Python. These run in plain `dotnet test`, so they gate local work
/// and CI identically. See docs/NORTH_STAR.md §4.
/// </summary>
public class RegressionGuardTests : IDisposable
{
    private readonly string _tmpDir;

    public RegressionGuardTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "anthill_guard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose() { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    /// <summary>Walk up from the test bin directory to the repo root (marked by Anthill.sln).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate repo root (Anthill.sln) above the test bin directory.");
        return dir!.FullName;
    }

    // ---- Version marker consistency ----------------------------------------------------------
    // The repo shipped with Directory.Build.props stuck at 1.8.15.6 while the runtime said 1.8.27.
    // Every version marker must agree: runtime const, assembly version, README, and CHANGELOG.

    [Fact]
    public void VersionMarkers_RuntimeMatchesDirectoryBuildProps()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var m = Regex.Match(props, @"<AnthillVersion>([^<]+)</AnthillVersion>");
        Assert.True(m.Success, "Directory.Build.props has no <AnthillVersion> marker.");
        Assert.Equal(AnthillRuntime.Version, m.Groups[1].Value.Trim());
    }

    [Fact]
    public void VersionMarkers_ReadmeAdvertisesRuntimeVersion()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        var m = Regex.Match(readme, @"\*\*Current version:\*\*\s*v([0-9][0-9A-Za-z.\-]*)");
        Assert.True(m.Success, "README.md has no '**Current version:** vX.Y.Z' marker.");
        Assert.Equal(AnthillRuntime.Version, m.Groups[1].Value.Trim());
    }

    [Fact]
    public void VersionMarkers_ChangelogHasEntryForRuntimeVersion()
    {
        var changelog = File.ReadAllText(Path.Combine(RepoRoot(), "CHANGELOG.md"));
        var pattern = @"^##\s+v" + Regex.Escape(AnthillRuntime.Version) + @"\b";
        Assert.True(Regex.IsMatch(changelog, pattern, RegexOptions.Multiline),
            $"CHANGELOG.md has no '## v{AnthillRuntime.Version}' entry for the current runtime version.");
    }

    // ---- Migration idempotence -----------------------------------------------------------------
    // Schema creation must be safe on a fresh DB, on an existing DB, and when re-run repeatedly.

    [Fact]
    public void Migration_FreshDb_CreatesSchemaAndReportsTables()
    {
        var dbPath = Path.Combine(_tmpDir, "fresh.db");
        using var mem = new SqliteMemory(dbPath);
        var counts = mem.TableCounts();
        Assert.NotEmpty(counts);
    }

    [Fact]
    public void Migration_ExistingDb_ReopenIsIdempotent()
    {
        var dbPath = Path.Combine(_tmpDir, "reopen.db");
        Dictionary<string, object?> first;
        using (var mem = new SqliteMemory(dbPath))
            first = mem.TableCounts();
        // Re-opening runs InitDb again over the existing schema — must not throw or change shape.
        using (var mem = new SqliteMemory(dbPath))
        {
            var second = mem.TableCounts();
            Assert.Equal(first.Keys.OrderBy(k => k), second.Keys.OrderBy(k => k));
        }
    }

    [Fact]
    public void Migration_RepeatedReruns_AllPass()
    {
        var dbPath = Path.Combine(_tmpDir, "rerun.db");
        for (var i = 0; i < 3; i++)
        {
            using var mem = new SqliteMemory(dbPath);
            Assert.NotEmpty(mem.TableCounts());
        }
    }

    // ---- UI glyph/encoding integrity -------------------------------------------------------------
    // An editor in the pipeline has repeatedly re-saved the embedded UI as non-UTF-8, flattening
    // icon glyphs to '?' / U+FFFD. Mirror of the CI ui-integrity job so `dotnet test` catches it too.

    /// <summary>
    /// v2.6.3: the console script moved out of index.html into app.js. UI guards that scan the
    /// console *source* (glyphs, id/lookup integrity, adapter accessors) must read BOTH files, or
    /// they silently go blind to everything that now lives in app.js.
    /// </summary>
    private static string UiSource()
    {
        // v2.15.2: dashboard-workspace.js joins the scan. It was excluded simply because it did not
        // exist when this helper was written, which left an entire UI file — by now the one that
        // builds most of the console's chrome — outside every guard that uses UiSource.
        var dir = Path.Combine(RepoRoot(), "src", "Anthill.Api", "Ui");
        var parts = new List<string> { File.ReadAllText(Path.Combine(dir, "index.html")) };
        foreach (var js in new[] { "app.js", "dashboard-workspace.js" })
        {
            var path = Path.Combine(dir, js);
            if (File.Exists(path)) parts.Add(File.ReadAllText(path));
        }
        return string.Join("\n", parts);
    }

    [Fact]
    public void UiIntegrity_NoFlattenedGlyphsOrReplacementChars()
    {
        var ui = UiSource();
        var problems = new List<string>();

        var fffd = ui.Count(c => c == '�');
        if (fffd > 0) problems.Add($"{fffd} U+FFFD replacement char(s)");

        var bare = Regex.Matches(ui, @"(?<!<kbd)>\?<").Count; // bare >?< icons, excluding <kbd>?</kbd>
        if (bare > 0) problems.Add($"{bare} bare '>?<' icon glyph(s) flattened to '?'");

        var labeled = Regex.Matches(ui, @">\s*\?\s+[A-Z][a-z]").Count; // '>? Label' buttons
        if (labeled > 0) problems.Add($"{labeled} '>? Label' button glyph(s)");

        // v2.6.3: two rot classes the checks above missed and that shipped to production —
        // trailing action arrows ('Events ?</button>') and leading icons on dynamic labels
        // ('...">? ${count} change(s)'). <kbd>?</kbd> is '>?<' and is not matched by either.
        var trailing = Regex.Matches(ui, @"[A-Za-z0-9\)] \?<").Count; // 'Label ?</tag>' trailing glyphs
        if (trailing > 0) problems.Add($"{trailing} 'Label ?<' trailing icon glyph(s) flattened to '?'");

        var leading = Regex.Matches(ui, @">\? ").Count; // '>? {content}' leading icon glyphs
        if (leading > 0) problems.Add($"{leading} '>? ' leading icon glyph(s) flattened to '?'");

        var ternary = Regex.Matches(ui, Regex.Escape("'?':'?'")).Count; // caret ternaries
        if (ternary > 0) problems.Add($"{ternary} \"'?':'?'\" caret ternary(ies)");

        Assert.True(problems.Count == 0,
            "UI encoding corruption in src/Anthill.Api/Ui/index.html: " + string.Join("; ", problems));
    }

    /// <summary>
    /// v1.9.1.1: the UI title/header versions were hardcoded markup and silently drifted
    /// (stuck at v1.8.29.1 while the runtime said v1.9.1). The UI must render the version it
    /// fetches from /health — never a literal version baked into the HTML.
    /// </summary>
    [Fact]
    public void UiIntegrity_NoHardcodedVersionInMarkup()
    {
        var ui = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "Ui", "index.html"));
        var hardcoded = Regex.Matches(ui, @">\s*v\d+\.\d+[\d.]*\s*<");
        Assert.True(hardcoded.Count == 0,
            "Hardcoded version string(s) in UI markup (must come from /health at runtime): "
            + string.Join("; ", hardcoded.Select(m => m.Value.Trim())));
        Assert.DoesNotContain("<title>ANTHILL v", ui);
    }

    /// <summary>
    /// v2.2.6: release mishaps have twice tagged a version whose CHANGELOG top entry was older
    /// (rebase ordering). The FIRST '## vX.Y.Z' heading must be the current runtime version.
    /// </summary>
    [Fact]
    public void VersionMarkers_ChangelogTopEntryIsRuntimeVersion()
    {
        var changelog = File.ReadAllText(Path.Combine(RepoRoot(), "CHANGELOG.md"));
        var m = Regex.Match(changelog, @"^##\s+v([0-9][0-9A-Za-z.\-]*)", RegexOptions.Multiline);
        Assert.True(m.Success, "CHANGELOG.md has no '## vX.Y.Z' entries.");
        Assert.Equal(AnthillRuntime.Version, m.Groups[1].Value.Trim());
    }

    /// <summary>
    /// v2.2.6: the retired System Core panel was deleted while legacy code still looked its
    /// elements up (silent dead work). Every getElementById target must exist as a static id in
    /// the markup, ids created at runtime are allow-listed, and no id may be declared twice.
    /// </summary>
    /// <summary>
    /// Strip JS string literals, comments, and regex literals so identifier scans cannot be fooled
    /// by prose in a template string, a URL's "//", or an apostrophe inside "Queen's Core".
    /// </summary>
    private static string StripJsLiteralsAndComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        var prev = '\0';   // last non-whitespace emitted char, for regex-literal detection
        int i = 0, n = src.Length;
        while (i < n)
        {
            var ch = src[i];
            if (ch == '"' || ch == '\'' || ch == '`')
            {
                var quote = ch;
                i++;
                while (i < n)
                {
                    if (src[i] == '\\') { i += 2; continue; }
                    if (src[i] == quote) { i++; break; }
                    i++;
                }
                sb.Append("\"\""); prev = '"'; continue;
            }
            if (ch == '/' && i + 1 < n && src[i + 1] == '/')
            {
                while (i < n && src[i] != '\n') i++;
                continue;
            }
            if (ch == '/' && i + 1 < n && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(src[i] == '*' && src[i + 1] == '/')) i++;
                i += 2; continue;
            }
            if (ch == '/' && "=(,:[!&|?{};".IndexOf(prev) >= 0)
            {
                i++;
                while (i < n && src[i] != '/' && src[i] != '\n') i += src[i] == '\\' ? 2 : 1;
                i++;
                while (i < n && char.IsLetter(src[i])) i++;
                sb.Append("/x/"); prev = '/'; continue;
            }
            sb.Append(ch);
            if (!char.IsWhiteSpace(ch)) prev = ch;
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// v2.14.12: every colony*/chamber*-prefixed symbol app.js REFERENCES must also be DECLARED.
    /// v2.14.13: widened to topology* and overlay* so Stage 6/7 code is covered as it lands.
    ///
    /// This guard exists because v2.14.5-v2.14.10 shipped call sites whose definitions were never
    /// written: drawChambers/drawNode/maybeSpawn read colonyMotion and colonyLabels, loop() read
    /// colonyPheromones, buildNodes called chamberCentres, and the mouse handlers called
    /// chamberAt/moveChamber/persistChamber - none of which existed. An undeclared identifier is a
    /// runtime ReferenceError, not a syntax error, so `node --check` passed and CI stayed green
    /// while the live colony canvas rendered edges and no ants at all: loop() threw every frame
    /// after edges.forEach and before nodes.forEach.
    ///
    /// Scoped to the colony/chamber prefixes deliberately. A whole-file undeclared-global scan
    /// drowns in false positives from object keys and HTML attribute names inside template strings;
    /// this narrow rule covers the subsystem that actually broke and stays trustworthy.
    /// </summary>
    [Fact]
    public void UiIntegrity_ColonyAndChamberSymbolsAreDeclared()
    {
        var appJs = Path.Combine(RepoRoot(), "src", "Anthill.Api", "Ui", "app.js");
        var code = StripJsLiteralsAndComments(File.ReadAllText(appJs));

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(code, @"\bfunction\s+([A-Za-z_$][\w$]*)"))
            declared.Add(m.Groups[1].Value);

        // Declaration lists: `let a=1,b={x:0},c;` - split on top-level commas, then take each LHS.
        foreach (Match m in Regex.Matches(code, @"\b(?:const|let|var)\s+((?:[^;\n]|\n(?=\s*[A-Za-z_$]))*)"))
        {
            var depth = 0;
            var body = m.Groups[1].Value;
            var parts = new List<string>();
            var cur = new System.Text.StringBuilder();
            foreach (var ch in body)
            {
                if (ch == '(' || ch == '[' || ch == '{') depth++;
                else if (ch == ')' || ch == ']' || ch == '}') depth--;
                if (ch == ',' && depth == 0) { parts.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(ch);
            }
            parts.Add(cur.ToString());
            foreach (var part in parts)
            {
                var lhs = part.Split('=')[0];
                foreach (Match id in Regex.Matches(lhs, @"[A-Za-z_$][\w$]*")) declared.Add(id.Value);
            }
        }

        // References: prefixed identifiers that are not property access (`obj.chamberX`) and not
        // object-literal keys (`chambers:{}`), since neither is a binding lookup.
        var undeclared = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(code, @"(?<![.\w$])((?:colony|chamber|topology|overlay|CHAMBER|COLONY|TOPOLOGY|OVERLAY)[A-Za-z_$][\w$]*)(?![\w$])"))
        {
            var name = m.Groups[1].Value;
            if (Regex.IsMatch(code.Substring(m.Index + m.Length), @"^\s*:")) continue;
            if (declared.Contains(name)) continue;
            if (!undeclared.ContainsKey(name))
                undeclared[name] = code.Take(m.Index).Count(c => c == '\n') + 1;
        }

        Assert.True(undeclared.Count == 0,
            "src/Anthill.Api/Ui/app.js references colony/chamber symbols that are never declared, " +
            "which throws a ReferenceError at runtime while `node --check` still passes: " +
            string.Join("; ", undeclared.Select(kv => $"{kv.Key} (line {kv.Value})")));
    }

    /// <summary>
    /// v2.14.12: every colony canvas control in the markup must have a handler in app.js. The
    /// Motion/Labels/Pheromones selects and the View/Layout reset buttons shipped as inert markup
    /// because the listeners were never written. CSP is `script-src 'self'` with no unsafe-inline,
    /// so a control's only route to behaviour is a data-attribute dispatch in app.js.
    /// </summary>
    /// <summary>
    /// v2.14.13: ant names and accent colours are operator-controlled and are round-tripped
    /// verbatim by <see cref="UiStateStore"/> ("the UI owns the shape"), so the client is the only
    /// place they can be sanitised. Any node field that reaches innerHTML must go through
    /// escapeHtml, and any colour that reaches a style="" attribute must go through cssColor.
    ///
    /// The console's CSP is `script-src 'self'` with no unsafe-inline, which blocks the classic
    /// onerror payload — but CSP is a second line of defence, not a substitute for escaping, and
    /// markup injection remains possible without script execution.
    /// </summary>
    [Fact]
    public void UiIntegrity_OperatorControlledFieldsAreEscapedBeforeMarkup()
    {
        var appJs = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "Ui", "app.js"));

        // Only markup sinks matter. Assigning a template to .textContent or .value sets TEXT, not
        // HTML — escaping there would be a bug in the other direction, rendering a literal
        // "&amp;" to the operator. Verified: every such assignment in app.js closes on its own
        // line, so a line-scoped skip is complete rather than approximate.
        var raw = appJs.Split('\n')
            .Where(line => !Regex.IsMatch(line, @"\.(?:textContent|value)\s*="))
            .SelectMany(line => Regex.Matches(line,
                @"\$\{[^}]*\b(?:n|hit|node)\.(?:label|color|role|colony|parent)\b[^}]*\}")
                .Select(m => m.Value))
            .Where(v => !v.Contains("escapeHtml") && !v.Contains("cssColor"))
            .Distinct()
            .ToList();

        Assert.True(raw.Count == 0,
            "Operator-controlled node fields are interpolated into markup without escapeHtml/cssColor: "
            + string.Join(" | ", raw));

        Assert.Contains("function cssColor(", appJs);
    }

    /// <summary>
    /// v2.14.13 Stage 6: the topology became the dashboard background by RE-PARENTING the single
    /// canvas, not by adding a second renderer. Two invariants keep that true:
    ///
    /// 1. One canvas element and one render-loop bootstrap. A second renderer is how this project
    ///    previously ended up with two topologies, two sets of map preferences, and two inspectors
    ///    that disagreed (the cmap2 SVG, retired in v2.14.8).
    /// 2. `.ws-root` must not capture pointer events. The workspace root covers the entire
    ///    dashboard and now sits ON TOP of the map; if it captures clicks it becomes an invisible
    ///    shield over a topology you can no longer pan, drag ants on, or select chambers in —
    ///    and it would look like the map "went dead" rather than like a CSS bug.
    /// </summary>
    [Fact]
    public void UiIntegrity_TopologyHasOneRendererAndPassesPointersThrough()
    {
        var dir = Path.Combine(RepoRoot(), "src", "Anthill.Api", "Ui");
        var html = File.ReadAllText(Path.Combine(dir, "index.html"));
        var appJs = File.ReadAllText(Path.Combine(dir, "app.js"));
        var wsCss = File.ReadAllText(Path.Combine(dir, "dashboard-workspace.css"));

        var canvases = Regex.Matches(html, @"<canvas\b").Count;
        Assert.True(canvases == 1, $"Expected exactly one <canvas> in the console markup, found {canvases}.");

        var loopStarts = Regex.Matches(appJs, @"requestAnimationFrame\(loop\)").Count;
        Assert.True(loopStarts == 2,
            "Expected exactly two references to requestAnimationFrame(loop) — the self-schedule "
            + $"inside loop() and the single bootstrap call — found {loopStarts}.");

        var wsRoot = Regex.Match(wsCss, @"\.ws-root\s*\{[^}]*\}").Value;
        Assert.False(string.IsNullOrEmpty(wsRoot), "dashboard-workspace.css no longer defines .ws-root.");
        Assert.True(Regex.IsMatch(wsRoot, @"pointer-events\s*:\s*none"),
            ".ws-root must set pointer-events:none so the topology beneath it stays interactive. "
            + "Found: " + wsRoot);

        // Panels and toolbar have to opt back in, or the workspace chrome itself stops responding.
        Assert.True(Regex.IsMatch(wsCss, @"\.ws-panel\s*\{[^}]*pointer-events\s*:\s*auto"),
            ".ws-panel must re-enable pointer events.");
        Assert.True(Regex.IsMatch(wsCss, @"\.ws-toolbar[^{]*\{[^}]*pointer-events\s*:\s*auto"),
            ".ws-toolbar must re-enable pointer events.");
    }

    /// <summary>
    /// v2.14.14: the /ui/state endpoints must actually RUN the workspace sanitizer.
    ///
    /// Stage 1 built DashboardWorkspaceState with 20 unit tests and the stated decision that
    /// "layout correctness lives in C#" — but nothing ever called it. GET returned the raw file and
    /// PUT persisted the request body verbatim, so validation, clamping, off-screen recovery, and
    /// desktop/compact profile isolation were all inert in the running system while every test
    /// stayed green. Same shape as the v2.14.12 defect: tested code with no call site.
    /// </summary>
    [Fact]
    public void Workspace_SanitizerIsWiredIntoTheUiStateEndpoints()
    {
        var api = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "ApiHost.cs"));

        var getIdx = api.IndexOf("MapGet(\"/ui/state\"", StringComparison.Ordinal);
        var putIdx = api.IndexOf("MapPut(\"/ui/state\"", StringComparison.Ordinal);
        Assert.True(getIdx >= 0 && putIdx >= 0, "The /ui/state endpoints are missing.");

        // Look inside each handler only, so a call somewhere else in the file cannot satisfy this.
        var getBody = api.Substring(getIdx, Math.Min(900, api.Length - getIdx));
        var putBody = api.Substring(putIdx, Math.Min(1200, api.Length - putIdx));

        Assert.True(getBody.Contains("WithSanitizedWorkspace"),
            "GET /ui/state must return sanitized workspace state, or a hand-edited ui_state.json "
            + "reaches the browser unvalidated.");
        Assert.True(putBody.Contains("WithSanitizedWorkspace"),
            "PUT /ui/state must sanitize, or the client is told its unrepaired layout was accepted.");
    }

    /// <summary>
    /// v2.14.14: the server's canonical panel/overlay ids must match what the client registers.
    /// If they drift, Sanitize() silently deletes real panels as "unknown" and invents placements
    /// for panels that have no renderer.
    /// </summary>
    [Fact]
    public void Workspace_CanonicalIdsMatchTheClientRegistrations()
    {
        var appJs = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.Api", "Ui", "app.js"));

        // v3.3.0: the grid replaced the floating workspace, so this reads the GRID widget
        // registrations. Both def shapes start `{id:'x', title:'...'` — the grid's carry a `size:`
        // and the workspace's do not, which is what distinguishes them while both files coexist.
        // The property being defended is unchanged and still worth defending: if the server and the
        // client disagree about which widgets exist, Sanitize() silently drops real ones and
        // invents placements for ones that have no renderer.
        var registered = Regex.Matches(appJs, @"\{\s*id\s*:\s*'([a-z0-9-]+)'\s*,\s*title\s*:[^}]*?\bsize\s*:")
            .Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(registered.Count > 0, "Could not find any grid widget registrations in app.js.");
        Assert.Equal(
            DashboardWorkspaceState.KnownPanelIds.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            registered);

        var overlays = Regex.Matches(appJs, @"TOPOLOGY_OVERLAYS\s*=\s*\{(.*?)\n\};", RegexOptions.Singleline);
        Assert.True(overlays.Count == 1, "app.js must declare exactly one TOPOLOGY_OVERLAYS registry.");
        var overlayIds = Regex.Matches(overlays[0].Groups[1].Value, @"^\s*([a-z]+)\s*:", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(
            DashboardWorkspaceState.KnownOverlayIds.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            overlayIds);
    }

    /// <summary>
    /// v2.15.0 Stage 8: ui_state.json has exactly ONE writer.
    ///
    /// app.js and dashboard-workspace.js used to run independent debounced read-modify-write
    /// cycles against the same document on different timers (350ms and 600ms). Each preserved the
    /// other's keys, but they still raced: a panel drag landing inside an ant rename's window read
    /// a stale document, and whichever PUT finished second silently discarded the other's change.
    ///
    /// Both surfaces now register mutators with UiStateWriter in app.js — one debounce, one read,
    /// one write, chained so flushes cannot interleave.
    /// </summary>
    [Fact]
    public void UiState_HasASingleWriter()
    {
        var dir = Path.Combine(RepoRoot(), "src", "Anthill.Api", "Ui");
        var appJs = File.ReadAllText(Path.Combine(dir, "app.js"));
        var wsJs = File.ReadAllText(Path.Combine(dir, "dashboard-workspace.js"));

        Assert.Contains("const UiStateWriter", appJs);
        Assert.Contains("window.AnthillUiState = UiStateWriter", appJs);

        // app.js must not keep a second ad-hoc PUT alongside the writer.
        var appPuts = Regex.Matches(appJs, @"'/ui/state'\s*,\s*'PUT'").Count;
        Assert.True(appPuts == 1,
            $"app.js should PUT /ui/state from exactly one place (UiStateWriter); found {appPuts}.");

        // The workspace module must prefer the shared writer, and its own PUT may only survive as
        // the guarded fallback for app.js being absent.
        var saveIdx = wsJs.IndexOf("function save()", StringComparison.Ordinal);
        Assert.True(saveIdx >= 0, "dashboard-workspace.js no longer defines save().");
        var saveBody = wsJs.Substring(saveIdx, Math.Min(1400, wsJs.Length - saveIdx));
        Assert.True(saveBody.Contains("window.AnthillUiState"),
            "dashboard-workspace.js save() must route through the shared writer, not its own cycle.");
        Assert.True(saveBody.IndexOf("window.AnthillUiState", StringComparison.Ordinal)
                    < saveBody.IndexOf("'/ui/state', 'PUT'", StringComparison.Ordinal),
            "The shared writer must be tried BEFORE the fallback PUT, or the race returns.");

        // Overlays belong to app.js; the workspace must drop its stale copy on load.
        Assert.Contains("delete W.state.topology_overlays", wsJs);
    }

    [Fact]
    public void UiIntegrity_ColonyCanvasControlsHaveHandlers()
    {
        var dir = Path.Combine(RepoRoot(), "src", "Anthill.Api", "Ui");
        var html = File.ReadAllText(Path.Combine(dir, "index.html"));
        var appJs = File.ReadAllText(Path.Combine(dir, "app.js"));
        var missing = new List<string>();

        foreach (var attr in new[] { "colonyact", "colonypref" })
        {
            var values = Regex.Matches(html, "data-" + attr + "=\"([^\"]+)\"")
                              .Select(m => m.Groups[1].Value).Distinct().ToList();
            if (values.Count == 0) continue;

            // The dispatch must read the attribute at all...
            if (!appJs.Contains("dataset." + attr) && !appJs.Contains("data-" + attr))
                missing.Add($"data-{attr} is used in index.html but app.js never reads it");

            // ...and each distinct value must be named somewhere in app.js.
            foreach (var v in values)
                if (!appJs.Contains("'" + v + "'") && !appJs.Contains("\"" + v + "\""))
                    missing.Add($"data-{attr}=\"{v}\" has no handler in app.js");
        }

        Assert.True(missing.Count == 0,
            "Colony canvas controls exist in markup but do nothing: " + string.Join("; ", missing));
    }

    [Fact]
    public void UiIntegrity_NoOrphanedElementLookupsAndNoDuplicateIds()
    {
        var ui = UiSource(); // index.html (static ids) + app.js (getElementById + template-built ids)
        // Created via document.createElement at runtime, so they legitimately have no static id=.
        // ws-root, ws-topology, ws-topbar and ws-bottombar only exist when
        // dashboard_workspace_enabled is on — which is precisely why they are built at runtime
        // rather than declared in markup.
        var dynamicIds = new HashSet<string>
        {
            "pc-toast",
            // The workspace shell is built at runtime and only exists when the kill switch is on,
            // which is precisely why these carry no static id= in the markup.
            "ws-root", "ws-topology", "ws-topbar", "ws-bottombar",
            "ws-panel-layer", "ws-guides", "ws-snapzones", "ws-modules",
            // v3.3.0: the grid root is created by initDashboardGrid for the same reason — it is
            // the container the widget framework owns and rewrites, not page markup.
            "dg-root",
        };

        var declared = Regex.Matches(ui, "id=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        var duplicates = declared.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "Duplicate element id(s) in UI markup: " + string.Join(", ", duplicates));

        var declaredSet = declared.ToHashSet();
        var orphans = Regex.Matches(ui, @"getElementById\('([^']+)'\)")
            .Select(m => m.Groups[1].Value).Distinct()
            .Where(id => !declaredSet.Contains(id) && !dynamicIds.Contains(id))
            .OrderBy(id => id).ToList();
        Assert.True(orphans.Count == 0,
            "getElementById target(s) with no matching id= in markup (orphaned lookups): " + string.Join(", ", orphans));
    }

    /// <summary>
    /// v2.2.3 shipped 'Other · 25': the registry serializes PascalCase but a UI adapter read only
    /// camelCase, classifying the whole colony as Other. The adapter accessors must stay
    /// case-tolerant (both casings listed) so a serializer/policy change can never silently
    /// unclassify the colony again.
    /// </summary>
    [Fact]
    public void UiIntegrity_RegistryAdapterAccessorsAreCaseTolerant()
    {
        var ui = UiSource(); // adapter accessors live in app.js since the v2.6.3 script externalization
        Assert.Contains("prop(r,'roleId','RoleId'", ui);
        Assert.Contains("prop(w,'workerId','WorkerId'", ui);
        Assert.Contains("prop(r,'workers','Workers')", ui);
    }

    // ---- No active Python ------------------------------------------------------------------------
    // py.old/ is archived history. No .py file may exist anywhere else in the repo.

    [Fact]
    public void NoPython_NoPyFilesOutsidePyOld()
    {
        var root = RepoRoot();
        var offenders = Directory.EnumerateFiles(root, "*.py", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Where(p => !p.StartsWith("py.old/", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains("/bin/") && !p.Contains("/obj/") && !p.StartsWith(".git/"))
            .ToList();
        Assert.True(offenders.Count == 0,
            "Active Python files found outside py.old/ (forbidden by NORTH_STAR §3.1 rule 13): "
            + string.Join(", ", offenders));
    }
}
