using System.Text.RegularExpressions;
using Anthill.Core.Agents;
using Anthill.Core.Common;
using Anthill.Core.Configuration;

namespace Anthill.Core.Diagnostics;

/// <summary>
/// v3.0.0 baseline lock — the generated runtime inventory.
///
/// V2 shipped seven well-tested subsystems that nothing called. Each was found by hand, one
/// release apart, by a human noticing. That is not a process. This inventory enumerates what the
/// runtime DECLARES — roles, contracts, tools, feature gates, endpoints, tables, background loops
/// — from the live registries where possible and from the source where reflection cannot see, and
/// pairs each declaration with its production call sites.
///
/// It is deliberately not clever. A regex over source can be fooled; the point is not perfect
/// static analysis but a durable, reviewable answer to "what does this system claim to have, and
/// who actually uses it?" — cheap enough to run in CI, honest enough that a gap is visible.
///
/// Companion: <see cref="CallSiteAudit"/> turns the gaps into a CI failure.
/// </summary>
public sealed record InventoryEntry(
    string Kind,            // role | contract_kind | feature_gate | endpoint | table | background_loop
    string Name,
    string Detail,
    IReadOnlyList<string> CallSites)
{
    /// <summary>A declaration with no production consumer — the defect this inventory exists for.</summary>
    public bool Orphaned => CallSites.Count == 0;
}

public sealed record RuntimeInventoryReport(
    string Version,
    string GeneratedAt,
    IReadOnlyList<InventoryEntry> Entries)
{
    public IReadOnlyList<InventoryEntry> Orphans => Entries.Where(e => e.Orphaned).ToList();
    public IEnumerable<IGrouping<string, InventoryEntry>> ByKind => Entries.GroupBy(e => e.Kind);
}

public static class RuntimeInventory
{
    public const string Kinds_Role = "role";
    public const string Kinds_FeatureGate = "feature_gate";
    public const string Kinds_Endpoint = "endpoint";
    public const string Kinds_Table = "table";
    public const string Kinds_BackgroundLoop = "background_loop";

    /// <summary>
    /// Build the inventory. <paramref name="repoRoot"/> is scanned for production sources; the
    /// live registries supply what only the runtime knows.
    /// </summary>
    public static RuntimeInventoryReport Build(string repoRoot)
    {
        var sources = ProductionSources(repoRoot);
        var entries = new List<InventoryEntry>();

        entries.AddRange(Roles(sources));
        entries.AddRange(FeatureGates(repoRoot, sources));
        entries.AddRange(Endpoints(sources));
        entries.AddRange(Tables(repoRoot, sources));
        entries.AddRange(BackgroundLoops(sources));

        return new RuntimeInventoryReport(AnthillRuntime.Version, AnthillTime.NowUtc().ToIso(),
            entries.OrderBy(e => e.Kind, StringComparer.Ordinal)
                   .ThenBy(e => e.Name, StringComparer.Ordinal).ToList());
    }

    /// <summary>Every production .cs file, keyed by repo-relative path. Tests are excluded on
    /// purpose: a test is not a production consumer — that is the entire lesson of V2.</summary>
    internal static IReadOnlyDictionary<string, string> ProductionSources(string repoRoot)
    {
        var src = Path.Combine(repoRoot, "src");
        if (!Directory.Exists(src)) return new Dictionary<string, string>();
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToDictionary(
                p => Path.GetRelativePath(repoRoot, p).Replace('\\', '/'),
                p => StripComments(File.ReadAllText(p)));
    }

    /// <summary>
    /// Comments are stripped before searching: a call site mentioned only in a doc comment is
    /// exactly the false positive that let V2's dead code look alive.
    ///
    /// A SCANNER, not two regexes — and the reason is a defect this tool had for three releases.
    /// The old implementation removed block comments first with <c>/\*.*?\*/</c>, which cannot tell a
    /// real comment opener from the characters <c>/*</c> appearing inside a line comment or a string.
    /// ModelRouter.cs contains the prose "API lived at /api/*" in a doc comment; that phantom opener
    /// matched forward to the next genuine <c>*&#47;</c> two hundred and seventy lines later, and the
    /// scanner silently deleted everything between them.
    ///
    /// Both directions of that failure matter, and the quiet one matters more. It reported a false
    /// orphan for a gate that was wired — annoying, visible, fixable. It would equally have hidden a
    /// REAL orphan whose only call sites fell inside the swallowed window, reporting a dead
    /// subsystem as healthy. That is the precise failure this whole audit exists to prevent, in the
    /// audit itself.
    ///
    /// Reordering the two regexes does not fix it: stripping line comments first truncates a
    /// <c>*&#47;</c> that happens to follow a <c>//</c> inside a real block comment, leaving comment
    /// prose in the source to be counted as code — false call sites instead of false orphans. Only a
    /// pass that knows what it is inside gets both right.
    /// </summary>
    internal static string StripComments(string source)
    {
        if (string.IsNullOrEmpty(source)) return source ?? "";

        var output = new System.Text.StringBuilder(source.Length);
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            // ---- comments ------------------------------------------------------------------
            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;                      // the newline itself is kept below
            }
            if (c == '/' && next == '*')
            {
                i += 2;
                while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                {
                    // Newlines are preserved so line numbers and line-oriented searches still line
                    // up with the original file.
                    if (source[i] == '\n') output.Append('\n');
                    i++;
                }
                i = Math.Min(i + 2, source.Length);
                continue;
            }

