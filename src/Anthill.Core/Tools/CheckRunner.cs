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

    /// <summary>The ids compiled in, which no caller may remove. See <see cref="Unregister"/>.</summary>
    private static readonly HashSet<string> BuiltIn =
        new(Checks.Keys, StringComparer.OrdinalIgnoreCase);

    /// <summary>Operator/test extension point — still a declared allowlist, never free text.</summary>
    public static void Register(CheckDefinition def) => Checks[def.Id] = def;

    /// <summary>
    /// Take a registered check back out. v0.3.8.70.
    ///
    /// <see cref="Register"/> called itself a test extension point and offered no way back, so every
    /// check a test added stayed in this process-global allowlist for the rest of the run. Four test
    /// classes do it. That is the same shape as the two static leaks v0.3.8.69 closed, and it reaches
    /// further than it looks: <c>TesterAnt</c> selects from <see cref="Ids"/> when the workspace
    /// manifest is empty, matching ids against the task's own title and description — so which checks
    /// a later mission can be asked to run depends on which tests ran first.
    ///
    /// BUILT-INS ARE REFUSED, the same rule and the same reasoning as
    /// <c>ToolRegistry.Unregister</c>: registration composes what the colony may execute, and a
    /// runtime call able to strip <c>dotnet_build</c> out of the catalog would be a second, unaudited
    /// way to decide what gets verified. Returns false rather than throwing, so a cleanup path that
    /// names one by mistake does not fail a test for a reason unrelated to the test.
    /// </summary>
    public static bool Unregister(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || BuiltIn.Contains(id)) return false;
        return Checks.Remove(id);
    }
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

        // v0.3.8.70 — WHERE a check runs and WHICH checks exist are two questions, and one flag was
        // answering both.
        //
        // `manifest.IsEmpty ? _workdir : manifest.Root` reads as "no workspace in scope, use the
        // configured directory". It does not mean that. The manifest is empty when the adapters
        // detect NO PROJECT TYPE at the scoped root — which is a statement about what is in the
        // directory, not about whether a directory is in scope. So a mission that materialized a
        // patched revision, entered a scope bound to it, and dispatched the tester inside that scope
        // ran the check against `_workdir` — `AnthillRuntime.AllowedWorkspaceRoot`, the ORIGINAL
        // tree — the moment the revision held nothing the adapters recognise.
        //
        // ExecutionService stamps `task.RanRevisionId = revision.RevisionId` whenever a revision
        // exists, unconditionally. So the record said the tester judged the patched revision while
        // the check had run somewhere else: a declaration disagreeing with the runtime, in the
        // evidence path, on the side that reports success.
        //
        // The scope answers "where", because that is the question it was built for — it is the same
        // value `WorkspacePathGuard` confines writes to. The manifest keeps answering "which",
        // unchanged, below.
        var workdir = MissionWorkspaceScope.CurrentRoot ?? _workdir;

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
