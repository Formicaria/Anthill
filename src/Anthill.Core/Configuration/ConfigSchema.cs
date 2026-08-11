using System.Text.Json;

namespace Anthill.Core.Configuration;

/// <summary>What the migration did to an on-disk configuration. v0.3.8.41.</summary>
public enum ConfigMigrationAction
{
    /// <summary>No configuration file existed. The shipped defaults apply, unmigrated.</summary>
    FreshInstall,

    /// <summary>An untouched legacy configuration was moved to the full roster.</summary>
    AdoptedFullRoster,

    /// <summary>A legacy configuration carried operator choices. They were kept, verbatim.</summary>
    PreservedOperatorChoice,

    /// <summary>The configuration already carries the current schema version. Nothing to do.</summary>
    AlreadyCurrent,
}

/// <summary>
/// The result of planning a configuration migration — a value, not a mutation. v0.3.8.41.
/// </summary>
/// <param name="RosterProfile">The profile that should apply after migration.</param>
/// <param name="DisabledRoles">Kill switches, carried through untouched. A migration that dropped
/// one would silently re-enable a role an operator had turned off, which is the single worst thing
/// a migration can do.</param>
public sealed record ConfigMigrationResult(
    int FromVersion,
    int ToVersion,
    ConfigMigrationAction Action,
    string RosterProfile,
    IReadOnlyList<string> DisabledRoles,
    string Explanation)
{
    /// <summary>Whether anything about the effective roster actually changed.</summary>
    public bool ChangedTheRoster => Action == ConfigMigrationAction.AdoptedFullRoster;
}

/// <summary>
/// Configuration schema versioning, and the one migration that comes with it. v0.3.8.41.
///
/// THE PROBLEM A VERSION NUMBER SOLVES. The default roster profile moves from <c>core</c> to
/// <c>full</c> in this release. Without a schema version there is no way to tell an operator who
/// deliberately chose <c>core</c> from an installation that simply never touched the setting — the
/// on-disk bytes are identical, and both read <c>"roster_profile": "core"</c>. Migrating both would
/// override a deliberate choice; migrating neither would leave every existing installation on a
/// roster the release exists to replace.
///
/// The version breaks the tie. Below <see cref="Current"/> the value <c>core</c> is a DEFAULT nobody
/// selected; at <see cref="Current"/> it is a SELECTION, because the only way a file reaches this
/// version is by being written after the default changed.
///
/// A PURE FUNCTION over the raw on-disk document, for the same reason
/// <see cref="RosterProfiles.Resolve"/> is one: it can be exhaustively tested without a filesystem,
/// and it cannot be called before its inputs exist. It returns a plan; the caller applies it.
/// </summary>
public static class ConfigSchema
{
    /// <summary>The schema version this build writes.</summary>
    public const int Current = 2;

    /// <summary>The version an unversioned file is treated as.</summary>
    public const int UnversionedLegacy = 1;

    public const string VersionKey = "config_schema_version";
    public const string RosterProfileKey = "roster_profile";
    public const string DisabledRolesKey = "disabled_roles";

    /// <summary>
    /// The keys whose legacy defaults were all "off", and which together decided whether the roster
    /// ran. If every one of them is absent or still at its legacy default, nobody configured the
    /// roster and the new default may apply.
    ///
    /// Named as data rather than written into the condition below, so the list is reviewable and so
    /// <c>ConfigMigrationTests</c> can assert it covers every switch <see cref="RosterProfiles"/>
    /// actually reads.
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyOffByDefaultKeys = new[]
    {
        "specialist_ant_execution_enabled",
        "tester_ant_enabled",
        "soldier_ant_enabled",
        "medic_ant_enabled",
        "archivist_ant_enabled",
        "ui_cartographer_ant_enabled",
        "scribe_ant_enabled",
        "handoff_ingestion_enabled",
        "adaptive_mission_control_enabled",
    };

    /// <summary>
    /// Plan the migration for a raw on-disk configuration document.
    /// </summary>
    /// <param name="raw">The document exactly as parsed, before defaults are overlaid. Defaults must
    /// NOT be merged in first: this function's entire job is telling absent from present, and a
    /// merged document has no absent keys left.</param>
    public static ConfigMigrationResult Plan(IReadOnlyDictionary<string, JsonElement>? raw)
    {
        raw ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var disabled = ReadStringArray(raw, DisabledRolesKey);

        if (raw.Count == 0)
            return new ConfigMigrationResult(0, Current, ConfigMigrationAction.FreshInstall,
                RosterProfiles.Full, disabled,
                "New installation: the shipped default roster profile is 'full'.");

        var version = ReadInt(raw, VersionKey) ?? (raw.Count > 0 ? UnversionedLegacy : 0);

        if (version >= Current)
        {
            var chosen = ReadString(raw, RosterProfileKey) ?? RosterProfiles.Full;
            return new ConfigMigrationResult(version, version, ConfigMigrationAction.AlreadyCurrent,
                Normalise(chosen), disabled,
                $"Configuration is already at schema version {version}; '{Normalise(chosen)}' is an "
                + "explicit operator choice and is preserved.");
        }

        var onDiskProfile = ReadString(raw, RosterProfileKey);
        var touched = new List<string>();

        // An explicit non-legacy profile is a choice, whatever it says.
        if (onDiskProfile is not null && Normalise(onDiskProfile) != RosterProfiles.Core)
            touched.Add($"{RosterProfileKey}={Normalise(onDiskProfile)}");

        foreach (var key in LegacyOffByDefaultKeys)
            if (ReadBool(raw, key) == true)
                touched.Add($"{key}=true");

        if (touched.Count > 0)
        {
            var preserved = Normalise(onDiskProfile ?? RosterProfiles.Core);
            return new ConfigMigrationResult(version, Current, ConfigMigrationAction.PreservedOperatorChoice,
                preserved, disabled,
                $"Legacy configuration carries operator choices ({string.Join(", ", touched)}); "
                + $"roster profile left at '{preserved}'. Set \"{RosterProfileKey}\": \"full\" to adopt "
                + "the complete roster.");
        }

        return new ConfigMigrationResult(version, Current, ConfigMigrationAction.AdoptedFullRoster,
            RosterProfiles.Full, disabled,
            "Legacy configuration matched the untouched defaults exactly; migrated roster profile "
            + "'core' -> 'full'."
            + (disabled.Count > 0
                ? $" Kill switches preserved: {string.Join(", ", disabled)}."
                : ""));
    }

    private static string Normalise(string profile) => (profile ?? "").Trim().ToLowerInvariant();

    private static string? ReadString(IReadOnlyDictionary<string, JsonElement> raw, string key) =>
        raw.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBool(IReadOnlyDictionary<string, JsonElement> raw, string key) =>
        raw.TryGetValue(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int? ReadInt(IReadOnlyDictionary<string, JsonElement> raw, string key) =>
        raw.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static IReadOnlyList<string> ReadStringArray(IReadOnlyDictionary<string, JsonElement> raw, string key)
    {
        if (!raw.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var items = new List<string>();
        foreach (var element in value.EnumerateArray())
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text)
                items.Add(text);
        return items;
    }
}
