using System.Text.Json;
using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The roster default moves to `full`, and no operator loses a decision to it. v0.3.8.41.
///
/// The migration exists to answer ONE question that the on-disk bytes cannot: is
/// <c>"roster_profile": "core"</c> a choice, or a default nobody touched? Both look identical, and
/// the two correct answers are opposite — override the second, never the first.
///
/// <see cref="ConfigSchema"/> breaks the tie with a schema version, and these tests are the four
/// cases the brief names: new, untouched legacy, customised, and disabled-role. The fifth test is
/// the one that matters most in a year — that the key list the migration inspects still covers every
/// switch the roster actually reads, so a switch added later cannot make a customised configuration
/// look untouched.
/// </summary>
public class ConfigMigrationTests
{
    private static IReadOnlyDictionary<string, JsonElement> Raw(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    // -------------------------------------------------------------------------------------------
    // 1. New installation
    // -------------------------------------------------------------------------------------------

    /// <summary>No file on disk: the shipped default applies, and it is `full`.</summary>
    [Fact]
    public void ANewInstallation_GetsTheFullRoster()
    {
        var plan = ConfigSchema.Plan(new Dictionary<string, JsonElement>());

        Assert.Equal(ConfigMigrationAction.FreshInstall, plan.Action);
        Assert.Equal(RosterProfiles.Full, plan.RosterProfile);
        Assert.Equal(ConfigSchema.Current, plan.ToVersion);
    }

    /// <summary>And the config object a fresh install serialises agrees with the plan.</summary>
    [Fact]
    public void TheShippedDefaults_MatchWhatAFreshInstallIsMigratedTo()
    {
        var shipped = new AnthillConfig();
        var plan = ConfigSchema.Plan(new Dictionary<string, JsonElement>());

        Assert.Equal(plan.RosterProfile, shipped.RosterProfile);
        Assert.Equal(plan.ToVersion, shipped.ConfigSchemaVersion);
    }

    // -------------------------------------------------------------------------------------------
    // 2. Untouched legacy
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A real v3.8.26-era config: the roster keys are all present and all at their legacy defaults.
    /// Nobody configured the roster, so the new default applies.
    /// </summary>
    [Fact]
    public void AnUntouchedLegacyConfig_IsMigratedToFull()
    {
        var plan = ConfigSchema.Plan(Raw("""
        {
          "config_version": "config-v1",
          "safety_profile": "SAFE_LOCAL",
          "roster_profile": "core",
          "disabled_roles": [],
          "specialist_ant_execution_enabled": false,
          "tester_ant_enabled": false,
          "soldier_ant_enabled": false,
          "medic_ant_enabled": false,
          "archivist_ant_enabled": false,
          "ui_cartographer_ant_enabled": false,
          "scribe_ant_enabled": false,
          "handoff_ingestion_enabled": false,
          "adaptive_mission_control_enabled": false
        }
        """));

        Assert.Equal(ConfigMigrationAction.AdoptedFullRoster, plan.Action);
        Assert.Equal(RosterProfiles.Full, plan.RosterProfile);
        Assert.Equal(ConfigSchema.UnversionedLegacy, plan.FromVersion);
        Assert.Equal(ConfigSchema.Current, plan.ToVersion);
        Assert.True(plan.ChangedTheRoster);
    }

    /// <summary>
    /// A legacy config that never mentioned the roster at all — the common case, because these keys
    /// only appear once something writes them. Absent must read as untouched, not as customised.
    /// </summary>
    [Fact]
    public void ALegacyConfigWithNoRosterKeys_IsAlsoMigrated()
    {
        var plan = ConfigSchema.Plan(Raw("""
        { "safety_profile": "SAFE_LOCAL", "api_port": 8713 }
        """));

        Assert.Equal(ConfigMigrationAction.AdoptedFullRoster, plan.Action);
        Assert.Equal(RosterProfiles.Full, plan.RosterProfile);
    }

    // -------------------------------------------------------------------------------------------
    // 3. Customised
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// An operator who hand-enabled two specialists made a decision about the roster. The migration
    /// does not widen it — widening a partial rollout is exactly the surprise the version exists to
    /// prevent — and the explanation says how to adopt the full profile deliberately.
    /// </summary>
    [Fact]
    public void AHandConfiguredRoster_IsPreservedNotWidened()
    {
        var plan = ConfigSchema.Plan(Raw("""
        {
          "roster_profile": "core",
          "specialist_ant_execution_enabled": true,
          "tester_ant_enabled": true,
          "soldier_ant_enabled": false
        }
        """));

        Assert.Equal(ConfigMigrationAction.PreservedOperatorChoice, plan.Action);
        Assert.Equal(RosterProfiles.Core, plan.RosterProfile);
        Assert.False(plan.ChangedTheRoster);
        Assert.Contains("roster_profile", plan.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// A deliberate `core` recorded at the CURRENT schema version is untouchable. This is the whole
    /// reason the version exists: after this release, `core` on disk can only have been chosen.
    /// </summary>
    [Fact]
    public void AnExplicitCoreAtTheCurrentVersion_IsNeverMigrated()
    {
        var plan = ConfigSchema.Plan(Raw($$"""
        { "config_schema_version": {{ConfigSchema.Current}}, "roster_profile": "core" }
        """));

        Assert.Equal(ConfigMigrationAction.AlreadyCurrent, plan.Action);
        Assert.Equal(RosterProfiles.Core, plan.RosterProfile);
        Assert.False(plan.ChangedTheRoster);
    }

    /// <summary>An operator already on `full` is left exactly where they are, and not re-announced.</summary>
    [Fact]
    public void AnOperatorAlreadyOnFull_IsLeftAlone()
    {
        var plan = ConfigSchema.Plan(Raw("""{ "roster_profile": "full" }"""));

        Assert.Equal(ConfigMigrationAction.PreservedOperatorChoice, plan.Action);
        Assert.Equal(RosterProfiles.Full, plan.RosterProfile);
        Assert.False(plan.ChangedTheRoster);
    }

    // -------------------------------------------------------------------------------------------
    // 4. Disabled roles
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE one a migration must never get wrong. A kill switch survives the migration verbatim: an
    /// operator who turned the scribe off gets eleven roles, not twelve, and does not have to notice
    /// that an upgrade quietly re-enabled the role they switched off for a reason.
    /// </summary>
    [Fact]
    public void KillSwitchesSurviveTheMigration()
    {
        var plan = ConfigSchema.Plan(Raw("""
        { "roster_profile": "core", "disabled_roles": ["scribe", "medic"] }
        """));

        Assert.Equal(ConfigMigrationAction.AdoptedFullRoster, plan.Action);
        Assert.Equal(RosterProfiles.Full, plan.RosterProfile);
        Assert.Equal(new[] { "scribe", "medic" }, plan.DisabledRoles);

        // And they still subtract from the profile they were migrated into.
        var roster = RosterProfiles.Resolve(plan.RosterProfile, plan.DisabledRoles,
            new RosterActivation(false, ActivationTier.Core, false, false, false, false, false, false, false, false));

        Assert.False(roster.Scribe);
        Assert.False(roster.Medic);
        Assert.True(roster.Tester);
        Assert.True(roster.Soldier);
        Assert.True(roster.Archivist);
        Assert.True(roster.UiCartographer);
    }

    /// <summary>Kill switches survive a preserved-choice outcome too, not only a migrated one.</summary>
    [Fact]
    public void KillSwitchesSurviveAPreservedChoice()
    {
        var plan = ConfigSchema.Plan(Raw("""
        { "roster_profile": "full", "disabled_roles": ["soldier"] }
        """));

        Assert.Equal(ConfigMigrationAction.PreservedOperatorChoice, plan.Action);
        Assert.Equal(new[] { "soldier" }, plan.DisabledRoles);
    }

    // -------------------------------------------------------------------------------------------
    // 5. The list that decides "untouched" must stay complete
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Every switch the full profile turns on must be one the migration inspects.
    ///
    /// Without this the migration decays silently: a seventh role added to
    /// <see cref="RosterProfiles"/> and not added here would mean an operator who hand-enabled ONLY
    /// that role reads as untouched, and gets the whole roster switched on underneath them. The
    /// failure would be invisible — every test above would still pass — which is the shape of defect
    /// this repository keeps finding.
    /// </summary>
    [Fact]
    public void TheMigrationInspects_EverySwitchTheFullProfileTurnsOn()
    {
        var inspected = ConfigSchema.LegacyOffByDefaultKeys.ToHashSet(StringComparer.Ordinal);

        var expected = RosterProfiles.SwitchableRoles
            .Select(role => $"{role}_ant_enabled")
            .Concat(new[]
            {
                "specialist_ant_execution_enabled",
                "handoff_ingestion_enabled",
                "adaptive_mission_control_enabled",
            })
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var missing = expected.Where(k => !inspected.Contains(k)).ToList();

        Assert.True(missing.Count == 0,
            "ConfigSchema.LegacyOffByDefaultKeys does not inspect every switch the full profile "
            + "turns on, so a config customised through one of these would be migrated as if it were "
            + "untouched: " + string.Join(", ", missing));
    }

    /// <summary>
    /// And nothing in that list is dead. An inspected key that no longer exists would quietly stop
    /// protecting anything while continuing to look like it does.
    /// </summary>
    [Fact]
    public void TheMigrationInspectsNothingThatNoLongerExists()
    {
        var shipped = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(new AnthillConfig(), AnthillConfig.JsonOptions))!;

        var stale = ConfigSchema.LegacyOffByDefaultKeys
            .Where(k => !shipped.ContainsKey(k))
            .ToList();

        Assert.True(stale.Count == 0,
            "these keys are inspected by the migration but are not in the configuration schema: "
            + string.Join(", ", stale));
    }
}
