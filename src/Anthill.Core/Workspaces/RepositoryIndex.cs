using System.Security.Cryptography;
using System.Text;
using Anthill.Core.Security;

namespace Anthill.Core.Workspaces;

/// <summary>One indexed file. Excerpts an agent acts on trace back to these fields.</summary>
public sealed record IndexedFile(
    string Path,
    string Language,
    long Bytes,
    int Lines,
    string ContentHash);

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
            return new IndexedFile(
                relative,
                LanguageOf(full),
                info.Length,
                CountLines(bytes),
                Convert.ToHexString(SHA256.HashData(bytes))[..16]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
