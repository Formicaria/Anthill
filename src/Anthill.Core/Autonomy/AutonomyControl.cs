using Anthill.Core.Common;
using Anthill.Core.Configuration;

namespace Anthill.Core.Autonomy;

/// <summary>
/// The autonomous Director's kill switch (Phase 0 rail). Halting is durable and double-gated:
/// an on-disk sentinel file (survives restarts) OR an in-process flag (instant, same run).
/// The Director must call <see cref="IsStopped"/> before every mission and abort if true.
/// There is no auto-clear: a human (or the API in Phase 1) must explicitly <see cref="Resume"/>.
/// </summary>
public static class AutonomyControl
{
    private static volatile bool _processStopped;

    /// <summary>Absolute path to the STOP sentinel, under the workspace root (e.g. .anthill/STOP).</summary>
    public static string StopFilePath()
    {
        AnthillRuntime.Initialize();
        return Path.Combine(AnthillRuntime.WorkspaceRootPath, AnthillRuntime.AutonomyStopFileName);
    }

    /// <summary>True when autonomy is halted by either the sentinel file or the in-process flag.</summary>
    public static bool IsStopped => _processStopped || File.Exists(StopFilePath());

    /// <summary>Halts autonomy now and persists the halt by writing the sentinel file.</summary>
    public static void Stop(string reason = "manual stop")
    {
        _processStopped = true;
        try
        {
            var path = StopFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"{AnthillTime.NowUtc().ToIso()} {reason}\n");
        }
        catch { /* in-process flag still enforces the halt even if the file write fails */ }
    }

    /// <summary>Clears both the in-process flag and the sentinel file so the Director may run again.</summary>
    public static void Resume()
    {
        _processStopped = false;
        try { File.Delete(StopFilePath()); } catch { /* nothing to delete is fine */ }
    }
}
