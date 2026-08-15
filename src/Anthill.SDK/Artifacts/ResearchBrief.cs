using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Anthill.SDK.Artifacts;

/// <summary>
/// The researcher's brief, as data. v0.3.8.57.
///
/// WHY THIS IS NOT RELABELLING. v3.8.21 declined to give the researcher a schema, and its reasoning
/// was right at the time: "the researcher, builder and verifier produce prose synthesis — giving that
/// a schema name would be relabelling, which is the 'two channels and the prose one wins' failure
/// ADR-004 exists to prevent." What that reasoning missed is that the researcher's PROMPT already
/// demands a shape, and has since the ant was written:
///
///     Return format:
///     - Relevant Memory:
///     - Useful Tool Context:
///     - Pheromone Guidance:
///     - Research Need:
///
/// So the structure was being produced and then thrown into a string. Extracting a shape the producer
/// was already asked for is not the same act as inventing one for prose that has none — which is
/// exactly why the BUILDER is not typed here. Its prompt asks for "a practical final response" in
/// 200-400 words, with no sections at all; typing that would be the relabelling ADR-004 rejects, and
/// structuring it honestly means first changing what the builder is asked to produce.
///
/// ALL FOUR SECTIONS OR NONE. A response missing a section did not follow the format, and accepting
/// a partial would mean the difference between "the researcher found no pheromone guidance" and "the
/// model ignored the format" disappears into the same empty string. An absent section is allowed to
/// be EMPTY — a researcher with nothing to say under a heading says so — but the heading has to be
/// there, because that is the difference between an answer and a guess about one.
/// </summary>
public sealed record ResearchBrief
{
    [JsonPropertyName("relevant_memory")] public string RelevantMemory { get; init; } = "";
    [JsonPropertyName("tool_context")] public string ToolContext { get; init; } = "";
    [JsonPropertyName("pheromone_guidance")] public string PheromoneGuidance { get; init; } = "";
    [JsonPropertyName("research_need")] public string ResearchNeed { get; init; } = "";

    /// <summary>
    /// The sections the researcher's prompt asks for, in prompt order. The prompt and this list are
    /// the same contract stated twice, which is a real risk — so a test asserts the prompt still
    /// contains every heading named here. Two components disagreeing about the format is how a
    /// parser quietly stops matching and starts reporting every response as unstructured.
    /// </summary>
    public static readonly IReadOnlyList<string> Headings =
        new[] { "Relevant Memory", "Useful Tool Context", "Pheromone Guidance", "Research Need" };

    /// <summary>
    /// Parse the brief, or return null when the response did not follow the format.
    ///
    /// NULL IS A RESULT, not a failure to handle later. A caller that receives null has learned
    /// something true — the model returned prose — and must record that rather than emit an artifact
    /// full of empty strings, which would be indistinguishable from a researcher that genuinely found
    /// nothing.
    /// </summary>
    public static ResearchBrief? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = new List<(string Heading, int Start, int End)>();

        foreach (var heading in Headings)
        {
            // Leading "- " optional and the colon required: the prompt writes "- Relevant Memory:",
            // and models routinely drop the bullet or bold the label. Matching the LABEL rather than
            // the exact line is what keeps this a format check and not a formatting check.
            var match = Regex.Match(text!, $@"^\s*[-*]?\s*\**{Regex.Escape(heading)}\**\s*:",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            matches.Add((heading, match.Index, match.Index + match.Length));
        }

        // Bodies run to the next heading BY POSITION, not by prompt order — a model that answers the
        // sections out of order still answered them, and reading to the next heading in prompt order
        // would splice one section's text onto another's.
        var ordered = matches.OrderBy(m => m.Start).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var bodyStart = ordered[i].End;
            var bodyEnd = i + 1 < ordered.Count ? ordered[i + 1].Start : text!.Length;
            sections[ordered[i].Heading] = text![bodyStart..bodyEnd].Trim();
        }

        return new ResearchBrief
        {
            RelevantMemory = sections["Relevant Memory"],
            ToolContext = sections["Useful Tool Context"],
            PheromoneGuidance = sections["Pheromone Guidance"],
            ResearchNeed = sections["Research Need"],
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ResearchBrief? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ResearchBrief>(json!, Options); }
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
