using System.Text;
using System.Text.Json;
using Anthill.Core.Configuration;
using Anthill.Core.Memory;

namespace Anthill.Core.Models;

/// <summary>
/// v3.8.4 — the old name for <see cref="IReasoningProvider"/>, kept so nothing breaks in the move.
///
/// The interface itself now lives in <c>Anthill.SDK.Reasoning</c>, because a provider contract that
/// lives in the core is a core that cannot be built without providers. This declares NO members of
/// its own: an implementation of <c>IModelClient</c> is an implementation of
/// <c>IReasoningProvider</c> and vice versa, so existing implementers and existing consumers both
/// keep compiling unchanged.
///
/// Deliberately not marked <c>[Obsolete]</c> yet. Doing so in the same release that moves the type
/// would fill the build with warnings about a rename nothing has had a chance to react to; the
/// attribute goes on when the last in-tree caller has migrated, and the alias goes away a release
/// after that.
/// </summary>
public interface IModelClient : IReasoningProvider
{
}

/// <summary>
/// Role-based model routing. Resolves a provider/model per role, caches clients, records
/// each call as an event, and reinforces or decays the model-route pheromone trail by outcome.
/// Faithful to the Python <c>ModelRouter</c>, including the softened generic-failure penalty.
/// </summary>
public sealed class ModelRouter
{
    private readonly SqliteMemory? _memory;
    private readonly Dictionary<string, IModelClient> _clients = new();
    private readonly object _lock = new();
    private readonly ModelCircuitBreaker? _breaker;
    public int CallCount { get; private set; }

    /// <summary>
    /// Model calls per task, read and cleared when the execution record is persisted. v3.8.31.
    ///
    /// Bounded the same way the tool counter is: entries are removed on read, so a task that never
    /// reaches persistence leaks one small counter rather than growing a dictionary keyed by every
    /// task the process has ever seen.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _taskModelCalls = new();

    /// <summary>Read and CLEAR the model-call count for a task. Called once, at persistence.</summary>
    public int TakeModelCallCount(string? taskId) =>
        taskId is not null && _taskModelCalls.TryRemove(taskId, out var n) ? n : 0;

    /// <summary>
    /// How the router discovers what a local host has installed, when no model is configured.
    ///
    /// Injected rather than performed here, because <c>Anthill.Core</c> does not make HTTP calls to
    /// providers — that is what the module boundary is for (ADR-007). The composition root supplies
    /// one that asks Ollama; the default answers "cannot ask", which resolves to a refusal that says
    /// so rather than to a guessed model.
    /// </summary>
    private readonly LocalModelResolver.ModelLister _modelLister;

    /// <summary>
    /// Default lister: whatever the composition root registered, or a throw meaning "cannot ask".
    /// <see cref="LocalModelResolver"/> turns the throw into a refusal that names the host, which is
    /// a different fix from "the host has no models" and must stay distinguishable from it.
    /// </summary>
    private static IReadOnlyList<string> DiscoverViaRegisteredLister(string host) =>
        ReasoningProviders.ListLocalModels(host);

    /// <param name="breaker">Test seam. When null a default breaker is built from
    /// <see cref="AnthillRuntime"/> (or none, if the feature is disabled).</param>
    /// <param name="modelLister">Lists installed models for a local host. Test seam and composition
    /// point; see <see cref="_modelLister"/>.</param>
    public ModelRouter(SqliteMemory? memory = null, ModelCircuitBreaker? breaker = null,
        LocalModelResolver.ModelLister? modelLister = null)
    {
        _memory = memory;
        _modelLister = modelLister ?? DiscoverViaRegisteredLister;
        _breaker = breaker ?? (AnthillRuntime.EnableModelCircuitBreaker
            ? new ModelCircuitBreaker(AnthillRuntime.ModelCircuitFailureThreshold, AnthillRuntime.ModelCircuitCooldownSeconds)
            : null);
    }

