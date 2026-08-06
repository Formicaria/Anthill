using System.Text;
using System.Text.Json;
using Anthill.SDK.Reasoning;

namespace Anthill.Modules.Reasoning;


/// <summary>
/// Local Ollama client, speaking OpenAI on the wire.
///
/// v3.3.0: this talks to Ollama's OPENAI-COMPATIBLE endpoint (<c>/v1/chat/completions</c>), not the
/// native <c>/api/generate</c>. One decision, three consequences, and the first is the reason:
///
/// 1. <c>/api/generate</c> HAS NO TOOL-CALL CHANNEL. It takes a prompt string and returns a
///    completion string, so a local model physically cannot ask to run a tool through it. Every
///    local agent loop, every self-improvement cycle, every "read this file then patch it" is
///    unreachable on that endpoint — not hard, unreachable. Function-calling local models
///    (Hermes, Qwen, Llama 3.x) emit OpenAI-shaped <c>tool_calls</c>, and this is where they land.
/// 2. It collapses a special case rather than adding one. Ollama now shares the exact request
///    projection, tool schema and response reader with OpenAI, LM Studio, vLLM, llama.cpp and
///    OpenRouter — so a tool-calling bug is fixed once for every provider, and the tests that
///    cover the shape cover all of them.
/// 3. Local stays first-class. No API key, no cost, no cloud round-trip; the only thing that
///    changed is the dialect it is asked in.
///
/// What is deliberately KEPT is the diagnostic that matters most here: a 404 from Ollama nearly
/// always means the model is not pulled, and saying so — with the exact <c>ollama pull</c> command
/// — is the difference between a two-second fix and an operator debugging their network.
/// </summary>
public sealed class OllamaClient : IReasoningProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(185) };
    private readonly string _model;
    private readonly string _host;
    private readonly IReasoningRuntimeOptions _options;

    /// <summary>
    /// v3.8.5 — host, model and the call timeout are HANDED IN rather than read from the core's
    /// runtime statics. That is the whole reason this class could move out of the core: it was one
    /// `using Anthill.Core.Configuration` away from being module code already, and that import was
    /// the only thing making a provider implementation a core dependency.
    /// </summary>
    public OllamaClient(string model, string host, IReasoningRuntimeOptions? options = null)
    {
        _model = model;
        _host = (host ?? string.Empty).TrimEnd('/');
        _options = options ?? DefaultReasoningRuntimeOptions.Instance;
    }

    /// <summary>
    /// v3.3.0: typed, still on /api/generate.
    ///
    /// The endpoint deliberately does NOT change in this increment. Ollama's /api/chat is where
    /// tool calling and real multi-turn live, and it is where this is going — but moving the wire
    /// AND the contract in one step would leave a broken local model call indistinguishable from a
    /// broken refactor. This step is structural only: identical request on the wire, identical
    /// bytes back, transport and error classification untouched.
    ///
    /// Messages are flattened with role labels. Lossy in principle, lossless in practice today —
    /// every caller sends a single user message — and tools cannot arrive here because the
    /// capability catalog gives the ollama PROVIDER no tool calling, so nothing offers them.
    /// </summary>
    public ModelResponse Send(ModelRequest request, int retries = 2)
    {
        // Ollama's OpenAI-compatible endpoint, not /api/generate. Same body, same tool schema and
        // same reader as OpenAI, LM Studio, vLLM, llama.cpp and OpenRouter — see the class remarks.
        var url = ChatEndpoint(_host);
        var model = request.Model ?? _model;

        // Negotiated against what OLLAMA REPORTS about this model, not against a table of guesses.
        // The name table remains the fallback inside the cache for a model Ollama does not describe.
        var negotiated = ModelCapabilityCatalog.Negotiate(
            request, OllamaCapabilityCache.For(_host, model));
        var payload = ProviderWireFormat.OpenAiBody(negotiated, model).ToJsonString();
        // The operator-facing prose is unchanged throughout; only the STATUS is now carried
        // alongside it instead of being recoverable from it.
        var lastError = Fail(ModelCallOutcome.Empty, model, "");
        for (var attempt = 1; attempt <= retries; attempt++)
        {
            // Link the mission's ambient token (so a timed-out/cancelled mission aborts this call)
            // with a hard per-call deadline — the wait is now bounded AND cancellable, never the
            // old up-to-185s-per-attempt block that could freeze the single-writer job queue.
            var ambient = ModelCallScope.Current;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ambient);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.ModelCallTimeoutSeconds));
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = Http.PostAsync(url, content, cts.Token).GetAwaiter().GetResult();
                // v2.4.3: a non-2xx is NOT a connection failure — report what Ollama actually said.
                // The classic trap: a 404 here almost always means the model is not pulled, which
                // used to masquerade as "could not connect" and sent operators chasing networking.
                if (!response.IsSuccessStatusCode)
                {
                    var errBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var detail = errBody.Length > 0 && errBody.Length <= 300 ? $" — {errBody.Trim()}" : "";
                    return (int)response.StatusCode == 404
                        ? Fail(ModelCallOutcome.NotAvailable, model,
                            $"ERROR: Ollama at {_host} is reachable but model '{model}' is not available{detail}. Run: ollama pull {model} (an offline machine needs the model blobs copied in — it cannot pull).")
                        : Fail(ModelCallOutcome.HttpError, model,
                            $"ERROR: Ollama at {_host} answered HTTP {(int)response.StatusCode}{detail}.");
                }
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                // The same tested reader every OpenAI-compatible provider uses. It recovers tool
                // calls and usage, which is the entire point of moving off /api/generate: that
                // endpoint has no tool-call channel, so a local model could never call anything.
                return ProviderWireFormat.ReadOpenAi(body, "ollama", model);
            }
            catch (HttpRequestException error)
            {
                return Fail(ModelCallOutcome.ConnectError, model, $"ERROR: Could not connect to Ollama at {_host} ({error.GetBaseException().Message}). "
                    + "Check: is Ollama running there; if it is on another machine, is OLLAMA_HOST=0.0.0.0 set on it "
                    + "(Ollama binds only 127.0.0.1 by default) and does ANTHILL's ollama_host point at its IP, not localhost?");
            }
            catch (OperationCanceledException) when (ambient.IsCancellationRequested)
            {
                // The mission itself was stopped (deadline reached or job cancelled) — abort cleanly
                // and do NOT retry; retrying would just re-hit the already-cancelled token.
                return Fail(ModelCallOutcome.Cancelled, model, "ERROR: Ollama request cancelled because the mission was stopped.");
            }
            catch (OperationCanceledException)
            {
                lastError = Fail(ModelCallOutcome.Timeout, model, $"ERROR: Ollama request timed out after {_options.ModelCallTimeoutSeconds}s (attempt {attempt}/{retries}).");
            }
            catch (Exception error)
            {
                lastError = Fail(ModelCallOutcome.Error, model, $"ERROR: Ollama request failed: {error.Message} (attempt {attempt}/{retries}).");
            }
        }
        return lastError;
    }

    /// <summary>
    /// A failure, carrying which provider and model produced it. The operator prose is byte for
    /// byte what it was — only the envelope changed — because these strings are what an operator
    /// reads when a local model will not answer, and a refactor is not a licence to reword them.
    /// </summary>
    private static ModelResponse Fail(ModelCallOutcome status, string model, string message) =>
        new() { Status = status, Content = message, Provider = "ollama", Model = model };

    /// <summary>
    /// The chat endpoint for a configured Ollama host, tolerating what operators actually type.
    ///
    /// `ollama_host` has always meant the bare host ("http://10.10.10.57:11434") because the native
    /// API lived at /api/*. Now that the OpenAI-compatible path is used, an operator who knows that
    /// will reasonably paste "…:11434/v1" — the form every OpenAI client calls a base URL — and
    /// blindly appending would post to /v1/v1/chat/completions and 404. Both forms are accepted, as
    /// is a host that already carries the full path.
    ///
    /// Public and pure so it is testable without a network call, exactly like
    /// <c>OpenAiCompatibleClient.NormalizeEndpoint</c>, whose job this is the Ollama-side twin of.
    /// </summary>
    public static string ChatEndpoint(string host)
    {
        var trimmed = (host ?? "").Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return trimmed;
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return trimmed + "/chat/completions";
        return trimmed + "/v1/chat/completions";
    }

    // Flatten() lived here and is deleted with the endpoint that needed it. It squashed a message
    // list into one prompt string because /api/generate accepted nothing else — a lossy step that
    // the OpenAI-compatible endpoint makes unnecessary: roles now travel as roles.
}