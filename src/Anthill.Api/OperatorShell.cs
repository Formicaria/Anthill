using System.Diagnostics;
using System.Text;
using Anthill.Core.Common;
using Anthill.Core.Configuration;

namespace Anthill.Api;

/// <summary>
/// The admin-only operator shell console (Configuration → Shell). A logged-in <b>administrator</b>
/// runs commands directly on the host the API is running on — a real terminal into the LXC/VM/box,
/// for maintenance the AI ants must never do (restart the service, edit config, pull updates,
/// inspect the system).
///
/// This is deliberately separate from the AI ants' <c>ShellCommandTool</c>: that tool is
/// allowlisted because an LLM drives it; this console is arbitrary because a trusted human admin
/// drives it. It is nonetheless the highest-risk surface in the app — remote code execution — so
/// it is gated four ways: (1) the caller must be authenticated, (2) with the admin role
/// (<c>operator_shell</c> is never in the coordinator permission set), (3) the
/// <c>operator_shell_enabled</c> config gate must be on, and (4) every command is written to the
/// audit event log with the operator's username before and after it runs. Output is capped and
/// each command is bounded by a timeout so a runaway process can't wedge the API.
/// </summary>
public static class OperatorShell
{
    public const int TimeoutSeconds = 60;
    private const int MaxOutputChars = 40_000;

    public sealed record ShellResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, string WorkingDir, double ElapsedSeconds);

    /// <summary>The console's default working directory: the configured override, else the agent workspace root.</summary>
    public static string DefaultWorkingDir()
    {
        var configured = AnthillRuntime.OperatorShellDir;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;
        var ws = AnthillRuntime.AllowedWorkspaceRoot;
        return Directory.Exists(ws) ? Path.GetFullPath(ws) : Environment.CurrentDirectory;
    }

    /// <summary>
    /// Runs one command via <c>/bin/sh -c</c> (or <c>cmd /c</c> on Windows) in the given directory.
    /// Callers must have already enforced auth/role/gate; this method assumes an authorized admin.
    /// </summary>
    public static ShellResult Execute(string command, string? workingDir)
    {
        var dir = string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir)
            ? DefaultWorkingDir() : Path.GetFullPath(workingDir);

        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = dir,
        };
        if (isWindows) { psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(command); }
        else { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(command); }

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)!;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        // The async handlers fire on a threadpool thread; lock the builders so a concurrent
        // AppendLine can't tear the StringBuilder while the result thread reads it.
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var exited = proc.WaitForExit(TimeoutSeconds * 1000);
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            proc.WaitForExit(2000);
            sw.Stop();
            lock (stdout) lock (stderr)
                return new ShellResult(-1, Cap(stdout.ToString()), Cap(stderr + $"\n[command exceeded {TimeoutSeconds}s and was terminated]"),
                    true, dir, Math.Round(sw.Elapsed.TotalSeconds, 2));
        }
        // WaitForExit(ms)==true can return BEFORE the async output handlers finish draining; the
        // parameterless overload blocks until stdout/stderr are fully flushed, so no truncation.
        proc.WaitForExit();
        sw.Stop();
        lock (stdout) lock (stderr)
            return new ShellResult(proc.ExitCode, Cap(stdout.ToString()), Cap(stderr.ToString()), false, dir, Math.Round(sw.Elapsed.TotalSeconds, 2));
    }

    private static string Cap(string s) =>
        s.Length <= MaxOutputChars ? s.TrimEnd() : s[..MaxOutputChars].TrimEnd() + "\n...[output truncated]";
}
