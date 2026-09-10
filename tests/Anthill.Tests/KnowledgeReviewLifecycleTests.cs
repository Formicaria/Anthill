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

    // ---- and the claim it must never make ---------------------------------------------------------

    /// <summary>
    /// THERE IS NO `applied`, AND THAT ABSENCE IS THE LOAD-BEARING PART.
    ///
    /// Accepting records that an OPERATOR agreed with an objection. It does not change a knowledge
    /// base and this build cannot: §1 gives FORAGER the classification, and the producer publishes
    /// no mutation for a review — that is P13. A status this build could never reach would be a
    /// promise living in an enum, which is the shape of claim this repository refuses everywhere
    /// else, and the console says the same thing beside the button rather than in a doc.
    /// </summary>
    [Fact]
    public void AcceptingIsAgreement_NeverApplication()
    {
        using var memory = Fresh();
        var proposal = Proposal();
        memory.SaveKnowledgeReview(proposal);

        var decided = memory.DecideKnowledgeReview(proposal.Id, true, "operator", null);

        Assert.Equal("accepted", decided!.Status);
        Assert.NotEqual("applied", decided.Status);

        var console = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.UI", "knowledge.js"));
        Assert.Contains("does not change the knowledge base", console, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AND THE CONTRACT NAMES THE MISSING PRODUCER SURFACE. An accepted review with nowhere to go is
    /// a gap in the integration, and this repository records those as numbered provisions rather than
    /// leaving them for someone to rediscover — the same argument P11 made for the project listing.
    /// </summary>
    [Fact]
    public void TheContract_NamesWhereAnAcceptedReviewWouldGo()
    {
        var contract = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "docs", "FORAGER_SHARED_CONTRACT.md"));

        Assert.Contains("| P13 |", contract, StringComparison.Ordinal);
        Assert.Contains("Applying a review decision", contract, StringComparison.Ordinal);
    }
}
