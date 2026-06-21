using System.Text.Json.Nodes;
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
    private static readonly HashSet<string> AllowedAnts = new() { "researcher", "web", "file", "coder", "builder", "verifier" };

    private readonly bool _useOllama;
    private readonly ModelRouter? _router;

    public Planner(bool useOllama, ModelRouter? router)
    {
        _useOllama = useOllama;
        _router = router;
    }

    public List<Task> CreateTasks(string goal, string memoryContext = "", string toolContext = "", string pheromoneContext = "")
    {
        if (!_useOllama || _router is null) return FallbackTasks(goal);

        var prompt = $@"{AnthillRuntime.PromptInjectionPrefix}
ANTHILL v{AnthillRuntime.Version} | role: planner | timestamp: {AnthillTime.NowUtc().ToIso()} | mission: {TextUtil.Truncate(goal, 180)}
You are concise. Do not explain your reasoning unless asked.

You are the Planner inside ANTHILL, a local swarm-intelligence AI harness.

Available ants:
- researcher: summarizes local memory, tool context, and mission-relevant internal context.
- web: performs read-only external research when the mission requires current/public information.
- file: inspects workspace files read-only. Use only for file/code/repo/folder missions.
- coder: proposes structured JSON patches only.
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

Rules:
- Return ONLY valid JSON.
Do not wrap JSON in markdown code fences.
- Create between {AnthillRuntime.MinDynamicTasks} and {AnthillRuntime.MaxDynamicTasks} tasks.
- assigned_ant must be one of: researcher, web, file, coder, builder, verifier.
- Keep each task description under 100 words.
- Skip the file ant unless file/code/repo/folder/path keywords appear in the goal.
- Use web only when the mission needs current, public, external, version, docs, price, news, or online information.
- Use file/coder for code, scripts, patches, folders, repos, bugs, or refactors.
- Do not ask ants to write files.
- Patch application is user-triggered later through /apply after approval.
- Final task should usually be verifier.
- depends_on should usually be [] because ANTHILL auto-wires safe dependencies.

Required JSON:
{{
  ""tasks"": [
    {{
      ""title"": ""Short title"",
      ""description"": ""Clear task description under 100 words"",
      ""assigned_ant"": ""researcher"",
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
            return FallbackTasks(goal);
        }
        try
        {
            var parsed = Json.ExtractJsonObject(response);
            var tasks = TasksFromJson(parsed, goal);
            if (tasks.Count == 0)
            {
                Console.Error.WriteLine("Dynamic planner returned no valid task plan. Using fallback plan.");
                return FallbackTasks(goal);
            }
            return tasks;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Dynamic planner parse failed: {error.Message}");
            return FallbackTasks(goal);
        }
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
            var taskType = (obj["task_type"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
            if (taskType.Length == 0) taskType = TextUtil.InferTaskType(assignedAnt, title, description);
            var dependsOn = (obj["depends_on"] as JsonArray)?.Select(n => n?.ToString() ?? "").Where(s => s.Length > 0).ToList() ?? new();
            tasks.Add(new Task { Title = title, Description = description, AssignedAnt = assignedAnt, TaskType = taskType, DependsOn = dependsOn });
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
                AssignedAnt = "verifier", TaskType = "verification",
            });
        return tasks.Take(AnthillRuntime.MaxDynamicTasks).ToList();
    }

    private static List<Task> FallbackTasks(string goal)
    {
        var lowered = goal.ToLowerInvariant();
        var codeKeywords = new[] { "code", "script", "python", "bug", "debug", "review", "refactor", "function", "class", "repo", "repository", "file", "folder", "directory", "patch", "modify", "change" };

        if (AnthillRuntime.EnableWebSearch && TextUtil.ShouldUseWebSearch(goal))
            return new()
            {
                new() { Title = "Frame research need", Description = $"Identify what current/public information is needed for: {goal}", AssignedAnt = "researcher", TaskType = "research" },
                new() { Title = "External web research", Description = $"Run read-only web research and save source records for: {goal}", AssignedAnt = "web", TaskType = "external_research" },
                new() { Title = "Build sourced response", Description = $"Create a concise answer using internal context and saved source summaries: {goal}", AssignedAnt = "builder", TaskType = "build_answer" },
                new() { Title = "Verify sourced result", Description = $"Check that the answer addresses the question and notes source limitations: {goal}", AssignedAnt = "verifier", TaskType = "verification" },
            };

        if (codeKeywords.Any(lowered.Contains))
            return new()
            {
                new() { Title = "Research mission", Description = $"Understand the goal and frame the code/project inspection need: {goal}", AssignedAnt = "researcher", TaskType = "research" },
                new() { Title = "Inspect workspace files", Description = $"List relevant workspace files and read safe text files if useful: {goal}", AssignedAnt = "file", TaskType = "file_inspection" },
                new() { Title = "Create structured patch proposal", Description = $"Analyze available code/file context and propose structured patches as JSON only: {goal}", AssignedAnt = "coder", TaskType = "patch_proposal" },
                new() { Title = "Build final response", Description = $"Create a practical answer or implementation plan from the prior findings: {goal}", AssignedAnt = "builder", TaskType = "build_answer" },
                new() { Title = "Verify result", Description = $"Check the result for accuracy, usefulness, missing steps, and risk: {goal}", AssignedAnt = "verifier", TaskType = "verification" },
            };

        return new()
        {
            new() { Title = "Research mission", Description = $"Understand the goal and gather useful context: {goal}", AssignedAnt = "researcher", TaskType = "research" },
            new() { Title = "Build response", Description = $"Create a practical answer or action plan for: {goal}", AssignedAnt = "builder", TaskType = "build_answer" },
            new() { Title = "Verify result", Description = $"Check the result for accuracy, usefulness, and missing steps: {goal}", AssignedAnt = "verifier", TaskType = "verification" },
        };
    }
}
