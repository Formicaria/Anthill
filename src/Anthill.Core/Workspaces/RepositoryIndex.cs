using System.Security.Cryptography;
using System.Text;
using Anthill.Core.Security;

namespace Anthill.Core.Workspaces;

/// <summary>
/// A declaration found in a file, with the line it is on.
///
/// EVIDENCE, NOT AUTHORITY — and the distinction is the whole design. This comes from pattern
/// matching, not from a compiler: it will occasionally name something inside a comment or a string,
/// and it will miss declarations written in shapes the patterns do not cover. That is acceptable
/// precisely because of what it is used for. The index POINTS; the agent then reads the file and
/// sees for itself.
///
/// What would not be acceptable is claiming more. A symbol index presented as authoritative gets
/// believed: an agent told "this function is declared nowhere" stops looking, and an agent told
/// "these are all the callers" makes a change on the strength of a list that was never complete.
/// So every answer built on these carries its path and line, and none of them says "all".
/// </summary>
public sealed record IndexedSymbol(string Name, string Kind, int Line);

/// <summary>One indexed file. Excerpts an agent acts on trace back to these fields.</summary>
public sealed record IndexedFile(
    string Path,
    string Language,
    long Bytes,
    int Lines,
    string ContentHash)
{
    /// <summary>Declarations found in this file. Empty for languages with no declared patterns.</summary>
    public IReadOnlyList<IndexedSymbol> Symbols { get; init; } = Array.Empty<IndexedSymbol>();
}

/// <summary>
/// v3.6.0 — a durable inventory of what is in a repository, keyed to the revision it describes.
///
/// The goal the phase states is "answer 'where is this handled' from evidence rather than a guess,
/// WITHOUT reading the repository into the context window". So this is deliberately not a
/// context-stuffing preprocessor: it is a queryable record, and the agent decides what it needs.
///
/// STALE MUST BE DETECTABLE, NOT MERELY OLD — the phase's exit gate, and the reason every entry
/// carries a content hash rather than a timestamp. A mtime tells you an index is old; it cannot tell
/// you whether the answer it would give is still true. A revision plus per-file content hashes can:
/// same revision and same hashes means the same answer, and anything else reports itself stale
/// rather than answering confidently from a repository that has moved.
///
/// Bounded by construction. A large repository degrades to a TRUNCATED inventory rather than
/// failing, because "the index did not build" and "the index covers the first N files" call for
/// completely different operator responses, and only the second is still useful.
/// </summary>
public sealed record RepositoryIndex
{
    /// <summary>Files beyond this are not indexed; the index says so rather than silently omitting them.</summary>
    public const int MaxFiles = 20_000;

    /// <summary>Above this a file is inventoried but not line-counted or hashed by content.</summary>
    public const long MaxHashedBytes = 4_000_000;

    public required string WorkspaceId { get; init; }
    public required string Root { get; init; }

    /// <summary>The revision this index DESCRIBES. An index without one cannot claim to be current.</summary>
    public required string Revision { get; init; }

    public required string RepositoryFingerprint { get; init; }
    public IReadOnlyList<IndexedFile> Files { get; init; } = Array.Empty<IndexedFile>();

    /// <summary>True when <see cref="MaxFiles"/> stopped the walk. Reported, never silent.</summary>
    public bool Truncated { get; init; }

    public int BuildMilliseconds { get; init; }
    public DateTime BuiltAt { get; init; } = Common.AnthillTime.NowUtc();

    public long TotalBytes => Files.Sum(f => f.Bytes);

