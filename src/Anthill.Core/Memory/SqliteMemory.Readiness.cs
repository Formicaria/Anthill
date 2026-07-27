using Anthill.Core.Common;
using Anthill.Core.Readiness;

namespace Anthill.Core.Memory;

/// <summary>
/// v2.25.0 Phase F — operator attestations for the V3 readiness gate.
///
/// Several thresholds are judgments ANTHILL cannot make about itself ("I ran the recovery suites
/// and watched them pass", "I pulled the kill switch and execution halted"). Those live here as
/// explicit operator records — the same stance as shadow outcomes: the machine's claim means
/// nothing until a human has said what was true.
///
/// An attestation can also record NOT satisfied — an operator who tested the kill switch and found
/// it wanting needs that on the record more than one who found it working.
/// </summary>
public sealed partial class SqliteMemory
{
    /// <summary>Record an operator attestation. Unknown threshold ids are refused (returns false)
    /// so a typo cannot create a phantom threshold.</summary>
    public bool SaveReadinessAttestation(string thresholdId, bool satisfied, string note, string attestedBy)
    {
        if (!V3Readiness.AttestableIds.Contains(thresholdId)) return false;

        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO readiness_attestations (threshold_id, satisfied, note, attested_by, attested_at)
                  VALUES (@id, @sat, @note, @by, @at)
                  ON CONFLICT(threshold_id) DO UPDATE SET
                      satisfied=@sat, note=@note, attested_by=@by, attested_at=@at",
                ("@id", thresholdId), ("@sat", satisfied ? 1 : 0),
                ("@note", note ?? ""), ("@by", attestedBy ?? ""),
                ("@at", AnthillTime.NowUtc().ToIso()));
        }
        LogEvent(Configuration.AnthillRuntime.SystemApiMissionId, "readiness_attested",
            $"Operator '{attestedBy}' attested '{thresholdId}': {(satisfied ? "satisfied" : "NOT satisfied")}."
            + (string.IsNullOrWhiteSpace(note) ? "" : $" Note: {note}"),
            antName: "operator",
            metadata: new() { ["threshold_id"] = thresholdId, ["satisfied"] = satisfied });
        InvalidateCache();
        return true;
    }

    /// <summary>All recorded attestations, keyed by threshold id. Absence means "not attested",
    /// which the evaluation treats as not satisfied.</summary>
    public Dictionary<string, (bool Satisfied, string Note)> LoadReadinessAttestations()
    {
        var result = new Dictionary<string, (bool, string)>();
        foreach (var row in Query(
            "SELECT threshold_id, satisfied, note, attested_by, attested_at FROM readiness_attestations"))
        {
            var id = row.GetValueOrDefault("threshold_id")?.ToString() ?? "";
            if (id.Length == 0) continue;
            var by = row.GetValueOrDefault("attested_by")?.ToString() ?? "";
            var at = row.GetValueOrDefault("attested_at")?.ToString() ?? "";
            var note = row.GetValueOrDefault("note")?.ToString() ?? "";
            result[id] = (AsLong(row.GetValueOrDefault("satisfied")) != 0,
                $"Attested by {by} at {at}." + (note.Length > 0 ? $" \"{note}\"" : ""));
        }
        return result;
    }
}
