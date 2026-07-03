using System.Text.Json.Nodes;
using Anthill.Core.Agents;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Models;

namespace Anthill.Core.Planning;

/// <summary>
/// Turns a mission goal into a task plan. Asks the routed planner model for a strict JSON
/// plan, validates and repairs it (drops invalid ants, guarantees a verifier, clamps count),
/// and falls back to a deterministic static plan whenever the model is unavailable or unusable.
/// Faithful to the Python <c>Planner</c>, prompt and all.
/// </summary>
public sealed class Planner
{
    private static readonly HashSet<string> AllowedAnts = new(AntRegistry.ExecutableRoleIds, StringComparer.OrdinalIgnoreCase);

    private readonly bool _useOllama;
    private readonly ModelRouter? _router;

    public Planner(bool useOllama, ModelRouter? router)
    {
        _useOllama = useOllama;
        _router = router;
    }

    /// <summary>
    /// True when the request is large enough that planning it as a single broad task would
    /// overflow context. Such missions are handled as specification ingestion: split, analyze
    /// per section, then synthesize. Measured on the raw goal length in characters.
    /// </summary>
    public static bool IsLongInput(string goal) =>
        AnthillRuntime.EnableSpecIngestion && (goal ?? "").Length > AnthillRuntime.LongInputThreshold;

