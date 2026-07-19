using Anthill.Core.Common;
using Anthill.Core.Configuration;

namespace Anthill.Core.Homelab.Actions;

/// <summary>
/// The homelab action kill switch (v2.3.0, NORTH_STAR Phase 12 / safety rule 12: "No action
/// executes while .anthill/HOMELAB_STOP exists"). Mirrors <c>AutonomyControl</c> exactly:
/// halting is durable and double-gated — an on-disk sentinel file (survives restarts) OR an
/// in-process flag (instant, same run). The ActionExecutor must call <see cref="IsStopped"/>
/// immediately before every execution and refuse if true. There is no auto-clear: a human must
/// explicitly <see cref="Resume"/> (or delete the file).
/// </summary>
public static class HomelabActionControl
{
    private static volatile bool _processStopped;

    /// <summary>Absolute path to the HOMELAB_STOP sentinel, under the workspace root (e.g. .anthill/HOMELAB_STOP).</summary>
    public static string StopFilePath()
    {
        AnthillRuntime.Initialize();
        return Path.Combine(AnthillRuntime.WorkspaceRootPath, AnthillRuntime.HomelabStopFileName);
    }

    /// <summary>True when homelab actions are halted by either the sentinel file or the in-process flag.</summary>
    public static bool IsStopped => _processStopped || File.Exists(StopFilePath());

    /// <summary>Halts all homelab actions now and persists the halt by writing the sentinel file.</summary>
    public static void Stop(string reason = "manual stop")
    {
        _processStopped = true;
        try
        {
            var path = StopFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"{AnthillTime.NowUtc().ToIso()} {reason}\n");
        }
        catch { /* the in-process flag still enforces the halt even if the file write fails */ }
    }

    /// <summary>Clears both the in-process flag and the sentinel file so actions may execute again.</summary>
    public static void Resume()
    {
        _processStopped = false;
        try { File.Delete(StopFilePath()); }
        catch { /* if the file cannot be deleted it keeps enforcing the halt — fail toward stopped */ }
    }
}
