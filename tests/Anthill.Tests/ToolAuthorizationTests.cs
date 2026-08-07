using Anthill.Core.Memory;
using Anthill.Core.Security;
using Anthill.Core.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Execution framework Stage B validation gate (spec §15 tool authorization tests): allowed tool
/// succeeds, unlisted tool denied, forbidden tool denied, name spoofing denied, denials are
/// structured and produce no side effect, control-plane approval path still works.
/// </summary>
public class ToolAuthorizationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_auth_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (ToolRegistry Tools, string Workspace) Harness()
    {
        Directory.CreateDirectory(_dir);
        var ws = Path.Combine(_dir, "ws"); Directory.CreateDirectory(ws);
        var mem = new SqliteMemory(Path.Combine(_dir, "t.db"));
        var registry = new ToolRegistry(mem);
        var guard = new WorkspacePathGuard(ws);
        registry.Register(new SystemInfoTool());
        registry.Register(new DirectoryListTool(guard));
        registry.Register(new ReadTextFileTool(guard));
        registry.Register(new WriteTextFileTool(guard));
        return (registry, ws);
    }

    // ---- Decision matrix -----------------------------------------------------------------------

    [Theory]
    [InlineData("file", "read_text_file", true)]      // allowed by role dispatch policy
    [InlineData("file", "list_directory", true)]
    [InlineData("researcher", "system_info", true)]
    [InlineData("web", "web_search", true)]
    [InlineData("file", "web_search", false)]          // unlisted for this role
    [InlineData("researcher", "read_text_file", false)]
    [InlineData("coder", "list_directory", false)]     // model-only role: empty allowlist, fail closed
    [InlineData("researcher", "apply_patch", false)]   // structurally forbidden to mission agents
    [InlineData("file", "shell_command", false)]
    [InlineData("builder", "write_text_file", false)]
    [InlineData("totally_fake_ant", "system_info", false)] // spoofed identity grants nothing
    [InlineData("queen", "apply_patch", true)]         // control-plane approval pipeline unchanged
    [InlineData("director", "apply_patch", true)]
    [InlineData(null, "system_info", true)]            // system-internal compatibility
    public void Evaluate_EnforcesTheDeclaredBoundary(string? ant, string tool, bool allowed)
        => Assert.Equal(allowed, ToolAuthorization.Evaluate(ant, tool).Allowed);

    [Fact]
    public void SpecialistContracts_AreEnforcedEvenBeforeActivation()
    {
        Assert.True(ToolAuthorization.Evaluate("ui_cartographer", "read_text_file").Allowed);
        Assert.False(ToolAuthorization.Evaluate("ui_cartographer", "write_text_file").Allowed);
        Assert.False(ToolAuthorization.Evaluate("scribe", "apply_patch").Allowed);
        Assert.False(ToolAuthorization.Evaluate("tester", "shell_command").Allowed);
    }

    // ---- Dispatch behavior ---------------------------------------------------------------------

    [Fact]
    public void AllowedCall_StillSucceeds_NoRegression()
    {
        var (tools, ws) = Harness();
        File.WriteAllText(Path.Combine(ws, "hello.txt"), "hi");
        var result = tools.RunTool("read_text_file", null, null, "file", new() { ["path"] = "hello.txt" });
        Assert.True(result.Success);
        Assert.Contains("hi", result.Output);
    }

    [Fact]
    public void DeniedCall_IsStructured_AndProducesNoSideEffect()
    {
        var (tools, ws) = Harness();
        var result = tools.RunTool("write_text_file", null, null, "researcher",
            new() { ["path"] = "evil.txt", ["content"] = "should never exist" });
        Assert.False(result.Success);
        Assert.Contains("authorization_denied", result.Error);
        Assert.False(File.Exists(Path.Combine(ws, "evil.txt"))); // the tool never ran
    }

    [Fact]
    public void SpoofedName_CannotDispatchAnything()
    {
        var (tools, _) = Harness();
        var result = tools.RunTool("system_info", null, null, "made_up_ant");
        Assert.False(result.Success);
        Assert.Contains("fail closed", result.Error);
    }

    // ---- Context path (Stage C/D consumers) ----------------------------------------------------

    [Fact]
    public void ContextEvaluation_ChecksCapabilities_AllowForbidden()
    {
        var ctx = new ToolExecutionContext("m1", "t1", "ui_cartographer", "ui_cartographer.route_mapper",
            GrantedCapabilities: new HashSet<string> { Anthill.SDK.Contracts.Capability.RepoRead, Anthill.SDK.Contracts.Capability.RepoSearch },
            AllowedTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "read_text_file", "list_directory" },
            ForbiddenTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web_search" });
        Assert.True(ToolAuthorization.Evaluate(ctx, "read_text_file").Allowed);
        Assert.False(ToolAuthorization.Evaluate(ctx, "web_search").Allowed);   // forbidden wins
        Assert.False(ToolAuthorization.Evaluate(ctx, "apply_patch").Allowed);  // structural, even if someone allowlists it
        var partial = ctx with { GrantedCapabilities = new HashSet<string> { Anthill.SDK.Contracts.Capability.RepoRead } };
        Assert.False(ToolAuthorization.Evaluate(partial, "read_text_file").Allowed); // missing capability
    }
}
