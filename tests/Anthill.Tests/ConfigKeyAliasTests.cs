using System.Text.Json;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A RENAMED SETTING IS STILL THE OPERATOR'S SETTING. v0.3.8.126.
///
/// `ConfigKeyAttribute.Aliases` has existed since v0.3.8.91, and its own doc comment says "the
/// migration reads these". Nothing read them. The only consumer was `RenderMarkdown`, which prints
/// "was: old_name" in the configuration reference — so a renamed key was DOCUMENTED as renamed and
/// then silently dropped on load, because `System.Text.Json` binds on `[JsonPropertyName]` and
/// nothing else. A declaration that reaches nobody: defect class #2, inside the mechanism built to
/// stop settings drifting away from their documentation.
///
/// It cost nothing for thirty-five releases because no key had ever declared an alias. v0.3.8.126
/// renames forty at once. Without this, every existing `config.json` would load, parse cleanly,
/// report no error, and quietly revert forty settings to their defaults — including safety gates.
/// That is the failure this file exists to make impossible, so it is tested BEFORE the rename that
/// needs it rather than alongside.
/// </summary>
public class ConfigKeyAliasTests
{
    private static Dictionary<string, JsonElement> Doc(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    /// <summary>
    /// A fixture catalog, so the RULES are proved on their own rather than through whichever keys
    /// the current release happens to have renamed. This mechanism went thirty-five releases with
    /// no user at all; a test that only says "the current catalog behaves" would go quiet again the
    /// moment the last rename ages out.
    /// </summary>
    private static IReadOnlyList<ConfigDeclaration> Fixture(string key, params string[] aliases) =>
        [new ConfigDeclaration(key, typeof(bool), false, "", ConfigExposure.FileOnly,
            ConfigSecurity.Ordinary, "", double.NaN, double.NaN, aliases, "", "", "", "")];

    /// <summary>
    /// The mechanism itself: a value written under a former name is read under the current one, and
    /// the former name does not survive alongside it.
    /// </summary>
    [Fact]
    public void AKeyWrittenUnderItsFormerName_IsReadUnderItsCurrentOne()
    {
        var doc = Doc("""{ "old_gate": true }""");

        var applied = ConfigCatalog.ApplyKeyAliases(doc, Fixture("new_gate", "old_gate"));

        Assert.Contains(applied, r => r.From == "old_gate" && r.To == "new_gate");
        Assert.True(doc["new_gate"].GetBoolean());
        Assert.False(doc.ContainsKey("old_gate"),
            "the former spelling must not survive alongside the current one");
    }

    /// <summary>
    /// THE CURRENT SPELLING WINS, AND THE STALE ONE IS DISCARDED.
    ///
    /// The rule that matters most, and the one a naive implementation gets backwards. A file
    /// carrying both names has already been migrated — by hand, or by a settings save that rewrote
    /// it — so the old key is a leftover. Preferring it would make an edit the operator made to the
    /// NEW key do nothing at all, silently, which is worse than not migrating in the first place.
    /// </summary>
    [Fact]
    public void WhenBothSpellingsArePresent_TheCurrentOneWins()
    {
        var doc = Doc("""{ "old_gate": false, "new_gate": true }""");

        var applied = ConfigCatalog.ApplyKeyAliases(doc, Fixture("new_gate", "old_gate"));

        Assert.Empty(applied);                    // nothing to report: the operator is already there
        Assert.True(doc["new_gate"].GetBoolean()); // and their value is untouched
        Assert.False(doc.ContainsKey("old_gate")); // the leftover is dropped rather than left to confuse
    }

    /// <summary>
    /// A key with two former names takes them in declared order, and only one of them: a document
    /// carrying both old spellings is an operator who renamed once and stopped, not a licence to
    /// apply the rename twice.
    /// </summary>
    [Fact]
    public void AKeyWithTwoFormerNames_TakesTheFirstThatMatches()
    {
        var doc = Doc("""{ "oldest_gate": false, "old_gate": true }""");

        var applied = ConfigCatalog.ApplyKeyAliases(doc, Fixture("new_gate", "old_gate", "oldest_gate"));

        Assert.Single(applied);
        Assert.Equal("old_gate", applied[0].From);
        Assert.True(doc["new_gate"].GetBoolean());
    }

    /// <summary>
    /// A DOCUMENT WITH NO FORMER SPELLINGS IS RETURNED EXACTLY AS IT CAME.
    ///
    /// The un-migrated case, which is every colony that has never upgraded across a rename and must
    /// not pay anything for the mechanism. Asserted against the whole document rather than the one
    /// key, because the way this goes wrong is by dropping something adjacent.
    /// </summary>
    [Fact]
    public void ADocumentWithNothingToRename_IsUnchanged()
    {
        var doc = Doc("""
        { "safety_profile": "SAFE_LOCAL", "api_port": 8787, "knowledge_enabled": true }
        """);

        var applied = ConfigCatalog.ApplyKeyAliases(doc);

        Assert.Empty(applied);
        Assert.Equal(3, doc.Count);
        Assert.Equal("SAFE_LOCAL", doc["safety_profile"].GetString());
        Assert.Equal(8787, doc["api_port"].GetInt32());
        Assert.True(doc["knowledge_enabled"].GetBoolean());
    }

    /// <summary>
    /// An empty document is not a special case, and neither is an unknown key. Both are ordinary
    /// inputs — a fresh install and a hand-edited file — and neither may throw.
    /// </summary>
    [Fact]
    public void AnEmptyOrUnrecognisedDocument_IsHandledWithoutThrowing()
    {
        Assert.Empty(ConfigCatalog.ApplyKeyAliases(new Dictionary<string, JsonElement>()));

        var stray = Doc("""{ "a_key_that_was_never_a_setting": 1 }""");
        Assert.Empty(ConfigCatalog.ApplyKeyAliases(stray));
        Assert.Single(stray);
    }

    /// <summary>
    /// NO ALIAS COLLIDES WITH A LIVE KEY.
    ///
    /// The one way this mechanism could destroy a setting rather than preserve one: if some key
    /// declared another key's CURRENT name as its former name, a document would have one of its
    /// live settings moved on top of a different setting. Impossible to spot by reading a diff of
    /// forty renames, trivial to assert.
    /// </summary>
    [Fact]
    public void NoFormerSpelling_IsAnotherSettingsCurrentName()
    {
        var current = ConfigCatalog.Declarations.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var declaration in ConfigCatalog.Declarations)
            foreach (var alias in declaration.Aliases)
            {
                Assert.False(current.Contains(alias),
                    $"'{declaration.Key}' claims '{alias}' as a former name, but '{alias}' is a live "
                  + "setting. Reading a document that has both would move one operator's setting on "
                  + "top of another.");

                Assert.NotEqual(declaration.Key, alias);
            }
    }

