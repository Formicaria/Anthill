using Anthill.Core.Agents;
using Anthill.Core.Common;
using Anthill.Core.Verification;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// WHICH VERIFICATION POLICIES PRODUCTION CAN ACTUALLY SELECT, and which are DORMANT ON PURPOSE.
/// v0.3.8.94.
///
/// The policy table has carried keys since v2.12 that no production task type has ever reached:
/// nothing emits `code_patch_full`, `config_change`, or `artifact_production` — the planner's
/// types are normalized against role contracts (the coder supports exactly
/// patch_proposal/patch/code_change, all aliased to `code_patch`), and `InferTaskType` produces
/// none of the three. A table key nothing selects is "declared, and reaching nobody" (defect
/// class 2) — UNLESS it is a recorded decision, which these are: `code_patch_full` is the
/// documented escape hatch for requiring the full test suite ("can be required per task type when
/// someone wants that trade", v3.8.21), and the other two await their producers (shadow
/// operations name `config_change` as its intended key).
///
/// This ledger converts the accident into the decision. A dormant key that GAINS a producer must
/// be moved to the reachable list here — consciously — and a reachable key that loses its last
/// producer must be moved the other way, not left looking alive.
/// </summary>
public class VerificationPolicyReachabilityTests
{
    /// <summary>Every task type production can put on a patch-producing task: the coder contract's
    /// declared types (the planner normalizes against them), plus the alias spellings the table
    /// maps, plus the docs types.</summary>
    private static readonly string[] ProductionTaskTypes =
    {
        "patch_proposal", "patch", "code_change", "docs_update", "documentation",
        // The inferred types for every other role, none of which is a patch policy key.
        "research", "file_inspection", "build_answer", "verification", "external_research",
        "validation_check", "security_review", "failure_diagnosis", "memory_consolidation",
        "ui_mapping", "operator_documentation", "general",
    };

    /// <summary>The keys production reaches, and how.</summary>
    [Fact]
    public void TheReachableKeys_AreCodePatch_AndDocsPatchViaPaths()
    {
        // Every patch-shaped production type lands on code_patch…
        foreach (var t in new[] { "patch_proposal", "patch", "code_change" })
            Assert.Equal("code_patch", VerificationPolicy.Canonical(t));

        // …and docs_patch is reached from what the SET TOUCHES, never from the type alone.
        Assert.Equal("docs_patch",
            VerificationPolicy.Canonical("patch_proposal", new[] { "docs/notes.md", "README.md" }));
        Assert.Equal("code_patch",
            VerificationPolicy.Canonical("patch_proposal", new[] { "docs/notes.md", "src/App.cs" }));
        Assert.Equal("docs_patch", VerificationPolicy.Canonical("docs_update"));
    }

    /// <summary>
    /// THE DORMANT LEDGER. Each key is real in the table (its policy differs from the
    /// unknown-type fallback), and no production task type selects it. If this fails on the
    /// "no production type reaches it" half, a producer appeared: move the key to the reachable
    /// test above and delete its ledger row here — consciously, with the producer named.
    /// </summary>
    [Theory]
    [InlineData("code_patch_full")]    // the documented full-suite escape hatch, v3.8.21
    [InlineData("config_change")]      // shadow operations' intended key; the executor is M2 work
    [InlineData("artifact_production")] // awaits a producer that types tasks as artifact work
    public void TheDormantKeys_AreRealPolicies_ThatNothingSelects(string key)
    {
        // Real: the table knows it, and its verifier list is not the unknown-type fallback.
        Assert.True(VerificationPolicy.IsKnown(key), $"{key} is no longer in the policy table — "
            + "delete its ledger row here in the same change.");
        Assert.NotEqual(new[] { "security_policy" }, VerificationPolicy.For(key));

        // Dormant: no production task type canonicalizes onto it.
        foreach (var t in ProductionTaskTypes)
            Assert.NotEqual(key, VerificationPolicy.Canonical(t));
    }

    /// <summary>The production-type list above is honest about the coder: its contract declares
    /// exactly the three types the reachable test uses, so the list cannot silently narrow.</summary>
    [Fact]
    public void TheCoderContract_DeclaresExactlyTheTypesThisLedgerAssumes()
    {
        var contract = AntExecutionCatalog.ContractFor("coder");
        Assert.NotNull(contract);
        Assert.Equal(
            new[] { "code_change", "patch", "patch_proposal" },
            contract!.SupportedTaskTypes.OrderBy(t => t, StringComparer.Ordinal));
    }
}
