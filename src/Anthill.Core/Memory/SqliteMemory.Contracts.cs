using Anthill.Core.Missions;

namespace Anthill.Core.Memory;

/// <summary>
/// THE MISSION CONTRACT, PERSISTED. v0.3.8.104.
///
/// A SEPARATE TABLE, NOT COLUMNS ON `missions`, and the reason is a defect this codebase has
/// already paid for once. `SaveMission` is an `INSERT OR REPLACE` over the whole row, so anything
/// written to a mission column by a different code path is erased the next time the mission saves.
/// The evaluation columns hit exactly that and have to be written by a later `UPDATE`, with a
/// comment in `Queen.RunMission` explaining the ordering that keeps them alive. A contract that
/// could be silently erased mid-mission by an unrelated save would be a contract in name only, so
/// it goes somewhere `SaveMission` does not reach.
///
/// WRITE-ONCE IS ENFORCED HERE, not asked for. `INSERT OR IGNORE` means a second write for the same
/// mission is a no-op rather than an overwrite: a resumed or replayed mission cannot acquire a new
/// contract by being run again, which is the property the whole release depends on.
/// </summary>
public sealed partial class SqliteMemory
{
    /// <summary>
    /// Record the contract for a mission. Silently keeps the FIRST contract if one already exists —
    /// that is the write-once rule, and a caller that wanted to change a mission's contract is
    /// asking for something this type deliberately cannot do.
    /// </summary>
    public void SaveMissionContract(string missionId, MissionContract contract)
    {
        if (string.IsNullOrWhiteSpace(missionId) || contract is null) return;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT OR IGNORE INTO mission_contracts
                      (mission_id, contract_version, intake_version, mission_class, contract_json, recorded_at)
                  VALUES (@id, @cv, @iv, @class, @json, @at)",
                ("@id", missionId),
                ("@cv", contract.ContractVersion),
                ("@iv", contract.IntakeVersion),
                // Denormalised so a reader can ask "which missions were audits" without parsing
                // every stored document. The json stays authoritative; this column is an index.
                ("@class", contract.Specification.MissionClass),
                ("@json", contract.ToJson()),
                ("@at", AnthillTime.NowUtc().ToIso()));
        }
        InvalidateCache();
    }

    /// <summary>
    /// The recorded contract, or null when this mission predates contracts.
    ///
    /// Null is a real answer and callers must treat it as one: `MissionContracts.LoadOrCreate`
    /// resolves a legacy contract and marks it, so nothing downstream has to decide what an absent
    /// contract means. Returning a freshly-resolved contract from HERE would hide the distinction
    /// at the only layer that can still see it.
    /// </summary>
    public MissionContract? LoadMissionContract(string? missionId)
    {
        if (string.IsNullOrWhiteSpace(missionId)) return null;

        try
        {
            var json = Scalar(
                "SELECT contract_json FROM mission_contracts WHERE mission_id = @id",
                ("@id", missionId!))?.ToString();
            return MissionContract.FromJson(json);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // A store that cannot be read is not a mission without a contract. The caller resolves
            // a legacy one and marks it as resolved-at-read, which is true either way.
            return null;
        }
    }
}
