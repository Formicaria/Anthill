using System.Runtime.InteropServices;

namespace Anthill.Api;

/// <summary>
/// v0.3.8.49 (§14) — platform-aware operator-shell quick actions.
///
/// The console's quick buttons used to be four hardcoded Linux/systemd commands — <c>systemctl
/// restart</c>, <c>journalctl</c>, <c>df -h; free -h</c>. On a Windows host every one of them is a
/// "command not found", offered to the operator as if it would work. That is the failure this
/// exists to prevent: an action is only shown when the environment it targets can actually run it.
///
/// The set is chosen by the OS the API process is running on, discovered here rather than guessed in
/// the browser — the host knows what it is, the client does not. Each action carries the exact
/// command for THAT platform, so restarting runs <c>systemctl restart anthill</c> on Linux and
/// relaunches the <c>AnthillDesktop</c> process on Windows, and the button means the same thing on
/// both even though the thing being restarted is not the same kind of thing.
///
/// v0.3.8.124 — THE WINDOWS SET WAS AIMED AT A SERVICE THAT DOES NOT EXIST, which is the failure
/// described in the paragraph above, committed by the file that describes it. See
/// <c>WindowsProcess</c> below.
///
/// Deliberately data, not behaviour: the API returns the list, the console renders whatever it is
/// given, and adding a platform (or a per-environment action for LXC vs. bare metal later) is a new
/// entry here rather than new UI. Nothing is executed from this file — these are proposed commands
/// the existing <see cref="OperatorShell"/> path runs, under the same auth/role/gate/audit rules.
/// </summary>
public static class ShellPlatform
{
    /// <summary>A quick action the console may offer, with the command for the resolved platform.</summary>
    /// <param name="Id">Stable id, platform-independent — "restart_service" is the same intent everywhere.</param>
    /// <param name="Label">What the button says.</param>
    /// <param name="Command">The exact command for THIS platform.</param>
    /// <param name="Danger">Disruptive (restart/stop): the console confirms before running it.</param>
    public sealed record QuickAction(string Id, string Label, string Command, bool Danger = false);

    /// <summary>The coarse platform family the quick-action set is chosen by.</summary>
    public static string Detect()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        return "unknown";
    }

    private const string LinuxUnit = "anthill";

    /* WINDOWS RUNS THE DESKTOP APP, NOT A SERVICE. v0.3.8.124.
       This file offered three Windows actions against a Windows service called `Anthill`:
       `Get-Service Anthill`, `Get-EventLog -Source Anthill`, `Restart-Service Anthill`. No such
       service exists. Nothing in this repository registers one — `docs/DEPLOYMENT.md` still lists
       the Windows install script as unbuilt — and on Windows an operator runs `AnthillDesktop.exe`,
       which the installer puts on the Start menu. So all three actions failed, and they failed in
       the way this file's own header says it exists to prevent: "an action is only shown when the
       environment it targets can actually run it."

       The name was never verified. It was written as a plausible convention (lowercase unit on
       Linux, PascalCase service on Windows) beside a Linux set that was real, and it read as
       symmetric rather than as a guess — which is what kept it there for seventy-five releases.

       So the Windows actions now target the PROCESS. `Restart` stops `AnthillDesktop` and starts it
       again from the path the running process reports, which is the only way to relaunch something
       whose install location this file does not get to assume. `Status` reports whether the process
       is up rather than asking a service manager about a name it has never heard of. */
    private const string WindowsProcess = "AnthillDesktop";

    private static readonly IReadOnlyList<QuickAction> Linux = new List<QuickAction>
    {
        new("service_status", "Service status", $"systemctl status {LinuxUnit} --no-pager"),
        new("recent_logs",    "Recent logs",    $"journalctl -u {LinuxUnit} -n 40 --no-pager"),
        new("host_health",    "Host health",    "df -h; echo; free -h; echo; uptime"),
        new("restart_service","Restart service",$"systemctl restart {LinuxUnit}", Danger: true),
    };

    private static readonly IReadOnlyList<QuickAction> Windows = new List<QuickAction>
    {
        // PowerShell, which cmd /c can invoke. The ids stay platform-independent — "restart_service"
        // is the same INTENT on every platform even where the thing restarted is a process — because
        // the console keys its confirm-before-running behaviour on them.
        new("service_status", "App status", $"powershell -NoProfile -Command \"$p = Get-Process {WindowsProcess} -ErrorAction SilentlyContinue; if ($p) {{ $p | Format-List Id,ProcessName,StartTime,Path }} else {{ 'AnthillDesktop is not running.' }}\""),

        // The desktop app writes its own log rather than to the Application event log, which is
        // where `Get-EventLog -Source Anthill` was looking and finding nothing. DesktopLog puts it
        // under LOCALAPPDATA; a missing file is reported as such instead of as an empty log.
        new("recent_logs",    "Recent logs",    "powershell -NoProfile -Command \"$f = Join-Path $env:LOCALAPPDATA 'Anthill\\desktop.log'; if (Test-Path $f) { Get-Content $f -Tail 40 } else { \\\"No desktop log at $f yet.\\\" }\""),

        new("host_health",    "Host health",    "powershell -NoProfile -Command \"Get-PSDrive -PSProvider FileSystem | Format-Table Name,Used,Free; systeminfo | findstr /C:'Total Physical Memory' /C:'Available Physical Memory' /C:'System Boot Time'\""),

        // RELAUNCHED FROM THE RUNNING PROCESS'S OWN PATH. Where AnthillDesktop.exe is installed is
        // the installer's business and this file must not assume it — reading `MainModule.FileName`
        // off the live process is the one way to start the same binary that is already running. A
        // process that is not running cannot report a path, so that case says so rather than
        // starting nothing and reporting success.
        new("restart_service","Restart app",    $"powershell -NoProfile -Command \"$p = Get-Process {WindowsProcess} -ErrorAction SilentlyContinue; if (-not $p) {{ '{WindowsProcess} is not running — start it from the Start menu.'; exit }} $exe = $p[0].Path; $p | Stop-Process -Force; Start-Sleep -Seconds 2; Start-Process $exe; \\\"Restarted $exe\\\"\"", Danger: true),
    };

    private static readonly IReadOnlyList<QuickAction> MacOs = new List<QuickAction>
    {
        // launchd rather than systemd; a plain top/df for health. macOS is a developer host here, so
        // "the service" is a launchctl label the operator controls.
        new("service_status", "Service status", "launchctl list | grep -i anthill || echo 'anthill not registered with launchctl'"),
        new("recent_logs",    "Recent logs",    "log show --predicate 'process == \"anthill\"' --last 30m --style compact 2>/dev/null | tail -n 40"),
        new("host_health",    "Host health",    "df -h; echo; vm_stat; echo; uptime"),
    };

    /// <summary>The actions supported on the given platform — empty for an environment we cannot
    /// speak for, so the console shows a clear "no quick actions here" state rather than Linux
    /// buttons that would fail.</summary>
    public static IReadOnlyList<QuickAction> ActionsFor(string platform) => platform switch
    {
        "linux" => Linux,
        "windows" => Windows,
        "macos" => MacOs,
        _ => Array.Empty<QuickAction>(),
    };

    /// <summary>Shape the actions for the /shell/info payload.</summary>
    public static List<Dictionary<string, object?>> ActionsPayload(string platform) =>
        ActionsFor(platform).Select(a => new Dictionary<string, object?>
        {
            ["id"] = a.Id,
            ["label"] = a.Label,
            ["command"] = a.Command,
            ["danger"] = a.Danger,
        }).ToList();
}