    public List<Task> CreateTasks(string goal, string memoryContext = "", string toolContext = "", string pheromoneContext = "")
    {
        // v1.8.16: read explicit mission constraints up front. A verification-only / read-only /
        // "do not modify files" mission must never have coder patch-proposal tasks planned for it.
        var constraints = MissionConstraints.Parse(goal);

        // Long specification / architecture / framework documents are never sent into a single
        // "Analyze Mission Goal" task — they are chunked into bounded, parallel section reviews
        // followed by a synthesis pass. This runs regardless of model availability. (Spec-ingestion
        // plans are already research/synthesis/verify only — no coder tasks — so they honour the
        // no-patch constraint by construction.)
        if (IsLongInput(goal)) return AssignDefaultWorkers(CreateSpecIngestionTasks(goal), goal, constraints);

        if (!_useOllama || _router is null) return AssignDefaultWorkers(EnforceConstraints(FallbackTasks(goal), goal, constraints), goal, constraints);

        var constraintDirective = constraints.BlocksPatches
            ? "\nHARD CONSTRAINT (operator requested verification / read-only / no file changes):\n" +
              "- Do NOT include any coder task or any task_type \"patch_proposal\". Propose NO file changes.\n" +
              "- Use only researcher, web, file (read-only), builder, and verifier ants.\n" +
              "- The mission's job is to inspect, verify, and report — not to modify anything.\n"
            : "";

        var prompt = $@"{AnthillRuntime.PromptInjectionPrefix}
ANTHILL v{AnthillRuntime.Version} | role: planner | timestamp: {AnthillTime.NowUtc().ToIso()} | mission: {TextUtil.Truncate(goal, 180)}
You are concise. Do not explain your reasoning unless asked.

You are the Planner inside ANTHILL, a local swarm-intelligence AI harness.

Available ants:
- researcher: summarizes local memory, tool context, and mission-relevant internal context.
- web: performs read-only external research when the mission requires current/public information.
- file: inspects workspace files read-only. Use only for file/code/repo/folder missions.
- coder: proposes structured JSON patches to CREATE or MODIFY files (code, config, documentation, scripts). This is the ONLY ant that changes files — any goal that creates, adds, writes, edits, or patches a file needs a coder task.
- builder: creates the final response from prior ant outputs.
- verifier: verifies result quality and safety.

Available tools:
{toolContext}

Memory:
{memoryContext}

Pheromone trail summary. Prefer high-strength matching patterns, but do not force them if the mission does not fit:
{pheromoneContext}

Mission goal:
{goal}
{constraintDirective}
Rules:
- Return ONLY valid JSON.
Do not wrap JSON in markdown code fences.
- Create between {AnthillRuntime.MinDynamicTasks} and {AnthillRuntime.MaxDynamicTasks} tasks.
- assigned_ant must be one of: researcher, web, file, coder, builder, verifier.
- assigned_worker is optional but, when present, must be a registered worker under assigned_ant.
  Prefer these worker IDs: researcher.repo_researcher, researcher.mission_researcher,
  web.source_finder, web.source_verifier, file.file_scout, file.file_reader,
  coder.backend_coder, coder.ui_coder, coder.docs_coder,
  builder.response_builder, builder.result_compiler,
  verifier.result_verifier, verifier.safety_verifier.
- Keep each task description under 100 words.
- Skip the file ant unless file/code/repo/folder/path keywords appear in the goal.
- Use web only when the mission needs current, public, external, version, price, news, or online information from the internet. Do NOT use web merely because the goal mentions a documentation file or a path.
- If the goal creates, adds, writes, edits, modifies, or patches ANY file — including documentation (.md), config, or a new source file — you MUST include a coder task with task_type ""patch_proposal"" that proposes the change as a structured JSON patch. This is the only way ANTHILL produces file changes; a research/build answer that merely describes the change is NOT sufficient.
- Ants never write to disk directly — the coder only PROPOSES a patch, which a human (or gated auto-apply) applies later through /apply after approval. So proposing a patch via the coder is correct and expected, not a violation.
- Use file/coder for code, scripts, patches, folders, repos, bugs, refactors, and creating or editing any file.
- Final task should usually be verifier.
- depends_on should usually be [] because ANTHILL auto-wires safe dependencies.

Required JSON:
{{
  ""tasks"": [
    {{
      ""title"": ""Short title"",
      ""description"": ""Clear task description under 100 words"",
      ""assigned_ant"": ""researcher"",
      ""assigned_worker"": ""researcher.repo_researcher"",
      ""task_type"": ""research"",
      ""depends_on"": []
    }}
  ]
}}
";
        var response = _router.Generate("planner", prompt, antName: "planner");
        if (response.StartsWith("ERROR:"))
        {
            Console.Error.WriteLine($"Planner failed to use Ollama: {response}");
            Console.Error.WriteLine("Using fallback static task plan.");
            return EnforceConstraints(FallbackTasks(goal), goal, constraints);
        }
        try
        {
            var parsed = Json.ExtractJsonObject(response);
            var tasks = TasksFromJson(parsed, goal);
            if (tasks.Count == 0)
            {
                Console.Error.WriteLine("Dynamic planner returned no valid task plan. Using fallback plan.");
                return EnforceConstraints(FallbackTasks(goal), goal, constraints);
            }
            // Belt-and-suspenders: even with the prompt directive, a small model may still emit a
            // coder patch task on a verification-only mission. Strip them deterministically.
            return AssignDefaultWorkers(EnforceConstraints(tasks, goal, constraints), goal, constraints);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Dynamic planner parse failed: {error.Message}");
            return AssignDefaultWorkers(EnforceConstraints(FallbackTasks(goal), goal, constraints), goal, constraints);
        }
    }

