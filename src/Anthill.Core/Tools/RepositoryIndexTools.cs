using System.Text;
using Anthill.Core.Domain;
using Anthill.Core.Workspaces;

namespace Anthill.Core.Tools;

/// <summary>
/// v3.6.0 — "what is in this repository, and where", answered from the index.
///
/// The exit gate this serves is worth quoting: "an agent asked a repository question CALLS A TOOL;
/// it does not receive a pre-stuffed context blob." So the index is never injected into a prompt —
/// it sits behind this, and the agent spends a turn to ask. That costs a round trip and buys the
/// thing that matters: the agent decides what it needs to see, and the context holds an answer
/// rather than a repository.
///
/// Distinct from <see cref="SearchWorkspaceTool"/>, which reads file CONTENT. This answers
/// structural questions — which languages, how many files, where does anything named like this live
/// — without opening a single file, because the index already knows.
///
/// Every result is bounded and carries its revision, so an excerpt an agent acts on can be traced.
/// </summary>
public sealed class RepositoryIndexTool : ITool
{
    public const int MaxListed = 200;

    private readonly Func<MissionWorkspace, RepositoryIndex> _index;

    /// <summary>
    /// The index is supplied by a factory rather than built here, so the caller owns caching. A tool
    /// that rebuilt the index per call would walk the repository once per question — which is
    /// precisely the cost the index exists to avoid.
    /// </summary>
    public RepositoryIndexTool(Func<MissionWorkspace, RepositoryIndex> index) => _index = index;

    public string Name => "repository_index";
    public string Description =>
        "Ask what is in the mission's repository: language breakdown, file counts, and file paths "
      + "matching a name fragment or language. Answers from a revision-keyed index without reading "
      + "file contents.";

    public string ParametersJson => """
        {"type":"object",
         "properties":{
           "name":{"type":"string","description":"Case-insensitive fragment of a file path to match"},
           "language":{"type":"string","description":"Restrict to one language, e.g. csharp, typescript"},
           "symbol":{"type":"string","description":"Find where a class, function or type is DECLARED"},
           "summary":{"type":"boolean","description":"Return only the language breakdown (default when no filter is given)"}},
         "required":[]}
        """;

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        var workspace = MissionWorkspaceScope.Current;
        if (workspace is null || !workspace.Usable)
            // Refuses rather than indexing the live checkout. An answer about a tree the mission is
            // forbidden to touch would be worse than no answer: it would be confidently irrelevant.
            return new ToolResult(Name, false, "",
                "There is no mission workspace in scope, so there is no repository to describe.",
                Contracts.FailureClass.UnsafeState);

        var index = _index(workspace);

        var report = new StringBuilder();
        report.Append("revision=").Append(index.Revision.Length > 0 ? index.Revision : "(unknown)").Append('\n');
        report.Append("files=").Append(index.Files.Count);
        // Truncation is stated. An agent told "3 files match" out of a truncated index would
        // conclude the other twelve do not exist.
        if (index.Truncated) report.Append(" (TRUNCATED at ").Append(RepositoryIndex.MaxFiles).Append(')');
        report.Append('\n');

        var name = args.GetValueOrDefault("name")?.ToString();
        var language = args.GetValueOrDefault("language")?.ToString();
        var symbol = args.GetValueOrDefault("symbol")?.ToString();

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var found = index.FindSymbol(symbol);

            report.Append("--- declarations matching '").Append(symbol).Append("' (")
                  .Append(found.Count).Append(") ---\n");
            foreach (var (path, declaration) in found.Take(MaxListed))
                report.Append(path).Append(':').Append(declaration.Line).Append(": ")
                      .Append(declaration.Kind).Append(' ').Append(declaration.Name).Append('\n');
            if (found.Count > MaxListed)
                report.Append("…(").Append(found.Count - MaxListed).Append(" more)\n");

            // The honesty line, and it is not decoration. These come from pattern matching, not a
            // compiler: an agent told "declared nowhere" would stop looking, and one told "these are
            // all the callers" would change code on the strength of a list that was never complete.
            // Saying so costs a sentence and prevents a class of confident wrong answer.
            report.Append("NOTE: found by pattern matching, not by a compiler. These are candidates "
                        + "to READ, not a complete or authoritative list — a declaration written in "
                        + "an unusual shape will be missing, and a mention in a comment may appear.\n");

            return new ToolResult(Name, true, report.ToString());
        }

        var summaryOnly = args.GetValueOrDefault("summary") is true or "true" or "True"
                          || (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(language));

        if (summaryOnly)
        {
            report.Append("symbols=").Append(index.SymbolCount).Append('\n');
            report.Append("--- languages ---\n");
            foreach (var (lang, count) in index.LanguageCounts)
                report.Append(lang).Append(": ").Append(count).Append('\n');
            return new ToolResult(Name, true, report.ToString());
        }

        var matches = index.Files
            .Where(f => string.IsNullOrWhiteSpace(language)
                     || string.Equals(f.Language, language, StringComparison.OrdinalIgnoreCase))
            .Where(f => string.IsNullOrWhiteSpace(name)
                     || f.Path.Contains(name!, StringComparison.OrdinalIgnoreCase))
            .ToList();

        report.Append("--- matches (").Append(matches.Count).Append(") ---\n");
        foreach (var file in matches.Take(MaxListed))
            report.Append(file.Path).Append("  [").Append(file.Language).Append(", ")
                  .Append(file.Lines).Append(" lines]\n");

        if (matches.Count > MaxListed)
            report.Append("…(").Append(matches.Count - MaxListed)
                  .Append(" more — narrow the filter)\n");

        return new ToolResult(Name, true, report.ToString());
    }
}
