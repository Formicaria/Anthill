using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// WHAT THE CONSOLE SENDS IS WHAT THE ROUTE ACCEPTS. v0.3.9.5.
///
/// THE DEFECT THIS EXISTS FOR MADE **Bind** DO **UNBIND**, and nothing else in this suite could have
/// caught it — including the first version of this guard, which asserted the wrong rule and is worth
/// recording because getting it wrong was instructive.
///
/// `ReadFromJsonAsync&lt;T&gt;` uses ASP.NET's default HTTP JSON options: camelCase naming policy,
/// case-INsensitive matching. Case-insensitive is not separator-insensitive. `knowledge.js` posted
/// `{"project": "...", "knowledge_base": "..."}`; `KnowledgeMapRequest` declared `KnowledgeBase`,
/// which the policy spells `knowledgeBase`; the two differ by an underscore, matched nothing, and
/// the field arrived null. An empty knowledge base means UNBIND — a real operation the page needs —
/// so the route did exactly what the missing field told it, removed the mapping, and reported that
/// accurately in green. `Project` is one word, so it bound fine and the message even named the right
/// project. No exception, no log, a success envelope, and a true sentence saying the opposite of
/// what the button promised.
///
/// THE FIRST VERSION OF THIS GUARD DEMANDED `[JsonPropertyName]` ON EVERY MULTI-WORD FIELD, and it
/// was wrong. It found thirty-four fields across the Infrastructure module — every one of which is
/// CORRECT, because `infrastructure.js` sends camelCase (`nodeId`, `fromKind`, `internetExposed`)
/// and the default policy matches that exactly. Annotating them snake_case would have broken a
/// working console to satisfy a test. There is no repository-wide convention to enforce here and
/// inventing one would be a guard imposing a rule rather than protecting a property: Micromound is
/// snake_case because a device is not a browser, Infrastructure is camelCase because it is one.
///
/// THE PROPERTY THAT ACTUALLY MATTERS is that the two ENDS AGREE, so that is what is asserted. Each
/// console `api('&lt;route&gt;', 'POST', { ... })` is paired with the record the matching `MapPost`
/// deserializes, and every key in the body must be a name that record accepts — its
/// `[JsonPropertyName]` if it has one, otherwise the camelCase of its property, compared
/// case-insensitively exactly as the runtime compares them.
///
/// IT FOUND A SECOND LIVE INSTANCE THE MOMENT IT RAN. `/infrastructure/credentials` posts
/// `target_host` into a `TargetHost` that spelled itself `targetHost`, so every credential saved
/// from that page was stored against an EMPTY host — silently, with a success envelope, for as long
/// as the page has existed. One defect reported by an operator, one found by the guard written for
/// it, in a module nobody was looking at.
/// </summary>
public class RequestWireNameTests
{
    /// <summary>A POST route and the request record its handler reads.</summary>
    private sealed record RouteBinding(string Route, string Record, IReadOnlySet<string> Accepts);

    /// <summary>One console call: a literal route and the keys of its object-literal body.</summary>
    private sealed record ConsoleCall(string File, string Route, IReadOnlyList<string> Keys);

    private static string Root() => SourceText.RepoRoot();

