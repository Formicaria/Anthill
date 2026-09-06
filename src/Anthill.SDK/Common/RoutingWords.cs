using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Anthill.SDK.Common;

/// <summary>
/// KEYWORD ROUTING MATCHES WORDS, NOT LETTERS. v0.3.8.126.
///
/// WHAT HAPPENED. A mission whose text contained the phrase "requiring the user" was routed to
/// `coder.ui_coder`, because `AntRegistry.ResolveWorker` decided the UI lane with
/// <c>text.Contains("ui")</c> — and "ui" sits inside "req·ui·ring". It also sits inside "b·ui·ld",
/// "g·ui·de", "q·ui·te", "s·ui·te" and "fl·ui·d", every one of which appears in ordinary mission
/// prose. The wrong choice then became unappealable: `Pick(true, …)` marks the decision
/// <c>WorkerDecisionBasis.Keyword</c>, and `PlanningService` treats a keyword basis as final, so no
/// pheromone evidence could route the task back.
///
/// THIS REPOSITORY HAD ALREADY FIXED IT ONCE. `UiChangeGate` hit the identical defect at v0.3.8.96
/// — "a docs-file change was refused for having no frontend map" — and its comment records the
/// diagnosis in full. The fix there was <c>\bui\b</c>. It was never carried to the worker resolver,
/// which is the code that actually picks who does the work. One rule, two implementations, one of
/// them fixed: defect class #5, and the reason this lives in a file of its own that both call.
///
/// LIVES IN THE SDK, beside `TextUtil`, because the rule has callers on both sides of the module
/// boundary: `AntRegistry` and `Planner` in the core pick a worker and a lane with it, and
/// `TextUtil.ShouldUseWebSearch` — SDK code the core cannot reach into — decides the web lane from
/// the same kind of keyword list and had the same kind of collision ("search" inside "re·search").
/// A copy on each side would be the very shape this file exists to remove.
///
/// TWO MATCH MODES, because "match the whole word" is not always right either:
///
///   · <see cref="Word"/> — <c>\bkeyword\b</c>. For short keywords that hide inside common English.
///     "ui" is the whole reason this file exists.
///   · <see cref="Prefix"/> — <c>\bkeyword</c>. For stems whose inflections are genuinely the same
///     signal: "doc" must still catch "docs" and "document", "read" must catch "readme" and
///     "reading". Anchored at the START only, which is what distinguishes "read" from "al·read·y",
///     "th·read" and "sp·read", and "data" from "meta·data".
///
/// A bare <c>Contains</c> is neither, and is what every branch of the resolver used.
/// </summary>
public static class RoutingWords
{
    // Compiled once per pattern and shared. Routing runs per task, and building a Regex per
    // keyword per task would make planning quadratic in the size of the keyword lists.
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    private static Regex Compiled(string pattern) =>
        Cache.GetOrAdd(pattern, p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    /// <summary>The keyword as a whole word: <c>\bkeyword\b</c>.</summary>
    public static bool Word(string? text, string keyword) =>
        !string.IsNullOrEmpty(text)
        && Compiled($@"\b{Regex.Escape(keyword)}\b").IsMatch(text);

    /// <summary>Any of the keywords as a whole word.</summary>
    public static bool AnyWord(string? text, params string[] keywords) =>
        keywords.Any(k => Word(text, k));

    /// <summary>
    /// The keyword at the START of a word: <c>\bkeyword</c>. Catches its inflections ("doc" →
    /// "docs", "document") without catching it buried inside an unrelated one ("read" ≠ "already").
    /// </summary>
    public static bool Prefix(string? text, string keyword) =>
        !string.IsNullOrEmpty(text)
        && Compiled($@"\b{Regex.Escape(keyword)}").IsMatch(text);

    /// <summary>Any of the keywords at the start of a word.</summary>
    public static bool AnyPrefix(string? text, params string[] keywords) =>
        keywords.Any(k => Prefix(text, k));

    /// <summary>
    /// A literal phrase, whole-word at both ends. For signals that only mean something together —
    /// "mission history" is a request about past missions; "mission" alone is in the scaffolding of
    /// every composed goal this colony builds.
    /// </summary>
    public static bool Phrase(string? text, string phrase) => Word(text, phrase);
}
