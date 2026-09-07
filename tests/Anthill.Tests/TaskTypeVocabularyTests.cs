using Anthill.Core.Agents;
using Anthill.Core.Planning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A SYNONYM IS NOT A BROKEN MISSION. v0.3.8.133.
///
/// THE OUTAGE. "how to make tacos". The researcher ran for 22 seconds and produced a brief; the
/// verifier then refused its own task — `task type 'verify' is outside the verifier execution
/// contract (v1)` — the medic diagnosed it, the builder handed back to the medic, and the mission
/// ended `failed` / `escalated` having answered nothing. The verifier declares `verification`. The
/// plan said `verify`. The two were never compared until dispatch, by which point the work was
/// spent and the only thing left to do with the mismatch was fail on it.
///
/// It stayed invisible because a larger planner model writes the declared spelling most of the
/// time. A small local one — the configuration this colony ships for — writes the English word,
/// and every mission it plans dies at its last step with nothing saying the planner and the
/// contract disagree about vocabulary.
///
/// WHAT IS PINNED HERE is the shape rather than the table: that the contract stays the authority,
/// that an alias is only honoured when the ROLE declares its target, and — the one that matters
/// most — that an unresolvable type is still returned unchanged so the dispatch gate can refuse it.
/// A reconciliation that always succeeds would have turned this outage into a silently wrong task.
/// </summary>
public class TaskTypeVocabularyTests
{
    /// <summary>The exact defect: the word a planner writes becomes the word the verifier declares.</summary>
    [Fact]
    public void TheWordThatTookAMissionDown_BecomesTheDeclaredOne()
    {
        Assert.Equal("verification",
            TaskTypeVocabulary.Reconcile("verifier", "verify"));
    }

    /// <summary>A type the contract already declares is returned untouched — the overwhelming case.</summary>
    [Theory]
    [InlineData("verifier", "verification")]
    [InlineData("builder", "build_answer")]
    [InlineData("researcher", "research")]
    [InlineData("file", "file_inspection")]
    [InlineData("tester", "test_execution")]
    [InlineData("coder", "patch_proposal")]
    public void ADeclaredType_IsLeftAlone(string role, string type)
    {
        Assert.Equal(type, TaskTypeVocabulary.Reconcile(role, type));
    }

    /// <summary>
    /// AN ALIAS IS ONLY HONOURED WHERE THE ROLE DECLARES ITS TARGET. `verify` means `verification`
    /// to a verifier and nothing at all to a builder — the builder declares four types and none of
    /// them is a verification, so there is nothing to correct it TO. Accepting the alias by name
    /// would route a verification task to the role that writes answers.
    /// </summary>
    [Fact]
    public void AnAlias_IsRefusedForARoleThatDoesNotDeclareItsTarget()
    {
        Assert.NotEqual("verification",
            TaskTypeVocabulary.Reconcile("builder", "verify"));
    }

    /// <summary>
    /// A ROLE WITH EXACTLY ONE DECLARED TYPE HAS NOTHING TO CHOOSE. The verifier and the file ant
    /// each declare one, so any unrecognisable spelling resolves to it — this is not a guess, it is
    /// the only thing the role can execute.
    /// </summary>
    [Theory]
    [InlineData("verifier", "verification")]
    [InlineData("file", "file_inspection")]
    public void ARoleWithOneDeclaredType_ResolvesAnythingToIt(string role, string only)
    {
        Assert.Equal(only, TaskTypeVocabulary.Reconcile(role, "wildly_unrecognisable_type"));
    }

    /// <summary>
    /// AND WHERE NOTHING CAN HONESTLY DECIDE, THE GATE STILL REFUSES. This is the assertion that
    /// keeps the fix from becoming a worse defect: a type no role can execute must reach the
    /// dispatch chokepoint and be refused there, loudly, rather than being defaulted into a task
    /// the colony then runs and reports as if it were asked for.
    /// </summary>
    [Fact]
    public void AnUnresolvableType_IsReturnedUnchanged_SoDispatchStillRefusesIt()
    {
        // The tester declares eight types, so there is no single answer to fall back to, and
        // "sculpt_a_horse" is neither declared nor aliased. THIS TEST ALREADY EARNED ITS KEEP: the
        // first cut of `Reconcile` also inferred from the role, `InferTaskType` answers for every
        // role, and this assertion is what caught the refusal becoming unreachable.
        var reconciled = TaskTypeVocabulary.Reconcile("tester", "sculpt_a_horse");

        Assert.Equal("sculpt_a_horse", reconciled);
        Assert.False(AntExecutionCatalog.ContractFor("tester")!.SupportsTaskType(reconciled));
    }

    /// <summary>A role with no contract at all is not this type's business, and it says so by doing nothing.</summary>
    [Fact]
    public void ARoleWithNoContract_IsLeftEntirelyAlone()
    {
        Assert.Equal("anything", TaskTypeVocabulary.Reconcile("not_a_role", "Anything"));
    }

    /// <summary>Case and whitespace are the planner's, not the contract's.</summary>
    [Fact]
    public void SpellingIsNormalisedBeforeAnythingElseIsTried()
    {
        Assert.Equal("verification", TaskTypeVocabulary.Reconcile("verifier", "  VERIFY  "));
    }

    /// <summary>
    /// THE CONTRACT REMAINS THE AUTHORITY, and this is the guard against the table drifting away
    /// from it: every alias target must be a type SOME role actually declares. An alias pointing at
    /// a spelling no contract knows would be a rewrite that guarantees the refusal it exists to
    /// prevent — silently, and only for the missions unlucky enough to use that word.
    /// </summary>
    [Fact]
    public void EveryAliasTarget_IsATypeSomeContractDeclares()
    {
        var declared = AntExecutionCatalog.Contracts.Values
            .SelectMany(c => c.SupportedTaskTypes)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(declared);

        foreach (var role in new[] { "verifier", "tester", "builder", "researcher", "file", "coder", "medic", "scribe" })
        foreach (var word in new[] { "verify", "validate", "test", "build", "summarize", "investigate",
                                     "inspect", "implement", "diagnose", "document", "analyze" })
        {
            var result = TaskTypeVocabulary.Reconcile(role, word);
            if (!string.Equals(result, word, StringComparison.OrdinalIgnoreCase))
                Assert.Contains(result, declared);
        }
    }
}
