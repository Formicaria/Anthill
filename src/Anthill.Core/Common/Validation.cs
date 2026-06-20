using System.Text.RegularExpressions;
using Anthill.Core.Configuration;

namespace Anthill.Core.Common;

/// <summary>
/// Input validators for ids and patch paths. These are the defensive boundary the
/// API and approval/patch flows lean on; they throw <see cref="ArgumentException"/>
/// with the same messages the Python validators produced.
/// </summary>
public static partial class Validation
{
    public static bool IsValidUuid(string value) => Guid.TryParse(value, out _);

    public static string ValidateUuidId(string value, string label, int maxChars = 80)
    {
        var cleaned = (value ?? "").Trim();
        if (cleaned.Length == 0) throw new ArgumentException($"Missing {label}.");
        if (cleaned.Length > maxChars) throw new ArgumentException($"{label} is too long.");
        if (!IsValidUuid(cleaned)) throw new ArgumentException($"{label} must be a valid UUID.");
        return cleaned;
    }

    public static string ValidateApprovalId(string value) => ValidateUuidId(value, "approval id", AnthillRuntime.ApprovalIdMaxChars);
    public static string ValidatePatchId(string value) => ValidateUuidId(value, "patch id", AnthillRuntime.PatchIdMaxChars);

    public static string ValidateSourceId(string value)
    {
        var cleaned = (value ?? "").Trim();
        if (cleaned.Length == 0) throw new ArgumentException("Missing source id.");
        if (cleaned.Length > AnthillRuntime.SourceIdMaxChars) throw new ArgumentException("source id is too long.");
        if (!SourceIdPattern().IsMatch(cleaned)) throw new ArgumentException("source id must match src_<24hexchars>.");
        return cleaned;
    }

    /// <summary>
    /// Rejects absolute paths, parent traversal, blocked internal directories, and
    /// disallowed/blocked file types before any patch touches the workspace.
    /// </summary>
    public static string ValidateSafePatchPath(string filePath)
    {
        var cleaned = (filePath ?? "").Trim();
        if (cleaned.Length == 0) throw new ArgumentException("Patch proposal missing file_path.");
        if (Path.IsPathRooted(cleaned)) throw new ArgumentException($"Patch file_path must be relative, not absolute: {cleaned}");

        var parts = cleaned.Split('/', '\\');
        if (parts.Contains("..")) throw new ArgumentException($"Patch file_path cannot contain '..': {cleaned}");
        var loweredParts = parts.Select(p => p.ToLowerInvariant()).ToHashSet();
        if (loweredParts.Overlaps(AnthillRuntime.BlockedPathParts))
            throw new ArgumentException($"Patch file_path targets blocked internal path: {cleaned}");

        var suffix = Path.GetExtension(cleaned).ToLowerInvariant();
        if (AnthillRuntime.BlockedFileSuffixes.Contains(suffix))
            throw new ArgumentException($"Patch file_path targets blocked file type: {suffix}");
        if (!AnthillRuntime.PatchAllowedSuffixes.Contains(suffix))
            throw new ArgumentException($"Patch file_path has unsupported file type: {suffix}");
        return cleaned;
    }

    [GeneratedRegex("^src_[0-9a-f]{24}$")] private static partial Regex SourceIdPattern();
}
