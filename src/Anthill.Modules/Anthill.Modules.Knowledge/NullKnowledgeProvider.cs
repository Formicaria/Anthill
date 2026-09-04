using Anthill.SDK.Knowledge;

namespace Anthill.Modules.Knowledge;

/// <summary>
/// What answers when knowledge is switched off or unconfigured.
///
/// NOT A STUB, and not a test double. This class is the mechanism by which Rule 15 holds: an
/// existing colony with no FORAGER configured keeps working, and every knowledge call gets a typed
/// <see cref="KnowledgeFailure.Disabled"/> with a sentence explaining it — rather than an empty
/// result, a null, or an exception.
///
/// THE DIFFERENCE THAT MATTERS: "the knowledge base was searched and had nothing" and "there is no
/// knowledge base" are different facts, and a model that confuses them will answer confidently from
/// its own priors in the second case. An empty success would invite exactly that. So this returns a
/// failure, the tools render it as an explicit unavailability, and the unavailability text tells the
/// model not to substitute anything for what it did not get.
///
/// In practice the module does not even register the knowledge tools when knowledge is disabled, so
/// most callers never reach this. It exists for the paths that hold a provider reference regardless
/// — the console's availability endpoint, and any future core consumer — so that "off" is a
/// behaviour rather than a null check every caller has to remember.
/// </summary>
internal sealed class NullKnowledgeProvider : IKnowledgeProvider
{
    private readonly string _reason;

    public NullKnowledgeProvider(string reason) => _reason = reason;

    public string Name => "none";

    public Task<KnowledgeAvailability> ProbeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(KnowledgeAvailability.Off(_reason));

    public Task<KnowledgeOutcome<KnowledgeSearchResult>> SearchAsync(
        KnowledgeSearchRequest request, CancellationToken cancellationToken) => Off<KnowledgeSearchResult>();

    public Task<KnowledgeOutcome<KnowledgeContext>> RetrieveAsync(
        KnowledgeRetrievalRequest request, CancellationToken cancellationToken) => Off<KnowledgeContext>();

    public Task<KnowledgeOutcome<KnowledgeFact>> GetAsync(
        string knowledgeId, KnowledgeScope scope, CancellationToken cancellationToken) => Off<KnowledgeFact>();

    public Task<KnowledgeOutcome<IReadOnlyList<KnowledgeEvidence>>> GetEvidenceAsync(
        string knowledgeId, KnowledgeScope scope, CancellationToken cancellationToken)
        => Off<IReadOnlyList<KnowledgeEvidence>>();

    public Task<KnowledgeOutcome<IReadOnlyList<KnowledgeEntity>>> GetRelatedEntitiesAsync(
        string knowledgeId, KnowledgeScope scope, CancellationToken cancellationToken)
        => Off<IReadOnlyList<KnowledgeEntity>>();

    public Task<KnowledgeOutcome<IReadOnlyList<KnowledgeEntity>>> FindEntitiesAsync(
        string name, KnowledgeScope scope, CancellationToken cancellationToken)
        => Off<IReadOnlyList<KnowledgeEntity>>();

    public Task<KnowledgeOutcome<IReadOnlyList<KnowledgeConflict>>> GetConflictsAsync(
        KnowledgeScope scope, CancellationToken cancellationToken) => Off<IReadOnlyList<KnowledgeConflict>>();

    private Task<KnowledgeOutcome<T>> Off<T>() where T : class =>
        Task.FromResult(KnowledgeOutcome<T>.Failed(KnowledgeFailure.Disabled, _reason));
}
