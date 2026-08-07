namespace Anthill.SDK.Tools;

/// <summary>
/// The settings a tool consults when it RUNS. v3.8.11.
///
/// An interface with live-reading properties, not a snapshot record — and that is the whole design
/// decision, arrived at by measuring rather than by copying the pattern that worked for the homelab.
///
/// <c>HomelabOptions</c> could be a record because its values are read once and describe where the
/// process lives. These are different: they are CAPABILITY GATES, and the colony already gates them
/// twice on purpose. <c>RuntimeOptions</c> decides at composition time whether a tool is registered
/// at all; then the tool re-checks at call time, so a tool that somehow reached the registry still
/// refuses to act. That second check is defence in depth on the question of whether an agent may run
/// a shell command or write to disk.
///
/// A snapshot would silently collapse the second check into the first. Worse, the fields behind
/// these are mutable statics that the test suite toggles — so a captured value would make a test
/// that flips <c>EnableShellTool</c> pass while the production path it is meant to protect reads
/// something else entirely. Live reads keep the two checks genuinely independent.
///
/// Only the MUTABLE settings appear here. <c>MaxFileReadChars</c>, <c>MaxDirectoryItems</c>,
/// <c>WebSearchProvider</c>, <c>MaxWebResults</c> and <c>WebSearchTimeoutSeconds</c> are <c>const</c>
/// in the runtime and cannot vary, so putting them behind an interface would suggest a flexibility
/// that does not exist.
/// </summary>
public interface IToolRuntimeOptions
{
    /// <summary>Reading the filesystem at all. Gates directory listing and file reads.</summary>
    bool FileToolsEnabled { get; }

    /// <summary>Writing to the filesystem. Off by default — the colony reads before it writes.</summary>
    bool FileWritingEnabled { get; }

    /// <summary>Running shell commands. Off by default, and the highest-consequence gate here.</summary>
    bool ShellToolEnabled { get; }

    /// <summary>Outbound web search. Off by default.</summary>
    bool WebSearchEnabled { get; }

    /// <summary>Applying patches to the working tree. Off by default.</summary>
    bool PatchApplicationEnabled { get; }

    /// <summary>Where a patch may write. A suffix outside this set is refused.</summary>
    IReadOnlySet<string> PatchAllowedSuffixes { get; }

    /// <summary>Suffixes no file tool may touch — the colony's own database among them.</summary>
    IReadOnlySet<string> BlockedFileSuffixes { get; }

    /// <summary>Resolved root for relative paths.</summary>
    string ScriptDirectory { get; }

    /// <summary>Where a patch's pre-change copy is kept, so an application can be reverted.</summary>
    string BackupDirectory { get; }
}
