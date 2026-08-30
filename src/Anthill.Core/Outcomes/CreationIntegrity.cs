using Anthill.SDK.Artifacts;

namespace Anthill.Core.Outcomes;

/// <summary>
/// THE DETERMINISTIC GATE FOR CREATED DELIVERABLES. v0.3.8.100, PLAN.md §2b.
///
/// What it decides, from records alone: when a mission's plan typed work as creation, a
/// `created_artifact` record EXISTS and its content is bytes rather than a description; every
/// requirement the record states either traces into that content or stands visibly unmet; every
/// input the record claims resolves to an artifact this mission actually holds; and a data
/// analysis carries the two things that make it an analysis — identified inputs and a
/// transformation account.
///
/// KEYED ON THE PLAN'S OWN TYPING rather than on reading the goal's prose: the planner proposing
/// `document_creation` is a model proposing, which is allowed; this gate enforcing what such a
/// task must leave behind is deterministic, which is the division ADR-008 requires. A mission with
/// no creation-typed task and no creation record is untouched — no existing answer changes shape.
///
/// WHAT IT DOES NOT DECIDE, deliberately: whether the content is GOOD, or whether a traced section
/// truly satisfies its requirement. `.99`'s line holds — traceability, not support. And the same
/// null-store asymmetry: an UNREADABLE store means "cannot check", not "guilty"; converting a
/// storage outage into a failed deliverable would be this gate inventing evidence. The layers that
/// catch contradiction catch that case.
/// </summary>
public static class CreationIntegrity
{
    /// <summary>
    /// The task types whose work is a created deliverable, spelled once — the planner offers them,
    /// the builder answers them in the deliverable format, and this gate enforces the record they
    /// owe. A vocabulary spelled twice eventually differs, and a type the planner may propose but
    /// the gate does not watch is a lane around the gate.
    /// </summary>
    public static readonly IReadOnlySet<string> CreationTaskTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "document_creation",
            "data_analysis",
        };

    /// <param name="Satisfied">Whether every check passed.</param>
    /// <param name="Failures">Each failed check, named — the record, the requirement, the input.</param>
    /// <param name="Created">How many creation records the mission holds.</param>
    /// <param name="Unmet">Requirements the deliverables admit are unaddressed. Counted, not
    /// fatal: an admitted gap is the honest record, and punishing the admission teaches deletion.</param>
    public sealed record Result(bool Satisfied, IReadOnlyList<string> Failures, int Created, int Unmet)
    {
        public string Explanation => Satisfied
            ? $"Creation integrity: {Created} deliverable record(s), {Unmet} admitted-unmet requirement(s)."
            : $"Creation integrity: {string.Join(" ", Failures)}";
    }

    /// <summary>
    /// Whether this mission is one the gate has anything to say about: its plan typed work as
    /// creation, or a creation record exists (a record present without the typing is still a
    /// record owing its checks). Null artifacts mean the store was unreadable — the gate cannot
    /// distinguish "produced nothing" from "cannot read", so it does not apply.
    /// </summary>
    public static bool Applies(IEnumerable<string?> taskTypes, IReadOnlyList<Artifact>? artifacts) =>
        artifacts is not null
        && (taskTypes.Any(t => CreationTaskTypes.Contains(t ?? ""))
            || Records(artifacts).Count > 0);

    /// <param name="taskTypes">The task types of the mission's plan, creation-typed or not.</param>
    /// <param name="artifacts">The mission's artifacts; null (an unreadable store) evaluates as
    /// empty here, but `Applies` has already declined that case — mirrored from `.99`'s gate so
    /// the two share a calling contract.</param>
    public static Result Evaluate(IEnumerable<string?> taskTypes, IReadOnlyList<Artifact>? artifacts)
    {
        artifacts ??= Array.Empty<Artifact>();
        var failures = new List<string>();
        var records = Records(artifacts);
        var unmet = 0;

        if (records.Count == 0)
        {
            // The plan said this mission creates; the store holds no creation record. Whatever the
            // prose answer says it made, nothing checkable was made — the described-not-produced
            // shape, caught by absence.
            failures.Add("the mission's plan typed work as creation and no created_artifact "
                       + "record was produced — the answer describes a deliverable that does not "
                       + "exist as a record.");
            return new Result(false, failures, 0, 0);
        }

        // Everything this mission holds, for input resolution. The creation records themselves are
        // excluded: a deliverable citing itself as its own input would resolve, and mean nothing.
        var held = artifacts
            .Where(a => !string.Equals(a.Schema, ArtifactSchemas.CreatedArtifact, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(a => a.Id, a => a, StringComparer.Ordinal);

        foreach (var record in records)
        {
            unmet += record.UnmetCount;

            // ---- 1. THE CREATED THING EXISTS ----------------------------------------------------
            if (string.IsNullOrWhiteSpace(record.Content))
            {
                failures.Add($"deliverable '{record.Title}' has empty content — a record of a "
                           + "creation with nothing created.");
                continue;   // the remaining checks would all be statements about nothing
            }

            // ---- 2. EVERY TRACED REQUIREMENT RESOLVES INTO THE CONTENT --------------------------
            //
            // The model proposed WHERE each requirement is addressed; this decides whether that
            // place exists in the bytes. A trace to a section the content does not contain is a
            // fabricated trace — the requirement equivalent of `.99`'s invented citation.
            foreach (var requirement in record.Requirements.Where(r => !r.Unmet))
            {
                if (string.IsNullOrWhiteSpace(requirement.Where)
                    || !record.Content.Contains(requirement.Where!, StringComparison.OrdinalIgnoreCase))
                    failures.Add($"requirement '{requirement.Text}' is traced to "
                               + $"'{requirement.Where}', which does not appear in the content.");
            }

            // ---- 3. EVERY CLAIMED INPUT IS HELD -------------------------------------------------
            //
            // Resolution was the deterministic layer's job at persist time; an input still
            // unresolved here referenced something this mission never held.
            foreach (var input in record.Inputs)
            {
                if (string.IsNullOrWhiteSpace(input.ArtifactId) || !held.ContainsKey(input.ArtifactId!))
                    failures.Add($"input '{input.Reference}' does not resolve to any record this "
                               + "mission holds — the deliverable claims a provenance it does not have.");
            }

            // ---- 4. AN ANALYSIS RECORDS INPUT IDENTITY AND TRANSFORMATION -----------------------
            if (string.Equals(record.Kind, CreatedArtifact.KindDataAnalysis, StringComparison.OrdinalIgnoreCase))
            {
                if (!record.Inputs.Any(i => !string.IsNullOrWhiteSpace(i.ArtifactId)))
                    failures.Add($"data analysis '{record.Title}' identifies no input — a "
                               + "conclusion about data it does not name having read.");
                if (record.Transformation.Count == 0)
                    failures.Add($"data analysis '{record.Title}' records no transformation — "
                               + "what was done to the inputs is unaccounted for.");
            }
        }

        return new Result(failures.Count == 0, failures, records.Count, unmet);
    }

    /// <summary>The mission's first creation record, for rendering — or null when there is none.</summary>
    public static CreatedArtifact? Created(IReadOnlyList<Artifact>? artifacts) =>
        artifacts is null ? null : Records(artifacts).FirstOrDefault();

    private static IReadOnlyList<CreatedArtifact> Records(IReadOnlyList<Artifact> artifacts) =>
        artifacts
            .Where(a => string.Equals(a.Schema, ArtifactSchemas.CreatedArtifact, StringComparison.OrdinalIgnoreCase))
            .Select(a => CreatedArtifact.FromJson(a.Payload))
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();
}
