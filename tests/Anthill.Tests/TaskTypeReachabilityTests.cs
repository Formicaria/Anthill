using System.Text.RegularExpressions;
using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Anthill.Core.Planning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// EVERY TASK THIS COLONY CONSTRUCTS CAN BE RUN BY THE ROLE IT IS GIVEN TO. v0.3.8.135.
///
/// WHAT WAS WRONG, and it is one line that had been shipping since the adaptive controller was
/// written. `ExecutionService`'s delta-verification step built its task with `TaskType = "verify"`
/// and `AssignedAnt = "verifier"`. The verifier's contract declares exactly one type:
/// `"verification"`. So the dispatch chokepoint blocked it, `Critical = true` failed the mission on
/// it, and every adaptive replan that reached that line built a mission WIRED TO FAIL ITS OWN
/// RECOVERY — the step inserted precisely because the mission was already in trouble.
///
/// IT IS THE SAME WORD `.133` SHIPPED A RELEASE FOR. That release traced a dead mission to `verify`
/// versus `verification`, built <see cref="TaskTypeVocabulary"/>, and wired it into
/// `Planner.AssignDefaultWorkers` — the funnel every PLANNER path goes through, and no dynamic one
/// does. `HandoffGate` had the same hole from the other side: it refused an unreconciled type, and a
/// refused REQUIRED handoff is a deterministic block, so a medic asking a builder to `summarize` —
/// a word the builder's contract has a declared spelling for — could end a mission.
///
/// SO THE GUARD IS THE POINT OF THIS FILE, not the two fixes. Both were one-line defects that no
/// test could see, in a vocabulary spread across three files, and the reason they survived is that
/// nothing ever asked the question: does the role this task is addressed to declare the type it
/// carries? It is asked here, of every such pair in the source, so the next one fails on the day it
/// is written rather than in the field on a mission that was already struggling.
/// </summary>
public class TaskTypeReachabilityTests
{
    /// <summary>
    /// Every `AssignedAnt` / `TaskType` literal pair in production source, read by BRACE MATCHING
    /// the object initializer they sit in rather than by a character budget around one of them.
    ///
    /// The budget version was written first and is why this remark exists: `SourceText` already
    /// carries two paragraphs on it — a window is a proxy for "inside this initializer" and a bad
    /// one, invisible when it is wrong and drifting every time the block grows. Reading the
    /// delimiters answers the question that was actually being asked.
    /// </summary>
    private static IEnumerable<(string File, string Role, string Type)> DeclaredPairs()
    {
        var root = Path.Combine(SourceText.RepoRoot(), "src");

        foreach (var path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
             || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var code = SourceText.CodeOnly(File.ReadAllText(path));

            foreach (Match m in Regex.Matches(code, @"TaskType\s*=\s*""(?<t>[a-z_]+)"""))
            {
                var block = EnclosingInitializer(code, m.Index);
                if (block is null) continue;

                var role = Regex.Match(block, @"AssignedAnt\s*=\s*""(?<r>[a-z_]+)""");
                if (!role.Success) continue;

                yield return (Path.GetFileName(path), role.Groups["r"].Value, m.Groups["t"].Value);
            }
        }
    }

    /// <summary>The `{ … }` block immediately containing <paramref name="at"/>, or null.</summary>
    private static string? EnclosingInitializer(string code, int at)
    {
        var depth = 0;
        var open = -1;
        for (var i = at; i >= 0; i--)
        {
            if (code[i] == '}') depth++;
            else if (code[i] == '{')
            {
                if (depth == 0) { open = i; break; }
                depth--;
            }
        }
        if (open < 0) return null;

        depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[open..(i + 1)];
        }
        return null;
    }

    /// <summary>
    /// THE GUARD. A task addressed to a role must carry a type that role's contract declares.
    ///
    /// Asserted against the contract DIRECTLY rather than through <see cref="TaskTypeVocabulary"/>:
    /// the reconciler exists to rescue a spelling a MODEL produced, and a literal in this repository
    /// is not a model's guess — it is a choice, and the correct spelling was available when it was
    /// made. A source literal that needs rescuing is a defect that has merely been made survivable.
    /// </summary>
    [Fact]
    public void EveryConstructedTask_NamesATypeItsRoleCanRun()
    {
        var pairs = DeclaredPairs().ToList();

        // Non-vacuity first: a guard that found nothing to check reports success indistinguishably
        // from one whose subject moved, and this one reads the source by pattern.
        Assert.True(pairs.Count >= 15,
            $"expected many AssignedAnt/TaskType literal pairs in src/, found {pairs.Count} — this "
          + "guard is probably reading nothing.");

        var unreachable = pairs
            .Where(p => AntExecutionCatalog.ContractFor(p.Role) is { } c && !c.SupportsTaskType(p.Type))
            .Select(p => $"{p.File}: '{p.Type}' addressed to '{p.Role}', which declares "
                       + string.Join("/", AntExecutionCatalog.ContractFor(p.Role)!.SupportedTaskTypes))
            .Distinct()
            .ToList();

        Assert.True(unreachable.Count == 0,
            "these tasks name a type the role they are given to cannot run, so the dispatch gate "
          + "blocks them — and a critical one fails the mission:\n  "
          + string.Join("\n  ", unreachable));
    }

    /// <summary>
    /// THE ONE THAT WAS ACTUALLY BROKEN, asserted at the value rather than only through the sweep
    /// above — because the sweep would go green if somebody deleted the line, and the delta verifier
    /// is the mission's own recovery step.
    /// </summary>
    [Fact]
    public void TheAdaptiveDeltaVerification_IsRunnableByTheVerifier()
    {
        var resolved = TaskTypeVocabulary.Reconcile("verifier", "verify");

        Assert.Equal("verification", resolved);
        Assert.True(AntExecutionCatalog.ContractFor("verifier")!.SupportsTaskType(resolved));
    }

    /// <summary>
    /// AND THE SECOND DOOR. A handoff naming a synonym the destination's contract has a declared
    /// spelling for is ADMITTED, and the task it creates carries the reconciled type — because the
    /// created task is what dispatch reads, and admitting it with the requested spelling would make
    /// the gate's own check ceremonial.
    /// </summary>
    [Fact]
    public void AHandoffNamingASynonym_IsAdmittedWithTheDeclaredType()
    {
        var admission = HandoffGate.Evaluate(
            new AntHandoff("medic", "builder", "the diagnosis needs writing up", "summarize",
                Array.Empty<string>(), Required: true, Depth: 1, DedupeKey: "reach-k1"),
            new Mission { Id = "m1", Goal = "g" });

        Assert.True(admission.Accepted, admission.Reason);
        Assert.Equal("build_answer", admission.CreatedTask!.TaskType);
    }

    /// <summary>
    /// AND THE REFUSAL STILL FIRES. Reconciliation is placed BEFORE the contract check, never
    /// instead of it: a type nothing resolves is refused by name exactly as before. A gate that
    /// always passes is not a gate, which is the argument `.133` had to make about its own fix
    /// after the first cut made the refusal unreachable.
    /// </summary>
    [Fact]
    public void AHandoffNamingNothingAnyRoleDeclares_IsStillRefused()
    {
        var admission = HandoffGate.Evaluate(
            new AntHandoff("medic", "builder", "why not", "interpretive_dance",
                Array.Empty<string>(), Required: true, Depth: 1, DedupeKey: "reach-k2"),
            new Mission { Id = "m1", Goal = "g" });

        Assert.False(admission.Accepted);
        Assert.Contains("interpretive_dance", admission.Reason, StringComparison.Ordinal);
    }
}
