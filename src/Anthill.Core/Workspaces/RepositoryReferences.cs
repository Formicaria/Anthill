using System.Text;
using System.Text.RegularExpressions;

namespace Anthill.Core.Workspaces;

/// <summary>One mention of a name, with enough provenance to go and look at it.</summary>
public sealed record SymbolReference(string Path, int Line, string Text);

/// <summary>
/// What a reference lookup found, and how much it can be trusted.
///
/// The second half is the part that matters and the part a naive design omits. "What calls this" is
/// the question an agent would most like to trust completely — it is the input to "what would my
/// change break" — and name matching cannot answer it reliably. A name declared in one place gives a
/// usable answer; a name declared in twelve, or a name like <c>Run</c> or <c>Name</c>, gives a list
/// that LOOKS like an answer and is not.
///
/// So ambiguity travels WITH the result rather than being left for the caller to infer. A list that
/// cannot be attributed is reported as exactly that.
/// </summary>
public sealed record ReferenceReport(
    string Name,
    IReadOnlyList<SymbolReference> References,
    int DeclarationCount,
    bool Truncated,
    int FilesScanned)
{
    /// <summary>
    /// Below this length a name is too common for matching to mean anything. <c>id</c>, <c>run</c>
    /// and <c>get</c> appear in every file of every repository for unrelated reasons.
    /// </summary>
    public const int MinTrustworthyNameLength = 4;

    /// <summary>
    /// Whether these references can be attributed to ONE declaration.
    ///
    /// Requires exactly one declaration and a name long enough to be distinctive. Anything else and
    /// the references are mentions of a NAME, not uses of a THING — which is a different and much
    /// weaker claim, and the difference decides whether an agent should act on the list.
    /// </summary>
    public bool Attributable =>
        DeclarationCount == 1 && Name.Length >= MinTrustworthyNameLength;

    /// <summary>Why the result cannot be attributed, in terms an agent can act on. Empty when it can.</summary>
    public string Caveat =>
        DeclarationCount == 0
            ? $"No declaration of '{Name}' was found by the index, so these are mentions of a name "
            + "whose definition is unknown — possibly external, possibly declared in a shape the "
            + "patterns do not cover."
        : DeclarationCount > 1
            ? $"'{Name}' is declared in {DeclarationCount} places, so these mentions CANNOT be "
            + "attributed to any one of them. Treat this as a list of places to read, not as callers."
        : Name.Length < MinTrustworthyNameLength
            ? $"'{Name}' is short enough that most of these matches are probably unrelated."
            : "";
}

/// <summary>
/// v3.6.0 — "what mentions this name", scanned from the workspace on demand.
///
/// Scanned at QUERY TIME rather than stored in the index, and the reason is worth stating: a stored
/// reference graph is a second thing that goes stale, and it goes stale exactly when a mission is
/// editing code — which is when the question gets asked. Recomputing over a file list the index
/// already has is cheap enough and cannot be wrong about a file the mission just changed.
///
/// It answers a NARROWER question than its name suggests, deliberately. This finds mentions of a
/// name; it does not resolve types, imports, overloads or scope. The gap between "mentions X" and
/// "calls the X you mean" is precisely what <see cref="ReferenceReport.Attributable"/> exists to
/// make visible instead of papering over.
/// </summary>
public static class RepositoryReferences
{
    public const int MaxReferences = 300;
    public const int MaxFilesScanned = 5_000;
    public const int MaxLineChars = 300;

    /// <summary>
    /// Find mentions of <paramref name="name"/> across the indexed files.
    ///
    /// Declaration lines are EXCLUDED: an agent asking "what uses this" does not want the definition
    /// back as its own first caller, and including it inflates a count the agent may be using to
    /// judge how risky a change is.
    /// </summary>
    public static ReferenceReport Find(RepositoryIndex index, string root, string? name)
    {
        var wanted = (name ?? "").Trim();
        if (index is null || wanted.Length == 0)
            return new ReferenceReport(wanted, Array.Empty<SymbolReference>(), 0, false, 0);

        var declarations = index.FindSymbol(wanted, exact: true);
        var declaredAt = declarations
            .Select(d => (d.Path, d.Symbol.Line))
            .ToHashSet();

        Regex matcher;
        try
        {
            // Whole-word only. Without the boundaries, searching for "User" matches "UserService",
            // "Users" and "ParseUserInput", and the result stops meaning anything at all.
            matcher = new Regex($@"\b{Regex.Escape(wanted)}\b",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException)
        {
            return new ReferenceReport(wanted, Array.Empty<SymbolReference>(), declarations.Count, false, 0);
        }

        var references = new List<SymbolReference>();
        var scanned = 0;
        var truncated = false;

        foreach (var file in index.Files)
        {
            if (scanned >= MaxFilesScanned || references.Count >= MaxReferences) { truncated = true; break; }
            // Files with no symbols are markdown, JSON, lockfiles: a name appearing there is not a
            // use of it, and including them buries the real answers.
            if (file.Symbols.Count == 0 && file.Language is "other" or "markdown" or "json") continue;
            scanned++;

            string[] lines;
            try
            {
                var full = Path.Combine(root, file.Path);
                if (!File.Exists(full)) continue;      // indexed then deleted; not an error
                lines = File.ReadAllLines(full);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            for (var i = 0; i < lines.Length && references.Count < MaxReferences; i++)
            {
                if (declaredAt.Contains((file.Path, i + 1))) continue;

                bool hit;
                try { hit = matcher.IsMatch(lines[i]); }
                catch (RegexMatchTimeoutException) { break; }
                if (!hit) continue;

                var text = lines[i].Trim();
                if (text.Length > MaxLineChars) text = text[..MaxLineChars] + "…";
                references.Add(new SymbolReference(file.Path, i + 1, text));
            }
        }

        return new ReferenceReport(wanted, references, declarations.Count, truncated, scanned);
    }
}
