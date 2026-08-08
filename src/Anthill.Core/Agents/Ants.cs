using System.Text.Json;
using System.Text.RegularExpressions;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Models;
using Anthill.Core.Sandbox;
using Anthill.Core.Tools;

namespace Anthill.Core.Agents;

/// <summary>
/// A specialised colony worker. Given a task and a locked mission snapshot, it returns a STRUCTURED
/// outcome.
///
/// v2.19.0: <c>Execute</c> replaces the previous <c>string Run</c>. Until now an ant's only channel
/// back to the orchestrator was prose, so <see cref="AntExecutionResult"/> objects built by the
/// specialists were serialised into text (the old <c>Compat</c> helper) and the executor — which
/// never parsed them — marked every non-throwing task complete. A specialist could report
/// <c>failed_retryable</c> and have the mission record it as a success. Status is now carried by
/// <see cref="AntExecutionResult.StatusCode"/> and nothing else.
///
/// IMPLEMENTATIONS MUST BE STATELESS. One instance per role is shared across the whole colony and
/// parallel execution runs several tasks through the same object concurrently, so per-run state on
/// the ant would race. Everything an execution needs is passed in and returned out.
/// </summary>
public abstract class BaseAnt
{
    public string Name { get; }
    protected BaseAnt(string name) => Name = name;

    /// <summary>
    /// Run the task and report a STRUCTURED outcome. The only execution contract in the colony;
    /// nothing downstream may infer status from prose.
    ///
    /// v3.2.0 (phase, increment 3): ABSTRACT, and the string adapter beside it is gone.
    ///
    /// What was here until now: a <c>string Run(Task, Mission)</c> that every ant also implemented,
    /// and a default <c>Execute</c> that called it and recovered a status by testing the text for
    /// an <c>"ERROR:"</c> prefix — the last such test in the colony. It survived because the
    /// adapter destroyed the status before the result arrived: a typed <see cref="ModelCallResult"/>
    /// came back from the router, was flattened into a string by <c>Run</c>, and the prefix was the
    /// only trace of failure left to read.
    ///
    /// It could be deleted rather than rewritten because all twelve ants already overrode
    /// <c>Execute</c>, so the fallback had no call site — the repo's own rule, applied to the file
    /// that was making the exception for itself. <c>Run</c> is deleted with it (exit gate:
    /// compatibility adapters are deleted, not deprecated); the narrative it returned is
    /// <see cref="AntExecutionResult.Narrative"/>, which is the same text from the same execution.
    ///
    /// IMPLEMENTATIONS MUST BE STATELESS — see the class remarks.
    /// </summary>
    public abstract AntExecutionResult Execute(Task task, Mission mission);

    /// <summary>
    /// Wrap a text-producing ant's output as a structured result.
    ///
    /// The six original ants (researcher, web, file, coder, builder, verifier) legitimately produce
    /// prose or code as their ARTIFACT. What changed is that the ant itself now declares its
    /// outcome in code rather than the orchestrator inferring one from the text: each caller below
    /// decides succeeded / failed and says so explicitly. The narrative remains available to the
    /// operator but carries no control meaning.
    /// </summary>
    protected static AntExecutionResult TextResult(string role, string text, string? summaryOverride = null)
    {
        var body = text ?? "";
        var summary = summaryOverride ?? TextUtil.CreateResultSummary(body, AnthillRuntime.MaxResultSummaryChars);
        // NB: a `with` expression must ASSIGN each member. Collection-initializer syntax
        // (`Artifacts = { ... }`) is only legal inside an object initializer, which is why the
        // same shape works in SpecialistAnts.cs but not here.
        return AntExecutionResult.Succeeded(summary, body) with
        {
            Artifacts = new List<AntArtifact> { new("text", $"{role} output", body) },
            Metrics = new AntMetrics { OutputChars = body.Length },
        };
    }

    /// <summary>
    /// Attach a TYPED artifact to a result, alongside the untyped narrative one. v3.8.21.
    ///
    /// Appends rather than replaces, deliberately. <see cref="TextResult"/> already carries an
    /// <c>AntArtifact("text", ...)</c> — the prose an operator reads — and that stays: the narrative
    /// is still the human record. What this adds is the machine copy, and only where the ant is
    /// holding something with a genuine shape.
    ///
    /// Three of the six core ants can honestly do this and three cannot. <c>FileAnt</c> holds the
    /// paths it read, <c>WebResearchAnt</c> holds <c>SourceRecord</c>s it already persists, and the
    /// coder's output becomes a real <c>PatchSet</c> one layer up. The researcher, builder and
    /// verifier produce prose synthesis — giving that a schema name would be relabelling, which is
    /// the "two channels and the prose one wins" failure ADR-004 exists to prevent.
    /// </summary>
    protected static AntExecutionResult WithArtifact(
        AntExecutionResult result, string kind, string title, string content) =>
        result with
        {
            Artifacts = result.Artifacts.Concat(new[] { new AntArtifact(kind, title, content) }).ToList(),
        };

    // ModelUnavailable() lived here and is deleted with the adapter that was its only caller.
    //
    // It existed to recover a status the string contract had destroyed, so it had to answer for
    // every role at once — one generic "routed model call failed" for twelve ants with genuinely
    // different stakes. Each ant now branches on ModelCallResult.Ok at its own call site and says
    // what a dead provider means for ITS work: the researcher and builder degrade to a local
    // answer and disclose it as a structured warning, because partial context still helps; the
    // coder fails the task, because a patch proposal invented without the model would be worse
    // than none. A shared helper could not have made that distinction.
}

public sealed class ResearcherAnt : BaseAnt
{
    private readonly SqliteMemory _memory;
    private readonly ToolRegistry _tools;
    private readonly ModelRouter? _router;

    public ResearcherAnt(SqliteMemory memory, ToolRegistry tools, ModelRouter? router) : base("researcher")
    {
        _memory = memory; _tools = tools; _router = router;
    }


