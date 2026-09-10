using Anthill.Core.Agents;
using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A KNOWLEDGE OBJECTION CAN BE RAISED, READ AND ANSWERED. v0.3.8.155.
///
/// `knowledge_review` shipped at `.121` and spent thirty-four releases registered, described, argued
/// for, and reachable by nobody: no role's contract named it, and the proposal it raises reached the
/// event log and stopped. Three layers of a feature with no first layer and no last one.
///
/// The failure is this repository's most-named defect wearing its largest coat — a declaration that
/// reaches nobody — so these guards hold both ends open rather than the middle.
/// </summary>
public class KnowledgeReviewLifecycleTests
{
    private static SqliteMemory Fresh() =>
        new(Path.Combine(Path.GetTempPath(), "anthill-review-" + Guid.NewGuid().ToString("N") + ".db"));

    private static SqliteMemory.KnowledgeReview Proposal(string id = "k1", string action = "reject") => new()
    {
        Id = Guid.NewGuid().ToString(),
        KnowledgeId = id,
        ProjectRef = "proj_a",
        Action = action,
        Rationale = "The cited source was superseded in 2024 and the statement contradicts it.",
        MissionId = "m1",
    };

    // ---- the first layer: something can raise one ------------------------------------------------

    /// <summary>
    /// THE ROLE THAT READS KNOWLEDGE MAY OBJECT TO IT, and until this release no role could.
    ///
    /// The researcher is the right one because an objection is worth having only from something that
    /// has just looked at the evidence. It PROPOSES and changes nothing, which is what makes the
    /// grant safe under §1: FORAGER classifies and ranks, ANTHILL records that an agent disagreed.
    /// </summary>
    [Fact]
    public void TheResearcher_MayRaiseAReviewProposal()
    {
        var contract = AntExecutionCatalog.Contracts["researcher"];

        Assert.Contains(Anthill.SDK.Knowledge.KnowledgeToolNames.Review, contract.AllowedTools);
    }

    /// <summary>
    /// AND NO ROLE THAT CANNOT SEE THE EVIDENCE MAY. The objection rests on having read something;
    /// a role with no knowledge tools proposing a knowledge correction would be an opinion with no
    /// basis, and the whole point of the required rationale is that there is one.
    /// </summary>
    [Theory]
    [InlineData("builder")]
    [InlineData("coder")]
    [InlineData("verifier")]
    public void ARoleThatCannotReadKnowledge_CannotObjectToIt(string role)
    {
        var contract = AntExecutionCatalog.Contracts[role];

        Assert.DoesNotContain(Anthill.SDK.Knowledge.KnowledgeToolNames.Review, contract.AllowedTools);
    }

    // ---- the last layer: somebody can answer it ---------------------------------------------------

    /// <summary>A raised proposal is pending, and findable as pending.</summary>
    [Fact]
    public void ARaisedProposal_IsWaitingRatherThanRecordedAsDone()
    {
        using var memory = Fresh();
        memory.SaveKnowledgeReview(Proposal());

        var pending = memory.KnowledgeReviews("pending");
        Assert.Single(pending);
        Assert.Equal("reject", pending[0].Action);
        Assert.Null(pending[0].DecidedAt);
    }

    /// <summary>Accepting and declining both record WHO decided, which is the point of a record.</summary>
    [Theory]
    [InlineData(true, "accepted")]
    [InlineData(false, "declined")]
    public void ADecisionRecordsItsAuthor(bool accept, string expected)
    {
        using var memory = Fresh();
        var proposal = Proposal();
        memory.SaveKnowledgeReview(proposal);

        var decided = memory.DecideKnowledgeReview(proposal.Id, accept, "operator", "checked the source");

        Assert.NotNull(decided);
        Assert.Equal(expected, decided!.Status);
        Assert.Equal("operator", decided.DecidedBy);
        Assert.NotNull(decided.DecidedAt);
        Assert.Empty(memory.KnowledgeReviews("pending"));
    }

