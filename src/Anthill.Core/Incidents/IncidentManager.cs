using System.Text.Json.Serialization;
using Anthill.Core.Common;
using Anthill.Core.Homelab;

namespace Anthill.Core.Incidents;

/// <summary>One time-ordered entry in an incident's reconstructed history.</summary>
public sealed class TimelineEntry
{
    [JsonPropertyName("at")] public string At { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = ""; // change | event | health | incident
    [JsonPropertyName("severity")] public string Severity { get; set; } = "info";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    /// <summary>True for changes that landed in the lookback window BEFORE the incident opened —
    /// the "what changed right before it broke" suspects.</summary>
    [JsonPropertyName("suspect")] public bool Suspect { get; set; }
}

/// <summary>A past incident scored against the current one, carrying what fixed it last time.</summary>
public sealed class SimilarIncident
{
    [JsonPropertyName("incident")] public IncidentRecord Incident { get; set; } = new();
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("fixed_by")] public string FixedBy { get; set; } = ""; // the resolved root cause, verbatim
}

/// <summary>
/// Incident + change memory (v1.14.0, NORTH_STAR Phase 10). Connects failures to recent changes
/// and past fixes. Tracking, timelines, and recommendations ONLY — nothing here can remediate.
///
/// - Auto-opens incidents from the health system's `incident_candidate` events (idempotent sweep:
///   one open incident per subject).
/// - Timeline: reconstructs everything around an incident — the change_log lookback window before
///   it opened (suspects), health results and homelab events for its subject during it.
/// - Similar incidents: deterministic token-overlap + same-subject scoring over past incidents;
///   resolved matches surface their root cause as "this fixed it last time" (the fix memory).
/// - Repeated-failure patterns: a subject that keeps producing incidents gets flagged and its new
///   incidents open at error severity.
/// </summary>
public sealed class IncidentManager
{
    private readonly IHomelabRepository _repository;

    /// <summary>Hours of change_log history before an incident opens that count as suspects.</summary>
    public const int SuspectLookbackHours = 24;
    /// <summary>Incidents from the same subject within the pattern window that mark a repeat offender.</summary>
    public const int PatternThreshold = 3;
    public const int PatternWindowDays = 14;

    public IncidentManager(IHomelabRepository repository) => _repository = repository;

    // ---- Opening (manual + candidate sweep) ------------------------------------------------------

    /// <summary>
    /// Opens an incident unless one is already open/investigating for the same subject (dedupe).
    /// Detects repeat offenders and upgrades their severity. Returns the incident (new or existing).
    /// </summary>
    public IncidentRecord Open(string title, string subjectKind, string subjectId, string severity, string openedBy)
    {
        var existing = _repository.ListIncidents()
            .FirstOrDefault(i => i.SubjectId == subjectId && i.Status is "open" or "investigating");
        if (existing is not null) return existing;

        var isRepeat = IsRepeatOffender(subjectId);
        var incident = new IncidentRecord
        {
            Title = title, SubjectKind = subjectKind, SubjectId = subjectId,
            Severity = isRepeat ? "error" : severity, Status = "open",
            OpenedAt = AnthillTime.NowUtc().ToIso(),
        };
        _repository.OpenIncident(incident, openedBy);
        if (isRepeat)
            _repository.RecordEvent(new HomelabEvent
            {
                EventType = "incident_pattern", SubjectKind = "incident", SubjectId = incident.Id,
                Severity = "error",
                Message = $"Repeated-failure pattern: '{subjectId}' has produced {PatternThreshold}+ incidents in {PatternWindowDays} days",
            });
        return incident;
    }

    internal bool IsRepeatOffender(string subjectId)
    {
        var cutoff = AnthillTime.NowUtc().AddDays(-PatternWindowDays).ToIso();
        return _repository.ListIncidents()
            .Count(i => i.SubjectId == subjectId && string.CompareOrdinal(i.OpenedAt, cutoff) >= 0) >= PatternThreshold;
    }

    /// <summary>
    /// Scheduler sweep: turns recent `incident_candidate` events (written by the health system at
    /// 3 consecutive failures) into incidents. Idempotent — the per-subject dedupe in Open() means
    /// re-sweeping the same candidates never duplicates.
    /// </summary>
    public System.Threading.Tasks.Task<HomelabProviderResult> SweepAsync(CancellationToken ct)
    {
        var opened = 0;
        foreach (var candidate in _repository.RecentEvents(100).Where(e => e.EventType == "incident_candidate"))
        {
            var before = _repository.ListIncidents().Count;
            Open($"Health failures: {candidate.SubjectId}", "health_check", candidate.SubjectId, "warning", "incident-sweep");
            if (_repository.ListIncidents().Count > before) opened++;
        }
        return System.Threading.Tasks.Task.FromResult(
            HomelabProviderResult.Success($"incident sweep ok ({opened} opened)", opened));
    }

    // ---- Timeline -----------------------------------------------------------------------------------

