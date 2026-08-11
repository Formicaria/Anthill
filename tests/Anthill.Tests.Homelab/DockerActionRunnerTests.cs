using Anthill.Modules.Homelab.Actions;
using Anthill.Modules.Homelab.Approvals;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// Container control, and the three gates in front of it. v0.3.8.40.
///
/// Every test here asserts a REFUSAL, and that is the point at this stage. This runner can stop and
/// restart containers on a real host; the valuable property is not that it works but that it
/// declines in every state where it must, and says why. A suite that only proved the happy path
/// would be proving the less important half.
///
/// The gates are constructor delegates rather than global settings, which is what makes these
/// tests honest: each one states the world it is testing, nothing is mutated process-wide, and the
/// shared-static flake this repository serialises other suites for cannot occur here. That shape
/// was not a testing convenience — the homelab module may not reference Anthill.Core at all, so the
/// composition root has to supply them, and the testable design fell out of the boundary.
///
/// Nothing here starts docker. Every refusal is reached before a process would launch, so these run
/// identically on a laptop without docker and on a container host with it.
/// </summary>
public class DockerActionRunnerTests
{
    private static DockerActionRunner Runner(bool server = true, bool execute = true) =>
        new(isServerDeployment: () => server,
            deploymentDescription: () => server ? "server (detected)" : "desktop (No container signals found.)",
            executeEnabled: () => execute);

    private static ActionProposal Proposal(string type = "restart_container", string target = "web") => new()
    {
        ActionType = type,
        TargetKind = "container",
        TargetId = target,
        Title = "test",
    };

    [Theory]
    [InlineData("start_container")]
    [InlineData("stop_container")]
    [InlineData("restart_container")]
    public void ItClaims_TheContainerLifecycleActions(string type) =>
        Assert.True(Runner().CanRun(Proposal(type)));

    /// <summary>
    /// It must not claim another runner's work. CanRun is first-match-wins in ActionExecutor, so a
    /// runner that over-claims silently shadows the one that should have acted — the defect
    /// v2.3.1.1 records about the mock runner hiding Proxmox.
    /// </summary>
    [Theory]
    [InlineData("start_vm", "vm")]
    [InlineData("restart_service", "service")]
    [InlineData("run_backup", "vm")]
    public void ItDoesNotClaim_AnotherRunnersWork(string type, string targetKind)
    {
        var p = Proposal(type);
        p.TargetKind = targetKind;

        Assert.False(Runner().CanRun(p));
    }

