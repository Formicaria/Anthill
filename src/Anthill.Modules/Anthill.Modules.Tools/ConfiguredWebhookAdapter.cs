using System.Text;
using Anthill.SDK.External;

namespace Anthill.Modules.Tools;

/// <summary>
/// THE PRODUCTION ADAPTER, AND THE CONFIGURED MAP IS THE ALLOWLIST. v0.3.8.103.
///
/// WHAT IT RESOLVES AGAINST. An operator names destinations in configuration — `incident webhook`
/// → `https://hooks.example/incident` — and this resolves an alias only to something in that map.
/// There is no second allowlist on top, deliberately: an explicit operator-written name→url pair IS
/// the strongest allowlist available, and a colony that could reach a host nobody configured would
/// be one whose destination list was advisory. Anything not in the map is unresolvable, and an
/// unresolvable destination never reaches approval, because asking a human to approve a name the
/// colony cannot turn into a url is how a signature ends up attached to whatever that name means
/// later.
///
/// THE EMPTY MAP IS THE DEFAULT, and it is a real state rather than a broken one. A fresh install
/// configures no destinations, so every external-action mission resolves nothing and refuses with a
/// message naming what IS configured — none — which is exactly what an operator needs to read in
/// order to fix it. The class ships fail-closed and self-describing rather than shipping off.
///
/// AMBIGUITY IS A REFUSAL, not a best guess. Two configured names both matching the request means
/// the colony cannot say where the operator meant it to go, and picking the first would be deciding
/// a destination on a coin flip and then asking someone to approve the result.
///
/// THE MAP IS READ THROUGH A DELEGATE rather than captured at construction: configuration changes
/// while the process runs, and an adapter holding a snapshot would keep sending to a destination
/// the operator had already removed. The same reason the decision source is a delegate — this
/// module references the SDK and nothing else, so anything that knows about runtime configuration
/// is handed in at composition.
/// </summary>
public sealed class ConfiguredWebhookAdapter : IExternalActionAdapter
{
    private readonly Func<IReadOnlyDictionary<string, string>> _destinations;
    private readonly Func<HttpClient> _client;

    /// <param name="destinations">Operator-configured name → url. Read on every resolve.</param>
    /// <param name="client">The HTTP client factory, injected so a test can drive this without a
    /// network and so the caller owns redirect and timeout policy.</param>
    public ConfiguredWebhookAdapter(
        Func<IReadOnlyDictionary<string, string>> destinations, Func<HttpClient> client)
    {
        _destinations = destinations ?? throw new ArgumentNullException(nameof(destinations));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Kind => "webhook";

    public ExternalTargetResolution Resolve(string requestedTarget)
    {
        var request = requestedTarget ?? "";
        var configured = _destinations() ?? new Dictionary<string, string>();

        if (configured.Count == 0)
            return ExternalTargetResolution.Unresolvable(
                $"no external destinations are configured, so '{request}' names nothing this colony "
              + "can reach. Add one under `external_destinations` (a name and its url) and it "
              + "becomes resolvable.");

        var matches = configured
            .Where(d => !string.IsNullOrWhiteSpace(d.Key)
                     && request.Contains(d.Key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            return ExternalTargetResolution.Unresolvable(
                $"'{request}' does not name a configured destination. Configured: "
              + string.Join(", ", configured.Keys.OrderBy(k => k, StringComparer.Ordinal)));

        if (matches.Count > 1)
            return ExternalTargetResolution.Unresolvable(
                $"'{request}' names more than one configured destination — "
              + string.Join(", ", matches.Select(m => m.Key).OrderBy(k => k, StringComparer.Ordinal))
              + " — and this colony will not choose between them on the operator's behalf.");

        var url = matches[0].Value ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            return ExternalTargetResolution.Unresolvable(
                $"the destination configured for '{matches[0].Key}' is not a usable http(s) url: '{url}'");

        return ExternalTargetResolution.Resolved(parsed.ToString(), "POST");
    }

    /// <summary>
    /// Deliver, and report the destination FROM THE RESPONSE rather than echoing the argument.
    ///
    /// `response.RequestMessage?.RequestUri` is where the request actually went, which is the value
    /// the integrity gate compares against what the operator approved. Echoing the parameter back
    /// would make that comparison the caller agreeing with itself — the `.99` fixture defect, where
    /// a test and the code it tested matched each other and both disagreed with production.
    /// </summary>
    public ExternalSendReceipt Send(string resolvedTarget, string method, string body)
    {
        try
        {
            using var request = new HttpRequestMessage(
                new HttpMethod(string.IsNullOrWhiteSpace(method) ? "POST" : method), resolvedTarget)
            {
                Content = new StringContent(body ?? "", Encoding.UTF8, "application/json"),
            };

            using var response = _client().Send(request);
            var landed = response.RequestMessage?.RequestUri?.ToString() ?? resolvedTarget;
            var status = $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();

            return response.IsSuccessStatusCode
                ? ExternalSendReceipt.Accepted(landed, status)
                : ExternalSendReceipt.Refused($"the destination answered {status}");
        }
        catch (Exception error)
        {
            // The destination is unreachable, or refused the connection, or timed out. Reported as
            // a refusal rather than thrown: this is the outcome of the send, and the mission's
            // record has a field for exactly this.
            return ExternalSendReceipt.Refused($"the send could not be completed: {error.Message}");
        }
    }
}