    /// <summary>
    /// Where a role's model calls go.
    ///
    /// v3.8.1 puts one step in front: when an operator has named a PRIORITY model, every role tries
    /// it first, whatever its own route says. That is the point of the setting — "I have a better
    /// model, use it everywhere" is one decision, and expressing it by rewriting fourteen per-role
    /// routes is how half of them end up stale.
    ///
    /// The role's own route is not discarded, only outranked: it stays the first thing tried when
    /// the priority route is unhealthy. A deliberate per-ant specialisation — a bigger context
    /// window, a tool-calling model for an ant that needs one — survives the priority being switched
    /// on and returns intact when it is switched off.
    /// </summary>
    public (string Provider, string Model) GetRoute(string role) =>
        AnthillRuntime.HasModelPriority
            ? (AnthillRuntime.ModelPriorityProvider, AnthillRuntime.ModelPriorityModel)
            : RoleRoute(role);

    /// <summary>
    /// A route with its model actually resolved, or the reason it could not be. v0.3.8.41.
    ///
    /// <paramref name="Resolved"/> false means a call to this route would REFUSE — it is not a
    /// degraded answer to be displayed as if it were a model. A reader showing an unresolved route
    /// must show <paramref name="Reason"/>, which names the remedy.
    /// </summary>
    public sealed record ResolvedRoute(string Provider, string Model, bool Resolved, string Reason)
    {
        public static ResolvedRoute Ok(string provider, string model) => new(provider, model, true, "");
    }

    /// <summary>The route configured for this role specifically, ignoring any priority override.</summary>
    public (string Provider, string Model) RoleRoute(string role)
    {
        var route = AnthillRuntime.ModelRouting.GetValueOrDefault(role)
                    ?? AnthillRuntime.ModelRouting.GetValueOrDefault("fallback")
                    ?? new Dictionary<string, string> { ["provider"] = AnthillRuntime.DefaultModelProvider, ["model"] = AnthillRuntime.OllamaModel };

        var provider = route.GetValueOrDefault("provider", AnthillRuntime.DefaultModelProvider);

        // v0.3.8.41 — the Ollama model is only a default for the LOCAL provider.
        //
        // This read `route.GetValueOrDefault("model", AnthillRuntime.OllamaModel)`, so a route that
        // named a provider and no model inherited whatever Ollama happened to be set to. Routing
        // every ant to Claude Code produced `agent:claude-code : gemma4:31b` — an agent paired with
        // a local model it has never heard of and cannot serve. The same applied to any keyed
        // provider: an OpenAI route with no model would have carried gemma4:31b too.
        //
        // Decided by whether the provider is the local one rather than by naming agents, because
        // the core may not know what an agent is. Empty means "this provider decides", which is
        // exactly what GetClient already does for keyed providers via the catalogue default, and
        // what a module-registered provider does for itself.
        var isLocalProvider = string.Equals(provider, AnthillRuntime.DefaultModelProvider, StringComparison.OrdinalIgnoreCase);
        var model = route.GetValueOrDefault("model", isLocalProvider ? AnthillRuntime.OllamaModel : "");

        return (provider, model);
    }

    /// <summary>
    /// v3.8.5 — the router asks for a provider; it no longer knows how to build one.
    ///
    /// What used to be here were two switch statements naming <c>OllamaClient</c>,
    /// <c>OpenAiCompatibleClient</c> and <c>AnthropicClient</c>. They were the one edge that made
    /// "the core runs without any AI provider" false: the core could not even COMPILE without every
    /// provider implementation present. Construction now lives in <c>Anthill.Modules.Reasoning</c>
    /// behind <see cref="IReasoningProviderFactory"/>, and this resolves through
    /// <see cref="ReasoningProviders"/>.
    ///
    /// What is unchanged is everything the router is actually for. Credential resolution still
    /// happens HERE, because the core owns the encrypted store and a module must never reach into
    /// it. The caching rule is untouched, and it is the interesting part:
    ///
    /// Keyed providers are rebuilt on EVERY call rather than cached, because the API key lives in
    /// provider_credentials and can be rotated or revoked from Settings → Providers at any moment.
    /// A cached client would keep using a stale — or just-deleted — key until process restart.
    /// Construction is cheap (each client shares one static HttpClient), so this costs an
    /// allocation and buys correctness.
    /// </summary>
    /// <summary>
    /// Where calls to this provider go. ONE implementation. v0.3.8.41.
    ///
    /// Extracted because <see cref="ResolveEffectiveModel"/> needs the same endpoint
    /// <see cref="GetClient"/> uses — <c>LocalModelResolver</c> asks the host what it has installed,
    /// so a second copy of this expression would let the reported model and the called model be
    /// resolved against different hosts.
    /// </summary>
    private string EndpointFor(string provider)
    {
        var info = ProviderCatalog.Find(provider);
        var keyed = ProviderCatalog.KeyedProviders.Contains(provider);
        var stored = _memory?.GetProviderBaseUrl(provider);
        return string.IsNullOrWhiteSpace(stored)
            ? info?.DefaultEndpoint ?? (keyed ? "" : AnthillRuntime.OllamaHost)
            : stored;
    }

