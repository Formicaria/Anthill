using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Anthill.SDK.Artifacts;

/// <summary>One assertion in an answer, and the source it rests on — or the fact that it has none.</summary>
/// <param name="Text">The claim as the builder wrote it.</param>
/// <param name="SourceUrl">The retrieved source it cites, or null for an unsourced claim.</param>
public sealed record SourcedClaim(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("source_url")] string? SourceUrl)
{
    [JsonPropertyName("sourced")]
    public bool Sourced => !string.IsNullOrWhiteSpace(SourceUrl);
}

/// <summary>
/// AN ANSWER, CLAIM BY CLAIM, WITH WHAT EACH RESTS ON. v0.3.8.99.
///
/// WHY THE BUILDER IS TYPED NOW, HAVING DELIBERATELY NOT BEEN. <see cref="ResearchBrief"/> declined
/// to give the builder a schema and was right to: its prompt asked for "a practical final response"
/// in 200-400 words with no sections at all, and typing prose that has no structure is the
/// relabelling ADR-004 rejects. That doc also said what would have to change first — "structuring it
/// honestly means first changing what the builder is asked to produce" — and this is that change.
/// The builder is now ASKED for claims and their sources when the mission retrieved any, so the
/// structure is produced rather than imputed.
///
/// WHY IT IS CITED BY URL. A model can only cite what it was SHOWN, and what it is shown is the
/// source material: title, url, snippet. Asking it to reproduce a database id would be asking it to
/// know something it has no honest access to — and an id it invented would be indistinguishable from
/// one it remembered, which is precisely the failure this type exists to make detectable. The url is
/// the identity the world already has.
///
/// WHAT THIS RECORD DOES NOT CLAIM: that a source SUPPORTS the claim attached to it. That is a
/// semantic judgment; a model asserting it is the evidence v2.19.0 stopped accepting. What is
/// checkable, and what <c>CitationIntegrity</c> checks, is whether the cited thing was ever
/// retrieved — traceability, not support.
///
/// AN UNSOURCED CLAIM IS A FIRST-CLASS ENTRY, not an omission. A builder that dropped what it could
/// not attribute would produce an answer that looks fully sourced because the unsupported parts were
/// deleted — the same "two channels and the prose one wins" defect arriving as a silence instead of
/// an assertion. Keeping it, marked, is what lets an operator see the shape of what is known.
/// </summary>
public sealed record SourcedAnswer
{
    [JsonPropertyName("claims")]
    public IReadOnlyList<SourcedClaim> Claims { get; init; } = Array.Empty<SourcedClaim>();

    /// <summary>Every distinct source this answer cites. The set `CitationIntegrity` resolves.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> CitedUrls =>
        Claims.Where(c => c.Sourced)
            .Select(c => c.SourceUrl!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    [JsonIgnore]
    public int UnsourcedCount => Claims.Count(c => !c.Sourced);

    /// <summary>The format the builder's prompt asks for, stated here so the two cannot drift.</summary>
    public const string ClaimPrefix = "CLAIM:";
    public const string UnsourcedMarker = "[UNSOURCED]";

    /// <summary>
    /// Parse an answer written in the claim format, or null when the response did not follow it.
    ///
    /// NULL IS A RESULT, exactly as it is for <see cref="ResearchBrief"/>: a caller receiving null
    /// has learned that the model returned ordinary prose, and must record that rather than emit an
    /// artifact of empty claims — which would be indistinguishable from an answer that genuinely
    /// asserted nothing.
    /// </summary>
    public static SourcedAnswer? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // The SOURCE marker is matched case-insensitively and tolerates a missing space, because a
        // model that writes `[source:https://…]` has followed the format. Matching the SHAPE rather
        // than the exact spelling keeps this a format check and not a formatting check.
        var claims = new List<SourcedClaim>();
        foreach (Match line in Regex.Matches(text!,
                     $@"^\s*\**{Regex.Escape(ClaimPrefix)}\**\s*(?<body>.+)$",
                     RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            var body = line.Groups["body"].Value.Trim();
            var source = Regex.Match(body, @"\[\s*source\s*:\s*(?<url>[^\]\s]+)\s*\]", RegexOptions.IgnoreCase);

            var claimText = Regex.Replace(body, @"\[\s*(source\s*:[^\]]*|unsourced)\s*\]", "", RegexOptions.IgnoreCase).Trim();
            if (claimText.Length == 0) continue;

            claims.Add(new SourcedClaim(claimText, source.Success ? source.Groups["url"].Value : null));
        }

        return claims.Count == 0 ? null : new SourcedAnswer { Claims = claims };
    }

    /// <summary>
    /// The answer an operator reads, rendered from the claims — with the unsourced ones MARKED.
    ///
    /// Rendered rather than passed through, so the marking cannot depend on the model having
    /// remembered to write it. A claim the model left unattributed is labelled here whatever it
    /// said, which is the difference between the record and a promise about the record.
    /// </summary>
    public string Render() => string.Join("\n\n", Claims.Select(c => c.Sourced
        ? $"{c.Text}\n    source: {c.SourceUrl}"
        : $"{c.Text}\n    [UNSOURCED — this claim is not attributed to anything the mission retrieved]"));

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static SourcedAnswer? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<SourcedAnswer>(json!, Options); }
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
