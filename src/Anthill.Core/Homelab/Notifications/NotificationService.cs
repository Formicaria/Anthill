using System.Text;
using System.Text.Json;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Health;

namespace Anthill.Core.Homelab.Notifications;

/// <summary>
/// Webhook notifications (v1.11.0, NORTH_STAR Phase 7). Config-gated and DISABLED by default
/// (homelab_notifications_enabled). Supports Slack, Discord, and a generic JSON webhook. Fires on
/// health-check failures and incident candidates (pending approvals join in V2.1). Strict timeout,
/// never throws into callers, and every send attempt writes an audit homelab_events row that
/// carries the alert content — never a webhook URL or any secret.
/// </summary>
public sealed class NotificationService
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly IHomelabRepository _repository;
    private readonly TimeSpan _timeout;

    public NotificationService(IHomelabRepository repository, TimeSpan? timeout = null)
    {
        _repository = repository;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    public static bool Enabled => AnthillRuntime.EnableHomelabNotifications;

    /// <summary>Configured webhook targets (kind + URL), read fresh so settings edits apply live.</summary>
    private static IEnumerable<(string Kind, string Url, string PayloadKey)> Targets()
    {
        if (!string.IsNullOrWhiteSpace(AnthillRuntime.HomelabSlackWebhook))
            yield return ("slack", AnthillRuntime.HomelabSlackWebhook, "text");
        if (!string.IsNullOrWhiteSpace(AnthillRuntime.HomelabDiscordWebhook))
            yield return ("discord", AnthillRuntime.HomelabDiscordWebhook, "content");
        if (!string.IsNullOrWhiteSpace(AnthillRuntime.HomelabGenericWebhook))
            yield return ("generic", AnthillRuntime.HomelabGenericWebhook, "");
    }

    /// <summary>
    /// Pushes one alert to every configured webhook. Returns how many webhooks accepted it.
    /// No-ops (0) when the gate is off or nothing is configured.
    /// </summary>
    public async System.Threading.Tasks.Task<int> SendAsync(AlertRecord alert, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(alert.CreatedAt)) alert.CreatedAt = AnthillTime.NowUtc().ToIso();
        if (!Enabled) return 0;

        var delivered = 0;
        var text = $"[ANTHILL {alert.Severity}] {alert.Kind}: {alert.Message}" +
                   (alert.Target.Length > 0 ? $" (target: {alert.Target})" : "");
        foreach (var (kind, url, key) in Targets())
        {
            var payload = key.Length > 0
                ? JsonSerializer.Serialize(new Dictionary<string, string> { [key] = text })
                : JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["title"] = alert.Kind, ["message"] = alert.Message, ["severity"] = alert.Severity,
                    ["target"] = alert.Target, ["source"] = "anthill", ["version"] = AnthillRuntime.Version,
                    ["created_at"] = alert.CreatedAt,
                });
            var ok = false;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_timeout);
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var resp = await Http.PostAsync(url, content, cts.Token).ConfigureAwait(false);
                ok = resp.IsSuccessStatusCode;
            }
            catch { /* offline/misconfigured webhook must never break a health run */ }

            // Audit: alert content + webhook kind only — the URL never appears anywhere.
            _repository.RecordEvent(new HomelabEvent
            {
                EventType = ok ? "notification_sent" : "notification_failed",
                SubjectKind = "alert", SubjectId = alert.Id,
                Severity = ok ? "info" : "warning",
                Message = $"{kind} webhook — {text}",
            });
            if (ok) delivered++;
        }
        return delivered;
    }
}
