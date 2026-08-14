using System.Diagnostics;
using System.Text;
using Anthill.Core.Domain;
using Anthill.Core.Workspaces;   // the mission's manifest decides which checks exist and where they run

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

        // v3.5.0 — the mission's own workspace decides both WHERE a check runs and WHICH checks
        // exist. Two things change together, and they have to:
        //
        //   - the working directory becomes the mission workspace, so a verification actually
        //     verifies the changed files. Running in the live checkout would have tested code the
        //     mission never touched and reported success about the wrong tree.
        //   - the check comes from the workspace's detected manifest, so a Node workspace has Node
        //     checks. The hard-coded catalog only ever knew .NET, which meant a frontend change had
        //     nothing to verify with — and "no check exists" is exactly the pressure that turns into
        //     handing a model a shell.
        //
        // Outside a mission scope this is the configured workdir and the declared catalog, unchanged.
        var manifest = WorkspaceCapabilityManifest.ForCurrentMission();
        var workdir = manifest.IsEmpty ? _workdir : manifest.Root;

        // The manifest is consulted FIRST, then the global catalog. Both are declared in this
        // repository under review; neither is ever read from the project being modified.
        var def = manifest.Find(id) ?? CheckCatalog.Get(id);
        if (def is null)
        {
            var available = manifest.IsEmpty
                ? string.Join(", ", CheckCatalog.Ids)
                : string.Join(", ", manifest.Checks.Select(c => c.Id));
            return new ToolResult(Name, false, "",
                $"check '{id}' is not in the allowlisted catalog — refused. Available here: {available}",
                FailureClass.AuthorizationFailure);
        }
        if (!def.Enabled)
            return new ToolResult(Name, false, "", $"check '{id}' is disabled — refused", FailureClass.AuthorizationFailure);

        var started = DateTime.UtcNow;
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(def.FileName, def.Arguments)
                {
                    WorkingDirectory = workdir, RedirectStandardOutput = true,
                    RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,   // v0.3.8.55: children emit UTF-8, not the OS codepage
                    StandardErrorEncoding = Encoding.UTF8,
                },
            };
            proc.Start();
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(TimeSpan.FromSeconds(def.TimeoutSeconds)))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new ToolResult(Name, false, "", $"check '{id}' timed out after {def.TimeoutSeconds}s", FailureClass.Timeout);
            }
            var output = $"check_id={id}\nexit_code={proc.ExitCode}\nduration_ms={(DateTime.UtcNow - started).TotalMilliseconds:F0}\n"
                + $"--- output ---\n{Truncate(stdout.Result)}\n{Truncate(stderr.Result)}";
            return proc.ExitCode == 0
                ? new ToolResult(Name, true, output, "")
                : new ToolResult(Name, false, output, $"check '{id}' exited {proc.ExitCode}", FailureClass.VerificationFailure);
        }
        catch (Exception e)
        {
            return new ToolResult(Name, false, "", $"check '{id}' could not start: {e.Message}", ToolRegistry.ClassifyThrown(e));
        }
    }

    private static string Truncate(string s) => s.Length <= 8000 ? s : s[..8000] + "\n…(truncated)";
}
