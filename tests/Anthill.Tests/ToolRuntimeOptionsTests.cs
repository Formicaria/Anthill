using Anthill.Core.Configuration;
using Anthill.Core.Security;
using Anthill.Core.Tools;
using Anthill.SDK.Contracts;
using Anthill.SDK.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.8.11 — the tool gates, now behind <see cref="IToolRuntimeOptions"/>.
///
/// These are the switches that decide whether an agent may run a shell command or write to disk, so
/// what is asserted here is narrow and deliberate: that the gate is READ WHEN THE TOOL RUNS, not
/// captured when it was built. The colony gates twice on purpose — <c>RuntimeOptions</c> decides
/// whether a tool is registered at all, and the tool re-checks at call time — and a captured value
/// would silently collapse the second check into the first while every existing test still passed.
/// </summary>
public class ToolRuntimeOptionsTests
{
    private sealed class Gates : IToolRuntimeOptions
    {
        public bool FileToolsEnabled { get; set; } = true;
        public bool FileWritingEnabled { get; set; }
        public bool ShellToolEnabled { get; set; }
        public bool WebSearchEnabled { get; set; }
        public bool PatchApplicationEnabled { get; set; }
        public IReadOnlySet<string> PatchAllowedSuffixes { get; set; } = new HashSet<string> { ".cs" };
        public IReadOnlySet<string> BlockedFileSuffixes { get; set; } = new HashSet<string> { ".db" };
        // v3.8.12 — added to the interface so Validation.ValidateSafePatchPath can take it whole.
        public IReadOnlySet<string> BlockedPathParts { get; set; } = new HashSet<string> { ".git" };
        public string ScriptDirectory { get; set; } = ".";
        public string BackupDirectory { get; set; } = "data/backups";
    }

    private static WorkspacePathGuard Guard() => new(Directory.GetCurrentDirectory());

    [Fact]
    public void A_disabled_shell_tool_refuses_with_an_authorization_failure()
    {
        var result = new ShellCommandTool(new Gates { ShellToolEnabled = false })
            .Run(new Dictionary<string, object?> { ["command"] = "echo hi" });

        Assert.False(result.Success);
        Assert.Equal(FailureClass.AuthorizationFailure, result.Failure);
    }

    [Fact]
    public void A_disabled_write_tool_refuses_with_an_authorization_failure()
    {
        var result = new WriteTextFileTool(Guard(), new Gates { FileWritingEnabled = false })
            .Run(new Dictionary<string, object?> { ["path"] = "x.txt", ["content"] = "hi" });

        Assert.False(result.Success);
        Assert.Equal(FailureClass.AuthorizationFailure, result.Failure);
    }

    /// <summary>
    /// The property this whole design exists for. Flipping the gate AFTER construction must change
    /// the answer — if it does not, the value was captured and the colony's second gate is gone.
    /// </summary>
    [Fact]
    public void A_gate_flipped_after_construction_takes_effect_on_the_next_call()
    {
        var gates = new Gates { ShellToolEnabled = false };
        var tool = new ShellCommandTool(gates);
        var args = new Dictionary<string, object?> { ["command"] = "echo hi" };

        Assert.Equal(FailureClass.AuthorizationFailure, tool.Run(args).Failure);

        gates.ShellToolEnabled = true;

        // Not asserting success — the command may legitimately fail in a sandbox. Asserting only
        // that it is no longer REFUSED, which is what the gate controls.
        Assert.NotEqual(FailureClass.AuthorizationFailure, tool.Run(args).Failure);
    }

    /// <summary>
    /// The default is the live runtime, so every existing construction — all of them, since the
    /// Queen still builds every tool — behaves exactly as it did before the interface existed.
    /// </summary>
    [Fact]
    public void An_uninjected_tool_reads_the_live_runtime()
    {
        var saved = AnthillRuntime.EnableShellTool;
        try
        {
            AnthillRuntime.EnableShellTool = false;
            var tool = new ShellCommandTool();

            Assert.Equal(FailureClass.AuthorizationFailure,
                tool.Run(new Dictionary<string, object?> { ["command"] = "echo hi" }).Failure);
        }
        finally { AnthillRuntime.EnableShellTool = saved; }
    }

    [Fact]
    public void Blocked_suffixes_are_refused_on_read_and_write()
    {
        var gates = new Gates { FileToolsEnabled = true, FileWritingEnabled = true };

        var read = new ReadTextFileTool(Guard(), gates)
            .Run(new Dictionary<string, object?> { ["path"] = "anthill.db" });
        var write = new WriteTextFileTool(Guard(), gates)
            .Run(new Dictionary<string, object?> { ["path"] = "anthill.db", ["content"] = "x" });

        Assert.Equal(FailureClass.AuthorizationFailure, read.Failure);
        Assert.Equal(FailureClass.AuthorizationFailure, write.Failure);
    }
}