    /// <summary>
    /// The model a call would ACTUALLY use, and when it would not, why. v0.3.8.41.
    ///
    /// THE DEFECT THIS EXISTS FOR. Model resolution had two halves living in different places. The
    /// CONFIGURED route came from <see cref="RoleRoute"/> and could legitimately carry an empty
    /// model since v3.8.33 — "no model chosen" became an honest state. The EFFECTIVE model was then
    /// worked out inside <see cref="GetClient"/>, on the call path, where the catalogue default and
    /// <c>LocalModelResolver</c>'s sole-installed-model rule were applied.
    ///
    /// Everything that DISPLAYS a route read the first and everything that CALLS a model used the
    /// second, so they answered different questions and nothing made them agree. On this project's
    /// own machine that produced seven boot warnings of the form "role 'coder' is routed to
    /// `ollama:`, which is missing: structured output" — grading the capabilities of a model whose
    /// name is the empty string, on a host with `llama3.1:8b` installed and working. `builder` came
    /// back FIT and `coder` UNFIT from the same empty string, which is the tell: the report was a
    /// function of the role's requirements and nothing else.
    ///
    /// This is the same defect v3.8.2 fixed in the same report — fitness computed against a source
    /// the call path does not use — returning through a different door once an empty model became
    /// legal. So the resolution is now ONE method that both the caller and every reader go through.
    /// </summary>
    public ResolvedRoute ResolveEffectiveModel(string provider, string configuredModel)
    {
        var info = ProviderCatalog.Find(provider);
        var keyed = ProviderCatalog.KeyedProviders.Contains(provider);

        // v0.3.8.41 — a provider the catalogue does not know keeps its own (possibly empty) model.
        //
        // `info?.DefaultModel ?? AnthillRuntime.OllamaModel` handed the Ollama tag to every provider
        // the core has never heard of, which is the second half of the same defect as RoleRoute: an
        // agent arrived carrying gemma4:31b. A module-registered provider describes itself, so the
        // core substituting a local model name for it is guesswork dressed as a default.
        var model = !string.IsNullOrWhiteSpace(configuredModel) ? configuredModel
            : info is not null ? (info.DefaultModel ?? AnthillRuntime.OllamaModel)
            : "";

        if (keyed || info is null || !string.IsNullOrWhiteSpace(model))
            return ResolvedRoute.Ok(provider, model);

        // The ROUTE's configured model, not the global one. v0.3.8.41.
        //
        // This read `AnthillRuntime.OllamaModel` — a second read of a global that
        // `RoleRoute` has already consulted and, for a per-role route, deliberately overridden. A
        // role explicitly routed to Ollama with no model of its own would have had the colony-wide
        // tag substituted here, behind the route's back. We only reach this line when the resolved
        // model is blank, so passing the route's own (blank) value is both correct and one fewer
        // mutable static on the path.
        var choice = LocalModelResolver.Resolve(configuredModel, EndpointFor(provider), _modelLister);
        return choice.Resolved
            ? ResolvedRoute.Ok(provider, choice.Model)
            : new ResolvedRoute(provider, "", false, choice.Reason);
    }

    private IModelClient GetClient(string provider, string model)
    {
        var keyed = ProviderCatalog.KeyedProviders.Contains(provider);

        var apiKey = keyed ? _memory?.GetDecryptedApiKey(provider) : null;
        var endpoint = EndpointFor(provider);

        // The resolution — catalogue default, then the sole-installed-model rule for a local
        // provider — now lives in ResolveEffectiveModel, which every reporting surface also calls.
        // A LOCAL provider with no model chosen still refuses here, naming what to do about it
        // (v3.8.33); what changed in v0.3.8.41 is only that the refusal and the answer are computed
        // in one place instead of two.
        var resolution = ResolveEffectiveModel(provider, model);
        if (!resolution.Resolved) return UnavailableProvider.NoModelChosen(provider, resolution.Reason);
        var effectiveModel = resolution.Model;

        if (keyed) return ReasoningProviders.Resolve(provider, effectiveModel, apiKey, endpoint);

        var key = $"{provider}:{effectiveModel}";
        lock (_lock)
        {
            if (_clients.TryGetValue(key, out var existing)) return existing;
            var client = ReasoningProviders.Resolve(provider, effectiveModel, apiKey, endpoint);
            // Only a real provider is cached. Caching an UnavailableProvider would pin the colony
            // to "no AI" for the life of the process even after a module registered — and the
            // registration order between a composition root and a first mission is not something
            // this class should have to be sure about.
            if (client is not UnavailableProvider) _clients[key] = client;
            return client;
        }
    }

