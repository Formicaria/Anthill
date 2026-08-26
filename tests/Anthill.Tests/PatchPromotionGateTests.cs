using System.Text.RegularExpressions;
using Anthill.Core.Verification;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// One authority on whether a proposed patch may be written. v0.3.8.91.
///
/// WHAT WAS WRONG. Five code paths could put a proposal's bytes on the operator's tree and each
/// carried its own precondition set. The Apply button checked five things — an approval row, its
/// status, its type, the patch's status — and **nothing about whether anything had verified the
/// change**. The bypass lane checked two, then satisfied the apply path's human gate with an
/// approval row it had just created itself. The auto-apply lane checked nine. One capability, five
/// answers, and the strictest was the only one with no human on it.
///
/// WHAT THE ACTOR CHANGES, and this is the property most worth pinning: exactly one condition, the
/// human. Everything else applies to everybody. *Skip All Approvals skips the human, not the
/// colony's safety system.*
///
/// These are structural assertions over the gate's shape and its call sites. The behavioural half —
/// a real refusal driven through a real mission — is `CodePatchLifecycleTests`' territory and is
/// where the next commit in this release puts it, alongside set atomicity.
/// </summary>
public class PatchPromotionGateTests
{
    private static string GateSource() => File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
        "src", "Anthill.Core", "Verification", "PatchPromotionGate.cs"));

    /// <summary>
    /// THE ACTOR CHANGES THE HUMAN AND NOTHING ELSE.
    ///
    /// Every non-human refusal must be reachable before the actor is consulted at all. If a
    /// condition moved inside the actor switch, one lane would stop being checked for it — which is
    /// precisely the state this gate replaced.
    /// </summary>
    [Fact]
    public void EveryConditionExceptTheHuman_IsCheckedBeforeTheActorIsConsulted()
    {
        var code = SourceText.CodeOnly(GateSource());
        var switchAt = code.IndexOf("switch (actor)", StringComparison.Ordinal);

        Assert.True(switchAt > 0, "the actor switch has moved; this guard reads it by shape.");

        var common = code[..switchAt];

        foreach (var refusal in new[]
                 {
                     nameof(PromotionRefusal.PatchUnknown),
                     nameof(PromotionRefusal.PatchStatusForbids),
                     nameof(PromotionRefusal.WriteGatesOff),
                     nameof(PromotionRefusal.RollbackHalted),
                     nameof(PromotionRefusal.DeterministicBlock),
                     nameof(PromotionRefusal.SecurityReviewBlocked),
                     nameof(PromotionRefusal.ReviewIncomplete),
                     nameof(PromotionRefusal.EvidenceAboutAnotherRevision),
                     nameof(PromotionRefusal.WorkspaceMoved),
                 })
            Assert.True(common.Contains(refusal, StringComparison.Ordinal),
                $"{refusal} is no longer checked before the actor switch. Moving a condition inside "
              + "the switch exempts at least one lane from it — Bypass or Automation would stop "
              + "being checked for something the Apply button still is, which is the divergence this "
              + "gate exists to end.");

        var actorScoped = code[switchAt..];
        Assert.Contains(nameof(PromotionRefusal.HumanApprovalMissing), actorScoped, StringComparison.Ordinal);
        Assert.Contains(nameof(PromotionRefusal.MissionNotVerified), actorScoped, StringComparison.Ordinal);
    }

    /// <summary>
    /// EVERY REFUSAL NAMES THE LAYER THAT SAID NO.
    ///
    /// A standing rule in this repository: a failure message that does not name the gate forces the
    /// operator to infer it. `PromotionVerdict.Refuse` takes the layer as a required argument, so a
    /// nameless refusal cannot be written — but a refusal could still pass an empty string, and this
    /// is what stops that.
    /// </summary>
    [Fact]
    public void NoRefusalIsAnonymous()
    {
        var code = SourceText.CodeOnly(GateSource());

        var refusals = Regex.Matches(code,
            @"PromotionVerdict\.Refuse\(\s*PromotionRefusal\.(?<r>\w+)\s*,\s*""(?<layer>[^""]*)""");

        Assert.True(refusals.Count >= 8,
            $"this guard found only {refusals.Count} refusal(s); the shape it reads has moved.");

        foreach (Match m in refusals)
            Assert.True(m.Groups["layer"].Value.Trim().Length > 0,
                $"the {m.Groups["r"].Value} refusal names no layer.");
    }

    /// <summary>
    /// EVERY DECLARED REFUSAL IS REACHABLE.
    ///
    /// An enum arm nothing returns is the v0.3.8.89 defect class — a value a consumer can switch on
    /// that no producer ever writes — applied to a safety verdict.
    /// </summary>
    [Fact]
    public void EveryRefusalReason_IsOneTheGateCanActuallyReturn()
    {
        var code = SourceText.CodeOnly(GateSource());

        var unreachable = Enum.GetNames<PromotionRefusal>()
            .Where(name => name != nameof(PromotionRefusal.None))
            .Where(name => !code.Contains($"PromotionRefusal.{name},", StringComparison.Ordinal))
            .ToList();

        Assert.True(unreachable.Count == 0,
            "these refusal reasons are declared and never returned: " + string.Join(", ", unreachable));
    }

    /// <summary>
    /// EVERY LANE THAT CAN WRITE A PROPOSED PATCH CONSULTS THE GATE.
    ///
    /// The Apply button and the bypass lane since v0.3.8.91; the auto-apply Director since
    /// v0.3.8.94, which closed the "keeps its own nine checks for now" note this comment used to
    /// carry. What the Director retains beside the gate is exactly the part a per-proposal gate
    /// cannot own: the set-level evidence content check, the whole-set preflight, and the durable
    /// transaction.
    /// </summary>
    [Theory]
    [InlineData("src/Anthill.Core/Orchestration/Queen.Views.cs", "PromotionActor.Human")]
    [InlineData("src/Anthill.Core/Orchestration/ExecutionService.cs", "PromotionActor.Bypass")]
    // v0.3.8.94 — the third lane folded in, closing this file's own "auto-apply keeps its nine
    // checks for now" note: the Director consults the gate as Automation for every eligible
    // proposal, and its former private copies of the evaluation/write-gate/rollback checks are
    // deleted. The set-level rules that stay in the runner (content hash, mixed deterministic
    // rows, patch-set identity) are about the SET, which a per-proposal gate cannot answer.
    [InlineData("src/Anthill.Api/AutoApplyRunner.cs", "PromotionActor.Automation")]
    public void TheApplyPaths_AskTheGate(string relativePath, string actor)
    {
        var code = SourceText.CodeOnly(File.ReadAllText(
            Path.Combine(SourceText.RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar))));

        Assert.Contains("PatchPromotionGate.Evaluate", code, StringComparison.Ordinal);
        Assert.Contains(actor, code, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE BYPASS LANE IS EVALUATED AS BYPASS, NOT AS A HUMAN.
    ///
    /// It reaches the apply path through `ApproveAndApplyPatch`, which CREATES the approval row and
    /// approves it. So the human gate downstream is satisfied by a row this same call minted
    /// microseconds earlier — an approval with no human in it. Evaluating the lane as `Bypass`
    /// before that happens is what subjects it to every non-human condition.
    /// </summary>
    [Fact]
    public void TheBypassLane_IsGatedBeforeItSynthesizesItsOwnApproval()
    {
        var code = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));

        var start = code.IndexOf("private void ApplyUnderBypass", StringComparison.Ordinal);
        Assert.True(start > 0, "ApplyUnderBypass has moved or been renamed.");

        // The WHOLE method, brace-matched, rather than a character window. v0.3.8.92: the window
        // was 4,000 characters, which fitted on Linux and ran three lines short on a Windows
        // checkout where CRLF makes every line one character longer — so this guard passed locally
        // and failed on main. A guard whose verdict depends on the reader's line endings is not
        // checking what it says it checks.
        var body = MethodBody(code, start);

        var gateAt = body.IndexOf("PatchPromotionGate.Evaluate", StringComparison.Ordinal);
        var applyAt = body.IndexOf("_applyPatchSet(", StringComparison.Ordinal);

        Assert.True(gateAt > 0, "the bypass lane no longer consults the promotion gate.");
        Assert.True(applyAt > 0, "the bypass lane's set-apply call has moved.");
        Assert.True(gateAt < applyAt,
            "the bypass lane applies before it asks the gate. The apply path it calls creates and "
          + "approves its own approval row, so asking afterwards means the only thing standing "
          + "between a bypass and the operator's tree is a human gate satisfied by a synthesized "
          + "human.");
    }

    /// <summary>
    /// One method's body, from its signature to its closing brace.
    ///
    /// Brace-matched rather than sliced to a character budget: a budget has to be guessed, the guess
    /// is invisible when it is wrong, and it silently means something different on a checkout with
    /// different line endings. Falls back to the rest of the file if the braces do not balance,
    /// which reads as "too much" rather than "too little" — a guard that over-reads reports a
    /// false pass on a neighbour, and one that under-reads reports a false failure on itself.
    /// </summary>
    private static string MethodBody(string code, int signatureAt)
    {
        var open = code.IndexOf('{', signatureAt);
        if (open < 0) return code[signatureAt..];

        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[signatureAt..(i + 1)];
        }
        return code[signatureAt..];
    }

    /// <summary>
    /// THE DETERMINISTIC BLOCK IS PERSISTED, AND THE GATE READS THE PERSISTED ONE.
    ///
    /// `Task.DeterministicBlock` had no column. It has gated the most consequential decision in the
    /// system since v3.8.21 and lived only on the in-memory object: a restart forgot every block,
    /// and a gate reading stored state could not see one at all. v0.3.8.91 gave it a column — which
    /// also means the fault-block this release added to `VerifyPatchSet` now survives the process
    /// that computed it, which its own comment had claimed before it was true.
    /// </summary>
    [Fact]
    public void TheDeterministicBlock_HasAColumn_AndIsWrittenAndRead()
    {
        var schema = File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Memory", "SqliteMemory.Schema.cs"));
        var operations = File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Memory", "SqliteMemory.Operations.cs"));

        Assert.Contains("[\"deterministic_block\"] = \"TEXT\"", schema, StringComparison.Ordinal);

        Assert.True(Regex.IsMatch(operations, @"INSERT OR REPLACE INTO tasks[\s\S]{0,900}deterministic_block"),
            "the task upsert no longer writes deterministic_block, so a block does not survive a restart.");

        Assert.True(Regex.IsMatch(operations, @"SELECT[\s\S]{0,700}deterministic_block[\s\S]{0,200}FROM tasks"),
            "GetTasksForMission no longer selects deterministic_block, so the promotion gate cannot "
          + "see a block that was persisted.");

        Assert.Contains("deterministic_block", SourceText.CodeOnly(GateSource()), StringComparison.Ordinal);
    }
}
