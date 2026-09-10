using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Anthill.SDK.Artifacts;
using Anthill.SDK.Knowledge;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// KNOWLEDGE THE COLONY READ IS KNOWLEDGE THE ANSWER CAN BE HELD TO. v0.3.8.157.
///
/// The gap these close is the last one in a chain three releases long: `.121` granted the researcher
/// the knowledge tools, `.136` entered the scope so a dispatched call would resolve, `.156`
/// guaranteed a plan step that asks for a retrieval — and a knowledge-backed claim still rendered
/// `[UNSOURCED]`, because `CitationIntegrity` resolves against `source_set` records and nothing ever
/// wrote one for a knowledge item.
/// </summary>
public class KnowledgeCitationTests
{
    private const string ProjectRef = "forager-proj-1";

    private static KnowledgeFact Fact(string id, string statement, params string[] evidenceIds) => new()
    {
        KnowledgeId = id,
        Statement = statement,
        Support = KnowledgeSupport.DirectFact,
        Status = evidenceIds.Length > 0 ? KnowledgeStatus.Active : KnowledgeStatus.Unresolved,
        EvidenceIds = evidenceIds,
    };

    private static KnowledgeEvidence Evidence(string id, string knowledgeId) => new()
    {
        EvidenceId = id,
        KnowledgeId = knowledgeId,
        SourceId = "src_1",
        SourceName = "handbook.md",
        Excerpt = "deploys happen on Friday",
    };

    private static KnowledgeContext Context(params KnowledgeFact[] facts) => new()
    {
        Facts = facts,
        Evidence = facts.SelectMany(f => f.EvidenceIds.Select(e => Evidence(e, f.KnowledgeId))).ToList(),
        Metadata = new RetrievalMetadata
        {
            Query = "how do we deploy",
            Scope = KnowledgeScope.ForMission(ProjectRef, "mission-1", "proj-1"),
        },
    };