    public override AntExecutionResult Execute(Task task, Mission mission)
    {
        var recentMemory = _memory.FormatRecentMemory(AnthillRuntime.RecentMemoryLimit, AnthillRuntime.MemoryResultChars);
        var relevantMemory = _memory.FormatRelevantMemory(mission.Goal, AnthillRuntime.RelevantMemoryLimit, AnthillRuntime.MemoryResultChars);
        var pheromoneContext = _memory.FormatPheromoneContext(8);
        var toolResults = new List<ToolResult> { _tools.RunTool("system_info", mission.Id, task.Id, Name) };
        if (ShouldInspectWorkspace(task, mission))
            toolResults.Add(_tools.RunTool("list_directory", mission.Id, task.Id, Name, new() { ["path"] = "." }));

        var rawContext = $"Mission: {mission.Goal}\n\nTask: {task.Description}\n\nRecent Memory:\n{recentMemory}\n\n" +
                         $"Relevant Memory:\n{relevantMemory}\n\nPheromone Trails:\n{pheromoneContext}\n\n" +
                         $"Tool Context:\n{FormatToolReport(toolResults)}";
        rawContext = TextUtil.Truncate(rawContext, AnthillRuntime.MaxContextPacketChars, "...[research context truncated]");

        if (_router is null || !AnthillRuntime.UseOllama)
        {
            // Configured-offline mode: the local summary IS the deliverable — a plain success.
            var offline = "Researcher Ant summarized local context without LLM routing.\n\n" + rawContext +
                   $"\n\nResearch Finding:\nANTHILL v{AnthillRuntime.Version} supports read-only external research when web search is enabled.";
            return TextResult(Name, offline);
        }

        var prompt = $@"{AnthillRuntime.PromptInjectionPrefix}
ANTHILL v{AnthillRuntime.Version} | role: researcher | timestamp: {AnthillTime.NowUtc().ToIso()} | mission: {TextUtil.Truncate(mission.Goal, 180)}
You are concise. Do not explain your reasoning unless asked.

Summarize only the context that is relevant to the mission below.
Do not repeat the mission goal back to the user.
Produce a tight context brief for downstream ants.
Aim for 150-300 words unless more is required.

Context:
{rawContext}

Return format:
- Relevant Memory:
- Useful Tool Context:
- Pheromone Guidance:
- Research Need:
";
        var call = _router.GenerateTyped("researcher", prompt, mission.Id, task.Id, Name);
        if (call.Status == ModelCallOutcome.Empty)
            return AntExecutionResult.Failed(FailureClass.TransientProviderFailure,
                "Researcher: routed model returned an empty response.");
        if (!call.Ok)
        {
            // The provider failed but the local context is genuinely useful — succeeded WITH the
            // degradation disclosed as a structured warning, not silently as ordinary success and
            // not a task failure that would discard usable context.
            var fallback = "Researcher routed model unavailable. Fallback context brief:\n\n" + rawContext;
            return AntExecutionResult.SucceededWithWarnings(
                "Researcher fell back to a local context brief (routed model unavailable).",
                new[] { $"provider_failure[{call.Status.Name()}]: {TextUtil.Truncate(call.Content, 300)}" }, fallback);
        }
        return TextResult(Name, call.Content);
    }

    private static bool ShouldInspectWorkspace(Task task, Mission mission)
    {
        var text = $"{mission.Goal} {task.Title} {task.Description}".ToLowerInvariant();
        var keywords = new[] { "file", "folder", "directory", "workspace", "project", "code", "script", "python", "repo", "repository", "debug", "error", "config", "read", "inspect", "list", "show", "look at", "open", "tree", "structure", "patch" };
        return keywords.Any(text.Contains);
    }

    private static string FormatToolReport(List<ToolResult> results) =>
        string.Join("\n\n---\n\n", results.Select(r => r.Success
            ? $"Tool: {r.ToolName}\nSuccess: {r.Success}\nOutput:\n{r.Output}"
            : $"Tool: {r.ToolName}\nSuccess: {r.Success}\nError:\n{r.Error}"));
}

/// <summary>
/// Heuristic source quality scoring (relevance/authority/freshness → confidence). Intentionally
/// advisory, not censorship; later versions can replace it with learned source pheromones.
/// </summary>
public sealed class SourceQualityEngine
{
    // v2.26.0: recency years derive from the CLOCK — hard-coded "2025"/"2026" would rot into
    // staleness hints the moment the calendar moved on.
    private static readonly HashSet<string> RecentHints = new(
        new[] { "latest", "current", "release", "updated", "today", "recent" }
            .Concat(new[] { AnthillTime.NowUtc().Year.ToString(), (AnthillTime.NowUtc().Year - 1).ToString() }));
    private static readonly HashSet<string> StaleHints = new() { "2019", "2018", "2017", "2016", "2015", "archived", "deprecated" };

    public Dictionary<string, object?> Score(string goal, string title, string url, string snippet)
    {
        var decodedUrl = UrlSafety.DecodeSearchUrl(url);
        var domain = UrlSafety.ExtractDomain(decodedUrl);
        var text = $"{title} {decodedUrl} {snippet}".ToLowerInvariant();
        var goalKeywords = TextUtil.ExtractKeywords(goal);
        var sourceKeywords = TextUtil.ExtractKeywords(text);

        var relevance = 0.25;
        if (goalKeywords.Count > 0)
        {
            var overlap = goalKeywords.Intersect(sourceKeywords).Count();
            relevance = Math.Min(1.0, 0.25 + (double)overlap / Math.Max(4, goalKeywords.Count));
        }

        var authority = 0.35;
        if (AnthillRuntime.SourceAllowlistDomains.Contains(domain)) authority = 0.95;
        else if (AnthillRuntime.HighAuthorityDomainSuffixes.Any(domain.EndsWith)) authority = 0.85;
        else if (AnthillRuntime.HighAuthorityDomainKeywords.Any(domain.Contains)) authority = 0.75;
        else if (AnthillRuntime.SourceBlocklistDomains.Contains(domain)) authority = 0.05;

        var freshness = 0.5;
        if (RecentHints.Any(text.Contains)) freshness = 0.8;
        if (StaleHints.Any(text.Contains)) freshness = 0.25;

        var confidence = Math.Round(relevance * 0.45 + authority * 0.35 + freshness * 0.20, 3);
        var label = confidence >= 0.78 ? "high" : confidence >= 0.55 ? "medium" : "low";

        var notes = new List<string>();
        if (AnthillRuntime.SourceAllowlistDomains.Contains(domain)) notes.Add("allowlist authority boost");
        if (AnthillRuntime.SourceBlocklistDomains.Contains(domain)) notes.Add("blocklisted domain");
        if (goalKeywords.Count > 0 && !goalKeywords.Overlaps(sourceKeywords)) notes.Add("low keyword overlap");
        if (StaleHints.Any(text.Contains)) notes.Add("possible stale source");
        if (notes.Count == 0) notes.Add("heuristic score");

        return new Dictionary<string, object?>
        {
            ["domain"] = domain, ["url"] = decodedUrl, ["relevance_score"] = Math.Round(relevance, 3),
            ["freshness_score"] = Math.Round(freshness, 3), ["authority_score"] = Math.Round(authority, 3),
            ["confidence_score"] = confidence, ["confidence_label"] = label, ["quality_notes"] = string.Join("; ", notes),
        };
    }

