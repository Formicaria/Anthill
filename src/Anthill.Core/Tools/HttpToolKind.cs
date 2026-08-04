using System.Text;
using System.Text.Json;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;   // ToolResult — the dispatch one. Anthill.Core.Contracts declares a
                             // second type of that name, so it is qualified below rather than
                             // imported; importing both would make every `new ToolResult` ambiguous.

namespace Anthill.Core.Tools;

/// <summary>
/// v3.4.1 (ADR-006) — the first user-tool kind: an HTTP request to an ALLOWLISTED host.
///
/// Chosen first because it adds real reach (wire in an internal API without a rebuild) while
/// introducing no new execution path: the definition is data, and everything it can do is bounded by
/// an allowlist a human maintains in config.
///
/// The security argument, stated once, because everything below follows from it:
///   - the URL comes from the DEFINITION, never from the model. The model fills declared
///     placeholders, and each substituted value is URL-encoded, so an argument cannot add a path
///     segment, a query parameter, or a new host.
///   - the resolved URL is re-checked against the allowlist AFTER substitution, not before. A check
///     on the template proves nothing about the request that is actually sent.
///   - redirects are NOT followed. A 302 from an allowlisted host to an arbitrary one would turn the
///     allowlist into a suggestion, and that is precisely the bypass an allowlist exists to stop.
///   - responses are size-capped, because a tool result is fed straight back into a model's context.
/// </summary>
public sealed class HttpToolKind : IToolKindExecutor
{
    public const int MaxResponseChars = 20_000;
    public const int DefaultTimeoutSeconds = 20;