    /// <summary>
    /// THE ROUND TRIP ASSERTED AGAINST THE PRODUCER'S ACTUAL OUTPUT, which is the property
    /// `SourceSetPayload`'s own documentation says a fixture cannot give you: there, the tests
    /// passed because their fixtures were written the way the READER expected, while the producer
    /// wrote something else and every reader silently found nothing.
    /// </summary>
    [Fact]
    public void WhatTheRendererWrites_IsWhatTheParserReads()
    {
        var rendered = Context(Fact("ki_a", "Deploys happen on Friday", "ev_1")).Render();

        var citations = KnowledgeCitations.Read(rendered);

        var one = Assert.Single(citations);
        Assert.Equal(KnowledgeCitations.UrlFor(ProjectRef, "ki_a"), one.Url);
        Assert.Contains("Deploys happen on Friday", one.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// A STATEMENT WHOSE SUPPORTING TEXT COULD NOT BE FOUND IS NOT A SOURCE. `HasProvenance` is TRUE
    /// for an UNRESOLVED fact — Rule 9 says a fact carries evidence or is explicitly unresolved —
    /// so citing on that predicate would hand back, as something to rest on, the one statement the
    /// renderer tells the model not to rely on.
    /// </summary>
    [Fact]
    public void AnUnresolvedFact_IsNotCitable()
    {
        var context = Context(Fact("ki_a", "Deploys happen on Friday", "ev_1"),
                              Fact("ki_b", "The rota is owned by platform"));

        Assert.True(context.Facts.All(f => f.HasProvenance), "the fixture is not exercising the near-miss");

        var citable = context.Citable();

        Assert.Equal(new[] { KnowledgeCitations.UrlFor(ProjectRef, "ki_a") }, citable.Select(c => c.Url));
    }

    /// <summary>An empty context offers no block at all — a heading with no rows under it reads as
    /// retrieval that failed rather than as a knowledge base with nothing to say.</summary>
    [Fact]
    public void NothingCitable_RendersNoBlock()
    {
        var rendered = Context(Fact("ki_b", "Unsupported")).Render();

        Assert.DoesNotContain(KnowledgeCitations.Header, rendered, StringComparison.Ordinal);
        Assert.Empty(KnowledgeCitations.Read(rendered));
    }

    /// <summary>
    /// THE PROJECT REF IS PART OF THE IDENTITY. Two knowledge bases may both hold an item under ids
    /// their producer chose independently, so a citation that names only the item cannot be resolved
    /// back to the tenant it came from — and the tenant boundary is the whole of what the scope
    /// model protects.
    /// </summary>
    [Fact]
    public void ACitationNamesItsKnowledgeBase()
    {
        Assert.Equal("knowledge:forager-proj-1/ki_a", KnowledgeCitations.UrlFor(ProjectRef, "ki_a"));
        Assert.True(KnowledgeCitations.IsKnowledge("knowledge:forager-proj-1/ki_a"));
        Assert.False(KnowledgeCitations.IsKnowledge("https://example.com/a"));
        Assert.False(KnowledgeCitations.IsKnowledge("mission:abc"));
    }

    /// <summary>
    /// AND THE RECORD THE RESEARCHER WRITES IS ONE THE EXISTING GATE ALREADY RESOLVES. The schema is
    /// `source_set` rather than a new one precisely so that `CitationIntegrity` and the builder's
    /// citable list — which are two readers of one named set — need no change to see it.
    /// </summary>
    [Fact]
    public void TheResearchersRecord_ResolvesAsACitation()
    {
        var url = KnowledgeCitations.UrlFor(ProjectRef, "ki_a");
        var payload = Anthill.Core.Agents.ResearcherAnt.KnowledgeSourceSetPayload(
            "how do we deploy", new[] { new KnowledgeCitation(url, "Deploys happen on Friday") });

        // The producer's spelling, through the shared parser, exactly as a consumer will read it.
        Assert.Equal(new[] { url }, SourceSetPayload.Read(payload).Select(s => s.Url));

        var artifacts = new List<Artifact>
        {
            Artifact.Create(ArtifactSchemas.SourceSet, "researcher", "m1", payload),
        };

        Assert.Contains(url, Anthill.Core.Outcomes.CitationIntegrity.Retrieved(artifacts));
        // Resolvable, not merely retrieved: a knowledge url is a thing that was read, so unlike
        // `mission:` it needs no provenance walk to be something a claim may rest on.
        Assert.Contains(url, Anthill.Core.Outcomes.CitationIntegrity.Resolvable(artifacts, _ => null));
    }

    /// <summary>
    /// THE DISPATCH ITSELF, guarded at the source because the alternative is a colony-wide fixture:
    /// the researcher is deterministic, so "does this handler call the knowledge tool" is a question
    /// about the handler and not about a model's choices. Bounded by the member's own delimiters —
    /// `GuardHierarchyTests` refuses a character budget, and this is why that rule exists.
    /// </summary>
    [Fact]
    public void TheResearcher_DispatchesTheRetrieval()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Agents", "Ants.cs")));

        // ANCHORED ON THE CLASS FIRST, then on its handler, then bounded by the handler's own
        // braces: `Ants.cs` holds several roles and every one of them declares `Execute`, so a bare
        // search for the signature guards whichever class happens to come first in the file.
        var researcher = source.IndexOf("class ResearcherAnt", StringComparison.Ordinal);
        Assert.True(researcher > 0, "ResearcherAnt is gone; this guard reads nothing");

        var at = source.IndexOf("public override AntExecutionResult Execute(", researcher, StringComparison.Ordinal);
        Assert.True(at > researcher, "ResearcherAnt no longer declares Execute");

        var body = SourceText.MemberBody(source, at);
        Assert.Contains("KnowledgeScopeContext.HasScope", body, StringComparison.Ordinal);
        Assert.Contains("KnowledgeToolNames.Retrieve", body, StringComparison.Ordinal);
    }
}
