using System.Text.Json;
using System.Text.RegularExpressions;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Models;
using Anthill.Core.Tools;

namespace Anthill.Core.Agents;

/// <summary>A specialised colony worker. Given a task and a locked mission snapshot, it returns a text result.</summary>
public abstract class BaseAnt
{
    public string Name { get; }
    protected BaseAnt(string name) => Name = name;
    public abstract string Run(Task task, Mission mission);
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

    public override string Run(Task task, Mission mission)
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
            return "Researcher Ant summarized local context without LLM routing.\n\n" + rawContext +
                   $"\n\nResearch Finding:\nANTHILL v{AnthillRuntime.Version} supports read-only external research when web search is enabled.";

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
        var response = _router.Generate("researcher", prompt, mission.Id, task.Id, Name);
        return response.StartsWith("ERROR:")
            ? "Researcher routed model unavailable. Fallback context brief:\n\n" + rawContext
            : response;
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
    private static readonly HashSet<string> RecentHints = new() { "2026", "2025", "latest", "current", "release", "updated", "today", "recent" };
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

    public override string Run(Task task, Mission mission)
    {
        var existingSources = _memory.CountSourcesForMission(mission.Id);
        if (existingSources >= AnthillRuntime.MaxSourcesPerMission)
            return $"WebResearchAnt skipped search because the mission source budget is exhausted.\nMission Sources: {existingSources}/{AnthillRuntime.MaxSourcesPerMission}";

        var existingSearches = _memory.CountWebSearchAttemptsForMission(mission.Id);
        if (existingSearches >= AnthillRuntime.MaxWebSearchesPerMission)
        {
            _memory.LogEvent(mission.Id, "web_search_budget_exhausted",
                "WebResearchAnt skipped search because the mission web-search attempt budget is exhausted.", task.Id, Name,
                new() { ["web_search_attempts"] = existingSearches, ["max_web_searches_per_mission"] = AnthillRuntime.MaxWebSearchesPerMission });
            return $"WebResearchAnt skipped search because the mission web-search attempt budget is exhausted.\nWeb Searches: {existingSearches}/{AnthillRuntime.MaxWebSearchesPerMission}";
        }

        var query = BuildQuery(task, mission);
        _memory.LogEvent(mission.Id, "web_search_attempted", "WebResearchAnt requested read-only external research.", task.Id, Name,
            new() { ["query"] = query, ["attempt_number"] = existingSearches + 1, ["max_web_searches_per_mission"] = AnthillRuntime.MaxWebSearchesPerMission, ["max_sources_per_search"] = AnthillRuntime.MaxSourcesPerSearch });

        var result = _tools.RunTool("web_search", mission.Id, task.Id, Name,
            new() { ["query"] = query, ["max_results"] = Math.Min(AnthillRuntime.MaxWebResults, AnthillRuntime.MaxSourcesPerSearch) });
        if (!result.Success)
            return $"WebResearchAnt could not perform external research.\nQuery: {query}\nError: {result.Error}\nNote: Enable web search to allow read-only web research.";

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
            return $"WebResearchAnt ran query but saved no source records.\nQuery: {query}\nSkipped: {skipped}\nPreview:\n{TextUtil.Truncate(preview, 1000)}";
        }

        var lines = new List<string>
        {
            $"WebResearchAnt saved {savedSources.Count} source record(s).", $"Query: {query}", $"Skipped/Deduped/Filtered: {skipped}",
        };
        lines.AddRange(savedSources.Select(src =>
            $"Source ID: {src.Id}\nTitle: {src.Title}\nDomain: {src.Domain}\nConfidence: {src.ConfidenceLabel} ({src.ConfidenceScore})\nURL: {src.Url}\nSummary: {src.Summary}"));
        return string.Join("\n\n---\n\n", lines);
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
        var response = _router.Generate("web", prompt, antName: "web");
        return response.StartsWith("ERROR:")
            ? TextUtil.Truncate(string.IsNullOrEmpty(snippet) ? title : snippet, AnthillRuntime.MaxSourceSummaryChars)
            : TextUtil.Truncate(response, AnthillRuntime.MaxSourceSummaryChars);
    }
}

public sealed partial class FileAnt : BaseAnt
{
    private readonly ToolRegistry _tools;
    public FileAnt(ToolRegistry tools) : base("file") => _tools = tools;