    public bool ShouldSkip(string domain) => AnthillRuntime.SourceBlocklistDomains.Contains(domain);
}

public sealed class WebResearchAnt : BaseAnt
{
    private readonly SqliteMemory _memory;
    private readonly ToolRegistry _tools;
    private readonly ModelRouter? _router;
    private readonly SourceQualityEngine _quality = new();

    public WebResearchAnt(SqliteMemory memory, ToolRegistry tools, ModelRouter? router) : base("web")
    {
        _memory = memory; _tools = tools; _router = router;
    }


    public override AntExecutionResult Execute(Task task, Mission mission)
    {
        // v2.26.0: each terminal below declares its own class — an exhausted budget is a SKIP, a
        // provider failure is retryable, zero usable sources is NOT an ordinary success. "Search
        // ran but saved nothing" used to read as a completed research task.
        var existingSources = _memory.CountSourcesForMission(mission.Id);
        if (existingSources >= AnthillRuntime.MaxSourcesPerMission)
            return AntExecutionResult.Skipped(
                $"Web search skipped: mission source budget exhausted ({existingSources}/{AnthillRuntime.MaxSourcesPerMission}).");

        var existingSearches = _memory.CountWebSearchAttemptsForMission(mission.Id);
        if (existingSearches >= AnthillRuntime.MaxWebSearchesPerMission)
        {
            _memory.LogEvent(mission.Id, "web_search_budget_exhausted",
                "WebResearchAnt skipped search because the mission web-search attempt budget is exhausted.", task.Id, Name,
                new() { ["web_search_attempts"] = existingSearches, ["max_web_searches_per_mission"] = AnthillRuntime.MaxWebSearchesPerMission });
            return AntExecutionResult.Skipped(
                $"Web search skipped: attempt budget exhausted ({existingSearches}/{AnthillRuntime.MaxWebSearchesPerMission}).");
        }

        if (!AnthillRuntime.EnableWebSearch)
            return AntExecutionResult.Blocked(
                "Web search is disabled (web_search_enabled=false) — external research cannot run.");

        var query = BuildQuery(task, mission);
        _memory.LogEvent(mission.Id, "web_search_attempted", "WebResearchAnt requested read-only external research.", task.Id, Name,
            new() { ["query"] = query, ["attempt_number"] = existingSearches + 1, ["max_web_searches_per_mission"] = AnthillRuntime.MaxWebSearchesPerMission, ["max_sources_per_search"] = AnthillRuntime.MaxSourcesPerSearch });

        var result = _tools.RunTool("web_search", mission.Id, task.Id, Name,
            new() { ["query"] = query, ["max_results"] = Math.Min(AnthillRuntime.MaxWebResults, AnthillRuntime.MaxSourcesPerSearch) });
        if (!result.Success)
            return AntExecutionResult.Failed(FailureClass.TransientProviderFailure,
                $"Web search provider failed for query '{TextUtil.Truncate(query, 120)}': {TextUtil.Truncate(result.Error ?? "unknown error", 300)}");

        JsonElement payload;
        try { payload = JsonDocument.Parse(string.IsNullOrEmpty(result.Output) ? "{}" : result.Output).RootElement; }
        catch { payload = JsonDocument.Parse("{\"results\":[]}").RootElement; }

        var savedSources = new List<SourceRecord>();
        var seenUrls = new HashSet<string>();
        var skipped = 0;

        var items = payload.TryGetProperty("results", out var resultsEl) && resultsEl.ValueKind == JsonValueKind.Array
            ? resultsEl.EnumerateArray().Take(AnthillRuntime.MaxSourcesPerSearch).ToList()
            : new List<JsonElement>();

        foreach (var item in items)
        {
            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "Untitled source" : "Untitled source";
            var rawUrl = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var url = UrlSafety.DecodeSearchUrl(rawUrl);
            var snippet = item.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(url) || UrlSafety.IsBlockedOutboundUrl(url)) { skipped++; continue; }

            var dedupeKey = UrlSafety.NormalizeUrlForDedupe(url);
            if (!seenUrls.Add(dedupeKey)) { skipped++; continue; }

            var quality = _quality.Score(mission.Goal, title, url, snippet);
            var domain = quality["domain"]!.ToString()!;
            if (_quality.ShouldSkip(domain)) { skipped++; continue; }

            var summary = SummarizeSource(mission.Goal, title, url, snippet, quality);
            var source = new SourceRecord
            {
                Id = UrlSafety.SourceIdFromUrl(url), MissionId = mission.Id, TaskId = task.Id, AntName = Name,
                Title = title, Url = url, Domain = domain,
                Snippet = TextUtil.Truncate(snippet, AnthillRuntime.MaxSourceSummaryChars),
                Summary = TextUtil.Truncate(summary, AnthillRuntime.MaxSourceSummaryChars), Provider = AnthillRuntime.WebSearchProvider,
                RelevanceScore = Convert.ToDouble(quality["relevance_score"]), FreshnessScore = Convert.ToDouble(quality["freshness_score"]),
                AuthorityScore = Convert.ToDouble(quality["authority_score"]), ConfidenceScore = Convert.ToDouble(quality["confidence_score"]),
                ConfidenceLabel = quality["confidence_label"]!.ToString()!, QualityNotes = quality["quality_notes"]!.ToString()!,
            };
            _memory.SaveSourceRecord(source);
            savedSources.Add(source);

            var sourceDelta = source.ConfidenceScore >= 0.55 ? 0.02 : -0.005;
            _memory.UpdatePheromoneTrail($"source_domain:{source.Domain}", "source_domain", source.ConfidenceScore >= 0.55, sourceDelta,
                new() { ["mission_id"] = mission.Id, ["task_id"] = task.Id, ["source_id"] = source.Id, ["confidence_score"] = source.ConfidenceScore, ["confidence_label"] = source.ConfidenceLabel });
        }

