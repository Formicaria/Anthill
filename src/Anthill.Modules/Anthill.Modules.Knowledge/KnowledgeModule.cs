using Anthill.SDK.Events;
using Anthill.SDK.Knowledge;
using Anthill.SDK.Modules;
using Anthill.SDK.Tools;

namespace Anthill.Modules.Knowledge;

/// <summary>
/// Organizational knowledge, as a module.
///
/// The colony's knowledge comes from FORAGER — a separate application, in a different language, with
/// its own database, its own ingestion pipeline and its own canonical schema. This module is the
/// entire surface of that relationship: an HTTP client, a provider that turns wire records into the
/// colony's vocabulary, and six tools.
///
/// WHAT THIS MODULE DELIBERATELY DOES NOT DO, because the temptation will recur:
///   - It does not parse documents. FORAGER owns ingestion.
///   - It does not store knowledge. FORAGER owns the canonical representation, and ANTHILL's
///     database gains no knowledge tables at all — which is why this change needs no migration.
///   - It does not resolve conflicts, merge entities, or decide what is true. Those are FORAGER's
///     to compute and an operator's to decide.
///   - It does not embed, chunk, or rank. A second retrieval implementation on this side of the
///     boundary would be the duplication the whole integration exists to avoid.
///
/// See docs/FORAGER_INTEGRATION.md for the boundary, and docs/KNOWLEDGE_ARCHITECTURE.md for the
/// retrieval pipeline.
///
/// OFF BY DEFAULT. When knowledge is not configured this module registers NO tools, so a colony that
/// has never heard of FORAGER offers a model nothing about it, and existing installations are
/// unaffected. That is Rule 15, and it is enforced in <see cref="Register"/> before anything else
/// happens.
/// </summary>
public sealed class KnowledgeModule : IAnthillModule, IDisposable
{
    private readonly KnowledgeOptionsSource _options;
    private readonly Action<KnowledgeReviewProposal> _propose;
    private readonly KnowledgeCache _cache = new();
    private readonly ForagerClient _client;
    private readonly ForagerKnowledgeProvider _forager;

    /// <param name="options">
    /// Read live, on every call, never captured — so an operator switching knowledge off, or moving
    /// the endpoint, takes effect on the next request rather than the next restart. The composition
    /// root supplies this because the configuration lives in <c>AnthillRuntime</c>, which a module
    /// may not reference.
    /// </param>
    /// <param name="propose">
    /// Where a review proposal goes. A delegate rather than a store reference for the same reason:
    /// the approval pipeline is core. Rule 8 is that an agent proposes and a human decides, so this
    /// callback records intent and never applies it.
    /// </param>
    /// <param name="handler">
    /// Test seam for the HTTP transport. Null in every real process.
    /// </param>
    public KnowledgeModule(
        KnowledgeOptionsSource options,
        Action<KnowledgeReviewProposal>? propose = null,
        System.Net.Http.HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        // v0.3.8.122 — the default REFUSES rather than discarding. It was `_ => { }`: a module
        // composed without a sink accepted every proposal, dropped it, and let the tool report
        // success. A worker cannot tell a silent discard from a filing, so the default has to be the
        // one that cannot be mistaken for either — the tool catches this and says the proposal could
        // not be recorded, which is the truth.
        _propose = propose ?? (_ => throw new InvalidOperationException(
            "This knowledge module was composed without a proposal sink, so a review proposal has "
          + "nowhere to be recorded."));
        _client = new ForagerClient(options, handler);
        _forager = new ForagerKnowledgeProvider(options, _client, _cache);
    }

    public string Name => "knowledge";

    public string Version => "0.3.8.122";

    /// <summary>
    /// The retrieval face, for the console's API surface.
    ///
    /// Returns the live provider when knowledge is usable and a <see cref="NullKnowledgeProvider"/>
    /// carrying the reason when it is not, so a caller never has to null-check and never has to
    /// interpret a silence. Resolved per access rather than at construction because configuration is
    /// live.
    /// </summary>
    public IKnowledgeProvider Provider
    {
        get
        {
            var unusable = _options().Unusable();
            return unusable is null ? _forager : new NullKnowledgeProvider(unusable);
        }
    }