    public override string Run(Task task, Mission mission)
    {
        var directoryResult = _tools.RunTool("list_directory", mission.Id, task.Id, Name, new() { ["path"] = "." });
        var candidatePaths = ShouldAttemptFileReads(task, mission) ? ExtractCandidatePaths(task, mission) : new List<string>();
        var fileReports = new List<string>();
        foreach (var path in candidatePaths.Take(AnthillRuntime.MaxFileAntFilesToRead))
        {
            var readResult = _tools.RunTool("read_text_file", mission.Id, task.Id, Name, new() { ["path"] = path });
            fileReports.Add(readResult.Success
                ? $"File: {path}\nRead Success: True\nContent:\n{readResult.Output}"
                : $"File: {path}\nRead Success: False\nError:\n{readResult.Error}");
        }
        if (fileReports.Count == 0)
            fileReports.Add("FileAnt did not identify specific readable file paths. It only listed workspace structure.");

        return $"FileAnt performed safe read-only workspace inspection.\n\nMission:\n{mission.Goal}\n\nAssigned Task:\n{task.Description}\n\n" +
               $"Workspace Listing:\nSuccess: {directoryResult.Success}\n{(directoryResult.Success ? directoryResult.Output : directoryResult.Error)}\n\n" +
               $"File Read Reports:\n{string.Join("\n\n---\n\n", fileReports)}" +
               "\n\nFileAnt did not write, modify, delete, execute, or patch any files.";
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
        var lowered = text.ToLowerInvariant();
        if (new[] { "anthill", "this script", "main script", "python script" }.Any(lowered.Contains))
            candidates.Add("anthill.py");
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
    [GeneratedRegex(@"\b[\w\-/\\.]+\.(?:py|txt|md|json|yaml|yml|toml|ini|cfg|log|csv|html|css|js|ts|tsx|jsx|xml)\b")] private static partial Regex SuffixPath();
}

public sealed class CoderAnt : BaseAnt
{
    private readonly bool _useOllama;
    private readonly ModelRouter? _router;
    public CoderAnt(bool useOllama, ModelRouter? router) : base("coder") { _useOllama = useOllama; _router = router; }

    public override string Run(Task task, Mission mission)
    {
        var codeContext = DomainHelpers.BuildContextPacketText(mission, "coder", Math.Min(AnthillRuntime.MaxCoderContextChars, AnthillRuntime.MaxContextPacketChars));
        if (!_useOllama || _router is null)
            return FallbackPatchJson("CoderAnt fallback mode produced no patch proposals because model routing/LLM generation is unavailable.");

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
      ""file_path"": ""relative/path/to/file.py"",
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
- Prefer modify or add.
- If you are unsure of the exact old_content, return an empty proposals list rather than guessing.
- Do not wrap JSON in markdown code fences.
- For modify, old_content must be exact and unambiguous.
- For add, old_content should be null.
- Do not propose database, .git, venv, cache, or absolute paths.
- Do not propose paths containing ..
- Every proposal requires approval.
- If context is incomplete, return an empty proposals list.
";
        var response = _router.Generate("coder", prompt, mission.Id, task.Id, Name);
        return response.StartsWith("ERROR:")
            ? FallbackPatchJson($"CoderAnt could not reach the routed model, so no patch proposals were created. Model error: {response}")
            : response;
    }

    private static string FallbackPatchJson(string summary) =>
        Json.Dumps(new { summary, proposals = Array.Empty<object>() }, indented: true);
}

public sealed class BuilderAnt : BaseAnt
{
    private readonly bool _useOllama;
    private readonly ModelRouter? _router;
    public BuilderAnt(bool useOllama, ModelRouter? router) : base("builder") { _useOllama = useOllama; _router = router; }

    public override string Run(Task task, Mission mission)
    {
        var previousContext = DomainHelpers.BuildContextPacketText(mission, "builder", Math.Min(AnthillRuntime.MaxPreviousContextChars, AnthillRuntime.MaxContextPacketChars));
        if (!_useOllama || _router is null) return FallbackResponse(task, mission, previousContext);

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
        var response = _router.Generate("builder", prompt, mission.Id, task.Id, Name);
        return response.StartsWith("ERROR:")
            ? $"{response}\n\nFallback Builder Response:\n{FallbackResponse(task, mission, previousContext)}"
            : response;
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
    public VerifierAnt(bool useOllama, ModelRouter? router) : base("verifier") { _useOllama = useOllama; _router = router; }

    public override string Run(Task task, Mission mission)
    {
        var priorTasks = mission.Tasks.Where(t => t.Id != task.Id).ToList();
        var completed = priorTasks.Where(t => t.Status == TaskStatus.Complete).ToList();
        var failed = priorTasks.Where(t => t.Status == TaskStatus.Failed).ToList();
        var outputs = priorTasks.Where(t => (t.AssignedAnt is "builder" or "coder") && !string.IsNullOrEmpty(t.Result)).ToList();
        var staticCheck = StaticVerify(completed, failed, outputs);
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
        var response = _router.Generate("verifier", prompt, mission.Id, task.Id, Name);
        return response.StartsWith("ERROR:") ? $"{staticCheck}\n\nRouted verifier model unavailable:\n{response}" : response;
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
