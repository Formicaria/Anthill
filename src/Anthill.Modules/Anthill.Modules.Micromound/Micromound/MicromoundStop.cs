namespace Anthill.Modules.Micromound;

/// <summary>
/// The global kill switch — <c>.anthill/MICROMOUND_STOP</c>, mirroring HOMELAB_STOP.
///
/// SAFETY.md gives every stop three routes: physically at the device, per-mound from ANTHILL, and
/// globally through this file. This is the third. While the file exists, a stop order is forced
/// into every sync response for every mound, and nothing can override it from inside the colony —
/// which is the point of it being a file on disk rather than a row in a table an approval flow
/// could clear.
///
/// Reading it is a file existence check on every sync, deliberately: caching the answer would
/// mean an operator who creates the file has to wait for a cache to notice.
/// </summary>
public static class MicromoundStop
{
    public const string DirectoryName = ".anthill";

    public static string PathFor(MicromoundOptions options) =>
        Path.Combine(options.WorkspaceRootPath, DirectoryName, options.StopFileName);

    /// <summary>True when all mound-directed action is halted colony-wide.</summary>
    public static bool IsEngaged(MicromoundOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            return File.Exists(PathFor(options));
        }
        catch (Exception)
        {
            // An unreadable workspace is not permission to keep going. Ambiguity resolves downward.
            return true;
        }
    }

    /// <summary>
    /// Whether this particular mound is halted: globally, or by its own record. Resume is never
    /// automatic — clearing either one is an explicit operator act.
    /// </summary>
    public static bool AppliesTo(MoundRecord mound, MicromoundOptions options) =>
        mound.Stopped || IsEngaged(options);
}
