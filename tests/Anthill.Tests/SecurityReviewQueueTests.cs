using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The v0.3.8.57 security review, as an EXECUTABLE LEDGER. v0.3.8.58.
///
/// WHY THIS FILE EXISTS. Four P0 and two P1 findings arrived against a release whose CI was green,
/// and — stated in the review itself — no GitHub issue tracks any of them. `docs/PLAN.md`'s security-review section is
/// therefore the only record they have. A prose section is exactly the kind of record that survives
/// one release and gets tidied away in the next, and the findings it holds are the ones that decide
/// whether autonomy can be switched back on.
///
/// So the findings are pinned the same way `QualificationMatrixTests` pins the scenario set: every
/// claim the plan makes about the code is checked shallowly against the code, so a citation cannot
/// rot silently into a file that was renamed or a config flag that was never real. That second one
/// is not hypothetical — the containment block instructs an operator to set five flags, and a
/// containment instruction naming a flag that does not exist is worse than no instruction, because
/// it is followed and believed.
///
/// WHAT THIS FILE DOES NOT DO. It does not verify a single finding. A test that could establish
/// "symlink traversal is possible here" would be the fix's test, not the plan's, and it belongs with
/// S1 when S1 lands. These assertions establish only that the findings are RECORDED, that they name
/// real things, and that nobody can mark them closed by deleting them — which is the failure mode a
/// document-only record actually has.
/// </summary>
public class SecurityReviewQueueTests
{
    private static string Plan() => File.ReadAllText(
        Path.Combine(SourceText.RepoRoot(), "docs", "PLAN.md"));

    /// <summary>
    /// The security-review section only — so a phrase appearing anywhere else in the plan cannot
    /// satisfy an assertion about the queue.
    ///
    /// Located by NAME rather than by section number. Restructuring the plan into a prioritised
    /// release list renumbered its sections, and this locator was hard-coded to the old ordinal — so
    /// a reorganisation that moved every finding intact failed as though they had been deleted. What must not change is that the section
    /// exists and still says what is wrong; where it sits in the numbering is presentation, and a
    /// guard that cannot tell those apart teaches people to renumber around it.
    /// </summary>
    private static string SecuritySection()
    {
        var plan = Plan();
        var heading = SecurityHeading(plan);
        Assert.True(heading >= 0,
            "docs/PLAN.md no longer has a '## … Security review' section. Four P0 findings have no "
          + "other record — the review reported no open issues tracking them — so removing the section "
          + "removes the findings.");

        var end = plan.IndexOf("\n## ", heading + 1, StringComparison.Ordinal);
        return end < 0 ? plan[heading..] : plan[heading..end];
    }

