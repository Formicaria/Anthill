using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Which of the two Anthills this is. v0.3.8.40.
///
/// The whole reason the decision is a pure function over <see cref="DeploymentModeResolver.HostFacts"/>
/// is this file. Asserting the container rules against a real host would need a container, so on a
/// developer laptop and on this repository's CI runner they would be verified by not being
/// verified — every Docker and LXC branch permanently unexecuted, and nobody the wiser until an
/// operator's LXC came up in desktop mode.
/// </summary>
public class DeploymentModeTests
{
    private static DeploymentModeResolver.HostFacts Bare => new(false, "", "", false);

    [Theory]
    [InlineData("desktop", DeploymentMode.Desktop)]
    [InlineData("server", DeploymentMode.Server)]
    [InlineData("DESKTOP", DeploymentMode.Desktop)]
    [InlineData("Server", DeploymentMode.Server)]
    public void AnExplicitSetting_Wins_OverAnyDetection(string configured, DeploymentMode expected)
    {
        // Every container signal on at once. The operator still gets what they asked for: a
        // heuristic does not overrule a decision, and all of these signals are heuristics.
        var containerish = new DeploymentModeResolver.HostFacts(
            DockerEnvFileExists: true,
            CgroupText: "0::/docker/abc",
            InitEnviron: "container=lxc",
            RunningOnWindows: false);

        var (mode, _, detected) = DeploymentModeResolver.Resolve(configured, containerish);

        Assert.Equal(expected, mode);
        Assert.False(detected);
    }

    /// <summary>
    /// A typo does NOT silently become auto-detection.
    ///
    /// "sever" would otherwise pick a mode by accident and report it as detected, and the operator
    /// would have no way to learn their setting was being ignored. The value is named back to them
    /// in the reason.
    /// </summary>
    [Fact]
    public void AnUnreadableSetting_SaysSo_RatherThanQuietlyDetecting()
    {
        var (mode, reason, detected) = DeploymentModeResolver.Resolve("sever", Bare);

        Assert.Equal(DeploymentMode.Desktop, mode);   // fell back to detection on a bare host
        Assert.True(detected);
        Assert.Contains("sever", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData(null)]
    public void AutoOrUnset_Detects(string? configured)
    {
        var (mode, _, detected) = DeploymentModeResolver.Resolve(configured, Bare);

        Assert.Equal(DeploymentMode.Desktop, mode);
        Assert.True(detected);
    }

    [Fact]
    public void DockerEnvFile_MeansServer()
    {
        var (mode, reason, _) = DeploymentModeResolver.Resolve("auto", Bare with { DockerEnvFileExists = true });

        Assert.Equal(DeploymentMode.Server, mode);
        Assert.Contains("Docker", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0::/docker/3fa2c9", "Docker")]
    [InlineData("11:devices:/lxc/108", "LXC")]
    [InlineData("0::/system.slice/containerd.service", "containerd")]
    [InlineData("0::/kubepods/besteffort/podabc", "Kubernetes")]
    public void ContainerCgroups_MeanServer_AndNameTheRuntime(string cgroup, string named)
    {
        var (mode, reason, _) = DeploymentModeResolver.Resolve("auto", Bare with { CgroupText = cgroup });

        Assert.Equal(DeploymentMode.Server, mode);
        Assert.Contains(named, reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The case the cgroup check misses. On a cgroup v2 LXC host the path is often bare, so PID 1's
    /// environment is the only signal left — and without it an LXC would come up as a desktop.
    /// </summary>
    [Fact]
    public void LxcWithABareCgroup_IsStillDetected_FromPidOneEnvironment()
    {
        var facts = Bare with { CgroupText = "0::/", InitEnviron = "PATH=/usr/bin\ncontainer=lxc\nHOME=/root" };

        var (mode, reason, _) = DeploymentModeResolver.Resolve("auto", facts);

        Assert.Equal(DeploymentMode.Server, mode);
        Assert.Contains("LXC", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Windows is the deployment this distinction exists to protect. Treating a Windows desktop as
    /// a server because some path happened to match would be the worst available default, so the
    /// check comes first and nothing after it can override it.
    /// </summary>
    [Fact]
    public void Windows_IsAlwaysDesktop_EvenWithContainerSignals()
    {
        var facts = new DeploymentModeResolver.HostFacts(true, "0::/docker/x", "container=lxc", RunningOnWindows: true);

        var (mode, _, _) = DeploymentModeResolver.Resolve("auto", facts);

        Assert.Equal(DeploymentMode.Desktop, mode);
    }

    /// <summary>
    /// Unknown means Desktop, which is the answer that grants the least. A host Anthill could not
    /// read must not be assumed to be one it may manage.
    /// </summary>
    [Fact]
    public void AHostThatCannotBeRead_IsTreatedAsDesktop()
    {
        var (mode, reason, _) = DeploymentModeResolver.Resolve("auto", Bare);

        Assert.Equal(DeploymentMode.Desktop, mode);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    /// <summary>Probing must never throw, whatever the host is. It runs during Initialize.</summary>
    [Fact]
    public void Probing_NeverThrows()
    {
        var facts = DeploymentModeResolver.Probe();
        var (_, reason, _) = DeploymentModeResolver.Resolve("auto", facts);

        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