        _memory.UpdatePheromoneTrail($"tool:web_search:{AnthillRuntime.WebSearchProvider}", "external_research_tool",
            savedSources.Count > 0, savedSources.Count > 0 ? 0.02 : -0.01,
            new() { ["mission_id"] = mission.Id, ["task_id"] = task.Id, ["query"] = query, ["source_count"] = savedSources.Count, ["skipped_count"] = skipped });

        if (savedSources.Count == 0)
        {
            var preview = payload.TryGetProperty("preview", out var p) ? p.GetString() ?? "No parsed search results returned." : "No parsed search results returned.";
            // The search RAN and produced nothing usable. That is a failed research task — the
            // deliverable (saved sources) does not exist — and retryable, because a different
            // provider window may answer. It must never read as ordinary success.
            return AntExecutionResult.Failed(FailureClass.TransientProviderFailure,
                $"Search ran but saved zero usable sources (query '{TextUtil.Truncate(query, 120)}', {skipped} skipped/filtered). "
                + $"Preview: {TextUtil.Truncate(preview, 300)}");
        }

        var lines = new List<string>
        {
            $"WebResearchAnt saved {savedSources.Count} source record(s).", $"Query: {query}", $"Skipped/Deduped/Filtered: {skipped}",
        };
        lines.AddRange(savedSources.Select(src =>
            $"Source ID: {src.Id}\nTitle: {src.Title}\nDomain: {src.Domain}\nConfidence: {src.ConfidenceLabel} ({src.ConfidenceScore})\nURL: {src.Url}\nSummary: {src.Summary}"));
        var narrative = string.Join("\n\n---\n\n", lines);

        // Sourced research succeeded; uniformly low-confidence sources succeed WITH the caveat
        // disclosed — advisory quality, never silently equated with proven truth.
        var allLow = savedSources.All(src => src.ConfidenceScore < 0.55);
        var researched = allLow
            ? AntExecutionResult.SucceededWithWarnings($"Saved {savedSources.Count} source record(s), all low-confidence.",
                new[] { "low_confidence_sources: every saved source scored below 0.55 heuristic confidence" }, narrative)
            : TextResult(Name, narrative);

        // v3.8.21 — the sources as data rather than as a rendered list. Confidence travels with each
        // one, because a source set whose scores are stripped reads as stronger than it is.
        return WithArtifact(researched, "source_set", "Sources consulted",
            Json.Dumps(new
            {
                query,
                sources = savedSources.Select(src => new
                {
                    src.Title, src.Url, src.Domain, confidence = src.ConfidenceScore,
                }),
            }, indented: true));
    }

    private static string BuildQuery(Task task, Mission mission) =>
        TextUtil.Truncate($"{mission.Goal} {task.Description}".Trim(), 300, "");

    private string SummarizeSource(string goal, string title, string url, string snippet, Dictionary<string, object?> quality)
    {
        var baseText = $"Title: {title}\nURL: {url}\nSnippet: {snippet}\n" +
                       $"Quality: confidence={quality.GetValueOrDefault("confidence_score")} label={quality.GetValueOrDefault("confidence_label")} notes={quality.GetValueOrDefault("quality_notes")}";
        if (_router is null || !AnthillRuntime.UseOllama)
            return TextUtil.Truncate(string.IsNullOrEmpty(snippet) ? title : snippet, AnthillRuntime.MaxSourceSummaryChars);
        var prompt = $@"{AnthillRuntime.PromptInjectionPrefix}
ANTHILL v{AnthillRuntime.Version} | role: web | timestamp: {AnthillTime.NowUtc().ToIso()} | mission: {TextUtil.Truncate(goal, 180)}
You are concise. Do not explain your reasoning unless asked.

Summarize why this source may be relevant to the mission in 1-3 sentences.
Include any obvious freshness or authority caveat.
Do not invent details beyond the title/snippet/url/quality fields.

Source:
{baseText}
";
        var summary = _router.GenerateTyped("web", prompt, antName: "web");
        var response = summary.Content;
        return !summary.Ok
            ? TextUtil.Truncate(string.IsNullOrEmpty(snippet) ? title : snippet, AnthillRuntime.MaxSourceSummaryChars)
            : TextUtil.Truncate(response, AnthillRuntime.MaxSourceSummaryChars);
    }
}

public sealed partial class FileAnt : BaseAnt
{
    private readonly ToolRegistry _tools;
    public FileAnt(ToolRegistry tools) : base("file") => _tools = tools;


    // v2.26.0: an inspection whose every tool call failed is a FAILED inspection, not a successful
    // narrative about failure. Partial read failures and paths-not-found are disclosed as warnings.
    public override AntExecutionResult Execute(Task task, Mission mission)
    {
        var directoryResult = _tools.RunTool("list_directory", mission.Id, task.Id, Name, new() { ["path"] = "." });
        var candidatePaths = ShouldAttemptFileReads(task, mission) ? ExtractCandidatePaths(task, mission) : new List<string>();
        var fileReports = new List<string>();
        int readsOk = 0, readsFailed = 0;
        foreach (var path in candidatePaths.Take(AnthillRuntime.MaxFileAntFilesToRead))
        {
            var readResult = _tools.RunTool("read_text_file", mission.Id, task.Id, Name, new() { ["path"] = path });
            if (readResult.Success) readsOk++; else readsFailed++;
            fileReports.Add(readResult.Success
                ? $"File: {path}\nRead Success: True\nContent:\n{readResult.Output}"
                : $"File: {path}\nRead Success: False\nError:\n{readResult.Error}");
        }
        if (fileReports.Count == 0)
            fileReports.Add("FileAnt did not identify specific readable file paths. It only listed workspace structure.");

        var narrative = $"FileAnt performed safe read-only workspace inspection.\n\nMission:\n{mission.Goal}\n\nAssigned Task:\n{task.Description}\n\n" +
               $"Workspace Listing:\nSuccess: {directoryResult.Success}\n{(directoryResult.Success ? directoryResult.Output : directoryResult.Error)}\n\n" +
               $"File Read Reports:\n{string.Join("\n\n---\n\n", fileReports)}" +
               "\n\nFileAnt did not write, modify, delete, execute, or patch any files.";

        // Every tool call failed — listing AND all attempted reads. Nothing was inspected.
        if (!directoryResult.Success && candidatePaths.Count > 0 && readsOk == 0)
            return AntExecutionResult.Failed(FailureClass.DependencyFailure,
                $"Workspace inspection failed: the directory listing and all {readsFailed} file read(s) failed. "
                + $"Listing error: {TextUtil.Truncate(directoryResult.Error ?? "unknown", 200)}");
        if (!directoryResult.Success && candidatePaths.Count == 0)
            return AntExecutionResult.Failed(FailureClass.DependencyFailure,
                $"Workspace inspection failed: the directory listing failed and no file paths were identified. "
                + $"Listing error: {TextUtil.Truncate(directoryResult.Error ?? "unknown", 200)}");

        var warnings = new List<string>();
        if (readsFailed > 0) warnings.Add($"partial_read_failures: {readsFailed} of {readsOk + readsFailed} file read(s) failed");
        if (candidatePaths.Count == 0 && ShouldAttemptFileReads(task, mission))
            warnings.Add("no_target_paths: the mission suggested file reads but named no identifiable paths — listing only");
        var inspected = warnings.Count > 0
            ? AntExecutionResult.SucceededWithWarnings($"Workspace inspected: listing ok, {readsOk} read(s) ok, {readsFailed} failed.", warnings, narrative)
            : TextResult(Name, narrative);

        // v3.8.21 — the paths this ant actually read, as data. It has held them all along; they were
        // only ever reachable by parsing the narrative back out, which is exactly what a typed
        // artifact removes the need to do.
        return candidatePaths.Count == 0
            ? inspected
            : WithArtifact(inspected, "file_set", "Files examined",
                           Json.Dumps(new { files = candidatePaths, read_ok = readsOk, read_failed = readsFailed }, indented: true));
    }

