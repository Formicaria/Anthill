using System;
using System.Collections.Generic;
using System.IO;
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
///   exists. infrastructure.js registers PAGE_ENTER['infrastructure'] at load and PAGE_ENTER lives in app.js.</item>
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
            + "infrastructure domain out; a regression past that means a domain came back in.");
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
    /// <summary>
    /// A CONSOLE FILE REACHES ANOTHER ONE THROUGH A PUBLISHED SEAM, NEVER THROUGH ITS PRIVATES.
    /// v0.3.9.1.
    ///
    /// THE DEFECT, and it shipped. `memory-vault.js` called `liveApi()` — a helper PRIVATE to
    /// `colony-home.js`'s IIFE — behind `typeof liveApi === 'function'`. That guard is false in
    /// every other file, so the chambers were never handed the vault, a leaf never moved the
    /// camera, and no local graph was ever drawn. Nothing threw. The tree, the search and the card
    /// all worked, so the feature looked finished and half of it was reaching nobody.
    ///
    /// WHAT MAKES IT CHECKABLE is that these files are IIFEs with exactly one export each: what a
    /// file publishes it hangs on `window`. So a name that is defined inside another file's closure
    /// and used here is, by construction, a call into a private scope — and the typeof guard in
    /// front of it is what turns the mistake silent.
    /// </summary>
    /// <summary>
    /// A DELEGATED LISTENER MUST COVER EVERY ROOT ITS CONTROLS ARE DRAWN INTO. v0.3.9.4.
    ///
    /// THE DEFECT, and it shipped for two releases: `memory-vault.js` bound one click listener to
    /// `#mv-panel` and drew the record card into `#mv-card`, which is a SIBLING of the panel in
    /// `index.html` — it has to be, because the card floats to the left of the 340px column and
    /// nesting it would clip it. So every control the card drew was inert. The ✕ did nothing, the
    /// link chips did nothing, "Open the full record" did nothing, and the card LOOKED correct,
    /// which is why nobody caught it: a delegated listener aimed at the wrong subtree fails exactly
    /// as silently as one that was never written.
    ///
    /// THE GUARD IS STRUCTURAL rather than a list of button names. Every element id the module
    /// emits `data-mv*` attributes into is a root that has to be bound, and there are two; asserting
    /// the two bindings exist is the smallest check that cannot be satisfied by a card that merely
    /// renders. A third host added later and not bound fails here rather than in an operator's
    /// hands.
    /// </summary>
    [Fact]
    public void TheVaultsClickHandler_IsBoundToEveryHostItDrawsControlsInto()
    {
        var code = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.UI", "memory-vault.js")));

        foreach (var host in new[] { "mv-panel", "mv-card" })
        {
            Assert.True(
                Regex.IsMatch(code, @"\$\(['""]" + host + @"['""]\)"),
                $"memory-vault.js no longer looks up #{host}.");
        }

        // Both roots register the SAME handler. Two handlers with two copies of the routing would
        // pass a naive check and diverge on the first control added to only one of them.
        var registrations = Regex.Matches(code, @"addEventListener\(\s*['""]click['""]\s*,\s*onClick\s*\)").Count;
        Assert.True(registrations >= 2,
            $"memory-vault.js registers `onClick` on {registrations} root(s). The panel and the card "
          + "are SIBLINGS in index.html — the card floats outside the panel column on purpose — so "
          + "one registration leaves every control the card draws inert, silently. That is how the "
          + "✕, the link chips and 'Open the full record' all shipped doing nothing.");
    }

    /// <summary>
    /// THE VAULT'S RECORDS SURVIVE A TOPOLOGY POLL. v0.3.9.4.
    ///
    /// `.9` recorded `vaultChambers[id] = true` in `setVaultRecords` and NOTHING EVER READ IT, so
    /// the next `/colony/topology` poll rebuilt the memory chamber from the reducer's recent slice
    /// and eight thousand dots vanished. The operator's report was "they disappear fully when i
    /// click off, and i can only see them again by refreshing" — a reload being the only thing that
    /// makes the panel push again.
    ///
    /// Declared and reaching nobody, at the seam between two writers of one chamber. The guard is
    /// that `setTopology`'s rebuild goes through the merge rather than straight to `rebuildSector`,
    /// because that call site IS the rule.
    /// </summary>
    [Fact]
    public void TheTopologyPoll_DoesNotOverwriteAChamberTheVaultOwns()
    {
        var code = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.UI", "colony-live.js")));

        Assert.True(code.Contains("rebuildSector(s, vaultOver(sec))", StringComparison.Ordinal),
            "setTopology rebuilds sectors straight from the snapshot again. The snapshot carries the "
          + "reducer's RECENT slice, so a chamber the vault has filled is emptied on the next poll — "
          + "which is exactly the defect v0.3.9.4 fixed. Route it through `vaultOver`.");

        Assert.True(code.Contains("vaultSectors[id] =", StringComparison.Ordinal),
            "setVaultRecords no longer HOLDS what it pushed. `vaultOver` has nothing to put back, so "
          + "the flag it reads is true and the records are gone — the same defect one step in.");
    }

    [Fact]
    public void NoConsoleScript_CallsAnotherFilesPrivateHelper()
    {
        // The known private helpers, by the file that owns them. A name added here is a name that
        // must be reached through `window.<Module>` from anywhere else.
        var privates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["liveApi"] = "colony-home.js (reach the renderer with window.ColonyHost.live())",
        };

        var problems = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.UI"), "*.js"))
        {
            var name = Path.GetFileName(file);
            var code = SourceText.CodeOnly(File.ReadAllText(file));

            foreach (var (helper, owner) in privates)
            {
                if (owner.StartsWith(name, StringComparison.Ordinal)) continue;   // its own file may use it
                if (!Regex.IsMatch(code, @"\b" + Regex.Escape(helper) + @"\s*\(")) continue;
                problems.Add($"{name} calls `{helper}()`, which is private to {owner}. It is not "
                           + "defined in this file's scope, so the call is skipped silently and "
                           + "whatever it was wiring reaches nobody.");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
    }

}