    /// <summary>Builds a client for an ad-hoc connection test — the same routing used at mission
    /// time, but callable directly by the API's "Test Connection" action without a role/route.</summary>
    public IModelClient GetClientForProvider(string provider, string? model = null) =>
        GetClient(provider, model ?? ProviderCatalog.Find(provider)?.DefaultModel ?? "");

    /// <summary>
    /// v2.11.2 — resolves the effective route for a role. Normally this is the configured route, but
    /// if that provider's circuit breaker is OPEN and a distinct configured <c>fallback</c> route is
    /// healthy, it fails over to the fallback so the mission keeps moving instead of erroring on a
    /// dead provider. The decision runs through the deterministic <see cref="ModelRoutingPolicy"/>
    /// (stability-preferring: the configured route is only abandoned when proven unhealthy). This is
    /// a no-op when the breaker is disabled or when no distinct fallback is configured.
    /// </summary>
    /// <summary>
    /// What a provider/model pair can do. Discovered where the runtime publishes it (Ollama),
    /// declared otherwise — the same source the call path negotiates against, so routing and
    /// negotiation cannot disagree about whether a model can call tools.
    /// </summary>
    /// <remarks>
    /// Public since v3.4.2 so <c>AntModelFitness</c> can check a role's declared model requirements
    /// against the SAME capability source the call path negotiates against. A fitness report derived
    /// from a second source would eventually describe a different model than the one that runs —
    /// the exact failure the capability endpoint already made once.
    /// </remarks>
    /// <remarks>
    /// v3.8.5: the Ollama-specific branch became a probe lookup. The PRECEDENCE is deliberately
    /// unchanged — a registered probe's answer still beats the hand-written name table, which is
    /// the whole lesson of v3.8.2, where the table reported five roles broken on every restart for
    /// a model that reports tools and thinking. What is new is only that the probe may be absent,
    /// and a probe that cannot describe a model returns null rather than an empty capability set:
    /// "I don't know" falls through to the table, "it supports nothing" would not.
    /// </remarks>
    public static ModelCapabilities CapabilitiesFor(string provider, string model) =>
        ReasoningProviders.Capabilities?.For(provider, model)
        ?? ModelCapabilityCatalog.For(provider, model);

