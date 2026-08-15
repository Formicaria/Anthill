using Anthill.Core.Agents;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A declared scheduling mode is a PROMISE, and every promise has a keeper. v0.3.8.57.
///
/// `SchedulingMode` has been declared on every contract since v3.8.23 and has real consequences:
/// `RoleReadiness` reports it, the API exposes it as `scheduling_mode`, and a reader consults it to
/// answer "can this role be skipped by a plan that forgets it". So a mode that does not match the
/// runtime is not a documentation slip — it is the system stating a guarantee it does not keep.
///
/// THE ONE THIS FOUND. The verifier stayed `PlannerSelectable` — inherited by never being written
/// down — while `EnsureVerificationWaitsFor` and `EnsureVerificationAfterDeliverable` had guaranteed
/// its insertion since v0.3.8.41. Six releases of the table answering "yes, verification can be
/// skipped" when the runtime had made it "no".
///
/// The check below is the general form: every role claiming `PolicyInserted` must have somewhere that
/// inserts it. Otherwise the mode is the same defect in the other direction — a promise with no
/// keeper, which is exactly `RequiredInputArtifactTypes` and `EvidenceKinds.SchemaValid` again.
/// </summary>
public class PolicySchedulingTests
{
    /// <param name="InsertionMarker">
    /// A string that must appear in the runtime source for this role's insertion to be real. Named
    /// per role rather than searched for generically, because "something in ExecutionService mentions
    /// this role" is a much weaker claim than "this is the code that creates the task".
    /// </param>
    private sealed record PolicyRole(string Role, string InsertionMarker, string Trigger);

    private static readonly PolicyRole[] Inserted =
    {
        new("tester", "InsertPolicyReviewTasks",
            "a patch set exists, so there is something to run checks against"),

        new("soldier", "InsertPolicyReviewTasks",
            "a patch set exists, so there is proposed source to review"),

        new("verifier", "EnsureVerificationWaitsFor",
            "review evidence exists for a patch set, or a builder produced a deliverable"),
    };

    private static string ExecutionSource() => SourceText.CodeOnly(File.ReadAllText(
        Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));

    /// <summary>
    /// The ledger names every role the contracts declare PolicyInserted. A role that acquires the
    /// mode without an entry here is one whose insertion nobody checked.
    /// </summary>
    [Fact]
    public void EveryPolicyInsertedRole_IsInTheLedger()
    {
        var declared = AntExecutionCatalog.Contracts
            .Where(c => c.Value.Scheduling == SchedulingMode.PolicyInserted)
            .Select(c => c.Key)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        var known = Inserted.Select(i => i.Role).ToHashSet(StringComparer.Ordinal);
        var unaccounted = declared.Where(r => !known.Contains(r)).ToList();

        Assert.True(unaccounted.Count == 0,
            $"these roles declare PolicyInserted and this ledger does not name their insertion site: "
          + $"{string.Join(", ", unaccounted)}. A declared mode with no keeper is a guarantee the "
          + "runtime does not make.");
    }

    /// <summary>
    /// And each insertion site exists. This is what makes the declaration a fact rather than an
    /// intention — the failure it prevents is a role marked PolicyInserted that nothing inserts,
    /// which reads to every consumer as "the runtime guarantees this runs".
    /// </summary>
    [Fact]
    public void EveryPolicyInsertedRole_HasRealInsertionCode()
    {
        var source = ExecutionSource();

        foreach (var role in Inserted)
            Assert.True(source.Contains(role.InsertionMarker, StringComparison.Ordinal),
                $"{role.Role} declares PolicyInserted, triggered when {role.Trigger}, and "
              + $"'{role.InsertionMarker}' is no longer in ExecutionService. Either the insertion moved "
              + "— update this ledger — or it is gone, and the contract is now promising something "
              + "nothing delivers.");
    }

    /// <summary>
    /// The verifier specifically. This is the entry that was wrong, so it is asserted directly rather
    /// than only through the loop above.
    /// </summary>
    [Fact]
    public void TheVerifier_IsDeclaredPolicyInserted()
    {
        var verifier = AntExecutionCatalog.ContractFor("verifier");

        Assert.NotNull(verifier);
        Assert.Equal(SchedulingMode.PolicyInserted, verifier!.Scheduling);
    }

    /// <summary>
    /// PolicyInserted is a FLOOR, not a ceiling — a planned verifier is still admissible.
    ///
    /// This is the v0.3.8.51 field-report rule and it is easy to get backwards. A plan that asks for
    /// verification is asking for MORE checking; refusing it would throw away an operator's explicit
    /// step to enforce a guarantee that is already enforced. Only the two modes whose handlers can do
    /// nothing but refuse a planned invocation — the medic and the archivist — are blocked.
    /// </summary>
    [Fact]
    public void APlannedVerifier_IsStillAdmissible()
    {
        var planned = new Anthill.Core.Domain.Task
        {
            Id = "t1", Title = "Verify the change", Description = "check it",
            AssignedAnt = "verifier", TaskType = "verification",
            // No ParentTaskIds: this is the shape a PLANNER produces, which is the case under test.
        };

        Assert.True(AntRegistry.ValidateTask(planned, Anthill.Core.Common.MissionConstraints.None).Allowed);
    }

    /// <summary>
    /// Verification fails CLOSED when it cannot be inserted. The mode says the runtime guarantees the
    /// role runs; the honest completion of that promise is that a mission which could not insert one
    /// is demoted rather than quietly finishing unverified.
    /// </summary>
    [Fact]
    public void VerificationThatCannotBeInserted_BlocksTheDeliverable()
    {
        var source = ExecutionSource();

        Assert.Contains("verification_refused", source);
        Assert.Contains("DeterministicBlock ??= $\"verification could not be inserted", source);
    }
}