    /// <summary>
    /// v1.8.16 planner constraint enforcement. When the mission is verification-only / read-only /
    /// no-patch, deterministically removes every patch-producing task (coder ant or
    /// <c>patch_proposal</c> task type) and drops now-orphaned dependencies on them. If removing the
    /// coder task would leave nothing to inspect the workspace, a read-only file-inspection task is
    /// substituted so verification missions still actually look at the files. A verifier is always
    /// guaranteed. Missions without a no-patch constraint pass through unchanged.
    /// </summary>
    internal static List<Task> EnforceConstraints(List<Task> tasks, string goal, MissionConstraints constraints)
    {
        if (!constraints.BlocksPatches || tasks.Count == 0) return tasks;

        bool IsPatchTask(Task t) =>
            t.AssignedAnt == "coder" || t.TaskType is "patch_proposal" or "patch" or "code_change";
        var removedIds = tasks.Where(IsPatchTask).Select(t => t.Id).ToHashSet();
        var kept = tasks.Where(t => !IsPatchTask(t)).ToList();

        // Drop dependencies that pointed at removed tasks so the scheduler can't deadlock.
        foreach (var t in kept)
            t.DependsOn = t.DependsOn.Where(d => !removedIds.Contains(d)).ToList();

        // Guarantee the mission still inspects the workspace if it names files/code/paths.
        var mentionsFiles = new[] { "file", "code", "repo", "path", "folder", "directory", ".cs", ".md", ".json", "config" }
            .Any(k => goal.ToLowerInvariant().Contains(k));
        if (mentionsFiles && !kept.Any(t => t.AssignedAnt == "file"))
            kept.Insert(0, new Task
            {
                Title = "Inspect workspace files (read-only)",
                Description = $"List relevant workspace files and read safe text files to verify — do NOT modify anything: {goal}",
                AssignedAnt = "file", AssignedWorker = "file.file_reader", TaskType = "file_inspection",
            });

        if (kept.Count == 0)
            kept.Add(new Task
            {
                Title = "Research and report",
                Description = $"Investigate and report on the mission without changing any files: {goal}",
                AssignedAnt = "researcher", AssignedWorker = "researcher.mission_researcher", TaskType = "research",
            });

        if (!kept.Any(t => t.AssignedAnt == "verifier"))
            kept.Add(new Task
            {
                Title = "Verify findings",
                Description = $"Check the inspection/verification result for accuracy and completeness: {goal}",
                AssignedAnt = "verifier", AssignedWorker = "verifier.result_verifier", TaskType = "verification",
            });
        return kept;
    }