    /// <summary>Index of the security section's heading line, or -1.</summary>
    private static int SecurityHeading(string plan)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            plan, @"^##\s+\S+\s+.*Security review.*$",
            System.Text.RegularExpressions.RegexOptions.Multiline
          | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return m.Success ? m.Index : -1;
    }

    // -------------------------------------------------------------------------------------------
    // The findings are recorded, and they are recorded as BLOCKING
    // -------------------------------------------------------------------------------------------

    /// <param name="Id">The plan's own heading id.</param>
    /// <param name="Subject">A phrase from the finding that will not survive a rewrite that guts it.</param>
    /// <param name="Evidence">A file the finding cites, which must still exist.</param>
    private sealed record Finding(string Id, string Priority, string Subject, string Evidence);

    private static readonly Finding[] Findings =
    {
        new("S1", "P0", "Filesystem confinement",
            "src/Anthill.Core/Security/WorkspacePathGuard.cs"),
        new("S2", "P0", "Shell tool confinement",
            "src/Anthill.Modules/Anthill.Modules.Tools/ShellAndWebTools.cs"),
        new("S3", "P0", "Verification and evidence fail OPEN",
            "src/Anthill.Api/AutoApplyRunner.cs"),
        new("S4", "P0", "Transactional patch application",
            "src/Anthill.Modules/Anthill.Modules.Tools/ApplyPatchTool.cs"),
        new("S5", "P0", "does not prevent model disclosure",
            "src/Anthill.Core/Domain/ArtifactContext.cs"),
        new("S6", "P1", "UI-map gate fails open",
            "src/Anthill.Core/Agents/UiChangeGate.cs"),
        new("S7", "P1", "Subprocess timeouts that cannot fire",
            "src/Anthill.Core/Projects/RepoOps.cs"),
    };

    [Fact]
    public void EveryFinding_IsStillRecordedInThePlan()
    {
        var section = SecuritySection();

        foreach (var finding in Findings)
        {
            Assert.True(section.Contains($"#### {finding.Id} —", StringComparison.Ordinal),
                $"{finding.Id} has no heading in the plan's security-review section. A finding is "
              + "closed by FIXING it and saying so, never by deleting the paragraph that described it.");
            Assert.True(section.Contains(finding.Subject, StringComparison.Ordinal),
                $"{finding.Id}'s heading survives but its subject (\"{finding.Subject}\") does not, so "
              + "the section has been reworded into something that no longer says what is wrong.");
        }
    }

    /// <summary>
    /// Every cited file is real. A review is only actionable while its citations resolve, and these
    /// span four projects — the exact situation where a rename lands somewhere the citation does not.
    /// </summary>
    [Fact]
    public void EveryCitedFile_StillExists()
    {
        foreach (var finding in Findings)
        {
            var path = Path.Combine(SourceText.RepoRoot(),
                finding.Evidence.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path),
                $"{finding.Id} cites {finding.Evidence}, which no longer exists. Either the finding "
              + "moved with the code and the citation must follow it, or the code is gone and the "
              + "finding needs re-stating against what replaced it. A dangling citation reads as a "
              + "fixed defect.");
        }
    }

    // -------------------------------------------------------------------------------------------
    // The priority rule, conditional on the thing it was always about
    // -------------------------------------------------------------------------------------------

    /// <summary>The repair-order list: one line per finding, ticked or not, with what closed it.</summary>
    private static readonly System.Text.RegularExpressions.Regex RepairLine = new(
        @"^\d+\.\s*(?<tick>✅|◻)\s*\*\*(?<id>S\d)\*\*(?<rest>.*)$",
        System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>
    /// EVERY finding appears exactly once in the repair order, ticked or not.
    ///
    /// Split out of the ordering assertion because it is the half that never expires: whatever the
    /// section is numbered and wherever it sits, a finding that vanishes from the repair order has
    /// been closed by omission. It is also what the two assertions below both read, so a finding
    /// that dropped out cannot make them vacuously pass.
    /// </summary>
    [Fact]
    public void TheRepairOrder_ListsEveryFindingExactlyOnce()
    {
        var listed = RepairLine.Matches(SecuritySection())
            .Select(m => m.Groups["id"].Value).ToList();

        foreach (var finding in Findings)
            Assert.True(listed.Count(id => id == finding.Id) == 1,
                $"{finding.Id} appears {listed.Count(id => id == finding.Id)} times in the repair "
              + "order and must appear exactly once. A finding with no line has no state — neither "
              + "open nor closed — and the assertions that read this list would skip it in silence.");
    }

    /// <summary>
    /// WHILE ANY FINDING IS OPEN the queue is marked BLOCKING and sits ahead of the forward plan.
    ///
    /// Order is the substance here, not presentation. §2 is a list of ways to give the colony more
    /// authority; every open finding is a way the authority it already has is not contained. A
    /// security section filed politely after the roadmap is one that gets read after it too.
    ///
    /// WHY THIS IS NOW CONDITIONAL, and why that is not the guard being loosened to let a document
    /// change through. Written at v0.3.8.58 the rule was unconditional, because at v0.3.8.58 every
    /// finding was open and the two facts were the same fact. They came apart when S1–S7 closed at
    /// v0.3.8.65: the section stopped being a queue and became the record of one. An unconditional
    /// rule then demands that closed history outrank the forward plan and that a closed review still
    /// call itself BLOCKING — which is a false sentence in a current document, and one written to
    /// satisfy a substring rather than to be true. The rule is not removed. It re-arms the moment a
    /// line in the repair order loses its tick, which is exactly when it has something to defend.
    /// </summary>
    [Fact]
    public void WhileAnyFindingIsOpen_TheQueueIsBlocking_AndPrecedesTheForwardPlan()
    {
        var plan = Plan();
        var section = SecuritySection();

        var open = RepairLine.Matches(section)
            .Where(m => m.Groups["tick"].Value != "✅")
            .Select(m => m.Groups["id"].Value).ToList();

        if (open.Count == 0) return;   // closed — the assertion below covers this state instead

        var security = SecurityHeading(plan);
        var forward = plan.IndexOf("\n## 2.", StringComparison.Ordinal);

        Assert.True(forward > security,
            $"{string.Join(", ", open)} are still open and the security queue no longer precedes the "
          + "forward plan (§2). While anything here is open it is read first or it is not read.");
        Assert.Contains("BLOCKING", section);
    }

    /// <summary>
    /// AND WHEN EVERY FINDING IS CLOSED, each one names the release that closed it.
    ///
    /// This is what replaces the priority rule rather than nothing replacing it. "Closed" is a claim
    /// about a shipped release, so the claim carries the version — a bare tick is indistinguishable
    /// from a tidy-up, and it is the tick that removes the finding from the assertion above.
    /// </summary>
    [Fact]
    public void EveryClosedFinding_NamesTheReleaseThatClosedIt()
    {
        var version = new System.Text.RegularExpressions.Regex(@"v\d+\.\d+\.\d+(\.\d+)?");

        foreach (System.Text.RegularExpressions.Match m in RepairLine.Matches(SecuritySection()))
        {
            if (m.Groups["tick"].Value != "✅") continue;

            Assert.True(version.IsMatch(m.Groups["rest"].Value),
                $"{m.Groups["id"].Value} is ticked closed and names no release. A tick is what takes "
              + "a finding out of the BLOCKING rule, so it has to be evidence of a shipped fix and "
              + "not of somebody reaching the end of the line.");
        }
    }

    // -------------------------------------------------------------------------------------------
    // The containment instructions are real
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Every flag the containment block tells an operator to set EXISTS in the source.
    ///
    /// This is the assertion with teeth. The block is an instruction to be followed under incident
    /// conditions, and a flag name that is subtly wrong — renamed, or never real — produces an
    /// operator who has containment they do not have. That is strictly worse than an operator who
    /// knows the system is exposed, because only the second one keeps looking.
    /// </summary>
    [Theory]
    [InlineData("autonomy_autoapply_enabled")]
    [InlineData("patch_application_enabled")]
    [InlineData("file_writing_enabled")]
    [InlineData("file_tools_enabled")]
    [InlineData("shell_tool_enabled")]
    public void EveryContainmentFlag_IsARealSetting(string flag)
    {
        Assert.Contains(flag, SecuritySection());

        var found = Directory.EnumerateFiles(
                Path.Combine(SourceText.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Any(p => File.ReadAllText(p).Contains(flag, StringComparison.Ordinal));

        Assert.True(found,
            $"the plan's security-review section tells the operator to set \"{flag}\": false for "
          + "containment, and no source "
          + "file mentions it. Either the setting was renamed and the instruction now silently does "
          + "nothing, or it never existed — and an operator following it believes they are contained "
          + "when they are not.");
    }

    /// <summary>
    /// The containment block SAYS the flags are not sufficient on their own.
    ///
    /// The Files-pane endpoints do not consult the runtime write flags and their read route is
    /// escapable by itself, so an operator who sets all five and stops has closed less than they
    /// think. This is the one place the plan must not be tidied into a clean checklist.
    /// </summary>
    [Fact]
    public void TheContainmentBlock_SaysTheFlagsAreNotEnoughAlone()
    {
        var section = SecuritySection();

        Assert.Contains("/projects/{id}/file", section);
        Assert.Contains("do not consult the runtime write flags", section);
    }

    // -------------------------------------------------------------------------------------------
    // The two findings that green CI already agreed with
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// S4 and S6 are recorded WITH the reason the existing suite passes over them, because "CI is
    /// green" is the argument that will be made against this queue and it is answered in advance.
    ///
    /// `AutoApplyAtomicityTests` asserts that the SOURCE contains a rollback call rather than that a
    /// tree is restored; `UiChangeGateTests` proves a truncated `ui_map` is refused while `{}`
    /// conforms to a schema requiring no keys. Both pass. Both answer a question adjacent to the one
    /// asked — this repository's own most frequent defect, found here in its own guards.
    /// </summary>
    [Fact]
    public void TheAdjacentPassingTests_AreNamedRatherThanTrusted()
    {
        var section = SecuritySection();

        Assert.Contains("AutoApplyAtomicityTests.cs", section);
        Assert.Contains("UiChangeGateTests", section);
        Assert.Contains("adjacent", section);

        // And both cited test files still exist, or the warning is about nothing.
        foreach (var test in new[] { "AutoApplyAtomicityTests.cs", "UiChangeGateTests.cs" })
            Assert.True(File.Exists(Path.Combine(SourceText.RepoRoot(), "tests", "Anthill.Tests", test)),
                $"the plan warns that {test} passes while proving something adjacent, and the file is gone.");
    }
}
