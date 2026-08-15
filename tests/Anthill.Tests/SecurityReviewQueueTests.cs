using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The v0.3.8.57 security review, as an EXECUTABLE LEDGER. v0.3.8.58.
///
/// WHY THIS FILE EXISTS. Four P0 and two P1 findings arrived against a release whose CI was green,
/// and — stated in the review itself — no GitHub issue tracks any of them. `docs/PLAN.md` §1b is
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

    /// <summary>The §1b section only — so a phrase appearing anywhere else in the plan cannot
    /// satisfy an assertion about the security queue.</summary>
    private static string SecuritySection()
    {
        var plan = Plan();
        var start = plan.IndexOf("## 1b. Security review", StringComparison.Ordinal);
        Assert.True(start >= 0,
            "docs/PLAN.md no longer has a '## 1b. Security review' section. Four P0 findings have no "
          + "other record — the review reported no open issues tracking them — so removing the section "
          + "removes the findings.");

        var end = plan.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return end < 0 ? plan[start..] : plan[start..end];
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
                $"{finding.Id} has no heading in PLAN.md §1b. A finding is closed by FIXING it and "
              + "saying so, never by deleting the paragraph that described it.");
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

    /// <summary>
    /// The queue is recorded as BLOCKING and sits ahead of the forward plan.
    ///
    /// Order is the substance here, not presentation. §2 is a list of ways to give the colony more
    /// authority; every finding above is a way the authority it already has is not contained. A
    /// security section filed politely after the roadmap is one that gets read after it too.
    /// </summary>
    [Fact]
    public void TheQueue_IsMarkedBlocking_AndPrecedesTheForwardPlan()
    {
        var plan = Plan();

        var security = plan.IndexOf("## 1b. Security review", StringComparison.Ordinal);
        var forward = plan.IndexOf("## 2. The plan, in order", StringComparison.Ordinal);

        Assert.True(security >= 0 && forward > security,
            "the security queue no longer precedes '## 2. The plan, in order'.");
        Assert.Contains("BLOCKING", SecuritySection());
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
            $"PLAN.md §1b tells the operator to set \"{flag}\": false for containment, and no source "
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
                $"§1b warns that {test} passes while proving something adjacent, and the file is gone.");
    }
}
