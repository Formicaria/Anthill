using System.Text.Json;
using Anthill.Core.Common;
using Anthill.Core.Workspaces;

namespace Anthill.Core.Memory;

/// <summary>
/// v3.6.0 — the repository index, persisted.
///
/// The phase asks for a DURABLE index, and without this it was an in-memory cache: every process
/// start re-walked and re-parsed the whole repository, which on a large one is exactly the cost the
/// index exists to remove. Stored, the first mission after a restart reuses everything that has not
/// changed.
///
/// Keyed by workspace AND revision, so a stored index can never be applied to a tree it does not
/// describe — the same rule the in-memory cache follows, written down where it survives a restart.
/// </summary>
public sealed partial class SqliteMemory
{
    /// <summary>Save (or replace) the index for one workspace+revision.</summary>
    public void SaveRepositoryIndex(RepositoryIndex index)
    {
        if (index is null || index.WorkspaceId.Length == 0 || index.Revision.Length == 0) return;

        lock (_writeLock)
        {
            using var conn = Connect();
            using var tx = conn.BeginTransaction();

            NonQuery(conn, tx,
                @"INSERT INTO repository_index
                    (workspace_id, revision, fingerprint, root, truncated, build_ms, reused_files, built_at)
                  VALUES (@ws, @rev, @fp, @root, @trunc, @ms, @reused, @at)
                  ON CONFLICT(workspace_id, revision) DO UPDATE SET
                    fingerprint=@fp, root=@root, truncated=@trunc, build_ms=@ms,
                    reused_files=@reused, built_at=@at",
                ("@ws", index.WorkspaceId), ("@rev", index.Revision),
                ("@fp", index.RepositoryFingerprint), ("@root", index.Root),
                ("@trunc", index.Truncated ? 1 : 0), ("@ms", index.BuildMilliseconds),
                ("@reused", index.ReusedFiles), ("@at", index.BuiltAt.ToIso()));

            // Replaced wholesale, not merged. A file DELETED since the last index must disappear
            // from it — merging would keep answering questions with a path that no longer exists,
            // and an agent sent to read it gets a confusing failure instead of a correct absence.
            NonQuery(conn, tx, "DELETE FROM repository_index_files WHERE workspace_id=@ws AND revision=@rev",
                ("@ws", index.WorkspaceId), ("@rev", index.Revision));

            foreach (var file in index.Files)
                NonQuery(conn, tx,
                    @"INSERT INTO repository_index_files
                        (workspace_id, revision, path, language, bytes, lines, content_hash, symbols_json)
                      VALUES (@ws, @rev, @path, @lang, @bytes, @lines, @hash, @symbols)",
                    ("@ws", index.WorkspaceId), ("@rev", index.Revision),
                    ("@path", file.Path), ("@lang", file.Language),
                    ("@bytes", file.Bytes), ("@lines", file.Lines),
                    ("@hash", file.ContentHash),
                    ("@symbols", file.Symbols.Count == 0 ? null : Json.SafeDumps(file.Symbols)));

            tx.Commit();
        }
    }

    /// <summary>
    /// The stored index for a workspace+revision, or null.
    ///
    /// Null rather than an empty index for a miss, deliberately: an empty index is a legitimate
    /// answer for an empty repository, and conflating "nothing stored" with "nothing there" would
    /// make the first mission after a restart believe the repository has no files.
    /// </summary>
    public RepositoryIndex? LoadRepositoryIndex(string workspaceId, string revision)
    {
        var header = Query(
            "SELECT * FROM repository_index WHERE workspace_id=@ws AND revision=@rev",
            ("@ws", workspaceId ?? ""), ("@rev", revision ?? "")).FirstOrDefault();
        if (header is null) return null;

        var files = new List<IndexedFile>();
        foreach (var row in Query(
                     "SELECT * FROM repository_index_files WHERE workspace_id=@ws AND revision=@rev ORDER BY path",
                     ("@ws", workspaceId ?? ""), ("@rev", revision ?? "")))
        {
            // One unreadable row must not lose the whole index — the rest is still a correct answer
            // about the files it does describe, and rebuilding from scratch is the fallback anyway.
            try
            {
                var symbolsJson = row.GetValueOrDefault("symbols_json")?.ToString();
                files.Add(new IndexedFile(
                    row.GetValueOrDefault("path")?.ToString() ?? "",
                    row.GetValueOrDefault("language")?.ToString() ?? "other",
                    Convert.ToInt64(row.GetValueOrDefault("bytes") ?? 0L),
                    Convert.ToInt32(row.GetValueOrDefault("lines") ?? 0),
                    row.GetValueOrDefault("content_hash")?.ToString() ?? "")
                {
                    Symbols = string.IsNullOrWhiteSpace(symbolsJson)
                        ? Array.Empty<IndexedSymbol>()
                        : JsonSerializer.Deserialize<List<IndexedSymbol>>(symbolsJson!) ?? new(),
                });
            }
            catch (Exception error) when (error is JsonException or FormatException or InvalidCastException)
            {
                continue;
            }
        }

        return new RepositoryIndex
        {
            WorkspaceId = workspaceId ?? "",
            Revision = revision ?? "",
            Root = header.GetValueOrDefault("root")?.ToString() ?? "",
            RepositoryFingerprint = header.GetValueOrDefault("fingerprint")?.ToString() ?? "",
            Files = files,
            Truncated = Convert.ToInt64(header.GetValueOrDefault("truncated") ?? 0L) != 0,
            BuildMilliseconds = Convert.ToInt32(header.GetValueOrDefault("build_ms") ?? 0),
            ReusedFiles = Convert.ToInt32(header.GetValueOrDefault("reused_files") ?? 0),
            BuiltAt = AnthillTime.ParseIsoOrNow(header.GetValueOrDefault("built_at")?.ToString()),
        };
    }

    /// <summary>
    /// Forget every stored index for a workspace. Called when it is cleaned: an index outliving the
    /// tree it describes is a set of answers about files nobody can read.
    /// </summary>
    public void DeleteRepositoryIndexes(string workspaceId)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, "DELETE FROM repository_index_files WHERE workspace_id=@ws", ("@ws", workspaceId ?? ""));
            NonQuery(conn, null, "DELETE FROM repository_index WHERE workspace_id=@ws", ("@ws", workspaceId ?? ""));
        }
    }
}
