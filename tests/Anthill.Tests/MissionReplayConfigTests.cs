using System.Text.Json;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// MISSION REPLAY — THE CONFIGURATION CONTRACT.
///
/// This release ships settings and nothing else, so these tests check the two things a settings-only
/// release can actually get wrong: that the values reach the runtime with the meaning the operator
/// wrote, and that every unusable combination is REPORTED rather than quietly replaced by a default.
///
/// The pure cases run against <see cref="MissionReplayOptions"/> directly — no workspace, no config
/// file, no globals — so they assert real values rather than the absence of an exception. The one
/// case that cannot be hermetic is the environment override, which is exercised through
/// <c>AnthillRuntime.Initialize</c> because that is the only path an operator's variables actually
/// travel; it restores the runtime afterwards.
/// </summary>
public class MissionReplayConfigTests
{
    private static MissionReplayOptions Options(bool enabled = false, string vault = "", string? tag = null, bool learning = false) =>
        new()
        {
            Enabled = enabled,
            VaultPath = vault,
            ReplayTag = tag ?? MissionReplayOptions.DefaultReplayTag,
            LearningEnabled = learning,
        };

    // ---- 1. defaults ---------------------------------------------------------------------------

    /// <summary>
    /// The shipped defaults are the safe ones. Asserted on a fresh <see cref="AnthillConfig"/>
    /// rather than on the record, because the config object is what a first run actually gets.
    /// </summary>
    [Fact]
    public void Defaults_AreOffAndCarryTheStandardTag()
    {
        var config = new AnthillConfig();

        Assert.False(config.MissionReplayEnabled);
        Assert.False(config.MissionReplayLearningEnabled);
        Assert.Equal("", config.MissionReplayVaultPath);
        Assert.Equal("anthill/replay", config.MissionReplayTag);
        Assert.Equal(MissionReplayOptions.DefaultReplayTag, config.MissionReplayTag);
    }

    /// <summary>The runtime's pre-projection value is the safe state, not an uninitialised one.</summary>
    [Fact]
    public void TheOffValue_IsSafeAndInoperable()
    {
        Assert.False(MissionReplayOptions.Off.Enabled);
        Assert.False(MissionReplayOptions.Off.LearningEnabled);
        Assert.False(MissionReplayOptions.Off.IsOperable);
        Assert.False(MissionReplayOptions.Off.LearningEffective);
        Assert.Empty(MissionReplayOptions.Off.Validate());
    }

    // ---- 2. a valid configured vault ------------------------------------------------------------

    [Fact]
    public void EnabledWithARealVault_IsValidAndOperable()
    {
        var vault = Directory.CreateTempSubdirectory("anthill-vault-").FullName;
        try
        {
            var options = Options(enabled: true, vault: vault);

            Assert.Empty(options.Validate());
            Assert.True(options.IsOperable);
            Assert.Equal(vault, options.ResolvedVaultPath);
            // Configured but not learning: the two switches are independent, and learning is opt-in.
            Assert.False(options.LearningEffective);
        }
        finally
        {
            Directory.Delete(vault, recursive: true);
        }
    }

    [Fact]
    public void EnabledWithARealVaultAndLearning_TurnsLearningOn()
    {
        var vault = Directory.CreateTempSubdirectory("anthill-vault-").FullName;
        try
        {
            var options = Options(enabled: true, vault: vault, learning: true);

            Assert.Empty(options.Validate());
            Assert.True(options.IsOperable);
            Assert.True(options.LearningEffective);
        }
        finally
        {
            Directory.Delete(vault, recursive: true);
        }
    }

    /// <summary>A relative path is resolved, not rejected — and resolution never throws.</summary>
    [Fact]
    public void ARelativeVaultPath_IsResolvedAgainstTheWorkingDirectory()
    {
        var options = Options(enabled: true, vault: "some/relative/vault");
        Assert.Equal(Path.GetFullPath("some/relative/vault"), options.ResolvedVaultPath);
    }

    // ---- 3. replay disabled with no vault -------------------------------------------------------

    /// <summary>
    /// The overwhelmingly common state, and the one that must never complain: replay off, no vault.
    /// The filesystem is not probed at all while the feature is off, so a stale or unmounted path
    /// left in a config file cannot produce a finding either.
    /// </summary>
    [Fact]
    public void DisabledWithNoVault_IsValid()
    {
        var options = Options();

        Assert.Empty(options.Validate());
        Assert.False(options.IsOperable);
    }

    [Fact]
    public void DisabledWithAVaultThatDoesNotExist_IsStillValid()
    {
        var options = Options(vault: "/no/such/directory/anywhere");

        Assert.Empty(options.Validate());
        Assert.False(options.IsOperable);
    }

    // ---- 4. enabled with a missing vault path ----------------------------------------------------

    [Fact]
    public void EnabledWithNoVaultPath_IsReportedAndNotOperable()
    {
        var options = Options(enabled: true, vault: "");

        var finding = Assert.Single(options.Validate());
        Assert.Equal("mission_replay_without_vault_path", finding.Combination);
        Assert.Contains("mission_replay_vault_path", finding.Detail, StringComparison.Ordinal);
        Assert.False(options.IsOperable);
    }

