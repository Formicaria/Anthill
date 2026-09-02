using System.Text.RegularExpressions;
using Anthill.Api;
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
    /// A rejected dispatch must surface the error, not go quiet. v2.16.0 swallowed the failure
    /// entirely. v0.3.8.42 (§3): the typed-directive composers are retired — dispatch's one
    /// remaining production caller is Re-run — but the failure-surfacing rules survive it: the
    /// error is shown, the double-submit guard holds, and the caller learns whether it worked.
    /// </summary>
    [Fact]
    public void Dispatch_SurfacesFailures()
    {
        var js = Ui("app.js");

        var submit = BodyOf(js, "async function submitMissionGoal(goal)");
        Assert.Contains("msShowDispatchError", submit);
        Assert.Contains("msDispatchInFlight", submit);            // double-submit guard
        Assert.Contains("return true;", submit);
        Assert.Contains("return false;", submit);

        // The one production caller, and the surface its errors land on.
        Assert.Contains("submitMissionGoal(goal)", BodyOf(js, "async function reRunJob(id)"));
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

    /// <summary>
    /// The node --test suite must actually run in CI, or it proves nothing.
    ///
    /// v0.3.8.52 strengthened this from a substring match on one filename to a check that the whole
    /// DIRECTORY is run, because the original form is what let the gap happen: it pinned
    /// `node --test tests/ui/mission-thread.test.js`, that exact line was what CI ran, and
    /// navigation.test.js — added in v0.3.8.48 and passing — never executed anywhere. The guard was
    /// satisfied while the thing it exists to guarantee was false for half the suite.
    ///
    /// Asserting the directory form makes the property hold for every file, including ones not
    /// written yet, which is the only version of this check that stays true.
    /// </summary>
    [Fact]
    public void EveryUiTestFile_RunsInCiAndInBothValidateScripts()
    {
        const string runsTheDirectory = "node --test tests/ui/";

        Assert.Contains(runsTheDirectory, Src(".github", "workflows", "ci.yml"));
        Assert.Contains(runsTheDirectory, Src("scripts", "validate.ps1"));
        // v0.3.8.52: validate.sh ran no UI tests at all, so a local "full validation" could pass
        // green with a red console suite. Both scripts are asserted so they cannot drift apart.
        Assert.Contains(runsTheDirectory, Src("scripts", "validate.sh"));

        var uiTests = Directory.GetFiles(Path.Combine(Root(), "tests", "ui"), "*.test.js");
        Assert.True(uiTests.Length >= 5,
            $"expected the console suite to cover more than the original two files; found {uiTests.Length}");
        // The named file the old assertion pinned still has to exist — it is the reconciler's only
        // behavioural coverage, and the directory form would happily pass if someone deleted it.
        Assert.Contains(uiTests, f => Path.GetFileName(f) == "mission-thread.test.js");
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
    /// `pollHud` writes only into widget bodies, and a hidden widget has no body — so every write
    /// there must tolerate a missing element.
    ///
    /// v3.8.34. `attnPanel` was null-guarded and `attnList` — the next line, same widget — was not.
    /// On any dashboard without Operator Attention, `attnList.innerHTML` threw
    /// "Cannot set properties of null" and took the REST of pollHud with it: the missions, changes
    /// and objectives summaries below never ran. Every poll. The live console had logged a thousand
    /// of these, and the visible symptom was only that four panels stayed empty — no broken layout,
    /// no error surface, nothing an operator could report beyond "buggy".
    ///
    /// DEFAULT_DASHBOARD_VIEW ships operator-attention hidden, which made this the DEFAULT
    /// first-run dashboard rather than an edge case, and is why a guard keyed to the default layout
    /// is worth more than one keyed to the code alone.
    /// </summary>
    [Theory]
    [InlineData("async function pollHud()")]
    // v3.8.34: pollHealth had four more of the same shape — two dots, a colour and a class — found
    // only because the autonomy status was being restyled next to them. One poller fixed and its
    // neighbour left is how this defect comes back.
    [InlineData("async function pollHealth()")]
    public void EveryWidgetBodyWrite_InAPoller_ToleratesAHiddenWidget(string signature)
    {
        var js = Ui("app.js");
        var body = BodyOf(js, signature);

        Assert.True(body.Length > 0, $"{signature} not found — this guard needs its new shape.");

        // The unguarded shape: reach into the DOM and write straight through the result.
        var chained = Regex.Matches(body, @"document\.getElementById\([^)]*\)\.(innerHTML|textContent|className|style)")
            .Select(m => m.Value).ToList();

        Assert.True(chained.Count == 0,
            signature + " writes through getElementById without a null check, so hiding the widget "
            + "that owns this element throws and aborts the rest of the poll: " + string.Join(", ", chained));

        // And the element that actually caused it: assigned to a local, then written unguarded.
        var unguarded = Regex.Matches(body, @"(?<!if\s*\()\b(\w+)\.innerHTML\s*=")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Where(v => Regex.IsMatch(body, @"\b(?:const|let|var)\s+" + Regex.Escape(v) + @"\s*=\s*document\.getElementById")
                     && !Regex.IsMatch(body, @"if\s*\(\s*" + Regex.Escape(v) + @"\s*\)"))
            .ToList();

        Assert.True(unguarded.Count == 0,
            "these pollHud locals come from getElementById and are written without a null check: "
            + string.Join(", ", unguarded));
    }

    /// <summary>
    /// `api()` takes its method POSITIONALLY, and no caller may pass an options object instead.
    ///
    /// v3.8.34. `hlAutoToggle` and `hlAutoEvaluate` called
    /// `window.api(path, {method:'POST'})` — the fetch-style shape, not this console's. The second
    /// parameter is `method`, so it received an object, `fetch` stringified it to
    /// "[object Object]", and the request threw on an invalid method token before leaving the
    /// browser. `api` catches that and RETURNS `{success:false}` rather than throwing, so the
    /// `catch(e){}` wrapped around each call never ran either; the result was discarded and the
    /// panel re-rendered unchanged. Enabling an automation rule did nothing, and said nothing.
    ///
    /// Two console call sites out of roughly ninety had this shape and both were dead. The failure
    /// is invisible at runtime — no console error, no failed request in the network panel, just a
    /// control that does not work — so it has to be caught in the source.
    /// </summary>
    [Fact]
    public void NoConsoleCall_PassesAnOptionsObjectWhereApiExpectsAMethod()
    {
        var js = Ui("app.js");

        // Guard the reader: if `api` stops taking the method positionally this test is describing
        // a function that no longer exists, and must fail rather than pass vacuously.
        // (v0.3.8.46: the signature grew a timeout — the method is still positional, which is
        // what this test actually cares about.)
        Assert.Contains("async function api(path, method='GET', body=null, timeoutMs=10000)", js, StringComparison.Ordinal);

        // Matched on the object's `method:` key rather than on argument POSITION. Finding the
        // second argument means balancing parentheses — the offending path was
        // `'…/'+encodeURIComponent(id)+'/'+(on?'enable':'disable')`, which a positional pattern
        // cannot span, and a guard that caught one of the two instances would have been worse than
        // none. An options object handed to this `api` is wrong wherever it appears, so the key is
        // the reliable signal.
        var offenders = Regex.Matches(js, @"\bapi\([^;\n]*\{\s*method\s*:")
            .Select(m => m.Value.Trim())
            .ToList();

        Assert.True(offenders.Count == 0,
            "These calls pass an object where api() expects a method string, so the request is sent "
            + "with an invalid method and fails silently: " + string.Join(" | ", offenders));
    }

    /// <summary>
    /// Collapsing the sidebar because the WINDOW is narrow must not rewrite what the operator
    /// chose.
    ///
    /// v3.8.34. `#nav-rail` was a flat `width:var(--nav-w)` — 240px at every size — and none of the
    /// console's seven narrow breakpoints touched it, so at 760px the rail held about a third of
    /// the viewport. `applyNarrowNav` reuses the existing `.nav-collapsed` styling rather than
    /// restating those ten selectors behind a media query, because two copies of one appearance is
    /// how they come to disagree.
    ///
    /// The hazard that reuse introduces is this one: the manual toggle persists to localStorage, so
    /// an automatic collapse that took the same path would let dragging a window narrow overwrite a
    /// preference the operator set deliberately — and it would only show up later, on a wide screen,
    /// as a sidebar that "randomly" remembered the wrong thing. Narrow forces the class; only the
    /// button writes the preference.
    /// </summary>
    [Fact]
    public void TheAutomaticNavCollapse_NeverWritesTheOperatorsPreference()
    {
        var body = BodyOf(Ui("app.js"), "function applyNarrowNav()");

        Assert.True(body.Length > 0,
            "applyNarrowNav is missing — the sidebar has no automatic narrow-viewport behaviour, "
            + "which is the state this test was written against.");

        Assert.DoesNotContain("setItem", body);

        // It must still READ the preference, or returning to a wide viewport would forget a
        // deliberate collapse instead of restoring it.
        Assert.Contains("getItem", body);
    }

    /// <summary>
    /// Every control `buildNav` gives an ARIA role must also be given a name.
    ///
    /// v3.8.34 caught six unnamed domain-head buttons. v0.3.8.49 flattened the sidebar to a single
    /// nav-item builder (no domain heads, no nav-children — sub-sections are in-page tabs now), so
    /// there is ONE roled control here rather than three; the rule it enforces is unchanged: a role
    /// implies an accessible name, because name-from-content cannot be relied on when a label sits
    /// between an icon span and other markup.
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
        Assert.True(roled.Count >= 1,
            $"expected at least one roled nav control in buildNav, found {roled.Count} — has the "
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
    /// The original defect: the Mission Composer lived only on the classic overview grid, which
    /// the topology workspace hid, so from v2.15.0 the plan-review step had no reachable control.
    /// v0.3.8.42 (§3) retired the composer deliberately — Chat is the one mission entry — so the
    /// property this test defends is restated once more: the widget that replaced the composer
    /// must REACH the canonical entry, from the same dashboard slot. A card that says "start in
    /// Chat" and goes nowhere would be the v3.1.1 defect wearing the new design.
    /// </summary>
    [Fact]
    public void EveryWorkWorkflowControl_IsReachableOnTheDashboard()
    {
        var html = Ui("index.html");
        var app = Ui("app.js");

        // The widget is still registered, so it renders on the default dashboard.
        Assert.Contains("body:'ov-composer-body'", app.Replace(" ", ""));
        Assert.Contains("id=\"ov-composer-body\"", html);

        var open = html.IndexOf("id=\"ov-composer-body\"", StringComparison.Ordinal);
        var close = html.IndexOf("/ov-composer-body", open, StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, "ov-composer-body wrapper not found or unterminated.");
        var body = html[open..close];

        // The card reaches Chat, and no composer remnant survives in it.
        Assert.Contains("go('/chat')", body);
        Assert.DoesNotContain("ov-mission-input", body);
        Assert.DoesNotContain("ov-preview-btn", body);
        Assert.DoesNotContain("ov-modes", body);
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

        // v0.3.8.56 (the cell grid): the CSS floor is GONE — a widget's height is its cell span,
        // sized by the fixed row track, so a min-height would fight the track it lives in. The
        // track itself is what keeps rows from collapsing now.
        Assert.Contains("grid-auto-rows: var(--dg-cell-h)", css);
        Assert.DoesNotContain("min-height: var(--dg-widget-h)", css);
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
        // half the dashboard in one and a quarter of it in the other. v0.3.8.56 (cell grid): the
        // fraction quantizes to SIXTHS — whole cells of the 6-cell row — and is re-resolved
        // against the current column count on every render.
        var setter = BodyOf(js, "G.setSpanFraction = function");
        Assert.NotEqual("", setter);
        Assert.Contains("/ 6", setter);
        Assert.Contains("fraction * 6", setter);

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
        var reset = BodyOf(Ui("dashboard-grid.js"), "G.resetLayout = function");
        Assert.Contains("G.defaults", reset);
        // v0.3.8.56: the shipped view is a curated PLACEMENT (captured from the operators' own
        // board), so the default carries positions and reset restores them — a reset that forgets
        // pos produces an auto-packed arrangement nobody chose.
        Assert.Contains("d.pos", reset);

        // Every hidden-by-default widget must still be registered, or it is unreachable rather
        // than merely off: the Widgets menu can only list what the grid knows about.
        var view = Regex.Match(app, @"var DEFAULT_DASHBOARD_VIEW = \{.*?\n\};", RegexOptions.Singleline).Value;
        Assert.NotEqual("", view);
        foreach (Match m in Regex.Matches(view, @"'([a-z-]+)':\s*true"))
            Assert.Contains("id:'" + m.Groups[1].Value + "'", app);
        Assert.Contains("pos:", view);
        // Every placed widget must be a registered one, or the default seats a ghost.
        var posBlock = Regex.Match(view, @"pos: \{.*?\n  \}", RegexOptions.Singleline).Value;
        Assert.NotEqual("", posBlock);
        foreach (Match m in Regex.Matches(posBlock, @"'([a-z-]+)':"))
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

        // and the rules it depends on must still be scoped that way, or the class is cargo.
        // v0.3.8.42 (§3): .mn-send left this scope — the composer retired and the rule now styles
        // the chat composer's button globally — so .mn-prompt is the remaining scoped dependency.
        Assert.Contains(".mission-node .mn-prompt", html);
    }

    /// <summary>
    /// markQuiet() must clear any legacy inline floor BEFORE it measures, and its answer is CELLS.
    ///
    /// v0.3.8.56 (the cell grid): the pass still measures content, but what it writes is a
    /// grid-row span in whole cells — enough to hold the content, capped by the auto ceiling so a
    /// long list scrolls instead of growing the page. Measuring with a floor still applied would
    /// make the previous pass part of this pass's answer — the slow silent growth the original
    /// guard was written for, and the ordering it defends is unchanged.
    /// </summary>
    [Fact]
    public void QuietMeasurement_ReadsNaturalHeight_NotItsOwnPreviousFloor()
    {
        var body = BodyOf(Ui("dashboard-grid.js"), "function markQuiet()");
        Assert.NotEqual("", body);

        var reset = body.IndexOf("style.minHeight = ''", StringComparison.Ordinal);
        var measure = body.IndexOf("getBoundingClientRect", StringComparison.Ordinal);
        // v0.3.8.56 (free placement): the measured answer lands in _hCells and placeAll writes
        // every widget's grid position from the rects — the write moved, the ordering did not.
        var write = body.LastIndexOf("_hCells[", StringComparison.Ordinal);
        var place = body.LastIndexOf("placeAll(", StringComparison.Ordinal);

        Assert.True(reset >= 0, "markQuiet must clear the inline floor before measuring");
        Assert.True(measure > reset, "measurement must happen after the floor is cleared");
        Assert.True(write > measure, "the measured cells must be recorded after measurement");
        Assert.True(place > write, "placement must follow the measurements it consumes");

        // The auto ceiling: content past it scrolls, never grows the page.
        Assert.Contains("AUTO_MAX_CELLS", body);
        var js = Ui("dashboard-grid.js");
        Assert.Contains("--dg-cell-h", js);
    }

    /// <summary>
    /// v0.3.8.42: the console's terminal-status set is keyed to the registry's, derived rather
    /// than restated — the same rule <c>EveryEndingStatus_IsRecognisedAsTerminal</c> enforces
    /// server-side, asserted across the boundary.
    ///
    /// Three console call sites each carried their own subset. The active-job poller omitted
    /// 'cancelled' and 'timed_out', so cancelling a run from the jobs list left the poller running
    /// forever and the composer locked — the exact defect v3.8.34 fixed for 'escalated', one
    /// status over. The jobs list omitted 'timed_out', leaving a timed-out run with no View Result
    /// and no Re-run. The set now has a single home (JOB_TERMINAL_STATUSES) and this test fails if
    /// a call site re-inlines its own copy or the registry ends a job on a status the console
    /// does not recognise.
    /// </summary>
    [Fact]
    public void ConsoleTerminalStatuses_MatchTheRegistry()
    {
        var js = Ui("app.js");
        var m = Regex.Match(js, @"JOB_TERMINAL_STATUSES\s*=\s*\[([^\]]*)\]");
        Assert.True(m.Success, "JOB_TERMINAL_STATUSES not found in app.js");
        var jsSet = Regex.Matches(m.Groups[1].Value, @"'([^']+)'").Select(x => x.Groups[1].Value).ToHashSet();

        // Every status the registry can end a job on, derived from the real mapper.
        var vocabulary = new string?[]
        {
            "completed", "completed_verified", "completed_unverified", "partial", "failed",
            "failed_retryable", "failed_permanent", "timed_out", "cancelled", "escalated",
            "compensating", "compensated", "rollback_failed", null, "",
        };
        var ending = vocabulary.Select(v => ApiJobRegistry.StatusFromOutcome(v, null))
            .Concat(vocabulary.Select(v => ApiJobRegistry.StatusFromOutcome("completed", v)))
            .Where(ApiJobRegistry.IsTerminalStatus)
            .Distinct().ToList();
        Assert.True(ending.Count >= 4, "status mapping looks broken");

        var missing = ending.Where(s => !jsSet.Contains(s!)).ToList();
        Assert.True(missing.Count == 0,
            "the registry ends jobs on statuses the console does not treat as terminal, so the "
            + "composer stays locked and the run gets no View Result: " + string.Join(", ", missing));

        // The call sites consume the shared set — a re-inlined copy is how this drifted twice.
        Assert.Contains("jobIsTerminal(j.status)", BodyOf(js, "async function pollActiveJob()"));
        Assert.Contains("jobIsTerminal(j.status)", BodyOf(js, "function renderJobList(jobs, listId, badgeId, limit)"));
        Assert.Contains("jobIsTerminal(j.status)", BodyOf(js, "async function pollJobs()"));
    }

    /// <summary>
    /// v0.3.8.42 (§3): Chat is the ONE mission entry. The three composers that competed with it —
    /// the colony page's mission bar, the Missions console box, the dashboard's Mission Command —
    /// are retired, and every former entry point now REACHES Chat instead of quietly disappearing
    /// (the v3.1.1 lesson: a control removed without a path left behind is a dead end, not a
    /// consolidation). Stopping work follows the entry: the chat header carries a Stop wired to
    /// the conversation cancel, shown exactly while the colony is working, and the jobs list keeps
    /// its durable per-run Cancel.
    /// </summary>
    [Fact]
    public void Chat_IsTheOneMissionEntry_AndCarriesTheStop()
    {
        var js = Ui("app.js");
        var html = Ui("index.html");

        // The retired composers are GONE — inputs, send buttons and their wiring.
        foreach (var relic in new[] { "id=\"mission-input\"", "id=\"send-btn\"",
                 "id=\"ms-mission-input\"", "id=\"ms-send-btn\"", "id=\"ov-mission-input\"", "id=\"ov-send-btn\"" })
            Assert.DoesNotContain(relic, html);
        Assert.DoesNotContain("function dispatchMission", js);
        Assert.DoesNotContain("setComposerRunState", js);

        // Every former entry surface points at the canonical one.
        Assert.Equal(3, Regex.Matches(html, "Start a mission in Chat").Count);
        Assert.Contains("data-onclick=\"go('/chat')\"", html);

        // The chat Stop: present in the header, wired to the conversation cancel, and shown only
        // while the colony is actually working — a visible Stop over an idle conversation would
        // claim there is something to stop.
        Assert.Contains("id=\"chat-stop\"", html);
        Assert.Contains("await convCancel(chatActiveId)", js);
        // v0.3.8.42: `doing` became a truthful vocabulary — Stop exists exactly while something
        // stoppable is running; "unanswered" is a failure state, not work.
        Assert.Contains("stopBtn.hidden = !(d.doing||'').startsWith('running mission')", js);
        Assert.Contains("/conversations/'+id+'/cancel'", BodyOf(js, "async function convCancel(id)"));

        // Found by driving the real page: Doing() answers "cancelled" as PROSE, so a truthiness
        // check rendered a stopped conversation as "Working…" with a live Stop, forever, and
        // masked refusal summaries. The detail endpoint now carries the flag the list always had,
        // and the console consumes the field rather than string-matching the prose.
        Assert.Contains("[\"cancelled\"] = state.Cancelled", ApiHostSource.All());
        Assert.Contains("d.cancelled ? 'Stopped", js);
        // The refusal summary outlives the refresh that used to overwrite it.
        var send = BodyOf(js, "async function chatSend(mode)");
        Assert.Contains("await chatOpen(chatActiveId);", send);
        Assert.Contains("if(note) chatSetState(note);", send);

        // The jobs list keeps the durable per-run Cancel.
        Assert.Contains("cancelJob", BodyOf(js, "function renderJobList(jobs, listId, badgeId, limit)"));

        // v0.3.8.51 (field report): the ⚒ "Do the work" button is RETIRED — one send path. A
        // second send button was a magic word with a tooltip.
        //
        // v0.3.8.58: and the one remaining path IS the mission path. The escalation marker that
        // made this true is gone with the lane it escalated FROM — the colony no longer decides,
        // per turn, whether the operator's message deserves the pipeline. What must hold: the
        // button is gone, the gates affordance exists beside the policy selector, and approvals
        // still travel mode:'mission' through the same turn endpoint.
        Assert.DoesNotContain("id=\"chat-work\"", html);
        Assert.DoesNotContain("chatSend('mission')", js);
        // Operator correction: the policy SELECTOR is the gates, so it wears the label — a
        // sibling button also named Gates was one gates too many.
        Assert.Contains("id=\"chat-gates-label\"", html);
        Assert.DoesNotContain("id=\"chat-gates\">", html);
        Assert.Contains("mode:'mission'", js);   // convApprove's re-send keeps the gated path
    }

    /// <summary>
    /// v0.3.8.45: Chat + Colony is a SPLIT page. The v0.3.8.43 frosted-overlay presentation was
    /// corrected from the field twice — the desktop tester could not see the colony (it drew,
    /// centred, exactly under the opaque panel), and the operator's ruling was explicit: "should
    /// be a split page not the chat box on top of the colony." The properties pinned here are the
    /// ones that made every earlier shape wrong somewhere: a second canvas, a 0×0 mount, a
    /// stowaway composer, a silent no-op below 900px, and now an overlay occluding the map.
    /// One canvas element, one mount mechanism (re-parenting), two working halves side by side.
    /// </summary>
    [Fact]
    public void ChatColony_IsALayer_ReusingTheOneCanvas()
    {
        var html = Ui("index.html");
        var js = Ui("app.js");

        // The layer and its controls exist; the retired side panel does not.
        Assert.Contains("id=\"chat-colony-layer\"", html);
        Assert.Contains("id=\"chat-colony-mount\"", html);
        Assert.Contains("id=\"chat-colony-full\"", html);
        Assert.DoesNotContain("id=\"chat-side\"", html);
        Assert.DoesNotContain("chat-side-hd", html);

        // ONE canvas host element in the document, ever. The layer must be fed by re-parenting
        // the canonical node, not by a Chat-specific renderer.
        Assert.Single(Regex.Matches(html, "id=\"colony-canvas-area\""));
        var toggle = BodyOf(js, "function chatToggleColony(open)");
        Assert.Contains("topologyMountTo(chatColonyOpen?'chat':'home')", toggle);
        Assert.DoesNotContain("createElement('canvas')", toggle);

        // The toggle is a real toggle: pressed state exposed, both directions through one function.
        Assert.Contains("aria-pressed", Regex.Match(html, "<button[^>]*id=\"chat-colony-toggle\"[^>]*>").Value);
        Assert.Contains("setAttribute('aria-pressed'", toggle);

        // Escape closes the layer, but never out from under a modal that owns the key.
        Assert.Contains(".ui-modal-ov", js[js.IndexOf("e.key!=='Escape'||!chatColonyOpen", StringComparison.Ordinal)..][..600]);

        // Leaving for the full Colony page hands the canvas home BEFORE navigating, and re-entering
        // Chat reclaims it — the two directions of the same hand-off.
        Assert.Contains("chatToggleColony(false); go('/colony/topology');", js);
        Assert.Contains("topologyMountTo('chat')", BodyOf(js, "PAGE_ENTER['chat']=()=>"));

        // Topology failure stays a topology-sized problem: the layer reports it, chat survives.
        Assert.Contains("The colony view could not load.", toggle);

        // Two defects found by driving the real page, pinned so they cannot return:
        // #colony-canvas-area sizes itself with flex:1, so its host must be a flex column — as a
        // plain block the canvas measured 0×0 and the pane was an empty void…
        Assert.Matches(new Regex(@"\.chat-colony-mount\{[^}]*display:flex;flex-direction:column;"), html);
        // …and the colony page's mission bar lives INSIDE the canvas area; unhidden it becomes a
        // second composer beside the conversation's. Chat is the canonical entry surface.
        Assert.Contains(".chat-colony-mount #mission-bar{display:none;}", html);

        // The split itself: the colony pane is IN-FLOW beside the conversation — not an absolute
        // overlay, not frosted glass over the map. Both halves are whole; neither occludes.
        Assert.Contains(".chat-colony-layer{display:flex;flex-direction:column;min-height:0;min-width:0;flex:1 1 52%;", html);
        Assert.Contains("#page-chat.colony-open .chat-main{flex:1 1 48%;order:1;min-width:380px;}", html);
        Assert.DoesNotContain("position:absolute;inset:0;z-index:0;display:flex;flex-direction:column", html);
        Assert.DoesNotContain("backdrop-filter:blur(8px)", html);

        // Panning that loses the colony has an obvious way home, wired to the canonical reset.
        Assert.Contains("id=\"chat-colony-fit\"", html);
        Assert.Contains("colonyResetView", BodyOf(js, "document.getElementById('chat-colony-fit')?.addEventListener('click', ()=>"));

        // Reduced motion is honored at the render loop: idle + reduced → 4fps; real work → full
        // rate, because at that point the motion IS the information.
        Assert.Contains("prefers-reduced-motion", js);
        Assert.Contains("REDUCED_MOTION && !colonyRunning", BodyOf(js, "function loop(ts)"));

        // Mobile is a clean switch, not a miniature unusable graph under the thread.
        Assert.Contains("#page-chat.colony-open .chat-main{display:none;}", html);
    }

    /// <summary>
    /// v0.3.8.45 — the field reports behind the split, pinned so the overlay cannot return.
    /// Desktop tester: "its like the colony behind the chat but you cant like see the colony" —
    /// the map WAS drawing, centred exactly under the opaque centred panel. Operator: "should be
    /// a split page not the chat box on top of the colony." A presentation where the conversation
    /// floats over the map is a design this product has now rejected twice from live use.
    /// </summary>
    [Fact]
    public void ChatColony_ColonyIsVisible_NotCentredBehindTheGlass()
    {
        var html = Ui("index.html");
        var js = Ui("app.js");

        // No frosted floating panel, no translucent conversation over the map.
        Assert.DoesNotContain("backdrop-filter:blur(8px)", html);
        Assert.DoesNotContain("color-mix(in srgb, var(--panel) 92%, transparent)", html);
        // The camera centres the canvas it owns — no occlusion means no offset arithmetic.
        var resize = BodyOf(js, "function resize()");
        Assert.Contains("cx=W/2; cy=H/2;", resize);
        // The pane is a sibling in the page's own flow: the conversation orders first, the
        // colony second, and hiding the pane returns the row to a plain full-width chat.
        Assert.Contains("order:2;border-left:1px solid var(--border);", html);
        Assert.Contains(".chat-colony-layer[hidden]{display:none;}", html);
    }

    /// <summary>
    /// v0.3.8.46: search, pins and export are honest features, not decoration. The search box
    /// queries the SERVER (titles and transcript content — GET /conversations?q=), the pin is a
    /// stored fact that survives restart (POST pin/unpin against the record), and export downloads
    /// what the store holds via the authenticated endpoint. Each pin here is a claim the backend
    /// can prove.
    /// </summary>
    [Fact]
    public void ChatRail_SearchPinsAndExport_AreBackedByTheStore()
    {
        var html = Ui("index.html");
        var js = Ui("app.js");

        // The three surfaces exist.
        Assert.Contains("id=\"chat-search\"", html);
        Assert.Contains("id=\"chat-export\"", html);
        Assert.Contains("conv-pin", js);

        // Search goes to the server, not a client-side filter over whatever happens to be loaded —
        // transcript content lives in the store and only the store can search it.
        var load = BodyOf(js, "async function loadChat()");
        Assert.Contains("'?q='+encodeURIComponent(chatSearchQuery)", load);
        // A search result is a candidate, not a selection: no auto-open while searching.
        Assert.Contains("!chatSearchQuery) chatOpen(", load);

        // Pinning hits the two explicit endpoints and cannot ALSO open the row it sits on.
        Assert.Contains("(was?'/unpin':'/pin')", load);
        Assert.Contains("e.stopPropagation();", load);

        // The debounce means typing is not a request storm; Escape clears back to the full rail.
        Assert.Contains("chatSearchTimer=setTimeout", js);

        // Export authenticates (fetch with the bearer header, not a bare link) and downloads the
        // server's file rather than serialising whatever the DOM currently shows.
        var export = js[js.IndexOf("chat-export", StringComparison.Ordinal)..][..900];
        Assert.Contains("/export", export);
        Assert.Contains("'Authorization':'Bearer '+TOKEN", export);
        Assert.Contains("a.download", export);
    }

    /// <summary>
    /// v0.3.8.46: syntax highlighting is home-grown (no framework, no CDN — the brief's rule) and
    /// STRUCTURALLY escape-first: the tokenizer's one output function passes every character
    /// through escapeHtml before wrapping, so highlighting can change how code looks and never
    /// what is allowed to render. A tokenizer failure falls back to the plain escaped text.
    /// </summary>
    [Fact]
    public void SyntaxHighlighting_IsEscapeFirst_AndSelfContained()
    {
        var js = Ui("app.js");

        // The fenced-code path renders through the highlighter, which receives the language tag.
        Assert.Contains("chatHighlight(code,lang)", js);
        var hl = BodyOf(js, "function chatHighlight(code, lang)");
        // The single output seam: markup is only ever wrapped AROUND escaped text.
        Assert.Contains("out+=cls?'<span class=\"'+cls+'\">'+escapeHtml(text)+'</span>':escapeHtml(text);", hl);
        // Failure is the old behaviour, not a broken bubble.
        Assert.Contains("catch(e){ return escapeHtml(code); }", hl);
        // No third-party highlighter smuggled in later.
        Assert.DoesNotContain("highlight.js", js);
        Assert.DoesNotContain("hljs", js);
        Assert.DoesNotContain("prismjs", js, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// v0.3.8.46, found live: approving a MID-MISSION gate re-sends the message, and the re-send
    /// meets the start_mission gate first — which ate the answer. The click approved nothing;
    /// both actions sat waiting. The re-send restates the start_mission decision that is already
    /// on record (a secondary gate can only exist inside an approved mission), so the approval
    /// the operator clicked actually reaches its gate.
    /// </summary>
    [Fact]
    public void ApprovingASecondaryGate_CarriesTheRecordedMissionApproval()
    {
        var approve = BodyOf(Ui("app.js"), "async function convApprove(id, action)");

        Assert.Contains("if(action!=='start_mission') answers['start_mission']='approve';", approve);
    }

    /// <summary>
    /// v0.3.8.48 (defect 20): corrupted replacement glyphs are gone and stay gone. These exact
    /// strings shipped with a literal '?' where an arrow or check once lived — mojibake from a
    /// lost encoding pass — and every one of them read as a broken product.
    /// </summary>
    [Fact]
    public void NoCorruptedGlyphs_InVisibleText()
    {
        var ui = Ui("index.html") + Ui("app.js");

        foreach (var bad in new[] { "Settings ? Colony", "Configuration ? Security",
            "Configuration ? Shell", "Saved ?'", "'Verified ?'", "Maintenance ? Flush" })
            Assert.DoesNotContain(bad, ui);
    }

    /// <summary>
    /// v0.3.8.48: approvals live ON the conversation. The selector carries the three exact
    /// user-facing labels mapped to the three policies the backend has always had; Skip-all
    /// requires a spoken confirmation and a refusal snaps the selector back; and proposed
    /// changes render as cards IN the thread with approve-and-apply running both audited
    /// transitions in sequence. The separate Changes page is no longer the only door.
    /// </summary>
    [Fact]
    public void Approvals_LiveOnTheConversation()
    {
        var html = Ui("index.html");
        var js = Ui("app.js");

        Assert.Contains("id=\"chat-policy\"", html);
        Assert.Contains(">Manual approval<", html);
        Assert.Contains(">Automatically approve<", html);
        Assert.Contains(">Skip all approvals<", html);

        // Bypass is confirmed in words, and the words say the honest thing.
        Assert.Contains("this skips prompts, not security", js);
        Assert.Contains("sel.value=was; return;", js);

        // The policy endpoint is called attributed; the selector reflects the EFFECTIVE policy.
        Assert.Contains("'/policy','POST'", js);

        // Inline change cards: in the thread, no navigation. v0.3.8.51: the card's action is ONE
        // JSON endpoint doing approve-then-apply — the old two-call flow parsed text/plain
        // responses as JSON and reported "Approval failed" over approvals that had landed.
        Assert.Contains("chatRenderPatches", js);
        Assert.Contains("data-p-approve", js);
        Assert.Contains("'/patches/'+encodeURIComponent(pid)+'/approve-apply'", js);
        Assert.DoesNotContain("'/apply/'+encodeURIComponent(approvalId)", js);
        Assert.Contains("'/revert/'+encodeURIComponent(pid)", js);
    }

    /// <summary>
    /// v0.3.8.51 third round — the files pane's editor DOCKS BELOW the working tree: click a file
    /// and it opens underneath while the tree stays visible and clickable (the operator asked for
    /// exactly this after the replace-the-tree version). The editor carries its own Changes toggle
    /// (recent edits to THAT file: uncommitted git diff + colony patch history), stays an editable
    /// textarea with Save, and the pane states what the directory IS — git repo with branch and
    /// dirty count, or plain folder — with an operator Commit button only a repo shows.
    /// </summary>
    [Fact]
    public void FilesPane_EditorDocksBelowTheTree_AndGitIsStated()
    {
        var html = Ui("index.html");
        var js = Ui("app.js");

        // The docked editor: its own bar ids, a border seam, and NO tree-hiding on open.
        Assert.Contains("id=\"chat-editor-changes\"", html);
        Assert.Contains("id=\"chat-editor-diff\"", html);
        Assert.Contains("id=\"chat-editor-text\"", html);
        Assert.DoesNotContain("body.style.display='none'", js);   // the tree survives a file click

        // Git awareness, stated in the bar; Commit is the operator's half of the commit story.
        Assert.Contains("id=\"chat-files-repo\"", html);
        Assert.Contains("id=\"chat-files-commit\"", html);
        Assert.Contains("chatFilesRepoLoad", js);
        Assert.Contains("/repo/diff?path=", js);
        Assert.Contains("'/repo/commit'", js);
        Assert.Contains("plain folder", js);

        // Per-file recent edits come from the same patch surfaces the cards use.
        Assert.Contains("chat-editor-changes", js);
        Assert.Contains("cfGitDiffHtml", js);

        // v0.3.8.52 field round: the whole row selects and stays lit; git letters fill the dead
        // space; the bar buttons keep to one line (the fullwidth ＋ wrapped them into two); the
        // editor gained a highlighted layer and chat prose gained inline code — all escape-first.
        Assert.Contains(">+ File<", html);
        Assert.Contains(">+ Folder<", html);
        Assert.DoesNotContain("＋ File", html);
        Assert.Contains("id=\"chat-editor-hl\"", html);
        Assert.Contains("id=\"chat-editor-wrap\"", html);
        Assert.Contains("chatFilesSelPath", js);
        Assert.Contains("chatFilesApplyGitMarks", js);
        Assert.Contains("cf-git", js);
        Assert.Contains("chat-inline", js);
        Assert.Contains("chatEditorHl", js);

        // v0.3.8.52 — the commit train: per-file history on demand from a GitHub-style branch
        // selector (view-only; nothing is checked out), each stop's diff one click deeper.
        Assert.Contains("id=\"chat-files-branch\"", html);
        Assert.Contains("cf-hist", js);
        Assert.Contains("chatFilesTrain", js);
        Assert.Contains("/repo/log?path=", js);
        Assert.Contains("/repo/branches", js);
        Assert.Contains("/repo/show?hash=", js);
        Assert.Contains("ev.stopPropagation()", js);   // the clock must not also open the editor
    }

    /// <summary>
    /// v0.3.8.42 (§5 of docs/UI-CONTRACT-AUDIT.md): surfaces claim only what the backend provides.
    /// "Projects" implied project management over what is really GET /workspaces, so the label was
    /// corrected to "Mission Workspaces". v0.3.8.47 BUILT the project concept — a projects table,
    /// per-conversation creation, purpose-as-context, the CRUD endpoints — so the label "Projects"
    /// is now the truthful one, and the checkout report keeps its own honest heading below.
    /// "Scheduled" presented the Director's objectives as a general scheduler. Quick actions
    /// "Patch Colony" and "Run Diagnostic" were navigation dressed as mutations.
    /// </summary>
    [Fact]
    public void SurfacesClaimOnlyWhatTheBackendProvides()
    {
        var js = Ui("app.js");
        var html = Ui("index.html");

        Assert.Contains("label:'Projects'", js);
        // v0.3.8.55 (operator's layout rule): the Projects title is its COLUMN's header now — the
        // page splits into Projects | Automation and each column heads itself in the same shape.
        Assert.Contains(">Projects</h2>", html);
        // The claim is backed: the console actually calls the project endpoints.
        Assert.Contains("'/projects'", js);
        Assert.Contains("project_id", js);
        // The checkout report is still honestly named, one level down.
        Assert.Contains("Mission workspace checkouts", html);

        // No top-level Scheduled entry; and (UI/UX pass §5) no standalone Objectives destination —
        // Objectives folded into Projects. The old /objectives bookmark resolves as an alias.
        Assert.DoesNotContain("label:'Scheduled'", js);
        Assert.DoesNotContain("route:'/objectives',page:'objboard'", js.Replace(" ", ""));
        Assert.Contains("'/objectives':'/projects'", js.Replace(" ", ""));

        // Navigation carries navigational labels.
        Assert.DoesNotContain("Patch Colony", html);
        Assert.DoesNotContain("Run Diagnostic", html);
    }

    /// <summary>
    /// v0.3.8.42 (§7): one home per concept. The Monitoring domain was a second door to five
    /// concepts that already had homes — its Activity/Events/Results moved in with Missions, its
    /// Changes tab duplicated Changes &amp; Approvals, "Autonomous Runs" opened the Director page
    /// under a second name, and the two homelab views went home to Infrastructure. Dissolving a
    /// domain must not break a bookmark: every route that lived there resolves through
    /// ROUTE_ALIAS, and both route consumers consult it.
    /// </summary>
    [Fact]
    public void MovedRoutes_StayReachable_AndConceptsHaveOneHome()
    {
        var js = Ui("app.js");

        Assert.DoesNotContain("id:'monitoring'", js);

        foreach (var old in new[]{ "/monitoring/activity", "/monitoring/activity/events",
            "/monitoring/activity/results", "/monitoring/activity/changes",
            "/monitoring/activity/runs", "/monitoring/activity/infra", "/monitoring/alerts",
            "/scheduled" })
            Assert.Contains("'" + old + "':", js);

        Assert.Contains("ROUTE_ALIAS[h]", BodyOf(js, "function router()"));
        Assert.Contains("ROUTE_ALIAS[route]", BodyOf(js, "function go(route,push)"));

        // v0.3.8.48: events lives in exactly one IA route (Settings → System); activity and
        // results left the navigation entirely — their concerns live in project History and
        // Settings → System, and a reappearance is the duplicate-door pattern starting over.
        Assert.Single(Regex.Matches(js, "page:'events'"));
        Assert.Empty(Regex.Matches(js, "page:'activity'"));
        Assert.Empty(Regex.Matches(js, "page:'results'"));
    }

    /// <summary>
    /// v0.3.8.42 (§13): chat quality-of-life, and the properties that keep it safe. The thread
    /// re-renders only when its fingerprint changes (the 4s poll must cost nothing and destroy
    /// nothing), the reading position is preserved unless the reader was already at the bottom
    /// (the v2.17.1 mission-thread lesson, applied to chat), fenced code renders through
    /// escapeHtml FIRST with only &lt;pre&gt;&lt;code&gt; structure added — no markdown engine, no
    /// new sanitisation surface — and Up-arrow recall never hijacks a non-empty draft.
    /// </summary>
    [Fact]
    public void ChatThread_RefreshesLive_WithoutDestroyingTheReader()
    {
        var js = Ui("app.js");
        var open = BodyOf(js, "async function chatOpen(id)");

        // Fingerprinted rendering: unchanged polls skip the rebuild entirely.
        Assert.Contains("print!==chatFingerprint", open);
        Assert.Contains("if(changed)", open);
        // Scroll preserved unless already following the bottom.
        Assert.Contains("nearBottom", open);
        Assert.Contains("thread.scrollTop = keepTop", open);

        // The poll runs only while the Chat page is on screen.
        Assert.Contains("page-chat')?.classList.contains('active')", js);

        // Escape-first code rendering: prose escapes directly; fenced code goes through the
        // highlighter, whose escape-first structure SyntaxHighlighting_IsEscapeFirst pins.
        var render = BodyOf(js, "function chatRenderContent(text)");
        Assert.Contains("escapeHtml(parts[i])", render);
        Assert.Contains("'<pre class=\"chat-code\"><code>'+chatHighlight(code,lang)", render);

        // Up-arrow recall only into an EMPTY composer.
        Assert.Contains("e.key==='ArrowUp' && chatLastSent && !e.target.value", js);

        // Copy exists per message and reads content from JS state, not from DOM attributes.
        Assert.Contains("chat-copy", js);
        Assert.Contains("chatTurnContents[+b.dataset.i]", js);
    }

    /// <summary>
    /// v0.3.8.44: the streamed turn, and the properties that keep it honest. Deltas render
    /// through the SAME escape-first chatRenderContent every recorded turn uses (streaming changes
    /// WHEN text appears, never what is allowed to appear); the provisional bubble yields to the
    /// recorded turn on completion, so what remains on screen is exactly what the database holds;
    /// and aborting is real — the ■ send button aborts the fetch, whose RequestAborted token the
    /// server binds into ModelCallScope, so cancellation reaches the provider.
    /// </summary>
    [Fact]
    public void StreamedTurns_RenderEscapedDeltas_AndYieldToTheRecord()
    {
        var js = Ui("app.js");

        var consume = BodyOf(js, "async function chatConsumeStream(response)");
        Assert.Contains("chatRenderContent(raw)", consume);   // escape-first, same renderer as recorded turns
        Assert.Contains("nearBottom", consume);               // reading position preserved while streaming

        var send = BodyOf(js, "async function chatSend(mode)");
        Assert.Contains("stream:true", send);
        Assert.Contains("chatStreamAbort=new AbortController()", send);
        // The provisional bubble is held as a REFERENCE (an id lookup for a dynamic node is an
        // orphan to the markup guard, and a weaker pattern besides), removed on completion, and
        // the recorded turn re-rendered — screen equals DB.
        Assert.Contains("chatStreamLiveEl?.remove(); chatStreamLiveEl=null;", send);
        Assert.Contains("chatFingerprint=''", send);

        // ■ is abort, wired at the same button; the server binds the abort into the model call.
        Assert.Contains("chatStreamAbort.abort()", js);
        Assert.Contains("ModelCallScope.Enter(ctx.RequestAborted)", ApiHostSource.All());
    }

    /// <summary>
    /// v0.3.8.42 (§9/§14): a failed role registry is a STATE the operator sees, never a fiction.
    /// buildNodes used to invent six "Legacy executable ant" roles whenever /colony/registry had
    /// not answered, and the legend padded itself from a hardcoded list — so a dead endpoint drew
    /// a healthy colony. Now: no data → core nodes only, and the legend names the failure with a
    /// retry beside it; stale data → cached roles stay visible, marked stale with when and why.
    /// </summary>
    [Fact]
    public void RegistryFailure_IsAState_NeverAFabricatedRoster()
    {
        var js = Ui("app.js");

        Assert.DoesNotContain("Legacy executable ant", js);
        Assert.DoesNotContain("CHUD_CASTES=[", js);

        // The failure is captured with its reason, in both the rejected and unreachable shapes…
        Assert.Contains("colonyRegistryProblem={message:(r&&r.message)||'registry request rejected'", js);
        Assert.Contains("colonyRegistryProblem={message:e.message||'registry unreachable'", js);
        // …rendered where the roles would be, with retry beside it, distinguishing stale from absent…
        var legend = BodyOf(js, "function renderColonyLegend()");
        Assert.Contains("Roles are STALE", legend);
        Assert.Contains("Role registry unavailable", legend);
        Assert.Contains("chud-legend-retry", legend);
        // …and retry busts the cache, or it would re-read the same failure for 30 seconds.
        Assert.Contains("apiCacheBust('/colony/registry')", BodyOf(js, "function colonyRegistryRetry()"));
    }

    /// <summary>
    /// v0.3.8.52 — the Formicaria mark is a door home, in every shape the console ships in. The
    /// link must be target=_blank (the desktop shell routes those to the operator's real browser;
    /// see ShellForm's NewWindowRequested handler and DesktopShellTests' pin of it) and noopener,
    /// because a page the colony links to must never get a handle back into the console.
    /// </summary>
    [Fact]
    public void TheMark_LinksHome_InEveryShape()
    {
        var page = Ui("index.html");
        Assert.Contains("href=\"https://formicaria.us\"", page);
        var anchor = page[page.IndexOf("class=\"nav-logo-link\"", StringComparison.Ordinal)..];
        anchor = anchor[..anchor.IndexOf('>')];
        Assert.Contains("target=\"_blank\"", anchor);
        Assert.Contains("rel=\"noopener noreferrer\"", anchor);
    }

    /// <summary>
    /// v0.3.8.52 — the LOCAL runtime card. The first agent offer must be the no-account path, and
    /// its Install button may only render where the server says an end-to-end install is real
    /// (install_supported) — a button that could only refuse is a lie in primary-button blue.
    /// v0.3.8.56: the hidden agents page folded into Tools → Integrations; the same rules hold on
    /// the cards' new home, loadIntegrations.
    /// </summary>
    [Fact]
    public void TheAgentsPage_OffersTheLocalRuntime_HonestlyPerPlatform()
    {
        var js = Ui("app.js");
        var body = BodyOf(js, "async function loadIntegrations()");
        Assert.Contains("agentsData.local", body);
        Assert.Contains("agentcli-install-local", body);
        Assert.Contains("install_supported", body);
        Assert.Contains("/agents/local/install", body);

        var api = Src("src", "Anthill.Api", "ApiHost.Agents.cs");
        Assert.Contains("/agents/local/install", api);
        Assert.Contains("LocalRuntimeInstaller.Install()", api);
        // The same operator-shell gate as an agent install: this too runs a command on the host.
        Assert.Contains("RequireAuth(ctx, \"operator_shell\")", api);
    }

    /// <summary>
    /// v0.3.8.52 (field report) — Browse for a working directory, both lanes wired end to end.
    /// The desktop shell asks its host for the REAL OS folder dialog (the pick-folder bridge);
    /// every browser shape falls back to the server-backed directory browser, whose endpoint is
    /// gated on exactly the permission that may PATCH the path it exists to find. Each half is
    /// pinned to its consumer, because a bridge without a caller and a caller without a bridge
    /// both leave a Browse button that does nothing.
    /// </summary>
    [Fact]
    public void BrowseForWorkingDirectory_HasBothLanes_WiredEndToEnd()
    {
        var js = Ui("app.js");
        Assert.Contains("cf-set-root-browse", js);

        // The native lane's message contract, both ends of the bridge.
        Assert.Contains("postMessage('pick-folder')", BodyOf(js, "function cfPickFolderNative()"));
        var shell = Src("src", "Anthill.Desktop", "ShellForm.cs");
        Assert.Contains("\"pick-folder\"", shell);
        Assert.Contains("FolderBrowserDialog", shell);
        Assert.Contains("picked-folder", shell);
        Assert.Contains("picked-folder", js);

        // The server lane: the browser panel over /fs/dirs, run_mission-gated before any listing.
        Assert.Contains("/fs/dirs", BodyOf(js, "async function cfDirBrowserOpen(startPath, onPick)"));
        var providers = Src("src", "Anthill.Api", "ApiHost.Providers.cs");
        var at = providers.IndexOf("app.MapGet(\"/fs/dirs\"", StringComparison.Ordinal);
        Assert.True(at >= 0, "/fs/dirs endpoint is gone");
        var gate = providers[at..];
        gate = gate[..gate.IndexOf("Directory.Exists", StringComparison.Ordinal)];
        Assert.Contains("RequireAuth(ctx, \"run_mission\")", gate);
    }

    /// <summary>
    /// v0.3.8.52 (second field round) — every project gets ITS OWN tree. The old fallback sent
    /// every pathless project to the ONE shared workspace root: the files pane showed every
    /// project the same directory, and the chat agent ran every project on the same branch. The
    /// rule now lives in ProjectRoots and every consumer resolves through it — the files pane and
    /// the agent's confinement (via AgentAccessScope.WorkingDirectory, which the provider must
    /// prefer over its static default). Two copies of a boundary rule is one copy that eventually
    /// disagrees.
    ///
    /// v0.3.8.55 (fourth field round) REVERSED the third round's gate: a pathless project no
    /// longer refuses work. It stands in ANTHILL's own source checkout — direct source access,
    /// primary until the operator sets a directory, which then takes over. The old fallback stays
    /// forbidden: the default is the COLONY'S OWN TREE, never the shared workspace root that made
    /// every project identical.
    ///
    /// v0.3.8.58: the CONSUMER changed and the rule did not. Chat is deleted, so the lane that
    /// stands in a project's tree is the mission, and ExecutionService now resolves through the
    /// same shared pair rather than its old inline copy — which read grants without the colony's
    /// source reach and never resolved a directory at all.
    /// </summary>
    [Fact]
    public void EveryProject_OwnsItsOwnTree_AndAPathlessOne_StandsInTheColonySource()
    {
        var roots = Src("src", "Anthill.Core", "Projects", "ProjectRoots.cs");
        Assert.Contains("\"projects\"", roots);                       // the shared parent
        Assert.Contains("{Slug(project.Name)}-{project.Id}", roots);  // unique per project

        var providers = Src("src", "Anthill.Api", "ApiHost.Providers.cs");
        Assert.Contains("suggested_path", providers);
        Assert.Contains("ProjectRoots.DefaultFor(project)", providers);
        // Fourth round: the gate is gone — no turn is refused for a missing working directory —
        // and the files pane resolves a pathless project to the colony's own source checkout.
        Assert.DoesNotContain("workdir_required", providers);
        Assert.Contains("ProjectRoots.ColonySource()", providers);
        Assert.DoesNotContain("? AnthillRuntime.AllowedWorkspaceRoot : project.Path", providers);

        // And the git story speaks only for the project's OWN tree: nested inside a larger
        // repository ⇒ plain folder with the enclosure named, commit gate included.
        Assert.Contains("sits inside the repository at", providers);
        Assert.Contains("PathsEqual", providers);

        // v0.3.8.58: the resolution still lives in ConversationRunner and its CONSUMER moved.
        // Chat is deleted, so the only lane that stands in a project tree is the mission — and
        // ExecutionService previously had a thinner inline copy with no working directory at all.
        // Left alone, deleting chat would have reproduced the v0.3.8.52 field report one door
        // down, as "every project's MISSION runs in the same tree".
        var runner = Src("src", "Anthill.Core", "Conversations", "ConversationRunner.cs");
        Assert.Contains("ProjectDirectory(SqliteMemory _memory, Conversation conversation)", runner);
        // One resolution: explicit path first, colony source as the default.
        Assert.Contains("return Projects.ProjectRoots.ColonySource();", runner);

        var execution = Src("src", "Anthill.Core", "Orchestration", "ExecutionService.cs");
        Assert.Contains("workingDirectory: workingDirectory", execution);
        Assert.Contains("ConversationRunner.ProjectDirectory(_memory, conversation)", execution);
        // And the grants come from the same shared resolution, so the mission's reach is not
        // quietly narrower than what the operator granted the conversation.
        Assert.Contains("ConversationRunner.ProjectGrantPaths(_memory, conversation)", execution);
        // The mission tree is always disposable; only the deleted chat lane stood in live files.
        Assert.Contains("confinedWorkspace: true", execution);
        Assert.DoesNotContain("confinedWorkspace: false", execution);

        var provider = Src("src", "Anthill.Modules", "Anthill.Modules.Reasoning", "AgentCliProvider.cs");
        Assert.Contains("EffectiveWorkingDirectory", provider);
        var sdk = Src("src", "Anthill.SDK", "Reasoning", "AgentAccessScope.cs");
        Assert.Contains("WorkingDirectory", sdk);

        // The console's half: no dead remedy for a gate that no longer exists, and the set-root
        // form still arrives prefilled with the suggestion for when the operator DOES choose.
        var js = Ui("app.js");
        Assert.DoesNotContain("workdir_required", js);
        Assert.Contains("suggested_path", js);
    }

    /// <summary>
    /// v0.3.8.52 (second field round) — the project page's New Conversation BUTTON creates the
    /// conversation, immediately and in that project; it used to only flip the chat page into
    /// composing mode, which read as a dead navigation. A conversation born untitled is then
    /// named by the server from the first thing said in it, the tracker says which project every
    /// row lives in, and + File / + Folder browse the project's own tree instead of prompt()ing
    /// for a path from memory.
    /// </summary>
    [Fact]
    public void ConversationsAreBornInTheirProject_AndTheTrackerSaysWhere()
    {
        var js = Ui("app.js");
        var newConv = BodyOf(js, "document.getElementById('pv-new-conv')?.addEventListener('click',async ()=>");
        Assert.Contains("api('/conversations','POST',{ project_id: projectViewId })", newConv);

        var providers = Src("src", "Anthill.Api", "ApiHost.Providers.cs");
        Assert.Contains("string.IsNullOrWhiteSpace(conversation.Title)", providers);  // first message names it
        Assert.Contains("project_name", providers);
        Assert.Contains("conv-proj", js);

        // Eighth field round: the chat header itself says which project the work belongs to —
        // the DETAIL carries the name, the second line under the title wears it and clicks
        // through to the project page. With the files pane open, the tree is never anonymous.
        // v0.3.8.112 — bounded by the registration's OWN parentheses rather than by 2,500
        // characters. The budget was a guess about how long that handler is, invisible when wrong,
        // and it shrinks whenever anyone adds a comment inside the handler. See docs/GUARDS.md.
        // CodeOnly first: `Src` returns raw text, and a `MapGet(` quoted inside a comment would be
        // parsed as a call — the trap SourceText was extracted to end.
        var detail = SourceText.CallSites(SourceText.CodeOnly(providers), "MapGet")
            .FirstOrDefault(call => call.Resolve(0, null) == "/conversations/{id}");
        Assert.True(detail is not null,
            "the /conversations/{id} registration is no longer recognisable in ApiHost.Providers.cs.");
        Assert.Contains("project_name", detail!.Text);
        Assert.Contains("id=\"chat-title-proj\"", Ui("index.html"));
        Assert.Contains("chat-title-proj", js);
        Assert.Contains("go('/projects/'+encodeURIComponent(d.project_id||''))", js);

        // + File / + Folder are pickers over the jailed project tree now — no prompt().
        var create = BodyOf(js, "async function chatFilesCreate(isDir, at)");
        Assert.Contains("cf-create-panel", create);
        Assert.DoesNotContain("prompt(", create);
    }

    /// <summary>
    /// v0.3.8.52 (fourth field round) — the files-pane toolbar survives every repo state. The
    /// badge is SHORT ("no git", never git's stderr — RepoOpsTests pins the server half); Init
    /// git is offered exactly while the directory is NOT a repository, through an apply_patch-
    /// gated endpoint that refuses a directory which already is one; and the working directory
    /// stays changeable after first setup — the Dir… button reopens the same set-root form.
    /// </summary>
    [Fact]
    public void TheFilesToolbar_SurvivesEveryRepoState_AndTheDirStaysChangeable()
    {
        var js = Ui("app.js");
        Assert.Contains("badge.textContent='no git'", js);
        var repoLoad = BodyOf(js, "async function chatFilesRepoLoad()");
        Assert.Contains("initBtn.hidden=true", repoLoad);    // a repo never offers Init
        Assert.Contains("initBtn.hidden=false", repoLoad);   // a non-repo always does
        Assert.Contains("/repo/init", js);

        var html = Ui("index.html");
        Assert.Contains("id=\"chat-files-gitinit\"", html);
        Assert.Contains("id=\"chat-files-chroot\"", html);
        Assert.Contains("chatFilesShowRootForm", js);        // one form, two doors

        // Sixth field round: hidden=true MUST mean invisible. .btn's display:inline-flex beat
        // the UA's [hidden]{display:none}, so Init git stood on screen beside a repo badge
        // saying master·clean with its hidden attribute correctly set. The global rule makes
        // the attribute unbeatable; verified live before landing.
        Assert.Contains("[hidden]{display:none !important;}", html);

        var providers = Src("src", "Anthill.Api", "ApiHost.Providers.cs");
        Assert.Contains("app.MapPost(\"/projects/{id}/repo/init\"", providers);
        Assert.Contains("already a git repository", providers);

        // Fifth field round: the badge must go LIVE the moment init or a directory change
        // succeeds — the cached GETs are busted, or 'Init git' appears to ignore the click
        // for a TTL while the stale "not a repo" answer replays.
        Assert.Contains("apiCacheBust('/projects/'+encodeURIComponent(chatFilesProjectId))", js);
    }

    /// <summary>
    /// v0.3.8.52 (fifth field round) — the splits are SIZABLE: chat ↔ pane horizontally, tree ↔
    /// editor vertically. One wiring function, two handles, proportions persisted; the vertical
    /// handle exists only while the editor is open (:has), so a handle never floats beside one
    /// lone pane.
    /// </summary>
    [Fact]
    public void TheSplits_AreSizable_AndRememberTheirProportions()
    {
        var js = Ui("app.js");
        Assert.Contains("wireSplit('chat-split-x', true, 'anthill_split_x'", js);
        Assert.Contains("wireSplit('chat-split-y', false, 'anthill_split_y'", js);
        // Guarded: a pointer that cannot be captured must not kill the drag handler (E2E sweep).
        Assert.Contains("try{ handle.setPointerCapture(e.pointerId); }catch", js);

        // And from the same sweep: inline-SVG glyphs may never ride a textContent sink — the
        // project subtitle printed literal "<svg …>" prose. Icons go through innerHTML with the
        // operator's data escaped around them; no setEl/textContent call may carry a GLYPH.
        Assert.DoesNotMatch(@"setEl\([^)]*GLYPH", js);
        Assert.DoesNotMatch(@"textContent\s*=[^;\n]*GLYPH", js);
        Assert.Contains("pvSub.innerHTML", js);

        var html = Ui("index.html");
        Assert.Contains("id=\"chat-split-x\"", html);
        Assert.Contains("id=\"chat-split-y\"", html);
        Assert.Contains(":has(#chat-files-editor[hidden])", html);
        Assert.Contains("cursor:col-resize", html);
        Assert.Contains("cursor:row-resize", html);

        // Seventh field round: a narrow pane may never push the controls off screen. The bar's
        // yield order is explicit — the PATH collapses first (10× shrink), controls never shrink
        // (buttons and the branch select are flex-shrink:0), wrap is the last resort. Verified
        // live at the drag clamp's narrowest before landing.
        Assert.Contains("flex-wrap:wrap", html);
        Assert.Contains(".chat-colony-bar .btn,.chat-colony-bar select{flex-shrink:0;}", html);
        Assert.Contains("flex:0 10 auto", html);
    }
}
