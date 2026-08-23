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
    /// THE OTHER SIDE OF THE CALL, and the blind spot it found. v0.3.8.89.
    ///
    /// The two assertions above read PUBLICATION: event names handed directly to `LogEvent`. That is
    /// what v0.3.8.86 could see, and its own doc comment says so — but a name passed through a
    /// wrapper never appears in that position. `ExecutionService.RecordAdaptiveAdmission` takes the
    /// event type as a parameter and its callers pass the literal; the memory-candidate names reach
    /// `LogEvent` the same way. All four were emitted, queried, asserted on in tests, and declared
    /// nowhere — while the sweep above reported the vocabulary complete.
    ///
    /// So this reads CONSUMPTION instead. `GetRecentEvents(limit, "name", ...)` names an event type
    /// in an unambiguous position — the parameter exists for nothing else — which makes it a
    /// zero-false-positive detector and a genuinely different question from the one above.
    ///
    /// It is not a general fix for wrapper-passed names: an event emitted through a wrapper and never
    /// queried by name is still invisible to both directions. That is a known remaining gap and it is
    /// recorded as one rather than implied to be closed.
    /// </summary>
    [Fact]
    public void EveryEventTypeQueriedByName_IsDeclared()
    {
        var values = Declared().Values.ToHashSet(StringComparer.Ordinal);
        var queried = new SortedDictionary<string, string>(StringComparer.Ordinal);

        var roots = new[] { Path.Combine(SourceText.RepoRoot(), "src"), Path.Combine(SourceText.RepoRoot(), "tests") };
        foreach (var root in roots)
            foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                 || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    continue;

                foreach (Match m in QueriedByName.Matches(SourceText.CodeOnly(File.ReadAllText(file))))
                    queried.TryAdd(m.Groups["name"].Value, Path.GetFileName(file));
            }

        Assert.True(queried.Count >= 10,
            $"only {queried.Count} event type(s) are queried by name across the tree. More than that "
          + "are; the call shape this reads has moved and the guard is checking nothing.");

        var undeclared = queried.Where(kv => !values.Contains(kv.Key)).ToList();
        Assert.True(undeclared.Count == 0,
            $"{undeclared.Count} event type(s) are QUERIED by name and declared nowhere:\n  "
          + string.Join("\n  ", undeclared.Select(kv => $"{kv.Key}  ({kv.Value})"))
          + "\nSomething reads these events, so they are part of the vocabulary whether or not they "
          + "reach LogEvent as a literal. A caller filtering on a name the vocabulary does not carry "
          + "is one rename away from matching nothing forever.");
    }

    /// <summary>An event type in the one position that can only be an event type:
    /// <c>GetRecentEvents(200, "adaptive_repair", missionId)</c>.</summary>
    private static readonly Regex QueriedByName =
        new(@"GetRecentEvents\(\s*\d+\s*,\s*""(?<name>[a-z][a-z0-9_]*)""");

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
