using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A FUNCTION THE CONSOLE CALLS FROM INSIDE A TEMPLATE LITERAL EXISTS. v0.3.8.129.
///
/// WHAT THIS IS FOR. v0.3.8.127 deleted a block of inspector markup and left its call standing at
/// `app.js`'s `${inspectorFactsHtml(n)}`. Two readers should have caught it and neither could:
/// `node --check` parses an interpolation without resolving what it names, and
/// `RegressionGuardTests.UiIntegrity_ColonyAndChamberSymbolsAreDeclared` strips template literals
/// WHOLE before it looks for undeclared symbols, so the one place the reference lived was the one
/// place that guard blanks. Its rule was right and its reader could not see the site — the shape
/// this repository has now paid for often enough to name it defect class 11.
///
/// So this guard looks where that one does not, and only there: the inside of an interpolation.
/// It is deliberately NOT a general "is every identifier declared" sweep — that is a type checker,
/// and a bad one written in regex. An interpolated call is the narrow case where a missing name is
/// silent at parse time, fatal at click time, and cheap to detect.
///
/// WIDEN WHERE IT LOOKS, NEVER WHAT IT ACCEPTS: a name that is genuinely supplied by the browser
/// goes in <see cref="Builtins"/> by name, so adding one is a decision somebody made rather than a
/// pattern that quietly stopped matching.
/// </summary>
public class ConsoleInterpolationTests
{
    /// <summary>`${someFunction(` — a call in the one position the other guard blanks.</summary>
    private static readonly Regex InterpolatedCall =
        new(@"\$\{\s*(?<name>[A-Za-z_$][\w$]*)\s*\(", RegexOptions.Compiled);

    /// <summary>`function f(`, `const f = `, `let f = `, `var f = ` — every top-level shape the console uses.</summary>
    private static readonly Regex Declaration =
        new(@"\bfunction\s+(?<name>[A-Za-z_$][\w$]*)\s*\(|\b(?:const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*=",
            RegexOptions.Compiled);

    /// <summary>Supplied by the browser, so absent from the assets and correctly so.</summary>
    private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "Math", "JSON", "Object", "Array", "String", "Number", "Date", "Boolean",
        "parseInt", "parseFloat", "isNaN", "encodeURIComponent", "decodeURIComponent",
        "Set", "Map", "RegExp", "Promise",
    };

    private static IEnumerable<string> ConsoleAssets() =>
        Directory.GetFiles(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.UI"), "*.js");

    [Fact]
    public void EveryFunctionCalledInsideAnInterpolation_IsDeclaredByAConsoleAsset()
    {
        var sources = ConsoleAssets().ToDictionary(f => Path.GetFileName(f), File.ReadAllText);

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in sources.Values)
            foreach (Match m in Declaration.Matches(text))
                declared.Add(m.Groups["name"].Value);

        var undeclared = new SortedSet<string>(StringComparer.Ordinal);
        var sites = 0;

        foreach (var (file, text) in sources)
            foreach (Match m in InterpolatedCall.Matches(text))
            {
                sites++;
                var name = m.Groups["name"].Value;
                if (declared.Contains(name) || Builtins.Contains(name)) continue;
                undeclared.Add($"{file}: {name}(");
            }

        Assert.True(sites >= 200,
            $"only {sites} interpolated calls were found across the console assets. There are "
          + "hundreds, so this sweep has stopped seeing them and the assertion below is vacuous.");

        Assert.True(undeclared.Count == 0,
            "the console interpolates calls to functions nothing declares. Each one throws a "
          + "ReferenceError the moment its markup is built, and takes the rest of the surrounding "
          + "render with it:\n  " + string.Join("\n  ", undeclared));
    }

    /// <summary>
    /// AND BOTH READERS STILL READ. A regex that stopped matching is indistinguishable from a clean
    /// tree, which is the failure this whole file was written about — so the detector is proved
    /// against the exact line that got past it, and the declaration it was missing.
    /// </summary>
    [Fact]
    public void TheDetectors_StillDetect()
    {
        Assert.Matches(InterpolatedCall, "${inspectorFactsHtml(n)}");
        Assert.Matches(Declaration, "function inspectorFactsHtml(n){");
        Assert.Matches(Declaration, "const antTrailStrengths = () => 1;");

        Assert.Equal("inspectorFactsHtml",
            InterpolatedCall.Match("${inspectorFactsHtml(n)}").Groups["name"].Value);
        Assert.Equal("inspectorFactsHtml",
            Declaration.Match("function inspectorFactsHtml(n){").Groups["name"].Value);
    }
}
