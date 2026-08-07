using System.Text.RegularExpressions;
using Anthill.SDK.Tools;

namespace Anthill.SDK.Common;

/// <summary>
/// Input validators for ids and patch paths. These are the defensive boundary the
/// API and approval/patch flows lean on; they throw <see cref="ArgumentException"/>
/// with the same messages the Python validators produced.
///
/// v3.8.12 — moved from <c>Anthill.Core.Common</c>. The id length caps were <c>const</c> in
/// <c>AnthillRuntime</c> and are declared here now, for the reason
/// <see cref="IToolRuntimeOptions"/> gives for leaving consts out of interfaces: a value that cannot
/// vary should not be dressed as one that can. <c>AnthillRuntime</c> re-exports these so the
/// operator-facing surface is unchanged and there is still one declaration.
///
/// <see cref="ValidateSafePatchPath"/> is the only method here that reads mutable settings, and it
/// takes them as an optional <see cref="IToolRuntimeOptions"/> — the same contract the seven tools
/// already hold, rather than a second interface re-declaring
/// <see cref="IToolRuntimeOptions.PatchAllowedSuffixes"/> and
/// <see cref="IToolRuntimeOptions.BlockedFileSuffixes"/> and inviting the two to drift.
/// </summary>
public static partial class Validation
{
    /// <summary>Length cap for an approval id. Was <c>AnthillRuntime.ApprovalIdMaxChars</c>.</summary>
    public const int ApprovalIdMaxChars = 80;

    /// <summary>Length cap for a patch id. Was <c>AnthillRuntime.PatchIdMaxChars</c>.</summary>
    public const int PatchIdMaxChars = 80;

    /// <summary>Length cap for a source id. Was <c>AnthillRuntime.SourceIdMaxChars</c>.</summary>
    public const int SourceIdMaxChars = 80;

    public static bool IsValidUuid(string value) => Guid.TryParse(value, out _);

    public static string ValidateUuidId(string value, string label, int maxChars = 80)
    {
        var cleaned = (value ?? "").Trim();
        if (cleaned.Length == 0) throw new ArgumentException($"Missing {label}.");
        if (cleaned.Length > maxChars) throw new ArgumentException($"{label} is too long.");
        if (!IsValidUuid(cleaned)) throw new ArgumentException($"{label} must be a valid UUID.");
        return cleaned;
    }

    public static string ValidateApprovalId(string value) => ValidateUuidId(value, "approval id", ApprovalIdMaxChars);
    public static string ValidatePatchId(string value) => ValidateUuidId(value, "patch id", PatchIdMaxChars);

    public static string ValidateSourceId(string value)
    {
        var cleaned = (value ?? "").Trim();
        if (cleaned.Length == 0) throw new ArgumentException("Missing source id.");
        if (cleaned.Length > SourceIdMaxChars) throw new ArgumentException("source id is too long.");
        if (!SourceIdPattern().IsMatch(cleaned)) throw new ArgumentException("source id must match src_<24hexchars>.");
        return cleaned;
    }

    /// <summary>
    /// Rejects absolute paths, parent traversal, blocked internal directories, and
    /// disallowed/blocked file types before any patch touches the workspace.
    /// </summary>
    /// <param name="options">
    /// The patch gates to enforce. Null — every call site in the colony today — reads
    /// <see cref="SafetyPolicy.ToolOptions"/> live, falling back to the built-in defaults below if no
    /// composition root has run.
    /// </param>
    public static string ValidateSafePatchPath(string filePath, IToolRuntimeOptions? options = null)
    {
        var cleaned = (filePath ?? "").Trim();
        if (cleaned.Length == 0) throw new ArgumentException("Patch proposal missing file_path.");
        if (Path.IsPathRooted(cleaned)) throw new ArgumentException($"Patch file_path must be relative, not absolute: {cleaned}");

        var gates = options ?? SafetyPolicy.ToolOptions;
        var blockedPathParts = gates?.BlockedPathParts ?? DefaultBlockedPathParts;
        var blockedFileSuffixes = gates?.BlockedFileSuffixes ?? DefaultBlockedFileSuffixes;
        var allowedSuffixes = gates?.PatchAllowedSuffixes ?? DefaultPatchAllowedSuffixes;

        var parts = cleaned.Split('/', '\\');
        if (parts.Contains("..")) throw new ArgumentException($"Patch file_path cannot contain '..': {cleaned}");
        var loweredParts = parts.Select(p => p.ToLowerInvariant()).ToHashSet();
        if (loweredParts.Overlaps(blockedPathParts))
            throw new ArgumentException($"Patch file_path targets blocked internal path: {cleaned}");

        var suffix = Path.GetExtension(cleaned).ToLowerInvariant();
        if (blockedFileSuffixes.Contains(suffix))
            throw new ArgumentException($"Patch file_path targets blocked file type: {suffix}");
        if (!allowedSuffixes.Contains(suffix))
            throw new ArgumentException($"Patch file_path has unsupported file type: {suffix}");
        return cleaned;
    }

    // Mirror AnthillRuntime's declared defaults, so an unconfigured process is never more permissive
    // than a configured one. The core overwrites these via SafetyPolicy.Configure at load.
    private static readonly IReadOnlySet<string> DefaultBlockedPathParts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "data", ".git", "__pycache__", ".venv", "venv", "env", ".mypy_cache", ".pytest_cache" };

    private static readonly IReadOnlySet<string> DefaultBlockedFileSuffixes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".db", ".sqlite", ".sqlite3" };

    private static readonly IReadOnlySet<string> DefaultPatchAllowedSuffixes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".py", ".json", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".log",
            ".csv", ".html", ".css", ".js", ".ts", ".tsx", ".jsx", ".xml",
            // .NET / C# source files — required for self-modification missions
            ".cs", ".csproj", ".sln", ".props", ".targets",
            // Shell / scripting
            ".sh", ".bat", ".ps1", ".cmd",
            // Other common code types
            ".go", ".rs", ".java", ".kt", ".rb", ".php", ".tf", ".hcl", ".sql",
        };

    [GeneratedRegex("^src_[0-9a-f]{24}$")] private static partial Regex SourceIdPattern();
}
