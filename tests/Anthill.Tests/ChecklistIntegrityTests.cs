using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A checklist box agrees with the prose under it. v0.3.8.76 (PLAN.md §2 R1).
///
/// THE DEFECT THIS CLOSES, and it cost a release plan. `PLAN.md`'s repair-order line for S7 sat
/// UNTICKED for ten releases while its own S7 section recorded the fault-injection suites as landed
/// in v0.3.8.65 — `ApplyTransactionTests`, `EvidenceFailsClosedTests`, `SubprocessHangTests`, all
/// three shipped, named, and passing. The box said open. The body said done. Both were in the same
/// document, four hundred lines apart.
///
/// Nothing could have caught it. `DocumentCurrencyTests` compares VERSION CLAIMS against the shipped
/// release, and this line names no version — it names a checkbox. `SecurityReviewQueueTests` asserts
/// the findings are recorded, which they were. So the plan's own forward schedule carried work that
/// was already finished, and the only reason it surfaced was a human reading two sections in the
/// same sitting.
///
/// A stale ledger entry and a stale checkbox are the same defect: a summary that stopped tracking
/// the thing it summarises. This repository already guards the first one. This is the second.
///
/// WHAT IT CANNOT DO. It cannot tell whether the body is TRUE — only whether the box agrees with it.
/// A section claiming work that never happened passes here and always will; that is a reading
/// problem, and the changelog is where the reading gets recorded. What it closes is the mechanical
/// half: a box and a body, in one document, saying different things.
/// </summary>
public class ChecklistIntegrityTests
{
    private static string Plan() => File.ReadAllText(
        Path.Combine(SourceText.RepoRoot(), "docs", "PLAN.md"));

    /// <summary>A numbered checklist line carrying a tick and an id: `3. ✅ **S3** — …`.</summary>
    private static readonly Regex Item = new(
        @"^\d+\.\s*(?<tick>✅|◻)\s*\*\*(?<id>S\d+)\*\*(?<rest>.*)$", RegexOptions.Multiline);

    /// <summary>
    /// Phrases in a body section that assert the work IS DONE. Deliberately narrow and past-tense —
    /// "closes with R3" and "will be closed" are schedule, not completion, and a guard that read
    /// them as completion would fire on every plan entry that names its own exit.
    /// </summary>
    private static readonly Regex BodySaysClosed = new(
        @"\b(?:landed|shipped|closed|fixed|resolved)\b(?:\s+\w+){0,3}\s+(?:in|at|by)\s+v\d+\.\d+\.\d+"
      + @"|\bis\s+(?:now\s+)?(?:closed|fixed|resolved|landed)\b"
      + @"|\bthis\s+is\s+done\b",
        RegexOptions.IgnoreCase);

    /// <summary>The body section for one finding id: from `#### S3 —` to the next heading.</summary>
    private static string? BodyOf(string plan, string id)
    {
        var start = plan.IndexOf($"#### {id} —", StringComparison.Ordinal);
        if (start < 0) return null;

        var end = plan.IndexOf("\n#", start + 1, StringComparison.Ordinal);
        return end < 0 ? plan[start..] : plan[start..end];
    }

    /// <summary>
    /// THE ASSERTION. No item's box says OPEN while its own body says the work shipped.
    ///
    /// This is the direction that cost something: an unticked box for finished work puts done work
    /// on the schedule, and the schedule is what the next release is planned from.
    /// </summary>
    [Fact]
    public void NoUntickedItem_HasABodyThatSaysItShipped()
    {
        var plan = Plan();
        var disagreeing = new List<string>();

        foreach (Match m in Item.Matches(plan))
        {
            if (m.Groups["tick"].Value == "✅") continue;

            var body = BodyOf(plan, m.Groups["id"].Value);
            if (body is null) continue;

            var claim = BodySaysClosed.Match(body);
            if (claim.Success)
                disagreeing.Add($"{m.Groups["id"].Value}: box is ◻, body says \"{claim.Value.Trim()}\"");
        }

        Assert.True(disagreeing.Count == 0,
            "these checklist items are unticked and their own sections record the work as done: "
          + string.Join("; ", disagreeing)
          + ". S7 sat in exactly this state for ten releases and put finished work on the forward "
          + "plan. Tick the box, or correct the section — the two cannot both be right.");
    }

    /// <summary>
    /// Every item's id resolves to a body. An id with no section is a checkbox for something the
    /// document no longer describes, and it would make the assertion above skip it in silence —
    /// which is how a guard comes to pass by finding nothing.
    /// </summary>
    [Fact]
    public void EveryChecklistItem_HasASectionItRefersTo()
    {
        var plan = Plan();

        var orphans = Item.Matches(plan)
            .Select(m => m.Groups["id"].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(id => BodyOf(plan, id) is null)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.True(orphans.Count == 0,
            "these checklist items have no `#### <id> —` section: " + string.Join(", ", orphans)
          + ". The tick-versus-body check cannot run on them, and passes.");
    }

    /// <summary>
    /// The checklist is not empty. The two assertions above are both satisfied by a document with no
    /// checklist at all, so the thing they read has to be shown to exist — the same reason
    /// `ContractDeclarationTests` checks its own role mapping resolves before asserting on it.
    /// </summary>
    [Fact]
    public void ThereIsAChecklistToCheck()
    {
        var count = Item.Matches(Plan()).Count;

        Assert.True(count >= 7,
            $"docs/PLAN.md has {count} numbered checklist items with ids; the security repair order "
          + "alone has seven. Either the format changed and this guard now reads nothing, or the "
          + "list is gone.");
    }
}