    private static bool ShouldAttemptFileReads(Task task, Mission mission)
    {
        var text = $"{mission.Goal} {task.Title} {task.Description}".ToLowerInvariant();
        var keywords = new[] { "read", "open", "inspect", "review", "check", "debug", "analyze", "look at", "show me", "this file", "my file", "script", "code", "repo", "repository", "project", "patch" };
        return keywords.Any(text.Contains);
    }

    private static List<string> ExtractCandidatePaths(Task task, Mission mission)
    {
        var text = $"{mission.Goal}\n{task.Title}\n{task.Description}";
        var candidates = new List<string>();
        candidates.AddRange(QuotedPath().Matches(text).Select(m => m.Groups[1].Value));
        candidates.AddRange(SuffixPath().Matches(text).Select(m => m.Value));
        // (An old heuristic injected "anthill.py" here whenever the mission mentioned "anthill" or
        // "script" — a relic of the Python build that biased the whole colony toward Python. Gone:
        // candidate paths now come only from what the mission text actually names.)
        var cleaned = new List<string>();
        var seen = new HashSet<string>();
        foreach (var raw in candidates)
        {
            var candidate = raw.Trim().Trim('.', ',', ';', ':', '(', ')', '[', ']', '{', '}');
            if (candidate.Length > 0 && seen.Add(candidate)) cleaned.Add(candidate);
        }
        return cleaned;
    }

    [GeneratedRegex("['\"]([^'\"]+\\.[A-Za-z0-9]+)['\"]")] private static partial Regex QuotedPath();
    // Keep this suffix list aligned with AnthillRuntime.PatchAllowedSuffixes so the file ant can
    // spot (and read) every file type the coder is allowed to patch — most importantly the
    // colony's own C#/.NET sources for self-modification missions.
    [GeneratedRegex(@"\b[\w\-/\\.]+\.(?:cs|csproj|sln|props|targets|py|txt|md|json|yaml|yml|toml|ini|cfg|log|csv|html|css|js|ts|tsx|jsx|xml|sh|bat|ps1|cmd|go|rs|java|kt|rb|php|tf|hcl|sql)\b")] private static partial Regex SuffixPath();
}

public sealed class CoderAnt : BaseAnt
{
    private readonly bool _useOllama;
    private readonly ModelRouter? _router;
    public CoderAnt(bool useOllama, ModelRouter? router) : base("coder") { _useOllama = useOllama; _router = router; }


    // v2.26.0: a file-change task that returns zero proposals is NOT a success — the coder exists
    // to produce patches. Classification is by parsing the coder's OWN JSON artifact (counting its
    // proposals), never by reading intent out of narrative prose.
    public override AntExecutionResult Execute(Task task, Mission mission)
    {
        var constraints = MissionConstraints.Parse(mission.Goal);
        if (constraints.BlocksPatches)
            return AntExecutionResult.Blocked(
                "Coder admitted to a read-only / no-patch mission — the planner must not assign coder tasks here, "
                + "and the coder refuses rather than proposing changes the operator forbade.");

        var codeContext = DomainHelpers.BuildContextPacketText(mission, "coder", Math.Min(AnthillRuntime.MaxCoderContextChars, AnthillRuntime.MaxContextPacketChars));
        if (!_useOllama || _router is null)
            return AntExecutionResult.Failed(FailureClass.TransientProviderFailure,
                "Coder cannot produce patch proposals: model routing/LLM generation is unavailable.");

        // v2.11.1: when the sandbox gate is on, iterate inside a disposable sandbox (propose ->
        // apply IN THE SANDBOX -> build -> refine on failure) and return proposals that verified —
        // the same patch JSON the approval pipeline already consumes. The live workspace is never
        // touched; any unavailability falls through to the unchanged one-shot path below.
        if (AnthillRuntime.EnableSandboxExecution)
        {
            var sandboxed = TryRunSandboxed(task, mission, codeContext);
            if (sandboxed is not null) return ClassifyPatchJson(sandboxed);
        }

        var call = _router.GenerateTyped("coder", BuildPrompt(task, mission, codeContext, ""), mission.Id, task.Id, Name);
        if (!call.Ok)
            return AntExecutionResult.Failed(FailureClass.TransientProviderFailure,
                $"Coder could not reach the routed model ({call.Status.Name()}) — no patch proposals created. {TextUtil.Truncate(call.Content, 300)}");
        return ClassifyPatchJson(call.Content);
    }

