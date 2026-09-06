
namespace Anthill.Modules.Infrastructure.Actions;

/// <summary>
/// The infrastructure action kill switch (v2.3.0, NORTH_STAR Phase 12 / safety rule 12: "No action
/// executes while .anthill/INFRASTRUCTURE_STOP exists"). Mirrors <c>AutonomyControl</c> exactly:
/// halting is durable and double-gated — an on-disk sentinel file (survives restarts) OR an
/// in-process flag (instant, same run). The ActionExecutor must call <see cref="IsStopped"/>
/// immediately before every execution and refuse if true. There is no auto-clear: a human must
/// explicitly <see cref="Resume"/> (or delete the file).
/// </summary>
public static class InfrastructureActionControl
{
    private static volatile bool _processStopped;

    /// <summary>Absolute path to the INFRASTRUCTURE_STOP sentinel, under the workspace root (e.g. .anthill/INFRASTRUCTURE_STOP).</summary>
    public static string StopFilePath()
    {
        // v3.8.7: no AnthillRuntime.Initialize() here. Config bootstrapping is the composition
        // root's job, and a module calling it would be reaching into the core to fix an ordering
        // problem that belongs to whoever started the process.
        return Path.Combine(InfrastructureRuntime.Options.WorkspaceRootPath, InfrastructureRuntime.Options.StopFileName);
    }

    /// <summary>
    /// The sentinel this kill switch used to be called, still honoured. v0.3.8.128.
    ///
    /// The file was `HOMELAB_STOP` and is `INFRASTRUCTURE_STOP` now. It is not config and not a
    /// database row — it is a file an operator creates BY HAND, often as the fastest way to halt a
    /// misbehaving colony, and it is the one piece of state in this rename that a person may have
    /// written without the product's help. Reading only the new name would mean a release silently
    /// resumed actions an operator had stopped, which is the worst failure available to a kill
    /// switch: it fails OPEN and says nothing.
    ///
    /// So both are read, forever. `Stop` writes the current name and `Resume` deletes both, so an
    /// operator who resumes deliberately is not left halted by a file they cannot see.
    /// </summary>
    private static string LegacyStopFilePath() =>
        Path.Combine(InfrastructureRuntime.Options.WorkspaceRootPath, "HOMELAB_STOP");

    /// <summary>True when infrastructure actions are halted by either sentinel file or the in-process flag.</summary>
    public static bool IsStopped =>
        _processStopped || File.Exists(StopFilePath()) || File.Exists(LegacyStopFilePath());

    /// <summary>Halts all infrastructure actions now and persists the halt by writing the sentinel file.</summary>
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
        // Both names — resuming must actually resume. Deleting only the current one would leave a
        // colony that reports itself resumed and refuses every action, which is the same fail-open
        // defect inverted and just as silent.
        try { File.Delete(LegacyStopFilePath()); } catch { /* best effort */ }
        try { File.Delete(StopFilePath()); }
        catch { /* if the file cannot be deleted it keeps enforcing the halt — fail toward stopped */ }
    }
}