    /// <summary>
    /// AND NO TWO KEYS CLAIM THE SAME FORMER NAME. The other collision, and the one a bulk rename
    /// produces by copy-paste: two declarations both listing `infrastructure_enabled` would make which
    /// setting an old document fed depend on declaration order.
    /// </summary>
    [Fact]
    public void NoTwoSettings_ClaimTheSameFormerName()
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var declaration in ConfigCatalog.Declarations)
            foreach (var alias in declaration.Aliases)
            {
                Assert.False(owners.TryGetValue(alias, out var first),
                    $"'{alias}' is claimed as a former name by both '{first}' and '{declaration.Key}'.");
                owners[alias] = declaration.Key;
            }
    }

    /// <summary>
    /// THE RENAME REACHES THE RUNTIME, not just the dictionary.
    ///
    /// The black-box half. Everything above tests `ApplyKeyAliases` in isolation, and a mechanism
    /// that works perfectly and is never called is the exact defect this file was written about. So
    /// this asserts the CALL SITE: the loader consults the catalog before it merges defaults, which
    /// is the only ordering under which "was the current spelling present?" can be answered.
    /// </summary>
    [Fact]
    public void TheLoader_AppliesAliasesBeforeItOverlaysDefaults()
    {
        var runtime = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Configuration", "AnthillRuntime.cs")));

        var call = runtime.IndexOf("ConfigCatalog.ApplyKeyAliases(raw)", StringComparison.Ordinal);
        Assert.True(call > 0, "the config loader never applies key aliases, so a renamed key is dropped.");

        // Before the profile overlay, and before the roster plan — both of which read a document
        // that must already be speaking current names.
        var overlay = runtime.IndexOf("AnthillConfig.ApplySafetyProfile(config, requestedProfile)", StringComparison.Ordinal);
        var plan = runtime.IndexOf("ConfigSchema.Plan(raw)", StringComparison.Ordinal);
        Assert.True(overlay > call, "aliases must be applied before profile defaults are overlaid.");
        Assert.True(plan > call, "aliases must be applied before the roster migration reads the document.");

        // And it is reported rather than done silently.
        Assert.Contains("[config-rename]", runtime, StringComparison.Ordinal);
    }
}
