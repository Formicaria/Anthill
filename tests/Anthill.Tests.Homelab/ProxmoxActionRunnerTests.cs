using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Actions;
using Anthill.Core.Homelab.Approvals;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v2.3.1 guards on the first write-capable runner, extended in v2.3.1.1 with the D1 target-guard
/// requirement and node-segment character validation. The client is exercised structurally
/// (path + target allowlists fire before any I/O) — no network in tests.
/// </summary>
public class ProxmoxActionRunnerTests
{
    private static ActionProposal P(string type, string targetId, string kind = "vm")
    {
        var p = new ActionProposal();
        p.ActionType = type; p.TargetId = targetId; p.TargetKind = kind;
        return p;
    }

    /// <summary>v2.3.1.1: the client requires the D1 target guard; tests inject fakes.</summary>
    private sealed class FakeGuard : IHomelabTargetGuard
    {
        public bool Allow { get; init; } = true;
        public bool IsAllowed(string hostOrIp) => Allow;
    }

    private static ProxmoxActionClient Client(bool allowed = true) =>
        new("localhost", 1, new FakeGuard { Allow = allowed }, () => "t", protocol: "http");

    private static ProxmoxActionRunner Runner() => new(() => Client());

    [Theory]
    [InlineData("start_vm", "pve1/104", true)]
    [InlineData("stop_container", "pve1/200", true)]
    [InlineData("create_snapshot", "pve1/104", true)]
    [InlineData("run_backup", "pve1/104", true)]
    [InlineData("resolve_incident", "pve1/104", false)]   // local runner's job, never Proxmox
    [InlineData("delete_vm", "pve1/104", false)]           // forbidden by catalog AND unsupported here
    [InlineData("start_vm", "104", false)]                 // must be node/vmid
    [InlineData("start_vm", "pve1/not-a-vmid", false)]
    [InlineData("start_vm", "pve1?x=y/104", false)]        // v2.3.1.1: query injection in node segment
    [InlineData("start_vm", "pve1#f/104", false)]          // v2.3.1.1: fragment injection in node segment
    [InlineData("start_vm", "/104", false)]                // v2.3.1.1: empty node
    public void CanRun_AcceptsOnlySupportedActionsWithValidatedNodeVmidTargets(string type, string target, bool expected)
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
        using var c = Client();
        // Structural guard fires BEFORE any network I/O, so no server is needed.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => c.PostAsync("/nodes/pve1/qemu/104/config", null, default));       // reconfigure — refused
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => c.PostAsync("/access/users", null, default));                      // user admin — refused
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => c.PostAsync("/nodes/pve1/qemu/104/status/suspend", null, default)); // not in catalog — refused
    }

    [Fact]
    public async System.Threading.Tasks.Task Client_RefusesNonAllowlistedHost_BeforeAnyIo()
    {
        // v2.3.1.1: D1 — even a catalog-legal write is refused when the HOST is not allowlisted.
        using var c = Client(allowed: false);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => c.PostAsync("/nodes/pve1/qemu/104/status/start", null, default));
        Assert.Contains("allowlist", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => c.GetGuestStatusAsync("pve1", "qemu", "104", default)); // verification path guarded too
    }

    [Fact]
    public void StopMapsToCleanShutdown_NeverHardStop()
    {
        // Verified via dry-run text: the stop actions must issue 'shutdown' (guest-clean), not 'stop'.
        var msg = Runner().DryRunAsync(P("stop_vm", "pve1/104")).Result.Message;
        Assert.Contains("status/shutdown", msg);
    }
}
