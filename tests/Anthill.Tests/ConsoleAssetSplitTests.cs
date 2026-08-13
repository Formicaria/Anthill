using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The invariants that make splitting app.js safe. v0.3.8.52 (AUTONOMY-10).
///
/// A console asset can be broken in three ways that NOTHING else in this repository would catch,
/// because each of them builds, tests and type-checks perfectly and only fails in a browser:
///
/// <list type="number">
/// <item>The file is not pinned as an EmbeddedResource, so the self-contained single-file binary
///   404s on it. Local `dotnet run` from a source checkout still works, which is what makes this
///   the nastiest of the three — it appears only in the shipped artifact.</item>
/// <item>The file is not served by a route, or not referenced by index.html, so it never loads.</item>
/// <item>It loads in the WRONG ORDER, so a load-time statement runs before what it depends on
///   exists. homelab.js registers PAGE_ENTER['homelab'] at load and PAGE_ENTER lives in app.js.</item>
/// </list>
///
/// These tests are the standing contract for every future extraction, not just the first one. They
/// enumerate the directory rather than naming files, so a domain split out next week is covered
/// without editing this file — the lesson from the CI step that named one test file and quietly
/// skipped the other, and from ConsoleRouteCoverageTests' hardcoded asset list.
/// </summary>
public class ConsoleAssetSplitTests
{
    private static string Root() => SourceText.RepoRoot();
    private static string UiDir() => Path.Combine(Root(), "src", "Anthill.UI");
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(Root(), Path.Combine(parts)));

    /// <summary>Every .js asset in the UI directory, by file name.</summary>
    private static IEnumerable<string> ConsoleScripts() =>
        Directory.GetFiles(UiDir(), "*.js").Select(Path.GetFileName).Where(n => n is not null).Select(n => n!);

    [Fact]
    public void EveryConsoleScript_IsPinnedAsAnEmbeddedResource()
    {
        var csproj = Read("src", "Anthill.Api", "Anthill.Api.csproj");

        foreach (var script in ConsoleScripts())
        {
            Assert.True(csproj.Contains($@"Anthill.UI\{script}", StringComparison.Ordinal),
                $"{script} is not an EmbeddedResource in Anthill.Api.csproj — it would 404 from the "
                + "single-file binary while working perfectly from a source checkout.");
            Assert.True(csproj.Contains($"LogicalName=\"Anthill.Api.Ui.{script}\"", StringComparison.Ordinal),
                $"{script} has no LogicalName pin; LoadUiAsset resolves by that exact name.");
        }
    }

    [Fact]
    public void EveryConsoleScript_IsLoadedAndServed()
    {
        var html = Read("src", "Anthill.UI", "index.html");
        var host = ApiHostSource.All();

        foreach (var script in ConsoleScripts())
        {
            Assert.True(html.Contains($"/ui/{script}", StringComparison.Ordinal),
                $"index.html never loads {script}.");
            Assert.True(host.Contains($"\"/ui/{script}\"", StringComparison.Ordinal),
                $"no route serves /ui/{script}.");
            Assert.True(host.Contains($"LoadUiAsset(\"{script}\")", StringComparison.Ordinal),
                $"{script} is never read out of the embedded resources.");
        }
    }

    /// <summary>
    /// app.js defines the shared foundation — PAGE_ENTER, api(), escapeHtml, the handler dispatcher
    /// — so every domain file split out of it must load after it. Asserted by position in the HTML,
    /// which is what actually determines execution order for deferred scripts.
    /// </summary>
    [Fact]
    public void DomainScripts_LoadAfterAppJs()
    {
        var html = Read("src", "Anthill.UI", "index.html");
        var appAt = html.IndexOf("/ui/app.js", StringComparison.Ordinal);
        Assert.True(appAt >= 0, "index.html must load app.js");

        // mission-thread.js is the deliberate exception: app.js consumes it at PARSE time, which is
        // why UiShellTests already asserts it loads first. dashboard-grid.js predates the split and
        // is self-contained (it only assigns window.AnthillGrid).
        var loadsBefore = new[] { "mission-thread.js", "dashboard-grid.js" };

        foreach (var script in ConsoleScripts().Where(s => s != "app.js" && !loadsBefore.Contains(s)))
        {
            var at = html.IndexOf($"/ui/{script}", StringComparison.Ordinal);
            Assert.True(at > appAt,
                $"{script} must load AFTER app.js — it depends on globals app.js defines, and a "
                + "deferred script that runs too early fails at load with a ReferenceError.");
        }
    }

    /// <summary>
    /// THE reason the split did not adopt `type="module"`, enforced rather than explained.
    ///
    /// The CSP-safe handler dispatcher resolves callbacks through `window[name]`. Under module
    /// scope, top-level declarations are not on `window`, so every `data-onclick="foo(...)"` in the
    /// console would silently stop resolving — no build error, no test failure, just dead buttons.
    /// If someone converts these to modules later, they must make the global surface explicit
    /// first, and this test is what tells them that at the moment they try.
    /// </summary>
    [Fact]
    public void ConsoleScripts_AreClassicScripts_BecauseHandlersResolveThroughWindow()
    {
        var html = Read("src", "Anthill.UI", "index.html");
        var appJs = Read("src", "Anthill.UI", "app.js");

        Assert.Contains("window[path[0]]", appJs);

        foreach (Match tag in Regex.Matches(html, @"<script\b[^>]*src=""/ui/[^""]+""[^>]*>"))
        {
            Assert.DoesNotContain("type=\"module\"", tag.Value);
            Assert.Contains("defer", tag.Value);
        }
    }

    /// <summary>
    /// The split has to actually reduce the thing it was for. app.js was ~10,600 lines in one unit;
    /// a "split" that leaves it that size has moved comments around.
    /// </summary>
    [Fact]
    public void AppJs_IsSmallerThanTheMonolithItWas()
    {
        var appLines = File.ReadAllLines(Path.Combine(UiDir(), "app.js")).Length;

        Assert.True(appLines < 10_000,
            $"app.js is {appLines} lines. The v0.3.8.52 split brought it under 10,000 by moving the "
            + "homelab domain out; a regression past that means a domain came back in.");
        Assert.True(ConsoleScripts().Count() >= 4,
            "the console should be more than one script plus its two pre-split helpers.");
    }

    /// <summary>
    /// Nothing may be defined in two console assets at once. A duplicated function is the specific
    /// way a copy-paste "split" goes wrong: both files parse, the later definition silently wins,
    /// and the two copies drift until one of them is subtly stale.
    /// </summary>
    [Fact]
    public void NoTopLevelFunction_IsDefinedInTwoConsoleAssets()
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var duplicates = new List<string>();

        foreach (var script in ConsoleScripts())
        {
            var text = File.ReadAllText(Path.Combine(UiDir(), script));
            foreach (Match m in Regex.Matches(text, @"(?m)^(?:async\s+)?function\s+([A-Za-z_$][\w$]*)\s*\("))
            {
                var name = m.Groups[1].Value;
                if (owners.TryGetValue(name, out var first) && first != script)
                    duplicates.Add($"{name} (in {first} and {script})");
                else owners[name] = script;
            }
        }

        Assert.True(duplicates.Count == 0,
            "the same top-level function is defined in more than one console asset; the later load "
            + "silently wins: " + string.Join(", ", duplicates));
    }
}