    // ── the server side ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every positional record in the API, with the wire names it will actually match.
    ///
    /// The camelCase fallback is not a guess — it is the naming policy ASP.NET applies when a
    /// property carries no attribute, and reproducing it here is the only way the comparison means
    /// anything.
    /// </summary>
    private static Dictionary<string, HashSet<string>> RequestRecords(
        IReadOnlyDictionary<string, string> sources)
    {
        var records = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var code in sources.Values)
        foreach (Match m in Regex.Matches(code, @"record\s+(\w+)\s*\(([^;]*?)\)\s*;", RegexOptions.Singleline))
        {
            var accepts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in SplitParameters(m.Groups[2].Value))
            {
                var text = parameter.Trim();
                if (text.Length == 0) continue;

                var named = Regex.Match(text, @"JsonPropertyName\(""([^""]+)""\)");
                if (named.Success) { accepts.Add(named.Groups[1].Value); continue; }

                var withoutAttributes = Regex.Replace(text, @"\[[^\]]*\]", " ");
                var identifiers = Regex.Matches(withoutAttributes.Split('=')[0], @"\b[A-Za-z_]\w*\b")
                    .Select(x => x.Value).ToList();
                if (identifiers.Count == 0) continue;

                var property = identifiers[^1];
                accepts.Add(char.ToLowerInvariant(property[0]) + property[1..]);
            }
            records[m.Groups[1].Value] = accepts;
        }
        return records;
    }

    /// <summary>
    /// `MapPost("route", …)` paired with the `ReadFromJsonAsync&lt;T&gt;` inside its handler.
    ///
    /// Bounded to the text following the route so a handler that reads no body pairs with nothing
    /// rather than borrowing the next route's record.
    /// </summary>
    private static Dictionary<string, RouteBinding> PostRoutes(
        IReadOnlyDictionary<string, string> sources, IReadOnlyDictionary<string, HashSet<string>> records)
    {
        var routes = new Dictionary<string, RouteBinding>(StringComparer.Ordinal);

        foreach (var code in sources.Values)
        foreach (Match m in Regex.Matches(code, @"MapPost\(\s*""([^""]+)"""))
        {
            var window = code.Substring(m.Index, Math.Min(3000, code.Length - m.Index));
            var body = Regex.Match(window, @"ReadFromJsonAsync<\s*(\w+)\s*>");
            if (!body.Success) continue;
            if (!records.TryGetValue(body.Groups[1].Value, out var accepts)) continue;
            routes[m.Groups[1].Value] = new RouteBinding(m.Groups[1].Value, body.Groups[1].Value, accepts);
        }
        return routes;
    }

    // ── the console side ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Console POSTs with a LITERAL route and an object-literal body — the calls where both ends can
    /// be read statically. A templated route or a body held in a variable is skipped rather than
    /// guessed at; the coverage floor below is what keeps that from quietly becoming everything.
    /// </summary>
    private static List<ConsoleCall> ConsoleCalls()
    {
        var calls = new List<ConsoleCall>();

        foreach (var file in Directory.GetFiles(Path.Combine(Root(), "src", "Anthill.UI"), "*.js"))
        {
            var js = SourceText.CodeOnly(File.ReadAllText(file));
            foreach (Match m in Regex.Matches(js,
                @"api\(\s*['""]([^'""$]+)['""]\s*,\s*['""]POST['""]\s*,\s*\{"))
            {
                var open = m.Index + m.Length - 1;
                var body = BracedBody(js, open);
                if (body is null) continue;

                var keys = Regex.Matches(body, @"(?:^|[,{])\s*([A-Za-z_]\w*)\s*:")
                    .Select(k => k.Groups[1].Value).ToList();
                calls.Add(new ConsoleCall(Path.GetFileName(file), m.Groups[1].Value, keys));
            }
        }
        return calls;
    }

    [Fact]
    public void EveryConsolePostField_IsANameItsRouteAccepts()
    {
        var sources = Directory
            .GetFiles(Path.Combine(Root(), "src", "Anthill.Api"), "*.cs", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllText, StringComparer.Ordinal);

        var records = RequestRecords(sources);
        var routes = PostRoutes(sources, records);

        var mismatches = new List<string>();
        foreach (var call in ConsoleCalls())
        {
            if (!routes.TryGetValue(call.Route, out var route)) continue;
            foreach (var key in call.Keys.Where(k => !route.Accepts.Contains(k)))
            {
                mismatches.Add(
                    $"{call.File} → POST {call.Route} sends \"{key}\", but {route.Record} accepts "
                  + $"[{string.Join(", ", route.Accepts.OrderBy(x => x, StringComparer.Ordinal))}]");
            }
        }

        Assert.True(mismatches.Count == 0,
            "The console sends a field name its route will not bind, so the value arrives NULL and "
          + "the handler runs on a default nobody chose. Nothing errors: the request succeeds and "
          + "the response is accurate about what the handler actually did. That is how Bind came to "
          + "unbind — an empty knowledge_base legitimately means unbind — and how credentials came "
          + "to save against an empty host. Fix the END THAT IS WRONG: add "
          + "[property: JsonPropertyName(\"the_wire_name\")] if the console's spelling is right, or "
          + "change the console if the record's is:\n  " + string.Join("\n  ", mismatches));
    }

    /// <summary>
    /// COVERAGE FLOOR. Both halves of this pairing are regex over source, and either one silently
    /// matching nothing leaves the guard permanently green over an empty comparison — the failure
    /// mode this suite has now found in seven separate checks, including a `colony-live.js` guard
    /// that was passing against a branch the code no longer took.
    ///
    /// The named route is the one the defect shipped in; the count is a floor and not the current
    /// total, so adding a console call does not mean editing a test.
    /// </summary>
    [Fact]
    public void ThePairing_ActuallyReachesBothEnds()
    {
        var sources = Directory
            .GetFiles(Path.Combine(Root(), "src", "Anthill.Api"), "*.cs", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllText, StringComparer.Ordinal);

        var records = RequestRecords(sources);
        var routes = PostRoutes(sources, records);
        var calls = ConsoleCalls();

        Assert.True(records.Count >= 20,
            $"Found {records.Count} request records in the API. The scan is broken, not the API.");
        Assert.True(routes.Count >= 15,
            $"Paired {routes.Count} POST routes with a request record; the API has many more.");

        var paired = calls.Count(c => routes.ContainsKey(c.Route));
        Assert.True(paired >= 15,
            $"Only {paired} console POST bodies paired with a route. The comparison above is then "
          + "asserting almost nothing, which is worse than not having it.");

        // The route the defect shipped in must be one of them, by name.
        Assert.Contains(calls, c => c.Route == "/knowledge/project-map" && routes.ContainsKey(c.Route));
        Assert.Contains(routes.Values, r => r.Record == "KnowledgeMapRequest");

        // And the accepted-name derivation must reproduce BOTH forms, or the comparison is trivially
        // satisfiable: an explicit wire name, and the camelCase fallback for an unannotated field.
        Assert.Contains("knowledge_base", routes["/knowledge/project-map"].Accepts);
        Assert.Contains("nodeId", records["ServiceUpsertRequest"]);
    }

    // ── small helpers ────────────────────────────────────────────────────────────────────────

    /// <summary>The text between a `{` and its matching `}`, or null if it never closes.</summary>
    private static string? BracedBody(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length && i < open + 2000; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return text[(open + 1)..i];
            }
        }
        return null;
    }

    /// <summary>Split a parameter list on top-level commas — generics carry commas of their own.</summary>
    private static IEnumerable<string> SplitParameters(string parameters)
    {
        var depth = 0;
        var current = new StringBuilder();
        foreach (var c in parameters)
        {
            if (c is '<' or '[' or '(') depth++;
            else if (c is '>' or ']' or ')') depth--;

            if (c == ',' && depth == 0) { yield return current.ToString(); current.Clear(); continue; }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
