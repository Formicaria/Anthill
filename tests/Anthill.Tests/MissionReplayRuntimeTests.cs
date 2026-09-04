using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// MISSION REPLAY — THE PARTS THAT TOUCH THE RUNTIME.
///
/// Separated from <see cref="MissionReplayConfigTests"/> because these mutate process-wide state
/// (environment variables and the projected runtime) and must not run beside tests that read it.
/// They share the collection the other runtime-mutating configuration tests already use.
/// </summary>
[Collection("specialist-gates")]
public class MissionReplayRuntimeTests : IDisposable
{
    private static readonly string[] Vars =
    [
        "ANTHILL_MISSION_REPLAY_ENABLED",
        "ANTHILL_MISSION_REPLAY_VAULT_PATH",
        "ANTHILL_MISSION_REPLAY_TAG",
        "ANTHILL_MISSION_REPLAY_LEARNING_ENABLED",
    ];

    private readonly Dictionary<string, string?> _saved = [];

    public MissionReplayRuntimeTests()
    {
        foreach (var v in Vars) _saved[v] = Environment.GetEnvironmentVariable(v);
    }

    /// <summary>Put the environment back, then re-project so the runtime stops carrying test values.</summary>
    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        AnthillRuntime.Initialize(force: true);
        GC.SuppressFinalize(this);
    }

    // ---- 7. environment variable overrides --------------------------------------------------------

    /// <summary>
    /// The variables an operator sets in a compose file, an LXC profile or a service unit actually
    /// reach the runtime. Asserted on the projected VALUES, not on the declaration — a documented
    /// override nothing reads is the failure this checks for.
    /// </summary>
    [Fact]
    public void EnvironmentVariables_OverrideTheConfiguredValues()
    {
        var vault = Directory.CreateTempSubdirectory("anthill-vault-env-").FullName;
        try
        {
            Environment.SetEnvironmentVariable("ANTHILL_MISSION_REPLAY_ENABLED", "true");
            Environment.SetEnvironmentVariable("ANTHILL_MISSION_REPLAY_VAULT_PATH", vault);
            Environment.SetEnvironmentVariable("ANTHILL_MISSION_REPLAY_TAG", "team/replay");
            Environment.SetEnvironmentVariable("ANTHILL_MISSION_REPLAY_LEARNING_ENABLED", "1");

            AnthillRuntime.Initialize(force: true);

            Assert.True(AnthillRuntime.MissionReplay.Enabled);
            Assert.Equal(vault, AnthillRuntime.MissionReplay.VaultPath);
            Assert.Equal("team/replay", AnthillRuntime.MissionReplay.ReplayTag);
            Assert.True(AnthillRuntime.MissionReplay.LearningEnabled);
            Assert.True(AnthillRuntime.MissionReplay.IsOperable);
            Assert.True(AnthillRuntime.MissionReplay.LearningEffective);
        }
        finally
        {
            Directory.Delete(vault, recursive: true);
        }
    }

    /// <summary>
    /// A VARIABLE SET TO THE EMPTY STRING IS AN OPERATOR SAYING NOTHING — the v0.3.8.91 rule these
    /// four were written to obey. `ANTHILL_MISSION_REPLAY_ENABLED=` in a compose file's
    /// `environment:` block must leave the configured value alone rather than read as "false".
    /// </summary>
    [Fact]
    public void ABlankEnvironmentVariable_LeavesTheConfiguredValueAlone()
    {
        foreach (var v in Vars) Environment.SetEnvironmentVariable(v, "");
        AnthillRuntime.Initialize(force: true);

        // Whatever the file says, a blank variable did not change it — and the tag is never blanked.
        Assert.False(string.IsNullOrWhiteSpace(AnthillRuntime.MissionReplay.ReplayTag));
    }

    /// <summary>An unrecognised truth spelling resolves to OFF: for a safety gate, that is the safe way to be wrong.</summary>
    [Fact]
    public void AnUnrecognisedBooleanValue_ResolvesToOff()
    {
        Environment.SetEnvironmentVariable("ANTHILL_MISSION_REPLAY_ENABLED", "yes-please");
        AnthillRuntime.Initialize(force: true);

        Assert.False(AnthillRuntime.MissionReplay.Enabled);
        Assert.False(AnthillRuntime.MissionReplay.IsOperable);
    }

    /// <summary>The runtime starts from the safe state, before any configuration is projected.</summary>
    [Fact]
    public void TheRuntimeValue_IsNeverNull()
    {
        AnthillRuntime.Initialize();
        Assert.NotNull(AnthillRuntime.MissionReplay);
    }

    // ---- the validator surfaces the findings ------------------------------------------------------

    /// <summary>
    /// A misconfiguration reaches the operator through the same channel every other configuration
    /// health finding uses — startup events and /config/health — rather than a log line nobody reads.
    /// </summary>
    [Fact]
    public void AnUnusableConfiguration_SurfacesAsAConfigHealthFinding()
    {
        Environment.SetEnvironmentVariable("ANTHILL_MISSION_REPLAY_ENABLED", "true");
        Environment.SetEnvironmentVariable("ANTHILL_MISSION_REPLAY_VAULT_PATH", "");
        AnthillRuntime.Initialize(force: true);

        var findings = RuntimeConfigValidator.Validate();

        Assert.Contains(findings, f => f.Combination == "mission_replay_without_vault_path");
    }

    /// <summary>And a colony with replay off contributes no findings of its own.</summary>
    [Fact]
    public void TheDefaultConfiguration_AddsNoFindings()
    {
        foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null);
        AnthillRuntime.Initialize(force: true);

        Assert.DoesNotContain(RuntimeConfigValidator.Validate(),
            f => f.Combination.StartsWith("mission_replay", StringComparison.Ordinal));
    }
}