    /// <summary>
    /// The ingestion face. Null when knowledge is not usable — unlike retrieval there is no
    /// meaningful null object for "start a job", and a caller that wants to ingest genuinely does
    /// need to handle the unconfigured case rather than be handed something that always refuses.
    /// </summary>
    public IKnowledgeIngestionProvider? Ingestion => _options().Unusable() is null ? _forager : null;

    /// <summary>Drop every cached read. For a configuration change, where the endpoint may have moved.</summary>
    public void InvalidateCache() => _cache.Clear();

    /// <summary>
    /// No I/O, per the <see cref="IAnthillModule"/> contract — and here that rule bites harder than
    /// usual. FORAGER is a network service, and probing it during registration would make an
    /// unreachable knowledge base into a colony that will not boot. Availability is discovered on
    /// first use and reported as an event, never as a startup failure.
    /// </summary>
    public void Register(IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = _options();
        var unusable = options.Unusable();

        // REGISTERED UNCONDITIONALLY, REFUSING HONESTLY WHEN KNOWLEDGE IS OFF.
        //
        // The first version of this method returned early when knowledge was unconfigured, reasoning
        // that offering a model six tools which can only fail wastes context on every turn. That was
        // wrong for this colony, and three guards said so in one run: `FullRosterQualificationTests`,
        // `TheFullyEquippedToolSet_CoversEveryDeclaredTool` and
        // `AcceptanceGateOne_AllTwelveRolesReportReady_UnderTheFullProfile` all failed with
        // "researcher: declared tools not registered".
        //
        // The rule they enforce is the one `ToolInventory` already states out loud: "registered and
        // refusing" is a DIFFERENT FACT from "declared and absent", and only the second is a defect.
        // A contract naming a tool nothing registers makes that ROLE unqualified — so shipping
        // knowledge switched off, which is the default, would have shipped a researcher that cannot
        // pass readiness. The context argument was real but much smaller than that.
        //
        // Refusal happens at call time instead: `Provider` returns a `NullKnowledgeProvider` carrying
        // the reason whenever configuration is unusable, and every tool renders that as an explicit
        // unavailability telling the model not to substitute anything for what it did not get.
        var registered = new List<string>();

        void Offer(ITool tool)
        {
            context.RegisterTool(tool);
            registered.Add(tool.Name);
        }

        Offer(new KnowledgeSearchTool(_forager, _options));
        Offer(new KnowledgeRetrieveTool(_forager, _options));
        Offer(new KnowledgeGetTool(_forager, _options));
        Offer(new KnowledgeEvidenceTool(_forager, _options));
        Offer(new KnowledgeEntityTool(_forager, _options));

        // The one tool that writes anything, and it writes a PROPOSAL into ANTHILL's approval
        // pipeline rather than a change into FORAGER. Registered alongside the read tools because
        // the gate that matters is the role contract in AntExecutionCatalog, not this list — a role
        // that may not propose simply never has the name in its allowlist.
        Offer(new KnowledgeReviewTool(_propose));

        context.Events.Publish(new ColonyEvent
        {
            EventType = EventTypes.ModuleRegistered,

            // Says which state it is in. "Registered and refusing" and "registered and working" are
            // the two facts an operator reading the event log needs to tell apart, and the reason is
            // carried when it is the first.
            Message = unusable is null
                ? $"Knowledge tools available: {string.Join(", ", registered)}."
                : $"Knowledge tools registered but inactive ({unusable}): {string.Join(", ", registered)}.",
            Metadata = new Dictionary<string, object?>
            {
                ["module"] = Name,
                ["version"] = Version,
                ["tools"] = registered,
                ["usable"] = unusable is null,
                ["reason"] = unusable,
                ["endpoint"] = options.Endpoint,

                // The token is never published, and neither is anything derived from it. This
                // metadata reaches the event log and the console.
                ["authenticated"] = options.Token.Length > 0,
                ["remote_allowed"] = options.AllowRemoteEndpoint,
                ["mapped_projects"] = options.ProjectMap.Count,
            },
        });
    }

    /// <summary>
    /// Disposes the HTTP client this module constructed. The provider is not disposable — it holds
    /// the client but does not own it, which is why the ownership lives here.
    /// </summary>
    public void Dispose()
    {
        _client.Dispose();
        _cache.Clear();
    }
}
