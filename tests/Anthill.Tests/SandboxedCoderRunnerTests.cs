using System.Diagnostics;
using Anthill.Core.Configuration;
using Anthill.Core.Sandbox;
using Anthill.Core.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.11.0 — the sandboxed coder loop: green check completes and the LIVE tree is never touched,
/// a persistently failing check stops with an explicable bounded reason, and the gate OFF does no
/// work at all. Checks are allowlisted git probes so the test needs no .NET SDK.
/// </summary>
public class SandboxedCoderRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_scr_" + Guid.NewGuid().ToString("N"));
    private readonly bool _gateWas = AnthillRuntime.EnableSandboxExecution;

    public SandboxedCoderRunnerTests()
    {
        // Allowlisted, SDK-free checks: git --version always succeeds; an unknown subcommand fails.
        CheckCatalog.Register(new CheckDefinition("sbx_ok", "git", "--version", 30, true, "test: passing probe"));
        CheckCatalog.Register(new CheckDefinition("sbx_fail", "git", "not-a-real-subcommand", 30, true, "test: failing probe"));
        AnthillRuntime.EnableSandboxExecution = true;
    }

    public void Dispose()
    {
        AnthillRuntime.EnableSandboxExecution = _gateWas;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string ThrowawayGitRepo()
    {
        var repo = Path.Combine(_dir, "repo"); Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "hello.txt"), "original");
        Git(repo, "init"); Git(repo, "config user.email t@t.t"); Git(repo, "config user.name t");
        Git(repo, "add ."); Git(repo, "commit -m init --no-gpg-sign");
        return repo;
    }

    private static void Git(string wd, string args)
    {
        using var p = Process.Start(new ProcessStartInfo("git", args)
        { WorkingDirectory = wd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        p.WaitForExit(30_000);
    }

    private static string AddFileJson(string path, string content) =>
        $@"{{""summary"":""add a file"",""proposals"":[{{""file_path"":""{path}"",""change_type"":""add"",""reason"":""test"",""risk"":""low"",""old_content"":null,""new_content"":""{content}"",""requires_approval"":true}}]}}";

    [Fact]
    public void GateOff_DoesNoWork_AndReportsDisabled()
    {
        AnthillRuntime.EnableSandboxExecution = false;
        var repo = ThrowawayGitRepo();
        var runner = new SandboxedCoderRunner((_, _) => AddFileJson("generated.txt", "x"), "sbx_ok");

        var report = runner.Run(repo, "m1", "t1");

        Assert.Equal("disabled", report.StopReason);
        Assert.False(report.Verified);
        Assert.Empty(report.Proposals);
        Assert.False(File.Exists(Path.Combine(repo, "generated.txt")));
    }

    [Fact]
    public void GreenCheck_Completes_AndLiveTreeIsUntouched()
    {
        var repo = ThrowawayGitRepo();
        var runner = new SandboxedCoderRunner((_, _) => AddFileJson("generated.txt", "hello from sandbox"), "sbx_ok");

        var report = runner.Run(repo, "m1", "t1");

        Assert.Equal("completed", report.StopReason);
        Assert.True(report.Verified);
        Assert.Single(report.Proposals);
        Assert.Contains("generated.txt", report.ChangeSummary);
        // The proposal exists to be approved+applied later — it was NEVER applied to the live tree.
        Assert.False(File.Exists(Path.Combine(repo, "generated.txt")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(repo, "hello.txt")));
    }

    [Fact]
    public void FailingCheck_StopsBounded_WithExplicableReason()
    {
        var repo = ThrowawayGitRepo();
        var runner = new SandboxedCoderRunner((_, _) => AddFileJson("generated.txt", "hello"), "sbx_fail");

        var report = runner.Run(repo, "m1", "t1");

        Assert.False(report.Verified);
        Assert.Contains(report.StopReason, new[] { "repeated_action", "max_turns", "max_tool_calls" });
        Assert.False(File.Exists(Path.Combine(repo, "generated.txt"))); // still never touched the live tree
    }

    [Fact]
    public void UnknownCheck_RefusedBeforeAnyWork()
    {
        var repo = ThrowawayGitRepo();
        var runner = new SandboxedCoderRunner((_, _) => AddFileJson("generated.txt", "x"), "not_allowlisted");

        var report = runner.Run(repo, "m1", "t1");

        Assert.Equal("refused", report.StopReason);
        Assert.False(report.Verified);
    }
}