    [Fact]
    public void EnabledWithAVaultThatIsNotThere_IsReportedAndNotOperable()
    {
        var missing = Path.Combine(Path.GetTempPath(), "anthill-vault-missing-" + Guid.NewGuid().ToString("N"));
        var options = Options(enabled: true, vault: missing);

        var finding = Assert.Single(options.Validate());
        Assert.Equal("mission_replay_vault_missing", finding.Combination);
        Assert.Contains(missing, finding.Detail, StringComparison.Ordinal);
        Assert.False(options.IsOperable);
    }

    /// <summary>A file is not a vault, and saying so beats "does not exist" when it plainly does.</summary>
    [Fact]
    public void EnabledWithAFileInsteadOfADirectory_SaysSo()
    {
        var file = Path.GetTempFileName();
        try
        {
            var finding = Assert.Single(Options(enabled: true, vault: file).Validate());
            Assert.Equal("mission_replay_vault_not_a_directory", finding.Combination);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ---- 5. an empty replay tag -------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EnabledWithAnEmptyTag_IsReportedAndNotOperable(string tag)
    {
        var vault = Directory.CreateTempSubdirectory("anthill-vault-").FullName;
        try
        {
            var options = Options(enabled: true, vault: vault, tag: tag);

            var finding = Assert.Single(options.Validate());
            Assert.Equal("mission_replay_without_tag", finding.Combination);
            Assert.Contains("mission_replay_tag", finding.Detail, StringComparison.Ordinal);
            Assert.False(options.IsOperable);
        }
        finally
        {
            Directory.Delete(vault, recursive: true);
        }
    }

    /// <summary>An empty tag while replay is OFF is not a problem yet, and is not reported as one.</summary>
    [Fact]
    public void DisabledWithAnEmptyTag_IsValid()
    {
        Assert.Empty(Options(tag: "").Validate());
    }

    // ---- 6. learning enabled while replay is disabled ---------------------------------------------

    /// <summary>
    /// Reported, and deterministically inert — not silently rewritten. `RuntimeConfigValidator`'s
    /// house rule is to degrade loudly rather than refuse to boot, so this is a finding plus a
    /// <see cref="MissionReplayOptions.LearningEffective"/> of false, and the operator's file keeps
    /// saying exactly what they wrote.
    /// </summary>
    [Fact]
    public void LearningWithoutReplay_IsReportedAndHasNoEffect()
    {
        var options = Options(enabled: false, learning: true);

        var finding = Assert.Single(options.Validate());
        Assert.Equal("mission_replay_learning_without_replay", finding.Combination);
        Assert.Contains("mission_replay_learning_enabled", finding.Detail, StringComparison.Ordinal);

        Assert.False(options.LearningEffective);
        // The configured value is preserved rather than normalised away.
        Assert.True(options.LearningEnabled);
    }

    /// <summary>Learning cannot survive a replay configuration that is switched on but unusable.</summary>
    [Fact]
    public void LearningWithAnUnusableVault_IsAlsoInert()
    {
        var options = Options(enabled: true, vault: "", learning: true);

        Assert.False(options.IsOperable);
        Assert.False(options.LearningEffective);
    }

    // ---- 9. malformed values fail rather than silently defaulting ---------------------------------

    /// <summary>
    /// A wrong-typed value in config.json is a typo the operator must see. System.Text.Json refuses
    /// it; this pins that the refusal is not softened into a default somewhere along the way.
    /// </summary>
    [Fact]
    public void AWrongTypedValue_IsRefusedRatherThanDefaulted()
    {
        const string json = """{"mission_replay_enabled": "yes"}""";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<AnthillConfig>(json, AnthillConfig.JsonOptions));
    }

    /// <summary>And a well-formed file that omits the section simply gets the safe defaults.</summary>
    [Fact]
    public void AConfigWithoutTheSection_GetsTheSafeDefaults()
    {
        var config = JsonSerializer.Deserialize<AnthillConfig>("""{"api_port": 8713}""", AnthillConfig.JsonOptions)!;

        Assert.False(config.MissionReplayEnabled);
        Assert.False(config.MissionReplayLearningEnabled);
        Assert.Equal(MissionReplayOptions.DefaultReplayTag, config.MissionReplayTag);
    }

    /// <summary>The four keys round-trip through the serializer under their documented names.</summary>
    [Fact]
    public void TheKeys_RoundTripUnderTheirDocumentedNames()
    {
        var vault = Path.Combine(Path.GetTempPath(), "vault");
        var json = JsonSerializer.Serialize(
            new AnthillConfig
            {
                MissionReplayEnabled = true,
                MissionReplayVaultPath = vault,
                MissionReplayTag = "team/replay",
                MissionReplayLearningEnabled = true,
            }, AnthillConfig.JsonOptions);

        Assert.Contains("\"mission_replay_enabled\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"mission_replay_tag\": \"team/replay\"", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize<AnthillConfig>(json, AnthillConfig.JsonOptions)!;
        Assert.True(back.MissionReplayEnabled);
        Assert.Equal(vault, back.MissionReplayVaultPath);
        Assert.Equal("team/replay", back.MissionReplayTag);
        Assert.True(back.MissionReplayLearningEnabled);
    }
}
