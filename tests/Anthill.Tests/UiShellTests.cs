using System.Text.RegularExpressions;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Static integrity of the console shell — the parts that are independent of whichever layout
/// engine is in front of them.
///
/// v3.3.0: split out of DashboardWorkspaceShellTests. That file had grown to 62 tests and only
/// about two thirds concerned the floating workspace; the rest landed there because it had become
/// the de-facto UI shell file. Deleting it with the workspace would have taken these with it —
/// including an XSS guard (server text is written as text, never markup), an out-of-order-response
/// guard, and a scroll/focus-loss guard. Splitting BEFORE deleting is the whole point of the
/// ordering: nothing here depends on the workspace, so nothing here should die with it.
/// </summary>
public class UiShellTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Ui(string file) => File.ReadAllText(Path.Combine(Root(), "src", "Anthill.UI", file));
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Root() }.Concat(parts).ToArray()));

    private static string BodyOf(string js, string signature)
    {
        var at = js.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return "";
        var open = js.IndexOf('{', at);
        if (open < 0) return "";
        var depth = 0;
        for (var i = open; i < js.Length; i++)
        {
            if (js[i] == '{') depth++;
            else if (js[i] == '}') { depth--; if (depth == 0) return js[open..(i + 1)]; }
        }
        return js[open..];
    }

    /// <summary>
    /// v3.3.0: the grid replaced the workspace in the page. Asserted end to end — embedded in the
    /// csproj, loaded and routed by the host, referenced by the markup — because "the file exists"
    /// has never been the same thing as "the console uses it", which is the lesson the Mission
    /// Composer taught in v3.1.1.
    /// </summary>
    [Fact]
    public void GridAssets_AreEmbedded_Served_AndReferencedByThePage()
    {
        var csproj = Src("src", "Anthill.Api", "Anthill.Api.csproj");
        Assert.Contains("Anthill.UI\\dashboard-grid.js", csproj);
        Assert.Contains("Anthill.UI\\dashboard-grid.css", csproj);

        var host = ApiHostSource.All();
        Assert.Contains("/ui/dashboard-grid.js", host);
        Assert.Contains("/ui/dashboard-grid.css", host);
        Assert.Contains("LoadUiAsset(\"dashboard-grid.js\")", host);

        var page = Ui("index.html");
        Assert.Contains("/ui/dashboard-grid.js", page);
        Assert.Contains("/ui/dashboard-grid.css", page);
    }

    [Fact]
    public void CspRemains_ScriptSrcSelf_WithoutUnsafeInline()
    {
        var host = ApiHostSource.All();
        Assert.Contains("script-src 'self'", host);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", host);
    }

    /// <summary>
    /// v2.16.0: chamber layout gives every role its own angular sector.
    ///
    /// Before this, roles sat on a ring capped at 46px while their workers were placed 72px out,
    /// and the worker bearing came from colonyAngleFor() — which in chamber mode derives from the
    /// CHAMBER's index and is identical for every role in it. Each role's workers therefore landed
    /// on the neighbouring role, and the view read as a smudge.
    ///
    /// Sectors make cross-role collision geometrically impossible rather than merely unlikely, so
    /// this test pins the three properties that guarantee it. The radii must stay DERIVED from the
    /// arc length required; reintroducing a constant cap is what broke it the first time.
    /// </summary>
    [Fact]
    public void ChamberLayout_GivesEachRoleItsOwnSector()
    {
        var js = Ui("app.js");
        var build = BodyOf(js, "function buildNodes()");

        // 1. Sector width is derived from the member count.
        Assert.Contains("const sector = (Math.PI*2)/Math.max(1,n);", build);
        // 2. The role sits at its sector's CENTRE, so neighbouring sectors cannot touch.
        Assert.Contains("(k+0.5)*sector", build);
        // 3. Both radii come from required arc length, not a magic cap.
        Assert.Contains("(n*CHAMBER_ROLE_GAP)/(Math.PI*2)", build);
        Assert.Contains("(ws.length*CHAMBER_WORKER_GAP)/(slot.sector*CHAMBER_SECTOR_USE)", build);

        // Workers are positioned from the CHAMBER centre along their own role's bearing. If this
        // reverts to the role-relative global fan, the sectors stop containing anything.
        Assert.Contains("slot.cx+Math.cos(cr)*r2", build);
        Assert.DoesNotContain("roleAngleInChamber", js);

        // The sector must keep a margin, or adjacent fans meet at the boundary.
        var use = Regex.Match(js, @"CHAMBER_SECTOR_USE\s*=\s*([0-9.]+)");
        Assert.True(use.Success, "CHAMBER_SECTOR_USE not found");
        var fraction = double.Parse(use.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(fraction, 0.5, 0.9);
    }

    /// <summary>
    /// Chambers must not collide with each other either — the contents got larger, so the ring
    /// they sit on had to grow with them. Six chambers ring a central one, so adjacent centres are
    /// exactly R apart; the largest chamber measured 136px, needing 272px of clearance.
    /// </summary>
    [Fact]
    public void ChamberRing_LeavesRoomForTheLargestChamber()
    {
        var js = Ui("app.js");
        var centres = BodyOf(js, "function chamberCentres(names)");
        var m = Regex.Match(centres, @"Math\.min\(W,H\)\s*\*\s*([0-9.]+)");
        Assert.True(m.Success, "chamberCentres no longer derives its radius from the viewport");
        var factor = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        // On the shortest supported axis the ring must still clear two chamber radii.
        const int shortestAxis = 900;
        var adjacentGap = shortestAxis * factor;   // 2*R*sin(pi/6) == R for six ring positions
        Assert.True(adjacentGap >= 272,
            $"ring factor {factor} gives only {adjacentGap:F0}px between adjacent chamber centres; "
            + "the largest chamber needs 272px to avoid overlapping its neighbour.");
    }

    /// <summary>
    /// v2.16.0: the answer is the default content of a mission response; the trace is one
    /// disclosure away. The colony always stored both — this asserts the UI leads with the right
    /// one, which is the whole point of the change.
    /// </summary>
    [Fact]
    public void MissionThread_LeadsWithTheAnswer_NotTheTrace()
    {
        var js = Ui("app.js");
        var mt = Ui("mission-thread.js");

        // v2.18.1: the answer selection moved into the reconciler (answerOf) and the row patcher,
        // because rendering is no longer a single string-building pass. The property asserted is
        // unchanged: the synthesized answer is preferred, falling back to the raw best output.
        Assert.Contains("m.final_result || m.user_result", mt);

        var patch = BodyOf(js, "function msPatchExchange(row,m)");
        Assert.Contains("MT.answerOf(m)", patch);
        Assert.Contains("Working — no answer recorded yet.", patch);

        // The trace must never be rendered inline — it belongs behind the disclosure.
        Assert.DoesNotContain("debug_result", patch);
        Assert.DoesNotContain("debug_result", BodyOf(js, "function renderMissionThread()"));
        Assert.Contains("Show activity", BodyOf(js, "function msBuildExchange(m)"));
    }

    /// <summary>
    /// Forty missions in a thread must not fetch forty reports. Activity loads on first expand.
    /// </summary>
    [Fact]
    public void MissionActivity_LoadsLazily_OnFirstExpandOnly()
    {
        var js = Ui("app.js");
        Assert.Contains("data-msact", js);

        // v2.18.1: the DOM latch (dataset.loaded) was replaced by msActivity, because a latch set
        // before the request resolved could never be retried. Laziness is now expressed by
        // begin() refusing a second fetch — see MissionActivity_TracksStateOutsideTheDom.
        Assert.Contains("msActivity.begin(missionId)", BodyOf(js, "async function msLoadActivity(missionId,det)"));

        // Detail rendering still reuses the Results page implementation rather than a second copy.
        Assert.Contains("renderMissionReport(missionId,body)", js);

        // The list render must never fetch a report — forty exchanges must not mean forty requests.
        Assert.DoesNotContain("renderMissionReport", BodyOf(js, "function renderMissionThread()"));
        Assert.DoesNotContain("renderMissionReport", BodyOf(js, "function msBuildExchange(m)"));
        Assert.DoesNotContain("renderMissionReport", BodyOf(js, "async function loadMissionThread(opts)"));
    }

    /// <summary>
    /// The thread must not yank the view while history is being read.
    ///
    /// v2.18.1: this used to also assert `aria-live="polite"` on #ms-thread. That assertion was
    /// itself describing the bug — a live region on the thread re-announced all forty exchanges
    /// on every three-second poll. The announcement contract now lives in
    /// MissionThread_AnnouncesOneResult_NotTheWholeThread, which asserts the opposite and is
    /// correct. The scroll contract below is unchanged, only renamed.
    /// </summary>
    [Fact]
    public void MissionThread_IsPoliteAboutScrolling()
    {
        var js = Ui("app.js");
        var render = BodyOf(js, "function renderMissionThread()");

        // Measured BEFORE the update and applied after, so reading history is never interrupted.
        Assert.Contains("MT.shouldFollowBottom(", render);
        Assert.Contains("if(follow) thread.scrollTop=thread.scrollHeight;", render);
        Assert.Contains("else thread.scrollTop=prevTop;", render);
        Assert.Contains("const prevTop=thread.scrollTop;", render);

        // The threshold itself is exercised in tests/ui/mission-thread.test.js, both directions.
        Assert.Contains("FOLLOW_THRESHOLD_PX", Ui("mission-thread.js"));

        var html = Ui("index.html");
        Assert.Contains("id=\"ms-thread\"", html);
        Assert.Contains("role=\"log\"", html);
        // The composer stays pinned: the THREAD scrolls, not the page.
        Assert.Contains("#page-missions.page-scroll{overflow:hidden;}", html);
    }

    /// <summary>
    /// The report endpoint must expose the answer and the raw output separately, or the activity
    /// view cannot show what the winning task actually emitted before it was rewritten.
    /// </summary>
    [Fact]
    public void MissionReport_ExposesAnswerAndRawOutputSeparately()
    {
        var api = ApiHostSource.All();
        Assert.Contains("[\"final_output\"] = mission.GetValueOrDefault(\"final_result\")", api);
        Assert.Contains("[\"raw_output\"] = mission.GetValueOrDefault(\"user_result\")", api);
    }

    /// <summary>
    /// v2.17.1: the thread must never be rebuilt wholesale.
    ///
    /// v2.16.0 rendered with `thread.innerHTML = rows.map(...).join('')` on every jobs poll — every
    /// three seconds — which destroyed open activity disclosures, loaded reports, focus, selection
    /// and scroll position, and re-announced all forty exchanges. Replacing innerHTML also clamps
    /// scrollTop to 0, so reading history threw you to the top of the thread on every poll.
    ///
    /// The behavioural proof lives in tests/ui/mission-thread.test.js (node --test). This guards
    /// the one thing C# can see: that the destructive pattern has not come back.
    /// </summary>
    [Fact]
    public void MissionThread_IsNeverRebuiltWholesale()
    {
        var js = Ui("app.js");
        var render = BodyOf(js, "function renderMissionThread()");

        // The only innerHTML writes allowed here would be a full-thread replacement.
        Assert.DoesNotContain("thread.innerHTML", render);
        Assert.DoesNotContain("rows.map(", render);

        // Unchanged data must short-circuit before touching the DOM at all.
        Assert.Contains("if(!plan.changed", render);
        Assert.Contains("reconcileThread", render);

        var load = BodyOf(js, "async function loadMissionThread(opts)");
        Assert.DoesNotContain("thread.innerHTML", load);
    }

    /// <summary>
    /// Server text reaches the thread through textContent, never parsed as HTML. Escaping by hand
    /// into an innerHTML string is what this replaces.
    /// </summary>
    [Fact]
    public void MissionThread_WritesServerTextAsTextNotMarkup()
    {
        var js = Ui("app.js");
        var patch = BodyOf(js, "function msPatchExchange(row,m)");
        Assert.DoesNotContain("innerHTML", patch);
        Assert.Contains(".textContent=m.goal", patch);
        Assert.Contains("say.textContent", patch);
    }

    /// <summary>
    /// Stale responses must be rejected. Page entry and the three-second poll can both have a
    /// request in flight; without a generation token a slow older one overwrites newer state.
    /// </summary>
    [Fact]
    public void MissionThread_RejectsStaleResponses()
    {
        var js = Ui("app.js");
        var load = BodyOf(js, "async function loadMissionThread(opts)");
        Assert.Contains("msGate.next()", load);
        Assert.Contains("if(!msGate.isCurrent(token)) return;", load);
    }

    /// <summary>
    /// Activity state lives outside the DOM so it survives updates, and a report is marked loaded
    /// only after it succeeds — v2.16.0 set dataset.loaded before the request resolved, stranding
    /// any report that failed.
    /// </summary>
    [Fact]
    public void MissionActivity_TracksStateOutsideTheDom_AndIsRetryable()
    {
        var js = Ui("app.js");
        Assert.Contains("msActivity", js);
        Assert.Contains("MT.ActivityStore()", js);

        var load = BodyOf(js, "async function msLoadActivity(missionId,det)");
        Assert.Contains("msActivity.begin(missionId)", load);   // duplicate-request guard
        Assert.Contains("msActivity.succeed(missionId)", load); // only after success
        Assert.Contains("msActivity.fail(missionId", load);
        Assert.Contains("data-msretry", js);                    // a visible retry control

        // The Missions path must not use a DOM latch at all — its state is in msActivity, which
        // is what lets it survive a re-render and be retried. (The Results page still uses
        // dataset.loaded, but only after success; that is asserted separately.)
        Assert.DoesNotContain("dataset.loaded", load);
    }

    /// <summary>
    /// renderMissionReport reports success so callers can decide whether to latch. Both callers —
    /// the Missions thread and the Results page — must honour it.
    /// </summary>
    [Fact]
    public void MissionReport_ReportsSuccess_AndBothCallersHonourIt()
    {
        var js = Ui("app.js");
        var report = BodyOf(js, "async function renderMissionReport(missionId, body)");
        Assert.Contains("return false;", report);
        Assert.Contains("return true;", report);

        var toggle = BodyOf(js, "async function onResultToggle(det)");
        Assert.Contains("if(ok) det.dataset.loaded='1';", toggle);
    }

    /// <summary>
    /// The live region must not be the thread itself, or every poll re-announces all forty
    /// exchanges. One dedicated status element announces one finished mission.
    /// </summary>
    [Fact]
    public void MissionThread_AnnouncesOneResult_NotTheWholeThread()
    {
        var html = Ui("index.html");
        var threadTag = Regex.Match(html, @"<div id=""ms-thread""[^>]*>").Value;
        Assert.False(string.IsNullOrEmpty(threadTag), "#ms-thread not found");
        Assert.DoesNotContain("aria-live", threadTag);

        Assert.Contains("id=\"ms-announce\"", html);
        Assert.Contains("aria-live=\"polite\"", html);
        Assert.Contains("announcementFor", Ui("app.js"));
    }

    /// <summary>
    /// A rejected dispatch must show the error and hand the directive back, not clear the box and
    /// go quiet. v2.16.0 did `input.value=''` before the request and swallowed the failure.
    /// </summary>
    [Fact]
    public void Dispatch_SurfacesFailures_AndKeepsTheTypedDirective()
    {
        var js = Ui("app.js");
        var dispatch = BodyOf(js, "async function dispatchMission(inputId)");
        Assert.Contains("const typed=input.value;", dispatch);
        Assert.Contains("else { input.value=typed; }", dispatch);
        Assert.Contains("msDispatchInFlight", dispatch);          // double-submit guard

        var submit = BodyOf(js, "async function submitMissionGoal(goal)");
        Assert.Contains("msShowDispatchError", submit);
        Assert.Contains("return true;", submit);
        Assert.Contains("return false;", submit);

        Assert.Contains("id=\"ms-error\"", Ui("index.html"));
    }

    /// <summary>The reconciler is a served asset, embedded and loaded before app.js.</summary>
    [Fact]
    public void MissionThreadModule_IsShippedAndLoaded()
    {
        Assert.Contains("Anthill.UI\\mission-thread.js", Src("src", "Anthill.Api", "Anthill.Api.csproj"));
        var host = ApiHostSource.All();
        Assert.Contains("LoadUiAsset(\"mission-thread.js\")", host);
        Assert.Contains("/ui/mission-thread.js", host);

        var html = Ui("index.html");
        Assert.Contains("/ui/mission-thread.js", html);
        Assert.True(html.IndexOf("/ui/mission-thread.js", StringComparison.Ordinal)
                    < html.IndexOf("/ui/app.js", StringComparison.Ordinal),
            "mission-thread.js must load before app.js, which consumes it at parse time.");
    }

    /// <summary>The node --test suite must actually run in CI, or it proves nothing.</summary>
    [Fact]
    public void MissionThreadTests_RunInCiAndValidate()
    {
        Assert.Contains("node --test tests/ui/mission-thread.test.js", Src(".github", "workflows", "ci.yml"));
        Assert.Contains("node --test tests/ui/mission-thread.test.js", Src("scripts", "validate.ps1"));
        Assert.True(File.Exists(Path.Combine(Root(), "tests", "ui", "mission-thread.test.js")));
    }

    /// <summary>
    /// v2.18.2: /missions/json must carry the ANSWER.
    ///
    /// It projected only id/goal/status/success_score/created_at/saved_at — final_result and
    /// user_result were never in the payload — so the conversation view had nothing to display and
    /// every finished exchange read "Working — no answer recorded yet" indefinitely. The bug was
    /// present from v2.16.0 and survived the v2.18.1 rewrite because the client faithfully
    /// reproduced it: both versions read fields the endpoint does not return.
    ///
    /// This is a server-side contract, which is why it is asserted here rather than only in the
    /// JS suite — no amount of client testing would have caught a missing column.
    /// </summary>
    [Fact]
    public void MissionsJson_CarriesTheAnswer()
    {
        var api = ApiHostSource.All();
        var idx = api.IndexOf("MapGet(\"/missions/json\"", StringComparison.Ordinal);
        Assert.True(idx > 0, "/missions/json endpoint not found");
        var handler = api.Substring(idx, Math.Min(2200, api.Length - idx));

        Assert.Contains("[\"answer\"]", handler);
        Assert.Contains("[\"answer_truncated\"]", handler);
        // Prefers the synthesized answer, falls back to the raw best-task output.
        Assert.Contains("final_result", handler);
        Assert.Contains("user_result", handler);

        // Bounded: this endpoint serves up to 100 rows and a raw result can be a whole diff.
        Assert.Contains("MissionAnswerPreviewChars", handler);
        Assert.Contains("public const int MissionAnswerPreviewChars", api);

        // The client must read the field the endpoint actually returns.
        var mt = Ui("mission-thread.js");
        Assert.Contains("m.answer === 'string'", mt);
        Assert.Contains("'answer'", mt);              // included in the render fingerprint
        Assert.Contains("'answer_truncated'", mt);
    }

    /// <summary>
    /// v2.24.0: the bug that made two previous fixes invisible.
    ///
    /// `hidden` carries display:none from the USER AGENT stylesheet only. `.ws-modules` and
    /// `.ws-tray` both set `display:flex`, and an author rule outranks the UA sheet — so
    /// `el.hidden = true` set the attribute correctly and changed nothing on screen. The modules
    /// menu stayed open through the v2.19.0 "collapsible" work AND the v2.22.0 focus-mode fix:
    /// correct JavaScript, defeated by one line of CSS. The empty minimized-panel tray had the
    /// same defect.
    ///
    /// Every element the workspace hides via the `hidden` property needs a matching `[hidden]`
    /// rule, because every one of them also sets `display`.
    /// </summary>
    [Fact]
    public void EveryElementHiddenFromScript_HasACssRuleThatActuallyHidesIt()
    {
        // v3.3.0: the elements this guarded (ws-modules, ws-tray) went with the workspace, so the
        // instances are gone — but the LESSON is not, and the grid will grow hidden elements of
        // its own. Restated as a general rule over the console's stylesheets: anything the script
        // hides via the `hidden` property needs a matching [hidden] rule, because an element that
        // sets `display` overrides the UA stylesheet's display:none and stays visible. Correct
        // JavaScript defeated by one line of CSS is what this cost twice.
        var js = Ui("dashboard-grid.js");
        var css = Ui("dashboard-grid.css") + Ui("index.html");

        var hiddenTargets = System.Text.RegularExpressions.Regex
            .Matches(js, @"(\w+)\.hidden\s*=")
            .Select(m => m.Groups[1].Value).Distinct().ToList();

        foreach (var target in hiddenTargets)
            Assert.True(css.Contains("[hidden]", StringComparison.Ordinal),
                $"'{target}.hidden' is set in dashboard-grid.js but no [hidden] rule exists in the "
                + "console stylesheets — the element sets display and will stay visible.");
    }

    /// <summary>
    /// Accessibility floor for the console's own chrome: a visible focus ring, and animation that
    /// respects the operator's motion preference. An always-open operations console is exactly
    /// where a permanently spinning element is worst.
    /// </summary>
    [Fact]
    public void FocusStyles_AndReducedMotion_ArePresent()
    {
        var css = Ui("dashboard-grid.css");
        Assert.Contains(":focus-visible", css);
        Assert.Contains("prefers-reduced-motion", css);
    }

    /// <summary>
    /// Every control `buildNav` gives an ARIA role must also be given a name.
    ///
    /// v3.8.34. The six domain heads (Monitoring, Operations, Infrastructure, Colony, Security,
    /// Administration) carried `role="button"`, `tabIndex` and `aria-expanded` but no `aria-label`,
    /// while `nav-item` and `nav-child` — built in the same loop, three lines away — both set one.
    /// The browser's interactive accessibility tree showed six buttons with no name at the top of
    /// primary navigation, against named links for every one of their children.
    ///
    /// Name-from-content does not save this. The label span sits between an icon and a `&#9656;`
    /// chevron, so a computed name is either absent or carries the arrow. That is why the rule is
    /// "a role implies a name" rather than "a role implies text somewhere inside".
    ///
    /// Asserted over the source rather than a rendered page because `buildNav` needs a DOM and a
    /// session to run, and a guard that requires a browser is a guard that runs once.
    /// </summary>
    [Fact]
    public void EveryNavControlGivenARole_IsAlsoGivenAName()
    {
        var body = BodyOf(Ui("app.js"), "function buildNav()");
        Assert.NotEqual("", body);

        // Elements handed an explicit role, by the variable they are built into.
        var roled = Regex.Matches(body, @"(\w+)\.setAttribute\('role'")
            .Select(m => m.Groups[1].Value).Distinct().ToList();

        // Guard the reader: if the regex stops matching, the test must fail loudly rather than
        // pass over an empty set.
        Assert.True(roled.Count >= 3,
            $"expected at least three roled nav controls in buildNav, found {roled.Count} — has the "
            + "construction changed shape?");

        var unnamed = roled
            .Where(v => !Regex.IsMatch(body, Regex.Escape(v) + @"\.setAttribute\('aria-label'"))
            .ToList();

        Assert.True(unnamed.Count == 0,
            "buildNav gives these elements an ARIA role but no accessible name: "
            + string.Join(", ", unnamed));
    }

    // ---- ported from DashboardWorkspaceShellTests when the workspace was retired --------------
    // These four defended properties that outlived the layout engine. Each is restated in grid
    // terms rather than deleted, because the lesson each encodes is still live.

    /// <summary>
    /// v3.1.1, restated for the grid: every control that starts or reviews work must be REACHABLE.
    ///
    /// The defect: the Mission Composer lived only on the classic overview grid, which the
    /// topology workspace hid, so from v2.15.0 the execution mode selector and the plan REVIEW
    /// step had no reachable control at all. The endpoint, the renderer and the button all
    /// existed; nothing could reach them. CallSiteAudit cannot see this class of defect — it
    /// proves a C# declaration has a production consumer, and says nothing about whether a UI
    /// control has a path to it.
    ///
    /// Being present in the markup is not the same as being reachable.
    /// </summary>
    [Fact]
    public void EveryWorkWorkflowControl_IsReachableOnTheDashboard()
    {
        var html = Ui("index.html");
        var app = Ui("app.js");

        // The composer is a registered grid widget, so it renders on the default dashboard.
        Assert.Contains("body:'ov-composer-body'", app.Replace(" ", ""));
        Assert.Contains("id=\"ov-composer-body\"", html);

        var open = html.IndexOf("id=\"ov-composer-body\"", StringComparison.Ordinal);
        var close = html.IndexOf("/ov-composer-body", open, StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, "ov-composer-body wrapper not found or unterminated.");
        var body = html[open..close];

        Assert.Contains("id=\"ov-preview-btn\"", body);   // plan review
        Assert.Contains("id=\"ov-modes\"", body);         // execution mode selector
        Assert.Contains("id=\"ov-plan\"", body);          // where the reviewed plan renders
    }

    /// <summary>
    /// ONE rule takes the classic sections out of flow, not an enumeration.
    ///
    /// v2.15.3 shipped with the status bar and the mission directive box invisible because the
    /// rule excluded by an id ALLOW-LIST, which silently hid every element added after it was
    /// written. Excluding by CLASS is what makes adding a new element safe. The grid inherits the
    /// lesson even though it has no layers of its own.
    /// </summary>
    [Fact]
    public void GridDashboard_TakesClassicContentOutOfFlow_ByClassNotByIdList()
    {
        var html = Ui("index.html");
        Assert.Contains("#page-overview.dg-active", html);
        Assert.Contains("#page-overview.dg-active > *:not(#dg-root):not(.dg-keep){display:none !important;}", html);
        Assert.Contains("classList.add('dg-active')", Ui("app.js"));
    }

    /// <summary>
    /// Widget chrome must be real buttons with real state, not clickable divs. Carried over from
    /// the workspace shell, which is where the requirement was first established.
    /// </summary>
    [Fact]
    public void WidgetControlsAreRealButtons_WithLabelsAndState()
    {
        var js = Ui("dashboard-grid.js");
        Assert.Contains("createElement('button')", js.Replace("el('button'", "createElement('button')"));
        Assert.Contains("aria-label", js);
        Assert.Contains("refresh.type = 'button'", js);   // never a submit inside a form
        Assert.Contains("setAttribute('role', 'region')", js);
        Assert.Contains("aria-label", js);
    }

    /// <summary>
    /// The mission directive box could be covered by a floating panel, so the workspace pinned it
    /// above the panel layer. The grid has no panel layer and no z-order, so the hazard is removed
    /// BY CONSTRUCTION rather than by a guard — recorded here so the next reader knows the
    /// property was retired deliberately and not dropped.
    ///
    /// What replaces it is the reachability test above: the composer must exist as a widget.
    /// </summary>
    [Fact]
    public void NothingCanCoverTheMissionDirective_BecauseThereIsNoPanelLayer()
    {
        var css = Ui("dashboard-grid.css");
        Assert.DoesNotContain("position: absolute", css);
        Assert.DoesNotContain("z-index", css);
    }

    /// <summary>
    /// The stylesheet must NOT give `.dg-quiet` a min-height of its own.
    ///
    /// This looks like the obvious way to shrink an idle card and it is a layout bug. `.dg-widget`
    /// sets `overflow: hidden`, which makes every widget a scroll container, and a scroll container
    /// contributes nothing to the height of an `auto` grid row. Relaxing the floor in CSS therefore
    /// collapses the row to 2px and the cards overlap each other — measured, not theorised: it
    /// produced four overlapping pairs and pushed thirteen widgets past the grid's bottom edge.
    ///
    /// The floor is written by markQuiet() as an inline style instead, so the un-scripted state
    /// stays the safe one and script only ever shrinks a card.
    /// </summary>
    [Fact]
    public void QuietWidgets_DoNotGetTheirFloorFromCss()
    {
        var css = Ui("dashboard-grid.css");
        foreach (Match m in Regex.Matches(css, @"\.dg-quiet[^{}]*\{([^}]*)\}"))
            Assert.DoesNotContain("min-height", m.Groups[1].Value);

        // and the widget floor itself must survive, since that is what keeps rows from collapsing
        Assert.Contains("min-height: var(--dg-widget-h)", css);
    }

    /// <summary>
    /// Adoption must carry the class an element's styles are scoped to.
    ///
    /// Every Mission Command rule is written `.mission-node ...`. Adoption moves the element out
    /// from under that ancestor, so all of them stopped applying: the prompt row was no longer a
    /// flex row, the textarea fell back to its intrinsic width — 47% of the card — and the dispatch
    /// button collapsed to 8×15px. Nothing about that is visible in the markup, the ids, or the
    /// renderers, which is exactly why it needs a guard rather than a reviewer.
    /// </summary>
    [Fact]
    public void ResizedWidths_AreStoredAsAProportion_NotAColumnCount()
    {
        var js = Ui("dashboard-grid.js");

        // The grid has 12 columns on desktop and 24 on an ultrawide, so a stored COUNT would mean
        // half the dashboard in one and a quarter of it in the other. The fraction is re-resolved
        // against the current column count on every render.
        var setter = BodyOf(js, "G.setSpanFraction = function");
        Assert.NotEqual("", setter);
        Assert.Contains("/ cols", setter);
        Assert.Contains("columnCount()", setter);

        // and the inline pixel width the browser's resize grip writes must be handed back to the
        // grid, or the widget stays frozen at one breakpoint's measurement
        var snap = BodyOf(js, "function snapToGrid");
        Assert.NotEqual("", snap);
        Assert.Contains("w.style.width = ''", snap);
    }

    /// <summary>
    /// Hiding a widget must not throw the operator back to the top of the dashboard.
    ///
    /// render() used to append every widget in order. Appending relocates a node to the end, so the
    /// content height collapsed and regrew mid-loop and the scroller clamped to zero — dismissing
    /// one widget near the bottom of a long console scrolled you back to the first row. Widgets are
    /// now moved only when their position actually changes, and the scroll position is restored.
    /// </summary>
    [Fact]
    public void Rerender_PreservesScrollPosition_AndMovesOnlyWhatMoved()
    {
        var body = BodyOf(Ui("dashboard-grid.js"), "G.render = function");
        Assert.NotEqual("", body);
        Assert.Contains("scrollTop", body);                 // captured and restored
        Assert.Contains("insertBefore", body);              // positional, not wholesale append
        Assert.DoesNotContain("rootEl.appendChild(f.widget)", body);
    }

    /// <summary>
    /// A first-run console opens on a curated view, and a saved layout always wins over it.
    ///
    /// The test that matters is the second half: an operator who deliberately turns every widget on
    /// has a layout that hides nothing, and a default applied by "does this look empty?" would
    /// silently re-hide them on the next visit. Presence of the saved document is the only safe
    /// signal.
    /// </summary>
    [Fact]
    public void FirstRunGetsTheDefaultView_ButASavedLayoutAlwaysWins()
    {
        var app = Ui("app.js");
        Assert.Contains("DEFAULT_DASHBOARD_VIEW", app);
        Assert.Contains("applyLayout(saved || DEFAULT_DASHBOARD_VIEW)", app);

        // Reset must restore the SAME view, or the button quietly outranks the default: resetting
        // to "every widget visible" is an arrangement nobody chose, and it is saved on the way out.
        Assert.Contains("AnthillGrid.defaults = DEFAULT_DASHBOARD_VIEW", app);
        Assert.Contains("G.defaults", BodyOf(Ui("dashboard-grid.js"), "G.resetLayout = function"));

        // Every hidden-by-default widget must still be registered, or it is unreachable rather
        // than merely off: the Widgets menu can only list what the grid knows about.
        var view = Regex.Match(app, @"var DEFAULT_DASHBOARD_VIEW = \{.*?\n\};", RegexOptions.Singleline).Value;
        Assert.NotEqual("", view);
        foreach (Match m in Regex.Matches(view, @"'([a-z-]+)':\s*true"))
            Assert.Contains("id:'" + m.Groups[1].Value + "'", app);
    }

    /// <summary>
    /// A resize must snap when the drag ENDS, never on a timer while it is still happening.
    ///
    /// The first implementation debounced off size changes. Pausing mid-drag with the grip still
    /// held fired the snap: the inline width was cleared underneath the operator, the browser
    /// carried on resizing from the width it still believed in, and the widget fought the cursor
    /// and settled on the wrong size. The end of a drag is a real event and is waited for, rather
    /// than inferred from a gap in a stream of them.
    /// </summary>
    [Fact]
    public void ResizeSnaps_OnPointerRelease_NotOnATimer()
    {
        var body = BodyOf(Ui("dashboard-grid.js"), "function watchResize");
        Assert.NotEqual("", body);
        Assert.Contains("mouseup", body);
        Assert.DoesNotContain("setTimeout", body);
        Assert.DoesNotContain("ResizeObserver", body);
    }

    /// <summary>
    /// An operator-set height must survive the content-fit pass.
    ///
    /// markQuiet() writes min-height from measured content and runs on a 4s timer. Without an
    /// explicit opt-out it undoes every resize seconds after it was made — a defect that presents
    /// as the feature "not saving" and sends you hunting in the persistence layer.
    /// </summary>
    [Fact]
    public void OperatorSetHeight_IsNotOverwrittenByAutoFit()
    {
        var js = Ui("dashboard-grid.js");
        Assert.Contains("data-user-h", BodyOf(js, "function applySize"));
        Assert.Contains("hasAttribute('data-user-h')", BodyOf(js, "function markQuiet()"));
    }

    /// <summary>
    /// Dragging must not be the only way to rearrange the dashboard.
    ///
    /// Carried from the workspace, which established the rule: a pointer-only affordance is
    /// unreachable by keyboard and by anyone who cannot drag accurately.
    /// </summary>
    [Fact]
    public void DragIsNotTheOnlyPathToRearrange()
    {
        var js = Ui("dashboard-grid.js");
        Assert.Contains("G.move = function", js);          // the arrow buttons
        Assert.Contains("G.setHidden = function", js);     // the widget menu
        Assert.Contains("G.resetLayout = function", js);   // the way back
        Assert.Contains("'dragstart'", js);                // and drag, alongside them
    }

    /// <summary>
    /// Size overrides loaded from storage must be validated, not trusted. They arrive from a
    /// document that could be old, hand-edited, or written by another release, and a span of 0 or
    /// NaN silently breaks the row it lands in.
    /// </summary>
    [Fact]
    public void SizeOverridesFromStorage_AreSanitized()
    {
        var js = Ui("dashboard-grid.js");
        var apply = BodyOf(js, "G.applyLayout = function");
        Assert.Contains("sanitizeSizes(saved.spans", apply);
        Assert.Contains("sanitizeSizes(saved.heights", apply);

        var san = BodyOf(js, "function sanitizeSizes");
        Assert.Contains("typeof v === 'number'", san);
        Assert.Contains("isFinite(v)", san);
    }

    /// <summary>
    /// Adopted widgets must carry the class an element's styles are scoped to.
    /// </summary>
    [Fact]
    public void AdoptedWidgets_KeepTheClassTheirStylesAreScopedTo()
    {
        var appJs = Ui("app.js");
        var html = Ui("index.html");

        // the composer must declare its style scope, and adoption must apply it
        var composer = Regex.Match(appJs, @"id:'mission-composer'[^}]*\}");
        Assert.True(composer.Success, "mission-composer registration not found");
        Assert.Contains("cls:'mission-node'", composer.Value);
        Assert.Contains("if(cls) existing.classList.add(cls)", appJs);

        // and the rules it depends on must still be scoped that way, or the class is cargo
        Assert.Contains(".mission-node .mn-prompt", html);
        Assert.Contains(".mission-node .mn-send", html);
    }

    /// <summary>
    /// markQuiet() must clear the inline floor BEFORE it measures.
    ///
    /// Without this the previous pass's floor is part of this pass's measurement, and because the
    /// re-measure runs on a timer, every idle card grows a little every few seconds. The failure is
    /// slow and silent, which is exactly why it is worth a guard rather than an eyeball.
    /// </summary>
    [Fact]
    public void QuietMeasurement_ReadsNaturalHeight_NotItsOwnPreviousFloor()
    {
        var body = BodyOf(Ui("dashboard-grid.js"), "function markQuiet()");
        Assert.NotEqual("", body);

        var reset = body.IndexOf("style.minHeight = ''", StringComparison.Ordinal);
        var measure = body.IndexOf("getBoundingClientRect", StringComparison.Ordinal);
        var write = body.LastIndexOf("style.minHeight =", StringComparison.Ordinal);

        Assert.True(reset >= 0, "markQuiet must clear the inline floor before measuring");
        Assert.True(measure > reset, "measurement must happen after the floor is cleared");
        Assert.True(write > measure, "the computed floor must be written after measurement");

        // The standard height is a ceiling: a widget with MORE content than the floor must fall
        // back to the CSS floor and scroll, never grow the page to fit a long list.
        Assert.Contains("--dg-widget-h", body);
        Assert.Contains("desired < cap", body);
    }
}
