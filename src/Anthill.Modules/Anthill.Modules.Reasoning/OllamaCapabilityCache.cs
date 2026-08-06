using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthill.SDK.Reasoning;

namespace Anthill.Modules.Reasoning;

/// <summary>
/// v3.4.0 — what Ollama reports about its own models, cached, and used by the CALL PATH.
///
/// WHY THIS EXISTS, and it is a bug report as much as a design note: discovery was wired into the
/// /providers/capabilities endpoint and nowhere else. The endpoint told the truth — gemma4:31b
/// supports tools and thinking — while <see cref="OllamaClient"/> kept negotiating against the
/// hand-written name table, which does not know that model at all. So tools were stripped from
/// every request before it left, the model never saw one, and it answered from priors: on the first
/// live run it replied "the system information tool shows that the host is running Linux Ubuntu"
/// having called no tool whatsoever. A hallucinated tool result is worse than a refusal, because it
/// reads as success.
///
/// A page that reports capabilities the runtime does not act on is not a feature, it is a lie with
/// a UI. One source, consulted by both.
///
/// Cached with a TTL because the call path is synchronous and hot: an HTTP round-trip per model
/// call to ask what a model can do would cost more than the call it is describing. The list changes
/// only when an operator pulls or removes a model, so a minute of staleness is invisible — and the
/// failure mode of stale data here is bounded, since an unknown model falls back to the table and
/// then to text-only.
/// </summary>
public static class OllamaCapabilityCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly ConcurrentDictionary<string, ModelCapabilities> Known =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object RefreshLock = new();

    private static DateTime _lastRefresh = DateTime.MinValue;
    private static string _lastHost = "";

    /// <summary>How long a snapshot is trusted. Model lists change when an operator pulls one.</summary>
    public static TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Capabilities for a model served by <paramref name="host"/>. PURE — never does I/O.
    ///
    /// This is on the model-call path, and the first version of it fetched /api/tags here. That was
    /// wrong twice over: a model call must never wait on a lookup describing it, and in the stub
    /// tests the extra request was answered by the one-shot server, leaving the real chat request
    /// with nobody to reply and blocking until the 120s provider timeout. The suite hung.
    ///
    /// Reading only, so the cost is a dictionary lookup. <see cref="Warm"/> is the one thing that
    /// talks to Ollama, and it is called deliberately rather than as a side effect of a call.
    ///
    /// Falls back to the declared table for a model nothing has described — never to "supports
    /// everything". A runtime we have not asked can fail to CONFIRM a capability, never grant one.
    /// </summary>
    public static ModelCapabilities For(string host, string model) =>
        Known.TryGetValue(model ?? "", out var caps)
            ? caps
            : ModelCapabilityCatalog.For("ollama", model);

    /// <summary>Everything currently known, for the operator-facing capabilities report.</summary>
    public static IReadOnlyDictionary<string, ModelCapabilities> Snapshot() =>
        new Dictionary<string, ModelCapabilities>(Known, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ask Ollama what it is holding, and remember it. The ONLY method here that does I/O.
    ///
    /// Called at startup and whenever the capabilities report is requested, so the call path always
    /// reads an already-populated cache. Best-effort: an unreachable runtime leaves the previous
    /// snapshot in place and callers fall back to the table.
    /// </summary>
    public static void Warm(string host) => RefreshIfStale(host);

    /// <summary>Drop the cache — for tests, and for an operator who has just pulled a model.</summary>
    public static void Invalidate()
    {
        lock (RefreshLock) { Known.Clear(); _lastRefresh = DateTime.MinValue; _lastHost = ""; }
    }

    /// <summary>
    /// Populate directly, without asking Ollama. A test seam, and the only way to exercise
    /// capability-dependent behaviour — routing, negotiation — without a live runtime holding the
    /// specific models a test needs. Named plainly rather than hidden, because a hidden seam gets
    /// used in production by someone who does not realise what it bypasses.
    /// </summary>
    public static void Seed(string host, IReadOnlyDictionary<string, ModelCapabilities> models)
    {
        lock (RefreshLock)
        {
            Known.Clear();
            foreach (var (name, caps) in models) Known[name] = caps;
            _lastHost = (host ?? "").TrimEnd('/');
            _lastRefresh = DateTime.UtcNow;   // treat as fresh, so nothing overwrites it mid-test
        }
    }

    private static void RefreshIfStale(string host)
    {
        var normalized = (host ?? "").TrimEnd('/');
        lock (RefreshLock)
        {
            // A host change invalidates regardless of age: capabilities are a property of the
            // runtime that holds the weights, and pointing at a different Ollama is a different set.
            var stale = DateTime.UtcNow - _lastRefresh > Ttl || !string.Equals(_lastHost, normalized, StringComparison.OrdinalIgnoreCase);
            if (!stale) return;

            // Marked refreshed BEFORE the attempt, so a provider that is down costs one timeout per
            // TTL rather than one per model call — the whole point of the cache is that a sleeping
            // local runtime does not make every call wait.
            _lastRefresh = DateTime.UtcNow;
            _lastHost = normalized;

            try
            {
                var body = Http.GetStringAsync($"{normalized}/api/tags").GetAwaiter().GetResult();
                var root = JsonNode.Parse(body)?.AsObject();
                var found = new Dictionary<string, ModelCapabilities>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in root?["models"]?.AsArray() ?? new JsonArray())
                {
                    var name = entry?["name"]?.GetValue<string>() ?? entry?["model"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var reported = new List<string>();
                    foreach (var c in entry?["capabilities"]?.AsArray() ?? new JsonArray())
                    {
                        var value = c?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(value)) reported.Add(value!);
                    }
                    // The context window comes from /api/show, not /api/tags — tags does not carry
                    // it. Discovered here rather than left null because a requirement nothing can
                    // evaluate is a requirement that quietly stops being true: the archivist and
                    // scribe contracts declare context floors, and with this field never populated
                    // they reported FIT against every model regardless of window. Verified live —
                    // the fitness report said archivist was fit on a model whose window was unknown.
                    found[name!] = ModelCapabilities.FromOllama(reported) with
                    {
                        ContextWindowTokens = ContextWindowOf(normalized, name!),
                    };
                }

                // Replaced wholesale rather than merged: a model the operator has REMOVED must stop
                // being credited with its capabilities, and merging would keep it alive forever.
                Known.Clear();
                foreach (var (name, caps) in found) Known[name] = caps;
            }
            catch (Exception)
            {
                // Unreachable or unrecognised: keep whatever was last known and let callers fall
                // back to the declared table. Deliberately silent — this runs on the model call
                // path, and an operator without a local runtime should not get log noise per call.
            }
        }
    }

    /// <summary>
    /// One model's context window, from <c>/api/show</c>.
    ///
    /// The key is architecture-prefixed — <c>llama.context_length</c>, <c>gemma3.context_length</c>,
    /// <c>qwen3.context_length</c> — so it is found by SUFFIX rather than by a table of
    /// architectures, which would need editing every time a new one ships and would silently report
    /// "unknown" until someone noticed.
    ///
    /// Null on any doubt, and that is the safe direction: <see cref="Agents.AntModelFitness"/>
    /// deliberately does not treat an unknown window as too small, so a failed probe costs a check
    /// that does not fire rather than a false warning an operator learns to ignore.
    ///
    /// One extra request per model, inside <see cref="Warm"/>, which is already the only method here
    /// that does I/O and is called off the call path.
    /// </summary>
    private static int? ContextWindowOf(string host, string model)
    {
        try
        {
            using var content = new StringContent(
                new JsonObject { ["model"] = model }.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json");
            var response = Http.PostAsync($"{host}/api/show", content).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return null;

            return ReadContextWindow(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }
        catch (Exception)
        {
            return null;   // unknown, never guessed — see the remarks above
        }
    }

    /// <summary>
    /// The parsing half, separated from the I/O so it is testable without a live runtime — the same
    /// split <c>ProviderWireFormat</c> uses, and for the same reason: every mistake in reading a
    /// provider's reply is a SILENT one, and silent mistakes need tests rather than integration luck.
    /// </summary>
    internal static int? ReadContextWindow(string json)
    {
        try
        {
            var info = JsonNode.Parse(json)?.AsObject()?["model_info"]?.AsObject();
            if (info is null) return null;

            foreach (var (key, value) in info)
                if (key.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase)
                    && value?.GetValue<int>() is { } tokens and > 0)
                    return tokens;

            return null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