    /// <summary>Classify the coder's own JSON artifact by its proposal count: proposals → success;
    /// well-formed but empty → failed (the deliverable does not exist — an intentionally empty
    /// result must say so in its summary); unparseable → failed (malformed output).</summary>
    internal static AntExecutionResult ClassifyPatchJson(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return AntExecutionResult.Failed(FailureClass.TransientProviderFailure,
                "Coder: routed model returned an empty response.");
        try
        {
            var parsed = Json.ExtractJsonObject(response);
            var count = parsed["proposals"] is System.Text.Json.Nodes.JsonArray arr ? arr.Count : 0;
            if (count > 0)
                return AntExecutionResult.Succeeded($"Coder produced {count} patch proposal(s).", response) with
                {
                    Artifacts = new List<AntArtifact> { new("patch_json", "coder patch proposals", response) },
                    Metrics = new AntMetrics { OutputChars = response.Length },
                };
            var summary = parsed["summary"]?.GetValue<string>() ?? "";
            return AntExecutionResult.Failed(FailureClass.InternalDefect,
                $"Coder returned zero patch proposals for a patch task. Coder's stated reason: "
                + $"{TextUtil.Truncate(summary.Length > 0 ? summary : "(none given)", 300)}");
        }
        catch
        {
            return AntExecutionResult.Failed(FailureClass.InternalDefect,
                $"Coder returned malformed patch output (not parseable JSON): {TextUtil.Truncate(response, 200)}");
        }
    }

    /// <summary>Runs the bounded sandbox loop for this coder task. Returns the coder's best patch
    /// JSON (verified in-sandbox when the loop completed) for the existing approval pipeline, or
    /// null to fall back to the one-shot path (gate off/refused, or no usable workspace root).</summary>
    private string? TryRunSandboxed(Task task, Mission mission, string codeContext)
    {
        var sourceRoot = AnthillRuntime.AllowedWorkspaceRoot;
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot)) return null;

        var lastProposalJson = FallbackPatchJson("CoderAnt sandbox run produced no verified proposals.");
        var runner = new SandboxedCoderRunner(
            (turn, feedback) =>
            {
                // v3.2.0: one status decides both branches. Before, two independent prefix tests
                // could disagree — an empty generation passed the first (so it was cached as the
                // last good proposal) and passed the second (so it was handed on as a patch set).
                var call = _router!.GenerateTyped("coder", BuildPrompt(task, mission, codeContext, feedback), mission.Id, task.Id, Name);
                if (call.Ok) lastProposalJson = call.Content;
                return call.Ok
                    ? call.Content
                    : FallbackPatchJson($"Model {call.Status.Name()} during sandbox iteration: {call.Content}");
            },
            checkId: "dotnet_build");

        var report = runner.Run(sourceRoot, mission.Id, task.Id, ModelCallScope.Current);
        if (report.StopReason is "disabled" or "refused") return null; // let the caller do the one-shot

        // v3.0.1: surface the sandbox loop outcome — the report was previously discarded, making
        // sandbox execution invisible (you could not tell whether the in-sandbox build even ran).
        // One structured line to the app log per coder task keeps the loop observable + debuggable.
        Console.WriteLine($"[sandbox] coder task={task.Id} stop={report.StopReason} turns={report.Turns} "
            + $"toolCalls={report.ToolCalls} verified={report.Verified} check={report.CheckId} "
            + $"diff={TextUtil.Truncate(report.ChangeSummary.Replace('\n', ' '), 300)}");

        // Verified (built green inside the sandbox) or best-effort on budget exhaustion — either way
        // these proposals remain human-approval-gated before anything touches the live tree.
        return lastProposalJson;
    }

    private static string BuildPrompt(Task task, Mission mission, string codeContext, string feedback)
    {
        var prompt = $@"{AnthillRuntime.PromptInjectionPrefix}
ANTHILL v{AnthillRuntime.Version} | role: coder | timestamp: {AnthillTime.NowUtc().ToIso()} | mission: {TextUtil.Truncate(mission.Goal, 180)}
You are concise. Do not explain your reasoning unless asked.

You are Coder Ant inside ANTHILL v{AnthillRuntime.Version}.

Your role:
Create structured patch proposals as JSON only.

Limits:
- You do not write files.
- You do not run shell commands.
- You do not apply patches.
- You only propose patches.
- Patch application happens later through /apply after approval and config gates.

Mission goal:
{mission.Goal}

Assigned task:
{task.Title}
{task.Description}

Prior context:
{codeContext}

Return ONLY valid JSON.

Required format:
{{
  ""summary"": ""Brief summary."",
  ""proposals"": [
    {{
      ""file_path"": ""<workspace-relative path to a REAL file, e.g. src/Foo/Bar.cs or docs/notes.md>"",
      ""change_type"": ""modify"",
      ""reason"": ""Why this change is recommended."",
      ""risk"": ""Risk level and what should be reviewed."",
      ""old_content"": ""Exact old content for modify, or null for add."",
      ""new_content"": ""Proposed new content."",
      ""requires_approval"": true
    }}
  ]
}}

Allowed change_type values:
add, modify, delete, rename

Rules:
- Match the programming language, file types, and conventions of the files shown in the prior
  context and mission goal. Never assume a language: if the context shows C# (.cs), propose C#;
  if it shows Python (.py), propose Python. If no existing code is visible in context and the
  goal doesn't name a language, return an empty proposals list instead of guessing a language.
- Prefer modify or add.
- file_path MUST be a real workspace-relative path with a real filename and extension (e.g.
  src/App/Thing.cs, docs/notes.md). NEVER output a placeholder like ""file.ext"", "".ext"", or
  ""path/to/file"" — a proposal whose path you cannot name concretely should be omitted instead.
- In new_content and old_content, escape every newline as \n — do NOT put raw line breaks inside a
  JSON string value. The whole response must be a single line of valid JSON.
- If you are unsure of the exact old_content, return an empty proposals list rather than guessing.
- Do not wrap JSON in markdown code fences.
- For modify, old_content must be exact and unambiguous.
- For add, old_content should be null.
- Choose change_type by whether the target file ALREADY EXISTS: use modify (with exact old_content)
  to change a file that exists; use add ONLY for a file that does not exist yet. Never propose add
  for a path that already exists in the context or workspace — to append to or edit an existing file,
  use modify with the exact surrounding old_content you are replacing.
- Do not propose database, .git, venv, cache, or absolute paths.
- Do not propose paths containing ..
- Every proposal requires approval.
- If context is incomplete, return an empty proposals list.
";
        if (!string.IsNullOrWhiteSpace(feedback))
            prompt += $@"

Your previous patch attempt was applied in an isolated sandbox and the verification check FAILED.
Fix the underlying cause and return a corrected patch (exact old_content for modify; add only for
new files). Do not repeat the same change that just failed.
--- sandbox check feedback ---
{feedback}
";
        return prompt;
    }

    private static string FallbackPatchJson(string summary) =>
        Json.Dumps(new { summary, proposals = Array.Empty<object>() }, indented: true);
}

