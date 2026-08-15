using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anthill.SDK.Common;

/// <summary>
/// The durable transaction around writes to the operator's real tree. v0.3.8.62, PLAN.md §1b S4.
///
/// The v0.3.8.57 guarantee — "a patch set applies as a unit or not at all" — held only while
/// nothing failed mid-write. A `WriteAllText` that truncated before throwing was unrecoverable
/// (the backup path died with the exception), a crash mid-batch left no record that a batch was
/// ever in flight, and rollback overwrote whatever was at the path without asking whether it was
/// still the thing that had been applied. This class is the missing bookkeeping:
///
/// <list type="bullet">
/// <item>The JOURNAL is written durably BEFORE the first mutation, and updated before each one:
/// a crash at any instant leaves a journal that says exactly which files were touched, where
/// their pre-apply bytes are, and what was written.</item>
/// <item>Writes are STAGED: content goes to a temporary in the same directory and lands by an
/// atomic move, so a target file is never half-written — it holds the old bytes or the new ones,
/// nothing between.</item>
/// <item>Rollback is HASH-CHECKED: a file is restored only while its current bytes are the bytes
/// this transaction wrote. Anything else changed since — newer work — and destroying it to
/// restore older state is not "rollback", it is data loss wearing rollback's name.</item>
/// <item>An incomplete rollback is a durable <c>ROLLBACK_FAILED</c> state, not a log line: the
/// marker survives restarts and <see cref="HasRollbackFailure"/> lets auto-apply refuse to run
/// until an operator has looked.</item>
/// <item><see cref="Recover"/> replays incomplete journals at startup, with the same hash rule.</item>
/// </list>
/// </summary>
public sealed class ApplyTransaction
{
    /// <summary>Under the workspace root; also where backups and the failure marker live.</summary>
    public const string JournalDirectoryName = ".anthill/apply-journal";

    /// <summary>
    /// Test seam for fault injection: invoked with the target path immediately before the atomic
    /// swap. A test that throws here simulates disk-full / permission-revoked at the worst moment
    /// — after the temp write, before the target changes. Never set in production.
    /// </summary>
    internal static Func<string, Exception?>? WriteFault;

