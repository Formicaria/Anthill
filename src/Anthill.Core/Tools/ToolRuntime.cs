using Anthill.Core.Configuration;
using Anthill.SDK.Tools;

namespace Anthill.Core.Tools;

/// <summary>
/// The colony's live tool settings, and the default every tool falls back to. v3.8.11.
///
/// This is the bridge between <see cref="AnthillRuntime"/> — mutable statics the operator surface
/// and the test suite both write — and <see cref="IToolRuntimeOptions"/>, which is what a tool in a
/// module will hold once the implementations move out.
///
/// Every property READS THROUGH on each access. Capturing any of them would break the colony's
/// two-level gating: <c>RuntimeOptions</c> gates whether a tool is registered, and the tool re-checks
/// when it runs. Those are meant to be independent, and a cached value silently makes them one.
/// </summary>
public sealed class ToolRuntime : IToolRuntimeOptions
{
    /// <summary>
    /// The default a tool uses when none is injected — which is every tool today, since they are
    /// all still constructed by <c>Queen.BuildToolRegistry</c>. A module-supplied tool will be
    /// handed one explicitly through its module context.
    /// </summary>
    public static readonly ToolRuntime Live = new();

    private ToolRuntime() { }

    public bool FileToolsEnabled => AnthillRuntime.EnableFileTools;

    public bool FileWritingEnabled => AnthillRuntime.EnableFileWriting;

    public bool ShellToolEnabled => AnthillRuntime.EnableShellTool;

    public bool WebSearchEnabled => AnthillRuntime.EnableWebSearch;

    public bool PatchApplicationEnabled => AnthillRuntime.EnablePatchApplication;

    public IReadOnlySet<string> PatchAllowedSuffixes => AnthillRuntime.PatchAllowedSuffixes;

    public IReadOnlySet<string> BlockedFileSuffixes => AnthillRuntime.BlockedFileSuffixes;

    public IReadOnlySet<string> BlockedPathParts => AnthillRuntime.BlockedPathParts;

    public string ScriptDirectory => AnthillRuntime.ScriptDir;

    public string BackupDirectory => AnthillRuntime.BackupDir;
}