    /// <summary>
    /// A SECOND DECISION IS REFUSED. It is not an update: the record already says what happened, and
    /// letting it be overwritten would mean the last person to click decides what the first one did.
    /// </summary>
    [Fact]
    public void AProposalIsDecidedOnce()
    {
        using var memory = Fresh();
        var proposal = Proposal();
        memory.SaveKnowledgeReview(proposal);

        Assert.NotNull(memory.DecideKnowledgeReview(proposal.Id, true, "operator", null));
        Assert.Null(memory.DecideKnowledgeReview(proposal.Id, false, "someone-else", null));
        Assert.Equal("accepted", memory.KnowledgeReviewById(proposal.Id)!.Status);
    }

    /// <summary>An unknown id is refused rather than silently creating something.</summary>
    [Fact]
    public void AnUnknownProposal_IsRefused() =>
        Assert.Null(Fresh().DecideKnowledgeReview("no-such-id", true, "operator", null));

    // ---- accepting and applying are two acts ------------------------------------------------------

    /// <summary>
    /// ACCEPTING IS AGREEMENT. APPLYING IS THE CHANGE. v0.3.9.2 — and this test used to assert the
    /// OPPOSITE, which is worth keeping visible rather than quietly rewriting.
    ///
    /// `.155` wrote: "there is no `applied`, and that absence is the load-bearing part… the producer
    /// publishes no mutation for a review — that is P13. A status this build could never reach would
    /// be a promise living in an enum." Every word of that was true against FORAGER 0.1.4, and this
    /// test held the console to saying so.
    ///
    /// FORAGER 0.6 publishes `POST /api/knowledge/:id/review`. So the absence stopped being
    /// load-bearing and became stale, and what is asserted flips with it: accepting still does not
    /// apply, and `applied` is now a state a proposal reaches by a SECOND, separate act.
    /// </summary>
    [Fact]
    public void AcceptingIsAgreement_ApplyingIsASecondAct()
    {
        using var memory = Fresh();
        var proposal = Proposal();
        memory.SaveKnowledgeReview(proposal);

        var decided = memory.DecideKnowledgeReview(proposal.Id, true, "operator", null);
        Assert.Equal("accepted", decided!.Status);
        Assert.Null(decided.AppliedAt);

        var applied = memory.MarkKnowledgeReviewApplied(proposal.Id, null);
        Assert.Equal("applied", applied!.Status);
        Assert.NotNull(applied.AppliedAt);

        // ONCE. A second apply is not an update — the record already says the change went.
        Assert.Null(memory.MarkKnowledgeReviewApplied(proposal.Id, null));
    }

    /// <summary>
    /// AND A PROPOSAL NOBODY AGREED WITH CANNOT BE APPLIED. Applying is what happens after an
    /// operator accepts, never instead of it: the mutation lane's whole justification is that a
    /// person decided.
    /// </summary>
    [Fact]
    public void APendingProposal_CannotBeApplied()
    {
        using var memory = Fresh();
        var proposal = Proposal();
        memory.SaveKnowledgeReview(proposal);

        Assert.Null(memory.MarkKnowledgeReviewApplied(proposal.Id, null));

        memory.DecideKnowledgeReview(proposal.Id, false, "operator", "not convinced");
        Assert.Null(memory.MarkKnowledgeReviewApplied(proposal.Id, null));
    }

    /// <summary>
    /// AND THE CONSOLE OFFERS THE SECOND ACT. The page said, beside the button, that accepting could
    /// not change the knowledge base — correct then, misleading now.
    /// </summary>
    [Fact]
    public void TheConsole_OffersApply()
    {
        var console = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.UI", "knowledge.js"));

        Assert.Contains("knApplyReview", console, StringComparison.Ordinal);
        Assert.Contains("/apply", console, StringComparison.Ordinal);
        Assert.DoesNotContain("does not change the knowledge base", console, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AND THE CONTRACT RECORDS THAT THE PRODUCER DELIVERED IT. P13 stays in the table — a provision
    /// that vanishes when it lands leaves no record that it was ever the thing standing in the way.
    /// </summary>
    [Fact]
    public void TheContract_RecordsP13AsDelivered()
    {
        var contract = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "docs", "FORAGER_SHARED_CONTRACT.md"));

        Assert.Contains("| P13 |", contract, StringComparison.Ordinal);
        Assert.Contains("DELIVERED", contract, StringComparison.Ordinal);
        Assert.Contains("/api/knowledge/:id/review", contract, StringComparison.Ordinal);
    }
}
