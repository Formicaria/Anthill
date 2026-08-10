using Anthill.SDK.Contracts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The plan's failure taxonomy, reconciled against the one the code actually has. v0.3.8.37.
///
/// `AUTONOMY-10.md` Phase 1 item 5 asks for one canonical taxonomy across providers, tools, ants,
/// scheduler, evidence, memory, UI and telemetry, and lists thirteen minimum classes. The code has
/// `FailureClass` — twelve real members plus `None` — reached through exactly one string converter
/// since v3.8.32.
///
/// The honest position, and the reason this is a reconciliation rather than a rename: **the code's
/// taxonomy is already canonical and already enforced.** What it is not is IDENTICAL to the plan's
/// list. Renaming twelve enum members to match a document would churn every switch, every persisted
/// row and every wire value in exchange for vocabulary, and this repository has spent four releases
/// learning what it costs when a stored string changes shape.
///
/// So the mapping is written down, and the three genuine gaps are named rather than papered over.
/// A gap recorded is a decision; a gap unrecorded is the thing that shipped nine times.
/// </summary>
public class FailureTaxonomyTests
{
    /// <summary>
    /// The plan's thirteen, mapped to what the runtime actually classifies with.
    ///
    /// `null` means the plan names a distinction the code does not draw — a real gap, listed so it
    /// can be argued about instead of discovered.
    /// </summary>
    private static readonly Dictionary<string, FailureClass?> PlanToCode = new(StringComparer.Ordinal)
    {
        ["transient_provider"] = FailureClass.TransientProviderFailure,
        ["timeout"] = FailureClass.Timeout,
        ["dependency_failure"] = FailureClass.DependencyFailure,
        ["verification_failure"] = FailureClass.VerificationFailure,
        ["internal_runtime_failure"] = FailureClass.InternalDefect,
        ["policy_denial"] = FailureClass.AuthorizationFailure,
        ["invalid_artifact"] = FailureClass.ValidationFailure,
        ["patch_conflict"] = FailureClass.TargetRejection,
        ["security_failure"] = FailureClass.UnsafeState,

        // --- GAPS. The plan draws a distinction the code does not. ------------------------------
        //
        // `permanent_provider` — the code has TransientProviderFailure and nothing for a provider
        // that will never answer (a deleted model, a revoked key). Today both look retryable to
        // FailureClassify, so the colony burns its whole attempt budget on a permanent condition.
        ["permanent_provider"] = null,

        // `tool_failure` — tools classify into the general set (ValidationFailure, Timeout,
        // TransientProviderFailure...). That is arguably right: what failed matters more than that a
        // tool was the thing failing. Recorded so the choice is visible.
        ["tool_failure"] = null,

        // `cancellation` — an operator stop is currently a task STATUS (Cancelled) rather than a
        // failure class, so a cancelled task carries no class at all. Attribution already treats it
        // as neutral, so nothing is mis-blamed; the gap is that reporting cannot count them.
        ["cancellation"] = null,

        // `test_failure` — found while writing this file, by counting. The plan lists thirteen and
        // the first draft of this map had twelve.
        //
        // It cannot map: `TesterAnt` emits VerificationFailure for a failed check, and the verifier
        // emits VerificationFailure for a failed verification. Two different events, one class, so
        // nothing downstream can tell "the tests went red" from "the evidence did not support the
        // claim" — and those want different responses. The medic is for the first; the second is a
        // mission that should not promote.
        //
        // Mapping it onto VerificationFailure anyway would make this map non-injective and hide the
        // collapse behind a tick.
        ["test_failure"] = null,
    };

    /// <summary>Every one of the plan's thirteen classes has a decision recorded against it.</summary>
    [Fact]
    public void EveryPlanFailureClass_IsEitherMappedOrRecordedAsAGap()
    {
        // THIRTEEN, as the plan states. Pinned by count because the first draft of this map had
        // twelve and the missing one — test_failure — was only found by counting against the
        // document. A map that silently omits a class looks complete and is not.
        Assert.Equal(13, PlanToCode.Count);

        var undecided = PlanToCode.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList();

        // Gaps are allowed; SILENT gaps are not. Four are expected and each is argued above.
        Assert.Equal(new[] { "cancellation", "permanent_provider", "test_failure", "tool_failure" },
            undecided.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Every mapped target is a real enum member, and no two plan classes collapse onto one code
    /// class — a collapse would mean the runtime cannot tell them apart, which is the same as not
    /// having the distinction at all.
    /// </summary>
    [Fact]
    public void TheMappingIsInjective_AndPointsAtRealClasses()
    {
        var mapped = PlanToCode.Where(kv => kv.Value is not null).Select(kv => kv.Value!.Value).ToList();

        Assert.All(mapped, c => Assert.True(Enum.IsDefined(c), $"{c} is not a FailureClass member"));
        Assert.Equal(mapped.Count, mapped.Distinct().Count());
    }

    /// <summary>
    /// The retry set still matches the taxonomy's meaning. `permanent_provider` being unmapped is
    /// exactly why this matters: everything provider-shaped that the code CAN express is retryable,
    /// so a permanent provider condition consumes the whole attempt budget before it terminates.
    /// </summary>
    [Fact]
    public void TheRetrySet_IsTransientConditionsOnly()
    {
        foreach (var retryable in new[]
                 {
                     FailureClass.TransientProviderFailure, FailureClass.RateLimit,
                     FailureClass.Timeout, FailureClass.Conflict,
                 })
            Assert.True(FailureClassify.IsRetryable(retryable), $"{retryable} should be retryable");

        // Retrying these cannot change the answer, so they must terminate immediately.
        foreach (var terminal in new[]
                 {
                     FailureClass.ValidationFailure, FailureClass.AuthorizationFailure,
                     FailureClass.TargetRejection, FailureClass.VerificationFailure,
                     FailureClass.UnsafeState, FailureClass.InternalDefect,
                 })
            Assert.False(FailureClassify.IsRetryable(terminal), $"{terminal} must not be retryable");
    }

    /// <summary>
    /// Every code class round-trips through the one converter. This is the property that actually
    /// makes the taxonomy canonical, and the one whose absence charged six releases of provider
    /// outages to the wrong ant.
    /// </summary>
    [Fact]
    public void EveryCodeClass_HasExactlyOneWireForm()
    {
        var wire = Enum.GetValues<FailureClass>().Select(FailureClassNames.Wire).ToList();

        Assert.Equal(wire.Count, wire.Distinct(StringComparer.Ordinal).Count());
        Assert.All(Enum.GetValues<FailureClass>(), c =>
        {
            Assert.True(FailureClassNames.TryParse(FailureClassNames.Wire(c), out var back));
            Assert.Equal(c, back);
        });
    }
}
