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
    /// <param name="call">
    /// The model call that produced this text, when one did. v0.3.8.57 — its provider and model
    /// travel into <see cref="AntMetrics"/> and from there into an artifact's provenance, so a
    /// stored artifact can say which model wrote it.
    ///
    /// Optional because most callers here are the OFFLINE and FALLBACK paths, where no model served
    /// the text and saying so by omission is correct. Passing the configured route instead would
    /// record a model that never ran.
    /// </param>
    protected static AntExecutionResult TextResult(string role, string text, string? summaryOverride = null,
        ModelCallResult? call = null)
    {
        var body = text ?? "";
        var summary = summaryOverride ?? TextUtil.CreateResultSummary(body, AnthillRuntime.MaxResultSummaryChars);
        // NB: a `with` expression must ASSIGN each member. Collection-initializer syntax
        // (`Artifacts = { ... }`) is only legal inside an object initializer, which is why the
        // same shape works in SpecialistAnts.cs but not here.
        return AntExecutionResult.Succeeded(summary, body) with
        {
            Artifacts = new List<AntArtifact> { new("text", $"{role} output", body) },
            Metrics = new AntMetrics
            {
                OutputChars = body.Length,
                // ModelCalls drives ArtifactProvenance.ModelInvolved, so it must count the call
                // even when the provider name did not survive — "a model ran and I do not know
                // which" and "no model ran" are different claims about the work.
                ModelCalls = call is null ? 0 : 1,
                Provider = call?.Provider,
                Model = call?.Model,
            },
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


    /// <summary>
    /// The few distinctive words worth searching the workspace for. v3.8.30.
    ///
    /// BOUNDED AND DETERMINISTIC — at most three terms, drawn from the goal and task text, no model
    /// involved. An unbounded search per goal word would turn one research task into dozens of
    /// dispatches, and a model choosing the terms would make the researcher's context depend on the
    /// thing the research is supposed to inform.
    ///
    /// Short and common words are dropped: searching a codebase for "the" or "code" returns
    /// everything, which is the same as returning nothing while costing a great deal more.
    /// </summary>
    private static IReadOnlyList<string> SearchTerms(string goal, string description)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the","and","for","with","this","that","from","into","code","file","files","about",
            "what","which","where","when","find","show","list","summarize","summarise","explain",
            "please","using","make","create","update","change","using","should","would","there",
        };

        return Regex.Matches(goal + " " + description, @"[A-Za-z][A-Za-z0-9_.]{3,}")
            .Select(m => m.Value)
            .Where(w => !stop.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
    }

    public override AntExecutionResult Execute(Task task, Mission mission)
    {
        var recentMemory = _memory.FormatRecentMemory(AnthillRuntime.RecentMemoryLimit, AnthillRuntime.MemoryResultChars);
        var relevantMemory = _memory.FormatRelevantMemory(mission.Goal, AnthillRuntime.RelevantMemoryLimit, AnthillRuntime.MemoryResultChars);
        var pheromoneContext = _memory.FormatPheromoneContext(8);
        var toolResults = new List<ToolResult> { _tools.RunTool("system_info", mission.Id, task.Id, Name) };

        // v0.3.8.98 — THE RUNTIME HALF, dispatched when the TASK says that is what it is for.
        //
        // Branching on the task's declared capability rather than on its worker id or on words in
        // its description: the capability is what the step was created to exercise, the registry
        // decides which worker serves it, and neither this handler nor the plan has to spell a
        // worker name for the right tool to run. `colony_state` reports what is enabled and
        // registered NOW — the question the repository cannot answer, and the one an operator
        // means by "is the colony healthy?".
        if (string.Equals(task.RequiredCapability, Missions.WorkerCapabilities.InspectRuntimeState,
                          StringComparison.OrdinalIgnoreCase))
            toolResults.Add(_tools.RunTool("colony_state", mission.Id, task.Id, Name));

        if (ShouldInspectWorkspace(task, mission))
        {
            toolResults.Add(_tools.RunTool("list_directory", mission.Id, task.Id, Name, new() { ["path"] = "." }));

            // v3.8.30 — SEARCH, not just list.
            //
            // The researcher answered "what is in this codebase" by reading folder names. Both tools
            // below have been registered since v3.5.0/v3.6.0 and reachable by exactly one role.
            //
            // Granting them in the contract without dispatching them here would have been the
            // defect this project has found eight times: a declared surface that does not match the
            // real one. Contract and handler in the same release, or neither.
            //
            // A failed search is KEPT in the report rather than dropped. "I searched and found
            // nothing" and "I did not search" are different facts, and FormatToolReport renders
            // both — the researcher's own context should not be able to hide which one happened.
            foreach (var term in SearchTerms(mission.Goal, task.Description))
                toolResults.Add(_tools.RunTool("search_workspace", mission.Id, task.Id, Name,
                    new() { ["query"] = term }));

            // The index answers a different question from the search: what KINDS of code exist and
            // where symbols are declared, rather than where a string appears. Summary mode, because
            // a researcher wants the shape of the repository, not every match in it.
            toolResults.Add(_tools.RunTool("repository_index", mission.Id, task.Id, Name,
                new() { ["summary"] = true }));
        }

        var rawContext = $"Mission: {mission.Goal}\n\nTask: {task.Description}\n\nRecent Memory:\n{recentMemory}\n\n" +
                         $"Relevant Memory:\n{relevantMemory}\n\nPheromone Trails:\n{pheromoneContext}\n\n" +
                         $"Tool Context:\n{FormatToolReport(toolResults)}";
        rawContext = TextUtil.Truncate(rawContext, AnthillRuntime.MaxContextPacketChars, "...[research context truncated]");

        // v0.3.8.57 — the researcher joins the typed channel.
        //
        // It is a CORE ant and it had never seen an artifact. Its context was memory, pheromones
        // and tool output — every one of them prose — so a researcher summarising "what has this
        // mission established" was reading other workers' narrative about the patch set rather
        // than the patch set. The brief it produces then feeds the coder, which is how a
        // paraphrase of a paraphrase became load-bearing.
        //
        // APPENDED AFTER the truncation, exactly as BuildContextPacketText does it, because the
        // two halves are budgeted separately on purpose: sharing one cap lets a long narrative
        // crowd out the structured record, which is the arrangement this whole stage exists to end.
        rawContext += DomainHelpers.ArtifactBlock((Anthill.SDK.Artifacts.IArtifactStore)_memory,
            mission.Id, AnthillRuntime.MaxContextPacketChars, task.InputArtifactIds,
            consumerRole: Name, consumerTaskId: task.Id);

        if (_router is null || !AnthillRuntime.UseOllama)
        {
            // Configured-offline mode: the local summary IS the deliverable — a plain success.
            var offline = "Researcher Ant summarized local context without LLM routing.\n\n" + rawContext +
                   $"\n\nResearch Finding:\nANTHILL v{AnthillRuntime.Version} supports read-only external research when web search is enabled.";
            return TextResult(Name, offline);
        }

        var prompt = $@"Summarize only the context that is relevant to the mission below.
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
        var call = _router.GenerateTyped("researcher", prompt, mission.Id, task.Id, Name,
            system: AnthillRuntime.RoleSystemPrompt("researcher", mission.Goal));
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
        // v0.3.8.57 — the four sections the prompt above DEMANDS, extracted rather than flattened.
        //
        // v3.8.21 declined to type the researcher on the grounds that naming prose `change_plan`
        // would be relabelling. Correct in general and wrong here: the shape is not being invented,
        // it is being recovered — the return format has been part of this prompt since the ant was
        // written, and the response was parsed by nobody.
        //
        // A response that did not follow the format produces NO artifact and a disclosed warning.
        // Emitting one full of empty sections would make "the model ignored the format" read exactly
        // like "the researcher found nothing", and every consumer downstream would believe the
        // second. See ResearchBrief for why the builder gets no equivalent.
        var brief = Anthill.SDK.Artifacts.ResearchBrief.TryParse(call.Content);
        var text = TextResult(Name, call.Content, call: call);
        return brief is null
            ? text with
            {
                StatusCode = "succeeded_with_warnings",
                Warnings = text.Warnings
                    .Append("unstructured_research_output: the response did not carry the declared "
                          + "sections, so no research_brief artifact was produced").ToList(),
            }
            : WithArtifact(text, "research_brief", "Research brief", brief.ToJson());
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

    /// <summary>
    /// The search query, with an explicitly requested site HONOURED. v0.3.8.73 — the first live
    /// qualification run's finding #7: asked to consult a named source, the web ant queried an
    /// unrelated domain.
    ///
    /// It was not ignoring the target so much as never looking for one: the query was
    /// `goal + description`, and a domain mentioned in either was just more words for a search
    /// engine to weigh against the rest. When the operator names a site, that is a CONSTRAINT on
    /// where the answer may come from, not a hint about what to type.
    ///
    /// Narrow on purpose. This recognises the two spellings an operator actually writes — a bare
    /// domain and a `site:` directive — and does nothing clever with URLs, paths or multiple
    /// domains, because a parser that guessed which of three mentioned domains was meant would be
    /// inventing intent. One recognised target becomes a `site:` filter; anything else is unchanged
    /// from the behaviour every existing mission has.
    /// </summary>
    internal static string BuildQuery(Task task, Mission mission)
    {
        var text = $"{mission.Goal} {task.Description}".Trim();
        var target = RequestedSite(text);
        var query = target is null ? text : $"site:{target} {text}";
        return TextUtil.Truncate(query, 300, "");
    }

    /// <summary>The site the request names, or null when it names none or names several.</summary>
    internal static string? RequestedSite(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // `site:example.com` — the operator has already said it in the search engine's own words.
        var explicitDirective = System.Text.RegularExpressions.Regex.Match(
            text, @"\bsite:\s*([a-z0-9][a-z0-9.-]*\.[a-z]{2,})\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (explicitDirective.Success) return explicitDirective.Groups[1].Value.ToLowerInvariant();

        var domains = System.Text.RegularExpressions.Regex.Matches(
                text, @"\b(?:https?://)?((?:[a-z0-9][a-z0-9-]*\.)+[a-z]{2,})\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            // A sentence-ending "…the docs." is not a domain, and neither is a file name.
            .Where(d => !d.EndsWith(".md") && !d.EndsWith(".txt") && !d.EndsWith(".json")
                     && !d.EndsWith(".cs") && !d.EndsWith(".html"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // EXACTLY ONE, or none. Two mentioned domains mean the request is about a comparison as
        // often as it means one of them is the target, and picking is guessing.
        return domains.Count == 1 ? domains[0] : null;
    }

    private string SummarizeSource(string goal, string title, string url, string snippet, Dictionary<string, object?> quality)
    {
        var baseText = $"Title: {title}\nURL: {url}\nSnippet: {snippet}\n" +
                       $"Quality: confidence={quality.GetValueOrDefault("confidence_score")} label={quality.GetValueOrDefault("confidence_label")} notes={quality.GetValueOrDefault("quality_notes")}";
        if (_router is null || !AnthillRuntime.UseOllama)
            return TextUtil.Truncate(string.IsNullOrEmpty(snippet) ? title : snippet, AnthillRuntime.MaxSourceSummaryChars);
        var prompt = $@"Summarize why this source may be relevant to the mission in 1-3 sentences.
Include any obvious freshness or authority caveat.
Do not invent details beyond the title/snippet/url/quality fields.

Source:
{baseText}
";
        var summary = _router.GenerateTyped("web", prompt, antName: "web",
            system: AnthillRuntime.RoleSystemPrompt("web", goal));
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
    /// <summary>
    /// Paths the workspace itself suggests, via the repository index and a bounded content search.
    /// v3.8.30.
    ///
    /// Returns EMPTY on any refusal — no mission workspace in scope, tools not registered, a search
    /// that matched nothing. An empty result means the ant proceeds exactly as it did before this
    /// existed, which is the property that lets discovery be added without changing what a task
    /// that already names its files does.
    ///
    /// Bounded at both ends: at most three search terms, at most eight discovered paths. The read
    /// loop below applies its own cap as well, so this widens what CAN be found without widening
    /// how much is read.
    /// </summary>
    private List<string> DiscoverPaths(Task task, Mission mission)
    {
        var found = new List<string>();
        var text = task.Description + " " + task.Title + " " + mission.Goal;

        void Harvest(ToolResult result)
        {
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output)) return;
            foreach (Match m in Regex.Matches(result.Output,
                         @"\b(?:src|tests|docs|scripts|deploy)/[\w./\-]+\.\w{1,6}\b"))
                if (!found.Contains(m.Value, StringComparer.OrdinalIgnoreCase)) found.Add(m.Value);
        }

        try
        {
            foreach (var term in FileSearchTerms(text))
            {
                Harvest(_tools.RunTool("repository_index", mission.Id, task.Id, Name, new() { ["name"] = term }));
                Harvest(_tools.RunTool("search_workspace", mission.Id, task.Id, Name, new() { ["query"] = term }));
                if (found.Count >= 8) break;
            }
        }
        catch (Exception error)
        {
            // Discovery is an ENHANCEMENT. A file ant that failed because the index was unavailable
            // would be worse than one that reads the files the task named.
            Console.Error.WriteLine($"[file] path discovery unavailable for {mission.Id}: {error.Message}");
        }

        return found.Take(8).ToList();
    }

    /// <summary>At most three distinctive terms, deterministic, no model. Same reasoning as the
    /// researcher's: an unbounded term list turns one task into dozens of dispatches.</summary>
    private static IReadOnlyList<string> FileSearchTerms(string text)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the","and","for","with","this","that","from","into","code","file","files","about",
            "what","which","where","when","find","show","list","read","inspect","collect","relevant",
        };
        return Regex.Matches(text, @"[A-Za-z][A-Za-z0-9_.]{3,}")
            .Select(m => m.Value)
            .Where(w => !stop.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
    }

    public override AntExecutionResult Execute(Task task, Mission mission)
    {
        var directoryResult = _tools.RunTool("list_directory", mission.Id, task.Id, Name, new() { ["path"] = "." });

        // v3.8.30 — FIND files, do not only read ones already named.
        //
        // `ExtractCandidatePaths` pulls path-shaped tokens out of the task text, so the file ant
        // could read a file somebody had already named and had no way to discover one. That makes
        // "collect the relevant files" a guess dressed as a query.
        //
        // The index is asked FIRST (it answers "which files are of this kind and where are symbols
        // declared"), then a content search for the task's own distinctive terms. Both are additive:
        // explicitly named paths still win, because a task that names a file means that file.
        var discovered = DiscoverPaths(task, mission);

        var candidatePaths = ShouldAttemptFileReads(task, mission)
            ? ExtractCandidatePaths(task, mission)
                .Concat(discovered)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();
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
    private readonly Anthill.SDK.Artifacts.IArtifactStore? _artifacts;

    /// <summary>
    /// v3.8.29 (Stage C): the artifact store, so the coder receives TYPED context rather than other
    /// ants' prose about it. This is the role the interchange matters most for — it is the one that
    /// produces source changes, and it has been working from narrative summaries of what the file
    /// and cartographer ants found.
    /// </summary>
    public CoderAnt(bool useOllama, ModelRouter? router,
        Anthill.SDK.Artifacts.IArtifactStore? artifacts = null) : base("coder")
    { _useOllama = useOllama; _router = router; _artifacts = artifacts; }


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

        var codeContext = DomainHelpers.BuildContextPacketText(mission, "coder", Math.Min(AnthillRuntime.MaxCoderContextChars, AnthillRuntime.MaxContextPacketChars), artifacts: _artifacts,
            // v0.3.8.57 — the artifacts THIS TASK was given, when the runtime named any.
            // Empty falls back to the mission-wide block, which is what every task received before.
            declaredInputIds: task.InputArtifactIds,
            consumerTaskId: task.Id);
        if (!_useOllama || _router is null)
            return AntExecutionResult.Failed(FailureClass.TransientProviderFailure,
                "Coder cannot produce patch proposals: model routing/LLM generation is unavailable.");

        // v0.3.8.95 — ACTING MODE. Three conditions, each load-bearing: the operator turned the
        // gate on; the mission holds a usable isolated worktree; and the routed provider is an
        // agent CLI — the only kind of provider with hands. A local model routed to coder keeps
        // proposing JSON: it cannot edit a file, and pretending otherwise would classify its
        // prose as work.
        //
        // v0.3.8.97 — A MISSING WORKTREE FAILS CLOSED, it no longer falls through. The old
        // fall-through went to "propose-only", but propose-only through the SAME writing CLI is a
        // sentence in a prompt, not a boundary: the process still stood in the project's live
        // checkout with its editing tools installed. An acting-capable coder with nowhere isolated
        // to act refuses here, by name — and AgentCliProvider's worktree gate refuses the process
        // start independently, so this branch being edited away would not reopen the road.
        var actingRoute = AnthillRuntime.EnableActingCoder
            && _router.GetRoute("coder").Provider.StartsWith("agent:", StringComparison.OrdinalIgnoreCase);
        if (actingRoute && Anthill.Core.Workspaces.MissionWorkspaceScope.CurrentRoot is { } actingTree)
            return ExecuteActing(task, mission, codeContext, actingTree);
        if (actingRoute)
            return AntExecutionResult.Failed(FailureClass.PolicyDenial,
                "Acting coder refused by the worktree gate: the routed provider is a writable agent "
              + "CLI and this mission has no usable isolated worktree (preparation failed, was "
              + "rejected, or the workspace is unusable). A writing agent is never invoked in the "
              + "live project, and prompt-only restrictions are not a fallback — fix the mission "
              + "workspace and retry.");

        // v2.11.1: when the sandbox gate is on, iterate inside a disposable sandbox (propose ->
        // apply IN THE SANDBOX -> build -> refine on failure) and return proposals that verified —
        // the same patch JSON the approval pipeline already consumes. The live workspace is never
        // touched; any unavailability falls through to the unchanged one-shot path below.
        if (AnthillRuntime.EnableSandboxExecution)
        {
            var sandboxed = TryRunSandboxed(task, mission, codeContext);
            if (sandboxed is not null) return ClassifyPatchJson(sandboxed);
        }

        var call = _router.GenerateTyped("coder", BuildPrompt(task, mission, codeContext, ""), mission.Id, task.Id, Name,
            system: AnthillRuntime.RoleSystemPrompt("coder", mission.Goal, CoderRules),
            schema: PatchSetSchema);
        if (!call.Ok)
            return AntExecutionResult.Failed(FailureClass.TransientProviderFailure,
                $"Coder could not reach the routed model ({call.Status.Name()}) — no patch proposals created. {TextUtil.Truncate(call.Content, 300)}");
        return ClassifyPatchJson(call.Content);
    }

    /// <summary>
    /// v0.3.8.95 — the artifact kind an acting turn carries. ExecutionService branches its coder
    /// post-processing on exactly this: the producer states what kind of result it made, and the
    /// consumer reads the statement rather than re-deriving config and routing on its own.
    /// </summary>
    public const string WorkspaceEditReportKind = "workspace_edit_report";

    /// <summary>
    /// v0.3.8.95 — the marker an acting coder uses to state, deliberately, that the task required
    /// no change. A distinct token rather than inferred intent: "the tree is clean because nothing
    /// was needed" and "the tree is clean because the agent did nothing" must never be the same
    /// observation.
    /// </summary>
    public const string NoChangesMarker = "NO_CHANGES_NEEDED";

    /// <summary>
    /// The acting coder's operating contract, as the model receives it. v0.3.8.97 made it
    /// `internal` so its guard can assert on the ASSEMBLED string rather than on this file's text:
    /// a rule split across two C# literals is present at runtime and absent from any source-level
    /// search, so a source assertion fails on a re-wrap while passing on a deleted rule that
    /// happens to stay on one line. The value is the subject; read the value.
    /// </summary>
    internal const string ActingCoderRules =
        "Your role:\nImplement the assigned task by editing files directly in your working directory.\n\n"
      + "Your working directory is an ISOLATED git worktree prepared for this mission. Nothing you "
      + "change here reaches the live checkout: ANTHILL diffs this tree afterwards, the diff becomes "
      + "a reviewed patch set verified in its own revision, and the promotion gate owns the only "
      + "road to the real repository.\n\n"
      + "Rules:\n"
      + "- Edit real files, in place, relative to the working directory. Create files where the task needs them.\n"
      // v0.3.8.97 — iteration is allowed, evidence is not delegated. The permission layer grants
      // exactly the repository's own declared check commands (CheckCommandStems), so the rule and
      // the mechanism state the same boundary.
      + "- You MAY run the repository's own declared build/test commands (e.g. dotnet build, dotnet test) "
      + "inside your working directory to check your work while iterating. ANTHILL's tester re-runs the "
      + "declared checks independently afterwards; its runs are the evidence, yours are only for iteration.\n"
      + "- Do not run git commands that change state, and do not commit; leave your work as uncommitted "
      + "changes for the diff.\n"
      + "- When done, reply with a short factual summary of WHAT you changed and WHY.\n"
      + "- If, after examining the code, the task genuinely requires no change, reply with the single "
      + "marker " + NoChangesMarker + " followed by one sentence explaining why.";

    /// <summary>
    /// One acting turn: the routed agent CLI works directly in the mission worktree (it is already
    /// confined there — the provider resolves its working directory from the ambient access scope),
    /// and the outcome is judged from the TREE. The model call and the classification are separate
    /// so the classifier stays a pure function a test can hold still.
    /// </summary>
    private AntExecutionResult ExecuteActing(Task task, Mission mission, string codeContext, string workspaceRoot)
    {
        var call = _router!.GenerateTyped("coder", BuildActingPrompt(task, mission, codeContext),
            mission.Id, task.Id, Name,
            system: AnthillRuntime.RoleSystemPrompt("coder", mission.Goal, ActingCoderRules));
        if (!call.Ok)
            return AntExecutionResult.Failed(FailureClass.TransientProviderFailure,
                $"Acting coder could not reach the routed provider ({call.Status.Name()}) — no work performed. "
                + TextUtil.Truncate(call.Content, 300));

        var changed = Anthill.Core.Workspaces.WorkspaceChangeSet.ChangedPaths(workspaceRoot);
        return ClassifyActingOutcome(call.Content, changed);
    }

    /// <summary>
    /// v0.3.8.95 — classify an acting turn from the DISK, not the prose. Changes on disk are the
    /// deliverable regardless of what was said; a clean tree succeeds only when the agent declared
    /// <see cref="NoChangesMarker"/>, and a clean tree with a story about edits is a failure that
    /// says so. The narrative rides along as the artifact either way — the reviewers read it, they
    /// just never grade by it.
    /// </summary>
    internal static AntExecutionResult ClassifyActingOutcome(string response, IReadOnlyList<string> changedPaths)
    {
        var text = response ?? "";
        if (changedPaths.Count > 0)
            return AntExecutionResult.Succeeded(
                $"Acting coder changed {changedPaths.Count} file(s) in the mission workspace.", text) with
            {
                Artifacts = new List<AntArtifact>
                {
                    new(WorkspaceEditReportKind, "acting coder report", text.Length > 0 ? text : "(no narrative)"),
                },
                Metrics = new AntMetrics { OutputChars = text.Length },
            };

        if (text.Contains(NoChangesMarker, StringComparison.Ordinal))
            return AntExecutionResult.Succeeded(
                "Acting coder examined the workspace and declared no changes were needed.", text) with
            {
                Artifacts = new List<AntArtifact> { new(WorkspaceEditReportKind, "acting coder report", text) },
                Metrics = new AntMetrics { OutputChars = text.Length },
            };

        return AntExecutionResult.Failed(FailureClass.InternalDefect,
            "Acting coder finished without changing any file and without declaring "
            + NoChangesMarker + " — a clean tree with a work narrative is not a completed code task. "
            + TextUtil.Truncate(text, 300));
    }

    private static string BuildActingPrompt(Task task, Mission mission, string codeContext) =>
        $@"Mission goal:
{mission.Goal}

Assigned task:
{task.Title}
{task.Description}

Prior context:
{codeContext}";

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
                var call = _router!.GenerateTyped("coder", BuildPrompt(task, mission, codeContext, feedback), mission.Id, task.Id, Name,
                    system: AnthillRuntime.RoleSystemPrompt("coder", mission.Goal, CoderRules),
                    // The SAME schema as the one-shot path. A refinement turn that dropped it would
                    // be the sandbox loop quietly running under weaker constraints than the call it
                    // is refining, and the loop exists to fix malformed output.
                    schema: PatchSetSchema);
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

    /// <summary>
    /// The coder's contract. Its LIMITS especially belong on the system channel: "you do not write
    /// files, you do not apply patches" is a statement about what the harness permits, and stated in
    /// the request it was indistinguishable from a requester claiming to permit something.
    /// </summary>
    /// <summary>
    /// The patch set's shape, on the wire, as a JSON Schema. v0.3.8.76 (PLAN.md §2 R1).
    ///
    /// WHY THIS IS NOT JUST THE PROMPT AGAIN. `BuildPrompt` describes this shape in English, and
    /// that description stays — a small local model follows an example better than it follows a
    /// schema, and the prompt's rules carry judgement ("return an empty list rather than guessing")
    /// that no schema can express. What the prompt could never do is BIND the provider. Asking for
    /// JSON in prose makes the format a request the model may decline; `response_format:
    /// json_schema` makes it a constraint the provider enforces before a token reaches us.
    ///
    /// It is also the last place in the colony where a control channel was prose. Everything else —
    /// verdicts, blocks, failure classes, handoffs — was moved to typed fields across v3.2.0 and
    /// v3.8.22 precisely because prose parsed as structure fails to an EMPTY result, and an empty
    /// result reads as "found nothing" rather than as "the model did not answer the question". A
    /// coder returning zero proposals is exactly that failure, and it is a failure class this
    /// colony has a name and a retry loop for.
    ///
    /// THE `required` LIST IS DERIVED FROM `PatchProposalParser`, NOT FROM THE PROMPT, and the two
    /// disagree in three places that would each have been a regression:
    ///
    ///   * `new_content` is NOT required. The prompt asks for it on every proposal, but a `delete`
    ///     has no new content, and a schema demanding one would have made deletions unrepresentable
    ///     at the provider — a change_type the applier supports and the wire would forbid.
    ///   * `old_content` is nullable, because `add` has no prior state. The parser reads it through
    ///     `JsonStrOrNull` for exactly this reason.
    ///   * `requires_approval` is asked for in the prompt and read by NOBODY — approval is decided
    ///     by policy, never by the proposer. It stays in `properties` (with
    ///     `additionalProperties: false`, omitting it would forbid a field the prompt requests) and
    ///     out of `required` (demanding a field nothing reads is asking a model to spend tokens on
    ///     a value that cannot affect anything).
    ///
    /// What IS required is what the applier cannot proceed without: a path, and what to do to it.
    ///
    /// Kept beside <see cref="CoderRules"/> and the prompt because
    /// `StructuredOutputTests.TheCoderSchemaAndItsPrompt_DescribeTheSameShape` pins the two
    /// together — two descriptions of one format that nothing compares is how they drift, and this
    /// pair would drift toward the schema being right and the prompt being followed.
    /// </summary>
    internal const string PatchSetSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["summary", "proposals"],
          "properties": {
            "summary": { "type": "string" },
            "proposals": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["file_path", "change_type"],
                "properties": {
                  "file_path": { "type": "string" },
                  "change_type": { "type": "string", "enum": ["add", "modify", "delete", "rename"] },
                  "reason": { "type": "string" },
                  "risk": { "type": "string" },
                  "old_content": { "type": ["string", "null"] },
                  "new_content": { "type": "string" },
                  "requires_approval": { "type": "boolean" }
                }
              }
            }
          }
        }
        """;

    private const string CoderRules =
        "Your role:\n"
      + "Create structured patch proposals as JSON only.\n\n"
      + "Limits:\n"
      + "- You do not write files.\n"
      + "- You do not run shell commands.\n"
      + "- You do not apply patches.\n"
      + "- You only propose patches. Application happens later, after approval and the config gates.";

    private static string BuildPrompt(Task task, Mission mission, string codeContext, string feedback)
    {
        // v0.3.8.93: the goal is the OPERATOR REQUEST — the instruction this patch exists to serve —
        // not untrusted data. See AnthillRuntime.OperatorRequestBlock for why the old fence was
        // false about this payload; retrieved context and prior model output keep the untrusted one.
        var prompt = $@"{AnthillRuntime.OperatorRequestBlock("mission goal", mission.Goal)}

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
    private readonly Anthill.SDK.Artifacts.IArtifactStore? _artifacts;

    /// <summary>v3.8.29 (Stage C): the artifact store, so the operator-facing answer is assembled
    /// from the typed record as well as the narrative.</summary>
    public BuilderAnt(bool useOllama, ModelRouter? router,
        Anthill.SDK.Artifacts.IArtifactStore? artifacts = null) : base("builder")
    { _useOllama = useOllama; _router = router; _artifacts = artifacts; }


    /// <summary>
    /// What the builder OWES the operator. On the system channel, so a requester cannot restate them
    /// and a worker cannot mistake them for part of the request.
    ///
    /// Note what is absent: nothing here tells the model to mention a feature or a command. An answer
    /// should describe what the mission did, and a rule that says "always mention X" produces a
    /// sentence about X whether or not X happened.
    /// </summary>
    private const string BuilderRules =
        "Rules:\n"
      + "- Lead with the direct answer before any explanation.\n"
      + "- Do not repeat the mission goal back to the operator.\n"
      + "- Aim for 200-400 words unless the task requires more.\n"
      + "- Be direct.\n"
      + "- Report only what this mission actually did. Do not claim a file was changed unless a "
      + "patch was applied, and do not describe capabilities the mission did not use.\n"
      // v0.3.8.73 — the first live qualification run. Asked for a report, the builder produced
      // commands, exit codes, durations, test totals, a role census and a `Dispatched` column, all
      // of it plausible and none of it from the colony. The real fix is structural — the compiled
      // `operator_summary` artifact is now the record, written by the Queen from persisted rows with
      // no model in the path — and this rule is the smaller half: the narrative must stop competing
      // with it. A prompt cannot make a model stop inventing figures, but it can stop asking it to,
      // and the two together mean an invented figure has no reason to exist and nowhere to land.
      + "- NEVER state a command, exit code, duration, timestamp, test count, file count or role "
      + "census. Those are compiled from the mission's own records and shown to the operator "
      + "separately. If the answer needs one, say what it describes in words instead of giving a "
      + "number you were not given. A figure you produce here is invented, however plausible.";

    public override AntExecutionResult Execute(Task task, Mission mission)
    {
        var previousContext = DomainHelpers.BuildContextPacketText(mission, "builder", Math.Min(AnthillRuntime.MaxPreviousContextChars, AnthillRuntime.MaxContextPacketChars), artifacts: _artifacts,
            // v0.3.8.57 — the artifacts THIS TASK was given, when the runtime named any.
            // Empty falls back to the mission-wide block, which is what every task received before.
            declaredInputIds: task.InputArtifactIds,
            consumerTaskId: task.Id);
        // Configured-offline: the static response IS the configured behaviour — plain success.
        if (!_useOllama || _router is null) return TextResult(Name, FallbackResponse(task, mission, previousContext));

        // v0.3.8.59 (PLAN.md §1b S9, second round) — the REST of the wrapper, found in the field.
        //
        // A worker's answer arrived carrying a warning: "this request arrived wrapped in a fake
        // system contract (forced persona, tool-permission claims, scripted talking points)". Those
        // three names map exactly onto three things that were still in this user turn after the
        // header moved to the system channel, so the model was not being vague — it was itemising.
        //
        //   * forced persona — "You are Builder Ant inside ANTHILL v…", now in the contract, where
        //     saying what the worker is has standing rather than being a claim in the request.
        //   * tool-permission claims — the /patches, /patch and /apply rules.
        //   * scripted talking points — "Mention that ANTHILL supports dependency-aware parallel
        //     execution, FTS memory search, and role-based model routing."
        //
        // The last two are DELETED, not moved, because they were also untrue. They instructed the
        // builder to advertise features and command syntax on every answer regardless of whether the
        // mission used any of them — so a mission that never touched memory ended by telling the
        // operator about FTS memory search. That is the colony's own "declared and reaching nobody",
        // pointed at the person reading the answer.
        //
        // And the colony already disagreed with itself about it: the verifier is told to mark an
        // answer Needs Improvement when it "contains only procedural ANTHILL commands like /apply,
        // /patches, or /approval". One role was instructed to produce what another was instructed to
        // penalise.
        // v0.3.8.93: the goal moves to the OPERATOR REQUEST fence — it is the question the answer
        // must serve. Prior task output stays UNTRUSTED: it is model output, and a hostile string
        // that rode in through a fetched page must not gain authority by being quoted downstream.
        var prompt = $@"{AnthillRuntime.OperatorRequestBlock("mission goal", mission.Goal)}

Assigned task:
{task.Title}
{task.Description}

{AnthillRuntime.UntrustedBlock("prior task output", previousContext)}

Create a practical final response.
";
        var call = _router.GenerateTyped("builder", prompt, mission.Id, task.Id, Name,
            system: AnthillRuntime.RoleSystemPrompt("builder", mission.Goal, BuilderRules));
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
        return TextResult(Name, call.Content, call: call);
    }

    private static string FallbackResponse(Task task, Mission mission, string previousContext) =>
        $"Builder Ant created a basic non-LLM response.\n\nMission Goal:\n{mission.Goal}\n\nAssigned Task:\n{task.Title}\n{task.Description}\n\n" +
        $"Previous Context:\n{previousContext}\n\n" +
        // v0.3.8.59: the numbered list that used to sit here told the operator to "review patch
        // proposals using /patches" on EVERY offline answer, including the overwhelming majority of
        // missions that produce no patches, and closed by advertising three capabilities the mission
        // may not have touched. Same untruth the model was being instructed to speak — deterministic
        // rather than generated, so nothing downstream would ever flag it. A fallback answer says
        // what the fallback did; it is not a place to put a product tour.
        "No model was routed for this task, so this is the colony's own summary of the inputs above " +
        "rather than a composed answer.";
}

public sealed class VerifierAnt : BaseAnt
{
    /// <summary>
    /// The verifier's contract and its VERDICT VOCABULARY.
    ///
    /// The return shape matters more here than anywhere else: `VerificationVerdict` parses this text
    /// to decide whether work is verified, so the format is a machine contract wearing prose. Stated
    /// in the request, it sat next to task output the verifier is judging — output that could
    /// contain the very words the parser looks for.
    /// </summary>
    private const string VerifierRules =
        "Judge the task outputs against the mission goal. Return exactly this shape:\n"
      + "- Verdict: Verification Passed / Needs Improvement / Verification Failed\n"
      + "- Reasoning:\n"
      + "- Missing Steps:\n"
      + "- Risk Notes:";


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
    private readonly Anthill.SDK.Artifacts.IArtifactStore? _artifactStore;

    public VerifierAnt(bool useOllama, ModelRouter? router,
        Anthill.SDK.Artifacts.IEvidenceStore? evidence = null,
        Anthill.SDK.Artifacts.IArtifactStore? artifacts = null) : base("verifier")
    {
        _useOllama = useOllama; _router = router; _evidence = evidence; _artifactStore = artifacts;
    }

    /// <summary>
    /// What asking the evidence store produced. Three states, not two — and the difference is the
    /// whole of PLAN.md §1b S3's verifier finding. NO STORE is the CLI and the older tests: the
    /// static/prose path is that configuration's contract and it stands. STORE FAILED is a colony
    /// whose verification machinery is down, and until v0.3.8.61 it was silently collapsed into
    /// "no store": the verdict fell through to prose parsed out of model text, which means a store
    /// outage WIDENED what the model's words could decide. Failure now carries its own state and
    /// produces <see cref="Outcomes.VerificationVerdict.Unavailable"/> — never prose.
    /// </summary>
    private sealed record EvidenceRead(
        Outcomes.EvidenceVerdict.Result? Verdict, bool StoreFailed, string? Error)
    {
        public static readonly EvidenceRead NoStore = new(null, false, null);
    }

    private EvidenceRead ReadEvidenceVerdict(Mission mission)
    {
        if (_evidence is null) return EvidenceRead.NoStore;
        try { return new EvidenceRead(Outcomes.EvidenceVerdict.For(_evidence.ForMission(mission.Id)), false, null); }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[verifier] could not read evidence for {mission.Id}: {error.Message}");
            return new EvidenceRead(null, true, error.Message);
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
        var read = ReadEvidenceVerdict(mission);
        var fromEvidence = read.Verdict;

        // S3 (v0.3.8.61): a store that FAILED is not a store that is absent. The prose fallback
        // exists for configurations that never had a store; letting it answer for a broken one
        // turns an outage into authority. Unavailable is not a pass, and nothing downstream —
        // MissionVerification, the scribe, auto-apply — treats it as one.
        // v0.3.8.73 — and a PASS may not come from prose, on any arm.
        //
        // The first live qualification run reported "the verifier fails open — it returned PASS
        // without evidence". Investigating it, that is NOT what production does: `Queen` always
        // hands this ant the evidence store, and an empty evidence list resolves to `Unknown` with
        // "no evidence was recorded for this mission — nothing has been verified". The PASS the
        // operator read was the BUILDER's prose, which is a different defect and is fixed by
        // `MissionReport`.
        //
        // But the report was one line from something true, and the first attempt at closing it was
        // WRONG in a way worth keeping written down, because it is this repository's signature
        // mistake made by the person documenting it.
        //
        // With no store, `fromEvidence` is null and the verdict comes from `Parse(text)`. The first
        // fix refused ANY promotion from that path — and broke two tests that were right. `text` is
        // not always model prose: with `_useOllama` false there is no model in this ant at all, and
        // `text` is the STATIC verifier's own deterministic evaluation of the mission's task states.
        // That is the CLI's and the offline suite's contract, and S3 preserved it deliberately
        // ("collapsing this configuration into unavailable would be rigour's costume on a
        // regression"). Refusing it would have made every offline mission unverifiable to close a
        // hole that is unreachable in production, where `Queen` always supplies the store.
        //
        // The real discriminator is not "did this come from text" but "did a MODEL write it". So the
        // refusal is scoped to exactly that: a model-written verdict with no evidence behind it
        // cannot promote. It may still DOWNGRADE — a model saying "needs improvement" is heard,
        // because a model's doubt costs nothing and its confidence is what has no standing.
        var fromProse = Outcomes.VerificationVerdict.Parse(text);
        var modelWroteIt = _useOllama && _router is not null;
        var verdict = read.StoreFailed
            ? Outcomes.VerificationVerdict.Unavailable
            : fromEvidence?.Verdict
              ?? (modelWroteIt && Outcomes.VerificationVerdict.IsPass(fromProse)
                    ? Outcomes.VerificationVerdict.Unknown
                    : fromProse);
        var passed = Outcomes.VerificationVerdict.IsPass(verdict);

        var narrative = read.StoreFailed
            ? text
              + "\n\nevidence_verdict: verification_unavailable"
              + $"\nevidence_basis: the evidence store could not be read ({read.Error}); "
              + "the model text above is narrative only and decides nothing"
            : fromEvidence is null ? text
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
            Evidence = BuildVerdictEvidence(verdict, text, fromEvidence, read.StoreFailed, read.Error),
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
        string verdict, string modelText, Outcomes.EvidenceVerdict.Result? fromEvidence,
        bool storeFailed = false, string? storeError = null)
    {
        var rows = new List<AntEvidence>
        {
            new(AntEvidenceKinds.VerificationVerdict, verdict, Outcomes.VerificationVerdict.Explain(verdict)),
        };
        if (storeFailed)
        {
            // The source row says WHY there is no basis, so an operator reading the report can tell
            // "the store was down" from "nobody produced evidence" — the second is a mission
            // property, the first is an incident.
            rows.Add(new(AntEvidenceKinds.VerdictSource, "evidence_store_unavailable",
                $"the evidence store exists and could not be read: {storeError}"));
            return rows;
        }
        if (fromEvidence is null) return rows;

        rows.Add(new(AntEvidenceKinds.VerdictSource, "evidence_store", fromEvidence.Explanation));

        // The model's own reading, kept and explicitly subordinate. Discarding it would lose the one
        // thing a model is genuinely good at here — explaining WHY something looks wrong — and
        // recording it as an override makes the disagreement auditable rather than invisible.
        var modelRead = Outcomes.VerificationVerdict.Parse(modelText);
        if (modelRead != verdict)
            rows.Add(new(AntEvidenceKinds.ModelVerdictOverridden, modelRead,
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

        var context = DomainHelpers.BuildContextPacketText(mission, "verifier", Math.Min(AnthillRuntime.MaxVerifierContextChars, AnthillRuntime.MaxContextPacketChars), artifacts: _artifactStore,
            // v0.3.8.57 — the artifacts THIS TASK was given, when the runtime named any.
            // Empty falls back to the mission-wide block, which is what every task received before.
            declaredInputIds: task.InputArtifactIds,
            consumerTaskId: task.Id);
        // v0.3.8.93: the goal is what the verifier judges the answer AGAINST — an operator request,
        // not untrusted data. Task outputs stay untrusted: they are the model output under judgment.
        var prompt = $@"{AnthillRuntime.OperatorRequestBlock("mission goal", mission.Goal)}

Static system check:
{staticCheck}

{AnthillRuntime.UntrustedBlock("task outputs", context)}

Rules:
- Check whether the builder actually answered the specific question asked, not merely whether output exists.
- If the builder response contains only procedural ANTHILL commands like /apply, /patches, or /approval, mark Needs Improvement.
- If patch proposals exist, confirm they were only proposed.
- If /apply was not executed, do not claim files were modified.
- If write gates are disabled, confirm patch application cannot run.
";
        var verdict = _router.GenerateTyped("verifier", prompt, mission.Id, task.Id, Name,
            system: AnthillRuntime.RoleSystemPrompt("verifier", mission.Goal, VerifierRules));
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
