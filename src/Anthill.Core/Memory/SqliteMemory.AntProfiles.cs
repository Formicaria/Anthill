using Anthill.Core.Common;

namespace Anthill.Core.Memory;

/// <summary>
/// v0.3.8.50 (field report §20) — the operator's face for an ant: a display name and a color,
/// persisted as overrides keyed by the registry's role id. The registry stays authoritative for
/// WHAT an ant is; this table only records what the operator calls it and how it is drawn.
/// </summary>
public sealed record AntProfile(string AntId, string DisplayName, string Color)
{
    public string UpdatedBy { get; init; } = "";
    public DateTime UpdatedAt { get; init; } = AnthillTime.NowUtc();
}

public sealed partial class SqliteMemory
{
    public void SaveAntProfile(AntProfile profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.AntId)) return;
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO ant_profiles (ant_id, display_name, color, updated_by, updated_at)
                  VALUES (@id, @name, @color, @by, @at)
                  ON CONFLICT(ant_id) DO UPDATE SET
                    display_name=@name, color=@color, updated_by=@by, updated_at=@at",
                ("@id", profile.AntId.Trim().ToLowerInvariant()),
                ("@name", profile.DisplayName ?? ""), ("@color", profile.Color ?? ""),
                ("@by", profile.UpdatedBy), ("@at", profile.UpdatedAt.ToIso()));
        }
    }

    public void DeleteAntProfile(string antId)
    {
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, "DELETE FROM ant_profiles WHERE ant_id=@id",
                ("@id", (antId ?? "").Trim().ToLowerInvariant()));
        }
    }

    public IReadOnlyList<AntProfile> LoadAntProfiles() =>
        Query("SELECT ant_id, display_name, color, updated_by, updated_at FROM ant_profiles")
        .Select(row => new AntProfile(
            row.GetValueOrDefault("ant_id")?.ToString() ?? "",
            row.GetValueOrDefault("display_name")?.ToString() ?? "",
            row.GetValueOrDefault("color")?.ToString() ?? "")
        {
            UpdatedBy = row.GetValueOrDefault("updated_by")?.ToString() ?? "",
            UpdatedAt = AnthillTime.ParseIsoOrNow(row.GetValueOrDefault("updated_at")?.ToString()),
        }).ToList();
}
