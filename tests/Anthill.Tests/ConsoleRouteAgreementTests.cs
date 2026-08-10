using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Every endpoint the console asks for must be an endpoint the API registers.
///
/// v3.8.34. `loadAntObsDirectory` fetched `/colony/graph` — a route that has never existed in this
/// API. It 404'd on every load of the Agent Inspector. Nothing failed: the response was destructured
/// behind `g&&g.success&&…`, the lookup table stayed empty, the card template omitted the runtime
/// state line via `${s?…:''}`, and the whole function sat inside a `catch` commented "directory is
/// additive". So v3.8.32's readiness work — `RoleGateStatus.NotGated`, `status_label`,
/// `unavailability_reason`, the fields written precisely so the console would stop reducing "not
/// running" to a bare 'inactive' — reached the operator on zero of 56 cards, and the comment above
/// the call claimed the opposite.
///
/// The shape is the one <c>CrossBoundaryAgreementTests</c> was written for, moved to the boundary
/// that file does not cover. The console and the API are two halves of a contract, and every test
/// over either half built its own input: the API tests assert `/colony/registry` returns
/// `runtime_status`, the console tests assert the cards render, and neither asks whether the console
/// requests the route the API serves. `runtime_status` was in the response the function ALREADY had
/// — the second request was not merely wrong, it was redundant, and line ~986 reads the same field
/// off the same response correctly. Two call sites for one contract; one of them disagreed, and
/// only the wrong one was unguarded.
///
/// This detector was verified to FAIL on the tree before the fix (one offender, `/colony/graph`) and
/// pass after. A guard nobody has seen fail is a guard nobody has tested.
/// </summary>
public class ConsoleRouteAgreementTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    /// <summary>
    /// Route templates the API registers. Both registration forms count: the minimal-API
    /// `app.MapGet("/x", …)` and the helpers (`ProtectedJson`, `ProtectedText`) that take the path as
    /// their second argument. Reading only the first form is how a route inventory comes to be
    /// missing `/status` — which would then look like console drift rather than a blind spot in the
    /// reader.
    /// </summary>
    private static HashSet<string> RegisteredRoutes()
    {
        var routes = new HashSet<string>(StringComparer.Ordinal);
        var api = Path.Combine(Root(), "src", "Anthill.Api");

        foreach (var file in Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            foreach (var line in File.ReadLines(file))
            {
                if (!Regex.IsMatch(line, @"\b(Map[A-Za-z]+|Protected[A-Za-z]*)\s*\(")) continue;
                foreach (Match m in Regex.Matches(line, "\"(/[^\"]*)\""))
                    routes.Add(Normalise(m.Groups[1].Value));
            }
        }

        return routes;
    }

    /// <summary>
    /// Paths the console requests, as far as they are statically knowable. A path is truncated at
    /// the first interpolation or concatenation because what follows is not a literal; the prefix is
    /// still enough to catch a route that does not exist at all, which is the defect class here.
    /// </summary>
    private static Dictionary<string, string> ConsoleRequests()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var ui = Path.Combine(Root(), "src", "Anthill.UI");

        foreach (var file in Directory.EnumerateFiles(ui, "*.*", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetExtension(file) is not (".js" or ".html")) continue;
            var text = File.ReadAllText(file);

            foreach (Match m in Regex.Matches(text, @"\bapi(?:Text)?\s*\(\s*[`'""](/[^`'""]*)"))
            {
                var path = Normalise(m.Groups[1].Value);
                if (path.Length == 0) continue;
                found.TryAdd(path, Path.GetFileName(file));
            }
        }

        return found;
    }

    /// <summary>Query string, interpolation and trailing slash carry no routing information.</summary>
    private static string Normalise(string path)
    {
        var at = path.IndexOf('?');
        if (at >= 0) path = path[..at];

        at = path.IndexOf("${", StringComparison.Ordinal);
        if (at >= 0) path = path[..at];

        return path.TrimEnd('/');
    }

    /// <summary>
    /// A console path is satisfied by a route that equals it or extends it. Extension counts because
    /// `api('/missions/' + id)` truncates to `/missions`, which `/missions/{id}` legitimately serves.
    /// </summary>
    private static bool IsServed(string consolePath, HashSet<string> routes) =>
        routes.Contains(consolePath) ||
        routes.Any(r => r.StartsWith(consolePath + "/", StringComparison.Ordinal));

    [Fact]
    public void EveryEndpointTheConsoleCalls_IsRegisteredByTheApi()
    {
        var routes = RegisteredRoutes();
        var requests = ConsoleRequests();

        // Guard the reader before trusting its verdict: an inventory that silently collected nothing
        // would make this test pass by finding no offenders, which is the failure mode of every
        // source-level detector.
        Assert.True(routes.Count > 100, $"route inventory looks broken — found only {routes.Count} routes");
        Assert.True(requests.Count > 50, $"console request inventory looks broken — found only {requests.Count} paths");

        var orphans = requests
            .Where(kv => !IsServed(kv.Key, routes))
            .Select(kv => $"{kv.Value} calls {kv.Key}, which no route serves")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(orphans.Count == 0,
            "The console requests endpoints the API does not register:\n  " + string.Join("\n  ", orphans));
    }
}
