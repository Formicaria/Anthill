using Anthill.Core.Homelab.Actions;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v2.3.1 guards on the first write-capable runner. The client itself is exercised structurally
/// (path allowlist) — no network in tests.
/// </summary>
public class ProxmoxActionRunnerTests
{
    private static ActionProposal P(string type, string targetId, string kind = "vm")
    {
        var p = new ActionProposal();
        p.ActionType = type; p.TargetId = targetId; p.TargetKind = kind;
        return p;
    }

    private static ProxmoxActionRunner Runner() =>
        new(() => new ProxmoxActionClient("localhost", 1, () => "t", protocol: "http"));

    [Theory]
    [InlineData("start_vm", "pve1/104", true)]
    [InlineData("stop_container", "pve1/200", true)]
    [InlineData("create_snapshot", "pve1/104", true)]
    [InlineData("run_backup", "pve1/104", true)]
    [InlineData("resolve_incident", "pve1/104", false)]   // local runner's job, never Proxmox
    [InlineData("delete_vm", "pve1/104", false)]           // forbidden by catalog AND unsupported here
    [InlineData("start_vm", "104", false)]                 // must be node/vmid
    [InlineData("start_vm", "pve1/not-a-vmid", false)]
    public void CanRun_AcceptsOnlySupportedActionsWithNodeVmidTargets(string type, string target, bool expected)
        => Assert.Equal(expected, Runner().CanRun(P(type, target)));

    [Fact]
    public async System.Threading.Tasks.Task DryRun_DescribesWithoutExecuting_AndNamesRealTarget()
    {
        var r = await Runner().DryRunAsync(P("restart_vm", "pve1/104"));
        Assert.True(r.Ok);
        Assert.Contains("/nodes/pve1/qemu/104/status/reboot", r.Message);
    }

    [Fact]
    public async System.Threading.Tasks.Task Client_RefusesAnyPathOutsideTheActionAllowlist()
    {
        using var c = new ProxmoxActionClient("localhost", 1, () => "t", protocol: "http");
        // Structural guard fires BEFORE any network I/O, so no server is needed.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => c.PostAsync("/nodes/pve1/qemu/104/config", null, default));       // reconfigure — refused
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => c.PostAsync("/access/users", null, default));                      // user admin — refused
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => c.PostAsync("/nodes/pve1/qemu/104/status/suspend", null, default)); // not in catalog — refused
    }

    [Fact]
    public void StopMapsToCleanShutdown_NeverHardStop()
    {
        // Verified via dry-run text: the stop actions must issue 'shutdown' (guest-clean), not 'stop'.
        var msg = Runner().DryRunAsync(P("stop_vm", "pve1/104")).Result.Message;
        Assert.Contains("status/shutdown", msg);
    }
}