    /// <summary>
    /// Gate 1. A desktop colony refuses outright, and names the mode and the reason it holds it —
    /// otherwise an operator on a laptop sees "refused" with nothing to act on. Asserted with
    /// execution fully ENABLED, so the mode is proven to be the thing doing the refusing.
    /// </summary>
    [Fact]
    public async Task OnADesktop_ItRefuses_AndSaysWhy()
    {
        var runner = Runner(server: false, execute: true);

        var dry = await runner.DryRunAsync(Proposal());
        var exec = await runner.ExecuteAsync(Proposal());

        Assert.False(dry.Ok);
        Assert.False(exec.Ok);
        Assert.Contains("desktop", dry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No container signals found.", dry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gate 3, and the one that is easiest to get wrong. The target becomes a process ARGUMENT, so
    /// a name like `-v` cannot inject a shell command — but docker would read it as an OPTION.
    /// Requiring a leading alphanumeric is what stops an argument becoming a flag.
    /// </summary>
    [Theory]
    [InlineData("-v")]
    [InlineData("--privileged")]
    [InlineData("--volume=/:/host")]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    public async Task AnUnsafeContainerName_IsRefused(string target)
    {
        var result = await Runner(server: true, execute: true).ExecuteAsync(Proposal(target: target));

        Assert.False(result.Ok);
        Assert.Contains("valid container name", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("web")]
    [InlineData("my-app_1")]
    [InlineData("a.b-c_d")]
    [InlineData("0abc")]
    public async Task AnOrdinaryContainerName_PassesTheNameGuard(string target)
    {
        // Execution off, so the run stops at the EXECUTE gate — which proves the name was accepted
        // on the way past it rather than merely that something refused.
        var result = await Runner(server: true, execute: false).ExecuteAsync(Proposal(target: target));

        Assert.False(result.Ok);
        Assert.Contains("execution is disabled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- compose (v0.3.8.40) --------------------------------------------------------------------

    private static ActionProposal ComposeProposal(string type = "compose_up", string dir = "/srv/stack") => new()
    {
        ActionType = type,
        TargetKind = "compose_project",
        TargetId = dir,
        Title = "test",
    };

    [Theory]
    [InlineData("compose_up")]
    [InlineData("compose_down")]
    public void ItClaims_ComposeActions_OnAComposeProject(string type) =>
        Assert.True(Runner().CanRun(ComposeProposal(type)));

    /// <summary>
    /// A compose action aimed at a CONTAINER is not this runner's, and neither is a container
    /// action aimed at a project. The target kind carries which shape the payload is, and claiming
    /// a proposal whose shape cannot be acted on is how a runner swallows work it then fails.
    /// </summary>
    [Fact]
    public void ItDoesNotClaim_ComposeAgainstTheWrongTargetKind()
    {
        var p = ComposeProposal();
        p.TargetKind = "container";

        Assert.False(Runner().CanRun(p));
    }

    /// <summary>
    /// The compose target is a DIRECTORY, so the container-name rule would reject every valid one —
    /// but the same underlying concerns apply in a different shape. A leading '-' is read by docker
    /// as an option; `..` lets the path that runs differ from the path that was approved; a relative
    /// path means something different depending on where Anthill was started.
    /// </summary>
    [Theory]
    [InlineData("-rf")]
    [InlineData("")]
    [InlineData("/srv/../etc")]
    [InlineData("relative/path")]
    public async Task AnUnsafeComposeDirectory_IsRefused(string dir)
    {
        var result = await Runner(server: true, execute: true).ExecuteAsync(ComposeProposal(dir: dir));

        Assert.False(result.Ok);
        Assert.False(result.Message.Contains("succeeded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnAbsoluteComposeDirectory_PassesTheTargetGuard()
    {
        // Execution off, so it stops at the EXECUTE gate — proving the path was accepted past the
        // target guard rather than merely that something refused.
        var result = await Runner(server: true, execute: false).ExecuteAsync(ComposeProposal(dir: "/srv/stack"));

        Assert.False(result.Ok);
        Assert.Contains("execution is disabled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ComposeOnADesktop_IsRefusedLikeEverythingElse()
    {
        var result = await Runner(server: false, execute: true).ExecuteAsync(ComposeProposal());

        Assert.False(result.Ok);
        Assert.Contains("desktop", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gate 2. Execution off is the default, and it must stop EXECUTE without stopping DRY RUN —
    /// seeing what would happen is precisely how an operator decides whether to enable it.
    /// </summary>
    [Fact]
    public async Task WithExecutionDisabled_ExecuteRefuses_AndSaysHowToEnableIt()
    {
        var result = await Runner(server: true, execute: false).ExecuteAsync(Proposal());

        Assert.False(result.Ok);
        Assert.Contains("docker_execute_enabled", result.Message, StringComparison.Ordinal);
        Assert.Contains("Dry run is available", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A dry run never refuses for the EXECUTE gate. It may fail for any other reason — no docker
    /// installed, no such container — but "execution is disabled" must not be the answer, because
    /// that is the question the dry run exists to help answer.
    /// </summary>
    [Fact]
    public async Task DryRun_IsNotBlockedByTheExecuteGate()
    {
        var result = await Runner(server: true, execute: false)
            .DryRunAsync(Proposal(target: "anthill-no-such-container"));

        Assert.DoesNotContain("Container execution is disabled", result.Message, StringComparison.Ordinal);
    }
}