    /// <summary>
    /// Shared, and configured NOT to follow redirects — see the class remarks. A per-call client
    /// would leak sockets under a loop that calls a tool every turn.
    /// </summary>
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds),
    };

    public ToolKind Kind => ToolKind.Http;

    public IReadOnlyList<string> ValidateConfig(ToolDefinition definition)
    {
        var problems = new List<string>();
        var url = definition.Config.GetValueOrDefault("url") ?? "";
        var method = (definition.Config.GetValueOrDefault("method") ?? "GET").Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(url))
        {
            problems.Add("http tools require a 'url'");
        }
        else
        {
            // Validated against the template with placeholders neutralised: the template itself is
            // not a valid URI while it still contains braces, and refusing it for that would make
            // every parameterised tool unregisterable.
            var probe = System.Text.RegularExpressions.Regex.Replace(url, @"\{[a-zA-Z0-9_]+\}", "x");
            if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri))
                problems.Add($"'url' is not an absolute URI: {url}");
            else if (uri.Scheme is not ("http" or "https"))
                problems.Add($"'url' scheme must be http or https, not '{uri.Scheme}'");
            else if (!HostIsAllowed(uri.Host))
                // Names the setting AND where to change it. Since v3.7.2 user_tool_allowed_hosts is
                // operator-editable, so "add it to config" sent people to a file they no longer
                // need to touch — an accurate refusal that points at the wrong remedy still leaves
                // someone stuck.
                problems.Add($"host '{uri.Host}' is not in user_tool_allowed_hosts — add it there "
                           + "(Settings) first; registering a tool must not be able to widen what "
                           + "the colony can reach");
        }

        if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
            problems.Add($"unsupported method '{method}'");

        return problems;
    }

    /// <summary>
    /// Case-insensitive exact host match. Deliberately NOT suffix matching: an allowlist entry of
    /// "example.com" matching "evil-example.com" — or worse, "example.com.attacker.net" — is the
    /// classic way this check is defeated. A subdomain must be listed on purpose.
    /// </summary>
    public static bool HostIsAllowed(string host) =>
        AnthillRuntime.UserToolAllowedHosts.Contains((host ?? "").Trim().ToLowerInvariant());

    public ToolResult Execute(ToolDefinition definition, IReadOnlyDictionary<string, object?> args)
    {
        var name = definition.Name;

        if (!AnthillRuntime.EnableUserTools)
            return new ToolResult(name, false, "", "User-defined tools are disabled by config.",
                Contracts.FailureClass.AuthorizationFailure);

        var template = definition.Config.GetValueOrDefault("url") ?? "";
        var method = new HttpMethod((definition.Config.GetValueOrDefault("method") ?? "GET").Trim().ToUpperInvariant());

        string url;
        try
        {
            url = Substitute(template, args);
        }
        catch (ArgumentException error)
        {
            return new ToolResult(name, false, "", error.Message, Contracts.FailureClass.ValidationFailure);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new ToolResult(name, false, "", $"resolved URL is not valid: {url}",
                Contracts.FailureClass.ValidationFailure);

        // AFTER substitution. Checking the template would prove a property of a string that is not
        // the one being requested.
        if (!HostIsAllowed(uri.Host))
            return new ToolResult(name, false, "", $"host '{uri.Host}' is not allowlisted for user tools",
                Contracts.FailureClass.AuthorizationFailure);

        try
        {
            using var request = new HttpRequestMessage(method, uri);
            foreach (var (key, value) in definition.Config)
                if (key.StartsWith("header.", StringComparison.OrdinalIgnoreCase))
                    request.Headers.TryAddWithoutValidation(key["header.".Length..], value);

            if (method != HttpMethod.Get && definition.Config.GetValueOrDefault("body") is { Length: > 0 } bodyTemplate)
                request.Content = new StringContent(Substitute(bodyTemplate, args, urlEncode: false),
                    Encoding.UTF8, definition.Config.GetValueOrDefault("content_type") ?? "application/json");

            using var response = Http.Send(request);
            using var reader = new StreamReader(response.Content.ReadAsStream());
            var body = reader.ReadToEnd();
            if (body.Length > MaxResponseChars)
                body = body[..MaxResponseChars] + $"\n...[truncated at {MaxResponseChars} chars]";

            // A redirect is reported rather than followed, and says so, because "302" with no
            // explanation looks like a broken tool instead of a boundary doing its job.
            if ((int)response.StatusCode is >= 300 and < 400)
                return new ToolResult(name, false, "",
                    $"{(int)response.StatusCode} redirect to '{response.Headers.Location}' — user tools do "
                  + "not follow redirects, because a redirect off an allowlisted host would bypass the allowlist",
                    Contracts.FailureClass.TargetRejection);

            if (!response.IsSuccessStatusCode)
                // 4xx is the caller's fault and fixable; 5xx is the far end's and worth retrying.
                return new ToolResult(name, false, body, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                    (int)response.StatusCode >= 500
                        ? Contracts.FailureClass.TransientProviderFailure
                        : Contracts.FailureClass.ValidationFailure);

            return new ToolResult(name, true, body);
        }
        catch (Exception error)
        {
            return new ToolResult(name, false, "", $"{name} request failed: {error.Message}",
                ToolRegistry.ClassifyThrown(error));
        }
    }

    /// <summary>
    /// Fill <c>{placeholder}</c> slots from the model's arguments.
    ///
    /// URL-encoded by default, and that default is load-bearing: without it an argument containing
    /// <c>?</c>, <c>#</c>, <c>/</c> or <c>@</c> rewrites the request into a different path, a
    /// different query, or — with a userinfo <c>@</c> — a different host entirely. The model chose
    /// those characters; the definition chose the URL's shape, and the shape must survive.
    ///
    /// A missing placeholder THROWS rather than resolving to empty. Silently sending
    /// <c>/users//orders</c> produces a confusing 404 far from the actual mistake.
    /// </summary>
    internal static string Substitute(string template, IReadOnlyDictionary<string, object?> args, bool urlEncode = true)
    {
        var missing = new List<string>();
        var filled = System.Text.RegularExpressions.Regex.Replace(template, @"\{([a-zA-Z0-9_]+)\}", match =>
        {
            var key = match.Groups[1].Value;
            if (!args.TryGetValue(key, out var value) || value is null)
            {
                missing.Add(key);
                return "";
            }
            var text = value as string ?? JsonSerializer.Serialize(value).Trim('"');
            return urlEncode ? Uri.EscapeDataString(text) : text;
        });

        if (missing.Count > 0)
            throw new ArgumentException(
                $"missing required argument(s): {string.Join(", ", missing)}");

        return filled;
    }
}
