using System.Text.RegularExpressions;
using Anthill.Core.Memory;
using Anthill.Core.Pheromones;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The trail vocabulary matches the call sites, in BOTH directions. v3.8.31.
///
/// `TrailKind` was written in v3.8.29 from a prose description of the kinds in use, and was wrong
/// twice over: it declared `procedural_route` and `skill`, which nothing writes, and omitted
/// `model_route`, which `ModelRouter` writes on every routed call.
///
/// Declaring a category the system does not produce is the phantom-tool defect wearing different
/// clothes — three of those were deleted in v3.8.23 for exactly this reason. Omitting one that IS
/// produced defeats the guard entirely, because the undeclared kind is the one a typo creates.
///
/// This test reads the SOURCE, so the vocabulary can only ever be as right as the code.
/// </summary>
public class PheromoneVocabularyTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    /// <summary>Every `trail_type` argument passed to `UpdatePheromoneTrail` anywhere in src/.</summary>
    private static HashSet<string> WrittenKinds()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var text = File.ReadAllText(file);
            foreach (Match call in Regex.Matches(text, @"UpdatePheromoneTrail\s*\("))
            {
                // Walk to the second top-level argument — the trail TYPE. The first is often an
                // interpolated key containing its own string literals, so taking "the second literal"
                // would read the wrong thing.
                var segment = text[call.Index..Math.Min(text.Length, call.Index + 400)];
                var open = segment.IndexOf('(');
                if (open < 0) continue;

                int depth = 0, argIndex = 0, start = open + 1;
                for (var i = open; i < segment.Length; i++)
                {
                    var c = segment[i];
                    if (c is '(' or '[' or '{') depth++;
                    else if (c is ')' or ']' or '}')
                    {
                        depth--;
                        if (depth == 0) break;
                    }
                    else if (c == ',' && depth == 1)
                    {
                        if (argIndex == 1)
                        {
                            var arg = segment[start..i];
                            var lit = Regex.Match(arg, "\"([a-z_]+)\"");
                            if (lit.Success) found.Add(lit.Groups[1].Value);
                            break;
                        }
                        argIndex++;
                        start = i + 1;
                    }
                }
            }
        }
        return found;
    }

    /// <summary>Every kind the code WRITES must be declared, or nothing can reason about its subject.</summary>
    [Fact]
    public void EveryWrittenTrailKind_IsDeclared()
    {
        var undeclared = WrittenKinds().Where(k => !TrailKind.IsKnown(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(undeclared.Count == 0,
            "These trail kinds are written but not declared in TrailKind, so a reader cannot tell "
            + "whether they describe a worker, a tool or a route: " + string.Join(", ", undeclared));
    }

    /// <summary>
    /// ...and every kind DECLARED must be written. This is the half that caught the v3.8.29 mistake:
    /// a vocabulary is allowed to be incomplete for a moment, but a vocabulary describing categories
    /// the system never produces is fiction that later readers will trust.
    /// </summary>
    [Fact]
    public void EveryDeclaredTrailKind_IsActuallyWritten()
    {
        var written = WrittenKinds();
        var phantom = TrailKind.All.Where(k => !written.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(phantom.Count == 0,
            "These trail kinds are declared but nothing writes them — declare only what the code "
            + "produces: " + string.Join(", ", phantom));
    }

    /// <summary>
    /// Reputation and environment stay disjoint. A failing tool and a failing worker are different
    /// facts with different remedies; a kind in both sets would let one be mistaken for the other.
    /// </summary>
    [Fact]
    public void ReputationAndEnvironmentalKinds_DoNotOverlap()
    {
        var overlap = TrailKind.Reputation.Intersect(TrailKind.Environmental, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(overlap.Count == 0, "kinds in both partitions: " + string.Join(", ", overlap));
    }

    /// <summary>`model_route` is environmental — a provider that timed out says nothing about the
    /// worker whose task it was. Pinned because that is the whole reason it is tracked separately.</summary>
    [Fact]
    public void ModelRoute_IsEnvironmentalNotReputation()
    {
        Assert.Contains(TrailKind.ModelRoute, TrailKind.Environmental);
        Assert.False(TrailKind.IsReputation(TrailKind.ModelRoute));
    }

    /// <summary>
    /// Every declared kind CLASSIFIES. v3.8.32.
    ///
    /// `SignalCategoryFor` decides which trails may steer planning, and its fallback arm is
    /// `operational_telemetry` — a category `Top` and `Recall` both exclude. So a kind that is
    /// declared, written and validated, but has no arm in that switch, is recorded and then silently
    /// ignored by everything that learns. It would look completely healthy: the trail row exists,
    /// the vocabulary guard passes, the write emits no warning.
    ///
    /// The two sides agree today. This is what makes that a fact rather than a coincidence — it is
    /// the same producer/consumer agreement check as the rest of v3.8.32, applied to the boundary
    /// between the vocabulary and the thing that reads it.
    /// </summary>
    [Fact]
    public void EveryDeclaredTrailKind_HasAnExplicitSignalCategory()
    {
        var unclassified = TrailKind.All
            .Where(kind => SqliteMemory.SignalCategoryFor(kind) == "operational_telemetry")
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(unclassified.Count == 0,
            "These kinds are declared but fall through SignalCategoryFor to `operational_telemetry`, "
            + "so they are recorded and then excluded from everything that learns: "
            + string.Join(", ", unclassified));
    }

    /// <summary>
    /// Every kind's signal category, as a TABLE rather than as a rule.
    ///
    /// The first version of this test asserted a rule I had inferred instead of read: "environmental
    /// kinds do not steer planning, except model_route". That is false — `capability` is
    /// environmental AND classifies as `procedural_learning` — and the comment I wrote next to it
    /// confidently stated the opposite of what `TrailKind.Environmental` actually declares. Written
    /// from memory, in the release whose entire subject is checking one side of a boundary against
    /// the other rather than against a belief about it.
    ///
    /// THE TWO PARTITIONS ARE ORTHOGONAL, which is what I had missed and is worth stating plainly
    /// because it is not obvious:
    ///
    /// <list type="bullet">
    /// <item><c>Reputation</c> vs <c>Environmental</c> asks WHOSE record this is — a worker's, or the
    ///   environment's. It decides whether a failure may be charged to an ant.</item>
    /// <item><c>SignalCategoryFor</c> asks whether the trail may STEER PLANNING. It decides whether
    ///   the planner may read it.</item>
    /// </list>
    ///
    /// A capability trail is environmental (no ant earns credit for the colony owning a tool) and
    /// strategic (the planner absolutely should know whether structured patch proposals work here).
    /// Both at once, with no contradiction. `model_route` is the same shape.
    ///
    /// So this pins the actual mapping per kind. A table cannot be satisfied by a plausible-sounding
    /// generalisation, and adding a kind forces a row rather than letting it inherit a default.
    /// </summary>
    [Fact]
    public void EveryTrailKind_HasTheSignalCategoryItWasGiven()
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Strategy — what verifiably works. The planner may read these.
            [TrailKind.Ant] = "procedural_learning",
            [TrailKind.Worker] = "procedural_learning",
            [TrailKind.TaskType] = "procedural_learning",
            [TrailKind.PlannerPattern] = "procedural_learning",
            [TrailKind.WorkerPattern] = "procedural_learning",
            [TrailKind.TaskPattern] = "procedural_learning",
            // Environmental, and STILL strategic: whether the colony can do a thing is a planning
            // input even though no ant gets credit for it.
            [TrailKind.Capability] = "procedural_learning",
            // Environmental and plannable, as a routing preference rather than as strategy.
            [TrailKind.ModelRoute] = "routing_preference",
            // Advisory only — never proven truth, never steers.
            [TrailKind.SourceDomain] = "quality_signal",
            // Did the thing answer. Reliability, not strategy.
            [TrailKind.Tool] = "reliability_signal",
            [TrailKind.ExternalResearchTool] = "reliability_signal",
        };

        // The table must cover the vocabulary exactly — otherwise a new kind could be added, fall to
        // the `operational_telemetry` fallback, and this test would never notice.
        Assert.Equal(
            TrailKind.All.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            expected.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        foreach (var (kind, category) in expected)
            Assert.Equal(category, SqliteMemory.SignalCategoryFor(kind));
    }

    /// <summary>
    /// A worker's or role's own record ALWAYS steers planning. This is the half of the old test that
    /// was true: a kind naming a worker but classified as telemetry would make reputation
    /// unreadable by the thing that needs it.
    /// </summary>
    [Fact]
    public void EveryReputationKind_SteersPlanning() =>
        Assert.All(TrailKind.Reputation,
            kind => Assert.Equal("procedural_learning", SqliteMemory.SignalCategoryFor(kind)));

    /// <summary>
    /// The orthogonality, asserted rather than only described — because I got it wrong once by
    /// reasoning about it instead of checking. An environmental kind that steers planning is FINE;
    /// what would be wrong is a reputation kind that does not.
    /// </summary>
    [Fact]
    public void BeingEnvironmental_DoesNotDecideWhetherATrailMaySteerPlanning()
    {
        var planning = new[] { "procedural_learning", "routing_preference" };

        Assert.Contains(TrailKind.Environmental,
            k => planning.Contains(SqliteMemory.SignalCategoryFor(k)));
        Assert.Contains(TrailKind.Environmental,
            k => !planning.Contains(SqliteMemory.SignalCategoryFor(k)));
    }

    /// <summary>
    /// An UNDECLARED kind is still recorded. Deliberate, and worth a test of its own so the choice is
    /// visible: a new kind is usually a decision someone is midway through making, and losing the
    /// observation would be a worse outcome than the inconsistency. The write warns; it does not
    /// throw and does not drop.
    /// </summary>
    [Fact]
    public void AnUndeclaredKind_WarnsButIsStillRecorded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"anthill-vocab-{Guid.NewGuid():N}.db");
        try
        {
            using var memory = new SqliteMemory(path);
            memory.UpdatePheromoneTrail("probe::undeclared", "not_a_declared_kind", success: true, strengthDelta: 0.2);

            Assert.Contains(memory.ListPheromoneTrails(50),
                row => (row.GetValueOrDefault("trail_key")?.ToString() ?? "") == "probe::undeclared");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>
    /// THE FOURTH DIRECTION: every arm of the category switch names a kind that can arrive. v0.3.8.90.
    ///
    /// Three guards in this file already check declared→written, written→declared and
    /// declared→categorised. `SqliteMemory.SignalCategoryFor` had four arms — "objective",
    /// "objective_pattern", "model_provider", "provider" — for kinds `TrailKind` does not declare and
    /// nothing writes, so they were unreachable and the `_ => "operational_telemetry"` fallback made
    /// them invisible. That is the v0.3.8.89 class ("a filter that could not match") in a switch
    /// rather than a query: unreachable arms read as coverage, and the next person adding a kind
    /// looks at the switch, sees a plausible-looking arm, and assumes it is wired.
    ///
    /// Reads the SOURCE because switch arms are not enumerable at runtime — and reads the arms only,
    /// not the fallback, which is exactly what a `_` is for.
    /// </summary>
    [Fact]
    public void EverySignalCategoryArm_NamesADeclaredTrailKind()
    {
        var file = Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Memory", "SqliteMemory.Operations.cs");
        var code = SourceText.CodeOnly(File.ReadAllText(file));

        var start = code.IndexOf("SignalCategoryFor", StringComparison.Ordinal);
        Assert.True(start >= 0, "SignalCategoryFor has moved or been renamed; this guard reads it by name.");

        var end = code.IndexOf("};", start, StringComparison.Ordinal);
        Assert.True(end > start, "the category switch no longer ends where this guard expects.");

        // The LEFT sides only. A switch arm's kind is always followed by `or` or `=>`; the category
        // it returns is followed by a comma. Matching every quoted string in the region would sweep
        // the categories in as well and report `procedural_learning` as an undeclared trail kind —
        // a guard failing on its own right-hand side, which is this repository's favourite mistake.
        var arms = Regex.Matches(code[start..end], @"""(?<kind>[a-z][a-z0-9_]*)""(?=\s*(?:or\s|=>))")
            .Select(m => m.Groups["kind"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(arms.Count >= 8,
            $"this guard found only {arms.Count} arm(s) in the category switch; the shape it reads "
          + "has changed and it would pass over almost nothing.");

        var unreachable = arms
            .Where(kind => !TrailKind.All.Contains(kind))
            .OrderBy(kind => kind, StringComparer.Ordinal)
            .ToList();

        Assert.True(unreachable.Count == 0,
            "SignalCategoryFor has arms for trail kinds nothing can write: "
          + string.Join(", ", unreachable)
          + ". TrailKind.All is the whole population — an arm outside it is unreachable, and the "
          + "`_` fallback means it fails silently rather than visibly.");
    }
}
