namespace Anthill.SDK.Knowledge;

/// <summary>One knowledge statement an answer may be held to having consulted.</summary>
public sealed record KnowledgeCitation(string Url, string Title);

/// <summary>
/// WHAT A KNOWLEDGE-GROUNDED CLAIM RESTS ON, AND HOW BOTH SIDES SPELL IT. v0.3.8.157.
///
/// THE GAP THIS CLOSES. Since v0.3.8.121 the colony could retrieve what the organization knows;
/// since v0.3.8.136 the scope resolved so an ant's call would not refuse; since v0.3.8.156 a plan
/// guarantees a step that retrieves. At the end of all of it the answer still rendered every
/// knowledge-backed claim as `[UNSOURCED]` — because `CitationIntegrity` resolves a citation
/// against `source_set` records and nothing ever wrote one for a knowledge item. The organization's
/// own documents were the one class of evidence the colony could read and could not cite.
///
/// A URL, BECAUSE THE CITATION VOCABULARY IS ALREADY ONE. `mission:&lt;id&gt;` did exactly this for
/// the colony's own history in v0.3.8.99, and its comment states the rule this follows: one
/// vocabulary for "what may be cited" is what lets ONE gate resolve all of them, and a second shape
/// would mean a second parser and eventually a second set of bugs.
///
/// THE PROJECT REF IS PART OF THE IDENTITY, not decoration. `ki_68c6…` means nothing without the
/// knowledge base it came from, two projects may both hold an item under ids the producer chose
/// independently, and a citation an operator cannot resolve back to a tenant is a citation that
/// cannot be audited. It is also the reason a knowledge url can never be mistaken for a page in the
/// world: the scheme says where to look.
///
/// ONE FILE HOLDS THE WRITER AND THE READER, which is <see cref="Artifacts.SourceSetPayload"/>'s
/// hard-won lesson. There, the producer serialised `"Url"` and both readers looked for `"url"`, both
/// found nothing, silently, and the mission reported an answer that cited nothing — while the unit
/// tests passed, because their fixtures were written the way the READERS expected. So the block is
/// rendered here and parsed here, and the test asserts the round trip against the producer's actual
/// output rather than against a fixture that agrees with the parser.
/// </summary>
public static class KnowledgeCitations
{
    /// <summary>The url scheme a knowledge statement is cited under.</summary>
    public const string Scheme = "knowledge:";

    /// <summary>
    /// The heading that opens the citable block in a rendered context. Model-facing text and a
    /// parse anchor at once — which is a coupling, and it is declared here rather than discovered:
    /// changing this line changes what `Read` can find, so the two move together or not at all.
    /// </summary>
    public const string Header = "CITABLE SOURCES";

    /// <summary>
    /// The citable identity of one statement in one knowledge base.
    ///
    /// An empty project ref still produces a url rather than throwing: a scope with no ref cannot
    /// retrieve at all, so this cannot be reached in production, and a formatter that throws inside
    /// a renderer would fail the mission it was describing.
    /// </summary>
    public static string UrlFor(string? projectRef, string? knowledgeId) =>
        $"{Scheme}{(projectRef ?? "").Trim()}/{(knowledgeId ?? "").Trim()}";

    /// <summary>True when this url names a knowledge statement rather than a page or a mission.</summary>
    public static bool IsKnowledge(string? url) =>
        url is not null && url.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The block appended to a rendered context. Empty for no citations — an empty heading would
    /// tell a model there is a list and then show it none, which reads as retrieval that failed.
    /// </summary>
    public static string RenderBlock(IEnumerable<KnowledgeCitation> citations)
    {
        var rows = (citations ?? Array.Empty<KnowledgeCitation>())
            .Where(c => !string.IsNullOrWhiteSpace(c.Url)).ToList();
        if (rows.Count == 0) return "";

        var text = new System.Text.StringBuilder();
        text.Append('\n').Append(Header).Append('\n');
        text.Append("Cite these urls for any claim that rests on the knowledge above. A claim you\n");
        text.Append("cannot attribute to one of them is unsourced — say so rather than dropping it.\n");
        foreach (var row in rows)
            text.Append("  ").Append(row.Url).Append(" - ").Append(Flatten(row.Title)).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// Every citation in a rendered context.
    ///
    /// PARSED BY THE SCHEME RATHER THAN BY THE LAYOUT, so an added heading line, a reflowed
    /// paragraph or a changed separator cannot silently empty the result: a line whose first token
    /// starts with <see cref="Scheme"/> is a citation, and everything after that token is its title.
    /// A layout-shaped parser is how a reader comes to find nothing and report it as "no sources".
    /// </summary>
    public static IReadOnlyList<KnowledgeCitation> Read(string? rendered)
    {
        var found = new List<KnowledgeCitation>();
        if (string.IsNullOrWhiteSpace(rendered)) return found;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in rendered!.Split('\n'))
        {
            var line = raw.Trim();
            if (!IsKnowledge(line)) continue;

            var space = line.IndexOf(' ');
            var url = (space < 0 ? line : line[..space]).Trim();
            if (url.Length <= Scheme.Length || !seen.Add(url)) continue;

            var title = space < 0 ? "" : line[(space + 1)..].TrimStart(' ', '-', '—').Trim();
            found.Add(new KnowledgeCitation(url, title));
        }
        return found;
    }

    private static string Flatten(string? title)
    {
        var flat = (title ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= 160 ? flat : flat[..160] + "...";
    }
}
