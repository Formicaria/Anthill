using System.Text.Json.Serialization;
using Anthill.SDK.Contracts;

namespace Anthill.Core.Contracts;

// v3.8.9 — what could NOT leave the core. TaskContract and ContractGate operate on Domain.Task and
// consult Agents.AntRegistry; ToolResult stays because Anthill.Core.Domain declares another type of
// the same name and every call site disambiguates against it by namespace.
//
// The shared vocabulary this file used to also contain — Capability, FailureClass, FailureClassify,
// ToolDescriptor, ToolCatalog — now lives in Anthill.SDK.Contracts.

/// <summary>The machine-readable task contract (NORTH_STAR Phase 2 schema).</summary>
public sealed class TaskContract
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("objective")] public string Objective { get; set; } = "";
    [JsonPropertyName("task_type")] public string TaskType { get; set; } = "diagnose"; // diagnose|change|verify|research|recover
    [JsonPropertyName("required_capabilities")] public List<string> RequiredCapabilities { get; set; } = new();
    [JsonPropertyName("side_effect_class")] public string SideEffectClass { get; set; } = "none";
    [JsonPropertyName("risk_class")] public string RiskClass { get; set; } = "low";
    [JsonPropertyName("idempotency_key")] public string IdempotencyKey { get; set; } = "";
    [JsonPropertyName("dependencies")] public List<string> Dependencies { get; set; } = new();
    [JsonPropertyName("timeout_seconds")] public int TimeoutSeconds { get; set; }
    [JsonPropertyName("success_criteria")] public List<string> SuccessCriteria { get; set; } = new();

    private static readonly string[] TaskTypes = { "diagnose", "change", "verify", "research", "recover" };
    private static readonly string[] SideEffects = { "none", "reversible", "destructive" };
    private static readonly string[] Risks = { "low", "medium", "high", "critical" };

    /// <summary>Project a planner task into its contract using the tool catalog's declarations.</summary>
    public static TaskContract FromTask(Domain.Task t)
    {
        var d = ToolCatalog.Describe(t.AssignedAnt);
        // A role the registry says is executable+enabled but the catalog doesn't know yet must not
        // be silently un-plannable — it gets a cautious fallback declaration (high risk, manual
        // compensation) instead. Ants unknown to BOTH stay capability-less and are rejected.
        if (d is null && Agents.AntRegistry.ExecutableRoleIds.Contains(t.AssignedAnt ?? ""))
            d = new ToolDescriptor
            {
                Name = t.AssignedAnt!, Description = "Executable role without an explicit catalog entry (fallback declaration).",
                RequiredCapabilities = new[] { Capability.ModelInvoke },
                SideEffectClass = "reversible", RiskClass = "high", Compensation = "manual",
            };
        return new TaskContract
        {
            Id = t.Id, Title = t.Title, Objective = t.Description,
            TaskType = t.TaskType switch
            {
                "verification" => "verify",
                "research" or "analysis" => "research",
                "patch_proposal" or "patch" or "code_change" or "build" => "change",
                _ => d?.SideEffectClass == "none" ? "diagnose" : "change",
            },
            RequiredCapabilities = d?.RequiredCapabilities.ToList() ?? new List<string>(),
            SideEffectClass = d?.SideEffectClass ?? "destructive", // unknown ant fails toward caution
            RiskClass = d?.RiskClass ?? "critical",
            Dependencies = t.DependsOn.ToList(),
            IdempotencyKey = t.Id, // task identity doubles as the replay key at this layer
        };
    }

    /// <summary>Schema validation. Empty list = admissible; anything else stays OUT of the queue.</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("id is required");
        if (string.IsNullOrWhiteSpace(Title)) errors.Add("title is required");
        if (string.IsNullOrWhiteSpace(Objective)) errors.Add("objective is required");
        if (!TaskTypes.Contains(TaskType)) errors.Add($"task_type '{TaskType}' is not in the schema");
        if (!SideEffects.Contains(SideEffectClass)) errors.Add($"side_effect_class '{SideEffectClass}' is not in the schema");
        if (!Risks.Contains(RiskClass)) errors.Add($"risk_class '{RiskClass}' is not in the schema");
        if (RequiredCapabilities.Count == 0) errors.Add("a task with no declared capabilities cannot be permission-checked");
        if (Dependencies.Contains(Id)) errors.Add("a task cannot depend on itself");
        return errors;
    }
}

/// <summary>Structured tool result — control flow never parses free text again.</summary>
public sealed class ToolResult
{
    [JsonPropertyName("status")] public string Status { get; set; } = "succeeded"; // succeeded|failed_retryable|failed_permanent|cancelled
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("failure_class")] public FailureClass Failure { get; set; } = FailureClass.None;
    [JsonPropertyName("error_message")] public string ErrorMessage { get; set; } = "";
    [JsonPropertyName("retry_after_seconds")] public int RetryAfterSeconds { get; set; }
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
    [JsonPropertyName("evidence")] public List<string> Evidence { get; set; } = new();

    public static ToolResult Succeeded(string summary) => new() { Status = "succeeded", Summary = summary };

    public static ToolResult Failed(FailureClass cls, string message, int retryAfterSeconds = 0) => new()
    {
        Status = FailureClassify.IsRetryable(cls) ? "failed_retryable" : "failed_permanent",
        Failure = cls, ErrorMessage = message, RetryAfterSeconds = retryAfterSeconds,
        Summary = $"{cls}: {message}",
    };
}

/// <summary>The admission gate: planner output passes through here on its way to the scheduler.</summary>
public static class ContractGate
{
    /// <summary>Returns only admissible tasks; each rejection is reported with its schema errors
    /// so the planner's failure is visible, never silent.</summary>
    public static List<Domain.Task> Admit(List<Domain.Task> tasks, Action<string>? onReject = null)
    {
        var admitted = new List<Domain.Task>(tasks.Count);
        foreach (var task in tasks)
        {
            var errors = TaskContract.FromTask(task).Validate();
            if (errors.Count == 0) { admitted.Add(task); continue; }
            onReject?.Invoke($"Contract gate rejected task '{task.Title}' ({task.AssignedAnt}): {string.Join("; ", errors)}");
        }
        return admitted;
    }
}

