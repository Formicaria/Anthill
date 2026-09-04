using System.Reflection;
using System.Text.Json.Serialization;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// MISSION REPLAY IN THE GENERATED SURFACE.
///
/// `ConfigCatalogTests` already regenerates `config.example.json` and `docs/CONFIGURATION.md` and
/// fails on any difference, which catches a forgotten regeneration. It cannot catch a key that was
/// declared in a way that renders nothing useful — so these assert what the four keys are worth to
/// an operator reading the artifacts: they are present, they carry their environment overrides, the
/// safety promise is written down, and they are NOT live-writable by the settings surface.
/// </summary>
public class MissionReplayCatalogTests
{
    private static readonly string[] Keys =
    [
        "mission_replay_enabled",
        "mission_replay_vault_path",
        "mission_replay_tag",
        "mission_replay_learning_enabled",
    ];

    [Fact]
    public void EveryKey_IsDeclaredAndDocumented()
    {
        foreach (var key in Keys)
        {
            var declaration = ConfigCatalog.Find(key);
            Assert.True(declaration is not null, $"{key} is not in the catalog.");
            Assert.True(declaration!.IsDocumented, $"{key} is hidden from the generated example file.");
        }
    }

    [Fact]
    public void EveryKey_DeclaresTheEnvironmentOverrideItsDocumentationPromises()
    {
        Assert.Equal("ANTHILL_MISSION_REPLAY_ENABLED", ConfigCatalog.Find("mission_replay_enabled")!.EnvOverride);
        Assert.Equal("ANTHILL_MISSION_REPLAY_VAULT_PATH", ConfigCatalog.Find("mission_replay_vault_path")!.EnvOverride);
        Assert.Equal("ANTHILL_MISSION_REPLAY_TAG", ConfigCatalog.Find("mission_replay_tag")!.EnvOverride);
        Assert.Equal("ANTHILL_MISSION_REPLAY_LEARNING_ENABLED", ConfigCatalog.Find("mission_replay_learning_enabled")!.EnvOverride);
    }

    /// <summary>
    /// Both switches change what the colony may do, and are classified as such — which is what makes
    /// them render with the safety marking in the operator's reference.
    /// </summary>
    [Fact]
    public void TheTwoSwitches_AreClassifiedAsSafety()
    {
        Assert.Equal(ConfigSecurity.Safety, ConfigCatalog.Find("mission_replay_enabled")!.Security);
        Assert.Equal(ConfigSecurity.Safety, ConfigCatalog.Find("mission_replay_learning_enabled")!.Security);
    }

    /// <summary>
    /// THE SETTINGS SURFACE MAY NOT FLIP THESE LIVE, and that is deliberate rather than an oversight.
    /// `mission_replay_enabled` gates a capability that will eventually execute missions; widening
    /// what the console can write is a decision for the release that ships the engine, not a side
    /// effect of the release that declares the keys. `TheEditableSurface_IsExactlyWhatItWasBefore..`
    /// pins the count at 98, and this says why these four are not among them.
    /// </summary>
    [Fact]
    public void NoKey_IsLiveWritableByTheSettingsSurface()
    {
        foreach (var key in Keys)
        {
            Assert.False(ConfigCatalog.IsEditable(key),
                $"{key} became live-editable. That widens what the console may change without a "
              + "restart, which is a security decision — make it deliberately and update "
              + "TheEditableSurface_IsExactlyWhatItWasBeforeItBecameAProjection in the same commit.");
        }
    }

    /// <summary>
    /// The example file states the safety guarantee in the operator's own words. This is the promise
    /// the release is making — that configuring a vault does nothing by itself — so it is pinned
    /// rather than left to survive the next edit of a long string by luck.
    /// </summary>
    [Fact]
    public void TheExampleFile_PromisesThatConfiguringAVaultDoesNothingByItself()
    {
        var json = ConfigCatalog.RenderExampleJson();

        Assert.Contains("\"mission_replay_enabled\": false", json, StringComparison.Ordinal);
        Assert.Contains("\"mission_replay_learning_enabled\": false", json, StringComparison.Ordinal);

        var note = ConfigCatalog.Find("mission_replay_enabled")!.SectionNote;
        Assert.Contains("does NOT import", note, StringComparison.Ordinal);
        Assert.Contains("does NOT modify pheromones", note, StringComparison.Ordinal);
        Assert.Contains("CONFIGURATION ONLY", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// The defaults the catalog reports come off a fresh instance, so this also pins that the shipped
    /// defaults are the safe ones as the GENERATOR sees them — not only as the type declares them.
    /// </summary>
    [Fact]
    public void TheRenderedDefaults_AreTheSafeOnes()
    {
        Assert.Equal(false, ConfigCatalog.Find("mission_replay_enabled")!.Default);
        Assert.Equal(false, ConfigCatalog.Find("mission_replay_learning_enabled")!.Default);
        Assert.Equal("", ConfigCatalog.Find("mission_replay_vault_path")!.Default);
        Assert.Equal("anthill/replay", ConfigCatalog.Find("mission_replay_tag")!.Default);
    }

    /// <summary>
    /// SCOPE GUARD. This release declares configuration and nothing else: no vault reader, no
    /// Markdown parser, no mission generator, no scheduler. A later release adds those deliberately;
    /// until then, the only type carrying the feature's name is the options record itself.
    /// </summary>
    [Fact]
    public void NoReplayEngine_ShippedWithTheConfiguration()
    {
        var replayTypes = typeof(AnthillConfig).Assembly
            .GetTypes()
            .Where(t => t.Name.Contains("Replay", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name)
            .ToList();

        Assert.Equal(["MissionReplayOptions"], replayTypes);
    }
}