            // ---- string and character literals ---------------------------------------------
            // Kept VERBATIM. A symbol named inside a string is not a call site for a static gate,
            // but it very much is one for a role id — Roles() searches for "\"researcher\"" — so
            // stripping literals would blind the audit to every role in the runtime.
            if (c == '"' && i >= 2 && source[i - 1] == '@' && source[i - 2] == '$'
                || c == '"' && i >= 1 && source[i - 1] == '@')
            {
                // Verbatim: no escapes; a doubled "" is a literal quote.
                output.Append(c); i++;
                while (i < source.Length)
                {
                    if (source[i] == '"' && i + 1 < source.Length && source[i + 1] == '"')
                    { output.Append('"').Append('"'); i += 2; continue; }
                    if (source[i] == '"') { output.Append('"'); i++; break; }
                    output.Append(source[i]); i++;
                }
                continue;
            }
            if (c == '"' || c == '\'')
            {
                var quote = c;
                output.Append(c); i++;
                while (i < source.Length && source[i] != quote)
                {
                    // An escaped quote does not end the literal, and \\ before a quote does.
                    if (source[i] == '\\' && i + 1 < source.Length)
                    { output.Append(source[i]).Append(source[i + 1]); i += 2; continue; }
                    if (source[i] == '\n') break;   // unterminated: do not run away with the file
                    output.Append(source[i]); i++;
                }
                if (i < source.Length && source[i] == quote) { output.Append(quote); i++; }
                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
    }

