using System.Reflection;
using System.Text.RegularExpressions;
using Anthill.SDK.Events;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The event vocabulary describes the events the runtime actually emits. v0.3.8.86.
///
/// WHY THIS EXISTS. `EventTypes` opens by saying its contents "were READ, out of the working tree,
/// from the LogEvent call sites", that "a subscriber written against this file is written against
/// reality rather than against an intention", and — one line further down — "when adding an event:
/// add the constant here in the same change as the publisher, never after."
///
/// That instruction was in place from the file's first release and was followed for roughly half the
/// events. At v0.3.8.85 the runtime emitted **134** distinct event names through `LogEvent` and the
/// file declared **69**. The missing sixty-seven were not obscure: `archivist_ran`,
/// `archivist_skipped`, every `autonomy_autoapply_*` outcome, every `patch_verify_*` step, every
/// `policy_review_*` and `verification_*` decision — the operator-facing half, where a filter that
/// matches nothing is indistinguishable from a quiet colony.
///
/// AND TWO CONSTANTS WERE EMITTED BY NOBODY, which is the sharper half of the finding.
/// `AutonomyAutoApplyRolledBack` and `AutonomyAutoApplyRollbackFailed` existed only in that file —
/// and both were NEAR-MISSES of real event names (`autonomy_autoapply_batch_rolled_back`,
/// `autonomy_autoapply_rollback_incomplete`). A subscriber filtering on the constant would have
/// matched nothing while the real events streamed past. That is precisely the empty-panel failure
/// the file was written to prevent, caused by the file itself: *declared, and reaching nobody*.
///
/// A rule a document states and nothing checks is a rule that describes the author's intention
/// rather than the tree. This is the check.
/// </summary>
public class EventVocabularyTests
{
    private static string Src() => Path.Combine(SourceText.RepoRoot(), "src");
    private static string VocabularyFile() =>
        Path.Combine(Src(), "Anthill.SDK", "Events", "EventTypes.cs");

    /// <summary>An event name handed to `LogEvent` as a literal: `LogEvent(mission.Id, "task_failed"`.</summary>
    private static readonly Regex LoggedLiteral =
        new(@"LogEvent\(\s*[^,()]+,\s*""(?<name>[a-z][a-z0-9_]*)""");

    /// <summary>Every `public const string X = "y";` in the vocabulary, as name → value.</summary>
    private static Dictionary<string, string> Declared() =>
        typeof(EventTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

    private static IEnumerable<string> SourceFiles() =>
        Directory.GetFiles(Src(), "*.cs", SearchOption.AllDirectories);

    /// <summary>
    /// Read from the TYPE rather than by parsing the file. The constants are compile-time literals,
    /// so reflection sees exactly what a consumer would — and a guard that re-parsed the declaration
    /// site would be checking its own regex against the same text it was derived from.
    /// </summary>
    [Fact]
    public void EveryEventTheRuntimeLogs_IsDeclaredInTheVocabulary()
    {
        var values = Declared().Values.ToHashSet(StringComparer.Ordinal);
        var undeclared = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in SourceFiles())
            foreach (Match m in LoggedLiteral.Matches(SourceText.CodeOnly(File.ReadAllText(file))))
            {
                var name = m.Groups["name"].Value;
                if (!values.Contains(name)) undeclared.TryAdd(name, Path.GetFileName(file));
            }

        Assert.True(undeclared.Count == 0,
            $"{undeclared.Count} event name(s) are emitted and not declared in EventTypes:\n  "
          + string.Join("\n  ", undeclared.Select(kv => $"{kv.Key}  ({kv.Value})"))
          + "\nA subscriber cannot filter on what the vocabulary does not name, and a filter that "
          + "matches nothing looks exactly like a quiet colony.");
    }

    /// <summary>
    /// The other direction, and the one that catches a near-miss.
    ///
    /// A constant nothing publishes is worse than a missing one when its value ALMOST matches a real
    /// event: the subscriber compiles, the filter runs, and it matches nothing forever.
    ///
    /// TWO CHANNELS COUNT AS PUBLICATION, and conflating them would have produced a false finding
    /// here. `Memory.LogEvent` writes the persisted log; the event bus carries
    /// `EventType = EventTypes.X`. `ModuleRegistered` is live through the second and appears in no
    /// `LogEvent` at all — an earlier draft of this sweep called it a phantom for exactly that
    /// reason, which is the adjacent-question defect committed while hunting one.
    /// </summary>
    [Fact]
    public void EveryDeclaredEvent_IsPublishedBySomething()
    {
        var declared = Declared();
        var vocabulary = VocabularyFile();

        var corpus = SourceFiles()
            .Where(f => !string.Equals(f, vocabulary, StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();

        var phantoms = declared
            .Where(kv => !corpus.Any(text =>
                text.Contains($"\"{kv.Value}\"", StringComparison.Ordinal)      // emitted as a literal
             || Regex.IsMatch(text, $@"EventTypes\.{Regex.Escape(kv.Key)}\b"))) // or used by name
            .Select(kv => $"{kv.Key} = \"{kv.Value}\"")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(phantoms.Count == 0,
            "these event types are declared and nothing publishes them:\n  "
          + string.Join("\n  ", phantoms)
          + "\nDeclare only what the code produces. A constant whose value nearly matches a real "
          + "event is the worst case: the filter compiles, runs, and matches nothing forever.");
    }

    /// <summary>
    /// Neither assertion above may pass over an empty set. A rename of `LogEvent`, or a change to how
    /// the constants are declared, would otherwise leave both of them green and blind — which is how
    /// the vocabulary drifted for as long as it did.
    /// </summary>
    [Fact]
    public void TheSweep_SeesBothTheVocabularyAndTheCallSites()
    {
        var declared = Declared();
        Assert.True(declared.Count >= 100,
            $"only {declared.Count} event constants were found by reflection. The vocabulary has more "
          + "than that; something about how they are declared has changed.");

        var logged = SourceFiles()
            .SelectMany(f => LoggedLiteral.Matches(SourceText.CodeOnly(File.ReadAllText(f))).Select(m => m.Groups["name"].Value))
            .Distinct(StringComparer.Ordinal)
            .Count();

        Assert.True(logged >= 100,
            $"only {logged} distinct event literals were found at LogEvent call sites. The runtime "
          + "emits more than that; the pattern this guard matches has moved.");
    }
}
