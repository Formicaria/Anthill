namespace Anthill.SDK.Tools;

/// <summary>
/// The tool settings that cannot vary. v3.8.16.
///
/// WHY THESE ARE HERE AND NOT ON <see cref="IToolRuntimeOptions"/>. That interface carries the
/// MUTABLE settings, and it says so: putting a `const` behind a live-reading property would suggest
/// a flexibility that does not exist, and would invite someone to add a setter for it. These five
/// are `const` in <c>AnthillRuntime</c> and always have been, so they move as constants — the same
/// treatment the id caps got in v3.8.12 and the summary caps in v3.8.14.
///
/// <c>AnthillRuntime</c> re-exports every one of them, so the operator-facing surface is unchanged
/// and there is still exactly one declaration. That direction matters: the core re-exports the SDK's
/// value rather than the SDK duplicating the core's, because only one of those two arrangements can
/// drift.
/// </summary>
public static class ToolLimits
{
    /// <summary>Characters a single file read returns before truncating.</summary>
    public const int MaxFileReadChars = 5000;

    /// <summary>Entries a directory listing returns before truncating.</summary>
    public const int MaxDirectoryItems = 100;

    /// <summary>Results a web search returns, and the ceiling on what a caller may ask for.</summary>
    public const int MaxWebResults = 5;

    /// <summary>Wall-clock budget for one web search.</summary>
    public const int WebSearchTimeoutSeconds = 12;

    /// <summary>
    /// The search backend, reported on every result so a source can be attributed. A constant
    /// because there is one implementation; when there are two this becomes a setting, and the
    /// change will be visible rather than absorbed.
    /// </summary>
    public const string WebSearchProvider = "duckduckgo_html";
}
