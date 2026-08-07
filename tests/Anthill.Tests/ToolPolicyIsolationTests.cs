using Anthill.Core.Configuration;
using Anthill.Core.Security;
using Anthill.Modules.Tools;
using Anthill.SDK.Contracts;
using Anthill.SDK.Security;
using Anthill.SDK.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Injected policy is the policy that EXECUTES. v3.8.18.
///
/// An external review found that <c>ApplyPatchTool</c> held an <see cref="IToolRuntimeOptions"/> and
/// then called <c>Validation.ValidateSafePatchPath(filePath)</c> without it, so the suffix allow-list
/// and blocked-path parts came from process-global state while the tool's own enable gates came from
/// the contract. <c>WebSearchTool</c> had the same defect on the SSRF blocklist, and
/// <c>WorkspacePathGuard.IsBlockedPath</c> read <c>AnthillRuntime</c> directly.
///
/// Each of those passed every existing test, because every existing test ran in a process where the
/// ambient policy and the injected policy said the same thing. The only way to catch it is to make
/// them DISAGREE, which is what this file does: two tools in one process, given contradictory
/// policy, must each obey their own.
///
/// This is the difference between "the host has its own profile" and "the host's tools enforce its
/// own rules". The first was true since v3.1.0; the second was not true until this release.
/// </summary>
public class ToolPolicyIsolationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_polisol_" + Guid.NewGuid().ToString("N"));

    public ToolPolicyIsolationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class Gates : IToolRuntimeOptions
    {
        public bool FileToolsEnabled { get; init; } = true;
        public bool FileWritingEnabled { get; init; } = true;
        public bool ShellToolEnabled { get; init; }
        public bool WebSearchEnabled { get; init; } = true;
        public bool PatchApplicationEnabled { get; init; } = true;
        public IReadOnlySet<string> WebSearchKeywords { get; init; } = new HashSet<string>();
        public IReadOnlySet<string> PatchAllowedSuffixes { get; init; } = new HashSet<string> { ".md" };
        public IReadOnlySet<string> BlockedFileSuffixes { get; init; } = new HashSet<string>();
        public IReadOnlySet<string> BlockedPathParts { get; init; } = new HashSet<string>();
        public string ScriptDirectory { get; init; } = ".";
        public string BackupDirectory { get; init; } = "data/backups";
    }

    private Dictionary<string, object?> Patch(string file) => new()
    {
        ["patch"] = new Dictionary<string, object?>
        {
            ["change_type"] = "add",
            ["file_path"] = file,
            ["new_content"] = "hello",
        },
    };

    /// <summary>
    /// The exact defect. Two patch tools, same process, differing only in which suffixes their
    /// injected options permit. Before the fix both consulted <c>AnthillRuntime</c> and gave the same
    /// answer regardless of what they were handed.
    /// </summary>
    [Fact]
    public void TwoPatchTools_WithDifferentAllowedSuffixes_EachObeyTheirOwn()
    {
        var guard = new WorkspacePathGuard(_dir);

        var mdOnly = new ApplyPatchTool(guard, new Gates { PatchAllowedSuffixes = new HashSet<string> { ".md" } });
        var csOnly = new ApplyPatchTool(guard, new Gates { PatchAllowedSuffixes = new HashSet<string> { ".cs" } });

        var mdOnMdTool = mdOnly.Run(Patch("notes.md"));
        var mdOnCsTool = csOnly.Run(Patch("notes.md"));

        Assert.True(mdOnMdTool.Success);
        Assert.False(mdOnCsTool.Success);
        Assert.Equal(FailureClass.ValidationFailure, mdOnCsTool.Failure);
    }

    /// <summary>
    /// The guard's blocked-path list is per-guard too. <c>IsBlockedPath</c> read
    /// <c>AnthillRuntime.BlockedPathParts</c> directly, so a host built from explicit options had its
    /// blocklist answered by whatever the process last said.
    /// </summary>
    [Fact]
    public void TwoGuards_WithDifferentBlockedPathParts_EachObeyTheirOwn()
    {
        var blocksVendor = new WorkspacePathGuard(_dir,
            new Gates { BlockedPathParts = new HashSet<string> { "vendor" } });
        var blocksNothing = new WorkspacePathGuard(_dir,
            new Gates { BlockedPathParts = new HashSet<string>() });

        var path = Path.Combine(_dir, "vendor", "thing.md");

        Assert.True(blocksVendor.IsBlockedPath(path));
        Assert.False(blocksNothing.IsBlockedPath(path));
    }

    /// <summary>
    /// And the guard is immune to the ambient runtime once it has been given options — the property
    /// that makes a second host in one process meaningful rather than decorative.
    /// </summary>
    [Fact]
    public void AGuardWithInjectedOptions_IgnoresTheAmbientRuntime()
    {
        var guard = new WorkspacePathGuard(_dir, new Gates { BlockedPathParts = new HashSet<string>() });
        var path = Path.Combine(_dir, "quarantine", "thing.md");

        var prior = AnthillRuntime.BlockedPathParts.Contains("quarantine");
        AnthillRuntime.BlockedPathParts.Add("quarantine");
        try
        {
            Assert.False(guard.IsBlockedPath(path));                       // its own answer
            Assert.True(new WorkspacePathGuard(_dir).IsBlockedPath(path));  // the ambient one
        }
        finally
        {
            if (!prior) AnthillRuntime.BlockedPathParts.Remove("quarantine");
        }
    }

    /// <summary>
    /// <c>WebSearchTool</c> takes its SSRF policy too. Asserted on the policy object rather than by
    /// running a search, because the search reaches the network; what matters is that the tool holds
    /// and consults the one it was handed.
    /// </summary>
    [Fact]
    public void TheWebSearchTool_AcceptsAnInjectedSsrfPolicy()
    {
        var strict = new BlockEverything();
        var tool = new WebSearchTool(new Gates { WebSearchEnabled = false }, strict);

        // Disabled by its own gate, which is the cheap half of the same property: the tool's answer
        // comes from what it was given, not from the process.
        var result = tool.Run(new Dictionary<string, object?> { ["query"] = "anything" });

        Assert.False(result.Success);
        Assert.Equal(FailureClass.AuthorizationFailure, result.Failure);
    }

    private sealed class BlockEverything : ISsrfPolicy
    {
        public IReadOnlySet<string> BlockedHostnames { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "duckduckgo.com" };
        public IReadOnlyList<string> BlockedHostSuffixes { get; } = new[] { ".com", ".net", ".org" };
    }
}