    private static List<Task> AssignDefaultWorkers(List<Task> tasks, string goal, MissionConstraints constraints)
    {
        var valid = new List<Task>();
        foreach (var task in tasks)
        {
            task.AssignedAnt = (task.AssignedAnt ?? "").Trim().ToLowerInvariant();
            task.TaskType = string.IsNullOrWhiteSpace(task.TaskType)
                ? TextUtil.InferTaskType(task.AssignedAnt, task.Title, task.Description)
                : task.TaskType.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(task.AssignedWorker))
                task.AssignedWorker = AntRegistry.DefaultWorkerFor(task.AssignedAnt, task.TaskType, $"{goal} {task.Title} {task.Description}")?.WorkerId;
            var result = AntRegistry.ValidateTask(task, constraints);
            if (!result.Allowed)
            {
                Console.Error.WriteLine($"Planner rejected task '{task.Title}': {result.Reason}");
                continue;
            }
            valid.Add(task);
        }
        return valid;
    }

    private List<Task> TasksFromJson(JsonObject parsed, string goal)
    {
        if (parsed["tasks"] is not JsonArray rawTasks) return new();
        var tasks = new List<Task>();
        var dropped = 0;
        foreach (var item in rawTasks.Take(AnthillRuntime.MaxDynamicTasks))
        {
            if (item is not JsonObject obj) { dropped++; continue; }
            var title = (obj["title"]?.GetValue<string>() ?? "").Trim();
            if (title.Length == 0) title = "Task";
            var description = (obj["description"]?.GetValue<string>() ?? "").Trim();
            if (description.Length == 0) description = $"Handle part of the mission: {goal}";
            var assignedAnt = (obj["assigned_ant"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
            if (!AllowedAnts.Contains(assignedAnt)) { dropped++; continue; }
            var assignedWorker = (obj["assigned_worker"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
            var taskType = (obj["task_type"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
            if (taskType.Length == 0) taskType = TextUtil.InferTaskType(assignedAnt, title, description);
            var dependsOn = (obj["depends_on"] as JsonArray)?.Select(n => n?.ToString() ?? "").Where(s => s.Length > 0).ToList() ?? new();
            tasks.Add(new Task { Title = title, Description = description, AssignedAnt = assignedAnt, AssignedWorker = assignedWorker.Length == 0 ? null : assignedWorker, TaskType = taskType, DependsOn = dependsOn });
        }

        // LLMs often emit non-ID dependency references: integer indices ([0],[1]) or task titles.
        // Build lookup maps and resolve everything to real task IDs.
        var idByIndex = tasks.Select((t, i) => (t, i)).ToDictionary(x => x.i, x => x.t.Id);
        var idByTitle = tasks.ToDictionary(t => t.Title.Trim(), t => t.Id, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < tasks.Count; i++)
        {
            tasks[i].DependsOn = tasks[i].DependsOn
                .Select(dep =>
                {
                    // Integer index
                    if (int.TryParse(dep, out var idx) && idx >= 0 && idx < tasks.Count && idx != i)
                        return idByIndex[idx];
                    // Exact task title
                    if (idByTitle.TryGetValue(dep.Trim(), out var titleId) && titleId != tasks[i].Id)
                        return titleId;
                    // Already a valid task ID
                    if (tasks.Any(t => t.Id == dep))
                        return dep;
                    // Unknown reference — drop it so the scheduler doesn't deadlock
                    return "";
                })
                .Where(dep => dep.Length > 0)
                .Distinct()
                .ToList();
        }

        if (dropped > 0) Console.Error.WriteLine($"Planner dropped {dropped} invalid task(s).");
        if (tasks.Count < AnthillRuntime.MinDynamicTasks) return new();
        if (!tasks.Any(t => t.AssignedAnt == "verifier"))
            tasks.Add(new Task
            {
                Title = "Verify mission output",
                Description = $"Check the final result for accuracy, completeness, and usefulness: {goal}",
                AssignedAnt = "verifier", AssignedWorker = "verifier.result_verifier", TaskType = "verification",
            });
        return tasks.Take(AnthillRuntime.MaxDynamicTasks).ToList();
    }

    /// <summary>
    /// Specification-ingestion plan: one bounded analysis task per document section (non-critical,
    /// runnable in parallel), then a synthesis task that depends on all of them, then verification.
    /// Section tasks are non-critical so a single failed/timed-out section never skips synthesis —
    /// the synthesis still runs against whatever sections completed. Faithful to the long-input rule.
    /// </summary>
    public static List<Task> CreateSpecIngestionTasks(string goal)
    {
        var sections = SplitIntoSections(goal, AnthillRuntime.MaxSectionChars, AnthillRuntime.MaxSectionTasks);
        if (sections.Count == 0) sections.Add(TextUtil.Truncate(goal, AnthillRuntime.MaxSectionChars, "...[section truncated]"));

        var tasks = new List<Task>();
        var sectionIds = new List<string>();
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var task = new Task
            {
                Title = $"Analyze section {i + 1} of {sections.Count}",
                Description =
                    $"You are reviewing ONE section of a larger specification/architecture document. " +
                    $"Analyze ONLY this section. Extract: (1) concrete requirements and rules, (2) any " +
                    $"named components, tasks, or roles, (3) constraints, limits, and edge cases, " +
                    $"(4) open questions. Be concise and structured. Do not attempt to cover the whole document.\n\n" +
                    $"--- SECTION {i + 1}/{sections.Count} START ---\n{section}\n--- SECTION {i + 1}/{sections.Count} END ---",
                AssignedAnt = "researcher",
                AssignedWorker = "researcher.mission_researcher",
                TaskType = "section_analysis",
                Critical = false, // a failed section must not abort the mission
                MaxAttempts = 2,  // route timeouts back for one bounded retry with the same (already small) scope
            };
            tasks.Add(task);
            sectionIds.Add(task.Id);
        }

        var synthesis = new Task
        {
            Title = "Synthesize condensed implementation plan",
            Description =
                "Combine the per-section analyses above into ONE condensed implementation plan. " +
                "Produce: (1) a short overview of what the document asks for, (2) a deduplicated, ordered " +
                "list of concrete requirements/rules, (3) a proposed task breakdown (which work items, in what " +
                "order, with dependencies), and (4) risks and open questions. If some sections are missing " +
                "because their analysis failed, proceed with the sections that succeeded and note the gap.",
            AssignedAnt = "builder",
            AssignedWorker = "builder.result_compiler",
            TaskType = "synthesis",
            DependsOn = new List<string>(sectionIds),
            Critical = true,
            MaxAttempts = 1,
        };
        tasks.Add(synthesis);

        tasks.Add(new Task
        {
            Title = "Verify synthesized plan",
            Description = "Check the synthesized implementation plan for accuracy, completeness against the " +
                          "section analyses, internal consistency, and missing steps. Note any section gaps.",
            AssignedAnt = "verifier",
            AssignedWorker = "verifier.result_verifier",
            TaskType = "verification",
            DependsOn = new List<string> { synthesis.Id },
            Critical = true,
        });

        return tasks;
    }

    /// <summary>
    /// Splits a document into ordered chunks, each at most <paramref name="maxSectionChars"/> characters,
    /// preferring natural boundaries (markdown headings, ALL-CAPS label lines, then blank-line paragraphs).
    /// The number of chunks is capped at <paramref name="maxSections"/>; overflow is merged into the last chunk.
    /// </summary>
    public static List<string> SplitIntoSections(string text, int maxSectionChars, int maxSections)
    {
        var normalized = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
        if (normalized.Trim().Length == 0) return new List<string>();

        // 1) Prefer structural blocks: a heading/label line starts a new block.
        var lines = normalized.Split('\n');
        var blocks = new List<string>();
        var current = new System.Text.StringBuilder();
        bool IsBoundary(string line)
        {
            var t = line.Trim();
            if (t.Length == 0) return false;
            if (t.StartsWith("#")) return true;                                    // markdown heading
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Z][A-Z0-9 _\-]{3,}:?$")) return true; // ALL_CAPS label
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d+[\.\)]\s+\S")) return true;           // numbered heading
            return false;
        }
        foreach (var line in lines)
        {
            if (IsBoundary(line) && current.Length > 0)
            {
                blocks.Add(current.ToString());
                current.Clear();
            }
            current.Append(line).Append('\n');
        }
        if (current.Length > 0) blocks.Add(current.ToString());

        // 2) If structure was too coarse, fall back to blank-line paragraph blocks.
        if (blocks.Count < 2)
            blocks = System.Text.RegularExpressions.Regex.Split(normalized, @"\n\s*\n")
                .Where(b => b.Trim().Length > 0).ToList();
        if (blocks.Count == 0) blocks.Add(normalized);

        // 3) Hard-split any single block that on its own exceeds the cap.
        var sized = new List<string>();
        foreach (var block in blocks)
        {
            if (block.Length <= maxSectionChars) { sized.Add(block); continue; }
            for (var i = 0; i < block.Length; i += maxSectionChars)
                sized.Add(block.Substring(i, Math.Min(maxSectionChars, block.Length - i)));
        }

        // 4) Greedily pack consecutive blocks up to the cap.
        var chunks = new List<string>();
        var buffer = new System.Text.StringBuilder();
        foreach (var block in sized)
        {
            if (buffer.Length > 0 && buffer.Length + block.Length > maxSectionChars)
            {
                chunks.Add(buffer.ToString().Trim());
                buffer.Clear();
            }
            buffer.Append(block);
        }
        if (buffer.Length > 0) chunks.Add(buffer.ToString().Trim());

        // 5) Enforce the section-count cap by merging the overflow into the final chunk.
        if (chunks.Count > maxSections)
        {
            var head = chunks.Take(maxSections - 1).ToList();
            var tail = string.Join("\n\n", chunks.Skip(maxSections - 1));
            if (tail.Length > maxSectionChars) tail = tail.Substring(0, maxSectionChars);
            head.Add(tail);
            chunks = head;
        }

        return chunks.Where(c => c.Trim().Length > 0).ToList();
    }

    private static List<Task> FallbackTasks(string goal)
    {
        var lowered = goal.ToLowerInvariant();
        var codeKeywords = new[] { "code", "script", "python", "bug", "debug", "review", "refactor", "function", "class", "repo", "repository", "file", "folder", "directory", "patch", "modify", "change", "create", "add", "write", "edit", "document", "docs/", ".md", ".cs", ".json", "ui", "frontend", "canvas", "css", "html", "javascript", "visualization", "dashboard" };
        var isCodeGoal = codeKeywords.Any(lowered.Contains);

        // A goal that creates/edits a file must reach the coder — check it BEFORE the web branch,
        // so "create a docs file" produces a patch rather than a research answer that never lands.
        if (!isCodeGoal && AnthillRuntime.EnableWebSearch && TextUtil.ShouldUseWebSearch(goal))
            return new()
            {
                new() { Title = "Frame research need", Description = $"Identify what current/public information is needed for: {goal}", AssignedAnt = "researcher", AssignedWorker = "researcher.mission_researcher", TaskType = "research" },
                new() { Title = "External web research", Description = $"Run read-only web research and save source records for: {goal}", AssignedAnt = "web", AssignedWorker = "web.source_finder", TaskType = "external_research" },
                new() { Title = "Build sourced response", Description = $"Create a concise answer using internal context and saved source summaries: {goal}", AssignedAnt = "builder", AssignedWorker = "builder.response_builder", TaskType = "build_answer" },
                new() { Title = "Verify sourced result", Description = $"Check that the answer addresses the question and notes source limitations: {goal}", AssignedAnt = "verifier", AssignedWorker = "verifier.result_verifier", TaskType = "verification" },
            };

        if (isCodeGoal)
            return new()
            {
                new() { Title = "Research mission", Description = $"Understand the goal and frame the code/project inspection need: {goal}", AssignedAnt = "researcher", AssignedWorker = "researcher.repo_researcher", TaskType = "research" },
                new() { Title = "Inspect workspace files", Description = $"List relevant workspace files and read safe text files if useful: {goal}", AssignedAnt = "file", AssignedWorker = "file.file_scout", TaskType = "file_inspection" },
                new() { Title = "Create structured patch proposal", Description = $"Analyze available code/file context and propose structured patches as JSON only: {goal}", AssignedAnt = "coder", AssignedWorker = AntRegistry.DefaultWorkerFor("coder", "patch_proposal", goal)?.WorkerId, TaskType = "patch_proposal" },
                new() { Title = "Build final response", Description = $"Create a practical answer or implementation plan from the prior findings: {goal}", AssignedAnt = "builder", AssignedWorker = "builder.response_builder", TaskType = "build_answer" },
                new() { Title = "Verify result", Description = $"Check the result for accuracy, usefulness, missing steps, and risk: {goal}", AssignedAnt = "verifier", AssignedWorker = "verifier.result_verifier", TaskType = "verification" },
            };

        return new()
        {
            new() { Title = "Research mission", Description = $"Understand the goal and gather useful context: {goal}", AssignedAnt = "researcher", AssignedWorker = "researcher.mission_researcher", TaskType = "research" },
            new() { Title = "Build response", Description = $"Create a practical answer or action plan for: {goal}", AssignedAnt = "builder", AssignedWorker = "builder.response_builder", TaskType = "build_answer" },
            new() { Title = "Verify result", Description = $"Check the result for accuracy, usefulness, and missing steps: {goal}", AssignedAnt = "verifier", AssignedWorker = "verifier.result_verifier", TaskType = "verification" },
        };
    }
}