    /// <summary>Reconstructs the incident's history: suspect changes before it, everything during it.</summary>
    public IReadOnlyList<TimelineEntry> Timeline(string incidentId)
    {
        var incident = _repository.GetIncident(incidentId);
        if (incident is null) return Array.Empty<TimelineEntry>();
        var openedAt = AnthillTime.ParseIsoOrNow(incident.OpenedAt);
        var windowStart = openedAt.AddHours(-SuspectLookbackHours).ToIso();
        var windowEnd = string.IsNullOrWhiteSpace(incident.ResolvedAt) ? AnthillTime.NowUtc().ToIso() : incident.ResolvedAt;

        var entries = new List<TimelineEntry>
        {
            new() { At = incident.OpenedAt, Kind = "incident", Severity = incident.Severity, Summary = $"Incident opened: {incident.Title}" },
        };
        if (!string.IsNullOrWhiteSpace(incident.ResolvedAt))
            entries.Add(new TimelineEntry
            {
                At = incident.ResolvedAt, Kind = "incident", Severity = "info",
                Summary = "Incident resolved" + (incident.RootCause.Length > 0 ? $" — root cause: {incident.RootCause}" : ""),
            });

        foreach (var change in _repository.RecentChanges(500)
                     .Where(c => InWindow(c.CreatedAt, windowStart, windowEnd)))
        {
            var beforeOpen = string.CompareOrdinal(change.CreatedAt, incident.OpenedAt) < 0;
            entries.Add(new TimelineEntry
            {
                At = change.CreatedAt, Kind = "change", Severity = "info",
                Summary = $"{change.ChangeKind} {change.SubjectKind}: {change.Summary}" + (change.ChangedBy.Length > 0 ? $" (by {change.ChangedBy})" : ""),
                Suspect = beforeOpen, // it landed in the lookback window before things broke
            });
        }

        foreach (var evt in _repository.RecentEvents(500)
                     .Where(e => InWindow(e.CreatedAt, windowStart, windowEnd))
                     .Where(e => Mentions(e.SubjectId, incident.SubjectId) || Mentions(e.Message, incident.SubjectId)))
            entries.Add(new TimelineEntry { At = evt.CreatedAt, Kind = "event", Severity = evt.Severity, Summary = $"{evt.EventType}: {evt.Message}" });

        foreach (var health in _repository.RecentHealthResultsForTarget(incident.SubjectId, 50)
                     .Where(h => InWindow(h.CheckedAt, windowStart, windowEnd)))
            entries.Add(new TimelineEntry
            {
                At = health.CheckedAt, Kind = "health",
                Severity = health.Status == "failed" ? "warning" : "info",
                Summary = $"{health.CheckKind} check {health.Status} ({health.LatencyMs}ms): {health.Detail}",
            });

        return entries.OrderBy(e => e.At, StringComparer.Ordinal).ToList();
    }

    private static bool InWindow(string at, string start, string end) =>
        at.Length > 0 && string.CompareOrdinal(at, start) >= 0 && string.CompareOrdinal(at, end) <= 0;
    private static bool Mentions(string haystack, string needle) =>
        needle.Length > 0 && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    // ---- Similar incidents + fix memory ----------------------------------------------------------------

    /// <summary>Deterministic similarity over past incidents; resolved matches carry their fix.</summary>
    public IReadOnlyList<SimilarIncident> Similar(string incidentId, int top = 3)
    {
        var current = _repository.GetIncident(incidentId);
        if (current is null) return Array.Empty<SimilarIncident>();
        var currentTokens = Tokens(current.Title + " " + current.SubjectId);

        return _repository.ListIncidents()
            .Where(i => i.Id != current.Id)
            .Select(i =>
            {
                var overlap = Overlap(currentTokens, Tokens(i.Title + " " + i.SubjectId));
                var score = overlap * 0.5
                    + (string.Equals(i.SubjectKind, current.SubjectKind, StringComparison.OrdinalIgnoreCase) ? 0.2 : 0)
                    + (string.Equals(i.SubjectId, current.SubjectId, StringComparison.OrdinalIgnoreCase) ? 0.3 : 0);
                return new SimilarIncident
                {
                    Incident = i, Score = Math.Round(score, 3),
                    FixedBy = i.Status == "resolved" ? i.RootCause : "",
                };
            })
            .Where(s => s.Score >= 0.2)
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.FixedBy.Length) // prefer matches that carry a fix
            .Take(Math.Clamp(top, 1, 10))
            .ToList();
    }

    internal static HashSet<string> Tokens(string text) =>
        text.ToLowerInvariant()
            .Split(new[] { ' ', ':', '/', '.', '-', '_', ',', '(', ')', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .ToHashSet();

    private static double Overlap(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var shared = a.Intersect(b).Count();
        return (double)shared / Math.Min(a.Count, b.Count);
    }

    // ---- Resolution (records the fix memory) ------------------------------------------------------------

    /// <summary>Moves an incident's status; resolving with a root cause writes the durable fix memory.</summary>
    public bool SetStatus(string incidentId, string status, string rootCause, string changedBy)
    {
        var incident = _repository.GetIncident(incidentId);
        if (incident is null) return false;
        var allowed = new[] { "open", "investigating", "resolved" };
        if (!allowed.Contains(status)) return false;

        _repository.SetIncidentStatus(incidentId, status, rootCause, changedBy);
        if (status == "resolved" && !string.IsNullOrWhiteSpace(rootCause))
            _repository.RecordEvent(new HomelabEvent
            {
                EventType = "incident_fix_recorded", SubjectKind = "incident", SubjectId = incidentId,
                Severity = "info",
                Message = $"Fix recorded for '{incident.Title}' ({incident.SubjectId}): {rootCause}",
            });
        return true;
    }
}
