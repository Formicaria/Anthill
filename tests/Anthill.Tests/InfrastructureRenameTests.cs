using Anthill.Core.Security;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE SUBSYSTEM IS CALLED INFRASTRUCTURE, AND NOTHING AN OPERATOR SAVED WAS LOST SAYING SO.
/// v0.3.8.128.
///
/// The label changed at v2.6 and again at v0.3.8.122, and both times the IDS were deliberately kept
/// — `.122`'s changelog says so in as many words: "the sector id and its eight roles do not, so
/// saved layouts survive." This release renames the ids too, which means the argument that stopped
/// it twice has to be answered rather than overruled.
///
/// It is answered in five places, and this file is the list of them. Every one is a READ that
/// understands the old spelling, not a migration pass that rewrites and hopes:
///
///   · `users.role`            — `UserRoles.Normalize`, consulted on every role read
///   · the colony-live layout  — rewritten as it is applied, saved back under the new id
///   · the dashboard zone      — rewritten before the zone is read
///   · the module's tables     — `ALTER TABLE ... RENAME TO`, before the idempotent DDL
///   · the kill-switch file    — both names honoured, forever
///
/// The last of those is the one that would have been worst. `HOMELAB_STOP` is a file an operator
/// creates BY HAND to halt a misbehaving colony. Reading only the new name would have meant a
/// release silently resuming actions somebody had stopped — a kill switch failing OPEN, quietly.
/// </summary>
public class InfrastructureRenameTests
{
    private static string Src(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { SourceText.RepoRoot() }.Concat(parts).ToArray()));

    /// <summary>
    /// AN OPERATOR WHO WAS A `homelab_operator` STILL IS ONE.
    ///
    /// `users.role` is a persisted column, so the literal is on disk for every account that had the
    /// role. Driven through the real normalizer rather than asserted about the source, because the
    /// property is "this account keeps its permissions", not "a string appears in a switch".
    /// </summary>
    [Theory]
    [InlineData("homelab_operator")]
    [InlineData("homelab-operator")]
    [InlineData("HOMELAB_OPERATOR")]
    [InlineData("infrastructure_operator")]
    public void AnAccountSavedUnderTheOldRoleName_KeepsItsRole(string stored)
    {
        Assert.Equal(UserRoles.InfrastructureOperator, UserRoles.Normalize(stored));
    }

    /// <summary>
    /// And the rename did not quietly widen what that role may do. The permission set is the same
    /// set under a new name; a rename that also granted something would be a privilege change
    /// wearing a refactor's clothes.
    /// </summary>
    [Fact]
    public void TheRenamedRole_HasTheSamePermissionsItAlwaysHad()
    {
        var role = UserRoles.InfrastructureOperator;

        // What it has always been able to do: look, and approve an action somebody else executes.
        foreach (var granted in new[]
                 { "read_infrastructure", "approve_infrastructure_actions",
                   "read_micromound", "approve_micromound_actions", "run_mission" })
            Assert.True(UserRoles.RoleAllows(role, granted),
                $"the infrastructure operator lost '{granted}' in the rename.");

        // And what it has never been able to do. A rename that also granted something would be a
        // privilege change wearing a refactor's clothes — EXECUTING an action, writing credentials
        // or the allowlist, and minting device identities are all admin acts.
        foreach (var refused in new[]
                 { "execute_infrastructure_actions", "manage_infrastructure_integrations",
                   "manage_micromound", "manage_settings", "manage_users" })
            Assert.False(UserRoles.RoleAllows(role, refused),
                $"the infrastructure operator gained '{refused}' in the rename.");
    }

    /// <summary>
    /// THE TABLE RENAME RUNS BEFORE THE DDL, and that ordering is the whole correctness argument.
    ///
    /// `CREATE TABLE IF NOT EXISTS` is idempotent in the worst possible way here: run first against
    /// a pre-rename database and SQLite creates five EMPTY `infrastructure_*` tables beside five
    /// populated `homelab_*` ones. The rename then finds its destination occupied, does nothing, and
    /// the colony comes up with an empty inventory, an empty credential store and an empty
    /// allowlist. Nothing errors. The operator's hosts are simply gone.
    ///
    /// Asserted by ORDER in the source, because there is no way to observe it from outside without
    /// a pre-rename database to open — and a test that built one would be asserting against a
    /// fixture rather than against the statement that actually runs.
    /// </summary>
    [Fact]
    public void TheTableRename_RunsBeforeTheCreateStatements()
    {
        var repo = Src("src", "Anthill.Modules", "Anthill.Modules.Infrastructure",
                       "Infrastructure", "InfrastructureRepository.cs");

        var rename = repo.IndexOf("RenameLegacyHomelabTables(conn, tx);", StringComparison.Ordinal);
        var ddl = repo.IndexOf("foreach (var ddl in SchemaStatements)", StringComparison.Ordinal);

        Assert.True(rename > 0, "the legacy table rename is gone — a pre-rename database loses its data.");
        Assert.True(ddl > rename,
            "CREATE TABLE IF NOT EXISTS runs before the rename. On an existing database that creates "
          + "empty tables beside the populated ones and the rename then silently declines.");

        // All five, and only tables that actually carried the prefix.
        foreach (var pair in new[] { "homelab_nodes", "homelab_events", "homelab_credentials",
                                     "homelab_target_allowlist", "homelab_meta" })
            Assert.Contains(pair, repo, StringComparison.Ordinal);

        // A database with BOTH is left alone rather than guessed about.
        Assert.Contains("if (!TableExists(conn, tx, legacy) || TableExists(conn, tx, current)) continue;",
            repo, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE KILL SWITCH HONOURS BOTH SENTINELS, AND RESUME CLEARS BOTH.
    ///
    /// Reading only the new name fails OPEN: a colony an operator halted resumes itself on upgrade.
    /// Deleting only the new name on resume fails CLOSED: a colony that reports itself resumed and
    /// refuses every action, with the reason in a file nobody thinks to look for. Both directions
    /// are silent, which is why both are pinned.
    /// </summary>
    [Fact]
    public void BothKillSwitchSentinels_AreHonoured_AndBothAreClearedOnResume()
    {
        var control = Src("src", "Anthill.Modules", "Anthill.Modules.Infrastructure",
                          "Infrastructure", "Actions", "InfrastructureActionControl.cs");

        Assert.Contains("\"HOMELAB_STOP\"", control, StringComparison.Ordinal);
        Assert.Contains("File.Exists(StopFilePath()) || File.Exists(LegacyStopFilePath())",
            control, StringComparison.Ordinal);
        Assert.Contains("File.Delete(LegacyStopFilePath())", control, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE TWO PERSISTED CLIENT IDS ARE REWRITTEN ON READ.
    ///
    /// A saved colony layout is keyed on the sector id, and the dashboard's widget placements are
    /// keyed on the zone id. `.122` refused to rename the first for precisely this reason, and the
    /// CHANGELOG records the same refusal for the Agent Inspector's widget id. Renaming them is only
    /// safe because the read understands both — and because a document carrying both keeps the
    /// CURRENT one, so an operator who has already rearranged since upgrading is not undone by a
    /// leftover.
    /// </summary>
    [Fact]
    public void ASavedLayoutOrWidgetPlacement_SurvivesTheRename()
    {
        var live = Src("src", "Anthill.UI", "colony-live.js");
        Assert.Contains("function migrateLegacySectorId(l)", live, StringComparison.Ordinal);
        Assert.Contains("b.infrastructure = b.homelab;", live, StringComparison.Ordinal);
        // Applied on the one door every saved layout comes through.
        Assert.Contains("l = migrateLegacySectorId(l);", live, StringComparison.Ordinal);

        var infra = Src("src", "Anthill.UI", "infrastructure.js");
        Assert.Contains("function wgtZoneMigrateLegacy()", infra, StringComparison.Ordinal);
        Assert.Contains("if(!Array.isArray(w.infrastructure)) w.infrastructure=w.homelab;",
            infra, StringComparison.Ordinal);
    }

    /// <summary>
    /// EVERY RENAMED SETTING DECLARES ITS FORMER NAME.
    ///
    /// Forty keys. Without the aliases, an existing `config.json` parses cleanly, reports no error,
    /// and reverts all forty to their defaults — safety gates among them. The alias MECHANISM is
    /// guarded in `ConfigKeyAliasTests`; this is the half that says every key actually uses it,
    /// which is the part a bulk rename gets wrong by missing one.
    /// </summary>
    [Fact]
    public void EveryInfrastructureSetting_StillAnswersToItsFormerName()
    {
        var renamed = Anthill.Core.Configuration.ConfigCatalog.Declarations
            .Where(d => d.Key.StartsWith("infrastructure_", StringComparison.Ordinal))
            .ToList();

        Assert.True(renamed.Count >= 40,
            $"only {renamed.Count} infrastructure settings were found; the rename was expected to "
          + "move forty, so either the prefix changed or this guard is watching a subset.");

        foreach (var d in renamed)
        {
            var expected = "homelab_" + d.Key["infrastructure_".Length..];
            Assert.True(d.Aliases.Contains(expected),
                $"'{d.Key}' does not declare '{expected}' as a former name. An existing config.json "
              + "would lose this setting silently on upgrade.");
        }
    }
}