public sealed class BuilderAnt : BaseAnt
{
    private readonly bool _useOllama;
    private readonly ModelRouter? _router;
    public BuilderAnt(bool useOllama, ModelRouter? router) : base("builder") { _useOllama = useOllama; _router = router; }


    public override AntExecutionResult Execute(Task task, Mission mission)
    {
        var previousContext = DomainHelpers.BuildContextPacketText(mission, "builder", Math.Min(AnthillRuntime.MaxPreviousContextChars, AnthillRuntime.MaxContextPacketChars));
        // Configured-offline: the static response IS the configured behaviour — plain success.
        if (!_useOllama || _router is null) return TextResult(Name, FallbackResponse(task, mission, previousContext));

        var prompt = $@"{AnthillRuntime.PromptInjectionPrefix}
ANTHILL v{AnthillRuntime.Version} | role: builder | timestamp: {AnthillTime.NowUtc().ToIso()} | mission: {TextUtil.Truncate(mission.Goal, 180)}
You are concise. Do not explain your reasoning unless asked.

You are Builder Ant inside ANTHILL v{AnthillRuntime.Version}.

Mission goal:
{mission.Goal}

Assigned task:
{task.Title}
{task.Description}

Prior context:
{previousContext}

Create a practical final response.

Rules:
- Lead with the direct answer before any explanation.
- Do not repeat the mission goal back to the user.
- Aim for 200-400 words unless the task requires more.
- Be direct.
- Do not claim files were changed unless /apply actually ran.
- If patch proposals exist, say they can be inspected with /patches and /patch <patch_id>.
- Explain that approved patches can be applied with /apply <approval_id> only if config write gates are enabled.
- Mention that ANTHILL supports dependency-aware parallel execution, FTS memory search, and role-based model routing.
";
        var call = _router.GenerateTyped("builder", prompt, mission.Id, task.Id, Name);
        if (call.Status == ModelCallOutcome.Empty)
            return AntExecutionResult.Failed(FailureClass.TransientProviderFailure,
                "Builder: routed model returned an empty response.");
        if (!call.Ok)
            // v2.26.0: the fallback still answers, but degraded generation is DISCLOSED as a
            // structured warning rather than buried in the prose.
            return AntExecutionResult.SucceededWithWarnings(
                "Builder fell back to a non-LLM response (routed model unavailable).",
                new[] { $"provider_failure[{call.Status.Name()}]: {TextUtil.Truncate(call.Content, 300)}" },
                $"{call.Content}\n\nFallback Builder Response:\n{FallbackResponse(task, mission, previousContext)}");
        return TextResult(Name, call.Content);
    }

    private static string FallbackResponse(Task task, Mission mission, string previousContext) =>
        $"Builder Ant created a basic non-LLM response.\n\nMission Goal:\n{mission.Goal}\n\nAssigned Task:\n{task.Title}\n{task.Description}\n\n" +
        $"Previous Context:\n{previousContext}\n\nProposed Output:\n" +
        "1. Review patch proposals using /patches and /patch <patch_id>.\n" +
        "2. Approve with /approve <approval_id>.\n" +
        "3. Apply with /apply <approval_id> only after enabling write gates.\n" +
        "4. ANTHILL can run eligible independent tasks in parallel, uses FTS5 when available, and routes model calls by role.";
}

public sealed class VerifierAnt : BaseAnt
{
    private readonly bool _useOllama;
    private readonly ModelRouter? _router;
    private readonly Anthill.SDK.Artifacts.IEvidenceStore? _evidence;

    /// <summary>
    /// v3.8.27: the evidence store, so the verdict can be READ rather than asked for.
    ///
    /// Optional for the same reason the soldier's is — dozens of tests and the CLI construct a
    /// verifier without one, and in that configuration it behaves exactly as it always has. A
    /// required dependency would have rewritten every call site to gain a capability none of them
    /// exercise.
    /// </summary>
    public VerifierAnt(bool useOllama, ModelRouter? router,
        Anthill.SDK.Artifacts.IEvidenceStore? evidence = null) : base("verifier")
    {
        _useOllama = useOllama; _router = router; _evidence = evidence;
    }