    /// <summary>
    /// Files that consume the symbol.
    ///
    /// Two rules learned from the first draft, which reported 61 false positives:
    ///  1. Dot-qualified access counts. `AnthillRuntime.EnableAutonomy` IS the call site for a
    ///     static gate — excluding it (as a naive whole-word lookbehind does) marks nearly every
    ///     gate dead. Only a trailing word-boundary is needed to keep `Mission` from matching
    ///     inside `MissionEvaluator`.
    ///  2. A declaring file may also be a consumer. `HomelabRepository` declares its schema AND
    ///     queries it; requiring an external file would mark every table dead. The declaring file
    ///     counts once the symbol appears more times than the declaration itself
    ///     (<paramref name="declarationOccurrences"/>), or never when that is int.MaxValue — used
    ///     for gates, where "read only inside the class that declares it" is precisely the defect.
    /// </summary>
    internal static List<string> FindCallSites(IReadOnlyDictionary<string, string> sources,
        string symbol, string? declaringFile = null, int declarationOccurrences = 1)
    {
        var pattern = new Regex(@"(?<![\w])" + Regex.Escape(symbol) + @"(?![\w])");
        var found = new List<string>();
        foreach (var (file, text) in sources)
        {
            var hits = pattern.Matches(text).Count;
            if (hits == 0) continue;
            if (file == declaringFile && hits <= declarationOccurrences) continue;
            found.Add(file);
        }
        return found.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    // ---- roles ------------------------------------------------------------------------------------

    private static IEnumerable<InventoryEntry> Roles(IReadOnlyDictionary<string, string> sources)
    {
        foreach (var role in AntRegistry.Roles)
        {
            var kind = AntExecutionCatalog.KindOf(role.RoleId);
            // A role's "call site" is a handler: something that constructs its ant or names it in a
            // dispatch/registration path. Control-plane and deterministic-service roles are not
            // mission agents and are not expected to have one.
            var sites = FindCallSites(sources, $"\"{role.RoleId}\"");
            yield return new InventoryEntry(Kinds_Role, role.RoleId,
                $"{kind} · enabled={role.Enabled} · executable={role.Executable} · workers={role.Workers.Count}",
                sites);
        }
    }

    // ---- feature gates ----------------------------------------------------------------------------

    private static IEnumerable<InventoryEntry> FeatureGates(string repoRoot,
        IReadOnlyDictionary<string, string> sources)
    {
        const string runtimeFile = "src/Anthill.Core/Configuration/AnthillRuntime.cs";
        var runtimeSource = sources.GetValueOrDefault(runtimeFile, "");
        // Public static bool gates declared on AnthillRuntime.
        foreach (Match m in Regex.Matches(runtimeSource, @"public static bool (\w+)\s*="))
        {
            var gate = m.Groups[1].Value;
            // A gate is CONSUMED when something OUTSIDE AnthillRuntime reads it — declaring the
            // field and assigning it from config are both inside the declaring file, and a gate
            // that never leaves that file is a switch wired to nothing.
            var sites = FindCallSites(sources, gate, runtimeFile, declarationOccurrences: int.MaxValue);
            yield return new InventoryEntry(Kinds_FeatureGate, gate, "public static bool on AnthillRuntime", sites);
        }
    }

    // ---- endpoints --------------------------------------------------------------------------------

    private static IEnumerable<InventoryEntry> Endpoints(IReadOnlyDictionary<string, string> sources)
    {
        foreach (var (file, text) in sources.Where(kv => kv.Key.Contains("Anthill.Api/")))
        {
            foreach (Match m in Regex.Matches(text, @"app\.Map(Get|Post|Put|Delete)\(\s*""([^""]+)"""))
            {
                var verb = m.Groups[1].Value.ToUpperInvariant();
                var route = m.Groups[2].Value;
                // An endpoint IS its own call site — it is the outermost production surface.
                yield return new InventoryEntry(Kinds_Endpoint, $"{verb} {route}", $"declared in {file}",
                    new[] { file });
            }
        }
    }

    // ---- tables -----------------------------------------------------------------------------------

    private static IEnumerable<InventoryEntry> Tables(string repoRoot,
        IReadOnlyDictionary<string, string> sources)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, text) in sources)
        {
            foreach (Match m in Regex.Matches(text, @"CREATE TABLE IF NOT EXISTS (\w+)"))
            {
                var table = m.Groups[1].Value;
                if (!seen.Add(table)) continue;
                // A table's consumers are the files that read or write it. The schema file counts
                // too once it queries the table beyond the CREATE statement — repositories
                // legitimately declare and use their own tables.
                var sites = FindCallSites(sources, table, file, declarationOccurrences: 1);
                yield return new InventoryEntry(Kinds_Table, table, $"declared in {file}", sites);
            }
        }
    }

    // ---- background loops -------------------------------------------------------------------------

    private static IEnumerable<InventoryEntry> BackgroundLoops(IReadOnlyDictionary<string, string> sources)
    {
        foreach (var (file, text) in sources)
        {
            foreach (Match m in Regex.Matches(text, @"new HomelabScheduledJob\(\s*""([^""]+)"""))
                yield return new InventoryEntry(Kinds_BackgroundLoop, m.Groups[1].Value,
                    $"scheduler job registered in {file}", new[] { file });
        }
    }

    // ---- rendering --------------------------------------------------------------------------------

    public static string ToMarkdown(RuntimeInventoryReport report)
    {
        var lines = new List<string>
        {
            "# ANTHILL Runtime Inventory",
            "",
            $"Generated {report.GeneratedAt} for v{report.Version}.",
            "",
            "What the runtime declares, and who consumes it. Generated by `RuntimeInventory.Build`;",
            "gaps are enforced by `CallSiteAudit`. Tests are deliberately NOT counted as consumers.",
            "",
            $"**Declarations:** {report.Entries.Count} · **without a production consumer:** {report.Orphans.Count}",
            "",
        };
        foreach (var group in report.ByKind.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            lines.Add($"## {group.Key} ({group.Count()})");
            lines.Add("");
            foreach (var e in group.OrderBy(x => x.Name, StringComparer.Ordinal))
                lines.Add($"- `{e.Name}` — {e.Detail} · consumers: "
                        + (e.Orphaned ? "**NONE**" : $"{e.CallSites.Count}"));
            lines.Add("");
        }
        return string.Join("\n", lines);
    }

    public static (string JsonPath, string MarkdownPath) Write(RuntimeInventoryReport report, string? outputDir = null)
    {
        var dir = outputDir ?? AnthillRuntime.PathFromScript("data/reports");
        Directory.CreateDirectory(dir);
        var jsonPath = Path.Combine(dir, "runtime-inventory.json");
        var mdPath = Path.Combine(dir, "runtime-inventory.md");
        File.WriteAllText(jsonPath, Json.Dumps(report, indented: true));
        File.WriteAllText(mdPath, ToMarkdown(report));
        return (jsonPath, mdPath);
    }
}
