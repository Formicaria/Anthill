using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Anthill.SDK.Knowledge;

namespace Anthill.Modules.Knowledge;

/// <summary>
/// The only thing in ANTHILL that speaks HTTP to FORAGER.
///
/// Deliberately dumb: it builds a URL, sends it, and turns whatever comes back into a typed
/// <see cref="KnowledgeOutcome{T}"/>. It knows nothing about scopes, ranking, evidence or context
/// assembly — those are decisions, and decisions live a layer up in
/// <see cref="ForagerKnowledgeProvider"/>. Keeping transport ignorant is what makes the failure
/// taxonomy trustworthy: every error in this file is a fact about the network or the response, not
/// an interpretation of one.
///
/// NEVER THROWS for an operational failure. A knowledge base being down is an ordinary condition
/// for a colony whose knowledge base is optional, and an exception crossing this boundary would
/// turn it into an incident. The only exceptions that escape are the ones that indicate a defect in
/// this process, and those are caught at the tool boundary and classified there.
/// </summary>
internal sealed class ForagerClient : IDisposable
{
    // ONE HttpClient for the process lifetime. A client per call exhausts sockets under any real
    // polling load — the console alone probes availability on a timer — and the well-known fix is
    // exactly this. PooledConnectionLifetime is what keeps it from pinning stale DNS forever, which
    // is the failure mode that made "just use a static HttpClient" bad advice on its own.
    private readonly HttpClient _http;
    private readonly KnowledgeOptionsSource _options;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        // FORAGER sends nulls for absent optional fields rather than omitting them, and sends
        // numbers for enums nowhere. Nothing exotic is needed; this stays minimal on purpose.
    };

    public ForagerClient(KnowledgeOptionsSource options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AllowAutoRedirect = false,
            })
            : new HttpClient(handler);

        // No client-level Timeout. Per-call cancellation is used instead, because the three call
        // classes (probe, retrieval, ingestion) have genuinely different ceilings and a single
        // client timeout would force the shortest one on all of them.
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// GET a JSON document. <paramref name="path"/> is the API-relative path with its query string
    /// already built and escaped — the caller owns escaping because the caller knows which segments
    /// are identifiers and which are user text.
    /// </summary>
    public async Task<KnowledgeOutcome<T>> GetAsync<T>(string path, int timeoutMs, CancellationToken cancellationToken)
        where T : class
        => await SendAsync<T>(HttpMethod.Get, path, null, timeoutMs, cancellationToken).ConfigureAwait(false);

    public async Task<KnowledgeOutcome<T>> PostAsync<T>(string path, object? body, int timeoutMs, CancellationToken cancellationToken)
        where T : class
        => await SendAsync<T>(HttpMethod.Post, path, body, timeoutMs, cancellationToken).ConfigureAwait(false);

    private async Task<KnowledgeOutcome<T>> SendAsync<T>(
        HttpMethod method, string path, object? body, int timeoutMs, CancellationToken cancellationToken)
        where T : class
    {
        var options = _options();

        var unusable = options.Unusable();
        if (unusable is not null)
        {
            // Configuration is checked HERE, on every call, rather than once at construction.
            // Options are re-read per call by design, so a colony that has knowledge switched off
            // mid-run must stop talking to FORAGER on the next request, not at the next restart.
            return KnowledgeOutcome<T>.Failed(
                options.Enabled ? KnowledgeFailure.Invalid : KnowledgeFailure.Disabled, unusable);
        }

        if (!Uri.TryCreate(CombineUrl(options.Endpoint, path), UriKind.Absolute, out var uri))
            return KnowledgeOutcome<T>.Failed(KnowledgeFailure.Invalid, $"could not build a request URL for '{path}'");

        // Re-validated per call rather than trusted from Unusable() above. The two checks look
        // redundant and are not: Unusable() validated the configured BASE, this validates the URL
        // actually about to be sent, and a path that somehow carried an absolute URL would slip
        // past the first check and be caught by this one.
        if (!options.AllowRemoteEndpoint && !KnowledgeOptions.IsLoopback(uri))
            return KnowledgeOutcome<T>.Failed(KnowledgeFailure.Invalid,
                $"refusing a non-loopback knowledge request to '{uri.Host}'");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Max(250, timeoutMs));

        using var request = new HttpRequestMessage(method, uri);
        if (options.Token.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The CALLER cancelled — a stopped mission, a closed request. Not a knowledge failure,
            // and reporting it as Unavailable would slander a healthy service.
            return KnowledgeOutcome<T>.Failed(KnowledgeFailure.Timeout, "the knowledge request was cancelled");
        }
        catch (OperationCanceledException)
        {
            return KnowledgeOutcome<T>.Failed(KnowledgeFailure.Timeout,
                $"the knowledge service did not respond within {timeoutMs}ms");
        }
        catch (HttpRequestException error)
        {
            return KnowledgeOutcome<T>.Failed(KnowledgeFailure.Unavailable,
                $"the knowledge service at {options.Endpoint} could not be reached: {error.Message}");
        }

        using (response)
        {
            string payload;
            try
            {
                payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return KnowledgeOutcome<T>.Failed(KnowledgeFailure.Timeout,
                    $"the knowledge service response was not fully read within {timeoutMs}ms");
            }
            catch (HttpRequestException error)
            {
                return KnowledgeOutcome<T>.Failed(KnowledgeFailure.Unavailable,
                    $"the knowledge service response could not be read: {error.Message}");
            }

            if (!response.IsSuccessStatusCode)
                return Failure<T>(response.StatusCode, payload);

            if (payload.Length == 0)
                return KnowledgeOutcome<T>.Failed(KnowledgeFailure.Malformed,
                    "the knowledge service returned an empty body where a document was expected");

            try
            {
                var value = JsonSerializer.Deserialize<T>(payload, Json);
                return value is null
                    ? KnowledgeOutcome<T>.Failed(KnowledgeFailure.Malformed,
                        "the knowledge service returned a null document")
                    : KnowledgeOutcome<T>.Success(value);
            }
            catch (JsonException error)
            {
                // Deliberately does NOT include the payload. A malformed knowledge response can
                // contain confidential source text, and this string reaches logs.
                return KnowledgeOutcome<T>.Failed(KnowledgeFailure.Malformed,
                    $"the knowledge service returned a response this build could not parse: {error.Message}");
            }
        }
    }

    /// <summary>
    /// Turn a non-2xx into a typed failure, keeping FORAGER's <c>request_id</c> when it sent one.
    /// That id is echoed in its own log, so it is the single most useful thing for correlating a
    /// colony-side failure with the line that explains it.
    /// </summary>
    private static KnowledgeOutcome<T> Failure<T>(HttpStatusCode status, string payload) where T : class
    {
        string? requestId = null;
        string? message = null;
        try
        {
            var envelope = JsonSerializer.Deserialize<ForagerErrorEnvelope>(payload, Json);
            requestId = envelope?.Error?.RequestId;
            message = envelope?.Error?.Message;
        }
        catch (JsonException)
        {
            // A non-JSON error body is entirely possible — a proxy's HTML 502, for one — and is not
            // itself an error worth reporting over the status code that caused it.
        }

        var described = string.IsNullOrWhiteSpace(message) ? $"HTTP {(int)status}" : message;

        var failure = status switch
        {
            HttpStatusCode.NotFound => KnowledgeFailure.NotFound,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => KnowledgeFailure.Unauthorized,
            HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType
                or HttpStatusCode.RequestEntityTooLarge => KnowledgeFailure.Invalid,

            // 409 is FORAGER saying "a job is already running" or "that was already decided". The
            // request was well-formed and the service is healthy, so this is Upstream (retryable)
            // rather than Invalid (the caller's fault) — waiting really can change the answer.
            HttpStatusCode.Conflict => KnowledgeFailure.Upstream,

            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => KnowledgeFailure.Timeout,
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway => KnowledgeFailure.Unavailable,
            _ => (int)status >= 500 ? KnowledgeFailure.Upstream : KnowledgeFailure.Invalid,
        };

        return KnowledgeOutcome<T>.Failed(failure, $"the knowledge service refused the request: {described}", requestId);
    }

    private static string CombineUrl(string endpoint, string path) =>
        $"{endpoint.TrimEnd('/')}/api/{path.TrimStart('/')}";

    /// <summary>Percent-encode one path segment. Ids are opaque and must never be interpolated raw.</summary>
    public static string Segment(string value) => Uri.EscapeDataString(value);

    /// <summary>Percent-encode one query-string value.</summary>
    public static string Query(string value) => Uri.EscapeDataString(value);

    public void Dispose() => _http.Dispose();
}
