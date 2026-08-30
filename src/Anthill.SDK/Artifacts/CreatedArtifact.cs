using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Anthill.SDK.Artifacts;

/// <summary>
/// One stated requirement of a created deliverable, and where in the content it is addressed —
/// or the fact that it is not.
/// </summary>
/// <param name="Text">The requirement as the builder stated it.</param>
/// <param name="Where">A fragment that appears in the content where the requirement is addressed,
/// or null for an unmet requirement.</param>
/// <param name="Unmet">Whether the deliverable admits this requirement is not addressed.</param>
public sealed record RequirementTrace(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("where")] string? Where,
    [property: JsonPropertyName("unmet")] bool Unmet);

/// <summary>
/// One input a creation claims to rest on. The model writes the REFERENCE — a schema name or an
/// artifact id, whichever it was honestly shown — and the deterministic layer resolves it to a
/// concrete held artifact, stamping id, schema and content hash. A reference that resolves to
/// nothing keeps a null <paramref name="ArtifactId"/>, which is what the gate refuses by name.
/// </summary>
public sealed record CreatedInput(
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("artifact_id")] string? ArtifactId,
    [property: JsonPropertyName("schema")] string? Schema,
    [property: JsonPropertyName("content_hash")] string? ContentHash);

/// <summary>
/// A CREATED DELIVERABLE AS A RECORD — the thing itself, what it promised, and what it rests on.
/// v0.3.8.100.
///
/// THE FAILURE THIS TYPE MAKES DETECTABLE. A model asked to create something can instead DESCRIBE
/// having created it, and the two are indistinguishable in prose: "I have prepared an onboarding
/// guide covering setup" reads identically whether or not any guide exists. `.99` typed the
/// research answer so a citation could be checked against what was retrieved; this types the
/// creation answer so the deliverable can be checked against what was produced — the content is IN
/// the record (existence is bytes, not an assertion), each stated requirement carries a trace into
/// that content or an admission that it has none, and each claimed input carries the identity of a
/// record the mission actually holds.
///
/// WHY INPUTS ARE REFERENCED BY SCHEMA OR ID AND THEN RESOLVED DETERMINISTICALLY. The `.99` rule:
/// a model can only cite what it was SHOWN. It is shown its mission's artifacts as ids and typed
/// schema names, so either is an honest reference — but identity (the content hash) is stamped by
/// the deterministic layer at persist time, never written by the model, because a hash a model
/// wrote is a hash it could have invented.
///
/// WHAT THIS RECORD DOES NOT CLAIM: that the content is good, or that a traced section truly
/// SATISFIES its requirement. Those are semantic judgments; what is checkable, and what
/// `CreationIntegrity` checks, is that the content exists, the trace resolves into it, and the
/// inputs resolve to held records — traceability, not quality.
///
/// AN UNMET REQUIREMENT IS A FIRST-CLASS ENTRY, exactly as an unsourced claim is: a deliverable
/// that deletes what it did not do looks more complete than one that admits it, and only the
/// admission lets an operator see the shape of what was made.
/// </summary>
public sealed record CreatedArtifact(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("requirements")] IReadOnlyList<RequirementTrace> Requirements,
    [property: JsonPropertyName("inputs")] IReadOnlyList<CreatedInput> Inputs,
    [property: JsonPropertyName("transformation")] IReadOnlyList<string> Transformation,
    [property: JsonPropertyName("content")] string Content)
{
    public const string KindDocument = "document";
    public const string KindDataAnalysis = "data_analysis";

    /// <summary>The format the builder's prompt asks for, stated here so the two cannot drift.</summary>
    public const string DeliverableMarker = "DELIVERABLE:";
    public const string ContentMarker = "CONTENT:";
    public const string UnmetMarker = "[UNMET]";
    public const string NoInputs = "none";
    /// <summary>An input referenced by its typed name — `schema:source_set` — resolved to every
    /// held artifact of that schema.</summary>
    public const string SchemaRefPrefix = "schema:";

    [JsonIgnore]
    public int UnmetCount => Requirements.Count(r => r.Unmet);

    /// <summary>
    /// Parse a response written in the deliverable format, or null when the model returned
    /// ordinary prose. NULL IS A RESULT, as it is for <see cref="SourcedAnswer"/>: the caller must
    /// record "no deliverable record was produced" rather than emit an empty record that reads as
    /// a deliverable which promised nothing.
    /// </summary>
    public static CreatedArtifact? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var title = Line(text!, "DELIVERABLE");
        if (title is null) return null;

        // Everything after the CONTENT marker is the deliverable itself. No marker, no
        // deliverable: a record without content is the described-not-made shape this type exists
        // to catch, and refusing to parse it keeps that visible as "answered in prose".
        var contentMatch = Regex.Match(text!, @"^\s*\**CONTENT\**\s*:\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!contentMatch.Success) return null;
        var content = text![(contentMatch.Index + contentMatch.Length)..].Trim();

        var head = text[..contentMatch.Index];
        var kind = (Line(head, "KIND") ?? KindDocument).Trim().ToLowerInvariant();
        if (kind != KindDocument && kind != KindDataAnalysis) return null;

        var requirements = new List<RequirementTrace>();
        foreach (var body in Lines(head, "REQUIREMENT"))
        {
            var where = Regex.Match(body, @"\[\s*where\s*:\s*(?<frag>[^\]]+)\]", RegexOptions.IgnoreCase);
            var stated = Regex.Replace(body, @"\[\s*(where\s*:[^\]]*|unmet)\s*\]", "", RegexOptions.IgnoreCase).Trim();
            if (stated.Length == 0) continue;
            // A requirement with no trace IS untraced, whatever the model wrote after it: unmet by
            // shape, not by admission. Kept and marked, never dropped.
            requirements.Add(where.Success
                ? new RequirementTrace(stated, where.Groups["frag"].Value.Trim(), Unmet: false)
                : new RequirementTrace(stated, null, Unmet: true));
        }

        var inputs = Lines(head, "INPUT")
            .Select(r => r.Trim())
            .Where(r => r.Length > 0 && !string.Equals(r, NoInputs, StringComparison.OrdinalIgnoreCase))
            .Select(r => new CreatedInput(r, ArtifactId: null, Schema: null, ContentHash: null))
            .ToList();

        var transformation = Lines(head, "TRANSFORMATION")
            .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

        return new CreatedArtifact(kind, title.Trim(), requirements, inputs, transformation, content);
    }

    private static string? Line(string text, string marker)
    {
        var match = Regex.Match(text, $@"^\s*\**{marker}\**\s*:\s*(?<body>.+)$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["body"].Value.Trim() : null;
    }

    private static IEnumerable<string> Lines(string text, string marker) =>
        Regex.Matches(text, $@"^\s*\**{marker}\**\s*:\s*(?<body>.+)$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Select(m => m.Groups["body"].Value);

    /// <summary>
    /// The answer an operator reads: the deliverable itself, followed by its own account of what
    /// it did not do and what it rests on. Rendered from the record rather than passed through, so
    /// the admission cannot depend on the model having remembered to write it.
    /// </summary>
    public string Render()
    {
        var parts = new List<string> { Content };

        var unmet = Requirements.Where(r => r.Unmet).ToList();
        if (unmet.Count > 0)
            parts.Add("Unmet requirements — stated for this deliverable and not addressed in it:\n"
                + string.Join("\n", unmet.Select(r => $"    - {r.Text}")));

        if (Inputs.Count > 0)
            parts.Add("Inputs:\n" + string.Join("\n", Inputs.Select(i =>
                $"    - {i.Reference}" + (i.ArtifactId is null ? "  [UNRESOLVED]" : $"  ({i.ArtifactId})"))));

        if (Transformation.Count > 0)
            parts.Add("Transformation:\n" + string.Join("\n", Transformation.Select(t => $"    - {t}")));

        return string.Join("\n\n", parts);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static CreatedArtifact? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<CreatedArtifact>(json!, Options); }
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