    /// <summary>
    /// The verdict this mission's stored evidence supports, or null when there is no store to ask.
    /// A read failure is null too: a verifier that refused to run because the store hiccuped would
    /// be worse than one falling back to the static check it has always had.
    /// </summary>
    private Outcomes.EvidenceVerdict.Result? ReadEvidenceVerdict(Mission mission)
    {
        if (_evidence is null) return null;
        try { return Outcomes.EvidenceVerdict.For(_evidence.ForMission(mission.Id)); }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[verifier] could not read evidence for {mission.Id}: {error.Message}");
            return null;
        }
    }

    /// <summary>
    /// v2.19.0: the verdict is now declared, not left for a reader to infer. Run() still returns
    /// the identical text (operators and the mission thread depend on the full verdict, reasoning,
    /// missing steps and risk notes), but Execute() classifies it so MissionVerification can gate
    /// on a real PASS instead of on "a verification task completed".
    ///
    /// A non-passing verdict is NOT a task failure: the verification ran correctly and produced a
    /// finding. Failing the task would replace the full verdict text with a one-line reason in
    /// ApplyNonCompletingOutcome, destroying exactly the explanation the operator needs. The
    /// verdict travels as evidence and warnings instead, and the mission gate reads it.
    /// </summary>
    public override AntExecutionResult Execute(Task task, Mission mission)
    {
        var text = Compose(task, mission);

        // v3.8.27 — THE EVIDENCE DECIDES; the model describes.
        //
        // Until this release the verdict was `VerificationVerdict.Parse(text)` — a search for the
        // phrase "verification passed" in whatever the model wrote. That was the LAST place model
        // output reached a verification decision, and it stood while everything underneath it was
        // built to make it unnecessary: the store (v3.8.19), the producers (v3.8.20), and
        // deterministic verifiers running against a workspace that actually contains the patch
        // (v3.8.23).
        //
        // Stored evidence is consulted first and a deterministic failure cannot be talked out of.
        // The prose stays as the narrative an operator reads and stops being the thing anything
        // branches on.
        //
        // Falls back to the prose verdict ONLY when there is no store — the CLI and the older tests.
        // Returning `unknown` for every mission in those configurations would be a regression
        // wearing the costume of rigour.
        var fromEvidence = ReadEvidenceVerdict(mission);

        var verdict = fromEvidence?.Verdict ?? Outcomes.VerificationVerdict.Parse(text);
        var passed = Outcomes.VerificationVerdict.IsPass(verdict);

        var narrative = fromEvidence is null ? text
            : text
              + $"\n\nevidence_verdict: {fromEvidence.Verdict}"
              + $"\nevidence_basis: {fromEvidence.Explanation}"
              + $"\ndeterministic_passed: {fromEvidence.DeterministicPassed}"
              + $"\ndeterministic_failed: {fromEvidence.DeterministicFailed}"
              + $"\nnon_deterministic_recorded: {fromEvidence.NonDeterministicRecorded}";

        var result = TextResult(Name, narrative) with
        {
            StatusCode = passed ? "succeeded" : "succeeded_with_warnings",
            Summary = passed
                ? "Verification passed."
                : $"Verification did not pass: {Outcomes.VerificationVerdict.Explain(verdict)}.",
            Warnings = passed ? new List<string>() : new List<string> { Outcomes.VerificationVerdict.Explain(verdict) },
        };
        return result with
        {
            Evidence = BuildVerdictEvidence(verdict, text, fromEvidence),
        };
    }

    /// <summary>
    /// The verdict, WHERE IT CAME FROM, and what the model thought if it disagreed. v3.8.27.
    ///
    /// Recording the source is the point. Without it an operator cannot tell a verdict computed from
    /// a compiler's exit code apart from one parsed out of prose — and those two look identical in
    /// every report the colony produces, which is precisely the confusion this release removes.
    /// </summary>
    private static List<AntEvidence> BuildVerdictEvidence(
        string verdict, string modelText, Outcomes.EvidenceVerdict.Result? fromEvidence)
    {
        var rows = new List<AntEvidence>
        {
            new("verification_verdict", verdict, Outcomes.VerificationVerdict.Explain(verdict)),
        };
        if (fromEvidence is null) return rows;

        rows.Add(new("verdict_source", "evidence_store", fromEvidence.Explanation));

        // The model's own reading, kept and explicitly subordinate. Discarding it would lose the one
        // thing a model is genuinely good at here — explaining WHY something looks wrong — and
        // recording it as an override makes the disagreement auditable rather than invisible.
        var modelRead = Outcomes.VerificationVerdict.Parse(modelText);
        if (modelRead != verdict)
            rows.Add(new("model_verdict_overridden", modelRead,
                $"the model read '{modelRead}'; the evidence supports '{verdict}', and the evidence decides"));

        return rows;
    }


    private string Compose(Task task, Mission mission)
    {
        var priorTasks = mission.Tasks.Where(t => t.Id != task.Id).ToList();
        var completed = priorTasks.Where(t => t.Status == TaskStatus.Complete).ToList();
        // Only critical failures fail verification; non-critical section failures are reported as gaps.
        var failed = priorTasks.Where(t => t.Status == TaskStatus.Failed && t.Critical).ToList();
        var degraded = priorTasks.Where(t => !t.Critical && t.Status is TaskStatus.Failed or TaskStatus.Skipped).ToList();
        var outputs = priorTasks.Where(t => (t.AssignedAnt is "builder" or "coder") && !string.IsNullOrEmpty(t.Result)).ToList();
        var staticCheck = StaticVerify(completed, failed, outputs);
        if (degraded.Count > 0)
            staticCheck += $"\nDegraded Sections: {degraded.Count} non-critical task(s) failed or were skipped; synthesis proceeded with partial input.";
        if (!_useOllama || _router is null) return staticCheck;

        var context = DomainHelpers.BuildContextPacketText(mission, "verifier", Math.Min(AnthillRuntime.MaxVerifierContextChars, AnthillRuntime.MaxContextPacketChars));
        var prompt = $@"{AnthillRuntime.PromptInjectionPrefix}
ANTHILL v{AnthillRuntime.Version} | role: verifier | timestamp: {AnthillTime.NowUtc().ToIso()} | mission: {TextUtil.Truncate(mission.Goal, 180)}
You are concise. Do not explain your reasoning unless asked.

You are Verifier Ant inside ANTHILL v{AnthillRuntime.Version}.

Mission goal:
{mission.Goal}

Static system check:
{staticCheck}

Task outputs:
{context}

Return:
- Verdict: Verification Passed / Needs Improvement / Verification Failed
- Reasoning:
- Missing Steps:
- Risk Notes:

Rules:
- Check whether the builder actually answered the specific question asked, not merely whether output exists.
- If the builder response contains only procedural ANTHILL commands like /apply, /patches, or /approval, mark Needs Improvement.
- If patch proposals exist, confirm they were only proposed.
- If /apply was not executed, do not claim files were modified.
- If write gates are disabled, confirm patch application cannot run.
";
        var verdict = _router.GenerateTyped("verifier", prompt, mission.Id, task.Id, Name);
        return verdict.Ok
            ? verdict.Content
            : $"{staticCheck}\n\nRouted verifier model unavailable ({verdict.Status.Name()}):\n{verdict.Content}";
    }

    private static string StaticVerify(List<Task> completed, List<Task> failed, List<Task> outputs)
    {
        if (failed.Count > 0)
            return "Verification Failed\nReasoning: One or more tasks failed before verification.\nMissing Steps: Resolve failed task output before finalizing.\nRisk Notes: Mission may be incomplete or partially invalid.";
        if (outputs.Count == 0)
            return "Verification Failed\nReasoning: No builder or coder output was found to verify.\nMissing Steps: Builder or coder output is required.\nRisk Notes: Mission result may be empty or incomplete.";
        if (completed.Count >= 2)
            return "Verification Passed\nReasoning: Mission has completed task output and at least one builder/coder result.\nMissing Steps: None identified by static verification.\nRisk Notes: Static verification does not evaluate factual content.";
        return "Needs Improvement\nReasoning: Mission may not have enough completed task output.\nMissing Steps: More task output may be needed before finalizing.\nRisk Notes: Output may be incomplete.";
    }
}