    public sealed class Entry
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        /// <summary>add | modify | delete | rename</summary>
        [JsonPropertyName("op")] public string Op { get; set; } = "";
        [JsonPropertyName("destination")] public string? Destination { get; set; }
        /// <summary>Null when the file did not exist before this transaction.</summary>
        [JsonPropertyName("pre_hash")] public string? PreHash { get; set; }
        [JsonPropertyName("backup")] public string? Backup { get; set; }
        /// <summary>Hash of what this transaction left at <see cref="Path"/> (or Destination for
        /// a rename). Null while the mutation has not happened yet — which is exactly what a
        /// recovery pass needs to know.</summary>
        [JsonPropertyName("post_hash")] public string? PostHash { get; set; }
        [JsonPropertyName("applied")] public bool Applied { get; set; }
    }

    private sealed class JournalDoc
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        /// <summary>open | committed | rollback_failed</summary>
        [JsonPropertyName("state")] public string State { get; set; } = "open";
        [JsonPropertyName("started_at")] public string StartedAt { get; set; } = "";
        [JsonPropertyName("note")] public string Note { get; set; } = "";
        [JsonPropertyName("entries")] public List<Entry> Entries { get; set; } = new();
    }

    public sealed record RollbackReport(
        int Restored, IReadOnlyList<string> Conflicts, IReadOnlyList<string> Failures)
    {
        /// <summary>True when every touched file was either restored byte-identically or was
        /// already back at its pre-apply bytes. Conflicts (newer work preserved) and failures
        /// both mean the tree is NOT the pre-apply tree, and the caller must say so.</summary>
        public bool Clean => Conflicts.Count == 0 && Failures.Count == 0;
    }

    private readonly string _root;
    private readonly string _dir;
    private readonly string _journalPath;
    private readonly JournalDoc _doc;

    public string Id => _doc.Id;
    public IReadOnlyList<Entry> Entries => _doc.Entries;

    private ApplyTransaction(string root, JournalDoc doc)
    {
        _root = root;
        _dir = System.IO.Path.Combine(root, JournalDirectoryName);
        _journalPath = System.IO.Path.Combine(_dir, doc.Id + ".journal.json");
        _doc = doc;
    }

    /// <summary>Open a transaction and write its journal durably before anything mutates.</summary>
    public static ApplyTransaction Begin(string workspaceRoot, string? note = null)
    {
        var doc = new JournalDoc
        {
            Id = AnthillTime.TimestampId() + "-" + Guid.NewGuid().ToString("N")[..8],
            StartedAt = AnthillTime.NowUtc().ToIso(),
            Note = note ?? "",
        };
        var tx = new ApplyTransaction(System.IO.Path.GetFullPath(workspaceRoot), doc);
        Directory.CreateDirectory(tx._dir);
        tx.PersistJournal();
        return tx;
    }

    // ---- The mutations -------------------------------------------------------------------------

    /// <summary>
    /// Record intent, back up, then write staged-atomically. The journal knows about the file
    /// BEFORE the target can change, so no failure mode loses the recovery metadata — returning
    /// it in an exception was S4's original sin.
    /// </summary>
    public Entry WriteFile(string path, string content, string op = "modify")
    {
        var entry = Stage(path, op, destination: null);
        AtomicWrite(path, content);
        entry.PostHash = HashText(content);
        entry.Applied = true;
        PersistJournal();
        return entry;
    }

    public Entry DeleteFile(string path)
    {
        var entry = Stage(path, "delete", destination: null);
        File.Delete(path);
        entry.PostHash = null;   // "absent" is the post-state
        entry.Applied = true;
        PersistJournal();
        return entry;
    }

    public Entry MoveFile(string path, string destination)
    {
        var entry = Stage(path, "rename", destination);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        var fault = WriteFault?.Invoke(destination);
        if (fault is not null) throw fault;
        File.Move(path, destination);
        entry.PostHash = HashFile(destination);
        entry.Applied = true;
        PersistJournal();
        return entry;
    }

    /// <summary>
    /// Stage for a mutation SOMETHING ELSE will perform (the apply tool, which owns path guards
    /// and patch semantics). Records pre-state and backup durably before the caller mutates;
    /// the caller reports back through <see cref="MarkApplied"/>. This is what lets the runner
    /// journal a batch without re-implementing the tool.
    /// </summary>
    public Entry StageExternal(string path, string op, string? destination = null) =>
        Stage(path, op, destination);

    /// <summary>The external mutation landed; record what it left behind.</summary>
    public void MarkApplied(Entry entry, string? postHash)
    {
        entry.PostHash = postHash;
        entry.Applied = true;
        PersistJournal();
    }

    private Entry Stage(string path, string op, string? destination)
    {
        var entry = new Entry
        {
            Path = System.IO.Path.GetFullPath(path),
            Op = op,
            Destination = destination is null ? null : System.IO.Path.GetFullPath(destination),
            PreHash = HashFile(path),
        };
        if (entry.PreHash is not null)
        {
            var backup = System.IO.Path.Combine(_dir, $"{_doc.Id}.{_doc.Entries.Count}.bak");
            File.Copy(entry.Path, backup, overwrite: true);
            entry.Backup = backup;
        }
        _doc.Entries.Add(entry);
        PersistJournal();   // intent is durable before the mutation happens
        return entry;
    }

    // ---- Outcomes ------------------------------------------------------------------------------

    /// <summary>The batch is being kept: the journal and backups have served their purpose.</summary>
    public void Commit()
    {
        _doc.State = "committed";
        PersistJournal();
        Cleanup();
    }

    /// <summary>
    /// Put the pre-apply tree back — but ONLY where this transaction's bytes are still the
    /// current bytes. Reverse order, so a rename chain unwinds the way it wound.
    /// </summary>
    public RollbackReport Rollback()
    {
        var report = RollbackEntries(_doc.Entries, _dir);
        if (report.Clean) { Cleanup(); return report; }

        _doc.State = "rollback_failed";
        PersistJournal();
        MarkRollbackFailed(_root, _doc.Id,
            $"restored {report.Restored}; conflicts: {string.Join("; ", report.Conflicts)}; "
          + $"failures: {string.Join("; ", report.Failures)}");
        return report;
    }

    private static RollbackReport RollbackEntries(List<Entry> entries, string journalDir)
    {
        var restored = 0;
        var conflicts = new List<string>();
        var failures = new List<string>();

        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            try
            {
                // Where did this transaction leave bytes, and are they still there untouched?
                var livePath = e.Op == "rename" ? (e.Destination ?? e.Path) : e.Path;
                var currentHash = HashFile(livePath);

                if (!e.Applied && currentHash == e.PreHash) continue;   // never mutated: nothing to do

                if (currentHash != e.PostHash)
                {
                    // The pre-apply state may ALREADY be back (an earlier partial recovery, or the
                    // failed write never landed). That is success, not conflict.
                    if (HashFile(e.Path) == e.PreHash && (e.Op != "rename" || HashFile(e.Destination!) is null))
                        continue;
                    conflicts.Add($"{livePath}: changed after apply — left alone (newer work is not rollback's to destroy)");
                    continue;
                }

                switch (e.Op)
                {
                    case "add":
                        File.Delete(e.Path);
                        break;
                    case "rename":
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(e.Path)!);
                        File.Move(e.Destination!, e.Path, overwrite: true);
                        break;
                    default:   // modify, delete — put the pre-apply bytes back atomically
                        if (e.Backup is null || !File.Exists(e.Backup))
                        {
                            failures.Add($"{e.Path}: backup missing ({e.Backup ?? "none recorded"})");
                            continue;
                        }
                        AtomicCopy(e.Backup, e.Path);
                        break;
                }
                restored++;
            }
            catch (Exception ex)
            {
                failures.Add($"{e.Path}: {ex.Message}");
            }
        }
        return new RollbackReport(restored, conflicts, failures);
    }

    // ---- Recovery and the durable failure state ------------------------------------------------

    /// <summary>
    /// Replay every incomplete journal under <paramref name="workspaceRoot"/> — the startup half
    /// of durability. A journal in state "open" is a batch interrupted by a crash; its entries are
    /// rolled back under the same hash rule as a live rollback. Returns a human-readable line per
    /// journal handled.
    /// </summary>
    public static IReadOnlyList<string> Recover(string workspaceRoot)
    {
        var results = new List<string>();
        var dir = System.IO.Path.Combine(System.IO.Path.GetFullPath(workspaceRoot), JournalDirectoryName);
        if (!Directory.Exists(dir)) return results;

        foreach (var journalPath in Directory.GetFiles(dir, "*.journal.json"))
        {
            JournalDoc? doc;
            try { doc = JsonSerializer.Deserialize<JournalDoc>(File.ReadAllText(journalPath)); }
            catch (Exception e) { results.Add($"{journalPath}: unreadable journal ({e.Message}) — left for the operator"); continue; }
            if (doc is null || doc.State != "open") continue;

            var report = RollbackEntries(doc.Entries, dir);
            if (report.Clean)
            {
                foreach (var e in doc.Entries.Where(e => e.Backup is not null && File.Exists(e.Backup)))
                    TryDelete(e.Backup!);
                TryDelete(journalPath);
                results.Add($"{doc.Id}: interrupted batch of {doc.Entries.Count} rolled back cleanly");
            }
            else
            {
                doc.State = "rollback_failed";
                try { File.WriteAllText(journalPath, JsonSerializer.Serialize(doc)); } catch { }
                MarkRollbackFailed(workspaceRoot, doc.Id,
                    $"startup recovery: restored {report.Restored}; conflicts: {string.Join("; ", report.Conflicts)}; failures: {string.Join("; ", report.Failures)}");
                results.Add($"{doc.Id}: recovery INCOMPLETE — rollback_failed marker written");
            }
        }
        return results;
    }

    /// <summary>True while any transaction under this root ended in an incomplete rollback. The
    /// caller that writes to live trees (auto-apply) must treat this as a halt.</summary>
    public static bool HasRollbackFailure(string workspaceRoot)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetFullPath(workspaceRoot), JournalDirectoryName);
        return Directory.Exists(dir) && Directory.GetFiles(dir, "ROLLBACK_FAILED-*").Length > 0;
    }

    private static void MarkRollbackFailed(string workspaceRoot, string id, string detail)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetFullPath(workspaceRoot), JournalDirectoryName);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(System.IO.Path.Combine(dir, $"ROLLBACK_FAILED-{id}.txt"),
                $"Transaction {id} could not be fully rolled back.\n{detail}\n"
              + "The tree may hold a partial apply. Inspect, resolve, then delete this file to re-enable auto-apply.\n");
        }
        catch { /* the journal itself still records rollback_failed */ }
    }

    // ---- Primitives ----------------------------------------------------------------------------

    /// <summary>Temp-in-same-directory then atomic move: the target is never half-written.</summary>
    public static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        var fault = WriteFault?.Invoke(path);
        if (fault is not null) { TryDelete(temp); throw fault; }
        File.Move(temp, path, overwrite: true);
    }

    private static void AtomicCopy(string source, string destination)
    {
        var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, destination, overwrite: true);
    }

    private void PersistJournal()
    {
        var temp = _journalPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_doc, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _journalPath, overwrite: true);
    }

    private void Cleanup()
    {
        foreach (var e in _doc.Entries.Where(e => e.Backup is not null)) TryDelete(e.Backup!);
        TryDelete(_journalPath);
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    /// <summary>Null for "file absent" — a distinct state, not an empty hash.</summary>
    public static string? HashFile(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    public static string HashText(string content) =>
        Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(content))).ToLowerInvariant();
}