    /// <summary>
    /// Languages present, with file counts — the cheapest useful answer to "what is this repository",
    /// and one an agent can get without a single file read.
    /// </summary>
    public IReadOnlyDictionary<string, int> LanguageCounts =>
        Files.GroupBy(f => f.Language).OrderByDescending(g => g.Count())
             .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>
    /// Whether this index still describes <paramref name="revision"/>.
    ///
    /// Revision equality ALONE is the check here, and deliberately: within one revision the working
    /// tree can still be edited, which is exactly what a mission does, so callers that care about
    /// uncommitted drift compare hashes via <see cref="FileChanged"/>. Conflating the two would make
    /// every mission's own edits read as a corrupt index.
    /// </summary>
    public bool DescribesRevision(string? revision) =>
        Revision.Length > 0 && string.Equals(Revision, revision, StringComparison.Ordinal);

    /// <summary>
    /// Whether the file on disk differs from what was indexed. The precise form of "stale": it
    /// answers about ONE answer rather than about the index as a whole, so a mission editing three
    /// files does not invalidate everything the index knows about the other twenty thousand.
    /// </summary>
    public bool FileChanged(string path, string currentHash) =>
        Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal)) is { } indexed
        && !string.Equals(indexed.ContentHash, currentHash, StringComparison.Ordinal);

    public IndexedFile? Find(string path) =>
        Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal));

    /// <summary>
    /// Where a name is DECLARED, as far as the patterns can tell. Ordered by path so the same
    /// question gives the same answer.
    ///
    /// Returns candidates, never a verdict — see <see cref="IndexedSymbol"/>. An empty result means
    /// "not found by these patterns", which is emphatically not "does not exist".
    /// </summary>
    public IReadOnlyList<(string Path, IndexedSymbol Symbol)> FindSymbol(string? name, bool exact = false)
    {
        if (string.IsNullOrWhiteSpace(name)) return Array.Empty<(string, IndexedSymbol)>();

        return Files
            .SelectMany(f => f.Symbols.Select(s => (f.Path, Symbol: s)))
            .Where(x => exact
                ? string.Equals(x.Symbol.Name, name, StringComparison.Ordinal)
                : x.Symbol.Name.Contains(name!, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .ThenBy(x => x.Symbol.Line)
            .ToList();
    }

    public int SymbolCount => Files.Sum(f => f.Symbols.Count);
}

/// <summary>
/// v3.6.0 — builds a <see cref="RepositoryIndex"/> from a workspace, and never from anywhere else.
///
/// The boundary is the exit gate "no indexing path can read outside the mission workspace boundary",
/// and it is enforced by construction rather than by care: the walk starts at the workspace root and
/// every path is resolved through <see cref="WorkspacePathGuard"/>, the same chokepoint every file
/// tool passes through. An indexer with its own traversal would be a second file-access path that
/// nothing else audits — which is precisely how a containment boundary acquires a hole.
/// </summary>
public static class RepositoryIndexBuilder
{
    /// <summary>Directories that hold other people's code. Indexing them answers questions about dependencies.</summary>
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "target", "vendor",
        ".venv", "__pycache__", ".next", ".vs", ".idea", "packages",
    };

    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp", [".fs"] = "fsharp", [".vb"] = "vb",
        [".js"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript",
        [".ts"] = "typescript", [".tsx"] = "typescript", [".jsx"] = "javascript",
        [".py"] = "python", [".rb"] = "ruby", [".go"] = "go", [".rs"] = "rust",
        [".java"] = "java", [".kt"] = "kotlin", [".swift"] = "swift",
        [".c"] = "c", [".h"] = "c", [".cpp"] = "cpp", [".hpp"] = "cpp",
        [".css"] = "css", [".scss"] = "css", [".html"] = "html",
        [".json"] = "json", [".yml"] = "yaml", [".yaml"] = "yaml",
        [".xml"] = "xml", [".md"] = "markdown", [".sql"] = "sql", [".sh"] = "shell",
        [".csproj"] = "msbuild", [".sln"] = "msbuild", [".props"] = "msbuild",
    };

    public static string LanguageOf(string path) =>
        Languages.GetValueOrDefault(System.IO.Path.GetExtension(path), "other");

    /// <summary>
    /// Index <paramref name="workspace"/>. Never throws: an unreadable file is skipped, and a
    /// workspace that cannot be walked yields an EMPTY index rather than an exception. Indexing is a
    /// convenience over the filesystem, and a convenience that can fail a mission is not one.
    /// </summary>
    public static RepositoryIndex Build(MissionWorkspace workspace)
    {
        var started = DateTime.UtcNow;
        var files = new List<IndexedFile>();
        var truncated = false;

        if (workspace is not null && workspace.Usable && Directory.Exists(workspace.Root))
        {
            var guard = new WorkspacePathGuard(workspace.Root);

            foreach (var full in Walk(workspace.Root))
            {
                if (files.Count >= RepositoryIndex.MaxFiles) { truncated = true; break; }

                try
                {
                    // Through the guard, not around it. A symlink pointing out of the workspace
                    // resolves outside the root and is refused here — the one traversal case a
                    // hand-rolled walk gets wrong, and the reason this does not roll its own.
                    guard.ResolveSafePath(full);
                }
                catch (UnauthorizedAccessException) { continue; }

                var indexed = Describe(workspace.Root, full);
                if (indexed is not null) files.Add(indexed);
            }
        }

        return new RepositoryIndex
        {
            WorkspaceId = workspace?.Id ?? "",
            Root = workspace?.Root ?? "",
            Revision = workspace?.BaseRevision ?? "",
            RepositoryFingerprint = workspace?.RepositoryFingerprint ?? "",
            // Ordered by path so two builds of the same tree produce the same index — the exit gate
            // "an index query returns the same answer for the same revision" is unmeetable if the
            // order of the answer depends on what the filesystem felt like returning.
            Files = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(),
            Truncated = truncated,
            BuildMilliseconds = (int)(DateTime.UtcNow - started).TotalMilliseconds,
        };
    }

    private static IndexedFile? Describe(string root, string full)
    {
        try
        {
            var info = new FileInfo(full);
            var relative = System.IO.Path.GetRelativePath(root, full).Replace('\\', '/');

            // Large files are INVENTORIED but not read. Hashing a 200MB asset to decide whether a
            // code question is stale spends the whole build budget on a file no agent will read.
            if (info.Length > RepositoryIndex.MaxHashedBytes)
                return new IndexedFile(relative, LanguageOf(full), info.Length, 0, "");

            var bytes = File.ReadAllBytes(full);
            var language = LanguageOf(full);
            return new IndexedFile(
                relative,
                language,
                info.Length,
                CountLines(bytes),
                Convert.ToHexString(SHA256.HashData(bytes))[..16])
            {
                Symbols = ExtractSymbols(language, bytes),
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Symbols per file, capped. A generated file with 40,000 declarations is not a map.</summary>
    public const int MaxSymbolsPerFile = 400;

    /// <summary>
    /// Declaration patterns, per language.
    ///
    /// PATTERNS, NOT A PARSER, and the choice is deliberate rather than lazy. A real parser per
    /// language means a compiler dependency per language, versioned against the project being read —
    /// which is a large amount of machinery to answer "where is this declared", a question the agent
    /// verifies by reading the file anyway. See <see cref="IndexedSymbol"/> for why pointing is
    /// enough and claiming authority would be worse.
    ///
    /// Each pattern captures the NAME in group 1 and is anchored to a line start with optional
    /// modifiers, so a mention inside an expression is not mistaken for a declaration. Comments and
    /// strings can still fool it; that is the cost of the trade and the reason nothing here says
    /// "all".
    /// </summary>
    private static readonly Dictionary<string, (string Kind, System.Text.RegularExpressions.Regex Pattern)[]> Patterns =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = new[]
        {
            ("type", Rx(@"^\s*(?:public|internal|private|protected|abstract|sealed|static|partial|\s)*\b(?:class|interface|record|struct|enum)\s+([A-Za-z_]\w*)")),
            ("method", Rx(@"^\s*(?:public|internal|private|protected|static|virtual|override|async|sealed|extern|unsafe|\s)+[\w<>\[\],\?\.]+\s+([A-Za-z_]\w*)\s*\(")),
        },
        ["typescript"] = TsPatterns,
        ["javascript"] = TsPatterns,
        ["python"] = new[]
        {
            ("type", Rx(@"^\s*class\s+([A-Za-z_]\w*)")),
            ("function", Rx(@"^\s*(?:async\s+)?def\s+([A-Za-z_]\w*)")),
        },
        ["go"] = new[]
        {
            ("type", Rx(@"^\s*type\s+([A-Za-z_]\w*)")),
            ("function", Rx(@"^\s*func\s+(?:\([^)]*\)\s*)?([A-Za-z_]\w*)")),
        },
        ["rust"] = new[]
        {
            ("type", Rx(@"^\s*(?:pub\s+)?(?:struct|enum|trait)\s+([A-Za-z_]\w*)")),
            ("function", Rx(@"^\s*(?:pub\s+)?(?:async\s+)?fn\s+([A-Za-z_]\w*)")),
        },
    };

    private static (string, System.Text.RegularExpressions.Regex)[] TsPatterns => new[]
    {
        ("type", Rx(@"^\s*(?:export\s+)?(?:abstract\s+)?(?:class|interface|type|enum)\s+([A-Za-z_$][\w$]*)")),
        ("function", Rx(@"^\s*(?:export\s+)?(?:async\s+)?function\s*\*?\s*([A-Za-z_$][\w$]*)")),
        ("function", Rx(@"^\s*(?:export\s+)?(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*(?:async\s*)?(?:\([^)]*\)|[A-Za-z_$][\w$]*)\s*=>")),
    };

    private static System.Text.RegularExpressions.Regex Rx(string pattern) =>
        new(pattern, System.Text.RegularExpressions.RegexOptions.Compiled
                   | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Declarations in one file. Returns empty rather than throwing for anything unreadable — a
    /// symbol scan that can fail an index build would make the index less reliable than no index.
    /// </summary>
    internal static IReadOnlyList<IndexedSymbol> ExtractSymbols(string language, byte[] bytes)
    {
        if (!Patterns.TryGetValue(language, out var patterns)) return Array.Empty<IndexedSymbol>();

        var symbols = new List<IndexedSymbol>();
        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            // A NUL byte means this is not source, whatever its extension says. Running patterns
            // over a binary produces confident nonsense rather than an error.
            if (text.Contains('\0')) return Array.Empty<IndexedSymbol>();

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length && symbols.Count < MaxSymbolsPerFile; i++)
            {
                var line = lines[i];
                if (line.Length is 0 or > 500) continue;   // minified bundles are not maps either

                foreach (var (kind, pattern) in patterns)
                {
                    var match = pattern.Match(line);
                    if (!match.Success) continue;
                    symbols.Add(new IndexedSymbol(match.Groups[1].Value, kind, i + 1));
                    break;   // one declaration per line; the first pattern that fits wins
                }
            }
        }
        catch (Exception error) when (error is System.Text.RegularExpressions.RegexMatchTimeoutException
                                          or ArgumentException or DecoderFallbackException)
        {
            return symbols;
        }

        return symbols;
    }

    private static int CountLines(byte[] bytes)
    {
        if (bytes.Length == 0) return 0;
        var lines = 1;
        foreach (var b in bytes) if (b == (byte)'\n') lines++;
        return lines;
    }

    private static IEnumerable<string> Walk(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;

            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(dir).OrderByDescending(d => d, StringComparer.Ordinal); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children)
                if (!Skip.Contains(System.IO.Path.GetFileName(child))) stack.Push(child);
        }
    }

    /// <summary>The hash of a file as it is on disk right now, for comparing against the index.</summary>
    public static string HashOf(string full)
    {
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > RepositoryIndex.MaxHashedBytes) return "";
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full)))[..16];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