    /// <summary>
    /// A model this provider serves that CAN call tools, or null if none is known.
    ///
    /// Ordered by name for determinism: an agent that silently lands on a different model between
    /// two identical runs is impossible to reason about, and prompt caching dies with it.
    ///
    /// Answerable only from DISCOVERED capabilities, so with no probe registered this is null and
    /// the caller falls back — the name table cannot enumerate what a host happens to have pulled.
    /// </summary>
    private static string? FirstToolCapableModel(string provider)
    {
        var probe = ReasoningProviders.Capabilities;
        if (probe is null) return null;
        return probe.Snapshot(provider)
            .Where(kv => kv.Value.ToolCalling)
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key)
            .FirstOrDefault();
    }

    /// <summary>
    /// v0.3.8.49 (§15) — a model this provider serves that CAN reason (infer a conclusion from evidence
    /// that does not state one), or null if none is known.
    ///
    /// The exact sibling of <see cref="FirstToolCapableModel"/>, and it exists for the exact same
    /// class of silent failure. `coder` and `medic` both declare <c>Reasoning: true</c> — the coder
    /// must hold a change consistent across several files, the medic must infer a failure's cause
    /// from evidence that never names it — yet a fresh install routes every role to the sole local
    /// default (`llama3.1:8b`), which Ollama reports as <c>completion</c> only. A non-reasoning model
    /// does not error on a reasoning task; it returns a fluent, plausible answer to the wrong
    /// question. Routing a declared-reasoning role onto it is the defect, not the model. Discovered
    /// capabilities only, so with no probe registered this is null and the caller leaves the route
    /// untouched (the boot-time fitness warning still fires — reporting, not silence).
    /// </summary>
    private static string? FirstReasoningCapableModel(string provider)
    {
        var probe = ReasoningProviders.Capabilities;
        if (probe is null) return null;
        return probe.Snapshot(provider)
            .Where(kv => kv.Value.Reasoning)
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key)
            .FirstOrDefault();
    }

    /// <summary>
    /// Does this role's declared contract require reasoning? Read from the ONE place role
    /// requirements live (<see cref="Anthill.Core.Agents.AntExecutionCatalog"/>), so routing honours
    /// the same requirement the boot-time fitness report grades — never a second, drifting copy.
    /// </summary>
    private static bool RoleRequiresReasoning(string role) =>
        Anthill.Core.Agents.AntExecutionCatalog.Contracts.TryGetValue(role, out var c)
        && c.ModelNeeds.Reasoning;

    public (string Provider, string Model, string? RerouteReason) ResolveRoute(string role)
    {
        var primary = GetRoute(role);
        if (_breaker is null) return (primary.Provider, primary.Model, null);

        // The role's OWN route is what a priority override fails over to, before the global
        // fallback. That ordering is the whole reason the priority is a preference rather than a
        // replacement: when the promoted model is unhealthy, work should land on the model this ant
        // was deliberately given, not on whatever everything else defaults to.
        var fallback = AnthillRuntime.HasModelPriority ? RoleRoute(role) : RoleRoute("fallback");
        if (fallback.Provider == primary.Provider && fallback.Model == primary.Model)
            return (primary.Provider, primary.Model, null); // nothing distinct to fail over to

        // Health straight from live breaker state: an open breaker == proven-unhealthy for this
        // decision. Unknown routes are left out of the map, which the policy reads as healthy.
        var stats = new Dictionary<string, RouteHealth>(StringComparer.Ordinal);
        void Mark((string Provider, string Model) r)
        {
            var key = ModelStats.Key(r.Provider, r.Model);
            if (_breaker!.Blocked(key) is not null)
                stats[key] = new RouteHealth(key, RouteHealth.MinCallsForVerdict, 0, 0d, 0d);
        }
        Mark(primary);
        Mark(fallback);

        var choice = ModelRoutingPolicy.Choose("high", primary, new[] { fallback }, stats);
        return choice.Provider == primary.Provider && choice.Model == primary.Model
            ? (primary.Provider, primary.Model, null)
            : (choice.Provider, choice.Model, choice.Reason);
    }

    /// <summary>
    /// v2.26.0 introduced this typed boundary; v3.2.0 made it authoritative. It used to call
    /// <c>Generate</c> and re-derive the status by parsing the prose that came back. Now the
    /// status travels with the result from the client that knew it, and the string-returning
    /// <c>Generate</c> is the thin projection instead of the other way round.
    /// </summary>
    /// <param name="system">
    /// v0.3.8.59 (PLAN.md §1b S9) — the role's OPERATING CONTRACT, on the system channel.
    ///
    /// Until this existed every role call was a single user message (see
    /// <see cref="Anthill.SDK.Reasoning.ModelRequest.FromPrompt"/>, which builds exactly one, with
    /// role `user`) carrying the persona, the rules, the output format and the operator's text in
    /// one undifferentiated block. A worker had no way to tell its contract from its input, and
    /// neither did anything downstream — an agent CLI refused whole missions as prompt injection,
    /// which is what a user turn asserting a persona and a system boundary actually looks like.
    ///
    /// Null keeps the old single-message shape, so a caller that has not been converted behaves
    /// exactly as before rather than silently losing its instructions.
    /// </param>
    public ModelCallResult GenerateTyped(string role, string prompt, string? missionId = null,
        string? taskId = null, string? antName = null, int retries = 2, string? system = null) =>
        GenerateCore(role, prompt, missionId, taskId, antName, retries, system);

    /// <summary>Content-only projection, for callers that have not yet moved to the typed result.</summary>
    public string Generate(string role, string prompt, string? missionId = null, string? taskId = null,
        string? antName = null, int retries = 2) =>
        GenerateCore(role, prompt, missionId, taskId, antName, retries).Content;

    private ModelCallResult GenerateCore(string role, string prompt, string? missionId, string? taskId,
        string? antName, int retries, string? system = null) =>
        SendCore(role, Compose(prompt, system), missionId, taskId, antName, retries).ToCallResult();

    /// <summary>
    /// One user message, or a system message and a user message. Never a system message alone —
    /// every provider the colony speaks to treats that as a request with nothing to answer.
    /// </summary>
    private static ModelRequest Compose(string prompt, string? system) =>
        string.IsNullOrWhiteSpace(system)
            ? ModelRequest.FromPrompt(prompt)
            : new ModelRequest
            {
                Messages = new[]
                {
                    new ModelMessage(ModelMessage.System, system!),
                    new ModelMessage(ModelMessage.User, prompt ?? ""),
                },
            };

    /// <summary>
    /// v3.4.0 (ADR-006): route and send a TYPED request — the path a tool-calling agent loop needs,
    /// because it must carry a conversation and a tool list, not a prompt string.
    ///
    /// Deliberately the SAME routing path the string call uses rather than a parallel one. Route
    /// resolution, the circuit breaker, the model_call event and the pheromone trail all live here
    /// once; a second copy for typed calls would drift, and the two would eventually disagree about
    /// whether a route is healthy — which is the failure this method's own comments below record
    /// happening before, when success had two definitions in one method.
    /// </summary>
    public ModelResponse SendTyped(string role, ModelRequest request, string? missionId = null,
        string? taskId = null, string? antName = null, int retries = 2) =>
        SendCore(role, request, missionId, taskId, antName, retries);

    private ModelResponse SendCore(string role, ModelRequest request, string? missionId, string? taskId,
        string? antName, int retries)
    {
        if (!AnthillRuntime.UseOllama && AnthillRuntime.DefaultModelProvider == "ollama")
            return new ModelResponse
            {
                Status = ModelCallOutcome.Error,
                Content = "ERROR: Model routing requested Ollama, but USE_OLLAMA is False.",
            };

        var (provider, model, rerouteReason) = ResolveRoute(role);

        /*
         * v3.4.0 — CAPABILITY-AWARE ROUTING. If this request needs tools and the routed model
         * cannot use them, route to one that can.
         *
         * Found live, and it defeated everything above it: the researcher role routes to
         * llama2-uncensored:70b, which Ollama itself reports as `completion` only. So the tool
         * schemas were correctly stripped — the model genuinely cannot use them — the model was
         * asked a question it could only guess at, and it answered "the system information tool
         * shows that the host is running Linux Ubuntu" having called nothing. Every layer behaved
         * correctly and the result was still a confident fabrication, because the route chose a
         * model that cannot do the job.
         *
         * Reroute rather than fail: an operator asking a question should get an answer from a model
         * that can actually look, not an error about routing tables. The substitution is RECORDED
         * in the reroute reason, because silently answering from a different model than configured
         * is its own kind of lie.
         */
        var toolCapableReroute = (string?)null;
        if (request.Tools.Count > 0 && request.Model is null
            && !CapabilitiesFor(provider, model).ToolCalling)
        {
            var candidate = FirstToolCapableModel(provider);
            if (candidate is not null)
            {
                toolCapableReroute = $"{model} cannot call tools; used {candidate}";
                model = candidate;
            }
        }
        rerouteReason ??= toolCapableReroute;

        /*
         * v0.3.8.49 (§15) — REASONING-AWARE ROUTING, the sibling of the tool reroute above.
         *
         * The same silent failure, one capability over. `coder` and `medic` declare Reasoning: true
         * because their work is inference from evidence that does not state its own conclusion — a
         * change held consistent across files, a cause diagnosed from symptoms that never name it.
         * A fresh install routes every role to the sole local default (llama3.1:8b), which Ollama
         * reports as completion-only. That model does not fail the task; it returns a fluent answer
         * to the wrong question, and the mission reads as a weak result rather than a misroute.
         *
         * So when the ROLE's declared contract needs reasoning and the routed model lacks it, move to
         * a reasoning-capable model the provider actually serves — recorded in the reroute reason,
         * because answering from a different model than configured is only honest when it is logged.
         * Guarded by request.Model is null: a caller that pinned a model has made the decision, and
         * a candidate that is null (no reasoning model installed, or no probe) leaves the route
         * untouched so the boot-time fitness warning remains the operator's cue.
         */
        if (request.Model is null && RoleRequiresReasoning(role)
            && !CapabilitiesFor(provider, model).Reasoning)
        {
            var candidate = FirstReasoningCapableModel(provider);
            if (candidate is not null && candidate != model)
            {
                rerouteReason = rerouteReason is null
                    ? $"{model} cannot reason; used {candidate}"
                    : $"{rerouteReason}; {model} cannot reason, used {candidate}";
                model = candidate;
            }
        }

        var routeKey = $"{provider}:{model}";
        var started = DateTime.UtcNow;

        // If this provider's breaker is open, fail fast without a network call — the whole point is to
        // stop a dead/slow provider from making every mission wait out a full timeout and pin the queue.
        var blockedReason = _breaker?.Blocked(routeKey);
        ModelResponse result;
        if (blockedReason is not null)
        {
            result = new ModelResponse
            {
                Status = ModelCallOutcome.ConnectError,
                Content = $"ERROR: {provider} temporarily unavailable — {blockedReason}. "
                    + "Fast-failed without a network call to keep the mission queue moving.",
                Provider = provider, Model = model,
            };
        }
        else
        {
            // v3.2.0: the status arrives WITH the result. This used to be
            // Classify(response) — recovering, by substring match, what the client already knew.
            // The model the ROUTE selected wins unless the caller pinned one explicitly — per-agent
            // model assignment is a request-level decision, route policy is the default.
            // The model the ROUTE selected wins unless the caller pinned one explicitly — per-agent
            // model assignment is a request-level decision, route policy is the default.
            var effective = request.Model ?? model;
            result = GetClient(provider, model).Send(request with { Model = effective }, retries);
            _breaker?.Record(routeKey, result.Status.ToCircuitSignal());
        }
        var response = result.Content;
        var outcome = result.Status;

        var durationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        // v3.2.0 BEHAVIOUR FIX, called out because it is one: success was
        // !response.StartsWith("ERROR:"), which disagreed with ModelCallResult.Ok — whose own
        // documentation already said "an Empty response is never Ok". A provider returning nothing
        // does not start with ERROR:, so it was counted as a successful call, REINFORCING the
        // route's pheromone trail and reporting success:true in telemetry. Two definitions of
        // success in one method, the exact disease this phase exists to cure. There is now one.
        var success = result.Ok;
        var pheromoneDelta = success ? 0.01
            : outcome is ModelCallOutcome.Timeout or ModelCallOutcome.ConnectError ? -0.02 : -0.01;

        lock (_lock) CallCount++;

        // v3.8.31 — per-TASK model calls, counted here for the same reason tool dispatches are
        // counted in ToolRegistry: this is the chokepoint every call already passes through, and
        // `AntMetrics.ModelCalls` has been zero for every role since it was declared because it was
        // something each ant was expected to self-report and none does.
        //
        // `CallCount` above is per-SESSION and answers a different question — how busy the process
        // has been — so it could never fill a per-task metric. Counted whether or not the call
        // succeeded: a role that burned three failed model calls did three model calls, and a
        // qualification review needs to see that rather than a zero.
        if (taskId is not null) _taskModelCalls.AddOrUpdate(taskId, 1, (_, n) => n + 1);

        if (_memory is not null && missionId is not null)
        {
            _memory.LogEvent(missionId, "model_call", $"Model call for role {role}: {provider}/{model}",
                taskId: taskId, antName: antName ?? role,
                metadata: new()
                {
                    ["role"] = role, ["provider"] = provider,
                    // The model that ACTUALLY served this. Logging the route's choice hid both a
                    // caller-pinned model and a capability reroute — a run attributed to a model
                    // that never saw it is worse than no attribution.
                    ["model"] = result.Model ?? request.Model ?? model,
                    ["route_model"] = model,
                    ["success"] = success,
                    ["outcome"] = outcome.Name(), ["circuit_open"] = blockedReason is not null,
                    ["reroute_reason"] = rerouteReason,
                    ["duration_ms"] = durationMs,
                    ["prompt_chars"] = request.Messages.Sum(m => (m.Content ?? "").Length),
                    ["response_chars"] = response.Length,
                    // v3.4.0: what the call actually cost and whether it asked for tools. Absent
                    // usage stays absent — a provider that reports nothing is unknown, not zero.
                    ["prompt_tokens"] = result.Usage.PromptTokens,
                    ["completion_tokens"] = result.Usage.CompletionTokens,
                    ["tool_calls_requested"] = result.ToolCalls.Count,
                    ["tools_offered"] = request.Tools.Count,
                    ["pheromone_delta"] = pheromoneDelta,
                });
            _memory.UpdatePheromoneTrail($"model:{provider}:{model}:{role}", "model_route", success, pheromoneDelta,
                new()
                {
                    ["role"] = role, ["provider"] = provider, ["model"] = model, ["duration_ms"] = durationMs,
                    ["last_mission_id"] = missionId, ["last_task_id"] = taskId,
                });
        }
        return result;
    }

    /// <summary>
    /// Per-route circuit-breaker health for operator dashboards: which providers are healthy, which
    /// are open (cooling down after repeated transport faults), and which are half-open probing. Empty
    /// when the breaker is disabled or no route has been exercised yet.
    /// </summary>
    public List<Dictionary<string, object?>> ProviderHealth() =>
        _breaker is null
            ? new()
            : _breaker.Snapshot().Select(s => new Dictionary<string, object?>
            {
                ["route"] = s.Key,
                ["state"] = s.State,
                ["consecutive_faults"] = s.ConsecutiveFaults,
                ["seconds_until_close"] = s.SecondsUntilClose,
            }).ToList();

    public string FormatRoutes()
    {
        var lines = new List<string> { $"ANTHILL v{AnthillRuntime.Version} Model Routes" };
        foreach (var role in new[] { "planner", "researcher", "web", "coder", "builder", "verifier", "strategist", "fallback" })
        {
            var (provider, model) = GetRoute(role);
            lines.Add($"{role}: provider={provider} | model={model}");
        }
        return string.Join("\n", lines);
    }

    public string FormatModels()
    {
        var active = AnthillRuntime.ModelRouting.Keys
            .Select(r => { var (p, m) = GetRoute(r); return $"{p}:{m}"; })
            .Distinct().OrderBy(x => x, StringComparer.Ordinal);
        var configuredProviders = _memory?.ListProviderConnections()
            .Where(c => c["configured"] is true)
            .Select(c => c["provider"]?.ToString() ?? "")
            .ToList() ?? new List<string>();
        return $"ANTHILL v{AnthillRuntime.Version} Model Router\n" +
               $"Routing Enabled: {(AnthillRuntime.EnableModelRouting ? "ON" : "OFF")}\n" +
               $"Default Provider: {AnthillRuntime.DefaultModelProvider}\n" +
               $"Ollama Host: {AnthillRuntime.OllamaHost}\n" +
               $"Total Model Calls This Session: {CallCount}\n" +
               $"Active Route Targets: {string.Join(", ", active)}\n" +
               $"Configured External Providers: {(configuredProviders.Count > 0 ? string.Join(", ", configuredProviders) : "none")}\n" +
               $"Per-Call Timeout: {AnthillRuntime.ModelCallTimeoutSeconds}s | " +
               $"Circuit Breaker: {(AnthillRuntime.EnableModelCircuitBreaker ? $"ON (opens after {AnthillRuntime.ModelCircuitFailureThreshold} faults, {AnthillRuntime.ModelCircuitCooldownSeconds}s cooldown)" : "OFF")}\n" +
               FormatProviderHealthLine() +
               "Provider Support: Ollama (local, keyless), OpenAI, Anthropic (Claude), Perplexity, and OpenRouter — " +
               "connect API keys in Settings → Providers.";
    }

    /// <summary>Plain-English live breaker state for the /models view — nothing to interpret: healthy,
    /// or exactly which route is cooling down and for how long.</summary>
    private string FormatProviderHealthLine()
    {
        if (!AnthillRuntime.EnableModelCircuitBreaker) return "";
        var health = ProviderHealth();
        var degraded = health.Where(h => (string?)h["state"] is "open" or "half_open").ToList();
        if (degraded.Count == 0)
            return $"Provider Health: all routes healthy ({health.Count} seen this session)\n";
        var parts = degraded.Select(h => (string?)h["state"] == "open"
            ? $"{h["route"]} cooling down ({h["seconds_until_close"]}s left)"
            : $"{h["route"]} probing (half-open)");
        return $"Provider Health: DEGRADED — {string.Join("; ", parts)}\n";
    }
}
