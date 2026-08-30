using Anthill.Core.Missions;
using Anthill.SDK.Artifacts;

namespace Anthill.Core.Outcomes;

/// <summary>
/// THE DETERMINISTIC GATE FOR DIAGNOSES. v0.3.8.101, PLAN.md §2b — the exit line verbatim: a
/// symptom reaches a diagnosis supported by command receipts and exit statuses.
///
/// What it decides, from records alone: a troubleshooting mission EXECUTED checks (the
/// `command_check` rows `ToolEvidence` writes at the dispatch chokepoint — the honest witness,
/// never a task's self-report); a diagnosis record EXISTS; and that diagnosis rests on receipts BY
/// NAME — every `supporting_receipt:` line resolves to a check this mission actually ran. A cited
/// receipt that resolves to nothing is the class's own fabrication, exactly parallel to `.99`'s
/// invented url and `.100`'s claimed input: a provenance line that reads identically to a real one
/// and is checkable by nobody.
///
/// KEYED ON THE SPECIFICATION, like the audit gate and unlike `.100`'s plan-typing key, because
/// this class HAS an intake classification: `MissionIntake` derives `troubleshooting` from the
/// dimensions deterministically. That also decides the null-store rule: specification-keyed gates
/// fail CLOSED (the S3 doctrine — an outage is never permission), where the record-keyed gates of
/// `.99`/`.100` decline to apply. Both asymmetries are deliberate and each matches its key: a gate
/// that cannot know whether it applies must not guess guilt; a gate that KNOWS this mission owes
/// receipts must not accept an unreadable store as their substitute.
///
/// WHAT IT DOES NOT DECIDE: whether the root cause is CORRECT. That is a semantic judgment; what
/// is checkable is that the checks ran, how they exited, and that the diagnosis's claimed support
/// resolves — the same traceability-not-truth line every gate in this program holds.
/// </summary>
public static class DiagnosisIntegrity
{
    /// <param name="Satisfied">Whether every check passed.</param>
    /// <param name="Reasons">Each failed check, named — the missing receipts, the absent record,
    /// the citation that resolves to nothing.</param>
    public sealed record Result(bool Satisfied, IReadOnlyList<string> Reasons)
    {
        public string Explanation => Satisfied
            ? "diagnosis integrity: satisfied"
            : "diagnosis integrity NOT satisfied — " + string.Join("; ", Reasons);
    }

    private static readonly Result Ok = new(true, Array.Empty<string>());

    /// <summary>The marker a diagnosis cites its receipts with, spelled once — the medic stamps
    /// these lines and this gate resolves them, and two spellings would eventually differ.</summary>
    public const string ReceiptMarker = "supporting_receipt:";

    public static bool Applies(MissionSpecification? specification) =>
        specification is { MissionClass: MissionSpecification.TroubleshootingClass } && specification.IsActionable;

    /// <summary>
    /// Grade the diagnosis. Every input is a record the mission left behind.
    /// </summary>
    /// <param name="evidence">The mission's evidence rows, or null when the store could not be
    /// read — which fails CLOSED, per the class comment.</param>
    /// <param name="artifacts">The mission's artifacts (the diagnosis records live here), same rule.</param>
    public static Result Evaluate(MissionSpecification specification,
        IReadOnlyList<Evidence>? evidence,
        IReadOnlyList<Artifact>? artifacts)
    {
        if (!Applies(specification)) return Ok;

        var reasons = new List<string>();

        // ---- 1. CHECKS RAN, AND THEIR RECEIPTS ARE HELD -----------------------------------------
        //
        // Read from `RequiredEvidence` rather than hard-coded, the audit gate's own rule: the
        // requirement is stated once, where the class is defined.
        var receipts = new List<Evidence>();
        if (evidence is null)
            reasons.Add("the evidence store could not be read, so no check receipt can be shown");
        else
        {
            foreach (var kind in specification.RequiredEvidence)
            {
                var rows = evidence.Where(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase)).ToList();
                if (rows.Count == 0)
                    reasons.Add($"no '{kind}' receipt was recorded — the mission executed nothing, "
                              + "and a diagnosis without execution is an audit finding at best");
                receipts.AddRange(rows);
            }
        }

        // ---- 2. A DIAGNOSIS RECORD EXISTS -------------------------------------------------------
        var diagnoses = artifacts is null
            ? new List<Artifact>()
            : artifacts.Where(a => string.Equals(a.Schema, ArtifactSchemas.FailureDiagnosis, StringComparison.OrdinalIgnoreCase))
                .ToList();
        if (artifacts is null)
            reasons.Add("the artifact store could not be read, so no diagnosis can be shown");
        else if (diagnoses.Count == 0)
            reasons.Add("no diagnosis record exists — the symptom was reported and nothing "
                      + "diagnosed it (every executed check may simply have passed; 'could not "
                      + "reproduce' as a first-class answer is future work, recorded in §2c)");

        // ---- 3. EVERY CITED RECEIPT RESOLVES, AND AT LEAST ONE IS CITED -------------------------
        //
        // Resolution is by content: a cited id must appear in some receipt's detail, which carries
        // the check identity stamped from the dispatch's own arguments. The medic stamps these
        // lines deterministically, so in production an unresolvable citation means the record and
        // the store disagree — and whichever of the two is lying, the mission must not pass on it.
        foreach (var diagnosis in diagnoses)
        {
            var cited = CitedReceipts(diagnosis.Payload);
            if (cited.Count == 0)
            {
                reasons.Add($"diagnosis '{diagnosis.Id}' cites no receipt — a root cause resting "
                          + "on nothing that ran");
                continue;
            }
            foreach (var id in cited)
                if (!receipts.Any(r => r.Detail.Contains(id, StringComparison.OrdinalIgnoreCase)))
                    reasons.Add($"diagnosis '{diagnosis.Id}' cites receipt '{id}', which resolves "
                              + "to no check this mission ran");
        }

        return reasons.Count == 0 ? Ok : new Result(false, reasons);
    }

    /// <summary>The check ids a diagnosis declares, from its own `supporting_receipt:` lines.</summary>
    public static IReadOnlyList<string> CitedReceipts(string? payload) =>
        string.IsNullOrWhiteSpace(payload)
            ? Array.Empty<string>()
            : System.Text.RegularExpressions.Regex
                .Matches(payload!, $@"^\s*{ReceiptMarker}\s*(?<id>\S+)",
                    System.Text.RegularExpressions.RegexOptions.Multiline
                    | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Select(m => m.Groups["id"].Value.Trim())
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
}
