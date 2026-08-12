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
/// command for THAT platform, so "Restart service" runs <c>systemctl restart anthill</c> on Linux
/// and <c>Restart-Service Anthill</c> on Windows, and the button means the same thing on both.
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

    // The service name differs per platform convention: lowercase unit on Linux, PascalCase service
    // on Windows. Kept in one place so a rename is a single edit.
    private const string LinuxUnit = "anthill";
    private const string WindowsService = "Anthill";

    private static readonly IReadOnlyList<QuickAction> Linux = new List<QuickAction>
    {
        new("service_status", "Service status", $"systemctl status {LinuxUnit} --no-pager"),
        new("recent_logs",    "Recent logs",    $"journalctl -u {LinuxUnit} -n 40 --no-pager"),
        new("host_health",    "Host health",    "df -h; echo; free -h; echo; uptime"),
        new("restart_service","Restart service",$"systemctl restart {LinuxUnit}", Danger: true),
    };

    private static readonly IReadOnlyList<QuickAction> Windows = new List<QuickAction>
    {
        // Windows service control + inspection via PowerShell, which cmd /c can invoke. These are the
        // Windows-native equivalents of the Linux set, not Linux commands aimed at a Windows box.
        new("service_status", "Service status", $"powershell -NoProfile -Command \"Get-Service {WindowsService} | Format-List Name,Status,StartType\""),
        new("recent_logs",    "Recent logs",    $"powershell -NoProfile -Command \"Get-EventLog -LogName Application -Source {WindowsService} -Newest 40 -ErrorAction SilentlyContinue | Format-Table TimeGenerated,EntryType,Message -AutoSize\""),
        new("host_health",    "Host health",    "powershell -NoProfile -Command \"Get-PSDrive -PSProvider FileSystem | Format-Table Name,Used,Free; systeminfo | findstr /C:'Total Physical Memory' /C:'Available Physical Memory' /C:'System Boot Time'\""),
        new("restart_service","Restart service",$"powershell -NoProfile -Command \"Restart-Service {WindowsService}\"", Danger: true),
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
