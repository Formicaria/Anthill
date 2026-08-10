using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.8.13 — guards the boundary between untrusted values and the console's pseudo-JavaScript
/// dispatcher.
///
/// `data-onclick` is a micro-interpreter: it splits its attribute on ';' and resolves whatever name
/// it finds against `window`. Patch file paths were being interpolated into a quoted argument there.
/// `ValidateSafePatchPath` rejects absolute paths, traversal, blocked directories and disallowed
/// suffixes, and has no reason to reject an apostrophe — so a path-valid filename could close the
/// argument early, append a second statement, and have it invoked as the operator when they clicked
/// the link.
///
/// These are source-scanning guards for the same reason the rest of this repository's UI guards are:
/// there is no browser harness, and the defect is visible in the source. The real invariant is
/// narrow and checkable — an untrusted value must never appear inside an executable attribute.
/// </summary>
public class UiActionDispatchTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate repo root (Anthill.sln) above the test bin directory.");
        return dir!.FullName;
    }

    private static string AppJs() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anthill.UI", "app.js"));

    /// <summary>
    /// The regression itself: no executable attribute may interpolate a patch file path.
    ///
    /// Deliberately matched on `file_path` rather than on the old call, so re-introducing the bug
    /// through a DIFFERENT handler still fails. A filename is the one value here that reaches the
    /// console straight from model output.
    /// </summary>
    [Fact]
    public void NoExecutableAttributeInterpolatesAPatchFilePath()
    {
        var offenders = Regex.Matches(AppJs(), @"data-on[a-z]+=""[^""]*file_path[^""]*""")
            .Select(m => m.Value)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A patch file_path is interpolated into an executable data-on* attribute, where an "
            + "apostrophe ends the argument early and a ';' starts a second statement. Use "
            + "data-action with a plain data-* attribute instead: " + string.Join(" | ", offenders));
    }

    /// <summary>
    /// The replacement must actually be wired, or the link silently stops working and the guard
    /// above still passes.
    /// </summary>
    [Fact]
    public void ThePatchLinkUsesTheFixedActionMap()
    {
        var js = AppJs();
        Assert.Contains("data-action=\"open-patches\"", js);
        Assert.Contains("'open-patches':", js);
        Assert.Contains("hasOwnProperty.call(ACTIONS", js);
    }

    /// <summary>
    /// The action name is looked up with hasOwnProperty, so inherited members cannot resolve.
    /// Without it, `data-action="constructor"` finds a function on the object prototype.
    /// </summary>
    [Fact]
    public void TheActionMapDoesNotResolveInheritedMembers()
    {
        // The dispatcher must never reach `window` to find its handler — that is precisely what
        // makes the data-onclick path unsafe for untrusted values.
        var js = AppJs();
        var start = js.IndexOf("var ACTIONS = {", StringComparison.Ordinal);
        Assert.True(start >= 0, "The data-action dispatcher is no longer recognisable.");
        var body = js.Substring(start, Math.Min(1200, js.Length - start));

        Assert.Contains("hasOwnProperty.call(ACTIONS", body);
        Assert.DoesNotContain("window[", body);
    }

    /// <summary>
    /// escapeHtml encodes the apostrophe. This is defence in depth and NOT the fix — getAttribute
    /// decodes entities before the dispatcher's parser runs, so encoding alone never protected the
    /// executable attributes. It is pinned so nobody removes it believing it was redundant.
    /// </summary>
    [Fact]
    public void EscapeHtmlEncodesTheApostrophe()
    {
        var m = Regex.Match(AppJs(), @"function escapeHtml\(s\)\{[^}]*replace\((/\[[^/]+/g)");
        Assert.True(m.Success, "escapeHtml no longer has a recognisable character class.");
        Assert.Contains("'", m.Groups[1].Value);
    }

    /// <summary>
    /// v3.8.34 — the GENERAL form of the v3.8.13 defect, which was only ever fixed for one value.
    ///
    /// The comment on <see cref="EscapeHtmlEncodesTheApostrophe"/> has said since v3.8.13 that
    /// "getAttribute decodes entities before the dispatcher's parser runs, so encoding alone never
    /// protected the executable attributes". That was true of EVERY interpolated value, not just a
    /// patch path — and 105 `data-onclick` attributes were still relying on `escapeHtml`, including
    /// Proxmox container ids and conversation ids that arrive from outside the colony.
    ///
    /// `jsArg` escapes for the INNER layer (backslash, then apostrophe) before escaping for the
    /// outer HTML one, so the emitted `\&#39;` decodes to `\'` and the interpreter unescapes it back
    /// to a literal apostrophe. Lossless, unlike stripping, which would silently alter an id.
    /// </summary>
    [Fact]
    public void NoExecutableAttributeRelaysAValueThroughEscapeHtmlAlone()
    {
        var offenders = Regex.Matches(AppJs(), @"data-on[a-z]+=""(?:[^""\\]|\\.)*""")
            .Select(m => m.Value)
            .Where(a => a.Contains("escapeHtml(", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These executable attributes interpolate through escapeHtml, which the HTML parser "
            + "decodes before the data-onclick interpreter ever sees it — so an apostrophe still "
            + "ends the argument and ';' still starts a second statement. Use jsArg (or data-action "
            + "with a plain data-* attribute): "
            + string.Join(" | ", offenders.Select(o => o.Length > 120 ? o[..120] + "…" : o)));
    }

    /// <summary>
    /// ...and `jsArg` must escape in the ORDER that survives both layers. Escaping the apostrophe
    /// before the backslash would let `\` + `'` collapse into an escaped quote the interpreter then
    /// unescapes into a real one — the same hole, reached the long way round.
    /// </summary>
    [Fact]
    public void JsArgEscapesTheBackslashBeforeTheApostrophe()
    {
        var m = Regex.Match(AppJs(), @"function jsArg\(s\)\{(?<body>[\s\S]{0,400}?)\n\}");
        Assert.True(m.Success, "jsArg is missing — the executable-attribute escape has no implementation.");

        var body = m.Groups["body"].Value;
        var backslashAt = body.IndexOf(@"replace(/\\/g", StringComparison.Ordinal);
        var quoteAt = body.IndexOf(@"replace(/'/g", StringComparison.Ordinal);

        Assert.True(backslashAt >= 0, "jsArg does not escape the backslash.");
        Assert.True(quoteAt >= 0, "jsArg does not escape the apostrophe.");
        Assert.True(backslashAt < quoteAt, "jsArg must escape the backslash BEFORE the apostrophe.");
        Assert.Contains("escapeHtml(", body);   // and still escape for the attribute it sits in
    }
}
