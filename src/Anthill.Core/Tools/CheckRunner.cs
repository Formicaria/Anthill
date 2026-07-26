using System.Diagnostics;
using Anthill.Core.Domain;

namespace Anthill.Core.Tools;

/// <summary>
/// Execution framework Stage D-2 — the ONLY path by which TesterAnt executes anything. Checks are
/// declared, allowlisted commands with stable ids, fixed arguments, and hard timeouts. There is no
/// arbitrary-shell escape hatch: an unknown or disabled check id is refused before any process
/// starts, and the command line comes from the catalog — never from model output or task text.
/// </summary>
public sealed record CheckDefinition(
    string Id, string FileName, string Arguments, int TimeoutSeconds, bool Enabled, string Description);

public static class CheckCatalog
{
    private static readonly Dictionary<string, CheckDefinition> Checks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dotnet_build"] = new("dotnet_build", "dotnet", "build -c Release --nologo", 600, true, ".NET solution build"),
        ["dotnet_test"] = new("dotnet_test", "dotnet", "test -c Release --nologo", 1200, true, ".NET full test suite"),
        ["dotnet_version"] = new("dotnet_version", "dotnet", "--version", 30, true, "SDK availability probe"),
    };

    public static CheckDefinition? Get(string id) => Checks.TryGetValue(id ?? "", out var c) ? c : null;
    public static IReadOnlyCollection<string> Ids => Checks.Keys;

    /// <summary>Operator/test extension point — still a declared allowlist, never free text.</summary>
    public static void Register(CheckDefinition def) => Checks[def.Id] = def;
}

public sealed class RunAllowlistedCheckTool : ITool
{
    private readonly string _workdir;
    public RunAllowlistedCheckTool(string workdir) => _workdir = workdir;
    public string Name => "run_allowlisted_check";
    public string Description => "Run one declared check from the allowlisted catalog (no arbitrary commands).";

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        var id = args.TryGetValue("check_id", out var v) ? v?.ToString() ?? "" : "";
        var def = CheckCatalog.Get(id);
        if (def is null)
            return new ToolResult(Name, false, "", $"check '{id}' is not in the allowlisted catalog — refused");
        if (!def.Enabled)
            return new ToolResult(Name, false, "", $"check '{id}' is disabled — refused");

        var started = DateTime.UtcNow;
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(def.FileName, def.Arguments)
                {
                    WorkingDirectory = _workdir, RedirectStandardOutput = true,
                    RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                },
            };
            proc.Start();
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(TimeSpan.FromSeconds(def.TimeoutSeconds)))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new ToolResult(Name, false, "", $"check '{id}' timed out after {def.TimeoutSeconds}s");
            }
            var output = $"check_id={id}\nexit_code={proc.ExitCode}\nduration_ms={(DateTime.UtcNow - started).TotalMilliseconds:F0}\n"
                + $"--- output ---\n{Truncate(stdout.Result)}\n{Truncate(stderr.Result)}";
            return proc.ExitCode == 0
                ? new ToolResult(Name, true, output, "")
                : new ToolResult(Name, false, output, $"check '{id}' exited {proc.ExitCode}");
        }
        catch (Exception e)
        {
            return new ToolResult(Name, false, "", $"check '{id}' could not start: {e.Message}");
        }
    }

    private static string Truncate(string s) => s.Length <= 8000 ? s : s[..8000] + "\n…(truncated)";
}
